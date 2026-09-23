/* ============ 单据打印（打印预览 / 打印 / 打印设计）与单据操作日志 ============ */
/* 依赖：app.js（api/toast/fmtMoney/fmtDate）、bill-v2.js（BILL_CONFIG/BILL_CODE/BILL_CODE_MAP） */

/* 明细列中文标签（与存储过程副表字段一致） */
const PRINT_DETAIL_LABELS = {
  ProductId: '商品编码', ProductName: '商品名称', Spec: '规格', Quantity: '数量', Unit: '单位',
  UnitPrice: '单价', Amount: '金额', Cartons: '箱数', Weight: '毛重(kg)', Volume: '体积(m³)', Remark: '备注',
};

/* 单据状态文本（与存储过程一致：1=保存 / 2=已审核 / -1=已作废） */
const PRINT_STATUS_TEXT = { 1: '保存', 2: '已审核', '-1': '已作废' };

/* 纸张规格选项 */
const PAPER_SIZES = [
  { value: 'A4', label: 'A4 纵向' },
  { value: 'A5', label: 'A5 纵向' },
  { value: 'A4-L', label: 'A4 横向' },
  { value: '80mm', label: '80mm 小票' },
];

/* 打印样式（屏幕预览 + 打印输出共用） */
const PRINT_STYLE = `
  .print-page {
    background:#fff; color:var(--pp-text,#000); padding:16px 20px;
    font-family:var(--pp-family,"Microsoft YaHei","SimSun",Arial,sans-serif);
    font-size:var(--pp-font,12px);
  }
  .print-company { text-align:var(--pp-title-align,center); font-size:var(--pp-company-size,18px); font-weight:700; letter-spacing:1px; color:var(--pp-company-color,#000); }
  .print-company-sub { text-align:var(--pp-title-align,center); font-size:var(--pp-font-sm,11px); color:var(--pp-text,#000); opacity:.78; margin-top:2px; }
  .print-title { text-align:var(--pp-title-align,center); font-size:var(--pp-title-size,16px); font-weight:700; margin:10px 0 8px; letter-spacing:2px; color:var(--pp-title-color,#000); }
  .print-meta { display:flex; justify-content:space-between; font-size:var(--pp-font-sm,11px); color:var(--pp-text,#000); border-bottom:1px solid var(--pp-border,#999); padding-bottom:4px; margin-bottom:8px; }
  table.print-fields { width:100%; border-collapse:collapse; font-size:var(--pp-font,12px); }
  table.print-fields td { border:1px var(--pp-border-style,solid) var(--pp-border,#999); padding:var(--pp-pad,4px) var(--pp-pad-x,6px); height:var(--pp-row-h,auto); }
  table.print-fields td.lbl { background:var(--pp-head-bg,#f2f2f2); width:14%; white-space:nowrap; }
  table.print-fields td.val { width:36%; }
  table.print-details { width:100%; border-collapse:collapse; margin-top:10px; font-size:var(--pp-font,12px); }
  table.print-details th { border:1px var(--pp-border-style,solid) var(--pp-border,#999); background:var(--pp-head-bg,#f2f2f2); padding:var(--pp-pad,4px) var(--pp-pad-x,6px); height:var(--pp-row-h,auto); }
  table.print-details td { border:1px var(--pp-border-style,solid) var(--pp-border,#999); padding:var(--pp-pad,4px) var(--pp-pad-x,6px); height:var(--pp-row-h,auto); }
  table.print-details td.num { text-align:right; }
  .print-remark { margin-top:10px; font-size:var(--pp-font,12px); line-height:1.6; }
  .print-sign { margin-top:26px; display:flex; justify-content:space-between; font-size:var(--pp-font,12px); }
  .print-footer { margin-top:14px; border-top:1px dashed var(--pp-border,#999); padding-top:6px; font-size:var(--pp-font-sm,11px); color:var(--pp-text,#000); opacity:.82; display:flex; justify-content:space-between; }
`;

/* 注入打印样式（仅在首次调用时注入） */
function ensurePrintStyle() {
  if (document.getElementById('bill-print-style')) return;
  const style = document.createElement('style');
  style.id = 'bill-print-style';
  style.textContent = PRINT_STYLE;
  document.head.appendChild(style);
}

