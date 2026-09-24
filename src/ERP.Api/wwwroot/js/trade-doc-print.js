/* ============ 单证中心（doc-center）单据打印：打印预览 / 直接打印 / 打印设计 ============ */
/* 依赖：app.js（api / toast / fmtMoney / fmtDate / escapeHtml）、crud.js（CURRENT_MODULE_CODE / MODULES）、
         bill-print.js（moduleFieldLabels / normalizeTemplate / ensurePrintStyle / PRINT_STYLE / printPageCss / closeModal）、
         print-design.js（样式设计：列表页工具栏「🎨 打印设计」→ gotoPrintDesign） */

/* 单证中心打印配置：模块编码 / 接口 / 默认可打印字段（顺序 = 未保存打印模板时的打印顺序）。
   字段键与后端打印模型（TradeDocumentPrintSemantics）及 MODULES['doc-center'] 完全同名（camelCase），
   因此「样式设计」勾选的字段顺序（FieldKeys）可直接驱动打印顺序，无需二次映射。 */
const TRADE_DOC_PRINT = {
  code: 'doc-center',
  title: '单证中心',
  api: '/api/trade/documents',
  templateApi: '/api/sys/print-templates',
  noKey: 'docNo',
  fields: ['docNo', 'docType', 'status', 'issueDate', 'customerName', 'salesOrderNo', 'refNo', 'declareNo',
    'amount', 'currency', 'departurePort', 'destinationPort', 'issuedBy', 'copies', 'fileNote', 'remark'],
};

/* 单值打印格式化：日期 / 金额按单据习惯输出，其余原样；空值一律空字符串（不填 0、不猜测） */
function tradeDocPrintValue(key, value) {
  if (value === null || value === undefined || value === '') return '';
  if (key === 'issueDate') return fmtDate(value);
  if (key === 'amount') return fmtMoney(value);
  return String(value);
}

/* 打印字段顺序：打印模板的字段顺序优先（与「样式设计」勾选顺序一致），其余按默认配置顺序补齐；
   模板中出现但不属于单证台账打印字段的键（如客户 Id 等非打印字段）直接忽略，
   不猜测、不回退到其他单据的字段口径。 */
function tradeDocPrintFieldKeys(tpl) {
  const configured = TRADE_DOC_PRINT.fields;
  let ordered = [];
  if (tpl && tpl.FieldKeys) {
    try { ordered = (JSON.parse(tpl.FieldKeys) || []).filter(k => configured.includes(k)); }
    catch (e) { ordered = []; }
  }
  const keys = ordered.concat(configured.filter(k => !ordered.includes(k)));
  /* 模板关闭「打印备注」时，字段表里的备注列一并去掉 */
  return (tpl && tpl.ShowRemark === false) ? keys.filter(k => k !== 'remark') : keys;
}

/* 字段表数据：值只取打印接口返回的台账字段（Available=false 的字段渲染空白，不推测、不回查档案） */
function tradeDocPrintFieldRows(doc, tpl) {
  const labels = moduleFieldLabels(MODULES[TRADE_DOC_PRINT.code]) || {};
  const fromApi = {};
  (Array.isArray(doc.fields) ? doc.fields : []).forEach(f => { if (f && f.key) fromApi[f.key] = f; });
  return tradeDocPrintFieldKeys(tpl).map(key => {
    const field = fromApi[key];
    const raw = field ? field.value : doc[key];
    const available = field ? field.available === true : !(raw === null || raw === undefined || raw === '');
    return {
      key,
      label: (field && field.label) || labels[key] || key,
      available,
      text: available ? tradeDocPrintValue(key, raw) : '',
    };
  });
}

/* ============ 明细行快照（ERP-052）：打印接口返回的 detailColumns / detailLines / detailTotals ============
   口径：列由后端按单证类型适配（商业发票 → 单价与行金额；装箱单 → 箱数与净重 / 毛重）；
   未登记值（null / undefined）渲染为空白，**绝不**显示为 0；行金额是服务端计算值，前端不做重算与汇率换算；
   所有文本一律经 escapeHtml 输出为纯文本（不会被当成标记执行）。 */
function tradeDocLineCell(line, column) {
  const raw = line ? line[column.key] : null;
  if (raw === null || raw === undefined || raw === '') return '';
  return column.isMonetary ? fmtMoney(raw) : String(raw);
}

