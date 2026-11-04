namespace Lis.Reports.Printing;

/// <summary>
/// Gửi một PDF (tem hoặc phiếu) ra máy in. Tên máy in lấy từ <c>setting</c> (<c>printer.label</c>,
/// <c>printer.report</c>); <c>null</c> nghĩa là dùng máy in mặc định của hệ thống.
/// </summary>
public interface IPrintService
{
    Task PrintAsync(byte[] pdf, string? printerName, CancellationToken ct = default);

    /// <summary>Tên các máy in đã cài trên máy này — rỗng nếu hệ điều hành không liệt kê được (macOS/Linux).</summary>
    IReadOnlyList<string> Printers();
}
