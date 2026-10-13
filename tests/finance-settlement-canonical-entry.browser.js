'use strict';
/* ERP-481 装柜结算单 / 散货结算单规范入口路由契约（离线可执行，无真实网络 / 无成功业务写入模拟）：

   本文件只验证**可离线判定**的接线契约，绝不伪造任何成功业务写入：
     1) 菜单路由：bill-v2.js 的 BILL_CODE_MAP **生效键**不再包含 container-settlement / bulk-settlement
        （旧存储过程族写路径已被 ERP-404 门禁整体停用）；说明性注释里的原字面量不参与路由判定，
        本文件通过**真实执行 bill-v2.js** 取得的 BILL_CODE_MAP 对象来证明这一点；
     2) 路由优先级：app.js 先判 BILL_CODE_MAP 再判 MODULES，因此删除映射后菜单必然落到规范模块；
     3) 规范模块：modules-finance.js 的 MODULES['container-settlement'] / MODULES['bulk-settlement'] 走
        api/finance/container-settlements、api/finance/bulk-settlements（规范 Id 口径），
        装柜结算单来源装柜清单字段是声明式可空引用（nullable: true）并声明既有来源选择器；
        散货结算单没有来源单据字段（手工 / 未关联语义）；两者都没有币种 / 汇率字段；
     4) 表单接线：crud.js 新增 / 修改 / 详情回填按 mod.api 走规范 Id，声明式可空引用留空提交 null；
     5) 既有来源选择器：container-settlement-loading-source.js 复用 ERP-392 实现（可显式断开）；
     6) 旧族分离：旧 BILL_CONFIG 旧库单体表 Oid 读侧保持只读可用，规范 Id 与旧 Oid 之间不存在任何映射。
   真实浏览器 + 真实隔离库 + 真实授权身份（含真实 API 写入）的端到端验收由 IntegrationTests 执行
   （src/ERP.IntegrationTests/FinanceSettlementCanonicalEntrySqlServerTests.cs，专用 localdb + 全新 GUID
   NEWERP_AUTOTEST 库）。本文件不声称替代该端到端验收：本环境无浏览器验收执行器（任务
   browser_acceptance.required = false），因此**不执行**真实浏览器工作流，也不把离线断言当作业务写入成功证据。
   说明：ERP-480 的离线契约（tests/finance-apply-canonical-entry.browser.js）含一条 ERP-480 作用域的守卫
   （断言 container-settlement 旧族键仍在生效映射中）；ERP-481 正是要停用该旧族路由，因此该守卫必然被取代，
   本文件以「生效映射不含 container-settlement / bulk-settlement」为准。

   运行：node tests/finance-settlement-canonical-entry.browser.js
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

ok(!Object.prototype.hasOwnProperty.call(billCodeMap, 'container-settlement'),
  "生效中的 BILL_CODE_MAP 不再包含 'container-settlement'（否则菜单打开永远无法保存的旧单据页）");
ok(!Object.prototype.hasOwnProperty.call(billCodeMap, 'bulk-settlement'),
  "生效中的 BILL_CODE_MAP 不再包含 'bulk-settlement'");
eq(billCodeMap['container-settlement'], undefined, 'container-settlement 不再映射到任何旧存储过程族单据类型');
eq(billCodeMap['bulk-settlement'], undefined, 'bulk-settlement 不再映射到任何旧存储过程族单据类型');
ok(Object.prototype.hasOwnProperty.call(billCodeMap, 'complaint'),
  '其它旧族单据映射不受影响（本次只改装柜结算单 / 散货结算单路由）');
ok(Object.prototype.hasOwnProperty.call(billCodeMap, 'receiving-plan'),
  '其它旧族单据映射不受影响（本次只改装柜结算单 / 散货结算单路由）');

const mapStart = billV2Src.indexOf('const BILL_CODE_MAP = {');
const mapEnd = billV2Src.indexOf('};', mapStart);
ok(mapStart >= 0 && mapEnd > mapStart, 'BILL_CODE_MAP 定义存在');
const mapText = billV2Src.slice(mapStart, mapEnd);
ok(/\/\*[\s\S]*ERP-481[\s\S]*\*\//.test(mapText),
  '两条旧族映射以 ERP-481 说明「原地停用」，不再参与任何路由判定');
ok(billV2Src.includes('ERP-481'), '路由改动带 ERP-481 说明（保留旧族只读与「不映射 Oid」口径）');
ok(/\/api\/finance\/container-settlements/.test(mapText) && /\/api\/finance\/bulk-settlements/.test(mapText),
  '说明中记录规范端点（api/finance/container-settlements、api/finance/bulk-settlements）');

/* ============ 2. 路由优先级：先 BILL_CODE_MAP，再 MODULES ============ */
const appSrc = jsSource('app.js');
const billMapCheck = appSrc.indexOf('else if (BILL_CODE_MAP[code]) renderBillV2');
const modulesLookup = appSrc.indexOf('const mod = MODULES[code]');
ok(billMapCheck >= 0, 'app.js 仍保留 BILL_CODE_MAP 路由分支（其它旧族单据仍可打开）');
ok(modulesLookup > billMapCheck, 'app.js 在 MODULES 之前判定 BILL_CODE_MAP（移除映射即落到规范模块）');
ok(appSrc.includes('CURRENT_MODULE_CODE = code; renderModule(mod)'),
  'app.js 落到规范模块时设置 CURRENT_MODULE_CODE（既有来源选择器据此路由结算单接口）');

