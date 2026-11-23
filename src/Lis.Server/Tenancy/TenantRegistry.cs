using System.Collections.Concurrent;
using Lis.Data;
using Lis.Data.Auth;
using Lis.Data.Catalog;
using Lis.Data.Ingest;
using Lis.Data.Orders;
using Lis.Data.Instruments;
using Lis.Data.RawMessages;
using Lis.Data.Results;

namespace Lis.Server.Tenancy;

/// <summary>Các dịch vụ nghiệp vụ gắn với CSDL của một phòng khám.</summary>
public sealed class TenantServices
{
    /// <param name="clock">Đồng hồ gốc — chỉ dùng cho <see cref="AuthService"/> (chỉ ghi / so sánh thời điểm UTC, không chia ngày).</param>
    /// <param name="clinicClock">Đồng hồ có "giờ địa phương" là giờ phòng khám — dùng cho mọi dịch vụ chia ngày / tính tuổi
    /// (kết quả nhập tay, máy gửi, gán tay từ Chưa ghép). Thời điểm UTC của nó trùng đồng hồ gốc nên mốc thời gian lưu vẫn đúng.</param>
    /// <param name="log">Nơi <see cref="RawProcessor"/> ghi dòng log (phục hồi, lỗi tạm thời...); không có thì bỏ qua.</param>
    public TenantServices(string connectionString, TimeProvider clock, TimeProvider clinicClock, Action<string>? log = null)
    {
        ConnectionString = connectionString;
        Db = Db.Create(connectionString);
        Instruments = new InstrumentStore(Db);
        Ingest = new IngestService(Db, clinicClock);
        Learning = new ChannelMapLearning(Db, Instruments, new UnmatchedService(Db, clinicClock));
        OrderQueries = new OrderQueryService(Db);
        Orders = new OrderService(Db, clinicClock);
        Users = new UserStore(Db);
        Catalog = new CatalogStore(Db);
        Auth = new AuthService(Db, new PasswordHasher(), clock);
        Results = new ResultService(Db, clinicClock);
        RawMessages = new RawMessageStore(Db, clock);
        RawProcessor = new RawMessageProcessor(Db, Ingest, Learning, clock, log);
    }

    public string ConnectionString { get; }

    public Db Db { get; }

    public InstrumentStore Instruments { get; }

    public IngestService Ingest { get; }

    public ChannelMapLearning Learning { get; }

    public OrderQueryService OrderQueries { get; }

    /// <summary>Tiếp nhận: tìm người bệnh, tạo chỉ định + mẫu + barcode (cùng quy tắc với <c>Lis.App</c>).</summary>
    public OrderService Orders { get; }

    public UserStore Users { get; }

    public CatalogStore Catalog { get; }

    public AuthService Auth { get; }

    /// <summary>Kết quả: đọc danh sách / chi tiết mẫu, nhập, sửa, duyệt, hủy duyệt, chấp nhận / bỏ bản máy gửi lại.</summary>
    public ResultService Results { get; }

    /// <summary>Kho bản tin thô agent gửi lên (lưu, chống trùng, liệt kê, phục hồi, dọn).</summary>
    public RawMessageStore RawMessages { get; }

    /// <summary>Giải mã bản tin thô ở server — mọi kết quả đi qua <see cref="Ingest"/>.</summary>
    public RawMessageProcessor RawProcessor { get; }
}

/// <summary>
/// Từ phòng khám ra CSDL của nó: chuỗi kết nối = mẫu trong cấu hình (<c>Tenants:ConnectionTemplate</c>,
/// có <c>{db}</c>) + <see cref="Clinic.DbName"/>. Mỗi phòng khám giữ một bộ dịch vụ (một pool kết nối).
/// <paramref name="localUtcOffsetMinutes"/>: múi giờ phòng khám (<see cref="ServerOptions.LocalUtcOffsetMinutes"/>) mà phần
/// kết quả dùng để chia "ngày" và tính tuổi người bệnh, không phụ thuộc múi giờ của máy chủ.
/// </summary>
public sealed class TenantRegistry(
    string connectionTemplate, TimeProvider clock, int localUtcOffsetMinutes = ServerOptions.DefaultLocalUtcOffsetMinutes, Action<string>? log = null)
{
    private readonly ConcurrentDictionary<string, TenantServices> _tenants = new(StringComparer.Ordinal);
    private readonly ClinicTimeProvider _clinicClock = new(clock, localUtcOffsetMinutes);

    public string ConnectionStringFor(Clinic clinic) => connectionTemplate.Replace("{db}", clinic.DbName, StringComparison.Ordinal);

    public TenantServices For(Clinic clinic) =>
        _tenants.GetOrAdd(clinic.DbName, _ => new TenantServices(ConnectionStringFor(clinic), clock, _clinicClock, log));

    /// <summary>
    /// Bỏ bộ dịch vụ (và pool kết nối) của một CSDL, vd CSDL tạo dở vừa bị xoá — lần sau dùng lại tên đó sẽ mở kết nối mới
    /// thay vì vấp kết nối cũ đã bị <c>DROP DATABASE … WITH (FORCE)</c> cắt.
    /// </summary>
    public void Forget(Clinic clinic) => _tenants.TryRemove(clinic.DbName, out _);
}
