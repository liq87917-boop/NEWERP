/* ==================================================================================
   ====== 预装柜明细 → 已审核销售订单需求来源 显式链接工作台（ERP-372） ======
   ==================================================================================
   定位：为**已保存**（待提交）的预装柜单逐行显式登记 / 清除「需求计划证据」——
         该预装行的数量计划满足哪一条**已审核、未删除**销售订单明细
         （ContainerPreLoadingDetail.SourceSalesOrderDetailId，ERP-368）。
   为什么需要本工作台：ERP-368 已提供权威只读候选接口与链接指派接口，但业务界面此前
         没有任何入口，用户无法通过业务界面完成显式需求链接（只能靠接口 / 脚本）。
   规则（与 PreLoadingSalesOrderLinkRules 同口径，服务端为唯一权威）：
     1. 只接受**显式选择**的销售订单明细 Id：候选列表全部来自服务端有界只读查询，
        **绝不按订单号 / 商品 / 规格 / 文本相似度猜测来源**，也绝不臆造明细 Id；
     2. 未链接（null）是**显式历史事实**：历史 / 未登记需求来源的行一律标记「未链接」，绝不回填；
     3. 未保存单据（无服务端行 Id）**不能**登记链接：必须先保存成功，再回来维护；
     4. 只有「待提交」状态可维护；已提交 / 已审核 / 已取消 / 已完成 / 已驳回一律**只读**；
     5. 提交前由服务端在同一可串行化事务内重新校验（来源已审核且未删除、商品 / 基础单位一致、
        客户等于权威订柜客户、累计容量、状态仍为待提交），任一步失败整体回滚；
        界面在失败时**保留全部草稿输入**并原样显示服务端失败原因；
     6. 双击 / 重复点击被 `submitting` 闸门阻断；在别处重新加载过明细后，陈旧选择必须重新确认；
     7. 来源已不可用（明细删除 / 订单取消 / 撤销审核）时**原链接原样保留**，
        界面显式标注「来源不可用」，绝不静默清除。
   边界：本工作台只维护这一列需求计划 / 追溯证据：不锁库、不预留库存、不生成库存流水 /
         费用 / 单证，不改写商品 / 客户 / 订单 / 订柜主数据，不改写预装数量与状态，
         也不新增表 / 菜单 / 用户授权（复用既有「预装柜单」+「销售订单」菜单与实时客户范围）。
   ================================================================================== */

/* 只有「待提交」可维护链接（与后端 AssignSalesOrderLinks 的 GetStatus(entity) != Pending 一致） */
const PDL_EDITABLE_STATUS = 'Pending';
const PDL_STATUS_TEXT = {
  Pending: '待提交（草稿）', Submitted: '已提交', Approved: '已审核',
  Rejected: '已驳回', Completed: '已完成', Cancelled: '已取消',
};
const PDL_TAKE_DEFAULT = 50;
const PDL_TAKE_MAX = 200;

/* 证据文案（与 PreLoadingSalesOrderLinkRules 的三段文案同口径，服务端权威值优先） */
const PDL_UNLINKED_TEXT = '未链接（历史 / 未登记需求来源；保持显式未链接，绝不回填）';
const PDL_LINKED_TEXT = '已链接（有效需求计划证据）';
const PDL_UNAVAILABLE_TEXT = '来源不可用（来源销售订单明细已删除或订单已取消 / 撤销审核；原链接原样保留，绝不静默清除）';
const PDL_UNSAVED_TEXT = '请先保存该预装柜单，再登记 / 维护需求来源链接（不允许为未保存单据臆造明细 Id）';

/* 一次对话 = 一个预装柜单；linesToken 每次从服务端重新加载明细行都会递增，
   用于把「在别处换过明细后仍按旧行集提交」的陈旧选择挡在提交之前。 */
let PDL = {
  preLoadingId: 0, preLoadingNo: '', status: '', lines: [], candidates: [],
  draft: {}, selectionTokens: {}, linesToken: 0, keyword: '', take: PDL_TAKE_DEFAULT,
  hint: '', error: '', loading: false, submitting: false, readonly: true, result: '',
};

