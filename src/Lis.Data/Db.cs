using Dapper;
using Npgsql;

namespace Lis.Data;

/// <summary>
/// Nguồn kết nối CSDL PostgreSQL dùng chung trong ứng dụng (bọc <see cref="NpgsqlDataSource"/>),
/// kèm tiện ích mở kết nối và chạy một đơn vị công việc trong giao dịch.
/// </summary>
public sealed class Db(NpgsqlDataSource source)
{
    static Db()
    {
        // Cho phép Dapper ánh xạ cột snake_case trên CSDL (vd order_id) sang thuộc tính
        // PascalCase trên C# (OrderId) mà không cần khai Alias thủ công.
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    /// <summary>
    /// Tạo <see cref="Db"/> từ chuỗi kết nối, dùng <see cref="NpgsqlDataSource"/> làm nguồn
    /// kết nối (pool kết nối do Npgsql quản lý).
    /// </summary>
    public static Db Create(string connectionString)
    {
        var dataSource = NpgsqlDataSource.Create(connectionString);
        return new Db(dataSource);
    }

    /// <summary>
    /// Mở một kết nối mới từ nguồn kết nối.
    /// </summary>
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default) =>
        await source.OpenConnectionAsync(ct);

    /// <summary>
    /// Mở kết nối, bắt đầu giao dịch, chạy <paramref name="work"/>; commit nếu thành công,
    /// rollback nếu có ngoại lệ.
    /// </summary>
    public async Task<T> InTransactionAsync<T>(
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work,
        CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            var result = await work(connection, transaction);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch
        {
            try
            {
                // Không truyền ct: bị huỷ thì vẫn phải rollback cho xong.
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception)
            {
                // Rollback hỏng (vd kết nối đã đứt) không được che lỗi gốc: giao dịch dù sao cũng không được commit
                // và máy chủ tự huỷ giao dịch dở khi kết nối đóng. Db không có logger nên bỏ qua.
            }

            throw;
        }
    }
}
