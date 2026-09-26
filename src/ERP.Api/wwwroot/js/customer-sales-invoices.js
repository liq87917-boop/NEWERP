/* ==================================================================================
   ========== 客户销项发票证据登记册（ERP-055）— 普票 / 专票 / 出口发票 ==========
   ==================================================================================
   定位：登记客户销项发票的**运营证据**（发票类型 / 代码 / 号码 / 开票日期 / 客户 / 币种 /
         不含税金额 + 税额 + 含税总额 / 备注），并可（可选、显式）把含税总额分摊到既有销售订单，
         在发票侧显式展示已分摊 / 未分摊金额。
   金额口径：含税总额 = 不含税金额 + 税额；三项按币种精度（JPY 等 0 位，其余 2 位）取整后严格相等。
   边界（界面侧同样遵守）：
     1. 本登记册不是发票开具系统（不连税务局、不调用任何开票服务）、不是税务申报与销项税金计算、
        不是应收账款台账或余额、不是收款核销；登记 / 记录 / 作废都不会开具或作废真实发票；
     2. 只读写 CustomerSalesInvoiceEvidences / CustomerSalesInvoiceAllocations 两张表：不改写销售订单状态 /
        出货进度 / 金额与明细、客户信用状态、收款单与其引用行、库存与库存成本、装柜与单证、佣金 / 回佣、
        费用与退税记录；
     3. 分摊只允许同客户 + 同币种且未取消的销售订单：系统不会按单号 / 金额 / 日期相似度猜测订单；
        分摊金额按币种精度取整，合计不得超过含税总额；同一订单在同一发票内只能分摊一次；
     4. 与单证中心商业发票刻意分离：不转换、不替换、不自动链接，只接受**显式**的有界交叉引用留痕；
     5. 更正走显式作废（必填原因）：保留身份 / 金额 / 分摊 / 历史，不提供硬删除与静默替换。
   文案与服务端 CustomerSalesInvoiceEvidenceRules / CustomerSalesInvoiceEvidenceService 保持一致。
   ================================================================================== */

let CSI = {
  view: 'list',
  list: [], total: 0, page: 1, pageSize: 50,
  filters: {
    customerId: '', invoiceType: '', status: '', currency: '',
    dateFrom: '', dateTo: '', linkage: '', keyword: ''
  },
  customers: [],          // 客户选择项（新建 / 编辑用）
  current: null,          // 当前发票（详情 / 分摊 / 作废）
  form: null,             // 发票表单工作副本
  candidates: [],         // 可分摊销售订单候选
  candidateKeyword: '',   // 候选订单关键字
  allocate: {},           // 分摊金额工作副本：{ salesOrderId: amount }
  preview: null,          // 分摊预览结果
  docs: [],               // 可显式交叉引用的商业发票候选
  docKeyword: '',
  voidReason: '',
  hint: '',
  _prefill: null
};

const CSI_TYPES = ['普票', '专票', '出口发票'];
const CSI_CURRENCIES = ['CNY', 'USD', 'EUR', 'HKD', 'GBP', 'JPY'];
const CSI_STATUSES = [{ value: 0, label: '草稿' }, { value: 1, label: '已登记' }, { value: 2, label: '已作废' }];
const CSI_LINKAGES = [
  { value: 'linked', label: '已全额分摊' },
  { value: 'partial', label: '部分分摊' },
  { value: 'unlinked', label: '未分摊' }
];

/* 打开登记册（销售订单行操作会传入订单 Id：自动预填该单的客户与币种并在台账内预筛选） */
async function openCustomerSalesInvoiceRegister(salesOrderId) {
  CSI = {
    view: 'list', list: [], total: 0, page: 1, pageSize: 50,
    filters: {
      customerId: '', invoiceType: '', status: '', currency: '',
      dateFrom: '', dateTo: '', linkage: '', keyword: ''
    },
    customers: [], current: null, form: null, candidates: [], candidateKeyword: '',
    allocate: {}, preview: null, docs: [], docKeyword: '', voidReason: '', hint: '', _prefill: null
  };

  const modal = document.getElementById('modal');
  modal.innerHTML = '<div style="padding:40px;text-align:center;color:#64748b">正在加载…</div>';
  modal.style.display = 'block';

  try {
    const res = await api('/api/base/customers?page=1&pageSize=500');
    CSI.customers = (res && res.items) ? res.items : (Array.isArray(res) ? res : []);
  } catch (e) {
    toast('客户列表加载失败：' + e.message, 'error');
  }

  if (salesOrderId) {
    try {
      const order = await api('/api/sales-orders/' + salesOrderId);
      CSI.filters.customerId = String(order.customerId || '');
      CSI.hint = '从销售订单「' + (order.orderNo || '') + '」进入：已按该单客户预筛选；'
        + '新建发票时客户与币种（' + (order.currency || '') + '）已预填（只允许分摊同客户 + 同币种订单）。';
      CSI._prefill = {
        customerId: order.customerId,
        currency: order.currency || 'CNY',
        orderId: order.id,
        orderNo: order.orderNo || ''
      };
    } catch (e) {
      toast('销售订单加载失败：' + e.message, 'error');
    }
  }

  await csiLoadList();
  csiRender();
}

/* ==================== 列表 ==================== */

