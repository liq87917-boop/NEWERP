/* ============================================================
   ========== 商品规格（颜色 / 尺码 SKU 变体）维护 — ERP-037 ==========
   ============================================================
   定位：商品资料下的**可选**主数据子表。商品仍是唯一权威身份：
   一个商品可以零到多条规格；没有规格即「单规格商品」，行为与历史完全一致。
   边界（界面侧同样遵守）：
     1. 本模块只调用 /api/base/products/{id}/variants，只维护规格自身；
     2. 不触达询价 / 报价 / PI / 订单 / 库存与库存流水（不拆分、不重算已有库存）；
     3. 停用规格历史可读（带显式标注）但不可再被新选中。
   标注文案与服务端 ProductVariantRules.UnavailableMark 保持一致。
   ============================================================ */

const PRODUCT_VARIANT_UNAVAILABLE_MARK = '（已停用，不可再被新选中）';

/* 当前打开的商品与正在编辑的规格（新增时为 null） */
let __pvProductId = null;
let __pvEditingId = null;
let __pvRows = [];

/* 列表「规格数」单元格：0 条 = 单规格商品；存在停用规格时显式提示，避免历史规格被误认为丢失 */
function productVariantCellHtml(row) {
  const active = Number(row.variantCount || 0);
  const total = Number(row.variantTotalCount || 0);
  if (total <= 0) return '<span class="text-muted">—（单规格）</span>';
  const disabled = total - active;
  const mark = disabled > 0
    ? ` <span class="text-muted">（含 ${disabled} 条停用）</span>`
    : '';
  return `<span class="status ${active > 0 ? 'status-info' : 'status-neutral'}">${active} 启用</span>`
    + `<span class="text-muted"> / ${total} 条</span>${mark}`;
}

/* 打开规格维护视图（商品列表「更多」菜单 → 颜色/尺码规格） */
async function openProductVariants(id) {
  __pvProductId = id;
  __pvEditingId = null;
  __pvRows = [];
  await pvRender();
}

/* 拉取商品信息与规格列表并渲染弹窗（弹窗内所有操作完成后都会重新渲染，保证计数与列表一致） */
async function pvRender() {
  const productId = __pvProductId;
  try {
    const product = await api(`/api/base/products/${productId}`);
    const rows = await api(`/api/base/products/${productId}/variants`);
    if (productId !== __pvProductId) return;                  // 已切换到别的商品：丢弃过期响应
    __pvRows = Array.isArray(rows) ? rows : (rows.items || []);

    const activeCount = __pvRows.filter(v => Number(v.status) === 1).length;
    const disabledCount = __pvRows.length - activeCount;
    const body = __pvRows.length
      ? __pvRows.map(pvRowHtml).join('')
      : '<tr><td colspan="7" class="text-center text-muted">暂无规格（单规格商品）</td></tr>';

    const modal = document.getElementById('modal');
    modal.innerHTML = `<div class="modal modal-lg" style="width:1080px;max-width:96vw">
      <h3>🎨 商品规格（颜色 / 尺码）— ${escapeHtml(product.productCode || '')} ${escapeHtml(product.productName || '')}</h3>
      <div class="toolbar" style="margin:8px 0">
        <div class="toolbar-left">
          <span class="text-muted">共 ${__pvRows.length} 条规格（启用 ${activeCount} / 停用 ${disabledCount}）。
          规格仅为主数据细分：不改动历史询价 / 报价 / 订单 / 库存与库存流水，也不拆分已有库存；商品身份与商品字段保持不变。</span>
        </div>
      </div>
      <div class="form-grid" style="margin-bottom:10px">
        <div class="form-item"><label>规格编码 *</label>
          <input type="text" id="pv-code" maxlength="50" placeholder="如 RED-XL（同商品内唯一，忽略大小写与首尾空白）"></div>
        <div class="form-item"><label>颜色</label>
          <input type="text" id="pv-color" maxlength="50" placeholder="如 红色"></div>
        <div class="form-item"><label>尺码</label>
          <input type="text" id="pv-size" maxlength="50" placeholder="如 XL"></div>
        <div class="form-item"><label>备注</label>
          <input type="text" id="pv-remark" maxlength="500" placeholder="可选"></div>
      </div>
      <div class="toolbar" style="margin:0 0 8px">
        <div class="toolbar-left"><span class="text-muted" id="pv-form-hint">颜色与尺码至少填写一个；新规格默认为启用。</span></div>
        <div class="toolbar-actions">
          <button class="btn btn-primary" onclick="pvSubmit()">保存规格</button>
          <button class="btn btn-neutral" onclick="pvResetForm()">取消编辑</button>
        </div>
      </div>
      <div class="table-wrap" style="max-height:40vh;overflow:auto">
        <table><thead><tr>
          <th>规格编码</th><th>颜色</th><th>尺码</th><th>状态</th><th>备注</th><th>更新时间</th><th style="width:210px">操作</th>
        </tr></thead><tbody>${body}</tbody></table>
      </div>
      <div class="modal-footer">
        <button class="btn btn-neutral" onclick="pvClose()">关闭</button>
      </div>
    </div>`;
    modal.style.display = 'flex';

    if (__pvEditingId) pvFillForm(__pvEditingId);              // 编辑态：重新渲染后回填表单
  } catch (err) { toast(err.message, 'error'); }
}

