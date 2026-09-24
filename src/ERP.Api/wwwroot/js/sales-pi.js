/* ============ 报价单 / 形式发票 PI（EF 主子表单据）业务动作与单据打印 ============ */
/* 依赖：app.js（api / toast / fmtMoney / fmtDate / escapeHtml / navigate / openNavGroup）、
         crud.js（CURRENT_MODULE_CODE / CURRENT_LOADER / MODULES / REF_APIS / DETAIL_ROWS / openForm / detailRender）、
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

/* ============ 询价单 → 报价单（带入预填 / 直接生成，ERP-021） ============ */
async function inquiryToQuotation(id) {
  if (!confirm('确认按该已审核询价单生成报价单？同一询价单只能生成一张。')) return;
  try {
    const result = await api(`/api/inquiries/${id}/to-quotation`, 'POST');
    toast(`已生成报价单：${result.quotationNo}`);
    if (CURRENT_LOADER) CURRENT_LOADER();
    return result;
  } catch (err) { toast(err.message, 'error'); }
}

async function inquiryPrefillQuotation(id) {
  if (!confirm('按该询价单带入一张新的报价单？带入后可继续编辑，保存时服务端会复核金额。')) return;
  try {
    const data = await api(`/api/inquiries/${id}/quotation-prefill`);
    const mod = MODULES.quotation;
    if (!mod) { toast('报价单模块未加载', 'error'); return; }
    gotoModulePage('quotation', mod.title);
    openForm();
    await fillQuotationForm(data.quotation);
    toast('已按询价单带入，请核对后保存');
  } catch (err) { toast(err.message, 'error'); }
}

async function fillQuotationForm(quotation) {
  const mod = MODULES.quotation;
  if (!mod || !quotation) throw new Error('报价单带入数据为空');
  (mod.fields || []).forEach(f => {
    const el = document.getElementById('f_' + f.key);
    if (!el) return;
    const value = quotation[f.key];
    el.value = f.type === 'date' ? (value ? fmtDate(value) : '') : (value ?? '');
  });
  for (const f of (mod.fields || []).filter(x => x.type === 'ref')) {
    const box = document.getElementById('f_' + f.key + '_search');
    const id = quotation[f.key];
    if (!box || !id) continue;
    try {
      const ref = REF_APIS[f.ref];
      const item = await api(`${ref.api}/${id}`);
      box.value = item[ref.nameKey] || '';
    } catch (e) { /* 名称查询失败不影响带入 */ }
  }
  if (mod.detailFields && mod.detailFields.length) {
    DETAIL_ROWS = (quotation[mod.detailKey || 'details'] || []).map(d => Object.assign({}, d));
    detailRender();
  }
}

/* ============ 报价单 / PI → 销售订单（带入预填 / 直接生成，ERP-010） ============ */

/* 来源模块 -> 销售订单转换配置（与后端 SalesOrderConversion 的守卫规则一一对应） */
const SALES_ORDER_SOURCE = {
  quotation: { api: '/api/sales/quotations', label: '报价单' },
  'proforma-invoice': { api: '/api/sales/proforma-invoices', label: '形式发票（PI）' },
};

/* 报价单：直接生成销售订单（服务端守卫：同一报价单仅一张，重复点击返回明确错误） */
function quotationToOrder(id) { return salesDocToOrder('quotation', id); }

/* 报价单：带入预填（打开销售订单新增表单，核对 / 编辑后再保存） */
function quotationPrefillOrder(id) { return salesDocPrefillOrder('quotation', id); }

/* PI：直接生成销售订单（来源保留 PI 与其背后的报价单） */
function piToOrder(id) { return salesDocToOrder('proforma-invoice', id); }

/* PI：带入预填 */
function piPrefillOrder(id) { return salesDocPrefillOrder('proforma-invoice', id); }

