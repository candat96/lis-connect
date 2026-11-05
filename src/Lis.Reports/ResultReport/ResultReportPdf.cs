using System.Globalization;
using Lis.Core.Patients;
using Lis.Core.Results;
using Lis.Data.Facility;
using Lis.Data.Results;
using Lis.Reports.Fonts;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;

namespace Lis.Reports.ResultReport;

/// <summary>
/// Dựng PDF phiếu kết quả xét nghiệm, khổ A4 dọc: đầu phiếu (logo/tên/địa chỉ cơ sở), tiêu đề,
/// thông tin người bệnh và phiếu chỉ định, rồi bảng kết quả của từng mẫu — gộp mọi mẫu của cùng
/// một phiếu chỉ định vào một phiếu in (<see cref="ReportData.Sample"/> và
/// <see cref="ReportData.OtherSamplesSameOrder"/>). Mỗi bảng: dịch vụ cha (panel) đứng trước,
/// chỉ số con thụt vào; giá trị bất thường in đậm kèm ↑ (cao/cao nguy hiểm), ↓ (thấp/thấp nguy
/// hiểm) hoặc "*" (định tính bất thường). Cuối phiếu là người duyệt và thời điểm duyệt. Khi còn
/// mẫu nào của phiếu chưa duyệt, dòng "CHƯA DUYỆT — KHÔNG CÓ GIÁ TRỊ TRẢ KẾT QUẢ" in ở cả đầu và
/// cuối phiếu (xem <see cref="ReportData.Approved"/>) — đây là bản chính thức duy nhất được coi
/// là có giá trị trả kết quả khi không có dòng này.
/// </summary>
public static class ResultReportPdf
{
    private const string FontFamily = EmbeddedFontResolver.FamilyName;
    private const string UnapprovedBanner = "CHƯA DUYỆT — KHÔNG CÓ GIÁ TRỊ TRẢ KẾT QUẢ";
    private const string DateTimeFormat = "HH:mm dd/MM/yyyy";

    public static byte[] Render(ReportData d)
    {
        ArgumentNullException.ThrowIfNull(d);

        EmbeddedFontResolver.Register();

        var document = new Document();
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = FontFamily;
        normal.Font.Size = Unit.FromPoint(10);

        var section = document.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = Unit.FromMillimeter(15);
        section.PageSetup.BottomMargin = Unit.FromMillimeter(15);
        section.PageSetup.LeftMargin = Unit.FromMillimeter(20);
        section.PageSetup.RightMargin = Unit.FromMillimeter(20);

        AddHeader(section, d.Facility);
        AddTitle(section);

        if (!d.Approved)
        {
            AddBanner(section);
        }

        AddPatientInfo(section, d);

        foreach (var sample in AllSamples(d))
        {
            AddSampleTable(section, sample);
        }

        AddFooter(section, d);

        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();

        using var stream = new MemoryStream();
        renderer.PdfDocument.Save(stream, closeStream: false);
        return stream.ToArray();
    }

    /// <summary>Mẫu chính rồi các mẫu khác của cùng phiếu chỉ định, theo đúng thứ tự in.</summary>
    private static IEnumerable<SampleResults> AllSamples(ReportData d)
    {
        yield return d.Sample;
        foreach (var other in d.OtherSamplesSameOrder)
        {
            yield return other;
        }
    }

    private static void AddHeader(Section section, FacilityInfo facility)
    {
        if (facility.Logo is { Length: > 0 } logo)
        {
            var image = section.AddImage("base64:" + Convert.ToBase64String(logo));
            image.Width = Unit.FromMillimeter(20);
            image.LockAspectRatio = true;
        }

        var name = section.AddParagraph(facility.Name);
        name.Format.Font.Bold = true;
        name.Format.Font.Size = Unit.FromPoint(13);

        if (!string.IsNullOrWhiteSpace(facility.Address))
        {
            section.AddParagraph(facility.Address);
        }

        section.AddParagraph().Format.SpaceAfter = Unit.FromMillimeter(1);
    }

    private static void AddTitle(Section section)
    {
        var title = section.AddParagraph("PHIẾU KẾT QUẢ XÉT NGHIỆM");
        title.Format.Alignment = ParagraphAlignment.Center;
        title.Format.Font.Bold = true;
        title.Format.Font.Size = Unit.FromPoint(14);
        title.Format.SpaceAfter = Unit.FromMillimeter(2);
    }

    /// <summary>Dòng "CHƯA DUYỆT" — dùng cả ở đầu và cuối phiếu khi phiếu chưa đủ điều kiện trả kết quả.</summary>
    private static void AddBanner(Section section)
    {
        var banner = section.AddParagraph();
        banner.Format.Alignment = ParagraphAlignment.Center;
        banner.Format.SpaceAfter = Unit.FromMillimeter(2);
        var text = banner.AddFormattedText(UnapprovedBanner, TextFormat.Bold);
        text.Font.Color = new Color(200, 0, 0);
    }

