/* ============ 代理服务费对账单月度汇总（ERP-110 只读派生；ERP-181 动态字段选择预览） ============
   口径与后端 AgencyServiceFeeMonthlySummaryRules / AgencyServiceFeeMonthlySummaryService 一一对应：
   - 仅统计**未删除**的对账单证据，按「对账日期所属年月 + 客户 + 原币」分组；
   - 仅**未删除且已登记**的对账单计入原币合计（合计直接来自服务端持久化 TotalAmount，不重算、不换算）；
   - 草稿与已作废对账单单独计数、金额单独列示，绝不并入原币合计；
   - 服务期间跨月**不按期间分摊**：全额计入对账日期所属月份；
   - 不同币种分别成组、绝不合并，本页没有任何跨币种总额；
   - 只读：不写库、不迁移、不回填。
   ERP-181 新增字段选择器：字段只来自 GET /api/dynamic-agency-service-fee-monthly-report 返回的有限白名单目录
   （示例键：statementYear / statementMonth / statementMonthText / customerId / customerCode / customerName /
   currency / amountDecimals / registeredCount / registeredTotalAmount / registeredTotalAmountText /
   draftCount / draftTotalAmount / draftTotalAmountText / voidedCount / voidedTotalAmount /
   voidedTotalAmountText / statementCount），预览走 POST /api/dynamic-agency-service-fee-monthly-report，
   只发送「白名单字段 + 客户 / 币种 / 对账日期筛选 + 有界分页（pageSize 1~200）」。
   既有只读接口 GET /api/agency-service-fee-statements/monthly-summary? 仍由 ERP-110 提供（本页预览已切换为动态接口）。 */

let ASFMS = {
  customers: [],
  catalog: null,       // 字段白名单目录 DTO
  fields: [],          // 目录字段
  selectedKeys: [],    // 当前勾选的字段键（默认全选）
  filters: { customerId: '', currency: '', from: '', to: '', page: 1, pageSize: 50 },
  view: null,
  loading: false,
  error: null
};

const ASFMS_CURRENCIES = ['CNY', 'USD', 'EUR', 'HKD', 'GBP', 'JPY'];

/* 从对账单证据册（ERP-070）打开：只读月度汇总，带客户 / 币种 / 对账日期区间筛选与字段选择；可选 customerId 预筛选 */
async function openAgencyServiceFeeMonthlySummary(customerId) {
  ASFMS = {
    customers: [],
    catalog: null,
    fields: [],
    selectedKeys: [],
    filters: {
      customerId: customerId ? String(customerId) : '',
      currency: '', from: '', to: '', page: 1, pageSize: 50
    },
    view: null,
    loading: true,
    error: null
  };

  const modal = document.getElementById('modal');
  modal.innerHTML = '<div style="padding:40px;text-align:center;color:#64748b">正在加载…</div>';
  modal.style.display = 'block';

  try {
    const res = await api('/api/base/customers?page=1&pageSize=500');
    ASFMS.customers = (res && res.items) ? res.items : (Array.isArray(res) ? res : []);
  } catch (e) {
    toast('客户列表加载失败：' + e.message, 'error');
  }

  await asfmsLoadCatalog();
  if (ASFMS.catalog) {
    await asfmsPreview(1);
  }
  asfmsRender();
}

/* 轻量请求封装：返回完整 ApiResponse 信封（保留 code），网络异常抛给调用方 */
async function asfmsRequest(path, method = 'GET', body = null) {
  const headers = { 'Content-Type': 'application/json' };
  const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
  if (token) headers['Authorization'] = 'Bearer ' + token;
  const opts = { method, headers };
  if (body) opts.body = JSON.stringify(body);
  const resp = await fetch(path, opts);
  return await resp.json();
}

/* 加载字段白名单目录（需登录 + 客户资料菜单授权 + 业务员数据范围；授权 / 网络失败 fail closed，不渲染任何字段） */
async function asfmsLoadCatalog() {
  try {
    const resp = await asfmsRequest('/api/dynamic-agency-service-fee-monthly-report');
    if (resp.code === 0) {
      ASFMS.catalog = resp.data;
      ASFMS.fields = (resp.data && resp.data.fields) || [];
      ASFMS.selectedKeys = ASFMS.fields.map(f => f.key);
      return;
    }
    ASFMS.catalog = null;
    if (resp.code === 2000 || resp.code === 2003) { if (typeof logout === 'function') logout(); }
    ASFMS.error = { kind: asfmsKindOfCode(resp.code), message: resp.message };
  } catch (err) {
    ASFMS.catalog = null;
    ASFMS.error = { kind: 'network', message: (err && err.message) || '无法连接到服务器' };
  }
}

