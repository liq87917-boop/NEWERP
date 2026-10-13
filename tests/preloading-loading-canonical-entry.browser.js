'use strict';
/* ERP-485 预装柜单 / 装柜清单规范入口主子表路由契约（离线可执行，无真实网络 / 无成功业务写入模拟）：

   本文件只验证**可离线判定**的接线契约，绝不伪造任何成功业务写入：
     1) 菜单路由：真实执行 bill-v2.js 后，BILL_CODE_MAP 中**不存在** 'pre-loading' / 'loading-list' 键
        （ERP-040 已把装柜三单改由 EF 页面承载），因此 app.js 的 `else if (BILL_CODE_MAP[code])` 判定为假，
        菜单落到 MODULES['pre-loading'] / MODULES['loading-list'] 规范模块；
     2) 路由优先级：app.js 先判 BILL_CODE_MAP 再判 MODULES，因此键不存在即等于落到规范模块；
     3) 规范模块：modules-doc2.js 的两个模块走 api/container/pre-loadings 与 api/container/loading-lists
        （规范 Id 口径），并声明 detailKey/detailFields 以支持主表 + 明细一起录入；表单**不提交**
        preLoadingNo / loadingListNo（服务端权威生成），可选订柜 / 预装柜引用留空提交 null；
     4) 表单接线：crud.js 新增 / 修改 / 详情回填按 mod.api 走规范 Id，主表 + 明细一次提交，
        明细行随回填携带的持久化证据链接原样回传；
     5) 旧族分离：旧 BILL_CONFIG 读侧保持只读可用，规范 Id 与旧 Oid 之间不存在任何映射，
        也绝不因此启用任何旧写适配器（旧族写路径由 ERP-404 门禁整体停用）。
    真实浏览器 + 真实隔离库 + 真实授权身份（含真实 API 写入）的端到端验收由 IntegrationTests 执行
    （src/ERP.IntegrationTests/PreLoadingLoadingCanonicalEntrySqlServerTests.cs，专用 localdb + 全新 GUID
    NEWERP_AUTOTEST 库）。本环境无浏览器验收执行器（任务 browser_acceptance.required = false），因此
    **不执行**真实浏览器工作流，也不把离线断言当作业务写入成功证据。

    运行：node tests/preloading-loading-canonical-entry.browser.js
    失败时抛异常并以非零码退出。 */

const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..');
const JS_DIR = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js');
const jsSource = name => fs.readFileSync(path.join(JS_DIR, name), 'utf8');

let passed = 0;
function ok(cond, msg) { assert.ok(cond, msg); passed += 1; }

function moduleBlock(source, moduleKey) {
  const start = source.indexOf(moduleKey);
  ok(start >= 0, `modules-doc2.js 注册了 ${moduleKey} 规范模块`);
  const end = source.indexOf('\n  },', start);
  ok(end > start, `${moduleKey} 规范模块闭合`);
  return source.slice(start, end);
}

/* ============ 1. 菜单路由：两个模块都不再映射旧存储过程族 ============ */
const billV2Src = jsSource('bill-v2.js');
const billCodeMap = new Function('BILL_CONFIG', billV2Src + '\n;return BILL_CODE_MAP;')({});

ok(!Object.prototype.hasOwnProperty.call(billCodeMap, 'pre-loading'),
  "BILL_CODE_MAP 不再含 'pre-loading' 键：菜单落到规范模块");
ok(!Object.prototype.hasOwnProperty.call(billCodeMap, 'loading-list'),
  "BILL_CODE_MAP 不再含 'loading-list' 键：菜单落到规范模块");
ok(!billCodeMap['pre-loading'] && !billCodeMap['loading-list'],
  '两键取值恒为假（即便被外部探测也只是未定义 / 假值）');

/* ============ 2. 路由优先级：先 BILL_CODE_MAP，再 MODULES ============ */
const appSrc = jsSource('app.js');
const billMapCheck = appSrc.indexOf('else if (BILL_CODE_MAP[code]) renderBillV2');
const modulesLookup = appSrc.indexOf('const mod = MODULES[code]');
ok(billMapCheck >= 0 && modulesLookup > billMapCheck,
  'app.js 先判 BILL_CODE_MAP 再判 MODULES：键不存在时菜单必然落到规范模块');

/* ============ 3. 规范模块：主子表 + 不提交单号 + 可选引用 ============ */
const modulesSrc = jsSource('modules-doc2.js');

const preLoading = moduleBlock(modulesSrc, "'pre-loading': {");
ok(preLoading.includes("api: '/api/container/pre-loadings'"),
  '预装柜单走 api/container/pre-loadings（规范 Id 口径）');
ok(preLoading.includes('canSubmit: true'), '预装柜单支持提交 / 审核（状态流转入口可用）');
ok(preLoading.includes("detailKey: 'details'") && preLoading.includes('detailFields: PRE_LOADING_DETAIL_FIELDS'),
  '预装柜单声明主子表（主表 + 明细一起保存 / 回填）');
ok(preLoading.includes("type: 'ref', ref: 'booking'"),
  '可选订柜引用是既有引用字段（留空提交 null = 未关联）');
