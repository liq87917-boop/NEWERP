'use strict';
/* ERP-480 定金申请单 / 货款申请单规范入口路由契约（离线可执行，无真实网络 / 无成功业务写入模拟）：

   本文件只验证**可离线判定**的接线契约，绝不伪造任何成功业务写入：
     1) 菜单路由：bill-v2.js 的 BILL_CODE_MAP **生效键**不再包含 deposit-apply / payment-apply
        （旧存储过程族写路径已被 ERP-404 门禁整体停用）；说明性注释里的原字面量不参与路由判定，
        本文件通过**真实执行 bill-v2.js** 取得的 BILL_CODE_MAP 对象来证明这一点；
     2) 路由优先级：app.js 先判 BILL_CODE_MAP 再判 MODULES，因此删除映射后菜单必然落到规范模块；
     3) 规范模块：modules-finance.js 的 MODULES['deposit-apply'] / MODULES['payment-apply'] 走
        api/finance/deposit-applies、api/finance/payment-applies（规范 Id 口径），
        来源销售订单字段是声明式可空引用（nullable: true）并声明既有来源选择器；
     4) 表单接线：crud.js 新增 / 修改 / 详情回填按 mod.api 走规范 Id，声明式可空引用留空提交 null；
     5) 既有来源选择器：finance-apply-sales-order-source.js 复用 ERP-390 / ERP-391 同一份实现；
     6) 旧族分离：旧 BILL_CONFIG 旧库单体表 Oid 读侧保持只读可用，规范 Id 与旧 Oid 之间不存在任何映射。
   真实浏览器 + 真实隔离库 + 真实授权身份（含真实 API 写入）的端到端验收由 IntegrationTests 执行
   （src/ERP.IntegrationTests/FinanceApplyCanonicalEntrySqlServerTests.cs，专用 localdb + 全新 GUID
   NEWERP_AUTOTEST 库）。本文件不声称替代该端到端验收：本环境无浏览器验收执行器（任务
   browser_acceptance.required = false），因此**不执行**真实浏览器工作流，也不把离线断言当作业务写入成功证据。

   运行：node tests/finance-apply-canonical-entry.browser.js
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

/* ============ 1. 菜单路由：真实执行 bill-v2.js 证明生效键已移除 ============ */
const billV2Src = jsSource('bill-v2.js');
const evaluateBillCodeMap = new Function('BILL_CONFIG', billV2Src + '\n;return BILL_CODE_MAP;');
const billCodeMap = evaluateBillCodeMap({});

ok(!Object.prototype.hasOwnProperty.call(billCodeMap, 'deposit-apply'),
  "生效中的 BILL_CODE_MAP 不再包含 'deposit-apply'（否则菜单打开永远无法保存的旧单据页）");
ok(!Object.prototype.hasOwnProperty.call(billCodeMap, 'payment-apply'),
  "生效中的 BILL_CODE_MAP 不再包含 'payment-apply'");
eq(billCodeMap['deposit-apply'], undefined, 'deposit-apply 不再映射到任何旧存储过程族单据类型');
eq(billCodeMap['payment-apply'], undefined, 'payment-apply 不再映射到任何旧存储过程族单据类型');
ok(Object.prototype.hasOwnProperty.call(billCodeMap, 'container-settlement'),
  '其它旧族单据映射不受影响（本次只改定金申请单 / 货款申请单路由）');

const mapStart = billV2Src.indexOf('const BILL_CODE_MAP = {');
const mapEnd = billV2Src.indexOf('};', mapStart);
ok(mapStart >= 0 && mapEnd > mapStart, 'BILL_CODE_MAP 定义存在');
const mapText = billV2Src.slice(mapStart, mapEnd);
ok(/\/\*[\s\S]*ERP-480[\s\S]*\*\//.test(mapText),
  '两条旧族映射以 ERP-480 说明「原地停用」，不再参与任何路由判定');
ok(billV2Src.includes('ERP-480'), '路由改动带 ERP-480 说明（保留旧族只读与「不映射 Oid」口径）');
ok(/\/api\/finance\/deposit-applies/.test(mapText) && /\/api\/finance\/payment-applies/.test(mapText),
  '说明中记录规范端点（api/finance/deposit-applies、api/finance/payment-applies）');

