/* ============ 采购交期异常工作台（ERP-099：只读派生；按供应商 + 显式 as-of 基准日派生逾期 / 即将到期 / 晚确认 / 已收齐） ============
   口径与后端 PurchaseOrderDeliveryExceptionSemantics 一一对应：
   - 交期状态：received 已收齐 / overdue 逾期 / due_soon 即将到期 / late_confirmation 晚确认 / on_schedule 在途正常 / unknown 未知；
   - 收货证据只取「以本单为来源、未删除、已审核」的采购入库；部分收货仍视为未收齐；
   - 缺日期或收货证据不完整显示「未知」，绝不回落为 0；
   - 只读派生：不改写订单任何已登记进度，不执行迁移 / 生产 SQL / 部署。 */

const PDE_STATUS_LABELS = {
  received: '已收齐',
  overdue: '逾期',
  due_soon: '即将到期',
  late_confirmation: '晚确认',
  on_schedule: '在途正常',
  unknown: '未知',
};
const PDE_RECEIPT_LABELS = {
  none: '未收货', partial: '部分收货', complete: '已收齐', over_received: '超收', unknown: '未知',
};

/* 工具栏入口（采购订单页）：渲染独立工作台页（只读，不落库） */
function openPurchaseOrderDeliveryExceptions() {
  CURRENT_PAGE_CODE = 'purchase-order-delivery-exceptions';
  document.getElementById('header-title').textContent = '采购交期异常';
  const today = pdeToday();
  document.getElementById('content').innerHTML = `
    <div class="page-hero report-hero">
      <h2>🚚 采购交期异常</h2>
      <p>按供应商 + 显式 as-of 基准日派生交期异常（逾期 / 即将到期 / 晚确认 / 已收齐）· 只读派生，未知不推断</p>
    </div>

    <div class="pd-hint" style="margin-bottom:10px">
      ⚠️ 只读视图：只按既有要求交期 / 供应商确认交期与已审核入库收货证据派生交期状态，<b>不</b>改写订单任何已登记进度，
      也<b>不</b>执行迁移 / 生产 SQL / 部署。
    </div>

    <div class="toolbar">
      <div class="toolbar-left" style="flex-wrap:wrap;gap:8px;align-items:center">
        <label>供应商 <select id="pde-supplier" style="min-width:170px"><option value="">全部供应商</option></select></label>
        <label>基准日 as-of <input type="date" id="pde-asof" value="${today}" style="width:150px"></label>
        <label>关键字 <input type="text" id="pde-keyword" style="width:190px" placeholder="采购单号 / 合同号 / 归属销售订单号"></label>
        <label>每页 <input type="number" id="pde-pagesize" value="50" min="1" max="200" style="width:70px"></label>
      </div>
      <div class="toolbar-actions">
        <button class="btn btn-primary" onclick="loadPurchaseOrderDeliveryExceptions(1)">查询</button>
        <button class="btn btn-neutral" onclick="exportPdeCsv()" title="导出本页订单明细为 CSV">📤 导出 CSV</button>
      </div>
    </div>

    <div class="kpi-grid" id="pde-kpi"></div>
    <div class="table-wrap" id="pde-table">
      <div class="skeleton-row"></div><div class="skeleton-row"></div>
    </div>
    <div class="pd-hint" id="pde-rule"></div>
    <div class="pagination" id="pde-pagination"></div>`;
  loadPdeSuppliers();
  loadPurchaseOrderDeliveryExceptions(1);
}

function pdeToday() {
  const d = new Date();
  return d.getFullYear() + '-' + String(d.getMonth() + 1).padStart(2, '0') + '-' + String(d.getDate()).padStart(2, '0');
}

/* 供应商下拉：既有基础资料接口；失败不阻断工作台（仍可留空或只用其它筛选） */
async function loadPdeSuppliers() {
  try {
    const data = await api('/api/base/suppliers?page=1&pageSize=200');
    const sel = document.getElementById('pde-supplier');
    if (!sel) return;
    (data.items || []).forEach(s => {
      const opt = document.createElement('option');
      opt.value = s.id;
      opt.textContent = s.supplierName || ('供应商 ' + s.id);
      sel.appendChild(opt);
    });
  } catch (e) { /* 忽略：供应商下拉失败不影响工作台查询 */ }
}

function pdeVal(id) {
  const el = document.getElementById(id);
  return el ? String(el.value || '').trim() : '';
}

function pdeQuery(page) {
  const q = new URLSearchParams();
  if (pdeVal('pde-supplier')) q.set('supplierId', pdeVal('pde-supplier'));
  if (pdeVal('pde-asof')) q.set('asOfDate', pdeVal('pde-asof'));
  if (pdeVal('pde-keyword')) q.set('keyword', pdeVal('pde-keyword'));
  q.set('page', page || 1);
  q.set('pageSize', pdeVal('pde-pagesize') || '50');
  return q.toString();
}

async function loadPurchaseOrderDeliveryExceptions(page) {
  const el = document.getElementById('pde-table');
  if (!el) return;
  el.innerHTML = '<div class="skeleton-row"></div><div class="skeleton-row"></div>';
  try {
    const data = await api('/api/purchase-orders/delivery-exceptions?' + pdeQuery(page));
    pdeRenderKpi(data);
    pdeRenderTable(data);
    pdeRenderPagination(data);
    const rule = document.getElementById('pde-rule');
    if (rule) rule.textContent = '口径：' + (data.rule || '') + ' ' + (data.scopeNote || '');
  } catch (e) {
    el.innerHTML = `<div class="empty"><div style="font-size:48px">⚠️</div><div>工作台加载失败：${escapeHtml(e.message)}</div></div>`;
  }
}

