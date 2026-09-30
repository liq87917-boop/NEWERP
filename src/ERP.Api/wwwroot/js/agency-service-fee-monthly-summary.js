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
  filters: { customerId: '', currency: '', from: '', to: '', groupBy: 'none', page: 1, pageSize: 50 },
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
      currency: '', from: '', to: '', groupBy: 'none', page: 1, pageSize: 50
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
        ${asfmsGroupSelectHtml()}
        <div><label class="ea-lb">对账日期从</label>
          <input type="date" id="asfms-f-from" value="${escapeHtml(f.from)}"></div>
        <div><label class="ea-lb">到</label>
          <input type="date" id="asfms-f-to" value="${escapeHtml(f.to)}"></div>
        <div><label class="ea-lb">每页</label>
          <input type="number" id="asfms-f-pagesize" value="${Number(f.pageSize)}" min="1" max="200" style="width:70px"></div>
        <button class="btn btn-primary btn-sm" onclick="asfmsSearch()">🔍 预览</button>
        <button class="btn btn-neutral btn-sm" onclick="asfmsExportExcel()" title="下载当前页所选列为 Excel（只读，复用当前筛选与分页）">📥 导出 Excel</button>
        <button class="btn btn-neutral btn-sm" onclick="asfmsExportPdf()" title="下载当前页所选列为 PDF（只读，复用当前筛选与分页）">📄 导出 PDF</button>
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

/* 分组键选择（ERP-185）：只提供 ERP-184 目录返回的有限分组键（none / month / customer），无自由输入 */
function asfmsGroupByOptions() {
  const catalog = ASFMS.catalog;
  if (catalog && Array.isArray(catalog.groupBys)) {
    return catalog.groupBys
      .filter(o => o && o.key)
      .map(o => ({ key: String(o.key), label: String(o.label || o.key) }));
  }
  return [];
}

/* 分组键规范化（fail closed）：仅保留目录白名单内的分组键，缺失 / 空白 / 非法值一律回落 none */
function asfmsGroupKey(value) {
  const key = String(value == null ? '' : value).trim();
  return asfmsGroupByOptions().some(o => o.key === key) ? key : 'none';
}

/* 分组键选择器：仅目录分组键（fail closed，无自由输入） */
function asfmsGroupSelectHtml() {
  const opts = asfmsGroupByOptions();
  if (!opts.length) return '';
  const selected = asfmsGroupKey(ASFMS.filters.groupBy);
  const options = opts.map(o =>
    `<option value="${escapeHtml(o.key)}" ${o.key === selected ? 'selected' : ''}>${escapeHtml(o.label)}</option>`).join('');
  return `<div><label class="ea-lb">分组</label>`
    + `<select id="asfms-f-groupby" style="min-width:120px" onchange="asfmsGroupChange()">${options}</select></div>`;
}

/* 分组键变化：只更新分组键并保留当前分页与其它筛选 / 已选字段后重新预览 */
function asfmsGroupChange() {
  ASFMS.filters.groupBy = asfmsGroupKey((document.getElementById('asfms-f-groupby') || {}).value);
  const page = (ASFMS.view && ASFMS.view.page > 0) ? ASFMS.view.page : ASFMS.filters.page;
  asfmsPreview(page > 0 ? page : 1);
}

