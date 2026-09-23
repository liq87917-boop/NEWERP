function renderModule(mod) {
  CURRENT_MODULE = mod;
  __listRequestSeq++;                   // 作废在途的上一模块列表响应（含树形模块），避免旧数据覆盖新页面
  if (mod.tree) { renderTreeModule(mod); return; }
  CURRENT_LOADER = loadList;
  CURRENT_PAGE = 1;
  CURRENT_KEYWORD = '';
  // 仅基础资料支持 Excel 导入导出（单据资源在 /api/base/io 中登记）
  const isBaseData = (mod.api || '').startsWith('/api/base/');
  const ioButtons = isBaseData
    ? `<button class="btn btn-neutral" onclick="openBaseImportDialog()" title="从 Excel 导入">📥 导入</button>
       <button class="btn btn-neutral" onclick="exportBaseData()" title="导出为 Excel">📤 导出</button>`
    : '';
  const content = document.getElementById('content');
  content.innerHTML = `
    <div class="toolbar">
      <div class="toolbar-left">
        <input type="text" id="search-input" placeholder="搜索关键字..." onkeydown="if(event.key==='Enter')searchList()">
        <button class="btn btn-neutral" onclick="searchList()">搜索</button>
        <button class="btn btn-neutral btn-sm" onclick="showModuleLogs()" title="查看该模块操作日志">📜 操作日志</button>
      </div>
      <div class="toolbar-actions">
        ${ioButtons}
        ${(mod.extraActions || []).map(a => `<button class="btn btn-neutral" onclick="${a.onclick}" title="${a.title || ''}">${a.label}</button>`).join('')}
        <button class="btn btn-neutral" onclick="previewModulePrint()" title="打印预览">🖨 打印预览</button>
        <button class="btn btn-neutral" onclick="printModule()" title="直接打印">🖨 打印</button>
        <button class="btn btn-neutral" onclick="openModulePrintDesign()" title="设计打印模板">🎨 打印设计</button>
        ${mod.readonly ? '' : '<button class="btn btn-primary" onclick="openForm()">+ 新增</button>'}
      </div>
    </div>
    <div class="table-wrap" id="table-wrap"></div>
    <div class="pagination" id="pagination"></div>`;
  loadList();
}

async function exportProducts() {
  try {
    const resp = await fetch('/api/base/products/export', { headers: { Authorization: 'Bearer ' + TOKEN } });
    if (!resp.ok) { toast('导出失败', 'error'); return; }
    const blob = await resp.blob();
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = '商品资料.xlsx';
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
    toast('导出成功');
  } catch (err) { toast('导出失败：' + err.message, 'error'); }
}

function searchList() {
  CURRENT_KEYWORD = document.getElementById('search-input').value.trim();
  CURRENT_PAGE = 1;
  loadList();
}

/* 列表请求序号：切换模块 / 搜索 / 翻页 / 操作后刷新都会各自发起请求，响应不保证按发起顺序返回。
   旧响应若晚到，会把过期数据覆盖到列表上，并使页面里已渲染的行元素失效
   （真实浏览器验收中表现为 stale element reference）。因此只允许最后一次发出的请求渲染结果。 */
let __listRequestSeq = 0;

async function loadList() {
  const mod = CURRENT_MODULE;
  const qs = `page=${CURRENT_PAGE}&pageSize=${PAGE_SIZE}${CURRENT_KEYWORD ? '&keyword=' + encodeURIComponent(CURRENT_KEYWORD) : ''}`;
  const seq = ++__listRequestSeq;
  const data = await api(`${mod.api}?${qs}`);
  if (seq !== __listRequestSeq) return;     // 过期响应：已有更新的请求（新模块 / 新关键字 / 新页码）
  window.__moduleRows = data.items || [];   // 供打印预览使用当前页数据
  renderTable(mod, data);
  renderPagination(data);
}

