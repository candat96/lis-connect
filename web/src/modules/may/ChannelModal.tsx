// Thêm / sửa một kênh: mã kênh máy gửi → chỉ số trong danh mục. Cũng dùng để khai chỉ số máy chạy khi chưa biết mã kênh.
import { useMutation, useQuery } from '@tanstack/react-query';
import { App, Button, Col, Form, Input, InputNumber, Modal, Row, Select, Switch } from 'antd';
import { useEffect, useMemo } from 'react';
import { catalogService, instrumentService, qk, type ChannelInput } from '@/services';

/** Dữ liệu mở hộp thoại: có `id` là sửa kênh đã có; không có là thêm mới (có thể điền sẵn mã kênh / mã chỉ số). */
export interface ChannelDraft extends ChannelInput {
  id?: string;
}

interface Props {
  instrumentId: string;
  draft: ChannelDraft | null;
  onClose: () => void;
  onSaved: () => void;
}

// Bỏ dấu để tìm "bach cau" ra "Bạch cầu".
const fold = (text: string) => text.normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/đ/g, 'd').replace(/Đ/g, 'D').toLowerCase();

export function ChannelModal({ instrumentId, draft, onClose, onSaved }: Props) {
  const { message } = App.useApp();
  const [form] = Form.useForm<ChannelInput>();
  const services = useQuery({ queryKey: qk.catalog, queryFn: catalogService.services, staleTime: 60_000, enabled: draft !== null });

  // Chỉ số lá (kể cả chỉ số con của gói) để chọn; kênh máy không map tới gói.
  const options = useMemo(() => {
    const leaves = (services.data ?? []).flatMap((s) => (s.isPanel ? s.children.map((c) => ({ ...c, parent: s.name })) : [{ ...s, parent: null as string | null }]));
    return leaves.map((s) => ({
      value: s.code,
      label: `${s.code} – ${s.name}${s.parent ? ` (${s.parent})` : ''}`,
      search: fold(`${s.code} ${s.name} ${s.parent ?? ''}`),
    }));
  }, [services.data]);

  useEffect(() => {
    if (draft) form.setFieldsValue({ ...draft, serviceCode: draft.serviceCode || undefined });
  }, [draft, form]);

  const onError = (e: unknown) => message.error((e as Error).message);

  const save = useMutation({
    mutationFn: async (v: ChannelInput): Promise<void> => {
      const input: ChannelInput = { ...v, channelCode: v.channelCode.trim(), decimals: v.decimals ?? null, factor: v.factor ?? null };
      if (draft?.id) await instrumentService.updateChannel(instrumentId, draft.id, input);
      else await instrumentService.addChannel(instrumentId, input);
    },
    onSuccess: () => { message.success('Đã lưu kênh'); onSaved(); },
    onError,
  });

  const declare = useMutation({
    mutationFn: (serviceCode: string) => instrumentService.declare(instrumentId, serviceCode),
    onSuccess: () => { message.success('Đã khai chỉ số — hệ thống sẽ tự dò mã kênh khi máy gửi kết quả'); onSaved(); },
    onError,
  });

  const declareNow = () => {
    const serviceCode = form.getFieldValue('serviceCode') as string | undefined;
    if (!serviceCode) {
      void form.validateFields(['serviceCode']);
      return;
    }
    declare.mutate(serviceCode);
  };

  return (
    <Modal open={draft !== null} title={draft?.id ? 'Sửa kênh' : 'Thêm kênh'} width={620} destroyOnHidden onCancel={onClose}
      footer={[
        !draft?.id && (
          <Button key="declare" onClick={declareNow} loading={declare.isPending} title="Máy chạy chỉ số này nhưng chưa biết mã kênh — để hệ thống tự dò theo barcode">
            Chưa biết mã kênh — để tự dò
          </Button>
        ),
        <Button key="cancel" onClick={onClose}>Hủy bỏ</Button>,
        <Button key="ok" type="primary" loading={save.isPending} onClick={() => form.submit()}>Lưu</Button>,
      ]}>
      <Form form={form} layout="vertical" onFinish={(v) => save.mutate(v)}>
        <Form.Item name="channelCode" label="Mã kênh máy" rules={[{ required: true, whitespace: true, message: 'Nhập mã kênh máy' }]}
          extra="Đúng như máy gửi (mã test / số thứ tự test). Xem ở mục Log agent hoặc bảng 'mã kênh chưa map'.">
          <Input autoFocus maxLength={64} className="code" />
        </Form.Item>
        <Form.Item name="serviceCode" label="Chỉ số" rules={[{ required: true, message: 'Chọn chỉ số' }]}>
          <Select showSearch placeholder="Gõ mã hoặc tên chỉ số (không dấu cũng được)" loading={services.isLoading} options={options}
            filterOption={(input, option) => (option?.search ?? '').includes(fold(input.trim()))} />
        </Form.Item>
        <Row gutter={12}>
          <Col span={12}>
            <Form.Item name="decimals" label="Số lẻ" extra="Để trống = theo chỉ số.">
              <InputNumber min={0} max={6} style={{ width: '100%' }} />
            </Form.Item>
          </Col>
          <Col span={12}>
            <Form.Item name="factor" label="Hệ số nhân" extra="Để trống = không nhân. Dùng khi máy và phiếu khác đơn vị.">
              <InputNumber min={0} step={0.1} style={{ width: '100%' }} />
            </Form.Item>
          </Col>
          <Col span={12}>
            <Form.Item name="keepComparator" label="Giữ dấu < > máy gửi" valuePropName="checked">
              <Switch />
            </Form.Item>
          </Col>
          <Col span={12}>
            <Form.Item name="sendOnQuery" label="Gửi kênh này khi máy hỏi chỉ định" valuePropName="checked">
              <Switch />
            </Form.Item>
          </Col>
        </Row>
      </Form>
    </Modal>
  );
}
