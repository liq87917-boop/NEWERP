'use strict';
/* ERP-163 前端金额汇总 UI 逻辑单测：汇总模式选择（仅 ERP-162 白名单）、请求携带 summaryMode、按客户 + 币种分行、
   出货状态 / 收款链接状态拆分、null 金额证据保持「未知」、未知行数、安全转义渲染、翻页随页更新，
   以及权限 / 网络失败态与文档接线契约（汇总仅当前预览页、不进入行导出范围、未覆盖金额不是应收余额）。
   运行：node tests/automation/dynamic_shipment_finance_amount_summary_ui.test.js
   失败时抛异常并以非零码退出。 */

const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const JS_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'sales-order-progress.js');
const DOC_PATH = path.join(ROOT, 'docs', 'dynamic-shipment-finance-report.md');

let passed = 0;
function ok(cond, msg) { assert.ok(cond, msg); passed += 1; }
function eq(actual, expected, msg) { assert.deepStrictEqual(actual, expected, msg); passed += 1; }

const mod = require(JS_PATH);

// ---- 1. 汇总模式选择（仅 ERP-162 白名单：none / customerCurrency / customerCurrencyShipment / customerCurrencyFinance）----
eq(mod.dsfSummaryMode('none'), 'none', 'none 合法模式');
eq(mod.dsfSummaryMode('customerCurrency'), 'customerCurrency', 'customerCurrency 合法模式');
eq(mod.dsfSummaryMode('customerCurrencyShipment'), 'customerCurrencyShipment', 'customerCurrencyShipment 合法模式');
eq(mod.dsfSummaryMode('customerCurrencyFinance'), 'customerCurrencyFinance', 'customerCurrencyFinance 合法模式');
eq(mod.dsfSummaryMode('supplierCurrency'), 'none', '范围外模式回落 none（fail closed）');
eq(mod.dsfSummaryMode(''), 'none', '空模式回落 none');
eq(mod.dsfSummaryMode(null), 'none', 'null 模式回落 none');
eq(mod.dsfSummaryMode(' customerCurrency '), 'customerCurrency', '空白被修剪后仍为合法模式');

const selectHtml = mod.dsfSummarySelectHtml('none');
for (const k of ['none', 'customerCurrency', 'customerCurrencyShipment', 'customerCurrencyFinance']) {
  ok(selectHtml.includes(`value="${k}"`), `选择器包含模式 ${k}`);
}
ok(selectHtml.includes('id="dsf-summarymode"'), '选择器 id 正确');
ok(!/<input[^>]*type="text"/i.test(selectHtml), '汇总选择器无自由文本输入');
ok(!selectHtml.includes('value="supplierCurrency"'), '选择器不含范围外模式');

// ---- 2. 请求边界：携带选中汇总模式（非法值绝不进入请求），并复用有界筛选 ----
const catalogFields = [{ key: 'orderNo', label: '订单号', dataType: 'text' }];
const req = mod.dsfBuildRequest({
  catalogFields,
  selectedKeys: ['orderNo'],
  summaryMode: 'customerCurrencyShipment',
  customerId: '123',
  currency: 'CNY',
  shipmentStatus: 'shipped',
  page: 2, pageSize: 50, maxPageSize: 200,
});
eq(req.summaryMode, 'customerCurrencyShipment', '请求携带选中汇总模式');
eq(req.customerId, 123, '有界筛选客户 Id 随请求发送');
eq(req.currency, 'CNY', '有界筛选币种随请求发送');
eq(req.shipmentStatus, 'shipped', '有界筛选出货状态随请求发送');
eq(req.page, 2, '分页随请求发送');

const reqBad = mod.dsfBuildRequest({
  catalogFields, selectedKeys: [], summaryMode: 'bogus', page: 1, pageSize: 50, maxPageSize: 200,
});
eq(reqBad.summaryMode, 'none', '非法模式回落 none（绝不进入请求）');

const reqDefault = mod.dsfBuildRequest({
  catalogFields, selectedKeys: [], page: 1, pageSize: 50, maxPageSize: 200,
});
eq(reqDefault.summaryMode, 'none', '未选择时默认 none');

