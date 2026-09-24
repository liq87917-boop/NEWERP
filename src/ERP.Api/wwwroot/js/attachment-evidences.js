/* ==================================================================================
   ========== 业务单据附件内容证据登记册（ERP-061）— 唯一附件内容册 ==========
   ==================================================================================
   定位：把用户提供的 PDF / PNG / JPEG 证据挂到**既有**销售订单 / 采购订单（后续任务在同一模型上扩展单证），
         由服务端权威保存内容元数据（净化文件名快照 / 媒体类型 / 字节长度 / SHA-256 摘要 / 上传人 / 登记时间）。
   边界（界面侧同样遵守）：
     1. 上传内容一律按**不可信文件**处理：只接受服务端按文件签名判定的 PDF / PNG / JPEG；
        界面只做「提示性」预检（扩展名 / 大小），服务端才是权威（扩展名、声明的 Content-Type、签名三者必须一致）；
     2. 界面不计算也不提交摘要 / 字节长度 / 存储键 / 归属快照 / 上传人 / 登记时间：这些一律由服务端生成；
     3. 下载一律带 Bearer 认证请求内容接口，并以「附件」方式保存到本地；界面**不**内联渲染上传内容、
        **不**把内容拼进 DOM、**不**生成任何存储路径或链接（存储键从不返回给界面）；
     4. 更正走显式作废（必填原因）：界面不提供编辑 / 删除 / 替换二进制 / 改派归属；
     5. 附件证据是用户提供的仓库文件证据，不是报关 / 报税 / 银行 / 承运人 / 客户确认：文案与服务端
        AttachmentEvidenceRules / AttachmentEvidenceService 保持一致。
   ================================================================================== */

let AE = {
  ownerType: '', ownerId: '', ownerNo: '',
  filters: { ownerType: '', status: '', sha256: '', keyword: '' },
  list: [], total: 0, page: 1, pageSize: 50,
  metadata: null,
  owners: [], ownerKeyword: '',
  upload: null, voidId: null, voidReason: '',
  busy: false
};

/* HTML 转义：所有来自接口的文本（文件名 / 说明 / 摘要 / 快照 / 服务端文案）都必须转义后再进 innerHTML */
function aeEsc(value) {
  return String(value == null ? '' : value)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}

/* 由当前模块编码推断归属单据类型（供列表行操作「附件证据」按钮复用；未知模块返回空串 → 由用户显式选择） */
function aeOwnerTypeOfModule(code) {
  switch (code) {
    case 'sales-order': return 'SalesOrder';
    case 'purchase-order': return 'PurchaseOrder';
    /* ERP-062：单证中心（出口单证）行操作「附件证据」→ 走同一附件证据模型
       （类型编码与 ERP-045 附件引用册的父单据类型完全一致） */
    case 'doc-center': return 'TradeDocument';
    default: return '';
  }
}

/* 单次「附件证据概览」请求的单据数上限（与 AttachmentEvidenceRules.MaxSummaryOwnerIds 同源：
   服务端才是权威，界面只做提示性预检，避免把明显超量的请求发出去） */
const AE_SUMMARY_MAX_OWNER_IDS = 200;

/* 列表行操作入口：crud.js 会传入行 Id，归属类型按当前模块编码推断 */
async function openAttachmentEvidencesForCurrentModule(ownerId) {
  await openAttachmentEvidences(aeOwnerTypeOfModule(CURRENT_MODULE_CODE), ownerId);
}

/* ==================== 打开登记册 ==================== */

