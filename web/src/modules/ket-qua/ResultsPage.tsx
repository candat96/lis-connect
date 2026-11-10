// Màn Kết quả: trái là danh sách mẫu (lọc ngày/trạng thái/tìm), phải là phiếu kết quả của mẫu đang chọn.
// Đợt này chỉ xem; nhập tay, duyệt, in phiếu vẫn làm ở app Windows.
import { SearchOutlined, SwapOutlined, WarningOutlined } from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { Alert, Card, DatePicker, Descriptions, Empty, Flex, Input, Segmented, Space, Table, Tag, Tooltip, Typography } from 'antd';
import dayjs, { type Dayjs } from 'dayjs';
import { useEffect, useMemo, useState } from 'react';
import { qk, resultService, type ResultFlag, type ResultRow, type SampleListItem, type SampleStatus } from '@/services';
import { StatusTag } from '@/shared/ui/StatusTag';

const REFRESH_MS = 5_000;

type StatusFilter = SampleStatus | 'tat-ca';
const STATUS_OPTIONS: { value: StatusFilter; label: string }[] = [
  { value: 'tat-ca', label: 'Tất cả' },
  { value: 'cho-ket-qua', label: 'Chờ kết quả' },
  { value: 'cho-duyet', label: 'Chờ duyệt' },
  { value: 'da-duyet', label: 'Đã duyệt' },
];

// Cờ kết quả: chữ + lớp tô nền dòng (luôn có chữ, không chỉ dựa vào màu).
const FLAG: Record<ResultFlag, { text: string; rowClass: string } | null> = {
  None: null,
  Low: { text: '↓ Thấp', rowClass: 'lab-low' },
  High: { text: '↑ Cao', rowClass: 'lab-abn' },
  CriticalLow: { text: '↓↓ Thấp nguy hiểm', rowClass: 'lab-crit' },
  CriticalHigh: { text: '↑↑ Cao nguy hiểm', rowClass: 'lab-crit' },
  Abnormal: { text: 'Bất thường', rowClass: 'lab-abn' },
};

const SEX: Record<string, string> = { M: 'Nam', F: 'Nữ', O: 'Khác' };

const presets = (): { label: string; value: [Dayjs, Dayjs] }[] => {
  const today = dayjs();
  return [
    { label: 'Hôm nay', value: [today, today] },
    { label: 'Hôm qua', value: [today.subtract(1, 'day'), today.subtract(1, 'day')] },
    { label: '7 ngày qua', value: [today.subtract(6, 'day'), today] },
    { label: '30 ngày qua', value: [today.subtract(29, 'day'), today] },
  ];
};

