// Điều khiển một agent từ web: ra lệnh qua kênh WebSocket (nạp lại, quét / thử cổng COM, nối lại máy, gửi bù, khởi động lại,
// cập nhật, thu hồi), xem máy xét nghiệm agent đang chạy, cổng COM lần quét cuối và lịch sử lệnh.
import {
  CloudDownloadOutlined, DisconnectOutlined, FileSearchOutlined, PoweroffOutlined, ReloadOutlined, SearchOutlined, SendOutlined, StopOutlined,
} from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, App, Button, Col, Descriptions, Drawer, Empty, Flex, Form, Input, InputNumber, Modal, Row, Select, Space, Table, Tooltip, Typography,
} from 'antd';
import dayjs from 'dayjs';
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  agentService, instrumentService, qk, type Agent, type AgentCommand, type AgentCommandName, type AgentPort, type Instrument, type TestPortArgs,
} from '@/services';
import { StatusTag } from '@/shared/ui/StatusTag';
import { useIsAdmin } from '@/stores/sessionStore';
import { decodeBytes } from './bytes';
import { commandLabel, isPending, summarizeResult } from './commands';
import { pairingState } from './pairing';

const BAUD_RATES = [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200];
const OFFLINE_HINT = 'Agent đang mất kết nối';
const NO_CHANNEL_HINT = 'Kênh lệnh đang đứt';

interface Props {
  agentId: string | null;
  onClose: () => void;
}

/** Cổng cần thử: lấy sẵn thông số từ cấu hình máy (nếu có) để chỉ việc bấm. */
interface TestTarget {
  portName: string;
  settings: Omit<TestPortArgs, 'portName' | 'seconds'>;
}

const sameText = (a: string | null | undefined, b: string | null | undefined) => !!a && !!b && a.toUpperCase() === b.toUpperCase();
const time = (iso: string | null) => (iso ? dayjs(iso).format('HH:mm:ss DD/MM') : '—');

