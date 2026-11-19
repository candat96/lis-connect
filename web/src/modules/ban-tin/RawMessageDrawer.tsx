// Chi tiết một bản tin thô: nội dung máy gửi (chữ đọc được / hex), kết quả giải mã, lịch sử giải mã,
// giải mã thử (không ghi gì) và giải mã lại (Quản trị, Trưởng khoa).
import { ExperimentOutlined, ReloadOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, Button, Descriptions, Drawer, Empty, Flex, Modal, Spin, Switch, Table, Tag, Typography } from 'antd';
import dayjs from 'dayjs';
import { useState } from 'react';
import { qk, rawMessageService, type DecodeResult, type RawMessageDecode, type RawPreview, type ResolvedMismatch } from '@/services';
import { StatusTag } from '@/shared/ui/StatusTag';
import { useCanRedecode } from '@/stores/sessionStore';
import { decodeBytes, spacedHex } from '../agent/bytes';
import { decodeKindLabel, formatSize, purgeDate, REDECODE_SAFETY, redecodeText, summarizeDecode } from './rawMessages';

const time = (v: string | null) => (v ? dayjs(v).format('DD/MM/YYYY HH:mm:ss.SSS') : '—');

/** Các con số của một lượt giải mã, dạng thẻ — chỉ hiện số khác 0. */
function DecodeCounts({ r }: { r: DecodeResult }) {
  const items: [string, number | undefined, string?][] = [
    ['Mẫu', r.readings],
    ['Kết quả ghi', r.stored, 'green'],
    ['Chờ thay', r.pendingReplacement, 'orange'],
    ['Chưa ghép', r.unmatched, 'gold'],
    ['QC', r.qc, 'blue'],
    ['Giữ nguyên', r.unchanged],
    ['Xung đột chờ thay', r.conflicts, 'orange'],
    ['Cũ hơn kết quả hiện có', r.olderThanCurrent],
    ['Bỏ chờ thay cũ', r.pendingCleared],
    ['Thay dòng chưa ghép cũ', r.supersededUnmatched],
    ['Thay dòng QC cũ', r.supersededQc],
  ];
  const shown = items.filter(([, v]) => (v ?? 0) > 0);
  if (shown.length === 0) return <Typography.Text type="secondary">Không ra kết quả nào</Typography.Text>;
  return (
    <Flex gap={6} wrap>
      {shown.map(([label, v, color]) => (
        <Tag key={label} color={color} style={{ marginInlineEnd: 0 }}>{label}: <b className="num">{v}</b></Tag>
      ))}
    </Flex>
  );
}

function MismatchList({ rows }: { rows: ResolvedMismatch[] }) {
  return (
    <Alert
      type="warning" showIcon
      message={`${rows.length} dòng Chưa ghép đã gán cho người bệnh nhưng giải mã lại ra giá trị khác — giá trị đã gán được giữ nguyên, cần kiểm tay`}
      description={
        <ul style={{ margin: 0, paddingInlineStart: 18 }}>
          {rows.map((m, i) => (
            <li key={`${m.unmatchedId ?? i}`}>
              <span className="code">{m.barcode || '(không barcode)'}</span> · kênh <span className="code">{m.channel}</span>:
              {' '}đang là <b>{m.oldValue ?? '—'}</b>, giải mã lại ra <b>{m.newValue}</b>
            </li>
          ))}
        </ul>
      }
    />
  );
}

function PreviewTable({ preview }: { preview: RawPreview }) {
  const rows = preview.readings.flatMap((r, ri) =>
    r.items.map((item, ii) => ({ key: `${ri}-${ii}`, barcode: r.barcode, isQc: r.isQc, first: ii === 0, span: r.items.length, ...item })),
  );
  return (
    <Flex vertical gap={8}>
      {preview.abandoned && <Alert type="warning" showIcon message="Lượt truyền bị bỏ dở giữa chừng — có thể thiếu kết quả" />}
      {preview.notes.length > 0 && <Alert type="info" showIcon message="Ghi chú của bộ giải mã" description={preview.notes.join('\n')} style={{ whiteSpace: 'pre-wrap' }} />}
      {preview.queries.length > 0 && (
        <Typography.Text>Máy hỏi chỉ định cho: {preview.queries.map((b) => <span key={b} className="code" style={{ marginInlineEnd: 8 }}>{b}</span>)}</Typography.Text>
      )}
      <Table
        size="small" bordered pagination={false} dataSource={rows} aria-label="Kết quả giải mã thử"
        locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Giải mã thử không ra kết quả nào" /> }}
        columns={[
          {
            title: 'Barcode', dataIndex: 'barcode', width: 150,
            onCell: (row) => ({ rowSpan: row.first ? row.span : 0 }),
            render: (v: string, row) => <Flex gap={6} align="center"><span className="code">{v || '—'}</span>{row.isQc && <Tag color="blue">QC</Tag>}</Flex>,
          },
          { title: 'Kênh máy', dataIndex: 'channelCode', width: 120, render: (v: string) => <span className="code">{v}</span> },
          { title: 'Giá trị', dataIndex: 'rawValue', render: (v: string) => <b className="num">{v}</b> },
          { title: 'Đơn vị', dataIndex: 'unit', width: 100, render: (v: string | null) => v ?? '' },
          { title: 'Cờ máy', dataIndex: 'flag', width: 90, render: (v: string | null) => v ?? '' },
        ]}
      />
    </Flex>
  );
}

