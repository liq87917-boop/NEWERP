/* ============ 单证明细行（ERP-051：商业发票 / 装箱单 的商品明细行快照） ============ */

/* 依赖：app.js（api / toast / escapeHtml / fmtMoney）、crud.js（closeModal）、bill-edit.js（searchRef / selectRef）。
   契约：
   1. 只有商业发票（含单价与金额）与装箱单（含箱数 / 净重 / 毛重，不含价格）允许维护明细行；
      单证状态只有「待制作 / 已制作」为准备状态 —— 已提交客户 / 已使用（以及未知状态）时明细行只读；
   2. 行金额一律服务端按「数量 × 单价」与币种精度计算：前端只提交数量与单价，不提交金额、不提交币种；
   3. 商品资料为可选引用：选择后由服务端写入商品编码 / 中英文名称 / 规格 / 单位快照；
      未改指商品时不刷新历史快照，商品停用 / 删除只做只读标注；
   4. 箱数 / 净重 / 毛重未填写即留空（不发 0），空值显示为「—」而不是 0；
   5. 差异提示只是提示：行合计与单证台账金额不一致时**不会**自动改写单证金额；
   6. 维护明细行不改写商品资料、销售订单、采购订单、装柜清单、库存与库存流水、发票、退税、费用或财务记录。
   文案与服务端 TradeDocumentItemRules / TradeDocumentItemService 保持一致。 */

const TDI_API = '/api/trade/documents';

/* 当前对话框状态：
   - docId / data / editing：当前展示的单证、清单数据与正在编辑的行（null = 未编辑，{id:0} = 新增）；
   - gen：对话框代际 —— 每次打开 / 切换单证 / 关闭自增；所有异步响应必须连同代际一起校验，代际不符一律丢弃；
   - editingGen：进入编辑时的代际，用于确认保存 / 删除仍作用于同一对话框代际与同一编辑身份；
   - loadGen：重新读取序号 —— 同一代际内只有最后一次读取可以落地（乱序响应丢弃）；
   - open：对话框是否仍在打开 —— 关闭后旧响应不得重新打开 / 渲染 / 覆盖数据；
   - pending：进行中的一次行变更提交 —— 同一时刻只允许一次，杜绝重复点击重复提交；
   - editingDraft：成功刷新时若用户已改指另一行，保留其尚未提交的表单输入（绝不覆盖另一个编辑目标）。 */
let TDI = {
  docId: 0, data: null, editing: null,
  gen: 0, editingGen: 0, loadGen: 0, open: false, pending: null, editingDraft: null
};

/* 编辑身份：当前编辑行的 Id（0 = 未编辑 / 新增） */
function tdiEditingId() { return TDI.editing && TDI.editing.id ? Number(TDI.editing.id) : 0; }

/* 捕获操作上下文（单证 Id + 对话框代际 + 编辑身份）：异步结果只有在上下文仍然一致时才允许落地 */
function tdiCapture() {
  return { docId: TDI.docId, gen: TDI.gen, open: TDI.open, editingId: tdiEditingId(), editingGen: TDI.editingGen };
}

/* 上下文是否仍然有效：仍打开、仍是同一张单证、仍是同一对话框代际 */
function tdiIsCurrent(ctx) {
  return !!ctx && TDI.open && ctx.docId === TDI.docId && ctx.gen === TDI.gen;
}

/* 在当前已加载清单中按行 Id 定位（只读；找不到即视为已删除 / 不属于当前文档） */
function tdiFindItem(id) {
  return (((TDI.data || {}).items) || []).find(x => String(x.id) === String(id)) || null;
}

/* 行操作入口（单证中心 rowActions；参数为单证 Id） */
async function manageTradeDocumentItems(id) {
  const docId = Number(id) || 0;
  if (!docId) { toast('请先选择要维护明细行的单证', 'error'); return; }
  /* 切换文档 = 新代际：旧代际所有在途响应（读取 / 保存 / 删除）全部失效，绝不覆盖新单证数据 */
  TDI = {
    docId, data: null, editing: null,
    gen: TDI.gen + 1, editingGen: 0, loadGen: TDI.loadGen,
    open: true, pending: null, editingDraft: null
  };
  await tdiReload();
}

