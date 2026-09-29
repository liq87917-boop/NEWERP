'use strict';
/* ERP-118 前端 UI 逻辑单测：字段选择、请求边界、安全单元格渲染、剩余证据状态标注、失败态，以及前端接线契约。
   运行：node tests/automation/dynamic_receivable_report_ui.test.js
   失败时抛异常并以非零码退出。 */

const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const JS_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'dynamic-receivable-report.js');
const CSI_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'customer-sales-invoices.js');
const INDEX_HTML = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'index.html');

let passed = 0;
function ok(cond, msg) { assert.ok(cond, msg); passed += 1; }
function eq(actual, expected, msg) { assert.deepStrictEqual(actual, expected, msg); passed += 1; }

const mod = require(JS_PATH);

// ---- 1. 字段选择（只来自目录白名单，去重、保持顺序、丢弃未知键） ----
const catalogFields = [
  { key: 'invoiceId', label: '发票Id', dataType: 'number', filterable: false },
  { key: 'invoiceNumber', label: '发票号码', dataType: 'text', filterable: false },
  { key: 'invoiceDate', label: '开票日期', dataType: 'date', filterable: true },
  { key: 'customerId', label: '客户Id', dataType: 'number', filterable: true },
  { key: 'remainingState', label: '剩余证据状态', dataType: 'text', filterable: false },
];
eq(mod.dsrSelectFields(catalogFields, ['invoiceNumber', 'invoiceNumber', 'bogus', ' invoiceDate ']),
  ['invoiceNumber', 'invoiceDate'], '字段选择去重并丢弃未知键');

// ---- 2. 请求边界（仅选定字段、分页有界、筛选只含客户/日期/币种/分配状态/发票状态，无任意字段名） ----
const req = mod.dsrBuildRequest({
  catalogFields,
  selectedKeys: ['invoiceNumber', 'invoiceDate', 'invoiceNumber', 'hacked'],
  startDate: '2026-09-01T00:00:00',
  endDate: '2026-09-30',
  customerId: '42',
  currency: 'USD',
  allocationState: 'partial',
  invoiceStatus: 'recorded',
  page: 0,
  pageSize: 9999,
  maxPageSize: 100,
});
eq(req.fields, ['invoiceNumber', 'invoiceDate'], '请求只含选定白名单字段');
eq(req.page, 1, '页码最小 1');
eq(req.pageSize, 100, '每页钳制到上限');
eq(req.startDate, '2026-09-01', '开始日期只取日期部分');
eq(req.endDate, '2026-09-30');
eq(req.customerId, 42, '客户 Id 转为正整数');
eq(req.currency, 'USD');
eq(req.allocationState, 'partial');
eq(req.invoiceStatus, 'recorded');
ok(!req.fields.includes('hacked'), '未知字段绝不进入请求');

const emptyReq = mod.dsrBuildRequest({ catalogFields, selectedKeys: [], page: 2, pageSize: 20, maxPageSize: 100 });
eq(emptyReq.fields, [], '空选择 = 空字段列表（后端返回全部白名单）');
ok(!('customerId' in emptyReq) && !('currency' in emptyReq) && !('allocationState' in emptyReq) && !('invoiceStatus' in emptyReq),
  '空筛选不携带多余键');

const invalidReq = mod.dsrBuildRequest({
  catalogFields, selectedKeys: ['invoiceNumber'],
  currency: 'XXX', allocationState: 'bogus', invoiceStatus: 'bogus',
  page: 1, pageSize: 20, maxPageSize: 100,
});
ok(!('currency' in invalidReq) && !('allocationState' in invalidReq) && !('invoiceStatus' in invalidReq),
  '非法币种 / 分配状态 / 发票状态取值绝不进入请求（fail closed）');

// ---- 3. 安全单元格渲染（转义、null 为空、布尔 是/否、日期截断、剩余证据 / 分配状态映射） ----
eq(mod.dsrCellText(null, { key: 'identityText', dataType: 'text' }), '', 'null 显示为空（不回落 0）');
eq(mod.dsrCellText(undefined, { key: 'identityText', dataType: 'text' }), '', 'undefined 显示为空');
eq(mod.dsrCellText(true, { key: 'isDraft', dataType: 'boolean' }), '是');
eq(mod.dsrCellText(false, { key: 'isVoided', dataType: 'boolean' }), '否');
eq(mod.dsrCellText('2026-09-01T00:00:00', { key: 'invoiceDate', dataType: 'date' }), '2026-09-01');
eq(mod.dsrCellText('known', { key: 'remainingState', dataType: 'text' }), '剩余可确认');
eq(mod.dsrCellText('unknown', { key: 'remainingState', dataType: 'text' }), '剩余未知');
eq(mod.dsrCellText('over_allocated', { key: 'remainingState', dataType: 'text' }), '超额分摊（无效）');
eq(mod.dsrCellText('partial', { key: 'allocationState', dataType: 'text' }), '部分分摊');
eq(mod.dsrRenderCell('<script>alert(1)</script>', { key: 'identityText', dataType: 'text' }),
  '&lt;script&gt;alert(1)&lt;/script&gt;', '注入标签被转义');

