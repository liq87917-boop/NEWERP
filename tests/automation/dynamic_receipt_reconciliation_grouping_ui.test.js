'use strict';
/* ERP-171 前端当前页计数分组 UI 逻辑单测：分组键选择（仅 ERP-170 白名单）、scope-safe 请求携带分组键、
   两个独立计数面板（订单 / 未关联收款，仅当前预览页）渲染、unknown / 截断 / 转义 / 空态、错误态，
   以及前端接线契约（无任意 SQL / 跨币种合计）。
   运行：node tests/automation/dynamic_receipt_reconciliation_grouping_ui.test.js
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

// ---- 1. 分组键规范化（仅 ERP-170 白名单：none / customer / currency / receiptCoverageStatus / receiptEvidenceStatus）----
eq(mod.drrGroupKey('customer'), 'customer', 'customer 合法分组键');
eq(mod.drrGroupKey('currency'), 'currency', 'currency 合法分组键');
eq(mod.drrGroupKey('receiptCoverageStatus'), 'receiptCoverageStatus', 'receiptCoverageStatus 合法分组键');
eq(mod.drrGroupKey('receiptEvidenceStatus'), 'receiptEvidenceStatus', 'receiptEvidenceStatus 合法分组键');
eq(mod.drrGroupKey('none'), 'none', 'none 合法分组键');
eq(mod.drrGroupKey('supplier'), 'none', '非法分组键回落 none（fail closed）');
eq(mod.drrGroupKey(''), 'none', '空分组键回落 none');
eq(mod.drrGroupKey(null), 'none', 'null 分组键回落 none');
eq(mod.drrGroupKey(' Customer '), 'customer', '空白被修剪后仍为合法键');
eq(mod.drrGroupKey('ReceiptCoverageStatus'), 'receiptCoverageStatus', '分组键大小写不敏感（保留规范键）');
eq(mod.drrGroupKey('RECEIPTEVIDENCESTATUS'), 'receiptEvidenceStatus', '分组键大小写不敏感');

// 选择器仅提供 ERP-170 白名单键，无自由输入
const selectHtml = mod.drrGroupSelectHtml('none');
for (const k of ['none', 'customer', 'currency', 'receiptCoverageStatus', 'receiptEvidenceStatus']) {
  ok(selectHtml.includes(`value="${k}"`), `选择器包含分组键 ${k}`);
}
ok(selectHtml.includes('id="drr-groupby"'), '选择器 id 正确');
ok(!/<input[^>]*type="text"/i.test(selectHtml), '分组选择器无自由文本输入');
ok(!selectHtml.includes('value="supplier"'), '选择器不含范围外分组键');

// ---- 2. 请求边界：携带选中的分组键（非法值绝不进入请求）----
const orderFields = [{ key: 'orderNo', label: '订单号', dataType: 'text' }];
const receiptFields = [{ key: 'receiptNo', label: '收款单号', dataType: 'text' }];
const req = mod.drrBuildRequest({
  catalogFields: orderFields, receiptFields,
  selectedKeys: ['orderNo'], selectedReceiptKeys: ['receiptNo'],
  groupBy: 'customer', page: 1, pageSize: 50, maxPageSize: 200,
});
eq(req.groupBy, 'customer', '请求携带选中分组键');

const reqNone = mod.drrBuildRequest({
  catalogFields: orderFields, receiptFields,
  selectedKeys: [], selectedReceiptKeys: [],
  groupBy: 'bogus', page: 1, pageSize: 50, maxPageSize: 200,
});
eq(reqNone.groupBy, 'none', '非法分组键回落 none（绝不进入请求）');

const reqDefault = mod.drrBuildRequest({
  catalogFields: orderFields, receiptFields,
  selectedKeys: [], selectedReceiptKeys: [],
  page: 1, pageSize: 50, maxPageSize: 200,
});
eq(reqDefault.groupBy, 'none', '未选择时默认 none');

// ---- 3. 两个独立计数面板（客户分组：订单 + 未关联收款各自渲染，标签 + 计数，转义安全，仅当前预览页）----
const customerView = {
  groupBy: 'customer',
  orderGroups: [
    { key: 'customer:1', label: '甲客户', orderCount: 3 },
    { key: 'customer:2', label: '<script>乙客户</script>', orderCount: 1 },
  ],
  receiptGroups: [
    { key: 'customer:1', label: '甲客户', receiptCount: 2 },
  ],
};
const orderPanel = mod.drrOrderGroupPanelHtml(customerView);
ok(orderPanel.includes('本页订单计数'), '订单计数面板标题');
ok(orderPanel.includes('甲客户') && orderPanel.includes('>3</span>'), '订单标签与计数可见');
ok(orderPanel.includes('&lt;script&gt;乙客户&lt;/script&gt;'), '订单分组标签被转义');
ok(!orderPanel.includes('<script>乙客户</script>'), '未转义标签不出现');
ok(orderPanel.includes('仅当前预览页'), '订单面板标注仅当前预览页（非全量合计）');

const receiptPanel = mod.drrReceiptGroupPanelHtml(customerView);
ok(receiptPanel.includes('本页未关联收款计数'), '未关联收款计数面板标题');
ok(receiptPanel.includes('甲客户') && receiptPanel.includes('>2</span>'), '收款标签与计数可见');
ok(receiptPanel.includes('仅当前预览页'), '收款面板标注仅当前预览页');

const panels = mod.drrGroupPanelsHtml(customerView);
ok(panels.includes('本页订单计数') && panels.includes('本页未关联收款计数'), '两个独立计数面板同时渲染');
ok(panels.indexOf('本页订单计数') < panels.indexOf('本页未关联收款计数'), '订单面板先于收款面板');

// ---- 4. 收款覆盖状态分组：订单侧显式保留 unknown；未关联收款侧不适用 ----
const coverageView = {
  groupBy: 'receiptCoverageStatus',
  orderGroups: [
    { key: 'coverage:linked', label: '已关联（全部可计入）', orderCount: 2 },
    { key: 'coverage:partial', label: '部分可归属（其余未知）', orderCount: 0 },
    { key: 'coverage:unlinked', label: '未关联（金额未知）', orderCount: 1 },
    { key: 'coverage:unknown', label: '未知（超出派生上限）', orderCount: 1 },
  ],
  receiptGroups: [],
};
const coverageOrder = mod.drrOrderGroupPanelHtml(coverageView);
ok(coverageOrder.includes('按收款覆盖状态分组'), '收款覆盖状态分组标题');
ok(coverageOrder.includes('未知（超出派生上限）') && coverageOrder.includes('>1</span>'), 'unknown 覆盖类别显式保留');
ok(coverageOrder.includes('部分可归属（其余未知）') && coverageOrder.includes('>0</span>'), '空覆盖分类计数 0 仍显示');
const coverageReceipt = mod.drrReceiptGroupPanelHtml(coverageView);
ok(coverageReceipt.includes('订单证据侧') && coverageReceipt.includes('不适用'), '收款覆盖状态分组下未关联收款侧不适用可见');

// ---- 5. 收款证据状态分组：未关联收款侧 active / pending / historical 显式保留 + 截断；订单侧不适用 ----
const evidenceView = {
  groupBy: 'receiptEvidenceStatus',
  orderGroups: [],
  receiptGroups: [
    { key: 'evidence:active', label: '有效证据（已审核）', receiptCount: 2, truncated: true },
    { key: 'evidence:pending', label: '未审核（仅列出、不计入）', receiptCount: 1, truncated: true },
    { key: 'evidence:historical', label: '历史证据（仅历史核对、不计入）', receiptCount: 1, truncated: true },
  ],
};
const evidenceReceipt = mod.drrReceiptGroupPanelHtml(evidenceView);
ok(evidenceReceipt.includes('按收款证据状态分组'), '收款证据状态分组标题');
ok(evidenceReceipt.includes('有效证据（已审核）') && evidenceReceipt.includes('>2</span>'), 'active 证据类别可见');
ok(evidenceReceipt.includes('未审核（仅列出、不计入）') && evidenceReceipt.includes('>1</span>'), 'pending 证据类别可见');
ok(evidenceReceipt.includes('历史证据（仅历史核对、不计入）') && evidenceReceipt.includes('>1</span>'), 'historical 证据类别可见');
ok(evidenceReceipt.includes('（截断）'), '截断标记显式保留');
const evidenceOrder = mod.drrOrderGroupPanelHtml(evidenceView);
ok(evidenceOrder.includes('未关联收款证据侧') && evidenceOrder.includes('不适用'), '收款证据状态分组下订单侧不适用可见');

// ---- 6. 币种分组：不同币种分别成行，绝不合并 ----
const currencyView = {
  groupBy: 'currency',
  orderGroups: [
    { key: 'currency:CNY', label: 'CNY', orderCount: 2 },
    { key: 'currency:USD', label: 'USD', orderCount: 1 },
  ],
  receiptGroups: [
    { key: 'currency:CNY', label: 'CNY', receiptCount: 1 },
    { key: 'currency:USD', label: 'USD', receiptCount: 1 },
  ],
};
ok(mod.drrOrderGroupPanelHtml(currencyView).includes('按币种分组'), '币种分组标题');
ok(mod.drrReceiptGroupPanelHtml(currencyView).includes('CNY') && mod.drrReceiptGroupPanelHtml(currencyView).includes('USD'), '币种分组不同币种分别成行');

// ---- 7. 未知计数 / 空分组 / none / 翻页变化 ----
const unknownOrderView = { groupBy: 'customer', orderGroups: [{ key: 'customer:1', label: '甲客户', orderCount: null }], receiptGroups: [] };
ok(mod.drrOrderGroupPanelHtml(unknownOrderView).includes('未知'), '订单计数未知（null）不回落 0');
const unknownReceiptView = { groupBy: 'customer', orderGroups: [], receiptGroups: [{ key: 'customer:1', label: '甲客户', receiptCount: null }] };
ok(mod.drrReceiptGroupPanelHtml(unknownReceiptView).includes('未知'), '收款计数未知（null）不回落 0');

eq(mod.drrGroupPanelsHtml({ groupBy: 'none', orderGroups: [], receiptGroups: [] }), '', 'none 不渲染计数面板');
eq(mod.drrGroupPanelsHtml(null), '', '空视图不渲染计数面板');
const emptyCustomer = mod.drrGroupPanelsHtml({ groupBy: 'customer', orderGroups: [], receiptGroups: [] });
ok(emptyCustomer.includes('本页没有可分组计数的证据'), '动态分组空页可见提示');
ok(emptyCustomer.includes('仅当前预览页'), '空页仍标注仅当前预览页');

const page1 = { groupBy: 'customer', orderGroups: [{ key: 'customer:1', label: '甲客户', orderCount: 2 }], receiptGroups: [] };
const page2 = { groupBy: 'customer', orderGroups: [{ key: 'customer:1', label: '甲客户', orderCount: 1 }], receiptGroups: [] };
ok(mod.drrOrderGroupPanelHtml(page1).includes('>2</span>'), '第 1 页计数');
ok(mod.drrOrderGroupPanelHtml(page2).includes('>1</span>'), '翻页后计数随页更新（当前页证据）');

// ---- 8. 权限 / 网络 / 无效失败态（fail closed）----
ok(mod.drrErrorHtml('forbidden', '无销售订单菜单授权').includes('权限不足'), '授权失败可见');
ok(mod.drrErrorHtml('unauthorized', '请先登录').includes('未登录'), '未登录可见');
ok(mod.drrErrorHtml('network', 'fetch failed').includes('网络请求失败'), '网络失败可见');
ok(mod.drrErrorHtml('invalid', '未知分组键').includes('请求无效'), '无效分组可见');
eq(mod.drrKindOfCode(2002), 'forbidden', '2002 → 权限不足');
eq(mod.drrKindOfCode(2000), 'unauthorized', '2000 → 未登录');
eq(mod.drrKindOfCode(2003), 'unauthorized', '2003 → 未登录');

// ---- 9. 接线契约（分组选择器、groupBy 请求、仅当前预览页语义、无任意 SQL / 跨币种合计）----
const jsSrc = fs.readFileSync(JS_PATH, 'utf8');
ok(jsSrc.includes('id="drr-groupby"'), '设计器接线分组选择器');
ok(jsSrc.includes('req.groupBy'), '请求体包含 groupBy');
ok(jsSrc.includes('drrOrderGroupPanelHtml') && jsSrc.includes('drrReceiptGroupPanelHtml'), '两个独立计数面板已接线');
ok(jsSrc.includes('仅当前预览页'), '计数仅当前预览页语义已接线');
ok(!/FromSql|ExecuteSql|SqlCommand|SELECT\s+\*/i.test(jsSrc), '不执行任意 SQL');
ok(!/GrandTotal|跨币种合计|Grand\s+Total/i.test(jsSrc), '无跨币种总额');

const doc = fs.readFileSync(DOC, 'utf8');
ok(doc.includes('ERP-171'), '文档登记 ERP-171');
ok(doc.includes('仅当前预览页'), '文档标注计数仅当前预览页');

console.log('\n✅ ERP-171 客户订单与收款核对报表当前页计数分组 UI 测试全部通过（' + passed + ' 项断言）。\n');
