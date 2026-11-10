(() => {
'use strict';
const $ = (id) => document.getElementById(id);
const state = { settings: null, doc: null, page: 0, rev: 0, jobs: [], remote: null, printers: null };

// ---------- helpers ----------
async function api(path, opts = {}) {
  const o = { method: 'GET', headers: { 'X-ZLP': '1' }, ...opts };
  o.headers = { 'X-ZLP': '1', ...(opts.headers || {}) };
  if (o.json !== undefined) { o.body = JSON.stringify(o.json); o.headers['Content-Type'] = 'application/json'; delete o.json; }
  const r = await fetch('/api' + path, o);
  const ct = r.headers.get('content-type') || '';
  const data = ct.includes('json') ? await r.json() : await r.text();
  if (!r.ok) throw new Error((data && data.error) || ('Request failed (' + r.status + ')'));
  return data;
}
function h(tag, attrs, ...kids) {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (k === 'class') e.className = v; else if (k.startsWith('on')) e.addEventListener(k.slice(2), v); else if (v !== false && v != null) e.setAttribute(k, v === true ? '' : v);
  }
  for (const c of kids.flat()) if (c != null) e.append(c.nodeType ? c : document.createTextNode(String(c)));
  return e;
}
let toastT;
function toast(msg, bad) { const t = $('toast'); t.textContent = msg; t.className = 'toast' + (bad ? ' bad' : ''); t.hidden = false; clearTimeout(toastT); toastT = setTimeout(() => t.hidden = true, bad ? 7000 : 3500); }
const guard = (fn) => async (...a) => { try { return await fn(...a); } catch (e) { toast(e.message, true); } };
const fmtTime = (s) => new Date(s).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });

// ---------- tabs ----------
document.querySelectorAll('#tabs button').forEach(b => b.onclick = () => showTab(b.dataset.tab));
function showTab(t) {
  document.querySelectorAll('#tabs button').forEach(b => b.classList.toggle('on', b.dataset.tab === t));
  document.querySelectorAll('.tab').forEach(s => s.classList.toggle('on', s.id === 'tab-' + t));
  if (t === 'diag') loadDiag(false);
  if (t === 'settings') loadPrinters();
}

