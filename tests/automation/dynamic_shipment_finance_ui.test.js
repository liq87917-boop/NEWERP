'use strict';
/* ERP-157 前端 UI 逻辑单测：字段选择、请求边界与分页、安全单元格渲染（未知=「未知」）、
   CSV（未知保留 + 公式前导转义）、失败态，以及前端接线契约。
   运行：node tests/automation/dynamic_shipment_finance_ui.test.js
   失败时抛异常并以非零码退出。 */

const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const JS_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'sales-order-progress.js');
const DOC = path.join(ROOT, 'docs', 'dynamic-shipment-finance-report.md');

let passed = 0;
function ok(cond, msg) { assert.ok(cond, msg); passed += 1; }
function eq(actual, expected, msg) { assert.deepStrictEqual(actual, expected, msg); passed += 1; }

const mod = require(JS_PATH);

// ---- 1. 字段选择（只来自目录白名单，去重、保持顺序、丢弃未知键） ----
const catalogFields = [
  { key: 'orderId', label: '订单Id', dataType: 'number', filterable: false },
  { key: 'orderNo', label: '订单号', dataType: 'text', filterable: false },
  { key: 'orderDate', label: '订单日期', dataType: 'date', filterable: true },
  { key: 'customerId', label: '客户Id', dataType: 'number', filterable: true },
  { key: 'currency', label: '币种', dataType: 'text', filterable: true },
];
eq(mod.dsfSelectFields(catalogFields, ['orderNo', 'orderNo', 'bogus', ' currency ']),
  ['orderNo', 'currency'], '字段选择去重并丢弃未知键');

// ---- 2. 请求边界与分页（仅选定字段、分页有界、筛选只含客户/币种/日期/出货/收款链接，无任意字段名） ----
const req = mod.dsfBuildRequest({
  catalogFields,
  selectedKeys: ['orderNo', 'orderDate', 'orderNo', 'hacked'],
  customerId: '42',
  currency: 'USD',
  dateFrom: '2026-09-01T00:00:00',
  dateTo: '2026-09-30',
  shipmentStatus: 'none',
  financeLinkStatus: 'linked',
  page: 0,
  pageSize: 9999,
  maxPageSize: 200,
});
eq(req.fields, ['orderNo', 'orderDate'], '请求只含选定白名单字段');
eq(req.page, 1, '页码最小 1');
eq(req.pageSize, 200, '每页钳制到上限');
eq(req.orderDateFrom, '2026-09-01', '开始日期只取日期部分');
eq(req.orderDateTo, '2026-09-30');
eq(req.customerId, 42);
eq(req.currency, 'USD');
eq(req.shipmentStatus, 'none');
eq(req.financeLinkStatus, 'linked');
ok(!req.fields.includes('hacked'), '未知字段绝不进入请求');

const emptyReq = mod.dsfBuildRequest({ catalogFields, selectedKeys: [], page: 2, pageSize: 20, maxPageSize: 200 });
eq(emptyReq.fields, [], '空选择 = 空字段列表（后端返回全部白名单）');
ok(!('customerId' in emptyReq) && !('currency' in emptyReq) && !('orderDateFrom' in emptyReq)
  && !('orderDateTo' in emptyReq) && !('shipmentStatus' in emptyReq) && !('financeLinkStatus' in emptyReq),
  '空筛选不携带多余键');

// ---- 3. 安全单元格渲染（未知=「未知」不回落 0、布尔 是/否、日期截断、状态映射中文、转义） ----
eq(mod.dsfCellText(null, { key: 'linkedAmount', dataType: 'number' }), '未知', '未知金额显示「未知」，不回落 0');
eq(mod.dsfCellText(null, { key: 'shippedQuantity', dataType: 'number' }), '未知', '未知数量显示「未知」');
eq(mod.dsfCellText(undefined, { key: 'orderedQuantity', dataType: 'number' }), '未知', 'undefined 显示「未知」');
eq(mod.dsfCellText(true, { key: 'hasApprovedShipment', dataType: 'boolean' }), '是');
eq(mod.dsfCellText(false, { key: 'overReceived', dataType: 'boolean' }), '否');
eq(mod.dsfCellText('2026-09-01T00:00:00', { key: 'orderDate', dataType: 'date' }), '2026-09-01');
eq(mod.dsfCellText('none', { key: 'shipmentStatus', dataType: 'text' }), '未出货');
eq(mod.dsfCellText('unknown', { key: 'shipmentStatus', dataType: 'text' }), '未知（超出派生上限）');
eq(mod.dsfCellText('linked', { key: 'financeLinkStatus', dataType: 'text' }), '收款引用完整');
eq(mod.dsfCellText('Approved', { key: 'status', dataType: 'text' }), '已审核');
eq(mod.dsfCellText('USD', { key: 'currency', dataType: 'text' }), 'USD');
eq(mod.dsfRenderCell('<script>alert(1)</script>', { key: 'note', dataType: 'text' }),
  '&lt;script&gt;alert(1)&lt;/script&gt;', '注入标签被转义');

