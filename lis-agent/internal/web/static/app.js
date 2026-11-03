// LIS Agent — giao diện tại chỗ (không cần build): chỉ định, kết quả, máy xét nghiệm, kết nối HIS/CIS, nhật ký.
'use strict';

const $ = (s, el = document) => el.querySelector(s);
const $$ = (s, el = document) => [...el.querySelectorAll(s)];
const esc = (v) => String(v ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const STATUS = { cho: 'Chờ máy', 'dang-chay': 'Đang chạy', 'co-ket-qua': 'Có kết quả', 'da-gui': 'Đã trả HIS', huy: 'Đã hủy' };
const pad = (n) => String(n).padStart(2, '0');
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
  if (res.status === 401 && path !== 'login' && path !== 'password') { showGate(); throw new Error('Cần đăng nhập'); }
  if (!res.ok) throw new Error(data.message || ('Lỗi ' + res.status));
  return data;
}

function toast(msg, bad) {
  const t = $('#toast');
  t.textContent = msg; t.className = 'toast' + (bad ? ' bad' : ''); t.hidden = false;
  clearTimeout(toast.h); toast.h = setTimeout(() => (t.hidden = true), bad ? 5000 : 2500);
}

// ============================================================ cài đặt, đăng nhập
let me = null;
const ROLE = { 'quan-tri': 'Quản trị LIS', ktv: 'Kỹ thuật viên' };
const isAdmin = () => me && me.role === 'quan-tri';

async function boot() {
  const info = await fetch('/ui/api/me').then((r) => r.json()).catch(() => ({}));
  $('#setup').hidden = $('#gate').hidden = $('#app').hidden = true;
  if (info.setup) return showSetup(info);
  $('#site').textContent = info.siteName || '';
  $('#gateAgent').textContent = [info.siteName, info.agent].filter(Boolean).join(' · ');
  boot.info = info;
  if (!info.loggedIn) return showGate();
  me = info.user;
  $('#app').hidden = false;
  $('#userName').textContent = me.fullName || me.username;
  $('#userRole').textContent = ROLE[me.role] || me.role;
  $('#userRole').className = 'tag ' + (isAdmin() ? 't-co-ket-qua' : 't-cho');
  $('#userSrc').textContent = `${me.username} · ${me.source === 'his' ? 'tài khoản ' + (info.hisName || 'HIS') : 'tài khoản LIS'} · Agent ${info.agent}`;
  $('#pwOpen').hidden = me.source !== 'local';
  $$('.adm').forEach((b) => (b.hidden = !isAdmin()));
  start();
}

function showSetup(info) {
  $('#setup').hidden = false;
  const db = info.db || {};
  if (db.host) $('#sHost').value = db.host;
  if (db.port) $('#sPort').value = db.port;
  if (db.name) $('#sName').value = db.name;
  if (db.user) $('#sUser').value = db.user;
  if (db.sslMode) $('#sSsl').value = db.sslMode;
  if (!$('#sAgent').value) $('#sAgent').value = info.agent || '';
  $('#setupRemote').hidden = info.local;
  $('#setupLastErr').hidden = !info.error;
  $('#setupLastErr').textContent = info.error ? 'Chưa vào được CSDL đã khai báo (Agent tự thử lại mỗi 15 giây): ' + info.error : '';
  $$('#setupForm input, #setupForm select, #setupForm button').forEach((el) => (el.disabled = !info.local));
  setTimeout(() => $('#sHost').focus(), 50);
}
const setupBody = () => ({
  db: { host: $('#sHost').value.trim(), port: +$('#sPort').value || 5432, name: $('#sName').value.trim(), user: $('#sUser').value.trim(), password: $('#sPass').value, sslMode: $('#sSsl').value },
  agent: $('#sAgent').value.trim(), siteName: $('#sSite').value.trim(),
  adminUser: $('#sAdmin').value.trim(), adminName: $('#sAdminName').value.trim(), adminPass: $('#sAdminPass').value,
});
$('#sTest').onclick = async () => {
  const m = $('#sTestMsg'); m.className = 'small muted'; m.textContent = 'Đang thử…';
  try { await api('setup/test', { method: 'POST', body: setupBody() }); m.className = 'small ok'; m.textContent = 'Kết nối được CSDL'; }
  catch (e) { m.className = 'small err'; m.textContent = e.message; }
};
$('#setupForm').addEventListener('keydown', (e) => {
  if (e.key !== 'Enter' || e.target.tagName !== 'INPUT') return;
  e.preventDefault();
  const f = $$('#setupForm input');
  const i = f.indexOf(e.target);
  if (i < f.length - 1) f[i + 1].focus(); else $('#setupBtn').click();
});
$('#setupForm').addEventListener('submit', async (e) => {
  e.preventDefault(); $('#setupErr').textContent = '';
  $('#setupBtn').disabled = true; $('#setupBtn').textContent = 'Đang tạo bảng, khởi động…';
  try {
    await api('setup', { method: 'POST', body: setupBody() });
    toast('Đã cài đặt — đăng nhập bằng tài khoản quản trị');
    setTimeout(boot, 600);
  } catch (err) { $('#setupErr').textContent = err.message; }
  finally { $('#setupBtn').disabled = false; $('#setupBtn').textContent = 'Lưu và khởi động Agent'; }
});

