/* ==================================================================================
   ========== 客户收款引用登记册（ERP-053）— 收款单 → 销售订单 的引用证据 ==========
   ==================================================================================
   定位：在**既有**客户收款单（收款单）之上登记「这笔收款指向哪几张销售订单」的引用证据，
         让收款与销售订单的对应关系可追溯，并在收款单侧显式展示已引用 / 未引用金额。
   为什么需要本登记册（ERP-053 审计结论）：ERP-032 / ERP-046 的权威口径里收款单
         （FinanceReceipt，引用字段 SalesOrderProgress.ReferenceReceipt = FinanceReceipt.CustomerId）
         只记录客户、**没有订单级持久化引用**，历史收款证据只能作为「未关联证据」列出；
         仓库中不存在可复用的收款单 → 订单权威关系，因此本登记册是**唯一**的收款引用模型
         （不在收款单 / 销售订单上加列，也不建第二套链接表）。
   边界（界面侧同样遵守）：
     1. 本登记册不是银行入账 / 到账凭证、不是应收账款台账或余额、不是货款核销、不是客户对账单、
        不是税务（销项）判断，也不构成债务清偿；登记 / 作废引用行都不会真的收款、不会移动资金；
     2. 只读写 CustomerReceiptAllocations 一张表：不改写收款单状态 / 金额 / 币种 / 付款方式 / 银行账户，
        也不改写销售订单状态 / 出货进度 / 金额与明细、客户信用状态、发票、库存成本、装柜与单证、费用与退税；
     3. 引用只允许同客户 + 同币种且未取消的销售订单：系统不会按单号 / 金额 / 日期相似度猜测订单；
     4. 引用金额按币种精度取整（JPY 等 0 位小数，其余 2 位），有效行合计不得超过收款单金额；
        同一订单在同一收款单内只能有一条有效引用行；
     5. 更正走显式作废（必填原因）：保留原始值 / 快照 / 历史，不提供硬删除与静默替换。
   文案与服务端 CustomerReceiptAllocationRules / CustomerReceiptAllocationService 保持一致。
   ================================================================================== */

let CRA = {
  view: 'list',
  list: [], total: 0, page: 1, pageSize: 50,
  filters: { status: '', currency: '', dateFrom: '', dateTo: '', keyword: '', salesOrderId: '' },
  receipts: [],            // 可引用收款单候选
  receiptSummary: null,    // 当前收款单汇总
  currentReceiptId: null,  // 当前收款单 Id
  candidates: [],          // 可引用销售订单候选
  candidateKeyword: '',    // 候选订单关键字
  amounts: {},             // 每行待登记金额工作副本：{ salesOrderId: amount }
  remarks: {},             // 每行备注工作副本
  current: null,           // 当前引用行（详情 / 作废）
  voidReason: '',
  hint: ''
};

const CRA_CURRENCIES = ['CNY', 'USD', 'EUR', 'HKD', 'GBP', 'JPY'];
const CRA_STATUSES = [{ value: 1, label: '有效' }, { value: 2, label: '已作废' }];

/* 打开登记册（销售订单行操作会传入订单 Id：自动预填该单的客户并在台账内预筛选） */
async function openCustomerReceiptAllocationRegister(salesOrderId) {
  CRA = {
    view: 'list', list: [], total: 0, page: 1, pageSize: 50,
    filters: { status: '', currency: '', dateFrom: '', dateTo: '', keyword: '', salesOrderId: '' },
    receipts: [], receiptSummary: null, currentReceiptId: null,
    candidates: [], candidateKeyword: '', amounts: {}, remarks: {},
    current: null, voidReason: '', hint: ''
  };

  const modal = document.getElementById('modal');
  modal.innerHTML = '<div style="padding:40px;text-align:center;color:#64748b">正在加载…</div>';
  modal.style.display = 'block';

  if (salesOrderId) {
    try {
      const order = await api('/api/sales-orders/' + salesOrderId);
      CRA.filters.salesOrderId = String(order.id || '');
      CRA.hint = '从销售订单「' + (order.orderNo || '') + '」进入：台账已按该订单预筛选（只显示指向本单的引用行）。';
    } catch (e) {
      toast('销售订单加载失败：' + e.message, 'error');
    }
  }

  await craLoadReceipts();
  await craLoadList();
  craRender();
}

