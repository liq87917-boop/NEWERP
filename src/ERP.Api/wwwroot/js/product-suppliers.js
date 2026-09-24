/* ============================================================
   ========== 商品 / SKU 货源关系（多供应商货源指引）— ERP-038 ==========
   ============================================================
   定位：主数据关系——把一个商品（或它的一个启用规格）与多个既有供应商关联起来，
   记录供应商货号、采购单位、最小起订量、交期、启用状态与「该范围的首选」。
   边界（界面侧同样遵守）：
     1. 本模块只调用 /api/base/products/{id}/suppliers 与
        /api/base/suppliers/{id}/sourcing，只维护货源关系自身；
     2. 不自动选择供应商、不报价、不定价、不审批价；
     3. 不生成 / 改写采购报价、采购订单、库存成本与库存流水，也不改任何历史单据；
     4. 停用关系历史可读（带显式标注）但不再作为启用货源；首选切换只能通过「设为首选」显式完成。
   标注文案与服务端 ProductSupplierRules.UnavailableMark / AvailabilityText 保持一致。
   ============================================================ */

const PRODUCT_SUPPLIER_UNAVAILABLE_MARK = '（已停用/不可用）';

/* 当前打开的商品 / 编辑中的关系 / 关系列表 / 下拉数据源（供应商、启用规格） */
let __psProductId = null;
let __psEditingId = null;
let __psRows = [];
let __psSuppliers = [];
let __psVariants = [];

/* 商品列表「货源数」单元格：0 条 = 未维护货源指引；存在停用关系时显式提示，避免历史关系被误认为丢失 */
function productSourcingCellHtml(row) {
  const active = Number(row.sourcingCount || 0);
  const total = Number(row.sourcingTotalCount || 0);
  if (total <= 0) return '<span class="text-muted">—（未维护）</span>';
  const disabled = total - active;
  const mark = disabled > 0
    ? ` <span class="text-muted">（含 ${disabled} 条停用）</span>`
    : '';
  return `<span class="status ${active > 0 ? 'status-info' : 'status-neutral'}">${active} 启用</span>`
    + `<span class="text-muted"> / ${total} 条</span>${mark}`;
}

/* 供应商列表「供货商品」单元格：含义与商品侧一致（仅计数，不改动任何采购数据） */
function supplierSourcingCellHtml(row) {
  const active = Number(row.sourcingCount || 0);
  const total = Number(row.sourcingTotalCount || 0);
  if (total <= 0) return '<span class="text-muted">—（未维护）</span>';
  const disabled = total - active;
  const mark = disabled > 0
    ? ` <span class="text-muted">（含 ${disabled} 条停用）</span>`
    : '';
  return `<span class="status ${active > 0 ? 'status-info' : 'status-neutral'}">${active} 商品</span>`
    + `<span class="text-muted"> / ${total} 条</span>${mark}`;
}

/* 打开商品侧货源维护视图（商品列表「更多」菜单 → 供应商货源） */
async function openProductSuppliers(id) {
  __psProductId = id;
  __psEditingId = null;
  __psRows = [];
  __psSuppliers = [];
  __psVariants = [];
  await psRender();
}

