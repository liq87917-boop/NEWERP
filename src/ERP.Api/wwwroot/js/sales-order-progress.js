/* ============ 销售订单出货与财务进度（ERP-032：只读派生；出货数量来自既有销售出库单，收款链接复用 ERP-028 的既有引用规则） ============
   口径与后端 SalesOrderProgress / SalesOrderShipmentFinanceReport 一一对应：
   - 出货数量只按「以本单为来源、未删除、已审核」的销售出库单明细派生（待提交 / 已提交只单列，已驳回 / 已取消不计入；库存流水不叠加）；
   - 只有定金 / 货款申请单以 SalesOrderId 指向本单才是权威引用，且只有「已审核 + 币种一致」计入已关联金额；
   - 未知 / 未链接一律显示「未知」或「未链接」，绝不回落为 0；本页不是应收账款台账 / 账龄表（见页脚声明）。
   筛选与数据全部走既有只读接口：GET /api/sales-orders/shipment-finance-report、GET /api/sales-orders/{id}/progress */

/* 出货状态文案（与后端 SalesOrderProgress 常量一一对应） */
const SOP_SHIPMENT_LABELS = {
  none: '未出货',
  partial: '部分出货',
  complete: '已出齐',
  over_shipped: '超发',
  unknown: '未知（超出派生上限）',
};

/* 收款链接状态文案（与后端 SalesOrderProgress 常量一一对应） */
const SOP_FINANCE_LABELS = {
  linked: '收款引用完整',
  partial: '部分可归属（其余未知）',
  unlinked: '未链接（金额未知）',
  unknown: '未知（超出派生上限）',
};

/* 工具栏入口（销售订单页）：渲染独立报表页（只读，不落库） */
function openSalesOrderShipmentFinanceReport() {
  CURRENT_PAGE_CODE = 'sales-order-shipment-finance-report';
  document.getElementById('header-title').textContent = '销售订单出货 / 财务进度报表';
  document.getElementById('content').innerHTML = `
    <div class="page-hero report-hero">
      <h2>🚚 销售订单出货 / 财务进度报表</h2>
      <p>按客户 + 币种聚合销售订单 · 出货数量来自已审核销售出库单 · 收款链接复用财务核对的权威引用口径（只读派生，未知不推断）</p>
    </div>

    <div class="pd-hint" style="margin-bottom:10px">
      ⚠️ 只读视图：<b>不是</b>应收账款台账，也<b>不是</b>账龄表 —— 不创建发票 / 应收记录、不推算账期与到期日；
      「未覆盖金额」只是订单金额与权威计入金额之差，不得当作应收余额或据以催收。
    </div>

    <div class="toolbar">
      <div class="toolbar-left" style="flex-wrap:wrap;gap:8px;align-items:center">
        <label>客户 <select id="sop-customer" style="min-width:170px"><option value="">全部客户</option></select></label>
        <label>币种 <select id="sop-currency" style="min-width:130px"><option value="">全部币种</option></select></label>
        <label>订单日期 <input type="date" id="sop-date-from" style="width:140px"> 至
          <input type="date" id="sop-date-to" style="width:140px"></label>
        <label>出货状态 <select id="sop-shipment-status" style="min-width:170px">
          <option value="">全部</option>
          <option value="none">未出货（无已审核出库单）</option>
          <option value="shipped">已有已审核出库单</option>
        </select></label>
        <label>收款链接 <select id="sop-finance-status" style="min-width:190px">
          <option value="">全部</option>
          <option value="linked">收款引用完整</option>
          <option value="partial">部分可归属（其余未知）</option>
          <option value="unlinked">未链接（金额未知）</option>
        </select></label>
        <label>关键字 <input type="text" id="sop-keyword" style="width:190px" placeholder="订单号 / 合同号 / 客户 PO 号"></label>
        <label>每页 <input type="number" id="sop-pagesize" value="50" min="1" max="200" style="width:70px"></label>
      </div>
      <div class="toolbar-actions">
        <button class="btn btn-primary" onclick="loadSalesOrderShipmentFinance(1)">查询</button>
        <button class="btn btn-neutral" onclick="exportSopCsv()" title="导出本页订单明细为 CSV">📤 导出 CSV</button>
      </div>
    </div>

    <div class="kpi-grid" id="sop-kpi"></div>
    <div class="table-wrap" id="sop-currency-table"></div>
    <div class="table-wrap" id="sop-group-table"></div>
    <div class="table-wrap" id="sop-order-table">
      <div class="skeleton-row"></div><div class="skeleton-row"></div>
    </div>
    <div class="pd-hint" id="sop-rule"></div>
    <div class="pagination" id="sop-pagination"></div>`;
  loadSopCurrencies();
  loadSopCustomers();
  loadSalesOrderShipmentFinance(1);
}

