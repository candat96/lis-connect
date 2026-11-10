# Sinh don-vi-tham-chieu.sql: đơn vị + khoảng tham chiếu MẶC ĐỊNH (người lớn) cho phòng khám pkstl.
# BẢN NHÁP — trưởng khoa xét nghiệm duyệt, sửa ở màn Danh mục. Dùng: python -I gen_don_vi_tham_chieu.py <out.sql>
import sys

N = None


def r(lo=N, hi=N, cl=N, ch=N, t=N, sex=N):
    return (sex, lo, hi, cl, ch, t)


def mf(m, f):
    return [r(*m, sex='M'), r(*f, sex='F')]


NEG = [r(t='Âm tính')]
NONE = [r(t='Không thấy')]
HGB = [r(130, 170, 70, 200, sex='M'), r(120, 150, 70, 200, sex='F')]

D = {
    # --- Huyết học: tổng phân tích tế bào máu BC-5380 (XN38) ---
    'XNMD194_01': ('10^9/L', [r(4, 10, 2, 30)]), 'XNMD194_02': ('%', [r(40, 74)]), 'XNMD194_03': ('%', [r(20, 40)]),
    'XNMD194_07': ('10^9/L', [r(2, 7, 0.5)]), 'XNMD194_08': ('10^9/L', [r(0.8, 4)]),
    'XNMD194_12': ('10^12/L', mf((4.2, 5.4), (3.8, 5.0))), 'XNMD194_13': ('g/L', HGB),
    'XNMD194_14': ('%', mf((40, 50), (36, 46))), 'XNMD194_15': ('fL', [r(80, 100)]), 'XNMD194_16': ('pg', [r(27, 33)]),
    'XNMD194_17': ('g/L', [r(320, 360)]), 'XNMD194_19': ('%', [r(11, 16)]), 'XNMD194_27': ('fL', [r(35, 56)]),
    'XNMD194_18': ('10^9/L', [r(150, 400, 50, 1000)]), 'XNMD194_21': ('fL', [r(7, 11)]), 'XNMD194_22': ('fL', [r(9, 17)]),
    'XNMD194_20': ('%', [r(0.1, 0.28)]), 'XNMD194_23': ('%', NONE),
    # CTM 3 thành phần (DV000267)
    'DV000268': ('10^9/L', [r(4, 10, 2, 30)]), 'DV000269': ('10^9/L', [r(0.8, 4)]), 'DV000270': ('10^9/L', [r(0.1, 1.5)]),
    'DV000271': ('10^9/L', [r(2, 7)]), 'DV000272': ('%', [r(20, 40)]), 'DV000273': ('%', [r(3, 15)]), 'DV000274': ('%', [r(50, 70)]),
    'DV000275': ('10^12/L', mf((4.2, 5.4), (3.8, 5.0))), 'DV000276': ('g/L', HGB), 'DV000277': ('%', mf((40, 50), (36, 46))),
    'DV000278': ('fL', [r(80, 100)]), 'DV000279': ('pg', [r(27, 33)]), 'DV000280': ('g/L', [r(320, 360)]),
    'DV000281': ('%', [r(11, 16)]), 'DV000282': ('fL', [r(35, 56)]), 'DV000283': ('10^9/L', [r(150, 400, 50, 1000)]),
    'DV000284': ('fL', [r(7, 11)]), 'DV000285': ('fL', [r(9, 17)]), 'DV000286': ('%', [r(0.1, 0.28)]),
    'DV000287': ('10^9/L', [r(30, 90)]), 'DV000288': ('%', [r(11, 45)]),
    # Đông máu
    'DV000233': ('giây', [r(10, 14)]), 'DV000234': ('%', [r(70, 140)]), 'DV000235': ('', [r(0.8, 1.2, N, 5)]),
    'DV000231': ('giây', [r(26, 40)]), 'DV000232': ('', [r(0.8, 1.2)]), 'XN43': ('giây', [r(14, 21)]), 'XN44': ('g/L', [r(2, 4, 1)]),
    'XN45': ('ng/mL FEU', [r(N, 500)]), 'XN46': ('mm/giờ', [r(t='Nam ≤ 15; Nữ ≤ 20')]),
    'DV000248': ('', [r(N, 1.2)]), 'DV000249': ('', NEG),
    # --- Sinh hoá ---
    'XN01': ('mmol/L', [r(3.9, 6.4, 2.2, 25)]), 'XN02': ('%', [r(4, 6)]), 'XN04': ('U/L', [r(N, 40)]), 'XN05': ('U/L', [r(N, 40)]),
    'XN06': ('U/L', mf((N, 50), (N, 32))), 'XN07': ('mmol/L', [r(2.5, 7.5)]), 'XN08': ('µmol/L', mf((62, 106), (44, 80))),
    'XN09': ('µmol/L', mf((202, 416), (142, 339))), 'XN10': ('mmol/L', [r(N, 1.7)]), 'XN11': ('mmol/L', [r(N, 5.2)]),
    'XN12': ('mmol/L', [r(0.9, N)]), 'XN13': ('mmol/L', [r(N, 3.4)]), 'XN14': ('g/L', [r(66, 83)]), 'XN15': ('g/L', [r(35, 52)]),
    'XN16': ('g/L', [r(24, 38)]), 'XN17': ('µmol/L', [r(N, 17)]), 'XN18': ('µmol/L', [r(N, 5)]), 'XN19': ('µmol/L', [r(N, 12)]),
    'XN21': ('mmol/L', [r(1.12, 1.32)]), 'XN22': ('mmol/L', [r(2.15, 2.55, 1.6, 3.5)]), 'XN23': ('µmol/L', mf((11, 28), (6.6, 26))),
    'XN24': ('ng/mL', mf((30, 400), (13, 150))), 'XN25': ('mmol/L', [r(0.81, 1.45)]), 'XN26': ('mmol/L', [r(0.66, 1.07)]),
    'XN27': ('µmol/L', [r(10.7, 18.4)]), 'XN28': ('µg/dL', [r(t='< 5')]), 'XN29': ('U/L', [r(40, 130)]), 'XN30': ('U/L', [r(28, 100)]),
    'XN31': ('U/L', mf((N, 190), (N, 170))), 'XN32': ('U/L', [r(N, 24)]), 'XN33': ('mg/L', [r(N, 5)]), 'XN34': ('mg/L', [r(N, 5)]),
    'XN35': ('IU/mL', [r(N, 200)]), 'XN36': ('U/g Hb', [r(t='6.4 – 18.7')]),
    'DV000151': ('mmol/L', [r(N, 5.1)]), 'DV000152': ('mmol/L', [r(N, 10.0)]), 'DV000153': ('mmol/L', [r(N, 8.5)]),
    'DV000049': ('mmol/L', [r(135, 145, 120, 160)]), 'DV000050': ('mmol/L', [r(3.5, 5.0, 2.8, 6.2)]), 'DV000051': ('mmol/L', [r(98, 106)]),
    'DV000066': ('U/L', [r(13, 60)]), 'DV000068': ('U/L', [r(13, 60)]), 'DV000070': ('U/L', [r(13, 53)]),
    'DV000074': ('g/L', [r(0.2, 0.4)]), 'DV000124': ('mL/ph/1.73m²', [r(t='≥ 90')]), 'DV000125': ('mmol/L', [r(0.5, 2.2)]),
    'DV000126': ('µmol/L', [r(11, 51)]), 'DV000206': ('mg/L', [r(0.8, 2.2)]), 'DV000208': ('U/L', [r(135, 250)]),
    'DV000209': ('pg/mL', [r(t='15 – 65')]), 'DV000210': ('IU/mL', [r(N, 14)]), 'DV000211': ('ng/mL', [r(t='< 0.04')]),
    # Nước tiểu 10 thông số (XN37)
    'XNNT36_01': ('', NEG), 'XNNT36_02': ('', NEG), 'XNNT36_03': ('', NEG), 'XNNT36_04': ('', NEG),
    'XNNT36_05': ('', [r(1.005, 1.030)]), 'XNNT36_06': ('', [r(5, 8)]), 'XNNT36_07': ('', NEG), 'XNNT36_08': ('', NEG),
    'XNNT36_10': ('', NEG), 'XNNT36_12': ('', [r(t='Bình thường')]),
    'DV000185': ('mg/L', [r(N, 20)]), 'DV000062': ('g/24h', [r(t='< 0.15')]), 'DV000212': ('', NEG),
    # --- Miễn dịch (dải của hãng Beckman Access cho các test chạy Access 2) ---
    'XN61': ('nmol/L', [r(1.34, 2.73)]), 'XN62': ('nmol/L', [r(78.4, 157.4)]), 'XN63': ('pmol/L', [r(3.8, 6.0)]),
    'XN64': ('pmol/L', [r(7.9, 14.4)]), 'XN65': ('µIU/mL', [r(0.38, 5.33)]), 'XN66': ('ng/mL', [r(t='Đánh giá theo tuổi')]),
    'XN67': ('pg/mL', [r(t='Theo chu kỳ: nang 21–251; rụng trứng 38–649; hoàng thể 21–312; mãn kinh < 28; nam 11–44')]),
    'XN70': ('ng/mL', mf((2.64, 13.13), (3.34, 26.72))),
    'XN72': ('mIU/mL', [r(t='Theo chu kỳ: nang 2.1–10.9; rụng trứng 19–103; hoàng thể 1.2–12.9; mãn kinh 10–58; nam 1.2–8.6')]),
    'DV000182': ('mIU/mL', [r(t='Theo chu kỳ: nang 3.9–8.8; rụng trứng 4.5–22.5; hoàng thể 1.8–5.1; mãn kinh 16.7–113.6; nam 1.3–19.3')]),
    'DV000183': ('ng/mL', mf((1.75, 7.81), (N, 0.75))),
    'DV000237': ('ng/mL', [r(t='Theo chu kỳ: nang 0.31–1.52; hoàng thể 5.16–18.56; mãn kinh < 0.78; nam 0.14–2.06')]),
    'XN73': ('ng/mL', [r(N, 8.04)]), 'XN74': ('ng/mL', [r(N, 5)]), 'XN75': ('U/mL', [r(N, 35)]), 'XN76': ('U/mL', [r(N, 35)]),
    'XN77': ('pmol/L', [r(t='Tiền mãn kinh ≤ 70; mãn kinh ≤ 140')]), 'XN78': ('U/mL', [r(N, 31.3)]), 'XN79': ('U/mL', [r(N, 6.9)]),
    'XN80': ('ng/mL', [r(t='≤ 1.5')]), 'XN81': ('ng/mL', [r(5.38, N)]), 'XN82': ('pg/mL', [r(180, 914)]), 'XN83': ('ng/mL', [r(30, 100)]),
    'XN84': ('µg/dL', [r(t='Sáng 6.7 – 22.6; chiều < 10')]), 'XN85': ('pg/mL', [r(7.2, 63.3)]),
    'XN86': ('ng/mL', [r(t='Đánh giá theo nghiệm pháp')]), 'XN87': ('ng/mL', [r(t='Nam < 3; Nữ < 8')]), 'XN88': ('ng/mL', [r(t='Đánh giá theo tuổi')]),
    'XN89': ('U/mL', [r(N, 20)]), 'XN90': ('U/mL', [r(N, 20)]), 'XN91': ('U/mL', [r(N, 20)]), 'XN92': ('U/mL', [r(N, 20)]),
    'XN93': ('U/mL', [r(N, 20)]), 'XN94': ('U/mL', [r(N, 20)]),
    'DV000078': ('µIU/mL', [r(1.9, 23)]),
    'DV000096': ('ng/mL', [r(t='Đánh giá theo tỉ lệ PSA tự do / toàn phần (> 25%: nguy cơ thấp)')]),
    'DV000097': ('ng/mL', [r(N, 4)]), 'DV000134': ('mIU/mL', [r(t='< 5: âm tính; ≥ 25: có thai')]), 'DV000186': ('ng/mL', [r(3.5, 77)]),
    'DV000187': ('IU/mL', [r(N, 115)]), 'DV000244': ('U/mL', [r(N, 17)]), 'DV000245': ('', NEG),
    'DV000253': ('mIU/mL', [r(t='≥ 10: có miễn dịch')]), 'DV000072': ('pg/mL', [r(t='< 125')]),
    'DV000079': ('ng/mL', [r(t='Đánh giá theo tuổi, giới')]), 'DV000121': ('IU/L', [r(t='≤ 1.75')]),
    'XN134': ('g/L', [r(t='0.7 – 4.0')]), 'XN135': ('g/L', [r(t='7 – 16')]), 'XN136': ('g/L', [r(t='0.4 – 2.3')]), 'XN138': ('IU/mL', [r(N, 100)]),
    # Phân, tinh dịch đồ (WHO 2021 — giới hạn dưới)
    'DV000032': ('', [r(6.0, 7.5)]),
    'DV000164': ('', [r(t='Trắng đục, xám')]), 'DV000165': ('mL', [r(1.4, N)]), 'DV000166': ('phút', [r(N, 60)]),
    'DV000167': ('', [r(7.2, N)]), 'DV000168': ('triệu/mL', [r(16, N)]), 'DV000169': ('triệu', [r(39, N)]),
    'DV000170': ('%', [r(30, N)]), 'DV000171': ('%', []), 'DV000172': ('%', []), 'DV000173': ('%', [r(54, N)]),
    'DV000174': ('%', [r(4, N)]),
}

