'use strict';
/* ERP-479 收款单 / 付款单规范入口路由契约（离线可执行，无真实网络 / 无成功业务写入模拟）：

   本文件只验证**可离线判定**的接线契约，绝不伪造任何成功业务写入：
     1) 菜单路由：bill-v2.js 的 BILL_CODE_MAP 不再把 receipt / payment 映射到旧存储过程族
        （ERP-404 写路径已 fail closed）；
     2) 路由优先级：app.js 先判 BILL_CODE_MAP 再判 MODULES，因此移除映射后菜单必然落到规范模块；
     3) 规范模块：modules-finance.js 的 MODULES['receipt'] / MODULES['payment'] 走 api/finance/receipts、
        api/finance/payments（规范 Id 口径），付款单声明可空来源引用 paymentApplyId；
     4) 引用工作台：规范收款单 / 付款单行操作复用既有收款 / 付款引用工作台入口，不新增接口、不臆造订单过滤；
     5) 规范 Id 接线：crud.js 新增 / 修改 / 详情回填按 mod.api 走规范 Id，声明式可空引用留空提交 null；
     6) 旧族分离：旧 BILL_CONFIG 旧库单体表 Oid 读侧保持只读可用，规范 Id 与旧 Oid 之间不存在任何映射。
   真实浏览器 + 真实隔离库 + 真实授权身份（含真实 API 写入）的端到端验收由 IntegrationTests 执行
   （src/ERP.IntegrationTests/ReceiptPaymentCanonicalEntrySqlServerTests.cs，专用 localdb + 全新 GUID
   NEWERP_AUTOTEST 库）。本文件不声称替代该端到端验收：本环境无浏览器验收执行器（任务
   browser_acceptance.required = false），因此**不执行**真实浏览器工作流，也不把离线断言当作业务写入成功证据。

   运行：node tests/receipt-payment-canonical-entry.browser.js
   失败时抛异常并以非零码退出。 */

const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..');
const JS_DIR = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js');
const jsPath = name => path.join(JS_DIR, name);
const jsSource = name => fs.readFileSync(jsPath(name), 'utf8');

let passed = 0;
function ok(cond, msg) { assert.ok(cond, msg); passed += 1; }
function eq(actual, expected, msg) { assert.deepStrictEqual(actual, expected, msg); passed += 1; }

/* ============ 1. 菜单路由：receipt / payment 不再映射旧存储过程族 ============ */
const billV2Src = jsSource('bill-v2.js');
const mapStart = billV2Src.indexOf('const BILL_CODE_MAP = {');
const mapEnd = billV2Src.indexOf('};', mapStart);
ok(mapStart >= 0 && mapEnd > mapStart, 'BILL_CODE_MAP 定义存在');
const billCodeMap = billV2Src.slice(mapStart, mapEnd);

