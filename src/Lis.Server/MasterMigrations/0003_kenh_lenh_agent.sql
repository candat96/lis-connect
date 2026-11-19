-- Kênh lệnh agent: web gửi lệnh (quét cổng, cập nhật, ...) cho agent đã ghép nối; vòng đời mỗi lệnh ghi trong bảng này.
-- Mọi mốc thời gian do server ghi từ TimeProvider (không dùng now() của PostgreSQL).
CREATE TABLE agent_command (
    id uuid PRIMARY KEY,
    agent_id uuid NOT NULL REFERENCES agent(id) ON DELETE CASCADE,
    clinic_id uuid NOT NULL,
    name text NOT NULL,
    args jsonb NULL,
    status text NOT NULL,
    requested_by uuid NULL,
    requested_by_name text NULL,
    requested_at timestamptz NOT NULL,
    started_at timestamptz NULL,
    finished_at timestamptz NULL,
    result jsonb NULL,
    error text NULL
);
CREATE INDEX ix_agent_command_agent_requested ON agent_command (agent_id, requested_at DESC);
CREATE INDEX ix_agent_command_status ON agent_command (status);

-- Cổng COM lần quét cuối của agent (JSON), để web hiện danh sách cổng kể cả khi agent đang ngoại tuyến.
ALTER TABLE agent
    ADD COLUMN last_ports jsonb NULL,
    ADD COLUMN last_ports_at timestamptz NULL;
