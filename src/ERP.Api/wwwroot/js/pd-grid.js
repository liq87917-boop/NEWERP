/* ============================================================
   ============ 单据模板中心（Excel 式打印模板设计器） ========
   ============================================================
   · 左侧：单据类型清单（业务单据 / 基础资料）
   · 主区：模板列表（模板名称 / 单据类型 / 类型 / 最后更新 / 打开编辑器）
   · 编辑器：类 Excel 单元格网格（可输入文本或 {字段}），右侧「系统字段」点击绑定到当前单元格
   · 支持明细表区域（勾选明细列，打印时自动按明细行展开）
   存储：SysPrintTemplates.LayoutJson（网格 JSON），与「快速样式配置」共用同一模板记录
   下一阶段：合并单元格 / 图片 / Excel 导入 / PDF 导出（界面已标注）
   ============================================================ */

let PC = {
  code: '',            // 当前单据类型
  title: '',
  template: {},        // 模板记录（字段名已做 camelCase/PascalCase 兼容）
  grid: null,          // { cols:[], rows:[], cells:{}, detail:{} }
  sel: null,           // 当前选中单元格 { r, c }
  selType: 'cell',     // 选区类型：cell / row / col
  history: [],         // 撤销栈
  future: [],          // 重做栈
};
window.PC = PC;        // 暴露到 window，便于调试与自动化验证

/* 单元格默认属性 */
function pcNewCell() {
  return { v: '', bold: false, italic: false, align: 'left', fs: 12, color: '#000000', bg: '', bd: 1 };
}

/* 公司信息字段（打印页眉常用） */
const PC_COMPANY_FIELDS = [
  ['CompanyName', '公司中文名称'], ['CompanyNameEn', '公司英文名称'],
  ['CompanyAddress', '公司中文地址'], ['CompanyAddressEn', '公司英文地址'],
  ['CompanyPhone', '公司电话'], ['CompanyEmail', '公司邮箱'],
  ['CompanyWebsite', '公司网站'], ['CompanyTaxNo', '税号/统一社会信用代码'],
  ['CompanyBank', '开户银行'], ['CompanyWhatsApp', 'WhatsApp'],
];

/* ============ 模板中心（入口） ============ */
async function renderTplCenterModule(preCode) {
  CURRENT_LOADER = null;
  ensurePrintStyle();
  document.getElementById('content').innerHTML = `
    <div class="pc-center">
      <aside class="pc-side card">
        <div class="pd-side-title">📄 单据类型</div>
        <input type="text" id="pc-search" placeholder="搜索单据..." oninput="pcFilterDocs(this.value)">
        <div class="pc-doc-list" id="pc-doc-list"></div>
        <div class="pc-side-foot">
          <button class="btn btn-neutral btn-sm" onclick="renderPrintDesignModule()">⚡ 快速样式配置</button>
        </div>
      </aside>
      <section class="pc-main">
        <div class="card pc-head">
          <div>
            <div class="pd-doc-name" id="pc-doc-name">请选择左侧单据类型</div>
            <div class="pd-doc-tip" id="pc-doc-tip">设计可打印的 Excel 式模板：自由排版版面 + 绑定 ERP 字段与单据业务数据</div>
          </div>
          <div class="pd-head-actions">
            <button class="btn btn-primary" onclick="pcNewTemplate()">＋ 新建模板</button>
            <button class="btn btn-neutral" onclick="pcImportExcel()" title="下一阶段开放">📥 由 Excel 导入模板</button>
          </div>
        </div>
        <div class="card" id="pc-list-host">
          <div class="empty">请先在左侧选择单据类型</div>
        </div>
      </section>
    </div>`;
  pcRenderDocList();
  if (preCode) pcSelectDoc(preCode);
  window.__pdPreSelect = '';
}

/* 左侧单据清单（ERP-018 去重：EF 主子表单据已登记在 BILL_CONFIG，不再重复出现在基础资料分组） */
function pcRenderDocList(keyword) {
  const kw = (keyword || '').trim().toLowerCase();
  const docs = Object.keys(BILL_CONFIG || {}).map(k => ({ code: k, name: (BILL_CONFIG[k] || {}).title || k, group: '业务单据', icon: '🧾' }))
    .concat(Object.keys(MODULES || {}).filter(k => !(BILL_CONFIG || {})[k]).map(k => ({ code: k, name: (MODULES[k] || {}).title || k, group: '基础资料', icon: '🗂' })))
    .filter(d => !kw || d.name.toLowerCase().includes(kw) || d.code.toLowerCase().includes(kw));
  const host = document.getElementById('pc-doc-list');
  if (!host) return;
  host.innerHTML = ['业务单据', '基础资料'].map(g => {
    const items = docs.filter(d => d.group === g);
    if (!items.length) return '';
    return `<div class="pd-group"><div class="pd-group-title">${g}<span>${items.length}</span></div>
      ${items.map(d => `<div class="pd-doc-item${PC.code === d.code ? ' active' : ''}" data-code="${d.code}" onclick="pcSelectDoc('${d.code}')">
        <span class="pd-doc-ico">${d.icon}</span><span class="pd-doc-label">${escapeHtml(d.name)}</span></div>`).join('')}
    </div>`;
  }).join('');
}

function pcFilterDocs(v) { pcRenderDocList(v); }

/* 选择单据 → 载入模板列表 */
async function pcSelectDoc(code) {
  PC.code = code;
  PC.title = ((BILL_CONFIG[code] || MODULES[code] || {}).title) || code;
  document.querySelectorAll('.pc-doc-item').forEach(el => el.classList.toggle('active', el.dataset.code === code));
  document.getElementById('pc-doc-name').textContent = '🧾 ' + PC.title;
  const host = document.getElementById('pc-list-host');
  host.innerHTML = '<div class="skeleton-line w-30"></div><div class="skeleton-line w-90"></div><div class="skeleton-line w-70"></div>';
  try {
    const list = await api(`/api/sys/print-templates?billType=${encodeURIComponent(code)}`);
    const items = Array.isArray(list) ? list : (list.items || []);
    const rows = items.map(t => `<tr>
        <td class="text-center"><input type="checkbox" title="勾选后可与另一个模板对比" ${PC_CMP.indexOf(t.id) >= 0 ? 'checked' : ''} onclick="pcToggleCompare(${t.id})"></td>
        <td><b>${escapeHtml(t.templateName || '')}</b>${t.isDefault ? ' <span class="status status-success">默认</span>' : ''}</td>
        <td>${escapeHtml(PC.title)}<div class="dt-code">${escapeHtml(code)}</div></td>
        <td>${t.layoutJson ? '🧮 网格模板' : '📝 表单模板'}</td>
        <td>${escapeHtml(String(t.updatedAt || t.createdAt || '').replace('T', ' ').slice(0, 19))}</td>
        <td>
          <div class="row-actions">
            <button class="btn btn-neutral btn-sm" onclick="pcOpenEditor(${t.id})">打开编辑器</button>
            <button class="btn btn-neutral btn-sm row-more" onclick="toggleRowMenu(event, this)" title="更多操作">更多</button>
          </div>
          <div class="row-menu" hidden>
            <button class="row-menu-item" onclick="pcOpenEditor(${t.id})"><span class="rmi-ico">✏️</span><span class="rmi-txt">编辑布局</span></button>
            <button class="row-menu-item" onclick="pcCopyTemplate(${t.id})"><span class="rmi-ico">📋</span><span class="rmi-txt">复制模板</span></button>
            <button class="row-menu-item" onclick="pcPreviewTemplate(${t.id})"><span class="rmi-ico">👁</span><span class="rmi-txt">打印预览</span></button>
            <div class="row-menu-sep"></div>
            <button class="row-menu-item danger" onclick="pcDeleteTemplate(${t.id})"><span class="rmi-ico">🗑</span><span class="rmi-txt">删除模板</span></button>
          </div>
        </td>
      </tr>`).join('');
    host.innerHTML = `
      <div class="card-title">📋 模板列表 <span class="card-title-tip">共 ${items.length} 套</span>
        <button class="btn btn-neutral btn-sm" style="margin-left:auto" onclick="pcCompareTemplates()" title="勾选两个模板后对比布局差异">🔍 对比所选（${PC_CMP.length}/2）</button>
      </div>
      ${items.length ? `<div class="table-wrap"><table><thead><tr>
          <th style="width:44px" class="text-center">对比</th>
          <th>模板名称</th><th style="width:150px">单据类型</th><th style="width:120px">类型</th>
          <th style="width:170px">最后更新</th><th style="width:160px">操作</th>
        </tr></thead><tbody>${rows}</tbody></table></div>`
      : '<div class="empty">该单据还没有模板，点右上角「＋ 新建模板」开始设计</div>'}
      <div class="pd-hint" style="margin-top:10px">
        网格模板 = Excel 式版面（自由排版 + 字段绑定）；「⚡ 快速样式配置」= 统一调整字体 / 字号 / 颜色 / 行高 / 边框。
      </div>`;
  } catch (e) {
    host.innerHTML = '<div class="empty">读取模板失败：' + escapeHtml(e.message) + '</div>';
  }
}

/* Excel 导入（下一阶段） */
function pcImportExcel() {
  toast('Excel 导入模板功能开发中（下一阶段开放：文字/合并/边框/底色/对齐/列宽的自动解析）', 'info');
}

/* 被合并区覆盖（非左上角）的单元格坐标集合 */
function pcMergedCoveredSet() {
  const set = {};
  const merges = (PC.grid && PC.grid.merges) || [];
  merges.forEach(m => {
    for (let r = m.r; r < m.r + (m.rs || 1); r++) {
      for (let c = m.c; c < m.c + (m.cs || 1); c++) {
        if (r === m.r && c === m.c) continue;
        set[r + '_' + c] = true;
      }
    }
  });
  return set;
}

/* 查找坐标所属的合并区（含左上角） */
function pcMergeAt(r, c) {
  const merges = (PC.grid && PC.grid.merges) || [];
  return merges.find(m =>
    r >= m.r && r < m.r + (m.rs || 1) && c >= m.c && c < m.c + (m.cs || 1)) || null;
}

/* ============ 空白网格与解析 ============ */
function pcBlankGrid() {
  return {
    font: 'Microsoft YaHei',
    cols: [110, 110, 110, 110, 110, 110, 110, 110],
    rows: [34, 34, 34, 34, 34, 34, 34, 34, 34, 34, 34, 34],
    cells: {
      '0_0': Object.assign(pcNewCell(), { v: '公司名称：{CompanyName}', bold: true, fs: 14 }),
      '1_0': Object.assign(pcNewCell(), { v: '单据号：{BillNo}' }),
      '1_4': Object.assign(pcNewCell(), { v: '日期：{OrderDate}' }),
      '2_0': Object.assign(pcNewCell(), { v: '客户：{CustomerName}' }),
    },
    detail: { enabled: true, fields: ['ProductName', 'Spec', 'Quantity', 'Unit', 'UnitPrice', 'Amount'], fs: 12, headBg: '#f2f2f2', bd: 1 },
    merges: [],                                 // 合并区：[{r,c,rs,cs}]
    detailRow: { enabled: false, rows: [5] },   // 明细模板行（支持多行成组循环展开）
    watermark: {                                // 水印（文本 / 图片，平铺 / 居中单排）
      enabled: false, mode: 'tile', text: '{CompanyName}', image: '', imageWidth: 200,
      color: '#93c5fd', opacity: 0.18, size: 20, rotate: -30, repeat: true,
    },
    footer: {                                   // 页脚
      enabled: true, text: '地址：{CompanyAddress}　电话：{CompanyPhone}',
      align: 'center', fontSize: 10, borderTop: true,
      showTime: true, showPage: true, showUser: false,
    },
  };
}

