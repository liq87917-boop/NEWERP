/* ============ 报价单 / 形式发票 PI（EF 主子表单据）业务动作与单据打印 ============ */
/* 依赖：app.js（api / toast / fmtMoney / fmtDate / escapeHtml）、crud.js（CURRENT_MODULE_CODE / CURRENT_LOADER）、
         bill-print.js（moduleFieldLabels / normalizeTemplate / ensurePrintStyle / PRINT_STYLE / printPageCss / closeModal） */

/* 模块编码 -> 单据打印配置（主表字段打印顺序 + 接口地址） */
const SALES_DOC_PRINT = {
  quotation: {
    title: '报价单', api: '/api/sales/quotations', noKey: 'quotationNo',
    fields: ['quotationNo', 'quotationDate', 'validUntil', 'customerName', 'contactPerson', 'contactPhone',
      'contactEmail', 'inquiryNo', 'tradeTerms', 'portOfLoading', 'portOfDestination', 'paymentTerms',
      'leadTime', 'currency', 'exchangeRate', 'totalAmount', 'totalAmountCny', 'salesmanName'],
  },
  'proforma-invoice': {
    title: '形式发票 PI', api: '/api/sales/proforma-invoices', noKey: 'piNo',
    fields: ['piNo', 'piDate', 'quotationNo', 'customerName', 'contactPerson', 'contactPhone', 'contactEmail',
      'consignee', 'notifyParty', 'tradeTerms', 'portOfLoading', 'portOfDestination', 'paymentTerms',
      'shippingTerms', 'leadTime', 'currency', 'exchangeRate', 'totalAmount', 'totalAmountCny',
      'depositRatio', 'depositAmount', 'salesmanName'],
  },
  /* ERP-008：销售订单（外销合同）打印——外贸合同条款与来源追溯字段一并输出 */
  'sales-order': {
    title: '销售订单（外销合同）', api: '/api/sales-orders', noKey: 'orderNo',
    detailColumns: 'order',
    fields: ['orderNo', 'orderDate', 'customerPoNo', 'contractNo', 'tradeTerms', 'destinationPort',
      'consignee', 'notifyParty', 'shippingMarks', 'sourceQuotationNo', 'sourcePiNo', 'exportMode', 'businessNature',
      'commissionRatio', 'splitShipment', 'inspectionRequirement', 'packagingRequirement',
      'currency', 'exchangeRate', 'totalAmount', 'depositRatio', 'depositAmount',
      'paymentTerms', 'deliveryDate', 'shippingMethod', 'remark'],
  },
  'purchase-order': {
    title: '采购订单', api: '/api/purchase-orders', noKey: 'orderNo',
    detailColumns: 'order',
    fields: ['orderNo', 'orderDate', 'supplierId', 'contractNo', 'owningCustomerName',
      'owningSalesOrderNo', 'advanceOnBehalf', 'supplierConfirmedDate', 'taxRate', 'taxIncluded',
      'arrivalProgress', 'qcStatus', 'settlementProgress', 'currency', 'exchangeRate', 'totalAmount',
      'paymentTerms', 'deliveryDate', 'remark'],
  },
};

/* 明细打印列（行号 / 商品 / 规格 / 数量 / 单价 / 金额 / 起订量 / 备注） */
const SALES_DOC_DETAIL_COLUMNS = [
  { key: 'sortNo', label: '行号', width: '44px' },
  { key: 'productCode', label: '商品编码', width: '110px' },
  { key: 'productName', label: '商品名称', width: '170px' },
  { key: 'spec', label: '规格', width: '120px' },
  { key: 'unit', label: '单位', width: '60px' },
  { key: 'quantity', label: '数量', width: '80px', num: true },
  { key: 'unitPrice', label: '单价', width: '80px', num: true },
  { key: 'amount', label: '金额', width: '100px', num: true },
  { key: 'moq', label: '起订量', width: '80px' },
  { key: 'remark', label: '备注', width: '120px' },
];

