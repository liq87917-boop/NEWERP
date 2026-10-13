'use strict';
/* ERP-484 订柜信息规范入口路由契约（离线可执行，无真实网络 / 无成功业务写入模拟）：

   本文件只验证**可离线判定**的接线契约，绝不伪造任何成功业务写入：
     1) 菜单路由：真实执行 bill-v2.js 后，BILL_CODE_MAP 中**不存在** booking 键（ERP-040 已把装柜三单改由
        EF 页面承载），因此 app.js 的 `else if (BILL_CODE_MAP[code])` 判定为假，菜单落到
        MODULES['booking'] 规范模块；旧存储过程族写路径已被 ERP-404 门禁整体停用（fail closed）；
     2) 路由优先级：app.js 先判 BILL_CODE_MAP 再判 MODULES，因此键不存在即等于落到规范模块；
     3) 规范模块：modules-doc2.js 的 MODULES['booking'] 走 api/container/bookings（规范 Id 口径），
        客户 / 供应商是既有主数据引用（可选项声明 nullable: true），表单**不提交 bookingNo**
        （服务端权威生成），可选日期与 ERP-040 跟踪字段（出运方式 / B/L / S-O / 中转港 / ETD·ETA·ATD·ATA /
        拖车 / 查验三态 / 放行日期）参与往返；
     4) 表单接线：crud.js 新增 / 修改 / 详情回填按 mod.api 走规范 Id，声明式可空引用留空提交 null，
        日期留空提交 null；
     5) 旧族分离：旧 BILL_CONFIG['booking']（bill-config3.js 的旧库单体表 Oid 读侧）保持只读可用，
        规范 Id 与旧 Oid 之间不存在任何映射，也绝不因此启用任何旧写适配器。
    真实浏览器 + 真实隔离库 + 真实授权身份（含真实 API 写入）的端到端验收由 IntegrationTests 执行
    （src/ERP.IntegrationTests/BookingCanonicalEntrySqlServerTests.cs，专用 localdb + 全新 GUID
    NEWERP_AUTOTEST 库）。本环境无浏览器验收执行器（任务 browser_acceptance.required = false），因此
    **不执行**真实浏览器工作流，也不把离线断言当作业务写入成功证据。

    运行：node tests/booking-canonical-entry.browser.js
    失败时抛异常并以非零码退出。 */

const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..');
const JS_DIR = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js');
const jsSource = name => fs.readFileSync(path.join(JS_DIR, name), 'utf8');

let passed = 0;
function ok(cond, msg) { assert.ok(cond, msg); passed += 1; }

/* ============ 1. 菜单路由：真实执行 bill-v2.js，确认 booking 不再映射旧存储过程族 ============ */
const billV2Src = jsSource('bill-v2.js');
const billCodeMap = new Function('BILL_CONFIG', billV2Src + '\n;return BILL_CODE_MAP;')({});

ok(!Object.prototype.hasOwnProperty.call(billCodeMap, 'booking'),
  'BILL_CODE_MAP 不再含 booking 键：app.js 判定为假 → 菜单落到规范模块，不再进入旧存储过程族');
ok(!billCodeMap['booking'],
  'booking 取值恒为假（即便被外部探测也只是未定义 / 假值）');
ok(Object.prototype.hasOwnProperty.call(billCodeMap, 'receiving-plan'),
  '其它旧族停用记录不受影响（本次不触碰收货计划路由）');

const mapStart = billV2Src.indexOf('const BILL_CODE_MAP = {');
const mapEnd = billV2Src.indexOf('};', mapStart);
ok(mapStart >= 0 && mapEnd > mapStart, 'BILL_CODE_MAP 定义存在');
const mapText = billV2Src.slice(mapStart, mapEnd);
ok(!/^\s*'?booking'?\s*:/m.test(mapText), 'BILL_CODE_MAP 不含任何 booking 映射键');
ok(mapText.includes("'booking' / 'pre-loading' / 'loading-list' 模块"),
  '说明中记录 ERP-040：装柜三单由规范模块承载');

/* ============ 2. 路由优先级：先 BILL_CODE_MAP，再 MODULES ============ */
const appSrc = jsSource('app.js');
const billMapCheck = appSrc.indexOf('else if (BILL_CODE_MAP[code]) renderBillV2');
const modulesLookup = appSrc.indexOf('const mod = MODULES[code]');
ok(billMapCheck >= 0 && modulesLookup > billMapCheck,
  'app.js 先判 BILL_CODE_MAP 再判 MODULES：键不存在时菜单必然落到规范模块');

/* ============ 3. 规范模块：api/container/bookings ============ */
const modulesSrc = jsSource('modules-doc2.js');
const blockStart = modulesSrc.indexOf('\n  booking: {');
ok(blockStart >= 0, "modules-doc2.js 注册了 'booking' 规范模块");
const blockEnd = modulesSrc.indexOf('\n  },', blockStart);
ok(blockEnd > blockStart, '订柜信息规范模块闭合');
const block = modulesSrc.slice(blockStart, blockEnd);
ok(block.includes("api: '/api/container/bookings'"),
  '订柜信息走 api/container/bookings（规范 Id 口径）');