// ---- 4. 表格渲染列名与单元格（转义、未知显式、翻页控件） ----
const view = {
  columns: [
    { key: 'orderNo', label: '订单号', dataType: 'text', filterable: false },
    { key: 'linkedAmount', label: '已关联金额', dataType: 'number', filterable: false },
    { key: 'shipmentStatus', label: '出货状态', dataType: 'text', filterable: true },
  ],
  rows: [
    { orderNo: 'SO-001', linkedAmount: 250, shipmentStatus: 'complete' },
    { orderNo: '<b>SO-002</b>', linkedAmount: null, shipmentStatus: 'unknown' },
  ],
  total: 2, page: 1, pageSize: 20, totalPages: 1,
};
const tableHtml = mod.dsfTableHtml(view);
ok(tableHtml.includes('<th>订单号</th>'), '渲染返回的列名');
ok(tableHtml.includes('>已关联金额</th>'), '渲染返回的列名（数字列右对齐）');
ok(tableHtml.includes('>SO-001<'), '渲染单元格值');
ok(tableHtml.includes('&lt;b&gt;SO-002&lt;/b&gt;'), '单元格值被转义');
ok(!tableHtml.includes('<b>SO-002</b>'), '未转义标签不出现');
ok(tableHtml.includes('未知'), '未知证据显式渲染为「未知」');
ok(tableHtml.includes('已出齐'), '出货状态映射中文');
ok(tableHtml.includes('上一页') && tableHtml.includes('下一页'), '表格含翻页控件（分页）');

// ---- 5. 空结果与失败态 ----
const emptyView = {
  columns: [{ key: 'orderNo', label: '订单号', dataType: 'text' }],
  rows: [], total: 0, page: 1, pageSize: 20, totalPages: 0,
  readOnlyText: '只读', boundaryText: '边界', disclaimerText: '免责',
};
const emptyHtml = mod.dsfResultHtml(emptyView);
ok(emptyHtml.includes('没有符合条件'), '空结果可见');
ok(emptyHtml.includes('只读') && emptyHtml.includes('边界') && emptyHtml.includes('免责'), '口径文案可见');
ok(mod.dsfErrorHtml('forbidden', '无销售订单菜单授权').includes('权限不足'), '授权失败可见');
ok(mod.dsfErrorHtml('unauthorized', '登录已过期').includes('未登录'), '未登录可见');
ok(mod.dsfErrorHtml('network', 'fetch failed').includes('网络请求失败'), '网络失败可见');
ok(mod.dsfErrorHtml('invalid', '未知字段').includes('请求无效'), '无效请求可见');

// ---- 6. CSV：未知保留「未知」、公式前导转义、表头为列名 ----
const csvView = {
  columns: [
    { key: 'orderNo', label: '订单号', dataType: 'text' },
    { key: 'linkedAmount', label: '已关联金额', dataType: 'number' },
    { key: 'note', label: '说明', dataType: 'text' },
  ],
  rows: [
    { orderNo: 'SO-001', linkedAmount: 250, note: 'normal' },
    { orderNo: '=SUM(A1:A2)', linkedAmount: null, note: '+cmd' },
  ],
};
const csv = mod.dsfCsv(csvView);
ok(csv.includes('"订单号","已关联金额","说明"'), 'CSV 表头为列名');
ok(csv.includes('"SO-001","250","normal"'), 'CSV 普通行');
ok(csv.includes('"未知"'), '未知金额在 CSV 中保留为「未知」，不回落 0');
ok(csv.includes('"\'=SUM(A1:A2)"'), '公式前导 = 被单引号转义');
ok(csv.includes('"\'+cmd"'), '公式前导 + 被单引号转义');

// ---- 7. 字段选择器为复选框（无自由字段名输入） ----
const chooser = mod.dsfFieldChooserHtml(catalogFields, ['orderNo']);
ok(chooser.includes('name="dsf-field"') && chooser.includes('type="checkbox"'), '字段选择器为复选框');
ok(chooser.includes('value="orderNo"') && chooser.includes('checked'), '已选字段回显为勾选');
ok(!/type="text"/.test(chooser), '选择器无自由文本输入');

// ---- 8. 前端接线契约（入口、ERP-156 接口、无任意 SQL / 自由字段名输入） ----
const jsSrc = fs.readFileSync(JS_PATH, 'utf8');
ok(jsSrc.includes('function openSalesOrderShipmentFinanceFieldDesigner()'), '设计器入口函数存在');
ok(jsSrc.includes('/api/sales-orders/dynamic-shipment-finance-report'), '调用 ERP-156 只读接口');
ok(jsSrc.includes('onclick="openSalesOrderShipmentFinanceFieldDesigner()"'), '出货/财务进度页工具栏提供字段设计器入口');
ok(/FromSql|ExecuteSql|SqlCommand|SELECT\s+\*/i.test(jsSrc) === false, '不执行任意 SQL');

// ---- 9. 文档工作流已记录 ----
const docSrc = fs.readFileSync(DOC, 'utf8');
ok(docSrc.includes('ERP-157'), '文档记录 ERP-157 工作流');

console.log('\n✅ ERP-157 动态销售订单出货/财务进度字段设计器 UI 逻辑测试全部通过（' + passed + ' 项断言）。\n');