async function csiLoadList() {
  const f = CSI.filters;
  const params = ['page=' + CSI.page, 'pageSize=' + CSI.pageSize];
  if (f.customerId) params.push('customerId=' + encodeURIComponent(f.customerId));
  if (f.invoiceType) params.push('invoiceType=' + encodeURIComponent(f.invoiceType));
  if (f.status !== '') params.push('status=' + encodeURIComponent(f.status));
  if (f.currency) params.push('currency=' + encodeURIComponent(f.currency));
  if (f.dateFrom) params.push('invoiceDateFrom=' + encodeURIComponent(f.dateFrom));
  if (f.dateTo) params.push('invoiceDateTo=' + encodeURIComponent(f.dateTo));
  if (f.linkage) params.push('linkageStatus=' + encodeURIComponent(f.linkage));
  if (f.keyword) params.push('keyword=' + encodeURIComponent(f.keyword));

  try {
    const res = await api('/api/customer-sales-invoices?' + params.join('&'));
    CSI.list = (res && res.items) ? res.items : [];
    CSI.total = (res && res.total) || 0;
  } catch (e) {
    CSI.list = []; CSI.total = 0;
    toast('销项发票台账加载失败：' + e.message, 'error');
  }
}

function csiRender() {
  const modal = document.getElementById('modal');
  modal.innerHTML = `
    <div style="max-width:1320px;margin:2vh auto;background:#fff;border-radius:14px;padding:16px 18px;max-height:94vh;overflow:auto;box-shadow:0 20px 60px rgba(0,0,0,.25)">
      <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:8px">
        <h3 style="margin:0">🧾 客户销项发票证据登记册
          <span style="font-size:13px;color:#64748b;font-weight:400;margin-left:10px">
            普票 / 专票 / 出口发票运营证据；含税总额可分摊到同客户同币种销售订单
            （不是开票系统 / 税务申报 / 应收账款台账 / 收款核销）</span></h3>
        <button class="btn btn-neutral btn-sm" onclick="closeModal()">✕ 关闭</button>
      </div>
      ${CSI.hint ? `<div style="padding:6px 10px;background:#eff6ff;border:1px solid #bfdbfe;border-radius:8px;color:#1e40af;font-size:13px;margin-bottom:8px">${escapeHtml(CSI.hint)}</div>` : ''}
      <div style="padding:6px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;color:#334155;font-size:12px;margin-bottom:8px">
        金额等式：<b>含税总额 = 不含税金额 + 税额</b>（按币种精度取整后严格相等）；分摊只允许<b>同客户 + 同币种</b>且未取消的销售订单，
        合计不得超过含税总额；更正走<b>显式作废（必填原因）</b>，历史保留可读；与单证中心商业发票<b>刻意分离</b>，只接受显式交叉引用留痕。
      </div>
      ${CSI.view === 'list' ? csiListView() : ''}
      ${CSI.view === 'form' ? csiFormView() : ''}
      ${CSI.view === 'detail' ? csiDetailView() : ''}
      ${CSI.view === 'allocate' ? csiAllocationView() : ''}
      ${CSI.view === 'void' ? csiVoidView() : ''}
    </div>`;
  modal.style.display = 'block';
}

/* ==================== 列表视图 ==================== */

function csiMoney(v) { return Number(v || 0).toFixed(2); }

function csiStatusBadge(row) {
  if (row.isDraft) return '<span class="status status-warning">草稿</span>';
  if (row.isRecorded) return '<span class="status status-success">已登记</span>';
  if (row.isVoided) return '<span class="status status-neutral">已作废</span>'
    + `<div class="text-muted">${escapeHtml(row.voidReason || '')}</div>`;
  return `<span class="text-muted">${escapeHtml(row.statusText || '')}</span>`;
}

function csiLinkageBadge(row) {
  const cls = row.linkageStatus === 'linked' ? 'status-success'
    : row.linkageStatus === 'partial' ? 'status-warning' : 'status-neutral';
  return `<span class="status ${cls}">${escapeHtml(row.linkageText || '')}</span>`;
}

