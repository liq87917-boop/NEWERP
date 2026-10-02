/* ============ 通用报表配置工作台（ERP-262 Stage 1：前端，目录驱动 + 私有配置持久化） ============
   口径与后端 ReportConfigurationsController / ReportConfigurationService 一一对应：
   - 数据集目录只由 GET /api/report-configurations/catalog 返回（当前账号已授权数据集的有限字段白名单），
     绝无自由填写的字段名 / 数据集 / SQL / 脚本；
   - 定义只描述「选择 / 排序白名单字段 + 有界类型化筛选 + 支持的单一分组键 + 展示」，保存走
     POST /api/report-configurations（新增）或 PUT /api/report-configurations/{id}?version=N（更新，匹配预期版本令牌）；
   - 复制 / 重命名 / 软删除 / 发布 / 恢复 / 修订列表分别走 {id}/copy、{id}/rename、DELETE {id}、
     {id}/publish、{id}/restore、{id}/revisions；预览走 POST /api/report-configurations/preview；
   - 不支持的能力（自定义公式 / 透视 / 跨数据集联接 / 全匹配合计 / 共享 / 导出）只在「为什么不支持」面板说明原因，
     绝不渲染为装饰性按钮假装可运行；
   - 币种 / 单位分离：列头带币种单位语义，分组小计按币种分区呈现，绝不跨币种 / 单位合并；
   - 草稿与已发布区分展示；当前预览页覆盖口径与全量合计明确区分（仅当前预览页小计）；
   - 未保存本地编辑保留到保存成功为止；保存冲突（1004）不覆盖；所有用户 / 目录字符串一律转义；
   - 数据集 / 配置变化后丢弃迟到的预览响应（请求序号令牌）；授权 / 失败时清除过期预览行；
   - 报表配置数据表缺失（5000）显式显示为「环境未就绪（environment-blocked）」，绝不使用浏览器本地存储替代持久化定义。 */

const RCC_API = '/api/report-configurations';
const RCC_DEFAULT_PAGE_SIZE = 20;

const RCC_GROUP_LABELS = {
  none: '不分组',
  customer: '按客户分组',
  month: '按月份分组',
};

/* 不支持能力的「为什么不支持」说明（Stage 1 显式失败，绝不假装运行） */
const RCC_UNSUPPORTED_REASONS = {
  'custom-formula': '自定义公式：本阶段不支持，只能选择目录白名单字段，绝不执行任意公式 / SQL / 脚本',
  'cross-dataset-join': '跨数据集联接：本阶段不支持，单个报表配置只绑定一个已授权数据集',
  'pivot': '透视表：本阶段不支持，只提供有限字段 / 筛选 / 单一分组键',
  'all-match-total': '全匹配合计：本阶段不支持，页面小计只覆盖当前预览页（非全量合计）',
  'sharing': '共享：本阶段仅支持私有配置，专用管理员共享权限为未来平台增量',
  'export': '导出：本阶段不支持导出，仅提供页面预览',
};

/* 设计器状态（纯数据；DOM 访问只在事件处理函数内部发生） */
let RCC = {
  catalog: null,
  datasets: [],
  datasetKey: '',
  fields: [],
  list: [],
  current: null,
  name: '',
  selectedKeys: [],
  filters: [],
  groupBy: 'none',
  page: 1,
  pageSize: RCC_DEFAULT_PAGE_SIZE,
  maxPageSize: 200,
  previewRevision: null,
  view: null,
  revisions: [],
  dirty: false,
  requestSeq: 0,
  envBlocked: false,
  busy: false,
};

/* ==================== 纯函数（可在 Node 中逐条单测） ==================== */