/* 状态归一化：既接受后端枚举名（Pending），也接受数字（0）与已有中文文案 */
function pdlNormalizeStatus(status) {
  if (status === null || status === undefined || status === '') return '';
  const raw = String(status).trim();
  if (PDL_STATUS_TEXT[raw]) return raw;
  const byNumber = { '0': 'Pending', '1': 'Submitted', '2': 'Approved', '3': 'Rejected', '4': 'Completed', '5': 'Cancelled' };
  if (byNumber[raw]) return byNumber[raw];
  const hit = Object.keys(PDL_STATUS_TEXT).find(k => PDL_STATUS_TEXT[k] === raw);
  return hit || raw;
}

function pdlStatusText(status) {
  const key = pdlNormalizeStatus(status);
  return PDL_STATUS_TEXT[key] || (key || '未知');
}

/* 可维护性：仅「待提交」可写；其余状态（含已审核 / 已取消）一律只读（服务端同样 fail closed） */
function pdlIsEditableStatus(status) {
  return pdlNormalizeStatus(status) === PDL_EDITABLE_STATUS;
}

function pdlReadonlyReason(status) {
  return pdlIsEditableStatus(status)
    ? ''
    : `当前状态「${pdlStatusText(status)}」为只读：仅「待提交（草稿）」可维护需求来源链接`;
}

/* 未保存单据（无正向整数 Id）绝不能登记链接：返回拒绝文案，空串 = 放行 */
function pdlGuardSavedId(preLoadingId) {
  const n = Number(preLoadingId);
  return Number.isInteger(n) && n > 0 ? '' : PDL_UNSAVED_TEXT;
}

/* 服务端持久化的显式链接（null / 非正 = 显式未链接） */
function pdlLineSourceId(line) {
  const raw = line ? line.sourceSalesOrderDetailId : null;
  if (raw === null || raw === undefined || raw === '') return null;
  const n = Number(raw);
  return Number.isInteger(n) && n > 0 ? n : null;
}

/* 草稿初始值：原样回显服务端持久化链接（'123'）或显式未链接（''） */
function pdlLineDraftValue(line) {
  const sid = pdlLineSourceId(line);
  return sid === null ? '' : String(sid);
}

function pdlInitialDraft(lines) {
  const draft = {};
  (lines || []).forEach(line => {
    if (!line) return;
    draft[String(line.preLoadingDetailId)] = pdlLineDraftValue(line);
  });
  return draft;
}

/* 证据文案：服务端权威文案优先；缺失时按显式链接 + 可用性回退（绝不臆造订单号） */
function pdlLineEvidenceText(line) {
  if (!line || pdlLineSourceId(line) === null) return PDL_UNLINKED_TEXT;
  if (line.sourceAvailabilityText) return String(line.sourceAvailabilityText);
  return line.sourceAvailable ? PDL_LINKED_TEXT : PDL_UNAVAILABLE_TEXT;
}

/* 行标签：商品名称（数量 单位），缺失不臆造 */
function pdlLineLabel(line) {
  if (!line) return '';
  const name = String(line.productName || `商品 Id ${line.productId ?? ''}`).trim();
  const qty = line.quantity === null || line.quantity === undefined ? '' : String(line.quantity);
  return qty ? `${name}（${qty}）` : name;
}

/* 选择归一化：只接受空串（清除）或正整数文本；其它输入（含从任意文本「解析」出的数字）一律拒绝 ——
   绝不解文本为 Id、绝不臆造来源。 */
function pdlNormalizeSelection(value) {
  if (value === null || value === undefined) return { ok: true, value: '' };
  const raw = String(value).trim();
  if (raw === '') return { ok: true, value: '' };
  if (!/^\d+$/.test(raw)) return { ok: false, value: '' };
  const n = Number(raw);
  if (!Number.isInteger(n) || n <= 0) return { ok: false, value: '' };
  return { ok: true, value: String(n) };
}

/* 记录一次显式选择（并记下当时的行集 token）：行必须来自当前服务端行集，非法输入被拒绝且保留原草稿 */
function pdlApplySelection(draft, lines, selectionTokens, detailId, value, linesToken) {
  const id = String(detailId);
  const known = (lines || []).some(l => l && String(l.preLoadingDetailId) === id);
  const next = pdlNormalizeSelection(value);
  if (!known || !next.ok) {
    return { draft, selectionTokens, rejected: true };
  }
  const nextDraft = Object.assign({}, draft);
  nextDraft[id] = next.value;
  const nextTokens = Object.assign({}, selectionTokens);
  nextTokens[id] = Number(linesToken) || 0;
  return { draft: nextDraft, selectionTokens: nextTokens, rejected: false };
}

