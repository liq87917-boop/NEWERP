/* ============ 动态销售订单报表设计器（ERP-113：只读、有界的前端字段 / 筛选设计器） ============
   口径与后端 ERP-112（DynamicSalesOrderReportController / DynamicSalesOrderReportRules）一一对应：
   - 字段选择器只由 GET /api/sales-orders/report 返回的有限白名单目录（31 个字段）渲染，绝无自由填写的字段名或 SQL；
   - 筛选只允许订单日期 / 客户 / 状态 / 币种；状态与币种只接受枚举取值（下拉），客户来自既有 /api/base/customers；
   - 预览走 POST /api/sales-orders/report，只发送「白名单字段 + 有界筛选 + 有界分页」，按请求顺序渲染返回的列名与单元格；
   - 全程只读：不写库、不迁移、不执行任意 SQL；授权 / 无效请求 / 空结果 / 网络失败都在界面可见，且不暴露范围外数据。 */

/* 状态枚举（与 DocumentStatus 枚举名一致；未知取值由后端拒绝） */
const DSOR_STATUS_OPTS = [
  { value: 'Pending', label: '待提交' },
  { value: 'Submitted', label: '已提交' },
  { value: 'Approved', label: '已审核' },
  { value: 'Rejected', label: '已驳回' },
  { value: 'Completed', label: '已完成' },
  { value: 'Cancelled', label: '已取消' },
];

/* 币种枚举（与 Currency 枚举名一致；未知取值由后端拒绝） */
const DSOR_CURRENCY_OPTS = [
  { value: 'CNY', label: 'CNY 人民币' },
  { value: 'USD', label: 'USD 美元' },
  { value: 'EUR', label: 'EUR 欧元' },
  { value: 'HKD', label: 'HKD 港币' },
  { value: 'GBP', label: 'GBP 英镑' },
  { value: 'JPY', label: 'JPY 日元' },
];

/* 分组键枚举（与后端 NormalizeGroupBy 一致；未知取值由后端拒绝） */
const DSOR_GROUP_OPTS = [
  { value: 'none', label: '不分组' },
  { value: 'customer', label: '按客户分组' },
  { value: 'month', label: '按月份分组' },
];

const DSOR_STATUS_LABELS = {
  Pending: '待提交', Submitted: '已提交', Approved: '已审核',
  Rejected: '已驳回', Completed: '已完成', Cancelled: '已取消',
};

/* 默认每页条数（后端上限 200，由目录 maxPageSize 供给并钳制） */
const DSOR_DEFAULT_PAGE_SIZE = 20;

/* 设计器状态（纯数据；DOM 访问只在事件处理函数内部发生） */
let DSOR = {
  catalog: null,      // GET /api/sales-orders/report 返回的目录 DTO
  fields: [],         // 目录字段（白名单）
  selectedKeys: [],   // 当前勾选的字段键（默认全选）
  customers: [],      // 客户下拉来源（/api/base/customers）
  filters: { startDate: '', endDate: '', customerId: '', status: '', currency: '', groupBy: 'none', page: 1, pageSize: DSOR_DEFAULT_PAGE_SIZE },
  view: null,         // 最近一次预览结果
};

/* ==================== 纯函数（可在 Node 中逐条单测） ==================== */

