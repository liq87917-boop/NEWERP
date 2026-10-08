'use strict';
/* ERP-376 销售出库来源选择器前端逻辑单测（可执行）：
   只接受服务端标记 available 的候选、重复 / 歧义明细与已发货满额不可选、数量必须为正数且不超剩余可发数量、
   回填明细只使用权威商品 / 规格 / 基础单位（本页不录入单价 / 价格，绝不臆造价格）、回填权威客户、
   **保留用户所选仓库**、应用闸门（只读 / 加载中 / **正在应用** 一律不可应用，防止重复点击）、安全转义，
   以及前端接线契约（复用只读候选 / 详情接口、声明式表单入口、加载脚本、不执行任意 SQL、绝不从文本解析 Id）。
   运行：node tests/automation/stock-out-source-picker.test.js
   失败时抛异常并以非零码退出。 */

const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const JS_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'stock-out-source-picker.js');
const INDEX_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'index.html');
const MODULES_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'modules-doc2.js');
const BILL_EDIT_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'bill-edit.js');

let passed = 0;
function ok(cond, msg) { assert.ok(cond, msg); passed += 1; }
function eq(actual, expected, msg) { assert.deepStrictEqual(actual, expected, msg); passed += 1; }

const mod = require(JS_PATH);

const candidate = (extra) => Object.assign({
  salesOrderId: 31, orderNo: 'SO-0001', customerId: 9001, customerName: '义乌客户',
  productId: 601, productName: '商品601', spec: '规格B', baseUnit: 'PCS',
  authorizedBaseQuantity: 10, shippedBaseQuantity: 3, remainingBaseQuantity: 7,
  available: true, unavailableReason: '',
}, extra || {});

// ---- 1. 状态口径：仅「待提交」可维护来源 ----
ok(mod.sosIsEditableStatus('Pending'), 'Pending 可维护');
ok(mod.sosIsEditableStatus(0), '数字 0（Pending 枚举值）可维护');
ok(!mod.sosIsEditableStatus('Approved'), 'Approved 只读');
ok(!mod.sosIsEditableStatus(2), '数字 2（Approved）只读');
eq(mod.sosNormalizeStatus('已审核'), 'Approved', '中文状态归一化');
eq(mod.sosNormalizeStatus('待提交（草稿）'), 'Pending', '状态文案归一化');
ok(mod.sosReadonlyReason('Approved').includes('只读'), '只读原因显式可见');
eq(mod.sosReadonlyReason('Pending'), '', '待提交无只读原因');

// ---- 2. 单据 Id 闸门与条数钳制 ----
eq(mod.sosGuardSavedId(0), '', '新建（0）允许选择来源');
eq(mod.sosGuardSavedId(12), '', '已保存单据放行');
ok(mod.sosGuardSavedId(-1) !== '', '负数 Id 拒绝');
ok(mod.sosGuardSavedId('abc') !== '', '非数字 Id 拒绝');
eq(mod.sosNormalizeTake(0), mod.SOS_TAKE_DEFAULT, '0 取默认条数');
eq(mod.sosNormalizeTake(-5), mod.SOS_TAKE_DEFAULT, '负数取默认条数');
eq(mod.sosNormalizeTake(9999), mod.SOS_TAKE_MAX, '超上限被钳制');

// ---- 3. 候选可用性：只有 available = true 可选；来源按订单 Id 去重 ----
const candidates = [
  candidate(),
  candidate({ salesOrderId: 32, orderNo: 'SO-0002', remainingBaseQuantity: 0, available: false, unavailableReason: '剩余可发数量为 0' }),
  candidate({ salesOrderId: 33, orderNo: 'SO-0003', productId: 602, baseUnit: '', available: false, unavailableReason: '单位未知' }),
  candidate({ salesOrderId: 34, orderNo: 'SO-0004', available: false, unavailableReason: '来源销售订单同商品存在多条明细，授权数量不唯一（不可用）' }),
];
eq(mod.sosAvailableCandidates(candidates).length, 1, '零容量 / 单位未知 / 重复歧义不可选');
eq(mod.sosAvailableCandidates(candidates)[0].salesOrderId, 31, '仅保留可用候选');

const options = mod.sosSourceOptions(candidates.concat([candidate({ productId: 603, remainingBaseQuantity: 2 })]));
eq(options.length, 1, '按来源订单 Id 去重，仅保留有可发行的来源');
eq(options[0].lineCount, 2, '统计可发行数');
eq(options[0].remainingTotal, 9, '合计剩余可发数量');
eq(options[0].customerName, '义乌客户', '来源汇总带权威客户');

