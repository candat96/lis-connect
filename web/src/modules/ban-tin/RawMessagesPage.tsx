// Bản tin thô: mọi lượt truyền máy gửi lên, agent lưu nguyên byte, máy chủ giải mã. Xem để biết bản tin nào lỗi / cần xem lại,
// đọc nội dung máy gửi, giải mã thử và giải mã lại sau khi sửa cấu hình máy.
import { ReloadOutlined } from '@ant-design/icons';
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, Button, Card, DatePicker, Empty, Flex, Form, Modal, Segmented, Select, Switch, Table, Tag, Typography } from 'antd';
import dayjs, { type Dayjs } from 'dayjs';
import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { useSearchParams } from 'react-router-dom';
import { instrumentService, qk, rawMessageService, type DecodeStatus, type RawMessageRow } from '@/services';
import { StatusTag } from '@/shared/ui/StatusTag';
import { useCanRedecode } from '@/stores/sessionStore';
import { RawMessageDrawer } from './RawMessageDrawer';
import { formatSize, REDECODE_SAFETY, redecodeText, summarizeDecode } from './rawMessages';

const PAGE = 50;

type View = 'chu-y' | 'tat-ca';
const VIEW_OPTIONS: { value: View; label: string }[] = [
  { value: 'chu-y', label: 'Cần chú ý' },
  { value: 'tat-ca', label: 'Tất cả' },
];

const STATUS_OPTIONS: { value: DecodeStatus; label: string }[] = [
  { value: 'loi', label: 'Lỗi' },
  { value: 'can-xem-lai', label: 'Cần xem lại' },
  { value: 'da-giai-ma', label: 'Đã giải mã' },
  { value: 'khong-co-ket-qua', label: 'Không có kết quả' },
  { value: 'chi-luu', label: 'Chỉ lưu' },
  { value: 'cho-giai-ma', label: 'Chờ giải mã' },
  { value: 'dang-giai-ma', label: 'Đang giải mã' },
];

