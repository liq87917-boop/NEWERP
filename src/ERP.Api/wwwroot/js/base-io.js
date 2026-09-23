/* ============ 基础资料 打印 / 导入 / 导出 / 操作日志 ============ */
/* 依赖：app.js（api/toast/TOKEN/escapeHtml/uploadFile）、crud.js（CURRENT_MODULE）、bill-print.js（打印模板/下载/导入结果） */

/* 当前基础资料对应的导入导出资源键（由接口地址末段推导，如 /api/base/customers -> customers） */
function baseResourceKey() {
  const apiPath = (CURRENT_MODULE && CURRENT_MODULE.api) || '';
  const segments = apiPath.split('/').filter(Boolean);
  return segments.length ? segments[segments.length - 1] : '';
}

/* 当前基础资料可打印的列（图片列不参与打印） */
function basePrintColumns() {
  return ((CURRENT_MODULE && CURRENT_MODULE.columns) || []).filter(c => c.type !== 'image');
}

/* 打印值格式化（复用单据打印的格式化规则） */
function formatBaseValue(column, value) {
  if (value === null || value === undefined || value === '') return '';
  if (column.type === 'money') return fmtMoney(value);
  if (column.type === 'date') return fmtDate(value);
  return String(value);
}

/* 生成基础资料打印 HTML（公司抬头 + 标题 + 数据表格 + 页脚） */
function buildBasePrintHtml(tpl, rows) {
  const columns = basePrintColumns();
  const head = columns.map(c => `<th>${escapeHtml(c.label)}</th>`).join('');
  const body = rows.map(r => `<tr>${columns.map(c => {
    const value = formatBaseValue(c, r[c.key]);
    return `<td class="${c.type === 'money' ? 'num' : ''}">${escapeHtml(value)}</td>`;
  }).join('')}</tr>`).join('');

  const company = tpl.ShowCompanyHeader === false ? '' : `<div class="print-company">${escapeHtml(tpl.CompanyName || '')}</div>
    ${(tpl.CompanyAddress || tpl.CompanyPhone) ? `<div class="print-company-sub">${escapeHtml(tpl.CompanyAddress || '')}${tpl.CompanyAddress && tpl.CompanyPhone ? '　' : ''}${tpl.CompanyPhone ? '电话：' + escapeHtml(tpl.CompanyPhone) : ''}</div>` : ''}`;

  return `<div class="print-page" style="--print-font:${tpl.FontSize || 12}px">
    ${company}
    <div class="print-title">${escapeHtml(tpl.Title || (CURRENT_MODULE && CURRENT_MODULE.title) || '')}</div>
    <div class="print-meta">
      <span>共 ${rows.length} 条记录</span>
      <span>打印时间：${new Date().toLocaleString('zh-CN')}</span>
    </div>
    <table class="print-details"><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table>
    <div class="print-footer"><span>${escapeHtml(tpl.FooterText || '')}</span><span>共 1 页</span></div>
  </div>`;
}

/* 打印预览（基础资料列表） */
async function previewModulePrint() {
  const rows = window.__moduleRows || [];
  if (!rows.length) { toast('当前列表暂无数据可打印', 'error'); return; }
  ensurePrintStyle();
  try {
    const tpl = await api(`/api/sys/print-templates/${encodeURIComponent(CURRENT_MODULE_CODE)}`);
    const html = buildBasePrintHtml(tpl || {}, rows);
    const modal = document.getElementById('modal');
    modal.innerHTML = `<div class="modal modal-lg" style="width:960px;max-width:96vw">
      <h3>🖨 打印预览 - ${escapeHtml((CURRENT_MODULE && CURRENT_MODULE.title) || '')}</h3>
      <div style="max-height:62vh;overflow:auto;border:1px solid #e2e8f0;border-radius:6px;background:#f8fafc">${html}</div>
      <div class="modal-footer">
        <button class="btn btn-neutral" onclick="closeModal()">关闭</button>
        <button class="btn btn-primary" onclick="printModule()">🖨 打印</button>
      </div>
    </div>`;
    modal.style.display = 'flex';
  } catch (err) { toast(err.message, 'error'); }
}


/* 打印（基础资料列表，新窗口） */
async function printModule() {
  const rows = window.__moduleRows || [];
  if (!rows.length) { toast('当前列表暂无数据可打印', 'error'); return; }
  const win = window.open('', '_blank');
  if (!win) { toast('浏览器拦截了打印窗口，请允许弹出窗口后重试', 'error'); return; }
  win.document.write('<div style="font-family:sans-serif;padding:20px">正在准备打印内容……</div>');
  try {
    const tpl = await api(`/api/sys/print-templates/${encodeURIComponent(CURRENT_MODULE_CODE)}`);
    const html = buildBasePrintHtml(tpl || {}, rows);
    win.document.open();
    win.document.write(`<!DOCTYPE html><html lang="zh-CN"><head><meta charset="UTF-8">
      <title>${escapeHtml((tpl && tpl.Title) || (CURRENT_MODULE && CURRENT_MODULE.title) || '打印')}</title>
      <style>${printPageCss(tpl && tpl.PaperSize)} ${PRINT_STYLE} body{margin:0;background:#fff}</style>
      </head><body>${html}</body></html>`);
    win.document.close();
    win.focus();
    setTimeout(() => { try { win.print(); } catch (e) { /* 用户取消打印 */ } }, 300);
  } catch (err) {
    win.document.close();
    toast(err.message, 'error');
  }
}


