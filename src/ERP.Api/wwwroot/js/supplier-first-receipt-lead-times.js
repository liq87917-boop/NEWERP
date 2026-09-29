/* ============ 供应商首收交期（ERP-109：只读派生；按供应商 + 订单日期区间统计「订单日期 → 首张有效已审核入库」的间隔天数） ============
   口径与后端 SupplierFirstReceiptLeadTimeRules 一一对应：
   - 首收状态：received 已派生 / negative_interval 负间隔（异常，不钳制为 0）/ unavailable 未知；
   - 首收只取「以本单为来源、未删除、已审核、供应商与本单一致」的最早入库；
   - 未审核 / 已删除 / 供应商不一致 / 早于订单日期的入库仅作异常证据列出、不计入首收；
   - 缺失或不一致日期显示「未知」，绝不回落为 0；只读派生，不改写任何单据。 */

const SFRT_STATUS_LABELS = {
  received: '已派生',
  negative_interval: '负间隔（异常）',
  unavailable: '未知',
};

/* 工具栏入口（采购订单页）：渲染独立报表页（只读，不落库） */
function openSupplierFirstReceiptLeadTimes() {
  CURRENT_PAGE_CODE = 'supplier-first-receipt-lead-times';
  document.getElementById('header-title').textContent = '供应商首收交期';
  document.getElementById('content').innerHTML = `
    <div class="page-hero report-hero">
      <h2>⏱️ 供应商首收交期</h2>
      <p>按供应商 + 订单日期统计「订单日期 → 首张有效已审核入库」的间隔天数 · 只读派生，未知不推断、负间隔不钳制</p>
    </div>

    <div class="pd-hint" style="margin-bottom:10px">
      ⚠️ 只读视图：只按既有入库单的显式采购订单链接派生首收日期与间隔天数，<b>不</b>改写采购订单 / 入库单 / 库存，
      <b>不</b>是完整交付完成度，也<b>不</b>是准时率评分。
    </div>

    <div class="toolbar">
      <div class="toolbar-left" style="flex-wrap:wrap;gap:8px;align-items:center">
        <label>供应商 <select id="sfrt-supplier" style="min-width:170px"><option value="">全部供应商</option></select></label>
        <label>订单日期 <input type="date" id="sfrt-date-from" style="width:140px"> 至
          <input type="date" id="sfrt-date-to" style="width:140px"></label>
        <label>关键字 <input type="text" id="sfrt-keyword" style="width:190px" placeholder="采购单号 / 合同号 / 归属销售订单号"></label>
        <label>每页 <input type="number" id="sfrt-pagesize" value="50" min="1" max="200" style="width:70px"></label>
      </div>
      <div class="toolbar-actions">
        <button class="btn btn-primary" onclick="loadSupplierFirstReceiptLeadTimes(1)">查询</button>
        <button class="btn btn-neutral" onclick="exportSfrtCsv()" title="导出本页订单明细为 CSV">📤 导出 CSV</button>
      </div>
    </div>

    <div class="kpi-grid" id="sfrt-kpi"></div>
    <div class="table-wrap" id="sfrt-table">
      <div class="skeleton-row"></div><div class="skeleton-row"></div>
    </div>
    <div class="pd-hint" id="sfrt-rule"></div>
    <div class="pagination" id="sfrt-pagination"></div>`;
  loadSfrtSuppliers();
  loadSupplierFirstReceiptLeadTimes(1);
}

/* 供应商下拉：既有基础资料接口；失败不阻断报表（仍可留空或只用其它筛选） */
async function loadSfrtSuppliers() {
  try {
    const data = await api('/api/base/suppliers?page=1&pageSize=200');
    const sel = document.getElementById('sfrt-supplier');
    if (!sel) return;
    (data.items || []).forEach(s => {
      const opt = document.createElement('option');
      opt.value = s.id;
      opt.textContent = s.supplierName || ('供应商 ' + s.id);
      sel.appendChild(opt);
    });
  } catch (e) { /* 忽略：供应商下拉失败不影响报表查询 */ }
}

function sfrtVal(id) {
  const el = document.getElementById(id);
  return el ? String(el.value || '').trim() : '';
}

function sfrtQuery(page) {
  const q = new URLSearchParams();
  if (sfrtVal('sfrt-supplier')) q.set('supplierId', sfrtVal('sfrt-supplier'));
  if (sfrtVal('sfrt-date-from')) q.set('orderDateFrom', sfrtVal('sfrt-date-from'));
  if (sfrtVal('sfrt-date-to')) q.set('orderDateTo', sfrtVal('sfrt-date-to'));
  if (sfrtVal('sfrt-keyword')) q.set('keyword', sfrtVal('sfrt-keyword'));
  q.set('page', page || 1);
  q.set('pageSize', sfrtVal('sfrt-pagesize') || '50');
  return q.toString();
}

async function loadSupplierFirstReceiptLeadTimes(page) {
  const el = document.getElementById('sfrt-table');
  if (!el) return;
  el.innerHTML = '<div class="skeleton-row"></div><div class="skeleton-row"></div>';
  try {
    const data = await api('/api/purchase-orders/first-receipt-lead-times?' + sfrtQuery(page));
    sfrtRenderKpi(data);
    sfrtRenderTable(data);
    sfrtRenderPagination(data);
    const rule = document.getElementById('sfrt-rule');
    if (rule) rule.textContent = '口径：' + (data.rule || '') + ' ' + (data.scopeNote || '') + ' ' + (data.boundary || '');
  } catch (e) {
    el.innerHTML = `<div class="empty"><div style="font-size:48px">⚠️</div><div>报表加载失败：${escapeHtml(e.message)}</div></div>`;
  }
}

