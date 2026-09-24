/* ==================================================================================
   ========== 供应商采购发票登记册（ERP-043）— 普票 / 专票证据 + 采购订单关联 ==========
   ==================================================================================
   定位：登记普通发票 / 增值税专用发票的**运营证据**，并可（可选、显式）把含税总额
         全部或部分关联到**同供应商 + 同币种**的既有采购订单。
   边界（界面侧同样遵守）：
     1. 本登记册不是应付账款台账、不是税务申报系统、也不是付款授权机制；
     2. 登记 / 作废只改本模块两张表：不改写采购订单状态 / 到货进度 / 金额与明细、库存与库存成本、
        退税记录、供应商余额与付款状态，也不生成凭证 / 收款 / 付款 / 结算单；
     3. 金额等式：含税总额 = 不含税金额 + 税额（服务端按币种精度取整后严格校验），界面只做提示；
     4. 关联只允许同供应商 + 同币种订单：系统不会按单号 / 金额 / 开票日期相似度猜测订单；
     5. 已登记证据冻结（可作废、可读，不可改写）；作废保留身份 / 金额 / 关联 / 审计历史。
   文案与服务端 PurchaseInvoiceRules / PurchaseInvoiceService 保持一致。
   ================================================================================== */

let PIR = {
  view: 'list',
  list: [], total: 0, page: 1, pageSize: 50,
  filters: {
    supplierId: '', invoiceType: '', status: '', currency: '',
    dateFrom: '', dateTo: '', linkage: '', keyword: ''
  },
  suppliers: [],
  current: null,          // 当前发票 DTO（表单 / 关联 / 详情）
  form: null,             // 表单工作副本
  candidates: [],         // 可关联采购订单候选
  candidateKeyword: '',   // 候选订单关键字
  allocate: {},           // 关联金额工作副本：{ purchaseOrderId: amount }
  preview: null,          // 关联预览结果
  voidReason: '',         // 作废原因工作副本
  hint: ''                // 从采购订单行操作进入时的预填提示
};

const PIR_TYPES = ['普票', '专票'];
const PIR_CURRENCIES = ['CNY', 'USD', 'EUR', 'HKD', 'GBP', 'JPY'];
const PIR_STATUSES = [{ value: 0, label: '草稿' }, { value: 1, label: '已登记' }, { value: 2, label: '已作废' }];
const PIR_LINKAGES = [
  { value: 'linked', label: '已全额关联' },
  { value: 'partial', label: '部分关联' },
  { value: 'unlinked', label: '未关联' }
];

/* 打开登记册（采购订单行操作会传入订单 Id：自动预填该单的供应商与币种） */
async function openPurchaseInvoiceRegister(purchaseOrderId) {
  PIR = {
    view: 'list', list: [], total: 0, page: 1, pageSize: 50,
    filters: {
      supplierId: '', invoiceType: '', status: '', currency: '',
      dateFrom: '', dateTo: '', linkage: '', keyword: ''
    },
    suppliers: [], current: null, form: null, candidates: [], candidateKeyword: '',
    allocate: {}, preview: null, voidReason: '', hint: ''
  };
  const modal = document.getElementById('modal');
  modal.innerHTML = '<div style="padding:40px;text-align:center;color:#64748b">正在加载…</div>';
  modal.style.display = 'block';

  try {
    const res = await api('/api/base/suppliers?page=1&pageSize=500');
    PIR.suppliers = (res && res.items) ? res.items : (Array.isArray(res) ? res : []);
  } catch (e) {
    toast('供应商列表加载失败：' + e.message, 'error');
  }

  if (purchaseOrderId) {
    try {
      const order = await api('/api/purchase-orders/' + purchaseOrderId);
      PIR.filters.supplierId = String(order.supplierId || '');
      PIR.filters.keyword = '';
      PIR.hint = '从采购订单「' + (order.orderNo || '') + '」进入：已按该单供应商预筛选；'
        + '新建发票时供应商与币种（' + (order.currency || '') + '）已预填（只允许关联同供应商 + 同币种订单）。';
      PIR._prefill = {
        supplierId: order.supplierId,
        currency: order.currency || 'CNY',
        orderId: order.id,
        orderNo: order.orderNo || ''
      };
    } catch (e) {
      toast('采购订单加载失败：' + e.message, 'error');
    }
  }

  await pirLoadList();
  pirRender();
}

/* ==================== 列表 ==================== */

