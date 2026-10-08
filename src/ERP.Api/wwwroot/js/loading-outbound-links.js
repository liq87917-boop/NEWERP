/* ==================================================================================
   ====== 装柜明细 → 已审核销售出库明细 显式出运证据 链接工作台（ERP-373） ======
   ==================================================================================
   定位：为**已保存**（待提交）的装柜清单逐行显式登记 / 清除「物理出运证据」——
         该装柜行的数量由哪一条**已审核、未删除**销售出库明细证明
         （ContainerLoadingDetail.SourceStockOutDetailId，ERP-366）。
   为什么需要本工作台：ERP-366 已提供权威只读候选接口与链接指派接口，但业务界面此前
         没有任何入口，普通操作员无法通过业务界面完成显式出运证据登记（只能靠接口 / 脚本）。
   规则（与 LoadingStockOutLinkRules 同口径，服务端为唯一权威）：
     1. 只接受**显式选择**的销售出库明细 Id：候选列表全部来自服务端有界只读查询，
        **绝不按出库单号 / 商品 / 规格 / 文本相似度猜测来源**，也绝不臆造明细 Id；
     2. 未链接（null）是**显式历史事实**：历史 / 未登记出运证据的行一律标记「未链接」，绝不回填；
     3. 未保存单据（无服务端行 Id）**不能**登记链接：必须先保存成功，再回来维护；
     4. 只有「待提交」状态可维护；已提交 / 已审核 / 已取消 / 已完成 / 已驳回一律**只读**；
     5. 提交前由服务端在同一可串行化事务内重新校验（来源已审核且未删除、商品 / 基础单位一致、
        客户属于权威客户范围、累计容量、状态仍为待提交），任一步失败整体回滚；
        界面在失败时**保留全部草稿输入**并原样显示服务端失败原因；
     6. 双击 / 重复点击被 `submitting` 闸门阻断；在别处重新加载过明细后，陈旧选择必须重新确认；
     7. 来源已不可用（明细删除 / 出库单取消 / 撤销审核）时**原链接原样保留**，
        界面显式标注「来源不可用」，绝不静默清除。
   边界：本工作台只维护这一列出运 / 追溯证据：不锁库、不预留库存、不生成库存流水 /
         费用 / 单证，不改写商品 / 客户 / 出库单主数据，不改写装柜数量与状态，
         也不新增表 / 菜单 / 用户授权（复用既有「装柜清单」+「销售出库」菜单与实时客户范围）。
   ================================================================================== */

/* 只有「待提交」可维护链接（与后端 AssignStockOutLinks 的 GetStatus(entity) != Pending 一致） */
const LOL_EDITABLE_STATUS = 'Pending';
const LOL_STATUS_TEXT = {
  Pending: '待提交（草稿）', Submitted: '已提交', Approved: '已审核',
  Rejected: '已驳回', Completed: '已完成', Cancelled: '已取消',
};
const LOL_TAKE_DEFAULT = 50;
const LOL_TAKE_MAX = 200;

/* 证据文案（与 LoadingStockOutLinkRules 的三段文案同口径，服务端权威值优先） */
const LOL_UNLINKED_TEXT = '未链接（历史 / 无出运证据；保持显式未链接，绝不回填）';
const LOL_LINKED_TEXT = '已链接（有效出运证据）';
const LOL_UNAVAILABLE_TEXT = '来源不可用（来源销售出库明细已删除或出库单已取消 / 撤销审核；原链接原样保留，绝不静默清除）';
const LOL_UNSAVED_TEXT = '请先保存该装柜清单，再登记 / 维护出运证据链接（不允许为未保存单据臆造明细 Id）';

/* 一次对话 = 一个装柜清单；linesToken 每次从服务端重新加载明细行都会递增，
   用于把「在别处换过明细后仍按旧行集提交」的陈旧选择挡在提交之前。 */
let LOL = {
  loadingListId: 0, loadingListNo: '', status: '', lines: [], candidates: [],
  draft: {}, selectionTokens: {}, linesToken: 0, keyword: '', take: LOL_TAKE_DEFAULT,
  hint: '', error: '', loading: false, submitting: false, readonly: true, result: '',
};

