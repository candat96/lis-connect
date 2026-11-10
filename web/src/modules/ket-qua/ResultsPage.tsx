// Màn Kết quả: trái là danh sách mẫu (lọc ngày/trạng thái/tìm), phải là phiếu kết quả của mẫu đang chọn
// (nhập / sửa / duyệt ở SampleResultCard).
import { SearchOutlined, SwapOutlined, WarningOutlined } from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { Alert, App, Card, DatePicker, Empty, Flex, Input, Segmented, Space, Table, Tooltip, Typography } from 'antd';
import dayjs, { type Dayjs } from 'dayjs';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { qk, resultService, type SampleListItem, type SampleStatus } from '@/services';
import { StatusTag } from '@/shared/ui/StatusTag';
import { SampleResultCard } from './SampleResultCard';

const REFRESH_MS = 5_000;

type StatusFilter = SampleStatus | 'tat-ca';
const STATUS_OPTIONS: { value: StatusFilter; label: string }[] = [
  { value: 'tat-ca', label: 'Tất cả' },
  { value: 'cho-ket-qua', label: 'Chờ kết quả' },
  { value: 'cho-duyet', label: 'Chờ duyệt' },
  { value: 'da-duyet', label: 'Đã duyệt' },
];

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
  const { modal } = App.useApp();
  const [range, setRange] = useState<[Dayjs, Dayjs]>(() => [dayjs().subtract(6, 'day'), dayjs()]);
  const [status, setStatus] = useState<StatusFilter>('tat-ca');
  const [search, setSearch] = useState('');
  const [selectedId, setSelectedId] = useState<string | null>(null);
  // Phiếu đang mở có giá trị gõ dở chưa lưu hay không (SampleResultCard báo lên).
  const dirtyRef = useRef(false);
  const onDirtyChange = useCallback((dirty: boolean) => { dirtyRef.current = dirty; }, []);

  const filter = useMemo(
    () => ({ from: range[0].format('YYYY-MM-DD'), to: range[1].format('YYYY-MM-DD'), status: status === 'tat-ca' ? undefined : status, q: search || undefined }),
    [range, status, search],
  );

  // Tự làm mới để kết quả máy vừa gửi hiện ra mà không phải bấm gì.
  const samples = useQuery({ queryKey: qk.samples(filter), queryFn: () => resultService.list(filter), refetchInterval: REFRESH_MS, placeholderData: (prev) => prev });

  // Chưa chọn mẫu nào thì chọn dòng đầu. Mẫu đang chọn rời khỏi danh sách (vd vừa duyệt khi đang lọc "Chờ duyệt")
  // thì VẪN giữ phiếu đang mở — không tự nhảy sang mẫu khác khi người dùng đang làm dở.
  useEffect(() => {
    if (!selectedId && samples.data?.length) setSelectedId(samples.data[0].sampleId);
  }, [samples.data, selectedId]);

  const select = (sampleId: string) => {
    if (sampleId === selectedId) return;
    if (!dirtyRef.current) {
      setSelectedId(sampleId);
      return;
    }
    modal.confirm({
      title: 'Bỏ các giá trị đang gõ?',
      content: 'Phiếu đang mở có giá trị chưa lưu. Chuyển sang mẫu khác sẽ mất các giá trị đó.',
      okText: 'Bỏ và chuyển mẫu', cancelText: 'Ở lại', okButtonProps: { danger: true },
      onOk: () => setSelectedId(sampleId),
    });
  };

  return (
    <div data-screen="ket-qua">
      <div className="page-header">
        <h1 className="page-title">Kết quả xét nghiệm</h1>
        <Typography.Text type="secondary">{samples.data ? `${samples.data.length} mẫu · ` : ''}tự làm mới mỗi 5 giây</Typography.Text>
      </div>

      <div className="split-page" style={{ gridTemplateColumns: 'minmax(380px, 30%) minmax(0, 1fr)' }}>
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
              onRow={(row) => ({ onClick: () => select(row.sampleId) })}
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

        <SampleResultCard sampleId={selectedId} onDirtyChange={onDirtyChange} />
      </div>
    </div>
  );
}
