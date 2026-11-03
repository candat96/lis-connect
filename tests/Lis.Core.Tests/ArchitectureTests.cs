using System.Reflection;
using Shouldly;
using Xunit;

namespace Lis.Core.Tests;

/// <summary>
/// Kiểm tra ràng buộc kiến trúc: Lis.Core là lớp lõi, không được phụ thuộc
/// vào Lis.Data, Lis.Reports hay Lis.App (chỉ chiều ngược lại mới hợp lệ).
/// </summary>
public class ArchitectureTests
{
    [Fact]
    public void Core_DoesNotReferenceDataReportsOrApp()
    {
        var coreAssembly = typeof(Lis.Core.Marker).Assembly;

        var cacTenKhongDuocPhuThuoc = new[] { "Lis.Data", "Lis.Reports", "Lis.App" };

        var tenCacAssemblyDuocThamChieu = coreAssembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .ToArray();

        var coThamChieuBiCam = tenCacAssemblyDuocThamChieu.Any(ten => cacTenKhongDuocPhuThuoc.Contains(ten));

        coThamChieuBiCam.ShouldBeFalse();
    }
}
