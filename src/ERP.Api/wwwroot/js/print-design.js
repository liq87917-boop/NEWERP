/* ============================================================
   ============ 样式设计（打印模板可视化设计器） ==============
   ============================================================
   入口：菜单「系统设置 → 样式设计」（菜单编码 print-design）
   设计目标：可视化、傻瓜式 ——
     · 左侧列出全部可打印单据（业务单据 / 基础资料），点一下即开始设计
     · 中间为表单式配置（抬头、纸张、字号、页脚…），每项都带中文说明
     · 右侧为字段勾选与实时预览（改哪看哪，所见即所得）
     · 一键「智能推荐」自动选好常用字段；一键「恢复默认」回到系统内置模板
   依赖：app.js（api/toast）、bill-print.js（buildPrintHtml / PRINT_DETAIL_LABELS /
         billFieldLabels / parseFieldKeys / ensurePrintStyle）、bill-v2.js（BILL_CONFIG）、
         modules.js（MODULES）
   ============================================================ */

/* 设计器状态 */
let PD = {
  code: '',            // 当前设计的单据类型
  title: '',           // 单据中文名
  template: {},        // 当前模板对象（后端结构）
  labels: {},          // 字段 key → 中文标签
  items: [],           // [{ key, label, checked }]
  timer: null,         // 预览防抖
};

/* 常用字段优先顺序（智能推荐使用） */
const PD_PREFERRED = ['BillNo', 'OrderDate', 'Date', 'CustomerId', 'CustomerName', 'SupplierName',
  'Currency', 'TotalAmount', 'DepositAmount', 'Status', 'Remark', 'SalesmanName',
  'customerCode', 'customerName', 'productCode', 'productName', 'spec', 'unit', 'salePrice'];

/* ============ 模块入口 ============ */
async function renderPrintDesignModule(preCode) {
  CURRENT_LOADER = null;
  ensurePrintStyle();
  document.getElementById('content').innerHTML = `
    <div class="pd-layout">
      <aside class="pd-side card">
        <div class="pd-side-title">📄 单据清单</div>
        <input type="text" id="pd-search" placeholder="搜索单据名称…" oninput="pdFilterDocs(this.value)">
        <div class="pd-doc-list" id="pd-doc-list"></div>
      </aside>

      <section class="pd-main">
        <div class="card pd-head">
          <div class="pd-head-info">
            <div class="pd-doc-name" id="pd-doc-name">请选择左侧单据</div>
            <div class="pd-doc-tip" id="pd-doc-tip">选择单据后即可为它配置打印模板，保存后该单据的「打印 / 打印预览」立即生效</div>
          </div>
          <div class="pd-head-actions">
            <button class="btn btn-neutral" onclick="pdSmartFill()" title="自动勾选常用字段">⚡ 智能推荐</button>
            <button class="btn btn-neutral" onclick="pdLoadDefault()" title="读取系统内置默认模板">↺ 恢复默认</button>
            <button class="btn btn-neutral" onclick="pdOpenPreview()" title="全屏查看打印效果">👁 效果预览</button>
            <button class="btn btn-primary" onclick="pdSave()" id="pd-save-btn">💾 保存模板</button>
          </div>
        </div>

        <div class="pd-empty" id="pd-empty">
          <div class="pd-empty-emoji">🎨</div>
          <div class="pd-empty-title">从左侧选择一张单据开始设计</div>
          <div class="pd-empty-tip">支持的打印对象共 ${pdDocCount()} 种；同一单据可保存多套模板并指定默认</div>
        </div>

        <div class="pd-body" id="pd-body" style="display:none">
          <div class="pd-col pd-col-form" id="pd-col-form"></div>
          <div class="pd-col pd-col-fields" id="pd-col-fields"></div>
          <div class="pd-col pd-col-preview" id="pd-col-preview"></div>
        </div>
      </section>
    </div>`;
  renderPdDocList();
  if (preCode) pdSelectDoc(preCode);
  window.__pdPreSelect = '';     // 预选用完即清空
}

/* 可打印单据总数 */
function pdDocCount() {
  return Object.keys(BILL_CONFIG || {}).length + Object.keys(MODULES || {}).length;
}

