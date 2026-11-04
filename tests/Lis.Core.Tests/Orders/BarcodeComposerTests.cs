using Lis.Core.Orders;
using Shouldly;
using Xunit;

namespace Lis.Core.Tests.Orders;

/// <summary>Kiểm tra sinh chuỗi barcode từ tiền tố + ngày + số thứ tự, và tràn số thứ tự trong ngày.</summary>
public class BarcodeComposerTests
{
    [Fact]
    public void Compose_Default_Is10Digits()
    {
        var format = new BarcodeFormat("9", "yyMMdd", 3, SampleGrouper.PerSpecimen);

        var barcode = BarcodeComposer.Compose(format, new DateOnly(2026, 11, 3), 1);

        barcode.ShouldBe("9261103001");
        barcode.Length.ShouldBe(10);
        format.Length.ShouldBe(10);
    }

    [Fact]
    public void Compose_SeqOverflow_Throws()
    {
        var format = new BarcodeFormat("9", "yyMMdd", 3, SampleGrouper.PerSpecimen);

        var exception = Should.Throw<InvalidOperationException>(() =>
            BarcodeComposer.Compose(format, new DateOnly(2026, 11, 3), 1000));

        exception.Message.ShouldBe("Hết số barcode trong ngày — tăng số chữ số thứ tự trong Cài đặt");
    }
}
