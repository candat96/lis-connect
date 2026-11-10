// Log agent: mọi tín hiệu trên cổng nối máy (byte máy gửi, byte trả lời) và sự kiện kết nối, agent đẩy về máy chủ.
// Xem để biết máy có gửi không, gửi gì, phần mềm trả lời gì — không cần ra tận máy tính đặt agent.
import { SearchOutlined } from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { Alert, Button, Card, Empty, Flex, Input, Segmented, Select, Switch, Table, Tag, Typography } from 'antd';
import dayjs from 'dayjs';
import { useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { agentService, instrumentService, logService, qk, type AgentLog, type LogDirection } from '@/services';
import { decodeBytes, spacedHex } from './bytes';

const PAGE = 300;

type Kind = 'tat-ca' | 'du-lieu' | 'su-kien';
const KIND_OPTIONS: { value: Kind; label: string }[] = [
  { value: 'tat-ca', label: 'Tất cả' },
  { value: 'du-lieu', label: 'Dữ liệu máy' },
  { value: 'su-kien', label: 'Sự kiện' },
];
const KIND_DIRECTIONS: Record<Kind, string | undefined> = { 'tat-ca': undefined, 'du-lieu': 'in,out', 'su-kien': 'event,agent' };

const DIRECTION: Record<LogDirection, { label: string; color?: string }> = {
  in: { label: 'Máy → LIS', color: 'blue' },
  out: { label: 'LIS → Máy', color: 'green' },
  event: { label: 'Sự kiện', color: 'gold' },
  agent: { label: 'Agent' },
};

export default function LogsPage() {
  const [params, setParams] = useSearchParams();
  const agentId = params.get('agentId') ?? undefined;
  const instrumentId = params.get('instrumentId') ?? undefined;
  const [kind, setKind] = useState<Kind>('tat-ca');
  const [search, setSearch] = useState('');
  const [live, setLive] = useState(true);
  const [showHex, setShowHex] = useState(false);
  const [limit, setLimit] = useState(PAGE);

  const agents = useQuery({ queryKey: qk.agents, queryFn: agentService.list, staleTime: 30_000 });
  const instruments = useQuery({ queryKey: qk.instruments, queryFn: instrumentService.list, staleTime: 30_000 });

  const filter = useMemo(
    () => ({ agentId, instrumentId, direction: KIND_DIRECTIONS[kind], q: search || undefined, limit }),
    [agentId, instrumentId, kind, search, limit],
  );
  const logs = useQuery({
    queryKey: qk.logs(filter), queryFn: () => logService.list(filter),
    refetchInterval: live ? 2_000 : false, placeholderData: (prev) => prev,
  });

  const setParam = (key: string, value: string | undefined) => {
    const next = new URLSearchParams(params);
    if (value) next.set(key, value);
    else next.delete(key);
    setParams(next, { replace: true });
    setLimit(PAGE);
  };

  const rows = logs.data ?? [];

  return (
    <div data-screen="log-agent">
      <div className="page-header">
        <h1 className="page-title">Log agent</h1>
        <Flex align="center" gap={8}>
          <Switch checked={live} onChange={setLive} aria-label="Tự làm mới" />
          <Typography.Text type="secondary">{live ? 'Đang tự làm mới mỗi 2 giây' : 'Đã tạm dừng làm mới'}</Typography.Text>
        </Flex>
      </div>

      <Card size="small">
        <Flex vertical gap={12}>
          <Flex gap={8} wrap align="center">
            <Select aria-label="Lọc theo agent" placeholder="Tất cả agent" allowClear style={{ width: 240 }} value={agentId}
              onChange={(v) => setParam('agentId', v)} options={(agents.data ?? []).map((a) => ({ value: a.id, label: a.name }))} />
            <Select aria-label="Lọc theo máy" placeholder="Tất cả máy" allowClear style={{ width: 240 }} value={instrumentId}
              onChange={(v) => setParam('instrumentId', v)} options={(instruments.data ?? []).map((i) => ({ value: i.id, label: i.name }))} />
            <Segmented<Kind> aria-label="Loại log" options={KIND_OPTIONS} value={kind} onChange={(v) => { setKind(v); setLimit(PAGE); }} />
            <Input aria-label="Tìm trong log" placeholder="Tìm barcode, chữ trong bản tin…" allowClear prefix={<SearchOutlined />} style={{ width: 280 }}
              onPressEnter={(e) => { setSearch(e.currentTarget.value.trim()); setLimit(PAGE); }}
              onChange={(e) => e.target.value === '' && setSearch('')} />
            <Flex align="center" gap={6}>
              <Switch size="small" checked={showHex} onChange={setShowHex} aria-label="Hiện hex" />
              <Typography.Text type="secondary">Hiện hex</Typography.Text>
            </Flex>
          </Flex>

          {logs.error && <Alert type="error" showIcon message={(logs.error as Error).message} />}

          <Table<AgentLog>
            rowKey="id" size="small" bordered pagination={false} loading={logs.isLoading} dataSource={rows} scroll={{ x: 900 }}
            locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Chưa có log nào khớp. Agent đẩy log lên ngay khi cổng có tín hiệu; máy chủ giữ 14 ngày." /> }}
            columns={[
              {
                title: 'Thời gian', dataIndex: 'at', width: 150,
                render: (v: string) => <span className="dt" style={{ fontSize: 13 }}>{dayjs(v).format('DD/MM HH:mm:ss.SSS')}</span>,
              },
              { title: 'Máy', dataIndex: 'instrumentName', width: 170, ellipsis: true, render: (v: string | null) => v ?? <Typography.Text type="secondary">—</Typography.Text> },
              {
                title: 'Chiều', dataIndex: 'direction', width: 112,
                render: (v: LogDirection) => <Tag color={DIRECTION[v].color} style={{ marginInlineEnd: 0 }}>{DIRECTION[v].label}</Tag>,
              },
              {
                title: 'Nội dung',
                render: (_, row) => (
                  <div style={{ wordBreak: 'break-all' }}>
                    {row.note && <div>{row.note}</div>}
                    {row.hex && (
                      <div className="code" style={{ fontWeight: 500, fontSize: 13, whiteSpace: 'pre-wrap' }}>
                        {showHex ? spacedHex(row.hex) : decodeBytes(row.hex)}
                      </div>
                    )}
                  </div>
                ),
              },
            ]}
          />

          <Flex justify="space-between" align="center">
            <Typography.Text type="secondary">{rows.length} dòng mới nhất · ký tự điều khiển hiện dạng &lt;STX&gt;, &lt;CR&gt;…</Typography.Text>
            {rows.length >= limit && <Button onClick={() => setLimit((n) => Math.min(1000, n + PAGE))} disabled={limit >= 1000}>Xem thêm dòng cũ hơn</Button>}
          </Flex>
        </Flex>
      </Card>
    </div>
  );
}