function pcParseLayout(json) {
  if (!json) return pcBlankGrid();
  try {
    const g = JSON.parse(json) || {};
    g.font = g.font || 'Microsoft YaHei';
    g.cols = Array.isArray(g.cols) && g.cols.length ? g.cols : [110, 110, 110, 110, 110, 110, 110, 110];
    g.rows = Array.isArray(g.rows) && g.rows.length ? g.rows : [34, 34, 34, 34, 34, 34, 34, 34];
    g.cells = g.cells || {};
    g.detail = Object.assign({ enabled: false, fields: ['ProductName', 'Quantity', 'UnitPrice', 'Amount'], fs: 12, headBg: '#f2f2f2', bd: 1 }, g.detail || {});
    g.merges = Array.isArray(g.merges) ? g.merges : [];
    g.detailRow = Object.assign({ enabled: false, rows: [5] }, g.detailRow || {});
    if (!Array.isArray(g.detailRow.rows)) {
      g.detailRow.rows = (g.detailRow.row !== undefined && g.detailRow.row !== null)
        ? [Number(g.detailRow.row)] : [5];                       // 兼容旧数据（单行 row）
    }
    g.watermark = Object.assign({
      enabled: false, mode: 'tile', text: '{CompanyName}', image: '', imageWidth: 200,
      color: '#93c5fd', opacity: 0.18, size: 20, rotate: -30, repeat: true,
    }, g.watermark || {});
    g.footer = Object.assign({
      enabled: true, text: '地址：{CompanyAddress}　电话：{CompanyPhone}',
      align: 'center', fontSize: 10, borderTop: true,
      showTime: true, showPage: true, showUser: false,
    }, g.footer || {});
    return g;
  } catch (e) {
    return pcBlankGrid();
  }
}

/* ============ 新建模板 ============ */
async function pcNewTemplate() {
  if (!PC.code) { toast('请先在左侧选择单据类型', 'error'); return; }
  const input = prompt('请输入模板名称', PC.title + ' 打印模板');
  if (input === null) return;
  const name = (input || '').trim() || (PC.title + ' 打印模板');
  try {
    const saved = await api('/api/sys/print-templates', 'POST', {
      Id: 0, BillType: PC.code, TemplateName: name, Title: PC.title,
      PaperSize: 'A4', FontSize: 12, FieldKeys: '', FooterText: '',
      ShowCompanyHeader: true, ShowDetailTable: true, ShowRemark: true, IsDefault: false,
      LayoutJson: JSON.stringify(pcBlankGrid()),
    });
    toast('模板已创建，开始设计');
    if (saved && saved.id) pcOpenEditor(saved.id);
    else pcSelectDoc(PC.code);
  } catch (e) { toast(e.message, 'error'); }
}

/* ============ 打开编辑器 ============ */
async function pcOpenEditor(id) {
  try {
    const list = await api(`/api/sys/print-templates?billType=${encodeURIComponent(PC.code)}`);
    const items = Array.isArray(list) ? list : (list.items || []);
    const raw = items.find(t => t.id === id) || items[0];
    if (!raw) { toast('模板不存在', 'error'); return; }
    PC.template = normalizeTemplate(raw);
    PC.grid = pcParseLayout(PC.template.LayoutJson);
    PC.sel = null;
    pcRenderEditor();
    toast('已进入编辑器：点选单元格后可输入内容或插入字段');
  } catch (e) { toast('打开编辑器失败：' + e.message, 'error'); }
}

/* ============ 编辑器界面 ============ */
function pcRenderEditor() {
  const t = PC.template || {};
  document.getElementById('content').innerHTML = `
    <div class="card pc-editor-head">
      <div class="pc-editor-title">
        <button class="btn btn-neutral btn-sm" onclick="renderTplCenterModule('${PC.code}')">← 返回模板中心</button>
        <b>${escapeHtml(t.TemplateName || '未命名模板')}</b>
        <span class="dt-code">${escapeHtml(PC.code)}</span>
        <span class="status status-info">网格模板</span>
      </div>
      <div class="pd-head-actions">
        <button class="btn btn-neutral btn-sm" onclick="pcPreview()">👁 效果预览</button>
        <button class="btn btn-neutral btn-sm" onclick="pcExportPdf()">📄 导出 PDF</button>
        <button class="btn btn-neutral btn-sm" onclick="pcExportExcel()">📊 导出 Excel</button>
        <button class="btn btn-neutral btn-sm" onclick="pcPrintTest()">🖨 打印测试</button>
        <button class="btn btn-neutral btn-sm" onclick="pcSaveAs()" title="把当前布局另存为一个新模板">📄 另存为</button>
        <button class="btn btn-primary btn-sm" onclick="pcSaveLayout()">💾 保存模板</button>
      </div>
    </div>
    <div class="pc-editor">
      <div class="pc-canvas card">
        ${pcToolbarHtml()}
        <div class="pc-grid-scroll"><div id="pc-grid-host"></div></div>
        <div class="pc-detail-cfg" id="pc-detail-cfg"></div>
        <div class="pc-status" id="pc-status"></div>
      </div>
      <aside class="pc-fields card">
        <div class="card-title">🔗 系统字段 <span class="card-title-tip">点击插入当前单元格</span></div>
        <input type="text" id="pc-field-search" placeholder="搜索字段..." oninput="pcFilterFields(this.value)">
        <div class="pc-field-list" id="pc-field-list"></div>
        <div class="pc-fields-foot">
          <button class="btn btn-neutral btn-sm" onclick="pcClearCell()">🧹 清除当前单元格绑定</button>
        </div>
      </aside>
    </div>`;
  pcRenderGrid();
  pcRenderDetailCfg();
  pcRenderFields();
}

/* 工具栏 */
function pcToolbarHtml() {
  const fonts = ['Microsoft YaHei', 'SimSun', 'SimHei', 'KaiTi', 'DengXian', 'Arial', 'Times New Roman'];
  const fontLabel = { 'Microsoft YaHei': '微软雅黑', 'SimSun': '宋体', 'SimHei': '黑体', 'KaiTi': '楷体', 'DengXian': '等线' };
  return `<div class="pc-toolbar">
    <input type="text" id="pc-cell-content" class="pc-cell-input" placeholder="选中单元格后输入文字，或点右侧字段插入"
           oninput="pcSetCellValue(this.value)">
    <span class="pc-tb-sep"></span>
    <select id="pc-font" onchange="pcApplyFont(this.value)" title="正文字体">
      ${fonts.map(f => `<option value="${f}" ${(PC.grid.font || 'Microsoft YaHei') === f ? 'selected' : ''}>${fontLabel[f] || f}</option>`).join('')}
    </select>
    <select id="pc-fs" onchange="pcApplyStyle('fs', Number(this.value))" title="字号">
      ${[9, 10, 11, 12, 13, 14, 16, 18, 20, 24].map(n => `<option value="${n}">${n}</option>`).join('')}
    </select>
    <button class="pc-tb-btn" id="pc-b-bold" onclick="pcToggleStyle('bold')" title="加粗"><b>B</b></button>
    <button class="pc-tb-btn" id="pc-b-italic" onclick="pcToggleStyle('italic')" title="斜体"><i>I</i></button>
    <label class="pc-tb-color" title="文字颜色">A<input type="color" id="pc-color" oninput="pcApplyStyle('color', this.value)"></label>
    <label class="pc-tb-color" title="背景色">▨<input type="color" id="pc-bg" oninput="pcApplyStyle('bg', this.value)"></label>
    <select id="pc-align" onchange="pcApplyStyle('align', this.value)" title="对齐">
      <option value="left">居左</option><option value="center">居中</option><option value="right">居右</option>
    </select>
    <select id="pc-bd" onchange="pcApplyStyle('bd', Number(this.value))" title="边框">
      <option value="0">无边框</option><option value="1">细边框</option><option value="2">粗边框</option>
    </select>
    <span class="pc-tb-sep"></span>
    <button class="pc-tb-btn" onclick="pcUndo()" title="撤销（Ctrl+Z）">↶ 撤销</button>
    <button class="pc-tb-btn" onclick="pcRedo()" title="重做（Ctrl+Y）">↷ 重做</button>
    <button class="pc-tb-btn" onclick="pcCopyCell()" title="复制单元格含样式（Ctrl+C）">⧉ 复制</button>
    <button class="pc-tb-btn" onclick="pcPasteCell()" title="粘贴到选区（Ctrl+V）">📋 粘贴</button>
    <button class="pc-tb-btn" onclick="pcApplyToRow()" title="把当前单元格样式套用到整行">→ 套整行</button>
    <button class="pc-tb-btn" onclick="pcApplyToCol()" title="把当前单元格样式套用到整列">↓ 套整列</button>
    <span class="pc-tb-sep"></span>
    <button class="pc-tb-btn" onclick="pcMergeCells()" title="先按住鼠标拖动选中区域，再点此合并">⛶ 合并</button>
    <button class="pc-tb-btn" onclick="pcUnmergeCells()" title="取消当前单元格所在合并区">⇱ 取消合并</button>
    <button class="pc-tb-btn" onclick="pcToggleDetailRow()" title="把当前单元格所在行设为明细行模板（打印时按明细条数循环展开）">🧾 明细行</button>
    <button class="pc-tb-btn" id="pc-wm-btn" onclick="pcToggleWatermark(!(PC.grid.watermark && PC.grid.watermark.enabled))" title="显示 / 隐藏水印">💧 水印</button>
    <span class="pc-tb-sep"></span>
    <button class="pc-tb-btn" onclick="pcResize('rows', 4)" title="行高 +">行高＋</button>
    <button class="pc-tb-btn" onclick="pcResize('rows', -4)" title="行高 −">行高－</button>
    <button class="pc-tb-btn" onclick="pcResize('cols', 10)" title="列宽 +">列宽＋</button>
    <button class="pc-tb-btn" onclick="pcResize('cols', -10)" title="列宽 −">列宽－</button>
    <span class="pc-tb-sep"></span>
    <button class="pc-tb-btn" onclick="pcAddRow()">＋行</button>
    <button class="pc-tb-btn" onclick="pcAddCol()">＋列</button>
    <button class="pc-tb-btn" onclick="pcDelRow()">－行</button>
    <button class="pc-tb-btn" onclick="pcDelCol()">－列</button>
  </div>`;
}

/* ============ 网格渲染 ============ */
function pcCellStyle(cell) {
  const bd = cell.bd === undefined ? 1 : Number(cell.bd);
  const border = bd === 0 ? '1px dashed #cbd5e1' : (bd === 2 ? '2px solid #475569' : '1px solid #94a3b8');
  return [
    'text-align:' + (cell.align || 'left'),
    'font-size:' + (Number(cell.fs) || 12) + 'px',
    'font-weight:' + (cell.bold ? '700' : '400'),
    'font-style:' + (cell.italic ? 'italic' : 'normal'),
    'color:' + (cell.color || '#000000'),
    cell.bg ? ('background:' + cell.bg) : '',
    'border:' + border,
  ].filter(Boolean).join(';');
}

