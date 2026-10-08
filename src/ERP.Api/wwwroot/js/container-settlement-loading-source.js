/* ==================================================================================
   ====== 装柜结算单 → 来源装柜清单 显式选择器（ERP-392） ======
   ==================================================================================
   定位：把装柜结算单（`container-settlement`）的「来源装柜清单」从「只能手工填 Id」升级为**显式有界选择**：
        服务端只读分页候选接口在既有「装柜结算单」+「装柜清单」菜单与实时客户数据范围之内，按**精确客户**返回
        该客户确实是权威归属客户（有效参与方之一或历史兼容客户）且未删除、未取消的装柜清单候选，
        操作员显式选定后表单只回填**权威来源 Id**（共享柜越范围 / 已取消候选不可选）。
   规则（与 ContainerSettlementLoadingSourceService / FinanceContainerSettlementLifecycleRules 同口径，服务端为唯一权威）：
     1. 只接受**显式选择**的来源装柜清单 Id：候选全部来自服务端有界只读分页查询，
        **绝不按柜号 / 单号文本 / 相似度猜测来源**，也绝不提供按猜测 Id 直取的旁路；
     2. 候选只含当前账号客户数据范围之内、**精确客户**确实是权威归属客户的「未删除、未取消」装柜清单；
        共享柜里其他越范围客户 / 越范围上游出运一律整张不返回（绝不复用越范围共享柜）；
     3. **客户变更即失效**：切换客户会清除先前的来源选择（绝不让旧上下文的来源留在新表单上）；
     4. **异步响应不得回填陈旧上下文结果**：候选请求带序号，客户 / 关键字变化后旧响应一律丢弃；
     5. **重开**：既有结算单的来源 Id 由既有接口持久化并原样回显；来源已取消 / 不可用时仍以显式文案标注展示
        （绝不静默清除 / 重绑定）；表单可**显式断开**链接（留空 → 保存为未关联，仅待提交可改）；
     6. 只回填来源 Id，**绝不自动改动结算单的总额 / 海运费 / 其他费用**，也绝不臆造柜号 / 清单号；
     7. 失败（网络 / 权限 / 服务端）保留表单既有输入并显式提示，不清空用户已填内容。
   边界：本选择器只做只读候选展示与来源 Id 回填：不锁库、不写库存 / 流水 / 财务、不改写清单 / 明细 / 参与方 / 客户主数据、
         不执行任何结算计算 / 分摊 / 记账 / 核销，也不新增表 / 菜单 / 用户授权；最终保存仍由服务端按
         FinanceContainerSettlementLifecycleRules 在锁内复核精确来源。
   ================================================================================== */

/* 只有「待提交」可维护来源（与后端 FinanceContainerSettlementController 的状态判定一致） */
const CSLS_EDITABLE_STATUS = 'Pending';
const CSLS_STATUS_TEXT = {
  Pending: '待提交（草稿）', Submitted: '已提交', Approved: '已审核',
  Rejected: '已驳回', Completed: '已完成', Cancelled: '已取消',
};
const CSLS_PAGE_SIZE_DEFAULT = 20;
const CSLS_PAGE_SIZE_MAX = 100;
const CSLS_READONLY_TEXT = '当前装柜结算单不是「待提交（草稿）」：来源只读，如需更换请先另建待提交结算单。';
const CSLS_ERROR_PREFIX = { network: '无法连接服务器', unauthorized: '登录已过期或权限不足', server: '服务端拒绝' };
const CSLS_API_BASE = '/api/finance/container-settlements';

/* 一次对话 = 一个结算单表单；candidates 为服务端权威候选，stored / selected 为来源链接状态 */
let CSLS = {
  settlementId: 0, status: '', customerId: 0, selectedContext: null,
  keyword: '', page: 1, pageSize: CSLS_PAGE_SIZE_DEFAULT, total: 0,
  candidates: [], stored: null, selected: null,
  loading: false, readonly: false, error: '', result: '', open: false, requestSeq: 0,
};