// ---------- settings form ----------
const F = {
  s_lw: ['labelWidthIn', 'n'], s_lh: ['labelHeightIn', 'n'], s_media: ['media', 's'], s_gap: ['gapDots', 'n'], s_pw: ['printableWidthDots', 'n'],
  s_smode: ['scaleMode', 's'], s_scale: ['scalePercent', 'n'], s_rot: ['rotationDegrees', 'n'], s_anchor: ['anchor', 's'], s_ox: ['offsetXDots', 'n'], s_oy: ['offsetYDots', 'n'],
  s_auto: ['autoRotate', 'b'], s_crop: ['autoCrop', 'b'], s_watch: ['watchEnabled', 'b'], s_updates: ['checkForUpdates', 'b'], s_watchdir: ['watchFolder', 's'], s_watchlabels: ['watchOnlyLabels', 'b'], s_den: ['density', 'n'], s_spd: ['speed', 'n'], s_thr: ['threshold', 'n'], s_ss: ['supersample', 'n'], s_dir: ['printDirectionRotated', 'bool-s'], s_inv: ['invertImage', 'b'], s_gw: ['gwZeroIsBlack', 'b'],
  s_ret: ['uploadRetentionHours', 'n'], s_startup: ['startWithWindows', 'b'], s_backend: ['backend', 's'], s_usb: ['directDevicePath', 's'], s_file: ['saveToFilePath', 's'],
  r_enable: ['remoteEnabled', 'b'], r_port: ['remotePort', 'n'], r_https: ['remoteHttps', 'bool-s'], r_approve: ['requireApproval', 'b'],
};
function fillForm() {
  const s = state.settings;
  for (const [id, [key, t]] of Object.entries(F)) {
    const el = $(id); if (!el) continue;
    if (t === 'b') el.checked = !!s[key]; else el.value = String(s[key]);
  }
  $('autoRot').checked = s.autoRotate;
  $('thrVal').textContent = s.threshold;
  $('f_file').hidden = s.backend !== 'SaveToFile';
  $('f_usb').hidden = s.backend !== 'DirectUsb';
  $('f_queue').hidden = !['RawEpl2', 'WindowsDriverGdi'].includes(s.backend);
  $('backendHelp').textContent = ({
    DirectUsb: 'The app is the driver: it renders the PDF to a 1-bit 203 dpi bitmap and sends EPL2 straight to the printer through the USB adapter. No Windows printer setup needed, and any USB socket works.',
    RawEpl2: 'Renders the PDF to a 1-bit bitmap at 203 dpi and sends it as EPL2 straight through the Windows queue. The driver cannot resize or dither it.',
    WindowsDriverGdi: 'Sends the same 1-bit bitmap through the printer driver. Use only if RAW fails; the driver must be set to 4×6 in, 203 dpi.',
    SaveToFile: 'Writes the generated EPL2 to a file instead of printing. Handy for inspection.',
  })[s.backend];
  updateChip();
}
function readForm() {
  const s = { ...state.settings };
  for (const [id, [key, t]] of Object.entries(F)) {
    const el = $(id); if (!el) continue;
    if (t === 'b') s[key] = el.checked; else if (t === 'n') s[key] = Number(el.value); else if (t === 'bool-s') s[key] = el.value === 'true'; else s[key] = el.value;
  }
  s.printerName = $('s_printer').value || state.settings.printerName;
  return s;
}
let saveT;
function scheduleSave() {
  clearTimeout(saveT);
  saveT = setTimeout(guard(async () => {
    const r = await api('/settings', { method: 'PUT', json: readForm() });
    const cropChanged = state.settings.autoCrop !== r.settings.autoCrop;
    state.settings = r.settings; fillForm();
    if (cropChanged && state.doc) { state.doc = await api('/docs/' + state.doc.id); state.page = 0; $('pager').hidden = state.doc.pageCount < 2; }
    bumpPreview();
  }), 350);
}
Object.keys(F).forEach(id => { const el = $(id); if (el) { el.addEventListener('input', scheduleSave); el.addEventListener('change', scheduleSave); } });
$('s_printer').addEventListener('change', scheduleSave);
$('autoRot').onchange = () => { $('s_auto').checked = $('autoRot').checked; scheduleSave(); };
$('s_auto').addEventListener('change', () => { $('autoRot').checked = $('s_auto').checked; });
$('rot90').onclick = () => { $('s_rot').value = String((Number($('s_rot').value) + 90) % 360); scheduleSave(); };
document.querySelectorAll('.nudge [data-n]').forEach(b => b.onclick = () => {
  const [dx, dy] = b.dataset.n.split(',').map(Number), step = Number($('nudgeStep').value);
  $('s_ox').value = Number($('s_ox').value) + dx * step; $('s_oy').value = Number($('s_oy').value) + dy * step; scheduleSave();
});
$('resetCal').onclick = guard(async () => {
  if (!confirm('Restore all placement and quality settings to defaults? (Your printer choice is kept.)')) return;
  const r = await api('/settings/reset-calibration', { method: 'POST' }); state.settings = r.settings; fillForm(); bumpPreview(); toast('Defaults restored');
});
function updateChip() {
  const c = $('printerChip'), s = state.settings;
  const usbName = (state.printers && state.printers.usb || []).find(u => u.path === s.directDevicePath || (!s.directDevicePath && u.looksLikeZebra)) || (state.printers && state.printers.usb && state.printers.usb.length === 1 ? state.printers.usb[0] : null);
  const n = s.backend === 'SaveToFile' ? 'Save to file' : s.backend === 'DirectUsb' ? (usbName ? 'USB: ' + usbName.name : (state.printers ? 'No USB printer found' : 'USB printer')) : s.printerName;
  c.textContent = n || 'No printer selected'; c.className = 'chip ' + (n ? '' : 'bad');
}

