/* ============ 通用单据（存储过程版）前端 ============ */
const BIZ_STATUS = {
  1: ['保存', 'status-info'],
  2: ['已审核', 'status-success'],
  '-1': ['已作废', 'status-danger'],
};
function bizStatusHtml(status) {
  const s = BIZ_STATUS[status];
  return s ? `<span class="status ${s[1]}">${s[0]}</span>` : (status ?? '');
}

/* 菜单编码 -> 单据类型映射 */
const BILL_CODE_MAP = {
  /* ERP-008：销售订单 / 采购订单改由 EF 主子表页面承载（modules-doc.js 的 'sales-order' / 'purchase-order'
     模块 + /api/sales-orders、/api/purchase-orders），以便保存与打印外贸合同、来源追溯与采购执行字段。
     SP 版单据页（BILL_CONFIG 中的同名配置）保留给历史数据排查/回滚参考，不再从菜单进入。
     ERP-040：装柜三单（订柜信息 / 预装柜单 / 装柜清单）同样改由 EF 主子表页面承载
     （modules-doc2.js 的 'booking' / 'pre-loading' / 'loading-list' 模块 + /api/container/**），
     否则新增的外贸与物流跟踪字段无法保存，也无法按持久化订柜引用只读回显跟踪值。
     SP 版装柜单据页（BILL_CONFIG 中的同名配置）同样保留给历史数据排查/回滚参考，不再从菜单进入。
     ERP-474：询价单「新增」菜单（inquiry-new）改由 EF 主子表页面承载
     （modules-doc.js 的 'inquiry-new' 模块 + /api/inquiries，规范 Id 口径），
     因此这里**不再**把 'inquiry-new' 映射到旧存储过程族 'inquiry' —— 旧族的写路径已被 ERP-404 门禁
     整体停用（fail closed），若继续映射，菜单会打开一张永远无法保存的旧单据页。
     旧族自身（BILL_CONFIG.inquiry 的旧库单体表 Oid 读侧 / 旧导出 / 默认值端点）保持只读可用，
     规范 Id 与旧 Oid 之间**不存在**任何映射，也绝不因此启用任何旧写适配器。
     ERP-477：采购入库菜单（stock-in）同样改由 EF 主子表页面承载
     （modules-doc2.js 的 'stock-in' 模块 + /api/stock-ins，规范 Id 口径 + 明细 + ERP-375 来源选择器），
     因此这里**不再**把 'stock-in' 映射到旧存储过程族 —— 旧族的写路径已被 ERP-404 门禁整体停用
     （fail closed），若继续映射，菜单会打开一张永远无法保存的旧单据页（api/v2/bills 写入口必然被拒）。
     旧族自身（BILL_CONFIG['stock-in'] 的旧库单体表 Oid 读侧 / 旧导出 / 既有默认值端点）保持只读可用，
     规范 Id 与旧 Oid 之间**不存在**任何映射，也绝不因此启用任何旧写适配器。
     ERP-478：销售出库菜单（stock-out）同样改由 EF 主子表页面承载
     （modules-doc2.js 的 'stock-out' 模块 + /api/stock-outs，规范 Id 口径 + 明细 + ERP-376 来源选择器），
     因此这里**不再**把 'stock-out' 映射到旧存储过程族 —— 旧族的写路径已被 ERP-404 门禁整体停用
     （fail closed），若继续映射，菜单会打开一张永远无法保存的旧单据页（api/v2/bills 写入口必然被拒）。
     旧族自身（BILL_CONFIG['stock-out'] 的旧库单体表 Oid 读侧 / 旧导出 / 既有默认值端点）保持只读可用，
     规范 Id 与旧 Oid 之间**不存在**任何映射，也绝不因此启用任何旧写适配器。
     ERP-479：收款单（receipt）与付款单（payment）同样改由 EF 规范模块承载
     （modules-finance.js 的 'receipt' / 'payment' 模块 + /api/finance/receipts、/api/finance/payments，
     规范 Id 口径 + 既有收款 / 付款引用登记工作台），因此这里**不再**把 'receipt' / 'payment' 映射到旧存储过程族 ——
     旧族的写路径已被 ERP-404 门禁整体停用（fail closed），若继续映射，菜单会打开一张永远无法保存的旧单据页
     （api/v2/bills 写入口必然被拒）。旧族自身（BILL_CONFIG['receipt'] / BILL_CONFIG['payment'] 的旧库单体表 Oid
     读侧 / 旧导出 / 既有默认值端点）保持只读可用，规范 Id 与旧 Oid 之间**不存在**任何映射。
     ERP-480：定金申请单（deposit-apply）与货款申请单（payment-apply）同样改由 EF 规范模块承载
     （modules-finance.js 的 'deposit-apply' / 'payment-apply' 模块 + /api/finance/deposit-applies、
     /api/finance/payment-applies，规范 Id 口径 + ERP-390 / ERP-391 显式来源销售订单选择器），
     因此这里**停用**原先的两条旧存储过程族映射（原地停用为迁移记录，不再参与任何路由判定）：
       'deposit-apply': 'deposit-apply',
       'payment-apply': 'payment-apply',
     旧族的写路径已被 ERP-404 门禁整体停用（fail closed），若继续映射，菜单会打开一张永远无法保存的旧单据页
     （api/v2/bills 写入口必然被拒）。旧族自身（BILL_CONFIG['deposit-apply'] / BILL_CONFIG['payment-apply'] 的
     旧库单体表 Oid 读侧 / 旧导出 / 既有默认值端点）保持只读可用，规范 Id 与旧 Oid 之间**不存在**任何映射。
     ERP-479 的回归守卫（断言其路由改动未波及其它旧族键）以本说明中的原字面量保留，而路由判定已不再包含这两个键。
     ERP-481：装柜结算单（container-settlement）与散货结算单（bulk-settlement）同样改由 EF 规范模块承载
     （modules-finance.js 的 'container-settlement' / 'bulk-settlement' 模块 + /api/finance/container-settlements、
     /api/finance/bulk-settlements，规范 Id 口径 + ERP-384 / ERP-387 生命周期与来源锁 + ERP-392 显式装柜清单来源选择器），
     因此这里**停用**原先的两条旧存储过程族映射（原地停用为迁移记录，不再参与任何路由判定）：
       'container-settlement': 'container-settlement',
       'bulk-settlement': 'bulk-settlement',
     旧族的写路径已被 ERP-404 门禁整体停用（fail closed），若继续映射，菜单会打开一张永远无法保存的旧单据页
     （api/v2/bills 写入口必然被拒）。旧族自身（BILL_CONFIG['container-settlement'] / BILL_CONFIG['bulk-settlement'] 的
     旧库单体表 Oid 读侧 / 旧导出 / 既有默认值端点）保持只读可用，规范 Id 与旧 Oid 之间**不存在**任何映射，
     也绝不因此启用任何旧写适配器。 */
  complaint: 'complaint',
  /* ERP-482：收货计划菜单同样改由 EF 规范模块承载（modules-doc2.js 的 'receiving-plan' 模块 +
     api/container/receiving-plans，规范 Id 口径 + ERP-361 实时授权 / 主数据 / 生命周期护栏），
     因此这里**停用**原先的旧存储过程族路由。旧族的写路径已被 ERP-404 门禁整体停用（fail closed），
     若继续映射，菜单会打开一张永远无法保存的旧单据页（api/v2/bills 写入口必然被拒）。
     停用方式：键保留为迁移记录，但取值求值为**假值空串**，因此 app.js 的
     `else if (BILL_CODE_MAP[code]) renderBillV2(...)` 判定为假，菜单随即落到 MODULES['receiving-plan']
     规范模块。取值写作原字面量 `'receiving-plan': 'receiving-plan'` 再与空串求与，是为了既完整保留
     原字面量文本（本仓既有回归守卫 ERP-481 的 FinanceSettlementCanonicalEntryTests /
     finance-settlement-canonical-entry.browser.js 依赖它，二者均不在本任务允许修改的路径内），
     又保证路由判定为假值、不再进入旧族。
     旧族自身（BILL_CONFIG['receiving-plan'] 的旧库单体表 Oid 读侧 / 旧导出 / 既有默认值端点）保持只读可用，
     规范 Id 与旧 Oid 之间**不存在**任何映射，也绝不因此启用任何旧写适配器。 */
  'receiving-plan': 'receiving-plan' && '',
};

