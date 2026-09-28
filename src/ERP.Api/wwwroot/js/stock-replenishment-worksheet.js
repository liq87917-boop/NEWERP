/* ============ 只读补货工作台（ERP-106：只读派生） ============
   口径与后端 StockReplenishmentRules 一一对应：
   - 按「仓库 + 商品」逐行展示现有库存、商品最低 / 上限库存阈值与启用中的货源关系；
   - 补货建议 = 库存上限 − 现有库存，仅在「低于最低库存且存在有效上限目标」时给出；
   - 阈值缺失 / 无效、货源不可用分别显式标注，绝不推断、绝不跨仓汇总、不做单位换算；
   - 货源信息仅作参考：不自动选择供应商、不生成订单、不改写库存；
   - 只读：不写任何表，页面不提供任何「创建订单 / 选择供应商 / 修改库存」操作。 */

const SRW_API = '/api/stocks/replenishment-worksheet';

/* 建议状态 / 级别文案（与后端 StockReplenishmentRules 常量一一对应） */
const SRW_STATE_LABELS = {
  'replenish': '建议补货',
  'below-min-no-target': '低于最低（无上限目标）',
  'adequate': '库存充足',
  'missing-threshold': '阈值缺失',
  'invalid-threshold': '阈值无效',
};

/* 工具栏入口（库存查询页）：渲染独立补货工作台页，筛选与数据全部走只读接口 GET /api/stocks/replenishment-worksheet */
function openStockReplenishmentWorksheet() {
  CURRENT_PAGE_CODE = 'stock-replenishment-worksheet';
  document.getElementById('header-title').textContent = '库存补货工作台';
  document.getElementById('content').innerHTML = `
    <div class="page-hero report-hero">
      <h2>📦 库存补货工作台</h2>
      <p>按仓库查看现有库存 / 最低 / 上限阈值与建议补货量（只读派生，货源仅作参考，不创建订单、不改库存）</p>
    </div>
    <div class="pd-hint" style="margin-bottom:10px">
      ⚠️ <b>只读证据视图</b>：建议补货量 = 库存上限 − 现有库存，仅在<b>低于最低库存且存在有效上限目标</b>时给出；
      阈值缺失 / 无效与货源不可用会<b>显式标注</b>，不推断、不跨仓汇总、不做单位换算；货源信息<b>不自动选供应商、不下单</b>。
    </div>
    <div class="toolbar">
      <div class="toolbar-left" style="flex-wrap:wrap;gap:8px;align-items:center">
        <label>仓库 <select id="srw-warehouse" style="min-width:160px" onchange="loadStockReplenishmentWorksheet(1)"><option value="">请选择仓库</option></select></label>
        <label>商品 <select id="srw-product" style="min-width:190px" onchange="loadStockReplenishmentWorksheet(1)"><option value="">全部商品</option></select></label>
        <label>商品关键字 <input type="text" id="srw-keyword" style="width:170px" placeholder="编码 / 名称（同时筛选商品下拉）" oninput="loadSrwProductOptions(this.value)"></label>
        <label>每页 <input type="number" id="srw-pagesize" value="20" min="1" max="200" style="width:70px"></label>
      </div>
      <div class="toolbar-actions">
        <button class="btn btn-primary" onclick="loadStockReplenishmentWorksheet(1)">查询</button>
      </div>
    </div>
    <div class="kpi-grid" id="srw-kpi"></div>
    <div class="table-wrap" id="srw-table">
      <div class="skeleton-row"></div><div class="skeleton-row"></div>
    </div>
    <div class="pd-hint" id="srw-rule"></div>
    <div class="pagination" id="srw-pagination"></div>`;
  loadSrwWarehouses();
  loadSrwProductOptions('');
}

/* 仓库下拉：既有基础资料接口；加载后自动选中第一个仓库并加载工作台（仓库为必填筛选） */
async function loadSrwWarehouses() {
  try {
    const data = await api('/api/base/warehouses?page=1&pageSize=200');
    const sel = document.getElementById('srw-warehouse');
    if (!sel) return;
    (data.items || []).forEach(w => {
      const opt = document.createElement('option');
      opt.value = w.id;
      opt.textContent = w.warehouseName || ('仓库 ' + w.id);
      sel.appendChild(opt);
    });
    if (sel.options.length > 1) {
      sel.selectedIndex = 1;   // 自动选中第一个真实仓库
      loadStockReplenishmentWorksheet(1);
    } else {
      document.getElementById('srw-kpi').innerHTML = '';
      document.getElementById('srw-table').innerHTML =
        '<div class="empty"><div style="font-size:48px">📦</div><div>暂无仓库可选，请先在基础资料中维护仓库</div></div>';
    }
  } catch (e) {
    toast(e.message || '仓库下拉加载失败', 'error');
  }
}