ok(block.includes('canSubmit: true'), '支持提交 / 审核（状态流转入口可用）');
ok(block.includes("key: 'customerId'") && block.includes("type: 'ref', ref: 'customer'"),
  '客户是既有主数据引用（必填）');
ok(block.includes("key: 'supplierId'") && block.includes('nullable: true'),
  '可选供应商是声明式可空引用（留空提交 null = 未指定，绝不回落为 0）');
ok(block.includes("ref: 'customsBroker', nullable: true"),
  '可选报关行是声明式可空引用（留空提交 null = 未指定）');
const fieldsStart = block.indexOf('fields: [');
ok(fieldsStart > 0, '订柜信息规范模块声明 fields');
ok(!block.slice(fieldsStart).includes("key: 'bookingNo'"),
  '表单不提交 bookingNo：省略 / 空串一律由服务端权威生成');
ok(block.includes("key: 'shipmentMode'") && block.includes("key: 'inspectionRequired'"),
  'ERP-040 跟踪字段（出运方式 / 查验三态）参与往返');
ok(block.includes("valueType: 'bool?'"), '查验要求保持三态（未知绝不回落为 false）');
ok(block.includes("key: 'etd'") && block.includes("key: 'customsReleaseDate'"),
  '可选日期（ETD / 放行日期等）参与往返');
ok(block.includes("key: 'remark'"), '备注字段参与保存 / 重开 / 修改往返');
ok(!/\/api\/v2\/bills/.test(modulesSrc), '规范模块接线绝不调用旧存储过程族');
ok(!/FromSql|ExecuteSql|SqlCommand|select\s+\*/i.test(modulesSrc), '不执行任意 SQL');

/* ============ 4. 规范 Id 接线：crud.js 新增 / 修改 / 回填 / 可空引用 ============ */
const crudSrc = jsSource('crud.js');
ok(crudSrc.includes("await api(`${mod.api}/${id}`, 'PUT', body)"),
  '修改按规范 Id（mod.api/{id}）提交，不使用任何 Oid');
ok(crudSrc.includes("await api(mod.api, 'POST', body)"), '新增提交到规范集合端点');
ok(crudSrc.includes('const row = await api(`${mod.api}/${id}`)'), '详情回填按规范 Id 读取');
ok(crudSrc.includes('f.nullable === true ? null : 0'),
  '声明式可空引用（含订柜可选引用）留空提交 null，绝不回落为 0');
ok(crudSrc.includes("if (f.type === 'ref') v = v === '' ? null : Number(v);"),
  '主数据引用留空提交 null（未指定）');
ok(crudSrc.includes("if (f.type === 'date') v = v ? v + 'T00:00:00' : null;"),
  '可选日期留空提交 null');
ok(!/\/api\/v2\/bills/.test(crudSrc), 'crud.js 规范路径绝不调用旧存储过程族');

/* ============ 5. 页面接线、旧族分离与交付说明 ============ */
const indexSrc = fs.readFileSync(path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'index.html'), 'utf8');
ok(indexSrc.includes('/js/modules-doc2.js'), 'index.html 加载规范单据模块脚本');

const billConfigSrc = jsSource('bill-config3.js');
ok(/booking: \{/.test(billConfigSrc), '旧族订柜信息配置（旧库单体表 Oid 读侧）仍保留只读可用');

const docPath = path.join(ROOT, 'docs', 'booking-canonical-entry.md');
ok(fs.existsSync(docPath), '交付说明 docs/booking-canonical-entry.md 存在');
const docSrc = fs.readFileSync(docPath, 'utf8');
ok(docSrc.includes('ERP-484') && docSrc.includes('api/container/bookings'),
  '交付说明记录规范端点与 ERP-484 口径');
ok(docSrc.includes('真实浏览器') && /未执行|不声称/.test(docSrc),
  '交付说明如实记录浏览器验收未执行（不以离线断言替代业务写入证据）');

console.log('\n✅ ERP-484 订柜信息规范入口路由 / 规范模块 / 可空引用接线契约全部通过（'
  + passed + ' 项断言）。');
console.log('ℹ️ 真实浏览器 + 真实隔离库 + 真实授权身份（真实 API 写入）的端到端验收由 ERP.IntegrationTests 执行'
  + '（BookingCanonicalEntrySqlServerTests）；本文件为离线契约验证，不声称业务写入成功。');
console.log('ℹ️ 本环境无浏览器验收执行器（browser_acceptance.required = false）：真实浏览器 UI 工作流未执行，'
  + '不声称浏览器端到端或整阶段验收通过。');
process.exit(0);

