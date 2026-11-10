// Thêm / sửa một dịch vụ hoặc chỉ số: thông tin chung, chỉ số con (nếu là gói), khoảng tham chiếu, quy đổi định tính.
// Một lần Lưu ghi tất cả trong cùng giao dịch ở máy chủ.
import { ArrowDownOutlined, ArrowUpOutlined, DeleteOutlined, PlusOutlined } from '@ant-design/icons';
import { useMutation, useQuery } from '@tanstack/react-query';
import { Alert, App, AutoComplete, Button, Col, Empty, Flex, Form, Input, InputNumber, Modal, Row, Select, Spin, Switch, Tabs, Typography } from 'antd';
import { useEffect, useMemo, useState } from 'react';
import { catalogService, qk, type CatalogService, type QualitativeRuleInput, type RangeInput, type ServiceInput } from '@/services';

type AgeUnit = 'ngay' | 'thang' | 'nam';
const AGE_FACTOR: Record<AgeUnit, number> = { ngay: 1, thang: 30, nam: 365 };
const AGE_OPTIONS = [{ value: 'nam', label: 'năm' }, { value: 'thang', label: 'tháng' }, { value: 'ngay', label: 'ngày' }];

/** Dòng khoảng tham chiếu trên form: tuổi nhập theo đơn vị người dùng chọn, lưu thì đổi ra ngày. */
interface RangeRow extends Omit<RangeInput, 'ageFromDays' | 'ageToDays'> {
  key: number;
  ageFrom: number | null;
  ageTo: number | null;
  ageUnit: AgeUnit;
}
interface RuleRow extends QualitativeRuleInput {
  key: number;
}

/** Ngày → (số, đơn vị) gọn nhất: 6570 ngày hiện là 18 năm. */
function splitAge(days: number | null): { value: number | null; unit: AgeUnit } {
  if (days === null) return { value: null, unit: 'nam' };
  if (days % 365 === 0) return { value: days / 365, unit: 'nam' };
  if (days % 30 === 0) return { value: days / 30, unit: 'thang' };
  return { value: days, unit: 'ngay' };
}

// Bỏ dấu để tìm "bach cau" ra "Bạch cầu".
const fold = (text: string) => text.normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/đ/g, 'd').replace(/Đ/g, 'D').toLowerCase();

interface InfoForm {
  code: string;
  name: string;
  groupName?: string;
  specimenTypeId: string;
  unit?: string;
  valueType: string;
  decimals?: number | null;
  printOrder?: number | null;
  isPanel: boolean;
  autoApprove: boolean;
  active: boolean;
  hisCode?: string;
}

interface Props {
  /** id dịch vụ đang sửa, 'new' để thêm, null để đóng. */
  target: string | 'new' | null;
  /** Toàn bộ cây dịch vụ — để chọn chỉ số con. */
  services: CatalogService[];
  onClose: () => void;
  onSaved: () => void;
}

let nextKey = 1;