async function openAttachmentEvidences(ownerType, ownerId) {
  AE = {
    ownerType: ownerType || '',
    ownerId: ownerId ? String(ownerId) : '',
    ownerNo: '',
    filters: { ownerType: ownerType || '', status: '', sha256: '', keyword: '' },
    list: [], total: 0, page: 1, pageSize: 50,
    metadata: null,
    owners: [], ownerKeyword: '',
    upload: null, voidId: null, voidReason: '',
    busy: false
  };

  const modal = document.getElementById('modal');
  modal.innerHTML = '<div style="padding:40px;text-align:center;color:#64748b">正在加载…</div>';
  modal.style.display = 'block';

  try {
    AE.metadata = await api('/api/attachment-evidences/metadata');
  } catch (e) {
    toast('附件证据模块元数据加载失败：' + e.message, 'error');
  }

  if (AE.filters.ownerType) await aeLoadOwnerOptions('');
  await aeLoadList();
  aeRender();
}

async function aeLoadOwnerOptions(keyword) {
  const type = AE.filters.ownerType;
  if (!type) { AE.owners = []; return; }
  try {
    const res = await api('/api/attachment-evidences/owner-options?ownerType='
      + encodeURIComponent(type) + '&keyword=' + encodeURIComponent(keyword || '') + '&take=200');
    AE.owners = Array.isArray(res) ? res : [];
  } catch (e) {
    AE.owners = [];
    toast('归属单据候选加载失败：' + e.message, 'error');
  }
}

async function aeLoadList() {
  const f = AE.filters;
  const params = ['page=' + AE.page, 'pageSize=' + AE.pageSize];
  if (f.ownerType) params.push('ownerType=' + encodeURIComponent(f.ownerType));
  if (AE.ownerId) params.push('ownerId=' + encodeURIComponent(AE.ownerId));
  if (f.status !== '') params.push('status=' + encodeURIComponent(f.status));
  if (f.sha256) params.push('sha256=' + encodeURIComponent(f.sha256));
  if (f.keyword) params.push('keyword=' + encodeURIComponent(f.keyword));

  try {
    const res = await api('/api/attachment-evidences?' + params.join('&'));
    AE.list = (res && res.items) ? res.items : [];
    AE.total = (res && res.total) || 0;
  } catch (e) {
    AE.list = []; AE.total = 0;
    toast('附件证据台账加载失败：' + e.message, 'error');
  }

  /* 从单据行操作进入时用首条记录回填当前单据号码，便于标题显示（只读展示，不猜测归属） */
  if (!AE.ownerNo && AE.list.length) AE.ownerNo = AE.list[0].ownerNo || '';
}

/* ==================== 渲染 ==================== */