/* 重新读取明细行清单（有界；服务端一次查询 + 一次批量装载商品引用） */
async function tdiReload() {
  if (!TDI.docId || !TDI.open) return;
  const ctx = tdiCapture();
  const loadSeq = ++TDI.loadGen;
  try {
    const data = await api(`${TDI_API}/${ctx.docId}/items`);
    /* 陈旧响应（已切换文档 / 已关闭 / 已有更新的读取）绝不允许覆盖当前数据或渲染 */
    if (!tdiIsCurrent(ctx) || loadSeq !== TDI.loadGen) return;
    TDI.data = data;
    tdiRender();
  } catch (err) {
    if (!tdiIsCurrent(ctx) || loadSeq !== TDI.loadGen) return;
    toast(err.message, 'error');
  }
}

/* 关闭对话框：代际自增，所有在途响应立即失效（不会重新打开对话框、不会落地旧数据、不会清空新输入） */
function tdiOnClose() {
  TDI.open = false;
  TDI.gen += 1;
  TDI.editingGen = 0;
  TDI.loadGen += 1;
  TDI.data = null;
  TDI.editing = null;
  TDI.editingDraft = null;
  TDI.docId = 0;
  TDI.pending = null;
  return true;
}

/* 对话框内「关闭」按钮：先失效当前代际，再真正隐藏（crud.js closeModal） */
function tdiClose() {
  tdiOnClose();
  if (typeof closeModal === 'function') closeModal();
}

/* 兜底：包装全局 closeModal，任何关闭路径都让当前代际失效（只在首次渲染时安装一次） */
function tdiEnsureCloseHook() {
  if (typeof window === 'undefined' || typeof window.closeModal !== 'function' || window.closeModal.__tdiWrapped) return;
  const original = window.closeModal;
  const wrapped = function () {
    if (TDI.open || TDI.pending) tdiOnClose();
    return original.apply(this, arguments);
  };
  wrapped.__tdiWrapped = true;
  window.closeModal = wrapped;
}

/* 记住进行中的提交：同一对话框代际只允许一次（重复点击直接忽略，不产生第二次请求） */
function tdiBeginMutation(ctx, kind, itemId) {
  if (TDI.pending) return null;
  const token = { kind, docId: ctx.docId, gen: ctx.gen, itemId: itemId || 0 };
  TDI.pending = token;
  return token;
}

function tdiEndMutation(token) { if (TDI.pending === token) TDI.pending = null; }

/* 运行时状态快照（只读；用于单测断言与故障定位，不改变任何业务口径） */
function tdiSnapshot() {
  return {
    docId: TDI.docId, open: TDI.open, gen: TDI.gen, loadGen: TDI.loadGen,
    editingId: tdiEditingId(), editingGen: TDI.editingGen,
    pendingKind: TDI.pending ? TDI.pending.kind : '', pendingItemId: TDI.pending ? TDI.pending.itemId : 0,
    dataDocNo: TDI.data ? (TDI.data.docNo || '') : '',
    itemIds: (((TDI.data || {}).items) || []).map(x => Number(x.id))
  };
}

function tdiEsc(v) { return escapeHtml(v); }

/* 金额：null / undefined = 无值（留空显示「—」，绝不显示为 0） */
function tdiMoney(v) { return (v === null || v === undefined) ? '—' : fmtMoney(v); }

/* 数值：null / undefined = 未登记（显示「—」，不臆造为 0） */
function tdiNum(v) { return (v === null || v === undefined) ? '—' : String(v); }