/* 明细表 + 行合计说明（无明细行或类型不支持明细行时返回空串，老单证照常打印表头字段） */
function tradeDocPrintDetailHtml(doc) {
  const columns = Array.isArray(doc.detailColumns) ? doc.detailColumns : [];
  const lines = Array.isArray(doc.detailLines) ? doc.detailLines : [];
  if (doc.hasDetailLines !== true || columns.length === 0) return '';

  const head = columns
    .map(c => `<th${c.isNumeric ? ' class="num"' : ''}>${escapeHtml(c.label)}</th>`).join('');
  const body = lines.map(line =>
    `<tr>${columns.map(c => `<td${c.isNumeric ? ' class="num"' : ''}>${escapeHtml(tradeDocLineCell(line, c))}</td>`).join('')}</tr>`
  ).join('');

  const totals = doc.detailTotals || {};
  const parts = [];
  const amounts = (totals.amountByCurrency || [])
    .map(t => `${t.currency} ${t.amountText}（${t.lineCount} 行）`).join('　');
  if (amounts) parts.push(`行金额合计（按币种分开，不做汇率换算）：${amounts}`);
  if (totals.packageCountTotal !== null && totals.packageCountTotal !== undefined)
    parts.push(`箱数合计：${totals.packageCountTotal}（${totals.packageCountRecordedLines} 行登记）`);
  if (totals.netWeightTotal !== null && totals.netWeightTotal !== undefined)
    parts.push(`净重合计：${totals.netWeightTotal} kg（${totals.weightRecordedLines} 行登记）`);
  if (totals.grossWeightTotal !== null && totals.grossWeightTotal !== undefined)
    parts.push(`毛重合计：${totals.grossWeightTotal} kg（${totals.weightRecordedLines} 行登记）`);
  if (doc.detailLinesTruncated === true) parts.push('明细行超过单次读取上限：以上合计只是部分合计');

  const rules = [doc.detailRuleText, totals.missingEvidenceText].filter(t => t).map(escapeHtml).join('　');
  return `<table class="print-details"><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table>
    ${parts.length ? `<div class="print-line-totals">${escapeHtml(parts.join('　｜　'))}</div>` : ''}
    ${rules ? `<div class="print-remark">${rules}</div>` : ''}`;
}

/* 生成单证打印 HTML（公司抬头 + 标题 + 字段表 + 明细表 + 签署栏 + 页脚）。
   明细行只按打印接口返回的只读投影渲染：没有明细行（老单证 / 类型不支持）时输出纯表头打印件。 */