/* ============ 左侧：单据清单 ============ */
function pdDocGroups() {
  const bills = Object.keys(BILL_CONFIG || {})
    .map(k => ({ code: k, name: (BILL_CONFIG[k] || {}).title || k, group: '业务单据', icon: '🧾' }));
  const bases = Object.keys(MODULES || {})
    .map(k => ({ code: k, name: (MODULES[k] || {}).title || k, group: '基础资料', icon: '🗂' }));
  return bills.concat(bases);
}

function renderPdDocList(keyword) {
  const kw = (keyword || '').trim().toLowerCase();
  const list = pdDocGroups().filter(d => !kw || d.name.toLowerCase().includes(kw) || d.code.toLowerCase().includes(kw));
  const host = document.getElementById('pd-doc-list');
  if (!host) return;
  if (!list.length) { host.innerHTML = '<div class="pd-side-empty">未找到匹配的单据</div>'; return; }

  host.innerHTML = ['业务单据', '基础资料'].map(g => {
    const items = list.filter(d => d.group === g);
    if (!items.length) return '';
    return `<div class="pd-group">
        <div class="pd-group-title">${g}<span>${items.length}</span></div>
        ${items.map(d => `<div class="pd-doc-item${PD.code === d.code ? ' active' : ''}" onclick="pdSelectDoc('${d.code}')" data-code="${d.code}">
            <span class="pd-doc-ico">${d.icon}</span>
            <span class="pd-doc-label">${escapeHtml(d.name)}</span>
          </div>`).join('')}
      </div>`;
  }).join('');
}

function pdFilterDocs(v) { renderPdDocList(v); }

/* ============ 选择单据 → 载入模板 ============ */
async function pdSelectDoc(code) {
  if (!code) return;
  try {
    PD.code = code;
    PD.title = ((BILL_CONFIG[code] || MODULES[code] || {}).title) || code;
    PD.labels = billFieldLabels(code);
    const tpl = await api(`/api/sys/print-templates/${encodeURIComponent(code)}`);
    PD.template = normalizeTemplate(tpl);
    const order = parseFieldKeys(PD.template.FieldKeys);
    const allKeys = order.concat(Object.keys(PD.labels).filter(k => !order.includes(k)));
    PD.items = allKeys.map(k => ({
      key: k,
      label: PD.labels[k] || PRINT_DETAIL_LABELS[k] || k,
      checked: order.length === 0 || order.includes(k),
    }));

    document.querySelectorAll('.pd-doc-item').forEach(el => el.classList.toggle('active', el.dataset.code === code));
    document.getElementById('pd-empty').style.display = 'none';
    document.getElementById('pd-body').style.display = 'grid';
    document.getElementById('pd-doc-name').textContent = `🎨 ${PD.title}`;
    document.getElementById('pd-doc-tip').innerHTML =
      `单据类型 <code>${escapeHtml(code)}</code> ｜ 模板 <b>${escapeHtml(PD.template.TemplateName || '默认模板')}</b>`
      + (PD.template.Id ? ` ｜ 已保存（Id=${PD.template.Id}）` : ' ｜ 尚未保存，当前为系统内置默认');

    renderPdForm();
    renderPdFields();
    renderPdPreview();
  } catch (err) { toast(err.message, 'error'); }
}

