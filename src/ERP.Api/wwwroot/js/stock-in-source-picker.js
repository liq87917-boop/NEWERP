/* ==================================================================================
   ====== 采购入库 → 已审核采购订单 来源显式选择器（ERP-375） ======
   ==================================================================================
   定位：把「来源采购订单」从「手工填 Id」升级为**显式有界选择**：服务端只读候选接口给出
        当前账号客户范围内「已审核、未删除」采购订单的逐商品剩余可收数量（ERP-342 口径），
        操作员显式选定一张来源单据后，表单自动回填权威来源 Id / 单号 / 供应商与可收货商品行。
        同时覆盖两个入库业务界面：通用模块表单（crud.js，camelCase 字段 + DETAIL_ROWS）
        与采购入库 SP 单据编辑页（bill-edit.js，PascalCase 字段 + #detail-tbody）。
   规则（与 StockInOrderFulfillmentRules / ERP-342 同口径，服务端为唯一权威）：
     1. 只接受**显式选择**的来源采购订单 Id：候选全部来自服务端有界只读查询，
        **绝不按订单号 / 商品 / 相似度猜测来源**，也绝不臆造明细；
     2. 候选只含当前账号客户数据范围之内「未审核 / 已删除 / 已取消」都不返回；
        重复 / 歧义订单明细、单位未知、已收货满额的来源标记为**不可用**且不可选择；
     3. 回填明细只使用服务端投影的**权威商品 / 规格 / 基础单位**，数量默认剩余可收；
        **绝不臆造成本 / 单价**（入库成本由服务端按 ERP-033 口径兜底）；
     4. 操作员必须录入**正数部分数量**且不得超过该商品剩余可收数量，否则不写入表单；
     5. **保留用户所选仓库**：选择器绝不改写仓库字段（仓库由操作员在有界选项中选择）；
     6. 保存 / 重开：来源 Id 由既有创建 / 修改接口持久化并原样回显；
        未选来源（null / 表单留空）的历史入库保持「无来源」，绝不回填；
     7. 非「待提交」单据只读（服务端同样 fail closed），失败时保留表单输入并显式提示。
   边界：本选择器只做只读候选展示与表单回填：不锁库、不预留库存、不写流水 / 财务、
         不改写采购订单 / 商品 / 供应商主数据，也不新增表 / 菜单 / 用户授权。
   ================================================================================== */

/* 只有「待提交」可维护来源（与后端 StockInController 的状态判定一致） */
const SIS_EDITABLE_STATUS = 'Pending';
const SIS_STATUS_TEXT = {
  Pending: '待提交（草稿）', Submitted: '已提交', Approved: '已审核',
  Rejected: '已驳回', Completed: '已完成', Cancelled: '已取消',
};
const SIS_TAKE_DEFAULT = 50;
const SIS_TAKE_MAX = 200;

const SIS_READONLY_TEXT = '当前单据不是「待提交（草稿）」：来源只读，如需更换请先销审 / 另建待提交单据。';

/* 选择器会写入的表单字段（**不含 warehouseId**：仓库由用户选择并保留，绝不改写） */
const SIS_WRITTEN_FIELDS = ['purchaseOrderId', 'supplierId'];

/* 一次对话 = 一个入库表单；candidates 为服务端权威候选，detail 为选定来源的权威可收行 */
let SIS = {
  stockInId: 0, status: '', keyword: '', supplierId: '', take: SIS_TAKE_DEFAULT,
  candidates: [], detail: null, quantities: {}, loading: false, submitting: false,
  error: '', result: '', readonly: false, open: false,
};

/* 状态归一化：既接受后端枚举名（Pending），也接受数字与已有中文文案 */
function sisNormalizeStatus(status) {
  if (status === null || status === undefined || status === '') return '';
  const raw = String(status).trim();
  if (SIS_STATUS_TEXT[raw]) return raw;
  const byNumber = { '0': 'Pending', '1': 'Submitted', '2': 'Approved', '3': 'Rejected', '4': 'Completed', '5': 'Cancelled' };
  if (byNumber[raw]) return byNumber[raw];
  const hit = Object.keys(SIS_STATUS_TEXT).find(k => SIS_STATUS_TEXT[k] === raw);
  return hit || raw;
}

