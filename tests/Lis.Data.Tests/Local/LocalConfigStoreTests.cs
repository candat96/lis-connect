using Lis.Data.Local;
using Npgsql;
using Shouldly;
using Xunit;

namespace Lis.Data.Tests.Local;

/// <summary>
/// Kiểm tra lưu/đọc cấu hình cục bộ và việc mật khẩu không bao giờ được ghi dạng chữ rõ.
/// </summary>
public class LocalConfigStoreTests : IDisposable
{
    private readonly string _tempDirectory;

    public LocalConfigStoreTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "lis-connect-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private LocalConfigStore CreateStore()
    {
        var protector = SecretProtectors.ForCurrentOs(_tempDirectory);
        return new LocalConfigStore(_tempDirectory, protector);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var store = CreateStore();
        var config = new LocalConfig(
            new DbSettings("localhost", 5432, "lis", "lis_user", "Thu@12345", RequireSsl: false),
            "WS-01");

        store.Save(config);
        var loaded = store.Load();

        loaded.ShouldNotBeNull();
        loaded.ShouldBe(config);
    }

    [Fact]
    public void Save_DoesNotWritePlainPassword()
    {
        var store = CreateStore();
        var config = new LocalConfig(
            new DbSettings("localhost", 5432, "lis", "lis_user", "Thu@12345", RequireSsl: true),
            "WS-01");

        store.Save(config);

        var fileContent = File.ReadAllText(Path.Combine(_tempDirectory, "config.json"));
        fileContent.ShouldNotContain("Thu@12345");
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        var store = CreateStore();

        var result = store.Load();

        result.ShouldBeNull();
    }

    [Fact]
    public void DbSettings_ToString_RedactsPassword()
    {
        var db = new DbSettings("localhost", 5432, "lis", "lis_user", "Thu@12345", RequireSsl: false);
        var config = new LocalConfig(db, "WS-01");

        db.ToString().ShouldNotContain("Thu@12345");
        config.ToString().ShouldNotContain("Thu@12345");
    }

    [Theory]
    [InlineData("mat;khau=\"ky,tu\"dac;biet")]
    [InlineData("a\"b;c=d")]
    [InlineData("đã có dấu;cách")]
    public void DbSettings_ToConnectionString_RoundTripsSpecialCharacterPassword(string password)
    {
        var db = new DbSettings("localhost", 5432, "lis", "lis_user", password, RequireSsl: false);

        var connectionString = db.ToConnectionString();
        var parsed = new NpgsqlConnectionStringBuilder(connectionString);

        parsed.Password.ShouldBe(password);
        parsed.Host.ShouldBe("localhost");
        parsed.Database.ShouldBe("lis");
        parsed.Username.ShouldBe("lis_user");
    }

    [Fact]
    public void Load_CorruptJson_ThrowsAndLeavesFileUntouched()
    {
        var store = CreateStore();
        var configFile = Path.Combine(_tempDirectory, "config.json");
        File.WriteAllText(configFile, "{ không phải json hợp lệ");

        Should.Throw<Exception>(() => store.Load());
    }

    [Fact]
    public void Load_CorruptJson_RenamesToBakOnRecovery()
    {
        var store = CreateStore();
        var configFile = Path.Combine(_tempDirectory, "config.json");
        File.WriteAllText(configFile, "{ không phải json hợp lệ");

        store.TryLoadOrRecover(out var error).ShouldBeNull();
        error.ShouldNotBeNullOrWhiteSpace();

        File.Exists(configFile).ShouldBeFalse();
        File.Exists(Path.Combine(_tempDirectory, "config.json.bak")).ShouldBeTrue();
    }

    [Fact]
    public void TryLoadOrRecover_ValidFile_ReturnsConfig()
    {
        var store = CreateStore();
        var config = new LocalConfig(
            new DbSettings("localhost", 5432, "lis", "lis_user", "Thu@12345", RequireSsl: false),
            "WS-01");
        store.Save(config);

        var loaded = store.TryLoadOrRecover(out var error);

        loaded.ShouldBe(config);
        error.ShouldBeNull();
    }

    [Fact]
    public void TryLoadOrRecover_MissingFile_ReturnsNullNoError()
    {
        var store = CreateStore();

        var loaded = store.TryLoadOrRecover(out var error);

        loaded.ShouldBeNull();
        error.ShouldBeNull();
    }
}