/* 币种下拉：与单据币种同一枚举口径（枚举名），不同币种绝不合并汇总 */
function loadSopCurrencies() {
  const sel = document.getElementById('sop-currency');
  if (!sel) return;
  (typeof CURRENCY_NAME_OPTS !== 'undefined' ? CURRENCY_NAME_OPTS : []).forEach(o => {
    const opt = document.createElement('option');
    opt.value = o.value;
    opt.textContent = o.label;
    sel.appendChild(opt);
  });
}

/* 客户下拉：既有基础资料接口；失败不阻断报表（仍可留空或只用其它筛选） */
async function loadSopCustomers() {
  try {
    const data = await api('/api/base/customers?page=1&pageSize=200');
    const sel = document.getElementById('sop-customer');
    if (!sel) return;
    (data.items || []).forEach(c => {
      const opt = document.createElement('option');
      opt.value = c.id;
      opt.textContent = c.customerName || ('客户 ' + c.id);
      sel.appendChild(opt);
    });
  } catch (e) { /* 忽略：客户下拉失败不影响报表查询 */ }
}

function sopVal(id) {
  const el = document.getElementById(id);
  return el ? String(el.value || '').trim() : '';
}

function sopQuery(page) {
  const q = new URLSearchParams();
  if (sopVal('sop-customer')) q.set('customerId', sopVal('sop-customer'));
  if (sopVal('sop-currency')) q.set('currency', sopVal('sop-currency'));
  if (sopVal('sop-date-from')) q.set('orderDateFrom', sopVal('sop-date-from'));
  if (sopVal('sop-date-to')) q.set('orderDateTo', sopVal('sop-date-to'));
  if (sopVal('sop-shipment-status')) q.set('shipmentStatus', sopVal('sop-shipment-status'));
  if (sopVal('sop-finance-status')) q.set('financeLinkStatus', sopVal('sop-finance-status'));
  if (sopVal('sop-keyword')) q.set('keyword', sopVal('sop-keyword'));
  q.set('page', page || 1);
  q.set('pageSize', sopVal('sop-pagesize') || '50');
  return q.toString();
}

async function loadSalesOrderShipmentFinance(page) {
  const el = document.getElementById('sop-order-table');
  if (!el) return;
  el.innerHTML = '<div class="skeleton-row"></div><div class="skeleton-row"></div>';
  try {
    const data = await api('/api/sales-orders/shipment-finance-report?' + sopQuery(page));
    sopRenderKpi(data);
    sopRenderCurrencyTable(data);
    sopRenderGroupTable(data);
    sopRenderOrderTable(data);
    sopRenderPagination(data);
    const rule = document.getElementById('sop-rule');
    if (rule) {
      rule.textContent = '口径：' + (data.rule || '') + ' ' + (data.scopeNote || '') + ' ' +
        (data.receivableDisclaimer || '');
    }
  } catch (e) {
    el.innerHTML = `<div class="empty"><div style="font-size:48px">⚠️</div><div>报表加载失败：${escapeHtml(e.message)}</div></div>`;
  }
}

/* 未知（null）= 无权威引用 / 命中等派生命中上限：显示「未知」而不是 0 */
function sopMoney(v) { return v === null || v === undefined ? '未知' : fmtMoney(v); }
function sopQty(v) { return v === null || v === undefined ? '未知' : fmtMoney(v); }

function sopShipmentHtml(status) {
  const cls = status === 'complete' ? 'status-success'
    : (status === 'over_shipped' ? 'status-danger'
      : (status === 'unknown' ? 'status-warning' : 'status-neutral'));
  return `<span class="status ${cls}">${escapeHtml(SOP_SHIPMENT_LABELS[status] || status || '')}</span>`;
}

function sopFinanceHtml(status) {
  const cls = status === 'linked' ? 'status-success'
    : (status === 'partial' ? 'status-warning' : 'status-neutral');
  return `<span class="status ${cls}">${escapeHtml(SOP_FINANCE_LABELS[status] || status || '')}</span>`;
}

