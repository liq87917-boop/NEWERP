/* ==================================================================================
   ========== 代理服务费对账单证据（ERP-070）— 显式来源引用的操作性费用证据册 ==========
   ==================================================================================
   定位：把授权用户**显式提供**的客户代理服务费对账证据登记为可审计证据
        （对账单号 / 客户 / 币种 / 对账日期 / **可选**到期日 / 服务期间 / 显式关联的 ERP-069 协议 /
         有界备注 / **一条或多条显式服务来源引用行**），并可显式登记与作废。
   来源链接口径：每一行只用**来源类型 + 来源记录 Id（持久化标识符）**指向既有的销售订单 / 装柜清单，
        来源单号 / 日期 / 状态 / 客户 / 币种快照由服务端写入；界面**不**提供「按单号猜来源」的入口，
        也**不**按文本、金额或日期相似度匹配来源。装柜清单不携带币种 ⇒ 币种校验对该来源不适用。
   金额口径：行说明、计费基础数量与行金额都只来自用户显式填写；系统**不会**把当前协议费率 / 协议固定金额、
        客户账期或默认值、来源单据金额或自由文本折算成费用；合计由**服务端**按币种精度对已校验行求和，
        界面上显示的合计只是服务端返回值的回显（不做前端计算，也不覆盖服务端值）。
   到期日口径：留空即**未知**，系统不会按客户账期、协议或对账日期推一个日期。
   唯一性口径：同一「客户 + 对账单号」在未作废对账单内唯一；同一服务来源同时只能被一条未作废对账单行引用
        （重复计费证据会被拒绝而不是合并；草稿行同样占用来源身份，作废后释放）。
   边界（界面侧同样遵守）：本册**不是**税务发票、**不是**具有法律效力的客户对账单确认、**不是**收入确认、
        **不是**付款通知或催款函、**不是**结算 / 核销确认，也**不是**会计凭证或记账分录；
        登记 / 作废都不开票、不记账、不收款或付款、不催收或联系客户，也不改写协议、客户、订单、装柜清单、
        单证、发票、收款、库存与费用记录。
   文案与服务端 AgencyServiceFeeStatementRules / AgencyServiceFeeStatementService 保持一致。
   ================================================================================== */

let ASFS = {
  view: 'list',
  list: [], total: 0, page: 1, pageSize: 50,
  filters: {
    customerId: '', agreementId: '', status: '', currency: '',
    sourceType: '', from: '', to: '', keyword: ''
  },
  customers: [],
  agreements: [],
  sourceOptions: [],
  current: null,
  form: null,
  voidReason: '',
  hint: '',
  loading: false
};

const ASFS_SOURCE_TYPES = [
  { value: 'sales-order', label: '销售订单' },
  { value: 'loading-list', label: '装柜清单' }
];
const ASFS_CURRENCIES = ['CNY', 'USD', 'EUR', 'HKD', 'GBP', 'JPY'];
const ASFS_STATUSES = [{ value: 0, label: '草稿' }, { value: 1, label: '已登记' }, { value: 2, label: '已作废' }];

/* 打开对账单证据册（客户列表行操作会传入客户 Id：自动预选该客户并在台账内预筛选） */
async function openAgencyServiceFeeStatementRegister(customerId) {
  ASFS = {
    view: 'list', list: [], total: 0, page: 1, pageSize: 50,
    filters: {
      customerId: '', agreementId: '', status: '', currency: '',
      sourceType: '', from: '', to: '', keyword: ''
    },
    customers: [], agreements: [], sourceOptions: [],
    current: null, form: null, voidReason: '', hint: '', loading: false
  };

  const modal = document.getElementById('modal');
  modal.innerHTML = '<div style="padding:40px;text-align:center;color:#64748b">正在加载…</div>';
  modal.style.display = 'block';

  try {
    const res = await api('/api/base/customers?page=1&pageSize=500');
    ASFS.customers = (res && res.items) ? res.items : (Array.isArray(res) ? res : []);
  } catch (e) {
    toast('客户列表加载失败：' + e.message, 'error');
  }

  if (customerId) {
    ASFS.filters.customerId = String(customerId);
    ASFS.hint = '从客户列表进入：已按该客户预筛选；新建对账单时客户已预填。'
      + '每一行都必须显式选择服务来源（销售订单 / 装柜清单），系统不会按单号或金额猜来源。';
  }

  await asfsLoadList();
  asfsRender();
}