/* 规格行：停用规格照常显示并显式标注（历史可读但不可再被新选中） */
function pvRowHtml(v) {
  const disabled = Number(v.status) !== 1;
  const status = disabled
    ? `<span class="status status-warning">停用</span> <span class="text-muted">${PRODUCT_VARIANT_UNAVAILABLE_MARK}</span>`
    : '<span class="status status-success">启用</span>';
  const toggle = disabled
    ? `<button class="btn btn-neutral btn-sm" onclick="pvSetStatus(${v.id}, true)">启用</button>`
    : `<button class="btn btn-neutral btn-sm" onclick="pvSetStatus(${v.id}, false)">停用</button>`;
  return `<tr>
    <td>${escapeHtml(v.variantCode || '')}</td>
    <td>${escapeHtml(v.color || '') || '<span class="text-muted">—</span>'}</td>
    <td>${escapeHtml(v.size || '') || '<span class="text-muted">—</span>'}</td>
    <td>${status}</td>
    <td>${escapeHtml(v.remark || '')}</td>
    <td>${fmtDate(v.updatedAt || v.createdAt)}</td>
    <td>
      <div class="row-actions">
        <button class="btn btn-neutral btn-sm" onclick="pvEdit(${v.id})">编辑</button>
        ${toggle}
        <button class="btn btn-neutral btn-sm" onclick="pvDelete(${v.id})">删除</button>
      </div>
    </td>
  </tr>`;
}

/* 保存（新增或更新）：服务端负责规范化与唯一性校验，界面只提交原始输入 */
async function pvSubmit() {
  const productId = __pvProductId;
  const el = k => document.getElementById('pv-' + k);
  const body = {
    variantCode: el('code').value,
    color: el('color').value,
    size: el('size').value,
    remark: el('remark').value,
  };
  /* 前端先做一次轻量校验，避免明显缺项时白跑一次请求（权威判定仍以服务端为准） */
  if (!String(body.variantCode || '').trim()) { toast('规格编码不能为空', 'error'); return; }
  if (!String(body.color || '').trim() && !String(body.size || '').trim()) {
    toast('颜色与尺码至少填写一个', 'error'); return;
  }

  try {
    if (__pvEditingId) {
      await api(`/api/base/products/${productId}/variants/${__pvEditingId}`, 'PUT', body);
      toast('规格已更新');
      __pvEditingId = null;
    } else {
      await api(`/api/base/products/${productId}/variants`, 'POST', body);
      toast('规格已新增');
    }
    await pvRender();
    if (CURRENT_LOADER) CURRENT_LOADER();      // 列表「规格数」同步刷新
  } catch (err) { toast(err.message, 'error'); }
}

/* 进入编辑态：把该规格的值回填到表单 */
function pvEdit(variantId) {
  __pvEditingId = Number(variantId);
  pvFillForm(__pvEditingId);
}

function pvFillForm(variantId) {
  const row = __pvRows.find(v => Number(v.id) === Number(variantId));
  if (!row) return;
  document.getElementById('pv-code').value = row.variantCode || '';
  document.getElementById('pv-color').value = row.color || '';
  document.getElementById('pv-size').value = row.size || '';
  document.getElementById('pv-remark').value = row.remark || '';
  const hint = document.getElementById('pv-form-hint');
  if (hint) hint.textContent = `正在编辑规格「${row.variantCode}」，保存后立即生效；停用状态需在列表中用「停用 / 启用」调整。`;
}

/* 退出编辑态并清空表单（不修改任何数据） */
function pvResetForm() {
  __pvEditingId = null;
  ['code', 'color', 'size', 'remark'].forEach(k => {
    const el = document.getElementById('pv-' + k);
    if (el) el.value = '';
  });
  const hint = document.getElementById('pv-form-hint');
  if (hint) hint.textContent = '颜色与尺码至少填写一个；新规格默认为启用。';
}

/* 停用 / 启用：启用需仍满足「启用中颜色 + 尺码组合唯一」，否则服务端拒绝 */
async function pvSetStatus(variantId, enable) {
  const productId = __pvProductId;
  try {
    await api(`/api/base/products/${productId}/variants/${variantId}/${enable ? 'enable' : 'disable'}`, 'POST');
    toast(enable ? '规格已启用' : '规格已停用（历史仍可读，不可再被新选中）');
    await pvRender();
    if (CURRENT_LOADER) CURRENT_LOADER();
  } catch (err) { toast(err.message, 'error'); }
}

/* 删除：本表软删除（历史行保留在库中，不改动任何单据与库存） */
async function pvDelete(variantId) {
  const productId = __pvProductId;
  const row = __pvRows.find(v => Number(v.id) === Number(variantId));
  const name = row ? (row.variantCode || '') : '';
  if (!confirm(`删除规格「${name}」？仅从商品资料中移除该规格，不改动历史询价 / 报价 / 订单 / 库存与库存流水。`)) return;
  try {
    await api(`/api/base/products/${productId}/variants/${variantId}`, 'DELETE');
    toast('规格已删除');
    if (Number(__pvEditingId) === Number(variantId)) pvResetForm();
    await pvRender();
    if (CURRENT_LOADER) CURRENT_LOADER();
  } catch (err) { toast(err.message, 'error'); }
}

/* 关闭弹窗（不修改数据） */
function pvClose() {
  __pvEditingId = null;
  closeModal();
}
