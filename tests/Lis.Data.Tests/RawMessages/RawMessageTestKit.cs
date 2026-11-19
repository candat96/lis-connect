using System.Text;
using Dapper;
using Lis.Data.Ingest;
using Lis.Data.Instruments;
using Lis.Data.RawMessages;
using Lis.Data.Tests.Results;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using static Lis.Data.Tests.Results.ResultTestData;

namespace Lis.Data.Tests.RawMessages;

/// <summary>Dựng môi trường và bản tin HL7 giả cho kiểm thử bản tin thô. Mọi dữ liệu là dữ liệu giả.</summary>
internal sealed record RawEnv(
    ResultTestEnv Base, Guid InstrumentId, Guid WbcId, Guid HgbId, RawMessageStore Store, RawMessageProcessor Processor,
    IngestService Ingest, FakeTimeProvider Clock)
{
    public static readonly DateTimeOffset CompletedAt = new(2026, 11, 19, 8, 30, 0, TimeSpan.FromHours(7));

    public Db Db => Base.Db;

    public static async Task<RawEnv> CreateAsync(PostgresFixture fixture, string? family = "hl7-mllp", IChannelMapLearning? learning = null)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 19, 2, 0, 0, TimeSpan.Zero));
        var baseEnv = await ResultTestEnv.CreateAsync(fixture, clock);
        var instruments = new InstrumentStore(baseEnv.Db);
        var instrumentId = await instruments.SaveAsync(new InstrumentInfo(
            Guid.Empty, "Máy thử BC-5380", "BC-5380", family, null, null, "BC-01", true));
        var wbcId = await SaveServiceAsync(baseEnv.Catalog, "WBC", "Bạch cầu", baseEnv.EdtaId, decimals: 2, unit: "G/L");
        var hgbId = await SaveServiceAsync(baseEnv.Catalog, "HGB", "Huyết sắc tố", baseEnv.EdtaId, decimals: 0, unit: "g/L");
        await instruments.SaveChannelAsync(new ChannelMap(Guid.Empty, instrumentId, "6690-2", wbcId, null, null, false, false));
        await instruments.SaveChannelAsync(new ChannelMap(Guid.Empty, instrumentId, "718-7", hgbId, null, null, false, false));

        var ingest = new IngestService(baseEnv.Db, clock);
        return new RawEnv(baseEnv, instrumentId, wbcId, hgbId, new RawMessageStore(baseEnv.Db, clock),
            new RawMessageProcessor(baseEnv.Db, ingest, learning, clock), ingest, clock);
    }

    /// <summary>Tạo một mẫu có chỉ định WBC + HGB; trả về id mẫu, barcode và order_item theo dịch vụ.</summary>
    public async Task<(Guid SampleId, string Barcode, IReadOnlyDictionary<Guid, Guid> Items)> NewSampleAsync(string name = "Trần Thị Mẫu")
    {
        var (sampleId, items) = await CreateSampleAsync(Base, FemaleAdult(name), WbcId, HgbId);
        return (sampleId, await BarcodeAsync(Db, sampleId), items);
    }

    public RawMessageInsert Insert(byte[] bytes, Guid? id = null, bool partial = false) =>
        new(id ?? Guid.NewGuid(), InstrumentId, null, 1, "phiên-thử", CompletedAt.AddSeconds(-5), CompletedAt, bytes, partial);

    /// <summary>Lưu một bản tin thô vào kho rồi trả về id.</summary>
    public async Task<Guid> StoreAsync(byte[] bytes, bool partial = false)
    {
        var insert = Insert(bytes, partial: partial);
        (await Store.InsertIfAbsentAsync(insert, CancellationToken.None)).ShouldNotBeNull();
        return insert.Id;
    }

    public async Task<int> CountAsync(string sql, object? args = null)
    {
        await using var connection = await Db.OpenAsync();
        return await connection.ExecuteScalarAsync<int>(sql, args);
    }

    /// <summary>Bản tin HL7-MLLP: mỗi OBR là một mẫu (barcode ở OBR-3); <paramref name="qc"/> = MSH-11 là Q.</summary>
    public static byte[] Hl7(bool qc, params (string Barcode, string Wbc, string Hgb)[] samples) => BuildHl7(qc, "N", samples);

    /// <summary>Như <see cref="Hl7"/> nhưng OBX-8 (cờ bất thường) để trống: kết quả không mang cờ máy nên mới tự duyệt được.</summary>
    public static byte[] Hl7Unflagged(params (string Barcode, string Wbc, string Hgb)[] samples) => BuildHl7(false, "", samples);

    private static byte[] BuildHl7(bool qc, string flag, (string Barcode, string Wbc, string Hgb)[] samples)
    {
        var text = new StringBuilder();
        text.Append($"MSH|^~\\&|Analyzer|Mindray|||20261119083000||ORU^R01|1|{(qc ? "Q" : "P")}|2.3.1||||||UNICODE\r");
        text.Append("PID|1||BN0001^^^^MR||Mẫu^Thử||19900804000000|Female\r");
        var i = 1;
        foreach (var (barcode, wbc, hgb) in samples)
        {
            text.Append($"OBR|{i++}||{barcode}|00001^Automated Count^99MRC||20261119082000|20261119083000\r");
            text.Append($"OBX|1|NM|6690-2^WBC^LN||{wbc}|10*9/L|4.00-10.00|{flag}|||F\r");
            text.Append($"OBX|2|NM|718-7^HGB^LN||{hgb}|g/L|110-150|{flag}|||F\r");
        }

        return [0x0B, .. Encoding.UTF8.GetBytes(text.ToString()), 0x1C, 0x0D];
    }
}