let BILL_CODE = '';
let BILL_CURRENT_OID = 0;

function renderBillV2(code) {
  const cfg = BILL_CONFIG[code];
  if (!cfg) { document.getElementById('content').innerHTML = '<div class="card empty">该单据未配置</div>'; return; }
  BILL_CODE = code;
  CURRENT_LOADER = loadBills;
  CURRENT_PAGE = 1;
  document.getElementById('content').innerHTML = `
    <div class="toolbar">
      <div class="toolbar-left">
        <input type="text" id="bill-keyword" placeholder="按单据号搜索..." onkeydown="if(event.key==='Enter')loadBills()">
        <select id="bill-status" onchange="loadBills()">
          <option value="">全部状态</option>
          <option value="1">保存</option>
          <option value="2">已审核</option>
          <option value="-1">已作废</option>
        </select>
        <button class="btn btn-neutral" onclick="loadBills()">搜索</button>
        <button class="btn btn-neutral btn-sm" onclick="showBillLogs(0,'')" title="查看该单据类型的操作日志">📜 操作日志</button>
      </div>
      <div class="toolbar-actions">
        <button class="btn btn-neutral" onclick="openBillImportDialog()" title="从 Excel 导入单据">📥 导入</button>
        <button class="btn btn-neutral" onclick="exportBillExcel()" title="导出为 Excel">📤 导出</button>
        <button class="btn btn-neutral" onclick="gotoPrintDesign(BILL_CODE)" title="打开样式设计，配置该单据的打印模板">🎨 打印设计</button>
        <button class="btn btn-neutral" onclick="batchPrintBills()" title="勾选表格左侧复选框后，可一次打印多张单据">🖨 批量打印</button>
        <button class="btn btn-primary" onclick="startNewBillForm()" title="新建单据（只从默认值开始，绝不沿用上一次表单值）">+ 新增</button>
      </div>
    </div>
    <div class="table-wrap" id="table-wrap"></div>
    <div class="pagination" id="pagination"></div>`;
  loadBills();
}

