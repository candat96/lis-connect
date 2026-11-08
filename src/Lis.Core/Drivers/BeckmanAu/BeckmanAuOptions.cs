namespace Lis.Core.Drivers.BeckmanAu;

/// <summary>Cách trả lời khi mẫu máy hỏi không có chỉ định trên LIS (docs/drivers/beckman-au.md mục 5.1).</summary>
public enum AuNoOrderReply
{
    /// <summary><c>SE</c> "response stop": máy không đăng ký mẫu, hỏi tiếp mẫu sau (mặc định).</summary>
    ResponseStop,

    /// <summary><c>S␣</c> không có mục xét nghiệm: máy đăng ký mẫu "không chỉ định", không chạy.</summary>
    EmptyRequisition,
}

/// <summary>
/// Hồ sơ cấu hình online của một máy họ AU. Bố cục bản tin AU là cột cố định và <b>thay đổi theo cài
/// đặt trên máy</b> (Menu List &gt; System &gt; Online — tab Set Up / Protocol / Format Configuration /
/// Online Test No., và [Requisition Format]) — mọi offset tính từ hồ sơ này, không cứng số
/// (docs/drivers/beckman-au.md mục 1, 8.2). Giá trị sai một trường là lệch toàn bộ cột, nên driver
/// từ chối cả bản tin khi độ dài không khớp thay vì đoán.
/// </summary>
public sealed record BeckmanAuOptions
{
    // ---- Tab Protocol ----

    /// <summary>Class B: bên nhận trả ACK/NAK sau mỗi bản tin (cả hai chiều). Class A (mặc định nhà máy): không ACK/NAK.</summary>
    public bool ClassB { get; init; }

    /// <summary>Có byte BCC (XOR từ mã phân biệt đến hết mã kết thúc, 1 byte nhị phân) sau mã kết thúc. Mặc định nhà máy: tắt.</summary>
    public bool Bcc { get; init; }

    /// <summary>Start code 1st (01h–1Fh), mặc định STX. Không hỗ trợ Start/End code 2nd.</summary>
    public byte StartCode { get; init; } = 0x02;

    /// <summary>End code 1st (01h–1Fh), mặc định ETX. ETB (17h) luôn được nhận là kết thúc khối giữa.</summary>
    public byte EndCode { get; init; } = 0x03;

    /// <summary>Text Length 256/512/1024 (mặc định 1024) — dùng khi chia khối bản tin <c>S␣</c> LIS gửi.</summary>
    public int TextLength { get; init; } = 1024;

    /// <summary>Retry Class B khi máy NAK bản tin LIS gửi (0–3, mặc định 1).</summary>
    public int Retry { get; init; } = 1;

    /// <summary>"Device No." bật: bản tin máy gửi (<c>R*</c>, <c>D*</c>) có System No. 2 chữ số sau mã phân biệt. Bản tin <c>S*</c> không có.</summary>
    public bool SystemNo { get; init; }

    // ---- Tab Format Configuration / Requisition Format ----

    /// <summary>Số chữ số Rack No.: 0 (không dùng), 4 hoặc 5.</summary>
    public int RackDigits { get; init; } = 4;

    /// <summary>Có Cup position (2) — thường đi cùng Rack No.</summary>
    public bool CupPosition { get; init; } = true;

    /// <summary>Độ dài trường Sample ID (4–26, căn phải đệm dấu cách). Phải ≥ độ dài barcode LIS in.</summary>
    public int SampleIdLength { get; init; } = 10;

    /// <summary>Có Sex (1) ở phần biến đổi khối đầu của <c>D␣</c>/<c>S␣</c>.</summary>
    public bool Sex { get; init; }

    /// <summary>Có Year age (3).</summary>
    public bool YearAge { get; init; }

    /// <summary>Có Month age (2).</summary>
    public bool MonthAge { get; init; }

    /// <summary>Độ dài các trường Patient information 1–6 đang bật (mỗi trường 1–20), theo thứ tự; rỗng = không có.</summary>
    public IReadOnlyList<int> PatientInfoLengths { get; init; } = [];

    /// <summary>Có Loading Time (14, YYYYMMDDHHMMSS) — chỉ AU5800; AU480 để false.</summary>
    public bool LoadingTime { get; init; }

    /// <summary>Tổng số byte thông tin thuốc thử (lot/bottle) trong mỗi mục kết quả; 0 = "Reagent Inf." tắt. Độ dài từng ô phải đo trên máy thật.</summary>
    public int ReagentInfoLength { get; init; }

    /// <summary>Online Test No. Digit: 2 (01–99) hoặc 3 (001–120).</summary>
    public int TestNoDigits { get; init; } = 2;