async function pirLoadList() {
  const f = PIR.filters;
  const params = ['page=' + PIR.page, 'pageSize=' + PIR.pageSize];
  if (f.supplierId) params.push('supplierId=' + encodeURIComponent(f.supplierId));
  if (f.invoiceType) params.push('invoiceType=' + encodeURIComponent(f.invoiceType));
  if (f.status !== '') params.push('status=' + encodeURIComponent(f.status));
  if (f.currency) params.push('currency=' + encodeURIComponent(f.currency));
  if (f.dateFrom) params.push('invoiceDateFrom=' + encodeURIComponent(f.dateFrom));
  if (f.dateTo) params.push('invoiceDateTo=' + encodeURIComponent(f.dateTo));
  if (f.linkage) params.push('linkageStatus=' + encodeURIComponent(f.linkage));
  if (f.keyword) params.push('keyword=' + encodeURIComponent(f.keyword));

  try {
    const res = await api('/api/purchase-invoices?' + params.join('&'));
    PIR.list = (res && res.items) ? res.items : [];
    PIR.total = (res && res.total) || 0;
  } catch (e) {
    PIR.list = []; PIR.total = 0;
    toast('发票台账加载失败：' + e.message, 'error');
  }
}

function pirRender() {
  const modal = document.getElementById('modal');
  modal.innerHTML = `
    <div style="max-width:1280px;margin:2vh auto;background:#fff;border-radius:14px;padding:16px 18px;max-height:94vh;overflow:auto;box-shadow:0 20px 60px rgba(0,0,0,.25)">
      <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:8px">
        <h3 style="margin:0">🧾 供应商采购发票登记册
          <span style="font-size:13px;color:#64748b;font-weight:400;margin-left:10px">
            普票 / 专票运营证据；含税总额可关联到同供应商同币种采购订单（不是应付账款台账 / 税务申报 / 付款授权）</span></h3>
        <button class="btn btn-neutral btn-sm" onclick="closeModal()">✕ 关闭</button>
      </div>
      ${PIR.hint ? `<div style="padding:6px 10px;background:#eff6ff;border:1px solid #bfdbfe;border-radius:8px;color:#1e40af;font-size:13px;margin-bottom:8px">${escapeHtml(PIR.hint)}</div>` : ''}
      ${PIR.view === 'list' ? pirListView() : ''}
      ${PIR.view === 'form' ? pirFormView() : ''}
      ${PIR.view === 'allocate' ? pirAllocationView() : ''}
      ${PIR.view === 'detail' ? pirDetailView() : ''}
      ${PIR.view === 'void' ? pirVoidView() : ''}
    </div>`;
  modal.style.display = 'block';
}

/* ==================== 列表视图 ==================== */

function pirMoney(v) { return Number(v || 0).toFixed(2); }

function pirStatusBadge(row) {
  if (row.isDraft) return '<span class="status status-info">草稿</span>';
  if (row.isRecorded) return '<span class="status status-success">已登记</span>';
  if (row.isVoided) return '<span class="status status-neutral">已作废</span>'
    + `<div class="text-muted">${escapeHtml(row.voidReason || '')}</div>`;
  return `<span class="text-muted">${escapeHtml(row.statusText || '')}</span>`;
}

function pirLinkageBadge(row) {
  const text = escapeHtml(row.linkageText || '');
  if (row.linkageStatus === 'linked') return `<span class="status status-success">已全额关联</span><div class="text-muted">${text}</div>`;
  if (row.linkageStatus === 'partial') return `<span class="status status-warning">部分关联</span><div class="text-muted">${text}</div>`;
  return `<span class="status status-neutral">未关联</span><div class="text-muted">${text}</div>`;
}

function pirListView() {
  const f = PIR.filters;
  const supplierOptions = PIR.suppliers.map(s =>
    `<option value="${s.id}" ${String(s.id) === String(f.supplierId) ? 'selected' : ''}>`
    + `${escapeHtml(s.supplierCode || '')} ${escapeHtml(s.supplierName || '')}</option>`).join('');

  const rows = PIR.list.map(row => `
    <tr>
      <td>${escapeHtml(row.identityText || '')}</td>
      <td>${fmtDate(row.invoiceDate)}</td>
      <td>${escapeHtml(row.supplierName || '')}
        <div class="text-muted">${escapeHtml(row.supplierAvailabilityText || '')}</div></td>
      <td>${escapeHtml(row.currency || '')}</td>
      <td style="text-align:right">${pirMoney(row.netAmount)}</td>
      <td style="text-align:right">${pirMoney(row.taxAmount)}</td>
      <td style="text-align:right"><b>${pirMoney(row.grossAmount)}</b></td>
      <td style="text-align:right">${pirMoney(row.linkedAmount)}</td>
      <td style="text-align:right">${pirMoney(row.unlinkedAmount)}</td>
      <td>${pirStatusBadge(row)}</td>
      <td>${pirLinkageBadge(row)}</td>
      <td style="white-space:nowrap">
        <button class="btn btn-neutral btn-sm" onclick="pirOpenDetail(${row.id})">详情</button>
        ${row.isDraft ? `<button class="btn btn-neutral btn-sm" onclick="pirOpenForm(${row.id})">编辑</button>` : ''}
        ${row.isDraft ? `<button class="btn btn-neutral btn-sm" onclick="pirOpenAllocations(${row.id})">关联</button>` : ''}
        ${row.isDraft ? `<button class="btn btn-primary btn-sm" onclick="pirRecord(${row.id})">登记</button>` : ''}
        ${row.isVoided ? '' : `<button class="btn btn-neutral btn-sm" onclick="pirOpenVoid(${row.id})">作废</button>`}
      </td>
    </tr>`).join('');

  return `
    ${pirFilterBar(supplierOptions)}
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <div class="text-muted">共 ${PIR.total} 条（本页 ${PIR.list.length} 条）；未关联金额不会被系统猜测到任何采购订单。</div>
      <div style="display:flex;gap:8px">
        <button class="btn btn-neutral" onclick="pirSearch()">🔍 查询</button>
        <button class="btn btn-primary" onclick="pirOpenForm(null)">＋ 新建发票草稿</button>
      </div>
    </div>
    ${pirListTable(rows)}
    ${pirPager()}`;
}