function aeRender() {
  const modal = document.getElementById('modal');
  const meta = AE.metadata;
  const statusOptions = meta ? (meta.statusOptions || [])
    : [{ value: '0', label: '有效' }, { value: '1', label: '已作废' }];
  const ownerTypeOptions = meta ? (meta.ownerTypes || []) : [];
  const allowed = meta ? (meta.allowedExtensions || []).join(' / ') : '.pdf / .png / .jpg / .jpeg';
  const pages = Math.max(1, Math.ceil(AE.total / AE.pageSize));
  const rows = AE.list.map(aeRowHtml).join('');

  modal.innerHTML = `
    <div style="max-width:1360px;margin:2vh auto;background:#fff;border-radius:14px;padding:16px 18px;max-height:94vh;overflow:auto;box-shadow:0 20px 60px rgba(0,0,0,.25)">
      <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:6px">
        <h3 style="margin:0">📎 业务单据附件证据
          <span style="font-size:13px;color:#64748b;font-weight:400;margin-left:10px">
            只接受 ${aeEsc(allowed)} 证据（按文件签名判定）；摘要 / 长度 / 存储键 / 上传人均由服务端生成</span></h3>
        <div>
          <button class="btn btn-primary btn-sm" onclick="aeOpenUpload()">＋ 上传附件证据</button>
          <button class="btn btn-neutral btn-sm" onclick="aeClose()">关闭</button>
        </div>
      </div>
      <div style="background:#fff7ed;border:1px solid #fed7aa;color:#9a3412;border-radius:8px;padding:8px 10px;font-size:12px;line-height:1.7;margin-bottom:8px">
        <b>边界</b>：${aeEsc(meta ? meta.boundaryText : '')}<br>
        <b>格式</b>：${aeEsc(meta ? meta.formatPolicyText : '')}<br>
        <b>大小</b>：${aeEsc(meta ? meta.sizePolicyText : '')}<br>
        <b>存储</b>：${aeEsc(meta ? meta.storagePolicyText : '')}（当前提供程序：${aeEsc(meta ? meta.storageProviderText : '')}）<br>
        <b>下载</b>：${aeEsc(meta ? meta.downloadPolicyText : '')}
        ${meta && meta.legacyFileNotePolicyText ? '<br><b>历史文本</b>：' + aeEsc(meta.legacyFileNotePolicyText) : ''}
      </div>
      ${AE.upload ? aeUploadFormHtml(ownerTypeOptions, allowed) : ''}
      ${AE.ownerId ? aeFixedOwnerSelect() : ''}
      <div style="display:flex;gap:6px;flex-wrap:wrap;align-items:center;margin:6px 0">
        ${AE.ownerId ? '' : `
        <label style="font-size:13px;color:#475569">归属类型
          <select id="ae-f-owner-type" onchange="aeSetFilter('ownerType', this.value)" style="margin-left:4px">
            <option value="">（全部类型）</option>
            ${ownerTypeOptions.map(o => `<option value="${aeEsc(o.value)}" ${AE.filters.ownerType === o.value ? 'selected' : ''}>${aeEsc(o.label)}</option>`).join('')}
          </select></label>`}
        <label style="font-size:13px;color:#475569">状态
          <select id="ae-f-status" onchange="aeSetFilter('status', this.value)" style="margin-left:4px">
            <option value="">（全部，含已作废）</option>
            ${statusOptions.map(o => `<option value="${aeEsc(o.value)}" ${AE.filters.status === o.value ? 'selected' : ''}>${aeEsc(o.label)}</option>`).join('')}
          </select></label>
        <input id="ae-f-keyword" placeholder="单据号 / 文件名 / 说明" value="${aeEsc(AE.filters.keyword)}"
               style="padding:5px 8px;border:1px solid #cbd5e1;border-radius:6px;font-size:13px" />
        <input id="ae-f-sha256" placeholder="摘要前缀（8~64 位十六进制，仅人工比对）" value="${aeEsc(AE.filters.sha256)}"
               style="width:250px;padding:5px 8px;border:1px solid #cbd5e1;border-radius:6px;font-size:13px" />
        <button class="btn btn-neutral btn-sm" onclick="aeSearch()">查询</button>
        <button class="btn btn-neutral btn-sm" onclick="aeResetFilters()">重置</button>
      </div>
      <div style="overflow:auto;max-height:50vh;border:1px solid #e2e8f0;border-radius:8px">
        <table style="width:100%;border-collapse:collapse;font-size:13px">
          <thead><tr style="background:#f8fafc;text-align:left">
            <th style="padding:6px">归属单据</th><th style="padding:6px">原始文件名</th>
            <th style="padding:6px">类型 / 大小</th><th style="padding:6px">SHA-256 摘要</th>
            <th style="padding:6px">说明</th><th style="padding:6px">上传人 / 登记时间</th>
            <th style="padding:6px">状态 / 下载</th><th style="padding:6px">操作</th>
          </tr></thead>
          <tbody>${rows || '<tr><td colspan="8" style="padding:14px;text-align:center;color:#94a3b8">该筛选条件下暂无附件证据（系统不回填历史 FileNote 或图片位，也不做内容去重）</td></tr>'}</tbody>
        </table>
      </div>
      <div style="display:flex;justify-content:space-between;align-items:center;margin-top:8px;font-size:13px;color:#475569">
        <span>共 ${AE.total} 条 · 第 ${AE.page} / ${pages} 页 · 每页 ${AE.pageSize} 条（单次上限 ${meta ? meta.maxPageSize : 200}）</span>
        <span>
          <button class="btn btn-neutral btn-sm" ${AE.page <= 1 ? 'disabled' : ''} onclick="aePage(${AE.page - 1})">上一页</button>
          <button class="btn btn-neutral btn-sm" ${AE.page >= pages ? 'disabled' : ''} onclick="aePage(${AE.page + 1})">下一页</button>
        </span>
      </div>
    </div>`;
}

