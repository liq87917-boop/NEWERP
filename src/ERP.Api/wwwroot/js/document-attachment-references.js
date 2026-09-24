/* ==================================================================================
   ========== 业务单据附件引用登记册（ERP-045）— 仅元数据的附件引用 ==========
   ==================================================================================
   定位：为既有销售订单 / 采购订单 / 装柜清单 / 出口单证登记**仅元数据**的附件引用
         （分类 / 安全显示名 / 不透明引用标识 / 可选内容类型 / 字节数 / 校验和 / 备注）。
   边界（界面侧同样遵守）：
     1. 界面不上传 / 不下载 / 不预览 / 不抓取任何文件，也不把引用标识拼成链接或任何图片 / 框架标签；
     2. 引用标识按不透明令牌显示（服务端校验：拒绝链接、路径穿越、空白、HTML / 脚本标记）；
        历史或外部写入的不安全值只显示服务端返回的「引用不可用」文本；
     3. 登记必须显式勾选来源授权确认并填写确认说明（确认不授予存储访问权）；
     4. 更正走作废（必须填原因），界面不提供编辑 / 删除：作废保留原始元数据与授权留痕；
     5. 列表条目是**元数据引用**，不是文件可用的证明；界面文案与服务端
        DocumentAttachmentReferenceRules / DocumentAttachmentReferenceService 保持一致。
   ================================================================================== */

let DAR = {
  parentType: '',
  parentId: '',
  parentNo: '',
  filters: { parentType: '', category: '', status: '', keyword: '' },
  list: [], total: 0, page: 1, pageSize: 50,
  metadata: null,
  parents: [], parentKeyword: '',
  form: null,
  voidId: null, voidReason: '',
  busy: false
};

/* HTML 转义：所有来自接口的文本（显示名 / 备注 / 引用标识 / 快照）都必须转义后再进 innerHTML */
function darEsc(value) {
  return String(value == null ? '' : value)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}

/* 由当前模块编码推断父单据类型（供列表行操作「附件引用」按钮复用） */
function darParentTypeOfModule(code) {
  switch (code) {
    case 'sales-order': return 'SalesOrder';
    case 'purchase-order': return 'PurchaseOrder';
    case 'loading-list': return 'ContainerLoadingList';
    case 'doc-center': return 'TradeDocument';
    default: return '';
  }
}

/* 列表行操作入口：crud.js 会传入行 Id，父单据类型按当前模块编码推断 */
async function openDocumentAttachmentReferencesForCurrentModule(parentId) {
  await openDocumentAttachmentReferences(darParentTypeOfModule(CURRENT_MODULE_CODE), parentId, '');
}

/* ==================== 打开登记册 ==================== */

async function openDocumentAttachmentReferences(parentType, parentId, parentNo) {
  DAR = {
    parentType: parentType || '',
    parentId: parentId ? String(parentId) : '',
    parentNo: parentNo || '',
    filters: {
      parentType: parentType || '',
      category: '', status: '',
      keyword: parentNo || ''
    },
    list: [], total: 0, page: 1, pageSize: 50,
    metadata: null,
    parents: [], parentKeyword: '',
    form: null, voidId: null, voidReason: '', busy: false
  };

  const modal = document.getElementById('modal');
  modal.innerHTML = '<div style="padding:40px;text-align:center;color:#64748b">正在加载…</div>';
  modal.style.display = 'block';

  try {
    DAR.metadata = await api('/api/document-attachment-references/metadata');
  } catch (e) {
    toast('附件引用模块元数据加载失败：' + e.message, 'error');
  }

  if (DAR.filters.parentType) await darLoadParentOptions('');
  await darLoadList();
  darRender();
}

async function darLoadParentOptions(keyword) {
  const type = DAR.filters.parentType;
  if (!type) { DAR.parents = []; return; }
  try {
    const res = await api('/api/document-attachment-references/parent-options?parentType='
      + encodeURIComponent(type) + '&keyword=' + encodeURIComponent(keyword || '') + '&take=200');
    DAR.parents = Array.isArray(res) ? res : [];
  } catch (e) {
    DAR.parents = [];
    toast('父单据候选加载失败：' + e.message, 'error');
  }
}

