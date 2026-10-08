/* ==================================================================================
   ====== 采购退货 → 已审核采购入库单 来源显式选择器（ERP-377） ======
   ==================================================================================
   定位：把「来源采购入库单」从「手工填 Id / 单号」升级为**显式有界选择**：
        服务端只读候选接口给出当前账号采购入库归属范围内「已审核、未删除」来源的逐商品净可退容量，
        操作员显式选定一张来源单据后，表单自动回填权威来源 Id / 单号 / 供应商 / 仓库与可退商品行。
   规则（与 PurchaseReturnSourceRules 同口径，服务端为唯一权威）：
     1. 只接受**显式选择**的来源入库单 Id：候选全部来自服务端有界只读查询，
        **绝不按来源单号 / 商品 / 相似度猜测来源**，也绝不臆造明细 Id；
     2. 候选只含当前账号采购入库归属范围内「未审核 / 已删除 / 已取消」都不返回；
        零容量、单位未知、负数量证据的来源标记为**不可用**且不可选择；
     3. 回填明细只使用服务端投影的**权威基础单位 / 规格 / 商品**，单价与成本单价一律留 0
        （**绝不臆造价格 / 成本**；出库成本由服务端按来源入库成本兜底）；
     4. 操作员必须录入**正数部分数量**且不得超过该商品净可退容量，否则不写入表单；
     5. 保存 / 重开：来源 Id 与单号由既有创建 / 修改接口持久化并原样回显；
        未选来源（null）的历史退货保持「无来源」，绝不回填；
     6. 非「待提交」单据只读（服务端同样 fail closed），失败时保留表单输入并显式提示。
   边界：本选择器只做只读候选展示与表单回填：不锁库、不预留库存、不写流水 / 财务、
        不改写商品 / 供应商 / 仓库 / 入库单主数据，也不新增表 / 菜单 / 用户授权。
   ================================================================================== */

/* 只有「待提交」可维护来源（与后端 PurchaseReturnController 的状态判定一致） */
const PRS_EDITABLE_STATUS = 'Pending';
const PRS_STATUS_TEXT = {
  Pending: '待提交（草稿）', Submitted: '已提交', Approved: '已审核',
  Rejected: '已驳回', Completed: '已完成', Cancelled: '已取消',
};
const PRS_TAKE_DEFAULT = 50;
const PRS_TAKE_MAX = 200;

const PRS_UNSAVED_HINT = '新建单据可直接选择来源；保存后重开仍按服务端持久化的来源 Id / 单号回显。';
const PRS_READONLY_TEXT = '当前单据不是「待提交（草稿）」：来源只读，如需更换请先销审 / 另建待提交单据。';

/* 选择器写入表单的字段（服务端权威回填；绝不多写） */
const PRS_WRITTEN_FIELDS = ['sourceStockInId', 'sourceStockInNo', 'supplierId', 'supplierName', 'warehouseId'];

/* 一次对话 = 一个退货表单；candidates 为服务端权威候选，detail 为选定来源的权威可退行 */
let PRS = {
  returnId: 0, status: '', keyword: '', supplierId: '', warehouseId: '', take: PRS_TAKE_DEFAULT,
  candidates: [], detail: null, quantities: {}, loading: false, submitting: false,
  error: '', result: '', readonly: false, open: false,
};

/* 状态归一化：既接受后端枚举名（Pending），也接受数字与已有中文文案 */
function prsNormalizeStatus(status) {
  if (status === null || status === undefined || status === '') return '';
  const raw = String(status).trim();
  if (PRS_STATUS_TEXT[raw]) return raw;
  const byNumber = { '0': 'Pending', '1': 'Submitted', '2': 'Approved', '3': 'Rejected', '4': 'Completed', '5': 'Cancelled' };
  if (byNumber[raw]) return byNumber[raw];
  const hit = Object.keys(PRS_STATUS_TEXT).find(k => PRS_STATUS_TEXT[k] === raw);
  return hit || raw;
}

function prsStatusText(status) {
  const key = prsNormalizeStatus(status);
  return PRS_STATUS_TEXT[key] || (key || '未知');
}

