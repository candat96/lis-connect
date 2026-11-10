using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lis.Core.Ingest;
using Lis.Data.Instruments;
using Lis.Server.Tenancy;

namespace Lis.Server.AgentApi;

/// <summary>Thông tin agent nhận về khi khởi động.</summary>
public sealed record AgentMe(string ClinicCode, string ClinicName, string AgentName, string WorkstationCode);

/// <summary>Kết quả server trả cho một bản tin máy gửi. <see cref="Notes"/>: các dòng ghi log (vd tự map kênh). <see cref="Duplicate"/>: bản tin đã nhận trước đó.</summary>
public sealed record ReadingResponse(int Stored, int PendingReplacement, int Unmatched, int Qc, IReadOnlyList<string> Notes, bool Duplicate = false);

public sealed record OrderQueryRequest(Guid InstrumentId, string Barcode);

public sealed record OrderQueryResponse(IReadOnlyList<string> ChannelCodes);

/// <summary>Trạng thái kết nối một máy do agent báo về (hiện trên web).</summary>
public sealed record InstrumentStatusReport(
    Guid InstrumentId, string State, string Text, DateTimeOffset? LastResultAt, int SamplesReceived, int PendingToRetry);

public sealed record StatusRequest(IReadOnlyList<InstrumentStatusReport> Instruments);

/// <summary>
/// API cho Lis.Agent (<c>/agent/v1</c>). Xác thực bằng header <c>X-Agent-Key</c>; khoá xác định
/// luôn phòng khám, nên agent chỉ chạm được CSDL của phòng khám mình và chỉ các máy gán cho
/// máy trạm của nó.
/// </summary>
public static class AgentEndpoints
{
    public const string KeyHeader = "X-Agent-Key";
    private const string IdentityItem = "agent-identity";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapAgentApi(this WebApplication app)
    {
        var group = app.MapGroup("/agent/v1");

        group.AddEndpointFilter(async (context, next) =>
        {
            var master = context.HttpContext.RequestServices.GetRequiredService<MasterStore>();
            var identity = await master.AuthenticateAsync(context.HttpContext.Request.Headers[KeyHeader].FirstOrDefault());
            if (identity is null)
            {
                return Results.Problem("Khoá agent không hợp lệ", statusCode: StatusCodes.Status401Unauthorized);
            }

            context.HttpContext.Items[IdentityItem] = identity;
            return await next(context);
        });

        group.MapGet("/me", (HttpContext http) =>
        {
            var id = Identity(http);
            return new AgentMe(id.Clinic.Code, id.Clinic.Name, id.Agent.Name, id.Agent.WorkstationCode);
        });

        // Các máy gán cho máy trạm của agent này (kèm cấu hình kết nối, driver).
        group.MapGet("/instruments", async (HttpContext http, TenantRegistry tenants) =>
        {
            var id = Identity(http);
            var all = await tenants.For(id.Clinic).Instruments.ListAsync();
            return all.Where(i => IsMine(i, id)).ToList();
        });

        group.MapPost("/readings", async (InstrumentReading reading, HttpContext http, TenantRegistry tenants, MasterStore master) =>
        {
            var id = Identity(http);
            var tenant = tenants.For(id.Clinic);
            if (!await OwnsInstrumentAsync(tenant, id, reading.InstrumentId))
            {
                return Results.Problem("Máy này không gán cho agent", statusCode: StatusCodes.Status403Forbidden);
            }

            var key = ReadingKey(reading);
            var (isNew, previous) = await master.ReserveReadingAsync(id.Clinic.Id, key);
            if (!isNew)
            {
                if (previous is null)
                {
                    // Request khác đang ghi dở đúng bản tin này — agent giữ lại và gửi lại sau.
                    return Results.Problem("Bản tin đang được ghi — gửi lại sau", statusCode: StatusCodes.Status409Conflict);
                }

                // Đã ghi xong từ lần gửi trước — không ghi lần hai.
                return Results.Ok(JsonSerializer.Deserialize<ReadingResponse>(previous, Json)! with { Duplicate = true });
            }

            try
            {
                var outcome = await tenant.Ingest.IngestAsync(reading);
                IReadOnlyList<string> notes = [];
                if (outcome.Unmatched > 0)
                {
                    try
                    {
                        notes = await tenant.Learning.AutoLearnAsync(reading.InstrumentId);
                    }
                    catch (Exception ex)
                    {
                        notes = [$"Không tự dò được map kênh: {ex.Message}"];
                    }
                }

                var response = new ReadingResponse(outcome.Stored, outcome.PendingReplacement, outcome.Unmatched, outcome.Qc, notes);
                await master.CompleteReadingAsync(id.Clinic.Id, key, JsonSerializer.Serialize(response, Json));
                return Results.Ok(response);
            }
            catch
            {
                await master.ReleaseReadingAsync(id.Clinic.Id, key);
                throw;
            }
        });

        group.MapPost("/order-queries", async (OrderQueryRequest request, HttpContext http, TenantRegistry tenants, CancellationToken ct) =>
        {
            var id = Identity(http);
            var tenant = tenants.For(id.Clinic);
            if (!await OwnsInstrumentAsync(tenant, id, request.InstrumentId))
            {
                return Results.Problem("Máy này không gán cho agent", statusCode: StatusCodes.Status403Forbidden);
            }

            return Results.Ok(new OrderQueryResponse(await tenant.OrderQueries.ChannelsToRunAsync(request.InstrumentId, request.Barcode, ct)));
        });

        group.MapPost("/status", async (StatusRequest request, HttpContext http, MasterStore master) =>
        {
            await master.TouchAgentAsync(Identity(http).Agent.Id, JsonSerializer.Serialize(request.Instruments, Json));
            return Results.NoContent();
        });
    }

    private static AgentIdentity Identity(HttpContext http) => (AgentIdentity)http.Items[IdentityItem]!;

    private static bool IsMine(InstrumentInfo instrument, AgentIdentity id) =>
        string.Equals(instrument.WorkstationCode, id.Agent.WorkstationCode, StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> OwnsInstrumentAsync(TenantServices tenant, AgentIdentity id, Guid instrumentId) =>
        (await tenant.Instruments.ListAsync()).Any(i => i.Id == instrumentId && IsMine(i, id));

    /// <summary>Khoá nhận dạng một bản tin: cùng máy, cùng barcode, cùng thời điểm nhận, cùng nội dung → cùng khoá.</summary>
    internal static string ReadingKey(InstrumentReading reading)
    {
        var text = new StringBuilder()
            .Append(reading.InstrumentId.ToString("N")).Append('|')
            .Append(reading.Barcode).Append('|')
            .Append(reading.ReceivedAt.UtcTicks).Append('|')
            .Append(reading.RawRef).Append('|');
        foreach (var item in reading.Items)
        {
            text.Append(item.ChannelCode).Append('=').Append(item.RawValue).Append(';');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
