/* ============ 通用报表配置工作台（ERP-262 Stage 1：前端，目录驱动 + 私有配置持久化） ============
   口径与后端 ReportConfigurationsController / ReportConfigurationService 一一对应：
   - 数据集目录只由 GET /api/report-configurations/catalog 返回（当前账号已授权数据集的有限字段白名单），
     绝无自由填写的字段名 / 数据集 / SQL / 脚本；
   - 定义只描述「选择 / 排序白名单字段 + 有界类型化筛选 + 支持的单一分组键 + 展示」，保存走
     POST /api/report-configurations（新增）或 PUT /api/report-configurations/{id}?version=N（更新，匹配预期版本令牌）；
   - 复制 / 重命名 / 软删除 / 发布 / 恢复 / 修订列表分别走 {id}/copy、{id}/rename、DELETE {id}、
     {id}/publish、{id}/restore、{id}/revisions；预览走 POST /api/report-configurations/preview；
   - 不支持的能力（自定义公式 / 透视 / 跨数据集联接 / 全匹配合计）只在「为什么不支持」面板说明原因，
     绝不渲染为装饰性按钮假装可运行；
   - 币种 / 单位分离：列头带币种单位语义，分组小计按币种分区呈现，绝不跨币种 / 单位合并；
   - 草稿与已发布区分展示；当前预览页覆盖口径与全量合计明确区分（仅当前预览页小计）；
   - 未保存本地编辑保留到保存成功为止；保存冲突（1004）不覆盖；所有用户 / 目录字符串一律转义；
   - 数据集 / 配置变化后丢弃迟到的预览响应（请求序号令牌）；授权 / 失败时清除过期预览行；
   - 报表配置数据表缺失（5000）显式显示为「环境未就绪（environment-blocked）」，绝不使用浏览器本地存储替代持久化定义；
   - 导出：POST /api/report-configurations/export 复用同一有界、已授权预览管线，仅导出当前预览页选定列；
     导出失败只显示错误、保留未保存编辑，绝不下载过期内容、绝不覆盖设计器状态。 */

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
  'pivot': '任意透视：本阶段不支持（仅提供有界透视：一个行维度 + 一个不同列维度 + 最多 4 个基础指标）',
  'all-match-total': '全匹配合计：本阶段不支持，页面小计只覆盖当前预览页（非全量合计）',
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
  computedColumns: [],
  aggregates: [],
  relations: [],
  groupings: [],
  coverage: 'current-page',
  pivot: { rowDimension: '', columnDimension: '' },
  sortFieldKey: '',
  sortDirection: 'asc',
  page: 1,
  pageSize: RCC_DEFAULT_PAGE_SIZE,
  maxPageSize: 200,
  previewRevision: null,
  view: null,
  revisions: [],
  sharedList: [],
  sharedCurrent: null,
  grants: [],
  dirty: false,
  requestSeq: 0,
  lastAction: '',
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
  if (code === 1005) return 'busy';
  if (code === 1006) return 'timeout';
  if (code === 1007) return 'cancelled';
  if (code === 1008) return 'too-large';
  if (code === 5000) return 'environment';
  if (code === 5001) return 'rendering';
  if (code === 5002) return 'environment';
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
    busy: '执行繁忙',
    timeout: '执行超时',
    cancelled: '请求已取消',
    'too-large': '结果过大',
    rendering: '文件生成失败',
    network: '网络请求失败',
    unknown: '操作失败',
  };
  const retryable = ['busy', 'timeout', 'network', 'rendering'].indexOf(kind) >= 0;
  const retry = retryable
    ? ' <button type="button" class="rcc-retry" onclick="rccRetry()">重试</button>'
    : '';
  return '<div class="rcc-error"><b>' + (labels[kind] || labels.unknown) + '</b>：' + rccEsc(message || '') + retry + '</div>';
}

function rccEmptyHtml() { return '<div class="empty">暂无数据</div>'; }
function rccLoadingHtml() { return '<div class="rcc-hint">正在预览…</div>'; }

/* 显式重试：按最近一次失败的操作重放（预览 / Excel 导出 / PDF 导出），绝不覆盖未保存编辑 */
function rccRetry() {
  if (RCC.lastAction === 'export') { rccExport(); return; }
  if (RCC.lastAction === 'exportPdf') { rccExportPdf(); return; }
  rccPreview();
}

/* 不支持能力面板：只解释「为什么不支持」，绝不渲染可点击的装饰性控件 */
function rccUnsupportedHtml(dataset) {
  const keys = (dataset && dataset.unsupportedCapabilities) || [];
  const rows = keys
    .map(k => '<li><b>' + rccEsc(k) + '</b>：' + rccEsc(RCC_UNSUPPORTED_REASONS[k] || '本阶段不支持') + '</li>')
    .join('');
  return '<div class="rcc-unsupported"><div class="rcc-unsupported-title">为什么不支持（本阶段不提供）</div><ul>' + rows + '</ul></div>';
}

/* ==================== 受限计算列（ERP-266）：结构化 字段 / 数字 / + - × ÷ 编辑器 ==================== */

const RCC_FORMULA_NODE_KINDS = {
  field: '字段',
  literal: '数字',
  add: '+',
  subtract: '-',
  multiply: '×',
  divide: '÷',
};

function rccSupportsComputedColumns() {
  const ds = rccCurrentDataset();
  return !!ds && ((ds.supportedCapabilities || []).indexOf('computed-columns') >= 0);
}

function rccNormalizeFormulaNode(node) {
  if (!node || !node.kind) return null;
  const kind = String(node.kind);
  if (kind === 'field') return { kind, fieldKey: String(node.fieldKey || '') };
  if (kind === 'literal') return { kind, literal: Number(node.literal) || 0 };
  const left = rccNormalizeFormulaNode(node.left);
  const right = rccNormalizeFormulaNode(node.right);
  if (!left || !right) return { kind, fieldKey: '' };
  return { kind, left, right };
}

function rccBuildComputedColumns(state) {
  return ((state && state.computedColumns) || []).map(c => ({
    key: String(c.key || '').trim(),
    label: String(c.label || ''),
    expression: rccNormalizeFormulaNode(c.expression),
  })).filter(c => c.key && c.expression && c.expression.kind);
}

function rccNewFormulaNode(kind) {
  if (kind === 'field') return { kind: 'field', fieldKey: '' };
  if (kind === 'literal') return { kind: 'literal', literal: 0 };
  return { kind, left: { kind: 'field', fieldKey: '' }, right: { kind: 'field', fieldKey: '' } };
}

function rccFormulaNodeAt(colIndex, path) {
  const col = (RCC.computedColumns || [])[colIndex];
  if (!col) return null;
  if (!col.expression) col.expression = { kind: 'field', fieldKey: '' };
  let cur = col.expression;
  if (path) {
    for (const p of path.split('.')) {
      cur[p] = cur[p] || {};
      cur = cur[p];
    }
  }
  return cur;
}

