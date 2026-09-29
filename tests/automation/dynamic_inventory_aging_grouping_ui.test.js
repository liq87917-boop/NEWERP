'use strict';
/* ERP-137 前端分组行数分布逻辑单测：分组请求边界（非法分组键钳制为 none）、可访问条形图渲染（仓库 / 库龄依据 / 成本依据）、
   空证据分类保留、安全转义、空页 / 不分组不渲染，以及前端接线契约（无任意 SQL）。
   运行：node tests/automation/dynamic_inventory_aging_grouping_ui.test.js
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

const catalogFields = [
  { key: 'warehouseId', label: '仓库Id', dataType: 'number', filterable: true },
  { key: 'productCode', label: '商品编码', dataType: 'text', filterable: false },
  { key: 'evidenceStatus', label: '库龄依据', dataType: 'enum', filterable: false },
  { key: 'costStatus', label: '成本状态', dataType: 'enum', filterable: false },
];

// ---- 1. 分组请求边界：合法分组进入请求，非法分组钳制为 none（后端 fail closed） ----
eq(Array.isArray(mod.IAR_DYN_GROUP_OPTS) && mod.IAR_DYN_GROUP_OPTS.length === 4, true, '导出 4 个分组键选项');

const reqWarehouse = mod.iarDynBuildRequest({
  catalogFields, selectedKeys: ['productCode'], groupBy: 'warehouse', page: 1, pageSize: 20, maxPageSize: 200,
});
eq(reqWarehouse.groupBy, 'warehouse', '合法分组键进入请求');

const reqAge = mod.iarDynBuildRequest({
  catalogFields, selectedKeys: ['productCode'], groupBy: 'ageEvidence', page: 1, pageSize: 20, maxPageSize: 200,
});
eq(reqAge.groupBy, 'ageEvidence', '库龄依据分组键进入请求');

const reqCost = mod.iarDynBuildRequest({
  catalogFields, selectedKeys: ['productCode'], groupBy: 'costEvidence', page: 1, pageSize: 20, maxPageSize: 200,
});
eq(reqCost.groupBy, 'costEvidence', '成本依据分组键进入请求');

const reqInvalid = mod.iarDynBuildRequest({
  catalogFields, selectedKeys: [], groupBy: 'bogus', page: 1, pageSize: 20, maxPageSize: 200,
});
eq(reqInvalid.groupBy, undefined, '非法分组键不进入请求（钳制为 none）');

const reqDefault = mod.iarDynBuildRequest({
  catalogFields, selectedKeys: [], page: 1, pageSize: 20, maxPageSize: 200,
});
eq(reqDefault.groupBy, undefined, '默认不分组');

// ---- 2. 仓库分组图：可访问列表 + 可见标签与计数 ----
const wh = mod.iarDynGroupsHtml({
  groupBy: 'warehouse',
  groups: [
    { key: 'warehouse:920001', label: '主仓', count: 3 },
    { key: 'warehouse:920002', label: '副仓', count: 1 },
  ],
});
ok(wh.includes('主仓') && wh.includes('副仓'), '仓库分组标签可见');
ok(wh.includes('3 行') && wh.includes('1 行'), '仓库分组计数可见');
ok(wh.includes('role="list"') && wh.includes('role="listitem"'), '分组图使用可访问列表');

// ---- 3. 库龄依据分组图：完整 / 部分 / 缺失三分类均渲染（计数可为 0） ----
const age = mod.iarDynGroupsHtml({
  groupBy: 'ageEvidence',
  groups: [
    { key: 'ageEvidence:full', label: '有台账分层依据', count: 2 },
    { key: 'ageEvidence:partial', label: '部分数量无依据', count: 0 },
    { key: 'ageEvidence:none', label: '无台账分层依据', count: 1 },
  ],
});
ok(age.includes('有台账分层依据') && age.includes('部分数量无依据') && age.includes('无台账分层依据'), '库龄依据三分类全部渲染');
ok(age.includes('2 行') && age.includes('0 行') && age.includes('1 行'), '空分类计数为 0 仍可见');

// ---- 4. 成本依据分组图：已知 / 未知均渲染 ----
const cost = mod.iarDynGroupsHtml({
  groupBy: 'costEvidence',
  groups: [
    { key: 'costEvidence:known', label: '成本已知', count: 2 },
    { key: 'costEvidence:unknown', label: '成本未知', count: 0 },
  ],
});
ok(cost.includes('成本已知') && cost.includes('成本未知'), '成本依据分类可见');
ok(cost.includes('2 行') && cost.includes('0 行'), '成本未知空分类计数为 0 仍可见');

// ---- 5. 安全渲染：标签与计数转义，绝不注入 ----
const safe = mod.iarDynGroupsHtml({
  groupBy: 'warehouse',
  groups: [{ key: 'warehouse:9', label: '<b>危险</b>', count: 1 }],
});
ok(safe.includes('&lt;b&gt;危险&lt;/b&gt;'), '标签被转义');
ok(!safe.includes('<b>危险</b>'), '未转义标签不出现');

// ---- 6. 空页 / 不分组：不渲染图表 ----
eq(mod.iarDynGroupsHtml({ groupBy: 'warehouse', groups: [] }), '', '空分组不渲染图表');
eq(mod.iarDynGroupsHtml({ groupBy: 'ageEvidence', groups: null }), '', '无分组不渲染图表');
eq(mod.iarDynGroupsHtml({ groupBy: 'none', groups: [{ key: 'a', label: 'x', count: 1 }] }), '', '不分组不渲染图表');

// ---- 7. 分页变化：翻页后的 page 进入请求（分布仅统计当前页） ----
const page2 = mod.iarDynBuildRequest({
  catalogFields, selectedKeys: ['productCode'], groupBy: 'warehouse', page: 2, pageSize: 20, maxPageSize: 200,
});
eq(page2.page, 2, '翻页变化进入请求');
eq(page2.groupBy, 'warehouse', '翻页仍保留分组键');

// ---- 8. 前端接线契约：分组选择器、复用只读接口、无任意 SQL ----
ok(typeof mod.iarDynGroupsHtml === 'function', '导出分组图渲染函数');
const jsSrc = fs.readFileSync(JS_PATH, 'utf8');
ok(jsSrc.includes('id="iar-dyn-group"'), '页面包含分组选择器');
ok(jsSrc.includes('/api/dynamic-inventory-aging-report'), '复用 ERP-135 只读预览接口');
ok(!/FromSql|ExecuteSql|SqlCommand|SELECT\s+\*/i.test(jsSrc), '不执行任意 SQL');

console.log('\n✅ ERP-137 动态库存库龄报表分组行数分布 UI 逻辑测试全部通过（' + passed + ' 项断言）。\n');
