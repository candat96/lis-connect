namespace Lis.App.Services;

/// <summary>Tệp đã chọn qua <see cref="IFilePicker"/>: tên hiển thị và toàn bộ nội dung trong bộ nhớ.</summary>
public sealed record PickedFile(string Name, byte[] Content);

/// <summary>
/// Mở hộp thoại chọn tệp. Tách interface để ViewModel (vd <c>CatalogViewModel</c>) kiểm thử
/// được bằng bản giả — <c>IStorageProvider</c> của Avalonia cần một <c>TopLevel</c> (cửa sổ
/// thật), không dựng được trong unit test.
/// </summary>
public interface IFilePicker
{
    /// <summary>Mở hộp thoại chọn một tệp Excel (.xlsx). Trả null khi người dùng bấm Hủy.</summary>
    Task<PickedFile?> PickExcelFileAsync();
}