/* 单据字段中文标签（BILL_CONFIG 优先，基础资料回退到 MODULES 配置） */
function billFieldLabels(code) {
  const cfg = BILL_CONFIG[code];
  if (!cfg) return moduleFieldLabels(MODULES[code]);
  const labels = {};
  (cfg.columns || []).forEach(c => { if (!c.status) labels[c.key] = c.label; });
  (cfg.fields || []).forEach(f => { labels[f.key] = f.label; });
  labels.BillNo = '单据号';
  labels.Status = '单据状态';
  return labels;
}

/* 基础资料字段中文标签（由 MODULES 的列定义与表单字段合并而来） */
function moduleFieldLabels(mod) {
  const labels = {};
  if (!mod) return labels;
  (mod.columns || []).forEach(c => { if (c.type !== 'image') labels[c.key] = c.label; });
  (mod.fields || []).forEach(f => { labels[f.key] = f.label; });
  return labels;
}

/* 打印值格式化：状态 / 日期 / 金额 */
function formatPrintValue(key, value) {
  if (value === null || value === undefined || value === '') return '';
  if (key === 'Status') return PRINT_STATUS_TEXT[value] ?? String(value);
  if (key.endsWith('Date') || key.endsWith('Time')) return fmtDate(value);
  if (['TotalAmount', 'Amount', 'DepositAmount', 'TotalCartons', 'TotalWeight', 'TotalVolume',
    'FreightCost', 'OtherCost', 'CreditLimit', 'DepositRatio', 'ExchangeRate', 'TotalQuantity'].includes(key))
    return fmtMoney(value);
  return String(value);
}

/* 打印模板字段名兼容：后端 JSON 为 camelCase（ASP.NET Core 默认），
   前端历史代码按 PascalCase 取值，这里统一补齐别名，两套写法都可用 */
function normalizeTemplate(t) {
  if (!t) return {};
  const pairs = {
    Id: 'id', BillType: 'billType', TemplateName: 'templateName', Title: 'title',
    CompanyName: 'companyName', CompanyAddress: 'companyAddress', CompanyPhone: 'companyPhone',
    ShowCompanyHeader: 'showCompanyHeader', ShowDetailTable: 'showDetailTable', ShowRemark: 'showRemark',
    PaperSize: 'paperSize', FontSize: 'fontSize', FieldKeys: 'fieldKeys',
    FooterText: 'footerText', IsDefault: 'isDefault',
    LayoutJson: 'layoutJson',
    FontFamily: 'fontFamily', TitleFontSize: 'titleFontSize', TitleColor: 'titleColor', TitleAlign: 'titleAlign',
    CompanyFontSize: 'companyFontSize', CompanyColor: 'companyColor', TextColor: 'textColor',
    HeaderBgColor: 'headerBgColor', BorderColor: 'borderColor', BorderStyle: 'borderStyle',
    RowHeight: 'rowHeight', CellPadding: 'cellPadding',
  };
  const out = Object.assign({}, t);
  Object.keys(pairs).forEach(P => {
    if (out[P] === undefined && t[pairs[P]] !== undefined) out[P] = t[pairs[P]];
  });
  return out;
}

/* 读取单据打印上下文：主表 + 明细 + 打印模板 + 字段标签 */
async function fetchPrintContext(code, oid) {
  const billType = BILL_CODE_MAP[code] || code;
  const detail = await api(`/api/v2/bills/${billType}/${oid}`);
  const template = await api(`/api/sys/print-templates/${encodeURIComponent(code)}`);
  return {
    code, billType, oid,
    main: detail.main || {},
    details: detail.details || [],
    template: normalizeTemplate(template),
    labels: billFieldLabels(code),
    billTitle: (BILL_CONFIG[code] && BILL_CONFIG[code].title) || billType,
  };
}