/* HTML 转义（本地独立实现，避免依赖全局 escapeHtml 的加载顺序） */
function rccEsc(v) {
  return String(v ?? '').replace(/[&<>"']/g, c => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}

/* 状态枚举名 → 中文文案（未知取值原样返回，不猜测） */
function rccStatusLabel(v) {
  const s = String(v ?? '').toLowerCase();
  if (s === 'draft') return '草稿';
  if (s === 'published') return '已发布';
  return String(v ?? '');
}

function rccStatusBadge(v) {
  const s = String(v ?? '').toLowerCase();
  if (s === 'draft') return '<span class="status status-info">草稿</span>';
  if (s === 'published') return '<span class="status status-success">已发布</span>';
  return rccEsc(String(v ?? ''));
}

/* 规范化选定字段（fail closed）：只保留目录白名单内的键、去重、保持请求顺序；未知键丢弃 */
function rccSelectFields(catalogFields, selectedKeys) {
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

/* 业务码 → 前端错误分类（1004 = 版本冲突；5000 = 环境未就绪；-1 = 网络） */
function rccKindOfCode(code) {
  if (code === 2000 || code === 2003) return 'unauthorized';
  if (code === 2002) return 'forbidden';
  if (code === 1001) return 'invalid';
  if (code === 1002) return 'notfound';
  if (code === 1004) return 'conflict';
  if (code === 5000) return 'environment';
  if (code === -1) return 'network';
  return 'unknown';
}

/* 环境未就绪：报表配置数据表缺失时显式阻断，绝不使用浏览器本地存储替代持久化定义 */
function rccEnvBlockedHtml(message) {
  return '<div class="rcc-env-blocked"><b>环境未就绪（environment-blocked）</b>'
    + '<p>' + rccEsc(message || '报表配置数据表尚未创建，无法持久化') + '</p>'
    + '<p>本阶段不使用浏览器本地存储替代持久化定义；请联系管理员完成数据库表创建后再使用保存 / 预览。</p></div>';
}

function rccErrorHtml(kind, message) {
  if (kind === 'environment') return rccEnvBlockedHtml(message);
  const labels = {
    unauthorized: '未登录或登录已过期',
    forbidden: '无权限',
    invalid: '请求无效',
    notfound: '不存在或无权访问',
    conflict: '版本冲突',
    network: '网络请求失败',
    unknown: '操作失败',
  };
  return '<div class="rcc-error"><b>' + (labels[kind] || labels.unknown) + '</b>：' + rccEsc(message || '') + '</div>';
}

function rccEmptyHtml() { return '<div class="empty">暂无数据</div>'; }
function rccLoadingHtml() { return '<div class="rcc-hint">正在预览…</div>'; }

/* 不支持能力面板：只解释「为什么不支持」，绝不渲染可点击的装饰性控件 */
function rccUnsupportedHtml(dataset) {
  const keys = (dataset && dataset.unsupportedCapabilities) || [];
  const extra = ['sharing', 'export'];
  const rows = keys.concat(extra.filter(k => keys.indexOf(k) < 0))
    .map(k => '<li><b>' + rccEsc(k) + '</b>：' + rccEsc(RCC_UNSUPPORTED_REASONS[k] || '本阶段不支持') + '</li>')
    .join('');
  return '<div class="rcc-unsupported"><div class="rcc-unsupported-title">为什么不支持（本阶段不提供）</div><ul>' + rows + '</ul></div>';
}

/* 数值格式化：整数原样，小数两位；不追加币种符号（币种由列头 / 分区单独呈现） */
function rccNumberText(v) {
  if (v === null || v === undefined || v === '') return '';
  const n = Number(v);
  if (!Number.isFinite(n)) return String(v);
  return Number.isInteger(n) ? String(n) : n.toFixed(2);
}

function rccCellText(value, type, currencyUnit) {
  if (value === null || value === undefined) return '';
  if (type === 'boolean') return value === true || value === 'true' || value === 1 ? '是' : '否';
  if (type === 'number') return rccNumberText(value);
  return String(value);
}

function rccRenderCell(value, type, currencyUnit) {
  return '<td>' + rccEsc(rccCellText(value, type, currencyUnit)) + '</td>';
}

function rccTableHtml(preview) {
  const columns = (preview && preview.columns) || [];
  const rows = (preview && preview.rows) || [];
  const head = '<tr>' + columns.map(c =>
    '<th>' + rccEsc(c.label) + (c.currencyUnit ? '（' + rccEsc(c.currencyUnit) + '）' : '') + '</th>').join('') + '</tr>';
  const body = rows.map(row => '<tr>' + columns.map(c => rccRenderCell(row[c.key], c.type, c.currencyUnit)).join('') + '</tr>').join('');
  return '<div class="rcc-table-wrap"><table class="rcc-table"><thead>' + head + '</thead><tbody>' + body + '</tbody></table></div>';
}

/* 分组页面小计：组内按币种分区呈现，绝不跨币种 / 单位合并金额 */
function rccGroupHtml(preview) {
  const groups = (preview && preview.groups) || [];
  if (!groups.length) return '';
  const rows = groups.map(g => {
    const parts = ((g && g.partitions) || []).map(p => {
      const amounts = [];
      if (p.amount !== null && p.amount !== undefined) amounts.push('金额 ' + rccNumberText(p.amount));
      if (p.grossAmount !== null && p.grossAmount !== undefined) amounts.push('含税 ' + rccNumberText(p.grossAmount));
      if (p.effectiveAllocatedAmount !== null && p.effectiveAllocatedAmount !== undefined) amounts.push('已分摊 ' + rccNumberText(p.effectiveAllocatedAmount));
      if (p.remainingAmount !== null && p.remainingAmount !== undefined) amounts.push('剩余 ' + rccNumberText(p.remainingAmount));
      return '<div class="rcc-partition">币种：' + rccEsc(p.currency) + ' · 条数 ' + rccEsc(p.count)
        + (amounts.length ? ' · ' + amounts.join(' · ') : '') + '</div>';
    }).join('');
    return '<div class="rcc-group"><b>' + rccEsc(g.label) + '</b>' + parts + '</div>';
  }).join('');
  return '<div class="rcc-groups">' + rows + '</div>';
}

function rccResultHtml(preview) {
  if (!preview) return rccEmptyHtml();
  const evidence = preview.evidence || {};
  const coverage = evidence.coverage || 'current-page';
  const coverageText = coverage === 'current-page' ? '当前预览页（非全量合计）' : coverage;
  const parts = [];
  if (preview.groupBy && preview.groupBy !== 'none') parts.push(rccGroupHtml(preview));
  parts.push(rccTableHtml(preview));
  parts.push('<div class="rcc-evidence">' + rccEsc(evidence.grain || '')
    + ' · ' + rccEsc(evidence.currencyUnitSemantics || '')
    + ' · 覆盖口径：' + rccEsc(coverageText) + '</div>');
  if (evidence.disclaimerText) parts.push('<div class="rcc-disclaimer">' + rccEsc(evidence.disclaimerText) + '</div>');
  parts.push('<div class="rcc-meta">第 ' + rccEsc(preview.page) + '/' + rccEsc(preview.totalPages)
    + ' 页 · 共 ' + rccEsc(preview.total) + ' 条</div>');
  return parts.join('');
}

/* 字段选择器：勾选 + 上下调整顺序；隐藏字段不渲染，未知键在 rccSelectFields 丢弃 */
function rccFieldChooserHtml(fields, selectedKeys) {
  const sel = new Set(selectedKeys || []);
  const rows = (fields || []).filter(f => f && !f.hidden).map(f => {
    const idx = (selectedKeys || []).indexOf(f.key);
    const move = idx >= 0
      ? '<button type="button" class="btn" onclick="rccMoveField(\'' + rccEsc(f.key) + '\', -1)" '
        + (idx === 0 ? 'disabled' : '') + '>↑</button>'
        + '<button type="button" class="btn" onclick="rccMoveField(\'' + rccEsc(f.key) + '\', 1)" '
        + (idx === (selectedKeys || []).length - 1 ? 'disabled' : '') + '>↓</button>'
      : '';
    return '<label class="rcc-field-row">'
      + '<input type="checkbox" ' + (sel.has(f.key) ? 'checked' : '') + ' data-key="' + rccEsc(f.key) + '" onchange="rccToggleField(this)">'
      + '<span>' + rccEsc(f.label) + '</span>'
      + '<span class="rcc-field-type">' + rccEsc(f.type) + (f.currencyUnit ? ' · ' + rccEsc(f.currencyUnit) : '') + '</span>'
      + move + '</label>';
  }).join('');
  return '<div class="rcc-field-chooser">' + rows + '</div>';
}

/* 类型化筛选值：数字 / 布尔 / 日期 / 文本 / 枚举；in 拆分为数组，between 返回上下界标量 */
function rccTypedScalar(field, raw) {
  if (raw === null || raw === undefined) return null;
  const s = String(raw).trim();
  if (s === '') return null;
  if (field && field.type === 'number') { const n = Number(s); return Number.isFinite(n) ? n : null; }
  if (field && field.type === 'boolean') return s === 'true' || s === '1';
  return s;
}

function rccTypedValue(field, operator, raw) {
  if (operator === 'in') {
    const parts = String(raw ?? '').split(',').map(x => rccTypedScalar(field, x)).filter(v => v !== null);
    return parts.length ? parts : null;
  }
  return rccTypedScalar(field, raw);
}

function rccFilterValueToString(value) {
  if (Array.isArray(value)) return value.join(',');
  if (value === null || value === undefined) return '';
  return String(value);
}

function rccBuildFilter(fields, f) {
  if (!f || !f.fieldKey || !f.operator) return null;
  const field = (fields || []).find(x => x.key === f.fieldKey);
  if (!field) return null;
  const value = rccTypedValue(field, f.operator, f.value);
  if (value === null) return null;
  const result = { fieldKey: field.key, operator: f.operator, value };
  if (f.operator === 'between') {
    const upper = rccTypedScalar(field, f.value2);
    if (upper === null) return null;
    result.value2 = upper;
  }
  return result;
}

/* 组装有界定义：字段只来自目录、筛选只来自可筛选字段、单一分组键；不包含任意 SQL / 脚本 / 联接 */
function rccBuildDefinition(state) {
  const fields = rccSelectFields(state.fields || [], state.selectedKeys || []);
  const filters = (state.filters || []).map(f => rccBuildFilter(state.fields, f)).filter(Boolean);
  const grouping = (state.groupBy && state.groupBy !== 'none') ? [state.groupBy] : ['none'];
  return {
    schemaVersion: (state.catalog && state.catalog.schemaVersion) || 1,
    datasetKey: state.datasetKey || '',
    fields,
    filters,
    grouping,
    aggregates: [],
    capabilities: [],
    presentation: { page: 1, pageSize: state.pageSize || RCC_DEFAULT_PAGE_SIZE },
  };
}

/* 组装有界预览请求：页码 / 每页条数 / 分组键受后端校验，固定发布版本可选 */
function rccBuildPreviewRequest(state) {
  return {
    configurationId: state.current ? state.current.id : 0,
    revisionVersion: state.previewRevision || null,
    page: state.page || 1,
    pageSize: state.pageSize || RCC_DEFAULT_PAGE_SIZE,
    groupBy: state.groupBy || 'none',
  };
}

/* 筛选值输入（按字段类型 + 操作符渲染；in 用逗号分隔，between 用双输入） */
function rccFilterValueHtml(field, f, i) {
  if (!field) return '';
  if (f.operator === 'between') {
    return '<input value="' + rccEsc(f.value ?? '') + '" oninput="rccOnFilterValue(' + i + ', this.value)" placeholder="开始">'
      + '<input value="' + rccEsc(f.value2 ?? '') + '" oninput="rccOnFilterValue2(' + i + ', this.value)" placeholder="结束">';
  }
  if (f.operator === 'in') {
    return '<input value="' + rccEsc(f.value ?? '') + '" oninput="rccOnFilterValue(' + i + ', this.value)" placeholder="多个值用逗号分隔">';
  }
  if (field.type === 'date') {
    return '<input type="date" value="' + rccEsc(f.value ?? '') + '" oninput="rccOnFilterValue(' + i + ', this.value)">';
  }
  if (field.type === 'boolean') {
    return '<select onchange="rccOnFilterValue(' + i + ', this.value)">'
      + '<option value="">—</option>'
      + '<option value="true" ' + (f.value === true || f.value === 'true' ? 'selected' : '') + '>是</option>'
      + '<option value="false" ' + (f.value === false || f.value === 'false' ? 'selected' : '') + '>否</option></select>';
  }
  if (field.type === 'number') {
    return '<input type="number" value="' + rccEsc(f.value ?? '') + '" oninput="rccOnFilterValue(' + i + ', this.value)">';
  }
  return '<input value="' + rccEsc(f.value ?? '') + '" oninput="rccOnFilterValue(' + i + ', this.value)">';
}

function rccFilterRowHtml(fields, f, i) {
  const field = (fields || []).find(x => x.key === f.fieldKey);
  const ops = field ? (field.filterOperators || []) : [];
  const fieldOpts = (fields || []).filter(x => x.filterable)
    .map(x => '<option value="' + rccEsc(x.key) + '" ' + (x.key === f.fieldKey ? 'selected' : '') + '>' + rccEsc(x.label) + '</option>').join('');
  const opOpts = ops.map(o => '<option value="' + rccEsc(o) + '" ' + (o === f.operator ? 'selected' : '') + '>' + rccEsc(o) + '</option>').join('');
  return '<div class="rcc-filter-row">'
    + '<select onchange="rccOnFilterField(' + i + ', this.value)">' + fieldOpts + '</select>'
    + '<select onchange="rccOnFilterOp(' + i + ', this.value)">' + opOpts + '</select>'
    + rccFilterValueHtml(field, f, i)
    + '<button type="button" class="btn" onclick="rccRemoveFilter(' + i + ')">删除</button></div>';
}

function rccFiltersHtml(filters, fields) {
  if (!filters || !filters.length) return '<div class="rcc-hint">暂无筛选</div>';
  return filters.map((f, i) => rccFilterRowHtml(fields, f, i)).join('');
}

function rccGroupingHtml(groupingKeys, groupBy) {
  const keys = (groupingKeys && groupingKeys.length) ? groupingKeys : ['none', 'customer', 'month'];
  return '<select onchange="rccOnGroupBy(this.value)">'
    + keys.map(k => '<option value="' + rccEsc(k) + '" ' + (k === groupBy ? 'selected' : '') + '>' + rccEsc(RCC_GROUP_LABELS[k] || k) + '</option>').join('')
    + '</select>';
}

function rccDatasetLabel(key) {
  const ds = (RCC.datasets || []).find(d => d.datasetKey === key);
  return ds ? ds.label : key;
}

/* ==================== DOM / 交互（事件处理函数内部才访问 DOM） ==================== */

function rccCurrentDataset() {
  return (RCC.datasets || []).find(d => d.datasetKey === RCC.datasetKey) || null;
}

function rccRenderEnvBanner() {
  const el = document.getElementById('rcc-env-banner');
  if (el) el.innerHTML = RCC.envBlocked ? rccEnvBlockedHtml('报表配置数据表尚未创建，无法持久化') : '';
}

function rccRenderDirty() {
  const el = document.getElementById('rcc-dirty');
  if (el) el.textContent = RCC.dirty ? '● 未保存' : '';
}

function rccRenderResult(html) {
  const el = document.getElementById('rcc-result');
  if (el) el.innerHTML = html;
}

function rccRenderDesigner(html) {
  const el = document.getElementById('rcc-designer');
  if (!el) return;
  if (html) { el.innerHTML = html; return; }
  const ds = rccCurrentDataset();
  if (!ds) { el.innerHTML = '<div class="empty">请先选择数据集</div>'; return; }
  el.innerHTML = ''
    + '<div class="rcc-ds-select"><label>数据集</label>'
    + '<select onchange="rccSelectDataset(this.value)">'
    + (RCC.datasets || []).map(d => '<option value="' + rccEsc(d.datasetKey) + '" ' + (d.datasetKey === RCC.datasetKey ? 'selected' : '') + '>' + rccEsc(d.label) + '</option>').join('')
    + '</select>'
    + '<div class="rcc-ds-meta">' + rccEsc(ds.grain) + ' · ' + rccEsc(ds.currencyUnitSemantics) + '</div></div>'
    + '<div class="rcc-name"><label>配置名称</label>'
    + '<input id="rcc-name" value="' + rccEsc(RCC.name) + '" maxlength="200" oninput="rccOnNameInput(this.value)"></div>'
    + '<div class="rcc-fields"><label>选择字段（可上下调整顺序）</label>' + rccFieldChooserHtml(RCC.fields, RCC.selectedKeys) + '</div>'
    + '<div class="rcc-filters"><label>类型化筛选</label>' + rccFiltersHtml(RCC.filters, RCC.fields)
    + '<button type="button" class="btn" onclick="rccAddFilter()">+ 添加筛选</button></div>'
    + '<div class="rcc-grouping"><label>分组</label>' + rccGroupingHtml(ds.groupingKeys, RCC.groupBy) + '</div>'
    + rccUnsupportedHtml(ds);
}

function rccRenderList(list) {
  const el = document.getElementById('rcc-list');
  if (!el) return;
  if (!list || !list.length) { el.innerHTML = '<div class="empty">暂无私有配置</div>'; return; }
  el.innerHTML = list.map(item => ''
    + '<div class="rcc-list-item">'
    + '<div class="rcc-list-title">' + rccEsc(item.name) + ' ' + rccStatusBadge(item.status) + '</div>'
    + '<div class="rcc-list-meta">' + rccEsc(rccDatasetLabel(item.datasetKey)) + ' · v' + rccEsc(item.version) + '</div>'
    + '<div class="rcc-list-actions"><button type="button" class="btn" onclick="rccLoadConfiguration(' + item.id + ')">加载</button></div>'
    + '</div>').join('');
}

function rccRenderRevisions() {
  const el = document.getElementById('rcc-revisions');
  if (!el) return;
  const revisions = RCC.revisions || [];
  if (!revisions.length) { el.innerHTML = '<div class="empty">暂无发布修订</div>'; return; }
  el.innerHTML = '<div class="rcc-revisions-title">发布修订</div>'
    + revisions.map(r => '<div class="rcc-revision-row">'
      + '<span>v' + rccEsc(r.version) + ' · ' + rccEsc(r.name) + ' · ' + rccEsc(String((r.publishedAt || '')).slice(0, 10)) + '</span>'
      + '<button type="button" class="btn" onclick="rccPreviewRevision(' + r.version + ')">预览</button>'
      + '<button type="button" class="btn" onclick="rccRestore(' + r.version + ')">恢复</button>'
      + '</div>').join('');
}

/* 请求封装：返回 { code, message, data } 信封；网络失败返回 code -1，不静默吞错误 */
async function rccFetch(path, method = 'GET', body = null) {
  const headers = { 'Content-Type': 'application/json' };
  const token = (typeof localStorage !== 'undefined') ? (localStorage.getItem('erp_token') || '') : '';
  if (token) headers['Authorization'] = 'Bearer ' + token;
  const opts = { method, headers };
  if (body !== null) opts.body = JSON.stringify(body);
  let resp;
  try { resp = await fetch(path, opts); }
  catch (err) { return { code: -1, message: (err && err.message) || '无法连接到服务器' }; }
  let envelope = null;
  try { envelope = await resp.json(); } catch (e) { envelope = null; }
  return envelope || { code: 5000, message: '服务器无响应' };
}

function rccOnUnauthorized(message) {
  if (typeof logout === 'function') logout();
  rccRenderResult(rccErrorHtml('unauthorized', message || '登录已过期'));
}

function rccTouch() {
  RCC.dirty = true;
  RCC.requestSeq++;                        // 使在途的旧预览 / 旧配置响应全部过期
  RCC.view = null;
  rccRenderResult(rccEmptyHtml());          // 数据集 / 配置变化后清除过期预览行
  rccRenderDirty();
}

async function rccInit() {
  RCC = {
    catalog: null, datasets: [], datasetKey: '', fields: [], list: [], current: null, name: '',
    selectedKeys: [], filters: [], groupBy: 'none', page: 1, pageSize: RCC_DEFAULT_PAGE_SIZE,
    maxPageSize: 200, previewRevision: null, view: null, revisions: [], dirty: false,
    requestSeq: 0, envBlocked: false, busy: false,
  };
  rccRenderDesigner();
  rccRenderList([]);
  rccRenderResult(rccEmptyHtml());
  await rccLoadCatalog();
  await rccLoadList();
}

async function rccLoadCatalog() {
  const env = await rccFetch(RCC_API + '/catalog');
  if (env.code === 2000 || env.code === 2003) { rccOnUnauthorized(env.message); return false; }
  if (env.code !== 0) {
    if (env.code === 5000) { RCC.envBlocked = true; rccRenderEnvBanner(); }
    rccRenderDesigner(rccErrorHtml(rccKindOfCode(env.code), env.message || '目录加载失败'));
    return false;
  }
  RCC.catalog = env.data || {};
  RCC.datasets = (env.data && env.data.datasets) || [];
  if (!RCC.datasets.length) {
    rccRenderDesigner('<div class="empty">当前账号没有任何已授权数据集，无法使用报表配置工作台</div>');
    return false;
  }
  rccSelectDataset(RCC.datasets[0].datasetKey, false);
  return true;
}

async function rccLoadList() {
  const env = await rccFetch(RCC_API);
  if (env.code === 2000 || env.code === 2003) { rccOnUnauthorized(env.message); return; }
  if (env.code !== 0) {
    if (env.code === 5000) { RCC.envBlocked = true; rccRenderEnvBanner(); }
    rccRenderList([]);
    return;
  }
  RCC.list = env.data || [];
  rccRenderList(RCC.list);
}

function rccSelectDataset(key, touch = true) {
  RCC.datasetKey = key;
  const ds = rccCurrentDataset();
  RCC.fields = ds ? (ds.fields || []).filter(f => !f.hidden) : [];
  RCC.selectedKeys = rccSelectFields(RCC.fields, RCC.fields.map(f => f.key));
  RCC.maxPageSize = ds ? (ds.maxPageSize || 200) : 200;
  RCC.pageSize = Math.min(RCC.pageSize || RCC_DEFAULT_PAGE_SIZE, RCC.maxPageSize);
  RCC.filters = [];
  RCC.groupBy = 'none';
  RCC.page = 1;
  if (touch) rccTouch();
  rccRenderDesigner();
}

function rccOnNameInput(v) { RCC.name = v; rccTouch(); }

function rccToggleField(el) {
  const key = el && el.getAttribute('data-key');
  if (!key) return;
  const idx = RCC.selectedKeys.indexOf(key);
  if (idx >= 0) RCC.selectedKeys.splice(idx, 1);
  else RCC.selectedKeys.push(key);
  rccTouch();
  rccRenderDesigner();
}

function rccMoveField(key, dir) {
  const idx = RCC.selectedKeys.indexOf(key);
  const to = idx + dir;
  if (idx < 0 || to < 0 || to >= RCC.selectedKeys.length) return;
  const item = RCC.selectedKeys.splice(idx, 1)[0];
  RCC.selectedKeys.splice(to, 0, item);
  rccTouch();
  rccRenderDesigner();
}

function rccAddFilter() {
  const ds = rccCurrentDataset();
  const field = (ds && (ds.fields || []).filter(f => f.filterable))[0];
  if (!field) { toast('没有可筛选字段', 'error'); return; }
  RCC.filters.push({ fieldKey: field.key, operator: (field.filterOperators || [])[0] || 'eq', value: '', value2: '' });
  rccTouch();
  rccRenderDesigner();
}

function rccRemoveFilter(i) {
  RCC.filters.splice(i, 1);
  rccTouch();
  rccRenderDesigner();
}

function rccOnFilterField(i, value) {
  const f = RCC.filters[i];
  if (!f) return;
  const field = (RCC.fields || []).find(x => x.key === value);
  f.fieldKey = value;
  f.operator = field ? ((field.filterOperators || [])[0] || 'eq') : 'eq';
  f.value = '';
  f.value2 = '';
  rccTouch();
  rccRenderDesigner();
}

function rccOnFilterOp(i, value) {
  const f = RCC.filters[i];
  if (!f) return;
  f.operator = value;
  f.value = '';
  f.value2 = '';
  rccTouch();
  rccRenderDesigner();
}

function rccOnFilterValue(i, value) { const f = RCC.filters[i]; if (f) { f.value = value; rccTouch(); } }
function rccOnFilterValue2(i, value) { const f = RCC.filters[i]; if (f) { f.value2 = value; rccTouch(); } }

function rccOnGroupBy(value) {
  RCC.groupBy = value;
  rccTouch();
  rccRenderDesigner();
}

function rccNew() {
  RCC.current = null;
  RCC.name = '';
  RCC.previewRevision = null;
  RCC.selectedKeys = rccSelectFields(RCC.fields, RCC.fields.map(f => f.key));
  RCC.filters = [];
  RCC.groupBy = 'none';
  RCC.page = 1;
  RCC.dirty = false;
  RCC.revisions = [];
  rccRenderDirty();
  rccRenderDesigner();
  rccRenderResult(rccEmptyHtml());
  rccRenderRevisions();
}

function rccApplyDefinition(def) {
  RCC.datasetKey = (def && def.datasetKey) || '';
  const ds = rccCurrentDataset();
  RCC.fields = ds ? (ds.fields || []).filter(f => !f.hidden) : [];
  RCC.maxPageSize = ds ? (ds.maxPageSize || 200) : 200;
  RCC.selectedKeys = rccSelectFields(RCC.fields, (def && def.fields) || []);
  RCC.filters = ((def && def.filters) || []).map(f => ({
    fieldKey: f.fieldKey,
    operator: f.operator,
    value: rccFilterValueToString(f.value),
    value2: f.value2 !== null && f.value2 !== undefined ? String(f.value2) : '',
  }));
  const g = ((def && def.grouping) || []).filter(k => k && k !== 'none');
  RCC.groupBy = g.length === 1 ? g[0] : 'none';
  RCC.page = (def && def.presentation && def.presentation.page) || 1;
  RCC.pageSize = (def && def.presentation && def.presentation.pageSize) || RCC_DEFAULT_PAGE_SIZE;
}

async function rccLoadConfiguration(id) {
  if (RCC.dirty) { toast('当前存在未保存编辑，请先保存或新建', 'error'); return; }
  const env = await rccFetch(RCC_API + '/' + id);
  if (env.code === 2000 || env.code === 2003) { rccOnUnauthorized(env.message); return; }
  if (env.code !== 0) {
    if (env.code === 5000) { RCC.envBlocked = true; rccRenderEnvBanner(); }
    rccRenderResult(rccErrorHtml(rccKindOfCode(env.code), env.message || '加载失败'));
    return;
  }
  const dto = env.data;
  RCC.current = dto;
  RCC.name = dto.name || '';
  RCC.previewRevision = null;
  rccApplyDefinition(dto.definition || {});
  RCC.dirty = false;
  rccRenderDirty();
  rccRenderDesigner();
  rccRenderResult(rccEmptyHtml());
  await rccLoadRevisions(id);
}

async function rccSave() {
  if (RCC.busy) return;
  const name = (RCC.name || '').trim();
  if (!name) { toast('请输入配置名称', 'error'); return; }
  const definition = rccBuildDefinition(RCC);
  if (!definition.fields.length) { toast('请至少选择一个字段', 'error'); return; }
  RCC.busy = true;
  try {
    let env;
    if (RCC.current && RCC.current.id) {
      env = await rccFetch(RCC_API + '/' + RCC.current.id + '?version=' + RCC.current.version, 'PUT', { name, definition });
    } else {
      env = await rccFetch(RCC_API, 'POST', { name, definition });
    }
    if (env.code === 2000 || env.code === 2003) { rccOnUnauthorized(env.message); return; }
    if (env.code === 5000) { RCC.envBlocked = true; rccRenderEnvBanner(); rccRenderResult(rccEnvBlockedHtml(env.message)); return; }
    if (env.code !== 0) {
      if (env.code === 1004) {
        toast('版本冲突：配置已被其他操作修改，未覆盖，请刷新后重试', 'error');
        rccRenderResult(rccErrorHtml('conflict', env.message || '版本冲突'));
      } else {
        toast(env.message || '保存失败', 'error');
        rccRenderResult(rccErrorHtml(rccKindOfCode(env.code), env.message || '保存失败'));
      }
      return;   // 保存失败：保留本地未保存编辑，绝不覆盖 / 清空
    }
    RCC.current = env.data;
    RCC.name = env.data.name || name;
    RCC.dirty = false;
    rccRenderDirty();
    toast('保存成功');
    await rccLoadList();
  } finally { RCC.busy = false; }
}

async function rccCopy() {
  if (!RCC.current) { toast('请先选择配置', 'error'); return; }
  const env = await rccFetch(RCC_API + '/' + RCC.current.id + '/copy', 'POST');
  if (env.code === 2000 || env.code === 2003) { rccOnUnauthorized(env.message); return; }
  if (env.code === 5000) { RCC.envBlocked = true; rccRenderEnvBanner(); return; }
  if (env.code !== 0) { toast(env.message || '复制失败', 'error'); return; }
  toast('复制成功');
  await rccLoadList();
}

async function rccRename() {
  if (!RCC.current) { toast('请先选择配置', 'error'); return; }
  const name = (RCC.name || '').trim();
  if (!name) { toast('请输入名称', 'error'); return; }
  const env = await rccFetch(RCC_API + '/' + RCC.current.id + '/rename?version=' + RCC.current.version, 'POST', { name });
  if (env.code === 2000 || env.code === 2003) { rccOnUnauthorized(env.message); return; }
  if (env.code === 5000) { RCC.envBlocked = true; rccRenderEnvBanner(); return; }
  if (env.code === 1004) { toast('版本冲突：配置已被其他操作修改，未覆盖', 'error'); return; }
  if (env.code !== 0) { toast(env.message || '重命名失败', 'error'); return; }
  RCC.current = env.data;
  RCC.name = env.data.name || name;
  RCC.dirty = false;
  rccRenderDirty();
  toast('重命名成功');
  await rccLoadList();
}

async function rccDelete() {
  if (!RCC.current) { toast('请先选择配置', 'error'); return; }
  if (!confirm('确认删除该配置及其全部发布修订？')) return;
  const env = await rccFetch(RCC_API + '/' + RCC.current.id + '?version=' + RCC.current.version, 'DELETE');
  if (env.code === 2000 || env.code === 2003) { rccOnUnauthorized(env.message); return; }
  if (env.code === 5000) { RCC.envBlocked = true; rccRenderEnvBanner(); return; }
  if (env.code === 1004) { toast('版本冲突：配置已被其他操作修改，未删除', 'error'); return; }
  if (env.code !== 0) { toast(env.message || '删除失败', 'error'); return; }
  RCC.current = null;
  RCC.name = '';
  RCC.revisions = [];
  RCC.dirty = false;
  rccRenderDirty();
  rccRenderDesigner();
  rccRenderResult(rccEmptyHtml());
  rccRenderRevisions();
  toast('删除成功');
  await rccLoadList();
}

async function rccPublish() {
  if (!RCC.current) { toast('请先选择配置', 'error'); return; }
  const env = await rccFetch(RCC_API + '/' + RCC.current.id + '/publish?version=' + RCC.current.version, 'POST');
  if (env.code === 2000 || env.code === 2003) { rccOnUnauthorized(env.message); return; }
  if (env.code === 5000) { RCC.envBlocked = true; rccRenderEnvBanner(); return; }
  if (env.code === 1004) { toast('版本冲突：配置已被其他操作修改，未发布', 'error'); return; }
  if (env.code !== 0) { toast(env.message || '发布失败', 'error'); return; }
  RCC.current = env.data;
  RCC.dirty = false;
  rccRenderDirty();
  toast('发布成功');
  await rccLoadList();
  await rccLoadRevisions(RCC.current.id);
}

async function rccLoadRevisions(id) {
  const env = await rccFetch(RCC_API + '/' + id + '/revisions');
  if (env.code === 2000 || env.code === 2003) { rccOnUnauthorized(env.message); return; }
  if (env.code === 5000) { RCC.envBlocked = true; rccRenderEnvBanner(); return; }
  if (env.code !== 0) { rccRenderRevisions(); return; }
  RCC.revisions = env.data || [];
  rccRenderRevisions();
}

async function rccRestore(version) {
  if (!RCC.current) return;
  const env = await rccFetch(RCC_API + '/' + RCC.current.id + '/restore?version=' + RCC.current.version + '&revisionVersion=' + version, 'POST');
  if (env.code === 2000 || env.code === 2003) { rccOnUnauthorized(env.message); return; }
  if (env.code === 5000) { RCC.envBlocked = true; rccRenderEnvBanner(); return; }
  if (env.code === 1004) { toast('版本冲突：配置已被其他操作修改，未恢复', 'error'); return; }
  if (env.code !== 0) { toast(env.message || '恢复失败', 'error'); return; }
  RCC.current = env.data;
  RCC.name = env.data.name || RCC.name;
  RCC.dirty = false;
  rccRenderDirty();
  toast('恢复成功');
  await rccLoadList();
  await rccLoadRevisions(RCC.current.id);
}

async function rccPreview() {
  if (RCC.busy) return;
  if (!RCC.current) { rccRenderResult(rccErrorHtml('invalid', '请先选择或保存一个报表配置')); return; }
  if (RCC.dirty) { rccRenderResult(rccErrorHtml('invalid', '存在未保存编辑，请先保存后再预览')); return; }
  const seq = ++RCC.requestSeq;   // 本次预览的令牌：迟到响应一律丢弃
  rccRenderResult(rccLoadingHtml());
  try {
    const env = await rccFetch(RCC_API + '/preview', 'POST', rccBuildPreviewRequest(RCC));
    if (seq !== RCC.requestSeq) return;   // 数据集 / 配置已变化：丢弃迟到的预览响应
    if (env.code === 2000 || env.code === 2003) { rccOnUnauthorized(env.message); return; }
    if (env.code === 5000) { RCC.envBlocked = true; rccRenderEnvBanner(); rccRenderResult(rccEnvBlockedHtml(env.message)); return; }
    if (env.code !== 0) {
      rccRenderResult(rccErrorHtml(rccKindOfCode(env.code), env.message || '预览失败'));  // 失败即清除过期预览行
      return;
    }
    RCC.view = env.data;
    rccRenderResult(rccResultHtml(RCC.view));
  } catch (err) {
    if (seq !== RCC.requestSeq) return;
    rccRenderResult(rccErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

function rccPreviewRevision(version) {
  RCC.previewRevision = version;
  rccPreview();
}

/* 工作台入口（app.js 路由 code === 'report-configuration' 调用） */
function renderReportConfigurationWorkspace() {
  document.getElementById('header-title').textContent = '报表配置工作台';
  const content = document.getElementById('content');
  content.innerHTML = ''
    + '<div class="card rcc-workspace">'
    + '<div id="rcc-env-banner"></div>'
    + '<div class="rcc-toolbar">'
    + '<button type="button" class="btn" onclick="rccNew()">新建</button>'
    + '<button type="button" class="btn btn-primary" onclick="rccSave()">保存</button>'
    + '<button type="button" class="btn" onclick="rccCopy()">复制</button>'
    + '<button type="button" class="btn" onclick="rccRename()">重命名</button>'
    + '<button type="button" class="btn btn-danger" onclick="rccDelete()">删除</button>'
    + '<button type="button" class="btn" onclick="rccPublish()">发布</button>'
    + '<button type="button" class="btn" onclick="rccPreview()">预览</button>'
    + '<span id="rcc-dirty" class="rcc-dirty"></span>'
    + '</div>'
    + '<div class="rcc-layout">'
    + '<aside class="rcc-list" id="rcc-list"></aside>'
    + '<section class="rcc-designer" id="rcc-designer"></section>'
    + '<section class="rcc-result" id="rcc-result"></section>'
    + '<section class="rcc-revisions" id="rcc-revisions"></section>'
    + '</div>'
    + '</div>';
  rccInit();
}

/* Node 单测导出（浏览器中 module 为 undefined，自动跳过） */
if (typeof module !== 'undefined' && module.exports) {
  module.exports = {
    RCC_API,
    RCC_DEFAULT_PAGE_SIZE,
    RCC_GROUP_LABELS,
    RCC_UNSUPPORTED_REASONS,
    rccEsc,
    rccStatusLabel,
    rccStatusBadge,
    rccSelectFields,
    rccKindOfCode,
    rccEnvBlockedHtml,
    rccErrorHtml,
    rccEmptyHtml,
    rccLoadingHtml,
    rccUnsupportedHtml,
    rccNumberText,
    rccCellText,
    rccRenderCell,
    rccTableHtml,
    rccGroupHtml,
    rccResultHtml,
    rccFieldChooserHtml,
    rccTypedScalar,
    rccTypedValue,
    rccFilterValueToString,
    rccBuildFilter,
    rccBuildDefinition,
    rccBuildPreviewRequest,
    rccFilterValueHtml,
    rccFilterRowHtml,
    rccFiltersHtml,
    rccGroupingHtml,
    rccDatasetLabel,
    rccRenderDesigner,
    rccRenderList,
    rccRenderRevisions,
    rccRenderResult,
    rccFetch,
    rccInit,
    rccLoadCatalog,
    rccLoadList,
    rccSelectDataset,
    rccApplyDefinition,
    rccLoadConfiguration,
    rccSave,
    rccCopy,
    rccRename,
    rccDelete,
    rccPublish,
    rccRestore,
    rccLoadRevisions,
    rccPreview,
    renderReportConfigurationWorkspace,
  };
}
