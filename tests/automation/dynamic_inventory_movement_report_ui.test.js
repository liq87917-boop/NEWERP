'use strict';
/* ERP-131 前端 UI 逻辑单测：字段选择、请求边界、安全单元格渲染、未知历史 / 基础单位口径、CSV 公式转义与当前页语义、失败态，以及前端接线契约。
   运行：node tests/automation/dynamic_inventory_movement_report_ui.test.js
   失败时抛异常并以非零码退出。 */

const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const JS_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'inventory-movement-report.js');

let passed = 0;
function ok(cond, msg) { assert.ok(cond, msg); passed += 1; }
function eq(actual, expected, msg) { assert.deepStrictEqual(actual, expected, msg); passed += 1; }

const mod = require(JS_PATH);

// ---- 1. 字段选择（只来自目录白名单，去重、保持顺序、丢弃未知键） ----
const catalogFields = [
  { key: 'warehouseName', label: '仓库名称', dataType: 'text', filterable: false },
  { key: 'productCode', label: '商品编码', dataType: 'text', filterable: false },
  { key: 'unit', label: '基础单位', dataType: 'text', filterable: false },
  { key: 'currentQuantity', label: '当前现存量', dataType: 'number', filterable: false },
  { key: 'lastMovementDate', label: '最后移动日期', dataType: 'date', filterable: false },
  { key: 'inactivityDays', label: '停滞天数', dataType: 'number', filterable: false },
  { key: 'historyStatus', label: '台账状态', dataType: 'enum', filterable: false },
  { key: 'classification', label: '分类', dataType: 'enum', filterable: false },
];
eq(mod.imrDynSelectFields(catalogFields, ['productCode', 'productCode', 'bogus', ' unit ']),
  ['productCode', 'unit'], '字段选择去重并丢弃未知键');

// ---- 2. 请求边界（仅选定字段、分页有界、筛选只含仓库/商品/日期/阈值，无任意字段名） ----
const req = mod.imrDynBuildRequest({
  catalogFields,
  selectedKeys: ['productCode', 'lastMovementDate', 'productCode', 'hacked'],
  warehouseId: '42',
  productId: '7',
  asOfDate: '2026-09-24',
  windowStart: '2026-06-01',
  windowEnd: '2026-09-24',
  inactiveDays: '90',
  page: 0,
  pageSize: 9999,
  maxPageSize: 200,
});
eq(req.fields, ['productCode', 'lastMovementDate'], '请求只含选定白名单字段');
eq(req.page, 1, '页码最小 1');
eq(req.pageSize, 200, '每页钳制到上限 200');
eq(req.warehouseId, 42);
eq(req.productId, 7);
eq(req.asOfDate, '2026-09-24');
eq(req.windowStart, '2026-06-01');
eq(req.windowEnd, '2026-09-24');
eq(req.inactiveDays, 90);
ok(!('keyword' in req) && !('onlyPositiveQuantity' in req), '不携带关键字 / 仅正向等扩展筛选');
ok(!req.fields.includes('hacked'), '未知字段绝不进入请求');

const emptyReq = mod.imrDynBuildRequest({ catalogFields, selectedKeys: [], page: 2, pageSize: 20, maxPageSize: 200 });
eq(emptyReq.fields, [], '空选择 = 空字段列表（后端返回全部白名单）');

// ---- 3. 单元格文本（未知历史 = 未知不回落 0、普通 null 为空、日期截断、枚举映射中文） ----
eq(mod.imrDynCellText(null, { key: 'lastMovementDate', dataType: 'date' }), '未知', '无台账最后移动日期显示未知');
eq(mod.imrDynCellText(null, { key: 'inactivityDays', dataType: 'number' }), '未知', '无台账停滞天数显示未知');
eq(mod.imrDynCellText(null, { key: 'note', dataType: 'text' }), '', '普通字段 null 显示为空');
eq(mod.imrDynCellText(undefined, { key: 'spec', dataType: 'text' }), '', 'undefined 显示为空');
eq(mod.imrDynCellText('2026-09-24T00:00:00', { key: 'lastMovementDate', dataType: 'date' }), '2026-09-24', '日期截断到日');
eq(mod.imrDynCellText('ledger', { key: 'historyStatus', dataType: 'enum' }), '有台账（窗口内有移动）');
eq(mod.imrDynCellText('stagnant', { key: 'classification', dataType: 'enum' }), '呆滞');
eq(mod.imrDynCellText('active', { key: 'classification', dataType: 'enum' }), '正常流动');

