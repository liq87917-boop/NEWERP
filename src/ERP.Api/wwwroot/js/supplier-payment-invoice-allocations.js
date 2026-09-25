/* ==================================================================================
   ====== 供应商付款 → 采购发票 引用登记册（ERP-066）— 付款单 → 已登记采购发票 的引用证据 ======
   ==================================================================================
   定位：在**既有**供应商付款单（付款单）与**既有**供应商采购发票（ERP-043 / ERP-065 证据）之上登记
         「这笔付款指向哪几张已登记采购发票」的引用证据，并在付款单侧 / 发票侧分别显式展示
         已引用与未引用金额（未引用金额绝不被猜测到任何发票或付款单）。
   边界（界面侧同样遵守）：
     1. 本登记册不是银行付款凭证、不是应付账款核销、不是发票认证 / 抵扣、不是税务申报，也不是供应商余额；
        登记 / 作废引用行都不会真的付款、不会移动资金、不会把发票或采购订单标记为已结清；
     2. 只读写 SupplierPaymentInvoiceAllocations 一张表：不改写付款单状态 / 金额 / 币种 / 付款方式 / 银行账户，
        也不改写发票类型 / 代码 / 号码 / 日期 / 到期日 / 付款条件 / 金额 / 状态 / 关联行，
        更不改写采购订单、库存成本、退税与费用；
     3. 引用只允许同供应商 + 同币种且**已登记（未作废）**的采购发票：系统不会按号码 / 金额 / 日期相似度猜测发票；
     4. 引用金额按币种精度取整（JPY 等 0 位小数，其余 2 位），既不得超过付款单未引用金额，
        也不得超过发票未引用含税总额；同一发票在同一付款单内只能有一条有效引用行；
     5. 更正走显式作废（必填原因）：保留原始值 / 快照 / 登记人与历史，不提供硬删除、改派与静默替换；
     6. 证据维度分离：本册金额与 ERP-049「付款单 → 采购订单」引用金额分别记录，界面上分别展示、**绝不相加**。
   文案与服务端 SupplierPaymentInvoiceAllocationRules / SupplierPaymentInvoiceAllocationService 保持一致。
   ================================================================================== */

let SPIA = {
  view: 'list',
  list: [], total: 0, page: 1, pageSize: 50,
  filters: { status: '', currency: '', dateFrom: '', dateTo: '', keyword: '', supplierId: '' },
  payments: [],           // 可引用付款单候选
  paymentSummary: null,   // 当前付款单汇总（含发票引用与独立标注的采购订单引用维度）
  currentPaymentId: null, // 当前付款单 Id
  candidates: [],         // 可引用采购发票候选
  candidateKeyword: '',   // 候选发票关键字
  amounts: {},            // 每行待登记金额工作副本：{ purchaseInvoiceId: amount }
  remarks: {},            // 每行备注工作副本
  current: null,          // 当前引用行（详情 / 作废）
  invoiceSummary: null,   // 当前发票侧汇总
  voidReason: '',
  hint: ''
};

const SPIA_CURRENCIES = ['CNY', 'USD', 'EUR', 'HKD', 'GBP', 'JPY'];
const SPIA_STATUSES = [{ value: 1, label: '有效' }, { value: 2, label: '已作废' }];

/* 打开登记册（采购订单行操作会传入订单 Id：自动预填该单的供应商并在台账内预筛选该供应商） */
async function openSupplierPaymentInvoiceAllocationRegister(purchaseOrderId) {
  SPIA = {
    view: 'list', list: [], total: 0, page: 1, pageSize: 50,
    filters: { status: '', currency: '', dateFrom: '', dateTo: '', keyword: '', supplierId: '' },
    payments: [], paymentSummary: null, currentPaymentId: null,
    candidates: [], candidateKeyword: '', amounts: {}, remarks: {},
    current: null, invoiceSummary: null, voidReason: '', hint: ''
  };

  const modal = document.getElementById('modal');
  modal.innerHTML = '<div style="padding:40px;text-align:center;color:#64748b">正在加载…</div>';
  modal.style.display = 'block';

  if (purchaseOrderId) {
    try {
      const order = await api('/api/purchase-orders/' + purchaseOrderId);
      SPIA.filters.supplierId = String(order.supplierId || '');
      SPIA.hint = '从采购订单「' + (order.orderNo || '') + '」进入：台账已按该单的供应商预筛选'
        + '（发票引用只允许同供应商 + 同币种的已登记发票）。';
      await spaLoadPayments(order.supplierId);
    } catch (e) {
      toast('采购订单加载失败：' + e.message, 'error');
    }
  }

  if (!SPIA.payments.length) await spaLoadPayments();
  await spaLoadList();
  spaRender();
}

