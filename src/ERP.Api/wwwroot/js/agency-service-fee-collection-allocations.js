/* ==================================================================================
   ====== 代理服务费收款分摊证据（ERP-071）— 客户收款单 → 已登记对账单的显式分摊册 =====
   ==================================================================================
   定位：把**既有、未删除且未取消**的客户收款单（FinanceReceipt）的一部分（或全部）金额
        **显式分摊**到一条**已登记**的代理服务费对账单证据（ERP-070）；一行 = 一张收款单 → 一条对账单 + 一个分摊金额。
   分摊口径：只用**收款单 Id + 对账单 Id（持久化标识符）**建立关系；界面**不**提供「按单号 / 金额 / 日期猜对应关系」的入口；
        对账单号 / 日期 / 状态 / 客户 / 币种 / 金额快照与登记人全部由服务端权威写入，界面不做前端计算。
   金额口径：分摊金额必须大于 0（按币种精度取整）；收款单可分摊余额 = 收款金额 − **本维度**已分摊，
        对账单未分摊额 = 对账单服务端合计 − **本维度**已分摊；两侧未分摊金额分别展示、**绝不**被静默核销或改派，
        也不代表已付 / 已结清 / 逾期 / 收入确认 / 记账状态或应收余额。
   证据维度分离：本册只属于「客户收款 → 代理服务费对账单」维度，与 ERP-053「收款单 → 销售订单」引用、
        ERP-055「销项发票 → 销售订单」分摊**绝不相加**。
   边界（界面侧同样遵守）：本册**不是**银行入账 / 到账凭证、**不是**应收账款台账或余额、**不是**货款核销、
        **不是**客户对账单、**不是**收入确认、**不是**税务判断、**不是**结算确认，也**不是**会计凭证或记账分录；
        登记 / 作废都不收款、不付款、不记账、不核销、不催收或联系客户，也不改写收款单与对账单的任何字段。
   文案与服务端 AgencyServiceFeeCollectionAllocationRules / AgencyServiceFeeCollectionAllocationService 保持一致。
   ================================================================================== */

const ASFCA_CURRENCIES = ['CNY', 'USD', 'EUR', 'HKD', 'GBP', 'JPY'];
const ASFCA_STATUSES = [{ value: 1, label: '有效' }, { value: 2, label: '已作废' }];

function asfcaNewState() {
  return {
    view: 'list',
    list: [], total: 0, page: 1, pageSize: 50,
    filters: { statementId: '', receiptId: '', customerId: '', status: '', currency: '', from: '', to: '', keyword: '' },
    customers: [],
    statement: null,
    receiptCandidates: [],
    selectedReceiptId: '',
    amount: '',
    remark: '',
    receiptSummary: null,
    voidTarget: null,
    voidReason: '',
    hint: '',
    loading: false
  };
}

let ASFCA = asfcaNewState();

/* 打开收款分摊登记册：
   - 从对账单工作流传入 statementId：直接进入该对账单的分摊工作台（客户 / 币种已由对账单确定）；
   - 从客户列表行操作传入 customerId：按该客户预筛选台账。 */
async function openAgencyServiceFeeCollectionAllocationRegister(statementId, customerId) {
  ASFCA = asfcaNewState();

  const modal = document.getElementById('modal');
  modal.innerHTML = '<div style="padding:40px;text-align:center;color:#64748b">正在加载…</div>';
  modal.style.display = 'block';

  try {
    const res = await api('/api/base/customers?page=1&pageSize=500');
    ASFCA.customers = (res && res.items) ? res.items : (Array.isArray(res) ? res : []);
  } catch (e) {
    toast('客户列表加载失败：' + e.message, 'error');
  }

  if (statementId) {
    ASFCA.hint = '从代理服务费对账单进入：本工作台只登记「收款单 → 本对账单」的分摊证据，'
      + '不改写对账单与收款单，也不执行收款 / 记账 / 核销 / 结算。';
    await asfcaOpenStatement(statementId, true);
    return;
  }

  if (customerId) {
    ASFCA.filters.customerId = String(customerId);
    ASFCA.hint = '从客户列表进入：已按该客户预筛选；分摊只按持久化标识符建立，系统不会按单号或金额猜对应关系。';
  }

  await asfcaLoadList();
  asfcaRender();
}