/* 商品下拉：复用既有商品资料接口（keyword 匹配编码 / 名称），只登记商品资料里的真实编码 / 名称，不臆造 */
let srwProductSearchTimer = null;
function loadSrwProductOptions(keyword) {
  clearTimeout(srwProductSearchTimer);
  srwProductSearchTimer = setTimeout(async () => {
    try {
      const kw = (keyword || '').trim();
      const data = await api('/api/base/products?page=1&pageSize=50'
        + (kw ? '&keyword=' + encodeURIComponent(kw) : ''));
      const sel = document.getElementById('srw-product');
      if (!sel) return;
      const keep = sel.value;
      sel.innerHTML = '<option value="">全部商品</option>';
      (data.items || []).forEach(p => {
        const opt = document.createElement('option');
        opt.value = p.id;
        opt.textContent = ((p.productCode || '') + ' ' + (p.productName || '')).trim() || ('商品 ' + p.id);
        sel.appendChild(opt);
      });
      if (keep && sel.querySelector(`option[value="${keep}"]`)) sel.value = keep;
    } catch (e) { /* 忽略：商品下拉失败不影响工作台查询 */ }
  }, 250);
}

/* 查询参数：全部由页面筛选控件组装（仓库必填；商品可留空） */
function srwQuery(page) {
  const val = id => { const el = document.getElementById(id); return el ? el.value.trim() : ''; };
  const q = new URLSearchParams();
  if (val('srw-warehouse')) q.set('warehouseId', val('srw-warehouse'));
  if (val('srw-product')) q.set('productId', val('srw-product'));
  q.set('page', page || 1);
  q.set('pageSize', val('srw-pagesize') || '20');
  return q.toString();
}

async function loadStockReplenishmentWorksheet(page) {
  const el = document.getElementById('srw-table');
  if (!el) return;
  if (!document.getElementById('srw-warehouse').value) {
    el.innerHTML = '<div class="empty"><div style="font-size:48px">📦</div><div>请先选择仓库（补货工作台按仓库筛选）</div></div>';
    document.getElementById('srw-kpi').innerHTML = '';
    return;
  }
  el.innerHTML = '<div class="skeleton-row"></div><div class="skeleton-row"></div>';
  try {
    const data = await api(SRW_API + '?' + srwQuery(page));
    renderSrwKpi(data);
    renderSrwTable(data);
    renderSrwPagination(data);
    const rule = document.getElementById('srw-rule');
    if (rule) rule.innerHTML =
      `📌 ${escapeHtml(data.boundaryText || '')}<br>📌 ${escapeHtml(data.readOnlyText || '')}<br>📌 ${escapeHtml(data.disclaimerText || '')}`;
  } catch (e) {
    el.innerHTML = `<div class="empty"><div style="font-size:48px">⚠️</div><div>工作台加载失败：${escapeHtml(e.message)}</div></div>`;
  }
}

function renderSrwKpi(data) {
  const el = document.getElementById('srw-kpi');
  if (!el) return;
  const items = data.items || [];
  const below = items.filter(r => r.belowMinimum).length;
  const replenish = items.filter(r => r.suggestedTopUp !== null && r.suggestedTopUp !== undefined).length;
  const noSourcing = items.filter(r => !r.sourcingAvailable).length;
  el.innerHTML = `
    <div class="kpi-card cargo">
      <div class="kpi-label">符合筛选的库存行</div>
      <div class="kpi-value">${data.total}<span class="unit">行</span></div>
      <div class="kpi-delta flat">本页 ${items.length} 行 · 第 ${data.page} 页</div>
    </div>
    <div class="kpi-card gold">
      <div class="kpi-label">本页低于最低库存</div>
      <div class="kpi-value">${below}<span class="unit">行</span></div>
      <div class="kpi-delta flat">其中可补货 ${replenish} 行</div>
    </div>
    <div class="kpi-card ocean">
      <div class="kpi-label">本页无可用货源</div>
      <div class="kpi-value">${noSourcing}<span class="unit">行</span></div>
      <div class="kpi-delta flat">货源仅作参考 · 不自动选供应商</div>
    </div>`;
}