/* ============ 中间列：模板表单 ============ */
function renderPdForm() {
  const t = PD.template || {};
  const paperOpts = PAPER_SIZES.map(p =>
    `<option value="${p.value}" ${t.PaperSize === p.value ? 'selected' : ''}>${p.label}</option>`).join('');
  document.getElementById('pd-col-form').innerHTML = `
    <div class="card pd-card">
      <div class="card-title">📝 基本信息</div>
      <div class="pd-field">
        <label>模板名称</label>
        <input type="text" id="pd-name" value="${escapeHtml(t.TemplateName || '默认模板')}" oninput="pdOnEdit()">
        <div class="pd-hint">同一单据可保存多套模板，如「A4 正式版」「小票简易版」</div>
      </div>
      <div class="pd-field">
        <label>打印标题</label>
        <input type="text" id="pd-title" value="${escapeHtml(t.Title || PD.title)}" oninput="pdOnEdit()">
        <div class="pd-hint">打印页顶部大标题，留空则使用单据名称</div>
      </div>
      <div class="pd-field">
        <label>公司抬头</label>
        <input type="text" id="pd-company" value="${escapeHtml(t.CompanyName || '')}" oninput="pdOnEdit()">
        <div class="pd-hint">默认取「系统参数 → CompanyName」，此处可单独覆盖</div>
      </div>
      <div class="pd-field">
        <label>公司地址</label>
        <input type="text" id="pd-address" value="${escapeHtml(t.CompanyAddress || '')}" oninput="pdOnEdit()">
      </div>
      <div class="pd-field">
        <label>联系电话</label>
        <input type="text" id="pd-phone" value="${escapeHtml(t.CompanyPhone || '')}" oninput="pdOnEdit()">
      </div>
    </div>

    <div class="card pd-card">
      <div class="card-title">🖨 版式与输出</div>
      <div class="pd-grid2">
        <div class="pd-field">
          <label>纸张规格</label>
          <select id="pd-paper" onchange="pdOnEdit()">${paperOpts}</select>
        </div>
        <div class="pd-field">
          <label>正文字号</label>
          <select id="pd-font" onchange="pdOnEdit()">
            ${[9, 10, 11, 12, 13, 14, 16].map(n => `<option value="${n}" ${Number(t.FontSize) === n ? 'selected' : ''}>${n} px</option>`).join('')}
          </select>
        </div>
      </div>
      <div class="pd-switch">
        <label class="pd-check"><input type="checkbox" id="pd-show-company" ${t.ShowCompanyHeader !== false ? 'checked' : ''} onchange="pdOnEdit()"> 打印公司抬头</label>
        <label class="pd-check"><input type="checkbox" id="pd-show-detail" ${t.ShowDetailTable !== false ? 'checked' : ''} onchange="pdOnEdit()"> 打印明细表格</label>
        <label class="pd-check"><input type="checkbox" id="pd-show-remark" ${t.ShowRemark !== false ? 'checked' : ''} onchange="pdOnEdit()"> 打印备注</label>
      </div>
      <div class="pd-field">
        <label>页脚文本</label>
        <input type="text" id="pd-footer" value="${escapeHtml(t.FooterText || '')}" oninput="pdOnEdit()">
        <div class="pd-hint">例如开户行 / 账号 / 签字说明，打印在页脚位置</div>
      </div>
      <label class="pd-check pd-default-check">
        <input type="checkbox" id="pd-default" ${t.IsDefault ? 'checked' : ''} onchange="pdOnEdit()">
        <span>设为该单据的默认模板（打印与预览优先使用本套模板）</span>
      </label>
    </div>

    <div class="card pd-card">
      <div class="card-title">🎨 外观样式 <span class="card-title-tip">字体 · 字号 · 颜色 · 单元格</span></div>
      <div class="pd-grid2">
        <div class="pd-field">
          <label>正文字体</label>
          <select id="pd-family" onchange="pdOnEdit()">
            ${PD_FONTS.map(f => `<option value="${f.value}" ${((t.FontFamily || 'Microsoft YaHei') === f.value) ? 'selected' : ''}>${f.label}</option>`).join('')}
          </select>
        </div>
        <div class="pd-field">
          <label>标题对齐</label>
          <select id="pd-title-align" onchange="pdOnEdit()">
            ${[['center', '居中'], ['left', '居左'], ['right', '居右']].map(o => `<option value="${o[0]}" ${((t.TitleAlign || 'center') === o[0]) ? 'selected' : ''}>${o[1]}</option>`).join('')}
          </select>
        </div>
      </div>
      <div class="pd-grid2">
        <div class="pd-field"><label>标题字号 (px)</label><input type="number" id="pd-title-size" min="8" max="48" value="${Number(t.TitleFontSize) || 16}" oninput="pdOnEdit()"></div>
        <div class="pd-field"><label>公司名字号 (px)</label><input type="number" id="pd-company-size" min="8" max="48" value="${Number(t.CompanyFontSize) || 18}" oninput="pdOnEdit()"></div>
      </div>
      <div class="pd-grid2">
        <div class="pd-field"><label>标题颜色</label><input type="color" id="pd-title-color" value="${t.TitleColor || '#000000'}" oninput="pdOnEdit()"></div>
        <div class="pd-field"><label>公司名颜色</label><input type="color" id="pd-company-color" value="${t.CompanyColor || '#000000'}" oninput="pdOnEdit()"></div>
      </div>
      <div class="pd-grid2">
        <div class="pd-field"><label>正文颜色</label><input type="color" id="pd-text-color" value="${t.TextColor || '#000000'}" oninput="pdOnEdit()"></div>
        <div class="pd-field"><label>表头背景色</label><input type="color" id="pd-head-bg" value="${t.HeaderBgColor || '#f2f2f2'}" oninput="pdOnEdit()"></div>
      </div>
      <div class="pd-grid2">
        <div class="pd-field"><label>边框颜色</label><input type="color" id="pd-border-color" value="${t.BorderColor || '#999999'}" oninput="pdOnEdit()"></div>
        <div class="pd-field">
          <label>边框样式</label>
          <select id="pd-border-style" onchange="pdOnEdit()">
            ${[['solid', '实线'], ['dashed', '虚线'], ['none', '无边框']].map(o => `<option value="${o[0]}" ${((t.BorderStyle || 'solid') === o[0]) ? 'selected' : ''}>${o[1]}</option>`).join('')}
          </select>
        </div>
      </div>
      <div class="pd-field">
        <label>数据行高：<b id="pd-row-h-val">${Number(t.RowHeight) || 0}</b> px <span class="pd-hint-inline">（0 = 按内容自适应）</span></label>
        <input type="range" id="pd-row-h" min="0" max="60" step="2" value="${Number(t.RowHeight) || 0}"
               oninput="pdOnRange('pd-row-h','pd-row-h-val');pdOnEdit()">
      </div>
      <div class="pd-field">
        <label>单元格内边距：<b id="pd-cell-pad-val">${(t.CellPadding === undefined || t.CellPadding === null) ? 6 : Number(t.CellPadding)}</b> px</label>
        <input type="range" id="pd-cell-pad" min="0" max="20" step="1" value="${(t.CellPadding === undefined || t.CellPadding === null) ? 6 : Number(t.CellPadding)}"
               oninput="pdOnRange('pd-cell-pad','pd-cell-pad-val');pdOnEdit()">
      </div>
      <div class="pd-hint">以上样式同时作用于「实时预览」与「实际打印」输出，改完记得点「保存模板」。</div>
    </div>`;
}