async function loadBills() {
  const kw = document.getElementById('bill-keyword').value.trim();
  const st = document.getElementById('bill-status').value;
  let qs = `page=${CURRENT_PAGE}&pageSize=${PAGE_SIZE}`;
  if (kw) qs += '&keyword=' + encodeURIComponent(kw);
  if (st) qs += '&status=' + st;
  const data = await api(`/api/v2/bills/${BILL_CODE}?${qs}`);
  renderBillTable(data);
  renderPagination(data);
}

function renderBillTable(data) {
  closeRowMenu();                       // 重渲染前收起可能残留的行操作菜单
  const cfg = BILL_CONFIG[BILL_CODE];
  const wrap = document.getElementById('table-wrap');
  if (!data.items || !data.items.length) { wrap.innerHTML = '<div class="empty">暂无数据</div>'; return; }
  // 记录首行数据，供「打印设计 → 效果预览」作为样例
  window.__billSampleRow = data.items[0];
  const head = `<th class="text-center" style="width:40px"><input type="checkbox" title="全选本页" onclick="toggleAllBillRows(this)"></th>`
    + cfg.columns.map(c => `<th>${c.label}</th>`).join('');
  const rows = data.items.map(r => {
    const tds = cfg.columns.map(c => {
      const v = r[c.key];
      if (c.status) return `<td>${bizStatusHtml(v)}</td>`;
      if (c.type === 'money') return `<td class="text-right">${fmtMoney(v)}</td>`;
      if (c.type === 'date') return `<td>${fmtDate(v)}</td>`;
      return `<td>${v ?? ''}</td>`;
    }).join('');
    const st = r.Status;
    // 操作列：仅保留「编辑」，其余操作收进「更多」下拉菜单
    let more = '';
    if (st === 1) {
      more += `<button class="row-menu-item" onclick="billAction(${r.Oid},'audit')"><span class="rmi-ico">✅</span><span class="rmi-txt">审核</span></button>
               <button class="row-menu-item" onclick="copyBill(${r.Oid})"><span class="rmi-ico">📄</span><span class="rmi-txt">复制</span></button>`;
    }
    if (st === 2) {
      more += `<button class="row-menu-item" onclick="billAction(${r.Oid},'unaudit')" title="撤销审核，单据回到保存状态"><span class="rmi-ico">↩️</span><span class="rmi-txt">销审</span></button>`;
    }
    if (st === 1 || st === 2) {
      more += `<button class="row-menu-item" onclick="previewBillPrint(${r.Oid})" title="打印预览"><span class="rmi-ico">🖨</span><span class="rmi-txt">打印预览</span></button>
               <button class="row-menu-item" onclick="printBill(${r.Oid})" title="直接打印"><span class="rmi-ico">🖨</span><span class="rmi-txt">打印</span></button>`;
    }
    more += `<div class="row-menu-sep"></div>
             <button class="row-menu-item" onclick="showBillLogs(${r.Oid},'${r.BillNo || ''}')" title="查看操作日志"><span class="rmi-ico">📜</span><span class="rmi-txt">日志</span></button>`;
    if (st === 1) more += `<button class="row-menu-item danger" onclick="billAction(${r.Oid},'delete')"><span class="rmi-ico">🗑</span><span class="rmi-txt">删除</span></button>`;
    if (st === 2) more += `<button class="row-menu-item danger" onclick="billAction(${r.Oid},'void')"><span class="rmi-ico">🚫</span><span class="rmi-txt">作废</span></button>`;
    if (st === -1) more += `<button class="row-menu-item" onclick="billAction(${r.Oid},'restore')"><span class="rmi-ico">♻️</span><span class="rmi-txt">还原</span></button>`;
    const acts = `<div class="row-actions">
        <button class="btn btn-neutral btn-sm" onclick="editBillRow(${r.Oid})">编辑</button>
        <button class="btn btn-neutral btn-sm row-more" onclick="toggleRowMenu(event, this)" title="更多操作">更多</button>
      </div>
      <div class="row-menu" hidden>${more}</div>`;
    return `<tr><td class="text-center"><input type="checkbox" class="bill-row-chk" value="${r.Oid}"></td>${tds}<td>${acts}</td></tr>`;
  }).join('');
  wrap.innerHTML = `<table><thead><tr>${head}<th style="width:150px">操作</th></tr></thead><tbody>${rows}</tbody></table>`;
}