function buildTradeDocPrintHtml(tpl, doc) {
  const rows = tradeDocPrintFieldRows(doc, tpl);
  const cells = rows.map(r =>
    `<td class="lbl">${escapeHtml(r.label)}</td><td class="val">${escapeHtml(r.text)}</td>`);
  let fieldRows = '';
  for (let i = 0; i < cells.length; i += 4) fieldRows += `<tr>${cells.slice(i, i + 4).join('')}</tr>`;

  const company = tpl.ShowCompanyHeader === false ? '' : `<div class="print-company">${escapeHtml(tpl.CompanyName || '')}</div>
    ${(tpl.CompanyAddress || tpl.CompanyPhone) ? `<div class="print-company-sub">${escapeHtml(tpl.CompanyAddress || '')}${tpl.CompanyAddress && tpl.CompanyPhone ? '　' : ''}${tpl.CompanyPhone ? '电话：' + escapeHtml(tpl.CompanyPhone) : ''}</div>` : ''}`;

  const pvFont = Number(tpl.FontSize) || 12;
  const pvPad = tpl.CellPadding === undefined || tpl.CellPadding === null ? 6 : Number(tpl.CellPadding);
  const pvFamily = "'" + String(tpl.FontFamily || 'Microsoft YaHei').replace(/['"]/g, '') + "'";
  const ppVars = [
    `--pp-font:${pvFont}px`,
    `--pp-font-sm:${Math.max(8, pvFont - 1)}px`,
    `--pp-family:${pvFamily}`,
    `--pp-text:${tpl.TextColor || '#000'}`,
    `--pp-title-size:${Number(tpl.TitleFontSize) || 16}px`,
    `--pp-title-color:${tpl.TitleColor || '#000'}`,
    `--pp-title-align:${tpl.TitleAlign || 'center'}`,
    `--pp-company-size:${Number(tpl.CompanyFontSize) || 18}px`,
    `--pp-company-color:${tpl.CompanyColor || '#000'}`,
    `--pp-head-bg:${tpl.HeaderBgColor || '#f2f2f2'}`,
    `--pp-border:${tpl.BorderColor || '#999'}`,
    `--pp-border-style:${tpl.BorderStyle || 'solid'}`,
    `--pp-pad:${pvPad}px`,
    `--pp-pad-x:${Math.max(pvPad, 4)}px`,
    tpl.RowHeight ? `--pp-row-h:${Number(tpl.RowHeight)}px` : '',
  ].filter(Boolean).join(';');

  return `<div class="print-page" style="${ppVars}">
    ${company}
    <div class="print-title">${escapeHtml(tpl.Title || doc.title || TRADE_DOC_PRINT.title)}</div>
    <div class="print-meta">
      <span>单证编号：${escapeHtml(doc.docNo || '')}</span>
      <span>单证类型：${escapeHtml(doc.docType || '')}</span>
      <span>状态：${escapeHtml(doc.status || '')}</span>
      <span>打印时间：${new Date().toLocaleString('zh-CN')}</span>
    </div>
    ${fieldRows ? `<table class="print-fields"><tbody>${fieldRows}</tbody></table>` : ''}
    ${tradeDocPrintDetailHtml(doc)}
    <div class="print-sign"><span>制单人：____________</span><span>审核人：____________</span><span>客户签收：____________</span></div>
    <div class="print-footer"><span>${escapeHtml(tpl.FooterText || '')}</span><span>共 1 页</span></div>
  </div>`;
}


/* 读取单证打印上下文：打印接口返回的台账字段投影（含 Available 标记） + 打印模板 */
async function fetchTradeDocPrintContext(oid) {
  const doc = await api(`${TRADE_DOC_PRINT.api}/${oid}/print`);
  const template = await api(`${TRADE_DOC_PRINT.templateApi}/${encodeURIComponent(TRADE_DOC_PRINT.code)}`);
  return { oid, doc, template: normalizeTemplate(template) };
}

/* 打印预览：弹窗中查看打印效果，并提供「打印」按钮 */
async function previewTradeDocPrint(oid) {
  if (!oid) { toast('请先保存单证后再打印', 'error'); return; }
  ensurePrintStyle();
  try {
    const ctx = await fetchTradeDocPrintContext(oid);
    window.__tradeDocPrintContext = ctx;
    const html = buildTradeDocPrintHtml(ctx.template, ctx.doc);
    const modal = document.getElementById('modal');
    modal.innerHTML = `<div class="modal modal-lg" style="width:920px;max-width:96vw">
      <h3>🖨 打印预览 - ${escapeHtml(ctx.doc.docType || TRADE_DOC_PRINT.title)} ${escapeHtml(ctx.doc.docNo || '')}</h3>
      <div style="max-height:62vh;overflow:auto;border:1px solid #e2e8f0;border-radius:6px;background:#f8fafc">${html}</div>
      <p class="text-muted" style="margin:8px 0 0;line-height:1.7">
        空白字段表示单证台账中未登记该值：系统不会按客户 / 柜号 / 订单号反查或推测单证上的值。
        商品明细行按行序输出单证制作当时的行快照（商业发票含服务端计算的单价与行金额、装箱单含箱数与净重 / 毛重）；
        未登记的箱数 / 净重 / 毛重留空显示，<b>不会</b>写成 0，也不会按商品资料或自由文本推断。
      </p>
      <div class="modal-footer">
        <button class="btn btn-neutral" onclick="closeModal()">关闭</button>
        <button class="btn btn-primary" onclick="printTradeDoc()">🖨 打印</button>
      </div>
    </div>`;
    modal.style.display = 'flex';
  } catch (err) { toast(err.message, 'error'); }
}

/* 预览弹窗中的「打印」：与直接打印共用同一份打印数据与模板 */
function printTradeDoc() {
  const ctx = window.__tradeDocPrintContext;
  if (!ctx) { toast('请先打开打印预览', 'error'); return; }
  openTradeDocPrintWindow(ctx);
}

/* 直接打印：不先弹预览，按打印模板直接输出并调起浏览器打印 */
async function printTradeDocDirect(oid) {
  if (!oid) { toast('请先保存单证后再打印', 'error'); return; }
  ensurePrintStyle();
  try {
    openTradeDocPrintWindow(await fetchTradeDocPrintContext(oid));
  } catch (err) { toast(err.message, 'error'); }
}

/* 新窗口输出并调起浏览器打印（支持模板纸张与字号） */
function openTradeDocPrintWindow(ctx) {
  const win = window.open('', '_blank');
  if (!win) { toast('浏览器拦截了打印窗口，请允许弹出窗口后重试', 'error'); return; }
  const tpl = ctx.template || {};
  win.document.open();
  win.document.write(`<!DOCTYPE html><html lang="zh-CN"><head><meta charset="UTF-8">
    <title>${escapeHtml(tpl.Title || ctx.doc.docType || TRADE_DOC_PRINT.title)}</title>
    <style>${printPageCss(tpl.PaperSize)} ${PRINT_STYLE} body{margin:0;background:#fff}</style>
    </head><body>${buildTradeDocPrintHtml(tpl, ctx.doc)}</body></html>`);
  win.document.close();
  win.focus();
  setTimeout(() => { try { win.print(); } catch (e) { /* 用户取消打印 */ } }, 300);
}

/* 打印设计入口：单证中心与基础资料共用列表页工具栏的「🎨 打印设计」按钮
   （crud.js → openModulePrintDesign → gotoPrintDesign(CURRENT_MODULE_CODE)，当前模块码 = doc-center），
   因此这里不重复注册按钮。doc-center 在「样式设计」左侧清单中只登记一次
   （pdDocGroups 排除已登记在 BILL_CONFIG 的同名单据），保存后的 doc-center 模板即本文件的打印模板。 */
