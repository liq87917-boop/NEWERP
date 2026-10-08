/* ==================================================================================
   ====== 销售出库 → 已审核销售订单 来源显式选择器（ERP-376） ======
   ==================================================================================
   定位：把「来源销售订单」从「手工填 Id」升级为**显式有界选择**：服务端只读候选接口给出
        当前账号客户范围内「已审核、未删除」销售订单的逐商品剩余可发数量（ERP-343 口径），
        操作员显式选定一张来源单据后，表单自动回填权威来源 Id / 单号 / 客户与可发货商品行。
        同时覆盖两个出库业务界面：通用模块表单（crud.js，camelCase 字段 + DETAIL_ROWS）
        与销售出库 SP 单据页（bill-edit.js，PascalCase 字段 + #detail-tbody）。
   规则（与 StockOutOrderFulfillmentRules / ERP-343 同口径，服务端为唯一权威）：
     1. 只接受**显式选择**的来源销售订单 Id：候选全部来自服务端有界只读查询，
        **绝不按订单号 / 客户 / 商品 / 相似度猜测来源**，也绝不臆造明细；
     2. 候选只含当前账号客户数据范围之内「未审核 / 已删除 / 已取消」都不返回；
        重复 / 歧义订单明细、单位未知、已发货满额的来源标记为**不可用**且不可选择；
     3. 回填明细只使用服务端投影的**权威商品 / 规格 / 基础单位**，数量默认剩余可发；
        **绝不臆造价格 / 成本**（出库成本由服务端移动加权平均口径兜底）；
     4. 操作员必须录入**正数部分数量**且不得超过该商品剩余可发数量，否则不写入表单；
     5. **保留用户所选仓库**：选择器绝不改写仓库字段（仓库由操作员在有界选项中选择）；
     6. 保存 / 重开：来源 Id 由既有创建 / 修改接口持久化并原样回显；
        未选来源（null / 表单留空）的历史出库保持「无来源（未链接）」，绝不回填；
     7. 非「待提交」单据只读（服务端同样 fail closed），失败时保留表单输入并显式提示；
        应用期间禁用按钮，**防止重复点击**造成重复写入。
   边界：本选择器只做只读候选展示与表单回填：不锁库、不预留库存、不写流水 / 财务、
         不改写销售订单 / 商品 / 客户主数据，也不新增表 / 菜单 / 用户授权；
         绝不绕过 ERP-343 数量、ERP-359 退货、ERP-367 装柜取消护栏，也不从销售退货恢复发货容量。
   ================================================================================== */

/* 只有「待提交」可维护来源（与后端 StockOutController 的状态判定一致） */
const SOS_EDITABLE_STATUS = 'Pending';
const SOS_STATUS_TEXT = {
  Pending: '待提交（草稿）', Submitted: '已提交', Approved: '已审核',
  Rejected: '已驳回', Completed: '已完成', Cancelled: '已取消',
};
const SOS_TAKE_DEFAULT = 50;
const SOS_TAKE_MAX = 200;

const SOS_READONLY_TEXT = '当前单据不是「待提交（草稿）」：来源只读，如需更换请先销审 / 另建待提交单据。';

/* 选择器会写入的表单字段（**不含 warehouseId**：仓库由用户选择并保留，绝不改写） */
const SOS_WRITTEN_FIELDS = ['salesOrderId', 'customerId'];

/* 一次对话 = 一个出库表单；candidates 为服务端权威候选，detail 为选定来源的权威可发行 */
let SOS = {
  stockOutId: 0, status: '', keyword: '', customerId: '', take: SOS_TAKE_DEFAULT,
  candidates: [], detail: null, quantities: {}, loading: false, applying: false,
  error: '', result: '', readonly: false, open: false,
};

/* 状态归一化：既接受后端枚举名（Pending），也接受数字与已有中文文案 */
function sosNormalizeStatus(status) {
  if (status === null || status === undefined || status === '') return '';
  const raw = String(status).trim();
  if (SOS_STATUS_TEXT[raw]) return raw;
  const byNumber = { '0': 'Pending', '1': 'Submitted', '2': 'Approved', '3': 'Rejected', '4': 'Completed', '5': 'Cancelled' };
  if (byNumber[raw]) return byNumber[raw];
  const hit = Object.keys(SOS_STATUS_TEXT).find(k => SOS_STATUS_TEXT[k] === raw);
  return hit || raw;
}

