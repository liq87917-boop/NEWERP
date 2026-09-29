/* ============ 动态客户应收账款证据报表设计器（ERP-118：只读、有界的前端字段 / 筛选设计器） ============
   口径与后端 ERP-117（DynamicReceivableReportController / DynamicReceivableReportRules）一一对应：
   - 字段选择器只由 GET /api/dynamic-receivable-report 返回的有限白名单目录（29 个字段）渲染，绝无自由填写的字段名或 SQL；
   - 筛选只允许客户 / 开票日期 / 币种 / 分配状态 / 发票状态；币种、分配状态与发票状态只接受枚举取值（下拉），客户来自既有 /api/base/customers；
   - 预览走 POST /api/dynamic-receivable-report，只发送「白名单字段 + 有界筛选 + 有界分页（pageSize 1~100）」，按请求顺序渲染返回的列名与单元格；
   - 剩余证据状态（known / unknown / over_allocated）与分配状态单独着色标注，绝不掩盖 unknown / over_allocated，也不把 null 金额回落为 0；
   - 全程只读：不写库、不迁移、不执行任意 SQL；授权 / 无效请求 / 空结果 / 网络失败都在界面可见，且不暴露范围外数据。 */

/* 币种枚举（与系统 Currency 枚举名一致；未知取值由后端拒绝） */
const DSR_CURRENCY_OPTS = [
  { value: 'CNY', label: 'CNY 人民币' },
  { value: 'USD', label: 'USD 美元' },
  { value: 'EUR', label: 'EUR 欧元' },
  { value: 'HKD', label: 'HKD 港币' },
  { value: 'GBP', label: 'GBP 英镑' },
  { value: 'JPY', label: 'JPY 日元' },
];

/* 分配状态枚举（仅可筛选的四种持久化派生状态；over_allocated / unknown 只在读取时派生，不提供为筛选） */
const DSR_ALLOCATION_OPTS = [
  { value: 'none', label: '无分摊行（证据缺口）' },
  { value: 'historical_only', label: '仅有历史/无效分摊行' },
  { value: 'partial', label: '部分分摊' },
  { value: 'full', label: '全额分摊' },
];

/* 发票状态枚举（默认 recorded；未知取值由后端拒绝） */
const DSR_INVOICE_STATUS_OPTS = [
  { value: 'recorded', label: '已登记（有效证据）' },
  { value: 'draft', label: '草稿（单列）' },
  { value: 'voided', label: '已作废（历史证据单列）' },
  { value: 'all', label: '全部状态' },
];

/* 剩余证据状态中文文案（与后端 RemainingStateText 同源，短文案便于着色标注） */
const DSR_REMAINING_STATE_LABELS = {
  known: '剩余可确认',
  unknown: '剩余未知',
  over_allocated: '超额分摊（无效）',
};

/* 分配状态中文文案（与后端 AllocationStateText 同源，短文案） */
const DSR_ALLOCATION_STATE_LABELS = {
  none: '无分摊行',
  historical_only: '仅历史/无效分摊',
  partial: '部分分摊',
  full: '全额分摊',
  over_allocated: '超额分摊（无效）',
  unknown: '未知',
};

/* 默认每页条数（后端上限 100，由目录 maxPageSize 供给并钳制） */
const DSR_DEFAULT_PAGE_SIZE = 20;
const DSR_MAX_PAGE_SIZE = 100;

/* 设计器状态（纯数据；DOM 访问只在事件处理函数内部发生） */
let DSR = {
  catalog: null,      // GET /api/dynamic-receivable-report 返回的目录 DTO
  fields: [],         // 目录字段（白名单）
  selectedKeys: [],   // 当前勾选的字段键（默认全选）
  customers: [],      // 客户下拉来源（/api/base/customers）
  filters: { startDate: '', endDate: '', customerId: '', currency: '', allocationState: '', invoiceStatus: 'recorded', page: 1, pageSize: DSR_DEFAULT_PAGE_SIZE },
  view: null,         // 最近一次预览结果
};

/* ==================== 纯函数（可在 Node 中逐条单测） ==================== */

