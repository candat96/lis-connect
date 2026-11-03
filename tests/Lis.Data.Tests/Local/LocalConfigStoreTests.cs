using Lis.Data.Local;
using Shouldly;
using Xunit;

namespace Lis.Data.Tests.Local;

/// <summary>
/// Kiểm tra lưu/đọc cấu hình cục bộ và việc mật khẩu không bao giờ được ghi dạng chữ rõ.
/// </summary>
public class LocalConfigStoreTests : IDisposable
{
    private readonly string _thuMucTam;

    public LocalConfigStoreTests()
    {
        _thuMucTam = Path.Combine(Path.GetTempPath(), "lis-connect-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_thuMucTam);
    }

    public void Dispose()
    {
        if (Directory.Exists(_thuMucTam))
        {
            Directory.Delete(_thuMucTam, recursive: true);
        }
    }

    private LocalConfigStore TaoStore()
    {
        var protector = SecretProtectors.ForCurrentOs(_thuMucTam);
        return new LocalConfigStore(_thuMucTam, protector);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var store = TaoStore();
        var cauHinh = new LocalConfig(
            new DbSettings("localhost", 5432, "lis", "lis_user", "Thu@12345", RequireSsl: false),
            "WS-01");

        store.Save(cauHinh);
        var doc = store.Load();

        doc.ShouldNotBeNull();
        doc.ShouldBe(cauHinh);
    }

    [Fact]
    public void Save_DoesNotWritePlainPassword()
    {
        var store = TaoStore();
        var cauHinh = new LocalConfig(
            new DbSettings("localhost", 5432, "lis", "lis_user", "Thu@12345", RequireSsl: true),
            "WS-01");

        store.Save(cauHinh);

        var noiDungTep = File.ReadAllText(Path.Combine(_thuMucTam, "config.json"));
        noiDungTep.ShouldNotContain("Thu@12345");
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        var store = TaoStore();

        var ketQua = store.Load();

        ketQua.ShouldBeNull();
    }
}
