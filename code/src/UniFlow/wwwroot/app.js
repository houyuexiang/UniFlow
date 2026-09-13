const BASE = '/api';

async function api(path, opts) {
  try {
    const r = await fetch(BASE + path, opts);
    if (!r.ok) throw new Error(r.statusText);
    return await r.json();
  } catch(e) {
    console.error('API error:', e);
    return null;
  }
}

// ===== Tab Switching =====
document.querySelectorAll('.tab').forEach(tab => {
  tab.addEventListener('click', () => {
    document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
    document.querySelectorAll('.tab-content').forEach(t => t.classList.remove('active'));
    tab.classList.add('active');
    document.getElementById('tab-' + tab.dataset.tab).classList.add('active');
  });
});

// ===== Dashboard =====
const FEATURE_MAP = {
  System: null,
  DisposeSample: { path: 'Aptio:DisposeSample', group: 'Aptio' },
  SrmExport: { path: 'Aptio:SrmExport', group: 'Aptio' },
  Delivery: { path: 'Aptio:Delivery', group: 'Aptio' },
  DeliveryFile: { path: 'Aptio:DeliveryFile', group: 'Aptio' },
  Priority: { path: 'Aptio:Priority', group: 'Aptio' },
  TestNameDispose: { path: 'Aptio:TestNameDispose', group: 'Aptio' },
  WorkListCleaner: { path: 'Immulite:WorkListCleaner', group: 'Immulite' },
  DmsPitStop: { path: 'Dms:PitStopMonitor', group: 'Dms' },
  DmsStatusCorr: { path: 'Dms:StatusCorrection', group: 'Dms' },
  DmsCleanup: { path: 'Dms:SampleCleanup', group: 'Dms' },
  DmsEmptyResultCleanup: { path: 'Dms:EmptyResultCleanup', group: 'Dms' },
};
const GROUP_ORDER = ['Aptio', 'Dms'];  // Immulite 暂未使用，隐藏

// 读取分组配置: cfg.Features["Aptio"]["DisposeSample"]
function getFeatureVal(cfg, path) {
  if (!cfg || !cfg.Features) return null;
  const parts = path.split(':');
  let cur = cfg.Features;
  for (const p of parts) {
    if (cur == null || typeof cur !== 'object') return null;
    cur = cur[p];
  }
  return cur;
}

async function loadVersion() {
  const v = await api('/version');
  if (v && v.informational) {
    document.getElementById('appVersion').textContent = 'v' + v.informational;
  }
}

let _pendingRestartCount = 0;

async function loadHealth() {
  const [data, cfg] = await Promise.all([api('/health'), api('/config')]);
  if (!data) return;
  const badge = document.getElementById('systemStatus');
  badge.textContent = data.status === 'healthy' ? '正常运行' : '异常';
  badge.className = 'status-badge ' + data.status;

  _pendingRestartCount = 0;

  const container = document.getElementById('healthCards');
  container.innerHTML = '';

  // 按功能组分组展示：以已知模块清单为基准（全部功能都有卡片，未启用的显示"未启用"），
  // 健康记录覆盖实时状态（无记录的启用功能显示"启动中"）
  const groups = {};
  const seen = new Set();
  for (const [mod, map] of Object.entries(FEATURE_MAP)) {
    if (!map) continue;                                   // System:null 等隐藏项
    if (mod === 'AptioBatchScanner') continue;            // 扫描器不单独展示
    const isOn = getFeatureVal(cfg, map.path);
    let info = data.modules[mod];
    let pendingBoot = false;   // 配置已开启但业务宿主未注册（需重启生效）
    if (!info) {
      info = { status: isOn ? 'unknown' : 'stopped', timestamp: '',
               message: isOn ? '启动中，等待健康上报' : '功能未启用' };
    } else if (info.status !== 'stopped' && info.status !== 'degraded' && !isOn) {
      // 功能已被关闭（即使健康记录还是陈旧 healthy）→ 强制按未启用显示
      info = { ...info, status: 'stopped', message: '功能未启用' };
    } else if (info.status === 'stopped' && isOn) {
      // 配置开启但 Worker 未随当前宿主注册（开启时宿主已在跑）→ 需重启业务宿主
      pendingBoot = true;
      info = { ...info, message: '已开启，需重启服务生效' };
    }
    if (pendingBoot) _pendingRestartCount = (_pendingRestartCount || 0) + 1;
    const group = map.group || 'Immulite';
    if (!groups[group]) groups[group] = [];
    groups[group].push({ mod, info, map });
    seen.add(mod);
  }
  // 待重启横幅：有配置已开启但未生效的功能时显示
  renderRestartBanner();

  // 清单外模块（如仪器名 Immulite2000）：归 Immulite 组（该组暂不渲染于 GROUP_ORDER）
  for (const [mod, info] of Object.entries(data.modules)) {
    if (seen.has(mod)) continue;
    if (mod === 'System' || mod === 'DmsAutoOrder' || mod === 'AptioBatchScanner') continue;
    if (!groups.Immulite) groups.Immulite = [];
    groups.Immulite.push({ mod, info, map: undefined });
  }

  for (const group of GROUP_ORDER) {
    const items = groups[group];
    if (!items || items.length === 0) continue;

    // 分组标题
    const hdr = document.createElement('div');
    hdr.className = 'group-header';
    hdr.textContent = group;
    hdr.style.gridColumn = '1 / -1';
    container.appendChild(hdr);

    for (const { mod, info, map } of items) {
      const card = document.createElement('div');
      card.className = 'card ' + (info.status || 'unknown');
      // map 存在但无 path → 无独立开关；map 不存在 → 仪器名，用 WorkListCleaner 开关
      let featPath = map === undefined ? 'Immulite.WorkListCleaner' : (map && map.path ? map.path : null);
      let featVal = featPath ? getFeatureVal(cfg, featPath) : null;
      let isOn = featVal === true;
      let toggleHtml = featPath ? `<span class="toggle-switch${isOn?' on':''}" style="vertical-align:middle;margin-left:4px"></span>` : '';
      card.innerHTML = `<div class="label">${mod}${toggleHtml}</div>
        <div class="value">${info.status === 'healthy' ? '✓' : info.status === 'degraded' ? '⚠' : info.status === 'unknown' ? '…' : '⏻'}</div>
        <div class="meta">${info.timestamp || ''} ${info.message ? '· ' + info.message : ''}</div>`;
      if (featPath) {
        card.querySelector('.toggle-switch').onclick = function(e) {
          e.stopPropagation();
          // 要打开（!isOn）且当前未运行（stopped，如"功能未启用"）→ 先确认需重启才生效
          if (!isOn && info.status === 'stopped') {
            if (!confirm('该功能当前未运行；启用后需要重启服务才能生效。确定继续？')) return;
          }
          api('/config', { method:'PUT', headers:{'Content-Type':'application/json'}, body:JSON.stringify({path:'Features:'+featPath, value:!isOn}) }).then(function(){ loadHealth(); });
        };
      }
      container.appendChild(card);
    }
  }
}

async function loadHealthHistory() {
  const days = document.getElementById('healthDays').value;
  const data = await api(`/health/history?days=${days}`);
  if (!data) return;
  const container = document.getElementById('healthChart');
  // Group by date
  const byDate = {};
  data.forEach(r => {
    const d = r.timestamp.substring(0, 10);
    if (!byDate[d]) byDate[d] = { healthy: 0, degraded: 0, down: 0 };
    byDate[d][r.status]++;
  });
  const dates = Object.keys(byDate).sort();
  if (dates.length === 0) { container.innerHTML = '<div class="loading">暂无数据</div>'; return; }
  const max = Math.max(...dates.map(d => Math.max(byDate[d].healthy, byDate[d].degraded, byDate[d].down, 1)));
  let html = '<div class="chart-bar-area">';
  dates.forEach(d => {
    const h = byDate[d].healthy / max * 150;
    const dg = byDate[d].degraded / max * 150;
    const dn = byDate[d].down / max * 150;
    const total = byDate[d].healthy + byDate[d].degraded + byDate[d].down;
    html += `<div style="display:flex;flex-direction:column;align-items:center;flex:1;height:150px;justify-content:flex-end;">
      <div class="chart-bar green" style="height:${Math.max(h, 2)}px"><div class="tooltip">${d}: ${byDate[d].healthy} healthy</div></div>
      ${dg > 0 ? `<div class="chart-bar yellow" style="height:${Math.max(dg, 2)}px"><div class="tooltip">${d}: ${byDate[d].degraded} degraded</div></div>` : ''}
      ${dn > 0 ? `<div class="chart-bar red" style="height:${Math.max(dn, 2)}px"><div class="tooltip">${d}: ${byDate[d].down} down</div></div>` : ''}
      <span style="font-size:10px;color:var(--text2);margin-top:4px">${d.slice(5)}</span>
    </div>`;
  });
  html += '</div>';
  container.innerHTML = html;
}

// ===== Errors =====
async function clearAllErrors() {
  if (!confirm('确定清除所有错误记录？此操作不可恢复。')) return;
  await api('/errors', { method: 'DELETE' });
  loadErrors();
}