async function darLoadList() {
  const f = DAR.filters;
  const params = ['page=' + DAR.page, 'pageSize=' + DAR.pageSize];
  if (f.parentType) params.push('parentType=' + encodeURIComponent(f.parentType));
  if (DAR.parentId) params.push('parentId=' + encodeURIComponent(DAR.parentId));
  if (f.category) params.push('category=' + encodeURIComponent(f.category));
  if (f.status !== '') params.push('status=' + encodeURIComponent(f.status));
  if (f.keyword) params.push('keyword=' + encodeURIComponent(f.keyword));

  try {
    const res = await api('/api/document-attachment-references?' + params.join('&'));
    DAR.list = (res && res.items) ? res.items : [];
    DAR.total = (res && res.total) || 0;
  } catch (e) {
    DAR.list = []; DAR.total = 0;
    toast('附件引用台账加载失败：' + e.message, 'error');
  }

  /* 从单据行操作进入时用首条记录回填当前单据号码，便于标题显示（只读展示，不猜测父单据归属） */
  if (!DAR.parentNo && DAR.list.length) DAR.parentNo = DAR.list[0].parentNo || '';
}

/* ==================== 渲染 ==================== */

function darRender() {
  const modal = document.getElementById('modal');
  const meta = DAR.metadata;
  const boundary = meta ? meta.boundaryText : '';
  const notice = meta ? meta.metadataOnlyNoticeText : '';
  const typeOptions = meta ? meta.parentTypes : [];
  const categoryOptions = meta ? meta.categories : [];
  const statusOptions = meta ? meta.statusOptions : [{ value: '0', label: '有效' }, { value: '1', label: '已作废' }];

  const rows = DAR.list.map(row => darRowHtml(row)).join('');
  const fixedParent = DAR.parentId ? darFixedParentSelect() : '';

  modal.innerHTML = `
    <div style="max-width:1280px;margin:2vh auto;background:#fff;border-radius:14px;padding:16px 18px;max-height:94vh;overflow:auto;box-shadow:0 20px 60px rgba(0,0,0,.25)">
      <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:6px">
        <h3 style="margin:0">📎 业务单据附件引用登记册
          <span style="font-size:13px;color:#64748b;font-weight:400;margin-left:10px">
            仅登记元数据（分类 / 显示名 / 不透明引用标识 / 大小 / 校验和）；不上传、不下载、不预览、不抓取任何文件</span></h3>
        <div>
          <button class="btn btn-primary btn-sm" onclick="darOpenForm()">＋ 登记附件引用</button>
          <button class="btn btn-neutral btn-sm" onclick="closeModal()">✕ 关闭</button>
        </div>
      </div>
      <div style="background:#fff7ed;border:1px solid #fed7aa;border-radius:8px;padding:8px 10px;font-size:12px;color:#9a3412;margin-bottom:10px">
        ${darEsc(notice)}<br>${darEsc(boundary)}
      </div>
      <div style="display:flex;flex-wrap:wrap;gap:8px;align-items:flex-end;margin-bottom:10px">
        <label style="font-size:12px;color:#475569">父单据类型<br>
          <select id="dar-ftype" onchange="darFilterTypeChanged()" ${DAR.parentId ? 'disabled' : ''}>
            <option value="">全部类型</option>
            ${typeOptions.map(o => `<option value="${darEsc(o.value)}" ${o.value === DAR.filters.parentType ? 'selected' : ''}>${darEsc(o.label)}</option>`).join('')}
          </select></label>
        <label style="font-size:12px;color:#475569">分类<br>
          <select id="dar-fcategory" onchange="darFiltersChanged()">
            <option value="">全部分类</option>
            ${categoryOptions.map(o => `<option value="${darEsc(o.value)}" ${o.value === DAR.filters.category ? 'selected' : ''}>${darEsc(o.label)}</option>`).join('')}
          </select></label>
        <label style="font-size:12px;color:#475569">状态<br>
          <select id="dar-fstatus" onchange="darFiltersChanged()">
            <option value="">全部（含已作废）</option>
            ${statusOptions.map(o => `<option value="${darEsc(o.value)}" ${String(o.value) === String(DAR.filters.status) ? 'selected' : ''}>${darEsc(o.label)}</option>`).join('')}
          </select></label>
        <label style="font-size:12px;color:#475569;flex:1;min-width:180px">关键字（父单据号 / 显示名 / 引用标识 / 备注）<br>
          <input id="dar-fkeyword" value="${darEsc(DAR.filters.keyword)}" onkeydown="if(event.key==='Enter')darFiltersChanged()"></label>
        <button class="btn btn-neutral btn-sm" onclick="darFiltersChanged()">查询</button>
        <button class="btn btn-neutral btn-sm" onclick="darResetFilters()">重置</button>
      </div>
      ${fixedParent}
      <div style="font-size:12px;color:#64748b;margin-bottom:6px">共 ${DAR.total} 条（每页 ${DAR.pageSize} 条，单次最多 ${meta ? meta.maxPageSize : 200} 条；无逐行查询）</div>
      <div id="dar-table">${rows || '<div style="padding:20px;text-align:center;color:#94a3b8">暂无附件引用记录</div>'}</div>
      <div style="display:flex;justify-content:flex-end;gap:8px;margin-top:10px">
        <button class="btn btn-neutral btn-sm" onclick="darPage(-1)">上一页</button>
        <button class="btn btn-neutral btn-sm" onclick="darPage(1)">下一页</button>
      </div>
      ${DAR.form ? darFormHtml() : ''}
      ${DAR.voidId ? darVoidHtml() : ''}
    </div>`;
}