function prsIsEditableStatus(status) {
  return prsNormalizeStatus(status) === PRS_EDITABLE_STATUS;
}

function prsReadonlyReason(status) {
  return prsIsEditableStatus(status) ? '' : PRS_READONLY_TEXT;
}

/* 未保存单据（0 = 新建）允许选择来源；非法值（负数 / 非数字）一律拒绝 */
function prsGuardSavedId(returnId) {
  if (returnId === null || returnId === undefined || returnId === '') return '';
  const n = Number(returnId);
  if (!Number.isInteger(n) || n < 0) return '单据 Id 非法：拒绝选择来源（绝不臆造单据）';
  return '';
}

/* 可选择的候选：仅服务端标记 available = true（零容量 / 损坏证据绝不可选） */
function prsAvailableCandidates(candidates) {
  return (candidates || []).filter(c => c && c.available === true);
}

/* 可选择的来源单据（按来源 Id 去重）：只保留至少有一条可退行的来源 */
function prsSourceOptions(candidates) {
  const bySource = new Map();
  prsAvailableCandidates(candidates).forEach(c => {
    const id = String(c.sourceStockInId);
    const option = bySource.get(id) || {
      sourceStockInId: c.sourceStockInId, sourceStockInNo: c.sourceStockInNo,
      supplierName: c.supplierName, warehouseName: c.warehouseName,
      lineCount: 0, remainingTotal: 0,
    };
    option.lineCount += 1;
    option.remainingTotal += Number(c.remainingBaseQuantity) || 0;
    bySource.set(id, option);
  });
  return Array.from(bySource.values());
}

/* 候选展示文案：来源单号 / 供应商 / 商品 / 规格 / 基础单位 / 来源数量 / 已生效退货 / 剩余可退 */
function prsCandidateLabel(candidate) {
  if (!candidate) return '';
  const parts = [
    candidate.sourceStockInNo ? `来源 ${candidate.sourceStockInNo}` : '来源 未知',
    candidate.supplierName ? `供应商 ${candidate.supplierName}` : '',
    candidate.productName || `商品 Id ${candidate.productId ?? ''}`,
    candidate.spec ? `规格 ${candidate.spec}` : '规格 未知',
    `基础单位 ${candidate.baseUnit || '未知'}`,
    `来源数量 ${candidate.sourceBaseQuantity ?? ''}`,
    `已生效退货 ${candidate.effectiveReturnedBaseQuantity ?? 0}`,
    `剩余可退 ${candidate.remainingBaseQuantity ?? ''}`,
  ];
  return parts.filter(Boolean).join(' / ');
}

/* 显式选择回填表头：权威来源 Id / 单号 / 供应商 / 仓库（绝不准臆造，绝不按文本推断） */
function prsHeaderFill(detail) {
  if (!detail) {
    return {
      sourceStockInId: 0, sourceStockInNo: '', supplierId: '',
      supplierName: '', warehouseId: '',
    };
  }
  return {
    sourceStockInId: detail.sourceStockInId,
    sourceStockInNo: detail.sourceStockInNo || '',
    supplierId: detail.supplierId,
    supplierName: detail.supplierName || '',
    warehouseId: detail.warehouseId,
  };
}

/* 数量校验：必须为正数且不超过该商品净可退容量（正数部分数量语义） */
function prsValidateQuantity(value, remaining) {
  const raw = value === null || value === undefined ? '' : String(value).trim();
  const max = Number(remaining);
  if (raw === '') return { ok: false, value: 0, reason: '请录入大于 0 的退货数量' };
  if (!/^\d+(\.\d+)?$/.test(raw)) return { ok: false, value: 0, reason: `数量 [${raw}] 不是合法正数` };
  const n = Number(raw);
  if (!(n > 0)) return { ok: false, value: 0, reason: '退货数量必须大于 0' };
  if (Number.isFinite(max) && n > max + 1e-9) {
    return { ok: false, value: 0, reason: `数量 ${n} 超过该商品净可退容量 ${max}` };
  }
  return { ok: true, value: n, reason: '' };
}