function renderTable(mod, data) {
  closeRowMenu();                       // 重渲染前收起可能残留的行操作菜单
  const wrap = document.getElementById('table-wrap');
  if (!data.items || !data.items.length) { wrap.innerHTML = '<div class="empty">暂无数据</div>'; return; }
  const head = mod.columns.map(c => `<th>${c.label}</th>`).join('');
  const rows = data.items.map(row => {
    const tds = mod.columns.map(c => {
      const v = row[c.key];
      if (c.status) return `<td>${statusHtml(v)}</td>`;
      if (c.type === 'money') return `<td class="text-right">${fmtMoney(v)}</td>`;
      if (c.type === 'date') return `<td>${fmtDate(v)}</td>`;
      /* 枚举 / 布尔映射列：把后端枚举值（数字或布尔）渲染为业务文案，未知值原样显示 */
      if (c.type === 'map') {
        const hit = (c.options || []).find(o => String(o.value) === String(v));
        return `<td>${hit ? hit.label : (v ?? '')}</td>`;
      }
      if (c.type === 'image') return `<td>${v ? `<img class="thumb-img" src="${escapeHtml(v)}" onclick="showLightbox(this.src)">` : ''}</td>`;
      /* 派生列（列声明 render 回调时生效）：用于报价单「有效期状态」这类由整行数据计算的列 */
      if (typeof c.render === 'function') return `<td>${c.render(row) || ''}</td>`;
      return `<td>${v ?? ''}</td>`;
    }).join('');
    const subActions = (mod.canSubmit ? submitActions(row) : '') + customRowActions(mod, row);
    const actions = mod.readonly ? '' : `<td>
      <div class="row-actions">
        <button class="btn btn-neutral btn-sm" onclick="openForm(${row.id})">编辑</button>
        <button class="btn btn-neutral btn-sm row-more" onclick="toggleRowMenu(event, this)" title="更多操作">更多</button>
      </div>
      <div class="row-menu" hidden>
        ${subActions}${subActions ? '<div class="row-menu-sep"></div>' : ''}
        <button class="row-menu-item danger" onclick="removeRow(${row.id})"><span class="rmi-ico">🗑</span><span class="rmi-txt">删除</span></button>
      </div>
    </td>`;
    return `<tr>${tds}${actions}</tr>`;
  }).join('');
  wrap.innerHTML = `<table><thead><tr>${head}<th style="width:${mod.readonly ? '0' : '150px'}">操作</th></tr></thead><tbody>${rows}</tbody></table>`;
}

/* 行内「更多」菜单里的状态流转项（样式统一为 .row-menu-item） */
function submitActions(row) {
  let html = '';
  if (row.status === 'Pending' || row.status === 0) html += `<button class="row-menu-item" onclick="changeStatus(${row.id},'submit')"><span class="rmi-ico">✅</span><span class="rmi-txt">提交</span></button>`;
  if (row.status === 'Submitted' || row.status === 1) html += `<button class="row-menu-item" onclick="changeStatus(${row.id},'approve')"><span class="rmi-ico">🟠</span><span class="rmi-txt">审核</span></button>`;
  if (row.status !== 'Cancelled' && row.status !== 5) html += `<button class="row-menu-item" onclick="changeStatus(${row.id},'cancel')"><span class="rmi-ico">🚫</span><span class="rmi-txt">取消</span></button>`;
  return html;
}

/* 模块自定义行操作（模块声明 rowActions 时生效，与工具栏 extraActions 同一风格）
   配置：rowActions: [{ label, icon, title, onclick, statuses? }]
   - onclick 为全局函数名，渲染时生成 onclick="fn(行Id)"；函数内可用 CURRENT_MODULE_CODE 判断当前单据
   - statuses 可选：仅当行状态命中时显示（状态取接口返回的枚举名，如 'Approved'） */
function customRowActions(mod, row) {
  return (mod.rowActions || [])
    .filter(a => !a.statuses || a.statuses.some(s => String(s) === String(row.status)))
    .map(a => `<button class="row-menu-item" onclick="${a.onclick}(${row.id})" title="${a.title || ''}">` +
      `<span class="rmi-ico">${a.icon || '•'}</span><span class="rmi-txt">${a.label}</span></button>`)
    .join('');
}

function renderPagination(data) {
  const opts = [
    { v: '50', l: '50' }, { v: '100', l: '100' }, { v: '200', l: '200' },
    { v: '500', l: '500' }, { v: '1000', l: '1000' }, { v: 'all', l: '不限' }
  ];
  document.getElementById('pagination').innerHTML = `
    <span>共 ${data.total} 条</span>
    <span class="page-size">每页
      <select id="page-size-select" onchange="changePageSize(this.value)">
        ${opts.map(o => `<option value="${o.v}">${o.l}</option>`).join('')}
      </select> 条</span>
    <button ${data.page <= 1 ? 'disabled' : ''} onclick="gotoPage(${data.page - 1})">上一页</button>
    <span>第 ${data.page} / ${data.totalPages} 页</span>
    <button ${data.page >= data.totalPages ? 'disabled' : ''} onclick="gotoPage(${data.page + 1})">下一页</button>`;
  const sel = document.getElementById('page-size-select');
  if (sel) sel.value = PAGE_SIZE >= 100000 ? 'all' : String(PAGE_SIZE);
}

