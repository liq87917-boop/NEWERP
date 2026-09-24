/* ==================================================================================
   ====== 装柜出运证据时间线与跟踪工作台（ERP-059）— **只读**呈现 ======
   ==================================================================================
   定位：把 ERP-057 出运引用证据（出运方式 / S/O / B/L / 港口 / 计划开船·到港时间等）与
         ERP-058 里程碑证据（实际开船 / 实际到港 / 查验 / 放行）合成为**时间线**：
         计划（ETD / ETA）与实际事件**分开标注**，缺失的实际事件显示「无（未登记）」、
         未填写的计划时间显示「未知」，两者绝不互相替代。
   为什么只读（ERP-059 审计结论）：ERP-057 已是出运引用证据的唯一权威登记册、ERP-058 已是里程碑证据的
         唯一登记册，因此本模块**不新建表、不新增列**，只按显式的源记录类型 + Id / 出运引用 Id 读取呈现。
   边界（界面侧同样遵守）：
     1. 只调用 {订柜信息|预装柜单|装柜清单}/{id}/shipment-timeline 与 /api/container/shipment-timeline*，
        不写任何表、不改写装柜三单 / 出运引用 / 里程碑，也不改写订单、库存、单证、发票、费用与财务记录；
     2. 不推断业务状态：缺失事件绝不显示为已开船 / 已到港 / 已清关 / 已放行 / 延误 / 逾期；
     3. 时间差只作**算术证据**（不换算时区、不是承运人 / 海关确认、不是合同 SLA 或索赔依据）；
     4. 已作废里程碑从有效时间线排除，只在「已作废历史」区块按原值显示（含作废原因）；
     5. 未关联有效出运引用时证据一律「未知 / 无」，绝不按柜号 / S/O / B/L 等自由文本猜记录，也不改派。
   文案与服务端 ContainerShipmentTimelineRules / ContainerShipmentTimelineService 保持一致。
   ================================================================================== */

/* 未知 / 缺失文案（与后端 ContainerShipmentTimelineRules.UnknownText / MissingEventText 一致） */
const CST_UNKNOWN = '未知';
const CST_MISSING = '无（未登记）';

/* 装柜三单模块 → 时间线接口（行操作入口） */
const CST_MODULE_ENDPOINTS = {
  booking: '/api/container/bookings',
  'pre-loading': '/api/container/pre-loadings',
  'loading-list': '/api/container/loading-lists',
};

/* 源记录类型选项（与后端 ContainerShipmentReferenceRules.SupportedSourceTypes 一致） */
const CST_SOURCE_TYPES = [
  { value: 'booking', label: '订柜信息' },
  { value: 'pre-loading', label: '预装柜单' },
  { value: 'loading-list', label: '装柜清单' },
];

/* 记录事件类型选项（与后端 ContainerShipmentMilestoneRules.SupportedEventTypes 完全一致） */
const CST_EVENT_TYPES = [
  { value: '', label: '（全部事件类型）' },
  { value: 'actual-departure', label: '实际开船 / 离港' },
  { value: 'actual-arrival', label: '实际到港 / 抵达' },
  { value: 'inspection', label: '查验' },
  { value: 'customs-release', label: '放行' },
];

/* 工作台状态（只读视图状态，不落库） */
let CST = {
  view: 'list', list: [], total: 0, page: 1, pageSize: 50,
  filters: {
    sourceType: '', status: '', containerNo: '', sourceNo: '', billOfLadingNo: '', shippingOrderNo: '',
    departurePort: '', destinationPort: '', plannedDepartureFrom: '', plannedDepartureTo: '',
    eventType: '', eventDateFrom: '', eventDateTo: '', includeVoidedEvents: false, keyword: '',
  },
  detail: null, hint: '',
};

/* ==================== 只读文本 / 日期助手（未知一律显示「未知」，缺失事件显示「无（未登记）」） ==================== */

function cstText(v) {
  const s = String(v === null || v === undefined ? '' : v).trim();
  return s ? escapeHtml(s) : CST_UNKNOWN;
}

