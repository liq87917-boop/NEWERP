/* ============ 供应商采购发票对账报表（ERP-044：只读派生；按「供应商 + 币种」分组，不同币种绝不合并） ============
   口径与后端 SupplierInvoiceReconciliationSemantics 一一对应：
   - 订单侧：订单金额取采购订单已落库总额，已开票金额只按 ERP-043 持久化关联行派生（仅未作废发票），
     未开票余额 = 订单金额 − 已开票金额（下限 0）；订单已取消 / 已删除时按「未知」显示，绝不用 0 顶替；
   - 发票侧：已关联金额与未关联金额分开显示；未关联金额只作单列，绝不猜测到任何订单；
     已作废发票默认不统计（需显式选择证据状态），其金额永不并入有效合计；
   - 收货 / 结算上下文复用「执行进度（ERP-026）」的同一套权威口径，未知一律显示「未知」；
   - 本页是运营性的采购发票对账视图，不是应付账款台账 / 付款授权 / 税务申报 / 账龄表（见页脚与提示声明）。
   数据全部走只读接口 GET /api/purchase-invoices/reconciliation?… */

/* 关联状态文案（与后端 PurchaseInvoiceRules 常量一一对应） */
const SIR_LINKAGE_LABELS = {
  linked: '已全额关联',
  partial: '部分关联（存在未关联金额）',
  unlinked: '未关联（整笔未关联）',
};
/* 证据状态文案（与后端 SupplierInvoiceReconciliationSemantics 常量一一对应） */
const SIR_EVIDENCE_LABELS = {
  recorded: '仅已登记证据（默认）',
  draft: '仅草稿（工作数据）',
  voided: '仅已作废历史证据',
  all: '全部状态（草稿 + 已登记 + 已作废）',
};
/* 订单发票覆盖状态文案（与后端常量一一对应；unknown 不当作未开票） */
const SIR_COVERAGE_LABELS = {
  fully_invoiced: '已全额开票',
  partially_invoiced: '部分开票',
  not_invoiced: '未开票',
  unknown: '未知（订单金额未知或已取消）',
};
/* 订单可用性文案 */
const SIR_ORDER_STATE_LABELS = {
  available: '订单可用',
  cancelled: '订单已取消（仅历史参考）',
  unavailable: '订单不存在 / 已删除（金额未知）',
};
/* 收货状态文案（与后端 PurchaseOrderProgress 常量一一对应；unknown = 本次派生命中上限或订单不可用） */
const SIR_RECEIPT_LABELS = {
  none: '未收货',
  partial: '部分收货',
  complete: '已收齐',
  over_received: '超收',
  unknown: '未知（超出派生上限或订单不可用）',
};

