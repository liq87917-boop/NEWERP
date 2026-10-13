'use strict';
/* ERP-477 采购入库规范主子表入口路由契约（离线可执行，无真实网络 / 无成功业务写入模拟）：

   本文件只验证**可离线判定**的接线契约，绝不伪造任何成功业务写入：
     1) 菜单路由：bill-v2.js 的 BILL_CODE_MAP 不再把 stock-in 映射到旧存储过程族（ERP-404 写路径已 fail closed）；
     2) 规范模块：modules-doc2.js 的 MODULES['stock-in'] 是 EF 主子表模块（api/stock-ins，规范 Id + 明细 + 来源选择器）；
     3) 路由优先级：app.js 先判 BILL_CODE_MAP 再判 MODULES，因此移除映射后菜单必然落到规范模块；
     4) 旧族分离：旧 BILL_CONFIG['stock-in']（旧库单体表 Oid 读侧 / 默认值端点 / 整数天口径）保持只读可用，
        规范 Id 与旧 Oid 之间不存在任何映射；
     5) 规范 Id 接线：crud.js 新增 / 修改 / 详情回填与明细提交（含声明式可空引用）全部按 mod.api 走规范 Id；
     6) 来源选择器：stock-in-source-picker.js 显式选择 + 显式清空来源（未链接语义）且绝不改写仓库 / 供应商。
   真实浏览器 + 真实测试库 + 真实授权身份（含真实 API 写入）的端到端验收由 IntegrationTests 执行
   （src/ERP.IntegrationTests/StockInCanonicalEntrySqlServerTests.cs，专用 localdb + 全新 GUID NEWERP_AUTOTEST 库），
   本文件不声称替代该端到端验收：环境受限时不执行真实浏览器，也不把离线断言当作业务写入成功证据。

   运行：node tests/stock-in-canonical-entry.browser.js
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

/* ============ 1. 菜单路由：stock-in 不再映射旧存储过程族 ============ */
const billV2Src = jsSource('bill-v2.js');
const mapStart = billV2Src.indexOf('const BILL_CODE_MAP = {');
const mapEnd = billV2Src.indexOf('};', mapStart);
ok(mapStart >= 0 && mapEnd > mapStart, 'BILL_CODE_MAP 定义存在');
const billCodeMap = billV2Src.slice(mapStart, mapEnd);

