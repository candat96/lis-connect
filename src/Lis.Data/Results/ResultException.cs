namespace Lis.Data.Results;

/// <summary>Lý do một thao tác trên kết quả bị từ chối (server web dịch sang mã HTTP).</summary>
public enum ResultFailure
{
    /// <summary>Không có mẫu / dòng chỉ định / kết quả / dịch vụ với id đó.</summary>
    NotFound,

    /// <summary>
    /// Trạng thái hiện tại không cho phép thao tác: mẫu đã duyệt (phải hủy duyệt trước khi sửa),
    /// chưa có kết quả để duyệt, không có bản thay thế để chấp nhận / bỏ.
    /// </summary>
    Conflict,

    /// <summary>Thao tác không áp dụng được cho đối tượng này (vd nhập giá trị cho dịch vụ cha — panel).</summary>
    NotApplicable,
}

/// <summary>
/// Lỗi nghiệp vụ của <see cref="ResultService"/> / <see cref="SampleResultsQuery"/>; <see cref="Exception.Message"/>
/// là câu tiếng Việt hiển thị cho người dùng. Là <see cref="InvalidOperationException"/> như trước nên bên gọi cũ
/// (app máy trạm) bắt kiểu đó vẫn chạy.
/// </summary>
public sealed class ResultException(ResultFailure failure, string message) : InvalidOperationException(message)
{
    public ResultFailure Failure { get; } = failure;
}

/// <summary>
/// Dữ liệu người dùng nhập bị từ chối: giá trị số không đọc được, thiếu lý do sửa / hủy duyệt.
/// Là <see cref="ArgumentException"/> như trước (không đặt tên tham số, nên <see cref="Exception.Message"/>
/// đúng nguyên câu tiếng Việt).
/// </summary>
public sealed class ResultInputException(string message) : ArgumentException(message);