function pcGridColName(i) {
  let s = '';
  i = i + 1;
  while (i > 0) { const m = (i - 1) % 26; s = String.fromCharCode(65 + m) + s; i = Math.floor((i - 1) / 26); }
  return s;
}

function pcRenderGrid() {
  const host = document.getElementById('pc-grid-host');
  if (!host || !PC.grid) return;
  const g = PC.grid;
  const font = (g.font || 'Microsoft YaHei') + ', "SimSun", Arial, sans-serif';
  const covered = pcMergedCoveredSet();
  const rg = PC.range;
  const inRange = (r, c) => rg && r >= Math.min(rg.r1, rg.r2) && r <= Math.max(rg.r1, rg.r2)
    && c >= Math.min(rg.c1, rg.c2) && c <= Math.max(rg.c1, rg.c2);

  let html = '<div class="pc-canvas-wrap">';
  html += pcWatermarkLayerHtml(g.watermark, pcSampleMain(), { grid: g, details: pcSampleDetails() });
  html += `<table class="pc-grid" style="font-family:${font}">`;
  html += '<colgroup><col style="width:36px">' + g.cols.map(w => `<col style="width:${w}px">`).join('') + '</colgroup>';
  // 列标行（点击选中整列；右边缘可拖拽调列宽）
  html += '<thead><tr><th class="pc-gutter pc-corner" title="行列坐标">⋮</th>' + g.cols.map((w, c) => {
    const cls = (PC.selType === 'col' && PC.sel && PC.sel.c === c) ? ' sel-col' : '';
    return `<th class="pc-gutter pc-colhead${cls}" onclick="pcSelectCol(${c})" title="选中整列 ${pcGridColName(c)}">${pcGridColName(c)}<span class="pc-rz pc-rz-x" onmousedown="pcRzStart(event,'col',${c})" title="拖拽调整列宽"></span></th>`;
  }).join('') + '</tr></thead><tbody>';
  for (let r = 0; r < g.rows.length; r++) {
    const drRowsArr = (g.detailRow && Array.isArray(g.detailRow.rows)) ? g.detailRow.rows.map(Number) : [];
    const isDetailRow = g.detailRow && g.detailRow.enabled && drRowsArr.indexOf(r) >= 0;
    const rowCls = ((PC.selType === 'row' && PC.sel && PC.sel.r === r) ? ' sel-row' : '') + (isDetailRow ? ' detail-row' : '');
    html += `<tr style="height:${g.rows[r]}px" class="${rowCls}">`;
    html += `<th class="pc-gutter pc-rowhead" onclick="pcSelectRow(${r})" title="选中整行${isDetailRow ? '（当前为明细行模板）' : ''}">${r + 1}<span class="pc-rz pc-rz-y" onmousedown="pcRzStart(event,'row',${r})" title="拖拽调整行高"></span></th>`;
    for (let c = 0; c < g.cols.length; c++) {
      const key = r + '_' + c;
      if (covered[key]) continue;                          // 被合并区覆盖 → 不渲染
      const m = pcMergeAt(r, c);
      const cell = g.cells[key] || {};
      const cls = [];
      if (PC.selType === 'cell' && PC.sel && PC.sel.r === r && PC.sel.c === c) cls.push('sel');
      if (inRange(r, c)) cls.push('in-range');
      if ((PC.selType === 'row' && PC.sel && PC.sel.r === r) || (PC.selType === 'col' && PC.sel && PC.sel.c === c)) cls.push('in-sel');
      if (m) cls.push('merged');
      const span = m ? ` rowspan="${m.rs || 1}" colspan="${m.cs || 1}"` : '';
      html += `<td class="pc-cell${cls.length ? ' ' + cls.join(' ') : ''}" data-r="${r}" data-c="${c}"${span} style="${pcCellStyle(cell)}"
        onmousedown="pcStartSelect(event,${r},${c})"
        onclick="pcSelectCell(${r},${c})"
        ondblclick="pcBeginEdit(${r},${c})"
        ondragover="pcCellDragOver(event)" ondrop="pcCellDrop(event,${r},${c})">${escapeHtml(cell.v || '')}</td>`;
    }
    html += '</tr>';
  }
  html += '</tbody></table></div>';

  // 明细表区域（示例行预览）
  const d = g.detail || {};
  if (d.enabled && (d.fields || []).length) {
    const fs = Number(d.fs) || 12;
    const headBg = d.headBg || '#f2f2f2';
    html += `<table class="pc-grid pc-detail-grid" style="font-family:${font};font-size:${fs}px;margin-top:12px">
      <thead><tr>${d.fields.map(k => `<th style="background:${headBg};border:1px solid #94a3b8;padding:4px 6px">${escapeHtml(PRINT_DETAIL_LABELS[k] || k)}</th>`).join('')}</tr></thead>
      <tbody>${[1, 2, 3].map(n => `<tr>${d.fields.map((k, i) => `<td style="border:1px solid #cbd5e1;padding:4px 6px;color:#94a3b8">示例${n}${i === 0 ? '' : ''}</td>`).join('')}</tr>`).join('')}</tbody>
    </table>`;
  }
  host.innerHTML = html;
  pcRenderStatusBar();
}

/* 选中单元格（点合并区时归一到其左上角） */
function pcSelectCell(r, c) {
  const m0 = pcMergeAt(r, c);
  if (m0) { r = m0.r; c = m0.c; }
  PC.selType = 'cell';
  PC.sel = { r: r, c: c };
  if (!PC.dragging) PC.range = null;
  const cell = PC.grid.cells[r + '_' + c] || pcNewCell();
  const input = document.getElementById('pc-cell-content');
  if (input) input.value = cell.v || '';
  document.querySelectorAll('.pc-cell').forEach(td => {
    td.classList.toggle('sel', Number(td.dataset.r) === r && Number(td.dataset.c) === c);
    td.classList.remove('in-sel');
  });
  document.querySelectorAll('.pc-colhead').forEach(th => th.classList.remove('sel-col'));
  document.querySelectorAll('tr.sel-row').forEach(tr => tr.classList.remove('sel-row'));
  pcSyncToolbar(cell);
  pcRenderStatusBar();
}

/* 选中整行（可批量套用样式 / 复制） */
function pcSelectRow(r) {
  PC.selType = 'row';
  PC.sel = { r: r, c: PC.sel ? PC.sel.c : 0 };
  pcRenderGrid();
  pcSyncToolbar(PC.grid.cells[r + '_' + (PC.sel.c || 0)] || pcNewCell());
  pcRenderStatusBar();
}

/* 选中整列 */
function pcSelectCol(c) {
  PC.selType = 'col';
  PC.sel = { r: PC.sel ? PC.sel.r : 0, c: c };
  pcRenderGrid();
  pcSyncToolbar(PC.grid.cells[(PC.sel.r || 0) + '_' + c] || pcNewCell());
  pcRenderStatusBar();
}

/* 当前选中范围内的单元格键（单元格 / 整行 / 整列） */
function pcSelectionKeys() {
  if (!PC.sel || !PC.grid) return [];
  const g = PC.grid, keys = [];
  if (PC.selType === 'row') {
    for (let c = 0; c < g.cols.length; c++) keys.push(PC.sel.r + '_' + c);
  } else if (PC.selType === 'col') {
    for (let r = 0; r < g.rows.length; r++) keys.push(r + '_' + PC.sel.c);
  } else {
    keys.push(PC.sel.r + '_' + PC.sel.c);
  }
  return keys;
}

/* 底部状态栏：当前选区 + 操作提示 */
function pcRenderStatusBar() {
  const el = document.getElementById('pc-status');
  if (!el || !PC.grid) return;
  const pos = PC.sel ? (pcGridColName(PC.sel.c) + (PC.sel.r + 1)) : '—';
  const scope = !PC.sel ? '未选中'
    : PC.selType === 'row' ? ('整行 第 ' + (PC.sel.r + 1) + ' 行（样式将套用到整行）')
      : PC.selType === 'col' ? ('整列 ' + pcGridColName(PC.sel.c) + ' 列（样式将套用到整列）')
        : ('单元格 ' + pos);
  el.innerHTML = `<b>${scope}</b>　|　表格 ${PC.grid.rows.length} 行 × ${PC.grid.cols.length} 列`
    + `　|　<span class="pc-tip">↑↓←→ 移动 · 双击/F2 编辑 · Enter 换行 · Tab 右移 · Delete 清空 · Ctrl+C/V 复制粘贴 · Ctrl+Z 撤销 · 拖拽右侧字段到单元格</span>`;
}

/* 工具栏与选中单元格状态同步 */
function pcSyncToolbar(cell) {
  const set = (id, v) => { const el = document.getElementById(id); if (el && v !== undefined && v !== null) el.value = String(v); };
  set('pc-fs', Number(cell.fs) || 12);
  set('pc-color', cell.color || '#000000');
  set('pc-bg', cell.bg || '#ffffff');
  set('pc-align', cell.align || 'left');
  set('pc-bd', cell.bd === undefined ? 1 : cell.bd);
  const b = document.getElementById('pc-b-bold');
  const i = document.getElementById('pc-b-italic');
  if (b) b.classList.toggle('on', !!cell.bold);
  if (i) i.classList.toggle('on', !!cell.italic);
}

/* 当前单元格对象（不存在则创建） */
function pcCurrentCell(create = true) {
  if (!PC.sel) return null;
  const key = PC.sel.r + '_' + PC.sel.c;
  if (!PC.grid.cells[key] && create) PC.grid.cells[key] = pcNewCell();
  return PC.grid.cells[key];
}

/* 更新单元格内容 */
function pcSetCellValue(v) {
  const cell = pcCurrentCell();
  if (!cell) { toast('请先点选一个单元格', 'error'); return; }
  cell.v = v;
  const td = document.querySelector(`.pc-cell[data-r="${PC.sel.r}"][data-c="${PC.sel.c}"]`);
  if (td) td.textContent = v;
}

/* 修改样式（作用于当前选区：单元格 / 整行 / 整列） */
function pcApplyStyle(prop, value) {
  if (!PC.sel) { toast('请先点选单元格 / 行 / 列', 'error'); return; }
  pcPushHistory();
  const keys = pcSelectionKeys();
  keys.forEach(key => {
    if (!PC.grid.cells[key]) PC.grid.cells[key] = pcNewCell();
    PC.grid.cells[key][prop] = value;
  });
  pcRenderGrid();
  const cur = PC.grid.cells[PC.sel.r + '_' + PC.sel.c];
  if (cur) pcSyncToolbar(cur);
  pcRenderStatusBar();
}

function pcToggleStyle(prop) {
  const cell = pcCurrentCell();
  if (!cell) { toast('请先点选一个单元格', 'error'); return; }
  pcApplyStyle(prop, !cell[prop]);
}

/* 整表字体 */
function pcApplyFont(font) {
  PC.grid.font = font;
  pcRenderGrid();
}

/* ============ 行列操作 ============ */
function pcAddRow() {
  const g = PC.grid;
  const at = PC.sel ? PC.sel.r : g.rows.length - 1;
  g.rows.splice(at + 1, 0, 34);
  // 行索引后移：重建 cells 键
  const moved = {};
  Object.keys(g.cells).forEach(k => {
    const [r, c] = k.split('_').map(Number);
    moved[(r > at ? r + 1 : r) + '_' + c] = g.cells[k];
  });
  g.cells = moved;
  pcRenderGrid();
}