async function loadErrors() {
  const days = document.getElementById('errorDays').value;
  const level = document.getElementById('errorLevel').value;
  const params = `days=${days}` + (level ? `&level=${level}` : '');
  const data = await api(`/errors?${params}`);
  const summary = await api(`/errors/summary?days=${days}`);
  if (!data) return;

  const summaryEl = document.getElementById('errorSummary');
  summaryEl.innerHTML = `<div class="error-stat">总计: <span class="num">${data.length}</span></div>`;
  if (summary) {
    summary.byModule.forEach(m => {
      summaryEl.innerHTML += `<div class="error-stat">${m.module}: <span class="num">${m.count}</span></div>`;
    });
  }

  const tbody = document.getElementById('errorBody');
  tbody.innerHTML = data.length === 0 ? '<tr><td colspan="4" style="text-align:center;color:var(--text2)">暂无错误记录</td></tr>' : '';
  data.forEach(r => {
    const tr = document.createElement('tr');
    tr.innerHTML = `<td>${r.timestamp}</td><td>${r.module}</td><td class="level-${r.level}">${r.level}</td><td>${escapeHtml(r.message).substring(0, 200)}</td>`;
    tbody.appendChild(tr);
  });
}

// ===== Config =====
async function loadConfig() {
  // 「配置管理」页签已移除（表单+仪表盘覆盖），此处仅保留 API 兼容；容器不存在时跳过
  if (!document.getElementById('configTree')) return;
  const data = await api('/config');
  if (!data) return;
  const container = document.getElementById('configTree');
  container.innerHTML = '';
  renderConfigNode(container, data, '');
}

function renderConfigNode(parent, obj, path) {
  // 原始类型（含字符串）：直接按 leaf 渲染（否则 Object.entries 会把字符串逐字符拆开）
  if (obj !== null && typeof obj !== 'object') {
    const node = document.createElement('div');
    node.className = 'config-node';
    node.style.paddingLeft = '20px';
    const keySpan = document.createElement('span');
    keySpan.className = 'config-key';
    keySpan.textContent = path.split(':').pop() + ': ';
    node.appendChild(keySpan);
    const valSpan = document.createElement('span');
    valSpan.className = 'config-string';
    valSpan.textContent = '"' + obj + '"';
    node.appendChild(valSpan);
    parent.appendChild(node);
    return;
  }
  if (Array.isArray(obj)) {
    obj.forEach((item, index) => {
      const node = document.createElement('div');
      node.className = 'config-node';
      const toggle = document.createElement('span');
      toggle.className = 'config-toggle';
      toggle.textContent = '▼ ';
      toggle.onclick = () => {
        const child = node.nextElementSibling;
        if (child) { child.style.display = child.style.display === 'none' ? '' : 'none'; }
      };
      node.appendChild(toggle);
      const span = document.createElement('span');
      span.className = 'config-key';
      span.textContent = `[${index}]`;
      node.appendChild(span);
      parent.appendChild(node);
      const childContainer = document.createElement('div');
      childContainer.style.paddingLeft = '20px';
      renderConfigNode(childContainer, item, path ? `${path}[${index}]` : `[${index}]`);
      parent.appendChild(childContainer);
    });
    return;
  }
  for (const [key, value] of Object.entries(obj)) {
    const fullPath = path ? path + ':' + key : key;
    const node = document.createElement('div');
    node.className = 'config-node';
    if (typeof value === 'object' && value !== null && !(value instanceof Date)) {
      const toggle = document.createElement('span');
      toggle.className = 'config-toggle';
      toggle.textContent = '▼ ';
      toggle.onclick = () => {
        const child = node.nextElementSibling;
        if (child) { child.style.display = child.style.display === 'none' ? '' : 'none'; }
      };
      node.appendChild(toggle);
      const span = document.createElement('span');
      span.className = 'config-key';
      span.textContent = Array.isArray(value) ? key + ` [${value.length} 项]` : key;
      node.appendChild(span);
      parent.appendChild(node);
      const childContainer = document.createElement('div');
      childContainer.style.paddingLeft = '20px';
      renderConfigNode(childContainer, value, fullPath);
      parent.appendChild(childContainer);
    } else {
      node.style.paddingLeft = '20px';
      const keySpan = document.createElement('span');
      keySpan.className = 'config-key';
      keySpan.textContent = key + ': ';
      node.appendChild(keySpan);
      const valSpan = document.createElement('span');
      valSpan.style.cursor = 'pointer';
      valSpan.className = typeof value === 'string' ? 'config-string' : typeof value === 'number' ? 'config-number' : 'config-bool';
      valSpan.textContent = typeof value === 'string' ? '"' + value + '"' : String(value);
      valSpan.onclick = () => startEdit(valSpan, fullPath, value);
      node.appendChild(valSpan);
      parent.appendChild(node);
    }
  }
}

const ENUM_PATHS = {
  'Logging:LogLevel:Default': ['Trace', 'Debug', 'Information', 'Warning', 'Error', 'Critical', 'None'],
  'Logging:LogLevel:Microsoft.Hosting.Lifetime': ['Trace', 'Debug', 'Information', 'Warning', 'Error', 'Critical', 'None'],
  'Logging:LogLevel:UniFlow': ['Trace', 'Debug', 'Information', 'Warning', 'Error', 'Critical', 'None'],
  'Logging:UniFlowFile:LogLevel': ['Trace', 'Debug', 'Information', 'Warning', 'Error', 'Critical', 'None'],
  'WebAdmin:BindIp': ['localhost', '0.0.0.0', '127.0.0.1', '*'],
};

function startEdit(span, path, oldValue) {
  const isString = typeof oldValue === 'string';
  const isBool = typeof oldValue === 'boolean';
  const container = document.createElement('span');
  container.className = 'config-edit';
  const enumOptions = ENUM_PATHS[path];
  const displayText = span.textContent;

  var html = '';
  if (isBool) {
    html = '<select><option value="true"' + (oldValue ? ' selected' : '') + '>true</option><option value="false"' + (oldValue ? '' : ' selected') + '>false</option></select>';
  } else if (enumOptions) {
    html = '<select>' + enumOptions.map(function(o) { return '<option value="' + o + '"' + (oldValue === o ? ' selected' : '') + '>' + o + '</option>'; }).join('') + '</select>';
  } else {
    html = '<input type="text" value="' + (isString ? oldValue.replace(/"/g, '&quot;') : String(oldValue)) + '">';
  }
  html += '<button class="btn-save">保存</button><button class="btn-cancel" onclick="loadConfig()">取消</button>';
  container.innerHTML = html;

  container.querySelector('.btn-save').onclick = function() {
    var input = container.querySelector('input') || container.querySelector('select');
    var val = input.value;
    if (val === 'true') val = true;
    else if (val === 'false') val = false;
    else if (!isNaN(val) && val !== '') val = Number(val);
    api('/config', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ path: path, value: val })
}).then(function() { loadConfig(); });
  };

  span.parentNode.replaceChild(container, span);
}

// ===== Log Files =====
async function loadLogFiles() {
  const days = document.getElementById('logDays').value;
  const modSel = document.getElementById('logModule');
  const logMod = modSel ? modSel.value : '';
  const data = await api(`/logs/list?days=${days}`);
  if (!data) return;
  const container = document.getElementById('logFileList');
  if (data.length === 0) { container.innerHTML = '<div class="loading">暂无日志文件</div>'; return; }
  container.innerHTML = '';
  // 按日期目录分组（可选模块过滤）
  const groups = {};
  data.filter(f => !logMod || f.name.startsWith(logMod + '_')).forEach(f => {
    const d = f.date || '其他';
    if (!groups[d]) groups[d] = [];
    groups[d].push(f);
  });
  Object.keys(groups).sort().reverse().forEach(d => {
    const hdr = document.createElement('div');
    hdr.style.cssText = 'grid-column:1 / -1;font-size:12px;color:var(--text2);font-weight:600;padding:8px 0 2px;';
    hdr.textContent = d;
    container.appendChild(hdr);
    groups[d].forEach(f => {
      const item = document.createElement('div');
      item.className = 'log-file-item' + (f.name.startsWith('UniFlow_Error') ? ' error-file' : '');
      const size = f.size > 1024 * 1024 ? (f.size / 1024 / 1024).toFixed(1) + 'MB' : (f.size / 1024).toFixed(1) + 'KB';
      item.innerHTML = `<div><div class="name">${f.name}</div><div class="meta">${f.lastModified} · ${size}</div></div><span>查看</span><span class="log-dl" title="下载该文件">下载</span>`;
      item.onclick = () => viewLogFile(f.path);
      item.querySelector('.log-dl').onclick = (e) => {
        e.stopPropagation();
        window.location = `/api/logs/download?file=${encodeURIComponent(f.path)}`;
      };
      container.appendChild(item);
    });
  });
}

async function viewLogFile(path) {
  const data = await api(`/logs/view?file=${encodeURIComponent(path)}&tail=200`);
  if (!data) return;
  document.getElementById('logViewer').style.display = 'block';
  document.getElementById('logViewerTitle').textContent = path + ' (最近200行)';
  document.getElementById('logContent').textContent = data.lines.join('\n');
}

function escapeHtml(s) {
  const div = document.createElement('div');
  div.textContent = s;
  return div.innerHTML;
}

