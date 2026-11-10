import { DeleteOutlined } from '@ant-design/icons';
import { Button, Popconfirm } from 'antd';

/** Nút xoá (thùng rác đỏ) luôn có bước xác nhận — quy ước chung với clinic-web. */
export function DeleteButton({ title, description, onConfirm, label }: { title: string; description?: string; onConfirm: () => Promise<unknown> | void; label?: string }) {
  return (
    <Popconfirm title={title} description={description} okText="Xoá" cancelText="Hủy bỏ" okButtonProps={{ danger: true }} onConfirm={() => Promise.resolve(onConfirm()).catch(() => undefined)}>
      <Button size="small" danger icon={<DeleteOutlined />} aria-label={label ?? 'Xoá'}>{label}</Button>
    </Popconfirm>
  );
}
