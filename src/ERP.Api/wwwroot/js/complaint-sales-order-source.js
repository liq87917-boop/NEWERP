/* ==================================================================================
   ====== 客诉单 → 来源销售订单 显式选择器（ERP-389） ======
   ==================================================================================
   定位：把「客诉单来源销售订单」从「只能手工填 Id」升级为**显式有界选择**：服务端只读分页候选接口
        在既有「客诉单」+「销售订单」菜单与实时客户数据范围之内，按**精确客户**返回「未删除」销售订单候选，
        操作员显式选定后表单只回填**权威来源 Id**（已取消订单不可选）。
   规则（与 ComplaintSalesOrderSourceService / FinanceComplaintLifecycleRules 同口径，服务端为唯一权威）：
     1. 只接受**显式选择**的来源销售订单 Id：候选全部来自服务端有界只读分页查询，
        **绝不按订单号文本 / 金额 / 相似度猜测来源**，也绝不提供按猜测 Id 直取的旁路；
     2. 候选只含当前账号客户数据范围之内、**精确客户**名下的「未删除」销售订单；
        已取消订单标记为**不可用**且不可选择（历史已记录的链接只读保留，绝不静默重绑定）；
     3. **客户变更即失效**：切换客户会清除先前的来源选择（绝不让旧客户的来源留在新客户表单上）；
     4. **异步响应不得回填陈旧客户结果**：候选请求带序号，客户 / 关键字变化后旧响应一律丢弃；
     5. **重开**：既有客诉单的来源 Id 由既有接口持久化并原样回显；来源已取消 / 不可用时仍以显式文案
        标注展示（绝不静默清除 / 重绑定）；表单可**显式断开**链接（留空 → 保存为未关联）；
     6. 只回填来源 Id，**绝不自动改动数量 / 金额**（客诉单没有数量 / 金额联动，也绝不臆造订单号）；
     7. 失败（网络 / 权限 / 服务端）保留表单既有输入并显式提示，不清空用户已填内容。
   边界：本选择器只做只读候选展示与来源 Id 回填：不锁库、不写库存 / 流水 / 财务、不改写订单 / 客户主数据，
         也不新增表 / 菜单 / 用户授权；最终保存仍由服务端按 ERP-388 生命周期规则复核精确来源。
   ================================================================================== */

/* 只有「待提交」可维护来源（与后端 FinanceComplaintController 的状态判定一致） */
const COS_EDITABLE_STATUS = 'Pending';
const COS_STATUS_TEXT = {
  Pending: '待提交（草稿）', Submitted: '已提交', Approved: '已审核',
  Rejected: '已驳回', Completed: '已完成', Cancelled: '已取消',
};
const COS_PAGE_SIZE_DEFAULT = 20;
const COS_PAGE_SIZE_MAX = 100;
const COS_READONLY_TEXT = '当前单据不是「待提交（草稿）」：来源只读，如需更换请先另建待提交单据。';

/* 一次对话 = 一个客诉单表单；candidates 为服务端权威候选，stored / selected 为来源链接状态 */
let COS = {
  complaintId: 0, status: '', customerId: 0, selectedCustomerId: 0,
  keyword: '', page: 1, pageSize: COS_PAGE_SIZE_DEFAULT, total: 0,
  candidates: [], stored: null, selected: null,
  loading: false, readonly: false, error: '', result: '', open: false, requestSeq: 0,
};

/* 状态归一化：既接受后端枚举名（Pending），也接受数字与已有中文文案 */
function cosNormalizeStatus(status) {
  if (status === null || status === undefined || status === '') return '';
  const raw = String(status).trim();
  if (COS_STATUS_TEXT[raw]) return raw;
  const byNumber = { '0': 'Pending', '1': 'Submitted', '2': 'Approved', '3': 'Rejected', '4': 'Completed', '5': 'Cancelled' };
  if (byNumber[raw]) return byNumber[raw];
  const hit = Object.keys(COS_STATUS_TEXT).find(k => COS_STATUS_TEXT[k] === raw);
  return hit || raw;
}

function cosStatusText(status) {
  const key = cosNormalizeStatus(status);
  return COS_STATUS_TEXT[key] || (key || '未知');
}

