'use strict';
/* ERP-469 网页手工测试回归项前端逻辑单测（可执行，无浏览器依赖）：
   1) 新建询价单的默认币种 / 汇率：只接受既有（参数授权）默认值端点返回的合法取值，
      缺失 / 非法一律 fail closed（保持为空 + 显式提示，绝不回退美元 / 1、绝不臆造汇率）；
   2) 有效期（天）：整数步长 + 整数校验，小数 / 负数 / 文本无法作为有效天数提交；
   3) 标签栏：不再驱逐第 4 个及以后页面；切换页签保留未保存的非敏感界面状态，关闭页签即丢弃，
      绝不采集密码 / 令牌 / 隐藏域 / 文件域，也绝不写 localStorage；
   4) 导出：询价单与销售订单导出均提供显式「搜索 / 预览」，预览与下载使用**同一组**筛选参数，
      未预览即导出被拒绝；
   5) 新建单据隔离与「清空重填」：新表单只从文档化默认值开始，清空重填一次性恢复默认值。
   运行：node tests/manual-web-test-regressions.test.js
   失败时抛异常并以非零码退出。真实浏览器 / 样式验收不在本测试内执行（环境受限，不声称通过）。 */

const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..');
const JS_DIR = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js');
const jsPath = name => path.join(JS_DIR, name);
const jsSource = name => fs.readFileSync(jsPath(name), 'utf8');

let passed = 0;
function ok(cond, msg) { assert.ok(cond, msg); passed += 1; }
function eq(actual, expected, msg) { assert.deepStrictEqual(actual, expected, msg); passed += 1; }

/* ============ 离线 DOM 夹具（与 ERP-286 导航夹具同源，供 app.js 直接 require） ============ */
const elements = Object.create(null);
function makeEl(tag) {
  const el = {
    tagName: (tag || 'DIV').toUpperCase(),
    children: [],
    dataset: {},
    className: '',
    style: {},
    textContent: '',
    value: '',
    options: [],
    attributes: {},
    _fields: [],
    _classes: {},
    classList: {
      add() { Array.prototype.slice.call(arguments).forEach(c => { el._classes[c] = true; }); },
      remove() { Array.prototype.slice.call(arguments).forEach(c => { delete el._classes[c]; }); },
      toggle(c, force) {
        const next = (typeof force === 'boolean') ? force : !el._classes[c];
        if (next) el._classes[c] = true; else delete el._classes[c];
        return next;
      },
      contains(c) { return !!el._classes[c]; },
    },
    setAttribute(name, value) { el.attributes[name] = String(value); },
    getAttribute(name) { return Object.prototype.hasOwnProperty.call(el.attributes, name) ? el.attributes[name] : null; },
    insertAdjacentHTML() {},
    appendChild(child) { el.children.push(child); return child; },
    remove() {},
    getBoundingClientRect() { return { width: 0, height: 0 }; },
    querySelector() { return null; },
    querySelectorAll(selector) { return String(selector).indexOf('input') === 0 ? el._fields : []; },
    addEventListener() {},
    removeEventListener() {},
    click() {},
    focus() {},
    print() {},
  };
  Object.defineProperty(el, 'innerHTML', {
    get() { return el._html || ''; },
    set(v) { el._html = String(v); if (v === '') el.children = []; },
  });
  return el;
}

const storageWrites = [];
global.localStorage = {
  getItem() { return null; },
  setItem(key, value) { storageWrites.push(String(key) + '=' + String(value)); },
  removeItem() {},
};
global.icon = () => '<svg></svg>';
global.document = {
  getElementById(id) {
    if (!elements[id]) elements[id] = makeEl('div');
    return elements[id];
  },
  createElement: tag => makeEl(tag),
  querySelector: () => null,
  querySelectorAll: () => [],
  addEventListener() {},
  removeEventListener() {},
  /* 'loading' 与 ERP-286 离线夹具同源：只登记 DOMContentLoaded，不真正启动定时器（进程可正常退出） */
  readyState: 'loading',
  documentElement: makeEl('html'),
  body: makeEl('body'),
};
global.location = { reload() {} };

/* bill-config.js 在浏览器中依赖 modules.js 的币种选项常量（值 = 既有 Currency 枚举值） */
global.CURRENCY_OPTS = [
  { value: '1', label: '人民币 CNY' }, { value: '2', label: '美元 USD' },
  { value: '3', label: '欧元 EUR' }, { value: '4', label: '港币 HKD' },
];

