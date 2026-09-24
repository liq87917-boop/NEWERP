/* ============ 单证中心：由销售订单 / 装柜清单生成单证 + Excel 导出（ERP-019） ============ */
/* 依赖：app.js（api / toast / fmtDate / fmtMoney / navigate / openNavGroup / CURRENT_MODULE_CODE）、
         crud.js（CURRENT_MODULE / openForm / closeModal / setImageValue）、
         bill-edit.js（escapeHtml / REF_APIS）、sales-pi.js（gotoModulePage）、
         modules.js（MODULES['doc-center']，页面还需加载 modules-doc.js / modules-doc2.js 提供来源模块） */

/* 来源模块编码 -> 生成单证接口与业务名称（「生成单证 / 预填单证」行操作按当前页面判定来源） */
const TRADE_DOC_SOURCES = {
  'sales-order': {
    label: '销售订单', api: '/api/sales-orders', noKey: 'orderNo',
    defaultDocTypes: ['商业发票', '装箱单'],
  },
  'loading-list': {
    label: '装柜清单', api: '/api/container/loading-lists', noKey: 'loadingListNo',
    defaultDocTypes: ['装箱单'],
  },
};

/* 单证类型 -> 编号前缀（与后端 TradeDocumentGeneration.DocNoPrefixes 同口径，仅用于对话框提示） */
const TRADE_DOC_NO_PREFIX = {
  '报关单': 'CD', '装箱单': 'PL', '商业发票': 'CI', '形式发票': 'PI',
  '产地证': 'CO', '提单': 'BL', '订舱确认': 'BK', '外汇核销单': 'VR', '其他': 'TD',
};

/* 生成前明细行预览列（ERP-052，仅界面展示口径；落库 / 打印 / 导出的权威列由后端按单证类型给出）。
   显示文本字段（*Text）在未登记时为空串 —— 预览里空白就是「未登记」，绝不显示 0。 */
const TRADE_DOC_LINE_PREVIEW_COLUMNS = {
  '商业发票': [
    ['lineNo', '序号'], ['productCode', '商品编码'], ['productNameCn', '商品中文名称'], ['spec', '规格'],
    ['quantityText', '数量'], ['unit', '单位'], ['unitPriceText', '单价'], ['lineAmountText', '行金额'],
    ['remark', '行备注'],
  ],
  '装箱单': [
    ['lineNo', '序号'], ['productCode', '商品编码'], ['productNameCn', '商品中文名称'], ['spec', '规格'],
    ['quantityText', '数量'], ['unit', '单位'], ['packageCountText', '箱数'], ['netWeightText', '净重kg'],
    ['grossWeightText', '毛重kg'], ['remark', '行备注'],
  ],
};

/* 单次预览最多展示的行数（有界；其余行只报总数，避免对话框过大） */
const TRADE_DOC_LINE_PREVIEW_LIMIT = 8;

/* 明细行快照预览（ERP-052）：按单证类型分组展示预填结果里的行快照，未登记值留空 */
function tradeDocLinePreviewHtml(data, kinds) {
  const lines = Array.isArray(data.linePreviews) ? data.linePreviews : [];
  if (!lines.length) return '';

  const blocks = kinds.map(docType => {
    const group = lines.filter(l => l.docType === docType);
    if (!group.length) return '';
    const columns = TRADE_DOC_LINE_PREVIEW_COLUMNS[docType];
    if (!columns) return '';
    const head = columns.map(c => `<th>${escapeHtml(c[1])}</th>`).join('');
    const body = group.slice(0, TRADE_DOC_LINE_PREVIEW_LIMIT).map(line =>
      `<tr>${columns.map(c => `<td>${escapeHtml(line[c[0]])}</td>`).join('')}</tr>`).join('');
    const more = group.length > TRADE_DOC_LINE_PREVIEW_LIMIT
      ? `<tr><td colspan="${columns.length}" class="text-muted">…共 ${group.length} 行，仅预览前 ${TRADE_DOC_LINE_PREVIEW_LIMIT} 行</td></tr>`
      : '';
    return `<div style="margin:8px 0 12px">
      <b>${escapeHtml(docType)} · 明细行快照预览（${group.length} 行，按来源明细顺序）</b>
      <div class="table-wrap" style="max-height:26vh;overflow:auto">
        <table><thead><tr>${head}</tr></thead><tbody>${body}${more}</tbody></table>
      </div>
    </div>`;
  }).join('');

  if (!blocks) return '';
  const hints = [data.lineSummaryText, data.lineEvidenceText, data.lineRuleText]
    .filter(t => t).map(t => escapeHtml(t)).join('<br>');
  return `<div style="margin-top:10px">
    <div class="text-muted" style="line-height:1.7">
      明细行快照只取来源单据的权威明细值：未登记的箱数 / 净重 / 毛重留空（不写成 0、不按商品资料或自由文本推断）；
      商业发票行金额由服务端按币种精度计算，装箱单不含价格口径；生成后行快照保持生成当时的值，来源单据之后改动不会刷新它。
    </div>
    ${blocks}
    ${hints ? `<div class="text-muted" style="line-height:1.7">${hints}</div>` : ''}
  </div>`;
}

