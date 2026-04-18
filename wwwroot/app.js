'use strict';

// ─── state ───────────────────────────────────────────────────────────────────
let testRunning = false;
let statusTimer = null;
let logTimer    = null;

// ─── form fields to persist in localStorage ───────────────────────────────────
// Camera hardware settings (port, resolution, fps, rotation) come from server status
const PERSIST_FIELDS = [
  'in-mode','in-timeout','in-head','in-step',
  'roi-x','roi-y','roi-w','roi-h',
  'r2d-digits','r2d-rotate',
  'cmp-folder','cmp-steps',
  'led-space','led-l0','led-l1','led-l2','led-h0','led-h1','led-h2',
  'led-mask','led-hold',
  'blink-freq','blink-ftol','blink-duty','blink-dtol','blink-samples'
];

function saveFormState() {
  PERSIST_FIELDS.forEach(id => {
    const el = document.getElementById(id);
    if (el) localStorage.setItem('cs_' + id, el.value);
  });
}

function loadFormState() {
  PERSIST_FIELDS.forEach(id => {
    const val = localStorage.getItem('cs_' + id);
    const el  = document.getElementById(id);
    if (el && val !== null) el.value = val;
  });
}

// ─── init ─────────────────────────────────────────────────────────────────────
window.addEventListener('DOMContentLoaded', () => {
  loadFormState();
  onModeChange();
  onResolutionChange();
  document.getElementById('right-panel').addEventListener('change', saveFormState);
  loadCameraSettings();
  startStatusPolling();
  startLogPolling();
});

// ─── tab switching ────────────────────────────────────────────────────────────
function switchTab(name, btn) {
  document.querySelectorAll('.tab-content').forEach(t => t.classList.remove('active'));
  document.querySelectorAll('.tab-btn').forEach(b => b.classList.remove('active'));
  document.getElementById('tab-' + name).classList.add('active');
  btn.classList.add('active');
  if (name === 'logs')   loadLogs();
  if (name === 'camera') { refreshStatus(); loadCameraSettings(); }
}

// ─── mode params visibility ───────────────────────────────────────────────────
function onModeChange() {
  const mode = document.getElementById('in-mode').value;
  document.querySelectorAll('.mode-params').forEach(el => el.style.display = 'none');
  document.getElementById('params-blink').style.display = 'none';
  if (mode === 'Read2d')   document.getElementById('params-read2d').style.display = '';
  if (mode === 'Compare')  document.getElementById('params-compare').style.display = '';
  if (mode === 'CheckLed') document.getElementById('params-led').style.display = '';
  if (mode === 'BlinkLed') {
    document.getElementById('params-led').style.display = '';
    document.getElementById('params-blink').style.display = '';
  }
}

// ─── test start / cancel ──────────────────────────────────────────────────────
async function startTest() {
  if (testRunning) return;

  const mode    = document.getElementById('in-mode').value;
  const timeout = parseInt(document.getElementById('in-timeout').value) || 30000;

  const req = {
    mode,
    head:       document.getElementById('in-head').value,
    step:       document.getElementById('in-step').value,
    timeoutMs:  timeout,
    roi: {
      x:      parseInt(document.getElementById('roi-x').value) || 0,
      y:      parseInt(document.getElementById('roi-y').value) || 0,
      width:  parseInt(document.getElementById('roi-w').value) || 0,
      height: parseInt(document.getElementById('roi-h').value) || 0
    }
  };

  if (mode === 'Read2d') {
    req.expectedDigits = parseInt(document.getElementById('r2d-digits').value) || 0;
    req.adjustDegree   = parseInt(document.getElementById('r2d-rotate').value) || 0;
  }
  if (mode === 'Compare') {
    req.compareFolder = document.getElementById('cmp-folder').value;
    req.compareSteps  = parseInt(document.getElementById('cmp-steps').value) || 1;
  }
  if (mode === 'CheckLed' || mode === 'BlinkLed') {
    req.useHsv      = document.getElementById('led-space').value === 'hsv';
    req.colorLow    = [+document.getElementById('led-l0').value,
                       +document.getElementById('led-l1').value,
                       +document.getElementById('led-l2').value];
    req.colorHigh   = [+document.getElementById('led-h0').value,
                       +document.getElementById('led-h1').value,
                       +document.getElementById('led-h2').value];
    req.colorMaskPx = parseInt(document.getElementById('led-mask').value) || 100;
    req.colorHoldMs = parseInt(document.getElementById('led-hold').value) || 500;
  }
  if (mode === 'BlinkLed') {
    req.expectedFrequency  = parseFloat(document.getElementById('blink-freq').value) || 0;
    req.frequencyTolerance = parseFloat(document.getElementById('blink-ftol').value) || 0.5;
    req.expectedDuty       = parseFloat(document.getElementById('blink-duty').value) || 0;
    req.dutyTolerance      = parseFloat(document.getElementById('blink-dtol').value) || 5;
    req.blinkSampleCount   = parseInt(document.getElementById('blink-samples').value) || 20;
  }

  setTestRunning(true);
  showResult('RUNNING', '', '', '', 'running');
  resumeLive(); // ensure live feed during test

  try {
    const resp = await fetch('/api/test/start', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(req),
      signal: AbortSignal.timeout(timeout + 5000)
    });
    const data = await resp.json();
    handleTestResult(data);
  } catch (err) {
    showResult('ERROR', '', err.message, '', 'fail');
  } finally {
    setTestRunning(false);
  }
}