/* ============ 3. 规范模块注册（MODULES['container-settlement'] / MODULES['bulk-settlement']） ============ */
const CURRENCY_OPTS = [{ value: 'CNY', label: 'CNY' }, { value: 'USD', label: 'USD' }];
const PAYMENT_OPTS = [{ value: 'BankTransfer', label: '银行转账' }];
const MODULES = {};
new Function('MODULES', 'CURRENCY_OPTS', 'PAYMENT_OPTS', jsSource('modules-finance.js'))(
  MODULES, CURRENCY_OPTS, PAYMENT_OPTS);

const container = MODULES['container-settlement'];
ok(!!container, "modules-finance.js 注册了 'container-settlement' 规范模块");
eq(container.api, '/api/finance/container-settlements', '装柜结算单走 api/finance/container-settlements（规范 Id 口径）');
eq(container.canSubmit, true, '装柜结算单规范模块支持提交 / 审核（状态流转入口可用）');
const containerLoadingField = (container.fields || []).find(f => f.key === 'loadingListId');
ok(!!containerLoadingField, '装柜结算单声明来源装柜清单字段 loadingListId');
eq(containerLoadingField.nullable, true, '来源装柜清单是声明式可空引用（留空提交 null，绝不回落 0）');
eq(containerLoadingField.selector, 'container-settlement-loading-source', '来源装柜清单使用既有 ERP-392 显式选择器');
ok((container.fields || []).some(f => f.key === 'remark'), '装柜结算单声明备注字段（保存 / 重开 / 修改原样往返）');
ok(!(container.fields || []).some(f => /currency|exchangeRate/i.test(f.key)),
  '装柜结算单没有币种 / 汇率字段（绝不跨币种聚合）');

const bulk = MODULES['bulk-settlement'];
ok(!!bulk, "modules-finance.js 注册了 'bulk-settlement' 规范模块");
eq(bulk.api, '/api/finance/bulk-settlements', '散货结算单走 api/finance/bulk-settlements（规范 Id 口径）');
eq(bulk.canSubmit, true, '散货结算单规范模块支持提交 / 审核');
ok((bulk.fields || []).some(f => f.key === 'remark'), '散货结算单声明备注字段（保存 / 重开 / 修改原样往返）');
ok(!(bulk.fields || []).some(f => f.selector), '散货结算单没有来源选择器（手工 / 未关联语义，绝不推断来源）');
ok(!(bulk.fields || []).some(f => /currency|exchangeRate/i.test(f.key)),
  '散货结算单没有币种 / 汇率字段（绝不跨币种聚合）');

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
  '声明式可空引用（含装柜结算单来源装柜清单）留空提交 null，绝不回落为 0');