/* 生成打印 HTML（公司抬头 + 标题 + 主表字段 + 明细 + 备注 + 签署栏 + 页脚） */
function buildPrintHtml(ctx) {
  const tpl = ctx.template || {};
  const labels = ctx.labels;
  const main = ctx.main;

  // 打印字段顺序：模板配置优先，未配置时使用全部列定义
  let keys = [];
  if (tpl.FieldKeys) {
    try { keys = JSON.parse(tpl.FieldKeys) || []; } catch (e) { keys = []; }
  }
  if (!keys.length) keys = Object.keys(labels);

  const fieldCells = keys
    .filter(k => !['Oid', 'BillID'].includes(k))
    .map(k => {
      const label = labels[k] || PRINT_DETAIL_LABELS[k] || k;
      const value = formatPrintValue(k, main[k]);
      return `<td class="lbl">${escapeHtml(label)}</td><td class="val">${escapeHtml(value)}</td>`;
    });

  // 两列排布：每行 4 个单元格（标签 + 值 × 2）
  let fieldRows = '';
  for (let i = 0; i < fieldCells.length; i += 4) {
    fieldRows += `<tr>${fieldCells.slice(i, i + 4).join('')}</tr>`;
  }

  // 明细表
  let detailHtml = '';
  if (tpl.ShowDetailTable !== false && ctx.details.length) {
    const used = Object.keys(PRINT_DETAIL_LABELS)
      .filter(k => ctx.details.some(d => d[k] !== null && d[k] !== undefined && d[k] !== ''));
    const detailKeys = used.length ? used : Object.keys(PRINT_DETAIL_LABELS);
    const head = detailKeys.map(k => `<th>${PRINT_DETAIL_LABELS[k]}</th>`).join('');
    const body = ctx.details.map(d => `<tr>${detailKeys.map(k => {
      const v = d[k];
      const isNum = ['Quantity', 'UnitPrice', 'Amount', 'Cartons', 'Weight', 'Volume'].includes(k);
      return `<td class="${isNum ? 'num' : ''}">${escapeHtml(isNum ? fmtMoney(v) : (v ?? ''))}</td>`;
    }).join('')}</tr>`).join('');
    detailHtml = `<table class="print-details"><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table>`;
  }

  const company = tpl.ShowCompanyHeader === false ? '' : `<div class="print-company">${escapeHtml(tpl.CompanyName || '')}</div>
    ${(tpl.CompanyAddress || tpl.CompanyPhone) ? `<div class="print-company-sub">${escapeHtml(tpl.CompanyAddress || '')}${tpl.CompanyAddress && tpl.CompanyPhone ? '　' : ''}${tpl.CompanyPhone ? '电话：' + escapeHtml(tpl.CompanyPhone) : ''}</div>` : ''}`;

  const remarkHtml = (tpl.ShowRemark !== false && main.Remark)
    ? `<div class="print-remark"><b>备注：</b>${escapeHtml(main.Remark)}</div>` : '';

  // 外观样式变量：由「样式设计」中配置的字体 / 字号 / 颜色 / 单元格尺寸驱动
  const pvFont = Number(tpl.FontSize) || 12;
  const pvPad = tpl.CellPadding === undefined || tpl.CellPadding === null ? 6 : Number(tpl.CellPadding);
  // 注意：字体族必须用「单引号」，否则双引号会提前结束 HTML 的 style="..." 属性，
  //       导致其后的所有样式变量（颜色 / 字号 / 行高…）全部丢失
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
    `--print-font:${pvFont}px`,
  ].filter(Boolean).join(';');

  return `<div class="print-page" style="${ppVars}">
    ${company}
    <div class="print-title">${escapeHtml(tpl.Title || ctx.billTitle)}</div>
    <div class="print-meta">
      <span>单据号：${escapeHtml(main.BillNo || '')}</span>
      <span>打印时间：${new Date().toLocaleString('zh-CN')}</span>
    </div>
    <table class="print-fields"><tbody>${fieldRows}</tbody></table>
    ${detailHtml}
    ${remarkHtml}
    <div class="print-sign"><span>制单人：____________</span><span>审核人：____________</span><span>客户签收：____________</span></div>
    <div class="print-footer"><span>${escapeHtml(tpl.FooterText || '')}</span><span>共 1 页</span></div>
  </div>`;
}