function gotoPage(p) { CURRENT_PAGE = p; if (CURRENT_LOADER) CURRENT_LOADER(); }

function changePageSize(v) {
  PAGE_SIZE = v === 'all' ? 100000 : Number(v);
  gotoPage(1);
}

function openForm(id) {
  const mod = CURRENT_MODULE;
  const isEdit = id !== undefined;
  document.getElementById('sp-title').textContent = `${isEdit ? '编辑' : '新增'} - ${mod.title}`;
  document.getElementById('sp-body').innerHTML =
    `<div class="form-grid">${mod.fields.map(f => fieldHtml(f, null)).join('')}</div>` + detailSectionHtml(mod);
  const saveBtn = document.getElementById('sp-save-btn');
  setBtnNormal(saveBtn);
  saveBtn.onclick = () => saveForm(isEdit ? id : null);
  openPanel();
  if (isEdit) loadIntoForm(id);
}

function fieldHtml(f, value) {
  const val = value === null || value === undefined
    ? (f.default ?? (f.type === 'date' ? new Date().toISOString().slice(0, 10) : ''))
    : value;
  if (f.type === 'select') {
    const opts = (f.options || []).map(o => `<option value="${o.value}" ${String(val) === String(o.value) ? 'selected' : ''}>${o.label}</option>`).join('');
    return `<div class="form-item"><label>${f.label}</label><select id="f_${f.key}">${opts}</select></div>`;
  }
  if (f.type === 'ref') return renderRefField(f, f.ref);
  if (f.type === 'textarea') return `<div class="form-item full"><label>${f.label}</label><textarea id="f_${f.key}" rows="3">${val}</textarea></div>`;
  if (f.type === 'image') return imageFieldHtml(f, val);
  if (f.type === 'image-batch') return `<div class="form-item full"><label>${f.label}</label><button type="button" class="btn btn-neutral" onclick="batchUploadImages()">📷 批量上传图片</button><span class="text-muted" style="margin-left:8px">多选图片后依次填入图片1/2/3</span></div>`;
  if (f.type === 'parent') return parentFieldHtml(f, val);
  const type = f.type === 'number' ? 'number' : (f.type === 'date' ? 'date' : 'text');
  return `<div class="form-item"><label>${f.label}</label><input type="${type}" id="f_${f.key}" value="${val}" step="0.01"></div>`;
}

async function loadIntoForm(id) {
  const mod = CURRENT_MODULE;
  const row = await api(`${mod.api}/${id}`);
  /* 主子表：回填明细（模块声明 detailFields 时生效） */
  if (mod.detailFields && mod.detailFields.length) {
    DETAIL_ROWS = (row[mod.detailKey || 'details'] || []).map(d => Object.assign({}, d));
    detailRender();
  }
  mod.fields.forEach(async f => {
    if (f.type === 'ref') {
      const rid = row[f.key];
      if (rid) {
        document.getElementById('f_' + f.key).value = rid;
        const ref = REF_APIS[f.ref];
        try {
          const d = await api(`${ref.api}/${rid}`);
          document.getElementById('f_' + f.key + '_search').value = d[ref.nameKey] || '';
        } catch (e) { /* 忽略名称查询失败 */ }
      }
      return;
    }
    if (f.type === 'image') {
      setImageValue(f.key, row[f.key] || '');
      return;
    }
    const el = document.getElementById('f_' + f.key);
    if (!el) return;
    let v = row[f.key];
    if (f.type === 'date') v = fmtDate(v);
    el.value = v ?? '';
  });
}

function closeModal() { document.getElementById('modal').style.display = 'none'; }

/* Parallax Side Panel 开关（基础资料新建/编辑） */
function openPanel() {
  document.getElementById('side-panel-mask').classList.add('active');
  document.getElementById('side-panel').classList.add('active');
}
function closePanel() {
  document.getElementById('side-panel-mask').classList.remove('active');
  document.getElementById('side-panel').classList.remove('active');
}

