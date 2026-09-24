/* ==================================================================================
   ====== 装柜出运引用登记册（ERP-057）— 装柜链路的显式出运证据登记 ======
   ==================================================================================
   定位：为**既有**装柜链路记录（订柜信息 / 预装柜单 / 装柜清单）登记一条用户录入的**出运引用证据**
         （出运方式 / 订舱号 S/O / 提单号 B/L / 起运·中转·目的港 / 计划开船·到港时间 / 承运人 /
          货代 / 拖车 / 报关行快照），让「这票货按什么出运条件走」可追溯。
   为什么是「一套」而不是「第二套」（ERP-057 审计结论）：装柜链路已有**唯一**的持久化引用关系 ——
         ContainerPreLoading.BookingId（预装柜单 → 订柜信息）与
         ContainerLoadingList.PreLoadingId（装柜清单 → 预装柜单），而订柜信息由 ERP-040 承载本套
         跟踪值的权威记录；因此本登记册**只按显式「类型 + Id」**指向一条权威记录，
         **不**新建出运主数据、**不**复制订柜跟踪列、**不**按柜号 / 订单号 / 单证号等自由文本匹配。
   边界（界面侧同样遵守）：
     1. 本登记册不是承运人 / 海关 / 货代的确认或回执、不是提单正本、不是报关或放行结论，
        也不构成清关许可、交付承诺或法律依据；
     2. 只读写 ContainerShipmentReferences 与 ContainerShipmentReferenceRevisions 两张表：
        不改写订柜信息 / 预装柜单 / 装柜清单的任何列、状态与工作流（不推进装柜状态、不改柜号、
        不写单据号），也不改写订单、库存与库存成本、库存流水、单证中心、发票、费用与分摊、
        收付款、税务与结算记录，更不会联系承运人 / 海关 / 货代或任何外部跟踪系统；
     3. 缺失字段一律显示「未知」：不按柜型、体积、客户、航线或自由文本推断（出运方式只接受 LCL / FCL）；
     4. 同一条源记录最多 1 条有效引用；更正走显式**修订**（记录修订前原值 + 必填原因）或显式**作废**
        （必填原因），不提供硬删除与静默替换；
     5. 源记录类型 / Id 不允许改派：如需指向别的柜 / 清单，请新建一条引用并作废旧的。
   文案与服务端 ContainerShipmentReferenceRules / ContainerShipmentReferenceService 保持一致。
   ================================================================================== */

let CSR = {
  view: 'list', list: [], total: 0, page: 1, pageSize: 50,
  filters: { sourceType: '', sourceId: '', status: '', shipmentMode: '', keyword: '' },
  candidates: [], candidateKeyword: '', brokers: [],
  current: null, form: null, detail: null, revisions: [], voidReason: '', hint: '', error: ''
};

const CSR_SOURCE_TYPES = [
  { value: 'booking', label: '订柜信息' },
  { value: 'pre-loading', label: '预装柜单' },
  { value: 'loading-list', label: '装柜清单' },
];

/* 当前模块 → 源记录类型（订柜信息 / 预装柜单 / 装柜清单三个装柜模块的工具栏与行操作共用） */
const CSR_MODULE_SOURCE_TYPES = {
  booking: 'booking',
  'pre-loading': 'pre-loading',
  'loading-list': 'loading-list',
};

const CSR_MODE_OPTIONS = [
  { value: '', label: '未知 / 未指定' },
  { value: 'LCL', label: '拼箱 LCL' },
  { value: 'FCL', label: '整箱 FCL' },
];

/* 未知文案（与后端 ContainerShipmentReferenceRules.UnknownText 一致） */
const CSR_UNKNOWN = '未知';

function csrSourceTypeText(t) {
  const v = String(t || '').trim().toLowerCase();
  const hit = CSR_SOURCE_TYPES.find(s => s.value === v);
  return hit ? hit.label : CSR_UNKNOWN;
}

function csrModeText(mode) {
  const v = String(mode || '').trim().toUpperCase();
  if (v === 'LCL') return '拼箱 LCL';
  if (v === 'FCL') return '整箱 FCL';
  return CSR_UNKNOWN;
}

/* 只读文本：空值显示「未知」（已转义） */
function csrText(v) {
  const s = String(v === null || v === undefined ? '' : v).trim();
  return s ? escapeHtml(s) : CSR_UNKNOWN;
}

/* 只读日期：空值显示「未知」，不回落为空白或今天 */
function csrDate(v) { return v ? fmtDate(v) : CSR_UNKNOWN; }