/* 可引用付款单候选（既有、未删除；含发票引用已引用 / 未引用金额与资格文案） */
async function spaLoadPayments(supplierId) {
  const params = ['take=200'];
  if (supplierId) params.push('supplierId=' + encodeURIComponent(supplierId));
  try {
    SPIA.payments = await api('/api/supplier-payment-invoice-allocations/payments?' + params.join('&')) || [];
  } catch (e) {
    SPIA.payments = [];
    toast('付款单候选加载失败：' + e.message, 'error');
  }
}

async function spaLoadList() {
  const f = SPIA.filters;
  const params = ['page=' + SPIA.page, 'pageSize=' + SPIA.pageSize];
  if (f.status !== '') params.push('status=' + encodeURIComponent(f.status));
  if (f.currency) params.push('currency=' + encodeURIComponent(f.currency));
  if (f.dateFrom) params.push('allocatedDateFrom=' + encodeURIComponent(f.dateFrom));
  if (f.dateTo) params.push('allocatedDateTo=' + encodeURIComponent(f.dateTo));
  if (f.keyword) params.push('keyword=' + encodeURIComponent(f.keyword));
  if (f.supplierId) params.push('supplierId=' + encodeURIComponent(f.supplierId));

  try {
    const res = await api('/api/supplier-payment-invoice-allocations?' + params.join('&'));
    SPIA.list = (res && res.items) ? res.items : [];
    SPIA.total = (res && res.total) || 0;
  } catch (e) {
    SPIA.list = []; SPIA.total = 0;
    toast('付款发票引用台账加载失败：' + e.message, 'error');
  }
}

