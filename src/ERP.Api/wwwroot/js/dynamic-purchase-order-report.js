/* ============ 动态采购订单报表设计器（ERP-126：只读、有界的前端字段 / 筛选设计器） ============
   口径与后端 ERP-125（DynamicPurchaseOrderReportController / DynamicPurchaseOrderReportRules）一一对应：
   - 字段选择器只由 GET /api/purchase-orders/report 返回的有限白名单目录（24 个字段）渲染，绝无自由填写的字段名或 SQL；
   - 筛选只允许供应商 / 订单日期 / 状态 / 币种；状态与币种只接受枚举取值（下拉），供应商来自既有 /api/base/suppliers；
   - 预览走 POST /api/purchase-orders/report，只发送「白名单字段 + 有界筛选 + 有界分页（pageSize 1~100）」，按请求顺序渲染返回的列名与单元格；
   - 全程只读：不写库、不迁移、不执行任意 SQL；授权 / 无效请求 / 空结果 / 网络失败都在界面可见，且不暴露范围外数据。 */

/* 状态枚举（与 DocumentStatus 枚举名一致；未知取值由后端拒绝） */
const DPOR_STATUS_OPTS = [
  { value: 'Pending', label: '待提交' },
  { value: 'Submitted', label: '已提交' },
  { value: 'Approved', label: '已审核' },
  { value: 'Rejected', label: '已驳回' },
  { value: 'Completed', label: '已完成' },
  { value: 'Cancelled', label: '已取消' },
];

/* 币种枚举（与 Currency 枚举名一致；未知取值由后端拒绝） */
const DPOR_CURRENCY_OPTS = [
  { value: 'CNY', label: 'CNY 人民币' },
  { value: 'USD', label: 'USD 美元' },
  { value: 'EUR', label: 'EUR 欧元' },
  { value: 'HKD', label: 'HKD 港币' },
  { value: 'GBP', label: 'GBP 英镑' },
  { value: 'JPY', label: 'JPY 日元' },
];

/* 分组键枚举（与后端 NormalizeGroupBy 一致；未知取值由后端拒绝） */
const DPOR_GROUP_OPTS = [
  { value: 'none', label: '不分组' },
  { value: 'supplier', label: '按供应商分组' },
  { value: 'month', label: '按月份分组' },
];

const DPOR_STATUS_LABELS = {
  Pending: '待提交', Submitted: '已提交', Approved: '已审核',
  Rejected: '已驳回', Completed: '已完成', Cancelled: '已取消',
};

/* 默认每页条数（后端上限 100，由目录 maxPageSize 供给并钳制） */
const DPOR_DEFAULT_PAGE_SIZE = 20;

/* 设计器状态（纯数据；DOM 访问只在事件处理函数内部发生） */
let DPOR = {
  catalog: null,      // GET /api/purchase-orders/report 返回的目录 DTO
  fields: [],         // 目录字段（白名单）
  selectedKeys: [],   // 当前勾选的字段键（默认全选）
  suppliers: [],      // 供应商下拉来源（/api/base/suppliers）
  filters: { startDate: '', endDate: '', supplierId: '', status: '', currency: '', groupBy: 'none', page: 1, pageSize: DPOR_DEFAULT_PAGE_SIZE },
  view: null,         // 最近一次预览结果
};

/* ==================== 纯函数（可在 Node 中逐条单测） ==================== */