async function saveForm(id) {
  const mod = CURRENT_MODULE;
  const saveBtn = document.getElementById('sp-save-btn');
  const body = {};
  mod.fields.forEach(f => {
    const el = document.getElementById('f_' + f.key);
    if (!el) return;
    let v = el.value;
    if (f.type === 'number') v = v === '' ? 0 : Number(v);
    if (f.type === 'ref') v = v === '' ? null : Number(v);
    if (f.type === 'parent') v = v === '' ? null : Number(v);
    /* 日期字段：留空时提交 null（而非空串），避免服务端把 "" 反序列化为 DateTime? 时报错；
       有值时补时间部分，保持原有口径 */
    if (f.type === 'date') v = v ? v + 'T00:00:00' : null;
    /* 布尔字段（下拉选择 是/否）：统一转成真正的布尔值提交，避免服务端反序列化把 "true" 当字符串 */
    if (f.valueType === 'bool') v = (v === true || String(v).toLowerCase() === 'true');
    if (f.valueType === 'number' && v !== '') v = Number(v);
    body[f.key] = v;
  });
  /* 主子表：提交明细（模块声明 detailFields 时生效） */
  if (mod.detailFields && mod.detailFields.length) body[mod.detailKey || 'details'] = detailCollect();
  setBtnLoading(saveBtn);
  const start = Date.now();
  try {
    if (id) await api(`${mod.api}/${id}`, 'PUT', body);
    else await api(mod.api, 'POST', body);
    // 保证转圈至少转满 MIN_LOADING_MS（避免接口过快导致 loading 一闪而过）
    const loadingElapsed = Date.now() - start;
    if (loadingElapsed < MIN_LOADING_MS) await sleep(MIN_LOADING_MS - loadingElapsed);
    setBtnSuccess(saveBtn);
    await sleep(MIN_SUCCESS_MS);
    setBtnNormal(saveBtn);
    closePanel();
    toast('保存成功');
    if (CURRENT_LOADER) CURRENT_LOADER(); else loadList();
  } catch (err) {
    setBtnNormal(saveBtn);
    toast(err.message, 'error');
  }
}

/* ============ 保存按钮状态（loading / success） ============ */
/* ============ 保存按钮三态（loading / success / normal） ============
   加载态至少 350ms（避免闪屏）；成功态至少 450ms（够看清 ✓，不快不拖） */
const MIN_LOADING_MS = 350;
const MIN_SUCCESS_MS = 450;

function sleep(ms) { return new Promise(r => setTimeout(r, ms)); }

function setBtnLoading(btn) {
  if (!btn) return;
  btn.dataset.text = btn.textContent;
  btn.disabled = true;
  btn.classList.add('btn-loading');
  btn.innerHTML = '<span class="spinner"></span>';
}

function setBtnSuccess(btn) {
  if (!btn) return;
  btn.disabled = true;
  btn.classList.remove('btn-loading');
  btn.classList.add('btn-success-state');
  btn.innerHTML = '<span class="check">✓</span>';
}

function setBtnNormal(btn) {
  if (!btn) return;
  btn.disabled = false;
  btn.classList.remove('btn-loading', 'btn-success-state');
  btn.innerHTML = btn.dataset.text || '保存';
}

async function removeRow(id) {
  if (!confirm('确认删除该记录？')) return;
  try { await api(`${CURRENT_MODULE.api}/${id}`, 'DELETE'); toast('删除成功'); if (CURRENT_LOADER) CURRENT_LOADER(); else loadList(); }
  catch (err) { toast(err.message, 'error'); }
}

async function changeStatus(id, action) {
  try { await api(`${CURRENT_MODULE.api}/${id}/${action}`, 'POST'); toast('操作成功'); loadList(); }
  catch (err) { toast(err.message, 'error'); }
}

/* 销审（退回待提交）：仅已审核单据可用；库存单据（盘点 / 调拨 / 退货）销审会先冲销库存流水，
   因此必须二次确认，避免误操作把已入库/出库的库存还原。 */
async function unauditRow(id) {
  const mod = CURRENT_MODULE;
  if (!mod || !mod.api) return;
  if (!confirm('确认销审该单据？已产生的库存流水将被冲销（库存同步还原）。')) return;
  try {
    await api(`${mod.api}/${id}/unaudit`, 'POST');
    toast('已销审');
    if (CURRENT_LOADER) CURRENT_LOADER(); else loadList();
  } catch (err) { toast(err.message, 'error'); }
}