/* 工具栏入口（采购订单页）：渲染独立报表页（只读，不落库） */
function openSupplierInvoiceReconciliationReport() {
  CURRENT_PAGE_CODE = 'supplier-invoice-reconciliation';
  document.getElementById('header-title').textContent = '供应商采购发票对账报表';
  document.getElementById('content').innerHTML = `
    <div class="page-hero report-hero">
      <h2>🧾 供应商采购发票对账报表</h2>
      <p>按供应商 + 币种核对采购订单与已登记（未作废）发票证据 · 订单金额 / 已开票金额 / 未开票余额与未关联金额分开显示（只读派生）</p>
    </div>

    <div class="pd-hint" style="margin-bottom:10px">
      ⚠️ 运营性采购发票对账视图：<b>不是</b>发票口径的应付账款台账，<b>不是</b>付款授权，<b>不是</b>税务申报报表，也<b>不是</b>账龄表 ——
      不推算账期与到期日、不做账龄分摊、不做进项认证、不判断是否已付款；「未关联金额」与「未开票余额」都不得当作应付余额或据以付款。
    </div>

    <div class="toolbar">
      <div class="toolbar-left" style="flex-wrap:wrap;gap:8px;align-items:center">
        <label>供应商 <select id="sir-supplier" style="min-width:170px"><option value="">全部供应商</option></select></label>
        <label>币种 <select id="sir-currency" style="min-width:130px"><option value="">全部币种</option></select></label>
        <label>订单日期 <input type="date" id="sir-order-date-from" style="width:140px"> 至
          <input type="date" id="sir-order-date-to" style="width:140px"></label>
        <label>开票日期 <input type="date" id="sir-invoice-date-from" style="width:140px"> 至
          <input type="date" id="sir-invoice-date-to" style="width:140px"></label>
        <label>关联状态 <select id="sir-linkage" style="min-width:190px">
          <option value="">全部</option>
          <option value="linked">已全额关联</option>
          <option value="partial">部分关联（有未关联金额）</option>
          <option value="unlinked">未关联（整笔未关联）</option>
        </select></label>
        <label>证据状态 <select id="sir-evidence" style="min-width:190px">
          <option value="recorded">仅已登记证据（默认）</option>
          <option value="draft">仅草稿（工作数据）</option>
          <option value="voided">仅已作废历史证据</option>
          <option value="all">全部状态（含已作废）</option>
        </select></label>
        <label>关键字 <input type="text" id="sir-keyword" style="width:190px" placeholder="发票号码 / 代码 / 供应商 / 采购单号"></label>
        <label>每页 <input type="number" id="sir-pagesize" value="50" min="1" max="200" style="width:70px"></label>
      </div>
      <div class="toolbar-actions">
        <button class="btn btn-primary" onclick="loadSupplierInvoiceReconciliation(1)">查询</button>
        <button class="btn btn-neutral" onclick="exportSirCsv()" title="导出本页发票明细为 CSV">📤 导出 CSV</button>
      </div>
    </div>

    <div class="kpi-grid" id="sir-kpi"></div>
    <div class="table-wrap" id="sir-currency-table"></div>
    <div class="table-wrap" id="sir-group-table"></div>
    <div class="table-wrap" id="sir-invoice-table">
      <div class="skeleton-row"></div><div class="skeleton-row"></div>
    </div>
    <div class="pd-hint" id="sir-rule"></div>
    <div class="pagination" id="sir-pagination"></div>`;
  loadSirCurrencies();
  loadSirSuppliers();
  loadSupplierInvoiceReconciliation(1);
}

/* 币种下拉：与单据币种同一枚举口径（枚举名），不同币种绝不合并汇总 */
function loadSirCurrencies() {
  const sel = document.getElementById('sir-currency');
  if (!sel) return;
  (typeof CURRENCY_NAME_OPTS !== 'undefined' ? CURRENCY_NAME_OPTS : []).forEach(o => {
    const opt = document.createElement('option');
    opt.value = o.value;
    opt.textContent = o.label;
    sel.appendChild(opt);
  });
}

/* 供应商下拉：既有基础资料接口；失败不阻断报表（仍可留空或只用其它筛选） */
async function loadSirSuppliers() {
  try {
    const data = await api('/api/base/suppliers?page=1&pageSize=200');
    const sel = document.getElementById('sir-supplier');
    if (!sel) return;
    (data.items || []).forEach(s => {
      const opt = document.createElement('option');
      opt.value = s.id;
      opt.textContent = s.supplierName || ('供应商 ' + s.id);
      sel.appendChild(opt);
    });
  } catch (e) { /* 忽略：供应商下拉失败不影响报表查询 */ }
}

function sirVal(id) {
  const el = document.getElementById(id);
  return el ? String(el.value || '').trim() : '';
}

function sirQuery(page) {
  const q = new URLSearchParams();
  if (sirVal('sir-supplier')) q.set('supplierId', sirVal('sir-supplier'));
  if (sirVal('sir-currency')) q.set('currency', sirVal('sir-currency'));
  if (sirVal('sir-order-date-from')) q.set('orderDateFrom', sirVal('sir-order-date-from'));
  if (sirVal('sir-order-date-to')) q.set('orderDateTo', sirVal('sir-order-date-to'));
  if (sirVal('sir-invoice-date-from')) q.set('invoiceDateFrom', sirVal('sir-invoice-date-from'));
  if (sirVal('sir-invoice-date-to')) q.set('invoiceDateTo', sirVal('sir-invoice-date-to'));
  if (sirVal('sir-linkage')) q.set('linkageStatus', sirVal('sir-linkage'));
  q.set('evidenceStatus', sirVal('sir-evidence') || 'recorded');
  if (sirVal('sir-keyword')) q.set('keyword', sirVal('sir-keyword'));
  q.set('page', page || 1);
  q.set('pageSize', sirVal('sir-pagesize') || '50');
  return q.toString();
}