export function AgentDrawer({ agentId, onClose }: Props) {
  const open = agentId !== null;
  const isAdmin = useIsAdmin();
  const navigate = useNavigate();
  const { message, modal } = App.useApp();
  const queryClient = useQueryClient();
  const [watchId, setWatchId] = useState<string | null>(null);
  const [testTarget, setTestTarget] = useState<TestTarget | null>(null);
  const [revoking, setRevoking] = useState(false);

  const agents = useQuery({ queryKey: qk.agents, queryFn: agentService.list, enabled: open, refetchInterval: open ? 2000 : false });
  const instruments = useQuery({ queryKey: qk.instruments, queryFn: instrumentService.list, enabled: open, refetchInterval: open ? 2000 : false });
  const installer = useQuery({ queryKey: qk.agentInstaller, queryFn: agentService.installerInfo, enabled: open && isAdmin });
  const agent = agents.data?.find((a) => a.id === agentId) ?? null;

  const watched = useQuery({
    queryKey: qk.agentCommand(watchId ?? ''),
    queryFn: () => agentService.commandById(watchId!),
    enabled: watchId !== null,
    refetchInterval: (q) => (q.state.data && !isPending(q.state.data.status) ? false : 1000),
  });
  const history = useQuery({
    queryKey: qk.agentCommands(agentId ?? ''),
    queryFn: () => agentService.commands(agentId!),
    enabled: open,
    refetchInterval: (q) => (q.state.data?.some((c) => isPending(c.status)) ? 1000 : 2000),
  });

  // Lệnh vừa xong → làm mới ngay agent (cổng quét được, phiên bản), máy và lịch sử, không chờ vòng hỏi 2 giây.
  const finishedRef = useRef<string | null>(null);
  const current = watched.data;
  useEffect(() => {
    if (!current || isPending(current.status) || finishedRef.current === current.id) return;
    finishedRef.current = current.id;
    void queryClient.invalidateQueries({ queryKey: qk.agents });
    void queryClient.invalidateQueries({ queryKey: qk.instruments });
  }, [current, queryClient]);

  // Đóng drawer / đổi agent thì bỏ theo dõi lệnh cũ.
  useEffect(() => {
    setWatchId(null);
    setTestTarget(null);
    setRevoking(false);
  }, [agentId]);

  const send = useMutation({
    mutationFn: ({ name, args }: { name: AgentCommandName; args?: TestPortArgs | { instrumentId: string } }) =>
      args ? agentService.command(agentId!, name, args) : agentService.command(agentId!, name),
    onSuccess: (cmd) => {
      queryClient.setQueryData(qk.agentCommand(cmd.id), cmd);
      setWatchId(cmd.id);
      void queryClient.invalidateQueries({ queryKey: qk.agentCommands(agentId!) });
    },
    onError: (e) => message.error((e as Error).message),
  });

  const mine = useMemo(() => (instruments.data ?? []).filter((i) => i.agent?.id === agentId), [instruments.data, agentId]);

  if (!open) return null;

  const channel = agent?.channel ?? false;
  // online = còn kênh hoặc báo về trong 90 giây; có thể vẫn "online" (báo về qua HTTP) mà kênh lệnh đứt.
  const disabledHint = channel ? undefined : agent?.online ? NO_CHANNEL_HINT : OFFLINE_HINT;
  const paired = agent?.paired ?? false;
  const runningName = current && isPending(current.status) ? current.name : null;
  const updateTo = installer.data?.available && installer.data.version && agent?.version !== installer.data.version ? installer.data.version : null;

  /** Nút lệnh cần kênh: tắt + gợi ý khi agent mất kết nối. */
  const commandButton = (name: AgentCommandName, icon: ReactNode, onClick: () => void, label: string = commandLabel(name), danger = false) => (
    <Tooltip key={name} title={disabledHint}>
      <Button icon={icon} danger={danger} disabled={!channel || !paired} loading={runningName === name || (send.isPending && send.variables?.name === name)} onClick={onClick}>
        {label}
      </Button>
    </Tooltip>
  );

  const confirmRestart = () =>
    modal.confirm({
      title: 'Khởi động lại agent?',
      content: 'Dịch vụ agent dừng rồi Windows tự bật lại trong khoảng 1 phút. Trong lúc đó các máy xét nghiệm của agent tạm ngắt kết nối.',
      okText: 'Khởi động lại', cancelText: 'Hủy bỏ',
      onOk: () => send.mutateAsync({ name: 'restart' }),
    });

  const confirmUpdate = (version: string) =>
    modal.confirm({
      title: `Cập nhật agent lên ${version}?`,
      content: 'Agent tải bản mới từ máy chủ, kiểm SHA-256 rồi tự khởi động lại. Nếu bản mới không nối lại được trong 2 phút, agent tự quay về bản đang chạy.',
      okText: 'Cập nhật', cancelText: 'Hủy bỏ',
      onOk: () => send.mutateAsync({ name: 'update' }),
    });

  const testFromInstrument = (i: Instrument) => {
    const c = i.connection;
    setTestTarget({ portName: c.portName!, settings: { baudRate: c.baudRate ?? 9600, dataBits: c.dataBits ?? 8, parity: c.parity ?? 'None', stopBits: c.stopBits ?? 1 } });
  };
  const testFromPort = (p: AgentPort) => {
    const owner = mine.find((i) => i.connection.kind === 'serial' && sameText(i.connection.portName, p.name));
    if (owner) testFromInstrument(owner);
    else setTestTarget({ portName: p.name, settings: { baudRate: 9600, dataBits: 8, parity: 'None', stopBits: 1 } });
  };

  return (
    <Drawer open width={720} title={agent ? `Agent: ${agent.name}` : 'Agent'} onClose={onClose} destroyOnHidden
      extra={agent && <Button icon={<FileSearchOutlined />} onClick={() => navigate(`/log-agent?agentId=${agent.id}`)}>Xem log</Button>}>
      {!agent ? (
        agents.isLoading ? null : <Empty description="Không tìm thấy agent" />
      ) : (
        <Flex vertical gap={20}>
          <Descriptions size="small" column={2} bordered
            items={[
              { key: 's', label: 'Trạng thái', children: <StatusTag domain="agent" value={pairingState(agent, new Date())} /> },
              { key: 'l', label: 'Báo về lần cuối', children: time(agent.lastSeenAt) },
              { key: 'm', label: 'Máy tính', children: agent.machineName ? <span className="code">{agent.machineName}</span> : '—' },
              { key: 'v', label: 'Phiên bản', children: agent.version ?? '—' },
            ]}
          />

          {!paired && <Alert type="info" showIcon message="Agent chưa ghép nối — ghép nối xong mới điều khiển được." />}
          {paired && !channel && (
            <Alert type="warning" showIcon icon={<DisconnectOutlined />}
              message={`${disabledHint} — các lệnh tạm tắt`}
              description={agent.online
                ? 'Agent vừa tắt / mất mạng thì khoảng 90 giây sau sẽ chuyển "Mất kết nối". Nếu agent vẫn báo về mà kênh lệnh không nối được, kiểm tra reverse proxy có cho WebSocket đi qua (xem tài liệu cài đặt).'
                : 'Kiểm tra máy tính agent còn bật, còn mạng, và dịch vụ LIS Agent đang chạy. Kênh lệnh tự nối lại khi agent chạy.'} />
          )}

          {isAdmin && paired && (
            <Space wrap size={8}>
              {commandButton('reload-config', <ReloadOutlined />, () => send.mutate({ name: 'reload-config' }))}
              {commandButton('scan-ports', <SearchOutlined />, () => send.mutate({ name: 'scan-ports' }))}
              {commandButton('flush-pending', <SendOutlined />, () => send.mutate({ name: 'flush-pending' }))}
              {commandButton('restart', <PoweroffOutlined />, confirmRestart)}
              {updateTo && commandButton('update', <CloudDownloadOutlined />, () => confirmUpdate(updateTo), `Cập nhật lên ${updateTo}`)}
              <Button danger icon={<StopOutlined />} onClick={() => setRevoking(true)}>Thu hồi</Button>
            </Space>
          )}

          {current && <CommandResult cmd={current} />}

          <Section title="Máy xét nghiệm">
            <Table<Instrument>
              rowKey="id" size="small" bordered pagination={false} dataSource={mine} loading={instruments.isLoading}
              locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Agent chưa phụ trách máy nào — gán ở mục Máy &amp; map kênh" /> }}
              columns={[
                { title: 'Máy', width: 170, render: (_, i) => <Space direction="vertical" size={0}><b>{i.name}</b><Typography.Text type="secondary" style={{ fontSize: 12 }}>{i.connectionText ?? 'Chưa cấu hình kết nối'}</Typography.Text></Space> },
                { title: 'Đường truyền', render: (_, i) => (i.status ? <Space direction="vertical" size={0}><StatusTag domain="link" value={i.status.state} /><Typography.Paragraph type="secondary" style={{ fontSize: 12, margin: 0 }} ellipsis={{ rows: 2, tooltip: i.status.text }}>{i.status.text}</Typography.Paragraph></Space> : '—') },
                { title: 'Nhận lần cuối', width: 100, render: (_, i) => time(i.status?.lastResultAt ?? null) },
                { title: 'Bản tin', width: 70, align: 'right', render: (_, i) => <Tooltip title="Số bản tin đã lưu xuống đĩa từ lúc agent chạy">{i.status?.samplesReceived ?? 0}</Tooltip> },
                { title: 'Chờ gửi', width: 70, align: 'right', render: (_, i) => <Tooltip title="Số bản tin agent còn giữ, chưa gửi lên máy chủ">{i.status?.pendingToRetry ?? 0}</Tooltip> },
                ...(isAdmin && paired ? [{
                  title: '', width: 92, align: 'right' as const,
                  render: (_: unknown, i: Instrument) => (
                    <Space direction="vertical" size={4} align="end">
                      <Tooltip title={disabledHint}>
                        <Button size="small" disabled={!channel} onClick={() => send.mutate({ name: 'reconnect-instrument', args: { instrumentId: i.id } })}>Nối lại</Button>
                      </Tooltip>
                      {i.connection.kind === 'serial' && i.connection.portName && (
                        <Tooltip title={disabledHint}>
                          <Button size="small" disabled={!channel} onClick={() => testFromInstrument(i)}>Thử cổng</Button>
                        </Tooltip>
                      )}
                    </Space>
                  ),
                }] : []),
              ]}
            />
          </Section>

          <Section title="Cổng COM" extra={agent.portsScannedAt ? `Quét lúc ${time(agent.portsScannedAt)}` : 'Chưa quét lần nào'}>
            {agent.ports === null ? (
              <Typography.Text type="secondary">Bấm "Quét cổng COM" để xem các cổng trên máy tính agent.</Typography.Text>
            ) : (
              <Table<AgentPort>
                rowKey="name" size="small" bordered pagination={false} dataSource={agent.ports}
                locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Máy tính agent không có cổng COM nào — kiểm tra cáp / driver USB-COM" /> }}
                columns={[
                  { title: 'Cổng', width: 90, render: (_, p) => <b className="code">{p.name}</b> },
                  { title: 'Thiết bị', render: (_, p) => p.description ?? <Typography.Text type="secondary">—</Typography.Text> },
                  { title: 'Tình trạng', width: 200, render: (_, p) => (p.usedBy ? `Máy ${p.usedBy} đang dùng` : p.busy ? <Typography.Text type="warning">Phần mềm khác đang giữ</Typography.Text> : 'Trống') },
                  ...(isAdmin && paired ? [{
                    title: '', width: 96, align: 'right' as const,
                    render: (_: unknown, p: AgentPort) => (
                      <Tooltip title={disabledHint}>
                        <Button size="small" disabled={!channel} onClick={() => testFromPort(p)}>Thử cổng</Button>
                      </Tooltip>
                    ),
                  }] : []),
                ]}
              />
            )}
          </Section>

          <Section title="Lịch sử lệnh">
            <Table<AgentCommand>
              rowKey="id" size="small" bordered pagination={{ pageSize: 10, hideOnSinglePage: true, size: 'small' }} dataSource={history.data ?? []} loading={history.isLoading}
              locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Chưa ra lệnh nào" /> }}
              expandable={{ expandRowByClick: true, expandedRowRender: (c) => <CommandDetail cmd={c} /> }}
              columns={[
                {
                  title: 'Lệnh',
                  render: (_, c) => (
                    <Space direction="vertical" size={0}>
                      <Space size={6}><b>{commandLabel(c.name)}</b>{argsHint(c, mine)}</Space>
                      {summarizeResult(c) && <Typography.Text type={c.status === 'failed' ? 'danger' : 'secondary'} style={{ fontSize: 12 }}>{summarizeResult(c)}</Typography.Text>}
                    </Space>
                  ),
                },
                { title: 'Lúc', width: 130, render: (_, c) => <Space direction="vertical" size={0}><span>{time(c.requestedAt)}</span><Typography.Text type="secondary" style={{ fontSize: 12 }}>{c.requestedBy ?? '—'}</Typography.Text></Space> },
                { title: 'Trạng thái', width: 150, render: (_, c) => <StatusTag domain="command" value={c.status} /> },
              ]}
            />
          </Section>
        </Flex>
      )}

      {agent && (
        <TestPortModal target={testTarget} agent={agent} instruments={mine} loading={send.isPending}
          onCancel={() => setTestTarget(null)}
          onSubmit={(args) => send.mutateAsync({ name: 'test-port', args }).then(() => setTestTarget(null))} />
      )}
      {agent && (
        <RevokeModal open={revoking} agentName={agent.name} loading={send.isPending} onCancel={() => setRevoking(false)}
          onConfirm={() => send.mutateAsync({ name: 'revoke' }).then(() => setRevoking(false))} />
      )}
    </Drawer>
  );
}