/* 选定来源后的可录入明细行：只使用服务端权威商品 / 规格 / 基础单位；
   单价与成本单价一律留 0（**绝不臆造价格 / 成本**，出库成本由服务端按来源入库成本兜底）。 */
function prsBuildDetailRows(detail) {
  return prsAvailableCandidates(detail && detail.lines).map(line => ({
    productId: line.productId,
    productName: line.productName || '',
    spec: line.spec || '',
    unit: line.baseUnit || '',
    quantity: Number(line.remainingBaseQuantity) || 0,
    unitPrice: 0,
    amount: 0,
    unitCost: 0,
    remark: `来源入库单 ${line.sourceStockInNo || ''}`.trim(),
    sourceStockInId: line.sourceStockInId,
    remainingBaseQuantity: Number(line.remainingBaseQuantity) || 0,
  }));
}

/* 录入数量写回：任一行不合法即整体拒绝（保留原行，不写入表单） */
function prsApplyQuantities(rows, quantities) {
  const result = [];
  for (const row of rows || []) {
    const verdict = prsValidateQuantity((quantities || {})[String(row.productId)], row.remainingBaseQuantity);
    if (!verdict.ok) {
      return { ok: false, reason: `${row.productName || row.productId}：${verdict.reason}`, rows };
    }
    result.push(Object.assign({}, row, {
      quantity: verdict.value,
      amount: Math.round(verdict.value * (Number(row.unitPrice) || 0) * 10000) / 10000,
    }));
  }
  return { ok: true, reason: '', rows: result };
}

function prsCanApply(state) {
  const s = state || {};
  if (s.readonly || s.loading || s.submitting) return false;
  if (!s.detail || !s.detail.sourceStockInId) return false;
  const verdict = prsApplyQuantities(prsBuildDetailRows(s.detail), s.quantities);
  return verdict.ok && verdict.rows.length > 0;
}

/* 可选来源单据行（按来源 Id 去重：只保留至少有一条可退行的已审核来源） */
function prsSourceRowHtml(source) {
  if (!source) return '';
  return `<tr data-source-id="${prsEsc(source.sourceStockInId)}">
    <td>${prsEsc(source.sourceStockInId)}</td>
    <td>${prsEsc(source.sourceStockInNo || '')}</td>
    <td>${prsEsc(source.supplierName || '')}</td>
    <td>${prsEsc(source.warehouseName || '')}</td>
    <td>${prsEsc(source.lineCount)}</td>
    <td>${prsEsc(source.remainingTotal)}</td>
    <td><button type="button" class="btn btn-neutral btn-sm" onclick="prsPickSource(${prsEsc(source.sourceStockInId)})">选择此来源</button></td>
  </tr>`;
}

/* 候选证据行：来源单号 / 商品 / 规格 / 基础单位 / 来源数量 / 已生效退货 / 剩余可退 / 可用性原因 */
function prsCandidateRowHtml(candidate) {
  if (!candidate) return '';
  const available = candidate.available === true;
  return `<tr${available ? '' : ' class="text-muted"'}>
    <td>${prsEsc(candidate.sourceStockInNo || '')}</td>
    <td>${prsEsc(candidate.productName || '')}</td>
    <td>${prsEsc(candidate.spec || '')}</td>
    <td>${prsEsc(candidate.baseUnit || '')}</td>
    <td>${prsEsc(candidate.sourceBaseQuantity ?? '')}</td>
    <td>${prsEsc(candidate.effectiveReturnedBaseQuantity ?? '')}</td>
    <td>${prsEsc(candidate.remainingBaseQuantity ?? '')}</td>
    <td>${available ? '可用' : prsEsc(candidate.unavailableReason || '不可用')}</td>
  </tr>`;
}

const PRS_ERROR_PREFIX = { network: '无法连接服务器', unauthorized: '登录已过期或权限不足', server: '服务端拒绝' };