function rccFormulaNodeHtml(node, colIndex, path) {
  node = node || { kind: 'field', fieldKey: '' };
  const kind = node.kind || 'field';
  const kindSelect = '<select onchange="rccOnFormulaKind(' + colIndex + ', \'' + path + '\', this.value)">'
    + Object.keys(RCC_FORMULA_NODE_KINDS).map(k =>
        '<option value="' + k + '" ' + (k === kind ? 'selected' : '') + '>' + RCC_FORMULA_NODE_KINDS[k] + '</option>').join('')
    + '</select>';
  if (kind === 'field') {
    const opts = (RCC.fields || []).filter(f => f.type === 'number').map(f =>
      '<option value="' + rccEsc(f.key) + '" ' + (node.fieldKey === f.key ? 'selected' : '') + '>' + rccEsc(f.label) + '</option>').join('');
    return kindSelect + ' <select onchange="rccOnFormulaField(' + colIndex + ', \'' + path + '\', this.value)"><option value="">选择字段</option>' + opts + '</select>';
  }
  if (kind === 'literal') {
    return kindSelect + ' <input type="number" step="any" value="' + rccEsc(node.literal ?? '') + '" oninput="rccOnFormulaLiteral(' + colIndex + ', \'' + path + '\', this.value)">';
  }
  return kindSelect + ' ( ' + rccFormulaNodeHtml(node.left, colIndex, path + '.left')
    + ' <b>' + rccEsc(RCC_FORMULA_NODE_KINDS[kind]) + '</b> '
    + rccFormulaNodeHtml(node.right, colIndex, path + '.right') + ' )';
}

function rccComputedColumnsHtml() {
  if (!rccSupportsComputedColumns()) return '';
  const numeric = (RCC.fields || []).filter(f => f.type === 'number');
  const rows = (RCC.computedColumns || []).map((c, i) =>
    '<div class="rcc-computed-row">'
    + '<input value="' + rccEsc(c.key) + '" oninput="rccOnComputedKey(' + i + ', this.value)" placeholder="键（唯一）">'
    + '<input value="' + rccEsc(c.label) + '" oninput="rccOnComputedLabel(' + i + ', this.value)" placeholder="标签">'
    + '<div class="rcc-computed-expr">' + rccFormulaNodeHtml(c.expression, i, '') + '</div>'
    + '<button type="button" class="btn" onclick="rccRemoveComputedColumn(' + i + ')">删除</button></div>').join('');
  return '<div class="rcc-computed"><label>计算列（受限：字段 / 数字 / + - × ÷，最多 8 列）</label>'
    + rows
    + (numeric.length ? '<button type="button" class="btn" onclick="rccAddComputedColumn()">+ 添加计算列</button>' : '')
    + '</div>';
}

function rccOnFormulaKind(colIndex, path, kind) {
  if (!path) { const col = (RCC.computedColumns || [])[colIndex]; if (col) col.expression = rccNewFormulaNode(kind); }
  else {
    const parts = path.split('.');
    const child = parts.pop();
    const parent = rccFormulaNodeAt(colIndex, parts.join('.'));
    if (parent) parent[child] = rccNewFormulaNode(kind);
  }
  rccTouch(); rccRenderDesigner();
}

function rccOnFormulaField(colIndex, path, value) {
  const node = rccFormulaNodeAt(colIndex, path);
  if (node) { node.kind = 'field'; node.fieldKey = value; }
  rccTouch(); rccRenderDesigner();
}

function rccOnFormulaLiteral(colIndex, path, value) {
  const node = rccFormulaNodeAt(colIndex, path);
  if (node) { node.kind = 'literal'; node.literal = value === '' ? 0 : Number(value); }
  rccTouch(); rccRenderDesigner();
}

function rccAddComputedColumn() {
  if (!rccSupportsComputedColumns()) { toast('当前数据集不支持计算列', 'error'); return; }
  const numeric = (RCC.fields || []).filter(f => f.type === 'number');
  if (!numeric.length) { toast('没有可用的数值字段', 'error'); return; }
  RCC.computedColumns = RCC.computedColumns || [];
  RCC.computedColumns.push({ key: 'calc' + (RCC.computedColumns.length + 1), label: '计算列' + (RCC.computedColumns.length + 1), expression: { kind: 'field', fieldKey: numeric[0].key } });
  rccTouch(); rccRenderDesigner();
}

function rccRemoveComputedColumn(i) {
  (RCC.computedColumns || []).splice(i, 1);
  rccTouch(); rccRenderDesigner();
}

function rccOnComputedKey(i, v) { const c = (RCC.computedColumns || [])[i]; if (c) { c.key = v; rccTouch(); } }
function rccOnComputedLabel(i, v) { const c = (RCC.computedColumns || [])[i]; if (c) { c.label = v; rccTouch(); } }