/* 当前生成对话框上下文（来源 + 预填结果），由对话框内按钮共用 */
let TRADE_DOC_CTX = null;

/* 行操作：按当前来源单据直接生成所选单证（销售订单 / 装柜清单共用一套入口） */
function generateTradeDocsFromSource(id) { return openTradeDocDialog(id, 'generate'); }

/* 行操作：按当前来源单据带入单证草稿到「单证中心 → 新增」表单（不落库，可人工编辑后再保存） */
function prefillTradeDocFromSource(id) { return openTradeDocDialog(id, 'prefill'); }

/* 打开生成 / 预填对话框：先取预填结果（未落库草稿 + 已生成类型），再按结果渲染 */
async function openTradeDocDialog(id, mode) {
  const source = TRADE_DOC_SOURCES[CURRENT_MODULE_CODE];
  if (!source) { toast('当前模块不支持生成单证', 'error'); return; }
  if (!id) { toast('请先保存单据后再生成单证', 'error'); return; }
  try {
    const data = await api(`${source.api}/${id}/trade-documents/prefill`);
    TRADE_DOC_CTX = { mode: mode, source: source, sourceCode: CURRENT_MODULE_CODE, sourceId: id, data: data };
    renderTradeDocDialog();
  } catch (err) { toast(err.message, 'error'); }
}

/* 渲染对话框：单证类型勾选（已生成的置灰）+ 映射预览 + 生成 / 带入预填 / 关闭 */
function renderTradeDocDialog() {
  const ctx = TRADE_DOC_CTX;
  if (!ctx) return;
  const data = ctx.data || {};
  const drafts = data.documents || [];
  const generated = data.generatedDocTypes || [];
  const defaults = (data.defaultDocTypes && data.defaultDocTypes.length)
    ? data.defaultDocTypes : (ctx.source.defaultDocTypes || []);
  const kinds = (data.supportedDocTypes && data.supportedDocTypes.length)
    ? data.supportedDocTypes : drafts.map(d => d.docType);
  const sourceNo = data.sourceNo || '';

  const lineCounts = data.lineCounts || {};
  const rows = kinds.map(t => {
    const draft = drafts.find(d => d.docType === t) || {};
    const done = generated.indexOf(t) >= 0;
    const checked = !done && defaults.indexOf(t) >= 0;
    const noHint = TRADE_DOC_NO_PREFIX[t] ? `${TRADE_DOC_NO_PREFIX[t]}-${sourceNo}` : '';
    const lineCount = Number(lineCounts[t] || 0);
    return `<tr>
      <td><label><input type="checkbox" class="td-kind" value="${escapeHtml(t)}"
          ${checked ? 'checked' : ''} ${done ? 'disabled' : ''} onchange="tradeDocSelectionChanged()"> ${escapeHtml(t)}</label></td>
      <td>${escapeHtml(draft.docNo || noHint)}</td>
      <td>${escapeHtml(draft.customerName || '')}</td>
      <td class="text-right">${draft.amount ? escapeHtml(draft.currency || '') + ' ' + fmtMoney(draft.amount) : ''}</td>
      <td>${escapeHtml(draft.destinationPort || '')}</td>
      <td>${escapeHtml(draft.issuedBy || '')}</td>
      <td class="text-right">${draft.copies ? draft.copies : ''}</td>
      <td class="text-right">${lineCount ? lineCount + ' 行' : '—'}</td>
      <td>${done ? '<span class="status status-success">已生成</span>' : '<span class="status status-info">可生成</span>'}</td>
    </tr>`;
  }).join('');

  const modal = document.getElementById('modal');
  modal.innerHTML = `<div class="modal modal-lg" style="width:1180px;max-width:96vw">
    <h3>📋 由${escapeHtml(ctx.source.label)} ${escapeHtml(sourceNo)} 生成单证</h3>
    <p class="text-muted" style="margin:8px 0 12px">
      勾选需要的单证类型后可直接生成（同一来源 + 同一类型只允许一张，重复点击会被拒绝）；生成时会按来源明细
      在<b>同一事务</b>内写入明细行快照（商业发票含服务端计算的行金额，装箱单含箱数与净重 / 毛重）。
      或选一个类型「带入预填」到单证中心新增表单，人工核对后再保存（状态默认「待制作」）。
      ${data.sourceRefNo ? `柜号 / 订舱号：<b>${escapeHtml(data.sourceRefNo)}</b>。` : ''}
    </p>
    <div class="table-wrap" style="max-height:36vh;overflow:auto">
      <table><thead><tr>
        <th style="width:150px">单证类型</th><th>单证编号（预填）</th><th>客户</th>
        <th class="text-right">金额</th><th>目的港</th><th>制作人 / 出证机构</th>
        <th class="text-right">份数</th><th class="text-right">明细行</th><th>状态</th>
      </tr></thead><tbody>${rows}</tbody></table>
    </div>
    ${tradeDocLinePreviewHtml(data, kinds)}
    <div class="modal-footer">
      <span class="text-muted" id="td-selection-hint">请选择要生成的单证类型</span>
      <button class="btn btn-neutral" onclick="prefillSelectedTradeDoc()" title="把选中的单证类型带入单证中心新增表单（不落库，可编辑后保存）">📝 带入预填</button>
      <button class="btn btn-primary" onclick="generateSelectedTradeDocs()" title="按选中的单证类型直接生成单证中心记录">📋 生成所选单证</button>
      <button class="btn btn-neutral" onclick="closeModal()">关闭</button>
    </div>
  </div>`;
  modal.style.display = 'flex';
  tradeDocSelectionChanged();
}