/* ============ 2. 路由优先级：先 BILL_CODE_MAP，再 MODULES ============ */
const appSrc = jsSource('app.js');
const billMapCheck = appSrc.indexOf('else if (BILL_CODE_MAP[code]) renderBillV2');
const modulesLookup = appSrc.indexOf('const mod = MODULES[code]');
ok(billMapCheck >= 0, 'app.js 仍保留 BILL_CODE_MAP 路由分支（其它旧族单据仍可打开）');
ok(modulesLookup > billMapCheck, 'app.js 在 MODULES 之前判定 BILL_CODE_MAP（移除映射即落到规范模块）');
ok(appSrc.includes("CURRENT_MODULE_CODE = code; renderModule(mod)"),
  'app.js 落到规范模块时设置 CURRENT_MODULE_CODE（既有来源选择器据此路由申请单接口）');

/* ============ 3. 规范模块注册（MODULES['deposit-apply'] / MODULES['payment-apply']） ============ */
const CURRENCY_OPTS = [{ value: 'CNY', label: 'CNY' }, { value: 'USD', label: 'USD' }];
const PAYMENT_OPTS = [{ value: 'BankTransfer', label: '银行转账' }];
const MODULES = {};
new Function('MODULES', 'CURRENCY_OPTS', 'PAYMENT_OPTS', jsSource('modules-finance.js'))(
  MODULES, CURRENCY_OPTS, PAYMENT_OPTS);

const depositApply = MODULES['deposit-apply'];
const paymentApply = MODULES['payment-apply'];
ok(!!depositApply && !!paymentApply, "modules-finance.js 注册了 'deposit-apply' / 'payment-apply' 规范模块");
eq(depositApply.api, '/api/finance/deposit-applies',
  '定金申请单规范模块走 api/finance/deposit-applies（规范 Id 口径，不是 api/v2/bills）');
eq(paymentApply.api, '/api/finance/payment-applies',
  '货款申请单规范模块走 api/finance/payment-applies（规范 Id 口径）');
eq(depositApply.canSubmit, true, '定金申请单支持提交 / 审核（状态流转入口可用）');
eq(paymentApply.canSubmit, true, '货款申请单支持提交 / 审核');

const depositSourceField = depositApply.fields.find(f => f.key === 'salesOrderId');
const paymentSourceField = paymentApply.fields.find(f => f.key === 'salesOrderId');
ok(!!depositSourceField && !!paymentSourceField, '两个申请单模块都声明来源销售订单字段（显式关联来源）');
eq(depositSourceField.type, 'number', '来源销售订单为数值字段（实体 long? 同形）');
eq(paymentSourceField.type, 'number', '来源销售订单为数值字段（实体 long? 同形）');
eq(depositSourceField.nullable, true, '定金申请单来源是声明式可空引用（留空提交 null，而不是哨兵值 0）');
eq(paymentSourceField.nullable, true, '货款申请单来源是声明式可空引用（留空提交 null，而不是哨兵值 0）');
eq(depositSourceField.selector, 'finance-apply-sales-order-source', '定金申请单复用既有来源选择器');
eq(paymentSourceField.selector, 'finance-apply-sales-order-source', '货款申请单复用既有来源选择器');

const modulesSrc = jsSource('modules-finance.js');
ok(!/\/api\/v2\/bills/.test(modulesSrc), '规范模块接线绝不调用旧存储过程族');
ok(!/FromSql|ExecuteSql|SqlCommand|select\s+\*/i.test(modulesSrc), '不执行任意 SQL');

/* ============ 4. 规范 Id 接线：crud.js 新增 / 修改 / 回填 / 可空引用 ============ */
const crudSrc = jsSource('crud.js');
ok(crudSrc.includes("await api(`${mod.api}/${id}`, 'PUT', body)"),
  '修改按规范 Id（mod.api/{id}）提交，不使用任何 Oid');
ok(crudSrc.includes("await api(mod.api, 'POST', body)"), '新增提交到规范集合端点');
ok(crudSrc.includes('const row = await api(`${mod.api}/${id}`)'), '详情回填按规范 Id 读取');
ok(crudSrc.includes('f.nullable === true ? null : 0'),
  '声明式可空引用（含定金 / 货款申请单来源销售订单）留空提交 null，绝不回落为 0');
ok(crudSrc.includes('const mod = CURRENT_MODULE;'), '保存 / 回填都基于当前规范模块（mod.api）');
ok(!/\/api\/v2\/bills/.test(crudSrc), 'crud.js 规范路径绝不调用旧存储过程族');

