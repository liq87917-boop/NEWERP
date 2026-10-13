'use strict';
/* ERP-474 询价单「新增」菜单的规范入口路由契约（离线可执行，无真实网络 / 无成功业务写入模拟）：

   本文件只验证**可离线判定**的接线契约，绝不伪造任何成功业务写入：
     1) 菜单路由：bill-v2.js 的 BILL_CODE_MAP 不再把 inquiry-new 映射到旧存储过程族（ERP-404 写路径已 fail closed）；
     2) 规范模块：modules-doc.js 的 MODULES['inquiry-new'] 是 EF 主子表模块（api/inquiries，规范 Id + 明细字段）；
     3) 路由优先级：app.js 先判 BILL_CODE_MAP 再判 MODULES，因此移除映射后菜单必然落到规范模块；
     4) 旧族分离：旧 BILL_CONFIG.inquiry（旧库单体表 Oid 读侧）与既有授权默认值端点 / 整数天口径保持只读可用；
     5) 规范 Id 接线：crud.js 保存 / 回填与 sales-pi.js 预填 / 转报价均按规范 Id 调用 api/inquiries。
   真实浏览器 + 真实测试库 + 真实授权身份的端到端验收由 IntegrationTests 的真实 HTTP / Selenium 集合执行
   （src/ERP.IntegrationTests/InquiryCanonicalEntrySqlServerTests.cs，Collection=UiTests / 全新 GUID NEWERP_AUTOTEST 库），
   本文件不声称替代该端到端验收：环境受限时不执行真实浏览器，也不把离线断言当作业务写入成功证据。

   运行：node tests/inquiry-canonical-entry.browser.js
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

/* 与 modules.js / bill-config.js 同源的币种选项（值 = 既有 Currency 枚举值） */
const CURRENCY_OPTS = [
  { value: '1', label: '人民币 CNY' }, { value: '2', label: '美元 USD' },
  { value: '3', label: '欧元 EUR' }, { value: '4', label: '港币 HKD' },
  { value: '5', label: '英镑 GBP' }, { value: '6', label: '日元 JPY' },
];
/* 币种（枚举名口径）：EF 主子表单据（报价单 / PI）按枚举名返回币种 */
const CURRENCY_NAME_OPTS = [
  { value: 'USD', label: 'USD 美元' }, { value: 'CNY', label: 'CNY 人民币' },
  { value: 'EUR', label: 'EUR 欧元' }, { value: 'HKD', label: 'HKD 港币' },
];

/* ============ 1. 菜单路由：inquiry-new 不再映射旧存储过程族 ============ */
const billV2Src = jsSource('bill-v2.js');
const mapStart = billV2Src.indexOf('const BILL_CODE_MAP = {');
const mapEnd = billV2Src.indexOf('};', mapStart);
ok(mapStart >= 0 && mapEnd > mapStart, 'BILL_CODE_MAP 定义存在');
const billCodeMap = billV2Src.slice(mapStart, mapEnd);