// ---- 3. 金额汇总表：客户 / 原币 / 订单金额 / 已关联 / 未覆盖 / 已提交证据，不同币种分别成行 ----
const multi = mod.dsfSummaryHtml({
  summaryMode: 'customerCurrency',
  summaries: [
    { customerId: 1, customerName: '甲客户', currency: 'CNY', shipmentStatus: null, financeLinkStatus: null, orderCount: 2, orderAmount: 200, knownLinkedAmountRows: 2, unknownLinkedAmountRows: 0, linkedAmount: 150, knownUncoveredAmountRows: 2, unknownUncoveredAmountRows: 0, uncoveredAmount: 50, knownSubmittedAmountRows: 1, unknownSubmittedAmountRows: 1, submittedAmount: null },
    { customerId: 1, customerName: '甲客户', currency: 'USD', shipmentStatus: null, financeLinkStatus: null, orderCount: 1, orderAmount: 120, knownLinkedAmountRows: 0, unknownLinkedAmountRows: 1, linkedAmount: null, knownUncoveredAmountRows: 0, unknownUncoveredAmountRows: 1, uncoveredAmount: null, knownSubmittedAmountRows: 0, unknownSubmittedAmountRows: 1, submittedAmount: null },
  ],
});
ok(multi.includes('当前页金额汇总'), '汇总标题可见');
ok(multi.includes('甲客户'), '客户标签可见');
ok(multi.includes('>CNY</b>') && multi.includes('>USD</b>'), '原币分别成行');
ok(multi.includes('>200</td>') && multi.includes('>120</td>'), '订单金额保持原币');
ok(multi.includes('>150</td>') && multi.includes('>50</td>'), '已知已关联 / 未覆盖金额可见');
ok(multi.includes('未知'), 'null 金额证据显示未知（绝不回落 0）');
ok(!multi.includes('>320</td>'), '不同币种绝不合并（无跨币种总额）');
ok(multi.includes('已关联·未知行') && multi.includes('未覆盖·未知行') && multi.includes('已提交·未知行'), '未知行数列可见');

// ---- 4. 出货状态 / 收款链接状态拆分 ----
const ship = mod.dsfSummaryHtml({
  summaryMode: 'customerCurrencyShipment',
  summaries: [
    { customerId: 1, customerName: '甲客户', currency: 'CNY', shipmentStatus: 'partial', financeLinkStatus: null, orderCount: 1, orderAmount: 100, knownLinkedAmountRows: 1, unknownLinkedAmountRows: 0, linkedAmount: 60, knownUncoveredAmountRows: 1, unknownUncoveredAmountRows: 0, uncoveredAmount: 40, knownSubmittedAmountRows: 1, unknownSubmittedAmountRows: 0, submittedAmount: 0 },
  ],
});
ok(ship.includes('出货状态') && ship.includes('部分出货'), '按出货状态拆分可见');

const fin = mod.dsfSummaryHtml({
  summaryMode: 'customerCurrencyFinance',
  summaries: [
    { customerId: 1, customerName: '甲客户', currency: 'CNY', shipmentStatus: null, financeLinkStatus: 'unlinked', orderCount: 1, orderAmount: 100, knownLinkedAmountRows: 0, unknownLinkedAmountRows: 1, linkedAmount: null, knownUncoveredAmountRows: 0, unknownUncoveredAmountRows: 1, uncoveredAmount: null, knownSubmittedAmountRows: 0, unknownSubmittedAmountRows: 1, submittedAmount: null },
  ],
});
ok(fin.includes('收款链接状态') && fin.includes('未链接（金额未知）'), '按收款链接状态拆分可见');

// ---- 5. null 金额证据与未知行数 ----
const unknown = mod.dsfSummaryHtml({
  summaryMode: 'customerCurrency',
  summaries: [
    { customerId: 1, customerName: '乙客户', currency: 'CNY', shipmentStatus: null, financeLinkStatus: null, orderCount: 3, orderAmount: 90, knownLinkedAmountRows: 1, unknownLinkedAmountRows: 2, linkedAmount: null, knownUncoveredAmountRows: 1, unknownUncoveredAmountRows: 2, uncoveredAmount: null, knownSubmittedAmountRows: 1, unknownSubmittedAmountRows: 2, submittedAmount: null },
  ],
});
ok(unknown.includes('>90</td>'), '已知订单金额原样显示');
ok(unknown.includes('未知'), '未知金额显示未知（绝不回落 0）');
ok(unknown.includes('>2</td>'), '未知行数显示 2');

// ---- 6. none / 空汇总 / 空视图可见状态 ----
eq(mod.dsfSummaryHtml({ summaryMode: 'none', summaries: [] }), '', 'none 不渲染汇总');
eq(mod.dsfSummaryHtml(null), '', '空视图不渲染汇总');
const emptySummary = mod.dsfSummaryHtml({ summaryMode: 'customerCurrency', summaries: [] });
ok(emptySummary.includes('本页没有可汇总金额的销售订单出货 / 财务进度证据'), '空汇总可见提示');
ok(emptySummary.includes('仅当前预览页'), '空汇总仍标注仅当前预览页');

