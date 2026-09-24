/* ==================================================================================
   ====== 装柜出运里程碑证据登记册（ERP-058）— 挂 ERP-057 出运引用之下的只追加事件留痕 ======
   ==================================================================================
   定位：为**既有** ERP-057 出运引用证据（订柜信息 / 预装柜单 / 装柜清单的显式出运引用）
         登记用户录入的操作性**里程碑证据**：实际开船 / 实际到港 / 查验 / 放行，
         让「这票货实际什么时候走、什么时候到、有没有查验 / 放行」可追溯。
   为什么挂在出运引用之下（ERP-058 审计结论）：ERP-057 已建立唯一权威的出运引用登记册，
         因此本登记册只按显式的**父出运引用 Id** 挂在它下面，不新建出运 / 跟踪主数据、
         不复制订柜跟踪列，也不按柜号 / S/O / B/L 等自由文本匹配任何记录。
   边界（界面侧同样遵守）：
     1. 里程碑只是用户录入的**仓库操作性证据**：不是承运人 / 海关 / 货代的确认或回执，
        不是报关或放行结论，也不构成清关许可、交付承诺、法律依据或出运许可；
     2. 只读写 ContainerShipmentMilestones 一张表：不改写父出运引用，也不改写订柜信息 /
        预装柜单 / 装柜清单的任何列、状态与工作流，不会自动推进装柜 / 报关 / 出运状态，
        也不改写订单、库存与库存成本、库存流水、单证中心、发票、费用与分摊、收付款、
        税务与结算记录，更不会轮询承运人 / 海关 / 货代或任何外部系统；
     3. 事件时间必填；来源说明 / 备注 / 记录人留空一律显示「未知」，
        不按计划开船 / 计划到港时间、单据状态或自由文本推断；
     4. 同一父记录 + 事件类型 + 事件时间不允许重复有效登记（重复直接拒绝，不静默合并）；
     5. 更正走显式**作废**（必填原因，保留原始类型 / 时间 / 来源），不提供硬删除与静默改写。
   文案与服务端 ContainerShipmentMilestoneRules / ContainerShipmentMilestoneService 保持一致。
   ================================================================================== */

let CSM = {
  view: 'list', list: [], total: 0, page: 1, pageSize: 50,
  filters: { referenceId: '', eventType: '', status: '', keyword: '' },
  parents: [], parentKeyword: '',
  current: null, form: null, detail: null, voidReason: '', hint: '', error: ''
};

/* 事件类型 allowlist（与后端 ContainerShipmentMilestoneRules 完全一致：其他取值一律拒绝） */
const CSM_EVENT_TYPES = [
  { value: 'actual-departure', label: '实际开船 / 离港' },
  { value: 'actual-arrival', label: '实际到港 / 抵达' },
  { value: 'inspection', label: '查验' },
  { value: 'customs-release', label: '放行' },
];

/* 未知文案（与后端 ContainerShipmentMilestoneRules.UnknownText 一致） */
const CSM_UNKNOWN = '未知';

function csmEventTypeText(t) {
  const v = String(t || '').trim().toLowerCase();
  const hit = CSM_EVENT_TYPES.find(e => e.value === v);
  return hit ? hit.label : CSM_UNKNOWN;
}

/* 查验 / 放行类事件：界面必须显式标注「不是海关决定 / 不是放行许可」 */
function csmIsInspectionOrRelease(t) {
  const v = String(t || '').trim().toLowerCase();
  return v === 'inspection' || v === 'customs-release';
}

/* 只读文本：空值显示「未知」（已转义） */
function csmText(v) {
  const s = String(v === null || v === undefined ? '' : v).trim();
  return s ? escapeHtml(s) : CSM_UNKNOWN;
}

/* 只读事件时间：空值显示「未知」，不回落为空白或今天 */
function csmDateTime(v) {
  if (!v) return CSM_UNKNOWN;
  return escapeHtml(String(v).replace('T', ' ').slice(0, 16));
}

/* 父记录摘要（父出运引用被删除 / 不存在时显式标注不可用，历史里程碑仍可读） */
function csmParentCell(row) {
  if (!row.parentAvailable) {
    return `<span class="status status-warning">父记录不可用</span>`
      + `<div class="text-muted">${escapeHtml(row.parentAvailabilityText || '')}</div>`;
  }
  return `${escapeHtml(row.parentSourceTypeText || CSM_UNKNOWN)}`
    + `<div class="text-muted">${escapeHtml(row.parentSourceNo || '')} · `
    + `${csmText(row.parentContainerNo)} · ${escapeHtml(row.parentStatusText || '')}</div>`;
}

