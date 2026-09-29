/* ============ 客户报告包预览（ERP-122：只读、有界、双菜单授权） ============
   口径与后端 ERP-122（CustomerReportPacketController / CustomerReportPacketRules）一一对应：
   - 从客户销项发票登记册打开，按正整数客户 Id + 有界日期 / 分页筛选预览两个独立分区：
     ① 销售订单分区（复用 ERP-112 作用域化报表查询，销售订单菜单授权 + 业务员数据范围）；
     ② 发票 / 显式收款分摊证据分区（复用 ERP-117 作用域化报表查询，客户资料菜单授权 + 业务员数据范围）；
   - 两个分区各自独立计数、金额按原币呈现、剩余证据保留 known / unknown / over_allocated 标签，绝不推断或拼接跨单链接；
   - 全程只读：不写库、不迁移、不执行任意 SQL；授权 / 无效 / 空 / 网络失败都在界面可见，且不暴露范围外数据。 */

/* 剩余证据状态中文文案（与后端 RemainingStateText 同源，短文案便于着色标注） */
const CPK_REMAINING_STATE_LABELS = {
  known: '剩余可确认',
  unknown: '剩余未知',
  over_allocated: '超额分摊（无效）',
};

/* 分配状态中文文案（与后端 AllocationStateText 同源，短文案） */
const CPK_ALLOCATION_STATE_LABELS = {
  none: '无分摊行',
  historical_only: '仅历史/无效分摊',
  partial: '部分分摊',
  full: '全额分摊',
  over_allocated: '超额分摊（无效）',
  unknown: '未知',
};

/* 分页上限（与后端 CustomerReportPacketRules.MaxPageSize 一致） */
const CPK_DEFAULT_PAGE_SIZE = 20;
const CPK_MAX_PAGE_SIZE = 100;

/* 预览状态（纯数据；DOM 访问只在事件处理函数内部发生） */
let CPK = {
  customers: [],        // 客户下拉来源（/api/base/customers）
  filters: { customerId: '', startDate: '', endDate: '', page: 1, pageSize: CPK_DEFAULT_PAGE_SIZE },
  view: null,           // 最近一次预览结果（客户报告包 DTO）
};

/* ==================== 纯函数（可在 Node 中逐条单测） ==================== */