function asfsRender() {
  const modal = document.getElementById('modal');
  modal.innerHTML = `
    <div class="modal-content" style="max-width:1280px">
      <div class="modal-header">
        <h3>代理服务费对账单证据（ERP-070）</h3>
        <button class="modal-close" onclick="closeModal()">✕</button>
      </div>
      ${ASFS.hint ? `<div style="padding:6px 10px;background:#eff6ff;border:1px solid #bfdbfe;border-radius:8px;color:#1e40af;font-size:12px;margin-bottom:8px">${escapeHtml(ASFS.hint)}</div>` : ''}
      <div style="padding:6px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;color:#334155;font-size:12px;margin-bottom:8px">
        本册是客户代理服务费的<b>仓库内操作性费用证据</b>：<b>不是税务发票 / 不是具有法律效力的客户对账单确认 / 不是收入确认 / 不是付款通知或催款函 / 不是结算或核销确认 / 不是会计凭证或记账分录</b>；
        每一行都用<b>持久化来源标识符</b>显式引用销售订单或装柜清单（系统不会按单号、金额或相似度猜链接）；
        行金额与计费基础由你显式填写，合计由<b>服务端</b>计算；到期日留空即<b>未知</b>；
        更正走<b>显式作废（原因必填）</b>，原始行与历史保留可读。
      </div>
      ${ASFS.view === 'list' ? asfsListView() : ''}
      ${ASFS.view === 'form' ? asfsFormView() : ''}
      ${ASFS.view === 'detail' ? asfsDetailView() : ''}
      ${ASFS.view === 'void' ? asfsVoidView() : ''}
    </div>`;
  modal.style.display = 'block';
}

function asfsStatusBadge(row) {
  if (row.isDraft) return '<span class="status status-warning">草稿</span>';
  if (row.isRecorded) return '<span class="status status-success">已登记</span>';
  if (row.isVoided) return '<span class="status status-neutral">已作废</span>'
    + `<div class="text-muted">${escapeHtml(row.voidReason || '')}</div>`;
  return `<span class="text-muted">${escapeHtml(row.statusText || '')}</span>`;
}

function asfsSourceTypeLabel(value) {
  const found = ASFS_SOURCE_TYPES.find(t => t.value === value);
  return found ? found.label : (value || '');
}

function asfsToday() { return new Date().toISOString().slice(0, 10); }

/* ==================== 列表视图 ==================== */