/* 状态归一化：既接受后端枚举名（Pending），也接受数字与已有中文文案 */
function cslsNormalizeStatus(status) {
  if (status === null || status === undefined || status === '') return '';
  const raw = String(status).trim();
  if (CSLS_STATUS_TEXT[raw]) return raw;
  const byNumber = { '0': 'Pending', '1': 'Submitted', '2': 'Approved', '3': 'Rejected', '4': 'Completed', '5': 'Cancelled' };
  if (byNumber[raw]) return byNumber[raw];
  const hit = Object.keys(CSLS_STATUS_TEXT).find(k => CSLS_STATUS_TEXT[k] === raw);
  return hit || raw;
}

function cslsStatusText(status) {
  const key = cslsNormalizeStatus(status);
  return CSLS_STATUS_TEXT[key] || (key || '未知');
}

function cslsIsEditableStatus(status) {
  return cslsNormalizeStatus(status) === CSLS_EDITABLE_STATUS;
}

function cslsReadonlyReason(status) {
  return cslsIsEditableStatus(status) ? '' : CSLS_READONLY_TEXT;
}

/* 结算单 Id 闸门：0 = 新建；负数 / 非数字一律拒绝（绝不臆造单据） */
function cslsGuardSettlementId(settlementId) {
  if (settlementId === null || settlementId === undefined || settlementId === '') return '';
  const n = Number(settlementId);
  if (!Number.isInteger(n) || n < 0) return '装柜结算单 Id 非法：拒绝选择来源（绝不臆造单据）';
  return '';
}

/* 正整数判定（Id 一律用 Number 解析，绝不用 parseInt / parseFloat 从文本里抠） */
function cslsIsPositiveId(value) {
  if (value === null || value === undefined || value === '') return false;
  const n = Number(value);
  return Number.isInteger(n) && n > 0;
}

function cslsCustomerId(value) {
  const n = Number(value);
  return Number.isInteger(n) && n > 0 ? n : 0;
}

/* 分页归一化（与服务端 NormalizePage / NormalizePageSize 同口径） */
function cslsNormalizePage(page) {
  const n = Number(page);
  if (!Number.isFinite(n) || n < 1) return 1;
  return Math.floor(n);
}

function cslsNormalizePageSize(pageSize) {
  const n = Number(pageSize);
  if (!Number.isFinite(n) || n < 1) return CSLS_PAGE_SIZE_DEFAULT;
  return Math.min(Math.floor(n), CSLS_PAGE_SIZE_MAX);
}

/* 关键字归一化：去首尾空白并截断（与服务端 NormalizeKeyword 同口径，先归一化再计数） */
function cslsNormalizeKeyword(keyword) {
  const text = keyword === null || keyword === undefined ? '' : String(keyword).trim();
  return text.length <= 100 ? text : text.slice(0, 100);
}

/* 可选择的候选：仅服务端标记 eligible = true（已取消 / 越范围绝不出现在候选里，仍双保险过滤） */
function cslsAvailableCandidates(candidates) {
  return (candidates || []).filter(c => c && c.eligible === true && cslsIsPositiveId(c.loadingListId));
}

/* 候选 → 显式选择（不可用 / 无 Id 一律返回 null，绝不臆造来源） */
function cslsSelectionFromCandidate(candidate) {
  if (!candidate || candidate.eligible !== true) return null;
  if (!cslsIsPositiveId(candidate.loadingListId)) return null;
  return {
    loadingListId: candidate.loadingListId,
    loadingListNo: candidate.loadingListNo || '',
    customerId: cslsCustomerId(candidate.customerId),
  };
}

/* 已选来源是否仍属于当前上下文（精确客户变更即失效的依据） */
function cslsSelectionMatchesContext(selection, customerId) {
  if (!selection || !cslsIsPositiveId(selection.loadingListId)) return false;
  const currentCustomer = cslsCustomerId(customerId);
  if (!(currentCustomer > 0)) return false;
  return Number(selection.customerId) === currentCustomer;
}