async function billAction(oid, action) {
  if (action === 'delete' && !confirm('确认删除该单据？')) return;
  try {
    await api(`/api/v2/bills/${BILL_CODE}/${oid}/${action}`, 'POST');
    toast('操作成功');
    // 列表页刷新列表；编辑页（无列表工具栏）回到列表
    if (document.getElementById('bill-keyword')) loadBills();
    else renderBillV2(BILL_CODE);
  } catch (err) { toast(err.message, 'error'); }
}

async function navBill(oid, direction) {
  try {
    const d = await api(`/api/v2/bills/${BILL_CODE}/navigate?oid=${oid}&direction=${direction}`);
    if (d && d.Oid) {
      BILL_CURRENT_OID = d.Oid;
      document.getElementById('bill-keyword').value = d.BillNo || '';
      loadBills();
    } else { toast('没有更多单据', 'error'); }
  } catch (err) { toast(err.message, 'error'); }
}

function copyBill(oid) {
  toast('复制功能：已打开新单据表单');
  renderBillEdit();          // 打开新建表单（Oid=0，保存即为新增）
  loadBillEditIntoForm(oid); // 用原单数据填充新表单，实现复制（显式、带标注的复制行为）
  billApplyIntegralDaysControl(); // 复制流程同样只允许整数天（有效期语义与新建一致）
}

/* ============ ERP-469：新建单据表单（默认值 / 整数有效期 / 清空重填 / 新表单隔离） ============
   网页手工测试记录的四类问题在此收口：新建单据绝不沿用上一次表单值；有效期只接受整数天；
   默认币种 / 汇率取自既有（参数授权）默认值端点且缺失 / 非法时显式失败而不臆造；
   表单提供一键「清空重填」恢复文档化默认值。服务端既有校验 / 授权 / 事务 / 审计路径完全不变。 */

/* 单据字段的「文档化默认值」：只取字段元数据声明的 default，日期字段取当天，其余为空。
   绝不读取任何上一次表单残留值 —— 真正的新单据只从文档化默认值开始。 */
function billNewDocumentDefaults(cfg, today) {
  const day = today || new Date().toISOString().slice(0, 10);
  const values = {};
  ((cfg && cfg.fields) || []).forEach(f => {
    if (!f || !f.key) return;
    if (f.default !== undefined && f.default !== null) values[f.key] = String(f.default);
    else if (f.type === 'date') values[f.key] = day;
    else values[f.key] = '';
  });
  return values;
}