function aeFixedOwnerSelect() {
  return `<div style="background:#eff6ff;border:1px solid #bfdbfe;color:#1e3a8a;border-radius:8px;padding:6px 10px;font-size:12px;margin-bottom:6px">
    当前单据：${aeEsc(AE.ownerNo || ('Id=' + AE.ownerId))}（按当前单据筛选；上传时归属由本单据显式确定，系统不按号码猜测）
  </div>`;
}

function aeUploadFormHtml(ownerTypeOptions, allowed) {
  const u = AE.upload;
  return `
    <div style="border:1px solid #cbd5e1;border-radius:8px;padding:10px;margin:6px 0;background:#f8fafc">
      <b style="font-size:13px">上传附件证据（服务端按文件签名复核；客户端只提交文件与说明）</b>
      <div style="display:flex;gap:8px;flex-wrap:wrap;align-items:flex-end;margin-top:8px">
        ${AE.ownerId ? '' : `
        <label style="font-size:13px;color:#475569">归属类型 *<br>
          <select id="ae-u-owner-type" onchange="aeChangeUploadOwnerType(this.value)" style="margin-top:3px">
            <option value="">请选择</option>
            ${ownerTypeOptions.map(o => `<option value="${aeEsc(o.value)}" ${u.ownerType === o.value ? 'selected' : ''}>${aeEsc(o.label)}</option>`).join('')}
          </select></label>
        <label style="font-size:13px;color:#475569">归属单据 *<br>
          <select id="ae-u-owner-id" style="margin-top:3px;min-width:320px">
            <option value="">请选择</option>
            ${AE.owners.map(o => `<option value="${o.ownerId}" ${String(u.ownerId) === String(o.ownerId) ? 'selected' : ''}>${aeEsc(o.selectableText || o.summaryText)}</option>`).join('')}
          </select></label>`}
        <label style="font-size:13px;color:#475569">证据文件（${aeEsc(allowed)}）*<br>
          <input type="file" id="ae-u-file" accept=".pdf,.png,.jpg,.jpeg,application/pdf,image/png,image/jpeg" style="margin-top:3px" /></label>
        <label style="font-size:13px;color:#475569">说明（可选，≤500 字）<br>
          <input id="ae-u-description" value="${aeEsc(u.description || '')}" maxlength="500"
                 style="margin-top:3px;width:320px;padding:5px 8px;border:1px solid #cbd5e1;border-radius:6px" /></label>
        <button class="btn btn-primary btn-sm" ${AE.busy ? 'disabled' : ''} onclick="aeSubmitUpload()">上传</button>
        <button class="btn btn-neutral btn-sm" onclick="aeCancelUpload()">取消</button>
      </div>
      <div style="font-size:12px;color:#64748b;margin-top:6px">
        界面只做提示性预检（扩展名 / 大小上限：${aeEsc(AE.metadata ? AE.metadata.sizePolicyText : '')}）；服务端会再次校验扩展名、
        声明的 Content-Type 与文件签名，并拒绝可执行 / 脚本 / 标记类格式与无法识别的二进制。
      </div>
    </div>`;
}

