// Agent: máy tính đặt cạnh máy xét nghiệm, nối cổng COM/TCP và gửi dữ liệu về server.
// Màn này tạo agent (lấy khoá cài đặt), đổi tên / mã máy trạm, cấp khoá mới, xoá, và xem agent đang chạy hay không.
import { CopyOutlined, FileSearchOutlined, KeyOutlined, PlusOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, Button, Card, Empty, Flex, Form, Input, Modal, Space, Table, Tag, Typography } from 'antd';
import dayjs from 'dayjs';
import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { agentService, qk, type Agent } from '@/services';
import { DeleteButton } from '@/shared/ui/DeleteButton';
import { StatusTag } from '@/shared/ui/StatusTag';
import { useIsAdmin } from '@/stores/sessionStore';

interface AgentForm {
  name: string;
  workstationCode: string;
}

export default function AgentsPage() {
  const isAdmin = useIsAdmin();
  const { message } = App.useApp();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [form] = Form.useForm<AgentForm>();
  const [editing, setEditing] = useState<Agent | 'new' | null>(null);
  // Khoá vừa cấp — chỉ hiện một lần trong hộp thoại này.
  const [issued, setIssued] = useState<{ agentName: string; key: string } | null>(null);

  const agents = useQuery({ queryKey: qk.agents, queryFn: agentService.list, refetchInterval: 10_000 });
  const refresh = () => Promise.all([queryClient.invalidateQueries({ queryKey: qk.agents }), queryClient.invalidateQueries({ queryKey: qk.instruments })]);

  const save = useMutation({
    mutationFn: async (values: AgentForm) => {
      if (editing === 'new') {
        const created = await agentService.create(values);
        return { key: created.key, name: values.name };
      }
      await agentService.update((editing as Agent).id, values);
      return null;
    },
    onSuccess: async (created) => {
      setEditing(null);
      await refresh();
      if (created) setIssued({ agentName: created.name, key: created.key });
      else message.success('Đã lưu agent');
    },
    onError: (e) => message.error((e as Error).message),
  });

  const regenerate = useMutation({
    mutationFn: (agent: Agent) => agentService.regenerateKey(agent.id).then((r) => ({ agentName: agent.name, key: r.key })),
    onSuccess: setIssued,
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
    form.setFieldsValue(agent === 'new' ? { name: '', workstationCode: '' } : { name: agent.name, workstationCode: agent.workstationCode });
  };

  const { modal } = App.useApp();
  const confirmRegenerate = (agent: Agent) =>
    modal.confirm({
      title: `Cấp khoá mới cho "${agent.name}"?`,
      content: 'Khoá cũ hết hiệu lực ngay: agent đang chạy sẽ mất kết nối với máy chủ cho tới khi bạn dán khoá mới vào cấu hình của nó.',
      okText: 'Cấp khoá mới', cancelText: 'Hủy bỏ', okButtonProps: { danger: true },
      onOk: () => regenerate.mutateAsync(agent),
    });

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
            Cấu hình máy và map chỉ số làm ở mục "Máy &amp; map kênh" — agent tự nhận cấu hình mới trong vòng 30 giây.
          </Typography.Text>
          {agents.error && <Alert type="error" showIcon message={(agents.error as Error).message} />}
          <Table<Agent>
            rowKey="id" size="small" bordered pagination={false} loading={agents.isLoading} dataSource={agents.data ?? []} scroll={{ x: 900 }}
            locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Chưa có agent nào — bấm Thêm agent để lấy khoá cài đặt" /> }}
            columns={[
              { title: 'Tên agent', dataIndex: 'name', render: (v: string) => <b>{v}</b> },
              { title: 'Mã máy trạm', dataIndex: 'workstationCode', width: 190, render: (v: string) => <span className="code">{v}</span> },
              {
                title: 'Trạng thái', width: 250,
                render: (_, row) => (
                  <Space direction="vertical" size={0}>
                    <StatusTag domain="agent" value={row.online ? 'online' : row.lastSeenAt ? 'offline' : 'never'} />
                    {row.lastSeenAt && <Typography.Text type="secondary" style={{ fontSize: 12 }}>Báo về lần cuối {dayjs(row.lastSeenAt).format('DD/MM/YYYY HH:mm:ss')}</Typography.Text>}
                  </Space>
                ),
              },
              {
                title: 'Máy xét nghiệm phụ trách',
                render: (_, row) =>
                  row.instruments.length === 0
                    ? <Typography.Text type="secondary">Chưa gán máy nào</Typography.Text>
                    : (
                      <Space size={[6, 6]} wrap>
                        {row.instruments.map((i) => (
                          <Tag key={i.id} color={i.status?.state === 'Connected' ? 'success' : i.status?.state === 'Error' ? 'error' : undefined} style={{ marginInlineEnd: 0 }}>
                            {i.name}{i.status ? ` · ${i.status.text}` : ''}
                          </Tag>
                        ))}
                      </Space>
                    ),
              },
              {
                title: '', width: isAdmin ? 300 : 110, align: 'right',
                render: (_, row) => (
                  <Space size={4}>
                    <Button size="small" icon={<FileSearchOutlined />} onClick={() => navigate(`/log-agent?agentId=${row.id}`)}>Log</Button>
                    {isAdmin && <Button size="small" onClick={() => openForm(row)}>Sửa</Button>}
                    {isAdmin && <Button size="small" icon={<KeyOutlined />} onClick={() => confirmRegenerate(row)}>Khoá mới</Button>}
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
        <Form form={form} layout="vertical" onFinish={(v) => save.mutate({ name: v.name.trim(), workstationCode: v.workstationCode.trim() })}>
          <Form.Item name="name" label="Tên agent" rules={[{ required: true, whitespace: true, message: 'Nhập tên agent' }]}
            extra="Đặt theo vị trí để dễ nhận, vd: Máy tính phòng xét nghiệm tầng 2.">
            <Input autoFocus maxLength={100} />
          </Form.Item>
          <Form.Item name="workstationCode" label="Mã máy trạm"
            rules={[{ required: true, message: 'Nhập mã máy trạm' }, { pattern: /^[A-Za-z0-9._-]+$/, message: 'Chỉ gồm chữ không dấu, số, dấu chấm, gạch ngang, gạch dưới' }]}
            extra="Mã ngắn để gán máy xét nghiệm cho agent này, vd: XN-01. Đổi mã thì các máy đang gán tự đi theo.">
            <Input maxLength={64} style={{ textTransform: 'uppercase' }} />
          </Form.Item>
        </Form>
      </Modal>

      <Modal open={issued !== null} title={`Khoá cài đặt của "${issued?.agentName ?? ''}"`} footer={<Button type="primary" onClick={() => setIssued(null)}>Tôi đã lưu khoá</Button>}
        closable={false} maskClosable={false} keyboard={false} width={680}>
        {issued && (
          <Flex vertical gap={12}>
            <Alert type="warning" showIcon message="Khoá chỉ hiện một lần. Đóng hộp thoại này là không xem lại được — nếu mất phải cấp khoá mới." />
            <Input.TextArea value={issued.key} readOnly autoSize className="code" aria-label="Khoá agent" />
            <Button icon={<CopyOutlined />} onClick={async () => {
              try {
                await navigator.clipboard.writeText(issued.key);
                message.success('Đã chép khoá');
              } catch {
                message.warning('Trình duyệt không cho chép tự động — bôi đen khoá rồi Ctrl+C');
              }
            }}>Chép khoá</Button>
            <Typography.Text>Trên máy tính đặt agent, mở tệp <span className="code">appsettings.json</span> cạnh <span className="code">LisAgent.exe</span> và điền:</Typography.Text>
            <pre className="code" style={{ margin: 0, padding: 12, background: 'var(--c-muted)', borderRadius: 8, whiteSpace: 'pre-wrap', wordBreak: 'break-all', fontWeight: 500 }}>
{`{
  "Server": {
    "Url": "${window.location.origin}",
    "AgentKey": "${issued.key}"
  }
}`}
            </pre>
            <Typography.Text type="secondary">Sau đó khởi động lại dịch vụ "LIS Agent". Trong vòng một phút, agent sẽ hiện "Đang chạy" ở danh sách.</Typography.Text>
          </Flex>
        )}
      </Modal>
    </div>
  );
}
