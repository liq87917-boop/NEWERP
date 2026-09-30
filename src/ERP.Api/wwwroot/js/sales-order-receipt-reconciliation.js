/* ============ 客户订单与收款核对报表（ERP-046：只读派生；按「客户 + 币种」分组，不同币种绝不合并） ============
   口径与后端 SalesOrderReceiptReconciliationSemantics 一一对应：
   - 订单侧：已订 / 已出 / 未出数量与订单金额完全复用 ERP-032 的权威派生（已审核销售出库单）；
     「定金申请单 / 货款申请单的 SalesOrderId」才是权威收款引用，只有「已审核 + 同币种」计入已关联收款金额；
     没有权威引用 / 命中派生上限时金额一律显示「未知」，绝不回落为 0；
   - 收款单（FinanceReceipt）只记录客户、没有订单级引用 → 一律作为「未关联证据」单独列出（链接状态恒为 unlinked），
     系统绝不按客户名、订单号文本、日期或金额相似度匹配到任何订单；未关联金额只按收款单自身币种单列，绝不并入订单金额；
   - 历史（已驳回 / 已取消 / 已完成）与未审核证据必须显式选择「收款证据状态」才可见，且永不计入有效合计；
   - ERP-054：订单侧另按 ERP-053 的持久化收款引用行单独标注「收款引用证据」（有效已引用金额 / 引用行条数 /
     收款单张数），已作废 / 无效 / 无法确认单独列出；它与「已关联收款金额」（收款申请单的权威引用）是两类独立证据，
     绝不相加、不得互相替代，「无收款引用证据」只表示没有登记，绝不等于未收款或已收款；
    - ERP-056：订单侧另按 ERP-055 的持久化销项发票证据行与其分摊行单独标注「销项发票证据」（有效已分摊金额 /
      分摊行条数 / 发票张数 / 未指向本单金额），草稿（发票未登记）/ 已作废 / 无效 / 无法确认单独列出；
      它与订单金额、收款申请链接、收款引用登记证据都是相互独立的证据类别，四者绝不相加，
      「无销项发票证据」只表示没有登记，绝不等于未开票、已开票、欠税、已收款或已结清；

   - 本页是运营性订单 / 收款证据核对视图，不是应收账款台账 / 客户对账单 / 收款授权 / 结算结果 / 账龄表（见页脚声明）。
   数据全部走只读接口 GET /api/sales-orders/receipt-reconciliation-report */

/* 收款覆盖状态文案（与后端 SalesOrderReceiptReconciliationSemantics 常量一一对应） */
const SORR_COVERAGE_LABELS = {
  linked: '已关联（全部可计入）',
  partial: '部分可归属（其余未知）',
  unlinked: '未关联（金额未知）',
  unknown: '未知（超出派生上限）',
};

/* 收款证据状态文案（与后端常量一一对应；历史 / 未审核不并入有效合计） */
const SORR_RECEIPT_STATUS_LABELS = {
  active: '仅有效收款证据（默认：已审核）',
  pending: '仅未审核收款单（待提交 / 已提交）',
  historical: '仅历史收款单（已驳回 / 已取消 / 已完成）',
  all: '全部状态（有效 + 未审核 + 历史）',
};

/* 单张收款单的证据分档文案（与后端 EvidenceTextOf 一一对应） */
const SORR_EVIDENCE_LABELS = {
  active: '有效证据（已审核，计入有效合计）',
  pending: '未审核（仅列出、不计入）',
  historical: '历史证据（仅历史核对、不计入）',
};

/* 收款引用登记证据状态文案（ERP-054；与后端 SalesOrderReceiptEvidenceSemantics 常量一一对应） */
const SORR_ALLOC_LABELS = {
  recorded: '有收款引用证据',
  historical_only: '仅有历史 / 无效收款引用证据',
  none: '无收款引用证据',
  unknown: '未知（超出有界读取上限）',
};


/* 销项发票登记证据状态文案（ERP-056；与后端 SalesOrderInvoiceEvidenceSemantics 常量一一对应） */
const SORR_INVOICE_LABELS = {
  recorded: '有销项发票证据',
  historical_only: '仅有草稿 / 作废 / 无效销项发票证据',
  none: '无销项发票证据',
  unknown: '未知（超出有界读取上限）',
};


/* 工具栏入口（销售订单页）：渲染独立报表页（只读，不落库） */
function openSalesOrderReceiptReconciliationReport() {
  CURRENT_PAGE_CODE = 'sales-order-receipt-reconciliation';
  document.getElementById('header-title').textContent = '客户订单与收款核对报表';
  document.getElementById('content').innerHTML = `
    <div class="page-hero report-hero">
      <h2>🧾 客户订单与收款核对报表</h2>
      <p>按客户 + 币种核对销售订单与收款证据 · 已订 / 已出 / 未出数量复用出货进度口径 · 收款单作为未关联证据单独列出（只读派生，未知不推断）</p>
    </div>

    <div class="pd-hint" style="margin-bottom:10px">
      ⚠️ 运营性订单 / 收款证据核对视图：<b>不是</b>应收账款台账，<b>不是</b>客户对账单，<b>不是</b>收款授权或结算结果，也<b>不是</b>账龄表 ——
      不创建发票 / 应收记录、不推算账期与到期日、不判断是否已收讫；「未关联收款证据」与「未覆盖金额」都不得当作应收余额或据以催收。
    </div>

    <div class="toolbar">
      <div class="toolbar-left" style="flex-wrap:wrap;gap:8px;align-items:center">
        <label>客户 <select id="sorr-customer" style="min-width:170px"><option value="">全部客户</option></select></label>
        <label>币种 <select id="sorr-currency" style="min-width:130px"><option value="">全部币种</option></select></label>
        <label>订单日期 <input type="date" id="sorr-date-from" style="width:140px"> 至
          <input type="date" id="sorr-date-to" style="width:140px"></label>
        <label>出货状态 <select id="sorr-shipment-status" style="min-width:170px">
          <option value="">全部</option>
          <option value="none">未出货（无已审核出库单）</option>
          <option value="shipped">已有已审核出库单</option>
        </select></label>
        <label>收款链接 <select id="sorr-link-status" style="min-width:190px">
          <option value="">全部</option>
          <option value="linked">已关联（全部可计入）</option>
          <option value="partial">部分可归属（其余未知）</option>
          <option value="unlinked">未关联（金额未知）</option>
        </select></label>
        <label>收款证据 <select id="sorr-receipt-status" style="min-width:210px">
          <option value="active">仅有效收款证据（默认：已审核）</option>
          <option value="pending">仅未审核收款单（待提交 / 已提交）</option>
          <option value="historical">仅历史收款单（已驳回 / 已取消 / 已完成）</option>
          <option value="all">全部状态（含未审核 / 历史）</option>
        </select></label>
        <label>订单状态 <select id="sorr-order-status" style="min-width:190px">
          <option value="active">仅有效订单（默认：排除已取消）</option>
          <option value="cancelled">仅已取消订单（历史）</option>
          <option value="all">全部未删除订单</option>
        </select></label>
        <label>关键字 <input type="text" id="sorr-keyword" style="width:190px" placeholder="订单号 / 合同号 / 客户 PO 号"></label>
        <label>每页 <input type="number" id="sorr-pagesize" value="50" min="1" max="200" style="width:70px"></label>
      </div>
      <div class="toolbar-actions">
        <button class="btn btn-primary" onclick="loadSalesOrderReceiptReconciliation(1)">查询</button>
        <button class="btn btn-neutral" onclick="exportSorrcsv()" title="导出本页订单明细为 CSV">📤 导出 CSV</button>
        <button class="btn btn-neutral" onclick="openDynamicReceiptReconciliationDesigner()" title="打开目录驱动的订单证据与未关联收款证据字段 / 筛选设计器（只读，走 ERP-164/165 预览接口）">🔧 动态设计器</button>
      </div>
    </div>

    <div class="kpi-grid" id="sorr-kpi"></div>
    <div class="table-wrap" id="sorr-currency-table"></div>
    <div class="table-wrap" id="sorr-group-table"></div>
    <div class="table-wrap" id="sorr-order-table">
      <div class="skeleton-row"></div><div class="skeleton-row"></div>
    </div>
    <div class="table-wrap" id="sorr-receipt-currency-table"></div>
    <div class="table-wrap" id="sorr-receipt-table"></div>
    <div class="pd-hint" id="sorr-rule"></div>
    <div class="pagination" id="sorr-pagination"></div>`;
  loadSorCurrencies();
  loadSorCustomers();
  loadSalesOrderReceiptReconciliation(1);
}


/* 币种下拉：与单据币种同一枚举口径（枚举名），不同币种绝不合并汇总 */
function loadSorCurrencies() {
  const sel = document.getElementById('sorr-currency');
  if (!sel) return;
  (typeof CURRENCY_NAME_OPTS !== 'undefined' ? CURRENCY_NAME_OPTS : []).forEach(o => {
    const opt = document.createElement('option');
    opt.value = o.value;
    opt.textContent = o.label;
    sel.appendChild(opt);
  });
}

/* 客户下拉：既有基础资料接口；失败不阻断报表（仍可留空或只用其它筛选） */
async function loadSorCustomers() {
  try {
    const data = await api('/api/base/customers?page=1&pageSize=200');
    const sel = document.getElementById('sorr-customer');
    if (!sel) return;
    (data.items || []).forEach(c => {
      const opt = document.createElement('option');
      opt.value = c.id;
      opt.textContent = c.customerName || ('客户 ' + c.id);
      sel.appendChild(opt);
    });
  } catch (e) { /* 忽略：客户下拉失败不影响报表查询 */ }
}

function sorVal(id) {
  const el = document.getElementById(id);
  return el ? String(el.value || '').trim() : '';
}