/* 拉取商品信息、货源关系列表、启用供应商与启用规格，并渲染弹窗 */
async function psRender() {
  const productId = __psProductId;
  try {
    const product = await api(`/api/base/products/${productId}`);
    const rows = await api(`/api/base/products/${productId}/suppliers`);
    if (productId !== __psProductId) return;                  // 已切换到别的商品：丢弃过期响应
    __psRows = Array.isArray(rows) ? rows : (rows.items || []);

    /* 下拉数据源：供应商只列启用中的；规格只列该商品启用中的（停用规格不接受新选择，历史关系仍可读） */
    const suppliers = await api('/api/base/suppliers/all');
    const variants = await api(`/api/base/products/${productId}/variants/options`);
    if (productId !== __psProductId) return;
    __psSuppliers = (Array.isArray(suppliers) ? suppliers : []).filter(s => Number(s.status) === 1);
    __psVariants = (Array.isArray(variants) ? variants : []).filter(v => Number(v.status) === 1);

    const activeCount = __psRows.filter(r => Number(r.status) === 1).length;
    const disabledCount = __psRows.length - activeCount;
    const rowsHtml = __psRows.length
      ? __psRows.map(psRowHtml).join('')
      : '<tr><td colspan="9" class="text-center text-muted">暂无货源关系（不维护即视为没有货源指引，采购流程不受影响）</td></tr>';

    const supplierOptions = __psSuppliers
      .map(s => `<option value="${s.id}">${escapeHtml(s.supplierCode || '')} ${escapeHtml(s.supplierName || '')}</option>`)
      .join('');
    const variantOptions = __psVariants
      .map(v => `<option value="${v.id}">${escapeHtml(v.variantCode || '')} ${escapeHtml(v.variantName || '')}</option>`)
      .join('');

    const modal = document.getElementById('modal');
    modal.innerHTML = `<div class="modal modal-lg" style="width:1120px;max-width:96vw">
      <h3>🏭 货源关系（多供应商指引）— ${escapeHtml(product.productCode || '')} ${escapeHtml(product.productName || '')}</h3>
      <div class="toolbar" style="margin:8px 0">
        <div class="toolbar-left">
          <span class="text-muted">共 ${__psRows.length} 条（启用 ${activeCount} / 停用 ${disabledCount}）。
          货源关系只是人工比价与下单前的指引：系统不会自动选择供应商、不会改价，也不会改动采购报价 / 采购订单 / 库存与任何历史单据。</span>
        </div>
      </div>
      <div class="form-grid" style="margin-bottom:10px">
        <div class="form-item"><label>供应商 *</label>
          <select id="ps-supplierId">${supplierOptions || '<option value="">（没有启用中的供应商）</option>'}</select></div>
        <div class="form-item"><label>货源归属</label>
          <select id="ps-variantId">
            <option value="">商品级（整品通用）</option>
            ${variantOptions}
          </select></div>
        <div class="form-item"><label>供应商货号 / 款号</label>
          <input type="text" id="ps-itemCode" maxlength="100" placeholder="可选，如 F-8899"></div>
        <div class="form-item"><label>采购单位</label>
          <input type="text" id="ps-unit" maxlength="20" placeholder="可选，如 箱 / 打 / 个"></div>
        <div class="form-item"><label>最小起订量 MOQ</label>
          <input type="number" id="ps-moq" step="0.01" min="0" placeholder="0 = 未指定"></div>
        <div class="form-item"><label>交期天数</label>
          <input type="number" id="ps-leadTime" step="1" min="0" placeholder="0 = 未指定"></div>
        <div class="form-item"><label>首选货源</label>
          <select id="ps-preferred">
            <option value="false">否</option>
            <option value="true">是（该范围内唯一）</option>
          </select></div>
        <div class="form-item"><label>备注</label>
          <input type="text" id="ps-remark" maxlength="500" placeholder="可选"></div>
      </div>
      <div class="toolbar" style="margin:0 0 8px">
        <div class="toolbar-left"><span class="text-muted" id="ps-form-hint">同一「商品 / 规格 + 供应商」只能维护一条；更换首选请用列表中的「设为首选」（服务端会先释放旧首选）。</span></div>
        <div class="toolbar-actions">
          <button class="btn btn-neutral" onclick="psResetForm()">清空表单</button>
          <button class="btn btn-primary" id="ps-save-btn" onclick="psSubmit()">保存</button>
        </div>
      </div>
      <div class="table-wrap" style="max-height:46vh;overflow:auto">
        <table><thead><tr>
          <th>货源归属</th><th>供应商</th><th>供应商货号</th><th>采购单位</th>
          <th class="text-right">MOQ</th><th class="text-right">交期(天)</th><th>首选</th><th>状态 / 可用性</th><th style="width:230px">操作</th>
        </tr></thead><tbody>${rowsHtml}</tbody></table>
      </div>
      <div class="toolbar" style="margin-top:10px">
        <div class="toolbar-actions">
          <button class="btn btn-neutral" onclick="psClose()">关闭</button>
        </div>
      </div>
    </div>`;
    modal.style.display = 'flex';
  } catch (err) { toast(err.message, 'error'); }
}