NEGATIVE_CODES = (
    ['DV000052', 'XN95', 'XN96', 'XN97', 'XN98', 'XN99'] + [f'XN{i}' for i in range(100, 130)] + ['XN131', 'XN132', 'XN133', 'XN139', 'XN141']
    + ['DV000176', 'DV000177', 'DV000178', 'DV000179', 'DV000180', 'DV000181']
    + ['DC.XNMD07', 'DV000005', 'DV000116', 'DV000199', 'DV000200', 'DV000213', 'DV000214', 'DV000219', 'DV000226', 'DV000227', 'DV000229']
    + ['XN47', 'XN48', 'XN49', 'XN50', 'XN51', 'XN54', 'XN55', 'XN56', 'XN57', 'XN58', 'XN59']
    + ['DV000251', 'DV000252', 'DV000044', 'DV000045', 'DV000046', 'DV000047', 'DV000048']
    + [f'DV{n:06d}' for n in range(255, 267)]
    + ['DV000215', 'DV000216', 'DV000217', 'DV000218', 'DV000220', 'DV000221', 'DV000222', 'DV000224', 'DV000228', 'DV000230',
       'DV000033', 'DV000034', 'DV000038', 'DV000039']
)
for c in NEGATIVE_CODES:
    D.setdefault(c, ('', NEG))