function sorQuery(page) {
  const q = new URLSearchParams();
  if (sorVal('sorr-customer')) q.set('customerId', sorVal('sorr-customer'));
  if (sorVal('sorr-currency')) q.set('currency', sorVal('sorr-currency'));
  if (sorVal('sorr-date-from')) q.set('orderDateFrom', sorVal('sorr-date-from'));
  if (sorVal('sorr-date-to')) q.set('orderDateTo', sorVal('sorr-date-to'));
  if (sorVal('sorr-shipment-status')) q.set('shipmentStatus', sorVal('sorr-shipment-status'));
  if (sorVal('sorr-link-status')) q.set('receiptLinkStatus', sorVal('sorr-link-status'));
  q.set('receiptStatus', sorVal('sorr-receipt-status') || 'active');
  q.set('orderStatus', sorVal('sorr-order-status') || 'active');
  if (sorVal('sorr-keyword')) q.set('keyword', sorVal('sorr-keyword'));
  q.set('page', page || 1);
  q.set('pageSize', sorVal('sorr-pagesize') || '50');
  return q.toString();
}

async function loadSalesOrderReceiptReconciliation(page) {
  const el = document.getElementById('sorr-order-table');
  if (!el) return;
  el.innerHTML = '<div class="skeleton-row"></div><div class="skeleton-row"></div>';
  try {
    const data = await api('/api/sales-orders/receipt-reconciliation-report?' + sorQuery(page));
    sorRenderKpi(data);
    sorRenderCurrencyTable(data);
    sorRenderGroupTable(data);
    sorRenderOrderTable(data);
    sorRenderReceiptCurrencyTable(data);
    sorRenderReceiptTable(data);
    sorRenderPagination(data);
    const rule = document.getElementById('sorr-rule');
    if (rule) {
      rule.textContent = '口径：' + (data.rule || '') + ' ' + (data.scopeNote || '') + ' ' +
        (data.ledgerBoundary || '') + ' 收款引用证据口径（ERP-054）：' + (data.receiptAllocationRule || '') +
        ' ' + (data.receiptAllocationBoundary || '') +
        ' 销项发票证据口径（ERP-056）：' + (data.invoiceEvidenceRule || '') +
        ' ' + (data.invoiceEvidenceBoundary || '');
    }
  } catch (e) {
    el.innerHTML = `<div class="empty"><div style="font-size:48px">⚠️</div><div>报表加载失败：${escapeHtml(e.message)}</div></div>`;
  }
}

/* 未知（null）= 无权威引用 / 命中等派生命中上限：显示「未知」而不是 0 */
function sorMoney(v) { return v === null || v === undefined ? '未知' : fmtMoney(v); }
function sorQty(v) { return v === null || v === undefined ? '未知' : fmtMoney(v); }

function sorCoverageHtml(status) {
  const cls = status === 'linked' ? 'status-success'
    : (status === 'unknown' ? 'status-danger'
      : (status === 'partial' ? 'status-warning' : 'status-neutral'));
  return `<span class="status ${cls}">${escapeHtml(SORR_COVERAGE_LABELS[status] || status || '')}</span>`;
}

function sorEvidenceHtml(status) {
  const cls = status === 'active' ? 'status-success'
    : (status === 'historical' ? 'status-neutral' : 'status-warning');
  return `<span class="status ${cls}">${escapeHtml(SORR_EVIDENCE_LABELS[status] || status || '')}</span>`;
}

/* 收款引用登记证据（ERP-054）：状态徽标 + 引用行条数；「无收款引用证据」只是登记缺口，绝不等于未收款 / 已收款 */
function sorrAllocationHtml(o) {
  const status = o.receiptAllocationStatus;
  const cls = status === 'recorded' ? 'status-success'
    : (status === 'historical_only' ? 'status-warning'
      : (status === 'unknown' ? 'status-danger' : 'status-neutral'));
  const label = SORR_ALLOC_LABELS[status] || status || '';
  const count = (o.receiptAllocationCount === null || o.receiptAllocationCount === undefined)
    ? '未知' : `${o.receiptAllocationCount} 条引用行`;
  const title = `${o.receiptAllocationEvidenceLabel || ''} ${o.receiptAllocationNote || ''}`.trim();
  return `<span class="status ${cls}" title="${escapeHtml(title)}">${escapeHtml(label)}</span>` +
    `<div class="text-muted">${escapeHtml(count)}</div>`;
}
/* 销项发票登记证据（ERP-056）：状态徽标 + 分摊行条数；「无销项发票证据」只是登记缺口，
   绝不等于未开票 / 已开票 / 欠税 / 已收款 / 已结清 */
function sorrInvoiceEvidenceHtml(o) {
  const status = o.invoiceEvidenceStatus;
  const cls = status === 'recorded' ? 'status-success'
    : (status === 'historical_only' ? 'status-warning'
      : (status === 'unknown' ? 'status-danger' : 'status-neutral'));
  const label = SORR_INVOICE_LABELS[status] || status || '';
  const count = (o.invoiceAllocationCount === null || o.invoiceAllocationCount === undefined)
    ? '未知' : `${o.invoiceAllocationCount} 条分摊行`;
  const title = `${o.invoiceEvidenceLabel || ''} ${o.invoiceEvidenceNote || ''}`.trim();
  return `<span class="status ${cls}" title="${escapeHtml(title)}">${escapeHtml(label)}</span>` +
    `<div class="text-muted">${escapeHtml(count)}</div>`;
}

function sorRenderKpi(data) {
  const el = document.getElementById('sorr-kpi');
  if (!el) return;
  el.innerHTML = `
    <div class="kpi-card cargo">
      <div class="kpi-label">符合筛选的订单</div>
      <div class="kpi-value">${data.total}<span class="unit">张</span></div>
      <div class="kpi-delta flat">本页 ${data.pageOrderCount} 张 · 第 ${data.page}/${data.totalPages} 页 · ${escapeHtml(data.orderStatusText || '')} · ${escapeHtml(data.receiptStatusText || '')}</div>
    </div>
    <div class="kpi-card gold">
      <div class="kpi-label">本页收款覆盖（已关联 / 部分 / 未关联 / 未知）</div>
      <div class="kpi-value">${data.linkedOrderCount} / ${data.partialOrderCount} / ${data.unlinkedOrderCount} / ${data.unknownCoverageOrderCount}<span class="unit">张</span></div>
      <div class="kpi-delta flat">未关联 / 未知金额显示「未知」，绝不当作已收 0、未收全额或逾期</div>
    </div>
    <div class="kpi-card ocean">
      <div class="kpi-label">本页未关联收款证据</div>
      <div class="kpi-value">${data.pageUnlinkedReceiptCount}<span class="unit">张</span></div>
      <div class="kpi-delta flat">${data.pageUnlinkedReceiptTruncated ? '已命中查询上限：张数与金额不完整，请收窄筛选' : '收款单没有订单级引用：只列出、不匹配、不并入订单金额'}</div>
    </div>
    <div class="kpi-card lc">
      <div class="kpi-label">本页币种数（不跨币种汇总）</div>
      <div class="kpi-value">${(data.currencies || []).length}<span class="unit">种</span></div>
      <div class="kpi-delta flat">已有已审核出库单 ${data.shippedOrderCount} 张 · 无出库单 ${data.unshippedOrderCount} 张 · 出货未知 ${data.unknownShipmentOrderCount} 张</div>
    </div>
    <div class="kpi-card cargo">
      <div class="kpi-label">本页收款引用证据（ERP-053 登记，独立证据）</div>
      <div class="kpi-value">${data.receiptAllocationOrderCount} / ${data.historicalOnlyReceiptAllocationOrderCount} / ${data.noReceiptAllocationOrderCount} / ${data.unknownReceiptAllocationOrderCount}<span class="unit">张</span></div>
      <div class="kpi-delta flat">有效 / 仅历史无效 / 无引用证据 / 未知：「无收款引用证据」只是登记缺口，绝不等于未收款或已收款</div>
    </div>
    <div class="kpi-card gold">
      <div class="kpi-label">本页销项发票证据（ERP-055 登记，独立证据）</div>
      <div class="kpi-value">${data.invoiceEvidenceOrderCount} / ${data.historicalOnlyInvoiceEvidenceOrderCount} / ${data.noInvoiceEvidenceOrderCount} / ${data.unknownInvoiceEvidenceOrderCount}<span class="unit">张</span></div>
      <div class="kpi-delta flat">有效 / 仅草稿作废无效 / 无销项发票证据 / 未知：「无销项发票证据」只是登记缺口，绝不等于未开票、已开票、欠税或已收款</div>
    </div>`;
}

/* 本页订单侧按币种汇总：同一币种内跨客户汇总，不同币种分别成行（不做汇率换算） */
function sorRenderCurrencyTable(data) {
  const el = document.getElementById('sorr-currency-table');
  if (!el) return;
  const rows = (data.currencies || []).map(c => `<tr>
      <td><b>${escapeHtml(c.currency)}</b></td>
      <td class="text-right">${c.customerCount}</td>
      <td class="text-right">${c.orderCount}</td>
      <td class="text-right">${fmtMoney(c.orderAmount)}</td>
      <td class="text-right">${sorQty(c.orderedQuantity)}</td>
      <td class="text-right">${sorQty(c.shippedQuantity)}</td>
      <td class="text-right">${sorQty(c.outstandingQuantity)}</td>
      <td class="text-right">${sorMoney(c.linkedReceiptAmount)}</td>
      <td class="text-right">${sorMoney(c.uncoveredAmount)}</td>
      <td class="text-right">${sorMoney(c.pendingReceiptAmount)}</td>
      <td>${c.linkedOrderCount} / ${c.partialOrderCount} / ${c.unlinkedOrderCount} / ${c.unknownCoverageOrderCount}</td>
    </tr>`).join('');
  el.innerHTML = `<table><thead><tr>
      <th>币种</th><th class="text-right">客户数</th><th class="text-right">订单数</th>
      <th class="text-right">订单金额</th>
      <th class="text-right">已订数量</th><th class="text-right">已出数量</th><th class="text-right">未出数量</th>
      <th class="text-right">已关联收款金额</th><th class="text-right">未覆盖金额（不是应收余额）</th>
      <th class="text-right">未审核收款（仅单列）</th>
      <th>已关联 / 部分 / 未关联 / 未知（订单数）</th>
    </tr></thead>
    <tbody>${rows || '<tr><td colspan="11" class="empty">本页没有订单：没有可汇总的币种</td></tr>'}</tbody></table>`;
}