function pcDelRow() {
  const g = PC.grid;
  if (g.rows.length <= 1) { toast('至少保留一行', 'error'); return; }
  const at = PC.sel ? PC.sel.r : g.rows.length - 1;
  g.rows.splice(at, 1);
  const moved = {};
  Object.keys(g.cells).forEach(k => {
    const [r, c] = k.split('_').map(Number);
    if (r === at) return;
    moved[(r > at ? r - 1 : r) + '_' + c] = g.cells[k];
  });
  g.cells = moved;
  PC.sel = null;
  pcRenderGrid();
}

function pcAddCol() {
  const g = PC.grid;
  const at = PC.sel ? PC.sel.c : g.cols.length - 1;
  g.cols.splice(at + 1, 0, 110);
  const moved = {};
  Object.keys(g.cells).forEach(k => {
    const [r, c] = k.split('_').map(Number);
    moved[r + '_' + (c > at ? c + 1 : c)] = g.cells[k];
  });
  g.cells = moved;
  pcRenderGrid();
}

function pcDelCol() {
  const g = PC.grid;
  if (g.cols.length <= 1) { toast('至少保留一列', 'error'); return; }
  const at = PC.sel ? PC.sel.c : g.cols.length - 1;
  g.cols.splice(at, 1);
  const moved = {};
  Object.keys(g.cells).forEach(k => {
    const [r, c] = k.split('_').map(Number);
    if (c === at) return;
    moved[r + '_' + (c > at ? c - 1 : c)] = g.cells[k];
  });
  g.cells = moved;
  PC.sel = null;
  pcRenderGrid();
}

/* 行高 / 列宽调整（作用于当前选中行/列，未选中则全体） */
function pcResize(kind, delta) {
  const g = PC.grid;
  if (kind === 'rows') {
    const list = PC.sel ? [PC.sel.r] : g.rows.map((_, i) => i);
    list.forEach(r => { g.rows[r] = Math.max(18, Math.min(200, (g.rows[r] || 34) + delta)); });
  } else {
    const list = PC.sel ? [PC.sel.c] : g.cols.map((_, i) => i);
    list.forEach(c => { g.cols[c] = Math.max(40, Math.min(500, (g.cols[c] || 110) + delta)); });
  }
  pcRenderGrid();
}

/* ============ 明细表 / 明细行 / 水印 配置面板 ============ */
function pcRenderDetailCfg() {
  const host = document.getElementById('pc-detail-cfg');
  if (!host || !PC.grid) return;
  const d = PC.grid.detail || {};
  const dr = PC.grid.detailRow || {};
  const wm = PC.grid.watermark || {};
  const ft = PC.grid.footer || {};
  host.innerHTML = `
    <div class="pc-cfg-block">
      <div class="pc-cfg-title">📦 明细数据</div>
      <div class="pc-cfg-row">
        <label class="pd-check"><input type="checkbox" ${dr.enabled ? 'checked' : ''} onchange="pcToggleDetailRowEnabled(this.checked)">
          在网格内展开明细行（模板行组：${dr.enabled && Array.isArray(dr.rows) && dr.rows.length ? dr.rows.map(r => '第' + (Number(r) + 1) + '行').join('、') : '—'}）</label>
        <span class="pd-hint-inline">点选一行（或拖选多行）→ 工具栏「🧾 明细行」可指定模板行组</span>
      </div>
      <div class="pc-cfg-row">
        <label class="pd-check"><input type="checkbox" ${d.enabled ? 'checked' : ''} onchange="pcToggleDetail(this.checked)">
          网格下方附带独立明细表</label>
      </div>
      ${d.enabled ? `<div class="pc-cfg-row">
        <span class="pd-hint-inline">明细列：</span>
        ${Object.keys(PRINT_DETAIL_LABELS).map(k => `<label class="pd-check"><input type="checkbox" ${(d.fields || []).includes(k) ? 'checked' : ''}
           onchange="pcToggleDetailField('${k}', this.checked)">${PRINT_DETAIL_LABELS[k]}</label>`).join('')}
        <span class="pc-tb-sep"></span>
        <span class="pd-hint-inline">字号</span>
        <select onchange="PC.grid.detail.fs=Number(this.value);pcRenderGrid()">
          ${[9, 10, 11, 12, 13, 14].map(n => `<option value="${n}" ${Number(d.fs) === n ? 'selected' : ''}>${n}</option>`).join('')}
        </select>
      </div>` : ''}
    </div>
    <div class="pc-cfg-block">
      <div class="pc-cfg-title">💧 水印</div>
      <div class="pc-cfg-row">
        <label class="pd-check"><input type="checkbox" ${wm.enabled ? 'checked' : ''} onchange="pcToggleWatermark(this.checked)"> 启用</label>
        <span class="pd-hint-inline">排列</span>
        <select onchange="pcWatermarkChange('mode', this.value)">
          <option value="tile" ${(wm.mode || 'tile') === 'tile' ? 'selected' : ''}>平铺重复</option>
          <option value="center" ${wm.mode === 'center' ? 'selected' : ''}>居中单排</option>
        </select>
        <input type="text" class="pc-wm-text" value="${escapeHtml(wm.text || '')}" placeholder="水印文字，支持 {字段}，如 {CompanyName}"
               oninput="pcWatermarkChange('text', this.value)">
        <label class="pc-tb-color" title="文字颜色">A<input type="color" value="${wm.color || '#93c5fd'}" oninput="pcWatermarkChange('color', this.value)"></label>
        <span class="pd-hint-inline">字号</span>
        <input type="number" class="pc-wm-num" min="8" max="60" value="${Number(wm.size) || 20}"
               oninput="pcWatermarkChange('size', Number(this.value))">
        <span class="pd-hint-inline">角度</span>
        <input type="number" class="pc-wm-num" min="-90" max="90" value="${Number(wm.rotate) || -30}"
               oninput="pcWatermarkChange('rotate', Number(this.value))">
        <span class="pd-hint-inline">透明度</span>
        <input type="range" class="pc-wm-range" min="0.05" max="0.6" step="0.01" value="${wm.opacity || 0.18}"
               oninput="pcWatermarkChange('opacity', Number(this.value))">
      </div>
      <div class="pc-cfg-row">
        <input type="text" class="pc-wm-text" style="width:420px" value="${escapeHtml(wm.image || '')}"
               placeholder="图片水印地址（填写后优先用图片），如 /uploads/logo.png 或 data:image/png;base64,..."
               oninput="pcWatermarkChange('image', this.value)">
        <span class="pd-hint-inline">图片宽度</span>
        <input type="number" class="pc-wm-num" min="40" max="600" value="${Number(wm.imageWidth) || 200}"
               oninput="pcWatermarkChange('imageWidth', Number(this.value))">
        <span class="pd-hint-inline">提示：把 Logo 放到站点 wwwroot 目录下即可用相对路径</span>
      </div>
    </div>
    <div class="pc-cfg-block">
      <div class="pc-cfg-title">📄 页脚</div>
      <div class="pc-cfg-row">
        <label class="pd-check"><input type="checkbox" ${ft.enabled ? 'checked' : ''} onchange="pcToggleFooter(this.checked)"> 启用页脚</label>
        <input type="text" class="pc-wm-text" style="width:340px" value="${escapeHtml(ft.text || '')}" placeholder="页脚文本，支持 {字段}"
               oninput="pcFooterChange('text', this.value)">
        <span class="pd-hint-inline">对齐</span>
        <select onchange="pcFooterChange('align', this.value)">
          <option value="left" ${ft.align === 'left' ? 'selected' : ''}>居左</option>
          <option value="center" ${(ft.align || 'center') === 'center' ? 'selected' : ''}>居中</option>
          <option value="right" ${ft.align === 'right' ? 'selected' : ''}>居右</option>
        </select>
        <span class="pd-hint-inline">字号</span>
        <input type="number" class="pc-wm-num" min="8" max="18" value="${Number(ft.fontSize) || 10}"
               oninput="pcFooterChange('fontSize', Number(this.value))">
      </div>
      <div class="pc-cfg-row">
        <label class="pd-check"><input type="checkbox" ${ft.showTime !== false ? 'checked' : ''} onchange="pcFooterChange('showTime', this.checked)"> 打印时间</label>
        <label class="pd-check"><input type="checkbox" ${ft.showPage !== false ? 'checked' : ''} onchange="pcFooterChange('showPage', this.checked)"> 页码</label>
        <label class="pd-check"><input type="checkbox" ${ft.showUser ? 'checked' : ''} onchange="pcFooterChange('showUser', this.checked)"> 打印人</label>
        <label class="pd-check"><input type="checkbox" ${ft.borderTop !== false ? 'checked' : ''} onchange="pcFooterChange('borderTop', this.checked)"> 上边框</label>
      </div>
    </div>`;
}

function pcToggleFooter(enabled) {
  PC.grid.footer = PC.grid.footer || {};
  PC.grid.footer.enabled = !!enabled;
  pcRenderDetailCfg();
}

function pcFooterChange(prop, value) {
  PC.grid.footer = PC.grid.footer || {};
  PC.grid.footer[prop] = value;
}

function pcToggleDetailRowEnabled(enabled) {
  PC.grid.detailRow = PC.grid.detailRow || { enabled: false, rows: [0] };
  if (!Array.isArray(PC.grid.detailRow.rows) || !PC.grid.detailRow.rows.length) PC.grid.detailRow.rows = [0];
  PC.grid.detailRow.enabled = !!enabled;
  pcRenderGrid();
  pcRenderDetailCfg();
}

function pcToggleDetail(enabled) {
  PC.grid.detail.enabled = enabled;
  pcRenderGrid();
}

function pcToggleDetailField(key, checked) {
  const fields = PC.grid.detail.fields || [];
  if (checked && !fields.includes(key)) fields.push(key);
  if (!checked) PC.grid.detail.fields = fields.filter(f => f !== key);
  else PC.grid.detail.fields = fields;
  pcRenderGrid();
}

/* ============ 右侧：系统字段面板 ============ */
function pcFieldGroups() {
  const labels = billFieldLabels(PC.code);
  const billFields = Object.keys(labels).map(k => ({ key: k, label: labels[k] }));
  const detailFields = Object.keys(PRINT_DETAIL_LABELS).map(k => ({ key: k, label: PRINT_DETAIL_LABELS[k] }));
  return [
    { name: '公司信息 Company', icon: '🏢', items: PC_COMPANY_FIELDS.map(f => ({ key: f[0], label: f[1] })) },
    { name: (PC.title || '单据') + ' 主表字段', icon: '📋', items: billFields },
    { name: '明细行字段', icon: '📦', items: detailFields },
  ];
}

function pcRenderFields() {
  const host = document.getElementById('pc-field-list');
  if (!host) return;
  const kw = ((document.getElementById('pc-field-search') || {}).value || '').trim().toLowerCase();
  const groups = pcFieldGroups();
  host.innerHTML = groups.map(g => {
    const items = g.items.filter(f => !kw || f.label.toLowerCase().includes(kw) || f.key.toLowerCase().includes(kw));
    if (!items.length) return '';
    return `<div class="pc-field-group">
        <div class="pc-field-group-title">${g.icon} ${escapeHtml(g.name)}<span>${items.length}</span></div>
        <div class="pc-field-items">
          ${items.map(f => `<button class="pc-field-btn" draggable="true"
              ondragstart="pcFieldDragStart(event, '${f.key}')"
              onclick="pcInsertField('${f.key}')" title="点击插入到当前单元格，或直接拖到网格单元格">
              <span class="pc-field-name">${escapeHtml(f.label)}</span>
              <code>{${escapeHtml(f.key)}}</code>
            </button>`).join('')}
        </div>
      </div>`;
  }).join('') || '<div class="pd-side-empty">未找到匹配字段</div>';
}