/* HTML 转义（本地独立实现，避免依赖全局 escapeHtml 的加载顺序） */
function dporEsc(v) {
  return String(v ?? '').replace(/[&<>"']/g, c => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}

/* 状态枚举名 → 中文文案（未知取值原样返回，不猜测） */
function dporStatusLabel(v) {
  return DPOR_STATUS_LABELS[String(v)] || String(v);
}

/* 规范化选定字段（fail closed）：只保留目录白名单内的键、去重、保持请求顺序；未知键丢弃 */
function dporSelectFields(catalogFields, selectedKeys) {
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

/* 组装有界预览请求体：字段只来自目录、分页有界、筛选仅供应商 / 日期 / 状态 / 币种，绝不接受任意字段名或 SQL */
function dporBuildRequest(state) {
  const fields = dporSelectFields(state.catalogFields, state.selectedKeys);
  const page = Math.max(1, Math.floor(Number(state.page) || 1));
  const maxPageSize = Number(state.maxPageSize) || 100;
  let pageSize = Math.floor(Number(state.pageSize));
  if (!Number.isFinite(pageSize)) pageSize = DPOR_DEFAULT_PAGE_SIZE;
  pageSize = Math.max(1, Math.min(maxPageSize, pageSize));

  const req = { fields, page, pageSize };

  const groupBy = DPOR_GROUP_OPTS.some(o => o.value === state.groupBy) ? state.groupBy : 'none';
  if (groupBy && groupBy !== 'none') req.groupBy = groupBy;

  const startDate = state.startDate ? String(state.startDate).slice(0, 10) : null;
  const endDate = state.endDate ? String(state.endDate).slice(0, 10) : null;
  if (startDate) req.startDate = startDate;
  if (endDate) req.endDate = endDate;

  const supplierId = Number(state.supplierId);
  if (Number.isFinite(supplierId) && supplierId > 0) req.supplierId = supplierId;
  if (state.status) req.status = state.status;
  if (state.currency) req.currency = state.currency;
  return req;
}

/* 单元格纯文本（安全：null/undefined 显示为空、布尔显示 是/否、日期截断到日、状态映射中文；绝不回落为 0） */
function dporCellText(value, field) {
  const key = (field && field.key) || '';
  const dataType = (field && field.dataType) || 'text';
  if (value === null || value === undefined) return '';
  if (key === 'status') return dporStatusLabel(value);
  if (dataType === 'boolean') return (value === true || value === 'true' || value === 1 || value === '1') ? '是' : '否';
  if (dataType === 'date') return String(value).slice(0, 10);
  return String(value);
}

/* 单元格 HTML（转义后安全渲染） */
function dporRenderCell(value, field) {
  return dporEsc(dporCellText(value, field));
}

/* 结果表格 HTML：表头为返回的列名、单元格为返回的选定字段值，全部经转义 */
function dporTableHtml(view) {
  const cols = (view && view.columns) || [];
  const rows = (view && view.rows) || [];
  if (!cols.length) return '';
  const align = c => (c.dataType === 'number' || c.dataType === 'boolean') ? ' class="text-right"' : '';
  const head = cols.map(c => `<th${align(c)}>${dporEsc(c.label || c.key)}</th>`).join('');
  const body = rows.map(r =>
    `<tr>${cols.map(c => `<td${align(c)}>${dporRenderCell(r[c.key], c)}</td>`).join('')}</tr>`).join('');
  const emptyRow = `<tr><td colspan="${cols.length}" class="empty">没有符合条件的采购订单</td></tr>`;
  const prevDisabled = !view || view.page <= 1 ? ' disabled' : '';
  const nextDisabled = !view || view.page >= view.totalPages ? ' disabled' : '';
  const paging = `<div style="display:flex;justify-content:space-between;align-items:center;margin-top:8px">
      <span class="text-muted">第 ${view ? view.page : 1} 页 / 共 ${view ? view.totalPages : 0} 页</span>
      <div>
        <button class="btn btn-neutral btn-sm" onclick="dporPage(-1)"${prevDisabled}>← 上一页</button>
        <button class="btn btn-neutral btn-sm" onclick="dporPage(1)"${nextDisabled}>下一页 →</button>
      </div></div>`;
  return `<div class="table-wrap" style="margin-top:8px"><table><thead><tr>${head}</tr></thead><tbody>${body || emptyRow}</tbody></table></div>${paging}`;
}

/* 空结果提示 */
function dporEmptyHtml() {
  return '<div class="empty" style="margin:8px 0">没有符合条件的采购订单（当前账号数据范围内的只读快照）。</div>';
}

/* 分组页面小计（ERP-128）：每个分组按币种分开，金额保留原币、绝不跨币种相加；仅当前预览页 */
function dporGroupsHtml(view) {
  const groups = (view && view.groups) || [];
  if (!groups.length) return '';
  const rows = groups.map(g => {
    const subs = (g.subtotals || []).map(s =>
      `<span>${dporEsc(s.currency)}：${s.count} 条 · 金额 ${dporEsc(s.amount)}</span>`).join('　');
    return `<div>${dporEsc(g.label || g.key)}：${subs}</div>`;
  }).join('');
  return `<div class="pd-hint" style="margin:6px 0;color:#1d4ed8;background:#eff6ff;border-color:#bfdbfe">`
    + `<b>本页小计（按币种，仅当前页）</b>${rows}</div>`;
}

/* 错误提示（授权 / 未登录 / 无效请求 / 网络失败分别可见，且不暴露任何数据） */
function dporErrorHtml(kind, message) {
  const labels = {
    forbidden: '权限不足',
    unauthorized: '未登录 / 登录已过期',
    invalid: '请求无效',
    empty: '导出内容为空',
    network: '网络请求失败',
    error: '预览失败',
  };
  return `<div class="pd-hint" style="color:#b91c1c;background:#fef2f2;border-color:#fecaca">
      <b>${dporEsc(labels[kind] || '预览失败')}</b>：${dporEsc(message || '')}</div>`;
}

/* 预览结果（口径 / 边界 / 免责文案 + 汇总 + 空结果 + 表格） */
function dporResultHtml(view) {
  const readOnly = view && view.readOnlyText ? `<div class="pd-hint">${dporEsc(view.readOnlyText)}</div>` : '';
  const boundary = view && view.boundaryText ? `<div class="pd-hint">${dporEsc(view.boundaryText)}</div>` : '';
  const disclaimer = view && view.disclaimerText ? `<div class="pd-hint" style="color:#64748b">${dporEsc(view.disclaimerText)}</div>` : '';
  const summary = view
    ? `<div class="text-muted" style="margin:6px 0">共 ${view.total} 条 · 第 ${view.page} 页 · 每页 ${view.pageSize} 条</div>`
    : '';
  const groups = view ? dporGroupsHtml(view) : '';
  const empty = view && (!view.rows || view.rows.length === 0) ? dporEmptyHtml() : '';
  return `${readOnly}${boundary}${disclaimer}${summary}${groups}${empty}${dporTableHtml(view)}`;
}

/* 字段选择器：仅由目录白名单渲染为复选框，无自由填写的字段名 */
function dporFieldChooserHtml(fields, selectedKeys) {
  const selected = new Set(selectedKeys || []);
  return (fields || []).map(f => {
    const checked = selected.has(f.key) ? 'checked' : '';
    return `<label style="display:inline-flex;align-items:center;gap:4px;margin:3px 6px 3px 0;padding:2px 8px;border:1px solid #e2e8f0;border-radius:6px;background:#f8fafc;cursor:pointer">
        <input type="checkbox" name="dpor-field" value="${dporEsc(f.key)}" ${checked} onchange="dporSyncSelection()">
        <span>${dporEsc(f.label || f.key)}</span></label>`;
  }).join('');
}

/* ==================== 状态 / 请求 / 渲染 ==================== */

/* 轻量请求封装：返回完整 ApiResponse 信封（保留 code），网络异常抛给调用方 */
async function dporRequest(path, method = 'GET', body = null) {
  const headers = { 'Content-Type': 'application/json' };
  const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
  if (token) headers['Authorization'] = 'Bearer ' + token;
  const opts = { method, headers };
  if (body) opts.body = JSON.stringify(body);
  const resp = await fetch(path, opts);
  return await resp.json();
}

function dporLoadingHtml() {
  return '<div class="pd-hint" style="text-align:center;color:#64748b">正在预览（只读查询）…</div>';
}

function dporRenderResult(html) {
  const el = document.getElementById('dpor-result');
  if (el) el.innerHTML = html;
}

/* 同步勾选状态到 selectedKeys（复选框 onchange） */
function dporSyncSelection() {
  const boxes = document.querySelectorAll('input[name="dpor-field"]');
  DPOR.selectedKeys = Array.from(boxes).filter(b => b.checked).map(b => b.value);
}

function dporToggleAll(checked) {
  const boxes = document.querySelectorAll('input[name="dpor-field"]');
  DPOR.selectedKeys = [];
  boxes.forEach(b => { b.checked = checked; if (checked) DPOR.selectedKeys.push(b.value); });
}

/* 从采购订单页打开设计器（加载目录 + 供应商，渲染字段选择器与筛选器） */
async function openDynamicPurchaseOrderReport() {
  DPOR = {
    catalog: null, fields: [], selectedKeys: [], suppliers: [],
    filters: { startDate: '', endDate: '', supplierId: '', status: '', currency: '', groupBy: 'none', page: 1, pageSize: DPOR_DEFAULT_PAGE_SIZE },
    view: null,
  };
  const modal = document.getElementById('modal');
  modal.innerHTML = '<div class="modal modal-lg" style="max-width:1100px"><div class="pd-hint" style="text-align:center;color:#64748b">正在加载字段目录…</div></div>';
  modal.style.display = 'flex';

  // 1) 目录（需登录 + 采购订单菜单授权；授权失败 fail closed，不返回任何字段）
  try {
    const resp = await dporRequest('/api/purchase-orders/report');
    if (resp.code === 2000 || resp.code === 2003) {
      if (typeof logout === 'function') logout();
      modal.innerHTML = dporErrorModalHtml('unauthorized', resp.message);
      return;
    }
    if (resp.code !== 0) {
      modal.innerHTML = dporErrorModalHtml(dporKindOfCode(resp.code), resp.message);
      return;
    }
    DPOR.catalog = resp.data;
  } catch (err) {
    modal.innerHTML = dporErrorModalHtml('network', (err && err.message) || '无法连接到服务器');
    return;
  }

  DPOR.fields = (DPOR.catalog && DPOR.catalog.fields) || [];
  DPOR.selectedKeys = DPOR.fields.map(f => f.key);
  const maxPageSize = DPOR.catalog && DPOR.catalog.maxPageSize ? DPOR.catalog.maxPageSize : 100;
  DPOR.filters.pageSize = Math.min(DPOR_DEFAULT_PAGE_SIZE, maxPageSize);

  // 2) 供应商下拉（尽力而为：失败仅保留「全部供应商」，仍可预览）
  try {
    const cresp = await dporRequest('/api/base/suppliers?page=1&pageSize=200');
    if (cresp.code === 0) {
      DPOR.suppliers = (cresp.data && cresp.data.items) || (Array.isArray(cresp.data) ? cresp.data : []) || [];
    }
  } catch (e) {
    DPOR.suppliers = [];
  }

  dporRender();
}

/* 目录加载失败 / 授权失败时的整页错误态（带关闭） */
function dporErrorModalHtml(kind, message) {
  return `<div class="modal modal-lg">
    <h3>📊 采购订单动态报表设计器</h3>
    ${dporErrorHtml(kind, message)}
    <div class="modal-footer"><button class="btn btn-neutral" onclick="closeModal()">关闭</button></div>
  </div>`;
}

/* 业务码 → 错误态分类 */
function dporKindOfCode(code) {
  if (code === 2002) return 'forbidden';
  if (code === 2000 || code === 2003) return 'unauthorized';
  return 'invalid';
}

/* 渲染设计器（字段选择器 + 有界筛选器 + 预览按钮 + 结果区） */
function dporRender() {
  const f = DPOR.filters;
  const maxPage = DPOR.catalog && DPOR.catalog.maxPageSize ? DPOR.catalog.maxPageSize : 100;
  const supplierOptions = DPOR.suppliers.map(s =>
    `<option value="${dporEsc(s.id)}" ${String(s.id) === String(f.supplierId) ? 'selected' : ''}>${dporEsc(s.supplierCode || '')} ${dporEsc(s.supplierName || '')}</option>`).join('');
  const statusOptions = DPOR_STATUS_OPTS.map(o =>
    `<option value="${o.value}" ${f.status === o.value ? 'selected' : ''}>${o.label}</option>`).join('');
  const currencyOptions = DPOR_CURRENCY_OPTS.map(o =>
    `<option value="${o.value}" ${f.currency === o.value ? 'selected' : ''}>${o.label}</option>`).join('');
  const groupOptions = DPOR_GROUP_OPTS.map(o =>
    `<option value="${o.value}" ${f.groupBy === o.value ? 'selected' : ''}>${o.label}</option>`).join('');

  document.getElementById('modal').innerHTML = `
  <div class="modal modal-lg" style="max-width:1100px">
    <h3>📊 采购订单动态报表设计器（只读预览）</h3>
    <div class="pd-hint">只读：仅按 ERP-125 白名单字段与有界筛选预览当前账号数据范围内的采购订单；不新增 / 修改 / 删除任何记录，不执行任意 SQL。</div>

    <div style="margin:10px 0">
      <div style="font-weight:600;margin-bottom:6px">① 选择字段（仅 ERP-125 白名单目录，无自由字段名）</div>
      <div style="margin-bottom:6px">
        <button class="btn btn-neutral btn-sm" onclick="dporToggleAll(true)">全选</button>
        <button class="btn btn-neutral btn-sm" onclick="dporToggleAll(false)">清空</button>
      </div>
      <div style="max-height:180px;overflow:auto;border:1px solid #e2e8f0;border-radius:8px;padding:8px">${dporFieldChooserHtml(DPOR.fields, DPOR.selectedKeys)}</div>
    </div>

    <div style="margin:10px 0">
      <div style="font-weight:600;margin-bottom:6px">② 筛选（供应商 / 订单日期 / 状态 / 币种）</div>
      <div class="form-grid" style="grid-template-columns:repeat(3,1fr);gap:10px">
        <label>订单日期从 <input type="date" id="dpor-date-from" value="${dporEsc(f.startDate)}" style="width:100%"></label>
        <label>至 <input type="date" id="dpor-date-to" value="${dporEsc(f.endDate)}" style="width:100%"></label>
        <label>供应商 <select id="dpor-supplier" style="width:100%"><option value="">全部供应商</option>${supplierOptions}</select></label>
        <label>状态 <select id="dpor-status" style="width:100%"><option value="">全部状态</option>${statusOptions}</select></label>
        <label>币种 <select id="dpor-currency" style="width:100%"><option value="">全部币种</option>${currencyOptions}</select></label>
        <label>分组 <select id="dpor-groupby" style="width:100%">${groupOptions}</select></label>
        <label>每页 <input type="number" id="dpor-pagesize" value="${Number(f.pageSize)}" min="1" max="${maxPage}" style="width:80px"></label>
      </div>
    </div>

    <div class="modal-footer">
      <button class="btn btn-primary" onclick="dporPreview(1)">预览</button>
      <button class="btn btn-neutral" onclick="dporExport()">📥 导出 Excel</button>
      <button class="btn btn-neutral" onclick="dporExportPdf()">📄 导出 PDF</button>
      <button class="btn btn-neutral" onclick="closeModal()">关闭</button>
    </div>
    <div id="dpor-result"></div>
  </div>`;
}

/* 读取当前字段 / 筛选 / 分页状态（预览与分页复用，单一来源） */
function dporBuildState(page) {
  return {
    catalogFields: DPOR.fields,
    selectedKeys: DPOR.selectedKeys,
    startDate: document.getElementById('dpor-date-from').value,
    endDate: document.getElementById('dpor-date-to').value,
    supplierId: document.getElementById('dpor-supplier').value,
    status: document.getElementById('dpor-status').value,
    currency: document.getElementById('dpor-currency').value,
    groupBy: document.getElementById('dpor-groupby').value,
    pageSize: document.getElementById('dpor-pagesize').value,
    page: page || 1,
    maxPageSize: DPOR.catalog && DPOR.catalog.maxPageSize ? DPOR.catalog.maxPageSize : 100,
  };
}

/* 预览：组装有界请求 → POST → 安全渲染列名与单元格；授权 / 无效 / 空 / 网络失败均可见 */
async function dporPreview(page) {
  const state = dporBuildState(page);
  if (state.startDate && state.endDate && state.startDate > state.endDate) {
    dporRenderResult(dporErrorHtml('invalid', '开始日期不能晚于结束日期'));
    return;
  }

  const req = dporBuildRequest(state);
  DPOR.filters.page = req.page;
  DPOR.filters.pageSize = req.pageSize;

  dporRenderResult(dporLoadingHtml());

  try {
    const resp = await dporRequest('/api/purchase-orders/report', 'POST', req);
    if (resp.code === 0) {
      DPOR.view = resp.data;
      DPOR.filters.page = resp.data.page;
      dporRenderResult(dporResultHtml(resp.data));
    } else if (resp.code === 2000 || resp.code === 2003) {
      if (typeof logout === 'function') logout();
      dporRenderResult(dporErrorHtml('unauthorized', resp.message));
    } else {
      dporRenderResult(dporErrorHtml(dporKindOfCode(resp.code), resp.message));
    }
  } catch (err) {
    dporRenderResult(dporErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 翻页（有界：最小第 1 页） */
function dporPage(delta) {
  const view = DPOR.view;
  const page = (view ? view.page : DPOR.filters.page) + delta;
  if (page < 1) return;
  dporPreview(page);
}

/* 导出当前页为 Excel（ERP-127，只读）：复用预览请求体 POST /api/purchase-orders/report/export；
   成功（xlsx 附件）触发下载；授权 / 无效 / 空结果 / 网络失败在结果区可见，不下载任何内容 */
async function dporExport() {
  const state = dporBuildState(DPOR.view ? DPOR.view.page : DPOR.filters.page);
  if (state.startDate && state.endDate && state.startDate > state.endDate) {
    dporRenderResult(dporErrorHtml('invalid', '开始日期不能晚于结束日期'));
    return;
  }

  // 当前页为空：显示可见错误，不下载仅表头的空工作簿
  if (DPOR.view && (!DPOR.view.rows || DPOR.view.rows.length === 0)) {
    dporRenderResult(dporErrorHtml('empty', '没有符合条件的采购订单，无法导出'));
    return;
  }

  const req = dporBuildRequest(state);

  try {
    const resp = await fetch('/api/purchase-orders/report/export', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': 'Bearer ' + (typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : ''),
      },
      body: JSON.stringify(req),
    });

    const contentType = (resp.headers.get('content-type') || '');
    if (contentType.indexOf('spreadsheetml') >= 0) {
      const blob = await resp.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      const dateStr = new Date().toISOString().slice(0, 10).replace(/-/g, '');
      a.href = url;
      a.download = '采购订单报表_' + dateStr + '.xlsx';
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
      return;
    }

    let envelope = null;
    try { envelope = await resp.json(); } catch (e) { /* 忽略解析失败 */ }
    const code = envelope && envelope.code;
    const message = (envelope && envelope.message) || '导出失败';
    if (code === 2000 || code === 2003) {
      if (typeof logout === 'function') logout();
      dporRenderResult(dporErrorHtml('unauthorized', message));
      return;
    }
    dporRenderResult(dporErrorHtml(dporKindOfCode(code), message));
  } catch (err) {
    dporRenderResult(dporErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 导出当前页为 PDF（ERP-129，只读）：复用预览请求体 POST /api/purchase-orders/report/export/pdf；
   成功（pdf 附件）触发下载；授权 / 无效 / 字体缺失 / 网络失败在结果区可见，不下载任何内容 */
async function dporExportPdf() {
  const state = dporBuildState(DPOR.view ? DPOR.view.page : DPOR.filters.page);
  if (state.startDate && state.endDate && state.startDate > state.endDate) {
    dporRenderResult(dporErrorHtml('invalid', '开始日期不能晚于结束日期'));
    return;
  }

  // 当前页为空：显示可见错误，不下载仅表头的空 PDF
  if (DPOR.view && (!DPOR.view.rows || DPOR.view.rows.length === 0)) {
    dporRenderResult(dporErrorHtml('empty', '没有符合条件的采购订单，无法导出'));
    return;
  }

  const req = dporBuildRequest(state);

  try {
    const resp = await fetch('/api/purchase-orders/report/export/pdf', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': 'Bearer ' + (typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : ''),
      },
      body: JSON.stringify(req),
    });

    const contentType = (resp.headers.get('content-type') || '');
    if (contentType.indexOf('application/pdf') >= 0) {
      const blob = await resp.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      const dateStr = new Date().toISOString().slice(0, 10).replace(/-/g, '');
      a.href = url;
      a.download = '采购订单报表_' + dateStr + '.pdf';
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
      return;
    }

    let envelope = null;
    try { envelope = await resp.json(); } catch (e) { /* 忽略解析失败 */ }
    const code = envelope && envelope.code;
    const message = (envelope && envelope.message) || 'PDF 导出失败';
    if (code === 2000 || code === 2003) {
      if (typeof logout === 'function') logout();
      dporRenderResult(dporErrorHtml('unauthorized', message));
      return;
    }
    dporRenderResult(dporErrorHtml(dporKindOfCode(code), message));
  } catch (err) {
    dporRenderResult(dporErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* Node 单测导出（浏览器中 module 为 undefined，自动跳过） */
if (typeof module !== 'undefined' && module.exports) {
  module.exports = {
    DPOR_STATUS_OPTS,
    DPOR_CURRENCY_OPTS,
    DPOR_GROUP_OPTS,
    DPOR_STATUS_LABELS,
    dporEsc,
    dporStatusLabel,
    dporSelectFields,
    dporBuildRequest,
    dporCellText,
    dporRenderCell,
    dporTableHtml,
    dporEmptyHtml,
    dporGroupsHtml,
    dporErrorHtml,
    dporResultHtml,
    dporFieldChooserHtml,
    dporKindOfCode,
    dporErrorModalHtml,
    dporPreview,
    dporPage,
    dporExport,
    dporExportPdf,
  };
}