/* 变更集：只返回相对服务端现状**真正变化**的行。行必须来自当前服务端行集（否则忽略 → 挡住陈旧 / 伪造行），
   清空 → null（绝不臆造 Id），未通过校验的输入被忽略。 */
function pdlBuildAssignments(draft, lines) {
  const byId = new Map((lines || []).filter(Boolean).map(l => [String(l.preLoadingDetailId), l]));
  const assignments = [];
  Object.keys(draft || {}).forEach(key => {
    const line = byId.get(String(key));
    if (!line) return;
    const previous = pdlLineDraftValue(line);
    const next = pdlNormalizeSelection((draft || {})[key]);
    if (!next.ok || next.value === previous) return;
    assignments.push({
      preLoadingDetailId: Number(line.preLoadingDetailId),
      sourceSalesOrderDetailId: next.value === '' ? null : Number(next.value),
    });
  });
  return assignments.sort((a, b) => a.preLoadingDetailId - b.preLoadingDetailId);
}

function pdlIsDirty(draft, lines) {
  return pdlBuildAssignments(draft, lines).length > 0;
}

/* 陈旧选择：明细行在别处被重新加载（token 变化）后仍按旧行集提交的已变更行 —— 必须重新确认后再提交 */
function pdlStaleSelections(assignments, selectionTokens, linesToken) {
  const token = Number(linesToken) || 0;
  return (assignments || []).filter(a =>
    Number((selectionTokens || {})[String(a.preLoadingDetailId)]) !== token);
}

/* 重新加载后：草稿中已不存在的行（已被删除 / 替换）被标记为陈旧并剔除，但**绝不**修改仍存在行的链接 */
function pdlPruneStaleDraft(draft, lines) {
  const known = new Set((lines || []).filter(Boolean).map(l => String(l.preLoadingDetailId)));
  const nextDraft = {};
  const staleLineIds = [];
  Object.keys(draft || {}).forEach(key => {
    if (known.has(String(key))) nextDraft[key] = (draft || {})[key];
    else staleLineIds.push(key);
  });
  return { draft: nextDraft, staleLineIds };
}

/* 候选是否与行商品一致（服务端仍会最终校验；这里只用于界面友好分组，绝不用来「推断」来源） */
function pdlCandidateMatchesLine(candidate, line) {
  if (!candidate || !line) return false;
  return String(candidate.productId) === String(line.productId);
}

/* 候选完整证据文案：订单号 / 客户 / 商品 / 规格 / 基础单位 / 剩余可链接数量（缺失显示「未知」，不臆造） */
function pdlCandidateLabel(candidate) {
  if (!candidate) return '';
  const parts = [
    candidate.orderNo ? `订单 ${candidate.orderNo}` : `订单 Id ${candidate.salesOrderId ?? ''}`,
    candidate.customerName ? `客户 ${candidate.customerName}` : '',
    candidate.productName || `商品 Id ${candidate.productId ?? ''}`,
    candidate.spec ? `规格 ${candidate.spec}` : '规格 未知',
    `基础单位 ${candidate.unit || '未知'}`,
    `剩余可链接 ${candidate.remainingBaseQuantity ?? ''}`,
  ];
  return parts.filter(Boolean).join(' / ');
}

/* 提交闸门：必须已保存、非只读、非加载 / 提交中、且确有变更 */
function pdlCanSubmit(state) {
  const s = state || {};
  if (!(Number(s.preLoadingId) > 0)) return false;
  if (s.readonly || s.loading || s.submitting) return false;
  return pdlIsDirty(s.draft, s.lines);
}

