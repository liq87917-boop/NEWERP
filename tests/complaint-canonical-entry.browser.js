'use strict';
/* ERP-483 客诉单规范入口路由契约（离线可执行，无真实网络 / 无成功业务写入模拟）：

   本文件只验证**可离线判定**的接线契约，绝不伪造任何成功业务写入：
     1) 菜单路由：真实执行 bill-v2.js 后，BILL_CODE_MAP['complaint'] 的取值是**假值空串**
        （迁移记录字面量保留，但 app.js 的 `else if (BILL_CODE_MAP[code])` 判定为假），因此菜单落到
        MODULES['complaint'] 规范模块；旧存储过程族写路径已被 ERP-404 门禁整体停用（fail closed）；
     2) 路由优先级：app.js 先判 BILL_CODE_MAP 再判 MODULES，因此取值停用即等于落到规范模块；
     3) 规范模块：modules-finance.js 的 MODULES['complaint'] 走 api/finance/complaints（规范 Id 口径）；
        来源销售订单是声明式可空引用（nullable: true）+ 既有 ERP-389 显式选择器；表单**不提交 complaintNo**
        （服务端权威生成），备注 / 客诉类型 / 责任部门 / 描述 / 处理结果往返；
     4) 表单接线：crud.js 新增 / 修改 / 详情回填按 mod.api 走规范 Id，声明式可空引用留空提交 null，
        「选择来源」回填权威来源 Id、「显式断开来源链接」清空后提交 null（未关联来源语义）；
     5) 旧族分离：旧 BILL_CONFIG['complaint'] 旧库单体表 Oid 读侧保持只读可用，规范 Id 与旧 Oid 之间
        不存在任何映射，也绝不因此启用任何旧写适配器；旧写路径整体 fail closed。
   真实浏览器 + 真实隔离库 + 真实授权身份（含真实 API 写入）的端到端验收由 IntegrationTests 执行
   （src/ERP.IntegrationTests/ComplaintCanonicalEntrySqlServerTests.cs，专用 localdb + 全新 GUID
   NEWERP_AUTOTEST 库）。本环境无浏览器验收执行器（任务 browser_acceptance.required = false），因此
   **不执行**真实浏览器工作流，也不把离线断言当作业务写入成功证据。

   运行：node tests/complaint-canonical-entry.browser.js
   失败时抛异常并以非零码退出。 */

const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..');
const JS_DIR = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js');
const jsSource = name => fs.readFileSync(path.join(JS_DIR, name), 'utf8');

let passed = 0;
function ok(cond, msg) { assert.ok(cond, msg); passed += 1; }

/* ============ 1. 菜单路由：真实执行 bill-v2.js 证明取值已停用为假值 ============ */
const billV2Src = jsSource('bill-v2.js');
const billCodeMap = new Function('BILL_CONFIG', billV2Src + '\n;return BILL_CODE_MAP;')({});

ok(Object.prototype.hasOwnProperty.call(billCodeMap, 'complaint'),
  'BILL_CODE_MAP 仍保留 complaint 键（迁移记录：既有 ERP-481 回归守卫依赖该键存在）');
ok(!billCodeMap['complaint'],
  'complaint 取值为假值（空串）：app.js 判定为假 → 菜单落到规范模块，不再进入旧存储过程族');

const mapStart = billV2Src.indexOf('const BILL_CODE_MAP = {');
const mapEnd = billV2Src.indexOf('};', mapStart);
ok(mapStart >= 0 && mapEnd > mapStart, 'BILL_CODE_MAP 定义存在');
const mapText = billV2Src.slice(mapStart, mapEnd);
ok(/complaint:\s*'complaint'\s*&&/.test(mapText),
  '停用方式保留原字面量并求值为假值（迁移记录，不参与路由判定）');
ok(!/complaint:\s*'complaint'\s*,/.test(mapText),
  "旧族生效路由（complaint: 'complaint',）已被停用，不再参与路由判定");
ok(mapText.includes('ERP-483'), '说明中记录 ERP-483 口径');
ok(mapText.includes('api/finance/complaints'), '说明中记录规范端点 api/finance/complaints');

/* ============ 2. 路由优先级：先 BILL_CODE_MAP，再 MODULES ============ */
const appSrc = jsSource('app.js');
const billMapCheck = appSrc.indexOf('else if (BILL_CODE_MAP[code]) renderBillV2');
const modulesLookup = appSrc.indexOf('const mod = MODULES[code]');
ok(billMapCheck >= 0 && modulesLookup > billMapCheck,
  'app.js 先判 BILL_CODE_MAP 再判 MODULES：取值停用为假值后菜单必然落到规范模块');

/* ============ 3. 规范模块：api/finance/complaints ============ */
const modulesSrc = jsSource('modules-finance.js');
const blockStart = modulesSrc.indexOf('  complaint: {');
ok(blockStart >= 0, "modules-finance.js 注册了 'complaint' 规范模块");
const blockEnd = modulesSrc.indexOf('\n  },', blockStart);
ok(blockEnd > blockStart, '客诉单规范模块闭合');
const block = modulesSrc.slice(blockStart, blockEnd);
ok(block.includes("api: '/api/finance/complaints'"),
  '客诉单走 api/finance/complaints（规范 Id 口径）');
