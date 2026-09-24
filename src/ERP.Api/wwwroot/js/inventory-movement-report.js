/* ============ 库存移动与呆滞报表（ERP-029：只读派生，基础单位口径；未知显示「未知」，绝不显示为 0） ============ */

/* 台账状态 / 分类文案（与后端 InventoryMovementSemantics 常量一一对应） */
const IMR_HISTORY_LABELS = {
  ledger: '有台账（窗口内有移动）',
  window_empty: '有台账（窗口内无移动）',
  no_history: '无台账（历史库存 · 未知）',
};
const IMR_CLASS_LABELS = { active: '正常流动', stagnant: '呆滞', unknown: '无法判定' };

/* 工具栏入口（库存查询页）：渲染独立报表页，筛选与数据全部走既有只读接口 GET /api/reports/inventory-movement */
function openInventoryMovementReport() {
  const today = new Date().toISOString().slice(0, 10);
  const windowStart = new Date(Date.now() - 89 * 86400000).toISOString().slice(0, 10);
  CURRENT_PAGE_CODE = 'inventory-movement';
  document.getElementById('header-title').textContent = '库存移动与呆滞报表';
  document.getElementById('content').innerHTML = `
    <div class="page-hero report-hero">
      <h2>📉 库存移动与呆滞报表</h2>
      <p>基础单位口径 · 主表为库存行 · 出入库 / 最后移动日期 / 停滞天数取自库存流水台账（只读派生，不估算成本）</p>
    </div>

    <div class="toolbar">
      <div class="toolbar-left" style="flex-wrap:wrap;gap:8px;align-items:center">
        <label>仓库 <select id="imr-warehouse" style="min-width:150px"><option value="">全部仓库</option></select></label>
        <label>商品ID <input type="number" id="imr-product" style="width:110px" placeholder="可留空"></label>
        <label>商品关键字 <input type="text" id="imr-keyword" style="width:150px" placeholder="编码 / 名称"></label>
        <label>截止日期 <input type="date" id="imr-asof" value="${today}"></label>
        <label>移动窗口 <input type="date" id="imr-window-start" value="${windowStart}"> 至
          <input type="date" id="imr-window-end" value="${today}"></label>
        <label>呆滞阈值(天) <input type="number" id="imr-inactive" value="90" min="1" style="width:80px"></label>
        <label><input type="checkbox" id="imr-only-positive" checked> 仅现存量 &gt; 0</label>
        <label>每页 <input type="number" id="imr-pagesize" value="50" min="1" max="200" style="width:70px"></label>
      </div>
      <div class="toolbar-actions">
        <button class="btn btn-primary" onclick="loadInventoryMovementReport(1)">查询</button>
        <button class="btn btn-neutral" onclick="exportImrCsv()" title="导出当前页为 CSV">📤 导出 CSV</button>
      </div>
    </div>

    <div class="kpi-grid" id="imr-kpi"></div>
    <div class="table-wrap" id="imr-table">
      <div class="skeleton-row"></div><div class="skeleton-row"></div>
    </div>
    <div class="pd-hint" id="imr-rule"></div>
    <div class="pagination" id="imr-pagination"></div>`;
  loadImrWarehouses();
  loadInventoryMovementReport(1);
}

/* 仓库下拉：既有基础资料接口；仓库列表不可用时不阻断报表（仍可用商品筛选） */
async function loadImrWarehouses() {
  try {
    const data = await api('/api/base/warehouses?page=1&pageSize=200');
    const sel = document.getElementById('imr-warehouse');
    if (!sel) return;
    (data.items || []).forEach(w => {
      const opt = document.createElement('option');
      opt.value = w.id;
      opt.textContent = w.warehouseName || ('仓库 ' + w.id);
      sel.appendChild(opt);
    });
  } catch (e) { /* 忽略：仓库下拉失败不影响报表查询 */ }
}