/* 已勾选的单证类型（保持对话框内的展示顺序） */
function tradeDocSelectedTypes() {
  return Array.from(document.querySelectorAll('#modal .td-kind:checked')).map(el => el.value);
}

/* 勾选变化时更新提示文案（「带入预填」只支持单个类型） */
function tradeDocSelectionChanged() {
  const picked = tradeDocSelectedTypes();
  const hint = document.getElementById('td-selection-hint');
  if (!hint) return;
  hint.textContent = picked.length === 0 ? '请选择要生成的单证类型'
    : (picked.length === 1 ? `已选择：${picked[0]}`
      : `已选择 ${picked.length} 项：${picked.join('、')}（「带入预填」需只选 1 项，可直接「生成所选单证」）`);
}

/* 直接生成所选单证：服务端落库并守卫重复生成；成功后跳到单证中心查看结果 */
async function generateSelectedTradeDocs() {
  const ctx = TRADE_DOC_CTX;
  if (!ctx) return;
  const picked = tradeDocSelectedTypes();
  if (picked.length === 0) { toast('请先勾选需要生成的单证类型', 'error'); return; }
  if (!confirm(`确认由该${ctx.source.label}生成 ${picked.length} 张单证？\n\n同一来源的同一单证类型只允许一张；生成时会按来源明细在**同一事务**内写入明细行快照（商业发票含服务端计算的行金额，装箱单含箱数与净重 / 毛重；未登记的量留空，不写成 0）。`)) return;
  try {
    const result = await api(`${ctx.source.api}/${ctx.sourceId}/trade-documents`, 'POST', { docTypes: picked });
    const items = (result && result.documents) || [];
    const lineTotal = Number((result && result.totalLineCount) || 0);
    const detail = items.filter(d => Number(d.lineCount || 0) > 0)
      .map(d => `${d.docNo} ${d.lineCount} 行`).join('、');
    toast(`已生成 ${items.length} 张单证：${items.map(d => d.docNo).join('、')}`
      + (lineTotal > 0 ? `（明细行快照共 ${lineTotal} 行：${detail}）` : '（来源没有明细行，未带入明细行）'));
    closeModal();
    TRADE_DOC_CTX = null;
    const mod = MODULES['doc-center'];
    if (mod) gotoModulePage('doc-center', mod.title);   // 跳到单证中心，用户立刻看到新生成的单证
  } catch (err) { toast(err.message, 'error'); }
}