function showGate() {
  me = null;
  $('#app').hidden = true; $('#gate').hidden = false;
  const i = boot.info || {};
  $('#gateHint').textContent = i.hisLogin
    ? `Dùng tài khoản ${i.hisName || 'HIS/CIS'} như khi đăng nhập HIS. Khi HIS mất kết nối, dùng tài khoản LIS dự phòng.`
    : 'Đăng nhập bằng tài khoản LIS.';
  setTimeout(() => $('#gateUser').focus(), 50);
}
$('#gateUser').addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); $('#gatePw').focus(); } });
$('#gateForm').addEventListener('submit', async (e) => {
  e.preventDefault(); $('#gateErr').textContent = '';
  $('#gateBtn').disabled = true;
  try {
    await api('login', { method: 'POST', body: { username: $('#gateUser').value, password: $('#gatePw').value } });
    $('#gatePw').value = '';
    boot();
  } catch (err) { $('#gateErr').textContent = err.message; $('#gatePw').select(); }
  finally { $('#gateBtn').disabled = false; }
});
$('#logout').onclick = async () => { await api('logout', { method: 'POST' }).catch(() => {}); location.reload(); };
$('#userBtn').onclick = (e) => { e.stopPropagation(); $('#userPop').hidden = !$('#userPop').hidden; };
document.addEventListener('click', (e) => { if (!e.target.closest('.user-menu')) $('#userPop').hidden = true; });
$('#pwOpen').onclick = () => { $('#userPop').hidden = true; $('#pwModal').hidden = false; $('#pErr').textContent = ''; $('#pOld').value = $('#pNew').value = ''; $('#pOld').focus(); };
$('#pClose').onclick = () => ($('#pwModal').hidden = true);
$('#pwForm').addEventListener('submit', async (e) => {
  e.preventDefault();
  try { await api('password', { method: 'POST', body: { old: $('#pOld').value, new: $('#pNew').value } }); $('#pwModal').hidden = true; toast('Đã đổi mật khẩu'); }
  catch (err) { $('#pErr').textContent = err.message; }
});

// ============================================================ khoảng ngày (như HIS: chọn nhanh + từ/đến)
const ymd = (d) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
function presetRange(p) {
  const t = new Date(); const d = (n) => { const x = new Date(t); x.setDate(x.getDate() + n); return ymd(x); };
  switch (p) {
    case 'yesterday': return [d(-1), d(-1)];
    case '7d': return [d(-6), d(0)];
    case '30d': return [d(-29), d(0)];
    case 'month': return [ymd(new Date(t.getFullYear(), t.getMonth(), 1)), d(0)];
    case 'lastMonth': return [ymd(new Date(t.getFullYear(), t.getMonth() - 1, 1)), ymd(new Date(t.getFullYear(), t.getMonth(), 0))];
    default: return [d(0), d(0)];
  }
}
function dateRange(id, onChange) {
  const el = $('#' + id), sel = $('[data-r="preset"]', el), from = $('[data-r="from"]', el), to = $('[data-r="to"]', el);
  const key = 'lis.range.' + id;
  let saved = null;
  try { saved = JSON.parse(sessionStorage.getItem(key) || 'null'); } catch { /* bỏ qua */ }
  const apply = (p) => { if (p !== 'custom') [from.value, to.value] = presetRange(p); };
  sel.value = saved?.preset || 'today';
  if (sel.value === 'custom' && saved) { from.value = saved.from; to.value = saved.to; } else apply(sel.value);
  const changed = () => {
    if (from.value && to.value && from.value > to.value) to.value = from.value;
    try { sessionStorage.setItem(key, JSON.stringify({ preset: sel.value, from: from.value, to: to.value })); } catch { /* bỏ qua */ }
    onChange();
  };
  sel.onchange = () => { apply(sel.value); changed(); };
  from.onchange = to.onchange = () => { sel.value = 'custom'; changed(); };
  return {
    query: () => `from=${from.value}&to=${to.value}`,
    oneDay: () => from.value === to.value,
    // Khoảng "Hôm nay"/"7 ngày"… trượt theo ngày: qua nửa đêm vẫn đúng
    roll: () => { if (sel.value !== 'custom') { const [f, t] = presetRange(sel.value); if (f !== from.value || t !== to.value) { from.value = f; to.value = t; return true; } } return false; },
  };
}
let oRange, rRange, tRange;