/* ERP-008：销售订单 / 采购订单明细打印列（订单明细无商品编码与起订量，改为商品名称 / 规格 / 单位 / 数量 / 单价 / 金额 / 备注） */
const SALES_ORDER_DETAIL_COLUMNS = [
  { key: 'productId', label: '商品ID', width: '70px' },
  { key: 'productName', label: '商品名称', width: '190px' },
  { key: 'spec', label: '规格', width: '140px' },
  { key: 'unit', label: '单位', width: '60px' },
  { key: 'quantity', label: '数量', width: '80px', num: true },
  { key: 'unitPrice', label: '单价', width: '80px', num: true },
  { key: 'amount', label: '金额', width: '100px', num: true },
  { key: 'remark', label: '备注', width: '120px' },
];
const SALES_DOC_DETAIL_SETS = { order: SALES_ORDER_DETAIL_COLUMNS };

/* 取单据的明细打印列（未声明 detailColumns 的沿用报价单 / PI 明细列） */
function salesDocDetailColumns(cfg) {
  return (cfg && cfg.detailColumns && SALES_DOC_DETAIL_SETS[cfg.detailColumns]) || SALES_DOC_DETAIL_COLUMNS;
}

/* 单行文本打印格式化：日期 / 金额 / 布尔按单据习惯输出，其余原样 */
function salesDocPrintValue(key, value) {
  if (value === null || value === undefined || value === '') return '';
  if (['quotationDate', 'piDate', 'validUntil', 'orderDate', 'deliveryDate', 'supplierConfirmedDate'].includes(key))
    return fmtDate(value);
  if (['totalAmount', 'totalAmountCny', 'depositAmount'].includes(key)) return fmtMoney(value);
  if (['splitShipment', 'advanceOnBehalf', 'taxIncluded'].includes(key))
    return (value === true || String(value).toLowerCase() === 'true') ? '是' : '否';
  return String(value);
}

/* 多行文本转义后保留换行（银行信息 / 唛头 / 联系人等） */
function salesDocMultiline(value) {
  return escapeHtml(value ?? '').replace(/\r?\n/g, '<br>');
}

