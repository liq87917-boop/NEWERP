'use strict';
/* ERP-149 前端 UI 逻辑单测：字段选择、请求边界、未知证据渲染、失败态、所选列 CSV（未知保留 + 公式转义），以及前端接线契约。
   运行：node tests/automation/dynamic_supplier_exposure_ui.test.js
   失败时抛异常并以非零码退出。 */

const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const JS_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'supplier-purchase-exposure.js');

let passed = 0;
function ok(cond, msg) { assert.ok(cond, msg); passed += 1; }
function eq(actual, expected, msg) { assert.deepStrictEqual(actual, expected, msg); passed += 1; }

const mod = require(JS_PATH);

// ---- 1. 字段选择（只来自目录白名单，去重、保持顺序、丢弃未知键） ----
const catalogFields = [
  { key: 'orderNo', label: '采购单号', dataType: 'text', filterable: false },
  { key: 'supplierName', label: '供应商名称', dataType: 'text', filterable: false },
  { key: 'currency', label: '币种', dataType: 'text', filterable: true },
  { key: 'orderedAmount', label: '订单金额', dataType: 'number', filterable: false },
  { key: 'linkStatus', label: '链接状态', dataType: 'text', filterable: true },
  { key: 'settledAmount', label: '已结算金额', dataType: 'number', filterable: false },
  { key: 'receiptStatus', label: '收货状态', dataType: 'text', filterable: false },
];
eq(mod.speDesSelectFields(catalogFields, ['supplierName', 'supplierName', 'bogus', ' currency ']),
  ['supplierName', 'currency'], '字段选择去重并丢弃未知键');

// ---- 2. 请求边界（仅选定字段、分页有界、筛选仅复用工作台当前筛选，无任意字段名） ----
const req = mod.speDesBuildRequest({
  catalogFields,
  selectedKeys: ['supplierName', 'currency', 'orderedAmount', 'hacked'],
  supplierId: '42',
  currency: 'USD',
  linkStatus: 'linked',
  orderDateFrom: '2026-09-01T00:00:00',
  orderDateTo: '2026-09-30',
  keyword: 'PO',
  page: 0,
  pageSize: 9999,
  maxPageSize: 200,
});
eq(req.fields, ['supplierName', 'currency', 'orderedAmount'], '请求只含选定白名单字段');
eq(req.page, 1, '页码最小 1');
eq(req.pageSize, 200, '每页钳制到上限 200');
eq(req.supplierId, 42, '供应商 Id 转数字');
eq(req.currency, 'USD');
eq(req.linkStatus, 'linked');
eq(req.orderDateFrom, '2026-09-01', '订单日期只取日期部分');
eq(req.orderDateTo, '2026-09-30');
eq(req.keyword, 'PO');
ok(!req.fields.includes('hacked'), '未知字段绝不进入请求');

const emptyReq = mod.speDesBuildRequest({ catalogFields, selectedKeys: [], page: 2, pageSize: 20, maxPageSize: 200 });
eq(emptyReq.fields, [], '空选择 = 空字段列表');
ok(!('supplierId' in emptyReq) && !('currency' in emptyReq) && !('linkStatus' in emptyReq), '空筛选不携带多余键');
eq(emptyReq.page, 2, '页码保持（≥1）');
eq(emptyReq.pageSize, 20, '每页条数保持（≤上限）');

// ---- 3. 未知证据渲染（null/undefined → 「未知」，绝不回落为 0；布尔、日期、链接 / 收货状态） ----
eq(mod.speDesCellText(null, { key: 'settledAmount', dataType: 'number' }), '未知', '未知已结算不回落 0');
eq(mod.speDesCellText(undefined, { key: 'outstandingAmount', dataType: 'number' }), '未知', '未知未结算不回落 0');
eq(mod.speDesCellText(null, { key: 'receivedQuantity', dataType: 'number' }), '未知', '未知收货数量不回落 0');
eq(mod.speDesCellText(true, { key: 'overSettled', dataType: 'boolean' }), '是', '布尔真 → 是');
eq(mod.speDesCellText(false, { key: 'overSettled', dataType: 'boolean' }), '否', '布尔假 → 否');
eq(mod.speDesCellText('2026-09-24T00:00:00', { key: 'orderDate', dataType: 'date' }), '2026-09-24', '日期截断到日');
eq(mod.speDesCellText('linked', { key: 'linkStatus', dataType: 'text' }), '链接可用', '链接状态中文');
eq(mod.speDesCellText('ambiguous', { key: 'linkStatus', dataType: 'text' }), '链接不唯一（金额未知）', '链接不唯一中文');
eq(mod.speDesCellText('unavailable', { key: 'linkStatus', dataType: 'text' }), '无可用链接（金额未知）', '链接缺失中文');
eq(mod.speDesCellText('unknown', { key: 'receiptStatus', dataType: 'text' }), '未知（超出派生上限）', '收货未知中文');
eq(mod.speDesCellText('CNY', { key: 'currency', dataType: 'text' }), 'CNY', '币种原样显示');
eq(mod.speDesCellText(100, { key: 'orderedAmount', dataType: 'number' }), '100', '数字原样显示');

