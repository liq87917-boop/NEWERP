/* ==================================================================================
   ====== 财务申请单（定金申请单）→ 来源销售订单 显式选择器（ERP-390） ======
   ==================================================================================
   定位：把「定金申请单来源销售订单」从「只能手工填 Id」升级为**显式有界选择**：服务端只读分页候选接口
        在既有「定金申请单」+「销售订单」菜单与实时客户数据范围之内，按**精确客户 + 归一化币种**返回
        「未删除」销售订单候选，操作员显式选定后表单只回填**权威来源 Id**（已取消 / 币种不一致不可选）。
   规则（与 FinanceApplySalesOrderSourceService / FinanceDepositApplyLifecycleRules 同口径，服务端为唯一权威）：
     1. 只接受**显式选择**的来源销售订单 Id：候选全部来自服务端有界只读分页查询，
        **绝不按订单号文本 / 金额 / 相似度猜测来源**，也绝不提供按猜测 Id 直取的旁路；
     2. 候选只含当前账号客户数据范围之内、**精确客户**名下的「未删除」销售订单；已取消订单标记为
        **不可用**；币种与申请单归一化币种不一致的订单同样标记不可选（**绝不自动换算币种**）；
     3. **客户 / 币种变更即失效**：切换客户或币种会清除先前的来源选择（绝不让旧上下文的来源留在新表单上）；
     4. **异步响应不得回填陈旧上下文结果**：候选请求带序号，客户 / 币种 / 关键字变化后旧响应一律丢弃；
     5. **重开**：既有申请单的来源 Id 由既有接口持久化并原样回显；来源已取消 / 不可用时仍以显式文案标注展示
        （绝不静默清除 / 重绑定）；表单可**显式断开**链接（留空 → 保存为未关联，仅待提交可改）；
     6. 只回填来源 Id，**绝不自动改动金额 / 汇率**，也绝不臆造订单号；
     7. 失败（网络 / 权限 / 服务端）保留表单既有输入并显式提示，不清空用户已填内容。
   边界：本选择器只做只读候选展示与来源 Id 回填：不锁库、不写库存 / 流水 / 财务、不改写订单 / 客户主数据、
         不执行任何资金记账 / 核销 / 余额计算，也不新增表 / 菜单 / 用户授权；最终保存仍由服务端按
         FinanceDepositApplyLifecycleRules 复核精确来源。
   ================================================================================== */

/* 只有「待提交」可维护来源（与后端 FinanceDepositApplyController 的状态判定一致） */
const FAS_EDITABLE_STATUS = 'Pending';
const FAS_STATUS_TEXT = {
  Pending: '待提交（草稿）', Submitted: '已提交', Approved: '已审核',
  Rejected: '已驳回', Completed: '已完成', Cancelled: '已取消',
};
const FAS_PAGE_SIZE_DEFAULT = 20;
const FAS_PAGE_SIZE_MAX = 100;
const FAS_READONLY_TEXT = '当前申请单不是「待提交（草稿）」：来源只读，如需更换请先另建待提交申请单。';
const FAS_ERROR_PREFIX = { network: '无法连接服务器', unauthorized: '登录已过期或权限不足', server: '服务端拒绝' };

/* 一次对话 = 一个申请单表单；candidates 为服务端权威候选，stored / selected 为来源链接状态 */
let FAS = {
  applyId: 0, status: '', customerId: 0, currency: '', selectedContext: null,
  keyword: '', page: 1, pageSize: FAS_PAGE_SIZE_DEFAULT, total: 0,
  candidates: [], stored: null, selected: null,
  loading: false, readonly: false, error: '', result: '', open: false, requestSeq: 0,
};