function prsErrorHtml(kind, message) {
  const prefix = PRS_ERROR_PREFIX[kind] || '操作失败';
  return `<div class="prs-error" role="alert" style="padding:8px 10px;background:#fef2f2;border:1px solid #fecaca;border-radius:8px;color:#b91c1c;font-size:13px;margin-bottom:8px">` +
    `${prsEsc(prefix)}：${prsEsc(message || '')}</div>`;
}

/* ==================== 浏览器接线（Node 单测只使用上面的纯函数） ==================== */

function prsErrorMessage(err) {
  const message = err && err.message ? err.message : String(err || '');
  if (/Failed to fetch|NetworkError|network|无法连接/i.test(message)) return { kind: 'network', message };
  if (/权限|授权|未认证|登录|禁止/.test(message)) return { kind: 'unauthorized', message };
  return { kind: 'server', message };
}

function prsSetField(key, value) {
  const el = document.getElementById('f_' + key);
  if (el) el.value = value === null || value === undefined ? '' : value;
}

/* 引用字段的可见搜索框（type: 'ref' 渲染为 f_<key>_search + 隐藏 Id）：一并回填权威名称快照 */
function prsSetRefSearch(key, text) {
  const el = document.getElementById('f_' + key + '_search');
  if (el) el.value = text || '';
}

function prsCurrentSourceId() {
  const el = document.getElementById('f_sourceStockInId');
  const n = el ? Number(el.value) : 0;
  return Number.isInteger(n) && n > 0 ? n : 0;
}

/* 读取当前表单状态：仅「待提交（草稿）」可维护来源（服务端同样 fail closed） */
async function prsLoadStatus(returnId) {
  PRS.returnId = Number(returnId) || 0;
  PRS.status = '';
  PRS.readonly = false;
  if (!(PRS.returnId > 0)) return;
  try {
    const row = await api(`/api/inventory/purchase-returns/${PRS.returnId}`);
    PRS.status = prsNormalizeStatus(row && row.status);
    PRS.readonly = !prsIsEditableStatus(PRS.status);
  } catch (err) {
    PRS.readonly = true;
    PRS.error = prsErrorMessage(err).message;
  }
}

/* 有界候选查询：关键字 / 供应商 / 仓库 / 条数全部由服务端再次校验与钳制 */
async function prsLoadCandidates() {
  PRS.loading = true;
  PRS.error = '';
  prsRender();
  const params = ['take=' + encodeURIComponent(prsNormalizeTake(PRS.take))];
  if (PRS.keyword) params.push('keyword=' + encodeURIComponent(PRS.keyword));
  if (Number(PRS.supplierId) > 0) params.push('supplierId=' + encodeURIComponent(Number(PRS.supplierId)));
  if (Number(PRS.warehouseId) > 0) params.push('warehouseId=' + encodeURIComponent(Number(PRS.warehouseId)));
  try {
    const rows = await api(`/api/inventory/purchase-returns/source-candidates?${params.join('&')}`);
    PRS.candidates = rows || [];
  } catch (err) {
    PRS.candidates = [];
    PRS.error = prsErrorMessage(err).message;
  }
  PRS.loading = false;
  prsRender();
}

/* 只读加载权威来源详情（重开回显用；不校验可维护状态） */
async function prsLoadDetail(sourceStockInId) {
  PRS.loading = true;
  PRS.error = '';
  prsRender();
  try {
    PRS.detail = await api(`/api/inventory/purchase-returns/source-candidates/${sourceStockInId}`);
    PRS.quantities = {};
    prsAvailableCandidates(PRS.detail && PRS.detail.lines).forEach(l => {
      PRS.quantities[String(l.productId)] = String(l.remainingBaseQuantity);
    });
    if (PRS.detail && !PRS.detail.available) {
      PRS.error = PRS.detail.unavailableReason || '该来源当前不可用';
    }
  } catch (err) {
    PRS.detail = null;
    PRS.error = prsErrorMessage(err).message;
  }
  PRS.loading = false;
  prsRender();
}

/* 显式选择来源（仅可维护状态）：绝不从文本推断来源 */
async function prsPickSource(sourceStockInId) {
  if (PRS.readonly) { toast(prsReadonlyReason(PRS.status), 'error'); return; }
  await prsLoadDetail(sourceStockInId);
}