async function loadPrinters() {
  const r = await api('/printers'); state.printers = r;
  const us = $('s_usb');
  us.replaceChildren(h('option', { value: '' }, r.usb.length ? 'Automatic (' + ((r.usb.find(u => u.looksLikeZebra) || r.usb[0]).name) + ')' : 'Automatic (no USB printer found yet)'));
  for (const u of r.usb) us.append(h('option', { value: u.path }, u.name + (u.deviceId1284 ? '  [' + u.deviceId1284.slice(0, 60) + ']' : '')));
  us.value = state.settings.directDevicePath || '';
  updateChip();
  const sel = $('s_printer'); sel.replaceChildren(h('option', { value: '' }, r.printers.length ? '— choose —' : '— no printers found —'));
  for (const p of r.printers) sel.append(h('option', { value: p.name }, `${p.name}  [${p.port}]${p.offline ? ' (offline)' : ''}`));
  const cur = state.settings.printerName;
  if (cur && !r.printers.some(p => p.name === cur)) sel.append(h('option', { value: cur }, cur + ' (not found)'));
  sel.value = cur || '';
  if (!cur && state.settings.backend !== 'DirectUsb') { // pre-select an obvious Zebra queue
    const z = r.printers.find(p => /zebra|zdesigner|eltron|lp ?2443|tlp|2443/i.test(p.name + p.driver));
    if (z) { sel.value = z.name; scheduleSave(); }
  }
}
$('refreshPrinters').onclick = guard(loadPrinters);

// ---------- document / preview ----------
async function uploadFile(file) {
  if (!file) return;
  if (!/\.pdf$/i.test(file.name) && file.type !== 'application/pdf') return toast('Please choose a PDF file.', true);
  const doc = await api('/docs', { method: 'POST', body: file, headers: { 'X-File-Name': encodeURIComponent(file.name), 'Content-Type': 'application/pdf' } });
  state.page = 0; setDoc(doc);
}
function setDoc(doc) {
  state.doc = doc; state.rev++; state.page = Math.max(0, Math.min(state.page || 0, doc.pageCount - 1));
  $('cropBtn').disabled = false;
  $('dropEmpty').hidden = true; $('stage').hidden = false; $('closeDoc').hidden = false;
  $('docName').textContent = doc.name;
  $('docMeta').textContent = doc.manualCrop ? `Printing ${doc.pageCount} area${doc.pageCount > 1 ? 's' : ''} you cropped by hand. (Crop… → Back to automatic to undo.)` : doc.autoCropped ? `Found ${doc.pageCount} label${doc.pageCount > 1 ? 's' : ''} on ${doc.pdfPageCount} page${doc.pdfPageCount > 1 ? 's' : ''} and cropped ${doc.pageCount > 1 ? 'them' : 'it'} out.` : `${doc.pageCount} page${doc.pageCount > 1 ? 's' : ''}. Each page prints as its own label.`;
  $('pager').hidden = doc.pageCount < 2; $('printBtn').disabled = false;
  showPage();
}
const uploadSafe = guard(uploadFile);
function bumpPreview() { state.rev++; if (state.doc) showPage(); }
const showPage = guard(async () => {
  const d = state.doc, p = state.page;
  $('pgInfo').textContent = `Label ${p + 1} of ${d.pageCount}`;
  const info = await api(`/docs/${d.id}/info/${p}`);
  $('preview').src = `/api/docs/${d.id}/preview/${p}.png?r=${state.rev}`;
  const wrap = $('printableMask');
  const pr = Math.min(1, info.printableWidth / info.width);
  wrap.style.right = ((1 - pr) * 100) + '%';
  $('previewInfo').textContent = info.summary;
  const w = $('warnings'); w.replaceChildren(...info.warnings.map(t => h('li', { class: /CROPPED/.test(t) ? 'bad' : '' }, t)));
  const bc = info.barcodes, el = $('bcCheck');
  el.hidden = !bc || !bc.available;
  if (bc && bc.available) {
    el.className = 'bccheck ' + (!bc.ok ? 'bad' : bc.found ? 'ok' : 'none');
    el.textContent = (!bc.ok ? '✕ ' : bc.found ? '✓ ' : '') + bc.summary + (bc.missing.length ? '  Missing: ' + bc.missing.map(m => m.split(':').slice(1).join(':')).join(', ') : '');
  }
});
$('prevPg').onclick = () => { if (state.page > 0) { state.page--; showPage(); } };
$('nextPg').onclick = () => { if (state.doc && state.page < state.doc.pageCount - 1) { state.page++; showPage(); } };
$('openBtn').onclick = () => $('file').click();
$('file').onchange = (e) => { uploadSafe(e.target.files[0]); e.target.value = ''; };
$('closeDoc').onclick = () => { state.doc = null; $('cropBtn').disabled = true; $('dropEmpty').hidden = false; $('stage').hidden = true; $('closeDoc').hidden = true; $('pager').hidden = true; $('printBtn').disabled = true; $('docName').textContent = 'No PDF loaded'; $('docMeta').textContent = 'Each PDF page prints as its own label.'; $('warnings').replaceChildren(); $('previewInfo').textContent = ''; };
const drop = $('drop');
['dragenter', 'dragover'].forEach(ev => window.addEventListener(ev, e => { e.preventDefault(); drop.classList.add('over'); }));
['dragleave', 'drop'].forEach(ev => window.addEventListener(ev, e => { e.preventDefault(); if (ev === 'drop' || e.target === document.documentElement) drop.classList.remove('over'); }));
window.addEventListener('drop', e => { showTab('print'); const f = e.dataTransfer.files[0]; uploadSafe(f); });
drop.addEventListener('keydown', e => { if (e.key === 'Enter') $('file').click(); });