ok(crudSrc.includes('const mod = CURRENT_MODULE;'), '保存 / 回填都基于当前规范模块（mod.api）');
ok(!/\/api\/v2\/bills/.test(crudSrc), 'crud.js 规范路径绝不调用旧存储过程族');

/* ============ 5. 既有来源选择器（ERP-392 实现，本任务不新增实现） ============ */
const pickerSrc = jsSource('container-settlement-loading-source.js');
ok(/\bcslsInstallFormHook\b/.test(pickerSrc), '既有选择器提供表单接入钩子（按字段 selector 接线）');
ok(pickerSrc.includes("field.selector !== 'container-settlement-loading-source'"),
  '表单钩子只接管声明了本选择器的字段');
ok(pickerSrc.includes('cslsUnlinkFill') && pickerSrc.includes('cslsFormFill') && pickerSrc.includes('cslsUnlinkSource'),
  '选择器支持显式断开来源（留空 → 未关联语义）');
ok(pickerSrc.includes('cslsShouldAcceptResponse'), '陈旧异步响应一律丢弃（客户变更不回填旧结果）');
ok(pickerSrc.includes('cslsAvailableCandidates'), '选择器只接受服务端标记可用的候选（已取消 / 越范围不可选）');
ok(!/\/api\/v2\/bills/.test(pickerSrc), '选择器绝不调用旧存储过程族');
ok(!/FromSql|ExecuteSql|SqlCommand|select\s+\*/i.test(pickerSrc), '选择器不执行任意 SQL');
const indexSrc = fs.readFileSync(path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'index.html'), 'utf8');
ok(indexSrc.includes('/js/container-settlement-loading-source.js'), 'index.html 加载既有来源选择器脚本');
ok(indexSrc.includes('/js/modules-finance.js'), 'index.html 加载规范财务模块脚本');

/* ============ 6. 旧族分离与交付说明 ============ */
const billConfigSrc = jsSource('bill-config3.js');
ok(/'container-settlement': \{/.test(billConfigSrc), '旧族装柜结算单配置（旧库单体表 Oid 读侧）仍保留只读可用');
ok(/'bulk-settlement': \{/.test(billConfigSrc), '旧族散货结算单配置仍保留只读可用');
ok(!Object.prototype.hasOwnProperty.call(billCodeMap, 'container-settlement')
  && !Object.prototype.hasOwnProperty.call(billCodeMap, 'bulk-settlement'),
  '旧 Oid 与规范 Id 之间不存在任何生效映射');

const docPath = path.join(ROOT, 'docs', 'finance-settlement-canonical-entry.md');
ok(fs.existsSync(docPath), '交付说明 docs/finance-settlement-canonical-entry.md 存在');
const docSrc = fs.readFileSync(docPath, 'utf8');
ok(docSrc.includes('ERP-481') && docSrc.includes('api/finance/container-settlements')
  && docSrc.includes('api/finance/bulk-settlements'),
  '交付说明记录规范端点与 ERP-481 口径');
ok(docSrc.includes('真实浏览器') && /未执行|不声称/.test(docSrc),
  '交付说明如实记录浏览器验收未执行（不以离线断言替代业务写入证据）');

console.log('\n✅ ERP-481 装柜结算单 / 散货结算单规范入口路由 / 规范模块 / 来源选择器接线契约全部通过（'
  + passed + ' 项断言）。');
console.log('ℹ️ 真实浏览器 + 真实隔离库 + 真实授权身份（真实 API 写入）的端到端验收由 ERP.IntegrationTests 执行'
  + '（FinanceSettlementCanonicalEntrySqlServerTests）；本文件为离线契约验证，不声称业务写入成功。');
console.log('ℹ️ 本环境无浏览器验收执行器（browser_acceptance.required = false）：真实浏览器 UI 工作流未执行，'
  + '不声称浏览器端到端或整阶段验收通过。');
process.exit(0);