/* 对话框渲染：单证信息 + 行合计 / 差异提示 + （可维护时）编辑表单 + 明细行表 */
function tdiRender() {
  const d = TDI.data;
  if (!d) return;

  /* 任何关闭路径都让当前代际失效（只安装一次）：关闭后在途响应不得重新渲染 */
  tdiEnsureCloseHook();

  const editable = !!(d.editable && d.docTypeSupported);
  const body = (d.items || []).map(row => tdiRowHtml(row, editable)).join('');
  const priceTip = d.docType === '商业发票'
    ? '单价最多 4 位小数，行金额由服务端按「数量 × 单价」与币种精度计算（前端提交的金额一律不被信任）'
    : '装箱单不含价格口径：单价恒为 0，行金额不派生；只登记数量、箱数、净重与毛重';

  const modal = document.getElementById('modal');
  modal.innerHTML = `<div class="modal modal-lg" style="width:1520px;max-width:97vw;max-height:92vh;overflow:auto">
    <h3>📦 单证明细行：${tdiEsc(d.docNo || '')}（${tdiEsc(d.docType || '')}）</h3>
    <div class="pd-hint">
      ⚠️ 明细行是单证的<b>行级快照证据</b>：<b>不是</b>第二套商品主数据、<b>不是</b>库存交易、<b>不是</b>报关核定价格，
      也<b>不是</b>退税或税务依据 —— 维护明细行不会改写商品资料、销售订单、采购订单、装柜清单、库存与库存流水、
      发票、退税、费用或财务记录，也<b>不会</b>回写单证表头金额。
    </div>
    <div class="form-grid" style="grid-template-columns:repeat(4,1fr)">
      <div><b>单证状态</b>：${tdiEsc(d.status || '')}${editable ? '' : '（明细行只读）'}</div>
      <div><b>币种 / 精度</b>：${tdiEsc(d.currency || '')} / ${tdiNum(d.amountDecimals)} 位小数</div>
      <div><b>单证台账金额</b>：${d.headerAmountRecorded ? tdiMoney(d.headerAmount) + ' ' + tdiEsc(d.currency || '') : '未登记（金额 0 视为未填写）'}</div>
      <div><b>明细行数</b>：${tdiNum(d.lineCount)}${d.truncated ? `（已截断，单次上限 ${tdiNum(d.take)} 行）` : ''}</div>
      <div><b>行金额合计（服务端计算）</b>：${tdiMoney(d.lineAmountTotal)} ${tdiEsc(d.currency || '')}</div>
      <div><b>与表头金额差异</b>：${d.amountMismatch ? '<b style="color:#dc2626">不一致（仅提示）</b>' : '无差异 / 不可判定'}</div>
      <div><b>箱数合计</b>：${d.packageCountRecordedLines ? tdiNum(d.packageCountTotal) + `（${tdiNum(d.packageCountRecordedLines)} 行登记）` : '无行登记（不臆造为 0）'}</div>
      <div><b>净重 / 毛重合计</b>：${d.weightRecordedLines ? tdiNum(d.netWeightTotal) + ' / ' + tdiNum(d.grossWeightTotal) + ` kg（${tdiNum(d.weightRecordedLines)} 行登记）` : '无行登记（不臆造为 0）'}</div>
    </div>
    <div class="pd-hint">可维护性：${tdiEsc(d.editabilityText || '')}</div>
    <div class="pd-hint">单证类型：${tdiEsc(d.docTypeText || '')}</div>
    <div class="pd-hint">差异口径：${tdiEsc(d.amountMismatchText || '')}</div>
    ${editable ? tdiFormHtml(priceTip) : ''}
    <h4>明细行（按行序；商品引用为只读标注）</h4>
    <div class="table-wrap" style="max-height:38vh;overflow:auto">
      <table><thead><tr>
        <th>行序</th><th>商品编码 / 中文名称</th><th>英文名称</th><th>规格</th>
        <th class="text-right">数量</th><th>单位</th><th class="text-right">单价</th><th class="text-right">行金额</th>
        <th class="text-right">箱数</th><th class="text-right">净重</th><th class="text-right">毛重</th>
        <th>备注</th><th>商品引用</th><th>操作</th>
      </tr></thead>
      <tbody>${body || `<tr><td colspan="14" class="empty">本单证没有明细行${editable ? '（可用上方表单新增）' : '（当前状态或类型不允许维护明细行）'}</td></tr>`}</tbody></table>
    </div>
    <div class="pd-hint">行口径：${tdiEsc(d.rule || '')}</div>
    <div class="pd-hint">边界：${tdiEsc(d.boundary || '')}</div>
    <div class="modal-footer"><button class="btn btn-neutral" onclick="tdiClose()">关闭</button></div>
  </div>`;
  modal.style.display = 'flex';

  if (TDI.editing) tdiFillForm(TDI.editing);
}

