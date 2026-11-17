using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Lis.Data.Local;

namespace Lis.Agent;

/// <summary>Kết quả đã ghép nối với một phòng khám (không chứa khoá).</summary>
public sealed record PairingState(
    string ServerUrl, Guid ClinicId, string ClinicCode, string ClinicName, Guid AgentId, string AgentName, DateTimeOffset PairedAt);

/// <summary>
/// Lưu kết quả ghép nối ở <c>pairing.json</c>: thông tin phòng khám để rõ, khoá agent chỉ nằm dạng đã mã hoá
/// (<see cref="ISecretProtector"/>). Ghi qua tệp tạm rồi đổi tên nên không bao giờ để lại tệp dở.
/// Ghép nối sang phòng khám khác thì hàng chờ <c>cho-gui</c> cũ bị cách ly — không bao giờ gửi lên phòng khám mới.
/// </summary>
public sealed class PairingStore(AgentPaths paths, ISecretProtector protector, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Thư mục cách ly của lần <see cref="Save"/> gần nhất (null nếu không phải cách ly) — để nơi gọi báo cho người vận hành.</summary>
    public string? LastQuarantine { get; private set; }

    private sealed record Stored(PairingState State, string ProtectedKey);

    /// <summary>Đọc ghép nối đã lưu; tệp không có, hỏng hoặc không giải mã được → null.</summary>
    public (PairingState State, string AgentKey)? Load()
    {
        try
        {
            if (ReadStored() is not { } stored)
            {
                return null;
            }

            var key = Encoding.UTF8.GetString(protector.Unprotect(Convert.FromBase64String(stored.ProtectedKey)));
            return string.IsNullOrWhiteSpace(key) ? null : (stored.State, key);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    public void Save(PairingState state, string agentKey)
    {
        Directory.CreateDirectory(paths.Root);
        LastQuarantine = null;

        // Phòng khám cũ đọc từ phần thông tin (không cần giải mã khoá) để khoá hỏng vẫn cách ly đúng.
        var previous = TryReadStored()?.State;
        if (previous is null ? HasPending() : previous.ClinicId != state.ClinicId)
        {
            QuarantinePending(previous?.ClinicCode ?? "khong-ro");
        }

        var stored = new Stored(state, Convert.ToBase64String(protector.Protect(Encoding.UTF8.GetBytes(agentKey))));
        var temp = paths.Pairing + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(stored, Json), new UTF8Encoding(false));
            if (OperatingSystem.IsWindows())
            {
                RestrictToAdministrators(temp);
            }

            File.Move(temp, paths.Pairing, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }

        // Bộ đệm của agent / phòng khám cũ không dùng lại được cho ghép nối mới.
        File.Delete(paths.IdentityCache);
        File.Delete(paths.InstrumentsCache);
    }

    public void Delete() => File.Delete(paths.Pairing);

    private Stored? TryReadStored()
    {
        try
        {
            return ReadStored();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private Stored? ReadStored()
    {
        if (!File.Exists(paths.Pairing))
        {
            return null;
        }

        var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(paths.Pairing), Json);
        return stored?.State is null || stored.ProtectedKey is null ? null : stored;
    }

    private bool HasPending() =>
        Directory.Exists(paths.Pending) && Directory.EnumerateFileSystemEntries(paths.Pending).Any();

    /// <summary>Chuyển <c>cho-gui</c> sang <c>cach-ly/&lt;yyyyMMdd-HHmmss&gt;-&lt;mã phòng khám cũ&gt;/cho-gui</c>.</summary>
    private void QuarantinePending(string oldClinicCode)
    {
        if (!Directory.Exists(paths.Pending))
        {
            return;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var safeCode = new string(oldClinicCode.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        var folder = Path.Combine(paths.Quarantine, $"{clock.GetUtcNow():yyyyMMdd-HHmmss}-{safeCode}");
        for (var n = 2; Directory.Exists(folder); n++)
        {
            folder = Path.Combine(paths.Quarantine, $"{clock.GetUtcNow():yyyyMMdd-HHmmss}-{safeCode}-{n}");
        }

        Directory.CreateDirectory(folder);
        Directory.Move(paths.Pending, Path.Combine(folder, "cho-gui"));
        LastQuarantine = folder;
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictToAdministrators(string file)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        }

        new FileInfo(file).SetAccessControl(security);
    }
}
