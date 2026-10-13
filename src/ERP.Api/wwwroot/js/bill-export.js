/* ============ 单据导出模块（询价单/销售订单/采购订单导出） ============ */

/* 导出菜单编码 -> 单据类型（BILL_CODE_MAP 映射）
   ERP-008：销售订单 / 采购订单改用 EF 主子表接口的 Excel 导出（api），
   使导出的列包含外贸合同、来源追溯与采购执行进度字段；其余仍走存储过程版单据导出。
   ERP-469：每个导出都提供「搜索 / 预览」入口（previewApi）——预览与下载使用**同一组**筛选参数，
   预览只调用既有已授权接口（存储过程族：{billType}/export-preview；EF 族：既有 JSON 导出接口），
   绝不新增权限 / 数据范围旁路。 */
const EXPORT_MENU_MAP = {
  'inquiry-export': {
    billType: 'inquiry', title: '询价单导出', dateField: 'InquiryDate',
    previewApi: cfg => `/api/v2/bills/${cfg.billType}/export-preview`,
    previewClientFilter: false,
  },
  'sales-order-export': {
    api: '/api/sales-orders/export-excel', title: '销售订单导出',
    previewApi: () => '/api/sales-orders/export',
    previewClientFilter: true,
    previewStatusField: 'status',
    previewKeywordFields: ['orderNo', 'customerPoNo', 'contractNo'],
    statusOptions: [
      { value: '', label: '全部状态' }, { value: 'Pending', label: '待提交' },
      { value: 'Submitted', label: '已提交' }, { value: 'Approved', label: '已审核' },
      { value: 'Cancelled', label: '已取消' },
    ],
  },
  'purchase-order-export': {
    api: '/api/purchase-orders/export-excel', title: '采购订单导出',
    previewApi: () => '/api/purchase-orders/export',
    previewClientFilter: true,
    previewStatusField: 'status',
    previewKeywordFields: ['orderNo', 'contractNo'],
    statusOptions: [
      { value: '', label: '全部状态' }, { value: 'Pending', label: '待提交' },
      { value: 'Submitted', label: '已提交' }, { value: 'Approved', label: '已审核' },
      { value: 'Cancelled', label: '已取消' },
    ],
  },
};

/* 存储过程单据导出菜单的状态选项（1=保存 / 2=已审核 / -1=已作废） */
const LEGACY_EXPORT_STATUS_OPTS = [
  { value: '', label: '全部状态' }, { value: '1', label: '保存' },
  { value: '2', label: '已审核' }, { value: '-1', label: '已作废' },
];

/* 当前导出菜单编码 */
let EXPORT_CURRENT_CODE = '';

/* 最近一次「搜索 / 预览」的筛选签名（ERP-469：导出前必须先预览同一组条件） */
let EXPORT_PREVIEW_STATE = null;

/* ============ ERP-469：筛选参数 / 预览（纯函数，离线可单测） ============ */

/* 归一化筛选参数：导出与预览共用同一对象，保证口径完全一致 */
function billExportParams(filters) {
  const f = filters || {};
  return {
    start: f.start ? String(f.start) : '',
    end: f.end ? String(f.end) : '',
    status: (f.status === undefined || f.status === null) ? '' : String(f.status),
    keyword: (f.keyword === undefined || f.keyword === null) ? '' : String(f.keyword).trim(),
  };
}

/* 参数 → 查询串（纯函数）：dateField 只对存储过程族生效（与既有导出口径一致） */
function billExportQuery(params, dateField) {
  const qs = new URLSearchParams();
  if (dateField) qs.set('dateField', dateField);
  if (params.start) qs.set('start', params.start + 'T00:00:00');
  if (params.end) qs.set('end', params.end + 'T23:59:59');
  if (params.status) qs.set('status', params.status);
  if (params.keyword) qs.set('keyword', params.keyword);
  return qs.toString();
}

/* 下载地址（纯函数）：与预览共用同一 billExportParams →「下载文件使用同一组授权筛选参数」 */
function billExportDownloadUrl(code, params) {
  const cfg = EXPORT_MENU_MAP[code];
  if (!cfg) return '';
  if (cfg.api) return `${cfg.api}?${billExportQuery(params, '')}`;
  return `/api/v2/bills/${cfg.billType}/export?${billExportQuery(params, cfg.dateField)}`;
}

/* 预览地址（纯函数）：存储过程族把全部筛选条件交给服务端（与下载同参）；
   EF 族复用既有 JSON 导出接口（日期由服务端过滤，状态 / 关键字按同一参数在前端等价过滤）。 */