ok(!/'inquiry-new'\s*:/.test(billCodeMap), "BILL_CODE_MAP 不再把 'inquiry-new' 映射到旧族（否则菜单打开无法保存的旧页）");
ok(!/['"]?inquiry['"]?\s*:\s*['"]inquiry['"]/.test(billCodeMap), "BILL_CODE_MAP 不存在 inquiry -> inquiry 的旧族映射");
ok(!/\binquiry-new\b\s*:\s*['"]/.test(billCodeMap), 'BILL_CODE_MAP 无任何 inquiry-new 键');

/* ============ 2. 路由优先级：先 BILL_CODE_MAP，再 MODULES ============ */
const appSrc = jsSource('app.js');
const billMapCheck = appSrc.indexOf('else if (BILL_CODE_MAP[code]) renderBillV2');
const modulesLookup = appSrc.indexOf('const mod = MODULES[code]');
ok(billMapCheck >= 0, 'app.js 仍保留 BILL_CODE_MAP 路由分支（旧族单据仍可打开）');
ok(modulesLookup > billMapCheck, 'app.js 在 MODULES 之前判定 BILL_CODE_MAP（移除映射即落到规范模块）');
ok(appSrc.includes("'inquiry-new': 'file-plus'"), 'inquiry-new 菜单图标仍注册（入口可见）');

/* ============ 3. 规范主子表模块注册（MODULES['inquiry-new']） ============ */
const MODULES = {};
new Function('MODULES', 'CURRENCY_OPTS', 'CURRENCY_NAME_OPTS', jsSource('modules-doc.js'))(
  MODULES, CURRENCY_OPTS, CURRENCY_NAME_OPTS);
const inquiry = MODULES['inquiry-new'];
ok(!!inquiry, "modules-doc.js 注册了 'inquiry-new' 规范模块");
eq(inquiry.api, '/api/inquiries', '规范模块走 api/inquiries（规范 Id 口径，不是 api/v2/bills）');
eq(inquiry.canSubmit, true, '规范模块支持提交（状态流转入口可用）');
eq(inquiry.detailKey, 'details', '规范模块声明主子表明细键 details');
ok(Array.isArray(inquiry.detailFields) && inquiry.detailFields.length > 0, '规范模块声明明细列（主子表表单可用）');
const detailKeys = inquiry.detailFields.map(f => f.key);
['productId', 'productName', 'spec', 'unit', 'quantity', 'unitPrice', 'amount']
  .forEach(key => ok(detailKeys.includes(key), '明细列包含 ' + key));
const amountField = inquiry.detailFields.find(f => f.key === 'amount');
ok(amountField && amountField.readonly === true, '明细金额列只读（服务端按币种精度权威派生）');
eq(inquiry.detailAmount && inquiry.detailAmount.qty, 'quantity', '明细金额 = 数量 × 单价 的接线同源');
eq(inquiry.detailAmount && inquiry.detailAmount.totalId, 'detail-total', '明细合计元素同源');
const headerKeys = inquiry.fields.map(f => f.key);
['inquiryDate', 'customerId', 'currency', 'exchangeRate', 'validDays']
  .forEach(key => ok(headerKeys.includes(key), '表头字段包含 ' + key));
const dayField = inquiry.fields.find(f => f.key === 'validDays');
eq(dayField.type, 'number', '有效期字段为整数天数值字段');
eq(dayField.default, 30, '有效期默认 30 天（既有实体默认值口径）');
const actions = (inquiry.rowActions || []).map(a => a.onclick);
ok(actions.includes('inquiryPrefillQuotation') && actions.includes('inquiryToQuotation'),
  '已审核询价单提供预填 / 转报价单入口（规范 Id）');

/* ============ 4. 旧族分离：旧读侧 / 默认值端点 / 整数天口径保持只读可用 ============ */
const billConfigSrc = jsSource('bill-config.js');
ok(billConfigSrc.includes("billType: 'inquiry'"), '旧族 BILL_CONFIG.inquiry（旧库单体表 Oid 读侧）仍保留');
const BILL_CONFIG = new Function('CURRENCY_OPTS', billConfigSrc + '\nreturn BILL_CONFIG;')(CURRENCY_OPTS);
ok(!!BILL_CONFIG.inquiry, '旧族询价单配置可在浏览器中加载（旧数据排查仍可读）');
ok(billV2Src.includes('api(`/api/v2/bills/${BILL_CODE}/defaults`)'),
  'ERP-469：仍复用既有（参数授权）默认值端点，未新增取数通道');
ok(billV2Src.includes('loadAuthorizedBillDefaults()') && billV2Src.includes('startNewBillForm()'),
  'ERP-469：新建入口显式加载既有默认值（旧族读侧不被削弱）');
ok(billV2Src.includes('billIntegralDays') && billV2Src.includes('billGuardValidDaysInput'),
  'ERP-469：有效期整数天口径保留');
ok(!/['"]?inquiry-new['"]?\s*:\s*['"]?inquiry['"]?/.test(billV2Src.slice(mapStart, mapEnd)),
  '旧 Oid 与规范 Id 之间不存在任何映射（绝不把 Oid 当作规范 Id）');

/* 动态验证：既有默认值 / 整数天纯逻辑保持不变（require 真实 bill-v2.js） */
const billV2 = require(jsPath('bill-v2.js'));
eq(billV2.billIntegralDays('30'), { ok: true, value: 30, message: '' }, '整数天接受');
eq(billV2.billIntegralDays(''), { ok: true, value: '', message: '' }, '留空交由服务端裁决（default failure 口径保留）');
ok(billV2.billIntegralDays('7.5').ok === false, '小数天被拒绝（整数天口径保留）');
const missingDefaults = billV2.billDefaultsIntoFields({}, BILL_CONFIG.inquiry);
eq(missingDefaults.values.Currency, '', '默认值缺失时旧族币种保持为空（绝不回退美元）');
eq(missingDefaults.values.ExchangeRate, '', '默认值缺失时旧族汇率保持为空（绝不臆造汇率 1）');

/* ============ 5. 规范 Id 接线：crud.js 保存 / 回填 + sales-pi.js 预填 / 转报价 ============ */
const crudSrc = jsSource('crud.js');
ok(crudSrc.includes('await api(`${mod.api}/${id}`, \'PUT\', body)'),
  '修改按规范 Id（mod.api/{id}）提交，不使用任何 Oid');
ok(crudSrc.includes('await api(mod.api, \'POST\', body)'), '新增提交到规范集合端点');
ok(crudSrc.includes("body[mod.detailKey || 'details'] = detailCollect()"),
  '主子表提交把明细随表头一起提交（规范 Id 明细）');
ok(crudSrc.includes('const row = await api(`${mod.api}/${id}`)') && crudSrc.includes('DETAIL_ROWS = (row[mod.detailKey'),
  '详情回填按规范 Id 读取并回填明细');
ok(!/\/api\/v2\/bills/.test(crudSrc), 'crud.js 规范路径绝不调用旧存储过程族');

const salesPiSrc = jsSource('sales-pi.js');
ok(salesPiSrc.includes('/api/inquiries/${id}/quotation-prefill'), '预填报价单走规范询价单 Id');
ok(salesPiSrc.includes('/api/inquiries/${id}/to-quotation'), '转报价单走规范询价单 Id');

console.log('\n✅ ERP-474 询价单规范入口路由 / 主子表接线契约全部通过（' + passed + ' 项断言）。');
console.log('ℹ️ 真实浏览器 + 真实测试库 + 真实授权身份的端到端验收由 ERP.IntegrationTests 执行'
  + '（InquiryCanonicalEntrySqlServerTests / Collection=UiTests）；本文件为离线契约验证，不声称业务写入成功。');
process.exit(0);