/** Giải mã lại mọi bản tin của một máy trong một khoảng thời gian (tối đa 500, máy chủ chạy mới → cũ). */
function RangeRedecodeModal({ open, onClose, instruments, defaultInstrumentId }: {
  open: boolean; onClose: () => void; instruments: { value: string; label: string }[]; defaultInstrumentId?: string;
}) {
  const { message } = App.useApp();
  const queryClient = useQueryClient();
  const [form] = Form.useForm<{ instrumentId: string; range: [Dayjs, Dayjs] }>();
  const run = useMutation({
    mutationFn: (v: { instrumentId: string; range: [Dayjs, Dayjs] }) =>
      rawMessageService.redecode({ instrumentId: v.instrumentId, from: v.range[0].toISOString(), to: v.range[1].toISOString() }),
    onSuccess: (totals) => {
      message.success(redecodeText(totals), 8);
      queryClient.invalidateQueries({ queryKey: ['raw-messages'] });
      queryClient.invalidateQueries({ queryKey: ['raw-message'] });
      onClose();
    },
  });

  useEffect(() => {
    if (open) {
      run.reset();
      form.setFieldsValue({ instrumentId: defaultInstrumentId, range: [dayjs().startOf('day'), dayjs()] });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  return (
    <Modal
      open={open} title="Giải mã lại theo khoảng thời gian" okText="Giải mã lại" cancelText="Thôi" width={560}
      onOk={() => form.submit()} confirmLoading={run.isPending} onCancel={onClose} destroyOnClose
    >
      <Form form={form} layout="vertical" onFinish={(v) => run.mutate(v)} requiredMark={false}>
        <Form.Item name="instrumentId" label="Máy" rules={[{ required: true, message: 'Chọn máy' }]}>
          <Select aria-label="Máy giải mã lại" showSearch optionFilterProp="label" options={instruments} placeholder="Chọn máy" />
        </Form.Item>
        <Form.Item name="range" label="Bản tin nhận xong trong khoảng" rules={[{ required: true, message: 'Chọn khoảng thời gian' }]}
          extra="Tối đa 500 bản tin mỗi lần. Máy chủ chạy từ bản tin mới nhất về cũ nhất.">
          <DatePicker.RangePicker showTime={{ format: 'HH:mm' }} format="DD/MM/YYYY HH:mm" style={{ width: '100%' }} allowClear={false} />
        </Form.Item>
      </Form>
      <Flex vertical gap={8}>
        <Alert type="info" showIcon message={REDECODE_SAFETY} />
        {run.error && <Alert type="error" showIcon message={(run.error as Error).message} />}
      </Flex>
    </Modal>
  );
}

export default function RawMessagesPage() {
  const [params, setParams] = useSearchParams();
  const canRedecode = useCanRedecode();
  const instrumentId = params.get('instrumentId') ?? undefined;
  const view: View = params.get('view') === 'tat-ca' ? 'tat-ca' : 'chu-y';
  const statuses = (params.get('status') ?? '').split(',').filter(Boolean) as DecodeStatus[];
  const day = params.get('ngay');
  const openId = params.get('id');
  const [live, setLive] = useState(true);
  const [active, setActive] = useState(0);
  const [rangeOpen, setRangeOpen] = useState(false);
  const tableRef = useRef<HTMLDivElement>(null);

  const instruments = useQuery({ queryKey: qk.instruments, queryFn: instrumentService.list, staleTime: 30_000 });
  const instrumentOptions = (instruments.data ?? []).map((i) => ({ value: i.id, label: i.name }));

  const filter = useMemo(() => ({
    instrumentId,
    attention: view === 'chu-y',
    status: view === 'tat-ca' && statuses.length > 0 ? statuses.join(',') : undefined,
    from: day ? dayjs(day).startOf('day').toISOString() : undefined,
    to: day ? dayjs(day).add(1, 'day').startOf('day').toISOString() : undefined,
    limit: PAGE,
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }), [instrumentId, view, statuses.join(','), day]);

  const list = useInfiniteQuery({
    queryKey: qk.rawMessages(filter),
    queryFn: ({ pageParam }) => rawMessageService.list({ ...filter, before: pageParam }),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => (last.length === PAGE ? last[last.length - 1].cursor : undefined),
    refetchInterval: live ? 5_000 : false,
    placeholderData: (prev) => prev,
  });
  const rows = useMemo(() => list.data?.pages.flat() ?? [], [list.data]);

  useEffect(() => setActive(0), [filter]);

  const setParam = (changes: Record<string, string | undefined>) => {
    const next = new URLSearchParams(params);
    for (const [key, value] of Object.entries(changes)) {
      if (value) next.set(key, value);
      else next.delete(key);
    }
    setParams(next, { replace: true });
  };
  const open = (row: RawMessageRow) => setParam({ id: row.id });

  // Bàn phím: ↑/↓ chuyển dòng, Enter mở bản tin đang chọn.
  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    if (openId || rows.length === 0) return;
    if (e.key === 'ArrowDown') { e.preventDefault(); setActive((i) => Math.min(rows.length - 1, i + 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setActive((i) => Math.max(0, i - 1)); }
    else if (e.key === 'Enter') { e.preventDefault(); open(rows[Math.min(active, rows.length - 1)]); }
  };

  useEffect(() => {
    tableRef.current?.querySelector('.row-active')?.scrollIntoView?.({ block: 'nearest' });
  }, [active]);

  return (
    <div data-screen="ban-tin">
      <div className="page-header">
        <div>
          <h1 className="page-title">Bản tin thô</h1>
          <Typography.Text type="secondary">Nguyên văn mọi lượt máy gửi, máy chủ giải mã và giữ 90 ngày.</Typography.Text>
        </div>
        <Flex align="center" gap={12}>
          <Flex align="center" gap={8}>
            <Switch checked={live} onChange={setLive} aria-label="Tự làm mới" />
            <Typography.Text type="secondary">{live ? 'Tự làm mới mỗi 5 giây' : 'Đã tạm dừng làm mới'}</Typography.Text>
          </Flex>
          {canRedecode && <Button icon={<ReloadOutlined />} onClick={() => setRangeOpen(true)}>Giải mã lại theo khoảng</Button>}
        </Flex>
      </div>

      <Card size="small">
        <Flex vertical gap={12}>
          <Flex gap={8} wrap align="center">
            <Segmented<View> aria-label="Nhóm bản tin" options={VIEW_OPTIONS} value={view}
              onChange={(v) => setParam({ view: v === 'tat-ca' ? 'tat-ca' : undefined, status: undefined })} />
            <Select aria-label="Lọc theo máy" placeholder="Tất cả máy" allowClear showSearch optionFilterProp="label" style={{ width: 240 }}
              value={instrumentId} onChange={(v) => setParam({ instrumentId: v })} options={instrumentOptions} />
            {view === 'tat-ca' && (
              <Select<DecodeStatus[]> aria-label="Lọc theo trạng thái" mode="multiple" allowClear placeholder="Mọi trạng thái" style={{ minWidth: 260 }}
                value={statuses} onChange={(v) => setParam({ status: v.length > 0 ? v.join(',') : undefined })} options={STATUS_OPTIONS} maxTagCount="responsive" />
            )}
            <DatePicker aria-label="Lọc theo ngày" placeholder="Mọi ngày" format="DD/MM/YYYY" value={day ? dayjs(day) : null}
              onChange={(d) => setParam({ ngay: d ? d.format('YYYY-MM-DD') : undefined })} />
          </Flex>

          {view === 'chu-y' && (
            <Typography.Text type="secondary">
              Bản tin lỗi, cần xem lại, có cảnh báo bỏ dữ liệu, hoặc giải mã lại ra giá trị khác với dòng chưa ghép đã gán.
            </Typography.Text>
          )}
          {list.error && <Alert type="error" showIcon message={(list.error as Error).message} />}

          <div ref={tableRef} tabIndex={0} onKeyDown={onKeyDown} aria-label="Danh sách bản tin — ↑/↓ chọn, Enter mở" style={{ outline: 'none' }}>
            <Table<RawMessageRow>
              rowKey="id" size="small" bordered pagination={false} loading={list.isLoading} dataSource={rows} scroll={{ x: 900 }}
              rowClassName={(_, index) => `row-clickable ${index === active ? 'row-active' : ''}`}
              onRow={(row, index) => ({ onClick: () => { setActive(index ?? 0); open(row); } })}
              locale={{
                emptyText: (
                  <Empty image={Empty.PRESENTED_IMAGE_SIMPLE}
                    description={view === 'chu-y' ? 'Không có bản tin nào cần chú ý.' : 'Chưa có bản tin nào khớp bộ lọc.'} />
                ),
              }}
              columns={[
                {
                  title: 'Nhận xong', dataIndex: 'completedAt', width: 150,
                  render: (v: string) => <span className="num">{dayjs(v).format('DD/MM HH:mm:ss')}</span>,
                },
                { title: 'Máy', dataIndex: 'instrumentName', width: 180, ellipsis: true },
                { title: 'Trạng thái', dataIndex: 'decodeStatus', width: 150, render: (v: DecodeStatus) => <StatusTag domain="decode" value={v} /> },
                {
                  title: 'Kết quả',
                  render: (_, row) => {
                    const text = summarizeDecode(row.decodeStatus, row.summary);
                    const warn = (row.summary?.warnings?.length ?? 0) > 0 || (row.summary?.resolvedMismatch?.length ?? 0) > 0;
                    return <Typography.Text type={warn ? 'warning' : undefined}>{text || '—'}</Typography.Text>;
                  },
                },
                {
                  title: 'Cỡ', dataIndex: 'size', width: 110, align: 'right',
                  render: (v: number, row) => (
                    <Flex gap={6} justify="flex-end" align="center">
                      {row.partial && <Tag color="warning" style={{ marginInlineEnd: 0 }}>Dở dang</Tag>}
                      <span className="num">{formatSize(v)}</span>
                    </Flex>
                  ),
                },
              ]}
            />
          </div>

          <Flex justify="space-between" align="center">
            <Typography.Text type="secondary">{rows.length} bản tin · ↑/↓ chọn dòng, Enter mở</Typography.Text>
            {list.hasNextPage && (
              <Button onClick={() => list.fetchNextPage()} loading={list.isFetchingNextPage}>Xem bản tin cũ hơn</Button>
            )}
          </Flex>
        </Flex>
      </Card>

      <RawMessageDrawer id={openId} onClose={() => { setParam({ id: undefined }); tableRef.current?.focus(); }} />
      {canRedecode && (
        <RangeRedecodeModal open={rangeOpen} onClose={() => setRangeOpen(false)} instruments={instrumentOptions} defaultInstrumentId={instrumentId} />
      )}
    </div>
  );
}
