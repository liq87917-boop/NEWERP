/* ============================================================
   ========== 装柜清单多客户参与方（一柜多客户）— ERP-041 ==========
   ============================================================
   定位：装柜清单的**客户归属清单**——记录「这柜装了哪几个客户、哪个是主客户」，
   并显式指定其中一条为「主参与方」（主参与方会同步到装柜清单原有的兼容客户字段 CustomerId）。
   边界（界面侧同样遵守）：
     1. 本模块只调用 /api/container/loading-lists/{id} 与 .../{id}/participants，
        只维护参与方自身与装柜清单的兼容客户字段；
     2. 不按数量 / 体积 / 金额给客户分摊费用、不生成费用单或结算记录；
     3. 不改写装柜明细数量 / 箱数 / 重量 / 体积、柜号、订柜外贸与物流跟踪值、单证、库存与客户主数据；
     4. 停用参与方历史可读（带显式标注）但不再作为参与客户；主参与方只能通过「设为主参与方」显式转移；
        没有任何参与方行的清单按**历史单客户视图**只读说明，读取不会写库。
   标注文案与服务端 ContainerLoadingParticipantRules 保持一致。
   ============================================================ */

const LLP_UNAVAILABLE_MARK = '（已停用/不可用）';
const LLP_LEGACY_TEXT = '历史单客户视图（未维护参与方，客户归属沿用装柜清单原有客户字段）';
const LLP_NO_PRIMARY_TEXT = '未指定主参与方（兼容客户字段沿用装柜清单原值）';

/* 当前打开的装柜清单 / 清单详情 / 参与方列表 / 客户下拉（全部 + 仅启用） / 正在编辑的参与方 */
let __llpListId = null;
let __llpList = null;
let __llpRows = [];
let __llpCustomers = [];
let __llpAllCustomers = [];
let __llpEditingId = null;

/* 装柜清单列表「客户参与方」单元格：0 条 = 历史单客户视图（沿用原客户字段）；
   有参与方时显示启用数 / 总数、停用数与主参与方，避免历史参与方被误认为丢失 */
function loadingListParticipantCellHtml(row) {
  const active = Number(row.participantCount || 0);
  const total = Number(row.participantTotalCount || 0);
  if (total <= 0) return '<span class="text-muted">—（单客户）</span>';

  const disabled = total - active;
  const mark = disabled > 0 ? ` <span class="text-muted">（含 ${disabled} 条停用）</span>` : '';
  const primary = row.primaryParticipantCustomerId
    ? `<div class="text-muted">主：${escapeHtml(row.primaryParticipantCustomerName || '')
      }${row.primaryParticipantAvailable === false ? LLP_UNAVAILABLE_MARK : ''}</div>`
    : `<div class="text-muted">${LLP_NO_PRIMARY_TEXT}</div>`;

  return `<span class="status ${active > 0 ? 'status-info' : 'status-neutral'}">${active} 启用</span>`
    + `<span class="text-muted"> / ${total} 条</span>${mark}${primary}`;
}

/* 打开参与方维护视图（装柜清单列表行操作「多客户参与方」） */
async function openLoadingListParticipants(id) {
  __llpListId = id;
  __llpList = null;
  __llpRows = [];
  __llpCustomers = [];
  __llpAllCustomers = [];
  __llpEditingId = null;
  await llpRender();
}

/* 可维护性：与后端口径一致——只有已作废（Cancelled）的装柜清单参与方只读；
   其余状态（含已审核的历史柜）可维护，因为参与方是操作性客户归属清单，只会同步兼容客户字段 */
function llpEditable() {
  return !!__llpList && String(__llpList.status) !== 'Cancelled';
}

/* 关闭参与方弹窗（不修改数据） */
function llpClose() {
  __llpEditingId = null;
  closeModal();
}

/* 拉取清单详情、参与方列表与客户下拉，并渲染弹窗 */
async function llpRender() {
  const listId = __llpListId;
  try {
    const list = await api(`/api/container/loading-lists/${listId}`);
    const rows = await api(`/api/container/loading-lists/${listId}/participants`);
    if (listId !== __llpListId) return;                 // 已切换清单：丢弃过期响应
    __llpList = list;
    __llpRows = Array.isArray(rows) ? rows : (rows.items || []);

    /* 下拉只列启用中的客户（停用 / 已删除客户不接受新选择，历史参与方仍可读、可看可否可用） */
    const customers = await api('/api/base/customers/all');
    if (listId !== __llpListId) return;
    __llpAllCustomers = Array.isArray(customers) ? customers : [];
    __llpCustomers = __llpAllCustomers.filter(c => Number(c.status) === 1);

    const modal = document.getElementById('modal');
    modal.innerHTML = llpModalHtml();
    modal.style.display = 'flex';
    llpApplyFormDefaults();
  } catch (err) { toast(err.message, 'error'); }
}

