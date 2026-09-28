/* ============ 只读出口字段完整度工作台（ERP-107：只读派生） ============
   口径与后端 ProductExportFieldCompletenessRules 一一对应：
   - 只读取启用商品资料的英文报关品名、装箱单位与每箱数量、外箱尺寸 / 毛重、出口退税率字段；
   - 每个字段只报告「已填写 / 空白 / 为 0 / 无效值」，区分空白与 0、未知，绝不生成通过 / 不通过结论；
   - 不做报关合规、退税资格或税率结论；不读取图片、不改写商品 / 单证 / 报关单、不调用外部服务；
   - 只读：不写任何表，页面只提供「查询」与「编辑」跳转到既有商品编辑流程。 */

const PEFC_API = '/api/base/products/export-field-completeness';

/* 字段状态文案 / 样式（与后端 State 常量一一对应） */
const PEFC_STATE_LABELS = {
  present: '已填写',
  blank: '空白',
  zero: '为 0',
  unknown: '无效',
};
const PEFC_STATE_CLASS = {
  present: 'status-success',
  blank: 'status-warning',
  zero: 'status-warning',
  unknown: 'status-danger',
};

/* 字段顺序（与后端 BuildFields 顺序一致，用于表头与单元格渲染） */
const PEFC_FIELDS = [
  { key: 'englishDeclareName', label: '英文报关品名' },
  { key: 'packageUnit', label: '装箱单位' },
  { key: 'unitsPerPackage', label: '每箱数量' },
  { key: 'outerLength', label: '外箱长(cm)' },
  { key: 'outerWidth', label: '外箱宽(cm)' },
  { key: 'outerHeight', label: '外箱高(cm)' },
  { key: 'outerWeight', label: '外箱毛重(kg)' },
  { key: 'refundRate', label: '出口退税率(%)' },
];

/* 工具栏入口（商品资料页）：渲染独立只读工作台页 */
function openProductExportFieldCompleteness() {
  CURRENT_PAGE_CODE = 'product-export-field-completeness';
  document.getElementById('header-title').textContent = '出口字段完整度工作台';
  document.getElementById('content').innerHTML = `
    <div class="page-hero report-hero">
      <h2>🧾 出口字段完整度工作台</h2>
      <p>只读核对启用商品资料的出口 / 装箱字段填写与缺口（不判断报关合规 / 退税资格）</p>
    </div>
    <div class="pd-hint" style="margin-bottom:10px">
      ⚠️ <b>只读证据视图</b>：每个字段只报告「已填写 / 空白 / 为 0 / 无效值」，<b>不做</b>报关合规、退税资格或税率结论；
      不读取图片、不改写商品 / 单证 / 报关单。
    </div>
    <div class="toolbar">
      <div class="toolbar-left" style="flex-wrap:wrap;gap:8px;align-items:center">
        <label>关键字 <input type="text" id="pefc-keyword" style="width:180px" placeholder="商品编码 / 名称" onkeydown="if(event.key==='Enter')loadProductExportFieldCompleteness(1)"></label>
        <label>完整度 <select id="pefc-group" style="min-width:170px" onchange="loadProductExportFieldCompleteness(1)">
          <option value="all">全部商品</option>
          <option value="complete">完整（无缺口）</option>
          <option value="incomplete">有缺口</option>
          <option value="declaration">英文报关品名缺失</option>
          <option value="packing">装箱信息缺失</option>
          <option value="dimensions">外箱尺寸 / 重量缺失</option>
          <option value="refund-rate">退税率缺失</option>
        </select></label>
        <label>每页 <input type="number" id="pefc-pagesize" value="20" min="1" max="200" style="width:70px"></label>
      </div>
      <div class="toolbar-actions">
        <button class="btn btn-primary" onclick="loadProductExportFieldCompleteness(1)">查询</button>
      </div>
    </div>
    <div class="kpi-grid" id="pefc-kpi"></div>
    <div class="table-wrap" id="pefc-table">
      <div class="skeleton-row"></div><div class="skeleton-row"></div>
    </div>
    <div class="pd-hint" id="pefc-rule"></div>
    <div class="pagination" id="pefc-pagination"></div>`;
  loadProductExportFieldCompleteness(1);
}

async function loadProductExportFieldCompleteness(page) {
  const keyword = (document.getElementById('pefc-keyword') || {}).value || '';
  const group = (document.getElementById('pefc-group') || {}).value || 'all';
  const pageSize = Number((document.getElementById('pefc-pagesize') || {}).value || 20);
  const qs = `page=${page}&pageSize=${pageSize}&group=${encodeURIComponent(group)}`
    + (keyword ? `&keyword=${encodeURIComponent(keyword)}` : '');
  try {
    const data = await api(`${PEFC_API}?${qs}`);
    renderPefcKpi(data);
    renderPefcTable(data);
    renderPefcRule(data);
    renderPefcPagination(data);
  } catch (e) {
    toast(e.message || '完整度工作台加载失败', 'error');
  }
}

