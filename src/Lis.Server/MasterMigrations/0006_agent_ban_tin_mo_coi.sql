-- Agent báo số bản tin thô còn giữ trên đĩa thuộc máy không còn gán cho nó (máy đã chuyển agent khác hoặc bị xoá):
-- web hiện để quản trị chuyển máy về hoặc xử lý, thay vì kết quả nằm im trên máy trạm.
ALTER TABLE agent ADD COLUMN pending_orphan int NOT NULL DEFAULT 0;