/* 未知（null）= 缺日期 / 命中等派生命中上限；显示「未知」而不是 0 */
function pdeMoney(v) { return v === null || v === undefined ? '未知' : fmtMoney(v); }
function pdeDate(v) { return v ? fmtDate(v) : '未知'; }

function pdeStatusHtml(status) {
  const cls = status === 'received' ? 'status-success'
    : (status === 'overdue' || status === 'late_confirmation' ? 'status-danger'
      : (status === 'due_soon' ? 'status-warning'
        : (status === 'unknown' ? 'status-warning' : 'status-neutral')));
  return `<span class="status ${cls}">${escapeHtml(PDE_STATUS_LABELS[status] || status || '')}</span>`;
}

function pdeReceiptHtml(status) {
  const cls = status === 'complete' ? 'status-success'
    : (status === 'over_received' ? 'status-danger'
      : (status === 'unknown' ? 'status-warning' : 'status-neutral'));
  return `<span class="status ${cls}">${escapeHtml(PDE_RECEIPT_LABELS[status] || status || '')}</span>`;
}

function pdeRenderKpi(data) {
  const el = document.getElementById('pde-kpi');
  if (!el) return;
  const c = data.counts || {};
  el.innerHTML = `
    <div class="kpi-card cargo">
      <div class="kpi-label">符合筛选的采购订单</div>
      <div class="kpi-value">${data.total}<span class="unit">张</span></div>
      <div class="kpi-delta flat">本页 ${c.total || 0} 张 · 第 ${data.page}/${data.totalPages} 页 · 基准日 ${fmtDate(data.asOfDate)}</div>
    </div>
    <div class="kpi-card danger">
      <div class="kpi-label">逾期 / 即将到期</div>
      <div class="kpi-value">${c.overdue || 0} / ${c.dueSoon || 0}<span class="unit">张</span></div>
      <div class="kpi-delta flat">逾期与即将到期均指仍未收齐（部分收货仍为未收齐）</div>
    </div>
    <div class="kpi-card gold">
      <div class="kpi-label">晚确认 / 已收齐 / 未知</div>
      <div class="kpi-value">${c.lateConfirmation || 0} / ${c.received || 0} / ${c.unknown || 0}<span class="unit">张</span></div>
      <div class="kpi-delta flat">晚确认 = 供应商确认交期晚于要求交期或要求交期已过仍未确认；未知绝不回落为 0</div>
    </div>`;
}

function pdeRenderTable(data) {
  const el = document.getElementById('pde-table');
  if (!el) return;
  const rows = (data.items || []).map(o => `<tr>
      <td>${escapeHtml(o.orderNo)}</td>
      <td>${fmtDate(o.orderDate)}</td>
      <td>${statusHtml(o.status)}</td>
      <td>${escapeHtml(o.supplierName || ('供应商 ' + o.supplierId))}</td>
      <td>${pdeDate(o.requestedDate)}</td>
      <td>${pdeDate(o.confirmedDate)}</td>
      <td class="text-right">${pdeMoney(o.receivedQuantity)} / ${pdeMoney(o.orderedQuantity)}</td>
      <td class="text-right">${pdeMoney(o.outstandingQuantity)}</td>
      <td>${pdeReceiptHtml(o.receiptStatus)}</td>
      <td>${pdeDate(o.lastReceiptDate)}</td>
      <td>${pdeStatusHtml(o.deliveryStatus)}</td>
      <td title="${escapeHtml(o.note || '')}">${escapeHtml(o.note || '')}</td>
    </tr>`).join('');
  el.innerHTML = `<table><thead><tr>
      <th>采购单号</th><th>订单日期</th><th>单据状态</th><th>供应商</th>
      <th>要求交期</th><th>供应商确认交期</th>
      <th class="text-right">已收 / 已订</th><th class="text-right">未收</th>
      <th>收货状态</th><th>最近入库</th><th>交期状态</th><th>说明</th>
    </tr></thead>
    <tbody>${rows || '<tr><td colspan="12" class="empty">没有符合筛选条件的采购订单（可放宽供应商 / 关键字筛选）</td></tr>'}</tbody></table>`;
}

function pdeRenderPagination(data) {
  const el = document.getElementById('pde-pagination');
  if (!el) return;
  const page = data.page || 1;
  const totalPages = data.totalPages || 1;
  el.innerHTML = `
    <button class="btn btn-neutral btn-sm" ${page <= 1 ? 'disabled' : ''} onclick="loadPurchaseOrderDeliveryExceptions(${page - 1})">上一页</button>
    <span style="margin:0 10px">第 ${page} / ${totalPages} 页（共 ${data.total} 张订单）</span>
    <button class="btn btn-neutral btn-sm" ${page >= totalPages ? 'disabled' : ''} onclick="loadPurchaseOrderDeliveryExceptions(${page + 1})">下一页</button>`;
}

/* 导出本页订单明细为 CSV（与表格同一套口径：未知值按表格文本原样导出，不回落为 0） */
function exportPdeCsv() {
  const table = document.querySelector('#pde-table table');
  if (!table) { toast('暂无可导出数据', 'warning'); return; }
  const rows = Array.from(table.querySelectorAll('tr'));
  const csv = rows.map(tr => Array.from(tr.querySelectorAll('th,td'))
    .map(td => `"${td.textContent.replace(/"/g, '""')}"`).join(',')).join('\n');
  const blob = new Blob(['\uFEFF' + csv], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = '采购交期异常.csv';
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
  toast('CSV 已导出', 'success');
}