/* HTML 转义（本地独立实现，避免依赖全局 escapeHtml 的加载顺序） */
function dsorEsc(v) {
  return String(v ?? '').replace(/[&<>"']/g, c => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}

/* 状态枚举名 → 中文文案（未知取值原样返回，不猜测） */
function dsorStatusLabel(v) {
  return DSOR_STATUS_LABELS[String(v)] || String(v);
}

/* 规范化选定字段（fail closed）：只保留目录白名单内的键、去重、保持请求顺序；未知键丢弃 */
function dsorSelectFields(catalogFields, selectedKeys) {
  const valid = new Set((catalogFields || []).map(f => f && f.key).filter(Boolean));
  const seen = new Set();
  const result = [];
  for (const k of (Array.isArray(selectedKeys) ? selectedKeys : [])) {
    if (typeof k !== 'string') continue;
    const key = k.trim();
    if (!key || !valid.has(key) || seen.has(key)) continue;
    seen.add(key);
    result.push(key);
  }
  return result;
}

/* 组装有界预览请求体：字段只来自目录、分页有界、筛选仅日期 / 客户 / 状态 / 币种，绝不接受任意字段名或 SQL */
function dsorBuildRequest(state) {
  const fields = dsorSelectFields(state.catalogFields, state.selectedKeys);
  const page = Math.max(1, Math.floor(Number(state.page) || 1));
  const maxPageSize = Number(state.maxPageSize) || 200;
  let pageSize = Math.floor(Number(state.pageSize));
  if (!Number.isFinite(pageSize)) pageSize = DSOR_DEFAULT_PAGE_SIZE;
  pageSize = Math.max(1, Math.min(maxPageSize, pageSize));

  const req = { fields, page, pageSize };

  const groupBy = DSOR_GROUP_OPTS.some(o => o.value === state.groupBy) ? state.groupBy : 'none';
  if (groupBy && groupBy !== 'none') req.groupBy = groupBy;

  const startDate = state.startDate ? String(state.startDate).slice(0, 10) : null;
  const endDate = state.endDate ? String(state.endDate).slice(0, 10) : null;
  if (startDate) req.startDate = startDate;
  if (endDate) req.endDate = endDate;

  const customerId = Number(state.customerId);
  if (Number.isFinite(customerId) && customerId > 0) req.customerId = customerId;
  if (state.status) req.status = state.status;
  if (state.currency) req.currency = state.currency;
  return req;
}

/* 单元格纯文本（安全：null/undefined 显示为空、布尔显示 是/否、日期截断到日、状态映射中文；绝不回落为 0） */
function dsorCellText(value, field) {
  const key = (field && field.key) || '';
  const dataType = (field && field.dataType) || 'text';
  if (value === null || value === undefined) return '';
  if (key === 'status') return dsorStatusLabel(value);
  if (dataType === 'boolean') return (value === true || value === 'true' || value === 1 || value === '1') ? '是' : '否';
  if (dataType === 'date') return String(value).slice(0, 10);
  return String(value);
}

/* 单元格 HTML（转义后安全渲染） */
function dsorRenderCell(value, field) {
  return dsorEsc(dsorCellText(value, field));
}
/* 结果表格 HTML：表头为返回的列名、单元格为返回的选定字段值，全部经转义 */
function dsorTableHtml(view) {
  const cols = (view && view.columns) || [];
  const rows = (view && view.rows) || [];
  if (!cols.length) return '';
  const align = c => (c.dataType === 'number' || c.dataType === 'boolean') ? ' class="text-right"' : '';
  const head = cols.map(c => `<th${align(c)}>${dsorEsc(c.label || c.key)}</th>`).join('');
  const body = rows.map(r =>
    `<tr>${cols.map(c => `<td${align(c)}>${dsorRenderCell(r[c.key], c)}</td>`).join('')}</tr>`).join('');
  const emptyRow = `<tr><td colspan="${cols.length}" class="empty">没有符合条件的销售订单</td></tr>`;
  const prevDisabled = !view || view.page <= 1 ? ' disabled' : '';
  const nextDisabled = !view || view.page >= view.totalPages ? ' disabled' : '';
  const paging = `<div style="display:flex;justify-content:space-between;align-items:center;margin-top:8px">
      <span class="text-muted">第 ${view ? view.page : 1} 页 / 共 ${view ? view.totalPages : 0} 页</span>
      <div>
        <button class="btn btn-neutral btn-sm" onclick="dsorPage(-1)"${prevDisabled}>← 上一页</button>
        <button class="btn btn-neutral btn-sm" onclick="dsorPage(1)"${nextDisabled}>下一页 →</button>
      </div></div>`;
  return `<div class="table-wrap" style="margin-top:8px"><table><thead><tr>${head}</tr></thead><tbody>${body || emptyRow}</tbody></table></div>${paging}`;
}

/* 空结果提示 */
function dsorEmptyHtml() {
  return '<div class="empty" style="margin:8px 0">没有符合条件的销售订单（当前账号数据范围内的只读快照）。</div>';
}

/* 分组页面小计（ERP-114）：每个分组按币种分开，金额保留原币、绝不跨币种相加；仅当前预览页 */
function dsorGroupsHtml(view) {
  const groups = (view && view.groups) || [];
  if (!groups.length) return '';
  const rows = groups.map(g => {
    const subs = (g.subtotals || []).map(s =>
      `<span>${dsorEsc(s.currency)}：${s.count} 条 · 金额 ${dsorEsc(s.amount)}</span>`).join('　');
    return `<div>${dsorEsc(g.label || g.key)}：${subs}</div>`;
  }).join('');
  return `<div class="pd-hint" style="margin:6px 0;color:#1d4ed8;background:#eff6ff;border-color:#bfdbfe">`
    + `<b>本页小计（按币种）</b>${rows}</div>`;
}

/* 错误提示（授权 / 未登录 / 无效请求 / 网络失败分别可见，且不暴露任何数据） */
function dsorErrorHtml(kind, message) {
  const labels = {
    forbidden: '权限不足',
    unauthorized: '未登录 / 登录已过期',
    invalid: '请求无效',
    network: '网络请求失败',
    error: '预览失败',
  };
  return `<div class="pd-hint" style="color:#b91c1c;background:#fef2f2;border-color:#fecaca">
      <b>${dsorEsc(labels[kind] || '预览失败')}</b>：${dsorEsc(message || '')}</div>`;
}

/* 预览结果（口径 / 边界 / 免责文案 + 汇总 + 分组小计 + 空结果 + 表格） */
function dsorResultHtml(view) {
  const readOnly = view && view.readOnlyText ? `<div class="pd-hint">${dsorEsc(view.readOnlyText)}</div>` : '';
  const boundary = view && view.boundaryText ? `<div class="pd-hint">${dsorEsc(view.boundaryText)}</div>` : '';
  const disclaimer = view && view.disclaimerText ? `<div class="pd-hint" style="color:#64748b">${dsorEsc(view.disclaimerText)}</div>` : '';
  const summary = view
    ? `<div class="text-muted" style="margin:6px 0">共 ${view.total} 条 · 第 ${view.page} 页 · 每页 ${view.pageSize} 条</div>`
    : '';
  const groups = view ? dsorGroupsHtml(view) : '';
  const empty = view && (!view.rows || view.rows.length === 0) ? dsorEmptyHtml() : '';
  return `${readOnly}${boundary}${disclaimer}${summary}${groups}${empty}${dsorTableHtml(view)}`;
}

/* 字段选择器：仅由目录白名单渲染为复选框，无自由填写的字段名 */
function dsorFieldChooserHtml(fields, selectedKeys) {
  const selected = new Set(selectedKeys || []);
  return (fields || []).map(f => {
    const checked = selected.has(f.key) ? 'checked' : '';
    return `<label style="display:inline-flex;align-items:center;gap:4px;margin:3px 6px 3px 0;padding:2px 8px;border:1px solid #e2e8f0;border-radius:6px;background:#f8fafc;cursor:pointer">
        <input type="checkbox" name="dsor-field" value="${dsorEsc(f.key)}" ${checked} onchange="dsorSyncSelection()">
        <span>${dsorEsc(f.label || f.key)}</span></label>`;
  }).join('');
}

/* ==================== 状态 / 请求 / 渲染 ==================== */

/* 轻量请求封装：返回完整 ApiResponse 信封（保留 code），网络异常抛给调用方 */
async function dsorRequest(path, method = 'GET', body = null) {
  const headers = { 'Content-Type': 'application/json' };
  const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
  if (token) headers['Authorization'] = 'Bearer ' + token;
  const opts = { method, headers };
  if (body) opts.body = JSON.stringify(body);
  const resp = await fetch(path, opts);
  return await resp.json();
}

function dsorLoadingHtml() {
  return '<div class="pd-hint" style="text-align:center;color:#64748b">正在预览（只读查询）…</div>';
}

function dsorRenderResult(html) {
  const el = document.getElementById('dsor-result');
  if (el) el.innerHTML = html;
}

/* 同步勾选状态到 selectedKeys（复选框 onchange） */
function dsorSyncSelection() {
  const boxes = document.querySelectorAll('input[name="dsor-field"]');
  DSOR.selectedKeys = Array.from(boxes).filter(b => b.checked).map(b => b.value);
}

function dsorToggleAll(checked) {
  const boxes = document.querySelectorAll('input[name="dsor-field"]');
  DSOR.selectedKeys = [];
  boxes.forEach(b => { b.checked = checked; if (checked) DSOR.selectedKeys.push(b.value); });
}
/* 从销售订单页打开设计器（加载目录 + 客户，渲染字段选择器与筛选器） */
async function openDynamicSalesOrderReport() {
  DSOR = {
    catalog: null, fields: [], selectedKeys: [], customers: [],
    filters: { startDate: '', endDate: '', customerId: '', status: '', currency: '', groupBy: 'none', page: 1, pageSize: DSOR_DEFAULT_PAGE_SIZE },
    view: null,
  };
  const modal = document.getElementById('modal');
  modal.innerHTML = '<div class="modal modal-lg" style="max-width:1100px"><div class="pd-hint" style="text-align:center;color:#64748b">正在加载字段目录…</div></div>';
  modal.style.display = 'flex';

  // 1) 目录（需登录 + 销售订单菜单授权；授权失败 fail closed，不返回任何字段）
  try {
    const resp = await dsorRequest('/api/sales-orders/report');
    if (resp.code === 2000 || resp.code === 2003) {
      if (typeof logout === 'function') logout();
      modal.innerHTML = dsorErrorModalHtml('unauthorized', resp.message);
      return;
    }
    if (resp.code !== 0) {
      modal.innerHTML = dsorErrorModalHtml(dsorKindOfCode(resp.code), resp.message);
      return;
    }
    DSOR.catalog = resp.data;
  } catch (err) {
    modal.innerHTML = dsorErrorModalHtml('network', (err && err.message) || '无法连接到服务器');
    return;
  }

  DSOR.fields = (DSOR.catalog && DSOR.catalog.fields) || [];
  DSOR.selectedKeys = DSOR.fields.map(f => f.key);
  const maxPageSize = DSOR.catalog && DSOR.catalog.maxPageSize ? DSOR.catalog.maxPageSize : 200;
  DSOR.filters.pageSize = Math.min(DSOR_DEFAULT_PAGE_SIZE, maxPageSize);

  // 2) 客户下拉（尽力而为：失败仅保留「全部客户」，仍可预览）
  try {
    const cresp = await dsorRequest('/api/base/customers?page=1&pageSize=500');
    if (cresp.code === 0) {
      DSOR.customers = (cresp.data && cresp.data.items) || (Array.isArray(cresp.data) ? cresp.data : []) || [];
    }
  } catch (e) {
    DSOR.customers = [];
  }

  dsorRender();
}

/* 目录加载失败 / 授权失败时的整页错误态（带关闭） */
function dsorErrorModalHtml(kind, message) {
  return `<div class="modal modal-lg">
    <h3>📊 销售订单动态报表设计器</h3>
    ${dsorErrorHtml(kind, message)}
    <div class="modal-footer"><button class="btn btn-neutral" onclick="closeModal()">关闭</button></div>
  </div>`;
}

/* 业务码 → 错误态分类 */
function dsorKindOfCode(code) {
  if (code === 2002) return 'forbidden';
  if (code === 2000 || code === 2003) return 'unauthorized';
  return 'invalid';
}
/* 渲染设计器（字段选择器 + 有界筛选器 + 预览按钮 + 结果区） */
function dsorRender() {
  const f = DSOR.filters;
  const maxPage = DSOR.catalog && DSOR.catalog.maxPageSize ? DSOR.catalog.maxPageSize : 200;
  const customerOptions = DSOR.customers.map(c =>
    `<option value="${dsorEsc(c.id)}" ${String(c.id) === String(f.customerId) ? 'selected' : ''}>${dsorEsc(c.customerCode || '')} ${dsorEsc(c.customerName || '')}</option>`).join('');
  const statusOptions = DSOR_STATUS_OPTS.map(o =>
    `<option value="${o.value}" ${f.status === o.value ? 'selected' : ''}>${o.label}</option>`).join('');
  const currencyOptions = DSOR_CURRENCY_OPTS.map(o =>
    `<option value="${o.value}" ${f.currency === o.value ? 'selected' : ''}>${o.label}</option>`).join('');
  const groupOptions = DSOR_GROUP_OPTS.map(o =>
    `<option value="${o.value}" ${f.groupBy === o.value ? 'selected' : ''}>${o.label}</option>`).join('');

  document.getElementById('modal').innerHTML = `
  <div class="modal modal-lg" style="max-width:1100px">
    <h3>📊 销售订单动态报表设计器（只读预览）</h3>
    <div class="pd-hint">只读：仅按 ERP-112 白名单字段与有界筛选预览当前账号数据范围内的销售订单；不新增 / 修改 / 删除任何记录，不执行任意 SQL。</div>

    <div style="margin:10px 0">
      <div style="font-weight:600;margin-bottom:6px">① 选择字段（仅 ERP-112 白名单目录，无自由字段名）</div>
      <div style="margin-bottom:6px">
        <button class="btn btn-neutral btn-sm" onclick="dsorToggleAll(true)">全选</button>
        <button class="btn btn-neutral btn-sm" onclick="dsorToggleAll(false)">清空</button>
      </div>
      <div style="max-height:180px;overflow:auto;border:1px solid #e2e8f0;border-radius:8px;padding:8px">${dsorFieldChooserHtml(DSOR.fields, DSOR.selectedKeys)}</div>
    </div>

    <div style="margin:10px 0">
      <div style="font-weight:600;margin-bottom:6px">② 筛选（订单日期 / 客户 / 状态 / 币种）</div>
      <div class="form-grid" style="grid-template-columns:repeat(3,1fr);gap:10px">
        <label>订单日期从 <input type="date" id="dsor-date-from" value="${dsorEsc(f.startDate)}" style="width:100%"></label>
        <label>至 <input type="date" id="dsor-date-to" value="${dsorEsc(f.endDate)}" style="width:100%"></label>
        <label>客户 <select id="dsor-customer" style="width:100%"><option value="">全部客户</option>${customerOptions}</select></label>
        <label>状态 <select id="dsor-status" style="width:100%"><option value="">全部状态</option>${statusOptions}</select></label>
        <label>币种 <select id="dsor-currency" style="width:100%"><option value="">全部币种</option>${currencyOptions}</select></label>
        <label>分组 <select id="dsor-groupby" style="width:100%">${groupOptions}</select></label>
        <label>每页 <input type="number" id="dsor-pagesize" value="${Number(f.pageSize)}" min="1" max="${maxPage}" style="width:80px"></label>
      </div>
    </div>

    <div class="modal-footer">
      <button class="btn btn-primary" onclick="dsorPreview(1)">预览</button>
      <button class="btn btn-neutral" onclick="closeModal()">关闭</button>
    </div>
    <div id="dsor-result"></div>
  </div>`;
}
/* 预览：组装有界请求 → POST → 安全渲染列名与单元格；授权 / 无效 / 空 / 网络失败均可见 */
async function dsorPreview(page) {
  const from = document.getElementById('dsor-date-from').value;
  const to = document.getElementById('dsor-date-to').value;
  if (from && to && from > to) {
    dsorRenderResult(dsorErrorHtml('invalid', '开始日期不能晚于结束日期'));
    return;
  }

  const state = {
    catalogFields: DSOR.fields,
    selectedKeys: DSOR.selectedKeys,
    startDate: from,
    endDate: to,
    customerId: document.getElementById('dsor-customer').value,
    status: document.getElementById('dsor-status').value,
    currency: document.getElementById('dsor-currency').value,
    groupBy: document.getElementById('dsor-groupby').value,
    pageSize: document.getElementById('dsor-pagesize').value,
    page: page || 1,
    maxPageSize: DSOR.catalog && DSOR.catalog.maxPageSize ? DSOR.catalog.maxPageSize : 200,
  };
  const req = dsorBuildRequest(state);
  DSOR.filters.page = req.page;
  DSOR.filters.pageSize = req.pageSize;

  dsorRenderResult(dsorLoadingHtml());

  try {
    const resp = await dsorRequest('/api/sales-orders/report', 'POST', req);
    if (resp.code === 0) {
      DSOR.view = resp.data;
      DSOR.filters.page = resp.data.page;
      dsorRenderResult(dsorResultHtml(resp.data));
    } else if (resp.code === 2000 || resp.code === 2003) {
      if (typeof logout === 'function') logout();
      dsorRenderResult(dsorErrorHtml('unauthorized', resp.message));
    } else {
      dsorRenderResult(dsorErrorHtml(dsorKindOfCode(resp.code), resp.message));
    }
  } catch (err) {
    dsorRenderResult(dsorErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 翻页（有界：最小第 1 页） */
function dsorPage(delta) {
  const view = DSOR.view;
  const page = (view ? view.page : DSOR.filters.page) + delta;
  if (page < 1) return;
  dsorPreview(page);
}

/* Node 单测导出（浏览器中 module 为 undefined，自动跳过） */
if (typeof module !== 'undefined' && module.exports) {
  module.exports = {
    DSOR_STATUS_OPTS,
    DSOR_CURRENCY_OPTS,
    DSOR_GROUP_OPTS,
    DSOR_STATUS_LABELS,
    dsorEsc,
    dsorStatusLabel,
    dsorSelectFields,
    dsorBuildRequest,
    dsorCellText,
    dsorRenderCell,
    dsorTableHtml,
    dsorEmptyHtml,
    dsorGroupsHtml,
    dsorErrorHtml,
    dsorResultHtml,
    dsorFieldChooserHtml,
  };
}