// ---- 4. 安全单元格渲染（转义） ----
eq(mod.imrDynRenderCell('<script>alert(1)</script>', { key: 'note', dataType: 'text' }),
  '&lt;script&gt;alert(1)&lt;/script&gt;', '注入标签被转义');

// ---- 5. 表格渲染列名与单元格（基础单位列名保留、未知历史证据、转义） ----
const view = {
  columns: [
    { key: 'productCode', label: '商品编码', dataType: 'text', filterable: false },
    { key: 'unit', label: '基础单位', dataType: 'text', filterable: false },
    { key: 'currentQuantity', label: '当前现存量', dataType: 'number', filterable: false },
    { key: 'lastMovementDate', label: '最后移动日期', dataType: 'date', filterable: false },
  ],
  rows: [
    { productCode: 'P001', unit: '件', currentQuantity: 100, lastMovementDate: '2026-09-20T00:00:00' },
    { productCode: '<b>P002</b>', unit: '件', currentQuantity: 5, lastMovementDate: null },
  ],
  total: 2, page: 1, pageSize: 50, totalPages: 1,
};
const tableHtml = mod.imrDynTableHtml(view);
ok(tableHtml.includes('<th>商品编码</th>'), '渲染返回的列名');
ok(tableHtml.includes('<th>基础单位</th>'), '基础单位列名保留');
ok(tableHtml.includes('>P001<'), '渲染单元格值');
ok(tableHtml.includes('&lt;b&gt;P002&lt;/b&gt;'), '单元格值被转义');
ok(!tableHtml.includes('<b>P002</b>'), '未转义标签不出现');
ok(tableHtml.includes('未知'), '无台账最后移动日期在表格中显示未知');

// ---- 6. 空结果与口径文案 ----
const emptyView = {
  columns: [{ key: 'productCode', label: '商品编码', dataType: 'text' }],
  rows: [], total: 0, page: 1, pageSize: 50, totalPages: 0,
  readOnlyText: '只读', boundaryText: '边界', disclaimerText: '免责',
};
const emptyHtml = mod.imrDynResultHtml(emptyView);
ok(emptyHtml.includes('没有符合条件的库存行'), '空结果可见');
ok(emptyHtml.includes('只读') && emptyHtml.includes('边界') && emptyHtml.includes('免责'), '口径文案可见');

// ---- 7. 失败态（授权 / 未登录 / 无效 / 网络） ----
ok(mod.imrDynErrorHtml('forbidden', '无库存查询菜单授权').includes('权限不足'), '授权失败可见');
ok(mod.imrDynErrorHtml('unauthorized', '请先登录').includes('未登录'), '未登录可见');
ok(mod.imrDynErrorHtml('invalid', '未知字段').includes('请求无效'), '无效请求可见');
ok(mod.imrDynErrorHtml('network', 'fetch failed').includes('网络请求失败'), '网络失败可见');

// ---- 8. 当前页 CSV：选定列顺序 + 公式前导转义 + 未知值保留 ----
const csv = mod.imrDynCsv({
  columns: [
    { key: 'productCode', label: '商品编码', dataType: 'text' },
    { key: 'note', label: '口径说明', dataType: 'text' },
    { key: 'lastMovementDate', label: '最后移动日期', dataType: 'date' },
  ],
  rows: [
    { productCode: 'P001', note: '=HYPERLINK("http://evil")', lastMovementDate: null },
    { productCode: 'P002', note: '普通文本', lastMovementDate: '2026-09-20T00:00:00' },
  ],
});
ok(csv.indexOf('商品编码') < csv.indexOf('口径说明') && csv.indexOf('口径说明') < csv.indexOf('最后移动日期'), 'CSV 按选定列顺序');
ok(csv.includes('"\'='), '公式前导文本被单引号转义');
ok(!csv.includes('"='), '公式前导不直接以等号开头');
ok(csv.includes('未知'), '无台账日期未知值被保留');

// ---- 9. 前端接线契约（接口路径、复选框字段选择器、无任意 SQL / 自由字段名输入） ----
const jsSrc = fs.readFileSync(JS_PATH, 'utf8');
ok(jsSrc.includes('/api/dynamic-inventory-movement-report'), '调用 ERP-130 只读预览接口');
ok(jsSrc.includes('name="imr-dyn-field"') && jsSrc.includes('type="checkbox"'), '字段选择器为复选框，无自由字段名输入');
ok(!/FromSql|ExecuteSql|SqlCommand|SELECT\s+\*/i.test(jsSrc), '不执行任意 SQL');

console.log('\n✅ ERP-131 动态库存移动报表 UI 逻辑测试全部通过（' + passed + ' 项断言）。\n');

