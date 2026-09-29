'use strict';
/* ERP-153 前端分组计数 UI 逻辑单测：分组键选择（仅 ERP-152 白名单）、请求携带分组键、计数条形图文本、
   未知 / 空分组、翻页变化、权限与网络失败态，以及「计数仅当前预览页」的接线契约。
   运行：node tests/automation/dynamic_supplier_exposure_grouping_ui.test.js
   失败时抛异常并以非零码退出。 */

const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const JS_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'supplier-purchase-exposure.js');
const DOC_PATH = path.join(ROOT, 'docs', 'dynamic-supplier-exposure-report.md');

let passed = 0;
function ok(cond, msg) { assert.ok(cond, msg); passed += 1; }
function eq(actual, expected, msg) { assert.deepStrictEqual(actual, expected, msg); passed += 1; }

const mod = require(JS_PATH);

// ---- 1. 分组键选择（仅 ERP-152 白名单：none / supplier / currency / linkStatus / receiptStatus） ----
eq(mod.speDesGroupKey('supplier'), 'supplier', 'supplier 合法分组键');
eq(mod.speDesGroupKey('currency'), 'currency', 'currency 合法分组键');
eq(mod.speDesGroupKey('linkStatus'), 'linkStatus', 'linkStatus 合法分组键');
eq(mod.speDesGroupKey('receiptStatus'), 'receiptStatus', 'receiptStatus 合法分组键');
eq(mod.speDesGroupKey('none'), 'none', 'none 合法分组键');
eq(mod.speDesGroupKey('quarter'), 'none', '非法分组键回落 none（fail closed）');
eq(mod.speDesGroupKey(''), 'none', '空分组键回落 none');
eq(mod.speDesGroupKey(null), 'none', 'null 分组键回落 none');
eq(mod.speDesGroupKey(' supplier '), 'supplier', '空白被修剪后仍为合法键');

// 选择器仅提供 ERP-152 白名单键，无自由输入
const selectHtml = mod.speDesGroupSelectHtml('none');
for (const k of ['none', 'supplier', 'currency', 'linkStatus', 'receiptStatus']) {
  ok(selectHtml.includes(`value="${k}"`), `选择器包含分组键 ${k}`);
}
ok(selectHtml.includes('id="spe-des-groupby"'), '选择器 id 正确');
ok(!/<input[^>]*type="text"/i.test(selectHtml), '分组选择器无自由文本输入');
ok(!selectHtml.includes('value="quarter"'), '选择器不含范围外分组键');

// ---- 2. 请求边界：携带选中的分组键（非法值绝不进入请求） ----
const catalogFields = [{ key: 'supplierName', label: '供应商名称', dataType: 'text' }];
const req = mod.speDesBuildRequest({
  catalogFields,
  selectedKeys: ['supplierName'],
  groupBy: 'supplier',
  page: 1, pageSize: 50, maxPageSize: 200,
});
eq(req.groupBy, 'supplier', '请求携带选中分组键');

const reqNone = mod.speDesBuildRequest({
  catalogFields, selectedKeys: [], groupBy: 'bogus', page: 1, pageSize: 50, maxPageSize: 200,
});
eq(reqNone.groupBy, 'none', '非法分组键回落 none（绝不进入请求）');

