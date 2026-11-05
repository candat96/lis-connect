-- 0007_pending_flag.sql
-- (0006 dành cho his_profile ở Task 14 — xem docs/superpowers/plans — nên dùng số kế tiếp.)
-- Giữ cờ ĐÃ TÍNH (ResultFlag, vd "Abnormal") của bản chờ thay thế, không chỉ cờ máy gắn:
--   result.pending_flag: cờ tính lúc ghi bản chờ thay thế (R22, qua ResultWriter.ComputeAsync).
-- Cần thiết vì với chỉ số định tính, pending_value lưu CHỮ HIỂN THỊ ("Dương tính"), không phải
-- giá trị thô của máy ("pos") — lúc "Chấp nhận" không thể chạy lại QualitativeMapper trên chữ
-- hiển thị để tìm lại cờ (tra theo raw_value sẽ không khớp), nên phải lưu sẵn cờ đã tính.

-- IF NOT EXISTS: Migrator chạy script ngoài giao dịch, chạy lại sau khi đứt giữa chừng vẫn an toàn.
ALTER TABLE result ADD COLUMN IF NOT EXISTS pending_flag text NULL;
