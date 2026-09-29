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
        <button class="btn btn-neutral" onclick="openSalesOrderShipmentFinanceFieldDesigner()" title="按 ERP-156 白名单字段选择列并预览当前账号数据范围（只读）">🧩 字段设计器</button>
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

/* ============ 销售订单出货 / 财务进度字段设计器（ERP-157：只读、有界的可视化字段设计器） ============
   口径与后端 ERP-156（DynamicShipmentFinanceReportController / DynamicShipmentFinanceReportRules）一一对应：
   - 字段选择器只由 GET /api/sales-orders/dynamic-shipment-finance-report 返回的有限白名单目录（27 个字段）渲染，绝无自由填写的字段名或 SQL；
   - 筛选只允许客户 / 币种 / 订单日期（起止）/ 出货状态（none / shipped）/ 收款链接状态（linked / partial / unlinked），与 ERP-032 同口径；
   - 预览走 POST /api/sales-orders/dynamic-shipment-finance-report，只发送「白名单字段 + 有界筛选 + 有界分页」，按请求顺序渲染返回的列名与单元格；
   - 未知金额 / 未知数量（null）在界面与 CSV 中显式显示「未知」，绝不回落为 0；金额按原币成行、绝不跨币种合并或换算；
   - 全程只读：不写库、不迁移、不执行任意 SQL；授权 / 无效请求 / 空结果 / 网络失败都在界面可见，且不暴露范围外数据。 */

/* ERP-156 预览接口与额度口径（有界，与后端 DynamicShipmentFinanceReportRules 同源） */
const DSF_API = '/api/sales-orders/dynamic-shipment-finance-report';
const DSF_DEFAULT_PAGE_SIZE = 50;
const DSF_MAX_PAGE_SIZE_FALLBACK = 200;

/* 出货状态筛选取值（与 ERP-032 / ERP-156 口径一致：none / shipped） */
const DSF_SHIPMENT_FILTER_OPTS = [
  { value: '', label: '全部' },
  { value: 'none', label: '未出货（无已审核出库单）' },
  { value: 'shipped', label: '已有已审核出库单' },
];

/* 收款链接状态筛选取值（linked / partial / unlinked；unknown 无法用既有列条件表达，不提供筛选） */
const DSF_FINANCE_FILTER_OPTS = [
  { value: '', label: '全部' },
  { value: 'linked', label: '收款引用完整' },
  { value: 'partial', label: '部分可归属（其余未知）' },
  { value: 'unlinked', label: '未链接（金额未知）' },
];

/* 订单状态枚举名 → 中文文案（与 DocumentStatus 枚举名一致） */
const DSF_STATUS_LABELS = {
  Pending: '待提交', Submitted: '已提交', Approved: '已审核',
  Rejected: '已驳回', Completed: '已完成', Cancelled: '已取消',
};

/* 设计器状态（纯数据；DOM 访问只在事件处理函数内部发生） */
let DSF = {
  catalog: null,      // GET 目录 DTO
  fields: [],         // 目录字段（白名单）
  selectedKeys: [],   // 当前勾选的字段键（默认全选）
  customers: [],      // 客户下拉来源（/api/base/customers）
  filters: { customerId: '', currency: '', dateFrom: '', dateTo: '', shipmentStatus: '', financeLinkStatus: '', page: 1, pageSize: DSF_DEFAULT_PAGE_SIZE },
  view: null,         // 最近一次预览结果
};

/* ==================== 纯函数（可在 Node 中逐条单测） ==================== */