// ===== API 说明页 =====
const API_DOCS = [
  { m:'GET',  ep:'/api/version',                    desc:'Program version',                              ex:'Open directly in browser',
    resp:'{"version":"1.0.6.0","informational":"1.0.6.0+e852259"}' },
  { m:'GET',  ep:'/api/health',                     desc:'Per-module health + overall aggregation',      ex:'Open directly in browser',
    resp:'{"status":"healthy","modules":{"Priority":{"status":"healthy","timestamp":"...","message":""},...},"summary":{"healthy":4,"degraded":0,"down":0}}' },
  { m:'GET',  ep:'/api/health/history?days=3',      desc:'Health history records',                       ex:'?days=<n>&module=<name> (optional)',
    resp:'[{"id":1,"timestamp":"2026-09-12 10:00:00","module":"Priority","status":"healthy","message":""},...]' },
  { m:'GET',  ep:'/api/config',                     desc:'Read full config (passwords masked as ***)',   ex:'Open directly in browser',
    resp:'{"ConfigVersion":"1.0","Features":{...},"Aptio":{...},"DMS":{...}}' },
  { m:'GET',  ep:'/api/config?path=Aptio:Port',     desc:'Read a single config item by path',            ex:'?path=<colon-separated path>, e.g. ?path=Aptio:Port',
    resp:'{"path":"Aptio:Port","value":2055}   (404 if not found; masked if Password key)' },
  { m:'PUT',  ep:'/api/config',                     desc:'Update one config item. Validation: unknown path / wrong type / out-of-range rejected with 400 + reason. Effective level returned.',
    ex:'string: {"path":"Aptio:Ip","value":"10.0.0.200"} | int: {"path":"Aptio:Port","value":2055} | bool: {"path":"Features:Aptio:Priority","value":true} | string[]: {"path":"Aptio:DisposeSample:AllowedSrmErrorCodes","value":["0000","0F0A"]} | int[]: {"path":"Aptio:DisposeSample:RunDays","value":[1,3,5]} | object[]: {"path":"DMS:SampleCleanup:TriggerRules","value":[{"TestName":"RMSMP","TimeoutMinutes":1}]} | timeRanges: {"path":"Aptio:DisposeSample:TimeRanges","value":[{"Start":"01:30","End":"05:45","Threshold":3800},{"Start":"18:00","End":"07:00","Threshold":7500}]}',
    resp:'{"success":true,"restart":{"level":"Hot|InnerRestart|ProcessRestart","message":"Saved. Effective immediately (hot reload)."}}  Hot=hot reload / InnerRestart=POST /api/restart / ProcessRestart=restart the OS service process. Rejected: 400 {"success":false,"error":"..."}' },
  { m:'POST', ep:'/api/restart',                     desc:'Restart business workers only (rebuild with latest config; web keeps running)', ex:'Returns 202 while starting; 409 if restart already in progress',
    resp:'{"restarting":true,"level":"inner","hint":"..."}' },
  { m:'POST', ep:'/api/restart/process',             desc:'Full process restart (exit code 1, auto-relaunched by systemd/Service Recovery). Used after upgrades or for process-level config to take effect', ex:'curl -X POST /api/restart/process',
    resp:'{"restarting":true,"level":"process","hint":"..."}' },
  { m:'GET',  ep:'/api/restart/status',             desc:'Restart state (ready/restarting/lastFailure)', ex:'Open directly in browser',
    resp:'{"ready":true,"restarting":false,"lastFailure":null}' },
  { m:'GET',  ep:'/api/update/status',              desc:'Update staging status (currentVersion/hasOldFile)', ex:'Open directly in browser',
    resp:'{"staged":true,"hasOldFile":true,"currentVersion":"1.0.6.0","backupAvailable":true}' },
  { m:'POST', ep:'/api/update/upload',              desc:'Upload an upgrade package (raw binary zip body; package may contain optional top-level dir like "uniflow/"). Binary+wwwroot are staged; appsettings.json is never overwritten', ex:'curl -X POST --data-binary @UniFlow-1.0.6.1-win-x64.zip /api/update/upload',
    resp:'{"staged":true,"files":["UniFlow.exe","wwwroot/app.js"],"md5":"274422b1...","hint":"POST /api/update/apply to apply the upgrade."}' },
  { m:'POST', ep:'/api/update/apply',               desc:'Apply the staged upgrade: backup current binary+wwwroot to backup/<timestamp>/, rename-replace binary (running exe is renamed to *.old), overwrite wwwroot, remove staging. Then POST /api/restart/process to activate', ex:'curl -X POST /api/update/apply',
    resp:'{"applied":true,"backup":"C:\\\\UniFlow\\\\backup\\\\20260913103658","hint":"..."}' },
  { m:'GET',  ep:'/api/errors?days=3',              desc:'Error records query',                          ex:'?days=<n>&module=<name>&level=<ERROR|WARN> (all optional)',
    resp:'[{"id":1,"timestamp":"...","module":"DmsCleanup","level":"ERROR","message":"..."},...]' },
  { m:'GET',  ep:'/api/errors/summary?days=3',      desc:'Error summary grouped by module/level',        ex:'?days=<n>',
    resp:'{"total":37,"byModule":[{"module":"DisposeSample","count":37}],"byLevel":[...]}' },
  { m:'DELETE', ep:'/api/errors',                    desc:'Delete ALL error records',                     ex:'No body required',
    resp:'{"success":true}' },
  { m:'GET',  ep:'/api/logs/list?days=7',           desc:'Log file list',                                ex:'?days=<n>',
    resp:'[{"path":"2026-09-12/Priority_0.log","name":"Priority_0.log","date":"2026-09-12","size":48211,"lastModified":"..."},...]' },
  { m:'GET',  ep:'/api/logs/view?file=...&tail=200', desc:'View log content',                             ex:'?file=<relative path>&tail=<last N lines>',
    resp:'{"lines":["2026-09-12 ... [INFO] ...",...],"total":200}' },
  { m:'GET',  ep:'/api/logs/download?file=...',      desc:'Download a single log file (attachment)',      ex:'?file=<relative path>',
    resp:'Raw log file (Content-Disposition: attachment; filename=Priority_0.log)' },
  { m:'GET',  ep:'/api/logs/bundle?days=7&module=Priority', desc:'Download filtered logs as zip (date folders kept)', ex:'?days=<n>&module=<name> (module optional)',
    resp:'zip file (Content-Disposition: attachment; filename=UniFlow-logs-all-....zip)' },
  { m:'GET',  ep:'/api/config/download',            desc:'Download full config file as backup (contains secrets)', ex:'Open directly in browser (timestamped filename)',
    resp:'Raw appsettings.json (Content-Disposition: attachment)' },
];

function levelText(f) {
  if (!f.restart) return '保存即生效';
  return f.restart === 'process' ? '⚠ 进程级重启' : 'ⓘ 重启业务任务';
}