/* 生成报价单 / PI 的打印 HTML（公司抬头 + 标题 + 主表字段 + 明细 + 合计 + 唛头/银行信息 + 备注 + 签署栏） */
function buildSalesDocPrintHtml(code, cfg, tpl, doc) {
  const labels = moduleFieldLabels(MODULES[code]) || {};
  const fieldCells = (cfg.fields || []).map(key =>
    `<td class="lbl">${escapeHtml(labels[key] || key)}</td><td class="val">${escapeHtml(salesDocPrintValue(key, doc[key]))}</td>`);
  let fieldRows = '';
  for (let i = 0; i < fieldCells.length; i += 4) fieldRows += `<tr>${fieldCells.slice(i, i + 4).join('')}</tr>`;

  const details = doc.details || [];
  let detailHtml = '';
  if (tpl.ShowDetailTable !== false) {
    const detailColumns = salesDocDetailColumns(cfg);
    const head = detailColumns.map(c => `<th style="width:${c.width}">${c.label}</th>`).join('');
    const body = details.map(d => `<tr>${detailColumns.map(c => {
      const raw = d[c.key];
      const text = c.num ? fmtMoney(raw) : (raw === null || raw === undefined ? '' : String(raw));
      return `<td class="${c.num ? 'num' : ''}">${escapeHtml(text)}</td>`;
    }).join('')}</tr>`).join('');
    detailHtml = `<table class="print-details"><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table>`;
  }

  // 合计：总额（原币 / 折人民币）；PI 同时给出定金比例与定金金额
  const currency = escapeHtml(String(doc.currency ?? ''));
  const depositText = (doc.depositAmount || doc.depositRatio)
    ? `　｜　定金比例 ${escapeHtml(String(doc.depositRatio || 0))}%，定金金额 ${currency} ${fmtMoney(doc.depositAmount)}`
    : '';
  const totals = `<div class="print-remark"><b>合计：</b>${currency} ${fmtMoney(doc.totalAmount)}${doc.totalAmountCny ? ` ／ 折人民币 ${fmtMoney(doc.totalAmountCny)}` : ''}${depositText}</div>`;

  // 销售订单专属：唛头与验货 / 包装要求（合同条款固定区）
  let orderBlock = '';
  if (code === 'sales-order' && (doc.shippingMarks || doc.inspectionRequirement || doc.packagingRequirement)) {
    orderBlock = `<div class="print-remark">
      ${doc.shippingMarks ? `<div><b>唛头 Shipping Marks：</b>${salesDocMultiline(doc.shippingMarks)}</div>` : ''}
      ${doc.inspectionRequirement ? `<div style="margin-top:4px"><b>验货要求：</b>${salesDocMultiline(doc.inspectionRequirement)}</div>` : ''}
      ${doc.packagingRequirement ? `<div style="margin-top:4px"><b>包装要求：</b>${salesDocMultiline(doc.packagingRequirement)}</div>` : ''}
    </div>`;
  }

  // PI 专属：唛头与银行信息（多行文本，打印模板固定区）
  let piBlock = '';
  if (code === 'proforma-invoice' && (doc.shippingMarks || doc.bankInfo)) {
    piBlock = `<div class="print-remark">
      ${doc.shippingMarks ? `<div><b>唛头 Shipping Marks：</b>${salesDocMultiline(doc.shippingMarks)}</div>` : ''}
      ${doc.bankInfo ? `<div style="margin-top:4px"><b>银行信息 Bank Details：</b>${salesDocMultiline(doc.bankInfo)}</div>` : ''}
    </div>`;
  }

  const company = tpl.ShowCompanyHeader === false ? '' : `<div class="print-company">${escapeHtml(tpl.CompanyName || '')}</div>
    ${(tpl.CompanyAddress || tpl.CompanyPhone) ? `<div class="print-company-sub">${escapeHtml(tpl.CompanyAddress || '')}${tpl.CompanyAddress && tpl.CompanyPhone ? '　' : ''}${tpl.CompanyPhone ? '电话：' + escapeHtml(tpl.CompanyPhone) : ''}</div>` : ''}`;
  const remarkHtml = (tpl.ShowRemark !== false && doc.remark)
    ? `<div class="print-remark"><b>备注：</b>${salesDocMultiline(doc.remark)}</div>` : '';

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
    <div class="print-title">${escapeHtml(tpl.Title || cfg.title)}</div>
    <div class="print-meta">
      <span>单据号：${escapeHtml(doc[cfg.noKey] || '')}</span>
      <span>打印时间：${new Date().toLocaleString('zh-CN')}</span>
    </div>
    <table class="print-fields"><tbody>${fieldRows}</tbody></table>
    ${detailHtml}
    ${totals}
    ${piBlock}
    ${orderBlock}
    ${remarkHtml}
    <div class="print-sign"><span>制单人：____________</span><span>审核人：____________</span><span>客户确认：____________</span></div>
    <div class="print-footer"><span>${escapeHtml(tpl.FooterText || '')}</span><span>共 1 页</span></div>
  </div>`;
}

/* ============ 单据动作（PENDING: 草稿 / APPROVED: 已审核 / CANCELLED: 已作废） ============ */

/* 通用状态动作：审核 / 销审 / 作废（报价单与 PI 共用接口风格） */
async function salesDocAction(id, action, okMessage) {
  const cfg = SALES_DOC_PRINT[CURRENT_MODULE_CODE];
  if (!cfg) { toast('当前模块不支持该操作', 'error'); return; }
  try {
    await api(`${cfg.api}/${id}/${action}`, 'POST');
    toast(okMessage);
    if (CURRENT_LOADER) CURRENT_LOADER();
  } catch (err) { toast(err.message, 'error'); }
}

/* PI 审核（草稿 → 已审核） */
function piApprove(id) { return salesDocAction(id, 'approve', 'PI 已审核'); }

/* 报价单审核（草稿 → 已审核；审核后才能转 PI） */
function quotationApprove(id) { return salesDocAction(id, 'approve', '报价单已审核'); }

/* 报价单销审（退回草稿，可继续修改） */
function quotationUnaudit(id) { return salesDocAction(id, 'unaudit', '已销审，可继续修改'); }

/* PI 销审（已审核 → 草稿，可继续修改） */
function piUnaudit(id) { return salesDocAction(id, 'unaudit', '已销审，可继续修改'); }

/* PI 作废（已转销售订单不可作废） */
function piVoid(id) {
  if (!confirm('确认作废该 PI？作废后不可恢复。')) return;
  return salesDocAction(id, 'void', 'PI 已作废');
}

/* 报价单转 PI：复制主表与明细并回填来源报价单，原报价单改为「已转 PI」 */
async function quotationToPi(id) {
  if (!confirm('确认把该报价单转为形式发票 PI？同一报价单只能转一次。')) return;
  try {
    const result = await api(`/api/sales/quotations/${id}/to-pi`, 'POST');
    toast(`已生成形式发票 PI：${result.piNo}`);
    if (CURRENT_LOADER) CURRENT_LOADER();
    return result;
  } catch (err) { toast(err.message, 'error'); }
}

/* ============ 单据打印（打印预览 / 直接打印，模板复用「样式设计」） ============ */

/* 读取单据打印上下文（主表 + 明细 + 打印模板） */
async function fetchSalesDocPrintContext(code, oid) {
  const cfg = SALES_DOC_PRINT[code];
  if (!cfg) throw new Error('当前模块不支持单据打印');
  const doc = await api(`${cfg.api}/${oid}/print`);
  const template = await api(`/api/sys/print-templates/${encodeURIComponent(code)}`);
  return { code, oid, cfg, doc, template: normalizeTemplate(template) };
}

/* 打印预览：弹窗中查看打印效果，并提供「打印」按钮 */
async function previewSalesDocPrint(oid) {
  const code = CURRENT_MODULE_CODE;
  if (!SALES_DOC_PRINT[code]) { toast('当前模块不支持单据打印', 'error'); return; }
  if (!oid) { toast('请先保存单据后再打印', 'error'); return; }
  ensurePrintStyle();
  try {
    const ctx = await fetchSalesDocPrintContext(code, oid);
    window.__salesDocPrintContext = ctx;
    const html = buildSalesDocPrintHtml(ctx.code, ctx.cfg, ctx.template, ctx.doc);
    const modal = document.getElementById('modal');
    modal.innerHTML = `<div class="modal modal-lg" style="width:920px;max-width:96vw">
      <h3>🖨 打印预览 - ${escapeHtml(ctx.template.Title || ctx.cfg.title)}</h3>
      <div style="max-height:62vh;overflow:auto;border:1px solid #e2e8f0;border-radius:6px;background:#f8fafc">
        ${html}
      </div>
      <div class="modal-footer">
        <button class="btn btn-neutral" onclick="closeModal()">关闭</button>
        <button class="btn btn-primary" onclick="printSalesDoc()">🖨 打印</button>
      </div>
    </div>`;
    modal.style.display = 'flex';
  } catch (err) { toast(err.message, 'error'); }
}

/* 打印：新窗口输出并调起浏览器打印（支持模板纸张与字号） */
function printSalesDoc() {
  const ctx = window.__salesDocPrintContext;
  if (!ctx) { toast('请先打开打印预览', 'error'); return; }
  const win = window.open('', '_blank');
  if (!win) { toast('浏览器拦截了打印窗口，请允许弹出窗口后重试', 'error'); return; }
  const html = buildSalesDocPrintHtml(ctx.code, ctx.cfg, ctx.template, ctx.doc);
  const tpl = ctx.template || {};
  win.document.open();
  win.document.write(`<!DOCTYPE html><html lang="zh-CN"><head><meta charset="UTF-8">
    <title>${escapeHtml(tpl.Title || ctx.cfg.title)}</title>
    <style>${printPageCss(tpl.PaperSize)} ${PRINT_STYLE} body{margin:0;background:#fff}</style>
    </head><body>${html}</body></html>`);
  win.document.close();
  win.focus();
  setTimeout(() => { try { win.print(); } catch (e) { /* 用户取消打印 */ } }, 300);
}