/* 纸张样式（打印窗口使用） */
function printPageCss(paper) {
  switch (paper) {
    case 'A5': return '@page { size: A5 portrait; margin: 10mm; }';
    case 'A4-L': return '@page { size: A4 landscape; margin: 10mm; }';
    case '80mm': return '@page { size: 80mm auto; margin: 4mm; } .print-page { padding:0; }';
    default: return '@page { size: A4 portrait; margin: 12mm; }';
  }
}

/* 打印预览：在弹窗中查看打印效果，并提供「打印」按钮 */
async function previewBillPrint(oid) {
  if (!oid) { toast('请先保存单据后再打印', 'error'); return; }
  ensurePrintStyle();
  try {
    const ctx = await fetchPrintContext(BILL_CODE, oid);
    const html = buildPrintHtml(ctx);
    const modal = document.getElementById('modal');
    modal.innerHTML = `<div class="modal modal-lg" style="width:900px;max-width:96vw">
      <h3>🖨 打印预览 - ${escapeHtml(ctx.billTitle)}</h3>
      <div style="max-height:62vh;overflow:auto;border:1px solid #e2e8f0;border-radius:6px;background:#f8fafc">
        ${html}
      </div>
      <div class="modal-footer">
        <button class="btn btn-neutral" onclick="closeModal()">关闭</button>
        <button class="btn btn-primary" onclick="printBill(${oid})">🖨 打印</button>
      </div>
    </div>`;
    modal.style.display = 'flex';
  } catch (err) { toast(err.message, 'error'); }
}

/* 打印：新窗口输出并调起浏览器打印（支持模板纸张与字号） */
async function printBill(oid) {
  if (!oid) { toast('请先保存单据后再打印', 'error'); return; }
  // 先同步打开窗口，避免异步请求后被浏览器拦截
  const win = window.open('', '_blank');
  if (!win) { toast('浏览器拦截了打印窗口，请允许弹出窗口后重试', 'error'); return; }
  win.document.write('<div style="font-family:sans-serif;padding:20px">正在准备打印内容……</div>');
  try {
    const ctx = await fetchPrintContext(BILL_CODE, oid);
    const html = buildPrintHtml(ctx);
    const tpl = ctx.template || {};
    win.document.open();
    win.document.write(`<!DOCTYPE html><html lang="zh-CN"><head><meta charset="UTF-8">
      <title>${escapeHtml(tpl.Title || ctx.billTitle)}</title>
      <style>${printPageCss(tpl.PaperSize)} ${PRINT_STYLE} body{margin:0;background:#fff}</style>
      </head><body>${html}</body></html>`);
    win.document.close();
    win.focus();
    setTimeout(() => { try { win.print(); } catch (e) { /* 用户取消打印 */ } }, 300);
  } catch (err) {
    win.document.open();
    win.document.write('<div style="font-family:sans-serif;padding:20px;color:#c00">打印内容加载失败：' + escapeHtml(err.message) + '</div>');
    win.document.close();
    toast(err.message, 'error');
  }
}

/* ============ 单据导出 / 导入（Excel） ============ */

/* 导出当前单据类型的 Excel（沿用列表页的关键字与状态筛选） */
async function exportBillExcel() {
  const kwEl = document.getElementById('bill-keyword');
  const stEl = document.getElementById('bill-status');
  const qs = new URLSearchParams();
  if (kwEl && kwEl.value.trim()) qs.set('keyword', kwEl.value.trim());
  if (stEl && stEl.value) qs.set('status', stEl.value);
  const title = (BILL_CONFIG[BILL_CODE] && BILL_CONFIG[BILL_CODE].title) || BILL_CODE;
  try {
    const resp = await fetch(`/api/v2/bills/${BILL_CODE}/export?${qs.toString()}`, {
      headers: { Authorization: 'Bearer ' + TOKEN },
    });
    if (!resp.ok) { toast('导出失败', 'error'); return; }
    downloadBlob(await resp.blob(), `${title}_${new Date().toISOString().slice(0, 10).replace(/-/g, '')}.xlsx`);
    toast('导出成功');
  } catch (err) { toast('导出失败：' + err.message, 'error'); }
}