function asfsListView() {
  const f = ASFS.filters;
  const customerOptions = ASFS.customers.map(c =>
    `<option value="${c.id}" ${String(c.id) === String(f.customerId) ? 'selected' : ''}>`
    + `${escapeHtml(c.customerCode || '')} ${escapeHtml(c.customerName || '')}</option>`).join('');

  return `
    <div style="display:flex;gap:6px;flex-wrap:wrap;align-items:end;margin-bottom:8px">
      <div><label class="ea-lb">客户</label>
        <select id="asfs-f-customer" style="min-width:180px">
          <option value="">全部客户</option>${customerOptions}
        </select></div>
      <div><label class="ea-lb">状态</label>
        <select id="asfs-f-status">
          <option value="">全部（含已作废历史）</option>
          ${ASFS_STATUSES.map(s => `<option value="${s.value}" ${String(s.value) === String(f.status) ? 'selected' : ''}>${s.label}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">币种</label>
        <select id="asfs-f-currency">
          <option value="">全部币种</option>
          ${ASFS_CURRENCIES.map(c => `<option value="${c}" ${f.currency === c ? 'selected' : ''}>${c}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">来源类型</label>
        <select id="asfs-f-source">
          <option value="">全部来源类型</option>
          ${ASFS_SOURCE_TYPES.map(t => `<option value="${t.value}" ${f.sourceType === t.value ? 'selected' : ''}>${t.label}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">对账日期从</label>
        <input type="date" id="asfs-f-from" value="${escapeHtml(f.from)}"></div>
      <div><label class="ea-lb">到</label>
        <input type="date" id="asfs-f-to" value="${escapeHtml(f.to)}"></div>
      <div><label class="ea-lb">关键字</label>
        <input id="asfs-f-keyword" style="min-width:150px" value="${escapeHtml(f.keyword)}"
          placeholder="对账单号 / 客户 / 协议号 / 备注"></div>
      <button class="btn btn-primary btn-sm" onclick="asfsSearch()">🔍 查询</button>
      <button class="btn btn-neutral btn-sm" onclick="asfsOpenForm(null)">➕ 新建对账单证据</button>
    </div>

    <table class="data-table">
      <thead><tr>
        <th>对账单号</th><th>客户</th><th>对账日期</th><th>到期日</th>
        <th>服务期间</th><th>关联协议</th><th>币种</th><th>合计（服务端）</th><th>行数</th><th>状态</th><th>操作</th>
      </tr></thead>
      <tbody>
        ${ASFS.list.length === 0 ? '<tr><td colspan="11" class="text-muted">没有符合条件的代理服务费对账单证据（不做历史回填，也不推断任何费用）。</td></tr>' : ''}
        ${ASFS.list.map(row => `
          <tr>
            <td>${escapeHtml(row.identityText || '')}</td>
            <td>${escapeHtml(row.customerName || '')}
              <div class="text-muted">${escapeHtml(row.customerCode || '')} · ${escapeHtml(row.customerAvailabilityText || '')}</div></td>
            <td>${escapeHtml(fmtDate(row.statementDate) || '')}</td>
            <td>${escapeHtml(row.dueDateText || '')}</td>
            <td>${escapeHtml(row.servicePeriodText || '')}</td>
            <td>${escapeHtml(row.agreementNo || '')}
              <div class="text-muted">${escapeHtml(row.agreementAvailabilityText || '')}</div></td>
            <td>${escapeHtml(row.currency || '')}</td>
            <td>${escapeHtml(row.totalAmountText || '')}</td>
            <td>${Number(row.lineCount || 0)}</td>
            <td>${asfsStatusBadge(row)}</td>
            <td>
              <button class="btn btn-neutral btn-sm" onclick="asfsOpenDetail(${row.id})">详情</button>
              ${row.isDraft ? `<button class="btn btn-neutral btn-sm" onclick="asfsOpenForm(${row.id})">编辑</button>
                <button class="btn btn-primary btn-sm" onclick="asfsRecord(${row.id})">登记</button>` : ''}
              ${!row.isVoided ? `<button class="btn btn-danger btn-sm" onclick="asfsOpenVoid(${row.id})">作废</button>` : ''}
            </td>
          </tr>`).join('')}
      </tbody>
    </table>

    <div style="display:flex;justify-content:space-between;align-items:center;margin-top:8px">
      <div class="text-muted">共 ${ASFS.total} 条 · 第 ${ASFS.page} 页 · 每页 ${ASFS.pageSize} 条（有界分页；
        列表只显示行数摘要，行的完整来源快照见详情）</div>
      <div>
        <button class="btn btn-neutral btn-sm" onclick="asfsPage(-1)">← 上一页</button>
        <button class="btn btn-neutral btn-sm" onclick="asfsPage(1)">下一页 →</button>
      </div>
    </div>`;
}

async function asfsLoadList() {
  const f = ASFS.filters;
  const qs = new URLSearchParams();
  qs.set('page', ASFS.page);
  qs.set('pageSize', ASFS.pageSize);
  if (f.customerId) qs.set('customerId', f.customerId);
  if (f.status !== '') qs.set('status', f.status);
  if (f.currency) qs.set('currency', f.currency);
  if (f.sourceType) qs.set('sourceType', f.sourceType);
  if (f.from) qs.set('statementDateFrom', f.from);
  if (f.to) qs.set('statementDateTo', f.to);
  if (f.keyword) qs.set('keyword', f.keyword);

  try {
    const data = await api('/api/agency-service-fee-statements?' + qs.toString());
    ASFS.list = (data && data.items) || [];
    ASFS.total = (data && data.total) || 0;
  } catch (e) {
    ASFS.list = [];
    ASFS.total = 0;
    toast('对账单台账加载失败：' + e.message, 'error');
  }
}

async function asfsSearch() {
  ASFS.filters.customerId = (document.getElementById('asfs-f-customer') || {}).value || '';
  ASFS.filters.status = (document.getElementById('asfs-f-status') || {}).value || '';
  ASFS.filters.currency = (document.getElementById('asfs-f-currency') || {}).value || '';
  ASFS.filters.sourceType = (document.getElementById('asfs-f-source') || {}).value || '';
  ASFS.filters.from = (document.getElementById('asfs-f-from') || {}).value || '';
  ASFS.filters.to = (document.getElementById('asfs-f-to') || {}).value || '';
  ASFS.filters.keyword = (document.getElementById('asfs-f-keyword') || {}).value || '';
  ASFS.page = 1;
  await asfsLoadList();
  asfsRender();
}

async function asfsPage(delta) {
  const next = ASFS.page + delta;
  if (next < 1) return;
  ASFS.page = next;
  await asfsLoadList();
  asfsRender();
}

async function asfsBackToList() {
  ASFS.view = 'list';
  ASFS.current = null;
  ASFS.form = null;
  ASFS.voidReason = '';
  await asfsLoadList();
  asfsRender();
}

/* 载入某客户下「已登记」的代理服务费协议证据（显式选择协议；系统不会按客户或金额自动匹配） */
async function asfsLoadAgreements() {
  ASFS.agreements = [];
  const customerId = ASFS.form ? ASFS.form.customerId : '';
  if (!customerId) return;
  try {
    const data = await api('/api/agency-service-fee-agreements?customerId=' + encodeURIComponent(customerId)
      + '&status=1&page=1&pageSize=200');
    ASFS.agreements = (data && data.items) || [];
  } catch (e) {
    toast('已登记的代理服务费协议加载失败：' + e.message, 'error');
  }
}

/* 载入可引用的显式服务来源候选（有界；只回显身份 / 日期 / 状态，不回显来源金额） */
async function asfsLoadSourceOptions() {
  ASFS.sourceOptions = [];
  const customerId = ASFS.form ? ASFS.form.customerId : '';
  const currency = ASFS.form ? ASFS.form.currency : '';
  if (!customerId || !currency) return;
  try {
    const data = await api('/api/agency-service-fee-statements/source-options?customerId='
      + encodeURIComponent(customerId) + '&currency=' + encodeURIComponent(currency) + '&take=200');
    ASFS.sourceOptions = Array.isArray(data) ? data : [];
  } catch (e) {
    toast('服务来源候选加载失败：' + e.message, 'error');
  }
}

/* ==================== 新建 / 编辑表单 ==================== */

function asfsBlankLine() {
  return { sourceType: 'sales-order', sourceId: '', description: '', basisQuantity: '', basisNote: '', amount: '', remark: '' };
}

async function asfsOpenForm(id) {
  if (id) {
    let row;
    try {
      row = await api('/api/agency-service-fee-statements/' + id);
    } catch (e) {
      toast('对账单详情加载失败：' + e.message, 'error');
      return;
    }
    if (!row.isDraft) { toast('只有草稿可以编辑（已登记 / 已作废证据不可修改）', 'error'); return; }

    ASFS.form = {
      id: row.id,
      statementNo: row.statementNo || '',
      customerId: String(row.customerId || ''),
      currency: row.currency || 'USD',
      statementDate: fmtDate(row.statementDate),
      dueDate: fmtDate(row.dueDate),
      servicePeriodFrom: fmtDate(row.servicePeriodFrom),
      servicePeriodTo: fmtDate(row.servicePeriodTo),
      agreementId: String(row.agreementId || ''),
      remark: row.remark || '',
      totalAmountText: row.totalAmountText || '',
      lines: (row.lines || []).map(l => ({
        sourceType: l.sourceType || 'sales-order',
        sourceId: String(l.sourceId || ''),
        description: l.description || '',
        basisQuantity: (l.basisQuantity === null || l.basisQuantity === undefined) ? '' : String(l.basisQuantity),
        basisNote: l.basisNote || '',
        amount: String(l.amount || ''),
        remark: l.remark || ''
      }))
    };
    if (ASFS.form.lines.length === 0) ASFS.form.lines.push(asfsBlankLine());
  } else {
    ASFS.form = {
      id: null,
      statementNo: '',
      customerId: ASFS.filters.customerId || '',
      currency: 'USD',
      statementDate: asfsToday(),
      dueDate: '',
      servicePeriodFrom: '',
      servicePeriodTo: '',
      agreementId: '',
      remark: '',
      totalAmountText: '',
      lines: [asfsBlankLine()]
    };
  }

  await asfsLoadAgreements();
  await asfsLoadSourceOptions();
  ASFS.view = 'form';
  asfsRender();
}

/* 结构化重绘前把当前 DOM 输入同步回状态（避免切换来源类型 / 增删行时丢失已填内容） */
function asfsSyncForm() {
  const f = ASFS.form;
  if (!f) return;
  const val = (id) => { const el = document.getElementById(id); return el ? el.value : null; };

  const header = [
    ['asfs-no', 'statementNo'], ['asfs-currency', 'currency'], ['asfs-date', 'statementDate'],
    ['asfs-due', 'dueDate'], ['asfs-from', 'servicePeriodFrom'], ['asfs-to', 'servicePeriodTo'],
    ['asfs-agreement', 'agreementId'], ['asfs-remark', 'remark']
  ];
  header.forEach(([id, key]) => { const v = val(id); if (v !== null) f[key] = v; });

  f.lines.forEach((line, i) => {
    const type = val('asfs-l-' + i + '-type'); if (type !== null) line.sourceType = type;
    const source = val('asfs-l-' + i + '-source'); if (source !== null) line.sourceId = source;
    const description = val('asfs-l-' + i + '-desc'); if (description !== null) line.description = description;
    const basisQty = val('asfs-l-' + i + '-qty'); if (basisQty !== null) line.basisQuantity = basisQty;
    const basisNote = val('asfs-l-' + i + '-basis'); if (basisNote !== null) line.basisNote = basisNote;
    const amount = val('asfs-l-' + i + '-amount'); if (amount !== null) line.amount = amount;
    const remark = val('asfs-l-' + i + '-remark'); if (remark !== null) line.remark = remark;
  });
}

function asfsFormView() {
  const f = ASFS.form;
  const customerOptions = ASFS.customers.map(c =>
    `<option value="${c.id}" ${String(c.id) === String(f.customerId) ? 'selected' : ''}>`
    + `${escapeHtml(c.customerCode || '')} ${escapeHtml(c.customerName || '')}</option>`).join('');
  const agreementOptions = ASFS.agreements.map(a =>
    `<option value="${a.id}" ${String(a.id) === String(f.agreementId) ? 'selected' : ''}>`
    + `${escapeHtml(a.identityText || a.agreementNo || '')} · ${escapeHtml(a.currency || '')} · ${escapeHtml(a.feeTermsText || '')}</option>`).join('');

  return `
    <div style="display:grid;grid-template-columns:1fr 1fr;gap:8px">
      <div><label class="ea-lb">对账单号 *（按真实对账单填写，系统不自动发号）</label>
        <input id="asfs-no" style="width:100%" value="${escapeHtml(f.statementNo)}"
          onchange="ASFS.form.statementNo=this.value"></div>
      <div><label class="ea-lb">客户 *（必须存在且启用）</label>
        <select id="asfs-customer" style="width:100%" onchange="asfsChangeCustomer(this.value)">
          <option value="">请选择客户…</option>${customerOptions}
        </select></div>
      <div><label class="ea-lb">币种 *（必须与关联协议币种一致；金额一律原币）</label>
        <select id="asfs-currency" style="width:100%" onchange="asfsChangeCurrency(this.value)">
          ${ASFS_CURRENCIES.map(c => `<option value="${c}" ${f.currency === c ? 'selected' : ''}>${c}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">关联协议 *（必须已登记的 ERP-069 协议证据，且客户 / 币种一致）</label>
        <select id="asfs-agreement" style="width:100%" onchange="ASFS.form.agreementId=this.value">
          <option value="">请选择已登记的协议证据…</option>${agreementOptions}
        </select>
        <div class="text-muted">系统不会自动匹配协议；协议费率 / 固定金额只作只读核对，不参与本对账单金额计算。</div></div>
      <div><label class="ea-lb">对账日期 *</label>
        <input type="date" id="asfs-date" style="width:100%" value="${escapeHtml(f.statementDate)}"
          onchange="ASFS.form.statementDate=this.value"></div>
      <div><label class="ea-lb">到期日（可选；留空 = 未知，系统不推算）</label>
        <input type="date" id="asfs-due" style="width:100%" value="${escapeHtml(f.dueDate)}"
          onchange="ASFS.form.dueDate=this.value"></div>
      <div><label class="ea-lb">服务期间起始 *</label>
        <input type="date" id="asfs-from" style="width:100%" value="${escapeHtml(f.servicePeriodFrom)}"
          onchange="ASFS.form.servicePeriodFrom=this.value"></div>
      <div><label class="ea-lb">服务期间结束 *</label>
        <input type="date" id="asfs-to" style="width:100%" value="${escapeHtml(f.servicePeriodTo)}"
          onchange="ASFS.form.servicePeriodTo=this.value"></div>
    </div>

    <div style="margin-top:8px"><label class="ea-lb">备注</label>
      <input id="asfs-remark" style="width:100%" value="${escapeHtml(f.remark)}"
        onchange="ASFS.form.remark=this.value"></div>

    <div style="margin-top:8px;padding:6px 10px;background:#fff7ed;border:1px solid #fed7aa;border-radius:8px;font-size:12px;color:#9a3412">
      每一行都必须<b>显式选择服务来源</b>（销售订单 / 装柜清单的持久化记录）；系统不会按单号文本、金额或日期相似度猜来源，
      也不会把协议费率、客户默认值或来源金额折算成行金额。同一来源同时只能被一条未作废对账单行引用（重复会被拒绝）。
    </div>

    ${asfsLineRows()}

    <div style="display:flex;gap:6px;margin-top:10px;align-items:center">
      <button class="btn btn-primary" onclick="asfsSaveForm()">💾 保存草稿</button>
      <button class="btn btn-neutral" onclick="asfsAddLine()">➕ 添加来源行</button>
      <button class="btn btn-neutral" onclick="asfsBackToList()">取消</button>
      <span class="text-muted">保存只写本证据册：不会开发票、不会记账、不会收款、不会联系客户。合计由服务端计算。</span>
    </div>`;
}

/* 客户变更：重新载入该客户下已登记的协议与来源候选（清空已选协议与来源，避免跨客户误引用） */
async function asfsChangeCustomer(customerId) {
  asfsSyncForm();
  ASFS.form.customerId = customerId || '';
  ASFS.form.agreementId = '';
  ASFS.form.lines.forEach(line => { line.sourceId = ''; });
  await asfsLoadAgreements();
  await asfsLoadSourceOptions();
  asfsRender();
}

/* 币种变更：重新载入来源候选（来源资格依赖币种），并清空已选来源 */
async function asfsChangeCurrency(currency) {
  asfsSyncForm();
  ASFS.form.currency = currency || 'CNY';
  ASFS.form.lines.forEach(line => { line.sourceId = ''; });
  await asfsLoadSourceOptions();
  asfsRender();
}

/* 来源引用行编辑区（每行都用来源类型 + 持久化来源 Id 显式引用；不可引用的来源在候选里被禁用并给出原因） */
function asfsLineRows() {
  const f = ASFS.form;
  if (!f) return '';

  const rows = f.lines.map((line, i) => {
    const options = ASFS.sourceOptions.filter(o => o.sourceType === line.sourceType);
    const sourceOptions = options.map(o => {
      const label = (o.sourceNo || '') + ' · ' + (fmtDate(o.sourceDate) || '') + ' · ' + (o.sourceStatusText || '')
        + (o.eligible ? '' : '（不可引用：' + (o.eligibilityText || '') + '）');
      return `<option value="${o.sourceId}"${String(o.sourceId) === String(line.sourceId) ? ' selected' : ''}`
        + `${o.eligible ? '' : ' disabled'}>${escapeHtml(label)}</option>`;
    }).join('');
    const selected = options.find(o => String(o.sourceId) === String(line.sourceId));

    return `
      <tr>
        <td>${i + 1}</td>
        <td>
          <select id="asfs-l-${i}-type" style="width:100%" onchange="asfsChangeLineType(${i}, this.value)">
            ${ASFS_SOURCE_TYPES.map(t => `<option value="${t.value}"${t.value === line.sourceType ? ' selected' : ''}>${t.label}</option>`).join('')}
          </select>
        </td>
        <td>
          <select id="asfs-l-${i}-source" style="width:100%" onchange="ASFS.form.lines[${i}].sourceId=this.value">
            <option value="">请显式选择来源记录…</option>${sourceOptions}
          </select>
          ${selected ? `<div class="text-muted">${escapeHtml(selected.eligibilityText || '')}</div>` : ''}
          ${options.length === 0 ? '<div class="text-muted">该客户 + 该币种下没有可用的此类型来源记录（系统不会替换成别的记录）。</div>' : ''}
        </td>
        <td><input id="asfs-l-${i}-desc" style="width:150px" value="${escapeHtml(line.description)}"
          onchange="ASFS.form.lines[${i}].description=this.value" placeholder="这一行对应什么服务"></td>
        <td><input type="number" step="0.0001" id="asfs-l-${i}-qty" style="width:90px" value="${escapeHtml(line.basisQuantity)}"
          onchange="ASFS.form.lines[${i}].basisQuantity=this.value" placeholder="留空 = 未知"></td>
        <td><input id="asfs-l-${i}-basis" style="width:150px" value="${escapeHtml(line.basisNote)}"
          onchange="ASFS.form.lines[${i}].basisNote=this.value" placeholder="数量 / 口径来自哪里"></td>
        <td><input type="number" step="0.01" id="asfs-l-${i}-amount" style="width:100px" value="${escapeHtml(line.amount)}"
          onchange="ASFS.form.lines[${i}].amount=this.value" placeholder="显式金额"></td>
        <td><input id="asfs-l-${i}-remark" style="width:120px" value="${escapeHtml(line.remark)}"
          onchange="ASFS.form.lines[${i}].remark=this.value"></td>
        <td><button class="btn btn-danger btn-sm" onclick="asfsRemoveLine(${i})">删除</button></td>
      </tr>`;
  }).join('');

  return `
    <table class="data-table" style="margin-top:8px">
      <thead><tr>
        <th>#</th><th>来源类型 *</th><th>来源记录 *（持久化标识符）</th><th>行说明 *</th>
        <th>计费基础数量</th><th>计费基础说明 *</th><th>金额 *</th><th>行备注</th><th>操作</th>
      </tr></thead>
      <tbody>${rows}</tbody>
    </table>
    <div class="text-muted">行号由服务端按提交顺序写入；来源单号 / 日期 / 状态 / 客户 / 币种快照由服务端按来源记录权威写入；
      合计 = 服务端按币种精度对有效行金额求和${f.totalAmountText ? '（上次服务端合计：' + escapeHtml(f.totalAmountText) + '）' : ''}。</div>`;
}

function asfsChangeLineType(index, sourceType) {
  asfsSyncForm();
  const line = ASFS.form.lines[index];
  line.sourceType = sourceType;
  line.sourceId = '';
  asfsRender();
}

function asfsAddLine() {
  asfsSyncForm();
  if (ASFS.form.lines.length >= 200) { toast('单张对账单最多 200 行，请拆分对账单', 'error'); return; }
  ASFS.form.lines.push(asfsBlankLine());
  asfsRender();
}

function asfsRemoveLine(index) {
  asfsSyncForm();
  ASFS.form.lines.splice(index, 1);
  if (ASFS.form.lines.length === 0) ASFS.form.lines.push(asfsBlankLine());
  asfsRender();
}

async function asfsSaveForm() {
  asfsSyncForm();
  const f = ASFS.form || {};

  if (!f.statementNo) { toast('请填写对账单号', 'error'); return; }
  if (!f.customerId) { toast('请选择客户', 'error'); return; }
  if (!f.agreementId) { toast('请显式选择关联的已登记协议证据（系统不自动匹配协议）', 'error'); return; }
  if (!f.statementDate) { toast('请填写对账日期', 'error'); return; }
  if (!f.servicePeriodFrom || !f.servicePeriodTo) { toast('请填写完整的服务期间（起始与结束）', 'error'); return; }
  if (f.lines.length === 0) { toast('请至少添加一条服务来源引用行', 'error'); return; }

  const lines = [];
  for (let i = 0; i < f.lines.length; i++) {
    const line = f.lines[i];
    if (!line.sourceId) { toast(`第 ${i + 1} 行请显式选择来源记录`, 'error'); return; }
    if (!line.description) { toast(`第 ${i + 1} 行请填写行说明`, 'error'); return; }
    if (!line.basisNote) { toast(`第 ${i + 1} 行请填写计费基础说明`, 'error'); return; }
    if (line.amount === '' || line.amount === null || Number(line.amount) <= 0) {
      toast(`第 ${i + 1} 行请填写大于 0 的显式金额（系统不会用协议费率或客户默认值补一个金额）`, 'error');
      return;
    }
    lines.push({
      sourceType: line.sourceType,
      sourceId: Number(line.sourceId),
      description: line.description,
      basisQuantity: line.basisQuantity === '' ? null : Number(line.basisQuantity),
      basisNote: line.basisNote,
      amount: Number(line.amount),
      remark: line.remark || ''
    });
  }

  const payload = {
    statementNo: f.statementNo,
    customerId: Number(f.customerId),
    currency: f.currency,
    statementDate: f.statementDate + 'T00:00:00',
    dueDate: f.dueDate ? f.dueDate + 'T00:00:00' : null,
    servicePeriodFrom: f.servicePeriodFrom + 'T00:00:00',
    servicePeriodTo: f.servicePeriodTo + 'T00:00:00',
    agreementId: Number(f.agreementId),
    remark: f.remark || '',
    lines: lines
  };

  try {
    if (f.id) await api('/api/agency-service-fee-statements/' + f.id, 'PUT', payload);
    else await api('/api/agency-service-fee-statements', 'POST', payload);
    toast(f.id ? '对账单证据草稿已更新（合计按服务端已校验行重算）'
      : '对账单证据草稿已登记（仅操作性费用证据留痕，未开票 / 未收款 / 未记账 / 未联系客户）');
  } catch (e) {
    toast('保存失败：' + e.message, 'error');
    return;
  }

  await asfsBackToList();
}

/* ==================== 详情 / 登记 / 作废 ==================== */

async function asfsOpenDetail(id) {
  try {
    ASFS.current = await api('/api/agency-service-fee-statements/' + id);
  } catch (e) {
    toast('对账单详情加载失败：' + e.message, 'error');
    return;
  }
  ASFS.view = 'detail';
  asfsRender();
}

function asfsDetailView() {
  const row = ASFS.current || {};
  const lines = row.lines || [];

  return `
    <table class="data-table">
      <tbody>
        <tr><th style="width:180px">对账单号</th><td>${escapeHtml(row.identityText || '')}</td></tr>
        <tr><th>客户</th><td>${escapeHtml(row.customerCode || '')} ${escapeHtml(row.customerName || '')}
          <div class="text-muted">${escapeHtml(row.customerAvailabilityText || '')}</div></td></tr>
        <tr><th>币种 / 服务端合计</th><td>${escapeHtml(row.currency || '')}（小数位 ${Number(row.amountDecimals || 0)}）·
          <b>${escapeHtml(row.totalAmountText || '')}</b>
          <div class="text-muted">合计由服务端按币种精度对有效行金额求和；客户端提交的合计不被采信。</div></td></tr>
        <tr><th>对账日期 / 到期日</th><td>${escapeHtml(fmtDate(row.statementDate) || '')} ·
          <b>${escapeHtml(row.dueDateText || '')}</b></td></tr>
        <tr><th>服务期间</th><td>${escapeHtml(row.servicePeriodText || '')}</td></tr>
        <tr><th>关联协议</th><td>${escapeHtml(row.agreementNo || '')}（${escapeHtml(row.agreementCurrency || '')} ·
          ${escapeHtml(row.agreementFeeMethod || '')}）
          <div class="text-muted">${escapeHtml(row.agreementTermsText || '')}</div>
          <div class="text-muted">${escapeHtml(row.agreementAvailabilityText || '')}</div>
          <div class="text-muted">协议条款只作只读核对，不参与本对账单任何行金额的计算。</div></td></tr>
        <tr><th>状态</th><td>${asfsStatusBadge(row)}</td></tr>
        <tr><th>登记时间 / 登记人</th><td>${escapeHtml(fmtDate(row.recordedAt) || '未登记')} · ${escapeHtml(row.recordedBy || '')}</td></tr>
        <tr><th>作废时间 / 原因</th><td>${row.isVoided
          ? `${escapeHtml(fmtDate(row.voidedAt))} · ${escapeHtml(row.voidReason || '')}`
          : '未作废'}</td></tr>
        <tr><th>备注</th><td>${escapeHtml(row.remark || '')}</td></tr>
      </tbody>
    </table>

    <div style="margin-top:10px"><b>服务来源引用行（共 ${lines.length} 行）</b></div>
    <table class="data-table" style="margin-top:4px">
      <thead><tr>
        <th>#</th><th>来源类型</th><th>来源单号 / 日期</th><th>来源状态</th><th>来源客户</th>
        <th>来源币种</th><th>行说明</th><th>计费基础</th><th>金额</th><th>行状态</th>
      </tr></thead>
      <tbody>
        ${lines.length === 0 ? '<tr><td colspan="10" class="text-muted">没有可读的引用行。</td></tr>' : ''}
        ${lines.map(l => `
          <tr>
            <td>${Number(l.lineNo || 0)}</td>
            <td>${escapeHtml(l.sourceTypeText || '')}</td>
            <td>${escapeHtml(l.sourceNo || '')}
              <div class="text-muted">${escapeHtml(fmtDate(l.sourceDate) || '')} · Id=${Number(l.sourceId || 0)}</div></td>
            <td>${escapeHtml(l.sourceStatusText || '')}
              <div class="text-muted">${escapeHtml(l.sourceAvailabilityText || '')}</div></td>
            <td>${escapeHtml(l.sourceCustomerCode || '')} ${escapeHtml(l.sourceCustomerName || '')}</td>
            <td>${escapeHtml(l.sourceCurrency || '（来源不携带币种）')}
              <div class="text-muted">${escapeHtml(l.sourceCurrencyCompatibilityText || '')}</div></td>
            <td>${escapeHtml(l.description || '')}</td>
            <td>${(l.basisQuantity === null || l.basisQuantity === undefined) ? '（未提供）' : Number(l.basisQuantity)}
              <div class="text-muted">${escapeHtml(l.basisNote || '')}</div></td>
            <td><b>${escapeHtml(l.amountText || '')}</b></td>
            <td>${escapeHtml(l.statusText || '')}${l.isVoided && l.voidReason ? `<div class="text-muted">${escapeHtml(l.voidReason)}</div>` : ''}</td>
          </tr>`).join('')}
      </tbody>
    </table>

    <div style="margin-top:8px;padding:8px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;font-size:12px;color:#334155">
      <div><b>来源链接口径</b>：${escapeHtml(row.sourceLinkRuleText || '')}</div>
      <div style="margin-top:4px"><b>金额口径</b>：${escapeHtml(row.amountRuleText || '')}</div>
      <div style="margin-top:4px"><b>唯一性口径</b>：${escapeHtml(row.uniquenessRuleText || '')}</div>
      <div style="margin-top:4px"><b>与发票 / 收款 / 记账 / 提成的分离</b>：${escapeHtml(row.separationText || '')}</div>
      <div style="margin-top:4px"><b>模块边界</b>：${escapeHtml(row.boundaryText || '')}</div>
    </div>

    <div style="display:flex;gap:6px;margin-top:10px">
      <button class="btn btn-neutral" onclick="asfsBackToList()">返回列表</button>
      <button class="btn btn-neutral" title="打开代理服务费收款分摊证据册（只显式把既有、未删除、未取消的收款单的一部分金额分摊到本已登记对账单；不改写对账单与收款单，也不收款 / 记账 / 核销 / 结算）"
        onclick="openAgencyServiceFeeCollectionAllocationRegister(${row.id})">💰 收款分摊</button>
      ${row.isDraft ? `<button class="btn btn-neutral" onclick="asfsOpenForm(${row.id})">编辑草稿</button>
        <button class="btn btn-primary" onclick="asfsRecord(${row.id})">登记</button>` : ''}
      ${row.isVoided ? '' : `<button class="btn btn-danger" onclick="asfsOpenVoid(${row.id})">作废</button>`}
    </div>`;
}

async function asfsRecord(id) {
  if (!confirm('确认登记该代理服务费对账单证据？登记后表头与全部行冻结，只可作废、不可改写'
    + '（合计由服务端按行金额重算；不会开票、不会记账、不会收款、不会联系客户）。')) return;
  try {
    await api('/api/agency-service-fee-statements/' + id + '/record', 'POST');
    toast('对账单证据已登记（证据已冻结，可作废但不可改写）');
  } catch (e) {
    toast('登记失败：' + e.message, 'error');
    return;
  }
  await asfsBackToList();
}

async function asfsOpenVoid(id) {
  try {
    ASFS.current = await api('/api/agency-service-fee-statements/' + id);
  } catch (e) {
    toast('对账单详情加载失败：' + e.message, 'error');
    return;
  }
  ASFS.voidReason = '';
  ASFS.view = 'void';
  asfsRender();
}

function asfsVoidView() {
  const row = ASFS.current || {};
  return `
    <div style="padding:8px;background:#fef2f2;border:1px solid #fecaca;border-radius:8px;color:#991b1b;font-size:12px">
      作废保留对账单身份、全部原始行、来源快照、客户与协议快照、登记人与时间戳（历史可读），
      不会删除记录、不会改写原始金额，也不会开具 / 作废任何真实发票、不会收款或催收、不会产生任何财务动作；
      作废后该服务来源可重新被新对账单显式引用。
    </div>
    <div style="margin-top:8px"><label class="ea-lb">作废目标</label>
      <div>${escapeHtml(row.identityText || '')} · ${escapeHtml(row.customerName || '')} ·
        ${escapeHtml(row.currency || '')} ${escapeHtml(row.totalAmountText || '')} ·
        行数 ${Number(row.lineCount || 0)}</div></div>
    <div style="margin-top:8px"><label class="ea-lb">作废原因 *</label>
      <input id="asfs-void-reason" style="width:100%" value="${escapeHtml(ASFS.voidReason)}"
        onchange="ASFS.voidReason=this.value" placeholder="例如：对账口径更正 / 登记错误 / 客户争议"></div>
    <div style="display:flex;gap:6px;margin-top:10px">
      <button class="btn btn-danger" onclick="asfsConfirmVoid(${row.id})">确认作废</button>
      <button class="btn btn-neutral" onclick="asfsBackToList()">取消</button>
    </div>`;
}

async function asfsConfirmVoid(id) {
  const reason = (document.getElementById('asfs-void-reason') || {}).value || ASFS.voidReason || '';
  if (!reason.trim()) { toast('请填写作废原因（作废会保留历史证据）', 'error'); return; }
  try {
    await api('/api/agency-service-fee-statements/' + id + '/void', 'POST', { reason: reason.trim() });
    toast('对账单证据已作废（原始行与历史保留，可读）');
  } catch (e) {
    toast('作废失败：' + e.message, 'error');
    return;
  }
  await asfsBackToList();
}
