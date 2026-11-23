// Tiếp nhận tối giản: nhập người bệnh (hoặc chọn người có sẵn), chọn dịch vụ bằng mã + Enter, lưu → mẫu có barcode.
// Bàn phím là chính: Enter chuyển ô, Ctrl+Enter lưu, Delete xoá dịch vụ đang chọn, sau khi lưu con trỏ về Họ tên.
import { CopyOutlined, DeleteOutlined, SaveOutlined, UserOutlined } from '@ant-design/icons';
import { useMutation, useQuery } from '@tanstack/react-query';
import { Alert, App, AutoComplete, Button, Card, Empty, Flex, Input, Result, Table, Tag, Typography, type InputRef } from 'antd';
import dayjs from 'dayjs';
import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { catalogService, qk, receptionService, type CatalogService, type OrderCreated, type PatientHit } from '@/services';
import { useCanReceive } from '@/stores/sessionStore';
import { findServiceByCode, parseBirth, parseSex, SEX_LABEL } from './reception';

const EMPTY = { fullName: '', sex: '', birth: '', phone: '', doctor: '', diagnosis: '' };
type Field = keyof typeof EMPTY;
const ORDER: Field[] = ['fullName', 'sex', 'birth', 'phone', 'doctor', 'diagnosis'];

const birthText = (p: PatientHit) => (p.dob ? dayjs(p.dob).format('DD/MM/YYYY') : p.birthYear ? String(p.birthYear) : '—');

