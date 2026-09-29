/* ============ 库存移动与呆滞报表（ERP-029：只读派生，基础单位口径；未知显示「未知」，绝不显示为 0） ============
   商品筛选取自商品资料下拉（复用既有 GET /api/base/products 的有界查询 + keyword 匹配编码 / 名称）：
   选中下拉项 = 精确 productId，关键字 = 编码 / 名称模糊并同时筛选下拉内容，两者同时存在时按 AND 组合；
   下拉只登记商品资料里的真实编码 / 名称，报表不臆造名称、也不做事后文本权威匹配。 */

/* 台账状态 / 分类文案（与后端 InventoryMovementSemantics 常量一一对应） */
const IMR_HISTORY_LABELS = {
  ledger: '有台账（窗口内有移动）',
  window_empty: '有台账（窗口内无移动）',
  no_history: '无台账（历史库存 · 未知）',
};
const IMR_CLASS_LABELS = { active: '正常流动', stagnant: '呆滞', unknown: '无法判定' };

/* 分组键枚举（与后端 NormalizeGroupBy 一致；未知取值由后端拒绝） */
const IMR_DYN_GROUP_OPTS = [
  { value: 'none', label: '不分组' },
  { value: 'warehouse', label: '按仓库分组' },
  { value: 'classification', label: '按分类分组' },
  { value: 'history', label: '按台账状态分组' },
];

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
        <label>商品 <select id="imr-product" style="min-width:190px"><option value="">全部商品</option></select></label>
        <label>商品关键字 <input type="text" id="imr-keyword" style="width:170px" placeholder="编码 / 名称（同时筛选商品下拉）" oninput="loadImrProductOptions(this.value)"></label>
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

    <!-- ERP-131：字段设计器（只读预览）：复用上方仓库 / 商品 / 日期 / 阈值 / 每页筛选，勾选白名单字段预览授权有界结果 -->
    <div class="pd-hint" id="imr-designer-hint">
      🎛 字段设计器（只读预览）：勾选可见列 → 复用上方仓库 / 商品 / 日期 / 阈值筛选 → 预览授权有界结果；可导出当前页 CSV（选定列顺序，公式转义）。
    </div>
    <div class="toolbar" style="margin-top:0">
      <div class="toolbar-left" style="flex-wrap:wrap;gap:6px;align-items:center;font-size:13px">
        <label>分组 <select id="imr-dyn-group" style="min-width:150px" onchange="imrDynPreview(1)">
          <option value="none">不分组</option>
          <option value="warehouse">按仓库分组</option>
          <option value="classification">按分类分组</option>
          <option value="history">按台账状态分组</option>
        </select></label>
        <span id="imr-designer-fields">正在加载字段目录…</span>
      </div>
      <div class="toolbar-actions">
        <button class="btn btn-neutral btn-sm" onclick="imrDynToggleAll(true)">全选</button>
        <button class="btn btn-neutral btn-sm" onclick="imrDynToggleAll(false)">清空</button>
        <button class="btn btn-primary" onclick="imrDynPreview(1)">预览</button>
        <button class="btn btn-neutral" onclick="exportImrDesignerCsv()" title="导出当前页为 CSV（选定列）">📤 导出 CSV（选定列）</button>
      </div>
    </div>
    <div id="imr-designer-result"></div>

    <div class="kpi-grid" id="imr-kpi"></div>
    <div class="table-wrap" id="imr-table">
      <div class="skeleton-row"></div><div class="skeleton-row"></div>
    </div>
    <div class="pd-hint" id="imr-rule"></div>
    <div class="pagination" id="imr-pagination"></div>`;
  loadImrWarehouses();
  loadImrProductOptions('');
  loadInventoryMovementReport(1);
  loadImrDesignerCatalog();
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

/* 商品下拉：复用既有商品资料接口（keyword 匹配编码 / 名称），只登记商品资料里的真实编码 / 名称，不臆造；
   输入关键字时防抖刷新下拉内容（有界：单次最多 50 条）；接口不可用时不阻断报表，仍可留空或仅用关键字查询 */
let imrProductSearchTimer = null;
function loadImrProductOptions(keyword) {
  clearTimeout(imrProductSearchTimer);
  imrProductSearchTimer = setTimeout(async () => {
    try {
      const kw = (keyword || '').trim();
      const data = await api('/api/base/products?page=1&pageSize=50'
        + (kw ? '&keyword=' + encodeURIComponent(kw) : ''));
      const sel = document.getElementById('imr-product');
      if (!sel) return;
      const keep = sel.value;
      sel.innerHTML = '<option value="">全部商品</option>';
      (data.items || []).forEach(p => {
        const opt = document.createElement('option');
        opt.value = p.id;
        opt.textContent = ((p.productCode || '') + ' ' + (p.productName || '')).trim() || ('商品 ' + p.id);
        sel.appendChild(opt);
      });
      if (keep && sel.querySelector(`option[value="${keep}"]`)) sel.value = keep;
    } catch (e) { /* 忽略：商品下拉失败不影响报表查询 */ }
  }, 250);
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

/* ============ 库存移动字段设计器（ERP-131：只读、有界的前端字段选择与当前页 CSV 导出） ============
   口径与后端 ERP-130（DynamicInventoryMovementReportController / DynamicInventoryMovementReportRules）一一对应：
   - 字段选择器只由 GET /api/dynamic-inventory-movement-report 返回的有限白名单目录（18 个字段）渲染，绝无自由填写的字段名或 SQL；
   - 筛选复用上方既有的仓库 / 商品 / 截止日期 / 移动窗口 / 呆滞阈值 / 每页控件，预览走 POST /api/dynamic-inventory-movement-report，
     只发送「白名单字段 + 有界筛选 + 有界分页（pageSize 1~200）」，按请求顺序渲染返回的列名与单元格；
   - 基础单位口径（unit 列名「基础单位」）与未知历史语义（lastMovementDate / inactivityDays 无台账 = 未知）保持不变；
   - CSV 仅导出当前预览页、按选定列顺序、对公式前导文本加单引号转义、未知值原样保留；
   - 全程只读：不写库、不迁移、不执行任意 SQL；授权 / 无效请求 / 空结果 / 网络失败都在界面可见，且不暴露范围外数据。 */

/* 字段设计器状态（纯数据；DOM 访问只在事件处理函数内部发生） */
let IMR_DYN = {
  catalog: null,      // GET /api/dynamic-inventory-movement-report 返回的目录 DTO
  fields: [],         // 目录字段（白名单）
  selectedKeys: [],   // 当前勾选的字段键（默认全选）
  groupBy: 'none',    // 当前分组键（none / warehouse / classification / history）
  view: null,         // 最近一次预览结果
  page: 1,            // 当前预览页（预览 / 翻页复用）
};

/* HTML 转义（本地独立实现，避免依赖全局 escapeHtml 的加载顺序） */
function imrDynEsc(v) {
  return String(v ?? '').replace(/[&<>"']/g, c => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}

/* 规范化选定字段（fail closed）：只保留目录白名单内的键、去重、保持请求顺序；未知键丢弃 */
function imrDynSelectFields(catalogFields, selectedKeys) {
  const valid = new Set((catalogFields || []).map(f => f && f.key).filter(Boolean));
  const seen = new Set();
  const result = [];
  for (const k of (Array.isArray(selectedKeys) ? selectedKeys : [])) {
    if (typeof k !== 'string') continue;
    const key = k.trim();
    if (!key || !valid.has(key) || seen.has(key)) continue;
    seen.add(key);
    result.push(key);
  }
  return result;
}

/* 组装有界预览请求体：字段只来自目录、分页有界、筛选仅仓库 / 商品 / 日期 / 阈值，绝不接受任意字段名或 SQL */
function imrDynBuildRequest(state) {
  const fields = imrDynSelectFields(state.catalogFields, state.selectedKeys);
  const page = Math.max(1, Math.floor(Number(state.page) || 1));
  const maxPageSize = Number(state.maxPageSize) || 200;
  let pageSize = Math.floor(Number(state.pageSize));
  if (!Number.isFinite(pageSize)) pageSize = 50;
  pageSize = Math.max(1, Math.min(maxPageSize, pageSize));

  const req = { fields, page, pageSize };

  const groupBy = IMR_DYN_GROUP_OPTS.some(o => o.value === state.groupBy) ? state.groupBy : 'none';
  if (groupBy && groupBy !== 'none') req.groupBy = groupBy;

  const warehouseId = Number(state.warehouseId);
  if (Number.isFinite(warehouseId) && warehouseId > 0) req.warehouseId = warehouseId;
  const productId = Number(state.productId);
  if (Number.isFinite(productId) && productId > 0) req.productId = productId;

  if (state.asOfDate) req.asOfDate = String(state.asOfDate).slice(0, 10);
  if (state.windowStart) req.windowStart = String(state.windowStart).slice(0, 10);
  if (state.windowEnd) req.windowEnd = String(state.windowEnd).slice(0, 10);

  const inactiveRaw = (state.inactiveDays === null || state.inactiveDays === undefined || state.inactiveDays === '')
    ? null : Number(state.inactiveDays);
  req.inactiveDays = (inactiveRaw !== null && Number.isFinite(inactiveRaw)) ? Math.trunc(inactiveRaw) : 90;

  return req;
}

/* 单元格纯文本：无台账日期 / 停滞天数显示「未知」（不回落 0）、普通字段 null 显示为空、日期截断、枚举映射中文 */
function imrDynCellText(value, field) {
  const key = (field && field.key) || '';
  const dataType = (field && field.dataType) || 'text';
  if (value === null || value === undefined) {
    if (key === 'lastMovementDate' || key === 'inactivityDays') return '未知';
    return '';
  }
  if (key === 'historyStatus') return IMR_HISTORY_LABELS[value] || String(value);
  if (key === 'classification') return IMR_CLASS_LABELS[value] || String(value);
  if (dataType === 'date') return String(value).slice(0, 10);
  return String(value);
}

/* 单元格 HTML（转义后安全渲染） */
function imrDynRenderCell(value, field) {
  return imrDynEsc(imrDynCellText(value, field));
}

/* 结果表格 HTML：表头为返回的列名、单元格为返回的选定字段值，全部经转义 */
function imrDynTableHtml(view) {
  const cols = (view && view.columns) || [];
  const rows = (view && view.rows) || [];
  if (!cols.length) return '';
  const align = c => (c.dataType === 'number' || c.dataType === 'boolean') ? ' class="text-right"' : '';
  const head = cols.map(c => `<th${align(c)}>${imrDynEsc(c.label || c.key)}</th>`).join('');
  const body = rows.map(r =>
    `<tr>${cols.map(c => `<td${align(c)}>${imrDynRenderCell(r[c.key], c)}</td>`).join('')}</tr>`).join('');
  const emptyRow = `<tr><td colspan="${cols.length}" class="empty">没有符合条件的库存行</td></tr>`;
  const prevDisabled = !view || view.page <= 1 ? ' disabled' : '';
  const nextDisabled = !view || view.page >= view.totalPages ? ' disabled' : '';
  const paging = `<div style="display:flex;justify-content:space-between;align-items:center;margin-top:8px">
      <span class="text-muted">第 ${view ? view.page : 1} 页 / 共 ${view ? view.totalPages : 0} 页</span>
      <div>
        <button class="btn btn-neutral btn-sm" onclick="imrDynPage(-1)"${prevDisabled}>← 上一页</button>
        <button class="btn btn-neutral btn-sm" onclick="imrDynPage(1)"${nextDisabled}>下一页 →</button>
      </div></div>`;
  return `<div class="table-wrap" style="margin-top:8px"><table><thead><tr>${head}</tr></thead><tbody>${body || emptyRow}</tbody></table></div>${paging}`;
}

/* 分组行数分布图（ERP-132）：按当前授权预览页的分组行数渲染可访问的横向条形图（只统计行数，绝不求和任何数量）；
   固定分类（classification / history）的空分类与未知历史分类保留（计数可为 0）；标签与计数全部转义 */
function imrDynGroupsHtml(view) {
  const groupBy = view && view.groupBy;
  const groups = (view && view.groups) || [];
  if (!groupBy || groupBy === 'none' || !Array.isArray(groups) || groups.length === 0) return '';
  const max = Math.max(1, ...groups.map(g => Number(g && g.count) || 0));
  const bars = groups.map(g => {
    const label = (g && g.label) || (g && g.key) || '';
    const count = Number(g && g.count) || 0;
    const pct = Math.round(count / max * 100);
    return `<li role="listitem" aria-label="${imrDynEsc(label)}：${count} 行" style="display:flex;align-items:center;gap:8px;margin:4px 0">
      <span style="flex:0 0 180px;text-align:right;color:#334155;overflow:hidden;text-overflow:ellipsis;white-space:nowrap" title="${imrDynEsc(label)}">${imrDynEsc(label)}</span>
      <span style="flex:1;background:#e2e8f0;border-radius:4px;height:16px;overflow:hidden;min-width:40px">
        <span style="display:block;height:100%;background:#2563eb;width:${pct}%"></span>
      </span>
      <span style="flex:0 0 72px;text-align:right;color:#0f172a">${count} 行</span>
    </li>`;
  }).join('');
  return `<div class="pd-hint" role="img" aria-label="本页库存行数分布图（仅统计本页）" style="margin-top:8px">📊 本页行数分布（仅统计本页）</div>
    <ul role="list" style="list-style:none;padding:0 8px;margin:4px 0 8px">${bars}</ul>`;
}

/* 空结果提示 */
function imrDynEmptyHtml() {
  return '<div class="empty" style="margin:8px 0">没有符合条件的库存行（可放宽仓库 / 商品 / 日期筛选）。</div>';
}

/* 错误提示（授权 / 未登录 / 无效请求 / 网络失败分别可见，且不暴露任何数据） */
function imrDynErrorHtml(kind, message) {
  const labels = {
    forbidden: '权限不足',
    unauthorized: '未登录 / 登录已过期',
    invalid: '请求无效',
    empty: '导出内容为空',
    network: '网络请求失败',
    error: '预览失败',
  };
  return `<div class="pd-hint" style="color:#b91c1c;background:#fef2f2;border-color:#fecaca">
      <b>${imrDynEsc(labels[kind] || '预览失败')}</b>：${imrDynEsc(message || '')}</div>`;
}

/* 预览结果（口径 / 边界 / 免责文案 + 汇总 + 空结果 + 表格） */
function imrDynResultHtml(view) {
  const readOnly = view && view.readOnlyText ? `<div class="pd-hint">${imrDynEsc(view.readOnlyText)}</div>` : '';
  const boundary = view && view.boundaryText ? `<div class="pd-hint">${imrDynEsc(view.boundaryText)}</div>` : '';
  const disclaimer = view && view.disclaimerText ? `<div class="pd-hint" style="color:#64748b">${imrDynEsc(view.disclaimerText)}</div>` : '';
  const summary = view
    ? `<div class="text-muted" style="margin:6px 0">共 ${view.total} 行 · 第 ${view.page} 页 · 每页 ${view.pageSize} 行 · 基础单位口径</div>`
    : '';
  const groups = imrDynGroupsHtml(view);
  const empty = view && (!view.rows || view.rows.length === 0) ? imrDynEmptyHtml() : '';
  return `${readOnly}${boundary}${disclaimer}${summary}${groups}${empty}${imrDynTableHtml(view)}`;
}

/* 字段选择器：仅由目录白名单渲染为复选框，无自由填写的字段名 */
function imrDynFieldChooserHtml(fields, selectedKeys) {
  const selected = new Set(selectedKeys || []);
  return (fields || []).map(f => {
    const checked = selected.has(f.key) ? 'checked' : '';
    return `<label style="display:inline-flex;align-items:center;gap:4px;margin:3px 6px 3px 0;padding:2px 8px;border:1px solid #e2e8f0;border-radius:6px;background:#f8fafc;cursor:pointer">
        <input type="checkbox" name="imr-dyn-field" value="${imrDynEsc(f.key)}" ${checked} onchange="imrDynSyncSelection()">
        <span>${imrDynEsc(f.label || f.key)}</span></label>`;
  }).join('');
}

/* 业务码 → 错误态分类 */
function imrDynKindOfCode(code) {
  if (code === 2002) return 'forbidden';
  if (code === 2000 || code === 2003) return 'unauthorized';
  return 'invalid';
}

/* CSV 单元格：公式前导文本（= + - @ 制表 / 回车）前加单引号转义，再按 CSV 规则包裹双引号并转义内部双引号 */
function imrDynCsvCell(v) {
  let s = v === null || v === undefined ? '' : String(v);
  if (/^[=+\-@\t\r]/.test(s)) s = "'" + s;
  return '"' + s.replace(/"/g, '""') + '"';
}

/* 当前预览页 CSV：表头与数据行都按返回列（= 选定字段顺序）排列，未知值（无台账日期 / 停滞天数）原样保留为「未知」 */
function imrDynCsv(view) {
  const cols = (view && view.columns) || [];
  const rows = (view && view.rows) || [];
  const lines = [cols.map(c => imrDynCsvCell(c.label || c.key)).join(',')];
  for (const r of rows) {
    lines.push(cols.map(c => imrDynCsvCell(imrDynCellText(r[c.key], c))).join(','));
  }
  return lines.join('\r\n');
}

/* 轻量请求封装：返回完整 ApiResponse 信封（保留 code），网络异常抛给调用方 */
async function imrDynRequest(path, method = 'GET', body = null) {
  const headers = { 'Content-Type': 'application/json' };
  const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
  if (token) headers['Authorization'] = 'Bearer ' + token;
  const opts = { method, headers };
  if (body) opts.body = JSON.stringify(body);
  const resp = await fetch(path, opts);
  return await resp.json();
}

function imrDynLoadingHtml() {
  return '<div class="pd-hint" style="text-align:center;color:#64748b">正在预览（只读查询）…</div>';
}

function imrDynRenderResult(html) {
  const el = document.getElementById('imr-designer-result');
  if (el) el.innerHTML = html;
}

/* 同步勾选状态到 selectedKeys（复选框 onchange） */
function imrDynSyncSelection() {
  const boxes = document.querySelectorAll('input[name="imr-dyn-field"]');
  IMR_DYN.selectedKeys = Array.from(boxes).filter(b => b.checked).map(b => b.value);
}

function imrDynToggleAll(checked) {
  const boxes = document.querySelectorAll('input[name="imr-dyn-field"]');
  IMR_DYN.selectedKeys = [];
  boxes.forEach(b => { b.checked = checked; if (checked) IMR_DYN.selectedKeys.push(b.value); });
}

/* 读取当前字段 / 筛选 / 分页状态（预览与分页复用，单一来源；筛选复用既有 imr-* 控件） */
function imrDynBuildState(page) {
  const val = id => { const el = document.getElementById(id); return el ? el.value.trim() : ''; };
  return {
    catalogFields: IMR_DYN.fields,
    selectedKeys: IMR_DYN.selectedKeys,
    warehouseId: val('imr-warehouse'),
    productId: val('imr-product'),
    asOfDate: val('imr-asof'),
    windowStart: val('imr-window-start'),
    windowEnd: val('imr-window-end'),
    inactiveDays: val('imr-inactive'),
    pageSize: val('imr-pagesize'),
    groupBy: val('imr-dyn-group') || 'none',
    page: page || IMR_DYN.page || 1,
    maxPageSize: IMR_DYN.catalog && IMR_DYN.catalog.maxPageSize ? IMR_DYN.catalog.maxPageSize : 200,
  };
}

/* 预览：组装有界请求 → POST → 安全渲染列名与单元格；授权 / 无效 / 空 / 网络失败均可见 */
async function imrDynPreview(page) {
  const state = imrDynBuildState(page);
  if (state.windowStart && state.windowEnd && state.windowStart > state.windowEnd) {
    imrDynRenderResult(imrDynErrorHtml('invalid', '移动窗口开始日期不能晚于结束日期'));
    return;
  }

  const req = imrDynBuildRequest(state);
  IMR_DYN.page = req.page;

  imrDynRenderResult(imrDynLoadingHtml());

  try {
    const resp = await imrDynRequest('/api/dynamic-inventory-movement-report', 'POST', req);
    if (resp.code === 0) {
      IMR_DYN.view = resp.data;
      IMR_DYN.page = resp.data.page;
      imrDynRenderResult(imrDynResultHtml(resp.data));
    } else if (resp.code === 2000 || resp.code === 2003) {
      if (typeof logout === 'function') logout();
      imrDynRenderResult(imrDynErrorHtml('unauthorized', resp.message));
    } else {
      imrDynRenderResult(imrDynErrorHtml(imrDynKindOfCode(resp.code), resp.message));
    }
  } catch (err) {
    imrDynRenderResult(imrDynErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 翻页（有界：最小第 1 页） */
function imrDynPage(delta) {
  const page = (IMR_DYN.view ? IMR_DYN.view.page : IMR_DYN.page) + delta;
  if (page < 1) return;
  imrDynPreview(page);
}

/* 加载字段目录（需登录 + 库存查询菜单授权；授权 / 网络失败 fail closed，不渲染任何字段） */
async function loadImrDesignerCatalog() {
  try {
    const resp = await imrDynRequest('/api/dynamic-inventory-movement-report');
    if (resp.code === 2000 || resp.code === 2003) {
      if (typeof logout === 'function') logout();
      imrDynRenderResult(imrDynErrorHtml('unauthorized', resp.message));
      return;
    }
    if (resp.code !== 0) {
      imrDynRenderResult(imrDynErrorHtml(imrDynKindOfCode(resp.code), resp.message));
      return;
    }
    IMR_DYN.catalog = resp.data;
    IMR_DYN.fields = (resp.data && resp.data.fields) || [];
    IMR_DYN.selectedKeys = IMR_DYN.fields.map(f => f.key);
    const el = document.getElementById('imr-designer-fields');
    if (el) el.innerHTML = imrDynFieldChooserHtml(IMR_DYN.fields, IMR_DYN.selectedKeys);
  } catch (err) {
    imrDynRenderResult(imrDynErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 导出当前预览页为 CSV（选定列顺序 + 公式转义 + 未知值保留），不下载无数据的空表 */
function exportImrDesignerCsv() {
  if (!IMR_DYN.view || !IMR_DYN.view.rows || IMR_DYN.view.rows.length === 0) {
    toast('暂无可导出的预览数据（请先预览）', 'warning');
    return;
  }
  const csv = imrDynCsv(IMR_DYN.view);
  const blob = new Blob(['\uFEFF' + csv], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = '库存移动字段设计器_当前页.csv';
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
  toast('CSV 已导出（当前页 · 选定列）', 'success');
}

/* Node 单测导出（浏览器中 module 为 undefined，自动跳过） */
if (typeof module !== 'undefined' && module.exports) {
  module.exports = {
    IMR_HISTORY_LABELS,
    IMR_CLASS_LABELS,
    IMR_DYN_GROUP_OPTS,
    imrDynEsc,
    imrDynSelectFields,
    imrDynBuildRequest,
    imrDynCellText,
    imrDynRenderCell,
    imrDynTableHtml,
    imrDynGroupsHtml,
    imrDynEmptyHtml,
    imrDynErrorHtml,
    imrDynResultHtml,
    imrDynFieldChooserHtml,
    imrDynKindOfCode,
    imrDynCsvCell,
    imrDynCsv,
  };
}
