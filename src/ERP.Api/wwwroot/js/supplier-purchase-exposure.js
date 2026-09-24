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

