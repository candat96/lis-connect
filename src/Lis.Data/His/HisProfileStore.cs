using System.Text;
using Dapper;
using Lis.Data.Local;

namespace Lis.Data.His;

/// <summary>
/// Truy cập cấu hình kết nối HIS (bảng <c>his_profile</c>, id luôn là 1). Bí mật
/// (<see cref="HisProfile.Secret"/>) được mã hóa bằng <see cref="ISecretProtector"/> trước khi
/// ghi CSDL (lưu base64 ở cột <c>secret_protected</c>) và giải mã khi đọc.
///
/// DPAPI phạm vi máy (<c>LocalMachine</c>) nghĩa là bí mật mã hóa trên máy trạm A không giải mã
/// được trên máy trạm B — <see cref="GetAsync"/> coi lỗi giải mã là "chưa có bí mật" (trả về
/// <c>Secret = null</c>) thay vì ném lỗi, để màn Cài đặt chỉ cần người dùng nhập lại.
/// </summary>
public sealed class HisProfileStore(Db db, ISecretProtector protector)
{
    public async Task<HisProfile?> GetAsync()
    {
        await using var connection = await db.OpenAsync();
        var row = await connection.QuerySingleOrDefaultAsync<HisProfileRow>(
            """
            SELECT enabled, name, base_url, auth_type, api_key_header, secret_protected, username,
                   login_path, login_body_template, token_json_path, token_ttl_minutes,
                   orders_mode, orders_path, poll_seconds, results_path, staff_path,
                   push_listen_port, push_api_key_hash, push_workstation_code
            FROM his_profile
            WHERE id = 1
            """);

        if (row is null)
        {
            return null;
        }

        return row.ToHisProfile(DecryptSecret(row.SecretProtected));
    }

    public async Task SaveAsync(HisProfile profile, Guid actorId)
    {
        // Để trống Secret nghĩa là giữ bí mật cũ (màn Cài đặt không hiện lại bí mật đã lưu) —
        // COALESCE với giá trị cũ trong CSDL khi tham số truyền vào là null.
        string? newSecretProtected = string.IsNullOrEmpty(profile.Secret)
            ? null
            : Convert.ToBase64String(protector.Protect(Encoding.UTF8.GetBytes(profile.Secret)));

        await using var connection = await db.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO his_profile (
                id, enabled, name, base_url, auth_type, api_key_header, secret_protected, username,
                login_path, login_body_template, token_json_path, token_ttl_minutes,
                orders_mode, orders_path, poll_seconds, results_path, staff_path,
                push_listen_port, push_api_key_hash, push_workstation_code, updated_at, updated_by)
            VALUES (
                1, @Enabled, @Name, @BaseUrl, @AuthType, @ApiKeyHeader, @SecretProtected, @Username,
                @LoginPath, @LoginBodyTemplate, @TokenJsonPath, @TokenTtlMinutes,
                @OrdersMode, @OrdersPath, @PollSeconds, @ResultsPath, @StaffPath,
                @PushListenPort, @PushApiKeyHash, @PushWorkstationCode, now(), @ActorId)
            ON CONFLICT (id) DO UPDATE SET
                enabled = EXCLUDED.enabled,
                name = EXCLUDED.name,
                base_url = EXCLUDED.base_url,
                auth_type = EXCLUDED.auth_type,
                api_key_header = EXCLUDED.api_key_header,
                secret_protected = COALESCE(EXCLUDED.secret_protected, his_profile.secret_protected),
                username = EXCLUDED.username,
                login_path = EXCLUDED.login_path,
                login_body_template = EXCLUDED.login_body_template,
                token_json_path = EXCLUDED.token_json_path,
                token_ttl_minutes = EXCLUDED.token_ttl_minutes,
                orders_mode = EXCLUDED.orders_mode,
                orders_path = EXCLUDED.orders_path,
                poll_seconds = EXCLUDED.poll_seconds,
                results_path = EXCLUDED.results_path,
                staff_path = EXCLUDED.staff_path,
                push_listen_port = EXCLUDED.push_listen_port,
                push_api_key_hash = EXCLUDED.push_api_key_hash,
                push_workstation_code = EXCLUDED.push_workstation_code,
                updated_at = now(),
                updated_by = EXCLUDED.updated_by
            """,
            new
            {
                profile.Enabled,
                profile.Name,
                profile.BaseUrl,
                profile.AuthType,
                profile.ApiKeyHeader,
                SecretProtected = newSecretProtected,
                profile.Username,
                profile.LoginPath,
                profile.LoginBodyTemplate,
                profile.TokenJsonPath,
                profile.TokenTtlMinutes,
                profile.OrdersMode,
                profile.OrdersPath,
                profile.PollSeconds,
                profile.ResultsPath,
                profile.StaffPath,
                profile.PushListenPort,
                profile.PushApiKeyHash,
                profile.PushWorkstationCode,
                ActorId = actorId,
            });
    }

    /// <summary>Giải mã bí mật; coi mọi lỗi giải mã (vd đổi máy, DPAPI LocalMachine khác khóa) là "chưa có bí mật".</summary>
    private string? DecryptSecret(string? secretProtected)
    {
        if (string.IsNullOrEmpty(secretProtected))
        {
            return null;
        }

        try
        {
            var cipher = Convert.FromBase64String(secretProtected);
            return Encoding.UTF8.GetString(protector.Unprotect(cipher));
        }
        catch
        {
            return null;
        }
    }

    private sealed record HisProfileRow
    {
        public bool Enabled { get; init; }
        public string Name { get; init; } = "";
        public string BaseUrl { get; init; } = "";
        public string AuthType { get; init; } = "";
        public string? ApiKeyHeader { get; init; }
        public string? SecretProtected { get; init; }
        public string? Username { get; init; }
        public string? LoginPath { get; init; }
        public string? LoginBodyTemplate { get; init; }
        public string? TokenJsonPath { get; init; }
        public int? TokenTtlMinutes { get; init; }
        public string? OrdersMode { get; init; }
        public string? OrdersPath { get; init; }
        public int? PollSeconds { get; init; }
        public string? ResultsPath { get; init; }
        public string? StaffPath { get; init; }
        public int? PushListenPort { get; init; }
        public string? PushApiKeyHash { get; init; }
        public string? PushWorkstationCode { get; init; }

        public HisProfile ToHisProfile(string? secret) => new(
            Enabled, Name, BaseUrl, AuthType, ApiKeyHeader, secret, Username,
            LoginPath, LoginBodyTemplate, TokenJsonPath, TokenTtlMinutes,
            OrdersMode, OrdersPath, PollSeconds, ResultsPath, StaffPath,
            PushListenPort, PushApiKeyHash, PushWorkstationCode);
    }
}