// ---- 4. 候选展示文案：订单号 / 客户 / 商品 / 规格 / 基础单位 / 授权数量 / 已发货 / 剩余可发 ----
const label = mod.sosCandidateLabel(candidate());
ok(label.includes('SO-0001') && label.includes('义乌客户') && label.includes('商品601')
  && label.includes('规格B') && label.includes('PCS') && label.includes('授权数量 10')
  && label.includes('已发货 3') && label.includes('剩余可发 7'), '候选文案含权威字段与数量');

// ---- 5. 回填表头：权威来源 Id / 单号 / 客户；不含仓库 ----
const detail = { salesOrderId: 31, orderNo: 'SO-0001', customerId: 9001, customerName: '义乌客户', available: true, unavailableReason: '', lines: [candidate()] };
const header = mod.sosHeaderFill(detail);
eq(header.salesOrderId, 31, '回填权威来源 Id');
eq(header.orderNo, 'SO-0001', '回填权威来源单号');
eq(header.customerId, 9001, '回填权威客户 Id');
eq(header.customerName, '义乌客户', '回填权威客户名称');
eq(Object.prototype.hasOwnProperty.call(header, 'warehouseId'), false, '表头回填不含仓库（保留用户所选）');
eq(mod.sosHeaderFill(null).salesOrderId, 0, '未选来源时来源 Id 为 0（历史未链接）');

// ---- 6. 数量校验：正数部分数量且不超过剩余可发 ----
ok(!mod.sosValidateQuantity('', 7).ok, '空数量拒绝');
ok(!mod.sosValidateQuantity('0', 7).ok, '零数量拒绝');
ok(!mod.sosValidateQuantity('-1', 7).ok, '负数拒绝');
ok(!mod.sosValidateQuantity('abc', 7).ok, '非数字拒绝');
ok(!mod.sosValidateQuantity('8', 7).ok, '超过剩余可发数量拒绝');
eq(mod.sosValidateQuantity('2', 7).value, 2, '正数部分数量通过');
ok(mod.sosValidateQuantity('7', 7).ok, '等于剩余可发数量通过');
ok(mod.sosValidateQuantity('7.0000000005', 7).ok, '极小舍入尾差在客户端容差内通过');

// ---- 7. 选定来源 → 明细行：权威商品 / 规格 / 基础单位；绝不臆造价格；无可用行则为空 ----
const rows = mod.sosBuildDetailRows(detail);
eq(rows.length, 1, '只保留可用行');
eq(rows[0].productId, 601, '权威商品 Id');
eq(rows[0].unit, 'PCS', '权威基础单位');
eq(rows[0].quantity, 7, '默认数量 = 剩余可发数量');
eq(rows[0].weight, 0, '毛重不臆造');
eq(rows[0].volume, 0, '体积不臆造');
ok(String(rows[0].remark).includes('SO-0001'), '明细备注带来源订单号');
eq(mod.sosBuildDetailRows({ salesOrderId: 31, lines: candidates.slice(1) }).length, 0, '全部不可用的来源没有明细行');

const billRows = mod.sosBillDetailRows(rows);
eq(billRows[0].ProductId, 601, 'SP 明细行 PascalCase 商品 Id');
eq(billRows[0].Unit, 'PCS', 'SP 明细行单位取权威基础单位');
eq(billRows[0].UnitPrice, 0, 'SP 明细行单价留 0（绝不臆造价格）');
eq(billRows[0].Amount, 0, 'SP 明细行金额留 0（绝不臆造价格）');

// ---- 8. 录入数量写回：任一行不合法即整体拒绝 ----
const applied = mod.sosApplyQuantities(rows, { 601: '2' });
ok(applied.ok, '合法数量写回');
eq(applied.rows[0].quantity, 2, '写回数量');
const rejected = mod.sosApplyQuantities(rows, { 601: '99' });
ok(!rejected.ok && rejected.reason.includes('超过该商品剩余可发数量'), '超量整体拒绝并给出原因');
eq(rejected.rows, rows, '拒绝时保留原行（不写入表单）');

// ---- 9. 应用闸门：只读 / 加载中 / 正在应用 / 未选来源 / 数量非法一律不可应用（防止重复点击） ----
ok(mod.sosCanApply({ detail, quantities: { 601: '2' } }), '待提交 + 合法数量可应用');
ok(!mod.sosCanApply({ detail, quantities: { 601: '2' }, readonly: true }), '只读不可应用');
ok(!mod.sosCanApply({ detail, quantities: { 601: '2' }, loading: true }), '加载中不可应用');
ok(!mod.sosCanApply({ detail, quantities: { 601: '2' }, applying: true }), '正在应用时不可再次应用（防止重复点击）');
ok(!mod.sosCanApply({ detail: null, quantities: {} }), '未选来源不可应用');
ok(!mod.sosCanApply({ detail: Object.assign({}, detail, { salesOrderId: 0 }), quantities: { 601: '2' } }), '来源 Id 为 0 不可应用');
ok(!mod.sosCanApply({ detail, quantities: { 601: '0' } }), '数量非法不可应用');

