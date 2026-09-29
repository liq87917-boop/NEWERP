'use strict';
/* ERP-161 前端分组计数 UI 逻辑单测：分组键选择（仅 ERP-160 白名单）、请求携带分组键、
   计数视图安全文本渲染（未知 / 空分类 / 转义）、权限 / 网络失败态、翻页变化，以及「计数仅当前预览页」的接线契约。
   运行：node tests/automation/dynamic_shipment_finance_grouping_ui.test.js
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

// ---- 1. 分组键规范化（仅 ERP-160 白名单：none / customer / currency / shipmentStatus / financeLinkStatus）----
eq(mod.dsfGroupKey('customer'), 'customer', 'customer 合法分组键');
eq(mod.dsfGroupKey('currency'), 'currency', 'currency 合法分组键');
eq(mod.dsfGroupKey('shipmentStatus'), 'shipmentStatus', 'shipmentStatus 合法分组键');
eq(mod.dsfGroupKey('financeLinkStatus'), 'financeLinkStatus', 'financeLinkStatus 合法分组键');
eq(mod.dsfGroupKey('none'), 'none', 'none 合法分组键');
eq(mod.dsfGroupKey('supplier'), 'none', '非法分组键回落 none（fail closed）');
eq(mod.dsfGroupKey(''), 'none', '空分组键回落 none');
eq(mod.dsfGroupKey(null), 'none', 'null 分组键回落 none');
eq(mod.dsfGroupKey(' customer '), 'customer', '空白被修剪后仍为合法键');

// 选择器仅提供 ERP-160 白名单键，无自由输入
const selectHtml = mod.dsfGroupSelectHtml('none');
for (const k of ['none', 'customer', 'currency', 'shipmentStatus', 'financeLinkStatus']) {
  ok(selectHtml.includes(`value="${k}"`), `选择器包含分组键 ${k}`);
}
ok(selectHtml.includes('id="dsf-groupby"'), '选择器 id 正确');
ok(!/<input[^>]*type="text"/i.test(selectHtml), '分组选择器无自由文本输入');
ok(!selectHtml.includes('value="supplier"'), '选择器不含范围外分组键');

// ---- 2. 请求边界：携带选中的分组键（非法值绝不进入请求）----
const catalogFields = [{ key: 'orderNo', label: '订单号', dataType: 'text' }];
const req = mod.dsfBuildRequest({
  catalogFields, selectedKeys: ['orderNo'], groupBy: 'customer', page: 1, pageSize: 50, maxPageSize: 200,
});
eq(req.groupBy, 'customer', '请求携带选中分组键');

const reqNone = mod.dsfBuildRequest({
  catalogFields, selectedKeys: [], groupBy: 'bogus', page: 1, pageSize: 50, maxPageSize: 200,
});
eq(reqNone.groupBy, 'none', '非法分组键回落 none（绝不进入请求）');

const reqDefault = mod.dsfBuildRequest({
  catalogFields, selectedKeys: [], page: 1, pageSize: 50, maxPageSize: 200,
});
eq(reqDefault.groupBy, 'none', '未选择时默认 none');

// ---- 3. 计数视图文本（客户 / 币种 / 出货状态 / 收款链接状态，标签 + 计数，转义安全）----
const customerView = {
  groupBy: 'customer',
  groups: [
    { key: 'customer:1', label: '甲客户', count: 3 },
    { key: 'customer:2', label: '<script>乙客户</script>', count: 1 },
  ],
};
const customerChart = mod.dsfGroupChartHtml(customerView);
ok(customerChart.includes('按客户分组'), '客户分组标题');
ok(customerChart.includes('甲客户') && customerChart.includes('>3</span>'), '标签与计数可见');
ok(customerChart.includes('&lt;script&gt;乙客户&lt;/script&gt;'), '分组标签被转义');
ok(!customerChart.includes('<script>乙客户</script>'), '未转义标签不出现');
ok(customerChart.includes('仅当前预览页'), '标注仅当前预览页（非全量合计）');

const currencyView = {
  groupBy: 'currency',
  groups: [
    { key: 'currency:CNY', label: 'CNY', count: 2 },
    { key: 'currency:USD', label: 'USD', count: 1 },
  ],
};
ok(mod.dsfGroupChartHtml(currencyView).includes('按币种分组'), '币种分组标题');

// 出货状态：固定分类（none / partial / complete / over_shipped / unknown）始终保留，unknown 类别可见（含计数 0）
const shipmentView = {
  groupBy: 'shipmentStatus',
  groups: [
    { key: 'shipmentStatus:none', label: '未出货', count: 1 },
    { key: 'shipmentStatus:partial', label: '部分出货', count: 0 },
    { key: 'shipmentStatus:complete', label: '已出齐', count: 1 },
    { key: 'shipmentStatus:over_shipped', label: '超发', count: 0 },
    { key: 'shipmentStatus:unknown', label: '未知（超出派生上限）', count: 2 },
  ],
};
const shipmentChart = mod.dsfGroupChartHtml(shipmentView);
ok(shipmentChart.includes('按出货状态分组'), '出货状态分组标题');
ok(shipmentChart.includes('未知（超出派生上限）') && shipmentChart.includes('>2</span>'), '未知出货类别可见');
ok(shipmentChart.includes('部分出货') && shipmentChart.includes('>0</span>'), '空出货分类计数 0 仍显示');

// 收款链接状态：固定分类（linked / partial / unlinked / unknown）始终保留，unknown 保持可见
const financeView = {
  groupBy: 'financeLinkStatus',
  groups: [
    { key: 'financeLinkStatus:linked', label: '收款引用完整', count: 2 },
    { key: 'financeLinkStatus:partial', label: '部分可归属（其余未知）', count: 0 },
    { key: 'financeLinkStatus:unlinked', label: '未链接（金额未知）', count: 1 },
    { key: 'financeLinkStatus:unknown', label: '未知（超出派生上限）', count: 0 },
  ],
};
const financeChart = mod.dsfGroupChartHtml(financeView);
ok(financeChart.includes('按收款链接状态分组'), '收款链接状态分组标题');
ok(financeChart.includes('未链接（金额未知）') && financeChart.includes('>1</span>'), '未链接类别可见');
ok(financeChart.includes('未知（超出派生上限）') && financeChart.includes('>0</span>'), '未知收款链接类别保持可见（计数 0）');

// ---- 4. 未知计数 / 空分组 / none / 翻页变化 ----
const unknownCountView = { groupBy: 'customer', groups: [{ key: 'customer:1', label: '甲客户', count: null }] };
ok(mod.dsfGroupChartHtml(unknownCountView).includes('未知'), '未知计数不回落 0');

eq(mod.dsfGroupChartHtml({ groupBy: 'none', groups: [] }), '', 'none 不渲染图表');
eq(mod.dsfGroupChartHtml(null), '', '空视图不渲染图表');
const emptyGroupsChart = mod.dsfGroupChartHtml({ groupBy: 'customer', groups: [] });
ok(emptyGroupsChart.includes('本页没有可分组计数'), '动态分组空页可见提示');
ok(emptyGroupsChart.includes('仅当前预览页'), '空页仍标注仅当前预览页');

const page1 = { groupBy: 'customer', groups: [{ key: 'customer:1', label: '甲客户', count: 2 }] };
const page2 = { groupBy: 'customer', groups: [{ key: 'customer:1', label: '甲客户', count: 1 }] };
ok(mod.dsfGroupChartHtml(page1).includes('>2</span>'), '第 1 页计数');
ok(mod.dsfGroupChartHtml(page2).includes('>1</span>'), '翻页后计数随页更新（当前页证据）');

// ---- 5. 权限 / 网络失败态（fail closed）----
ok(mod.dsfErrorHtml('forbidden', '无销售订单菜单授权').includes('权限不足'), '授权失败可见');
ok(mod.dsfErrorHtml('unauthorized', '请先登录').includes('未登录'), '未登录可见');
ok(mod.dsfErrorHtml('network', 'fetch failed').includes('网络请求失败'), '网络失败可见');
ok(mod.dsfErrorHtml('invalid', '未知分组键').includes('请求无效'), '无效分组可见');
eq(mod.dsfKindOfCode(2002), 'forbidden', '2002 → 权限不足');
eq(mod.dsfKindOfCode(2000), 'unauthorized', '2000 → 未登录');
eq(mod.dsfKindOfCode(2003), 'unauthorized', '2003 → 未登录');

// ---- 6. 接线契约（分组选择器、groupBy 请求、仅当前预览页语义、无任意 SQL / 跨币种合计）----
const jsSrc = fs.readFileSync(JS_PATH, 'utf8');
ok(jsSrc.includes('id="dsf-groupby"'), '设计器接线分组选择器');
ok(jsSrc.includes('req.groupBy'), '请求体包含 groupBy');
ok(jsSrc.includes('仅当前预览页'), '计数仅当前预览页语义已接线');
ok(!/FromSql|ExecuteSql|SqlCommand|SELECT\s+\*/i.test(jsSrc), '不执行任意 SQL');
ok(!/GrandTotal|跨币种合计|Grand\s+Total/i.test(jsSrc), '无跨币种总额');

const doc = fs.readFileSync(DOC, 'utf8');
ok(doc.includes('ERP-161'), '文档登记 ERP-161');
ok(doc.includes('仅当前预览页'), '文档标注计数仅当前预览页');

console.log('\n✅ ERP-161 动态销售订单出货/财务进度分组计数 UI 测试全部通过（' + passed + ' 项断言）。\n');