// ============================================================ tab
let tab = 'orders';
function setTab(t) {
  tab = t;
  $$('#tabs button').forEach((b) => b.classList.toggle('on', b.dataset.tab === t));
  $$('.tab').forEach((s) => (s.hidden = s.id !== 'tab-' + t));
  ({ orders: loadOrders, results: loadResults, devices: loadDevices, his: loadHis, users: loadUsers, trace: loadTrace, logs: loadLogs })[t]();
}
$$('#tabs button').forEach((b) => (b.onclick = () => setTab(b.dataset.tab)));

let started = false;
function start() {
  if (started) return; started = true;
  oRange = dateRange('oRange', () => loadOrders());
  rRange = dateRange('rRange', () => loadResults());
  tRange = dateRange('tRange', () => loadTrace());
  api('devices').then((d) => { devCache = d.devices; refreshHealth(); }).catch(() => {});
  setTab('orders');
  setInterval(() => { oRange.roll(); rRange.roll(); refreshHealth(); if (tab === 'orders' && $('#oForm').hidden) loadOrders(true); if (tab === 'results') loadResults(true); if (tab === 'trace' && $('#tLive').checked) loadTrace(true); }, 5000);
}

async function refreshHealth() {
  try {
    const s = await api('stats?' + oRange.query());
    $('#health').innerHTML = (s.dbError ? `<span class="pill bad" title="${esc(s.dbError)}">Mất kết nối CSDL</span>` : '')
      + (s.spooled ? `<span class="pill bad" title="Kết quả máy gửi lúc mất CSDL, đang ghi tạm trên máy này">${s.spooled} kết quả ghi tạm</span>` : '')
      + (s.devices || []).map((d) => {
      const dev = devCache.find((x) => x.id === d.id);
      const name = dev ? dev.name : d.id;
      const waiting = !d.connected && /^đang chờ|đang tắt/.test(d.lastError || '');
      const cls = d.connected ? 'ok' : waiting ? 'off' : 'bad';
      return `<span class="pill ${cls}" title="${esc(d.lastError || 'Đã kết nối')}">${esc(name)}</span>`;
    }).join('') + (s.outbox ? `<span class="pill bad" title="Kết quả chờ gửi HIS">${s.outbox} chờ gửi HIS</span>` : '');
    const b = $('#unmatchedBadge'); b.hidden = !s.unmatched; b.textContent = s.unmatched;
    $('#outboxInfo').textContent = s.outbox ? `${s.outbox} kết quả đang chờ gửi HIS` : 'Không có kết quả chờ gửi HIS';
  } catch { /* bỏ qua */ }
}

// ============================================================ chỉ định
let orders = [], oStatus = '', selSid = null;
$$('#oStatus button').forEach((b) => (b.onclick = () => { oStatus = b.dataset.v; $$('#oStatus button').forEach((x) => x.classList.toggle('on', x === b)); renderOrders(); }));
$('#oQ').oninput = () => { clearTimeout(loadOrders.h); loadOrders.h = setTimeout(() => loadOrders(), 250); };

