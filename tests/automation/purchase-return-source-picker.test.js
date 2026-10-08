'use strict';
/* ERP-377 采购退货来源选择器前端逻辑单测（可执行）：
   只接受服务端标记 available 的候选、零容量 / 损坏证据不可选、数量必须为正数且不超净可退容量、
   回填明细只使用权威商品 / 规格 / 基础单位（单价与成本留 0，绝不臆造价格 / 成本）、安全转义，
   以及前端接线契约（复用只读候选 / 详情接口、声明式表单入口、加载脚本、SP 单据页入口、
   不执行任意 SQL、绝不从文本解析 Id）。
   运行：node tests/automation/purchase-return-source-picker.test.js
   失败时抛异常并以非零码退出。 */

const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const JS_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'purchase-return-source-picker.js');
const INDEX_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'index.html');
const MODULES_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'modules-doc2.js');
const BILL_EDIT_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'bill-edit.js');

let passed = 0;
function ok(cond, msg) { assert.ok(cond, msg); passed += 1; }
function eq(actual, expected, msg) { assert.deepStrictEqual(actual, expected, msg); passed += 1; }

const mod = require(JS_PATH);

const candidate = (extra) => Object.assign({
  sourceStockInId: 21, sourceStockInNo: 'RK-PR-0001', supplierId: 9002, supplierName: '供应商A',
  warehouseId: 77, warehouseName: '退货仓A', productId: 501, productName: '商品501', spec: '规格A',
  baseUnit: 'PCS', sourceBaseQuantity: 10, effectiveReturnedBaseQuantity: 3, remainingBaseQuantity: 7,
  available: true, unavailableReason: '',
}, extra || {});

// ---- 1. 状态口径：仅「待提交」可维护来源 ----
ok(mod.prsIsEditableStatus('Pending'), 'Pending 可维护');
ok(mod.prsIsEditableStatus(0), '数字 0（Pending 枚举值）可维护');
ok(!mod.prsIsEditableStatus('Approved'), 'Approved 只读');
ok(!mod.prsIsEditableStatus(2), '数字 2（Approved）只读');
eq(mod.prsNormalizeStatus('已审核'), 'Approved', '中文状态归一化');
eq(mod.prsNormalizeStatus('待提交（草稿）'), 'Pending', '状态文案归一化');
ok(mod.prsReadonlyReason('Approved').includes('只读'), '只读原因显式可见');
eq(mod.prsReadonlyReason('Pending'), '', '待提交无只读原因');

// ---- 2. 单据 Id 闸门与条数钳制 ----
eq(mod.prsGuardSavedId(0), '', '新建（0）允许选择来源');
eq(mod.prsGuardSavedId(12), '', '已保存单据放行');
ok(mod.prsGuardSavedId(-1) !== '', '负数 Id 拒绝');
ok(mod.prsGuardSavedId('abc') !== '', '非数字 Id 拒绝');
eq(mod.prsNormalizeTake(0), mod.PRS_TAKE_DEFAULT, '0 取默认条数');
eq(mod.prsNormalizeTake(-5), mod.PRS_TAKE_DEFAULT, '负数取默认条数');
eq(mod.prsNormalizeTake(9999), mod.PRS_TAKE_MAX, '超上限被钳制');
eq(mod.prsNormalizeTake(20), 20, '合法条数原样');

// ---- 3. 候选可用性：只有 available = true 可选 ----
const candidates = [
  candidate(),
  candidate({ sourceStockInId: 22, sourceStockInNo: 'RK-PR-0002', remainingBaseQuantity: 0, available: false, unavailableReason: '可退容量为 0' }),
  candidate({ sourceStockInId: 23, sourceStockInNo: 'RK-PR-0003', productId: 502, baseUnit: '', available: false, unavailableReason: '单位未知' }),
];
eq(mod.prsAvailableCandidates(candidates).length, 1, '零容量 / 单位未知不可选');
eq(mod.prsAvailableCandidates(candidates)[0].sourceStockInId, 21, '仅保留可用候选');

