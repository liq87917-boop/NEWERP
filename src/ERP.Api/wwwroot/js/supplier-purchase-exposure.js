/* ============ 供应商采购敞口报表（ERP-031：只读派生；按「供应商 + 币种」分组，不同币种绝不合并） ============
   口径与后端 SupplierPurchaseExposureSemantics 一一对应：
   - 订单金额取采购订单已落库总额；已结算 / 未结算金额复用「执行进度 / 财务核对」的同一套权威引用规则（付款单 → 货款申请单 → 归属销售订单）；
   - 链接不唯一或无可用引用时金额显示「未知」（绝不回落为 0）；这些订单金额只作「未链接敞口」单列；
   - 本页是运营敞口视图，不是应付账款台账 / 账龄表，界面必须明确区分（见页脚声明）。
   筛选与数据全部走既有只读接口 GET /api/purchase-orders/supplier-exposure?… */

/* 链接状态文案（与后端 PurchaseOrderProgress / SupplierPurchaseExposureSemantics 常量一一对应） */
const SPE_LINK_LABELS = {
  linked: '链接可用',
  ambiguous: '链接不唯一（金额未知）',
  unavailable: '无可用链接（金额未知）',
};
/* 收货状态文案（与后端 PurchaseOrderProgress 常量一一对应；unknown = 本次派生命中上限） */
const SPE_RECEIPT_LABELS = {
  none: '未收货',
  partial: '部分收货',
  complete: '已收齐',
  over_received: '超收',
  unknown: '未知（超出派生上限）',
};

/* 工具栏入口（采购订单页）：渲染独立报表页（只读，不落库） */
function openSupplierPurchaseExposureReport() {
  CURRENT_PAGE_CODE = 'supplier-purchase-exposure';
  document.getElementById('header-title').textContent = '供应商采购敞口报表';
  document.getElementById('content').innerHTML = `
    <div class="page-hero report-hero">
      <h2>🏭 供应商采购敞口报表</h2>
      <p>按供应商 + 币种聚合采购订单金额 · 已结算金额复用执行进度 / 财务核对的权威引用口径（只读派生，未知不推断）</p>
    </div>

    <div class="pd-hint" style="margin-bottom:10px">
      ⚠️ 运营敞口视图：<b>不是</b>应付账款台账，也<b>不是</b>账龄表 —— 不创建发票 / 应付记录、不推算账期与到期日；
      「未链接敞口」只是尚未按既有引用归属到订单的订单金额，不得当作应付余额或据以付款。
    </div>

    <div class="toolbar">
      <div class="toolbar-left" style="flex-wrap:wrap;gap:8px;align-items:center">
        <label>供应商 <select id="spe-supplier" style="min-width:170px"><option value="">全部供应商</option></select></label>
        <label>币种 <select id="spe-currency" style="min-width:130px"><option value="">全部币种</option></select></label>
        <label>订单日期 <input type="date" id="spe-date-from" style="width:140px"> 至
          <input type="date" id="spe-date-to" style="width:140px"></label>
        <label>链接状态 <select id="spe-link-status" style="min-width:180px">
          <option value="">全部</option>
          <option value="linked">链接可用</option>
          <option value="ambiguous">链接不唯一（金额未知）</option>
          <option value="unavailable">无可用链接（金额未知）</option>
        </select></label>
        <label>关键字 <input type="text" id="spe-keyword" style="width:190px" placeholder="采购单号 / 合同号 / 归属销售订单号"></label>
        <label>每页 <input type="number" id="spe-pagesize" value="50" min="1" max="200" style="width:70px"></label>
      </div>
      <div class="toolbar-actions">
        <button class="btn btn-primary" onclick="loadSupplierPurchaseExposure(1)">查询</button>
        <button class="btn btn-neutral" onclick="openSupplierPurchaseExposureDesigner()" title="按 ERP-148 白名单字段目录选择证据列，复用工作台当前筛选预览当前授权页并导出所选列 CSV">🎛 字段设计器</button>
        <button class="btn btn-neutral" onclick="exportSpeCsv()" title="导出本页订单明细为 CSV">📤 导出 CSV</button>
      </div>
    </div>

    <div class="kpi-grid" id="spe-kpi"></div>
    <div class="table-wrap" id="spe-currency-table"></div>
    <div class="table-wrap" id="spe-group-table"></div>
    <div class="table-wrap" id="spe-order-table">
      <div class="skeleton-row"></div><div class="skeleton-row"></div>
    </div>
    <div class="pd-hint" id="spe-rule"></div>
    <div class="pagination" id="spe-pagination"></div>`;
  loadSpeCurrencies();
  loadSpeSuppliers();
  loadSupplierPurchaseExposure(1);
}