function csmStatusBadge(row) {
  if (row.isVoided) {
    return '<span class="status status-neutral">已作废</span>'
      + `<div class="text-muted">${escapeHtml(row.voidReason || '')}</div>`;
  }
  return '<span class="status status-success">已登记</span>'
    + `<div class="text-muted">登记 ${fmtDate(row.recordedAt)}</div>`;
}

/* 打开登记册：ERP-057 出运引用台账的行操作会传入该引用 Id（自动按父出运引用预筛选） */
async function openContainerShipmentMilestones(referenceId) {
  CSM = {
    view: 'list', list: [], total: 0, page: 1, pageSize: 50,
    filters: { referenceId: '', eventType: '', status: '', keyword: '' },
    parents: [], parentKeyword: '',
    current: null, form: null, detail: null, voidReason: '', hint: '', error: ''
  };

  const modal = document.getElementById('modal');
  modal.innerHTML = '<div style="padding:40px;text-align:center;color:#64748b">正在加载…</div>';
  modal.style.display = 'block';

  if (referenceId) {
    CSM.filters.referenceId = String(referenceId);
    CSM.hint = `从出运引用台账第 ${referenceId} 行进入：台账已按该父出运引用预筛选。`;
  }

  await csmLoadParents('');
  await csmLoadList();
  csmRender();
}

/* 可挂里程碑的出运引用候选（只读、有界；用于显式选择父记录，不按柜号 / 单号猜记录） */
async function csmLoadParents(keyword) {
  const params = ['take=200'];
  if (keyword) params.push('keyword=' + encodeURIComponent(keyword));
  try {
    CSM.parents = await api('/api/container/shipment-milestones/parent-candidates?' + params.join('&')) || [];
  } catch (e) {
    CSM.parents = [];
    toast('出运引用候选加载失败：' + e.message, 'error');
  }
}

async function csmLoadList() {
  const f = CSM.filters;
  const params = ['page=' + CSM.page, 'pageSize=' + CSM.pageSize];
  if (f.referenceId) params.push('containerShipmentReferenceId=' + encodeURIComponent(f.referenceId));
  if (f.eventType) params.push('eventType=' + encodeURIComponent(f.eventType));
  if (f.status) params.push('status=' + encodeURIComponent(f.status));
  if (f.keyword) params.push('keyword=' + encodeURIComponent(f.keyword));

  try {
    const page = await api('/api/container/shipment-milestones?' + params.join('&'));
    CSM.list = (page && page.items) || [];
    CSM.total = (page && page.total) || 0;
  } catch (e) {
    CSM.list = [];
    CSM.total = 0;
    toast('里程碑台账加载失败：' + e.message, 'error');
  }
}

function csmRender() {
  const modal = document.getElementById('modal');
  const body = CSM.view === 'form' ? csmFormView()
    : CSM.view === 'detail' ? csmDetailView()
    : CSM.view === 'void' ? csmVoidView()
    : csmListView();

  modal.innerHTML = `
    <div style="max-width:1360px;margin:2vh auto;background:#fff;border-radius:14px;padding:16px 18px;max-height:94vh;overflow:auto;box-shadow:0 20px 60px rgba(0,0,0,.25)">
      <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:8px">
        <h3 style="margin:0">🛣 装柜出运里程碑证据登记册
          <span style="font-size:13px;color:#64748b;font-weight:400;margin-left:10px">
            登记「这票货实际什么时候走 / 到、有没有查验 / 放行」的操作性证据（不是承运人 / 海关 / 货代确认，不是放行结论）</span></h3>
        <div style="display:flex;gap:8px">
          <button class="btn btn-neutral btn-sm" onclick="csmReturnToReferences()">← 出运引用台账</button>
          <button class="btn btn-neutral btn-sm" onclick="closeModal()">关闭</button>
        </div>
      </div>
      <div style="padding:8px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;color:#475569;font-size:13px;margin-bottom:10px">
        只写本登记册一张表：不改写出运引用与订柜信息 / 预装柜单 / 装柜清单的状态与任何列，也不会自动推进装柜、报关或出运；
        来源说明 / 备注 / 记录人留空一律显示「未知」，不按计划时间、单据状态或自由文本推断。
      </div>
      ${CSM.hint ? `<div style="padding:8px 10px;background:#eff6ff;border:1px solid #bfdbfe;border-radius:8px;color:#1e3a8a;font-size:13px;margin-bottom:10px">${escapeHtml(CSM.hint)}</div>` : ''}
      ${CSM.error ? `<div style="padding:8px 10px;background:#fef2f2;border:1px solid #fecaca;border-radius:8px;color:#991b1b;font-size:13px;margin-bottom:10px">${escapeHtml(CSM.error)}</div>` : ''}
      ${body}
    </div>`;
}

