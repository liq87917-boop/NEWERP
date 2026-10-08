'use strict';
/* ERP-393 采购订单归属来源销售订单选择器前端逻辑单测（可执行）：
   只接受服务端标记 eligible 的候选、未审核 / 已取消 / 客户缺失候选不可选、只回填权威归属来源 Id / 单号与归属客户
   （绝不臆造订单号、绝不改币种 / 汇率 / 单价 / 金额 / 明细）、归属客户变更即失效、陈旧异步响应不回填、
   重开保留历史链接并显式标注（不可用时绝不披露范围外客户 / 订单字段）、可显式断开、失败保留表单状态并显式提示，
   以及前端接线契约（复用只读候选 / 存储来源接口、声明式表单入口、加载脚本、不执行任意 SQL、
   绝不用 parseInt / parseFloat 从文本解析 Id）。
   覆盖 create / edit / reopen / customer switch / stale response / unlink / denied candidate / error 行为。
   运行：node tests/purchase-order-sales-order-source.test.js
   失败时抛异常并以非零码退出。 */

const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..');
const JS_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'purchase-order-sales-order-source.js');
const INDEX_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'index.html');
const MODULES_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'modules-doc.js');

let passed = 0;
function ok(cond, msg) { assert.ok(cond, msg); passed += 1; }
function eq(actual, expected, msg) { assert.deepStrictEqual(actual, expected, msg); passed += 1; }

const mod = require(JS_PATH);

const candidate = (extra) => Object.assign({
  salesOrderId: 11, orderNo: 'SO-0001', orderDate: '2026-09-20T00:00:00', customerId: 9001,
  customerName: '义乌客户', currency: 'USD', status: 'Approved', eligible: true, ineligibleReason: '',
}, extra || {});

// ---- 1. 状态口径：仅「待提交」可维护来源 ----
ok(mod.posIsEditableStatus('Pending'), 'Pending 可维护');
ok(mod.posIsEditableStatus(0), '数字 0（Pending 枚举值）可维护');
ok(!mod.posIsEditableStatus('Approved'), 'Approved 只读');
ok(!mod.posIsEditableStatus(2), '数字 2（Approved）只读');
eq(mod.posNormalizeStatus('已审核'), 'Approved', '中文状态归一化');
eq(mod.posNormalizeStatus('待提交（草稿）'), 'Pending', '状态文案归一化');
ok(mod.posReadonlyReason('Approved').includes('只读'), '只读原因显式可见');
eq(mod.posReadonlyReason('Pending'), '', '待提交无只读原因');

// ---- 2. 单据 Id 闸门与归一化 ----
eq(mod.posGuardOrderId(0), '', '新建（0）允许选择来源');
eq(mod.posGuardOrderId(12), '', '已保存单据放行');
ok(mod.posGuardOrderId(-1) !== '', '负数 Id 拒绝');
ok(mod.posGuardOrderId('abc') !== '', '非数字 Id 拒绝');
eq(mod.posIsPositiveId(11), true, '正整数 Id 判定');
eq(mod.posIsPositiveId(''), false, '空 Id 不是正整数');
eq(mod.posCustomerId(' 9001 '), 9001, '客户 Id 解析');
eq(mod.posCustomerId(0), 0, '0 客户不是正整数');
eq(mod.posNormalizePage(0), 1, '页码 0 收敛为 1');
eq(mod.posNormalizePage(-5), 1, '负页码收敛为 1');
eq(mod.posNormalizePageSize(0), mod.POS_PAGE_SIZE_DEFAULT, '0 取默认每页条数');
eq(mod.posNormalizePageSize(9999), mod.POS_PAGE_SIZE_MAX, '超上限被钳制');
eq(mod.posNormalizePageSize(15), 15, '合法每页条数原样');
eq(mod.posNormalizeKeyword('  SO-1  '), 'SO-1', '关键字去首尾空白');
eq(mod.posNormalizeKeyword('x'.repeat(150)).length, 100, '超长关键字截断');

// ---- 3. 候选可用性：只有 eligible = true 可选 ----
const candidates = [
  candidate(),
  candidate({ salesOrderId: 12, orderNo: 'SO-0002', status: 'Cancelled', eligible: false, ineligibleReason: '归属销售订单已取消，不能作为采购备货来源' }),
  candidate({ salesOrderId: 13, orderNo: 'SO-0003', customerId: 0, customerName: '', eligible: false, ineligibleReason: '来源销售订单的归属客户不存在或已删除：不能作为采购备货来源' }),
  candidate({ salesOrderId: 0, orderNo: '', eligible: true }),
];
eq(mod.posAvailableCandidates(candidates).length, 1, '已取消 / 客户缺失 / 无 Id 候选不可选');
eq(mod.posAvailableCandidates(candidates)[0].salesOrderId, 11, '仅保留可用候选');