function darFixedParentSelect() {
  const options = DAR.parents.map(p =>
    `<option value="${p.parentId}" ${String(p.parentId) === DAR.parentId ? 'selected' : ''}>${darEsc(p.summaryText)}</option>`).join('');
  return `<div style="background:#f1f5f9;border-radius:8px;padding:8px 10px;font-size:12px;color:#334155;margin-bottom:8px">
      当前单据：<strong>${darEsc(DAR.parentNo || DAR.parentId)}</strong>（${darEsc(DAR.filters.parentType)}）
      —— 只显示本单据的附件引用；新增时父单据已固定。
      ${options ? `<br>同类型其他单据：<select id="dar-pswitch" onchange="darSwitchParent()">${options}</select>` : ''}
    </div>`;
}

/* 单条记录：所有字段转义后显示；引用标识按服务端安全标注显示，绝不拼成链接或标记 */
function darRowHtml(row) {
  const statusColor = row.isVoided ? '#94a3b8' : '#0f766e';
  const referenceCell = row.referenceAvailable
    ? `<code>${darEsc(row.referenceId)}</code>`
    : '<span style="color:#b91c1c">不可用（不透明标识口径不满足）</span>';
  const unsafeNote = row.referenceAvailable ? ''
    : `<div style="color:#b91c1c;font-size:11px">${darEsc(row.referenceUnavailableText)}</div>`;
  const parentNote = row.parentAvailable ? ''
    : `<div style="color:#b91c1c;font-size:11px">${darEsc(row.parentAvailabilityText)}</div>`;

  return `
  <div style="border:1px solid #e2e8f0;border-radius:10px;padding:8px 10px;margin-bottom:8px;background:${row.isVoided ? '#f8fafc' : '#fff'}">
    <div style="display:flex;justify-content:space-between;gap:10px;flex-wrap:wrap">
      <div style="font-size:13px">
        <span style="color:${statusColor};font-weight:600">[${darEsc(row.categoryText)}] ${darEsc(row.displayName)}</span>
        <span style="color:#64748b">· ${darEsc(row.parentTypeText)} ${darEsc(row.parentNo)} · ${darEsc(row.statusText)}</span>
        ${parentNote}
      </div>
      <div style="font-size:12px;color:#475569">
        ${row.isActive ? `<button class="btn btn-neutral btn-sm" onclick="darOpenVoid(${row.id})">作废</button>` : ''}
      </div>
    </div>
    <div style="font-size:12px;color:#334155;margin-top:4px">
      引用标识：${referenceCell} · 内容类型：${darEsc(row.contentType || '未提供')} · 大小：${darEsc(row.sizeText)}
      · 校验和：${darEsc(row.checksum || '未提供')}
    </div>
    ${unsafeNote}
    <div style="font-size:11px;color:#64748b;margin-top:4px">
      元数据引用（不代表文件可用）：不上传、不下载、不预览、不抓取；登记时间 ${darEsc(row.registeredAt)}；
      来源授权确认：${row.sourceAuthorizationAcknowledged ? '已确认' : '未确认'}（${darEsc(row.sourceAuthorizationNote || '无说明')}）
      ${row.authorizedBy ? '· 确认人 ' + darEsc(row.authorizedBy) : ''} · 确认时间 ${darEsc(row.authorizedAt)}
    </div>
    ${row.notes ? `<div style="font-size:11px;color:#64748b">备注：${darEsc(row.notes)}</div>` : ''}
    ${row.isVoided ? `<div style="font-size:11px;color:#b91c1c">已作废：${darEsc(row.voidReason)}（${darEsc(row.voidedAt)}；原始元数据与授权留痕保留）</div>` : ''}
  </div>`;
}

/* ==================== 筛选与分页交互 ==================== */

function darFilterTypeChanged() {
  DAR.filters.parentType = document.getElementById('dar-ftype').value;
  DAR.parentId = '';
  DAR.parentNo = '';
  DAR.page = 1;
  darLoadParentOptions('').then(() => darLoadList()).then(() => darRender());
}