// copies
const clampCopies = () => { let v = parseInt($('copies').value || '1', 10); v = Math.min(99, Math.max(1, isNaN(v) ? 1 : v)); $('copies').value = v; return v; };
$('cMinus').onclick = () => { $('copies').value = clampCopies() - 1; clampCopies(); };
$('cPlus').onclick = () => { $('copies').value = clampCopies() + 1; clampCopies(); };
$('copies').onchange = clampCopies;

// print
$('printBtn').onclick = guard(async () => {
  if (!state.doc) return;
  if (!state.settings.printerName && ['RawEpl2', 'WindowsDriverGdi'].includes(state.settings.backend)) { showTab('settings'); return toast('Choose your Zebra printer first.', true); }
  if ($('safeBanner').hidden === false) return toast('Paper-safe mode: run the raw text test first (blue bar at the top) and confirm it printed correctly.', true);
  const pages = $('allPages').checked || state.doc.pageCount < 2 ? null : [state.page];
  const total = (pages ? 1 : state.doc.pageCount) * clampCopies();
  if (total > 5 && !confirm(`This will print ${total} labels. Continue?`)) return;
  const r = await api('/print', { method: 'POST', json: { docId: state.doc.id, pages, copies: clampCopies() } });
  toast(`Sent ${r.jobs.length} label${r.jobs.length > 1 ? 's' : ''} to the queue`);
  poll();
});
$('testBtn').onclick = $('testBtn2').onclick = guard(async () => { await api('/test/label', { method: 'POST' }); toast('Test label queued'); poll(); });
$('reprintBtn').onclick = guard(async () => { await api('/reprint-last', { method: 'POST' }); toast('Reprinting the last label'); poll(); });

// ---------- jobs ----------
function jobRow(j, full) {
  const actions = [];
  if (j.status === 'AwaitingApproval') { actions.push(h('button', { class: 'small primary', onclick: guard(() => act(j.id, 'approve')) }, 'Approve'), h('button', { class: 'small', onclick: guard(() => act(j.id, 'reject')) }, 'Reject')); }
  if (['Queued', 'AwaitingApproval', 'Spooled'].includes(j.status)) actions.push(h('button', { class: 'small', onclick: guard(() => act(j.id, 'cancel')) }, 'Cancel'));
  if (j.status !== 'AwaitingApproval' && ['Completed', 'Failed', 'Spooled', 'Cancelled'].includes(j.status)) actions.push(h('button', { class: 'small', onclick: guard(() => act(j.id, 'reprint')) }, 'Reprint'));
  const label = j.kind === 'pdf' ? `${j.docName} · p${j.page + 1}${j.pageCount > 1 ? '/' + j.pageCount : ''}${j.copies > 1 ? ' ×' + j.copies : ''}` : j.docName;
  return h('div', { class: 'job' },
    h('span', { class: 'st ' + j.status }, j.status === 'AwaitingApproval' ? 'Needs approval' : j.status === 'Spooled' ? 'In queue' : j.status),
    h('div', { class: 'nm' }, h('div', {}, label), h('div', { class: 'muted small', title: j.message }, (full ? fmtTime(j.createdUtc) + ' · ' + j.source + ' · ' : '') + j.message)),
    ...actions);
}
async function act(id, what) {
  const r = await api(`/jobs/${id}/${what}`, { method: 'POST' });
  if (what === 'cancel') toast(r.message, !r.ok);
  poll();
}
function renderJobs() {
  $('recent').replaceChildren(...(state.jobs.slice(0, 5).map(j => jobRow(j, false))), ...(state.jobs.length ? [] : [h('div', { class: 'muted small' }, 'Nothing printed yet.')]));
  $('histList').replaceChildren(...(state.jobs.length ? state.jobs.map(j => jobRow(j, true)) : [h('div', { class: 'muted' }, 'No print history yet.')]));
  const pend = state.jobs.filter(j => j.status === 'AwaitingApproval');
  const b = $('approvals'); b.hidden = pend.length === 0;
  b.replaceChildren(...pend.map(j => h('div', { class: 'item' }, h('b', {}, j.source), ` wants to print “${j.docName}” page ${j.page + 1}${j.copies > 1 ? ' ×' + j.copies : ''}`, h('span', { class: 'grow' }), h('button', { class: 'small primary', onclick: guard(() => act(j.id, 'approve')) }, 'Approve'), h('button', { class: 'small', onclick: guard(() => act(j.id, 'reject')) }, 'Reject'))));
  const badge = $('badge'); badge.hidden = pend.length === 0; badge.textContent = pend.length;
}
$('clearHist').onclick = guard(async () => { await api('/jobs/clear', { method: 'POST' }); poll(); });