for c in ['DV000017', 'DV000203', 'DV000204', 'DV000205', 'XN163', 'XN165', 'DV000035', 'DV000036', 'DV000037', 'DV000202']:
    D.setdefault(c, ('', NONE))


def q(v):
    return 'NULL' if v is None else str(v)


def s(v):
    return 'NULL' if v is None else "'" + v.replace("'", "''") + "'"


out = [
    "-- Đơn vị + khoảng tham chiếu MẶC ĐỊNH (người lớn) cho phòng khám pkstl — BẢN NHÁP, trưởng khoa xét nghiệm duyệt, sửa ở màn Danh mục.",
    "-- Sinh bằng gen_don_vi_tham_chieu.py. Chạy lại an toàn: xoá khoảng chung (không theo máy) của các chỉ số này rồi ghi lại.",
    "BEGIN;",
]
for code, (unit, ranges) in D.items():
    if unit:
        out.append(f"UPDATE service SET unit = {s(unit)} WHERE code = {s(code)};")
    out.append(f"DELETE FROM reference_range WHERE instrument_id IS NULL AND service_id IN (SELECT id FROM service WHERE code = {s(code)});")
    for (sex, lo, hi, cl, ch, t) in ranges:
        out.append(
            "INSERT INTO reference_range (service_id, sex, low, high, critical_low, critical_high, text_range) "
            f"SELECT id, {s(sex)}, {q(lo)}, {q(hi)}, {q(cl)}, {q(ch)}, {s(t)} FROM service WHERE code = {s(code)};")
out.append("COMMIT;")
with open(sys.argv[1], 'w', encoding='utf-8') as f:
    f.write("\n".join(out) + "\n")
print(len(D), 'services')