// ---- 4. 剩余证据状态着色标注（known 绿 / unknown 黄 / over_allocated 红） ----
ok(mod.dsrRenderCell('known', { key: 'remainingState', dataType: 'text' }).includes('status-success'), 'known 绿色标注');
ok(mod.dsrRenderCell('unknown', { key: 'remainingState', dataType: 'text' }).includes('status-warning'), 'unknown 黄色标注');
ok(mod.dsrRenderCell('over_allocated', { key: 'remainingState', dataType: 'text' }).includes('status-danger'), 'over_allocated 红色标注');
// ---- 5. 表格渲染列名与单元格（转义） + 剩余证据标注 ----
const view = {
  columns: [
    { key: 'identityText', label: '发票身份', dataType: 'text', filterable: false },
    { key: 'grossAmount', label: '发票含税总额', dataType: 'number', filterable: false },
    { key: 'remainingState', label: '剩余证据状态', dataType: 'text', filterable: false },
  ],
  rows: [
    { identityText: 'INV-001', grossAmount: 100, remainingState: 'known' },
    { identityText: '<b>INV-002</b>', grossAmount: null, remainingState: 'unknown' },
  ],
  total: 2, page: 1, pageSize: 20, totalPages: 1,
};
const tableHtml = mod.dsrTableHtml(view);
ok(tableHtml.includes('<th>发票身份</th>'), '渲染返回的列名');
ok(tableHtml.includes('>发票含税总额</th>'), '渲染返回的列名（数字列右对齐）');
ok(tableHtml.includes('>INV-001<'), '渲染单元格值');
ok(tableHtml.includes('&lt;b&gt;INV-002&lt;/b&gt;'), '单元格值被转义');
ok(!tableHtml.includes('<b>INV-002</b>'), '未转义标签不出现');
ok(tableHtml.includes('status-success'), 'known 剩余证据着色标注');
ok(tableHtml.includes('剩余未知'), 'unknown 剩余证据文案可见');

// ---- 6. 空结果与失败态 ----
const emptyView = {
  columns: [{ key: 'identityText', label: '发票身份', dataType: 'text' }],
  rows: [], total: 0, page: 1, pageSize: 20, totalPages: 0,
  readOnlyText: '只读', boundaryText: '边界', disclaimerText: '免责',
};
const emptyHtml = mod.dsrResultHtml(emptyView);
ok(emptyHtml.includes('没有符合条件'), '空结果可见');
ok(emptyHtml.includes('只读') && emptyHtml.includes('边界') && emptyHtml.includes('免责'), '口径文案可见');
ok(mod.dsrErrorHtml('forbidden', '无客户资料菜单授权').includes('权限不足'), '授权失败可见');
ok(mod.dsrErrorHtml('network', 'fetch failed').includes('网络请求失败'), '网络失败可见');
ok(mod.dsrErrorHtml('invalid', '未知字段').includes('请求无效'), '无效请求可见');
eq(mod.dsrKindOfCode(2002), 'forbidden', '2002 → 权限不足');
eq(mod.dsrKindOfCode(2000), 'unauthorized', '2000 → 未登录');

// ---- 7. 前端接线契约（登记册工具栏入口、脚本注册、接口路径、无任意 SQL / 自由字段名输入） ----
const csiSrc = fs.readFileSync(CSI_PATH, 'utf8');
ok(csiSrc.includes('openDynamicReceivableReport('), 'customer-sales-invoices.js 注册工具栏入口');
const indexHtml = fs.readFileSync(INDEX_HTML, 'utf8');
ok(indexHtml.includes('/js/dynamic-receivable-report.js'), 'index.html 注册脚本');
const jsSrc = fs.readFileSync(JS_PATH, 'utf8');
ok(jsSrc.includes('/api/dynamic-receivable-report'), '调用 ERP-117 只读接口');
ok(jsSrc.includes('name="dsr-field"') && jsSrc.includes('type="checkbox"'), '字段选择器为复选框，无自由字段名输入');
ok(!/FromSql|ExecuteSql|SqlCommand|SELECT\s+\*/i.test(jsSrc), '不执行任意 SQL');

console.log('\n✅ ERP-118 客户应收账款证据动态报表 UI 逻辑测试全部通过（' + passed + ' 项断言）。\n');