function pcFilterFields() { pcRenderFields(); }

/* 插入字段到当前单元格（追加到内容末尾） */
function pcInsertField(key) {
  const cell = pcCurrentCell();
  if (!cell) { toast('请先在网格中点选一个单元格', 'error'); return; }
  const token = '{' + key + '}';
  cell.v = (cell.v || '') + token;
  const input = document.getElementById('pc-cell-content');
  if (input) input.value = cell.v;
  const td = document.querySelector(`.pc-cell[data-r="${PC.sel.r}"][data-c="${PC.sel.c}"]`);
  if (td) td.textContent = cell.v;
  toast('已插入字段 ' + token);
}

/* 清除当前单元格绑定（连同普通文本一并清空） */
function pcClearCell() {
  if (!PC.sel) { toast('请先在网格中点选一个单元格', 'error'); return; }
  PC.grid.cells[PC.sel.r + '_' + PC.sel.c] = pcNewCell();
  const input = document.getElementById('pc-cell-content');
  if (input) input.value = '';
  pcRenderGrid();
  pcSelectCell(PC.sel.r, PC.sel.c);
  toast('已清除当前单元格');
}

/* ============ 保存模板 ============ */
async function pcSaveLayout() {
  if (!PC.template || !PC.template.Id) { toast('模板信息缺失，请重新打开', 'error'); return; }
  const btn = event && event.target;
  try {
    if (btn) { btn.disabled = true; btn.textContent = '保存中…'; }
    const payload = Object.assign({}, {
      Id: PC.template.Id,
      BillType: PC.code,
      TemplateName: PC.template.TemplateName || '默认模板',
      Title: PC.template.Title || PC.title,
      CompanyName: PC.template.CompanyName || '',
      CompanyAddress: PC.template.CompanyAddress || '',
      CompanyPhone: PC.template.CompanyPhone || '',
      PaperSize: PC.template.PaperSize || 'A4',
      FontSize: PC.template.FontSize || 12,
      FooterText: PC.template.FooterText || '',
      ShowCompanyHeader: PC.template.ShowCompanyHeader !== false,
      ShowDetailTable: PC.template.ShowDetailTable !== false,
      ShowRemark: PC.template.ShowRemark !== false,
      IsDefault: !!PC.template.IsDefault,
      FieldKeys: PC.template.FieldKeys || '',
      LayoutJson: JSON.stringify(PC.grid),
    });
    const saved = await api('/api/sys/print-templates', 'POST', payload);
    if (saved) PC.template = normalizeTemplate(saved);
    toast('模板已保存（网格布局 + 明细表配置）');
  } catch (e) { toast('保存失败：' + e.message, 'error'); }
  finally { if (btn) { btn.disabled = false; btn.textContent = '💾 保存模板'; } }
}

/* ============ 网格 → 打印 HTML（占位符替换 + 明细行展开） ============ */
function pcRenderCellValue(v, main, ctx) {
  const s = String(v == null ? '' : v);
  if (s.trim().startsWith('=')) {
    return pcEvalFormula(s.trim(), (ctx && ctx.grid) || PC.grid, main, (ctx && ctx.details) || []);
  }
  return s.replace(/\{(\w+)\}/g, function (m, key) {
    if (main && main[key] !== undefined && main[key] !== null && main[key] !== '') return String(main[key]);
    return m;                     // 无数据时保留占位符，便于核对
  });
}

/* 单元格引用 "B3" → { r, c }（0 基） */
function pcCellIndex(ref) {
  const m = /^([A-Za-z]+)(\d+)$/.exec(String(ref || '').trim());
  if (!m) return null;
  const letters = m[1].toUpperCase();
  let c = 0;
  for (let i = 0; i < letters.length; i++) c = c * 26 + (letters.charCodeAt(i) - 64);
  return { r: Number(m[2]) - 1, c: c - 1 };
}

/* 公式求值：=SUM(A1:A5, 明细.Amount, 100) / AVG / COUNT / MAX / MIN
   说明：明细字段用「明细.字段名」，网格区间用「A1:A5」，也支持直接填数字 */
function pcEvalFormula(expr, grid, main, details) {
  const m = /^=\s*([A-Za-z]+)\s*\((.*)\)\s*$/.exec(expr);
  if (!m) return '#公式错误';
  const fn = m[1].toUpperCase();
  const args = m[2].split(',').map(s => s.trim()).filter(Boolean);
  const nums = [];
  const push = v => {
    const raw = String(v == null ? '' : v);
    if (raw.trim() === '') return;
    const n = Number(raw.replace(/[,\s￥$]/g, ''));
    if (!isNaN(n)) nums.push(n);
  };

  args.forEach(a => {
    if (/^明细\.(\w+)$/.test(a)) {                                  // 明细字段聚合
      const key = a.split('.')[1];
      (details || []).forEach(d => push(d[key]));
    } else if (/^[A-Za-z]+\d+(:[A-Za-z]+\d+)?$/.test(a)) {          // 网格单元格 / 区间
      const parts = a.split(':');
      const p1 = pcCellIndex(parts[0]);
      if (!p1) return;
      if (parts[1]) {
        const p2 = pcCellIndex(parts[1]);
        if (!p2) return;
        for (let r = Math.min(p1.r, p2.r); r <= Math.max(p1.r, p2.r); r++)
          for (let c = Math.min(p1.c, p2.c); c <= Math.max(p1.c, p2.c); c++) {
            const cell = (grid && grid.cells ? grid.cells[r + '_' + c] : null);
            if (cell && cell.v && !String(cell.v).trim().startsWith('=')) push(pcRenderCellValue(cell.v, main));
          }
      } else {
        const cell = (grid && grid.cells ? grid.cells[p1.r + '_' + p1.c] : null);
        if (cell && cell.v && !String(cell.v).trim().startsWith('=')) push(pcRenderCellValue(cell.v, main));
      }
    } else {
      push(a);                                                       // 直接数字
    }
  });

  const sum = nums.reduce((a, b) => a + b, 0);
  switch (fn) {
    case 'SUM': return sum.toFixed(2);
    case 'AVG': return (nums.length ? sum / nums.length : 0).toFixed(2);
    case 'COUNT': return String(nums.length);
    case 'MAX': return (nums.length ? Math.max.apply(null, nums) : 0).toFixed(2);
    case 'MIN': return (nums.length ? Math.min.apply(null, nums) : 0).toFixed(2);
    default: return '#未知函数';
  }
}

function buildGridPrintHtml(template, code, main, details, pageCtx) {
  const g = pcParseLayout((template && (template.LayoutJson || template.layoutJson)) || null);
  const font = (g.font || 'Microsoft YaHei') + ', "SimSun", Arial, sans-serif';
  const ctx = { grid: g, details: details || [] };            // 公式求值上下文

  // 合并区覆盖集合
  const coveredSet = {};
  (g.merges || []).forEach(m => {
    for (let r = m.r; r < m.r + (m.rs || 1); r++) {
      for (let c = m.c; c < m.c + (m.cs || 1); c++) {
        if (r === m.r && c === m.c) continue;
        coveredSet[r + '_' + c] = true;
      }
    }
  });
  const mergeAt = (r, c) => (g.merges || []).find(m =>
    r >= m.r && r < m.r + (m.rs || 1) && c >= m.c && c < m.c + (m.cs || 1)) || null;
  const dr = g.detailRow || {};
  const drRows = (Array.isArray(dr.rows) ? dr.rows : []).map(Number).filter(n => !isNaN(n)).sort((a, b) => a - b);
  const drSet = {};
  drRows.forEach(n => { drSet[n] = true; });
  const drFirst = drRows.length ? drRows[0] : -1;

  let html = `<div class="print-page" style="position:relative;font-family:${font}">`;
  html += pcWatermarkLayerHtml(g.watermark, main, ctx);           // 水印层（文本 / 图片）
  html += '<table class="pc-print-grid" style="border-collapse:collapse;width:100%;position:relative;z-index:1">';
  html += '<colgroup>' + g.cols.map(w => `<col style="width:${w}px">`).join('') + '</colgroup>';

  const rowHtml = (r, dataForRow) => {
    let out = `<tr style="height:${g.rows[r]}px">`;
    for (let c = 0; c < g.cols.length; c++) {
      if (coveredSet[r + '_' + c]) continue;                       // 合并覆盖区不输出
      const m = mergeAt(r, c);
      const cell = g.cells[r + '_' + c] || {};
      const span = m ? ` rowspan="${m.rs || 1}" colspan="${m.cs || 1}"` : '';
      out += `<td${span} style="${pcCellStyle(cell)}">${escapeHtml(pcRenderCellValue(cell.v, dataForRow, ctx))}</td>`;
    }
    out += '</tr>';
    return out;
  };

  for (let r = 0; r < g.rows.length; r++) {
    if (dr.enabled && drRows.length && r === drFirst && details && details.length) {
      // 明细模板行组（可多行）：按明细条数循环，逐条展开整组行（主行 + 子行成对展开）
      details.forEach(row => {
        const merged = Object.assign({}, main, row);
        drRows.forEach(rr => { html += rowHtml(rr, merged); });
      });
    } else if (dr.enabled && drSet[r] && details && details.length) {
      continue;                                   // 该组行已在上面的循环中渲染
    } else {
      html += rowHtml(r, main);
    }
  }
  html += '</table>';

  // 网格下方的独立明细表（可选）
  const d = g.detail || {};
  if (d.enabled && (d.fields || []).length && details && details.length) {
    const fs = Number(d.fs) || 12;
    html += `<table class="pc-print-detail" style="border-collapse:collapse;width:100%;margin-top:10px;font-size:${fs}px;position:relative;z-index:1">
      <thead><tr>${d.fields.map(k => `<th style="background:${d.headBg || '#f2f2f2'};border:1px solid #999;padding:4px 6px;text-align:left">${escapeHtml(PRINT_DETAIL_LABELS[k] || k)}</th>`).join('')}</tr></thead>
      <tbody>${details.map(row => `<tr>${d.fields.map(k => {
        const isNum = ['Quantity', 'UnitPrice', 'Amount', 'Cartons', 'Weight', 'Volume'].indexOf(k) >= 0;
        const v = row[k];
        return `<td style="border:1px solid #999;padding:4px 6px;${isNum ? 'text-align:right' : ''}">${escapeHtml(isNum ? fmtMoney(v) : (v == null ? '' : v))}</td>`;
      }).join('')}</tr>`).join('')}</tbody>
    </table>`;
  }
  // ============ 页脚 ============
  const f = g.footer || {};
  if (f.enabled) {
    const parts = [];
    if (f.text) parts.push(pcRenderCellValue(f.text, main, ctx));
    if (f.showTime) parts.push('打印时间：' + new Date().toLocaleString('zh-CN'));
    if (f.showUser) parts.push('打印人：' + (main.Operator || main.__operator || ''));
    if (f.showPage) {
      parts.push((pageCtx && pageCtx.total > 1)
        ? ('第 ' + pageCtx.page + ' / ' + pageCtx.total + ' 页')
        : '共 1 页');
    }
    const fStyle = [
      'text-align:' + (f.align || 'center'),
      'font-size:' + (Number(f.fontSize) || 10) + 'px',
      f.borderTop ? 'border-top:1px solid #999;padding-top:6px;margin-top:10px' : 'margin-top:10px',
      'position:relative;z-index:1',
    ].join(';');
    html += `<div class="pc-print-footer" style="${fStyle}">${escapeHtml(parts.join('　'))}</div>`;
  }

  html += '</div>';
  return html;
}