function sosStatusText(status) {
  const key = sosNormalizeStatus(status);
  return SOS_STATUS_TEXT[key] || (key || '未知');
}

function sosIsEditableStatus(status) {
  return sosNormalizeStatus(status) === SOS_EDITABLE_STATUS;
}

function sosReadonlyReason(status) {
  return sosIsEditableStatus(status) ? '' : SOS_READONLY_TEXT;
}

/* 未保存单据（0 = 新建）允许选择来源；非法值（负数 / 非数字）一律拒绝 */
function sosGuardSavedId(stockOutId) {
  if (stockOutId === null || stockOutId === undefined || stockOutId === '') return '';
  const n = Number(stockOutId);
  if (!Number.isInteger(n) || n < 0) return '单据 Id 非法：拒绝选择来源（绝不臆造单据）';
  return '';
}

/* 条数钳制：1..SOS_TAKE_MAX；<= 0 取默认值（与服务端 ClampTake 同口径） */
function sosNormalizeTake(take) {
  const n = Number(take);
  if (!Number.isFinite(n) || n <= 0) return SOS_TAKE_DEFAULT;
  return Math.min(Math.floor(n), SOS_TAKE_MAX);
}

/* 可选择的候选：仅服务端标记 available = true（零容量 / 重复歧义 / 损坏证据绝不可选） */
function sosAvailableCandidates(candidates) {
  return (candidates || []).filter(c => c && c.available === true);
}

/* 可选择的来源单据（按来源订单 Id 去重）：只保留至少有一条可发行的来源 */
function sosSourceOptions(candidates) {
  const bySource = new Map();
  sosAvailableCandidates(candidates).forEach(c => {
    const id = String(c.salesOrderId);
    const option = bySource.get(id) || {
      salesOrderId: c.salesOrderId, orderNo: c.orderNo, customerId: c.customerId,
      customerName: c.customerName, lineCount: 0, remainingTotal: 0,
    };
    option.lineCount += 1;
    option.remainingTotal += Number(c.remainingBaseQuantity) || 0;
    bySource.set(id, option);
  });
  return Array.from(bySource.values());
}

/* 候选展示文案：订单号 / 客户 / 商品 / 规格 / 基础单位 / 授权数量 / 已发货 / 剩余可发 */
function sosCandidateLabel(candidate) {
  if (!candidate) return '';
  const parts = [
    candidate.orderNo ? `来源订单 ${candidate.orderNo}` : '来源订单 未知',
    candidate.customerName ? `客户 ${candidate.customerName}` : '',
    candidate.productName || `商品 Id ${candidate.productId ?? ''}`,
    candidate.spec ? `规格 ${candidate.spec}` : '规格 未知',
    `基础单位 ${candidate.baseUnit || '未知'}`,
    `授权数量 ${candidate.authorizedBaseQuantity ?? ''}`,
    `已发货 ${candidate.shippedBaseQuantity ?? 0}`,
    `剩余可发 ${candidate.remainingBaseQuantity ?? ''}`,
  ];
  return parts.filter(Boolean).join(' / ');
}

/* 显式选择回填表头：权威来源 Id / 单号 / 客户（绝不臆造，绝不按文本推断）；**不含仓库**（保留用户所选） */
function sosHeaderFill(detail) {
  if (!detail) {
    return { salesOrderId: 0, orderNo: '', customerId: '', customerName: '' };
  }
  return {
    salesOrderId: detail.salesOrderId,
    orderNo: detail.orderNo || '',
    customerId: detail.customerId,
    customerName: detail.customerName || '',
  };
}

/* 数量校验：必须为正数且不超过该商品剩余可发数量（正数部分数量语义） */
function sosValidateQuantity(value, remaining) {
  const raw = value === null || value === undefined ? '' : String(value).trim();
  const max = Number(remaining);
  if (raw === '') return { ok: false, value: 0, reason: '请录入大于 0 的发货数量' };
  if (!/^\d+(\.\d+)?$/.test(raw)) return { ok: false, value: 0, reason: `数量 [${raw}] 不是合法正数` };
  const n = Number(raw);
  if (!(n > 0)) return { ok: false, value: 0, reason: '发货数量必须大于 0' };
  if (Number.isFinite(max) && n > max + 1e-9) {
    return { ok: false, value: 0, reason: `数量 ${n} 超过该商品剩余可发数量 ${max}` };
  }
  return { ok: true, value: n, reason: '' };
}