/* 弹窗 HTML：清单信息 + 参与方表单 + 有界列表（文案明确边界与可维护性） */
function llpModalHtml() {
  const list = __llpList || {};
  const editable = llpEditable();
  const activeCount = __llpRows.filter(r => Number(r.status) === 1).length;
  const disabledCount = __llpRows.length - activeCount;
  const primary = __llpRows.find(r => Number(r.status) === 1 && r.isPrimary);
  const legacy = Number(list.participantTotalCount || 0) <= 0;
  const statusNote = editable
    ? '可直接维护参与方（新增 / 编辑 / 设为主参与方 / 停用 / 删除）：只会同步兼容客户字段，'
      + '不改动柜号、装柜明细、物流跟踪值、单证、费用、库存与客户主数据。'
    : `当前状态 ${escapeHtml(String(list.status || ''))}（已作废）：参与方只读，历史参与方仍可查看。`;
  const customerId = Number(list.customerId || 0) || 0;
  const primaryNote = legacy
    ? `${LLP_LEGACY_TEXT}：兼容客户字段 CustomerId=${customerId}`
      + `（${escapeHtml(llpCustomerLabel(customerId))}），读取不写库`
    : (primary
      ? `兼容客户字段 CustomerId=${customerId}；当前主参与方：`
        + `<b>${escapeHtml(primary.customerName || '')}</b>（显式置主时同步，与列表顺序无关）`
      : `兼容客户字段 CustomerId=${customerId}；${LLP_NO_PRIMARY_TEXT}`);
  const rowsHtml = __llpRows.length
    ? __llpRows.map(llpRowHtml).join('')
    : '<tr><td colspan="6" class="text-center text-muted">暂无参与方：该清单按历史单客户视图读取，'
      + '不维护参与方不影响装柜 / 单证 / 费用 / 库存</td></tr>';

  return `<div class="modal modal-lg" style="width:1080px;max-width:96vw">
    <h3>👥 多客户参与方（一柜多客户）— ${escapeHtml(list.loadingListNo || '')} ${escapeHtml(list.containerNo || '')}</h3>
    <div class="toolbar" style="margin:8px 0"><div class="toolbar-left"><span class="text-muted">
      共 ${__llpRows.length} 条（启用 ${activeCount} / 停用 ${disabledCount}）。${statusNote}
      本视图只维护客户归属：不按数量 / 体积 / 金额分摊费用，不生成费用单，也不改动装柜明细、柜号、
      物流跟踪值、单证、库存与客户主数据。</span></div></div>
    <div class="text-muted" style="margin:6px 0">${primaryNote}</div>
    <div class="form-grid" style="margin-bottom:10px">
      <div class="form-item"><label>参与客户 *</label><select id="llp-customerId">${llpCustomerOptions()}</select></div>
      <div class="form-item"><label>主参与方</label><select id="llp-primary">
        <option value="false">否</option>
        <option value="true">是（该清单唯一，并同步兼容客户字段）</option></select></div>
      <div class="form-item"><label>排序号</label>
        <input type="number" id="llp-sortOrder" step="1" min="0" max="9999" placeholder="0 = 默认，仅影响展示顺序"></div>
      <div class="form-item"><label>备注</label>
        <input type="text" id="llp-remark" maxlength="500" placeholder="可选"></div>
    </div>
    <div class="toolbar" style="margin:0 0 8px">
      <div class="toolbar-left"><span class="text-muted" id="llp-form-hint"></span></div>
      <div class="toolbar-actions">
        <button class="btn btn-neutral" onclick="llpResetForm()">清空表单</button>
        <button class="btn btn-primary" id="llp-save-btn" onclick="llpSubmit()" ${editable ? '' : 'disabled'}>保存</button>
      </div>
    </div>
    <div class="table-wrap" style="max-height:44vh;overflow:auto">
      <table><thead><tr><th>客户</th><th>主参与方</th><th>状态 / 可用性</th>
        <th class="text-right">排序</th><th>备注</th><th style="width:280px">操作</th></tr></thead>
        <tbody>${rowsHtml}</tbody></table>
    </div>
    <div class="toolbar" style="margin-top:10px"><div class="toolbar-actions">
      <button class="btn btn-neutral" onclick="llpClose()">关闭</button></div></div>
  </div>`;
}

/* 客户下拉：只列启用中的客户；没有启用客户时给出明确提示（而不是空白下拉） */
function llpCustomerOptions() {
  if (!__llpCustomers.length) return '<option value="">（没有启用中的客户）</option>';
  return __llpCustomers
    .map(c => `<option value="${c.id}">${escapeHtml(c.customerCode || '')} ${escapeHtml(c.customerName || '')}</option>`)
    .join('');
}