function aeRowHtml(row) {
  const available = row.ownerAvailable
    ? ''
    : `<div style="color:#b45309;font-size:12px">${aeEsc(row.ownerAvailabilityText)}</div>`;
  const voidInfo = row.isVoided
    ? `<div style="color:#b91c1c;font-size:12px">作废于 ${aeEsc(row.voidedAt ? String(row.voidedAt).slice(0, 16).replace('T', ' ') : '未知')}：${aeEsc(row.voidReason)}</div>`
    : '';
  return `<tr style="border-top:1px solid #e2e8f0">
    <td style="padding:6px">${aeEsc(row.ownerSnapshotText)}<div style="font-size:12px;color:#64748b">Id=${row.ownerId}</div>${available}</td>
    <td style="padding:6px">${aeEsc(row.originalFileName)}
      <div style="font-size:12px;color:#64748b">存储：${aeEsc(row.storageProviderText)}</div></td>
    <td style="padding:6px">${aeEsc(row.mediaTypeText)}<div style="font-size:12px;color:#64748b">${aeEsc(row.sizeText)}（${row.sizeBytes} 字节）</div></td>
    <td style="padding:6px;font-family:monospace;font-size:12px" title="${aeEsc(row.sha256)}">${aeEsc(String(row.sha256).slice(0, 16))}…</td>
    <td style="padding:6px">${aeEsc(row.description)}</td>
    <td style="padding:6px">${aeEsc(row.uploadedBy)}<div style="font-size:12px;color:#64748b">${aeEsc(row.recordedAt ? String(row.recordedAt).slice(0, 16).replace('T', ' ') : '')}</div></td>
    <td style="padding:6px">${aeEsc(row.statusText)}
      <div style="font-size:12px;color:#64748b">${aeEsc(row.downloadAvailabilityText)}</div>${voidInfo}</td>
    <td style="padding:6px;white-space:nowrap">
      <button class="btn btn-neutral btn-sm" ${row.contentDownloadable ? '' : 'disabled'} onclick="aeDownload(${row.id})">下载</button>
      <button class="btn btn-neutral btn-sm" ${row.isActive ? '' : 'disabled'} onclick="aeOpenVoid(${row.id})">作废</button>
    </td>
  </tr>`;
}

/* ==================== 交互：筛选 / 分页 / 关闭 ==================== */

async function aeSetFilter(key, value) {
  AE.filters[key] = value;
  AE.page = 1;
  if (key === 'ownerType') {
    AE.ownerKeyword = '';
    await aeLoadOwnerOptions('');
  }
  await aeLoadList();
  aeRender();
}

async function aeSearch() {
  const kw = document.getElementById('ae-f-keyword');
  const sha = document.getElementById('ae-f-sha256');
  AE.filters.keyword = kw ? kw.value.trim() : '';
  AE.filters.sha256 = sha ? sha.value.trim() : '';
  AE.page = 1;
  await aeLoadList();
  aeRender();
}

async function aeResetFilters() {
  AE.filters.status = '';
  AE.filters.sha256 = '';
  AE.filters.keyword = '';
  AE.page = 1;
  await aeLoadList();
  aeRender();
}

async function aePage(page) {
  AE.page = page < 1 ? 1 : page;
  await aeLoadList();
  aeRender();
}

function aeClose() {
  const modal = document.getElementById('modal');
  modal.style.display = 'none';
  modal.innerHTML = '';
}

/* ==================== 上传（界面只做提示性预检，服务端才是权威） ==================== */

function aeOpenUpload() {
  AE.upload = { ownerType: AE.filters.ownerType || AE.ownerType || '', ownerId: AE.ownerId || '', description: '' };
  aeRender();
}

function aeCancelUpload() {
  AE.upload = null;
  aeRender();
}

async function aeChangeUploadOwnerType(type) {
  if (!AE.upload) return;
  AE.upload.ownerType = type;
  AE.upload.ownerId = '';
  AE.filters.ownerType = type;
  await aeLoadOwnerOptions('');
  aeRender();
}

