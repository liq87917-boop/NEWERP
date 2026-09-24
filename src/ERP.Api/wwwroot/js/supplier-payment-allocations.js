/* ==================================================================================
   ========== 供应商付款引用登记册（ERP-049）— 付款单 → 采购订单 的引用证据 ==========
   ==================================================================================
   定位：在**既有**供应商付款单（付款单）之上登记「这笔付款指向哪几张采购订单」的引用证据，
         让付款与采购订单的对应关系可追溯，并在付款单侧显式展示已引用 / 未引用金额。
   边界（界面侧同样遵守）：
     1. 本登记册不是银行付款凭证、不是应付账款核销、不是发票核销、不是税务（进项）判断，
        也不是供应商余额；登记 / 作废引用行都不会真的付款、不会移动资金；
     2. 只读写 SupplierPaymentAllocations 一张表：不改写付款单状态 / 金额 / 币种 / 付款方式 / 银行账户，
        也不改写采购订单状态 / 到货进度 / 金额与明细 / 结算进度、发票与发票关联、库存成本、退税与费用；
     3. 引用只允许同供应商 + 同币种且未取消的采购订单：系统不会按单号 / 金额 / 日期相似度猜测订单；
     4. 引用金额按币种精度取整（JPY 等 0 位小数，其余 2 位），有效行合计不得超过付款单金额；
        同一订单在同一付款单内只能有一条有效引用行；
     5. 更正走显式作废（必填原因）：保留原始值 / 快照 / 历史，不提供硬删除与静默替换。
   文案与服务端 SupplierPaymentAllocationRules / SupplierPaymentAllocationService 保持一致。
   ================================================================================== */

let SPA = {
  view: 'list',
  list: [], total: 0, page: 1, pageSize: 50,
  filters: { status: '', currency: '', dateFrom: '', dateTo: '', keyword: '', purchaseOrderId: '' },
  payments: [],           // 可引用付款单候选
  paymentSummary: null,   // 当前付款单汇总
  currentPaymentId: null, // 当前付款单 Id
  candidates: [],         // 可引用采购订单候选
  candidateKeyword: '',   // 候选订单关键字
  amounts: {},            // 每行待登记金额工作副本：{ purchaseOrderId: amount }
  remarks: {},            // 每行备注工作副本
  current: null,          // 当前引用行（详情 / 作废）
  voidReason: '',
  hint: ''
};

const SPA_CURRENCIES = ['CNY', 'USD', 'EUR', 'HKD', 'GBP', 'JPY'];
const SPA_STATUSES = [{ value: 1, label: '有效' }, { value: 2, label: '已作废' }];

/* 打开登记册（采购订单行操作会传入订单 Id：自动预填该单的供应商并在台账内预筛选） */
async function openSupplierPaymentAllocationRegister(purchaseOrderId) {
  SPA = {
    view: 'list', list: [], total: 0, page: 1, pageSize: 50,
    filters: { status: '', currency: '', dateFrom: '', dateTo: '', keyword: '', purchaseOrderId: '' },
    payments: [], paymentSummary: null, currentPaymentId: null,
    candidates: [], candidateKeyword: '', amounts: {}, remarks: {},
    current: null, voidReason: '', hint: ''
  };

  const modal = document.getElementById('modal');
  modal.innerHTML = '<div style="padding:40px;text-align:center;color:#64748b">正在加载…</div>';
  modal.style.display = 'block';

  if (purchaseOrderId) {
    try {
      const order = await api('/api/purchase-orders/' + purchaseOrderId);
      SPA.filters.purchaseOrderId = String(order.id || '');
      SPA.hint = '从采购订单「' + (order.orderNo || '') + '」进入：台账已按该订单预筛选（只显示指向本单的引用行）。';
    } catch (e) {
      toast('采购订单加载失败：' + e.message, 'error');
    }
  }

  await spaLoadPayments();
  await spaLoadList();
  spaRender();
}