/* 可选字体（打印端系统字体，避免使用网页字体导致打印失真） */
const PD_FONTS = [
  { value: 'Microsoft YaHei', label: '微软雅黑（推荐）' },
  { value: 'SimSun', label: '宋体' },
  { value: 'SimHei', label: '黑体' },
  { value: 'KaiTi', label: '楷体' },
  { value: 'DengXian', label: '等线' },
  { value: 'Arial', label: 'Arial' },
  { value: 'Times New Roman', label: 'Times New Roman' },
];

/* 滑块联动显示数值 */
function pdOnRange(inputId, labelId) {
  const el = document.getElementById(inputId);
  const lb = document.getElementById(labelId);
  if (el && lb) lb.textContent = el.value;
}

/* ============ 右列：字段勾选与排序 ============ */
function renderPdFields() {
  const checked = PD.items.filter(i => i.checked).length;
  const rows = PD.items.map((it, i) => `
    <div class="pd-field-item${it.checked ? ' checked' : ''}" draggable="true"
         ondragstart="pdDragStart(event, ${i})" ondragover="pdDragOver(event)" ondrop="pdDrop(event, ${i})">
      <label class="pd-check">
        <input type="checkbox" ${it.checked ? 'checked' : ''} onchange="pdToggleField(${i}, this.checked)">
        <span class="pd-field-label">${escapeHtml(it.label)}</span>
        <code class="pd-field-key">${escapeHtml(it.key)}</code>
      </label>
      <span class="pd-move">
        <button class="btn btn-neutral btn-sm" ${i === 0 ? 'disabled' : ''} onclick="pdMoveField(${i}, -1)" title="上移">↑</button>
        <button class="btn btn-neutral btn-sm" ${i === PD.items.length - 1 ? 'disabled' : ''} onclick="pdMoveField(${i}, 1)" title="下移">↓</button>
      </span>
    </div>`).join('');

  document.getElementById('pd-col-fields').innerHTML = `
    <div class="card pd-card pd-fields-card">
      <div class="card-title">✅ 打印字段 <span class="card-title-tip">已选 ${checked} / ${PD.items.length}</span></div>
      <div class="pd-fields-toolbar">
        <button class="btn btn-neutral btn-sm" onclick="pdCheckAll(true)">全选</button>
        <button class="btn btn-neutral btn-sm" onclick="pdCheckAll(false)">全不选</button>
        <button class="btn btn-neutral btn-sm" onclick="pdSmartFill()">⚡ 智能推荐</button>
      </div>
      <div class="pd-field-list" id="pd-field-list">
        ${rows || '<div class="pd-side-empty">该单据没有可配置字段</div>'}
      </div>
      <div class="pd-hint">提示：可直接拖拽条目调整顺序，或使用 ↑ ↓ 按钮</div>
    </div>`;
}

