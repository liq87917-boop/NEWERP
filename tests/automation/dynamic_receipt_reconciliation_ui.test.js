'use strict';
/* ERP-166 前端 UI 逻辑单测：目录驱动的订单证据 / 未关联收款证据字段选择、请求边界、分页渲染、单元格渲染、
   CSV 导出（两类证据分开、null 未知保留、防公式注入）与失败态，以及前端接线契约。
   运行：node tests/automation/dynamic_receipt_reconciliation_ui.test.js
   失败时抛异常并以非零码退出。 */

const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const JS_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'sales-order-receipt-reconciliation.js');
const INDEX_HTML = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'index.html');

let passed = 0;
function ok(cond, msg) { assert.ok(cond, msg); passed += 1; }
function eq(actual, expected, msg) { assert.deepStrictEqual(actual, expected, msg); passed += 1; }

const mod = require(JS_PATH);

// ---- 1. 字段选择（只来自目录白名单，去重、保持顺序、丢弃未知键） ----
const orderFields = [
  { key: 'orderNo', label: '订单号', dataType: 'text', filterable: false },
  { key: 'orderDate', label: '订单日期', dataType: 'date', filterable: true },
  { key: 'customerName', label: '客户名', dataType: 'text', filterable: false },
];
const receiptFields = [
  { key: 'receiptNo', label: '收款单号', dataType: 'text', filterable: false },
  { key: 'currency', label: '币种', dataType: 'enum', filterable: false },
  { key: 'amount', label: '金额', dataType: 'number', filterable: false },
];
eq(mod.drrSelectFields(orderFields, ['orderNo', 'orderNo', 'bogus', ' orderDate ']),
  ['orderNo', 'orderDate'], '订单字段选择去重并丢弃未知键');
eq(mod.drrSelectFields(receiptFields, ['receiptNo', 'receiptNo', 'hacked', ' amount ']),
  ['receiptNo', 'amount'], '收款字段选择去重并丢弃未知键');

// ---- 2. 请求边界（仅选定字段、分页有界、筛选只取枚举白名单，无任意字段名） ----
const req = mod.drrBuildRequest({
  catalogFields: orderFields,
  receiptFields,
  selectedKeys: ['orderNo', 'orderDate', 'orderNo', 'hacked'],
  selectedReceiptKeys: ['receiptNo', 'amount', 'receiptNo'],
  customerId: '42',
  currency: 'USD',
  orderDateFrom: '2026-09-01T00:00:00',
  orderDateTo: '2026-09-30',
  shipmentStatus: 'shipped',
  receiptLinkStatus: 'linked',
  receiptStatus: 'active',
  orderStatus: 'active',
  keyword: '  SO-001  ',
  page: 0,
  pageSize: 9999,
  maxPageSize: 200,
});
eq(req.fields, ['orderNo', 'orderDate'], '请求只含选定白名单订单字段');
eq(req.receiptFields, ['receiptNo', 'amount'], '请求只含选定白名单收款字段');
eq(req.page, 1, '页码最小 1');
eq(req.pageSize, 200, '每页钳制到上限');
eq(req.customerId, 42, '客户 Id 转为正整数');
eq(req.currency, 'USD');
eq(req.orderDateFrom, '2026-09-01', '开始日期只取日期部分');
eq(req.orderDateTo, '2026-09-30');
eq(req.shipmentStatus, 'shipped');
eq(req.receiptLinkStatus, 'linked');
eq(req.receiptStatus, 'active');
eq(req.orderStatus, 'active');
eq(req.keyword, 'SO-001', '关键字去首尾空白');
ok(!req.fields.includes('hacked'), '未知字段绝不进入请求');

const emptyReq = mod.drrBuildRequest({
  catalogFields: orderFields, receiptFields,
  selectedKeys: [], selectedReceiptKeys: [],
  page: 2, pageSize: 20, maxPageSize: 200,
});
eq(emptyReq.fields, [], '空选择 = 空字段列表（后端返回全部白名单）');
eq(emptyReq.receiptFields, [], '空收款字段选择 = 空列表');
ok(!('customerId' in emptyReq) && !('currency' in emptyReq) && !('shipmentStatus' in emptyReq)
  && !('receiptLinkStatus' in emptyReq) && !('receiptStatus' in emptyReq) && !('orderStatus' in emptyReq),
  '空筛选不携带多余键');

