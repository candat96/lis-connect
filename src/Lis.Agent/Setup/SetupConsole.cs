using Lis.Core.Agents;

namespace Lis.Agent;

/// <summary>
/// Giao diện dòng lệnh của tệp cài: xin quyền quản trị, lấy mã ghép nối (từ tên tệp hoặc hỏi), cài / ghép nối lại / gỡ.
/// Đọc, ghi qua <see cref="TextReader"/>/<see cref="TextWriter"/> để kiểm thử; mã hoá UTF-8 của console đặt ở Program.
/// </summary>
public sealed class SetupConsole(
    TextReader input, TextWriter output, IElevation elevation, AgentInstaller installer, PairingStore store, string exePath)
{
    private const int MaxCodeAttempts = 3;

    public const int ExitNeedAdmin = 5;

    public async Task<int> RunAsync(AgentCommand command, CancellationToken ct)
    {
        switch (command)
        {
            case AgentCommand.Interactive when !elevation.IsElevated():
                return Relaunch("", pause: true);
            case AgentCommand.Uninstall when !elevation.IsElevated():
                return Relaunch("uninstall", pause: false);
            case AgentCommand.Install(null) when !elevation.IsElevated():
                return Relaunch("install", pause: false);
            case AgentCommand.Install when !elevation.IsElevated():
                // Mã là bí mật — không đưa lên dòng lệnh của tiến trình nâng quyền.
                output.WriteLine("Cần quyền quản trị Windows để cài agent. Mở cửa sổ dòng lệnh bằng \"Run as administrator\" rồi chạy lại lệnh.");
                return ExitNeedAdmin;
            case AgentCommand.Install install:
                return await InstallAsync(install.CodeText ?? FileNameCodeText(), ct, pause: false);
            case AgentCommand.Uninstall:
                return Report(installer.Uninstall(), pause: false);
            case AgentCommand.Interactive:
                return await InteractiveAsync(ct);
            default:
                output.WriteLine("Lệnh này không dùng cho tệp cài.");
                return 2;
        }
    }

    private int Relaunch(string arguments, bool pause)
    {
        if (elevation.RelaunchElevated(exePath, arguments, out var exitCode) == ElevationResult.Started)
        {
            return exitCode;
        }

        output.WriteLine("Cần quyền quản trị Windows để cài agent. Nhờ người có tài khoản quản trị bấm chạy lại tệp này.");
        if (pause)
        {
            WaitEnter();
        }

        return ExitNeedAdmin;
    }

    private async Task<int> InteractiveAsync(CancellationToken ct)
    {
        var installed = installer.ServiceExists || store.Load() is not null;
        if (!installed)
        {
            return await InstallAsync(FileNameCodeText(), ct, pause: true);
        }

        var paired = store.Load();
        output.WriteLine(paired is { } p
            ? $"Agent đã cài trên máy này: phòng khám {p.State.ClinicName}, agent {p.State.AgentName}."
            : "Agent đã cài trên máy này nhưng chưa ghép nối.");
        while (true)
        {
            output.WriteLine("1. Ghép nối lại  2. Gỡ agent  0. Thoát");
            output.Write("Chọn: ");
            switch (input.ReadLine()?.Trim())
            {
                case "1":
                    return await InstallAsync(FileNameCodeText(), ct, pause: true);
                case "2":
                    return Report(installer.Uninstall(), pause: true);
                case "0" or null:
                    return 0;
                default:
                    output.WriteLine("Lựa chọn không hợp lệ.");
                    break;
            }
        }
    }

    private async Task<int> InstallAsync(string? codeText, CancellationToken ct, bool pause)
    {
        if (!PairingCode.TryFind(codeText, out var code) && (code = AskCode()) is null)
        {
            output.WriteLine("Không nhận được mã ghép nối hợp lệ. Lấy mã mới ở mục Agent trên web rồi chạy lại tệp này.");
            if (pause)
            {
                WaitEnter();
            }

            return 1;
        }

        return Report(await installer.InstallOrRepairAsync(exePath, code, ct), pause);
    }

    /// <summary>Mã nằm trong tên tệp tải về (trình duyệt có thể thêm " (1)"); không có → null.</summary>
    private string FileNameCodeText() => Path.GetFileName(exePath);

    private PairingCode? AskCode()
    {
        for (var attempt = 1; attempt <= MaxCodeAttempts; attempt++)
        {
            output.WriteLine("Dán mã ghép nối (lấy ở mục Agent trên web) rồi Enter:");
            var line = input.ReadLine();
            if (line is null)
            {
                return null;
            }

            if (PairingCode.TryFind(line, out var code))
            {
                return code;
            }

            output.WriteLine("Không đọc được mã ghép nối — hãy sao chép đủ chuỗi bắt đầu bằng LIS1. (không thiếu ký tự cuối).");
        }

        return null;
    }

    private int Report(InstallOutcome outcome, bool pause)
    {
        var exit = 0;
        switch (outcome)
        {
            case InstallOutcome.Succeeded ok:
                if (ok.Pairing.Warning is not null)
                {
                    output.WriteLine(ok.Pairing.Warning);
                }

                if (ok.Pairing.QuarantinedTo is not null)
                {
                    output.WriteLine($"Đổi sang phòng khám khác: kết quả đang chờ gửi của phòng khám cũ đã chuyển vào {ok.Pairing.QuarantinedTo} (không gửi lên phòng khám mới).");
                }

                output.WriteLine($"Xong. Agent đang chạy và đã kết nối tới {ok.Pairing.State.ServerUrl}. Có thể đóng cửa sổ này.");
                break;
            case InstallOutcome.Uninstalled gone:
                output.WriteLine($"Đã gỡ agent. Dữ liệu (kết quả chờ gửi, nhật ký) vẫn giữ ở {gone.DataDirectory}; xoá thư mục đó nếu không cần nữa.");
                break;
            case InstallOutcome.Failed failed:
                output.WriteLine(failed.Message);
                exit = 1;
                break;
        }

        if (pause)
        {
            WaitEnter();
        }

        return exit;
    }

    private void WaitEnter()
    {
        output.WriteLine("Nhấn Enter để đóng");
        input.ReadLine();
    }
}