/* 带入预填：把选中的单一类型草稿写入「单证中心 → 新增」表单（不落库，可编辑后保存） */
async function prefillSelectedTradeDoc() {
  const ctx = TRADE_DOC_CTX;
  if (!ctx) return;
  const picked = tradeDocSelectedTypes();
  if (picked.length !== 1) { toast('「带入预填」请只勾选 1 个单证类型', 'error'); return; }
  const docType = picked[0];
  const draft = (ctx.data.documents || []).find(d => d.docType === docType);
  if (!draft) { toast(`未找到「${docType}」的预填数据`, 'error'); return; }
  const mod = MODULES['doc-center'];
  if (!mod) { toast('单证中心模块未加载', 'error'); return; }
  closeModal();
  gotoModulePage('doc-center', mod.title);   // 切到单证中心页面（同步菜单高亮与标签页）
  openForm();
  await fillTradeDocForm(draft);
  toast(`已带入「${docType}」草稿（状态：待制作），请核对后保存`);
}

/* 把带入的单证草稿写入当前新增表单：字段按类型回填、客户引用同步补齐名称 */
async function fillTradeDocForm(doc) {
  const mod = MODULES['doc-center'];
  if (!mod || !doc) throw new Error('单证带入数据为空');
  (mod.fields || []).forEach(f => {
    const el = document.getElementById('f_' + f.key);
    if (!el) return;
    const v = doc[f.key];
    if (f.type === 'date') { el.value = v ? fmtDate(v) : ''; return; }
    if (f.type === 'image') { setImageValue(f.key, v || ''); return; }
    el.value = (v === null || v === undefined) ? '' : v;
  });
  /* 客户（引用字段）：与 crud.js loadIntoForm 同口径，异步补齐搜索框名称 */
  for (const f of (mod.fields || []).filter(x => x.type === 'ref')) {
    const box = document.getElementById('f_' + f.key + '_search');
    const rid = doc[f.key];
    if (!box) continue;
    if (!rid) { box.value = ''; continue; }
    try {
      const ref = REF_APIS[f.ref];
      const d = await api(`${ref.api}/${rid}`);
      box.value = d[ref.nameKey] || '';
    } catch (e) { /* 名称查询失败不影响带入 */ }
  }
}

/* ============ 单证中心 Excel 导出（列表筛选导出 / 单条导出） ============ */

/* 单证中心模块声明里的选项（单证类型 / 状态），保证与页面渲染同一份口径 */
function tradeDocOptionsOf(fieldKey) {
  const mod = MODULES['doc-center'];
  const field = ((mod && mod.fields) || []).find(f => f.key === fieldKey);
  return (field && field.options) || [];
}

/* 列表导出对话框：单证类型 / 状态 / 出具日期区间 / 关键字，导出与页面同源的筛选结果 */
function openTradeDocExportDialog() {
  const docTypeOpts = tradeDocOptionsOf('docType')
    .map(o => `<option value="${o.value}">${o.label}</option>`).join('');
  const statusOpts = tradeDocOptionsOf('status')
    .map(o => `<option value="${o.value}">${o.label}</option>`).join('');
  const defStart = new Date(Date.now() - 90 * 86400000).toISOString().slice(0, 10);
  const defEnd = new Date().toISOString().slice(0, 10);
  const keyword = (typeof CURRENT_KEYWORD === 'string' ? CURRENT_KEYWORD : '');

  const modal = document.getElementById('modal');
  modal.innerHTML = `<div class="modal modal-lg" style="width:760px;max-width:96vw">
    <h3>📤 单证中心导出 Excel</h3>
    <p class="text-muted" style="margin:8px 0 16px">
      按条件导出单证台账（列含单证编号 / 类型 / 出具日期 / 客户 / 金额币种 / 港口 / 份数 / 状态 / 来源留痕）；
      留空表示不限，日期区间按「出具 / 签发日期」过滤。勾选「明细行布局」时改为**每行一条明细记录 + 行合计**
      （商业发票输出单价与服务端计算的行金额；装箱单输出箱数与净重 / 毛重，未登记留空而不是 0；
      老单证没有明细行时照常导出一行并标明「无明细行」）。
    </p>
    <div class="form-grid">
      <div class="form-item"><label>单证类型</label>
        <select id="td-ex-doctype"><option value="">全部类型</option>${docTypeOpts}</select></div>
      <div class="form-item"><label>状态</label>
        <select id="td-ex-status"><option value="">全部状态</option>${statusOpts}</select></div>
      <div class="form-item"><label>开始日期</label>
        <input type="date" id="td-ex-start" value="${defStart}"></div>
      <div class="form-item"><label>结束日期</label>
        <input type="date" id="td-ex-end" value="${defEnd}"></div>
      <div class="form-item full"><label>关键字（单证编号 / 客户 / 柜号 / 订单号 / 报关单号）</label>
        <input type="text" id="td-ex-keyword" value="${escapeHtml(keyword)}" placeholder="按单证编号、客户、柜号、关联订单号、报关单号模糊搜索"></div>
      <div class="form-item full"><label>导出布局</label>
        <label><input type="checkbox" id="td-ex-lines"> 按明细行导出（每行一条记录 + 行金额按币种分开的合计 + 箱数 / 重量合计）</label></div>
    </div>
    <div class="modal-footer">
      <span class="text-muted">导出为 xlsx 文件（与销售订单 / 采购订单导出一致的列头与样式）</span>
      <button class="btn btn-primary" onclick="doExportTradeDocs()">📥 导出 Excel</button>
      <button class="btn btn-neutral" onclick="closeModal()">关闭</button>
    </div>
  </div>`;
  modal.style.display = 'flex';
}