function spaRender() {
  const modal = document.getElementById('modal');
  modal.innerHTML = `
    <div style="max-width:1360px;margin:2vh auto;background:#fff;border-radius:14px;padding:16px 18px;max-height:94vh;overflow:auto;box-shadow:0 20px 60px rgba(0,0,0,.25)">
      <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:8px">
        <h3 style="margin:0">🧾 供应商付款发票引用登记册
          <span style="font-size:13px;color:#64748b;font-weight:400;margin-left:10px">
            登记「这笔付款指向哪几张已登记采购发票」的引用证据（不是付款凭证 / 应付账款核销 / 发票认证 / 税务申报 / 供应商余额）</span></h3>
        <button class="btn btn-neutral btn-sm" onclick="closeModal()">✕ 关闭</button>
      </div>
      ${SPIA.hint ? `<div style="padding:6px 10px;background:#eff6ff;border:1px solid #bfdbfe;border-radius:8px;color:#1e40af;font-size:13px;margin-bottom:8px">${escapeHtml(SPIA.hint)}</div>` : ''}
      <div style="padding:6px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;color:#334155;font-size:12px;margin-bottom:8px">
        引用只允许<b>同供应商 + 同币种</b>且<b>已登记（未作废）</b>的采购发票；引用金额按币种精度取整，
        既不得超过付款单未引用金额，也不得超过发票未引用含税总额；同一发票在同一付款单内只能有一条有效引用行；
        更正走<b>显式作废（必填原因）</b>，历史保留可读。本册与 ERP-049 的采购订单引用<b>分别记录、绝不相加</b>。
      </div>
      ${SPIA.view === 'list' ? spaListView() : ''}
      ${SPIA.view === 'allocate' ? spaAllocationView() : ''}
      ${SPIA.view === 'invoice' ? spaInvoiceView() : ''}
      ${SPIA.view === 'detail' ? spaDetailHtml() : ''}
      ${SPIA.view === 'void' ? spaVoidView() : ''}
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
  const f = SPIA.filters;
  const rows = SPIA.list.map(row => `
    <tr>
      <td>${escapeHtml(row.paymentNo || '')}
        <div class="text-muted">${fmtDate(row.paymentDate)} · ${escapeHtml(row.paymentStatusText || '')}
        · ${spaMoney(row.paymentAmount)} ${escapeHtml(row.currency || '')}</div></td>
      <td>${escapeHtml(row.supplierName || '')}
        <div class="text-muted">${escapeHtml(row.supplierCode || '')}</div></td>
      <td>${escapeHtml(row.invoiceIdentityText || row.invoiceNumber || '')}
        <div class="text-muted">${escapeHtml(row.invoiceTypeText || '')} · ${fmtDate(row.invoiceDate)}
        · ${escapeHtml(row.invoiceStatusText || '')}</div></td>
      <td style="text-align:right">${spaMoney(row.invoiceGrossAmount)} ${escapeHtml(row.currency || '')}</td>
      <td style="text-align:right"><b>${spaMoney(row.allocatedAmount)}</b> ${escapeHtml(row.currency || '')}</td>
      <td>${spaStatusBadge(row)}</td>
      <td>${escapeHtml(row.paymentAvailabilityText || '')}
        <div class="text-muted">${escapeHtml(row.invoiceAvailabilityText || '')}</div></td>
      <td>${fmtDate(row.allocatedAt)}
        <div class="text-muted">${escapeHtml(row.recordedBy || '')}</div></td>
      <td style="white-space:nowrap">
        <button class="btn btn-neutral btn-sm" onclick="spaOpenDetail(${row.id})">详情</button>
        <button class="btn btn-neutral btn-sm" onclick="spaOpenInvoiceSummary(${row.purchaseInvoiceId})">发票侧</button>
        ${row.isActive ? `<button class="btn btn-neutral btn-sm" onclick="spaOpenVoid(${row.id})">作废</button>` : ''}
      </td>
    </tr>`).join('');

  return `
    <div style="display:grid;grid-template-columns:repeat(5,1fr);gap:8px;margin-bottom:8px">
      <div><label class="ea-lb">状态</label>
        <select style="width:100%" onchange="SPIA.filters.status=this.value">
          <option value="">（全部，含已作废历史）</option>
          ${SPIA_STATUSES.map(s => `<option value="${s.value}" ${String(f.status) === String(s.value) ? 'selected' : ''}>${s.label}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">币种</label>
        <select style="width:100%" onchange="SPIA.filters.currency=this.value">
          <option value="">（全部）</option>
          ${SPIA_CURRENCIES.map(c => `<option value="${c}" ${f.currency === c ? 'selected' : ''}>${c}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">登记日期从</label>
        <input type="date" style="width:100%" value="${escapeHtml(f.dateFrom)}" onchange="SPIA.filters.dateFrom=this.value"></div>
      <div><label class="ea-lb">登记日期到</label>
        <input type="date" style="width:100%" value="${escapeHtml(f.dateTo)}" onchange="SPIA.filters.dateTo=this.value"></div>
      <div><label class="ea-lb">关键字（付款单号 / 发票号码 / 供应商）</label>
        <input style="width:100%" value="${escapeHtml(f.keyword)}" onchange="SPIA.filters.keyword=this.value"
          placeholder="如 FK20260901 或发票号码"></div>
    </div>
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <div style="font-size:13px;color:#475569">共 ${SPIA.total} 条引用行${f.supplierId ? '（已按供应商预筛选）' : ''}</div>
      <div style="display:flex;gap:8px">
        ${f.supplierId ? '<button class="btn btn-neutral btn-sm" onclick="spaClearSupplierFilter()">清除供应商筛选</button>' : ''}
        <button class="btn btn-neutral btn-sm" onclick="spaSearch()">查询</button>
        <button class="btn btn-primary btn-sm" onclick="spaPickPayment()">＋ 按付款单登记发票引用</button>
      </div>
    </div>
    <div class="table-wrap" style="max-height:52vh;overflow:auto">
      <table>
        <thead><tr>
          <th>付款单</th><th>供应商</th><th>采购发票</th>
          <th style="text-align:right">发票含税总额</th>
          <th style="text-align:right">引用金额</th><th>状态</th><th>可用性</th><th>登记时间 / 登记人</th><th>操作</th>
        </tr></thead>
        <tbody>${rows || '<tr><td colspan="9" style="text-align:center;color:#64748b;padding:18px">暂无付款发票引用记录（登记引用不会执行付款、不会核销、不会认证）</td></tr>'}</tbody>
      </table>
    </div>
    <div style="display:flex;justify-content:flex-end;gap:8px;margin-top:8px">
      <button class="btn btn-neutral btn-sm" onclick="spaPage(-1)" ${SPIA.page <= 1 ? 'disabled' : ''}>上一页</button>
      <span class="text-muted" style="line-height:30px">第 ${SPIA.page} 页</span>
      <button class="btn btn-neutral btn-sm" onclick="spaPage(1)" ${SPIA.list.length < SPIA.pageSize ? 'disabled' : ''}>下一页</button>
    </div>`;
}

function spaClearSupplierFilter() {
  SPIA.filters.supplierId = '';
  SPIA.hint = '';
  spaSearch();
}

async function spaSearch() {
  SPIA.page = 1;
  await spaLoadList();
  spaRender();
}

async function spaPage(delta) {
  const next = SPIA.page + delta;
  if (next < 1) return;
  SPIA.page = next;
  await spaLoadList();
  spaRender();
}

/* ==================== 付款单侧登记（发票引用维护） ==================== */

function spaPickPayment() {
  if (!SPIA.payments.length) {
    toast('当前没有可引用的付款单（付款单必须存在且未删除）', 'error');
    return;
  }
  SPIA.currentPaymentId = null;
  SPIA.paymentSummary = null;
  SPIA.candidates = [];
  SPIA.amounts = {};
  SPIA.remarks = {};
  SPIA.view = 'allocate';
  spaRender();
}

async function spaSelectPayment(paymentId) {
  if (!paymentId) {
    SPIA.currentPaymentId = null;
    SPIA.paymentSummary = null;
    SPIA.candidates = [];
    spaRender();
    return;
  }

  SPIA.currentPaymentId = Number(paymentId);
  SPIA.amounts = {};
  SPIA.remarks = {};
  try {
    SPIA.paymentSummary = await api('/api/supplier-payment-invoice-allocations/payments/'
      + paymentId + '/summary');
    await spaLoadCandidates('');
  } catch (e) {
    SPIA.paymentSummary = null;
    SPIA.candidates = [];
    toast('付款单汇总加载失败：' + e.message, 'error');
  }
  spaRender();
}

async function spaLoadCandidates(keyword) {
  if (!SPIA.currentPaymentId) { SPIA.candidates = []; return; }
  const params = ['take=200'];
  if (keyword) params.push('keyword=' + encodeURIComponent(keyword));
  try {
    SPIA.candidates = await api('/api/supplier-payment-invoice-allocations/payments/'
      + SPIA.currentPaymentId + '/invoice-candidates?' + params.join('&')) || [];
  } catch (e) {
    SPIA.candidates = [];
    toast('可引用发票候选加载失败：' + e.message, 'error');
  }
}

async function spaCandidateSearch() {
  await spaLoadCandidates(SPIA.candidateKeyword);
  spaRender();
}

function spaSetAmount(invoiceId, value) {
  SPIA.amounts[invoiceId] = value;
}

function spaSetRemark(invoiceId, value) {
  SPIA.remarks[invoiceId] = value;
}

async function spaCreateAllocation(invoiceId) {
  if (!SPIA.currentPaymentId) { toast('请先选择付款单', 'error'); return; }
  const amount = Number(String(SPIA.amounts[invoiceId] || '').trim());
  if (!amount || amount <= 0) { toast('请填写大于 0 的引用金额', 'error'); return; }

  try {
    await api('/api/supplier-payment-invoice-allocations', 'POST', {
      paymentId: SPIA.currentPaymentId,
      purchaseInvoiceId: Number(invoiceId),
      allocatedAmount: amount,
      remark: SPIA.remarks[invoiceId] || ''
    });
    toast('付款发票引用已登记（仅证据留痕；未执行付款、未核销、未认证）');
    SPIA.amounts[invoiceId] = '';
    SPIA.remarks[invoiceId] = '';
    SPIA.paymentSummary = await api('/api/supplier-payment-invoice-allocations/payments/'
      + SPIA.currentPaymentId + '/summary');
    await spaLoadCandidates(SPIA.candidateKeyword);
    await spaLoadPayments();
    await spaLoadList();
    spaRender();
  } catch (e) {
    toast('登记失败：' + e.message, 'error');
  }
}

function spaPaymentSummaryHtml(s) {
  return `
    <div style="display:grid;grid-template-columns:repeat(4,1fr);gap:8px;padding:8px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;font-size:13px;margin-bottom:8px">
      <div>付款单<div><b>${escapeHtml(s.paymentNo || '')}</b></div></div>
      <div>付款日期<div>${fmtDate(s.paymentDate)}</div></div>
      <div>付款单状态（快照）<div>${escapeHtml(s.paymentStatusText || '')}</div></div>
      <div>付款金额<div><b>${spaMoney(s.paymentAmount)}</b> ${escapeHtml(s.currency || '')}</div></div>
      <div>发票引用已引用<div><b>${spaMoney(s.invoiceAllocatedAmount)}</b> ${escapeHtml(s.currency || '')}
        （${s.allocationCount} 行有效 / ${s.voidedCount} 行已作废）</div></div>
      <div>发票引用未引用<div><b>${spaMoney(s.unallocatedAmount)}</b> ${escapeHtml(s.currency || '')}</div></div>
      <div>引用状态<div>${escapeHtml(s.linkageText || '')}</div></div>
      <div>付款单可用性<div>${escapeHtml(s.paymentAvailabilityText || '')}</div></div>
    </div>
    <div style="padding:6px 10px;background:#fffbeb;border:1px solid #fde68a;border-radius:8px;color:#92400e;font-size:12px;margin-bottom:8px">
      ${escapeHtml(s.separateEvidenceText || '')}
      采购订单引用（ERP-049，独立维度，仅供对照）：
      <b>${spaMoney(s.purchaseOrderAllocatedAmount)}</b> ${escapeHtml(s.currency || '')}
      / ${s.purchaseOrderAllocationCount} 行有效 —— 与上面的发票引用金额<b>分别记录、绝不相加</b>。
    </div>`;
}

function spaAllocationView() {
  const paymentId = SPIA.currentPaymentId;
  const options = SPIA.payments.map(p => `
    <option value="${p.paymentId}" ${String(paymentId) === String(p.paymentId) ? 'selected' : ''}>
      ${escapeHtml(p.paymentNo || '')} · ${escapeHtml(p.supplierName || '')} · ${spaMoney(p.paymentAmount)} ${escapeHtml(p.currency || '')}
      （发票引用已引用 ${spaMoney(p.allocatedAmount)} / 未引用 ${spaMoney(p.unallocatedAmount)}）</option>`).join('');

  const picker = `
    <div style="display:flex;gap:8px;align-items:flex-end;margin-bottom:8px">
      <div style="flex:1"><label class="ea-lb">付款单（既有、未删除；付款发票引用证据的登记对象）</label>
        <select style="width:100%" onchange="spaSelectPayment(this.value)">
          <option value="">（请选择付款单）</option>
          ${options}
        </select></div>
      <button class="btn btn-neutral btn-sm" onclick="spaBackToList()">← 返回台账</button>
    </div>`;

  if (!paymentId || !SPIA.paymentSummary) {
    return `
      <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
        <h4 style="margin:0">按付款单登记发票引用</h4>
        <button class="btn btn-neutral btn-sm" onclick="spaBackToList()">← 返回台账</button>
      </div>
      ${picker}
      <div class="text-muted">请选择一张付款单：系统会显示该付款金额的发票引用已引用 / 未引用金额，
        并只列出<b>同供应商 + 同币种</b>且<b>已登记</b>的采购发票候选（草稿 / 已作废发票会列出并标注不可引用）。</div>`;
  }

  const s = SPIA.paymentSummary;
  const candRows = SPIA.candidates.map(c => `
    <tr>
      <td>${escapeHtml(c.invoiceIdentityText || c.invoiceNumber || '')}
        <div class="text-muted">${escapeHtml(c.invoiceTypeText || '')} · ${fmtDate(c.invoiceDate)}</div></td>
      <td>${escapeHtml(c.invoiceStatusText || '')}</td>
      <td style="text-align:right">${spaMoney(c.invoiceGrossAmount)}</td>
      <td style="text-align:right">${spaMoney(c.allocatedByThisPayment)}</td>
      <td style="text-align:right">${spaMoney(c.allocatedByOtherPayments)}</td>
      <td style="text-align:right">${spaMoney(c.remainingUnallocatedAmount)}</td>
      <td style="color:${c.eligible ? '#166534' : '#b91c1c'}">${escapeHtml(c.eligibilityText || '')}</td>
      <td style="width:130px">
        <input type="number" step="0.01" min="0" style="width:100%" ${c.eligible ? '' : 'disabled'}
          value="${SPIA.amounts[c.purchaseInvoiceId] !== undefined ? SPIA.amounts[c.purchaseInvoiceId] : ''}"
          onchange="spaSetAmount(${c.purchaseInvoiceId}, this.value)"></td>
      <td style="width:150px">
        <input style="width:100%" ${c.eligible ? '' : 'disabled'} placeholder="备注（可选）"
          value="${escapeHtml(SPIA.remarks[c.purchaseInvoiceId] || '')}"
          onchange="spaSetRemark(${c.purchaseInvoiceId}, this.value)"></td>
      <td style="white-space:nowrap">
        <button class="btn btn-primary btn-sm" ${c.eligible ? '' : 'disabled'}
          onclick="spaCreateAllocation(${c.purchaseInvoiceId})">登记引用</button>
      </td>
    </tr>`).join('');

  const rowList = (s.allocations || []).map(a => `
    <tr>
      <td>${escapeHtml(a.invoiceIdentityText || a.invoiceNumber || '')}
        <div class="text-muted">${escapeHtml(a.invoiceTypeText || '')} · ${fmtDate(a.invoiceDate)}
        · ${escapeHtml(a.invoiceStatusText || '')}</div></td>
      <td style="text-align:right">${spaMoney(a.allocatedAmount)} ${escapeHtml(a.currency || '')}</td>
      <td>${spaStatusBadge(a)}</td>
      <td>${escapeHtml(a.invoiceAvailabilityText || '')}</td>
      <td>${escapeHtml(a.remark || '—')}</td>
      <td>${fmtDate(a.allocatedAt)}<div class="text-muted">${escapeHtml(a.recordedBy || '')}</div></td>
      <td style="white-space:nowrap">
        <button class="btn btn-neutral btn-sm" onclick="spaOpenDetail(${a.id})">详情</button>
        ${a.isActive ? `<button class="btn btn-neutral btn-sm" onclick="spaOpenVoid(${a.id})">作废</button>` : ''}
      </td>
    </tr>`).join('');

  return `
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <h4 style="margin:0">按付款单登记发票引用：${escapeHtml(s.paymentNo || '')}</h4>
      <button class="btn btn-neutral btn-sm" onclick="spaBackToList()">← 返回台账</button>
    </div>
    ${picker}
    ${spaPaymentSummaryHtml(s)}

    <div style="display:flex;gap:8px;align-items:flex-end;margin:8px 0">
      <div style="flex:1"><label class="ea-lb">采购发票候选关键字（发票号码 / 发票代码）</label>
        <input style="width:100%" value="${escapeHtml(SPIA.candidateKeyword)}"
          onchange="SPIA.candidateKeyword=this.value" placeholder="如 04512345"></div>
      <button class="btn btn-neutral btn-sm" onclick="spaCandidateSearch()">查询候选</button>
    </div>
    <div class="table-wrap" style="max-height:32vh;overflow:auto">
      <table>
        <thead><tr>
          <th>采购发票</th><th>发票状态</th>
          <th style="text-align:right">含税总额</th><th style="text-align:right">本付款单已引用</th>
          <th style="text-align:right">其他付款单已引用</th><th style="text-align:right">剩余未引用</th>
          <th>可否引用</th><th style="width:130px">引用金额</th><th style="width:150px">备注</th><th>操作</th>
        </tr></thead>
        <tbody>${candRows || '<tr><td colspan="10" style="text-align:center;color:#64748b;padding:12px">没有同供应商 + 同币种的已登记采购发票候选（系统不会按相似度猜测发票）</td></tr>'}</tbody>
      </table>
    </div>

    <h4 style="margin:12px 0 6px">本付款单的发票引用行（含已作废历史）</h4>
    <div class="table-wrap" style="max-height:28vh;overflow:auto">
      <table>
        <thead><tr>
          <th>采购发票</th><th style="text-align:right">引用金额</th><th>状态</th>
          <th>发票可用性</th><th>备注</th><th>登记时间 / 登记人</th><th>操作</th>
        </tr></thead>
        <tbody>${rowList || '<tr><td colspan="7" style="text-align:center;color:#64748b;padding:12px">该付款单尚未登记任何发票引用行（未引用金额不会被猜测到任何发票）</td></tr>'}</tbody>
      </table>
    </div>`;
}

/* ==================== 发票侧汇总（只读派生：发票未引用含税总额） ==================== */

async function spaOpenInvoiceSummary(purchaseInvoiceId) {
  try {
    SPIA.invoiceSummary = await api('/api/supplier-payment-invoice-allocations/invoices/'
      + purchaseInvoiceId + '/summary');
    SPIA.view = 'invoice';
    spaRender();
  } catch (e) {
    toast('发票侧汇总加载失败：' + e.message, 'error');
  }
}

function spaInvoiceView() {
  const s = SPIA.invoiceSummary;
  if (!s) return '<div class="text-muted">请选择采购发票。</div>';

  const rows = (s.allocations || []).map(a => `
    <tr>
      <td>${escapeHtml(a.paymentNo || '')}<div class="text-muted">${fmtDate(a.paymentDate)}</div></td>
      <td style="text-align:right">${spaMoney(a.allocatedAmount)} ${escapeHtml(a.currency || '')}</td>
      <td>${spaStatusBadge(a)}</td>
      <td>${escapeHtml(a.paymentAvailabilityText || '')}</td>
      <td>${fmtDate(a.allocatedAt)}<div class="text-muted">${escapeHtml(a.recordedBy || '')}</div></td>
    </tr>`).join('');

  return `
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <h4 style="margin:0">发票侧引用汇总：${escapeHtml(s.invoiceIdentityText || s.invoiceNumber || '')}</h4>
      <button class="btn btn-neutral btn-sm" onclick="spaBackToList()">← 返回台账</button>
    </div>
    <div style="display:grid;grid-template-columns:repeat(4,1fr);gap:8px;padding:8px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;font-size:13px;margin-bottom:8px">
      <div>发票类型<div>${escapeHtml(s.invoiceTypeText || '')}</div></div>
      <div>开票日期<div>${fmtDate(s.invoiceDate)}</div></div>
      <div>发票状态（快照）<div>${escapeHtml(s.invoiceStatusText || '')}</div></div>
      <div>供应商<div>${escapeHtml(s.supplierName || '')} ${escapeHtml(s.supplierCode || '')}</div></div>
      <div>含税总额<div><b>${spaMoney(s.invoiceGrossAmount)}</b> ${escapeHtml(s.currency || '')}</div></div>
      <div>付款引用已引用<div><b>${spaMoney(s.allocatedAmount)}</b> ${escapeHtml(s.currency || '')}
        （${s.allocationCount} 行有效 / ${s.voidedCount} 行已作废）</div></div>
      <div>未引用含税总额<div><b>${spaMoney(s.unallocatedAmount)}</b> ${escapeHtml(s.currency || '')}</div></div>
      <div>引用状态<div>${escapeHtml(s.linkageText || '')}</div></div>
    </div>
    <div style="padding:6px 10px;background:#eff6ff;border:1px solid #bfdbfe;border-radius:8px;color:#1e40af;font-size:12px;margin-bottom:8px">
      ${escapeHtml(s.invoiceAvailabilityText || '')}；未引用含税总额只是「尚未被付款引用证据覆盖的发票金额」，
      不是应付余额、账龄或付款依据；发票→采购订单关联（ERP-043）与付款→采购订单引用（ERP-049）是另外两个独立维度。
    </div>
    <div class="table-wrap" style="max-height:48vh;overflow:auto">
      <table>
        <thead><tr>
          <th>付款单</th><th style="text-align:right">引用金额</th><th>状态</th>
          <th>付款单可用性</th><th>登记时间 / 登记人</th>
        </tr></thead>
        <tbody>${rows || '<tr><td colspan="5" style="text-align:center;color:#64748b;padding:12px">该发票尚未被任何付款单引用（未引用部分不会被猜测到任何付款单）</td></tr>'}</tbody>
      </table>
    </div>`;
}

/* ==================== 详情 / 作废 / 返回 ==================== */

async function spaOpenDetail(id) {
  try {
    SPIA.current = await api('/api/supplier-payment-invoice-allocations/' + id);
    SPIA.view = 'detail';
    spaRender();
  } catch (e) {
    toast('详情加载失败：' + e.message, 'error');
  }
}

function spaDetailHtml() {
  const row = SPIA.current;
  if (!row) return '<div class="text-muted">请选择引用行。</div>';

  return `
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <h4 style="margin:0">付款发票引用详情 ${spaStatusBadge(row)}</h4>
      <div style="display:flex;gap:8px">
        ${row.isActive ? `<button class="btn btn-neutral btn-sm" onclick="spaOpenVoid(${row.id})">作废</button>` : ''}
        <button class="btn btn-neutral btn-sm" onclick="spaOpenInvoiceSummary(${row.purchaseInvoiceId})">发票侧汇总</button>
        <button class="btn btn-neutral btn-sm" onclick="spaBackFromDetail()">← 返回</button>
      </div>
    </div>
    <div style="display:grid;grid-template-columns:repeat(4,1fr);gap:8px;font-size:13px;margin-bottom:8px">
      <div>付款单<div><b>${escapeHtml(row.paymentNo || '')}</b></div></div>
      <div>付款日期<div>${fmtDate(row.paymentDate)}</div></div>
      <div>付款单状态（快照）<div>${escapeHtml(row.paymentStatusText || '')}</div></div>
      <div>付款金额（快照）<div>${spaMoney(row.paymentAmount)} ${escapeHtml(row.currency || '')}</div></div>
      <div>采购发票<div><b>${escapeHtml(row.invoiceIdentityText || row.invoiceNumber || '')}</b></div></div>
      <div>发票类型<div>${escapeHtml(row.invoiceTypeText || '')}</div></div>
      <div>开票日期<div>${fmtDate(row.invoiceDate)}</div></div>
      <div>发票状态（快照）<div>${escapeHtml(row.invoiceStatusText || '')}</div></div>
      <div>发票含税总额（快照）<div>${spaMoney(row.invoiceGrossAmount)} ${escapeHtml(row.currency || '')}</div></div>
      <div>供应商（快照）<div>${escapeHtml(row.supplierName || '')} ${escapeHtml(row.supplierCode || '')}</div></div>
      <div>引用金额<div><b>${spaMoney(row.allocatedAmount)}</b> ${escapeHtml(row.currency || '')}</div></div>
      <div>金额精度<div>${row.amountDecimals} 位小数（按币种口径）</div></div>
      <div>登记时间<div>${fmtDate(row.allocatedAt)}</div></div>
      <div>登记人<div>${escapeHtml(row.recordedBy || '')}</div></div>
      <div>付款单可用性<div>${escapeHtml(row.paymentAvailabilityText || '')}</div></div>
      <div>发票可用性<div>${escapeHtml(row.invoiceAvailabilityText || '')}</div></div>
      <div>作废时间<div>${row.voidedAt ? fmtDate(row.voidedAt) : '—'}</div></div>
      <div>作废原因<div>${escapeHtml(row.voidReason || '—')}</div></div>
    </div>
    <div style="padding:8px 10px;background:#eff6ff;border:1px solid #bfdbfe;border-radius:8px;color:#1e40af;font-size:12px;margin-bottom:8px">
      ${escapeHtml(row.boundaryText || '')}
    </div>
    <div style="font-size:13px">备注<div>${escapeHtml(row.remark || '—')}</div></div>`;
}

/* ==================== 作废（保留历史） ==================== */

function spaOpenVoid(id) {
  const pools = [
    SPIA.list || [],
    (SPIA.paymentSummary && SPIA.paymentSummary.allocations) || [],
    (SPIA.invoiceSummary && SPIA.invoiceSummary.allocations) || [],
    SPIA.current ? [SPIA.current] : []
  ];
  let row = null;
  for (const pool of pools) {
    const hit = pool.find(x => x.id === id);
    if (hit) { row = hit; break; }
  }
  if (!row) { toast('未找到该引用行，请刷新台账后重试', 'error'); return; }

  SPIA.current = row;
  SPIA.voidReason = '';
  SPIA.view = 'void';
  spaRender();
}

function spaVoidView() {
  const row = SPIA.current;
  if (!row) return '<div class="text-muted">请选择引用行。</div>';

  return `
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <h4 style="margin:0">作废付款发票引用行</h4>
      <button class="btn btn-neutral btn-sm" onclick="spaBackToList()">← 返回台账</button>
    </div>
    <div style="display:grid;grid-template-columns:repeat(4,1fr);gap:8px;padding:8px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;font-size:13px;margin-bottom:8px">
      <div>付款单<div><b>${escapeHtml(row.paymentNo || '')}</b></div></div>
      <div>采购发票<div><b>${escapeHtml(row.invoiceIdentityText || row.invoiceNumber || '')}</b></div></div>
      <div>引用金额<div><b>${spaMoney(row.allocatedAmount)}</b> ${escapeHtml(row.currency || '')}</div></div>
      <div>登记时间 / 登记人<div>${fmtDate(row.allocatedAt)} · ${escapeHtml(row.recordedBy || '')}</div></div>
    </div>
    <div style="padding:8px 10px;background:#fef2f2;border:1px solid #fecaca;border-radius:8px;color:#991b1b;font-size:13px">
      作废<b>保留</b>原始引用金额、付款单 / 供应商 / 采购发票快照、登记人与审计历史（不物理删除、不改派、不静默替换），
      也<b>不会</b>改写付款单与发票，更不会执行付款、核销或发票认证动作；作废后该发票可重新登记有效引用行。
      请填写作废原因（必填）。
    </div>
    <div style="margin-top:8px"><label class="ea-lb">作废原因 *</label>
      <textarea id="spia-void-reason" style="width:100%;height:64px" onchange="SPIA.voidReason=this.value"
        placeholder="如：引用金额录错 / 付款单指向的发票选错 / 重复登记">${escapeHtml(SPIA.voidReason || '')}</textarea></div>
    <div style="display:flex;justify-content:flex-end;gap:8px;margin-top:12px">
      <button class="btn btn-neutral" onclick="spaBackToList()">取消</button>
      <button class="btn btn-danger" onclick="spaConfirmVoid()">🗑 确认作废（保留历史）</button>
    </div>`;
}

async function spaConfirmVoid() {
  const row = SPIA.current;
  if (!row) return;
  const reason = String(SPIA.voidReason || '').trim();
  if (!reason) { toast('请填写作废原因', 'error'); return; }

  try {
    await api('/api/supplier-payment-invoice-allocations/' + row.id + '/void', 'POST', { reason: reason });
    toast('付款发票引用已作废（原始值、快照与登记人保留可读）');
    SPIA.current = null;
    SPIA.voidReason = '';
    await spaLoadPayments();
    await spaLoadList();
    if (SPIA.currentPaymentId) {
      try {
        SPIA.paymentSummary = await api('/api/supplier-payment-invoice-allocations/payments/'
          + SPIA.currentPaymentId + '/summary');
      } catch (e) {
        SPIA.paymentSummary = null;
      }
      await spaLoadCandidates(SPIA.candidateKeyword);
    }
    SPIA.view = SPIA.currentPaymentId ? 'allocate' : 'list';
    spaRender();
  } catch (e) {
    toast('作废失败：' + e.message, 'error');
  }
}

function spaBackFromDetail() {
  if (SPIA.currentPaymentId) { SPIA.view = 'allocate'; spaRender(); return; }
  if (SPIA.invoiceSummary) { SPIA.view = 'invoice'; spaRender(); return; }
  spaBackToList();
}

function spaBackToList() {
  SPIA.view = 'list';
  SPIA.currentPaymentId = null;
  SPIA.paymentSummary = null;
  SPIA.candidates = [];
  SPIA.invoiceSummary = null;
  SPIA.current = null;
  spaRender();
}
