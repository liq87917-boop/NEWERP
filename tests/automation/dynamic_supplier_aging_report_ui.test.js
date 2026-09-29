'use strict';
/* ERP-141 前端 UI 逻辑单测：字段选择、请求边界、未知证据渲染、失败态、所选列 CSV（未知保留 + 公式转义），以及前端接线契约。
   运行：node tests/automation/dynamic_supplier_aging_report_ui.test.js
   失败时抛异常并以非零码退出。 */

const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const JS_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'supplier-reconciliation-aging.js');

let passed = 0;
function ok(cond, msg) { assert.ok(cond, msg); passed += 1; }
function eq(actual, expected, msg) { assert.deepStrictEqual(actual, expected, msg); passed += 1; }

const mod = require(JS_PATH);

// ---- 1. 字段选择（只来自目录白名单，去重、保持顺序、丢弃未知键） ----
const catalogFields = [
  { key: 'invoiceId', label: '发票Id', dataType: 'number', filterable: false },
  { key: 'supplierName', label: '供应商名称', dataType: 'text', filterable: false },
  { key: 'currency', label: '币种', dataType: 'text', filterable: true },
  { key: 'grossAmount', label: '含税总额', dataType: 'number', filterable: false },
  { key: 'dueDate', label: '显式到期日', dataType: 'date', filterable: true },
  { key: 'remainingAmount', label: '算术剩余证据', dataType: 'number', filterable: false },
];
eq(mod.sraDesSelectFields(catalogFields, ['supplierName', 'supplierName', 'bogus', ' currency ']),
  ['supplierName', 'currency'], '字段选择去重并丢弃未知键');

// ---- 2. 请求边界（仅选定字段、分页有界、筛选仅复用工作台当前筛选，无任意字段名） ----
const req = mod.sraDesBuildRequest({
  catalogFields,
  selectedKeys: ['supplierName', 'currency', 'grossAmount', 'hacked'],
  supplierId: '42',
  currency: 'USD',
  invoiceStatus: 'recorded',
  allocationState: 'partial',
  invoiceDateFrom: '2026-09-01T00:00:00',
  invoiceDateTo: '2026-09-30',
  dueDateFrom: '2026-10-01',
  dueDateTo: '2026-10-31',
  asOfDate: '2026-09-24T00:00:00',
  keyword: 'INV',
  page: 0,
  pageSize: 9999,
  maxPageSize: 200,
});
eq(req.fields, ['supplierName', 'currency', 'grossAmount'], '请求只含选定白名单字段');
eq(req.page, 1, '页码最小 1');
eq(req.pageSize, 200, '每页钳制到上限 200');
eq(req.supplierId, 42, '供应商 Id 转数字');
eq(req.currency, 'USD');
eq(req.invoiceStatus, 'recorded');
eq(req.allocationState, 'partial');
eq(req.invoiceDateFrom, '2026-09-01', '开票日期只取日期部分');
eq(req.invoiceDateTo, '2026-09-30');
eq(req.dueDateFrom, '2026-10-01');
eq(req.dueDateTo, '2026-10-31');
eq(req.asOfDate, '2026-09-24');
eq(req.keyword, 'INV');
ok(!req.fields.includes('hacked'), '未知字段绝不进入请求');

const emptyReq = mod.sraDesBuildRequest({ catalogFields, selectedKeys: [], page: 2, pageSize: 20, maxPageSize: 200 });
eq(emptyReq.fields, [], '空选择 = 空字段列表');
ok(!('supplierId' in emptyReq) && !('currency' in emptyReq) && !('invoiceStatus' in emptyReq), '空筛选不携带多余键');
eq(emptyReq.page, 2, '页码保持（≥1）');
eq(emptyReq.pageSize, 20, '每页条数保持（≤上限）');

// ---- 3. 未知证据渲染（null/undefined → 「未知」，绝不回落为 0；布尔、日期、币种） ----
eq(mod.sraDesCellText(null, { key: 'remainingAmount', dataType: 'number' }), '未知', '未知剩余证据不回落 0');
eq(mod.sraDesCellText(undefined, { key: 'dueDate', dataType: 'date' }), '未知', '未知到期日不回落');
eq(mod.sraDesCellText(null, { key: 'overdueDays', dataType: 'number' }), '未知', '未知逾期天数');
eq(mod.sraDesCellText(true, { key: 'dueDateKnown', dataType: 'boolean' }), '是', '布尔真 → 是');
eq(mod.sraDesCellText(false, { key: 'dueDateKnown', dataType: 'boolean' }), '否', '布尔假 → 否');
eq(mod.sraDesCellText('2026-09-24T00:00:00', { key: 'invoiceDate', dataType: 'date' }), '2026-09-24', '日期截断到日');
eq(mod.sraDesCellText('CNY', { key: 'currency', dataType: 'text' }), 'CNY', '币种原样显示');
eq(mod.sraDesCellText(100, { key: 'grossAmount', dataType: 'number' }), '100', '数字原样显示');

// ---- 4. 安全单元格渲染（转义） ----
eq(mod.sraDesRenderCell('<script>alert(1)</script>', { key: 'note', dataType: 'text' }),
  '&lt;script&gt;alert(1)&lt;/script&gt;', '注入标签被转义');
