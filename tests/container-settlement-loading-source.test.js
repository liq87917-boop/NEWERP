'use strict';
/* ERP-392 装柜结算单来源装柜清单选择器前端逻辑单测（可执行）：
   只接受服务端标记 eligible 的候选、共享柜越范围 / 已取消候选不可选、只回填权威来源 Id（绝不臆造柜号 / 清单号、
   不改结算总额 / 费用）、客户变更即失效、陈旧异步响应不回填、重开保留历史链接并显式标注、可显式断开、
   失败保留表单状态并显式提示，以及前端接线契约（复用只读候选 / 存储来源接口、声明式表单入口、加载脚本、
   不执行任意 SQL、绝不用 parseInt / parseFloat 从文本解析 Id、绝不触碰结算总额 / 费用字段）。
   覆盖 create / edit / reopen / customer switch / stale response / unlink / denied candidate / error 行为。
   运行：node tests/container-settlement-loading-source.test.js
   失败时抛异常并以非零码退出。 */

const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..');
const JS_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'container-settlement-loading-source.js');
const INDEX_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'index.html');
const MODULES_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'modules-finance.js');

let passed = 0;
function ok(cond, msg) { assert.ok(cond, msg); passed += 1; }
function eq(actual, expected, msg) { assert.deepStrictEqual(actual, expected, msg); passed += 1; }

const mod = require(JS_PATH);

const candidate = (extra) => Object.assign({
  loadingListId: 21, loadingListNo: 'CL-0001', loadingDate: '2026-09-18T00:00:00', containerNo: 'TCNU-0001',
  customerId: 9001, customerName: '义乌客户', status: 'Pending', eligible: true, ineligibleReason: '',
  activeParticipantCount: 2, legacySingleCustomer: false, preLoadingId: null, upstreamBookingCustomerId: null,
}, extra || {});

// ---- 1. 状态口径：仅「待提交」可维护来源 ----
ok(mod.cslsIsEditableStatus('Pending'), 'Pending 可维护');
ok(mod.cslsIsEditableStatus(0), '数字 0（Pending 枚举值）可维护');
ok(!mod.cslsIsEditableStatus('Approved'), 'Approved 只读');
ok(!mod.cslsIsEditableStatus(2), '数字 2（Approved）只读');
eq(mod.cslsNormalizeStatus('已审核'), 'Approved', '中文状态归一化');
eq(mod.cslsNormalizeStatus('待提交（草稿）'), 'Pending', '状态文案归一化');
ok(mod.cslsReadonlyReason('Approved').includes('只读'), '只读原因显式可见');
eq(mod.cslsReadonlyReason('Pending'), '', '待提交无只读原因');

// ---- 2. 单据 Id 闸门与归一化 ----
eq(mod.cslsGuardSettlementId(0), '', '新建（0）允许选择来源');
eq(mod.cslsGuardSettlementId(12), '', '已保存单据放行');
ok(mod.cslsGuardSettlementId(-1) !== '', '负数 Id 拒绝');
ok(mod.cslsGuardSettlementId('abc') !== '', '非数字 Id 拒绝');
eq(mod.cslsIsPositiveId(11), true, '正整数 Id 判定');
eq(mod.cslsIsPositiveId(''), false, '空 Id 不是正整数');
eq(mod.cslsNormalizePage(0), 1, '页码 0 收敛为 1');
eq(mod.cslsNormalizePage(-5), 1, '负页码收敛为 1');
eq(mod.cslsNormalizePageSize(0), mod.CSLS_PAGE_SIZE_DEFAULT, '0 取默认每页条数');
eq(mod.cslsNormalizePageSize(9999), mod.CSLS_PAGE_SIZE_MAX, '超上限被钳制');
eq(mod.cslsNormalizePageSize(15), 15, '合法每页条数原样');
eq(mod.cslsNormalizeKeyword('  CL-1  '), 'CL-1', '关键字去首尾空白');
eq(mod.cslsNormalizeKeyword('x'.repeat(150)).length, 100, '超长关键字截断');