/* 客户变更后是否需要让先前的来源选择失效（有选择且不再匹配当前上下文） */
function cslsShouldInvalidateSelection(selection, currentCustomerId) {
  if (!selection || !cslsIsPositiveId(selection.loadingListId)) return false;
  return !cslsSelectionMatchesContext(selection, currentCustomerId);
}

/* 陈旧异步响应判定：请求序号不一致、或响应上下文已不是当前上下文，一律丢弃 */
function cslsShouldAcceptResponse(seq, currentSeq, responseCustomerId, currentCustomerId) {
  if (seq !== currentSeq) return false;
  const current = cslsCustomerId(currentCustomerId);
  if (!(current > 0)) return false;
  return cslsCustomerId(responseCustomerId) === current;
}

/* 应用到表单的值：只回填来源 Id（不臆造柜号 / 清单号，不改总额 / 费用；无选择 → 留空显式断开） */
function cslsFormFill(selection) {
  if (!selection || !cslsIsPositiveId(selection.loadingListId)) return { loadingListId: '' };
  return { loadingListId: selection.loadingListId };
}

function cslsUnlinkFill() {
  return { loadingListId: '' };
}


/* 候选展示文案：清单号 / 日期 / 柜号 / 客户 / 状态 / 参与方 */
function cslsCandidateLabel(candidate) {
  if (!candidate) return '';
  const parts = [
    candidate.loadingListNo ? `装柜清单 ${candidate.loadingListNo}` : `装柜清单 Id ${candidate.loadingListId ?? ''}`,
    candidate.customerName ? `客户 ${candidate.customerName}` : '',
    candidate.loadingDate ? `日期 ${String(candidate.loadingDate).slice(0, 10)}` : '',
    candidate.containerNo ? `柜号 ${candidate.containerNo}` : '',
    `状态 ${cslsStatusText(candidate.status)}`,
    `有效参与方 ${candidate.activeParticipantCount ?? 0}`,
    candidate.eligible === true ? '可显式选择' : (candidate.ineligibleReason || '不可选'),
  ];
  return parts.filter(Boolean).join(' / ');
}