/* 状态归一化：既接受后端枚举名（Pending），也接受数字（0）与已有中文文案 */
function lolNormalizeStatus(status) {
  if (status === null || status === undefined || status === '') return '';
  const raw = String(status).trim();
  if (LOL_STATUS_TEXT[raw]) return raw;
  const byNumber = { '0': 'Pending', '1': 'Submitted', '2': 'Approved', '3': 'Rejected', '4': 'Completed', '5': 'Cancelled' };
  if (byNumber[raw]) return byNumber[raw];
  const hit = Object.keys(LOL_STATUS_TEXT).find(k => LOL_STATUS_TEXT[k] === raw);
  return hit || raw;
}

function lolStatusText(status) {
  const key = lolNormalizeStatus(status);
  return LOL_STATUS_TEXT[key] || (key || '未知');
}

/* 可维护性：仅「待提交」可写；其余状态（含已审核 / 已取消）一律只读（服务端同样 fail closed） */
function lolIsEditableStatus(status) {
  return lolNormalizeStatus(status) === LOL_EDITABLE_STATUS;
}

function lolReadonlyReason(status) {
  return lolIsEditableStatus(status)
    ? ''
    : `当前状态「${lolStatusText(status)}」为只读：仅「待提交（草稿）」可维护出运证据链接`;
}

/* 未保存单据（无正向整数 Id）绝不能登记链接：返回拒绝文案，空串 = 放行 */
function lolGuardSavedId(loadingListId) {
  const n = Number(loadingListId);
  return Number.isInteger(n) && n > 0 ? '' : LOL_UNSAVED_TEXT;
}

/* 服务端持久化的显式链接（null / 非正 = 显式未链接） */
function lolLineSourceId(line) {
  const raw = line ? line.sourceStockOutDetailId : null;
  if (raw === null || raw === undefined || raw === '') return null;
  const n = Number(raw);
  return Number.isInteger(n) && n > 0 ? n : null;
}

/* 草稿初始值：原样回显服务端持久化链接（'123'）或显式未链接（''） */
function lolLineDraftValue(line) {
  const sid = lolLineSourceId(line);
  return sid === null ? '' : String(sid);
}

function lolInitialDraft(lines) {
  const draft = {};
  (lines || []).forEach(line => {
    if (!line) return;
    draft[String(line.loadingDetailId)] = lolLineDraftValue(line);
  });
  return draft;
}

/* 证据文案：服务端权威文案优先；缺失时按显式链接 + 可用性回退（绝不臆造出库单号） */
function lolLineEvidenceText(line) {
  if (!line || lolLineSourceId(line) === null) return LOL_UNLINKED_TEXT;
  if (line.sourceAvailabilityText) return String(line.sourceAvailabilityText);
  return line.sourceAvailable ? LOL_LINKED_TEXT : LOL_UNAVAILABLE_TEXT;
}

/* 行标签：商品名称（数量 单位），缺失不臆造 */
function lolLineLabel(line) {
  if (!line) return '';
  const name = String(line.productName || `商品 Id ${line.productId ?? ''}`).trim();
  const qty = line.quantity === null || line.quantity === undefined ? '' : String(line.quantity);
  return qty ? `${name}（${qty}）` : name;
}

/* 选择归一化：只接受空串（清除）或正整数文本；其它输入（含从任意文本「解析」出的数字）一律拒绝 ——
   绝不解文本为 Id、绝不臆造来源。 */
function lolNormalizeSelection(value) {
  if (value === null || value === undefined) return { ok: true, value: '' };
  const raw = String(value).trim();
  if (raw === '') return { ok: true, value: '' };
  if (!/^\d+$/.test(raw)) return { ok: false, value: '' };
  const n = Number(raw);
  if (!Number.isInteger(n) || n <= 0) return { ok: false, value: '' };
  return { ok: true, value: String(n) };
}