const reqDefault = mod.speDesBuildRequest({
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
const supplierChart = mod.speDesGroupChartHtml(supplierView);
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
ok(mod.speDesGroupChartHtml(currencyView).includes('按币种分组'), '币种分组标题');

// 链接状态：固定分类 + 未知链接类别（链接不唯一 / 无可用链接）保持可见（含计数 0）
const linkView = {
  groupBy: 'linkStatus',
  groups: [
    { key: 'linkStatus:linked', label: '链接可用', count: 2 },
    { key: 'linkStatus:ambiguous', label: '链接不唯一（金额未知）', count: 0 },
    { key: 'linkStatus:unavailable', label: '无可用链接（金额未知）', count: 1 },
  ],
};
const linkChart = mod.speDesGroupChartHtml(linkView);
ok(linkChart.includes('按链接状态分组'), '链接状态分组标题');
ok(linkChart.includes('链接不唯一（金额未知）') && linkChart.includes('>0</span>'), '链接不唯一空分类计数 0 仍显示');
ok(linkChart.includes('无可用链接（金额未知）') && linkChart.includes('>1</span>'), '无可用链接类别可见');

// 收货状态：固定分类 + 未知收货类别（超出派生上限）保持可见（含计数 0）
const receiptView = {
  groupBy: 'receiptStatus',
  groups: [
    { key: 'receiptStatus:none', label: '未收货', count: 1 },
    { key: 'receiptStatus:partial', label: '部分收货', count: 0 },
    { key: 'receiptStatus:complete', label: '已收齐', count: 1 },
    { key: 'receiptStatus:over_received', label: '超收', count: 0 },
    { key: 'receiptStatus:unknown', label: '未知（超出派生上限）', count: 2 },
  ],
};
const receiptChart = mod.speDesGroupChartHtml(receiptView);
ok(receiptChart.includes('按收货状态分组'), '收货状态分组标题');
ok(receiptChart.includes('未知（超出派生上限）') && receiptChart.includes('>2</span>'), '未知收货类别可见');
ok(receiptChart.includes('部分收货') && receiptChart.includes('>0</span>'), '空收货分类计数 0 仍显示');

// ---- 4. 未知计数 / 空分组 / none / 翻页变化 ----
const unknownCountView = {
  groupBy: 'supplier',
  groups: [{ key: 'supplier:1', label: '甲供应商', count: null }],
};
ok(mod.speDesGroupChartHtml(unknownCountView).includes('未知'), '未知计数不回落 0');

eq(mod.speDesGroupChartHtml({ groupBy: 'none', groups: [] }), '', 'none 不渲染图表');
eq(mod.speDesGroupChartHtml(null), '', '空视图不渲染图表');
const emptyGroupsChart = mod.speDesGroupChartHtml({ groupBy: 'supplier', groups: [] });
ok(emptyGroupsChart.includes('本页没有可分组计数的采购订单敞口证据'), '动态分组空页可见提示');
ok(emptyGroupsChart.includes('仅当前预览页'), '空页仍标注仅当前预览页');

const page1 = { groupBy: 'supplier', groups: [{ key: 'supplier:1', label: '甲供应商', count: 2 }] };
const page2 = { groupBy: 'supplier', groups: [{ key: 'supplier:1', label: '甲供应商', count: 1 }] };
ok(mod.speDesGroupChartHtml(page1).includes('>2</span>'), '第 1 页计数');
ok(mod.speDesGroupChartHtml(page2).includes('>1</span>'), '翻页后计数随页更新（当前页证据）');

// ---- 5. 权限 / 网络失败态（fail closed） ----
ok(mod.speDesErrorHtml('forbidden', '无采购订单菜单授权').includes('权限不足'), '授权失败可见');
ok(mod.speDesErrorHtml('unauthorized', '请先登录').includes('未登录'), '未登录可见');
ok(mod.speDesErrorHtml('network', 'fetch failed').includes('网络请求失败'), '网络失败可见');
ok(mod.speDesErrorHtml('invalid', '未知分组键').includes('请求无效'), '无效分组可见');
eq(mod.speDesKindOfCode(2002), 'forbidden', '2002 → 权限不足');
eq(mod.speDesKindOfCode(2000), 'unauthorized', '2000 → 未登录');
eq(mod.speDesKindOfCode(2003), 'unauthorized', '2003 → 未登录');

// ---- 6. 接线契约（分组选择器、groupBy 请求、仅当前预览页语义、无任意 SQL / 跨币种总额） ----
const jsSrc = fs.readFileSync(JS_PATH, 'utf8');
ok(jsSrc.includes('id="spe-des-groupby"'), '设计器接线分组选择器');
ok(jsSrc.includes('req.groupBy'), '请求体包含 groupBy');
ok(jsSrc.includes('仅当前预览页'), '计数仅当前预览页语义已接线');
ok(!/FromSql|ExecuteSql|SqlCommand|SELECT\s+\*/i.test(jsSrc), '不执行任意 SQL');
ok(!/GrandTotal|跨币种合计|Grand\s+Total/i.test(jsSrc), '无跨币种总额');

const doc = fs.readFileSync(DOC_PATH, 'utf8');
ok(doc.includes('ERP-153'), '文档登记 ERP-153');
ok(doc.includes('仅当前预览页'), '文档标注计数仅当前预览页');

console.log('\n✅ ERP-153 动态供应商采购敞口分组计数 UI 测试全部通过（' + passed + ' 项断言）。\n');