function sisStatusText(status) {
  const key = sisNormalizeStatus(status);
  return SIS_STATUS_TEXT[key] || (key || '未知');
}

function sisIsEditableStatus(status) {
  return sisNormalizeStatus(status) === SIS_EDITABLE_STATUS;
}

function sisReadonlyReason(status) {
  return sisIsEditableStatus(status) ? '' : SIS_READONLY_TEXT;
}

/* 未保存单据（0 = 新建）允许选择来源；非法值（负数 / 非数字）一律拒绝 */
function sisGuardSavedId(stockInId) {
  if (stockInId === null || stockInId === undefined || stockInId === '') return '';
  const n = Number(stockInId);
  if (!Number.isInteger(n) || n < 0) return '单据 Id 非法：拒绝选择来源（绝不臆造单据）';
  return '';
}

/* 条数钳制：1..SIS_TAKE_MAX；<= 0 取默认值（与服务端 ClampTake 同口径） */
function sisNormalizeTake(take) {
  const n = Number(take);
  if (!Number.isFinite(n) || n <= 0) return SIS_TAKE_DEFAULT;
  return Math.min(Math.floor(n), SIS_TAKE_MAX);
}

/* 可选择的候选：仅服务端标记 available = true（零容量 / 重复歧义 / 损坏证据绝不可选） */
function sisAvailableCandidates(candidates) {
  return (candidates || []).filter(c => c && c.available === true);
}

/* 可选择的来源单据（按来源订单 Id 去重）：只保留至少有一条可收行的来源 */
function sisSourceOptions(candidates) {
  const bySource = new Map();
  sisAvailableCandidates(candidates).forEach(c => {
    const id = String(c.purchaseOrderId);
    const option = bySource.get(id) || {
      purchaseOrderId: c.purchaseOrderId, orderNo: c.orderNo,
      supplierName: c.supplierName, supplierId: c.supplierId, currency: c.currency,
      lineCount: 0, remainingTotal: 0,
    };
    option.lineCount += 1;
    option.remainingTotal += Number(c.remainingBaseQuantity) || 0;
    bySource.set(id, option);
  });
  return Array.from(bySource.values());
}

/* 候选展示文案：订单号 / 供应商 / 商品 / 规格 / 基础单位 / 授权数量 / 已收货 / 剩余可收 */
function sisCandidateLabel(candidate) {
  if (!candidate) return '';
  const parts = [
    candidate.orderNo ? `来源订单 ${candidate.orderNo}` : '来源订单 未知',
    candidate.supplierName ? `供应商 ${candidate.supplierName}` : '',
    candidate.productName || `商品 Id ${candidate.productId ?? ''}`,
    candidate.spec ? `规格 ${candidate.spec}` : '规格 未知',
    `基础单位 ${candidate.baseUnit || '未知'}`,
    `授权数量 ${candidate.authorizedBaseQuantity ?? ''}`,
    `已收货 ${candidate.receivedBaseQuantity ?? 0}`,
    `剩余可收 ${candidate.remainingBaseQuantity ?? ''}`,
  ];
  return parts.filter(Boolean).join(' / ');
}

/* 显式选择回填表头：权威来源 Id / 单号 / 供应商（绝不臆造，绝不按文本推断）；**不含仓库**（保留用户所选） */
function sisHeaderFill(detail) {
  if (!detail) {
    return { purchaseOrderId: 0, orderNo: '', supplierId: '', supplierName: '', currency: '', exchangeRate: '' };
  }
  return {
    purchaseOrderId: detail.purchaseOrderId,
    orderNo: detail.orderNo || '',
    supplierId: detail.supplierId,
    supplierName: detail.supplierName || '',
    currency: detail.currency,
    exchangeRate: detail.exchangeRate,
  };
}

/* 数量校验：必须为正数且不超过该商品剩余可收数量（正数部分数量语义） */
function sisValidateQuantity(value, remaining) {
  const raw = value === null || value === undefined ? '' : String(value).trim();
  const max = Number(remaining);
  if (raw === '') return { ok: false, value: 0, reason: '请录入大于 0 的收货数量' };
  if (!/^\d+(\.\d+)?$/.test(raw)) return { ok: false, value: 0, reason: `数量 [${raw}] 不是合法正数` };
  const n = Number(raw);
  if (!(n > 0)) return { ok: false, value: 0, reason: '收货数量必须大于 0' };
  if (Number.isFinite(max) && n > max + 1e-9) {
    return { ok: false, value: 0, reason: `数量 ${n} 超过该商品剩余可收数量 ${max}` };
  }
  return { ok: true, value: n, reason: '' };
}