function prsSetQuantity(productId, value) {
  PRS.quantities[String(productId)] = value;
  prsRender();
}

function prsSearch() {
  const kw = document.getElementById('prs-keyword');
  const take = document.getElementById('prs-take');
  PRS.keyword = kw ? kw.value.trim() : '';
  PRS.take = take ? take.value : PRS_TAKE_DEFAULT;
  return prsLoadCandidates();
}

/* 显式选择 → 回填表头与明细行（价格 / 成本留 0，服务端为唯一权威；失败保留表单输入） */
function prsApplyToForm() {
  if (PRS.readonly) { toast(prsReadonlyReason(PRS.status), 'error'); return; }
  const verdict = prsApplyQuantities(prsBuildDetailRows(PRS.detail), PRS.quantities);
  if (!verdict.ok) { PRS.error = verdict.reason; prsRender(); return; }
  if (!verdict.rows.length) { PRS.error = '该来源没有可退商品行'; prsRender(); return; }

  const header = prsHeaderFill(PRS.detail);
  prsSetField('sourceStockInId', header.sourceStockInId);
  prsSetField('sourceStockInNo', header.sourceStockInNo);
  prsSetField('supplierId', header.supplierId);
  prsSetField('supplierName', header.supplierName);
  prsSetField('warehouseId', header.warehouseId);
  prsSetRefSearch('supplierId', header.supplierName);
  if (typeof DETAIL_ROWS !== 'undefined' && typeof detailRender === 'function') {
    DETAIL_ROWS = verdict.rows;
    detailRender();
  }
  PRS.result = `已按来源 ${header.sourceStockInNo} 回填 ${verdict.rows.length} 行可退明细（数量请复核后保存）`;
  prsCloseModal();
  toast(PRS.result);
}

function prsDetailHtml(detail) {
  if (!detail) return '';
  const lines = (detail.lines || []).map(line => {
    const usable = line.available === true;
    const value = prsEsc(PRS.quantities[String(line.productId)] ?? '');
    return `<tr>
      <td>${prsEsc(line.productId)}</td>
      <td>${prsEsc(line.productName || '')}</td>
      <td>${prsEsc(line.spec || '')}</td>
      <td>${prsEsc(line.baseUnit || '')}</td>
      <td>${prsEsc(line.remainingBaseQuantity ?? '')}</td>
      <td>${usable
        ? `<input type="number" step="0.0001" min="0" style="width:120px" value="${value}" ` +
          `onchange="prsSetQuantity(${prsEsc(line.productId)}, this.value)">`
        : prsEsc(line.unavailableReason || '不可用')}</td>
    </tr>`;
  }).join('');
  return `<div class="prs-detail" style="margin-top:10px">
    <div style="font-size:13px;margin-bottom:4px">
      已选来源：<b>${prsEsc(detail.sourceStockInNo || '')}</b>
      （供应商 ${prsEsc(detail.supplierName || '')} / 仓库 ${prsEsc(detail.warehouseName || '')}）
      ${detail.available ? '' : `／<span style="color:#b91c1c">${prsEsc(detail.unavailableReason || '不可用')}</span>`}
    </div>
    <div class="table-wrap" style="max-height:240px;overflow:auto">
      <table class="data-table"><thead><tr>
        <th style="width:80px">商品ID</th><th>商品名称</th><th style="width:90px">规格</th>
        <th style="width:60px">基础单位</th><th style="width:90px">剩余可退</th><th style="width:140px">本次退货数量</th>
      </tr></thead><tbody>${lines || '<tr><td colspan="6" style="text-align:center;color:#999">没有可退商品行</td></tr>'}</tbody></table>
    </div>
    <div style="margin-top:8px;font-size:12px;color:#666">
      单价与成本单价一律留 0：本选择器绝不臆造价格 / 成本；出库成本由服务端按来源入库成本兜底。
    </div>
  </div>`;
}