/* 查询参数：全部由页面筛选控件组装（留空即不传，由后端取默认值） */
function imrQuery(page) {
  const val = id => { const el = document.getElementById(id); return el ? el.value.trim() : ''; };
  const q = new URLSearchParams();
  if (val('imr-warehouse')) q.set('warehouseId', val('imr-warehouse'));
  if (val('imr-product')) q.set('productId', val('imr-product'));
  if (val('imr-keyword')) q.set('keyword', val('imr-keyword'));
  if (val('imr-asof')) q.set('asOfDate', val('imr-asof'));
  if (val('imr-window-start')) q.set('windowStart', val('imr-window-start'));
  if (val('imr-window-end')) q.set('windowEnd', val('imr-window-end'));
  q.set('inactiveDays', val('imr-inactive') || '90');
  const onlyPositive = document.getElementById('imr-only-positive');
  q.set('onlyPositiveQuantity', onlyPositive && onlyPositive.checked ? 'true' : 'false');
  q.set('page', page || 1);
  q.set('pageSize', val('imr-pagesize') || '50');
  return q.toString();
}

async function loadInventoryMovementReport(page) {
  const el = document.getElementById('imr-table');
  if (!el) return;
  el.innerHTML = '<div class="skeleton-row"></div><div class="skeleton-row"></div>';
  try {
    const data = await api('/api/reports/inventory-movement?' + imrQuery(page));
    imrRenderKpi(data);
    imrRenderTable(data);
    imrRenderPagination(data);
    const rule = document.getElementById('imr-rule');
    if (rule) rule.textContent = '口径：' + (data.rule || '') + ' ' + (data.scopeNote || '');
  } catch (e) {
    el.innerHTML = `<div class="empty"><div style="font-size:48px">⚠️</div><div>报表加载失败：${escapeHtml(e.message)}</div></div>`;
  }
}


/* 未知（null）= 无台账 / 不适用，显示「未知」而不是 0 */
function imrDate(v) { return v ? fmtDate(v) : '未知'; }
function imrDays(v) { return v === null || v === undefined ? '未知' : String(v); }

function imrClassHtml(c) {
  const cls = c === 'stagnant' ? 'status-danger' : (c === 'active' ? 'status-success' : 'status-neutral');
  return `<span class="status ${cls}">${escapeHtml(IMR_CLASS_LABELS[c] || c || '')}</span>`;
}

function imrHistoryHtml(h) {
  const cls = h === 'no_history' ? 'status-warning' : 'status-neutral';
  return `<span class="status ${cls}">${escapeHtml(IMR_HISTORY_LABELS[h] || h || '')}</span>`;
}

function imrRenderKpi(data) {
  const el = document.getElementById('imr-kpi');
  if (!el) return;
  el.innerHTML = `
    <div class="kpi-card cargo">
      <div class="kpi-label">符合筛选的库存行</div>
      <div class="kpi-value">${data.total}<span class="unit">行</span></div>
      <div class="kpi-delta flat">本页 ${(data.items || []).length} 行 · 第 ${data.page}/${data.totalPages} 页</div>
    </div>
    <div class="kpi-card gold">
      <div class="kpi-label">本页现存量（基础单位）</div>
      <div class="kpi-value">${fmtMoney(data.pageCurrentQuantity)}</div>
      <div class="kpi-delta flat">窗口内入库 ${fmtMoney(data.pageInboundQuantity)} / 出库 ${fmtMoney(data.pageOutboundQuantity)} / 净 ${fmtMoney(data.pageNetQuantity)}</div>
    </div>
    <div class="kpi-card ocean">
      <div class="kpi-label">呆滞 / 正常流动</div>
      <div class="kpi-value">${data.stagnantCount} / ${data.activeCount}<span class="unit">行</span></div>
      <div class="kpi-delta flat">阈值 ${data.inactiveDays} 天 · 无台账 ${data.insufficientHistoryCount} 行 · 窗口内无移动 ${data.windowEmptyCount} 行</div>
    </div>
    <div class="kpi-card lc">
      <div class="kpi-label">报表时点 / 移动窗口</div>
      <div class="kpi-value" style="font-size:16px">${fmtDate(data.asOfDate)}</div>
      <div class="kpi-delta flat">${fmtDate(data.windowStart)} ~ ${fmtDate(data.windowEnd)}</div>
    </div>`;
}

