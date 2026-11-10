using Lis.Data;
using Lis.Server.Tenancy;
using Npgsql;

namespace Lis.Server;

/// <summary>Lệnh quản trị dòng lệnh của server: thêm phòng khám (tạo + migrate CSDL), thêm agent (in khoá).</summary>
public static class AdminCommands
{
    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] is "clinic" or "agent";

    public static async Task<int> RunAsync(string[] args, MasterStore master, TenantRegistry tenants)
    {
        try
        {
            await master.EnsureSchemaAsync();
            var action = args.Length > 1 ? args[1] : "";
            switch (args[0], action)
            {
                case ("clinic", "add"):
                    return await AddClinicAsync(Option(args, "--code"), Option(args, "--name"), Option(args, "--db"), master, tenants);
                case ("clinic", "list"):
                    foreach (var clinic in await master.ClinicsAsync())
                    {
                        Console.WriteLine($"{clinic.Code}\t{clinic.Name}\tCSDL: {clinic.DbName}");
                    }

                    return 0;
                case ("agent", "add"):
                    return await AddAgentAsync(Option(args, "--clinic"), Option(args, "--name"), Option(args, "--workstation"), master);
                default:
                    Console.Error.WriteLine("Lệnh: clinic add --code --name --db | clinic list | agent add --clinic --name --workstation");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Lỗi: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Thêm phòng khám: tạo CSDL nếu chưa có, migrate, ghi vào master. CSDL có sẵn dữ liệu (chuyển từ bản cài tại chỗ) dùng lại được.</summary>
    public static async Task<int> AddClinicAsync(string code, string name, string dbName, MasterStore master, TenantRegistry tenants)
    {
        if (!dbName.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            Console.Error.WriteLine("Tên CSDL chỉ gồm chữ không dấu, số, gạch dưới");
            return 2;
        }

        var probe = new Clinic(Guid.Empty, code, name, dbName);
        var connectionString = tenants.ConnectionStringFor(probe);

        var admin = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
            exists.Parameters.AddWithValue("name", dbName);
            if (await exists.ExecuteScalarAsync() is null)
            {
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", connection);
                await create.ExecuteNonQueryAsync();
                Console.WriteLine($"Đã tạo CSDL {dbName}.");
            }
        }

        var migration = Migrator.Upgrade(connectionString);
        if (!migration.Ok)
        {
            Console.Error.WriteLine($"Migrate CSDL {dbName} thất bại: {migration.Error}");
            return 1;
        }

        var clinic = await master.AddClinicAsync(code, name, dbName);
        Console.WriteLine($"Đã thêm phòng khám {clinic.Code} ({clinic.Name}) → CSDL {clinic.DbName}.");
        return 0;
    }

    public static async Task<int> AddAgentAsync(string clinicCode, string name, string workstation, MasterStore master)
    {
        var clinic = await master.FindClinicAsync(clinicCode);
        if (clinic is null)
        {
            Console.Error.WriteLine($"Không có phòng khám mã {clinicCode}");
            return 2;
        }

        var (agent, key) = await master.AddAgentAsync(clinic.Id, name, workstation);
        Console.WriteLine($"Đã thêm agent \"{agent.Name}\" (máy trạm {agent.WorkstationCode}) cho phòng khám {clinic.Code}.");
        Console.WriteLine("Khoá agent — CHỈ HIỆN MỘT LẦN, dán vào appsettings.json của agent (Server:AgentKey):");
        Console.WriteLine(key);
        return 0;
    }

    private static string Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length
            ? args[index + 1]
            : throw new ArgumentException($"Thiếu tham số {name}");
    }
}