/* 单行渲染：归属（商品级 / 规格级）与可用性都显式标注，避免历史关系被误认为可用货源 */
function psRowHtml(r) {
  const scopeBadge = Number(r.variantId || 0) > 0
    ? `<span class="status status-info">规格级</span> <span class="text-muted">${escapeHtml(r.variantCode || '')} ${escapeHtml(r.variantName || '')}</span>`
    : '<span class="status status-neutral">商品级</span> <span class="text-muted">整品通用</span>';
  const supplierCell = Number(r.supplierAvailable) === 1 || r.supplierAvailable === true
    ? escapeHtml(r.supplierName || '')
    : `${escapeHtml(r.supplierName || '')}<span class="text-muted">${PRODUCT_SUPPLIER_UNAVAILABLE_MARK}</span>`;
  const preferredMark = r.isPreferred
    ? '<span class="status status-success">首选</span>'
    : '<span class="text-muted">—</span>';
  const statusMark = Number(r.status) === 1
    ? `<span class="status status-info">启用</span><div class="text-muted">${escapeHtml(r.availabilityText || '')}</div>`
    : `<span class="status status-neutral">停用</span><div class="text-muted">${escapeHtml(r.availabilityText || '')}</div>`;
  const toggle = Number(r.status) === 1
    ? `<button class="btn btn-neutral btn-sm" onclick="psSetStatus(${r.id}, false)">停用</button>`
    : `<button class="btn btn-neutral btn-sm" onclick="psSetStatus(${r.id}, true)">启用</button>`;
  const preferredAction = Number(r.status) === 1
    ? (r.isPreferred
      ? `<button class="btn btn-neutral btn-sm" onclick="psSetPreferred(${r.id}, false)">取消首选</button>`
      : `<button class="btn btn-neutral btn-sm" onclick="psSetPreferred(${r.id}, true)">设为首选</button>`)
    : '';

  return `<tr>
    <td>${scopeBadge}</td>
    <td>${supplierCell}</td>
    <td>${escapeHtml(r.supplierItemCode || '')}</td>
    <td>${escapeHtml(r.purchaseUnit || '')}</td>
    <td class="text-right">${Number(r.minOrderQty || 0) || ''}</td>
    <td class="text-right">${Number(r.leadTimeDays || 0) || ''}</td>
    <td>${preferredMark}</td>
    <td>${statusMark}</td>
    <td>
      <div class="row-actions">
        <button class="btn btn-neutral btn-sm" onclick="psEdit(${r.id})">编辑</button>
        ${preferredAction}
        ${toggle}
        <button class="btn btn-neutral btn-sm" onclick="psDelete(${r.id})">删除</button>
      </div>
    </td>
  </tr>`;
}

/* 保存（新增或更新）：服务端负责引用校验、规范化与唯一性判定，界面只提交原始输入 */
async function psSubmit() {
  const productId = __psProductId;
  const supplierId = Number((document.getElementById('ps-supplierId') || {}).value || 0);
  const variantRaw = (document.getElementById('ps-variantId') || {}).value || '';
  if (!supplierId) { toast('请选择供应商（如列表为空说明没有启用中的供应商）', 'error'); return; }

  const body = {
    supplierId: supplierId,
    variantId: variantRaw ? Number(variantRaw) : null,
    supplierItemCode: (document.getElementById('ps-itemCode') || {}).value || '',
    purchaseUnit: (document.getElementById('ps-unit') || {}).value || '',
    minOrderQty: Number((document.getElementById('ps-moq') || {}).value || 0),
    leadTimeDays: Number((document.getElementById('ps-leadTime') || {}).value || 0),
    isPreferred: String((document.getElementById('ps-preferred') || {}).value) === 'true',
    remark: (document.getElementById('ps-remark') || {}).value || '',
  };

  try {
    if (__psEditingId) {
      await api(`/api/base/products/${productId}/suppliers/${__psEditingId}`, 'PUT', body);
      toast('货源关系已更新');
      __psEditingId = null;
    } else {
      await api(`/api/base/products/${productId}/suppliers`, 'POST', body);
      toast('货源关系已新增');
    }
    await psRender();
    if (CURRENT_LOADER) CURRENT_LOADER();      // 列表「货源数」同步刷新
  } catch (err) { toast(err.message, 'error'); }
}

/* 进入编辑态：把该关系的值回填到表单（含商品级 / 规格级归属） */
function psEdit(relationId) {
  __psEditingId = Number(relationId);
  psFillForm(__psEditingId);
}

/* 下拉里若没有该值（供应商 / 规格已停用或已删除，不在启用选项中），补一个仅用于回显的选项：
   保证历史货源关系仍可打开编辑；引用未更换时服务端允许继续保存其他字段。 */
