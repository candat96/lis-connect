-- 0003_result_instrument_flag.sql
-- Phục vụ nhập/duyệt kết quả (Lis.Data/Results/ResultService.cs):
--   * result.instrument_flag: cờ do máy gửi kèm kết quả (nhập tay để NULL). Có cờ máy thì
--     không tự duyệt (TryAutoApproveAsync).
--   * result_history.action thêm 'bo-thay-the' (bỏ bản thay thế máy gửi lại cho mẫu đã duyệt).
--   * result_history.actor_id cho phép NULL: dòng 'duyet' do tự duyệt và dòng 'nhan' do máy
--     gửi không có người thao tác.

ALTER TABLE result ADD COLUMN instrument_flag text NULL;

ALTER TABLE result_history DROP CONSTRAINT result_history_action_check;
ALTER TABLE result_history ADD CONSTRAINT result_history_action_check
    CHECK (action IN ('nhan', 'sua', 'duyet', 'huy-duyet', 'thay-the', 'bo-thay-the'));

ALTER TABLE result_history ALTER COLUMN actor_id DROP NOT NULL;
