/* ==================================================================================
   ====== 客户收款关联销项发票分摊证据（ERP-073）— 客户收款单 → 已登记销项发票的显式分摊册 =====
   ==================================================================================
   分摊口径：只用收款单 Id + 发票证据 Id（持久化标识符）建立关系；界面不提供「按单号 / 金额 / 日期猜对应关系」入口；
        发票 / 收款单 / 客户快照与登记人全部由服务端权威写入。
   金额口径：分摊金额大于 0（按币种精度取整）；收款单可分摊余额 = 收款金额 − 本维度已分摊，
        发票未分摊含税额 = 发票含税总额 − 本维度已分摊；两侧未分摊金额分别展示、绝不被静默核销或改派。
   证据维度分离：本册只属于「客户收款 → 客户销项发票」维度，与 ERP-053 / ERP-055 / ERP-071 绝不相加。
   边界：本册不是到账凭证 / 应收台账或余额 / 货款核销 / 客户对账单 / 收入确认 / 税务判断 / 结算确认 /
        会计凭证或记账分录；登记 / 作废都不收款、不付款、不记账、不核销、不催收或联系客户。
   ================================================================================== */

const CSICA_CURRENCIES = ['CNY', 'USD', 'EUR', 'HKD', 'GBP', 'JPY'];
const CSICA_STATUSES = [{ value: 1, label: '有效' }, { value: 2, label: '已作废' }];

function csicaNewState() {
  return {
    view: 'list',
    list: [], total: 0, page: 1, pageSize: 50,
    filters: { invoiceId: '', receiptId: '', customerId: '', status: '', currency: '', from: '', to: '', keyword: '' },
    customers: [],
    invoice: null,
    receiptSummary: null,
    voidTarget: null,
    voidReason: '',
    hint: '',
    loading: false
  };
}

let CSICA = csicaNewState();

async function openCustomerSalesInvoiceCollectionAllocationRegister(invoiceId, customerId) {
  CSICA = csicaNewState();

  const modal = document.getElementById('modal');
  modal.innerHTML = '<div style="padding:40px;text-align:center;color:#64748b">正在加载…</div>';
  modal.style.display = 'block';

  try {
    const res = await api('/api/base/customers?page=1&pageSize=500');
    CSICA.customers = (res && res.items) ? res.items : (Array.isArray(res) ? res : []);
  } catch (e) {
    toast('客户列表加载失败：' + e.message, 'error');
  }

  if (invoiceId) {
    CSICA.hint = '从客户销项发票进入：本工作台只登记「收款单 → 本发票」的分摊证据。';
    await csicaOpenInvoice(invoiceId);
    return;
  }

  if (customerId) {
    CSICA.filters.customerId = String(customerId);
    CSICA.hint = '从客户列表进入：已按该客户预筛选；系统不会按单号或金额猜对应关系。';
  }

  await csicaLoadList();
  csicaRender();
}

async function openCustomerSalesInvoiceCollectionAllocationForCustomer(customerId) {
  await openCustomerSalesInvoiceCollectionAllocationRegister(null, customerId);
}