function asfmsRender() {
  const modal = document.getElementById('modal');
  const f = ASFMS.filters;
  const customerOptions = ASFMS.customers.map(c =>
    `<option value="${c.id}" ${String(c.id) === String(f.customerId) ? 'selected' : ''}>`
    + `${escapeHtml(c.customerCode || '')} ${escapeHtml(c.customerName || '')}</option>`).join('');

  modal.innerHTML = `
    <div class="modal-content" style="max-width:1280px">
      <div class="modal-header">
        <h3>代理服务费对账单月度汇总（ERP-110 / 动态字段预览）</h3>
        <button class="modal-close" onclick="closeModal()">✕</button>
      </div>
      <div style="padding:6px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;color:#334155;font-size:12px;margin-bottom:8px">
        <b>只读证据汇总</b>：仅<b>未删除且已登记</b>的对账单计入<b>原币合计</b>；草稿与已作废单独计数；
        服务期间跨月<b>不按期间分摊</b>；<b>不是</b>收入确认 / 应收账款或应收余额 / 付款通知 / 税务申报 / 结算确认。
      </div>
      <div style="display:flex;gap:6px;flex-wrap:wrap;align-items:end;margin-bottom:8px">
        <div><label class="ea-lb">客户</label>
          <select id="asfms-f-customer" style="min-width:180px">
            <option value="">全部客户</option>${customerOptions}
          </select></div>
        <div><label class="ea-lb">币种</label>
          <select id="asfms-f-currency" style="min-width:110px">
            <option value="">全部币种</option>
            ${ASFMS_CURRENCIES.map(c => `<option value="${c}" ${f.currency === c ? 'selected' : ''}>${c}</option>`).join('')}
          </select></div>
        <div><label class="ea-lb">对账日期从</label>
          <input type="date" id="asfms-f-from" value="${escapeHtml(f.from)}"></div>
        <div><label class="ea-lb">到</label>
          <input type="date" id="asfms-f-to" value="${escapeHtml(f.to)}"></div>
        <div><label class="ea-lb">每页</label>
          <input type="number" id="asfms-f-pagesize" value="${Number(f.pageSize)}" min="1" max="200" style="width:70px"></div>
        <button class="btn btn-primary btn-sm" onclick="asfmsSearch()">🔍 预览</button>
        <button class="btn btn-neutral btn-sm" onclick="openAgencyServiceFeeStatementRegister()">← 返回对账单台账</button>
      </div>
      ${asfmsFieldChooserHtml()}
      <div id="asfms-result">${asfmsResultHtml()}</div>
    </div>`;
  modal.style.display = 'block';
}

/* 字段选择器：仅由目录白名单渲染为复选框，无自由填写的字段名 */
function asfmsFieldChooserHtml() {
  if (ASFMS.error && !ASFMS.catalog) {
    return asfmsErrorHtml(ASFMS.error.kind, ASFMS.error.message);
  }
  if (!ASFMS.catalog) {
    return '<div class="pd-hint" style="color:#64748b">正在加载字段目录…</div>';
  }
  const fields = ASFMS.fields || [];
  const selected = new Set(ASFMS.selectedKeys || []);
  const boxes = fields.map(f => {
    const checked = selected.has(f.key) ? 'checked' : '';
    return `<label style="display:inline-flex;align-items:center;gap:4px;margin:3px 6px 3px 0;padding:2px 8px;border:1px solid #e2e8f0;border-radius:6px;background:#f8fafc;cursor:pointer">
      <input type="checkbox" name="asfms-dyn-field" value="${escapeHtml(f.key)}" ${checked} onchange="asfmsSyncSelection()">
      <span>${escapeHtml(f.label || f.key)}</span></label>`;
  }).join('');
  return `<div style="margin-bottom:8px">
    <div style="font-size:12px;color:#334155;margin-bottom:4px"><b>选择字段</b>
      <button class="btn btn-neutral btn-sm" style="margin-left:8px" onclick="asfmsToggleAll(true)">全选</button>
      <button class="btn btn-neutral btn-sm" onclick="asfmsToggleAll(false)">全不选</button>
    </div>
    <div style="display:flex;flex-wrap:wrap">${boxes}</div>
  </div>`;
}

/* 同步勾选状态到 selectedKeys（复选框 onchange） */
function asfmsSyncSelection() {
  const boxes = document.querySelectorAll('input[name="asfms-dyn-field"]');
  ASFMS.selectedKeys = Array.from(boxes).filter(b => b.checked).map(b => b.value);
}