/* 一行明细的表格行（可维护时给出修改 / 删除入口） */
function tdiRowHtml(row, editable) {
  const product = [row.productCode, row.productNameCn].filter(x => x && String(x).trim()).join(' / ');
  const availability = row.productAvailabilityText
    ? `<div class="text-muted" style="font-size:12px">${tdiEsc(row.productAvailabilityText)}</div>` : '';
  return `<tr>
    <td>${tdiNum(row.lineNo)}</td>
    <td>${product ? tdiEsc(product) : '—'}</td>
    <td>${row.productNameEn ? tdiEsc(row.productNameEn) : '—'}</td>
    <td>${row.spec ? tdiEsc(row.spec) : '—'}</td>
    <td class="text-right">${tdiNum(row.quantity)}</td>
    <td>${row.unit ? tdiEsc(row.unit) : '—'}</td>
    <td class="text-right">${tdiNum(row.unitPrice)}</td>
    <td class="text-right">${tdiNum(row.lineAmount)}</td>
    <td class="text-right">${tdiNum(row.packageCount)}</td>
    <td class="text-right">${tdiNum(row.netWeight)}</td>
    <td class="text-right">${tdiNum(row.grossWeight)}</td>
    <td>${row.remark ? tdiEsc(row.remark) : '—'}</td>
    <td>${row.productId > 0 ? `Id=${tdiNum(row.productId)}` : '人工录入'}${availability}</td>
    <td>${editable ? `<button class="btn btn-neutral btn-sm" onclick="tdiEditItem(${row.id})">修改</button>
      <button class="btn btn-danger btn-sm" onclick="tdiDeleteItem(${row.id})">删除</button>` : '只读'}</td>
  </tr>`;
}

/* 编辑表单（可维护状态才渲染；装箱单隐藏单价口径，单价恒为 0） */
function tdiFormHtml(priceTip) {
  const editing = TDI.editing;
  const title = editing && editing.id ? `修改明细行 #${editing.id}` : '新增明细行';
  const prices = (TDI.data || {}).docType === '商业发票';
  return `<h4>${title}</h4>
  <div class="pd-hint">${tdiEsc(priceTip)}</div>
  <div class="form-grid" style="grid-template-columns:repeat(4,1fr)">
    <div class="form-item"><label>商品资料（可选）</label>
      <div class="ref-select">
        <input type="text" id="f_ProductId_search" placeholder="输入商品编码 / 名称搜索..." autocomplete="off" oninput="searchRef(this, 'ProductId', 'product')">
        <input type="hidden" id="f_ProductId" value="">
        <div class="ref-dropdown" id="f_ProductId_dd"></div>
      </div>
      <div class="text-muted" style="font-size:12px">选择商品后由服务端写入商品快照；清除引用即按人工录入文本保存</div>
    </div>
    <div class="form-item"><label>商品编码</label><input id="f_ProductCode" maxlength="50" placeholder="与中文名称至少填一项"></div>
    <div class="form-item"><label>商品中文名称</label><input id="f_ProductNameCn" maxlength="200"></div>
    <div class="form-item"><label>商品英文名称</label><input id="f_ProductNameEn" maxlength="200"></div>
    <div class="form-item"><label>规格型号</label><input id="f_Spec" maxlength="200"></div>
    <div class="form-item"><label>数量（&gt; 0，最多 4 位小数）</label><input id="f_Quantity" type="number" step="0.0001"></div>
    <div class="form-item"><label>单位</label><input id="f_Unit" maxlength="20"></div>
    ${prices
      ? '<div class="form-item"><label>单价（最多 4 位小数；金额服务端计算）</label><input id="f_UnitPrice" type="number" step="0.0001"></div>'
      : '<div class="form-item"><label>单价</label><input id="f_UnitPrice" type="number" value="0" disabled><div class="text-muted" style="font-size:12px">装箱单不含价格：单价恒为 0</div></div>'}
    <div class="form-item"><label>箱数（可选，留空即未登记）</label><input id="f_PackageCount" type="number" step="1"></div>
    <div class="form-item"><label>净重 kg（可选）</label><input id="f_NetWeight" type="number" step="0.0001"></div>
    <div class="form-item"><label>毛重 kg（可选，不得小于净重）</label><input id="f_GrossWeight" type="number" step="0.0001"></div>
    <div class="form-item"><label>行序（留空由服务端追加）</label><input id="f_LineOrder" type="number" step="1"></div>
    <div class="form-item"><label>行备注</label><input id="f_Remark" maxlength="500"></div>
  </div>
  <div style="margin:8px 0">
    <button class="btn btn-primary btn-sm" onclick="tdiSaveForm()">${editing && editing.id ? '保存修改' : '新增明细行'}</button>
    <button class="btn btn-neutral btn-sm" onclick="tdiClearProductRef()">清除商品引用</button>
    <button class="btn btn-neutral btn-sm" onclick="tdiCancelForm()">取消编辑</button>
  </div>`;
}