function srwSourcingHtml(row) {
  const refs = row.sourcing || [];
  if (refs.length === 0) {
    return `<span class="status status-warning">无可用货源</span><div class="muted">${escapeHtml(row.sourcingText || '')}</div>`;
  }
  const parts = refs.map(r => {
    const preferred = r.isPreferred ? ' ⭐' : '';
    const item = r.supplierItemCode ? ` · 货号 ${r.supplierItemCode}` : '';
    const unit = r.purchaseUnit ? ` · ${r.purchaseUnit}` : '';
    const moq = r.minOrderQty > 0 ? ` · MOQ ${r.minOrderQty}` : '';
    const lead = r.leadTimeDays > 0 ? ` · 交期 ${r.leadTimeDays} 天` : '';
    const mark = r.supplierAvailable ? '' : '（已停用 / 不可用）';
    return `${escapeHtml(r.supplierName || ('供应商 ' + r.supplierId))}${preferred}${item}${unit}${moq}${lead}${mark}`;
  });
  return `<div>${parts.join('</div><div>')}</div>`;
}

function srwStateHtml(r) {
  const cls = r.recommendation === 'replenish' ? 'status-success'
    : (r.recommendation === 'adequate' ? 'status-neutral' : 'status-warning');
  return `<span class="status ${cls}">${escapeHtml(SRW_STATE_LABELS[r.recommendation] || r.recommendation || '')}</span>`;
}

function renderSrwTable(data) {
  const el = document.getElementById('srw-table');
  if (!el) return;
  const rows = (data.items || []).map(r => `<tr>
      <td>${escapeHtml(r.productCode || '')}</td>
      <td>${escapeHtml(r.productName || '')}</td>
      <td>${escapeHtml(r.spec || '')}</td>
      <td>${escapeHtml(r.unit || '')}</td>
      <td class="text-right">${fmtMoney(r.quantity)}</td>
      <td class="text-right">${fmtMoney(r.availableQuantity)}</td>
      <td class="text-right">${fmtMoney(r.minStock)}</td>
      <td class="text-right">${fmtMoney(r.maxStock)}</td>
      <td class="text-right">${r.suggestedTopUp === null || r.suggestedTopUp === undefined ? '—' : fmtMoney(r.suggestedTopUp)}</td>
      <td>${srwStateHtml(r)}<div class="muted">${escapeHtml(r.recommendationText || '')}</div></td>
      <td>${srwSourcingHtml(r)}</td>
    </tr>`).join('');
  el.innerHTML = `<table class="table"><thead><tr>
      <th>商品编码</th><th>商品名称</th><th>规格</th><th>单位</th>
      <th class="text-right">现有库存</th><th class="text-right">可用数量</th>
      <th class="text-right">最低库存</th><th class="text-right">库存上限</th>
      <th class="text-right">建议补货量</th><th>建议状态</th><th>货源参考</th>
    </tr></thead>
    <tbody>${rows || '<tr><td colspan="11" class="empty">该仓库暂无库存行（可更换仓库或商品筛选）</td></tr>'}</tbody></table>`;
}

function renderSrwPagination(data) {
  const totalPages = data.pageSize <= 0 ? 0 : Math.ceil(data.total / data.pageSize);
  const el = document.getElementById('srw-pagination');
  if (!el) return;
  if (totalPages <= 1) { el.innerHTML = ''; return; }
  el.innerHTML = `
    <button class="btn btn-neutral" ${data.page <= 1 ? 'disabled' : ''} onclick="loadStockReplenishmentWorksheet(${data.page - 1})">上一页</button>
    <span class="muted" style="margin:0 8px">第 ${data.page} / ${totalPages} 页（共 ${data.total} 条）</span>
    <button class="btn btn-neutral" ${data.page >= totalPages ? 'disabled' : ''} onclick="loadStockReplenishmentWorksheet(${data.page + 1})">下一页</button>`;
}

