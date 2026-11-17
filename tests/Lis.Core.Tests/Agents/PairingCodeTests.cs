using Lis.Core.Agents;
using Shouldly;
using Xunit;

namespace Lis.Core.Tests.Agents;

public sealed class PairingCodeTests
{
    private static string NewEncoded(out PairingCode original)
    {
        original = new PairingCode("https://lis.thu.vn/", "pkthu", PairingCode.NewSecret());
        return original.Encode();
    }

    [Fact]
    public void Encode_roi_TryFind_ra_dung_ba_truong()
    {
        var original = new PairingCode("https://lis.thu.vn/", "pkthu", PairingCode.NewSecret());

        PairingCode.TryFind(original.Encode(), out var found).ShouldBeTrue();

        original.ServerUrl.ShouldBe("https://lis.thu.vn");
        found!.ServerUrl.ShouldBe("https://lis.thu.vn");
        found.ClinicCode.ShouldBe(original.ClinicCode);
        found.Secret.ShouldBe(original.Secret);
        original.Encode().ShouldStartWith(PairingCode.Prefix);
    }

    [Fact]
    public void NewSecret_dai_32_ky_tu_va_khong_trung()
    {
        var a = PairingCode.NewSecret();
        a.Length.ShouldBe(32);
        a.ShouldNotBe(PairingCode.NewSecret());
    }

    [Theory]
    [InlineData("LisAgent_{0}.exe")]
    [InlineData("LisAgent_{0} (1).exe")]
    [InlineData("LisAgent_{0}(2).exe")]
    [InlineData("  {0}\r\n")]
    public void TryFind_doc_duoc_trong_ten_tep_trinh_duyet_doi(string template)
    {
        var code = NewEncoded(out var original);

        PairingCode.TryFind(string.Format(template, code), out var found).ShouldBeTrue();

        found!.Secret.ShouldBe(original.Secret);
        found.ClinicCode.ShouldBe("pkthu");
        found.ServerUrl.ShouldBe("https://lis.thu.vn");
    }

    [Fact]
    public void TryFind_tra_false_cho_chuoi_khong_co_ma_hoac_ma_hong()
    {
        var good = NewEncoded(out _);
        var twoLines = Raw($"https://x.vn\n{PairingCode.NewSecret()}");
        var ftp = Raw($"ftp://x\npk\n{PairingCode.NewSecret()}");
        var shortSecret = Raw($"https://x.vn\npk\n{PairingCode.NewSecret()[..31]}");

        foreach (var text in new string?[] { "LisAgent.exe", null, "", "LIS1.!!!", good[..^5], twoLines, ftp, shortSecret })
        {
            PairingCode.TryFind(text, out var found).ShouldBeFalse($"đầu vào: {text}");
            found.ShouldBeNull();
        }
    }

    private static string Raw(string payload) =>
        PairingCode.Prefix + System.Buffers.Text.Base64Url.EncodeToString(System.Text.Encoding.UTF8.GetBytes(payload));

    [Theory]
    [InlineData("lis.thu.vn")]
    [InlineData("https://a.vn/?x=1")]
    [InlineData("https://a.vn/#f")]
    [InlineData("ftp://a.vn")]
    public void Constructor_tu_choi_url_tuong_doi_va_query(string url) =>
        Should.Throw<ArgumentException>(() => new PairingCode(url, "pk", PairingCode.NewSecret()));

    [Fact]
    public void Constructor_tu_choi_ma_phong_kham_rong_va_bi_mat_sai_do_dai()
    {
        Should.Throw<ArgumentException>(() => new PairingCode("https://a.vn", " ", PairingCode.NewSecret()));
        Should.Throw<ArgumentException>(() => new PairingCode("https://a.vn", "pk", "ngan"));
    }

    [Fact]
    public void Ma_cho_url_40_ky_tu_ngan_hon_160_ky_tu()
    {
        var url = "https://lis.phongkham-thu-nghiem.vn".PadRight(40, 'a')[..40];
        url.Length.ShouldBe(40);

        new PairingCode(url, "pkthu-mau", PairingCode.NewSecret()).Encode().Length.ShouldBeLessThan(160);
    }
}