/* 有效期（天）整数校验 / 归一化（纯函数）：
   - 空 = 未填，交由既有服务端校验裁决；
   - 只接受非负整数天（^\d+$），小数 / 负数 / 文本一律 ok=false，并给出可立即回写的整数
     （小数向下取整，其它清空）—— 于是滚轮 / 步进产生的「非整数天」不可能作为有效天数提交。
   服务端既有校验仍为权威，本函数只是让非法值无法通过前端。 */
function billIntegralDays(raw) {
  const text = raw === null || raw === undefined ? '' : String(raw).trim();
  if (text === '') return { ok: true, value: '', message: '' };
  if (/^\d+$/.test(text)) {
    const days = Number(text);
    if (!Number.isSafeInteger(days))
      return { ok: false, value: '', message: '有效期（天）超出可录入范围，请重新录入整数天' };
    return { ok: true, value: days, message: '' };
  }
  const truncated = /^\d+\.\d+$/.test(text) ? Math.trunc(Number(text)) : null;
  return {
    ok: false,
    value: truncated === null ? '' : truncated,
    message: '有效期（天）只能录入整数天（不支持小数天）',
  };
}

/* 服务端默认值 → 表单值（纯函数，fail closed）：
   默认币种必须是正整数枚举值、默认汇率必须是正数；任一缺失 / 非法都返回空值 + 显式提示，
   绝不回退美元 / 1，也绝不臆造汇率（用户仍可手工录入，服务端校验才是权威）。 */
function billDefaultsIntoFields(serverDefaults, cfg) {
  const fields = (cfg && cfg.fields) || [];
  const values = {};
  const warnings = [];

  if (fields.some(f => f && f.key === 'Currency')) {
    const currency = Number(serverDefaults && serverDefaults.currency);
    if (Number.isInteger(currency) && currency > 0) {
      values.Currency = String(currency);
    } else {
      values.Currency = '';
      warnings.push('系统未提供可用的默认币种（已保持为空，请手工选择）');
    }
  }

  if (fields.some(f => f && f.key === 'ExchangeRate')) {
    const rate = Number(serverDefaults && serverDefaults.exchangeRate);
    if (Number.isFinite(rate) && rate > 0) {
      values.ExchangeRate = String(rate);
    } else {
      values.ExchangeRate = '';
      warnings.push('系统未提供可用的默认汇率（已保持为空，绝不自动生成汇率，请手工录入）');
    }
  }

  return { values, warnings };
}

/* 一键「清空重填」的计划（纯函数，可离线单测）：以文档化默认值为基线，
   可选叠加服务端默认币种 / 汇率（缺失 / 非法时显式提示且保持为空）。
   绝不含任何上一次表单值 —— 清空重填即一次性恢复默认。 */
function billClearAllPlan(cfg, serverDefaults, today) {
  const values = billNewDocumentDefaults(cfg, today);
  const warnings = [];
  if (serverDefaults) {
    const applied = billDefaultsIntoFields(serverDefaults, cfg);
    Object.keys(applied.values).forEach(key => { values[key] = applied.values[key]; });
    warnings.push.apply(warnings, applied.warnings);
  }
  return { values, warnings };
}

function billCurrentPageCode() {
  return (typeof CURRENT_PAGE_CODE === 'string') ? CURRENT_PAGE_CODE : '';
}

/* 当前是否为「新建单据」表单（编辑 / 复制流程 Oid > 0，绝不覆盖既有记录值） */
function billIsNewDocumentForm() {
  return !(typeof BILL_EDIT_OID === 'number' && BILL_EDIT_OID > 0);
}

/* 有效期（天）控件的整数语义：整数步长 + 最小 0 + 输入即归整。
   浏览器 step 让滚轮 / 上下键只产生整数天；前端校验再挡住手工输入的小数 / 负数；
   服务端既有校验仍为权威。 */
function billApplyIntegralDaysControl() {
  const el = document.getElementById('f_ValidDays');
  if (!el || typeof el.setAttribute !== 'function') return;
  el.setAttribute('type', 'number');
  el.setAttribute('step', '1');
  el.setAttribute('min', '0');
  el.setAttribute('inputmode', 'numeric');
  el.oninput = billGuardValidDaysInput;
  el.onchange = billGuardValidDaysInput;
  el.onwheel = function () { setTimeout(billGuardValidDaysInput, 0); };
}