export default function ResultsPage() {
  const [range, setRange] = useState<[Dayjs, Dayjs]>(() => [dayjs().subtract(6, 'day'), dayjs()]);
  const [status, setStatus] = useState<StatusFilter>('tat-ca');
  const [search, setSearch] = useState('');
  const [selectedId, setSelectedId] = useState<string | null>(null);

  const filter = useMemo(
    () => ({ from: range[0].format('YYYY-MM-DD'), to: range[1].format('YYYY-MM-DD'), status: status === 'tat-ca' ? undefined : status, q: search || undefined }),
    [range, status, search],
  );

  // Tự làm mới để kết quả máy vừa gửi hiện ra mà không phải bấm gì.
  const samples = useQuery({ queryKey: qk.samples(filter), queryFn: () => resultService.list(filter), refetchInterval: REFRESH_MS, placeholderData: (prev) => prev });
  const detail = useQuery({
    queryKey: qk.sample(selectedId ?? ''), queryFn: () => resultService.detail(selectedId!), enabled: selectedId !== null,
    refetchInterval: REFRESH_MS, placeholderData: (prev, prevQuery) => (prevQuery?.queryKey[1] === selectedId ? prev : undefined),
  });

  // Mẫu đang chọn không còn trong danh sách (đổi bộ lọc) thì chọn dòng đầu; chưa chọn gì cũng vậy.
  useEffect(() => {
    const list = samples.data;
    if (!list) return;
    if (!selectedId || !list.some((s) => s.sampleId === selectedId)) setSelectedId(list[0]?.sampleId ?? null);
  }, [samples.data, selectedId]);

  const d = detail.data;

  return (
    <div data-screen="ket-qua">
      <div className="page-header">
        <h1 className="page-title">Kết quả xét nghiệm</h1>
        <Typography.Text type="secondary">{samples.data ? `${samples.data.length} mẫu · ` : ''}tự làm mới mỗi 5 giây</Typography.Text>
      </div>

      <div className="split-page" style={{ gridTemplateColumns: 'minmax(430px, 34%) minmax(0, 1fr)' }}>
        <Card size="small" className="split-pane">
          <Flex vertical gap={12}>
            <Flex gap={8} wrap>
              <DatePicker.RangePicker aria-label="Khoảng ngày chỉ định" value={range} format="DD/MM/YYYY" allowClear={false} presets={presets()}
                style={{ width: 250 }} onChange={(v) => v?.[0] && v[1] && setRange([v[0], v[1]])} />
              <Input aria-label="Tìm theo barcode, mã hoặc tên người bệnh" placeholder="Barcode / mã / tên người bệnh" allowClear prefix={<SearchOutlined />}
                style={{ flex: 1, minWidth: 200 }} onPressEnter={(e) => setSearch(e.currentTarget.value.trim())}
                onChange={(e) => e.target.value === '' && setSearch('')} />
            </Flex>
            <Segmented<StatusFilter> aria-label="Lọc theo trạng thái" options={STATUS_OPTIONS} value={status} onChange={setStatus} block />
            {samples.error && <Alert type="error" showIcon message={(samples.error as Error).message} />}
            <Table<SampleListItem>
              rowKey="sampleId" size="small" bordered pagination={false} loading={samples.isLoading} dataSource={samples.data ?? []}
              locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Không có mẫu nào trong khoảng ngày này" /> }}
              rowClassName={(row) => `row-clickable ${row.sampleId === selectedId ? 'row-active' : ''}`}
              onRow={(row) => ({ onClick: () => setSelectedId(row.sampleId) })}
              columns={[
                {
                  // Barcode + giờ chỉ định gộp một cột để danh sách gọn, nhường chỗ cho phiếu kết quả.
                  title: 'Barcode', dataIndex: 'barcode', width: 112,
                  render: (v: string, row) => (
                    <div>
                      <div className="code">{v}</div>
                      <Typography.Text type="secondary" style={{ fontSize: 12 }} className="num">{dayjs(row.orderedAt).format('DD/MM HH:mm')}</Typography.Text>
                    </div>
                  ),
                },
                {
                  title: 'Người bệnh', dataIndex: 'patientName',
                  render: (v: string, row) => (
                    <div>
                      <div style={{ fontWeight: 600 }}>{v}</div>
                      <Space size={6}>
                        <Typography.Text type="secondary" style={{ fontSize: 12 }}><span className="num">{row.withValue}/{row.total}</span> chỉ số</Typography.Text>
                        {row.hasAbnormal && <Tooltip title="Có chỉ số bất thường"><WarningOutlined style={{ color: 'var(--c-danger)' }} aria-label="Có chỉ số bất thường" /></Tooltip>}
                        {row.hasPending && <Tooltip title="Máy gửi lại giá trị khác — có bản chờ thay thế"><SwapOutlined style={{ color: 'var(--c-warning)' }} aria-label="Có bản chờ thay thế" /></Tooltip>}
                      </Space>
                    </div>
                  ),
                },
                { title: 'Trạng thái', dataIndex: 'status', width: 118, render: (v: string) => <StatusTag domain="sample" value={v} /> },
              ]}
            />
          </Flex>
        </Card>

        <Card size="small" className="split-pane"
          title={d ? <span>Phiếu kết quả <span className="code">{d.barcode}</span></span> : 'Phiếu kết quả'}
          extra={d && <StatusTag domain="sample" value={d.approvedAt ? 'da-duyet' : 'cho-duyet'} />}>
          {detail.error && <Alert type="error" showIcon message={(detail.error as Error).message} style={{ marginBottom: 12 }} />}
          {!selectedId && <Empty description="Chọn một mẫu ở danh sách bên trái" style={{ margin: '48px 0' }} />}
          {d && (
            <Flex vertical gap={12}>
              <Descriptions size="small" column={2} bordered
                items={[
                  { key: 'name', label: 'Người bệnh', children: <b>{d.patient.name}</b> },
                  { key: 'code', label: 'Mã người bệnh', children: <span className="code">{d.patient.code}</span> },
                  { key: 'sex', label: 'Giới / sinh', children: `${SEX[d.patient.sex] ?? '?'} · ${d.patient.dob ? dayjs(d.patient.dob).format('DD/MM/YYYY') : d.patient.birthYear ?? '?'}` },
                  { key: 'doctor', label: 'BS chỉ định', children: d.order.doctor ?? '—' },
                  { key: 'dept', label: 'Khoa / phòng', children: d.order.dept ?? '—' },
                  { key: 'diag', label: 'Chẩn đoán', children: d.order.diagnosis ?? '—' },
                  ...(d.approvedAt
                    ? [{ key: 'approved', label: 'Duyệt', children: `${d.approvedBy} · ${dayjs(d.approvedAt).format('DD/MM/YYYY HH:mm')}` }]
                    : []),
                ]} />
              <div className="lab-grid-wrap">
                <table className="lab-grid">
                  <colgroup>
                    <col style={{ width: 44 }} /><col /><col style={{ width: 132 }} /><col style={{ width: 84 }} /><col style={{ width: 150 }} /><col style={{ width: 120 }} />
                  </colgroup>
                  <thead>
                    <tr><th>STT</th><th>Chỉ số</th><th>Kết quả</th><th>Đơn vị</th><th>Khoảng tham chiếu</th><th>Đánh giá</th></tr>
                  </thead>
                  <tbody>{numbered(d.rows).map(({ row, no }) => <ResultLine key={row.orderItemId} row={row} no={no} />)}</tbody>
                </table>
              </div>
            </Flex>
          )}
        </Card>
      </div>
    </div>
  );
}