const EXAMPLES = {"Aptio:Ip": "\"10.0.0.200\"", "Aptio:Port": "2055", "Aptio:SrmNodeIds": "[\"09\",\"13\"]", "Aptio:Database:Host": "\"192.168.0.44\"", "Aptio:Database:Port": "3306", "Aptio:Database:User": "\"root\"", "Aptio:Database:Password": "\"***\" (string)", "Aptio:Database:Database": "\"flexlab\"", "Aptio:DisposeSample:CommandType": "0", "Aptio:DisposeSample:CommandName": "\"view_overtimestoragesample\"", "Aptio:DisposeSample:RunDays": "[1,3,5]", "Aptio:DisposeSample:TimeRanges": "[{\"Start\":\"01:30\",\"End\":\"05:45\",\"Threshold\":3800},{\"Start\":\"18:00\",\"End\":\"07:00\",\"Threshold\":7500}]", "Aptio:DisposeSample:LoopIntervalSeconds": "3", "Aptio:DisposeSample:MaxWaitDiscardCount": "2", "Aptio:DisposeSample:MaxOnetimeSelectDiscardCount": "8", "Aptio:DisposeSample:AllowedSrmErrorCodes": "[\"0000\",\"0F0A\",\"0E59\",\"AAAA\"]", "Aptio:DisposeSample:SkipOnUnknownNode": "true / false", "Aptio:SrmExport:ExportTime": "\"05:30\"", "Aptio:SrmExport:LoopIntervalSeconds": "60", "Aptio:SrmExport:LogRetentionDays": "30", "Aptio:SrmExport:OutputPath": "\"D:\\\\exports\" or \"/data/exports\"", "Aptio:Delivery:TestName": "\"TG\"", "Aptio:Priority:TestName": "\"FT3\"", "Aptio:TestNameDispose:DisposeTestName": "\"FT4\"", "Aptio:DeliveryFile:DeliveryListFilePath": "\"D:\\\\list\"", "Aptio:DeliveryFile:LoopIntervalSeconds": "60", "Aptio:BatchScan:LoopIntervalSeconds": "60", "Aptio:BatchScan:MaxOnetimeScanCount": "500", "DMS:DbHost": "\"10.0.0.100\"", "DMS:DbPort": "3306", "DMS:DbUser": "\"fse\"", "DMS:DbPassword": "\"***\" (string)", "DMS:DbName": "\"DMSCN\"", "DMS:LoopIntervalSeconds": "60", "DMS:StatusCorrection:AutoModifyTestStatus": "\"1\" / \"2\" / \"3\"", "DMS:StatusCorrection:IgnoreFlags": "[\"HIL\",\"LIPX\"]", "DMS:SampleCleanup:TriggerRules": "[{\"TestName\":\"RMSMP\",\"TimeoutMinutes\":1}]", "DMS:SampleCleanup:SendCancelMessageToAptio": "true / false", "DMS:PitStop:TimeoutMinutes": "1", "WebAdmin:BindIp": "\"0.0.0.0\"", "WebAdmin:Port": "5100", "WebAdmin:ErrorRetentionDays": "30", "WebAdmin:HealthCheckIntervalSeconds": "60", "Logging:LogLevel:Default": "\"Information\"", "Logging:LogLevel:UniFlow": "\"Information\"", "Logging:LogLevel:Microsoft.Hosting.Lifetime": "\"Warning\"", "Logging:UniFlowFile:FileLoggingEnabled": "true / false", "Logging:UniFlowFile:LogDirectory": "\"logs\"", "Logging:UniFlowFile:MaxFileSizeMb": "10", "Logging:UniFlowFile:RetentionDays": "30", "Logging:UniFlowFile:LogLevel": "\"Information\""};
const TYPE_CN = { text:'文本', number:'数字', switch:'开关', select:'下拉', tags:'标签列表', rows:'表格行', weekdays:'星期勾选', password:'密码' };
function loadConfigFieldsTable() {
  const tbody = document.getElementById('cfgFieldsBody');
  if (!tbody) return;
  tbody.innerHTML = FORM_FIELDS.map(f => {
    const lvl = f.restart === 'process' ? '<span style="color:var(--yellow)">需操作系统级重启进程</span>'
              : f.restart ? '<span style="color:var(--blue)">点「重启服务」生效</span>' : '<span style="color:var(--green)">保存即生效</span>';
    const ex = EXAMPLES[f.path] || '';
    return `<tr>
      <td>${f.section}</td>
      <td class="api-ep">${f.path}</td>
      <td>${f.label}${f.restart ? (f.restart==='process'?' <span class="needs-restart" style="color:var(--yellow)">?</span>':' <span class="needs-restart">ⓘ</span>') : ''}</td>
      <td>${TYPE_CN[f.type]||f.type}</td>
      <td>${lvl}</td>
      <td class="api-example">${ex}</td>
      <td class="api-example">${f.hint ? f.hint : ''}</td>
    </tr>`;
  }).join('');
}

async function loadApiDocs() {
  const tbody = document.getElementById('apiBody');
  if (!tbody) return;
  const colors = { GET:'var(--blue)', PUT:'var(--yellow)', POST:'var(--green)', DELETE:'var(--red)' };
  tbody.innerHTML = API_DOCS.map((d, i) => {
    const hasResp = d.resp && d.resp.length;
    return `<tr>
      <td><span class="api-method" style="background:${colors[d.m]||'var(--text2)'}">${d.m}</span></td>
      <td class="api-ep">${d.ep}</td>
      <td>${d.desc}</td>
      <td class="api-example">${d.ex}</td>
      <td class="api-resp-cell">${hasResp
        ? `<span class="api-resp-toggle" data-i="${i}">示例 ▸</span><pre class="api-resp" id="resp_${i}" style="display:none">${escapeHtml(d.resp)}</pre>`
        : '—'}</td>
    </tr>`;
  }).join('');
  // 折叠/展开事件
  tbody.querySelectorAll('.api-resp-toggle').forEach(t => {
    t.onclick = () => {
      const pre = document.getElementById('resp_' + t.dataset.i);
      const open = pre.style.display !== 'none';
      pre.style.display = open ? 'none' : 'block';
      t.textContent = open ? '示例 ▸' : '示例 ▾';
    };
  });
  loadConfigFieldsTable();
}

function refreshAll() {
  loadHealth(); loadHealthHistory(); loadErrors(); loadLogFiles(); loadConfig(); loadForms(); loadApiDocs();
}