/* 示例数据（预览与打印测试用） */
function pcSampleMain() {
  const labels = billFieldLabels(PC.code);
  const main = { BillNo: 'DEMO-20260918-001', Status: '已审核' };
  Object.keys(labels).forEach(k => { if (main[k] === undefined) main[k] = pdSampleValue(k, labels[k]); });
  main.CompanyName = main.CompanyName || '金华市奕鸣科技有限公司';
  main.CompanyNameEn = main.CompanyNameEn || 'Jinhua Yiming Technology Co., Ltd.';
  main.CompanyAddress = main.CompanyAddress || '浙江省金华市义乌国际商贸城';
  main.CompanyPhone = main.CompanyPhone || '0579-88886666';
  main.CompanyEmail = main.CompanyEmail || 'sales@yiming.com';
  return main;
}

function pcSampleDetails() {
  return [1, 2, 3].map(n => {
    const row = {};
    Object.keys(PRINT_DETAIL_LABELS).forEach(k => { row[k] = ''; });
    row.ProductName = '示例商品 ' + n;
    row.Spec = '标准规格';
    row.Quantity = n * 10;
    row.Unit = 'PCS';
    row.UnitPrice = 12.5;
    row.Amount = n * 125;
    return row;
  });
}

/* ============ 预览 / 打印测试 ============ */
function pcPreview() {
  if (!PC.grid) { toast('请先打开模板', 'error'); return; }
  pcShowPaper(buildGridPrintHtml({ LayoutJson: JSON.stringify(PC.grid) }, PC.code, pcSampleMain(), pcSampleDetails()), PC.title);
}

async function pcPreviewTemplate(id) {
  try {
    const list = await api(`/api/sys/print-templates?billType=${encodeURIComponent(PC.code)}`);
    const items = Array.isArray(list) ? list : (list.items || []);
    const raw = normalizeTemplate(items.find(t => t.id === id) || {});
    PC.template = raw;
    pcShowPaper(buildGridPrintHtml(raw, PC.code, pcSampleMain(), pcSampleDetails()), raw.TemplateName || PC.title);
  } catch (e) { toast(e.message, 'error'); }
}

function pcShowPaper(html, title) {
  const box = document.createElement('div');
  box.id = 'print-design-preview';
  box.className = 'pd-modal';
  box.innerHTML = `<div class="pd-modal-box">
      <div class="pd-modal-head"><b>👁 打印效果预览 · ${escapeHtml(title)}（示例数据）</b>
        <span class="pd-modal-actions">
          <button class="btn btn-neutral btn-sm" onclick="pcPrintTest()">🖨 打印测试</button>
          <button class="btn btn-neutral btn-sm" onclick="closePrintDesignPreview()">关闭</button>
        </span>
      </div>
      <div class="pd-modal-body"><div class="pd-paper" id="pc-print-paper">${html}</div></div>
    </div>`;
  document.body.appendChild(box);
}

function pcPrintTest() {
  const paper = document.getElementById('pc-print-paper');
  const content = paper ? paper.innerHTML : '';
  if (!content) { toast('请先预览后再打印', 'error'); return; }
  const win = window.open('', '_blank', 'width=920,height=720');
  win.document.write(`<!DOCTYPE html><html lang="zh-CN"><head><meta charset="utf-8">
    <title>${escapeHtml(PC.title)} - 打印测试</title>
    <style>${PRINT_STYLE}
    .print-page { position: relative; }
    .pw-layer { position: absolute; inset: 0; display: grid; grid-template-columns: repeat(4, 1fr);
      align-items: center; justify-items: center; pointer-events: none; overflow: hidden; z-index: 0; }
    .pw-layer span { font-size: var(--pw-size, 20px); font-weight: 700; white-space: nowrap; letter-spacing: 2px; }
    .pc-print-grid, .pc-print-detail { position: relative; z-index: 1; }
    body { margin: 0; } @page { margin: 8mm; }</style></head>
    <body>${content}</body></html>`);
  win.document.close();
  setTimeout(() => { try { win.focus(); win.print(); } catch (e) { /* 忽略 */ } }, 400);
}

/* ============ 模板另存为 / 复制 ============ */
async function pcSaveAs() {
  if (!PC.grid) { toast('请先打开模板', 'error'); return; }
  const cur = (PC.template && PC.template.TemplateName) || PC.title;
  const input = prompt('另存为新的模板名称', cur + ' - 副本');
  if (input === null) return;
  const name = (input || '').trim();
  if (!name) { toast('模板名称不能为空', 'error'); return; }
  try {
    await api('/api/sys/print-templates', 'POST', {
      Id: 0, BillType: PC.code, TemplateName: name,
      Title: PC.template.Title || PC.title,
      PaperSize: PC.template.PaperSize || 'A4', FontSize: PC.template.FontSize || 12,
      FieldKeys: PC.template.FieldKeys || '', FooterText: PC.template.FooterText || '',
      ShowCompanyHeader: PC.template.ShowCompanyHeader !== false,
      ShowDetailTable: PC.template.ShowDetailTable !== false,
      ShowRemark: PC.template.ShowRemark !== false,
      IsDefault: false,
      LayoutJson: JSON.stringify(PC.grid),
    });
    toast('已另存为新模板：' + name);
  } catch (e) { toast('另存为失败：' + e.message, 'error'); }
}

async function pcCopyTemplate(id) {
  try {
    const list = await api(`/api/sys/print-templates?billType=${encodeURIComponent(PC.code)}`);
    const items = (Array.isArray(list) ? list : (list.items || [])).map(normalizeTemplate);
    const src = items.find(t => t.Id === id);
    if (!src) { toast('模板不存在', 'error'); return; }
    const name = (src.TemplateName || '模板') + ' - 副本';
    await api('/api/sys/print-templates', 'POST', {
      Id: 0, BillType: PC.code, TemplateName: name,
      Title: src.Title || PC.title, PaperSize: src.PaperSize || 'A4', FontSize: src.FontSize || 12,
      FieldKeys: src.FieldKeys || '', FooterText: src.FooterText || '',
      ShowCompanyHeader: src.ShowCompanyHeader !== false,
      ShowDetailTable: src.ShowDetailTable !== false,
      ShowRemark: src.ShowRemark !== false, IsDefault: false,
      LayoutJson: src.LayoutJson || '',
    });
    toast('已复制为：' + name);
    pcSelectDoc(PC.code);
  } catch (e) { toast('复制失败：' + e.message, 'error'); }
}

/* ============ 模板版本对比 ============ */
let PC_CMP = [];

function pcToggleCompare(id) {
  const i = PC_CMP.indexOf(id);
  if (i >= 0) PC_CMP.splice(i, 1);
  else { if (PC_CMP.length >= 2) PC_CMP.shift(); PC_CMP.push(id); }
  pcSelectDoc(PC.code);
  toast('已选择 ' + PC_CMP.length + ' / 2 个模板用于对比');
}

async function pcCompareTemplates() {
  if (PC_CMP.length !== 2) { toast('请先勾选 2 个模板再点「对比所选」', 'error'); return; }
  try {
    const list = await api(`/api/sys/print-templates?billType=${encodeURIComponent(PC.code)}`);
    const items = (Array.isArray(list) ? list : (list.items || [])).map(normalizeTemplate);
    const a = items.find(t => t.Id === PC_CMP[0]);
    const b = items.find(t => t.Id === PC_CMP[1]);
    if (!a || !b) { toast('模板不存在', 'error'); return; }

    const ga = pcParseLayout(a.LayoutJson), gb = pcParseLayout(b.LayoutJson);
    const diffs = [];
    if (ga.rows.length !== gb.rows.length) diffs.push('行数：' + ga.rows.length + ' → ' + gb.rows.length);
    if (ga.cols.length !== gb.cols.length) diffs.push('列数：' + ga.cols.length + ' → ' + gb.cols.length);
    if ((ga.merges || []).length !== (gb.merges || []).length)
      diffs.push('合并区数量：' + (ga.merges || []).length + ' → ' + (gb.merges || []).length);
    const wa = ga.watermark || {}, wb = gb.watermark || {};
    if (wa.enabled !== wb.enabled || (wa.text || '') !== (wb.text || '') || (wa.image || '') !== (wb.image || ''))
      diffs.push('水印：' + (wa.enabled ? (wa.image ? '图片水印' : (wa.text || '')) : '关闭')
        + ' → ' + (wb.enabled ? (wb.image ? '图片水印' : (wb.text || '')) : '关闭'));
    const fa = ga.footer || {}, fb = gb.footer || {};
    if (fa.enabled !== fb.enabled || (fa.text || '') !== (fb.text || ''))
      diffs.push('页脚：' + (fa.enabled ? (fa.text || '') : '关闭') + ' → ' + (fb.enabled ? (fb.text || '') : '关闭'));
    if (JSON.stringify(ga.detailRow || {}) !== JSON.stringify(gb.detailRow || {}))
      diffs.push('明细模板行：' + JSON.stringify(ga.detailRow || {}) + ' → ' + JSON.stringify(gb.detailRow || {}));
    if ((ga.font || '') !== (gb.font || '')) diffs.push('字体：' + ga.font + ' → ' + gb.font);
    if ((a.PaperSize || '') !== (b.PaperSize || '')) diffs.push('纸张：' + a.PaperSize + ' → ' + b.PaperSize);

    const keys = {};
    Object.keys(ga.cells || {}).forEach(k => { keys[k] = 1; });
    Object.keys(gb.cells || {}).forEach(k => { keys[k] = 1; });
    const cellDiffs = [];
    Object.keys(keys).forEach(k => {
      const sig = c => c ? [(c.v || ''), (c.fs || 12), (c.bold ? 'B' : ''), (c.bg || ''), (c.align || 'left'), (c.color || '')].join('|') : '（空）';
      const sa = sig((ga.cells || {})[k]), sb = sig((gb.cells || {})[k]);
      if (sa !== sb) {
        const parts = k.split('_');
        cellDiffs.push({ ref: pcGridColName(Number(parts[1])) + (Number(parts[0]) + 1), a: sa, b: sb });
      }
    });

    document.getElementById('modal').innerHTML = `<div class="modal" style="width:920px">
      <h3>🔍 模板版本对比</h3>
      <div class="cmp-head">
        <div><span class="cmp-tag cmp-old">A</span> ${escapeHtml(a.TemplateName || '')}</div>
        <div><span class="cmp-tag cmp-new">B</span> ${escapeHtml(b.TemplateName || '')}</div>
      </div>
      <div class="pd-field"><label>结构与配置差异（${diffs.length} 项）</label>
        <ul class="cmp-list">${diffs.length ? diffs.map(d => '<li>' + escapeHtml(d) + '</li>').join('') : '<li>无差异</li>'}</ul>
      </div>
      <div class="pd-field"><label>单元格差异（${cellDiffs.length} 处）</label>
        <div class="table-wrap" style="max-height:340px;overflow:auto">
          ${cellDiffs.length
        ? `<table><thead><tr><th style="width:80px">单元格</th><th>模板 A（内容/字号/加粗/底色/对齐/颜色）</th><th>模板 B</th></tr></thead><tbody>
                ${cellDiffs.slice(0, 300).map(d => `<tr><td><b>${escapeHtml(d.ref)}</b></td><td class="cmp-old">${escapeHtml(d.a)}</td><td class="cmp-new">${escapeHtml(d.b)}</td></tr>`).join('')}
              </tbody></table>`
        : '<div class="empty">单元格内容与样式完全一致</div>'}
        </div>
      </div>
      <div class="modal-footer">
        <button class="btn btn-neutral" onclick="PC_CMP=[];closeModal();pcSelectDoc(PC.code)">关闭并清空选择</button>
      </div>
    </div>`;
    document.getElementById('modal').style.display = 'flex';
  } catch (e) { toast('对比失败：' + e.message, 'error'); }
}

