// Tự đổi mật khẩu của mình (mọi vai trò) — mở từ menu tài khoản trên thanh trên.
import { useMutation } from '@tanstack/react-query';
import { App, Form, Input, Modal } from 'antd';
import { userService } from '@/services';

interface PasswordForm {
  currentPassword: string;
  newPassword: string;
  confirm: string;
}

export function ChangePasswordModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { message } = App.useApp();
  const [form] = Form.useForm<PasswordForm>();

  const save = useMutation({
    mutationFn: (v: PasswordForm) => userService.changeOwnPassword(v.currentPassword, v.newPassword),
    onSuccess: () => {
      message.success('Đã đổi mật khẩu');
      onClose();
    },
    onError: (e) => message.error((e as Error).message),
  });

  return (
    <Modal open={open} title="Đổi mật khẩu" okText="Đổi mật khẩu" cancelText="Hủy bỏ" destroyOnHidden confirmLoading={save.isPending}
      onCancel={onClose} onOk={() => form.submit()} afterOpenChange={(visible) => visible && form.resetFields()}>
      <Form form={form} layout="vertical" onFinish={(v) => save.mutate(v)}>
        <Form.Item name="currentPassword" label="Mật khẩu hiện tại" rules={[{ required: true, message: 'Nhập mật khẩu hiện tại' }]}>
          <Input.Password autoFocus autoComplete="current-password" maxLength={256} />
        </Form.Item>
        <Form.Item name="newPassword" label="Mật khẩu mới" rules={[{ required: true, message: 'Nhập mật khẩu mới' }, { min: 8, message: 'Mật khẩu tối thiểu 8 ký tự' }]}>
          <Input.Password autoComplete="new-password" maxLength={256} />
        </Form.Item>
        <Form.Item name="confirm" label="Nhập lại mật khẩu mới" dependencies={['newPassword']}
          rules={[
            { required: true, message: 'Nhập lại mật khẩu mới' },
            ({ getFieldValue }) => ({
              validator: (_, value) => (!value || getFieldValue('newPassword') === value ? Promise.resolve() : Promise.reject(new Error('Hai lần nhập không giống nhau'))),
            }),
          ]}>
          <Input.Password autoComplete="new-password" maxLength={256} />
        </Form.Item>
      </Form>
    </Modal>
  );
}