async function loadSupplierInvoiceReconciliation(page) {
  const el = document.getElementById('sir-invoice-table');
  if (!el) return;
  el.innerHTML = '<div class="skeleton-row"></div><div class="skeleton-row"></div>';
  try {
    const data = await api('/api/purchase-invoices/reconciliation?' + sirQuery(page));
    sirRenderKpi(data);
    sirRenderCurrencyTable(data);
    sirRenderGroupTable(data);
    sirRenderInvoiceTable(data);
    sirRenderPagination(data);
    const rule = document.getElementById('sir-rule');
    if (rule) {
      rule.textContent = '口径：' + (data.rule || '') + ' ' + (data.scopeNote || '') + ' ' +
        (data.ledgerBoundary || '');
    }
  } catch (e) {
    el.innerHTML = `<div class="empty"><div style="font-size:48px">⚠️</div><div>报表加载失败：${escapeHtml(e.message)}</div></div>`;
  }
}

/* 未知（null）= 订单金额未知 / 订单已取消 / 命中批量派生上限；显示「未知」而不是 0 */
function sirMoney(v) { return v === null || v === undefined ? '未知' : fmtMoney(v); }
function sirQty(v) { return v === null || v === undefined ? '未知' : fmtMoney(v); }

function sirLinkageHtml(status) {
  const cls = status === 'linked' ? 'status-success'
    : (status === 'partial' ? 'status-warning' : 'status-neutral');
  return `<span class="status ${cls}">${escapeHtml(SIR_LINKAGE_LABELS[status] || status || '')}</span>`;
}

function sirCoverageHtml(status) {
  const cls = status === 'fully_invoiced' ? 'status-success'
    : (status === 'partially_invoiced' ? 'status-warning'
      : (status === 'unknown' ? 'status-warning' : 'status-neutral'));
  return `<span class="status ${cls}">${escapeHtml(SIR_COVERAGE_LABELS[status] || status || '')}</span>`;
}

function sirOrderStateHtml(state) {
  const cls = state === 'available' ? 'status-success'
    : (state === 'cancelled' ? 'status-danger' : 'status-warning');
  return `<span class="status ${cls}">${escapeHtml(SIR_ORDER_STATE_LABELS[state] || state || '')}</span>`;
}

function sirReceiptHtml(status) {
  const cls = status === 'complete' ? 'status-success'
    : (status === 'over_received' ? 'status-danger'
      : (status === 'unknown' ? 'status-warning' : 'status-neutral'));
  return `<span class="status ${cls}">${escapeHtml(SIR_RECEIPT_LABELS[status] || status || '')}</span>`;
}

function sirRenderKpi(data) {
  const el = document.getElementById('sir-kpi');
  if (!el) return;
  el.innerHTML = `
    <div class="kpi-card cargo">
      <div class="kpi-label">符合筛选的发票</div>
      <div class="kpi-value">${data.total}<span class="unit">张</span></div>
      <div class="kpi-delta flat">本页 ${data.pageInvoiceCount} 张 · 第 ${data.page}/${data.totalPages} 页 · ${escapeHtml(data.evidenceStatusText || '')}</div>
    </div>
    <div class="kpi-card gold">
      <div class="kpi-label">本页关联状态（全额 / 部分 / 未关联）</div>
      <div class="kpi-value">${data.linkedInvoiceCount} / ${data.partialInvoiceCount} / ${data.unlinkedInvoiceCount}<span class="unit">张</span></div>
      <div class="kpi-delta flat">未关联金额只作单列，绝不猜测到任何采购订单</div>
    </div>
    <div class="kpi-card ocean">
      <div class="kpi-label">本页币种数（不跨币种汇总）</div>
      <div class="kpi-value">${(data.currencies || []).length}<span class="unit">种</span></div>
      <div class="kpi-delta flat">涉及订单 ${data.orderCount} 张 · 金额未知 ${data.unknownOrderCount} 张 · 收货未知 ${data.receiptUnknownCount} 张 · 已作废 ${data.voidedInvoiceCount} 张</div>
    </div>`;
}

