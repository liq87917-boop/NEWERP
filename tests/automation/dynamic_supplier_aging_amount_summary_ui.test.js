'use strict';
/* ERP-147 前端金额汇总 UI 逻辑单测：汇总模式选择（仅 ERP-146 白名单）、请求携带 summaryMode、按币种分行、
   未知值保持未知、草稿/作废排除说明、安全转义渲染、翻页随页更新，以及权限/网络失败态与文档接线契约。
   运行：node tests/automation/dynamic_supplier_aging_amount_summary_ui.test.js
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

// ---- 1. 汇总模式选择（仅 ERP-146 白名单：none / supplierCurrency / supplierCurrencyAging） ----
eq(mod.sraDesSummaryMode('none'), 'none', 'none 合法模式');
eq(mod.sraDesSummaryMode('supplierCurrency'), 'supplierCurrency', 'supplierCurrency 合法模式');
eq(mod.sraDesSummaryMode('supplierCurrencyAging'), 'supplierCurrencyAging', 'supplierCurrencyAging 合法模式');
eq(mod.sraDesSummaryMode('supplierCurrencyQuarterly'), 'none', '非法模式回落 none（fail closed）');
eq(mod.sraDesSummaryMode(''), 'none', '空模式回落 none');
eq(mod.sraDesSummaryMode(null), 'none', 'null 模式回落 none');
eq(mod.sraDesSummaryMode(' supplierCurrency '), 'supplierCurrency', '空白被修剪后仍为合法模式');

const selectHtml = mod.sraDesSummarySelectHtml('none');
for (const k of ['none', 'supplierCurrency', 'supplierCurrencyAging']) {
  ok(selectHtml.includes(`value="${k}"`), `选择器包含模式 ${k}`);
}
ok(selectHtml.includes('id="sra-des-summarymode"'), '选择器 id 正确');
ok(!/<input[^>]*type="text"/i.test(selectHtml), '汇总选择器无自由文本输入');
ok(!selectHtml.includes('value="supplierCurrencyQuarterly"'), '选择器不含范围外模式');

// ---- 2. 请求边界：携带选中汇总模式（非法值绝不进入请求） ----
const catalogFields = [{ key: 'supplierName', label: '供应商名称', dataType: 'text' }];
const req = mod.sraDesBuildRequest({
  catalogFields,
  selectedKeys: ['supplierName'],
  summaryMode: 'supplierCurrencyAging',
  page: 1, pageSize: 50, maxPageSize: 200,
});
eq(req.summaryMode, 'supplierCurrencyAging', '请求携带选中汇总模式');

const reqBad = mod.sraDesBuildRequest({
  catalogFields, selectedKeys: [], summaryMode: 'bogus', page: 1, pageSize: 50, maxPageSize: 200,
});
eq(reqBad.summaryMode, 'none', '非法模式回落 none（绝不进入请求）');

const reqDefault = mod.sraDesBuildRequest({
  catalogFields, selectedKeys: [], page: 1, pageSize: 50, maxPageSize: 200,
});
eq(reqDefault.summaryMode, 'none', '未选择时默认 none');

// ---- 3. 金额汇总表：供应商 / 币种 / 可选账龄分桶 / 含税总额 / 有效已分配 / 算术剩余，未知值保持未知，草稿/作废排除说明 ----
const view = {
  summaryMode: 'supplierCurrencyAging',
  summaries: [
    { supplierId: 968001, supplierCode: 'S968001', supplierName: '甲供应商', currency: 'CNY', agingBucket: 'not_due', agingBucketText: '未到期', invoiceCount: 2, grossAmount: 200, activeAllocatedAmount: 50, remainingAmount: 150, unknownRemainingInvoiceCount: 0, overAllocatedInvoiceCount: 0 },
    { supplierId: 968001, supplierCode: 'S968001', supplierName: '甲供应商', currency: 'USD', agingBucket: 'unknown_due_date', agingBucketText: '未知到期日', invoiceCount: 1, grossAmount: 120, activeAllocatedAmount: null, remainingAmount: null, unknownRemainingInvoiceCount: 1, overAllocatedInvoiceCount: 1 },
  ],
};
const html = mod.sraDesSummaryHtml(view);
ok(html.includes('当前页金额汇总'), '汇总标题可见');
ok(html.includes('仅当前预览页，非全量合计'), '标注仅当前预览页');
ok(html.includes('甲供应商') && html.includes('S968001'), '供应商名称与编码可见');
ok(html.includes('<b>CNY</b>') && html.includes('<b>USD</b>'), '不同币种分别成行');
ok(html.includes('未到期') && html.includes('未知到期日'), '账龄分桶（含未知到期日）可见');
ok(html.includes('含税总额证据') && html.includes('有效已分配') && html.includes('算术剩余证据'), '三列金额标签可见');
ok(html.includes('>200</td>') && html.includes('>50</td>') && html.includes('>150</td>'), '已知金额原样显示');
ok(html.includes('未知'), '未知金额显示未知（绝不回落 0）');
ok(html.includes('草稿 / 已作废金额绝不并入'), '草稿 / 作废排除说明可见');

// ---- 4. 无账龄分桶模式（supplierCurrency）：不渲染账龄分桶列 ----
const noBucket = mod.sraDesSummaryHtml({
  summaryMode: 'supplierCurrency',
  summaries: [{ supplierId: 1, supplierCode: 'S1', supplierName: '乙供应商', currency: 'CNY', invoiceCount: 1, grossAmount: 100, activeAllocatedAmount: 0, remainingAmount: 100, unknownRemainingInvoiceCount: 0, overAllocatedInvoiceCount: 0 }],
});
ok(!noBucket.includes('账龄分桶'), 'supplierCurrency 模式无账龄分桶列');

// ---- 5. none / 空汇总 / 空页可见状态 ----
eq(mod.sraDesSummaryHtml({ summaryMode: 'none', summaries: [] }), '', 'none 不渲染汇总');
eq(mod.sraDesSummaryHtml(null), '', '空视图不渲染汇总');
const emptySummary = mod.sraDesSummaryHtml({ summaryMode: 'supplierCurrency', summaries: [] });
ok(emptySummary.includes('本页没有可汇总金额的有效发票证据'), '空汇总可见提示');
ok(emptySummary.includes('仅当前预览页'), '空汇总仍标注仅当前预览页');

// ---- 6. 安全转义渲染 ----
const unsafe = mod.sraDesSummaryHtml({
  summaryMode: 'supplierCurrency',
  summaries: [{ supplierId: 1, supplierCode: '<i>SC</i>', supplierName: '<script>恶意</script>', currency: 'CNY', invoiceCount: 1, grossAmount: 1, activeAllocatedAmount: 0, remainingAmount: 1, unknownRemainingInvoiceCount: 0, overAllocatedInvoiceCount: 0 }],
});
ok(unsafe.includes('&lt;script&gt;恶意&lt;/script&gt;'), '供应商名称被转义');
ok(!unsafe.includes('<script>恶意</script>'), '未转义标签不出现');
ok(unsafe.includes('&lt;i&gt;SC&lt;/i&gt;'), '供应商编码被转义');

// ---- 7. 翻页：汇总随页更新（当前页证据，非全量） ----
const page1 = mod.sraDesSummaryHtml({
  summaryMode: 'supplierCurrency',
  summaries: [{ supplierId: 1, supplierCode: 'S1', supplierName: '甲供应商', currency: 'CNY', invoiceCount: 2, grossAmount: 200, activeAllocatedAmount: 100, remainingAmount: 100, unknownRemainingInvoiceCount: 0, overAllocatedInvoiceCount: 0 }],
});
const page2 = mod.sraDesSummaryHtml({
  summaryMode: 'supplierCurrency',
  summaries: [{ supplierId: 1, supplierCode: 'S1', supplierName: '甲供应商', currency: 'CNY', invoiceCount: 1, grossAmount: 100, activeAllocatedAmount: 50, remainingAmount: 50, unknownRemainingInvoiceCount: 0, overAllocatedInvoiceCount: 0 }],
});
ok(page1.includes('>2</td>') && page1.includes('>200</td>'), '第 1 页汇总');
ok(page2.includes('>1</td>') && page2.includes('>100</td>'), '翻页后汇总随页更新');

// ---- 8. 权限 / 网络失败态（fail closed） ----
ok(mod.sraDesErrorHtml('forbidden', '无采购订单菜单授权').includes('权限不足'), '授权失败可见');
ok(mod.sraDesErrorHtml('unauthorized', '请先登录').includes('未登录'), '未登录可见');
ok(mod.sraDesErrorHtml('network', 'fetch failed').includes('网络请求失败'), '网络失败可见');
ok(mod.sraDesErrorHtml('invalid', '无效汇总模式').includes('请求无效'), '无效请求可见');
eq(mod.sraDesKindOfCode(2002), 'forbidden', '2002 → 权限不足');
eq(mod.sraDesKindOfCode(2000), 'unauthorized', '2000 → 未登录');
eq(mod.sraDesKindOfCode(2003), 'unauthorized', '2003 → 未登录');

// ---- 9. 接线契约与文档（汇总与下载范围） ----
const jsSrc = fs.readFileSync(JS_PATH, 'utf8');
ok(jsSrc.includes('id="sra-des-summarymode"'), '设计器接线汇总模式选择器');
ok(jsSrc.includes('req.summaryMode'), '请求体包含 summaryMode');
ok(jsSrc.includes('仅当前预览页'), '汇总仅当前预览页语义已接线');
ok(jsSrc.includes('草稿 / 已作废金额绝不并入'), '草稿 / 作废排除已接线');
ok(!/FromSql|ExecuteSql|SqlCommand|SELECT\s+\*/i.test(jsSrc), '不执行任意 SQL');
ok(!/GrandTotal|跨币种合计|Grand\s+Total/i.test(jsSrc), '无跨币种总额');

const doc = fs.readFileSync(DOC_PATH, 'utf8');
ok(doc.includes('ERP-147'), '文档登记 ERP-147');
ok(doc.includes('supplierCurrencyAging'), '文档说明汇总模式');
ok(doc.includes('仅当前预览页'), '文档标注汇总仅当前预览页');
ok(doc.includes('不进入'), '文档说明汇总不进入行导出范围');

console.log('\n✅ ERP-147 动态供应商对账与账龄金额汇总 UI 测试全部通过（' + passed + ' 项断言）。\n');

