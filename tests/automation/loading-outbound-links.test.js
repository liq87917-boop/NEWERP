'use strict';
/* ERP-373 装柜清单「出运证据」显式链接工作台前端逻辑单测（可执行）：
   只接受显式正整数选择（空 = 清除）、未保存单据不得登记链接、历史未链接 / 来源不可用都是显式事实、
   变更集只包含真实变化且必须来自当前服务端行集、陈旧选择必须重新确认、双击 / 重复提交被闸门阻断、
   安全转义，以及前端接线契约（复用既有只读候选接口与链接指派接口，不执行任意 SQL）。
   运行：node tests/automation/loading-outbound-links.test.js
   失败时抛异常并以非零码退出。 */

const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const JS_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'loading-outbound-links.js');

let passed = 0;
function ok(cond, msg) { assert.ok(cond, msg); passed += 1; }
function eq(actual, expected, msg) { assert.deepStrictEqual(actual, expected, msg); passed += 1; }

const mod = require(JS_PATH);

const line = (detailId, productId, sourceId, extra) => Object.assign({
  loadingDetailId: detailId, productId, productName: `商品${productId}`,
  quantity: 6, sourceStockOutDetailId: sourceId,
}, extra || {});

// ---- 1. 状态只读口径：仅「待提交」可维护 ----
ok(mod.lolIsEditableStatus('Pending'), 'Pending 可维护');
ok(mod.lolIsEditableStatus(0), '数字 0（Pending 枚举值）可维护');
ok(!mod.lolIsEditableStatus('Approved'), 'Approved 只读');
ok(!mod.lolIsEditableStatus(2), '数字 2（Approved）只读');
ok(!mod.lolIsEditableStatus('Cancelled'), 'Cancelled 只读');
ok(!mod.lolIsEditableStatus('Submitted'), 'Submitted 只读');
eq(mod.lolNormalizeStatus('已审核'), 'Approved', '中文状态归一化');
ok(mod.lolReadonlyReason('Approved').includes('只读'), '只读原因显式可见');
eq(mod.lolReadonlyReason('Pending'), '', '待提交无只读原因');

// ---- 2. 未保存单据：绝不臆造服务端行 Id ----
ok(mod.lolGuardSavedId(0) !== '', '新建未保存单据被拒绝');
ok(mod.lolGuardSavedId(-1) !== '', '负数 Id 被拒绝');
ok(mod.lolGuardSavedId('abc') !== '', '非数字 Id 被拒绝');
eq(mod.lolGuardSavedId(12), '', '已保存单据放行');
ok(mod.LOL_UNSAVED_TEXT.includes('先保存'), '拒绝文案要求先保存');

// ---- 3. 草稿初值：原样回显服务端持久化链接 / 显式未链接 ----
eq(mod.lolLineDraftValue(line(1, 9, 55)), '55', '已链接回显为字符串 Id');
eq(mod.lolLineDraftValue(line(1, 9, null)), '', '历史 null = 显式未链接');
eq(mod.lolLineDraftValue(line(1, 9, 0)), '', '非正 id = 显式未链接');
eq(mod.lolInitialDraft([line(1, 9, 55), line(2, 9, null)]), { 1: '55', 2: '' }, '草稿初值与服务端一致');

// ---- 4. 选择归一化：只接受正整数或清空，绝不解文本为 Id ----
eq(mod.lolNormalizeSelection('12'), { ok: true, value: '12' }, '正整数接受');
eq(mod.lolNormalizeSelection(''), { ok: true, value: '' }, '空 = 清除');
eq(mod.lolNormalizeSelection(null), { ok: true, value: '' }, 'null = 清除');
eq(mod.lolNormalizeSelection('出库A-12').ok, false, '文本拒绝（绝不从文本解析）');
eq(mod.lolNormalizeSelection('12abc').ok, false, '混合文本拒绝');
eq(mod.lolNormalizeSelection('-5').ok, false, '负数拒绝');
eq(mod.lolNormalizeSelection('0').ok, false, '0 拒绝（0 不是合法来源明细 Id）');
eq(mod.lolNormalizeSelection('1.5').ok, false, '小数拒绝');