function asfmsSearch() {
  ASFMS.filters.customerId = (document.getElementById('asfms-f-customer') || {}).value || '';
  ASFMS.filters.currency = (document.getElementById('asfms-f-currency') || {}).value || '';
  ASFMS.filters.from = (document.getElementById('asfms-f-from') || {}).value || '';
  ASFMS.filters.to = (document.getElementById('asfms-f-to') || {}).value || '';
  ASFMS.filters.groupBy = asfmsGroupKey((document.getElementById('asfms-f-groupby') || {}).value);
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
  const groupBy = asfmsGroupKey(ASFMS.filters.groupBy);
  if (groupBy && groupBy !== 'none') req.groupBy = groupBy;
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

/* 下载当前页选定列为 Excel（ERP-182，只读）：复用预览请求体 POST /api/dynamic-agency-service-fee-monthly-report/export；
   成功（xlsx 附件）触发下载；授权 / 无效 / 网络失败在结果区可见，不下载任何内容；空页下载含显式说明的工作簿 */
async function asfmsExportExcel() {
  if (!ASFMS.view || !ASFMS.view.columns || !ASFMS.view.columns.length) {
    ASFMS.error = { kind: 'invalid', message: '请先预览后再导出 Excel' };
    asfmsRender();
    return;
  }

  const req = asfmsBuildRequest(ASFMS.view ? ASFMS.view.page : ASFMS.filters.page);
  ASFMS.loading = true;
  ASFMS.error = null;
  asfmsRender();

  try {
    const headers = { 'Content-Type': 'application/json' };
    const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
    if (token) headers['Authorization'] = 'Bearer ' + token;
    const resp = await fetch('/api/dynamic-agency-service-fee-monthly-report/export', {
      method: 'POST', headers, body: JSON.stringify(req)
    });

    const contentType = (resp.headers.get('content-type') || '');
    if (contentType.indexOf('spreadsheetml') >= 0) {
      const blob = await resp.blob();
      const disposition = resp.headers.get('content-disposition') || '';
      const match = /filename[^;=\n]*=((['"]).*?\2|[^;\n]*)/i.exec(disposition);
      const filename = (match && match[1] ? match[1].replace(/['"]/g, '') : '') || 'AgencyServiceFeeMonthly.xlsx';
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = filename;
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
      ASFMS.loading = false;
      toast('Excel 已导出（当前页 · 选定列）', 'success');
      asfmsRender();
      return;
    }

    let envelope = null;
    try { envelope = await resp.json(); } catch (e) { /* 忽略非 JSON 响应体 */ }
    const code = envelope && envelope.code;
    const message = (envelope && envelope.message) || '导出失败';
    ASFMS.loading = false;
    if (code === 2000 || code === 2003) {
      if (typeof logout === 'function') logout();
      ASFMS.error = { kind: 'unauthorized', message };
    } else {
      ASFMS.error = { kind: asfmsKindOfCode(code), message };
    }
  } catch (err) {
    ASFMS.loading = false;
    ASFMS.error = { kind: 'network', message: (err && err.message) || '无法连接到服务器' };
  }
  asfmsRender();
}

/* 下载当前页选定列为 PDF（ERP-183，只读）：复用预览请求体 POST /api/dynamic-agency-service-fee-monthly-report/pdf；
   成功（application/pdf 附件）触发下载；授权 / 无效 / 网络失败在结果区可见，不下载任何内容 */
async function asfmsExportPdf() {
  if (!ASFMS.view || !ASFMS.view.columns || !ASFMS.view.columns.length) {
    ASFMS.error = { kind: 'invalid', message: '请先预览后再导出 PDF' };
    asfmsRender();
    return;
  }

  const req = asfmsBuildRequest(ASFMS.view ? ASFMS.view.page : ASFMS.filters.page);
  ASFMS.loading = true;
  ASFMS.error = null;
  asfmsRender();

  try {
    const headers = { 'Content-Type': 'application/json' };
    const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
    if (token) headers['Authorization'] = 'Bearer ' + token;
    const resp = await fetch('/api/dynamic-agency-service-fee-monthly-report/pdf', {
      method: 'POST', headers, body: JSON.stringify(req)
    });

    const contentType = (resp.headers.get('content-type') || '');
    if (contentType.indexOf('application/pdf') >= 0) {
      const blob = await resp.blob();
      const disposition = resp.headers.get('content-disposition') || '';
      const match = /filename[^;=\n]*=((['"]).*?\2|[^;\n]*)/i.exec(disposition);
      const filename = (match && match[1] ? match[1].replace(/['"]/g, '') : '') || 'AgencyServiceFeeMonthly.pdf';
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = filename;
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
      ASFMS.loading = false;
      toast('PDF 已导出（当前页 · 选定列）', 'success');
      asfmsRender();
      return;
    }

    let envelope = null;
    try { envelope = await resp.json(); } catch (e) { /* 忽略非 JSON 响应体 */ }
    const code = envelope && envelope.code;
    const message = (envelope && envelope.message) || '导出失败';
    ASFMS.loading = false;
    if (code === 2000 || code === 2003) {
      if (typeof logout === 'function') logout();
      ASFMS.error = { kind: 'unauthorized', message };
    } else {
      ASFMS.error = { kind: asfmsKindOfCode(code), message };
    }
  } catch (err) {
    ASFMS.loading = false;
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

/* 分组计数标签（ERP-185）：由分组维度生成（月份用 statementMonthText、客户用 customerName + customerCode），
   绝不从选定列推导 */
function asfmsGroupCountLabel(g) {
  if (!g) return '未知';
  const by = g.groupBy || '';
  if (by === 'month') {
    if (g.statementMonthText) return String(g.statementMonthText);
    if (g.statementYear != null && g.statementMonth != null) {
      return `${g.statementYear}-${String(g.statementMonth).padStart(2, '0')}`;
    }
    return '未知';
  }
  if (by === 'customer') {
    const code = g.customerCode ? String(g.customerCode) : (g.customerId != null ? String(g.customerId) : '');
    const name = g.customerName ? String(g.customerName) : '';
    return name ? (code ? `${name}（${code}）` : name) : (code || '未知');
  }
  return '未知';
}

/* 计数纯文本：整数直出，null / 非有限数回落 0（绝不推导合计） */
function asfmsCountText(v) {
  if (v === null || v === undefined) return '0';
  const n = Number(v);
  return Number.isFinite(n) ? String(n) : '0';
}

/* 金额纯文本（ERP-186）：直接取服务端按币种精度给出的原币金额文案（含币种），绝不重算、绝不换算；null 回落空串 */
function asfmsAmountText(v) {
  if (v === null || v === undefined) return '';
  return String(v);
}

/* 当前页分组汇总面板（ERP-185 计数 + ERP-186 原币金额小计）：按币种分行、各状态张数与原币金额分离，
   仅当前授权预览页，非全量合计；金额直接取服务端按币种精度给出的文案，绝不从选定列重算、绝不换算 */
function asfmsGroupPanelHtml(view) {
  const groupBy = (view && view.groupBy) || 'none';
  if (groupBy === 'none') return '';
  const counts = (view && Array.isArray(view.groupCounts)) ? view.groupCounts : [];
  const title = groupBy === 'month'
    ? '📊 按对账月份分组 · 本页各状态张数与原币小计'
    : '📊 按客户分组 · 本页各状态张数与原币小计';
  const scope = (view && view.groupCountScopeText)
    ? `<div class="text-muted" style="margin-top:6px">${escapeHtml(view.groupCountScopeText)}</div>` : '';
  const truncated = (view && view.truncated)
    ? '<div class="text-muted" style="color:#b45309;margin-top:4px">⚠ 分组计数与原币小计仅当前页，不含后续分页，也不是整份报表或会计合计。</div>' : '';
  const empty = counts.length === 0
    ? '<div class="text-muted" style="margin:4px 0">本页没有可分组计数的月度汇总（空页）。</div>' : '';
  const head = '<th>分组</th><th>币种</th>'
    + '<th class="text-right">月度汇总行数</th>'
    + '<th class="text-right">已登记张数</th>'
    + '<th class="text-right">已登记原币小计</th>'
    + '<th class="text-right">草稿张数</th>'
    + '<th class="text-right">草稿原币小计</th>'
    + '<th class="text-right">已作废张数</th>'
    + '<th class="text-right">已作废原币小计</th>'
    + '<th class="text-right">对账单张数</th>';
  const body = counts.map(g => `<tr>`
    + `<td>${escapeHtml(asfmsGroupCountLabel(g))}</td>`
    + `<td><b>${escapeHtml(g && g.currency ? String(g.currency) : '未知')}</b></td>`
    + `<td class="text-right">${escapeHtml(asfmsCountText(g && g.rowCount))}</td>`
    + `<td class="text-right">${escapeHtml(asfmsCountText(g && g.registeredCount))}</td>`
    + `<td class="text-right">${escapeHtml(asfmsAmountText(g && g.registeredTotalAmountText))}</td>`
    + `<td class="text-right">${escapeHtml(asfmsCountText(g && g.draftCount))}</td>`
    + `<td class="text-right">${escapeHtml(asfmsAmountText(g && g.draftTotalAmountText))}</td>`
    + `<td class="text-right">${escapeHtml(asfmsCountText(g && g.voidedCount))}</td>`
    + `<td class="text-right">${escapeHtml(asfmsAmountText(g && g.voidedTotalAmountText))}</td>`
    + `<td class="text-right">${escapeHtml(asfmsCountText(g && g.statementCount))}</td>`
    + `</tr>`).join('');
  const table = counts.length === 0 ? ''
    : `<table style="width:100%;margin-bottom:6px"><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table>`;
  return `<div class="pd-hint" style="margin:8px 0">`
    + `<div style="font-weight:600;margin-bottom:6px">${title}（仅当前预览页，非全量合计）</div>`
    + `${empty}${table}${truncated}${scope}</div>`;
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

  return `${hints}${truncated}${asfmsGroupPanelHtml(v)}${empty}${asfmsTableHtml(v)}`;
}

function asfmsPage(delta) {
  const v = ASFMS.view;
  const page = v ? v.page + delta : ASFMS.filters.page;
  if (page < 1) return;
  asfmsPreview(page);
}

