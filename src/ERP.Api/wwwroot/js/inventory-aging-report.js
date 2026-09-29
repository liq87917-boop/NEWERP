/* ============ 库存库龄与成本估值报表（ERP-034：只读派生，基础单位口径；未知一律显示「未知」，绝不回落为 0） ============
   库龄分层由后端按库存流水台账 FIFO 派生（红字冲销按 ReversalOfMovementId 权威配对），
   没有台账分层依据的数量单列为「库龄未知」而不放进任何分层；估值只用库存行持久化的移动加权平均成本与库存金额。
   商品筛选取自商品资料下拉（复用既有 GET /api/base/products 的有界查询 + keyword 匹配编码 / 名称）。 */

/* 库龄依据 / 成本状态文案（与后端 InventoryAgingSemantics 常量一一对应） */
const IAR_EVIDENCE_LABELS = {
  full: '有台账分层依据',
  partial: '部分数量无依据',
  none: '无台账分层依据',
};
const IAR_COST_LABELS = { known: '成本已知', unknown: '成本未知' };

/* 工具栏入口（库存查询页）：渲染独立报表页，筛选与数据全部走既有只读接口 GET /api/reports/inventory-aging */
function openInventoryAgingReport() {
  const today = new Date().toISOString().slice(0, 10);
  CURRENT_PAGE_CODE = 'inventory-aging';
  document.getElementById('header-title').textContent = '库存库龄与成本估值报表';
  document.getElementById('content').innerHTML = `
    <div class="page-hero report-hero">
      <h2>⏳ 库存库龄与成本估值报表</h2>
      <p>基础单位口径 · 主表为库存行 · 库龄按库存流水台账 FIFO 分层（红字冲销配对） · 金额以库存行持久化加权平均成本为准（只读派生）</p>
    </div>

    <div class="toolbar">
      <div class="toolbar-left" style="flex-wrap:wrap;gap:8px;align-items:center">
        <label>仓库 <select id="iar-warehouse" style="min-width:150px"><option value="">全部仓库</option></select></label>
        <label>商品 <select id="iar-product" style="min-width:190px"><option value="">全部商品</option></select></label>
        <label>商品关键字 <input type="text" id="iar-keyword" style="width:170px" placeholder="编码 / 名称（同时筛选商品下拉）" oninput="loadIarProductOptions(this.value)"></label>
        <label>截止日期 <input type="date" id="iar-asof" value="${today}"></label>
        <label><input type="checkbox" id="iar-only-positive" checked> 仅现存量 &gt; 0</label>
        <label>每页 <input type="number" id="iar-pagesize" value="50" min="1" max="200" style="width:70px"></label>
      </div>
      <div class="toolbar-actions">
        <button class="btn btn-primary" onclick="loadInventoryAgingReport(1)">查询</button>
        <button class="btn btn-neutral" onclick="exportIarCsv()" title="导出当前页为 CSV">📤 导出 CSV</button>
      </div>
    </div>

    <!-- ERP-136：字段设计器（只读预览）：复用上方仓库 / 商品 / 截止日期 / 每页筛选，勾选白名单字段预览授权有界结果 -->
    <div class="pd-hint" id="iar-designer-hint">
      🎛 字段设计器（只读预览）：勾选可见列 → 复用上方仓库 / 商品 / 截止日期筛选 → 预览授权有界结果；可导出当前页 CSV（选定列顺序，公式转义，未知值保留）。
    </div>
    <div class="toolbar" style="margin-top:0">
      <div class="toolbar-left" style="flex-wrap:wrap;gap:6px;align-items:center;font-size:13px">
        <label>分组 <select id="iar-dyn-group" style="min-width:150px" onchange="iarDynPreview(1)">
          <option value="none">不分组</option>
          <option value="warehouse">按仓库分组</option>
          <option value="ageEvidence">按库龄依据分组</option>
          <option value="costEvidence">按成本依据分组</option>
        </select></label>
        <span id="iar-designer-fields">正在加载字段目录…</span>
      </div>
      <div class="toolbar-actions">
        <button class="btn btn-neutral btn-sm" onclick="iarDynToggleAll(true)">全选</button>
        <button class="btn btn-neutral btn-sm" onclick="iarDynToggleAll(false)">清空</button>
        <button class="btn btn-primary" onclick="iarDynPreview(1)">预览</button>
        <button class="btn btn-neutral" onclick="exportIarDesignerCsv()" title="导出当前页为 CSV（选定列）">📤 导出 CSV（选定列）</button>
      </div>
    </div>
    <div id="iar-designer-result"></div>

    <div class="kpi-grid" id="iar-kpi"></div>
    <div class="table-wrap" id="iar-buckets"></div>
    <div class="table-wrap" id="iar-table">
      <div class="skeleton-row"></div><div class="skeleton-row"></div>
    </div>
    <div class="pd-hint" id="iar-rule"></div>
    <div class="pagination" id="iar-pagination"></div>`;
  loadIarWarehouses();
  loadIarProductOptions('');
  loadInventoryAgingReport(1);
  loadIarDesignerCatalog();
}