/* 记录一次显式选择（并记下当时的行集 token）：行必须来自当前服务端行集，非法输入被拒绝且保留原草稿 */
function lolApplySelection(draft, lines, selectionTokens, detailId, value, linesToken) {
  const id = String(detailId);
  const known = (lines || []).some(l => l && String(l.loadingDetailId) === id);
  const next = lolNormalizeSelection(value);
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
function lolBuildAssignments(draft, lines) {
  const byId = new Map((lines || []).filter(Boolean).map(l => [String(l.loadingDetailId), l]));
  const assignments = [];
  Object.keys(draft || {}).forEach(key => {
    const line = byId.get(String(key));
    if (!line) return;
    const previous = lolLineDraftValue(line);
    const next = lolNormalizeSelection((draft || {})[key]);
    if (!next.ok || next.value === previous) return;
    assignments.push({
      loadingDetailId: Number(line.loadingDetailId),
      sourceStockOutDetailId: next.value === '' ? null : Number(next.value),
    });
  });
  return assignments.sort((a, b) => a.loadingDetailId - b.loadingDetailId);
}

function lolIsDirty(draft, lines) {
  return lolBuildAssignments(draft, lines).length > 0;
}

/* 陈旧选择：明细行在别处被重新加载（token 变化）后仍按旧行集提交的已变更行 —— 必须重新确认后再提交 */
function lolStaleSelections(assignments, selectionTokens, linesToken) {
  const token = Number(linesToken) || 0;
  return (assignments || []).filter(a =>
    Number((selectionTokens || {})[String(a.loadingDetailId)]) !== token);
}

/* 重新加载后：草稿中已不存在的行（已被删除 / 替换）被标记为陈旧并剔除，但**绝不**修改仍存在行的链接 */
function lolPruneStaleDraft(draft, lines) {
  const known = new Set((lines || []).filter(Boolean).map(l => String(l.loadingDetailId)));
  const nextDraft = {};
  const staleLineIds = [];
  Object.keys(draft || {}).forEach(key => {
    if (known.has(String(key))) nextDraft[key] = (draft || {})[key];
    else staleLineIds.push(key);
  });
  return { draft: nextDraft, staleLineIds };
}

/* 候选是否与行商品一致（服务端仍会最终校验；这里只用于界面友好分组，绝不用来「推断」来源） */
function lolCandidateMatchesLine(candidate, line) {
  if (!candidate || !line) return false;
  return String(candidate.productId) === String(line.productId);
}

/* 候选完整证据文案：出库单号 / 订单号 / 客户 / 商品 / 规格 / 基础单位 / 已生效退货 / 剩余可链接数量
   （缺失显示「未知」，不臆造） */
function lolCandidateLabel(candidate) {
  if (!candidate) return '';
  const parts = [
    candidate.stockOutNo ? `出库单 ${candidate.stockOutNo}` : `出库单 Id ${candidate.stockOutId ?? ''}`,
    candidate.salesOrderNo ? `订单 ${candidate.salesOrderNo}` : '订单 无（未关联）',
    candidate.customerName ? `客户 ${candidate.customerName}` : '',
    candidate.productName || `商品 Id ${candidate.productId ?? ''}`,
    candidate.spec ? `规格 ${candidate.spec}` : '规格 未知',
    `基础单位 ${candidate.unit || '未知'}`,
    `已生效退货 ${candidate.effectiveReturnedBaseQuantity ?? 0}`,
    `剩余可链接 ${candidate.remainingBaseQuantity ?? ''}`,
  ];
  return parts.filter(Boolean).join(' / ');
}

/* 提交闸门：必须已保存、非只读、非加载 / 提交中、且确有变更 */
function lolCanSubmit(state) {
  const s = state || {};
  if (!(Number(s.loadingListId) > 0)) return false;
  if (s.readonly || s.loading || s.submitting) return false;
  return lolIsDirty(s.draft, s.lines);
}

/* 安全转义（浏览器复用全局 escapeHtml；Node 单测走本地实现，绝不注入） */
function lolEsc(v) {
  const s = v === null || v === undefined ? '' : String(v);
  if (typeof escapeHtml === 'function') return escapeHtml(s);
  return s.replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

/* 单行 HTML：行标签 + 明细 Id + 服务端权威证据文案 + 显式选择器（未链接 / 候选 / 当前链接保留） */
function lolLineRowHtml(line, draft, candidates) {
  if (!line) return '';
  const id = String(line.loadingDetailId);
  const hasDraft = draft && Object.prototype.hasOwnProperty.call(draft, id);
  const current = hasDraft ? String(draft[id]) : lolLineDraftValue(line);
  const persisted = lolLineDraftValue(line);
  const compatible = (candidates || []).filter(c => lolCandidateMatchesLine(c, line));

  const options = [`<option value=""${current === '' ? ' selected' : ''}>未链接（清除该行出运证据链接）</option>`];
  compatible.forEach(c => {
    const value = String(c.stockOutDetailId);
    options.push(`<option value="${lolEsc(value)}"${current === value ? ' selected' : ''}>` +
      `${lolEsc(lolCandidateLabel(c))}</option>`);
  });
  /* 当前持久化链接不在候选内（剩余为 0 / 来源已不可用）：保留该选项并显式标注，绝不静默清除 */
  if (persisted !== '' && !compatible.some(c => String(c.stockOutDetailId) === persisted)) {
    options.push(`<option value="${lolEsc(persisted)}"${current === persisted ? ' selected' : ''}>` +
      `出库明细 Id ${lolEsc(persisted)}（不在当前候选内：剩余为 0 或来源不可用，原链接保留）</option>`);
  }

  return `<tr data-detail-id="${lolEsc(id)}">
    <td>${lolEsc(lolLineLabel(line))}</td>
    <td>明细 Id ${lolEsc(id)}</td>
    <td>${lolEsc(lolLineEvidenceText(line))}</td>
    <td><select id="lol-line-${lolEsc(id)}" onchange="lolSelectLine(this, ${lolEsc(id)})">${options.join('')}</select></td>
  </tr>`;
}

/* 候选证据行：出库明细 Id / 出库单号 / 出库日期 / 订单号 / 客户 / 商品 / 规格 / 基础单位 /
   已生效退货 / 剩余可链接（全部来自服务端权威只读查询） */
function lolCandidateRowHtml(candidate) {
  if (!candidate) return '';
  return `<tr>
    <td>${lolEsc(candidate.stockOutDetailId)}</td>
    <td>${lolEsc(candidate.stockOutNo || '')}</td>
    <td>${lolEsc(candidate.stockOutDate || '')}</td>
    <td>${lolEsc(candidate.salesOrderNo || '无（未关联）')}</td>
    <td>${lolEsc(candidate.customerName || '')}</td>
    <td>${lolEsc(candidate.productName || '')}</td>
    <td>${lolEsc(candidate.spec || '')}</td>
    <td>${lolEsc(candidate.unit || '')}</td>
    <td>${lolEsc(candidate.effectiveReturnedBaseQuantity ?? '')}</td>
    <td>${lolEsc(candidate.remainingBaseQuantity ?? '')} / ${lolEsc(candidate.sourceBaseQuantity ?? '')}</td>
  </tr>`;
}

const LOL_ERROR_PREFIX = {
  network: '无法连接服务器', unauthorized: '登录已过期或权限不足', server: '服务端拒绝',
};

/* 失败展示：网络 / 授权 / 服务端失败均显式可见（草稿输入由调用方保留） */
function lolErrorHtml(kind, message) {
  const prefix = LOL_ERROR_PREFIX[kind] || '操作失败';
  return `<div class="lol-error" role="alert" style="padding:8px 10px;background:#fef2f2;border:1px solid #fecaca;border-radius:8px;color:#b91c1c;font-size:13px;margin-bottom:8px">` +
    `${lolEsc(prefix)}：${lolEsc(message || '')}</div>`;
}

function lolStatusBadgeHtml() {
  const editable = lolIsEditableStatus(LOL.status);
  return `<span class="status ${editable ? 'status-info' : 'status-neutral'}">${lolEsc(lolStatusText(LOL.status))}</span>`;
}

/* ==================== 界面（一次对话 = 一个装柜清单） ==================== */

function lolReset() {
  LOL = {
    loadingListId: 0, loadingListNo: '', status: '', lines: [], candidates: [],
    draft: {}, selectionTokens: {}, linesToken: 0, keyword: '', take: LOL_TAKE_DEFAULT,
    hint: '', error: '', loading: false, submitting: false, readonly: true, result: '',
  };
}

function lolErrorMessage(err) {
  const msg = err && err.message ? String(err.message) : '';
  return msg || '网络或服务端异常，请重试（草稿输入已保留）';
}

function lolBodyHtml() {
  const editable = lolIsEditableStatus(LOL.status);
  const linesHtml = LOL.lines.map(l => lolLineRowHtml(l, LOL.draft, LOL.candidates)).join('');
  const candidatesHtml = LOL.candidates.map(lolCandidateRowHtml).join('');
  const assignments = lolBuildAssignments(LOL.draft, LOL.lines);
  const canSubmit = lolCanSubmit(LOL);

  return `
    <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:8px">
      <h3 style="margin:0">🔗 出运证据链接
        <span style="font-size:13px;color:#64748b;font-weight:400;margin-left:10px">
          逐行显式指定该装柜行由哪一条已审核销售出库明细证明（物理出运 / 追溯证据，不是库存预留、不改库存与财务）</span></h3>
      <button class="btn btn-neutral btn-sm" onclick="closeModal()">关闭</button>
    </div>
    <div style="padding:8px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;color:#475569;font-size:13px;margin-bottom:10px">
      单号 <b>${lolEsc(LOL.loadingListNo || '未保存')}</b> · 状态 ${lolStatusBadgeHtml()} ·
      只接受显式选择的销售出库明细 Id，绝不按出库单号 / 商品 / 文本猜测来源；历史未链接保持「未链接」，来源不可用时原链接原样保留。
    </div>
    ${LOL.hint ? `<div style="padding:6px 10px;background:#eff6ff;border:1px solid #bfdbfe;border-radius:8px;color:#1e40af;font-size:13px;margin-bottom:8px">${lolEsc(LOL.hint)}</div>` : ''}
    ${LOL.result ? `<div style="padding:6px 10px;background:#f0fdf4;border:1px solid #bbf7d0;border-radius:8px;color:#166534;font-size:13px;margin-bottom:8px">${lolEsc(LOL.result)}</div>` : ''}
    ${LOL.error ? lolErrorHtml('server', LOL.error) : ''}
    ${LOL.loading ? '<div style="padding:20px;text-align:center;color:#64748b">正在加载服务端持久化结果…</div>' : ''}
    ${!editable && LOL.status ? `<div style="padding:6px 10px;background:#fff7ed;border:1px solid #fed7aa;border-radius:8px;color:#9a3412;font-size:13px;margin-bottom:8px">${lolEsc(lolReadonlyReason(LOL.status))}</div>` : ''}

    <div style="display:flex;align-items:center;justify-content:space-between;margin:10px 0 6px">
      <b style="font-size:14px">装柜明细行（${LOL.lines.length} 行）</b>
      <div>
        <button class="btn btn-neutral btn-sm" onclick="lolReload()" ${LOL.loading || LOL.submitting ? 'disabled' : ''}>⟳ 重新加载服务端结果</button>
        <button class="btn btn-primary btn-sm" id="lol-submit" onclick="lolSubmit()" ${canSubmit ? '' : 'disabled'}>
          ${LOL.submitting ? '提交中…' : `提交出运证据链接（${assignments.length} 行变更）`}
        </button>
      </div>
    </div>
    <div class="table-wrap" style="max-height:280px;overflow:auto">
      <table class="data-table"><thead><tr>
        <th>装柜明细</th><th>服务端行 Id</th><th>当前链接证据</th><th style="min-width:320px">显式出运证据（销售出库明细）</th>
      </tr></thead><tbody>${linesHtml || '<tr><td colspan="4" style="text-align:center;color:#999">该单据暂无未删除明细行</td></tr>'}</tbody></table>
    </div>

    <div style="display:flex;align-items:center;justify-content:space-between;margin:14px 0 6px">
      <b style="font-size:14px">可链接出运证据候选（服务端有界只读：本单权威客户下「已审核、未删除」销售出库明细）</b>
      <div>
        <input type="text" id="lol-keyword" placeholder="出库单号 / 商品名称" value="${lolEsc(LOL.keyword)}"
               onkeydown="if(event.key==='Enter')lolSearchCandidates()">
        <select id="lol-take" onchange="lolSearchCandidates()">
          ${[LOL_TAKE_DEFAULT, 100, LOL_TAKE_MAX].map(n =>
            `<option value="${n}"${Number(LOL.take) === n ? ' selected' : ''}>取 ${n} 条</option>`).join('')}
        </select>
        <button class="btn btn-neutral btn-sm" onclick="lolSearchCandidates()" ${LOL.loading || LOL.submitting ? 'disabled' : ''}>查询候选</button>
      </div>
    </div>
    <div class="table-wrap" style="max-height:260px;overflow:auto">
      <table class="data-table"><thead><tr>
        <th>出库明细 Id</th><th>出库单号</th><th>出库日期</th><th>订单号</th><th>客户</th><th>商品</th>
        <th>规格</th><th>基础单位</th><th>已生效退货</th><th>剩余 / 来源</th>
      </tr></thead><tbody>${candidatesHtml || '<tr><td colspan="10" style="text-align:center;color:#999">暂无可用候选（无剩余可链接数量即视为无可用容量，绝不猜测来源）</td></tr>'}</tbody></table>
    </div>`;
}

function lolRender() {
  const modal = document.getElementById('modal');
  if (!modal) return;
  modal.innerHTML = `<div style="max-width:1280px;margin:2vh auto;background:#fff;border-radius:14px;padding:16px 18px;max-height:94vh;overflow:auto;box-shadow:0 20px 60px rgba(0,0,0,.25)">
    ${lolBodyHtml()}
  </div>`;
  modal.style.display = 'flex';
}

/* 候选查询（有界：take 钳制在 1..LOL_TAKE_MAX；关键字原样透传，服务端再做权威过滤） */
async function lolLoadCandidates() {
  if (!(Number(LOL.loadingListId) > 0)) { LOL.candidates = []; return; }
  const take = Math.min(Math.max(Number(LOL.take) || LOL_TAKE_DEFAULT, 1), LOL_TAKE_MAX);
  LOL.take = take;
  const params = [`take=${take}`];
  if (LOL.keyword) params.push('keyword=' + encodeURIComponent(LOL.keyword));
  try {
    LOL.candidates = await api(`/api/container/loading-lists/${LOL.loadingListId}/stock-out-candidates?${params.join('&')}`) || [];
  } catch (err) {
    LOL.candidates = [];
    LOL.error = lolErrorMessage(err);
  }
}

/* 加载单据 + 服务端权威链接状态（含「来源不可用」证据）；失败时保留已有草稿输入 */
async function lolLoad() {
  const guard = lolGuardSavedId(LOL.loadingListId);
  if (guard) { LOL.error = guard; lolRender(); return; }

  LOL.loading = true;
  LOL.error = '';
  lolRender();
  try {
    const doc = await api(`/api/container/loading-lists/${LOL.loadingListId}`);
    LOL.loadingListNo = doc.loadingListNo || '';
    LOL.status = doc.status;
    LOL.readonly = !lolIsEditableStatus(doc.status);

    const rawLines = (doc.details || []).filter(d => d && !d.isDeleted);
    /* 服务端权威链接状态：显式链接 Id + 可用性（含「来源不可用」证据） */
    const links = await api(`/api/container/loading-lists/${LOL.loadingListId}/stock-out-links`);
    const evidenceById = new Map((links || []).map(l => [String(l.loadingDetailId), l]));
    LOL.lines = rawLines.map(d => {
      const evidence = evidenceById.get(String(d.id));
      return {
        loadingDetailId: d.id,
        productId: d.productId,
        productName: d.productName,
        quantity: d.quantity,
        sourceStockOutDetailId: evidence ? evidence.sourceStockOutDetailId : d.sourceStockOutDetailId,
        sourceAvailable: evidence ? !!evidence.sourceAvailable : undefined,
        sourceAvailabilityText: evidence ? (evidence.sourceAvailabilityText || '') : '',
      };
    });
    LOL.linesToken += 1;

    /* 重新加载：剔除已不存在的陈旧行选择，仍存在的行保留用户尚未提交的草稿输入 */
    const pruned = lolPruneStaleDraft(LOL.draft, LOL.lines);
    LOL.draft = Object.keys(pruned.draft).length ? pruned.draft : lolInitialDraft(LOL.lines);
    LOL.selectionTokens = {};
    if (pruned.staleLineIds.length) {
      LOL.hint = `已在别处删除 / 替换 ${pruned.staleLineIds.length} 条明细行：对应的未提交选择已剔除，请重新确认。`;
    }

    await lolLoadCandidates();
  } catch (err) {
    LOL.error = lolErrorMessage(err);
  }
  LOL.loading = false;
  lolRender();
}

async function lolReload() {
  if (LOL.submitting) return;
  await lolLoad();
}

async function lolSearchCandidates() {
  const kwEl = document.getElementById('lol-keyword');
  const takeEl = document.getElementById('lol-take');
  LOL.keyword = kwEl ? kwEl.value.trim() : '';
  if (takeEl) LOL.take = Number(takeEl.value) || LOL_TAKE_DEFAULT;
  await lolLoadCandidates();
  lolRender();
}

/* 显式选择：只接受正整数（或清空）；非法值被拒绝并保留原草稿 */
function lolSelectLine(selectEl, detailId) {
  if (LOL.readonly) {
    toast(lolReadonlyReason(LOL.status), 'error');
    lolRender();
    return;
  }
  const applied = lolApplySelection(
    LOL.draft, LOL.lines, LOL.selectionTokens, detailId,
    selectEl ? selectEl.value : '', LOL.linesToken);
  if (applied.rejected) {
    toast('只接受显式选择的销售出库明细 Id（不接受文本 / 非法值）', 'error');
    lolRender();
    return;
  }
  LOL.draft = applied.draft;
  LOL.selectionTokens = applied.selectionTokens;
  LOL.result = '';
  lolRender();
}

/* 提交：双击 / 重复提交被 submitting 闸门阻断；陈旧选择必须先重新加载确认；
   失败保留草稿输入并显示服务端失败原因（菜单授权 / 客户范围 / 商品 / 单位 / 容量 / 状态由服务端权威判定）。 */
async function lolSubmit() {
  if (LOL.submitting) return;
  if (LOL.readonly) { toast(lolReadonlyReason(LOL.status), 'error'); return; }
  const guard = lolGuardSavedId(LOL.loadingListId);
  if (guard) { toast(guard, 'error'); return; }

  const assignments = lolBuildAssignments(LOL.draft, LOL.lines);
  if (!assignments.length) { toast('没有需要提交的出运证据变更', 'warning'); return; }

  const stale = lolStaleSelections(assignments, LOL.selectionTokens, LOL.linesToken);
  if (stale.length) {
    LOL.error = '装柜清单明细已在别处变更：请先「重新加载服务端结果」并重新确认这些行的出运证据选择后再提交';
    lolRender();
    return;
  }

  LOL.submitting = true;
  LOL.error = '';
  lolRender();
  try {
    const result = await api(`/api/container/loading-lists/${LOL.loadingListId}/stock-out-links`, 'POST', {
      links: assignments.map(a => ({
        loadingDetailId: a.loadingDetailId,
        sourceStockOutDetailId: a.sourceStockOutDetailId,
      })),
    });
    LOL.submitting = false;
    LOL.draft = {};
    LOL.selectionTokens = {};
    LOL.result = `服务端已持久化：新增 / 变更 ${result && result.linkedCount != null ? result.linkedCount : 0} 行，`
      + `清除 ${result && result.clearedCount != null ? result.clearedCount : 0} 行`;
    await lolLoad();
  } catch (err) {
    LOL.submitting = false;
    LOL.error = lolErrorMessage(err);
    lolRender();
  }
}

/* 入口：装柜清单列表行操作（saved-draft 行）→ 出运证据链接工作台 */
async function openLoadingOutboundLinks(loadingListId) {
  lolReset();
  const guard = lolGuardSavedId(loadingListId);
  if (guard) { toast(guard, 'error'); return; }
  LOL.loadingListId = Number(loadingListId);
  LOL.loading = true;
  lolRender();
  await lolLoad();
}

/* Node 单测导出（浏览器中 module 为 undefined，自动跳过） */
if (typeof module !== 'undefined' && module.exports) {
  module.exports = {
    LOL_EDITABLE_STATUS,
    LOL_TAKE_DEFAULT,
    LOL_TAKE_MAX,
    LOL_UNLINKED_TEXT,
    LOL_LINKED_TEXT,
    LOL_UNAVAILABLE_TEXT,
    LOL_UNSAVED_TEXT,
    lolNormalizeStatus,
    lolStatusText,
    lolIsEditableStatus,
    lolReadonlyReason,
    lolGuardSavedId,
    lolLineSourceId,
    lolLineDraftValue,
    lolInitialDraft,
    lolLineEvidenceText,
    lolLineLabel,
    lolNormalizeSelection,
    lolApplySelection,
    lolBuildAssignments,
    lolIsDirty,
    lolStaleSelections,
    lolPruneStaleDraft,
    lolCandidateMatchesLine,
    lolCandidateLabel,
    lolCanSubmit,
    lolEsc,
    lolLineRowHtml,
    lolCandidateRowHtml,
    lolErrorHtml,
    lolStatusBadgeHtml,
    lolErrorMessage,
  };
}





