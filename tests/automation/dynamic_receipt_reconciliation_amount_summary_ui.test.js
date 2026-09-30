'use strict';
/* ERP-173 前端当前页金额汇总 UI 逻辑单测：金额汇总模式选择（仅 ERP-172 白名单 none / customerCurrency）、
   scope-safe 请求携带金额汇总模式、两个独立金额汇总面板（订单 / 未关联收款，仅当前预览页）渲染、
   多币种 / null 未知 / 已知未知行数 / 截断 / 转义 / 空态、错误态，以及前端接线契约（无任意 SQL / 跨币种合计）。
   运行：node tests/automation/dynamic_receipt_reconciliation_amount_summary_ui.test.js
   失败时抛异常并以非零码退出。 */

const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const JS_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'sales-order-receipt-reconciliation.js');
const DOC = path.join(ROOT, 'docs', 'dynamic-receipt-reconciliation-report.md');

let passed = 0;
function ok(cond, msg) { assert.ok(cond, msg); passed += 1; }
function eq(actual, expected, msg) { assert.deepStrictEqual(actual, expected, msg); passed += 1; }

const mod = require(JS_PATH);

// ---- 1. 金额汇总模式规范化（仅 ERP-172 白名单：none / customerCurrency）----
eq(mod.drrSummaryModeKey('customerCurrency'), 'customerCurrency', 'customerCurrency 合法金额汇总模式');
eq(mod.drrSummaryModeKey('none'), 'none', 'none 合法金额汇总模式');
eq(mod.drrSummaryModeKey('supplier'), 'none', '非法金额汇总模式回落 none（fail closed）');
eq(mod.drrSummaryModeKey(''), 'none', '空金额汇总模式回落 none');
eq(mod.drrSummaryModeKey(null), 'none', 'null 金额汇总模式回落 none');
eq(mod.drrSummaryModeKey(' CustomerCurrency '), 'customerCurrency', '空白被修剪后仍为合法模式');
eq(mod.drrSummaryModeKey('CUSTOMERCURRENCY'), 'customerCurrency', '金额汇总模式大小写不敏感（保留规范键）');
eq(mod.drrSummaryModeKey('amountTotal'), 'none', '范围外金额汇总模式回落 none');

// 选择器仅提供 ERP-172 白名单键，无自由输入
const selectHtml = mod.drrSummaryModeSelectHtml('none');
for (const k of ['none', 'customerCurrency']) {
  ok(selectHtml.includes(`value="${k}"`), `选择器包含金额汇总模式 ${k}`);
}
ok(selectHtml.includes('id="drr-summary-mode"'), '选择器 id 正确');
ok(!/<input[^>]*type="text"/i.test(selectHtml), '金额汇总选择器无自由文本输入');
ok(!selectHtml.includes('value="supplier"'), '选择器不含范围外金额汇总模式');

// ---- 2. 请求边界：携带选中的金额汇总模式（非法值绝不进入请求）----
const orderFields = [{ key: 'orderNo', label: '订单号', dataType: 'text' }];
const receiptFields = [{ key: 'receiptNo', label: '收款单号', dataType: 'text' }];
const req = mod.drrBuildRequest({
  catalogFields: orderFields, receiptFields,
  selectedKeys: ['orderNo'], selectedReceiptKeys: ['receiptNo'],
  summaryMode: 'customerCurrency', page: 1, pageSize: 50, maxPageSize: 200,
});
eq(req.summaryMode, 'customerCurrency', '请求携带选中金额汇总模式');

const reqNone = mod.drrBuildRequest({
  catalogFields: orderFields, receiptFields,
  selectedKeys: [], selectedReceiptKeys: [],
  summaryMode: 'bogus', page: 1, pageSize: 50, maxPageSize: 200,
});
eq(reqNone.summaryMode, 'none', '非法金额汇总模式回落 none（绝不进入请求）');

const reqDefault = mod.drrBuildRequest({
  catalogFields: orderFields, receiptFields,
  selectedKeys: [], selectedReceiptKeys: [],
  page: 1, pageSize: 50, maxPageSize: 200,
});
eq(reqDefault.summaryMode, 'none', '未选择时默认 none');

