/* ==================================================================================
   ========== 代理服务费协议证据登记册（ERP-069）— 客户代理服务费商业条款 ==========
   ==================================================================================
   定位：把**授权用户显式提供的**客户代理服务费商业条款登记为可审计的协议证据
        （协议号 / 客户 / 生效日期区间 / 币种 / 披露的计费方式与显式费率或固定金额 /
         有界的计费依据说明 / 有界备注），并可显式登记与作废。
   费用条款口径：费率 / 固定金额 / 计费依据都只来自用户显式提交的值；服务端不会从
        业务员提成设置（系统参数 SalesCommissionRate）、客户 / 供应商主数据比例、历史订单、
        自由文本、客户默认值或金额相似度推断；比例费率与固定金额不得同时填写。
   历史模型审计：本仓库此前没有「代理服务费协议」权威模型 —— 客户佣金 / 回佣比例、订单佣金比例
        快照与供应商返点比例只是主数据 / 单据设置，`业务员提成表`（/api/reports/sales-commission）
        是内部提成报表；三者都是**各自独立的模型**，本登记册既不读取、也不改写、也不派生它们。
   边界（界面侧同样遵守）：本登记册不是税务发票、不是会计凭证或记账分录、不是付款授权或资金指令、
        不是法律意见，也不是服务已交付或已收付款的证明；登记 / 作废都不开发票、不记账、
        不发起付款，也不改写客户、销售订单、装柜与单证、收款单、销项发票证据、库存与费用记录；
        系统不提供任何「不披露 / 账外 / 隐匿佣金」的字段或流程；更正走显式作废（原因必填）。
   文案与服务端 AgencyServiceFeeAgreementRules / AgencyServiceFeeAgreementService 保持一致。
   ================================================================================== */

let ASFA = {
  view: 'list',
  list: [], total: 0, page: 1, pageSize: 50,
  filters: { customerId: '', status: '', feeMethod: '', currency: '', from: '', to: '', keyword: '' },
  customers: [],
  current: null,
  form: null,
  voidReason: '',
  hint: '',
  loading: false
};

const ASFA_METHODS = ['比例费率', '固定金额'];
const ASFA_CURRENCIES = ['CNY', 'USD', 'EUR', 'HKD', 'GBP', 'JPY'];
const ASFA_STATUSES = [{ value: 0, label: '草稿' }, { value: 1, label: '已登记' }, { value: 2, label: '已作废' }];

/* 打开登记册（客户列表行操作会传入客户 Id：自动预选该客户并在台账内预筛选） */
async function openAgencyServiceFeeAgreementRegister(customerId) {
  ASFA = {
    view: 'list', list: [], total: 0, page: 1, pageSize: 50,
    filters: { customerId: '', status: '', feeMethod: '', currency: '', from: '', to: '', keyword: '' },
    customers: [], current: null, form: null, voidReason: '', hint: '', loading: false
  };

  const modal = document.getElementById('modal');
  modal.innerHTML = '<div style="padding:40px;text-align:center;color:#64748b">正在加载…</div>';
  modal.style.display = 'block';

  try {
    const res = await api('/api/base/customers?page=1&pageSize=500');
    ASFA.customers = (res && res.items) ? res.items : (Array.isArray(res) ? res : []);
  } catch (e) {
    toast('客户列表加载失败：' + e.message, 'error');
  }

  if (customerId) {
    ASFA.filters.customerId = String(customerId);
    ASFA.hint = '从客户列表进入：已按该客户预筛选；新建协议时客户已预填。'
      + '协议条款只来自显式填写的值（不从业务员提成设置或客户默认比例推断）。';
  }

  await asfaLoadList();
  asfaRender();
}

