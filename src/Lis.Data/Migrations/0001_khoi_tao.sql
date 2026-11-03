-- 0001_khoi_tao.sql
-- Lược đồ CSDL v1 cho LIS Connect (schema public).
-- Khóa chính là uuid, sinh bằng uuidv7() (PostgreSQL 18 có sẵn hàm này).
-- Mọi bảng có cột created_at timestamptz ghi thời điểm tạo dòng (UTC).
-- Giá trị kết quả luôn lưu dạng chuỗi; so ngưỡng dùng numeric (decimal).

CREATE EXTENSION IF NOT EXISTS citext;

-- Người dùng hệ thống (kỹ thuật viên, trưởng khoa, quản trị, tiếp nhận).
CREATE TABLE app_user (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    username citext NOT NULL UNIQUE,
    full_name text NOT NULL,
    title text NULL,
    password_hash text NOT NULL,
    roles text[] NOT NULL DEFAULT '{}',
    active bool NOT NULL DEFAULT true,
    failed_count int NOT NULL DEFAULT 0,
    locked_until timestamptz NULL,
    last_login_at timestamptz NULL,
    his_staff_code text NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

-- Thông tin cơ sở (chỉ một dòng duy nhất, id luôn là 1) và quy tắc sinh barcode mặc định.
CREATE TABLE facility (
    id int PRIMARY KEY CHECK (id = 1),
    name text NOT NULL,
    address text NULL,
    phone text NULL,
    logo bytea NULL,
    barcode_prefix text NOT NULL DEFAULT '9',
    barcode_date_pattern text NOT NULL DEFAULT 'yyMMdd',
    barcode_seq_digits int NOT NULL DEFAULT 3,
    barcode_mode text NOT NULL DEFAULT 'per-specimen' CHECK (barcode_mode IN ('per-specimen', 'single')),
    created_at timestamptz NOT NULL DEFAULT now()
);

-- Máy trạm (PC) trong khoa xét nghiệm.
CREATE TABLE workstation (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    code text NOT NULL UNIQUE,
    name text NOT NULL,
    last_seen_at timestamptz NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

-- Loại mẫu bệnh phẩm (ống EDTA, huyết thanh, nước tiểu, ...).
CREATE TABLE specimen_type (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    code text NOT NULL UNIQUE,
    name text NOT NULL,
    sort_order int NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL DEFAULT now()
);

INSERT INTO specimen_type (code, name, sort_order) VALUES
    ('EDTA', 'Máu EDTA (huyết học)', 1),
    ('SERUM', 'Huyết thanh (hóa sinh, miễn dịch)', 2),
    ('CITRAT', 'Máu citrat (đông máu)', 3),
    ('NUOC-TIEU', 'Nước tiểu', 4),
    ('KHAC', 'Khác', 5);

-- Máy xét nghiệm (instrument) nối với LIS, cấu hình giao thức/kết nối dạng jsonb.
CREATE TABLE instrument (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    name text NOT NULL,
    model text NULL,
    protocol_family text NULL,
    driver_config jsonb NULL,
    connection jsonb NULL,
    workstation_code text NULL,
    active bool NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now()
);

-- Dịch vụ/chỉ số xét nghiệm. Dịch vụ cha (panel) không có specimen_type_id riêng bắt buộc.
CREATE TABLE service (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    code text NOT NULL UNIQUE,
    name text NOT NULL,
    group_name text NULL,
    specimen_type_id uuid NULL REFERENCES specimen_type (id),
    unit text NULL,
    value_type text NOT NULL CHECK (value_type IN ('so', 'dinh-tinh', 'chu')),
    decimals int NULL,
    print_order int NULL,
    is_panel bool NOT NULL DEFAULT false,
    auto_approve bool NOT NULL DEFAULT false,
    active bool NOT NULL DEFAULT true,
    his_code text NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

-- Quan hệ dịch vụ cha (panel) - chỉ số con, có thứ tự in.
CREATE TABLE service_child (
    parent_id uuid NOT NULL REFERENCES service (id),
    child_id uuid NOT NULL REFERENCES service (id),
    sort_order int NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (parent_id, child_id)
);

-- Ánh xạ kênh đo của máy sang chỉ số xét nghiệm. Một kênh của một máy chỉ trỏ tới một chỉ số.
CREATE TABLE instrument_channel (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    instrument_id uuid NOT NULL REFERENCES instrument (id),
    channel_code text NOT NULL,
    service_id uuid NOT NULL REFERENCES service (id),
    decimals int NULL,
    factor numeric NULL,
    keep_comparator bool NOT NULL DEFAULT false,
    send_on_query bool NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (instrument_id, channel_code)
);

-- Quy tắc đổi giá trị định tính thô của máy sang giá trị hiển thị (và cờ cảnh báo nếu có).
CREATE TABLE qualitative_rule (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    instrument_id uuid NULL REFERENCES instrument (id),
    service_id uuid NOT NULL REFERENCES service (id),
    raw_value text NOT NULL,
    display_value text NOT NULL,
    flag text NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (instrument_id, service_id, raw_value)
);

-- Khoảng tham chiếu theo chỉ số, có thể lọc theo giới tính, tuổi (tính theo ngày) và theo máy.
CREATE TABLE reference_range (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    service_id uuid NOT NULL REFERENCES service (id),
    sex text NULL CHECK (sex IN ('M', 'F')),
    age_from_days int NULL,
    age_to_days int NULL,
    instrument_id uuid NULL REFERENCES instrument (id),
    low numeric NULL,
    high numeric NULL,
    critical_low numeric NULL,
    critical_high numeric NULL,
    text_range text NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

-- Người bệnh. source phân biệt bệnh nhân tạo trong LIS hay đồng bộ từ HIS.
CREATE TABLE patient (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    code text NOT NULL UNIQUE,
    full_name text NOT NULL,
    dob date NULL,
    birth_year int NULL,
    sex text NOT NULL CHECK (sex IN ('M', 'F', 'O')),
    address text NULL,
    phone text NULL,
    source text NOT NULL DEFAULT 'lis',
    his_id text NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

-- Chỉ định xét nghiệm (một phiếu chỉ định của một người bệnh).
CREATE TABLE lab_order (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    patient_id uuid NOT NULL REFERENCES patient (id),
    source text NOT NULL DEFAULT 'lis',
    his_order_id text NULL,
    ordering_doctor text NULL,
    ordering_dept text NULL,
    diagnosis text NULL,
    ordered_at timestamptz NOT NULL DEFAULT now(),
    created_by uuid NULL REFERENCES app_user (id),
    created_at timestamptz NOT NULL DEFAULT now()
);

-- Bộ đếm sinh số thứ tự barcode trong ngày, theo từng tiền tố.
CREATE TABLE barcode_counter (
    day date NOT NULL,
    prefix text NOT NULL,
    last_seq int NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (day, prefix)
);

-- Mẫu bệnh phẩm (ống nghiệm) gắn barcode, thuộc một chỉ định. Barcode chỉ gồm chữ số, duy nhất.
CREATE TABLE sample (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    order_id uuid NOT NULL REFERENCES lab_order (id),
    barcode text NOT NULL UNIQUE CHECK (barcode ~ '^[0-9]+$'),
    specimen_type_id uuid NOT NULL REFERENCES specimen_type (id),
    collected_at timestamptz NULL,
    received_at timestamptz NULL,
    approved_at timestamptz NULL,
    approved_by uuid NULL REFERENCES app_user (id),
    created_at timestamptz NOT NULL DEFAULT now()
);

-- Từng dòng dịch vụ trong chỉ định: dịch vụ cha và từng chỉ số con đều là một dòng,
-- liên kết tới mẫu đã gắn barcode của mình.
CREATE TABLE order_item (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    order_id uuid NOT NULL REFERENCES lab_order (id),
    sample_id uuid NOT NULL REFERENCES sample (id),
    service_id uuid NOT NULL REFERENCES service (id),
    parent_item_id uuid NULL REFERENCES order_item (id),
    created_at timestamptz NOT NULL DEFAULT now()
);

-- Kết quả của một dòng chỉ định (1-1 với order_item), gồm giá trị chính thức và giá trị
-- đang chờ thay thế khi máy gửi lại kết quả cho mẫu đã duyệt.
CREATE TABLE result (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    order_item_id uuid NOT NULL UNIQUE REFERENCES order_item (id),
    value text NULL,
    unit text NULL,
    ref_low numeric NULL,
    ref_high numeric NULL,
    ref_text text NULL,
    flag text NULL,
    source text NULL CHECK (source IN ('may', 'tay')),
    instrument_id uuid NULL REFERENCES instrument (id),
    raw_ref text NULL,
    status text NOT NULL DEFAULT 'cho-duyet' CHECK (status IN ('cho-duyet', 'da-duyet')),
    pending_value text NULL,
    pending_instrument_id uuid NULL REFERENCES instrument (id),
    pending_raw_ref text NULL,
    updated_at timestamptz NULL,
    updated_by uuid NULL REFERENCES app_user (id),
    created_at timestamptz NOT NULL DEFAULT now()
);

-- Lịch sử thao tác trên kết quả (nhận, sửa, duyệt, hủy duyệt, thay thế) — phục vụ truy vết.
CREATE TABLE result_history (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    result_id uuid NOT NULL REFERENCES result (id),
    action text NOT NULL CHECK (action IN ('nhan', 'sua', 'duyet', 'huy-duyet', 'thay-the')),
    old_value text NULL,
    new_value text NULL,
    reason text NULL,
    actor_id uuid NOT NULL REFERENCES app_user (id),
    at timestamptz NOT NULL DEFAULT now(),
    created_at timestamptz NOT NULL DEFAULT now()
);

-- Kết quả máy gửi lên nhưng chưa ghép được vào dòng chỉ định nào (sai/thiếu barcode,
-- chưa có chỉ định, hoặc kênh máy chưa được map tới chỉ số).
CREATE TABLE unmatched_result (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    instrument_id uuid NOT NULL REFERENCES instrument (id),
    barcode text NOT NULL,
    channel_code text NOT NULL,
    value text NOT NULL,
    unit text NULL,
    reason text NOT NULL CHECK (reason IN ('khong-co-barcode', 'khong-co-chi-dinh', 'kenh-chua-map')),
    raw_ref text NULL,
    received_at timestamptz NOT NULL DEFAULT now(),
    resolved_at timestamptz NULL,
    resolved_by uuid NULL REFERENCES app_user (id),
    resolved_item_id uuid NULL REFERENCES order_item (id),
    created_at timestamptz NOT NULL DEFAULT now()
);

-- Kết quả nội kiểm/ngoại kiểm (QC) do máy gửi lên, không gắn với bệnh nhân.
CREATE TABLE qc_result (
    id uuid PRIMARY KEY DEFAULT uuidv7(),
    instrument_id uuid NOT NULL REFERENCES instrument (id),
    barcode text NOT NULL,
    channel_code text NOT NULL,
    value text NOT NULL,
    unit text NULL,
    raw_ref text NULL,
    received_at timestamptz NOT NULL DEFAULT now(),
    created_at timestamptz NOT NULL DEFAULT now()
);

-- Cấu hình dạng khóa/giá trị dùng chung cho toàn hệ thống.
CREATE TABLE setting (
    key text PRIMARY KEY,
    value text NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