/* 状态归一化：既接受后端枚举名（Pending），也接受数字与已有中文文案 */
function fasNormalizeStatus(status) {
  if (status === null || status === undefined || status === '') return '';
  const raw = String(status).trim();
  if (FAS_STATUS_TEXT[raw]) return raw;
  const byNumber = { '0': 'Pending', '1': 'Submitted', '2': 'Approved', '3': 'Rejected', '4': 'Completed', '5': 'Cancelled' };
  if (byNumber[raw]) return byNumber[raw];
  const hit = Object.keys(FAS_STATUS_TEXT).find(k => FAS_STATUS_TEXT[k] === raw);
  return hit || raw;
}

function fasStatusText(status) {
  const key = fasNormalizeStatus(status);
  return FAS_STATUS_TEXT[key] || (key || '未知');
}

function fasIsEditableStatus(status) {
  return fasNormalizeStatus(status) === FAS_EDITABLE_STATUS;
}

function fasReadonlyReason(status) {
  return fasIsEditableStatus(status) ? '' : FAS_READONLY_TEXT;
}

/* 申请单 Id 闸门：0 = 新建；负数 / 非数字一律拒绝（绝不臆造单据） */
function fasGuardApplyId(applyId) {
  if (applyId === null || applyId === undefined || applyId === '') return '';
  const n = Number(applyId);
  if (!Number.isInteger(n) || n < 0) return '定金申请单 Id 非法：拒绝选择来源（绝不臆造单据）';
  return '';
}

/* 正整数判定（Id 一律用 Number 解析，绝不用 parseInt / parseFloat 从文本里抠） */
function fasIsPositiveId(value) {
  if (value === null || value === undefined || value === '') return false;
  const n = Number(value);
  return Number.isInteger(n) && n > 0;
}

function fasCustomerId(value) {
  const n = Number(value);
  return Number.isInteger(n) && n > 0 ? n : 0;
}

/* 币种归一化（与服务端币种口径一致：去空白 + 大写，绝不换算） */
function fasNormalizeCurrency(value) {
  if (value === null || value === undefined) return '';
  return String(value).trim().toUpperCase();
}

/* 分页归一化（与服务端 NormalizePage / NormalizePageSize 同口径） */
function fasNormalizePage(page) {
  const n = Number(page);
  if (!Number.isFinite(n) || n < 1) return 1;
  return Math.floor(n);
}

function fasNormalizePageSize(pageSize) {
  const n = Number(pageSize);
  if (!Number.isFinite(n) || n < 1) return FAS_PAGE_SIZE_DEFAULT;
  return Math.min(Math.floor(n), FAS_PAGE_SIZE_MAX);
}

/* 关键字归一化：去首尾空白并截断（与服务端 NormalizeKeyword 同口径，先归一化再计数） */
function fasNormalizeKeyword(keyword) {
  const text = keyword === null || keyword === undefined ? '' : String(keyword).trim();
  return text.length <= 100 ? text : text.slice(0, 100);
}

/* 可选择的候选：仅服务端标记 eligible = true（已取消 / 币种不一致绝不可选） */
function fasAvailableCandidates(candidates) {
  return (candidates || []).filter(c => c && c.eligible === true && fasIsPositiveId(c.salesOrderId));
}

/* 候选 → 显式选择（不可用 / 无 Id 一律返回 null，绝不臆造来源） */
function fasSelectionFromCandidate(candidate) {
  if (!candidate || candidate.eligible !== true) return null;
  if (!fasIsPositiveId(candidate.salesOrderId)) return null;
  return {
    salesOrderId: candidate.salesOrderId,
    orderNo: candidate.orderNo || '',
    customerId: fasCustomerId(candidate.customerId),
    currency: fasNormalizeCurrency(candidate.currency),
  };
}

/* 已选来源是否仍属于当前上下文（客户 + 归一化币种；客户 / 币种变更即失效的依据） */
function fasSelectionMatchesContext(selection, customerId, currency) {
  if (!selection || !fasIsPositiveId(selection.salesOrderId)) return false;
  const currentCustomer = fasCustomerId(customerId);
  if (!(currentCustomer > 0)) return false;
  if (Number(selection.customerId) !== currentCustomer) return false;
  const currentCurrency = fasNormalizeCurrency(currency);
  if (!currentCurrency) return true;
  return fasNormalizeCurrency(selection.currency) === currentCurrency;
}

