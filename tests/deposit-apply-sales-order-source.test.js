'use strict';
/* ERP-390 定金申请单来源销售订单选择器前端逻辑单测（可执行）：
   只接受服务端标记 eligible 的候选、已取消 / 币种不一致候选不可选、只回填权威来源 Id（绝不臆造订单号、不改金额 / 汇率）、
   客户 / 币种变更即失效、陈旧异步响应不回填、重开保留历史链接并显式标注、可显式断开、失败保留表单状态并显式提示，
   以及前端接线契约（复用只读候选 / 存储来源接口、声明式表单入口、加载脚本、不执行任意 SQL、
   绝不用 parseInt / parseFloat 从文本解析 Id、绝不触碰金额 / 汇率）。
   覆盖 create / edit / reopen / customer switch / currency switch / stale response / unlink / denied candidate / error 行为。
   运行：node tests/deposit-apply-sales-order-source.test.js
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
  salesOrderId: 11, orderNo: 'SO-0001', orderDate: '2026-09-20T00:00:00', customerId: 9001,
  customerName: '义乌客户', currency: 'USD', status: 'Approved', eligible: true, ineligibleReason: '',
}, extra || {});

// ---- 1. 状态口径：仅「待提交」可维护来源 ----
ok(mod.fasIsEditableStatus('Pending'), 'Pending 可维护');
ok(mod.fasIsEditableStatus(0), '数字 0（Pending 枚举值）可维护');
ok(!mod.fasIsEditableStatus('Approved'), 'Approved 只读');
ok(!mod.fasIsEditableStatus(2), '数字 2（Approved）只读');
eq(mod.fasNormalizeStatus('已审核'), 'Approved', '中文状态归一化');
eq(mod.fasNormalizeStatus('待提交（草稿）'), 'Pending', '状态文案归一化');
ok(mod.fasReadonlyReason('Approved').includes('只读'), '只读原因显式可见');
eq(mod.fasReadonlyReason('Pending'), '', '待提交无只读原因');

// ---- 2. 单据 Id 闸门与归一化 ----
eq(mod.fasGuardApplyId(0), '', '新建（0）允许选择来源');
eq(mod.fasGuardApplyId(12), '', '已保存单据放行');
ok(mod.fasGuardApplyId(-1) !== '', '负数 Id 拒绝');
ok(mod.fasGuardApplyId('abc') !== '', '非数字 Id 拒绝');
eq(mod.fasIsPositiveId(11), true, '正整数 Id 判定');
eq(mod.fasIsPositiveId(''), false, '空 Id 不是正整数');
eq(mod.fasNormalizeCurrency('  usd '), 'USD', '币种去空白 + 大写');
eq(mod.fasNormalizeCurrency(null), '', '空币种归一化为空');
eq(mod.fasNormalizePage(0), 1, '页码 0 收敛为 1');
eq(mod.fasNormalizePage(-5), 1, '负页码收敛为 1');
eq(mod.fasNormalizePageSize(0), mod.FAS_PAGE_SIZE_DEFAULT, '0 取默认每页条数');
eq(mod.fasNormalizePageSize(9999), mod.FAS_PAGE_SIZE_MAX, '超上限被钳制');
eq(mod.fasNormalizePageSize(15), 15, '合法每页条数原样');
eq(mod.fasNormalizeKeyword('  SO-1  '), 'SO-1', '关键字去首尾空白');
eq(mod.fasNormalizeKeyword('x'.repeat(150)).length, 100, '超长关键字截断');

// ---- 3. 候选可用性：只有 eligible = true 可选 ----
const candidates = [
  candidate(),
  candidate({ salesOrderId: 12, orderNo: 'SO-0002', status: 'Cancelled', eligible: false, ineligibleReason: '销售订单已取消：不能作为新来源' }),
  candidate({ salesOrderId: 13, orderNo: 'SO-0003', currency: 'CNY', eligible: false, ineligibleReason: '销售订单币种与申请单币种不一致' }),
  candidate({ salesOrderId: 0, orderNo: '', eligible: true }),
];
eq(mod.fasAvailableCandidates(candidates).length, 1, '已取消 / 币种不一致 / 无 Id 候选不可选');
eq(mod.fasAvailableCandidates(candidates)[0].salesOrderId, 11, '仅保留可用候选');

// ---- 4. denied candidate：不可用候选绝不产生选择 ----
eq(mod.fasSelectionFromCandidate(candidates[1]), null, '已取消候选不可选择');
eq(mod.fasSelectionFromCandidate(candidates[2]), null, '币种不一致候选不可选择');
eq(mod.fasSelectionFromCandidate(candidate()), { salesOrderId: 11, orderNo: 'SO-0001', customerId: 9001, currency: 'USD' }, '可用候选生成选择');
const deniedRendered = mod.fasCandidateRowHtml(candidates[1]);
ok(deniedRendered.includes('不可选'), '已取消候选显式标注不可选');
ok(deniedRendered.includes('已取消'), '不可用原因显式可见');
ok(mod.fasCandidateRowHtml(candidate()).includes('fasPickCandidate(11)'), '可用候选提供显式选择入口');

// ---- 5. 只回填来源 Id（不臆造订单号 / 不改金额汇率） ----
eq(mod.fasFormFill(mod.fasSelectionFromCandidate(candidate())), { salesOrderId: 11 }, '只回填来源 Id');
eq(mod.fasFormFill(null), { salesOrderId: '' }, '无选择留空');
eq(mod.fasUnlinkFill(), { salesOrderId: '' }, '显式断开留空');

// ---- 6. 客户 / 币种变更即失效 ----
const selectionA = mod.fasSelectionFromCandidate(candidate({ customerId: 9001, currency: 'USD' }));
ok(!mod.fasShouldInvalidateSelection(selectionA, 9001, 'USD'), '客户与币种未变不失效');
ok(mod.fasShouldInvalidateSelection(selectionA, 9002, 'USD'), '客户变更即失效');
ok(mod.fasShouldInvalidateSelection(selectionA, 9001, 'CNY'), '币种变更即失效');
ok(mod.fasShouldInvalidateSelection(selectionA, 0, 'USD'), '客户被清空即失效');
ok(!mod.fasShouldInvalidateSelection(null, 9002, 'CNY'), '无选择无需失效');
ok(mod.fasSelectionMatchesContext(selectionA, 9001, 'usd'), '选择匹配当前客户 + 归一化币种');
ok(!mod.fasSelectionMatchesContext(selectionA, 9001, 'CNY'), '选择不匹配新币种');

// ---- 7. 陈旧异步响应不回填 ----
ok(mod.fasShouldAcceptResponse(5, 5, 9001, 9001, 'USD', 'USD'), '同序号同上下文接受');
ok(!mod.fasShouldAcceptResponse(4, 5, 9001, 9001, 'USD', 'USD'), '旧序号响应丢弃');
ok(!mod.fasShouldAcceptResponse(5, 5, 9001, 9002, 'USD', 'USD'), '旧客户响应丢弃（客户已切换）');
ok(!mod.fasShouldAcceptResponse(5, 5, 9001, 9001, 'CNY', 'USD'), '旧币种响应丢弃（币种已切换）');
ok(!mod.fasShouldAcceptResponse(5, 5, 9001, 0, 'USD', 'USD'), '客户被清空时响应丢弃');

// ---- 8. 重开：保留历史链接并显式标注（含取消 / 不可用历史来源） ----
const reopenedLive = { linked: true, salesOrderId: 11, orderNo: 'SO-0001', status: 'Approved', currency: 'USD', eligibleForNewLink: true, unavailable: false, annotation: '已关联销售订单「SO-0001」' };
const liveHtml = mod.fasStoredSourceHtml(reopenedLive);
ok(liveHtml.includes('SO-0001') && liveHtml.includes('已关联'), '重开显示已关联来源');
ok(liveHtml.includes('仍可作为新来源'), '重开显示仍可作为新来源');

const reopenedCancelled = { linked: true, salesOrderId: 12, orderNo: 'SO-0002', status: 'Cancelled', currency: 'USD', eligibleForNewLink: false, unavailable: false, annotation: '已关联销售订单「SO-0002」：来源销售订单已取消，链接只读保留' };
const cancelledHtml = mod.fasStoredSourceHtml(reopenedCancelled);
ok(cancelledHtml.includes('已取消'), '重开显示来源已取消标注');
ok(cancelledHtml.includes('只读保留'), '重开明确只读保留');

const reopenedGone = { linked: true, salesOrderId: 13, orderNo: '', status: '', currency: '', eligibleForNewLink: false, unavailable: true, annotation: '已关联销售订单 Id 13：来源销售订单不可用（已删除或无法解析），原链接原样保留' };
ok(mod.fasStoredSourceHtml(reopenedGone).includes('不可用'), '重开显示来源不可用标注');

const unlinkedHtml = mod.fasStoredSourceHtml({ linked: false, salesOrderId: null, annotation: '当前未关联来源销售订单' });
ok(unlinkedHtml.includes('未关联'), '重开显示历史未关联语义');

// ---- 9. 安全渲染与错误提示 ----
const sourceRendered = mod.fasCandidateRowHtml(candidate({ orderNo: '<b>SO</b>', customerName: '客户' }));
ok(sourceRendered.includes('&lt;b&gt;SO&lt;/b&gt;'), '候选行转义');
const errorRendered = mod.fasErrorHtml('network', 'Failed to fetch');
ok(errorRendered.includes('无法连接服务器'), '网络失败显式可见');
ok(errorRendered.includes('role="alert"'), '失败提示是无障碍可见区域');
eq(mod.fasErrorMessage({ message: 'Failed to fetch' }).kind, 'network', '网络失败归类');
eq(mod.fasErrorMessage({ message: '当前账号没有「销售订单」模块授权' }).kind, 'unauthorized', '授权失败归类');
ok(mod.fasSourceButtonHtml().includes('openFinanceApplySalesOrderSourcePicker'), '表单入口调用选择器');

// ---- 10. 集成行为：create / edit / reopen / context switch / stale / unlink / error ----
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
let s = simulate({}, { type: 'open', customerId: 9001, currency: 'USD', storedSalesOrderId: null });
eq(s.form.salesOrderId, '', 'create 初始未关联');
s = simulate(s, { type: 'candidates' });
s = simulate(s, { type: 'response', seq: s.requestSeq, customerId: 9001, currency: 'USD', items: [candidate()] });
s = simulate(s, { type: 'pick', candidate: s.candidates[0] });
eq(s.form.salesOrderId, 11, 'create 显式选择回填权威来源 Id');
eq(Object.keys(s.form).length, 1, '只回填来源 Id（不改金额 / 汇率 / 币种）');

// denied candidate：候选不可用 → 不产生 / 不覆盖选择
s = simulate(s, { type: 'pick', candidate: candidate({ salesOrderId: 12, eligible: false }) });
eq(s.form.salesOrderId, 11, 'denied candidate 不覆盖既有选择');

// edit：已保存待提交单据 → 重开回显既有来源
ok(mod.fasIsEditableStatus('Pending'), 'edit 待提交可编辑');
const editable = simulate({}, { type: 'open', customerId: 9001, currency: 'USD', storedSalesOrderId: 11, storedOrderNo: 'SO-0001' });
eq(editable.form.salesOrderId, 11, 'edit 重开回显既有来源 Id');

// reopen：来源已取消仍原样保留 + 显式标注
const reopened = simulate({}, { type: 'open', customerId: 9001, currency: 'USD', storedSalesOrderId: 12, storedOrderNo: 'SO-0002' });
eq(reopened.form.salesOrderId, 12, 'reopen 保留历史来源 Id（即使来源已取消）');
ok(mod.fasStoredSourceHtml(reopenedCancelled).includes('已取消'), 'reopen 标注来源已取消');

// customer switch：切换客户 → 选择失效并清除
const switchedCustomer = simulate(editable, { type: 'switchContext', customerId: 9002, currency: 'USD' });
eq(switchedCustomer.form.salesOrderId, '', 'customer switch 清除旧来源选择');
eq(switchedCustomer.selected, null, 'customer switch 清空已选状态');

// currency switch：切换币种 → 选择失效并清除
const switchedCurrency = simulate(editable, { type: 'switchContext', customerId: 9001, currency: 'CNY' });
eq(switchedCurrency.form.salesOrderId, '', 'currency switch 清除旧来源选择');

// stale response：切客户后旧序号响应不得回填
const beforeStale = simulate(editable, { type: 'switchContext', customerId: 9002, currency: 'USD' });
const stale = simulate(beforeStale, { type: 'response', seq: 1, customerId: 9001, currency: 'USD', items: [candidate()] });
eq(stale.candidates, [], 'stale response 不回填陈旧客户结果');
const freshState = Object.assign({}, beforeStale, { requestSeq: 5 });
const fresh = simulate(freshState, { type: 'response', seq: 5, customerId: 9002, currency: 'USD', items: [candidate({ customerId: 9002 })] });
eq(fresh.candidates.length, 1, 'fresh response 正常回填');

// error：失败保留表单既有输入并显式提示
const errored = simulate(editable, { type: 'candidates' });
const failed = simulate(errored, { type: 'error', seq: errored.requestSeq, err: { message: 'Failed to fetch' } });
eq(failed.form.salesOrderId, 11, 'error 保留表单既有来源输入');
ok(failed.error.length > 0, 'error 显式提示');

// unlink：显式断开
const unlinked = simulate(editable, { type: 'unlink' });
eq(unlinked.form.salesOrderId, '', 'unlink 显式断开来源');

// ---- 11. 前端接线契约：复用只读接口与声明式表单，不执行任意 SQL，绝不从文本解析 Id ----
const jsSrc = fs.readFileSync(JS_PATH, 'utf8');
const indexSrc = fs.readFileSync(INDEX_PATH, 'utf8');
const modulesSrc = fs.readFileSync(MODULES_PATH, 'utf8');

ok(jsSrc.includes('/sales-order-candidates?'), '复用有界候选接口');
ok(jsSrc.includes('/sales-order-source`'), '复用已存储来源只读接口');
ok(jsSrc.includes('async function openFinanceApplySalesOrderSourcePicker'), '导出业务表单入口');
ok(jsSrc.includes('function fasInstallFormHook'), '导出声明式表单接入钩子');
ok(jsSrc.includes('fasApplyToForm'), '导出应用到表单动作');
ok(jsSrc.includes('fasUnlinkSource'), '导出显式断开动作');
ok(jsSrc.includes('fasOnContextChanged'), '导出客户 / 币种变更失效动作');
ok(!/FromSql|ExecuteSql|SqlCommand|select\s+\*/i.test(jsSrc), '不执行任意 SQL');
ok(!/\bparseInt\(|parseFloat\(/.test(jsSrc), '绝不从文本解析 Id / 数量');
ok(!/quantity|amount|exchangeRate/i.test(jsSrc), '绝不触碰金额 / 汇率字段');
ok(indexSrc.includes('/js/finance-apply-sales-order-source.js'), 'index.html 加载选择器脚本');
ok(modulesSrc.includes("selector: 'finance-apply-sales-order-source'"), 'modules-finance 声明定金来源选择入口');

console.log('\n✅ ERP-390 定金申请单来源销售订单选择器 UI 逻辑测试全部通过（' + passed + ' 项断言）。\n');



