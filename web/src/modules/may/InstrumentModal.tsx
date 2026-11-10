// Thêm / sửa máy xét nghiệm: tên, giao thức, agent phụ trách, và cách agent nối với máy (COM hoặc TCP).
import { useMutation, useQuery } from '@tanstack/react-query';
import { Alert, App, Col, Form, Input, InputNumber, Modal, Radio, Row, Select, Switch } from 'antd';
import { useEffect } from 'react';
import { agentService, instrumentService, qk, type ConnectionKind, type Instrument, type InstrumentInput } from '@/services';

const PROTOCOLS = [
  { value: 'hl7-mllp', label: 'HL7 MLLP (vd Mindray BC-5380)' },
  { value: 'astm-e1394', label: 'ASTM E1394 (vd Beckman Access 2)' },
  { value: 'beckman-au', label: 'Beckman AU (vd AU480)' },
];
const BAUD_RATES = [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200];

interface FormValues {
  name: string;
  model?: string;
  protocolFamily?: string;
  workstationCode?: string;
  active: boolean;
  kind: ConnectionKind;
  portName?: string;
  baudRate?: number;
  dataBits?: number;
  parity?: string;
  stopBits?: number;
  host?: string;
  tcpPort?: number;
}

interface Props {
  /** Máy đang sửa, 'new' để thêm, null để đóng. */
  target: Instrument | 'new' | null;
  onClose: () => void;
  onSaved: (createdId?: string) => void;
}