/* 「客户 + 币种」分组：只有同分组才汇总金额；数量未知一律显示「未知」 */
function sorRenderGroupTable(data) {
  const el = document.getElementById('sorr-group-table');
  if (!el) return;
  const rows = (data.groups || []).map(g => `<tr>
      <td>${escapeHtml(g.customerName || ('客户 ' + g.customerId))}</td>
      <td><b>${escapeHtml(g.currency)}</b></td>
      <td class="text-right">${g.orderCount}</td>
      <td class="text-right">${fmtMoney(g.orderAmount)}</td>
      <td class="text-right">${sorQty(g.orderedQuantity)}</td>
      <td class="text-right">${sorQty(g.shippedQuantity)}</td>
      <td class="text-right">${sorQty(g.outstandingQuantity)}</td>
      <td class="text-right">${sorMoney(g.linkedReceiptAmount)}</td>
      <td class="text-right">${sorMoney(g.uncoveredAmount)}</td>
      <td class="text-right">${sorMoney(g.pendingReceiptAmount)}</td>
      <td>${g.linkedOrderCount} / ${g.partialOrderCount} / ${g.unlinkedOrderCount} / ${g.unknownCoverageOrderCount}</td>
      <td>${g.receiptAllocationOrderCount} / ${g.historicalOnlyReceiptAllocationOrderCount} / ${g.noReceiptAllocationOrderCount} / ${g.unknownReceiptAllocationOrderCount}</td>
      <td class="text-right">${sorMoney(g.recordedReceiptAllocationAmount)}</td>
      <td>${g.invoiceEvidenceOrderCount} / ${g.historicalOnlyInvoiceEvidenceOrderCount} / ${g.noInvoiceEvidenceOrderCount} / ${g.unknownInvoiceEvidenceOrderCount}</td>
      <td class="text-right">${sorMoney(g.recordedInvoicedAmount)}</td>
      <td class="text-right">${g.cancelledOrderCount}</td>
      <td>${escapeHtml(g.note || '')}</td>
    </tr>`).join('');
  el.innerHTML = `<table><thead><tr>
      <th>客户</th><th>币种</th><th class="text-right">订单数</th><th class="text-right">订单金额</th>
      <th class="text-right">已订数量</th><th class="text-right">已出数量</th><th class="text-right">未出数量</th>
      <th class="text-right">已关联收款金额</th><th class="text-right">未覆盖金额</th>
      <th class="text-right">未审核收款（仅单列）</th>
      <th>已关联 / 部分 / 未关联 / 未知（订单数）</th>
      <th>收款引用证据：有效 / 仅历史无效 / 无 / 未知（订单数）</th>
      <th class="text-right">有效收款引用金额（独立证据）</th>
      <th>销项发票证据：有效 / 仅草稿作废无效 / 无 / 未知（订单数）</th>
      <th class="text-right">有效销项发票已分摊金额（独立证据）</th>
      <th class="text-right">已取消</th><th>说明</th>
    </tr></thead>
    <tbody>${rows || '<tr><td colspan="17" class="empty">没有符合筛选条件的「客户 + 币种」分组</td></tr>'}</tbody></table>`;
}

/* 本页订单明细（未知一律显示「未知」；未关联 / 未知不得当作未收或逾期） */
function sorRenderOrderTable(data) {
  const el = document.getElementById('sorr-order-table');
  if (!el) return;
  const orders = [];
  (data.groups || []).forEach(g => (g.orders || []).forEach(o => orders.push(o)));
  const rows = orders.map(o => `<tr>
      <td>${escapeHtml(o.orderNo)}</td>
      <td>${fmtDate(o.orderDate)}</td>
      <td>${escapeHtml(o.customerName || ('客户 ' + o.customerId))}</td>
      <td>${escapeHtml(o.currency)}</td>
      <td>${escapeHtml(o.status || '')}</td>
      <td class="text-right">${fmtMoney(o.orderAmount)}</td>
      <td class="text-right">${sorQty(o.orderedQuantity)}</td>
      <td class="text-right">${sorQty(o.shippedQuantity)}</td>
      <td class="text-right">${sorQty(o.pendingShipmentQuantity)}</td>
      <td class="text-right">${sorQty(o.outstandingQuantity)}</td>
      <td>${sorCoverageHtml(o.receiptCoverageStatus)}</td>
      <td class="text-right">${sorMoney(o.linkedReceiptAmount)}</td>
      <td class="text-right">${sorMoney(o.pendingReceiptAmount)}</td>
      <td class="text-right">${sorMoney(o.uncoveredAmount)}</td>
      <td>${sorrAllocationHtml(o)}</td>
      <td class="text-right">${sorMoney(o.recordedReceiptAllocationAmount)}</td>
      <td class="text-right">${sorMoney(o.unreferencedOrderAmount)}</td>
      <td>${sorrInvoiceEvidenceHtml(o)}</td>
      <td class="text-right">${sorMoney(o.recordedInvoicedAmount)}</td>
      <td class="text-right">${sorMoney(o.recordedInvoiceGrossAmount)}</td>
      <td class="text-right">${sorMoney(o.invoiceUnreferencedOrderAmount)}</td>
      <td class="text-right">${o.otherCurrencyReceiptCount} / ${o.unapprovedReceiptCount} / ${o.unattributedReceiptCount}</td>
      <td>${escapeHtml(o.note || '')}</td>
    </tr>`).join('');
  el.innerHTML = `<table><thead><tr>
      <th>订单号</th><th>订单日期</th><th>客户</th><th>币种</th><th>状态</th>
      <th class="text-right">订单金额</th>
      <th class="text-right">已订数量</th><th class="text-right">已出数量</th>
      <th class="text-right">待审出库</th><th class="text-right">未出数量</th>
      <th>收款覆盖（收款申请链接）</th>
      <th class="text-right">已关联收款金额</th><th class="text-right">未审核收款</th>
      <th class="text-right">未覆盖金额（不是应收余额）</th>
      <th>收款引用证据（ERP-053 登记）</th>
      <th class="text-right">有效收款引用金额（独立证据）</th>
      <th class="text-right">引用证据外订单金额（不是应收余额）</th>
      <th>销项发票证据（ERP-055 登记）</th>
      <th class="text-right">有效销项发票已分摊金额（独立证据）</th>
      <th class="text-right">参与证据的发票含税总额</th>
      <th class="text-right">发票证据外订单金额（不是应收余额）</th>
      <th class="text-right">他币种 / 未审核 / 客户级（条）</th>
      <th>说明</th>
    </tr></thead>
    <tbody>${rows || '<tr><td colspan="23" class="empty">没有符合筛选条件的订单（可放宽客户 / 币种 / 日期 / 出货 / 收款链接筛选）</td></tr>'}</tbody></table>`;
}

/* 未关联收款证据按币种汇总：只按收款单自身币种汇总，绝不并入订单侧金额 */
function sorRenderReceiptCurrencyTable(data) {
  const el = document.getElementById('sorr-receipt-currency-table');
  if (!el) return;
  const list = data.unlinkedReceiptCurrencies || [];
  const rows = list.map(c => `<tr>
      <td><b>${escapeHtml(c.currency)}</b></td>
      <td class="text-right">${c.customerCount}</td>
      <td class="text-right">${c.receiptCount}</td>
      <td class="text-right">${c.activeReceiptCount} / ${sorMoney(c.activeReceiptAmount)}</td>
      <td class="text-right">${c.pendingReceiptCount} / ${sorMoney(c.pendingReceiptAmount)}</td>
      <td class="text-right">${c.historicalReceiptCount} / ${sorMoney(c.historicalReceiptAmount)}</td>
      <td>${escapeHtml(c.note || '')}</td>
    </tr>`).join('');
  el.innerHTML = `<h4 style="margin:14px 0 6px">🧾 未关联收款证据（收款单无订单级引用：只列出、不匹配、不并入订单金额）</h4>
    <table><thead><tr>
      <th>币种</th><th class="text-right">客户数</th><th class="text-right">收款单张数</th>
      <th class="text-right">有效（张 / 金额）</th>
      <th class="text-right">未审核（张 / 金额，仅列出）</th>
      <th class="text-right">历史（张 / 金额，仅历史核对）</th>
      <th>说明</th>
    </tr></thead>
    <tbody>${rows || '<tr><td colspan="7" class="empty">本页客户没有符合条件的收款单（未关联收款证据为空）</td></tr>'}</tbody></table>`;
}

/* 未关联收款证据逐张明细：链接状态恒为 unlinked，绝不猜测订单 */
function sorRenderReceiptTable(data) {
  const el = document.getElementById('sorr-receipt-table');
  if (!el) return;
  const list = data.unlinkedReceipts || [];
  const rows = list.map(r => `<tr>
      <td>${escapeHtml(r.receiptNo)}</td>
      <td>${fmtDate(r.receiptDate)}</td>
      <td>${escapeHtml(r.customerName || ('客户 ' + r.customerId))}</td>
      <td>${escapeHtml(r.currency)}</td>
      <td class="text-right">${fmtMoney(r.amount)}</td>
      <td>${escapeHtml(r.paymentMethod || '')}</td>
      <td>${escapeHtml(r.status || '')}</td>
      <td>${sorEvidenceHtml(r.evidenceStatus)}</td>
      <td>${escapeHtml(r.referenceField || '')}</td>
      <td>${escapeHtml(r.note || '')}</td>
    </tr>`).join('');
  el.innerHTML = `<table><thead><tr>
      <th>收款单号</th><th>收款日期</th><th>客户</th><th>币种</th><th class="text-right">金额</th>
      <th>付款方式</th><th>状态</th><th>证据口径</th><th>引用字段（仅客户级）</th><th>说明</th>
    </tr></thead>
    <tbody>${rows || '<tr><td colspan="10" class="empty">本页客户没有符合条件的未关联收款单</td></tr>'}</tbody></table>`;
}

function sorRenderPagination(data) {
  const el = document.getElementById('sorr-pagination');
  if (!el) return;
  const page = data.page || 1;
  const totalPages = (data.totalPages || 0) === 0 ? 1 : data.totalPages;
  el.innerHTML = `
    <button class="btn btn-neutral btn-sm" ${page <= 1 ? 'disabled' : ''} onclick="loadSalesOrderReceiptReconciliation(${page - 1})">上一页</button>
    <span style="margin:0 10px">第 ${page} / ${totalPages} 页（共 ${data.total} 张订单）</span>
    <button class="btn btn-neutral btn-sm" ${page >= totalPages ? 'disabled' : ''} onclick="loadSalesOrderReceiptReconciliation(${page + 1})">下一页</button>`;
}