/* 下载通用文件（Blob） */
function downloadBlob(blob, fileName) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}

/* 下载单据导入模板（withSample=true 时附带一行示例数据） */
async function downloadBillImportTemplate(withSample) {
  const title = (BILL_CONFIG[BILL_CODE] && BILL_CONFIG[BILL_CODE].title) || BILL_CODE;
  const url = `/api/v2/bills/${BILL_CODE}/import-template${withSample ? '?withSample=true' : ''}`;
  try {
    const resp = await fetch(url, { headers: { Authorization: 'Bearer ' + TOKEN } });
    if (!resp.ok) { toast('模板下载失败', 'error'); return; }
    downloadBlob(await resp.blob(), `${title}_导入${withSample ? '示例' : '模板'}_${new Date().toISOString().slice(0, 10).replace(/-/g, '')}.xlsx`);
    toast(withSample ? '示例文件已下载，可直接按此格式填写' : '模板已下载，请按表头填写后导入');
  } catch (err) { toast('模板下载失败：' + err.message, 'error'); }
}

/* 导入单据：弹出文件选择框并提示模板下载 */
function openBillImportDialog() {
  const title = (BILL_CONFIG[BILL_CODE] && BILL_CONFIG[BILL_CODE].title) || BILL_CODE;
  const modal = document.getElementById('modal');
  modal.innerHTML = `<div class="modal" style="width:560px">
    <h3>📥 导入 ${escapeHtml(title)}</h3>
    <p class="text-muted" style="margin:8px 0 14px;line-height:1.7">
      1. 先<button class="btn btn-neutral btn-sm" onclick="downloadBillImportTemplate(false)">下载导入模板</button>
         <button class="btn btn-neutral btn-sm" onclick="downloadBillImportTemplate(true)">下载示例文件</button><br>
      2. 按模板中文表头逐行填写数据（单据号、状态由系统自动生成，无需填写）<br>
      3. 选择填好的 Excel 文件后点击「开始导入」，单次不超过 1000 行
    </p>
    <div class="form-item"><label>Excel 文件（.xlsx）</label><input type="file" id="bill-import-file" accept=".xlsx"></div>
    <div class="modal-footer">
      <button class="btn btn-neutral" onclick="closeModal()">取消</button>
      <button class="btn btn-primary" onclick="doImportBillExcel()">开始导入</button>
    </div>
  </div>`;
  modal.style.display = 'flex';
}

/* 执行导入并展示结果 */
async function doImportBillExcel() {
  const input = document.getElementById('bill-import-file');
  if (!input || !input.files || !input.files[0]) { toast('请先选择 Excel 文件', 'error'); return; }
  const fd = new FormData();
  fd.append('file', input.files[0]);
  try {
    toast('正在导入，请稍候……');
    const result = await uploadFile(`/api/v2/bills/${BILL_CODE}/import`, fd);
    showImportResult(result);
    if (typeof loadBills === 'function' && document.getElementById('bill-keyword')) loadBills();
  } catch (err) { toast(err.message, 'error'); }
}

/* 导入结果弹窗（含失败原因明细） */
function showImportResult(result) {
  const errors = result.errors || [];
  const rows = errors.map(e => `<tr><td>${e.row}</td><td>${escapeHtml(e.message)}</td></tr>`).join('');
  const modal = document.getElementById('modal');
  modal.innerHTML = `<div class="modal" style="width:660px">
    <h3>📥 导入结果</h3>
    <p style="margin:10px 0">共 ${result.total} 行：<span class="status status-success">成功 ${result.success}</span>
      ${result.failed ? `<span class="status status-danger" style="margin-left:8px">失败 ${result.failed}</span>` : ''}</p>
    ${rows ? `<div class="table-wrap" style="max-height:320px;overflow:auto"><table>
        <thead><tr><th style="width:90px">Excel 行号</th><th>失败原因</th></tr></thead><tbody>${rows}</tbody></table></div>` : ''}
    <div class="modal-footer">
      <button class="btn btn-neutral" onclick="closeModal()">关闭</button>
    </div>
  </div>`;
  modal.style.display = 'flex';
}

/* ============ 打印设计（打印模板配置） ============ */