function Section({ title, extra, children }: { title: string; extra?: string; children: ReactNode }) {
  return (
    <section>
      <Flex justify="space-between" align="baseline" style={{ marginBottom: 8 }}>
        <Typography.Title level={5} style={{ margin: 0 }}>{title}</Typography.Title>
        {extra && <Typography.Text type="secondary" style={{ fontSize: 12 }}>{extra}</Typography.Text>}
      </Flex>
      {children}
    </section>
  );
}

function argsHint(c: AgentCommand, instruments: Instrument[]): ReactNode {
  if (c.name === 'test-port' && typeof c.args?.portName === 'string') {
    return <Typography.Text type="secondary" style={{ fontSize: 12 }}>{c.args.portName} · {String(c.args.seconds)} giây</Typography.Text>;
  }
  if (c.name === 'reconnect-instrument') {
    const name = instruments.find((i) => i.id === c.args?.instrumentId)?.name;
    return name ? <Typography.Text type="secondary" style={{ fontSize: 12 }}>{name}</Typography.Text> : null;
  }
  return null;
}

/** Dữ liệu máy gửi khi thử cổng, đổi sang chữ đọc được (ký tự điều khiển thành &lt;STX&gt;…). */
function receivedText(cmd: AgentCommand): string | null {
  const hex = cmd.name === 'test-port' && typeof cmd.result?.hex === 'string' ? cmd.result.hex : '';
  return hex ? decodeBytes(hex) : null;
}