/* 计算列口径（单位 / 未知值原因 / 依赖） */
function rccComputedEvidenceHtml(preview) {
  const cols = (preview && preview.computedColumns) || [];
  if (!cols.length) return '';
  const rows = cols.map(c => '<li><b>' + rccEsc(c.label || c.key) + '</b>'
    + (c.unit ? '（' + rccEsc(c.unit) + '）' : '')
    + '：' + rccEsc(c.unknownReason || '')
    + '；依赖：' + rccEsc((c.dependencies || []).join(', ')) + '</li>').join('');
  return '<div class="rcc-computed-evidence"><div class="rcc-computed-evidence-title">计算列口径</div><ul>' + rows + '</ul></div>';
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

function rccColumnHeadHtml(c) {
  const unit = c.currencyUnit ? '（' + rccEsc(c.currencyUnit) + '）' : '';
  const marker = c.isComputed ? ' · 计算列' : '';
  const title = c.unknownReason ? ' title="' + rccEsc(c.unknownReason) + '"' : '';
  return '<th' + title + '>' + rccEsc(c.label) + unit + marker + '</th>';
}

function rccTableHtml(preview) {
  const columns = (preview && preview.columns) || [];
  const rows = (preview && preview.rows) || [];
  const head = '<tr>' + columns.map(c => rccColumnHeadHtml(c)).join('') + '</tr>';
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

/* 生效分组维度（兼容旧 groupBy 与新的有序 groupings；none 归一为空列表） */
function rccEffectiveGroupings(preview) {
  if (preview && Array.isArray(preview.groupings) && preview.groupings.length) {
    return preview.groupings.filter(k => k && k !== 'none');
  }
  if (preview && preview.groupBy && preview.groupBy !== 'none') return [preview.groupBy];
  return [];
}

function rccResultHtml(preview) {
  if (!preview) return rccEmptyHtml();
  const evidence = preview.evidence || {};
  const coverage = evidence.coverage || 'current-page';
  const coverageText = coverage === 'matched-set'
    ? '有界匹配集（≤1000 条一致快照，非全量合计）'
    : (coverage === 'current-page' ? '当前预览页（非全量合计）' : coverage);
  const parts = [];
  if (rccEffectiveGroupings(preview).length) parts.push(rccGroupHtml(preview));
  parts.push(rccPivotResultHtml(preview));
  parts.push(rccTableHtml(preview));
  parts.push(rccComputedEvidenceHtml(preview));
  parts.push(rccMetricsHtml(preview));
  parts.push('<div class="rcc-evidence">' + rccEsc(evidence.grain || '')
    + ' · ' + rccEsc(evidence.currencyUnitSemantics || '')
    + ' · 覆盖口径：' + rccEsc(coverageText) + '</div>');
  if (evidence.disclaimerText) parts.push('<div class="rcc-disclaimer">' + rccEsc(evidence.disclaimerText) + '</div>');
  if (preview.sortEvidence) parts.push('<div class="rcc-sort-evidence">' + rccEsc(preview.sortEvidence) + '</div>');
  const matchedSuffix = coverage === 'matched-set'
    ? ' · 匹配 ' + rccEsc(preview.matchedCount !== undefined ? preview.matchedCount : preview.total) + ' 条'
    : '';
  parts.push('<div class="rcc-meta">第 ' + rccEsc(preview.page) + '/' + rccEsc(preview.totalPages)
    + ' 页 · 共 ' + rccEsc(preview.total) + ' 条' + matchedSuffix + '</div>');
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
  const grouping = (state.groupings && state.groupings.length) ? state.groupings : ['none'];
  return {
    schemaVersion: (state.catalog && state.catalog.schemaVersion) || 1,
    datasetKey: state.datasetKey || '',
    fields,
    filters,
    grouping,
    aggregates: rccBuildAggregates(state),
    coverage: state.coverage || 'current-page',
    capabilities: [],
    computedColumns: rccBuildComputedColumns(state),
    relations: rccBuildRelations(state),
    pivot: rccBuildPivot(state),
    presentation: {
      page: 1,
      pageSize: state.pageSize || RCC_DEFAULT_PAGE_SIZE,
      sortFieldKey: state.sortFieldKey || null,
      sortDirection: state.sortFieldKey ? (state.sortDirection || 'asc') : null,
    },
  };
}

/* 组装有界预览请求：页码 / 每页条数 / 分组键受后端校验，固定发布版本可选 */
function rccBuildPreviewRequest(state) {
  return {
    configurationId: state.current ? state.current.id : 0,
    revisionVersion: state.previewRevision || null,
    page: state.page || 1,
    pageSize: state.pageSize || RCC_DEFAULT_PAGE_SIZE,
    groupings: (state.groupings && state.groupings.length) ? state.groupings : ['none'],
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

/* ERP-271：有序复合分组维度选择器（最多两个有区别的基础维度，保持选择顺序） */
function rccGroupingHtml(groupingDimensions, groupings) {
  const dims = (groupingDimensions && groupingDimensions.length) ? groupingDimensions
    : [{ key: 'customer', label: '按客户分组' }, { key: 'month', label: '按月份分组' }];
  const first = (groupings && groupings[0]) || '';
  const second = (groupings && groupings[1]) || '';
  const firstOptions = '<option value="">不分组</option>'
    + dims.map(d => '<option value="' + rccEsc(d.key) + '" ' + (d.key === first ? 'selected' : '')
      + '>' + rccEsc(d.label || RCC_GROUP_LABELS[d.key] || d.key) + '</option>').join('');
  const secondOptions = '<option value="">无（不复合分组）</option>'
    + dims.filter(d => d.key !== first).map(d => '<option value="' + rccEsc(d.key) + '" ' + (d.key === second ? 'selected' : '')
      + '>' + rccEsc(d.label || RCC_GROUP_LABELS[d.key] || d.key) + '</option>').join('');
  return '<div>第一分组：<select onchange="rccOnGrouping(0, this.value)">' + firstOptions + '</select></div>'
    + '<div>第二分组：<select onchange="rccOnGrouping(1, this.value)" ' + (first ? '' : 'disabled') + '>'
    + secondOptions + '</select></div>';
}

/* ERP-272：有界透视选择器（仅目录分组维度；行 / 列不同；与普通分组互斥由后端校验兜底） */
function rccPivotHtml() {
  const ds = rccCurrentDataset();
  const dims = (ds && ds.groupingDimensions && ds.groupingDimensions.length) ? ds.groupingDimensions
    : [{ key: 'customer', label: '按客户分组' }, { key: 'month', label: '按月份分组' }];
  if (!dims || dims.length < 2) return '';
  const p = RCC.pivot || { rowDimension: '', columnDimension: '' };
  const rowOptions = '<option value="">不透视</option>'
    + dims.map(d => '<option value="' + rccEsc(d.key) + '" ' + (d.key === p.rowDimension ? 'selected' : '')
      + '>' + rccEsc(d.label || RCC_GROUP_LABELS[d.key] || d.key) + '</option>').join('');
  const columnOptions = '<option value="">不透视</option>'
    + dims.filter(d => d.key !== p.rowDimension).map(d => '<option value="' + rccEsc(d.key) + '" '
      + (d.key === p.columnDimension ? 'selected' : '') + '>' + rccEsc(d.label || RCC_GROUP_LABELS[d.key] || d.key) + '</option>').join('');
  return '<div class="rcc-pivot"><label>透视（有界：1 行维度 + 1 不同列维度 + 指标，最多 4 个指标）</label>'
    + '<div>行维度：<select onchange="rccOnPivotDimension(\'row\', this.value)">' + rowOptions + '</select></div>'
    + '<div>列维度：<select onchange="rccOnPivotDimension(\'column\', this.value)" ' + (p.rowDimension ? '' : 'disabled') + '>'
    + columnOptions + '</select></div></div>';
}

function rccOnPivotDimension(axis, value) {
  const key = String(value || '').trim();
  RCC.pivot = RCC.pivot || { rowDimension: '', columnDimension: '' };
  if (axis === 'row') {
    RCC.pivot.rowDimension = key;
    if (!key || RCC.pivot.columnDimension === key) RCC.pivot.columnDimension = '';
  } else {
    RCC.pivot.columnDimension = key;
  }
  RCC.page = 1;
  rccTouch();
  rccRenderDesigner();
}

/* 排序：仅目录白名单可排序字段（有限持久化键）；切换即重置页码并作废在途旧响应 */
function rccSortingHtml(dataset) {
  const sortable = ((dataset && dataset.fields) || []).filter(f => f && !f.hidden && f.sortable);
  const opts = sortable.map(f =>
    '<option value="' + rccEsc(f.key) + '" ' + (f.key === RCC.sortFieldKey ? 'selected' : '') + '>' + rccEsc(f.label) + '</option>').join('');
  return '<select onchange="rccOnSortField(this.value)">'
    + '<option value="" ' + (!RCC.sortFieldKey ? 'selected' : '') + '>默认排序</option>'
    + opts
    + '</select>'
    + '<select onchange="rccOnSortDirection(this.value)" ' + (RCC.sortFieldKey ? '' : 'disabled') + '>'
    + '<option value="asc" ' + (RCC.sortDirection === 'asc' ? 'selected' : '') + '>升序</option>'
    + '<option value="desc" ' + (RCC.sortDirection === 'desc' ? 'selected' : '') + '>降序</option>'
    + '</select>';
}

function rccOnSortField(value) {
  RCC.sortFieldKey = value || '';
  RCC.sortDirection = 'asc';
  RCC.page = 1;                     // 切换排序：重置页码
  rccTouch();                        // 作废在途旧预览 / 旧配置响应
  rccRenderDesigner();
}

function rccOnSortDirection(value) {
  RCC.sortDirection = value === 'desc' ? 'desc' : 'asc';
  RCC.page = 1;                     // 切换排序方向：重置页码
  rccTouch();
  rccRenderDesigner();
}

/* ==================== 指标汇总（ERP-267）：目录驱动、仅当前预览页、分组 + 币种分区 ==================== */

const RCC_METRIC_FUNCTION_LABELS = { sum: '合计', count: '计数', avg: '平均', min: '最小', max: '最大' };

function rccFunctionLabel(fn) {
  return RCC_METRIC_FUNCTION_LABELS[fn] || String(fn || '');
}

/* 组装已选中的指标（字段只来自目录 metric 白名单、函数只来自允许函数；未知键 / 函数丢弃） */
function rccBuildAggregates(state) {
  const ds = rccCurrentDataset();
  const metrics = (ds && ds.metrics) || [];
  const valid = new Set(metrics.map(m => m && m.key).filter(Boolean));
  return ((state && state.aggregates) || []).map(a => ({
    function: String(a && a.function || '').trim(),
    fieldKey: String(a && a.fieldKey || '').trim(),
  })).filter(a => {
    if (!a.fieldKey || !a.function || !valid.has(a.fieldKey)) return false;
    const m = metrics.find(x => x.key === a.fieldKey);
    return !!m && ((m.allowedFunctions || []).indexOf(a.function) >= 0);
  });
}

/* 组装已选中的受控关系（关系键 / 字段只来自目录 relation 白名单；未知键 / 字段丢弃，fail closed） */
function rccBuildRelations(state) {
  const ds = rccCurrentDataset();
  const catalog = (ds && ds.relations) || [];
  const byKey = new Map(catalog.map(r => [String(r && r.key || '').trim(), r]));
  return ((state && state.relations) || []).map(r => ({
    relationKey: String(r && r.relationKey || '').trim(),
    fields: ((r && r.fields) || []).map(f => String(f || '').trim()).filter(Boolean),
  })).filter(r => {
    if (!r.relationKey || !byKey.has(r.relationKey)) return false;
    const meta = byKey.get(r.relationKey);
    const allowed = new Set(((meta && meta.fields) || []).map(f => f && f.key).filter(Boolean));
    if (!r.fields.length) return false;
    r.fields = r.fields.filter(f => allowed.has(f));
    return r.fields.length > 0;
  });
}

/* ERP-272：有界透视（一个行维度 + 一个不同列维度；维度只来自目录分组维度，指标复用 rccBuildAggregates） */
function rccBuildPivot(state) {
  const p = (state && state.pivot) || {};
  const row = String(p.rowDimension || '').trim();
  const column = String(p.columnDimension || '').trim();
  if (!row || !column || row === column) return null;
  return { schemaVersion: 1, rowDimension: row, columnDimension: column };
}

/* 受控关系选择器：仅目录 relation 白名单 + 允许字段，绝不渲染任意关系 / 联接 / SQL */
function rccRelationsHtml() {
  const ds = rccCurrentDataset();
  const relations = (ds && ds.relations) || [];
  if (!relations.length) return '';
  const selected = RCC.relations || [];
  const rows = relations.map(rel => {
    const relKey = String(rel.key || '').trim();
    const picked = (selected.find(s => s.relationKey === relKey) || {}).fields || [];
    const checks = (rel.fields || []).map(f => {
      const fk = String(f.key || '').trim();
      const on = picked.indexOf(fk) >= 0 ? ' checked' : '';
      return '<label class="rcc-relation-field"><input type="checkbox" data-rel="' + rccEsc(relKey)
        + '" data-field="' + rccEsc(fk) + '"' + on
        + ' onchange="rccOnRelationField(this.getAttribute(\'data-rel\'), this.getAttribute(\'data-field\'), this.checked)">'
        + rccEsc(f.label) + '</label>';
    }).join('');
    return '<div class="rcc-relation-row"><div class="rcc-relation-key">' + rccEsc(rel.label)
      + '<span class="rcc-muted">（' + rccEsc(rel.cardinality || '') + '）</span></div>'
      + '<div class="rcc-relation-fields">' + checks + '</div></div>';
  }).join('');
  return '<div class="rcc-relations"><label>客户维度（受控关系，仅客户编码 / 国别）</label>' + rows + '</div>';
}

function rccOnRelationField(relKey, fieldKey, checked) {
  let item = (RCC.relations || []).find(r => r.relationKey === relKey);
  if (!item) {
    if (!checked) return;
    item = { relationKey: relKey, fields: [] };
    RCC.relations.push(item);
  }
  const idx = item.fields.indexOf(fieldKey);
  if (checked && idx < 0) item.fields.push(fieldKey);
  if (!checked && idx >= 0) item.fields.splice(idx, 1);
  if (!item.fields.length) {
    RCC.relations = RCC.relations.filter(r => r !== item);
  }
  rccTouch();
}

function rccMetricFieldOptionsHtml(selected) {
  const ds = rccCurrentDataset();
  const metrics = (ds && ds.metrics) || [];
  return metrics.map(m => '<option value="' + rccEsc(m.key) + '" ' + (m.key === selected ? 'selected' : '') + '>'
    + rccEsc(m.label) + '</option>').join('');
}

function rccMetricFunctionOptionsHtml(fieldKey, selectedFn) {
  const ds = rccCurrentDataset();
  const m = ((ds && ds.metrics) || []).find(x => x.key === fieldKey);
  const fns = (m && m.allowedFunctions) || [];
  return fns.map(f => '<option value="' + rccEsc(f) + '" ' + (f === selectedFn ? 'selected' : '') + '>'
    + rccEsc(rccFunctionLabel(f)) + '</option>').join('');
}

/* 指标编辑器：仅目录 metric 白名单 + 允许函数，最多 4 个，绝不渲染自由函数 / 全匹配合计 */
function rccMetricEditorHtml() {
  const ds = rccCurrentDataset();
  if (!ds || !(ds.metrics || []).length) return '';
  const rows = (RCC.aggregates || []).map((a, i) =>
    '<div class="rcc-filter-row">'
    + '<select onchange="rccOnMetricField(' + i + ', this.value)">' + rccMetricFieldOptionsHtml(a.fieldKey) + '</select>'
    + '<select onchange="rccOnMetricFunction(' + i + ', this.value)">' + rccMetricFunctionOptionsHtml(a.fieldKey, a.function) + '</select>'
    + '<button type="button" class="btn" onclick="rccRemoveMetric(' + i + ')">删除</button></div>').join('');
  return '<div class="rcc-filters"><label>指标汇总（最多 4 个 · 仅当前预览页 · 金额按币种分区）</label>' + rows
    + '<button type="button" class="btn" onclick="rccAddMetric()">+ 添加指标</button></div>';
}

function rccAddMetric() {
  if ((RCC.aggregates || []).length >= 4) { toast('最多添加 4 个指标', 'error'); return; }
  const ds = rccCurrentDataset();
  const m = ((ds && ds.metrics) || [])[0];
  if (!m) { toast('没有可用的指标', 'error'); return; }
  RCC.aggregates = RCC.aggregates || [];
  RCC.aggregates.push({ function: (m.allowedFunctions || [])[0] || 'count', fieldKey: m.key });
  rccTouch();
  rccRenderDesigner();
}

function rccRemoveMetric(i) {
  (RCC.aggregates || []).splice(i, 1);
  rccTouch();
  rccRenderDesigner();
}

function rccOnMetricField(i, value) {
  const a = (RCC.aggregates || [])[i];
  if (!a) return;
  a.fieldKey = value;
  const ds = rccCurrentDataset();
  const m = ((ds && ds.metrics) || []).find(x => x.key === value);
  const fns = (m && m.allowedFunctions) || [];
  a.function = fns.indexOf(a.function) >= 0 ? a.function : (fns[0] || 'count');
  rccTouch();
  rccRenderDesigner();
}

function rccOnMetricFunction(i, value) {
  const a = (RCC.aggregates || [])[i];
  if (a) { a.function = value; rccTouch(); }
}

/* 指标汇总结果：仅当前预览页选中指标，分组 + 币种分区，绝不跨币种 / 单位合并 */
function rccMetricsHtml(preview) {
  const metrics = (preview && preview.metrics) || [];
  if (!metrics.length) return '';
  const rows = metrics.map(m => {
    const unit = m.unit ? '（' + rccEsc(m.unit) + '）' : '';
    const title = '<b>' + rccEsc(m.label) + '（' + rccEsc(rccFunctionLabel(m.function)) + '）' + unit + '</b>';
    const cells = (m.cells || []).map(c => '<div class="rcc-partition">'
      + (c.groupLabel ? '分组：' + rccEsc(c.groupLabel) + ' · ' : '')
      + (c.currency ? '币种：' + rccEsc(c.currency) + ' · ' : '')
      + '数值 ' + rccNumberText(c.value)
      + ' · 已知 ' + rccEsc(c.knownCount)
      + ' · 缺失 ' + rccEsc(c.missingCount)
      + ' · 来源 ' + rccEsc(c.sourceCount)
      + (c.reason ? ' · ' + rccEsc(c.reason) : '')
      + '</div>').join('');
    return '<div class="rcc-group">' + title + cells + '</div>';
  }).join('');
  return '<div class="rcc-metrics"><div class="rcc-metrics-title">指标汇总（当前预览页 · 非全量合计）</div>' + rows + '</div>';
}

/* ERP-272：透视结果矩阵（与 Excel / PDF 同一份轴 / 单元格 / 汇总 / 覆盖证据） */
function rccPivotCellText(parts) {
  if (!parts || !parts.length) return '';
  const sorted = parts.slice().sort((a, b) => String(a.currency || '').localeCompare(String(b.currency || '')));
  return sorted.map(p => {
    const v = (p.value === null || p.value === undefined) ? '' : rccNumberText(p.value);
    return p.currency ? (v === '' ? p.currency : p.currency + ' ' + v) : v;
  }).join(' / ');
}

function rccPivotResultHtml(preview) {
  const pivot = preview && preview.pivot;
  if (!pivot || !(pivot.metrics || []).length) return '';
  const dimLabels = { customer: '客户', month: '月份' };
  const rowHead = dimLabels[pivot.rowDimension] || pivot.rowDimension;
  const head = '<tr><th>' + rccEsc(rowHead) + '</th>'
    + (pivot.columnAxis || []).map(c => '<th>' + rccEsc(c.label) + '</th>').join('') + '</tr>';
  const blocks = (pivot.metrics || []).map(m => {
    const byCell = {};
    (m.cells || []).forEach(c => {
      const k = c.rowIndex + ':' + c.columnIndex;
      byCell[k] = byCell[k] || [];
      byCell[k].push(c);
    });
    const body = (pivot.rowAxis || []).map((r, ri) => '<tr><td>' + rccEsc(r.label) + '</td>'
      + (pivot.columnAxis || []).map((c, ci) => '<td>' + rccEsc(rccPivotCellText(byCell[ri + ':' + ci] || [])) + '</td>').join('')
      + '</tr>').join('');
    const unit = m.unit ? '（' + rccEsc(m.unit) + '）' : '';
    const title = '<div class="rcc-pivot-metric"><b>' + rccEsc(m.label) + '（' + rccEsc(rccFunctionLabel(m.function)) + '）' + unit + '</b></div>';
    const summary = '<div class="rcc-partition">已知 ' + rccEsc(m.knownCount) + ' · 缺失 ' + rccEsc(m.missingCount)
      + ' · 来源 ' + rccEsc(m.sourceCount) + ' · 覆盖 ' + rccEsc(pivot.coverage || 'current-page') + '（非全量合计）</div>';
    return title + '<div class="rcc-table-wrap"><table class="rcc-table"><thead>' + head
      + '</thead><tbody>' + body + '</tbody></table></div>' + summary;
  }).join('');
  return '<div class="rcc-pivot-result"><div class="rcc-pivot-title">透视（当前页 · 非全量合计）</div>' + blocks + '</div>';
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

function rccSupportsMatchedSet(ds) {
  return !!ds && Array.isArray(ds.supportedCapabilities) && ds.supportedCapabilities.indexOf('matched-set') >= 0;
}

function rccCoverageHtml(ds) {
  if (!rccSupportsMatchedSet(ds)) return '';
  const cur = RCC.coverage === 'matched-set' ? 'matched-set' : 'current-page';
  return '<div class="rcc-coverage"><label>覆盖口径</label>'
    + '<select onchange="rccSetCoverage(this.value)">'
    + '<option value="current-page" ' + (cur === 'current-page' ? 'selected' : '') + '>当前预览页（默认）</option>'
    + '<option value="matched-set" ' + (cur === 'matched-set' ? 'selected' : '') + '>有界匹配集（≤1000 条一致快照）</option>'
    + '</select>'
    + '<div class="rcc-hint">有界匹配集读取 ≤1000 条一致快照，仍只展示选中页；全量合计后续版本提供</div></div>';
}

function rccSetCoverage(v) {
  RCC.coverage = v === 'matched-set' ? 'matched-set' : 'current-page';
  rccTouch();
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
    + rccComputedColumnsHtml()
    + rccRelationsHtml()
    + '<div class="rcc-filters"><label>类型化筛选</label>' + rccFiltersHtml(RCC.filters, RCC.fields)
    + '<button type="button" class="btn" onclick="rccAddFilter()">+ 添加筛选</button></div>'
    + '<div class="rcc-grouping"><label>分组</label>' + rccGroupingHtml(ds.groupingDimensions, RCC.groupings) + '</div>'
    + rccPivotHtml()
    + '<div class="rcc-sorting"><label>排序（仅持久化键）</label>' + rccSortingHtml(ds)
    + '<div class="rcc-hint">' + rccEsc(ds.sortingExplanation || '仅订单 / 发票原生键可排序') + '</div></div>'
    + rccCoverageHtml(ds)
    + rccMetricEditorHtml()
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
    selectedKeys: [], filters: [], computedColumns: [], aggregates: [], groupings: [], coverage: 'current-page', sortFieldKey: '', sortDirection: 'asc', page: 1, pageSize: RCC_DEFAULT_PAGE_SIZE,
    maxPageSize: 200, previewRevision: null, view: null, revisions: [],
    sharedList: [], sharedCurrent: null, grants: [], dirty: false,
    requestSeq: 0, envBlocked: false, busy: false,
  };
  rccRenderDesigner();
  rccRenderList([]);
  rccRenderShared();
  rccRenderGrants();
  rccRenderResult(rccEmptyHtml());
  await rccLoadCatalog();
  await rccLoadList();
  await rccLoadShared();
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
  RCC.computedColumns = [];
  RCC.aggregates = [];
  RCC.relations = [];
  RCC.groupings = [];
  RCC.pivot = { rowDimension: '', columnDimension: '' };
  RCC.sortFieldKey = '';
  RCC.sortDirection = 'asc';
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

function rccOnGrouping(index, value) {
  const key = (value || '').trim();
  const cur = (RCC.groupings || []).slice(0, 2);
  if (index === 0) {
    if (!key) RCC.groupings = [];
    else if (cur[1] === key) RCC.groupings = [key, cur[0]];
    else RCC.groupings = [key, cur[1]].filter(Boolean).slice(0, 2);
  } else {
    if (!key) RCC.groupings = cur.slice(0, 1);
    else RCC.groupings = [cur[0], key].filter(Boolean).slice(0, 2);
  }
  rccTouch();
  rccRenderDesigner();
}

function rccNew() {
  RCC.current = null;
  RCC.sharedCurrent = null;
  RCC.name = '';
  RCC.previewRevision = null;
  RCC.selectedKeys = rccSelectFields(RCC.fields, RCC.fields.map(f => f.key));
  RCC.filters = [];
  RCC.computedColumns = [];
  RCC.aggregates = [];
  RCC.groupings = [];
  RCC.pivot = { rowDimension: '', columnDimension: '' };
  RCC.sortFieldKey = '';
  RCC.sortDirection = 'asc';
  RCC.page = 1;
  RCC.dirty = false;
  RCC.revisions = [];
  RCC.grants = [];
  rccRenderDirty();
  rccRenderDesigner();
  rccRenderResult(rccEmptyHtml());
  rccRenderRevisions();
  rccRenderGrants();
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
  RCC.groupings = ((def && def.grouping) || []).filter(k => k && k !== 'none').slice(0, 2);
  RCC.coverage = (def && def.coverage === 'matched-set') ? 'matched-set' : 'current-page';
  RCC.page = (def && def.presentation && def.presentation.page) || 1;
  RCC.pageSize = (def && def.presentation && def.presentation.pageSize) || RCC_DEFAULT_PAGE_SIZE;
  RCC.sortFieldKey = (def && def.presentation && def.presentation.sortFieldKey) || '';
  RCC.sortDirection = (def && def.presentation && def.presentation.sortDirection) || 'asc';
  RCC.computedColumns = ((def && def.computedColumns) || []).map(c => ({
    key: String(c.key || '').trim(),
    label: String(c.label || ''),
    expression: rccNormalizeFormulaNode(c.expression) || { kind: 'field', fieldKey: '' },
  })).filter(c => c.key);
  RCC.aggregates = ((def && def.aggregates) || []).map(a => ({
    function: String(a.function || '').trim(),
    fieldKey: String(a.fieldKey || '').trim(),
  })).filter(a => a.fieldKey && a.function);
  RCC.relations = ((def && def.relations) || []).map(r => ({
    relationKey: String(r.relationKey || '').trim(),
    fields: ((r && r.fields) || []).map(f => String(f || '').trim()).filter(Boolean),
  })).filter(r => r.relationKey && r.fields.length);
  RCC.pivot = {
    rowDimension: String(((def && def.pivot) || {}).rowDimension || '').trim(),
    columnDimension: String(((def && def.pivot) || {}).columnDimension || '').trim(),
  };
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
  RCC.sharedCurrent = null;
  RCC.name = dto.name || '';
  RCC.previewRevision = null;
  rccApplyDefinition(dto.definition || {});
  RCC.dirty = false;
  rccRenderDirty();
  rccRenderDesigner();
  rccRenderResult(rccEmptyHtml());
  await rccLoadRevisions(id);
  await rccLoadGrants();
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
  RCC.sharedCurrent = null;
  RCC.name = '';
  RCC.revisions = [];
  RCC.grants = [];
  RCC.dirty = false;
  rccRenderDirty();
  rccRenderDesigner();
  rccRenderResult(rccEmptyHtml());
  rccRenderRevisions();
  rccRenderGrants();
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

/* ==================== 只读共享（ERP-265 Stage 1）：owned / shared 区分 ==================== */

function rccRenderShared() {
  const el = document.getElementById('rcc-shared');
  if (!el) return;
  const list = RCC.sharedList || [];
  if (!list.length) {
    el.innerHTML = '<div class="rcc-shared-title">共享给我的</div><div class="empty">暂无共享报表</div>';
    return;
  }
  el.innerHTML = '<div class="rcc-shared-title">共享给我的</div>'
    + list.map(item => '<div class="rcc-list-item rcc-shared-item">'
      + '<div class="rcc-list-title">' + rccEsc(item.name) + ' <span class="status status-info">共享</span></div>'
      + '<div class="rcc-list-meta">' + rccEsc(rccDatasetLabel(item.datasetKey))
      + ' · v' + rccEsc(item.revisionVersion) + ' · 来自 ' + rccEsc(item.ownerDisplayName || item.ownerUserId) + '</div>'
      + '<div class="rcc-list-actions">'
      + '<button type="button" class="btn" onclick="rccOpenShared(' + item.reportConfigurationId + ')">查看</button>'
      + '<button type="button" class="btn" onclick="rccCopyShared(' + item.reportConfigurationId + ')">复制为草稿</button>'
      + '</div></div>').join('');
}

async function rccLoadShared() {
  const env = await rccFetch(RCC_API + '/shared');
  if (env.code === 2000 || env.code === 2003) { rccOnUnauthorized(env.message); return; }
  if (env.code !== 0) {
    if (env.code === 5000) { RCC.envBlocked = true; rccRenderEnvBanner(); }
    RCC.sharedList = [];
    rccRenderShared();
    return;
  }
  RCC.sharedList = env.data || [];
  rccRenderShared();
}

function rccSharedViewHtml(dto) {
  return '<div class="rcc-shared-view">'
    + '<div class="rcc-shared-title">共享报表（只读）</div>'
    + '<div class="rcc-meta">名称：' + rccEsc(dto.name)
    + ' · 数据集：' + rccEsc(rccDatasetLabel(dto.datasetKey))
    + ' · 固定修订 v' + rccEsc(dto.revisionVersion)
    + ' · 发布于 ' + rccEsc(String((dto.publishedAt || '')).slice(0, 10)) + '</div>'
    + '<div class="rcc-hint">这是别人分享给你的固定发布修订快照，无法编辑原配置；可预览 / 导出，或复制为自有草稿。</div>'
    + '</div>';
}

async function rccOpenShared(id) {
  if (RCC.dirty) { toast('当前存在未保存编辑，请先保存或新建', 'error'); return; }
  const env = await rccFetch(RCC_API + '/shared/' + id);
  if (env.code === 2000 || env.code === 2003) { rccOnUnauthorized(env.message); return; }
  if (env.code !== 0) {
    if (env.code === 5000) { RCC.envBlocked = true; rccRenderEnvBanner(); }
    rccRenderResult(rccErrorHtml(rccKindOfCode(env.code), env.message || '加载失败'));
    return;
  }
  const dto = env.data;
  RCC.sharedCurrent = dto;
  RCC.current = { id: dto.reportConfigurationId, name: dto.name, version: dto.revisionVersion };
  RCC.name = dto.name || '';
  RCC.previewRevision = dto.revisionVersion;
  rccApplyDefinition(dto.definition || {});
  RCC.dirty = false;
  rccRenderDirty();
  rccRenderDesigner('<div class="rcc-hint">共享视图（只读）：无法编辑原配置，可预览 / 导出或复制为自有草稿。</div>');
  rccRenderResult(rccSharedViewHtml(dto));
  rccRenderRevisions();
  rccRenderGrants();
}

async function rccCopyShared(id) {
  const env = await rccFetch(RCC_API + '/shared/' + id + '/copy', 'POST');
  if (env.code === 2000 || env.code === 2003) { rccOnUnauthorized(env.message); return; }
  if (env.code === 5000) { RCC.envBlocked = true; rccRenderEnvBanner(); return; }
  if (env.code !== 0) { toast(env.message || '复制失败', 'error'); return; }
  toast('已复制为自有草稿');
  await rccLoadList();
  await rccLoadShared();
}

function rccRenderGrants() {
  const el = document.getElementById('rcc-grants');
  if (!el) return;
  if (!RCC.current || !RCC.current.id || RCC.sharedCurrent) { el.innerHTML = ''; return; }
  const grants = RCC.grants || [];
  const rows = grants.length
    ? grants.map(g => '<div class="rcc-grant-row">'
        + '<span>' + rccEsc(g.recipientDisplayName || g.recipientUserName || g.recipientUserId)
        + ' · 固定修订 v' + rccEsc(g.revisionVersion) + '</span>'
        + '<button type="button" class="btn btn-danger" onclick="rccRevoke(' + g.recipientUserId + ', ' + g.version + ')">撤销</button>'
        + '</div>').join('')
    : '<div class="rcc-hint">暂无共享授权</div>';
  el.innerHTML = '<div class="rcc-grants-title">共享授权（owner-only）</div>'
    + '<div class="rcc-grant-form">'
    + '<input id="rcc-grant-recipient" placeholder="被授权用户 Id">'
    + '<input id="rcc-grant-revision" placeholder="固定发布修订号">'
    + '<button type="button" class="btn" onclick="rccGrant()">授权</button>'
    + '</div>'
    + rows;
}

async function rccLoadGrants() {
  if (!RCC.current || !RCC.current.id || RCC.sharedCurrent) { RCC.grants = []; rccRenderGrants(); return; }
  const env = await rccFetch(RCC_API + '/' + RCC.current.id + '/grants');
  if (env.code === 2000 || env.code === 2003) { rccOnUnauthorized(env.message); return; }
  if (env.code !== 0) {
    if (env.code === 5000) { RCC.envBlocked = true; rccRenderEnvBanner(); }
    RCC.grants = [];
    rccRenderGrants();
    return;
  }
  RCC.grants = env.data || [];
  rccRenderGrants();
}

async function rccGrant() {
  if (!RCC.current || !RCC.current.id || RCC.sharedCurrent) { toast('请先选择自有配置', 'error'); return; }
  const recipientEl = document.getElementById('rcc-grant-recipient');
  const revisionEl = document.getElementById('rcc-grant-revision');
  const recipientUserId = parseInt((recipientEl && recipientEl.value) || '', 10);
  const revisionVersion = parseInt((revisionEl && revisionEl.value) || '', 10);
  if (!Number.isFinite(recipientUserId) || recipientUserId <= 0) { toast('请输入被授权用户 Id', 'error'); return; }
  if (!Number.isFinite(revisionVersion) || revisionVersion <= 0) { toast('请输入固定发布修订号', 'error'); return; }
  const env = await rccFetch(RCC_API + '/' + RCC.current.id + '/grants', 'POST', { recipientUserId, revisionVersion });
  if (env.code === 2000 || env.code === 2003) { rccOnUnauthorized(env.message); return; }
  if (env.code === 5000) { RCC.envBlocked = true; rccRenderEnvBanner(); rccRenderResult(rccEnvBlockedHtml(env.message)); return; }
  if (env.code === 1004) { toast('版本冲突：授权已被其他操作修改，未覆盖，请刷新后重试', 'error'); await rccLoadGrants(); return; }
  if (env.code !== 0) { toast(env.message || '授权失败', 'error'); return; }
  toast('授权成功');
  await rccLoadGrants();
}

async function rccRevoke(recipientUserId, version) {
  if (!RCC.current || !RCC.current.id) return;
  if (!confirm('确认撤销该用户的共享授权？')) return;
  const env = await rccFetch(RCC_API + '/' + RCC.current.id + '/grants/' + recipientUserId + '?version=' + version, 'DELETE');
  if (env.code === 2000 || env.code === 2003) { rccOnUnauthorized(env.message); return; }
  if (env.code === 5000) { RCC.envBlocked = true; rccRenderEnvBanner(); return; }
  if (env.code === 1004) { toast('版本冲突：授权已被其他操作修改，未撤销，请刷新后重试', 'error'); await rccLoadGrants(); return; }
  if (env.code !== 0) { toast(env.message || '撤销失败', 'error'); return; }
  toast('撤销成功');
  await rccLoadGrants();
}

async function rccPreview() {
  if (RCC.busy) return;
  if (!RCC.current) { rccRenderResult(rccErrorHtml('invalid', '请先选择或保存一个报表配置')); return; }
  if (RCC.dirty) { rccRenderResult(rccErrorHtml('invalid', '存在未保存编辑，请先保存后再预览')); return; }
  const seq = ++RCC.requestSeq;   // 本次预览的令牌：迟到响应一律丢弃
  RCC.lastAction = 'preview';
  RCC.busy = true;
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
  } finally {
    RCC.busy = false;
  }
}

function rccPreviewRevision(version) {
  RCC.previewRevision = version;
  rccPreview();
}

/* 导出当前预览页为 Excel（ERP-263，只读）：复用预览请求体 POST /api/report-configurations/export；
   成功（xlsx 附件）触发下载；授权 / 无效 / 环境未就绪 / 网络失败在结果区可见，不下载任何内容，
   且绝不覆盖未保存编辑（保留 dirty 状态与设计器控件）。 */
async function rccExport() {
  if (RCC.busy) return;
  if (!RCC.current) { rccRenderResult(rccErrorHtml('invalid', '请先选择或保存一个报表配置')); return; }
  if (RCC.dirty) { rccRenderResult(rccErrorHtml('invalid', '存在未保存编辑，请先保存后再导出')); return; }
  RCC.lastAction = 'export';
  RCC.busy = true;
  const req = rccBuildPreviewRequest(RCC);
  try {
    const resp = await fetch(RCC_API + '/export', {
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
      a.download = '报表配置_' + dateStr + '.xlsx';
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
      return;   // 导出成功：不触碰任何未保存控件 / 设计器状态
    }

    let envelope = null;
    try { envelope = await resp.json(); } catch (e) { /* 忽略解析失败 */ }
    const code = envelope && envelope.code;
    const message = (envelope && envelope.message) || '导出失败';
    if (code === 2000 || code === 2003) { rccOnUnauthorized(message); return; }
    if (code === 5000) { RCC.envBlocked = true; rccRenderEnvBanner(); rccRenderResult(rccEnvBlockedHtml(message)); return; }
    rccRenderResult(rccErrorHtml(rccKindOfCode(code), message));   // 失败只显示错误，绝不覆盖未保存编辑
  } catch (err) {
    rccRenderResult(rccErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  } finally {
    RCC.busy = false;
  }
}

/* 导出当前预览页为中文 PDF（ERP-264，只读）：复用预览请求体 POST /api/report-configurations/export/pdf；
   成功（application/pdf 附件）触发下载；授权 / 无效 / 环境未就绪 / 字体缺失 / 渲染失败在结果区可见，
   不下载任何内容，且绝不覆盖未保存编辑（保留 dirty 状态与设计器控件）；迟到响应一律丢弃。 */
async function rccExportPdf() {
  if (RCC.busy) return;
  if (!RCC.current) { rccRenderResult(rccErrorHtml('invalid', '请先选择或保存一个报表配置')); return; }
  if (RCC.dirty) { rccRenderResult(rccErrorHtml('invalid', '存在未保存编辑，请先保存后再导出')); return; }
  const seq = ++RCC.requestSeq;   // 本次下载的令牌：迟到响应一律丢弃
  RCC.lastAction = 'exportPdf';
  RCC.busy = true;
  const req = rccBuildPreviewRequest(RCC);
  try {
    const resp = await fetch(RCC_API + '/export/pdf', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': 'Bearer ' + (typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : ''),
      },
      body: JSON.stringify(req),
    });

    if (seq !== RCC.requestSeq) return;   // 数据集 / 配置已变化：丢弃迟到的下载响应

    const contentType = (resp.headers.get('content-type') || '');
    if (contentType.indexOf('pdf') >= 0) {
      const blob = await resp.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      const dateStr = new Date().toISOString().slice(0, 10).replace(/-/g, '');
      a.href = url;
      a.download = '报表配置_' + dateStr + '.pdf';
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
      return;   // 导出成功：不触碰任何未保存控件 / 设计器状态
    }

    let envelope = null;
    try { envelope = await resp.json(); } catch (e) { /* 忽略解析失败 */ }
    const code = envelope && envelope.code;
    const message = (envelope && envelope.message) || '导出失败';
    if (code === 2000 || code === 2003) { rccOnUnauthorized(message); return; }
    if (code === 5000) { RCC.envBlocked = true; rccRenderEnvBanner(); rccRenderResult(rccEnvBlockedHtml(message)); return; }
    rccRenderResult(rccErrorHtml(rccKindOfCode(code), message));   // 失败只显示错误，绝不覆盖未保存编辑
  } catch (err) {
    if (seq !== RCC.requestSeq) return;
    rccRenderResult(rccErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  } finally {
    RCC.busy = false;
  }
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
    + '<button type="button" class="btn" onclick="rccExport()">导出</button>'
    + '<button type="button" class="btn" onclick="rccExportPdf()">导出PDF</button>'
    + '<span id="rcc-dirty" class="rcc-dirty"></span>'
    + '</div>'
    + '<div class="rcc-layout">'
    + '<aside class="rcc-list" id="rcc-list"></aside>'
    + '<aside class="rcc-shared" id="rcc-shared"></aside>'
    + '<section class="rcc-designer" id="rcc-designer"></section>'
    + '<section class="rcc-result" id="rcc-result"></section>'
    + '<section class="rcc-revisions" id="rcc-revisions"></section>'
    + '<section class="rcc-grants" id="rcc-grants"></section>'
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
    rccBuildPivot,
    rccPivotHtml,
    rccOnPivotDimension,
    rccPivotCellText,
    rccPivotResultHtml,
    rccSortingHtml,
    rccOnSortField,
    rccOnSortDirection,
    rccDatasetLabel,
    rccRenderDesigner,
    rccRenderList,
    rccRenderRevisions,
    rccRenderShared,
    rccRenderGrants,
    rccRenderResult,
    rccFetch,
    rccInit,
    rccLoadCatalog,
    rccLoadList,
    rccLoadShared,
    rccSelectDataset,
    rccApplyDefinition,
    rccLoadConfiguration,
    rccOpenShared,
    rccSharedViewHtml,
    rccCopyShared,
    rccSave,
    rccCopy,
    rccRename,
    rccDelete,
    rccPublish,
    rccRestore,
    rccLoadRevisions,
    rccLoadGrants,
    rccGrant,
    rccRevoke,
    rccPreview,
    rccExport,
    rccExportPdf,
    renderReportConfigurationWorkspace,
  };
}