/* 当前设计中的字段列表 [{key,label,checked}] 与模板对象 */
let PRINT_DESIGN = null;

/* 解析模板中的字段顺序 JSON */
function parseFieldKeys(json) {
  if (!json) return [];
  try { return JSON.parse(json) || []; } catch (e) { return []; }
}

/* 打开打印设计弹窗 */
async function openPrintDesign(code) {
  const target = code || BILL_CODE;
  ensurePrintStyle();
  try {
    const tpl = await api(`/api/sys/print-templates/${encodeURIComponent(target)}`);
    const labels = billFieldLabels(target);
    const order = parseFieldKeys(tpl.FieldKeys);
    const allKeys = order.concat(Object.keys(labels).filter(k => !order.includes(k)));
    PRINT_DESIGN = {
      code: target,
      template: normalizeTemplate(tpl),
      items: allKeys.map(k => ({
        key: k,
        label: labels[k] || PRINT_DETAIL_LABELS[k] || k,
        checked: order.length === 0 || order.includes(k),
      })),
    };
    renderPrintDesign();
  } catch (err) { toast(err.message, 'error'); }
}

/* 渲染打印设计界面 */
function renderPrintDesign() {
  const d = PRINT_DESIGN;
  const tpl = d.template || {};
  const paperOpts = PAPER_SIZES.map(p => `<option value="${p.value}" ${tpl.PaperSize === p.value ? 'selected' : ''}>${p.label}</option>`).join('');
  const fieldRows = d.items.map((item, index) => `<tr>
      <td style="width:40px"><input type="checkbox" ${item.checked ? 'checked' : ''} onchange="toggleDesignField(${index}, this.checked)"></td>
      <td>${escapeHtml(item.label)}</td>
      <td style="width:100px;white-space:nowrap">
        <button class="btn btn-neutral btn-sm" ${index === 0 ? 'disabled' : ''} onclick="moveDesignField(${index}, -1)">↑</button>
        <button class="btn btn-neutral btn-sm" ${index === d.items.length - 1 ? 'disabled' : ''} onclick="moveDesignField(${index}, 1)">↓</button>
      </td></tr>`).join('');

  document.getElementById('modal').innerHTML = `<div class="modal modal-lg" style="width:880px;max-width:96vw">
    <h3>🎨 打印设计 - ${escapeHtml(tpl.Title || d.code)}</h3>
    <div style="display:flex;gap:18px;margin-top:12px">
      <div style="flex:1">
        <div class="form-item"><label>模板名称</label><input type="text" id="pd-name" value="${escapeHtml(tpl.TemplateName || '默认模板')}"></div>
        <div class="form-item"><label>打印标题</label><input type="text" id="pd-title" value="${escapeHtml(tpl.Title || '')}"></div>
        <div class="form-item"><label>公司抬头</label><input type="text" id="pd-company" value="${escapeHtml(tpl.CompanyName || '')}"></div>
        <div class="form-item"><label>公司地址</label><input type="text" id="pd-address" value="${escapeHtml(tpl.CompanyAddress || '')}"></div>
        <div class="form-item"><label>联系电话</label><input type="text" id="pd-phone" value="${escapeHtml(tpl.CompanyPhone || '')}"></div>
        <div class="form-item"><label>纸张规格</label><select id="pd-paper">${paperOpts}</select></div>
        <div class="form-item"><label>正文字号(px)</label><input type="number" id="pd-font" min="8" max="24" value="${tpl.FontSize || 12}"></div>
        <div class="form-item"><label>页脚文本</label><input type="text" id="pd-footer" value="${escapeHtml(tpl.FooterText || '')}"></div>
        <div class="form-item full" style="display:flex;gap:16px;align-items:center;flex-wrap:wrap">
          <label style="margin:0"><input type="checkbox" id="pd-show-company" ${tpl.ShowCompanyHeader === false ? '' : 'checked'}> 显示公司抬头</label>
          <label style="margin:0"><input type="checkbox" id="pd-show-detail" ${tpl.ShowDetailTable === false ? '' : 'checked'}> 打印明细</label>
          <label style="margin:0"><input type="checkbox" id="pd-show-remark" ${tpl.ShowRemark === false ? '' : 'checked'}> 打印备注</label>
          <label style="margin:0"><input type="checkbox" id="pd-default" ${tpl.IsDefault ? 'checked' : ''}> 设为默认</label>
        </div>
      </div>
      <div style="flex:1">
        <div style="font-weight:600;margin-bottom:6px">打印字段（勾选并调整显示顺序）</div>
        <div class="table-wrap" style="max-height:430px;overflow:auto">
          <table><thead><tr><th></th><th>字段</th><th>排序</th></tr></thead><tbody>${fieldRows}</tbody></table>
        </div>
      </div>
    </div>
    <div class="modal-footer">
      <button class="btn btn-neutral" onclick="closeModal()">取消</button>
      <button class="btn btn-neutral" onclick="previewPrintDesign()">效果预览</button>
      <button class="btn btn-primary" onclick="savePrintDesign()">保存模板</button>
    </div>
  </div>`;
  document.getElementById('modal').style.display = 'flex';
}

