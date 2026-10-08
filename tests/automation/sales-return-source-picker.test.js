'use strict';
/* ERP-374 销售退货来源选择器前端逻辑单测（可执行）：
   只接受服务端标记 available 的候选、零容量 / 损坏证据不可选、数量必须为正数且不超剩余可退容量、
   回填明细只使用权威商品 / 规格 / 基础单位（单价与成本留 0，绝不臆造价格）、安全转义，
   以及前端接线契约（复用只读候选 / 详情接口、声明式表单入口、加载脚本、不执行任意 SQL、
   绝不从文本解析 Id）。
   运行：node tests/automation/sales-return-source-picker.test.js
   失败时抛异常并以非零码退出。 */

const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const JS_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'sales-return-source-picker.js');
const INDEX_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'index.html');
const MODULES_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'modules-doc2.js');

let passed = 0;
function ok(cond, msg) { assert.ok(cond, msg); passed += 1; }
function eq(actual, expected, msg) { assert.deepStrictEqual(actual, expected, msg); passed += 1; }

const mod = require(JS_PATH);

const candidate = (extra) => Object.assign({
  sourceStockOutId: 11, sourceStockOutNo: 'CK-SR-0001', customerId: 9001, customerName: '义乌客户',
  warehouseId: 77, warehouseName: '退货仓A', productId: 501, productName: '商品501', spec: '规格A',
  baseUnit: 'PCS', sourceBaseQuantity: 10, effectiveReturnedBaseQuantity: 3, remainingBaseQuantity: 7,
  available: true, unavailableReason: '',
}, extra || {});

// ---- 1. 状态口径：仅「待提交」可维护来源 ----
ok(mod.srsIsEditableStatus('Pending'), 'Pending 可维护');
ok(mod.srsIsEditableStatus(0), '数字 0（Pending 枚举值）可维护');
ok(!mod.srsIsEditableStatus('Approved'), 'Approved 只读');
ok(!mod.srsIsEditableStatus(2), '数字 2（Approved）只读');
eq(mod.srsNormalizeStatus('已审核'), 'Approved', '中文状态归一化');
eq(mod.srsNormalizeStatus('待提交（草稿）'), 'Pending', '状态文案归一化');
ok(mod.srsReadonlyReason('Approved').includes('只读'), '只读原因显式可见');
eq(mod.srsReadonlyReason('Pending'), '', '待提交无只读原因');

// ---- 2. 单据 Id 闸门与条数钳制 ----
eq(mod.srsGuardSavedId(0), '', '新建（0）允许选择来源');
eq(mod.srsGuardSavedId(12), '', '已保存单据放行');
ok(mod.srsGuardSavedId(-1) !== '', '负数 Id 拒绝');
ok(mod.srsGuardSavedId('abc') !== '', '非数字 Id 拒绝');
eq(mod.srsNormalizeTake(0), mod.SRS_TAKE_DEFAULT, '0 取默认条数');
eq(mod.srsNormalizeTake(-5), mod.SRS_TAKE_DEFAULT, '负数取默认条数');
eq(mod.srsNormalizeTake(9999), mod.SRS_TAKE_MAX, '超上限被钳制');
eq(mod.srsNormalizeTake(20), 20, '合法条数原样');

// ---- 3. 候选可用性：只有 available = true 可选 ----
const candidates = [
  candidate(),
  candidate({ sourceStockOutId: 12, sourceStockOutNo: 'CK-SR-0002', remainingBaseQuantity: 0, available: false, unavailableReason: '可退容量为 0' }),
  candidate({ sourceStockOutId: 13, sourceStockOutNo: 'CK-SR-0003', productId: 502, baseUnit: '', available: false, unavailableReason: '单位未知' }),
];
eq(mod.srsAvailableCandidates(candidates).length, 1, '零容量 / 单位未知不可选');
eq(mod.srsAvailableCandidates(candidates)[0].sourceStockOutId, 11, '仅保留可用候选');