function billExportPreviewUrl(code, params) {
  const cfg = EXPORT_MENU_MAP[code];
  if (!cfg) return '';
  if (cfg.api) {
    const qs = new URLSearchParams();
    if (params.start) qs.set('start', params.start + 'T00:00:00');
    if (params.end) qs.set('end', params.end + 'T23:59:59');
    return `${cfg.previewApi(cfg)}?${qs.toString()}`;
  }
  return `${cfg.previewApi(cfg)}?${billExportQuery(params, cfg.dateField)}`;
}

/* 预览筛选签名（纯函数）：条件变化即失效，导出前必须重新搜索 / 预览 */
function billExportSignature(params) {
  return [params.start, params.end, params.status, params.keyword].join('\u0001');
}

/* 预览是否与当前筛选条件一致（纯函数） */
function billExportPreviewMatches(previewState, params) {
  return !!(previewState && previewState.signature === billExportSignature(params));
}

/* EF 族预览行等价过滤（纯函数）：与既有导出接口同一组关键字 / 状态口径 */
function billPreviewFilterRows(rows, params, cfg) {
  const list = Array.isArray(rows) ? rows : [];
  const keywords = (cfg && cfg.previewKeywordFields) || [];
  const statusField = cfg && cfg.previewStatusField;
  const keyword = params.keyword ? params.keyword.toLowerCase() : '';
  return list.filter(row => {
    if (!row) return false;
    if (params.status && statusField && String(row[statusField]) !== params.status) return false;
    if (!keyword) return true;
    return keywords.some(key => String(row[key] === undefined || row[key] === null ? '' : row[key])
      .toLowerCase().includes(keyword));
  });
}

/* 预览摘要文案（纯函数）：命中条数 / 展示行数显式可见 */
function billPreviewSummaryText(preview) {
  if (!preview) return '';
  const shown = (preview.items || []).length;
  return `搜索 / 预览：命中 ${preview.total} 条，展示前 ${shown} 条；导出的 Excel 将使用同一组筛选条件。`;
}

function renderBillExport(code) {
  const cfg = EXPORT_MENU_MAP[code];
  if (!cfg) { document.getElementById('content').innerHTML = '<div class="card empty">该导出未配置</div>'; return; }
  EXPORT_CURRENT_CODE = code;
  EXPORT_PREVIEW_STATE = null;

  const defStart = new Date(Date.now() - 30 * 86400000).toISOString().slice(0, 10);
  const defEnd = new Date().toISOString().slice(0, 10);
  document.getElementById('content').innerHTML = `
    <div class="card">
      <div class="card-title">📤 ${cfg.title}</div>
      <p class="text-muted" style="margin:8px 0 16px">选择日期范围与单据状态后，先点「🔍 搜索 / 预览」确认命中结果，再导出为 Excel 文件。单据号支持模糊搜索。</p>
      <div class="form-grid">
        <div class="form-item">
          <label>开始日期</label>
          <input type="date" id="ex-start" value="${defStart}" onchange="billExportPreviewInvalidate()">
        </div>
        <div class="form-item">
          <label>结束日期</label>
          <input type="date" id="ex-end" value="${defEnd}" onchange="billExportPreviewInvalidate()">
        </div>
        <div class="form-item">
          <label>单据状态</label>
          <select id="ex-status" onchange="billExportPreviewInvalidate()">
            ${(cfg.statusOptions || LEGACY_EXPORT_STATUS_OPTS)
              .map(o => `<option value="${o.value}">${o.label}</option>`).join('')}
          </select>
        </div>
        <div class="form-item">
          <label>单据号关键字</label>
          <input type="text" id="ex-keyword" placeholder="按单据号模糊搜索"
                 oninput="billExportPreviewInvalidate()" onkeydown="if(event.key==='Enter')previewExport()">
        </div>
      </div>
      <div style="margin-top:16px;display:flex;gap:10px">
        <button class="btn btn-neutral" onclick="previewExport()" title="按当前筛选条件搜索并在下载前预览">🔍 搜索 / 预览</button>
        <button class="btn btn-primary" onclick="doExport()">📥 导出 Excel</button>
      </div>
      <div id="ex-preview" style="margin-top:16px"></div>
    </div>`;
}

/* 当前表单上的筛选参数（导出与预览共用同一读取口径） */
function billExportCurrentParams() {
  return billExportParams({
    start: document.getElementById('ex-start').value,
    end: document.getElementById('ex-end').value,
    status: document.getElementById('ex-status').value,
    keyword: document.getElementById('ex-keyword').value,
  });
}

