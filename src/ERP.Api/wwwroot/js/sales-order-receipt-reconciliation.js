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