/* ============ 图片字段渲染与上传/预览/下载 ============ */
function imageFieldHtml(f, value) {
  const v = value || '';
  const thumb = v;
  return `<div class="form-item full">
    <label>${f.label}</label>
    <div class="img-field">
      <input type="hidden" id="f_${f.key}" value="${v}">
      <div class="img-preview-wrap">
        <img id="f_${f.key}_thumb" src="${thumb}" ${v ? '' : 'style="display:none"'} onclick="previewImage('${f.key}')" title="点击预览原图">
      </div>
      <div class="img-actions">
        <button type="button" class="btn btn-neutral btn-sm" onclick="triggerImageUpload('${f.key}')">上传</button>
        <button type="button" class="btn btn-neutral btn-sm" onclick="previewImage('${f.key}')">预览</button>
        <button type="button" class="btn btn-neutral btn-sm" onclick="downloadImage('${f.key}')">下载</button>
        <button type="button" class="btn btn-danger btn-sm" onclick="clearImage('${f.key}')">删除</button>
        <input type="file" id="f_${f.key}_file" accept="image/*" style="display:none" onchange="uploadImage(this, '${f.key}')">
      </div>
    </div>
  </div>`;
}

function triggerImageUpload(key) { document.getElementById('f_' + key + '_file').click(); }

function setImageValue(key, url) {
  const hidden = document.getElementById('f_' + key);
  const thumb = document.getElementById('f_' + key + '_thumb');
  if (hidden) hidden.value = url;
  if (thumb) { thumb.src = url || ''; thumb.style.display = url ? '' : 'none'; }
}

async function uploadImage(input, key) {
  const file = input.files && input.files[0];
  if (!file) return;
  const fd = new FormData();
  fd.append('file', file);
  try {
    const data = await uploadFile('/api/base/products/upload', fd);
    setImageValue(key, data.url);
    toast('上传成功');
  } catch (e) { toast(e.message, 'error'); }
  input.value = '';
}

function previewImage(key) {
  const url = document.getElementById('f_' + key).value;
  if (!url) { toast('暂无图片', 'error'); return; }
  showLightbox(url);
}

/* 灯箱：页面内展示原图（不下载） */
function showLightbox(url) {
  const modal = document.getElementById('modal');
  modal.innerHTML = `
    <div class="modal modal-lg lightbox-modal">
      <div class="lightbox-head"><span>图片预览</span><button type="button" class="btn btn-neutral btn-sm" onclick="closeModal()">关闭</button></div>
      <div class="lightbox-body"><img src="${escapeHtml(url)}" alt="原图"></div>
    </div>`;
  modal.style.display = 'flex';
}

function downloadImage(key) {
  const url = document.getElementById('f_' + key).value;
  if (!url) { toast('暂无图片', 'error'); return; }
  const a = document.createElement('a');
  a.href = url; a.download = ''; a.target = '_blank';
  document.body.appendChild(a); a.click(); a.remove();
}

function clearImage(key) {
  const hidden = document.getElementById('f_' + key);
  const thumb = document.getElementById('f_' + key + '_thumb');
  if (hidden) hidden.value = '';
  if (thumb) { thumb.style.display = 'none'; thumb.src = ''; }
}

function batchUploadImages() {
  const input = document.createElement('input');
  input.type = 'file'; input.accept = 'image/*'; input.multiple = true;
  input.onchange = async () => {
    const files = Array.from(input.files || []);
    if (!files.length) return;
    const fd = new FormData();
    files.forEach(f => fd.append('files', f));
    try {
      const data = await uploadFile('/api/base/products/upload-batch', fd);
      const urls = data.urls || [];
      ['image1', 'image2', 'image3'].forEach((key, i) => {
        if (i < urls.length) setImageValue(key, urls[i]);
      });
      toast(files.length > 3 ? `已上传 ${urls.length} 张，仅前 3 张填入图片字段` : '批量上传成功');
    } catch (e) { toast(e.message, 'error'); }
  };
  input.click();
}

/* ============ 树形模块（费用科目多级分组） ============ */
let EXPENSE_ITEMS = [];
const EXPANDED = new Set();