    private static void AddPatientInfo(Section section, ReportData d)
    {
        var sample = d.Sample;
        var patient = sample.Patient;

        section.AddParagraph($"Họ tên: {sample.PatientName}   ·   Giới: {SexText(patient.Sex)}   ·   Năm sinh: {BirthText(patient)}");
        section.AddParagraph($"Mã bệnh nhân: {sample.PatientCode}");

        if (!string.IsNullOrWhiteSpace(d.Header.Address))
        {
            section.AddParagraph($"Địa chỉ: {d.Header.Address}");
        }

        if (!string.IsNullOrWhiteSpace(d.Header.OrderingDoctor) || !string.IsNullOrWhiteSpace(d.Header.OrderingDept))
        {
            section.AddParagraph($"Bác sĩ chỉ định: {d.Header.OrderingDoctor ?? "—"}   ·   Khoa chỉ định: {d.Header.OrderingDept ?? "—"}");
        }

        if (!string.IsNullOrWhiteSpace(d.Header.Diagnosis))
        {
            section.AddParagraph($"Chẩn đoán: {d.Header.Diagnosis}");
        }

        var barcodes = string.Join(", ", AllSamples(d).Select(s => s.Barcode));
        var barcodeLine = section.AddParagraph($"Barcode: {barcodes}");
        barcodeLine.Format.SpaceAfter = Unit.FromMillimeter(3);
    }

    private static string SexText(char sex) => sex switch
    {
        'M' => "Nam",
        'F' => "Nữ",
        _ => "Khác",
    };

    private static string BirthText(PatientFacts patient) => patient.Dob is { } dob
        ? dob.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
        : patient.BirthYear is { } year ? year.ToString(CultureInfo.InvariantCulture) : "—";

    /// <summary>
    /// Bảng kết quả của một mẫu: cột Xét nghiệm | Kết quả | Đơn vị | Khoảng tham chiếu. Dòng
    /// panel (cha) không có giá trị, đứng trước các chỉ số con — chỉ số con thụt vào bằng
    /// <c>LeftIndent</c> trên đoạn văn của ô tên.
    /// </summary>
    private static void AddSampleTable(Section section, SampleResults sample)
    {
        var header = section.AddParagraph($"Mẫu {sample.Barcode}");
        header.Format.Font.Bold = true;
        header.Format.SpaceBefore = Unit.FromMillimeter(2);
        header.Format.SpaceAfter = Unit.FromMillimeter(1);

        var table = section.AddTable();
        table.Borders.Width = 0.5;
        table.Borders.Color = new Color(150, 150, 150);

        table.AddColumn(Unit.FromMillimeter(70));
        table.AddColumn(Unit.FromMillimeter(30));
        table.AddColumn(Unit.FromMillimeter(25));
        table.AddColumn(Unit.FromMillimeter(45));

        var head = table.AddRow();
        head.Shading.Color = new Color(230, 230, 230);
        AddHeaderCell(head.Cells[0], "Xét nghiệm");
        AddHeaderCell(head.Cells[1], "Kết quả");
        AddHeaderCell(head.Cells[2], "Đơn vị");
        AddHeaderCell(head.Cells[3], "Khoảng tham chiếu");

        foreach (var row in sample.Rows)
        {
            AddResultRow(table, row);
        }
    }

    private static void AddResultRow(Table table, ResultRow row)
    {
        var tableRow = table.AddRow();
        var namePara = tableRow.Cells[0].AddParagraph();
        if (row.Depth > 0)
        {
            namePara.Format.LeftIndent = Unit.FromMillimeter(4);
        }

        if (row.IsPanelHeader)
        {
            namePara.AddFormattedText(row.ServiceName, TextFormat.Bold);
            tableRow.Cells[1].AddParagraph();
            tableRow.Cells[2].AddParagraph();
            tableRow.Cells[3].AddParagraph();
            return;
        }

        namePara.AddText(row.ServiceName);

        AddValue(tableRow.Cells[1].AddParagraph(), row.Value, row.Flag);
        tableRow.Cells[2].AddParagraph(row.Unit ?? "");
        tableRow.Cells[3].AddParagraph(row.RefText ?? "");
    }

    private static void AddHeaderCell(Cell cell, string text) =>
        cell.AddParagraph(text).Format.Font.Bold = true;

    /// <summary>Giá trị bình thường in thường; bất thường in đậm kèm ↑/↓/"*" ngay sau giá trị.</summary>
    private static void AddValue(Paragraph paragraph, string? value, ResultFlag flag)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var arrow = Arrow(flag);
        if (arrow is null)
        {
            paragraph.AddText(value);
            return;
        }

        paragraph.AddFormattedText($"{value} {arrow}", TextFormat.Bold);
    }

    private static string? Arrow(ResultFlag flag) => flag switch
    {
        ResultFlag.High or ResultFlag.CriticalHigh => "↑",
        ResultFlag.Low or ResultFlag.CriticalLow => "↓",
        ResultFlag.Abnormal => "*",
        _ => null,
    };

    private static void AddFooter(Section section, ReportData d)
    {
        var spacer = section.AddParagraph();
        spacer.Format.SpaceBefore = Unit.FromMillimeter(4);

        if (d.Approved)
        {
            var approvedAt = d.Sample.ApprovedAt is { } at
                ? TimeZoneInfo.ConvertTime(at, TimeZoneInfo.Local).ToString(DateTimeFormat, CultureInfo.InvariantCulture)
                : "—";
            section.AddParagraph($"Người duyệt: {d.ApproverName ?? "—"}   ·   Thời điểm duyệt: {approvedAt}");
        }
        else
        {
            AddBanner(section);
        }

        var printed = TimeZoneInfo.ConvertTime(d.PrintedAt, TimeZoneInfo.Local).ToString(DateTimeFormat, CultureInfo.InvariantCulture);
        var printedPara = section.AddParagraph($"In lúc: {printed}");
        printedPara.Format.Font.Size = Unit.FromPoint(8);
    }
}