/* 条件变更即让上一次预览失效（导出前必须重新预览） */
function billExportPreviewInvalidate() {
  EXPORT_PREVIEW_STATE = null;
  const panel = document.getElementById('ex-preview');
  if (panel) panel.innerHTML = '<div class="text-muted">筛选条件已变更：请重新点「🔍 搜索 / 预览」。</div>';
}

function renderExportPreview(preview) {
  const panel = document.getElementById('ex-preview');
  if (!panel) return;
  if (!preview) { panel.innerHTML = ''; return; }

  const columns = Array.isArray(preview.columns) && preview.columns.length
    ? preview.columns.map(c => c.title || c.key)
    : (preview.items && preview.items.length ? Object.keys(preview.items[0]).slice(0, 8) : []);

  const head = columns.map(c => `<th>${escapeHtml(String(c))}</th>`).join('');
  const body = (preview.items || []).map(row =>
    `<tr>${columns.map(c => `<td>${escapeHtml(String(row[c] ?? ''))}</td>`).join('')}</tr>`
  ).join('');

  panel.innerHTML = `
    <div class="card-title" style="font-size:14px">🔍 预览结果</div>
    <p class="text-muted" style="margin:6px 0 10px">${billPreviewSummaryText(preview)}</p>
    ${body
      ? `<div class="table-wrap" style="max-height:320px;overflow:auto"><table><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table></div>`
      : '<div class="empty">当前筛选条件下没有命中的单据</div>'}`;
}

async function previewExport() {
  const cfg = EXPORT_MENU_MAP[EXPORT_CURRENT_CODE];
  if (!cfg) return;
  const params = billExportCurrentParams();
  const button = (typeof event !== 'undefined' && event) ? event.target : null;
  try {
    if (button) { button.disabled = true; button.textContent = '搜索中…'; }
    const url = billExportPreviewUrl(EXPORT_CURRENT_CODE, params);
    const data = await api(url);

    let preview;
    if (cfg.api) {
      const items = billPreviewFilterRows(data, params, cfg);
      preview = { total: items.length, items: items.slice(0, 20), columns: null };
    } else {
      preview = {
        total: Number(data && data.total) || 0,
        items: (data && data.items) || [],
        columns: (data && data.columns) || null,
      };
    }

    EXPORT_PREVIEW_STATE = { signature: billExportSignature(params), total: preview.total };
    renderExportPreview(preview);
    toast(`搜索完成：命中 ${preview.total} 条`);
  } catch (err) {
    EXPORT_PREVIEW_STATE = null;
    renderExportPreview(null);
    toast('搜索 / 预览失败：' + ((err && err.message) ? err.message : '请求失败'), 'error');
  } finally {
    if (button) { button.disabled = false; button.textContent = '🔍 搜索 / 预览'; }
  }
}

async function doExport() {
  const cfg = EXPORT_MENU_MAP[EXPORT_CURRENT_CODE];
  if (!cfg) return;
  const params = billExportCurrentParams();

  /* ERP-469：下载前必须先按同一组条件「搜索 / 预览」，避免带着未确认的筛选条件直接下载 */
  if (!billExportPreviewMatches(EXPORT_PREVIEW_STATE, params)) {
    toast('请先点击「🔍 搜索 / 预览」确认筛选结果，再导出 Excel（下载将使用同一组条件）', 'error');
    return;
  }

  const url = billExportDownloadUrl(EXPORT_CURRENT_CODE, params);
  try {
    const resp = await fetch(url, { headers: { Authorization: 'Bearer ' + TOKEN } });
    if (!resp.ok) {
      let msg = '导出失败';
      try { msg = (await resp.json()).message || msg; } catch (e) { /* 忽略解析失败 */ }
      toast(msg, 'error');
      return;
    }
    const blob = await resp.blob();
    const objectUrl = URL.createObjectURL(blob);
    const a = document.createElement('a');
    const dateStr = new Date().toISOString().slice(0, 10).replace(/-/g, '');
    a.href = objectUrl;
    a.download = `${cfg.title}_${dateStr}.xlsx`;
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(objectUrl);
    toast('导出成功');
  } catch (err) {
    toast('导出失败：' + err.message, 'error');
  }
}

/* Node 单测导出（浏览器中 module 为 undefined，自动跳过） */
if (typeof module !== 'undefined' && module.exports) {
  module.exports = {
    EXPORT_MENU_MAP,
    billExportParams,
    billExportQuery,
    billExportDownloadUrl,
    billExportPreviewUrl,
    billExportSignature,
    billExportPreviewMatches,
    billPreviewFilterRows,
    billPreviewSummaryText,
  };
}