/* 安全转义（浏览器复用全局 escapeHtml；Node 单测走本地实现，绝不注入） */
function pdlEsc(v) {
  const s = v === null || v === undefined ? '' : String(v);
  if (typeof escapeHtml === 'function') return escapeHtml(s);
  return s.replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

/* 单行 HTML：行标签 + 明细 Id + 服务端权威证据文案 + 显式选择器（未链接 / 候选 / 当前链接保留） */
function pdlLineRowHtml(line, draft, candidates) {
  if (!line) return '';
  const id = String(line.preLoadingDetailId);
  const hasDraft = draft && Object.prototype.hasOwnProperty.call(draft, id);
  const current = hasDraft ? String(draft[id]) : pdlLineDraftValue(line);
  const persisted = pdlLineDraftValue(line);
  const compatible = (candidates || []).filter(c => pdlCandidateMatchesLine(c, line));

  const options = [`<option value=""${current === '' ? ' selected' : ''}>未链接（清除该行需求来源链接）</option>`];
  compatible.forEach(c => {
    const value = String(c.salesOrderDetailId);
    options.push(`<option value="${pdlEsc(value)}"${current === value ? ' selected' : ''}>` +
      `${pdlEsc(pdlCandidateLabel(c))}</option>`);
  });
  /* 当前持久化链接不在候选内（剩余为 0 / 来源已不可用）：保留该选项并显式标注，绝不静默清除 */
  if (persisted !== '' && !compatible.some(c => String(c.salesOrderDetailId) === persisted)) {
    options.push(`<option value="${pdlEsc(persisted)}"${current === persisted ? ' selected' : ''}>` +
      `订单明细 Id ${pdlEsc(persisted)}（不在当前候选内：剩余为 0 或来源不可用，原链接保留）</option>`);
  }

  return `<tr data-detail-id="${pdlEsc(id)}">
    <td>${pdlEsc(pdlLineLabel(line))}</td>
    <td>明细 Id ${pdlEsc(id)}</td>
    <td>${pdlEsc(pdlLineEvidenceText(line))}</td>
    <td><select id="pdl-line-${pdlEsc(id)}" onchange="pdlSelectLine(this, ${pdlEsc(id)})">${options.join('')}</select></td>
  </tr>`;
}

/* 候选证据行：订单号 / 客户 / 商品 / 规格 / 基础单位 / 剩余可链接数量（全部来自服务端权威只读查询） */
function pdlCandidateRowHtml(candidate) {
  if (!candidate) return '';
  return `<tr>
    <td>${pdlEsc(candidate.salesOrderDetailId)}</td>
    <td>${pdlEsc(candidate.orderNo || '')}</td>
    <td>${pdlEsc(candidate.customerName || '')}</td>
    <td>${pdlEsc(candidate.productName || '')}</td>
    <td>${pdlEsc(candidate.spec || '')}</td>
    <td>${pdlEsc(candidate.unit || '')}</td>
    <td>${pdlEsc(candidate.remainingBaseQuantity ?? '')} / ${pdlEsc(candidate.orderBaseQuantity ?? '')}</td>
  </tr>`;
}

const PDL_ERROR_PREFIX = {
  network: '无法连接服务器', unauthorized: '登录已过期或权限不足', server: '服务端拒绝',
};

/* 失败展示：网络 / 授权 / 服务端失败均显式可见（草稿输入由调用方保留） */
function pdlErrorHtml(kind, message) {
  const prefix = PDL_ERROR_PREFIX[kind] || '操作失败';
  return `<div class="pdl-error" role="alert" style="padding:8px 10px;background:#fef2f2;border:1px solid #fecaca;border-radius:8px;color:#b91c1c;font-size:13px;margin-bottom:8px">` +
    `${pdlEsc(prefix)}：${pdlEsc(message || '')}</div>`;
}

function pdlStatusBadgeHtml() {
  const editable = pdlIsEditableStatus(PDL.status);
  return `<span class="status ${editable ? 'status-info' : 'status-neutral'}">${pdlEsc(pdlStatusText(PDL.status))}</span>`;
}

/* ==================== 界面（一次对话 = 一个预装柜单） ==================== */

function pdlReset() {
  PDL = {
    preLoadingId: 0, preLoadingNo: '', status: '', lines: [], candidates: [],
    draft: {}, selectionTokens: {}, linesToken: 0, keyword: '', take: PDL_TAKE_DEFAULT,
    hint: '', error: '', loading: false, submitting: false, readonly: true, result: '',
  };
}

function pdlErrorMessage(err) {
  const msg = err && err.message ? String(err.message) : '';
  return msg || '网络或服务端异常，请重试（草稿输入已保留）';
}

function pdlBodyHtml() {
  const editable = pdlIsEditableStatus(PDL.status);
  const linesHtml = PDL.lines.map(l => pdlLineRowHtml(l, PDL.draft, PDL.candidates)).join('');
  const candidatesHtml = PDL.candidates.map(pdlCandidateRowHtml).join('');
  const assignments = pdlBuildAssignments(PDL.draft, PDL.lines);
  const canSubmit = pdlCanSubmit(PDL);

  return `
    <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:8px">
      <h3 style="margin:0">🔗 需求来源链接
        <span style="font-size:13px;color:#64748b;font-weight:400;margin-left:10px">
          逐行显式指定该预装行满足哪一条已审核销售订单明细（需求计划 / 追溯证据，不是库存预留、不是出运凭证）</span></h3>
      <button class="btn btn-neutral btn-sm" onclick="closeModal()">关闭</button>
    </div>
    <div style="padding:8px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;color:#475569;font-size:13px;margin-bottom:10px">
      单号 <b>${pdlEsc(PDL.preLoadingNo || '未保存')}</b> · 状态 ${pdlStatusBadgeHtml()} ·
      只接受显式选择的销售订单明细 Id，绝不按订单号 / 商品 / 文本猜测来源；历史未链接保持「未链接」，来源不可用时原链接原样保留。
    </div>
    ${PDL.hint ? `<div style="padding:6px 10px;background:#eff6ff;border:1px solid #bfdbfe;border-radius:8px;color:#1e40af;font-size:13px;margin-bottom:8px">${pdlEsc(PDL.hint)}</div>` : ''}
    ${PDL.result ? `<div style="padding:6px 10px;background:#f0fdf4;border:1px solid #bbf7d0;border-radius:8px;color:#166534;font-size:13px;margin-bottom:8px">${pdlEsc(PDL.result)}</div>` : ''}
    ${PDL.error ? pdlErrorHtml('server', PDL.error) : ''}
    ${PDL.loading ? '<div style="padding:20px;text-align:center;color:#64748b">正在加载服务端持久化结果…</div>' : ''}
    ${!editable && PDL.status ? `<div style="padding:6px 10px;background:#fff7ed;border:1px solid #fed7aa;border-radius:8px;color:#9a3412;font-size:13px;margin-bottom:8px">${pdlEsc(pdlReadonlyReason(PDL.status))}</div>` : ''}

    <div style="display:flex;align-items:center;justify-content:space-between;margin:10px 0 6px">
      <b style="font-size:14px">预装柜明细行（${PDL.lines.length} 行）</b>
      <div>
        <button class="btn btn-neutral btn-sm" onclick="pdlReload()" ${PDL.loading || PDL.submitting ? 'disabled' : ''}>⟳ 重新加载服务端结果</button>
        <button class="btn btn-primary btn-sm" id="pdl-submit" onclick="pdlSubmit()" ${canSubmit ? '' : 'disabled'}>
          ${PDL.submitting ? '提交中…' : `提交需求来源链接（${assignments.length} 行变更）`}
        </button>
      </div>
    </div>
    <div class="table-wrap" style="max-height:280px;overflow:auto">
      <table class="data-table"><thead><tr>
        <th>预装明细</th><th>服务端行 Id</th><th>当前链接证据</th><th style="min-width:320px">显式需求来源（销售订单明细）</th>
      </tr></thead><tbody>${linesHtml || '<tr><td colspan="4" style="text-align:center;color:#999">该单据暂无未删除明细行</td></tr>'}</tbody></table>
    </div>

    <div style="display:flex;align-items:center;justify-content:space-between;margin:14px 0 6px">
      <b style="font-size:14px">可链接需求证据候选（服务端有界只读：本单权威订柜客户下「已审核、未删除」销售订单明细）</b>
      <div>
        <input type="text" id="pdl-keyword" placeholder="订单号 / 商品名称" value="${pdlEsc(PDL.keyword)}"
               onkeydown="if(event.key==='Enter')pdlSearchCandidates()">
        <select id="pdl-take" onchange="pdlSearchCandidates()">
          ${[PDL_TAKE_DEFAULT, 100, PDL_TAKE_MAX].map(n =>
            `<option value="${n}"${Number(PDL.take) === n ? ' selected' : ''}>取 ${n} 条</option>`).join('')}
        </select>
        <button class="btn btn-neutral btn-sm" onclick="pdlSearchCandidates()" ${PDL.loading || PDL.submitting ? 'disabled' : ''}>查询候选</button>
      </div>
    </div>
    <div class="table-wrap" style="max-height:260px;overflow:auto">
      <table class="data-table"><thead><tr>
        <th>订单明细 Id</th><th>订单号</th><th>客户</th><th>商品</th><th>规格</th><th>基础单位</th><th>剩余 / 订单数量</th>
      </tr></thead><tbody>${candidatesHtml || '<tr><td colspan="7" style="text-align:center;color:#999">暂无可用候选（无剩余可链接数量即视为无可用容量，绝不猜测来源）</td></tr>'}</tbody></table>
    </div>`;
}

function pdlRender() {
  const modal = document.getElementById('modal');
  if (!modal) return;
  modal.innerHTML = `<div style="max-width:1280px;margin:2vh auto;background:#fff;border-radius:14px;padding:16px 18px;max-height:94vh;overflow:auto;box-shadow:0 20px 60px rgba(0,0,0,.25)">
    ${pdlBodyHtml()}
  </div>`;
  modal.style.display = 'flex';
}

/* 候选查询（有界：take 钳制在 1..PDL_TAKE_MAX；关键字原样透传，服务端再做权威过滤） */
async function pdlLoadCandidates() {
  if (!(Number(PDL.preLoadingId) > 0)) { PDL.candidates = []; return; }
  const take = Math.min(Math.max(Number(PDL.take) || PDL_TAKE_DEFAULT, 1), PDL_TAKE_MAX);
  PDL.take = take;
  const params = [`take=${take}`];
  if (PDL.keyword) params.push('keyword=' + encodeURIComponent(PDL.keyword));
  try {
    PDL.candidates = await api(`/api/container/pre-loadings/${PDL.preLoadingId}/sales-order-candidates?${params.join('&')}`) || [];
  } catch (err) {
    PDL.candidates = [];
    PDL.error = pdlErrorMessage(err);
  }
}

/* 加载单据 + 服务端权威链接状态（含「来源不可用」证据）；失败时保留已有草稿输入 */
async function pdlLoad() {
  const guard = pdlGuardSavedId(PDL.preLoadingId);
  if (guard) { PDL.error = guard; pdlRender(); return; }

  PDL.loading = true;
  PDL.error = '';
  pdlRender();
  try {
    const doc = await api(`/api/container/pre-loadings/${PDL.preLoadingId}`);
    PDL.preLoadingNo = doc.preLoadingNo || '';
    PDL.status = doc.status;
    PDL.readonly = !pdlIsEditableStatus(doc.status);

    const rawLines = (doc.details || []).filter(d => d && !d.isDeleted);
    /* 服务端权威链接状态：显式链接 Id + 可用性（含「来源不可用」证据） */
    const links = await api(`/api/container/pre-loadings/${PDL.preLoadingId}/sales-order-links`);
    const evidenceById = new Map((links || []).map(l => [String(l.preLoadingDetailId), l]));
    PDL.lines = rawLines.map(d => {
      const evidence = evidenceById.get(String(d.id));
      return {
        preLoadingDetailId: d.id,
        productId: d.productId,
        productName: d.productName,
        quantity: d.quantity,
        sourceSalesOrderDetailId: evidence ? evidence.sourceSalesOrderDetailId : d.sourceSalesOrderDetailId,
        sourceAvailable: evidence ? !!evidence.sourceAvailable : undefined,
        sourceAvailabilityText: evidence ? (evidence.sourceAvailabilityText || '') : '',
      };
    });
    PDL.linesToken += 1;

    /* 重新加载：剔除已不存在的陈旧行选择，仍存在的行保留用户尚未提交的草稿输入 */
    const pruned = pdlPruneStaleDraft(PDL.draft, PDL.lines);
    PDL.draft = Object.keys(pruned.draft).length ? pruned.draft : pdlInitialDraft(PDL.lines);
    PDL.selectionTokens = {};
    if (pruned.staleLineIds.length) {
      PDL.hint = `已在别处删除 / 替换 ${pruned.staleLineIds.length} 条明细行：对应的未提交选择已剔除，请重新确认。`;
    }

    await pdlLoadCandidates();
  } catch (err) {
    PDL.error = pdlErrorMessage(err);
  }
  PDL.loading = false;
  pdlRender();
}

async function pdlReload() {
  if (PDL.submitting) return;
  await pdlLoad();
}

async function pdlSearchCandidates() {
  const kwEl = document.getElementById('pdl-keyword');
  const takeEl = document.getElementById('pdl-take');
  PDL.keyword = kwEl ? kwEl.value.trim() : '';
  if (takeEl) PDL.take = Number(takeEl.value) || PDL_TAKE_DEFAULT;
  await pdlLoadCandidates();
  pdlRender();
}

/* 显式选择：只接受正整数（或清空）；非法值被拒绝并保留原草稿 */
function pdlSelectLine(selectEl, detailId) {
  if (PDL.readonly) {
    toast(pdlReadonlyReason(PDL.status), 'error');
    pdlRender();
    return;
  }
  const applied = pdlApplySelection(
    PDL.draft, PDL.lines, PDL.selectionTokens, detailId,
    selectEl ? selectEl.value : '', PDL.linesToken);
  if (applied.rejected) {
    toast('只接受显式选择的销售订单明细 Id（不接受文本 / 非法值）', 'error');
    pdlRender();
    return;
  }
  PDL.draft = applied.draft;
  PDL.selectionTokens = applied.selectionTokens;
  PDL.result = '';
  pdlRender();
}

/* 提交：双击 / 重复提交被 submitting 闸门阻断；陈旧选择必须先重新加载确认；
   失败保留草稿输入并显示服务端失败原因（菜单授权 / 客户范围 / 商品 / 单位 / 容量 / 状态由服务端权威判定）。 */
async function pdlSubmit() {
  if (PDL.submitting) return;
  if (PDL.readonly) { toast(pdlReadonlyReason(PDL.status), 'error'); return; }
  const guard = pdlGuardSavedId(PDL.preLoadingId);
  if (guard) { toast(guard, 'error'); return; }

  const assignments = pdlBuildAssignments(PDL.draft, PDL.lines);
  if (!assignments.length) { toast('没有需要提交的需求来源变更', 'warning'); return; }

  const stale = pdlStaleSelections(assignments, PDL.selectionTokens, PDL.linesToken);
  if (stale.length) {
    PDL.error = '预装柜单明细已在别处变更：请先「重新加载服务端结果」并重新确认这些行的需求来源选择后再提交';
    pdlRender();
    return;
  }

  PDL.submitting = true;
  PDL.error = '';
  pdlRender();
  try {
    const result = await api(`/api/container/pre-loadings/${PDL.preLoadingId}/sales-order-links`, 'POST', {
      links: assignments.map(a => ({
        preLoadingDetailId: a.preLoadingDetailId,
        sourceSalesOrderDetailId: a.sourceSalesOrderDetailId,
      })),
    });
    PDL.submitting = false;
    PDL.draft = {};
    PDL.selectionTokens = {};
    PDL.result = `服务端已持久化：新增 / 变更 ${result && result.linkedCount != null ? result.linkedCount : 0} 行，`
      + `清除 ${result && result.clearedCount != null ? result.clearedCount : 0} 行`;
    await pdlLoad();
  } catch (err) {
    PDL.submitting = false;
    PDL.error = pdlErrorMessage(err);
    pdlRender();
  }
}

/* 入口：预装柜单列表行操作（saved-draft 行）→ 需求来源链接工作台 */
async function openPreLoadingDemandLinks(preLoadingId) {
  pdlReset();
  const guard = pdlGuardSavedId(preLoadingId);
  if (guard) { toast(guard, 'error'); return; }
  PDL.preLoadingId = Number(preLoadingId);
  PDL.loading = true;
  pdlRender();
  await pdlLoad();
}

/* Node 单测导出（浏览器中 module 为 undefined，自动跳过） */
if (typeof module !== 'undefined' && module.exports) {
  module.exports = {
    PDL_EDITABLE_STATUS,
    PDL_TAKE_DEFAULT,
    PDL_TAKE_MAX,
    PDL_UNLINKED_TEXT,
    PDL_LINKED_TEXT,
    PDL_UNAVAILABLE_TEXT,
    PDL_UNSAVED_TEXT,
    pdlNormalizeStatus,
    pdlStatusText,
    pdlIsEditableStatus,
    pdlReadonlyReason,
    pdlGuardSavedId,
    pdlLineSourceId,
    pdlLineDraftValue,
    pdlInitialDraft,
    pdlLineEvidenceText,
    pdlLineLabel,
    pdlNormalizeSelection,
    pdlApplySelection,
    pdlBuildAssignments,
    pdlIsDirty,
    pdlStaleSelections,
    pdlPruneStaleDraft,
    pdlCandidateMatchesLine,
    pdlCandidateLabel,
    pdlCanSubmit,
    pdlEsc,
    pdlLineRowHtml,
    pdlCandidateRowHtml,
    pdlErrorHtml,
    pdlStatusBadgeHtml,
    pdlErrorMessage,
  };
}