function imrRenderTable(data) {
  const el = document.getElementById('imr-table');
  if (!el) return;
  const rows = (data.items || []).map(r => `<tr>
      <td>${escapeHtml(r.warehouseName || ('仓库 ' + r.warehouseId))}</td>
      <td>${escapeHtml(r.productCode || '')}</td>
      <td>${escapeHtml(r.productName || '')}</td>
      <td>${escapeHtml(r.spec || '')}</td>
      <td>${escapeHtml(r.unit || '')}</td>
      <td class="text-right">${fmtMoney(r.currentQuantity)}</td>
      <td>${imrDate(r.lastMovementDate)}</td>
      <td class="text-right">${fmtMoney(r.inboundQuantity)}</td>
      <td class="text-right">${fmtMoney(r.outboundQuantity)}</td>
      <td class="text-right">${fmtMoney(r.netQuantity)}</td>
      <td class="text-right">${r.movementCount}</td>
      <td class="text-right">${r.reversalCount}</td>
      <td class="text-right">${imrDays(r.inactivityDays)}</td>
      <td>${imrClassHtml(r.classification)}</td>
      <td>${imrHistoryHtml(r.historyStatus)}</td>
      <td>${escapeHtml(r.note || '')}</td>
    </tr>`).join('');
  el.innerHTML = `<table><thead><tr>
      <th>仓库</th><th>商品编码</th><th>商品名称</th><th>规格</th><th>单位</th>
      <th class="text-right">现存量</th><th>最后移动日期</th>
      <th class="text-right">入库</th><th class="text-right">出库</th><th class="text-right">净变动</th>
      <th class="text-right">台账行数</th><th class="text-right">红字行数</th>
      <th class="text-right">停滞天数</th><th>分类</th><th>台账状态</th><th>说明</th>
    </tr></thead>
    <tbody>${rows || '<tr><td colspan="16" class="empty">没有符合条件的库存行（可放宽仓库 / 商品筛选或勾选「仅现存量 &gt; 0」）</td></tr>'}</tbody></table>`;
}

function imrRenderPagination(data) {
  const el = document.getElementById('imr-pagination');
  if (!el) return;
  const page = data.page || 1;
  const totalPages = data.totalPages || 1;
  el.innerHTML = `
    <button class="btn btn-neutral btn-sm" ${page <= 1 ? 'disabled' : ''} onclick="loadInventoryMovementReport(${page - 1})">上一页</button>
    <span style="margin:0 10px">第 ${page} / ${totalPages} 页（共 ${data.total} 行）</span>
    <button class="btn btn-neutral btn-sm" ${page >= totalPages ? 'disabled' : ''} onclick="loadInventoryMovementReport(${page + 1})">下一页</button>`;
}

/* 导出当前页为 CSV（与报表中心同一套口径：未知值按表格文本原样导出，不回落为 0） */
function exportImrCsv() {
  const table = document.querySelector('#imr-table table');
  if (!table) { toast('暂无可导出数据', 'warning'); return; }
  const rows = Array.from(table.querySelectorAll('tr'));
  const csv = rows.map(tr => Array.from(tr.querySelectorAll('th,td'))
    .map(td => `"${td.textContent.replace(/"/g, '""')}"`).join(',')).join('\n');
  const blob = new Blob(['\uFEFF' + csv], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = '库存移动与呆滞报表.csv';
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
  toast('CSV 已导出', 'success');
}