/* 勾选/取消打印字段 */
function toggleDesignField(index, checked) { PRINT_DESIGN.items[index].checked = checked; }

/* 调整打印字段顺序 */
function moveDesignField(index, delta) {
  const items = PRINT_DESIGN.items;
  const target = index + delta;
  if (target < 0 || target >= items.length) return;
  const tmp = items[index];
  items[index] = items[target];
  items[target] = tmp;
  renderPrintDesign();
}

/* 收集设计表单为模板对象 */
function collectPrintDesign() {
  const tpl = PRINT_DESIGN.template || {};
  const keys = PRINT_DESIGN.items.filter(i => i.checked).map(i => i.key);
  return {
    Id: tpl.Id || 0,
    BillType: PRINT_DESIGN.code,
    TemplateName: document.getElementById('pd-name').value.trim() || '默认模板',
    Title: document.getElementById('pd-title').value.trim(),
    CompanyName: document.getElementById('pd-company').value.trim(),
    CompanyAddress: document.getElementById('pd-address').value.trim(),
    CompanyPhone: document.getElementById('pd-phone').value.trim(),
    PaperSize: document.getElementById('pd-paper').value,
    FontSize: Number(document.getElementById('pd-font').value) || 12,
    FooterText: document.getElementById('pd-footer').value.trim(),
    ShowCompanyHeader: document.getElementById('pd-show-company').checked,
    ShowDetailTable: document.getElementById('pd-show-detail').checked,
    ShowRemark: document.getElementById('pd-show-remark').checked,
    IsDefault: document.getElementById('pd-default').checked,
    FieldKeys: JSON.stringify(keys),
  };
}

/* 保存打印模板 */
async function savePrintDesign() {
  try {
    const payload = collectPrintDesign();
    const saved = await api('/api/sys/print-templates', 'POST', payload);
    PRINT_DESIGN.template = saved;
    toast('打印模板已保存');
    closeModal();
  } catch (err) { toast(err.message, 'error'); }
}

/* 效果预览：用当前列表首行数据生成样例单据 */
function previewPrintDesign() {
  try {
    const payload = collectPrintDesign();
    const code = PRINT_DESIGN.code;
    const sample = window.__billSampleRow || {};
    const ctx = {
      code,
      billTitle: BILL_CONFIG[code] ? BILL_CONFIG[code].title : code,
      labels: billFieldLabels(code),
      template: payload,
      main: Object.assign({ BillNo: sample.BillNo || '（示例单据号）', Status: sample.Status ?? 1 }, sample),
      details: window.__billSampleDetails || [],
    };
    const html = buildPrintHtml(ctx);
    const box = document.createElement('div');
    box.id = 'print-design-preview';
    box.style.cssText = 'position:fixed;inset:0;background:rgba(15,23,42,.55);z-index:3000;display:flex;align-items:center;justify-content:center';
    box.innerHTML = `<div class="card" style="width:840px;max-width:94vw;max-height:88vh;overflow:auto">
        <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:10px">
          <b>打印效果预览</b><button class="btn btn-neutral btn-sm" onclick="closePrintDesignPreview()">关闭</button>
        </div>${html}</div>`;
    document.body.appendChild(box);
  } catch (err) { toast(err.message, 'error'); }
}