// ---- 4. denied candidate：不可用候选绝不产生选择 ----
eq(mod.posSelectionFromCandidate(candidates[1]), null, '已取消候选不可选择');
eq(mod.posSelectionFromCandidate(candidates[2]), null, '客户缺失候选不可选择');
eq(mod.posSelectionFromCandidate(candidate()), { salesOrderId: 11, orderNo: 'SO-0001', customerId: 9001, customerName: '义乌客户' }, '可用候选生成选择');
ok(mod.posCandidateRowHtml(candidates[1]).includes('不可选'), '已取消候选显式标注不可选');
ok(mod.posCandidateRowHtml(candidates[1]).includes('已取消'), '不可用原因显式可见');
ok(mod.posCandidateRowHtml(candidate()).includes('posPickCandidate(11)'), '可用候选提供显式选择入口');

// ---- 5. 只回填归属来源与归属客户（绝不臆造订单号 / 不改金额汇率） ----
const fill = mod.posFormFill(mod.posSelectionFromCandidate(candidate()));
eq(fill, { owningSalesOrderId: 11, owningSalesOrderNo: 'SO-0001', owningCustomerId: 9001, owningCustomerName: '义乌客户' }, '只回填权威归属来源 + 归属客户');
eq(Object.keys(fill).sort(), mod.POS_WRITTEN_FIELDS.slice().sort(), '回填字段与服务端声明一致');
['amount', 'totalAmount', 'quantity', 'currency', 'exchangeRate', 'details', 'unitPrice']
  .forEach(k => ok(!(k in fill), `绝不触碰 ${k}`));
eq(mod.posFormFill(null), { owningSalesOrderId: '', owningSalesOrderNo: '' }, '无选择只清空来源链接');
eq(mod.posUnlinkFill(), { owningSalesOrderId: '', owningSalesOrderNo: '' }, '显式断开只清空来源链接');

// ---- 6. 归属客户变更即失效 ----
const selectionA = mod.posSelectionFromCandidate(candidate({ customerId: 9001 }));
ok(!mod.posShouldInvalidateSelection(selectionA, 9001), '同一客户不失效');
ok(mod.posShouldInvalidateSelection(selectionA, 9002), '客户变更即失效');
ok(mod.posShouldInvalidateSelection(selectionA, 0), '客户被清空即失效');
ok(!mod.posShouldInvalidateSelection(null, 9002), '无选择无需失效');
ok(mod.posSelectionMatchesContext(selectionA, 9001), '选择匹配当前客户');
ok(!mod.posSelectionMatchesContext(selectionA, 9002), '选择不匹配新客户');

// ---- 7. 陈旧异步响应不回填 ----
ok(mod.posShouldAcceptResponse(5, 5, 9001, 9001), '同序号同客户接受');
ok(!mod.posShouldAcceptResponse(4, 5, 9001, 9001), '旧序号响应丢弃');
ok(!mod.posShouldAcceptResponse(5, 5, 9001, 9002), '旧客户响应丢弃（客户已切换）');
ok(mod.posShouldAcceptResponse(5, 5, 0, 0), '无客户上下文时同序号接受');

// ---- 8. 重开：保留历史链接并显式标注（含取消 / 不可用 / 范围外历史来源） ----
const reopenedLive = { linked: true, salesOrderId: 11, orderNo: 'SO-0001', customerId: 9001, customerName: '义乌客户', status: 'Approved', eligibleForNewLink: true, unavailable: false, annotation: '已关联销售订单「SO-0001」' };
const liveHtml = mod.posStoredSourceHtml(reopenedLive);
ok(liveHtml.includes('SO-0001') && liveHtml.includes('已关联'), '重开显示已关联来源');
ok(liveHtml.includes('义乌客户'), '重开显示权威归属客户');
ok(liveHtml.includes('仍可作为新来源'), '重开显示仍可作为新来源');

const reopenedCancelled = { linked: true, salesOrderId: 12, orderNo: 'SO-0002', customerId: 9001, customerName: '义乌客户', status: 'Cancelled', eligibleForNewLink: false, unavailable: false, annotation: '已关联销售订单「SO-0002」：归属销售订单已取消，不能作为采购备货来源，链接只读保留' };
const cancelledHtml = mod.posStoredSourceHtml(reopenedCancelled);
ok(cancelledHtml.includes('已取消'), '重开显示来源已取消标注');
ok(cancelledHtml.includes('只读保留'), '重开明确只读保留');