// ---- 10. 安全渲染：转义 + 不可用原因显式可见 ----
const sourceRendered = mod.sosSourceRowHtml({ salesOrderId: 31, orderNo: '<b>SO</b>', customerName: '客户', lineCount: 1, remainingTotal: 7 });
ok(sourceRendered.includes('&lt;b&gt;SO&lt;/b&gt;'), '来源行转义');
ok(sourceRendered.includes('sosPickSource(31)'), '来源行提供显式选择入口');

const deniedRendered = mod.sosCandidateRowHtml(candidate({ orderNo: '<i>x</i>', available: false, unavailableReason: '单位未知' }));
ok(deniedRendered.includes('&lt;i&gt;x&lt;/i&gt;'), '候选行转义');
ok(deniedRendered.includes('单位未知'), '不可用原因显式可见');
ok(mod.sosCandidateRowHtml(candidate()).includes('可用'), '可用候选显式标注可用');

const errorRendered = mod.sosErrorHtml('network', 'Failed to fetch');
ok(errorRendered.includes('无法连接服务器'), '网络失败显式可见');
ok(errorRendered.includes('role="alert"'), '失败提示是无障碍可见区域');
eq(mod.sosErrorMessage({ message: 'Failed to fetch' }).kind, 'network', '网络失败归类');
eq(mod.sosErrorMessage({ message: '当前账号没有「销售订单」模块授权' }).kind, 'unauthorized', '授权失败归类');

ok(mod.sosSourceButtonHtml().includes('openStockOutSourcePicker'), '表单入口调用选择器');
ok(mod.sosSourceButtonHtml(7).includes('openStockOutSourcePicker(7)'), 'SP 单据页入口显式传入单据 Id');
ok(mod.sosSourceButtonHtml().includes('历史未链接'), '入口显式说明未选择 = 历史未链接');
eq(mod.sosInstallFormHook(), false, '未加载 crud.js 时安装钩子安全返回 false');

// ---- 11. 前端接线契约：复用只读接口与声明式表单，不执行任意 SQL，绝不从文本解析 Id ----
const jsSrc = fs.readFileSync(JS_PATH, 'utf8');
const indexSrc = fs.readFileSync(INDEX_PATH, 'utf8');
const modulesSrc = fs.readFileSync(MODULES_PATH, 'utf8');
const billEditSrc = fs.readFileSync(BILL_EDIT_PATH, 'utf8');

ok(jsSrc.includes('/source-candidates?'), '复用有界候选接口');
ok(jsSrc.includes('source-candidates/${salesOrderId}'), '复用来源详情接口');
ok(jsSrc.includes('async function openStockOutSourcePicker'), '导出业务表单入口');
ok(jsSrc.includes('function sosInstallFormHook'), '导出声明式表单接入钩子');
ok(jsSrc.includes('sosApplyToForm'), '导出应用到表单动作');
ok(jsSrc.includes('SOS.applying'), '应用期间置闸门（防止重复点击）');
ok(!/FromSql|ExecuteSql|SqlCommand|select\s+\*/i.test(jsSrc), '不执行任意 SQL');
ok(!/\bparseInt\(|parseFloat\(/.test(jsSrc), '绝不从文本解析 Id / 数量');
ok(!/sosSetField\(\s*'(warehouse|Warehouse)Id'/.test(jsSrc), '绝不写入仓库字段（camelCase / PascalCase）');
ok(!mod.SOS_WRITTEN_FIELDS.includes('warehouseId'), '写入字段清单不含仓库');
ok(jsSrc.includes("'f_' + pascal"), '回填同时覆盖 camelCase 与 PascalCase 字段');
ok(jsSrc.includes('detail-tbody') && jsSrc.includes('addDetailRow'), '支持 SP 单据编辑页明细回填');
ok(indexSrc.includes('/js/stock-out-source-picker.js'), 'index.html 加载选择器脚本');
ok(modulesSrc.includes("selector: 'stock-out-source'"), 'modules-doc2 声明销售出库来源选择入口');
ok(modulesSrc.includes('salesOrderId'), 'modules-doc2 声明 salesOrderId 字段');
ok(modulesSrc.includes('detailFields: STOCK_OUT_DETAIL_FIELDS'), 'modules-doc2 声明出库明细（可回填可发商品行）');
ok(billEditSrc.includes("BILL_CODE === 'stock-out'"), 'bill-edit 仅为销售出库注入来源选择入口');
ok(billEditSrc.includes('sosSourceButtonHtml'), 'bill-edit 调用来源选择器入口');
ok(billEditSrc.includes('${fieldsHtml}${sourcePickerHtml}'), 'bill-edit 在明细前插入来源选择入口');

console.log('\n✅ ERP-376 销售出库来源选择器 UI 逻辑测试全部通过（' + passed + ' 项断言）。\n');
