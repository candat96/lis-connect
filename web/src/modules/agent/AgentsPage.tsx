// Agent: máy tính đặt cạnh máy xét nghiệm, nối cổng COM/TCP và gửi dữ liệu về server.
// Màn này thêm agent bằng mã ghép nối (tải tệp cài → chạy là xong), đổi tên, ghép nối lại, xoá, xem agent đang kết nối hay không,
// và mở bảng điều khiển (lệnh qua kênh WebSocket) bằng nút "Điều khiển" hoặc bấm vào dòng.
import { ControlOutlined, LinkOutlined, PlusOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, Button, Card, Empty, Flex, Form, Input, Modal, Space, Table, Tag, Tooltip, Typography } from 'antd';
import dayjs from 'dayjs';
import { useState } from 'react';
import { agentService, qk, type Agent } from '@/services';
import { DeleteButton } from '@/shared/ui/DeleteButton';
import { StatusTag } from '@/shared/ui/StatusTag';
import { useIsAdmin } from '@/stores/sessionStore';
import { AgentDrawer } from './AgentDrawer';
import { PairingDialog, type PairingTarget } from './PairingDialog';
import { pairingState } from './pairing';

// Nhãn ngắn trạng thái đường truyền trong bảng; câu đầy đủ hiện khi rê chuột.
const LINK_LABEL: Record<string, string> = { Connected: 'Đã kết nối', Connecting: 'Đang chờ máy', Error: 'Lỗi kết nối', NotRunning: 'Không chạy' };

interface AgentForm {
  name: string;
}