/** Đánh số thứ tự chỉ cho dòng chỉ số — dòng tiêu đề gói không chiếm số. */
function numbered(rows: ResultRow[]): { row: ResultRow; no: number }[] {
  let no = 0;
  return rows.map((row) => ({ row, no: row.isPanelHeader ? 0 : ++no }));
}

function ResultLine({ row, no }: { row: ResultRow; no: number }) {
  if (row.isPanelHeader) {
    return <tr className="lab-svc"><td colSpan={6}><b>{row.serviceName}</b> <span className="code" style={{ fontWeight: 500 }}>({row.serviceCode})</span></td></tr>;
  }
  const flag = FLAG[row.flag];
  return (
    <tr className={flag?.rowClass ?? ''}>
      <td className="c muted">{no}</td>
      <td style={{ paddingLeft: row.depth ? 22 : 8 }}>{row.serviceName}</td>
      <td>
        <span className="num" style={{ fontWeight: 700 }}>{row.value ?? ''}</span>
        {row.pendingValue !== null && (
          <Tooltip title="Máy gửi lại giá trị này sau khi mẫu đã duyệt — chờ xem xét thay thế">
            <Tag color="warning" icon={<SwapOutlined />} style={{ marginInlineStart: 6, marginInlineEnd: 0 }}>{row.pendingValue || 'lỗi đọc'}</Tag>
          </Tooltip>
        )}
      </td>
      <td className="muted">{row.unit ?? ''}</td>
      <td className="muted">{row.refText ?? ''}</td>
      <td>
        {flag ? <span className="flag-text">{flag.text}</span> : row.source === 'may' ? <span className="muted">Máy</span> : ''}
      </td>
    </tr>
  );
}