/* HTML 转义（本地独立实现，避免依赖全局 escapeHtml 的加载顺序） */
function cpkEsc(v) {
  return String(v ?? '').replace(/[&<>"']/g, c => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}

/* 剩余证据状态 → 中文短文案（未知取值原样返回，不猜测） */
function cpkRemainingStateLabel(v) {
  return CPK_REMAINING_STATE_LABELS[String(v)] || String(v);
}

/* 分配状态 → 中文短文案（未知取值原样返回，不猜测） */
function cpkAllocationStateLabel(v) {
  return CPK_ALLOCATION_STATE_LABELS[String(v)] || String(v);
}

/* 组装有界预览请求体：客户 Id 必须为正整数（缺失 / 非法返回 null，fail closed），分页有界、日期只取日期部分 */
function cpkBuildRequest(state) {
  const page = Math.max(1, Math.floor(Number(state.page) || 1));
  let pageSize = Math.floor(Number(state.pageSize));
  if (!Number.isFinite(pageSize)) pageSize = CPK_DEFAULT_PAGE_SIZE;
  pageSize = Math.max(1, Math.min(CPK_MAX_PAGE_SIZE, pageSize));

  const customerId = Number(state.customerId);
  if (!Number.isFinite(customerId) || customerId <= 0) return null;

  const req = { customerId, page, pageSize };

  const startDate = state.startDate ? String(state.startDate).slice(0, 10) : null;
  const endDate = state.endDate ? String(state.endDate).slice(0, 10) : null;
  if (startDate) req.startDate = startDate;
  if (endDate) req.endDate = endDate;
  return req;
}

/* 单元格纯文本（安全：null/undefined 显示为空、布尔显示 是/否、日期截断到日、
   剩余证据 / 分配状态映射中文短文案；绝不回落为 0） */
function cpkCellText(value, field) {
  const key = (field && field.key) || '';
  const dataType = (field && field.dataType) || 'text';
  if (value === null || value === undefined) return '';
  if (key === 'remainingState') return cpkRemainingStateLabel(value);
  if (key === 'allocationState') return cpkAllocationStateLabel(value);
  if (dataType === 'boolean') return (value === true || value === 'true' || value === 1 || value === '1') ? '是' : '否';
  if (dataType === 'date') return String(value).slice(0, 10);
  return String(value);
}

/* 剩余证据状态着色徽章（known 绿 / unknown 黄 / over_allocated 红，绝不掩盖三者差异） */
function cpkRemainingBadge(value) {
  const s = String(value);
  const map = {
    known: ['status-success', '剩余可确认'],
    unknown: ['status-warning', '剩余未知'],
    over_allocated: ['status-danger', '超额分摊（无效）'],
  };
  const m = map[s] || ['status-neutral', cpkRemainingStateLabel(s)];
  return `<span class="status ${m[0]}">${cpkEsc(m[1])}</span>`;
}

/* 分配状态着色徽章（fail closed：未知取值按中性文案展示，不猜测） */
function cpkAllocationBadge(value) {
  const s = String(value);
  const map = {
    none: ['status-neutral', '无分摊行'],
    historical_only: ['status-warning', '仅历史/无效分摊'],
    partial: ['status-warning', '部分分摊'],
    full: ['status-success', '全额分摊'],
    over_allocated: ['status-danger', '超额分摊（无效）'],
    unknown: ['status-neutral', '未知'],
  };
  const m = map[s] || ['status-neutral', cpkAllocationStateLabel(s)];
  return `<span class="status ${m[0]}">${cpkEsc(m[1])}</span>`;
}

/* 单元格 HTML（转义后安全渲染；剩余证据 / 分配状态额外着色标注） */
function cpkRenderCell(value, field) {
  const key = (field && field.key) || '';
  if (key === 'remainingState') return cpkRemainingBadge(value);
  if (key === 'allocationState') return cpkAllocationBadge(value);
  return cpkEsc(cpkCellText(value, field));
}

/* 分区表格 HTML：表头为返回的列名、单元格为返回的字段值，全部经转义 */
function cpkSectionTableHtml(section, emptyText) {
  const cols = (section && section.columns) || [];
  const rows = (section && section.rows) || [];
  if (!cols.length) return `<div class="empty" style="margin:6px 0">${cpkEsc(emptyText || '没有数据')}</div>`;
  const align = c => (c.dataType === 'number' || c.dataType === 'boolean') ? ' class="text-right"' : '';
  const head = cols.map(c => `<th${align(c)}>${cpkEsc(c.label || c.key)}</th>`).join('');
  const body = rows.map(r =>
    `<tr>${cols.map(c => `<td${align(c)}>${cpkRenderCell(r[c.key], c)}</td>`).join('')}</tr>`).join('');
  const emptyRow = `<tr><td colspan="${cols.length}" class="empty">${cpkEsc(emptyText || '没有数据')}</td></tr>`;
  return `<div class="table-wrap" style="margin-top:6px"><table><thead><tr>${head}</tr></thead><tbody>${body || emptyRow}</tbody></table></div>`;
}

/* 分区标题 + 独立计数摘要 + 表格（两个分区各自独立计数，绝不合并） */
function cpkSectionHtml(title, section, emptyText) {
  if (!section) return '';
  const summary = `<div class="text-muted" style="margin:6px 0">${cpkEsc(title)}：共 ${section.total || 0} 条 · 第 ${section.page || 1} 页 · 每页 ${section.pageSize || 0} 条</div>`;
  return `<h4 style="margin:12px 0 4px">${cpkEsc(title)}</h4>${summary}${cpkSectionTableHtml(section, emptyText)}`;
}

/* 分页（有界）：两个分区共享同一页码，总页数取两者较大值 */
function cpkPagingHtml(view) {
  const so = (view && view.salesOrders) || {};
  const rec = (view && view.receivableEvidence) || {};
  const page = so.page || rec.page || 1;
  const totalPages = Math.max(so.totalPages || 0, rec.totalPages || 0);
  const prevDisabled = page <= 1 ? ' disabled' : '';
  const nextDisabled = page >= totalPages ? ' disabled' : '';
  return `<div style="display:flex;justify-content:space-between;align-items:center;margin-top:8px">
      <span class="text-muted">第 ${page} 页 / 共 ${totalPages} 页</span>
      <div>
        <button class="btn btn-neutral btn-sm" onclick="cpkPage(-1)"${prevDisabled}>← 上一页</button>
        <button class="btn btn-neutral btn-sm" onclick="cpkPage(1)"${nextDisabled}>下一页 →</button>
      </div></div>`;
}

/* 空结果提示（不暴露任何数据） */
function cpkEmptyHtml(label) {
  return `<div class="empty" style="margin:8px 0">没有符合条件的${cpkEsc(label || '数据')}（当前账号数据范围内的只读快照）。</div>`;
}

/* 错误提示（授权 / 未登录 / 无效请求 / 网络失败分别可见，且不暴露任何数据） */
function cpkErrorHtml(kind, message) {
  const labels = {
    forbidden: '权限不足',
    unauthorized: '未登录 / 登录已过期',
    invalid: '请求无效',
    network: '网络请求失败',
    error: '预览失败',
  };
  return `<div class="pd-hint" style="color:#b91c1c;background:#fef2f2;border-color:#fecaca">
      <b>${cpkEsc(labels[kind] || '预览失败')}</b>：${cpkEsc(message || '')}</div>`;
}

/* 预览结果：口径 / 边界 / 免责文案 + 两个独立分区 + 分页 */
function cpkResultHtml(view) {
  if (!view) return '';
  const readOnly = view.readOnlyText ? `<div class="pd-hint">${cpkEsc(view.readOnlyText)}</div>` : '';
  const boundary = view.boundaryText ? `<div class="pd-hint">${cpkEsc(view.boundaryText)}</div>` : '';
  const disclaimer = view.disclaimerText ? `<div class="pd-hint" style="color:#64748b">${cpkEsc(view.disclaimerText)}</div>` : '';
  const orders = cpkSectionHtml('销售订单', view.salesOrders, '没有符合条件的销售订单');
  const receivable = cpkSectionHtml('发票 / 收款分摊证据', view.receivableEvidence, '没有符合条件的发票 / 收款分摊证据');
  return `${readOnly}${boundary}${disclaimer}${orders}${receivable}${cpkPagingHtml(view)}`;
}

/* 加载态 */
function cpkLoadingHtml() {
  return '<div class="pd-hint" style="text-align:center;color:#64748b">正在预览（只读查询）…</div>';
}

/* 业务码 → 错误态分类 */
function cpkKindOfCode(code) {
  if (code === 2002) return 'forbidden';
  if (code === 2000 || code === 2003) return 'unauthorized';
  return 'invalid';
}

/* 目录 / 客户加载失败或授权失败时的整页错误态（带关闭） */
function cpkErrorModalHtml(kind, message) {
  return `<div class="modal modal-lg">
    <h3>🧾 客户报告包预览</h3>
    ${cpkErrorHtml(kind, message)}
    <div class="modal-footer"><button class="btn btn-neutral" onclick="closeModal()">关闭</button></div>
  </div>`;
}

/* ==================== 状态 / 请求 / 渲染 ==================== */

/* 轻量请求封装：返回完整 ApiResponse 信封（保留 code），网络异常抛给调用方 */
async function cpkRequest(path, method = 'GET', body = null) {
  const headers = { 'Content-Type': 'application/json' };
  const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
  if (token) headers['Authorization'] = 'Bearer ' + token;
  const opts = { method, headers };
  if (body) opts.body = JSON.stringify(body);
  const resp = await fetch(path, opts);
  return await resp.json();
}

function cpkRenderResult(html) {
  const el = document.getElementById('cpk-result');
  if (el) el.innerHTML = html;
}

/* 读取当前客户 / 日期 / 分页状态（预览复用，单一来源） */
function cpkBuildState(page) {
  return {
    customerId: (document.getElementById('cpk-customer') || {}).value,
    startDate: (document.getElementById('cpk-date-from') || {}).value,
    endDate: (document.getElementById('cpk-date-to') || {}).value,
    pageSize: (document.getElementById('cpk-pagesize') || {}).value,
    page: page || 1,
  };
}

/* 预览：组装有界请求 → POST → 安全渲染两个分区；授权 / 无效 / 空 / 网络失败均可见 */
async function cpkPreview(page) {
  const state = cpkBuildState(page);
  if (state.startDate && state.endDate && state.startDate > state.endDate) {
    cpkRenderResult(cpkErrorHtml('invalid', '开始日期不能晚于结束日期'));
    return;
  }

  const req = cpkBuildRequest(state);
  if (!req) {
    cpkRenderResult(cpkErrorHtml('invalid', '请选择有效客户（客户 Id 必须为正整数）'));
    return;
  }
  CPK.filters.page = req.page;
  CPK.filters.pageSize = req.pageSize;

  cpkRenderResult(cpkLoadingHtml());

  try {
    const resp = await cpkRequest('/api/customer-report-packet', 'POST', req);
    if (resp.code === 0) {
      CPK.view = resp.data;
      cpkRenderResult(cpkResultHtml(resp.data));
    } else if (resp.code === 2000 || resp.code === 2003) {
      if (typeof logout === 'function') logout();
      cpkRenderResult(cpkErrorHtml('unauthorized', resp.message));
    } else {
      cpkRenderResult(cpkErrorHtml(cpkKindOfCode(resp.code), resp.message));
    }
  } catch (err) {
    cpkRenderResult(cpkErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 翻页（有界：最小第 1 页） */
function cpkPage(delta) {
  const view = CPK.view;
  const page = (view && (view.salesOrders && view.salesOrders.page)) || CPK.filters.page;
  const next = page + delta;
  if (next < 1) return;
  cpkPreview(next);
}

/* 渲染预览弹窗（客户 + 日期区间 + 每页条数 + 预览按钮 + 结果区） */
function cpkRender() {
  const f = CPK.filters;
  const customerOptions = CPK.customers.map(c =>
    `<option value="${cpkEsc(c.id)}" ${String(c.id) === String(f.customerId) ? 'selected' : ''}>${cpkEsc(c.customerCode || '')} ${cpkEsc(c.customerName || '')}</option>`).join('');

  const modal = document.getElementById('modal');
  modal.innerHTML = `
  <div class="modal modal-lg" style="max-width:1200px">
    <h3>🧾 客户报告包预览（只读）</h3>
    <div class="pd-hint">只读：两个独立分区分别复用销售订单菜单授权与客户资料菜单授权、并按业务员数据范围过滤；不新增 / 修改 / 删除任何记录、不执行任意 SQL、不推断跨单链接。</div>

    <div style="margin:10px 0">
      <div class="form-grid" style="grid-template-columns:repeat(3,1fr);gap:10px">
        <label>客户 <select id="cpk-customer" style="width:100%"><option value="">请选择客户</option>${customerOptions}</select></label>
        <label>日期从 <input type="date" id="cpk-date-from" value="${cpkEsc(f.startDate)}" style="width:100%"></label>
        <label>至 <input type="date" id="cpk-date-to" value="${cpkEsc(f.endDate)}" style="width:100%"></label>
        <label>每页 <input type="number" id="cpk-pagesize" value="${Number(f.pageSize)}" min="1" max="${CPK_MAX_PAGE_SIZE}" style="width:80px"></label>
      </div>
    </div>

    <div class="modal-footer">
      <button class="btn btn-primary" onclick="cpkPreview(1)">预览</button>
      <button class="btn btn-neutral" onclick="closeModal()">关闭</button>
    </div>
    <div id="cpk-result"></div>
  </div>`;
  modal.style.display = 'flex';
}

/* 从客户销项发票登记册打开报告包（加载客户，渲染筛选器与结果区） */
async function openCustomerReportPacket(customerId) {
  CPK = {
    customers: [],
    filters: { customerId: customerId ? String(customerId) : '', startDate: '', endDate: '', page: 1, pageSize: CPK_DEFAULT_PAGE_SIZE },
    view: null,
  };
  const modal = document.getElementById('modal');
  modal.innerHTML = '<div class="modal modal-lg" style="max-width:1200px"><div class="pd-hint" style="text-align:center;color:#64748b">正在加载客户…</div></div>';
  modal.style.display = 'flex';

  // 客户下拉（尽力而为：失败仅保留「请选择客户」，仍可预览）
  try {
    const cresp = await cpkRequest('/api/base/customers?page=1&pageSize=500');
    if (cresp.code === 0) {
      CPK.customers = (cresp.data && cresp.data.items) || (Array.isArray(cresp.data) ? cresp.data : []) || [];
    }
  } catch (e) {
    CPK.customers = [];
  }

  cpkRender();
}

/* Node 单测导出（浏览器中 module 为 undefined，自动跳过） */
if (typeof module !== 'undefined' && module.exports) {
  module.exports = {
    CPK_REMAINING_STATE_LABELS,
    CPK_ALLOCATION_STATE_LABELS,
    CPK_DEFAULT_PAGE_SIZE,
    CPK_MAX_PAGE_SIZE,
    cpkEsc,
    cpkRemainingStateLabel,
    cpkAllocationStateLabel,
    cpkBuildRequest,
    cpkCellText,
    cpkRemainingBadge,
    cpkAllocationBadge,
    cpkRenderCell,
    cpkSectionTableHtml,
    cpkSectionHtml,
    cpkPagingHtml,
    cpkEmptyHtml,
    cpkErrorHtml,
    cpkResultHtml,
    cpkKindOfCode,
    cpkErrorModalHtml,
    cpkPreview,
    cpkPage,
  };
}
