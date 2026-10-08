'use strict';
/* ERP-391 货款申请单来源销售订单共享选择器前端逻辑单测（可执行）：
   验证共享选择器按当前模块路由到货款申请单只读接口与文案、只接受服务端标记 eligible 的候选、
   已取消 / 币种不一致候选不可选、只回填权威来源 Id（绝不臆造订单号、不改金额 / 汇率）、
   客户 / 币种变更即失效、陈旧异步响应不回填、重开保留历史链接并显式标注、可显式断开、失败保留表单状态并显式提示，
   以及前端接线契约（货款申请单模块声明共享入口、加载脚本、不执行任意 SQL、绝不用 parseInt / parseFloat 解析 Id）。
   覆盖 create / edit / reopen / customer switch / currency switch / stale response / unlink / denied candidate / error 行为。
   运行：node tests/payment-apply-sales-order-source.test.js
   失败时抛异常并以非零码退出。 */

const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..');
const JS_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'finance-apply-sales-order-source.js');
const INDEX_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'index.html');
const MODULES_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'modules-finance.js');

let passed = 0;
function ok(cond, msg) { assert.ok(cond, msg); passed += 1; }
function eq(actual, expected, msg) { assert.deepStrictEqual(actual, expected, msg); passed += 1; }

const mod = require(JS_PATH);

const candidate = (extra) => Object.assign({
  salesOrderId: 41, orderNo: 'SO-PAY-0001', orderDate: '2026-09-25', customerId: 7001,
  customerName: '货款客户', currency: 'USD', status: 'Approved', eligible: true, ineligibleReason: '',
}, extra || {});

// ---- 1. 共享选择器按模块路由（货款 / 定金 / 未知） ----
eq(mod.fasResolveApplyKind('payment-apply'), 'payment-apply', '货款申请单识别');
eq(mod.fasResolveApplyKind('deposit-apply'), 'deposit-apply', '定金申请单识别');
eq(mod.fasResolveApplyKind('sales-order'), 'deposit-apply', '未知模块退回定金口径（不放宽权限）');
eq(mod.fasResolveApplyKind(''), 'deposit-apply', '空模块退回定金口径');
eq(mod.FAS_APPLY_KINDS['payment-apply'].api, '/api/finance/payment-applies', '货款接口路径');
eq(mod.FAS_APPLY_KINDS['deposit-apply'].api, '/api/finance/deposit-applies', '定金接口路径');

// Node 默认无 CURRENT_MODULE_CODE → 默认定金口径（绝不放宽权限）
eq(mod.fasApplyKind(), 'deposit-apply', '无模块上下文默认定金口径');
eq(mod.fasApiBase(), '/api/finance/deposit-applies', '默认接口为定金');
eq(mod.fasApplyTitle(), '定金申请单', '默认文案为定金');

// 模拟货款模块上下文 → 路由到货款接口（同一份选择器实现，不复制第二份）
global.CURRENT_MODULE_CODE = 'payment-apply';
eq(mod.fasApplyKind(), 'payment-apply', '货款模块上下文生效');
eq(mod.fasApiBase(), '/api/finance/payment-applies', '货款模块路由到货款接口');
eq(mod.fasApplyTitle(), '货款申请单', '货款模块文案');
ok(mod.fasSourceButtonHtml().includes('货款申请单'), '货款模块表单入口文案');
ok(mod.fasGuardApplyId(-1).includes('货款申请单'), '货款模块 Id 闸门文案');
delete global.CURRENT_MODULE_CODE;
eq(mod.fasApiBase(), '/api/finance/deposit-applies', '清除上下文后回到定金口径');

// ---- 2. 状态口径：仅「待提交」可维护来源 ----
ok(mod.fasIsEditableStatus('Pending'), 'Pending 可维护');
ok(mod.fasIsEditableStatus(0), '数字 0（Pending 枚举值）可维护');
ok(!mod.fasIsEditableStatus('Approved'), 'Approved 只读');
ok(!mod.fasIsEditableStatus(2), '数字 2（Approved）只读');
eq(mod.fasNormalizeStatus('已审核'), 'Approved', '中文状态归一化');
ok(mod.fasReadonlyReason('Approved').includes('只读'), '只读原因显式可见');
eq(mod.fasReadonlyReason('Pending'), '', '待提交无只读原因');