function darFiltersChanged() {
  DAR.filters.category = document.getElementById('dar-fcategory').value;
  DAR.filters.status = document.getElementById('dar-fstatus').value;
  DAR.filters.keyword = document.getElementById('dar-fkeyword').value || '';
  DAR.page = 1;
  darLoadList().then(() => darRender());
}

function darResetFilters() {
  DAR.filters.category = '';
  DAR.filters.status = '';
  DAR.filters.keyword = '';
  DAR.parentId = '';
  DAR.parentNo = '';
  DAR.page = 1;
  darLoadList().then(() => darRender());
}

function darSwitchParent() {
  DAR.parentId = document.getElementById('dar-pswitch').value;
  DAR.parentNo = '';
  DAR.page = 1;
  darLoadList().then(() => darRender());
}

function darPage(delta) {
  const next = DAR.page + delta;
  if (next < 1) return;
  if (delta > 0 && (next - 1) * DAR.pageSize >= DAR.total) return;
  DAR.page = next;
  darLoadList().then(() => darRender());
}

/* ==================== 作废（保留历史，不做删除） ==================== */

function darOpenVoid(id) {
  DAR.voidId = id;
  DAR.voidReason = '';
  darRender();
}

function darCloseVoid() {
  DAR.voidId = null;
  DAR.voidReason = '';
  darRender();
}

async function darSubmitVoid() {
  const reason = document.getElementById('dar-void-reason').value || '';
  if (!reason.trim()) { toast('请填写作废原因', 'error'); return; }
  try {
    const res = await api('/api/document-attachment-references/' + DAR.voidId + '/void', 'POST', { reason: reason });
    toast(res.message || '附件引用已作废');
    DAR.voidId = null;
    await darLoadList();
    darRender();
  } catch (e) {
    toast('作废失败：' + e.message, 'error');
  }
}

function darVoidHtml() {
  return `
  <div style="border:1px solid #fecaca;background:#fef2f2;border-radius:10px;padding:10px;margin-top:10px">
    <div style="font-weight:600;font-size:13px;color:#b91c1c">作废附件引用（保留原始元数据与授权留痕，不做硬删除）</div>
    <div style="font-size:12px;color:#7f1d1d;margin:6px 0">作废原因（必填）：</div>
    <input id="dar-void-reason" value="${darEsc(DAR.voidReason)}" style="width:100%" placeholder="例如：引用标识填错 / 来源未获授权 / 重复登记">
    <div style="margin-top:8px;display:flex;gap:8px">
      <button class="btn btn-primary btn-sm" onclick="darSubmitVoid()">确认作废</button>
      <button class="btn btn-neutral btn-sm" onclick="darCloseVoid()">取消</button>
    </div>
  </div>`;
}

/* ==================== 新增表单（仅元数据；无上传控件） ==================== */

async function darOpenForm() {
  const meta = DAR.metadata;
  const parentType = DAR.filters.parentType;
  if (!parentType) {
    toast('请先选择父单据类型（销售订单 / 采购订单 / 装柜清单 / 出口单证）', 'error');
    return;
  }

  await darLoadParentOptions(DAR.parentKeyword);
  DAR.form = {
    parentType: parentType,
    parentId: DAR.parentId || (DAR.parents.length ? String(DAR.parents[0].parentId) : ''),
    category: (meta && meta.categories.length) ? meta.categories[0].value : '',
    displayName: '', referenceId: '', contentType: '', sizeBytes: '', checksum: '',
    notes: '', acknowledged: false, authorizationNote: '', authorizedBy: ''
  };
  darRender();
}

function darCloseForm() {
  DAR.form = null;
  darRender();
}

function darFormReadInputs() {
  const f = DAR.form;
  f.parentId = document.getElementById('dar-parent').value;
  f.category = document.getElementById('dar-category').value;
  f.displayName = document.getElementById('dar-display-name').value;
  f.referenceId = document.getElementById('dar-reference-id').value;
  f.contentType = document.getElementById('dar-content-type').value;
  f.sizeBytes = document.getElementById('dar-size').value;
  f.checksum = document.getElementById('dar-checksum').value;
  f.notes = document.getElementById('dar-notes').value;
  f.acknowledged = document.getElementById('dar-ack').checked;
  f.authorizationNote = document.getElementById('dar-ack-note').value;
  f.authorizedBy = document.getElementById('dar-authorized-by').value;
  return f;
}