const invalidReq = mod.drrBuildRequest({
  catalogFields: orderFields, receiptFields,
  selectedKeys: ['orderNo'], selectedReceiptKeys: ['receiptNo'],
  currency: 'XXX', shipmentStatus: 'bogus', receiptLinkStatus: 'bogus',
  receiptStatus: 'bogus', orderStatus: 'bogus',
  page: 1, pageSize: 20, maxPageSize: 200,
});
ok(!('currency' in invalidReq) && !('shipmentStatus' in invalidReq) && !('receiptLinkStatus' in invalidReq)
  && !('receiptStatus' in invalidReq) && !('orderStatus' in invalidReq),
  '非法币种 / 出货状态 / 收款链接 / 收款证据 / 订单状态取值绝不进入请求（fail closed）');

// ---- 3. 安全单元格渲染（null 数值显示「未知」、布尔 是/否、日期截断、转义） ----
eq(mod.drrCellText(null, { key: 'amount', dataType: 'number' }), '未知', 'null 数值显示「未知」（绝不回落 0）');
eq(mod.drrCellText(null, { key: 'note', dataType: 'text' }), '', 'null 文本显示为空');
eq(mod.drrCellText(undefined, { key: 'note', dataType: 'text' }), '', 'undefined 显示为空');
eq(mod.drrCellText(true, { key: 'hasApprovedShipment', dataType: 'boolean' }), '是');
eq(mod.drrCellText(false, { key: 'hasApprovedShipment', dataType: 'boolean' }), '否');
eq(mod.drrCellText('2026-09-01T00:00:00', { key: 'orderDate', dataType: 'date' }), '2026-09-01');
eq(mod.drrCellText('USD', { key: 'currency', dataType: 'enum' }), 'USD');
eq(mod.drrRenderCell('<script>alert(1)</script>', { key: 'note', dataType: 'text' }),
  '&lt;script&gt;alert(1)&lt;/script&gt;', '注入标签被转义');

// ---- 4. 表格渲染列名与单元格（转义）+ 未关联收款独立分区 + 截断警告 + 分页 ----
const view = {
  columns: [
    { key: 'orderNo', label: '订单号', dataType: 'text', filterable: false },
    { key: 'orderAmount', label: '订单金额', dataType: 'number', filterable: false },
  ],
  rows: [
    { orderNo: 'SO-001', orderAmount: 100 },
    { orderNo: '<b>SO-002</b>', orderAmount: null },
  ],
  receiptColumns: [
    { key: 'receiptNo', label: '收款单号', dataType: 'text', filterable: false },
    { key: 'currency', label: '币种', dataType: 'enum', filterable: false },
    { key: 'amount', label: '金额', dataType: 'number', filterable: false },
  ],
  receiptRows: [
    { receiptNo: 'R-001', currency: 'USD', amount: 40 },
  ],
  unlinkedReceiptTruncated: true,
  total: 2, page: 1, pageSize: 20, totalPages: 1,
  readOnlyText: '只读', boundaryText: '边界', disclaimerText: '免责',
};

const orderHtml = mod.drrOrderSectionHtml(view);
ok(orderHtml.includes('<th>订单号</th>'), '渲染返回的订单列名');
ok(orderHtml.includes('>订单金额</th>'), '渲染返回的订单列名（数字列右对齐）');
ok(orderHtml.includes('>SO-001<'), '渲染订单单元格值');
ok(orderHtml.includes('&lt;b&gt;SO-002&lt;/b&gt;'), '订单单元格值被转义');
ok(!orderHtml.includes('<b>SO-002</b>'), '未转义标签不出现');
ok(orderHtml.includes('上一页') && orderHtml.includes('下一页'), '分页控件可见');

const receiptHtml = mod.drrReceiptSectionHtml(view);
ok(receiptHtml.includes('未关联收款证据'), '未关联收款证据独立分区可见');
ok(receiptHtml.includes('>R-001<'), '渲染收款单元格值');
ok(receiptHtml.includes('>USD<'), '收款证据保留原币');
ok(receiptHtml.includes('截断'), '截断警告可见');

const resultHtml = mod.drrResultHtml(view);
ok(resultHtml.includes('只读') && resultHtml.includes('边界') && resultHtml.includes('免责'), '口径文案可见');

const emptyView = {
  columns: [{ key: 'orderNo', label: '订单号', dataType: 'text' }],
  rows: [],
  receiptColumns: [], receiptRows: [],
  unlinkedReceiptTruncated: false,
  total: 0, page: 1, pageSize: 20, totalPages: 0,
};
ok(mod.drrOrderSectionHtml(emptyView).includes('没有符合筛选条件'), '空结果可见');