/* 本页按币种汇总：同一币种内汇总，不同币种分别成行（不做汇率换算） */
function sirRenderCurrencyTable(data) {
  const el = document.getElementById('sir-currency-table');
  if (!el) return;
  const rows = (data.currencies || []).map(c => `<tr>
      <td><b>${escapeHtml(c.currency)}</b></td>
      <td class="text-right">${c.supplierCount}</td>
      <td class="text-right">${c.invoiceCount}</td>
      <td class="text-right">${fmtMoney(c.grossAmount)}</td>
      <td class="text-right">${fmtMoney(c.linkedAmount)}</td>
      <td class="text-right">${fmtMoney(c.unlinkedAmount)}</td>
      <td class="text-right">${c.voidedInvoiceCount} / ${fmtMoney(c.voidedGrossAmount)}</td>
      <td class="text-right">${c.orderCount}</td>
      <td class="text-right">${fmtMoney(c.orderedAmount)}</td>
      <td class="text-right">${fmtMoney(c.invoicedAmount)}</td>
      <td class="text-right">${fmtMoney(c.remainingUninvoicedAmount)}</td>
    </tr>`).join('');
  el.innerHTML = `<table><thead><tr>
      <th>币种</th><th class="text-right">供应商数</th><th class="text-right">有效发票</th>
      <th class="text-right">含税总额</th><th class="text-right">已关联</th><th class="text-right">未关联（不猜测订单）</th>
      <th class="text-right">已作废（张 / 金额，仅历史）</th>
      <th class="text-right">订单数</th><th class="text-right">订单金额</th>
      <th class="text-right">已开票金额</th><th class="text-right">未开票余额</th>
    </tr></thead>
    <tbody>${rows || '<tr><td colspan="11" class="empty">本页没有发票：没有可汇总的币种</td></tr>'}</tbody></table>`;
}

/* 「供应商 + 币种」分组：只有同分组才汇总金额；已作废金额单独成列 */
function sirRenderGroupTable(data) {
  const el = document.getElementById('sir-group-table');
  if (!el) return;
  const rows = (data.groups || []).map(g => `<tr>
      <td>${escapeHtml(g.supplierName || ('供应商 ' + g.supplierId))}</td>
      <td><b>${escapeHtml(g.currency)}</b></td>
      <td class="text-right">${g.invoiceCount}</td>
      <td class="text-right">${fmtMoney(g.grossAmount)}</td>
      <td class="text-right">${fmtMoney(g.linkedAmount)}</td>
      <td class="text-right">${fmtMoney(g.unlinkedAmount)}</td>
      <td class="text-right">${g.voidedInvoiceCount} / ${fmtMoney(g.voidedGrossAmount)}</td>
      <td class="text-right">${g.orderCount}</td>
      <td class="text-right">${fmtMoney(g.orderedAmount)}</td>
      <td class="text-right">${fmtMoney(g.invoicedAmount)}</td>
      <td class="text-right">${fmtMoney(g.remainingUninvoicedAmount)}</td>
      <td>${g.fullyInvoicedOrderCount} / ${g.partiallyInvoicedOrderCount} / ${g.notInvoicedOrderCount}</td>
      <td class="text-right">${g.unknownOrderCount} / ${g.cancelledOrderCount}</td>
      <td class="text-right">${fmtMoney(g.voidedOrderAllocatedAmount)}</td>
    </tr>`).join('');
  el.innerHTML = `<table><thead><tr>
      <th>供应商</th><th>币种</th><th class="text-right">有效发票</th>
      <th class="text-right">含税总额</th><th class="text-right">已关联</th><th class="text-right">未关联（不猜测订单）</th>
      <th class="text-right">已作废（张 / 金额）</th>
      <th class="text-right">订单数</th><th class="text-right">订单金额</th>
      <th class="text-right">已开票金额</th><th class="text-right">未开票余额</th>
      <th>全额 / 部分 / 未开票（订单）</th><th class="text-right">金额未知 / 已取消（订单）</th>
      <th class="text-right">作废发票关联金额（仅历史）</th>
    </tr></thead>
    <tbody>${rows || '<tr><td colspan="14" class="empty">没有符合筛选条件的「供应商 + 币种」分组</td></tr>'}</tbody></table>`;
}