/* 选定来源后的可录入明细行：只使用服务端权威商品 / 规格 / 基础单位；
   本页不录入单价 / 成本（**绝不臆造价格**，出库成本由服务端加权平均口径兜底）。 */
function sosBuildDetailRows(detail) {
  return sosAvailableCandidates(detail && detail.lines).map(line => ({
    productId: line.productId,
    productName: line.productName || '',
    spec: line.spec || '',
    unit: line.baseUnit || '',
    quantity: Number(line.remainingBaseQuantity) || 0,
    weight: 0,
    volume: 0,
    batchNo: '',
    remark: `来源销售订单 ${line.orderNo || ''}`.trim(),
    salesOrderId: line.salesOrderId,
    remainingBaseQuantity: Number(line.remainingBaseQuantity) || 0,
  }));
}

/* SP 单据编辑页（bill-edit.js）明细行口径：与既有 addDetailRow / collectDetails 同一字段名（PascalCase）；
   单价 / 金额一律留 0（**绝不臆造价格**），操作员核对后保存。 */
function sosBillDetailRows(rows) {
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
function sosApplyQuantities(rows, quantities) {
  const result = [];
  for (const row of rows || []) {
    const verdict = sosValidateQuantity((quantities || {})[String(row.productId)], row.remainingBaseQuantity);
    if (!verdict.ok) {
      return { ok: false, reason: `${row.productName || row.productId}：${verdict.reason}`, rows };
    }
    result.push(Object.assign({}, row, { quantity: verdict.value }));
  }
  return { ok: true, reason: '', rows: result };
}

/* 应用闸门：只读 / 加载中 / **正在应用（防止重复点击）** / 未选来源 / 无可发数量 都不允许应用 */
function sosCanApply(state) {
  const s = state || {};
  if (s.readonly || s.loading || s.applying) return false;
  if (!s.detail || !s.detail.salesOrderId) return false;
  const verdict = sosApplyQuantities(sosBuildDetailRows(s.detail), s.quantities);
  return verdict.ok && verdict.rows.length > 0;
}

/* 安全转义（浏览器复用全局 escapeHtml；Node 单测走本地实现，绝不注入） */
function sosEsc(v) {
  const s = v === null || v === undefined ? '' : String(v);
  if (typeof escapeHtml === 'function') return escapeHtml(s);
  return s.replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

/* ==================== 浏览器接线（Node 单测只使用上面的纯函数） ==================== */

/* 可选来源单据行（按来源订单 Id 去重：只保留至少有一条可发行的已审核来源） */
function sosSourceRowHtml(source) {
  if (!source) return '';
  return `<tr data-source-id="${sosEsc(source.salesOrderId)}">
    <td>${sosEsc(source.salesOrderId)}</td>
    <td>${sosEsc(source.orderNo || '')}</td>
    <td>${sosEsc(source.customerName || '')}</td>
    <td>${sosEsc(source.lineCount)}</td>
    <td>${sosEsc(source.remainingTotal)}</td>
    <td><button type="button" class="btn btn-neutral btn-sm" onclick="sosPickSource(${sosEsc(source.salesOrderId)})">选择此来源</button></td>
  </tr>`;
}

/* 候选证据行：订单号 / 客户 / 商品 / 规格 / 基础单位 / 授权数量 / 已发货 / 剩余可发 / 可用性原因 */
function sosCandidateRowHtml(candidate) {
  if (!candidate) return '';
  const available = candidate.available === true;
  return `<tr${available ? '' : ' class="text-muted"'}>
    <td>${sosEsc(candidate.orderNo || '')}</td>
    <td>${sosEsc(candidate.customerName || '')}</td>
    <td>${sosEsc(candidate.productName || '')}</td>
    <td>${sosEsc(candidate.spec || '')}</td>
    <td>${sosEsc(candidate.baseUnit || '')}</td>
    <td>${sosEsc(candidate.authorizedBaseQuantity ?? '')}</td>
    <td>${sosEsc(candidate.shippedBaseQuantity ?? '')}</td>
    <td>${sosEsc(candidate.remainingBaseQuantity ?? '')}</td>
    <td>${available ? '可用' : sosEsc(candidate.unavailableReason || '不可用')}</td>
  </tr>`;
}

const SOS_ERROR_PREFIX = { network: '无法连接服务器', unauthorized: '登录已过期或权限不足', server: '服务端拒绝' };

function sosErrorHtml(kind, message) {
  const prefix = SOS_ERROR_PREFIX[kind] || '操作失败';
  return `<div class="sos-error" role="alert" style="padding:8px 10px;background:#fef2f2;border:1px solid #fecaca;border-radius:8px;color:#b91c1c;font-size:13px;margin-bottom:8px">` +
    `${sosEsc(prefix)}：${sosEsc(message || '')}</div>`;
}

function sosErrorMessage(err) {
  const message = err && err.message ? err.message : String(err || '');
  if (/Failed to fetch|NetworkError|network|无法连接/i.test(message)) return { kind: 'network', message };
  if (/权限|授权|未认证|登录|禁止/.test(message)) return { kind: 'unauthorized', message };
  return { kind: 'server', message };
}

/* 回填字段：同时覆盖通用模块（camelCase → f_salesOrderId / f_customerId）与 SP 单据编辑页
   （bill-config 的 PascalCase → f_SalesOrderId / f_CustomerId）两种命名，绝不改写仓库字段。 */
function sosSetField(key, value) {
  const v = value === null || value === undefined ? '' : value;
  const pascal = key.charAt(0).toUpperCase() + key.slice(1);
  ['f_' + key, 'f_' + pascal].forEach(id => {
    const el = document.getElementById(id);
    if (el) el.value = v;
  });
}

/* 当前表单上的来源 Id：空 / 非法一律按「未选择来源（历史未链接）」处理，绝不按文本推断 */
function sosCurrentSourceId() {
  const el = document.getElementById('f_salesOrderId') || document.getElementById('f_SalesOrderId');
  if (!el) return 0;
  const raw = String(el.value || '').trim();
  if (raw === '') return 0;
  return /^\d+$/.test(raw) ? Number(raw) : 0;
}

/* 把选定来源的可发行写入当前表单明细：
   优先 SP 单据编辑页（#detail-tbody + 既有 addDetailRow），否则通用模块明细（#detail-body + DETAIL_ROWS）；
   两者都不存在时返回 false（绝不臆造表单结构，避免只回填表头的半成品）。 */
function sosApplyRowsToForm(rows) {
  const billTbody = document.getElementById('detail-tbody');
  if (billTbody && typeof addDetailRow === 'function') {
    billTbody.innerHTML = '';
    sosBillDetailRows(rows).forEach(r => addDetailRow(r));
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

async function sosLoadStatus(stockOutId) {
  SOS.status = '';
  if (!stockOutId) return;
  try {
    const row = await api(`/api/stock-outs/${stockOutId}`);
    SOS.status = sosNormalizeStatus(row && row.status);
  } catch (err) {
    SOS.error = sosErrorMessage(err).message;
  }
  SOS.readonly = !sosIsEditableStatus(SOS.status);
}

/* 有界只读候选：关键字 / 客户 / 条数由服务端钳制，绝不无界拉取 */
async function sosLoadCandidates() {
  SOS.loading = true;
  SOS.error = '';
  sosRender();
  const params = ['take=' + encodeURIComponent(sosNormalizeTake(SOS.take))];
  if (SOS.keyword) params.push('keyword=' + encodeURIComponent(SOS.keyword));
  if (Number(SOS.customerId) > 0) params.push('customerId=' + encodeURIComponent(Number(SOS.customerId)));
  try {
    const rows = await api(`/api/stock-outs/source-candidates?${params.join('&')}`);
    SOS.candidates = rows || [];
  } catch (err) {
    SOS.candidates = [];
    SOS.error = sosErrorMessage(err).message;
  }
  SOS.loading = false;
  sosRender();
}

/* 只读加载权威来源详情（重开回显用；不校验可维护状态） */
async function sosLoadDetail(salesOrderId) {
  SOS.loading = true;
  SOS.error = '';
  sosRender();
  try {
    SOS.detail = await api(`/api/stock-outs/source-candidates/${salesOrderId}`);
    SOS.quantities = {};
    sosAvailableCandidates(SOS.detail && SOS.detail.lines).forEach(l => {
      SOS.quantities[String(l.productId)] = String(l.remainingBaseQuantity);
    });
    if (SOS.detail && !SOS.detail.available) {
      SOS.error = SOS.detail.unavailableReason || '该来源当前不可用';
    }
  } catch (err) {
    SOS.detail = null;
    SOS.error = sosErrorMessage(err).message;
  }
  SOS.loading = false;
  sosRender();
}

/* 显式选择来源（仅可维护状态）：绝不从文本推断来源 */
async function sosPickSource(salesOrderId) {
  if (SOS.readonly) { toast(sosReadonlyReason(SOS.status), 'error'); return; }
  await sosLoadDetail(salesOrderId);
}

function sosSetQuantity(productId, value) {
  SOS.quantities[String(productId)] = value;
  sosRender();
}

function sosSearch() {
  const kw = document.getElementById('sos-keyword');
  const take = document.getElementById('sos-take');
  SOS.keyword = kw ? kw.value.trim() : '';
  SOS.take = take ? take.value : SOS_TAKE_DEFAULT;
  return sosLoadCandidates();
}

/* 显式选择 → 回填表头与明细行（不臆造价格；**保留用户所选仓库**；失败保留表单输入）；
   应用期间置 applying 闸门并提前返回，**防止重复点击**造成重复写入。 */
function sosApplyToForm() {
  if (SOS.applying) return;
  if (SOS.readonly) { toast(sosReadonlyReason(SOS.status), 'error'); return; }

  SOS.applying = true;
  try {
    const verdict = sosApplyQuantities(sosBuildDetailRows(SOS.detail), SOS.quantities);
    if (!verdict.ok) { SOS.error = verdict.reason; sosRender(); return; }
    if (!verdict.rows.length) { SOS.error = '该来源没有可发商品行'; sosRender(); return; }

    const header = sosHeaderFill(SOS.detail);
    sosSetField('salesOrderId', header.salesOrderId);
    sosSetField('customerId', header.customerId);
    /* 保留用户所选仓库：选择器绝不改写 f_warehouseId / f_WarehouseId（SOS_WRITTEN_FIELDS 亦不含仓库）。 */
    if (!sosApplyRowsToForm(verdict.rows)) {
      SOS.error = '当前页面没有可写入的出库明细区域：拒绝只回填来源（避免半成品写入）';
      sosRender();
      return;
    }
    SOS.result = `已按来源销售订单 ${header.orderNo} 回填 ${verdict.rows.length} 行可发明细（数量请复核后保存）`;
    sosCloseModal();
    toast(SOS.result);
  } finally {
    SOS.applying = false;
  }
}

function sosDetailHtml(detail) {
  if (!detail) return '';
  const lines = (detail.lines || []).map(line => {
    const usable = line.available === true;
    const value = sosEsc(SOS.quantities[String(line.productId)] ?? '');
    return `<tr>
      <td>${sosEsc(line.productId)}</td>
      <td>${sosEsc(line.productName || '')}</td>
      <td>${sosEsc(line.spec || '')}</td>
      <td>${sosEsc(line.baseUnit || '')}</td>
      <td>${sosEsc(line.remainingBaseQuantity ?? '')}</td>
      <td>${usable
        ? `<input type="number" step="0.0001" min="0" style="width:120px" value="${value}" ` +
          `onchange="sosSetQuantity(${sosEsc(line.productId)}, this.value)">`
        : sosEsc(line.unavailableReason || '不可用')}</td>
    </tr>`;
  }).join('');
  return `<div class="sos-detail" style="margin-top:10px">
    <div style="font-size:13px;margin-bottom:4px">
      已选来源：<b>${sosEsc(detail.orderNo || '')}</b>
      （客户 ${sosEsc(detail.customerName || '')}）
      ${detail.available ? '' : `／<span style="color:#b91c1c">${sosEsc(detail.unavailableReason || '不可用')}</span>`}
    </div>
    <div class="table-wrap" style="max-height:240px;overflow:auto">
      <table class="data-table"><thead><tr>
        <th style="width:80px">商品ID</th><th>商品名称</th><th style="width:90px">规格</th>
        <th style="width:60px">基础单位</th><th style="width:90px">剩余可发</th><th style="width:140px">本次发货数量</th>
      </tr></thead><tbody>${lines || '<tr><td colspan="6" style="text-align:center;color:#999">没有可发商品行</td></tr>'}</tbody></table>
    </div>
    <div style="margin-top:8px;font-size:12px;color:#666">
      本页不录入单价 / 成本：本选择器绝不臆造价格；出库成本仍由服务端按移动加权平均口径兜底。仓库保留您当前的选择。
    </div>
  </div>`;
}

function sosRender() {
  if (typeof document === 'undefined' || !SOS.open) return;
  const modal = document.getElementById('modal');
  if (!modal) return;

  const options = sosSourceOptions(SOS.candidates);
  const sourceRows = options.map(sosSourceRowHtml).join('') ||
    `<tr><td colspan="6" style="text-align:center;color:#999">没有可选的已审核来源（重复 / 歧义明细、单位未知、已发货满额不可选）</td></tr>`;
  const candidateRows = (SOS.candidates || []).map(sosCandidateRowHtml).join('') ||
    `<tr><td colspan="10" style="text-align:center;color:#999">候选为空</td></tr>`;

  modal.innerHTML = `
    <div class="modal modal-lg">
      <h3>选择销售出库来源销售订单</h3>
      <div style="font-size:12px;color:#666;margin-bottom:6px">
        仅「已审核、未删除」且仍有剩余可发数量的来源可选；候选与剩余可发数量全部来自服务端有界只读查询，绝不按订单号猜测来源。
        ${SOS.readonly ? `<b style="color:#b91c1c">${sosEsc(sosReadonlyReason(SOS.status))}</b>` : ''}
      </div>
      ${SOS.error ? sosErrorHtml('server', SOS.error) : ''}
      <div class="toolbar" style="margin-bottom:8px;display:flex;gap:6px;flex-wrap:wrap">
        <input type="text" id="sos-keyword" placeholder="订单号 / 客户 / 商品名称 / 规格" value="${sosEsc(SOS.keyword)}"
               onkeydown="if(event.key==='Enter')sosSearch()">
        <input type="number" id="sos-take" style="width:90px" value="${sosEsc(SOS.take)}"
               title="返回条数（服务端钳制 1~${SOS_TAKE_MAX}）">
        <button type="button" class="btn btn-neutral" onclick="sosSearch()">查询</button>
      </div>
      <div class="table-wrap" style="max-height:220px;overflow:auto">
        <table class="data-table"><thead><tr>
          <th style="width:70px">来源ID</th><th>订单号</th><th>客户</th>
          <th style="width:70px">可发行</th><th style="width:90px">剩余合计</th><th style="width:110px">操作</th>
        </tr></thead><tbody>${sourceRows}</tbody></table>
      </div>
      <details style="margin-top:8px">
        <summary style="cursor:pointer;font-size:13px">逐商品可发证据（含不可用行与原因）</summary>
        <div class="table-wrap" style="max-height:200px;overflow:auto;margin-top:6px">
          <table class="data-table"><thead><tr>
            <th>订单号</th><th>客户</th><th>商品</th><th>规格</th><th>基础单位</th>
            <th>授权数量</th><th>已发货</th><th>剩余可发</th><th>可用性</th>
          </tr></thead><tbody>${candidateRows}</tbody></table>
        </div>
      </details>
      ${sosDetailHtml(SOS.detail)}
      <div class="modal-footer">
        <button type="button" class="btn btn-neutral" onclick="sosCloseModal()">取消</button>
        <button type="button" class="btn btn-primary" ${sosCanApply(SOS) ? '' : 'disabled '}
                onclick="sosApplyToForm()">应用到表单</button>
      </div>
    </div>`;
  modal.style.display = 'flex';
}

function sosCloseModal() {
  SOS.open = false;
  SOS.error = '';
  if (typeof closeModal === 'function') closeModal();
}

/* 打开选择器：新建（0）与已保存单据都可用；重开时显式回显服务端持久化的来源（未选 = 历史未链接） */
async function openStockOutSourcePicker(stockOutId) {
  if (typeof document === 'undefined') return;
  const guard = sosGuardSavedId(stockOutId);
  if (guard) { toast(guard, 'error'); return; }

  const customerEl = document.getElementById('f_customerId') || document.getElementById('f_CustomerId');
  SOS.open = true;
  SOS.candidates = [];
  SOS.detail = null;
  SOS.quantities = {};
  SOS.error = '';
  SOS.result = '';
  SOS.keyword = '';
  SOS.take = SOS_TAKE_DEFAULT;
  SOS.customerId = customerEl ? customerEl.value : '';
  sosRender();

  await sosLoadStatus(Number(stockOutId) || 0);
  await sosLoadCandidates();
  const persisted = sosCurrentSourceId();
  if (persisted > 0) await sosLoadDetail(persisted);
  else sosRender();
}

/* 表单接入：为 modules-doc2 中声明 selector: 'stock-out-source' 的字段追加「选择来源」入口，
   也可由 SP 单据编辑页（bill-edit.js）显式传入当前单据 Id。只包裹既有全局函数，不新增模块 / 菜单。 */
function sosSourceButtonHtml(explicitId) {
  const target = (explicitId === null || explicitId === undefined || explicitId === '')
    ? 'window.__sosCurrentId || 0' : String(Number(explicitId) || 0);
  return `<div class="form-item full"><label>来源销售订单选择</label>` +
    `<button type="button" class="btn btn-neutral" onclick="openStockOutSourcePicker(${target})">` +
    `选择来源销售订单（按剩余可发数量过滤）</button>` +
    `<span class="text-muted" style="margin-left:8px">选定后自动回填权威客户与可发货商品行；仓库保留您的选择；留空 = 历史未链接（保持无来源）；也可手工填写来源 Id</span>` +
    `</div>`;
}

function sosInstallFormHook() {
  if (typeof window === 'undefined') return false;
  if (window.__sosHooked) return true;
  if (typeof window.fieldHtml !== 'function' || typeof window.openForm !== 'function') return false;

  const originalOpenForm = window.openForm;
  if (!originalOpenForm.__sosWrapped) {
    const wrappedOpenForm = function (id) {
      window.__sosCurrentId = (id === undefined || id === null || id === '') ? 0 : (Number(id) || 0);
      return originalOpenForm.apply(this, arguments);
    };
    wrappedOpenForm.__sosWrapped = true;
    window.openForm = wrappedOpenForm;
  }

  const originalFieldHtml = window.fieldHtml;
  const wrappedFieldHtml = function (field) {
    const html = originalFieldHtml.apply(this, arguments);
    if (!field || field.selector !== 'stock-out-source') return html;
    return html + sosSourceButtonHtml();
  };
  wrappedFieldHtml.__sosWrapped = true;
  window.fieldHtml = wrappedFieldHtml;
  window.__sosHooked = true;
  return true;
}

if (typeof window !== 'undefined' && typeof window.fieldHtml === 'function') sosInstallFormHook();

/* Node 单测导出（浏览器中 module 为 undefined，自动跳过） */
if (typeof module !== 'undefined' && module.exports) {
  module.exports = {
    SOS_EDITABLE_STATUS, SOS_TAKE_DEFAULT, SOS_TAKE_MAX, SOS_READONLY_TEXT, SOS_WRITTEN_FIELDS,
    sosNormalizeStatus,
    sosStatusText,
    sosIsEditableStatus,
    sosReadonlyReason,
    sosGuardSavedId,
    sosNormalizeTake,
    sosAvailableCandidates,
    sosSourceOptions,
    sosCandidateLabel,
    sosHeaderFill,
    sosValidateQuantity,
    sosBuildDetailRows,
    sosBillDetailRows,
    sosApplyQuantities,
    sosCanApply,
    sosEsc,
    sosSourceRowHtml,
    sosCandidateRowHtml,
    sosErrorHtml,
    sosErrorMessage,
    sosSourceButtonHtml,
    sosInstallFormHook,
  };
}