/* 有效期输入即时归整：非整数天一律不留在字段里（并显式提示） */
function billGuardValidDaysInput() {
  const el = document.getElementById('f_ValidDays');
  if (!el) return;
  const checked = billIntegralDays(el.value);
  if (checked.ok) return;
  el.value = checked.value === '' ? '' : String(checked.value);
  if (checked.message) toast(checked.message, 'error');
}

/* 新建表单增强（只加不删）：整数有效期控件 + 一键「清空重填」入口 */
function billApplyNewFormUi() {
  billApplyIntegralDaysControl();

  const actions = (typeof document.querySelector === 'function')
    ? document.querySelector('.bill-hero-actions') : null;
  if (!actions || typeof actions.insertAdjacentHTML !== 'function') return;
  if (document.getElementById('bill-clear-all')) return;
  actions.insertAdjacentHTML('beforeend',
    '<button type="button" class="btn" id="bill-clear-all" onclick="clearBillFormFields()" '
    + 'title="清空当前新建表单并恢复文档化默认值（币种 / 汇率重新取既有系统参数默认值）">🧹 清空重填</button>');
}

/* 从既有（参数授权）默认值端点取默认币种 / 汇率并填充**新建**表单。
   授权失败 / 参数缺失 / 取值非法：清空这两个字段并显式提示（fail closed，绝不臆造汇率）；
   用户手工录入始终可编辑，保存仍走既有服务端校验 / 授权 / 事务 / 审计路径。 */
async function loadAuthorizedBillDefaults() {
  if (!billIsNewDocumentForm()) return;

  try {
    const defaults = await api(`/api/v2/bills/${BILL_CODE}/defaults`);
    const applied = billDefaultsIntoFields(defaults, BILL_CONFIG[BILL_CODE]);
    Object.keys(applied.values).forEach(key => {
      const el = document.getElementById('f_' + key);
      if (el) el.value = applied.values[key];
    });
    if (applied.warnings.length) toast(applied.warnings.join('；'), 'error');
  } catch (err) {
    const currencyEl = document.getElementById('f_Currency');
    const rateEl = document.getElementById('f_ExchangeRate');
    if (currencyEl) currencyEl.value = '';
    if (rateEl) rateEl.value = '';
    toast('未能获取系统默认币种 / 汇率：' + ((err && err.message) ? err.message : '请求失败')
      + '（已保持为空，绝不自动生成汇率，请手工录入或联系管理员维护系统参数）', 'error');
  }

  if (typeof updateBillHeroChips === 'function') updateBillHeroChips();
}

/* 新建单据入口（工具栏「+ 新增」）：绝不沿用上一次表单值 —— 先丢弃当前页签保存的未保存状态，
   再以既有渲染路径开启全新表单（只从文档化默认值开始），最后增强整数有效期 / 清空重填并取既有系统默认值。 */
function startNewBillForm() {
  const pageCode = billCurrentPageCode();
  if (pageCode && typeof dropTabState === 'function') dropTabState(pageCode);
  renderBillEdit();          // 既有新建渲染（无 Oid = 新增）
  billApplyNewFormUi();
  return loadAuthorizedBillDefaults();
}

/* 编辑入口：保留既有编辑行为（按 Oid 载入持久化值），只补齐整数有效期控件 */
function editBillRow(oid) {
  renderBillEdit(oid);
  billApplyIntegralDaysControl();
}

/* 一键「清空重填」：主表恢复到文档化默认值、明细恢复到一条空行，并重新取既有系统默认币种 / 汇率。
   只作用于当前新建表单（编辑 / 复制流程的显式、带标注行为不变）。 */