function asfaRender() {
  const modal = document.getElementById('modal');
  modal.innerHTML = `
    <div class="modal-content" style="max-width:1180px">
      <div class="modal-header">
        <h3>代理服务费协议证据（ERP-069）</h3>
        <button class="modal-close" onclick="closeModal()">✕</button>
      </div>
      ${ASFA.hint ? `<div style="padding:6px 10px;background:#eff6ff;border:1px solid #bfdbfe;border-radius:8px;color:#1e40af;font-size:12px;margin-bottom:8px">${escapeHtml(ASFA.hint)}</div>` : ''}
      <div style="padding:6px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;color:#334155;font-size:12px;margin-bottom:8px">
        本登记册是客户代理服务费的<b>仓库内商业条款证据</b>：<b>不是税务发票 / 不是会计凭证或记账分录 / 不是付款授权 / 不是法律意见 / 不是服务已交付或已收付款的证明</b>；
        费用条款只来自显式填写（比例费率与固定金额不得同时填写，系统不从<b>业务员提成</b>设置、客户比例或历史订单推断）；
        更正走<b>显式作废（原因必填）</b>，原始条款与历史保留可读。
      </div>
      ${ASFA.view === 'list' ? asfaListView() : ''}
      ${ASFA.view === 'form' ? asfaFormView() : ''}
      ${ASFA.view === 'detail' ? asfaDetailView() : ''}
      ${ASFA.view === 'void' ? asfaVoidView() : ''}
    </div>`;
  modal.style.display = 'block';
}

function asfaMoney(v) { return Number(v || 0).toFixed(2); }

function asfaStatusBadge(row) {
  if (row.isDraft) return '<span class="status status-warning">草稿</span>';
  if (row.isRecorded) return '<span class="status status-success">已登记</span>';
  if (row.isVoided) return '<span class="status status-neutral">已作废</span>'
    + `<div class="text-muted">${escapeHtml(row.voidReason || '')}</div>`;
  return `<span class="text-muted">${escapeHtml(row.statusText || '')}</span>`;
}

function asfaTermsCell(row) {
  if (row.feeMethod === '固定金额') {
    return `固定金额 <b>${asfaMoney(row.fixedAmount)}</b> ${escapeHtml(row.currency || '')}`;
  }
  return `比例费率 <b>${Number(row.ratePercent || 0).toFixed(4).replace(/0+$/, '').replace(/\.$/, '')}%</b>`;
}

function asfaRangeText(row) {
  const from = fmtDate(row.effectiveFrom);
  return row.effectiveTo ? `${from} ~ ${fmtDate(row.effectiveTo)}` : `${from} 起（无固定结束日）`;
}

/* ==================== 列表视图 ==================== */

