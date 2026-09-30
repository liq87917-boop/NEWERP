'use strict';
/* ERP-169 前端 PDF 下载接线单测：设计器工具栏「导出 PDF（当前页）」按钮、export-pdf 接口路径、
   成功（application/pdf 附件）下载、空 / 授权 / 网络失败可见、两类证据分开导出契约，以及无任意 SQL。
   运行：node tests/automation/dynamic_receipt_reconciliation_pdf_ui.test.js
   失败时抛异常并以非零码退出。 */

const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const JS_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'sales-order-receipt-reconciliation.js');

let passed = 0;
function ok(cond, msg) { assert.ok(cond, msg); passed += 1; }

const mod = require(JS_PATH);
const jsSrc = fs.readFileSync(JS_PATH, 'utf8');

// ---- 1. 导出函数与设计器按钮接线 ----
ok(typeof mod.drrExportPdf === 'function', 'drrExportPdf 已导出');
ok(jsSrc.includes('onclick="drrExportPdf()"'), '设计器工具栏提供「导出 PDF（当前页）」按钮');
ok(jsSrc.includes('/api/sales-orders/dynamic-receipt-reconciliation-report/export-pdf'),
  'PDF 下载复用只读导出接口 export-pdf');

// ---- 2. 成功路径：识别 application/pdf 附件并触发下载 ----
ok(jsSrc.includes("contentType.indexOf('application/pdf')"), 'PDF 响应按 application/pdf 内容类型识别');
ok(jsSrc.includes("a.download = '客户订单与收款核对报表_'"), '成功时触发 PDF 附件下载');
ok(jsSrc.includes('URL.createObjectURL'), '成功时创建 blob 对象 URL');

// ---- 3. 失败态可见：空 / 授权 / 网络失败均渲染到结果区 ----
ok(jsSrc.includes("drrErrorHtml('empty'"), '空结果失败可见');
ok(jsSrc.includes("drrErrorHtml('unauthorized'"), '授权失败可见');
ok(jsSrc.includes("drrErrorHtml('network'"), '网络失败可见');
ok(jsSrc.includes("drrRenderResult(drrErrorHtml('invalid'"), '未预览 / 无效请求失败可见');

// ---- 4. 两类证据分开导出契约（订单证据 / 未关联收款证据绝不合并） ----
ok(jsSrc.includes('function drrExportOrderCsv'), '订单证据 CSV 分开导出保留');
ok(jsSrc.includes('function drrExportReceiptCsv'), '未关联收款证据 CSV 分开导出保留');
ok(typeof mod.drrExportExcel === 'function', 'Excel 两类证据独立工作表导出保留');

// ---- 5. 无任意 SQL / 自由字段名 ----
ok(!/FromSql|ExecuteSql|SqlCommand|SELECT\s+\*/i.test(jsSrc), '前端不执行任意 SQL');

console.log('\n✅ ERP-169 客户订单与收款核对 PDF 下载 UI 逻辑测试全部通过（' + passed + ' 项断言）。\n');