function csrStatusBadge(row) {
  if (row.isVoided) {
    return '<span class="status status-neutral">已作废</span>'
      + `<div class="text-muted">${escapeHtml(row.voidReason || '')}</div>`;
  }
  return '<span class="status status-success">已登记</span>'
    + `<div class="text-muted">修订 V${row.revisionNo || 1}</div>`;
}

/* 打开登记册：装柜三单的行操作会传入该行 Id（自动按当前模块预筛选该源记录） */
async function openContainerShipmentReferences(sourceId) {
  CSR = {
    view: 'list', list: [], total: 0, page: 1, pageSize: 50,
    filters: { sourceType: '', sourceId: '', status: '', shipmentMode: '', keyword: '' },
    candidates: [], candidateKeyword: '', brokers: [],
    current: null, form: null, detail: null, revisions: [], voidReason: '', hint: '', error: ''
  };

  const modal = document.getElementById('modal');
  modal.innerHTML = '<div style="padding:40px;text-align:center;color:#64748b">正在加载…</div>';
  modal.style.display = 'block';

  const moduleType = CSR_MODULE_SOURCE_TYPES[CURRENT_MODULE_CODE];
  if (sourceId && moduleType) {
    CSR.filters.sourceType = moduleType;
    CSR.filters.sourceId = String(sourceId);
    CSR.hint = `从「${csrSourceTypeText(moduleType)}」第 ${sourceId} 行进入：台账已按该源记录预筛选。`;
  }

  await csrLoadBrokers();
  await csrLoadList();
  csrRender();
}

/* 报关行下拉选项（只读；与写入校验同一口径，停用 / 删除 / 类型不符的字典项不会出现） */
async function csrLoadBrokers() {
  try {
    CSR.brokers = await api('/api/container/shipment-references/customs-broker-options') || [];
  } catch (e) {
    CSR.brokers = [];
  }
}

/* 源记录候选（只读、有界；用于显式选择，不按柜号 / 单号猜记录） */
async function csrLoadCandidates(sourceType, keyword) {
  if (!sourceType) { CSR.candidates = []; return; }
  const params = ['sourceType=' + encodeURIComponent(sourceType), 'take=200'];
  if (keyword) params.push('keyword=' + encodeURIComponent(keyword));
  try {
    CSR.candidates = await api('/api/container/shipment-references/source-candidates?' + params.join('&')) || [];
  } catch (e) {
    CSR.candidates = [];
    toast('源记录候选加载失败：' + e.message, 'error');
  }
}

async function csrLoadList() {
  const f = CSR.filters;
  const params = ['page=' + CSR.page, 'pageSize=' + CSR.pageSize];
  if (f.sourceType) params.push('sourceType=' + encodeURIComponent(f.sourceType));
  if (f.sourceId) params.push('sourceId=' + encodeURIComponent(f.sourceId));
  if (f.status) params.push('status=' + encodeURIComponent(f.status));
  if (f.shipmentMode) params.push('shipmentMode=' + encodeURIComponent(f.shipmentMode));
  if (f.keyword) params.push('keyword=' + encodeURIComponent(f.keyword));

  try {
    const page = await api('/api/container/shipment-references?' + params.join('&'));
    CSR.list = (page && page.items) || [];
    CSR.total = (page && page.total) || 0;
  } catch (e) {
    CSR.list = [];
    CSR.total = 0;
    toast('出运引用台账加载失败：' + e.message, 'error');
  }
}

function csrRender() {
  const modal = document.getElementById('modal');
  const body = CSR.view === 'form' ? csrFormView()
    : CSR.view === 'detail' ? csrDetailView()
    : CSR.view === 'void' ? csrVoidView()
    : csrListView();

  modal.innerHTML = `
    <div style="max-width:1360px;margin:2vh auto;background:#fff;border-radius:14px;padding:16px 18px;max-height:94vh;overflow:auto;box-shadow:0 20px 60px rgba(0,0,0,.25)">
      <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:8px">
        <h3 style="margin:0">📦 装柜出运引用登记册
          <span style="font-size:13px;color:#64748b;font-weight:400;margin-left:10px">
            登记「这票货按什么出运条件走」的操作性证据（不是承运人 / 海关 / 货代确认、不是提单正本、不是放行结论）</span></h3>
        <button class="btn btn-neutral btn-sm" onclick="closeModal()">关闭</button>
      </div>
      <div style="padding:8px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;color:#475569;font-size:13px;margin-bottom:10px">
        只写本登记册两张表：不改写订柜信息 / 预装柜单 / 装柜清单的状态与任何列，也不改写订单、库存、单证、发票、费用与结算；
        缺失字段一律显示「未知」，不按柜型 / 体积 / 客户 / 航线或自由文本推断。
      </div>
      ${CSR.hint ? `<div style="padding:6px 10px;background:#eff6ff;border:1px solid #bfdbfe;border-radius:8px;color:#1e40af;font-size:13px;margin-bottom:8px">${escapeHtml(CSR.hint)}</div>` : ''}
      ${body}
    </div>`;
  modal.style.display = 'flex';
}

