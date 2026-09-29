'use strict';
/* ERP-113 前端 UI 逻辑单测：字段选择、请求边界、安全单元格渲染、失败态，以及前端接线契约。
   运行：node tests/automation/dynamic_sales_order_report_ui.test.js
   失败时抛异常并以非零码退出。 */

const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const JS_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'dynamic-sales-order-report.js');
const MODULES_DOC = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'modules-doc.js');
const INDEX_HTML = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'index.html');

let passed = 0;
function ok(cond, msg) { assert.ok(cond, msg); passed += 1; }
function eq(actual, expected, msg) { assert.deepStrictEqual(actual, expected, msg); passed += 1; }

const mod = require(JS_PATH);

// ---- 1. 字段选择（只来自目录白名单，去重、保持顺序、丢弃未知键） ----
const catalogFields = [
  { key: 'id', label: '订单Id', dataType: 'number', filterable: false },
  { key: 'orderNo', label: '订单号', dataType: 'text', filterable: false },
  { key: 'orderDate', label: '订单日期', dataType: 'date', filterable: true },
  { key: 'customerId', label: '客户Id', dataType: 'number', filterable: true },
];
eq(mod.dsorSelectFields(catalogFields, ['orderNo', 'orderNo', 'bogus', ' orderDate ']),
  ['orderNo', 'orderDate'], '字段选择去重并丢弃未知键');

// ---- 2. 请求边界（仅选定字段、分页有界、筛选只含日期/客户/状态/币种，无任意字段名） ----
const req = mod.dsorBuildRequest({
  catalogFields,
  selectedKeys: ['orderNo', 'orderDate', 'orderNo', 'hacked'],
  startDate: '2026-09-01T00:00:00',
  endDate: '2026-09-30',
  customerId: '42',
  status: 'Approved',
  currency: 'USD',
  page: 0,
  pageSize: 9999,
  maxPageSize: 200,
});
eq(req.fields, ['orderNo', 'orderDate'], '请求只含选定白名单字段');
eq(req.page, 1, '页码最小 1');
eq(req.pageSize, 200, '每页钳制到上限');
eq(req.startDate, '2026-09-01', '开始日期只取日期部分');
eq(req.endDate, '2026-09-30');
eq(req.customerId, 42);
eq(req.status, 'Approved');
eq(req.currency, 'USD');
ok(!req.fields.includes('hacked'), '未知字段绝不进入请求');

const emptyReq = mod.dsorBuildRequest({ catalogFields, selectedKeys: [], page: 2, pageSize: 20, maxPageSize: 200 });
eq(emptyReq.fields, [], '空选择 = 空字段列表（后端返回全部白名单）');
ok(!('customerId' in emptyReq) && !('status' in emptyReq) && !('currency' in emptyReq), '空筛选不携带多余键');

// ---- 3. 安全单元格渲染（转义、null 为空、布尔 是/否、日期截断、状态映射中文） ----
eq(mod.dsorCellText(null, { key: 'remark', dataType: 'text' }), '', 'null 显示为空（不回落 0）');
eq(mod.dsorCellText(undefined, { key: 'remark', dataType: 'text' }), '', 'undefined 显示为空');
eq(mod.dsorCellText(true, { key: 'splitShipment', dataType: 'boolean' }), '是');
eq(mod.dsorCellText(false, { key: 'splitShipment', dataType: 'boolean' }), '否');
eq(mod.dsorCellText('2026-09-01T00:00:00', { key: 'orderDate', dataType: 'date' }), '2026-09-01');
eq(mod.dsorCellText('Approved', { key: 'status', dataType: 'enum' }), '已审核');
eq(mod.dsorCellText('USD', { key: 'currency', dataType: 'enum' }), 'USD');
eq(mod.dsorRenderCell('<script>alert(1)</script>', { key: 'remark', dataType: 'text' }),
  '&lt;script&gt;alert(1)&lt;/script&gt;', '注入标签被转义');

// ---- 4. 表格渲染列名与单元格（转义） ----
const view = {
  columns: [
    { key: 'orderNo', label: '订单号', dataType: 'text', filterable: false },
    { key: 'totalAmount', label: '订单总额', dataType: 'number', filterable: false },
  ],
  rows: [
    { orderNo: 'SO-001', totalAmount: 100 },
    { orderNo: '<b>SO-002</b>', totalAmount: null },
  ],
  total: 2, page: 1, pageSize: 20, totalPages: 1,
};
const tableHtml = mod.dsorTableHtml(view);
ok(tableHtml.includes('<th>订单号</th>'), '渲染返回的列名');
ok(tableHtml.includes('>订单总额</th>'), '渲染返回的列名（数字列右对齐）');
ok(tableHtml.includes('>SO-001<'), '渲染单元格值');
ok(tableHtml.includes('&lt;b&gt;SO-002&lt;/b&gt;'), '单元格值被转义');
ok(!tableHtml.includes('<b>SO-002</b>'), '未转义标签不出现');

// ---- 5. 空结果与失败态 ----
const emptyView = {
  columns: [{ key: 'orderNo', label: '订单号', dataType: 'text' }],
  rows: [], total: 0, page: 1, pageSize: 20, totalPages: 0,
  readOnlyText: '只读', boundaryText: '边界', disclaimerText: '免责',
};
const emptyHtml = mod.dsorResultHtml(emptyView);
ok(emptyHtml.includes('没有符合条件'), '空结果可见');
ok(emptyHtml.includes('只读') && emptyHtml.includes('边界') && emptyHtml.includes('免责'), '口径文案可见');
ok(mod.dsorErrorHtml('forbidden', '无销售订单菜单授权').includes('权限不足'), '授权失败可见');
ok(mod.dsorErrorHtml('network', 'fetch failed').includes('网络请求失败'), '网络失败可见');
ok(mod.dsorErrorHtml('invalid', '未知字段').includes('请求无效'), '无效请求可见');

// ---- 6. 前端接线契约（工具栏入口、脚本注册、接口路径、无任意 SQL / 自由字段名输入） ----
const modulesDoc = fs.readFileSync(MODULES_DOC, 'utf8');
ok(modulesDoc.includes("onclick: 'openDynamicSalesOrderReport()'"), 'modules-doc.js 注册工具栏入口');
const indexHtml = fs.readFileSync(INDEX_HTML, 'utf8');
ok(indexHtml.includes('/js/dynamic-sales-order-report.js'), 'index.html 注册脚本');
const jsSrc = fs.readFileSync(JS_PATH, 'utf8');
ok(jsSrc.includes('/api/sales-orders/report'), '调用 ERP-112 只读接口');
ok(jsSrc.includes('name="dsor-field"') && jsSrc.includes('type="checkbox"'), '字段选择器为复选框，无自由字段名输入');
ok(!/FromSql|ExecuteSql|SqlCommand|SELECT\s+\*/i.test(jsSrc), '不执行任意 SQL');

console.log('\n✅ ERP-113 动态销售订单报表 UI 逻辑测试全部通过（' + passed + ' 项断言）。\n');