/* 可引用付款单候选（既有、未删除；含有效行已引用 / 未引用金额与资格文案） */
async function spaLoadPayments(supplierId) {
  const params = ['take=200'];
  if (supplierId) params.push('supplierId=' + encodeURIComponent(supplierId));
  try {
    SPA.payments = await api('/api/supplier-payment-allocations/payments?' + params.join('&')) || [];
  } catch (e) {
    SPA.payments = [];
    toast('付款单候选加载失败：' + e.message, 'error');
  }
}

async function spaLoadList() {
  const f = SPA.filters;
  const params = ['page=' + SPA.page, 'pageSize=' + SPA.pageSize];
  if (f.status !== '') params.push('status=' + encodeURIComponent(f.status));
  if (f.currency) params.push('currency=' + encodeURIComponent(f.currency));
  if (f.dateFrom) params.push('allocatedDateFrom=' + encodeURIComponent(f.dateFrom));
  if (f.dateTo) params.push('allocatedDateTo=' + encodeURIComponent(f.dateTo));
  if (f.keyword) params.push('keyword=' + encodeURIComponent(f.keyword));
  if (f.purchaseOrderId) params.push('purchaseOrderId=' + encodeURIComponent(f.purchaseOrderId));

  try {
    const res = await api('/api/supplier-payment-allocations?' + params.join('&'));
    SPA.list = (res && res.items) ? res.items : [];
    SPA.total = (res && res.total) || 0;
  } catch (e) {
    SPA.list = []; SPA.total = 0;
    toast('付款引用台账加载失败：' + e.message, 'error');
  }
}

function spaRender() {
  const modal = document.getElementById('modal');
  modal.innerHTML = `
    <div style="max-width:1320px;margin:2vh auto;background:#fff;border-radius:14px;padding:16px 18px;max-height:94vh;overflow:auto;box-shadow:0 20px 60px rgba(0,0,0,.25)">
      <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:8px">
        <h3 style="margin:0">💳 供应商付款引用登记册
          <span style="font-size:13px;color:#64748b;font-weight:400;margin-left:10px">
            登记「这笔付款指向哪几张采购订单」的引用证据（不是付款凭证 / 应付账款核销 / 发票核销 / 税务判断 / 供应商余额）</span></h3>
        <button class="btn btn-neutral btn-sm" onclick="closeModal()">✕ 关闭</button>
      </div>
      ${SPA.hint ? `<div style="padding:6px 10px;background:#eff6ff;border:1px solid #bfdbfe;border-radius:8px;color:#1e40af;font-size:13px;margin-bottom:8px">${escapeHtml(SPA.hint)}</div>` : ''}
      <div style="padding:6px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;color:#334155;font-size:12px;margin-bottom:8px">
        引用只允许<b>同供应商 + 同币种</b>且未取消的采购订单；引用金额按币种精度取整、有效行合计不得超过付款单金额；
        同一订单在同一付款单内只能有一条有效引用行；更正走<b>显式作废（必填原因）</b>，历史保留可读。
      </div>
      ${SPA.view === 'list' ? spaListView() : ''}
      ${SPA.view === 'allocate' ? spaAllocationView() : ''}
      ${SPA.view === 'void' ? spaVoidView() : ''}
    </div>`;
  modal.style.display = 'block';
}


/* ==================== 台账列表 ==================== */

function spaMoney(v) { return Number(v || 0).toFixed(2); }

function spaStatusBadge(row) {
  if (row.isActive) return '<span class="status status-success">有效</span>';
  if (row.isVoided) return '<span class="status status-neutral">已作废</span>'
    + `<div class="text-muted">${escapeHtml(row.voidReason || '')}</div>`;
  return `<span class="text-muted">${escapeHtml(row.statusText || '')}</span>`;
}