/* 选定来源后的可录入明细行：只使用服务端权威商品 / 规格 / 基础单位；
   本页不录入单价 / 成本（**绝不臆造成本**，成本由服务端按 ERP-033 口径兜底）。 */
function sisBuildDetailRows(detail) {
  return sisAvailableCandidates(detail && detail.lines).map(line => ({
    productId: line.productId,
    productName: line.productName || '',
    spec: line.spec || '',
    unit: line.baseUnit || '',
    quantity: Number(line.remainingBaseQuantity) || 0,
    weight: 0,
    volume: 0,
    batchNo: '',
    remark: `来源采购订单 ${line.orderNo || ''}`.trim(),
    purchaseOrderId: line.purchaseOrderId,
    remainingBaseQuantity: Number(line.remainingBaseQuantity) || 0,
  }));
}

/* SP 单据编辑页（bill-edit.js）明细行口径：与既有 addDetailRow / collectDetails 同一字段名（PascalCase）；
   单价 / 金额一律留 0（**绝不臆造成本**），操作员核对后保存。 */
function sisBillDetailRows(rows) {
  return (rows || []).map(r => ({
    ProductId: r.productId,
    ProductName: r.productName || '',
    Spec: r.spec || '',
    Quantity: r.quantity,
    Unit: r.unit || '',
    UnitPrice: 0,
    Amount: 0,
  }));
}

/* 录入数量写回：任一行不合法即整体拒绝（保留原行，不写入表单） */
function sisApplyQuantities(rows, quantities) {
  const result = [];
  for (const row of rows || []) {
    const verdict = sisValidateQuantity((quantities || {})[String(row.productId)], row.remainingBaseQuantity);
    if (!verdict.ok) {
      return { ok: false, reason: `${row.productName || row.productId}：${verdict.reason}`, rows };
    }
    result.push(Object.assign({}, row, { quantity: verdict.value }));
  }
  return { ok: true, reason: '', rows: result };
}

function sisCanApply(state) {
  const s = state || {};
  if (s.readonly || s.loading || s.submitting) return false;
  if (!s.detail || !s.detail.purchaseOrderId) return false;
  const verdict = sisApplyQuantities(sisBuildDetailRows(s.detail), s.quantities);
  return verdict.ok && verdict.rows.length > 0;
}