/* 仓库下拉：既有基础资料接口；仓库列表不可用时不阻断报表（仍可用商品筛选） */
async function loadIarWarehouses() {
  try {
    const data = await api('/api/base/warehouses?page=1&pageSize=200');
    const sel = document.getElementById('iar-warehouse');
    if (!sel) return;
    (data.items || []).forEach(w => {
      const opt = document.createElement('option');
      opt.value = w.id;
      opt.textContent = w.warehouseName || ('仓库 ' + w.id);
      sel.appendChild(opt);
    });
  } catch (e) { /* 忽略：仓库下拉失败不影响报表查询 */ }
}

/* 商品下拉：复用既有商品资料接口（keyword 匹配编码 / 名称），只登记真实商品资料，不臆造编码或名称；
   输入关键字时防抖刷新（有界：单次最多 50 条）；接口不可用时不阻断报表，仍可留空或仅用关键字查询 */
let iarProductSearchTimer = null;
function loadIarProductOptions(keyword) {
  clearTimeout(iarProductSearchTimer);
  iarProductSearchTimer = setTimeout(async () => {
    try {
      const kw = (keyword || '').trim();
      const data = await api('/api/base/products?page=1&pageSize=50'
        + (kw ? '&keyword=' + encodeURIComponent(kw) : ''));
      const sel = document.getElementById('iar-product');
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
function iarQuery(page) {
  const val = id => { const el = document.getElementById(id); return el ? el.value.trim() : ''; };
  const q = new URLSearchParams();
  if (val('iar-warehouse')) q.set('warehouseId', val('iar-warehouse'));
  if (val('iar-product')) q.set('productId', val('iar-product'));
  if (val('iar-keyword')) q.set('keyword', val('iar-keyword'));
  if (val('iar-asof')) q.set('asOfDate', val('iar-asof'));
  const onlyPositive = document.getElementById('iar-only-positive');
  q.set('onlyPositiveQuantity', onlyPositive && onlyPositive.checked ? 'true' : 'false');
  q.set('page', page || 1);
  q.set('pageSize', val('iar-pagesize') || '50');
  return q.toString();
}

async function loadInventoryAgingReport(page) {
  const el = document.getElementById('iar-table');
  if (!el) return;
  el.innerHTML = '<div class="skeleton-row"></div><div class="skeleton-row"></div>';
  try {
    const data = await api('/api/reports/inventory-aging?' + iarQuery(page));
    iarRenderKpi(data);
    iarRenderPageBuckets(data);
    iarRenderTable(data);
    iarRenderPagination(data);
    const rule = document.getElementById('iar-rule');
    if (rule) {
      rule.textContent = '口径：' + (data.rule || '') + ' 估值：' + (data.costRule || '')
        + ' 币种：' + (data.costCurrency || '') + ' ' + (data.scopeNote || '');
    }
  } catch (e) {
    el.innerHTML = `<div class="empty"><div style="font-size:48px">⚠️</div><div>报表加载失败：${escapeHtml(e.message)}</div></div>`;
  }
}

/* 未知（null / undefined）= 无成本依据，显示「未知」而不是 0 */
function iarMoney(v) { return v === null || v === undefined ? '未知' : fmtMoney(v); }

function iarEvidenceHtml(s) {
  const cls = s === 'none' ? 'status-danger' : (s === 'partial' ? 'status-warning' : 'status-success');
  return `<span class="status ${cls}">${escapeHtml(IAR_EVIDENCE_LABELS[s] || s || '')}</span>`;
}

function iarCostHtml(s) {
  const cls = s === 'unknown' ? 'status-warning' : 'status-success';
  return `<span class="status ${cls}">${escapeHtml(IAR_COST_LABELS[s] || s || '')}</span>`;
}

function iarRenderKpi(data) {
  const el = document.getElementById('iar-kpi');
  if (!el) return;
  el.innerHTML = `
    <div class="kpi-card cargo">
      <div class="kpi-label">符合筛选的库存行</div>
      <div class="kpi-value">${data.total}<span class="unit">行</span></div>
      <div class="kpi-delta flat">本页 ${(data.items || []).length} 行 · 第 ${data.page}/${data.totalPages} 页 · 截止 ${fmtDate(data.asOfDate)}</div>
    </div>
    <div class="kpi-card gold">
      <div class="kpi-label">本页现存量（基础单位）</div>
      <div class="kpi-value">${fmtMoney(data.pageCurrentQuantity)}</div>
      <div class="kpi-delta flat">有台账分层依据 ${fmtMoney(data.pageKnownAgedQuantity)} · 库龄未知 ${fmtMoney(data.pageUnknownAgeQuantity)}</div>
    </div>
    <div class="kpi-card ocean">
      <div class="kpi-label">本页权威库存金额（${escapeHtml(data.costCurrency || '')}）</div>
      <div class="kpi-value">${iarMoney(data.pageAuthoritativeAmount)}</div>
      <div class="kpi-delta flat">成本已知 ${data.knownCostCount} 行 · 成本未知 ${data.unknownCostCount} 行（数量 ${fmtMoney(data.pageUnknownCostQuantity)}，金额未知）</div>
    </div>
    <div class="kpi-card lc">
      <div class="kpi-label">库龄依据</div>
      <div class="kpi-value" style="font-size:16px">完整 ${data.fullEvidenceCount} · 部分缺失 ${data.partialEvidenceCount} · 无依据 ${data.noEvidenceCount}</div>
      <div class="kpi-delta flat">没有台账分层依据的数量一律单列「库龄未知」，不放进任何分层</div>
    </div>`;
}

/* 本页分层合计（数量 / 金额）：金额为 null 表示本页相关行都没有成本依据（未知，不是 0） */
function iarRenderPageBuckets(data) {
  const el = document.getElementById('iar-buckets');
  if (!el) return;
  const buckets = data.pageBuckets || [];
  el.innerHTML = `<table><thead><tr><th>本页分层合计</th>
      ${buckets.map(b => `<th class="text-right">${escapeHtml(b.label || b.key)}</th>`).join('')}
      <th class="text-right">库龄未知</th></tr></thead>
    <tbody>
      <tr><td>数量（基础单位）</td>
        ${buckets.map(b => `<td class="text-right">${fmtMoney(b.quantity)}</td>`).join('')}
        <td class="text-right">${fmtMoney(data.pageUnknownAgeQuantity)}</td></tr>
      <tr><td>金额（${escapeHtml(data.costCurrency || '')}）</td>
        ${buckets.map(b => `<td class="text-right">${iarMoney(b.amount)}</td>`).join('')}
        <td class="text-right">未知</td></tr>
    </tbody></table>`;
}

function iarBucketCell(row, index) {
  const bucket = (row.buckets || [])[index];
  if (!bucket) return '<td class="text-right"></td>';
  return `<td class="text-right">${fmtMoney(bucket.quantity)}<br><span class="kpi-delta flat">${iarMoney(bucket.amount)}</span></td>`;
}


/* 分层列头：优先取本页分层合计的标签（后端返回的中文口径），无数据时回落第一行的分层标签 */
function iarBucketLabels(data) {
  const fromPage = (data.pageBuckets || []).map(b => b.label || b.key);
  if (fromPage.length) return fromPage;
  const first = (data.items || [])[0];
  return first ? (first.buckets || []).map(b => b.label || b.key) : [];
}

function iarRenderTable(data) {
  const el = document.getElementById('iar-table');
  if (!el) return;
  const labels = iarBucketLabels(data);
  const rows = (data.items || []).map(r => `<tr>
      <td>${escapeHtml(r.warehouseName || ('仓库 ' + r.warehouseId))}</td>
      <td>${escapeHtml(r.productCode || '')}</td>
      <td>${escapeHtml(r.productName || '')}</td>
      <td>${escapeHtml(r.spec || '')}</td>
      <td>${escapeHtml(r.unit || '')}</td>
      <td class="text-right">${fmtMoney(r.currentQuantity)}</td>
      ${labels.map((_, i) => iarBucketCell(r, i)).join('')}
      <td class="text-right">${fmtMoney(r.unknownAgeQuantity)}</td>
      <td class="text-right">${iarMoney(r.agedAmount)}</td>
      <td class="text-right">${iarMoney(r.authoritativeAmount)}</td>
      <td class="text-right">${fmtMoney(r.averageCost)}</td>
      <td>${iarCostHtml(r.costStatus)}</td>
      <td>${iarEvidenceHtml(r.evidenceStatus)}</td>
      <td>${escapeHtml(r.note || '')}</td>
    </tr>`).join('');
  const colspan = 12 + labels.length;
  el.innerHTML = `<table><thead><tr>
      <th>仓库</th><th>商品编码</th><th>商品名称</th><th>规格</th><th>单位</th>
      <th class="text-right">现存量</th>
      ${labels.map(l => `<th class="text-right">${escapeHtml(l)}<br><span class="kpi-delta flat">数量 / 金额</span></th>`).join('')}
      <th class="text-right">库龄未知</th>
      <th class="text-right">分层金额合计</th><th class="text-right">权威金额</th>
      <th class="text-right">成本单价</th><th>成本状态</th><th>库龄依据</th><th>说明</th>
    </tr></thead>
    <tbody>${rows || `<tr><td colspan="${colspan}" class="empty">没有符合条件的库存行（可放宽仓库 / 商品筛选或勾选「仅现存量 &gt; 0」）</td></tr>`}</tbody></table>`;
}

function iarRenderPagination(data) {
  const el = document.getElementById('iar-pagination');
  if (!el) return;
  const page = data.page || 1;
  const totalPages = data.totalPages || 1;
  el.innerHTML = `
    <button class="btn btn-neutral btn-sm" ${page <= 1 ? 'disabled' : ''} onclick="loadInventoryAgingReport(${page - 1})">上一页</button>
    <span style="margin:0 10px">第 ${page} / ${totalPages} 页（共 ${data.total} 行）</span>
    <button class="btn btn-neutral btn-sm" ${page >= totalPages ? 'disabled' : ''} onclick="loadInventoryAgingReport(${page + 1})">下一页</button>`;
}

/* ============ 库存库龄字段设计器（ERP-136：只读、有界的前端字段选择与当前页 CSV 导出） ============
   口径与后端 ERP-135（DynamicInventoryAgingReportController / DynamicInventoryAgingReportRules）一一对应：
   - 字段选择器只由 GET /api/dynamic-inventory-aging-report 返回的有限白名单目录（32 个字段）渲染，绝无自由填写的字段名或 SQL；
   - 筛选复用上方既有的仓库 / 商品 / 截止日期 / 每页控件，预览走 POST /api/dynamic-inventory-aging-report，
     只发送「白名单字段 + 有界筛选 + 有界分页（pageSize 1~200）」，按请求顺序渲染返回的列名与单元格；
   - 未知库龄（无台账分层依据）与未知成本（成本状态 unknown、金额 null）语义保持不变，金额币种显式为 CNY；
   - 固定 5 格库龄分层顺序（0-30 / 31-60 / 61-90 / 91-180 / 180 天以上）绝不重排；
   - CSV 仅导出当前预览页、按选定列顺序、对公式前导文本加单引号转义、未知值原样保留为「未知」；
   - 全程只读：不写库、不迁移、不执行任意 SQL；授权 / 无效请求 / 空结果 / 网络失败都在界面可见，且不暴露范围外数据。 */

/* 固定 5 格库龄分层（顺序与后端 InventoryAgingSemantics.BucketKeys 一致，绝不重排） */
const IAR_DYN_BUCKET_KEYS = [
  'bucket0To30', 'bucket31To60', 'bucket61To90', 'bucket91To180', 'bucketOver180',
];

/* 分组键枚举（与后端 NormalizeGroupBy 一致；未知取值由后端拒绝） */
const IAR_DYN_GROUP_OPTS = [
  { value: 'none', label: '不分组' },
  { value: 'warehouse', label: '按仓库分组' },
  { value: 'ageEvidence', label: '按库龄依据分组' },
  { value: 'costEvidence', label: '按成本依据分组' },
];

/* 字段设计器状态（纯数据；DOM 访问只在事件处理函数内部发生） */
let IAR_DYN = {
  catalog: null,      // GET /api/dynamic-inventory-aging-report 返回的目录 DTO
  fields: [],         // 目录字段（白名单）
  selectedKeys: [],   // 当前勾选的字段键（默认全选）
  view: null,         // 最近一次预览结果
  page: 1,            // 当前预览页（预览 / 翻页复用）
};

/* HTML 转义（本地独立实现，避免依赖全局 escapeHtml 的加载顺序） */
function iarDynEsc(v) {
  return String(v ?? '').replace(/[&<>"']/g, c => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}

/* 规范化选定字段（fail closed）：只保留目录白名单内的键、去重、保持请求顺序；未知键丢弃 */
function iarDynSelectFields(catalogFields, selectedKeys) {
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

/* 组装有界预览请求体：字段只来自目录、分页有界、筛选仅仓库 / 商品 / 截止日期，绝不接受任意字段名或 SQL */
function iarDynBuildRequest(state) {
  const fields = iarDynSelectFields(state.catalogFields, state.selectedKeys);
  const page = Math.max(1, Math.floor(Number(state.page) || 1));
  const maxPageSize = Number(state.maxPageSize) || 200;
  let pageSize = Math.floor(Number(state.pageSize));
  if (!Number.isFinite(pageSize)) pageSize = 50;
  pageSize = Math.max(1, Math.min(maxPageSize, pageSize));

  const req = { fields, page, pageSize };

  const groupBy = IAR_DYN_GROUP_OPTS.some(o => o.value === state.groupBy) ? state.groupBy : 'none';
  if (groupBy && groupBy !== 'none') req.groupBy = groupBy;

  const warehouseId = Number(state.warehouseId);
  if (Number.isFinite(warehouseId) && warehouseId > 0) req.warehouseId = warehouseId;
  const productId = Number(state.productId);
  if (Number.isFinite(productId) && productId > 0) req.productId = productId;
  if (state.asOfDate) req.asOfDate = String(state.asOfDate).slice(0, 10);

  return req;
}

/* 是否为成本 / 金额类字段（未知成本时金额为 null，显示「未知」而非 0） */
function iarDynIsCostKey(key) {
  return key === 'averageCost' || /Amount$/.test(key);
}

/* 单元格纯文本：成本 / 金额类字段 null 显示「未知」（不回落 0）、普通字段 null 显示为空、枚举映射中文、数字合理格式化 */
function iarDynCellText(value, field) {
  const key = (field && field.key) || '';
  const dataType = (field && field.dataType) || 'text';
  if (value === null || value === undefined) {
    return iarDynIsCostKey(key) ? '未知' : '';
  }
  if (key === 'evidenceStatus') return IAR_EVIDENCE_LABELS[value] || String(value);
  if (key === 'costStatus') return IAR_COST_LABELS[value] || String(value);
  if (dataType === 'number') {
    const n = Number(value);
    if (Number.isFinite(n)) return Number.isInteger(n) ? String(n) : n.toFixed(2);
    return String(value);
  }
  return String(value);
}

/* 单元格 HTML（转义后安全渲染） */
function iarDynRenderCell(value, field) {
  return iarDynEsc(iarDynCellText(value, field));
}

/* 结果表格 HTML：表头为返回的列名、单元格为返回的选定字段值，全部经转义；空结果在表体内可见 */
function iarDynTableHtml(view) {
  const cols = (view && view.columns) || [];
  const rows = (view && view.rows) || [];
  if (!cols.length) return '';
  const align = c => (c.dataType === 'number') ? ' class="text-right"' : '';
  const head = cols.map(c => `<th${align(c)}>${iarDynEsc(c.label || c.key)}</th>`).join('');
  const body = rows.length
    ? rows.map(r => `<tr>${cols.map(c => `<td${align(c)}>${iarDynRenderCell(r[c.key], c)}</td>`).join('')}</tr>`).join('')
    : `<tr><td colspan="${cols.length}" class="empty">没有符合条件的库存行</td></tr>`;
  const prevDisabled = !view || view.page <= 1 ? ' disabled' : '';
  const nextDisabled = !view || view.page >= view.totalPages ? ' disabled' : '';
  const paging = `<div style="display:flex;justify-content:space-between;align-items:center;margin-top:8px">
      <span class="text-muted">第 ${view ? view.page : 1} 页 / 共 ${view ? view.totalPages : 0} 页</span>
      <div>
        <button class="btn btn-neutral btn-sm" onclick="iarDynPage(-1)"${prevDisabled}>← 上一页</button>
        <button class="btn btn-neutral btn-sm" onclick="iarDynPage(1)"${nextDisabled}>下一页 →</button>
      </div></div>`;
  return `<div class="table-wrap" style="margin-top:8px"><table><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table></div>${paging}`;
}

/* 分组行数分布图（ERP-137）：按当前授权预览页的分组行数渲染可访问的横向条形图（只统计行数，绝不求和任何数量 / 金额）；
   固定证据分类（ageEvidence / costEvidence）的空分类始终保留（计数可为 0）；标签与计数全部转义 */
function iarDynGroupsHtml(view) {
  const groupBy = view && view.groupBy;
  const groups = (view && view.groups) || [];
  if (!groupBy || groupBy === 'none' || !Array.isArray(groups) || groups.length === 0) return '';
  const max = Math.max(1, ...groups.map(g => Number(g && g.count) || 0));
  const bars = groups.map(g => {
    const label = (g && g.label) || (g && g.key) || '';
    const count = Number(g && g.count) || 0;
    const pct = Math.round(count / max * 100);
    return `<li role="listitem" aria-label="${iarDynEsc(label)}：${count} 行" style="display:flex;align-items:center;gap:8px;margin:4px 0">
      <span style="flex:0 0 180px;text-align:right;color:#334155;overflow:hidden;text-overflow:ellipsis;white-space:nowrap" title="${iarDynEsc(label)}">${iarDynEsc(label)}</span>
      <span style="flex:1;background:#e2e8f0;border-radius:4px;height:16px;overflow:hidden;min-width:40px">
        <span style="display:block;height:100%;background:#2563eb;width:${pct}%"></span>
      </span>
      <span style="flex:0 0 72px;text-align:right;color:#0f172a">${count} 行</span>
    </li>`;
  }).join('');
  return `<div class="pd-hint" role="img" aria-label="本页库存行数分布图（仅统计本页）" style="margin-top:8px">📊 本页行数分布（仅统计本页）</div>
    <ul role="list" style="list-style:none;padding:0 8px;margin:4px 0 8px">${bars}</ul>`;
}

/* 空结果提示（用于结果区） */
function iarDynEmptyHtml() {
  return '<div class="empty" style="margin:8px 0">没有符合条件的库存行（可放宽仓库 / 商品 / 日期筛选）。</div>';
}

/* 错误提示（授权 / 未登录 / 无效请求 / 网络失败分别可见，且不暴露任何数据） */
function iarDynErrorHtml(kind, message) {
  const labels = {
    forbidden: '权限不足',
    unauthorized: '未登录 / 登录已过期',
    invalid: '请求无效',
    empty: '导出内容为空',
    network: '网络请求失败',
    error: '预览失败',
  };
  return `<div class="pd-hint" style="color:#b91c1c;background:#fef2f2;border-color:#fecaca">
      <b>${iarDynEsc(labels[kind] || '预览失败')}</b>：${iarDynEsc(message || '')}</div>`;
}

/* 预览结果（口径 / 边界 / 免责文案 + 汇总 + 空结果 + 表格） */
function iarDynResultHtml(view) {
  const readOnly = view && view.readOnlyText ? `<div class="pd-hint">${iarDynEsc(view.readOnlyText)}</div>` : '';
  const boundary = view && view.boundaryText ? `<div class="pd-hint">${iarDynEsc(view.boundaryText)}</div>` : '';
  const disclaimer = view && view.disclaimerText ? `<div class="pd-hint" style="color:#64748b">${iarDynEsc(view.disclaimerText)}</div>` : '';
  const summary = view
    ? `<div class="text-muted" style="margin:6px 0">共 ${view.total} 行 · 第 ${view.page} 页 · 每页 ${view.pageSize} 行 · 币种 ${iarDynEsc(view.costCurrency || '')}</div>`
    : '';
  const groups = iarDynGroupsHtml(view);
  const empty = view && (!view.rows || view.rows.length === 0) ? iarDynEmptyHtml() : '';
  return `${readOnly}${boundary}${disclaimer}${summary}${groups}${empty}${iarDynTableHtml(view)}`;
}

/* 字段选择器：仅由目录白名单渲染为复选框，无自由填写的字段名 */
function iarDynFieldChooserHtml(fields, selectedKeys) {
  const selected = new Set(selectedKeys || []);
  return (fields || []).map(f => {
    const checked = selected.has(f.key) ? 'checked' : '';
    return `<label style="display:inline-flex;align-items:center;gap:4px;margin:3px 6px 3px 0;padding:2px 8px;border:1px solid #e2e8f0;border-radius:6px;background:#f8fafc;cursor:pointer">
        <input type="checkbox" name="iar-dyn-field" value="${iarDynEsc(f.key)}" ${checked} onchange="iarDynSyncSelection()">
        <span>${iarDynEsc(f.label || f.key)}</span></label>`;
  }).join('');
}

/* 业务码 → 错误态分类 */
function iarDynKindOfCode(code) {
  if (code === 2002) return 'forbidden';
  if (code === 2000 || code === 2003) return 'unauthorized';
  if (code === 5000) return 'error';
  return 'invalid';
}

/* CSV 单元格：公式前导文本（= + - @ 制表 / 回车）前加单引号转义，再按 CSV 规则包裹双引号并转义内部双引号 */
function iarDynCsvCell(v) {
  let s = v === null || v === undefined ? '' : String(v);
  if (/^[=+\-@\t\r]/.test(s)) s = "'" + s;
  return '"' + s.replace(/"/g, '""') + '"';
}

/* 当前预览页 CSV：表头与数据行都按返回列（= 选定字段顺序）排列，未知值（未知成本金额）原样保留为「未知」 */
function iarDynCsv(view) {
  const cols = (view && view.columns) || [];
  const rows = (view && view.rows) || [];
  const lines = [cols.map(c => iarDynCsvCell(c.label || c.key)).join(',')];
  for (const r of rows) {
    lines.push(cols.map(c => iarDynCsvCell(iarDynCellText(r[c.key], c))).join(','));
  }
  return lines.join('\r\n');
}

/* 轻量请求封装：返回完整 ApiResponse 信封（保留 code），网络异常抛给调用方 */
async function iarDynRequest(path, method = 'GET', body = null) {
  const headers = { 'Content-Type': 'application/json' };
  const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
  if (token) headers['Authorization'] = 'Bearer ' + token;
  const opts = { method, headers };
  if (body) opts.body = JSON.stringify(body);
  const resp = await fetch(path, opts);
  return await resp.json();
}

function iarDynLoadingHtml() {
  return '<div class="pd-hint" style="text-align:center;color:#64748b">正在预览（只读查询）…</div>';
}

function iarDynRenderResult(html) {
  const el = document.getElementById('iar-designer-result');
  if (el) el.innerHTML = html;
}

/* 同步勾选状态到 selectedKeys（复选框 onchange） */
function iarDynSyncSelection() {
  const boxes = document.querySelectorAll('input[name="iar-dyn-field"]');
  IAR_DYN.selectedKeys = Array.from(boxes).filter(b => b.checked).map(b => b.value);
}

function iarDynToggleAll(checked) {
  const boxes = document.querySelectorAll('input[name="iar-dyn-field"]');
  IAR_DYN.selectedKeys = [];
  boxes.forEach(b => { b.checked = checked; if (checked) IAR_DYN.selectedKeys.push(b.value); });
}

/* 读取当前字段 / 筛选 / 分页状态（预览与分页复用，单一来源；筛选复用既有 iar-* 控件） */
function iarDynBuildState(page) {
  const val = id => { const el = document.getElementById(id); return el ? el.value.trim() : ''; };
  return {
    catalogFields: IAR_DYN.fields,
    selectedKeys: IAR_DYN.selectedKeys,
    warehouseId: val('iar-warehouse'),
    productId: val('iar-product'),
    asOfDate: val('iar-asof'),
    pageSize: val('iar-pagesize'),
    groupBy: val('iar-dyn-group') || 'none',
    page: page || IAR_DYN.page || 1,
    maxPageSize: IAR_DYN.catalog && IAR_DYN.catalog.maxPageSize ? IAR_DYN.catalog.maxPageSize : 200,
  };
}

/* 预览：组装有界请求 → POST → 安全渲染列名与单元格；授权 / 无效 / 空 / 网络失败均可见 */
async function iarDynPreview(page) {
  const state = iarDynBuildState(page);
  const req = iarDynBuildRequest(state);
  IAR_DYN.page = req.page;

  iarDynRenderResult(iarDynLoadingHtml());

  try {
    const resp = await iarDynRequest('/api/dynamic-inventory-aging-report', 'POST', req);
    if (resp.code === 0) {
      IAR_DYN.view = resp.data;
      IAR_DYN.page = resp.data.page;
      iarDynRenderResult(iarDynResultHtml(resp.data));
    } else if (resp.code === 2000 || resp.code === 2003) {
      if (typeof logout === 'function') logout();
      iarDynRenderResult(iarDynErrorHtml('unauthorized', resp.message));
    } else {
      iarDynRenderResult(iarDynErrorHtml(iarDynKindOfCode(resp.code), resp.message));
    }
  } catch (err) {
    iarDynRenderResult(iarDynErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 翻页（有界：最小第 1 页） */
function iarDynPage(delta) {
  const page = (IAR_DYN.view ? IAR_DYN.view.page : IAR_DYN.page) + delta;
  if (page < 1) return;
  iarDynPreview(page);
}

/* 加载字段目录（需登录 + 库存查询菜单授权；授权 / 网络失败 fail closed，不渲染任何字段） */
async function loadIarDesignerCatalog() {
  try {
    const resp = await iarDynRequest('/api/dynamic-inventory-aging-report');
    if (resp.code === 2000 || resp.code === 2003) {
      if (typeof logout === 'function') logout();
      iarDynRenderResult(iarDynErrorHtml('unauthorized', resp.message));
      return;
    }
    if (resp.code !== 0) {
      iarDynRenderResult(iarDynErrorHtml(iarDynKindOfCode(resp.code), resp.message));
      return;
    }
    IAR_DYN.catalog = resp.data;
    IAR_DYN.fields = (resp.data && resp.data.fields) || [];
    IAR_DYN.selectedKeys = IAR_DYN.fields.map(f => f.key);
    const el = document.getElementById('iar-designer-fields');
    if (el) el.innerHTML = iarDynFieldChooserHtml(IAR_DYN.fields, IAR_DYN.selectedKeys);
  } catch (err) {
    iarDynRenderResult(iarDynErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 导出当前预览页为 CSV（选定列顺序 + 公式转义 + 未知值保留），不下载无数据的空表 */
function exportIarDesignerCsv() {
  if (!IAR_DYN.view || !IAR_DYN.view.rows || IAR_DYN.view.rows.length === 0) {
    toast('暂无可导出的预览数据（请先预览）', 'warning');
    return;
  }
  const csv = iarDynCsv(IAR_DYN.view);
  const blob = new Blob(['\uFEFF' + csv], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = '库存库龄字段设计器_当前页.csv';
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
  toast('CSV 已导出（当前页 · 选定列）', 'success');
}

/* Node 单测导出（浏览器中 module 为 undefined，自动跳过） */
if (typeof module !== 'undefined' && module.exports) {
  module.exports = {
    IAR_DYN_BUCKET_KEYS,
    IAR_DYN_GROUP_OPTS,
    iarDynEsc,
    iarDynSelectFields,
    iarDynBuildRequest,
    iarDynIsCostKey,
    iarDynCellText,
    iarDynRenderCell,
    iarDynTableHtml,
    iarDynGroupsHtml,
    iarDynEmptyHtml,
    iarDynErrorHtml,
    iarDynResultHtml,
    iarDynFieldChooserHtml,
    iarDynKindOfCode,
    iarDynCsvCell,
    iarDynCsv,
    iarDynRequest,
    iarDynLoadingHtml,
    iarDynRenderResult,
    iarDynSyncSelection,
    iarDynToggleAll,
    iarDynBuildState,
    iarDynPreview,
    iarDynPage,
    loadIarDesignerCatalog,
    exportIarDesignerCsv,
  };
}

/* 导出当前页为 CSV（与报表中心同一套口径：未知值按表格文本原样导出，不回落为 0） */
function exportIarCsv() {
  const table = document.querySelector('#iar-table table');
  if (!table) { toast('暂无可导出数据', 'warning'); return; }
  const rows = Array.from(table.querySelectorAll('tr'));
  const csv = rows.map(tr => Array.from(tr.querySelectorAll('th,td'))
    .map(td => `"${td.textContent.replace(/"/g, '""')}"`).join(',')).join('\n');
  const blob = new Blob(['\uFEFF' + csv], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = '库存库龄与成本估值报表.csv';
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
  toast('CSV 已导出', 'success');
}