function pdToggleField(index, checked) {
  PD.items[index].checked = checked;
  const el = document.querySelectorAll('.pd-field-item')[index];
  if (el) el.classList.toggle('checked', checked);
  const tip = document.querySelector('.pd-fields-card .card-title-tip');
  if (tip) tip.textContent = `已选 ${PD.items.filter(i => i.checked).length} / ${PD.items.length}`;
  pdOnEdit();
}

function pdCheckAll(checked) {
  PD.items.forEach(i => { i.checked = checked; });
  renderPdFields();
  pdOnEdit();
}

/* 智能推荐：优先选中常用 / 已填值字段，其余按原顺序补足到 12 个 */
function pdSmartFill() {
  const preferred = PD.items.filter(i => PD_PREFERRED.includes(i.key));
  const rest = PD.items.filter(i => !PD_PREFERRED.includes(i.key));
  const picked = preferred.concat(rest).slice(0, 12).map(i => i.key);
  PD.items.forEach(i => { i.checked = picked.includes(i.key); });
  renderPdFields();
  pdOnEdit();
  toast('已自动勾选常用字段');
}

/* 上移 / 下移 */
function pdMoveField(index, delta) {
  const target = index + delta;
  if (target < 0 || target >= PD.items.length) return;
  const tmp = PD.items[index];
  PD.items[index] = PD.items[target];
  PD.items[target] = tmp;
  renderPdFields();
  pdOnEdit();
}

/* 拖拽排序 */
let PD_DRAG = -1;
function pdDragStart(e, index) {
  PD_DRAG = index;
  if (e.dataTransfer) e.dataTransfer.effectAllowed = 'move';
}
function pdDragOver(e) { e.preventDefault(); }
function pdDrop(e, index) {
  e.preventDefault();
  if (PD_DRAG < 0 || PD_DRAG === index) return;
  const moved = PD.items.splice(PD_DRAG, 1)[0];
  PD.items.splice(index, 0, moved);
  PD_DRAG = -1;
  renderPdFields();
  pdOnEdit();
}

/* ============ 右列：实时预览 ============ */
function renderPdPreview() {
  const host = document.getElementById('pd-col-preview');
  if (!host) return;
  const payload = pdCollect();
  const paper = payload.PaperSize || 'A4';
  const pxWidth = { 'A4': 794, 'A5': 559, 'A4-L': 1123, '80mm': 302 }[paper] || 794;
  const paperLabel = (PAPER_SIZES.find(p => p.value === paper) || {}).label || paper;
  let html = '';
  try { html = buildPrintHtml(pdSampleCtx(payload)); } catch (e) { html = '<div style="padding:20px;color:#c00">预览生成失败：' + escapeHtml(e.message) + '</div>'; }
  host.innerHTML = `
    <div class="card pd-card pd-preview-card">
      <div class="card-title">👁 实时预览 <span class="card-title-tip">${escapeHtml(paperLabel)} · ${pxWidth}px</span></div>
      <div class="pd-preview-viewport" id="pd-preview-viewport">
        <div class="pd-paper" id="pd-preview-paper" style="width:${pxWidth}px">${html}</div>
      </div>
      <div class="pd-hint">按真实纸张宽度等比缩放显示；点击「效果预览」可 100% 查看并直接打印</div>
    </div>`;
  pdFitPreview();
}

/* 预览自适应缩放 */
function pdFitPreview() {
  const vp = document.getElementById('pd-preview-viewport');
  const paper = document.getElementById('pd-preview-paper');   // 注意：不要与纸张下拉框 id="pd-paper" 重名
  if (!vp || !paper) return;
  const raw = paper.offsetWidth || 794;
  const scale = Math.min(1, (vp.clientWidth - 12) / raw);
  paper.style.transform = `scale(${scale})`;
  paper.style.transformOrigin = 'top left';
  vp.style.height = Math.ceil(paper.offsetHeight * scale) + 'px';
}
window.addEventListener('resize', () => { if (typeof pdFitPreview === 'function' && PD.code) pdFitPreview(); });

