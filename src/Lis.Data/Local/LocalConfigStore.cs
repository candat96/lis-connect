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

    private string TepCauHinh => Path.Combine(_directory, "config.json");

    public LocalConfig? Load()
    {
        if (!File.Exists(TepCauHinh))
        {
            return null;
        }

        var json = File.ReadAllText(TepCauHinh);
        var banGhi = JsonSerializer.Deserialize<BanGhiCauHinh>(json)
            ?? throw new InvalidDataException("Tệp cấu hình cục bộ không đọc được.");

        var matKhauMa = Convert.FromBase64String(banGhi.PasswordProtected);
        var matKhau = Encoding.UTF8.GetString(_protector.Unprotect(matKhauMa));

        var db = new DbSettings(
            banGhi.Db.Host,
            banGhi.Db.Port,
            banGhi.Db.Database,
            banGhi.Db.Username,
            matKhau,
            banGhi.Db.RequireSsl);

        return new LocalConfig(db, banGhi.WorkstationCode);
    }

    public void Save(LocalConfig cfg)
    {
        Directory.CreateDirectory(_directory);

        var matKhauMa = _protector.Protect(Encoding.UTF8.GetBytes(cfg.Db.Password));

        var banGhi = new BanGhiCauHinh(
            new BanGhiDb(cfg.Db.Host, cfg.Db.Port, cfg.Db.Database, cfg.Db.Username, cfg.Db.RequireSsl),
            Convert.ToBase64String(matKhauMa),
            cfg.WorkstationCode);

        var json = JsonSerializer.Serialize(banGhi, TuyChonJson);
        File.WriteAllText(TepCauHinh, json);
    }

    private static readonly JsonSerializerOptions TuyChonJson = new() { WriteIndented = true };

    /// <summary>Bản ghi JSON trên đĩa: mật khẩu chỉ xuất hiện dưới dạng đã mã hóa.</summary>
    private sealed record BanGhiCauHinh(
        [property: JsonPropertyName("db")] BanGhiDb Db,
        [property: JsonPropertyName("passwordProtected")] string PasswordProtected,
        [property: JsonPropertyName("workstationCode")] string WorkstationCode);

    private sealed record BanGhiDb(
        [property: JsonPropertyName("host")] string Host,
        [property: JsonPropertyName("port")] int Port,
        [property: JsonPropertyName("database")] string Database,
        [property: JsonPropertyName("username")] string Username,
        [property: JsonPropertyName("requireSsl")] bool RequireSsl);
}