/* 安全转义（浏览器复用全局 escapeHtml；Node 单测走本地实现，绝不注入） */
function cslsEsc(value) {
  const s = value === null || value === undefined ? '' : String(value);
  if (typeof escapeHtml === 'function') return escapeHtml(s);
  return s.replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

/* 候选行：清单号 / 日期 / 柜号 / 客户 / 状态 / 参与方 / 可用性 + 显式选择入口（不可选行禁用） */
function cslsCandidateRowHtml(candidate) {
  if (!candidate) return '';
  const eligible = candidate.eligible === true && cslsIsPositiveId(candidate.loadingListId);
  const action = eligible
    ? `<button type="button" class="btn btn-neutral btn-sm" onclick="cslsPickCandidate(${cslsEsc(candidate.loadingListId)})">选择此装柜清单</button>`
    : `<span class="text-muted">不可选</span>`;
  return `<tr${eligible ? '' : ' class="text-muted"'}>
    <td>${cslsEsc(candidate.loadingListId)}</td>
    <td>${cslsEsc(candidate.loadingListNo || '')}</td>
    <td>${cslsEsc(candidate.loadingDate ? String(candidate.loadingDate).slice(0, 10) : '')}</td>
    <td>${cslsEsc(candidate.containerNo || '')}</td>
    <td>${cslsEsc(candidate.customerName || '')}</td>
    <td>${cslsEsc(cslsStatusText(candidate.status))}</td>
    <td>${cslsEsc(candidate.activeParticipantCount ?? 0)}</td>
    <td>${eligible ? '可用' : cslsEsc(candidate.ineligibleReason || '不可用')}</td>
    <td>${action}</td>
  </tr>`;
}

/* 已存储来源只读展示：显式标注未关联 / 已关联 / 来源已取消 / 来源不可用（历史原样保留） */
function cslsStoredSourceHtml(view) {
  if (!view || view.linked !== true) {
    return `<div class="csls-stored" style="padding:6px 8px;color:#6b7280">当前未关联来源装柜清单（历史未关联语义原样保留，绝不回填）</div>`;
  }
  const status = view.status ? `（状态 ${cslsStatusText(view.status)}${view.eligibleForNewLink === true ? '，仍可作为新来源' : '，只读保留'}）` : '';
  const listNo = view.loadingListNo ? `「${cslsEsc(view.loadingListNo)}」` : `Id ${cslsEsc(view.loadingListId)}`;
  return `<div class="csls-stored" style="padding:6px 8px;background:#f9fafb;border:1px solid #e5e7eb;border-radius:8px">`
    + `已存储来源 ${listNo}${cslsEsc(status)}：${cslsEsc(view.annotation || '')}</div>`;
}

function cslsErrorHtml(kind, message) {
  const prefix = CSLS_ERROR_PREFIX[kind] || '操作失败';
  return `<div class="csls-error" role="alert" style="padding:8px 10px;background:#fef2f2;border:1px solid #fecaca;border-radius:8px;color:#b91c1c;font-size:13px;margin-bottom:8px">`
    + `${cslsEsc(prefix)}：${cslsEsc(message || '')}</div>`;
}

function cslsErrorMessage(err) {
  const message = err && err.message ? err.message : String(err || '');
  if (/Failed to fetch|NetworkError|network|无法连接/i.test(message)) return { kind: 'network', message };
  if (/权限|授权|未认证|登录|禁止/.test(message)) return { kind: 'unauthorized', message };
  return { kind: 'server', message };
}


/* ==================== 浏览器接线（Node 单测只使用上面的纯函数） ==================== */

function cslsFieldValue(key) {
  const el = document.getElementById('f_' + key);
  return el ? el.value : '';
}

/* 只回填来源 Id 字段：无选择 / 显式断开 → 留空（保存时服务端归一化为 null） */
function cslsFillForm(fill) {
  const el = typeof document !== 'undefined' ? document.getElementById('f_loadingListId') : null;
  if (!el) return;
  const value = fill && fill.loadingListId;
  el.value = cslsIsPositiveId(value) ? value : '';
}

function cslsModalHtml() {
  const warning = CSLS.readonly ? cslsErrorHtml('server', CSLS_READONLY_TEXT) : '';
  const error = CSLS.error ? cslsErrorHtml(cslsErrorMessage({ message: CSLS.error }).kind, CSLS.error) : '';
  const result = CSLS.result ? `<div class="text-muted" role="status" style="margin:6px 0">${cslsEsc(CSLS.result)}</div>` : '';
  const rows = (CSLS.candidates || []).map(cslsCandidateRowHtml).join('')
    || `<tr><td colspan="9" class="text-muted">没有匹配的来源装柜清单候选（严格按精确客户 + 客户数据范围返回）</td></tr>`;
  return `<div class="modal modal-lg">
    <div class="modal-head"><span>选择来源装柜清单（装柜结算单）</span><button type="button" class="btn btn-neutral btn-sm" onclick="cslsCloseModal()">关闭</button></div>
    <div class="modal-body">
      ${warning}${error}${result}
      <div style="margin-bottom:8px">客户 Id：<b>${cslsEsc(CSLS.customerId)}</b>；只显示该客户确实是权威归属客户（有效参与方或历史兼容客户）且未删除、未取消的装柜清单（共享柜越范围整张不返回）</div>
      <div style="margin-bottom:8px">
        <input type="text" id="csls_keyword" value="${cslsEsc(CSLS.keyword)}" placeholder="按装柜清单号 / 柜号搜索" style="width:260px">
        <button type="button" class="btn btn-neutral btn-sm" onclick="cslsSearch()">搜索</button>
        <button type="button" class="btn btn-neutral btn-sm" onclick="cslsUnlinkSource()">显式断开来源链接</button>
      </div>
      <div style="margin-bottom:8px">${cslsStoredSourceHtml(CSLS.stored)}</div>
      <table class="data-table">
        <thead><tr><th>Id</th><th>装柜清单号</th><th>日期</th><th>柜号</th><th>客户</th><th>状态</th><th>有效参与方</th><th>可用性</th><th>操作</th></tr></thead>
        <tbody>${rows}</tbody>
      </table>
      <div class="text-muted" style="margin-top:6px">共 ${cslsEsc(CSLS.total)} 条候选；每页最多 ${cslsEsc(CSLS.pageSize)} 条。只回填来源 Id，绝不自动改动结算单的总额 / 费用。</div>
    </div>
  </div>`;
}

function cslsRender() {
  if (typeof document === 'undefined' || !CSLS.open) return;
  const modal = document.getElementById('modal');
  if (!modal) return;
  modal.innerHTML = cslsModalHtml();
  modal.style.display = 'flex';
}

function cslsCloseModal() {
  CSLS.open = false;
  CSLS.error = '';
  if (typeof closeModal === 'function') closeModal();
}

async function cslsLoadStatus(settlementId) {
  CSLS.status = '';
  CSLS.readonly = false;
  if (!(settlementId > 0)) return;
  try {
    const row = await api(`${CSLS_API_BASE}/${settlementId}`);
    CSLS.status = cslsNormalizeStatus(row && row.status);
    CSLS.readonly = !cslsIsEditableStatus(CSLS.status);
  } catch (err) {
    CSLS.readonly = true;
    CSLS.error = cslsErrorMessage(err).message;
  }
}

async function cslsLoadStoredSource(settlementId) {
  CSLS.stored = null;
  if (!(settlementId > 0)) return;
  try {
    CSLS.stored = await api(`${CSLS_API_BASE}/${settlementId}/loading-list-source`) || null;
  } catch (err) {
    CSLS.stored = null;
    CSLS.error = cslsErrorMessage(err).message;   // 只读展示失败显式可见，保留表单状态
  }
  cslsRender();
}


async function cslsLoadCandidates() {
  const seq = ++CSLS.requestSeq;
  const requestCustomerId = CSLS.customerId;
  CSLS.loading = true;
  CSLS.error = '';
  cslsRender();
  const params = ['customerId=' + encodeURIComponent(requestCustomerId)];
  if (CSLS.keyword) params.push('keyword=' + encodeURIComponent(CSLS.keyword));
  params.push('page=' + encodeURIComponent(cslsNormalizePage(CSLS.page)));
  params.push('pageSize=' + encodeURIComponent(cslsNormalizePageSize(CSLS.pageSize)));
  try {
    const res = await api(`${CSLS_API_BASE}/loading-list-candidates?${params.join('&')}`);
    // 陈旧响应（客户已切换 / 关键字已变化）一律丢弃，绝不回填陈旧客户结果
    if (!cslsShouldAcceptResponse(seq, CSLS.requestSeq, requestCustomerId, CSLS.customerId)) return;
    CSLS.page = res && res.page ? cslsNormalizePage(res.page) : CSLS.page;
    CSLS.pageSize = cslsNormalizePageSize(res && res.pageSize ? res.pageSize : CSLS.pageSize);
    CSLS.total = res && Number.isFinite(Number(res.total)) ? Number(res.total) : 0;
    CSLS.candidates = (res && res.items) || [];
    CSLS.loading = false;
    cslsRender();
  } catch (err) {
    if (seq !== CSLS.requestSeq) return;      // 陈旧失败也不覆盖当前状态
    CSLS.loading = false;
    CSLS.error = cslsErrorMessage(err).message; // 失败保留表单状态
    cslsRender();
  }
}

async function cslsSearch() {
  const el = typeof document !== 'undefined' ? document.getElementById('csls_keyword') : null;
  CSLS.keyword = cslsNormalizeKeyword(el ? el.value : CSLS.keyword);
  CSLS.page = 1;
  await cslsLoadCandidates();
}

function cslsPickCandidate(loadingListId) {
  if (CSLS.readonly) { if (typeof toast === 'function') toast(CSLS_READONLY_TEXT, 'error'); return; }
  const candidate = (CSLS.candidates || []).find(c => Number(c.loadingListId) === Number(loadingListId));
  const selection = cslsSelectionFromCandidate(candidate);
  if (!selection) {
    if (typeof toast === 'function') toast('该候选不可选（已取消 / 越范围 / 无效）：绝不臆造来源', 'error');
    return;
  }
  CSLS.selected = selection;
  cslsFillForm(cslsFormFill(selection));
  CSLS.result = `已回填来源装柜清单 Id ${selection.loadingListId}（不自动改动结算单的总额 / 费用）`;
  cslsRender();
}

function cslsUnlinkSource() {
  if (CSLS.readonly) { if (typeof toast === 'function') toast(CSLS_READONLY_TEXT, 'error'); return; }
  CSLS.selected = null;
  cslsFillForm(cslsUnlinkFill());
  CSLS.result = '已显式断开来源链接（保存后生效；历史未关联语义保留）';
  cslsRender();
}

function cslsApplyToForm() {
  if (!CSLS.selected) { if (typeof toast === 'function') toast('请先选择一个可用的来源装柜清单', 'error'); return; }
  cslsFillForm(cslsFormFill(CSLS.selected));
  CSLS.result = `已应用来源装柜清单 Id ${CSLS.selected.loadingListId}`;
  cslsRender();
}

/* 客户变更：作废在途响应 + 清除先前的来源选择（绝不把旧上下文来源留在新表单上） */
function cslsOnContextChanged() {
  CSLS.requestSeq += 1;
  CSLS.customerId = cslsCustomerId(cslsFieldValue('customerId'));
  CSLS.selectedContext = { customerId: CSLS.customerId };
  CSLS.candidates = [];
  CSLS.total = 0;
  CSLS.stored = null;
  CSLS.selected = null;
  if (typeof document !== 'undefined') {
    const el = document.getElementById('f_loadingListId');
    if (el && cslsIsPositiveId(el.value)) {
      el.value = '';
      CSLS.result = '客户已变更：原来源选择已失效并清除，请重新选择来源';
    }
  }
  cslsRender();
}

function cslsInstallContextChangeHook() {
  if (typeof document === 'undefined') return false;
  let installed = false;
  ['customerId'].forEach(key => {
    const el = document.getElementById('f_' + key);
    if (!el || el.__cslsWatched) return;
    el.__cslsWatched = true;
    el.addEventListener('change', cslsOnContextChanged);
    el.addEventListener('input', cslsOnContextChanged);
    installed = true;
  });
  return installed;
}


/* 打开选择器：新建（0）与已保存单据都可用；重开时显式回显服务端持久化的来源 */
async function openContainerSettlementLoadingSourcePicker(settlementId) {
  if (typeof document === 'undefined') return;
  const guard = cslsGuardSettlementId(settlementId);
  if (guard) { if (typeof toast === 'function') toast(guard, 'error'); return; }

  const customerId = cslsCustomerId(cslsFieldValue('customerId'));

  CSLS.settlementId = Number(settlementId) || 0;
  CSLS.customerId = customerId;
  CSLS.selectedContext = { customerId };
  CSLS.keyword = '';
  CSLS.page = 1;
  CSLS.pageSize = CSLS_PAGE_SIZE_DEFAULT;
  CSLS.total = 0;
  CSLS.candidates = [];
  CSLS.stored = null;
  CSLS.selected = null;
  CSLS.loading = false;
  CSLS.readonly = false;
  CSLS.error = '';
  CSLS.result = '';
  CSLS.open = true;
  CSLS.requestSeq += 1;
  cslsRender();

  if (!(customerId > 0)) {
    CSLS.error = '请先选择客户后再选择来源装柜清单（候选严格按精确客户返回，绝不返回任意客户清单）';
    cslsRender();
    return;
  }

  await cslsLoadStatus(CSLS.settlementId);
  await cslsLoadStoredSource(CSLS.settlementId);
  await cslsLoadCandidates();
}

/* 表单接入：为 modules-finance 中声明 selector: 'container-settlement-loading-source' 的字段追加「选择来源」入口。
   只包裹既有全局函数，不新增模块 / 菜单，也不影响其它单据。 */
function cslsSourceButtonHtml() {
  return `<div class="form-item full"><label>来源装柜清单选择</label>`
    + `<button type="button" class="btn btn-neutral" onclick="openContainerSettlementLoadingSourcePicker(window.__cslsCurrentId || 0)">选择来源装柜清单（装柜结算单：按客户 + 数据范围有界候选）</button>`
    + `<span class="text-muted" style="margin-left:8px">仅回填权威来源 Id；共享柜越范围 / 已取消不可选；可显式断开；不自动改动总额 / 费用</span>`
    + `</div>`;
}

function cslsInstallFormHook() {
  if (typeof window === 'undefined') return false;
  if (window.__cslsHooked) return true;
  if (typeof window.fieldHtml !== 'function' || typeof window.openForm !== 'function') return false;

  const originalOpenForm = window.openForm;
  const wrappedOpenForm = function (id) {
    window.__cslsCurrentId = (id === undefined || id === null || id === '') ? 0 : (Number(id) || 0);
    const result = originalOpenForm.apply(this, arguments);
    cslsInstallContextChangeHook();
    return result;
  };
  wrappedOpenForm.__cslsWrapped = true;
  window.openForm = wrappedOpenForm;

  const originalFieldHtml = window.fieldHtml;
  const wrappedFieldHtml = function (field) {
    const html = originalFieldHtml.apply(this, arguments);
    if (!field || field.selector !== 'container-settlement-loading-source') return html;
    return html + cslsSourceButtonHtml();
  };
  wrappedFieldHtml.__cslsWrapped = true;
  window.fieldHtml = wrappedFieldHtml;
  window.__cslsHooked = true;
  return true;
}

if (typeof window !== 'undefined' && typeof window.fieldHtml === 'function') cslsInstallFormHook();

/* Node 单测导出（浏览器中 module 为 undefined，自动跳过） */
if (typeof module !== 'undefined' && module.exports) {
  module.exports = {
    CSLS_EDITABLE_STATUS, CSLS_PAGE_SIZE_DEFAULT, CSLS_PAGE_SIZE_MAX, CSLS_READONLY_TEXT,
    CSLS_ERROR_PREFIX, CSLS_API_BASE,
    cslsNormalizeStatus, cslsStatusText, cslsIsEditableStatus, cslsReadonlyReason,
    cslsGuardSettlementId, cslsIsPositiveId, cslsCustomerId,
    cslsNormalizePage, cslsNormalizePageSize, cslsNormalizeKeyword,
    cslsAvailableCandidates, cslsSelectionFromCandidate, cslsSelectionMatchesContext,
    cslsShouldInvalidateSelection, cslsShouldAcceptResponse, cslsFormFill, cslsUnlinkFill,
    cslsCandidateLabel, cslsEsc, cslsCandidateRowHtml, cslsStoredSourceHtml, cslsErrorHtml, cslsErrorMessage,
    cslsSourceButtonHtml, cslsInstallFormHook, cslsInstallContextChangeHook, cslsOnContextChanged,
    openContainerSettlementLoadingSourcePicker, cslsApplyToForm, cslsUnlinkSource, cslsPickCandidate,
    cslsSearch, cslsLoadCandidates, cslsLoadStoredSource, cslsLoadStatus,
  };
}

