'use strict';
/* ERP-136 前端 UI 逻辑单测：字段选择、请求边界、安全单元格渲染、未知库龄 / 未知成本 / CNY 口径、固定库龄分层顺序、
   CSV 公式转义与当前页语义、失败态，以及前端接线契约（无任意 SQL）。
   运行：node tests/automation/dynamic_inventory_aging_report_ui.test.js
   失败时抛异常并以非零码退出。 */

const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const JS_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'inventory-aging-report.js');

let passed = 0;
function ok(cond, msg) { assert.ok(cond, msg); passed += 1; }
function eq(actual, expected, msg) { assert.deepStrictEqual(actual, expected, msg); passed += 1; }

const mod = require(JS_PATH);

// ---- 1. 固定库龄分层顺序（0-30 / 31-60 / 61-90 / 91-180 / 180+，绝不重排） ----
eq(mod.IAR_DYN_BUCKET_KEYS, ['bucket0To30', 'bucket31To60', 'bucket61To90', 'bucket91To180', 'bucketOver180'],
  '固定 5 格库龄分层顺序');

// ---- 2. 字段选择（只来自目录白名单，去重、保持顺序、丢弃未知键） ----
const catalogFields = [
  { key: 'warehouseId', label: '仓库Id', dataType: 'number', filterable: true },
  { key: 'productCode', label: '商品编码', dataType: 'text', filterable: false },
  { key: 'currentQuantity', label: '当前现存量', dataType: 'number', filterable: false },
  { key: 'unknownAgeQuantity', label: '库龄未知数量', dataType: 'number', filterable: false },
  { key: 'evidenceStatus', label: '库龄依据', dataType: 'enum', filterable: false },
  { key: 'averageCost', label: '移动加权平均成本', dataType: 'number', filterable: false },
  { key: 'costStatus', label: '成本状态', dataType: 'enum', filterable: false },
  { key: 'costCurrency', label: '成本币种', dataType: 'text', filterable: false },
  { key: 'authoritativeAmount', label: '权威库存金额', dataType: 'number', filterable: false },
  { key: 'bucket0To30Amount', label: '0-30天金额', dataType: 'number', filterable: false },
  { key: 'note', label: '口径说明', dataType: 'text', filterable: false },
];
eq(mod.iarDynSelectFields(catalogFields, ['productCode', 'productCode', 'bogus', ' averageCost ']),
  ['productCode', 'averageCost'], '字段选择去重并丢弃未知键');

// ---- 3. 请求边界（仅选定字段、分页有界、筛选只含仓库/商品/截止日期，无任意字段名） ----
const req = mod.iarDynBuildRequest({
  catalogFields,
  selectedKeys: ['productCode', 'authoritativeAmount', 'productCode', 'hacked'],
  warehouseId: '42',
  productId: '7',
  asOfDate: '2026-09-24',
  page: 0,
  pageSize: 9999,
  maxPageSize: 200,
});
eq(req.fields, ['productCode', 'authoritativeAmount'], '请求只含选定白名单字段');
eq(req.page, 1, '页码最小 1');
eq(req.pageSize, 200, '每页钳制到上限 200');
eq(req.warehouseId, 42);
eq(req.productId, 7);
eq(req.asOfDate, '2026-09-24');
ok(!('keyword' in req) && !('onlyPositiveQuantity' in req), '不携带关键字 / 仅正向等扩展筛选');
ok(!req.fields.includes('hacked'), '未知字段绝不进入请求');

const emptyReq = mod.iarDynBuildRequest({ catalogFields, selectedKeys: [], page: 2, pageSize: 20, maxPageSize: 200 });
eq(emptyReq.fields, [], '空选择 = 空字段列表（后端返回全部白名单）');

// ---- 4. 单元格文本（未知成本金额 = 未知不回落 0、未知库龄依据枚举、CNY 币种、数字格式化、普通 null 为空） ----
eq(mod.iarDynCellText(null, { key: 'authoritativeAmount', dataType: 'number' }), '未知', '未知成本金额显示未知');
eq(mod.iarDynCellText(null, { key: 'averageCost', dataType: 'number' }), '未知', '未知成本单价显示未知');
eq(mod.iarDynCellText(null, { key: 'bucket0To30Amount', dataType: 'number' }), '未知', '未知分层金额显示未知');
eq(mod.iarDynCellText('none', { key: 'evidenceStatus', dataType: 'enum' }), '无台账分层依据', '无台账依据显示未知库龄证据');
eq(mod.iarDynCellText('unknown', { key: 'costStatus', dataType: 'enum' }), '成本未知');
eq(mod.iarDynCellText('CNY', { key: 'costCurrency', dataType: 'text' }), 'CNY', '成本币种显式显示 CNY');
eq(mod.iarDynCellText(920001, { key: 'warehouseId', dataType: 'number' }), '920001', '整数 Id 不格式化小数');
eq(mod.iarDynCellText(12, { key: 'currentQuantity', dataType: 'number' }), '12');
eq(mod.iarDynCellText(10.5, { key: 'averageCost', dataType: 'number' }), '10.50', '小数金额两位');
eq(mod.iarDynCellText(null, { key: 'note', dataType: 'text' }), '', '普通字段 null 显示为空');