async function darSubmitForm() {
  if (DAR.busy) return;
  const f = darFormReadInputs();
  if (!f.parentId) { toast('请选择父单据', 'error'); return; }
  if (!f.acknowledged) {
    toast('请先勾选来源授权确认（该确认不授予存储访问权，只表示你声明有权引用该来源）', 'error');
    return;
  }

  const payload = {
    parentType: f.parentType,
    parentId: Number(f.parentId),
    category: f.category,
    displayName: f.displayName,
    referenceId: f.referenceId,
    contentType: f.contentType,
    sizeBytes: f.sizeBytes === '' ? 0 : Number(f.sizeBytes),
    checksum: f.checksum,
    notes: f.notes,
    sourceAuthorizationAcknowledged: f.acknowledged,
    sourceAuthorizationNote: f.authorizationNote,
    authorizedBy: f.authorizedBy
  };

  DAR.busy = true;
  try {
    const res = await api('/api/document-attachment-references', 'POST', payload);
    toast(res.message || '附件引用已登记（仅元数据）');
    DAR.form = null;
    await darLoadList();
    darRender();
  } catch (e) {
    toast('登记失败：' + e.message, 'error');
  } finally {
    DAR.busy = false;
  }
}

function darFormHtml() {
  const f = DAR.form;
  const meta = DAR.metadata || { categories: [], parentTypes: [] };
  const parentOptions = DAR.parents.map(p =>
    `<option value="${p.parentId}" ${String(p.parentId) === String(f.parentId) ? 'selected' : ''}>${darEsc(p.summaryText)}</option>`).join('');

  return `
  <div style="border:1px solid #bfdbfe;background:#eff6ff;border-radius:10px;padding:10px;margin-top:10px">
    <div style="font-weight:600;font-size:13px;color:#1d4ed8">登记附件引用（仅元数据；不上传、不抓取任何文件）</div>
    <div style="display:grid;grid-template-columns:repeat(auto-fit,minmax(260px,1fr));gap:8px;margin-top:8px;font-size:12px">
      <label>父单据类型<br>
        <input value="${darEsc(f.parentType)}" disabled style="width:100%"></label>
      <label>父单据<br>
        <select id="dar-parent" style="width:100%">${parentOptions || '<option value="">（无可用父单据）</option>'}</select></label>
      <label>分类<br>
        <select id="dar-category" style="width:100%">
          ${meta.categories.map(c => `<option value="${darEsc(c.value)}" ${c.value === f.category ? 'selected' : ''}>${darEsc(c.label)}</option>`).join('')}
        </select></label>
      <label>显示名（纯文本标签）<br>
        <input id="dar-display-name" value="${darEsc(f.displayName)}" style="width:100%" placeholder="例如：2026 秋款采购合同（扫描件）"></label>
      <label>不透明引用标识<br>
        <input id="dar-reference-id" value="${darEsc(f.referenceId)}" style="width:100%" placeholder="例如：att-2026-000123 或 GUID（不接受链接 / 路径）"></label>
      <label>内容类型（可选）<br>
        <input id="dar-content-type" value="${darEsc(f.contentType)}" style="width:100%" placeholder="例如：application/pdf"></label>
      <label>字节大小（可选，0 = 未提供）<br>
        <input id="dar-size" type="number" min="0" value="${darEsc(f.sizeBytes)}" style="width:100%"></label>
      <label>校验和（可选，16~128 位十六进制）<br>
        <input id="dar-checksum" value="${darEsc(f.checksum)}" style="width:100%"></label>
      <label style="grid-column:1/-1">备注<br>
        <input id="dar-notes" value="${darEsc(f.notes)}" style="width:100%"></label>
      <label style="grid-column:1/-1">
        <input id="dar-ack" type="checkbox" ${f.acknowledged ? 'checked' : ''}>
        我确认已获得引用该来源的授权（该确认不授予系统存储访问权，也不代表生产数据已被系统认定合规）</label>
      <label style="grid-column:1/-1">来源授权确认说明（必填）<br>
        <input id="dar-ack-note" value="${darEsc(f.authorizationNote)}" style="width:100%" placeholder="例如：客户邮件确认可引用合同扫描件"></label>
      <label>确认人 / 确认来源（可选）<br>
        <input id="dar-authorized-by" value="${darEsc(f.authorizedBy)}" style="width:100%"></label>
    </div>
    <div style="margin-top:8px;display:flex;gap:8px">
      <button class="btn btn-primary btn-sm" onclick="darSubmitForm()">保存登记</button>
      <button class="btn btn-neutral btn-sm" onclick="darCloseForm()">取消</button>
    </div>
    <div style="font-size:11px;color:#1e40af;margin-top:6px">
      ${darEsc(meta.referencePolicyText || '')}<br>${darEsc(meta.sizePolicyText || '')}<br>${darEsc(meta.authorizationPolicyText || '')}
    </div>
  </div>`;
}