// ============================================================
// 功能配置（表单化）
// ============================================================
// 字段元数据：Features.* 之外的全部设置（WorkListCleaners 数组除外，见页内说明）
const FORM_FIELDS = [
  // —— Aptio 连接 ——
  { section:'Aptio 连接', path:'Aptio:Ip', label:'Aptio 地址', type:'text', required:true, restart:'inner', hint:'Aptio/Dream 服务器 IP 地址' },
  { section:'Aptio 连接', path:'Aptio:Port', label:'Aptio 端口', type:'number', min:1, max:65535, required:true, restart:'inner', hint:'Aptio GUI 通信端口，默认 2055' },
  { section:'Aptio 连接', path:'Aptio:SrmNodeIds', label:'SRM 节点号', type:'tags', hint:'批量丢弃/导出所监控的 SRM 存储模块节点号，逗号分隔，如 09,13' },
  { section:'Aptio 连接', path:'Aptio:Database:Host', label:'MySQL 地址', type:'text', required:true, restart:'inner', hint:'FlexLab 样本库（flexlab）所在数据库服务器' },
  { section:'Aptio 连接', path:'Aptio:Database:Port', label:'MySQL 端口', type:'number', min:1, max:65535, required:true, restart:'inner', hint:'MySQL 端口，默认 3306' },
  { section:'Aptio 连接', path:'Aptio:Database:User', label:'MySQL 用户', type:'text', restart:'inner', hint:'FlexLab 数据库账号' },
  { section:'Aptio 连接', path:'Aptio:Database:Password', label:'MySQL 密码', type:'password', restart:'inner', hint:'留空表示保持当前密码不变' },
  { section:'Aptio 连接', path:'Aptio:Database:Database', label:'数据库名', type:'text', required:true, restart:'inner', hint:'FlexLab 库名，通常为 flexlab' },
  // —— 批量丢弃（SRM 容量阈值状态机）——
  { section:'SRM 样本批量丢弃', path:'Aptio:DisposeSample:CommandType', label:'命令类型', type:'select', asNumber:true, options:[['0','0=视图'],[1,'1=存储过程']], hint:'读取"待丢弃样本"的方式：0=执行 SQL 视图，1=调用存储过程' },
  { section:'SRM 样本批量丢弃', path:'Aptio:DisposeSample:CommandName', label:'视图/存储过程名', type:'text', hint:'返回候选丢弃样本清单的视图/存储过程名称' },
  { section:'SRM 样本批量丢弃', path:'Aptio:DisposeSample:RunDays', label:'运行日期', type:'weekdays', legacyClear:'Aptio:DisposeSample:DiscardRunDate', hint:'勾选星期几执行批量丢弃；全部取消 = 功能不运行' },
  { section:'SRM 样本批量丢弃', path:'Aptio:DisposeSample:TimeRanges', label:'时间段与阈值', type:'rows', restart:false,
    columns:[{key:'Start',label:'开始',time:true,required:true},{key:'End',label:'结束',time:true,required:true},{key:'Threshold',label:'阈值',num:true,min:0}],
    legacy:{path:'Aptio:DisposeSample:DiscardTimeRange', parse:'timeRanges'}, legacyClear:'Aptio:DisposeSample:DiscardTimeRange',
    hint:'时间段内 SRM 样本总数超过阈值才开始丢弃；支持多段（如早间段+夜间段）；支持跨零点（如 18:00-07:00）；清空=不运行' },
  { section:'SRM 样本批量丢弃', path:'Aptio:DisposeSample:LoopIntervalSeconds', label:'循环间隔(秒)', type:'number', min:1, hint:'丢弃状态机（None→Ready→Ack→Dispose）的轮询间隔' },
  { section:'SRM 样本批量丢弃', path:'Aptio:DisposeSample:MaxWaitDiscardCount', label:'最大等待确认次数', type:'number', min:0, hint:'发送丢弃命令后等待 Aptio 确认的最大轮数，超时则放弃等待' },
  { section:'SRM 样本批量丢弃', path:'Aptio:DisposeSample:MaxOnetimeSelectDiscardCount', label:'单次最大丢弃数', type:'number', min:1, hint:'每轮最多读取并插入丢弃任务的样本数' },
  { section:'SRM 样本批量丢弃', path:'Aptio:DisposeSample:AllowedSrmErrorCodes', label:'允许的 SRM 错误码', type:'rows', flat:true, legacy:{path:'Aptio:DisposeSample:AllowSrmErrorCode', parse:'csv'}, legacyClear:'Aptio:DisposeSample:AllowSrmErrorCode',
    columns:[{key:'Code',label:'错误码',required:true}], hint:'节点带这些错误码时仍视为正常可丢弃；每行一个' },
  { section:'SRM 样本批量丢弃', path:'Aptio:DisposeSample:SkipOnUnknownNode', label:'未知节点跳过', type:'switch', hint:'样本所在节点不在 SRM 节点号清单中：勾选=跳过不丢弃，取消=仍尝试丢弃' },
  // —— SRM 导出 ——
  { section:'SRM 丢弃记录导出', path:'Aptio:SrmExport:ExportTime', label:'每日导出时间', type:'text', format:/^\d{2}:\d{2}$/, hint:'每天该时刻导出 SRM 样本清单，格式 HH:mm' },
  { section:'SRM 丢弃记录导出', path:'Aptio:SrmExport:LoopIntervalSeconds', label:'检查间隔(秒)', type:'number', min:1, hint:'检查是否到达导出时间的轮询间隔' },
  { section:'SRM 丢弃记录导出', path:'Aptio:SrmExport:LogRetentionDays', label:'文件保留天数', type:'number', min:1, hint:'导出文件超过该天数自动清理' },
  { section:'SRM 丢弃记录导出', path:'Aptio:SrmExport:OutputPath', label:'输出目录', type:'text', restart:'inner', hint:'相对安装目录（如 DisposeFile）或绝对路径（如 D:\\exports）' },
  // —— 测试触发（共享扫描器驱动）——
  { section:'测试触发', path:'Aptio:Delivery:TestName', label:'Delivery 触发测试名', type:'text', restart:'inner', hint:'该测试待处理时向 Aptio 发送 DELIVER 命令递送样本（完全匹配 test 第 5 段）' },
  { section:'测试触发', path:'Aptio:Priority:TestName', label:'Priority 触发测试名', type:'text', restart:'inner', hint:'该测试待处理时发送 S010 将样本升级为 STAT 优先' },
  { section:'测试触发', path:'Aptio:TestNameDispose:DisposeTestName', label:'Dispose 触发测试名', type:'text', restart:'inner', hint:'该测试待处理时向 Aptio 发送 TRASH 丢弃样本（管道侧动作）' },
  { section:'测试触发', path:'Aptio:DeliveryFile:DeliveryListFilePath', label:'Delivery 清单目录', type:'text', restart:'inner', hint:'独立功能：读取该目录下清单文件逐行递送样本，完成后移入 Success/Failed 子目录' },
  { section:'测试触发', path:'Aptio:DeliveryFile:LoopIntervalSeconds', label:'文件检查间隔(秒)', type:'number', min:1, restart:'inner', hint:'扫描清单目录的间隔' },
  { section:'测试触发', path:'Aptio:BatchScan:LoopIntervalSeconds', label:'扫描间隔(秒)', type:'number', min:5, restart:'inner', hint:'共享数据库扫描周期（生产者），扫描结果分发给上面三个测试触发功能' },
  { section:'测试触发', path:'Aptio:BatchScan:MaxOnetimeScanCount', label:'单轮扫描上限', type:'number', min:1, restart:'inner', hint:'单轮最多推送的新任务数，超出下轮继续（防任务风暴）' },
  // —— DMS 连接 ——
  { section:'DMS 连接', path:'DMS:DbHost', label:'DMS 地址', type:'text', required:true, restart:'inner', hint:'DMS 数据库（DMSCN）服务器地址' },
  { section:'DMS 连接', path:'DMS:DbPort', label:'DMS 端口', type:'number', min:1, max:65535, required:true, restart:'inner', hint:'DMS 数据库端口' },
  { section:'DMS 连接', path:'DMS:DbUser', label:'DMS 用户', type:'text', restart:'inner', hint:'DMS 数据库账号' },
  { section:'DMS 连接', path:'DMS:DbPassword', label:'DMS 密码', type:'password', restart:'inner', hint:'留空表示保持当前密码不变' },
  { section:'DMS 连接', path:'DMS:DbName', label:'DMS 库名', type:'text', required:true, restart:'inner', hint:'DMS 库名，通常 DMSCN' },
  { section:'DMS 连接', path:'DMS:LoopIntervalSeconds', label:'循环间隔(秒)', type:'number', min:1, restart:'inner', hint:'DMS 各功能共用的轮询间隔' },
  // —— DMS 功能参数 ——
  { section:'DMS 功能参数', path:'DMS:StatusCorrection:AutoModifyTestStatus', label:'修正模式', type:'select', restart:'inner', options:[['1','1=不改变状态（Do Not Change The Status）'],['2','2=Host 标志置 0 并重发结果（Change Hostflag status to 0, resent the result）'],['3','3=测试状态 Val→F（Change Test Status From Val To F）']], hint:'reqtest 状态与 reqtestresult 状态不一致时的处理策略' },
  { section:'DMS 功能参数', path:'DMS:StatusCorrection:IgnoreFlags', label:'忽略标志', type:'rows', restart:'inner', flat:true, legacy:{path:'DMS:StatusCorrection:IgnoreFlagList', parse:'csv'}, legacyClear:'DMS:StatusCorrection:IgnoreFlagList',
    columns:[{key:'Flag',label:'标志名',required:true}], hint:'仪器结果报告这些标志（jsnflaginstrument）时自动置 V 并清 flgtohost；每行一个' },
  { section:'DMS 功能参数', path:'DMS:SampleCleanup:TriggerRules', label:'DMS 工单删除规则', type:'rows', restart:'inner',
    columns:[{key:'TestName',label:'测试名',required:true},{key:'TimeoutMinutes',label:'超时(分钟)',num:true,min:0}],
    legacy:{path:'DMS:SampleCleanup:TestTriggerSampleDeletion', parse:'triggerRules'}, legacyClear:'DMS:SampleCleanup:TestTriggerSampleDeletion',
    hint:'该测试在 DMS 超时后删除其工单记录（DMS 库删库，与管道侧的 Dispose 丢弃相互独立）；超时 0=到期即删' },
  { section:'DMS 功能参数', path:'DMS:SampleCleanup:SendCancelMessageToAptio', label:'删除后发取消到 Aptio', type:'switch', restart:'inner', hint:'删除工单后向 Aptio 发送 ORDER…C 取消整个样本的待处理测试' },
  { section:'DMS 功能参数', path:'DMS:PitStop:TimeoutMinutes', label:'PitStop 超时(分钟)', type:'number', min:1, restart:'inner', hint:'启用 PitStop 监控（仪表盘卡片开关）后：运行中任务超过该时长仍未完成即自动清理' },
  // —— Web 管理界面 ——
  { section:'Web 管理界面', path:'WebAdmin:BindIp', label:'绑定地址', type:'text', restart:'process', hint:'0.0.0.0=所有网卡，127.0.0.1=仅本机' },
  { section:'Web 管理界面', path:'WebAdmin:Port', label:'端口', type:'number', min:1, max:65535, restart:'process', hint:'Web 界面端口' },
  { section:'Web 管理界面', path:'WebAdmin:ErrorRetentionDays', label:'错误保留天数', type:'number', min:1, restart:'process', hint:'管理库（健康+错误）记录保留天数' },
  { section:'Web 管理界面', path:'WebAdmin:HealthCheckIntervalSeconds', label:'健康检查间隔(秒)', type:'number', min:5, restart:'process', hint:'System 徽标与清理任务的心跳周期' },
  // —— 日志 ——
  { section:'日志', path:'Logging:LogLevel:Default', label:'默认日志级别', type:'select', options:[['Trace','Trace'],['Debug','Debug'],['Information','Information'],['Warning','Warning'],['Error','Error']], hint:'全局默认日志级别，保存后热生效' },
  { section:'日志', path:'Logging:LogLevel:UniFlow', label:'UniFlow 日志级别', type:'select', options:[['Trace','Trace'],['Debug','Debug'],['Information','Information'],['Warning','Warning'],['Error','Error']], hint:'业务模块日志级别，保存后热生效' },
  { section:'日志', path:'Logging:LogLevel:Microsoft.Hosting.Lifetime', label:'框架日志级别', type:'select', options:[['Trace','Trace'],['Debug','Debug'],['Information','Information'],['Warning','Warning'],['Error','Error']], hint:'框架生命周期日志级别，保存后热生效' },
  { section:'日志', path:'Logging:UniFlowFile:FileLoggingEnabled', label:'启用文件日志', type:'switch', restart:'process', hint:'关闭后日志仅输出控制台（journal）' },
  { section:'日志', path:'Logging:UniFlowFile:LogDirectory', label:'日志目录', type:'text', restart:'process', hint:'相对安装目录，如 logs' },
  { section:'日志', path:'Logging:UniFlowFile:MaxFileSizeMb', label:'单文件上限(MB)', type:'number', min:1, restart:'process', hint:'超过则滚动新文件（_0、_1…）' },
  { section:'日志', path:'Logging:UniFlowFile:RetentionDays', label:'保留天数', type:'number', min:1, restart:'process', hint:'超期日志目录自动清理' },
  { section:'日志', path:'Logging:UniFlowFile:LogLevel', label:'文件日志级别', type:'select', restart:'process', options:[['Trace','Trace'],['Debug','Debug'],['Information','Information'],['Warning','Warning'],['Error','Error']], hint:'写入日志文件的最低级别' },
];

function getValByPath(obj, path) {
  let cur = obj;
  for (const p of path.split(':')) {
    if (cur == null || typeof cur !== 'object') return undefined;
    cur = cur[p];
  }
  return cur;
}