// ---- 7. 安全转义渲染 ----
const unsafe = mod.dsfSummaryHtml({
  summaryMode: 'customerCurrency',
  summaries: [
    { customerId: 1, customerName: '<script>恶意</script>', currency: 'CNY', shipmentStatus: null, financeLinkStatus: null, orderCount: 1, orderAmount: 1, knownLinkedAmountRows: 0, unknownLinkedAmountRows: 1, linkedAmount: null, knownUncoveredAmountRows: 0, unknownUncoveredAmountRows: 1, uncoveredAmount: null, knownSubmittedAmountRows: 0, unknownSubmittedAmountRows: 1, submittedAmount: null },
  ],
});
ok(unsafe.includes('&lt;script&gt;恶意&lt;/script&gt;'), '客户名称被转义');
ok(!unsafe.includes('<script>恶意</script>'), '未转义标签不出现');

// ---- 8. 翻页：汇总随页更新（当前页证据，非全量）----
const page1 = mod.dsfSummaryHtml({
  summaryMode: 'customerCurrency',
  summaries: [
    { customerId: 1, customerName: '甲客户', currency: 'CNY', shipmentStatus: null, financeLinkStatus: null, orderCount: 2, orderAmount: 200, knownLinkedAmountRows: 2, unknownLinkedAmountRows: 0, linkedAmount: 100, knownUncoveredAmountRows: 2, unknownUncoveredAmountRows: 0, uncoveredAmount: 100, knownSubmittedAmountRows: 1, unknownSubmittedAmountRows: 1, submittedAmount: null },
  ],
});
const page2 = mod.dsfSummaryHtml({
  summaryMode: 'customerCurrency',
  summaries: [
    { customerId: 1, customerName: '甲客户', currency: 'CNY', shipmentStatus: null, financeLinkStatus: null, orderCount: 1, orderAmount: 100, knownLinkedAmountRows: 1, unknownLinkedAmountRows: 0, linkedAmount: 50, knownUncoveredAmountRows: 1, unknownUncoveredAmountRows: 0, uncoveredAmount: 50, knownSubmittedAmountRows: 1, unknownSubmittedAmountRows: 0, submittedAmount: 25 },
  ],
});
ok(page1.includes('>200</td>') && page1.includes('>100</td>'), '第 1 页汇总');
ok(page2.includes('>100</td>') && page2.includes('>50</td>'), '翻页后汇总随页更新');

// ---- 9. 权限 / 网络失败态（fail closed）----
ok(mod.dsfErrorHtml('forbidden', '无销售订单菜单授权').includes('权限不足'), '授权失败可见');
ok(mod.dsfErrorHtml('unauthorized', '请先登录').includes('未登录'), '未登录可见');
ok(mod.dsfErrorHtml('network', 'fetch failed').includes('网络请求失败'), '网络失败可见');
ok(mod.dsfErrorHtml('invalid', '无效汇总模式').includes('请求无效'), '无效请求可见');
eq(mod.dsfKindOfCode(2002), 'forbidden', '2002 → 权限不足');
eq(mod.dsfKindOfCode(2000), 'unauthorized', '2000 → 未登录');
eq(mod.dsfKindOfCode(2003), 'unauthorized', '2003 → 未登录');

// ---- 10. 接线契约与文档（汇总范围与下载范围）----
const jsSrc = fs.readFileSync(JS_PATH, 'utf8');
ok(jsSrc.includes('id="dsf-summarymode"'), '设计器接线汇总模式选择器');
ok(jsSrc.includes('req.summaryMode'), '请求体包含 summaryMode');
ok(jsSrc.includes('仅当前预览页'), '汇总仅当前预览页语义已接线');
ok(jsSrc.includes('不是应收余额'), '未覆盖金额不是应收余额语义已接线');
ok(!/FromSql|ExecuteSql|SqlCommand|SELECT\s+\*/i.test(jsSrc), '不执行任意 SQL');
ok(!/GrandTotal|跨币种合计|Grand\s+Total/i.test(jsSrc), '无跨币种总额');

const doc = fs.readFileSync(DOC_PATH, 'utf8');
ok(doc.includes('ERP-163'), '文档登记 ERP-163');
ok(doc.includes('customerCurrencyShipment') && doc.includes('customerCurrencyFinance'), '文档说明汇总模式');
ok(doc.includes('仅当前预览页'), '文档标注汇总仅当前预览页');
ok(doc.includes('不进入行导出范围'), '文档说明汇总不进入行导出范围');
ok(doc.includes('Excel（ERP-158）') && doc.includes('PDF（ERP-159）'), '文档说明既有 Excel / PDF 下载范围');

console.log('\n✅ ERP-163 动态销售订单出货/财务进度金额汇总 UI 测试全部通过（' + passed + ' 项断言）。\n');