// 不可用（含范围外 foreign）：即使响应里带了订单号 / 客户，也绝不渲染（绝不披露范围外来源字段）
const reopenedForeign = { linked: true, salesOrderId: 13, orderNo: 'SO-FOREIGN', customerId: 9999, customerName: 'Foreign Customer', status: 'Approved', eligibleForNewLink: true, unavailable: true, annotation: '已存储来源销售订单不可用（已删除 / 已取消 / 未审核 / 不在当前账号客户数据范围内），原链接原样保留' };
const foreignHtml = mod.posStoredSourceHtml(reopenedForeign);
ok(foreignHtml.includes('不可用'), '重开显示来源不可用标注');
ok(!foreignHtml.includes('SO-FOREIGN'), '不可用时绝不披露来源订单号');
ok(!foreignHtml.includes('Foreign Customer'), '不可用时绝不披露范围外客户名称');

const unlinkedHtml = mod.posStoredSourceHtml({ linked: false, salesOrderId: null, annotation: '' });
ok(unlinkedHtml.includes('未关联'), '重开显示历史未关联语义');

// ---- 9. 安全渲染与错误提示 ----
const sourceRendered = mod.posCandidateRowHtml(candidate({ orderNo: '<b>SO</b>', customerName: '客户' }));
ok(sourceRendered.includes('&lt;b&gt;SO&lt;/b&gt;'), '候选行转义');
const errorRendered = mod.posErrorHtml('network', 'Failed to fetch');
ok(errorRendered.includes('无法连接服务器'), '网络失败显式可见');
ok(errorRendered.includes('role="alert"'), '失败提示是无障碍可见区域');
eq(mod.posErrorMessage({ message: 'Failed to fetch' }).kind, 'network', '网络失败归类');
eq(mod.posErrorMessage({ message: '当前账号没有「采购订单」模块授权' }).kind, 'unauthorized', '授权失败归类');
ok(mod.posSourceButtonHtml().includes('openPurchaseOrderSalesOrderSourcePicker'), '表单入口调用选择器');

// ---- 10. 集成行为：create / edit / reopen / customer switch / stale / unlink / error ----
function simulate(state, action) {
  const s = Object.assign({
    requestSeq: 0, customerId: 0, candidates: [], selected: null,
    form: { owningSalesOrderId: '', owningSalesOrderNo: '' }, error: '',
  }, state);
  if (action.type === 'open') {
    s.customerId = action.customerId;
    s.form = Object.assign({}, s.form, {
      owningSalesOrderId: action.storedSalesOrderId || '',
      owningSalesOrderNo: action.storedOrderNo || '',
    });
    s.selected = action.storedSalesOrderId
      ? { salesOrderId: action.storedSalesOrderId, orderNo: action.storedOrderNo, customerId: action.customerId, customerName: action.storedCustomerName || '' }
      : null;
  } else if (action.type === 'candidates') {
    s.requestSeq += 1;
  } else if (action.type === 'response') {
    if (mod.posShouldAcceptResponse(action.seq, s.requestSeq, action.customerId, s.customerId)) s.candidates = action.items;
  } else if (action.type === 'error') {
    if (action.seq === s.requestSeq) s.error = mod.posErrorMessage(action.err).message;  // 失败保留表单
  } else if (action.type === 'pick') {
    const sel = mod.posSelectionFromCandidate(action.candidate);
    if (sel) { s.selected = sel; s.form = Object.assign({}, s.form, mod.posFormFill(sel)); }
  } else if (action.type === 'switchContext') {
    s.requestSeq += 1;
    if (mod.posShouldInvalidateSelection(s.selected, action.customerId)) {
      s.selected = null;
      s.form = Object.assign({}, s.form, mod.posUnlinkFill());
    }
    s.customerId = action.customerId;
    s.candidates = [];
  } else if (action.type === 'unlink') {
    s.selected = null;
    s.form = Object.assign({}, s.form, mod.posUnlinkFill());
  }
  return s;
}

// create：新建 + 客户上下文候选 + 显式选择
let s = simulate({}, { type: 'open', customerId: 9001, storedSalesOrderId: null });
eq(s.form.owningSalesOrderId, '', 'create 初始未关联');
s = simulate(s, { type: 'candidates' });
s = simulate(s, { type: 'response', seq: s.requestSeq, customerId: 9001, items: [candidate()] });
s = simulate(s, { type: 'pick', candidate: s.candidates[0] });
eq(s.form.owningSalesOrderId, 11, 'create 显式选择回填权威来源 Id');
eq(s.form.owningSalesOrderNo, 'SO-0001', 'create 回填权威来源单号');

// denied candidate：候选不可用 → 不产生 / 不覆盖选择
s = simulate(s, { type: 'pick', candidate: candidate({ salesOrderId: 12, eligible: false }) });
eq(s.form.owningSalesOrderId, 11, 'denied candidate 不覆盖既有选择');

