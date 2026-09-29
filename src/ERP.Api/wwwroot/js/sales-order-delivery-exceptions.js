/* ============ 销售交期异常工作台（ERP-102：只读派生；按客户 + 显式 as-of 基准日派生逾期 / 即将到期 / 已出齐 / 在途 / 未知） ============
   口径与后端 SalesOrderDeliveryExceptionSemantics 一一对应：
   - 交期状态：fulfilled 已出齐 / overdue 逾期 / due_soon 即将到期 / on_schedule 在途正常 / unknown 未知；
   - 交期判定：明细行交期优先，无明细交期回落订单头交期，两者都缺显示「未知」；
   - 出货证据只取「以本单为来源、未删除、已审核」的销售出库（复用 ERP-032）；部分出货仍视为未出齐；
   - 缺日期或出货证据不完整显示「未知」，绝不回落为 0；
   - 只读派生：不改写订单状态与任何已登记进度，不执行迁移 / 生产 SQL / 部署。 */

const SODE_STATUS_LABELS = {
  fulfilled: '已出齐',
  overdue: '逾期',
  due_soon: '即将到期',
  on_schedule: '在途正常',
  unknown: '未知',
};
const SODE_SHIPMENT_LABELS = {
  none: '未出货', partial: '部分出货', complete: '已出齐', over_shipped: '超发', unknown: '未知',
};

/* 工具栏入口（销售订单页）：渲染独立工作台页（只读，不落库） */
function openSalesOrderDeliveryExceptions() {
  CURRENT_PAGE_CODE = 'sales-order-delivery-exceptions';
  document.getElementById('header-title').textContent = '销售交期异常';
  const today = sodeToday();
  document.getElementById('content').innerHTML = `
    <div class="page-hero report-hero">
      <h2>🚚 销售交期异常</h2>
      <p>按客户 + 显式 as-of 基准日派生交期异常（逾期 / 即将到期 / 已出齐）· 明细交期优先 · 只读派生，未知不推断</p>
    </div>

    <div class="pd-hint" style="margin-bottom:10px">
      ⚠️ 只读视图：只按既有订单交期 / 明细交期与已审核销售出库出货证据派生交期状态，<b>不</b>改写订单状态与任何已登记进度，
      也<b>不</b>执行迁移 / 生产 SQL / 部署。
    </div>

    <div class="toolbar">
      <div class="toolbar-left" style="flex-wrap:wrap;gap:8px;align-items:center">
        <label>客户 <select id="sode-customer" style="min-width:170px"><option value="">全部客户</option></select></label>
        <label>基准日 as-of <input type="date" id="sode-asof" value="${today}" style="width:150px"></label>
        <label>关键字 <input type="text" id="sode-keyword" style="width:190px" placeholder="订单号 / 合同号 / 客户 PO 号"></label>
        <label>每页 <input type="number" id="sode-pagesize" value="50" min="1" max="200" style="width:70px"></label>
      </div>
      <div class="toolbar-actions">
        <button class="btn btn-primary" onclick="loadSalesOrderDeliveryExceptions(1)">查询</button>
        <button class="btn btn-neutral" onclick="exportSodeCsv()" title="导出本页订单明细为 CSV">📤 导出 CSV</button>
      </div>
    </div>

    <div class="kpi-grid" id="sode-kpi"></div>
    <div class="table-wrap" id="sode-table">
      <div class="skeleton-row"></div><div class="skeleton-row"></div>
    </div>
    <div class="pd-hint" id="sode-rule"></div>
    <div class="pagination" id="sode-pagination"></div>`;
  loadSodeCustomers();
  loadSalesOrderDeliveryExceptions(1);
}

function sodeToday() {
  const d = new Date();
  return d.getFullYear() + '-' + String(d.getMonth() + 1).padStart(2, '0') + '-' + String(d.getDate()).padStart(2, '0');
}