/* 币种下拉：与单据币种同一枚举口径（枚举名），不同币种绝不合并汇总 */
function loadSpeCurrencies() {
  const sel = document.getElementById('spe-currency');
  if (!sel) return;
  (typeof CURRENCY_NAME_OPTS !== 'undefined' ? CURRENCY_NAME_OPTS : []).forEach(o => {
    const opt = document.createElement('option');
    opt.value = o.value;
    opt.textContent = o.label;
    sel.appendChild(opt);
  });
}

/* 供应商下拉：既有基础资料接口；失败不阻断报表（仍可留空或只用其它筛选） */
async function loadSpeSuppliers() {
  try {
    const data = await api('/api/base/suppliers?page=1&pageSize=200');
    const sel = document.getElementById('spe-supplier');
    if (!sel) return;
    (data.items || []).forEach(s => {
      const opt = document.createElement('option');
      opt.value = s.id;
      opt.textContent = s.supplierName || ('供应商 ' + s.id);
      sel.appendChild(opt);
    });
  } catch (e) { /* 忽略：供应商下拉失败不影响报表查询 */ }
}

function speVal(id) {
  const el = document.getElementById(id);
  return el ? String(el.value || '').trim() : '';
}

function speQuery(page) {
  const q = new URLSearchParams();
  if (speVal('spe-supplier')) q.set('supplierId', speVal('spe-supplier'));
  if (speVal('spe-currency')) q.set('currency', speVal('spe-currency'));
  if (speVal('spe-date-from')) q.set('orderDateFrom', speVal('spe-date-from'));
  if (speVal('spe-date-to')) q.set('orderDateTo', speVal('spe-date-to'));
  if (speVal('spe-link-status')) q.set('linkStatus', speVal('spe-link-status'));
  if (speVal('spe-keyword')) q.set('keyword', speVal('spe-keyword'));
  q.set('page', page || 1);
  q.set('pageSize', speVal('spe-pagesize') || '50');
  return q.toString();
}

async function loadSupplierPurchaseExposure(page) {
  const el = document.getElementById('spe-order-table');
  if (!el) return;
  el.innerHTML = '<div class="skeleton-row"></div><div class="skeleton-row"></div>';
  try {
    const data = await api('/api/purchase-orders/supplier-exposure?' + speQuery(page));
    speRenderKpi(data);
    speRenderCurrencyTable(data);
    speRenderGroupTable(data);
    speRenderOrderTable(data);
    speRenderPagination(data);
    const rule = document.getElementById('spe-rule');
    if (rule) {
      rule.textContent = '口径：' + (data.rule || '') + ' ' + (data.scopeNote || '') + ' ' +
        (data.payableDisclaimer || '');
    }
  } catch (e) {
    el.innerHTML = `<div class="empty"><div style="font-size:48px">⚠️</div><div>报表加载失败：${escapeHtml(e.message)}</div></div>`;
  }
}

/* 未知（null）= 无可用链接 / 命中等派生命中上限；显示「未知」而不是 0 */
function speMoney(v) { return v === null || v === undefined ? '未知' : fmtMoney(v); }
function speQty(v) { return v === null || v === undefined ? '未知' : fmtMoney(v); }

function speLinkHtml(status) {
  const cls = status === 'linked' ? 'status-success'
    : (status === 'ambiguous' ? 'status-warning' : 'status-neutral');
  return `<span class="status ${cls}">${escapeHtml(SPE_LINK_LABELS[status] || status || '')}</span>`;
}

function speReceiptHtml(status) {
  const cls = status === 'complete' ? 'status-success'
    : (status === 'over_received' ? 'status-danger'
      : (status === 'unknown' ? 'status-warning' : 'status-neutral'));
  return `<span class="status ${cls}">${escapeHtml(SPE_RECEIPT_LABELS[status] || status || '')}</span>`;
}