/* 关闭打印效果预览 */
function closePrintDesignPreview() {
  const box = document.getElementById('print-design-preview');
  if (box) box.remove();
}

/* ============ 单据操作日志（按单据追溯） ============ */

/* 查看单据操作日志（oid 传 0 表示查看该单据类型的全部操作日志） */
async function showBillLogs(oid, billNo) {
  try {
    const data = await api(`/api/v2/bills/${BILL_CODE}/${oid || 0}/logs?page=1&pageSize=50`);
    const items = data.items || [];
    const rows = items.map(l => `<tr>
        <td>${escapeHtml(l.createdAt ? String(l.createdAt).replace('T', ' ').slice(0, 19) : '')}</td>
        <td>${escapeHtml(l.userName)}</td>
        <td>${escapeHtml(l.action)}</td>
        <td>${escapeHtml(l.billNo)}</td>
        <td>${escapeHtml(l.ipAddress)}</td>
      </tr>`).join('');
    const title = (BILL_CONFIG[BILL_CODE] && BILL_CONFIG[BILL_CODE].title) || BILL_CODE;
    document.getElementById('modal').innerHTML = `<div class="modal modal-lg" style="width:860px;max-width:96vw">
      <h3>📜 操作日志 - ${escapeHtml(billNo || title)}</h3>
      <div style="margin:10px 0">
        <input type="text" id="log-keyword" placeholder="按单据号 / 操作人 / 动作搜索全部日志" style="width:340px" onkeydown="if(event.key==='Enter')searchAllBillLogs()">
        <button class="btn btn-neutral" onclick="searchAllBillLogs()">搜索</button>
      </div>
      <div class="table-wrap" style="max-height:420px;overflow:auto">
        ${items.length ? `<table><thead><tr><th>时间</th><th>操作人</th><th>动作</th><th>单据号</th><th>IP</th></tr></thead><tbody>${rows}</tbody></table>`
      : '<div class="empty">暂无操作日志</div>'}
      </div>
      <div class="modal-footer"><button class="btn btn-neutral" onclick="closeModal()">关闭</button></div>
    </div>`;
    document.getElementById('modal').style.display = 'flex';
  } catch (err) { toast(err.message, 'error'); }
}

/* 在全量日志中按关键字搜索（单据号 / 操作人 / 动作 / 路径） */
async function searchAllBillLogs() {
  const keyword = document.getElementById('log-keyword').value.trim();
  if (!keyword) { toast('请输入搜索关键字', 'error'); return; }
  try {
    const data = await api(`/api/sys/logs?page=1&pageSize=100&keyword=${encodeURIComponent(keyword)}`);
    const items = data.items || [];
    const rows = items.map(l => `<tr>
        <td>${escapeHtml(l.createdAt ? String(l.createdAt).replace('T', ' ').slice(0, 19) : '')}</td>
        <td>${escapeHtml(l.userName)}</td>
        <td>${escapeHtml(l.module)}</td>
        <td>${escapeHtml(l.action)}</td>
        <td>${escapeHtml(l.billNo)}</td>
        <td>${escapeHtml(l.path)}</td>
      </tr>`).join('');
    document.getElementById('modal').innerHTML = `<div class="modal modal-lg" style="width:980px;max-width:96vw">
      <h3>📜 操作日志 - 关键字「${escapeHtml(keyword)}」（共 ${data.total} 条）</h3>
      <div class="table-wrap" style="max-height:440px;overflow:auto">
        ${items.length ? `<table><thead><tr><th>时间</th><th>操作人</th><th>模块</th><th>动作</th><th>单据号</th><th>路径</th></tr></thead><tbody>${rows}</tbody></table>`
      : '<div class="empty">未找到匹配的日志</div>'}
      </div>
      <div class="modal-footer"><button class="btn btn-neutral" onclick="closeModal()">关闭</button></div>
    </div>`;
    document.getElementById('modal').style.display = 'flex';
  } catch (err) { toast(err.message, 'error'); }
}