function sopRenderKpi(data) {
  const el = document.getElementById('sop-kpi');
  if (!el) return;
  el.innerHTML = `
    <div class="kpi-card cargo">
      <div class="kpi-label">符合筛选的销售订单</div>
      <div class="kpi-value">${data.total}<span class="unit">张</span></div>
      <div class="kpi-delta flat">本页 ${data.pageOrderCount} 张 · 第 ${data.page}/${data.totalPages} 页 · 每页 ${data.pageSize}</div>
    </div>
    <div class="kpi-card gold">
      <div class="kpi-label">本页收款链接（完整 / 部分 / 未链接）</div>
      <div class="kpi-value">${data.linkedOrderCount} / ${data.partialOrderCount} / ${data.unlinkedOrderCount}<span class="unit">张</span></div>
      <div class="kpi-delta flat">未链接与部分可归属的金额显示「未知」，不得当作应收余额</div>
    </div>
    <div class="kpi-card ocean">
      <div class="kpi-label">本页出货（已出 / 未出）</div>
      <div class="kpi-value">${data.shippedOrderCount} / ${data.unshippedOrderCount}<span class="unit">张</span></div>
      <div class="kpi-delta flat">币种 ${(data.currencies || []).length} 种（不跨币种汇总）· 数量未知 ${data.unknownShipmentOrderCount} 张</div>
    </div>`;
}

/* 本页按币种汇总：同一币种内汇总，不同币种分别成行（不做汇率换算） */
function sopRenderCurrencyTable(data) {
  const el = document.getElementById('sop-currency-table');
  if (!el) return;
  const rows = (data.currencies || []).map(c => `<tr>
      <td><b>${escapeHtml(c.currency)}</b></td>
      <td class="text-right">${c.customerCount}</td>
      <td class="text-right">${c.orderCount}</td>
      <td class="text-right">${fmtMoney(c.orderAmount)}</td>
      <td>${c.linkedOrderCount} / ${c.partialOrderCount} / ${c.unlinkedOrderCount}</td>
      <td class="text-right">${sopMoney(c.linkedAmount)}</td>
      <td class="text-right">${sopMoney(c.uncoveredAmount)}</td>
      <td class="text-right">${sopQty(c.shippedQuantity)} / ${sopQty(c.orderedQuantity)}</td>
      <td class="text-right">${sopQty(c.outstandingQuantity)}</td>
    </tr>`).join('');
  el.innerHTML = `<table><thead><tr>
      <th>币种</th><th class="text-right">客户数</th><th class="text-right">订单数</th>
      <th class="text-right">订单金额</th><th>引用完整 / 部分 / 未链接（张）</th>
      <th class="text-right">已关联（仅金额已知）</th><th class="text-right">未覆盖（不是应收）</th>
      <th class="text-right">已出货 / 已订数量</th><th class="text-right">未出货数量</th>
    </tr></thead>
    <tbody>${rows || '<tr><td colspan="9" class="empty">本页没有订单：没有可汇总的币种</td></tr>'}</tbody></table>`;
}

/* 「客户 + 币种」分组：只有同分组才汇总金额；未链接 / 未知单列 */
function sopRenderGroupTable(data) {
  const el = document.getElementById('sop-group-table');
  if (!el) return;
  const rows = (data.groups || []).map(g => `<tr>
      <td>${escapeHtml(g.customerName || ('客户 ' + g.customerId))}</td>
      <td><b>${escapeHtml(g.currency)}</b></td>
      <td class="text-right">${g.orderCount}</td>
      <td class="text-right">${fmtMoney(g.orderAmount)}</td>
      <td>${g.linkedOrderCount} / ${g.partialOrderCount} / ${g.unlinkedOrderCount}</td>
      <td class="text-right">${sopMoney(g.linkedAmount)}</td>
      <td class="text-right">${sopMoney(g.uncoveredAmount)}</td>
      <td class="text-right">${sopQty(g.shippedQuantity)} / ${sopQty(g.orderedQuantity)}</td>
      <td class="text-right">${sopQty(g.outstandingQuantity)}</td>
      <td class="text-right">${g.overReceivedOrderCount}</td>
    </tr>`).join('');
  el.innerHTML = `<table><thead><tr>
      <th>客户</th><th>币种</th><th class="text-right">订单数</th><th class="text-right">订单金额</th>
      <th>引用完整 / 部分 / 未链接（张）</th>
      <th class="text-right">已关联（仅金额已知）</th><th class="text-right">未覆盖（不是应收）</th>
      <th class="text-right">已出货 / 已订数量</th><th class="text-right">未出货数量</th><th class="text-right">超收单数</th>
    </tr></thead>
    <tbody>${rows || '<tr><td colspan="10" class="empty">没有符合筛选条件的「客户 + 币种」分组</td></tr>'}</tbody></table>`;
}

