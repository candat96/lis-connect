-- 0008_instrument_service.sql
-- Dịch vụ (chỉ số lá) mà một máy chạy được, khai TRƯỚC khi biết mã kênh máy gửi — dùng cho máy
-- mà mã kênh do phòng xét nghiệm tự đặt (vd số test AU480): phần mềm đối chiếu barcode để tự
-- tìm mã kênh nào là dịch vụ nào trong danh sách này rồi tự tạo instrument_channel.
CREATE TABLE IF NOT EXISTS instrument_service (
    instrument_id uuid NOT NULL REFERENCES instrument (id) ON DELETE CASCADE,
    service_id uuid NOT NULL REFERENCES service (id),
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (instrument_id, service_id)
);