function psEnsureOption(selectId, value, label) {
  const el = document.getElementById(selectId);
  if (!el || !value) return;
  if (Array.from(el.options).some(o => String(o.value) === String(value))) { el.value = String(value); return; }
  const option = document.createElement('option');
  option.value = String(value);
  option.textContent = label;
  el.appendChild(option);
  el.value = String(value);
}

function psFillForm(relationId) {
  const row = __psRows.find(r => Number(r.id) === Number(relationId));
  if (!row) return;
  const set = (id, value) => { const el = document.getElementById(id); if (el) el.value = value; };
  set('ps-supplierId', String(row.supplierId || ''));
  set('ps-variantId', row.variantId ? String(row.variantId) : '');
  psEnsureOption('ps-supplierId', row.supplierId,
    `${row.supplierCode || ''} ${row.supplierName || ''} ${PRODUCT_SUPPLIER_UNAVAILABLE_MARK}`.trim());
  psEnsureOption('ps-variantId', row.variantId,
    `${row.variantCode || ''} ${row.variantName || ''} ${PRODUCT_SUPPLIER_UNAVAILABLE_MARK}`.trim());
  set('ps-itemCode', row.supplierItemCode || '');
  set('ps-unit', row.purchaseUnit || '');
  set('ps-moq', row.minOrderQty ? String(row.minOrderQty) : '');
  set('ps-leadTime', row.leadTimeDays ? String(row.leadTimeDays) : '');
  set('ps-preferred', row.isPreferred ? 'true' : 'false');
  set('ps-remark', row.remark || '');
  const hint = document.getElementById('ps-form-hint');
  if (hint) {
    hint.textContent = `正在编辑「${row.scopeText} / ${row.supplierName}」，保存后立即生效；`
      + '供应商或规格被停用 / 删除时，未更换的引用仍允许继续编辑（历史关系保持可读）。';
  }
}

/* 退出编辑态并清空表单（不修改任何数据） */
function psResetForm() {
  __psEditingId = null;
  ['itemCode', 'unit', 'moq', 'leadTime', 'remark'].forEach(k => {
    const el = document.getElementById('ps-' + k);
    if (el) el.value = '';
  });
  const preferred = document.getElementById('ps-preferred');
  if (preferred) preferred.value = 'false';
  const variant = document.getElementById('ps-variantId');
  if (variant) variant.value = '';
  const hint = document.getElementById('ps-form-hint');
  if (hint) {
    hint.textContent = '同一「商品 / 规格 + 供应商」只能维护一条；更换首选请用列表中的「设为首选」（服务端会先释放旧首选）。';
  }
}

/* 设为首选 / 取消首选：服务端在显式切换时先释放同范围旧首选，再置新首选（与列表顺序无关） */
async function psSetPreferred(relationId, preferred) {
  const productId = __psProductId;
  try {
    await api(`/api/base/products/${productId}/suppliers/${relationId}/preferred?preferred=${preferred ? 'true' : 'false'}`, 'POST');
    toast(preferred ? '已设为该范围的首选货源' : '已取消首选');
    await psRender();
    if (CURRENT_LOADER) CURRENT_LOADER();
  } catch (err) { toast(err.message, 'error'); }
}

/* 停用 / 启用：停用会同时释放首选标记；重新启用需商品 / 规格 / 供应商仍可用 */
async function psSetStatus(relationId, enable) {
  const productId = __psProductId;
  try {
    await api(`/api/base/products/${productId}/suppliers/${relationId}/${enable ? 'enable' : 'disable'}`, 'POST');
    toast(enable ? '货源关系已启用' : '货源关系已停用（历史仍可读，不再作为启用货源）');
    await psRender();
    if (CURRENT_LOADER) CURRENT_LOADER();
  } catch (err) { toast(err.message, 'error'); }
}

/* 删除：本表软删除（保留历史行，不改动任何采购单据与库存） */
async function psDelete(relationId) {
  const productId = __psProductId;
  const row = __psRows.find(r => Number(r.id) === Number(relationId));
  const name = row ? `${row.scopeText} / ${row.supplierName || ''}` : '';
  if (!confirm(`删除货源关系「${name}」？仅从主数据中移除该货源指引，不改动采购报价 / 采购订单 / 库存与任何历史单据。`)) return;
  try {
    await api(`/api/base/products/${productId}/suppliers/${relationId}`, 'DELETE');
    toast('货源关系已删除');
    if (Number(__psEditingId) === Number(relationId)) psResetForm();
    await psRender();
    if (CURRENT_LOADER) CURRENT_LOADER();
  } catch (err) { toast(err.message, 'error'); }
}