const preFields = preLoading.slice(preLoading.indexOf('fields: ['));
ok(!preFields.includes("key: 'preLoadingNo'"),
  '表单不提交 preLoadingNo：省略 / 空串一律由服务端权威生成');

const loadingList = moduleBlock(modulesSrc, "'loading-list': {");
ok(loadingList.includes("api: '/api/container/loading-lists'"),
  '装柜清单走 api/container/loading-lists（规范 Id 口径）');
ok(loadingList.includes('canSubmit: true'), '装柜清单支持提交 / 审核');
ok(loadingList.includes("detailKey: 'details'") && loadingList.includes('detailFields: LOADING_LIST_DETAIL_FIELDS'),
  '装柜清单声明主子表（主表 + 明细一起保存 / 回填）');
ok(loadingList.includes("type: 'ref', ref: 'customer'"), '客户是既有主数据引用（必填）');
ok(loadingList.includes("type: 'ref', ref: 'pre-loading'"),
  '可选预装柜引用是既有引用字段（留空提交 null = 未关联）');
const listFields = loadingList.slice(loadingList.indexOf('fields: ['));
ok(!listFields.includes("key: 'loadingListNo'"),
  '表单不提交 loadingListNo：省略 / 空串一律由服务端权威生成');

ok(modulesSrc.includes('const PRE_LOADING_DETAIL_FIELDS') && modulesSrc.includes('const LOADING_LIST_DETAIL_FIELDS'),
  '两个模块的明细列单独声明（数量 / 箱数 / 毛重 / 体积按基础单位）');
ok(!preLoading.includes('sourceSalesOrderDetailId'),
  '需求来源证据链接不在明细列里（只能由「需求来源」工作台显式维护）');
ok(!loadingList.includes('sourceStockOutDetailId'),
  '出运证据链接不在明细列里（只能由「出运证据」工作台显式维护）');
ok(!/\/api\/v2\/bills/.test(modulesSrc), '规范模块接线绝不调用旧存储过程族');
ok(!/FromSql|ExecuteSql|SqlCommand|select\s+\*/i.test(modulesSrc), '不执行任意 SQL');

/* ============ 4. 规范 Id 接线：crud.js 新增 / 修改 / 回填 / 主子表 ============ */
const crudSrc = jsSource('crud.js');
ok(crudSrc.includes("await api(`${mod.api}/${id}`, 'PUT', body)"),
  '修改按规范 Id（mod.api/{id}）提交，不使用任何 Oid');
ok(crudSrc.includes("await api(mod.api, 'POST', body)"), '新增提交到规范集合端点');
ok(crudSrc.includes('const row = await api(`${mod.api}/${id}`)'), '详情回填按规范 Id 读取');
ok(crudSrc.includes("body[mod.detailKey || 'details'] = detailCollect();"),
  '主子表：主表 + 明细一次提交（detailKey 默认 details）');
ok(crudSrc.includes("if (f.type === 'ref') v = v === '' ? null : Number(v);"),
  '主数据引用留空提交 null（未关联），绝不回落为 0');
ok(crudSrc.includes('sourceSalesOrderDetailId') && crudSrc.includes('sourceStockOutDetailId'),
  'crud.js 明确记录：随回填携带的持久化证据链接必须原样回传（不静默清空）');
ok(!/\/api\/v2\/bills/.test(crudSrc), 'crud.js 规范路径绝不调用旧存储过程族');

/* ============ 5. 页面接线与交付说明 ============ */
const indexSrc = fs.readFileSync(path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'index.html'), 'utf8');
ok(indexSrc.includes('/js/modules-doc2.js'), 'index.html 加载规范单据模块脚本');
ok(indexSrc.includes('/js/preloading-demand-links.js'), '「需求来源」工作台脚本已注册');
ok(indexSrc.includes('/js/loading-outbound-links.js'), '「出运证据」工作台脚本已注册');

const docPath = path.join(ROOT, 'docs', 'preloading-loading-canonical-entry.md');
ok(fs.existsSync(docPath), '交付说明 docs/preloading-loading-canonical-entry.md 存在');
const docSrc = fs.readFileSync(docPath, 'utf8');
ok(docSrc.includes('ERP-485') && docSrc.includes('api/container/pre-loadings')
  && docSrc.includes('api/container/loading-lists'),
  '交付说明记录两个规范端点与 ERP-485 口径');
ok(docSrc.includes('真实浏览器') && /未执行|不声称/.test(docSrc),
  '交付说明如实记录浏览器验收未执行（不以离线断言替代业务写入证据）');

console.log('\n✅ ERP-485 预装柜单 / 装柜清单规范入口主子表路由与表单接线契约全部通过（'
  + passed + ' 项断言）。');
console.log('ℹ️ 真实浏览器 + 真实隔离库 + 真实授权身份（真实 API 写入）的端到端验收由 ERP.IntegrationTests 执行'
  + '（PreLoadingLoadingCanonicalEntrySqlServerTests）；本文件为离线契约验证，不声称业务写入成功。');
console.log('ℹ️ 本环境无浏览器验收执行器（browser_acceptance.required = false）：真实浏览器 UI 工作流未执行，'
  + '不声称浏览器端到端或整阶段验收通过。');
process.exit(0);
