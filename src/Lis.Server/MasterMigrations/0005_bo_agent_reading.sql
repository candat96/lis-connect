-- 0005_bo_agent_reading.sql
-- Server nhận bản tin thô của agent (raw_message ở CSDL phòng khám, chống trùng bằng id do agent sinh),
-- không còn nhận kết quả đã giải mã qua /agent/v1/readings nên bảng chống trùng cũ không còn dùng.
DROP TABLE IF EXISTS agent_reading;
