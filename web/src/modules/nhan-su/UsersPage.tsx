// Nhân sự: tài khoản đăng nhập của nhân viên phòng xét nghiệm — thêm, sửa vai trò, khoá / mở, đặt lại mật khẩu.
// Chỉ Quản trị thấy màn này. Luôn phải còn ít nhất một Quản trị đang hoạt động (máy chủ chặn).
import { PlusOutlined, SearchOutlined } from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, Button, Card, Checkbox, Empty, Flex, Form, Input, Modal, Space, Switch, Table, Tag, Typography } from 'antd';
import dayjs from 'dayjs';
import { useMemo, useState } from 'react';
import { qk, ROLE_OPTIONS, roleLabel, userService, type User } from '@/services';
import { StatusTag } from '@/shared/ui/StatusTag';
import { useSessionStore } from '@/stores/sessionStore';

interface UserForm {
  username: string;
  fullName: string;
  title?: string;
  roles: string[];
  active: boolean;
  password?: string;
}

// Bỏ dấu để tìm "nguyen" ra "Nguyễn".
const fold = (text: string) => text.normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/đ/g, 'd').replace(/Đ/g, 'D').toLowerCase();

const userState = (user: User) => (!user.active ? 'disabled' : user.lockedUntil ? 'locked' : 'active');