/* ============ 改动 → 刷新预览（防抖） ============ */
function pdOnEdit() {
  clearTimeout(PD.timer);
  PD.timer = setTimeout(() => { if (PD.code) renderPdPreview(); }, 260);
}

/* ============ 表单 → 模板对象 ============ */
function pdCollect() {
  const t = PD.template || {};
  const val = id => { const el = document.getElementById(id); return el ? el.value : ''; };
  const chk = id => { const el = document.getElementById(id); return el ? el.checked : false; };
  return {
    Id: t.Id || 0,
    BillType: PD.code,
    TemplateName: (val('pd-name') || '').trim() || '默认模板',
    Title: (val('pd-title') || '').trim(),
    CompanyName: (val('pd-company') || '').trim(),
    CompanyAddress: (val('pd-address') || '').trim(),
    CompanyPhone: (val('pd-phone') || '').trim(),
    PaperSize: val('pd-paper') || 'A4',
    FontSize: Number(val('pd-font')) || 12,
    FooterText: (val('pd-footer') || '').trim(),
    ShowCompanyHeader: chk('pd-show-company'),
    ShowDetailTable: chk('pd-show-detail'),
    ShowRemark: chk('pd-show-remark'),
    IsDefault: chk('pd-default'),
    FieldKeys: JSON.stringify(PD.items.filter(i => i.checked).map(i => i.key)),
    // 外观样式（字体 / 字号 / 颜色 / 单元格尺寸）
    FontFamily: val('pd-family') || 'Microsoft YaHei',
    TitleFontSize: Number(val('pd-title-size')) || 16,
    TitleColor: val('pd-title-color') || '#000000',
    TitleAlign: val('pd-title-align') || 'center',
    CompanyFontSize: Number(val('pd-company-size')) || 18,
    CompanyColor: val('pd-company-color') || '#000000',
    TextColor: val('pd-text-color') || '#000000',
    HeaderBgColor: val('pd-head-bg') || '#f2f2f2',
    BorderColor: val('pd-border-color') || '#999999',
    BorderStyle: val('pd-border-style') || 'solid',
    RowHeight: Number(val('pd-row-h')) || 0,
    CellPadding: val('pd-cell-pad') === '' ? 6 : Number(val('pd-cell-pad')),
  };
}

/* ============ 示例数据（预览无需真实单据） ============ */
function pdSampleCtx(template) {
  return {
    code: PD.code,
    billTitle: PD.title,
    labels: PD.labels,
    template,
    main: pdSampleMain(),
    details: pdSampleDetails(),
  };
}

function pdSampleMain() {
  const main = { BillNo: 'DEMO-20260918-001', Status: 1 };
  Object.keys(PD.labels).forEach(k => {
    if (main[k] !== undefined) return;
    main[k] = pdSampleValue(k, PD.labels[k]);
  });
  return main;
}

function pdSampleValue(key, label) {
  const k = String(key).toLowerCase();
  if (k.includes('date') || k.includes('time')) return '2026-09-18';
  if (k.includes('amount') || k.includes('price') || k.includes('creditlimit') || k.includes('ratio')) return '12,345.67';
  if (k.includes('qty') || k.includes('quantity') || k.includes('count') || k.includes('cartons')) return '100';
  if (k.includes('phone') || k.includes('tel') || k.includes('mobile')) return '0579-88888888';
  if (k.includes('email')) return 'sales@example.com';
  if (k.includes('country')) return '美国 USA';
  if (k.includes('address')) return '浙江省义乌市国际商贸城一区 1F-001';
  if (k.includes('currency')) return 'USD 美元';
  if (k.includes('port')) return 'NINGBO 宁波';
  if (k.includes('remark') || k.includes('description')) return '按客户确认样生产，出货前 3 天通知验货。';
  return (label || key) + '（示例）';
}