function csicaRender() {
  const modal = document.getElementById('modal');
  if (CSICA.view === 'void') { modal.innerHTML = csicaVoidView(); return; }
  if (CSICA.view === 'invoice') { csicaRenderInvoice(); return; }

  const rows = (CSICA.list || []).map(r => `
    <tr>
      <td>${escapeHtml(r.identityText || '')}</td>
      <td>${escapeHtml(r.receiptNo || '')}</td>
      <td>${escapeHtml(r.customerName || '')}（${escapeHtml(r.customerCode || '')}）</td>
      <td class="text-right">${escapeHtml(r.allocatedAmountText || '')}</td>
      <td>${escapeHtml(r.statusText || '')}</td>
      <td>${escapeHtml(fmtDate(r.allocatedAt) || '')}</td>
      <td>${r.isActive ? `<button class="btn btn-danger btn-sm" onclick="csicaOpenVoid(${r.id},'list')">作废</button>` : ''}</td>
    </tr>`).join('');

  modal.innerHTML = `
    <div class="modal-content" style="max-width:1280px">
      <div class="modal-header">
        <h3>客户收款关联销项发票分摊证据（ERP-073）</h3>
        <button class="modal-close" onclick="closeModal()">✕</button>
      </div>
      ${CSICA.hint ? `<div style="padding:6px 10px;background:#eff6ff;color:#1e40af;font-size:12px;border-radius:6px;margin:8px">${escapeHtml(CSICA.hint)}</div>` : ''}
      <div class="modal-body" style="padding:12px 16px">
        <div style="display:flex;gap:8px;flex-wrap:wrap;margin-bottom:10px">
          <input placeholder="发票号码 / 收款单号 / 客户 / 备注" style="flex:1;min-width:180px"
            value="${escapeHtml(CSICA.filters.keyword)}" onchange="CSICA.filters.keyword=this.value">
          <select onchange="CSICA.filters.status=this.value;csicaReload()">
            <option value="">全部状态</option>
            ${CSICA_STATUSES.map(s => `<option value="${s.value}" ${String(CSICA.filters.status) === String(s.value) ? 'selected' : ''}>${s.label}</option>`).join('')}
          </select>
          <button class="btn btn-primary" onclick="csicaApplyFilters()">查询</button>
        </div>
        <table class="data-table">
          <thead><tr><th>发票 ← 收款单</th><th>收款单号</th><th>客户</th><th>分摊金额</th><th>状态</th><th>登记时间</th><th>操作</th></tr></thead>
          <tbody>${rows || `<tr><td colspan="7" style="text-align:center;color:#64748b">暂无分摊证据</td></tr>`}</tbody>
        </table>
      </div>
    </div>`;
}

async function csicaApplyFilters() { CSICA.page = 1; await csicaLoadList(); csicaRender(); }
async function csicaReload() { await csicaApplyFilters(); }

async function csicaLoadList() {
  const f = CSICA.filters;
  const q = new URLSearchParams();
  q.set('page', CSICA.page);
  q.set('pageSize', CSICA.pageSize);
  if (f.invoiceId) q.set('customerSalesInvoiceEvidenceId', f.invoiceId);
  if (f.receiptId) q.set('receiptId', f.receiptId);
  if (f.customerId) q.set('customerId', f.customerId);
  if (f.status) q.set('status', f.status);
  if (f.currency) q.set('currency', f.currency);
  if (f.keyword) q.set('keyword', f.keyword);

  try {
    const res = await api('/api/customer-sales-invoice-collection-allocations?' + q.toString());
    CSICA.list = res.items || [];
    CSICA.total = res.total || 0;
  } catch (e) {
    toast('分摊台账加载失败：' + e.message, 'error');
  }
}

async function csicaOpenInvoice(invoiceId) {
  try {
    const summary = await api('/api/customer-sales-invoice-collection-allocations/invoices/' + invoiceId + '/summary');
    CSICA.invoice = summary;
    CSICA.view = 'invoice';
    csicaRenderInvoice();
  } catch (e) {
    toast('发票分摊工作台加载失败：' + e.message, 'error');
  }
}