function csiListView() {
  const f = CSI.filters;
  const customerOptions = CSI.customers.map(c =>
    `<option value="${c.id}" ${String(c.id) === String(f.customerId) ? 'selected' : ''}>`
    + `${escapeHtml(c.customerCode || '')} ${escapeHtml(c.customerName || '')}</option>`).join('');

  return `
    <div style="display:flex;gap:6px;flex-wrap:wrap;align-items:end;margin-bottom:8px">
      <div><label class="ea-lb">客户</label>
        <select id="csi-f-customer" style="min-width:180px">
          <option value="">全部客户</option>${customerOptions}
        </select></div>
      <div><label class="ea-lb">发票类型</label>
        <select id="csi-f-type">
          <option value="">全部类型</option>
          ${CSI_TYPES.map(t => `<option value="${t}" ${f.invoiceType === t ? 'selected' : ''}>${t}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">状态</label>
        <select id="csi-f-status">
          <option value="">全部（含已作废历史）</option>
          ${CSI_STATUSES.map(s => `<option value="${s.value}" ${String(s.value) === String(f.status) ? 'selected' : ''}>${s.label}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">币种</label>
        <select id="csi-f-currency">
          <option value="">全部币种</option>
          ${CSI_CURRENCIES.map(c => `<option value="${c}" ${f.currency === c ? 'selected' : ''}>${c}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">分摊状态</label>
        <select id="csi-f-linkage">
          <option value="">全部分摊状态</option>
          ${CSI_LINKAGES.map(l => `<option value="${l.value}" ${f.linkage === l.value ? 'selected' : ''}>${l.label}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">开票日期从</label>
        <input type="date" id="csi-f-from" value="${escapeHtml(f.dateFrom)}"></div>
      <div><label class="ea-lb">到</label>
        <input type="date" id="csi-f-to" value="${escapeHtml(f.dateTo)}"></div>
      <div><label class="ea-lb">关键字</label>
        <input id="csi-f-keyword" style="min-width:160px" value="${escapeHtml(f.keyword)}"
          placeholder="发票号 / 代码 / 客户 / 商业发票引用"></div>
      <button class="btn btn-primary btn-sm" onclick="csiSearch()">🔍 查询</button>
      <button class="btn btn-neutral btn-sm" onclick="csiOpenForm(null)">➕ 新建发票证据</button>
    </div>

    <table class="data-table">
      <thead><tr>
        <th>发票</th><th>客户</th><th>开票日期</th>
        <th style="text-align:right">净额 + 税额 = 含税总额</th>
        <th style="text-align:right">已分摊 / 未分摊</th>
        <th>状态</th><th>操作</th>
      </tr></thead>
      <tbody>
        ${CSI.list.length === 0 ? '<tr><td colspan="7" class="text-muted">没有符合条件的销项发票证据（既有客户 / 销售订单 / 单证不因本登记册产生任何变化，不做历史回填）。</td></tr>' : ''}
        ${CSI.list.map(row => `
          <tr>
            <td>${escapeHtml(row.identityText || '')}
              <div class="text-muted">${escapeHtml(row.invoiceType || '')} · ${escapeHtml(row.currency || '')}</div></td>
            <td>${escapeHtml(row.customerName || '')}
              <div class="text-muted">${escapeHtml(row.customerCode || '')} · ${escapeHtml(row.customerAvailabilityText || '')}</div></td>
            <td>${fmtDate(row.invoiceDate)}</td>
            <td style="text-align:right">${csiMoney(row.netAmount)} + ${csiMoney(row.taxAmount)}
              = <b>${csiMoney(row.grossAmount)}</b> ${escapeHtml(row.currency || '')}</td>
            <td style="text-align:right">${csiMoney(row.linkedAmount)}
              <div class="text-muted">未分摊 ${csiMoney(row.unlinkedAmount)}</div></td>
            <td>${csiStatusBadge(row)}</td>
            <td>
              <button class="btn btn-neutral btn-sm" onclick="csiOpenDetail(${row.id})">详情</button>
              ${row.isDraft ? `<button class="btn btn-neutral btn-sm" onclick="csiOpenForm(${row.id})">编辑</button>
                <button class="btn btn-primary btn-sm" onclick="csiOpenAllocations(${row.id})">分摊</button>
                <button class="btn btn-primary btn-sm" onclick="csiRecord(${row.id})">登记</button>` : ''}
              ${!row.isVoided ? `<button class="btn btn-danger btn-sm" onclick="csiOpenVoid(${row.id})">作废</button>` : ''}
            </td>
          </tr>`).join('')}
      </tbody>
    </table>

    <div style="display:flex;justify-content:space-between;align-items:center;margin-top:8px">
      <div class="text-muted">共 ${CSI.total} 条 · 第 ${CSI.page} 页 · 每页 ${CSI.pageSize} 条（有界分页）</div>
      <div>
        <button class="btn btn-neutral btn-sm" onclick="csiPage(-1)">← 上一页</button>
        <button class="btn btn-neutral btn-sm" onclick="csiPage(1)">下一页 →</button>
      </div>
    </div>`;
}

async function csiSearch() {
  CSI.filters.customerId = (document.getElementById('csi-f-customer') || {}).value || '';
  CSI.filters.invoiceType = (document.getElementById('csi-f-type') || {}).value || '';
  CSI.filters.status = (document.getElementById('csi-f-status') || {}).value || '';
  CSI.filters.currency = (document.getElementById('csi-f-currency') || {}).value || '';
  CSI.filters.linkage = (document.getElementById('csi-f-linkage') || {}).value || '';
  CSI.filters.dateFrom = (document.getElementById('csi-f-from') || {}).value || '';
  CSI.filters.dateTo = (document.getElementById('csi-f-to') || {}).value || '';
  CSI.filters.keyword = (document.getElementById('csi-f-keyword') || {}).value || '';
  CSI.page = 1;
  await csiLoadList();
  csiRender();
}

async function csiPage(delta) {
  const next = CSI.page + delta;
  if (next < 1) return;
  CSI.page = next;
  await csiLoadList();
  csiRender();
}

async function csiBackToList() {
  CSI.view = 'list';
  CSI.current = null;
  CSI.form = null;
  CSI.preview = null;
  CSI.voidReason = '';
  await csiLoadList();
  csiRender();
}

/* ==================== 新建 / 编辑草稿 ==================== */

function csiOpenForm(id) {
  const prefill = CSI._prefill || null;

  if (id) {
    const row = CSI.list.find(x => x.id === id) || CSI.current;
    if (!row || row.id !== id) { toast('请先刷新台账再编辑', 'error'); return; }
    CSI.current = row;
    CSI.form = {
      id: row.id,
      invoiceType: row.invoiceType,
      invoiceCode: row.invoiceCode || '',
      invoiceNumber: row.invoiceNumber || '',
      invoiceDate: fmtDate(row.invoiceDate),
      customerId: row.customerId,
      currency: row.currency,
      netAmount: row.netAmount,
      taxAmount: row.taxAmount,
      grossAmount: row.grossAmount,
      tradeDocumentId: row.tradeDocumentId || '',
      commercialInvoiceReference: row.commercialInvoiceReference || '',
      remark: row.remark || ''
    };
  } else {
    CSI.current = null;
    CSI.form = {
      id: null, invoiceType: '普票', invoiceCode: '', invoiceNumber: '',
      invoiceDate: todayISO(),
      customerId: prefill ? prefill.customerId : '',
      currency: prefill ? prefill.currency : 'CNY',
      netAmount: 0, taxAmount: 0, grossAmount: 0,
      tradeDocumentId: '', commercialInvoiceReference: '',
      remark: prefill && prefill.orderNo ? ('对应销售订单 ' + prefill.orderNo) : ''
    };
  }

  CSI.docs = [];
  CSI.docKeyword = '';
  CSI.view = 'form';
  csiRender();
  csiLoadDocCandidates('');
}

async function csiLoadDocCandidates(keyword) {
  CSI.docKeyword = keyword || '';
  try {
    const params = 'take=200' + (keyword ? '&keyword=' + encodeURIComponent(keyword) : '');
    CSI.docs = await api('/api/customer-sales-invoices/commercial-invoice-candidates?' + params) || [];
  } catch (e) {
    CSI.docs = [];
    toast('商业发票候选加载失败：' + e.message, 'error');
  }
  if (CSI.view === 'form') csiRender();
}

async function csiDocSearch() {
  const el = document.getElementById('csi-doc-keyword');
  await csiLoadDocCandidates(el ? el.value : '');
}

async function csiSaveForm() {
  const f = CSI.form;
  if (!f) return;
  if (!String(f.customerId || '')) { toast('请选择客户', 'error'); return; }
  if (!String(f.invoiceNumber || '').trim()) { toast('请填写发票号码', 'error'); return; }
  if (f.invoiceType === '专票' && !String(f.invoiceCode || '').trim()) {
    toast('专票必须填写发票代码', 'error'); return;
  }
  if (!f.invoiceDate) { toast('请填写开票日期', 'error'); return; }

  const body = {
    invoiceType: f.invoiceType,
    invoiceCode: f.invoiceCode || '',
    invoiceNumber: String(f.invoiceNumber).trim(),
    invoiceDate: f.invoiceDate + 'T00:00:00',
    customerId: Number(f.customerId),
    currency: f.currency,
    netAmount: Number(f.netAmount || 0),
    taxAmount: Number(f.taxAmount || 0),
    grossAmount: Number(f.grossAmount || 0),
    tradeDocumentId: f.tradeDocumentId ? Number(f.tradeDocumentId) : null,
    commercialInvoiceReference: f.commercialInvoiceReference || '',
    remark: f.remark || ''
  };

  try {
    const saved = f.id
      ? await api('/api/customer-sales-invoices/' + f.id, 'PUT', body)
      : await api('/api/customer-sales-invoices', 'POST', body);
    toast(f.id ? '销项发票证据草稿已更新' : '销项发票证据草稿已保存（未登记前可继续修改与分摊）');
    CSI._prefill = null;
    CSI.current = saved;
    CSI.view = 'detail';
    await csiLoadList();
    csiRender();
  } catch (e) {
    toast('保存失败：' + e.message, 'error');
  }
}

function csiFormView() {
  const f = CSI.form;
  if (!f) return '<div class="text-muted">无可编辑的发票草稿。</div>';

  const customerOptions = CSI.customers.map(c =>
    `<option value="${c.id}" ${String(c.id) === String(f.customerId) ? 'selected' : ''}>`
    + `${escapeHtml(c.customerCode || '')} ${escapeHtml(c.customerName || '')}`
    + `${c.status === 1 ? '' : '（已停用）'}</option>`).join('');

  const docOptions = CSI.docs.map(d =>
    `<option value="${d.tradeDocumentId}" ${String(d.tradeDocumentId) === String(f.tradeDocumentId) ? 'selected' : ''}>`
    + `${escapeHtml(d.docNo || '')} · ${escapeHtml(d.salesOrderNo || '')} · ${escapeHtml(d.currency || '')}</option>`).join('');

  return `
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <h4 style="margin:0">${f.id ? '编辑销项发票证据草稿' : '新建销项发票证据草稿'}
        <span style="font-size:12px;color:#64748b;font-weight:400;margin-left:8px">草稿可修改；登记后冻结证据（可作废，不可改写）</span></h4>
      <button class="btn btn-neutral btn-sm" onclick="csiBackToList()">← 返回台账</button>
    </div>

    <div style="display:grid;grid-template-columns:repeat(3,1fr);gap:8px">
      <div><label class="ea-lb">发票类型 *</label>
        <select id="csi-type" style="width:100%" onchange="CSI.form.invoiceType=this.value;csiRender()">
          ${CSI_TYPES.map(t => `<option value="${t}" ${f.invoiceType === t ? 'selected' : ''}>${t}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">发票代码 ${f.invoiceType === '专票' ? '*（专票必填）' : '（可选）'}</label>
        <input id="csi-code" style="width:100%" value="${escapeHtml(f.invoiceCode)}"
          onchange="CSI.form.invoiceCode=this.value"></div>
      <div><label class="ea-lb">发票号码 *</label>
        <input id="csi-number" style="width:100%" value="${escapeHtml(f.invoiceNumber)}"
          onchange="CSI.form.invoiceNumber=this.value"></div>
      <div><label class="ea-lb">开票日期 *</label>
        <input type="date" id="csi-date" style="width:100%" value="${escapeHtml(f.invoiceDate)}"
          onchange="CSI.form.invoiceDate=this.value"></div>
      <div><label class="ea-lb">客户 *（必须存在且启用）</label>
        <select id="csi-customer" style="width:100%" onchange="CSI.form.customerId=this.value">
          <option value="">请选择客户…</option>${customerOptions}
        </select></div>
      <div><label class="ea-lb">币种 *（分摊订单时要求一致）</label>
        <select id="csi-currency" style="width:100%" onchange="CSI.form.currency=this.value">
          ${CSI_CURRENCIES.map(c => `<option value="${c}" ${f.currency === c ? 'selected' : ''}>${c}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">不含税金额（净额）*</label>
        <input type="number" step="0.01" id="csi-net" style="width:100%" value="${f.netAmount}"
          onchange="CSI.form.netAmount=this.value"></div>
      <div><label class="ea-lb">税额 *（允许 0）</label>
        <input type="number" step="0.01" id="csi-tax" style="width:100%" value="${f.taxAmount}"
          onchange="CSI.form.taxAmount=this.value"></div>
      <div><label class="ea-lb">含税总额 *（必须 = 净额 + 税额）</label>
        <input type="number" step="0.01" id="csi-gross" style="width:100%" value="${f.grossAmount}"
          onchange="CSI.form.grossAmount=this.value"></div>
    </div>

    <div style="margin-top:8px;padding:8px;background:#fff7ed;border:1px solid #fed7aa;border-radius:8px">
      <div style="font-size:12px;color:#9a3412;margin-bottom:6px">
        与单证中心商业发票<b>刻意分离</b>：单证中心的商业发票是出口报关用的单证快照，本登记册是账务 / 税务口径的发票证据；
        系统不会转换、替换或自动链接任何单证 —— 下面只做<b>显式</b>交叉引用留痕（不读取单证金额、不参与金额派生）。
      </div>
      <div style="display:grid;grid-template-columns:1fr 1fr;gap:8px">
        <div><label class="ea-lb">显式引用单证中心商业发票（可选）</label>
          <select id="csi-doc" style="width:100%" onchange="CSI.form.tradeDocumentId=this.value">
            <option value="">不引用任何单证（不建立自动链接）</option>${docOptions}
          </select>
          <div style="display:flex;gap:6px;margin-top:4px">
            <input id="csi-doc-keyword" style="flex:1" value="${escapeHtml(CSI.docKeyword)}"
              placeholder="按单证号 / 销售订单号检索">
            <button class="btn btn-neutral btn-sm" onclick="csiDocSearch()">检索</button>
          </div></div>
        <div><label class="ea-lb">商业发票号 / 引用说明（可选，有界文本）</label>
          <input id="csi-comref" style="width:100%" value="${escapeHtml(f.commercialInvoiceReference)}"
            onchange="CSI.form.commercialInvoiceReference=this.value"
            placeholder="仅人工留痕，系统不会据此自动匹配或链接"></div>
      </div>
    </div>

    <div style="margin-top:8px"><label class="ea-lb">备注</label>
      <input id="csi-remark" style="width:100%" value="${escapeHtml(f.remark)}"
        onchange="CSI.form.remark=this.value"></div>

    <div style="display:flex;gap:6px;margin-top:10px;align-items:center">
      <button class="btn btn-primary" onclick="csiSaveForm()">💾 保存草稿</button>
      <button class="btn btn-neutral" onclick="csiBackToList()">取消</button>
      <span class="text-muted">
        保存只写本登记册：不会开票、不会报税、不会记账，也不改写销售订单、客户、收款单与单证数据。
      </span>
    </div>`;
}

/* ==================== 详情 ==================== */

async function csiOpenDetail(id) {
  try {
    CSI.current = await api('/api/customer-sales-invoices/' + id);
  } catch (e) {
    toast('发票详情加载失败：' + e.message, 'error'); return;
  }
  CSI.preview = null;
  CSI.view = 'detail';
  csiRender();
}

function csiDetailView() {
  const inv = CSI.current;
  if (!inv) return '<div class="text-muted">请选择一张发票。</div>';

  const rows = inv.allocations || [];

  return `
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <h4 style="margin:0">${escapeHtml(inv.identityText || '')}
        <span style="font-size:12px;color:#64748b;font-weight:400;margin-left:8px">
          ${escapeHtml(inv.statusText || '')} · ${escapeHtml(inv.customerName || '')}（${escapeHtml(inv.customerCode || '')}）</span></h4>
      <div style="display:flex;gap:6px">
        ${inv.isDraft ? `<button class="btn btn-neutral btn-sm" onclick="csiOpenForm(${inv.id})">编辑</button>
          <button class="btn btn-primary btn-sm" onclick="csiOpenAllocations(${inv.id})">分摊到销售订单</button>
          <button class="btn btn-primary btn-sm" onclick="csiRecord(${inv.id})">登记</button>` : ''}
        ${!inv.isVoided ? `<button class="btn btn-danger btn-sm" onclick="csiOpenVoid(${inv.id})">作废</button>` : ''}
        ${!inv.isDraft && !inv.isVoided ? `<button class="btn btn-primary btn-sm" onclick="openCustomerSalesInvoiceCollectionAllocationRegister(${inv.id})">收款分摊</button>` : ''}
        <button class="btn btn-neutral btn-sm" onclick="csiBackToList()">← 返回台账</button>
      </div>
    </div>

    <div style="display:grid;grid-template-columns:repeat(4,1fr);gap:8px;margin-bottom:8px">
      <div class="text-muted">发票类型<div><b>${escapeHtml(inv.invoiceType || '')}</b></div></div>
      <div class="text-muted">发票代码 / 号码<div><b>${escapeHtml(inv.invoiceCode || '—')} / ${escapeHtml(inv.invoiceNumber || '')}</b></div></div>
      <div class="text-muted">开票日期<div><b>${fmtDate(inv.invoiceDate)}</b></div></div>
      <div class="text-muted">客户可用性<div><b>${escapeHtml(inv.customerAvailabilityText || '')}</b></div></div>
      <div class="text-muted">不含税金额（净额）<div><b>${csiMoney(inv.netAmount)} ${escapeHtml(inv.currency || '')}</b></div></div>
      <div class="text-muted">税额<div><b>${csiMoney(inv.taxAmount)} ${escapeHtml(inv.currency || '')}</b></div></div>
      <div class="text-muted">含税总额<div><b>${csiMoney(inv.grossAmount)} ${escapeHtml(inv.currency || '')}</b></div></div>
      <div class="text-muted">已分摊 / 未分摊<div><b>${csiMoney(inv.linkedAmount)} / ${csiMoney(inv.unlinkedAmount)}</b></div></div>
    </div>

    <div style="padding:6px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;font-size:12px;margin-bottom:8px">
      ${csiLinkageBadge(inv)}
      <div class="text-muted" style="margin-top:4px">${escapeHtml(inv.amountEquationText || '')}</div>
      <div class="text-muted">${escapeHtml(inv.linkageRuleText || '')}</div>
      <div class="text-muted">未分摊部分不会被系统猜测到任何订单，也不代表应收未收余额、未开票税金或催收依据。</div>
    </div>

    <div style="padding:6px 10px;background:#fff7ed;border:1px solid #fed7aa;border-radius:8px;font-size:12px;margin-bottom:8px">
      <b>与单证中心商业发票的关系（只作证据留痕）</b>
      <div>显式引用单证：${inv.tradeDocumentId
        ? `${escapeHtml(inv.tradeDocumentNo || '')}（${escapeHtml(inv.tradeDocumentDocType || '')}）`
        : '未引用任何单证（不建立自动链接）'}</div>
      <div>引用说明：${escapeHtml(inv.commercialInvoiceReference || '—')}</div>
      <div class="text-muted">${escapeHtml(inv.tradeDocumentAvailabilityText || '')}</div>
      <div class="text-muted">${escapeHtml(inv.tradeDocumentSeparationText || '')}</div>
    </div>

    ${inv.isVoided ? `<div style="padding:6px 10px;background:#fef2f2;border:1px solid #fecaca;border-radius:8px;font-size:12px;margin-bottom:8px">
      已作废：${escapeHtml(inv.voidReason || '')}（作废时间 ${fmtDate(inv.voidedAt)}）—— 身份 / 金额 / 分摊 / 历史保留可读，
      未分摊与已分摊金额都保留作废当时口径，也不会作废任何真实发票。</div>` : ''}

    <h5 style="margin:8px 0 4px">分摊到销售订单（${rows.length} 条）</h5>
    <table class="data-table">
      <thead><tr><th>销售订单</th><th>订单日期</th><th>订单状态</th><th style="text-align:right">分摊金额</th><th>备注</th></tr></thead>
      <tbody>
        ${rows.length === 0 ? '<tr><td colspan="5" class="text-muted">该发票尚未分摊到任何销售订单（允许部分或全部分摊，系统不会按相似度猜测订单）。</td></tr>' : ''}
        ${rows.map(a => `
          <tr>
            <td>${escapeHtml(a.orderNo || '')}
              <div class="text-muted">${escapeHtml(a.customerName || '')} · ${escapeHtml(a.orderCurrency || '')}</div></td>
            <td>${fmtDate(a.orderDate)}</td>
            <td>${escapeHtml(a.orderStatusText || '')}
              <div class="text-muted">${escapeHtml(a.orderAvailabilityText || '')}</div></td>
            <td style="text-align:right"><b>${csiMoney(a.allocatedAmount)}</b> ${escapeHtml(a.currency || '')}</td>
            <td>${escapeHtml(a.remark || '')}</td>
          </tr>`).join('')}
      </tbody>
    </table>

    <div style="padding:6px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;font-size:12px;margin-top:8px">
      ${escapeHtml(inv.boundaryText || '')}
    </div>`;
}

/* 登记草稿（草稿 → 已登记：冻结证据；不开票、不报税、不记账） */
async function csiRecord(id) {
  if (!confirm('确认登记该销项发票证据？\n\n登记会冻结身份 / 金额 / 分摊（之后只能作废，不能改写）；'
    + '系统不会开具真实发票、不会调用税务服务、不会改写销售订单与客户数据。')) return;

  try {
    CSI.current = await api('/api/customer-sales-invoices/' + id + '/record', 'POST', {});
    toast('销项发票证据已登记（证据已冻结；未开票、未报税、未记账）');
    CSI.view = 'detail';
    await csiLoadList();
    csiRender();
  } catch (e) {
    toast('登记失败：' + e.message, 'error');
  }
}

/* ==================== 分摊到销售订单 ==================== */

async function csiOpenAllocations(id) {
  try {
    CSI.current = await api('/api/customer-sales-invoices/' + id);
  } catch (e) {
    toast('发票加载失败：' + e.message, 'error'); return;
  }

  // 分摊金额工作副本：以当前已持久化分摊行初始化
  CSI.allocate = {};
  (CSI.current.allocations || []).forEach(a => { CSI.allocate[a.salesOrderId] = a.allocatedAmount; });
  CSI.preview = null;
  CSI.view = 'allocate';
  await csiLoadCandidates('');
  csiRender();
}

function csiAllocateArray() {
  return Object.keys(CSI.allocate)
    .map(k => ({ salesOrderId: Number(k), allocatedAmount: Number(CSI.allocate[k] || 0) }))
    .filter(x => x.allocatedAmount > 0);
}

function csiAllocatedTotal() {
  return csiAllocateArray().reduce((sum, x) => sum + x.allocatedAmount, 0);
}

async function csiLoadCandidates(keyword) {
  const inv = CSI.current;
  if (!inv) return;
  CSI.candidateKeyword = keyword || '';
  try {
    const params = 'take=200' + (keyword ? '&keyword=' + encodeURIComponent(keyword) : '');
    CSI.candidates = await api('/api/customer-sales-invoices/' + inv.id + '/order-candidates?' + params) || [];
  } catch (e) {
    CSI.candidates = [];
    toast('可分摊销售订单加载失败：' + e.message, 'error');
  }
}

function csiSetAmount(orderId, value) {
  const amount = Number(value || 0);
  if (amount > 0) CSI.allocate[orderId] = amount;
  else delete CSI.allocate[orderId];
  CSI.preview = null;
  csiRender();
}

async function csiCandidateSearch() {
  const el = document.getElementById('csi-cand-keyword');
  await csiLoadCandidates(el ? el.value : '');
  csiRender();
}

async function csiPreview() {
  const inv = CSI.current;
  if (!inv) return;
  try {
    CSI.preview = await api('/api/customer-sales-invoices/' + inv.id + '/allocations/preview', 'POST',
      { lines: csiAllocateArray() });
    csiRender();
  } catch (e) {
    toast('预览失败：' + e.message, 'error');
  }
}

async function csiSaveAllocations() {
  const inv = CSI.current;
  if (!inv) return;
  const lines = csiAllocateArray();
  if (!confirm(`确认保存 ${lines.length} 条分摊（合计 ${csiMoney(csiAllocatedTotal())} ${inv.currency}）？\n\n`
    + '保存只写本登记册的分摊证据：不会开票、不会报税、不会改写销售订单状态 / 出货进度 / 金额与明细。')) return;

  try {
    CSI.current = await api('/api/customer-sales-invoices/' + inv.id + '/allocations', 'POST', { lines: lines });
    toast('发票分摊已保存（仅证据留痕；未开票、未收款、未核销）');
    CSI.preview = null;
    CSI.view = 'detail';
    await csiLoadList();
    csiRender();
  } catch (e) {
    toast('保存分摊失败：' + e.message, 'error');
  }
}

function csiAllocationView() {
  const inv = CSI.current;
  if (!inv) return '<div class="text-muted">请选择一张发票。</div>';

  const total = csiAllocatedTotal();
  const unlinked = Math.max(0, Number(inv.grossAmount || 0) - total);

  return `
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <h4 style="margin:0">分摊「${escapeHtml(inv.identityText || '')}」
        <span style="font-size:12px;color:#64748b;font-weight:400;margin-left:8px">
          含税总额 ${csiMoney(inv.grossAmount)} ${escapeHtml(inv.currency || '')} · 拟分摊 ${csiMoney(total)} · 未分摊 ${csiMoney(unlinked)}</span></h4>
      <button class="btn btn-neutral btn-sm" onclick="csiOpenDetail(${inv.id})">← 返回详情</button>
    </div>

    <div style="padding:6px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;font-size:12px;margin-bottom:8px">
      只允许分摊到<b>同客户（${escapeHtml(inv.customerName || '')}）+ 同币种（${escapeHtml(inv.currency || '')}）</b>且未取消的销售订单；
      分摊金额按币种精度取整、合计不得超过含税总额；同一订单只能分摊一次。草稿期整体替换，登记后冻结。
      未分摊部分不会被系统猜测到任何订单。
    </div>

    <div style="display:flex;gap:6px;margin-bottom:6px">
      <input id="csi-cand-keyword" style="flex:1" value="${escapeHtml(CSI.candidateKeyword)}"
        placeholder="按销售订单号 / 合同号检索">
      <button class="btn btn-neutral btn-sm" onclick="csiCandidateSearch()">检索</button>
      <button class="btn btn-neutral btn-sm" onclick="csiPreview()">预览校验（不写库）</button>
      <button class="btn btn-primary btn-sm" onclick="csiSaveAllocations()">💾 保存分摊</button>
    </div>

    ${CSI.preview ? `<div style="padding:6px 10px;background:#eff6ff;border:1px solid #bfdbfe;border-radius:8px;color:#1e40af;font-size:12px;margin-bottom:8px">
      预览（未写库）：${escapeHtml(CSI.preview.linkageText || '')}；拟分摊合计 ${csiMoney(CSI.preview.proposedLinkedAmount)}
      ${escapeHtml(CSI.preview.currency || '')}，未分摊 ${csiMoney(CSI.preview.unlinkedAmount)}。</div>` : ''}

    <table class="data-table">
      <thead><tr>
        <th>销售订单</th><th>订单日期</th><th>订单状态</th><th style="text-align:right">订单总额</th>
        <th style="text-align:right">其他发票已分摊</th><th style="text-align:right">本发票分摊金额</th><th>资格</th>
      </tr></thead>
      <tbody>
        ${CSI.candidates.length === 0 ? '<tr><td colspan="7" class="text-muted">没有同客户 + 同币种的销售订单（系统不会跨客户、跨币种或按相似度猜测订单）。</td></tr>' : ''}
        ${CSI.candidates.map(c => `
          <tr>
            <td>${escapeHtml(c.orderNo || '')}
              <div class="text-muted">${escapeHtml(c.customerName || '')} · ${escapeHtml(c.currency || '')}</div></td>
            <td>${fmtDate(c.orderDate)}</td>
            <td>${escapeHtml(c.orderStatusText || '')}</td>
            <td style="text-align:right">${csiMoney(c.orderedAmount)}</td>
            <td style="text-align:right">${csiMoney(c.allocatedByOtherInvoices)}</td>
            <td style="text-align:right">
              <input type="number" step="0.01" style="width:120px;text-align:right"
                value="${CSI.allocate[c.salesOrderId] !== undefined ? CSI.allocate[c.salesOrderId] : ''}"
                ${c.eligible ? '' : 'disabled'}
                onchange="csiSetAmount(${c.salesOrderId}, this.value)"></td>
            <td>${c.eligible
              ? '<span class="status status-success">可分摊</span>'
              : '<span class="status status-neutral">不可分摊</span>'}
              <div class="text-muted">${escapeHtml(c.eligibilityText || '')}</div></td>
          </tr>`).join('')}
      </tbody>
    </table>`;
}

/* ==================== 作废 ==================== */

async function csiOpenVoid(id) {
  try {
    CSI.current = await api('/api/customer-sales-invoices/' + id);
  } catch (e) {
    toast('发票加载失败：' + e.message, 'error'); return;
  }
  CSI.voidReason = '';
  CSI.view = 'void';
  csiRender();
}

function csiVoidView() {
  const inv = CSI.current;
  if (!inv) return '<div class="text-muted">请选择一张发票。</div>';

  return `
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <h4 style="margin:0">作废「${escapeHtml(inv.identityText || '')}」
        <span style="font-size:12px;color:#64748b;font-weight:400;margin-left:8px">${csiMoney(inv.grossAmount)} ${escapeHtml(inv.currency || '')}</span></h4>
      <button class="btn btn-neutral btn-sm" onclick="csiOpenDetail(${inv.id})">← 返回详情</button>
    </div>

    <div style="padding:6px 10px;background:#fef2f2;border:1px solid #fecaca;border-radius:8px;font-size:12px;margin-bottom:8px">
      作废会<b>保留</b>发票身份 / 金额 / 分摊行 / 审计历史（不物理删除、不静默改写），分摊行与未分摊金额照常可读；
      作废<b>不会</b>作废任何真实发票、不会调用税务服务，也不会改写销售订单、客户信用状态、收款单与其引用行、库存与财务记录。
    </div>

    <label class="ea-lb">作废原因 *（必填：必须记录更正原因）</label>
    <input id="csi-void-reason" style="width:100%" value="${escapeHtml(CSI.voidReason)}"
      onchange="CSI.voidReason=this.value" placeholder="例如：发票号码录错，重新登记">

    <div style="display:flex;gap:6px;margin-top:10px">
      <button class="btn btn-danger" onclick="csiConfirmVoid(${inv.id})">确认作废</button>
      <button class="btn btn-neutral" onclick="csiOpenDetail(${inv.id})">取消</button>
    </div>`;
}

async function csiConfirmVoid(id) {
  const reason = String(CSI.voidReason || '').trim();
  if (!reason) { toast('请填写作废原因', 'error'); return; }
  if (!confirm('确认作废该销项发票证据？\n\n原始身份 / 金额 / 分摊 / 历史会保留可读，且不会作废任何真实发票。')) return;

  try {
    CSI.current = await api('/api/customer-sales-invoices/' + id + '/void', 'POST', { reason: reason });
    toast('销项发票证据已作废（历史证据与分摊保留，可读）');
    CSI.view = 'detail';
    await csiLoadList();
    csiRender();
  } catch (e) {
    toast('作废失败：' + e.message, 'error');
  }
}