function pirFilterBar(supplierOptions) {
  const f = PIR.filters;
  return `
    <div style="display:grid;grid-template-columns:repeat(4,1fr);gap:8px;margin-bottom:8px">
      <div><label class="ea-lb">供应商</label>
        <select id="pir-f-supplier" style="width:100%" onchange="PIR.filters.supplierId=this.value">
          <option value="">（全部供应商）</option>${supplierOptions}
        </select></div>
      <div><label class="ea-lb">发票类型</label>
        <select id="pir-f-type" style="width:100%" onchange="PIR.filters.invoiceType=this.value">
          <option value="">（全部类型）</option>
          ${PIR_TYPES.map(t => `<option value="${t}" ${f.invoiceType === t ? 'selected' : ''}>${t}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">状态</label>
        <select id="pir-f-status" style="width:100%" onchange="PIR.filters.status=this.value">
          <option value="">（全部，含已作废历史）</option>
          ${PIR_STATUSES.map(s => `<option value="${s.value}" ${String(f.status) === String(s.value) ? 'selected' : ''}>${s.label}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">币种</label>
        <select id="pir-f-currency" style="width:100%" onchange="PIR.filters.currency=this.value">
          <option value="">（全部币种）</option>
          ${PIR_CURRENCIES.map(c => `<option value="${c}" ${f.currency === c ? 'selected' : ''}>${c}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">开票日期从</label>
        <input type="date" id="pir-f-from" style="width:100%" value="${escapeHtml(f.dateFrom)}"
          onchange="PIR.filters.dateFrom=this.value"></div>
      <div><label class="ea-lb">开票日期到</label>
        <input type="date" id="pir-f-to" style="width:100%" value="${escapeHtml(f.dateTo)}"
          onchange="PIR.filters.dateTo=this.value"></div>
      <div><label class="ea-lb">关联状态</label>
        <select id="pir-f-linkage" style="width:100%" onchange="PIR.filters.linkage=this.value">
          <option value="">（全部）</option>
          ${PIR_LINKAGES.map(l => `<option value="${l.value}" ${f.linkage === l.value ? 'selected' : ''}>${l.label}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">关键字（号码 / 代码 / 供应商）</label>
        <input id="pir-f-keyword" style="width:100%" value="${escapeHtml(f.keyword)}"
          onchange="PIR.filters.keyword=this.value" placeholder="如 04412 或供应商名称"></div>
    </div>`;
}

function pirListTable(rows) {
  return `
    <div class="table-wrap" style="max-height:56vh;overflow:auto">
      <table>
        <thead><tr>
          <th>发票（类型 号码 / 代码）</th><th>开票日期</th><th>供应商</th><th>币种</th>
          <th style="text-align:right">不含税</th><th style="text-align:right">税额</th>
          <th style="text-align:right">含税总额</th><th style="text-align:right">已关联</th>
          <th style="text-align:right">未关联</th><th>状态</th><th>关联</th><th>操作</th>
        </tr></thead>
        <tbody>${rows || '<tr><td colspan="12" style="text-align:center;color:#64748b;padding:18px">暂无发票记录</td></tr>'}</tbody>
      </table>
    </div>`;
}

function pirPager() {
  return `
    <div style="display:flex;justify-content:flex-end;gap:8px;margin-top:8px">
      <button class="btn btn-neutral btn-sm" onclick="pirPage(-1)" ${PIR.page <= 1 ? 'disabled' : ''}>上一页</button>
      <span class="text-muted" style="line-height:30px">第 ${PIR.page} 页</span>
      <button class="btn btn-neutral btn-sm" onclick="pirPage(1)"
        ${PIR.list.length < PIR.pageSize ? 'disabled' : ''}>下一页</button>
    </div>`;
}

async function pirSearch() {
  PIR.page = 1;
  await pirLoadList();
  pirRender();
}

async function pirPage(delta) {
  const next = PIR.page + delta;
  if (next < 1) return;
  PIR.page = next;
  await pirLoadList();
  pirRender();
}

/* ==================== 新建 / 编辑（草稿） ==================== */

function pirOpenForm(id) {
  const prefill = PIR._prefill || null;

  if (id) {
    const row = PIR.list.find(x => x.id === id);
    if (!row) { toast('请先刷新台账再编辑', 'error'); return; }
    PIR.current = row;
    PIR.form = {
      id: row.id, invoiceType: row.invoiceType, invoiceCode: row.invoiceCode || '',
      invoiceNumber: row.invoiceNumber || '', invoiceDate: fmtDate(row.invoiceDate),
      supplierId: row.supplierId, currency: row.currency,
      netAmount: row.netAmount, taxAmount: row.taxAmount, grossAmount: row.grossAmount,
      remark: row.remark || ''
    };
  } else {
    PIR.current = null;
    PIR.form = {
      id: null, invoiceType: '普票', invoiceCode: '', invoiceNumber: '',
      invoiceDate: todayISO(),
      supplierId: prefill ? prefill.supplierId : '',
      currency: prefill ? prefill.currency : 'CNY',
      netAmount: 0, taxAmount: 0, grossAmount: 0,
      remark: prefill && prefill.orderNo ? ('对应采购订单 ' + prefill.orderNo) : ''
    };
  }

  PIR.view = 'form';
  pirRender();
}

function pirFormView() {
  const f = PIR.form;
  if (!f) return '<div class="text-muted">无可编辑的发票草稿。</div>';

  const supplierOptions = PIR.suppliers.map(s =>
    `<option value="${s.id}" ${String(s.id) === String(f.supplierId) ? 'selected' : ''}>`
    + `${escapeHtml(s.supplierCode || '')} ${escapeHtml(s.supplierName || '')}</option>`).join('');

  return `
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <h4 style="margin:0">${f.id ? '编辑发票草稿' : '新建发票草稿'}
        <span style="font-size:12px;color:#64748b;font-weight:400;margin-left:8px">草稿可修改；登记后冻结证据（可作废，不可改写）</span></h4>
      <button class="btn btn-neutral btn-sm" onclick="pirBackToList()">← 返回台账</button>
    </div>

    <div style="display:grid;grid-template-columns:repeat(3,1fr);gap:8px">
      <div><label class="ea-lb">发票类型 *</label>
        <select id="pir-type" style="width:100%" onchange="PIR.form.invoiceType=this.value;pirRender()">
          ${PIR_TYPES.map(t => `<option value="${t}" ${f.invoiceType === t ? 'selected' : ''}>${t}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">发票代码 ${f.invoiceType === '专票' ? '*（专票必填）' : '（普票可选）'}</label>
        <input id="pir-code" style="width:100%" value="${escapeHtml(f.invoiceCode)}"
          onchange="PIR.form.invoiceCode=this.value"></div>
      <div><label class="ea-lb">发票号码 *</label>
        <input id="pir-number" style="width:100%" value="${escapeHtml(f.invoiceNumber)}"
          onchange="PIR.form.invoiceNumber=this.value"></div>
      <div><label class="ea-lb">开票日期 *</label>
        <input type="date" id="pir-date" style="width:100%" value="${escapeHtml(f.invoiceDate)}"
          onchange="PIR.form.invoiceDate=this.value"></div>
      <div><label class="ea-lb">供应商 *（必须存在且启用）</label>
        <select id="pir-supplier" style="width:100%" onchange="PIR.form.supplierId=this.value">
          <option value="">请选择供应商…</option>${supplierOptions}
        </select></div>
      <div><label class="ea-lb">币种 *（关联订单时要求一致）</label>
        <select id="pir-currency" style="width:100%" onchange="PIR.form.currency=this.value">
          ${PIR_CURRENCIES.map(c => `<option value="${c}" ${f.currency === c ? 'selected' : ''}>${c}</option>`).join('')}
        </select></div>
      <div><label class="ea-lb">不含税金额（净额）*</label>
        <input type="number" step="0.01" id="pir-net" style="width:100%" value="${f.netAmount}"
          onchange="PIR.form.netAmount=this.value"></div>
      <div><label class="ea-lb">税额 *（允许 0）</label>
        <input type="number" step="0.01" id="pir-tax" style="width:100%" value="${f.taxAmount}"
          onchange="PIR.form.taxAmount=this.value"></div>
      <div><label class="ea-lb">含税总额（价税合计）*</label>
        <input type="number" step="0.01" id="pir-gross" style="width:100%" value="${f.grossAmount}"
          onchange="PIR.form.grossAmount=this.value"></div>
    </div>

    <div style="margin-top:8px"><label class="ea-lb">备注</label>
      <textarea id="pir-remark" style="width:100%;height:52px" onchange="PIR.form.remark=this.value">${escapeHtml(f.remark)}</textarea></div>

    <div style="margin-top:8px;padding:8px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;color:#475569;font-size:13px">
      金额等式（服务端权威校验）：<b>含税总额 = 不含税金额 + 税额</b>；三项按币种精度四舍五入（JPY 等无小数币种为 0 位，其余 2 位，0.5 进位）后必须严格相等，
      含税总额必须大于 0；系统不按税率反推金额、不做汇率换算；同一「供应商 + 类型 + 代码 / 号码」的未作废发票不允许重复登记。
    </div>

    <div style="display:flex;justify-content:flex-end;gap:8px;margin-top:12px">
      <button class="btn btn-neutral" onclick="pirBackToList()">取消</button>
      <button class="btn btn-primary" onclick="pirSaveForm()">💾 保存草稿</button>
    </div>`;
}

async function pirSaveForm() {
  const f = PIR.form;
  if (!f) return;
  if (!f.supplierId) { toast('请选择供应商', 'error'); return; }
  if (!f.invoiceNumber) { toast('请填写发票号码', 'error'); return; }
  if (f.invoiceType === '专票' && !String(f.invoiceCode || '').trim()) {
    toast('专票必须填写发票代码', 'error'); return;
  }
  if (!f.invoiceDate) { toast('请填写开票日期', 'error'); return; }

  const body = {
    invoiceType: f.invoiceType,
    invoiceCode: f.invoiceCode || '',
    invoiceNumber: f.invoiceNumber,
    invoiceDate: f.invoiceDate + 'T00:00:00',
    supplierId: Number(f.supplierId),
    currency: f.currency,
    netAmount: Number(f.netAmount || 0),
    taxAmount: Number(f.taxAmount || 0),
    grossAmount: Number(f.grossAmount || 0),
    remark: f.remark || ''
  };

  try {
    const saved = f.id
      ? await api('/api/purchase-invoices/' + f.id, 'PUT', body)
      : await api('/api/purchase-invoices', 'POST', body);
    toast(f.id ? '发票草稿已更新' : '发票草稿已保存（未登记前可继续修改与关联）');
    PIR._prefill = null;
    PIR.current = saved;
    PIR.view = 'detail';
    await pirLoadList();
    pirRender();
  } catch (e) {
    toast('保存失败：' + e.message, 'error');
  }
}

async function pirBackToList() {
  PIR.view = 'list';
  PIR.current = null;
  PIR.form = null;
  PIR.preview = null;
  await pirLoadList();
  pirRender();
}

/* ==================== 关联（分摊）到采购订单 ==================== */

async function pirOpenAllocations(id) {
  try {
    PIR.current = await api('/api/purchase-invoices/' + id);
  } catch (e) {
    toast('发票加载失败：' + e.message, 'error'); return;
  }

  // 关联金额工作副本：以当前已持久化关联行初始化
  PIR.allocate = {};
  (PIR.current.allocations || []).forEach(a => { PIR.allocate[a.purchaseOrderId] = a.allocatedAmount; });
  PIR.preview = null;
  PIR.view = 'allocate';
  await pirLoadCandidates('');
  pirRender();
}

function pirAllocateArray() {
  return Object.keys(PIR.allocate)
    .map(k => ({ purchaseOrderId: Number(k), allocatedAmount: Number(PIR.allocate[k] || 0) }))
    .filter(x => x.allocatedAmount > 0);
}

function pirAllocatedTotal() {
  return pirAllocateArray().reduce((sum, x) => sum + x.allocatedAmount, 0);
}

async function pirLoadCandidates(keyword) {
  const inv = PIR.current;
  if (!inv) return;
  PIR.candidateKeyword = keyword || '';
  try {
    const params = 'take=200' + (keyword ? '&keyword=' + encodeURIComponent(keyword) : '');
    PIR.candidates = await api('/api/purchase-invoices/' + inv.id + '/order-candidates?' + params);
  } catch (e) {
    PIR.candidates = [];
    toast('可关联采购订单加载失败：' + e.message, 'error');
  }
}

function pirSetAmount(orderId, value) {
  const amount = Number(value || 0);
  if (amount > 0) PIR.allocate[orderId] = amount;
  else delete PIR.allocate[orderId];
  PIR.preview = null;
  pirRender();
}

async function pirCandidateSearch() {
  const el = document.getElementById('pir-cand-keyword');
  await pirLoadCandidates(el ? el.value : '');
  pirRender();
}

async function pirPreview() {
  const inv = PIR.current;
  if (!inv) return;
  try {
    PIR.preview = await api('/api/purchase-invoices/' + inv.id + '/allocations/preview', 'POST',
      { lines: pirAllocateArray() });
    pirRender();
  } catch (e) {
    toast('预览失败：' + e.message, 'error');
  }
}

async function pirSaveAllocations() {
  const inv = PIR.current;
  if (!inv) return;
  const lines = pirAllocateArray();
  if (!confirm(`保存本发票的采购订单关联？将整体替换现有 ${inv.allocationCount} 条关联，`
    + `本次提交 ${lines.length} 条（关联金额合计 ${pirMoney(pirAllocatedTotal())} ${inv.currency}）；`
    + '登记后关联即冻结，作废会保留关联证据。')) return;

  try {
    PIR.current = await api('/api/purchase-invoices/' + inv.id + '/allocations', 'POST', { lines });
    PIR.allocate = {};
    (PIR.current.allocations || []).forEach(a => { PIR.allocate[a.purchaseOrderId] = a.allocatedAmount; });
    PIR.preview = null;
    toast('发票关联已保存');
    await pirLoadList();
    pirRender();
  } catch (e) {
    toast('保存关联失败：' + e.message, 'error');
  }
}

async function pirClearAllocations() {
  const inv = PIR.current;
  if (!inv) return;
  if (!confirm('清空本发票的全部采购订单关联？发票将保持「未关联」状态（不会猜测任何订单，也不改动采购订单）。')) return;
  try {
    PIR.current = await api('/api/purchase-invoices/' + inv.id + '/allocations', 'POST', { lines: [] });
    PIR.allocate = {};
    PIR.preview = null;
    toast('发票关联已清空（发票保持未关联）');
    await pirLoadList();
    pirRender();
  } catch (e) {
    toast('清空关联失败：' + e.message, 'error');
  }
}

function pirInvoiceSummaryHtml(inv) {
  return `
    <div style="display:grid;grid-template-columns:repeat(6,1fr);gap:8px;padding:8px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;font-size:13px;margin-bottom:8px">
      <div>发票<div><b>${escapeHtml(inv.identityText || '')}</b></div></div>
      <div>开票日期<div>${fmtDate(inv.invoiceDate)}</div></div>
      <div>供应商<div>${escapeHtml(inv.supplierName || '')}</div></div>
      <div>币种<div>${escapeHtml(inv.currency || '')}</div></div>
      <div>含税总额<div><b>${pirMoney(inv.grossAmount)}</b></div></div>
      <div>已关联 / 未关联<div>${pirMoney(inv.linkedAmount)} / <b>${pirMoney(inv.unlinkedAmount)}</b></div></div>
    </div>`;
}

function pirAllocationView() {
  const inv = PIR.current;
  if (!inv) return '<div class="text-muted">请选择发票。</div>';

  const rows = PIR.candidates.map(c => `
    <tr>
      <td>${escapeHtml(c.orderNo || '')}</td>
      <td>${fmtDate(c.orderDate)}</td>
      <td>${escapeHtml(c.statusText || '')}</td>
      <td style="text-align:right">${pirMoney(c.orderedAmount)}</td>
      <td style="text-align:right">${pirMoney(c.linkedByThisInvoice)}</td>
      <td style="text-align:right">${pirMoney(c.linkedByOtherInvoices)}</td>
      <td style="text-align:right">${pirMoney(c.remainingUnallocatedAmount)}</td>
      <td style="color:${c.eligible ? '#166534' : '#b91c1c'}">${escapeHtml(c.eligibilityText || '')}</td>
      <td style="width:150px">
        <input type="number" step="0.01" min="0" style="width:100%" ${c.eligible ? '' : 'disabled'}
          value="${PIR.allocate[c.purchaseOrderId] !== undefined ? PIR.allocate[c.purchaseOrderId] : ''}"
          onchange="pirSetAmount(${c.purchaseOrderId}, this.value)">
      </td>
    </tr>`).join('');

  const remaining = Number(inv.grossAmount || 0) - pirAllocatedTotal();

  return `
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <h4 style="margin:0">关联采购订单（发票草稿）</h4>
      <button class="btn btn-neutral btn-sm" onclick="pirBackToList()">← 返回台账</button>
    </div>
    ${pirInvoiceSummaryHtml(inv)}

    <div style="padding:8px 10px;background:#fffbeb;border:1px solid #fde68a;border-radius:8px;color:#92400e;font-size:13px;margin-bottom:8px">
      只能关联<b>同供应商 + 同币种</b>且未取消的采购订单；关联金额合计不得超过含税总额；同一订单只能关联一次。
      发票允许部分或全部未关联 —— 系统不会按单号相似度、金额相近或开票日期接近猜测订单。
      本发票口径：本次提交合计 <b>${pirMoney(pirAllocatedTotal())}</b> ${escapeHtml(inv.currency)}，
      保存后未关联 <b>${pirMoney(remaining)}</b> ${escapeHtml(inv.currency)}。
    </div>

    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:6px">
      <div style="display:flex;gap:8px;align-items:center">
        <label class="ea-lb" style="margin:0">候选订单关键字</label>
        <input id="pir-cand-keyword" style="width:220px" value="${escapeHtml(PIR.candidateKeyword || '')}"
          placeholder="采购单号 / 合同号">
        <button class="btn btn-neutral btn-sm" onclick="pirCandidateSearch()">查询候选</button>
      </div>
      <div style="display:flex;gap:8px">
        <button class="btn btn-neutral" onclick="pirClearAllocations()">清空全部关联</button>
        <button class="btn btn-neutral" onclick="pirPreview()">🔍 预览（只读）</button>
        <button class="btn btn-primary" onclick="pirSaveAllocations()">💾 保存关联</button>
      </div>
    </div>

    <div class="table-wrap" style="max-height:44vh;overflow:auto">
      <table>
        <thead><tr>
          <th>采购单号</th><th>订单日期</th><th>状态</th>
          <th style="text-align:right">订单总额</th><th style="text-align:right">本发票已关联</th>
          <th style="text-align:right">其他有效发票已关联</th><th style="text-align:right">剩余未被发票证据覆盖</th>
          <th>可否关联</th><th>本次关联金额</th>
        </tr></thead>
        <tbody>${rows || '<tr><td colspan="9" style="text-align:center;color:#64748b;padding:18px">没有同供应商 + 同币种的采购订单</td></tr>'}</tbody>
      </table>
    </div>
    <div class="text-muted" style="margin-top:4px;font-size:12px">
      「剩余未被发票证据覆盖」只按已登记（非作废）发票的关联行派生，下限 0；它不是应付余额、账龄或付款依据。
    </div>

    ${PIR.preview ? pirPreviewHtml() : ''}`;
}

function pirPreviewHtml() {
  const p = PIR.preview;
  const rows = (p.lines || []).map(l => `
    <tr>
      <td>${escapeHtml(l.orderNo || '')}</td>
      <td>${fmtDate(l.orderDate)}</td>
      <td>${escapeHtml(l.orderStatusText || '')}</td>
      <td style="text-align:right">${pirMoney(l.orderTotalAmount)}</td>
      <td style="text-align:right">${pirMoney(l.linkedByOtherInvoices)}</td>
      <td style="text-align:right"><b>${pirMoney(l.allocatedAmount)}</b></td>
      <td>${escapeHtml(l.eligibilityText || '')}</td>
    </tr>`).join('');

  return `
    <div style="margin-top:10px;padding:8px 10px;background:#eff6ff;border:1px solid #bfdbfe;border-radius:8px">
      <b>预览结果（只读，未写库）</b>
      <div class="text-muted" style="margin:4px 0">${escapeHtml(p.linkageText || '')}</div>
      <div style="font-size:13px">拟关联合计 <b>${pirMoney(p.proposedTotal)}</b> ${escapeHtml(p.currency)}
        ；保存后未关联 <b>${pirMoney(p.unlinkedAfterSave)}</b> ${escapeHtml(p.currency)}
        ；当前已持久化关联 ${pirMoney(p.persistedLinkedAmount)}（${p.lineCount} 行）。</div>
      <div class="text-muted" style="margin-top:4px;font-size:12px">${escapeHtml(p.ruleText || '')}</div>
      <div class="table-wrap" style="max-height:30vh;overflow:auto;margin-top:6px">
        <table>
          <thead><tr>
            <th>采购单号</th><th>订单日期</th><th>状态</th>
            <th style="text-align:right">订单总额</th><th style="text-align:right">其他有效发票已关联</th>
            <th style="text-align:right">本次关联</th><th>可否关联</th>
          </tr></thead>
          <tbody>${rows || '<tr><td colspan="7" style="text-align:center;color:#64748b;padding:12px">本次未提交任何关联行（保存后发票为未关联状态）</td></tr>'}</tbody>
        </table>
      </div>
    </div>`;
}

/* ==================== 登记 / 作废 / 详情 ==================== */

async function pirRecord(id) {
  if (!confirm('登记该发票？登记即冻结证据：之后不可编辑、不可再改关联（只能作废，作废保留历史与关联）。'
    + '登记不会改动采购订单、库存、退税、供应商余额与付款状态，也不生成任何财务单据。')) return;

  try {
    const saved = await api('/api/purchase-invoices/' + id + '/record', 'POST');
    toast('发票已登记（证据已冻结）');
    PIR.current = saved;
    PIR.view = 'detail';
    await pirLoadList();
    pirRender();
  } catch (e) {
    toast('登记失败：' + e.message, 'error');
  }
}

function pirOpenVoid(id) {
  const row = PIR.list.find(x => x.id === id);
  PIR.current = row || PIR.current;
  PIR.voidReason = '';
  PIR.view = 'void';
  pirRender();
}

function pirVoidView() {
  const inv = PIR.current;
  if (!inv) return '<div class="text-muted">请选择发票。</div>';

  return `
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <h4 style="margin:0">作废发票</h4>
      <button class="btn btn-neutral btn-sm" onclick="pirBackToList()">← 返回台账</button>
    </div>
    ${pirInvoiceSummaryHtml(inv)}
    <div style="padding:8px 10px;background:#fef2f2;border:1px solid #fecaca;border-radius:8px;color:#991b1b;font-size:13px">
      作废<b>保留</b>发票身份、金额、采购订单关联与审计历史（不物理删除、不改写已登记证据），
      也不产生任何收付款 / 记账动作；作废后同一身份可重新登记新发票。请填写作废原因（必填）。
    </div>
    <div style="margin-top:8px"><label class="ea-lb">作废原因 *</label>
      <textarea id="pir-void-reason" style="width:100%;height:64px" onchange="PIR.voidReason=this.value"
        placeholder="如：发票号码录入错误 / 供应商作废重开 / 重复登记">${escapeHtml(PIR.voidReason || '')}</textarea></div>
    <div style="display:flex;justify-content:flex-end;gap:8px;margin-top:12px">
      <button class="btn btn-neutral" onclick="pirBackToList()">取消</button>
      <button class="btn btn-danger" onclick="pirConfirmVoid()">🗑 确认作废（保留历史）</button>
    </div>`;
}

async function pirConfirmVoid() {
  const inv = PIR.current;
  if (!inv) return;
  if (!String(PIR.voidReason || '').trim()) { toast('请填写作废原因', 'error'); return; }

  try {
    const saved = await api('/api/purchase-invoices/' + inv.id + '/void', 'POST', { reason: PIR.voidReason });
    toast('发票已作废（身份 / 金额 / 关联 / 审计历史保留可读）');
    PIR.current = saved;
    PIR.view = 'detail';
    await pirLoadList();
    pirRender();
  } catch (e) {
    toast('作废失败：' + e.message, 'error');
  }
}

async function pirOpenDetail(id) {
  try {
    PIR.current = await api('/api/purchase-invoices/' + id);
    PIR.view = 'detail';
    pirRender();
  } catch (e) {
    toast('详情加载失败：' + e.message, 'error');
  }
}

function pirDetailView() {
  const inv = PIR.current;
  if (!inv) return '<div class="text-muted">请选择发票。</div>';

  const rows = (inv.allocations || []).map(a => `
    <tr>
      <td>${escapeHtml(a.orderNo || '')}</td>
      <td>${fmtDate(a.orderDate)}</td>
      <td>${escapeHtml(a.orderStatusText || '')}</td>
      <td>${escapeHtml(a.currency || '')}</td>
      <td style="text-align:right">${pirMoney(a.allocatedAmount)}</td>
      <td>${escapeHtml(a.orderAvailabilityText || '')}</td>
    </tr>`).join('');

  return `
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <h4 style="margin:0">发票详情 ${pirStatusBadge(inv)}</h4>
      <div style="display:flex;gap:8px">
        ${inv.isDraft ? `<button class="btn btn-neutral btn-sm" onclick="pirOpenForm(${inv.id})">编辑</button>` : ''}
        ${inv.isDraft ? `<button class="btn btn-neutral btn-sm" onclick="pirOpenAllocations(${inv.id})">关联采购订单</button>` : ''}
        ${inv.isDraft ? `<button class="btn btn-primary btn-sm" onclick="pirRecord(${inv.id})">登记</button>` : ''}
        ${inv.isVoided ? '' : `<button class="btn btn-neutral btn-sm" onclick="pirOpenVoid(${inv.id})">作废</button>`}
        <button class="btn btn-neutral btn-sm" onclick="pirBackToList()">← 返回台账</button>
      </div>
    </div>
    ${pirInvoiceSummaryHtml(inv)}

    <div style="display:grid;grid-template-columns:repeat(4,1fr);gap:8px;font-size:13px;margin-bottom:8px">
      <div>不含税金额<div><b>${pirMoney(inv.netAmount)}</b> ${escapeHtml(inv.currency)}</div></div>
      <div>税额<div><b>${pirMoney(inv.taxAmount)}</b> ${escapeHtml(inv.currency)}</div></div>
      <div>金额精度<div>${inv.amountDecimals} 位小数（按币种口径）</div></div>
      <div>供应商可用性<div>${escapeHtml(inv.supplierAvailabilityText || '')}</div></div>
      <div>登记时间<div>${inv.recordedAt ? fmtDate(inv.recordedAt) : '（未登记）'}</div></div>
      <div>作废时间<div>${inv.voidedAt ? fmtDate(inv.voidedAt) : '（未作废）'}</div></div>
      <div>作废原因<div>${escapeHtml(inv.voidReason || '—')}</div></div>
      <div>备注<div>${escapeHtml(inv.remark || '—')}</div></div>
    </div>

    <div style="padding:8px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;font-size:13px;margin-bottom:8px">
      <div><b>关联口径</b>：${escapeHtml(inv.linkageRuleText || '')}</div>
      <div style="margin-top:4px"><b>边界</b>：${escapeHtml(inv.boundaryText || '')}</div>
    </div>

    <div class="table-wrap" style="max-height:40vh;overflow:auto">
      <table>
        <thead><tr>
          <th>采购单号</th><th>订单日期</th><th>订单状态</th><th>币种</th>
          <th style="text-align:right">关联金额</th><th>订单可用性</th>
        </tr></thead>
        <tbody>${rows || '<tr><td colspan="6" style="text-align:center;color:#64748b;padding:18px">该发票未关联任何采购订单（未关联金额不会被猜测到任何订单）</td></tr>'}</tbody>
      </table>
    </div>`;
}
