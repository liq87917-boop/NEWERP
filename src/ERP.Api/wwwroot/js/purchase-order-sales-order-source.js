/* ==================================================================================
   ====== 采购订单 → 归属来源销售订单 显式选择器（ERP-393） ======
   ==================================================================================
   定位：把采购订单表单的「归属销售订单 ID / 归属销售订单号」从「手工填 Id + 手改单号」升级为**显式有界选择**：
        服务端只读候选接口在当前账号客户数据范围之内返回「已审核、未删除、未取消」的销售订单候选，
        操作员显式选定后，表单按**权威来源**回填归属销售订单 Id / 单号与归属客户 Id / 名称。
   规则（与 PurchaseOrderSalesOrderSourceService / PurchaseSalesOrderLinkRules 同口径，服务端为唯一权威）：
     1. 只接受**显式选择**的来源销售订单 Id：候选全部来自服务端有界只读分页查询，
        **绝不按订单号 / 单号文本 / 相似度猜测来源**，也绝不提供按猜测 Id 直取的旁路；
     2. 候选只含当前账号客户数据范围之内、「已审核、未删除、未取消」的销售订单；可选按精确归属客户收窄；
        **不要求币种一致**（币种仅供展示，绝不换算）；
     3. **归属客户变更即失效**：切换 / 清空归属客户后，先前的来源选择失效并清除（绝不把旧上下文来源留在新表单上）；
     4. **异步响应不得回填陈旧上下文结果**：候选请求带序号，客户 / 关键字变化后旧响应一律丢弃；
     5. **重开**：既有采购订单的来源 Id 由既有接口持久化并原样回显；来源已取消 / 不可用时仍以显式文案标注展示
        （绝不静默清除 / 重绑定）；表单可**显式断开**链接（留空 → 保存为未关联，仅待提交可改）；
     6. **金额不改**：只回填归属来源与归属客户字段，**绝不自动改动币种 / 汇率 / 单价 / 金额 / 明细**；
     7. 失败（网络 / 权限 / 服务端）保留表单既有输入并显式提示，不清空用户已填内容。
   边界：本选择器只做只读候选展示与归属来源回填：不锁库、不写库存 / 流水 / 财务、不改写订单 / 客户主数据，
         不执行任何价格 / 金额计算，也不新增表 / 菜单 / 用户授权；最终保存仍由服务端按
         PurchaseSalesOrderLinkRules.ApplyLinkAsync 复核精确来源、权威客户与商品 / 单位兼容性。
   ================================================================================== */

/* 只有「待提交」可维护归属来源（与后端 PurchaseOrderController 的状态判定一致） */
const POS_EDITABLE_STATUS = 'Pending';
const POS_STATUS_TEXT = {
  Pending: '待提交（草稿）', Submitted: '已提交', Approved: '已审核',
  Rejected: '已驳回', Completed: '已完成', Cancelled: '已取消',
};
const POS_PAGE_SIZE_DEFAULT = 20;
const POS_PAGE_SIZE_MAX = 100;
const POS_API_BASE = '/api/purchase-orders';
const POS_READONLY_TEXT = '当前采购订单不是「待提交（草稿）」：归属来源只读，如需更换请先销审 / 另建待提交单据。';
const POS_ERROR_PREFIX = { network: '无法连接服务器', unauthorized: '登录已过期或权限不足', server: '服务端拒绝' };
const POS_UNLINK_TEXT = '当前未关联来源销售订单（历史未关联语义原样保留，绝不回填）';

/* 选择器写入表单的字段（服务端权威回填；绝不多写币种 / 汇率 / 单价 / 金额 / 明细） */
const POS_WRITTEN_FIELDS = ['owningSalesOrderId', 'owningSalesOrderNo', 'owningCustomerId', 'owningCustomerName'];

/* 一次对话 = 一个采购订单表单；candidates 为服务端权威候选，stored / selected 为来源链接状态 */
let POS = {
  orderId: 0, status: '', customerId: 0, keyword: '', page: 1, pageSize: POS_PAGE_SIZE_DEFAULT, total: 0,
  candidates: [], stored: null, selected: null,
  loading: false, readonly: false, error: '', result: '', open: false, requestSeq: 0,
};