async function aeSubmitUpload() {
  if (!AE.upload || AE.busy) return;

  const meta = AE.metadata;
  const fileInput = document.getElementById('ae-u-file');
  const ownerTypeSelect = document.getElementById('ae-u-owner-type');
  const ownerIdSelect = document.getElementById('ae-u-owner-id');
  const descriptionInput = document.getElementById('ae-u-description');

  const ownerType = AE.ownerId ? AE.filters.ownerType : (ownerTypeSelect ? ownerTypeSelect.value : '');
  const ownerId = AE.ownerId ? AE.ownerId : (ownerIdSelect ? ownerIdSelect.value : '');
  const description = descriptionInput ? descriptionInput.value.trim() : '';
  const file = fileInput && fileInput.files && fileInput.files.length ? fileInput.files[0] : null;

  if (!ownerType) { toast('请选择归属单据类型', 'error'); return; }
  if (!ownerId) { toast('请显式选择归属单据（系统不按号码或名称猜测归属）', 'error'); return; }
  if (!file) { toast('请选择要上传的附件文件', 'error'); return; }

  if (meta && file.size > meta.maxSizeBytes) {
    toast('附件大小超过上限 ' + Math.round(meta.maxSizeBytes / 1048576) + ' MB：请压缩或拆分后再上传', 'error');
    return;
  }
  const allowedExtensions = (meta && meta.allowedExtensions && meta.allowedExtensions.length)
    ? meta.allowedExtensions : ['.pdf', '.png', '.jpg', '.jpeg'];
  const extension = (String(file.name).match(/\.[^.]+$/) || [''])[0].toLowerCase();
  if (allowedExtensions.indexOf(extension) < 0) {
    toast('仅支持 ' + allowedExtensions.join(' / ') + ' 证据（服务端还会按文件签名复核扩展名与内容）', 'error');
    return;
  }

  const formData = new FormData();
  formData.append('file', file);
  formData.append('ownerType', ownerType);
  formData.append('ownerId', ownerId);
  formData.append('description', description);

  AE.busy = true;
  try {
    const saved = await uploadFile('/api/attachment-evidences', formData);
    toast('附件证据已登记：' + (saved && saved.originalFileName ? saved.originalFileName : '')
      + '（摘要 / 长度 / 媒体类型均由服务端生成）');
    AE.upload = null;
    AE.page = 1;
    await aeLoadList();
    aeRender();
  } catch (e) {
    toast('上传失败：' + e.message, 'error');
  } finally {
    AE.busy = false;
  }
}

/* ==================== 下载（带认证请求内容接口；不内联渲染、不拼接存储路径） ==================== */

async function aeDownload(id) {
  try {
    const headers = {};
    if (TOKEN) headers['Authorization'] = 'Bearer ' + TOKEN;
    const resp = await fetch('/api/attachment-evidences/' + id + '/content', { headers: headers });
    const contentType = resp.headers.get('Content-Type') || '';

    /* 统一响应格式下，业务错误也是 JSON（HTTP 200），因此必须按 Content-Type 判定 */
    if (!resp.ok || contentType.indexOf('application/json') >= 0) {
      let message = '下载失败';
      try {
        const body = await resp.json();
        if (body && body.message) message = body.message;
      } catch (e) { /* 保持默认提示 */ }
      toast(message, 'error');
      return;
    }

    const blob = await resp.blob();
    const row = AE.list.find(r => r.id === id);
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = (row && row.originalFileName) ? row.originalFileName : ('attachment-evidence-' + id);
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
    toast('附件证据已下载（内容按不可信文件处理：浏览器以附件方式保存，请勿直接打开来源不明的文件）');
  } catch (e) {
    toast('下载失败：' + e.message, 'error');
  }
}

/* ==================== 作废（唯一更正方式：必填原因，保留原始元数据与历史） ==================== */