const app = require(jsPath('app.js'));
const billV2 = require(jsPath('bill-v2.js'));
const billV2Form = require(jsPath('bill-v2-form.js'));
const billExport = require(jsPath('bill-export.js'));
const { BILL_CONFIG } = require(jsPath('bill-config.js'));
const inquiryCfg = BILL_CONFIG.inquiry;
const salesOrderExportCfg = billExport.EXPORT_MENU_MAP['sales-order-export'];

/* ============ 1. 新建询价单：默认币种 / 汇率 fail closed ============ */
eq(inquiryCfg.billType, 'inquiry', '询价单配置存在（新建入口可达）');

const appliedDefaults = billV2.billDefaultsIntoFields({ currency: 1, exchangeRate: '7.2' }, inquiryCfg);
eq(appliedDefaults.values.Currency, '1', '默认币种按既有枚举值回填（可选入下拉）');
eq(appliedDefaults.values.ExchangeRate, '7.2', '默认汇率按既有取值回填');
eq(appliedDefaults.warnings, [], '合法默认值不产生任何提示');

const missingDefaults = billV2.billDefaultsIntoFields({}, inquiryCfg);
eq(missingDefaults.values.Currency, '', '默认值缺失时币种保持为空（绝不回退美元）');
eq(missingDefaults.values.ExchangeRate, '', '默认值缺失时汇率保持为空（绝不回退 1）');
eq(missingDefaults.warnings.length, 2, '缺失时逐项显式提示（fail closed）');

[
  { defaults: { currency: 0, exchangeRate: '7.2' }, invalid: 'Currency', why: '币种 0' },
  { defaults: { currency: -2, exchangeRate: '7.2' }, invalid: 'Currency', why: '负币种' },
  { defaults: { currency: 'CNY', exchangeRate: '7.2' }, invalid: 'Currency', why: '币种代码不是枚举值' },
  { defaults: { currency: 2, exchangeRate: 0 }, invalid: 'ExchangeRate', why: '汇率 0' },
  { defaults: { currency: 2, exchangeRate: -1 }, invalid: 'ExchangeRate', why: '负汇率' },
  { defaults: { currency: 2, exchangeRate: 'abc' }, invalid: 'ExchangeRate', why: '不可解析汇率' },
  { defaults: { currency: 2, exchangeRate: null }, invalid: 'ExchangeRate', why: '汇率缺失' },
  { defaults: { currency: 'CNY', exchangeRate: 'abc' }, invalid: 'both', why: '币种与汇率都非法' },
].forEach(item => {
  const applied = billV2.billDefaultsIntoFields(item.defaults, inquiryCfg);
  ok(applied.warnings.length > 0, '非法默认值（' + item.why + '）显式提示');
  if (item.invalid === 'Currency' || item.invalid === 'both')
    eq(applied.values.Currency, '', '非法默认值（' + item.why + '）不写入币种（绝不臆造）');
  else
    eq(applied.values.Currency, '2', '合法维度（' + item.why + '）照常回填');
  if (item.invalid === 'ExchangeRate' || item.invalid === 'both')
    eq(applied.values.ExchangeRate, '', '非法默认值（' + item.why + '）不写入汇率（绝不臆造）');
  else
    eq(applied.values.ExchangeRate, '7.2', '合法维度（' + item.why + '）照常回填');
});

const v2Src = jsSource('bill-v2.js');
ok(v2Src.includes('api(`/api/v2/bills/${BILL_CODE}/defaults`)'),
  '复用既有（参数授权）默认值端点，不新增取数通道');
ok(v2Src.includes('startNewBillForm()') && v2Src.includes('loadAuthorizedBillDefaults()'),
  '新建入口显式加载既有默认值');
ok(v2Src.includes('已保持为空，绝不自动生成汇率'), '失败路径显式提示且不生成汇率');
ok(!/exchangeRate\s*[:=]\s*1\b/.test(v2Src) && !/Currency\.USD/.test(v2Src), '前端绝不内置美元 / 汇率 1 兜底');

/* ============ 2. 有效期（天）整数语义 ============ */
eq(billV2.billIntegralDays('7'), { ok: true, value: 7, message: '' }, '整数天接受');
eq(billV2.billIntegralDays(' 30 '), { ok: true, value: 30, message: '' }, '整数天去空白后接受');
eq(billV2.billIntegralDays(''), { ok: true, value: '', message: '' }, '留空交由服务端既有校验裁决');
eq(billV2.billIntegralDays('0'), { ok: true, value: 0, message: '' }, '0 天是合法整数天');

const fractional = billV2.billIntegralDays('7.5');
eq(fractional.ok, false, '小数天被拒绝（滚轮 / 步进产生的非整数天不可提交）');
eq(fractional.value, 7, '小数天回落为整数天以供即时归整');
ok(fractional.message.includes('整数天'), '小数天提示明确指向整数天');