async function cancelTest() {
  await fetch('/api/test/cancel', { method: 'DELETE' });
  setTestRunning(false);
  showResult('CANCELLED', '', '', '', 'fail');
}

function handleTestResult(r) {
  const status  = (r.status || '').toUpperCase();
  const cls     = status === 'PASS' ? 'pass' : 'fail';
  const elapsed = r.elapsedMs != null ? `${(r.elapsedMs / 1000).toFixed(2)}s` : '';
  const ts      = r.timestamp ? new Date(r.timestamp).toLocaleTimeString() : '';
  showResult(status, r.value || '', r.details || '', `${elapsed}  ${ts}`, cls);
  freezeFrame(); // hold the last test frame
}

function setTestRunning(running) {
  testRunning = running;
  document.getElementById('btn-start').disabled  = running;
  document.getElementById('btn-cancel').disabled = !running;
}

function showResult(badge, value, detail, time, cls) {
  const el = document.getElementById('result-badge');
  el.textContent = badge;
  el.className   = 'badge-' + (cls || 'idle');
  document.getElementById('result-value').textContent  = value;
  document.getElementById('result-detail').textContent = detail;
  document.getElementById('result-time').textContent   = time;
}

// ─── status polling ───────────────────────────────────────────────────────────
function startStatusPolling() {
  refreshStatus();
  statusTimer = setInterval(refreshStatus, 2000);
}

async function refreshStatus() {
  try {
    const r = await fetch('/api/camera/status');
    if (!r.ok) return;
    const s = await r.json();
    updateStatusUI(s);
  } catch {}
}

function updateStatusUI(s) {
  const ready   = s.state === 'Ready';
  const stopped = s.state === 'Stopped';
  const pill    = document.getElementById('cam-status-pill');
  if (ready) {
    pill.className = 'pill pill-ok';
    pill.innerHTML = '<span class="dot"></span>Ready';
  } else if (stopped) {
    pill.className = 'pill pill-err';
    pill.innerHTML = '<span class="dot"></span>Stopped';
  } else if (s.state === 'BlackFrame' || s.state === 'Reopening') {
    pill.className = 'pill pill-warn';
    pill.innerHTML = `<span class="dot pulse"></span>${s.state}`;
  } else {
    pill.className = 'pill pill-err';
    pill.innerHTML = `<span class="dot"></span>${s.state}`;
  }

  // sync start / stop buttons
  const btnStart = document.getElementById('btn-cam-start');
  const btnStop  = document.getElementById('btn-cam-stop');
  if (btnStart && btnStop) {
    btnStart.style.display = s.enabled ? 'none' : '';
    btnStop.style.display  = s.enabled ? ''     : 'none';
  }

  document.getElementById('fps-label').textContent    = `${s.fps} fps`;
  document.getElementById('res-label').textContent    = `${s.width}×${s.height}`;
  document.getElementById('uptime-label').textContent = `up ${formatUptime(s.uptimeSeconds)}`;

  // camera tab
  document.getElementById('ci-state').textContent  = s.state;
  document.getElementById('ci-port').textContent   = s.port;
  document.getElementById('ci-res').textContent    = `${s.width}×${s.height}`;
  document.getElementById('ci-fps').textContent    = `${s.fps}`;
  document.getElementById('ci-uptime').textContent = formatUptime(s.uptimeSeconds);
  document.getElementById('ci-black').textContent  = s.blackFrameCount;
}

