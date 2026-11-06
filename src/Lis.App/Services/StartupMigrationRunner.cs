using Lis.Data;

namespace Lis.App.Services;

/// <summary>Kết quả quyết định sau khi chạy migration CSDL lúc khởi động.</summary>
public enum StartupMigrationOutcome
{
    Success,
    Failed,
}

/// <summary>
/// Kết quả của <see cref="StartupMigrationRunner.Run"/>: thành công, hoặc thất bại kèm thông báo
/// lỗi và (nếu có) ngoại lệ gốc — tách hai trường hợp này vì migration có thể thất bại "sạch"
/// (<see cref="MigrationResult.Ok"/> == false, không có ngoại lệ) hoặc ném ngoại lệ thật (vd mất
/// kết nối CSDL ngay ở <c>EnsureUuidv7Compat</c>), và nơi gọi (App.StartUp) cần ghi log đúng cách
/// cho từng trường hợp.
/// </summary>
public sealed record StartupMigrationResult(StartupMigrationOutcome Outcome, string? ErrorMessage, Exception? Exception)
{
    public static readonly StartupMigrationResult Success = new(StartupMigrationOutcome.Success, null, null);

    public bool Ok => Outcome == StartupMigrationOutcome.Success;
}

/// <summary>
/// Chạy bước migration CSDL lúc khởi động và chuẩn hóa kết quả — tách riêng khỏi
/// <see cref="App"/> để kiểm thử được bằng một hàm migrate giả (ném ngoại lệ hoặc trả về
/// <see cref="MigrationResult.Ok"/> == false) mà không cần CSDL thật hay Avalonia. Trước khi có
/// lớp này, <c>Migrator.Upgrade</c> được gọi trực tiếp trong <c>App.StartUp</c> không bọc
/// try/catch: CSDL không kết nối được lúc khởi động làm <c>EnsureUuidv7Compat</c>'s
/// <c>connection.Open()</c> ném ra ngoài <c>OnFrameworkInitializationCompleted</c>, crash cả ứng
/// dụng không kịp hiện thông báo gì.
/// </summary>
public static class StartupMigrationRunner
{
    public static StartupMigrationResult Run(Func<MigrationResult> migrate)
    {
        try
        {
            var result = migrate();
            return result.Ok
                ? StartupMigrationResult.Success
                : new StartupMigrationResult(StartupMigrationOutcome.Failed, result.Error, Exception: null);
        }
        catch (Exception ex)
        {
            return new StartupMigrationResult(StartupMigrationOutcome.Failed, ex.Message, ex);
        }
    }
}