/* 直接生成销售订单：POST {api}/{id}/to-order（落库一次，来源自动留痕） */
async function salesDocToOrder(code, id) {
  const cfg = SALES_ORDER_SOURCE[code];
  if (!cfg) { toast('当前模块不支持转销售订单', 'error'); return; }
  if (!confirm(`确认按该${cfg.label}生成销售订单？同一${cfg.label}只生成一张，生成后来源自动留痕。`)) return;
  try {
    const result = await api(`${cfg.api}/${id}/to-order`, 'POST');
    toast(`已生成销售订单：${result.orderNo}`);
    if (CURRENT_LOADER) CURRENT_LOADER();
    return result;
  } catch (err) { toast(err.message, 'error'); }
}

/* 带入预填：按来源单据打开「销售订单 → 新增」表单（字段与明细已带入，保存走销售订单接口做服务端复核） */
async function salesDocPrefillOrder(code, id) {
  const cfg = SALES_ORDER_SOURCE[code];
  if (!cfg) { toast('当前模块不支持带入销售订单', 'error'); return; }
  if (!confirm(`按该${cfg.label}带入一张新的销售订单？带入后可继续编辑，保存时由服务端复核数量、单价与合计。`)) return;
  try {
    const data = await api(`${cfg.api}/${id}/order-prefill`);
    const mod = MODULES['sales-order'];
    if (!mod) { toast('销售订单模块未加载', 'error'); return; }
    gotoModulePage('sales-order', mod.title);   // 切到销售订单页面（同步菜单高亮与标签页）
    openForm();
    await fillSalesOrderForm(data.order);
    toast(`已按${cfg.label}带入，请核对后保存`);
  } catch (err) { toast(err.message, 'error'); }
}

/* 切到目标菜单页：与左侧菜单点击同样效果（高亮当前项、必要时展开所属分组） */
function gotoModulePage(code, name) {
  const item = document.querySelector(`#sidebar-nav .nav-item.nav-child[data-code="${code}"]`);
  document.querySelectorAll('.nav-item.nav-child').forEach(x => x.classList.toggle('active', x === item));
  if (item) {
    const group = item.closest('.nav-group');
    if (group && !group.classList.contains('open') && typeof openNavGroup === 'function') openNavGroup(group, true);
  }
  navigate(code, name);
}

/* 把带入的销售订单草稿写入当前新增表单：字段按类型回填、引用字段同步名称、明细走 DETAIL_ROWS */
async function fillSalesOrderForm(order) {
  const mod = MODULES['sales-order'];
  if (!mod || !order) throw new Error('销售订单带入数据为空');
  (mod.fields || []).forEach(f => {
    const el = document.getElementById('f_' + f.key);
    if (!el) return;
    const v = order[f.key];
    if (f.valueType === 'bool') { el.value = (v === true || String(v).toLowerCase() === 'true') ? 'true' : 'false'; return; }
    if (f.type === 'date') { el.value = v ? fmtDate(v) : ''; return; }
    el.value = (v === null || v === undefined) ? '' : v;
  });
  /* 引用字段（客户 / 业务员）：与 crud.js loadIntoForm 同口径，异步补齐搜索框名称 */
  for (const f of (mod.fields || []).filter(x => x.type === 'ref')) {
    const box = document.getElementById('f_' + f.key + '_search');
    const rid = order[f.key];
    if (!box) continue;
    if (!rid) { box.value = ''; continue; }
    try {
      const ref = REF_APIS[f.ref];
      const d = await api(`${ref.api}/${rid}`);
      box.value = d[ref.nameKey] || '';
    } catch (e) { /* 名称查询失败不影响带入 */ }
  }
  /* 明细带入（数量 × 单价 = 金额由服务端在保存时复核） */
  if (mod.detailFields && mod.detailFields.length) {
    DETAIL_ROWS = (order[mod.detailKey || 'details'] || []).map(d => Object.assign({}, d));
    detailRender();
  }
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
  openSalesDocPrintWindow(ctx);
}