/* 表单填充（编辑既有行时；商品引用未变化时商品文本来自已登记快照，服务端不会刷新） */
function tdiFillForm(row) {
  const set = (id, value) => { const el = document.getElementById(id); if (el) el.value = value === null || value === undefined ? '' : value; };
  set('f_ProductId', row.productId > 0 ? row.productId : '');
  set('f_ProductId_search', row.productId > 0
    ? [row.productCode, row.productNameCn].filter(x => x && String(x).trim()).join(' / ')
    : (row.productNameCn || row.productCode || ''));
  set('f_ProductCode', row.productCode);
  set('f_ProductNameCn', row.productNameCn);
  set('f_ProductNameEn', row.productNameEn);
  set('f_Spec', row.spec);
  set('f_Quantity', row.quantity);
  set('f_Unit', row.unit);
  set('f_UnitPrice', row.unitPrice);
  set('f_PackageCount', row.packageCount);
  set('f_NetWeight', row.netWeight);
  set('f_GrossWeight', row.grossWeight);
  set('f_LineOrder', row.lineNo);
  set('f_Remark', row.remark);
  tdiApplyDraft(row);
}

/* 表单字段清单（提交 / 草稿共用；与后端 TradeDocumentItemRules 字段一一对应） */
const TDI_FORM_FIELDS = ['f_ProductId', 'f_ProductCode', 'f_ProductNameCn', 'f_ProductNameEn', 'f_Spec',
  'f_Quantity', 'f_Unit', 'f_UnitPrice', 'f_PackageCount', 'f_NetWeight', 'f_GrossWeight', 'f_LineOrder', 'f_Remark'];

/* 采集当前编辑行尚未提交的表单输入（只用于成功刷新时保留用户输入，不改变任何业务口径） */
function tdiDraftForm() {
  if (!tdiEditingId()) return null;
  const values = {};
  for (const id of TDI_FORM_FIELDS) values[id] = tdiValue(id);
  return { id: tdiEditingId(), values };
}

/* 渲染后回填草稿：仅当仍在编辑同一行时生效（绝不用旧草稿覆盖另一个编辑目标） */
function tdiApplyDraft(row) {
  const draft = TDI.editingDraft;
  if (!draft || !row || Number(draft.id) !== Number(row.id)) return;
  for (const id of TDI_FORM_FIELDS) {
    const el = document.getElementById(id);
    if (el) el.value = draft.values[id] === null || draft.values[id] === undefined ? '' : draft.values[id];
  }
}