function renderTreeModule(mod) {
  CURRENT_MODULE = mod;
  CURRENT_LOADER = loadExpenseTree;
  const content = document.getElementById('content');
  content.innerHTML = `
    <div class="toolbar">
      <div class="toolbar-left">
        <input type="text" id="search-input" placeholder="搜索科目编码/名称..." onkeydown="if(event.key==='Enter')loadExpenseTree()">
        <button class="btn btn-neutral" onclick="loadExpenseTree()">搜索</button>
        <button class="btn btn-neutral btn-sm" onclick="showModuleLogs()" title="查看该模块操作日志">📜 操作日志</button>
      </div>
      <div class="toolbar-actions">
        <button class="btn btn-neutral" onclick="openBaseImportDialog()" title="从 Excel 导入">📥 导入</button>
        <button class="btn btn-neutral" onclick="exportBaseData()" title="导出为 Excel">📤 导出</button>
        <button class="btn btn-neutral" onclick="previewModulePrint()" title="打印预览">🖨 打印预览</button>
        <button class="btn btn-neutral" onclick="printModule()" title="直接打印">🖨 打印</button>
        <button class="btn btn-neutral" onclick="openModulePrintDesign()" title="设计打印模板">🎨 打印设计</button>
        <button class="btn btn-primary" onclick="openForm()">+ 新增一级科目</button>
      </div>
    </div>
    <div class="table-wrap" id="table-wrap"></div>`;
  loadExpenseTree();
}

async function loadExpenseTree() {
  const data = await api(`${CURRENT_MODULE.api}/all`);
  EXPENSE_ITEMS = data || [];
  window.__moduleRows = EXPENSE_ITEMS;
  const kw = (document.getElementById('search-input')?.value || '').trim().toLowerCase();
  let filtered = EXPENSE_ITEMS;
  if (kw) {
    // 保留匹配项及其祖先链，避免子节点悬空
    const matchIds = new Set();
    EXPENSE_ITEMS.forEach(x => {
      if ((x.accountCode || '').toLowerCase().includes(kw) || (x.accountName || '').toLowerCase().includes(kw)) {
        matchIds.add(x.id);
        let p = x.parentId;
        while (p) {
          matchIds.add(p);
          p = EXPENSE_ITEMS.find(i => i.id === p)?.parentId;
        }
      }
    });
    filtered = EXPENSE_ITEMS.filter(x => matchIds.has(x.id));
  }
  // 默认展开所有有子级的节点，保证层级结构完整可见
  filtered.forEach(x => { if (filtered.some(c => c.parentId === x.id)) EXPANDED.add(x.id); });
  renderExpenseTable(filtered);
}

function buildTree(items, parentId = null) {
  return items
    .filter(x => (x.parentId ?? null) === parentId)
    .map(x => ({ ...x, children: buildTree(items, x.id) }));
}

function renderExpenseTable(items) {
  closeRowMenu();                       // 重渲染前收起可能残留的行操作菜单
  const wrap = document.getElementById('table-wrap');
  if (!items.length) { wrap.innerHTML = '<div class="empty">暂无数据</div>'; return; }
  const tree = buildTree(items);
  const head = `<table class="tree-table"><thead><tr>
    <th>科目编码</th><th>科目名称</th><th>类型</th><th>说明</th><th style="width:150px">操作</th>
  </tr></thead><tbody>`;
  const rows = tree.map(n => renderTreeNode(n, 0)).join('');
  wrap.innerHTML = `${head}${rows}</tbody></table>`;
}

function renderTreeNode(node, depth) {
  const hasChildren = node.children && node.children.length;
  const isExpanded = EXPANDED.has(node.id);
  const indent = depth * 24;
  const typeLabel = node.accountType === 1
    ? '<span class="status status-success">收入</span>'
    : '<span class="status status-info">支出</span>';
  const parentCls = hasChildren ? ' tree-parent' : '';
  const nameCls = hasChildren ? 'tree-title-parent' : '';
  const arrow = hasChildren
    ? `<span class="tree-arrow ${isExpanded ? 'open' : ''}" onclick="toggleTreeNode(${node.id})"><svg viewBox="0 0 16 16" fill="none"><path d="M6 4l4 4-4 4" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg></span>`
    : '<span class="tree-arrow-spacer"></span>';
  let html = `<tr class="tree-row${parentCls}" data-id="${node.id}">
    <td style="padding-left:${16 + indent}px">
      ${arrow}
      <span class="tree-code">${escapeHtml(node.accountCode)}</span>
    </td>
    <td><span class="tree-title ${nameCls}">${escapeHtml(node.accountName)}</span></td>
    <td>${typeLabel}</td>
    <td class="tree-desc">${escapeHtml(node.description)}</td>
    <td>
      <div class="row-actions">
        <button class="btn btn-neutral btn-sm" onclick="openForm(${node.id})">编辑</button>
        <button class="btn btn-neutral btn-sm row-more" onclick="toggleRowMenu(event, this)" title="更多操作">更多</button>
      </div>
      <div class="row-menu" hidden>
        <button class="row-menu-item" onclick="openChildForm(${node.id})"><span class="rmi-ico">➕</span><span class="rmi-txt">添加子级</span></button>
        <div class="row-menu-sep"></div>
        <button class="row-menu-item danger" onclick="removeRow(${node.id})"><span class="rmi-ico">🗑</span><span class="rmi-txt">删除</span></button>
      </div>
    </td>
  </tr>`;
  if (hasChildren && isExpanded) {
    html += node.children.map(c => renderTreeNode(c, depth + 1)).join('');
  }
  return html;
}