// ---- 5. 空 / 加载 / 失败态 ----
ok(mod.drrEmptyHtml().includes('没有符合条件'), '空结果提示可见');
ok(mod.drrLoadingHtml().includes('正在预览'), '加载态可见');
ok(mod.drrErrorHtml('forbidden', '无销售订单菜单授权').includes('权限不足'), '授权失败可见');
ok(mod.drrErrorHtml('network', 'fetch failed').includes('网络请求失败'), '网络失败可见');
ok(mod.drrErrorHtml('invalid', '未知字段').includes('请求无效'), '无效请求可见');
eq(mod.drrKindOfCode(2002), 'forbidden', '2002 → 权限不足');
eq(mod.drrKindOfCode(2000), 'unauthorized', '2000 → 未登录');
eq(mod.drrKindOfCode(2003), 'unauthorized', '2003 → 令牌过期');
eq(mod.drrKindOfCode(1001), 'invalid', '1001 → 请求无效');

// ---- 6. CSV：null 未知保留为空、防公式注入、两类证据分开 ----
eq(mod.drrCsvCell(null, { key: 'amount', dataType: 'number' }), '', 'null 未知导出为空（绝不回落 0）');
eq(mod.drrCsvCell('=1+1', { key: 'note', dataType: 'text' }), "'=1+1", '公式前导 = 被转义');
eq(mod.drrCsvCell('+SUM(A1)', { key: 'note', dataType: 'text' }), "'+SUM(A1)", '公式前导 + 被转义');
eq(mod.drrCsvCell('@cmd', { key: 'note', dataType: 'text' }), "'@cmd", '公式前导 @ 被转义');
eq(mod.drrCsvCell('-100', { key: 'orderAmount', dataType: 'number' }), '-100', '负数金额不当作公式文本');
eq(mod.drrCsvCell('a,b', { key: 'note', dataType: 'text' }), '"a,b"', '含逗号按 RFC4180 加引号');
eq(mod.drrCsvCell('say "hi"', { key: 'note', dataType: 'text' }), '"say ""hi"""', '含引号转义');

const orderCsv = mod.drrBuildCsv(view.columns, view.rows);
const receiptCsv = mod.drrBuildCsv(view.receiptColumns, view.receiptRows);
ok(orderCsv.startsWith('订单号,订单金额'), '订单证据 CSV 表头');
ok(orderCsv.includes('SO-001,100'), '订单证据 CSV 行');
ok(orderCsv.includes('<b>SO-002</b>,'), '订单证据 CSV 保留原文且未知金额为空');
ok(receiptCsv.startsWith('收款单号,币种,金额'), '未关联收款证据 CSV 表头');
ok(receiptCsv.includes('R-001,USD,40'), '未关联收款证据 CSV 行（原币）');
ok(orderCsv !== receiptCsv, '两类证据 CSV 分开导出');

// ---- 7. 前端接线契约（入口、脚本注册、接口路径、无任意 SQL / 自由字段名输入） ----
const jsSrc = fs.readFileSync(JS_PATH, 'utf8');
ok(jsSrc.includes('openDynamicReceiptReconciliationDesigner'), '文件内定义设计器入口');
ok(jsSrc.includes('/api/sales-orders/dynamic-receipt-reconciliation-report'), '调用 ERP-164/165 只读接口');
ok(jsSrc.includes('name="drr-field"') && jsSrc.includes('type="checkbox"'), '订单字段选择器为复选框');
ok(jsSrc.includes('name="drr-receipt-field"') && jsSrc.includes('type="checkbox"'), '收款字段选择器为复选框');
ok(jsSrc.includes('function openSalesOrderReceiptReconciliationReport()'), '既有静态核对报表入口保留');
ok(jsSrc.includes('drrExportOrderCsv') && jsSrc.includes('drrExportReceiptCsv'), '两类证据 CSV 分开导出');
ok(!/FromSql|ExecuteSql|SqlCommand|SELECT\s+\*/i.test(jsSrc), '不执行任意 SQL');
const indexHtml = fs.readFileSync(INDEX_HTML, 'utf8');
ok(indexHtml.includes('/js/sales-order-receipt-reconciliation.js'), 'index.html 注册脚本');

console.log('\n✅ ERP-166 客户订单与收款核对动态设计器 UI 逻辑测试全部通过（' + passed + ' 项断言）。\n');
