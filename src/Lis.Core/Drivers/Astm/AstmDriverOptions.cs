namespace Lis.Core.Drivers.Astm;

/// <summary>
/// Phần "biến thể" của từng máy ASTM: mã mẫu nằm ở đâu, mã xét nghiệm ở thành phần thứ mấy, nhận
/// biết QC thế nào, lấy cờ từ đâu, trả lời câu hỏi chỉ định ra sao. Lớp giao thức
/// (<see cref="AstmLink"/>, <see cref="AstmRecord"/>) dùng chung.
/// </summary>
public sealed record AstmDriverOptions
{
    /// <summary>Trường của record O chứa mã mẫu (barcode). Đa số máy: O.3; vài máy (Sysmex): O.4.</summary>
    public int SpecimenIdField { get; init; } = 3;

    /// <summary>Thành phần trong trường mã mẫu (1 = đầu tiên).</summary>
    public int SpecimenIdComponent { get; init; } = 1;

    /// <summary>Thành phần của Universal Test ID (R.3) chứa mã xét nghiệm của máy. Chuẩn: 4 (<c>^^^GLU</c>).</summary>
    public int TestIdComponent { get; init; } = 4;

    /// <summary>Thành phần của R.4 chứa giá trị kết quả (Access: <c>giá trị^diễn giải</c> → 1).</summary>
    public int ValueComponent { get; init; } = 1;

    /// <summary>
    /// Thành phần của R.4 chứa diễn giải định tính (Access: 2, vd <c>Non-React.</c>); null = không có.
    /// Diễn giải được đưa vào cờ máy để người duyệt thấy.
    /// </summary>
    public int? InterpretationComponent { get; init; }

    /// <summary>Chỉ nhận kết quả có R.9 thuộc tập này (rỗng = nhận mọi trạng thái trừ <see cref="SkipStatuses"/>).</summary>
    public IReadOnlyList<string> AcceptStatuses { get; init; } = [];

    /// <summary>Bỏ kết quả có R.9 thuộc tập này (mặc định <c>X</c> = không làm được, <c>I</c> = đang chờ).</summary>
    public IReadOnlyList<string> SkipStatuses { get; init; } = ["X", "I"];

    /// <summary>Gắn cờ trong record C (loại <c>I</c>) ngay sau R vào cờ máy của kết quả đó (Access: có).</summary>
    public bool FlagsFromComment { get; init; }

    /// <summary>O.12 bằng giá trị này → mẫu QC (chuẩn: <c>Q</c>).</summary>
    public string QcActionCode { get; init; } = "Q";

    /// <summary>H.12 (processing ID) bằng giá trị này → cả tin là QC (chuẩn: <c>Q</c>).</summary>
    public string QcProcessingId { get; init; } = "Q";

    /// <summary>Thành phần của Q.3 chứa mã mẫu khi máy hỏi chỉ định (chuẩn: 2, <c>^SID</c>).</summary>
    public int QuerySpecimenComponent { get; init; } = 2;

    /// <summary>Tên LIS ở H.5 khi LIS gửi xuống.</summary>
    public string HostName { get; init; } = "LIS";

    /// <summary>Mã kết thúc L.3 trong tin trả lời có chỉ định (chuẩn <c>N</c>; Access <c>F</c>).</summary>
    public string AnswerTerminationCode { get; init; } = "N";

    /// <summary>Mã kết thúc L.3 khi không có chỉ định (chuẩn <c>I</c>; Access chỉ gửi H + L với <c>F</c>).</summary>
    public string NoOrderTerminationCode { get; init; } = "I";

    /// <summary>O.12 action code trong tin trả lời chỉ định (chuẩn/Access: <c>A</c> = thêm).</summary>
    public string AnswerActionCode { get; init; } = "A";

    /// <summary>O.26 report type trong tin trả lời (chuẩn ví dụ <c>O</c>; Access không dùng → rỗng).</summary>
    public string AnswerReportType { get; init; } = "O";

    /// <summary>Thời gian chờ tầng thấp.</summary>
    public AstmTimeouts Timeouts { get; init; } = new();

    /// <summary>Cấu hình chuẩn LIS2-A2 cho máy chưa có tài liệu riêng.</summary>
    public static AstmDriverOptions Generic { get; } = new();

    /// <summary>
    /// Beckman Coulter Access 2 / UniCel DxI (LIS Vendor Information C03112-AB): mã mẫu O.3, mã XN thành
    /// phần 4 của R.3, R.4 = <c>giá trị^diễn giải</c>, cờ ở record C ngay sau R, chỉ lấy kết quả
    /// <c>F</c>; trả lời query bằng H-P-O-L (L.3 = F), không có chỉ định thì H + <c>L|1|F</c>.
    /// </summary>
    public static AstmDriverOptions Access2 { get; } = new()
    {
        InterpretationComponent = 2,
        AcceptStatuses = ["F", "C"],
        FlagsFromComment = true,
        AnswerTerminationCode = "F",
        NoOrderTerminationCode = "F",
        AnswerReportType = "",
    };
}