// ---- 3. 两个独立金额汇总面板（订单 + 未关联收款各自渲染，仅当前预览页）----
const customerCurrencyView = {
  summaryMode: 'customerCurrency',
  orderSummaries: [
    { customerId: 1, customerName: '甲客户', currency: 'CNY', orderCount: 2, orderAmount: 1234.5, knownLinkedReceiptAmountRows: 1, unknownLinkedReceiptAmountRows: 1, linkedReceiptAmount: null, knownUncoveredAmountRows: 2, unknownUncoveredAmountRows: 0, uncoveredAmount: 234.56 },
  ],
  receiptSummaries: [
    { customerId: 1, customerName: '甲客户', currency: 'CNY', evidenceStatus: 'active', receiptCount: 3, amount: 500, truncated: false },
  ],
};
const orderPanel = mod.drrOrderSummaryPanelHtml(customerCurrencyView);
ok(orderPanel.includes('本页订单金额汇总'), '订单金额汇总面板标题');
ok(orderPanel.includes('甲客户') && orderPanel.includes('CNY'), '订单金额汇总按客户 + 原币');
ok(orderPanel.includes('>2</td>'), '订单张数');
ok(orderPanel.includes('1234.50'), '订单金额两位小数原币');
ok(orderPanel.includes('未知') && orderPanel.includes('已知 1 行 / 未知 1 行'), '已关联收款金额未知（null）不回落 0，且已知 / 未知行数显式');
ok(orderPanel.includes('234.56') && orderPanel.includes('已知 2 行 / 未知 0 行'), '未覆盖金额已知合计 + 已知 / 未知行数');
ok(orderPanel.includes('仅当前预览页'), '订单金额汇总仅当前预览页语义');

const receiptPanel = mod.drrReceiptSummaryPanelHtml(customerCurrencyView);
ok(receiptPanel.includes('本页未关联收款金额汇总'), '未关联收款金额汇总面板标题');
ok(receiptPanel.includes('甲客户') && receiptPanel.includes('CNY'), '未关联收款金额汇总按客户 + 原币');
ok(receiptPanel.includes('有效证据（已审核') && receiptPanel.includes('>3</td>'), 'active 证据状态与张数');
ok(receiptPanel.includes('500.00'), '未关联收款金额两位小数原币');
ok(receiptPanel.includes('仅当前预览页'), '未关联收款金额汇总仅当前预览页语义');
ok(!receiptPanel.includes('（截断）'), '未截断时不显示截断标记');

// ---- 4. 多币种 / null 未知 / 证据状态 / 截断 / 转义安全 ----
const multiView = {
  summaryMode: 'customerCurrency',
  orderSummaries: [
    { customerId: 1, customerName: '<script>甲</script>', currency: 'CNY', orderCount: 1, orderAmount: 100, knownLinkedReceiptAmountRows: 1, unknownLinkedReceiptAmountRows: 0, linkedReceiptAmount: 100, knownUncoveredAmountRows: 1, unknownUncoveredAmountRows: 0, uncoveredAmount: 0 },
    { customerId: 2, customerName: '乙客户', currency: 'USD', orderCount: 1, orderAmount: 50, knownLinkedReceiptAmountRows: 0, unknownLinkedReceiptAmountRows: 1, linkedReceiptAmount: null, knownUncoveredAmountRows: 0, unknownUncoveredAmountRows: 1, uncoveredAmount: null },
  ],
  receiptSummaries: [
    { customerId: 1, customerName: '甲客户', currency: 'CNY', evidenceStatus: 'active', receiptCount: 2, amount: 10, truncated: false },
    { customerId: 1, customerName: '甲客户', currency: 'CNY', evidenceStatus: 'pending', receiptCount: 1, amount: 5, truncated: false },
    { customerId: 1, customerName: '甲客户', currency: 'CNY', evidenceStatus: 'historical', receiptCount: 1, amount: 3, truncated: true },
    { customerId: 2, customerName: '乙客户', currency: 'USD', evidenceStatus: 'active', receiptCount: 1, amount: 20, truncated: false },
  ],
};
const orderPanelMulti = mod.drrOrderSummaryPanelHtml(multiView);
ok(orderPanelMulti.includes('CNY') && orderPanelMulti.includes('USD'), '订单金额汇总多币种分别成行');
ok(!orderPanelMulti.includes('<script>'), '订单金额汇总标签已转义');
ok(orderPanelMulti.includes('&lt;script&gt;甲&lt;/script&gt;'), '订单金额汇总标签转义为 HTML 实体');

const receiptPanelMulti = mod.drrReceiptSummaryPanelHtml(multiView);
ok(receiptPanelMulti.includes('未审核（仅列出、不计入）'), 'pending 证据状态显式拆分');
ok(receiptPanelMulti.includes('历史证据（仅历史核对、不计入）'), 'historical 证据状态显式拆分');
ok(receiptPanelMulti.includes('（截断）'), '截断标记显式保留');
ok(receiptPanelMulti.includes('CNY') && receiptPanelMulti.includes('USD'), '未关联收款金额汇总多币种分别成行');

