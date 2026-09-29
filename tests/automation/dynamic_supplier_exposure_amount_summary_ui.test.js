'use strict';
/* ERP-155 前端金额汇总 UI 逻辑单测：汇总模式选择（仅 ERP-154 白名单）、请求携带 summaryMode、按币种分行、
   链接状态与未知结算证据、未链接敞口独立、安全转义渲染、翻页随页更新，以及权限/网络失败态与文档接线契约。
   运行：node tests/automation/dynamic_supplier_exposure_amount_summary_ui.test.js
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

// ---- 1. 汇总模式选择（仅 ERP-154 白名单：none / supplierCurrency / supplierCurrencyLink） ----
eq(mod.speDesSummaryMode('none'), 'none', 'none 合法模式');
eq(mod.speDesSummaryMode('supplierCurrency'), 'supplierCurrency', 'supplierCurrency 合法模式');
eq(mod.speDesSummaryMode('supplierCurrencyLink'), 'supplierCurrencyLink', 'supplierCurrencyLink 合法模式');
eq(mod.speDesSummaryMode('supplierCurrencyQuarterly'), 'none', '非法模式回落 none（fail closed）');
eq(mod.speDesSummaryMode(''), 'none', '空模式回落 none');
eq(mod.speDesSummaryMode(null), 'none', 'null 模式回落 none');
eq(mod.speDesSummaryMode(' supplierCurrency '), 'supplierCurrency', '空白被修剪后仍为合法模式');

const selectHtml = mod.speDesSummarySelectHtml('none');
for (const k of ['none', 'supplierCurrency', 'supplierCurrencyLink']) {
  ok(selectHtml.includes(`value="${k}"`), `选择器包含模式 ${k}`);
}
ok(selectHtml.includes('id="spe-des-summarymode"'), '选择器 id 正确');
ok(!/<input[^>]*type="text"/i.test(selectHtml), '汇总选择器无自由文本输入');
ok(!selectHtml.includes('value="supplierCurrencyQuarterly"'), '选择器不含范围外模式');

// ---- 2. 请求边界：携带选中汇总模式（非法值绝不进入请求） ----
const catalogFields = [{ key: 'supplierName', label: '供应商名称', dataType: 'text' }];
const req = mod.speDesBuildRequest({
  catalogFields,
  selectedKeys: ['supplierName'],
  summaryMode: 'supplierCurrencyLink',
  page: 1, pageSize: 50, maxPageSize: 200,
});
eq(req.summaryMode, 'supplierCurrencyLink', '请求携带选中汇总模式');

const reqBad = mod.speDesBuildRequest({
  catalogFields, selectedKeys: [], summaryMode: 'bogus', page: 1, pageSize: 50, maxPageSize: 200,
});
eq(reqBad.summaryMode, 'none', '非法模式回落 none（绝不进入请求）');

const reqDefault = mod.speDesBuildRequest({
  catalogFields, selectedKeys: [], page: 1, pageSize: 50, maxPageSize: 200,
});
eq(reqDefault.summaryMode, 'none', '未选择时默认 none');

// ---- 3. 金额汇总表：供应商 / 币种 / 链接状态 / 订单金额与已知结算证据，不同币种分别成行 ----
const view = {
  summaryMode: 'supplierCurrencyLink',
  summaries: [
    { supplierId: 968001, supplierName: '甲供应商', currency: 'CNY', linkStatus: 'linked', linkStatusText: '链接可用', orderCount: 2, orderedAmount: 200, linkedOrderCount: 2, linkedOrderedAmount: 200, ambiguousOrderCount: 0, ambiguousOrderedAmount: 0, unavailableOrderCount: 0, unavailableOrderedAmount: 0, settledAmount: 150, outstandingAmount: 50, submittedAmount: 120, unknownSettlementOrderCount: 0 },
    { supplierId: 968001, supplierName: '甲供应商', currency: 'USD', linkStatus: 'unavailable', linkStatusText: '无可用链接（金额未知）', orderCount: 1, orderedAmount: 120, linkedOrderCount: 0, linkedOrderedAmount: 0, ambiguousOrderCount: 0, ambiguousOrderedAmount: 0, unavailableOrderCount: 1, unavailableOrderedAmount: 120, settledAmount: null, outstandingAmount: null, submittedAmount: null, unknownSettlementOrderCount: 0 },
  ],
};
const html = mod.speDesSummaryHtml(view);
ok(html.includes('当前页金额汇总'), '汇总标题可见');
ok(html.includes('仅当前预览页，非全量合计'), '标注仅当前预览页');
ok(html.includes('甲供应商'), '供应商名称可见');
ok(html.includes('<b>CNY</b>') && html.includes('<b>USD</b>'), '不同币种分别成行');
ok(html.includes('链接状态'), '链接状态列可见（supplierCurrencyLink 模式）');
ok(html.includes('链接可用') && html.includes('无可用链接（金额未知）'), '链接状态文案可见');
ok(html.includes('订单金额·原币') && html.includes('>200</td>'), '订单金额·原币可见');
ok(html.includes('已结算·原币') && html.includes('>150</td>'), '已结算证据可见');
ok(html.includes('未结算·原币') && html.includes('>50</td>'), '未结算证据可见');
ok(html.includes('已提交·原币') && html.includes('>120</td>'), '已提交证据可见');
ok(html.includes('未知'), '未知结算金额显示未知（绝不回落 0）');

// ---- 4. 未链接敞口独立（链接不唯一 / 无可用链接金额绝不并入权威已结算合计） ----
const unlinkedHtml = mod.speDesSummaryHtml({
  summaryMode: 'supplierCurrency',
  summaries: [{ supplierId: 1, supplierName: '丙供应商', currency: 'CNY', orderCount: 3, orderedAmount: 300, linkedOrderCount: 1, linkedOrderedAmount: 100, ambiguousOrderCount: 1, ambiguousOrderedAmount: 100, unavailableOrderCount: 1, unavailableOrderedAmount: 100, settledAmount: 80, outstandingAmount: 20, submittedAmount: 60, unknownSettlementOrderCount: 0 }],
});
ok(unlinkedHtml.includes('链接不唯一·金额·原币'), '未链接敞口（链接不唯一）独立列');
ok(unlinkedHtml.includes('无可用链接·金额·原币'), '未链接敞口（无可用链接）独立列');
ok(unlinkedHtml.includes('>100</td>'), '未链接敞口金额单独成列（绝不并入已结算）');
ok(unlinkedHtml.includes('>80</td>'), '已结算金额独立显示');

// supplierCurrency 模式不渲染链接状态列
ok(!mod.speDesSummaryHtml({
  summaryMode: 'supplierCurrency',
  summaries: [{ supplierId: 1, supplierName: 'x', currency: 'CNY', orderCount: 1, orderedAmount: 1, linkedOrderCount: 0, linkedOrderedAmount: 0, ambiguousOrderCount: 0, ambiguousOrderedAmount: 0, unavailableOrderCount: 0, unavailableOrderedAmount: 0, settledAmount: null, outstandingAmount: null, submittedAmount: null, unknownSettlementOrderCount: 0 }],
}).includes('链接状态'), 'supplierCurrency 模式无链接状态列');

// ---- 5. 未知金额绝不回落 0 ----
const unknownHtml = mod.speDesSummaryHtml({
  summaryMode: 'supplierCurrency',
  summaries: [{ supplierId: 1, supplierName: '乙供应商', currency: 'EUR', orderCount: 1, orderedAmount: 90, linkedOrderCount: 1, linkedOrderedAmount: 90, ambiguousOrderCount: 0, ambiguousOrderedAmount: 0, unavailableOrderCount: 0, unavailableOrderedAmount: 0, settledAmount: null, outstandingAmount: null, submittedAmount: null, unknownSettlementOrderCount: 1 }],
});
ok(unknownHtml.includes('>90</td>'), '已知订单金额原样显示');
ok(unknownHtml.includes('未知'), '未知结算金额显示未知（绝不回落 0）');
ok(unknownHtml.includes('>1</td>'), '链接可用未知结算单数显示 1');

// ---- 6. none / 空汇总 / 空视图可见状态 ----
eq(mod.speDesSummaryHtml({ summaryMode: 'none', summaries: [] }), '', 'none 不渲染汇总');
eq(mod.speDesSummaryHtml(null), '', '空视图不渲染汇总');
const emptySummary = mod.speDesSummaryHtml({ summaryMode: 'supplierCurrency', summaries: [] });
ok(emptySummary.includes('本页没有可汇总金额的采购订单敞口证据'), '空汇总可见提示');
ok(emptySummary.includes('仅当前预览页'), '空汇总仍标注仅当前预览页');

// ---- 7. 安全转义渲染 ----
const unsafe = mod.speDesSummaryHtml({
  summaryMode: 'supplierCurrency',
  summaries: [{ supplierId: 1, supplierName: '<script>恶意</script>', currency: 'CNY', orderCount: 1, orderedAmount: 1, linkedOrderCount: 0, linkedOrderedAmount: 0, ambiguousOrderCount: 0, ambiguousOrderedAmount: 0, unavailableOrderCount: 0, unavailableOrderedAmount: 0, settledAmount: null, outstandingAmount: null, submittedAmount: null, unknownSettlementOrderCount: 0 }],
});
ok(unsafe.includes('&lt;script&gt;恶意&lt;/script&gt;'), '供应商名称被转义');
ok(!unsafe.includes('<script>恶意</script>'), '未转义标签不出现');

// ---- 8. 翻页：汇总随页更新（当前页证据，非全量） ----
const page1 = mod.speDesSummaryHtml({
  summaryMode: 'supplierCurrency',
  summaries: [{ supplierId: 1, supplierName: '甲供应商', currency: 'CNY', orderCount: 2, orderedAmount: 200, linkedOrderCount: 2, linkedOrderedAmount: 200, ambiguousOrderCount: 0, ambiguousOrderedAmount: 0, unavailableOrderCount: 0, unavailableOrderedAmount: 0, settledAmount: 100, outstandingAmount: 100, submittedAmount: 50, unknownSettlementOrderCount: 0 }],
});
const page2 = mod.speDesSummaryHtml({
  summaryMode: 'supplierCurrency',
  summaries: [{ supplierId: 1, supplierName: '甲供应商', currency: 'CNY', orderCount: 1, orderedAmount: 100, linkedOrderCount: 1, linkedOrderedAmount: 100, ambiguousOrderCount: 0, ambiguousOrderedAmount: 0, unavailableOrderCount: 0, unavailableOrderedAmount: 0, settledAmount: 50, outstandingAmount: 50, submittedAmount: 25, unknownSettlementOrderCount: 0 }],
});
ok(page1.includes('>200</td>') && page1.includes('>100</td>'), '第 1 页汇总');
ok(page2.includes('>100</td>') && page2.includes('>50</td>'), '翻页后汇总随页更新');

// ---- 9. 权限 / 网络失败态（fail closed） ----
ok(mod.speDesErrorHtml('forbidden', '无采购订单菜单授权').includes('权限不足'), '授权失败可见');
ok(mod.speDesErrorHtml('unauthorized', '请先登录').includes('未登录'), '未登录可见');
ok(mod.speDesErrorHtml('network', 'fetch failed').includes('网络请求失败'), '网络失败可见');
ok(mod.speDesErrorHtml('invalid', '无效汇总模式').includes('请求无效'), '无效请求可见');
eq(mod.speDesKindOfCode(2002), 'forbidden', '2002 → 权限不足');
eq(mod.speDesKindOfCode(2000), 'unauthorized', '2000 → 未登录');
eq(mod.speDesKindOfCode(2003), 'unauthorized', '2003 → 未登录');

// ---- 10. 接线契约与文档（汇总与下载范围） ----
const jsSrc = fs.readFileSync(JS_PATH, 'utf8');
ok(jsSrc.includes('id="spe-des-summarymode"'), '设计器接线汇总模式选择器');
ok(jsSrc.includes('req.summaryMode'), '请求体包含 summaryMode');
ok(jsSrc.includes('仅当前预览页'), '汇总仅当前预览页语义已接线');
ok(jsSrc.includes('绝不并入权威已结算合计'), '未链接敞口独立语义已接线');
ok(!/FromSql|ExecuteSql|SqlCommand|SELECT\s+\*/i.test(jsSrc), '不执行任意 SQL');
ok(!/GrandTotal|跨币种合计|Grand\s+Total/i.test(jsSrc), '无跨币种总额');

const doc = fs.readFileSync(DOC_PATH, 'utf8');
ok(doc.includes('ERP-155'), '文档登记 ERP-155');
ok(doc.includes('supplierCurrencyLink'), '文档说明汇总模式');
ok(doc.includes('仅当前预览页'), '文档标注汇总仅当前预览页');
ok(doc.includes('不进入'), '文档说明汇总不进入行导出范围');

console.log('\n✅ ERP-155 动态供应商采购敞口金额汇总 UI 测试全部通过（' + passed + ' 项断言）。\n');