export default function UsersPage() {
  const { message } = App.useApp();
  const queryClient = useQueryClient();
  const myId = useSessionStore((s) => s.user?.id);
  const [form] = Form.useForm<UserForm>();
  const [editing, setEditing] = useState<User | 'new' | null>(null);
  const [search, setSearch] = useState('');

  const users = useQuery({ queryKey: qk.users, queryFn: userService.list });
  const rows = useMemo(() => {
    const needle = fold(search.trim());
    return (users.data ?? []).filter((u) => needle === '' || fold(`${u.username} ${u.fullName} ${u.title ?? ''}`).includes(needle));
  }, [users.data, search]);

  const openForm = (user: User | 'new') => {
    setEditing(user);
    form.resetFields();
    form.setFieldsValue(user === 'new'
      ? { username: '', fullName: '', title: '', roles: ['ky-thuat-vien'], active: true, password: '' }
      : { username: user.username, fullName: user.fullName, title: user.title ?? '', roles: user.roles, active: user.active, password: '' });
  };

  const save = useMutation({
    mutationFn: async (v: UserForm) => {
      const title = v.title?.trim() || null;
      if (editing === 'new') {
        await userService.create({ username: v.username.trim(), fullName: v.fullName.trim(), title, roles: v.roles, password: v.password ?? '' });
      } else {
        await userService.update((editing as User).id, { fullName: v.fullName.trim(), title, roles: v.roles, active: v.active, newPassword: v.password || null });
      }
    },
    onSuccess: async () => {
      setEditing(null);
      await queryClient.invalidateQueries({ queryKey: qk.users });
      message.success('Đã lưu tài khoản');
    },
    onError: (e) => message.error((e as Error).message),
  });

  const isNew = editing === 'new';

  return (
    <div data-screen="nhan-su">
      <div className="page-header">
        <h1 className="page-title">Nhân sự</h1>
        <Button type="primary" icon={<PlusOutlined />} onClick={() => openForm('new')}>Thêm nhân viên</Button>
      </div>

      <Card size="small">
        <Flex vertical gap={12}>
          <Flex gap={12} align="center" wrap>
            <Input aria-label="Tìm nhân viên" placeholder="Tìm theo tên đăng nhập, họ tên, chức danh" allowClear prefix={<SearchOutlined />} style={{ width: 360 }}
              onChange={(e) => setSearch(e.target.value)} />
            <Typography.Text type="secondary">{rows.length} tài khoản</Typography.Text>
          </Flex>
          {users.error && <Alert type="error" showIcon message={(users.error as Error).message} />}
          <Table<User>
            rowKey="id" size="small" bordered pagination={false} loading={users.isLoading} dataSource={rows} scroll={{ x: 980 }}
            locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Không có tài khoản nào khớp" /> }}
            columns={[
              { title: 'Tên đăng nhập', dataIndex: 'username', width: 170, render: (v: string, row) => <span><span className="code">{v}</span>{row.id === myId && <Tag style={{ marginInlineStart: 6, marginInlineEnd: 0 }}>Bạn</Tag>}</span> },
              { title: 'Họ tên', dataIndex: 'fullName', render: (v: string) => <b>{v}</b> },
              { title: 'Chức danh', dataIndex: 'title', width: 170, render: (v: string | null) => v ?? '—' },
              {
                title: 'Vai trò', dataIndex: 'roles', width: 250,
                render: (roles: string[]) => <Space size={[4, 4]} wrap>{roles.map((r) => <Tag key={r} color={r === 'quan-tri' ? 'green' : undefined} style={{ marginInlineEnd: 0 }}>{roleLabel(r)}</Tag>)}</Space>,
              },
              {
                title: 'Trạng thái', width: 190,
                render: (_, row) => (
                  <Space direction="vertical" size={0}>
                    <StatusTag domain="user" value={userState(row)} />
                    {row.lockedUntil && row.active && <Typography.Text type="secondary" style={{ fontSize: 12 }}>tới {dayjs(row.lockedUntil).format('HH:mm DD/MM')}</Typography.Text>}
                  </Space>
                ),
              },
              { title: 'Đăng nhập gần nhất', dataIndex: 'lastLoginAt', width: 160, render: (v: string | null) => (v ? <span className="dt">{dayjs(v).format('DD/MM/YYYY HH:mm')}</span> : '—') },
              { title: '', width: 80, align: 'right', render: (_, row) => <Button size="small" onClick={() => openForm(row)}>Sửa</Button> },
            ]}
          />
        </Flex>
      </Card>

      <Modal open={editing !== null} title={isNew ? 'Thêm nhân viên' : 'Sửa tài khoản'} okText="Lưu" cancelText="Hủy bỏ" width={560} destroyOnHidden
        confirmLoading={save.isPending} onCancel={() => setEditing(null)} onOk={() => form.submit()}>
        <Form form={form} layout="vertical" onFinish={(v) => save.mutate(v)} autoComplete="off">
          <Form.Item name="username" label="Tên đăng nhập"
            rules={isNew ? [{ required: true, whitespace: true, message: 'Nhập tên đăng nhập' }, { pattern: /^[A-Za-z0-9._-]+$/, message: 'Chỉ gồm chữ không dấu, số, dấu chấm, gạch ngang, gạch dưới' }] : []}
            extra={isNew ? 'Không đổi được sau khi tạo.' : undefined}>
            <Input autoFocus={isNew} disabled={!isNew} maxLength={64} autoCapitalize="none" />
          </Form.Item>
          <Form.Item name="fullName" label="Họ tên" rules={[{ required: true, whitespace: true, message: 'Nhập họ tên' }]}>
            <Input autoFocus={!isNew} maxLength={100} />
          </Form.Item>
          <Form.Item name="title" label="Chức danh" extra="In trên phiếu kết quả cạnh tên người duyệt, vd: BS. CKI, KTV trưởng.">
            <Input maxLength={100} />
          </Form.Item>
          <Form.Item name="roles" label="Vai trò" rules={[{ required: true, type: 'array', min: 1, message: 'Chọn ít nhất một vai trò' }]}>
            <Checkbox.Group style={{ display: 'grid', gap: 8 }}
              options={ROLE_OPTIONS.map((r) => ({ value: r.value, label: <span><b>{r.label}</b> — <Typography.Text type="secondary">{r.hint}</Typography.Text></span> }))} />
          </Form.Item>
          <Form.Item name="password" label={isNew ? 'Mật khẩu' : 'Đặt lại mật khẩu'}
            rules={[{ required: isNew, message: 'Nhập mật khẩu' }, { min: 8, message: 'Mật khẩu tối thiểu 8 ký tự' }]}
            extra={isNew ? 'Tối thiểu 8 ký tự. Báo nhân viên đổi mật khẩu sau lần đăng nhập đầu.' : 'Để trống nếu không đổi. Đặt lại sẽ mở luôn khoá tạm do nhập sai.'}>
            <Input.Password autoComplete="new-password" maxLength={256} />
          </Form.Item>
          {!isNew && (
            <Form.Item name="active" label="Đang hoạt động" valuePropName="checked" extra="Tắt thì tài khoản không đăng nhập được nữa; dữ liệu và lịch sử duyệt vẫn giữ." style={{ marginBottom: 0 }}>
              <Switch />
            </Form.Item>
          )}
        </Form>
      </Modal>
    </div>
  );
}