/* 状态归一化：既接受后端枚举名（Pending），也接受数字与已有中文文案 */
function posNormalizeStatus(status) {
  if (status === null || status === undefined || status === '') return '';
  const raw = String(status).trim();
  if (POS_STATUS_TEXT[raw]) return raw;
  const byNumber = { '0': 'Pending', '1': 'Submitted', '2': 'Approved', '3': 'Rejected', '4': 'Completed', '5': 'Cancelled' };
  if (byNumber[raw]) return byNumber[raw];
  const hit = Object.keys(POS_STATUS_TEXT).find(k => POS_STATUS_TEXT[k] === raw);
  return hit || raw;
}

function posStatusText(status) {
  const key = posNormalizeStatus(status);
  return POS_STATUS_TEXT[key] || (key || '未知');
}

function posIsEditableStatus(status) {
  return posNormalizeStatus(status) === POS_EDITABLE_STATUS;
}

function posReadonlyReason(status) {
  return posIsEditableStatus(status) ? '' : POS_READONLY_TEXT;
}

/* 采购订单 Id 闸门：0 = 新建；负数 / 非数字一律拒绝（绝不臆造单据） */
function posGuardOrderId(orderId) {
  if (orderId === null || orderId === undefined || orderId === '') return '';
  const n = Number(orderId);
  if (!Number.isInteger(n) || n < 0) return '采购订单 Id 非法：拒绝选择来源（绝不臆造单据）';
  return '';
}

/* 正整数判定（Id 一律用 Number 解析，绝不用 parseInt / parseFloat 从文本里抠） */
function posIsPositiveId(value) {
  if (value === null || value === undefined || value === '') return false;
  const n = Number(value);
  return Number.isInteger(n) && n > 0;
}

function posCustomerId(value) {
  const n = Number(value);
  return Number.isInteger(n) && n > 0 ? n : 0;
}

/* 分页归一化（与服务端 NormalizePage / NormalizePageSize 同口径） */
function posNormalizePage(page) {
  const n = Number(page);
  if (!Number.isFinite(n) || n < 1) return 1;
  return Math.floor(n);
}

function posNormalizePageSize(pageSize) {
  const n = Number(pageSize);
  if (!Number.isFinite(n) || n < 1) return POS_PAGE_SIZE_DEFAULT;
  return Math.min(Math.floor(n), POS_PAGE_SIZE_MAX);
}

/* 关键字归一化：去首尾空白并截断（与服务端 NormalizeKeyword 同口径，先归一化再计数） */
function posNormalizeKeyword(keyword) {
  const text = keyword === null || keyword === undefined ? '' : String(keyword).trim();
  return text.length <= 100 ? text : text.slice(0, 100);
}

/* 可选择的候选：仅服务端标记 eligible = true（未审核 / 已取消 / 客户缺失绝不可选） */
function posAvailableCandidates(candidates) {
  return (candidates || []).filter(c => c && c.eligible === true && posIsPositiveId(c.salesOrderId));
}

/* 候选 → 显式选择（不可用 / 无 Id 一律返回 null，绝不臆造来源） */
function posSelectionFromCandidate(candidate) {
  if (!candidate || candidate.eligible !== true) return null;
  if (!posIsPositiveId(candidate.salesOrderId)) return null;
  return {
    salesOrderId: candidate.salesOrderId,
    orderNo: candidate.orderNo || '',
    customerId: posCustomerId(candidate.customerId),
    customerName: candidate.customerName || '',
  };
}

/* 已选来源是否仍属于当前上下文（归属客户一致；客户变更 / 清空即失效的依据） */
function posSelectionMatchesContext(selection, customerId) {
  if (!selection || !posIsPositiveId(selection.salesOrderId)) return false;
  const current = posCustomerId(customerId);
  if (!(current > 0)) return false;
  return posCustomerId(selection.customerId) === current;
}

