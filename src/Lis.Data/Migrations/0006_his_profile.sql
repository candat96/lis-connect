-- 0006_his_profile.sql
-- Cấu hình kết nối HIS (Task 14) — đợt 1 chỉ lưu cấu hình + thử kết nối, chưa đồng bộ thật.
-- Một dòng duy nhất (id luôn là 1), giống bảng facility.
-- secret_protected: khóa API hoặc mật khẩu đăng nhập HIS, mã hóa như mật khẩu CSDL (Task 2),
-- lưu base64 của bản đã mã hóa bằng ISecretProtector — không bao giờ lưu chữ rõ.

CREATE TABLE IF NOT EXISTS his_profile (
    id int PRIMARY KEY CHECK (id = 1),
    enabled bool NOT NULL DEFAULT false,
    name text NOT NULL DEFAULT '',
    base_url text NOT NULL DEFAULT '',
    auth_type text NOT NULL DEFAULT 'none' CHECK (auth_type IN ('none', 'api-key', 'basic', 'bearer-login')),
    api_key_header text NULL,
    secret_protected text NULL,
    username text NULL,
    login_path text NULL,
    login_body_template text NULL,
    token_json_path text NULL,
    token_ttl_minutes int NULL,
    orders_mode text NULL CHECK (orders_mode IN ('pull', 'push')),
    orders_path text NULL,
    poll_seconds int NULL,
    results_path text NULL,
    staff_path text NULL,
    push_listen_port int NULL,
    push_api_key_hash text NULL,
    push_workstation_code text NULL,
    updated_at timestamptz NULL,
    updated_by uuid NULL REFERENCES app_user (id)
);