// ---- 5. 安全单元格渲染（转义） ----
eq(mod.iarDynRenderCell('<script>alert(1)</script>', { key: 'note', dataType: 'text' }),
  '&lt;script&gt;alert(1)&lt;/script&gt;', '注入标签被转义');

// ---- 6. 表格渲染列名与单元格（未知成本、CNY、转义） ----
const view = {
  columns: [
    { key: 'productCode', label: '商品编码', dataType: 'text' },
    { key: 'authoritativeAmount', label: '权威库存金额', dataType: 'number' },
    { key: 'costCurrency', label: '成本币种', dataType: 'text' },
  ],
  rows: [
    { productCode: 'P001', authoritativeAmount: 120, costCurrency: 'CNY' },
    { productCode: '<b>P002</b>', authoritativeAmount: null, costCurrency: 'CNY' },
  ],
  total: 2, page: 1, pageSize: 50, totalPages: 1, costCurrency: 'CNY',
};
const tableHtml = mod.iarDynTableHtml(view);
ok(tableHtml.includes('<th>商品编码</th>'), '渲染返回的列名');
ok(tableHtml.includes('>权威库存金额</th>'), '渲染返回的列名（数字列右对齐）');
ok(tableHtml.includes('>P001<'), '渲染单元格值');
ok(tableHtml.includes('&lt;b&gt;P002&lt;/b&gt;'), '单元格值被转义');
ok(!tableHtml.includes('<b>P002</b>'), '未转义标签不出现');
ok(tableHtml.includes('未知'), '未知成本金额在表格中显示未知');
ok(tableHtml.includes('CNY'), 'CNY 币种显式显示');

// ---- 7. 空结果与口径文案 ----
const emptyView = {
  columns: [{ key: 'productCode', label: '商品编码', dataType: 'text' }],
  rows: [], total: 0, page: 1, pageSize: 50, totalPages: 0, costCurrency: 'CNY',
  readOnlyText: '只读', boundaryText: '边界', disclaimerText: '免责',
};
const emptyHtml = mod.iarDynResultHtml(emptyView);
ok(emptyHtml.includes('没有符合条件'), '空结果可见');
ok(emptyHtml.includes('只读') && emptyHtml.includes('边界') && emptyHtml.includes('免责'), '口径文案可见');

// ---- 8. 失败态（授权 / 未登录 / 无效 / 网络） ----
ok(mod.iarDynErrorHtml('forbidden', '无库存查询菜单授权').includes('权限不足'), '授权失败可见');
ok(mod.iarDynErrorHtml('unauthorized', '请先登录').includes('未登录'), '未登录可见');
ok(mod.iarDynErrorHtml('invalid', '未知字段').includes('请求无效'), '无效请求可见');
ok(mod.iarDynErrorHtml('network', 'fetch failed').includes('网络请求失败'), '网络失败可见');

// ---- 9. 当前页 CSV：选定列顺序 + 公式前导转义 + 未知值保留 ----
const csv = mod.iarDynCsv({
  columns: [
    { key: 'productCode', label: '商品编码', dataType: 'text' },
    { key: 'note', label: '口径说明', dataType: 'text' },
    { key: 'authoritativeAmount', label: '权威库存金额', dataType: 'number' },
  ],
  rows: [
    { productCode: 'P001', note: '=HYPERLINK("http://evil")', authoritativeAmount: null },
    { productCode: 'P002', note: '普通文本', authoritativeAmount: 120 },
  ],
});
ok(csv.indexOf('商品编码') < csv.indexOf('口径说明') && csv.indexOf('口径说明') < csv.indexOf('权威库存金额'),
  'CSV 按选定列顺序');
ok(csv.includes('"\'='), '公式前导文本被单引号转义');
ok(!csv.includes('"='), '公式前导不直接以等号开头');
ok(csv.includes('未知'), '未知成本金额被保留');

// ---- 10. 前端接线契约（接口路径、复选框字段选择器、无任意 SQL / 自由字段名输入） ----
const jsSrc = fs.readFileSync(JS_PATH, 'utf8');
ok(jsSrc.includes('/api/dynamic-inventory-aging-report'), '调用 ERP-135 只读预览接口');
ok(jsSrc.includes('name="iar-dyn-field"') && jsSrc.includes('type="checkbox"'), '字段选择器为复选框，无自由字段名输入');
ok(!/FromSql|ExecuteSql|SqlCommand|SELECT\s+\*/i.test(jsSrc), '不执行任意 SQL');

console.log('\n✅ ERP-136 库存库龄字段设计器 UI 逻辑测试全部通过（' + passed + ' 项断言）。\n');