/* 导出基础资料 Excel（导出全部记录） */
async function exportBaseData() {
  const resource = baseResourceKey();
  const title = (CURRENT_MODULE && CURRENT_MODULE.title) || resource;
  try {
    const resp = await fetch(`/api/base/io/${resource}/export`, { headers: { Authorization: 'Bearer ' + TOKEN } });
    if (!resp.ok) { toast('导出失败', 'error'); return; }
    downloadBlob(await resp.blob(), `${title}_${new Date().toISOString().slice(0, 10).replace(/-/g, '')}.xlsx`);
    toast('导出成功');
  } catch (err) { toast('导出失败：' + err.message, 'error'); }
}

/* 下载基础资料导入模板 */
async function downloadBaseImportTemplate() {
  const resource = baseResourceKey();
  const title = (CURRENT_MODULE && CURRENT_MODULE.title) || resource;
  try {
    const resp = await fetch(`/api/base/io/${resource}/import-template`, { headers: { Authorization: 'Bearer ' + TOKEN } });
    if (!resp.ok) { toast('模板下载失败', 'error'); return; }
    downloadBlob(await resp.blob(), `${title}_导入模板_${new Date().toISOString().slice(0, 10).replace(/-/g, '')}.xlsx`);
    toast('模板已下载，请按表头填写后导入');
  } catch (err) { toast('模板下载失败：' + err.message, 'error'); }
}

/* 打开基础资料导入弹窗 */
function openBaseImportDialog() {
  const title = (CURRENT_MODULE && CURRENT_MODULE.title) || '';
  const modal = document.getElementById('modal');
  modal.innerHTML = `<div class="modal" style="width:560px">
    <h3>📥 导入 ${escapeHtml(title)}</h3>
    <p class="text-muted" style="margin:8px 0 14px;line-height:1.7">
      1. 先<button class="btn btn-neutral btn-sm" onclick="downloadBaseImportTemplate()">下载导入模板</button><br>
      2. 按模板中文表头逐行填写（编码列需唯一，重复编码的行会导入失败）<br>
      3. 选择填好的 Excel 文件后点击「开始导入」
    </p>
    <div class="form-item"><label>Excel 文件（.xlsx）</label><input type="file" id="base-import-file" accept=".xlsx"></div>
    <div class="modal-footer">
      <button class="btn btn-neutral" onclick="closeModal()">取消</button>
      <button class="btn btn-primary" onclick="doImportBaseExcel()">开始导入</button>
    </div>
  </div>`;
  modal.style.display = 'flex';
}

/* 执行基础资料导入并展示结果 */
async function doImportBaseExcel() {
  const input = document.getElementById('base-import-file');
  if (!input || !input.files || !input.files[0]) { toast('请先选择 Excel 文件', 'error'); return; }
  const resource = baseResourceKey();
  const fd = new FormData();
  fd.append('file', input.files[0]);
  try {
    toast('正在导入，请稍候……');
    const result = await uploadFile(`/api/base/io/${resource}/import`, fd);
    showImportResult(result);
    if (CURRENT_LOADER) CURRENT_LOADER();
  } catch (err) { toast(err.message, 'error'); }
}

/* 打印设计（基础资料）→ 跳转到「样式设计」页面并预选当前单据 */
function openModulePrintDesign() { gotoPrintDesign(CURRENT_MODULE_CODE); }

/* 查看当前基础资料的操作日志 */
async function showModuleLogs() {
  const apiPath = (CURRENT_MODULE && CURRENT_MODULE.api) || '';
  try {
    const data = await api(`/api/sys/logs?page=1&pageSize=100&keyword=${encodeURIComponent(apiPath)}`);
    const items = data.items || [];
    const rows = items.map(l => `<tr>
        <td>${escapeHtml(l.createdAt ? String(l.createdAt).replace('T', ' ').slice(0, 19) : '')}</td>
        <td>${escapeHtml(l.userName)}</td>
        <td>${escapeHtml(l.action)}</td>
        <td>${escapeHtml(l.ipAddress)}</td>
        <td>${escapeHtml(l.path)}</td>
      </tr>`).join('');
    document.getElementById('modal').innerHTML = `<div class="modal modal-lg" style="width:960px;max-width:96vw">
      <h3>📜 操作日志 - ${escapeHtml((CURRENT_MODULE && CURRENT_MODULE.title) || '')}（共 ${data.total} 条）</h3>
      <div class="table-wrap" style="max-height:440px;overflow:auto">
        ${items.length ? `<table><thead><tr><th>时间</th><th>操作人</th><th>动作</th><th>IP</th><th>路径</th></tr></thead><tbody>${rows}</tbody></table>`
      : '<div class="empty">暂无操作日志</div>'}
      </div>
      <div class="modal-footer"><button class="btn btn-neutral" onclick="closeModal()">关闭</button></div>
    </div>`;
    document.getElementById('modal').style.display = 'flex';
  } catch (err) { toast(err.message, 'error'); }
}