const options = mod.prsSourceOptions(candidates.concat([candidate({ productId: 503, remainingBaseQuantity: 2 })]));
eq(options.length, 1, '按来源 Id 去重，仅保留有可退行的来源');
eq(options[0].lineCount, 2, '统计可退行数');
eq(options[0].remainingTotal, 9, '合计剩余可退容量');

// ---- 4. 候选展示文案：来源 / 供应商 / 商品 / 规格 / 基础单位 / 来源数量 / 已生效退货 / 剩余 ----
const label = mod.prsCandidateLabel(candidate());
ok(label.includes('RK-PR-0001') && label.includes('供应商A') && label.includes('商品501')
  && label.includes('规格A') && label.includes('PCS') && label.includes('来源数量 10')
  && label.includes('已生效退货 3') && label.includes('剩余可退 7'), '候选文案含全部权威字段');

// ---- 5. 表头回填：权威来源 Id / 单号 / 供应商 / 仓库 ----
eq(mod.prsHeaderFill(null).sourceStockInId, 0, '未选来源回填空表头');
const header = mod.prsHeaderFill(candidate());
eq(header.sourceStockInId, 21, '来源 Id 回填');
eq(header.sourceStockInNo, 'RK-PR-0001', '来源单号回填');
eq(header.supplierId, 9002, '供应商 Id 回填');
eq(header.supplierName, '供应商A', '供应商名称回填');
eq(header.warehouseId, 77, '仓库 Id 回填');
ok(mod.PRS_WRITTEN_FIELDS.includes('sourceStockInId') && mod.PRS_WRITTEN_FIELDS.includes('supplierId')
  && mod.PRS_WRITTEN_FIELDS.includes('warehouseId'), '回填字段受控');

// ---- 6. 数量校验：必须为正数且不超净可退容量 ----
ok(mod.prsValidateQuantity('2', 7).ok, '正数合法');
ok(!mod.prsValidateQuantity('0', 7).ok, '0 拒绝');
ok(!mod.prsValidateQuantity('8', 7).ok, '超容量拒绝');
ok(!mod.prsValidateQuantity('', 7).ok, '空值拒绝');
ok(!mod.prsValidateQuantity('1.2.3', 7).ok, '非法数字拒绝');
eq(mod.prsValidateQuantity('7', 7).value, 7, '精确边界允许');

// ---- 7. 明细回填：权威基础单位，价格 / 成本留 0 ----
const detail = {
  sourceStockInId: 21, sourceStockInNo: 'RK-PR-0001', supplierId: 9002, supplierName: '供应商A',
  warehouseId: 77, warehouseName: '退货仓A', available: true, unavailableReason: '',
  lines: [candidate(), candidate({ productId: 504, productName: '商品504', remainingBaseQuantity: 0, available: false })],
};
const rows = mod.prsBuildDetailRows(detail);
eq(rows.length, 1, '只构造可用行');
eq(rows[0].unit, 'PCS', '单位取权威基础单位');
eq(rows[0].unitPrice, 0, '单价留 0（绝不臆造价格）');
eq(rows[0].unitCost, 0, '成本单价留 0（服务端兜底）');
eq(rows[0].spec, '规格A', '规格取权威值');
eq(rows[0].quantity, 7, '默认数量为净可退容量（可下调）');


// ---- 8. 数量写回：部分数量允许，非法整体拒绝 ----
const applied = mod.prsApplyQuantities(rows, { 501: '2' });
ok(applied.ok, '部分数量写回成功');
eq(applied.rows[0].quantity, 2, '数量写入 2');
eq(applied.rows[0].amount, 0, '金额随 0 单价保持 0（不臆造金额）');
ok(!mod.prsApplyQuantities(rows, { 501: '9' }).ok, '超容量整体拒绝');
ok(!mod.prsApplyQuantities(rows, { 501: '0' }).ok, '0 整体拒绝');
ok(!mod.prsApplyQuantities(rows, {}).ok, '缺数量整体拒绝');
eq(mod.prsApplyQuantities(rows, { 501: '9' }).rows[0].quantity, 7, '拒绝时保留原行（不写入表单）');

