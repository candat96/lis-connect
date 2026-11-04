using Lis.Core.Orders;
using Shouldly;
using Xunit;

namespace Lis.Core.Tests.Orders;

/// <summary>Kiểm tra gom dịch vụ đã chọn thành nhóm mẫu theo loại mẫu bệnh phẩm.</summary>
public class SampleGrouperTests
{
    [Fact]
    public void Group_PerSpecimen_SplitsTubes()
    {
        var edta = Guid.NewGuid();
        var serum = Guid.NewGuid();

        var wbc = Guid.NewGuid();
        var rbc = Guid.NewGuid();
        var glu = Guid.NewGuid();

        IReadOnlyList<SampleGroup> groups = SampleGrouper.Group(
            [
                new OrderedService(wbc, edta),
                new OrderedService(glu, serum),
                new OrderedService(rbc, edta),
            ],
            SampleGrouper.PerSpecimen);

        groups.Count.ShouldBe(2);
        groups[0].SpecimenTypeId.ShouldBe(edta);
        groups[0].ServiceIds.ShouldBe([wbc, rbc]);
        groups[1].SpecimenTypeId.ShouldBe(serum);
        groups[1].ServiceIds.ShouldBe([glu]);
    }

    [Fact]
    public void Group_Single_OneGroup()
    {
        var edta = Guid.NewGuid();
        var serum = Guid.NewGuid();

        var wbc = Guid.NewGuid();
        var glu = Guid.NewGuid();

        IReadOnlyList<SampleGroup> groups = SampleGrouper.Group(
            [
                new OrderedService(wbc, edta),
                new OrderedService(glu, serum),
            ],
            SampleGrouper.Single);

        groups.Count.ShouldBe(1);
        groups[0].SpecimenTypeId.ShouldBe(edta);
        groups[0].ServiceIds.ShouldBe([wbc, glu]);
    }
}
