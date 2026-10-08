'use strict';
/* ERP-375 采购入库来源选择器前端逻辑单测（可执行）：
   只接受服务端标记 available 的候选、重复 / 歧义明细与已收货满额不可选、数量必须为正数且不超剩余可收数量、
   回填明细只使用权威商品 / 规格 / 基础单位（本页不录入单价 / 成本，绝不臆造成本）、**保留用户所选仓库**、
   安全转义，以及前端接线契约（复用只读候选 / 详情接口、声明式表单入口、加载脚本、不执行任意 SQL、
   绝不从文本解析 Id）。
   运行：node tests/automation/stock-in-source-picker.test.js
   失败时抛异常并以非零码退出。 */

const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const JS_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'stock-in-source-picker.js');
const INDEX_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'index.html');
const MODULES_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'modules-doc2.js');
const BILL_EDIT_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'bill-edit.js');

let passed = 0;
function ok(cond, msg) { assert.ok(cond, msg); passed += 1; }
function eq(actual, expected, msg) { assert.deepStrictEqual(actual, expected, msg); passed += 1; }

const mod = require(JS_PATH);

const candidate = (extra) => Object.assign({
  purchaseOrderId: 21, orderNo: 'PO-0001', supplierId: 9001, supplierName: '义乌供应商', currency: 'CNY',
  exchangeRate: 1, productId: 501, productName: '商品501', spec: '规格A', baseUnit: 'PCS',
  authorizedBaseQuantity: 10, receivedBaseQuantity: 3, remainingBaseQuantity: 7,
  available: true, unavailableReason: '',
}, extra || {});

// ---- 1. 状态口径：仅「待提交」可维护来源 ----
ok(mod.sisIsEditableStatus('Pending'), 'Pending 可维护');
ok(mod.sisIsEditableStatus(0), '数字 0（Pending 枚举值）可维护');
ok(!mod.sisIsEditableStatus('Approved'), 'Approved 只读');
ok(!mod.sisIsEditableStatus(2), '数字 2（Approved）只读');
eq(mod.sisNormalizeStatus('已审核'), 'Approved', '中文状态归一化');
eq(mod.sisNormalizeStatus('待提交（草稿）'), 'Pending', '状态文案归一化');
ok(mod.sisReadonlyReason('Approved').includes('只读'), '只读原因显式可见');
eq(mod.sisReadonlyReason('Pending'), '', '待提交无只读原因');

// ---- 2. 单据 Id 闸门与条数钳制 ----
eq(mod.sisGuardSavedId(0), '', '新建（0）允许选择来源');
eq(mod.sisGuardSavedId(12), '', '已保存单据放行');
ok(mod.sisGuardSavedId(-1) !== '', '负数 Id 拒绝');
ok(mod.sisGuardSavedId('abc') !== '', '非数字 Id 拒绝');
eq(mod.sisNormalizeTake(0), mod.SIS_TAKE_DEFAULT, '0 取默认条数');
eq(mod.sisNormalizeTake(-5), mod.SIS_TAKE_DEFAULT, '负数取默认条数');
eq(mod.sisNormalizeTake(9999), mod.SIS_TAKE_MAX, '超上限被钳制');
eq(mod.sisNormalizeTake(20), 20, '合法条数原样');

// ---- 3. 候选可用性：只有 available = true 可选 ----
const candidates = [
  candidate(),
  candidate({ purchaseOrderId: 22, orderNo: 'PO-0002', remainingBaseQuantity: 0, available: false, unavailableReason: '剩余可收数量为 0' }),
  candidate({ purchaseOrderId: 23, orderNo: 'PO-0003', productId: 502, baseUnit: '', available: false, unavailableReason: '单位未知' }),
  candidate({ purchaseOrderId: 24, orderNo: 'PO-0004', available: false, unavailableReason: '来源采购订单同商品存在多条明细，授权数量不唯一（不可用）' }),
];
eq(mod.sisAvailableCandidates(candidates).length, 1, '零容量 / 单位未知 / 重复歧义不可选');
eq(mod.sisAvailableCandidates(candidates)[0].purchaseOrderId, 21, '仅保留可用候选');

const options = mod.sisSourceOptions(candidates.concat([candidate({ productId: 503, remainingBaseQuantity: 2 })]));
eq(options.length, 1, '按来源订单 Id 去重，仅保留有可收行的来源');
eq(options[0].lineCount, 2, '统计可收行数');
eq(options[0].remainingTotal, 9, '合计剩余可收数量');

// ---- 4. 候选展示文案：订单号 / 供应商 / 商品 / 规格 / 基础单位 / 授权数量 / 已收货 / 剩余可收 ----
const label = mod.sisCandidateLabel(candidate());
ok(label.includes('PO-0001') && label.includes('义乌供应商') && label.includes('商品501')
  && label.includes('规格A') && label.includes('PCS') && label.includes('授权数量 10')
  && label.includes('已收货 3') && label.includes('剩余可收 7'), '候选文案含全部权威字段');