/* ============ 模板删除 ============ */
async function pcDeleteTemplate(id) {
  if (!confirm('确认删除该打印模板？')) return;
  try {
    await api(`/api/sys/print-templates/${id}`, 'DELETE');
    toast('模板已删除');
    pcSelectDoc(PC.code);
  } catch (e) { toast(e.message, 'error'); }
}

/* ============ 撤销 / 重做 ============ */
function pcPushHistory() {
  if (!PC.grid) return;
  PC.history = PC.history || [];
  PC.history.push(JSON.stringify(PC.grid));
  if (PC.history.length > 40) PC.history.shift();
  PC.future = [];
}

function pcUndo() {
  if (!PC.history || !PC.history.length) { toast('没有可撤销的操作', 'error'); return; }
  PC.future = PC.future || [];
  PC.future.push(JSON.stringify(PC.grid));
  PC.grid = JSON.parse(PC.history.pop());
  PC.sel = null;
  pcRenderGrid();
  toast('已撤销');
}

function pcRedo() {
  if (!PC.future || !PC.future.length) { toast('没有可重做的操作', 'error'); return; }
  PC.history = PC.history || [];
  PC.history.push(JSON.stringify(PC.grid));
  PC.grid = JSON.parse(PC.future.pop());
  PC.sel = null;
  pcRenderGrid();
  toast('已重做');
}

/* ============ 复制 / 粘贴单元格（含样式） ============ */
let PC_CLIP = null;

function pcCopyCell() {
  if (!PC.sel || !PC.grid) return;
  const cell = PC.grid.cells[PC.sel.r + '_' + PC.sel.c];
  if (!cell) { toast('当前单元格为空，无可复制内容', 'error'); return; }
  PC_CLIP = JSON.parse(JSON.stringify(cell));
  toast('已复制单元格（含样式），选中目标后按 Ctrl+V 粘贴');
}

function pcPasteCell() {
  if (!PC.sel || !PC_CLIP) { toast('剪贴板为空，请先 Ctrl+C 复制', 'error'); return; }
  pcPushHistory();
  pcSelectionKeys().forEach(key => { PC.grid.cells[key] = JSON.parse(JSON.stringify(PC_CLIP)); });
  pcRenderGrid();
  pcSelectCell(PC.sel.r, PC.sel.c);
  toast('已粘贴（' + pcSelectionKeys().length + ' 个单元格）');
}

/* ============ 单元格内联编辑（双击 / F2） ============ */
function pcBeginEdit(r, c) {
  if (!PC.grid) return;
  const td = document.querySelector(`.pc-cell[data-r="${r}"][data-c="${c}"]`);
  if (!td || td.classList.contains('editing')) return;
  pcSelectCell(r, c);
  pcPushHistory();
  td.contentEditable = 'true';
  td.classList.add('editing');
  td.focus();
  try {
    const range = document.createRange();
    range.selectNodeContents(td);
    range.collapse(false);
    const sel = window.getSelection();
    sel.removeAllRanges();
    sel.addRange(range);
  } catch (e) { /* 忽略 */ }
  td.onblur = () => pcEndEdit(r, c, td, true);
  td.onkeydown = (e) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault(); e.stopPropagation();
      pcEndEdit(r, c, td, true);
      pcSelectCell(Math.min(r + 1, PC.grid.rows.length - 1), c);
    } else if (e.key === 'Escape') {
      e.preventDefault(); e.stopPropagation();
      pcEndEdit(r, c, td, false);
    } else if (e.key === 'Tab') {
      e.preventDefault(); e.stopPropagation();
      pcEndEdit(r, c, td, true);
      pcSelectCell(r, Math.min(c + 1, PC.grid.cols.length - 1));
    } else if (e.key === 'Enter' && e.shiftKey) {
      // Shift+Enter 换行（默认行为）
    }
  };
}

function pcEndEdit(r, c, td, save) {
  if (!td.classList.contains('editing')) return;
  td.classList.remove('editing');
  td.removeAttribute('contenteditable');
  td.onblur = null;
  td.onkeydown = null;
  const key = r + '_' + c;
  const text = td.textContent || '';
  if (save) {
    if (!PC.grid.cells[key]) PC.grid.cells[key] = pcNewCell();
    PC.grid.cells[key].v = text;
    const input = document.getElementById('pc-cell-content');
    if (input) input.value = text;
  } else {
    td.textContent = (PC.grid.cells[key] || {}).v || '';
  }
  pcRenderStatusBar();
}

/* ============ 键盘导航与快捷键（仅在模板编辑器中生效） ============ */
function pcGlobalKeydown(e) {
  if (!PC.grid || !document.getElementById('pc-grid-host')) return;
  const t = e.target || {};
  const tag = (t.tagName || '').toLowerCase();
  const editing = tag === 'input' || tag === 'textarea' || tag === 'select' || t.isContentEditable;

  if ((e.ctrlKey || e.metaKey) && !editing) {
    const k = (e.key || '').toLowerCase();
    if (k === 'z') { e.preventDefault(); pcUndo(); return; }
    if (k === 'y') { e.preventDefault(); pcRedo(); return; }
    if (k === 'c') { e.preventDefault(); pcCopyCell(); return; }
    if (k === 'v') { e.preventDefault(); pcPasteCell(); return; }
    if (k === 's') { e.preventDefault(); pcSaveLayout(); return; }
  }
  if (editing || !PC.sel) return;

  const g = PC.grid;
  let moved = false;
  switch (e.key) {
    case 'ArrowUp': PC.sel.r = Math.max(0, PC.sel.r - 1); moved = true; break;
    case 'ArrowDown': PC.sel.r = Math.min(g.rows.length - 1, PC.sel.r + 1); moved = true; break;
    case 'ArrowLeft': PC.sel.c = Math.max(0, PC.sel.c - 1); moved = true; break;
    case 'ArrowRight': PC.sel.c = Math.min(g.cols.length - 1, PC.sel.c + 1); moved = true; break;
    case 'F2': e.preventDefault(); pcBeginEdit(PC.sel.r, PC.sel.c); return;
    case 'Delete':
    case 'Backspace':
      e.preventDefault();
      pcPushHistory();
      delete g.cells[PC.sel.r + '_' + PC.sel.c];
      PC.selType = 'cell';
      pcRenderGrid();
      pcSelectCell(PC.sel.r, PC.sel.c);
      return;
    default: break;
  }
  if (moved) {
    e.preventDefault();
    PC.selType = 'cell';
    pcRenderGrid();
    pcSelectCell(PC.sel.r, PC.sel.c);
    const td = document.querySelector(`.pc-cell[data-r="${PC.sel.r}"][data-c="${PC.sel.c}"]`);
    if (td && td.scrollIntoView) td.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  }
}
document.addEventListener('keydown', pcGlobalKeydown);

/* ============ 字段拖拽绑定 ============ */
function pcFieldDragStart(e, key) {
  if (e.dataTransfer) {
    e.dataTransfer.setData('text/plain', key);
    e.dataTransfer.effectAllowed = 'copy';
  }
}

function pcCellDragOver(e) { e.preventDefault(); }

function pcCellDrop(e, r, c) {
  e.preventDefault();
  const key = e.dataTransfer ? e.dataTransfer.getData('text/plain') : '';
  if (!key) return;
  pcPushHistory();
  const ck = r + '_' + c;
  if (!PC.grid.cells[ck]) PC.grid.cells[ck] = pcNewCell();
  PC.grid.cells[ck].v = (PC.grid.cells[ck].v || '') + '{' + key + '}';
  pcRenderGrid();
  pcSelectCell(r, c);
  toast('已把字段 {' + key + '} 绑定到 ' + pcGridColName(c) + (r + 1));
}

/* ============ 工具栏：把当前单元格样式套用到整行 / 整列 ============ */
function pcApplyToRow() {
  if (!PC.sel) { toast('请先点选一个单元格', 'error'); return; }
  PC.selType = 'row';
  const src = PC.grid.cells[PC.sel.r + '_' + PC.sel.c] || pcNewCell();
  pcPushHistory();
  for (let c = 0; c < PC.grid.cols.length; c++) {
    const k = PC.sel.r + '_' + c;
    const v = PC.grid.cells[k] ? PC.grid.cells[k].v : '';
    PC.grid.cells[k] = Object.assign(pcNewCell(), { bold: src.bold, italic: src.italic, fs: src.fs, color: src.color, bg: src.bg, align: src.align, bd: src.bd, v: v });
  }
  pcRenderGrid();
  toast('已将样式套用到第 ' + (PC.sel.r + 1) + ' 行');
}

function pcApplyToCol() {
  if (!PC.sel) { toast('请先点选一个单元格', 'error'); return; }
  PC.selType = 'col';
  const src = PC.grid.cells[PC.sel.r + '_' + PC.sel.c] || pcNewCell();
  pcPushHistory();
  for (let r = 0; r < PC.grid.rows.length; r++) {
    const k = r + '_' + PC.sel.c;
    const v = PC.grid.cells[k] ? PC.grid.cells[k].v : '';
    PC.grid.cells[k] = Object.assign(pcNewCell(), { bold: src.bold, italic: src.italic, fs: src.fs, color: src.color, bg: src.bg, align: src.align, bd: src.bd, v: v });
  }
  pcRenderGrid();
  toast('已将样式套用到 ' + pcGridColName(PC.sel.c) + ' 列');
}

/* ============ 水印层（预览 / 打印共用） ============ */
function pcWatermarkLayerHtml(wm, main, ctx) {
  if (!wm || !wm.enabled) return '';
  const center = wm.mode === 'center';
  const count = center ? 1 : (wm.repeat === false ? 1 : 24);
  const rot = Number(wm.rotate) || 0;
  let items = '';
  if (wm.image) {
    const img = `<img src="${escapeHtml(wm.image)}" alt="" style="width:${Number(wm.imageWidth) || 200}px;transform:rotate(${rot}deg)">`;
    for (let i = 0; i < count; i++) items += img;
  } else {
    const text = escapeHtml(pcRenderCellValue(wm.text, main, ctx));
    for (let i = 0; i < count; i++) items += `<span style="transform:rotate(${rot}deg)">${text}</span>`;
  }
  return `<div class="pw-layer${center ? ' pw-center' : ''}" style="color:${wm.color || '#93c5fd'};opacity:${wm.opacity};--pw-size:${Number(wm.size) || 20}px">${items}</div>`;
}