// 未知计数 / 未知金额 / 空页 / none / 翻页变化
const unknownOrderView = { summaryMode: 'customerCurrency', orderSummaries: [{ customerId: 1, customerName: '甲客户', currency: 'CNY', orderCount: null, orderAmount: null, linkedReceiptAmount: null, uncoveredAmount: null }], receiptSummaries: [] };
ok(mod.drrOrderSummaryPanelHtml(unknownOrderView).includes('未知'), '订单金额汇总未知（null）不回落 0');
const unknownReceiptView = { summaryMode: 'customerCurrency', orderSummaries: [], receiptSummaries: [{ customerId: 1, customerName: '甲客户', currency: 'CNY', evidenceStatus: 'active', receiptCount: null, amount: null, truncated: false }] };
ok(mod.drrReceiptSummaryPanelHtml(unknownReceiptView).includes('未知'), '未关联收款金额汇总未知（null）不回落 0');

eq(mod.drrSummaryPanelsHtml({ summaryMode: 'none', orderSummaries: [], receiptSummaries: [] }), '', 'none 不渲染金额汇总面板');
eq(mod.drrSummaryPanelsHtml(null), '', '空视图不渲染金额汇总面板');
const emptySummary = mod.drrSummaryPanelsHtml({ summaryMode: 'customerCurrency', orderSummaries: [], receiptSummaries: [] });
ok(emptySummary.includes('本页没有可汇总金额的订单证据'), '订单金额汇总空页可见提示');
ok(emptySummary.includes('本页没有可汇总金额的未关联收款证据'), '未关联收款金额汇总空页可见提示');
ok(emptySummary.includes('仅当前预览页'), '空页仍标注仅当前预览页');

const page1 = { summaryMode: 'customerCurrency', orderSummaries: [{ customerId: 1, customerName: '甲客户', currency: 'CNY', orderCount: 2, orderAmount: 200, linkedReceiptAmount: 100, uncoveredAmount: 50 }], receiptSummaries: [] };
const page2 = { summaryMode: 'customerCurrency', orderSummaries: [{ customerId: 1, customerName: '甲客户', currency: 'CNY', orderCount: 1, orderAmount: 100, linkedReceiptAmount: 50, uncoveredAmount: 25 }], receiptSummaries: [] };
ok(mod.drrOrderSummaryPanelHtml(page1).includes('>2</td>'), '第 1 页金额汇总张数');
ok(mod.drrOrderSummaryPanelHtml(page2).includes('>1</td>'), '翻页后金额汇总随页更新（当前页证据）');

// ---- 5. 权限 / 网络 / 无效失败态（fail closed）----
ok(mod.drrErrorHtml('forbidden', '无销售订单菜单授权').includes('权限不足'), '授权失败可见');
ok(mod.drrErrorHtml('unauthorized', '请先登录').includes('未登录'), '未登录可见');
ok(mod.drrErrorHtml('network', 'fetch failed').includes('网络请求失败'), '网络失败可见');
ok(mod.drrErrorHtml('invalid', '未知金额汇总模式').includes('请求无效'), '无效请求可见');
eq(mod.drrKindOfCode(2002), 'forbidden', '2002 → 权限不足');
eq(mod.drrKindOfCode(2000), 'unauthorized', '2000 → 未登录');

// ---- 6. 接线契约（金额汇总选择器、summaryMode 请求、仅当前预览页语义、无任意 SQL / 跨币种合计）----
const jsSrc = fs.readFileSync(JS_PATH, 'utf8');
ok(jsSrc.includes('id="drr-summary-mode"'), '设计器接线金额汇总选择器');
ok(jsSrc.includes('req.summaryMode'), '请求体包含 summaryMode');
ok(jsSrc.includes('drrOrderSummaryPanelHtml') && jsSrc.includes('drrReceiptSummaryPanelHtml'), '两个独立金额汇总面板已接线');
ok(jsSrc.includes('仅当前预览页'), '金额汇总仅当前预览页语义已接线');
ok(!/FromSql|ExecuteSql|SqlCommand|SELECT\s+\*/i.test(jsSrc), '不执行任意 SQL');
ok(!/GrandTotal|跨币种合计|Grand\s+Total/i.test(jsSrc), '无跨币种总额');

const doc = fs.readFileSync(DOC, 'utf8');
ok(doc.includes('ERP-173'), '文档登记 ERP-173');
ok(doc.includes('金额汇总'), '文档登记金额汇总工作流');

console.log('\n✅ ERP-173 客户订单与收款核对报表当前页金额汇总 UI 测试全部通过（' + passed + ' 项断言）。\n');