function speRenderKpi(data) {
  const el = document.getElementById('spe-kpi');
  if (!el) return;
  el.innerHTML = `
    <div class="kpi-card cargo">
      <div class="kpi-label">符合筛选的采购订单</div>
      <div class="kpi-value">${data.total}<span class="unit">张</span></div>
      <div class="kpi-delta flat">本页 ${data.pageOrderCount} 张 · 第 ${data.page}/${data.totalPages} 页 · 每页 ${data.pageSize}</div>
    </div>
    <div class="kpi-card gold">
      <div class="kpi-label">本页链接状态（可用 / 不唯一 / 无引用）</div>
      <div class="kpi-value">${data.linkedOrderCount} / ${data.ambiguousOrderCount} / ${data.unlinkedOrderCount}<span class="unit">张</span></div>
      <div class="kpi-delta flat">不唯一与无引用订单金额只作「未链接敞口」单列，不作为应付余额</div>
    </div>
    <div class="kpi-card ocean">
      <div class="kpi-label">本页币种数（不跨币种汇总）</div>
      <div class="kpi-value">${(data.currencies || []).length}<span class="unit">种</span></div>
      <div class="kpi-delta flat">收货数量未知 ${data.receiptUnknownCount} 张（本页入库单超过单次派生上限）</div>
    </div>`;
}

/* 本页按币种汇总：同一币种内汇总，不同币种分别成行（不做汇率换算） */
function speRenderCurrencyTable(data) {
  const el = document.getElementById('spe-currency-table');
  if (!el) return;
  const rows = (data.currencies || []).map(c => `<tr>
      <td><b>${escapeHtml(c.currency)}</b></td>
      <td class="text-right">${c.supplierCount}</td>
      <td class="text-right">${c.orderCount}</td>
      <td class="text-right">${fmtMoney(c.orderedAmount)}</td>
      <td>${c.linkedOrderCount} / ${c.ambiguousOrderCount} / ${c.unlinkedOrderCount}</td>
      <td class="text-right">${speMoney(c.settledAmount)}</td>
      <td class="text-right">${speMoney(c.outstandingAmount)}</td>
      <td class="text-right">${fmtMoney(c.unlinkedOrderedAmount)}</td>
    </tr>`).join('');
  el.innerHTML = `<table><thead><tr>
      <th>币种</th><th class="text-right">供应商数</th><th class="text-right">订单数</th>
      <th class="text-right">订单金额</th><th>链接可用 / 不唯一 / 无引用（张）</th>
      <th class="text-right">已结算（仅链接可用）</th><th class="text-right">未结算（仅链接可用）</th>
      <th class="text-right">未链接敞口（不作为应付）</th>
    </tr></thead>
    <tbody>${rows || '<tr><td colspan="8" class="empty">本页没有订单：没有可汇总的币种</td></tr>'}</tbody></table>`;
}

/* 「供应商 + 币种」分组：只有同分组才汇总金额；未链接敞口单列 */
function speRenderGroupTable(data) {
  const el = document.getElementById('spe-group-table');
  if (!el) return;
  const rows = (data.groups || []).map(g => `<tr>
      <td>${escapeHtml(g.supplierName || ('供应商 ' + g.supplierId))}</td>
      <td><b>${escapeHtml(g.currency)}</b></td>
      <td class="text-right">${g.orderCount}</td>
      <td class="text-right">${fmtMoney(g.orderedAmount)}</td>
      <td>${g.linkedOrderCount} / ${g.ambiguousOrderCount} / ${g.unlinkedOrderCount}</td>
      <td class="text-right">${speMoney(g.settledAmount)}</td>
      <td class="text-right">${speMoney(g.outstandingAmount)}</td>
      <td class="text-right">${fmtMoney(g.unlinkedOrderedAmount)}</td>
      <td class="text-right">${g.overSettledOrderCount}</td>
    </tr>`).join('');
  el.innerHTML = `<table><thead><tr>
      <th>供应商</th><th>币种</th><th class="text-right">订单数</th><th class="text-right">订单金额</th>
      <th>链接可用 / 不唯一 / 无引用（张）</th>
      <th class="text-right">已结算（仅链接可用）</th><th class="text-right">未结算（仅链接可用）</th>
      <th class="text-right">未链接敞口（不作为应付）</th><th class="text-right">超付单数</th>
    </tr></thead>
    <tbody>${rows || '<tr><td colspan="9" class="empty">没有符合筛选条件的「供应商 + 币种」分组</td></tr>'}</tbody></table>`;
}