const options = mod.srsSourceOptions(candidates.concat([candidate({ productId: 503, remainingBaseQuantity: 2 })]));
eq(options.length, 1, '按来源 Id 去重，仅保留有可退行的来源');
eq(options[0].lineCount, 2, '统计可退行数');
eq(options[0].remainingTotal, 9, '合计剩余可退容量');

// ---- 4. 候选展示文案：来源 / 客户 / 商品 / 规格 / 基础单位 / 来源数量 / 已生效退货 / 剩余 ----
const label = mod.srsCandidateLabel(candidate());
ok(label.includes('CK-SR-0001') && label.includes('义乌客户') && label.includes('商品501')
  && label.includes('规格A') && label.includes('PCS') && label.includes('来源数量 10')
  && label.includes('已生效退货 3') && label.includes('剩余可退 7'), '候选文案含全部权威字段');

// ---- 5. 表头回填：权威来源 Id / 单号 / 客户 / 仓库 ----
eq(mod.srsHeaderFill({
  sourceStockOutId: 11, sourceStockOutNo: 'CK-SR-0001', customerId: 9001, customerName: '义乌客户',
  warehouseId: 77, warehouseName: '退货仓A',
}), {
  sourceStockOutId: 11, sourceStockOutNo: 'CK-SR-0001', customerId: 9001, customerName: '义乌客户',
  warehouseId: 77, warehouseName: '退货仓A',
}, '表头回填与详情一致');
eq(mod.srsHeaderFill(null).sourceStockOutId, 0, '无详情时表头为空（绝不臆造）');

// ---- 6. 数量校验：必须为正数且不超剩余可退容量 ----
ok(mod.srsValidateQuantity('2', 7).ok, '正数部分数量接受');
eq(mod.srsValidateQuantity('2', 7).value, 2, '数量原样回填');
ok(!mod.srsValidateQuantity('', 7).ok, '空数量拒绝');
ok(!mod.srsValidateQuantity('0', 7).ok, '0 拒绝（必须为正数）');
ok(!mod.srsValidateQuantity('-1', 7).ok, '负数拒绝');
ok(!mod.srsValidateQuantity('abc', 7).ok, '文本拒绝');
ok(!mod.srsValidateQuantity('8', 7).ok, '超过剩余可退容量拒绝');
ok(mod.srsValidateQuantity('7', 7).ok, '恰好等于剩余容量允许');

// ---- 7. 明细行构造：权威商品 / 规格 / 基础单位，单价与成本留 0 ----
const detail = {
  sourceStockOutId: 11, sourceStockOutNo: 'CK-SR-0001', customerId: 9001, customerName: '义乌客户',
  warehouseId: 77, warehouseName: '退货仓A', available: true, unavailableReason: '',
  lines: [candidate(), candidate({ productId: 504, productName: '商品504', remainingBaseQuantity: 0, available: false })],
};
const rows = mod.srsBuildDetailRows(detail);
eq(rows.length, 1, '只构造可用行');
eq(rows[0].unit, 'PCS', '单位取权威基础单位');
eq(rows[0].unitPrice, 0, '单价留 0（绝不臆造价格）');
eq(rows[0].unitCost, 0, '成本单价留 0（服务端兜底）');
eq(rows[0].spec, '规格A', '规格取权威值');
eq(rows[0].quantity, 7, '默认数量为剩余可退容量（可下调）');

// ---- 8. 数量写回：部分数量允许，非法整体拒绝 ----
const applied = mod.srsApplyQuantities(rows, { 501: '2' });
ok(applied.ok, '部分数量写回成功');
eq(applied.rows[0].quantity, 2, '数量写入 2');
eq(applied.rows[0].amount, 0, '金额随 0 单价保持 0（不臆造金额）');
ok(!mod.srsApplyQuantities(rows, { 501: '9' }).ok, '超容量整体拒绝');
ok(!mod.srsApplyQuantities(rows, { 501: '0' }).ok, '0 整体拒绝');
ok(!mod.srsApplyQuantities(rows, {}).ok, '缺数量整体拒绝');
eq(mod.srsApplyQuantities(rows, { 501: '9' }).rows[0].quantity, 7, '拒绝时保留原行（不写入表单）');