function asfaListView() {
  const f = ASFA.filters;
  const customerOptions = ASFA.customers.map(c =>
    `<option value="${c.id}" ${String(c.id) === String(f.customerId) ? 'selected' : ''}>`
    + `${escapeHtml(c.customerCode || '')} ${escapeHtml(c.customerName || '')}</option>`).join('');

  return `
    <div style="display:flex;gap:6px;flex-wrap:wrap;align-items:end;margin-bottom:8px">
      <div><label class="ea-lb">客户</label>
        <select id="asfa-f-customer" style="min-width:180px">
          <option value="">全部客户</option>${customerOptions}
        </select></div>
      <div><label class="ea-lb">状态</label>
        <select id="asfa-f-status">
          <option value="">全部（含已作废历史）</option>
          ${ASFA_STATUSES.map(s => `<option value="${s.value}" ${String(s.value) === String(f.status) ? 'selected' : ''}>${s.label}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">计费方式</label>
        <select id="asfa-f-method">
          <option value="">全部方式</option>
          ${ASFA_METHODS.map(m => `<option value="${m}" ${f.feeMethod === m ? 'selected' : ''}>${m}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">币种</label>
        <select id="asfa-f-currency">
          <option value="">全部币种</option>
          ${ASFA_CURRENCIES.map(c => `<option value="${c}" ${f.currency === c ? 'selected' : ''}>${c}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">生效起始从</label>
        <input type="date" id="asfa-f-from" value="${escapeHtml(f.from)}"></div>
      <div><label class="ea-lb">到</label>
        <input type="date" id="asfa-f-to" value="${escapeHtml(f.to)}"></div>
      <div><label class="ea-lb">关键字</label>
        <input id="asfa-f-keyword" style="min-width:150px" value="${escapeHtml(f.keyword)}"
          placeholder="协议号 / 客户 / 计费依据"></div>
      <button class="btn btn-primary btn-sm" onclick="asfaSearch()">🔍 查询</button>
      <button class="btn btn-neutral btn-sm" onclick="asfaOpenForm(null)">➕ 新建协议证据</button>
    </div>

    <table class="data-table">
      <thead><tr>
        <th>协议号</th><th>客户</th><th>生效区间</th>
        <th>币种</th><th>披露的费用条款</th><th>计费依据</th><th>状态</th><th>操作</th>
      </tr></thead>
      <tbody>
        ${ASFA.list.length === 0 ? '<tr><td colspan="8" class="text-muted">没有符合条件的代理服务费协议证据（不做历史回填，也不推断任何条款）。</td></tr>' : ''}
        ${ASFA.list.map(row => `
          <tr>
            <td>${escapeHtml(row.identityText || '')}</td>
            <td>${escapeHtml(row.customerName || '')}
              <div class="text-muted">${escapeHtml(row.customerCode || '')} · ${escapeHtml(row.customerAvailabilityText || '')}</div></td>
            <td>${escapeHtml(asfaRangeText(row))}</td>
            <td>${escapeHtml(row.currency || '')}</td>
            <td>${asfaTermsCell(row)}</td>
            <td>${escapeHtml(row.feeBasis || '')}</td>
            <td>${asfaStatusBadge(row)}</td>
            <td>
              <button class="btn btn-neutral btn-sm" onclick="asfaOpenDetail(${row.id})">详情</button>
              ${row.isDraft ? `<button class="btn btn-neutral btn-sm" onclick="asfaOpenForm(${row.id})">编辑</button>
                <button class="btn btn-primary btn-sm" onclick="asfaRecord(${row.id})">登记</button>` : ''}
              ${!row.isVoided ? `<button class="btn btn-danger btn-sm" onclick="asfaOpenVoid(${row.id})">作废</button>` : ''}
            </td>
          </tr>`).join('')}
      </tbody>
    </table>

    <div style="display:flex;justify-content:space-between;align-items:center;margin-top:8px">
      <div class="text-muted">共 ${ASFA.total} 条 · 第 ${ASFA.page} 页 · 每页 ${ASFA.pageSize} 条（有界分页）</div>
      <div>
        <button class="btn btn-neutral btn-sm" onclick="asfaPage(-1)">← 上一页</button>
        <button class="btn btn-neutral btn-sm" onclick="asfaPage(1)">下一页 →</button>
      </div>
    </div>`;
}

async function asfaLoadList() {
  const f = ASFA.filters;
  const qs = new URLSearchParams();
  qs.set('page', ASFA.page);
  qs.set('pageSize', ASFA.pageSize);
  if (f.customerId) qs.set('customerId', f.customerId);
  if (f.status !== '') qs.set('status', f.status);
  if (f.feeMethod) qs.set('feeMethod', f.feeMethod);
  if (f.currency) qs.set('currency', f.currency);
  if (f.from) qs.set('effectiveFromFrom', f.from);
  if (f.to) qs.set('effectiveFromTo', f.to);
  if (f.keyword) qs.set('keyword', f.keyword);

  try {
    const data = await api('/api/agency-service-fee-agreements?' + qs.toString());
    ASFA.list = (data && data.items) || [];
    ASFA.total = (data && data.total) || 0;
  } catch (e) {
    ASFA.list = [];
    ASFA.total = 0;
    toast('协议台账加载失败：' + e.message, 'error');
  }
}

async function asfaSearch() {
  ASFA.filters.customerId = (document.getElementById('asfa-f-customer') || {}).value || '';
  ASFA.filters.status = (document.getElementById('asfa-f-status') || {}).value || '';
  ASFA.filters.feeMethod = (document.getElementById('asfa-f-method') || {}).value || '';
  ASFA.filters.currency = (document.getElementById('asfa-f-currency') || {}).value || '';
  ASFA.filters.from = (document.getElementById('asfa-f-from') || {}).value || '';
  ASFA.filters.to = (document.getElementById('asfa-f-to') || {}).value || '';
  ASFA.filters.keyword = (document.getElementById('asfa-f-keyword') || {}).value || '';
  ASFA.page = 1;
  await asfaLoadList();
  asfaRender();
}

async function asfaPage(delta) {
  const next = ASFA.page + delta;
  if (next < 1) return;
  ASFA.page = next;
  await asfaLoadList();
  asfaRender();
}

async function asfaBackToList() {
  ASFA.view = 'list';
  ASFA.current = null;
  ASFA.form = null;
  ASFA.voidReason = '';
  await asfaLoadList();
  asfaRender();
}

/* ==================== 新建 / 编辑表单 ==================== */

function asfaToday() { return new Date().toISOString().slice(0, 10); }

async function asfaOpenForm(id) {
  if (id) {
    try {
      const row = await api('/api/agency-service-fee-agreements/' + id);
      if (!row.isDraft) { toast('只有草稿可以编辑（已登记 / 已作废证据不可修改）', 'error'); return; }
      ASFA.form = {
        id: row.id,
        agreementNo: row.agreementNo || '',
        customerId: String(row.customerId || ''),
        effectiveFrom: fmtDate(row.effectiveFrom),
        effectiveTo: fmtDate(row.effectiveTo),
        currency: row.currency || 'USD',
        feeMethod: row.feeMethod || '比例费率',
        ratePercent: row.feeMethod === '比例费率' ? String(row.ratePercent) : '',
        fixedAmount: row.feeMethod === '固定金额' ? String(row.fixedAmount) : '',
        feeBasis: row.feeBasis || '',
        remark: row.remark || ''
      };
    } catch (e) {
      toast('协议详情加载失败：' + e.message, 'error');
      return;
    }
  } else {
    ASFA.form = {
      id: null,
      agreementNo: '',
      customerId: ASFA.filters.customerId || '',
      effectiveFrom: asfaToday(),
      effectiveTo: '',
      currency: 'USD',
      feeMethod: '比例费率',
      ratePercent: '',
      fixedAmount: '',
      feeBasis: '',
      remark: ''
    };
  }

  ASFA.view = 'form';
  asfaRender();
}

function asfaFormView() {
  const f = ASFA.form;
  const customerOptions = ASFA.customers.map(c =>
    `<option value="${c.id}" ${String(c.id) === String(f.customerId) ? 'selected' : ''}>`
    + `${escapeHtml(c.customerCode || '')} ${escapeHtml(c.customerName || '')}</option>`).join('');
  const isRate = f.feeMethod === '比例费率';

  return `
    <div style="display:grid;grid-template-columns:1fr 1fr;gap:8px">
      <div><label class="ea-lb">协议号 *</label>
        <input id="asfa-no" style="width:100%" value="${escapeHtml(f.agreementNo)}"
          onchange="ASFA.form.agreementNo=this.value"></div>
      <div><label class="ea-lb">客户 *（必须存在且启用）</label>
        <select id="asfa-customer" style="width:100%" onchange="ASFA.form.customerId=this.value">
          <option value="">请选择客户…</option>${customerOptions}
        </select></div>
      <div><label class="ea-lb">生效起始日期 *</label>
        <input type="date" id="asfa-from" style="width:100%" value="${escapeHtml(f.effectiveFrom)}"
          onchange="ASFA.form.effectiveFrom=this.value"></div>
      <div><label class="ea-lb">生效结束日期（留空 = 无固定结束日）</label>
        <input type="date" id="asfa-to" style="width:100%" value="${escapeHtml(f.effectiveTo)}"
          onchange="ASFA.form.effectiveTo=this.value"></div>
      <div><label class="ea-lb">币种 *（金额一律以原币记录）</label>
        <select id="asfa-currency" style="width:100%" onchange="ASFA.form.currency=this.value">
          ${ASFA_CURRENCIES.map(c => `<option value="${c}" ${f.currency === c ? 'selected' : ''}>${c}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">披露的计费方式 *</label>
        <select id="asfa-method" style="width:100%" onchange="ASFA.form.feeMethod=this.value">
          ${ASFA_METHODS.map(m => `<option value="${m}" ${f.feeMethod === m ? 'selected' : ''}>${m}</option>`).join('')}
        </select></div>
      ${isRate ? `
      <div><label class="ea-lb">费率（%）*（大于 0 且不超过 100，保留 4 位小数）</label>
        <input type="number" step="0.0001" id="asfa-rate" style="width:100%" value="${escapeHtml(f.ratePercent)}"
          onchange="ASFA.form.ratePercent=this.value"></div>` : `
      <div><label class="ea-lb">固定金额 *（按币种精度取整后必须大于 0）</label>
        <input type="number" step="0.01" id="asfa-fixed" style="width:100%" value="${escapeHtml(f.fixedAmount)}"
          onchange="ASFA.form.fixedAmount=this.value"></div>`}
      <div><label class="ea-lb">计费依据说明 *（有界的显式人工说明，系统不据此计算金额）</label>
        <input id="asfa-basis" style="width:100%" value="${escapeHtml(f.feeBasis)}"
          onchange="ASFA.form.feeBasis=this.value"
          placeholder="例如：按出口发票金额 / 按订单 FOB 金额 / 按月固定"></div>
    </div>

    <div style="margin-top:8px;padding:8px;background:#fff7ed;border:1px solid #fed7aa;border-radius:8px;font-size:12px;color:#9a3412">
      费用条款只保存你<b>显式填写</b>的值：系统不会从<b>业务员提成</b>设置（SalesCommissionRate）、客户 / 供应商主数据比例、
      历史订单或自由文本推断费率或金额；比例费率与固定金额<b>不得同时填写</b>，不兼容组合会被拒绝而不是被静默归一化。
    </div>

    <div style="margin-top:8px"><label class="ea-lb">备注</label>
      <input id="asfa-remark" style="width:100%" value="${escapeHtml(f.remark)}"
        onchange="ASFA.form.remark=this.value"></div>

    <div style="display:flex;gap:6px;margin-top:10px;align-items:center">
      <button class="btn btn-primary" onclick="asfaSaveForm()">💾 保存草稿</button>
      <button class="btn btn-neutral" onclick="asfaBackToList()">取消</button>
      <span class="text-muted">保存只写本登记册：不会开发票、不会记账、不会授权或发起付款。</span>
    </div>`;
}

async function asfaSaveForm() {
  const f = ASFA.form || {};
  if (!f.agreementNo) { toast('请填写协议号', 'error'); return; }
  if (!f.customerId) { toast('请选择客户', 'error'); return; }
  if (!f.effectiveFrom) { toast('请填写生效起始日期', 'error'); return; }
  if (!f.feeBasis) { toast('请填写计费依据说明', 'error'); return; }
  if (f.feeMethod === '比例费率' && !f.ratePercent) { toast('请填写费率（%）', 'error'); return; }
  if (f.feeMethod === '固定金额' && !f.fixedAmount) { toast('请填写固定金额', 'error'); return; }

  const payload = {
    agreementNo: f.agreementNo,
    customerId: Number(f.customerId),
    effectiveFrom: f.effectiveFrom + 'T00:00:00',
    effectiveTo: f.effectiveTo ? f.effectiveTo + 'T00:00:00' : null,
    currency: f.currency,
    feeMethod: f.feeMethod,
    ratePercent: f.feeMethod === '比例费率' ? Number(f.ratePercent) : null,
    fixedAmount: f.feeMethod === '固定金额' ? Number(f.fixedAmount) : null,
    feeBasis: f.feeBasis,
    remark: f.remark || ''
  };

  try {
    if (f.id) await api('/api/agency-service-fee-agreements/' + f.id, 'PUT', payload);
    else await api('/api/agency-service-fee-agreements', 'POST', payload);
    toast(f.id ? '协议证据草稿已更新' : '协议证据草稿已登记（仅证据留痕，未开发票 / 未记账 / 未授权付款）');
  } catch (e) {
    toast('保存失败：' + e.message, 'error');
    return;
  }

  await asfaBackToList();
}

/* ==================== 详情 / 登记 / 作废 ==================== */

async function asfaOpenDetail(id) {
  try {
    ASFA.current = await api('/api/agency-service-fee-agreements/' + id);
  } catch (e) {
    toast('协议详情加载失败：' + e.message, 'error');
    return;
  }
  ASFA.view = 'detail';
  asfaRender();
}

function asfaDetailView() {
  const row = ASFA.current || {};
  return `
    <table class="data-table">
      <tbody>
        <tr><th style="width:180px">协议号</th><td>${escapeHtml(row.identityText || '')}</td></tr>
        <tr><th>客户</th><td>${escapeHtml(row.customerCode || '')} ${escapeHtml(row.customerName || '')}
          <div class="text-muted">${escapeHtml(row.customerAvailabilityText || '')}</div></td></tr>
        <tr><th>生效区间</th><td>${escapeHtml(row.effectiveRangeText || '')}</td></tr>
        <tr><th>币种</th><td>${escapeHtml(row.currency || '')}（小数位 ${Number(row.amountDecimals || 0)}）</td></tr>
        <tr><th>披露的费用条款</th><td>${escapeHtml(row.feeTermsText || '')}</td></tr>
        <tr><th>计费依据说明</th><td>${escapeHtml(row.feeBasis || '')}</td></tr>
        <tr><th>状态</th><td>${asfaStatusBadge(row)}</td></tr>
        <tr><th>登记时间 / 登记人</th><td>${escapeHtml(fmtDate(row.recordedAt) || '未登记')} · ${escapeHtml(row.recordedBy || '')}</td></tr>
        <tr><th>作废时间 / 原因</th><td>${row.isVoided
          ? `${escapeHtml(fmtDate(row.voidedAt))} · ${escapeHtml(row.voidReason || '')}`
          : '未作废'}</td></tr>
        <tr><th>备注</th><td>${escapeHtml(row.remark || '')}</td></tr>
      </tbody>
    </table>

    <div style="margin-top:8px;padding:8px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;font-size:12px;color:#334155">
      <div><b>费用条款口径</b>：${escapeHtml(row.feeTermsRuleText || '')}</div>
      <div style="margin-top:4px"><b>与提成模型的分离</b>：${escapeHtml(row.commissionSeparationText || '')}</div>
      <div style="margin-top:4px"><b>模块边界</b>：${escapeHtml(row.boundaryText || '')}</div>
    </div>

    <div style="display:flex;gap:6px;margin-top:10px">
      <button class="btn btn-neutral" onclick="asfaBackToList()">返回列表</button>
      ${row.isDraft ? `<button class="btn btn-neutral" onclick="asfaOpenForm(${row.id})">编辑草稿</button>
        <button class="btn btn-primary" onclick="asfaRecord(${row.id})">登记</button>` : ''}
      ${row.isVoided ? '' : `<button class="btn btn-danger" onclick="asfaOpenVoid(${row.id})">作废</button>`}
    </div>`;
}

async function asfaRecord(id) {
  if (!confirm('确认登记该代理服务费协议证据？登记后条款冻结，只可作废、不可改写（不会开发票、不会记账、不会授权付款）。')) return;
  try {
    await api('/api/agency-service-fee-agreements/' + id + '/record', 'POST');
    toast('协议证据已登记（证据已冻结，可作废但不可改写）');
  } catch (e) {
    toast('登记失败：' + e.message, 'error');
    return;
  }
  await asfaBackToList();
}

async function asfaOpenVoid(id) {
  try {
    ASFA.current = await api('/api/agency-service-fee-agreements/' + id);
  } catch (e) {
    toast('协议详情加载失败：' + e.message, 'error');
    return;
  }
  ASFA.voidReason = '';
  ASFA.view = 'void';
  asfaRender();
}

function asfaVoidView() {
  const row = ASFA.current || {};
  return `
    <div style="padding:8px;background:#fef2f2;border:1px solid #fecaca;border-radius:8px;color:#991b1b;font-size:12px">
      作废保留原始条款、客户快照、登记人与时间戳（历史可读），不会删除记录、不会改写已登记条款，也不会作废任何真实发票或产生财务动作。
    </div>
    <div style="margin-top:8px"><label class="ea-lb">作废目标</label>
      <div>${escapeHtml(row.identityText || '')} · ${escapeHtml(row.customerName || '')} · ${escapeHtml(row.feeTermsText || '')}</div></div>
    <div style="margin-top:8px"><label class="ea-lb">作废原因 *</label>
      <input id="asfa-void-reason" style="width:100%" value="${escapeHtml(ASFA.voidReason)}"
        onchange="ASFA.voidReason=this.value" placeholder="例如：协议终止 / 登记错误 / 条款更正"></div>
    <div style="display:flex;gap:6px;margin-top:10px">
      <button class="btn btn-danger" onclick="asfaConfirmVoid(${row.id})">确认作废</button>
      <button class="btn btn-neutral" onclick="asfaBackToList()">取消</button>
    </div>`;
}

async function asfaConfirmVoid(id) {
  const reason = (document.getElementById('asfa-void-reason') || {}).value || ASFA.voidReason || '';
  if (!reason.trim()) { toast('请填写作废原因（作废会保留历史证据）', 'error'); return; }
  try {
    await api('/api/agency-service-fee-agreements/' + id + '/void', 'POST', { reason: reason.trim() });
    toast('协议证据已作废（原始条款与历史保留，可读）');
  } catch (e) {
    toast('作废失败：' + e.message, 'error');
    return;
  }
  await asfaBackToList();
}




