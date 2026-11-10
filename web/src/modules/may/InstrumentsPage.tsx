// Máy & map kênh: cài đặt máy xét nghiệm (giao thức, kết nối, agent phụ trách) và map mã kênh máy → chỉ số.
// Trái là các máy kèm trạng thái agent báo về, phải là bảng map kênh của máy đang chọn. Chỉ Quản trị được sửa.
import { EditOutlined, FileSearchOutlined, PlusOutlined, SearchOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, Button, Card, Descriptions, Empty, Flex, Input, Space, Table, Tag, Typography } from 'antd';
import dayjs from 'dayjs';
import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { instrumentService, qk, type Channel, type Instrument, type UnmappedChannel } from '@/services';
import { DeleteButton } from '@/shared/ui/DeleteButton';
import { StatusTag } from '@/shared/ui/StatusTag';
import { useIsAdmin } from '@/stores/sessionStore';
import { ChannelModal, type ChannelDraft } from './ChannelModal';
import { InstrumentModal } from './InstrumentModal';

export const PROTOCOL: Record<string, string> = { 'hl7-mllp': 'HL7 MLLP', 'astm-e1394': 'ASTM E1394', 'beckman-au': 'Beckman AU' };

/** Trạng thái hiển thị của một máy: theo agent báo về; agent im lặng hoặc chưa gán thì nói rõ. */
function linkState(instrument: Instrument): string {
  if (!instrument.agent) return 'no-agent';
  if (!instrument.agent.online) return 'offline';
  return instrument.status?.state ?? 'NotRunning';
}