/* 客户下拉：既有基础资料接口；失败不阻断工作台（仍可留空或只用其它筛选） */
async function loadSodeCustomers() {
  try {
    const data = await api('/api/base/customers?page=1&pageSize=200');
    const sel = document.getElementById('sode-customer');
    if (!sel) return;
    (data.items || []).forEach(c => {
      const opt = document.createElement('option');
      opt.value = c.id;
      opt.textContent = c.customerName || ('客户 ' + c.id);
      sel.appendChild(opt);
    });
  } catch (e) { /* 忽略：客户下拉失败不影响工作台查询 */ }
}

function sodeVal(id) {
  const el = document.getElementById(id);
  return el ? String(el.value || '').trim() : '';
}

function sodeQuery(page) {
  const q = new URLSearchParams();
  if (sodeVal('sode-customer')) q.set('customerId', sodeVal('sode-customer'));
  if (sodeVal('sode-asof')) q.set('asOfDate', sodeVal('sode-asof'));
  if (sodeVal('sode-keyword')) q.set('keyword', sodeVal('sode-keyword'));
  q.set('page', page || 1);
  q.set('pageSize', sodeVal('sode-pagesize') || '50');
  return q.toString();
}

async function loadSalesOrderDeliveryExceptions(page) {
  const el = document.getElementById('sode-table');
  if (!el) return;
  el.innerHTML = '<div class="skeleton-row"></div><div class="skeleton-row"></div>';
  try {
    const data = await api('/api/sales-orders/delivery-exceptions?' + sodeQuery(page));
    sodeRenderKpi(data);
    sodeRenderTable(data);
    sodeRenderPagination(data);
    const rule = document.getElementById('sode-rule');
    if (rule) rule.textContent = '口径：' + (data.rule || '') + ' ' + (data.scopeNote || '');
  } catch (e) {
    el.innerHTML = `<div class="empty"><div style="font-size:48px">⚠️</div><div>工作台加载失败：${escapeHtml(e.message)}</div></div>`;
  }
}
/* 未知（null）= 缺日期 / 命中等派生命中上限；显示「未知」而不是 0 */
function sodeMoney(v) { return v === null || v === undefined ? '未知' : fmtMoney(v); }
function sodeDate(v) { return v ? fmtDate(v) : '未知'; }

function sodeStatusHtml(status) {
  const cls = status === 'fulfilled' ? 'status-success'
    : (status === 'overdue' ? 'status-danger'
      : (status === 'due_soon' ? 'status-warning'
        : (status === 'unknown' ? 'status-warning' : 'status-neutral')));
  return `<span class="status ${cls}">${escapeHtml(SODE_STATUS_LABELS[status] || status || '')}</span>`;
}

function sodeShipmentHtml(status) {
  const cls = status === 'complete' ? 'status-success'
    : (status === 'over_shipped' ? 'status-danger'
      : (status === 'unknown' ? 'status-warning' : 'status-neutral'));
  return `<span class="status ${cls}">${escapeHtml(SODE_SHIPMENT_LABELS[status] || status || '')}</span>`;
}

/* 明细交期（行优先）：无明细交期时显示「—」（回落订单头交期） */
function sodeLineDatesHtml(dates) {
  const list = dates || [];
  if (!list.length) return '<span class="muted">—</span>';
  return list.map(d => `${escapeHtml(d.productName || '')} ${sodeDate(d.deliveryDate)}`).join('<br>');
}

