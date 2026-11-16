using Lis.Core.Results;
using Shouldly;
using Xunit;

namespace Lis.Core.Tests.Results;

/// <summary>
/// Các ca của <c>result-value.spec.ts</c> (bản NestJS chép lại quy tắc của Lis.Core; server NestJS đã bỏ, xem lịch sử git trước commit xoá <c>server/</c>) chạy trên chính
/// logic Lis.Core — để khi server .NET thay NestJS, đọc / làm tròn / tính cờ / quy đổi định tính vẫn cho cùng kết quả.
/// Ca chuẩn hóa số khi chỉ số không khai số lẻ (<c>normalizeNumber</c>) nằm ở đường ghi của Lis.Data và được kiểm
/// qua <c>PUT /api/results/items/{id}</c> trong <c>SamplesEndpointsTests</c>.
/// </summary>
public class NestResultValueParityTests
{
    [Fact]
    public void Doc_so_dau_phay_thap_phan_dau_so_sanh()
    {
        ResultValue.Parse("7.25").ShouldSatisfyAllConditions(v => v.Number.ShouldBe(7.25m), v => v.Comparator.ShouldBeNull(), v => v.IsEmpty.ShouldBeFalse());
        ResultValue.Parse(" 4,8 ").ShouldSatisfyAllConditions(v => v.Number.ShouldBe(4.8m), v => v.Comparator.ShouldBeNull(), v => v.IsEmpty.ShouldBeFalse());
        ResultValue.Parse(">500").ShouldSatisfyAllConditions(v => v.Number.ShouldBe(500m), v => v.Comparator.ShouldBe(">"), v => v.IsEmpty.ShouldBeFalse());
        ResultValue.Parse("<= 0.5").ShouldSatisfyAllConditions(v => v.Number.ShouldBe(0.5m), v => v.Comparator.ShouldBe("<="), v => v.IsEmpty.ShouldBeFalse());
        ResultValue.Parse("-3").ShouldSatisfyAllConditions(v => v.Number.ShouldBe(-3m), v => v.Comparator.ShouldBeNull(), v => v.IsEmpty.ShouldBeFalse());
    }

    [Fact]
    public void Hieu_gia_a_hai_cham_b_va_a_gach_b()
    {
        ResultValue.Parse("1:4").Number.ShouldBe(0.25m);
        ResultValue.Parse("1/160").Number.ShouldBe(1m / 160m);
        ResultValue.Parse("1:0").IsEmpty.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("***.*")]
    [InlineData("**")]
    [InlineData("abc")]
    [InlineData("12..5")]
    [InlineData("1e5")]
    [InlineData("1 000")]
    public void Rong_va_ky_hieu_loi_cua_may_la_khong_co_gia_tri(string? raw)
    {
        ResultValue.Parse(raw).IsEmpty.ShouldBeTrue();
    }

    [Theory]
    [InlineData("4.8", 2, "4.80")]
    [InlineData("12.46", 1, "12.5")]
    [InlineData("7", 0, "7")]
    [InlineData(".5", 1, "0.5")]
    public void Lam_tron_giu_dung_so_le(string raw, int decimals, string expected)
    {
        ValueRounding.Round(raw, decimals).ShouldBe(expected);
    }

    [Theory]
    [InlineData("2.675", 2, "2.68")]
    [InlineData("1.005", 2, "1.01")]
    [InlineData("0.5", 0, "1")]
    [InlineData("-2.5", 0, "-3")]
    [InlineData("9.995", 2, "10.00")]
    public void Lam_tron_nua_ra_xa_so_0_khong_sai_so_thuc(string raw, int decimals, string expected)
    {
        ValueRounding.Round(raw, decimals).ShouldBe(expected);
    }

    [Fact]
    public void Lam_tron_giu_dau_so_sanh_chu_giu_nguyen_khong_so_le_thi_khong_doi()
    {
        ValueRounding.Round(">500.26", 1).ShouldBe(">500.3");
        ValueRounding.Round("4,85", 1).ShouldBe("4.9");
        ValueRounding.Round("Âm tính", 2).ShouldBe("Âm tính");
        ValueRounding.Round("4.8", null).ShouldBe("4.8");
    }

