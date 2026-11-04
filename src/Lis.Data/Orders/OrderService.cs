using Dapper;
using Lis.Core.Orders;
using Lis.Data.Catalog;
using Lis.Data.Facility;
using Npgsql;

namespace Lis.Data.Orders;

/// <summary>
/// Một chỉ định xét nghiệm mới. Người bệnh là <see cref="PatientId"/> (đã có) hoặc
/// <see cref="Patient"/> (nhập mới ngay tại màn Tiếp nhận) — đúng một trong hai phải có giá
/// trị. <see cref="ServiceIds"/> là các dịch vụ/panel đã chọn (mã dịch vụ được gõ + Enter rồi
/// tra ra id ở màn hình, xem <see cref="CatalogStore.FindByCodeAsync"/>).
/// </summary>
public sealed record NewOrder(
    Guid? PatientId,
    NewPatient? Patient,
    IReadOnlyList<Guid> ServiceIds,
    string? OrderingDoctor,
    string? OrderingDept,
    string? Diagnosis);

/// <summary>Một mẫu bệnh phẩm vừa được cấp barcode, kèm tên loại mẫu và tên các dịch vụ dùng chung mẫu này.</summary>
public sealed record CreatedSample(Guid SampleId, string Barcode, string SpecimenName, IReadOnlyList<string> ServiceNames);

/// <summary>Kết quả tạo một chỉ định: chỉ định, người bệnh (mã nếu vừa tạo mới) và các mẫu đã cấp barcode.</summary>
public sealed record OrderCreated(Guid OrderId, Guid PatientId, string PatientCode, IReadOnlyList<CreatedSample> Samples);

/// <summary>
/// Tạo chỉ định xét nghiệm: tạo người bệnh mới nếu cần, tách panel ra chỉ số con, gom mẫu
/// theo loại mẫu bệnh phẩm (hoặc một mẫu duy nhất, theo <c>facility.barcode_mode</c>) và cấp
/// barcode — tất cả trong một giao dịch, để hai máy trạm tạo chỉ định cùng lúc không bao giờ
/// trùng barcode hay mã người bệnh (xem <see cref="NextSeqAsync"/>).
/// </summary>
public sealed class OrderService(Db db, TimeProvider clock)
{
    private const string PatientCodePrefix = "BN";
    private const string PatientCodeDatePattern = "yyMMdd";
    private const int PatientCodeSeqDigits = 4;

    private readonly CatalogStore _catalog = new(db);
    private readonly FacilityStore _facility = new(db);
    private readonly PatientStore _patients = new(db);

    /// <summary>Tìm người bệnh theo mã, họ tên hoặc số điện thoại — dùng cho gợi ý ở màn Tiếp nhận.</summary>
    public Task<IReadOnlyList<PatientHit>> FindPatientsAsync(string query) => _patients.FindAsync(query);

    public async Task<OrderCreated> CreateAsync(NewOrder order, Guid actorId)
    {
        if (order.PatientId is null && order.Patient is null)
        {
            throw new InvalidOperationException("Phải chọn người bệnh có sẵn hoặc nhập người bệnh mới");
        }

        if (order.ServiceIds.Count == 0)
        {
            throw new InvalidOperationException("Chưa chọn dịch vụ nào");
        }

        var facility = await _facility.GetAsync()
            ?? throw new InvalidOperationException("Chưa cài đặt thông tin cơ sở");

        var sampleBarcodeFormat = new BarcodeFormat(
            facility.BarcodePrefix, facility.BarcodeDatePattern, facility.BarcodeSeqDigits, facility.BarcodeMode);

        var allServices = await _catalog.ListServicesAsync(activeOnly: false);
        var servicesById = allServices.ToDictionary(s => s.Id);

        var specimenTypes = await _catalog.SpecimenTypesAsync();
        var specimenNameById = specimenTypes.ToDictionary(s => s.Id, s => s.Name);

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        return await db.InTransactionAsync(async (connection, transaction) =>
        {
            var (patientId, patientCode) = await ResolvePatientAsync(connection, transaction, order, today);

            var orderId = await connection.ExecuteScalarAsync<Guid>(
                """
                INSERT INTO lab_order (patient_id, ordering_doctor, ordering_dept, diagnosis, created_by)
                VALUES (@patientId, @orderingDoctor, @orderingDept, @diagnosis, @actorId)
                RETURNING id
                """,
                new
                {
                    patientId,
                    orderingDoctor = order.OrderingDoctor,
                    orderingDept = order.OrderingDept,
                    diagnosis = order.Diagnosis,
                    actorId,
                },
                transaction);

            var rows = BuildItemRows(order.ServiceIds, servicesById);

            var leafServices = rows
                .Where(r => r.IsLeaf)
                .Select(r => new OrderedService(r.ServiceId, servicesById[r.ServiceId].SpecimenTypeId))
                .ToList();

            var groups = SampleGrouper.Group(leafServices, facility.BarcodeMode);

            var sampleIdByService = new Dictionary<Guid, Guid>();
            var createdSamples = new List<CreatedSample>();

            foreach (var group in groups)
            {
                var seq = await NextSeqAsync(connection, transaction, sampleBarcodeFormat.Prefix, today);
                var barcode = BarcodeComposer.Compose(sampleBarcodeFormat, today, seq);

                var sampleId = await connection.ExecuteScalarAsync<Guid>(
                    """
                    INSERT INTO sample (order_id, barcode, specimen_type_id)
                    VALUES (@orderId, @barcode, @specimenTypeId)
                    RETURNING id
                    """,
                    new { orderId, barcode, specimenTypeId = group.SpecimenTypeId },
                    transaction);

                foreach (var serviceId in group.ServiceIds)
                {
                    sampleIdByService[serviceId] = sampleId;
                }

                createdSamples.Add(new CreatedSample(
                    sampleId,
                    barcode,
                    specimenNameById.GetValueOrDefault(group.SpecimenTypeId, ""),
                    group.ServiceIds.Select(id => servicesById[id].Name).ToList()));
            }

            // Mẫu của dòng cha (panel) chưa có loại mẫu bệnh phẩm riêng — dùng chung mẫu với
            // chỉ số con đầu tiên của nó (các chỉ số con một panel thường cùng loại mẫu).
            var firstLeafByParentIndex = rows
                .Select((row, index) => (row, index))
                .Where(x => x.row.IsLeaf && x.row.ParentIndex is not null)
                .GroupBy(x => x.row.ParentIndex!.Value)
                .ToDictionary(g => g.Key, g => g.First().row.ServiceId);

            var itemIdByIndex = new Dictionary<int, Guid>();

            for (var index = 0; index < rows.Count; index++)
            {
                var row = rows[index];
                var sampleId = row.IsLeaf
                    ? sampleIdByService[row.ServiceId]
                    : sampleIdByService[firstLeafByParentIndex[index]];

                Guid? parentItemId = row.ParentIndex is { } parentIndex ? itemIdByIndex[parentIndex] : null;

                var itemId = await connection.ExecuteScalarAsync<Guid>(
                    """
                    INSERT INTO order_item (order_id, sample_id, service_id, parent_item_id)
                    VALUES (@orderId, @sampleId, @serviceId, @parentItemId)
                    RETURNING id
                    """,
                    new { orderId, sampleId, serviceId = row.ServiceId, parentItemId },
                    transaction);

                itemIdByIndex[index] = itemId;
            }

            return new OrderCreated(orderId, patientId, patientCode, createdSamples);
        });
    }