/* ============ 5. 既有来源选择器（ERP-390 / ERP-391 共享实现，本任务不新增实现） ============ */
const pickerSrc = jsSource('finance-apply-sales-order-source.js');
ok(/\bfasInstallFormHook\b/.test(pickerSrc), '既有选择器提供表单接入钩子（按字段 selector 接线）');
ok(pickerSrc.includes("field.selector !== 'finance-apply-sales-order-source'"), '表单钩子只接管声明了本选择器的字段');
ok(pickerSrc.includes("'deposit-apply': { api: '/api/finance/deposit-applies'"),
  '选择器按当前模块路由定金申请单接口');
ok(pickerSrc.includes("'payment-apply': { api: '/api/finance/payment-applies'"),
  '选择器按当前模块路由货款申请单接口');
ok(pickerSrc.includes('fasUnlinkFill') && pickerSrc.includes('fasFormFill') && pickerSrc.includes('fasUnlinkSource'),
  '选择器支持显式断开来源（清空来源 Id → 未关联语义）');
ok(/function fasUnlinkFill\(\) \{\s*return \{ salesOrderId: '' \};/.test(pickerSrc),
  '显式断开把来源 Id 置空（留空 = 未关联），绝不臆造订单号 / 改金额 / 改汇率');
ok(/function fasFormFill\(selection\)/.test(pickerSrc) && pickerSrc.includes("return { salesOrderId: '' };"),
  '无有效选择时表单留空（显式断开），有选择时只回填权威来源 Id');
ok(pickerSrc.includes('fasAvailableCandidates'), '选择器只接受服务端标记可用的候选（已取消 / 币种不一致不可选）');
ok(pickerSrc.includes('fasShouldAcceptResponse'), '陈旧异步响应一律丢弃（客户 / 币种变更不回填旧结果）');
ok(!/\/api\/v2\/bills/.test(pickerSrc), '选择器绝不调用旧存储过程族');
ok(!/FromSql|ExecuteSql|SqlCommand|select\s+\*/i.test(pickerSrc), '选择器不执行任意 SQL');
const indexSrc = fs.readFileSync(path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'index.html'), 'utf8');
ok(indexSrc.includes('/js/finance-apply-sales-order-source.js'), 'index.html 加载既有来源选择器脚本');
ok(indexSrc.includes('/js/modules-finance.js'), 'index.html 加载规范财务模块脚本');

/* ============ 6. 旧族分离与交付说明 ============ */
const billConfigSrc = jsSource('bill-config2.js') + jsSource('bill-config3.js');
ok(/'deposit-apply': \{/.test(billConfigSrc), '旧族定金申请单配置（旧库单体表 Oid 读侧）仍保留只读可用');
ok(/'payment-apply': \{/.test(billConfigSrc), '旧族货款申请单配置仍保留只读可用');
ok(!Object.prototype.hasOwnProperty.call(billCodeMap, 'deposit-apply')
  && !Object.prototype.hasOwnProperty.call(billCodeMap, 'payment-apply'),
  '旧 Oid 与规范 Id 之间不存在任何生效映射');

const docPath = path.join(ROOT, 'docs', 'finance-apply-canonical-entry.md');
ok(fs.existsSync(docPath), '交付说明 docs/finance-apply-canonical-entry.md 存在');
const docSrc = fs.readFileSync(docPath, 'utf8');
ok(docSrc.includes('ERP-480') && docSrc.includes('api/finance/deposit-applies')
  && docSrc.includes('api/finance/payment-applies'),
  '交付说明记录规范端点与 ERP-480 口径');
ok(docSrc.includes('真实浏览器') && /未执行|不声称/.test(docSrc),
  '交付说明如实记录浏览器验收未执行（不以离线断言替代业务写入证据）');

console.log('\n✅ ERP-480 定金申请单 / 货款申请单规范入口路由 / 规范模块 / 来源选择器接线契约全部通过（'
  + passed + ' 项断言）。');
console.log('ℹ️ 真实浏览器 + 真实隔离库 + 真实授权身份（真实 API 写入）的端到端验收由 ERP.IntegrationTests 执行'
  + '（FinanceApplyCanonicalEntrySqlServerTests）；本文件为离线契约验证，不声称业务写入成功。');
console.log('ℹ️ 本环境无浏览器验收执行器（browser_acceptance.required = false）：真实浏览器 UI 工作流未执行，'
  + '不声称浏览器端到端或整阶段验收通过。');
process.exit(0);