// 复合值初始数据（新 JSON 格式优先，旧格式宽容解析）
function parseLegacyTimeRanges(s) {
  const rows = [];
  (s || '').split(';').forEach(item => {
    if (!item.trim()) return;
    const parts = item.split(',');
    if (parts.length < 2) return;
    const tp = parts[0].split('-');
    if (tp.length < 2) return;
    const th = parseInt(parts[1].trim(), 10);
    rows.push({ Start: tp[0].trim(), End: tp[1].trim(), Threshold: isNaN(th) ? 0 : th });
  });
  return rows;
}

function parseLegacyRules(s) {
  const rows = [];
  (s || '').split(';').forEach(item => {
    const parts = item.split(':');
    if (parts.length < 2) return;
    const min = parseInt(parts[1].trim(), 10);
    if (parts[0].trim() && !isNaN(min) && min >= 0)
      rows.push({ TestName: parts[0].trim(), TimeoutMinutes: min });
  });
  return rows;
}

// 日志级别名称集合：用于识别「日志级别下拉」字段（其 option 值全是级别名），
// 避免把 CommandType(0/1)、AutoModifyTestStatus(1/2/3) 等数值下拉也误判为日志级别
const LOG_LEVEL_NAMES = new Set(['Trace', 'Debug', 'Information', 'Warning', 'Error', 'Critical', 'None']);
function isLogLevelSelect(field) {
  const opts = field.options || [];
  return opts.length > 0 && opts.every(([v]) => LOG_LEVEL_NAMES.has(String(v)));
}
// .NET 数字日志级别 → 名称（0=Trace…5=Critical）
function numLogLevelToName(n) {
  return ['Trace', 'Debug', 'Information', 'Warning', 'Error', 'Critical'][n];
}

function getInitialValue(field, cfg) {
  if (field.type === 'rows') {
    const v = getValByPath(cfg, field.path);
    if (Array.isArray(v)) {
      if (!field.flat) return v.map(r => ({ ...r }));  // 空数组=已显式清空，权威
      // flat：元素可能是对象（历史错误保存）或字符串（新扁平格式），统一回显为字符串
      const k = field.columns && field.columns[0] ? field.columns[0].key : 'Value';
      return v.map(x => typeof x === 'object' && x !== null ? String(x[k] ?? '') : String(x)).filter(x => x !== '');
    }
    // 旧格式兜底（由字段定义的 legacy 声明驱动）
    const lg = field.legacy;
    if (lg) {
      const raw = String(getValByPath(cfg, lg.path) ?? '');
      if (lg.parse === 'timeRanges') return parseLegacyTimeRanges(raw);
      if (lg.parse === 'triggerRules') return parseLegacyRules(raw);
      if (lg.parse === 'csv' || lg.parse === 'csvSingle') {
        if (field.flat) return raw.split(',').map(x => x.trim()).filter(x => x !== '');
        const key = (field.columns && field.columns[0]) ? field.columns[0].key : 'Value';
        return raw.split(',').map(x => x.trim()).filter(x => x !== '').map(v => ({ [key]: v }));
      }
    }
    return [];
  }
  if (field.type === 'tags') {
    const v = getValByPath(cfg, field.path);
    if (Array.isArray(v)) return v.map(x => String(x)).filter(x => x !== '');
    return [];
  }
  if (field.type === 'weekdays') {
    const v = getValByPath(cfg, field.path);
    if (Array.isArray(v)) return v.map(Number).filter(d => d >= 1 && d <= 7);  // 空数组=显式清空，权威
    // 旧格式兜底（DiscardRunDate 逗号串 "1,2,3"）
    const legacy = String(getValByPath(cfg, 'Aptio.DisposeSample.DiscardRunDate') ?? '');
    return legacy.split(',').map(x => parseInt(x.trim(), 10)).filter(d => !isNaN(d) && d >= 1 && d <= 7);
  }
  let v = getValByPath(cfg, field.path);
  if (v === undefined || v === null) {
    if (field.type === 'switch') return false;
    return '';
  }
  // select 类型：存量配置里存的是 .NET 数字日志级别（如 1）时，规范化为名称，
  // 否则下拉无匹配项 → 保存会静默改写为首选项（如 "Trace"）。
  // 仅对「日志级别下拉」生效（isLogLevelSelect），避免误伤 CommandType/AutoModifyTestStatus 等数值下拉。
  if (field.type === 'select' && isLogLevelSelect(field) && typeof v === 'number' && v >= 0 && v <= 5)
    return numLogLevelToName(v);
  return v;
}

async function loadForms() {
  const cfg = await api('/config');
  if (!cfg) return;
  _lastCfg = cfg;
  renderForms(cfg);
}

function renderForms(cfg) {
  const vEl = document.getElementById('configVersion');
  if (vEl) vEl.textContent = cfg && cfg.ConfigVersion ? ('配置版本 v' + cfg.ConfigVersion) : '';
  const container = document.getElementById('formsBody');
  container.innerHTML = '';
  // 分组（按首次出现顺序）
  const sections = [];
  FORM_FIELDS.forEach(f => { if (!sections.includes(f.section)) sections.push(f.section); });

  sections.forEach(sec => {
    const box = document.createElement('div');
    box.className = 'form-section';
    box.innerHTML = `<h3>${escapeHtml(sec)}</h3>`;
    FORM_FIELDS.filter(f => f.section === sec).forEach(f => {
      const initVal = getInitialValue(f, cfg);
      _origValues[f.path] = initVal;
      const row = document.createElement('div');
      row.className = 'form-row';
      // 重启标注三级：process=需操作系统级重启进程（橙色 ?）；inner=保存后点「重启服务」生效（ⓘ）
      let mark = '';
      if (f.restart) {
        mark = f.restart === 'process'
          ? '<span class="needs-restart" title="需操作系统级重启服务进程">?</span>'
          : '<span class="needs-restart" title="保存后点「重启服务」生效">ⓘ</span>';
      }
      const tip = f.hint ? `<span class="tip-icon">?</span>` : '';
      row.innerHTML = `<label data-path="${f.path}"><span class="lbl-text">${escapeHtml(f.label)}</span>${mark}${tip}</label>`;
      // data-tip 用 setAttribute 设置（escapeHtml 不转义双引号，避免 hint 含引号时破坏属性）
      row.querySelector('.tip-icon')?.setAttribute('data-tip',
        f.hint + (f.restart ? '（重启说明见 ⓘ）' : '') + (EXAMPLES[f.path] ? '\nAPI: ' + EXAMPLES[f.path] : ''));
      const ctrl = renderControl(f, initVal);
      row.appendChild(ctrl);
      // 修改标记：值变更时 label 加粗显示，保存后清除
      const onEdit = () => markModified(f, ctrl);
      ctrl.addEventListener('change', onEdit);
      ctrl.addEventListener('input', onEdit);
      box.appendChild(row);
    });
    container.appendChild(box);
  });
  const note = document.createElement('div');
  note.className = 'form-note';
  note.textContent = '说明：功能启停开关在「仪表盘」卡片；字段名旁的 ? 悬停 0.5 秒或点击查看提示；粗体字段名=有未保存的修改。';
  container.appendChild(note);

  bindTips(container);
}

// ============ 未保存修改标记 ============
const _origValues = {};

// ============ Tips（悬停 500ms 或点击弹出） ============
let _tipBox = null, _tipTimer = null;
function showTip(icon) {
  hideTip();
  _tipBox = document.createElement('div');
  _tipBox.className = 'tip-box';
  _tipBox.textContent = icon.dataset.tip || '';
  document.body.appendChild(_tipBox);
  const r = icon.getBoundingClientRect();
  const boxH = _tipBox.offsetHeight;
  const w = Math.min(_tipBox.offsetWidth, 340);
  let left = r.left + r.width / 2 - w / 2;
  left = Math.max(8, Math.min(left, window.innerWidth - w - 8));
  _tipBox.style.left = left + 'px';
  // 下方放不下（超出视口）则翻到图标上方
  _tipBox.style.top = (r.bottom + 6 + boxH > window.innerHeight) ? (r.top - boxH - 6) + 'px' : (r.bottom + 6) + 'px';
  _tipBox.classList.add('visible');
  _tipBox.dataset.src = icon.dataset.tip;   // 记录来源（供 click 判断 toggle）
}
function hideTip() {
  if (_tipTimer) { clearTimeout(_tipTimer); _tipTimer = null; }
  if (_tipBox) { _tipBox.remove(); _tipBox = null; }
}
function bindTips(container) {
  container.querySelectorAll('.tip-icon').forEach(ic => {
    ic.addEventListener('mouseenter', () => { _tipTimer = setTimeout(() => showTip(ic), 500); });
    ic.addEventListener('mouseleave', () => hideTip());
    ic.addEventListener('click', e => {
      e.stopPropagation();
      const open = _tipBox && _tipBox.dataset.src === ic.dataset.tip;
      hideTip();
      if (!open) showTip(ic);
    });
  });
}
document.addEventListener('click', e => {
  if (_tipBox && !e.target.closest('.tip-icon') && !e.target.closest('.tip-box')) hideTip();
});