/* 本页订单明细（收货数量 / 结算金额一律复用执行进度口径；未知显示「未知」） */
function speRenderOrderTable(data) {
  const el = document.getElementById('spe-order-table');
  if (!el) return;
  const orders = [];
  (data.groups || []).forEach(g => (g.orders || []).forEach(o => orders.push(o)));
  const rows = orders.map(o => `<tr>
      <td>${escapeHtml(o.orderNo)}</td>
      <td>${fmtDate(o.orderDate)}</td>
      <td>${statusHtml(o.status)}</td>
      <td>${escapeHtml(o.supplierName || ('供应商 ' + o.supplierId))}</td>
      <td>${escapeHtml(o.currency)}</td>
      <td class="text-right">${fmtMoney(o.orderedAmount)}</td>
      <td title="${escapeHtml(o.linkReason || '')}">${speLinkHtml(o.linkStatus)}</td>
      <td class="text-right">${speMoney(o.settledAmount)}</td>
      <td class="text-right">${speMoney(o.outstandingAmount)}</td>
      <td class="text-right">${speQty(o.receivedQuantity)} / ${speQty(o.orderedQuantity)}</td>
      <td class="text-right">${speQty(o.pendingQuantity)}</td>
      <td>${speReceiptHtml(o.receiptStatus)}</td>
      <td>${escapeHtml(o.owningSalesOrderNo || '（未关联）')}</td>
      <td>${escapeHtml(o.recordedSettlementProgress || '（未登记）')}</td>
      <td>${escapeHtml(o.note || '')}</td>
    </tr>`).join('');
  el.innerHTML = `<table><thead><tr>
      <th>采购单号</th><th>订单日期</th><th>状态</th><th>供应商</th><th>币种</th>
      <th class="text-right">订单金额</th><th>链接状态</th>
      <th class="text-right">已结算</th><th class="text-right">未结算</th>
      <th class="text-right">已收 / 已订</th><th class="text-right">待审</th><th>收货状态</th>
      <th>归属销售订单</th><th>人工结算进度</th><th>说明</th>
    </tr></thead>
    <tbody>${rows || '<tr><td colspan="15" class="empty">没有符合筛选条件的采购订单（可放宽供应商 / 币种 / 日期 / 链接状态筛选）</td></tr>'}</tbody></table>`;
}

function speRenderPagination(data) {
  const el = document.getElementById('spe-pagination');
  if (!el) return;
  const page = data.page || 1;
  const totalPages = data.totalPages || 1;
  el.innerHTML = `
    <button class="btn btn-neutral btn-sm" ${page <= 1 ? 'disabled' : ''} onclick="loadSupplierPurchaseExposure(${page - 1})">上一页</button>
    <span style="margin:0 10px">第 ${page} / ${totalPages} 页（共 ${data.total} 张订单）</span>
    <button class="btn btn-neutral btn-sm" ${page >= totalPages ? 'disabled' : ''} onclick="loadSupplierPurchaseExposure(${page + 1})">下一页</button>`;
}