export function InstrumentModal({ target, onClose, onSaved }: Props) {
  const { message } = App.useApp();
  const [form] = Form.useForm<FormValues>();
  const kind = Form.useWatch('kind', form);
  const agents = useQuery({ queryKey: qk.agents, queryFn: agentService.list, enabled: target !== null });

  useEffect(() => {
    if (target === null) return;
    if (target === 'new') {
      form.setFieldsValue({ name: '', model: '', protocolFamily: undefined, workstationCode: undefined, active: true, kind: 'none', baudRate: 9600, dataBits: 8, parity: 'None', stopBits: 1 });
    } else {
      const c = target.connection;
      form.setFieldsValue({
        name: target.name, model: target.model ?? '', protocolFamily: target.protocolFamily ?? undefined, workstationCode: target.workstationCode ?? undefined,
        active: target.active, kind: c.kind, portName: c.portName, baudRate: c.baudRate ?? 9600, dataBits: c.dataBits ?? 8, parity: c.parity ?? 'None',
        stopBits: c.stopBits ?? 1, host: c.host, tcpPort: c.tcpPort,
      });
    }
  }, [target, form]);

  const save = useMutation({
    mutationFn: async (v: FormValues) => {
      const input: InstrumentInput = {
        name: v.name.trim(), model: v.model?.trim() || null, protocolFamily: v.protocolFamily ?? null, workstationCode: v.workstationCode ?? null, active: v.active,
        connection:
          v.kind === 'serial' ? { kind: 'serial', portName: v.portName, baudRate: v.baudRate, dataBits: v.dataBits, parity: v.parity, stopBits: v.stopBits }
          : v.kind === 'tcp-server' ? { kind: 'tcp-server', tcpPort: v.tcpPort }
          : v.kind === 'tcp-client' ? { kind: 'tcp-client', host: v.host, tcpPort: v.tcpPort }
          : { kind: 'none' },
      };
      if (target === 'new') return (await instrumentService.create(input)).id;
      await instrumentService.update((target as Instrument).id, input);
      return undefined;
    },
    onSuccess: (id) => {
      message.success('Đã lưu máy — agent nhận cấu hình mới trong vòng 30 giây');
      onSaved(id);
    },
    onError: (e) => message.error((e as Error).message),
  });

  return (
    <Modal open={target !== null} title={target === 'new' ? 'Thêm máy xét nghiệm' : 'Sửa máy xét nghiệm'} okText="Lưu" cancelText="Hủy bỏ" width={680} destroyOnHidden
      confirmLoading={save.isPending} onCancel={onClose} onOk={() => form.submit()}>
      <Form form={form} layout="vertical" onFinish={(v) => save.mutate(v)}>
        <Row gutter={12}>
          <Col span={12}>
            <Form.Item name="name" label="Tên máy" rules={[{ required: true, whitespace: true, message: 'Nhập tên máy' }]}>
              <Input autoFocus maxLength={100} placeholder="vd: Beckman AU480" />
            </Form.Item>
          </Col>
          <Col span={12}>
            <Form.Item name="model" label="Model">
              <Input maxLength={100} />
            </Form.Item>
          </Col>
          <Col span={12}>
            <Form.Item name="protocolFamily" label="Giao thức" extra="Quyết định cách đọc dữ liệu máy gửi.">
              <Select allowClear placeholder="Chọn giao thức" options={PROTOCOLS} />
            </Form.Item>
          </Col>
          <Col span={12}>
            <Form.Item name="workstationCode" label="Agent phụ trách" extra="Máy tính cắm cáp tới máy xét nghiệm này.">
              <Select allowClear placeholder="Chưa gán" loading={agents.isLoading}
                options={(agents.data ?? []).map((a) => ({ value: a.workstationCode, label: `${a.name} (${a.workstationCode})` }))}
                notFoundContent="Chưa có agent — tạo ở mục Agent" />
            </Form.Item>
          </Col>
        </Row>

        <Form.Item name="kind" label="Cách nối với máy">
          <Radio.Group
            options={[
              { value: 'none', label: 'Chưa cấu hình' },
              { value: 'serial', label: 'Cổng COM (RS-232)' },
              { value: 'tcp-server', label: 'TCP — máy nối vào agent' },
              { value: 'tcp-client', label: 'TCP — agent nối tới máy' },
            ]} />
        </Form.Item>

        {kind === 'serial' && (
          <Row gutter={12}>
            <Col span={8}>
              <Form.Item name="portName" label="Cổng COM" rules={[{ required: true, whitespace: true, message: 'Nhập cổng COM' }]} extra="Tên cổng trên máy tính agent.">
                <Input placeholder="COM3" style={{ textTransform: 'uppercase' }} maxLength={32} />
              </Form.Item>
            </Col>
            <Col span={8}>
              <Form.Item name="baudRate" label="Tốc độ baud"><Select options={BAUD_RATES.map((b) => ({ value: b, label: String(b) }))} /></Form.Item>
            </Col>
            <Col span={8}>
              <Form.Item name="dataBits" label="Bit dữ liệu"><Select options={[{ value: 8, label: '8' }, { value: 7, label: '7' }]} /></Form.Item>
            </Col>
            <Col span={8}>
              <Form.Item name="parity" label="Parity">
                <Select options={[{ value: 'None', label: 'None (không)' }, { value: 'Even', label: 'Even (chẵn)' }, { value: 'Odd', label: 'Odd (lẻ)' }]} />
              </Form.Item>
            </Col>
            <Col span={8}>
              <Form.Item name="stopBits" label="Stop bit"><Select options={[{ value: 1, label: '1' }, { value: 2, label: '2' }]} /></Form.Item>
            </Col>
          </Row>
        )}
        {kind === 'tcp-server' && (
          <Row gutter={12}>
            <Col span={8}>
              <Form.Item name="tcpPort" label="Cổng TCP agent mở" rules={[{ required: true, message: 'Nhập cổng TCP' }]}
                extra="Trên máy xét nghiệm, khai IP máy tính agent và cổng này.">
                <InputNumber min={1} max={65535} style={{ width: '100%' }} placeholder="5100" />
              </Form.Item>
            </Col>
          </Row>
        )}
        {kind === 'tcp-client' && (
          <Row gutter={12}>
            <Col span={12}>
              <Form.Item name="host" label="Địa chỉ IP của máy xét nghiệm" rules={[{ required: true, whitespace: true, message: 'Nhập địa chỉ IP' }]}>
                <Input placeholder="192.168.1.50" maxLength={255} />
              </Form.Item>
            </Col>
            <Col span={8}>
              <Form.Item name="tcpPort" label="Cổng TCP" rules={[{ required: true, message: 'Nhập cổng TCP' }]}>
                <InputNumber min={1} max={65535} style={{ width: '100%' }} />
              </Form.Item>
            </Col>
          </Row>
        )}

        <Form.Item name="active" label="Đang dùng" valuePropName="checked" extra="Tắt thì agent đóng kết nối với máy này." style={{ marginBottom: 8 }}>
          <Switch />
        </Form.Item>
        {kind !== 'none' && kind !== undefined && (
          <Alert type="info" showIcon message="Máy chỉ chạy khi đã chọn giao thức, agent phụ trách, và agent đó đang chạy." />
        )}
      </Form>
    </Modal>
  );
}