/* 清除商品引用：改为人工录入文本行（不会改写任何历史快照，只影响本次提交值） */
function tdiClearProductRef() {
  const hidden = document.getElementById('f_ProductId');
  const search = document.getElementById('f_ProductId_search');
  if (hidden) hidden.value = '';
  if (search) search.value = '';
  toast('已清除商品引用：本行将按人工录入的商品文本保存');
}

/* 读取表单文本 / 数字（空串 = 未填写 → null，交由服务端按「未登记」处理，绝不自动补 0） */
function tdiValue(id) { const el = document.getElementById(id); return el ? String(el.value ?? '').trim() : ''; }

function tdiNumberOrNull(id) {
  const raw = tdiValue(id);
  if (!raw) return null;
  const value = Number(raw);
  return Number.isFinite(value) ? value : NaN;
}

/* 提交表单：只提交数量 / 单价等行内容 —— 金额与币种一律由服务端计算与取用。
   单证 Id / 行 Id 在发起请求前从当前已加载文档捕获；异步等待后不再读取全局状态，陈旧响应一律丢弃。 */
async function tdiSaveForm() {
  if (!TDI.docId || !TDI.open || !TDI.data) { toast('请先打开单证明细行对话框', 'error'); return; }
  const ctx = tdiCapture();
  const editing = TDI.editing;
  const itemId = editing && editing.id ? Number(editing.id) : 0;

  const quantity = tdiNumberOrNull('f_Quantity');
  if (quantity === null || Number.isNaN(quantity) || quantity <= 0) {
    toast('请填写大于 0 的数量（最多 4 位小数）', 'error'); return;
  }

  const unitPrice = tdiNumberOrNull('f_UnitPrice');
  if (unitPrice !== null && (Number.isNaN(unitPrice) || unitPrice < 0)) {
    toast('单价必须是不小于 0 的数字（或留空表示 0）', 'error'); return;
  }

  for (const field of [['f_PackageCount', '箱数'], ['f_NetWeight', '净重'], ['f_GrossWeight', '毛重'], ['f_LineOrder', '行序']]) {
    const value = tdiNumberOrNull(field[0]);
    if (value !== null && Number.isNaN(value)) {
      toast(`${field[1]}必须填写数字（留空表示未登记，不会记为 0）`, 'error'); return;
    }
  }

  /* 单飞闸门：同一时刻只允许一次提交，重复点击不再产生第二个请求 */
  const token = tdiBeginMutation(ctx, 'save', itemId);
  if (!token) { toast('明细行正在提交，请稍候再试', 'error'); return; }

  const payload = {
    productId: Number(tdiValue('f_ProductId') || 0),
    productCode: tdiValue('f_ProductCode'),
    productNameCn: tdiValue('f_ProductNameCn'),
    productNameEn: tdiValue('f_ProductNameEn'),
    spec: tdiValue('f_Spec'),
    quantity,
    unit: tdiValue('f_Unit'),
    unitPrice: unitPrice === null ? 0 : unitPrice,
    packageCount: tdiNumberOrNull('f_PackageCount'),
    netWeight: tdiNumberOrNull('f_NetWeight'),
    grossWeight: tdiNumberOrNull('f_GrossWeight'),
    lineOrder: tdiNumberOrNull('f_LineOrder'),
    remark: tdiValue('f_Remark')
  };

  /* URL / 方法 / payload 全部取自捕获上下文（单证 Id 与行 Id 绝不来自 await 之后的全局状态） */
  const url = itemId ? `${TDI_API}/items/${itemId}` : `${TDI_API}/${ctx.docId}/items`;
  const method = itemId ? 'PUT' : 'POST';

  try {
    await api(url, method, payload);
    /* 陈旧成功（已切换文档 / 已关闭）：不提示成功、不清除编辑、不触发刷新 */
    if (!tdiIsCurrent(ctx)) return;
    toast('明细行已保存（行金额由服务端计算；未改写商品资料与来源单据）');
    if (!TDI.editing || tdiEditingId(TDI.editing) === itemId) {
      TDI.editing = null;
      TDI.editingGen = 0;
      TDI.editingDraft = null;
    } else {
      /* 用户在请求期间改指了另一行：保留其尚未提交的输入，刷新渲染时回填 */
      TDI.editingDraft = tdiDraftForm();
    }
    await tdiReload();
  } catch (err) {
    /* 失败：保留编辑身份与表单输入（不重渲染）；陈旧失败同样不提示 */
    if (!tdiIsCurrent(ctx)) return;
    toast(err.message, 'error');
  } finally { tdiEndMutation(token); }
}