function csicaRenderInvoice() {
  const s = CSICA.invoice || {};
  const rows = (s.allocations || []).map(a => `
    <tr>
      <td>${escapeHtml(a.receiptNo || '')}</td>
      <td class="text-right">${escapeHtml(a.allocatedAmountText || '')}</td>
      <td>${escapeHtml(a.statusText || '')}</td>
      <td>${escapeHtml(fmtDate(a.allocatedAt) || '')}</td>
      <td>${a.isActive ? `<button class="btn btn-danger btn-sm" onclick="csicaOpenVoid(${a.id},'invoice')">作废</button>` : ''}</td>
    </tr>`).join('');

  const modal = document.getElementById('modal');
  modal.innerHTML = `
    <div class="modal-content" style="max-width:1280px">
      <div class="modal-header">
        <h3>发票收款分摊 · ${escapeHtml(s.identityText || '')}</h3>
        <button class="modal-close" onclick="closeModal()">✕</button>
      </div>
      <div class="modal-body" style="padding:12px 16px">
        <table class="data-table" style="margin-bottom:10px">
          <tbody>
            <tr><th style="width:180px">发票身份</th><td>${escapeHtml(s.identityText || '')}
              <span class="text-muted">（${escapeHtml(s.invoiceStatusText || '')}）</span></td></tr>
            <tr><th>客户 / 币种</th><td>${escapeHtml(s.customerCode || '')} ${escapeHtml(s.customerName || '')} ·
              <b>${escapeHtml(s.currency || '')}</b>（小数位 ${Number(s.amountDecimals || 0)}）</td></tr>
            <tr><th>开票日期</th><td>${escapeHtml(fmtDate(s.invoiceDate) || '')}</td></tr>
            <tr><th>发票含税总额</th><td><b>${escapeHtml(String(s.invoiceGrossAmount))} ${escapeHtml(s.currency || '')}</b></td></tr>
            <tr><th>本维度已分摊 / 未分摊</th><td><b>${escapeHtml(String(s.allocatedAmount))}</b> /
              <b>${escapeHtml(String(s.unallocatedAmount))}</b> ${escapeHtml(s.currency || '')}</td></tr>
            <tr><th>关联状态</th><td>${escapeHtml(s.linkageText || '')}</td></tr>
          </tbody>
        </table>

        <h5 style="margin:6px 0">登记收款分摊</h5>
        <div style="display:grid;grid-template-columns:1fr 1fr 1fr auto;gap:8px;align-items:end;margin-bottom:10px">
          <div>
            <label class="ea-lb">收款单 *</label>
            <select id="csica-receipt" onchange="csicaLoadReceiptCandidates()">
              <option value="">请选择收款单（只列未删除且未取消）</option>
            </select>
          </div>
          <div><label class="ea-lb">分摊金额 *</label>
            <input id="csica-amount" style="width:100%" placeholder="原币金额"></div>
          <div><label class="ea-lb">备注</label>
            <input id="csica-remark" style="width:100%" placeholder="可选"></div>
          <button class="btn btn-primary" onclick="csicaCreate()">登记分摊</button>
        </div>
        <div class="text-muted" style="font-size:12px;margin-bottom:10px">
          分摊金额按币种精度取整后必须大于 0，且不得超过收款单可分摊余额与发票未分摊含税额；
          同一「发票 + 收款单」只能有一条有效分摊行；两侧未分摊金额分别展示、不会被静默核销或改派。
        </div>

        <h5 style="margin:6px 0">本发票分摊明细</h5>
        <table class="data-table">
          <thead><tr><th>收款单号</th><th>分摊金额</th><th>状态</th><th>登记时间</th><th>操作</th></tr></thead>
          <tbody>${rows || `<tr><td colspan="5" style="text-align:center;color:#64748b">暂无分摊明细</td></tr>`}</tbody>
        </table>
        <button class="btn btn-neutral btn-sm" onclick="csicaBackToList()">← 返回台账</button>
      </div>
    </div>`;
}

async function csicaLoadReceiptCandidates() {
  const invoice = CSICA.invoice;
  if (!invoice || !invoice.customerId || !invoice.currency) return;
  try {
    const options = await api('/api/customer-sales-invoice-collection-allocations/receipts?'
      + 'customerId=' + invoice.customerId + '&currency=' + invoice.currency);
    const select = document.getElementById('csica-receipt');
    if (!select) return;
    select.innerHTML = '<option value="">请选择收款单（只列未删除且未取消）</option>' + options.map(o =>
      `<option value="${o.receiptId}">${escapeHtml(o.receiptNo || '')} · 剩余 ${o.unallocatedAmount} ${escapeHtml(o.currency || '')}</option>`
    ).join('');
  } catch (e) {
    toast('收款单候选加载失败：' + e.message, 'error');
  }
}

async function csicaCreate() {
  const s = CSICA.invoice;
  if (!s) { toast('请先打开发票分摊工作台', 'error'); return; }
  const receiptId = Number((document.getElementById('csica-receipt') || {}).value || 0);
  const amountText = ((document.getElementById('csica-amount') || {}).value || '').trim();
  const remark = ((document.getElementById('csica-remark') || {}).value || '').trim();
  if (!receiptId) { toast('请显式选择要分摊的客户收款单（系统不按单号或金额猜对应关系）', 'error'); return; }
  const amount = Number(amountText);
  if (amountText === '' || Number.isNaN(amount) || amount <= 0) {
    toast('请填写大于 0 的分摊金额（币种精度由服务端按权威口径取整）', 'error');
    return;
  }

  try {
    await api('/api/customer-sales-invoice-collection-allocations', 'POST', {
      customerSalesInvoiceEvidenceId: Number(s.customerSalesInvoiceEvidenceId),
      receiptId: receiptId,
      allocatedAmount: amount,
      remark: remark || ''
    });
    toast('收款分摊证据已登记（仅证据留痕；未收款、未记账、未核销、未结算）');
  } catch (e) {
    toast('分摊失败：' + e.message, 'error');
    return;
  }

  await csicaOpenInvoice(s.customerSalesInvoiceEvidenceId);
}