// ---- 5. 表头回填：权威来源 Id / 单号 / 供应商；**不含仓库**（保留用户所选） ----
const header = mod.sisHeaderFill({
  purchaseOrderId: 21, orderNo: 'PO-0001', supplierId: 9001, supplierName: '义乌供应商',
  currency: 'CNY', exchangeRate: 1,
});
eq(header.purchaseOrderId, 21, '来源 Id 回填');
eq(header.orderNo, 'PO-0001', '来源单号回填');
eq(header.supplierId, 9001, '权威供应商回填');
ok(!('warehouseId' in header), '绝不回填仓库（保留用户所选仓库）');
eq(mod.sisHeaderFill(null).purchaseOrderId, 0, '未选来源时来源 Id 为 0');

// ---- 6. 数量校验：正数 + 不超剩余可收 ----
ok(mod.sisValidateQuantity('2', 7).ok, '正数部分数量允许');
eq(mod.sisValidateQuantity('2', 7).value, 2, '数量原样回填');
ok(!mod.sisValidateQuantity('9', 7).ok, '超剩余整体拒绝');
ok(!mod.sisValidateQuantity('0', 7).ok, '0 拒绝');
ok(!mod.sisValidateQuantity('-1', 7).ok, '负数拒绝');
ok(!mod.sisValidateQuantity('abc', 7).ok, '非数字拒绝');
ok(!mod.sisValidateQuantity('', 7).ok, '空拒绝');

// ---- 7. 明细回填：权威商品 / 规格 / 基础单位，数量默认剩余可收；本页不录入单价 / 成本 ----
const detail = {
  purchaseOrderId: 21, orderNo: 'PO-0001', supplierId: 9001, supplierName: '义乌供应商',
  currency: 'CNY', exchangeRate: 1, available: true, unavailableReason: '',
  lines: [candidate(), candidate({ productId: 504, productName: '商品504', remainingBaseQuantity: 0, available: false })],
};
const rows = mod.sisBuildDetailRows(detail);
eq(rows.length, 1, '只构造可用行');
eq(rows[0].unit, 'PCS', '单位取权威基础单位');
eq(rows[0].spec, '规格A', '规格取权威值');
eq(rows[0].quantity, 7, '默认数量为剩余可收数量（可下调）');
eq(rows[0].productId, 501, '商品 Id 取权威值');
ok(!('unitPrice' in rows[0]) && !('amount' in rows[0]) && !('unitCost' in rows[0]),
  '本页不录入单价 / 成本 / 金额（绝不臆造成本）');

// ---- 8. 数量写回：部分数量允许，非法整体拒绝 ----
const applied = mod.sisApplyQuantities(rows, { 501: '2' });
ok(applied.ok, '部分数量写回成功');
eq(applied.rows[0].quantity, 2, '数量写入 2');
ok(!mod.sisApplyQuantities(rows, { 501: '9' }).ok, '超剩余整体拒绝');
ok(!mod.sisApplyQuantities(rows, { 501: '0' }).ok, '0 整体拒绝');
ok(!mod.sisApplyQuantities(rows, {}).ok, '缺数量整体拒绝');
eq(mod.sisApplyQuantities(rows, { 501: '9' }).rows[0].quantity, 7, '拒绝时保留原行（不写入表单）');

// ---- 9. 应用闸门 ----
ok(mod.sisCanApply({ detail, quantities: { 501: '2' } }), '合法选择可应用');
ok(!mod.sisCanApply({ detail, quantities: { 501: '2' }, readonly: true }), '只读不可应用');
ok(!mod.sisCanApply({ detail, quantities: { 501: '2' }, loading: true }), '加载中不可应用');
ok(!mod.sisCanApply({ detail, quantities: { 501: '9' } }), '超剩余不可应用');
ok(!mod.sisCanApply({ detail: Object.assign({}, detail, { purchaseOrderId: 0 }), quantities: { 501: '2' } }), '未选来源不可应用');

// ---- 10. 安全渲染：转义 + 不可用原因显式可见 ----
const sourceRendered = mod.sisSourceRowHtml({ purchaseOrderId: 21, orderNo: '<b>PO</b>', supplierName: '供应商', currency: 'CNY', lineCount: 1, remainingTotal: 7 });
ok(sourceRendered.includes('&lt;b&gt;PO&lt;/b&gt;'), '来源行转义');
ok(sourceRendered.includes('sisPickSource(21)'), '来源行提供显式选择入口');

const deniedRendered = mod.sisCandidateRowHtml(candidate({ orderNo: '<i>x</i>', available: false, unavailableReason: '单位未知' }));
ok(deniedRendered.includes('&lt;i&gt;x&lt;/i&gt;'), '候选行转义');
ok(deniedRendered.includes('单位未知'), '不可用原因显式可见');
ok(mod.sisCandidateRowHtml(candidate()).includes('可用'), '可用候选显式标注可用');

