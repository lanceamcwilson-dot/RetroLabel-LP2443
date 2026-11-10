(() => {
'use strict';
const $ = (id) => document.getElementById(id);
const KEY = 'zlp-token';
let token = null; try { token = localStorage.getItem(KEY); } catch {}
let doc = null, page = 0, blobUrl = null;

function toast(m, bad) { const t = $('toast'); t.textContent = m; t.className = 'toast' + (bad ? ' bad' : ''); t.hidden = false; clearTimeout(toast.t); toast.t = setTimeout(() => t.hidden = true, bad ? 6000 : 2500); }
async function api(path, opts = {}) {
  const headers = { ...(opts.headers || {}) };
  if (token) headers.Authorization = 'Bearer ' + token;
  let body = opts.body;
  if (opts.json !== undefined) { body = JSON.stringify(opts.json); headers['Content-Type'] = 'application/json'; }
  const r = await fetch('/api/m' + path, { method: opts.method || 'GET', headers, body });
  if (opts.blob && r.ok) return r.blob();
  const data = await r.json().catch(() => ({}));
  if (r.status === 401 && token) { forget(); throw new Error(data.error || 'Not paired'); }
  if (!r.ok) throw new Error(data.error || ('Error ' + r.status));
  return data;
}
function forget() { token = null; try { localStorage.removeItem(KEY); } catch {} show(); }
function show() { $('pairView').hidden = !!token; $('mainView').hidden = !token; if (token) poll(); try { fillShortcut(); } catch {} }

// ---------- pairing ----------
const guess = () => /iPhone/.test(navigator.userAgent) ? 'iPhone' : /iPad/.test(navigator.userAgent) ? 'iPad' : /Android/.test(navigator.userAgent) ? 'Android phone' : 'Phone';
$('devName').value = guess();
const m = location.hash.match(/pair=([A-Za-z0-9]+)/);
if (m) { $('pairCode').value = m[1]; history.replaceState(null, '', location.pathname); }
$('pairBtn').onclick = async () => {
  $('pairErr').hidden = true;
  try {
    const r = await api('/pair', { method: 'POST', json: { code: $('pairCode').value, name: $('devName').value } });
    token = r.token; try { localStorage.setItem(KEY, token); } catch { toast('Browser storage is blocked; you will need to pair again next time.', true); }
    show();
  } catch (e) { $('pairErr').textContent = e.message; $('pairErr').hidden = false; }
};
$('unpair').onclick = () => { if (confirm('Forget this phone? You will need to scan the QR code again.')) forget(); };

// ---------- choose + preview ----------
$('pick').onclick = () => $('file').click();
$('file').onchange = async (e) => {
  const f = e.target.files[0]; e.target.value = ''; if (!f) return;
  if (f.size > 15 * 1024 * 1024) return toast('That PDF is larger than 15 MB.', true);
  $('pick').disabled = true; $('pick').textContent = 'Uploading…';
  try {
    const d = await api('/docs', { method: 'POST', body: f, headers: { 'Content-Type': 'application/pdf', 'X-File-Name': encodeURIComponent(f.name) } });
    page = 0; setMDoc(d);
  } catch (err) { toast(err.message, true); }
  $('pick').disabled = false; $('pick').textContent = 'Choose label PDF';
};

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
  api: (path, opts) => api(path, opts), toast,
  getImg: async (id, p) => URL.createObjectURL(await api(`/docs/${id}/page/${p}.png`, { blob: true })),
  onDone: (d, mode) => { setMDoc(d); toast(mode === 'reset' ? 'Back to automatic labels.' : mode === 'add' ? 'Added as another label.' : 'Will print only your area.'); },
});
$('cropBtn').onclick = () => { if (doc) cropper.open(doc, doc.labels && doc.labels[page] ? doc.labels[page].page : 0); };
function setMDoc(d) {
  doc = d; page = Math.min(page, d.pageCount - 1);
  $('docCard').hidden = false; $('docName').textContent = d.name;
  $('pager').hidden = $('allRow').hidden = d.pageCount < 2;
  $('cropNote').textContent = d.manualCrop ? 'Using your crop' : d.autoCropped ? 'Label found automatically' : '';
  showPage();
}
async function showPage() {
  $('pgInfo').textContent = doc.pageCount > 1 ? `Page ${page + 1}/${doc.pageCount}` : '';
  try {
    const [blob, info] = await Promise.all([api(`/docs/${doc.id}/preview/${page}.png`, { blob: true }), api(`/docs/${doc.id}/info/${page}`)]);
    if (blobUrl) URL.revokeObjectURL(blobUrl); blobUrl = URL.createObjectURL(blob); $('preview').src = blobUrl;
    $('warn').replaceChildren(...info.warnings.map(w => Object.assign(document.createElement('li'), { textContent: w })));
    const bc = info.barcodes;
    if (bc && bc.available && (bc.found || !bc.ok)) {
      const li = document.createElement('li'); li.className = 'bc ' + (bc.ok ? 'ok' : 'bad');
      li.textContent = (bc.ok ? '✓ ' : '✕ ') + bc.summary; $('warn').prepend(li);
    }
  } catch (e) { toast(e.message, true); }
}
$('prev').onclick = () => { if (page > 0) { page--; showPage(); } };
$('next').onclick = () => { if (page < doc.pageCount - 1) { page++; showPage(); } };
const cop = () => { let v = parseInt($('copies').value, 10); v = Math.min(99, Math.max(1, isNaN(v) ? 1 : v)); $('copies').value = v; return v; };
$('minus').onclick = () => { $('copies').value = cop() - 1; cop(); };
$('plus').onclick = () => { $('copies').value = cop() + 1; cop(); };
$('copies').onchange = cop;
$('print').onclick = async () => {
  $('print').disabled = true;
  try {
    const pages = $('allPages').checked || doc.pageCount < 2 ? null : [page];
    const r = await api('/print', { method: 'POST', json: { docId: doc.id, pages, copies: cop() } });
    toast(r.jobs[0].status === 'AwaitingApproval' ? 'Sent. Waiting for approval on the PC.' : 'Sent to the printer queue'); poll();
  } catch (e) { toast(e.message, true); }
  $('print').disabled = false;
};