ok(!/'stock-in'\s*:/.test(billCodeMap), "BILL_CODE_MAP 不再把 'stock-in' 映射到旧族（否则菜单打开永远无法保存的旧页）");
ok(!/stock-in\s*:\s*['\"]/.test(billCodeMap), 'BILL_CODE_MAP 无任何 stock-in 键');
ok(/\bstock-out\b/.test(billCodeMap), 'BILL_CODE_MAP 仍保留其它旧族单据（本次只改采购入库路由）');
ok(billV2Src.includes('ERP-477'), '路由改动带 ERP-477 说明（保留旧族只读与「不映射 Oid」口径）');

/* ============ 2. 路由优先级：先 BILL_CODE_MAP，再 MODULES ============ */
const appSrc = jsSource('app.js');
const billMapCheck = appSrc.indexOf('else if (BILL_CODE_MAP[code]) renderBillV2');
const modulesLookup = appSrc.indexOf('const mod = MODULES[code]');
ok(billMapCheck >= 0, 'app.js 仍保留 BILL_CODE_MAP 路由分支（其它旧族单据仍可打开）');
ok(modulesLookup > billMapCheck, 'app.js 在 MODULES 之前判定 BILL_CODE_MAP（移除映射即落到规范模块）');
ok(appSrc.includes("'stock-in': 'package-plus'"), 'stock-in 菜单图标仍注册（入口可见）');

/* ============ 3. 规范主子表模块注册（MODULES['stock-in']） ============ */
const CONTAINER_OPTS = [{ value: '20GP', label: '20GP' }, { value: '40HQ', label: '40HQ' }];
const MODULES = {};
new Function('MODULES', 'CONTAINER_OPTS', jsSource('modules-doc2.js'))(MODULES, CONTAINER_OPTS);
const stockIn = MODULES['stock-in'];
ok(!!stockIn, "modules-doc2.js 注册了 'stock-in' 规范模块");
eq(stockIn.api, '/api/stock-ins', '规范模块走 api/stock-ins（规范 Id 口径，不是 api/v2/bills）');
eq(stockIn.canSubmit, true, '规范模块支持提交 / 审核（状态流转入口可用）');
eq(stockIn.detailKey, 'details', '规范模块声明主子表明细键 details');
ok(Array.isArray(stockIn.detailFields) && stockIn.detailFields.length > 0, '规范模块声明明细列（主子表表单可用）');
const detailKeys = stockIn.detailFields.map(f => f.key);
['productId', 'productName', 'spec', 'unit', 'quantity', 'weight', 'volume', 'batchNo']
  .forEach(key => ok(detailKeys.includes(key), '明细列包含 ' + key));
const headerKeys = stockIn.fields.map(f => f.key);
['stockInDate', 'purchaseOrderId', 'supplierId', 'warehouseId']
  .forEach(key => ok(headerKeys.includes(key), '表头字段包含 ' + key));
const sourceField = stockIn.fields.find(f => f.key === 'purchaseOrderId');
eq(sourceField.selector, 'stock-in-source', '来源字段声明 ERP-375 显式来源选择器入口');
eq(sourceField.nullable, true, '来源字段是声明式可空引用（清空 / 留空提交 null，而不是哨兵值 0）');
const dateField = stockIn.fields.find(f => f.key === 'stockInDate');
eq(dateField.type, 'date', '入库日期为日期字段（服务端实体同形）');

/* ============ 4. 旧族分离：旧读侧 / 默认值端点 / 整数天口径保持只读可用 ============ */
const billConfigSrc = jsSource('bill-config.js');
ok(billConfigSrc.includes("billType: 'stock-in'"), '旧族 BILL_CONFIG.stock-in（旧库单体表 Oid 读侧）仍保留');
const BILL_CONFIG = new Function('CURRENCY_OPTS', billConfigSrc + '\nreturn BILL_CONFIG;')(
  [{ value: '1', label: '人民币 CNY' }]);
ok(!!BILL_CONFIG['stock-in'], '旧族采购入库配置可在浏览器中加载（旧数据排查仍可读）');
ok(billV2Src.includes('api(`/api/v2/bills/${BILL_CODE}/defaults`)'),
  'ERP-469：仍复用既有（参数授权）默认值端点，未新增取数通道');
ok(billV2Src.includes('loadAuthorizedBillDefaults()') && billV2Src.includes('startNewBillForm()'),
  'ERP-469：新建入口显式加载既有默认值（旧族读侧不被削弱）');
ok(!/['\"]?stock-in['\"]?\s*:\s*['\"]?stock-in['\"]?/.test(billV2Src.slice(mapStart, mapEnd)),
  '旧 Oid 与规范 Id 之间不存在任何映射（绝不把 Oid 当作规范 Id）');

/* ============ 5. 规范 Id 接线：crud.js 新增 / 修改 / 回填 / 明细 ============ */
const crudSrc = jsSource('crud.js');
ok(crudSrc.includes('await api(`${mod.api}/${id}`, \'PUT\', body)'),
  '修改按规范 Id（mod.api/{id}）提交，不使用任何 Oid');
ok(crudSrc.includes('await api(mod.api, \'POST\', body)'), '新增提交到规范集合端点');
ok(crudSrc.includes("body[mod.detailKey || 'details'] = detailCollect()"),
  '主子表提交把明细随表头一起提交（规范 Id 明细）');
ok(crudSrc.includes('const row = await api(`${mod.api}/${id}`)') && crudSrc.includes('DETAIL_ROWS = (row[mod.detailKey'),
  '详情回填按规范 Id 读取并回填明细');
ok(crudSrc.includes('f.nullable === true ? null : 0'),
  '声明式可空引用（含采购入库来源）留空提交 null，绝不回落为 0');
ok(!/\/api\/v2\/bills/.test(crudSrc), 'crud.js 规范路径绝不调用旧存储过程族');

/* ============ 6. 来源选择器：显式选择 + 显式清空（未链接）+ 不改写仓库 ============ */
const pickerSrc = jsSource('stock-in-source-picker.js');
const picker = require(jsPath('stock-in-source-picker.js'));
ok(pickerSrc.includes('function sisInstallFormHook'), '声明式表单接入钩子存在（包裹既有 fieldHtml / openForm）');
ok(pickerSrc.includes("field.selector !== 'stock-in-source'"), '只为声明 selector 的字段追加来源入口（其它单据不受影响）');
ok(pickerSrc.includes('async function openStockInSourcePicker'), '业务表单入口存在');
ok(pickerSrc.includes("sisSetField('purchaseOrderId', ''"), '显式清空来源：只清空来源引用字段');
ok(!pickerSrc.includes("sisSetField('warehouseId'"), '选择器绝不改写仓库字段（保留用户所选仓库）');
ok(!/sisSetField\(\s*'(warehouse|Warehouse)Id'/.test(pickerSrc), '绝不写入仓库字段（camelCase / PascalCase）');
ok(!/sisSetField\(\s*'supplierId',\s*''/.test(pickerSrc), '清空来源绝不臆造 / 清空供应商');
eq(picker.sisClearSource(), false, '未加载浏览器文档时清空来源安全返回 false（绝不臆造浏览器环境）');
eq(picker.sisInstallFormHook(), false, '未加载 crud.js 时安装钩子安全返回 false');
ok(picker.sisSourceButtonHtml().includes('openStockInSourcePicker'), '表单入口调用选择器');
ok(pickerSrc.includes('/source-candidates?') && pickerSrc.includes('source-candidates/${purchaseOrderId}'),
  '复用既有两个只读有界来源接口（不新增取数通道）');
ok(!/FromSql|ExecuteSql|SqlCommand|select\s+\*/i.test(pickerSrc), '不执行任意 SQL');
ok(!/\bparseInt\(|parseFloat\(/.test(pickerSrc), '绝不从文本解析 Id / 数量');
ok(picker.sisStatusText('Pending').includes('待提交') && picker.sisNormalizeStatus('2') === 'Approved',
  '状态口径与后端枚举同源（仅草稿可维护来源）');

console.log('\n✅ ERP-477 采购入库规范入口路由 / 主子表 / 来源选择器接线契约全部通过（' + passed + ' 项断言）。');
console.log('ℹ️ 真实浏览器 + 真实隔离库 + 真实授权身份（真实 API 写入）的端到端验收由 ERP.IntegrationTests 执行'
  + '（StockInCanonicalEntrySqlServerTests）；本文件为离线契约验证，不声称业务写入成功。');
process.exit(0);
