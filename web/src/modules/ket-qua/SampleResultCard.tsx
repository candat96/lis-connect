// Phiếu kết quả của một mẫu: nhập / sửa giá trị, lưu, duyệt, huỷ duyệt, xử lý giá trị máy gửi lại, xem lịch sử.
// Giá trị đang gõ giữ riêng (edits) nên việc tự làm mới mỗi 5 giây không đè mất chữ người dùng chưa lưu.
import { CheckOutlined, CloseOutlined, HistoryOutlined, SaveOutlined, SwapOutlined, UndoOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, AutoComplete, Button, Card, Descriptions, Empty, Flex, Input, Modal, Space, Table, Tag, Tooltip, Typography, type InputRef } from 'antd';
import dayjs from 'dayjs';
import { useEffect, useMemo, useRef, useState } from 'react';
import { qk, resultService, type ResultFlag, type ResultHistory, type ResultRow, type SampleDetail } from '@/services';
import { StatusTag } from '@/shared/ui/StatusTag';
import { useCanEditResults, useCanUnapprove } from '@/stores/sessionStore';

const REFRESH_MS = 5_000;

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
const ACTION: Record<string, string> = {
  nhan: 'Nhập / nhận', sua: 'Sửa', duyet: 'Duyệt', 'huy-duyet': 'Huỷ duyệt', 'thay-the': 'Máy gửi lại / dùng bản máy', 'bo-thay-the': 'Bỏ bản máy gửi lại',
};

interface Props {
  sampleId: string | null;
  /** Báo ra ngoài khi đang có giá trị gõ dở chưa lưu (để hỏi lại trước khi đổi mẫu). */
  onDirtyChange: (dirty: boolean) => void;
}