// ---- 3. 候选可用性：只有 eligible = true 可选 ----
const candidates = [
  candidate(),
  candidate({ loadingListId: 22, loadingListNo: 'CL-0002', status: 'Cancelled', eligible: false, ineligibleReason: '装柜清单已取消：不能作为新来源' }),
  candidate({ loadingListId: 23, loadingListNo: 'CL-0003', eligible: false, ineligibleReason: '共享柜包含范围外客户：整张不可选' }),
  candidate({ loadingListId: 0, loadingListNo: '', eligible: true }),
];
eq(mod.cslsAvailableCandidates(candidates).length, 1, '已取消 / 越范围 / 无 Id 候选不可选');
eq(mod.cslsAvailableCandidates(candidates)[0].loadingListId, 21, '仅保留可用候选');

// ---- 4. denied candidate：不可用候选绝不产生选择 ----
eq(mod.cslsSelectionFromCandidate(candidates[1]), null, '已取消候选不可选择');
eq(mod.cslsSelectionFromCandidate(candidates[2]), null, '越范围候选不可选择');
eq(mod.cslsSelectionFromCandidate(candidate()), { loadingListId: 21, loadingListNo: 'CL-0001', customerId: 9001 }, '可用候选生成选择');
const deniedRendered = mod.cslsCandidateRowHtml(candidates[1]);
ok(deniedRendered.includes('不可选'), '已取消候选显式标注不可选');
ok(deniedRendered.includes('已取消'), '不可用原因显式可见');
ok(mod.cslsCandidateRowHtml(candidate()).includes('cslsPickCandidate(21)'), '可用候选提供显式选择入口');

// ---- 5. 只回填来源 Id（不臆造柜号 / 清单号，不改结算总额 / 费用） ----
eq(mod.cslsFormFill(mod.cslsSelectionFromCandidate(candidate())), { loadingListId: 21 }, '只回填来源 Id');
eq(mod.cslsFormFill(null), { loadingListId: '' }, '无选择留空');
eq(mod.cslsUnlinkFill(), { loadingListId: '' }, '显式断开留空');

// ---- 6. 客户变更即失效 ----
const selectionA = mod.cslsSelectionFromCandidate(candidate({ customerId: 9001 }));
ok(!mod.cslsShouldInvalidateSelection(selectionA, 9001), '客户未变不失效');
ok(mod.cslsShouldInvalidateSelection(selectionA, 9002), '客户变更即失效');
ok(mod.cslsShouldInvalidateSelection(selectionA, 0), '客户被清空即失效');
ok(!mod.cslsShouldInvalidateSelection(null, 9002), '无选择无需失效');
ok(mod.cslsSelectionMatchesContext(selectionA, 9001), '选择匹配当前客户');
ok(!mod.cslsSelectionMatchesContext(selectionA, 9002), '选择不匹配新客户');

// ---- 7. 陈旧异步响应不回填 ----
ok(mod.cslsShouldAcceptResponse(5, 5, 9001, 9001), '同序号同上下文接受');
ok(!mod.cslsShouldAcceptResponse(4, 5, 9001, 9001), '旧序号响应丢弃');
ok(!mod.cslsShouldAcceptResponse(5, 5, 9001, 9002), '旧客户响应丢弃（客户已切换）');
ok(!mod.cslsShouldAcceptResponse(5, 5, 9001, 0), '客户被清空时响应丢弃');

// ---- 8. 重开：保留历史链接并显式标注（含取消 / 不可用历史来源） ----
const reopenedLive = { linked: true, loadingListId: 21, loadingListNo: 'CL-0001', status: 'Pending', eligibleForNewLink: true, unavailable: false, annotation: '已关联来源装柜清单' };
const liveHtml = mod.cslsStoredSourceHtml(reopenedLive);
ok(liveHtml.includes('CL-0001') && liveHtml.includes('已关联'), '重开显示已关联来源');
ok(liveHtml.includes('仍可作为新来源'), '重开显示仍可作为新来源');