function toggleTreeNode(id) {
  if (EXPANDED.has(id)) EXPANDED.delete(id); else EXPANDED.add(id);
  const kw = (document.getElementById('search-input')?.value || '').trim().toLowerCase();
  if (kw) { loadExpenseTree(); return; }
  renderExpenseTable(EXPENSE_ITEMS);
}

function openChildForm(parentId) {
  openForm(undefined);
  const el = document.getElementById('f_parentId');
  if (el) el.value = String(parentId);
}

function parentFieldHtml(f, value) {
  const opts = buildParentOptions(EXPENSE_ITEMS);
  return `<div class="form-item full"><label>${f.label}</label>
    <select id="f_${f.key}">
      <option value="">（无，作为一级科目）</option>
      ${opts.map(o => `<option value="${o.id}" ${String(value ?? '') === String(o.id) ? 'selected' : ''}>${escapeHtml(o.label)}</option>`).join('')}
    </select></div>`;
}

function buildParentOptions(items, parentId = null, depth = 0) {
  const result = [];
  items.filter(x => (x.parentId ?? null) === parentId)
    .forEach(x => {
      const indent = depth > 0 ? '　'.repeat(depth) + '└ ' : '';
      result.push({ id: x.id, label: indent + x.accountName });
      result.push(...buildParentOptions(items, x.id, depth + 1));
    });
  return result;
}

/* ============================================================
   ============ 主子表：通用明细编辑（crud.js 扩展） ============
   模块声明（可选）：
     detailKey    : 明细集合键，默认 'details'
     detailTitle  : 明细区标题，默认 '明细'
     detailFields : [{ key, label, type: 'number'|'text'|'select', options, width, readonly }]
     detailAmount : { qty: 'quantity', price: 'unitPrice', amount: 'amount', totalId: 'detail-total' }
   未声明 detailFields 的模块完全不受影响（保持原有单表行为）
   ============================================================ */
let DETAIL_ROWS = [];

function detailSectionHtml(mod) {
  if (!mod.detailFields || !mod.detailFields.length) return '';
  const heads = mod.detailFields.map(f => `<th${f.width ? ` style="width:${f.width}"` : ''}>${f.label}</th>`).join('');
  /* 合计行：普通主子表显示「数量 × 单价」合计；盘点单（detailDiff）显示差异金额合计 */
  const totalId = (mod.detailDiff && mod.detailDiff.totalId) || (mod.detailAmount && mod.detailAmount.totalId) || 'detail-total';
  const hasTotal = (mod.detailAmount && mod.detailAmount.amount) || (mod.detailDiff && mod.detailDiff.amount);
  const totalLabel = mod.detailDiff ? '差异合计：' : '合计：';
  const amountCol = hasTotal
    ? `<div style="margin-top:8px;text-align:right;font-size:14px">${totalLabel}<b id="${totalId}">0.00</b></div>` : '';
  return `<div style="margin-top:16px">
    <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:6px">
      <b style="font-size:14px">${mod.detailTitle || '明细'}</b>
      <button type="button" class="btn btn-neutral" onclick="detailAddRow()">＋ 加一行</button>
    </div>
    <div class="table-wrap" style="max-height:320px;overflow:auto">
      <table class="data-table"><thead><tr>${heads}<th style="width:60px">操作</th></tr></thead>
      <tbody id="detail-body"></tbody></table>
    </div>${amountCol}</div>`;
}