// ---------- remote ----------
function renderRemote() {
  const r = state.remote; if (!r) return;
  $('r_status').textContent = r.running ? `Listening on ${r.urls.length ? r.urls.join('  ') : 'port ' + r.port}` : (r.error ? 'Could not start: ' + r.error + ' (port in use? try another)' : 'Off');
  const dl = $('devList');
  dl.replaceChildren(...(r.devices.length ? r.devices.map(d => h('div', { class: 'job' }, h('div', { class: 'nm' }, h('div', {}, d.name), h('div', { class: 'muted small' }, 'last seen ' + fmtTime(d.lastSeenUtc) + ' · ' + d.lastIp)), h('button', { class: 'small danger', onclick: guard(async () => { if (confirm('Remove ' + d.name + '? It will no longer be able to print.')) { await api('/remote/devices/' + d.id, { method: 'DELETE' }); poll(); } }) }, 'Revoke'))) : ['None yet.']));
}
let pairTimer;
$('pairBtn').onclick = guard(async () => {
  if (!state.settings.remoteEnabled) return toast('Turn on phone printing first.', true);
  if (!state.remote.running) return toast('Phone printing is not running: ' + (state.remote.error || 'check the port'), true);
  const r = await api('/remote/pairing', { method: 'POST' });
  if (!r.urls.length) return toast('No local network address found. Connect this PC to Wi-Fi/Ethernet.', true);
  const sel = $('pairUrl'); sel.replaceChildren(...r.urls.map(u => h('option', { value: u }, u.replace(/#pair=.*/, ''))));
  const draw = () => { const q = qrcode(0, 'M'); q.addData(sel.value); q.make(); $('qr').innerHTML = q.createSvgTag({ cellSize: 6, margin: 0, scalable: true }); };
  sel.onchange = draw; draw();
  $('pairCode').textContent = r.code; $('pairOut').hidden = false;
  const exp = new Date(r.expiresUtc).getTime();
  clearInterval(pairTimer);
  pairTimer = setInterval(() => { const s = Math.max(0, Math.round((exp - Date.now()) / 1000)); $('pairTimer').textContent = Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0'); if (s === 0) { clearInterval(pairTimer); $('pairOut').hidden = true; } }, 500);
});
$('r_enable').addEventListener('change', () => setTimeout(poll, 800));

// ---------- diagnostics ----------
async function loadDiag(pnp) {
  const d = await api('/diagnostics' + (pnp ? '?pnp=true' : ''));
  const p = d.printer;
  const card = (k, v) => h('div', { class: 'card' }, h('div', { class: 'k' }, k), h('div', { class: 'v' }, v || '–'));
  if (d.backend === 'DirectUsb') {
    const u = d.usbPrinters[0];
    $('dCards').replaceChildren(card('Print method', 'Direct USB (built-in driver)'), card('USB printer devices', String(d.usbPrinters.length)), card('Device', u ? u.name : 'none found'), card('Printer ID (IEEE-1284)', u ? (u.deviceId1284 || 'not reported') : '–'));
  } else $('dCards').replaceChildren(card('Selected printer', d.selectedPrinter || '(none)'), card('Windows driver', p?.driver), card('Windows port', p?.port), card('Queue status', p ? p.statusText : (d.windowsSpooler ? 'not found' : 'n/a (not Windows)')), card('Jobs waiting', p ? String(d.queueJobs.length) : '–'), card('Print method', d.backend));
  $('dFindings').replaceChildren(...d.findings.map(f => h('li', { class: f.level }, f.text)));
  $('dQueue').replaceChildren(...(d.queueJobs.length ? d.queueJobs.map(j => h('div', { class: 'job' }, h('div', { class: 'nm' }, h('div', {}, '#' + j.id + ' ' + j.document), h('div', { class: 'muted small' }, j.statusText + ' · ' + j.user)), h('button', { class: 'small', onclick: guard(async () => { await api('/spool/' + j.id + '/cancel', { method: 'POST' }); loadDiag(false); }) }, 'Remove'))) : ['Empty.']));
  $('dPrinters').replaceChildren(...(d.allPrinters.length ? [h('table', {}, ...d.allPrinters.map(x => h('tr', {}, h('td', {}, x.name), h('td', {}, x.driver), h('td', {}, x.port), h('td', {}, x.statusText))))] : ['None.']));
  $('dPortsList').replaceChildren(...(d.usbAndParallelPorts.length ? [h('table', {}, ...d.usbAndParallelPorts.map(x => h('tr', {}, h('td', {}, x.name), h('td', {}, x.description))))] : ['No USB/LPT ports. If the adapter is plugged in, Windows has not created a port for it. See the troubleshooting checklist.']));
  const ps = $('dPorts'); ps.replaceChildren(...d.usbAndParallelPorts.map(x => h('option', { value: x.name }, x.name)));
  if (pnp) $('dPnpOut').textContent = d.pnpDevices.length ? d.pnpDevices.join('\n') : 'No matching devices reported.';
  state.diag = d;
}
$('dRefresh').onclick = guard(() => loadDiag(false));
$('dPnp').onclick = guard(async () => { $('dPnpOut').textContent = 'Scanning…'; await loadDiag(true); });
$('dCopy').onclick = guard(async () => { const t = await api('/diagnostics/report'); await navigator.clipboard.writeText(t); toast('Diagnostics report copied. Paste it wherever you need help.'); });
$('dHello').onclick = guard(async () => { await api('/test/hello', { method: 'POST' }); toast('Raw text test sent'); poll(); });
$('dLabel').onclick = guard(async () => { await api('/test/label', { method: 'POST' }); toast('Test label queued'); poll(); });
$('dMove').onclick = guard(async () => {
  const port = $('dPorts').value; if (!port) return toast('No port selected.', true);
  if (!confirm(`Point queue "${state.settings.printerName}" at ${port}?`)) return;
  const r = await api('/diagnostics/move-port', { method: 'POST', json: { printer: state.settings.printerName, port } }); toast(r.message);
});


// ---------- manual crop (mouse + touch) ----------
function makeCropper(o) {
  const $c = (id) => document.getElementById(id);
  let doc = null, page = 0, start = null, rect = null, url = null;
  const st = $c('cropStage'), img = $c('cropImg'), box = $c('cropRect');
  function pageSize() { return doc.pageSizesIn[page]; }
  async function load() {
    $c('cropPg').textContent = `Page ${page + 1} of ${doc.pdfPageCount || doc.pageSizesIn.length}`;
    $c('cropPrev').disabled = page === 0; $c('cropNext').disabled = page >= doc.pageSizesIn.length - 1;
    rect = null; box.hidden = true; updateSize();
    const u = await o.getImg(doc.id, page); if (url && url.startsWith('blob:')) URL.revokeObjectURL(url); url = u; img.src = u;
  }
  function pos(e) { const r = img.getBoundingClientRect(); return { x: Math.min(Math.max(e.clientX - r.left, 0), r.width), y: Math.min(Math.max(e.clientY - r.top, 0), r.height), w: r.width, h: r.height }; }
  st.addEventListener('pointerdown', (e) => { if (!img.complete) return; e.preventDefault(); st.setPointerCapture(e.pointerId); start = pos(e); rect = { x: start.x, y: start.y, w: 0, h: 0 }; draw(); });
  st.addEventListener('pointermove', (e) => { if (!start) return; const p = pos(e); rect = { x: Math.min(start.x, p.x), y: Math.min(start.y, p.y), w: Math.abs(p.x - start.x), h: Math.abs(p.y - start.y) }; draw(); });
  const end = () => { start = null; };
  st.addEventListener('pointerup', end); st.addEventListener('pointercancel', end);
  function draw() { box.hidden = !rect || rect.w < 2; if (!rect) return; box.style.left = rect.x + 'px'; box.style.top = rect.y + 'px'; box.style.width = rect.w + 'px'; box.style.height = rect.h + 'px'; updateSize(); }
  function inches() {
    if (!rect || rect.w < 4 || rect.h < 4) return null;
    const r = img.getBoundingClientRect(), [pw, ph] = pageSize();
    return { page, x: rect.x / r.width * pw, y: rect.y / r.height * ph, w: rect.w / r.width * pw, h: rect.h / r.height * ph };
  }
  function updateSize() { const i = rect && inches(); $c('cropSize').textContent = i ? `Selected ${i.w.toFixed(2)} × ${i.h.toFixed(2)} in. It will be rotated/scaled to fill the 4×6 label.` : 'Drag on the page. The area is scaled to fill the 4×6 label.'; }
  async function send(mode) {
    const i = inches();
    if (mode !== 'reset' && !i) return o.toast('Drag a box on the page first.', true);
    try { const d = await o.api(`/docs/${doc.id}/crop`, { method: 'POST', json: { ...(i || { page: 0, x: 0, y: 0, w: 0, h: 0 }), mode } }); close(); o.onDone(d, mode); }
    catch (e) { o.toast(e.message, true); }
  }
  function close() { $c('cropModal').hidden = true; }
  $c('cropUse').onclick = () => send('replace'); $c('cropAdd').onclick = () => send('add'); $c('cropReset').onclick = () => send('reset');
  $c('cropCancel').onclick = close;
  $c('cropModal').addEventListener('click', (e) => { if (e.target.id === 'cropModal') close(); });
  $c('cropPrev').onclick = () => { if (page > 0) { page--; load(); } }; $c('cropNext').onclick = () => { if (page < doc.pageSizesIn.length - 1) { page++; load(); } };
  return { open(d, startPage) { doc = d; page = Math.min(startPage || 0, d.pageSizesIn.length - 1); $c('cropModal').hidden = false; load(); } };
}

const cropper = makeCropper({
  api, toast,
  getImg: async (id, p) => `/api/docs/${id}/page/${p}.png`,
  onDone: (d, mode) => { setDoc(d); toast(mode === 'reset' ? 'Back to automatic labels.' : mode === 'add' ? 'Added your area as another label.' : 'Will print only your area.'); },
});
$('cropBtn').onclick = () => { if (state.doc) cropper.open(state.doc, state.doc.labels && state.doc.labels[state.page] ? state.doc.labels[state.page].page : 0); };
$('rescueBtn').onclick = guard(async () => {
  if (!confirm('Labels printing as code (q812, D8, P1…) or symbols?\n\nThis stops everything and sends the printer the "switch to label mode" command. Continue?')) return;
  const r = await api('/rescue', { method: 'POST' }); toast(r.message); poll();
});
function renderHeal() { const s = state.settings; if (s) $('healState').textContent = s.autoHealLineMode ? 'ON (tested on this printer)' : 'Off'; }
$('healOff').onclick = guard(async () => { const r = await api('/autoheal/false', { method: 'POST' }); state.settings = r.settings; renderHeal(); toast('Line-mode protection off.'); });
$('healTest').onclick = guard(async () => {
  if (!confirm('This prints 2 small labels.\n\nLabel 1 checks the protection command is harmless. For label 2 the app deliberately puts the printer into Line Mode, then checks it recovers by itself.\n\nStart?')) return;
  await api('/test/heal1', { method: 'POST' });
  await new Promise(r => setTimeout(r, 4000));
  await api('/test/heal2', { method: 'POST' });
  await new Promise(r => setTimeout(r, 9000));
  const ok = confirm('Look at the 2 labels.\n\nOK = BOTH show only "PROTECTION TEST 1 OK" / "PROTECTION TEST 2 OK" plus two short lines (no q812/D8/P1 lines, no symbols).\nCancel = either label shows code or symbols.');
  if (ok) { const r = await api('/autoheal/true', { method: 'POST' }); state.settings = r.settings; renderHeal(); toast('Protection is ON. If the printer ever slips into Line Mode, the next print fixes it.'); }
  else { const r = await api('/autoheal/false', { method: 'POST' }); state.settings = r.settings; renderHeal(); await api('/rescue', { method: 'POST' }); toast('Protection left OFF. Sent the fix command: turn the printer OFF and ON, then run 1. Raw text test.', true); }
});
$('stopBtn').onclick = guard(async () => { const r = await api('/stop', { method: 'POST' }); toast(r.message); poll(); });
$('zoomBtn').onclick = () => { const st = $('stage'); st.classList.toggle('zoomed'); $('zoomBtn').textContent = st.classList.contains('zoomed') ? 'Zoom: fit' : 'Zoom: actual dots'; };
function renderSafe() {
  const s = state.settings; if (!s) return;
  const needs = !s.printerVerified && s.backend !== 'SaveToFile' && !(s.printerName || '').startsWith('FILE:');
  $('safeBanner').hidden = !needs;
}
const fixMode = guard(async () => { await api('/test/pagemode', { method: 'POST' }); toast('Sent ESC EPL2 (switch to label mode). Nothing should print. Turn the printer off and on, then press "1. Send raw text test".'); poll(); });
$('dMode').onclick = fixMode;
$('safeTest').onclick = guard(async () => { await api('/test/hello', { method: 'POST' }); toast('Raw text test sent. Check the printer.'); poll(); });
$('safeYes').onclick = guard(async () => {
  if (!confirm('Confirm: the label showed ONLY "RAW EPL2 PATH OK" and two short lines, with NO lines like q812 / D8 / P1?')) return; const r = await api('/verify/true', { method: 'POST' }); state.settings = r.settings; renderSafe(); toast('Great. Labels and PDFs can print now.'); });
$('safeNo').onclick = guard(async () => {
  await api('/verify/false', { method: 'POST' });
  if (confirm('Your printer is in Line Mode (prints commands as text). Send the "switch to label mode" command now (ESC EPL2)?\n\nAfterwards: turn the printer OFF and ON, then press "1. Send raw text test" again.')) await fixMode();
});
$('quit').onclick = guard(async () => { if (confirm('Quit RetroLabel? Phone printing will stop until you start it again.')) { await api('/quit', { method: 'POST' }); document.body.innerHTML = '<p style="padding:40px">Stopped. You can close this window.</p>'; } });

// ---------- polling ----------
let lastV = -1, busy = false;
async function poll() {
  if (busy) return; busy = true;
  try {
    const s = await api('/state' + '?v=' + lastV);
    lastV = s.version; state.jobs = s.jobs; state.remote = s.remote;
    if (!state.settings) { state.settings = s.settings; fillForm(); await loadPrinters(); }
    if (state.settings) { state.settings.printerVerified = s.settings.printerVerified; state.settings.autoHealLineMode = s.settings.autoHealLineMode; }
    renderHeal();
    renderJobs(); renderRemote(); renderSafe();
    const ub = $('updateBanner'); ub.hidden = !s.update;
    if (s.update && !ub.dataset.v) { ub.dataset.v = s.update.version; ub.replaceChildren(h('b', {}, 'Update available: ' + s.update.version + '. '), 'Quit the app, then run the new installer over this one. ', h('a', { href: s.update.download || s.update.page, target: '_blank' }, 'Download')); }
    if (s.watch) $('watchStatus').textContent = s.watch.active ? 'Watching: ' + s.watch.folder : (s.watch.error ? 'Not watching: ' + s.watch.error : 'Off');
    if (s.incoming && s.incoming.seq !== state.incomingSeq) {
      const first = state.incomingSeq === undefined; state.incomingSeq = s.incoming.seq;
      if (!first) { try { const d = await api('/docs/' + s.incoming.docId); showTab('print'); state.page = 0; setDoc(d); toast('New label from your Downloads: ' + d.name); } catch (e) {} }
    }
  } catch (e) { $('printerChip').textContent = 'Disconnected'; $('printerChip').className = 'chip bad'; }
  finally { busy = false; }
}
setInterval(poll, 1500); poll();
})();