const reopenedCancelled = { linked: true, loadingListId: 22, loadingListNo: 'CL-0002', status: 'Cancelled', eligibleForNewLink: false, unavailable: false, cancelled: true, annotation: '已关联来源装柜清单：来源装柜清单已取消，原链接只读保留' };
const cancelledHtml = mod.cslsStoredSourceHtml(reopenedCancelled);
ok(cancelledHtml.includes('已取消'), '重开显示来源已取消标注');
ok(cancelledHtml.includes('只读保留'), '重开明确只读保留');

const reopenedGone = { linked: true, loadingListId: 23, loadingListNo: '', status: '', eligibleForNewLink: false, unavailable: true, annotation: '已关联来源装柜清单 Id 23：来源装柜清单不可用（已删除或无法解析），原链接原样保留' };
ok(mod.cslsStoredSourceHtml(reopenedGone).includes('不可用'), '重开显示来源不可用标注');

const unlinkedHtml = mod.cslsStoredSourceHtml({ linked: false, loadingListId: null, annotation: '当前未关联来源装柜清单' });
ok(unlinkedHtml.includes('未关联'), '重开显示历史未关联语义');

// ---- 9. 安全渲染与错误提示 ----
const sourceRendered = mod.cslsCandidateRowHtml(candidate({ loadingListNo: '<b>CL</b>', customerName: '客户' }));
ok(sourceRendered.includes('&lt;b&gt;CL&lt;/b&gt;'), '候选行转义');
const errorRendered = mod.cslsErrorHtml('network', 'Failed to fetch');
ok(errorRendered.includes('无法连接服务器'), '网络失败显式可见');
ok(errorRendered.includes('role="alert"'), '失败提示是无障碍可见区域');
eq(mod.cslsErrorMessage({ message: 'Failed to fetch' }).kind, 'network', '网络失败归类');
eq(mod.cslsErrorMessage({ message: '当前账号没有「装柜清单」模块授权' }).kind, 'unauthorized', '授权失败归类');
ok(mod.cslsSourceButtonHtml().includes('openContainerSettlementLoadingSourcePicker'), '表单入口调用选择器');


// ---- 10. 集成行为：create / edit / reopen / context switch / stale / unlink / error ----
function simulate(state, action) {
  const s = Object.assign({
    requestSeq: 0, customerId: 0, candidates: [], selected: null,
    form: { loadingListId: '' }, error: '',
  }, state);
  if (action.type === 'open') {
    s.customerId = action.customerId;
    s.form.loadingListId = action.storedLoadingListId || '';
    s.selected = action.storedLoadingListId
      ? { loadingListId: action.storedLoadingListId, loadingListNo: action.storedLoadingListNo, customerId: action.customerId }
      : null;
  } else if (action.type === 'candidates') {
    s.requestSeq += 1;
  } else if (action.type === 'response') {
    if (mod.cslsShouldAcceptResponse(action.seq, s.requestSeq, action.customerId, s.customerId)) s.candidates = action.items;
  } else if (action.type === 'error') {
    if (action.seq === s.requestSeq) s.error = mod.cslsErrorMessage(action.err).message;  // 失败保留表单
  } else if (action.type === 'pick') {
    const sel = mod.cslsSelectionFromCandidate(action.candidate);
    if (sel) { s.selected = sel; s.form = mod.cslsFormFill(sel); }
  } else if (action.type === 'switchContext') {
    s.requestSeq += 1;
    if (mod.cslsShouldInvalidateSelection(s.selected, action.customerId)) {
      s.selected = null;
      s.form = mod.cslsUnlinkFill();
    }
    s.customerId = action.customerId;
    s.candidates = [];
  } else if (action.type === 'unlink') {
    s.selected = null;
    s.form = mod.cslsUnlinkFill();
  }
  return s;
}