function cstDate(v) { return v ? fmtDate(v) : CST_UNKNOWN; }

function cstDateTime(v) {
  if (!v) return CST_UNKNOWN;
  const d = new Date(v);
  if (isNaN(d.getTime())) return escapeHtml(String(v));
  const pad = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/* 时间线条目时间：缺失时按计划 / 实际分别显示「未知」或「无（未登记）」 */
function cstEventAt(e) {
  if (e.hasTimestamp && e.eventAt) return escapeHtml(e.eventAtText || cstDateTime(e.eventAt));
  return e.isPlanned ? CST_UNKNOWN : CST_MISSING;
}

/* 条目种类文案（服务端权威文案优先；缺失时按种类回退，绝不假定为实际事件） */
function cstKindText(e) {
  if (e.kindText) return escapeHtml(e.kindText);
  if (e.kind === 'planned-departure') return '计划开船（ETD，出运引用计划值）';
  if (e.kind === 'planned-arrival') return '计划到港（ETA，出运引用计划值）';
  if (e.kind === 'actual-departure') return '实际开船 / 离港（里程碑证据）';
  if (e.kind === 'actual-arrival') return '实际到港 / 抵达（里程碑证据）';
  if (e.kind === 'inspection') return '查验（里程碑证据）';
  if (e.kind === 'customs-release') return '放行（里程碑证据）';
  return CST_UNKNOWN;
}

function cstSourceTypeText(t) {
  const v = String(t || '').trim().toLowerCase();
  const hit = CST_SOURCE_TYPES.find(s => s.value === v);
  return hit ? hit.label : CST_UNKNOWN;
}

function cstSourceRefText(s) {
  if (!s) return '';
  const typeText = s.sourceTypeText || cstSourceTypeText(s.sourceType);
  const id = s.sourceId ? `Id=${escapeHtml(String(s.sourceId))}` : 'Id=未知';
  return `${escapeHtml(typeText)} · ${id}`;
}

/* ==================== 时间线条目 / 时间差渲染 ==================== */

function cstEventRow(e) {
  const badge = e.isPlanned
    ? '<span class="status status-neutral">计划</span>'
    : (e.isVoided
      ? '<span class="status status-neutral">已作废（历史）</span>'
      : (e.status === 1
        ? '<span class="status status-success">实际证据</span>'
        : '<span class="status status-neutral">历史条目（状态异常，照实呈现）</span>'));
  const source = e.isPlanned
    ? escapeHtml(e.sourceLabelText || '来源：出运引用登记册（ERP-057）计划值')
    : escapeHtml(e.sourceLabelText || '来源：里程碑证据（ERP-058）');
  const evidence = e.isActual
    ? `<div class="text-muted">${escapeHtml(e.evidenceCategoryText || '')}</div>`
    : '';
  const meta = e.isActual
    ? `<div class="text-muted">登记时间 ${cstDateTime(e.recordedAt)} · 状态 ${escapeHtml(e.statusText || '')}`
      + ` · 记录人 ${cstText(e.recordedBy)}</div>`
    : '';
  const voidInfo = e.isVoided
    ? `<div class="text-muted">作废于 ${cstDateTime(e.voidedAt)}：${cstText(e.voidReason)}</div>`
    : '';
  const notes = e.isActual && e.notes
    ? `<div class="text-muted">备注：${escapeHtml(e.notes)}</div>` : '';

  return `<tr>
    <td>${badge}</td>
    <td>${cstKindText(e)}<div class="text-muted">${source}</div></td>
    <td>${cstEventAt(e)}<div class="text-muted">${escapeHtml(e.timestampKindText || '')}</div></td>
    <td>${cstText(e.sourceDescription)}${evidence}</td>
    <td>${meta}${voidInfo}${notes}</td>
  </tr>`;
}

function cstVarianceRows(list) {
  return (list || []).map(v => `<tr>
    <td>${escapeHtml(v.label || '')}</td>
    <td>${escapeHtml(v.plannedLabel || '')}<div class="text-muted">${escapeHtml(v.plannedAtText || CST_UNKNOWN)}</div></td>
    <td>${escapeHtml(v.actualLabel || '')}<div class="text-muted">${escapeHtml(v.actualAtText || CST_MISSING)}</div></td>
    <td>${v.comparable ? '' : '<span class="status status-neutral">无法比较</span>'}
      <div>${escapeHtml(v.text || '')}</div>
      <div class="text-muted">${escapeHtml(v.actualEvidenceText || '')}</div></td>
  </tr>`).join('');
}
/* ==================== 只读详情（行操作入口：装柜三单） ==================== */

/* 行操作：按持久化源记录读取该单的出运证据时间线（只读：不写任何表） */
async function showContainerShipmentTimeline(id) {
  const apiRoot = CST_MODULE_ENDPOINTS[CURRENT_MODULE_CODE];
  if (!apiRoot) { toast('当前模块不支持出运证据时间线', 'error'); return; }
  if (!id) { toast('请先保存单据后再查看出运证据时间线', 'error'); return; }

  const modal = document.getElementById('modal');
  modal.innerHTML = '<div style="padding:40px;text-align:center;color:#64748b">正在加载…</div>';
  modal.style.display = 'flex';

  try {
    const detail = await api(`${apiRoot}/${id}/shipment-timeline?includeHistory=true`);
    CST.fromWorkspace = false;
    CST.detail = detail;
    cstRenderDetail(detail);
  } catch (err) {
    toast(err.message, 'error');
    closeModal();
  }
}

/* 工作台 / 详情内部：按显式出运引用 Id 读取时间线（含历史视图） */
async function cstOpenReference(referenceId) {
  const modal = document.getElementById('modal');
  modal.innerHTML = '<div style="padding:40px;text-align:center;color:#64748b">正在加载…</div>';
  modal.style.display = 'flex';

  try {
    const detail = await api(`/api/container/shipment-timeline/references/${referenceId}?includeHistory=true`);
    CST.fromWorkspace = true;
    CST.detail = detail;
    cstRenderDetail(detail);
  } catch (err) {
    toast(err.message, 'error');
    CST.view = 'list';
    cstRender();
  }
}

/* 出运证据（来自 ERP-057 出运引用）：只读回显，缺失一律「未知」 */
function cstShipmentRows(s) {
  const row = (label, value) => `<tr><th style="width:150px;text-align:left">${label}</th><td>${value}</td></tr>`;
  return row('源记录', `${cstSourceRefText(s)}<div class="text-muted">${escapeHtml(s.sourceNo || '')} · `
    + `${cstDate(s.sourceDate)} · 源单状态 ${escapeHtml(s.sourceStatusText || CST_UNKNOWN)}</div>`)
    + row('柜号', cstText(s.containerNo))
    + row('出运方式', cstText(s.shipmentModeText || s.shipmentMode))
    + row('订舱号 S/O', cstText(s.shippingOrderNo))
    + row('提单号 B/L', cstText(s.billOfLadingNo))
    + row('起运 / 中转 / 目的港', `${cstText(s.departurePort)} → ${cstText(s.transitPort)} → ${cstText(s.destinationPort)}`)
    + row('计划开船 ETD', cstDate(s.plannedDepartureAt))
    + row('计划到港 ETA', cstDate(s.plannedArrivalAt))
    + row('承运人 / 货代', `${cstText(s.carrierName)} / ${cstText(s.forwarderName)}`)
    + row('拖车 / 集卡公司', cstText(s.truckerName))
    + row('引用状态', `${escapeHtml(s.statusText || CST_UNKNOWN)} · 修订 V${escapeHtml(String(s.revisionNo || 0))}`
      + `${s.voidReason ? `<div class="text-muted">作废原因：${escapeHtml(s.voidReason)}</div>` : ''}`)
    + row('可用性', `${escapeHtml(s.referenceAvailabilityText || '')}`
      + `<div class="text-muted">${escapeHtml(s.sourceAvailabilityText || '')}</div>`)
    + row('登记 / 作废时间', `${cstDateTime(s.recordedAt)} / ${s.voidedAt ? cstDateTime(s.voidedAt) : '未作废'}`);
}
/* ==================== 时间线详情（只读渲染） ==================== */

/* 计划 / 实际分开标注 + 时间差算术证据 + 已作废历史视图；界面不提供任何编辑入口 */
function cstRenderDetail(detail) {
  const s = (detail && detail.shipment) || {};
  const events = (detail && detail.events) || [];
  const history = (detail && detail.historyEvents) || [];
  const plannedRows = events.filter(e => e.isPlanned).map(cstEventRow).join('');
  const actualRows = events.filter(e => e.isActual).map(cstEventRow).join('');
  const timelineRows = (plannedRows + actualRows)
    || '<tr><td colspan="5" class="text-muted">没有可显示的时间线条目</td></tr>';
  const historyRows = history.map(cstEventRow).join('')
    || '<tr><td colspan="5" class="text-muted">没有已作废 / 历史异常状态的条目（历史为空）</td></tr>';
  const summary = (s.summary || {});
  const linkedBadge = s.linked
    ? (s.isVoided
      ? '<span class="status status-neutral">出运引用已作废（只作历史呈现）</span>'
      : '<span class="status status-success">已关联有效出运引用</span>')
    : '<span class="status status-neutral">未关联有效出运引用</span>';

  const modal = document.getElementById('modal');
  modal.innerHTML = `
    <div style="max-width:1240px;margin:2vh auto;background:#fff;border-radius:14px;padding:16px 18px;max-height:94vh;overflow:auto;box-shadow:0 20px 60px rgba(0,0,0,.25)">
      <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:8px">
        <h3 style="margin:0">🧭 出运证据时间线（只读）
          <span style="font-size:13px;color:#64748b;font-weight:400;margin-left:10px">
            计划（ETD / ETA）与实际事件分开标注：缺失事件显示「${CST_MISSING}」，未填写计划显示「${CST_UNKNOWN}」</span></h3>
        <div style="display:flex;gap:8px">
          ${CST.fromWorkspace ? '<button class="btn btn-neutral btn-sm" onclick="cstBackToList()">← 返回工作台</button>' : ''}
          <button class="btn btn-neutral btn-sm" onclick="closeModal()">关闭</button>
        </div>
      </div>
      <div style="padding:8px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;color:#475569;font-size:13px;margin-bottom:10px">
        ${escapeHtml(detail && detail.plannedVersusActualText || '')}<br>
        ${escapeHtml(detail && detail.noStatusInferenceText || '')}
      </div>
      ${s.linked ? '' : `<div style="padding:8px 10px;background:#fff7ed;border:1px solid #fed7aa;border-radius:8px;color:#9a3412;font-size:13px;margin-bottom:10px">${escapeHtml(s.notLinkedReason || '')}</div>`}
      <div style="display:flex;gap:10px;align-items:center;margin-bottom:8px">${linkedBadge}
        <span style="font-size:13px;color:#475569">${escapeHtml(summary.timelineStateText || '')}</span></div>

      <h4 style="margin:10px 0 6px">📦 出运证据（来自出运引用登记册 ERP-057，只读）</h4>
      <div class="table-wrap"><table><tbody>${cstShipmentRows(s)}</tbody></table></div>

      <h4 style="margin:14px 0 6px">🕒 有效时间线（计划与实际分开标注；已作废条目不出现在这里）</h4>
      <div class="table-wrap" style="max-height:34vh;overflow:auto">
        <table>
          <thead><tr><th>类型</th><th>条目</th><th>时间</th><th>来源说明</th><th>登记与状态</th></tr></thead>
          <tbody>${timelineRows}</tbody>
        </table>
      </div>
      ${detail && detail.activeEventsTruncated
        ? `<div class="text-muted" style="font-size:13px;margin-top:4px">有效条目超过单次上限 ${escapeHtml(String(detail.eventTakeLimit || ''))} 条：此处只显示前 ${escapeHtml(String(detail.eventTakeLimit || ''))} 条（按事件时间升序），系统不静默截断。</div>` : ''}
      <div id="cst-detail-tail"></div>
    </div>`;
  modal.style.display = 'flex';

  const tail = document.getElementById('cst-detail-tail');
  if (tail) tail.innerHTML = cstDetailTail(detail, historyRows);
}

/* 详情尾部（时间差 + 已作废历史 + 口径文案）：单独渲染以便替换 / 复用 */
function cstDetailTail(detail, historyRows) {
  const d = detail || {};
  return `
    <h4 style="margin:14px 0 6px">📐 时间差（算术证据：仅在计划与实际两条持久化时间戳都存在时给出）</h4>
    <div class="table-wrap">
      <table>
        <thead><tr><th>对比项</th><th>计划值</th><th>实际事件</th><th>比较结果</th></tr></thead>
        <tbody>${cstVarianceRows(d.variances) || '<tr><td colspan="4" class="text-muted">没有可比对的时间戳</td></tr>'}</tbody>
      </table>
    </div>
    <div class="text-muted" style="font-size:13px;margin-top:4px">${escapeHtml((d.variances && d.variances[0] && d.variances[0].basisText) || '')}</div>

    <h4 style="margin:14px 0 6px">🗂 已作废 / 历史异常状态视图（从有效时间线排除，按原值保留可读）</h4>
    <div class="table-wrap" style="max-height:26vh;overflow:auto">
      <table>
        <thead><tr><th>类型</th><th>条目</th><th>时间</th><th>来源说明</th><th>登记与状态</th></tr></thead>
        <tbody>${historyRows}</tbody>
      </table>
    </div>
    ${d.historyTruncated
      ? `<div class="text-muted" style="font-size:13px;margin-top:4px">历史条目超过单次上限 ${escapeHtml(String(d.historyTakeLimit || ''))} 条：此处只显示最近 ${escapeHtml(String(d.historyTakeLimit || ''))} 条，历史并未被删除。</div>`
      : ''}
    <div style="padding:8px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;color:#475569;font-size:13px;margin-top:10px">
      ${escapeHtml(d.evidenceText || '')}<br>${escapeHtml(d.historyText || '')}<br>
      ${escapeHtml(d.infoText || '')}<br>${escapeHtml(d.boundaryText || '')}
    </div>`;
}
/* ==================== 跟踪工作台（只读、分页、显式字段筛选） ==================== */

/* 工具栏入口：装柜三单模块的「🧭 跟踪工作台」（不依赖当前单据） */
async function openContainerShipmentTimelineWorkspace() {
  CST = {
    view: 'list', list: [], total: 0, page: 1, pageSize: 50,
    filters: {
      sourceType: CST_MODULE_ENDPOINTS[CURRENT_MODULE_CODE] ? CURRENT_MODULE_CODE : '',
      status: '', containerNo: '', sourceNo: '', billOfLadingNo: '', shippingOrderNo: '',
      departurePort: '', destinationPort: '', plannedDepartureFrom: '', plannedDepartureTo: '',
      eventType: '', eventDateFrom: '', eventDateTo: '', includeVoidedEvents: false, keyword: '',
    },
    detail: null, fromWorkspace: true,
    hint: '只读工作台：按显式字段筛选出运记录，计划（ETD / ETA）与实际事件分开标注，缺失事件显示「无（未登记）」，'
      + '不推断已开船 / 已到港 / 已清关 / 延误 / 逾期，也不按柜号 / S/O / B/L 等自由文本合并记录。',
  };

  const modal = document.getElementById('modal');
  modal.innerHTML = '<div style="padding:40px;text-align:center;color:#64748b">正在加载…</div>';
  modal.style.display = 'flex';

  await cstLoadList();
  cstRender();
}

function cstSetFilter(key, value) { CST.filters[key] = value; }

async function cstLoadList() {
  const f = CST.filters;
  const params = ['page=' + CST.page, 'pageSize=' + CST.pageSize];
  if (f.sourceType) params.push('sourceType=' + encodeURIComponent(f.sourceType));
  if (f.status) params.push('status=' + encodeURIComponent(f.status));
  if (f.sourceNo) params.push('sourceNo=' + encodeURIComponent(f.sourceNo));
  if (f.containerNo) params.push('containerNo=' + encodeURIComponent(f.containerNo));
  if (f.billOfLadingNo) params.push('billOfLadingNo=' + encodeURIComponent(f.billOfLadingNo));
  if (f.shippingOrderNo) params.push('shippingOrderNo=' + encodeURIComponent(f.shippingOrderNo));
  if (f.departurePort) params.push('departurePort=' + encodeURIComponent(f.departurePort));
  if (f.destinationPort) params.push('destinationPort=' + encodeURIComponent(f.destinationPort));
  if (f.plannedDepartureFrom) params.push('plannedDepartureFrom=' + f.plannedDepartureFrom);
  if (f.plannedDepartureTo) params.push('plannedDepartureTo=' + f.plannedDepartureTo);
  if (f.eventType) params.push('eventType=' + encodeURIComponent(f.eventType));
  if (f.eventDateFrom) params.push('eventDateFrom=' + f.eventDateFrom);
  if (f.eventDateTo) params.push('eventDateTo=' + f.eventDateTo);
  if (f.includeVoidedEvents) params.push('includeVoidedEvents=true');
  if (f.keyword) params.push('keyword=' + encodeURIComponent(f.keyword));

  try {
    const page = await api('/api/container/shipment-timeline?' + params.join('&'));
    CST.list = (page && page.items) || [];
    CST.total = (page && page.total) || 0;
  } catch (e) {
    CST.list = [];
    CST.total = 0;
    toast('跟踪工作台加载失败：' + e.message, 'error');
  }
}

function cstRender() {
  const modal = document.getElementById('modal');
  modal.innerHTML = `
    <div style="max-width:1400px;margin:2vh auto;background:#fff;border-radius:14px;padding:16px 18px;max-height:94vh;overflow:auto;box-shadow:0 20px 60px rgba(0,0,0,.25)">
      <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:8px">
        <h3 style="margin:0">🧭 装柜出运跟踪工作台（只读）
          <span style="font-size:13px;color:#64748b;font-weight:400;margin-left:10px">
            计划（ETD / ETA）与实际事件分开标注；缺失事件显示「${CST_MISSING}」，未填写计划显示「${CST_UNKNOWN}」</span></h3>
        <button class="btn btn-neutral btn-sm" onclick="closeModal()">关闭</button>
      </div>
      <div style="padding:8px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;color:#475569;font-size:13px;margin-bottom:10px">
        ${escapeHtml(CST.hint || '')}
      </div>
      ${cstListView()}
    </div>`;
  modal.style.display = 'flex';
}

function cstLatestEventCell(sm) {
  const one = (label, v) => `${label} ${v ? cstDateTime(v) : CST_MISSING}`;
  return `<div class="text-muted">${one('开船', sm.latestActualDepartureAt)} · ${one('到港', sm.latestActualArrivalAt)}`
    + `<br>${one('查验', sm.latestInspectionAt)} · ${one('放行', sm.latestCustomsReleaseAt)}</div>`;
}



function cstListView() {
  const f = CST.filters;
  const rows = CST.list.map(r => {
    const sm = r.summary || {};
    const badge = r.isVoided
      ? '<span class="status status-neutral">已作废</span>'
      : '<span class="status status-success">已登记</span>';
    return `<tr>
      <td>${escapeHtml(r.sourceTypeText || cstSourceTypeText(r.sourceType))}
        <div class="text-muted">${escapeHtml(r.sourceNo || '')} · ${cstDate(r.sourceDate)} · ${escapeHtml(r.sourceStatusText || CST_UNKNOWN)}</div></td>
      <td>${cstText(r.containerNo)}</td>
      <td>${cstText(r.shippingOrderNo)}<div class="text-muted">B/L：${cstText(r.billOfLadingNo)}</div></td>
      <td>${cstText(r.departurePort)} · ${cstText(r.transitPort)} · ${cstText(r.destinationPort)}</td>
      <td>${cstDate(r.plannedDepartureAt)} / ${cstDate(r.plannedArrivalAt)}</td>
      <td>${cstLatestEventCell(sm)}</td>
      <td>有效 ${escapeHtml(String(sm.activeEventCount || 0))} 条
        <div class="text-muted">已作废（历史）${escapeHtml(String(sm.voidedEventCount || 0))} 条</div>
        ${sm.otherStatusEventCount ? `<div class="text-muted">历史异常状态 ${escapeHtml(String(sm.otherStatusEventCount))} 条</div>` : ''}</td>
      <td>${badge}<div class="text-muted">修订 V${escapeHtml(String(r.revisionNo || 0))}</div></td>
      <td>${escapeHtml(r.referenceAvailabilityText || '')}
        <div class="text-muted">${escapeHtml(r.sourceAvailabilityText || '')}</div></td>
      <td style="white-space:nowrap">
        <button class="btn btn-neutral btn-sm" onclick="cstOpenReference(${r.referenceId})">时间线</button>
      </td>
    </tr>`;
  }).join('');

  return `
    <div style="display:grid;grid-template-columns:repeat(4,1fr);gap:8px;margin-bottom:8px">
      <div><label class="ea-lb">源记录类型</label>
        <select style="width:100%" onchange="cstSetFilter('sourceType', this.value)">
          <option value="">（全部类型）</option>
          ${CST_SOURCE_TYPES.map(s => `<option value="${s.value}" ${f.sourceType === s.value ? 'selected' : ''}>${s.label}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">引用状态</label>
        <select style="width:100%" onchange="cstSetFilter('status', this.value)">
          <option value="">（全部，含已作废历史）</option>
          <option value="1" ${String(f.status) === '1' ? 'selected' : ''}>已登记</option>
          <option value="2" ${String(f.status) === '2' ? 'selected' : ''}>已作废</option>
        </select></div>
      <div><label class="ea-lb">柜号（精确匹配）</label>
        <input style="width:100%" value="${escapeHtml(f.containerNo)}" onchange="cstSetFilter('containerNo', this.value)"
          placeholder="如 CONT-059"></div>
      <div><label class="ea-lb">源记录单号（精确匹配）</label>
        <input style="width:100%" value="${escapeHtml(f.sourceNo)}" onchange="cstSetFilter('sourceNo', this.value)"
          placeholder="订柜 / 预装柜 / 装柜清单单号"></div>
      <div><label class="ea-lb">提单号 B/L（精确匹配）</label>
        <input style="width:100%" value="${escapeHtml(f.billOfLadingNo)}" onchange="cstSetFilter('billOfLadingNo', this.value)"></div>
      <div><label class="ea-lb">订舱号 S/O（精确匹配）</label>
        <input style="width:100%" value="${escapeHtml(f.shippingOrderNo)}" onchange="cstSetFilter('shippingOrderNo', this.value)"></div>
      <div><label class="ea-lb">起运港（精确匹配）</label>
        <input style="width:100%" value="${escapeHtml(f.departurePort)}" onchange="cstSetFilter('departurePort', this.value)"></div>
      <div><label class="ea-lb">目的港（精确匹配）</label>
        <input style="width:100%" value="${escapeHtml(f.destinationPort)}" onchange="cstSetFilter('destinationPort', this.value)"></div>
      <div><label class="ea-lb">计划开船（自）</label>
        <input type="date" style="width:100%" value="${escapeHtml(f.plannedDepartureFrom)}" onchange="cstSetFilter('plannedDepartureFrom', this.value)"></div>
      <div><label class="ea-lb">计划开船（至）</label>
        <input type="date" style="width:100%" value="${escapeHtml(f.plannedDepartureTo)}" onchange="cstSetFilter('plannedDepartureTo', this.value)"></div>
      <div><label class="ea-lb">记录事件类型</label>
        <select style="width:100%" onchange="cstSetFilter('eventType', this.value)">
          ${CST_EVENT_TYPES.map(t => `<option value="${t.value}" ${f.eventType === t.value ? 'selected' : ''}>${t.label}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">记录事件（自）</label>
        <input type="date" style="width:100%" value="${escapeHtml(f.eventDateFrom)}" onchange="cstSetFilter('eventDateFrom', this.value)"></div>
      <div><label class="ea-lb">记录事件（至）</label>
        <input type="date" style="width:100%" value="${escapeHtml(f.eventDateTo)}" onchange="cstSetFilter('eventDateTo', this.value)"></div>
      <div><label class="ea-lb">关键字（源单号 / 柜号 / B/L / S/O / 承运人 / 目的港）</label>
        <input style="width:100%" value="${escapeHtml(f.keyword)}" onchange="cstSetFilter('keyword', this.value)"></div>
      <div style="display:flex;align-items:flex-end">
        <label style="font-size:13px;color:#475569">
          <input type="checkbox" ${f.includeVoidedEvents ? 'checked' : ''}
            onchange="cstSetFilter('includeVoidedEvents', this.checked)"> 记录事件筛选含已作废历史</label></div>
      <div style="display:flex;align-items:flex-end;gap:8px">
        <button class="btn btn-primary btn-sm" onclick="cstSearch()">查询</button>
        <button class="btn btn-neutral btn-sm" onclick="cstResetFilters()">重置筛选</button>
      </div>
    </div>
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <div style="font-size:13px;color:#475569">共 ${CST.total} 条出运记录（每页 ${CST.pageSize} 条，单次上限 200 条；分页有界，汇总按页批量取数、不逐行查库）</div>
      <div style="display:flex;gap:8px">
        <button class="btn btn-neutral btn-sm" onclick="cstPage(-1)">上一页</button>
        <span style="font-size:13px;color:#475569;line-height:32px">第 ${CST.page} 页</span>
        <button class="btn btn-neutral btn-sm" onclick="cstPage(1)">下一页</button>
      </div>
    </div>
    <div class="table-wrap" style="max-height:56vh;overflow:auto">
      <table>
        <thead><tr><th>源记录</th><th>柜号</th><th>S/O · B/L</th><th>港口（起运 · 中转 · 目的）</th>
          <th>计划开船 / 到港</th><th>最近有效实际事件</th><th>里程碑</th><th>状态</th><th>可用性</th><th>操作</th></tr></thead>
        <tbody>${rows || '<tr><td colspan="10" class="text-muted">没有符合条件的出运记录（历史装柜记录没有出运引用时不会凭空出现，也不需要任何回填）</td></tr>'}</tbody>
      </table>
    </div>`;
}

async function cstSearch() { CST.page = 1; await cstLoadList(); cstRender(); }

async function cstResetFilters() {
  CST.page = 1;
  CST.filters = {
    sourceType: '', status: '', containerNo: '', sourceNo: '', billOfLadingNo: '', shippingOrderNo: '',
    departurePort: '', destinationPort: '', plannedDepartureFrom: '', plannedDepartureTo: '',
    eventType: '', eventDateFrom: '', eventDateTo: '', includeVoidedEvents: false, keyword: '',
  };
  await cstLoadList();
  cstRender();
}

async function cstPage(delta) {
  const next = CST.page + delta;
  if (next < 1) return;
  CST.page = next;
  await cstLoadList();
  cstRender();
}

/* ← 返回工作台（复用内存中的筛选与分页状态，不重新查询、不改写任何记录） */
function cstBackToList() {
  CST.view = 'list';
  CST.detail = null;
  cstRender();
}