function prsRender() {
  if (typeof document === 'undefined' || !PRS.open) return;
  const modal = document.getElementById('modal');
  if (!modal) return;

  const options = prsSourceOptions(PRS.candidates);
  const sourceRows = options.map(prsSourceRowHtml).join('') ||
    `<tr><td colspan="7" style="text-align:center;color:#999">没有可选的已审核来源（零容量 / 损坏证据不可选）</td></tr>`;
  const candidateRows = (PRS.candidates || []).map(prsCandidateRowHtml).join('') ||
    `<tr><td colspan="8" style="text-align:center;color:#999">候选为空</td></tr>`;

  modal.innerHTML = `
    <div class="modal modal-lg">
      <h3>选择采购退货来源入库单</h3>
      <div style="font-size:12px;color:#666;margin-bottom:6px">
        仅「已审核、未删除」且未超出可退容量的来源可选；候选与净可退容量全部来自服务端有界只读查询，绝不按单号猜测来源。
        ${PRS.readonly ? `<b style="color:#b91c1c">${prsEsc(prsReadonlyReason(PRS.status))}</b>` : ''}
      </div>
      ${PRS.error ? prsErrorHtml('server', PRS.error) : ''}
      <div class="toolbar" style="margin-bottom:8px;display:flex;gap:6px;flex-wrap:wrap">
        <input type="text" id="prs-keyword" placeholder="来源单号 / 供应商 / 商品名称 / 规格" value="${prsEsc(PRS.keyword)}"
               onkeydown="if(event.key==='Enter')prsSearch()">
        <input type="number" id="prs-take" style="width:90px" value="${prsEsc(PRS.take)}"
               title="返回条数（服务端钳制 1~${PRS_TAKE_MAX}）">
        <button type="button" class="btn btn-neutral" onclick="prsSearch()">查询</button>
      </div>
      <div class="table-wrap" style="max-height:220px;overflow:auto">
        <table class="data-table"><thead><tr>
          <th style="width:70px">来源ID</th><th>来源单号</th><th>供应商</th><th>仓库</th>
          <th style="width:70px">可退行</th><th style="width:90px">剩余合计</th><th style="width:110px">操作</th>
        </tr></thead><tbody>${sourceRows}</tbody></table>
      </div>
      <details style="margin-top:8px">
        <summary style="cursor:pointer;font-size:13px">逐商品可退证据（含不可用行与原因）</summary>
        <div class="table-wrap" style="max-height:200px;overflow:auto;margin-top:6px">
          <table class="data-table"><thead><tr>
            <th>来源单号</th><th>商品</th><th>规格</th><th>基础单位</th>
            <th>来源数量</th><th>已生效退货</th><th>剩余可退</th><th>可用性</th>
          </tr></thead><tbody>${candidateRows}</tbody></table>
        </div>
      </details>
      ${prsDetailHtml(PRS.detail)}
      <div class="modal-footer">
        <button type="button" class="btn btn-neutral" onclick="prsCloseModal()">取消</button>
        <button type="button" class="btn btn-primary" ${prsCanApply(PRS) ? '' : 'disabled '}
                onclick="prsApplyToForm()">应用到表单</button>
      </div>
    </div>`;
  modal.style.display = 'flex';
}


/* 打开选择器：新建（0）与已保存单据都可用；重开时显式回显服务端持久化的来源 */
async function openPurchaseReturnSourcePicker(returnId) {
  if (typeof document === 'undefined') return;
  const guard = prsGuardSavedId(returnId);
  if (guard) { toast(guard, 'error'); return; }

  const supplierEl = document.getElementById('f_supplierId');
  const warehouseEl = document.getElementById('f_warehouseId');
  PRS.open = true;
  PRS.candidates = [];
  PRS.detail = null;
  PRS.quantities = {};
  PRS.error = '';
  PRS.result = '';
  PRS.keyword = '';
  PRS.take = PRS_TAKE_DEFAULT;
  PRS.supplierId = supplierEl ? supplierEl.value : '';
  PRS.warehouseId = warehouseEl ? warehouseEl.value : '';
  prsRender();

  await prsLoadStatus(Number(returnId) || 0);
  await prsLoadCandidates();
  const persisted = prsCurrentSourceId();
  if (persisted > 0) await prsLoadDetail(persisted);
  else prsRender();
}