/* 进入修改：把既有行载入表单（未改指商品时不刷新商品快照） */
function tdiEditItem(id) {
  const row = tdiFindItem(id);
  if (!row) { toast('明细行不存在或已被删除', 'error'); return; }
  TDI.editing = row;
  TDI.editingGen = TDI.gen;   /* 绑定编辑身份到当前对话框代际 */
  TDI.editingDraft = null;    /* 改指另一行：旧草稿不再适用 */
  tdiRender();
}

/* 取消编辑：回到新增状态 */
function tdiCancelForm() {
  TDI.editing = null;
  TDI.editingGen = 0;
  TDI.editingDraft = null;
  tdiRender();
}

/* 显式删除一行（仅在准备状态由服务端允许；已提交 / 已使用的单证会被服务端拒绝）。
   确认前按当前已加载清单校验行归属并捕获单证 / 行 Id；确认后与异步等待后都不再读取全局状态。 */
async function tdiDeleteItem(id) {
  const itemId = Number(id) || 0;
  const ctx = tdiCapture();
  const row = itemId ? tdiFindItem(itemId) : null;
  if (!row) { toast('明细行不存在或已被删除', 'error'); return; }
  const label = `行序 ${row.lineNo}（${row.productNameCn || row.productCode || '未填商品'}）`;
  if (!confirm(`确认删除明细 ${label}？\n\n只在单证处于准备状态（待制作 / 已制作）时允许删除；删除后该行不再出现在清单中（历史审计字段保留），已提交客户 / 已使用的单证会被服务端拒绝。`)) return;
  /* 确认期间若已切换单证 / 关闭对话框：绝不按旧上下文发请求 */
  if (!tdiIsCurrent(ctx)) return;
  const token = tdiBeginMutation(ctx, 'delete', itemId);
  if (!token) { toast('明细行正在提交，请稍候再试', 'error'); return; }
  try {
    await api(`${TDI_API}/items/${itemId}`, 'DELETE');
    /* 陈旧成功（已切换文档 / 已关闭）：不提示成功、不清除编辑、不触发刷新 */
    if (!tdiIsCurrent(ctx)) return;
    toast('明细行已删除（仅准备状态允许；已提交 / 已使用的单证不可删改）');
    if (!TDI.editing || tdiEditingId(TDI.editing) === itemId) {
      TDI.editing = null;
      TDI.editingGen = 0;
      TDI.editingDraft = null;
    } else {
      TDI.editingDraft = tdiDraftForm();
    }
    await tdiReload();
  } catch (err) {
    if (!tdiIsCurrent(ctx)) return;
    toast(err.message, 'error');
  } finally { tdiEndMutation(token); }
}

/* Node 单测导出（浏览器中 module 为 undefined，自动跳过） */
if (typeof module !== 'undefined' && module.exports) {
  module.exports = {
    TDI_API,
    manageTradeDocumentItems, tdiReload, tdiRender, tdiRowHtml, tdiFormHtml, tdiFillForm,
    tdiSaveForm, tdiEditItem, tdiCancelForm, tdiDeleteItem, tdiClearProductRef,
    tdiValue, tdiNumberOrNull, tdiMoney, tdiNum,
    tdiOnClose, tdiClose, tdiEnsureCloseHook, tdiCapture, tdiIsCurrent, tdiFindItem,
    tdiEditingId, tdiBeginMutation, tdiEndMutation, tdiDraftForm, tdiApplyDraft, tdiSnapshot,
    TDI_FORM_FIELDS
  };
}