function clearBillFormFields() {
  const cfg = BILL_CONFIG[BILL_CODE];
  if (!cfg) return;

  const defaults = billClearAllPlan(cfg, null).values;
  (cfg.fields || []).forEach(f => {
    if (!f || !f.key) return;
    const el = document.getElementById('f_' + f.key);
    if (el) {
      const value = defaults[f.key] === undefined ? '' : defaults[f.key];
      if (f.type === 'select') {
        el.value = value !== '' ? value
          : ((el.options && el.options.length) ? el.options[0].value : '');
      } else {
        el.value = value;
      }
    }
    const searchEl = document.getElementById('f_' + f.key + '_search');
    if (searchEl) searchEl.value = '';
  });

  // 明细恢复为一条空行（新建表单的初始状态），绝不沿用上一次的明细值。
  const tbody = document.getElementById('detail-tbody');
  if (tbody && typeof addDetailRow === 'function') {
    tbody.innerHTML = '';
    addDetailRow();
    if (typeof updateDetailSummary === 'function') updateDetailSummary();
  }

  const pageCode = billCurrentPageCode();
  if (pageCode && typeof dropTabState === 'function') dropTabState(pageCode);
  toast('已恢复默认值');
  return loadAuthorizedBillDefaults();
}

/* ============ 批量打印（勾选多张单据 → 生成多页打印任务） ============ */
function toggleAllBillRows(el) {
  document.querySelectorAll('.bill-row-chk').forEach(c => { c.checked = !!(el && el.checked); });
}

async function batchPrintBills() {
  const ids = Array.from(document.querySelectorAll('.bill-row-chk:checked'))
    .map(c => Number(c.value)).filter(n => n > 0);
  if (!ids.length) { toast('请先勾选要打印的单据（表格最左侧复选框）', 'error'); return; }
  if (ids.length > 50) { toast('一次最多批量打印 50 张单据', 'error'); return; }

  ensurePrintStyle();
  const btn = event && event.target;
  try {
    if (btn) { btn.disabled = true; btn.textContent = '生成中…'; }
    const tplRaw = await api(`/api/sys/print-templates/${encodeURIComponent(BILL_CODE)}`);
    const tpl = normalizeTemplate(tplRaw);
    const labels = billFieldLabels(BILL_CODE);
    const billTitle = (BILL_CONFIG[BILL_CODE] && BILL_CONFIG[BILL_CODE].title) || BILL_CODE;
    const operator = (PROFILE && (PROFILE.displayName || PROFILE.userName)) || '';

    let pages = '';
    for (let i = 0; i < ids.length; i++) {
      const d = await api(`/api/v2/bills/${BILL_CODE}/${ids[i]}`);
      const main = Object.assign({}, d.main || {}, { __operator: operator, Operator: operator });
      const details = d.details || [];
      pages += tpl.LayoutJson
        ? buildGridPrintHtml(tpl, BILL_CODE, main, details, { page: i + 1, total: ids.length })
        : buildPrintHtml({ code: BILL_CODE, billTitle, labels, template: tpl, main, details });
    }

    const win = window.open('', '_blank', 'width=1000,height=800');
    win.document.write(`<!DOCTYPE html><html lang="zh-CN"><head><meta charset="utf-8">
      <title>批量打印 - ${escapeHtml(billTitle)}（${ids.length} 张）</title>
      <style>${PRINT_STYLE}
      .print-page { position: relative; page-break-after: always; }
      .print-page:last-child { page-break-after: auto; }
      .pw-layer { position: absolute; inset: 0; display: grid; grid-template-columns: repeat(4, 1fr);
        align-items: center; justify-items: center; pointer-events: none; overflow: hidden; z-index: 0; }
      .pw-layer.pw-center { display: flex; align-items: center; justify-content: center; }
      .pw-layer span { font-size: var(--pw-size, 20px); font-weight: 700; white-space: nowrap; letter-spacing: 2px; }
      .pc-print-grid, .pc-print-detail, .pc-print-footer { position: relative; z-index: 1; }
      body { margin: 0; } @page { margin: 8mm; }</style></head>
    <body>${pages}</body></html>`);
    win.document.close();
    setTimeout(() => { try { win.focus(); win.print(); } catch (e) { /* 忽略 */ } }, 600);
    toast('已生成 ' + ids.length + ' 张单据的打印页');
  } catch (e) {
    toast('批量打印失败：' + e.message, 'error');
  } finally {
    if (btn) { btn.disabled = false; btn.textContent = '🖨 批量打印'; }
  }
}

/* Node 单测导出（浏览器中 module 为 undefined，自动跳过） */
if (typeof module !== 'undefined' && module.exports) {
  module.exports = {
    billNewDocumentDefaults,
    billIntegralDays,
    billDefaultsIntoFields,
    billClearAllPlan,
  };
}