/* 按对话框条件导出列表 */
async function doExportTradeDocs() {
  const docType = (document.getElementById('td-ex-doctype') || {}).value || '';
  const status = (document.getElementById('td-ex-status') || {}).value || '';
  const start = (document.getElementById('td-ex-start') || {}).value || '';
  const end = (document.getElementById('td-ex-end') || {}).value || '';
  const keyword = ((document.getElementById('td-ex-keyword') || {}).value || '').trim();
  const linesLayout = !!((document.getElementById('td-ex-lines') || {}).checked);

  const qs = new URLSearchParams();
  if (docType) qs.set('docType', docType);
  if (status) qs.set('status', status);
  if (start) qs.set('start', start + 'T00:00:00');
  if (end) qs.set('end', end + 'T23:59:59');
  if (keyword) qs.set('keyword', keyword);
  if (linesLayout) qs.set('layout', 'lines');

  const dateStr = new Date().toISOString().slice(0, 10).replace(/-/g, '');
  const fileName = linesLayout ? `单证明细行_${dateStr}.xlsx` : `单证台账_${dateStr}.xlsx`;
  await downloadTradeDocExcel(`/api/trade/documents/export-excel?${qs.toString()}`, fileName);
}

/* 行操作：按明细行导出单条单证（每行一条记录 + 行合计；老单证无明细行时照常导出一行） */
function exportTradeDocumentLines(id) {
  const row = (window.__moduleRows || []).find(r => String(r.id) === String(id)) || {};
  const base = row.docNo ? String(row.docNo).replace(/[\\/:*?"<>|]/g, '_') : String(id);
  return downloadTradeDocExcel(
    `/api/trade/documents/export-excel?id=${encodeURIComponent(id)}&layout=lines`,
    `单证明细行_${base}.xlsx`);
}

/* 行操作：导出单条单证（列表已加载行数据时以其单证编号命名文件） */
function exportTradeDocument(id) {
  const row = (window.__moduleRows || []).find(r => String(r.id) === String(id)) || {};
  const base = row.docNo ? String(row.docNo).replace(/[\\/:*?"<>|]/g, '_') : String(id);
  return downloadTradeDocExcel(`/api/trade/documents/export-excel?id=${encodeURIComponent(id)}`, `单证_${base}.xlsx`);
}

/* 下载 xlsx（与 bill-export.js doExport 同一套下载口径） */
async function downloadTradeDocExcel(path, fileName) {
  try {
    const resp = await fetch(path, { headers: { Authorization: 'Bearer ' + TOKEN } });
    if (!resp.ok) {
      let msg = '导出失败';
      try { msg = (await resp.json()).message || msg; } catch (e) { /* 忽略解析失败 */ }
      toast(msg, 'error');
      return;
    }
    const blob = await resp.blob();
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
    toast('导出成功');
  } catch (err) { toast('导出失败：' + err.message, 'error'); }
}