/* 未知（null）= 缺首收日期 / 命中等派生命中上限；显示「未知」而不是 0；负间隔保留负值不钳制 */
function sfrtDate(v) { return v ? fmtDate(v) : '未知'; }
function sfrtDays(v) { return v === null || v === undefined ? '未知' : String(v); }

function sfrtStatusHtml(status) {
  const cls = status === 'received' ? 'status-success'
    : (status === 'negative_interval' ? 'status-danger' : 'status-warning');
  return `<span class="status ${cls}">${escapeHtml(SFRT_STATUS_LABELS[status] || status || '')}</span>`;
}

function sfrtAnomalyHtml(anomalies) {
  if (!anomalies || !anomalies.length) return '';
  return `<span class="status status-warning" title="${escapeHtml(anomalies.join('；'))}">⚠ 异常</span>`;
}


function sfrtRenderKpi(data) {
  const el = document.getElementById('sfrt-kpi');
  if (!el) return;
  const c = data.counts || {};
  el.innerHTML = `
    <div class="kpi-card cargo">
      <div class="kpi-label">符合筛选的已审核采购订单</div>
      <div class="kpi-value">${data.total}<span class="unit">张</span></div>
      <div class="kpi-delta flat">本页 ${c.total || 0} 张 · 第 ${data.page}/${data.totalPages} 页</div>
    </div>
    <div class="kpi-card success">
      <div class="kpi-label">已派生首收</div>
      <div class="kpi-value">${c.received || 0}<span class="unit">张</span></div>
      <div class="kpi-delta flat">首收日期 = 最早「未删除、已审核、供应商一致」入库</div>
    </div>
    <div class="kpi-card danger">
      <div class="kpi-label">负间隔 / 未知 / 异常</div>
      <div class="kpi-value">${c.negativeInterval || 0} / ${c.unavailable || 0} / ${c.anomalous || 0}<span class="unit">张</span></div>
      <div class="kpi-delta flat">负间隔不钳制为 0；未知绝不回落为 0</div>
    </div>`;
}

function sfrtRenderTable(data) {
  const el = document.getElementById('sfrt-table');
  if (!el) return;
  const rows = (data.items || []).map(o => `<tr>
      <td><a href="javascript:void(0)" onclick="navigate('purchase-order','采购订单')">${escapeHtml(o.orderNo)}</a></td>
      <td>${fmtDate(o.orderDate)}</td>
      <td>${statusHtml(o.status)}</td>
      <td>${escapeHtml(o.supplierName || ('供应商 ' + o.supplierId))}</td>
      <td>${o.firstReceiptNo ? `<a href="javascript:void(0)" onclick="navigate('stock-in','采购入库')">${escapeHtml(o.firstReceiptNo)}</a>` : '未知'}</td>
      <td>${sfrtDate(o.firstReceiptDate)}</td>
      <td class="text-right">${sfrtDays(o.elapsedDays)}</td>
      <td>${sfrtStatusHtml(o.leadTimeStatus)}</td>
      <td>${sfrtAnomalyHtml(o.anomalies)}</td>
      <td title="${escapeHtml(o.note || '')}">${escapeHtml(o.note || '')}</td>
    </tr>`).join('');
  el.innerHTML = `<table><thead><tr>
      <th>采购单号</th><th>订单日期</th><th>单据状态</th><th>供应商</th>
      <th>首收入库单</th><th>首收日期</th><th class="text-right">间隔天数</th>
      <th>首收状态</th><th>异常</th><th>说明</th>
    </tr></thead>
    <tbody>${rows || '<tr><td colspan="10" class="empty">没有符合筛选条件的已审核采购订单（可放宽供应商 / 日期 / 关键字筛选）</td></tr>'}</tbody></table>`;
}

function sfrtRenderPagination(data) {
  const el = document.getElementById('sfrt-pagination');
  if (!el) return;
  const page = data.page || 1;
  const totalPages = data.totalPages || 1;
  el.innerHTML = `
    <button class="btn btn-neutral btn-sm" ${page <= 1 ? 'disabled' : ''} onclick="loadSupplierFirstReceiptLeadTimes(${page - 1})">上一页</button>
    <span style="margin:0 10px">第 ${page} / ${totalPages} 页（共 ${data.total} 张订单）</span>
    <button class="btn btn-neutral btn-sm" ${page >= totalPages ? 'disabled' : ''} onclick="loadSupplierFirstReceiptLeadTimes(${page + 1})">下一页</button>`;
}

/* 导出本页订单明细为 CSV（与表格同一套口径：未知值按表格文本原样导出，不回落为 0） */
function exportSfrtCsv() {
  const table = document.querySelector('#sfrt-table table');
  if (!table) { toast('暂无可导出数据', 'warning'); return; }
  const rows = Array.from(table.querySelectorAll('tr'));
  const csv = rows.map(tr => Array.from(tr.querySelectorAll('th,td'))
    .map(td => `"${td.textContent.replace(/"/g, '""')}"`).join(',')).join('\n');
  const blob = new Blob(['\uFEFF' + csv], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = '供应商首收交期.csv';
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
  toast('CSV 已导出', 'success');
}
