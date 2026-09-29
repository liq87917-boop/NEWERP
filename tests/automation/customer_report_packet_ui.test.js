'use strict';
/* ERP-122 前端 UI 逻辑单测：请求边界（正整数客户 / 有界分页 / 日期）、安全单元格渲染、两分区独立渲染、
   剩余证据状态标注、失败态，以及前端接线契约（登记册入口、脚本注册、接口路径、无任意 SQL）。
   运行：node tests/automation/customer_report_packet_ui.test.js
   失败时抛异常并以非零码退出。 */

const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const JS_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'customer-report-packet.js');
const CSI_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'customer-sales-invoices.js');
const INDEX_HTML = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'index.html');

let passed = 0;
function ok(cond, msg) { assert.ok(cond, msg); passed += 1; }
function eq(actual, expected, msg) { assert.deepStrictEqual(actual, expected, msg); passed += 1; }

const mod = require(JS_PATH);

// ---- 1. 请求边界（正整数客户必填、分页有界、日期只取日期部分；无任意字段名 / SQL） ----
eq(mod.cpkBuildRequest({
  customerId: '42', page: 0, pageSize: 9999,
  startDate: '2026-09-01T00:00:00', endDate: '2026-09-30',
}), { customerId: 42, page: 1, pageSize: 100, startDate: '2026-09-01', endDate: '2026-09-30' },
  '请求只含正整数客户 + 有界分页 + 日期部分');

const noDateReq = mod.cpkBuildRequest({ customerId: '7', page: 1, pageSize: 20 });
ok(!('startDate' in noDateReq) && !('endDate' in noDateReq), '无日期不携带日期键');

eq(mod.cpkBuildRequest({ customerId: '', page: 1, pageSize: 20 }), null, '无客户返回 null（fail closed）');
eq(mod.cpkBuildRequest({ customerId: '0', page: 1, pageSize: 20 }), null, '非正整数客户返回 null');
eq(mod.cpkBuildRequest({ customerId: '-5', page: 1, pageSize: 20 }), null, '负数客户返回 null');

// ---- 2. 安全单元格渲染（转义、null 为空、布尔 是/否、日期截断、剩余证据 / 分配状态映射） ----
eq(mod.cpkCellText(null, { key: 'identityText', dataType: 'text' }), '', 'null 显示为空（不回落 0）');
eq(mod.cpkCellText(undefined, { key: 'identityText', dataType: 'text' }), '', 'undefined 显示为空');
eq(mod.cpkCellText(true, { key: 'isDraft', dataType: 'boolean' }), '是');
eq(mod.cpkCellText(false, { key: 'isVoided', dataType: 'boolean' }), '否');
eq(mod.cpkCellText('2026-09-01T00:00:00', { key: 'invoiceDate', dataType: 'date' }), '2026-09-01');
eq(mod.cpkCellText('known', { key: 'remainingState', dataType: 'text' }), '剩余可确认');
eq(mod.cpkCellText('unknown', { key: 'remainingState', dataType: 'text' }), '剩余未知');
eq(mod.cpkCellText('over_allocated', { key: 'remainingState', dataType: 'text' }), '超额分摊（无效）');
eq(mod.cpkCellText('partial', { key: 'allocationState', dataType: 'text' }), '部分分摊');
eq(mod.cpkRenderCell('<script>alert(1)</script>', { key: 'identityText', dataType: 'text' }),
  '&lt;script&gt;alert(1)&lt;/script&gt;', '注入标签被转义');

// ---- 3. 剩余证据状态着色标注（known 绿 / unknown 黄 / over_allocated 红） ----
ok(mod.cpkRenderCell('known', { key: 'remainingState', dataType: 'text' }).includes('status-success'), 'known 绿色标注');
ok(mod.cpkRenderCell('unknown', { key: 'remainingState', dataType: 'text' }).includes('status-warning'), 'unknown 黄色标注');
ok(mod.cpkRenderCell('over_allocated', { key: 'remainingState', dataType: 'text' }).includes('status-danger'), 'over_allocated 红色标注');

// ---- 4. 分区表格渲染列名与单元格（转义） ----
const section = {
  columns: [
    { key: 'orderNo', label: '订单号', dataType: 'text' },
    { key: 'currency', label: '币种', dataType: 'text' },
    { key: 'totalAmount', label: '订单总额', dataType: 'number' },
  ],
  rows: [{ orderNo: 'SO-1', currency: 'USD', totalAmount: 100 }],
  total: 1, page: 1, pageSize: 20, totalPages: 1,
};
const sectionHtml = mod.cpkSectionTableHtml(section, '没有符合条件的销售订单');
ok(sectionHtml.includes('<th>订单号</th>'), '渲染返回的列名');
ok(sectionHtml.includes('>SO-1<'), '渲染单元格值');
ok(sectionHtml.includes('>USD<'), '原币可见');

// ---- 5. 两个独立分区渲染 + 空结果 + 口径文案（绝不合并） ----
const view = {
  customerId: 42,
  readOnlyText: '只读', boundaryText: '边界', disclaimerText: '免责',
  salesOrders: { columns: [{ key: 'orderNo', label: '订单号', dataType: 'text' }], rows: [], total: 0, page: 1, pageSize: 20, totalPages: 0 },
  receivableEvidence: { columns: [{ key: 'invoiceNumber', label: '发票号码', dataType: 'text' }], rows: [], total: 0, page: 1, pageSize: 20, totalPages: 0 },
};
const resultHtml = mod.cpkResultHtml(view);
ok(resultHtml.includes('销售订单'), '销售订单分区可见');
ok(resultHtml.includes('发票 / 收款分摊证据'), '发票 / 收款分摊证据分区可见');
ok(resultHtml.includes('没有符合条件的销售订单'), '销售订单分区空结果可见');
ok(resultHtml.includes('没有符合条件的发票 / 收款分摊证据'), '发票 / 收款分摊证据分区空结果可见');
ok(resultHtml.includes('只读') && resultHtml.includes('边界') && resultHtml.includes('免责'), '口径文案可见');

// ---- 6. 失败态与业务码分类 ----
ok(mod.cpkErrorHtml('forbidden', '无客户资料菜单授权').includes('权限不足'), '授权失败可见');
ok(mod.cpkErrorHtml('network', 'fetch failed').includes('网络请求失败'), '网络失败可见');
ok(mod.cpkErrorHtml('invalid', '未知字段').includes('请求无效'), '无效请求可见');
eq(mod.cpkKindOfCode(2002), 'forbidden', '2002 → 权限不足');
eq(mod.cpkKindOfCode(2000), 'unauthorized', '2000 → 未登录');

// ---- 7. 前端接线契约（登记册入口、脚本注册、接口路径、无任意 SQL / 自由字段名输入） ----
const csiSrc = fs.readFileSync(CSI_PATH, 'utf8');
ok(csiSrc.includes('openCustomerReportPacket('), 'customer-sales-invoices.js 注册工具栏入口');
const indexHtml = fs.readFileSync(INDEX_HTML, 'utf8');
ok(indexHtml.includes('/js/customer-report-packet.js'), 'index.html 注册脚本');
const jsSrc = fs.readFileSync(JS_PATH, 'utf8');
ok(jsSrc.includes('/api/customer-report-packet'), '调用 ERP-122 只读接口');
ok(!/FromSql|ExecuteSql|SqlCommand|SELECT\s+\*/i.test(jsSrc), '不执行任意 SQL');

console.log('\n✅ ERP-122 客户报告包预览 UI 逻辑测试全部通过（' + passed + ' 项断言）。\n');