function aeOpenVoid(id) {
  const row = AE.list.find(r => r.id === id);
  AE.voidId = id;

  const overlay = document.createElement('div');
  overlay.id = 'ae-void-box';
  overlay.style.cssText = 'position:fixed;inset:0;background:rgba(15,23,42,.45);display:flex;align-items:center;justify-content:center;z-index:1200';
  overlay.innerHTML = `
    <div style="background:#fff;border-radius:12px;padding:16px 18px;max-width:600px;box-shadow:0 20px 60px rgba(0,0,0,.3)">
      <h4 style="margin:0 0 6px">作废附件证据</h4>
      <div style="font-size:13px;color:#475569;line-height:1.7;margin-bottom:8px">
        归属：${aeEsc(row ? row.ownerSnapshotText : ('Id=' + id))} · 文件：${aeEsc(row ? row.originalFileName : '')}<br>
        SHA-256：<span style="font-family:monospace;font-size:12px">${aeEsc(row ? row.sha256 : '')}</span><br>
        作废后保留原始文件名 / 摘要 / 媒体类型 / 登记历史（可读），但<strong>内容不再提供下载</strong>；
        系统不提供硬删除、不替换二进制内容、不把证据改派到别的单据。
      </div>
      <textarea id="ae-void-reason" maxlength="500" placeholder="作废原因（必填，将写入作废留痕）"
        style="width:100%;height:80px;padding:8px;border:1px solid #cbd5e1;border-radius:8px;font-size:13px"></textarea>
      <div style="text-align:right;margin-top:8px">
        <button class="btn btn-neutral btn-sm" onclick="aeCancelVoid()">取消</button>
        <button class="btn btn-primary btn-sm" onclick="aeConfirmVoid()">确认作废</button>
      </div>
    </div>`;
  document.body.appendChild(overlay);
}

function aeCancelVoid() {
  const box = document.getElementById('ae-void-box');
  if (box) box.remove();
  AE.voidId = null;
  AE.voidReason = '';
}

async function aeConfirmVoid() {
  const reasonInput = document.getElementById('ae-void-reason');
  const reason = reasonInput ? reasonInput.value.trim() : '';
  if (!reason) { toast('请填写作废原因', 'error'); return; }
  if (!AE.voidId) return;

  try {
    await api('/api/attachment-evidences/' + AE.voidId + '/void', 'POST', { reason: reason });
    toast('附件证据已作废（原始文件名 / 摘要 / 登记历史保留可读，内容不再提供下载）');
    aeCancelVoid();
    await aeLoadList();
    aeRender();
  } catch (e) {
    toast('作废失败：' + e.message, 'error');
  }
}

/* ==================== 列表页有界摘要（ERP-062：一次批量计数，无逐行查库、不读取任何存储内容） ==================== */

/* 列表页入口（单证中心等模块的工具栏「📎 附件证据概览」）：按**当前页**单据 Id 一次取回
   附件证据条数与归属可用性；界面不自行计算计数、不逐行发请求、不访问任何存储内容 */
async function openAttachmentEvidenceSummaryForCurrentPage() {
  const ownerType = aeOwnerTypeOfModule(CURRENT_MODULE_CODE);
  if (!ownerType) {
    toast('当前模块没有可汇总的附件证据归属类型：请从销售订单 / 采购订单 / 单证中心列表进入', 'error');
    return;
  }

  const rows = Array.isArray(window.__moduleRows) ? window.__moduleRows : [];
  const ids = [];
  rows.forEach(row => {
    const id = Number(row && row.id);
    if (Number.isInteger(id) && id > 0 && ids.indexOf(id) < 0) ids.push(id);
  });

  if (!ids.length) { toast('当前页没有可汇总的单据：请先查询出本页数据', 'error'); return; }
  if (ids.length > AE_SUMMARY_MAX_OWNER_IDS) {
    toast('当前页单据数（' + ids.length + '）超过单次汇总上限 ' + AE_SUMMARY_MAX_OWNER_IDS
      + '：请缩小每页数量后重试', 'error');
    return;
  }

  try {
    const list = await api('/api/attachment-evidences/owner-summary?ownerType='
      + encodeURIComponent(ownerType) + '&ids=' + ids.join(','));
    aeRenderSummary(ownerType, Array.isArray(list) ? list : []);
  } catch (e) {
    toast('附件证据概览加载失败：' + e.message, 'error');
  }
}

/* 概览渲染：只显示服务端返回的有界计数与归属可用性；不访问存储、不拼任何存储路径或链接，
   也不把计数呈现为报关 / 报税 / 承运人提交或确认 */