// ---- 9. 应用闸门 ----
ok(mod.srsCanApply({ detail, quantities: { 501: '2' } }), '合法选择可应用');
ok(!mod.srsCanApply({ detail, quantities: { 501: '2' }, readonly: true }), '只读不可应用');
ok(!mod.srsCanApply({ detail, quantities: { 501: '2' }, loading: true }), '加载中不可应用');
ok(!mod.srsCanApply({ detail, quantities: { 501: '9' } }), '超容量不可应用');
ok(!mod.srsCanApply({ detail: Object.assign({}, detail, { sourceStockOutId: 0 }), quantities: { 501: '2' } }), '未选来源不可应用');

// ---- 10. 安全渲染：转义 + 不可用原因显式可见 ----
const sourceRendered = mod.srsSourceRowHtml({ sourceStockOutId: 11, sourceStockOutNo: '<b>CK</b>', customerName: '客户', warehouseName: '仓', lineCount: 1, remainingTotal: 7 });
ok(sourceRendered.includes('&lt;b&gt;CK&lt;/b&gt;'), '来源行转义');
ok(sourceRendered.includes('srsPickSource(11)'), '来源行提供显式选择入口');

const deniedRendered = mod.srsCandidateRowHtml(candidate({ sourceStockOutNo: '<i>x</i>', available: false, unavailableReason: '单位未知' }));
ok(deniedRendered.includes('&lt;i&gt;x&lt;/i&gt;'), '候选行转义');
ok(deniedRendered.includes('单位未知'), '不可用原因显式可见');
ok(mod.srsCandidateRowHtml(candidate()).includes('可用'), '可用候选显式标注可用');

const errorRendered = mod.srsErrorHtml('network', 'Failed to fetch');
ok(errorRendered.includes('无法连接服务器'), '网络失败显式可见');
ok(errorRendered.includes('role="alert"'), '失败提示是无障碍可见区域');
eq(mod.srsErrorMessage({ message: 'Failed to fetch' }).kind, 'network', '网络失败归类');
eq(mod.srsErrorMessage({ message: '当前账号没有「销售出库」模块授权' }).kind, 'unauthorized', '授权失败归类');

ok(mod.srsSourceButtonHtml().includes('openSalesReturnSourcePicker'), '表单入口调用选择器');

// ---- 11. 前端接线契约：复用只读接口与声明式表单，不执行任意 SQL，绝不从文本解析 Id ----
const jsSrc = fs.readFileSync(JS_PATH, 'utf8');
const indexSrc = fs.readFileSync(INDEX_PATH, 'utf8');
const modulesSrc = fs.readFileSync(MODULES_PATH, 'utf8');

ok(jsSrc.includes('/source-candidates?'), '复用有界候选接口');
ok(jsSrc.includes('source-candidates/${sourceStockOutId}'), '复用来源详情接口');
ok(jsSrc.includes('async function openSalesReturnSourcePicker'), '导出业务表单入口');
ok(jsSrc.includes('function srsInstallFormHook'), '导出声明式表单接入钩子');
ok(jsSrc.includes('srsApplyToForm'), '导出应用到表单动作');
ok(!/FromSql|ExecuteSql|SqlCommand|select\s+\*/i.test(jsSrc), '不执行任意 SQL');
ok(!/\bparseInt\(|parseFloat\(/.test(jsSrc), '绝不从文本解析 Id / 数量');
ok(jsSrc.includes('unitPrice: 0') && jsSrc.includes('unitCost: 0'), '单价与成本单价一律留 0');
ok(indexSrc.includes('/js/sales-return-source-picker.js'), 'index.html 加载选择器脚本');
ok(modulesSrc.includes("selector: 'sales-return-source'"), 'modules-doc2 声明销售退货来源选择入口');

console.log('\n✅ ERP-374 销售退货来源选择器 UI 逻辑测试全部通过（' + passed + ' 项断言）。\n');
