-- Khai dịch vụ AU480 chạy được cho CSDL đang dùng (seed-clinic mới đã tự làm bước này).
INSERT INTO instrument_service (instrument_id, service_id)
SELECT i.id, s.id FROM instrument i JOIN service s ON s.code IN ('XN01', 'DV000151', 'DV000152', 'DV000153', 'XN02', 'XN04', 'XN05', 'XN06', 'XN29', 'XN17', 'XN18', 'XN19', 'XN14', 'XN15', 'XN16', 'XN07', 'XN08', 'XN09', 'XN11', 'XN10', 'XN12', 'XN13', 'XN22', 'XN23', 'XN25', 'XN26', 'DV000049', 'DV000050', 'DV000051', 'XN30', 'DV000070', 'DV000066', 'DV000068', 'XN31', 'XN32', 'DV000208', 'XN33', 'XN34', 'XN35', 'DV000210', 'DV000125', 'DV000126', 'DV000074', 'DV000206', 'DV000185')
WHERE i.name = 'Beckman AU480' AND NOT s.is_panel ON CONFLICT DO NOTHING;
