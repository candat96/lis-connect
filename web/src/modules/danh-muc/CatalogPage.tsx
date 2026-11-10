// Danh mục dịch vụ / chỉ số: bảng cây — gói mở ra các chỉ số con. Đợt này chỉ xem; sửa danh mục vẫn ở app Windows.
import { useMemo, useState } from 'react';
import { Alert, Card, Empty, Input, Select, Space, Table, Tag, Typography } from 'antd';
import { useQuery } from '@tanstack/react-query';
import { catalogService, qk, type CatalogService } from '@/services';
import { tokens } from '@/app/theme/tokens';

// Dòng của bảng cây: chỉ số lá không có `children` (mảng rỗng sẽ làm antd hiện nút mở vô nghĩa).
type Row = Omit<CatalogService, 'children'> & { children?: Row[]; childCount: number };
const toRow = (s: CatalogService, children = s.children): Row => ({
  // childCount luôn là tổng số chỉ số của gói, kể cả khi đang lọc chỉ còn vài dòng con.
  ...s, childCount: s.children.length, children: children.length > 0 ? children.map((c) => toRow(c)) : undefined,
});

const VALUE_TYPE: Record<string, string> = { so: 'Số', 'dinh-tinh': 'Định tính', chu: 'Chữ' };

// Bỏ dấu để tìm "bach cau" ra "Bạch cầu".
const fold = (text: string) => text.normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/đ/g, 'd').replace(/Đ/g, 'D').toLowerCase();

export default function CatalogPage() {
  const [search, setSearch] = useState('');
  const [group, setGroup] = useState<string | undefined>();
  const services = useQuery({ queryKey: qk.catalog, queryFn: catalogService.services, staleTime: 60_000 });

  const groups = useMemo(() => [...new Set((services.data ?? []).map((s) => s.groupName ?? 'Khác'))], [services.data]);

  // Gói khớp khi chính nó hoặc một chỉ số con khớp; khi lọc theo chữ thì chỉ giữ các con khớp (trừ khi chính gói khớp).
  const rows = useMemo(() => {
    const needle = fold(search.trim());
    const matches = (s: CatalogService) => needle === '' || fold(s.code).includes(needle) || fold(s.name).includes(needle);
    return (services.data ?? [])
      .filter((s) => !group || (s.groupName ?? 'Khác') === group)
      .flatMap((s): Row[] => {
        if (matches(s)) return [toRow(s)];
        const children = s.children.filter(matches);
        return children.length > 0 ? [toRow(s, children)] : [];
      });
  }, [services.data, search, group]);

  const leafCount = useMemo(() => rows.reduce((n, s) => n + (s.isPanel ? s.childCount : 1), 0), [rows]);

  return (
    <Card data-screen="danh-muc" size="small" title="Danh mục dịch vụ và chỉ số" style={{ height: '100%', display: 'flex', flexDirection: 'column' }}
      styles={{ body: { flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', gap: tokens.space.sm } }}>
      <Space wrap>
        <Input.Search aria-label="Tìm theo mã hoặc tên" placeholder="Tìm theo mã hoặc tên (gõ không dấu cũng được)" allowClear style={{ width: 340 }}
          onChange={(e) => setSearch(e.target.value)} />
        <Select aria-label="Lọc theo nhóm" placeholder="Tất cả nhóm" allowClear style={{ width: 240 }} value={group} onChange={setGroup}
          options={groups.map((g) => ({ value: g, label: g }))} />
        <Typography.Text type="secondary">{rows.length} dịch vụ · {leafCount} chỉ số</Typography.Text>
      </Space>
      {services.error && <Alert type="error" showIcon message={(services.error as Error).message} />}
      <div style={{ flex: 1, minHeight: 0, overflow: 'auto' }}>
        <Table<Row>
          rowKey="id" size="small" bordered sticky pagination={false} loading={services.isLoading} dataSource={rows}
          // Đang tìm thì mở sẵn các gói để thấy chỉ số con khớp.
          expandable={{ childrenColumnName: 'children', ...(search.trim() ? { expandedRowKeys: rows.filter((r) => r.isPanel).map((r) => r.id) } : {}) }}
          locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Không có dịch vụ nào khớp" /> }}
          columns={[
            { title: 'Mã', dataIndex: 'code', width: 190, render: (v: string) => <span className="code" style={{ fontWeight: 700 }}>{v}</span> },
            {
              title: 'Tên dịch vụ / chỉ số', dataIndex: 'name',
              render: (v: string, row) => (
                <Space size={6}>
                  <span style={{ fontWeight: row.isPanel ? 700 : undefined }}>{v}</span>
                  {row.isPanel && <Tag color="blue" style={{ marginInlineEnd: 0 }}>Gói · {row.childCount} chỉ số</Tag>}
                  {!row.active && <Tag style={{ marginInlineEnd: 0 }}>Ngừng dùng</Tag>}
                </Space>
              ),
            },
            { title: 'Nhóm', dataIndex: 'groupName', width: 170, ellipsis: true, render: (v: string | null) => v ?? 'Khác' },
            { title: 'Loại mẫu', dataIndex: 'specimenName', width: 190, ellipsis: true, render: (v: string | null) => v ?? '—' },
            { title: 'Kiểu KQ', dataIndex: 'valueType', width: 84, render: (v: string, row) => (row.isPanel ? '' : VALUE_TYPE[v] ?? v) },
            { title: 'Đơn vị', dataIndex: 'unit', width: 100, render: (v: string | null, row) => (row.isPanel ? '' : v ?? '') },
            { title: 'Khoảng tham chiếu', dataIndex: 'rangeText', width: 300, ellipsis: true, render: (v: string | null, row) => (row.isPanel ? '' : v ?? '') },
          ]}
        />
      </div>
    </Card>
  );
}