/** Lệnh vừa ra: trạng thái + tóm tắt, cập nhật mỗi giây tới khi xong. */
function CommandResult({ cmd }: { cmd: AgentCommand }) {
  const summary = summarizeResult(cmd);
  const text = receivedText(cmd);
  const type = cmd.status === 'done' ? 'success' : cmd.status === 'failed' ? 'error' : isPending(cmd.status) ? 'info' : 'warning';
  return (
    <Alert type={type} showIcon={false}
      message={<Space size={8} wrap><b>{commandLabel(cmd.name)}</b><StatusTag domain="command" value={cmd.status} />{summary && <span>{summary}</span>}</Space>}
      description={text ? <pre className="raw-bytes" style={{ maxHeight: 220, overflow: 'auto', margin: 0, fontSize: 12, whiteSpace: 'pre-wrap' }}>{text}</pre> : undefined}
    />
  );
}

function CommandDetail({ cmd }: { cmd: AgentCommand }) {
  const text = receivedText(cmd);
  const { hex: _hex, ...rest } = cmd.result ?? {};
  const shown = { args: cmd.args, result: cmd.result ? (text ? rest : cmd.result) : null, error: cmd.error, startedAt: cmd.startedAt, finishedAt: cmd.finishedAt };
  return (
    <Flex vertical gap={8}>
      <pre style={{ margin: 0, fontSize: 12, whiteSpace: 'pre-wrap' }}>{JSON.stringify(shown, null, 2)}</pre>
      {text && (
        <>
          <Typography.Text type="secondary">Dữ liệu nhận được:</Typography.Text>
          <pre style={{ margin: 0, fontSize: 12, maxHeight: 240, overflow: 'auto', whiteSpace: 'pre-wrap' }}>{text}</pre>
        </>
      )}
    </Flex>
  );
}

