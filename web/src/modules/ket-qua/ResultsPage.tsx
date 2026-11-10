// Màn Kết quả: trái là danh sách mẫu (lọc ngày/trạng thái/tìm), phải là bảng chỉ số của mẫu đang chọn.
// Đợt này chỉ xem; nhập tay, duyệt, in phiếu vẫn làm ở app Windows.
import { useEffect, useMemo, useState } from 'react';
import { Alert, Card, DatePicker, Descriptions, Empty, Input, Segmented, Space, Table, Tag, Tooltip, Typography } from 'antd';
import { SwapOutlined, WarningOutlined } from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import dayjs, { type Dayjs } from 'dayjs';
import { qk, resultService, type ResultFlag, type ResultRow, type SampleListItem, type SampleStatus } from '@/services';
import { StatusTag } from '@/shared/ui/StatusTag';
import { tokens } from '@/app/theme/tokens';

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
  Low: { text: 'Thấp', rowClass: 'lab-L' },
  High: { text: 'Cao', rowClass: 'lab-H' },
  CriticalLow: { text: 'Thấp nguy hiểm', rowClass: 'lab-crit' },
  CriticalHigh: { text: 'Cao nguy hiểm', rowClass: 'lab-crit' },
  Abnormal: { text: 'Bất thường', rowClass: 'lab-H' },
};