/* 本页发票明细（含每张发票的关联订单行；未知一律显示「未知」） */
function sirRenderInvoiceTable(data) {
  const el = document.getElementById('sir-invoice-table');
  if (!el) return;
  const invoices = [];
  (data.groups || []).forEach(g => (g.invoices || []).forEach(i => invoices.push(i)));
  const rows = invoices.map(i => {
    const head = `<tr>
      <td>${escapeHtml(i.identityText || i.invoiceNumber)}${i.isVoided ? ' <span class="status status-danger">已作废</span>' : ''}</td>
      <td>${fmtDate(i.invoiceDate)}</td>
      <td>${escapeHtml(i.supplierName || ('供应商 ' + i.supplierId))}</td>
      <td>${escapeHtml(i.currency)}</td>
      <td class="text-right">${fmtMoney(i.netAmount)}</td>
      <td class="text-right">${fmtMoney(i.taxAmount)}</td>
      <td class="text-right">${fmtMoney(i.grossAmount)}</td>
      <td>${sirLinkageHtml(i.linkageStatus)}</td>
      <td class="text-right">${fmtMoney(i.linkedAmount)}</td>
      <td class="text-right">${fmtMoney(i.unlinkedAmount)}</td>
      <td>${escapeHtml(i.statusText || '')}</td>
      <td>${escapeHtml(i.evidenceText || '')}</td>
      <td>${escapeHtml(i.note || '')}</td>
    </tr>`;

    const orderRows = (i.orders || []).map(o => `<tr class="text-muted">
      <td>↳ 关联订单 ${escapeHtml(o.orderNo)}（${fmtDate(o.orderDate)} · ${escapeHtml(o.orderCurrency)}）</td>
      <td colspan="2">${sirOrderStateHtml(o.orderState)} ${escapeHtml(o.orderStatusText || '')}</td>
      <td colspan="2">${escapeHtml(o.coverageText || '')}</td>
      <td class="text-right">本票 ${fmtMoney(o.allocatedAmount)}</td>
      <td class="text-right">已开票 ${fmtMoney(o.invoicedAmount)}</td>
      <td class="text-right">订单金额 ${sirMoney(o.orderedAmount)}</td>
      <td class="text-right">未开票余额 ${sirMoney(o.remainingUninvoicedAmount)}</td>
      <td class="text-right">作废关联 ${fmtMoney(o.voidedAllocatedAmount)}</td>
      <td>收货 ${sirQty(o.receivedQuantity)} / ${sirQty(o.orderedQuantity)} · ${sirReceiptHtml(o.receiptStatus)}</td>
      <td class="text-right">已结算 ${sirMoney(o.settledAmount)} / 未结算 ${sirMoney(o.outstandingSettlementAmount)}</td>
      <td>${escapeHtml(o.orderStateText || '')} ${escapeHtml(o.settlementLinkReason || '')}</td>
    </tr>`).join('');

    return head + orderRows;
  }).join('');

  el.innerHTML = `<table><thead><tr>
      <th>发票（类型 / 代码 / 号码）</th><th>开票日期</th><th>供应商</th><th>币种</th>
      <th class="text-right">不含税</th><th class="text-right">税额</th><th class="text-right">含税总额</th>
      <th>关联状态</th><th class="text-right">已关联金额</th><th class="text-right">未关联金额（不猜测订单）</th>
      <th>状态</th><th>证据口径</th><th>说明</th>
    </tr></thead>
    <tbody>${rows || '<tr><td colspan="13" class="empty">没有符合筛选条件的发票（可放宽供应商 / 币种 / 日期 / 关联状态 / 证据状态筛选）</td></tr>'}</tbody></table>`;
}

function sirRenderPagination(data) {
  const el = document.getElementById('sir-pagination');
  if (!el) return;
  const page = data.page || 1;
  const totalPages = data.totalPages || 1;
  el.innerHTML = `
    <button class="btn btn-neutral btn-sm" ${page <= 1 ? 'disabled' : ''} onclick="loadSupplierInvoiceReconciliation(${page - 1})">上一页</button>
    <span style="margin:0 10px">第 ${page} / ${totalPages} 页（共 ${data.total} 张发票）</span>
    <button class="btn btn-neutral btn-sm" ${page >= totalPages ? 'disabled' : ''} onclick="loadSupplierInvoiceReconciliation(${page + 1})">下一页</button>`;
}

/* 导出本页发票明细为 CSV（只导出当前页，口径与页面一致） */
function exportSirCsv() {
  const table = document.querySelector('#sir-invoice-table table');
  if (!table) { toast('暂无可导出数据', 'warning'); return; }
  const rows = Array.from(table.querySelectorAll('tr'));
  const csv = rows.map(tr => Array.from(tr.querySelectorAll('th,td'))
    .map(td => `"${td.textContent.replace(/"/g, '""')}"`).join(',')).join('\n');
  const blob = new Blob(['\uFEFF' + csv], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = '供应商采购发票对账报表.csv';
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
  toast('CSV 已导出', 'success');
}