// ---- 3. 单据 Id 闸门与归一化 ----
eq(mod.fasGuardApplyId(0), '', '新建（0）允许选择来源');
eq(mod.fasGuardApplyId(12), '', '已保存单据放行');
ok(mod.fasGuardApplyId(-1) !== '', '负数 Id 拒绝');
ok(mod.fasGuardApplyId('abc') !== '', '非数字 Id 拒绝');
eq(mod.fasIsPositiveId(41), true, '正整数 Id 判定');
eq(mod.fasIsPositiveId(''), false, '空 Id 不是正整数');
eq(mod.fasNormalizeCurrency('  usd '), 'USD', '币种去空白 + 大写');
eq(mod.fasNormalizePage(0), 1, '页码 0 收敛为 1');
eq(mod.fasNormalizePageSize(0), mod.FAS_PAGE_SIZE_DEFAULT, '0 取默认每页条数');
eq(mod.fasNormalizePageSize(9999), mod.FAS_PAGE_SIZE_MAX, '超上限被钳制');
eq(mod.fasNormalizeKeyword('  SO-1  '), 'SO-1', '关键字去首尾空白');
eq(mod.fasNormalizeKeyword('x'.repeat(150)).length, 100, '超长关键字截断');

// ---- 4. 候选可用性：只有 eligible = true 可选 ----
const candidates = [
  candidate(),
  candidate({ salesOrderId: 42, orderNo: 'SO-PAY-0002', status: 'Cancelled', eligible: false, ineligibleReason: '销售订单已取消' }),
  candidate({ salesOrderId: 43, orderNo: 'SO-PAY-0003', currency: 'CNY', eligible: false, ineligibleReason: '销售订单币种与申请单币种不一致' }),
  candidate({ salesOrderId: 0, orderNo: '', eligible: true }),
];
eq(mod.fasAvailableCandidates(candidates).length, 1, '已取消 / 币种不一致 / 无 Id 候选不可选');
eq(mod.fasAvailableCandidates(candidates)[0].salesOrderId, 41, '仅保留可用候选');

// ---- 5. denied candidate：不可用候选绝不产生选择 ----
eq(mod.fasSelectionFromCandidate(candidates[1]), null, '已取消候选不可选择');
eq(mod.fasSelectionFromCandidate(candidates[2]), null, '币种不一致候选不可选择');
eq(mod.fasSelectionFromCandidate(candidate()), { salesOrderId: 41, orderNo: 'SO-PAY-0001', customerId: 7001, currency: 'USD' }, '可用候选生成选择');
ok(mod.fasCandidateRowHtml(candidates[1]).includes('不可选'), '已取消候选显式标注不可选');
ok(mod.fasCandidateRowHtml(candidate()).includes('fasPickCandidate(41)'), '可用候选提供显式选择入口');

// ---- 6. 只回填来源 Id（不臆造订单号 / 不改金额汇率） ----
eq(mod.fasFormFill(mod.fasSelectionFromCandidate(candidate())), { salesOrderId: 41 }, '只回填来源 Id');
eq(mod.fasFormFill(null), { salesOrderId: '' }, '无选择留空');
eq(mod.fasUnlinkFill(), { salesOrderId: '' }, '显式断开留空');

// ---- 7. 客户 / 币种变更即失效 ----
const selectionA = mod.fasSelectionFromCandidate(candidate({ customerId: 7001, currency: 'USD' }));
ok(!mod.fasShouldInvalidateSelection(selectionA, 7001, 'USD'), '客户与币种未变不失效');
ok(mod.fasShouldInvalidateSelection(selectionA, 7002, 'USD'), '客户变更即失效');
ok(mod.fasShouldInvalidateSelection(selectionA, 7001, 'CNY'), '币种变更即失效');
ok(!mod.fasShouldInvalidateSelection(null, 7002, 'CNY'), '无选择无需失效');

// ---- 8. 陈旧异步响应不回填 ----
ok(mod.fasShouldAcceptResponse(5, 5, 7001, 7001, 'USD', 'USD'), '同序号同上下文接受');
ok(!mod.fasShouldAcceptResponse(4, 5, 7001, 7001, 'USD', 'USD'), '旧序号响应丢弃');
ok(!mod.fasShouldAcceptResponse(5, 5, 7001, 7002, 'USD', 'USD'), '旧客户响应丢弃（客户已切换）');
ok(!mod.fasShouldAcceptResponse(5, 5, 7001, 7001, 'CNY', 'USD'), '旧币种响应丢弃（币种已切换）');

// ---- 9. 重开：保留历史链接并显式标注（含取消 / 不可用历史来源） ----
const reopenedLive = { linked: true, salesOrderId: 41, orderNo: 'SO-PAY-0001', status: 'Approved', currency: 'USD', eligibleForNewLink: true, unavailable: false, annotation: '已关联销售订单「SO-PAY-0001」' };
const liveHtml = mod.fasStoredSourceHtml(reopenedLive);
ok(liveHtml.includes('SO-PAY-0001') && liveHtml.includes('已关联'), '重开显示已关联来源');
ok(liveHtml.includes('仍可作为新来源'), '重开显示仍可作为新来源');