function renderControl(field, value) {
  const id = 'f_' + field.path.replace(/[.:\-]/g, '_');
  if (field.type === 'switch') {
    const wrap = document.createElement('label');
    wrap.className = 'switch-wrap';
    wrap.innerHTML = `<input type="checkbox" id="${id}" ${value ? 'checked' : ''}><span class="switch-ui"></span>`;
    return wrap;
  }
  if (field.type === 'select') {
    const sel = document.createElement('select');
    sel.id = id; sel.className = 'form-input';
    let matched = false;
    (field.options || []).forEach(([v, t]) => {
      const opt = document.createElement('option');
      opt.value = v; opt.textContent = t;
      if (String(value) === String(v)) { opt.selected = true; matched = true; }
      sel.appendChild(opt);
    });
    // 当前值不在预设选项中（如未规范化的数值级别 7、或 None/Critical 等）→ 动态插入并选中，
    // 防止浏览器默认选中首项导致保存时静默改写原始值
    if (!matched && value !== '' && value !== null && value !== undefined) {
      const opt = document.createElement('option');
      opt.value = String(value);
      opt.textContent = String(value) + '（数值级）';
      opt.selected = true;
      sel.appendChild(opt);
    }
    return sel;
  }
  if (field.type === 'tags') {
    const inp = document.createElement('input');
    inp.id = id; inp.className = 'form-input';
    inp.value = (value || []).join(',');
    inp.placeholder = '逗号分隔';
    return inp;
  }
  if (field.type === 'weekdays') {
    const wrap = document.createElement('div');
    wrap.className = 'weekdays-editor';
    wrap.id = id;
    const days = [['1','一'],['2','二'],['3','三'],['4','四'],['5','五'],['6','六'],['7','日']];
    days.forEach(([d, name]) => {
      const lb = document.createElement('label');
      const on = (value||[]).map(String).includes(d);
      lb.className = 'weekday-item' + (on ? ' on' : '');
      lb.innerHTML = `<input type="checkbox" value="${d}" ${on ? 'checked' : ''}><span>${name}</span>`;
      // 兼容不支持 :has() 的旧浏览器：用 class 切换选中态
      lb.querySelector('input').addEventListener('change', e => lb.classList.toggle('on', e.target.checked));
      wrap.appendChild(lb);
    });
    return wrap;
  }
  if (field.type === 'rows') {
    const wrap = document.createElement('div');
    wrap.className = 'rows-editor';
    wrap.id = id;
    const table = document.createElement('table');
    table.className = 'rows-table';
    const cols = field.columns || [];
    const head = table.insertRow();
    cols.forEach(c => { const th = document.createElement('th'); th.textContent = c.label; head.appendChild(th); });
    const thOp = document.createElement('th'); thOp.textContent = ''; head.appendChild(thOp);
    (value || []).forEach(r => {
      // flat 字段的初始值是字符串（readControlValue 扁平化输出），包装回行对象供编辑器渲染
      const rowData = (field.flat && (typeof r !== 'object' || r === null)) ? { [cols[0].key]: r } : r;
      table.appendChild(makeRowEditor(cols, rowData));
    });
    wrap.appendChild(table);
    const add = document.createElement('button');
    add.type = 'button'; add.className = 'btn'; add.textContent = '+ 添加行';
    add.onclick = () => table.appendChild(makeRowEditor(cols, {}));
    wrap.appendChild(add);
    return wrap;
  }
  // text / number / password
  const inp = document.createElement('input');
  inp.id = id; inp.className = 'form-input';
  inp.type = field.type === 'password' ? 'password' : (field.type === 'number' ? 'number' : 'text');
  if (field.type === 'number') { inp.min = field.min ?? ''; inp.max = field.max ?? ''; }
  if (field.type === 'password') {
    inp.value = ''; inp.placeholder = value && value !== '' ? '已设置（留空保持不变）' : '';
    inp.dataset.masked = value || '';
  } else {
    inp.value = value ?? '';
  }
  return inp;
}

// 从 DOM 读取某字段的当前值（统一入口）
function readControlValue(f, el) {
  if (f.type === 'weekdays')
    return Array.from(el.querySelectorAll('input:checked')).map(i => parseInt(i.value, 10)).sort((a,b)=>a-b);
  if (f.type === 'rows') {
    const rows = [];
    el.querySelectorAll('tr').forEach(tr => {
      if (!tr.querySelector('input[data-key]')) return;
      const row = {};
      tr.querySelectorAll('input[data-key]').forEach(inp => {
        const k = inp.dataset.key;
        // number 格留空兜底为 0（避免 NaN→null 落盘导致后端整段解析失败）
        if (inp.type === 'number') { const n = parseInt(inp.value, 10); row[k] = isNaN(n) ? 0 : n; }
        else row[k] = inp.value.trim();
      });
      rows.push(row);
    });
    if (f.flat && f.columns && f.columns[0]) {
      const k = f.columns[0].key;
      return rows.map(r => String(r[k] ?? '')).filter(x => x !== '');
    }
    return rows;
  }
  if (f.type === 'switch') return el.checked;
  if (f.type === 'tags') return el.value.split(',').map(x => x.trim()).filter(x => x !== '');
  if (f.type === 'password') return el.value;
  if (f.type === 'number') return el.value === '' ? '' : parseInt(el.value, 10);
  return el.value.trim();
}

// 全局待重启横幅（各页签顶部共享显示；点击可直接跳转重启）
function renderRestartBanner() {
  let banner = document.getElementById('restartBanner');
  if (_pendingRestartCount > 0) {
    if (!banner) {
      banner = document.createElement('div');
      banner.id = 'restartBanner';
      banner.className = 'restart-banner';
      banner.onclick = function() {
        if (confirm('检测到 ' + _pendingRestartCount + ' 个功能等待生效。立即重启服务？')) restartService();
      };
      const main = document.querySelector('main');
      main.parentNode.insertBefore(banner, main);
    }
    banner.innerHTML = '⚠ 有 <b>' + _pendingRestartCount + '</b> 个功能已开启但尚未生效 — <u>点击此处重启服务</u>';
    banner.style.display = '';
  } else if (banner) {
    banner.style.display = 'none';
  }
}

// 修改标记：与初始值比较，不同则 label 加粗（未保存状态）
function markModified(f, el) {
  const cur = readControlValue(f, el);
  const orig = _origValues[f.path];
  const dirty = JSON.stringify(cur) !== JSON.stringify(orig);
  const lbl = document.querySelector(`label[data-path="${f.path}"]`);
  if (lbl) lbl.classList.toggle('modified', dirty);
}

function makeRowEditor(cols, rowData) {
  const tr = document.createElement('tr');
  cols.forEach(c => {
    const td = document.createElement('td');
    const inp = document.createElement('input');
    inp.className = 'form-input';
    if (c.time) { inp.type = 'time'; }
    if (c.num) { inp.type = 'number'; inp.min = 0; }
    inp.value = rowData[c.key] ?? '';
    inp.dataset.key = c.key;
    td.appendChild(inp);
    tr.appendChild(td);
  });
  const tdOp = document.createElement('td');
  const del = document.createElement('button');
  del.type = 'button'; del.className = 'btn-icon'; del.textContent = '✕';
  del.onclick = () => tr.remove();
  tdOp.appendChild(del);
  tr.appendChild(tdOp);
  return tr;
}

// 收集 + 校验；返回 {changes:[{path,value}], errors:[msg]}
function collectForms(cfg) {
  const changes = [], errors = [];
  FORM_FIELDS.forEach(f => {
    const id = 'f_' + f.path.replace(/[.:\-]/g, '_');
    const el = document.getElementById(id);
    if (!el) return;
    let value = readControlValue(f, el);   // let：select 数字化等分支会转换后重新赋值
    const orig = _origValues[f.path] ?? getInitialValue(f, cfg);

    if (f.type === 'password') {
      if (value === '') return;                 // 留空=保持
      changes.push({ path: f.path, value });
      return;
    }
    if (f.type === 'switch') {
      if (value === orig) return;
      changes.push({ path: f.path, value });
      return;
    }
    if (f.type === 'tags' || f.type === 'weekdays' || f.type === 'rows') {
      if (f.type === 'rows') {
        const cols = f.columns || [];
        if (f.flat) {
          // flat 字段：行值为字符串（readControlValue 已扁平化）
          const c = cols[0];
          value.forEach((v, i) => {
            if (c && c.required && (v === undefined || v === ''))
              errors.push(`「${f.label}」第 ${i+1} 行：「${c.label}」不能为空`);
          });
        } else {
          value.forEach((r, i) => {
            cols.forEach(c => {
              const v = r[c.key];
              // number 格读值已兜底 0（留空=0），此处只需保留必填与 min 比较
              if (c.required && (v === undefined || v === ''))
                errors.push(`「${f.label}」第 ${i+1} 行：「${c.label}」不能为空`);
              if (c.num && c.min !== undefined && v < c.min)
                errors.push(`「${f.label}」第 ${i+1} 行：「${c.label}」需为 ≥${c.min} 的整数`);
            });
          });
        }
      }
      if (JSON.stringify(value) === JSON.stringify(orig)) return;
      changes.push({ path: f.path, value });
      return;
    }
    // text / number / select
    if (f.type === 'select' && f.asNumber) {
      const n = parseInt(value, 10);
      if (isNaN(n)) { errors.push(`「${f.label}」无效`); return; }
      value = n;
      if (String(value) === String(orig)) return;
      changes.push({ path: f.path, value });
      return;
    }
    if (f.type === 'number') {
      if (value === '') {
        if (f.required) { errors.push(`「${f.label}」不能为空`); el.classList.add('input-error'); }
        return;
      }
      if (isNaN(value) || (f.min !== undefined && value < f.min) || (f.max !== undefined && value > f.max)) {
        errors.push(`「${f.label}」需为 ${f.min ?? 0} ~ ${f.max ?? '∞'} 的整数`);
        el.classList.add('input-error');
        return;
      }
    } else {
      if (f.required && value === '') { errors.push(`「${f.label}」不能为空`); el.classList.add('input-error'); return; }
      if (f.format && value !== '' && !f.format.test(value)) { errors.push(`「${f.label}」格式应为 HH:mm`); el.classList.add('input-error'); return; }
    }
    if (String(value) === String(orig)) return;
    changes.push({ path: f.path, value });
  });
    return { changes, errors };
}