/* HTML 转义（本地独立实现，避免依赖全局 escapeHtml 的加载顺序） */
function dsfEsc(v) {
  return String(v ?? '').replace(/[&<>"']/g, c => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}

/* 出货 / 收款链接 / 订单状态中文（未知取值原样返回，不猜测） */
function dsfShipmentStatusLabel(v) { return SOP_SHIPMENT_LABELS[String(v)] || String(v) || ''; }
function dsfFinanceStatusLabel(v) { return SOP_FINANCE_LABELS[String(v)] || String(v) || ''; }
function dsfOrderStatusLabel(v) { return DSF_STATUS_LABELS[String(v)] || String(v) || ''; }

/* 规范化选定字段（fail closed）：只保留目录白名单内的键、去重、保持请求顺序；未知键丢弃 */
function dsfSelectFields(catalogFields, selectedKeys) {
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

/* 组装有界预览请求体：字段只来自目录、分页有界、筛选仅客户 / 币种 / 订单日期 / 出货状态 / 收款链接状态，绝不接受任意字段名或 SQL */
function dsfBuildRequest(state) {
  const fields = dsfSelectFields(state.catalogFields, state.selectedKeys);
  const page = Math.max(1, Math.floor(Number(state.page) || 1));
  const maxPageSize = Number(state.maxPageSize) || DSF_MAX_PAGE_SIZE_FALLBACK;
  let pageSize = Math.floor(Number(state.pageSize));
  if (!Number.isFinite(pageSize)) pageSize = DSF_DEFAULT_PAGE_SIZE;
  pageSize = Math.max(1, Math.min(maxPageSize, pageSize));

  const req = { fields, page, pageSize };

  const customerId = Number(state.customerId);
  if (Number.isFinite(customerId) && customerId > 0) req.customerId = customerId;

  const currency = state.currency ? String(state.currency).trim() : '';
  if (currency) req.currency = currency;

  const dateFrom = state.dateFrom ? String(state.dateFrom).slice(0, 10) : '';
  const dateTo = state.dateTo ? String(state.dateTo).slice(0, 10) : '';
  if (dateFrom) req.orderDateFrom = dateFrom;
  if (dateTo) req.orderDateTo = dateTo;

  const shipmentStatus = state.shipmentStatus ? String(state.shipmentStatus).trim() : '';
  if (shipmentStatus && DSF_SHIPMENT_FILTER_OPTS.some(o => o.value === shipmentStatus)) req.shipmentStatus = shipmentStatus;

  const financeLinkStatus = state.financeLinkStatus ? String(state.financeLinkStatus).trim() : '';
  if (financeLinkStatus && DSF_FINANCE_FILTER_OPTS.some(o => o.value === financeLinkStatus)) req.financeLinkStatus = financeLinkStatus;

  return req;
}

/* 单元格纯文本（安全：null/undefined 显示「未知」、布尔显示 是/否、日期截断到日、状态映射中文；绝不回落为 0） */
function dsfCellText(value, field) {
  const key = (field && field.key) || '';
  const dataType = (field && field.dataType) || 'text';
  if (value === null || value === undefined) return '未知';
  if (key === 'shipmentStatus') return dsfShipmentStatusLabel(value);
  if (key === 'financeLinkStatus') return dsfFinanceStatusLabel(value);
  if (key === 'status') return dsfOrderStatusLabel(value);
  if (dataType === 'boolean') return (value === true || value === 'true' || value === 1 || value === '1') ? '是' : '否';
  if (dataType === 'date') return String(value).slice(0, 10);
  return String(value);
}

/* 单元格 HTML（转义后安全渲染） */
function dsfRenderCell(value, field) {
  return dsfEsc(dsfCellText(value, field));
}

/* CSV 单元格安全封装：公式前导（= + - @ 或含制表 / 换行）加单引号防注入；内部引号翻倍；未知保留「未知」 */
function dsfCsvCell(text) {
  const s = (text === null || text === undefined) ? '' : String(text);
  let v = s;
  if (/^[=+\-@]/.test(v) || /[\t\r\n]/.test(v)) v = "'" + v;
  return '"' + v.replace(/"/g, '""') + '"';
}

/* 当前页选定列 CSV：表头为列名、单元格为选定字段纯文本（未知保留「未知」、公式前导转义） */
function dsfCsv(view) {
  const cols = (view && view.columns) || [];
  const rows = (view && view.rows) || [];
  const lines = [cols.map(c => dsfCsvCell(c.label || c.key)).join(',')];
  rows.forEach(r => lines.push(cols.map(c => dsfCsvCell(dsfCellText(r[c.key], c))).join(',')));
  return lines.join('\r\n');
}

/* ==================== 结果渲染（只读、转义、失败态分类） ==================== */

/* 结果表格 HTML：表头为返回的列名、单元格为返回的选定字段值，全部经转义 */
function dsfTableHtml(view) {
  const cols = (view && view.columns) || [];
  const rows = (view && view.rows) || [];
  if (!cols.length) return '';
  const align = c => (c.dataType === 'number' || c.dataType === 'boolean') ? ' class="text-right"' : '';
  const head = cols.map(c => `<th${align(c)}>${dsfEsc(c.label || c.key)}</th>`).join('');
  const body = rows.map(r =>
    `<tr>${cols.map(c => `<td${align(c)}>${dsfRenderCell(r[c.key], c)}</td>`).join('')}</tr>`).join('');
  const emptyRow = `<tr><td colspan="${cols.length}" class="empty">没有符合条件的销售订单</td></tr>`;
  const prevDisabled = !view || view.page <= 1 ? ' disabled' : '';
  const nextDisabled = !view || view.page >= view.totalPages ? ' disabled' : '';
  const paging = `<div style="display:flex;justify-content:space-between;align-items:center;margin-top:8px">
      <span class="text-muted">第 ${view ? view.page : 1} 页 / 共 ${view ? view.totalPages : 0} 页</span>
      <div>
        <button class="btn btn-neutral btn-sm" onclick="dsfPage(-1)"${prevDisabled}>← 上一页</button>
        <button class="btn btn-neutral btn-sm" onclick="dsfPage(1)"${nextDisabled}>下一页 →</button>
      </div></div>`;
  return `<div class="table-wrap" style="margin-top:8px"><table><thead><tr>${head}</tr></thead><tbody>${body || emptyRow}</tbody></table></div>${paging}`;
}

/* 空结果提示 */
function dsfEmptyHtml() {
  return '<div class="empty" style="margin:8px 0">没有符合条件的销售订单（当前账号数据范围内的只读快照，未知金额 / 数量显示「未知」）。</div>';
}

/* 错误提示（授权 / 未登录 / 无效请求 / 网络失败分别可见，且不暴露任何数据） */
function dsfErrorHtml(kind, message) {
  const labels = {
    forbidden: '权限不足',
    unauthorized: '未登录 / 登录已过期',
    invalid: '请求无效',
    network: '网络请求失败',
    error: '预览失败',
  };
  return `<div class="pd-hint" style="color:#b91c1c;background:#fef2f2;border-color:#fecaca">
      <b>${dsfEsc(labels[kind] || '预览失败')}</b>：${dsfEsc(message || '')}</div>`;
}

/* 预览结果（口径 / 边界 / 免责文案 + 汇总 + 空结果 + 表格） */
function dsfResultHtml(view) {
  const readOnly = view && view.readOnlyText ? `<div class="pd-hint">${dsfEsc(view.readOnlyText)}</div>` : '';
  const boundary = view && view.boundaryText ? `<div class="pd-hint">${dsfEsc(view.boundaryText)}</div>` : '';
  const disclaimer = view && view.disclaimerText ? `<div class="pd-hint" style="color:#64748b">${dsfEsc(view.disclaimerText)}</div>` : '';
  const summary = view
    ? `<div class="text-muted" style="margin:6px 0">共 ${view.total} 条 · 第 ${view.page} 页 · 每页 ${view.pageSize} 条</div>`
    : '';
  const empty = view && (!view.rows || view.rows.length === 0) ? dsfEmptyHtml() : '';
  return `${readOnly}${boundary}${disclaimer}${summary}${empty}${dsfTableHtml(view)}`;
}

/* 字段选择器：仅由目录白名单渲染为复选框，无自由填写的字段名 */
function dsfFieldChooserHtml(fields, selectedKeys) {
  const selected = new Set(selectedKeys || []);
  return (fields || []).map(f => {
    const checked = selected.has(f.key) ? 'checked' : '';
    return `<label style="display:inline-flex;align-items:center;gap:4px;margin:3px 6px 3px 0;padding:2px 8px;border:1px solid #e2e8f0;border-radius:6px;background:#f8fafc;cursor:pointer">
        <input type="checkbox" name="dsf-field" value="${dsfEsc(f.key)}" ${checked} onchange="dsfSyncSelection()">
        <span>${dsfEsc(f.label || f.key)}</span></label>`;
  }).join('');
}

/* ==================== 状态 / 请求 / 渲染 ==================== */

/* 轻量请求封装：返回完整 ApiResponse 信封（保留 code），网络异常抛给调用方 */
async function dsfRequest(path, method = 'GET', body = null) {
  const headers = { 'Content-Type': 'application/json' };
  const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
  if (token) headers['Authorization'] = 'Bearer ' + token;
  const opts = { method, headers };
  if (body) opts.body = JSON.stringify(body);
  const resp = await fetch(path, opts);
  return await resp.json();
}

/* 业务码 → 错误态分类（与 ErrorCodes 同源：2002 权限不足、2000/2003 未登录 / 令牌过期） */
function dsfKindOfCode(code) {
  if (code === 2002) return 'forbidden';
  if (code === 2000 || code === 2003) return 'unauthorized';
  return 'invalid';
}

function dsfLoadingHtml() {
  return '<div class="pd-hint" style="text-align:center;color:#64748b">正在预览（只读查询）…</div>';
}

function dsfRenderResult(html) {
  const el = document.getElementById('dsf-result');
  if (el) el.innerHTML = html;
}

/* 同步勾选状态到 selectedKeys（复选框 onchange） */
function dsfSyncSelection() {
  const boxes = document.querySelectorAll('input[name="dsf-field"]');
  DSF.selectedKeys = Array.from(boxes).filter(b => b.checked).map(b => b.value);
}

function dsfToggleAll(checked) {
  const boxes = document.querySelectorAll('input[name="dsf-field"]');
  DSF.selectedKeys = [];
  boxes.forEach(b => { b.checked = checked; if (checked) DSF.selectedKeys.push(b.value); });
}

/* 目录加载失败 / 授权失败时的整页错误态（带关闭） */
function dsfErrorModalHtml(kind, message) {
  return `<div class="modal modal-lg">
    <h3>🧩 销售订单出货 / 财务进度字段设计器</h3>
    ${dsfErrorHtml(kind, message)}
    <div class="modal-footer"><button class="btn btn-neutral" onclick="closeModal()">关闭</button></div>
  </div>`;
}

/* 读取当前字段 / 筛选 / 分页状态（预览与导出复用，单一来源） */
function dsfBuildState(page) {
  return {
    catalogFields: DSF.fields,
    selectedKeys: DSF.selectedKeys,
    customerId: document.getElementById('dsf-customer').value,
    currency: document.getElementById('dsf-currency').value,
    dateFrom: document.getElementById('dsf-date-from').value,
    dateTo: document.getElementById('dsf-date-to').value,
    shipmentStatus: document.getElementById('dsf-shipment-status').value,
    financeLinkStatus: document.getElementById('dsf-finance-status').value,
    pageSize: document.getElementById('dsf-pagesize').value,
    page: page || 1,
    maxPageSize: DSF.catalog && DSF.catalog.maxPageSize ? DSF.catalog.maxPageSize : DSF_MAX_PAGE_SIZE_FALLBACK,
  };
}

/* 预览：组装有界请求 → POST → 安全渲染列名与单元格；授权 / 无效 / 空 / 网络失败均可见 */
async function dsfPreview(page) {
  const state = dsfBuildState(page);
  if (state.dateFrom && state.dateTo && state.dateFrom > state.dateTo) {
    dsfRenderResult(dsfErrorHtml('invalid', '订单日期开始不能晚于结束'));
    return;
  }

  const req = dsfBuildRequest(state);
  DSF.filters.page = req.page;
  DSF.filters.pageSize = req.pageSize;

  dsfRenderResult(dsfLoadingHtml());

  try {
    const resp = await dsfRequest(DSF_API, 'POST', req);
    if (resp.code === 0) {
      DSF.view = resp.data;
      DSF.filters.page = resp.data.page;
      dsfRenderResult(dsfResultHtml(resp.data));
    } else if (resp.code === 2000 || resp.code === 2003) {
      if (typeof logout === 'function') logout();
      dsfRenderResult(dsfErrorHtml('unauthorized', resp.message));
    } else {
      dsfRenderResult(dsfErrorHtml(dsfKindOfCode(resp.code), resp.message));
    }
  } catch (err) {
    dsfRenderResult(dsfErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 翻页（有界：最小第 1 页） */
function dsfPage(delta) {
  const view = DSF.view;
  const page = (view ? view.page : DSF.filters.page) + delta;
  if (page < 1) return;
  dsfPreview(page);
}

/* 导出当前页选定列 CSV（只读）：未知保留「未知」、公式前导转义；无数据不导出 */
function dsfExportCsv() {
  const view = DSF.view;
  if (!view || !view.columns || !view.columns.length) { toast('暂无可导出数据', 'warning'); return; }
  const csv = dsfCsv(view);
  const blob = new Blob(['\uFEFF' + csv], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = '销售订单出货财务进度动态报表.csv';
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
  toast('CSV 已导出', 'success');
}

/* 渲染设计器（字段选择器 + 有界筛选器 + 预览按钮 + 结果区） */
function dsfRender() {
  const f = DSF.filters;
  const maxPage = DSF.catalog && DSF.catalog.maxPageSize ? DSF.catalog.maxPageSize : DSF_MAX_PAGE_SIZE_FALLBACK;
  const customerOptions = DSF.customers.map(c =>
    `<option value="${dsfEsc(c.id)}" ${String(c.id) === String(f.customerId) ? 'selected' : ''}>${dsfEsc(c.customerName || ('客户 ' + c.id))}</option>`).join('');
  const currencyOptions = (typeof CURRENCY_NAME_OPTS !== 'undefined' ? CURRENCY_NAME_OPTS : []).map(o =>
    `<option value="${dsfEsc(o.value)}" ${f.currency === o.value ? 'selected' : ''}>${dsfEsc(o.label)}</option>`).join('');
  const shipmentOptions = DSF_SHIPMENT_FILTER_OPTS.map(o =>
    `<option value="${o.value}" ${f.shipmentStatus === o.value ? 'selected' : ''}>${o.label}</option>`).join('');
  const financeOptions = DSF_FINANCE_FILTER_OPTS.map(o =>
    `<option value="${o.value}" ${f.financeLinkStatus === o.value ? 'selected' : ''}>${o.label}</option>`).join('');

  document.getElementById('modal').innerHTML = `
  <div class="modal modal-lg" style="max-width:1100px">
    <h3>🧩 销售订单出货 / 财务进度字段设计器（只读预览）</h3>
    <div class="pd-hint">只读：仅按 ERP-156 白名单字段与有界筛选（客户 / 币种 / 订单日期 / 出货状态 / 收款链接状态）预览当前账号数据范围内的销售订单出货与收款链接证据；未知金额与未知数量显示「未知」，绝不推算或回落为 0。</div>

    <div style="margin:10px 0">
      <div style="font-weight:600;margin-bottom:6px">① 选择字段（仅 ERP-156 白名单目录，无自由字段名）</div>
      <div style="margin-bottom:6px">
        <button class="btn btn-neutral btn-sm" onclick="dsfToggleAll(true)">全选</button>
        <button class="btn btn-neutral btn-sm" onclick="dsfToggleAll(false)">清空</button>
      </div>
      <div style="max-height:180px;overflow:auto;border:1px solid #e2e8f0;border-radius:8px;padding:8px">${dsfFieldChooserHtml(DSF.fields, DSF.selectedKeys)}</div>
    </div>

    <div style="margin:10px 0">
      <div style="font-weight:600;margin-bottom:6px">② 筛选（客户 / 币种 / 订单日期 / 出货状态 / 收款链接状态）</div>
      <div class="form-grid" style="grid-template-columns:repeat(3,1fr);gap:10px">
        <label>订单日期从 <input type="date" id="dsf-date-from" value="${dsfEsc(f.dateFrom)}" style="width:100%"></label>
        <label>至 <input type="date" id="dsf-date-to" value="${dsfEsc(f.dateTo)}" style="width:100%"></label>
        <label>客户 <select id="dsf-customer" style="width:100%"><option value="">全部客户</option>${customerOptions}</select></label>
        <label>币种 <select id="dsf-currency" style="width:100%"><option value="">全部币种</option>${currencyOptions}</select></label>
        <label>出货状态 <select id="dsf-shipment-status" style="width:100%">${shipmentOptions}</select></label>
        <label>收款链接 <select id="dsf-finance-status" style="width:100%">${financeOptions}</select></label>
        <label>每页 <input type="number" id="dsf-pagesize" value="${Number(f.pageSize)}" min="1" max="${maxPage}" style="width:80px"></label>
      </div>
    </div>

    <div class="modal-footer">
      <button class="btn btn-primary" onclick="dsfPreview(1)">预览</button>
      <button class="btn btn-neutral" onclick="dsfExportCsv()">📤 导出当前页 CSV</button>
      <button class="btn btn-neutral" onclick="closeModal()">关闭</button>
    </div>
    <div id="dsf-result"></div>
  </div>`;
}

/* 从出货 / 财务进度报表页打开字段设计器（加载目录 + 客户，渲染字段选择器与筛选器） */
async function openSalesOrderShipmentFinanceFieldDesigner() {
  DSF = {
    catalog: null, fields: [], selectedKeys: [], customers: [],
    filters: { customerId: '', currency: '', dateFrom: '', dateTo: '', shipmentStatus: '', financeLinkStatus: '', page: 1, pageSize: DSF_DEFAULT_PAGE_SIZE },
    view: null,
  };
  const modal = document.getElementById('modal');
  modal.innerHTML = '<div class="modal modal-lg" style="max-width:1100px"><div class="pd-hint" style="text-align:center;color:#64748b">正在加载字段目录…</div></div>';
  modal.style.display = 'flex';

  // 1) 目录（需登录 + 销售订单菜单授权；授权失败 fail closed，不返回任何字段）
  try {
    const resp = await dsfRequest(DSF_API);
    if (resp.code === 2000 || resp.code === 2003) {
      if (typeof logout === 'function') logout();
      modal.innerHTML = dsfErrorModalHtml('unauthorized', resp.message);
      return;
    }
    if (resp.code !== 0) {
      modal.innerHTML = dsfErrorModalHtml(dsfKindOfCode(resp.code), resp.message);
      return;
    }
    DSF.catalog = resp.data;
  } catch (err) {
    modal.innerHTML = dsfErrorModalHtml('network', (err && err.message) || '无法连接到服务器');
    return;
  }

  DSF.fields = (DSF.catalog && DSF.catalog.fields) || [];
  DSF.selectedKeys = DSF.fields.map(f => f.key);
  const maxPageSize = DSF.catalog && DSF.catalog.maxPageSize ? DSF.catalog.maxPageSize : DSF_MAX_PAGE_SIZE_FALLBACK;
  DSF.filters.pageSize = Math.min(DSF_DEFAULT_PAGE_SIZE, maxPageSize);

  // 2) 客户下拉（尽力而为：失败仅保留「全部客户」，仍可预览）
  try {
    const cresp = await dsfRequest('/api/base/customers?page=1&pageSize=200');
    if (cresp.code === 0) {
      DSF.customers = (cresp.data && cresp.data.items) || (Array.isArray(cresp.data) ? cresp.data : []) || [];
    }
  } catch (e) {
    DSF.customers = [];
  }

  dsfRender();
}

/* Node 单测导出（浏览器中 module 为 undefined，自动跳过） */
if (typeof module !== 'undefined' && module.exports) {
  module.exports = {
    DSF_API,
    DSF_SHIPMENT_FILTER_OPTS,
    DSF_FINANCE_FILTER_OPTS,
    DSF_STATUS_LABELS,
    dsfEsc,
    dsfSelectFields,
    dsfBuildRequest,
    dsfCellText,
    dsfRenderCell,
    dsfCsvCell,
    dsfCsv,
    dsfTableHtml,
    dsfEmptyHtml,
    dsfErrorHtml,
    dsfResultHtml,
    dsfFieldChooserHtml,
    dsfExportCsv,
  };
}