const reopenedCancelled = { linked: true, salesOrderId: 42, orderNo: 'SO-PAY-0002', status: 'Cancelled', currency: 'USD', eligibleForNewLink: false, unavailable: false, annotation: '已关联销售订单「SO-PAY-0002」：来源销售订单已取消，链接只读保留' };
const cancelledHtml = mod.fasStoredSourceHtml(reopenedCancelled);
ok(cancelledHtml.includes('已取消'), '重开显示来源已取消标注');
ok(cancelledHtml.includes('只读保留'), '重开明确只读保留');

const reopenedGone = { linked: true, salesOrderId: 43, orderNo: '', status: '', currency: '', eligibleForNewLink: false, unavailable: true, annotation: '已关联销售订单 Id 43：来源销售订单不可用（已删除或无法解析），原链接原样保留' };
ok(mod.fasStoredSourceHtml(reopenedGone).includes('不可用'), '重开显示来源不可用标注');

const unlinkedHtml = mod.fasStoredSourceHtml({ linked: false, salesOrderId: null, annotation: '当前未关联来源销售订单' });
ok(unlinkedHtml.includes('未关联'), '重开显示历史未关联语义');

// ---- 10. 安全渲染与错误提示 ----
const sourceRendered = mod.fasCandidateRowHtml(candidate({ orderNo: '<b>SO</b>', customerName: '客户' }));
ok(sourceRendered.includes('&lt;b&gt;SO&lt;/b&gt;'), '候选行转义');
const errorRendered = mod.fasErrorHtml('network', 'Failed to fetch');
ok(errorRendered.includes('无法连接服务器'), '网络失败显式可见');
ok(errorRendered.includes('role="alert"'), '失败提示是无障碍可见区域');
eq(mod.fasErrorMessage({ message: 'Failed to fetch' }).kind, 'network', '网络失败归类');
eq(mod.fasErrorMessage({ message: '当前账号没有「销售订单」模块授权' }).kind, 'unauthorized', '授权失败归类');
ok(mod.fasSourceButtonHtml().includes('openFinanceApplySalesOrderSourcePicker'), '表单入口调用选择器');

// ---- 11. 集成行为：create / edit / reopen / context switch / stale / unlink / error ----
function simulate(state, action) {
  const s = Object.assign({
    requestSeq: 0, customerId: 0, currency: '', candidates: [], selected: null,
    form: { salesOrderId: '' }, error: '',
  }, state);
  if (action.type === 'open') {
    s.customerId = action.customerId;
    s.currency = mod.fasNormalizeCurrency(action.currency);
    s.form.salesOrderId = action.storedSalesOrderId || '';
    s.selected = action.storedSalesOrderId
      ? { salesOrderId: action.storedSalesOrderId, orderNo: action.storedOrderNo, customerId: action.customerId, currency: s.currency }
      : null;
  } else if (action.type === 'candidates') {
    s.requestSeq += 1;
  } else if (action.type === 'response') {
    if (mod.fasShouldAcceptResponse(action.seq, s.requestSeq, action.customerId, s.customerId,
      action.currency, s.currency)) s.candidates = action.items;
  } else if (action.type === 'error') {
    if (action.seq === s.requestSeq) s.error = mod.fasErrorMessage(action.err).message;  // 失败保留表单
  } else if (action.type === 'pick') {
    const sel = mod.fasSelectionFromCandidate(action.candidate);
    if (sel) { s.selected = sel; s.form = mod.fasFormFill(sel); }
  } else if (action.type === 'switchContext') {
    s.requestSeq += 1;
    if (mod.fasShouldInvalidateSelection(s.selected, action.customerId, action.currency)) {
      s.selected = null;
      s.form = mod.fasUnlinkFill();
    }
    s.customerId = action.customerId;
    s.currency = mod.fasNormalizeCurrency(action.currency);
    s.candidates = [];
  } else if (action.type === 'unlink') {
    s.selected = null;
    s.form = mod.fasUnlinkFill();
  }
  return s;
}

// create：新建 + 精确客户候选 + 显式选择
let s = simulate({}, { type: 'open', customerId: 7001, currency: 'USD', storedSalesOrderId: null });
eq(s.form.salesOrderId, '', 'create 初始未关联');
s = simulate(s, { type: 'candidates' });
s = simulate(s, { type: 'response', seq: s.requestSeq, customerId: 7001, currency: 'USD', items: [candidate()] });
s = simulate(s, { type: 'pick', candidate: s.candidates[0] });
eq(s.form.salesOrderId, 41, 'create 显式选择回填权威来源 Id');
eq(Object.keys(s.form).length, 1, '只回填来源 Id（不改金额 / 汇率 / 币种）');

// denied：不可选候选不产生 / 不覆盖选择
s = simulate(s, { type: 'pick', candidate: candidate({ salesOrderId: 42, eligible: false }) });
eq(s.form.salesOrderId, 41, 'denied candidate 不覆盖既有选择');