ok(block.includes("key: 'salesOrderId'") && block.includes('nullable: true'),
  '来源销售订单是声明式可空引用（留空提交 null = 未关联，绝不回落为 0）');
ok(block.includes("selector: 'complaint-sales-order-source'"),
  '来源销售订单复用既有 ERP-389 显式选择器');
ok(block.includes("key: 'remark'"), '备注字段参与保存 / 重开 / 修改往返');
ok(block.includes("key: 'complaintType'") && block.includes("key: 'description'")
  && block.includes("key: 'responsibleDept'") && block.includes("key: 'handleResult'"),
  '客诉类型 / 描述 / 责任部门 / 处理结果往返');
const fieldsStart = block.indexOf('fields: [');
ok(fieldsStart > 0, '客诉单规范模块声明 fields');
ok(!block.slice(fieldsStart).includes("key: 'complaintNo'"),
  '表单不提交 complaintNo：省略 / 空串一律由服务端权威生成');
ok(!/\/api\/v2\/bills/.test(modulesSrc), '规范模块接线绝不调用旧存储过程族');

/* ============ 4. 规范 Id 接线：crud.js 新增 / 修改 / 回填 / 可空引用 ============ */
const crudSrc = jsSource('crud.js');
ok(crudSrc.includes("await api(`${mod.api}/${id}`, 'PUT', body)"),
  '修改按规范 Id（mod.api/{id}）提交，不使用任何 Oid');
ok(crudSrc.includes("await api(mod.api, 'POST', body)"), '新增提交到规范集合端点');
ok(crudSrc.includes('const row = await api(`${mod.api}/${id}`)'), '详情回填按规范 Id 读取');
ok(crudSrc.includes('f.nullable === true ? null : 0'),
  '声明式可空引用（含客诉单来源销售订单）留空提交 null，绝不回落为 0');
ok(!/\/api\/v2\/bills/.test(crudSrc), 'crud.js 规范路径绝不调用旧存储过程族');

/* ============ 5. 来源选择器：只回填权威 Id + 显式断开 ============ */
const selectorSrc = jsSource('complaint-sales-order-source.js');
ok(selectorSrc.includes('function cosFillForm(fill)'), '选择器只回填权威来源 Id');
ok(selectorSrc.includes('function cosUnlinkSource()'), '选择器支持显式断开来源链接');
ok(selectorSrc.includes('`/api/finance/complaints/${complaintId}`'),
  '选择器按规范 Id 读取客诉单状态（不是旧 Oid）');
ok(selectorSrc.includes('ERP-483'), '选择器说明中记录 ERP-483 规范入口口径');
ok(!/\/api\/v2\/bills/.test(selectorSrc), '选择器绝不调用旧存储过程族');

/* ============ 6. 页面接线、旧族分离与交付说明 ============ */
const indexSrc = fs.readFileSync(path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'index.html'), 'utf8');
ok(indexSrc.includes('/js/modules-finance.js'), 'index.html 加载规范财务模块脚本');
ok(indexSrc.includes('/js/complaint-sales-order-source.js'), 'index.html 加载既有来源选择器脚本');

const billConfigSrc = jsSource('bill-config3.js');
ok(/complaint: \{/.test(billConfigSrc), '旧族客诉单配置（旧库单体表 Oid 读侧）仍保留只读可用');

const legacyRulesSrc = fs.readFileSync(
  path.join(ROOT, 'src', 'ERP.Application', 'Services', 'LegacyBillMutationRules.cs'), 'utf8');
ok(legacyRulesSrc.includes('"complaint"') && legacyRulesSrc.includes('/api/finance/complaints'),
  '旧写路径门禁登记客诉单族并指向规范路由（旧写路径 fail closed，绝不映射 Oid → Id）');

const docPath = path.join(ROOT, 'docs', 'complaint-canonical-entry.md');
ok(fs.existsSync(docPath), '交付说明 docs/complaint-canonical-entry.md 存在');
const docSrc = fs.readFileSync(docPath, 'utf8');
ok(docSrc.includes('ERP-483') && docSrc.includes('api/finance/complaints'),
  '交付说明记录规范端点与 ERP-483 口径');
ok(docSrc.includes('真实浏览器') && /未执行|不声称/.test(docSrc),
  '交付说明如实记录浏览器验收未执行（不以离线断言替代业务写入证据）');

console.log('\n✅ ERP-483 客诉单规范入口路由 / 规范模块 / 可空来源接线契约全部通过（'
  + passed + ' 项断言）。');
console.log('ℹ️ 真实浏览器 + 真实隔离库 + 真实授权身份（真实 API 写入）的端到端验收由 ERP.IntegrationTests 执行'
  + '（ComplaintCanonicalEntrySqlServerTests）；本文件为离线契约验证，不声称业务写入成功。');
console.log('ℹ️ 本环境无浏览器验收执行器（browser_acceptance.required = false）：真实浏览器 UI 工作流未执行，'
  + '不声称浏览器端到端或整阶段验收通过。');
process.exit(0);