/* 可引用收款单候选（既有、未删除；含有效行已引用 / 未引用金额与资格文案） */
async function craLoadReceipts(customerId) {
  const params = ['take=200'];
  if (customerId) params.push('customerId=' + encodeURIComponent(customerId));
  try {
    CRA.receipts = await api('/api/customer-receipt-allocations/receipts?' + params.join('&')) || [];
  } catch (e) {
    CRA.receipts = [];
    toast('收款单候选加载失败：' + e.message, 'error');
  }
}

async function craLoadList() {
  const f = CRA.filters;
  const params = ['page=' + CRA.page, 'pageSize=' + CRA.pageSize];
  if (f.status !== '') params.push('status=' + encodeURIComponent(f.status));
  if (f.currency) params.push('currency=' + encodeURIComponent(f.currency));
  if (f.dateFrom) params.push('allocatedDateFrom=' + encodeURIComponent(f.dateFrom));
  if (f.dateTo) params.push('allocatedDateTo=' + encodeURIComponent(f.dateTo));
  if (f.keyword) params.push('keyword=' + encodeURIComponent(f.keyword));
  if (f.salesOrderId) params.push('salesOrderId=' + encodeURIComponent(f.salesOrderId));

  try {
    const res = await api('/api/customer-receipt-allocations?' + params.join('&'));
    CRA.list = (res && res.items) ? res.items : [];
    CRA.total = (res && res.total) || 0;
  } catch (e) {
    CRA.list = []; CRA.total = 0;
    toast('收款引用台账加载失败：' + e.message, 'error');
  }
}

function craRender() {
  const modal = document.getElementById('modal');
  modal.innerHTML = `
    <div style="max-width:1320px;margin:2vh auto;background:#fff;border-radius:14px;padding:16px 18px;max-height:94vh;overflow:auto;box-shadow:0 20px 60px rgba(0,0,0,.25)">
      <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:8px">
        <h3 style="margin:0">🧾 客户收款引用登记册
          <span style="font-size:13px;color:#64748b;font-weight:400;margin-left:10px">
            登记「这笔收款指向哪几张销售订单」的引用证据（不是到账凭证 / 应收账款台账 / 货款核销 / 客户对账单 / 税务判断）</span></h3>
        <button class="btn btn-neutral btn-sm" onclick="closeModal()">✕ 关闭</button>
      </div>
      ${CRA.hint ? `<div style="padding:6px 10px;background:#eff6ff;border:1px solid #bfdbfe;border-radius:8px;color:#1e40af;font-size:13px;margin-bottom:8px">${escapeHtml(CRA.hint)}</div>` : ''}
      <div style="padding:6px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;color:#334155;font-size:12px;margin-bottom:8px">
        引用只允许<b>同客户 + 同币种</b>且未取消的销售订单；引用金额按币种精度取整、有效行合计不得超过收款单金额；
        同一订单在同一收款单内只能有一条有效引用行；更正走<b>显式作废（必填原因）</b>，历史保留可读。
      </div>
      ${CRA.view === 'list' ? craListView() : ''}
      ${CRA.view === 'allocate' ? craAllocationView() : ''}
      ${CRA.view === 'void' ? craVoidView() : ''}
    </div>`;
  modal.style.display = 'block';
}

/* ==================== 台账列表 ==================== */

function craMoney(v) { return Number(v || 0).toFixed(2); }

function craStatusBadge(row) {
  if (row.isActive) return '<span class="status status-success">有效</span>';
  if (row.isVoided) return '<span class="status status-neutral">已作废</span>'
    + `<div class="text-muted">${escapeHtml(row.voidReason || '')}</div>`;
  return `<span class="text-muted">${escapeHtml(row.statusText || '')}</span>`;
}