// edit：已保存待提交单据 → 重开回显既有来源
const editable = simulate({}, { type: 'open', customerId: 7001, currency: 'USD', storedSalesOrderId: 41, storedOrderNo: 'SO-PAY-0001' });
eq(editable.form.salesOrderId, 41, 'edit 重开回显既有来源 Id');

// reopen：来源已取消仍原样保留
const reopened = simulate({}, { type: 'open', customerId: 7001, currency: 'USD', storedSalesOrderId: 42, storedOrderNo: 'SO-PAY-0002' });
eq(reopened.form.salesOrderId, 42, 'reopen 保留历史来源 Id（即使来源已取消）');

// customer switch：切换客户 → 选择失效并清除
const switchedCustomer = simulate(editable, { type: 'switchContext', customerId: 7002, currency: 'USD' });
eq(switchedCustomer.form.salesOrderId, '', 'customer switch 清除旧来源选择');
eq(switchedCustomer.selected, null, 'customer switch 清空已选状态');

// currency switch：切换币种 → 选择失效并清除
const switchedCurrency = simulate(editable, { type: 'switchContext', customerId: 7001, currency: 'CNY' });
eq(switchedCurrency.form.salesOrderId, '', 'currency switch 清除旧来源选择');

// stale response：切客户后旧序号响应不得回填
const beforeStale = simulate(editable, { type: 'switchContext', customerId: 7002, currency: 'USD' });
const stale = simulate(beforeStale, { type: 'response', seq: 1, customerId: 7001, currency: 'USD', items: [candidate()] });
eq(stale.candidates, [], 'stale response 不回填陈旧客户结果');
const freshState = Object.assign({}, beforeStale, { requestSeq: 5 });
const fresh = simulate(freshState, { type: 'response', seq: 5, customerId: 7002, currency: 'USD', items: [candidate({ customerId: 7002 })] });
eq(fresh.candidates.length, 1, 'fresh response 正常回填');

// error：失败保留表单既有输入并显式提示
const errored = simulate(editable, { type: 'candidates' });
const failed = simulate(errored, { type: 'error', seq: errored.requestSeq, err: { message: 'Failed to fetch' } });
eq(failed.form.salesOrderId, 41, 'error 保留表单既有来源输入');
ok(failed.error.length > 0, 'error 显式提示');

// unlink：显式断开
const unlinked = simulate(editable, { type: 'unlink' });
eq(unlinked.form.salesOrderId, '', 'unlink 显式断开来源');

// ---- 12. 前端接线契约：复用只读接口与共享声明式表单，不执行任意 SQL，绝不从文本解析 Id ----
const jsSrc = fs.readFileSync(JS_PATH, 'utf8');
const indexSrc = fs.readFileSync(INDEX_PATH, 'utf8');
const modulesSrc = fs.readFileSync(MODULES_PATH, 'utf8');

ok(jsSrc.includes('/sales-order-candidates?'), '复用有界候选接口');
ok(jsSrc.includes('/sales-order-source`'), '复用已存储来源只读接口');
ok(jsSrc.includes("'/api/finance/payment-applies'"), '共享选择器含货款申请单接口路由');
ok(jsSrc.includes("'/api/finance/deposit-applies'"), '共享选择器保留定金申请单接口路由');
ok(jsSrc.includes('function fasResolveApplyKind'), '导出模块路由纯函数');
ok(jsSrc.includes('async function openFinanceApplySalesOrderSourcePicker'), '导出业务表单入口');
ok(jsSrc.includes('function fasInstallFormHook'), '导出声明式表单接入钩子');
ok(jsSrc.includes('fasApplyToForm'), '导出应用到表单动作');
ok(jsSrc.includes('fasUnlinkSource'), '导出显式断开动作');
ok(jsSrc.includes('fasOnContextChanged'), '导出客户 / 币种变更失效动作');
ok(!/FromSql|ExecuteSql|SqlCommand|select\s+\*/i.test(jsSrc), '不执行任意 SQL');
ok(!/\bparseInt\(|parseFloat\(/.test(jsSrc), '绝不从文本解析 Id / 数量');
ok(!/quantity|amount|exchangeRate/i.test(jsSrc), '绝不触碰金额 / 汇率字段');
ok(indexSrc.includes('/js/finance-apply-sales-order-source.js'), 'index.html 加载共享选择器脚本');

// modules-finance 同时为定金 / 货款申请单声明共享来源选择入口（同一份实现）
const selectorCount = (modulesSrc.match(/selector: 'finance-apply-sales-order-source'/g) || []).length;
ok(selectorCount >= 2, '定金与货款申请单共用同一来源选择入口');
ok(modulesSrc.includes("'payment-apply': {"), '货款申请单模块存在');

console.log('\n✅ ERP-391 货款申请单来源销售订单共享选择器 UI 逻辑测试全部通过（' + passed + ' 项断言）。\n');