function cosIsEditableStatus(status) {
  return cosNormalizeStatus(status) === COS_EDITABLE_STATUS;
}

function cosReadonlyReason(status) {
  return cosIsEditableStatus(status) ? '' : COS_READONLY_TEXT;
}

/* 客诉单 Id 闸门：0 = 新建；负数 / 非数字一律拒绝（绝不臆造单据） */
function cosGuardComplaintId(complaintId) {
  if (complaintId === null || complaintId === undefined || complaintId === '') return '';
  const n = Number(complaintId);
  if (!Number.isInteger(n) || n < 0) return '客诉单 Id 非法：拒绝选择来源（绝不臆造单据）';
  return '';
}

/* 正整数判定（Id 一律用 Number 解析，绝不用 parseInt / parseFloat 从文本里抠） */
function cosIsPositiveId(value) {
  if (value === null || value === undefined || value === '') return false;
  const n = Number(value);
  return Number.isInteger(n) && n > 0;
}

function cosCustomerId(value) {
  const n = Number(value);
  return Number.isInteger(n) && n > 0 ? n : 0;
}

/* 分页归一化（与服务端 NormalizePage / NormalizePageSize 同口径） */
function cosNormalizePage(page) {
  const n = Number(page);
  if (!Number.isFinite(n) || n < 1) return 1;
  return Math.floor(n);
}

function cosNormalizePageSize(pageSize) {
  const n = Number(pageSize);
  if (!Number.isFinite(n) || n < 1) return COS_PAGE_SIZE_DEFAULT;
  return Math.min(Math.floor(n), COS_PAGE_SIZE_MAX);
}

/* 关键字归一化：去首尾空白并截断（与服务端 NormalizeKeyword 同口径，先归一化再计数） */
function cosNormalizeKeyword(keyword) {
  const text = keyword === null || keyword === undefined ? '' : String(keyword).trim();
  return text.length <= 100 ? text : text.slice(0, 100);
}

/* 可选择的候选：仅服务端标记 eligible = true（已取消订单绝不可选） */
function cosAvailableCandidates(candidates) {
  return (candidates || []).filter(c => c && c.eligible === true && cosIsPositiveId(c.salesOrderId));
}

/* 候选 → 显式选择（不可用 / 无 Id 一律返回 null，绝不臆造来源） */
function cosSelectionFromCandidate(candidate) {
  if (!candidate || candidate.eligible !== true) return null;
  if (!cosIsPositiveId(candidate.salesOrderId)) return null;
  return {
    salesOrderId: candidate.salesOrderId,
    orderNo: candidate.orderNo || '',
    customerId: cosCustomerId(candidate.customerId),
  };
}

/* 已选来源是否仍属于当前客户（客户变更即失效的依据） */
function cosSelectionMatchesCustomer(selection, customerId) {
  if (!selection || !cosIsPositiveId(selection.salesOrderId)) return false;
  const current = cosCustomerId(customerId);
  return current > 0 && Number(selection.customerId) === current;
}

/* 客户变更后是否需要让先前的来源选择失效（有选择且不再匹配当前客户） */
function cosShouldInvalidateSelection(selection, currentCustomerId) {
  if (!selection || !cosIsPositiveId(selection.salesOrderId)) return false;
  return !cosSelectionMatchesCustomer(selection, currentCustomerId);
}

/* 陈旧异步响应判定：请求序号不一致、或响应客户已不是当前客户，一律丢弃（绝不回填陈旧客户结果） */
function cosShouldAcceptResponse(seq, currentSeq, responseCustomerId, currentCustomerId) {
  if (seq !== currentSeq) return false;
  const current = cosCustomerId(currentCustomerId);
  return current > 0 && cosCustomerId(responseCustomerId) === current;
}

/* 应用到表单的值：只回填来源 Id（不臆造订单号，不改数量 / 金额；无选择 → 留空显式断开） */
function cosFormFill(selection) {
  if (!selection || !cosIsPositiveId(selection.salesOrderId)) return { salesOrderId: '' };
  return { salesOrderId: selection.salesOrderId };
}

function cosUnlinkFill() {
  return { salesOrderId: '' };
}

