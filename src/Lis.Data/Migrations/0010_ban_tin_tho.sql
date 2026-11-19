-- 0010_ban_tin_tho.sql
-- Bản tin thô do agent gửi lên (đã cắt theo ranh giới lượt truyền); server giải mã tại đây (thiết kế D).
-- id do agent sinh nên không có DEFAULT: gửi lại cùng id thì ON CONFLICT DO NOTHING (chống trùng).
CREATE TABLE IF NOT EXISTS raw_message (
    id uuid PRIMARY KEY,
    instrument_id uuid NOT NULL REFERENCES instrument (id) ON DELETE CASCADE,
    agent_id uuid NULL,
    seq bigint NOT NULL,
    session text NOT NULL,
    started_at timestamptz NOT NULL,
    completed_at timestamptz NOT NULL,
    stored_at timestamptz NOT NULL DEFAULT now(),
    bytes bytea NOT NULL,
    partial boolean NOT NULL,
    -- Ảnh chụp cấu hình máy ở lần giải mã tự động đầu tiên.
    protocol_family text NULL,
    model text NULL,
    driver_config jsonb NULL,
    decode_status text NOT NULL CHECK (decode_status IN
        ('cho-giai-ma', 'dang-giai-ma', 'da-giai-ma', 'khong-co-ket-qua', 'loi', 'chi-luu', 'can-xem-lai')),
    decode_started_at timestamptz NULL,
    decoder_version text NULL,
    decode_result jsonb NULL,
    decoded_at timestamptz NULL
);
CREATE INDEX IF NOT EXISTS raw_message_instrument_idx ON raw_message (instrument_id, completed_at DESC);
CREATE INDEX IF NOT EXISTS raw_message_status_idx ON raw_message (decode_status);
CREATE INDEX IF NOT EXISTS raw_message_stored_idx ON raw_message (stored_at);

-- Vết của từng lượt giải mã (tự động hoặc giải mã lại), kể cả lượt lỗi.
CREATE TABLE IF NOT EXISTS raw_message_decode (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    raw_message_id uuid NOT NULL REFERENCES raw_message (id) ON DELETE CASCADE,
    kind text NOT NULL CHECK (kind IN ('tu-dong', 'giai-ma-lai')),
    actor_id uuid NULL REFERENCES app_user (id),
    decoder_version text NOT NULL,
    protocol_family text NULL,
    model text NULL,
    driver_config jsonb NULL,
    status text NOT NULL,
    result jsonb NOT NULL,
    at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS raw_message_decode_msg_idx ON raw_message_decode (raw_message_id, at);

-- Khoá chống chèn trùng khi giải mã lại bản tin QC / chưa ghép (IngestService tìm theo khoá này).
CREATE INDEX IF NOT EXISTS qc_result_rawref_idx ON qc_result (instrument_id, raw_ref) WHERE raw_ref LIKE 'ban-tin:%';
CREATE INDEX IF NOT EXISTS unmatched_result_rawref_idx ON unmatched_result (instrument_id, raw_ref) WHERE raw_ref LIKE 'ban-tin:%';
