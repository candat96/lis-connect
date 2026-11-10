using System.Globalization;
using Lis.Core.Drivers.Astm;

namespace Lis.Simulator.Messages;

/// <summary>Dựng các record ASTM E1394 (H, P, O, R, Q, L) như máy gửi. Lớp thuần, không IO.</summary>
public static class AstmMessageBuilder
{
    private static readonly AstmDelimiters D = AstmDelimiters.Default;

    /// <summary>
    /// Phiên kết quả: H, P, O (mã mẫu ở O.3), mỗi kết quả một R (mã xét nghiệm là thành phần 4 của
    /// R.3, trạng thái F), L. QC: O.12 = Q và H.12 = Q.
    /// </summary>
    public static IReadOnlyList<string> BuildResultRecords(string barcode, IReadOnlyList<SimResult> results, DateTimeOffset now, bool isQc)
    {
        var stamp = Stamp(now);
        var records = new List<string>
        {
            Header(stamp, isQc),
            "P|1",
        };

        var tests = string.Join(D.Repeat, results.Select(r => $"^^^{D.EscapeValue(r.Code)}"));
        var order = new string[12];
        Array.Fill(order, "");
        order[0] = "O";
        order[1] = "1";
        order[2] = D.EscapeValue(barcode);
        order[4] = tests;
        order[5] = "R";
        order[6] = stamp;
        order[11] = isQc ? "Q" : "N";
        records.Add(string.Join(D.Field, order));

        var seq = 1;
        foreach (var result in results)
        {
            // R|seq|^^^mã|giá trị|đơn vị|khoảng tham chiếu|cờ|bản chất|trạng thái
            records.Add($"R|{seq++.ToString(CultureInfo.InvariantCulture)}|^^^{D.EscapeValue(result.Code)}|{D.EscapeValue(result.Value)}|||||F");
        }

        records.Add("L|1|N");
        return records;
    }

    /// <summary>Câu hỏi chỉ định: H, Q (mã mẫu ở thành phần 2 của Q.3), L.</summary>
    public static IReadOnlyList<string> BuildQueryRecords(string barcode, DateTimeOffset now) =>
    [
        Header(Stamp(now), false),
        $"Q|1|^{D.EscapeValue(barcode)}||||||||||O",
        "L|1|N",
    ];

    private static string Header(string stamp, bool isQc) =>
        $"H|\\^&|||SIM|||||LIS||{(isQc ? 'Q' : 'P')}|1|{stamp}";

    private static string Stamp(DateTimeOffset now) => now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
}