/* 表单接入：为 modules-doc2 中声明 selector: 'purchase-return-source' 的字段追加「选择来源」入口，
   也可由 SP 单据编辑页（bill-edit.js）显式传入当前单据 Id。只包裹既有全局函数，不新增模块 / 菜单。 */
function prsSourceButtonHtml(explicitId) {
  const target = (explicitId === null || explicitId === undefined || explicitId === '')
    ? 'window.__prsCurrentId || 0' : String(Number(explicitId) || 0);
  return `<div class="form-item full"><label>来源采购入库单选择</label>` +
    `<button type="button" class="btn btn-neutral" onclick="openPurchaseReturnSourcePicker(${target})">` +
    `选择来源采购入库单（按净可退容量过滤）</button>` +
    `<span class="text-muted" style="margin-left:8px">选定后自动回填来源单号 / 供应商 / 仓库与可退商品行；也可手工填写来源 Id</span>` +
    `</div>`;
}

function prsInstallFormHook() {
  if (typeof window === 'undefined') return false;
  if (window.__prsHooked) return true;
  if (typeof window.fieldHtml !== 'function' || typeof window.openForm !== 'function') return false;

  const originalOpenForm = window.openForm;
  if (!originalOpenForm.__prsWrapped) {
    const wrappedOpenForm = function (id) {
      window.__prsCurrentId = (id === undefined || id === null || id === '') ? 0 : (Number(id) || 0);
      return originalOpenForm.apply(this, arguments);
    };
    wrappedOpenForm.__prsWrapped = true;
    window.openForm = wrappedOpenForm;
  }

  const originalFieldHtml = window.fieldHtml;
  const wrappedFieldHtml = function (field) {
    const html = originalFieldHtml.apply(this, arguments);
    if (!field || field.selector !== 'purchase-return-source') return html;
    return html + prsSourceButtonHtml();
  };
  wrappedFieldHtml.__prsWrapped = true;
  window.fieldHtml = wrappedFieldHtml;
  window.__prsHooked = true;
  return true;
}

if (typeof window !== 'undefined' && typeof window.fieldHtml === 'function') prsInstallFormHook();

/* Node 单测导出（浏览器中 module 为 undefined，自动跳过） */
if (typeof module !== 'undefined' && module.exports) {
  module.exports = {
    PRS_EDITABLE_STATUS, PRS_TAKE_DEFAULT, PRS_TAKE_MAX, PRS_READONLY_TEXT, PRS_WRITTEN_FIELDS,
    prsNormalizeStatus,
    prsStatusText,
    prsIsEditableStatus,
    prsReadonlyReason,
    prsGuardSavedId,
    prsNormalizeTake,
    prsAvailableCandidates,
    prsSourceOptions,
    prsCandidateLabel,
    prsHeaderFill,
    prsValidateQuantity,
    prsBuildDetailRows,
    prsApplyQuantities,
    prsCanApply,
    prsEsc,
    prsSourceRowHtml,
    prsCandidateRowHtml,
    prsErrorHtml,
    prsErrorMessage,
    prsSourceButtonHtml,
    prsInstallFormHook,
  };
}

function prsCloseModal() {
  PRS.open = false;
  PRS.error = '';
  if (typeof closeModal === 'function') closeModal();
}

/* 安全转义（浏览器复用全局 escapeHtml；Node 单测走本地实现，绝不注入） */
function prsEsc(v) {
  const s = v === null || v === undefined ? '' : String(v);
  if (typeof escapeHtml === 'function') return escapeHtml(s);
  return s.replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

/* 条数钳制：1..PRS_TAKE_MAX；<= 0 取默认值（与服务端 ClampTake 同口径） */
function prsNormalizeTake(take) {
  const n = Number(take);
  if (!Number.isFinite(n) || n <= 0) return PRS_TAKE_DEFAULT;
  return Math.min(Math.floor(n), PRS_TAKE_MAX);
}