/* 本页订单明细（出货数量 / 收款金额一律复用后端权威口径；未知显示「未知」） */
function sopRenderOrderTable(data) {
  const el = document.getElementById('sop-order-table');
  if (!el) return;
  const orders = [];
  (data.groups || []).forEach(g => (g.orders || []).forEach(o => orders.push(o)));
  const rows = orders.map(o => `<tr>
      <td>${escapeHtml(o.orderNo)}</td>
      <td>${fmtDate(o.orderDate)}</td>
      <td>${statusHtml(o.status)}</td>
      <td>${escapeHtml(o.customerName || ('客户 ' + o.customerId))}</td>
      <td>${escapeHtml(o.currency)}</td>
      <td class="text-right">${fmtMoney(o.orderAmount)}</td>
      <td class="text-right">${sopQty(o.shippedQuantity)} / ${sopQty(o.orderedQuantity)}</td>
      <td class="text-right">${sopQty(o.pendingShipmentQuantity)}</td>
      <td class="text-right">${sopQty(o.outstandingQuantity)}</td>
      <td>${sopShipmentHtml(o.shipmentStatus)}</td>
      <td title="${escapeHtml(o.financeLinkReason || '')}">${sopFinanceHtml(o.financeLinkStatus)}</td>
      <td class="text-right">${sopMoney(o.linkedAmount)}</td>
      <td class="text-right">${sopMoney(o.uncoveredAmount)}</td>
      <td class="text-right">${sopMoney(o.submittedAmount)}</td>
      <td class="text-right">${o.otherCurrencyRecordCount} / ${o.unapprovedRecordCount} / ${o.unattributedRecordCount}</td>
      <td>${escapeHtml(o.note || '')}</td>
    </tr>`).join('');
  el.innerHTML = `<table><thead><tr>
      <th>订单号</th><th>订单日期</th><th>状态</th><th>客户</th><th>币种</th><th class="text-right">订单金额</th>
      <th class="text-right">已出 / 已订</th><th class="text-right">待审出货</th><th class="text-right">未出货</th><th>出货状态</th>
      <th>收款链接</th><th class="text-right">已关联</th><th class="text-right">未覆盖（不是应收）</th><th class="text-right">已提交未审核</th>
      <th>他币种 / 非已审核 / 客户级记录数</th><th>说明</th>
    </tr></thead>
    <tbody>${rows || '<tr><td colspan="16" class="empty">没有符合筛选条件的销售订单（可放宽客户 / 币种 / 日期 / 状态筛选）</td></tr>'}</tbody></table>`;
}

function sopRenderPagination(data) {
  const el = document.getElementById('sop-pagination');
  if (!el) return;
  const page = data.page || 1;
  const totalPages = data.totalPages || 1;
  el.innerHTML = `
    <button class="btn btn-neutral btn-sm" ${page <= 1 ? 'disabled' : ''} onclick="loadSalesOrderShipmentFinance(${page - 1})">上一页</button>
    <span style="margin:0 10px">第 ${page} / ${totalPages} 页（共 ${data.total} 张订单）</span>
    <button class="btn btn-neutral btn-sm" ${page >= totalPages ? 'disabled' : ''} onclick="loadSalesOrderShipmentFinance(${page + 1})">下一页</button>`;
}