// ---- 5. 表格渲染列名与单元格（未知 + 币种 + 转义） ----
const view = {
  columns: [
    { key: 'supplierName', label: '供应商名称', dataType: 'text', filterable: false },
    { key: 'currency', label: '币种', dataType: 'text', filterable: true },
    { key: 'dueDate', label: '显式到期日', dataType: 'date', filterable: true },
    { key: 'remainingAmount', label: '算术剩余证据', dataType: 'number', filterable: false },
  ],
  rows: [
    { supplierName: '甲供应商', currency: 'CNY', dueDate: null, remainingAmount: null },
    { supplierName: '<b>乙供应商</b>', currency: 'USD', dueDate: '2026-09-30T00:00:00', remainingAmount: 50 },
  ],
  total: 2, page: 1, pageSize: 50, totalPages: 1,
  readOnlyText: '只读', boundaryText: '边界', disclaimerText: '免责',
};
const tableHtml = mod.sraDesTableHtml(view);
ok(tableHtml.includes('<th>供应商名称</th>'), '渲染返回的列名');
ok(tableHtml.includes('>算术剩余证据</th>'), '渲染返回的列名（数字列右对齐）');
ok(tableHtml.includes('>未知</td>'), '未知到期日 / 未知剩余渲染为「未知」');
ok(tableHtml.includes('>CNY</td>') && tableHtml.includes('>USD</td>'), '不同币种分别成行、绝不合并');
ok(tableHtml.includes('&lt;b&gt;乙供应商&lt;/b&gt;'), '单元格值被转义');
ok(!tableHtml.includes('<b>乙供应商</b>'), '未转义标签不出现');

const resultHtml = mod.sraDesResultHtml(view);
ok(resultHtml.includes('只读') && resultHtml.includes('边界') && resultHtml.includes('免责'), '口径文案可见');
ok(resultHtml.includes('共 2 条'), '汇总可见');

// ---- 6. 空结果与失败态 ----
const emptyView = {
  columns: [{ key: 'supplierName', label: '供应商名称', dataType: 'text' }],
  rows: [], total: 0, page: 1, pageSize: 50, totalPages: 0,
  readOnlyText: '只读', boundaryText: '边界', disclaimerText: '免责',
};
const emptyHtml = mod.sraDesResultHtml(emptyView);
ok(emptyHtml.includes('没有符合条件的供应商发票证据'), '空结果可见');
ok(mod.sraDesErrorHtml('forbidden', '无采购订单菜单授权').includes('权限不足'), '授权失败可见');
ok(mod.sraDesErrorHtml('unauthorized', '请先登录').includes('未登录'), '未登录可见');
ok(mod.sraDesErrorHtml('network', 'fetch failed').includes('网络请求失败'), '网络失败可见');
ok(mod.sraDesErrorHtml('invalid', '未知字段').includes('请求无效'), '无效请求可见');
eq(mod.sraDesKindOfCode(2002), 'forbidden', '2002 → 权限不足');
eq(mod.sraDesKindOfCode(2000), 'unauthorized', '2000 → 未登录');
eq(mod.sraDesKindOfCode(2003), 'unauthorized', '2003 → 未登录');

// ---- 7. 所选列 CSV（未知保留 + 公式转义 + 引号转义） ----
const csvView = {
  columns: [
    { key: 'supplierName', label: '供应商名称', dataType: 'text' },
    { key: 'currency', label: '币种', dataType: 'text' },
    { key: 'dueDate', label: '显式到期日', dataType: 'date' },
    { key: 'remainingAmount', label: '算术剩余证据', dataType: 'number' },
    { key: 'note', label: '行级说明', dataType: 'text' },
  ],
  rows: [
    { supplierName: '甲供应商', currency: 'CNY', dueDate: null, remainingAmount: null, note: '=cmd|calc' },
    { supplierName: '-SUM(A1)', currency: 'USD', dueDate: '2026-09-30T00:00:00', remainingAmount: 50, note: '他说"好"' },
  ],
  total: 2, page: 1, pageSize: 50, totalPages: 1,
};
const csv = mod.sraDesCsv(csvView);
ok(csv.startsWith('\uFEFF'), 'CSV 带 BOM');
ok(csv.includes('"供应商名称","币种","显式到期日","算术剩余证据","行级说明"'), '表头为返回的列名');
ok(csv.includes('"未知"'), '未知值保留为「未知」（不回落 0）');
ok(csv.includes("'=cmd|calc"), '以 = 开头文本被转义');
ok(csv.includes("'-SUM(A1)"), '以 - 开头文本被转义');
ok(csv.includes('他说""好""'), '双引号被转义');

// ---- 8. 前端接线契约（入口、脚本、接口路径、复选框、无任意 SQL / 自由字段名输入） ----
const jsSrc = fs.readFileSync(JS_PATH, 'utf8');
ok(jsSrc.includes('function openSupplierAgingDesigner('), '注册设计器入口函数');
ok(jsSrc.includes('/api/supplier-reconciliation-aging/report'), '调用 ERP-140 只读接口');
ok(jsSrc.includes('name="sra-des-field"') && jsSrc.includes('type="checkbox"'), '字段选择器为复选框，无自由字段名输入');
ok(!/FromSql|ExecuteSql|SqlCommand|SELECT\s+\*/i.test(jsSrc), '不执行任意 SQL');
ok(!/GrandTotal|跨币种合计|Grand\s+Total/i.test(jsSrc), '无跨币种总额');

console.log('\n✅ ERP-141 动态供应商对账与账龄 UI 逻辑测试全部通过（' + passed + ' 项断言）。\n');