/* 客户文案（用于历史单客户视图与兼容字段说明）：找不到 / 已删除时显式说明，不臆造名称 */
function llpCustomerLabel(customerId) {
  if (!customerId) return '未设置客户';
  const found = __llpAllCustomers.find(c => Number(c.id) === Number(customerId));
  if (!found) return `客户#${customerId}（未找到或已删除）`;
  const name = `${found.customerCode || ''} ${found.customerName || ''}`.trim();
  const unavailable = found.isDeleted || Number(found.status) !== 1;
  return `${name}${unavailable ? LLP_UNAVAILABLE_MARK : ''}`;
}

/* 单行渲染：客户快照 + 主参与方 + 状态 / 可用性都显式标注，历史参与方不会被静默隐藏 */
function llpRowHtml(r) {
  const available = r.customerAvailable === true;
  const customerCell = `${escapeHtml(r.customerCode || '')} ${escapeHtml(r.customerName || '')}`
    + (available ? '' : ` <span class="text-muted">${LLP_UNAVAILABLE_MARK}</span>`)
    + (r.customerRenamed
      ? `<div class="text-muted">客户当前名称：${escapeHtml(r.customerCurrentName || '')}（快照保留原名）</div>`
      : '');
  const primaryMark = r.isPrimary
    ? '<span class="status status-success">主参与方</span>'
    : '<span class="text-muted">—</span>';
  const statusMark = Number(r.status) === 1
    ? `<span class="status status-info">启用</span><div class="text-muted">${escapeHtml(r.availabilityText || '')}</div>`
    : `<span class="status status-neutral">停用</span><div class="text-muted">${escapeHtml(r.availabilityText || '')}</div>`;
  const editable = llpEditable();
  const dis = editable ? '' : ' disabled';
  const toggle = Number(r.status) === 1
    ? `<button class="btn btn-neutral btn-sm" onclick="llpSetStatus(${r.id}, false)"${dis}>停用</button>`
    : `<button class="btn btn-neutral btn-sm" onclick="llpSetStatus(${r.id}, true)"${dis}>启用</button>`;
  const primaryAction = (Number(r.status) === 1 && !r.isPrimary)
    ? `<button class="btn btn-neutral btn-sm" onclick="llpSetPrimary(${r.id})"${dis}>设为主参与方</button>`
    : '';

  return `<tr>
    <td>${customerCell}</td>
    <td>${primaryMark}</td>
    <td>${statusMark}</td>
    <td class="text-right">${Number(r.sortOrder || 0) || ''}</td>
    <td>${escapeHtml(r.remark || '')}</td>
    <td><div class="row-actions">
      <button class="btn btn-neutral btn-sm" onclick="llpEdit(${r.id})"${dis}>编辑</button>
      ${primaryAction}${toggle}
      <button class="btn btn-neutral btn-sm" onclick="llpDelete(${r.id})"${dis}>删除</button>
    </div></td>
  </tr>`;
}

/* 表单默认值：编辑态回填该行；否则清空并按「清单是否已有主参与方」给出默认主参与方选择 */
function llpApplyFormDefaults() {
  if (__llpEditingId) { llpFillForm(__llpEditingId); return; }
  llpResetForm();
}

function llpHasActivePrimary() {
  return __llpRows.some(r => Number(r.status) === 1 && r.isPrimary);
}

/* 表单提示：显式说明重复口径、主参与方切换口径与边界 */
function llpFormHint() {
  const base = '同一客户在同一清单只能维护一条（含停用记录）；主参与方改指他人请用列表中的'
    + '「设为主参与方」（服务端会先释放旧主参与方并同步兼容客户字段，与列表顺序无关）。';
  if (__llpEditingId) return base;
  return (llpHasActivePrimary()
    ? '新增参与方默认「主参与方 = 否」（清单已有主参与方）。'
    : '新增参与方默认「主参与方 = 是」（清单当前无主参与方）；如不需要可改为否。') + base;
}

/* 退出编辑态并清空表单（不修改任何数据） */
function llpResetForm() {
  __llpEditingId = null;
  const set = (id, value) => { const el = document.getElementById(id); if (el) el.value = value; };
  set('llp-sortOrder', '');
  set('llp-remark', '');
  set('llp-primary', llpHasActivePrimary() ? 'false' : 'true');
  if (__llpCustomers.length) set('llp-customerId', String(__llpCustomers[0].id));
  const hint = document.getElementById('llp-form-hint');
  if (hint) hint.textContent = llpFormHint();
}

/* 进入编辑态并回填该参与方 */
function llpEdit(participantId) {
  __llpEditingId = Number(participantId);
  llpFillForm(__llpEditingId);
}