function pdSampleDetails() {
  const keys = ['ProductName', 'Spec', 'Quantity', 'Unit', 'UnitPrice', 'Amount'];
  const nameOf = k => PD.labels[k] || PRINT_DETAIL_LABELS[k] || k;
  return [1, 2, 3].map((n, i) => {
    const row = {};
    keys.forEach(k => {
      if (k === 'ProductName') row[k] = '示例商品 ' + n + '（' + escapeHtml(PD.title) + '）';
      else if (k === 'Spec') row[k] = '标准规格 ' + n;
      else if (k === 'Quantity') row[k] = String(n * 10);
      else if (k === 'Unit') row[k] = 'PCS';
      else if (k === 'UnitPrice') row[k] = '12.50';
      else row[k] = (n * 125).toFixed(2);
    });
    // 保证明细列至少包含该单据配置中的明细字段
    Object.keys(PRINT_DETAIL_LABELS).forEach(k => { if (row[k] === undefined) row[k] = ''; });
    row._idx = i;
    return row;
  });
}

/* ============ 保存 / 重置 / 全屏预览 ============ */
async function pdSave() {
  if (!PD.code) { toast('请先在左侧选择单据', 'error'); return; }
  const payload = pdCollect();
  if (!payload.TemplateName) { toast('模板名称不能为空', 'error'); return; }
  const btn = document.getElementById('pd-save-btn');
  try {
    if (btn) { btn.disabled = true; btn.textContent = '保存中…'; }
    const saved = await api('/api/sys/print-templates', 'POST', payload);
    if (saved) PD.template = normalizeTemplate(saved);
    toast('模板已保存');
    document.getElementById('pd-doc-tip').innerHTML =
      `单据类型 <code>${escapeHtml(PD.code)}</code> ｜ 模板 <b>${escapeHtml(PD.template.TemplateName || payload.TemplateName)}</b>`
      + (PD.template.Id ? ` ｜ 已保存（Id=${PD.template.Id}）` : '');
  } catch (err) { toast(err.message, 'error'); }
  finally { if (btn) { btn.disabled = false; btn.textContent = '💾 保存模板'; } }
}

/* 重置表单：放弃未保存的修改，重新从服务器读取该单据模板 */
async function pdLoadDefault() {
  if (!PD.code) { toast('请先在左侧选择单据', 'error'); return; }
  if (!confirm('将放弃当前未保存的修改，重新读取服务器上的模板，确定继续？')) return;
  await pdSelectDoc(PD.code);
  toast('已重新载入模板');
}

/* 全屏预览（100% 纸张宽度 + 可直接打印测试） */
function pdOpenPreview() {
  if (!PD.code) { toast('请先在左侧选择单据', 'error'); return; }
  try {
    const html = buildPrintHtml(pdSampleCtx(pdCollect()));
    const box = document.createElement('div');
    box.id = 'print-design-preview';
    box.className = 'pd-modal';
    box.innerHTML = `<div class="pd-modal-box">
        <div class="pd-modal-head">
          <b>👁 打印效果预览 · ${escapeHtml(PD.title)}（示例数据）</b>
          <span class="pd-modal-actions">
            <button class="btn btn-neutral btn-sm" onclick="pdPrintTest()">🖨 打印测试</button>
            <button class="btn btn-neutral btn-sm" onclick="closePrintDesignPreview()">关闭</button>
          </span>
        </div>
        <div class="pd-modal-body"><div class="pd-paper" id="pd-modal-paper">${html}</div></div>
      </div>`;
    document.body.appendChild(box);
  } catch (err) { toast(err.message, 'error'); }
}

function pdPrintTest() {
  const paper = document.getElementById('pd-modal-paper');
  if (!paper) return;
  const win = window.open('', '_blank', 'width=920,height=720');
  win.document.write(`<!DOCTYPE html><html lang="zh-CN"><head><meta charset="utf-8">
    <title>${escapeHtml(PD.title)} - 打印测试</title><style>${PRINT_STYLE}
    body { margin: 0; } @page { margin: 8mm; }</style></head>
    <body>${paper.innerHTML}</body></html>`);
  win.document.close();
  setTimeout(() => { try { win.focus(); win.print(); } catch (e) { /* 忽略 */ } }, 400);
}

/* ============ 列表页 → 样式设计（带单据预选） ============ */
function gotoPrintDesign(code) {
  window.__pdPreSelect = code || '';
  navigate('print-design', '样式设计');
}



