'use strict';
/* ERP-396 单证明细行「单证 / 对话框代际 + 编辑身份」绑定前端逻辑单测（可执行）：
   - 每个读取 / 保存 / 删除都绑定捕获到的单证 Id、对话框代际与编辑身份；切换单证、关闭、重开或更新的
     重新读取都会让旧响应失效（既不覆盖当前数据、也不清空输入、也不触发错误刷新、也不提示陈旧成功）；
   - 保存 / 删除的负载 Id 一律在请求前从当前已加载文档捕获，异步等待后不再读取全局状态；
   - 杜绝重复提交（单飞闸门），失败保留未提交输入，成功后只刷新仍然激活的那张单证；
   - 数量与可选箱数 / 净重 / 毛重语义不变（留空即 null，绝不臆造 0），行金额一律服务端计算、
     前端既不提交金额也不提交币种。
   覆盖 A→B 切换、乱序重新读取、关闭 / 重开同一 Id、保存进行中改指编辑、确认期间切换、失败变更、重复点击。
   运行：node tests/trade-document-item-context.test.js
   失败时抛异常并以非零码退出。 */

const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..');
const JS_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'trade-doc-items.js');
const INDEX_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'index.html');
const MODULES_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'modules.js');

let passed = 0;
function ok(cond, msg) { assert.ok(cond, msg); passed += 1; }
function eq(actual, expected, msg) { assert.deepStrictEqual(actual, expected, msg); passed += 1; }