function sodeRenderKpi(data) {
  const el = document.getElementById('sode-kpi');
  if (!el) return;
  const c = data.counts || {};
  el.innerHTML = `
    <div class="kpi-card cargo">
      <div class="kpi-label">符合筛选的销售订单</div>
      <div class="kpi-value">${data.total}<span class="unit">张</span></div>
      <div class="kpi-delta flat">本页 ${c.total || 0} 张 · 第 ${data.page}/${data.totalPages} 页 · 基准日 ${fmtDate(data.asOfDate)}</div>
    </div>
    <div class="kpi-card danger">
      <div class="kpi-label">逾期 / 即将到期</div>
      <div class="kpi-value">${c.overdue || 0} / ${c.dueSoon || 0}<span class="unit">张</span></div>
      <div class="kpi-delta flat">逾期与即将到期均指仍未出齐（部分出货仍为未出齐）</div>
    </div>
    <div class="kpi-card gold">
      <div class="kpi-label">已出齐 / 在途 / 未知</div>
      <div class="kpi-value">${c.fulfilled || 0} / ${c.onSchedule || 0} / ${c.unknown || 0}<span class="unit">张</span></div>
      <div class="kpi-delta flat">已出齐 = 已审核出库覆盖订单数量；未知绝不回落为 0</div>
    </div>`;
}

function sodeRenderTable(data) {
  const el = document.getElementById('sode-table');
  if (!el) return;
  const rows = (data.items || []).map(o => `<tr>
      <td>${escapeHtml(o.orderNo)}</td>
      <td>${fmtDate(o.orderDate)}</td>
      <td>${statusHtml(o.status)}</td>
      <td>${escapeHtml(o.customerName || ('客户 ' + o.customerId))}</td>
      <td>${sodeDate(o.headerDeliveryDate)}</td>
      <td>${sodeLineDatesHtml(o.lineDeliveryDates)}</td>
      <td>${sodeDate(o.effectiveDeliveryDate)}</td>
      <td class="text-right">${sodeMoney(o.approvedShippedQuantity)} / ${sodeMoney(o.orderedQuantity)}</td>
      <td class="text-right">${sodeMoney(o.outstandingQuantity)}</td>
      <td>${sodeShipmentHtml(o.shipmentStatus)}</td>
      <td>${sodeStatusHtml(o.deliveryStatus)}</td>
      <td title="${escapeHtml(o.note || '')}">${escapeHtml(o.note || '')}</td>
    </tr>`).join('');
  el.innerHTML = `<table><thead><tr>
      <th>订单号</th><th>订单日期</th><th>单据状态</th><th>客户</th>
      <th>订单交期</th><th>明细交期</th><th>判定交期</th>
      <th class="text-right">已出 / 已订</th><th class="text-right">未出</th>
      <th>出货状态</th><th>交期状态</th><th>说明</th>
    </tr></thead>
    <tbody>${rows || '<tr><td colspan="12" class="empty">没有符合筛选条件的销售订单（可放宽客户 / 关键字筛选）</td></tr>'}</tbody></table>`;
}

function sodeRenderPagination(data) {
  const el = document.getElementById('sode-pagination');
  if (!el) return;
  const page = data.page || 1;
  const totalPages = data.totalPages || 1;
  el.innerHTML = `
    <button class="btn btn-neutral btn-sm" ${page <= 1 ? 'disabled' : ''} onclick="loadSalesOrderDeliveryExceptions(${page - 1})">上一页</button>
    <span style="margin:0 10px">第 ${page} / ${totalPages} 页（共 ${data.total} 张订单）</span>
    <button class="btn btn-neutral btn-sm" ${page >= totalPages ? 'disabled' : ''} onclick="loadSalesOrderDeliveryExceptions(${page + 1})">下一页</button>`;
}

/* 导出本页订单明细为 CSV（与表格同一套口径：未知值按表格文本原样导出，不回落为 0） */
function exportSodeCsv() {
  const table = document.querySelector('#sode-table table');
  if (!table) { toast('暂无可导出数据', 'warning'); return; }
  const rows = Array.from(table.querySelectorAll('tr'));
  const csv = rows.map(tr => Array.from(tr.querySelectorAll('th,td'))
    .map(td => `"${td.textContent.replace(/\r?\n/g, ';').replace(/"/g, '""')}"`).join(',')).join('\n');
  const blob = new Blob(['\uFEFF' + csv], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = '销售交期异常.csv';
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
  toast('CSV 已导出', 'success');
}