function csicaBackToList() {
  CSICA.view = 'list';
  csicaLoadList().then(csicaRender);
}

async function csicaOpenVoid(allocationId, backTo) {
  let row = null;
  if (backTo === 'invoice' && CSICA.invoice) {
    row = (CSICA.invoice.allocations || []).find(x => Number(x.id) === Number(allocationId)) || null;
  } else if (backTo === 'receipt' && CSICA.receiptSummary) {
    row = (CSICA.receiptSummary.allocations || []).find(x => Number(x.id) === Number(allocationId)) || null;
  } else {
    row = (CSICA.list || []).find(x => Number(x.id) === Number(allocationId)) || null;
  }
  if (!row) {
    try {
      row = await api('/api/customer-sales-invoice-collection-allocations/' + allocationId);
    } catch (e) {
      toast('分摊行详情加载失败：' + e.message, 'error');
      return;
    }
  }
  CSICA.voidTarget = row;
  CSICA.voidReason = '';
  CSICA.view = 'void';
  csicaRender();
}

function csicaVoidView() {
  const row = CSICA.voidTarget || {};
  const modal = document.getElementById('modal');
  return `
    <div class="modal-content" style="max-width:640px">
      <div class="modal-header">
        <h3>作废收款分摊证据</h3>
        <button class="modal-close" onclick="closeModal()">✕</button>
      </div>
      <div class="modal-body" style="padding:12px 16px">
        <div style="padding:8px;background:#fef2f2;border:1px solid #fecaca;border-radius:8px;color:#991b1b;font-size:12px">
          作废保留原始分摊金额、发票 / 收款单 / 客户快照、登记人与时间戳（历史可读），
          不会删除记录、不会改派到别的收款单或发票、不会改写原始金额；
          作废后同一「发票 + 收款单」可以重新登记一条新的有效分摊行（新旧并存可查）。
        </div>
        <table class="data-table" style="margin-top:8px">
          <tbody>
            <tr><th style="width:180px">分摊目标</th><td>${escapeHtml(row.identityText || '')}</td></tr>
            <tr><th>客户 / 币种</th><td>${escapeHtml(row.customerCode || '')} ${escapeHtml(row.customerName || '')} ·
              ${escapeHtml(row.currency || '')}</td></tr>
            <tr><th>原始分摊金额</th><td><b>${escapeHtml(row.allocatedAmountText || '')}</b></td></tr>
            <tr><th>登记时间 / 登记人</th><td>${escapeHtml(fmtDate(row.allocatedAt) || '')} ·
              ${escapeHtml(row.allocatedBy || '')}</td></tr>
          </tbody>
        </table>
        <div style="margin-top:8px"><label class="ea-lb">作废原因 *</label>
          <input id="csica-void-reason" style="width:100%" value="${escapeHtml(CSICA.voidReason)}"
            onchange="CSICA.voidReason=this.value" placeholder="例如：分摊口径更正 / 登记错误 / 收款单选择错误"></div>
        <div style="display:flex;gap:6px;margin-top:10px">
          <button class="btn btn-danger" onclick="csicaConfirmVoid(${Number(row.id)})">确认作废</button>
          <button class="btn btn-neutral" onclick="csicaBackToList()">取消</button>
        </div>
      </div>
    </div>`;
}

async function csicaConfirmVoid(allocationId) {
  const reason = (document.getElementById('csica-void-reason') || {}).value || CSICA.voidReason || '';
  if (!reason.trim()) { toast('请填写作废原因（作废会保留原始分摊证据）', 'error'); return; }

  try {
    await api('/api/customer-sales-invoice-collection-allocations/' + allocationId + '/void', 'POST',
      { reason: reason.trim() });
    toast('收款分摊证据已作废（原始金额与历史保留，可读）');
  } catch (e) {
    toast('作废失败：' + e.message, 'error');
    return;
  }

  await csicaBackToList();
}


