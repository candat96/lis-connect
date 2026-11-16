using System.Collections.Concurrent;
using Lis.Core.Auth;
using Lis.Data;
using Lis.Data.Auth;
using Lis.Server.Api;
using Npgsql;

namespace Lis.Server.Tenancy;

/// <summary>Lỗi tạo / migrate CSDL phòng khám; <see cref="Exception.Message"/> là câu cho người vận hành (lệnh dòng lệnh in thẳng).</summary>
public sealed class ProvisioningException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Tạo phòng khám (CSDL riêng + migrate + tài khoản Quản trị đầu tiên) và nâng cấp CSDL của mọi phòng khám lúc khởi động.
/// Dùng chung cho lệnh dòng lệnh (<c>clinic add</c>, <c>user add</c>) và trang thiết lập trên web.
/// </summary>
public sealed class ClinicProvisioner(MasterStore master, TenantRegistry tenants, ILogger<ClinicProvisioner>? logger = null)
{
    /// <summary>Khoá <c>pg_advisory_lock</c> trên master xếp hàng việc tạo phòng khám ("LISCLINC").</summary>
    private const long CreateLockKey = 0x4C4953434C494E43;

    private readonly ConcurrentDictionary<Guid, string> _failedUpgrades = new();

    /// <summary>Phòng khám nâng cấp CSDL lỗi lúc khởi động → thông báo lỗi. Các route của phòng khám này trả 503.</summary>
    public IReadOnlyDictionary<Guid, string> FailedUpgrades => _failedUpgrades;

    /// <summary>
    /// Tạo phòng khám từ web: CSDL <c>lis_</c> + mã (gạch ngang → gạch dưới), migrate, tài khoản Quản trị đầu tiên, rồi mới ghi
    /// vào master. Thất bại giữa chừng thì không ghi master (và xoá CSDL vừa tạo), nên thử lại cùng mã được.
    /// Trùng mã → <see cref="ApiException"/> 409. Mã, tên, tài khoản đã được tầng web kiểm.
    /// </summary>
    public async Task<Clinic> CreateAsync(string code, string name, string adminUsername, string adminFullName, string adminPassword)
    {
        var dbName = "lis_" + code.Replace('-', '_');
        try
        {
            return await ProvisionAsync(
                code, name, dbName,
                async tenant => await tenant.Users.CreateAsync(adminUsername.Trim(), adminFullName.Trim(), title: null, adminPassword, [Roles.QuanTri]),
                log: null);
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Tạo phòng khám {Clinic} thất bại", code);
            throw new ApiException(
                StatusCodes.Status500InternalServerError,
                "Không tạo được phòng khám — hệ thống đã hoàn tác, thử lại; nếu vẫn lỗi báo quản trị hệ thống");
        }
    }

    /// <summary>
    /// Thêm phòng khám với CSDL đặt tên sẵn (lệnh <c>clinic add</c>): tạo CSDL nếu chưa có, migrate, ghi master.
    /// CSDL có sẵn dữ liệu (chuyển từ bản cài tại chỗ) dùng lại được. Migrate lỗi → <see cref="ProvisioningException"/>.
    /// </summary>
    public Task<Clinic> AddAsync(string code, string name, string dbName, Action<string> log) =>
        ProvisionAsync(code, name, dbName, seed: null, log);

    /// <summary>Tạo một tài khoản cho phòng khám đã có (lệnh <c>user add</c>).</summary>
    public Task<UserInfo> AddUserAsync(Clinic clinic, string username, string fullName, string password, string role) =>
        tenants.For(clinic).Users.CreateAsync(username.Trim(), fullName.Trim(), title: null, password, [role]);

    /// <summary>Nâng schema CSDL của mọi phòng khám lên bản mới nhất; phòng khám lỗi được ghi vào <see cref="FailedUpgrades"/> và bị cô lập.</summary>
    public async Task UpgradeAllAsync()
    {
        foreach (var clinic in await master.ClinicsAsync())
        {
            string? error;
            try
            {
                var result = Migrator.Upgrade(tenants.ConnectionStringFor(clinic));
                error = result.Ok ? null : result.Error;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            if (error is null)
            {
                _failedUpgrades.TryRemove(clinic.Id, out _);
                continue;
            }

            _failedUpgrades[clinic.Id] = error;
            logger?.LogError("Nâng cấp CSDL phòng khám {Clinic} thất bại: {Error}", clinic.Code, error);
        }
    }

    private async Task<Clinic> ProvisionAsync(string code, string name, string dbName, Func<TenantServices, Task>? seed, Action<string>? log)
    {
        await using var _ = await master.AcquireLockAsync(CreateLockKey);

        if (await master.FindClinicAsync(code) is not null)
        {
            throw ApiException.Conflict("Mã phòng khám đã tồn tại");
        }

        if ((await master.ClinicsAsync()).Any(c => c.DbName == dbName))
        {
            throw ApiException.Conflict("CSDL này đã dùng cho một phòng khám khác");
        }

        var probe = new Clinic(Guid.Empty, code, name, dbName);
        var connectionString = tenants.ConnectionStringFor(probe);
        var created = false;
        try
        {
            created = await CreateDatabaseIfMissingAsync(connectionString, dbName);
            if (created)
            {
                log?.Invoke($"Đã tạo CSDL {dbName}.");
            }

            var migration = Migrator.Upgrade(connectionString);
            if (!migration.Ok)
            {
                throw new ProvisioningException($"Migrate CSDL {dbName} thất bại: {migration.Error}");
            }

            if (seed is not null)
            {
                await seed(tenants.For(probe));
            }

            return await master.AddClinicAsync(code, name, dbName);
        }
        catch when (created)
        {
            await DropDatabaseQuietlyAsync(connectionString, dbName);
            throw;
        }
    }

    private static async Task<bool> CreateDatabaseIfMissingAsync(string connectionString, string dbName)
    {
        await using var connection = await OpenServerConnectionAsync(connectionString);
        await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
        exists.Parameters.AddWithValue("name", dbName);
        if (await exists.ExecuteScalarAsync() is not null)
        {
            return false;
        }

        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", connection);
        await create.ExecuteNonQueryAsync();
        return true;
    }

    /// <summary>Hoàn tác CSDL vừa tạo khi việc tạo phòng khám lỗi; lỗi lúc xoá không che lỗi gốc.</summary>
    private async Task DropDatabaseQuietlyAsync(string connectionString, string dbName)
    {
        try
        {
            await using var connection = await OpenServerConnectionAsync(connectionString);
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Không xoá được CSDL {Database} vừa tạo dở", dbName);
        }
    }

    private static async Task<NpgsqlConnection> OpenServerConnectionAsync(string tenantConnectionString)
    {
        var admin = new NpgsqlConnectionStringBuilder(tenantConnectionString) { Database = "postgres" };
        var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}