interface TestForm {
  baudRate: number;
  dataBits: number;
  parity: string;
  stopBits: number;
  seconds: number;
}

function TestPortModal({ target, agent, instruments, loading, onCancel, onSubmit }: {
  target: TestTarget | null;
  agent: Agent;
  instruments: Instrument[];
  loading: boolean;
  onCancel: () => void;
  onSubmit: (args: TestPortArgs) => Promise<void>;
}) {
  const [form] = Form.useForm<TestForm>();
  const seconds = Form.useWatch('seconds', form) ?? 10;

  useEffect(() => {
    if (target) form.setFieldsValue({ ...target.settings, seconds: 10 });
  }, [target, form]);

  if (!target) return null;

  const users = new Set(instruments.filter((i) => i.active && i.connection.kind === 'serial' && sameText(i.connection.portName, target.portName)).map((i) => i.name));
  const port = agent.ports?.find((p) => sameText(p.name, target.portName));
  if (port?.usedBy) users.add(port.usedBy);
  const names = [...users];

  return (
    <Modal open title={`Thử cổng ${target.portName}`} okText={names.length > 0 ? 'Vẫn thử' : 'Thử'} cancelText="Hủy bỏ" destroyOnHidden
      confirmLoading={loading} onCancel={onCancel} onOk={() => form.submit()}>
      <Flex vertical gap={12}>
        <Typography.Text type="secondary">
          Agent mở cổng và chỉ nghe, không gửi gì tới máy. Trong lúc thử, cho máy xét nghiệm gửi lại một kết quả để thấy dữ liệu về.
        </Typography.Text>
        {names.length > 0 && (
          <Alert type="warning" showIcon message={`Máy ${names.join(', ')} sẽ tạm ngắt trong ${seconds} giây`} description="Thử xong agent tự nối lại máy." />
        )}
        {port?.busy && !port.usedBy && <Alert type="info" showIcon message="Phần mềm khác đang giữ cổng này — lần thử có thể báo cổng bận." />}
        <Form form={form} layout="vertical" requiredMark={false}
          onFinish={(v) => onSubmit({ portName: target.portName, baudRate: v.baudRate, dataBits: v.dataBits, parity: v.parity, stopBits: v.stopBits, seconds: v.seconds })}>
          <Row gutter={12}>
            <Col span={8}><Form.Item name="baudRate" label="Tốc độ baud"><Select options={BAUD_RATES.map((b) => ({ value: b, label: String(b) }))} /></Form.Item></Col>
            <Col span={8}><Form.Item name="dataBits" label="Bit dữ liệu"><Select options={[{ value: 8, label: '8' }, { value: 7, label: '7' }]} /></Form.Item></Col>
            <Col span={8}>
              <Form.Item name="parity" label="Parity">
                <Select options={[{ value: 'None', label: 'None (không)' }, { value: 'Even', label: 'Even (chẵn)' }, { value: 'Odd', label: 'Odd (lẻ)' }]} />
              </Form.Item>
            </Col>
            <Col span={8}><Form.Item name="stopBits" label="Stop bit"><Select options={[{ value: 1, label: '1' }, { value: 2, label: '2' }]} /></Form.Item></Col>
            <Col span={8}>
              <Form.Item name="seconds" label="Nghe trong (giây)" rules={[{ required: true, type: 'number', min: 1, max: 60, message: 'Từ 1 đến 60 giây' }]}>
                <InputNumber min={1} max={60} precision={0} style={{ width: '100%' }} autoFocus onPressEnter={() => form.submit()} />
              </Form.Item>
            </Col>
          </Row>
        </Form>
      </Flex>
    </Modal>
  );
}

