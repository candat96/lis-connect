using System.Collections.Concurrent;
using Lis.Data;
using Lis.Data.Ingest;
using Lis.Data.Instruments;

namespace Lis.Server.Tenancy;

/// <summary>Các dịch vụ nghiệp vụ gắn với CSDL của một phòng khám.</summary>
public sealed class TenantServices
{
    public TenantServices(string connectionString, TimeProvider clock)
    {
        ConnectionString = connectionString;
        Db = Db.Create(connectionString);
        Instruments = new InstrumentStore(Db);
        Ingest = new IngestService(Db, clock);
        Learning = new ChannelMapLearning(Db, Instruments, new UnmatchedService(Db, clock));
        OrderQueries = new OrderQueryService(Db);
    }

    public string ConnectionString { get; }

    public Db Db { get; }

    public InstrumentStore Instruments { get; }

    public IngestService Ingest { get; }

    public ChannelMapLearning Learning { get; }

    public OrderQueryService OrderQueries { get; }
}

/// <summary>
/// Từ phòng khám ra CSDL của nó: chuỗi kết nối = mẫu trong cấu hình (<c>Tenants:ConnectionTemplate</c>,
/// có <c>{db}</c>) + <see cref="Clinic.DbName"/>. Mỗi phòng khám giữ một bộ dịch vụ (một pool kết nối).
/// </summary>
public sealed class TenantRegistry(string connectionTemplate, TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, TenantServices> _tenants = new(StringComparer.Ordinal);

    public string ConnectionStringFor(Clinic clinic) => connectionTemplate.Replace("{db}", clinic.DbName, StringComparison.Ordinal);

    public TenantServices For(Clinic clinic) =>
        _tenants.GetOrAdd(clinic.DbName, _ => new TenantServices(ConnectionStringFor(clinic), clock));
}