/* HTML 转义（本地独立实现，避免依赖全局 escapeHtml 的加载顺序） */
function dsrEsc(v) {
  return String(v ?? '').replace(/[&<>"']/g, c => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}

/* 剩余证据状态 → 中文短文案（未知取值原样返回，不猜测） */
function dsrRemainingStateLabel(v) {
  return DSR_REMAINING_STATE_LABELS[String(v)] || String(v);
}

/* 分配状态 → 中文短文案（未知取值原样返回，不猜测） */
function dsrAllocationStateLabel(v) {
  return DSR_ALLOCATION_STATE_LABELS[String(v)] || String(v);
}
/* 规范化选定字段（fail closed）：只保留目录白名单内的键、去重、保持请求顺序；未知键丢弃 */
function dsrSelectFields(catalogFields, selectedKeys) {
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

/* 组装有界预览请求体：字段只来自目录、分页有界、筛选仅客户 / 开票日期 / 币种 / 分配状态 / 发票状态，
   币种 / 分配状态 / 发票状态只接受枚举取值，绝不接受任意字段名或 SQL */
function dsrBuildRequest(state) {
  const fields = dsrSelectFields(state.catalogFields, state.selectedKeys);
  const page = Math.max(1, Math.floor(Number(state.page) || 1));
  const maxPageSize = Number(state.maxPageSize) || DSR_MAX_PAGE_SIZE;
  let pageSize = Math.floor(Number(state.pageSize));
  if (!Number.isFinite(pageSize)) pageSize = DSR_DEFAULT_PAGE_SIZE;
  pageSize = Math.max(1, Math.min(maxPageSize, pageSize));

  const req = { fields, page, pageSize };

  const startDate = state.startDate ? String(state.startDate).slice(0, 10) : null;
  const endDate = state.endDate ? String(state.endDate).slice(0, 10) : null;
  if (startDate) req.startDate = startDate;
  if (endDate) req.endDate = endDate;

  const customerId = Number(state.customerId);
  if (Number.isFinite(customerId) && customerId > 0) req.customerId = customerId;

  if (DSR_CURRENCY_OPTS.some(o => o.value === state.currency)) req.currency = state.currency;
  if (DSR_ALLOCATION_OPTS.some(o => o.value === state.allocationState)) req.allocationState = state.allocationState;
  if (DSR_INVOICE_STATUS_OPTS.some(o => o.value === state.invoiceStatus)) req.invoiceStatus = state.invoiceStatus;
  return req;
}

/* 单元格纯文本（安全：null/undefined 显示为空、布尔显示 是/否、日期截断到日、
   剩余证据 / 分配状态映射中文短文案；绝不回落为 0） */
function dsrCellText(value, field) {
  const key = (field && field.key) || '';
  const dataType = (field && field.dataType) || 'text';
  if (value === null || value === undefined) return '';
  if (key === 'remainingState') return dsrRemainingStateLabel(value);
  if (key === 'allocationState') return dsrAllocationStateLabel(value);
  if (dataType === 'boolean') return (value === true || value === 'true' || value === 1 || value === '1') ? '是' : '否';
  if (dataType === 'date') return String(value).slice(0, 10);
  return String(value);
}

/* 剩余证据状态着色徽章（known 绿 / unknown 黄 / over_allocated 红，绝不掩盖三者差异） */
function dsrRemainingBadge(value) {
  const s = String(value);
  const map = {
    known: ['status-success', '剩余可确认'],
    unknown: ['status-warning', '剩余未知'],
    over_allocated: ['status-danger', '超额分摊（无效）'],
  };
  const m = map[s] || ['status-neutral', dsrRemainingStateLabel(s)];
  return `<span class="status ${m[0]}">${dsrEsc(m[1])}</span>`;
}

/* 分配状态着色徽章（fail closed：未知取值按中性文案展示，不猜测） */
function dsrAllocationBadge(value) {
  const s = String(value);
  const map = {
    none: ['status-neutral', '无分摊行'],
    historical_only: ['status-warning', '仅历史/无效分摊'],
    partial: ['status-warning', '部分分摊'],
    full: ['status-success', '全额分摊'],
    over_allocated: ['status-danger', '超额分摊（无效）'],
    unknown: ['status-neutral', '未知'],
  };
  const m = map[s] || ['status-neutral', dsrAllocationStateLabel(s)];
  return `<span class="status ${m[0]}">${dsrEsc(m[1])}</span>`;
}

/* 单元格 HTML（转义后安全渲染；剩余证据 / 分配状态额外着色标注） */
function dsrRenderCell(value, field) {
  const key = (field && field.key) || '';
  if (key === 'remainingState') return dsrRemainingBadge(value);
  if (key === 'allocationState') return dsrAllocationBadge(value);
  return dsrEsc(dsrCellText(value, field));
}
/* 结果表格 HTML：表头为返回的列名、单元格为返回的选定字段值，全部经转义 */
function dsrTableHtml(view) {
  const cols = (view && view.columns) || [];
  const rows = (view && view.rows) || [];
  if (!cols.length) return '';
  const align = c => (c.dataType === 'number' || c.dataType === 'boolean') ? ' class="text-right"' : '';
  const head = cols.map(c => `<th${align(c)}>${dsrEsc(c.label || c.key)}</th>`).join('');
  const body = rows.map(r =>
    `<tr>${cols.map(c => `<td${align(c)}>${dsrRenderCell(r[c.key], c)}</td>`).join('')}</tr>`).join('');
  const emptyRow = `<tr><td colspan="${cols.length}" class="empty">没有符合条件的应收账款证据</td></tr>`;
  const prevDisabled = !view || view.page <= 1 ? ' disabled' : '';
  const nextDisabled = !view || view.page >= view.totalPages ? ' disabled' : '';
  const paging = `<div style="display:flex;justify-content:space-between;align-items:center;margin-top:8px">
      <span class="text-muted">第 ${view ? view.page : 1} 页 / 共 ${view ? view.totalPages : 0} 页</span>
      <div>
        <button class="btn btn-neutral btn-sm" onclick="dsrPage(-1)"${prevDisabled}>← 上一页</button>
        <button class="btn btn-neutral btn-sm" onclick="dsrPage(1)"${nextDisabled}>下一页 →</button>
      </div></div>`;
  return `<div class="table-wrap" style="margin-top:8px"><table><thead><tr>${head}</tr></thead><tbody>${body || emptyRow}</tbody></table></div>${paging}`;
}

/* 空结果提示 */
function dsrEmptyHtml() {
  return '<div class="empty" style="margin:8px 0">没有符合条件的应收账款证据（当前账号数据范围内的只读快照）。</div>';
}

/* 错误提示（授权 / 未登录 / 无效请求 / 网络失败分别可见，且不暴露任何数据） */
function dsrErrorHtml(kind, message) {
  const labels = {
    forbidden: '权限不足',
    unauthorized: '未登录 / 登录已过期',
    invalid: '请求无效',
    network: '网络请求失败',
    error: '预览失败',
  };
  return `<div class="pd-hint" style="color:#b91c1c;background:#fef2f2;border-color:#fecaca">
      <b>${dsrEsc(labels[kind] || '预览失败')}</b>：${dsrEsc(message || '')}</div>`;
}

/* 预览结果（口径 / 边界 / 免责文案 + 汇总 + 空结果 + 表格） */
function dsrResultHtml(view) {
  const readOnly = view && view.readOnlyText ? `<div class="pd-hint">${dsrEsc(view.readOnlyText)}</div>` : '';
  const boundary = view && view.boundaryText ? `<div class="pd-hint">${dsrEsc(view.boundaryText)}</div>` : '';
  const disclaimer = view && view.disclaimerText ? `<div class="pd-hint" style="color:#64748b">${dsrEsc(view.disclaimerText)}</div>` : '';
  const summary = view
    ? `<div class="text-muted" style="margin:6px 0">共 ${view.total} 条 · 第 ${view.page} 页 · 每页 ${view.pageSize} 条</div>`
    : '';
  const empty = view && (!view.rows || view.rows.length === 0) ? dsrEmptyHtml() : '';
  return `${readOnly}${boundary}${disclaimer}${summary}${empty}${dsrTableHtml(view)}`;
}

/* 字段选择器：仅由目录白名单渲染为复选框，无自由填写的字段名 */
function dsrFieldChooserHtml(fields, selectedKeys) {
  const selected = new Set(selectedKeys || []);
  return (fields || []).map(f => {
    const checked = selected.has(f.key) ? 'checked' : '';
    return `<label style="display:inline-flex;align-items:center;gap:4px;margin:3px 6px 3px 0;padding:2px 8px;border:1px solid #e2e8f0;border-radius:6px;background:#f8fafc;cursor:pointer">
        <input type="checkbox" name="dsr-field" value="${dsrEsc(f.key)}" ${checked} onchange="dsrSyncSelection()">
        <span>${dsrEsc(f.label || f.key)}</span></label>`;
  }).join('');
}
/* ==================== 状态 / 请求 / 渲染 ==================== */

/* 轻量请求封装：返回完整 ApiResponse 信封（保留 code），网络异常抛给调用方 */
async function dsrRequest(path, method = 'GET', body = null) {
  const headers = { 'Content-Type': 'application/json' };
  const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
  if (token) headers['Authorization'] = 'Bearer ' + token;
  const opts = { method, headers };
  if (body) opts.body = JSON.stringify(body);
  const resp = await fetch(path, opts);
  return await resp.json();
}

function dsrLoadingHtml() {
  return '<div class="pd-hint" style="text-align:center;color:#64748b">正在预览（只读查询）…</div>';
}

function dsrRenderResult(html) {
  const el = document.getElementById('dsr-result');
  if (el) el.innerHTML = html;
}

/* 同步勾选状态到 selectedKeys（复选框 onchange） */
function dsrSyncSelection() {
  const boxes = document.querySelectorAll('input[name="dsr-field"]');
  DSR.selectedKeys = Array.from(boxes).filter(b => b.checked).map(b => b.value);
}

function dsrToggleAll(checked) {
  const boxes = document.querySelectorAll('input[name="dsr-field"]');
  DSR.selectedKeys = [];
  boxes.forEach(b => { b.checked = checked; if (checked) DSR.selectedKeys.push(b.value); });
}

/* 业务码 → 错误态分类 */
function dsrKindOfCode(code) {
  if (code === 2002) return 'forbidden';
  if (code === 2000 || code === 2003) return 'unauthorized';
  return 'invalid';
}

/* 目录加载失败 / 授权失败时的整页错误态（带关闭） */
function dsrErrorModalHtml(kind, message) {
  return `<div class="modal modal-lg">
    <h3>🧾 客户应收账款证据动态报表设计器</h3>
    ${dsrErrorHtml(kind, message)}
    <div class="modal-footer"><button class="btn btn-neutral" onclick="closeModal()">关闭</button></div>
  </div>`;
}

/* 从客户销项发票登记册打开设计器（加载目录 + 客户，渲染字段选择器与筛选器） */
async function openDynamicReceivableReport(customerId) {
  DSR = {
    catalog: null, fields: [], selectedKeys: [], customers: [],
    filters: {
      startDate: '', endDate: '',
      customerId: customerId ? String(customerId) : '',
      currency: '', allocationState: '', invoiceStatus: 'recorded',
      page: 1, pageSize: DSR_DEFAULT_PAGE_SIZE,
    },
    view: null,
  };
  const modal = document.getElementById('modal');
  modal.innerHTML = '<div class="modal modal-lg" style="max-width:1100px"><div class="pd-hint" style="text-align:center;color:#64748b">正在加载字段目录…</div></div>';
  modal.style.display = 'flex';

  // 1) 目录（需登录 + 客户资料菜单授权；授权失败 fail closed，不返回任何字段）
  try {
    const resp = await dsrRequest('/api/dynamic-receivable-report');
    if (resp.code === 2000 || resp.code === 2003) {
      if (typeof logout === 'function') logout();
      modal.innerHTML = dsrErrorModalHtml('unauthorized', resp.message);
      return;
    }
    if (resp.code !== 0) {
      modal.innerHTML = dsrErrorModalHtml(dsrKindOfCode(resp.code), resp.message);
      return;
    }
    DSR.catalog = resp.data;
  } catch (err) {
    modal.innerHTML = dsrErrorModalHtml('network', (err && err.message) || '无法连接到服务器');
    return;
  }

  DSR.fields = (DSR.catalog && DSR.catalog.fields) || [];
  DSR.selectedKeys = DSR.fields.map(f => f.key);
  const maxPageSize = DSR.catalog && DSR.catalog.maxPageSize ? DSR.catalog.maxPageSize : DSR_MAX_PAGE_SIZE;
  DSR.filters.pageSize = Math.min(DSR_DEFAULT_PAGE_SIZE, maxPageSize);

  // 2) 客户下拉（尽力而为：失败仅保留「全部客户」，仍可预览）
  try {
    const cresp = await dsrRequest('/api/base/customers?page=1&pageSize=500');
    if (cresp.code === 0) {
      DSR.customers = (cresp.data && cresp.data.items) || (Array.isArray(cresp.data) ? cresp.data : []) || [];
    }
  } catch (e) {
    DSR.customers = [];
  }

  dsrRender();
}
/* 渲染设计器（字段选择器 + 有界筛选器 + 预览按钮 + 结果区） */
function dsrRender() {
  const f = DSR.filters;
  const maxPage = DSR.catalog && DSR.catalog.maxPageSize ? DSR.catalog.maxPageSize : DSR_MAX_PAGE_SIZE;
  const customerOptions = DSR.customers.map(c =>
    `<option value="${dsrEsc(c.id)}" ${String(c.id) === String(f.customerId) ? 'selected' : ''}>${dsrEsc(c.customerCode || '')} ${dsrEsc(c.customerName || '')}</option>`).join('');
  const currencyOptions = DSR_CURRENCY_OPTS.map(o =>
    `<option value="${o.value}" ${f.currency === o.value ? 'selected' : ''}>${o.label}</option>`).join('');
  const allocationOptions = DSR_ALLOCATION_OPTS.map(o =>
    `<option value="${o.value}" ${f.allocationState === o.value ? 'selected' : ''}>${o.label}</option>`).join('');
  const invoiceStatusOptions = DSR_INVOICE_STATUS_OPTS.map(o =>
    `<option value="${o.value}" ${f.invoiceStatus === o.value ? 'selected' : ''}>${o.label}</option>`).join('');

  document.getElementById('modal').innerHTML = `
  <div class="modal modal-lg" style="max-width:1100px">
    <h3>🧾 客户应收账款证据动态报表设计器（只读预览）</h3>
    <div class="pd-hint">只读：仅按 ERP-117 白名单字段与有界筛选预览当前账号数据范围内的客户销项发票证据与收款分摊证据；不新增 / 修改 / 删除任何记录，不执行任意 SQL。</div>

    <div style="margin:10px 0">
      <div style="font-weight:600;margin-bottom:6px">① 选择字段（仅 ERP-117 白名单目录，无自由字段名）</div>
      <div style="margin-bottom:6px">
        <button class="btn btn-neutral btn-sm" onclick="dsrToggleAll(true)">全选</button>
        <button class="btn btn-neutral btn-sm" onclick="dsrToggleAll(false)">清空</button>
      </div>
      <div style="max-height:180px;overflow:auto;border:1px solid #e2e8f0;border-radius:8px;padding:8px">${dsrFieldChooserHtml(DSR.fields, DSR.selectedKeys)}</div>
    </div>

    <div style="margin:10px 0">
      <div style="font-weight:600;margin-bottom:6px">② 筛选（客户 / 开票日期 / 币种 / 分配状态 / 发票状态）</div>
      <div class="form-grid" style="grid-template-columns:repeat(3,1fr);gap:10px">
        <label>开票日期从 <input type="date" id="dsr-date-from" value="${dsrEsc(f.startDate)}" style="width:100%"></label>
        <label>至 <input type="date" id="dsr-date-to" value="${dsrEsc(f.endDate)}" style="width:100%"></label>
        <label>客户 <select id="dsr-customer" style="width:100%"><option value="">全部客户</option>${customerOptions}</select></label>
        <label>币种 <select id="dsr-currency" style="width:100%"><option value="">全部币种</option>${currencyOptions}</select></label>
        <label>分配状态 <select id="dsr-allocation" style="width:100%"><option value="">全部分配状态</option>${allocationOptions}</select></label>
        <label>发票状态 <select id="dsr-invoice-status" style="width:100%">${invoiceStatusOptions}</select></label>
        <label>每页 <input type="number" id="dsr-pagesize" value="${Number(f.pageSize)}" min="1" max="${maxPage}" style="width:80px"></label>
      </div>
    </div>

    <div class="modal-footer">
      <button class="btn btn-primary" onclick="dsrPreview(1)">预览</button>
      <button class="btn btn-neutral" onclick="closeModal()">关闭</button>
    </div>
    <div id="dsr-result"></div>
  </div>`;
}

/* 读取当前字段 / 筛选 / 分页状态（预览复用，单一来源） */
function dsrBuildState(page) {
  return {
    catalogFields: DSR.fields,
    selectedKeys: DSR.selectedKeys,
    startDate: document.getElementById('dsr-date-from').value,
    endDate: document.getElementById('dsr-date-to').value,
    customerId: document.getElementById('dsr-customer').value,
    currency: document.getElementById('dsr-currency').value,
    allocationState: document.getElementById('dsr-allocation').value,
    invoiceStatus: document.getElementById('dsr-invoice-status').value,
    pageSize: document.getElementById('dsr-pagesize').value,
    page: page || 1,
    maxPageSize: DSR.catalog && DSR.catalog.maxPageSize ? DSR.catalog.maxPageSize : DSR_MAX_PAGE_SIZE,
  };
}

/* 预览：组装有界请求 → POST → 安全渲染列名与单元格；授权 / 无效 / 空 / 网络失败均可见 */
async function dsrPreview(page) {
  const state = dsrBuildState(page);
  if (state.startDate && state.endDate && state.startDate > state.endDate) {
    dsrRenderResult(dsrErrorHtml('invalid', '开始日期不能晚于结束日期'));
    return;
  }

  const req = dsrBuildRequest(state);
  DSR.filters.page = req.page;
  DSR.filters.pageSize = req.pageSize;

  dsrRenderResult(dsrLoadingHtml());

  try {
    const resp = await dsrRequest('/api/dynamic-receivable-report', 'POST', req);
    if (resp.code === 0) {
      DSR.view = resp.data;
      DSR.filters.page = resp.data.page;
      dsrRenderResult(dsrResultHtml(resp.data));
    } else if (resp.code === 2000 || resp.code === 2003) {
      if (typeof logout === 'function') logout();
      dsrRenderResult(dsrErrorHtml('unauthorized', resp.message));
    } else {
      dsrRenderResult(dsrErrorHtml(dsrKindOfCode(resp.code), resp.message));
    }
  } catch (err) {
    dsrRenderResult(dsrErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 翻页（有界：最小第 1 页） */
function dsrPage(delta) {
  const view = DSR.view;
  const page = (view ? view.page : DSR.filters.page) + delta;
  if (page < 1) return;
  dsrPreview(page);
}

/* Node 单测导出（浏览器中 module 为 undefined，自动跳过） */
if (typeof module !== 'undefined' && module.exports) {
  module.exports = {
    DSR_CURRENCY_OPTS,
    DSR_ALLOCATION_OPTS,
    DSR_INVOICE_STATUS_OPTS,
    DSR_REMAINING_STATE_LABELS,
    DSR_ALLOCATION_STATE_LABELS,
    dsrEsc,
    dsrRemainingStateLabel,
    dsrAllocationStateLabel,
    dsrSelectFields,
    dsrBuildRequest,
    dsrCellText,
    dsrRemainingBadge,
    dsrAllocationBadge,
    dsrRenderCell,
    dsrTableHtml,
    dsrEmptyHtml,
    dsrErrorHtml,
    dsrResultHtml,
    dsrFieldChooserHtml,
    dsrKindOfCode,
    dsrErrorModalHtml,
    dsrPreview,
    dsrPage,
  };
}




