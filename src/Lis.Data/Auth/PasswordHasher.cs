using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Lis.Data.Auth;

/// <summary>
/// Băm và kiểm mật khẩu bằng Argon2id. Chuỗi lưu CSDL có dạng
/// <c>argon2id$&lt;m&gt;$&lt;t&gt;$&lt;p&gt;$&lt;salt b64&gt;$&lt;hash b64&gt;</c> để có thể
/// đổi tham số (bộ nhớ/số lượt/số luồng) mà vẫn kiểm được mật khẩu cũ.
/// </summary>
public sealed class PasswordHasher
{
    private const string Prefix = "argon2id";
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;
    private const int MemoryKib = 64 * 1024; // 64 MB
    private const int Iterations = 3;
    private const int DegreeOfParallelism = 1;

    // Giới hạn hợp lý để chặn hash lưu bị hỏng/giả mạo (0, âm, hoặc quá lớn) trước khi đưa
    // vào Argon2id — tránh ném lỗi hoặc tốn tài nguyên bất thường khi Verify một hash hỏng.
    private const int MaxMemoryKib = 1024 * 1024; // 1 GB
    private const int MaxIterations = 100;
    private const int MaxDegreeOfParallelism = 64;

    /// <summary>
    /// Băm mật khẩu với salt ngẫu nhiên mới.
    /// </summary>
    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var hash = ComputeHash(password, salt, MemoryKib, Iterations, DegreeOfParallelism);

        return string.Join(
            '$',
            Prefix,
            MemoryKib,
            Iterations,
            DegreeOfParallelism,
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    /// <summary>
    /// Kiểm mật khẩu so với chuỗi băm đã lưu. So sánh theo thời gian cố định
    /// để tránh lộ thông tin qua thời gian xử lý (timing attack).
    /// </summary>
    public bool Verify(string password, string hash)
    {
        var parts = hash.Split('$');
        if (parts.Length != 6 || parts[0] != Prefix)
        {
            return false;
        }

        if (!int.TryParse(parts[1], out var memoryKib) ||
            !int.TryParse(parts[2], out var iterations) ||
            !int.TryParse(parts[3], out var degreeOfParallelism))
        {
            return false;
        }

        if (memoryKib <= 0 || memoryKib > MaxMemoryKib ||
            iterations <= 0 || iterations > MaxIterations ||
            degreeOfParallelism <= 0 || degreeOfParallelism > MaxDegreeOfParallelism)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[4]);
            expected = Convert.FromBase64String(parts[5]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length == 0 || expected.Length == 0)
        {
            return false;
        }

        try
        {
            var actual = ComputeHash(password, salt, memoryKib, iterations, degreeOfParallelism);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (Exception)
        {
            // Hash lưu có tham số hợp lệ về khoảng giá trị nhưng Argon2id vẫn có thể từ chối
            // (vd salt quá ngắn) — xử lý như mật khẩu không khớp, không để lộ ngoại lệ.
            return false;
        }
    }

    private static byte[] ComputeHash(string password, byte[] salt, int memoryKib, int iterations, int degreeOfParallelism)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = degreeOfParallelism,
            Iterations = iterations,
            MemorySize = memoryKib,
        };

        return argon2.GetBytes(HashSizeBytes);
    }
}