/* 归属客户变更后是否需要让先前的来源选择失效（有选择且不再匹配当前归属客户） */
function posShouldInvalidateSelection(selection, customerId) {
  if (!selection || !posIsPositiveId(selection.salesOrderId)) return false;
  return !posSelectionMatchesContext(selection, customerId);
}

/* 陈旧异步响应判定：请求序号不一致、或响应归属客户已不是当前归属客户，一律丢弃（绝不回填陈旧上下文结果） */
function posShouldAcceptResponse(seq, currentSeq, responseCustomerId, currentCustomerId) {
  if (seq !== currentSeq) return false;
  return posCustomerId(responseCustomerId) === posCustomerId(currentCustomerId);
}

/* 应用到表单的值：回填权威归属来源 + 归属客户（绝不回填币种 / 汇率 / 单价 / 金额 / 明细） */
function posFormFill(selection) {
  if (!selection || !posIsPositiveId(selection.salesOrderId)) {
    return { owningSalesOrderId: '', owningSalesOrderNo: '' };
  }
  return {
    owningSalesOrderId: selection.salesOrderId,
    owningSalesOrderNo: selection.orderNo || '',
    owningCustomerId: posCustomerId(selection.customerId),
    owningCustomerName: selection.customerName || '',
  };
}

/* 显式断开：只清空归属来源链接，保留用户已填的归属客户字段（历史未关联语义保留） */
function posUnlinkFill() {
  return { owningSalesOrderId: '', owningSalesOrderNo: '' };
}

/* 候选展示文案：订单号 / 日期 / 客户 / 币种 / 状态 / 可用性 */
function posCandidateLabel(candidate) {
  if (!candidate) return '';
  const parts = [
    candidate.orderNo ? `订单 ${candidate.orderNo}` : `订单 Id ${candidate.salesOrderId ?? ''}`,
    candidate.customerName ? `客户 ${candidate.customerName}` : '',
    candidate.orderDate ? `日期 ${String(candidate.orderDate).slice(0, 10)}` : '',
    candidate.currency ? `币种 ${String(candidate.currency).toUpperCase()}` : '',
    `状态 ${posStatusText(candidate.status)}`,
    candidate.eligible === true ? '可显式选择' : (candidate.ineligibleReason || '不可选'),
  ];
  return parts.filter(Boolean).join(' / ');
}