/* 新窗口输出并调起浏览器打印（支持模板纸张与字号）：打印预览弹窗的「打印」按钮与「直接打印」共用同一实现 */
function openSalesDocPrintWindow(ctx) {
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

/* ============ ERP-018：直接打印 / 打印设计 / 有效期治理 / 成交率报表 ============ */

/* 直接打印：不先弹预览，按打印模板直接输出并调起浏览器打印（与预览共用同一份打印数据与模板） */
async function printSalesDocDirect(oid) {
  const code = CURRENT_MODULE_CODE;
  if (!SALES_DOC_PRINT[code]) { toast('当前模块不支持单据打印', 'error'); return; }
  if (!oid) { toast('请先保存单据后再打印', 'error'); return; }
  ensurePrintStyle();
  try {
    openSalesDocPrintWindow(await fetchSalesDocPrintContext(code, oid));
  } catch (err) { toast(err.message, 'error'); }
}

/* 打印设计：跳到「样式设计」页并预选当前单据类型（报价单 / 形式发票 PI 已登记进共享 BILL_CONFIG） */
function designSalesDocPrint() {
  const code = CURRENT_MODULE_CODE;
  if (!SALES_DOC_PRINT[code]) { toast('当前模块不支持打印设计', 'error'); return; }
  gotoPrintDesign(code);
}

/* 有效期提醒窗口天数（与后端 QuotationValidityRules.DefaultAheadDays 保持一致，改动需同步两处） */
const QUOTATION_VALIDITY_AHEAD_DAYS = 7;

/* 报价单有效期分类：与后端 QuotationValidityRules 同口径（未设置有效期 / 已过期 / 今日到期 / 即将到期 / 有效） */
function quotationValidityOf(row) {
  if (!row || !row.validUntil) return { text: '未设置有效期', level: 'neutral', days: null };
  const today = new Date(); today.setHours(0, 0, 0, 0);
  const due = new Date(row.validUntil); due.setHours(0, 0, 0, 0);
  const days = Math.round((due.getTime() - today.getTime()) / 86400000);
  if (days < 0) return { text: '已过期', level: 'danger', days };
  if (days === 0) return { text: '今日到期', level: 'warning', days };
  if (days <= QUOTATION_VALIDITY_AHEAD_DAYS) return { text: '即将到期', level: 'warning', days };
  return { text: '有效', level: 'success', days };
}

/* 列表「有效期状态」列徽标（modules.js 的派生列通过 crud.js 列 render 回调调用） */
function quotationValidityBadge(row) {
  const v = quotationValidityOf(row);
  const extra = v.days === null ? '' : (v.days < 0 ? `（逾期 ${Math.abs(v.days)} 天）` : (v.days > 0 ? `（剩 ${v.days} 天）` : ''));
  return `<span class="status status-${v.level}">${v.text}${extra}</span>`;
}

/* 有效期提醒：列出已过期与提醒窗口内到期的报价单（后端按紧急度排序、已作废不提醒、未设置有效期不提醒） */
async function openQuotationValidityReminder(aheadDays) {
  const windowDays = Number(aheadDays) > 0 ? Number(aheadDays) : QUOTATION_VALIDITY_AHEAD_DAYS;
  try {
    const data = await api(`/api/sales/quotations/validity-due?aheadDays=${windowDays}`);
    const items = Array.isArray(data) ? data : (data.items || []);
    const rows = items.length ? items.map(r => `<tr>
        <td>${escapeHtml(r.quotationNo || '')}</td>
        <td>${escapeHtml(r.customerName || '')}</td>
        <td>${escapeHtml(r.salesmanName || '')}</td>
        <td>${fmtDate(r.validUntil)}</td>
        <td class="text-right">${r.validDays === null ? '--' : (r.validDays < 0 ? `逾期 ${Math.abs(r.validDays)} 天` : `剩 ${r.validDays} 天`)}</td>
        <td><span class="status status-${escapeHtml(r.validityLevel || 'neutral')}">${escapeHtml(r.validityStatus || '')}</span></td>
        <td class="text-right">${escapeHtml(r.currency || '')} ${fmtMoney(r.totalAmount)}</td>
        <td>${r.converted ? '<span class="status status-success">已转出</span>' : statusHtml(r.status)}</td>
      </tr>`).join('')
      : '<tr><td colspan="8" class="text-center text-muted">提醒窗口内没有需要跟进的报价单 👍</td></tr>';
    const windowOpts = [7, 15, 30]
      .map(d => `<option value="${d}" ${d === windowDays ? 'selected' : ''}>未来 ${d} 天内到期</option>`).join('');
    const modal = document.getElementById('modal');
    modal.innerHTML = `<div class="modal modal-lg" style="width:1120px;max-width:96vw">
      <h3>⏰ 报价有效期提醒（共 ${items.length} 张）</h3>
      <div class="toolbar" style="margin:8px 0">
        <div class="toolbar-left">
          <label>提醒窗口：
            <select id="qv-window" onchange="openQuotationValidityReminder(this.value)">${windowOpts}</select>
          </label>
          <span class="text-muted">口径：已过期 / 今日到期 / 即将到期；未设置有效期与已作废的报价单不提醒</span>
        </div>
      </div>
      <div class="table-wrap" style="max-height:52vh;overflow:auto">
        <table><thead><tr>
          <th>报价单号</th><th>客户</th><th>业务员</th><th>有效期至</th>
          <th class="text-right">剩余</th><th>有效期状态</th><th class="text-right">报价总额</th><th>单据状态</th>
        </tr></thead><tbody>${rows}</tbody></table>
      </div>
      <div class="modal-footer">
        <button class="btn btn-neutral" onclick="closeModal()">关闭</button>
        <button class="btn btn-primary" onclick="closeModal();openQuotationConversionReport()">📈 看成交率报表</button>
      </div>
    </div>`;
    modal.style.display = 'flex';
  } catch (err) { toast(err.message, 'error'); }
}

/* 成交率报表入口：直接渲染报表框架里的「报价成交率分析」（与报表中心同一套渲染与导出） */
function openQuotationConversionReport() {
  const rep = REPORTS['quotation-conversion'];
  if (!rep) { toast('成交率报表未加载', 'error'); return; }
  renderReport(rep, rep.title);
}

/* ============ 报价单版本链（ERP-035：多轮议价版本留痕） ============ */
/* 依赖：app.js（api / toast / fmtMoney / fmtDate / escapeHtml / statusHtml）、
         crud.js（CURRENT_LOADER / openForm）、modules.js 的 quotation 模块（列表派生列与行操作接线）。
   契约：版本号由服务端分配（初始版本 V1，链内单调递增），单号 = 根单号 + “-R版本号”。 */

/* 版本号：数据库字段为数字；历史报价单（新增版本列之前创建）为 0 或未赋值 → 按初始版本 V1 显示 */
function quotationRevisionNumberOf(row) {
  const rev = Number(row && row.revisionNumber);
  return Number.isFinite(rev) && rev > 0 ? rev : 1;
}

/* 列表「版本」派生列：V1 / V2 … 徽标 */
function quotationRevisionBadge(row) {
  const rev = quotationRevisionNumberOf(row);
  return `<span class="status ${rev > 1 ? 'status-info' : 'status-neutral'}" title="报价版本 V${rev}">V${rev}</span>`;
}

/* 列表「版本链根单号」派生列：初始版本没有冗余根单号，按自身单号显示（版本链的根即它自己） */
function quotationRootNo(row) {
  return escapeHtml((row && (row.rootQuotationNo || row.quotationNo)) || '');
}

/* 列表「上一版本」派生列：初始版本没有前序版本 → 显式标注「初始版本」，不留空白 */
function quotationPreviousNo(row) {
  const prev = row && row.previousRevisionNo;
  return prev ? escapeHtml(prev) : '<span class="text-muted">—（初始版本）</span>';
}

/* 创建新版本：POST {api}/{id}/revisions —— 服务端整单复制为草稿（源版本一行不改、合计服务端复算，
   审核状态与下游转换不复制）；已作废 / 并发同版本号由服务端守卫拒绝 */
async function quotationCreateRevision(id) {
  if (!confirm('按该报价单创建新版本？源版本将成为只读历史，新版本为草稿，可修改后再审核。')) return;
  try {
    const result = await api(`/api/sales/quotations/${id}/revisions`, 'POST');
    toast(`已创建新版本 ${result.quotationNo}（V${result.revisionNumber}，草稿；源版本 ${result.previousRevisionNo} 转为历史只读）`);
    if (CURRENT_LOADER) CURRENT_LOADER();
    return result;
  } catch (err) { toast(err.message, 'error'); }
}

/* 版本历史：GET {api}/{id}/revisions —— 完整版本链（根单 + 全部历史版本，不隐藏历史版本）。
   每行显示版本号 / 单号 / 根单号 / 上一版本 / 日期 / 金额 / 状态 / 下游，并可跳转到任一版本编辑页；
   「历史只读」与后端 Superseded 同口径（链内已有指向它的下一版本）。 */
async function quotationRevisionHistory(id) {
  try {
    const data = await api(`/api/sales/quotations/${id}/revisions`);
    const items = Array.isArray(data) ? data : (data.items || []);
    const rows = items.length ? items.map(r => `<tr>
        <td><span class="status ${r.revisionNumber > 1 ? 'status-info' : 'status-neutral'}">V${r.revisionNumber}</span>${r.isLatest ? ' <span class="status status-success">最新</span>' : ''}${r.superseded ? ' <span class="status status-warning">历史只读</span>' : ''}${r.isSelected ? ' <b>（当前）</b>' : ''}</td>
        <td>${escapeHtml(r.quotationNo || '')}</td>
        <td>${escapeHtml(r.rootQuotationNo || '')}</td>
        <td>${r.previousRevisionNo ? escapeHtml(r.previousRevisionNo) : '<span class="text-muted">—（初始版本）</span>'}</td>
        <td>${fmtDate(r.quotationDate)}</td>
        <td class="text-right">${escapeHtml(r.currency || '')} ${fmtMoney(r.totalAmount)}</td>
        <td>${statusHtml(r.status)}</td>
        <td>${r.converted ? '<span class="status status-success">已转出</span>' : '<span class="text-muted">—</span>'}</td>
        <td><button class="btn btn-neutral btn-sm" onclick="closeModal();openForm(${r.id})">打开</button></td>
      </tr>`).join('')
      : '<tr><td colspan="9" class="text-center text-muted">暂无版本记录</td></tr>';
    const modal = document.getElementById('modal');
    modal.innerHTML = `<div class="modal modal-lg" style="width:1180px;max-width:96vw">
      <h3>🧬 报价版本链（共 ${items.length} 个版本）</h3>
      <div class="toolbar" style="margin:8px 0">
        <div class="toolbar-left">
          <span class="text-muted">版本号由服务端分配（链内单调递增）；「历史只读」= 已被后续版本取代，不可再修改 / 审核 / 转换；下游 PI 与销售订单只认被显式选中的版本</span>
        </div>
      </div>
      <div class="table-wrap" style="max-height:52vh;overflow:auto">
        <table><thead><tr>
          <th>版本</th><th>报价单号</th><th>版本链根单号</th><th>上一版本</th><th>报价日期</th>
          <th class="text-right">报价总额</th><th>状态</th><th>下游</th><th>操作</th>
        </tr></thead><tbody>${rows}</tbody></table>
      </div>
      <div class="modal-footer">
        <button class="btn btn-neutral" onclick="closeModal()">关闭</button>
      </div>
    </div>`;
    modal.style.display = 'flex';
  } catch (err) { toast(err.message, 'error'); }
}