export function ServiceModal({ target, services, onClose, onSaved }: Props) {
  const { message } = App.useApp();
  const [form] = Form.useForm<InfoForm>();
  const isPanel = Form.useWatch('isPanel', form) ?? false;
  const valueType = Form.useWatch('valueType', form) ?? 'so';
  const [children, setChildren] = useState<{ id: string; code: string; name: string }[]>([]);
  const [ranges, setRanges] = useState<RangeRow[]>([]);
  const [rules, setRules] = useState<RuleRow[]>([]);
  const [tab, setTab] = useState('info');

  const isNew = target === 'new';
  const meta = useQuery({ queryKey: qk.catalogMeta, queryFn: catalogService.meta, enabled: target !== null, staleTime: 60_000 });
  const detail = useQuery({
    queryKey: qk.catalogDetail(typeof target === 'string' && !isNew ? target : ''), queryFn: () => catalogService.detail(target as string),
    enabled: target !== null && !isNew, gcTime: 0,
  });

  useEffect(() => {
    if (target === null) return;
    setTab('info');
    if (isNew) {
      form.resetFields();
      form.setFieldsValue({ code: '', name: '', valueType: 'so', isPanel: false, autoApprove: false, active: true, printOrder: 0 });
      setChildren([]);
      setRanges([]);
      setRules([]);
    } else if (detail.data) {
      const d = detail.data;
      form.setFieldsValue({
        code: d.code, name: d.name, groupName: d.groupName ?? '', specimenTypeId: d.specimenTypeId ?? undefined, unit: d.unit ?? '', valueType: d.valueType,
        decimals: d.decimals, printOrder: d.printOrder ?? 0, isPanel: d.isPanel, autoApprove: d.autoApprove, active: d.active, hisCode: d.hisCode ?? '',
      });
      setChildren(d.children);
      setRanges(d.ranges.map((r) => {
        const from = splitAge(r.ageFromDays);
        const to = splitAge(r.ageToDays);
        // Hai đầu khác đơn vị thì hiện cả hai theo ngày cho khỏi sai.
        const unit: AgeUnit = r.ageFromDays !== null && r.ageToDays !== null && from.unit !== to.unit ? 'ngay' : r.ageFromDays !== null ? from.unit : to.unit;
        return {
          ...r, key: nextKey++, ageUnit: unit,
          ageFrom: r.ageFromDays === null ? null : r.ageFromDays / AGE_FACTOR[unit], ageTo: r.ageToDays === null ? null : r.ageToDays / AGE_FACTOR[unit],
        };
      }));
      setRules(d.qualitativeRules.map((r) => ({ ...r, key: nextKey++ })));
    }
  }, [target, isNew, detail.data, form]);

  // Chỉ số lá chưa nằm trong gói đang sửa — để thêm làm chỉ số con.
  const leafOptions = useMemo(() => {
    const chosen = new Set(children.map((c) => c.id));
    const leaves = services.flatMap((s) => (s.isPanel ? s.children : [s]));
    const unique = [...new Map(leaves.map((s) => [s.id, s])).values()];
    return unique.filter((s) => !chosen.has(s.id) && s.id !== target)
      .map((s) => ({ value: s.id, label: `${s.code} – ${s.name}`, search: fold(`${s.code} ${s.name}`), code: s.code, name: s.name }));
  }, [services, children, target]);

  const save = useMutation({
    mutationFn: async (v: InfoForm) => {
      const input: ServiceInput = {
        code: v.code.trim(), name: v.name.trim(), groupName: v.groupName?.trim() || null, specimenTypeId: v.specimenTypeId, unit: v.unit?.trim() || null,
        valueType: v.valueType, decimals: v.decimals ?? null, printOrder: v.printOrder ?? 0, isPanel: v.isPanel, autoApprove: v.autoApprove, active: v.active,
        hisCode: v.hisCode?.trim() || null,
        childIds: v.isPanel ? children.map((c) => c.id) : [],
        ranges: v.isPanel ? [] : ranges.map((r) => ({
          sex: r.sex, instrumentId: r.instrumentId, low: r.low, high: r.high, criticalLow: r.criticalLow, criticalHigh: r.criticalHigh, textRange: r.textRange?.trim() || null,
          ageFromDays: r.ageFrom === null ? null : Math.round(r.ageFrom * AGE_FACTOR[r.ageUnit]), ageToDays: r.ageTo === null ? null : Math.round(r.ageTo * AGE_FACTOR[r.ageUnit]),
        })),
        qualitativeRules: !v.isPanel && v.valueType === 'dinh-tinh'
          ? rules.map((r) => ({ instrumentId: r.instrumentId, rawValue: r.rawValue.trim(), displayValue: r.displayValue.trim(), flag: r.flag })) : [],
      };
      if (isNew) await catalogService.create(input);
      else await catalogService.update(target as string, input);
    },
    onSuccess: () => { message.success('Đã lưu dịch vụ'); onSaved(); },
    onError: (e) => message.error((e as Error).message),
  });

  const submit = async () => {
    try {
      save.mutate(await form.validateFields());
    } catch {
      setTab('info'); // lỗi nhập ở tab Thông tin — chuyển về đó để người dùng thấy
    }
  };

  const setRange = (key: number, patch: Partial<RangeRow>) => setRanges((rows) => rows.map((r) => (r.key === key ? { ...r, ...patch } : r)));
  const setRule = (key: number, patch: Partial<RuleRow>) => setRules((rows) => rows.map((r) => (r.key === key ? { ...r, ...patch } : r)));
  const moveChild = (index: number, delta: number) => setChildren((list) => {
    const next = [...list];
    const [item] = next.splice(index, 1);
    next.splice(index + delta, 0, item);
    return next;
  });

  const instrumentOptions = [{ value: '', label: 'Mọi máy' }, ...(meta.data?.instruments ?? []).map((i) => ({ value: i.id, label: i.name }))];
  const loading = !isNew && detail.isLoading;

  return (
    <Modal open={target !== null} title={isNew ? 'Thêm dịch vụ / chỉ số' : 'Sửa dịch vụ / chỉ số'} okText="Lưu" cancelText="Hủy bỏ" width={980} destroyOnHidden
      confirmLoading={save.isPending} okButtonProps={{ disabled: loading }} onCancel={onClose} onOk={() => void submit()} styles={{ body: { minHeight: 380 } }}>
      {detail.error && <Alert type="error" showIcon message={(detail.error as Error).message} />}
      <Spin spinning={loading}>
        <Form form={form} layout="vertical">
          <Tabs activeKey={tab} onChange={setTab}
            items={[
              {
                key: 'info', label: 'Thông tin', forceRender: true,
                children: (
                  <Row gutter={12}>
                    <Col span={8}>
                      <Form.Item name="code" label="Mã" rules={[{ required: true, whitespace: true, message: 'Nhập mã' }, { pattern: /^\S+$/, message: 'Mã không được có khoảng trắng' }]}
                        extra="Trùng với mã dịch vụ bên HIS để nhận chỉ định.">
                        <Input autoFocus={isNew} maxLength={64} className="code" />
                      </Form.Item>
                    </Col>
                    <Col span={16}>
                      <Form.Item name="name" label="Tên dịch vụ / chỉ số" rules={[{ required: true, whitespace: true, message: 'Nhập tên' }]}>
                        <Input maxLength={500} />
                      </Form.Item>
                    </Col>
                    <Col span={8}>
                      <Form.Item name="groupName" label="Nhóm">
                        <AutoComplete options={(meta.data?.groups ?? []).map((g) => ({ value: g }))} placeholder="vd: Sinh hoá"
                          filterOption={(input, option) => fold(option?.value ?? '').includes(fold(input))} />
                      </Form.Item>
                    </Col>
                    <Col span={8}>
                      <Form.Item name="specimenTypeId" label="Loại mẫu" rules={[{ required: true, message: 'Chọn loại mẫu' }]}>
                        <Select loading={meta.isLoading} options={(meta.data?.specimenTypes ?? []).map((s) => ({ value: s.id, label: s.name }))} />
                      </Form.Item>
                    </Col>
                    <Col span={8}>
                      <Form.Item name="isPanel" label="Là gói có chỉ số con" valuePropName="checked" extra="vd: Tổng phân tích tế bào máu.">
                        <Switch />
                      </Form.Item>
                    </Col>
                    {!isPanel && (
                      <>
                        <Col span={8}>
                          <Form.Item name="valueType" label="Kiểu kết quả">
                            <Select options={[{ value: 'so', label: 'Số' }, { value: 'dinh-tinh', label: 'Định tính (âm / dương…)' }, { value: 'chu', label: 'Chữ tự do' }]} />
                          </Form.Item>
                        </Col>
                        <Col span={8}>
                          <Form.Item name="unit" label="Đơn vị"><Input maxLength={50} placeholder="vd: mmol/L" /></Form.Item>
                        </Col>
                        <Col span={8}>
                          <Form.Item name="decimals" label="Số chữ số lẻ" extra="Để trống = giữ như nhập.">
                            <InputNumber min={0} max={6} style={{ width: '100%' }} disabled={valueType !== 'so'} />
                          </Form.Item>
                        </Col>
                        <Col span={8}>
                          <Form.Item name="autoApprove" label="Tự duyệt" valuePropName="checked" extra="Mẫu tự duyệt khi mọi chỉ số bật mục này, đủ kết quả và không có cờ.">
                            <Switch />
                          </Form.Item>
                        </Col>
                      </>
                    )}
                    <Col span={8}>
                      <Form.Item name="printOrder" label="Thứ tự in" extra="Số nhỏ in trước."><InputNumber min={0} max={100000} style={{ width: '100%' }} /></Form.Item>
                    </Col>
                    <Col span={8}>
                      <Form.Item name="hisCode" label="Mã HIS (nếu khác mã)"><Input maxLength={64} /></Form.Item>
                    </Col>
                    <Col span={8}>
                      <Form.Item name="active" label="Đang dùng" valuePropName="checked" extra="Tắt thì không chọn được khi tiếp nhận.">
                        <Switch />
                      </Form.Item>
                    </Col>
                  </Row>
                ),
              },
              ...(isPanel ? [{
                key: 'children', label: `Chỉ số con (${children.length})`,
                children: (
                  <Flex vertical gap={8}>
                    <Select showSearch value={null} placeholder="Thêm chỉ số con — gõ mã hoặc tên (không dấu cũng được)" options={leafOptions}
                      filterOption={(input, option) => (option?.search ?? '').includes(fold(input.trim()))}
                      onChange={(_, option) => { const o = option as (typeof leafOptions)[number]; setChildren((list) => [...list, { id: o.value, code: o.code, name: o.name }]); }} />
                    {children.length === 0 && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Gói chưa có chỉ số con nào" />}
                    {children.length > 0 && (
                      <table className="lab-grid" style={{ minWidth: 0 }}>
                        <thead><tr><th style={{ width: 48 }}>STT</th><th style={{ width: 180 }}>Mã</th><th>Tên chỉ số</th><th style={{ width: 130 }} aria-label="Thao tác" /></tr></thead>
                        <tbody>
                          {children.map((c, i) => (
                            <tr key={c.id}>
                              <td className="c muted">{i + 1}</td>
                              <td className="code">{c.code}</td>
                              <td>{c.name}</td>
                              <td>
                                <Button size="small" type="text" icon={<ArrowUpOutlined />} aria-label="Đưa lên" disabled={i === 0} onClick={() => moveChild(i, -1)} />
                                <Button size="small" type="text" icon={<ArrowDownOutlined />} aria-label="Đưa xuống" disabled={i === children.length - 1} onClick={() => moveChild(i, 1)} />
                                <Button size="small" type="text" danger icon={<DeleteOutlined />} aria-label={`Gỡ ${c.name}`} onClick={() => setChildren((list) => list.filter((x) => x.id !== c.id))} />
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    )}
                    <Typography.Text type="secondary">Thứ tự ở đây là thứ tự in trên phiếu. Gỡ khỏi gói không xoá chỉ số.</Typography.Text>
                  </Flex>
                ),
              }] : [{
                key: 'ranges', label: `Khoảng tham chiếu (${ranges.length})`,
                children: (
                  <Flex vertical gap={8}>
                    {ranges.length === 0 && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Chưa có khoảng tham chiếu — kết quả sẽ không được gắn cờ cao / thấp" />}
                    {ranges.length > 0 && (
                      <div className="lab-grid-wrap">
                        <table className="lab-grid" style={{ minWidth: 900, tableLayout: 'fixed' }}>
                          <thead>
                            <tr>
                              <th style={{ width: 84 }}>Giới</th><th style={{ width: 196 }}>Tuổi từ – đến</th><th style={{ width: 132 }}>Máy</th>
                              <th style={{ width: 84 }}>Thấp</th><th style={{ width: 84 }}>Cao</th><th style={{ width: 84 }}>Nguy hiểm thấp</th><th style={{ width: 84 }}>Nguy hiểm cao</th>
                              <th>Hoặc chữ</th><th style={{ width: 40 }} aria-label="Xoá" />
                            </tr>
                          </thead>
                          <tbody>
                            {ranges.map((r, i) => (
                              <tr key={r.key}>
                                <td><Select size="small" aria-label={`Giới dòng ${i + 1}`} style={{ width: '100%' }} value={r.sex ?? ''} onChange={(v) => setRange(r.key, { sex: (v || null) as RangeRow['sex'] })}
                                  options={[{ value: '', label: 'Chung' }, { value: 'M', label: 'Nam' }, { value: 'F', label: 'Nữ' }]} /></td>
                                <td>
                                  <Flex gap={4}>
                                    <InputNumber size="small" controls={false} aria-label={`Tuổi từ dòng ${i + 1}`} min={0} style={{ width: 52 }} value={r.ageFrom} onChange={(v) => setRange(r.key, { ageFrom: v })} />
                                    <InputNumber size="small" controls={false} aria-label={`Tuổi đến dòng ${i + 1}`} min={0} style={{ width: 52 }} value={r.ageTo} onChange={(v) => setRange(r.key, { ageTo: v })} />
                                    <Select size="small" aria-label={`Đơn vị tuổi dòng ${i + 1}`} style={{ width: 76 }} value={r.ageUnit} onChange={(v) => setRange(r.key, { ageUnit: v })} options={AGE_OPTIONS} />
                                  </Flex>
                                </td>
                                <td><Select size="small" aria-label={`Máy dòng ${i + 1}`} style={{ width: '100%' }} value={r.instrumentId ?? ''} onChange={(v) => setRange(r.key, { instrumentId: v || null })} options={instrumentOptions} /></td>
                                {(['low', 'high', 'criticalLow', 'criticalHigh'] as const).map((field) => (
                                  <td key={field}><InputNumber size="small" controls={false} aria-label={`${field} dòng ${i + 1}`} style={{ width: '100%' }} value={r[field]} onChange={(v) => setRange(r.key, { [field]: v })} /></td>
                                ))}
                                <td><Input size="small" aria-label={`Chữ tham chiếu dòng ${i + 1}`} maxLength={200} value={r.textRange ?? ''} onChange={(e) => setRange(r.key, { textRange: e.target.value })} placeholder="vd: Âm tính" /></td>
                                <td className="c"><Button size="small" type="text" danger icon={<DeleteOutlined />} aria-label={`Xoá dòng ${i + 1}`} onClick={() => setRanges((rows) => rows.filter((x) => x.key !== r.key))} /></td>
                              </tr>
                            ))}
                          </tbody>
                        </table>
                      </div>
                    )}
                    <div>
                      <Button icon={<PlusOutlined />} onClick={() => setRanges((rows) => [...rows, {
                        key: nextKey++, sex: null, ageFrom: null, ageTo: null, ageUnit: 'nam', instrumentId: null, low: null, high: null, criticalLow: null, criticalHigh: null, textRange: null,
                      }])}>Thêm khoảng</Button>
                    </div>
                    <Typography.Text type="secondary">
                      Để trống tuổi = mọi tuổi. Khi nhiều khoảng cùng khớp, hệ thống ưu tiên khoảng đúng máy, rồi đúng giới, rồi khoảng tuổi hẹp hơn.
                      Ngưỡng nguy hiểm làm kết quả được tô đỏ.
                    </Typography.Text>
                  </Flex>
                ),
              }]),
              ...(!isPanel && valueType === 'dinh-tinh' ? [{
                key: 'rules', label: `Quy đổi định tính (${rules.length})`,
                children: (
                  <Flex vertical gap={8}>
                    {rules.length === 0 && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Chưa có luật — giá trị máy gửi được giữ nguyên" />}
                    {rules.length > 0 && (
                      <table className="lab-grid" style={{ minWidth: 0 }}>
                        <thead><tr><th>Máy gửi</th><th>Hiển thị trên phiếu</th><th style={{ width: 170 }}>Đánh giá</th><th style={{ width: 190 }}>Máy</th><th style={{ width: 40 }} aria-label="Xoá" /></tr></thead>
                        <tbody>
                          {rules.map((r, i) => (
                            <tr key={r.key}>
                              <td><Input size="small" aria-label={`Giá trị máy gửi dòng ${i + 1}`} maxLength={100} value={r.rawValue} onChange={(e) => setRule(r.key, { rawValue: e.target.value })} placeholder="vd: pos" /></td>
                              <td><Input size="small" aria-label={`Chữ hiển thị dòng ${i + 1}`} maxLength={100} value={r.displayValue} onChange={(e) => setRule(r.key, { displayValue: e.target.value })} placeholder="vd: Dương tính" /></td>
                              <td><Select size="small" aria-label={`Đánh giá dòng ${i + 1}`} style={{ width: '100%' }} value={r.flag ?? ''} onChange={(v) => setRule(r.key, { flag: v || null })}
                                options={[{ value: '', label: 'Bình thường' }, { value: 'Abnormal', label: 'Bất thường' }]} /></td>
                              <td><Select size="small" aria-label={`Máy dòng ${i + 1}`} style={{ width: '100%' }} value={r.instrumentId ?? ''} onChange={(v) => setRule(r.key, { instrumentId: v || null })} options={instrumentOptions} /></td>
                              <td className="c"><Button size="small" type="text" danger icon={<DeleteOutlined />} aria-label={`Xoá dòng ${i + 1}`} onClick={() => setRules((rows) => rows.filter((x) => x.key !== r.key))} /></td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    )}
                    <div>
                      <Button icon={<PlusOutlined />} onClick={() => setRules((rows) => [...rows, { key: nextKey++, instrumentId: null, rawValue: '', displayValue: '', flag: null }])}>Thêm luật</Button>
                    </div>
                    <Typography.Text type="secondary">Máy gửi "pos", "neg"… thì đổi thành chữ in trên phiếu. So không phân biệt hoa thường.</Typography.Text>
                  </Flex>
                ),
              }] : []),
            ]}
          />
        </Form>
      </Spin>
    </Modal>
  );
}