const SEX: Record<string, string> = { M: 'Nam', F: 'Nữ', O: 'Khác' };

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

  // Mẫu đang chọn không còn trong danh sách (đổi bộ lọc) thì bỏ chọn; chưa chọn gì thì chọn dòng đầu.
  useEffect(() => {
    const list = samples.data;
    if (!list) return;
    if (selectedId && !list.some((s) => s.sampleId === selectedId)) setSelectedId(list[0]?.sampleId ?? null);
    else if (!selectedId && list.length > 0) setSelectedId(list[0].sampleId);
  }, [samples.data, selectedId]);

  return (
    <div data-screen="ket-qua" style={{ height: '100%', display: 'grid', gridTemplateColumns: 'minmax(480px, 40%) minmax(0, 1fr)', gap: tokens.space.md }}>
      <Card size="small" title="Danh sách mẫu" style={{ display: 'flex', flexDirection: 'column', minHeight: 0 }}
        styles={{ body: { flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', gap: tokens.space.sm } }}>
        <Space wrap size={tokens.space.sm}>
          <DatePicker.RangePicker aria-label="Khoảng ngày chỉ định" value={range} format="DD/MM/YYYY" allowClear={false}
            onChange={(v) => v?.[0] && v[1] && setRange([v[0], v[1]])} />
          <Input.Search aria-label="Tìm theo barcode, mã hoặc tên người bệnh" placeholder="Barcode / mã / tên người bệnh" allowClear style={{ width: 240 }}
            onSearch={(v) => setSearch(v.trim())} />
        </Space>
        <Segmented<StatusFilter> aria-label="Lọc theo trạng thái" options={STATUS_OPTIONS} value={status} onChange={setStatus} />
        {samples.error && <Alert type="error" showIcon message={(samples.error as Error).message} />}
        <div style={{ flex: 1, minHeight: 0, overflow: 'auto' }}>
          <Table<SampleListItem>
            rowKey="sampleId" size="small" bordered pagination={false} sticky loading={samples.isLoading}
            dataSource={samples.data ?? []}
            locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Không có mẫu nào trong khoảng ngày này" /> }}
            rowClassName={(row) => (row.sampleId === selectedId ? 'lookup-row-active' : '')}
            onRow={(row) => ({ onClick: () => setSelectedId(row.sampleId), style: { cursor: 'pointer' } })}
            columns={[
              { title: 'Barcode', dataIndex: 'barcode', width: 104, render: (v: string) => <span className="code" style={{ fontWeight: 700 }}>{v}</span> },
              {
                title: 'Người bệnh', dataIndex: 'patientName', ellipsis: true,
                render: (v: string, row) => (
                  <Space size={4}>
                    <span>{v}</span>
                    {row.hasAbnormal && <Tooltip title="Có chỉ số bất thường"><WarningOutlined style={{ color: tokens.colors.dangerText }} aria-label="Có chỉ số bất thường" /></Tooltip>}
                    {row.hasPending && <Tooltip title="Máy gửi lại giá trị khác — có bản chờ thay thế"><SwapOutlined style={{ color: tokens.colors.warningText }} aria-label="Có bản chờ thay thế" /></Tooltip>}
                  </Space>
                ),
              },
              { title: 'KQ', width: 58, align: 'center', render: (_, row) => <span className="num">{row.withValue}/{row.total}</span> },
              { title: 'Trạng thái', dataIndex: 'status', width: 118, render: (v: string) => <StatusTag domain="sample" value={v} /> },
              { title: 'Chỉ định', dataIndex: 'orderedAt', width: 92, render: (v: string) => <span className="num">{dayjs(v).format('DD/MM HH:mm')}</span> },
            ]}
          />
        </div>
        <Typography.Text type="secondary">{samples.data ? `${samples.data.length} mẫu` : ''} · tự làm mới mỗi 5 giây</Typography.Text>
      </Card>

      <Card size="small" title={detail.data ? `Kết quả mẫu ${detail.data.barcode}` : 'Kết quả'} style={{ display: 'flex', flexDirection: 'column', minHeight: 0 }}
        styles={{ body: { flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', gap: tokens.space.sm } }}
        extra={detail.data && <StatusTag domain="sample" value={detail.data.approvedAt ? 'da-duyet' : 'cho-duyet'} />}>
        {detail.error && <Alert type="error" showIcon message={(detail.error as Error).message} />}
        {!selectedId && <Empty description="Chọn một mẫu ở danh sách bên trái" style={{ margin: 'auto' }} />}
        {detail.data && (
          <>
            <Descriptions size="small" column={4} bordered
              items={[
                { key: 'name', label: 'Người bệnh', children: <b>{detail.data.patient.name}</b>, span: 2 },
                { key: 'code', label: 'Mã', children: <span className="code">{detail.data.patient.code}</span> },
                { key: 'sex', label: 'Giới / sinh', children: `${SEX[detail.data.patient.sex] ?? '?'} · ${detail.data.patient.dob ? dayjs(detail.data.patient.dob).format('DD/MM/YYYY') : detail.data.patient.birthYear ?? '?'}` },
                { key: 'doctor', label: 'BS chỉ định', children: detail.data.order.doctor ?? '—' },
                { key: 'dept', label: 'Khoa / phòng', children: detail.data.order.dept ?? '—' },
                { key: 'diag', label: 'Chẩn đoán', children: detail.data.order.diagnosis ?? '—', span: 2 },
                ...(detail.data.approvedAt
                  ? [{ key: 'approved', label: 'Duyệt', children: `${detail.data.approvedBy} · ${dayjs(detail.data.approvedAt).format('DD/MM/YYYY HH:mm')}`, span: 4 }]
                  : []),
              ]} />
            <div style={{ flex: 1, minHeight: 0, overflow: 'auto' }}>
              <table className="lab-table">
                <colgroup>
                  <col style={{ width: 44 }} /><col /><col style={{ width: 130 }} /><col style={{ width: 96 }} /><col style={{ width: 190 }} /><col style={{ width: 130 }} />
                </colgroup>
                <thead>
                  <tr><th>STT</th><th>Chỉ số</th><th>Kết quả</th><th>Đơn vị</th><th>Khoảng tham chiếu</th><th>Đánh giá</th></tr>
                </thead>
                <tbody>{numbered(detail.data.rows).map(({ row, no }) => <ResultLine key={row.orderItemId} row={row} no={no} />)}</tbody>
              </table>
            </div>
          </>
        )}
      </Card>
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
    return <tr className="lab-svc"><td colSpan={6}>{row.serviceName} <span className="code" style={{ fontWeight: 500 }}>({row.serviceCode})</span></td></tr>;
  }
  const flag = FLAG[row.flag];
  return (
    <tr className={`lab-ind ${no % 2 ? '' : 'lab-odd'} ${flag?.rowClass ?? ''}`}>
      <td className="lab-stt">{no}</td>
      <td style={{ paddingLeft: row.depth ? 22 : 8 }}>{row.serviceName}</td>
      <td>
        <span className="num" style={{ fontWeight: 700 }}>{row.value ?? ''}</span>
        {row.pendingValue !== null && (
          <Tooltip title="Máy gửi lại giá trị này sau khi mẫu đã duyệt — chờ xem xét thay thế">
            <Tag color="warning" icon={<SwapOutlined />} style={{ marginInlineStart: 6, marginInlineEnd: 0 }}>{row.pendingValue || 'lỗi đọc'}</Tag>
          </Tooltip>
        )}
      </td>
      <td className="lab-muted">{row.unit ?? ''}</td>
      <td className="lab-muted">{row.refText ?? ''}</td>
      <td style={{ fontWeight: 700, color: flag ? tokens.colors.dangerText : undefined }}>
        {flag?.text ?? ''}
        {row.source === 'may' && !flag && <span className="lab-muted" style={{ fontWeight: 500 }}>Máy</span>}
      </td>
    </tr>
  );
}