/* 导出本页订单明细为 CSV（与表格同一套口径：未知值按表格文本原样导出，不回落为 0） */
function exportSpeCsv() {
  const table = document.querySelector('#spe-order-table table');
  if (!table) { toast('暂无可导出数据', 'warning'); return; }
  const rows = Array.from(table.querySelectorAll('tr'));
  const csv = rows.map(tr => Array.from(tr.querySelectorAll('th,td'))
    .map(td => `"${td.textContent.replace(/"/g, '""')}"`).join(',')).join('\n');
  const blob = new Blob(['\uFEFF' + csv], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = '供应商采购敞口报表.csv';
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
  toast('CSV 已导出', 'success');
}

/* ============ 供应商采购敞口 · 证据字段设计器（ERP-149：只读、有界的前端字段 / 筛选设计器） ============
   口径与后端 ERP-148（DynamicSupplierExposureReportController / DynamicSupplierExposureReportRules）一一对应：
   - 字段选择器只由 GET /api/supplier-purchase-exposure/report 返回的有限白名单目录渲染，绝无自由填写的字段名或 SQL；
   - 预览复用工作台当前筛选（供应商 / 币种 / 订单日期 / 链接状态 / 关键字 / 每页），并 POST /api/supplier-purchase-exposure/report，
     只发送「白名单字段 + 当前筛选 + 有界分页（pageSize 1~200）」，按请求顺序渲染返回的列名与单元格；
   - 链接不唯一 / 无引用（ambiguous / unavailable）、收货数量未知（receiptStatus unknown / 数量 null）与未知结算金额
     （settledAmount / outstandingAmount / submittedAmount 为 null）一律显示「未知」，绝不回落为 0；不同币种分别成行、绝不合并；
   - 全程只读：不写库、不迁移、不执行任意 SQL；授权 / 无效请求 / 空结果 / 网络失败都在界面可见，且不暴露范围外数据。 */

const SPE_DESIGNER_API = '/api/supplier-purchase-exposure/report';

let SPE_DESIGNER = {
  catalog: null,      // GET /api/supplier-purchase-exposure/report 返回的目录 DTO
  fields: [],         // 目录字段（白名单）
  selectedKeys: [],   // 当前勾选的字段键（默认全选）
  view: null,         // 最近一次预览结果
};

/* HTML 转义（本地独立实现，避免依赖全局 escapeHtml 的加载顺序） */
function speDesEsc(v) {
  return String(v == null ? '' : v).replace(/[&<>"']/g, c => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}

/* 规范化选定字段（fail closed）：只保留目录白名单内的键、去重、保持请求顺序；未知键丢弃（绝不进入请求） */
function speDesSelectFields(catalogFields, selectedKeys) {
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

/* 组装有界预览请求体：字段只来自目录、分页有界、筛选仅复用工作台当前筛选，绝不接受任意字段名或 SQL */
function speDesBuildRequest(state) {
  const fields = speDesSelectFields(state.catalogFields, state.selectedKeys);
  const page = Math.max(1, Math.floor(Number(state.page) || 1));
  const maxPageSize = Number(state.maxPageSize) || 200;
  let pageSize = Math.floor(Number(state.pageSize));
  if (!Number.isFinite(pageSize)) pageSize = 50;
  pageSize = Math.max(1, Math.min(maxPageSize, pageSize));

  const req = { fields, page, pageSize };
  const supplierId = Number(state.supplierId);
  if (Number.isFinite(supplierId) && supplierId > 0) req.supplierId = supplierId;
  if (state.currency) req.currency = state.currency;
  if (state.linkStatus) req.linkStatus = state.linkStatus;
  if (state.keyword) req.keyword = state.keyword;

  const orderDateFrom = state.orderDateFrom ? String(state.orderDateFrom).slice(0, 10) : null;
  const orderDateTo = state.orderDateTo ? String(state.orderDateTo).slice(0, 10) : null;
  if (orderDateFrom) req.orderDateFrom = orderDateFrom;
  if (orderDateTo) req.orderDateTo = orderDateTo;
  return req;
}

/* 单元格纯文本：null / undefined = 「未知」（未知结算 / 收货 / 链接状态，绝不回落为 0）；布尔显示 是/否；日期截断到日；
   链接状态 / 收货状态命中即用中文口径文案（与 ERP-031 界面同源），未知时保持「未知」。 */
function speDesCellText(value, field) {
  const key = (field && field.key) || '';
  const dataType = (field && field.dataType) || 'text';
  if (value === null || value === undefined) return '未知';
  if (dataType === 'boolean') return (value === true || value === 'true' || value === 1 || value === '1') ? '是' : '否';
  if (dataType === 'date') return String(value).slice(0, 10);
  if (key === 'linkStatus') return SPE_LINK_LABELS[value] || String(value);
  if (key === 'receiptStatus') return SPE_RECEIPT_LABELS[value] || String(value);
  return String(value);
}

/* 单元格 HTML（转义后安全渲染） */
function speDesRenderCell(value, field) {
  return speDesEsc(speDesCellText(value, field));
}

/* CSV 单元格：未知值保留「未知」，并转义以 = + - @ 或制表符 / 回车开头的文本（防公式注入） */
function speDesCsvCell(value, field) {
  let text = speDesCellText(value, field);
  if (/^[-=+@\t\r]/.test(text)) text = "'" + text;
  return text;
}

/* 当前预览页的所选列 CSV（纯字符串）：表头为返回的列名，行内仅选定字段；未知值保留、公式首字符转义、引号转义、CRLF + BOM */
function speDesCsv(view) {
  const cols = (view && view.columns) || [];
  const rows = (view && view.rows) || [];
  const q = (v) => '"' + String(v).replace(/"/g, '""') + '"';
  const header = cols.map(c => q(c.label || c.key)).join(',');
  const body = rows.map(r => cols.map(c => q(speDesCsvCell(r[c.key], c))).join(',')).join('\r\n');
  const lines = [header];
  if (body) lines.push(body);
  return '\uFEFF' + lines.join('\r\n');
}

/* 结果表格 HTML：表头为返回的列名、单元格为返回的选定字段值，全部经转义 */
function speDesTableHtml(view) {
  const cols = (view && view.columns) || [];
  const rows = (view && view.rows) || [];
  if (!cols.length) return '';
  const align = c => (c.dataType === 'number' || c.dataType === 'boolean') ? ' class="text-right"' : '';
  const head = cols.map(c => `<th${align(c)}>${speDesEsc(c.label || c.key)}</th>`).join('');
  const body = rows.map(r =>
    `<tr>${cols.map(c => `<td${align(c)}>${speDesRenderCell(r[c.key], c)}</td>`).join('')}</tr>`).join('');
  const emptyRow = `<tr><td colspan="${cols.length}" class="empty">本页没有符合条件的采购订单敞口证据</td></tr>`;
  const prevDisabled = !view || view.page <= 1 ? ' disabled' : '';
  const nextDisabled = !view || view.page >= view.totalPages ? ' disabled' : '';
  const paging = `<div style="display:flex;justify-content:space-between;align-items:center;margin-top:8px">
      <span class="text-muted">第 ${view ? view.page : 1} 页 / 共 ${view ? view.totalPages : 0} 页</span>
      <div>
        <button class="btn btn-neutral btn-sm" onclick="speDesPage(-1)"${prevDisabled}>← 上一页</button>
        <button class="btn btn-neutral btn-sm" onclick="speDesPage(1)"${nextDisabled}>下一页 →</button>
      </div></div>`;
  return `<div class="table-wrap" style="margin-top:8px"><table><thead><tr>${head}</tr></thead><tbody>${body || emptyRow}</tbody></table></div>${paging}`;
}

/* 空结果提示 */
function speDesEmptyHtml() {
  return '<div class="empty" style="margin:8px 0">没有符合条件的采购订单敞口证据（当前账号数据范围内的只读快照）。</div>';
}

/* 错误提示（授权 / 未登录 / 无效请求 / 空导出 / 网络失败分别可见，且不暴露任何数据） */
function speDesErrorHtml(kind, message) {
  const labels = {
    forbidden: '权限不足',
    unauthorized: '未登录 / 登录已过期',
    invalid: '请求无效',
    empty: '导出内容为空',
    network: '网络请求失败',
    error: '预览失败',
  };
  return `<div class="pd-hint" style="color:#b91c1c;background:#fef2f2;border-color:#fecaca">
      <b>${speDesEsc(labels[kind] || '预览失败')}</b>：${speDesEsc(message || '')}</div>`;
}

/* 预览结果（口径 / 边界 / 免责文案 + 汇总 + 空结果 + 表格） */
function speDesResultHtml(view) {
  const readOnly = view && view.readOnlyText ? `<div class="pd-hint">${speDesEsc(view.readOnlyText)}</div>` : '';
  const boundary = view && view.boundaryText ? `<div class="pd-hint">${speDesEsc(view.boundaryText)}</div>` : '';
  const disclaimer = view && view.disclaimerText ? `<div class="pd-hint" style="color:#64748b">${speDesEsc(view.disclaimerText)}</div>` : '';
  const summary = view
    ? `<div class="text-muted" style="margin:6px 0">共 ${view.total} 条 · 第 ${view.page} 页 · 每页 ${view.pageSize} 条 · 本页 ${(view.rows || []).length} 行证据</div>`
    : '';
  const empty = view && (!view.rows || view.rows.length === 0) ? speDesEmptyHtml() : '';
  return `${readOnly}${boundary}${disclaimer}${summary}${empty}${speDesTableHtml(view)}`;
}

/* 字段选择器：仅由目录白名单渲染为复选框，无自由填写的字段名 */
function speDesFieldChooserHtml(fields, selectedKeys) {
  const selected = new Set(selectedKeys || []);
  return (fields || []).map(f => {
    const checked = selected.has(f.key) ? 'checked' : '';
    return `<label style="display:inline-flex;align-items:center;gap:4px;margin:3px 6px 3px 0;padding:2px 8px;border:1px solid #e2e8f0;border-radius:6px;background:#f8fafc;cursor:pointer">
        <input type="checkbox" name="spe-des-field" value="${speDesEsc(f.key)}" ${checked} onchange="speDesSyncSelection()">
        <span>${speDesEsc(f.label || f.key)}</span></label>`;
  }).join('');
}

/* 当前筛选摘要（只读展示工作台当前筛选，预览与导出复用这些值） */
function speDesFilterSummaryHtml() {
  const rows = [];
  const add = (label, v) => { if (v) rows.push(`<div><span class="text-muted">${label}</span>：<b>${speDesEsc(v)}</b></div>`); };
  add('供应商', speVal('spe-supplier'));
  add('币种', speVal('spe-currency'));
  add('订单日期开始', speVal('spe-date-from'));
  add('订单日期结束', speVal('spe-date-to'));
  add('链接状态', SPE_LINK_LABELS[speVal('spe-link-status')] || speVal('spe-link-status'));
  add('关键字', speVal('spe-keyword'));
  add('每页', speVal('spe-pagesize') || '50');
  return rows.length ? rows.join('') : '<div class="text-muted">未设置筛选（默认全部）。</div>';
}

/* 轻量请求封装：返回完整 ApiResponse 信封（保留 code），网络异常抛给调用方 */
async function speDesRequest(path, method = 'GET', body = null) {
  const headers = { 'Content-Type': 'application/json' };
  const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
  if (token) headers['Authorization'] = 'Bearer ' + token;
  const opts = { method, headers };
  if (body) opts.body = JSON.stringify(body);
  const resp = await fetch(path, opts);
  return await resp.json();
}

/* 业务码 → 错误态分类 */
function speDesKindOfCode(code) {
  if (code === 2002) return 'forbidden';
  if (code === 2000 || code === 2003) return 'unauthorized';
  return 'invalid';
}

/* 目录加载失败 / 授权失败时的整页错误态（带关闭） */
function speDesErrorModalHtml(kind, message) {
  return `<div class="modal modal-lg">
    <h3>🎛 供应商采购敞口 · 证据字段设计器</h3>
    ${speDesErrorHtml(kind, message)}
    <div class="modal-footer"><button class="btn btn-neutral" onclick="closeModal()">关闭</button></div>
  </div>`;
}

function speDesLoadingHtml() {
  return '<div class="pd-hint" style="text-align:center;color:#64748b">正在预览（只读查询）…</div>';
}

function speDesRenderResult(html) {
  const el = document.getElementById('spe-des-result');
  if (el) el.innerHTML = html;
}

/* 同步勾选状态到 selectedKeys（复选框 onchange） */
function speDesSyncSelection() {
  const boxes = document.querySelectorAll('input[name="spe-des-field"]');
  SPE_DESIGNER.selectedKeys = Array.from(boxes).filter(b => b.checked).map(b => b.value);
}

function speDesToggleAll(checked) {
  const boxes = document.querySelectorAll('input[name="spe-des-field"]');
  SPE_DESIGNER.selectedKeys = [];
  boxes.forEach(b => { b.checked = checked; if (checked) SPE_DESIGNER.selectedKeys.push(b.value); });
}

/* 渲染设计器主体（字段选择器 + 当前筛选 + 操作按钮 + 结果区） */
function speDesRender() {
  const modal = document.getElementById('modal');
  if (!modal) return;
  const selected = SPE_DESIGNER.selectedKeys;
  modal.innerHTML = `
    <div class="modal modal-lg" style="max-width:1100px">
      <h3>🎛 供应商采购敞口 · 证据字段设计器（只读预览）</h3>
      <div class="pd-hint" style="margin-bottom:10px">
        只从 ERP-148 有限白名单字段目录勾选列，复用工作台当前筛选与有界分页预览当前账号可见的采购订单敞口；未知结算 / 收货值保持「未知」，绝不回落为 0，不同币种绝不合并。
      </div>

      <div style="margin:10px 0">
        <div style="font-weight:600;margin-bottom:6px">① 字段选择（仅限白名单，已选 ${selected.length} / ${SPE_DESIGNER.fields.length}）</div>
        <div style="display:flex;gap:8px;margin-bottom:6px">
          <button class="btn btn-neutral btn-sm" onclick="speDesToggleAll(true)">全选</button>
          <button class="btn btn-neutral btn-sm" onclick="speDesToggleAll(false)">全不选</button>
        </div>
        <div>${speDesFieldChooserHtml(SPE_DESIGNER.fields, selected)}</div>
      </div>

      <div style="margin:10px 0">
        <div style="font-weight:600;margin-bottom:6px">② 当前筛选（复用工作台，只读）</div>
        ${speDesFilterSummaryHtml()}
      </div>

      <div class="modal-footer">
        <button class="btn btn-primary" onclick="speDesPreview(1)">预览</button>
        <button class="btn btn-neutral" onclick="speDesExport()">📤 导出所选列 CSV</button>
        <button class="btn btn-neutral" onclick="closeModal()">关闭</button>
      </div>
      <div id="spe-des-result"></div>
    </div>`;
}

/* 读取当前字段 / 工作台当前筛选 / 分页状态（预览与分页复用，单一来源） */
function speDesBuildState(page) {
  return {
    catalogFields: SPE_DESIGNER.fields,
    selectedKeys: SPE_DESIGNER.selectedKeys,
    supplierId: speVal('spe-supplier'),
    currency: speVal('spe-currency'),
    orderDateFrom: speVal('spe-date-from'),
    orderDateTo: speVal('spe-date-to'),
    linkStatus: speVal('spe-link-status'),
    keyword: speVal('spe-keyword'),
    pageSize: speVal('spe-pagesize') || '50',
    page: page || 1,
    maxPageSize: SPE_DESIGNER.catalog && SPE_DESIGNER.catalog.maxPageSize ? SPE_DESIGNER.catalog.maxPageSize : 200,
  };
}

/* 预览：组装有界请求 → POST → 安全渲染列名与单元格；授权 / 无效 / 空 / 网络失败均可见 */
async function speDesPreview(page) {
  const state = speDesBuildState(page);
  if (state.orderDateFrom && state.orderDateTo && state.orderDateFrom > state.orderDateTo) {
    speDesRenderResult(speDesErrorHtml('invalid', '订单日期开始不能晚于结束日期'));
    return;
  }

  const req = speDesBuildRequest(state);
  speDesRenderResult(speDesLoadingHtml());

  try {
    const resp = await speDesRequest(SPE_DESIGNER_API, 'POST', req);
    if (resp.code === 0) {
      SPE_DESIGNER.view = resp.data;
      speDesRenderResult(speDesResultHtml(resp.data));
    } else if (resp.code === 2000 || resp.code === 2003) {
      if (typeof logout === 'function') logout();
      speDesRenderResult(speDesErrorHtml('unauthorized', resp.message));
    } else {
      speDesRenderResult(speDesErrorHtml(speDesKindOfCode(resp.code), resp.message));
    }
  } catch (err) {
    speDesRenderResult(speDesErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 翻页（有界：最小第 1 页） */
function speDesPage(delta) {
  const view = SPE_DESIGNER.view;
  const page = (view ? view.page : 1) + delta;
  if (page < 1) return;
  speDesPreview(page);
}

/* 导出当前预览页的所选列 CSV（只读）：未知值保留、公式首字符转义、无跨币种总额；空结果可见错误，不下载仅表头的 CSV */
function speDesExport() {
  const view = SPE_DESIGNER.view;
  if (!view || !view.rows || view.rows.length === 0) {
    speDesRenderResult(speDesErrorHtml('empty', '当前预览页没有采购订单敞口证据，无法导出'));
    return;
  }
  const csv = speDesCsv(view);
  const blob = new Blob([csv], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  const dateStr = new Date().toISOString().slice(0, 10).replace(/-/g, '');
  a.href = url;
  a.download = '供应商采购敞口_所选列_' + dateStr + '.csv';
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
  toast('所选列 CSV 已导出（仅当前预览页，未知值保留，无跨币种总额）', 'success');
}

/* 从工作台打开设计器（加载目录，渲染字段选择器与当前筛选；授权失败 fail closed，不返回任何字段） */
async function openSupplierPurchaseExposureDesigner() {
  SPE_DESIGNER = { catalog: null, fields: [], selectedKeys: [], view: null };
  const modal = document.getElementById('modal');
  if (!modal) return;
  modal.innerHTML = '<div class="modal modal-lg" style="max-width:1100px"><div class="pd-hint" style="text-align:center;color:#64748b">正在加载证据字段目录…</div></div>';
  modal.style.display = 'flex';

  try {
    const resp = await speDesRequest(SPE_DESIGNER_API);
    if (resp.code === 2000 || resp.code === 2003) {
      if (typeof logout === 'function') logout();
      modal.innerHTML = speDesErrorModalHtml('unauthorized', resp.message);
      return;
    }
    if (resp.code !== 0) {
      modal.innerHTML = speDesErrorModalHtml(speDesKindOfCode(resp.code), resp.message);
      return;
    }
    SPE_DESIGNER.catalog = resp.data;
  } catch (err) {
    modal.innerHTML = speDesErrorModalHtml('network', (err && err.message) || '无法连接到服务器');
    return;
  }

  SPE_DESIGNER.fields = (SPE_DESIGNER.catalog && SPE_DESIGNER.catalog.fields) || [];
  SPE_DESIGNER.selectedKeys = SPE_DESIGNER.fields.map(f => f.key);
  speDesRender();
}

/* Node 单测导出（浏览器中 module 为 undefined，自动跳过） */
if (typeof module !== 'undefined' && module.exports) {
  module.exports = {
    SPE_DESIGNER_API,
    speDesEsc,
    speDesSelectFields,
    speDesBuildRequest,
    speDesCellText,
    speDesRenderCell,
    speDesCsvCell,
    speDesCsv,
    speDesTableHtml,
    speDesEmptyHtml,
    speDesErrorHtml,
    speDesResultHtml,
    speDesFieldChooserHtml,
    speDesKindOfCode,
    speDesErrorModalHtml,
    speDesPreview,
    speDesPage,
    speDesExport,
  };
}