function asfmsToggleAll(checked) {
  const boxes = document.querySelectorAll('input[name="asfms-dyn-field"]');
  ASFMS.selectedKeys = [];
  boxes.forEach(b => { b.checked = checked; if (checked) ASFMS.selectedKeys.push(b.value); });
}

function asfmsSearch() {
  ASFMS.filters.customerId = (document.getElementById('asfms-f-customer') || {}).value || '';
  ASFMS.filters.currency = (document.getElementById('asfms-f-currency') || {}).value || '';
  ASFMS.filters.from = (document.getElementById('asfms-f-from') || {}).value || '';
  ASFMS.filters.to = (document.getElementById('asfms-f-to') || {}).value || '';
  const size = (document.getElementById('asfms-f-pagesize') || {}).value;
  if (size) ASFMS.filters.pageSize = Math.max(1, Math.min(200, Number(size) || 50));
  asfmsSyncSelection();
  asfmsPreview(1);
}

/* 规范化选定字段（fail closed）：只保留目录白名单内的键、去重、保持请求顺序 */
function asfmsSelectFields() {
  const valid = new Set((ASFMS.fields || []).map(f => f && f.key).filter(Boolean));
  const seen = new Set();
  const result = [];
  for (const k of (Array.isArray(ASFMS.selectedKeys) ? ASFMS.selectedKeys : [])) {
    if (typeof k !== 'string') continue;
    const key = k.trim();
    if (!key || !valid.has(key) || seen.has(key)) continue;
    seen.add(key);
    result.push(key);
  }
  return result;
}

/* 组装有界预览请求体：字段只来自目录、分页有界、筛选仅客户 / 币种 / 对账日期，绝不接受任意字段名 */
function asfmsBuildRequest(page) {
  const fields = asfmsSelectFields();
  const pageNum = Math.max(1, Math.floor(Number(page) || 1));
  const maxPageSize = (ASFMS.catalog && ASFMS.catalog.maxPageSize) ? Number(ASFMS.catalog.maxPageSize) : 200;
  let pageSize = Math.floor(Number(ASFMS.filters.pageSize));
  if (!Number.isFinite(pageSize)) pageSize = 50;
  pageSize = Math.max(1, Math.min(maxPageSize, pageSize));

  const req = { fields, page: pageNum, pageSize };
  const cid = Number(ASFMS.filters.customerId);
  if (Number.isFinite(cid) && cid > 0) req.customerId = cid;
  if (ASFMS.filters.currency) req.currency = ASFMS.filters.currency;
  if (ASFMS.filters.from) req.statementDateFrom = String(ASFMS.filters.from).slice(0, 10);
  if (ASFMS.filters.to) req.statementDateTo = String(ASFMS.filters.to).slice(0, 10);
  return req;
}

/* 预览：组装有界请求 → POST → 安全渲染列名与单元格；授权 / 无效 / 空 / 网络失败均可见 */
async function asfmsPreview(page) {
  ASFMS.filters.page = page;
  ASFMS.loading = true;
  ASFMS.error = null;
  asfmsRender();

  const req = asfmsBuildRequest(page);
  try {
    const resp = await asfmsRequest('/api/dynamic-agency-service-fee-monthly-report', 'POST', req);
    ASFMS.loading = false;
    if (resp.code === 0) {
      ASFMS.view = resp.data;
    } else if (resp.code === 2000 || resp.code === 2003) {
      if (typeof logout === 'function') logout();
      ASFMS.view = null;
      ASFMS.error = { kind: 'unauthorized', message: resp.message };
    } else {
      ASFMS.view = null;
      ASFMS.error = { kind: asfmsKindOfCode(resp.code), message: resp.message };
    }
  } catch (err) {
    ASFMS.loading = false;
    ASFMS.view = null;
    ASFMS.error = { kind: 'network', message: (err && err.message) || '无法连接到服务器' };
  }
  asfmsRender();
}

/* 业务码 → 错误态分类 */
function asfmsKindOfCode(code) {
  if (code === 2002) return 'forbidden';
  if (code === 2000 || code === 2003) return 'unauthorized';
  if (code === 5000) return 'error';
  return 'invalid';
}

