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
async function loadHealth() {
  const data = await api('/health');
  if (!data) return;
  const badge = document.getElementById('systemStatus');
  badge.textContent = data.status === 'healthy' ? '正常运行' : '异常';
  badge.className = 'status-badge ' + data.status;

  const container = document.getElementById('healthCards');
  container.innerHTML = '';
  const summary = document.createElement('div');
  summary.className = 'card';
  summary.innerHTML = `<div class="label">系统概览</div>
    <div class="value">${data.summary.healthy}/${data.summary.healthy + data.summary.degraded + data.summary.down}</div>
    <div class="meta">健康 · 降级 · 宕机: ${data.summary.healthy} / ${data.summary.degraded} / ${data.summary.down}</div>`;
  container.appendChild(summary);

  for (const [mod, info] of Object.entries(data.modules)) {
    const card = document.createElement('div');
    card.className = 'card ' + (info.status || 'unknown');
    card.innerHTML = `<div class="label">${mod}</div>
      <div class="value">${info.status === 'healthy' ? '✓' : info.status === 'degraded' ? '⚠' : '✗'}</div>
      <div class="meta">${info.timestamp || ''} ${info.message ? '· ' + info.message : ''}</div>`;
    container.appendChild(card);
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
  const data = await api('/config');
  if (!data) return;
  const container = document.getElementById('configTree');
  container.innerHTML = '';
  renderConfigNode(container, data, '');
}

function renderConfigNode(parent, obj, path) {
  for (const [key, value] of Object.entries(obj)) {
    const fullPath = path ? path + ':' + key : key;
    const node = document.createElement('div');
    node.className = 'config-node';
    if (typeof value === 'object' && value !== null && !Array.isArray(value) && !(value instanceof Date)) {
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
      span.textContent = key;
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
      valSpan.className = typeof value === 'string' ? 'config-string' : typeof value === 'number' ? 'config-number' : 'config-bool';
      valSpan.textContent = typeof value === 'string' ? `"${value}"` : String(value);
      valSpan.style.cursor = 'pointer';
      valSpan.onclick = () => startEdit(valSpan, fullPath, value);
      node.appendChild(valSpan);
      parent.appendChild(node);
    }
  }
}

function startEdit(span, path, oldValue) {
  const isString = typeof oldValue === 'string';
  const isBool = typeof oldValue === 'boolean';
  const container = document.createElement('span');
  container.className = 'config-edit';
  if (isBool) {
    container.innerHTML = `<select><option value="true" ${oldValue ? 'selected' : ''}>true</option><option value="false" ${oldValue ? '' : 'selected'}>false</option></select>
      <button class="btn-save" onclick="saveEdit(this,'${path}')">保存</button>
      <button class="btn-cancel" onclick="cancelEdit(this,'${span.textContent}')">取消</button>`;
  } else {
    container.innerHTML = `<input type="text" value="${isString ? oldValue.replace(/"/g, '&quot;') : oldValue}">
      <button class="btn-save" onclick="saveEdit(this,'${path}')">保存</button>
      <button class="btn-cancel" onclick="cancelEdit(this,'${span.textContent}')">取消</button>`;
  }
  span.replaceWith(container);
}

async function saveEdit(btn, path) {
  const container = btn.parentElement;
  const input = container.querySelector('input') || container.querySelector('select');
  let value = input.value;
  // Try to preserve type
  if (value === 'true') value = true;
  else if (value === 'false') value = false;
  else if (!isNaN(value) && value !== '') value = Number(value);
  const result = await api('/config', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path, value })
  });
  loadConfig();
}

function cancelEdit(btn, originalText) {
  const container = btn.parentElement;
  const span = document.createElement('span');
  span.textContent = originalText;
  span.style.cursor = 'pointer';
  container.replaceWith(span);
}

// ===== Log Files =====
async function loadLogFiles() {
  const days = document.getElementById('logDays').value;
  const data = await api(`/logs/list?days=${days}`);
  if (!data) return;
  const container = document.getElementById('logFileList');
  if (data.length === 0) { container.innerHTML = '<div class="loading">暂无日志文件</div>'; return; }
  container.innerHTML = '';
  data.forEach(f => {
    const item = document.createElement('div');
    item.className = 'log-file-item' + (f.name.startsWith('UniFlow_Error') ? ' error-file' : '');
    const size = f.size > 1024 * 1024 ? (f.size / 1024 / 1024).toFixed(1) + 'MB' : (f.size / 1024).toFixed(1) + 'KB';
    item.innerHTML = `<div><div class="name">${f.name}</div><div class="meta">${f.lastModified} · ${size}</div></div><span>查看</span>`;
    item.onclick = () => viewLogFile(f.name);
    container.appendChild(item);
  });
}

async function viewLogFile(name) {
  const data = await api(`/logs/view?file=${encodeURIComponent(name)}&tail=200`);
  if (!data) return;
  document.getElementById('logViewer').style.display = 'block';
  document.getElementById('logViewerTitle').textContent = name + ' (最近200行)';
  document.getElementById('logContent').textContent = data.lines.join('\n');
}

function escapeHtml(s) {
  const div = document.createElement('div');
  div.textContent = s;
  return div.innerHTML;
}

function refreshAll() {
  loadHealth(); loadHealthHistory(); loadErrors(); loadLogFiles(); loadConfig();
}

// ===== Init =====
document.addEventListener('DOMContentLoaded', () => {
  refreshAll();
  setInterval(loadHealth, 30000);
  setInterval(loadHealthHistory, 60000);
});