// ---- 5. 变更集：只含真实变化，必须来自当前服务端行集 ----
const lines = [line(1, 9, 55), line(2, 9, null)];
eq(mod.lolBuildAssignments({ 1: '55', 2: '' }, lines), [], '与服务端一致 = 无变更');
eq(mod.lolBuildAssignments({ 1: '77', 2: '' }, lines),
  [{ loadingDetailId: 1, sourceStockOutDetailId: 77 }], '新链接进入变更集');
eq(mod.lolBuildAssignments({ 1: '55', 2: '88' }, lines),
  [{ loadingDetailId: 2, sourceStockOutDetailId: 88 }], '另一行新链接');
eq(mod.lolBuildAssignments({ 1: '', 2: '' }, lines),
  [{ loadingDetailId: 1, sourceStockOutDetailId: null }], '清空 = 显式未链接（null）');
eq(mod.lolBuildAssignments({ 999: '77' }, lines), [], '不在服务端行集的（陈旧 / 伪造）行被忽略');
eq(mod.lolBuildAssignments({ 1: '出库A' }, lines), [], '非法输入被忽略（绝不进入请求）');
ok(mod.lolIsDirty({ 1: '77' }, lines), '有变更 = dirty');
ok(!mod.lolIsDirty({ 1: '55' }, lines), '无变更 = 不 dirty');

// ---- 6. 陈旧选择：重新加载（token 变化）后必须重新确认 ----
eq(mod.lolStaleSelections([{ loadingDetailId: 1 }], { 1: 3 }, 3), [], '同一行集 token = 非陈旧');
eq(mod.lolStaleSelections([{ loadingDetailId: 1 }], { 1: 2 }, 3).length, 1, '行集 token 变化 = 陈旧');
eq(mod.lolStaleSelections([{ loadingDetailId: 1 }], {}, 3).length, 1, '缺少 token = 陈旧（必须重新确认）');

// ---- 7. 重新加载：剔除已被删除 / 替换的陈旧行，保留仍存在行的选择 ----
const pruned = mod.lolPruneStaleDraft({ 1: '77', 9: '88' }, lines);
eq(pruned.draft, { 1: '77' }, '仍存在的行保留未提交选择');
eq(pruned.staleLineIds, ['9'], '已不存在的行标记为陈旧');

// ---- 8. 证据文案：未链接 / 已链接 / 来源不可用，服务端权威文案优先 ----
ok(mod.lolLineEvidenceText(line(1, 9, null)).includes('未链接'), 'null 显示未链接');
ok(mod.lolLineEvidenceText(line(1, 9, 55, { sourceAvailable: true })) === mod.LOL_LINKED_TEXT, '有效链接显示已链接');
ok(mod.lolLineEvidenceText(line(1, 9, 55, { sourceAvailable: false })).includes('来源不可用'), '不可用来源显式暴露');
ok(mod.lolLineEvidenceText(line(1, 9, 55, { sourceAvailabilityText: '服务端权威文案' })) === '服务端权威文案',
  '服务端权威文案优先');

// ---- 9. 提交闸门：未保存 / 只读 / 加载中 / 提交中 / 无变更都不允许 ----
const state = { loadingListId: 7, readonly: false, loading: false, submitting: false, draft: { 1: '77' }, lines };
ok(mod.lolCanSubmit(state), '正常可提交');
ok(!mod.lolCanSubmit(Object.assign({}, state, { loadingListId: 0 })), '未保存不可提交');
ok(!mod.lolCanSubmit(Object.assign({}, state, { readonly: true })), '只读不可提交');
ok(!mod.lolCanSubmit(Object.assign({}, state, { submitting: true })), '提交中不可提交（阻断双击）');
ok(!mod.lolCanSubmit(Object.assign({}, state, { loading: true })), '加载中不可提交');
ok(!mod.lolCanSubmit(Object.assign({}, state, { draft: { 1: '55' } })), '无变更不可提交');