['-1', 'abc', '1e3', '7天', '7,5'].forEach(raw => {
  eq(billV2.billIntegralDays(raw).ok, false, '非法天数（' + raw + '）被拒绝');
});

const dayField = inquiryCfg.fields.find(f => f.key === 'ValidDays');
eq(dayField.integer, true, '有效期字段声明为整数天');
eq(dayField.step, 1, '有效期字段声明整数步长（浏览器滚轮只产生整数天）');
eq(dayField.min, 0, '有效期字段声明最小 0');
ok(v2Src.includes("el.setAttribute('step', '1')"), '运行时把整数步长写到有效期控件上');
ok(v2Src.includes('onwheel'), '滚轮步进同样归整为整数天');
ok(v2Src.includes('billGuardValidDaysInput'), '客户端整数校验已接线');
ok(!/parseInt\(|parseFloat\(/.test(v2Src), '有效期不使用浮点解析');

/* ============ 3. 标签栏：不再驱逐 + 未保存状态生命周期 ============ */
['sales-order', 'purchase-order', 'inquiry', 'stock-in', 'receipt'].forEach(code => app.addTab(code, code));
const tabs = app.getOpenTabs();
eq(tabs.length, 5, '标签栏保留第 4 / 第 5 个页面（不再驱逐）');
eq(tabs[0].code, 'receipt', '最新打开的页签排最前');
eq(tabs.map(t => t.code).sort().join(','),
  ['sales-order', 'purchase-order', 'inquiry', 'stock-in', 'receipt'].sort().join(','),
  '全部已打开页面都在标签栏中');
app.addTab('inquiry', 'inquiry');
eq(app.getOpenTabs().length, 5, '重复打开同一页面只去重、不新增');
ok(!/openTabs\.slice\(0, 3\)/.test(jsSource('app.js')), '源码中不再存在 3 个上限的驱逐逻辑');

const fakeField = props => Object.assign({ type: 'text', value: '' }, props || {});
const fakeContainer = fields => ({ querySelectorAll: () => fields });

const sourceFields = [
  fakeField({ id: 'bill-keyword', value: 'INQ-2026' }),
  fakeField({ id: 'bill-status', value: '1' }),
  fakeField({ id: 'bill-flag', type: 'checkbox', checked: true }),
  fakeField({ id: 'login-password', type: 'password', value: 'secret-value' }),
  fakeField({ id: 'auth-token', value: 'bearer-token-value' }),
  fakeField({ id: 'hidden-oid', type: 'hidden', value: '42' }),
  fakeField({ id: 'upload-evidence', type: 'file', value: 'C:/secret.pdf' }),
  fakeField({ id: 'user-password-confirm', type: 'text', value: 'tokenish' }),
];
const captured = app.captureContainerTabState(fakeContainer(sourceFields));
eq(captured['bill-keyword'].value, 'INQ-2026', '普通输入被采集');
eq(captured['bill-status'].value, '1', '下拉值被采集');
eq(captured['bill-flag'].checked, true, '勾选状态被采集');
eq(captured['login-password'], undefined, '密码字段绝不采集');
eq(captured['auth-token'], undefined, '令牌字段绝不采集');
eq(captured['hidden-oid'], undefined, '隐藏域绝不采集');
eq(captured['upload-evidence'], undefined, '文件域绝不采集');
eq(captured['user-password-confirm'], undefined, '命中敏感词的字段绝不采集');
eq(Object.keys(captured).length, 3, '采集结果只含非敏感字段');

const targetFields = [
  fakeField({ id: 'bill-keyword', value: '' }),
  fakeField({ id: 'bill-status', value: '' }),
  fakeField({ id: 'bill-flag', type: 'checkbox', checked: false }),
  fakeField({ id: 'login-password', type: 'password', value: '' }),
];
app.applyContainerTabState(fakeContainer(targetFields), captured);
eq(targetFields[0].value, 'INQ-2026', '切换回来恢复未保存的输入');
eq(targetFields[1].value, '1', '切换回来恢复未保存的下拉值');
eq(targetFields[2].checked, true, '切换回来恢复未保存的勾选');
eq(targetFields[3].value, '', '敏感字段绝不回写');

/* 页签状态：保存 → 切换后恢复 → 关闭即丢弃 */
const contentEl = document.getElementById('content');
contentEl._fields = [fakeField({ id: 'bill-keyword', value: 'INQ-9' })];
app.captureTabState('inquiry');
eq(app.getTabState().inquiry.content['bill-keyword'].value, 'INQ-9', '页签状态按页面编码保存');
contentEl._fields = [fakeField({ id: 'bill-keyword', value: '' })];
app.applyTabState('inquiry');
eq(contentEl._fields[0].value, 'INQ-9', '切回该页签时恢复未保存状态');
contentEl._fields = [fakeField({ id: 'bill-keyword', value: '' })];
app.dropTabState('inquiry');
eq(app.getTabState().inquiry, undefined, '关闭页签即丢弃其状态');
app.applyTabState('inquiry');
eq(contentEl._fields[0].value, '', '已关闭页签的状态绝不复活');
eq(storageWrites.filter(w => w.indexOf('bill-keyword') >= 0).length, 0,
  '页签状态绝不写入 localStorage（仅浏览器会话内存）');

const appSrc = jsSource('app.js');
const functionBody = name => {
  const start = appSrc.indexOf('function ' + name);
  return start < 0 ? '' : appSrc.slice(start, start + 900);
};
const switchTabBody = functionBody('switchTab(code, name)');
ok(switchTabBody.indexOf('saveActiveTabState()') >= 0 && switchTabBody.indexOf('applyTabState(code)') >= 0,
  'switchTab 先保存当前页签、再恢复目标页签');
const closeTabBody = functionBody('closeTab(code)');
ok(closeTabBody.indexOf('dropTabState(code)') >= 0, 'closeTab 丢弃该页签保存的状态');
ok(appSrc.indexOf('function saveActiveTabState()') >= 0, '离开页面前保存状态的唯一入口存在');

/* ============ 4. 导出：显式搜索 / 预览 + 同一组筛选参数 ============ */
const exportParams = billExport.billExportParams({
  start: '2026-09-01', end: '2026-09-30', status: '1', keyword: '  INQ-1  ',
});
eq(exportParams, { start: '2026-09-01', end: '2026-09-30', status: '1', keyword: 'INQ-1' },
  '筛选参数归一化（关键字去空白）');

const inquiryDownloadUrl = billExport.billExportDownloadUrl('inquiry-export', exportParams);
const inquiryPreviewUrl = billExport.billExportPreviewUrl('inquiry-export', exportParams);
ok(inquiryDownloadUrl.indexOf('/api/v2/bills/inquiry/export?') === 0, '询价单导出走既有导出接口');
ok(inquiryPreviewUrl.indexOf('/api/v2/bills/inquiry/export-preview?') === 0, '询价单预览走导出前预览接口');
eq(inquiryDownloadUrl.split('?')[1], inquiryPreviewUrl.split('?')[1],
  '询价单：预览与下载使用完全相同的筛选参数（含 dateField）');
ok(inquiryPreviewUrl.indexOf('dateField=InquiryDate') >= 0, '预览沿用既有日期字段口径');

const soParams = billExport.billExportParams({
  start: '2026-09-01', end: '2026-09-30', status: 'Approved', keyword: 'SO-1',
});
eq(billExport.billExportDownloadUrl('sales-order-export', soParams),
  '/api/sales-orders/export-excel?start=2026-09-01T00%3A00%3A00&end=2026-09-30T23%3A59%3A59&status=Approved&keyword=SO-1',
  '销售订单下载携带日期 / 状态 / 关键字（既有授权导出接口）');
eq(billExport.billExportPreviewUrl('sales-order-export', soParams),
  '/api/sales-orders/export?start=2026-09-01T00%3A00%3A00&end=2026-09-30T23%3A59%3A59',
  '销售订单预览使用既有已授权 JSON 导出接口（同一日期范围）');

const previewRows = [
  { orderNo: 'SO-1', customerPoNo: 'PO-1', contractNo: 'C-1', status: 'Approved' },
  { orderNo: 'SO-2', customerPoNo: 'PO-2', contractNo: 'C-2', status: 'Cancelled' },
  { orderNo: 'SO-3', customerPoNo: 'PO-3', contractNo: 'C-9', status: 'Approved' },
];
eq(billExport.billPreviewFilterRows(previewRows, billExport.billExportParams({ status: 'Approved' }), salesOrderExportCfg).length,
  2, '预览按状态过滤');
eq(billExport.billPreviewFilterRows(previewRows, billExport.billExportParams({ keyword: 'so-1' }), salesOrderExportCfg).length,
  1, '预览按关键字过滤（大小写无关）');
eq(billExport.billPreviewFilterRows(previewRows, billExport.billExportParams({ keyword: 'c-9' }), salesOrderExportCfg).length,
  1, '预览关键字覆盖合同号（与导出同口径）');

ok(!billExport.billExportPreviewMatches(null, exportParams), '未预览时导出被拒绝');
ok(billExport.billExportPreviewMatches({ signature: billExport.billExportSignature(exportParams) }, exportParams),
  '预览后可用同一组条件导出');
const changedParams = billExport.billExportParams({
  start: '2026-09-01', end: '2026-09-30', status: '1', keyword: 'INQ-2',
});
ok(!billExport.billExportPreviewMatches({ signature: billExport.billExportSignature(exportParams) }, changedParams),
  '筛选条件变更后必须先重新搜索 / 预览');
ok(billExport.billPreviewSummaryText({ total: 3, items: [1, 2] }).indexOf('命中 3 条') >= 0,
  '预览摘要显式呈现命中条数');

const exportSrc = jsSource('bill-export.js');
ok(exportSrc.indexOf('🔍 搜索 / 预览') >= 0 && exportSrc.indexOf('onclick="previewExport()"') >= 0,
  '导出视图提供可见的搜索 / 预览动作');
ok(exportSrc.indexOf('previewApi') >= 0, '两个导出族都声明了预览接口');
ok(exportSrc.indexOf('billExportPreviewUrl(EXPORT_CURRENT_CODE, params)') >= 0
  && exportSrc.indexOf('billExportDownloadUrl(EXPORT_CURRENT_CODE, params)') >= 0,
  '预览与下载共用同一参数构造');
ok(exportSrc.indexOf('billExportCurrentParams()') >= 0, '预览读取同一表单参数');
ok(!/ExecuteSql|SqlCommand|select\s+\*/i.test(exportSrc), '前端绝不拼接 SQL');

/* ============ 5. 新建单据隔离与「清空重填」 ============ */
const today = '2026-10-13';
const firstDefaults = billV2.billNewDocumentDefaults(inquiryCfg, today);
eq(firstDefaults.InquiryDate, today, '新建日期默认取当天');
eq(firstDefaults.Remark, '', '新建备注为空（绝不沿用上一次表单值）');
eq(firstDefaults.ExchangeRate, '', '新建汇率从空开始（由既有默认值端点填充）');
eq(firstDefaults.ValidDays, '', '新建有效期为空（由用户 / 服务端校验裁决）');
eq(firstDefaults.Currency, '', '新建币种从空开始（由既有默认值端点填充）');
firstDefaults.Remark = '上次残留值';
eq(billV2.billNewDocumentDefaults(inquiryCfg, today).Remark, '', '默认值不残留任何调用期间的值');

const clearPlan = billV2.billClearAllPlan(inquiryCfg, { currency: 1, exchangeRate: '7.2' }, today);
eq(clearPlan.values.Currency, '1', '清空重填恢复既有默认币种');
eq(clearPlan.values.ExchangeRate, '7.2', '清空重填恢复既有默认汇率');
eq(clearPlan.values.Remark, '', '清空重填清掉用户输入');
eq(clearPlan.values.InquiryDate, today, '清空重填恢复文档化日期默认值');
eq(clearPlan.warnings, [], '清空重填在默认值合法时无提示');
const clearPlanNoServer = billV2.billClearAllPlan(inquiryCfg, null, today);
eq(clearPlanNoServer.values.Currency, '', '清空重填绝不臆造币种');
eq(clearPlanNoServer.values.ExchangeRate, '', '清空重填绝不臆造汇率');

ok(v2Src.indexOf('onclick="clearBillFormFields()"') >= 0, '表单暴露一键清空重填入口');
ok(v2Src.indexOf('🧹 清空重填') >= 0, '清空重填按钮文案明确');
ok(v2Src.indexOf('dropTabState(pageCode)') >= 0, '新单据入口丢弃该页签此前保存的未保存状态');
ok(v2Src.indexOf('billIsNewDocumentForm') >= 0, '默认值只在新建表单生效（编辑 / 复制不覆盖既有值）');

/* 弹层表单（历史入口）与独立表单页同一口径，且不重复实现 */
ok(typeof billV2Form.billFormHasSharedHelpers === 'function', '弹层表单导出共享判定');
const formSrc = jsSource('bill-v2-form.js');
ok(formSrc.indexOf('billDefaultsIntoFields') >= 0 && formSrc.indexOf('billIntegralDays') >= 0,
  '弹层表单复用同一默认值与整数天口径（不重复实现）');
ok(formSrc.indexOf('clearBillModalFields') >= 0, '弹层表单同样提供清空重填');

console.log('\n✅ ERP-469 网页手工测试回归项前端逻辑测试全部通过（' + passed + ' 项断言）。');
console.log('ℹ️ 真实浏览器 / 样式验收未执行（环境受限，不声称通过）：本测试只覆盖可离线执行的逻辑与接线契约。');
process.exit(0);