/* 编辑回填：客户已停用 / 已删除（不在启用下拉里）时补一个仅用于回显的选项，保证历史参与方仍可维护 */
function llpFillForm(participantId) {
  const row = __llpRows.find(r => Number(r.id) === Number(participantId));
  if (!row) return;
  const set = (id, value) => { const el = document.getElementById(id); if (el) el.value = value; };
  llpEnsureOption('llp-customerId', row.customerId,
    `${row.customerCode || ''} ${row.customerName || ''} ${LLP_UNAVAILABLE_MARK}`.trim());
  set('llp-customerId', String(row.customerId || ''));
  set('llp-primary', row.isPrimary ? 'true' : 'false');
  set('llp-sortOrder', row.sortOrder ? String(row.sortOrder) : '');
  set('llp-remark', row.remark || '');
  const hint = document.getElementById('llp-form-hint');
  if (hint) {
    hint.textContent = `正在编辑「${row.customerName || ''}」：客户未更换时保留历史编码 / 名称快照`
      + '（客户后来停用 / 改名也不会被静默改写）；设为「主参与方 = 是」会同步兼容客户字段。';
  }
}

/* 下拉里若没有该值（客户已停用或已删除），补一个仅用于回显的选项 */
function llpEnsureOption(selectId, value, label) {
  const el = document.getElementById(selectId);
  if (!el || !value) return;
  if (Array.from(el.options).some(o => String(o.value) === String(value))) { el.value = String(value); return; }
  const option = document.createElement('option');
  option.value = String(value);
  option.textContent = label;
  el.appendChild(option);
  el.value = String(value);
}

/* 保存（新增或更新）：服务端负责引用校验、规范化、唯一性与兼容字段同步，界面只提交原始输入 */
async function llpSubmit() {
  const listId = __llpListId;
  const customerId = Number((document.getElementById('llp-customerId') || {}).value || 0);
  if (!customerId) { toast('请选择参与客户（如列表为空说明没有启用中的客户）', 'error'); return; }

  const body = {
    customerId: customerId,
    isPrimary: String((document.getElementById('llp-primary') || {}).value) === 'true',
    sortOrder: Number((document.getElementById('llp-sortOrder') || {}).value || 0),
    remark: (document.getElementById('llp-remark') || {}).value || ''
  };

  try {
    if (__llpEditingId) {
      await api(`/api/container/loading-lists/${listId}/participants/${__llpEditingId}`, 'PUT', body);
      toast('参与方已更新');
      __llpEditingId = null;
    } else {
      await api(`/api/container/loading-lists/${listId}/participants`, 'POST', body);
      toast('参与方已新增');
    }
    await llpRender();
    if (CURRENT_LOADER) CURRENT_LOADER();      // 列表「客户参与方」列同步刷新
  } catch (err) { toast(err.message, 'error'); }
}

/* 设为主参与方：服务端先释放旧主参与方，再把清单兼容客户字段同步为该客户（与列表顺序无关） */
async function llpSetPrimary(participantId) {
  const listId = __llpListId;
  try {
    await api(`/api/container/loading-lists/${listId}/participants/${participantId}/primary`, 'POST');
    toast('已设为主参与方，并已同步兼容客户字段');
    await llpRender();
    if (CURRENT_LOADER) CURRENT_LOADER();
  } catch (err) { toast(err.message, 'error'); }
}

/* 停用 / 启用：停用会释放主参与方标记；清单还有别的启用参与方时不能直接停用主参与方 */
async function llpSetStatus(participantId, enable) {
  const listId = __llpListId;
  try {
    await api(`/api/container/loading-lists/${listId}/participants/${participantId}/${enable ? 'enable' : 'disable'}`, 'POST');
    toast(enable ? '参与方已启用（不会自动恢复主参与方标记）' : '参与方已停用（历史仍可读）');
    await llpRender();
    if (CURRENT_LOADER) CURRENT_LOADER();
  } catch (err) { toast(err.message, 'error'); }
}

/* 删除：本表软删除（保留历史行；不改动装柜明细、跟踪值、单证、费用与库存） */
async function llpDelete(participantId) {
  const listId = __llpListId;
  const row = __llpRows.find(r => Number(r.id) === Number(participantId));
  const name = row ? `${row.customerCode || ''} ${row.customerName || ''}`.trim() : '';
  if (!confirm(`删除参与方「${name}」？仅从本清单的客户归属清单中移除（历史记录保留），`
    + '不改动装柜明细、柜号、物流跟踪值、单证、费用、库存与客户主数据。')) return;
  try {
    await api(`/api/container/loading-lists/${listId}/participants/${participantId}`, 'DELETE');
    toast('参与方已删除（历史记录保留）');
    if (Number(__llpEditingId) === Number(participantId)) llpResetForm();
    await llpRender();
    if (CURRENT_LOADER) CURRENT_LOADER();
  } catch (err) { toast(err.message, 'error'); }
}