/* ==================== 可延迟裁决的 mock（api / toast / DOM） ==================== */
function deferred() {
  let resolve, reject;
  const promise = new Promise((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

/* 极简 DOM：innerHTML 替换会重建表单控件（与真实 DOM 一致，输入被清空），便于验证草稿回填 */
function createFakeDom() {
  const elements = new Map();
  let html = '';
  let renders = 0;
  const modal = { style: {} };
  Object.defineProperty(modal, 'innerHTML', {
    get() { return html; },
    set(value) {
      html = String(value); renders += 1;
      elements.clear();
      const re = /id="([^"]+)"/g; let m;
      while ((m = re.exec(html))) if (!elements.has(m[1])) elements.set(m[1], { id: m[1], value: '', style: {} });
    }
  });
  return {
    modal,
    document: { getElementById: (id) => (id === 'modal' ? modal : (elements.get(id) || null)) },
    set(id, value) { if (!elements.has(id)) elements.set(id, { id, value: '', style: {} }); elements.get(id).value = value; },
    get(id) { const el = elements.get(id); return el ? el.value : undefined; },
    renders: () => renders
  };
}

const apiCalls = [];
const toasts = [];
const dom = createFakeDom();

global.document = dom.document;
global.api = (url, method = 'GET', body = null) => {
  const d = deferred();
  apiCalls.push({ url, method, body: body === undefined ? null : body, d });
  return d.promise;
};
global.toast = (msg, type = 'success') => { toasts.push({ msg: String(msg), type }); };
global.escapeHtml = (v) => String(v === null || v === undefined ? '' : v);
global.fmtMoney = (v) => (v === null || v === undefined ? '' : Number(v).toFixed(2));

const mod = require(JS_PATH);

/* ==================== 测试脚手架 ==================== */
function reset() {
  apiCalls.length = 0;
  toasts.length = 0;
  global.confirm = () => true;
  mod.tdiOnClose();
}
const lastApi = () => apiCalls[apiCalls.length - 1];
const callsOf = (method) => apiCalls.filter(c => c.method === method);
const reloadCalls = () => apiCalls.filter(c => c.method === 'GET');
const hasToast = (text, type) => toasts.some(t => t.msg.includes(text) && (type === undefined || t.type === type));
async function tick() { for (let i = 0; i < 5; i++) await new Promise(r => setTimeout(r, 0)); }

function docData(docNo, items, extra) {
  return Object.assign({
    docNo, docType: '商业发票', status: '待制作', editable: true, docTypeSupported: true,
    currency: 'USD', amountDecimals: 2, headerAmountRecorded: true, headerAmount: 500,
    lineCount: items.length, take: 200, truncated: false, amountMismatch: false,
    editabilityText: '', docTypeText: '', amountMismatchText: '', rule: '', boundary: '',
    lineAmountTotal: items.reduce((sum, x) => sum + (x.lineAmount || 0), 0), items
  }, extra || {});
}

function item(id, lineNo, quantity, unitPrice, extra) {
  return Object.assign({
    id, lineNo, productId: 0, productCode: 'P-' + lineNo, productNameCn: '商品' + lineNo,
    productNameEn: '', spec: '', quantity, unit: 'PCS', unitPrice,
    lineAmount: Number((quantity * unitPrice).toFixed(2)),
    packageCount: null, netWeight: null, grossWeight: null, remark: ''
  }, extra || {});
}

(async () => {
  /* ---- 1. A→B 切换：A 的迟到响应不得覆盖 B、不得渲染、不得错误刷新 ---- */
  reset();
  const openA = mod.manageTradeDocumentItems(101);
  eq(apiCalls.length, 1, 'A 打开即发出一次读取');
  eq(lastApi().url, '/api/trade/documents/101/items', 'A 读取 URL 精确绑定单证 101');
  const openB = mod.manageTradeDocumentItems(202);
  eq(apiCalls.length, 2, 'B 打开即发出一次读取');
  eq(lastApi().url, '/api/trade/documents/202/items', 'B 读取 URL 精确绑定单证 202');

  apiCalls[1].d.resolve(docData('DOC-B', [item(22, 1, 5, 2)]));
  await tick();
  eq(mod.tdiSnapshot().docId, 202, '当前单证仍是 B');
  eq(mod.tdiSnapshot().dataDocNo, 'DOC-B', 'B 响应正常落地');
  const rendersAfterB = dom.renders();
  apiCalls[0].d.resolve(docData('DOC-A', [item(11, 1, 9, 2)]));
  await tick();
  eq(mod.tdiSnapshot().dataDocNo, 'DOC-B', 'A 的迟到响应被丢弃（不覆盖当前数据）');
  eq(mod.tdiSnapshot().itemIds, [22], '当前行 Id 仍是 B 的行');
  eq(dom.renders(), rendersAfterB, 'A 的迟到响应不触发渲染');
  ok(dom.modal.innerHTML.includes('DOC-B'), '对话框仍是 B 的内容');
  ok(!dom.modal.innerHTML.includes('DOC-A'), '对话框中不出现旧单证 A 的内容');
  await openA; await openB;

  /* ---- 2. 乱序重新读取：同一单证内只有最新一次读取可以落地 ---- */
  reset();
  const openOutOfOrder = mod.manageTradeDocumentItems(303);
  apiCalls[0].d.resolve(docData('DOC-303', [item(31, 1, 1, 1)]));
  await tick();
  eq(mod.tdiSnapshot().dataDocNo, 'DOC-303', '首次读取正常落地');
  const firstReload = mod.tdiReload();
  const secondReload = mod.tdiReload();
  eq(reloadCalls().length, 3, '两次重新读取各发一次请求');
  apiCalls[2].d.resolve(docData('FRESH', [item(31, 1, 7, 2)]));
  await tick();
  apiCalls[1].d.resolve(docData('STALE', [item(31, 1, 1, 1)]));
  await tick();
  eq(mod.tdiSnapshot().dataDocNo, 'FRESH', '乱序重新读取：只有最新一次读取可以落地');
  eq(mod.tdiSnapshot().itemIds, [31], '行 Id 保持最新读取结果');
  ok(!dom.modal.innerHTML.includes('STALE'), '旧读取不触发渲染');
  await openOutOfOrder; await firstReload; await secondReload;

  /* ---- 3. 关闭 / 重开同一 Id：关闭后旧响应不得落地或重新打开；重开使用新代际 ---- */
  reset();
  const openClose = mod.manageTradeDocumentItems(404);
  const staleLoad = lastApi();
  mod.tdiOnClose();
  eq(mod.tdiSnapshot().open, false, '关闭后对话框标记为关闭');
  eq(mod.tdiSnapshot().docId, 0, '关闭清空当前单证');
  staleLoad.d.resolve(docData('DOC-404-STALE', [item(41, 1, 3, 2)]));
  await tick();
  eq(mod.tdiSnapshot().dataDocNo, '', '关闭后旧响应不得落地');
  eq(mod.tdiSnapshot().open, false, '关闭后旧响应不得重新打开对话框');
  ok(!dom.modal.innerHTML.includes('DOC-404-STALE'), '关闭后旧响应不得渲染');

  const genBefore = mod.tdiSnapshot().gen;
  const reopen = mod.manageTradeDocumentItems(404);
  eq(lastApi().url, '/api/trade/documents/404/items', '重开同一 Id 仍按该单证读取');
  lastApi().d.resolve(docData('DOC-404', [item(41, 1, 3, 2)]));
  await tick();
  eq(mod.tdiSnapshot().dataDocNo, 'DOC-404', '重开后的新响应正常落地');
  ok(mod.tdiSnapshot().gen > genBefore, '重开使用新的对话框代际');
  ok(dom.modal.innerHTML.includes('DOC-404'), '重开后对话框渲染当前单证');
  await openClose; await reopen;


  /* ---- 4. 保存进行中改指另一编辑目标：不得丢输入、不得清掉新编辑身份 ---- */
  reset();
  const openEditWhileSave = mod.manageTradeDocumentItems(505);
  apiCalls[0].d.resolve(docData('DOC-505', [item(51, 1, 2, 3), item(52, 2, 4, 5)]));
  await tick();
  mod.tdiEditItem(51);
  eq(mod.tdiSnapshot().editingId, 51, '进入修改绑定行 51');
  dom.set('f_Quantity', '9');
  mod.tdiSaveForm();
  eq(apiCalls.length, 2, '保存发出一次请求');
  eq(lastApi().method, 'PUT', '修改既有行走 PUT');
  eq(lastApi().url, '/api/trade/documents/items/51', 'URL 精确绑定当前编辑行 Id');
  eq(lastApi().body.quantity, 9, '提交数量来自表单');
  eq(mod.tdiSnapshot().pendingKind, 'save', '提交期间标记单飞闸门');

  mod.tdiEditItem(52);
  dom.set('f_Quantity', '77');
  eq(mod.tdiSnapshot().editingId, 52, '在途期间可改指另一编辑目标');
  apiCalls[1].d.resolve({ id: 51 });
  await tick();
  ok(hasToast('已保存'), '当前代际保存成功仍提示（不是陈旧成功）');
  eq(mod.tdiSnapshot().editingId, 52, '已保存行不会清掉另一行的编辑身份');
  eq(lastApi().url, '/api/trade/documents/505/items', '成功后只刷新仍激活的单证 505');
  lastApi().d.resolve(docData('DOC-505', [item(51, 1, 9, 3), item(52, 2, 4, 5)]));
  await tick();
  eq(dom.get('f_Quantity'), '77', '刷新渲染不得丢失另一行尚未提交的输入');
  eq(mod.tdiSnapshot().pendingKind, '', '提交结束后释放单飞闸门');
  await openEditWhileSave;

  /* ---- 5. 保存进行中切换单证：陈旧成功不得提示、不得清编辑、不得刷新旧单证 ---- */
  reset();
  const openSwitchDuringSave = mod.manageTradeDocumentItems(606);
  apiCalls[0].d.resolve(docData('DOC-606', [item(61, 1, 2, 3)]));
  await tick();
  mod.tdiEditItem(61);
  dom.set('f_Quantity', '8');
  mod.tdiSaveForm();
  const saveCall = lastApi();
  eq(saveCall.url, '/api/trade/documents/items/61', '保存绑定单证 606 的行 61');
  mod.manageTradeDocumentItems(707);
  toasts.length = 0;
  saveCall.d.resolve({ id: 61 });
  await tick();
  ok(!hasToast('已保存'), '切换单证后陈旧保存成功不得提示成功');
  eq(callsOf('PUT').length, 1, '陈旧成功不产生额外刷新或重复提交');
  eq(reloadCalls().length, 2, '只保留 A→B 两次读取（陈旧成功不触发旧单证刷新）');
  eq(mod.tdiSnapshot().docId, 707, '当前单证仍是切换后的 707');
  eq(mod.tdiSnapshot().pendingKind, '', '陈旧成功同样释放单飞闸门');
  await openSwitchDuringSave;

  /* ---- 6. 确认期间切换单证：绝不再按旧上下文发删除请求、不得提示陈旧成功 ---- */
  reset();
  const openConfirmSwitch = mod.manageTradeDocumentItems(606);
  apiCalls[0].d.resolve(docData('DOC-606', [item(61, 1, 2, 3)]));
  await tick();
  const deletesBefore = callsOf('DELETE').length;
  global.confirm = () => { mod.manageTradeDocumentItems(808); return true; };
  mod.tdiDeleteItem(61);
  await tick();
  eq(callsOf('DELETE').length, deletesBefore, '确认期间切换单证后不得再按旧上下文发删除请求');
  ok(!hasToast('已删除'), '不得出现陈旧删除成功提示');
  eq(mod.tdiSnapshot().docId, 808, '当前单证是确认期间切换后的 808');
  await openConfirmSwitch;

  /* ---- 7. 失败的变更：显式提示、保留编辑身份与未提交输入、不刷新 ---- */
  reset();
  const openFail = mod.manageTradeDocumentItems(909);
  apiCalls[0].d.resolve(docData('DOC-909', [item(91, 1, 2, 3)]));
  await tick();
  mod.tdiEditItem(91);
  dom.set('f_Quantity', '12');
  dom.set('f_NetWeight', '');
  mod.tdiSaveForm();
  eq(callsOf('PUT').length, 1, '失败场景也只发一次保存请求');
  const rendersBeforeFailure = dom.renders();
  apiCalls[1].d.reject(new Error('单证已提交客户：明细行只读'));
  await tick();
  ok(hasToast('只读', 'error'), '失败显式提示服务端原因');
  eq(mod.tdiSnapshot().editingId, 91, '失败保留编辑身份');
  eq(dom.get('f_Quantity'), '12', '失败保留未提交的数量输入');
  eq(dom.renders(), rendersBeforeFailure, '失败不重渲染（不清空输入）');
  eq(apiCalls.length, 2, '失败不触发刷新请求');
  eq(mod.tdiSnapshot().pendingKind, '', '失败后释放单飞闸门');
  await openFail;

  /* ---- 8. 重复点击：只提交一次，且数量 / 可选重量 / 金额口径不变 ---- */
  reset();
  const openDuplicate = mod.manageTradeDocumentItems(1010);
  apiCalls[0].d.resolve(docData('DOC-1010', []));
  await tick();
  mod.tdiCancelForm();
  dom.set('f_Quantity', '3');
  dom.set('f_PackageCount', '');
  dom.set('f_NetWeight', '');
  dom.set('f_GrossWeight', '');
  mod.tdiSaveForm();
  mod.tdiSaveForm();
  eq(callsOf('POST').length, 1, '重复点击只产生一次提交请求');
  eq(lastApi().url, '/api/trade/documents/1010/items', '新增行 POST 绑定当前单证 1010');
  eq(lastApi().method, 'POST', '新增行使用 POST');
  eq(lastApi().body.quantity, 3, '数量由表单提交');
  eq(lastApi().body.packageCount, null, '箱数留空即 null（绝不臆造为 0）');
  eq(lastApi().body.netWeight, null, '净重留空即 null');
  eq(lastApi().body.grossWeight, null, '毛重留空即 null');
  eq(lastApi().body.unitPrice, 0, '单价留空按 0 提交（金额仍由服务端计算）');
  ok(!('lineAmount' in lastApi().body), '提交体不含行金额（金额由服务端计算）');
  ok(!('amount' in lastApi().body), '提交体不含金额');
  ok(!('currency' in lastApi().body), '提交体不含币种');
  ok(hasToast('正在提交', 'error'), '重复点击显式提示正在提交');
  lastApi().d.resolve({ id: 92 });
  await tick();
  ok(hasToast('已保存'), '去重后仍正常完成一次保存');
  eq(lastApi().url, '/api/trade/documents/1010/items', '成功后刷新当前单证 1010');
  await openDuplicate;

  /* ---- 9. 静态接线契约：代际失效 + 单飞闸门 + 无客户端权威 ---- */
  const jsSrc = fs.readFileSync(JS_PATH, 'utf8');
  const indexSrc = fs.readFileSync(INDEX_PATH, 'utf8');
  const modulesSrc = fs.readFileSync(MODULES_PATH, 'utf8');
  ok(jsSrc.includes('tdiIsCurrent'), '导出上下文一致性校验');
  ok(jsSrc.includes('tdiBeginMutation'), '导出单飞提交闸门');
  ok(jsSrc.includes('onclick="tdiClose()"'), '关闭按钮先失效代际再关闭');
  ok(jsSrc.includes('tdiEnsureCloseHook'), '包装全局 closeModal 兜底失效代际');
  ok(jsSrc.includes('loadSeq !== TDI.loadGen'), '乱序重新读取以最新序号为准');
  ok(jsSrc.includes('ctx.docId'), '请求 URL 取自捕获上下文');
  ok(!/FromSql|ExecuteSql|SqlCommand|select\s+\*/i.test(jsSrc), '不执行任意 SQL');
  ok(!/\bparseInt\(|parseFloat\(/.test(jsSrc), '绝不从文本解析 Id');
  ok(!/lineAmount\s*:/.test(jsSrc), '绝不提交 / 计算行金额');
  ok(!/currency\s*:/.test(jsSrc), '绝不提交币种');
  ok(indexSrc.includes('/js/trade-doc-items.js'), 'index.html 加载明细行脚本');
  ok(modulesSrc.includes('manageTradeDocumentItems'), '模块行操作入口保持不变');
  ok(jsSrc.includes("const TDI_API = '/api/trade/documents'"), '复用既有明细行接口前缀');

  /* ---- 10. 金额 / 数量语义：留空即 null（不臆造 0），行金额只展示服务端返回值 ---- */
  eq(mod.tdiMoney(null), '—', '未登记金额显示「—」而不是 0');
  eq(mod.tdiNum(null), '—', '未登记数值显示「—」');
  eq(mod.tdiNumberOrNull('f_NotExist'), null, '缺失控件按未填写处理');
  ok(mod.tdiRowHtml(item(99, 1, 4, 2.5), true).includes('<td class="text-right">10</td>'),
    '行金额只展示服务端返回值（前端不重算）');

  console.log('\n✅ ERP-396 单证明细行上下文绑定 UI 逻辑测试全部通过（' + passed + ' 项断言）。\n');
})().catch(err => { console.error('\n❌ ERP-396 单证明细行上下文绑定测试失败：\n', err); process.exit(1); });


