using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lis.Data.Local;

/// <summary>
/// Đọc/ghi cấu hình cục bộ tại <c>&lt;directory&gt;/config.json</c>. Mật khẩu CSDL không bao giờ
/// được ghi dạng chữ rõ: trường <c>passwordProtected</c> lưu chuỗi base64 của bản đã mã hóa
/// bằng <see cref="ISecretProtector"/>.
/// </summary>
public sealed class LocalConfigStore
{
    private readonly string _directory;
    private readonly ISecretProtector _protector;

    public LocalConfigStore(string directory, ISecretProtector protector)
    {
        _directory = directory;
        _protector = protector;
    }

    private string ConfigFile => Path.Combine(_directory, "config.json");

    public LocalConfig? Load()
    {
        if (!File.Exists(ConfigFile))
        {
            return null;
        }

        var json = File.ReadAllText(ConfigFile);
        var record = JsonSerializer.Deserialize<ConfigRecord>(json)
            ?? throw new InvalidDataException("Tệp cấu hình cục bộ không đọc được.");

        var protectedPassword = Convert.FromBase64String(record.PasswordProtected);
        var password = Encoding.UTF8.GetString(_protector.Unprotect(protectedPassword));

        var db = new DbSettings(
            record.Db.Host,
            record.Db.Port,
            record.Db.Database,
            record.Db.Username,
            password,
            record.Db.RequireSsl);

        return new LocalConfig(db, record.WorkstationCode);
    }

    /// <summary>
    /// Giống <see cref="Load"/> nhưng không bao giờ ném: tệp hỏng (JSON lỗi, hoặc mã hóa bằng
    /// DPAPI của máy khác nên không giải mã được) được coi như "chưa cài đặt" — đổi tên thành
    /// <c>config.json.bak</c> để giữ lại cho việc chẩn đoán, rồi trả về null kèm thông báo tiếng
    /// Việt để nơi gọi (App khởi động) mở lại trình hướng dẫn cài đặt thay vì crash.
    /// </summary>
    public LocalConfig? TryLoadOrRecover(out string? error)
    {
        try
        {
            error = null;
            return Load();
        }
        catch (Exception ex)
        {
            error = $"Tệp cấu hình cục bộ ({ConfigFile}) không đọc được, có thể do hỏng tệp hoặc tệp được tạo trên máy khác: {ex.Message}. " +
                    "Tệp cũ đã được đổi tên thành config.json.bak — vui lòng cài đặt lại.";

            try
            {
                var backupFile = ConfigFile + ".bak";
                if (File.Exists(backupFile))
                {
                    File.Delete(backupFile);
                }

                if (File.Exists(ConfigFile))
                {
                    File.Move(ConfigFile, backupFile);
                }
            }
            catch
            {
                // Không đổi tên được (vd quyền tệp) — vẫn tiếp tục mở trình hướng dẫn cài đặt,
                // người dùng có thể tự xóa tệp theo hướng dẫn trong CHAY-THU-WINDOWS.md.
            }

            return null;
        }
    }

    public void Save(LocalConfig cfg)
    {
        Directory.CreateDirectory(_directory);

        var protectedPassword = _protector.Protect(Encoding.UTF8.GetBytes(cfg.Db.Password));

        var record = new ConfigRecord(
            new DbRecord(cfg.Db.Host, cfg.Db.Port, cfg.Db.Database, cfg.Db.Username, cfg.Db.RequireSsl),
            Convert.ToBase64String(protectedPassword),
            cfg.WorkstationCode);

        var json = JsonSerializer.Serialize(record, JsonOptions);
        File.WriteAllText(ConfigFile, json);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Bản ghi JSON trên đĩa: mật khẩu chỉ xuất hiện dưới dạng đã mã hóa.</summary>
    private sealed record ConfigRecord(
        [property: JsonPropertyName("db")] DbRecord Db,
        [property: JsonPropertyName("passwordProtected")] string PasswordProtected,
        [property: JsonPropertyName("workstationCode")] string WorkstationCode);

    private sealed record DbRecord(
        [property: JsonPropertyName("host")] string Host,
        [property: JsonPropertyName("port")] int Port,
        [property: JsonPropertyName("database")] string Database,
        [property: JsonPropertyName("username")] string Username,
        [property: JsonPropertyName("requireSsl")] bool RequireSsl);
}