// edit / reopen：已保存待提交单据 → 重开回显既有来源（即使来源已取消也原样保留）
ok(mod.posIsEditableStatus('Pending'), 'edit 待提交可编辑');
const editable = simulate({}, { type: 'open', customerId: 9001, storedSalesOrderId: 11, storedOrderNo: 'SO-0001' });
eq(editable.form.owningSalesOrderId, 11, 'edit 重开回显既有来源 Id');
const reopened = simulate({}, { type: 'open', customerId: 9001, storedSalesOrderId: 12, storedOrderNo: 'SO-0002' });
eq(reopened.form.owningSalesOrderId, 12, 'reopen 保留历史来源 Id（即使来源已取消）');

// customer switch：切换 / 清空客户 → 选择失效并清除
const switchedCustomer = simulate(editable, { type: 'switchContext', customerId: 9002 });
eq(switchedCustomer.form.owningSalesOrderId, '', 'customer switch 清除旧来源选择');
eq(switchedCustomer.selected, null, 'customer switch 清空已选状态');
const clearedCustomer = simulate(editable, { type: 'switchContext', customerId: 0 });
eq(clearedCustomer.form.owningSalesOrderId, '', 'customer 清空同样令选择失效');

// stale response：切客户后旧序号响应不得回填
const beforeStale = simulate(editable, { type: 'switchContext', customerId: 9002 });
const stale = simulate(beforeStale, { type: 'response', seq: 1, customerId: 9001, items: [candidate()] });
eq(stale.candidates, [], 'stale response 不回填陈旧客户结果');
const freshState = Object.assign({}, beforeStale, { requestSeq: 5 });
const fresh = simulate(freshState, { type: 'response', seq: 5, customerId: 9002, items: [candidate({ customerId: 9002 })] });
eq(fresh.candidates.length, 1, 'fresh response 正常回填');

// error：失败保留表单既有输入并显式提示
const errored = simulate(editable, { type: 'candidates' });
const failed = simulate(errored, { type: 'error', seq: errored.requestSeq, err: { message: 'Failed to fetch' } });
eq(failed.form.owningSalesOrderId, 11, 'error 保留表单既有来源输入');
ok(failed.error.length > 0, 'error 显式提示');

// unlink：显式断开（保留用户已填归属客户）
const withCustomer = Object.assign({}, editable, {
  form: Object.assign({}, editable.form, { owningCustomerId: 9001, owningCustomerName: '义乌客户' }),
});
const unlinked = simulate(withCustomer, { type: 'unlink' });
eq(unlinked.form.owningSalesOrderId, '', 'unlink 显式断开来源');
eq(unlinked.form.owningSalesOrderNo, '', 'unlink 清空来源单号');
eq(unlinked.form.owningCustomerId, 9001, 'unlink 保留用户已填归属客户');

// ---- 11. 前端接线契约：复用只读接口与声明式表单，不执行任意 SQL，绝不从文本解析 Id ----
const jsSrc = fs.readFileSync(JS_PATH, 'utf8');
const indexSrc = fs.readFileSync(INDEX_PATH, 'utf8');
const modulesSrc = fs.readFileSync(MODULES_PATH, 'utf8');

ok(jsSrc.includes('/sales-order-source-candidates?'), '复用有界候选接口');
ok(jsSrc.includes('/sales-order-source`'), '复用已存储来源只读接口');
ok(jsSrc.includes('async function openPurchaseOrderSalesOrderSourcePicker'), '导出业务表单入口');
ok(jsSrc.includes('function posInstallFormHook'), '导出声明式表单接入钩子');
ok(jsSrc.includes('posApplyToForm'), '导出应用到表单动作');
ok(jsSrc.includes('posUnlinkSource'), '导出显式断开动作');
ok(jsSrc.includes('posOnContextChanged'), '导出客户变更失效动作');
ok(!/FromSql|ExecuteSql|SqlCommand|select\s+\*/i.test(jsSrc), '不执行任意 SQL');
ok(!/\bparseInt\(|parseFloat\(/.test(jsSrc), '绝不从文本解析 Id / 数量');
ok(!/quantity|amount|exchangeRate/i.test(jsSrc), '绝不触碰金额 / 汇率字段');
ok(indexSrc.includes('/js/purchase-order-sales-order-source.js'), 'index.html 加载选择器脚本');
ok(modulesSrc.includes("selector: 'purchase-order-sales-order-source'"), 'modules-doc 声明采购订单来源选择入口');

console.log('\n✅ ERP-393 采购订单来源销售订单选择器 UI 逻辑测试全部通过（' + passed + ' 项断言）。\n');