    /// <summary>Result Digit: 6 hoặc 9.</summary>
    public int ResultDigits { get; init; } = 6;

    /// <summary>No. of Data Marks: 2 loại → trường cờ 2 ký tự (mỗi cờ 1 ký tự đầu); 4 loại → 8 ký tự (4 cờ × 2).</summary>
    public int FlagLength { get; init; } = 2;

    /// <summary>"Dilution Inf." bật: mỗi mã XN trong <c>S␣</c> kèm Diluent type 1 ký tự. <c>SH</c>/<c>Sh</c> luôn có.</summary>
    public bool DilutionInfo { get; init; }

    /// <summary>
    /// Số chữ số Control No. ở đầu phần biến đổi của <c>DQ</c> (2/3). Vị trí trường này <b>suy luận</b> —
    /// tài liệu không vẽ bố cục <c>DQ</c>; phải đối chiếu log thô.
    /// </summary>
    public int ControlNoDigits { get; init; } = 2;

    // ---- Hành vi LIS ----

    /// <summary>Trả lời khi không có chỉ định.</summary>
    public AuNoOrderReply NoOrderReply { get; init; } = AuNoOrderReply.ResponseStop;

    /// <summary>T1: máy chờ trả lời <c>S␣</c> sau khi gửi xong <c>R␣</c> (mặc định màn hình 20 × 100 ms). Trả lời muộn hơn → ghi chú cảnh báo.</summary>
    public TimeSpan AnswerTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Bỏ khung nhận dở nếu quá thời gian này (T2 ~1,5 s ở 9600 bps; để rộng).</summary>
    public TimeSpan FrameTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Bỏ kết quả nhiều khối nếu không thấy khối <c>E</c> trong thời gian này.</summary>
    public TimeSpan MultiBlockTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>T5: khoảng tối thiểu giữa hai bản tin LIS gửi liên tiếp ở Class A (khi trả lời nhiều khối).</summary>
    public TimeSpan ClassAInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Độ dài phần cố định đến hết Sample ID (sau mã phân biệt, không kể System No.).</summary>
    internal int HeadLength => RackDigits + (CupPosition ? 2 : 0) + 1 + 4 + SampleIdLength;

    internal int ItemLength => TestNoDigits + ReagentInfoLength + ResultDigits + FlagLength;

    internal int PatientBlockLength =>
        (Sex ? 1 : 0) + (YearAge ? 3 : 0) + (MonthAge ? 2 : 0) + PatientInfoLengths.Sum();

    /// <summary>
    /// AU480 theo AU680/AU480 Online Specification V8 (2010): mặc định nhà máy Class A, BCC tắt,
    /// không Device No.; Rack 4 chữ số + cup; Sample ID 10; không giới/tuổi/thông tin BN/thuốc thử;
    /// test No. 2 chữ số, kết quả 6, cờ 2 loại. <b>Phải đọc lại trên màn Online của máy</b> (đặc biệt
    /// Sample ID Digits, Online Test No. Digit, Result Digit, No. of Data Marks, Class, BCC) rồi chỉnh.
    /// Cổng: mặc định 9600 bps, <b>7 bit</b>, None, 1 stop.
    /// </summary>
    public static BeckmanAuOptions Au480 { get; } = new();

    /// <summary>Kiểm tra giá trị hợp lệ theo đặc tả; ném <see cref="ArgumentException"/> nếu sai.</summary>
    public void Validate()
    {
        if (RackDigits is not (0 or 4 or 5)) throw new ArgumentException("Rack No. phải 0, 4 hoặc 5 chữ số");
        if (SampleIdLength is < 4 or > 26) throw new ArgumentException("Sample ID phải 4–26 ký tự");
        if (TestNoDigits is not (2 or 3)) throw new ArgumentException("Online Test No. phải 2 hoặc 3 chữ số");
        if (ResultDigits is not (6 or 9)) throw new ArgumentException("Result Digit phải 6 hoặc 9");
        if (FlagLength is not (2 or 8)) throw new ArgumentException("Trường cờ phải 2 hoặc 8 ký tự");
        if (ControlNoDigits is not (2 or 3)) throw new ArgumentException("Control No. phải 2 hoặc 3 chữ số");
        if (PatientInfoLengths.Count > 6 || PatientInfoLengths.Any(l => l is < 1 or > 20))
            throw new ArgumentException("Tối đa 6 trường thông tin BN, mỗi trường 1–20 ký tự");
        if (TextLength is not (256 or 512 or 1024)) throw new ArgumentException("Text Length phải 256/512/1024");
        if (Retry is < 0 or > 3) throw new ArgumentException("Retry phải 0–3");
        if (ReagentInfoLength < 0) throw new ArgumentException("Độ dài thông tin thuốc thử không âm");
    }
}