ok(!/\breceipt\s*:/.test(billCodeMap), "BILL_CODE_MAP 不再把 'receipt' 映射到旧族（否则菜单打开永远无法保存的旧页）");
ok(!/\bpayment\s*:/.test(billCodeMap), "BILL_CODE_MAP 不再把 'payment' 映射到旧族");
ok(!/receipt\s*:\s*['"]/.test(billCodeMap), 'BILL_CODE_MAP 无任何 receipt 键');
ok(!/payment\s*:\s*['"]/.test(billCodeMap), 'BILL_CODE_MAP 无任何 payment 键');
ok(/'deposit-apply'\s*:\s*'deposit-apply'/.test(billCodeMap), 'BILL_CODE_MAP 仍保留其它旧族单据（本次只改收款单 / 付款单路由）');
ok(billV2Src.includes('ERP-479'), '路由改动带 ERP-479 说明（保留旧族只读与「不映射 Oid」口径）');

/* ============ 2. 路由优先级：先 BILL_CODE_MAP，再 MODULES ============ */
const appSrc = jsSource('app.js');
const billMapCheck = appSrc.indexOf('else if (BILL_CODE_MAP[code]) renderBillV2');
const modulesLookup = appSrc.indexOf('const mod = MODULES[code]');
ok(billMapCheck >= 0, 'app.js 仍保留 BILL_CODE_MAP 路由分支（其它旧族单据仍可打开）');
ok(modulesLookup > billMapCheck, 'app.js 在 MODULES 之前判定 BILL_CODE_MAP（移除映射即落到规范模块）');

/* ============ 3. 规范模块注册（MODULES['receipt'] / MODULES['payment']） ============ */
const CURRENCY_OPTS = [{ value: 'CNY', label: 'CNY' }, { value: 'USD', label: 'USD' }];
const PAYMENT_OPTS = [{ value: 'BankTransfer', label: '银行转账' }];
const MODULES = {};
new Function('MODULES', 'CURRENCY_OPTS', 'PAYMENT_OPTS', jsSource('modules-finance.js'))(MODULES, CURRENCY_OPTS, PAYMENT_OPTS);
const receipt = MODULES['receipt'];
const payment = MODULES['payment'];
ok(!!receipt && !!payment, "modules-finance.js 注册了 'receipt' / 'payment' 规范模块");
eq(receipt.api, '/api/finance/receipts', '收款单规范模块走 api/finance/receipts（规范 Id 口径，不是 api/v2/bills）');
eq(payment.api, '/api/finance/payments', '付款单规范模块走 api/finance/payments（规范 Id 口径）');
eq(receipt.canSubmit, true, '收款单支持提交 / 审核（状态流转入口可用）');
eq(payment.canSubmit, true, '付款单支持提交 / 审核（状态流转入口可用）');
ok(receipt.fields.some(f => f.key === 'customerId' && f.type === 'number'), '收款单表头客户字段为数值（服务端同形）');
ok(receipt.fields.some(f => f.key === 'amount' && f.type === 'number'), '收款单金额字段为数值（服务端正金额校验）');
ok(payment.fields.some(f => f.key === 'supplierId' && f.type === 'number'), '付款单表头供应商字段为数值');
const applyField = payment.fields.find(f => f.key === 'paymentApplyId');

/* ============ 4. 引用工作台接线（只复用既有入口，不新增接口） ============ */
const modulesSrc = jsSource('modules-finance.js');
const receiptRowActions = receipt.rowActions.map(a => a.onclick);
const paymentRowActions = payment.rowActions.map(a => a.onclick);
ok(receiptRowActions.includes('openCustomerReceiptAllocationRegisterForReceipt'),
  '收款单行操作按规范 Id 打开既有收款引用工作台');
ok(receiptRowActions.includes('openCustomerSalesInvoiceCollectionAllocationForReceipt'),
  '收款单行操作保留既有销项发票收款分摊证据入口');
ok(paymentRowActions.includes('openSupplierPaymentAllocationRegisterForPayment'),
  '付款单行操作打开既有付款引用工作台');
ok(paymentRowActions.includes('openSupplierPaymentInvoiceAllocationRegisterForPayment'),
  '付款单行操作打开既有付款发票引用工作台');
ok(receipt.extraActions.every(a => /^open[A-Za-z]+\(\)$/.test(a.onclick)),
  '收款单工具栏入口只调用既有零参数工作台入口（不传行 Id）');
ok(payment.extraActions.every(a => /^open[A-Za-z]+\(\)$/.test(a.onclick)),
  '付款单工具栏入口只调用既有零参数工作台入口（不传行 Id）');

ok(modulesSrc.includes('async function openCustomerReceiptAllocationRegisterForReceipt(receiptId)'),
  '收款单包装函数接收规范收款单 Id');
ok(modulesSrc.includes('await craSelectReceipt(receiptId)'),
  '收款单包装函数复用既有工作台选择函数按规范 Id 定位（不臆造订单过滤）');
ok(modulesSrc.includes('return openSupplierPaymentAllocationRegister();'),
  '付款单包装函数只打开既有工作台（不把付款单 Id 当作采购订单 Id）');
ok(modulesSrc.includes('return openSupplierPaymentInvoiceAllocationRegister();'),
  '付款发票引用包装函数只打开既有工作台');
ok(!/FromSql|ExecuteSql|SqlCommand|select\s+\*/i.test(modulesSrc), '不执行任意 SQL');
ok(!/\/api\/v2\/bills/.test(modulesSrc), '规范模块接线绝不调用旧存储过程族');

/* ============ 5. 规范 Id 接线：crud.js 新增 / 修改 / 回填 / 可空引用 ============ */
const crudSrc = jsSource('crud.js');
ok(crudSrc.includes("await api(`${mod.api}/${id}`, 'PUT', body)"),
  '修改按规范 Id（mod.api/{id}）提交，不使用任何 Oid');
ok(crudSrc.includes("await api(mod.api, 'POST', body)"), '新增提交到规范集合端点');
ok(crudSrc.includes('const row = await api(`${mod.api}/${id}`)'), '详情回填按规范 Id 读取');
ok(crudSrc.includes('f.nullable === true ? null : 0'),
  '声明式可空引用（含付款单来源货款申请单）留空提交 null，绝不回落为 0');
ok(crudSrc.includes('onclick="${a.onclick}(${row.id})"'),
  '行操作把规范行 Id 传给模块声明的包装函数');
ok(!/\/api\/v2\/bills/.test(crudSrc), 'crud.js 规范路径绝不调用旧存储过程族');

/* ============ 6. 旧族分离与交付说明 ============ */
const billConfigSrc = jsSource('bill-config2.js') + jsSource('bill-config3.js') + jsSource('bill-config.js');
ok(/receipt/.test(billConfigSrc), '旧族收款单配置（旧库单体表 Oid 读侧）仍保留只读可用');
ok(/payment/.test(billConfigSrc), '旧族付款单配置仍保留只读可用');
ok(!/['"]receipt['"]\s*:\s*['"]receipt['"]/.test(billV2Src.slice(mapStart, mapEnd)),
  '旧 Oid 与规范 Id 之间不存在任何映射');
const docPath = path.join(ROOT, 'docs', 'receipt-payment-canonical-entry.md');
ok(fs.existsSync(docPath), '交付说明 docs/receipt-payment-canonical-entry.md 存在');
const docSrc = fs.readFileSync(docPath, 'utf8');
ok(docSrc.includes('ERP-479') && docSrc.includes('api/finance/receipts') && docSrc.includes('api/finance/payments'),
  '交付说明记录规范端点与 ERP-479 口径');
ok(docSrc.includes('真实浏览器') && /未执行|不声称/.test(docSrc),
  '交付说明如实记录浏览器验收未执行（不以离线断言替代业务写入证据）');

console.log('\n✅ ERP-479 收款单 / 付款单规范入口路由 / 规范模块 / 引用工作台接线契约全部通过（' + passed + ' 项断言）。');
console.log('ℹ️ 真实浏览器 + 真实隔离库 + 真实授权身份（真实 API 写入）的端到端验收由 ERP.IntegrationTests 执行'
  + '（ReceiptPaymentCanonicalEntrySqlServerTests）；本文件为离线契约验证，不声称业务写入成功。');
console.log('ℹ️ 本环境无浏览器验收执行器（browser_acceptance.required = false）：真实浏览器 UI 工作流未执行，'
  + '不声称浏览器端到端或整阶段验收通过。');
process.exit(0);

ok(!!applyField, '付款单声明来源货款申请单字段（显式关联来源）');
eq(applyField.nullable, true, '来源货款申请单是声明式可空引用（留空提交 null，而不是哨兵值 0）');
eq(applyField.type, 'number', '来源货款申请单为数值字段（实体 long? 同形）');