export function RawMessageDrawer({ id, onClose }: { id: string | null; onClose: () => void }) {
  const { message } = App.useApp();
  const queryClient = useQueryClient();
  const canRedecode = useCanRedecode();
  const [showHex, setShowHex] = useState(false);
  const [confirmOpen, setConfirmOpen] = useState(false);

  const detail = useQuery({
    queryKey: qk.rawMessage(id ?? ''), queryFn: () => rawMessageService.get(id!), enabled: !!id,
    // Đang chờ / đang giải mã thì hỏi lại cho tới khi có trạng thái cuối.
    refetchInterval: (q) => (q.state.data && ['cho-giai-ma', 'dang-giai-ma'].includes(q.state.data.decodeStatus) ? 2_000 : false),
  });
  const preview = useMutation({ mutationFn: () => rawMessageService.preview(id!) });
  const redecode = useMutation({
    mutationFn: () => rawMessageService.redecode({ ids: [id!] }),
    onSuccess: (totals) => {
      setConfirmOpen(false);
      message.success(redecodeText(totals), 6);
      queryClient.invalidateQueries({ queryKey: qk.rawMessage(id!) });
      queryClient.invalidateQueries({ queryKey: ['raw-messages'] });
    },
  });

  const close = () => {
    preview.reset();
    redecode.reset();
    setConfirmOpen(false);
    onClose();
  };

  const m = detail.data;
  const result = m?.decodeResult ?? null;
  const busy = m ? ['cho-giai-ma', 'dang-giai-ma'].includes(m.decodeStatus) : true;

  return (
    <Drawer
      open={!!id} onClose={close} width={860} destroyOnClose
      title={m ? <Flex gap={8} align="center" wrap><span>{m.instrumentName}</span><Typography.Text type="secondary" style={{ fontWeight: 400 }}>{time(m.completedAt)}</Typography.Text><StatusTag domain="decode" value={m.decodeStatus} /></Flex> : 'Bản tin thô'}
      extra={
        canRedecode && m ? (
          <Flex gap={8}>
            <Button icon={<ExperimentOutlined />} loading={preview.isPending} onClick={() => preview.mutate()}>Giải mã thử</Button>
            <Button type="primary" icon={<ReloadOutlined />} disabled={busy} onClick={() => setConfirmOpen(true)}>Giải mã lại</Button>
          </Flex>
        ) : null
      }
    >
      {detail.isLoading && <Spin style={{ margin: 48 }} />}
      {detail.error && <Alert type="error" showIcon message={(detail.error as Error).message} />}
      {m && (
        <Flex vertical gap={16}>
          {m.decodeStatus === 'loi' && (
            <Alert
              type="error" showIcon
              message={result?.error || result?.note || 'Giải mã lỗi'}
              description={`Bản tin lỗi chưa đưa được kết quả nào vào hệ thống. Máy chủ sẽ xoá bản tin này ngày ${purgeDate(m.storedAt).format('DD/MM/YYYY')} (giữ ${90} ngày) — kiểm cấu hình máy rồi giải mã lại trước ngày đó.`}
            />
          )}
          {m.decodeStatus === 'can-xem-lai' && (
            <Alert type="warning" showIcon message={result?.note || 'Giải mã bị gián đoạn'} description="Máy chủ không tự chạy lại bản tin này. Xem nội dung rồi bấm Giải mã lại." />
          )}
          {result?.warnings && result.warnings.length > 0 && (
            <Alert type="warning" showIcon message="Bộ giải mã đã bỏ một phần dữ liệu" description={<ul style={{ margin: 0, paddingInlineStart: 18 }}>{result.warnings.map((w) => <li key={w}>{w}</li>)}</ul>} />
          )}
          {result?.resolvedMismatch && result.resolvedMismatch.length > 0 && <MismatchList rows={result.resolvedMismatch} />}

          <Descriptions size="small" bordered column={2} labelStyle={{ width: 150 }}>
            <Descriptions.Item label="Máy">{m.instrumentName}{m.model ? ` · ${m.model}` : ''}</Descriptions.Item>
            <Descriptions.Item label="Giao thức">{m.protocolFamily ?? 'Chưa chọn'}</Descriptions.Item>
            <Descriptions.Item label="Bắt đầu nhận">{time(m.startedAt)}</Descriptions.Item>
            <Descriptions.Item label="Nhận xong">{time(m.completedAt)}</Descriptions.Item>
            <Descriptions.Item label="Máy chủ lưu lúc">{time(m.storedAt)}</Descriptions.Item>
            <Descriptions.Item label="Cỡ">
              {formatSize(m.size)}{m.partial && <Tag color="warning" style={{ marginInlineStart: 8 }}>Dở dang</Tag>}
            </Descriptions.Item>
            <Descriptions.Item label="Kết quả giải mã" span={2}>
              <Flex vertical gap={6}>
                {result ? <DecodeCounts r={result} /> : <Typography.Text type="secondary">{busy ? 'Đang chờ giải mã…' : '—'}</Typography.Text>}
                {(result?.queries?.length ?? 0) > 0 && <Typography.Text type="secondary">Máy hỏi chỉ định cho {result!.queries!.length} mẫu</Typography.Text>}
              </Flex>
            </Descriptions.Item>
            <Descriptions.Item label="Bộ giải mã" span={2}>
              <Typography.Text type="secondary" className="num">{m.decoderVersion ?? '—'}{m.decodedAt ? ` · lúc ${time(m.decodedAt)}` : ''}</Typography.Text>
            </Descriptions.Item>
          </Descriptions>

          <section>
            <Flex justify="space-between" align="center" style={{ marginBottom: 8 }}>
              <Typography.Title level={5} style={{ margin: 0 }}>Nội dung máy gửi</Typography.Title>
              <Flex align="center" gap={6}>
                <Switch size="small" checked={showHex} onChange={setShowHex} aria-label="Hiện hex" />
                <Typography.Text type="secondary">Hiện hex</Typography.Text>
              </Flex>
            </Flex>
            <pre className="raw-text" aria-label="Nội dung bản tin">{showHex ? spacedHex(m.hex) : decodeBytes(m.hex)}</pre>
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              Ký tự điều khiển hiện dạng &lt;STX&gt;, &lt;CR&gt;…{m.hexTruncated ? ` · chỉ hiện 256 KB đầu trong ${formatSize(m.size)}` : ''}
            </Typography.Text>
          </section>

          {(preview.data || preview.error) && (
            <section>
              <Typography.Title level={5} style={{ marginTop: 0 }}>Giải mã thử bằng cấu hình hiện tại <Typography.Text type="secondary" style={{ fontWeight: 400, fontSize: 13 }}>— chưa ghi gì</Typography.Text></Typography.Title>
              {preview.error ? <Alert type="error" showIcon message={(preview.error as Error).message} /> : <PreviewTable preview={preview.data!} />}
            </section>
          )}

          <section>
            <Typography.Title level={5} style={{ marginTop: 0 }}>Lịch sử giải mã</Typography.Title>
            <Table<RawMessageDecode>
              size="small" bordered pagination={false} rowKey={(r) => `${r.at}-${r.kind}`} dataSource={m.decodes}
              locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Chưa giải mã lần nào" /> }}
              columns={[
                { title: 'Lúc', dataIndex: 'at', width: 160, render: (v: string) => <span className="num">{dayjs(v).format('DD/MM HH:mm:ss')}</span> },
                { title: 'Cách', dataIndex: 'kind', width: 110, render: (v: RawMessageDecode['kind']) => decodeKindLabel(v) },
                { title: 'Người', dataIndex: 'actorName', width: 150, render: (v: string | null) => v ?? <Typography.Text type="secondary">Hệ thống</Typography.Text> },
                { title: 'Trạng thái', dataIndex: 'status', width: 140, render: (v: RawMessageDecode['status']) => <StatusTag domain="decode" value={v} /> },
                { title: 'Kết quả', render: (_, r) => summarizeDecode(r.status, r.result) || '—' },
              ]}
            />
          </section>
        </Flex>
      )}

      <Modal
        open={confirmOpen} title="Giải mã lại bản tin này?" okText="Giải mã lại" cancelText="Thôi"
        onOk={() => redecode.mutate()} confirmLoading={redecode.isPending} onCancel={() => setConfirmOpen(false)}
      >
        <Flex vertical gap={8}>
          <Typography.Text>Máy chủ giải mã lại bằng cấu hình hiện tại của máy <b>{m?.instrumentName}</b> rồi đưa kết quả vào hệ thống.</Typography.Text>
          <Alert type="info" showIcon message={REDECODE_SAFETY} />
          {redecode.error && <Alert type="error" showIcon message={(redecode.error as Error).message} />}
        </Flex>
      </Modal>
    </Drawer>
  );
}