// ---- 4. 表格渲染（列名、转义、未知、不同币种分行、分页接线） ----
const view = {
  columns: [
    { key: 'supplierName', label: '供应商名称', dataType: 'text', filterable: false },
    { key: 'currency', label: '币种', dataType: 'text', filterable: true },
    { key: 'orderedAmount', label: '订单金额', dataType: 'number', filterable: false },
    { key: 'linkStatus', label: '链接状态', dataType: 'text', filterable: true },
    { key: 'settledAmount', label: '已结算金额', dataType: 'number', filterable: false },
    { key: 'receivedQuantity', label: '已收数量', dataType: 'number', filterable: false },
  ],
  rows: [
    { supplierName: '甲供应商', currency: 'CNY', orderedAmount: 100, linkStatus: 'linked', settledAmount: null, receivedQuantity: 10 },
    { supplierName: '<b>乙供应商</b>', currency: 'USD', orderedAmount: 50, linkStatus: 'ambiguous', settledAmount: null, receivedQuantity: null },
    { supplierName: '丙供应商', currency: 'USD', orderedAmount: 30, linkStatus: 'unavailable', settledAmount: null, receivedQuantity: null },
  ],
  total: 3, page: 1, pageSize: 50, totalPages: 1,
  readOnlyText: '只读', boundaryText: '边界', disclaimerText: '免责',
};
const tableHtml = mod.speDesTableHtml(view);
ok(tableHtml.includes('<th>供应商名称</th>'), '渲染返回的列名');
ok(tableHtml.includes('<th class="text-right">订单金额</th>'), '数字列右对齐');
ok(tableHtml.includes('>未知</td>'), '未知结算 / 收货渲染为「未知」');
ok(tableHtml.includes('>CNY</td>') && tableHtml.includes('>USD</td>'), '不同币种分别成行、绝不合并');
ok(tableHtml.includes('链接不唯一（金额未知）') && tableHtml.includes('无可用链接（金额未知）'), '链接不唯一 / 无引用中文可见');
ok(tableHtml.includes('&lt;b&gt;乙供应商&lt;/b&gt;'), '单元格值被转义');
ok(!tableHtml.includes('<b>乙供应商</b>'), '未转义标签不出现');
ok(tableHtml.includes('speDesPage(-1)') && tableHtml.includes('speDesPage(1)'), '分页按钮接线');

const resultHtml = mod.speDesResultHtml(view);
ok(resultHtml.includes('只读') && resultHtml.includes('边界') && resultHtml.includes('免责'), '口径文案可见');
ok(resultHtml.includes('共 3 条'), '汇总可见');

// ---- 5. 空结果与失败态 ----
const emptyView = {
  columns: [{ key: 'supplierName', label: '供应商名称', dataType: 'text', filterable: false }],
  rows: [], total: 0, page: 1, pageSize: 50, totalPages: 0,
  readOnlyText: '只读', boundaryText: '边界', disclaimerText: '免责',
};
const emptyHtml = mod.speDesResultHtml(emptyView);
ok(emptyHtml.includes('没有符合条件的采购订单敞口证据'), '空结果可见');
ok(mod.speDesErrorHtml('forbidden', '无采购订单菜单授权').includes('权限不足'), '授权失败可见');
ok(mod.speDesErrorHtml('unauthorized', '请先登录').includes('未登录'), '未登录可见');
ok(mod.speDesErrorHtml('network', 'fetch failed').includes('网络请求失败'), '网络失败可见');
ok(mod.speDesErrorHtml('invalid', '未知字段').includes('请求无效'), '无效请求可见');
eq(mod.speDesKindOfCode(2002), 'forbidden', '2002 → 权限不足');
eq(mod.speDesKindOfCode(2000), 'unauthorized', '2000 → 未登录');
eq(mod.speDesKindOfCode(2003), 'unauthorized', '2003 → 未登录');

// ---- 6. 所选列 CSV（未知保留 + 公式转义 + 引号转义） ----
const csvView = {
  columns: [
    { key: 'supplierName', label: '供应商名称', dataType: 'text', filterable: false },
    { key: 'currency', label: '币种', dataType: 'text', filterable: true },
    { key: 'settledAmount', label: '已结算金额', dataType: 'number', filterable: false },
    { key: 'note', label: '说明', dataType: 'text', filterable: false },
  ],
  rows: [
    { supplierName: '甲供应商', currency: 'CNY', settledAmount: null, note: '=cmd|calc' },
    { supplierName: '-SUM(A1)', currency: 'USD', settledAmount: 50, note: '他说"好"' },
  ],
  total: 2, page: 1, pageSize: 50, totalPages: 1,
};
const csv = mod.speDesCsv(csvView);
ok(csv.startsWith('\uFEFF'), 'CSV 带 BOM');
ok(csv.includes('"供应商名称","币种","已结算金额","说明"'), '表头为返回的列名');
ok(csv.includes('"未知"'), '未知值保留为「未知」（不回落 0）');
ok(csv.includes("'=cmd|calc"), '以 = 开头文本被转义');
ok(csv.includes("'-SUM(A1)"), '以 - 开头文本被转义');
ok(csv.includes('他说""好""'), '双引号被转义');

// ---- 7. 前端接线契约（入口、脚本、接口路径、复选框、无任意 SQL / 自由字段名输入） ----
const jsSrc = fs.readFileSync(JS_PATH, 'utf8');
ok(jsSrc.includes('function openSupplierPurchaseExposureDesigner('), '注册设计器入口函数');
ok(jsSrc.includes('/api/supplier-purchase-exposure/report'), '调用 ERP-148 只读接口');
ok(jsSrc.includes('name="spe-des-field"') && jsSrc.includes('type="checkbox"'), '字段选择器为复选框，无自由字段名输入');
ok(!/FromSql|ExecuteSql|SqlCommand|SELECT\s+\*/i.test(jsSrc), '不执行任意 SQL');
ok(!/GrandTotal|跨币种合计|Grand\s+Total/i.test(jsSrc), '无跨币种总额');

console.log('\n✅ ERP-149 动态供应商采购敞口 UI 逻辑测试全部通过（' + passed + ' 项断言）。\n');

