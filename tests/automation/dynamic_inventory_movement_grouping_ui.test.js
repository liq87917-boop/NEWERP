'use strict';
/* ERP-132 前端分组行数分布逻辑单测：分组请求边界（非法分组键钳制为 none）、可访问条形图渲染（仓库 / 分类 / 台账状态）、
   空分类与未知历史分类保留、安全转义、空页 / 不分组不渲染，以及前端接线契约（无任意 SQL）。
   运行：node tests/automation/dynamic_inventory_movement_grouping_ui.test.js
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

const catalogFields = [
  { key: 'warehouseName', label: '仓库名称', dataType: 'text', filterable: false },
  { key: 'productCode', label: '商品编码', dataType: 'text', filterable: false },
  { key: 'classification', label: '分类', dataType: 'enum', filterable: false },
  { key: 'historyStatus', label: '台账状态', dataType: 'enum', filterable: false },
];

// ---- 1. 分组请求边界：合法分组进入请求，非法分组钳制为 none（后端 fail closed） ----
const reqWarehouse = mod.imrDynBuildRequest({
  catalogFields, selectedKeys: ['productCode'], groupBy: 'warehouse', page: 1, pageSize: 20, maxPageSize: 200,
});
eq(reqWarehouse.groupBy, 'warehouse', '合法分组键进入请求');

const reqHistory = mod.imrDynBuildRequest({
  catalogFields, selectedKeys: ['productCode'], groupBy: 'history', page: 1, pageSize: 20, maxPageSize: 200,
});
eq(reqHistory.groupBy, 'history', '台账状态分组键进入请求');

const reqInvalid = mod.imrDynBuildRequest({
  catalogFields, selectedKeys: [], groupBy: 'bogus', page: 1, pageSize: 20, maxPageSize: 200,
});
eq(reqInvalid.groupBy, undefined, '非法分组键不进入请求（钳制为 none）');

const reqDefault = mod.imrDynBuildRequest({
  catalogFields, selectedKeys: [], page: 1, pageSize: 20, maxPageSize: 200,
});
eq(reqDefault.groupBy, undefined, '默认不分组');

// ---- 2. 仓库分组图：可访问列表 + 可见标签与计数 ----
const wh = mod.imrDynGroupsHtml({
  groupBy: 'warehouse',
  groups: [
    { key: 'warehouse:1', label: '主仓', count: 3 },
    { key: 'warehouse:2', label: '副仓', count: 1 },
  ],
});
ok(wh.includes('主仓') && wh.includes('副仓'), '仓库分组标签可见');
ok(wh.includes('3 行') && wh.includes('1 行'), '仓库分组计数可见');
ok(wh.includes('role="list"') && wh.includes('role="listitem"'), '分组图使用可访问列表');

// ---- 3. 分类分组图：空分类与未知历史分类均渲染（计数可为 0） ----
const cls = mod.imrDynGroupsHtml({
  groupBy: 'classification',
  groups: [
    { key: 'classification:active', label: '正常流动', count: 2 },
    { key: 'classification:stagnant', label: '呆滞', count: 0 },
    { key: 'classification:unknown', label: '无法判定', count: 1 },
  ],
});
ok(cls.includes('正常流动') && cls.includes('呆滞') && cls.includes('无法判定'), '分类三分类全部渲染');
ok(cls.includes('2 行') && cls.includes('0 行') && cls.includes('1 行'), '空分类计数为 0 仍可见');

// ---- 4. 台账状态分组图：未知历史（无台账）分类可见 ----
const hist = mod.imrDynGroupsHtml({
  groupBy: 'history',
  groups: [
    { key: 'history:ledger', label: '有台账（窗口内有移动）', count: 0 },
    { key: 'history:window_empty', label: '有台账（窗口内无移动）', count: 0 },
    { key: 'history:no_history', label: '无台账（历史库存 · 未知）', count: 2 },
  ],
});
ok(hist.includes('无台账（历史库存 · 未知）'), '未知历史分类可见');
ok(hist.includes('2 行'), '未知历史计数可见');

// ---- 5. 安全渲染：标签与计数转义，绝不注入 ----
const safe = mod.imrDynGroupsHtml({
  groupBy: 'warehouse',
  groups: [{ key: 'warehouse:9', label: '<b>危险</b>', count: 1 }],
});
ok(safe.includes('&lt;b&gt;危险&lt;/b&gt;'), '标签被转义');
ok(!safe.includes('<b>危险</b>'), '未转义标签不出现');

// ---- 6. 空页 / 不分组：不渲染图表 ----
eq(mod.imrDynGroupsHtml({ groupBy: 'warehouse', groups: [] }), '', '空分组不渲染图表');
eq(mod.imrDynGroupsHtml({ groupBy: 'classification', groups: null }), '', '无分组不渲染图表');
eq(mod.imrDynGroupsHtml({ groupBy: 'none', groups: [{ key: 'a', label: 'x', count: 1 }] }), '', '不分组不渲染图表');

// ---- 7. 前端接线契约：分组选择器、复用只读接口、无任意 SQL ----
ok(typeof mod.imrDynGroupsHtml === 'function', '导出分组图渲染函数');
ok(Array.isArray(mod.IMR_DYN_GROUP_OPTS) && mod.IMR_DYN_GROUP_OPTS.length === 4, '导出分组键选项');
const jsSrc = fs.readFileSync(JS_PATH, 'utf8');
ok(jsSrc.includes('id="imr-dyn-group"'), '页面包含分组选择器');
ok(jsSrc.includes('/api/dynamic-inventory-movement-report'), '复用 ERP-130 只读预览接口');
ok(!/FromSql|ExecuteSql|SqlCommand|SELECT\s+\*/i.test(jsSrc), '不执行任意 SQL');

console.log('\n✅ ERP-132 动态库存移动报表分组行数分布 UI 逻辑测试全部通过（' + passed + ' 项断言）。\n');
