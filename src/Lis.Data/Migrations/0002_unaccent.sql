-- 0002_unaccent.sql
-- Bật extension unaccent, dùng để tìm người bệnh theo họ tên không phân biệt dấu/hoa thường
-- (OrderService.FindPatientsAsync — xem Lis.Data/Orders/PatientStore.cs).

CREATE EXTENSION IF NOT EXISTS unaccent;