export function SampleResultCard({ sampleId, onDirtyChange }: Props) {
  const { message, modal } = App.useApp();
  const queryClient = useQueryClient();
  const canEdit = useCanEditResults();
  const canUnapprove = useCanUnapprove();
  const [edits, setEdits] = useState<Record<string, string>>({});
  const [reasonFor, setReasonFor] = useState<'save' | 'unapprove' | null>(null);
  const [reason, setReason] = useState('');
  const [historyOf, setHistoryOf] = useState<ResultRow | null>(null);
  const inputs = useRef(new Map<string, InputRef | null>());

  const detail = useQuery({
    queryKey: qk.sample(sampleId ?? ''), queryFn: () => resultService.detail(sampleId!), enabled: sampleId !== null,
    refetchInterval: REFRESH_MS, placeholderData: (prev, prevQuery) => (prevQuery?.queryKey[1] === sampleId ? prev : undefined),
  });
  const d = detail.data;

  // Đổi mẫu thì bỏ các giá trị gõ dở của mẫu trước.
  useEffect(() => { setEdits({}); }, [sampleId]);

  const editable = canEdit && !!d && d.approvedAt === null;
  const leafRows = useMemo(() => (d?.rows ?? []).filter((r) => !r.isPanelHeader), [d]);
  const dirtyRows = useMemo(
    () => leafRows.filter((r) => edits[r.orderItemId] !== undefined && edits[r.orderItemId].trim() !== (r.value ?? '')),
    [leafRows, edits],
  );
  const dirty = dirtyRows.length > 0;
  useEffect(() => { onDirtyChange(dirty); }, [dirty, onDirtyChange]);

  const refresh = () => Promise.all([
    queryClient.invalidateQueries({ queryKey: qk.sample(sampleId ?? '') }),
    queryClient.invalidateQueries({ queryKey: ['samples'] }),
  ]);
  const onError = (e: unknown) => message.error((e as Error).message);

  /** Lưu lần lượt các ô đã đổi. Ô đang có giá trị cũ thì gửi kèm lý do. Ô lỗi giữ nguyên chữ đang gõ để sửa tiếp. */
  const save = useMutation({
    mutationFn: async (saveReason: string | null) => {
      const failed: string[] = [];
      const saved: string[] = [];
      for (const row of dirtyRows) {
        try {
          await resultService.setValue(row.orderItemId, edits[row.orderItemId], row.value ? saveReason : null);
          saved.push(row.orderItemId);
        } catch (e) {
          failed.push(`${row.serviceName}: ${(e as Error).message}`);
        }
      }
      return { saved, failed };
    },
    onSuccess: async ({ saved, failed }) => {
      setEdits((prev) => Object.fromEntries(Object.entries(prev).filter(([id]) => !saved.includes(id))));
      setReasonFor(null);
      await refresh();
      if (failed.length > 0) modal.error({ title: `Không lưu được ${failed.length} chỉ số`, content: <div>{failed.map((f) => <div key={f}>{f}</div>)}</div> });
      else message.success(`Đã lưu ${saved.length} chỉ số`);
    },
    onError,
  });

  const approve = useMutation({ mutationFn: () => resultService.approve(sampleId!), onSuccess: async () => { await refresh(); message.success('Đã duyệt mẫu'); }, onError });
  const unapprove = useMutation({
    mutationFn: (r: string) => resultService.unapprove(sampleId!, r),
    onSuccess: async () => { setReasonFor(null); await refresh(); message.success('Đã huỷ duyệt — mẫu về chờ duyệt'); },
    onError,
  });
  const acceptPending = useMutation({
    mutationFn: (resultId: string) => resultService.acceptPending(resultId),
    onSuccess: async () => { await refresh(); message.success('Đã dùng giá trị máy gửi lại — mẫu về chờ duyệt'); },
    onError,
  });
  const rejectPending = useMutation({ mutationFn: (resultId: string) => resultService.rejectPending(resultId), onSuccess: refresh, onError });

  const startSave = () => {
    if (dirtyRows.some((r) => r.value)) {
      setReason('');
      setReasonFor('save');
    } else {
      save.mutate(null);
    }
  };

  const focusNext = (orderItemId: string) => {
    const ids = leafRows.map((r) => r.orderItemId);
    const next = ids[ids.indexOf(orderItemId) + 1];
    if (next) inputs.current.get(next)?.focus({ cursor: 'all' });
  };

  const confirmAcceptPending = (row: ResultRow) =>
    modal.confirm({
      title: `Dùng giá trị máy gửi lại cho "${row.serviceName}"?`,
      content: `Giá trị ${row.value ?? '(trống)'} sẽ được thay bằng ${row.pendingValue || '(trống)'}. Mẫu sẽ về trạng thái chờ duyệt để duyệt lại.`,
      okText: 'Dùng giá trị máy', cancelText: 'Hủy bỏ',
      onOk: () => acceptPending.mutateAsync(row.resultId!),
    });

  let no = 0;

  return (
    <Card size="small" className="split-pane"
      title={d ? <span>Phiếu kết quả <span className="code">{d.barcode}</span></span> : 'Phiếu kết quả'}
      extra={d && (
        <Space size={8}>
          <StatusTag domain="sample" value={d.approvedAt ? 'da-duyet' : 'cho-duyet'} />
          {editable && (
            <Button size="small" icon={<SaveOutlined />} type={dirty ? 'primary' : 'default'} disabled={!dirty} loading={save.isPending} onClick={startSave}>
              Lưu{dirty ? ` (${dirtyRows.length})` : ''}
            </Button>
          )}
          {editable && (
            <Tooltip title={dirty ? 'Lưu các giá trị đang gõ trước khi duyệt' : undefined}>
              <Button size="small" icon={<CheckOutlined />} type={dirty ? 'default' : 'primary'} disabled={dirty} loading={approve.isPending} onClick={() => approve.mutate()}>Duyệt</Button>
            </Tooltip>
          )}
          {d.approvedAt && canUnapprove && (
            <Button size="small" icon={<UndoOutlined />} onClick={() => { setReason(''); setReasonFor('unapprove'); }}>Huỷ duyệt</Button>
          )}
        </Space>
      )}>
      {detail.error && <Alert type="error" showIcon message={(detail.error as Error).message} style={{ marginBottom: 12 }} />}
      {!sampleId && <Empty description="Chọn một mẫu ở danh sách bên trái" style={{ margin: '48px 0' }} />}
      {d && (
        <Flex vertical gap={12}>
          <PatientBlock d={d} />
          {d.approvedAt && canEdit && !canUnapprove && (
            <Alert type="info" showIcon message="Mẫu đã duyệt nên không sửa được. Cần sửa thì nhờ Trưởng khoa hoặc Quản trị huỷ duyệt." />
          )}
          <div className="lab-grid-wrap">
            <table className="lab-grid">
              <colgroup>
                <col style={{ width: 40 }} /><col /><col style={{ width: 132 }} /><col style={{ width: 76 }} /><col style={{ width: 128 }} /><col style={{ width: 104 }} /><col style={{ width: 36 }} />
              </colgroup>
              <thead>
                <tr><th>STT</th><th>Chỉ số</th><th>Kết quả</th><th>Đơn vị</th><th>Khoảng tham chiếu</th><th>Đánh giá</th><th aria-label="Lịch sử" /></tr>
              </thead>
              <tbody>
                {d.rows.map((row) => {
                  if (row.isPanelHeader) {
                    return <tr key={row.orderItemId} className="lab-svc"><td colSpan={7}><b>{row.serviceName}</b> <span className="code" style={{ fontWeight: 500 }}>({row.serviceCode})</span></td></tr>;
                  }
                  no += 1;
                  const flag = FLAG[row.flag];
                  const editing = edits[row.orderItemId];
                  const isDirty = editing !== undefined && editing.trim() !== (row.value ?? '');
                  const inputProps = {
                    'aria-label': `Kết quả ${row.serviceName}`,
                    value: editing ?? row.value ?? '',
                    status: isDirty ? ('warning' as const) : undefined,
                    style: { width: '100%', fontWeight: 700 },
                  };
                  const setValue = (v: string) => setEdits((prev) => ({ ...prev, [row.orderItemId]: v }));
                  return (
                    <tr key={row.orderItemId} className={flag?.rowClass ?? ''}>
                      <td className="c muted">{no}</td>
                      <td style={{ paddingLeft: row.depth ? 22 : 8 }}>{row.serviceName}</td>
                      <td>
                        {editable ? (
                          row.valueType === 'dinh-tinh' && row.choices.length > 0 ? (
                            <AutoComplete {...inputProps} size="small" options={row.choices.map((c) => ({ value: c }))} onChange={setValue}
                              filterOption={(input, option) => (option?.value ?? '').toLowerCase().includes(input.toLowerCase())} />
                          ) : (
                            <Input {...inputProps} size="small" className="num" maxLength={500}
                              ref={(el) => { inputs.current.set(row.orderItemId, el); }}
                              onChange={(e) => setValue(e.target.value)} onPressEnter={() => focusNext(row.orderItemId)} />
                          )
                        ) : (
                          <span className="num" style={{ fontWeight: 700 }}>{row.value ?? ''}</span>
                        )}
                        {row.pendingValue !== null && (
                          <Flex align="center" gap={4} style={{ marginTop: 4 }}>
                            <Tooltip title="Máy gửi lại giá trị này sau khi mẫu đã duyệt">
                              <Tag color="warning" icon={<SwapOutlined />} style={{ marginInlineEnd: 0 }}>{row.pendingValue || 'lỗi đọc'}</Tag>
                            </Tooltip>
                            {canEdit && row.resultId && (
                              <>
                                <Button size="small" type="link" style={{ padding: '0 4px' }} onClick={() => confirmAcceptPending(row)}>Dùng</Button>
                                <Tooltip title="Bỏ giá trị máy gửi lại, giữ giá trị hiện tại">
                                  <Button size="small" type="text" icon={<CloseOutlined />} aria-label="Bỏ giá trị máy gửi lại" loading={rejectPending.isPending}
                                    onClick={() => rejectPending.mutate(row.resultId!)} />
                                </Tooltip>
                              </>
                            )}
                          </Flex>
                        )}
                      </td>
                      <td className="muted">{row.unit ?? ''}</td>
                      <td className="muted">{row.refText ?? ''}</td>
                      <td>{flag ? <span className="flag-text">{flag.text}</span> : row.source === 'may' ? <span className="muted">Máy</span> : ''}</td>
                      <td className="c">
                        {row.resultId && (
                          <Tooltip title="Lịch sử của chỉ số này">
                            <Button size="small" type="text" icon={<HistoryOutlined />} aria-label={`Lịch sử ${row.serviceName}`} onClick={() => setHistoryOf(row)} />
                          </Tooltip>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
          {editable && <Typography.Text type="secondary">Gõ giá trị rồi Enter để xuống ô kế. Ô đã đổi có viền vàng; bấm Lưu để ghi. Sửa ô đã có giá trị phải nhập lý do.</Typography.Text>}
        </Flex>
      )}

      <Modal open={reasonFor !== null} destroyOnHidden
        title={reasonFor === 'unapprove' ? 'Huỷ duyệt mẫu' : 'Lý do sửa kết quả'}
        okText={reasonFor === 'unapprove' ? 'Huỷ duyệt' : 'Lưu'} cancelText="Hủy bỏ"
        okButtonProps={{ disabled: reason.trim() === '', danger: reasonFor === 'unapprove' }}
        confirmLoading={save.isPending || unapprove.isPending}
        onCancel={() => setReasonFor(null)}
        onOk={() => (reasonFor === 'unapprove' ? unapprove.mutate(reason.trim()) : save.mutate(reason.trim()))}>
        <Flex vertical gap={8}>
          <Typography.Text>
            {reasonFor === 'unapprove'
              ? 'Mẫu sẽ về trạng thái chờ duyệt để sửa. Lý do được ghi vào lịch sử từng chỉ số.'
              : `Bạn đang sửa ${dirtyRows.filter((r) => r.value).length} chỉ số đã có giá trị. Lý do được ghi vào lịch sử.`}
          </Typography.Text>
          <Input.TextArea aria-label="Lý do" autoFocus rows={3} maxLength={500} value={reason} onChange={(e) => setReason(e.target.value)}
            placeholder={reasonFor === 'unapprove' ? 'vd: Bác sĩ yêu cầu chạy lại mẫu' : 'vd: Nhập nhầm, sửa theo phiếu máy'} />
        </Flex>
      </Modal>

      <HistoryModal row={historyOf} onClose={() => setHistoryOf(null)} />
    </Card>
  );
}

function PatientBlock({ d }: { d: SampleDetail }) {
  return (
    <Descriptions size="small" column={2} bordered
      items={[
        { key: 'name', label: 'Người bệnh', children: <b>{d.patient.name}</b> },
        { key: 'code', label: 'Mã người bệnh', children: <span className="code">{d.patient.code}</span> },
        { key: 'sex', label: 'Giới / sinh', children: `${SEX[d.patient.sex] ?? '?'} · ${d.patient.dob ? dayjs(d.patient.dob).format('DD/MM/YYYY') : d.patient.birthYear ?? '?'}` },
        { key: 'doctor', label: 'BS chỉ định', children: d.order.doctor ?? '—' },
        { key: 'dept', label: 'Khoa / phòng', children: d.order.dept ?? '—' },
        { key: 'diag', label: 'Chẩn đoán', children: d.order.diagnosis ?? '—' },
        ...(d.approvedAt ? [{ key: 'approved', label: 'Duyệt', children: `${d.approvedBy} · ${dayjs(d.approvedAt).format('DD/MM/YYYY HH:mm')}` }] : []),
      ]} />
  );
}

function HistoryModal({ row, onClose }: { row: ResultRow | null; onClose: () => void }) {
  const history = useQuery({ queryKey: qk.resultHistory(row?.orderItemId ?? ''), queryFn: () => resultService.history(row!.orderItemId), enabled: row !== null });
  return (
    <Modal open={row !== null} title={`Lịch sử — ${row?.serviceName ?? ''}`} footer={null} width={760} onCancel={onClose} destroyOnHidden>
      {history.error && <Alert type="error" showIcon message={(history.error as Error).message} />}
      <Table<ResultHistory>
        rowKey={(h) => `${h.at}-${h.action}-${h.newValue}`} size="small" bordered pagination={false} loading={history.isLoading} dataSource={history.data ?? []}
        columns={[
          { title: 'Thời gian', dataIndex: 'at', width: 150, render: (v: string) => <span className="dt">{dayjs(v).format('DD/MM/YYYY HH:mm:ss')}</span> },
          { title: 'Thao tác', dataIndex: 'action', width: 170, render: (v: string) => ACTION[v] ?? v },
          { title: 'Giá trị', render: (_, h) => (h.oldValue !== null ? <span><s className="muted">{h.oldValue || '(trống)'}</s> → <b>{h.newValue ?? '(trống)'}</b></span> : <b>{h.newValue ?? ''}</b>) },
          { title: 'Người thực hiện', dataIndex: 'actorName', width: 150, render: (v: string | null) => v ?? 'Máy / hệ thống' },
          { title: 'Lý do', dataIndex: 'reason', render: (v: string | null) => v ?? '' },
        ]}
      />
    </Modal>
  );
}