/* 客户行操作入口（crud.js 的行操作会把行 Id = 客户 Id 作为第一个参数传入）：
   以该客户预筛选分摊台账，并提示必须显式选择对账单与收款单。 */
async function openAgencyServiceFeeCollectionAllocationForCustomer(customerId) {
  await openAgencyServiceFeeCollectionAllocationRegister(null, customerId);
}

function asfcaRender() {
  const modal = document.getElementById('modal');
  modal.innerHTML = `
    <div class="modal-content" style="max-width:1280px">
      <div class="modal-header">
        <h3>代理服务费收款分摊证据（ERP-071）</h3>
        <button class="modal-close" onclick="closeModal()">✕</button>
      </div>
      ${ASFCA.hint ? `<div style="padding:6px 10px;background:#eff6ff;border:1px solid #bfdbfe;border-radius:8px;color:#1e40af;font-size:12px;margin-bottom:8px">${escapeHtml(ASFCA.hint)}</div>` : ''}
      <div style="padding:6px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;color:#334155;font-size:12px;margin-bottom:8px">
        本册是「客户收款 → 代理服务费对账单」的<b>显式分摊证据</b>：<b>不是到账凭证 / 不是应收台账或余额 / 不是核销 /
        不是客户对账单 / 不是收入确认 / 不是税务判断 / 不是结算确认 / 不是记账分录</b>；
        一行只用<b>收款单 Id + 对账单 Id（持久化标识符）</b>建立关系；分摊金额不得超两侧可用金额；
        两侧未分摊金额分别展示、<b>不会被静默核销或改派</b>；更正走<b>显式作废（原因必填）</b>，原始金额与历史保留可读。
      </div>
      ${ASFCA.view === 'list' ? asfcaListView() : ''}
      ${ASFCA.view === 'statement' ? asfcaStatementView() : ''}
      ${ASFCA.view === 'receipt' ? asfcaReceiptView() : ''}
      ${ASFCA.view === 'void' ? asfcaVoidView() : ''}
    </div>`;
  modal.style.display = 'block';
}

function asfcaStatusBadge(row) {
  if (row.isActive) return '<span class="status status-success">有效</span>';
  if (row.isVoided) {
    return '<span class="status status-neutral">已作废</span>'
      + `<div class="text-muted">${escapeHtml(row.voidReason || '')}</div>`;
  }
  return `<span class="text-muted">${escapeHtml(row.statusText || '')}</span>`;
}

/* ==================== 台账视图 ==================== */