// ---- 10. 候选证据文案与商品匹配（只做展示分组，绝不用来推断来源） ----
const candidate = {
  stockOutDetailId: 55, stockOutId: 3, stockOutNo: 'SO-1', salesOrderNo: 'XS-1', customerName: '义乌客户',
  productId: 9, productName: '商品9', spec: '规格A', unit: 'PCS',
  sourceBaseQuantity: 10, effectiveReturnedBaseQuantity: 2, remainingBaseQuantity: 4,
};
const label = mod.lolCandidateLabel(candidate);
ok(label.includes('SO-1') && label.includes('XS-1') && label.includes('义乌客户') && label.includes('商品9')
  && label.includes('规格A') && label.includes('PCS') && label.includes('已生效退货 2') && label.includes('4'),
  '候选展示出库单 / 订单 / 客户 / 商品 / 规格 / 基础单位 / 已生效退货 / 剩余');
ok(mod.lolCandidateMatchesLine(candidate, line(1, 9, null)), '同商品候选匹配');
ok(!mod.lolCandidateMatchesLine(candidate, line(1, 8, null)), '不同商品候选不匹配');

// ---- 11. 安全渲染：转义 + 未链接 / 不可用 / 不在候选内的持久化链接都显式可见 ----
const rendered = mod.lolLineRowHtml(
  line(1, 9, 55, { productName: '<b>危险</b>', sourceAvailable: false }),
  { 1: '55' }, []);
ok(rendered.includes('&lt;b&gt;危险&lt;/b&gt;'), '商品名被转义');
ok(!rendered.includes('<b>危险</b>'), '未转义内容不出现');
ok(rendered.includes('来源不可用'), '不可用证据显式标注');
ok(rendered.includes('不在当前候选内'), '当前链接不在候选内时保留并标注（绝不静默清除）');
ok(rendered.includes('value="55"'), '持久化链接仍是可选项');

const unlinkedRendered = mod.lolLineRowHtml(line(2, 9, null), {}, [candidate]);
ok(unlinkedRendered.includes('未链接'), '未链接行显示未链接');
ok(unlinkedRendered.includes('未链接（清除该行出运证据链接）'), '提供显式清除选项');
ok(unlinkedRendered.includes('value="55"'), '候选作为可选项渲染');

const candidateRendered = mod.lolCandidateRowHtml({ stockOutId: 3, productName: '<i>x</i>', remainingBaseQuantity: 4 });
ok(candidateRendered.includes('&lt;i&gt;x&lt;/i&gt;'), '候选行转义');

const errorRendered = mod.lolErrorHtml('network', 'Failed to fetch');
ok(errorRendered.includes('无法连接服务器'), '网络失败显式可见');
ok(errorRendered.includes('role="alert"'), '失败提示是无障碍可见区域');

// ---- 12. 前端接线契约：复用既有接口，不执行任意 SQL ----
ok(typeof mod.lolBuildAssignments === 'function', '导出变更集构造');
ok(typeof mod.lolGuardSavedId === 'function', '导出未保存闸门');
const jsSrc = fs.readFileSync(JS_PATH, 'utf8');
ok(jsSrc.includes('/stock-out-candidates?'), '复用 ERP-366 只读候选接口');
ok(jsSrc.includes('/stock-out-links'), '复用 ERP-366 链接指派接口');
ok(jsSrc.includes('async function openLoadingOutboundLinks'), '导出业务界面行操作入口');
ok(jsSrc.includes('LOL.submitting') && jsSrc.includes('LOL.selectionTokens'), '提交闸门与陈旧选择保护存在');
ok(jsSrc.includes('LOL.error = lolErrorMessage(err)'), '失败只写错误、不清空草稿输入');
ok(!/FromSql|ExecuteSql|SqlCommand|select\s+\*/i.test(jsSrc), '不执行任意 SQL');
ok(!/\bparseInt\(|parseFloat\(/.test(jsSrc), '绝不从文本解析 Id（只接受显式数字选择）');

console.log('\n✅ ERP-373 装柜清单出运证据显式链接工作台 UI 逻辑测试全部通过（' + passed + ' 项断言）。\n');