function formatUptime(sec) {
  if (sec < 60)   return `${sec}s`;
  if (sec < 3600) return `${Math.floor(sec/60)}m ${sec%60}s`;
  return `${Math.floor(sec/3600)}h ${Math.floor((sec%3600)/60)}m`;
}

// ─── camera settings sync ─────────────────────────────────────────────────────
async function loadCameraSettings() {
  try {
    const r = await fetch('/api/camera/status');
    if (!r.ok) return;
    const s = await r.json();

    const portEl = document.getElementById('in-port');
    if (portEl) portEl.value = s.port ?? 0;

    const rotEl = document.getElementById('sel-rotation');
    if (rotEl) rotEl.value = String(s.rotation ?? 0);

    const fpsEl = document.getElementById('sel-fps');
    if (fpsEl && s.targetFps > 0) fpsEl.value = String(s.targetFps);

    // sync resolution selector
    const resEl = document.getElementById('sel-resolution');
    if (resEl && s.width > 0 && s.height > 0) {
      const key = `${s.width}x${s.height}`;
      const match = Array.from(resEl.options).find(o => o.value === key);
      resEl.value = match ? key : 'custom';
      if (!match) {
        document.getElementById('res-w').value = s.width;
        document.getElementById('res-h').value = s.height;
        document.getElementById('res-custom').style.display = '';
      }
      onResolutionChange();
    }
  } catch {}
}

async function setPort() {
  const port = parseInt(document.getElementById('in-port').value) || 0;
  try {
    await fetch('/api/camera/port', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ port })
    });
  } catch {}
}

async function forceReopen() {
  try { await fetch('/api/camera/reopen', { method: 'POST' }); } catch {}
}

async function stopCamera() {
  try { await fetch('/api/camera/stop', { method: 'POST' }); } catch {}
}

async function startCamera() {
  try { await fetch('/api/camera/start', { method: 'POST' }); } catch {}
}

function onResolutionChange() {
  const val = document.getElementById('sel-resolution').value;
  document.getElementById('res-custom').style.display = val === 'custom' ? '' : 'none';
}

async function setRotation() {
  const degrees = parseInt(document.getElementById('sel-rotation').value) || 0;
  try {
    await fetch('/api/camera/rotation', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ degrees })
    });
  } catch {}
}

async function setTargetFps() {
  const fps = parseInt(document.getElementById('sel-fps').value) || 30;
  try {
    await fetch('/api/camera/fps', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ fps })
    });
  } catch {}
}

async function saveSettings(btn) {
  if (btn) { btn.disabled = true; btn.textContent = '⏳ Saving…'; }
  try {
    await fetch('/api/camera/save', { method: 'POST' });
    if (btn) btn.textContent = '✓ Saved';
    setTimeout(() => { if (btn) { btn.disabled = false; btn.innerHTML = '💾 Save Settings'; } }, 1500);
  } catch {
    if (btn) { btn.disabled = false; btn.innerHTML = '💾 Save Settings'; }
  }
}

async function setResolution() {
  const val = document.getElementById('sel-resolution').value;
  let width, height;
  if (val === 'custom') {
    width  = parseInt(document.getElementById('res-w').value) || 640;
    height = parseInt(document.getElementById('res-h').value) || 480;
  } else if (val === '0x0') {
    width = 0; height = 0;
  } else {
    [width, height] = val.split('x').map(Number);
  }
  try {
    await fetch('/api/camera/resolution', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ width, height })
    });
  } catch {}
}