function aeRenderSummary(ownerType, list) {
  const ownerTypeText = aeEsc((list.length && list[0].ownerTypeText) ? list[0].ownerTypeText : ownerType);
  const boundary = (list.length && list[0].boundaryText)
    ? list[0].boundaryText
    : '附件证据是用户提供的仓库文件证据，不是报关 / 报税 / 银行 / 承运人 / 客户确认。';
  const body = list.map(row => `<tr style="border-top:1px solid #e2e8f0">
    <td style="padding:6px">${aeEsc(row.ownerSnapshotText)}
      <div style="font-size:12px;color:#64748b">Id=${aeEsc(row.ownerId)}</div>
      ${row.ownerAvailable ? ''
        : '<div style="color:#b45309;font-size:12px">归属单据已不存在或已删除：历史证据仍需显式进入登记册只读查看，不提供下载，也不改派</div>'}</td>
    <td style="padding:6px">${aeEsc(row.totalCount)} 条
      <div style="font-size:12px;color:#64748b">有效 ${aeEsc(row.activeCount)} / 已作废 ${aeEsc(row.voidedCount)}</div></td>
    <td style="padding:6px;white-space:nowrap">
      <button class="btn btn-neutral btn-sm" onclick="aeOpenFromSummary(${aeEsc(row.ownerId)})">查看 / 上传</button>
    </td>
  </tr>`).join('');

  const overlay = document.createElement('div');
  overlay.id = 'ae-summary-box';
  overlay.style.cssText = 'position:fixed;inset:0;background:rgba(15,23,42,.45);display:flex;align-items:center;justify-content:center;z-index:1200';
  overlay.innerHTML = `
    <div style="background:#fff;border-radius:12px;padding:16px 18px;max-width:920px;max-height:86vh;overflow:auto;box-shadow:0 20px 60px rgba(0,0,0,.3)">
      <h4 style="margin:0 0 6px">📎 附件证据概览（${ownerTypeText} · 当前页有界摘要）</h4>
      <div style="background:#fff7ed;border:1px solid #fed7aa;color:#9a3412;border-radius:8px;padding:8px 10px;font-size:12px;line-height:1.7;margin-bottom:8px">
        计数只表示用户已登记的<strong>仓库附件证据</strong>条数：一次批量查询，不逐行查库、不访问任何存储内容；
        内容只有在显式发起带认证的下载请求时才会被读取。${aeEsc(boundary)}
      </div>
      <div style="background:#eff6ff;border:1px solid #bfdbfe;color:#1e3a8a;border-radius:8px;padding:8px 10px;font-size:12px;line-height:1.7;margin-bottom:8px">
        单证台账既有的「附件说明 / 存放位置」是历史自由文本：系统保持原样可读，不解析成路径、不抓取其中的地址、
        不转成附件证据，也不在读取时回填（详细口径见登记册内说明）。
      </div>
      <table style="width:100%;border-collapse:collapse;font-size:13px">
        <thead><tr style="background:#f8fafc;text-align:left">
          <th style="padding:6px">单据</th><th style="padding:6px">附件证据</th><th style="padding:6px">操作</th>
        </tr></thead>
        <tbody>${body || '<tr><td colspan="3" style="padding:14px;text-align:center;color:#94a3b8">当前页没有可显示的单据</td></tr>'}</tbody>
      </table>
      <div style="text-align:right;margin-top:8px">
        <button class="btn btn-neutral btn-sm" onclick="aeCloseSummary()">关闭</button>
      </div>
    </div>`;
  document.body.appendChild(overlay);
}

/* 概览 → 本单据登记册（归属由当前模块类型 + 该行 Id 显式确定，绝不按号码或文件名猜测） */
function aeOpenFromSummary(ownerId) {
  const ownerType = aeOwnerTypeOfModule(CURRENT_MODULE_CODE);
  aeCloseSummary();
  openAttachmentEvidences(ownerType, ownerId);
}

function aeCloseSummary() {
  const box = document.getElementById('ae-summary-box');
  if (box) box.remove();
}