/* 候选展示文案：订单号 / 日期 / 客户 / 状态 / 可用性 */
function cosCandidateLabel(candidate) {
  if (!candidate) return '';
  const parts = [
    candidate.orderNo ? `订单 ${candidate.orderNo}` : `订单 Id ${candidate.salesOrderId ?? ''}`,
    candidate.customerName ? `客户 ${candidate.customerName}` : '',
    candidate.orderDate ? `日期 ${String(candidate.orderDate).slice(0, 10)}` : '',
    `状态 ${cosStatusText(candidate.status)}`,
    candidate.eligible === true ? '可显式选择' : (candidate.ineligibleReason || '不可选'),
  ];
  return parts.filter(Boolean).join(' / ');
}

/* 安全转义（浏览器复用全局 escapeHtml；Node 单测走本地实现，绝不注入） */
function cosEsc(value) {
  const s = value === null || value === undefined ? '' : String(value);
  if (typeof escapeHtml === 'function') return escapeHtml(s);
  return s.replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

/* 候选行：订单号 / 日期 / 客户 / 状态 / 可用性 + 显式选择入口（不可选行禁用） */
function cosCandidateRowHtml(candidate) {
  if (!candidate) return '';
  const eligible = candidate.eligible === true && cosIsPositiveId(candidate.salesOrderId);
  const action = eligible
    ? `<button type="button" class="btn btn-neutral btn-sm" onclick="cosPickCandidate(${cosEsc(candidate.salesOrderId)})">选择此订单</button>`
    : `<span class="text-muted">不可选</span>`;
  return `<tr${eligible ? '' : ' class="text-muted"'}>
    <td>${cosEsc(candidate.salesOrderId)}</td>
    <td>${cosEsc(candidate.orderNo || '')}</td>
    <td>${cosEsc(candidate.orderDate ? String(candidate.orderDate).slice(0, 10) : '')}</td>
    <td>${cosEsc(candidate.customerName || '')}</td>
    <td>${cosEsc(cosStatusText(candidate.status))}</td>
    <td>${eligible ? '可用' : cosEsc(candidate.ineligibleReason || '不可用')}</td>
    <td>${action}</td>
  </tr>`;
}

/* 已存储来源只读展示：显式标注未关联 / 已关联 / 来源已取消 / 来源不可用（历史原样保留） */
function cosStoredSourceHtml(view) {
  if (!view || view.linked !== true) {
    return `<div class="cos-stored" style="padding:6px 8px;color:#6b7280">当前未关联来源销售订单（历史未关联语义原样保留，绝不回填）</div>`;
  }
  const status = view.status ? `（状态 ${cosStatusText(view.status)}${view.eligibleForNewLink === true ? '，仍可作为新来源' : '，只读保留'}）` : '';
  const orderNo = view.orderNo ? `「${cosEsc(view.orderNo)}」` : `Id ${cosEsc(view.salesOrderId)}`;
  return `<div class="cos-stored" style="padding:6px 8px;background:#f9fafb;border:1px solid #e5e7eb;border-radius:8px">`
    + `已存储来源 ${orderNo}${cosEsc(status)}：${cosEsc(view.annotation || '')}</div>`;
}

const COS_ERROR_PREFIX = { network: '无法连接服务器', unauthorized: '登录已过期或权限不足', server: '服务端拒绝' };

function cosErrorHtml(kind, message) {
  const prefix = COS_ERROR_PREFIX[kind] || '操作失败';
  return `<div class="cos-error" role="alert" style="padding:8px 10px;background:#fef2f2;border:1px solid #fecaca;border-radius:8px;color:#b91c1c;font-size:13px;margin-bottom:8px">`
    + `${cosEsc(prefix)}：${cosEsc(message || '')}</div>`;
}

function cosErrorMessage(err) {
  const message = err && err.message ? err.message : String(err || '');
  if (/Failed to fetch|NetworkError|network|无法连接/i.test(message)) return { kind: 'network', message };
  if (/权限|授权|未认证|登录|禁止/.test(message)) return { kind: 'unauthorized', message };
  return { kind: 'server', message };
}

/* ==================== 浏览器接线（Node 单测只使用上面的纯函数） ==================== */

function cosFieldValue(key) {
  const el = document.getElementById('f_' + key);
  return el ? el.value : '';
}

/* 只回填来源 Id 字段：无选择 / 显式断开 → 留空（保存时服务端归一化为 null） */
function cosFillForm(fill) {
  const el = typeof document !== 'undefined' ? document.getElementById('f_salesOrderId') : null;
  if (!el) return;
  const value = fill && fill.salesOrderId;
  el.value = cosIsPositiveId(value) ? value : '';
}

function cosModalHtml() {
  const warning = COS.readonly ? cosErrorHtml('server', COS_READONLY_TEXT) : '';
  const error = COS.error ? cosErrorHtml(cosErrorMessage({ message: COS.error }).kind, COS.error) : '';
  const result = COS.result ? `<div class="text-muted" role="status" style="margin:6px 0">${cosEsc(COS.result)}</div>` : '';
  const rows = (COS.candidates || []).map(cosCandidateRowHtml).join('')
    || `<tr><td colspan="7" class="text-muted">没有匹配的来源销售订单候选（严格按精确客户返回）</td></tr>`;
  return `<div class="modal modal-lg">
    <div class="modal-head"><span>选择来源销售订单（客诉单）</span><button type="button" class="btn btn-neutral btn-sm" onclick="cosCloseModal()">关闭</button></div>
    <div class="modal-body">
      ${warning}${error}${result}
      <div style="margin-bottom:8px">客户 Id：<b>${cosEsc(COS.customerId)}</b>；只显示该客户名下「未删除」的销售订单（已取消不可选）</div>
      <div style="margin-bottom:8px">
        <input type="text" id="cos_keyword" value="${cosEsc(COS.keyword)}" placeholder="按订单号 / 客户 PO / 合同号搜索" style="width:260px">
        <button type="button" class="btn btn-neutral btn-sm" onclick="cosSearch()">搜索</button>
        <button type="button" class="btn btn-neutral btn-sm" onclick="cosUnlinkSource()">显式断开来源链接</button>
      </div>
      <div style="margin-bottom:8px">${cosStoredSourceHtml(COS.stored)}</div>
      <table class="data-table">
        <thead><tr><th>Id</th><th>订单号</th><th>日期</th><th>客户</th><th>状态</th><th>可用性</th><th>操作</th></tr></thead>
        <tbody>${rows}</tbody>
      </table>
      <div class="text-muted" style="margin-top:6px">共 ${cosEsc(COS.total)} 条候选；每页最多 ${cosEsc(COS.pageSize)} 条。</div>
    </div>
  </div>`;
}

function cosRender() {
  if (typeof document === 'undefined' || !COS.open) return;
  const modal = document.getElementById('modal');
  if (!modal) return;
  modal.innerHTML = cosModalHtml();
  modal.style.display = 'flex';
}

function cosCloseModal() {
  COS.open = false;
  COS.error = '';
  if (typeof closeModal === 'function') closeModal();
}

async function cosLoadStatus(complaintId) {
  COS.status = '';
  COS.readonly = false;
  if (!(complaintId > 0)) return;
  try {
    const row = await api(`/api/finance/complaints/${complaintId}`);
    COS.status = cosNormalizeStatus(row && row.status);
    COS.readonly = !cosIsEditableStatus(COS.status);
  } catch (err) {
    COS.readonly = true;
    COS.error = cosErrorMessage(err).message;
  }
}

async function cosLoadStoredSource(complaintId) {
  COS.stored = null;
  if (!(complaintId > 0)) return;
  try {
    COS.stored = await api(`/api/finance/complaints/${complaintId}/sales-order-source`) || null;
  } catch (err) {
    COS.stored = null;
    COS.error = cosErrorMessage(err).message;   // 只读展示失败显式可见，保留表单状态
  }
  cosRender();
}

async function cosLoadCandidates() {
  const seq = ++COS.requestSeq;
  const requestCustomerId = COS.customerId;
  COS.loading = true;
  COS.error = '';
  cosRender();
  const params = ['customerId=' + encodeURIComponent(requestCustomerId),
    'page=' + encodeURIComponent(cosNormalizePage(COS.page)),
    'pageSize=' + encodeURIComponent(cosNormalizePageSize(COS.pageSize))];
  const kw = cosNormalizeKeyword(COS.keyword);
  if (kw) params.push('keyword=' + encodeURIComponent(kw));
  try {
    const res = await api('/api/finance/complaints/sales-order-candidates?' + params.join('&'));
    // 陈旧响应（客户已切换 / 关键字已变化）一律丢弃，绝不回填陈旧客户结果
    if (!cosShouldAcceptResponse(seq, COS.requestSeq, requestCustomerId, COS.customerId)) return;
    COS.page = cosNormalizePage(res && res.page ? res.page : COS.page);
    COS.pageSize = cosNormalizePageSize(res && res.pageSize ? res.pageSize : COS.pageSize);
    COS.total = res && Number.isFinite(Number(res.total)) ? Number(res.total) : 0;
    COS.candidates = (res && res.items) || [];
    COS.loading = false;
    cosRender();
  } catch (err) {
    if (seq !== COS.requestSeq) return;      // 陈旧失败也不覆盖当前状态
    COS.loading = false;
    COS.error = cosErrorMessage(err).message; // 失败保留表单状态
    cosRender();
  }
}

async function cosSearch() {
  const el = typeof document !== 'undefined' ? document.getElementById('cos_keyword') : null;
  COS.keyword = cosNormalizeKeyword(el ? el.value : COS.keyword);
  COS.page = 1;
  await cosLoadCandidates();
}

function cosPickCandidate(salesOrderId) {
  if (COS.readonly) { if (typeof toast === 'function') toast(COS_READONLY_TEXT, 'error'); return; }
  const candidate = (COS.candidates || []).find(c => Number(c.salesOrderId) === Number(salesOrderId));
  const selection = cosSelectionFromCandidate(candidate);
  if (!selection) {
    if (typeof toast === 'function') toast('该候选不可选（已取消 / 无效）：绝不臆造来源', 'error');
    return;
  }
  COS.selected = selection;
  cosFillForm(cosFormFill(selection));
  COS.result = `已回填来源销售订单 Id ${selection.salesOrderId}（不自动改数量 / 金额）`;
  cosRender();
}

function cosUnlinkSource() {
  if (COS.readonly) { if (typeof toast === 'function') toast(COS_READONLY_TEXT, 'error'); return; }
  COS.selected = null;
  cosFillForm(cosUnlinkFill());
  COS.result = '已显式断开来源链接（保存后生效；历史未关联语义保留）';
  cosRender();
}

function cosApplyToForm() {
  if (!COS.selected) { if (typeof toast === 'function') toast('请先选择一个可用的来源销售订单', 'error'); return; }
  cosFillForm(cosFormFill(COS.selected));
  COS.result = `已应用来源销售订单 Id ${COS.selected.salesOrderId}`;
  cosRender();
}

/* 客户变更：作废在途响应 + 清除先前的来源选择（绝不把旧客户来源留在新客户表单上） */
function cosOnCustomerChanged() {
  COS.requestSeq += 1;
  COS.customerId = cosCustomerId(cosFieldValue('customerId'));
  COS.selectedCustomerId = COS.customerId;
  COS.candidates = [];
  COS.total = 0;
  COS.stored = null;
  COS.selected = null;
  if (typeof document !== 'undefined') {
    const el = document.getElementById('f_salesOrderId');
    if (el && cosIsPositiveId(el.value)) {
      el.value = '';
      COS.result = '客户已变更：原来源选择已失效并清除，请重新选择来源';
    }
  }
  cosRender();
}

function cosInstallCustomerChangeHook() {
  if (typeof document === 'undefined') return false;
  const el = document.getElementById('f_customerId');
  if (!el || el.__cosWatched) return false;
  el.__cosWatched = true;
  el.addEventListener('change', cosOnCustomerChanged);
  el.addEventListener('input', cosOnCustomerChanged);
  return true;
}

/* 打开选择器：新建（0）与已保存单据都可用；重开时显式回显服务端持久化的来源 */
async function openComplaintSalesOrderSourcePicker(complaintId) {
  if (typeof document === 'undefined') return;
  const guard = cosGuardComplaintId(complaintId);
  if (guard) { if (typeof toast === 'function') toast(guard, 'error'); return; }

  const customerId = cosCustomerId(cosFieldValue('customerId'));
  COS.open = true;
  COS.complaintId = Number(complaintId) || 0;
  COS.customerId = customerId;
  COS.selectedCustomerId = customerId;
  COS.keyword = '';
  COS.page = 1;
  COS.pageSize = COS_PAGE_SIZE_DEFAULT;
  COS.total = 0;
  COS.candidates = [];
  COS.stored = null;
  COS.selected = null;
  COS.loading = false;
  COS.readonly = false;
  COS.error = '';
  COS.result = '';
  COS.requestSeq += 1;
  cosRender();

  if (!(customerId > 0)) {
    COS.error = '请先选择客户后再选择来源销售订单（候选严格按精确客户返回，绝不返回任意客户订单）';
    cosRender();
    return;
  }

  await cosLoadStatus(COS.complaintId);
  await cosLoadStoredSource(COS.complaintId);
  await cosLoadCandidates();
}

/* 表单接入：为 modules-finance 中声明 selector: 'complaint-sales-order-source' 的字段追加「选择来源」入口。
   只包裹既有全局函数，不新增模块 / 菜单，也不影响其它单据。 */
function cosSourceButtonHtml() {
  return `<div class="form-item full"><label>来源销售订单选择</label>`
    + `<button type="button" class="btn btn-neutral" onclick="openComplaintSalesOrderSourcePicker(window.__cosCurrentId || 0)">选择来源销售订单（按客户有界候选）</button>`
    + `<span class="text-muted" style="margin-left:8px">仅回填权威来源 Id；已取消订单不可选；可显式断开；不自动改数量 / 金额</span>`
    + `</div>`;
}

function cosInstallFormHook() {
  if (typeof window === 'undefined') return false;
  if (window.__cosHooked) return true;
  if (typeof window.fieldHtml !== 'function' || typeof window.openForm !== 'function') return false;

  const originalOpenForm = window.openForm;
  const wrappedOpenForm = function (id) {
    window.__cosCurrentId = (id === undefined || id === null || id === '') ? 0 : (Number(id) || 0);
    const result = originalOpenForm.apply(this, arguments);
    cosInstallCustomerChangeHook();
    return result;
  };
  wrappedOpenForm.__cosWrapped = true;
  window.openForm = wrappedOpenForm;

  const originalFieldHtml = window.fieldHtml;
  const wrappedFieldHtml = function (field) {
    const html = originalFieldHtml.apply(this, arguments);
    if (!field || field.selector !== 'complaint-sales-order-source') return html;
    return html + cosSourceButtonHtml();
  };
  wrappedFieldHtml.__cosWrapped = true;
  window.fieldHtml = wrappedFieldHtml;
  window.__cosHooked = true;
  return true;
}

if (typeof window !== 'undefined' && typeof window.fieldHtml === 'function') cosInstallFormHook();

/* Node 单测导出（浏览器中 module 为 undefined，自动跳过） */
if (typeof module !== 'undefined' && module.exports) {
  module.exports = {
    COS_EDITABLE_STATUS, COS_PAGE_SIZE_DEFAULT, COS_PAGE_SIZE_MAX, COS_READONLY_TEXT, COS_ERROR_PREFIX,
    cosNormalizeStatus, cosStatusText, cosIsEditableStatus, cosReadonlyReason,
    cosGuardComplaintId, cosIsPositiveId, cosCustomerId,
    cosNormalizePage, cosNormalizePageSize, cosNormalizeKeyword,
    cosAvailableCandidates, cosSelectionFromCandidate, cosSelectionMatchesCustomer,
    cosShouldInvalidateSelection, cosShouldAcceptResponse, cosFormFill, cosUnlinkFill,
    cosCandidateLabel, cosEsc, cosCandidateRowHtml, cosStoredSourceHtml, cosErrorHtml, cosErrorMessage,
    cosSourceButtonHtml, cosInstallFormHook, cosInstallCustomerChangeHook, cosOnCustomerChanged,
    openComplaintSalesOrderSourcePicker, cosApplyToForm, cosUnlinkSource, cosPickCandidate,
    cosSearch, cosLoadCandidates, cosLoadStoredSource, cosLoadStatus,
  };
}