function craListView() {
  const f = CRA.filters;
  const rows = CRA.list.map(row => `
    <tr>
      <td>${escapeHtml(row.receiptNo || '')}
        <div class="text-muted">${fmtDate(row.receiptDate)} · ${escapeHtml(row.receiptStatusText || '')}
        · ${craMoney(row.receiptAmount)} ${escapeHtml(row.currency || '')}</div></td>
      <td>${escapeHtml(row.customerName || '')}
        <div class="text-muted">${escapeHtml(row.customerCode || '')}</div></td>
      <td>${escapeHtml(row.orderNo || '')}
        <div class="text-muted">${fmtDate(row.orderDate)} · ${escapeHtml(row.orderStatusText || '')}</div></td>
      <td style="text-align:right"><b>${craMoney(row.allocatedAmount)}</b> ${escapeHtml(row.currency || '')}</td>
      <td>${craStatusBadge(row)}</td>
      <td>${escapeHtml(row.receiptAvailabilityText || '')}
        <div class="text-muted">${escapeHtml(row.orderAvailabilityText || '')}</div></td>
      <td>${fmtDate(row.allocatedAt)}</td>
      <td style="white-space:nowrap">
        <button class="btn btn-neutral btn-sm" onclick="craOpenDetail(${row.id})">详情</button>
        ${row.isActive ? `<button class="btn btn-neutral btn-sm" onclick="craOpenVoid(${row.id})">作废</button>` : ''}
      </td>
    </tr>`).join('');

  return `
    <div style="display:grid;grid-template-columns:repeat(5,1fr);gap:8px;margin-bottom:8px">
      <div><label class="ea-lb">状态</label>
        <select style="width:100%" onchange="CRA.filters.status=this.value">
          <option value="">（全部，含已作废历史）</option>
          ${CRA_STATUSES.map(s => `<option value="${s.value}" ${String(f.status) === String(s.value) ? 'selected' : ''}>${s.label}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">币种</label>
        <select style="width:100%" onchange="CRA.filters.currency=this.value">
          <option value="">（全部）</option>
          ${CRA_CURRENCIES.map(c => `<option value="${c}" ${f.currency === c ? 'selected' : ''}>${c}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">登记日期从</label>
        <input type="date" style="width:100%" value="${escapeHtml(f.dateFrom)}" onchange="CRA.filters.dateFrom=this.value"></div>
      <div><label class="ea-lb">登记日期到</label>
        <input type="date" style="width:100%" value="${escapeHtml(f.dateTo)}" onchange="CRA.filters.dateTo=this.value"></div>
      <div><label class="ea-lb">关键字（收款单号 / 销售订单号 / 客户）</label>
        <input style="width:100%" value="${escapeHtml(f.keyword)}" onchange="CRA.filters.keyword=this.value"
          placeholder="如 SK20260901 或客户名称"></div>
    </div>
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <div style="font-size:13px;color:#475569">共 ${CRA.total} 条引用行${f.salesOrderId ? '（已按销售订单预筛选）' : ''}</div>
      <div style="display:flex;gap:8px">
        ${f.salesOrderId ? '<button class="btn btn-neutral btn-sm" onclick="craClearOrderFilter()">清除订单筛选</button>' : ''}
        <button class="btn btn-neutral btn-sm" onclick="craSearch()">查询</button>
        <button class="btn btn-primary btn-sm" onclick="craPickReceipt()">＋ 按收款单登记引用</button>
      </div>
    </div>
    <div class="table-wrap" style="max-height:52vh;overflow:auto">
      <table>
        <thead><tr>
          <th>收款单</th><th>客户</th><th>销售订单</th>
          <th style="text-align:right">引用金额</th><th>状态</th><th>可用性</th><th>登记时间</th><th>操作</th>
        </tr></thead>
        <tbody>${rows || '<tr><td colspan="8" style="text-align:center;color:#64748b;padding:18px">暂无收款引用记录（登记引用不会真的收款、不会结算、不会核销）</td></tr>'}</tbody>
      </table>
    </div>
    <div style="display:flex;justify-content:flex-end;gap:8px;margin-top:8px">
      <button class="btn btn-neutral btn-sm" onclick="craPage(-1)" ${CRA.page <= 1 ? 'disabled' : ''}>上一页</button>
      <span class="text-muted" style="line-height:30px">第 ${CRA.page} 页</span>
      <button class="btn btn-neutral btn-sm" onclick="craPage(1)" ${CRA.list.length < CRA.pageSize ? 'disabled' : ''}>下一页</button>
    </div>`;
}

function craClearOrderFilter() {
  CRA.filters.salesOrderId = '';
  CRA.hint = '';
  craSearch();
}

async function craSearch() {
  CRA.page = 1;
  await craLoadList();
  craRender();
}

async function craPage(delta) {
  const next = CRA.page + delta;
  if (next < 1) return;
  CRA.page = next;
  await craLoadList();
  craRender();
}

/* ==================== 收款单侧登记（引用维护） ==================== */

function craPickReceipt() {
  if (!CRA.receipts.length) {
    toast('当前没有可引用的收款单（收款单必须存在且未删除）', 'error');
    return;
  }
  CRA.currentReceiptId = null;
  CRA.receiptSummary = null;
  CRA.candidates = [];
  CRA.amounts = {};
  CRA.remarks = {};
  CRA.view = 'allocate';
  craRender();
}

async function craSelectReceipt(receiptId) {
  if (!receiptId) { CRA.currentReceiptId = null; CRA.receiptSummary = null; craRender(); return; }
  await craOpenAllocations(receiptId);
}

async function craOpenAllocations(receiptId) {
  CRA.currentReceiptId = receiptId;
  CRA.amounts = {};
  CRA.remarks = {};
  CRA.candidateKeyword = '';
  CRA.view = 'allocate';
  craRender();

  try {
    CRA.receiptSummary = await api('/api/customer-receipt-allocations/receipts/' + receiptId + '/summary');
  } catch (e) {
    CRA.receiptSummary = null;
    toast('收款单汇总加载失败：' + e.message, 'error');
  }
  await craLoadCandidates('');
  craRender();
}

async function craLoadCandidates(keyword) {
  const receiptId = CRA.currentReceiptId;
  if (!receiptId) { CRA.candidates = []; return; }
  const params = ['take=200'];
  if (keyword) params.push('keyword=' + encodeURIComponent(keyword));
  try {
    CRA.candidates = await api('/api/customer-receipt-allocations/receipts/' + receiptId
      + '/order-candidates?' + params.join('&')) || [];
  } catch (e) {
    CRA.candidates = [];
    toast('销售订单候选加载失败：' + e.message, 'error');
  }
}

async function craCandidateSearch() {
  await craLoadCandidates(CRA.candidateKeyword);
  craRender();
}

function craSetAmount(orderId, value) { CRA.amounts[orderId] = value; }
function craSetRemark(orderId, value) { CRA.remarks[orderId] = value; }

async function craCreateAllocation(orderId) {
  const receiptId = CRA.currentReceiptId;
  if (!receiptId) { toast('请先选择收款单', 'error'); return; }
  const amount = Number(CRA.amounts[orderId] || 0);
  if (!(amount > 0)) { toast('请填写大于 0 的引用金额', 'error'); return; }

  const summary = CRA.receiptSummary;
  const currency = summary ? summary.currency : '';
  if (!confirm('登记收款引用？\n'
    + `收款单 ${summary ? summary.receiptNo : ''} 引用金额 ${craMoney(amount)} ${currency} 到该销售订单。\n`
    + '登记只写入引用证据：不会真的收款、不会改变收款单与销售订单状态、不会结算或核销。')) return;

  try {
    await api('/api/customer-receipt-allocations', 'POST', {
      receiptId: receiptId,
      salesOrderId: orderId,
      allocatedAmount: amount,
      remark: CRA.remarks[orderId] || ''
    });
    toast('收款引用已登记（未执行收款、未结算、未核销）');
    CRA.amounts = {};
    CRA.remarks = {};
    CRA.receiptSummary = await api('/api/customer-receipt-allocations/receipts/' + receiptId + '/summary');
    await craLoadReceipts();
    await craLoadCandidates(CRA.candidateKeyword);
    await craLoadList();
    craRender();
  } catch (e) {
    toast('登记收款引用失败：' + e.message, 'error');
  }
}

function craReceiptSummaryHtml(s) {
  return `
    <div style="display:grid;grid-template-columns:repeat(6,1fr);gap:8px;padding:8px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;font-size:13px;margin-bottom:8px">
      <div>收款单<div><b>${escapeHtml(s.receiptNo || '')}</b></div></div>
      <div>收款日期<div>${fmtDate(s.receiptDate)}</div></div>
      <div>状态<div>${escapeHtml(s.receiptStatusText || '')}</div></div>
      <div>客户<div>${escapeHtml(s.customerName || '')}
        <div class="text-muted">${escapeHtml(s.customerCode || '')}</div></div></div>
      <div>收款金额<div><b>${craMoney(s.receiptAmount)}</b> ${escapeHtml(s.currency || '')}</div></div>
      <div>已引用 / 未引用<div>${craMoney(s.allocatedAmount)} / <b>${craMoney(s.unallocatedAmount)}</b></div></div>
    </div>
    <div style="font-size:13px;margin-bottom:8px">
      <div><b>引用状态</b>：${escapeHtml(s.linkageText || '')}</div>
      <div class="text-muted">有效引用行 ${s.allocationCount} 条；已作废历史 ${s.voidedCount} 条（已作废行不占用收款金额额度）</div>
      <div class="text-muted">${escapeHtml(s.receiptAvailabilityText || '')}</div>
      <div class="text-muted">${escapeHtml(s.boundaryText || '')}</div>
    </div>`;
}

function craAllocationView() {
  const receiptId = CRA.currentReceiptId;
  const options = CRA.receipts.map(r =>
    `<option value="${r.receiptId}" ${String(r.receiptId) === String(receiptId) ? 'selected' : ''}>`
    + `${escapeHtml(r.receiptNo || '')} · ${escapeHtml(r.customerName || '')} · ${craMoney(r.receiptAmount)} ${escapeHtml(r.currency || '')}`
    + `（已引用 ${craMoney(r.allocatedAmount)}）</option>`).join('');

  const picker = `
    <div style="display:flex;gap:8px;align-items:flex-end;margin-bottom:8px">
      <div style="flex:1"><label class="ea-lb">收款单（既有、未删除；收款引用证据的登记对象）</label>
        <select style="width:100%" onchange="craSelectReceipt(this.value)">
          <option value="">（请选择收款单）</option>
          ${options}
        </select></div>
      <button class="btn btn-neutral btn-sm" onclick="craBackToList()">← 返回台账</button>
    </div>`;

  if (!receiptId || !CRA.receiptSummary) {
    return `
      <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
        <h4 style="margin:0">按收款单登记引用</h4>
        <button class="btn btn-neutral btn-sm" onclick="craBackToList()">← 返回台账</button>
      </div>
      ${picker}
      <div class="text-muted">请选择一张收款单：系统会显示该收款金额的已引用 / 未引用金额，并只列出<b>同客户 + 同币种</b>的未取消销售订单候选。</div>`;
  }

  const s = CRA.receiptSummary;
  const candRows = CRA.candidates.map(c => `
    <tr>
      <td>${escapeHtml(c.orderNo || '')}<div class="text-muted">${fmtDate(c.orderDate)}</div></td>
      <td>${escapeHtml(c.statusText || '')}</td>
      <td style="text-align:right">${craMoney(c.orderedAmount)}</td>
      <td style="text-align:right">${craMoney(c.allocatedByThisReceipt)}</td>
      <td style="text-align:right">${craMoney(c.allocatedByOtherReceipts)}</td>
      <td style="text-align:right">${craMoney(c.remainingUnallocatedAmount)}</td>
      <td style="color:${c.eligible ? '#166534' : '#b91c1c'}">${escapeHtml(c.eligibilityText || '')}</td>
      <td style="width:130px">
        <input type="number" step="0.01" min="0" style="width:100%" ${c.eligible ? '' : 'disabled'}
          value="${CRA.amounts[c.salesOrderId] !== undefined ? CRA.amounts[c.salesOrderId] : ''}"
          onchange="craSetAmount(${c.salesOrderId}, this.value)"></td>
      <td style="width:150px">
        <input style="width:100%" ${c.eligible ? '' : 'disabled'} placeholder="备注（可选）"
          value="${escapeHtml(CRA.remarks[c.salesOrderId] || '')}"
          onchange="craSetRemark(${c.salesOrderId}, this.value)"></td>
      <td style="white-space:nowrap">
        <button class="btn btn-primary btn-sm" ${c.eligible ? '' : 'disabled'}
          onclick="craCreateAllocation(${c.salesOrderId})">登记引用</button>
      </td>
    </tr>`).join('');

  const rowList = (s.allocations || []).map(a => `
    <tr>
      <td>${escapeHtml(a.orderNo || '')}<div class="text-muted">${fmtDate(a.orderDate)} · ${escapeHtml(a.orderStatusText || '')}</div></td>
      <td style="text-align:right">${craMoney(a.allocatedAmount)} ${escapeHtml(a.currency || '')}</td>
      <td>${craStatusBadge(a)}</td>
      <td>${escapeHtml(a.orderAvailabilityText || '')}</td>
      <td>${escapeHtml(a.remark || '—')}</td>
      <td>${fmtDate(a.allocatedAt)}</td>
      <td style="white-space:nowrap">
        <button class="btn btn-neutral btn-sm" onclick="craOpenDetail(${a.id})">详情</button>
        ${a.isActive ? `<button class="btn btn-neutral btn-sm" onclick="craOpenVoid(${a.id})">作废</button>` : ''}
      </td>
    </tr>`).join('');

  return `
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <h4 style="margin:0">按收款单登记引用：${escapeHtml(s.receiptNo || '')}</h4>
      <button class="btn btn-neutral btn-sm" onclick="craBackToList()">← 返回台账</button>
    </div>
    ${picker}
    ${craReceiptSummaryHtml(s)}

    <div style="display:flex;gap:8px;align-items:flex-end;margin:8px 0">
      <div style="flex:1"><label class="ea-lb">销售订单候选关键字（销售订单号 / 外销合同号）</label>
        <input style="width:100%" value="${escapeHtml(CRA.candidateKeyword)}"
          onchange="CRA.candidateKeyword=this.value" placeholder="如 SO20260901"></div>
      <button class="btn btn-neutral btn-sm" onclick="craCandidateSearch()">查询候选</button>
    </div>
    <div class="table-wrap" style="max-height:32vh;overflow:auto">
      <table>
        <thead><tr>
          <th>销售订单号</th><th>状态</th>
          <th style="text-align:right">订单总额</th><th style="text-align:right">本收款单已引用</th>
          <th style="text-align:right">其他收款单已引用</th><th style="text-align:right">剩余未覆盖</th>
          <th>可否引用</th><th style="width:130px">引用金额</th><th style="width:150px">备注</th><th>操作</th>
        </tr></thead>
        <tbody>${candRows || '<tr><td colspan="10" style="text-align:center;color:#64748b;padding:12px">没有同客户 + 同币种的销售订单候选（系统不会按相似度猜测订单）</td></tr>'}</tbody>
      </table>
    </div>

    <h4 style="margin:12px 0 6px">本收款单的引用行（含已作废历史）</h4>
    <div class="table-wrap" style="max-height:28vh;overflow:auto">
      <table>
        <thead><tr>
          <th>销售订单</th><th style="text-align:right">引用金额</th><th>状态</th>
          <th>订单可用性</th><th>备注</th><th>登记时间</th><th>操作</th>
        </tr></thead>
        <tbody>${rowList || '<tr><td colspan="7" style="text-align:center;color:#64748b;padding:12px">该收款单尚未登记任何引用行（未引用金额不会被猜测到任何订单）</td></tr>'}</tbody>
      </table>
    </div>`;
}

/* ==================== 详情 / 作废 / 返回 ==================== */

async function craOpenDetail(id) {
  try {
    CRA.current = await api('/api/customer-receipt-allocations/' + id);
    CRA.view = 'detail';
    craRenderDetail();
  } catch (e) {
    toast('详情加载失败：' + e.message, 'error');
  }
}

function craDetailHtml() {
  const row = CRA.current;
  if (!row) return '<div class="text-muted">请选择引用行。</div>';

  return `
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <h4 style="margin:0">收款引用详情 ${craStatusBadge(row)}</h4>
      <div style="display:flex;gap:8px">
        ${row.isActive ? `<button class="btn btn-neutral btn-sm" onclick="craOpenVoid(${row.id})">作废</button>` : ''}
        <button class="btn btn-neutral btn-sm" onclick="craBackFromDetail()">← 返回</button>
      </div>
    </div>
    <div style="display:grid;grid-template-columns:repeat(4,1fr);gap:8px;font-size:13px;margin-bottom:8px">
      <div>收款单<div><b>${escapeHtml(row.receiptNo || '')}</b></div></div>
      <div>收款日期<div>${fmtDate(row.receiptDate)}</div></div>
      <div>收款单状态（快照）<div>${escapeHtml(row.receiptStatusText || '')}</div></div>
      <div>收款金额（快照）<div>${craMoney(row.receiptAmount)} ${escapeHtml(row.currency || '')}</div></div>
      <div>销售订单<div><b>${escapeHtml(row.orderNo || '')}</b></div></div>
      <div>订单日期<div>${fmtDate(row.orderDate)}</div></div>
      <div>订单状态（快照）<div>${escapeHtml(row.orderStatusText || '')}</div></div>
      <div>订单币种（快照）<div>${escapeHtml(row.orderCurrency || '')}</div></div>
      <div>客户（快照）<div>${escapeHtml(row.customerName || '')} ${escapeHtml(row.customerCode || '')}</div></div>
      <div>引用金额<div><b>${craMoney(row.allocatedAmount)}</b> ${escapeHtml(row.currency || '')}</div></div>
      <div>金额精度<div>${row.amountDecimals} 位小数（按币种口径）</div></div>
      <div>登记时间<div>${fmtDate(row.allocatedAt)}</div></div>
      <div>收款单可用性<div>${escapeHtml(row.receiptAvailabilityText || '')}</div></div>
      <div>订单可用性<div>${escapeHtml(row.orderAvailabilityText || '')}</div></div>
      <div>作废时间<div>${row.voidedAt ? fmtDate(row.voidedAt) : '（未作废）'}</div></div>
      <div>作废原因<div>${escapeHtml(row.voidReason || '—')}</div></div>
      <div>备注<div>${escapeHtml(row.remark || '—')}</div></div>
    </div>
    <div style="padding:8px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;font-size:13px">
      <div><b>边界</b>：${escapeHtml(row.boundaryText || '')}</div>
    </div>`;
}

function craRenderDetail() {
  const modal = document.getElementById('modal');
  modal.innerHTML = `
    <div style="max-width:960px;margin:4vh auto;background:#fff;border-radius:14px;padding:16px 18px;max-height:92vh;overflow:auto;box-shadow:0 20px 60px rgba(0,0,0,.25)">
      ${craDetailHtml()}
    </div>`;
  modal.style.display = 'block';
}

/* ==================== 作废（保留历史） ==================== */

function craOpenVoid(id) {
  const pools = [
    CRA.list || [],
    (CRA.receiptSummary && CRA.receiptSummary.allocations) || [],
    CRA.current ? [CRA.current] : []
  ];
  let row = null;
  for (const pool of pools) {
    const hit = pool.find(x => x.id === id);
    if (hit) { row = hit; break; }
  }
  if (!row) { toast('未找到该引用行，请刷新台账后重试', 'error'); return; }

  CRA.current = row;
  CRA.voidReason = '';
  CRA.view = 'void';
  craRender();
}

function craVoidView() {
  const row = CRA.current;
  if (!row) return '<div class="text-muted">请选择引用行。</div>';

  return `
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <h4 style="margin:0">作废收款引用行</h4>
      <button class="btn btn-neutral btn-sm" onclick="craBackToList()">← 返回台账</button>
    </div>
    <div style="display:grid;grid-template-columns:repeat(4,1fr);gap:8px;padding:8px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;font-size:13px;margin-bottom:8px">
      <div>收款单<div><b>${escapeHtml(row.receiptNo || '')}</b></div></div>
      <div>销售订单<div><b>${escapeHtml(row.orderNo || '')}</b></div></div>
      <div>引用金额<div><b>${craMoney(row.allocatedAmount)}</b> ${escapeHtml(row.currency || '')}</div></div>
      <div>登记时间<div>${fmtDate(row.allocatedAt)}</div></div>
    </div>
    <div style="padding:8px 10px;background:#fef2f2;border:1px solid #fecaca;border-radius:8px;color:#991b1b;font-size:13px">
      作废<b>保留</b>原始引用金额、收款单 / 客户 / 销售订单快照与审计历史（不物理删除、不静默替换），
      也<b>不会</b>改写收款单与销售订单，更不会撤销任何真实收款或核销动作；作废后该订单可重新登记有效引用行。
      请填写作废原因（必填）。
    </div>
    <div style="margin-top:8px"><label class="ea-lb">作废原因 *</label>
      <textarea id="cra-void-reason" style="width:100%;height:64px" onchange="CRA.voidReason=this.value"
        placeholder="如：引用金额录错 / 收款单指向的订单选错 / 重复登记">${escapeHtml(CRA.voidReason || '')}</textarea></div>
    <div style="display:flex;justify-content:flex-end;gap:8px;margin-top:12px">
      <button class="btn btn-neutral" onclick="craBackToList()">取消</button>
      <button class="btn btn-danger" onclick="craConfirmVoid()">🗑 确认作废（保留历史）</button>
    </div>`;
}

async function craConfirmVoid() {
  const row = CRA.current;
  if (!row) return;
  const reason = String(CRA.voidReason || '').trim();
  if (!reason) { toast('请填写作废原因', 'error'); return; }

  try {
    await api('/api/customer-receipt-allocations/' + row.id + '/void', 'POST', { reason: reason });
    toast('收款引用已作废（原始值与快照保留可读）');
    CRA.current = null;
    CRA.voidReason = '';
    await craLoadReceipts();
    await craLoadList();
    if (CRA.currentReceiptId) {
      try {
        CRA.receiptSummary = await api('/api/customer-receipt-allocations/receipts/'
          + CRA.currentReceiptId + '/summary');
      } catch (e) {
        CRA.receiptSummary = null;
      }
      await craLoadCandidates(CRA.candidateKeyword);
    }
    CRA.view = 'list';
    craRender();
  } catch (e) {
    toast('作废失败：' + e.message, 'error');
  }
}

function craBackFromDetail() {
  if (CRA.currentReceiptId) { craOpenAllocations(CRA.currentReceiptId); return; }
  craBackToList();
}

function craBackToList() {
  CRA.view = 'list';
  CRA.receiptSummary = null;
  CRA.candidates = [];
  craRender();
}