/* 导出本页订单明细为 CSV（只导出当前页，口径与页面一致；未知按「未知」导出） */
function exportSorrcsv() {
  const table = document.querySelector('#sorr-order-table table');
  if (!table) { toast('暂无可导出数据', 'warning'); return; }
  const rows = Array.from(table.querySelectorAll('tr'));
  const csv = rows.map(tr => Array.from(tr.querySelectorAll('th,td'))
    .map(td => `"${td.textContent.replace(/"/g, '""')}"`).join(',')).join('\n');
  const blob = new Blob(['\uFEFF' + csv], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = '客户订单与收款核对报表.csv';
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
  toast('CSV 已导出', 'success');
}

/* ============ 客户订单与收款核对报表 · 动态字段 / 筛选设计器（ERP-166：只读、有界） ============
    口径与后端 ERP-164 / ERP-165（DynamicReceiptReconciliationReportController / DynamicReceiptReconciliationReportRules）一一对应：
    - 字段选择器只由 GET /api/sales-orders/dynamic-receipt-reconciliation-report 返回的有限白名单目录渲染，
      订单证据字段（fields）与未关联收款证据字段（receiptFields）两个目录完全独立，绝无自由填写的字段名或 SQL；
    - 筛选只允许客户 / 币种 / 订单日期 / 出货状态 / 收款链接状态 / 收款证据状态 / 订单状态 / 关键字，且只接受枚举取值（下拉）；
    - 预览走 POST /api/sales-orders/dynamic-receipt-reconciliation-report，只发送「白名单字段 + 有界筛选 + 有界分页」，
      订单证据行与未关联收款证据行分别投影、绝不合并，按请求顺序渲染返回的列名与单元格；
    - 全程只读：不写库、不迁移、不执行任意 SQL；授权 / 无效请求 / 空结果 / 网络失败都在界面可见，且不暴露范围外数据。 */

/* 币种枚举（与系统 Currency 枚举名一致；未知取值由后端拒绝，前端也不再发送） */
const DRR_CURRENCY_OPTS = [
  { value: 'CNY', label: 'CNY 人民币' },
  { value: 'USD', label: 'USD 美元' },
  { value: 'EUR', label: 'EUR 欧元' },
  { value: 'HKD', label: 'HKD 港币' },
  { value: 'GBP', label: 'GBP 英镑' },
  { value: 'JPY', label: 'JPY 日元' },
];

/* 出货状态（与后端 none / shipped 一致） */
const DRR_SHIPMENT_STATUS_OPTS = [
  { value: 'none', label: '未出货（无已审核出库单）' },
  { value: 'shipped', label: '已有已审核出库单' },
];

/* 收款链接状态（与后端 linked / partial / unlinked 一致；unknown 只在派生上限出现，不提供筛选） */
const DRR_LINK_STATUS_OPTS = [
  { value: 'linked', label: '已关联（全部可计入）' },
  { value: 'partial', label: '部分可归属（其余未知）' },
  { value: 'unlinked', label: '未关联（金额未知）' },
];

/* 收款证据状态（与后端 active / pending / historical / all 一致） */
const DRR_RECEIPT_STATUS_OPTS = [
  { value: 'active', label: '仅有效收款证据（默认：已审核）' },
  { value: 'pending', label: '仅未审核收款单' },
  { value: 'historical', label: '仅历史收款单' },
  { value: 'all', label: '全部状态（有效 + 未审核 + 历史）' },
];

/* 订单状态（与后端 active / cancelled / all 一致） */
const DRR_ORDER_STATUS_OPTS = [
  { value: 'active', label: '仅有效订单（默认：排除已取消）' },
  { value: 'cancelled', label: '仅已取消订单' },
  { value: 'all', label: '全部未删除订单' },
];

/* 当前页计数分组键（与后端 NormalizeGroupBy 白名单一致：none / customer / currency / receiptCoverageStatus / receiptEvidenceStatus） */
const DRR_GROUP_OPTS = [
  { value: 'none', label: '不分组' },
  { value: 'customer', label: '按客户分组' },
  { value: 'currency', label: '按币种分组' },
  { value: 'receiptCoverageStatus', label: '按收款覆盖状态分组（订单侧）' },
  { value: 'receiptEvidenceStatus', label: '按收款证据状态分组（未关联收款侧）' },
];

/* 当前页金额汇总模式（与后端 NormalizeSummaryMode 白名单一致：none / customerCurrency） */
const DRR_SUMMARY_MODE_OPTS = [
  { value: 'none', label: '不汇总金额' },
  { value: 'customerCurrency', label: '按客户 + 币种汇总金额' },
];

/* 默认每页条数（后端上限 200，由目录 maxPageSize 供给并钳制） */
const DRR_DEFAULT_PAGE_SIZE = 20;
const DRR_MAX_PAGE_SIZE = 200;

/* 设计器状态（纯数据；DOM 访问只在事件处理函数内部发生） */
let DRR = {
  catalog: null,
  fields: [],
  receiptFields: [],
  selectedKeys: [],
  selectedReceiptKeys: [],
  customers: [],
  filters: { page: 1, pageSize: DRR_DEFAULT_PAGE_SIZE },
  view: null,
};

/* ==================== 纯函数（可在 Node 中逐条单测） ==================== */

/* HTML 转义（本地独立实现，避免依赖全局 escapeHtml 的加载顺序） */
function drrEsc(v) {
  return String(v ?? '').replace(/[&<>"']/g, c => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}

/* 规范化选定字段（fail closed）：只保留目录白名单内的键、去重、保持请求顺序；未知键丢弃 */
function drrSelectFields(catalogFields, selectedKeys) {
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

/* 枚举筛选取值：只在白名单内才携带（fail closed：非法取值绝不进入请求） */
function drrEnumValue(value, opts) {
  const v = value === null || value === undefined ? '' : String(value).trim();
  if (!v) return '';
  return (opts || []).some(o => o.value === v) ? v : '';
}

/* 分组键规范化（fail closed）：只保留 ERP-170 白名单（none / customer / currency / receiptCoverageStatus / receiptEvidenceStatus），
   缺失 / 空白 / 非法值一律回落 none（绝不进入请求） */
function drrGroupKey(value) {
  const key = String(value == null ? '' : value).trim();
  if (!key) return 'none';
  const hit = DRR_GROUP_OPTS.find(g => g.value.toLowerCase() === key.toLowerCase());
  return hit ? hit.value : 'none';
}

/* 分组键选择器：仅 ERP-170 白名单（fail closed，无自由输入） */
function drrGroupSelectHtml(groupBy) {
  const selected = drrGroupKey(groupBy);
  const opts = DRR_GROUP_OPTS.map(g =>
    `<option value="${drrEsc(g.value)}" ${g.value === selected ? 'selected' : ''}>${drrEsc(g.label)}</option>`).join('');
  return `<select id="drr-groupby" style="min-width:220px">${opts}</select>`;
}

/* 金额汇总模式规范化（fail closed）：只保留 ERP-172 白名单（none / customerCurrency），
   缺失 / 空白 / 非法值一律回落 none（绝不进入请求） */
function drrSummaryModeKey(value) {
  const key = String(value == null ? '' : value).trim();
  if (!key) return 'none';
  const hit = DRR_SUMMARY_MODE_OPTS.find(m => m.value.toLowerCase() === key.toLowerCase());
  return hit ? hit.value : 'none';
}

/* 金额汇总模式选择器：仅 ERP-172 白名单（fail closed，无自由输入） */
function drrSummaryModeSelectHtml(summaryMode) {
  const selected = drrSummaryModeKey(summaryMode);
  const opts = DRR_SUMMARY_MODE_OPTS.map(m =>
    `<option value="${drrEsc(m.value)}" ${m.value === selected ? 'selected' : ''}>${drrEsc(m.label)}</option>`).join('');
  return `<select id="drr-summary-mode" style="min-width:220px">${opts}</select>`;
}

/* 组装有界预览请求体：字段 / 收款字段只来自目录、分页有界、筛选只取枚举白名单，绝不接受任意字段名或 SQL */
function drrBuildRequest(state) {
  const fields = drrSelectFields(state.catalogFields, state.selectedKeys);
  const receiptFields = drrSelectFields(state.receiptFields, state.selectedReceiptKeys);

  const page = Math.max(1, Math.floor(Number(state.page) || 1));
  const maxPageSize = Number(state.maxPageSize) || DRR_MAX_PAGE_SIZE;
  let pageSize = Math.floor(Number(state.pageSize));
  if (!Number.isFinite(pageSize)) pageSize = DRR_DEFAULT_PAGE_SIZE;
  pageSize = Math.max(1, Math.min(maxPageSize, pageSize));

  const req = { fields, receiptFields, page, pageSize };
  req.groupBy = drrGroupKey(state.groupBy);
  req.summaryMode = drrSummaryModeKey(state.summaryMode);

  const customerId = Number(state.customerId);
  if (Number.isFinite(customerId) && customerId > 0) req.customerId = customerId;

  const currency = drrEnumValue(state.currency, DRR_CURRENCY_OPTS);
  if (currency) req.currency = currency;

  const orderDateFrom = state.orderDateFrom ? String(state.orderDateFrom).slice(0, 10) : '';
  const orderDateTo = state.orderDateTo ? String(state.orderDateTo).slice(0, 10) : '';
  if (orderDateFrom) req.orderDateFrom = orderDateFrom;
  if (orderDateTo) req.orderDateTo = orderDateTo;

  const shipmentStatus = drrEnumValue(state.shipmentStatus, DRR_SHIPMENT_STATUS_OPTS);
  if (shipmentStatus) req.shipmentStatus = shipmentStatus;

  const receiptLinkStatus = drrEnumValue(state.receiptLinkStatus, DRR_LINK_STATUS_OPTS);
  if (receiptLinkStatus) req.receiptLinkStatus = receiptLinkStatus;

  const receiptStatus = drrEnumValue(state.receiptStatus, DRR_RECEIPT_STATUS_OPTS);
  if (receiptStatus) req.receiptStatus = receiptStatus;

  const orderStatus = drrEnumValue(state.orderStatus, DRR_ORDER_STATUS_OPTS);
  if (orderStatus) req.orderStatus = orderStatus;

  const keyword = state.keyword ? String(state.keyword).trim() : '';
  if (keyword) req.keyword = keyword;

  return req;
}

/* 单元格纯文本（安全：null 数值显示「未知」、其余 null 为空、布尔 是/否、日期截断到日；绝不回落为 0） */
function drrCellText(value, field) {
  const dataType = (field && field.dataType) || 'text';
  if (value === null || value === undefined) return dataType === 'number' ? '未知' : '';
  if (dataType === 'boolean') return (value === true || value === 'true' || value === 1 || value === '1') ? '是' : '否';
  if (dataType === 'date') return String(value).slice(0, 10);
  return String(value);
}

/* 单元格 HTML（转义后安全渲染） */
function drrRenderCell(value, field) {
  return drrEsc(drrCellText(value, field));
}

/* 通用结果表格（无分页；表头为返回的列名、单元格为返回的选定字段值，全部经转义） */
function drrTableHtml(columns, rows, emptyText) {
  const cols = (columns || []).filter(c => c && c.key);
  if (!cols.length) return '';
  const align = c => (c.dataType === 'number' || c.dataType === 'boolean') ? ' class="text-right"' : '';
  const head = cols.map(c => `<th${align(c)}>${drrEsc(c.label || c.key)}</th>`).join('');
  const body = (rows || []).map(r =>
    `<tr>${cols.map(c => `<td${align(c)}>${drrRenderCell(r[c.key], c)}</td>`).join('')}</tr>`).join('');
  const emptyRow = `<tr><td colspan="${cols.length}" class="empty">${drrEsc(emptyText || '没有符合条件的数据')}</td></tr>`;
  return `<table><thead><tr>${head}</tr></thead><tbody>${body || emptyRow}</tbody></table>`;
}

/* 订单证据结果区：汇总 + 订单表 + 分页（有界） */
function drrOrderSectionHtml(view) {
  if (!view) return '';
  const summary = `<div class="text-muted" style="margin:6px 0">共 ${view.total} 张订单 · 第 ${view.page} 页 · 每页 ${view.pageSize} 条</div>`;
  const table = drrTableHtml(view.columns, view.rows, '没有符合筛选条件的销售订单证据（可放宽客户 / 币种 / 日期 / 出货 / 收款链接筛选）');
  const prevDisabled = view.page <= 1 ? ' disabled' : '';
  const nextDisabled = view.page >= view.totalPages ? ' disabled' : '';
  const paging = `<div style="display:flex;justify-content:space-between;align-items:center;margin-top:8px">
      <span class="text-muted">第 ${view.page} 页 / 共 ${view.totalPages} 页</span>
      <div>
        <button class="btn btn-neutral btn-sm" onclick="drrPage(-1)"${prevDisabled}>← 上一页</button>
        <button class="btn btn-neutral btn-sm" onclick="drrPage(1)"${nextDisabled}>下一页 →</button>
      </div></div>`;
  return `${summary}${table}${paging}`;
}

/* 未关联收款证据结果区（独立投影：收款字段目录 + 收款行；截断时显式标注，绝不静默截断） */
function drrReceiptSectionHtml(view) {
  if (!view) return '';
  const truncation = view.unlinkedReceiptTruncated
    ? `<div class="pd-hint" style="color:#b45309;background:#fffbeb;border-color:#fde68a">⚠️ 未关联收款证据命中读取上限，本页收款证据被截断（不完整，请缩小筛选范围后重试）。</div>`
    : '';
  const table = drrTableHtml(view.receiptColumns, view.receiptRows, '本页客户没有符合条件的未关联收款单（收款单无订单级引用，只列出、不匹配、不并入订单金额）');
  return `<h4 style="margin:14px 0 6px">🧾 未关联收款证据（收款单仅客户级引用：原币原样列出，绝不匹配 / 并入任何订单）</h4>${truncation}${table}`;
}

/* 单个计数分组面板（仅当前预览页）：标签转义、计数未知（null）绝不回落 0、截断标记显式保留、空页可见提示 */
function drrCountPanelHtml(title, groups, countKey) {
  const items = Array.isArray(groups) ? groups : [];
  const max = Math.max(1, ...items.map(g => {
    const n = Number(g && g[countKey]);
    return Number.isFinite(n) && n > 0 ? n : 0;
  }));
  const rows = items.map(g => {
    const label = drrEsc((g && (g.label || g.key)) || '未知');
    const unknown = !g || g[countKey] === null || g[countKey] === undefined || !Number.isFinite(Number(g[countKey]));
    const count = unknown ? '未知' : String(g[countKey]);
    const pct = unknown ? 0 : Math.max(0, Math.round((Number(g[countKey]) / max) * 100));
    const trunc = (g && g.truncated)
      ? '<span style="color:#b45309;font-size:11px;margin-left:4px">（截断）</span>' : '';
    return `<div style="display:flex;align-items:center;gap:8px;margin:3px 0">`
      + `<span style="min-width:200px;text-align:right;font-size:13px">${label}</span>`
      + `<div style="flex:1;background:#e2e8f0;border-radius:4px;height:12px;overflow:hidden">`
      + `<div style="height:12px;background:#2563eb;width:${pct}%"></div></div>`
      + `<span style="min-width:48px;font-variant-numeric:tabular-nums;font-size:13px">${drrEsc(count)}</span>${trunc}</div>`;
  }).join('');
  const empty = items.length === 0
    ? '<div class="text-muted" style="margin:4px 0">本页没有可分组计数的证据（空页）。</div>'
    : '';
  return `<div class="pd-hint" style="margin:8px 0">`
    + `<div style="font-weight:600;margin-bottom:6px">${drrEsc(title)}（仅当前预览页，非全量合计）</div>`
    + `<div class="text-muted" style="font-size:12px;margin-bottom:4px">计数只统计当前授权预览页，绝不求和金额 / 数量、绝不跨币种合并或换算、绝不推断收款单与订单的匹配关系。</div>`
    + `${rows}${empty}</div>`;
}

/* 当前页订单计数分组面板（ERP-171）：只计数本页订单张数、不含金额；收款覆盖状态 unknown 显式保留；
   收款证据状态分组不作用于订单侧（显式提示不适用） */
function drrOrderGroupPanelHtml(view) {
  const groupBy = (view && view.groupBy) || 'none';
  if (groupBy === 'none' || !view) return '';
  if (groupBy === 'receiptEvidenceStatus') {
    return `<div class="pd-hint" style="margin:8px 0"><div style="font-weight:600;margin-bottom:4px">📊 本页订单计数分组（仅当前预览页）</div>`
      + `<div class="text-muted">按收款证据状态分组只作用于未关联收款证据侧，订单侧不适用（本页无订单计数分组）。</div></div>`;
  }
  const title = {
    customer: '📊 本页订单计数 · 按客户分组',
    currency: '📊 本页订单计数 · 按币种分组',
    receiptCoverageStatus: '📊 本页订单计数 · 按收款覆盖状态分组',
  }[groupBy] || '📊 本页订单计数分组';
  return drrCountPanelHtml(title, view.orderGroups, 'orderCount');
}

/* 当前页未关联收款计数分组面板（ERP-171）：只计数本页未关联收款张数；active / pending / historical 证据状态显式保留，
   截断标记显式保留；收款覆盖状态分组不作用于未关联收款侧（显式提示不适用） */
function drrReceiptGroupPanelHtml(view) {
  const groupBy = (view && view.groupBy) || 'none';
  if (groupBy === 'none' || !view) return '';
  if (groupBy === 'receiptCoverageStatus') {
    return `<div class="pd-hint" style="margin:8px 0"><div style="font-weight:600;margin-bottom:4px">🧾 本页未关联收款计数分组（仅当前预览页）</div>`
      + `<div class="text-muted">按收款覆盖状态分组只作用于订单证据侧，未关联收款侧不适用（本页无收款计数分组）。</div></div>`;
  }
  const title = {
    customer: '🧾 本页未关联收款计数 · 按客户分组',
    currency: '🧾 本页未关联收款计数 · 按币种分组',
    receiptEvidenceStatus: '🧾 本页未关联收款计数 · 按收款证据状态分组',
  }[groupBy] || '🧾 本页未关联收款计数分组';
  return drrCountPanelHtml(title, view.receiptGroups, 'receiptCount');
}

/* 两个独立计数面板（订单 + 未关联收款），仅当前预览页 */
function drrGroupPanelsHtml(view) {
  const groupBy = (view && view.groupBy) || 'none';
  if (groupBy === 'none' || !view) return '';
  return drrOrderGroupPanelHtml(view) + drrReceiptGroupPanelHtml(view);
}

/* 金额显示（自包含，避免依赖全局 fmtMoney 的加载顺序）：null / undefined / 空串 → 未知；
   其余按两位小数原币显示（绝不换算） */
function drrMoney(v) {
  if (v === null || v === undefined || v === '') return '未知';
  const n = Number(v);
  if (!Number.isFinite(n)) return '未知';
  return n.toFixed(2);
}

/* 金额 + 已知 / 未知行数（null 合计绝不回落为 0，显式保留未知语义） */
function drrSummaryAmountHtml(amount, known, unknown) {
  const amt = drrMoney(amount);
  const k = (known === null || known === undefined) ? '未知' : String(known);
  const u = (unknown === null || unknown === undefined) ? '未知' : String(unknown);
  return `${drrEsc(amt)}（已知 ${drrEsc(k)} 行 / 未知 ${drrEsc(u)} 行）`;
}

/* 当前页订单金额汇总面板（ERP-173）：客户 + 原币强制分组边界；订单金额 / 已关联收款金额 / 未覆盖金额各自独立呈现，
   任一行金额未知则整体显示「未知」（不是 0）并给出已知 / 未知行数；绝不跨币种合并 / 换算、绝不推断收款分配或应收余额 */
function drrOrderSummaryPanelHtml(view) {
  const mode = (view && view.summaryMode) || 'none';
  if (mode !== 'customerCurrency' || !view) return '';
  const items = Array.isArray(view.orderSummaries) ? view.orderSummaries : [];
  const rows = items.map(s => {
    const name = drrEsc((s && (s.customerName || ('客户 ' + s.customerId))) || '未知');
    const currency = drrEsc((s && s.currency) || '未知');
    const orderCount = (s && s.orderCount !== null && s.orderCount !== undefined && Number.isFinite(Number(s.orderCount)))
      ? String(s.orderCount) : '未知';
    const orderAmount = drrMoney(s && s.orderAmount);
    const linked = drrSummaryAmountHtml(s && s.linkedReceiptAmount, s && s.knownLinkedReceiptAmountRows, s && s.unknownLinkedReceiptAmountRows);
    const uncovered = drrSummaryAmountHtml(s && s.uncoveredAmount, s && s.knownUncoveredAmountRows, s && s.unknownUncoveredAmountRows);
    return `<tr><td>${name}</td><td>${currency}</td><td class="text-right">${orderCount}</td><td class="text-right">${drrEsc(orderAmount)}</td><td class="text-right">${linked}</td><td class="text-right">${uncovered}</td></tr>`;
  }).join('');
  const empty = items.length === 0
    ? '<tr><td colspan="6" class="empty">本页没有可汇总金额的订单证据（空页）。</td></tr>' : '';
  return `<div class="pd-hint" style="margin:8px 0">`
    + `<div style="font-weight:600;margin-bottom:6px">💰 本页订单金额汇总 · 按客户 + 原币（仅当前预览页，非全量合计）</div>`
    + `<div class="text-muted" style="font-size:12px;margin-bottom:4px">金额只汇总当前授权预览页的订单证据；已关联 / 未覆盖收款证据合计只要任一行金额未知即整体显示「未知」（不是 0），绝不跨币种合并 / 换算、绝不推断收款分配或应收余额。</div>`
    + `<table><thead><tr><th>客户</th><th>币种</th><th class="text-right">订单张数</th><th class="text-right">订单金额</th><th class="text-right">已关联收款金额</th><th class="text-right">未覆盖金额</th></tr></thead>`
    + `<tbody>${rows}${empty}</tbody></table></div>`;
}

/* 当前页未关联收款金额汇总面板（ERP-173）：客户 + 原币 + 收款证据状态显式拆分；
   active / pending / historical 不回落、不并入有效合计；截断标记显式保留；绝不跨币种合并 / 换算、绝不并入订单侧合计 */
function drrReceiptSummaryPanelHtml(view) {
  const mode = (view && view.summaryMode) || 'none';
  if (mode !== 'customerCurrency' || !view) return '';
  const items = Array.isArray(view.receiptSummaries) ? view.receiptSummaries : [];
  const rows = items.map(s => {
    const name = drrEsc((s && (s.customerName || ('客户 ' + s.customerId))) || '未知');
    const currency = drrEsc((s && s.currency) || '未知');
    const status = drrEsc((s && (SORR_EVIDENCE_LABELS[s.evidenceStatus] || s.evidenceStatus)) || '未知');
    const count = (s && s.receiptCount !== null && s.receiptCount !== undefined && Number.isFinite(Number(s.receiptCount)))
      ? String(s.receiptCount) : '未知';
    const trunc = (s && s.truncated)
      ? '<span style="color:#b45309;font-size:11px;margin-left:4px">（截断）</span>' : '';
    return `<tr><td>${name}</td><td>${currency}</td><td>${status}</td><td class="text-right">${count}</td><td class="text-right">${drrEsc(drrMoney(s && s.amount))}${trunc}</td></tr>`;
  }).join('');
  const empty = items.length === 0
    ? '<tr><td colspan="5" class="empty">本页没有可汇总金额的未关联收款证据（空页）。</td></tr>' : '';
  return `<div class="pd-hint" style="margin:8px 0">`
    + `<div style="font-weight:600;margin-bottom:6px">🧾 本页未关联收款金额汇总 · 按客户 + 原币 + 收款证据状态（仅当前预览页，非全量合计）</div>`
    + `<div class="text-muted" style="font-size:12px;margin-bottom:4px">未关联收款金额只按收款单自身原币原样汇总；active / pending / historical 证据状态显式拆分（pending / historical 不并入有效合计），绝不跨币种合并 / 换算、绝不并入订单侧合计。</div>`
    + `<table><thead><tr><th>客户</th><th>币种</th><th>收款证据状态</th><th class="text-right">收款单张数</th><th class="text-right">金额</th></tr></thead>`
    + `<tbody>${rows}${empty}</tbody></table></div>`;
}

/* 两个独立金额汇总面板（订单 + 未关联收款），仅当前预览页、仅 customerCurrency 模式 */
function drrSummaryPanelsHtml(view) {
  const mode = (view && view.summaryMode) || 'none';
  if (mode !== 'customerCurrency' || !view) return '';
  return drrOrderSummaryPanelHtml(view) + drrReceiptSummaryPanelHtml(view);
}

/* 空结果提示 */
function drrEmptyHtml() {
  return '<div class="empty" style="margin:8px 0">没有符合条件的订单证据（当前账号数据范围内的只读快照）。</div>';
}

/* 加载中提示 */
function drrLoadingHtml() {
  return '<div class="pd-hint" style="text-align:center;color:#64748b">正在预览（只读查询）…</div>';
}

/* 错误提示（权限 / 未登录 / 无效请求 / 网络失败分别可见，且不暴露任何数据） */
function drrErrorHtml(kind, message) {
  const labels = {
    forbidden: '权限不足',
    unauthorized: '未登录 / 登录已过期',
    invalid: '请求无效',
    empty: '暂无数据',
    network: '网络请求失败',
    error: '预览失败',
  };
  return `<div class="pd-hint" style="color:#b91c1c;background:#fef2f2;border-color:#fecaca">
      <b>${drrEsc(labels[kind] || '预览失败')}</b>：${drrEsc(message || '')}</div>`;
}

/* 预览结果：只读 / 边界 / 免责文案 + 订单证据 + 独立未关联收款证据 */
function drrResultHtml(view) {
  if (!view) return drrEmptyHtml();
  const readOnly = view.readOnlyText ? `<div class="pd-hint">${drrEsc(view.readOnlyText)}</div>` : '';
  const boundary = view.boundaryText ? `<div class="pd-hint">${drrEsc(view.boundaryText)}</div>` : '';
  const disclaimer = view.disclaimerText ? `<div class="pd-hint" style="color:#64748b">${drrEsc(view.disclaimerText)}</div>` : '';
  return `${readOnly}${boundary}${disclaimer}${drrGroupPanelsHtml(view)}${drrSummaryPanelsHtml(view)}${drrOrderSectionHtml(view)}${drrReceiptSectionHtml(view)}`;
}

/* 字段选择器：仅由目录白名单渲染为复选框，无自由填写的字段名 */
function drrFieldChooserHtml(fields, selectedKeys, name) {
  const selected = new Set(selectedKeys || []);
  return (fields || []).map(f => {
    const checked = selected.has(f.key) ? 'checked' : '';
    const onChange = name === 'drr-receipt-field' ? 'drrSyncReceiptSelection()' : 'drrSyncOrderSelection()';
    return `<label style="display:inline-flex;align-items:center;gap:4px;margin:3px 6px 3px 0;padding:2px 8px;border:1px solid #e2e8f0;border-radius:6px;background:#f8fafc;cursor:pointer">
        <input type="checkbox" name="${drrEsc(name)}" value="${drrEsc(f.key)}" ${checked} onchange="${onChange}">
        <span>${drrEsc(f.label || f.key)}</span></label>`;
  }).join('');
}

/* 业务码 → 错误态分类（与后端 ErrorCodes 一致：2000/2003 未登录，2002 权限不足，其余按无效请求） */
function drrKindOfCode(code) {
  if (code === 2002) return 'forbidden';
  if (code === 2000 || code === 2003) return 'unauthorized';
  return 'invalid';
}

/* ==================== CSV 导出（纯函数，当前页、两类证据分开导出） ==================== */

/* CSV 单元格：null/undefined 保留为空（未知绝不回落为 0），
   文本类首字符为 = + - @ / 制表符 / 回车时前缀单引号防公式注入，含逗号 / 引号 / 换行时按 RFC4180 加引号 */
function drrCsvCell(value, field) {
  if (value === null || value === undefined) return '';
  const dataType = (field && field.dataType) || 'text';
  let s = String(value);
  if (dataType !== 'number' && dataType !== 'boolean' && dataType !== 'date') {
    if (/^[=+\-@\t\r]/.test(s)) s = "'" + s;
  }
  if (/[",\r\n]/.test(s)) s = '"' + s.replace(/"/g, '""') + '"';
  return s;
}

/* 组装当前页 CSV 正文（表头用返回列名；行按列键投影，null 保留为空） */
function drrBuildCsv(columns, rows) {
  const cols = (columns || []).filter(c => c && c.key);
  if (!cols.length) return '';
  const head = cols.map(c => drrCsvCell(c.label || c.key, { dataType: 'text' })).join(',');
  const body = (rows || []).map(r => cols.map(c => drrCsvCell(r[c.key], c)).join(','));
  return [head].concat(body).join('\n');
}

/* ==================== 状态 / 请求 / 渲染（DOM 访问只在事件处理函数内部发生） ==================== */

/* 轻量请求封装：返回完整 ApiResponse 信封（保留 code）；网络异常抛给调用方 */
async function drrRequest(path, method = 'GET', body = null) {
  const headers = { 'Content-Type': 'application/json' };
  const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
  if (token) headers['Authorization'] = 'Bearer ' + token;
  const opts = { method, headers };
  if (body) opts.body = JSON.stringify(body);
  const resp = await fetch(path, opts);
  return await resp.json();
}

function drrRenderResult(html) {
  const el = document.getElementById('drr-result');
  if (el) el.innerHTML = html;
}

/* 同步订单证据勾选状态到 selectedKeys */
function drrSyncOrderSelection() {
  const boxes = document.querySelectorAll('input[name="drr-field"]');
  DRR.selectedKeys = Array.from(boxes).filter(b => b.checked).map(b => b.value);
}

/* 同步未关联收款证据勾选状态到 selectedReceiptKeys */
function drrSyncReceiptSelection() {
  const boxes = document.querySelectorAll('input[name="drr-receipt-field"]');
  DRR.selectedReceiptKeys = Array.from(boxes).filter(b => b.checked).map(b => b.value);
}

function drrToggleOrderAll(checked) {
  const boxes = document.querySelectorAll('input[name="drr-field"]');
  DRR.selectedKeys = [];
  boxes.forEach(b => { b.checked = checked; if (checked) DRR.selectedKeys.push(b.value); });
}

function drrToggleReceiptAll(checked) {
  const boxes = document.querySelectorAll('input[name="drr-receipt-field"]');
  DRR.selectedReceiptKeys = [];
  boxes.forEach(b => { b.checked = checked; if (checked) DRR.selectedReceiptKeys.push(b.value); });
}

/* 从销售订单核对报表页打开设计器（加载目录 + 客户，渲染订单 / 收款字段选择器与筛选器） */
async function openDynamicReceiptReconciliationDesigner() {
  CURRENT_PAGE_CODE = 'sales-order-receipt-reconciliation';
  document.getElementById('header-title').textContent = '客户订单与收款核对报表 · 动态设计器';
  const content = document.getElementById('content');
  content.innerHTML = '<div class="pd-hint" style="text-align:center;color:#64748b">正在加载字段目录…</div>';

  DRR = {
    catalog: null, fields: [], receiptFields: [],
    selectedKeys: [], selectedReceiptKeys: [], customers: [],
    filters: { page: 1, pageSize: DRR_DEFAULT_PAGE_SIZE }, view: null,
  };

  try {
    const resp = await drrRequest('/api/sales-orders/dynamic-receipt-reconciliation-report', 'GET');
    if (resp.code === 0) {
      DRR.catalog = resp.data;
    } else if (resp.code === 2000 || resp.code === 2003) {
      if (typeof logout === 'function') logout();
      content.innerHTML = drrErrorHtml('unauthorized', resp.message);
      return;
    } else {
      content.innerHTML = drrErrorHtml(drrKindOfCode(resp.code), resp.message);
      return;
    }
  } catch (err) {
    content.innerHTML = drrErrorHtml('network', (err && err.message) || '无法连接到服务器');
    return;
  }

  DRR.fields = (DRR.catalog && DRR.catalog.fields) || [];
  DRR.receiptFields = (DRR.catalog && DRR.catalog.receiptFields) || [];
  DRR.selectedKeys = DRR.fields.map(f => f.key);
  DRR.selectedReceiptKeys = DRR.receiptFields.map(f => f.key);
  const maxPageSize = (DRR.catalog && DRR.catalog.maxPageSize) || DRR_MAX_PAGE_SIZE;
  DRR.filters.pageSize = Math.min(DRR_DEFAULT_PAGE_SIZE, maxPageSize);

  try {
    const cresp = await drrRequest('/api/base/customers?page=1&pageSize=500');
    if (cresp.code === 0) {
      DRR.customers = (cresp.data && cresp.data.items) || (Array.isArray(cresp.data) ? cresp.data : []) || [];
    }
  } catch (e) {
    DRR.customers = [];
  }

  drrRender();
}

/* 渲染设计器（订单 / 收款字段选择器 + 有界筛选器 + 预览按钮 + 结果区） */
function drrRender() {
  const f = DRR.filters;
  const maxPage = (DRR.catalog && DRR.catalog.maxPageSize) || DRR_MAX_PAGE_SIZE;

  const customerOptions = DRR.customers.map(c =>
    `<option value="${drrEsc(c.id)}" ${String(c.id) === String(f.customerId || '') ? 'selected' : ''}>${drrEsc(c.customerName || ('客户 ' + c.id))}</option>`).join('');
  const currencyOptions = DRR_CURRENCY_OPTS.map(o =>
    `<option value="${o.value}" ${f.currency === o.value ? 'selected' : ''}>${o.label}</option>`).join('');
  const shipmentOptions = DRR_SHIPMENT_STATUS_OPTS.map(o =>
    `<option value="${o.value}" ${f.shipmentStatus === o.value ? 'selected' : ''}>${o.label}</option>`).join('');
  const linkOptions = DRR_LINK_STATUS_OPTS.map(o =>
    `<option value="${o.value}" ${f.receiptLinkStatus === o.value ? 'selected' : ''}>${o.label}</option>`).join('');
  const receiptStatusOptions = DRR_RECEIPT_STATUS_OPTS.map(o =>
    `<option value="${o.value}" ${f.receiptStatus === o.value ? 'selected' : ''}>${o.label}</option>`).join('');
  const orderStatusOptions = DRR_ORDER_STATUS_OPTS.map(o =>
    `<option value="${o.value}" ${f.orderStatus === o.value ? 'selected' : ''}>${o.label}</option>`).join('');

  document.getElementById('content').innerHTML = `
  <div class="page-hero report-hero">
    <h2>🔧 客户订单与收款核对报表 · 动态设计器</h2>
    <p>目录驱动的订单证据与未关联收款证据字段 / 筛选设计器（只读：仅按 ERP-046 白名单字段与有界筛选预览，绝不写入、绝不执行任意 SQL）</p>
  </div>

  <div class="pd-hint" style="margin-bottom:10px">
    ⚠️ 本设计器是运营性订单 / 收款证据核对视图：<b>不是</b>应收账款台账，<b>不是</b>客户对账单，<b>不是</b>收款授权或结算结果，也<b>不是</b>账龄表；
    收款申请链接证据、收款引用登记证据、销项发票登记证据与未关联收款证据<b>各自独立、绝不合并</b>，未知金额 / 数量一律 null（不是 0）。
  </div>

  <div style="margin:10px 0">
    <div style="font-weight:600;margin-bottom:6px">① 选择订单证据字段（仅 ERP-046 订单字段白名单目录，无自由字段名）</div>
    <div style="margin-bottom:6px">
      <button class="btn btn-neutral btn-sm" onclick="drrToggleOrderAll(true)">全选</button>
      <button class="btn btn-neutral btn-sm" onclick="drrToggleOrderAll(false)">清空</button>
    </div>
    <div style="max-height:180px;overflow:auto;border:1px solid #e2e8f0;border-radius:8px;padding:8px">${drrFieldChooserHtml(DRR.fields, DRR.selectedKeys, 'drr-field')}</div>
  </div>

  <div style="margin:10px 0">
    <div style="font-weight:600;margin-bottom:6px">② 选择未关联收款证据字段（独立收款字段白名单目录，与订单字段目录完全分开）</div>
    <div style="margin-bottom:6px">
      <button class="btn btn-neutral btn-sm" onclick="drrToggleReceiptAll(true)">全选</button>
      <button class="btn btn-neutral btn-sm" onclick="drrToggleReceiptAll(false)">清空</button>
    </div>
    <div style="max-height:160px;overflow:auto;border:1px solid #e2e8f0;border-radius:8px;padding:8px">${drrFieldChooserHtml(DRR.receiptFields, DRR.selectedReceiptKeys, 'drr-receipt-field')}</div>
  </div>

  <div style="margin:10px 0">
    <div style="font-weight:600;margin-bottom:6px">③ 有界筛选（客户 / 币种 / 订单日期 / 出货状态 / 收款链接 / 收款证据 / 订单状态 / 关键字 / 每页 / 当前页分组 / 金额汇总模式）</div>
    <div class="form-grid" style="grid-template-columns:repeat(3,1fr);gap:10px">
      <label>订单日期从 <input type="date" id="drr-date-from" value="${drrEsc(f.orderDateFrom || '')}" style="width:100%"></label>
      <label>至 <input type="date" id="drr-date-to" value="${drrEsc(f.orderDateTo || '')}" style="width:100%"></label>
      <label>客户 <select id="drr-customer" style="width:100%"><option value="">全部客户</option>${customerOptions}</select></label>
      <label>币种 <select id="drr-currency" style="width:100%"><option value="">全部币种</option>${currencyOptions}</select></label>
      <label>出货状态 <select id="drr-shipment-status" style="width:100%"><option value="">全部</option>${shipmentOptions}</select></label>
      <label>收款链接 <select id="drr-link-status" style="width:100%"><option value="">全部</option>${linkOptions}</select></label>
      <label>收款证据 <select id="drr-receipt-status" style="width:100%"><option value="">全部</option>${receiptStatusOptions}</select></label>
      <label>订单状态 <select id="drr-order-status" style="width:100%"><option value="">全部</option>${orderStatusOptions}</select></label>
      <label>关键字 <input type="text" id="drr-keyword" value="${drrEsc(f.keyword || '')}" style="width:100%" placeholder="订单号 / 合同号 / 客户 PO 号"></label>
      <label>每页 <input type="number" id="drr-pagesize" value="${Number(f.pageSize)}" min="1" max="${maxPage}" style="width:100%"></label>
      <label>当前页分组 ${drrGroupSelectHtml(f.groupBy)}</label>
      <label>金额汇总模式 ${drrSummaryModeSelectHtml(f.summaryMode)}</label>
    </div>
  </div>

  <div class="toolbar" style="margin:10px 0">
    <div class="toolbar-left"></div>
    <div class="toolbar-actions">
      <button class="btn btn-primary" onclick="drrPreview(1)">预览</button>
      <button class="btn btn-neutral" onclick="drrExportOrderCsv()" title="导出当前页订单证据为 CSV（两类证据分开导出）">📤 导出订单证据 CSV</button>
      <button class="btn btn-neutral" onclick="drrExportReceiptCsv()" title="导出当前页未关联收款证据为 CSV">📤 导出未关联收款 CSV</button>
      <button class="btn btn-neutral" onclick="drrExportExcel()" title="导出当前页订单证据与未关联收款证据为两个独立工作表的 Excel（复用当前字段 / 筛选 / 分页）">📥 导出 Excel（选定列）</button>
      <button class="btn btn-neutral" onclick="drrExportPdf()" title="导出当前页订单证据与未关联收款证据为分页中文 PDF（两类证据独立分区、宽列自动分页、缺失 SimHei 显式失败）">📄 导出 PDF（当前页）</button>
      <button class="btn btn-neutral" onclick="openSalesOrderReceiptReconciliationReport()">← 返回核对报表</button>
    </div>
  </div>

  <div id="drr-result"></div>`;
}

/* 读取当前字段 / 筛选 / 分页状态（预览与导出复用，单一来源） */
function drrBuildState(page) {
  return {
    catalogFields: DRR.fields,
    receiptFields: DRR.receiptFields,
    selectedKeys: DRR.selectedKeys,
    selectedReceiptKeys: DRR.selectedReceiptKeys,
    orderDateFrom: document.getElementById('drr-date-from').value,
    orderDateTo: document.getElementById('drr-date-to').value,
    customerId: document.getElementById('drr-customer').value,
    currency: document.getElementById('drr-currency').value,
    shipmentStatus: document.getElementById('drr-shipment-status').value,
    receiptLinkStatus: document.getElementById('drr-link-status').value,
    receiptStatus: document.getElementById('drr-receipt-status').value,
    orderStatus: document.getElementById('drr-order-status').value,
    keyword: document.getElementById('drr-keyword').value,
    groupBy: document.getElementById('drr-groupby').value,
    summaryMode: document.getElementById('drr-summary-mode').value,
    pageSize: document.getElementById('drr-pagesize').value,
    page: page || 1,
    maxPageSize: (DRR.catalog && DRR.catalog.maxPageSize) || DRR_MAX_PAGE_SIZE,
  };
}

/* 预览：组装有界请求 → POST → 安全渲染列名与单元格；授权 / 无效 / 空 / 网络失败均可见 */
async function drrPreview(page) {
  const state = drrBuildState(page);
  if (state.orderDateFrom && state.orderDateTo && state.orderDateFrom > state.orderDateTo) {
    drrRenderResult(drrErrorHtml('invalid', '订单日期开始不能晚于结束'));
    return;
  }

  const req = drrBuildRequest(state);
  DRR.filters.page = req.page;
  DRR.filters.pageSize = req.pageSize;

  drrRenderResult(drrLoadingHtml());

  try {
    const resp = await drrRequest('/api/sales-orders/dynamic-receipt-reconciliation-report', 'POST', req);
    if (resp.code === 0) {
      DRR.view = resp.data;
      DRR.filters.page = resp.data.page;
      drrRenderResult(drrResultHtml(resp.data));
    } else if (resp.code === 2000 || resp.code === 2003) {
      if (typeof logout === 'function') logout();
      drrRenderResult(drrErrorHtml('unauthorized', resp.message));
    } else {
      drrRenderResult(drrErrorHtml(drrKindOfCode(resp.code), resp.message));
    }
  } catch (err) {
    drrRenderResult(drrErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 翻页（有界：最小第 1 页） */
function drrPage(delta) {
  const view = DRR.view;
  const page = (view ? view.page : DRR.filters.page) + delta;
  if (page < 1) return;
  drrPreview(page);
}

/* 下载当前页 CSV（两类证据分开；未知 null 保留为空、防公式注入） */
function drrDownloadCsv(filename, csv) {
  if (!csv) { toast('暂无可导出数据', 'warning'); return; }
  const blob = new Blob(['\uFEFF' + csv], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
  toast('CSV 已导出', 'success');
}

function drrExportOrderCsv() {
  const view = DRR.view;
  if (!view) { toast('请先预览再导出', 'warning'); return; }
  const csv = drrBuildCsv(view.columns, view.rows);
  drrDownloadCsv('客户订单证据_' + (view.page || 1) + '.csv', csv);
}

function drrExportReceiptCsv() {
  const view = DRR.view;
  if (!view) { toast('请先预览再导出', 'warning'); return; }
  const csv = drrBuildCsv(view.receiptColumns, view.receiptRows);
  drrDownloadCsv('未关联收款证据_' + (view.page || 1) + '.csv', csv);
}

/* 导出当前页为 Excel（ERP-167，只读）：复用预览请求体 POST /api/sales-orders/dynamic-receipt-reconciliation-report/export；
   把当前页订单证据与未关联收款证据写入两个独立工作表；成功（xlsx 附件）触发下载；
   授权 / 无效 / 空结果 / 网络失败在结果区可见，不下载任何内容 */
async function drrExportExcel() {
  const view = DRR.view;
  if (!view || !view.columns || !view.columns.length) {
    drrRenderResult(drrErrorHtml('invalid', '请先预览后再导出 Excel'));
    return;
  }
  const hasOrder = view.rows && view.rows.length > 0;
  const hasReceipt = view.receiptRows && view.receiptRows.length > 0;
  if (!hasOrder && !hasReceipt) {
    drrRenderResult(drrErrorHtml('empty', '没有符合条件的订单证据或未关联收款证据，无法导出（请先预览）'));
    return;
  }

  const state = drrBuildState(view ? view.page : DRR.filters.page);
  if (state.orderDateFrom && state.orderDateTo && state.orderDateFrom > state.orderDateTo) {
    drrRenderResult(drrErrorHtml('invalid', '订单日期开始不能晚于结束'));
    return;
  }

  const req = drrBuildRequest(state);
  drrRenderResult(drrLoadingHtml());

  try {
    const headers = { 'Content-Type': 'application/json' };
    const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
    if (token) headers['Authorization'] = 'Bearer ' + token;
    const resp = await fetch('/api/sales-orders/dynamic-receipt-reconciliation-report/export', {
      method: 'POST',
      headers,
      body: JSON.stringify(req),
    });

    const contentType = (resp.headers.get('content-type') || '');
    if (contentType.indexOf('spreadsheetml') >= 0) {
      const blob = await resp.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      const dateStr = new Date().toISOString().slice(0, 10).replace(/-/g, '');
      a.href = url;
      a.download = '客户订单与收款核对报表_' + dateStr + '.xlsx';
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
      toast('Excel 已导出（当前页 · 两个独立工作表）', 'success');
      return;
    }

    let envelope = null;
    try { envelope = await resp.json(); } catch (e) { /* 忽略解析失败 */ }
    const code = envelope && envelope.code;
    const message = (envelope && envelope.message) || '导出失败';
    if (code === 2000 || code === 2003) {
      if (typeof logout === 'function') logout();
      drrRenderResult(drrErrorHtml('unauthorized', message));
      return;
    }
    drrRenderResult(drrErrorHtml(drrKindOfCode(code), message));
  } catch (err) {
    drrRenderResult(drrErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 导出当前页为 PDF（ERP-169，只读）：复用预览请求体 POST /api/sales-orders/dynamic-receipt-reconciliation-report/export-pdf；
   把当前页订单证据与未关联收款证据渲染为两个独立分区（宽列自动分页、缺失中文字体 SimHei 显式失败）；
   授权 / 无效 / 空结果 / 网络失败在结果区可见，不下载任何内容 */
async function drrExportPdf() {
  const view = DRR.view;
  if (!view || !view.columns || !view.columns.length) {
    drrRenderResult(drrErrorHtml('invalid', '请先预览后再导出 PDF'));
    return;
  }
  const hasOrder = view.rows && view.rows.length > 0;
  const hasReceipt = view.receiptRows && view.receiptRows.length > 0;
  if (!hasOrder && !hasReceipt) {
    drrRenderResult(drrErrorHtml('empty', '没有符合条件的订单证据或未关联收款证据，无法导出（请先预览）'));
    return;
  }

  const state = drrBuildState(view ? view.page : DRR.filters.page);
  if (state.orderDateFrom && state.orderDateTo && state.orderDateFrom > state.orderDateTo) {
    drrRenderResult(drrErrorHtml('invalid', '订单日期开始不能晚于结束'));
    return;
  }

  const req = drrBuildRequest(state);
  drrRenderResult(drrLoadingHtml());

  try {
    const headers = { 'Content-Type': 'application/json' };
    const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
    if (token) headers['Authorization'] = 'Bearer ' + token;
    const resp = await fetch('/api/sales-orders/dynamic-receipt-reconciliation-report/export-pdf', {
      method: 'POST',
      headers,
      body: JSON.stringify(req),
    });

    const contentType = (resp.headers.get('content-type') || '');
    if (contentType.indexOf('application/pdf') >= 0) {
      const blob = await resp.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      const dateStr = new Date().toISOString().slice(0, 10).replace(/-/g, '');
      a.href = url;
      a.download = '客户订单与收款核对报表_' + dateStr + '.pdf';
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
      toast('PDF 已导出（当前页 · 订单证据与未关联收款证据独立分区）', 'success');
      return;
    }

    let envelope = null;
    try { envelope = await resp.json(); } catch (e) { /* 忽略解析失败 */ }
    const code = envelope && envelope.code;
    const message = (envelope && envelope.message) || '导出失败';
    if (code === 2000 || code === 2003) {
      if (typeof logout === 'function') logout();
      drrRenderResult(drrErrorHtml('unauthorized', message));
      return;
    }
    drrRenderResult(drrErrorHtml(drrKindOfCode(code), message));
  } catch (err) {
    drrRenderResult(drrErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}


/* Node 单测导出（浏览器中 module 为 undefined，自动跳过） */
if (typeof module !== 'undefined' && module.exports) {
  module.exports = {
    DRR_CURRENCY_OPTS,
    DRR_SHIPMENT_STATUS_OPTS,
    DRR_LINK_STATUS_OPTS,
    DRR_RECEIPT_STATUS_OPTS,
    DRR_ORDER_STATUS_OPTS,
    DRR_GROUP_OPTS,
    drrEsc,
    drrSelectFields,
    drrEnumValue,
    drrBuildRequest,
    drrCellText,
    drrRenderCell,
    drrTableHtml,
    drrOrderSectionHtml,
    drrReceiptSectionHtml,
    drrEmptyHtml,
    drrLoadingHtml,
    drrErrorHtml,
    drrResultHtml,
    drrFieldChooserHtml,
    drrKindOfCode,
    DRR_SUMMARY_MODE_OPTS,
    drrGroupKey,
    drrGroupSelectHtml,
    drrOrderGroupPanelHtml,
    drrReceiptGroupPanelHtml,
    drrGroupPanelsHtml,
    drrSummaryModeKey,
    drrSummaryModeSelectHtml,
    drrMoney,
    drrSummaryAmountHtml,
    drrOrderSummaryPanelHtml,
    drrReceiptSummaryPanelHtml,
    drrSummaryPanelsHtml,
    drrCsvCell,
    drrBuildCsv,
    drrExportExcel,
    drrExportPdf,
  };
}