/* 错误提示（授权 / 未登录 / 无效请求 / 网络失败分别可见，且不暴露任何数据） */
function asfmsErrorHtml(kind, message) {
  const labels = {
    forbidden: '权限不足',
    unauthorized: '未登录 / 登录已过期',
    invalid: '请求无效',
    network: '网络请求失败',
    error: '预览失败',
  };
  return `<div class="pd-hint" style="color:#b91c1c;background:#fef2f2;border-color:#fecaca">
    <b>${escapeHtml(labels[kind] || '预览失败')}</b>：${escapeHtml(message || '')}</div>`;
}

function asfmsLoadingHtml() {
  return '<div class="pd-hint" style="text-align:center;color:#64748b">正在预览（只读查询）…</div>';
}

/* 单元格纯文本：null 显示为空、数字合理格式化、其余原样 */
function asfmsCellText(value, field) {
  if (value === null || value === undefined) return '';
  const dataType = (field && field.dataType) || 'text';
  if (dataType === 'number') {
    const n = Number(value);
    if (Number.isFinite(n)) return Number.isInteger(n) ? String(n) : n.toFixed(2);
    return String(value);
  }
  return String(value);
}

function asfmsRenderCell(value, field) {
  return escapeHtml(asfmsCellText(value, field));
}

/* 结果表格 HTML：表头为返回的列名、单元格为返回的选定字段值，全部经转义；空结果在表体内可见 */
function asfmsTableHtml(view) {
  const cols = (view && view.columns) || [];
  const rows = (view && view.rows) || [];
  if (!cols.length) return '';
  const align = c => (c.dataType === 'number') ? ' class="text-right"' : '';
  const head = cols.map(c => `<th${align(c)}>${escapeHtml(c.label || c.key)}</th>`).join('');
  const body = rows.length
    ? rows.map(r => `<tr>${cols.map(c => `<td${align(c)}>${asfmsRenderCell(r[c.key], c)}</td>`).join('')}</tr>`).join('')
    : `<tr><td colspan="${cols.length}" class="text-muted">没有符合条件的月度汇总（证据数字，不代表收入或应收）。</td></tr>`;
  const prevDisabled = !view || view.page <= 1 ? ' disabled' : '';
  const nextDisabled = !view || view.page >= view.totalPages ? ' disabled' : '';
  const paging = `<div style="display:flex;justify-content:space-between;align-items:center;margin-top:8px">
    <div class="text-muted">共 ${view ? view.total : 0} 个月份分组 · 第 ${view ? view.page : 1} 页 · 每页 ${view ? view.pageSize : 0} 组</div>
    <div>
      <button class="btn btn-neutral btn-sm" onclick="asfmsPage(-1)"${prevDisabled}>← 上一页</button>
      <button class="btn btn-neutral btn-sm" onclick="asfmsPage(1)"${nextDisabled}>下一页 →</button>
    </div></div>`;
  return `<div class="table-wrap" style="margin-top:8px"><table><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table></div>${paging}`;
}

function asfmsResultHtml() {
  if (ASFMS.error) {
    return asfmsErrorHtml(ASFMS.error.kind, ASFMS.error.message);
  }
  if (ASFMS.loading) {
    return asfmsLoadingHtml();
  }
  const v = ASFMS.view;
  if (!v) return '<div class="text-muted" style="padding:8px">尚未预览或加载失败。</div>';

  const hints = [
    v.readOnlyText ? `<div class="pd-hint">${escapeHtml(v.readOnlyText)}</div>` : '',
    v.boundaryText ? `<div class="pd-hint">${escapeHtml(v.boundaryText)}</div>` : '',
    v.evidenceOnlyText ? `<div class="pd-hint">${escapeHtml(v.evidenceOnlyText)}</div>` : '',
    v.currencyIsolationText ? `<div class="pd-hint">${escapeHtml(v.currencyIsolationText)}</div>` : '',
    v.noProrationText ? `<div class="pd-hint">${escapeHtml(v.noProrationText)}</div>` : '',
  ].join('');

  const truncated = v.truncated
    ? `<div class="pd-hint" style="color:#b45309">⚠ 结果已分页截断：符合条件共 ${v.total} 个月份分组，本页显示 ${v.groupCount} 组。</div>`
    : '';
  const empty = (v.rows && v.rows.length === 0)
    ? `<div class="empty">${escapeHtml(v.emptyText || '没有符合条件的月度汇总（证据数字，不代表收入或应收）。')}</div>`
    : '';

  return `${hints}${truncated}${empty}${asfmsTableHtml(v)}`;
}

function asfmsPage(delta) {
  const v = ASFMS.view;
  const page = v ? v.page + delta : ASFMS.filters.page;
  if (page < 1) return;
  asfmsPreview(page);
}