function RevokeModal({ open, agentName, loading, onCancel, onConfirm }: {
  open: boolean;
  agentName: string;
  loading: boolean;
  onCancel: () => void;
  onConfirm: () => Promise<void>;
}) {
  const [typed, setTyped] = useState('');
  useEffect(() => {
    if (open) setTyped('');
  }, [open]);
  const matches = typed.trim() === agentName;

  return (
    <Modal open={open} title={`Thu hồi agent "${agentName}"?`} okText="Thu hồi agent" cancelText="Hủy bỏ" destroyOnHidden
      okButtonProps={{ danger: true, disabled: !matches }} confirmLoading={loading} onCancel={onCancel} onOk={() => matches && onConfirm()}>
      <Flex vertical gap={12}>
        <Typography.Paragraph style={{ margin: 0 }}>
          Máy chủ khoá agent ngay; agent đang chạy sẽ dừng dịch vụ và không gửi được dữ liệu nữa. Dữ liệu chờ gửi trên máy tính agent vẫn giữ.
          Muốn dùng lại phải bấm "Ghép nối lại" và chạy tệp cài mới.
        </Typography.Paragraph>
        <Typography.Text>Gõ đúng tên agent để xác nhận:</Typography.Text>
        <Input value={typed} onChange={(e) => setTyped(e.target.value)} placeholder={agentName} autoFocus
          onPressEnter={() => matches && onConfirm()} />
      </Flex>
    </Modal>
  );
}