export default function InstrumentsPage() {
  const isAdmin = useIsAdmin();
  const { message } = App.useApp();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [filter, setFilter] = useState('');
  const [instrumentModal, setInstrumentModal] = useState<Instrument | 'new' | null>(null);
  const [channelDraft, setChannelDraft] = useState<ChannelDraft | null>(null);

  const instruments = useQuery({ queryKey: qk.instruments, queryFn: instrumentService.list, refetchInterval: 10_000 });
  const channels = useQuery({ queryKey: qk.channels(selectedId ?? ''), queryFn: () => instrumentService.channels(selectedId!), enabled: selectedId !== null, refetchInterval: 10_000 });
  const unmapped = useQuery({ queryKey: qk.unmapped(selectedId ?? ''), queryFn: () => instrumentService.unmapped(selectedId!), enabled: selectedId !== null, refetchInterval: 10_000 });

  useEffect(() => {
    const list = instruments.data;
    if (!list) return;
    if (!selectedId || !list.some((i) => i.id === selectedId)) setSelectedId(list[0]?.id ?? null);
  }, [instruments.data, selectedId]);

  const refresh = () => queryClient.invalidateQueries({ queryKey: qk.instruments });
  const onError = (e: unknown) => message.error((e as Error).message);

  const removeInstrument = useMutation({
    mutationFn: (instrument: Instrument) => instrumentService.remove(instrument.id),
    onSuccess: async () => { await refresh(); message.success('Đã xoá máy'); },
    onError,
  });
  const removeChannel = useMutation({
    mutationFn: (channel: Channel) => channel.id
      ? instrumentService.removeChannel(selectedId!, channel.id)
      : instrumentService.undeclare(selectedId!, channel.serviceCode),
    onSuccess: refresh,
    onError,
  });

  const selected = instruments.data?.find((i) => i.id === selectedId);
  const needle = filter.trim().toLowerCase();
  const rows = (channels.data ?? []).filter((c) =>
    needle === '' || (c.channelCode ?? '').toLowerCase().includes(needle) || c.serviceCode.toLowerCase().includes(needle) || c.serviceName.toLowerCase().includes(needle));

  return (
    <div data-screen="may">
      <div className="page-header">
        <h1 className="page-title">Máy xét nghiệm và map kênh</h1>
        {isAdmin && <Button type="primary" icon={<PlusOutlined />} onClick={() => setInstrumentModal('new')}>Thêm máy</Button>}
      </div>

      <div className="split-page" style={{ gridTemplateColumns: 'minmax(340px, 26%) minmax(0, 1fr)' }}>
        <Card size="small" className="split-pane">
          {instruments.error && <Alert type="error" showIcon message={(instruments.error as Error).message} style={{ marginBottom: 12 }} />}
          <Table<Instrument>
            rowKey="id" size="small" bordered pagination={false} loading={instruments.isLoading} dataSource={instruments.data ?? []}
            locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Chưa có máy nào" /> }}
            rowClassName={(row) => `row-clickable ${row.id === selectedId ? 'row-active' : ''}`}
            onRow={(row) => ({ onClick: () => setSelectedId(row.id) })}
            columns={[
              {
                title: 'Máy', dataIndex: 'name',
                render: (v: string, row) => (
                  <div>
                    <div style={{ fontWeight: 700 }}>{v} {!row.active && <Tag style={{ marginInlineEnd: 0 }}>Ngừng dùng</Tag>}</div>
                    <Typography.Text type="secondary" style={{ fontSize: 13 }}>
                      {PROTOCOL[row.protocolFamily ?? ''] ?? row.protocolFamily ?? 'Chưa chọn giao thức'} · {row.channelCount} kênh
                    </Typography.Text>
                  </div>
                ),
              },
              { title: 'Kết nối', width: 176, render: (_, row) => <StatusTag domain="link" value={linkState(row)} /> },
            ]}
          />
        </Card>

        <Card size="small" className="split-pane" title={selected ? selected.name : 'Máy xét nghiệm'}
          extra={selected && (
            <Space size={6}>
              <Button size="small" icon={<FileSearchOutlined />} onClick={() => navigate(`/log-agent?instrumentId=${selected.id}`)}>Xem log</Button>
              {isAdmin && <Button size="small" icon={<EditOutlined />} onClick={() => setInstrumentModal(selected)}>Sửa máy</Button>}
              {isAdmin && (
                <DeleteButton title={`Xoá máy "${selected.name}"?`} description="Xoá luôn bảng map kênh của máy. Máy đã có kết quả thì không xoá được."
                  onConfirm={() => removeInstrument.mutateAsync(selected)} />
              )}
            </Space>
          )}>
          {!selected && <Empty description="Chọn một máy ở danh sách bên trái" style={{ margin: '48px 0' }} />}
          {selected && (
            <Flex vertical gap={12}>
              <Descriptions size="small" column={2} bordered
                items={[
                  { key: 'proto', label: 'Giao thức', children: PROTOCOL[selected.protocolFamily ?? ''] ?? 'Chưa chọn' },
                  { key: 'conn', label: 'Kết nối', children: selected.connectionText ?? 'Chưa cấu hình' },
                  { key: 'agent', label: 'Agent', children: selected.agent ? `${selected.agent.name} (${selected.workstationCode})` : 'Chưa gán agent' },
                  { key: 'state', label: 'Trạng thái', children: <Space size={8}><StatusTag domain="link" value={linkState(selected)} /><span>{selected.status?.text ?? ''}</span></Space> },
                  { key: 'last', label: 'Kết quả gần nhất', children: selected.status?.lastResultAt ? dayjs(selected.status.lastResultAt).format('DD/MM/YYYY HH:mm:ss') : '—' },
                  { key: 'seen', label: 'Agent báo về', children: selected.agent?.lastSeenAt ? dayjs(selected.agent.lastSeenAt).format('DD/MM/YYYY HH:mm:ss') : '—' },
                ]} />

              {selected.status && selected.status.pendingToRetry > 0 && (
                <Alert type="warning" showIcon message={`Agent đang giữ ${selected.status.pendingToRetry} bản tin chưa gửi được lên máy chủ — sẽ tự gửi lại.`} />
              )}

              {(unmapped.data?.length ?? 0) > 0 && (
                <UnmappedTable rows={unmapped.data!} canEdit={isAdmin}
                  onMap={(row) => setChannelDraft({ channelCode: row.channelCode, serviceCode: '', decimals: null, factor: null, keepComparator: false, sendOnQuery: true })} />
              )}

              {selected.pendingServiceCount > 0 && (
                <Alert type="info" showIcon
                  message={`${selected.pendingServiceCount} chỉ số đã khai là máy này chạy nhưng chưa biết mã kênh ("Chờ dò"). Khi máy gửi kết quả, hệ thống đối chiếu barcode để tự dò; khớp ở 2 mẫu trở lên thì tự map.`} />
              )}

              <Flex gap={12} align="center" wrap>
                <Input aria-label="Lọc theo mã kênh hoặc chỉ số" placeholder="Lọc theo mã kênh / chỉ số" allowClear prefix={<SearchOutlined />} style={{ width: 300 }}
                  onChange={(e) => setFilter(e.target.value)} />
                <Typography.Text type="secondary">{selected.channelCount} kênh đã map{selected.pendingServiceCount > 0 ? ` · ${selected.pendingServiceCount} chờ dò` : ''}</Typography.Text>
                <div style={{ flex: 1 }} />
                {isAdmin && (
                  <Button icon={<PlusOutlined />} onClick={() => setChannelDraft({ channelCode: '', serviceCode: '', decimals: null, factor: null, keepComparator: false, sendOnQuery: true })}>
                    Thêm kênh
                  </Button>
                )}
              </Flex>
              {channels.error && <Alert type="error" showIcon message={(channels.error as Error).message} />}
              <Table<Channel>
                rowKey={(row) => row.id ?? `cho-${row.serviceCode}`} size="small" bordered loading={channels.isLoading} dataSource={rows} scroll={{ x: 900 }}
                pagination={{ defaultPageSize: 50, showSizeChanger: true, hideOnSinglePage: true }}
                locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Máy này chưa có kênh nào" /> }}
                columns={[
                  {
                    title: 'Mã kênh máy', dataIndex: 'channelCode', width: 124,
                    render: (v: string | null) => (v === null ? <Tag color="warning" style={{ marginInlineEnd: 0 }}>Chờ dò</Tag> : <span className="code">{v}</span>),
                  },
                  { title: 'Mã chỉ số', dataIndex: 'serviceCode', width: 128, render: (v: string) => <span className="code" style={{ fontWeight: 500 }}>{v}</span> },
                  { title: 'Tên chỉ số', dataIndex: 'serviceName' },
                  { title: 'Số lẻ', dataIndex: 'decimals', width: 64, align: 'center', render: (v: number | null) => v ?? '—' },
                  { title: 'Hệ số', dataIndex: 'factor', width: 64, align: 'center', render: (v: number | null) => v ?? '—' },
                  { title: 'Giữ dấu <>', dataIndex: 'keepComparator', width: 96, align: 'center', render: (v: boolean) => (v ? 'Có' : '—') },
                  { title: 'Gửi khi hỏi', dataIndex: 'sendOnQuery', width: 100, align: 'center', render: (v: boolean) => (v ? 'Có' : '—') },
                  ...(isAdmin
                    ? [{
                      // Ghim phải để nút luôn thấy khi bảng phải cuộn ngang.
                      title: '', width: 140, align: 'right' as const, fixed: 'right' as const,
                      render: (_: unknown, row: Channel) => (
                        <Space size={4}>
                          <Button size="small" onClick={() => setChannelDraft({
                            id: row.id ?? undefined, channelCode: row.channelCode ?? '', serviceCode: row.serviceCode,
                            decimals: row.decimals, factor: row.factor, keepComparator: row.keepComparator, sendOnQuery: row.sendOnQuery,
                          })}>{row.id ? 'Sửa' : 'Nhập mã'}</Button>
                          <DeleteButton
                            title={row.id ? `Xoá kênh ${row.channelCode}?` : `Bỏ "${row.serviceName}" khỏi danh sách máy chạy?`}
                            description={row.id ? 'Kết quả máy gửi theo mã kênh này sẽ không vào phiếu nữa.' : undefined}
                            onConfirm={() => removeChannel.mutateAsync(row)} />
                        </Space>
                      ),
                    }]
                    : []),
                ]}
              />
            </Flex>
          )}
        </Card>
      </div>

      <InstrumentModal target={instrumentModal} onClose={() => setInstrumentModal(null)}
        onSaved={async (id) => { setInstrumentModal(null); await refresh(); if (id) setSelectedId(id); }} />
      {selected && (
        <ChannelModal instrumentId={selected.id} draft={channelDraft} onClose={() => setChannelDraft(null)}
          onSaved={async () => { setChannelDraft(null); await refresh(); }} />
      )}
    </div>
  );
}

