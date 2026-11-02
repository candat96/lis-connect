// LIS Agent — giao diện tại chỗ (không cần build): chỉ định, kết quả, máy xét nghiệm, kết nối HIS/CIS, nhật ký.
'use strict';

const $ = (s, el = document) => el.querySelector(s);
const $$ = (s, el = document) => [...el.querySelectorAll(s)];
const esc = (v) => String(v ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const STATUS = { cho: 'Chờ máy', 'dang-chay': 'Đang chạy', 'co-ket-qua': 'Có kết quả', 'da-gui': 'Đã trả HIS', huy: 'Đã hủy' };
const pad = (n) => String(n).padStart(2, '0');
const today = () => { const d = new Date(); return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`; };
const time = (iso) => { if (!iso) return ''; const d = new Date(iso); return `${pad(d.getHours())}:${pad(d.getMinutes())}`; };
const dt = (iso) => { if (!iso) return ''; const d = new Date(iso); return `${pad(d.getDate())}/${pad(d.getMonth() + 1)} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`; };
const dob = (v) => (v ? v.split('-').reverse().join('/') : '');
const upper = (s) => s.toLocaleUpperCase('vi').replace(/\s+/g, ' ').trim();

async function api(path, opts = {}) {
  const res = await fetch('/ui/api/' + path, {
    method: opts.method || 'GET', credentials: 'same-origin',
    headers: opts.body ? { 'Content-Type': 'application/json' } : {},
    body: opts.body ? JSON.stringify(opts.body) : undefined,
  });
  const data = await res.json().catch(() => ({}));
  if (res.status === 401 && path !== 'login') { showGate(); throw new Error('Cần đăng nhập'); }
  if (!res.ok) throw new Error(data.message || ('Lỗi ' + res.status));
  return data;
}

function toast(msg, bad) {
  const t = $('#toast');
  t.textContent = msg; t.className = 'toast' + (bad ? ' bad' : ''); t.hidden = false;
  clearTimeout(toast.h); toast.h = setTimeout(() => (t.hidden = true), bad ? 5000 : 2500);
}

// ============================================================ đăng nhập
let needSetup = false;
async function boot() {
  const me = await fetch('/ui/api/me').then((r) => r.json());
  needSetup = me.needSetup;
  $('#site').textContent = me.siteName || '';
  if (!me.loggedIn) return showGate();
  $('#gate').hidden = true; $('#app').hidden = false;
  start();
}
function showGate() {
  $('#app').hidden = true; $('#gate').hidden = false;
  $('#gateHint').textContent = needSetup ? 'Lần đầu sử dụng: đặt mật khẩu quản trị cho Agent (tối thiểu 6 ký tự).' : 'Nhập mật khẩu quản trị để vào.';
  $('#siteRow').hidden = !needSetup;
  $('#gateBtn').textContent = needSetup ? 'Đặt mật khẩu và vào' : 'Đăng nhập';
  setTimeout(() => (needSetup ? $('#gateSite') : $('#gatePw')).focus(), 50);
}
$('#gateForm').addEventListener('submit', async (e) => {
  e.preventDefault(); $('#gateErr').textContent = '';
  try {
    if (needSetup) await api('setup', { method: 'POST', body: { password: $('#gatePw').value, siteName: $('#gateSite').value } });
    else await api('login', { method: 'POST', body: { password: $('#gatePw').value } });
    $('#gatePw').value = '';
    boot();
  } catch (err) { $('#gateErr').textContent = err.message; }
});
$('#logout').onclick = async () => { await api('logout', { method: 'POST' }).catch(() => {}); location.reload(); };

// ============================================================ tab
let tab = 'orders';
function setTab(t) {
  tab = t;
  $$('#tabs button').forEach((b) => b.classList.toggle('on', b.dataset.tab === t));
  $$('.tab').forEach((s) => (s.hidden = s.id !== 'tab-' + t));
  ({ orders: loadOrders, results: loadResults, devices: loadDevices, his: loadHis, logs: loadLogs })[t]();
}
$$('#tabs button').forEach((b) => (b.onclick = () => setTab(b.dataset.tab)));

let started = false;
function start() {
  if (started) return; started = true;
  $('#oDate').value = today();
  api('devices').then((d) => (devCache = d.devices)).catch(() => {});
  setTab('orders');
  refreshHealth();
  setInterval(() => { refreshHealth(); if (tab === 'orders' && $('#oForm').hidden) loadOrders(true); if (tab === 'results') loadResults(true); }, 5000);
}

async function refreshHealth() {
  try {
    const s = await api('stats');
    $('#health').innerHTML = (s.devices || []).map((d) => {
      const dev = devCache.find((x) => x.id === d.id);
      const name = dev ? dev.name : d.id;
      const cls = d.connected ? 'ok' : d.lastError === 'đang tắt' ? 'off' : 'bad';
      return `<span class="pill ${cls}" title="${esc(d.lastError || 'Đã kết nối')}">${esc(name)}</span>`;
    }).join('') + (s.outbox ? `<span class="pill bad" title="Kết quả chờ gửi HIS">${s.outbox} chờ gửi HIS</span>` : '');
    const b = $('#unmatchedBadge'); b.hidden = !s.unmatched; b.textContent = s.unmatched;
    $('#outboxInfo').textContent = s.outbox ? `${s.outbox} kết quả đang chờ gửi HIS` : 'Không có kết quả chờ gửi HIS';
  } catch { /* bỏ qua */ }
}

// ============================================================ chỉ định
let orders = [], oStatus = '', selSid = null;
$$('#oStatus button').forEach((b) => (b.onclick = () => { oStatus = b.dataset.v; $$('#oStatus button').forEach((x) => x.classList.toggle('on', x === b)); renderOrders(); }));
$('#oDate').onchange = () => loadOrders();
$('#oQ').oninput = () => { clearTimeout(loadOrders.h); loadOrders.h = setTimeout(() => loadOrders(), 250); };

async function loadOrders(silent) {
  try {
    orders = await api(`orders?date=${encodeURIComponent($('#oDate').value)}&q=${encodeURIComponent($('#oQ').value)}`);
    renderOrders();
    if (selSid && !silent) showOrder(selSid);
  } catch (e) { if (!silent) toast(e.message, true); }
}
function renderOrders() {
  const counts = {};
  orders.forEach((o) => (counts[o.status] = (counts[o.status] || 0) + 1));
  $('#cAll').textContent = orders.length;
  Object.keys(STATUS).forEach((k) => ($('#c-' + k).textContent = counts[k] || 0));
  const rows = orders.filter((o) => !oStatus || o.status === oStatus);
  $('#oTable tbody').innerHTML = rows.map((o) => `
    <tr data-sid="${esc(o.sid)}" class="${o.sid === selSid ? 'sel' : ''}">
      <td class="sid">${esc(o.sid)}</td>
      <td><b>${esc(o.patient.name)}</b><div class="muted small">${esc(o.patient.code)}${o.patient.dob ? ' · ' + dob(o.patient.dob) : ''}</div></td>
      <td>${o.tests.map((t) => esc(t.code)).join(', ')}</td>
      <td>${o.source === 'his' ? 'HIS' : 'Nhập tay'}</td>
      <td><span class="tag t-${o.status}">${STATUS[o.status] || o.status}</span></td>
      <td>${time(o.createdAt)}</td>
    </tr>`).join('');
  $('#oEmpty').hidden = rows.length > 0;
  $$('#oTable tbody tr').forEach((tr) => (tr.onclick = () => showOrder(tr.dataset.sid)));
}
async function showOrder(sid) {
  selSid = sid; renderOrders();
  $('#oForm').hidden = true; $('#oDetail').hidden = false;
  const o = orders.find((x) => x.sid === sid);
  if (!o) return;
  const results = await api('results?sid=' + encodeURIComponent(sid)).catch(() => []);
  $('#oDetail').className = 'detail';
  $('#oDetail').innerHTML = `
    <h3>SID <span class="sid">${esc(o.sid)}</span> <span class="tag t-${o.status}">${STATUS[o.status]}</span></h3>
    <div class="kv">
      <span>Người bệnh</span><b>${esc(o.patient.name)}</b>
      <span>Mã / ngày sinh</span><span>${esc(o.patient.code || '—')} · ${dob(o.patient.dob) || '—'} · ${o.patient.gender === 'male' ? 'Nam' : o.patient.gender === 'female' ? 'Nữ' : '—'}</span>
      <span>Xét nghiệm</span><span>${o.tests.map((t) => `<b>${esc(t.code)}</b>${t.name ? ' ' + esc(t.name) : ''}`).join(', ')}</span>
      <span>Bác sĩ / phòng</span><span>${esc(o.doctor || '—')} · ${esc(o.room || '—')}</span>
      <span>Nguồn</span><span>${o.source === 'his' ? 'HIS/CIS gửi' : 'Nhập tay'}${o.hisOrderId ? ' · mã HIS ' + esc(o.hisOrderId) : ''}</span>
      <span>Tạo lúc</span><span>${dt(o.createdAt)}</span>
      ${o.note ? `<span>Ghi chú</span><span>${esc(o.note)}</span>` : ''}
    </div>
    ${results.length ? results.map(resultBlock).join('') : '<div class="muted">Máy chưa trả kết quả cho SID này.</div>'}
    <div class="actions">
      ${o.status === 'huy' ? '<button id="oRestore">Khôi phục</button>' : '<button id="oCancelBtn" class="danger">Hủy chỉ định</button>'}
    </div>`;
  const c = $('#oCancelBtn');
  if (c) c.onclick = async () => { if (!confirm(`Hủy chỉ định SID ${o.sid}? Máy hỏi SID này sẽ không nhận được xét nghiệm.`)) return; await api(`orders/${encodeURIComponent(o.sid)}/cancel`, { method: 'POST' }); toast('Đã hủy chỉ định'); loadOrders(); };
  const r = $('#oRestore');
  if (r) r.onclick = async () => { await api(`orders/${encodeURIComponent(o.sid)}/restore`, { method: 'POST' }); toast('Đã khôi phục'); loadOrders(); };
}
function resultBlock(r) {
  return `<div>
    <div class="small muted">${esc(r.deviceName)} · ${dt(r.receivedAt)} · ${r.sentAt ? '<span class="tag t-da-gui">Đã trả HIS</span>' : r.sendError ? `<span class="tag t-bad" title="${esc(r.sendError)}">Gửi HIS lỗi</span>` : r.matched ? '<span class="tag t-cho">Chờ gửi HIS</span>' : '<span class="tag t-bad">Chưa khớp</span>'}</div>
    <table class="grid items"><thead><tr><th>Mã máy</th><th>Mã HIS</th><th>Tên</th><th>Kết quả</th><th>Đơn vị</th><th>Cờ</th></tr></thead><tbody>
    ${r.items.map((i) => `<tr><td>${esc(i.deviceCode)}</td><td>${i.testCode ? esc(i.testCode) : '<span class="tag t-bad">chưa ánh xạ</span>'}</td><td>${esc(i.testName)}</td><td class="flag-${esc(i.flag)}">${esc(i.value)}</td><td>${esc(i.unit)}</td><td class="flag-${esc(i.flag)}">${esc(i.flag)}</td></tr>`).join('')}
    </tbody></table></div>`;
}

// ---- nhập chỉ định (Enter chuyển ô, Enter ở ô xét nghiệm thêm mã, F9 lưu, Esc thoát)
let formTests = [];
const formFields = ['fSid', 'fCode', 'fName', 'fDob', 'fGender', 'fTest', 'fDoctor', 'fRoom', 'fNote'];
async function openForm() {
  if (tab !== 'orders') setTab('orders');
  formTests = []; renderChips();
  formFields.forEach((id) => ($('#' + id).value = ''));
  $('#fErr').textContent = '';
  $('#oDetail').hidden = true; $('#oForm').hidden = false;
  $('#fSid').focus();
  const tests = await api('tests').catch(() => []);
  $('#testList').innerHTML = tests.map((t) => `<option value="${esc(t.code)}">${esc(t.name)}</option>`).join('');
  openForm.known = tests;
}
function closeForm() { $('#oForm').hidden = true; $('#oDetail').hidden = false; }
function renderChips() {
  $('#fTests').innerHTML = formTests.map((t, i) => `<span class="chip">${esc(t.code)}${t.name ? ' · ' + esc(t.name) : ''}<button type="button" data-i="${i}" aria-label="Bỏ ${esc(t.code)}">×</button></span>`).join('');
  $$('#fTests button').forEach((b) => (b.onclick = () => { formTests.splice(+b.dataset.i, 1); renderChips(); $('#fTest').focus(); }));
}
function addTest() {
  const raw = $('#fTest').value.trim();
  if (!raw) return false;
  raw.split(/[,;\s]+/).filter(Boolean).forEach((code) => {
    const c = code.toUpperCase();
    if (formTests.some((t) => t.code.toUpperCase() === c)) return;
    const known = (openForm.known || []).find((t) => t.code.toUpperCase() === c);
    formTests.push({ code: known ? known.code : c, name: known ? known.name : '' });
  });
  $('#fTest').value = ''; renderChips();
  return true;
}
// Ngày sinh: dd/mm/yyyy, ddmmyyyy hoặc chỉ năm
function parseDob(v) {
  v = v.trim(); if (!v) return '';
  let m = v.match(/^(\d{1,2})[/.-](\d{1,2})[/.-](\d{4})$/) || v.match(/^(\d{2})(\d{2})(\d{4})$/);
  if (m) return `${m[3]}-${pad(m[2])}-${pad(m[1])}`;
  if (/^\d{4}$/.test(v)) return `${v}-01-01`;
  if (/^\d{4}-\d{2}-\d{2}$/.test(v)) return v;
  throw new Error('Ngày sinh nhập dd/mm/yyyy hoặc năm');
}
$('#fName').addEventListener('blur', () => ($('#fName').value = upper($('#fName').value)));
$('#oForm').addEventListener('keydown', (e) => {
  if (e.key !== 'Enter' || e.target.tagName === 'BUTTON') return;
  e.preventDefault();
  if (e.target.id === 'fTest' && addTest()) return;
  const i = formFields.indexOf(e.target.id);
  if (i >= 0 && i < formFields.length - 1) $('#' + formFields[i + 1]).focus();
  else saveOrder();
});
$('#oForm').addEventListener('submit', (e) => { e.preventDefault(); saveOrder(); });
$('#fCancel').onclick = closeForm;
$('#newOrder').onclick = openForm;
async function saveOrder() {
  addTest();
  try {
    const body = {
      sid: $('#fSid').value.trim(), hisOrderId: '',
      patient: { code: $('#fCode').value.trim(), name: upper($('#fName').value), dob: parseDob($('#fDob').value), gender: $('#fGender').value },
      tests: formTests, doctor: $('#fDoctor').value.trim(), room: $('#fRoom').value.trim(), note: $('#fNote').value.trim(),
    };
    const saved = await api('orders', { method: 'POST', body });
    toast(`Đã lưu chỉ định SID ${saved.sid}`);
    selSid = saved.sid;
    await loadOrders();
    openForm(); // nhập tiếp ống sau
  } catch (err) { $('#fErr').textContent = err.message; }
}

// ============================================================ kết quả
let rUnmatched = '', results = [], selResult = null;
$$('#rFilter button').forEach((b) => (b.onclick = () => { rUnmatched = b.dataset.v; $$('#rFilter button').forEach((x) => x.classList.toggle('on', x === b)); loadResults(); }));
$('#rSid').oninput = () => { clearTimeout(loadResults.h); loadResults.h = setTimeout(() => loadResults(), 250); };
async function loadResults(silent) {
  try {
    results = await api(`results?unmatched=${rUnmatched}&sid=${encodeURIComponent($('#rSid').value)}`);
    $('#rTable tbody').innerHTML = results.map((r) => `
      <tr data-id="${esc(r.id)}" class="${r.id === selResult ? 'sel' : ''}">
        <td>${dt(r.receivedAt)}</td><td class="sid">${esc(r.sid || '—')}</td><td>${esc(r.deviceName)}</td><td>${r.items.length}</td>
        <td>${r.matched ? '<span class="tag t-da-gui">Khớp</span>' : '<span class="tag t-bad">Chưa khớp</span>'}</td>
        <td>${r.sentAt ? '<span class="tag t-da-gui">Đã gửi</span>' : r.sendError ? `<span class="tag t-bad" title="${esc(r.sendError)}">Lỗi</span>` : r.matched ? '<span class="tag t-cho">Chờ</span>' : '—'}</td>
      </tr>`).join('');
    $('#rEmpty').hidden = results.length > 0;
    $$('#rTable tbody tr').forEach((tr) => (tr.onclick = () => showResult(tr.dataset.id)));
    if (selResult && !silent) showResult(selResult);
  } catch (e) { if (!silent) toast(e.message, true); }
}
function showResult(id) {
  selResult = id;
  $$('#rTable tbody tr').forEach((tr) => tr.classList.toggle('sel', tr.dataset.id === id));
  const r = results.find((x) => x.id === id);
  if (!r) return;
  $('#rDetail').className = 'detail';
  $('#rDetail').innerHTML = `
    <h3>SID <span class="sid">${esc(r.sid || '—')}</span></h3>
    ${resultBlock(r)}
    ${r.sendError ? `<div class="err">Gửi HIS lỗi: ${esc(r.sendError)}</div>` : ''}
    ${!r.matched ? `<label class="small"><b>Gán cho SID có chỉ định</b><div class="inline"><input id="assignSid" placeholder="SID đúng"><button id="assignBtn" class="primary">Gán</button></div></label>` : ''}
    <div class="actions">${r.matched ? '<button id="resendBtn">Gửi lại HIS</button>' : ''}</div>
    <details><summary class="small muted">Bản tin gốc từ máy</summary><div class="raw">${esc(r.raw)}</div></details>`;
  const a = $('#assignBtn');
  if (a) a.onclick = async () => { try { await api(`results/${encodeURIComponent(r.id)}/assign`, { method: 'POST', body: { sid: $('#assignSid').value } }); toast('Đã gán, đang gửi HIS'); loadResults(); refreshHealth(); } catch (e) { toast(e.message, true); } };
  const s = $('#resendBtn');
  if (s) s.onclick = async () => { await api(`results/${encodeURIComponent(r.id)}/resend`, { method: 'POST' }); toast('Đã xếp gửi lại'); setTimeout(() => loadResults(), 1500); };
}

// ============================================================ máy xét nghiệm
let devCache = [], ports = [];
async function loadDevices() {
  const d = await api('devices');
  devCache = d.devices; ports = d.serialPorts || [];
  renderDevices(d.status || []);
}
function devCard(d, st, i) {
  const conn = st ? (st.connected ? '<span class="tag t-da-gui">Đã kết nối</span>' : `<span class="tag t-bad" title="${esc(st.lastError)}">${esc(st.lastError || 'Chưa kết nối')}</span>`) : '';
  const portOpts = [...new Set([d.port, ...ports].filter(Boolean))].map((p) => `<option ${p === d.port ? 'selected' : ''}>${esc(p)}</option>`).join('');
  return `<div class="card dev" data-i="${i}">
    <div class="dev-head">
      <input data-k="name" value="${esc(d.name)}" placeholder="Tên máy (vd Sinh hóa AU480)">
      <label class="small" style="flex-direction:row;align-items:center;gap:4px"><input type="checkbox" data-k="enabled" ${d.enabled ? 'checked' : ''} style="height:auto"> Bật</label>
      ${conn}
      <button class="ghost danger" data-del="${i}" title="Xóa máy">Xóa</button>
    </div>
    ${st ? `<div class="small muted">Đã nhận ${st.results} mẫu · máy hỏi ${st.queries} lần${st.lastActivity ? ' · lần cuối ' + dt(st.lastActivity) : ''}</div>` : ''}
    <div class="row3">
      <label>Giao thức<select data-k="protocol"><option value="astm" ${d.protocol === 'astm' ? 'selected' : ''}>ASTM E1394</option><option value="hl7" ${d.protocol === 'hl7' ? 'selected' : ''}>HL7 v2 (MLLP)</option></select></label>
      <label>Kết nối<select data-k="transport">
        <option value="serial" ${d.transport === 'serial' ? 'selected' : ''}>Cổng COM</option>
        <option value="tcp-server" ${d.transport === 'tcp-server' ? 'selected' : ''}>TCP – Agent chờ máy nối vào</option>
        <option value="tcp-client" ${d.transport === 'tcp-client' ? 'selected' : ''}>TCP – Agent nối tới máy</option></select></label>
      <span></span>
    </div>
    <div class="row3" ${d.transport === 'serial' ? '' : 'hidden'}>
      <label>Cổng COM<select data-k="port"><option value="">—</option>${portOpts}</select></label>
      <label>Tốc độ (baud)<select data-k="baud">${[1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200].map((b) => `<option ${+(d.baud || 9600) === b ? 'selected' : ''}>${b}</option>`).join('')}</select></label>
      <label>Data / Parity / Stop<div class="inline">
        <select data-k="dataBits"><option ${d.dataBits === 7 ? 'selected' : ''}>7</option><option ${d.dataBits !== 7 ? 'selected' : ''}>8</option></select>
        <select data-k="parity"><option value="none">N</option><option value="even" ${d.parity === 'even' ? 'selected' : ''}>E</option><option value="odd" ${d.parity === 'odd' ? 'selected' : ''}>O</option></select>
        <select data-k="stopBits"><option ${d.stopBits !== 2 ? 'selected' : ''}>1</option><option ${d.stopBits === 2 ? 'selected' : ''}>2</option></select></div></label>
    </div>
    <div class="row3" ${d.transport === 'serial' ? 'hidden' : ''}>
      <label>${d.transport === 'tcp-server' ? 'IP lắng nghe (trống = mọi IP)' : 'IP của máy'}<input data-k="host" value="${esc(d.host)}"></label>
      <label>Cổng TCP<input data-k="tcpPort" type="number" value="${esc(d.tcpPort || '')}"></label>
      <span></span>
    </div>
    <div>
      <div class="small"><b>Ánh xạ mã</b> <span class="muted">— mã chỉ số trên máy ↔ mã xét nghiệm HIS. Để trống bảng thì dùng nguyên mã máy.</span></div>
      <table class="map"><thead><tr><th>Mã trên máy</th><th>Mã HIS</th><th>Tên</th><th></th></tr></thead><tbody>
      ${(d.mappings || []).map((m, j) => `<tr><td><input data-m="${j}" data-f="deviceCode" value="${esc(m.deviceCode)}"></td><td><input data-m="${j}" data-f="testCode" value="${esc(m.testCode)}"></td><td><input data-m="${j}" data-f="testName" value="${esc(m.testName)}"></td><td><button class="ghost" data-mdel="${j}">×</button></td></tr>`).join('')}
      </tbody></table>
      <button class="ghost" data-madd="${i}">+ Thêm dòng</button>
    </div>
  </div>`;
}
function renderDevices(status) {
  $('#devList').innerHTML = devCache.length ? devCache.map((d, i) => devCard(d, (status || []).find((s) => s.id === d.id), i)).join('') : '<div class="empty card">Chưa có máy nào. Bấm <b>+ Thêm máy</b>.</div>';
  $$('#devList .dev').forEach((card) => {
    const d = devCache[+card.dataset.i];
    $$('[data-k]', card).forEach((el) => (el.onchange = () => {
      const k = el.dataset.k;
      d[k] = el.type === 'checkbox' ? el.checked : ['baud', 'dataBits', 'stopBits', 'tcpPort'].includes(k) ? +el.value : el.value;
      if (k === 'transport') renderDevices(status);
    }));
    $$('[data-m]', card).forEach((el) => (el.oninput = () => (d.mappings[+el.dataset.m][el.dataset.f] = el.value.trim())));
    $$('[data-mdel]', card).forEach((b) => (b.onclick = () => { d.mappings.splice(+b.dataset.mdel, 1); renderDevices(status); }));
    $$('[data-madd]', card).forEach((b) => (b.onclick = () => { d.mappings.push({ deviceCode: '', testCode: '', testName: '' }); renderDevices(status); }));
    $$('[data-del]', card).forEach((b) => (b.onclick = () => { if (confirm(`Xóa máy ${d.name || ''}?`)) { devCache.splice(+b.dataset.del, 1); renderDevices(status); } }));
  });
}
$('#addDevice').onclick = () => {
  devCache.push({ id: '', name: '', enabled: true, protocol: 'astm', transport: 'serial', baud: 9600, dataBits: 8, parity: 'none', stopBits: 1, mappings: [] });
  renderDevices([]);
  const inputs = $$('#devList .dev-head input[data-k="name"]'); inputs[inputs.length - 1].focus();
};
$('#saveDevices').onclick = async () => {
  devCache.forEach((d) => (d.mappings = (d.mappings || []).filter((m) => m.deviceCode && m.testCode)));
  try { const r = await api('devices', { method: 'PUT', body: devCache }); devCache = r.devices; toast('Đã lưu, đang kết nối lại các máy'); setTimeout(loadDevices, 1500); } catch (e) { toast(e.message, true); }
};

// ============================================================ HIS/CIS
const rnd = (n) => { const a = new Uint8Array(n); crypto.getRandomValues(a); return [...a].map((b) => b.toString(16).padStart(2, '0')).join(''); };
$('#genKey').onclick = () => ($('#hKey').value = rnd(20));
$('#genSecret').onclick = () => ($('#hSecret').value = rnd(24));
async function loadHis() {
  const d = await api('his');
  $('#hSite').value = d.siteName || ''; $('#hName').value = d.his.name || ''; $('#hKey').value = d.his.apiKey || '';
  $('#hUrl').value = d.his.webhookUrl || ''; $('#hSecret').value = d.his.secret || '';
  renderDoc(location.host);
}
const hisBody = () => ({ name: $('#hName').value.trim(), apiKey: $('#hKey').value.trim(), webhookUrl: $('#hUrl').value.trim(), secret: $('#hSecret').value.trim() });
$('#hisForm').addEventListener('submit', async (e) => {
  e.preventDefault(); $('#hErr').textContent = '';
  try { await api('his', { method: 'PUT', body: { his: hisBody(), siteName: $('#hSite').value.trim() } }); toast('Đã lưu kết nối HIS/CIS'); $('#site').textContent = $('#hSite').value.trim(); } catch (err) { $('#hErr').textContent = err.message; }
});
$('#hTest').onclick = async () => {
  $('#hErr').textContent = '';
  try { await api('his/test', { method: 'POST', body: hisBody() }); toast('HIS đã nhận bản tin thử'); } catch (err) { $('#hErr').textContent = 'Không gửi được: ' + err.message; }
};
$('#pSave').onclick = async () => {
  try { await api('password', { method: 'POST', body: { old: $('#pOld').value, new: $('#pNew').value } }); $('#pOld').value = $('#pNew').value = ''; toast('Đã đổi mật khẩu'); } catch (e) { toast(e.message, true); }
};
function renderDoc(host) {
  const base = `http://${host}/api/v1`;
  $('#apiDoc').innerHTML = `
  <h3>Tài liệu tích hợp cho HIS/CIS</h3>
  <p>Mọi lời gọi kèm header <code>X-Api-Key: &lt;khóa API&gt;</code>, nội dung JSON UTF-8. Địa chỉ Agent: <code>${esc(base)}</code></p>
  <h4>1. Gửi chỉ định (tạo hoặc cập nhật theo SID)</h4>
  <pre>POST ${esc(base)}/orders
{
  "sid": "2611020001",            // mã vạch dán trên ống mẫu
  "hisOrderId": "CD261102-0015",  // mã phiếu chỉ định bên HIS (tùy chọn)
  "patient": { "code": "BN000123", "name": "NGUYEN VAN A", "dob": "1985-04-12", "gender": "male" },
  "tests": [ { "code": "GLU", "name": "Glucose" }, { "code": "URE" } ],
  "doctor": "BS. ...", "room": "Phòng khám 01", "note": ""
}</pre>
  <h4>2. Hủy chỉ định</h4>
  <pre>POST ${esc(base)}/orders/{sid}/cancel</pre>
  <h4>3. Nhận kết quả</h4>
  <p><b>Cách A — webhook:</b> khai báo "Địa chỉ nhận kết quả", Agent POST mỗi kết quả; HIS trả HTTP 2xx là xong, lỗi thì Agent tự gửi lại. Nếu có khóa ký: header <code>X-Signature: sha256=&lt;HMAC-SHA256(khóa, nội dung)&gt;</code>.</p>
  <p><b>Cách B — HIS tự kéo:</b> <code>GET ${esc(base)}/results?since=2026-11-02T08:00:00+07:00</code>, xử lý xong báo <code>POST ${esc(base)}/results/ack {"ids":["&lt;resultId&gt;"]}</code>.</p>
  <pre>{
  "resultId": "...", "sid": "2611020001", "hisOrderId": "CD261102-0015", "patientCode": "BN000123",
  "deviceId": "...", "deviceName": "Sinh hóa AU480", "receivedAt": "2026-11-02T09:15:02+07:00",
  "results": [ { "testCode": "GLU", "deviceCode": "GLU", "testName": "Glucose", "value": "5.6",
                 "unit": "mmol/L", "flag": "N", "refRange": "3.9-6.4" } ]
}</pre>
  <h4>4. Kiểm tra Agent còn chạy</h4>
  <pre>GET ${esc(base)}/health</pre>
  <p class="muted">Kết quả là số liệu máy gửi, chưa qua duyệt: HIS/CIS đưa vào trạng thái chờ duyệt, bác sĩ xét nghiệm duyệt mới trả cho người bệnh.</p>`;
}

// ============================================================ nhật ký
async function loadLogs() {
  const logs = await api('logs?source=' + encodeURIComponent($('#lSource').value));
  const srcs = new Set(['his', 'ui', ...devCache.map((d) => 'may:' + d.id)]);
  const cur = $('#lSource').value;
  $('#lSource').innerHTML = '<option value="">Tất cả nguồn</option>' + [...srcs].map((s) => {
    const d = devCache.find((x) => 'may:' + x.id === s);
    return `<option value="${esc(s)}" ${s === cur ? 'selected' : ''}>${esc(d ? 'Máy ' + d.name : s === 'his' ? 'HIS/CIS' : s === 'ui' ? 'Giao diện' : s)}</option>`;
  }).join('');
  $('#lTable tbody').innerHTML = logs.map((l) => `<tr><td>${dt(l.at)}</td><td class="lv-${l.level}">${l.level === 'error' ? 'Lỗi' : l.level === 'warn' ? 'Cảnh báo' : 'Thông tin'}</td><td>${esc(l.source)}</td>
    <td>${esc(l.message)}${l.raw ? `<details><summary class="small muted">Bản tin</summary><div class="raw">${esc(l.raw)}</div></details>` : ''}</td></tr>`).join('');
}
$('#lSource').onchange = loadLogs;
$('#lRefresh').onclick = loadLogs;

// ============================================================ phím tắt
document.addEventListener('keydown', (e) => {
  if (!$('#app').hidden) {
    if (e.key === 'F1') { e.preventDefault(); setTab('orders'); }
    else if (e.key === 'F2') { e.preventDefault(); openForm(); }
    else if (e.key === 'F9' && !$('#oForm').hidden) { e.preventDefault(); saveOrder(); }
    else if (e.key === 'Escape' && !$('#oForm').hidden) closeForm();
    else if (e.key === '/' && !/INPUT|SELECT|TEXTAREA/.test(document.activeElement.tagName)) { e.preventDefault(); setTab('orders'); $('#oQ').focus(); }
  }
});

boot();