/* 返回 ERP-057 出运引用台账（CSR 状态仍在内存中，直接重绘即可） */
function csmReturnToReferences() {
  if (typeof csrRender === 'function' && typeof CSR !== 'undefined') { csrRender(); return; }
  closeModal();
}

/* ==================== 台账列表 ==================== */

function csmListView() {
  const f = CSM.filters;

  const rows = CSM.list.map(row => `
    <tr>
      <td>${csmDateTime(row.eventAt)}
        <div class="text-muted">${escapeHtml(row.eventTypeText || csmEventTypeText(row.eventType))}</div></td>
      <td>${csmParentCell(row)}</td>
      <td>${csmText(row.sourceDescription)}</td>
      <td>${csmText(row.notes)}</td>
      <td>${csmText(row.recordedBy)}
        <div class="text-muted">登记 ${fmtDate(row.recordedAt)}</div></td>
      <td>${csmStatusBadge(row)}</td>
      <td>${escapeHtml(row.evidenceCategoryText || '')}</td>
      <td style="white-space:nowrap">
        <button class="btn btn-neutral btn-sm" onclick="csmOpenDetail(${row.id})">详情</button>
        ${row.isRecorded ? `<button class="btn btn-neutral btn-sm" onclick="csmOpenVoid(${row.id})">作废</button>` : ''}
      </td>
    </tr>`).join('');

  return `
    <div style="display:grid;grid-template-columns:repeat(5,1fr);gap:8px;margin-bottom:8px">
      <div><label class="ea-lb">事件类型</label>
        <select style="width:100%" onchange="CSM.filters.eventType=this.value">
          <option value="">（全部类型）</option>
          ${CSM_EVENT_TYPES.map(e => `<option value="${e.value}" ${f.eventType === e.value ? 'selected' : ''}>${e.label}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">状态</label>
        <select style="width:100%" onchange="CSM.filters.status=this.value">
          <option value="">（全部，含已作废历史）</option>
          <option value="1" ${String(f.status) === '1' ? 'selected' : ''}>已登记</option>
          <option value="2" ${String(f.status) === '2' ? 'selected' : ''}>已作废</option>
        </select></div>
      <div><label class="ea-lb">父出运引用 Id</label>
        <input style="width:100%" value="${escapeHtml(f.referenceId)}"
          onchange="CSM.filters.referenceId=this.value" placeholder="留空 = 全部父记录"></div>
      <div><label class="ea-lb">关键字（来源说明 / 备注 / 记录人）</label>
        <input style="width:100%" value="${escapeHtml(f.keyword)}" onchange="CSM.filters.keyword=this.value"
          placeholder="如 船公司网站截图"></div>
      <div style="display:flex;align-items:flex-end;gap:8px">
        <button class="btn btn-neutral btn-sm" onclick="csmSearch()">查询</button>
        <button class="btn btn-primary btn-sm" onclick="csmOpenForm()">＋ 登记里程碑证据</button>
      </div>
    </div>
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <div style="font-size:13px;color:#475569">共 ${CSM.total} 条里程碑证据（按事件时间倒序）
        ${f.referenceId ? `（已按父出运引用 Id=${escapeHtml(f.referenceId)} 预筛选）` : ''}</div>
      <div style="display:flex;gap:8px">
        ${f.referenceId ? '<button class="btn btn-neutral btn-sm" onclick="csmClearReferenceFilter()">清除父记录筛选</button>' : ''}
        <button class="btn btn-neutral btn-sm" onclick="csmPage(-1)">上一页</button>
        <span style="font-size:13px;color:#475569;line-height:32px">第 ${CSM.page} 页</span>
        <button class="btn btn-neutral btn-sm" onclick="csmPage(1)">下一页</button>
      </div>
    </div>
    <div class="table-wrap" style="max-height:52vh;overflow:auto">
      <table>
        <thead><tr>
          <th>事件时间 · 类型</th><th>父出运引用</th><th>来源说明</th><th>备注</th><th>记录人 · 登记时间</th>
          <th>状态</th><th>证据性质</th><th>操作</th>
        </tr></thead>
        <tbody>${rows || '<tr><td colspan="8" class="text-muted">暂无里程碑证据（历史出运引用不需要任何回填，可直接登记一条）</td></tr>'}</tbody>
      </table>
    </div>`;
}

async function csmSearch() { CSM.page = 1; CSM.view = 'list'; await csmLoadList(); csmRender(); }

async function csmPage(delta) {
  const next = CSM.page + delta;
  if (next < 1) return;
  CSM.page = next;
  await csmLoadList();
  csmRender();
}

function csmClearReferenceFilter() {
  CSM.filters.referenceId = '';
  CSM.hint = '';
  csmSearch();
}

function csmBackToList() {
  CSM.view = 'list';
  CSM.current = null;
  CSM.form = null;
  CSM.detail = null;
  CSM.error = '';
  csmRender();
}

function csmSetField(key, value) { if (CSM.form) CSM.form[key] = value; }

/* ==================== 登记表单（只追加：不提供修改接口） ==================== */

async function csmOpenForm() {
  CSM.error = '';
  CSM.current = null;
  CSM.parentKeyword = '';
  CSM.form = {
    referenceId: CSM.filters.referenceId || '',
    eventType: '',
    eventAt: '',
    sourceDescription: '',
    notes: '',
    recordedBy: ''
  };
  if (!CSM.parents || CSM.parents.length === 0) await csmLoadParents('');
  CSM.view = 'form';
  csmRender();
}

async function csmParentSearch() {
  if (!CSM.form) return;
  await csmLoadParents(CSM.parentKeyword);
  csmRender();
}

function csmFormView() {
  const f = CSM.form;
  if (!f) return '<div class="text-muted">请选择要登记里程碑的父出运引用。</div>';

  const parentOptions = (CSM.parents || []).map(p =>
    `<option value="${p.containerShipmentReferenceId}" ${String(p.containerShipmentReferenceId) === String(f.referenceId) ? 'selected' : ''}>`
    + `${escapeHtml(p.sourceTypeText || '')} · ${escapeHtml(p.sourceNo || '')} · ${escapeHtml(p.containerNo || '')}`
    + ` · ${escapeHtml(p.sourceStatusText || '')}（里程碑 ${p.milestoneCount || 0} 条 / 有效 ${p.activeMilestoneCount || 0} 条）</option>`).join('');

  const eventHint = csmIsInspectionOrRelease(f.eventType)
    ? '查验 / 放行只是用户录入的仓库操作性证据：不是海关决定、不是查验或放行结论，也不代表允许出运。'
    : '开船 / 到港只是用户录入的操作性事件证据：不是承运人确认或航行回执，也不构成交付承诺。';

  return `
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <h4 style="margin:0">登记里程碑证据（只追加，不提供修改接口）</h4>
      <button class="btn btn-neutral btn-sm" onclick="csmBackToList()">← 返回台账</button>
    </div>
    <div style="display:grid;grid-template-columns:repeat(4,1fr);gap:8px;margin-bottom:8px">
      <div style="grid-column:span 2"><label class="ea-lb">父出运引用（显式选择，不按柜号 / 单号猜记录） *</label>
        <select style="width:100%" onchange="csmSetField('referenceId', this.value)">
          <option value="">（请选择）</option>
          ${parentOptions}
        </select></div>
      <div style="grid-column:span 2"><label class="ea-lb">候选筛选（源单号 / 柜号 / B/L / S/O / 承运人）</label>
        <div style="display:flex;gap:6px">
          <input style="flex:1" value="${escapeHtml(CSM.parentKeyword || '')}"
            onchange="CSM.parentKeyword=this.value" placeholder="留空 = 最近 200 条">
          <button class="btn btn-neutral btn-sm" onclick="csmParentSearch()">查询</button>
        </div></div>
      <div><label class="ea-lb">事件类型 *</label>
        <select style="width:100%" onchange="csmSetField('eventType', this.value)">
          <option value="">（请选择）</option>
          ${CSM_EVENT_TYPES.map(e => `<option value="${e.value}" ${f.eventType === e.value ? 'selected' : ''}>${e.label}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">事件发生时间 *（必填，不按计划时间推断）</label>
        <input type="datetime-local" style="width:100%" value="${escapeHtml(f.eventAt || '')}"
          onchange="csmSetField('eventAt', this.value)"></div>
      <div><label class="ea-lb">来源说明（留空 = 未知）</label>
        <input style="width:100%" value="${escapeHtml(f.sourceDescription || '')}"
          onchange="csmSetField('sourceDescription', this.value)" placeholder="如 船公司网站截图 / 货代邮件"></div>
      <div><label class="ea-lb">记录人（留空 = 未知）</label>
        <input style="width:100%" value="${escapeHtml(f.recordedBy || '')}"
          onchange="csmSetField('recordedBy', this.value)"></div>
    </div>
    <div style="margin-bottom:8px"><label class="ea-lb">备注</label>
      <textarea style="width:100%;height:56px" onchange="csmSetField('notes', this.value)">${escapeHtml(f.notes || '')}</textarea></div>
    <div style="padding:8px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;color:#475569;font-size:13px;margin-bottom:8px">
      ${escapeHtml(eventHint)}<br>
      保存只登记仓库操作性证据：不改写出运引用与装柜三单，也不自动推进装柜 / 报关 / 出运状态；
      同一父记录 + 事件类型 + 事件时间重复登记会被拒绝（不静默合并）。
    </div>
    <div style="display:flex;justify-content:flex-end;gap:8px">
      <button class="btn btn-neutral" onclick="csmBackToList()">取消</button>
      <button class="btn btn-primary" onclick="csmSaveForm()">登记里程碑证据</button>
    </div>`;
}

async function csmSaveForm() {
  const f = CSM.form;
  if (!f) return;
  if (!f.referenceId) { toast('请选择父出运引用', 'error'); return; }
  if (!f.eventType) { toast('请选择事件类型', 'error'); return; }
  if (!f.eventAt) { toast('请填写事件发生时间（系统不会按计划时间推断）', 'error'); return; }

  const payload = {
    containerShipmentReferenceId: Number(f.referenceId),
    eventType: f.eventType,
    eventAt: f.eventAt,
    sourceDescription: f.sourceDescription || '',
    notes: f.notes || '',
    recordedBy: f.recordedBy || ''
  };

  try {
    await api('/api/container/shipment-milestones', 'POST', payload);
    toast('里程碑证据已登记（不推进任何业务状态）');
    CSM.filters.referenceId = f.referenceId;
    CSM.current = null;
    CSM.form = null;
    CSM.view = 'list';
    CSM.page = 1;
    await csmLoadParents('');
    await csmLoadList();
    csmRender();
  } catch (e) {
    CSM.error = '登记失败：' + e.message;
    csmRender();
    toast('登记失败：' + e.message, 'error');
  }
}

/* ==================== 详情（含证据性质与父记录可用性） ==================== */

async function csmOpenDetail(id) {
  try {
    const detail = await api('/api/container/shipment-milestones/' + id);
    CSM.detail = detail;
    CSM.current = detail ? detail.milestone : null;
    CSM.view = 'detail';
    csmRender();
  } catch (e) {
    toast('里程碑详情加载失败：' + e.message, 'error');
  }
}

function csmDetailView() {
  const d = CSM.detail;
  if (!d || !d.milestone) return '<div class="text-muted">请选择里程碑证据。</div>';
  const m = d.milestone;

  const pairs = [
    ['父出运引用', `${escapeHtml(m.parentSourceTypeText || CSM_UNKNOWN)} · ${escapeHtml(m.parentSourceNo || '')} · `
      + `${csmText(m.parentContainerNo)} · ${escapeHtml(m.parentStatusText || '')}`],
    ['父记录可用性', escapeHtml(m.parentAvailabilityText || '')],
    ['事件类型', escapeHtml(m.eventTypeText || csmEventTypeText(m.eventType))],
    ['事件发生时间', csmDateTime(m.eventAt)],
    ['来源说明', csmText(m.sourceDescription)],
    ['备注', csmText(m.notes)],
    ['记录人', csmText(m.recordedBy)],
    ['登记时间', fmtDate(m.recordedAt)],
    ['状态', `${m.isVoided ? '已作废' : '已登记'}`],
    ['作废', m.isVoided ? `${csmDateTime(m.voidedAt)} · ${escapeHtml(m.voidReason || '')}` : CSM_UNKNOWN],
    ['证据性质', escapeHtml(m.evidenceCategoryText || '')],
  ];

  return `
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <h4 style="margin:0">里程碑证据详情</h4>
      <button class="btn btn-neutral btn-sm" onclick="csmBackToList()">← 返回台账</button>
    </div>
    <div class="table-wrap" style="max-height:52vh;overflow:auto;margin-bottom:8px">
      <table><tbody>
        ${pairs.map(([label, value]) => `<tr><th style="width:180px;text-align:left">${label}</th><td>${value}</td></tr>`).join('')}
      </tbody></table>
    </div>
    <div style="padding:8px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;color:#475569;font-size:13px">
      ${escapeHtml(d.infoText || '')}<br>
      ${escapeHtml(d.boundaryText || '')}
      <br>里程碑证据只追加、不可修改：更正请显式作废并重新登记一条，已作废证据保留原始类型 / 时间 / 来源可读。
    </div>`;
}

/* ==================== 作废（保留原始证据与留痕） ==================== */

function csmOpenVoid(id) {
  const pools = [
    CSM.list || [],
    (CSM.detail && CSM.detail.milestone) ? [CSM.detail.milestone] : [],
    CSM.current ? [CSM.current] : []
  ];
  let row = null;
  for (const pool of pools) {
    const hit = pool.find(x => x.id === id);
    if (hit) { row = hit; break; }
  }
  if (!row) { toast('未找到该里程碑证据，请刷新台账后重试', 'error'); return; }

  CSM.current = row;
  CSM.voidReason = '';
  CSM.view = 'void';
  csmRender();
}

function csmVoidView() {
  const row = CSM.current;
  if (!row) return '<div class="text-muted">请选择里程碑证据。</div>';

  const warning = csmIsInspectionOrRelease(row.eventType)
    ? '作废<b>不会</b>产生任何海关状态或放行结论：本登记册只记录仓库操作性证据，不是海关决定，也不代表允许出运。'
    : '作废<b>不会</b>改写承运人 / 航行信息：本登记册只记录用户录入的操作性证据，不是承运人确认。';

  return `
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <h4 style="margin:0">作废里程碑证据</h4>
      <button class="btn btn-neutral btn-sm" onclick="csmBackToList()">← 返回台账</button>
    </div>
    <div style="display:grid;grid-template-columns:repeat(4,1fr);gap:8px;padding:8px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;font-size:13px;margin-bottom:8px">
      <div>父出运引用<div><b>${escapeHtml(row.parentSourceTypeText || CSM_UNKNOWN)}「${escapeHtml(row.parentSourceNo || '')}」</b></div></div>
      <div>事件类型<div><b>${escapeHtml(row.eventTypeText || csmEventTypeText(row.eventType))}</b></div></div>
      <div>事件发生时间<div><b>${csmDateTime(row.eventAt)}</b></div></div>
      <div>来源说明<div>${csmText(row.sourceDescription)}</div></div>
    </div>
    <div style="padding:8px 10px;background:#fef2f2;border:1px solid #fecaca;border-radius:8px;color:#991b1b;font-size:13px">
      作废<b>保留</b>原始事件类型、事件时间、来源说明、备注、记录人与登记时间（不物理删除、不静默改写），
      也<b>不会</b>改写父出运引用与装柜三单、不会自动推进任何业务状态；作废后同一父记录 + 类型 + 时间可重新登记一条新的有效证据。
      ${warning}
      请填写作废原因（必填）。
    </div>
    <div style="margin-top:8px"><label class="ea-lb">作废原因 *</label>
      <textarea id="csm-void-reason" style="width:100%;height:64px" onchange="CSM.voidReason=this.value"
        placeholder="如：事件时间录错 / 来源说明有误 / 重复登记">${escapeHtml(CSM.voidReason || '')}</textarea></div>
    <div style="display:flex;justify-content:flex-end;gap:8px;margin-top:12px">
      <button class="btn btn-neutral" onclick="csmBackToList()">取消</button>
      <button class="btn btn-danger" onclick="csmConfirmVoid()">🗑 确认作废（保留历史）</button>
    </div>`;
}

async function csmConfirmVoid() {
  const row = CSM.current;
  if (!row) return;
  const reason = String(CSM.voidReason || '').trim();
  if (!reason) { toast('请填写作废原因', 'error'); return; }

  try {
    await api('/api/container/shipment-milestones/' + row.id + '/void', 'POST', { reason: reason });
    toast('里程碑证据已作废（原始类型 / 时间 / 来源与作废原因保留可读）');
    CSM.current = null;
    CSM.voidReason = '';
    CSM.view = 'list';
    await csmLoadParents('');
    await csmLoadList();
    csmRender();
  } catch (e) {
    CSM.error = '作废失败：' + e.message;
    csmRender();
    toast('作废失败：' + e.message, 'error');
  }
}
