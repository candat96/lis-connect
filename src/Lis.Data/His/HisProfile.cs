using System.Text;

namespace Lis.Data.His;

/// <summary>
/// Cấu hình kết nối HIS (bảng <c>his_profile</c>, luôn một dòng duy nhất, id = 1).
/// Đợt 1 (Task 14) chỉ lưu cấu hình và cho thử kết nối — kéo chỉ định/đẩy kết quả/đồng bộ
/// nhân sự thuộc đợt 3. <see cref="Secret"/> là khóa API hoặc mật khẩu đăng nhập HIS, đã được
/// giải mã khi đọc từ <see cref="HisProfileStore"/> — không bao giờ được ghi ra ToString().
/// </summary>
public sealed record HisProfile(
    bool Enabled,
    string Name,
    string BaseUrl,
    string AuthType,
    string? ApiKeyHeader,
    string? Secret,
    string? Username,
    string? LoginPath,
    string? LoginBodyTemplate,
    string? TokenJsonPath,
    int? TokenTtlMinutes,
    string? OrdersMode,
    string? OrdersPath,
    int? PollSeconds,
    string? ResultsPath,
    string? StaffPath,
    int? PushListenPort,
    string? PushApiKeyHash,
    string? PushWorkstationCode)
{
    /// <summary>Hồ sơ rỗng, dùng khi CSDL chưa có dòng nào (lần đầu mở tab).</summary>
    public static HisProfile Empty { get; } = new(
        Enabled: false,
        Name: "",
        BaseUrl: "",
        AuthType: "none",
        ApiKeyHeader: null,
        Secret: null,
        Username: null,
        LoginPath: null,
        LoginBodyTemplate: null,
        TokenJsonPath: null,
        TokenTtlMinutes: null,
        OrdersMode: null,
        OrdersPath: null,
        PollSeconds: null,
        ResultsPath: null,
        StaffPath: null,
        PushListenPort: null,
        PushApiKeyHash: null,
        PushWorkstationCode: null);

    /// <summary>
    /// Hồ sơ dựng sẵn "HIS-PROMAX": xác thực kiểu <c>api-key</c> (tiêu đề <c>X-Integration-Key</c>),
    /// đường kiểm tra kết nối là API công khai <c>/api/v1/system/info</c>. Các API đặt lệnh/kết
    /// quả/nhân sự thật của HIS-PROMAX chưa có ở đợt 1 — để trống, làm ở đợt 3.
    /// </summary>
    public static HisProfile HisPromaxPreset { get; } = Empty with
    {
        Name = "HIS-PROMAX",
        AuthType = "api-key",
        ApiKeyHeader = "X-Integration-Key",
        OrdersPath = "/api/v1/system/info",
    };

    /// <summary>
    /// Ghi đè để che bí mật trong ToString() — tránh lộ khóa API/mật khẩu qua log/thông báo lỗi/debugger.
    /// </summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(
            $"Enabled = {Enabled}, Name = {Name}, BaseUrl = {BaseUrl}, AuthType = {AuthType}, " +
            $"ApiKeyHeader = {ApiKeyHeader}, Secret = {(string.IsNullOrEmpty(Secret) ? "(trống)" : "***")}, " +
            $"Username = {Username}, LoginPath = {LoginPath}, OrdersMode = {OrdersMode}, OrdersPath = {OrdersPath}");
        return true;
    }
}
