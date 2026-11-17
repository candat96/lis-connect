CREATE TABLE IF NOT EXISTS clinic (
    id uuid PRIMARY KEY,
    code text NOT NULL UNIQUE,
    name text NOT NULL,
    db_name text NOT NULL UNIQUE,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS agent (
    id uuid PRIMARY KEY,
    clinic_id uuid NOT NULL REFERENCES clinic (id),
    name text NOT NULL,
    workstation_code text NOT NULL,
    key_hash text NOT NULL,
    last_seen_at timestamptz NULL,
    last_status jsonb NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (clinic_id, workstation_code)
);
-- Chống ghi trùng khi agent gửi lại một bản tin (mất mạng lúc chờ phản hồi).
CREATE TABLE IF NOT EXISTS agent_reading (
    clinic_id uuid NOT NULL REFERENCES clinic (id),
    reading_key text NOT NULL,
    outcome jsonb NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (clinic_id, reading_key)
);