/* 安全转义（浏览器复用全局 escapeHtml；Node 单测走本地实现，绝不注入） */
function posEsc(value) {
  const s = value === null || value === undefined ? '' : String(value);
  if (typeof escapeHtml === 'function') return escapeHtml(s);
  return s.replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

/* 候选行：订单号 / 日期 / 客户 / 币种 / 状态 / 可用性 + 显式选择入口（不可选行禁用） */
function posCandidateRowHtml(candidate) {
  if (!candidate) return '';
  const eligible = candidate.eligible === true && posIsPositiveId(candidate.salesOrderId);
  const action = eligible
    ? `<button type="button" class="btn btn-neutral btn-sm" onclick="posPickCandidate(${posEsc(candidate.salesOrderId)})">选择此订单</button>`
    : `<span class="text-muted">不可选</span>`;
  return `<tr${eligible ? '' : ' class="text-muted"'}>
    <td>${posEsc(candidate.salesOrderId)}</td>
    <td>${posEsc(candidate.orderNo || '')}</td>
    <td>${posEsc(candidate.orderDate ? String(candidate.orderDate).slice(0, 10) : '')}</td>
    <td>${posEsc(candidate.customerName || '')}</td>
    <td>${posEsc(candidate.currency ? String(candidate.currency).toUpperCase() : '')}</td>
    <td>${posEsc(posStatusText(candidate.status))}</td>
    <td>${eligible ? '可用' : posEsc(candidate.ineligibleReason || '不可用')}</td>
    <td>${action}</td>
  </tr>`;
}

/* 已存储来源只读展示：显式标注未关联 / 已关联 / 来源已取消 / 来源不可用（历史原样保留）。
   不可用（已删除 / 已取消 / 未审核 / 范围外）时**绝不渲染**客户 / 订单字段，避免披露范围外来源。 */
function posStoredSourceHtml(view) {
  if (!view || view.linked !== true) {
    return `<div class="pos-stored" style="padding:6px 8px;color:#6b7280">${posEsc(POS_UNLINK_TEXT)}</div>`;
  }
  if (view.unavailable === true) {
    return `<div class="pos-stored" style="padding:6px 8px;color:#6b7280">`
      + `${posEsc(view.annotation || '已存储来源销售订单不可用，原链接原样保留')}</div>`;
  }
  const status = view.status
    ? `（状态 ${posStatusText(view.status)}${view.eligibleForNewLink === true ? '，仍可作为新来源' : '，只读保留'}）`
    : '';
  const orderNo = view.orderNo ? `「${posEsc(view.orderNo)}」` : `Id ${posEsc(view.salesOrderId)}`;
  const customer = view.customerName ? `（客户 ${posEsc(view.customerName)}）` : '';
  return `<div class="pos-stored" style="padding:6px 8px;background:#f9fafb;border:1px solid #e5e7eb;border-radius:8px">`
    + `已存储来源销售订单 ${orderNo}${customer}${posEsc(status)}：${posEsc(view.annotation || '')}</div>`;
}

function posErrorHtml(kind, message) {
  const prefix = POS_ERROR_PREFIX[kind] || '操作失败';
  return `<div class="pos-error" role="alert" style="padding:8px 10px;background:#fef2f2;border:1px solid #fecaca;border-radius:8px;color:#b91c1c;font-size:13px;margin-bottom:8px">`
    + `${posEsc(prefix)}：${posEsc(message || '')}</div>`;
}

function posErrorMessage(err) {
  const message = err && err.message ? err.message : String(err || '');
  if (/Failed to fetch|NetworkError|network|无法连接/i.test(message)) return { kind: 'network', message };
  if (/权限|授权|未认证|登录|禁止/.test(message)) return { kind: 'unauthorized', message };
  return { kind: 'server', message };
}

/* ==================== 浏览器接线（Node 单测只使用上面的纯函数） ==================== */

function posFieldValue(key) {
  const el = document.getElementById('f_' + key);
  return el ? el.value : '';
}

/* 只回填归属来源与归属客户字段：无选择 / 显式断开 → 来源 Id 与单号留空（保存时服务端归一化为 null）。
   归属客户字段只在提供权威值时写入；绝不动币种 / 汇率 / 单价 / 金额 / 明细。 */
function posFillForm(fill) {
  if (typeof document === 'undefined') return;
  const f = fill || {};
  const idEl = document.getElementById('f_owningSalesOrderId');
  if (idEl) idEl.value = posIsPositiveId(f.owningSalesOrderId) ? f.owningSalesOrderId : '';
  const noEl = document.getElementById('f_owningSalesOrderNo');
  if (noEl) noEl.value = f.owningSalesOrderNo || '';
  if (f.owningCustomerId !== undefined) {
    const custHidden = document.getElementById('f_owningCustomerId');
    if (custHidden) custHidden.value = posIsPositiveId(f.owningCustomerId) ? f.owningCustomerId : '';
    const custSearch = document.getElementById('f_owningCustomerId_search');
    if (custSearch) custSearch.value = f.owningCustomerName || '';
  }
  if (f.owningCustomerName !== undefined) {
    const custName = document.getElementById('f_owningCustomerName');
    if (custName) custName.value = f.owningCustomerName || '';
  }
}

function posModalHtml() {
  const warning = POS.readonly ? posErrorHtml('server', POS_READONLY_TEXT) : '';
  const error = POS.error ? posErrorHtml(posErrorMessage({ message: POS.error }).kind, POS.error) : '';
  const result = POS.result ? `<div class="text-muted" role="status" style="margin:6px 0">${posEsc(POS.result)}</div>` : '';
  const rows = (POS.candidates || []).map(posCandidateRowHtml).join('')
    || `<tr><td colspan="8" class="text-muted">没有匹配的来源销售订单候选（严格按当前客户数据范围返回，已审核、未删除、未取消）</td></tr>`;
  return `<div class="modal modal-lg">
    <div class="modal-head"><span>选择来源销售订单（采购订单归属来源）</span><button type="button" class="btn btn-neutral btn-sm" onclick="posCloseModal()">关闭</button></div>
    <div class="modal-body">
      ${warning}${error}${result}
      <div style="margin-bottom:8px">归属客户 Id：<b>${posEsc(POS.customerId || '（未指定，按当前数据范围）')}</b>；只显示「已审核、未删除、未取消」的销售订单（不要求币种一致，币种仅供展示）</div>
      <div style="margin-bottom:8px">
        <input type="text" id="pos_keyword" value="${posEsc(POS.keyword)}" placeholder="按订单号 / 客户 PO / 合同号搜索" style="width:260px">
        <button type="button" class="btn btn-neutral btn-sm" onclick="posSearch()">搜索</button>
        <button type="button" class="btn btn-neutral btn-sm" onclick="posUnlinkSource()">显式断开来源链接</button>
      </div>
      <div style="margin-bottom:8px">${posStoredSourceHtml(POS.stored)}</div>
      <table class="data-table">
        <thead><tr><th>Id</th><th>订单号</th><th>日期</th><th>客户</th><th>币种</th><th>状态</th><th>可用性</th><th>操作</th></tr></thead>
        <tbody>${rows}</tbody>
      </table>
      <div class="text-muted" style="margin-top:6px">共 ${posEsc(POS.total)} 条候选；每页最多 ${posEsc(POS.pageSize)} 条。只回填归属来源与归属客户，绝不自动改动币种 / 汇率 / 单价 / 金额。</div>
    </div>
  </div>`;
}

function posRender() {
  if (typeof document === 'undefined' || !POS.open) return;
  const modal = document.getElementById('modal');
  if (!modal) return;
  modal.innerHTML = posModalHtml();
  modal.style.display = 'flex';
}

function posCloseModal() {
  POS.open = false;
  POS.error = '';
  if (typeof closeModal === 'function') closeModal();
}

async function posLoadStatus(orderId) {
  POS.status = '';
  POS.readonly = false;
  if (!(orderId > 0)) return;
  try {
    const row = await api(`${POS_API_BASE}/${orderId}`);
    POS.status = posNormalizeStatus(row && row.status);
    POS.readonly = !posIsEditableStatus(POS.status);
  } catch (err) {
    POS.readonly = true;
    POS.error = posErrorMessage(err).message;
  }
}

async function posLoadStoredSource(orderId) {
  POS.stored = null;
  if (!(orderId > 0)) return;
  try {
    POS.stored = await api(`${POS_API_BASE}/${orderId}/sales-order-source`) || null;
  } catch (err) {
    POS.stored = null;
    POS.error = posErrorMessage(err).message;   // 只读展示失败显式可见，保留表单状态
  }
  posRender();
}

async function posLoadCandidates() {
  const seq = ++POS.requestSeq;
  const requestCustomerId = POS.customerId;
  POS.loading = true;
  POS.error = '';
  posRender();
  const params = [];
  if (requestCustomerId > 0) params.push('customerId=' + encodeURIComponent(requestCustomerId));
  params.push('page=' + encodeURIComponent(posNormalizePage(POS.page)));
  params.push('pageSize=' + encodeURIComponent(posNormalizePageSize(POS.pageSize)));
  const kw = posNormalizeKeyword(POS.keyword);
  if (kw) params.push('keyword=' + encodeURIComponent(kw));
  try {
    const res = await api(`${POS_API_BASE}/sales-order-source-candidates?` + params.join('&'));
    // 陈旧响应（归属客户已切换）一律丢弃，绝不回填陈旧上下文结果
    if (!posShouldAcceptResponse(seq, POS.requestSeq, requestCustomerId, POS.customerId)) return;
    POS.page = posNormalizePage(res && res.page ? res.page : POS.page);
    POS.pageSize = posNormalizePageSize(res && res.pageSize ? res.pageSize : POS.pageSize);
    POS.total = res && Number.isFinite(Number(res.total)) ? Number(res.total) : 0;
    POS.candidates = (res && res.items) || [];
    POS.loading = false;
    posRender();
  } catch (err) {
    if (seq !== POS.requestSeq) return;      // 陈旧失败也不覆盖当前状态
    POS.loading = false;
    POS.error = posErrorMessage(err).message; // 失败保留表单状态
    posRender();
  }
}

async function posSearch() {
  const el = typeof document !== 'undefined' ? document.getElementById('pos_keyword') : null;
  POS.keyword = posNormalizeKeyword(el ? el.value : POS.keyword);
  POS.page = 1;
  await posLoadCandidates();
}

function posPickCandidate(salesOrderId) {
  if (POS.readonly) { if (typeof toast === 'function') toast(POS_READONLY_TEXT, 'error'); return; }
  const candidate = (POS.candidates || []).find(c => Number(c.salesOrderId) === Number(salesOrderId));
  const selection = posSelectionFromCandidate(candidate);
  if (!selection) {
    if (typeof toast === 'function') toast('该候选不可选（未审核 / 已取消 / 无效）：绝不臆造来源', 'error');
    return;
  }
  POS.selected = selection;
  posFillForm(posFormFill(selection));
  POS.result = `已回填归属来源销售订单 Id ${selection.salesOrderId}（不自动改币种 / 汇率 / 单价 / 金额）`;
  posRender();
}

function posUnlinkSource() {
  if (POS.readonly) { if (typeof toast === 'function') toast(POS_READONLY_TEXT, 'error'); return; }
  POS.selected = null;
  posFillForm(posUnlinkFill());
  POS.result = '已显式断开来源链接（保存后生效；历史未关联语义保留）';
  posRender();
}

function posApplyToForm() {
  if (!POS.selected) { if (typeof toast === 'function') toast('请先选择一个可用的来源销售订单', 'error'); return; }
  posFillForm(posFormFill(POS.selected));
  POS.result = `已应用来源销售订单 Id ${POS.selected.salesOrderId}`;
  posRender();
}

/* 归属客户变更：作废在途响应 + 清除先前的来源选择（绝不把旧上下文来源留在新表单上） */
function posOnContextChanged() {
  POS.requestSeq += 1;
  POS.customerId = posCustomerId(posFieldValue('owningCustomerId'));
  POS.candidates = [];
  POS.total = 0;
  POS.stored = null;
  POS.selected = null;
  if (typeof document !== 'undefined') {
    const el = document.getElementById('f_owningSalesOrderId');
    if (el && posIsPositiveId(el.value)) {
      posFillForm(posUnlinkFill());
      POS.result = '归属客户已变更：原来源选择已失效并清除，请重新选择来源';
    }
  }
  posRender();
}

function posInstallContextChangeHook() {
  if (typeof document === 'undefined') return false;
  let installed = false;
  const watch = (id) => {
    const el = document.getElementById(id);
    if (!el || el.__posWatched) return;
    el.__posWatched = true;
    el.addEventListener('change', posOnContextChanged);
    el.addEventListener('input', posOnContextChanged);
    installed = true;
  };
  watch('f_owningCustomerId');
  watch('f_owningCustomerId_search');
  return installed;
}

/* 选择归属客户（引用字段下拉）时同步作废先前的来源选择（selectRef 不派发 change 事件，故显式包裹） */
function posInstallSelectRefHook() {
  if (typeof window === 'undefined' || typeof window.selectRef !== 'function') return false;
  if (window.__posSelectRefHooked) return true;
  const original = window.selectRef;
  const wrapped = function (key) {
    const result = original.apply(this, arguments);
    if (String(key) === 'owningCustomerId') posOnContextChanged();
    return result;
  };
  wrapped.__posWrapped = true;
  window.selectRef = wrapped;
  window.__posSelectRefHooked = true;
  return true;
}

/* 打开选择器：新建（0）与已保存单据都可用；重开时显式回显服务端持久化的来源 */
async function openPurchaseOrderSalesOrderSourcePicker(orderId) {
  if (typeof document === 'undefined') return;
  const guard = posGuardOrderId(orderId);
  if (guard) { if (typeof toast === 'function') toast(guard, 'error'); return; }

  const customerId = posCustomerId(posFieldValue('owningCustomerId'));
  POS.open = true;
  POS.orderId = Number(orderId) || 0;
  POS.customerId = customerId;
  POS.keyword = '';
  POS.page = 1;
  POS.pageSize = POS_PAGE_SIZE_DEFAULT;
  POS.total = 0;
  POS.candidates = [];
  POS.stored = null;
  POS.selected = null;
  POS.loading = false;
  POS.readonly = false;
  POS.error = '';
  POS.result = '';
  POS.requestSeq += 1;
  posRender();
  posInstallContextChangeHook();
  posInstallSelectRefHook();

  await posLoadStatus(POS.orderId);
  await posLoadStoredSource(POS.orderId);
  await posLoadCandidates();
}

/* 表单接入：为 modules-doc 中声明 selector: 'purchase-order-sales-order-source' 的字段追加「选择来源」入口，
   也可由单据编辑页显式传入当前单据 Id。只包裹既有全局函数，不新增模块 / 菜单。 */
function posSourceButtonHtml() {
  return `<div class="form-item full"><label>来源销售订单选择</label>`
    + `<button type="button" class="btn btn-neutral" onclick="openPurchaseOrderSalesOrderSourcePicker(window.__posCurrentId || 0)">选择来源销售订单（按数据范围有界候选）</button>`
    + `<span class="text-muted" style="margin-left:8px">选定后按权威来源回填归属销售订单 Id / 单号与归属客户；归属客户变更即失效；可显式断开；不自动改币种 / 汇率 / 单价 / 金额</span>`
    + `</div>`;
}

function posInstallFormHook() {
  if (typeof window === 'undefined') return false;
  if (window.__posHooked) return true;
  if (typeof window.fieldHtml !== 'function' || typeof window.openForm !== 'function') return false;

  const originalOpenForm = window.openForm;
  const wrappedOpenForm = function (id) {
    window.__posCurrentId = (id === undefined || id === null || id === '') ? 0 : (Number(id) || 0);
    const result = originalOpenForm.apply(this, arguments);
    posInstallContextChangeHook();
    posInstallSelectRefHook();
    return result;
  };
  wrappedOpenForm.__posWrapped = true;
  window.openForm = wrappedOpenForm;

  const originalFieldHtml = window.fieldHtml;
  const wrappedFieldHtml = function (field) {
    const html = originalFieldHtml.apply(this, arguments);
    if (!field || field.selector !== 'purchase-order-sales-order-source') return html;
    return html + posSourceButtonHtml();
  };
  wrappedFieldHtml.__posWrapped = true;
  window.fieldHtml = wrappedFieldHtml;
  window.__posHooked = true;
  return true;
}

if (typeof window !== 'undefined' && typeof window.fieldHtml === 'function') posInstallFormHook();

/* Node 单测导出（浏览器中 module 为 undefined，自动跳过） */
if (typeof module !== 'undefined' && module.exports) {
  module.exports = {
    POS_EDITABLE_STATUS, POS_PAGE_SIZE_DEFAULT, POS_PAGE_SIZE_MAX, POS_READONLY_TEXT, POS_ERROR_PREFIX,
    POS_API_BASE, POS_WRITTEN_FIELDS, POS_UNLINK_TEXT,
    posNormalizeStatus, posStatusText, posIsEditableStatus, posReadonlyReason,
    posGuardOrderId, posIsPositiveId, posCustomerId,
    posNormalizePage, posNormalizePageSize, posNormalizeKeyword,
    posAvailableCandidates, posSelectionFromCandidate, posSelectionMatchesContext,
    posShouldInvalidateSelection, posShouldAcceptResponse, posFormFill, posUnlinkFill,
    posCandidateLabel, posEsc, posCandidateRowHtml, posStoredSourceHtml, posErrorHtml, posErrorMessage,
    posSourceButtonHtml, posInstallFormHook, posInstallContextChangeHook, posInstallSelectRefHook,
    posOnContextChanged, openPurchaseOrderSalesOrderSourcePicker, posApplyToForm, posUnlinkSource,
    posPickCandidate, posSearch, posLoadCandidates, posLoadStoredSource, posLoadStatus,
  };
}