/* 安全转义（浏览器复用全局 escapeHtml；Node 单测走本地实现，绝不注入） */
function sisEsc(v) {
  const s = v === null || v === undefined ? '' : String(v);
  if (typeof escapeHtml === 'function') return escapeHtml(s);
  return s.replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

/* 可选来源单据行（按来源订单 Id 去重：只保留至少有一条可收行的已审核来源） */
function sisSourceRowHtml(source) {
  if (!source) return '';
  return `<tr data-source-id="${sisEsc(source.purchaseOrderId)}">
    <td>${sisEsc(source.purchaseOrderId)}</td>
    <td>${sisEsc(source.orderNo || '')}</td>
    <td>${sisEsc(source.supplierName || '')}</td>
    <td>${sisEsc(source.currency || '')}</td>
    <td>${sisEsc(source.lineCount)}</td>
    <td>${sisEsc(source.remainingTotal)}</td>
    <td><button type="button" class="btn btn-neutral btn-sm" onclick="sisPickSource(${sisEsc(source.purchaseOrderId)})">选择此来源</button></td>
  </tr>`;
}

/* 候选证据行：订单号 / 商品 / 规格 / 基础单位 / 授权数量 / 已收货 / 剩余可收 / 可用性原因 */
function sisCandidateRowHtml(candidate) {
  if (!candidate) return '';
  const available = candidate.available === true;
  return `<tr${available ? '' : ' class="text-muted"'}>
    <td>${sisEsc(candidate.orderNo || '')}</td>
    <td>${sisEsc(candidate.productName || '')}</td>
    <td>${sisEsc(candidate.spec || '')}</td>
    <td>${sisEsc(candidate.baseUnit || '')}</td>
    <td>${sisEsc(candidate.authorizedBaseQuantity ?? '')}</td>
    <td>${sisEsc(candidate.receivedBaseQuantity ?? '')}</td>
    <td>${sisEsc(candidate.remainingBaseQuantity ?? '')}</td>
    <td>${available ? '可用' : sisEsc(candidate.unavailableReason || '不可用')}</td>
  </tr>`;
}

const SIS_ERROR_PREFIX = { network: '无法连接服务器', unauthorized: '登录已过期或权限不足', server: '服务端拒绝' };

function sisErrorHtml(kind, message) {
  const prefix = SIS_ERROR_PREFIX[kind] || '操作失败';
  return `<div class="sis-error" role="alert" style="padding:8px 10px;background:#fef2f2;border:1px solid #fecaca;border-radius:8px;color:#b91c1c;font-size:13px;margin-bottom:8px">` +
    `${sisEsc(prefix)}：${sisEsc(message || '')}</div>`;
}

/* ==================== 浏览器接线（Node 单测只使用上面的纯函数） ==================== */

function sisErrorMessage(err) {
  const message = err && err.message ? err.message : String(err || '');
  if (/Failed to fetch|NetworkError|network|无法连接/i.test(message)) return { kind: 'network', message };
  if (/权限|授权|未认证|登录|禁止/.test(message)) return { kind: 'unauthorized', message };
  return { kind: 'server', message };
}

/* 回填字段：同时覆盖通用模块（camelCase → f_purchaseOrderId）与 SP 单据编辑页
   （bill-config 的 PascalCase → f_PurchaseOrderId）两种命名，绝不改写仓库字段。 */
function sisSetField(key, value) {
  const v = value === null || value === undefined ? '' : value;
  const pascal = key.charAt(0).toUpperCase() + key.slice(1);
  ['f_' + key, 'f_' + pascal].forEach(id => {
    const el = document.getElementById(id);
    if (el) el.value = v;
  });
}

function sisCurrentSourceId() {
  const el = document.getElementById('f_purchaseOrderId') || document.getElementById('f_PurchaseOrderId');
  if (!el) return 0;
  const raw = String(el.value || '').trim();
  if (raw === '') return 0;
  return /^\d+$/.test(raw) ? Number(raw) : 0;
}

/* 把选定来源的可收行写入当前表单明细：
   优先 SP 单据编辑页（#detail-tbody + 既有 addDetailRow），否则通用模块明细（#detail-body + DETAIL_ROWS）；
   两者都不存在时返回 false（绝不臆造表单结构，避免只回填表头的半成品）。 */
function sisApplyRowsToForm(rows) {
  const billTbody = document.getElementById('detail-tbody');
  if (billTbody && typeof addDetailRow === 'function') {
    billTbody.innerHTML = '';
    sisBillDetailRows(rows).forEach(r => addDetailRow(r));
    if (typeof updateDetailSummary === 'function') updateDetailSummary();
    return true;
  }
  const moduleBody = document.getElementById('detail-body');
  if (moduleBody && typeof DETAIL_ROWS !== 'undefined' && typeof detailRender === 'function') {
    DETAIL_ROWS = rows;
    detailRender();
    return true;
  }
  return false;
}

async function sisLoadStatus(stockInId) {
  SIS.status = '';
  if (!stockInId) return;
  try {
    const row = await api(`/api/stock-ins/${stockInId}`);
    SIS.status = sisNormalizeStatus(row && row.status);
  } catch (err) {
    SIS.error = sisErrorMessage(err).message;
  }
  SIS.readonly = !sisIsEditableStatus(SIS.status);
}

/* 有界只读候选：关键字 / 供应商 / 条数由服务端钳制，绝不无界拉取 */
async function sisLoadCandidates() {
  SIS.loading = true;
  SIS.error = '';
  sisRender();
  const params = ['take=' + encodeURIComponent(sisNormalizeTake(SIS.take))];
  if (SIS.keyword) params.push('keyword=' + encodeURIComponent(SIS.keyword));
  if (Number(SIS.supplierId) > 0) params.push('supplierId=' + encodeURIComponent(Number(SIS.supplierId)));
  try {
    const rows = await api(`/api/stock-ins/source-candidates?${params.join('&')}`);
    SIS.candidates = rows || [];
  } catch (err) {
    SIS.candidates = [];
    SIS.error = sisErrorMessage(err).message;
  }
  SIS.loading = false;
  sisRender();
}

/* 只读加载权威来源详情（重开回显用；不校验可维护状态） */
async function sisLoadDetail(purchaseOrderId) {
  SIS.loading = true;
  SIS.error = '';
  sisRender();
  try {
    SIS.detail = await api(`/api/stock-ins/source-candidates/${purchaseOrderId}`);
    SIS.quantities = {};
    sisAvailableCandidates(SIS.detail && SIS.detail.lines).forEach(l => {
      SIS.quantities[String(l.productId)] = String(l.remainingBaseQuantity);
    });
    if (SIS.detail && !SIS.detail.available) {
      SIS.error = SIS.detail.unavailableReason || '该来源当前不可用';
    }
  } catch (err) {
    SIS.detail = null;
    SIS.error = sisErrorMessage(err).message;
  }
  SIS.loading = false;
  sisRender();
}

/* 显式选择来源（仅可维护状态）：绝不从文本推断来源 */
async function sisPickSource(purchaseOrderId) {
  if (SIS.readonly) { toast(sisReadonlyReason(SIS.status), 'error'); return; }
  await sisLoadDetail(purchaseOrderId);
}

function sisSetQuantity(productId, value) {
  SIS.quantities[String(productId)] = value;
  sisRender();
}

function sisSearch() {
  const kw = document.getElementById('sis-keyword');
  const take = document.getElementById('sis-take');
  SIS.keyword = kw ? kw.value.trim() : '';
  SIS.take = take ? take.value : SIS_TAKE_DEFAULT;
  return sisLoadCandidates();
}

/* 显式选择 → 回填表头与明细行（不臆造成本；**保留用户所选仓库**；失败保留表单输入） */
function sisApplyToForm() {
  if (SIS.readonly) { toast(sisReadonlyReason(SIS.status), 'error'); return; }
  const verdict = sisApplyQuantities(sisBuildDetailRows(SIS.detail), SIS.quantities);
  if (!verdict.ok) { SIS.error = verdict.reason; sisRender(); return; }
  if (!verdict.rows.length) { SIS.error = '该来源没有可收商品行'; sisRender(); return; }

  const header = sisHeaderFill(SIS.detail);
  sisSetField('purchaseOrderId', header.purchaseOrderId);
  sisSetField('supplierId', header.supplierId);
  /* 保留用户所选仓库：选择器绝不改写 f_warehouseId / f_WarehouseId（SIS_WRITTEN_FIELDS 亦不含仓库）。 */
  if (!sisApplyRowsToForm(verdict.rows)) {
    SIS.error = '当前页面没有可写入的入库明细区域：拒绝只回填来源（避免半成品写入）';
    sisRender();
    return;
  }
  SIS.result = `已按来源订单 ${header.orderNo} 回填 ${verdict.rows.length} 行可收明细（数量请复核后保存）`;
  sisCloseModal();
  toast(SIS.result);
}

function sisDetailHtml(detail) {
  if (!detail) return '';
  const lines = (detail.lines || []).map(line => {
    const usable = line.available === true;
    const value = sisEsc(SIS.quantities[String(line.productId)] ?? '');
    return `<tr>
      <td>${sisEsc(line.productId)}</td>
      <td>${sisEsc(line.productName || '')}</td>
      <td>${sisEsc(line.spec || '')}</td>
      <td>${sisEsc(line.baseUnit || '')}</td>
      <td>${sisEsc(line.remainingBaseQuantity ?? '')}</td>
      <td>${usable
        ? `<input type="number" step="0.0001" min="0" style="width:120px" value="${value}" ` +
          `onchange="sisSetQuantity(${sisEsc(line.productId)}, this.value)">`
        : sisEsc(line.unavailableReason || '不可用')}</td>
    </tr>`;
  }).join('');
  return `<div class="sis-detail" style="margin-top:10px">
    <div style="font-size:13px;margin-bottom:4px">
      已选来源：<b>${sisEsc(detail.orderNo || '')}</b>
      （供应商 ${sisEsc(detail.supplierName || '')} / 币种 ${sisEsc(detail.currency || '')}）
      ${detail.available ? '' : `／<span style="color:#b91c1c">${sisEsc(detail.unavailableReason || '不可用')}</span>`}
    </div>
    <div class="table-wrap" style="max-height:240px;overflow:auto">
      <table class="data-table"><thead><tr>
        <th style="width:80px">商品ID</th><th>商品名称</th><th style="width:90px">规格</th>
        <th style="width:60px">基础单位</th><th style="width:90px">剩余可收</th><th style="width:140px">本次收货数量</th>
      </tr></thead><tbody>${lines || '<tr><td colspan="6" style="text-align:center;color:#999">没有可收商品行</td></tr>'}</tbody></table>
    </div>
    <div style="margin-top:8px;font-size:12px;color:#666">
      本页不录入单价 / 成本：本选择器绝不臆造成本；入库成本由服务端按 ERP-033 口径兜底。仓库保留您当前的选择。
    </div>
  </div>`;
}

function sisRender() {
  if (typeof document === 'undefined' || !SIS.open) return;
  const modal = document.getElementById('modal');
  if (!modal) return;

  const options = sisSourceOptions(SIS.candidates);
  const sourceRows = options.map(sisSourceRowHtml).join('') ||
    `<tr><td colspan="7" style="text-align:center;color:#999">没有可选的已审核来源（重复 / 歧义明细、单位未知、已收货满额不可选）</td></tr>`;
  const candidateRows = (SIS.candidates || []).map(sisCandidateRowHtml).join('') ||
    `<tr><td colspan="8" style="text-align:center;color:#999">候选为空</td></tr>`;

  modal.innerHTML = `
    <div class="modal modal-lg">
      <h3>选择采购入库来源采购订单</h3>
      <div style="font-size:12px;color:#666;margin-bottom:6px">
        仅「已审核、未删除」且仍有剩余可收数量的来源可选；候选与剩余可收数量全部来自服务端有界只读查询，绝不按订单号猜测来源。
        ${SIS.readonly ? `<b style="color:#b91c1c">${sisEsc(sisReadonlyReason(SIS.status))}</b>` : ''}
      </div>
      ${SIS.error ? sisErrorHtml('server', SIS.error) : ''}
      <div class="toolbar" style="margin-bottom:8px;display:flex;gap:6px;flex-wrap:wrap">
        <input type="text" id="sis-keyword" placeholder="订单号 / 供应商 / 商品名称 / 规格" value="${sisEsc(SIS.keyword)}"
               onkeydown="if(event.key==='Enter')sisSearch()">
        <input type="number" id="sis-take" style="width:90px" value="${sisEsc(SIS.take)}"
               title="返回条数（服务端钳制 1~${SIS_TAKE_MAX}）">
        <button type="button" class="btn btn-neutral" onclick="sisSearch()">查询</button>
      </div>
      <div class="table-wrap" style="max-height:220px;overflow:auto">
        <table class="data-table"><thead><tr>
          <th style="width:70px">来源ID</th><th>订单号</th><th>供应商</th><th style="width:70px">币种</th>
          <th style="width:70px">可收行</th><th style="width:90px">剩余合计</th><th style="width:110px">操作</th>
        </tr></thead><tbody>${sourceRows}</tbody></table>
      </div>
      <details style="margin-top:8px">
        <summary style="cursor:pointer;font-size:13px">逐商品可收证据（含不可用行与原因）</summary>
        <div class="table-wrap" style="max-height:200px;overflow:auto;margin-top:6px">
          <table class="data-table"><thead><tr>
            <th>订单号</th><th>商品</th><th>规格</th><th>基础单位</th>
            <th>授权数量</th><th>已收货</th><th>剩余可收</th><th>可用性</th>
          </tr></thead><tbody>${candidateRows}</tbody></table>
        </div>
      </details>
      ${sisDetailHtml(SIS.detail)}
      <div class="modal-footer">
        <button type="button" class="btn btn-neutral" onclick="sisCloseModal()">取消</button>
        <button type="button" class="btn btn-primary" ${sisCanApply(SIS) ? '' : 'disabled '}
                onclick="sisApplyToForm()">应用到表单</button>
      </div>
    </div>`;
  modal.style.display = 'flex';
}

function sisCloseModal() {
  SIS.open = false;
  SIS.error = '';
  if (typeof closeModal === 'function') closeModal();
}

/* 打开选择器：新建（0）与已保存单据都可用；重开时显式回显服务端持久化的来源 */
async function openStockInSourcePicker(stockInId) {
  if (typeof document === 'undefined') return;
  const guard = sisGuardSavedId(stockInId);
  if (guard) { toast(guard, 'error'); return; }

  const supplierEl = document.getElementById('f_supplierId') || document.getElementById('f_SupplierId');
  SIS.open = true;
  SIS.candidates = [];
  SIS.detail = null;
  SIS.quantities = {};
  SIS.error = '';
  SIS.result = '';
  SIS.keyword = '';
  SIS.take = SIS_TAKE_DEFAULT;
  SIS.supplierId = supplierEl ? supplierEl.value : '';
  sisRender();

  await sisLoadStatus(Number(stockInId) || 0);
  await sisLoadCandidates();
  const persisted = sisCurrentSourceId();
  if (persisted > 0) await sisLoadDetail(persisted);
  else sisRender();
}

/* 表单接入：为 modules-doc2 中声明 selector: 'stock-in-source' 的字段追加「选择来源」入口，
   也可由 SP 单据编辑页（bill-edit.js）显式传入当前单据 Id。只包裹既有全局函数，不新增模块 / 菜单。 */
function sisSourceButtonHtml(explicitId) {
  const target = (explicitId === null || explicitId === undefined || explicitId === '')
    ? 'window.__sisCurrentId || 0' : String(Number(explicitId) || 0);
  return `<div class="form-item full"><label>来源采购订单选择</label>` +
    `<button type="button" class="btn btn-neutral" onclick="openStockInSourcePicker(${target})">` +
    `选择来源采购订单（按剩余可收数量过滤）</button>` +
    `<span class="text-muted" style="margin-left:8px">选定后自动回填权威供应商与可收货商品行；仓库保留您的选择；也可手工填写来源 Id</span>` +
    `</div>`;
}

function sisInstallFormHook() {
  if (typeof window === 'undefined') return false;
  if (window.__sisHooked) return true;
  if (typeof window.fieldHtml !== 'function' || typeof window.openForm !== 'function') return false;

  const originalOpenForm = window.openForm;
  if (!originalOpenForm.__sisWrapped) {
    const wrappedOpenForm = function (id) {
      window.__sisCurrentId = (id === undefined || id === null || id === '') ? 0 : (Number(id) || 0);
      return originalOpenForm.apply(this, arguments);
    };
    wrappedOpenForm.__sisWrapped = true;
    window.openForm = wrappedOpenForm;
  }

  const originalFieldHtml = window.fieldHtml;
  const wrappedFieldHtml = function (field) {
    const html = originalFieldHtml.apply(this, arguments);
    if (!field || field.selector !== 'stock-in-source') return html;
    return html + sisSourceButtonHtml();
  };
  wrappedFieldHtml.__sisWrapped = true;
  window.fieldHtml = wrappedFieldHtml;
  window.__sisHooked = true;
  return true;
}

if (typeof window !== 'undefined' && typeof window.fieldHtml === 'function') sisInstallFormHook();

/* Node 单测导出（浏览器中 module 为 undefined，自动跳过） */
if (typeof module !== 'undefined' && module.exports) {
  module.exports = {
    SIS_EDITABLE_STATUS, SIS_TAKE_DEFAULT, SIS_TAKE_MAX, SIS_READONLY_TEXT, SIS_WRITTEN_FIELDS,
    sisNormalizeStatus,
    sisStatusText,
    sisIsEditableStatus,
    sisReadonlyReason,
    sisGuardSavedId,
    sisNormalizeTake,
    sisAvailableCandidates,
    sisSourceOptions,
    sisCandidateLabel,
    sisHeaderFill,
    sisValidateQuantity,
    sisBuildDetailRows,
    sisBillDetailRows,
    sisApplyQuantities,
    sisCanApply,
    sisEsc,
    sisSourceRowHtml,
    sisCandidateRowHtml,
    sisErrorHtml,
    sisErrorMessage,
    sisSourceButtonHtml,
    sisInstallFormHook,
  };
}