// ---- 9. 应用闸门 ----
ok(mod.prsCanApply({ detail, quantities: { 501: '2' } }), '合法选择可应用');
ok(!mod.prsCanApply({ detail, quantities: { 501: '2' }, readonly: true }), '只读不可应用');
ok(!mod.prsCanApply({ detail, quantities: { 501: '2' }, loading: true }), '加载中不可应用');
ok(!mod.prsCanApply({ detail, quantities: { 501: '9' } }), '超容量不可应用');
ok(!mod.prsCanApply({ detail: Object.assign({}, detail, { sourceStockInId: 0 }), quantities: { 501: '2' } }), '未选来源不可应用');

// ---- 10. 安全渲染：转义 + 不可用原因显式可见 ----
const sourceRendered = mod.prsSourceRowHtml({ sourceStockInId: 21, sourceStockInNo: '<b>RK</b>', supplierName: '供应商', warehouseName: '仓', lineCount: 1, remainingTotal: 7 });
ok(sourceRendered.includes('&lt;b&gt;RK&lt;/b&gt;'), '来源行转义');
ok(sourceRendered.includes('prsPickSource(21)'), '来源行提供显式选择入口');

const deniedRendered = mod.prsCandidateRowHtml(candidate({ sourceStockInNo: '<i>x</i>', available: false, unavailableReason: '单位未知' }));
ok(deniedRendered.includes('&lt;i&gt;x&lt;/i&gt;'), '候选行转义');
ok(deniedRendered.includes('单位未知'), '不可用原因显式可见');
ok(mod.prsCandidateRowHtml(candidate()).includes('可用'), '可用候选显式标注可用');

const errorRendered = mod.prsErrorHtml('network', 'Failed to fetch');
ok(errorRendered.includes('无法连接服务器'), '网络失败显式可见');
ok(errorRendered.includes('role="alert"'), '失败提示是无障碍可见区域');
eq(mod.prsErrorMessage({ message: 'Failed to fetch' }).kind, 'network', '网络失败归类');
eq(mod.prsErrorMessage({ message: '当前账号没有「采购入库」模块授权' }).kind, 'unauthorized', '授权失败归类');

ok(mod.prsSourceButtonHtml().includes('openPurchaseReturnSourcePicker'), '表单入口调用选择器');

// ---- 11. 前端接线契约：复用只读接口与声明式表单，不执行任意 SQL，绝不从文本解析 Id ----
const jsSrc = fs.readFileSync(JS_PATH, 'utf8');
const indexSrc = fs.readFileSync(INDEX_PATH, 'utf8');
const modulesSrc = fs.readFileSync(MODULES_PATH, 'utf8');
const billEditSrc = fs.readFileSync(BILL_EDIT_PATH, 'utf8');

ok(jsSrc.includes('/source-candidates?'), '复用有界候选接口');
ok(jsSrc.includes('source-candidates/${sourceStockInId}'), '复用来源详情接口');
ok(jsSrc.includes('async function openPurchaseReturnSourcePicker'), '导出业务表单入口');
ok(jsSrc.includes('function prsInstallFormHook'), '导出声明式表单接入钩子');
ok(jsSrc.includes('prsApplyToForm'), '导出应用到表单动作');
ok(!/FromSql|ExecuteSql|SqlCommand|select\s+\*/i.test(jsSrc), '不执行任意 SQL');
ok(!/\bparseInt\(|parseFloat\(/.test(jsSrc), '绝不从文本解析 Id / 数量');
ok(jsSrc.includes('unitPrice: 0') && jsSrc.includes('unitCost: 0'), '单价与成本单价一律留 0');
ok(indexSrc.includes('/js/purchase-return-source-picker.js'), 'index.html 加载选择器脚本');
ok(modulesSrc.includes("selector: 'purchase-return-source'"), 'modules-doc2 声明采购退货来源选择入口');
ok(billEditSrc.includes('prsSourceButtonHtml') && billEditSrc.includes("BILL_CODE === 'purchase-return'"),
  'bill-edit.js 仅对 purchase-return 注入入口');

console.log('\n✅ ERP-377 采购退货来源选择器 UI 逻辑测试全部通过（' + passed + ' 项断言）。\n');