    private async Task<(Guid PatientId, string PatientCode)> ResolvePatientAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, NewOrder order, DateOnly today)
    {
        if (order.PatientId is { } existingId)
        {
            var code = await connection.ExecuteScalarAsync<string>(
                "SELECT code FROM patient WHERE id = @id", new { id = existingId }, transaction);

            if (code is null)
            {
                throw new InvalidOperationException("Không tìm thấy người bệnh");
            }

            return (existingId, code);
        }

        var patientBarcodeFormat = new BarcodeFormat(PatientCodePrefix, PatientCodeDatePattern, PatientCodeSeqDigits, SampleGrouper.Single);
        var seq = await NextSeqAsync(connection, transaction, PatientCodePrefix, today);
        var patientCode = BarcodeComposer.Compose(patientBarcodeFormat, today, seq);

        var patientId = await _patients.InsertAsync(connection, transaction, patientCode, order.Patient!);
        return (patientId, patientCode);
    }

    /// <summary>
    /// Số thứ tự kế tiếp cho một tiền tố barcode trong ngày — một câu lệnh UPSERT nguyên tử
    /// duy nhất, để hai máy trạm tạo chỉ định cùng lúc (cùng một giây) không bao giờ nhận
    /// được cùng một số.
    /// </summary>
    private static async Task<int> NextSeqAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string prefix, DateOnly day)
    {
        return await connection.ExecuteScalarAsync<int>(
            """
            INSERT INTO barcode_counter (day, prefix, last_seq)
            VALUES (@day, @prefix, 1)
            ON CONFLICT (day, prefix) DO UPDATE SET last_seq = barcode_counter.last_seq + 1
            RETURNING last_seq
            """,
            new { day, prefix },
            transaction);
    }

    /// <summary>Một dòng <c>order_item</c> sẽ tạo: dòng cha (panel hoặc dịch vụ đơn, <see cref="ParentIndex"/> null) hoặc dòng con (chỉ số con của panel).</summary>
    private sealed record ItemRow(Guid ServiceId, bool IsLeaf, int? ParentIndex);

    /// <summary>
    /// Tách các panel đã chọn ra thành dòng cha (<c>parent_item_id = null</c>) + một dòng con
    /// cho mỗi chỉ số con (<c>parent_item_id</c> trỏ về dòng cha). Dịch vụ đơn (không phải
    /// panel) là một dòng, vừa là "cha" (không có con) vừa là lá (được gom mẫu).
    /// </summary>
    private static List<ItemRow> BuildItemRows(IReadOnlyList<Guid> serviceIds, IReadOnlyDictionary<Guid, ServiceInfo> servicesById)
    {
        var rows = new List<ItemRow>();

        foreach (var serviceId in serviceIds)
        {
            if (!servicesById.TryGetValue(serviceId, out var service))
            {
                throw new InvalidOperationException("Dịch vụ không tồn tại trong danh mục");
            }

            if (!service.IsPanel)
            {
                rows.Add(new ItemRow(serviceId, IsLeaf: true, ParentIndex: null));
                continue;
            }

            var parentIndex = rows.Count;
            rows.Add(new ItemRow(serviceId, IsLeaf: false, ParentIndex: null));

            foreach (var childId in service.ChildIds)
            {
                rows.Add(new ItemRow(childId, IsLeaf: true, ParentIndex: parentIndex));
            }
        }

        return rows;
    }
}