// 缓存最近一次 /config 结果，供校验比对
let _lastCfg = null;
async function awaitCfg() {
  if (_lastCfg) return _lastCfg;
  _lastCfg = await api('/config') || {};
  return _lastCfg;
}

async function saveAllForms() {
  const cfg = await awaitCfg();
  document.querySelectorAll('.input-error').forEach(e => e.classList.remove('input-error'));
  const { changes, errors } = collectForms(cfg);
  const hint = document.getElementById('formsHint');
  if (errors.length > 0) {
    hint.textContent = '❌ ' + errors[0] + (errors.length > 1 ? `（共 ${errors.length} 处错误）` : '');
    hint.style.color = 'var(--red, #d9534f)';
    return;
  }
  if (changes.length === 0) { hint.textContent = '没有需要保存的修改'; hint.style.color = ''; return; }

  let ok = 0, fail = 0;
  const needRestart = new Set();   // inner：点「重启服务」生效
  const needProcess = new Set();   // process：需操作系统级重启进程
  const legacyCleared = new Set();
  let legacyClearFailed = false;
  for (const c of changes) {
    const field = FORM_FIELDS.find(f => f.path === c.path);
    // 生效方式以后端响应为准（模型 [Restart] 特性即真相），前端标注兜底
    let level = field && field.restart ? field.restart : null;
    const res = await api('/config', { method:'PUT', headers:{'Content-Type':'application/json'}, body: JSON.stringify({ path: c.path, value: c.value }) });
    if (res && res.success !== false && res.restart && res.restart.level) level = res.restart.level.toLowerCase().startsWith('hot') ? null : (res.restart.level.toLowerCase().includes('process') ? 'process' : 'inner');
    if (level === 'process') needProcess.add(field ? field.label : c.path);
    else if (level === 'inner') needRestart.add(field ? field.label : c.path);
    res && res.success !== false ? ok++ : fail++;
    // 迁移语义：写入 JSON 新格式后，同步清空旧格式字段
    //（防重启时 ConfigurationBinder 把空数组绑成 null 后旧串复活）
    if (res && res.success !== false && field && field.legacyClear && !legacyCleared.has(field.legacyClear)) {
      legacyCleared.add(field.legacyClear);
      const clr = await api('/config', { method:'PUT', headers:{'Content-Type':'application/json'}, body: JSON.stringify({ path: field.legacyClear, value: '' }) });
      if (!clr || clr.success === false) legacyClearFailed = true;
    }
  }
  _lastCfg = null;
  if (fail === 0) {
    Object.keys(_origValues).forEach(p => {
      const lbl = document.querySelector(`label[data-path="${p}"]`);
      if (lbl) lbl.classList.remove('modified');
    });
    // 用保存后的真实配置刷新 orig 基线
    const fresh = await api('/config');
    if (fresh) {
      _lastCfg = fresh;
      FORM_FIELDS.forEach(f => { _origValues[f.path] = getInitialValue(f, fresh); });
    }
  }
  let msg = `✅ 已保存 ${ok} 项` + (fail ? `，失败 ${fail} 项` : '');
  if (needProcess.size > 0) msg += `；⚠ 以下字段需操作系统级重启进程才能生效：${[...needProcess].join('、')}`;
  if (needRestart.size > 0) msg += `；含需重启项（${[...needRestart].join('、')}），点「重启服务」生效`;
  if (legacyClearFailed) msg += '；⚠ 旧格式字段清空失败，重启后可能回退';
  hint.textContent = msg;
  hint.style.color = fail ? 'var(--red, #d9534f)' : '';
  loadConfig();
}

function downloadLogBundle() {
  const days = document.getElementById('logDays').value;
  const modSel = document.getElementById('logModule');
  const mod = modSel ? modSel.value : '';
  window.location = `/api/logs/bundle?days=${days}` + (mod ? `&module=${encodeURIComponent(mod)}` : '');
}

function downloadConfig() {
  window.location = '/api/config/download';
}

async function restartService() {
  if (!confirm('确定重启服务？\n\n将按最新配置重建全部业务任务（约 2-5 秒，Web 不中断）。')) return;
  const btn = document.getElementById('restartBtn');
  const hint = document.getElementById('formsHint');
  btn.disabled = true; btn.textContent = '重启中...';
  hint.textContent = '⏳ 重启中，等待恢复...';
  try { await api('/restart', { method: 'POST' }); } catch (e) { /* 期间可能断连 */ }
  // 轮询业务宿主真实状态（/version 是外壳端点，重启期间也响应，不能作为完成依据）
  await new Promise(r => setTimeout(r, 2500));   // 先让重启真正开始（端点有 1.5s 延迟）
  let tries = 0, sawRestarting = false;
  const timer = setInterval(async () => {
    tries++;
    try {
      const st = await fetch('/api/restart/status').then(r => r.json());
      if (st.restarting) sawRestarting = true;
      const done = st.restarting === false && (sawRestarting || tries > 3) && st.ready === true;
      if (done) {
        clearInterval(timer);
        btn.disabled = false; btn.textContent = '重启服务';
        hint.textContent = st.lastFailure ? ('⚠️ 重启失败：' + st.lastFailure) : '✅ 业务任务已重建（Web 未中断）';
        _lastCfg = null;
        loadForms(); loadHealth(); loadConfig();
        return;
      }
    } catch (e) { /* 尚未恢复 */ }
    hint.textContent = `⏳ 重启中...（${tries}s）`;
    if (tries > 60) {
      clearInterval(timer);
      btn.disabled = false; btn.textContent = '重启服务';
      hint.textContent = '⚠️ 重启超时，请手动刷新页面';
    }
  }, 1000);
}

async function restartProcess() {
  if (!confirm('进程级重启：整个服务进程退出并由 systemd/服务管理器自动拉起（约 5 秒中断）。继续？')) return;
  const hint = document.getElementById('formsHint') || null;
  if (hint) hint.textContent = '⏳ 进程退出中，请稍候…';
  try {
    const r = await (await fetch('/api/restart/process', { method: 'POST' })).json();
    if (hint) hint.textContent = '✅ 已请求进程重启（' + (r.hint || '') + '）';
    // 轮询 /api/restart/status 等回来变 ready
    pollRestartBack();
  } catch (e) { if (hint) hint.textContent = '⚠ 已发出进程重启请求（进程退出时连接会断开，属正常现象）'; }
}

async function pollRestartBack(tries = 30) {
  for (let i = 0; i < tries; i++) {
    await new Promise(r => setTimeout(r, 2000));
    try {
      const r = await (await fetch('/api/restart/status')).json();
      if (r.ready) { const hint = document.getElementById('formsHint'); if (hint) hint.textContent = '✅ 服务已恢复（新版已就绪）'; return; }
    } catch (e) { /* 进程断连期间忽略 */ }
  }
}

// ===== Update (online upgrade) =====
async function uploadUpdate(file) {
  if (!file) return;
  if (!confirm(`确认上传升级包 ${file.name}？上传后需要点击「进程重启」生效。`)) return;
  const hint = document.getElementById('formsHint');
  hint.textContent = '⏳ 上传中...';
  try {
    const resp = await fetch('/api/update/upload', { method: 'POST', body: file });
    const r = await resp.json();
    if (!r.staged) { hint.textContent = '❌ ' + (r.error || '上传失败'); hint.style.color = 'var(--red, #d9534f)'; return; }
    hint.textContent = '📦 已暂存 (md5: ' + r.md5.slice(0, 8) + ')。正在应用...';
    const r2 = await (await fetch('/api/update/apply', { method: 'POST' })).json();
    if (r2.applied) {
      hint.textContent = '✅ 升级已就绪 (' + r.md5.slice(0, 8) + ')。点「进程重启」完成升级。';
      hint.style.color = '';
    } else {
      hint.textContent = '❌ 应用失败: ' + (r2.error || 'unknown');
      hint.style.color = 'var(--red, #d9534f)';
    }
  } catch(e) {
    hint.textContent = '❌ 上传失败: ' + e.message;
    hint.style.color = 'var(--red, #d9534f)';
  }
}

// ===== Init =====
document.addEventListener('DOMContentLoaded', () => {
  loadVersion();
  refreshAll();
  setInterval(loadHealth, 30000);
  setInterval(loadHealthHistory, 60000);
});