// create：新建 + 精确客户候选 + 显式选择
let s = simulate({}, { type: 'open', customerId: 9001, storedLoadingListId: null });
eq(s.form.loadingListId, '', 'create 初始未关联');
s = simulate(s, { type: 'candidates' });
s = simulate(s, { type: 'response', seq: s.requestSeq, customerId: 9001, items: [candidate()] });
s = simulate(s, { type: 'pick', candidate: s.candidates[0] });
eq(s.form.loadingListId, 21, 'create 显式选择回填权威来源 Id');
eq(Object.keys(s.form).length, 1, '只回填来源 Id（不改结算总额 / 费用）');

// denied candidate：候选不可用 → 不产生 / 不覆盖选择
s = simulate(s, { type: 'pick', candidate: candidate({ loadingListId: 22, eligible: false }) });
eq(s.form.loadingListId, 21, 'denied candidate 不覆盖既有选择');

// edit：已保存待提交单据 → 重开回显既有来源
ok(mod.cslsIsEditableStatus('Pending'), 'edit 待提交可编辑');
const editable = simulate({}, { type: 'open', customerId: 9001, storedLoadingListId: 21, storedLoadingListNo: 'CL-0001' });
eq(editable.form.loadingListId, 21, 'edit 重开回显既有来源 Id');

// reopen：来源已取消仍原样保留 + 显式标注
const reopened = simulate({}, { type: 'open', customerId: 9001, storedLoadingListId: 22, storedLoadingListNo: 'CL-0002' });
eq(reopened.form.loadingListId, 22, 'reopen 保留历史来源 Id（即使来源已取消）');
ok(mod.cslsStoredSourceHtml(reopenedCancelled).includes('已取消'), 'reopen 标注来源已取消');

// customer switch：切换客户 → 选择失效并清除
const switchedCustomer = simulate(editable, { type: 'switchContext', customerId: 9002 });
eq(switchedCustomer.form.loadingListId, '', 'customer switch 清除旧来源选择');
eq(switchedCustomer.selected, null, 'customer switch 清空已选状态');

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
eq(failed.form.loadingListId, 21, 'error 保留表单既有来源输入');
ok(failed.error.length > 0, 'error 显式提示');

// unlink：显式断开
const unlinked = simulate(editable, { type: 'unlink' });
eq(unlinked.form.loadingListId, '', 'unlink 显式断开来源');


// ---- 11. 前端接线契约：复用只读接口与声明式表单，不执行任意 SQL，绝不从文本解析 Id ----
const jsSrc = fs.readFileSync(JS_PATH, 'utf8');
const indexSrc = fs.readFileSync(INDEX_PATH, 'utf8');
const modulesSrc = fs.readFileSync(MODULES_PATH, 'utf8');

ok(jsSrc.includes('/loading-list-candidates?'), '复用有界候选接口');
ok(jsSrc.includes('/loading-list-source`'), '复用已存储来源只读接口');
ok(jsSrc.includes('async function openContainerSettlementLoadingSourcePicker'), '导出业务表单入口');
ok(jsSrc.includes('function cslsInstallFormHook'), '导出声明式表单接入钩子');
ok(jsSrc.includes('cslsApplyToForm'), '导出应用到表单动作');
ok(jsSrc.includes('cslsUnlinkSource'), '导出显式断开动作');
ok(jsSrc.includes('cslsOnContextChanged'), '导出客户变更失效动作');
ok(!/FromSql|ExecuteSql|SqlCommand|select\s+\*/i.test(jsSrc), '不执行任意 SQL');
ok(!/\bparseInt\(|parseFloat\(/.test(jsSrc), '绝不从文本解析 Id');
ok(!/amount|cost/i.test(jsSrc), '绝不触碰结算总额 / 费用字段');
ok(indexSrc.includes('/js/container-settlement-loading-source.js'), 'index.html 加载选择器脚本');
ok(modulesSrc.includes("selector: 'container-settlement-loading-source'"), 'modules-finance 声明装柜来源选择入口');

console.log('\n✅ ERP-392 装柜结算单来源装柜清单选择器 UI 逻辑测试全部通过（' + passed + ' 项断言）。\n');

