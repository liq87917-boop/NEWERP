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

