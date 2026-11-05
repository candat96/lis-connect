-- 0005_ingest_flags.sql
-- Giữ cờ máy (vd "*", "H") xuyên suốt đường Chưa ghép / bản chờ thay thế, để một giá trị có cờ
-- máy không bao giờ lọt qua tự duyệt dù đi đường nào:
--   * unmatched_result.instrument_flag: cờ máy của dòng Chưa ghép, mang theo khi "Gán vào mẫu…".
--   * result.pending_instrument_flag: cờ máy của bản chờ thay thế — "Chấp nhận" chuyển cờ này
--     thành instrument_flag chính thức thay vì bỏ mất (xem ResultService.AcceptPendingAsync).

-- IF NOT EXISTS: Migrator chạy script ngoài giao dịch, chạy lại sau khi đứt giữa chừng vẫn an toàn.
ALTER TABLE unmatched_result ADD COLUMN IF NOT EXISTS instrument_flag text NULL;
ALTER TABLE result ADD COLUMN IF NOT EXISTS pending_instrument_flag text NULL;