/* 客户 / 币种变更后是否需要让先前的来源选择失效（有选择且不再匹配当前上下文） */
function fasShouldInvalidateSelection(selection, currentCustomerId, currentCurrency) {
  if (!selection || !fasIsPositiveId(selection.salesOrderId)) return false;
  return !fasSelectionMatchesContext(selection, currentCustomerId, currentCurrency);
}

/* 陈旧异步响应判定：请求序号不一致、或响应上下文已不是当前上下文，一律丢弃 */
function fasShouldAcceptResponse(seq, currentSeq, responseCustomerId, currentCustomerId,
  responseCurrency, currentCurrency) {
  if (seq !== currentSeq) return false;
  const current = fasCustomerId(currentCustomerId);
  if (!(current > 0)) return false;
  if (fasCustomerId(responseCustomerId) !== current) return false;
  const currentCur = fasNormalizeCurrency(currentCurrency);
  if (!currentCur) return true;
  return fasNormalizeCurrency(responseCurrency) === currentCur;
}

/* 应用到表单的值：只回填来源 Id（不臆造订单号，不改金额 / 汇率；无选择 → 留空显式断开） */
function fasFormFill(selection) {
  if (!selection || !fasIsPositiveId(selection.salesOrderId)) return { salesOrderId: '' };
  return { salesOrderId: selection.salesOrderId };
}

function fasUnlinkFill() {
  return { salesOrderId: '' };
}

/* 候选展示文案：订单号 / 日期 / 客户 / 币种 / 状态 / 可用性 */
function fasCandidateLabel(candidate) {
  if (!candidate) return '';
  const parts = [
    candidate.orderNo ? `订单 ${candidate.orderNo}` : `订单 Id ${candidate.salesOrderId ?? ''}`,
    candidate.customerName ? `客户 ${candidate.customerName}` : '',
    candidate.orderDate ? `日期 ${String(candidate.orderDate).slice(0, 10)}` : '',
    candidate.currency ? `币种 ${fasNormalizeCurrency(candidate.currency)}` : '',
    `状态 ${fasStatusText(candidate.status)}`,
    candidate.eligible === true ? '可显式选择' : (candidate.ineligibleReason || '不可选'),
  ];
  return parts.filter(Boolean).join(' / ');
}