function spaListView() {
  const f = SPA.filters;
  const rows = SPA.list.map(row => `
    <tr>
      <td>${escapeHtml(row.paymentNo || '')}
        <div class="text-muted">${fmtDate(row.paymentDate)} · ${escapeHtml(row.paymentStatusText || '')}
        · ${spaMoney(row.paymentAmount)} ${escapeHtml(row.currency || '')}</div></td>
      <td>${escapeHtml(row.supplierName || '')}
        <div class="text-muted">${escapeHtml(row.supplierCode || '')}</div></td>
      <td>${escapeHtml(row.orderNo || '')}
        <div class="text-muted">${fmtDate(row.orderDate)} · ${escapeHtml(row.orderStatusText || '')}</div></td>
      <td style="text-align:right"><b>${spaMoney(row.allocatedAmount)}</b> ${escapeHtml(row.currency || '')}</td>
      <td>${spaStatusBadge(row)}</td>
      <td>${escapeHtml(row.paymentAvailabilityText || '')}
        <div class="text-muted">${escapeHtml(row.orderAvailabilityText || '')}</div></td>
      <td>${fmtDate(row.allocatedAt)}</td>
      <td style="white-space:nowrap">
        <button class="btn btn-neutral btn-sm" onclick="spaOpenDetail(${row.id})">详情</button>
        ${row.isActive ? `<button class="btn btn-neutral btn-sm" onclick="spaOpenVoid(${row.id})">作废</button>` : ''}
      </td>
    </tr>`).join('');

  return `
    <div style="display:grid;grid-template-columns:repeat(5,1fr);gap:8px;margin-bottom:8px">
      <div><label class="ea-lb">状态</label>
        <select style="width:100%" onchange="SPA.filters.status=this.value">
          <option value="">（全部，含已作废历史）</option>
          ${SPA_STATUSES.map(s => `<option value="${s.value}" ${String(f.status) === String(s.value) ? 'selected' : ''}>${s.label}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">币种</label>
        <select style="width:100%" onchange="SPA.filters.currency=this.value">
          <option value="">（全部）</option>
          ${SPA_CURRENCIES.map(c => `<option value="${c}" ${f.currency === c ? 'selected' : ''}>${c}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">登记日期从</label>
        <input type="date" style="width:100%" value="${escapeHtml(f.dateFrom)}" onchange="SPA.filters.dateFrom=this.value"></div>
      <div><label class="ea-lb">登记日期到</label>
        <input type="date" style="width:100%" value="${escapeHtml(f.dateTo)}" onchange="SPA.filters.dateTo=this.value"></div>
      <div><label class="ea-lb">关键字（付款单号 / 采购单号 / 供应商）</label>
        <input style="width:100%" value="${escapeHtml(f.keyword)}" onchange="SPA.filters.keyword=this.value"
          placeholder="如 FK20260901 或供应商名称"></div>
    </div>
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <div style="font-size:13px;color:#475569">共 ${SPA.total} 条引用行${f.purchaseOrderId ? '（已按采购订单预筛选）' : ''}</div>
      <div style="display:flex;gap:8px">
        ${f.purchaseOrderId ? '<button class="btn btn-neutral btn-sm" onclick="spaClearOrderFilter()">清除订单筛选</button>' : ''}
        <button class="btn btn-neutral btn-sm" onclick="spaSearch()">查询</button>
        <button class="btn btn-primary btn-sm" onclick="spaPickPayment()">＋ 按付款单登记引用</button>
      </div>
    </div>
    <div class="table-wrap" style="max-height:52vh;overflow:auto">
      <table>
        <thead><tr>
          <th>付款单</th><th>供应商</th><th>采购订单</th>
          <th style="text-align:right">引用金额</th><th>状态</th><th>可用性</th><th>登记时间</th><th>操作</th>
        </tr></thead>
        <tbody>${rows || '<tr><td colspan="8" style="text-align:center;color:#64748b;padding:18px">暂无付款引用记录（登记引用不会执行付款、不会结算、不会核销）</td></tr>'}</tbody>
      </table>
    </div>
    <div style="display:flex;justify-content:flex-end;gap:8px;margin-top:8px">
      <button class="btn btn-neutral btn-sm" onclick="spaPage(-1)" ${SPA.page <= 1 ? 'disabled' : ''}>上一页</button>
      <span class="text-muted" style="line-height:30px">第 ${SPA.page} 页</span>
      <button class="btn btn-neutral btn-sm" onclick="spaPage(1)" ${SPA.list.length < SPA.pageSize ? 'disabled' : ''}>下一页</button>
    </div>`;
}

function spaClearOrderFilter() {
  SPA.filters.purchaseOrderId = '';
  SPA.hint = '';
  spaSearch();
}

async function spaSearch() {
  SPA.page = 1;
  await spaLoadList();
  spaRender();
}

async function spaPage(delta) {
  const next = SPA.page + delta;
  if (next < 1) return;
  SPA.page = next;
  await spaLoadList();
  spaRender();
}

/* ==================== 付款单侧登记（引用维护） ==================== */

function spaPickPayment() {
  if (!SPA.payments.length) {
    toast('当前没有可引用的付款单（付款单必须存在且未删除）', 'error');
    return;
  }
  SPA.currentPaymentId = null;
  SPA.paymentSummary = null;
  SPA.candidates = [];
  SPA.amounts = {};
  SPA.remarks = {};
  SPA.view = 'allocate';
  spaRender();
}

async function spaSelectPayment(paymentId) {
  if (!paymentId) { SPA.currentPaymentId = null; SPA.paymentSummary = null; spaRender(); return; }
  await spaOpenAllocations(paymentId);
}

async function spaOpenAllocations(paymentId) {
  SPA.currentPaymentId = paymentId;
  SPA.amounts = {};
  SPA.remarks = {};
  SPA.candidateKeyword = '';
  SPA.view = 'allocate';
  spaRender();

  try {
    SPA.paymentSummary = await api('/api/supplier-payment-allocations/payments/' + paymentId + '/summary');
  } catch (e) {
    SPA.paymentSummary = null;
    toast('付款单汇总加载失败：' + e.message, 'error');
  }
  await spaLoadCandidates('');
  spaRender();
}

async function spaLoadCandidates(keyword) {
  const paymentId = SPA.currentPaymentId;
  if (!paymentId) { SPA.candidates = []; return; }
  const params = ['take=200'];
  if (keyword) params.push('keyword=' + encodeURIComponent(keyword));
  try {
    SPA.candidates = await api('/api/supplier-payment-allocations/payments/' + paymentId
      + '/order-candidates?' + params.join('&')) || [];
  } catch (e) {
    SPA.candidates = [];
    toast('采购订单候选加载失败：' + e.message, 'error');
  }
}

async function spaCandidateSearch() {
  await spaLoadCandidates(SPA.candidateKeyword);
  spaRender();
}

function spaSetAmount(orderId, value) { SPA.amounts[orderId] = value; }
function spaSetRemark(orderId, value) { SPA.remarks[orderId] = value; }

async function spaCreateAllocation(orderId) {
  const paymentId = SPA.currentPaymentId;
  if (!paymentId) { toast('请先选择付款单', 'error'); return; }
  const amount = Number(SPA.amounts[orderId] || 0);
  if (!(amount > 0)) { toast('请填写大于 0 的引用金额', 'error'); return; }

  const summary = SPA.paymentSummary;
  const currency = summary ? summary.currency : '';
  if (!confirm('登记付款引用？\n'
    + `付款单 ${summary ? summary.paymentNo : ''} 引用金额 ${spaMoney(amount)} ${currency} 到该采购订单。\n`
    + '登记只写入引用证据：不会真的付款、不会改变付款单与采购订单状态、不会结算或核销。')) return;

  try {
    await api('/api/supplier-payment-allocations', 'POST', {
      paymentId: paymentId,
      purchaseOrderId: orderId,
      allocatedAmount: amount,
      remark: SPA.remarks[orderId] || ''
    });
    toast('付款引用已登记（未执行付款、未结算、未核销）');
    SPA.amounts = {};
    SPA.remarks = {};
    SPA.paymentSummary = await api('/api/supplier-payment-allocations/payments/' + paymentId + '/summary');
    await spaLoadPayments();
    await spaLoadCandidates(SPA.candidateKeyword);
    await spaLoadList();
    spaRender();
  } catch (e) {
    toast('登记付款引用失败：' + e.message, 'error');
  }
}

function spaPaymentSummaryHtml(s) {
  return `
    <div style="display:grid;grid-template-columns:repeat(6,1fr);gap:8px;padding:8px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;font-size:13px;margin-bottom:8px">
      <div>付款单<div><b>${escapeHtml(s.paymentNo || '')}</b></div></div>
      <div>付款日期<div>${fmtDate(s.paymentDate)}</div></div>
      <div>状态<div>${escapeHtml(s.paymentStatusText || '')}</div></div>
      <div>供应商<div>${escapeHtml(s.supplierName || '')}
        <div class="text-muted">${escapeHtml(s.supplierCode || '')}</div></div></div>
      <div>付款金额<div><b>${spaMoney(s.paymentAmount)}</b> ${escapeHtml(s.currency || '')}</div></div>
      <div>已引用 / 未引用<div>${spaMoney(s.allocatedAmount)} / <b>${spaMoney(s.unallocatedAmount)}</b></div></div>
    </div>
    <div style="font-size:13px;margin-bottom:8px">
      <div><b>引用状态</b>：${escapeHtml(s.linkageText || '')}</div>
      <div class="text-muted">有效引用行 ${s.allocationCount} 条；已作废历史 ${s.voidedCount} 条（已作废行不占用付款金额额度）</div>
      <div class="text-muted">${escapeHtml(s.paymentAvailabilityText || '')}</div>
      <div class="text-muted">${escapeHtml(s.boundaryText || '')}</div>
    </div>`;
}

function spaAllocationView() {
  const paymentId = SPA.currentPaymentId;
  const options = SPA.payments.map(p =>
    `<option value="${p.paymentId}" ${String(p.paymentId) === String(paymentId) ? 'selected' : ''}>`
    + `${escapeHtml(p.paymentNo || '')} · ${escapeHtml(p.supplierName || '')} · ${spaMoney(p.paymentAmount)} ${escapeHtml(p.currency || '')}`
    + `（已引用 ${spaMoney(p.allocatedAmount)}）</option>`).join('');

  const picker = `
    <div style="display:flex;gap:8px;align-items:flex-end;margin-bottom:8px">
      <div style="flex:1"><label class="ea-lb">付款单（既有、未删除；付款引用证据的登记对象）</label>
        <select style="width:100%" onchange="spaSelectPayment(this.value)">
          <option value="">（请选择付款单）</option>
          ${options}
        </select></div>
      <button class="btn btn-neutral btn-sm" onclick="spaBackToList()">← 返回台账</button>
    </div>`;

  if (!paymentId || !SPA.paymentSummary) {
    return `
      <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
        <h4 style="margin:0">按付款单登记引用</h4>
        <button class="btn btn-neutral btn-sm" onclick="spaBackToList()">← 返回台账</button>
      </div>
      ${picker}
      <div class="text-muted">请选择一张付款单：系统会显示该付款金额的已引用 / 未引用金额，并只列出<b>同供应商 + 同币种</b>的未取消采购订单候选。</div>`;
  }

  const s = SPA.paymentSummary;
  const candRows = SPA.candidates.map(c => `
    <tr>
      <td>${escapeHtml(c.orderNo || '')}<div class="text-muted">${fmtDate(c.orderDate)}</div></td>
      <td>${escapeHtml(c.statusText || '')}</td>
      <td style="text-align:right">${spaMoney(c.orderedAmount)}</td>
      <td style="text-align:right">${spaMoney(c.allocatedByThisPayment)}</td>
      <td style="text-align:right">${spaMoney(c.allocatedByOtherPayments)}</td>
      <td style="text-align:right">${spaMoney(c.remainingUnallocatedAmount)}</td>
      <td style="color:${c.eligible ? '#166534' : '#b91c1c'}">${escapeHtml(c.eligibilityText || '')}</td>
      <td style="width:130px">
        <input type="number" step="0.01" min="0" style="width:100%" ${c.eligible ? '' : 'disabled'}
          value="${SPA.amounts[c.purchaseOrderId] !== undefined ? SPA.amounts[c.purchaseOrderId] : ''}"
          onchange="spaSetAmount(${c.purchaseOrderId}, this.value)"></td>
      <td style="width:150px">
        <input style="width:100%" ${c.eligible ? '' : 'disabled'} placeholder="备注（可选）"
          value="${escapeHtml(SPA.remarks[c.purchaseOrderId] || '')}"
          onchange="spaSetRemark(${c.purchaseOrderId}, this.value)"></td>
      <td style="white-space:nowrap">
        <button class="btn btn-primary btn-sm" ${c.eligible ? '' : 'disabled'}
          onclick="spaCreateAllocation(${c.purchaseOrderId})">登记引用</button>
      </td>
    </tr>`).join('');


  const rowList = (s.allocations || []).map(a => `
    <tr>
      <td>${escapeHtml(a.orderNo || '')}<div class="text-muted">${fmtDate(a.orderDate)} · ${escapeHtml(a.orderStatusText || '')}</div></td>
      <td style="text-align:right">${spaMoney(a.allocatedAmount)} ${escapeHtml(a.currency || '')}</td>
      <td>${spaStatusBadge(a)}</td>
      <td>${escapeHtml(a.orderAvailabilityText || '')}</td>
      <td>${escapeHtml(a.remark || '—')}</td>
      <td>${fmtDate(a.allocatedAt)}</td>
      <td style="white-space:nowrap">
        <button class="btn btn-neutral btn-sm" onclick="spaOpenDetail(${a.id})">详情</button>
        ${a.isActive ? `<button class="btn btn-neutral btn-sm" onclick="spaOpenVoid(${a.id})">作废</button>` : ''}
      </td>
    </tr>`).join('');

  return `
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <h4 style="margin:0">按付款单登记引用：${escapeHtml(s.paymentNo || '')}</h4>
      <button class="btn btn-neutral btn-sm" onclick="spaBackToList()">← 返回台账</button>
    </div>
    ${picker}
    ${spaPaymentSummaryHtml(s)}

    <div style="display:flex;gap:8px;align-items:flex-end;margin:8px 0">
      <div style="flex:1"><label class="ea-lb">采购订单候选关键字（采购单号 / 合同号）</label>
        <input style="width:100%" value="${escapeHtml(SPA.candidateKeyword)}"
          onchange="SPA.candidateKeyword=this.value" placeholder="如 PO20260901"></div>
      <button class="btn btn-neutral btn-sm" onclick="spaCandidateSearch()">查询候选</button>
    </div>
    <div class="table-wrap" style="max-height:32vh;overflow:auto">
      <table>
        <thead><tr>
          <th>采购单号</th><th>状态</th>
          <th style="text-align:right">订单总额</th><th style="text-align:right">本付款单已引用</th>
          <th style="text-align:right">其他付款单已引用</th><th style="text-align:right">剩余未覆盖</th>
          <th>可否引用</th><th style="width:130px">引用金额</th><th style="width:150px">备注</th><th>操作</th>
        </tr></thead>
        <tbody>${candRows || '<tr><td colspan="10" style="text-align:center;color:#64748b;padding:12px">没有同供应商 + 同币种的采购订单候选（系统不会按相似度猜测订单）</td></tr>'}</tbody>
      </table>
    </div>

    <h4 style="margin:12px 0 6px">本付款单的引用行（含已作废历史）</h4>
    <div class="table-wrap" style="max-height:28vh;overflow:auto">
      <table>
        <thead><tr>
          <th>采购订单</th><th style="text-align:right">引用金额</th><th>状态</th>
          <th>订单可用性</th><th>备注</th><th>登记时间</th><th>操作</th>
        </tr></thead>
        <tbody>${rowList || '<tr><td colspan="7" style="text-align:center;color:#64748b;padding:12px">该付款单尚未登记任何引用行（未引用金额不会被猜测到任何订单）</td></tr>'}</tbody>
      </table>
    </div>`;
}

/* ==================== 详情 / 作废 / 返回 ==================== */

async function spaOpenDetail(id) {
  try {
    SPA.current = await api('/api/supplier-payment-allocations/' + id);
    SPA.view = 'detail';
    spaRenderDetail();
  } catch (e) {
    toast('详情加载失败：' + e.message, 'error');
  }
}

function spaDetailHtml() {
  const row = SPA.current;
  if (!row) return '<div class="text-muted">请选择引用行。</div>';

  return `
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <h4 style="margin:0">付款引用详情 ${spaStatusBadge(row)}</h4>
      <div style="display:flex;gap:8px">
        ${row.isActive ? `<button class="btn btn-neutral btn-sm" onclick="spaOpenVoid(${row.id})">作废</button>` : ''}
        <button class="btn btn-neutral btn-sm" onclick="spaBackFromDetail()">← 返回</button>
      </div>
    </div>
    <div style="display:grid;grid-template-columns:repeat(4,1fr);gap:8px;font-size:13px;margin-bottom:8px">
      <div>付款单<div><b>${escapeHtml(row.paymentNo || '')}</b></div></div>
      <div>付款日期<div>${fmtDate(row.paymentDate)}</div></div>
      <div>付款单状态（快照）<div>${escapeHtml(row.paymentStatusText || '')}</div></div>
      <div>付款金额（快照）<div>${spaMoney(row.paymentAmount)} ${escapeHtml(row.currency || '')}</div></div>
      <div>采购订单<div><b>${escapeHtml(row.orderNo || '')}</b></div></div>
      <div>订单日期<div>${fmtDate(row.orderDate)}</div></div>
      <div>订单状态（快照）<div>${escapeHtml(row.orderStatusText || '')}</div></div>
      <div>订单币种（快照）<div>${escapeHtml(row.orderCurrency || '')}</div></div>
      <div>供应商（快照）<div>${escapeHtml(row.supplierName || '')} ${escapeHtml(row.supplierCode || '')}</div></div>
      <div>引用金额<div><b>${spaMoney(row.allocatedAmount)}</b> ${escapeHtml(row.currency || '')}</div></div>
      <div>金额精度<div>${row.amountDecimals} 位小数（按币种口径）</div></div>
      <div>登记时间<div>${fmtDate(row.allocatedAt)}</div></div>
      <div>付款单可用性<div>${escapeHtml(row.paymentAvailabilityText || '')}</div></div>
      <div>订单可用性<div>${escapeHtml(row.orderAvailabilityText || '')}</div></div>
      <div>作废时间<div>${row.voidedAt ? fmtDate(row.voidedAt) : '（未作废）'}</div></div>
      <div>作废原因<div>${escapeHtml(row.voidReason || '—')}</div></div>
      <div>备注<div>${escapeHtml(row.remark || '—')}</div></div>
    </div>
    <div style="padding:8px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;font-size:13px">
      <div><b>边界</b>：${escapeHtml(row.boundaryText || '')}</div>
    </div>`;
}

function spaRenderDetail() {
  const modal = document.getElementById('modal');
  modal.innerHTML = `
    <div style="max-width:960px;margin:4vh auto;background:#fff;border-radius:14px;padding:16px 18px;max-height:92vh;overflow:auto;box-shadow:0 20px 60px rgba(0,0,0,.25)">
      ${spaDetailHtml()}
    </div>`;
  modal.style.display = 'block';
}

/* ==================== 作废（保留历史） ==================== */

function spaOpenVoid(id) {
  const pools = [
    SPA.list || [],
    (SPA.paymentSummary && SPA.paymentSummary.allocations) || [],
    SPA.current ? [SPA.current] : []
  ];
  let row = null;
  for (const pool of pools) {
    const hit = pool.find(x => x.id === id);
    if (hit) { row = hit; break; }
  }
  if (!row) { toast('未找到该引用行，请刷新台账后重试', 'error'); return; }

  SPA.current = row;
  SPA.voidReason = '';
  SPA.view = 'void';
  spaRender();
}

function spaVoidView() {
  const row = SPA.current;
  if (!row) return '<div class="text-muted">请选择引用行。</div>';

  return `
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <h4 style="margin:0">作废付款引用行</h4>
      <button class="btn btn-neutral btn-sm" onclick="spaBackToList()">← 返回台账</button>
    </div>
    <div style="display:grid;grid-template-columns:repeat(4,1fr);gap:8px;padding:8px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;font-size:13px;margin-bottom:8px">
      <div>付款单<div><b>${escapeHtml(row.paymentNo || '')}</b></div></div>
      <div>采购订单<div><b>${escapeHtml(row.orderNo || '')}</b></div></div>
      <div>引用金额<div><b>${spaMoney(row.allocatedAmount)}</b> ${escapeHtml(row.currency || '')}</div></div>
      <div>登记时间<div>${fmtDate(row.allocatedAt)}</div></div>
    </div>
    <div style="padding:8px 10px;background:#fef2f2;border:1px solid #fecaca;border-radius:8px;color:#991b1b;font-size:13px">
      作废<b>保留</b>原始引用金额、付款单 / 供应商 / 采购订单快照与审计历史（不物理删除、不静默替换），
      也<b>不会</b>改写付款单与采购订单，更不会撤销任何真实付款或核销动作；作废后该订单可重新登记有效引用行。
      请填写作废原因（必填）。
    </div>
    <div style="margin-top:8px"><label class="ea-lb">作废原因 *</label>
      <textarea id="spa-void-reason" style="width:100%;height:64px" onchange="SPA.voidReason=this.value"
        placeholder="如：引用金额录错 / 付款单指向的订单选错 / 重复登记">${escapeHtml(SPA.voidReason || '')}</textarea></div>
    <div style="display:flex;justify-content:flex-end;gap:8px;margin-top:12px">
      <button class="btn btn-neutral" onclick="spaBackToList()">取消</button>
      <button class="btn btn-danger" onclick="spaConfirmVoid()">🗑 确认作废（保留历史）</button>
    </div>`;
}

async function spaConfirmVoid() {
  const row = SPA.current;
  if (!row) return;
  const reason = String(SPA.voidReason || '').trim();
  if (!reason) { toast('请填写作废原因', 'error'); return; }

  try {
    await api('/api/supplier-payment-allocations/' + row.id + '/void', 'POST', { reason: reason });
    toast('付款引用已作废（原始值与快照保留可读）');
    SPA.current = null;
    SPA.voidReason = '';
    await spaLoadPayments();
    await spaLoadList();
    if (SPA.currentPaymentId) {
      try {
        SPA.paymentSummary = await api('/api/supplier-payment-allocations/payments/'
          + SPA.currentPaymentId + '/summary');
      } catch (e) {
        SPA.paymentSummary = null;
      }
      await spaLoadCandidates(SPA.candidateKeyword);
    }
    SPA.view = 'list';
    spaRender();
  } catch (e) {
    toast('作废失败：' + e.message, 'error');
  }
}

function spaBackFromDetail() {
  if (SPA.currentPaymentId) { spaOpenAllocations(SPA.currentPaymentId); return; }
  spaBackToList();
}

function spaBackToList() {
  SPA.view = 'list';
  SPA.paymentSummary = null;
  SPA.candidates = [];
  spaRender();
}
