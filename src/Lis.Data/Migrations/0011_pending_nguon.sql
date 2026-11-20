-- Nguồn và giờ của bản chờ thay: lý do (máy gửi lại / giải mã lại / gán từ Chưa ghép ...),
-- giờ tạo bản chờ thay và giờ máy đo (completed_at của bản tin / giờ nhận).
ALTER TABLE result ADD COLUMN pending_reason text NULL;
ALTER TABLE result ADD COLUMN pending_at timestamptz NULL;
ALTER TABLE result ADD COLUMN pending_measured_at timestamptz NULL;