// ---------- status ----------
function jobEl(j) {
  const d = document.createElement('div'); d.className = 'job';
  const t = document.createElement('div'); t.className = 't';
  const n = document.createElement('div'); n.className = 'n'; n.textContent = j.kind === 'pdf' ? `${j.docName} · p${j.page + 1}${j.copies > 1 ? ' ×' + j.copies : ''}` : j.docName;
  const s = document.createElement('span'); s.className = 'st ' + j.status; s.textContent = ({ AwaitingApproval: 'NEEDS APPROVAL', Spooled: 'IN QUEUE' })[j.status] || j.status.toUpperCase();
  t.append(n, s);
  const mm = document.createElement('div'); mm.className = 'm'; mm.textContent = j.message;
  const a = document.createElement('div'); a.className = 'a';
  const add = (label, fn) => { const b = document.createElement('button'); b.textContent = label; b.onclick = async () => { try { const r = await fn(); if (r && r.ok === false) toast(r.message, true); poll(); } catch (e) { toast(e.message, true); } }; a.append(b); };
  if (['Queued', 'AwaitingApproval', 'Spooled'].includes(j.status)) add('Cancel', () => api(`/jobs/${j.id}/cancel`, { method: 'POST' }));
  if (['Completed', 'Failed', 'Cancelled', 'Spooled'].includes(j.status)) add('Reprint', async () => { await api(`/jobs/${j.id}/reprint`, { method: 'POST' }); toast('Reprint queued'); });
  d.append(t, mm, a); return d;
}
let busy = false;
async function poll() {
  if (!token || busy || document.hidden) return; busy = true;
  try {
    const s = await api('/state');
    const p = $('pstat'); p.textContent = s.printer.offline ? 'Printer not connected' : s.printer.name ? 'Printer ' + s.printer.status : 'No printer set'; p.className = 'pill ' + (s.printer.offline || !s.printer.name ? 'bad' : 'ok');
    $('jobs').replaceChildren(...(s.jobs.length ? s.jobs.map(jobEl) : [Object.assign(document.createElement('p'), { className: 'muted', textContent: 'Nothing yet.' })]));
    $('approvalNote').hidden = !s.requireApproval;
  } catch (e) { $('pstat').textContent = 'PC unreachable'; $('pstat').className = 'pill bad'; }
  busy = false;
}
const scUrl = location.origin + '/api/m/quickprint';
function fillShortcut() { $('scUrlBox').value = scUrl; $('scKey').value = token ? 'Bearer ' + token : '(pair this phone first)'; }
fillShortcut();
// iPhones block navigator.clipboard on plain-http pages; the old execCommand('copy') on a selected field still works there.
function copyText(v, box) {
  if (navigator.clipboard && window.isSecureContext) return navigator.clipboard.writeText(v).then(() => true, () => legacyCopy(v, box));
  return Promise.resolve(legacyCopy(v, box));
}
function legacyCopy(v, box) {
  try {
    box.removeAttribute('readonly'); box.value = v; box.focus(); box.setSelectionRange(0, v.length);
    const ok = document.execCommand('copy'); box.setAttribute('readonly', ''); box.blur(); return ok;
  } catch { return false; }
}
document.querySelectorAll('[data-copy]').forEach(b => b.onclick = async (e) => {
  e.preventDefault(); fillShortcut();
  const isUrl = b.dataset.copy === 'url', box = isUrl ? $('scUrlBox') : $('scKey');
  const ok = await copyText(box.value, box);
  toast(ok ? (isUrl ? 'Address copied' : 'Key copied: paste it as the header Text') : 'Press and hold in the box, then Select All → Copy', !ok);
});
setInterval(poll, 2000); document.addEventListener('visibilitychange', poll);
show();
if ('serviceWorker' in navigator && window.isSecureContext) navigator.serviceWorker.register('/m/sw.js', { scope: '/m' }).catch(() => {});
})();
