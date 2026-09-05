const VALID_DATABASE_NAME = /^[a-z0-9_]{1,63}$/

/**
 * Chặn tên database bất thường trước khi nội suy vào chuỗi kết nối hoặc
 * `CREATE DATABASE`.
 *
 * `tenants.database_name` chỉ ràng buộc `varchar(63) unique`, không có CHECK
 * định dạng ở DB quản trị. Nếu không kiểm ở đây, một giá trị như
 * `"lis_t_a?host=evil.example.com&port=6666"` bị bộ phân giải URL của `pg`
 * hiểu thành tham số kết nối và đẩy kết nối sang host khác — phá vỡ ràng buộc
 * "đúng một Postgres host" và rò rỉ thông tin đăng nhập tới host đó.
 */
export function assertValidDatabaseName(name: string): void {
  if (!VALID_DATABASE_NAME.test(name)) {
    throw new Error(`tên database không hợp lệ: ${name}`)
  }
}