    private static readonly ReferenceRange Range = new(Guid.Empty, Guid.Empty, null, null, null, null, 4m, 10m, 2m, 30m, null);

    [Theory]
    [InlineData("3.9", ResultFlag.Low)]
    [InlineData("4", ResultFlag.None)]
    [InlineData("10", ResultFlag.None)]
    [InlineData("10.1", ResultFlag.High)]
    public void Co_thap_cao_binh_thuong_bien_khong_bat_thuong(string raw, ResultFlag expected)
    {
        FlagCalculator.Compute(ResultValue.Parse(raw), Range).ShouldBe(expected);
    }

    [Theory]
    [InlineData("2", ResultFlag.CriticalLow)]
    [InlineData("30", ResultFlag.CriticalHigh)]
    [InlineData(">30", ResultFlag.CriticalHigh)]
    public void Nguy_kich_thang_cao_thap_tinh_ca_khi_bang_nguong(string raw, ResultFlag expected)
    {
        FlagCalculator.Compute(ResultValue.Parse(raw), Range).ShouldBe(expected);
    }

    [Fact]
    public void Khong_co_khoang_hoac_khong_doc_duoc_so_thi_khong_co()
    {
        FlagCalculator.Compute(ResultValue.Parse("99"), null).ShouldBe(ResultFlag.None);
        FlagCalculator.Compute(ResultValue.Parse("***"), Range).ShouldBe(ResultFlag.None);
        FlagCalculator.Compute(ResultValue.Parse("5"), new ReferenceRange(Guid.Empty, Guid.Empty, null, null, null, null, null, 5.2m, null, null, null))
            .ShouldBe(ResultFlag.None);
    }

    private static readonly Guid ServiceId = Guid.NewGuid();
    private static readonly Guid Machine1 = Guid.NewGuid();
    private static readonly Guid Machine2 = Guid.NewGuid();

    private static readonly QualitativeRule[] Rules =
    [
        new(null, ServiceId, "pos", "Dương tính", ResultFlag.Abnormal),
        new(null, ServiceId, "neg", "Âm tính", null),
        new(Machine1, ServiceId, "POS", "Dương tính (máy 1)", ResultFlag.Abnormal),
    ];

    [Fact]
    public void Dinh_tinh_khong_phan_biet_hoa_thuong_bo_khoang_trang()
    {
        QualitativeMapper.Apply(" Pos ", ServiceId, null, Rules).ShouldBe(("Dương tính", ResultFlag.Abnormal));
        QualitativeMapper.Apply("neg", ServiceId, null, Rules).ShouldBe(("Âm tính", (ResultFlag?)null));
    }

    [Fact]
    public void Dinh_tinh_luat_dung_may_uu_tien_luat_may_khac_khong_ap()
    {
        QualitativeMapper.Apply("pos", ServiceId, Machine1, Rules).Display.ShouldBe("Dương tính (máy 1)");
        QualitativeMapper.Apply("pos", ServiceId, Machine2, Rules).Display.ShouldBe("Dương tính");
    }

    [Fact]
    public void Dinh_tinh_khong_co_luat_khop_thi_giu_nguyen()
    {
        QualitativeMapper.Apply("Nghi ngờ", ServiceId, null, Rules).ShouldBe(("Nghi ngờ", (ResultFlag?)null));
    }

    /// <summary>
    /// Khác biệt đã ghi nhận: số quá lớn so với <see cref="decimal"/> (hơn 28–29 chữ số) — NestJS đọc được (số thực),
    /// Lis.Core coi là không có giá trị nên nhập tay chỉ số số bị từ chối "Giá trị không hợp lệ".
    /// </summary>
    [Fact]
    public void Khac_biet_so_qua_lon_la_khong_co_gia_tri()
    {
        ResultValue.Parse("99999999999999999999999999999999").IsEmpty.ShouldBeTrue();
    }
}