const errorRendered = mod.sisErrorHtml('network', 'Failed to fetch');
ok(errorRendered.includes('无法连接服务器'), '网络失败显式可见');
ok(errorRendered.includes('role="alert"'), '失败提示是无障碍可见区域');
eq(mod.sisErrorMessage({ message: 'Failed to fetch' }).kind, 'network', '网络失败归类');
eq(mod.sisErrorMessage({ message: '当前账号没有「采购订单」模块授权' }).kind, 'unauthorized', '授权失败归类');

ok(mod.sisSourceButtonHtml().includes('openStockInSourcePicker'), '表单入口调用选择器');
eq(mod.sisInstallFormHook(), false, '未加载 crud.js 时安装钩子安全返回 false');

// ---- 10b. SP 单据编辑页（bill-edit.js）明细口径与显式单据 Id 入口 ----
const billRows = mod.sisBillDetailRows(applied.rows);
eq(billRows.length, 1, 'SP 明细行构造');
eq(billRows[0].ProductId, 501, 'SP 明细行 PascalCase 商品 Id');
eq(billRows[0].Quantity, 2, 'SP 明细行数量');
eq(billRows[0].Unit, 'PCS', 'SP 明细行单位取权威基础单位');
eq(billRows[0].UnitPrice, 0, 'SP 明细行单价留 0（绝不臆造成本）');
eq(billRows[0].Amount, 0, 'SP 明细行金额留 0（绝不臆造成本）');
ok(mod.sisSourceButtonHtml(7).includes('openStockInSourcePicker(7)'), 'SP 单据页入口显式传入单据 Id');
ok(mod.sisSourceButtonHtml().includes('window.__sisCurrentId'), '模块入口使用当前单据 Id');

// ---- 11. 前端接线契约：复用只读接口与声明式表单，不执行任意 SQL，绝不从文本解析 Id ----
const jsSrc = fs.readFileSync(JS_PATH, 'utf8');
const indexSrc = fs.readFileSync(INDEX_PATH, 'utf8');
const modulesSrc = fs.readFileSync(MODULES_PATH, 'utf8');

ok(jsSrc.includes('/source-candidates?'), '复用有界候选接口');
ok(jsSrc.includes('source-candidates/${purchaseOrderId}'), '复用来源详情接口');
ok(jsSrc.includes('async function openStockInSourcePicker'), '导出业务表单入口');
ok(jsSrc.includes('function sisInstallFormHook'), '导出声明式表单接入钩子');
ok(jsSrc.includes('sisApplyToForm'), '导出应用到表单动作');
ok(!/FromSql|ExecuteSql|SqlCommand|select\s+\*/i.test(jsSrc), '不执行任意 SQL');
ok(!/\bparseInt\(|parseFloat\(/.test(jsSrc), '绝不从文本解析 Id / 数量');
ok(!jsSrc.includes("sisSetField('warehouseId'"), '选择器绝不改写仓库字段（保留用户所选仓库）');
ok(!/sisSetField\(\s*'(warehouse|Warehouse)Id'/.test(jsSrc), '绝不写入仓库字段（camelCase / PascalCase）');
ok(jsSrc.includes("'f_' + pascal"), '回填同时覆盖 camelCase 与 PascalCase 字段');
ok(jsSrc.includes('detail-tbody') && jsSrc.includes('addDetailRow'), '支持 SP 单据编辑页明细回填');
ok(!mod.SIS_WRITTEN_FIELDS.includes('warehouseId'), '写入字段清单不含仓库');
ok(indexSrc.includes('/js/stock-in-source-picker.js'), 'index.html 加载选择器脚本');
ok(modulesSrc.includes("selector: 'stock-in-source'"), 'modules-doc2 声明采购入库来源选择入口');
ok(modulesSrc.includes('purchaseOrderId'), 'modules-doc2 声明 purchaseOrderId 字段');
ok(modulesSrc.includes('detailFields: STOCK_IN_DETAIL_FIELDS'), 'modules-doc2 声明入库明细（可回填可收商品行）');
const billEditSrc = fs.readFileSync(BILL_EDIT_PATH, 'utf8');
ok(billEditSrc.includes("BILL_CODE === 'stock-in'"), 'bill-edit 仅为采购入库注入来源选择入口');
ok(billEditSrc.includes('sisSourceButtonHtml'), 'bill-edit 调用来源选择器入口');
ok(billEditSrc.includes('${fieldsHtml}${sourcePickerHtml}'), 'bill-edit 在明细前插入来源选择入口');

console.log('\n✅ ERP-375 采购入库来源选择器 UI 逻辑测试全部通过（' + passed + ' 项断言）。\n');