function asfcaListView() {
  const f = ASFCA.filters;
  const customerOptions = ASFCA.customers.map(c =>
    `<option value="${c.id}" ${String(c.id) === String(f.customerId) ? 'selected' : ''}>`
    + `${escapeHtml(c.customerCode || '')} ${escapeHtml(c.customerName || '')}</option>`).join('');

  return `
    <div style="display:flex;gap:6px;flex-wrap:wrap;align-items:end;margin-bottom:8px">
      <div><label class="ea-lb">客户</label>
        <select id="asfca-f-customer" style="min-width:180px">
          <option value="">全部客户</option>${customerOptions}
        </select></div>
      <div><label class="ea-lb">状态</label>
        <select id="asfca-f-status">
          <option value="">全部（含已作废历史）</option>
          ${ASFCA_STATUSES.map(s => `<option value="${s.value}" ${String(s.value) === String(f.status) ? 'selected' : ''}>${s.label}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">币种</label>
        <select id="asfca-f-currency">
          <option value="">全部币种</option>
          ${ASFCA_CURRENCIES.map(c => `<option value="${c}" ${f.currency === c ? 'selected' : ''}>${c}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">对账单 Id</label>
        <input id="asfca-f-statement" style="width:100px" value="${escapeHtml(f.statementId)}" placeholder="持久化 Id"></div>
      <div><label class="ea-lb">收款单 Id</label>
        <input id="asfca-f-receipt" style="width:100px" value="${escapeHtml(f.receiptId)}" placeholder="持久化 Id"></div>
      <div><label class="ea-lb">登记日期从</label>
        <input type="date" id="asfca-f-from" value="${escapeHtml(f.from)}"></div>
      <div><label class="ea-lb">到</label>
        <input type="date" id="asfca-f-to" value="${escapeHtml(f.to)}"></div>
      <div><label class="ea-lb">关键字</label>
        <input id="asfca-f-keyword" style="min-width:150px" value="${escapeHtml(f.keyword)}"
          placeholder="对账单号 / 收款单号 / 客户 / 协议号 / 备注"></div>
      <button class="btn btn-primary btn-sm" onclick="asfcaSearch()">🔍 查询</button>
      <button class="btn btn-neutral btn-sm" onclick="closeModal()">关闭</button>
    </div>

    <table class="data-table">
      <thead><tr>
        <th>#</th><th>对账单（状态）</th><th>对账日期</th><th>收款单（状态）</th><th>收款日期</th>
        <th>客户</th><th>分摊金额</th><th>登记时间 / 登记人</th><th>状态</th><th>操作</th>
      </tr></thead>
      <tbody>
        ${ASFCA.list.length === 0 ? '<tr><td colspan="10" class="text-muted">没有符合条件的分摊记录（默认包含已作废历史）。</td></tr>' : ''}
        ${ASFCA.list.map(r => `
          <tr>
            <td>${Number(r.id)}</td>
            <td>${escapeHtml(r.statementNo || '')}<div class="text-muted">${escapeHtml(r.statementStatusText || '')}</div></td>
            <td>${escapeHtml(fmtDate(r.statementDate) || '')}</td>
            <td>${escapeHtml(r.receiptNo || '')}<div class="text-muted">${escapeHtml(r.receiptStatusText || '')}</div></td>
            <td>${escapeHtml(fmtDate(r.receiptDate) || '')}</td>
            <td>${escapeHtml(r.customerCode || '')} ${escapeHtml(r.customerName || '')}</td>
            <td><b>${escapeHtml(r.allocatedAmountText || '')}</b>
              <div class="text-muted">对账单合计 ${escapeHtml(r.statementAmountText || '')}</div></td>
            <td>${escapeHtml(fmtDate(r.allocatedAt) || '')}<div class="text-muted">${escapeHtml(r.allocatedBy || '')}</div></td>
            <td>${asfcaStatusBadge(r)}</td>
            <td>
              <button class="btn btn-neutral btn-sm" onclick="asfcaOpenStatement(${Number(r.statementId)})">对账单视角</button>
              <button class="btn btn-neutral btn-sm" onclick="asfcaOpenReceipt(${Number(r.receiptId)})">收款单视角</button>
              ${r.isVoided ? '' : `<button class="btn btn-danger btn-sm" onclick="asfcaOpenVoid(${Number(r.id)}, 'list')">作废</button>`}
            </td>
          </tr>`).join('')}
      </tbody>
    </table>

    <div style="display:flex;justify-content:space-between;align-items:center;margin-top:8px">
      <div class="text-muted">共 ${ASFCA.total} 条 · 第 ${ASFCA.page} 页 · 每页 ${ASFCA.pageSize} 条（有界分页；
        已作废行保留可读、单独标注，永不并入有效合计）</div>
      <div>
        <button class="btn btn-neutral btn-sm" onclick="asfcaPage(-1)">← 上一页</button>
        <button class="btn btn-neutral btn-sm" onclick="asfcaPage(1)">下一页 →</button>
      </div>
    </div>`;
}

async function asfcaLoadList() {
  const f = ASFCA.filters;
  const qs = new URLSearchParams();
  qs.set('page', ASFCA.page);
  qs.set('pageSize', ASFCA.pageSize);
  if (f.customerId) qs.set('customerId', f.customerId);
  if (f.statementId) qs.set('statementId', f.statementId);
  if (f.receiptId) qs.set('receiptId', f.receiptId);
  if (f.status !== '') qs.set('status', f.status);
  if (f.currency) qs.set('currency', f.currency);
  if (f.from) qs.set('allocatedDateFrom', f.from);
  if (f.to) qs.set('allocatedDateTo', f.to);
  if (f.keyword) qs.set('keyword', f.keyword);

  try {
    const data = await api('/api/agency-service-fee-collection-allocations?' + qs.toString());
    ASFCA.list = (data && data.items) || [];
    ASFCA.total = (data && data.total) || 0;
  } catch (e) {
    ASFCA.list = [];
    ASFCA.total = 0;
    toast('收款分摊台账加载失败：' + e.message, 'error');
  }
}

async function asfcaSearch() {
  ASFCA.filters.customerId = (document.getElementById('asfca-f-customer') || {}).value || '';
  ASFCA.filters.status = (document.getElementById('asfca-f-status') || {}).value || '';
  ASFCA.filters.currency = (document.getElementById('asfca-f-currency') || {}).value || '';
  ASFCA.filters.statementId = (document.getElementById('asfca-f-statement') || {}).value || '';
  ASFCA.filters.receiptId = (document.getElementById('asfca-f-receipt') || {}).value || '';
  ASFCA.filters.from = (document.getElementById('asfca-f-from') || {}).value || '';
  ASFCA.filters.to = (document.getElementById('asfca-f-to') || {}).value || '';
  ASFCA.filters.keyword = (document.getElementById('asfca-f-keyword') || {}).value || '';
  ASFCA.page = 1;
  await asfcaLoadList();
  asfcaRender();
}

async function asfcaPage(delta) {
  const next = ASFCA.page + delta;
  if (next < 1) return;
  ASFCA.page = next;
  await asfcaLoadList();
  asfcaRender();
}

async function asfcaBackToList() {
  ASFCA.view = 'list';
  ASFCA.statement = null;
  ASFCA.receiptSummary = null;
  ASFCA.receiptCandidates = [];
  ASFCA.selectedReceiptId = '';
  ASFCA.amount = '';
  ASFCA.remark = '';
  ASFCA.voidTarget = null;
  ASFCA.voidReason = '';
  await asfcaLoadList();
  asfcaRender();
}

/* ==================== 对账单视角（分摊工作台） ==================== */

async function asfcaOpenStatement(statementId, keepHint) {
  try {
    ASFCA.statement = await api(
      '/api/agency-service-fee-collection-allocations/statements/' + statementId + '/summary');
  } catch (e) {
    toast('对账单侧分摊汇总加载失败：' + e.message, 'error');
    return;
  }
  ASFCA.hint = keepHint ? ASFCA.hint : '';
  ASFCA.view = 'statement';
  ASFCA.selectedReceiptId = '';
  ASFCA.amount = '';
  ASFCA.remark = '';
  await asfcaLoadReceiptCandidates();
  asfcaRender();
}

/* 载入可分摊收款单候选（有界；必须显式给出客户与币种，系统不按相似度推荐收款单） */
async function asfcaLoadReceiptCandidates() {
  ASFCA.receiptCandidates = [];
  const s = ASFCA.statement;
  if (!s || !s.customerId || !s.currency) return;
  try {
    const data = await api('/api/agency-service-fee-collection-allocations/receipts?customerId='
      + encodeURIComponent(s.customerId) + '&currency=' + encodeURIComponent(s.currency) + '&take=200');
    ASFCA.receiptCandidates = Array.isArray(data) ? data : [];
  } catch (e) {
    toast('可分摊收款单候选加载失败：' + e.message, 'error');
  }
}

function asfcaStatementView() {
  const s = ASFCA.statement || {};
  const rows = s.allocations || [];
  const candidates = ASFCA.receiptCandidates || [];
  const candidateOptions = candidates.map(c =>
    `<option value="${c.receiptId}" ${Number(c.receiptId) === Number(ASFCA.selectedReceiptId) ? 'selected' : ''}
      ${c.eligible ? '' : 'disabled'}>`
    + `${escapeHtml(c.receiptNo || '')} · ${escapeHtml(fmtDate(c.receiptDate) || '')} · 收款 `
    + `${escapeHtml(String(c.receiptAmount))} ${escapeHtml(c.currency || '')} · 本维度可分摊 `
    + `${escapeHtml(String(c.unallocatedAmount))}${c.eligible ? '' : ' · ' + escapeHtml(c.eligibilityText || '不可分摊')}`
    + `</option>`).join('');

  return `
    <div style="display:flex;gap:6px;align-items:center;margin-bottom:6px">
      <button class="btn btn-neutral btn-sm" onclick="asfcaBackToList()">← 返回台账</button>
      <span class="text-muted">当前工作台只服务这一条对账单证据；登记只写本分摊册，不改写对账单与收款单。</span>
    </div>

    <table class="data-table">
      <tbody>
        <tr><th style="width:180px">对账单</th><td>${escapeHtml(s.identityText || '')}
          <span class="text-muted">（${escapeHtml(s.statementStatusText || '')}）</span></td></tr>
        <tr><th>客户 / 币种</th><td>${escapeHtml(s.customerCode || '')} ${escapeHtml(s.customerName || '')} ·
          <b>${escapeHtml(s.currency || '')}</b>（小数位 ${Number(s.amountDecimals || 0)}）</td></tr>
        <tr><th>对账日期</th><td>${escapeHtml(fmtDate(s.statementDate) || '')}</td></tr>
        <tr><th>对账单服务端合计</th><td><b>${escapeHtml(String(s.statementTotalAmount))} ${escapeHtml(s.currency || '')}</b>
          <div class="text-muted">合计由 ERP-070 服务端按已校验行计算，本册只读它、绝不改写。</div></td></tr>
        <tr><th>本维度已分摊 / 未分摊</th><td>
          已分摊 <b>${escapeHtml(String(s.allocatedAmount))} ${escapeHtml(s.currency || '')}</b> ·
          未分摊 <b>${escapeHtml(String(s.unallocatedAmount))} ${escapeHtml(s.currency || '')}</b>
          <div class="text-muted">${escapeHtml(s.linkageText || '')}</div></td></tr>
        <tr><th>有效行 / 已作废行</th><td>${Number(s.allocationCount || 0)} / ${Number(s.voidedCount || 0)}
          <div class="text-muted">已作废历史永不并入有效合计，只单独计数与列出。</div></td></tr>
        <tr><th>可用性</th><td>${s.statementAvailable ? '可继续分摊' : '不可继续分摊'}
          <div class="text-muted">${escapeHtml(s.statementAvailabilityText || '')}</div></td></tr>
      </tbody>
    </table>

    <div style="margin-top:10px;padding:8px;background:#fff7ed;border:1px solid #fed7aa;border-radius:8px;font-size:12px;color:#9a3412">
      显式分摊：只能用<b>收款单 Id（持久化记录）</b>把既有、未删除、未取消的收款单的<b>一部分或全部</b>金额分摊到本对账单；
      系统<b>不</b>按单号文本、金额或日期相似度猜对应关系；金额不得超<b>收款单可分摊余额</b>与<b>对账单未分摊额</b>；
      客户与币种必须一致（不做汇率换算、不跨币种合并）。
    </div>

    <div style="display:flex;gap:6px;align-items:end;flex-wrap:wrap;margin-top:8px">
      <div><label class="ea-lb">收款单 *（持久化记录；客户与币种须一致）</label>
        <select id="asfca-receipt" style="min-width:420px" onchange="ASFCA.selectedReceiptId=this.value">
          <option value="">请显式选择可分摊收款单…</option>${candidateOptions}
        </select>
        <div class="text-muted">${candidates.length === 0
          ? '当前客户与币种下没有未删除、未取消的收款单候选（系统不会替你新建或猜测收款单）。'
          : '候选按客户 + 币种 + 未删除 + 未取消过滤；已占满 / 已取消的收款单被禁用并给出原因。'}</div></div>
      <div><label class="ea-lb">分摊金额 *（原币）</label>
        <input type="number" step="0.01" id="asfca-amount" style="width:130px"
          value="${escapeHtml(ASFCA.amount)}" onchange="ASFCA.amount=this.value" placeholder="显式金额"></div>
      <div><label class="ea-lb">备注</label>
        <input id="asfca-remark" style="width:200px" value="${escapeHtml(ASFCA.remark)}"
          onchange="ASFCA.remark=this.value"></div>
      <button class="btn btn-primary" onclick="asfcaCreate()">💾 登记分摊</button>
    </div>

    <div style="margin-top:10px"><b>收款分摊行（有效 ${Number(s.allocationCount || 0)} 行 · 已作废 ${Number(s.voidedCount || 0)} 行）</b></div>
    <table class="data-table" style="margin-top:4px">
      <thead><tr>
        <th>#</th><th>收款单（状态）</th><th>收款日期</th><th>收款金额</th><th>客户</th>
        <th>分摊金额</th><th>登记时间 / 登记人</th><th>状态</th><th>操作</th>
      </tr></thead>
      <tbody>
        ${rows.length === 0 ? '<tr><td colspan="9" class="text-muted">本对账单尚无收款分摊证据（缺失即「无」，不表示未付 / 已付 / 已结清 / 逾期或已记账）。</td></tr>' : ''}
        ${rows.map(r => `
          <tr>
            <td>${Number(r.id)}</td>
            <td>${escapeHtml(r.receiptNo || '')}<div class="text-muted">${escapeHtml(r.receiptStatusText || '')}</div></td>
            <td>${escapeHtml(fmtDate(r.receiptDate) || '')}</td>
            <td>${escapeHtml(r.receiptAmountText || '')}</td>
            <td>${escapeHtml(r.customerCode || '')} ${escapeHtml(r.customerName || '')}</td>
            <td><b>${escapeHtml(r.allocatedAmountText || '')}</b>
              ${r.remark ? `<div class="text-muted">${escapeHtml(r.remark)}</div>` : ''}</td>
            <td>${escapeHtml(fmtDate(r.allocatedAt) || '')}<div class="text-muted">${escapeHtml(r.allocatedBy || '')}</div></td>
            <td>${asfcaStatusBadge(r)}</td>
            <td>
              <button class="btn btn-neutral btn-sm" onclick="asfcaOpenReceipt(${Number(r.receiptId)})">收款单视角</button>
              ${r.isVoided ? '' : `<button class="btn btn-danger btn-sm" onclick="asfcaOpenVoid(${Number(r.id)}, 'statement')">作废</button>`}
            </td>
          </tr>`).join('')}
      </tbody>
    </table>

    <div style="margin-top:8px;padding:8px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;font-size:12px;color:#334155">
      <div><b>分摊口径</b>：${escapeHtml(s.ruleText || '')}</div>
      <div style="margin-top:4px"><b>金额口径</b>：${escapeHtml(s.amountRuleText || '')}</div>
      <div style="margin-top:4px"><b>证据维度分离</b>：${escapeHtml(s.dimensionSeparationText || '')}</div>
      <div style="margin-top:4px"><b>模块边界</b>：${escapeHtml(s.boundaryText || '')}</div>
    </div>`;
}

/* ==================== 收款单视角 ==================== */

async function asfcaOpenReceipt(receiptId) {
  try {
    ASFCA.receiptSummary = await api(
      '/api/agency-service-fee-collection-allocations/receipts/' + receiptId + '/summary');
  } catch (e) {
    toast('收款单侧分摊汇总加载失败：' + e.message, 'error');
    return;
  }
  ASFCA.view = 'receipt';
  asfcaRender();
}

function asfcaReceiptView() {
  const r = ASFCA.receiptSummary || {};
  const rows = r.allocations || [];

  return `
    <div style="display:flex;gap:6px;align-items:center;margin-bottom:6px">
      <button class="btn btn-neutral btn-sm" onclick="asfcaBackToList()">← 返回台账</button>
      <span class="text-muted">收款单视角只做只读派生：不改写收款单，也不与销售订单收款引用（ERP-053）相加。</span>
    </div>

    <table class="data-table">
      <tbody>
        <tr><th style="width:200px">收款单</th><td>${escapeHtml(r.receiptNo || '')}
          <span class="text-muted">（${escapeHtml(r.receiptStatusText || '')}）</span></td></tr>
        <tr><th>收款日期 / 客户</th><td>${escapeHtml(fmtDate(r.receiptDate) || '')} ·
          ${escapeHtml(r.customerCode || '')} ${escapeHtml(r.customerName || '')}</td></tr>
        <tr><th>币种</th><td><b>${escapeHtml(r.currency || '')}</b>（小数位 ${Number(r.amountDecimals || 0)}）</td></tr>
        <tr><th>收款金额</th><td><b>${escapeHtml(String(r.receiptAmount))} ${escapeHtml(r.currency || '')}</b>
          <div class="text-muted">金额取自收款单（只读快照），本册绝不改写收款单。</div></td></tr>
        <tr><th>本维度已分摊 / 可分摊余额</th><td>
          已分摊 <b>${escapeHtml(String(r.allocatedAmount))} ${escapeHtml(r.currency || '')}</b> ·
          可分摊 <b>${escapeHtml(String(r.unallocatedAmount))} ${escapeHtml(r.currency || '')}</b>
          <div class="text-muted">${escapeHtml(r.linkageText || '')}</div></td></tr>
        <tr><th>有效行 / 已作废行</th><td>${Number(r.allocationCount || 0)} / ${Number(r.voidedCount || 0)}</td></tr>
        <tr><th>可用性</th><td>${r.receiptAvailable ? '可继续分摊' : '不可继续分摊'}
          <div class="text-muted">${escapeHtml(r.receiptAvailabilityText || '')}</div></td></tr>
      </tbody>
    </table>

    <div style="margin-top:10px"><b>该收款单的分摊行（含已作废历史）</b></div>
    <table class="data-table" style="margin-top:4px">
      <thead><tr>
        <th>#</th><th>对账单（状态）</th><th>对账日期</th><th>对账单合计</th>
        <th>分摊金额</th><th>登记时间 / 登记人</th><th>状态</th><th>操作</th>
      </tr></thead>
      <tbody>
        ${rows.length === 0 ? '<tr><td colspan="8" class="text-muted">该收款单尚无代理服务费分摊证据（缺失即「无」，不代表未付 / 已付 / 已结清）。</td></tr>' : ''}
        ${rows.map(x => `
          <tr>
            <td>${Number(x.id)}</td>
            <td>${escapeHtml(x.statementNo || '')}<div class="text-muted">${escapeHtml(x.statementStatusText || '')}</div></td>
            <td>${escapeHtml(fmtDate(x.statementDate) || '')}</td>
            <td>${escapeHtml(x.statementAmountText || '')}</td>
            <td><b>${escapeHtml(x.allocatedAmountText || '')}</b>
              ${x.remark ? `<div class="text-muted">${escapeHtml(x.remark)}</div>` : ''}</td>
            <td>${escapeHtml(fmtDate(x.allocatedAt) || '')}<div class="text-muted">${escapeHtml(x.allocatedBy || '')}</div></td>
            <td>${asfcaStatusBadge(x)}</td>
            <td>
              <button class="btn btn-neutral btn-sm" onclick="asfcaOpenStatement(${Number(x.statementId)})">对账单视角</button>
              ${x.isVoided ? '' : `<button class="btn btn-danger btn-sm" onclick="asfcaOpenVoid(${Number(x.id)}, 'receipt')">作废</button>`}
            </td>
          </tr>`).join('')}
      </tbody>
    </table>

    <div style="margin-top:8px;padding:8px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;font-size:12px;color:#334155">
      <div><b>金额口径</b>：${escapeHtml(r.amountRuleText || '')}</div>
      <div style="margin-top:4px"><b>证据维度分离</b>：${escapeHtml(r.dimensionSeparationText || '')}</div>
      <div style="margin-top:4px"><b>模块边界</b>：${escapeHtml(r.boundaryText || '')}</div>
    </div>`;
}

/* ==================== 登记 / 作废 ==================== */

async function asfcaCreate() {
  const s = ASFCA.statement;
  if (!s) { toast('请先打开要分摊的对账单证据', 'error'); return; }

  const receiptId = (document.getElementById('asfca-receipt') || {}).value || ASFCA.selectedReceiptId;
  const amountText = (document.getElementById('asfca-amount') || {}).value || ASFCA.amount;
  const remark = (document.getElementById('asfca-remark') || {}).value || ASFCA.remark;

  if (!receiptId) {
    toast('请显式选择要分摊的收款单（系统不会按单号、金额或日期相似度猜对应关系）', 'error');
    return;
  }
  const amount = Number(amountText);
  if (amountText === '' || Number.isNaN(amount) || amount <= 0) {
    toast('请填写大于 0 的分摊金额（币种精度由服务端按权威口径取整）', 'error');
    return;
  }

  try {
    await api('/api/agency-service-fee-collection-allocations', 'POST', {
      statementId: Number(s.statementId),
      receiptId: Number(receiptId),
      allocatedAmount: amount,
      remark: remark || ''
    });
    toast('收款分摊证据已登记（仅证据留痕；未收款、未记账、未核销、未结算）');
  } catch (e) {
    toast('分摊失败：' + e.message, 'error');
    return;
  }

  await asfcaOpenStatement(s.statementId, true);
}

async function asfcaOpenVoid(allocationId, backTo) {
  let row = null;
  if (backTo === 'statement' && ASFCA.statement) {
    row = (ASFCA.statement.allocations || []).find(x => Number(x.id) === Number(allocationId)) || null;
  } else if (backTo === 'receipt' && ASFCA.receiptSummary) {
    row = (ASFCA.receiptSummary.allocations || []).find(x => Number(x.id) === Number(allocationId)) || null;
  } else {
    row = (ASFCA.list || []).find(x => Number(x.id) === Number(allocationId)) || null;
  }
  if (!row) {
    try {
      row = await api('/api/agency-service-fee-collection-allocations/' + allocationId);
    } catch (e) {
      toast('分摊行详情加载失败：' + e.message, 'error');
      return;
    }
  }
  ASFCA.voidTarget = row;
  ASFCA.voidReason = '';
  ASFCA.view = 'void';
  asfcaRender();
}

function asfcaVoidView() {
  const row = ASFCA.voidTarget || {};
  return `
    <div style="padding:8px;background:#fef2f2;border:1px solid #fecaca;border-radius:8px;color:#991b1b;font-size:12px">
      作废保留原始分摊金额、对账单 / 收款单 / 客户快照、登记人与时间戳（历史可读），
      不会删除记录、不会改派到别的收款单或对账单、不会改写原始金额，也不会产生任何收款 / 记账 / 核销 / 结算动作；
      作废后同一「对账单 + 收款单」可以重新登记一条新的有效分摊行（新旧并存可查）。
    </div>
    <table class="data-table" style="margin-top:8px">
      <tbody>
        <tr><th style="width:180px">分摊目标</th><td>${escapeHtml(row.identityText || '')}</td></tr>
        <tr><th>客户 / 币种</th><td>${escapeHtml(row.customerCode || '')} ${escapeHtml(row.customerName || '')} ·
          ${escapeHtml(row.currency || '')}</td></tr>
        <tr><th>原始分摊金额</th><td><b>${escapeHtml(row.allocatedAmountText || '')}</b>
          <div class="text-muted">作废不改写该金额；收款金额快照 ${escapeHtml(row.receiptAmountText || '')}</div></td></tr>
        <tr><th>登记时间 / 登记人</th><td>${escapeHtml(fmtDate(row.allocatedAt) || '')} ·
          ${escapeHtml(row.allocatedBy || '')}</td></tr>
      </tbody>
    </table>
    <div style="margin-top:8px"><label class="ea-lb">作废原因 *</label>
      <input id="asfca-void-reason" style="width:100%" value="${escapeHtml(ASFCA.voidReason)}"
        onchange="ASFCA.voidReason=this.value" placeholder="例如：分摊口径更正 / 登记错误 / 收款单选择错误"></div>
    <div style="display:flex;gap:6px;margin-top:10px">
      <button class="btn btn-danger" onclick="asfcaConfirmVoid(${Number(row.id)})">确认作废</button>
      <button class="btn btn-neutral" onclick="asfcaBackToList()">取消</button>
    </div>`;
}

async function asfcaConfirmVoid(allocationId) {
  const reason = (document.getElementById('asfca-void-reason') || {}).value || ASFCA.voidReason || '';
  if (!reason.trim()) { toast('请填写作废原因（作废会保留原始分摊证据）', 'error'); return; }

  try {
    await api('/api/agency-service-fee-collection-allocations/' + allocationId + '/void', 'POST',
      { reason: reason.trim() });
    toast('收款分摊证据已作废（原始金额与历史保留，可读）');
  } catch (e) {
    toast('作废失败：' + e.message, 'error');
    return;
  }

  await asfcaBackToList();
}