/* ============ 矩形选区（按住鼠标拖动选择多格，用于合并 / 批量操作） ============ */
function pcStartSelect(e, r, c) {
  if (e && e.button !== 0) return;
  if (e && e.target && e.target.classList && e.target.classList.contains('pc-rz')) return;  // 拖拽手柄不参与选区
  if (!PC.grid) return;
  PC.dragging = true;
  PC.dragStart = { r: r, c: c };
  PC.range = { r1: r, c1: c, r2: r, c2: c };
}

document.addEventListener('mousemove', function (e) {
  if (!PC.grid) return;
  // 拖拽调宽高
  if (typeof PC_RZ !== 'undefined' && PC_RZ) { pcRzMove(e); return; }
  if (!PC.dragging) return;
  const el = document.elementFromPoint(e.clientX, e.clientY);
  const td = el && el.closest ? el.closest('.pc-cell') : null;
  if (!td) return;
  const r = Number(td.dataset.r), c = Number(td.dataset.c);
  if (PC.range && PC.range.r2 === r && PC.range.c2 === c) return;
  PC.range = { r1: PC.dragStart.r, c1: PC.dragStart.c, r2: r, c2: c };
  pcRenderGrid();
  pcRenderStatusBar();
});

document.addEventListener('mouseup', function () {
  if (PC.dragging) {
    PC.dragging = false;
    if (PC.range) {
      const single = PC.range.r1 === PC.range.r2 && PC.range.c1 === PC.range.c2;
      if (single) PC.range = null;
    }
    pcRenderStatusBar();
  }
  if (typeof PC_RZ !== 'undefined' && PC_RZ) {
    PC_RZ = null;
    document.body.classList.remove('pc-resizing');
    pcRenderStatusBar();
  }
});

/* 选中范围（归一化后的矩形） */
function pcRangeRect() {
  const rg = PC.range;
  if (!rg) return null;
  return {
    r1: Math.min(rg.r1, rg.r2), r2: Math.max(rg.r1, rg.r2),
    c1: Math.min(rg.c1, rg.c2), c2: Math.max(rg.c1, rg.c2),
  };
}

/* ============ 合并 / 取消合并单元格 ============ */
function pcMergeOverlap(m, r1, c1, r2, c2) {
  const mr2 = m.r + (m.rs || 1) - 1, mc2 = m.c + (m.cs || 1) - 1;
  return !(m.r > r2 || mr2 < r1 || m.c > c2 || mc2 < c1);
}

function pcMergeCells() {
  const rect = pcRangeRect();
  if (!rect) { toast('请按住鼠标拖动选择要合并的区域（至少 2 格）', 'error'); return; }
  if (rect.r1 === rect.r2 && rect.c1 === rect.c2) { toast('请选择至少 2 个单元格', 'error'); return; }
  pcPushHistory();
  PC.grid.merges = (PC.grid.merges || []).filter(m => !pcMergeOverlap(m, rect.r1, rect.c1, rect.r2, rect.c2));
  PC.grid.merges.push({ r: rect.r1, c: rect.c1, rs: rect.r2 - rect.r1 + 1, cs: rect.c2 - rect.c1 + 1 });
  // 保留左上角内容，清空区域内其它单元格
  for (let r = rect.r1; r <= rect.r2; r++) {
    for (let c = rect.c1; c <= rect.c2; c++) {
      if (r !== rect.r1 || c !== rect.c1) delete PC.grid.cells[r + '_' + c];
    }
  }
  PC.range = null;
  PC.selType = 'cell';
  pcRenderGrid();
  pcSelectCell(rect.r1, rect.c1);
  toast('已合并 ' + (rect.r2 - rect.r1 + 1) + ' × ' + (rect.c2 - rect.c1 + 1) + ' 单元格');
}

function pcUnmergeCells() {
  const m = PC.sel ? pcMergeAt(PC.sel.r, PC.sel.c) : null;
  if (!m) { toast('当前单元格不在合并区内', 'error'); return; }
  pcPushHistory();
  PC.grid.merges = (PC.grid.merges || []).filter(x => x !== m);
  pcRenderGrid();
  toast('已取消合并');
}

/* ============ 拖拽调整列宽 / 行高 ============ */
let PC_RZ = null;

function pcRzStart(e, kind, index) {
  if (e) { e.preventDefault(); e.stopPropagation(); }
  PC_RZ = {
    kind: kind, index: index,
    startX: e ? e.clientX : 0, startY: e ? e.clientY : 0,
    base: kind === 'col' ? (PC.grid.cols[index] || 110) : (PC.grid.rows[index] || 34),
  };
  document.body.classList.add('pc-resizing');
}

function pcRzMove(e) {
  if (!PC_RZ || !PC.grid) return;
  const d = PC_RZ.kind === 'col' ? (e.clientX - PC_RZ.startX) : (e.clientY - PC_RZ.startY);
  const min = PC_RZ.kind === 'col' ? 40 : 18;
  const max = PC_RZ.kind === 'col' ? 600 : 240;
  const val = Math.max(min, Math.min(max, PC_RZ.base + d));
  if (PC_RZ.kind === 'col') PC.grid.cols[PC_RZ.index] = Math.round(val);
  else PC.grid.rows[PC_RZ.index] = Math.round(val);
  pcRenderGrid();
}

/* ============ 明细行模板（支持整组多行循环：主行 + 子行成对展开） ============ */
function pcToggleDetailRow() {
  const g = PC.grid;
  g.detailRow = g.detailRow || { enabled: false, rows: [] };
  const rect = pcRangeRect();
  let rows = [];
  if (rect && rect.r2 > rect.r1) {
    for (let r = rect.r1; r <= rect.r2; r++) rows.push(r);      // 矩形选区跨多行 → 整组
  } else if (PC.sel) {
    rows = [PC.sel.r];                                          // 单行
  }
  if (!rows.length) { toast('请先点选一行（或拖选多行）', 'error'); return; }

  const cur = (Array.isArray(g.detailRow.rows) ? g.detailRow.rows.map(Number) : []);
  const same = g.detailRow.enabled && JSON.stringify(cur) === JSON.stringify(rows);
  if (same) {
    g.detailRow.enabled = false;
    toast('已取消明细行模板');
  } else {
    g.detailRow.enabled = true;
    g.detailRow.rows = rows;
    delete g.detailRow.row;
    toast('已把第 ' + rows.map(r => r + 1).join('、') + ' 行设为明细模板行组（打印时按明细条数成组循环）');
  }
  PC.range = null;
  pcRenderGrid();
  pcRenderDetailCfg();
}

/* ============ 水印设置 ============ */
function pcToggleWatermark(enabled) {
  PC.grid.watermark = PC.grid.watermark || {};
  PC.grid.watermark.enabled = !!enabled;
  pcRenderGrid();
  pcRenderDetailCfg();
}

function pcWatermarkChange(prop, value) {
  PC.grid.watermark = PC.grid.watermark || {};
  PC.grid.watermark[prop] = value;
  pcRenderGrid();
}

/* ============ 导出：Excel（后端 NPOI）/ PDF（浏览器打印） ============ */
async function pcExportExcel() {
  if (!PC.template || !PC.template.Id) { toast('请先保存模板后再导出', 'error'); return; }
  try {
    await pcSaveLayout();
    const resp = await fetch(`/api/sys/print-templates/${PC.template.Id}/export-excel`, {
      headers: { Authorization: 'Bearer ' + TOKEN },
    });
    if (!resp.ok) { toast('导出 Excel 失败（HTTP ' + resp.status + '）', 'error'); return; }
    const blob = await resp.blob();
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = (PC.template.TemplateName || PC.title) + '.xlsx';
    document.body.appendChild(a); a.click(); a.remove();
    URL.revokeObjectURL(a.href);
    toast('已导出 Excel 模板');
  } catch (e) { toast('导出失败：' + e.message, 'error'); }
}

function pcExportPdf() {
  if (!PC.grid) { toast('请先打开模板', 'error'); return; }
  pcShowPaper(buildGridPrintHtml({ LayoutJson: JSON.stringify(PC.grid) }, PC.code, pcSampleMain(), pcSampleDetails()), PC.title);
  toast('在打印窗口中把「目标打印机」选为「另存为 PDF」即可导出 PDF', 'info');
}

/* ============ 由 Excel 导入模板（后端 NPOI 解析） ============ */
function pcImportExcel() {
  if (!PC.code) { toast('请先在左侧选择单据类型', 'error'); return; }
  const modal = document.getElementById('modal');
  modal.innerHTML = `<div class="modal" style="width:620px">
    <h3>📥 由 Excel 导入打印模板</h3>
    <p class="text-muted" style="margin:10px 0 14px;line-height:1.8">
      上传用 Excel 排好版的 .xlsx 文件，系统会自动解析出：<b>文字内容、字体与字号、加粗/斜体、字体颜色、单元格底色、边框、对齐方式、列宽行高、合并单元格</b>，
      导入后可在设计器中继续编辑并绑定 ERP 字段。
    </p>
    <div class="form-item"><label>Excel 文件（.xlsx）</label><input type="file" id="pc-excel-file" accept=".xlsx"></div>
    <div class="form-item"><label>导入方式</label>
      <select id="pc-import-mode">
        <option value="new">新建模板并进入编辑器</option>
        <option value="overwrite">覆盖当前模板布局</option>
      </select>
    </div>
    <div class="modal-footer">
      <button class="btn btn-neutral" onclick="closeModal()">取消</button>
      <button class="btn btn-primary" onclick="pcDoImportExcel()">开始导入</button>
    </div>
  </div>`;
  modal.style.display = 'flex';
}

async function pcDoImportExcel() {
  const input = document.getElementById('pc-excel-file');
  if (!input || !input.files || !input.files[0]) { toast('请先选择 .xlsx 文件', 'error'); return; }
  const mode = (document.getElementById('pc-import-mode') || {}).value || 'new';
  const fd = new FormData();
  fd.append('file', input.files[0]);
  try {
    toast('正在解析 Excel，请稍候…');
    const resp = await fetch('/api/sys/print-templates/import-excel', {
      method: 'POST', headers: { Authorization: 'Bearer ' + TOKEN }, body: fd,
    });
    const json = await resp.json();
    if (json.code !== 0) { toast(json.message || '解析失败', 'error'); return; }
    const grid = pcParseLayout(JSON.stringify(json.data.layout));
    closeModal();
    if (mode === 'overwrite' && PC.template && PC.template.Id) {
      PC.grid = grid;
      pcRenderEditor();
      toast('已覆盖当前模板布局（记得点保存）');
    } else {
      const name = (PC.title + ' - Excel导入 ' + new Date().toISOString().slice(5, 10));
      const saved = await api('/api/sys/print-templates', 'POST', {
        Id: 0, BillType: PC.code, TemplateName: name, Title: PC.title,
        PaperSize: 'A4', FontSize: 12, FieldKeys: '', FooterText: '',
        ShowCompanyHeader: true, ShowDetailTable: true, ShowRemark: true, IsDefault: false,
        LayoutJson: JSON.stringify(grid),
      });
      toast('Excel 已解析为网格模板');
      if (saved && saved.id) pcOpenEditor(saved.id);
      else pcSelectDoc(PC.code);
    }
  } catch (e) { toast('导入失败：' + e.message, 'error'); }
}









