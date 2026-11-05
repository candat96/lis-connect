-- 0004_unmatched_dismiss.sql
-- Phục vụ màn Chưa ghép (Lis.Data/Ingest/UnmatchedService.cs):
--   * unmatched_result.dismiss_reason: lý do khi người dùng "Bỏ qua" một dòng (bắt buộc).
--     Dòng bỏ qua có resolved_at/resolved_by nhưng resolved_item_id NULL.

-- IF NOT EXISTS: Migrator chạy script ngoài giao dịch, chạy lại sau khi đứt giữa chừng vẫn an toàn.
ALTER TABLE unmatched_result ADD COLUMN IF NOT EXISTS dismiss_reason text NULL;