/* 导出本页订单明细为 CSV（与表格同一套口径：未知值按表格文本原样导出，不回落为 0） */
function exportSopCsv() {
  const table = document.querySelector('#sop-order-table table');
  if (!table) { toast('暂无可导出数据', 'warning'); return; }
  const rows = Array.from(table.querySelectorAll('tr'));
  const csv = rows.map(tr => Array.from(tr.querySelectorAll('th,td'))
    .map(td => `"${td.textContent.replace(/"/g, '""')}"`).join(',')).join('\n');
  const blob = new Blob(['\uFEFF' + csv], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = '销售订单出货财务进度报表.csv';
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
  toast('CSV 已导出', 'success');
}

/* 行操作：查看单张销售订单的出货与收款进度（GET /api/sales-orders/{id}/progress，不落库） */
async function showSalesOrderProgress(id) {
  try {
    const p = await api(`/api/sales-orders/${id}/progress`);
    const s = p.shipment || {};
    const f = p.finance || {};
    const truncated = s.truncated === true;
    const qty = v => truncated ? '未知' : sopQty(v);

    const lineRows = (p.lines || []).map(l => `<tr>
      <td>${escapeHtml(l.productName || '')}</td>
      <td>${escapeHtml(l.spec || '')}</td>
      <td>${escapeHtml(l.unit || '')}</td>
      <td>${fmtMoney(l.orderedQuantity)}</td>
      <td>${fmtMoney(l.shippedQuantity)}</td>
      <td>${fmtMoney(l.pendingQuantity)}</td>
      <td>${fmtMoney(l.outstandingQuantity)}</td>
      <td>${fmtMoney(l.overShippedQuantity)}</td>
      <td>${escapeHtml(sopShipmentLabel(l.shipmentStatus))}</td>
    </tr>`).join('');

    const unmatchedRows = (p.unmatchedShipments || []).map(u => `<tr>
      <td>${escapeHtml(u.productName || '')}</td>
      <td>${u.productId}</td>
      <td>${fmtMoney(u.shippedQuantity)}</td>
    </tr>`).join('');

    const shipmentRows = (p.shipments || []).map(r => `<tr>
      <td>${escapeHtml(r.stockOutNo || '')}</td>
      <td>${escapeHtml(r.stockOutDate ? fmtDate(r.stockOutDate) : '')}</td>
      <td>${statusHtml(r.status)}</td>
      <td>${fmtMoney(r.totalQuantity)}</td>
      <td>${r.counted ? '计入已出货' : '不计入'}</td>
    </tr>`).join('');

    const financeRows = (f.records || []).map(r => `<tr>
      <td>${escapeHtml(r.documentNo || '')}</td>
      <td>${escapeHtml(r.documentDate ? fmtDate(r.documentDate) : '')}</td>
      <td>${fmtMoney(r.amount)}</td>
      <td>${escapeHtml(r.currency || '（无币种列）')}</td>
      <td>${escapeHtml(r.status || '')}</td>
      <td>${escapeHtml(r.referenceField || '')}</td>
      <td>${r.counted ? '计入已关联' : '不计入'}</td>
      <td>${escapeHtml(r.note || '')}</td>
    </tr>`).join('');

    const modal = document.getElementById('modal');
    modal.innerHTML = `<div class="modal modal-lg" style="width:1180px;max-width:96vw;max-height:92vh;overflow:auto">
      <h3>🚚 销售订单出货与收款进度：${escapeHtml(p.orderNo || '')}</h3>
      <div class="form-grid" style="grid-template-columns:repeat(4,1fr)">
        <div><b>订单金额</b>：${fmtMoney(p.orderAmount)} ${escapeHtml(p.currency || '')}</div>
        <div><b>已落库定金</b>：${fmtMoney(p.recordedDepositAmount)}（订单字段，非派生）</div>
        <div><b>订单状态</b>：${statusHtml(p.status)}</div>
        <div><b>合同号 / 客户 PO</b>：${escapeHtml(p.contractNo || '（无）')} / ${escapeHtml(p.customerPoNo || '（无）')}</div>
      </div>

      <h4>出货进度（数量口径：已审核销售出库单）</h4>
      <div class="form-grid" style="grid-template-columns:repeat(4,1fr)">
        <div><b>已订数量</b>：${qty(s.orderedQuantity)}</div>
        <div><b>已出货数量</b>：${qty(s.shippedQuantity)}（已审核出库）</div>
        <div><b>未出货数量</b>：${qty(s.outstandingQuantity)}</div>
        <div><b>待审数量</b>：${qty(s.pendingQuantity)}（未计入已出货）</div>
        <div><b>订单外已出货</b>：${qty(s.unmatchedShippedQuantity)}</div>
        <div><b>出货状态</b>：${escapeHtml(sopShipmentLabel(s.shipmentStatus))}</div>
        <div><b>出库单数</b>：${s.approvedShipmentCount || 0} / ${s.shipmentDocumentCount || 0}（已审核 / 全部）</div>
        <div><b>是否已有已审核出库</b>：${s.hasApprovedShipment ? '是' : '否'}</div>
      </div>

      ${truncated
        ? '<div class="pd-hint">⚠️ 以本单为来源的出库单据超过单次派生上限：数量不完整，已按「未知」呈现（不静默截断），请收窄范围后核对。</div>'
        : `<div class="table-wrap" style="max-height:32vh;overflow:auto">
        <table><thead><tr><th>商品</th><th>规格</th><th>单位</th><th>订单数量</th><th>已出</th><th>待审</th><th>未出</th><th>超发</th><th>行状态</th></tr></thead>
        <tbody>${lineRows || '<tr><td colspan="9" class="empty">无订单明细</td></tr>'}</tbody></table>
      </div>`}

      ${unmatchedRows ? `<h4>订单外商品已出货（显式单列，不并入订单行）</h4>
      <div class="table-wrap" style="max-height:20vh;overflow:auto">
        <table><thead><tr><th>商品</th><th>商品Id</th><th>已出货数量</th></tr></thead><tbody>${unmatchedRows}</tbody></table>
      </div>` : ''}

      <h4>出货来源单据（以本单为来源的销售出库单）</h4>
      <div class="table-wrap" style="max-height:24vh;overflow:auto">
        <table><thead><tr><th>出库单号</th><th>日期</th><th>状态</th><th>单据数量</th><th>是否计入</th></tr></thead>
        <tbody>${shipmentRows || '<tr><td colspan="5" class="empty">暂无以本单为来源的销售出库单</td></tr>'}</tbody></table>
      </div>

      <h4>收款链接（复用财务核对 ERP-028 的既有引用字段）</h4>
      <div class="form-grid" style="grid-template-columns:repeat(4,1fr)">
        <div><b>引用状态</b>：${escapeHtml(sopFinanceLabel(f.linkStatus))}</div>
        <div><b>已关联金额</b>：${sopMoney(f.linkedAmount)}${f.overReceived ? '（超收，请核对）' : ''}</div>
        <div><b>未覆盖金额</b>：${sopMoney(f.uncoveredAmount)}（不是应收余额）</div>
        <div><b>已提交未审核</b>：${sopMoney(f.submittedAmount)}</div>
        <div><b>他币种记录</b>：${f.otherCurrencyRecordCount || 0} 条（不汇总、不换算）</div>
        <div><b>非已审核记录</b>：${f.unapprovedRecordCount || 0} 条（仅列出）</div>
        <div><b>客户级不可归属记录</b>：${f.unattributedRecordCount || 0} 条${f.unattributedRecordsTruncated ? '（超过查询上限，计数不完整）' : ''}</div>
        <div><b>是否超收</b>：${f.overReceived ? '是' : '否'}</div>
      </div>
      <div class="pd-hint">${escapeHtml(f.linkReason || '')}</div>
      <div class="table-wrap" style="max-height:26vh;overflow:auto">
        <table><thead><tr><th>单号</th><th>日期</th><th>金额</th><th>币种</th><th>状态</th><th>引用依据</th><th>是否计入</th><th>说明</th></tr></thead>
        <tbody>${financeRows || '<tr><td colspan="8" class="empty">无相关收款记录</td></tr>'}</tbody></table>
      </div>

      <div class="pd-hint">出货口径：${escapeHtml(p.shipmentRule || '')}</div>
      <div class="pd-hint">收款口径：${escapeHtml(p.financeRule || '')}</div>
      <div class="modal-footer"><button class="btn btn-neutral" onclick="closeModal()">关闭</button></div>
    </div>`;
    modal.style.display = 'flex';
    return p;
  } catch (err) { toast(err.message, 'error'); }
}

/* 出货状态中文（与后端 SalesOrderProgress 常量一一对应） */
function sopShipmentLabel(status) {
  return SOP_SHIPMENT_LABELS[status] || status || '';
}

/* 收款引用状态中文（linked / partial / unlinked / unknown） */
function sopFinanceLabel(status) {
  return SOP_FINANCE_LABELS[status] || status || '';
}
