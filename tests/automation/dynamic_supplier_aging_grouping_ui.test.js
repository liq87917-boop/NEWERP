'use strict';
/* ERP-145 前端分组计数 UI 逻辑单测：分组键选择（仅 ERP-144 白名单）、请求携带分组键、计数条形图文本、
   未知 / 空分组、翻页变化、权限与网络失败态，以及「计数仅当前预览页」的接线契约。
   运行：node tests/automation/dynamic_supplier_aging_grouping_ui.test.js
   失败时抛异常并以非零码退出。 */

const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const JS_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'supplier-reconciliation-aging.js');
const DOC_PATH = path.join(ROOT, 'docs', 'dynamic-supplier-aging-report.md');

let passed = 0;
function ok(cond, msg) { assert.ok(cond, msg); passed += 1; }
function eq(actual, expected, msg) { assert.deepStrictEqual(actual, expected, msg); passed += 1; }

const mod = require(JS_PATH);

// ---- 1. 分组键选择（仅 ERP-144 白名单：none / supplier / currency / agingBucket / allocationState） ----
eq(mod.sraDesGroupKey('supplier'), 'supplier', 'supplier 合法分组键');
eq(mod.sraDesGroupKey('currency'), 'currency', 'currency 合法分组键');
eq(mod.sraDesGroupKey('agingBucket'), 'agingBucket', 'agingBucket 合法分组键');
eq(mod.sraDesGroupKey('allocationState'), 'allocationState', 'allocationState 合法分组键');
eq(mod.sraDesGroupKey('none'), 'none', 'none 合法分组键');
eq(mod.sraDesGroupKey('quarter'), 'none', '非法分组键回落 none（fail closed）');
eq(mod.sraDesGroupKey(''), 'none', '空分组键回落 none');
eq(mod.sraDesGroupKey(null), 'none', 'null 分组键回落 none');
eq(mod.sraDesGroupKey(' supplier '), 'supplier', '空白被修剪后仍为合法键');

// 选择器仅提供 ERP-144 白名单键，无自由输入
const selectHtml = mod.sraDesGroupSelectHtml('none');
for (const k of ['none', 'supplier', 'currency', 'agingBucket', 'allocationState']) {
  ok(selectHtml.includes(`value="${k}"`), `选择器包含分组键 ${k}`);
}
ok(selectHtml.includes('id="sra-des-groupby"'), '选择器 id 正确');
ok(!/<input[^>]*type="text"/i.test(selectHtml), '分组选择器无自由文本输入');
ok(!selectHtml.includes('value="quarter"'), '选择器不含范围外分组键');

// ---- 2. 请求边界：携带选中的分组键（非法值绝不进入请求） ----
const catalogFields = [{ key: 'supplierName', label: '供应商名称', dataType: 'text' }];
const req = mod.sraDesBuildRequest({
  catalogFields,
  selectedKeys: ['supplierName'],
  groupBy: 'supplier',
  page: 1, pageSize: 50, maxPageSize: 200,
});
eq(req.groupBy, 'supplier', '请求携带选中分组键');

const reqNone = mod.sraDesBuildRequest({
  catalogFields, selectedKeys: [], groupBy: 'bogus', page: 1, pageSize: 50, maxPageSize: 200,
});
eq(reqNone.groupBy, 'none', '非法分组键回落 none（绝不进入请求）');

const reqDefault = mod.sraDesBuildRequest({
  catalogFields, selectedKeys: [], page: 1, pageSize: 50, maxPageSize: 200,
});
eq(reqDefault.groupBy, 'none', '未选择时默认 none');

// ---- 3. 计数条形图文本（标签 + 计数，含未知类别、转义安全） ----
const supplierView = {
  groupBy: 'supplier',
  groups: [
    { key: 'supplier:1', label: '甲供应商', count: 3 },
    { key: 'supplier:2', label: '<script>乙供应商</script>', count: 1 },
  ],
  columns: [], rows: [], total: 4, page: 1, pageSize: 50, totalPages: 1,
};
const supplierChart = mod.sraDesGroupChartHtml(supplierView);
ok(supplierChart.includes('按供应商分组'), '标题显示分组键');
ok(supplierChart.includes('甲供应商') && supplierChart.includes('>3</span>'), '标签与计数可见');
ok(supplierChart.includes('&lt;script&gt;乙供应商&lt;/script&gt;'), '分组标签被转义');
ok(!supplierChart.includes('<script>乙供应商</script>'), '未转义标签不出现');
ok(supplierChart.includes('仅当前预览页'), '标注仅当前预览页（非全量合计）');

const currencyView = {
  groupBy: 'currency',
  groups: [
    { key: 'currency:CNY', label: 'CNY', count: 2 },
    { key: 'currency:USD', label: 'USD', count: 1 },
  ],
};
ok(mod.sraDesGroupChartHtml(currencyView).includes('按币种分组'), '币种分组标题');