async function showCameraProperties() {
  const btn  = document.getElementById('btn-cam-props');
  const hint = document.getElementById('cam-props-hint');
  btn.disabled = true;
  btn.textContent = '⏳ Opening…';
  try {
    const r = await fetch('/api/camera/show-properties', { method: 'POST' });
    const d = await r.json();
    if (r.ok) {
      hint.style.display = '';
      btn.textContent = '⚙️ Dialog is open (on server screen)';
      // Poll until dialog closes (capture resumes → status goes back to Ready)
      const poll = setInterval(async () => {
        try {
          const sr = await fetch('/api/camera/status');
          const ss = await sr.json();
          // Once the dialog is closed the capture loop resumes
          // We detect this by checking _showingDialog indirectly via fps > 0
          if (ss.fps > 0) {
            clearInterval(poll);
            btn.disabled = false;
            btn.textContent = '⚙️ Open Camera Properties';
            hint.style.display = 'none';
          }
        } catch { clearInterval(poll); }
      }, 1000);
    } else {
      alert(d.message || 'Could not open dialog');
      btn.disabled = false;
      btn.textContent = '⚙️ Open Camera Properties';
    }
  } catch (err) {
    alert('Error: ' + err.message);
    btn.disabled = false;
    btn.textContent = '⚙️ Open Camera Properties';
  }
}

// ─── snapshot / freeze ────────────────────────────────────────────────────────
let _frozenBlobUrl = null;

async function freezeFrame() {
  try {
    const r = await fetch('/snapshot/inline');
    if (!r.ok) return;
    const blob = await r.blob();
    const url  = URL.createObjectURL(blob);
    const img  = document.getElementById('stream');
    if (_frozenBlobUrl) URL.revokeObjectURL(_frozenBlobUrl);
    _frozenBlobUrl = url;
    img.src = url;
    document.getElementById('btn-resume').style.display = '';
  } catch {}
}

function resumeLive() {
  const img = document.getElementById('stream');
  if (_frozenBlobUrl) {
    URL.revokeObjectURL(_frozenBlobUrl);
    _frozenBlobUrl = null;
  }
  img.src = '/stream';
  document.getElementById('btn-resume').style.display = 'none';
}

async function doSnapshot() {
  await freezeFrame();
}

// ─── logs ─────────────────────────────────────────────────────────────────────
function startLogPolling() {
  loadLogs();
  logTimer = setInterval(() => {
    if (document.getElementById('log-auto').checked &&
        document.getElementById('tab-logs').classList.contains('active')) {
      loadLogs();
    }
  }, 3000);
}

async function loadLogs() {
  const level = document.getElementById('log-level-filter')?.value || '';
  const url   = `/api/logs?count=100${level ? '&level=' + level : ''}`;
  try {
    const r    = await fetch(url);
    const data = await r.json();
    renderLogs(data);
  } catch {}
}

function renderLogs(entries) {
  const box = document.getElementById('log-box');
  if (!box) return;
  const atBottom = box.scrollHeight - box.scrollTop <= box.clientHeight + 20;

  box.innerHTML = entries.map(e => {
    const t   = e.time?.slice(11, 23) ?? '';
    const cat = e.category ?? '';
    const msg = e.message  ?? '';
    const ctx = e.head ? `[${e.head}/${e.step}] ` : '';
    return `<div class="log-line log-${e.level}">
      <span style="color:var(--muted)">${t}</span>
      <span style="color:currentColor;font-weight:600;margin:0 5px;">${cat}</span>
      <span style="color:var(--muted);font-size:10px;">${ctx}</span>
      <span>${escHtml(msg)}</span>
    </div>`;
  }).join('');

  if (atBottom) box.scrollTop = box.scrollHeight;
}

async function exportLog() {
  try {
    const r    = await fetch('/api/logs/export');
    if (!r.ok) { alert('No log file found'); return; }
    const blob = await r.blob();
    const cd   = r.headers.get('Content-Disposition') || '';
    const name = cd.match(/filename="?([^"]+)"?/)?.[1] ?? 'camera_log.jsonl';
    const url  = URL.createObjectURL(blob);
    const a    = document.createElement('a');
    a.href = url; a.download = name; a.click();
    URL.revokeObjectURL(url);
  } catch {}
}

function escHtml(str) {
  return String(str).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
}
