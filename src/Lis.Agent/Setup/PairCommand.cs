using Lis.Core.Agents;

namespace Lis.Agent;

/// <summary>
/// <c>LisAgent pair &lt;mã&gt;</c>: đổi mã lấy khoá rồi lưu. Mã chỉ dùng được một lần và đổi xong là khoá cũ bị thu hồi,
/// nên mọi điều kiện chặn việc lưu (đã có dịch vụ giữ khoá cũ, thư mục dữ liệu không ghi được) được kiểm TRƯỚC khi gọi máy chủ.
/// </summary>
public sealed class PairCommand(
    AgentPaths paths, IServiceControl? service, Func<PairingCode, CancellationToken, Task<PairingResult>> pair,
    TextWriter output, TextWriter error)
{
    public async Task<int> RunAsync(string? codeText, CancellationToken ct)
    {
        if (!PairingCode.TryFind(codeText, out var code))
        {
            error.WriteLine("Mã ghép nối không hợp lệ — sao chép đủ chuỗi bắt đầu bằng LIS1. từ mục Agent trên web.");
            return 2;
        }

        if (service is not null && service.Exists())
        {
            error.WriteLine("Agent đã cài dạng dịch vụ — chạy tệp cài (LisAgent.exe) và chọn Ghép nối lại");
            return 2;
        }

        if (!DataDirectoryWritable())
        {
            error.WriteLine($"Không ghi được thư mục dữ liệu {paths.Root} — chạy với quyền quản trị");
            return 1;
        }

        try
        {
            var result = await pair(code, ct);
            if (result.Warning is not null)
            {
                error.WriteLine(result.Warning);
            }

            if (result.QuarantinedTo is not null)
            {
                error.WriteLine($"Đổi sang phòng khám khác: kết quả đang chờ gửi của phòng khám cũ đã chuyển vào {result.QuarantinedTo} (không gửi lên phòng khám mới).");
            }

            output.WriteLine($"Đã ghép nối với phòng khám {result.State.ClinicName} ({result.State.ClinicCode}), agent \"{result.State.AgentName}\".");
            return 0;
        }
        catch (Exception ex) when (ex is PairingException or UriFormatException)
        {
            error.WriteLine(ex is UriFormatException ? $"Địa chỉ máy chủ trong mã không hợp lệ: {ex.Message}" : ex.Message);
            return 1;
        }
    }

    /// <summary>Tạo + xoá được tệp thử trong thư mục dữ liệu, và mở được <c>pairing.json</c> hiện có để ghi.</summary>
    private bool DataDirectoryWritable()
    {
        try
        {
            Directory.CreateDirectory(paths.Root);
            var probe = Path.Combine(paths.Root, $".thu-ghi-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            if (File.Exists(paths.Pairing))
            {
                using var existing = new FileStream(paths.Pairing, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
