-- Ghép nối agent: agent có thể được tạo trước khi cài (chưa có khoá), nên key_hash được phép NULL.
ALTER TABLE agent ALTER COLUMN key_hash DROP NOT NULL;
ALTER TABLE agent
    ADD COLUMN paired_at timestamptz NULL,
    ADD COLUMN machine_name text NULL,
    ADD COLUMN agent_version text NULL;

-- Agent cũ đã có khoá coi như đã ghép nối từ lúc được tạo.
UPDATE agent SET paired_at = created_at WHERE key_hash IS NOT NULL;

-- Mã ghép nối dùng một lần, chỉ lưu bản băm SHA-256; hết hạn sau 30 phút (kiểm tra theo TimeProvider của server).
CREATE TABLE agent_pairing (
    id uuid PRIMARY KEY,
    agent_id uuid NOT NULL REFERENCES agent(id) ON DELETE CASCADE,
    secret_hash text NOT NULL UNIQUE,
    expires_at timestamptz NOT NULL,
    used_at timestamptz NULL,
    created_by uuid NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_agent_pairing_agent_id ON agent_pairing (agent_id);
