// Máy & map kênh: trái là các máy kèm trạng thái agent báo về, phải là bảng map kênh của máy đang chọn.
// Đợt này chỉ xem; sửa map, cấu hình kết nối vẫn ở app Windows.
import { useEffect, useState } from 'react';
import { Alert, Card, Descriptions, Empty, Input, Space, Table, Tag, Typography } from 'antd';
import { useQuery } from '@tanstack/react-query';
import dayjs from 'dayjs';
import { instrumentService, qk, type Channel, type Instrument } from '@/services';
import { StatusTag } from '@/shared/ui/StatusTag';
import { tokens } from '@/app/theme/tokens';

const PROTOCOL: Record<string, string> = { 'hl7-mllp': 'HL7 MLLP', 'astm-e1394': 'ASTM E1394', 'beckman-au': 'Beckman AU' };

/** Trạng thái hiển thị của một máy: theo agent báo về; agent im lặng hoặc chưa gán thì nói rõ. */
function linkState(instrument: Instrument): string {
  if (!instrument.agent) return 'no-agent';
  if (!instrument.agent.online) return 'offline';
  return instrument.status?.state ?? 'NotRunning';
}

export default function InstrumentsPage() {
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [filter, setFilter] = useState('');
  const instruments = useQuery({ queryKey: qk.instruments, queryFn: instrumentService.list, refetchInterval: 10_000 });
  const channels = useQuery({ queryKey: qk.channels(selectedId ?? ''), queryFn: () => instrumentService.channels(selectedId!), enabled: selectedId !== null, refetchInterval: 10_000 });

  useEffect(() => {
    if (!selectedId && instruments.data?.length) setSelectedId(instruments.data[0].id);
  }, [instruments.data, selectedId]);

  const selected = instruments.data?.find((i) => i.id === selectedId);
  const needle = filter.trim().toLowerCase();
  const rows = (channels.data ?? []).filter((c) =>
    needle === '' || (c.channelCode ?? '').toLowerCase().includes(needle) || c.serviceCode.toLowerCase().includes(needle) || c.serviceName.toLowerCase().includes(needle));

  return (
    <div data-screen="may" style={{ height: '100%', display: 'grid', gridTemplateColumns: 'minmax(420px, 30%) minmax(0, 1fr)', gap: tokens.space.md }}>
      <Card size="small" title="Máy xét nghiệm" style={{ minHeight: 0, overflow: 'auto' }}>
        {instruments.error && <Alert type="error" showIcon message={(instruments.error as Error).message} style={{ marginBottom: tokens.space.sm }} />}
        <Table<Instrument>
          rowKey="id" size="small" bordered pagination={false} loading={instruments.isLoading} dataSource={instruments.data ?? []}
          rowClassName={(row) => (row.id === selectedId ? 'lookup-row-active' : '')}
          onRow={(row) => ({ onClick: () => setSelectedId(row.id), style: { cursor: 'pointer' } })}
          columns={[
            {
              title: 'Máy', dataIndex: 'name',
              render: (v: string, row) => (
                <div>
                  <div style={{ fontWeight: 700 }}>{v}</div>
                  <Typography.Text type="secondary">{PROTOCOL[row.protocolFamily ?? ''] ?? row.protocolFamily ?? 'Chưa chọn giao thức'} · {row.channelCount} kênh</Typography.Text>
                </div>
              ),
            },
            { title: 'Kết nối', width: 196, render: (_, row) => <StatusTag domain="link" value={linkState(row)} /> },
          ]}
        />
      </Card>

      <Card size="small" title={selected ? `Map kênh — ${selected.name}` : 'Map kênh'} style={{ display: 'flex', flexDirection: 'column', minHeight: 0 }}
        styles={{ body: { flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', gap: tokens.space.sm } }}>
        {!selected && <Empty description="Chọn một máy ở danh sách bên trái" style={{ margin: 'auto' }} />}
        {selected && (
          <>
            <Descriptions size="small" column={3} bordered
              items={[
                { key: 'conn', label: 'Kết nối', children: selected.connectionText ?? 'Chưa cấu hình' },
                { key: 'ws', label: 'Máy trạm', children: selected.workstationCode ?? 'Chưa gán' },
                { key: 'agent', label: 'Agent', children: selected.agent ? `${selected.agent.name}${selected.agent.lastSeenAt ? ` · báo về ${dayjs(selected.agent.lastSeenAt).format('DD/MM HH:mm:ss')}` : ' · chưa từng báo về'}` : 'Chưa có agent cho máy trạm này' },
                { key: 'state', label: 'Trạng thái', children: <Space size={6}><StatusTag domain="link" value={linkState(selected)} /><span>{selected.status?.text ?? ''}</span></Space>, span: 2 },
                { key: 'last', label: 'Kết quả gần nhất', children: selected.status?.lastResultAt ? dayjs(selected.status.lastResultAt).format('DD/MM/YYYY HH:mm:ss') : '—' },
              ]} />
            {selected.status && selected.status.pendingToRetry > 0 && (
              <Alert type="warning" showIcon message={`Agent đang giữ ${selected.status.pendingToRetry} bản tin chưa gửi được lên máy chủ — sẽ tự gửi lại.`} />
            )}
            {selected.pendingServiceCount > 0 && (
              <Alert type="info" showIcon
                message={`${selected.pendingServiceCount} chỉ số đã khai là máy này chạy nhưng chưa biết mã kênh. Khi máy gửi kết quả, hệ thống đối chiếu barcode để tự dò; khớp ở 2 mẫu trở lên thì tự map.`} />
            )}
            <Space>
              <Input.Search aria-label="Lọc theo mã kênh hoặc chỉ số" placeholder="Lọc theo mã kênh / chỉ số" allowClear style={{ width: 300 }} onChange={(e) => setFilter(e.target.value)} />
              <Typography.Text type="secondary">{selected.channelCount} kênh đã map{selected.pendingServiceCount > 0 ? ` · ${selected.pendingServiceCount} chờ dò` : ''}</Typography.Text>
            </Space>
            {channels.error && <Alert type="error" showIcon message={(channels.error as Error).message} />}
            <div style={{ flex: 1, minHeight: 0, overflow: 'auto' }}>
              <Table<Channel>
                rowKey={(row) => row.id ?? `cho-${row.serviceCode}`} size="small" bordered sticky pagination={false} loading={channels.isLoading} dataSource={rows}
                locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Máy này chưa có kênh nào" /> }}
                columns={[
                  {
                    title: 'Mã kênh máy', dataIndex: 'channelCode', width: 150,
                    render: (v: string | null) => (v === null ? <Tag style={{ marginInlineEnd: 0 }}>Chờ dò</Tag> : <span className="code" style={{ fontWeight: 700 }}>{v}</span>),
                  },
                  { title: 'Mã chỉ số', dataIndex: 'serviceCode', width: 140, render: (v: string) => <span className="code">{v}</span> },
                  { title: 'Tên chỉ số', dataIndex: 'serviceName' },
                  { title: 'Số lẻ', dataIndex: 'decimals', width: 70, align: 'center', render: (v: number | null) => v ?? '—' },
                  { title: 'Hệ số', dataIndex: 'factor', width: 80, align: 'center', render: (v: number | null) => v ?? '—' },
                  { title: 'Giữ dấu <>', dataIndex: 'keepComparator', width: 96, align: 'center', render: (v: boolean) => (v ? 'Có' : '—') },
                  { title: 'Gửi khi máy hỏi', dataIndex: 'sendOnQuery', width: 124, align: 'center', render: (v: boolean) => (v ? 'Có' : '—') },
                ]}
              />
            </div>
          </>
        )}
      </Card>
    </div>
  );
}
