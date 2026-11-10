-- 0009_agent_log.sql
-- Log do agent (máy tính cạnh máy xét nghiệm) đẩy lên server để xem trên web: byte đi/về với máy
-- (hex), sự kiện kết nối, và cảnh báo của chính agent (instrument_id NULL). Bản đầy đủ vẫn nằm
-- trên đĩa agent; ở đây chỉ giữ ngắn hạn (server tự xoá dòng quá 14 ngày).
CREATE TABLE IF NOT EXISTS agent_log (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    agent_id uuid NOT NULL,
    instrument_id uuid NULL REFERENCES instrument (id) ON DELETE CASCADE,
    at timestamptz NOT NULL,
    direction text NOT NULL CHECK (direction IN ('in', 'out', 'event', 'agent')),
    session text NULL,
    hex text NULL,
    note text NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS agent_log_at_idx ON agent_log (at DESC, id DESC);
CREATE INDEX IF NOT EXISTS agent_log_instrument_idx ON agent_log (instrument_id, at DESC);