/* 关闭商品侧弹窗（不修改数据） */
function psClose() {
  __psEditingId = null;
  closeModal();
}

/* ============ 供应商侧：该供应商为哪些商品 / 规格供货（只读，有界列表） ============ */

let __ssSupplierId = null;
let __ssRows = [];

/* 打开供应商侧货源视图（供应商列表「更多」菜单 → 供货商品） */
async function openSupplierSourcing(id) {
  __ssSupplierId = id;
  __ssRows = [];
  await ssRender();
}

async function ssRender() {
  const supplierId = __ssSupplierId;
  try {
    const supplier = await api(`/api/base/suppliers/${supplierId}`);
    const rows = await api(`/api/base/suppliers/${supplierId}/sourcing`);
    if (supplierId !== __ssSupplierId) return;                 // 已切换到别的供应商：丢弃过期响应
    __ssRows = Array.isArray(rows) ? rows : (rows.items || []);

    const activeCount = __ssRows.filter(r => Number(r.status) === 1).length;
    const rowsHtml = __ssRows.length
      ? __ssRows.map(ssRowHtml).join('')
      : '<tr><td colspan="7" class="text-center text-muted">暂无货源关系（该供应商还没有被维护为任何商品的货源）</td></tr>';

    const modal = document.getElementById('modal');
    modal.innerHTML = `<div class="modal modal-lg" style="width:1040px;max-width:96vw">
      <h3>🏭 供货商品（货源关系）— ${escapeHtml(supplier.supplierCode || '')} ${escapeHtml(supplier.supplierName || '')}</h3>
      <div class="toolbar" style="margin:8px 0">
        <div class="toolbar-left">
          <span class="text-muted">共 ${__ssRows.length} 条（启用 ${activeCount} / 停用 ${__ssRows.length - activeCount}）。
          本视图只读：货源关系只是比价与下单前的指引，不改动采购报价 / 采购订单 / 库存与任何历史单据；
          新增 / 修改请到「商品资料 → 供应商货源」维护。</span>
        </div>
      </div>
      <div class="table-wrap" style="max-height:56vh;overflow:auto">
        <table><thead><tr>
          <th>商品</th><th>货源归属</th><th>供应商货号</th><th>采购单位</th>
          <th class="text-right">MOQ</th><th class="text-right">交期(天)</th><th>首选 / 状态</th>
        </tr></thead><tbody>${rowsHtml}</tbody></table>
      </div>
      <div class="toolbar" style="margin-top:10px">
        <div class="toolbar-actions">
          <button class="btn btn-neutral" onclick="ssClose()">关闭</button>
        </div>
      </div>
    </div>`;
    modal.style.display = 'flex';
  } catch (err) { toast(err.message, 'error'); }
}

/* 单行渲染（只读）：归属与可用性都显式标注，停用关系仍可读但不再作为启用货源 */
function ssRowHtml(r) {
  const scopeBadge = Number(r.variantId || 0) > 0
    ? `<span class="status status-info">规格级</span> <span class="text-muted">${escapeHtml(r.variantCode || '')} ${escapeHtml(r.variantName || '')}</span>`
    : '<span class="status status-neutral">商品级</span> <span class="text-muted">整品通用</span>';
  const preferredMark = r.isPreferred
    ? '<span class="status status-success">首选</span>'
    : '<span class="text-muted">—</span>';
  const statusMark = Number(r.status) === 1
    ? '<span class="status status-info">启用</span>'
    : '<span class="status status-neutral">停用</span>';

  return `<tr>
    <td>${escapeHtml(r.productCode || '')} ${escapeHtml(r.productName || '')}</td>
    <td>${scopeBadge}</td>
    <td>${escapeHtml(r.supplierItemCode || '')}</td>
    <td>${escapeHtml(r.purchaseUnit || '')}</td>
    <td class="text-right">${Number(r.minOrderQty || 0) || ''}</td>
    <td class="text-right">${Number(r.leadTimeDays || 0) || ''}</td>
    <td>${preferredMark} ${statusMark}<div class="text-muted">${escapeHtml(r.availabilityText || '')}</div></td>
  </tr>`;
}

/* 关闭供应商侧弹窗（不修改数据） */
function ssClose() {
  closeModal();
}