/** Mã kênh máy đã gửi mà chưa map: kết quả của các mã này chưa vào phiếu. */
function UnmappedTable({ rows, canEdit, onMap }: { rows: UnmappedChannel[]; canEdit: boolean; onMap: (row: UnmappedChannel) => void }) {
  return (
    <Alert type="warning" showIcon
      message={`Máy đã gửi ${rows.length} mã kênh chưa map — kết quả của các mã này chưa vào phiếu`}
      description={(
        <Table<UnmappedChannel>
          rowKey="channelCode" size="small" bordered pagination={false} dataSource={rows} style={{ marginTop: 8 }}
          columns={[
            { title: 'Mã kênh máy', dataIndex: 'channelCode', width: 140, render: (v: string) => <span className="code">{v}</span> },
            { title: 'Giá trị gần nhất', render: (_, row) => <span><b className="num">{row.lastValue}</b> {row.lastUnit ?? ''}</span> },
            { title: 'Số lần', dataIndex: 'count', width: 80, align: 'center' },
            { title: 'Lần cuối', dataIndex: 'lastAt', width: 150, render: (v: string) => <span className="dt">{dayjs(v).format('DD/MM HH:mm:ss')}</span> },
            ...(canEdit ? [{ title: '', width: 90, render: (_: unknown, row: UnmappedChannel) => <Button size="small" type="primary" onClick={() => onMap(row)}>Map</Button> }] : []),
          ]}
        />
      )}
    />
  );
}