function pefcFieldHtml(f) {
  const label = PEFC_STATE_LABELS[f.state] || f.state || '';
  const cls = PEFC_STATE_CLASS[f.state] || 'status-neutral';
  return `<span class="status ${cls}" title="${escapeHtml(f.text || '')}">${escapeHtml(label)}</span>`;
}

/* 跳转到既有商品编辑流程：先进入商品资料页，再打开该商品的编辑面板 */
function openProductEditor(productId) {
  navigate('product', '商品资料');
  openForm(productId);
}

function renderPefcTable(data) {
  const el = document.getElementById('pefc-table');
  if (!el) return;
  const totalCols = 4 + PEFC_FIELDS.length + 2;
  const head = PEFC_FIELDS.map(f => `<th>${escapeHtml(f.label)}</th>`).join('');
  const bodyRows = (data.items || []).map(r => {
    const fieldCells = PEFC_FIELDS.map(def => {
      const f = (r.fields || []).find(x => x.key === def.key);
      return `<td>${f ? pefcFieldHtml(f) : '<span class="muted">—</span>'}</td>`;
    }).join('');
    const status = r.completeness === 'complete'
      ? '<span class="status status-success">完整</span>'
      : `<span class="status status-warning">有 ${r.gapCount} 个缺口</span>`;
    return `<tr>
      <td>${escapeHtml(r.productCode || '')}</td>
      <td>${escapeHtml(r.productName || '')}</td>
      <td>${escapeHtml(r.spec || '')}</td>
      <td>${escapeHtml(r.unit || '')}</td>
      ${fieldCells}
      <td>${status}</td>
      <td><button class="btn btn-neutral btn-sm" onclick="openProductEditor(${r.productId})">编辑</button></td>
    </tr>`;
  }).join('');
  el.innerHTML = `<table class="table"><thead><tr>
      <th>商品编码</th><th>商品名称</th><th>规格</th><th>单位</th>${head}<th>完整度</th><th>操作</th>
    </tr></thead>
    <tbody>${bodyRows || `<tr><td colspan="${totalCols}" class="empty">暂无启用商品（可调整关键字 / 完整度筛选）</td></tr>`}</tbody></table>`;
}

function renderPefcKpi(data) {
  const el = document.getElementById('pefc-kpi');
  if (!el) return;
  const rows = data.items || [];
  const complete = rows.filter(r => r.completeness === 'complete').length;
  const incomplete = rows.length - complete;
  el.innerHTML = `
    <div class="kpi-card cargo">
      <div class="kpi-label">符合筛选的启用商品</div>
      <div class="kpi-value">${Number(data.total || 0)}<span class="unit">个</span></div>
      <div class="kpi-delta flat">本页 ${rows.length} 个 · 第 ${data.page} 页</div>
    </div>
    <div class="kpi-card gold">
      <div class="kpi-label">本页字段完整</div>
      <div class="kpi-value">${complete}<span class="unit">个</span></div>
      <div class="kpi-delta flat">字段全部已填写</div>
    </div>
    <div class="kpi-card ocean">
      <div class="kpi-label">本页有缺口</div>
      <div class="kpi-value">${incomplete}<span class="unit">个</span></div>
      <div class="kpi-delta flat">仅报告缺口 · 不判断合规 / 退税</div>
    </div>`;
}

function renderPefcRule(data) {
  const el = document.getElementById('pefc-rule');
  if (!el) return;
  el.innerHTML = `
    <div><b>口径</b>：${escapeHtml(data.boundaryText || '')}</div>
    <div><b>只读</b>：${escapeHtml(data.readOnlyText || '')}</div>
    <div><b>边界</b>：${escapeHtml(data.disclaimerText || '')}</div>`;
}

function renderPefcPagination(data) {
  const totalPages = data.pageSize <= 0 ? 0 : Math.ceil(data.total / data.pageSize);
  const el = document.getElementById('pefc-pagination');
  if (!el) return;
  if (totalPages <= 1) { el.innerHTML = ''; return; }
  el.innerHTML = `
    <button class="btn btn-neutral" ${data.page <= 1 ? 'disabled' : ''} onclick="loadProductExportFieldCompleteness(${data.page - 1})">上一页</button>
    <span class="muted" style="margin:0 8px">第 ${data.page} / ${totalPages} 页（共 ${data.total} 条）</span>
    <button class="btn btn-neutral" ${data.page >= totalPages ? 'disabled' : ''} onclick="loadProductExportFieldCompleteness(${data.page + 1})">下一页</button>`;
}