function csrListView() {
  const f = CSR.filters;
  const rows = CSR.list.map(row => `
    <tr>
      <td>${csrSourceTypeText(row.sourceType)}
        <div class="text-muted">${escapeHtml(row.sourceNo || '')} · ${fmtDate(row.sourceDate)} · ${escapeHtml(row.sourceStatusText || '')}</div></td>
      <td>${csrText(row.containerNo)}</td>
      <td>${escapeHtml(row.shipmentModeText || csrModeText(row.shipmentMode))}</td>
      <td>${csrText(row.shippingOrderNo)}
        <div class="text-muted">B/L：${csrText(row.billOfLadingNo)}</div></td>
      <td>${csrText(row.departurePort)} → ${csrText(row.transitPort)} → ${csrText(row.destinationPort)}</td>
      <td>${csrDate(row.plannedDepartureAt)} / ${csrDate(row.plannedArrivalAt)}</td>
      <td>${csrText(row.carrierName)}
        <div class="text-muted">货代：${csrText(row.forwarderName)}</div></td>
      <td>${csrText(row.customsBrokerName)}</td>
      <td>${csrStatusBadge(row)}</td>
      <td>${escapeHtml(row.sourceAvailabilityText || '')}</td>
      <td style="white-space:nowrap">
        <button class="btn btn-neutral btn-sm" onclick="csrOpenDetail(${row.id})">详情</button>
        ${row.isRecorded ? `<button class="btn btn-neutral btn-sm" onclick="csrOpenForm(${row.id})">修订</button>` : ''}
        ${row.isRecorded ? `<button class="btn btn-neutral btn-sm" onclick="csrOpenVoid(${row.id})">作废</button>` : ''}
        <button class="btn btn-neutral btn-sm" onclick="openContainerShipmentMilestones(${row.id})">里程碑</button>
      </td>
    </tr>`).join('');

  return `
    <div style="display:grid;grid-template-columns:repeat(5,1fr);gap:8px;margin-bottom:8px">
      <div><label class="ea-lb">源记录类型</label>
        <select style="width:100%" onchange="CSR.filters.sourceType=this.value;CSR.filters.sourceId=''">
          <option value="">（全部类型）</option>
          ${CSR_SOURCE_TYPES.map(s => `<option value="${s.value}" ${f.sourceType === s.value ? 'selected' : ''}>${s.label}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">状态</label>
        <select style="width:100%" onchange="CSR.filters.status=this.value">
          <option value="">（全部，含已作废历史）</option>
          <option value="1" ${String(f.status) === '1' ? 'selected' : ''}>已登记</option>
          <option value="2" ${String(f.status) === '2' ? 'selected' : ''}>已作废</option>
        </select></div>
      <div><label class="ea-lb">出运方式</label>
        <select style="width:100%" onchange="CSR.filters.shipmentMode=this.value">
          <option value="">（全部）</option>
          <option value="LCL" ${f.shipmentMode === 'LCL' ? 'selected' : ''}>拼箱 LCL</option>
          <option value="FCL" ${f.shipmentMode === 'FCL' ? 'selected' : ''}>整箱 FCL</option>
        </select></div>
      <div><label class="ea-lb">关键字（源单号 / 柜号 / B/L / S/O / 承运人 / 货代）</label>
        <input style="width:100%" value="${escapeHtml(f.keyword)}" onchange="CSR.filters.keyword=this.value"
          placeholder="如 DG20260901 或柜号"></div>
      <div style="display:flex;align-items:flex-end;gap:8px">
        <button class="btn btn-neutral btn-sm" onclick="csrSearch()">查询</button>
        <button class="btn btn-primary btn-sm" onclick="csrOpenForm()">＋ 登记出运引用</button>
        <button class="btn btn-neutral btn-sm" onclick="openContainerShipmentMilestones()">🛣 里程碑登记册</button>
      </div>
    </div>
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <div style="font-size:13px;color:#475569">共 ${CSR.total} 条出运引用
        ${f.sourceId ? `（已按 ${csrSourceTypeText(f.sourceType)} Id=${escapeHtml(f.sourceId)} 预筛选）` : ''}</div>
      <div style="display:flex;gap:8px">
        ${f.sourceId ? '<button class="btn btn-neutral btn-sm" onclick="csrClearSourceFilter()">清除源记录筛选</button>' : ''}
        <button class="btn btn-neutral btn-sm" onclick="csrPage(-1)">上一页</button>
        <span style="font-size:13px;color:#475569;line-height:32px">第 ${CSR.page} 页</span>
        <button class="btn btn-neutral btn-sm" onclick="csrPage(1)">下一页</button>
      </div>
    </div>
    <div class="table-wrap" style="max-height:52vh;overflow:auto">
      <table>
        <thead><tr>
          <th>源记录</th><th>柜号</th><th>出运方式</th><th>S/O · B/L</th><th>港口（起运 · 中转 · 目的）</th>
          <th>计划开船 / 到港</th><th>承运人 · 货代</th><th>报关行</th><th>状态</th><th>可用性</th><th>操作</th>
        </tr></thead>
        <tbody>${rows || '<tr><td colspan="11" class="text-muted">暂无出运引用（历史装柜记录不需要任何回填，可直接登记一条）</td></tr>'}</tbody>
      </table>
    </div>`;
}

async function csrSearch() { CSR.page = 1; CSR.view = 'list'; await csrLoadList(); csrRender(); }

async function csrPage(delta) {
  const next = CSR.page + delta;
  if (next < 1) return;
  CSR.page = next;
  await csrLoadList();
  csrRender();
}

function csrClearSourceFilter() {
  CSR.filters.sourceId = '';
  CSR.hint = '';
  csrSearch();
}

function csrBackToList() {
  CSR.view = 'list';
  CSR.current = null;
  CSR.form = null;
  CSR.detail = null;
  CSR.revisions = [];
  CSR.error = '';
  csrRender();
}

function csrSetField(key, value) { if (CSR.form) CSR.form[key] = value; }

/* ==================== 登记 / 修订表单 ==================== */

async function csrOpenForm(id) {
  CSR.error = '';

  if (id) {
    const row = (CSR.list || []).find(x => x.id === id)
      || (CSR.detail && CSR.detail.reference && CSR.detail.reference.id === id ? CSR.detail.reference : null);
    if (!row) { toast('未找到该出运引用，请刷新台账后重试', 'error'); return; }
    CSR.current = row;
    CSR.form = {
      sourceType: row.sourceType, sourceId: String(row.sourceId),
      shipmentMode: row.shipmentMode || '',
      shippingOrderNo: row.shippingOrderNo || '',
      billOfLadingNo: row.billOfLadingNo || '',
      carrierName: row.carrierName || '',
      forwarderName: row.forwarderName || '',
      departurePort: row.departurePort || '',
      transitPort: row.transitPort || '',
      destinationPort: row.destinationPort || '',
      plannedDepartureAt: row.plannedDepartureAt ? String(row.plannedDepartureAt).slice(0, 10) : '',
      plannedArrivalAt: row.plannedArrivalAt ? String(row.plannedArrivalAt).slice(0, 10) : '',
      truckerName: row.truckerName || '',
      customsBrokerId: row.customsBrokerId ? String(row.customsBrokerId) : '',
      remark: row.remark || '',
      reason: ''
    };
    CSR.view = 'form';
    csrRender();
    return;
  }

  CSR.current = null;
  CSR.candidateKeyword = '';
  CSR.form = {
    sourceType: CSR.filters.sourceType || '', sourceId: CSR.filters.sourceId || '',
    shipmentMode: '', shippingOrderNo: '', billOfLadingNo: '', carrierName: '', forwarderName: '',
    departurePort: '', transitPort: '', destinationPort: '',
    plannedDepartureAt: '', plannedArrivalAt: '', truckerName: '',
    customsBrokerId: '', remark: '', reason: ''
  };
  if (CSR.filters.sourceType) await csrLoadCandidates(CSR.filters.sourceType, '');
  CSR.view = 'form';
  csrRender();
}

async function csrChangeSourceType(value) {
  if (!CSR.form) return;
  CSR.form.sourceType = value;
  CSR.form.sourceId = '';
  CSR.candidateKeyword = '';
  await csrLoadCandidates(value, '');
  csrRender();
}

async function csrCandidateSearch() {
  if (!CSR.form) return;
  await csrLoadCandidates(CSR.form.sourceType, CSR.candidateKeyword);
  csrRender();
}

function csrFormView() {
  const f = CSR.form;
  if (!f) return '<div class="text-muted">请选择要登记 / 修订的出运引用。</div>';
  const editing = !!CSR.current;
  const lockInEdit = editing ? 'disabled' : '';

  const sourceOptions = (CSR.candidates || []).map(c =>
    `<option value="${c.sourceId}" ${String(c.sourceId) === String(f.sourceId) ? 'selected' : ''}>`
    + `${escapeHtml(c.sourceNo || '')} · ${escapeHtml(c.containerNo || '')} · ${fmtDate(c.sourceDate)}`
    + ` · ${escapeHtml(c.sourceStatusText || '')}`
    + `${c.alreadyReferenced ? '（已有有效引用 Id=' + c.existingReferenceId + '）' : ''}</option>`).join('');

  const brokerOptions = (CSR.brokers || []).map(b =>
    `<option value="${b.id}" ${String(b.id) === String(f.customsBrokerId) ? 'selected' : ''}>${escapeHtml(b.infoName || '')}</option>`).join('');

  const orphanBroker = f.customsBrokerId
    && !(CSR.brokers || []).some(b => String(b.id) === String(f.customsBrokerId))
    ? `<option value="${escapeHtml(f.customsBrokerId)}" selected>历史引用（字典项已停用 / 不可用，保留名称快照）</option>`
    : '';

  return `
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <h4 style="margin:0">${editing
        ? `修订出运引用证据（V${CSR.current.revisionNo || 1} → V${(CSR.current.revisionNo || 1) + 1}）`
        : '登记出运引用证据'}</h4>
      <button class="btn btn-neutral btn-sm" onclick="csrBackToList()">← 返回台账</button>
    </div>
    <div style="display:grid;grid-template-columns:repeat(4,1fr);gap:8px;margin-bottom:8px">
      <div><label class="ea-lb">源记录类型 *</label>
        <select style="width:100%" ${lockInEdit} onchange="csrChangeSourceType(this.value)">
          <option value="">（请选择）</option>
          ${CSR_SOURCE_TYPES.map(s => `<option value="${s.value}" ${f.sourceType === s.value ? 'selected' : ''}>${s.label}</option>`).join('')}
        </select></div>
      <div style="grid-column:span 2"><label class="ea-lb">源记录（显式选择，不按柜号 / 单号猜记录） *</label>
        <select style="width:100%" ${lockInEdit} onchange="csrSetField('sourceId', this.value)">
          <option value="">（请选择）</option>
          ${sourceOptions}
        </select>
        ${editing ? '<div class="text-muted">源记录不允许改派：如需指向别的柜 / 清单，请新建一条引用并作废本条。</div>' : ''}</div>
      <div><label class="ea-lb">候选筛选（源单号 / 柜号）</label>
        <div style="display:flex;gap:6px">
          <input style="flex:1" value="${escapeHtml(CSR.candidateKeyword || '')}"
            onchange="CSR.candidateKeyword=this.value" placeholder="留空 = 最近 200 条">
          <button class="btn btn-neutral btn-sm" onclick="csrCandidateSearch()">查询</button>
        </div></div>
    </div>
    <div style="display:grid;grid-template-columns:repeat(4,1fr);gap:8px;margin-bottom:8px">
      <div><label class="ea-lb">出运方式（留空 = 未知）</label>
        <select style="width:100%" onchange="csrSetField('shipmentMode', this.value)">
          ${CSR_MODE_OPTIONS.map(o => `<option value="${o.value}" ${String(f.shipmentMode || '') === o.value ? 'selected' : ''}>${o.label}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">订舱号 / 托运单号 S/O</label>
        <input style="width:100%" value="${escapeHtml(f.shippingOrderNo || '')}" onchange="csrSetField('shippingOrderNo', this.value)"></div>
      <div><label class="ea-lb">提单号 B/L（引用文本，非提单正本）</label>
        <input style="width:100%" value="${escapeHtml(f.billOfLadingNo || '')}" onchange="csrSetField('billOfLadingNo', this.value)"></div>
      <div><label class="ea-lb">承运人</label>
        <input style="width:100%" value="${escapeHtml(f.carrierName || '')}" onchange="csrSetField('carrierName', this.value)"></div>
      <div><label class="ea-lb">货代</label>
        <input style="width:100%" value="${escapeHtml(f.forwarderName || '')}" onchange="csrSetField('forwarderName', this.value)"></div>
      <div><label class="ea-lb">起运港</label>
        <input style="width:100%" value="${escapeHtml(f.departurePort || '')}" onchange="csrSetField('departurePort', this.value)"></div>
      <div><label class="ea-lb">中转港（可留空）</label>
        <input style="width:100%" value="${escapeHtml(f.transitPort || '')}" onchange="csrSetField('transitPort', this.value)"></div>
      <div><label class="ea-lb">目的港</label>
        <input style="width:100%" value="${escapeHtml(f.destinationPort || '')}" onchange="csrSetField('destinationPort', this.value)"></div>
      <div><label class="ea-lb">计划开船 ETD（留空 = 未知）</label>
        <input type="date" style="width:100%" value="${escapeHtml(f.plannedDepartureAt || '')}" onchange="csrSetField('plannedDepartureAt', this.value)"></div>
      <div><label class="ea-lb">计划到港 ETA（不得早于 ETD）</label>
        <input type="date" style="width:100%" value="${escapeHtml(f.plannedArrivalAt || '')}" onchange="csrSetField('plannedArrivalAt', this.value)"></div>
      <div><label class="ea-lb">拖车 / 集卡服务商</label>
        <input style="width:100%" value="${escapeHtml(f.truckerName || '')}" onchange="csrSetField('truckerName', this.value)"></div>
      <div><label class="ea-lb">报关行（留空 = 未指定）</label>
        <select style="width:100%" onchange="csrSetField('customsBrokerId', this.value)">
          <option value="">（未指定 / 未知）</option>
          ${brokerOptions}
          ${orphanBroker}
        </select></div>
    </div>
    <div style="margin-bottom:8px"><label class="ea-lb">备注</label>
      <textarea style="width:100%;height:56px" onchange="csrSetField('remark', this.value)">${escapeHtml(f.remark || '')}</textarea></div>
    ${editing ? `<div style="margin-bottom:8px"><label class="ea-lb">修订原因 *（会先保留修订前的原值，必填）</label>
      <input style="width:100%" value="${escapeHtml(f.reason || '')}" onchange="csrSetField('reason', this.value)"
        placeholder="如：计划开船时间由货代更新 / 提单号录入有误"></div>` : ''}
    <div style="padding:8px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;color:#475569;font-size:13px;margin-bottom:8px">
      保存只登记证据：不推进装柜 / 订货状态、不改柜号与单据号、不改写订单 / 库存 / 单证 / 发票 / 费用与结算；
      未填写的字段保持「未知」，系统不会按柜型、体积、客户、航线或自由文本推断。
    </div>
    <div style="display:flex;justify-content:flex-end;gap:8px">
      <button class="btn btn-neutral" onclick="csrBackToList()">取消</button>
      <button class="btn btn-primary" onclick="csrSaveForm()">${editing ? '保存修订（保留原值留痕）' : '登记出运引用'}</button>
    </div>`;
}

async function csrSaveForm() {
  const f = CSR.form;
  if (!f) return;
  if (!f.sourceType) { toast('请选择源记录类型', 'error'); return; }
  if (!f.sourceId) { toast('请选择源记录', 'error'); return; }
  if (CSR.current && !String(f.reason || '').trim()) {
    toast('请填写修订原因（修订会先保留修订前的原值）', 'error');
    return;
  }

  const payload = {
    sourceType: f.sourceType,
    sourceId: Number(f.sourceId),
    shipmentMode: f.shipmentMode || '',
    shippingOrderNo: f.shippingOrderNo || '',
    billOfLadingNo: f.billOfLadingNo || '',
    carrierName: f.carrierName || '',
    forwarderName: f.forwarderName || '',
    departurePort: f.departurePort || '',
    transitPort: f.transitPort || '',
    destinationPort: f.destinationPort || '',
    plannedDepartureAt: f.plannedDepartureAt || null,
    plannedArrivalAt: f.plannedArrivalAt || null,
    truckerName: f.truckerName || '',
    customsBrokerId: f.customsBrokerId ? Number(f.customsBrokerId) : null,
    remark: f.remark || ''
  };

  try {
    if (CSR.current) {
      await api('/api/container/shipment-references/' + CSR.current.id, 'PUT',
        Object.assign({}, payload, { reason: String(f.reason || '').trim() }));
      toast('出运引用已修订（修订前的原值已写入修订留痕）');
    } else {
      await api('/api/container/shipment-references', 'POST', payload);
      toast('出运引用已登记（未推进装柜状态、未联系承运人 / 海关 / 货代）');
    }
    await csrLoadList();
    CSR.view = 'list';
    CSR.form = null;
    CSR.current = null;
    CSR.hint = '';
    csrRender();
  } catch (e) {
    toast('保存失败：' + e.message, 'error');
  }
}

/* ==================== 详情（含修订留痕） ==================== */

async function csrOpenDetail(id) {
  try {
    const detail = await api('/api/container/shipment-references/' + id);
    /* 修订留痕走有界只读接口（take 上限由服务端收敛；超出时详情显式说明被截断） */
    const revisions = await api('/api/container/shipment-references/' + id + '/revisions?take=50');
    CSR.detail = detail;
    CSR.revisions = revisions || (detail && detail.revisions) || [];
    CSR.current = detail ? detail.reference : null;
    CSR.view = 'detail';
    csrRender();
  } catch (e) {
    toast('出运引用详情加载失败：' + e.message, 'error');
  }
}

function csrDetailView() {
  const d = CSR.detail;
  if (!d || !d.reference) return '<div class="text-muted">请选择出运引用。</div>';
  const r = d.reference;
  const pairs = [
    ['源记录', `${csrSourceTypeText(r.sourceType)} · ${escapeHtml(r.sourceNo || '')} · ${fmtDate(r.sourceDate)} · ${escapeHtml(r.sourceStatusText || '')}`],
    ['柜号', csrText(r.containerNo)],
    ['状态', `${r.isVoided ? '已作废' : '已登记'} · 修订 V${r.revisionNo || 1} · 留痕 ${r.revisionCount || 0} 条`],
    ['出运方式', escapeHtml(r.shipmentModeText || csrModeText(r.shipmentMode))],
    ['订舱号 S/O', csrText(r.shippingOrderNo)],
    ['提单号 B/L', csrText(r.billOfLadingNo)],
    ['起运港', csrText(r.departurePort)],
    ['中转港', csrText(r.transitPort)],
    ['目的港', csrText(r.destinationPort)],
    ['计划开船 ETD', csrDate(r.plannedDepartureAt)],
    ['计划到港 ETA', csrDate(r.plannedArrivalAt)],
    ['承运人', csrText(r.carrierName)],
    ['货代', csrText(r.forwarderName)],
    ['拖车 / 集卡服务商', csrText(r.truckerName)],
    ['报关行', `${csrText(r.customsBrokerName)}<div class="text-muted">${escapeHtml(r.customsBrokerAvailabilityText || '')}</div>`],
    ['备注', csrText(r.remark)],
    ['登记时间', fmtDate(r.recordedAt)],
    ['最近修订', r.lastRevisedAt ? `${fmtDate(r.lastRevisedAt)} · ${escapeHtml(r.lastRevisionReason || '')}` : CSR_UNKNOWN],
    ['作废', r.isVoided ? `${fmtDate(r.voidedAt)} · ${escapeHtml(r.voidReason || '')}` : CSR_UNKNOWN],
    ['源记录可用性', escapeHtml(r.sourceAvailabilityText || '')],
  ];

  const revisionRows = CSR.revisions.map(v => `
    <tr>
      <td>V${v.revisionNo} → V${(v.revisionNo || 1) + 1}</td>
      <td>${fmtDate(v.supersededAt)}</td>
      <td>${escapeHtml(v.reason || '')}</td>
      <td>${escapeHtml(v.shipmentModeText || csrModeText(v.shipmentMode))}</td>
      <td>${csrText(v.shippingOrderNo)} / ${csrText(v.billOfLadingNo)}</td>
      <td>${csrDate(v.plannedDepartureAt)} / ${csrDate(v.plannedArrivalAt)}</td>
      <td>${csrText(v.carrierName)} · ${csrText(v.forwarderName)}</td>
      <td>${csrText(v.customsBrokerName)}</td>
      <td>${csrText(v.remark)}</td>
    </tr>`).join('');

  return `
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <h4 style="margin:0">出运引用详情（${csrSourceTypeText(r.sourceType)}「${escapeHtml(r.sourceNo || '')}」）</h4>
      <button class="btn btn-neutral btn-sm" onclick="csrBackToList()">← 返回台账</button>
    </div>
    <div class="table-wrap" style="max-height:40vh;overflow:auto;margin-bottom:10px">
      <table><tbody>
        ${pairs.map(([label, value]) => `<tr><th style="width:180px;text-align:left">${label}</th><td>${value}</td></tr>`).join('')}
      </tbody></table>
    </div>
    <h4 style="margin:12px 0 6px">修订留痕（只追加，保留修订前的原值）
      <span style="font-size:13px;color:#64748b;font-weight:400">
        ${d.revisionsTruncated ? `仅显示最近 ${CSR.revisions.length} / ${d.revisionCount} 条（有界）` : `共 ${d.revisionCount || 0} 条`}</span></h4>
    <div class="table-wrap" style="max-height:32vh;overflow:auto;margin-bottom:8px">
      <table>
        <thead><tr>
          <th>版本</th><th>取代时间</th><th>修订原因</th><th>出运方式</th><th>S/O · B/L</th>
          <th>计划开船 / 到港</th><th>承运人 · 货代</th><th>报关行</th><th>备注</th>
        </tr></thead>
        <tbody>${revisionRows || '<tr><td colspan="9" class="text-muted">暂无修订留痕（首次登记后未修订过）</td></tr>'}</tbody>
      </table>
    </div>
    <div style="padding:8px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;color:#475569;font-size:13px">
      ${escapeHtml(d.infoText || '')}<br>
      ${escapeHtml(d.boundaryText || '')}
    </div>`;
}

/* ==================== 作废（保留历史） ==================== */

function csrOpenVoid(id) {
  const pools = [
    CSR.list || [],
    (CSR.detail && CSR.detail.reference) ? [CSR.detail.reference] : [],
    CSR.current ? [CSR.current] : []
  ];
  let row = null;
  for (const pool of pools) {
    const hit = pool.find(x => x.id === id);
    if (hit) { row = hit; break; }
  }
  if (!row) { toast('未找到该出运引用，请刷新台账后重试', 'error'); return; }

  CSR.current = row;
  CSR.voidReason = '';
  CSR.view = 'void';
  csrRender();
}

function csrVoidView() {
  const row = CSR.current;
  if (!row) return '<div class="text-muted">请选择出运引用。</div>';

  return `
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <h4 style="margin:0">作废出运引用证据</h4>
      <button class="btn btn-neutral btn-sm" onclick="csrBackToList()">← 返回台账</button>
    </div>
    <div style="display:grid;grid-template-columns:repeat(4,1fr);gap:8px;padding:8px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;font-size:13px;margin-bottom:8px">
      <div>源记录<div><b>${csrSourceTypeText(row.sourceType)}「${escapeHtml(row.sourceNo || '')}」</b></div></div>
      <div>出运方式<div><b>${escapeHtml(row.shipmentModeText || csrModeText(row.shipmentMode))}</b></div></div>
      <div>S/O · B/L<div>${csrText(row.shippingOrderNo)} / ${csrText(row.billOfLadingNo)}</div></div>
      <div>登记时间<div>${fmtDate(row.recordedAt)}</div></div>
    </div>
    <div style="padding:8px 10px;background:#fef2f2;border:1px solid #fecaca;border-radius:8px;color:#991b1b;font-size:13px">
      作废<b>保留</b>原始出运证据、源记录快照与全部修订留痕（不物理删除、不静默替换），
      也<b>不会</b>改写订柜信息 / 预装柜单 / 装柜清单与任何下游单据；作废后该源记录可重新登记一条新的有效引用。
      请填写作废原因（必填）。
    </div>
    <div style="margin-top:8px"><label class="ea-lb">作废原因 *</label>
      <textarea id="csr-void-reason" style="width:100%;height:64px" onchange="CSR.voidReason=this.value"
        placeholder="如：出运方式录错 / 提单号指向别的柜 / 重复登记">${escapeHtml(CSR.voidReason || '')}</textarea></div>
    <div style="display:flex;justify-content:flex-end;gap:8px;margin-top:12px">
      <button class="btn btn-neutral" onclick="csrBackToList()">取消</button>
      <button class="btn btn-danger" onclick="csrConfirmVoid()">🗑 确认作废（保留历史）</button>
    </div>`;
}

async function csrConfirmVoid() {
  const row = CSR.current;
  if (!row) return;
  const reason = String(CSR.voidReason || '').trim();
  if (!reason) { toast('请填写作废原因', 'error'); return; }

  try {
    await api('/api/container/shipment-references/' + row.id + '/void', 'POST', { reason: reason });
    toast('出运引用已作废（原始值与修订留痕保留可读）');
    CSR.current = null;
    CSR.voidReason = '';
    CSR.view = 'list';
    await csrLoadList();
    csrRender();
  } catch (e) {
    toast('作废失败：' + e.message, 'error');
  }
}