function detailRender() {
  const mod = CURRENT_MODULE;
  const tbody = document.getElementById('detail-body');
  if (!tbody) return;
  tbody.innerHTML = DETAIL_ROWS.map((row, i) => {
    const tds = (mod.detailFields || []).map(f => {
      const cell = detailCellHtml(f, row, i);
      return `<td>${cell}</td>`;
    }).join('');
    return `<tr>${tds}<td><button type="button" class="btn btn-neutral" style="padding:2px 8px" onclick="detailRemoveRow(${i})">✕</button></td></tr>`;
  }).join('') || `<tr><td colspan="${(mod.detailFields || []).length + 1}" style="text-align:center;color:#999">暂无明细，点「＋ 加一行」添加</td></tr>`;
  detailRecalc();
}

function detailCellHtml(f, row, index) {
  const v = row[f.key] ?? '';
  const id = `d_${index}_${f.key}`;
  const on = `onchange="detailSet(${index}, '${f.key}', this.value)"`;
  if (f.type === 'select') {
    const opts = (f.options || []).map(o => `<option value="${o.value}" ${String(v) === String(o.value) ? 'selected' : ''}>${o.label}</option>`).join('');
    return `<select id="${id}" ${on} style="width:100%">${opts}</select>`;
  }
  const type = f.type === 'number' ? 'number' : 'text';
  const step = f.type === 'number' ? ' step="0.0001"' : '';
  const ro = f.readonly ? ' readonly style="background:#f7f7f7;width:100%"' : ' style="width:100%"';
  return `<input id="${id}" type="${type}"${step} value="${v}" ${on}${ro}>`;
}

function detailAddRow() {
  const mod = CURRENT_MODULE;
  const row = {};
  (mod.detailFields || []).forEach(f => { row[f.key] = f.type === 'number' ? 0 : ''; });
  DETAIL_ROWS.push(row);
  detailRender();
}

function detailRemoveRow(index) {
  DETAIL_ROWS.splice(index, 1);
  detailRender();
}

function detailSet(index, key, value) {
  const mod = CURRENT_MODULE;
  const f = (mod.detailFields || []).find(x => x.key === key);
  DETAIL_ROWS[index][key] = (f && f.type === 'number') ? (value === '' ? 0 : Number(value)) : value;
  if (mod.detailAmount && (key === mod.detailAmount.qty || key === mod.detailAmount.price)) {
    const a = mod.detailAmount;
    const row = DETAIL_ROWS[index];
    const amt = Number(row[a.qty] || 0) * Number(row[a.price] || 0);
    row[a.amount] = Math.round(amt * 10000) / 10000;
    const el = document.getElementById(`d_${index}_${a.amount}`);
    if (el) el.value = row[a.amount];
    detailRecalc();
  }
  /* 盘点差异（detailDiff）：差异 = 实盘 - 账面，差异金额 = 差异 × 成本单价（后端审核时会再复核一次） */
  if (mod.detailDiff && (key === mod.detailDiff.from || key === mod.detailDiff.to || key === mod.detailDiff.unitCost)) {
    const a = mod.detailDiff;
    const row = DETAIL_ROWS[index];
    const diff = Number(row[a.to] || 0) - Number(row[a.from] || 0);
    row[a.diff] = Math.round(diff * 10000) / 10000;
    const amt = Math.round(row[a.diff] * Number(row[a.unitCost] || 0) * 10000) / 10000;
    row[a.amount] = amt;
    const diffEl = document.getElementById(`d_${index}_${a.diff}`);
    if (diffEl) diffEl.value = row[a.diff];
    const amountEl = document.getElementById(`d_${index}_${a.amount}`);
    if (amountEl) amountEl.value = row[a.amount];
    detailRecalc();
  }
}

function detailRecalc() {
  const mod = CURRENT_MODULE;
  const a = mod.detailAmount && mod.detailAmount.amount ? mod.detailAmount : (mod.detailDiff || null);
  if (!a || !a.amount) return;
  const total = DETAIL_ROWS.reduce((s, r) => s + Number(r[a.amount] || 0), 0);
  const el = document.getElementById(a.totalId || 'detail-total');
  if (el) el.textContent = total.toFixed(2);
}

function detailCollect() {
  const mod = CURRENT_MODULE;
  // 过滤掉整行空白的行（未录入内容）
  return DETAIL_ROWS.filter(r => (mod.detailFields || []).some(f => {
    const v = r[f.key];
    return f.type === 'number' ? Number(v || 0) !== 0 : String(v ?? '').trim() !== '';
  }));
}