/* 安全转义（浏览器复用全局 escapeHtml；Node 单测走本地实现，绝不注入） */
function fasEsc(value) {
  const s = value === null || value === undefined ? '' : String(value);
  if (typeof escapeHtml === 'function') return escapeHtml(s);
  return s.replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

/* 候选行：订单号 / 日期 / 客户 / 币种 / 状态 / 可用性 + 显式选择入口（不可选行禁用） */
function fasCandidateRowHtml(candidate) {
  if (!candidate) return '';
  const eligible = candidate.eligible === true && fasIsPositiveId(candidate.salesOrderId);
  const action = eligible
    ? `<button type="button" class="btn btn-neutral btn-sm" onclick="fasPickCandidate(${fasEsc(candidate.salesOrderId)})">选择此订单</button>`
    : `<span class="text-muted">不可选</span>`;
  return `<tr${eligible ? '' : ' class="text-muted"'}>
    <td>${fasEsc(candidate.salesOrderId)}</td>
    <td>${fasEsc(candidate.orderNo || '')}</td>
    <td>${fasEsc(candidate.orderDate ? String(candidate.orderDate).slice(0, 10) : '')}</td>
    <td>${fasEsc(candidate.customerName || '')}</td>
    <td>${fasEsc(fasNormalizeCurrency(candidate.currency))}</td>
    <td>${fasEsc(fasStatusText(candidate.status))}</td>
    <td>${eligible ? '可用' : fasEsc(candidate.ineligibleReason || '不可用')}</td>
    <td>${action}</td>
  </tr>`;
}

/* 已存储来源只读展示：显式标注未关联 / 已关联 / 来源已取消 / 来源不可用（历史原样保留） */
function fasStoredSourceHtml(view) {
  if (!view || view.linked !== true) {
    return `<div class="fas-stored" style="padding:6px 8px;color:#6b7280">当前未关联来源销售订单（历史未关联语义原样保留，绝不回填）</div>`;
  }
  const status = view.status ? `（状态 ${fasStatusText(view.status)}${view.eligibleForNewLink === true ? '，仍可作为新来源' : '，只读保留'}）` : '';
  const orderNo = view.orderNo ? `「${fasEsc(view.orderNo)}」` : `Id ${fasEsc(view.salesOrderId)}`;
  return `<div class="fas-stored" style="padding:6px 8px;background:#f9fafb;border:1px solid #e5e7eb;border-radius:8px">`
    + `已存储来源 ${orderNo}${fasEsc(status)}：${fasEsc(view.annotation || '')}</div>`;
}

function fasErrorHtml(kind, message) {
  const prefix = FAS_ERROR_PREFIX[kind] || '操作失败';
  return `<div class="fas-error" role="alert" style="padding:8px 10px;background:#fef2f2;border:1px solid #fecaca;border-radius:8px;color:#b91c1c;font-size:13px;margin-bottom:8px">`
    + `${fasEsc(prefix)}：${fasEsc(message || '')}</div>`;
}

function fasErrorMessage(err) {
  const message = err && err.message ? err.message : String(err || '');
  if (/Failed to fetch|NetworkError|network|无法连接/i.test(message)) return { kind: 'network', message };
  if (/权限|授权|未认证|登录|禁止/.test(message)) return { kind: 'unauthorized', message };
  return { kind: 'server', message };
}

/* ==================== 浏览器接线（Node 单测只使用上面的纯函数） ==================== */

function fasFieldValue(key) {
  const el = document.getElementById('f_' + key);
  return el ? el.value : '';
}

/* 只回填来源 Id 字段：无选择 / 显式断开 → 留空（保存时服务端归一化为 null） */
function fasFillForm(fill) {
  const el = typeof document !== 'undefined' ? document.getElementById('f_salesOrderId') : null;
  if (!el) return;
  const value = fill && fill.salesOrderId;
  el.value = fasIsPositiveId(value) ? value : '';
}

function fasModalHtml() {
  const warning = FAS.readonly ? fasErrorHtml('server', FAS_READONLY_TEXT) : '';
  const error = FAS.error ? fasErrorHtml(fasErrorMessage({ message: FAS.error }).kind, FAS.error) : '';
  const result = FAS.result ? `<div class="text-muted" role="status" style="margin:6px 0">${fasEsc(FAS.result)}</div>` : '';
  const rows = (FAS.candidates || []).map(fasCandidateRowHtml).join('')
    || `<tr><td colspan="8" class="text-muted">没有匹配的来源销售订单候选（严格按精确客户 + 币种返回）</td></tr>`;
  return `<div class="modal modal-lg">
    <div class="modal-head"><span>选择来源销售订单（定金申请单）</span><button type="button" class="btn btn-neutral btn-sm" onclick="fasCloseModal()">关闭</button></div>
    <div class="modal-body">
      ${warning}${error}${result}
      <div style="margin-bottom:8px">客户 Id：<b>${fasEsc(FAS.customerId)}</b>；币种：<b>${fasEsc(fasNormalizeCurrency(FAS.currency) || '（未指定）')}</b>；只显示该客户名下「未删除」的销售订单（已取消 / 币种不一致不可选）</div>
      <div style="margin-bottom:8px">
        <input type="text" id="fas_keyword" value="${fasEsc(FAS.keyword)}" placeholder="按订单号 / 客户 PO / 合同号搜索" style="width:260px">
        <button type="button" class="btn btn-neutral btn-sm" onclick="fasSearch()">搜索</button>
        <button type="button" class="btn btn-neutral btn-sm" onclick="fasUnlinkSource()">显式断开来源链接</button>
      </div>
      <div style="margin-bottom:8px">${fasStoredSourceHtml(FAS.stored)}</div>
      <table class="data-table">
        <thead><tr><th>Id</th><th>订单号</th><th>日期</th><th>客户</th><th>币种</th><th>状态</th><th>可用性</th><th>操作</th></tr></thead>
        <tbody>${rows}</tbody>
      </table>
      <div class="text-muted" style="margin-top:6px">共 ${fasEsc(FAS.total)} 条候选；每页最多 ${fasEsc(FAS.pageSize)} 条。只回填来源 Id，绝不自动改动金额 / 汇率。</div>
    </div>
  </div>`;
}

function fasRender() {
  if (typeof document === 'undefined' || !FAS.open) return;
  const modal = document.getElementById('modal');
  if (!modal) return;
  modal.innerHTML = fasModalHtml();
  modal.style.display = 'flex';
}

function fasCloseModal() {
  FAS.open = false;
  FAS.error = '';
  if (typeof closeModal === 'function') closeModal();
}

async function fasLoadStatus(applyId) {
  FAS.status = '';
  FAS.readonly = false;
  if (!(applyId > 0)) return;
  try {
    const row = await api(`/api/finance/deposit-applies/${applyId}`);
    FAS.status = fasNormalizeStatus(row && row.status);
    FAS.readonly = !fasIsEditableStatus(FAS.status);
  } catch (err) {
    FAS.readonly = true;
    FAS.error = fasErrorMessage(err).message;
  }
}

async function fasLoadStoredSource(applyId) {
  FAS.stored = null;
  if (!(applyId > 0)) return;
  try {
    FAS.stored = await api(`/api/finance/deposit-applies/${applyId}/sales-order-source`) || null;
  } catch (err) {
    FAS.stored = null;
    FAS.error = fasErrorMessage(err).message;   // 只读展示失败显式可见，保留表单状态
  }
  fasRender();
}

async function fasLoadCandidates() {
  const seq = ++FAS.requestSeq;
  const requestCustomerId = FAS.customerId;
  const requestCurrency = fasNormalizeCurrency(FAS.currency);
  FAS.loading = true;
  FAS.error = '';
  fasRender();
  const params = ['customerId=' + encodeURIComponent(requestCustomerId)];
  if (requestCurrency) params.push('currency=' + encodeURIComponent(requestCurrency));
  params.push('page=' + encodeURIComponent(fasNormalizePage(FAS.page)));
  params.push('pageSize=' + encodeURIComponent(fasNormalizePageSize(FAS.pageSize)));
  const kw = fasNormalizeKeyword(FAS.keyword);
  if (kw) params.push('keyword=' + encodeURIComponent(kw));
  try {
    const res = await api('/api/finance/deposit-applies/sales-order-candidates?' + params.join('&'));
    // 陈旧响应（客户 / 币种已切换）一律丢弃，绝不回填陈旧上下文结果
    if (!fasShouldAcceptResponse(seq, FAS.requestSeq, requestCustomerId, FAS.customerId,
      requestCurrency, FAS.currency)) return;
    FAS.page = fasNormalizePage(res && res.page ? res.page : FAS.page);
    FAS.pageSize = fasNormalizePageSize(res && res.pageSize ? res.pageSize : FAS.pageSize);
    FAS.total = res && Number.isFinite(Number(res.total)) ? Number(res.total) : 0;
    FAS.candidates = (res && res.items) || [];
    FAS.loading = false;
    fasRender();
  } catch (err) {
    if (seq !== FAS.requestSeq) return;      // 陈旧失败也不覆盖当前状态
    FAS.loading = false;
    FAS.error = fasErrorMessage(err).message; // 失败保留表单状态
    fasRender();
  }
}

async function fasSearch() {
  const el = typeof document !== 'undefined' ? document.getElementById('fas_keyword') : null;
  FAS.keyword = fasNormalizeKeyword(el ? el.value : FAS.keyword);
  FAS.page = 1;
  await fasLoadCandidates();
}

function fasPickCandidate(salesOrderId) {
  if (FAS.readonly) { if (typeof toast === 'function') toast(FAS_READONLY_TEXT, 'error'); return; }
  const candidate = (FAS.candidates || []).find(c => Number(c.salesOrderId) === Number(salesOrderId));
  const selection = fasSelectionFromCandidate(candidate);
  if (!selection) {
    if (typeof toast === 'function') toast('该候选不可选（已取消 / 币种不一致 / 无效）：绝不臆造来源', 'error');
    return;
  }
  FAS.selected = selection;
  fasFillForm(fasFormFill(selection));
  FAS.result = `已回填来源销售订单 Id ${selection.salesOrderId}（不自动改金额 / 汇率）`;
  fasRender();
}

function fasUnlinkSource() {
  if (FAS.readonly) { if (typeof toast === 'function') toast(FAS_READONLY_TEXT, 'error'); return; }
  FAS.selected = null;
  fasFillForm(fasUnlinkFill());
  FAS.result = '已显式断开来源链接（保存后生效；历史未关联语义保留）';
  fasRender();
}

function fasApplyToForm() {
  if (!FAS.selected) { if (typeof toast === 'function') toast('请先选择一个可用的来源销售订单', 'error'); return; }
  fasFillForm(fasFormFill(FAS.selected));
  FAS.result = `已应用来源销售订单 Id ${FAS.selected.salesOrderId}`;
  fasRender();
}

/* 客户 / 币种变更：作废在途响应 + 清除先前的来源选择（绝不把旧上下文来源留在新表单上） */
function fasOnContextChanged() {
  FAS.requestSeq += 1;
  FAS.customerId = fasCustomerId(fasFieldValue('customerId'));
  FAS.currency = fasNormalizeCurrency(fasFieldValue('currency'));
  FAS.selectedContext = { customerId: FAS.customerId, currency: FAS.currency };
  FAS.candidates = [];
  FAS.total = 0;
  FAS.stored = null;
  FAS.selected = null;
  if (typeof document !== 'undefined') {
    const el = document.getElementById('f_salesOrderId');
    if (el && fasIsPositiveId(el.value)) {
      el.value = '';
      FAS.result = '客户或币种已变更：原来源选择已失效并清除，请重新选择来源';
    }
  }
  fasRender();
}

function fasInstallContextChangeHook() {
  if (typeof document === 'undefined') return false;
  let installed = false;
  ['customerId', 'currency'].forEach(key => {
    const el = document.getElementById('f_' + key);
    if (!el || el.__fasWatched) return;
    el.__fasWatched = true;
    el.addEventListener('change', fasOnContextChanged);
    el.addEventListener('input', fasOnContextChanged);
    installed = true;
  });
  return installed;
}

/* 打开选择器：新建（0）与已保存单据都可用；重开时显式回显服务端持久化的来源 */
async function openFinanceApplySalesOrderSourcePicker(applyId) {
  if (typeof document === 'undefined') return;
  const guard = fasGuardApplyId(applyId);
  if (guard) { if (typeof toast === 'function') toast(guard, 'error'); return; }

  const customerId = fasCustomerId(fasFieldValue('customerId'));
  const currency = fasNormalizeCurrency(fasFieldValue('currency'));
  FAS.open = true;
  FAS.applyId = Number(applyId) || 0;
  FAS.customerId = customerId;
  FAS.currency = currency;
  FAS.selectedContext = { customerId, currency };
  FAS.keyword = '';
  FAS.page = 1;
  FAS.pageSize = FAS_PAGE_SIZE_DEFAULT;
  FAS.total = 0;
  FAS.candidates = [];
  FAS.stored = null;
  FAS.selected = null;
  FAS.loading = false;
  FAS.readonly = false;
  FAS.error = '';
  FAS.result = '';
  FAS.requestSeq += 1;
  fasRender();

  if (!(customerId > 0)) {
    FAS.error = '请先选择客户后再选择来源销售订单（候选严格按精确客户返回，绝不返回任意客户订单）';
    fasRender();
    return;
  }

  await fasLoadStatus(FAS.applyId);
  await fasLoadStoredSource(FAS.applyId);
  await fasLoadCandidates();
}

/* 表单接入：为 modules-finance 中声明 selector: 'finance-apply-sales-order-source' 的字段追加「选择来源」入口。
   只包裹既有全局函数，不新增模块 / 菜单，也不影响其它单据。 */
function fasSourceButtonHtml() {
  return `<div class="form-item full"><label>来源销售订单选择</label>`
    + `<button type="button" class="btn btn-neutral" onclick="openFinanceApplySalesOrderSourcePicker(window.__fasCurrentId || 0)">选择来源销售订单（按客户 + 币种有界候选）</button>`
    + `<span class="text-muted" style="margin-left:8px">仅回填权威来源 Id；已取消 / 币种不一致不可选；可显式断开；不自动改金额 / 汇率</span>`
    + `</div>`;
}

function fasInstallFormHook() {
  if (typeof window === 'undefined') return false;
  if (window.__fasHooked) return true;
  if (typeof window.fieldHtml !== 'function' || typeof window.openForm !== 'function') return false;

  const originalOpenForm = window.openForm;
  const wrappedOpenForm = function (id) {
    window.__fasCurrentId = (id === undefined || id === null || id === '') ? 0 : (Number(id) || 0);
    const result = originalOpenForm.apply(this, arguments);
    fasInstallContextChangeHook();
    return result;
  };
  wrappedOpenForm.__fasWrapped = true;
  window.openForm = wrappedOpenForm;

  const originalFieldHtml = window.fieldHtml;
  const wrappedFieldHtml = function (field) {
    const html = originalFieldHtml.apply(this, arguments);
    if (!field || field.selector !== 'finance-apply-sales-order-source') return html;
    return html + fasSourceButtonHtml();
  };
  wrappedFieldHtml.__fasWrapped = true;
  window.fieldHtml = wrappedFieldHtml;
  window.__fasHooked = true;
  return true;
}

if (typeof window !== 'undefined' && typeof window.fieldHtml === 'function') fasInstallFormHook();

/* Node 单测导出（浏览器中 module 为 undefined，自动跳过） */
if (typeof module !== 'undefined' && module.exports) {
  module.exports = {
    FAS_EDITABLE_STATUS, FAS_PAGE_SIZE_DEFAULT, FAS_PAGE_SIZE_MAX, FAS_READONLY_TEXT, FAS_ERROR_PREFIX,
    fasNormalizeStatus, fasStatusText, fasIsEditableStatus, fasReadonlyReason,
    fasGuardApplyId, fasIsPositiveId, fasCustomerId, fasNormalizeCurrency,
    fasNormalizePage, fasNormalizePageSize, fasNormalizeKeyword,
    fasAvailableCandidates, fasSelectionFromCandidate, fasSelectionMatchesContext,
    fasShouldInvalidateSelection, fasShouldAcceptResponse, fasFormFill, fasUnlinkFill,
    fasCandidateLabel, fasEsc, fasCandidateRowHtml, fasStoredSourceHtml, fasErrorHtml, fasErrorMessage,
    fasSourceButtonHtml, fasInstallFormHook, fasInstallContextChangeHook, fasOnContextChanged,
    openFinanceApplySalesOrderSourcePicker, fasApplyToForm, fasUnlinkSource, fasPickCandidate,
    fasSearch, fasLoadCandidates, fasLoadStoredSource, fasLoadStatus,
  };
}