// 账龄分桶：固定分类 + 未知到期日（空类计数 0 仍显示）
const agingView = {
  groupBy: 'agingBucket',
  groups: [
    { key: 'agingBucket:not_due', label: '未到期', count: 1 },
    { key: 'agingBucket:overdue_1_30', label: '逾期 1 ~ 30 天', count: 0 },
    { key: 'agingBucket:unknown_due_date', label: '未知到期日', count: 2 },
  ],
};
const agingChart = mod.sraDesGroupChartHtml(agingView);
ok(agingChart.includes('按账龄分桶分组'), '账龄分桶分组标题');
ok(agingChart.includes('未知到期日') && agingChart.includes('>2</span>'), '未知到期日类别可见');
ok(agingChart.includes('逾期 1 ~ 30 天') && agingChart.includes('>0</span>'), '空分类计数 0 仍显示');

// 分配状态：无效 / 未知证据类别保持可见（含计数 0）
const allocView = {
  groupBy: 'allocationState',
  groups: [
    { key: 'allocationState:none', label: '无持久化付款引用行', count: 1 },
    { key: 'allocationState:over_allocated', label: '无效证据（超过含税总额）', count: 0 },
    { key: 'allocationState:unknown', label: '未知（命中上限）', count: 1 },
  ],
};
const allocChart = mod.sraDesGroupChartHtml(allocView);
ok(allocChart.includes('按分配状态分组'), '分配状态分组标题');
ok(allocChart.includes('无效证据（超过含税总额）') && allocChart.includes('>0</span>'), '无效证据类别可见');
ok(allocChart.includes('未知（命中上限）') && allocChart.includes('>1</span>'), '未知证据类别可见');

// ---- 4. 未知计数 / 空分组 / none / 翻页变化 ----
const unknownCountView = {
  groupBy: 'supplier',
  groups: [{ key: 'supplier:1', label: '甲供应商', count: null }],
};
ok(mod.sraDesGroupChartHtml(unknownCountView).includes('未知'), '未知计数不回落 0');

eq(mod.sraDesGroupChartHtml({ groupBy: 'none', groups: [] }), '', 'none 不渲染图表');
eq(mod.sraDesGroupChartHtml(null), '', '空视图不渲染图表');
const emptyGroupsChart = mod.sraDesGroupChartHtml({ groupBy: 'supplier', groups: [] });
ok(emptyGroupsChart.includes('本页没有可分组计数的发票证据'), '动态分组空页可见提示');
ok(emptyGroupsChart.includes('仅当前预览页'), '空页仍标注仅当前预览页');

const page1 = { groupBy: 'supplier', groups: [{ key: 'supplier:1', label: '甲供应商', count: 2 }] };
const page2 = { groupBy: 'supplier', groups: [{ key: 'supplier:1', label: '甲供应商', count: 1 }] };
ok(mod.sraDesGroupChartHtml(page1).includes('>2</span>'), '第 1 页计数');
ok(mod.sraDesGroupChartHtml(page2).includes('>1</span>'), '翻页后计数随页更新（当前页证据）');

// ---- 5. 权限 / 网络失败态（fail closed） ----
ok(mod.sraDesErrorHtml('forbidden', '无采购订单菜单授权').includes('权限不足'), '授权失败可见');
ok(mod.sraDesErrorHtml('unauthorized', '请先登录').includes('未登录'), '未登录可见');
ok(mod.sraDesErrorHtml('network', 'fetch failed').includes('网络请求失败'), '网络失败可见');
ok(mod.sraDesErrorHtml('invalid', '未知分组键').includes('请求无效'), '无效分组可见');
eq(mod.sraDesKindOfCode(2002), 'forbidden', '2002 → 权限不足');
eq(mod.sraDesKindOfCode(2000), 'unauthorized', '2000 → 未登录');
eq(mod.sraDesKindOfCode(2003), 'unauthorized', '2003 → 未登录');

// ---- 6. 接线契约（分组选择器、groupBy 请求、仅当前预览页语义、无任意 SQL / 跨币种总额） ----
const jsSrc = fs.readFileSync(JS_PATH, 'utf8');
ok(jsSrc.includes('id="sra-des-groupby"'), '设计器接线分组选择器');
ok(jsSrc.includes('req.groupBy'), '请求体包含 groupBy');
ok(jsSrc.includes('仅当前预览页'), '计数仅当前预览页语义已接线');
ok(!/FromSql|ExecuteSql|SqlCommand|SELECT\s+\*/i.test(jsSrc), '不执行任意 SQL');
ok(!/GrandTotal|跨币种合计|Grand\s+Total/i.test(jsSrc), '无跨币种总额');

const doc = fs.readFileSync(DOC_PATH, 'utf8');
ok(doc.includes('ERP-145'), '文档登记 ERP-145');
ok(doc.includes('仅当前预览页'), '文档标注计数仅当前预览页');

console.log('\n✅ ERP-145 动态供应商对账与账龄分组计数 UI 测试全部通过（' + passed + ' 项断言）。\n');

