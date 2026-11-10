// Danh mục dịch vụ / chỉ số: bảng cây — gói mở ra các chỉ số con. Quản trị thêm / sửa / xoá được (ServiceModal).
import { PlusOutlined, SearchOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, Button, Card, Empty, Flex, Input, Select, Space, Table, Tag, Typography } from 'antd';
import { useMemo, useState } from 'react';
import { catalogService, qk, type CatalogService } from '@/services';
import { DeleteButton } from '@/shared/ui/DeleteButton';
import { useIsAdmin } from '@/stores/sessionStore';
import { ServiceModal } from './ServiceModal';

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
  const [editing, setEditing] = useState<string | 'new' | null>(null);
  const isAdmin = useIsAdmin();
  const { message } = App.useApp();
  const queryClient = useQueryClient();
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['catalog'] });
  const remove = useMutation({
    mutationFn: (id: string) => catalogService.remove(id),
    onSuccess: async () => { await refresh(); message.success('Đã xoá dịch vụ'); },
    onError: (e) => message.error((e as Error).message, 8),
  });
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

  const leafCount = useMemo(() => rows.reduce((n, s) => n + (s.isPanel ? s.children?.length ?? 0 : 1), 0), [rows]);

  return (
    <div data-screen="danh-muc">
      <div className="page-header">
        <h1 className="page-title">Danh mục dịch vụ và chỉ số</h1>
        <Space size={12}>
          <Typography.Text type="secondary">{rows.length} dịch vụ · {leafCount} chỉ số</Typography.Text>
          {isAdmin && <Button type="primary" icon={<PlusOutlined />} onClick={() => setEditing('new')}>Thêm dịch vụ</Button>}
        </Space>
      </div>

      <Card size="small">
        <Flex vertical gap={12}>
          <Flex gap={8} wrap>
            <Input aria-label="Tìm theo mã hoặc tên" placeholder="Tìm theo mã hoặc tên (gõ không dấu cũng được)" allowClear prefix={<SearchOutlined />}
              style={{ width: 380 }} onChange={(e) => setSearch(e.target.value)} />
            <Select aria-label="Lọc theo nhóm" placeholder="Tất cả nhóm" allowClear style={{ width: 260 }} value={group} onChange={setGroup}
              options={groups.map((g) => ({ value: g, label: g }))} />
          </Flex>
          {services.error && <Alert type="error" showIcon message={(services.error as Error).message} />}
          <Table<Row>
            rowKey="id" size="small" bordered loading={services.isLoading} dataSource={rows} scroll={{ x: 1300 }}
            pagination={{ defaultPageSize: 50, showSizeChanger: true, showTotal: (total) => `${total} dịch vụ` }}
            // Đang tìm thì mở sẵn các gói để thấy chỉ số con khớp.
            expandable={search.trim() ? { expandedRowKeys: rows.filter((r) => r.isPanel).map((r) => r.id) } : undefined}
            locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Không có dịch vụ nào khớp" /> }}
            columns={[
              { title: 'Mã', dataIndex: 'code', width: 168, render: (v: string) => <span className="code">{v}</span> },
              {
                title: 'Tên dịch vụ / chỉ số', dataIndex: 'name', width: 340,
                render: (v: string, row) => (
                  <Space size={8}>
                    <span style={{ fontWeight: row.isPanel ? 700 : undefined }}>{v}</span>
                    {row.isPanel && <Tag color="green" style={{ marginInlineEnd: 0 }}>Gói · {row.childCount} chỉ số</Tag>}
                    {!row.active && <Tag style={{ marginInlineEnd: 0 }}>Ngừng dùng</Tag>}
                  </Space>
                ),
              },
              { title: 'Nhóm', dataIndex: 'groupName', width: 130, ellipsis: true, render: (v: string | null) => v ?? 'Khác' },
              { title: 'Loại mẫu', dataIndex: 'specimenName', width: 170, ellipsis: true, render: (v: string | null) => v ?? '—' },
              { title: 'Kiểu KQ', dataIndex: 'valueType', width: 92, render: (v: string, row) => (row.isPanel ? '' : VALUE_TYPE[v] ?? v) },
              { title: 'Đơn vị', dataIndex: 'unit', width: 92, render: (v: string | null, row) => (row.isPanel ? '' : v ?? '') },
              { title: 'Khoảng tham chiếu', dataIndex: 'rangeText', render: (v: string | null, row) => (row.isPanel ? '' : v ?? '') },
              ...(isAdmin
                ? [{
                  // Ghim phải để nút luôn thấy khi bảng phải cuộn ngang.
                  title: '', width: 116, align: 'right' as const, fixed: 'right' as const,
                  render: (_: unknown, row: Row) => (
                    <Space size={4}>
                      <Button size="small" onClick={() => setEditing(row.id)}>Sửa</Button>
                      <DeleteButton title={`Xoá "${row.name}"?`} description="Chỉ xoá được dịch vụ chưa từng có trong phiếu chỉ định." onConfirm={() => remove.mutateAsync(row.id)} />
                    </Space>
                  ),
                }]
                : []),
            ]}
          />
        </Flex>
      </Card>

      <ServiceModal target={editing} services={services.data ?? []} onClose={() => setEditing(null)}
        onSaved={async () => { setEditing(null); await refresh(); }} />
    </div>
  );
}