async function loadOrders(silent) {
  try {
    orders = await api(`orders?${oRange.query()}&q=${encodeURIComponent($('#oQ').value)}`);
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
      <td>${oRange.oneDay() ? time(o.createdAt) : dt(o.createdAt).slice(0, 11)}</td>
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
      <span>Tạo lúc</span><span>${dt(o.createdAt)}${o.createdBy ? ' · ' + esc(o.createdBy) : ''}</span>
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
    results = await api(`results?${rRange.query()}&unmatched=${rUnmatched}&sid=${encodeURIComponent($('#rSid').value)}`);
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
      <label class="small" style="flex-direction:row;align-items:center;gap:4px" title="Ghi nguyên văn byte trao đổi với máy để gỡ lỗi (tab Bản tin thô)"><input type="checkbox" data-trace ${d.noTrace ? '' : 'checked'} style="height:auto"> Ghi bản tin thô</label>
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
    $$('[data-trace]', card).forEach((el) => (el.onchange = () => { d.noTrace = !el.checked; }));
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
  const a = d.auth || {};
  $('#aMode').value = a.mode || 'local'; $('#aUrl').value = a.url || ''; $('#aKeyHeader').value = a.keyHeader || '';
  $('#aKey').value = a.key || ''; $('#aAdminRoles').value = a.adminRoles || ''; $('#aBody').value = a.body || '';
  $('#aNameField').value = a.nameField || ''; $('#aRolesField').value = a.rolesField || '';
  authDefaults = a; syncAuth();
  renderDoc(location.host);
}
let authDefaults = {};
const syncAuth = () => ($('#aBox').hidden = $('#aMode').value !== 'his');
$('#aMode').onchange = syncAuth;
const authBody = () => ({
  ...authDefaults, mode: $('#aMode').value, url: $('#aUrl').value.trim(), keyHeader: $('#aKeyHeader').value.trim(), key: $('#aKey').value.trim(),
  adminRoles: $('#aAdminRoles').value.trim(), body: $('#aBody').value.trim(), nameField: $('#aNameField').value.trim(), rolesField: $('#aRolesField').value.trim(),
  defaultRole: 'ktv',
});
$('#aTest').onclick = async () => {
  const m = $('#aTestMsg'); m.className = 'small muted'; m.textContent = 'Đang hỏi HIS…';
  try {
    const r = await api('auth/test', { method: 'POST', body: { auth: authBody(), username: $('#aTestUser').value, password: $('#aTestPw').value } });
    m.className = 'small ok';
    m.textContent = `Đúng: ${r.fullName || r.username} · vai trò HIS ${(r.roles || []).join(', ') || '—'} → ${ROLE[r.role] || r.role} trên LIS`;
  } catch (e) { m.className = 'small err'; m.textContent = e.message; }
  $('#aTestPw').value = '';
};
const hisBody = () => ({ name: $('#hName').value.trim(), apiKey: $('#hKey').value.trim(), webhookUrl: $('#hUrl').value.trim(), secret: $('#hSecret').value.trim() });
$('#hisForm').addEventListener('submit', async (e) => {
  e.preventDefault(); $('#hErr').textContent = '';
  try { await api('his', { method: 'PUT', body: { his: hisBody(), auth: authBody(), siteName: $('#hSite').value.trim() } }); toast('Đã lưu kết nối HIS/CIS'); $('#site').textContent = $('#hSite').value.trim(); } catch (err) { $('#hErr').textContent = err.message; }
});
$('#hTest').onclick = async () => {
  $('#hErr').textContent = '';
  try { await api('his/test', { method: 'POST', body: hisBody() }); toast('HIS đã nhận bản tin thử'); } catch (err) { $('#hErr').textContent = 'Không gửi được: ' + err.message; }
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

// ============================================================ người dùng
let users = [], editUser = null;
async function loadUsers() {
  users = await api('users');
  $('#uTable tbody').innerHTML = users.map((u) => `
    <tr data-u="${esc(u.username)}" class="${u.username === editUser ? 'sel' : ''}">
      <td><b>${esc(u.username)}</b></td><td>${esc(u.fullName)}</td>
      <td>${u.source === 'his' ? 'HIS/CIS' : 'LIS'}</td>
      <td><span class="tag ${u.role === 'quan-tri' ? 't-co-ket-qua' : 't-cho'}">${ROLE[u.role] || u.role}</span></td>
      <td>${u.active ? 'Hoạt động' : '<span class="tag t-bad">Đã khóa</span>'}</td>
      <td>${u.lastLogin ? dt(u.lastLogin) : '—'}</td>
    </tr>`).join('');
  $$('#uTable tbody tr').forEach((tr) => (tr.onclick = () => editUserForm(tr.dataset.u)));
}
function editUserForm(name) {
  const u = users.find((x) => x.username === name);
  editUser = u ? u.username : null;
  $$('#uTable tbody tr').forEach((tr) => tr.classList.toggle('sel', tr.dataset.u === editUser));
  $('#uErr').textContent = '';
  $('#uTitle').textContent = u ? (u.source === 'his' ? 'Tài khoản HIS/CIS' : 'Tài khoản LIS') : 'Tài khoản LIS mới';
  $('#uUser').value = u ? u.username : ''; $('#uUser').disabled = !!u;
  $('#uName').value = u ? u.fullName : ''; $('#uName').disabled = u && u.source === 'his';
  $('#uRole').value = u ? u.role : 'ktv';
  $('#uActive').checked = u ? u.active : true;
  $('#uPw').value = '';
  $('#uPwRow').hidden = u && u.source === 'his';
  $('#uPwHint').textContent = u ? '(trống = giữ mật khẩu cũ)' : '(≥ 8 ký tự)';
  (u ? $('#uRole') : $('#uUser')).focus();
}
$('#uNew').onclick = () => editUserForm(null);
$('#uReset').onclick = () => editUserForm(editUser);
$('#uForm').addEventListener('submit', async (e) => {
  e.preventDefault(); $('#uErr').textContent = '';
  const u = users.find((x) => x.username === editUser);
  try {
    if (u && u.source === 'his') await api('users/' + encodeURIComponent(u.username), { method: 'PUT', body: { role: $('#uRole').value, active: $('#uActive').checked } });
    else await api('users', { method: 'POST', body: { username: $('#uUser').value.trim(), fullName: $('#uName').value.trim(), role: $('#uRole').value, password: $('#uPw').value, active: $('#uActive').checked } });
    toast('Đã lưu tài khoản');
    editUser = u ? u.username : $('#uUser').value.trim();
    await loadUsers(); editUserForm(editUser);
  } catch (err) { $('#uErr').textContent = err.message; }
});

// ============================================================ bản tin thô máy ↔ Agent
let tSession = null, tFramesCache = [], tImports = [];
const tSrc = () => $('#tSource').value; // '' = Agent này, còn lại = id tệp đã nạp
function tQuery() {
  const imp = tSrc();
  return imp ? `import=${encodeURIComponent(imp)}` : `${tRange.query()}&device=${encodeURIComponent($('#tDevice').value)}`;
}
const ms = (iso) => { const d = new Date(iso); return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${String(d.getMilliseconds()).padStart(3, '0')}`; };
// Chữ đã thay ký tự điều khiển bằng <ENQ>…: tô thành nhãn nhỏ cho dễ nhìn
const ctlHtml = (text) => esc(text).replace(/&lt;([A-Z]{2,3}|x[0-9A-F]{2})&gt;/g, (_, n) => `<span class="ctl${n === 'NAK' ? ' bad' : ''}">${n}</span>`);
async function loadTrace(silent) {
  try {
    if (!silent) {
      tImports = await api('traces/imports').catch(() => []);
      const cur = tSrc();
      $('#tSource').innerHTML = `<option value="">Bản tin của Agent này (${esc(boot.info?.agent || '')})</option>`
        + tImports.map((i) => `<option value="${esc(i.id)}" ${i.id === cur ? 'selected' : ''}>Tệp nạp: ${esc(i.name)} — ${i.frames} khối</option>`).join('');
      const dsel = $('#tDevice').value;
      $('#tDevice').innerHTML = '<option value="">Tất cả máy</option>' + devCache.map((d) => `<option value="${esc(d.id)}" ${d.id === dsel ? 'selected' : ''}>${esc(d.name)}</option>`).join('');
      const st = await api('traces/status').catch(() => null);
      $('#tFoot').textContent = st ? `Giữ bản tin thô ${st.days} ngày (tự xóa cũ hơn).${st.dropped ? ` ${st.dropped} khối bị bỏ do CSDL chậm.` : ''} Tệp .listrace nạp được vào Agent khác, hoặc phát lại tới một Agent: lis-sim -addr <IP>:<cổng> -replay <tệp>.` : '';
    }
    const imp = tSrc();
    $('#tRange').hidden = !!imp; $('#tDevice').hidden = !!imp;
    $('#tDelImport').hidden = !imp || !isAdmin();
    const sessions = await api('traces/sessions?' + tQuery());
    $('#tSessions tbody').innerHTML = sessions.map((x) => `
      <tr data-s="${esc(x.session)}" class="${x.session === tSession ? 'sel' : ''}">
        <td>${dt(x.start)}<div class="small muted">${x.end !== x.start ? 'đến ' + time(x.end) : ''}</div></td>
        <td>${esc(x.deviceName || x.deviceId)}<div class="small muted">${esc((x.protocol || '').toUpperCase())}</div></td>
        <td>${x.frames}</td><td class="small">${x.rxBytes} B / ${x.txBytes} B</td>
      </tr>`).join('');
    $('#tSessEmpty').hidden = sessions.length > 0;
    $$('#tSessions tbody tr').forEach((tr) => (tr.onclick = () => openSession(tr.dataset.s)));
    if (tSession && sessions.some((x) => x.session === tSession)) await openSession(tSession, true);
  } catch (e) { if (!silent) toast(e.message, true); }
}
async function openSession(s, silent) {
  tSession = s;
  $$('#tSessions tbody tr').forEach((tr) => tr.classList.toggle('sel', tr.dataset.s === s));
  const last = silent && tFramesCache.length ? tFramesCache[tFramesCache.length - 1].id : 0;
  const imp = tSrc();
  const more = await api(`traces?session=${encodeURIComponent(s)}${imp ? '&import=' + encodeURIComponent(imp) : ''}&limit=20000${last ? '&after=' + last : ''}`);
  if (!silent || !last) { tFramesCache = more; $('#tDecoded').hidden = true; } else tFramesCache = tFramesCache.concat(more);
  const f0 = tFramesCache[0];
  $('#tTitle').textContent = f0 ? `${f0.deviceName || f0.deviceId} — ${dt(f0.at)} — ${tFramesCache.length} khối` : 'Không có khối nào';
  $('#tDecode').disabled = !f0;
  renderFrames();
}
function renderFrames() {
  const hex = $('#tHex').checked, noAck = $('#tNoAck').checked;
  const single = (t) => /^<(ACK|ENQ|EOT|NAK)>$/.test(t);
  $('#tFrames').innerHTML = tFramesCache.filter((f) => !(noAck && f.dir !== 'ev' && single(f.text))).map((f) => `
    <div class="fr ${f.dir}"><span class="t">${ms(f.at)}</span>
      <span class="d">${f.dir === 'rx' ? 'MÁY → AGENT' : f.dir === 'tx' ? 'AGENT → MÁY' : 'SỰ KIỆN'}</span>
      <span><span class="c">${f.dir === 'ev' ? esc(f.text) : ctlHtml(f.text)}</span>${f.note ? `<div class="note small">${esc(f.note)}</div>` : ''}${hex && f.dir !== 'ev' ? `<div class="h">${esc(f.hex)}</div>` : ''}</span>
    </div>`).join('') || '<div class="empty">Không có khối nào.</div>';
}
$('#tHex').onchange = renderFrames;
$('#tDelImport').onclick = async () => {
  if (!confirm('Xóa tệp bản tin đã nạp này? Bản tin của chính Agent không bị ảnh hưởng.')) return;
  await api('traces/imports/' + encodeURIComponent(tSrc()), { method: 'DELETE' }).catch((e) => toast(e.message, true));
  $('#tSource').value = ''; tSession = null; loadTrace();
};
$('#tDays').onclick = async () => {
  const v = prompt('Giữ bản tin thô bao nhiêu ngày (1–365)?', '30');
  if (!v) return;
  try { await api('traces/settings', { method: 'PUT', body: { days: +v } }); toast('Đã lưu'); loadTrace(); } catch (e) { toast(e.message, true); }
};
$('#tNoAck').onchange = renderFrames;
$('#tSource').onchange = () => { tSession = null; tFramesCache = []; $('#tFrames').innerHTML = ''; $('#tDecoded').hidden = true; loadTrace(); };
$('#tDevice').onchange = () => loadTrace();
const download = (fmt) => {
  const q = tSession ? `session=${encodeURIComponent(tSession)}${tSrc() ? '&import=' + encodeURIComponent(tSrc()) : ''}` : tQuery();
  location.href = `/ui/api/traces/export?format=${fmt}&${q}`;
};
$('#tExport').onclick = () => download('listrace');
$('#tExportTxt').onclick = () => download('txt');
$('#tImport').onchange = async (e) => {
  const file = e.target.files[0]; e.target.value = '';
  if (!file) return;
  try {
    const res = await fetch('/ui/api/traces/import?name=' + encodeURIComponent(file.name), { method: 'POST', credentials: 'same-origin', body: file });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(data.message || 'Lỗi ' + res.status);
    toast(`Đã nạp ${data.frames} khối từ Agent ${data.fromAgent}`);
    tSession = null; await loadTrace();
    $('#tSource').value = data.id; loadTrace();
  } catch (err) { toast(err.message, true); }
};
const KIND = { 'ket-qua': 'Kết quả', 'hoi-chi-dinh': 'Máy hỏi chỉ định', khac: 'Bản tin khác', loi: 'Lỗi khung' };
$('#tDecode').onclick = async () => {
  try {
    const msgs = await api('traces/decode', { method: 'POST', body: { session: tSession, importId: tSrc() } });
    $('#tDecoded').hidden = false;
    $('#tDecoded').className = 'decoded';
    $('#tDecoded').innerHTML = `<div class="small muted">Giải mã thử bằng bộ đọc của Agent này — <b>không lưu, không gửi HIS</b>. ${msgs.length} bản tin.</div>`
      + (msgs.map((m) => `<div class="dec">
          <div><span class="tag ${m.kind === 'loi' ? 't-bad' : m.kind === 'ket-qua' ? 't-da-gui' : 't-cho'}">${KIND[m.kind] || m.kind}</span> <span class="small muted">${ms(m.at)}</span>
            ${m.queries && m.queries.length ? ` SID hỏi: <b>${m.queries.map(esc).join(', ')}</b>` : ''}${m.error ? ` <span class="err">${esc(m.error)}</span>` : ''}</div>
          ${(m.results || []).map((r) => `<table class="grid items"><thead><tr><th>SID ${esc(r.sid)}</th><th>Giá trị</th><th>Đơn vị</th><th>Cờ</th><th>Tham chiếu</th></tr></thead><tbody>
            ${r.items.map((i) => `<tr><td>${esc(i.deviceCode)}${i.testName ? ' · ' + esc(i.testName) : ''}</td><td class="flag-${esc(i.flag)}">${esc(i.value)}</td><td>${esc(i.unit)}</td><td>${esc(i.flag)}</td><td>${esc(i.refRange)}</td></tr>`).join('')}</tbody></table>`).join('')}
          ${m.lines && m.lines.length ? `<details><summary class="small muted">Bản ghi (${m.lines.length})</summary><pre>${esc(m.lines.join('\n'))}</pre></details>` : ''}
          ${m.raw ? `<pre>${ctlHtml(m.raw)}</pre>` : ''}
        </div>`).join('') || '<div class="empty">Không giải mã được bản tin nào từ các khối máy gửi.</div>');
    $('#tDecoded').scrollIntoView({ behavior: 'smooth', block: 'start' });
  } catch (e) { toast(e.message, true); }
};

// ============================================================ nhật ký
async function loadLogs() {
  const logs = await api('logs?source=' + encodeURIComponent($('#lSource').value));
  const srcs = new Set(['his', 'ui', 'dang-nhap', 'agent', ...devCache.map((d) => 'may:' + d.id)]);
  const cur = $('#lSource').value;
  $('#lSource').innerHTML = '<option value="">Tất cả nguồn</option>' + [...srcs].map((s) => {
    const d = devCache.find((x) => 'may:' + x.id === s);
    return `<option value="${esc(s)}" ${s === cur ? 'selected' : ''}>${esc(d ? 'Máy ' + d.name : s === 'his' ? 'HIS/CIS' : s === 'ui' ? 'Giao diện' : s === 'dang-nhap' ? 'Đăng nhập' : s === 'agent' ? 'Agent' : s)}</option>`;
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
    else if (e.key === 'Escape' && !$('#pwModal').hidden) $('#pwModal').hidden = true;
    else if (e.key === 'Escape' && !$('#oForm').hidden) closeForm();
    else if (e.key === '/' && !/INPUT|SELECT|TEXTAREA/.test(document.activeElement.tagName)) { e.preventDefault(); setTab('orders'); $('#oQ').focus(); }
  }
});

boot();