export default function AgentsPage() {
  const isAdmin = useIsAdmin();
  const { message, modal } = App.useApp();
  const queryClient = useQueryClient();
  const [form] = Form.useForm<AgentForm>();
  const [editing, setEditing] = useState<Agent | 'new' | null>(null);
  const [pairing, setPairing] = useState<PairingTarget | null>(null);
  const [controlId, setControlId] = useState<string | null>(null);

  const agents = useQuery({ queryKey: qk.agents, queryFn: agentService.list, refetchInterval: 10_000 });
  const refresh = () => Promise.all([queryClient.invalidateQueries({ queryKey: qk.agents }), queryClient.invalidateQueries({ queryKey: qk.instruments })]);

  const save = useMutation({
    mutationFn: async (values: AgentForm): Promise<PairingTarget | null> => {
      if (editing === 'new') {
        const created = await agentService.create(values);
        return { agentId: created.id, agentName: values.name, ticket: created.pairing, baselinePairedAt: null };
      }
      await agentService.update((editing as Agent).id, values);
      return null;
    },
    onSuccess: async (created) => {
      setEditing(null);
      if (created) setPairing(created);
      else message.success('Đã lưu agent');
      await refresh();
    },
    onError: (e) => message.error((e as Error).message),
  });

  const pair = useMutation({
    mutationFn: async (agent: { id: string; name: string; pairedAt: string | null }): Promise<PairingTarget> => ({
      agentId: agent.id, agentName: agent.name, ticket: await agentService.pair(agent.id), baselinePairedAt: agent.pairedAt,
    }),
    onSuccess: async (target) => {
      setPairing(target);
      await refresh();
    },
    onError: (e) => message.error((e as Error).message),
  });

  const remove = useMutation({
    mutationFn: (agent: Agent) => agentService.remove(agent.id),
    onSuccess: async () => {
      await refresh();
      message.success('Đã xoá agent');
    },
    onError: (e) => message.error((e as Error).message),
  });

  const openForm = (agent: Agent | 'new') => {
    setEditing(agent);
    form.setFieldsValue({ name: agent === 'new' ? '' : agent.name });
  };

  const confirmPair = (agent: Agent) => {
    // Agent chưa ghép nối thì cấp mã mới luôn, không cần hỏi.
    if (!agent.paired) {
      pair.mutate(agent);
      return;
    }
    modal.confirm({
      title: `Ghép nối lại "${agent.name}"?`,
      content: 'Dùng khi cài lại Windows, thay máy tính, hoặc chuyển agent sang máy khác. Khoá đang dùng vẫn chạy cho tới khi mã mới được dùng.',
      okText: 'Tạo mã ghép nối', cancelText: 'Hủy bỏ',
      onOk: () => pair.mutateAsync(agent),
    });
  };

  const now = new Date();

  return (
    <div data-screen="agent">
      <div className="page-header">
        <h1 className="page-title">Agent kết nối máy</h1>
        {isAdmin && <Button type="primary" icon={<PlusOutlined />} onClick={() => openForm('new')}>Thêm agent</Button>}
      </div>

      <Card size="small">
        <Flex vertical gap={12}>
          <Typography.Text type="secondary">
            Agent là phần mềm cài trên máy tính đặt cạnh máy xét nghiệm. Nó mở cổng COM / TCP tới máy và gửi mọi dữ liệu nhận được về máy chủ.
            Thêm agent → tải tệp cài → chạy trên máy tính đó là xong. Cấu hình máy và map chỉ số làm ở mục "Máy &amp; map kênh".
          </Typography.Text>
          {agents.error && <Alert type="error" showIcon message={(agents.error as Error).message} />}
          <Table<Agent>
            rowKey="id" size="small" bordered pagination={false} loading={agents.isLoading} dataSource={agents.data ?? []} scroll={{ x: 1100 }}
            rowClassName={() => 'row-clickable'}
            onRow={(row) => ({
              // Bấm vào dòng mở bảng điều khiển; bấm nút trong dòng thì để nút xử lý.
              onClick: (e) => { if (!(e.target as HTMLElement).closest('button, a, .ant-popover')) setControlId(row.id); },
            })}
            locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Chưa có agent nào — bấm Thêm agent để lấy tệp cài" /> }}
            columns={[
              { title: 'Tên agent', dataIndex: 'name', width: 200, render: (v: string) => <b>{v}</b> },
              {
                title: 'Trạng thái', width: 230,
                render: (_, row) => {
                  const state = pairingState(row, now);
                  return (
                    <Space direction="vertical" size={0}>
                      <StatusTag domain="agent" value={state} />
                      {state === 'pairing' && row.pairingExpiresAt && (
                        <Typography.Text type="secondary" style={{ fontSize: 12 }}>Mã hết hạn lúc {dayjs(row.pairingExpiresAt).format('HH:mm')}</Typography.Text>
                      )}
                      {row.paired && row.lastSeenAt && (
                        <Typography.Text type="secondary" style={{ fontSize: 12 }}>Báo về lần cuối {dayjs(row.lastSeenAt).format('DD/MM/YYYY HH:mm:ss')}</Typography.Text>
                      )}
                    </Space>
                  );
                },
              },
              {
                title: 'Máy tính', width: 190,
                render: (_, row) => row.machineName
                  ? <Space direction="vertical" size={0}><span className="code">{row.machineName}</span>{row.version && <Typography.Text type="secondary" style={{ fontSize: 12 }}>Phiên bản {row.version}</Typography.Text>}</Space>
                  : <Typography.Text type="secondary">—</Typography.Text>,
              },
              {
                title: 'Máy xét nghiệm phụ trách',
                render: (_, row) =>
                  row.instruments.length === 0
                    ? <Typography.Text type="secondary">Chưa gán máy nào</Typography.Text>
                    : (
                      <Space size={[6, 6]} wrap>
                        {row.instruments.map((i) => (
                          <Tooltip key={i.id} title={i.status?.text}>
                            <Tag color={i.status?.state === 'Connected' ? 'success' : i.status?.state === 'Error' ? 'error' : undefined} style={{ marginInlineEnd: 0 }}>
                              {i.name}{i.status ? ` · ${LINK_LABEL[i.status.state] ?? i.status.state}` : ''}
                            </Tag>
                          </Tooltip>
                        ))}
                      </Space>
                    ),
              },
              {
                title: '', width: isAdmin ? 350 : 120, align: 'right',
                render: (_, row) => (
                  <Space size={4}>
                    <Button size="small" icon={<ControlOutlined />} onClick={() => setControlId(row.id)}>Điều khiển</Button>
                    {isAdmin && <Button size="small" onClick={() => openForm(row)}>Sửa</Button>}
                    {isAdmin && (
                      <Button size="small" icon={<LinkOutlined />} loading={pair.isPending && pair.variables?.id === row.id} onClick={() => confirmPair(row)}>
                        Ghép nối lại
                      </Button>
                    )}
                    {isAdmin && (
                      <DeleteButton title={`Xoá agent "${row.name}"?`} description="Agent này sẽ không còn gửi được dữ liệu về máy chủ."
                        onConfirm={() => remove.mutateAsync(row)} />
                    )}
                  </Space>
                ),
              },
            ]}
          />
        </Flex>
      </Card>

      <Modal open={editing !== null} title={editing === 'new' ? 'Thêm agent' : 'Sửa agent'} okText="Lưu" cancelText="Hủy bỏ" destroyOnHidden
        confirmLoading={save.isPending} onCancel={() => setEditing(null)} onOk={() => form.submit()}>
        <Form form={form} layout="vertical" onFinish={(v) => save.mutate({ name: v.name.trim() })}>
          <Form.Item name="name" label="Tên agent" rules={[{ required: true, whitespace: true, message: 'Nhập tên agent' }]}
            extra="Đặt theo vị trí để dễ nhận, vd: Máy tính phòng xét nghiệm tầng 2.">
            <Input autoFocus maxLength={100} />
          </Form.Item>
        </Form>
      </Modal>

      <AgentDrawer agentId={controlId} onClose={() => setControlId(null)} />

      <PairingDialog target={pairing} onClose={() => setPairing(null)} newCodeLoading={pair.isPending}
        onNewCode={() => pairing && pair.mutate({ id: pairing.agentId, name: pairing.agentName, pairedAt: pairing.baselinePairedAt })} />
    </div>
  );
}