export default function ReceptionPage() {
  const { message } = App.useApp();
  const canReceive = useCanReceive();
  const [form, setForm] = useState({ ...EMPTY });
  const [existing, setExisting] = useState<PatientHit | null>(null);
  const [search, setSearch] = useState('');
  const [chosen, setChosen] = useState<CatalogService[]>([]);
  const [activeRow, setActiveRow] = useState(0);
  const [code, setCode] = useState('');
  const [codeError, setCodeError] = useState<string | null>(null);
  const [errors, setErrors] = useState<Partial<Record<Field, string>>>({});
  const [created, setCreated] = useState<OrderCreated | null>(null);
  const inputs = useRef(new Map<Field, InputRef | null>());
  const codeRef = useRef<InputRef>(null);

  const catalog = useQuery({ queryKey: qk.catalog, queryFn: catalogService.services, staleTime: 60_000 });
  const services = useMemo(() => (catalog.data ?? []).filter((s) => s.active), [catalog.data]);
  const patients = useQuery({
    queryKey: ['reception', 'patients', search], queryFn: () => receptionService.findPatients(search), enabled: search.trim().length >= 2, staleTime: 10_000,
  });

  const save = useMutation({
    mutationFn: receptionService.createOrder,
    onSuccess: (result) => {
      setCreated(result);
      setForm({ ...EMPTY });
      setExisting(null);
      setSearch('');
      setChosen([]);
      setErrors({});
      message.success(`Đã tiếp nhận — ${result.samples.length} mẫu`);
      inputs.current.get('fullName')?.focus();
    },
  });

  useEffect(() => { inputs.current.get('fullName')?.focus(); }, []);

  if (!canReceive) {
    return <Result status="403" title="Không có quyền tiếp nhận" subTitle="Chỉ tài khoản Tiếp nhận hoặc Quản trị dùng được màn này." />;
  }

  const set = (field: Field, value: string) => {
    setForm((f) => ({ ...f, [field]: value }));
    if (errors[field]) setErrors((e) => ({ ...e, [field]: undefined }));
  };

  const focusAfter = (field: Field) => {
    const next = ORDER[ORDER.indexOf(field) + 1];
    if (next) inputs.current.get(next)?.focus();
    else codeRef.current?.focus();
  };

  const validate = (): boolean => {
    const next: Partial<Record<Field, string>> = {};
    if (!existing) {
      if (!form.fullName.trim()) next.fullName = 'Nhập họ tên';
      else if (form.fullName.trim().length > 200) next.fullName = 'Họ tên tối đa 200 ký tự';
      if (!parseSex(form.sex)) next.sex = 'N = Nam, U = Nữ, K = Khác';
      if (!parseBirth(form.birth)) next.birth = 'Gõ năm sinh (1985) hoặc ngày sinh (12/03/1985)';
    }
    setErrors(next);
    return Object.keys(next).length === 0;
  };

  const submit = () => {
    if (save.isPending) return;
    if (chosen.length === 0) {
      setCodeError('Chưa chọn dịch vụ nào — gõ mã rồi Enter');
      codeRef.current?.focus();
      return;
    }
    if (!validate()) {
      const first = ORDER.find((f) => errors[f]) ?? ORDER.find((f) => (f === 'fullName' && !form.fullName.trim()) || (f === 'sex' && !parseSex(form.sex)) || (f === 'birth' && !parseBirth(form.birth)));
      if (first) inputs.current.get(first)?.focus();
      return;
    }
    const birth = parseBirth(form.birth);
    save.mutate({
      patientId: existing?.id ?? null,
      patient: existing ? null : {
        fullName: form.fullName.trim(), sex: parseSex(form.sex)!, dob: birth?.dob ?? null, birthYear: birth?.birthYear ?? null,
        phone: form.phone.trim() || null,
      },
      serviceIds: chosen.map((s) => s.id),
      orderingDoctor: form.doctor.trim() || null,
      diagnosis: form.diagnosis.trim() || null,
    });
  };

  const addCode = () => {
    const found = findServiceByCode(services, code);
    if (!found) {
      setCodeError(code.trim() ? `Không có dịch vụ mã "${code.trim()}" đang dùng` : 'Gõ mã dịch vụ');
      codeRef.current?.select();
      return;
    }
    if (chosen.some((s) => s.id === found.id)) {
      setCodeError(`${found.code} đã có trong danh sách`);
      codeRef.current?.select();
      return;
    }
    setChosen((c) => [...c, found]);
    setActiveRow(chosen.length);
    setCode('');
    setCodeError(null);
  };

  const removeAt = (index: number) => {
    setChosen((c) => c.filter((_, i) => i !== index));
    setActiveRow((i) => Math.max(0, Math.min(i, chosen.length - 2)));
  };

  // Ctrl+Enter ở bất kỳ ô nào → lưu.
  const onPageKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); submit(); }
    else if (e.key === 'F9') { e.preventDefault(); submit(); }
  };

  const onCodeKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter' && !e.ctrlKey && !e.metaKey) { e.preventDefault(); addCode(); }
    else if (e.key === 'Delete' && code === '' && chosen.length > 0) { e.preventDefault(); removeAt(activeRow); }
    else if (e.key === 'ArrowDown' && code === '') { e.preventDefault(); setActiveRow((i) => Math.min(chosen.length - 1, i + 1)); }
    else if (e.key === 'ArrowUp' && code === '') { e.preventDefault(); setActiveRow((i) => Math.max(0, i - 1)); }
  };

  const field = (name: Field, label: string, props: { placeholder?: string; hint?: string; width?: number | string } = {}) => (
    <Flex vertical gap={4} style={{ width: props.width ?? '100%' }}>
      <Typography.Text strong>{label}</Typography.Text>
      <Input
        ref={(el) => { inputs.current.set(name, el); }} aria-label={label} value={form[name]} placeholder={props.placeholder}
        status={errors[name] ? 'error' : undefined} disabled={!!existing && ['fullName', 'sex', 'birth', 'phone'].includes(name)}
        onChange={(e) => set(name, e.target.value)}
        onPressEnter={(e) => { if (!e.ctrlKey && !e.metaKey) focusAfter(name); }}
      />
      <Typography.Text type={errors[name] ? 'danger' : 'secondary'} style={{ fontSize: 12, minHeight: 18 }}>{errors[name] ?? props.hint ?? ''}</Typography.Text>
    </Flex>
  );

  const copy = async (text: string) => {
    try { await navigator.clipboard.writeText(text); message.success(`Đã chép ${text}`); } catch { message.error('Không chép được — chọn và sao chép bằng tay'); }
  };

  return (
    <div data-screen="tiep-nhan" onKeyDown={onPageKeyDown}>
      <div className="page-header">
        <div>
          <h1 className="page-title">Tiếp nhận</h1>
          <Typography.Text type="secondary">Bản web chưa in tem — chép barcode để dán vào máy hoặc bộ giả lập. Enter chuyển ô · mã dịch vụ + Enter để thêm · Ctrl+Enter lưu.</Typography.Text>
        </div>
        <Button type="primary" icon={<SaveOutlined />} loading={save.isPending} onClick={submit}>Lưu tiếp nhận (Ctrl+Enter)</Button>
      </div>

      {save.error && <Alert type="error" showIcon message={(save.error as Error).message} style={{ marginBottom: 12 }} />}

      <div className="split-page" style={{ gridTemplateColumns: 'minmax(360px, 5fr) minmax(360px, 6fr)' }}>
        <Card size="small" title={<Flex align="center" gap={8}><UserOutlined />Người bệnh</Flex>}>
          <Flex vertical gap={4}>
            <Typography.Text strong>Tìm người bệnh có sẵn</Typography.Text>
            <AutoComplete
              aria-label="Tìm người bệnh có sẵn" value={search} placeholder="Mã, họ tên hoặc số điện thoại (≥ 2 ký tự)" allowClear
              onChange={(v) => { setSearch(v); if (existing) setExisting(null); }}
              onSelect={(_, option) => {
                const hit = (patients.data ?? []).find((p) => p.id === option.value);
                if (hit) { setExisting(hit); setSearch(`${hit.code} · ${hit.fullName}`); setForm((f) => ({ ...f, fullName: hit.fullName, sex: hit.sex === 'M' ? 'N' : hit.sex === 'F' ? 'U' : 'K', birth: birthText(hit), phone: hit.phone ?? '' })); inputs.current.get('doctor')?.focus(); }
              }}
              options={(patients.data ?? []).map((p) => ({ value: p.id, label: `${p.code} · ${p.fullName} · ${SEX_LABEL[p.sex]} · ${birthText(p)}${p.phone ? ` · ${p.phone}` : ''}` }))}
            />
            <Typography.Text type="secondary" style={{ fontSize: 12, minHeight: 18 }}>
              {existing ? <>Đang dùng người bệnh <b className="code">{existing.code}</b> — <a onClick={() => { setExisting(null); setSearch(''); setForm({ ...EMPTY }); inputs.current.get('fullName')?.focus(); }}>Người bệnh mới</a></> : 'Không tìm thấy thì nhập mới bên dưới.'}
            </Typography.Text>
          </Flex>
          {field('fullName', 'Họ tên', { placeholder: 'vd: Nguyễn Văn Thử' })}
          <Flex gap={12}>
            {field('sex', 'Giới', { placeholder: 'N / U / K', hint: 'N = Nam, U = Nữ, K = Khác', width: 160 })}
            {field('birth', 'Năm sinh hoặc ngày sinh', { placeholder: '1985 hoặc 12/03/1985' })}
          </Flex>
          {field('phone', 'Số điện thoại', { placeholder: 'không bắt buộc' })}
          <Flex gap={12}>
            {field('doctor', 'Bác sĩ chỉ định', { placeholder: 'không bắt buộc' })}
            {field('diagnosis', 'Chẩn đoán', { placeholder: 'không bắt buộc' })}
          </Flex>
        </Card>

        <Card size="small" title="Dịch vụ xét nghiệm">
          <Flex vertical gap={4}>
            <Typography.Text strong>Mã dịch vụ</Typography.Text>
            <Input ref={codeRef} aria-label="Mã dịch vụ" value={code} placeholder="gõ mã rồi Enter (vd GLU, CBC)" status={codeError ? 'error' : undefined}
              onChange={(e) => { setCode(e.target.value); if (codeError) setCodeError(null); }} onKeyDown={onCodeKeyDown} autoComplete="off" />
            <Typography.Text type={codeError ? 'danger' : 'secondary'} style={{ fontSize: 12, minHeight: 18 }}>
              {codeError ?? (catalog.isLoading ? 'Đang tải danh mục…' : `${services.length} dịch vụ đang dùng · ↑/↓ chọn dòng, Delete xoá`)}
            </Typography.Text>
          </Flex>
          <Table<CatalogService>
            rowKey="id" size="small" bordered pagination={false} dataSource={chosen} style={{ marginTop: 8 }}
            rowClassName={(_, i) => (i === activeRow ? 'row-active' : '')}
            onRow={(_, i) => ({ onClick: () => setActiveRow(i ?? 0) })}
            locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Chưa chọn dịch vụ nào" /> }}
            columns={[
              { title: 'Mã', dataIndex: 'code', width: 110, render: (v: string, s) => <Flex gap={6} align="center"><span className="code">{v}</span>{s.isPanel && <Tag>Gói</Tag>}</Flex> },
              { title: 'Tên', dataIndex: 'name' },
              { title: 'Loại mẫu', dataIndex: 'specimenName', width: 140, render: (v: string | null) => v ?? '' },
              { title: '', width: 44, render: (_, __, i) => <Button size="small" type="text" danger icon={<DeleteOutlined />} aria-label="Xoá dịch vụ" onClick={() => removeAt(i)} /> },
            ]}
          />
        </Card>
      </div>

      {created && (
        <Card size="small" style={{ marginTop: 16, borderColor: 'var(--c-brand)' }}
          title={<span>Đã tiếp nhận · người bệnh <span className="code">{created.patientCode}</span> · {created.samples.length} mẫu</span>}
          extra={<Button size="small" onClick={() => setCreated(null)}>Đóng</Button>}>
          <Flex gap={12} wrap>
            {created.samples.map((s) => (
              <Card key={s.sampleId} size="small" style={{ minWidth: 280 }} data-testid="sample-card">
                <Flex vertical gap={6}>
                  <Flex align="center" gap={8}>
                    <span className="code" style={{ fontSize: 26, letterSpacing: 2 }}>{s.barcode}</span>
                    <Button size="small" icon={<CopyOutlined />} onClick={() => copy(s.barcode)}>Chép</Button>
                  </Flex>
                  <Typography.Text strong>{s.specimenName}</Typography.Text>
                  <Typography.Text type="secondary">{s.serviceNames.join(', ')}</Typography.Text>
                </Flex>
              </Card>
            ))}
          </Flex>
        </Card>
      )}
    </div>
  );
}
