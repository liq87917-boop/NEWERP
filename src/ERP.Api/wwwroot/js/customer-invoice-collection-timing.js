/* ==================================================================================
   ============ 客户销项发票收款时效证据（ERP-111）— 只读派生视图 ============
   ==================================================================================
   口径（与服务端 CustomerInvoiceCollectionTimingRules 一一对应）：
   1. 只筛选「未删除且已登记」的客户销项发票证据，并按客户 / 开票日期 / 关键字过滤（应用业务员数据范围）；
   2. 用显式 ERP-073「收款单 → 发票」分摊行，派生每张发票「开票日期 → 首张 / 末张有效收款日期」的间隔天数；
   3. 有效收款 = 未作废分摊行 + 收款单存在、未删除且未取消 + 币种与发票一致 + 收款日期不早于开票日期；
   4. 可比较已分摊 / 剩余只来自有效收款；缺链接 / 已作废 / 收款单取消 / 早于开票日期 / 币种不一致 仅作异常列出、不计入；
   5. 只读：不提供任何分摊 / 收款 / 作废动作；分摊行绝不表示银行到账、法定账龄或催收 SLA。
   ================================================================================== */

function csictNewState() {
  return {
    list: [], total: 0, page: 1, pageSize: 50,
    filters: { customerId: '', dateFrom: '', dateTo: '', keyword: '' },
    customers: [],
    loading: false
  };
}

let CSICT = csictNewState();

/* 从客户销项发票页打开收款时效视图（只读，不落库）；customerId 可选：预筛该客户 */
async function openCustomerInvoiceCollectionTiming(customerId) {
  CSICT = csictNewState();
  if (customerId) CSICT.filters.customerId = String(customerId);

  const modal = document.getElementById('modal');
  modal.innerHTML = '<div style="padding:40px;text-align:center;color:#64748b">正在加载…</div>';
  modal.style.display = 'block';

  try {
    const res = await api('/api/base/customers?page=1&pageSize=500');
    CSICT.customers = (res && res.items) ? res.items : (Array.isArray(res) ? res : []);
  } catch (e) {
    toast('客户列表加载失败：' + e.message, 'error');
  }

  await csictLoad(1);
  csictRender();
}

function csictVal(id) {
  const el = document.getElementById(id);
  return el ? String(el.value || '').trim() : '';
}

function csictQuery(page) {
  const q = new URLSearchParams();
  if (csictVal('csict-customer')) q.set('customerId', csictVal('csict-customer'));
  if (csictVal('csict-date-from')) q.set('invoiceDateFrom', csictVal('csict-date-from'));
  if (csictVal('csict-date-to')) q.set('invoiceDateTo', csictVal('csict-date-to'));
  if (csictVal('csict-keyword')) q.set('keyword', csictVal('csict-keyword'));
  q.set('page', page || 1);
  q.set('pageSize', csictVal('csict-pagesize') || '50');
  return q.toString();
}

async function csictLoad(page) {
  try {
    const data = await api('/api/customer-sales-invoices/collection-timing?' + csictQuery(page));
    CSICT.list = (data && data.items) ? data.items : [];
    CSICT.total = (data && data.total) || 0;
    CSICT.page = (data && data.page) || page || 1;
    CSICT.pageSize = (data && data.pageSize) || 50;
    CSICT.totalPages = (data && data.totalPages) || 1;
    CSICT.report = data;
  } catch (e) {
    CSICT.list = []; CSICT.total = 0;
    toast('收款时效证据加载失败：' + e.message, 'error');
  }
}

function csictRender() {
  const modal = document.getElementById('modal');
  const customerOptions = CSICT.customers.map(c =>
    `<option value="${c.id}" ${String(c.id) === String(CSICT.filters.customerId) ? 'selected' : ''}>`
    + `${escapeHtml(c.customerCode || '')} ${escapeHtml(c.customerName || '')}</option>`).join('');

  modal.innerHTML = `
    <div class="modal-content" style="max-width:1320px">
      <div class="modal-header">
        <h3>⏱️ 客户销项发票收款时效证据（ERP-111）
          <span style="font-size:13px;color:#64748b;font-weight:400;margin-left:10px">
            只读派生 · 首末有效收款日期与间隔天数（不是银行到账 / 法定账龄 / 催收 SLA）</span></h3>
        <button class="modal-close" onclick="closeModal()">✕</button>
      </div>
      <div class="modal-body" style="padding:12px 16px">
        <div style="padding:6px 10px;background:#fefce8;border:1px solid #fde68a;border-radius:8px;color:#713f12;font-size:12px;margin-bottom:8px">
          ⚠️ 只读视图：只按显式 ERP-073 收款分摊行派生首末收款日期与间隔天数，<b>不</b>提供任何分摊 / 收款 / 作废动作，
          <b>不</b>改写发票 / 收款单 / 分摊行 / 客户，也<b>不</b>表示已付 / 已结清 / 逾期 / 收入确认 / 记账状态。
        </div>
        <div style="display:flex;gap:6px;flex-wrap:wrap;align-items:end;margin-bottom:8px">
          <div><label class="ea-lb">客户</label>
            <select id="csict-customer" style="min-width:180px">
              <option value="">全部客户</option>${customerOptions}
            </select></div>
          <div><label class="ea-lb">开票日期从</label>
            <input type="date" id="csict-date-from" style="width:140px" value="${escapeHtml(CSICT.filters.dateFrom)}"></div>
          <div><label class="ea-lb">到</label>
            <input type="date" id="csict-date-to" style="width:140px" value="${escapeHtml(CSICT.filters.dateTo)}"></div>
          <div><label class="ea-lb">关键字</label>
            <input id="csict-keyword" style="min-width:160px" value="${escapeHtml(CSICT.filters.keyword)}"
              placeholder="发票号 / 发票代码 / 客户编码 / 客户名称"></div>
          <div><label class="ea-lb">每页</label>
            <input type="number" id="csict-pagesize" value="${CSICT.pageSize}" min="1" max="200" style="width:70px"></div>
          <button class="btn btn-primary btn-sm" onclick="csictSearch()">🔍 查询</button>
        </div>
        <div class="kpi-grid" id="csict-kpi"></div>
        <div class="table-wrap" id="csict-table"></div>
        <div class="pd-hint" id="csict-rule" style="margin-top:8px"></div>
        <div style="display:flex;justify-content:space-between;align-items:center;margin-top:8px">
          <div class="text-muted">共 ${CSICT.total} 条 · 第 ${CSICT.page} 页 · 每页 ${CSICT.pageSize} 条（有界分页）</div>
          <div>
            <button class="btn btn-neutral btn-sm" ${CSICT.page <= 1 ? 'disabled' : ''} onclick="csictPage(-1)">← 上一页</button>
            <button class="btn btn-neutral btn-sm" ${CSICT.page >= (CSICT.totalPages || 1) ? 'disabled' : ''} onclick="csictPage(1)">下一页 →</button>
          </div>
        </div>
      </div>
    </div>`;

  csictRenderKpi(CSICT.report);
  csictRenderTable(CSICT.list);
  csictRenderRule(CSICT.report);
}

async function csictSearch() {
  CSICT.filters.customerId = csictVal('csict-customer');
  CSICT.filters.dateFrom = csictVal('csict-date-from');
  CSICT.filters.dateTo = csictVal('csict-date-to');
  CSICT.filters.keyword = csictVal('csict-keyword');
  await csictLoad(1);
  csictRender();
}

async function csictPage(delta) {
  await csictLoad(CSICT.page + delta);
  csictRender();
}

function csictDays(v) {
  return (v === null || v === undefined) ? '未知' : `${v} 天`;
}

function csictDate(v) {
  return v ? fmtDate(v) : '未知';
}

function csictRenderKpi(data) {
  const el = document.getElementById('csict-kpi');
  if (!el) return;
  const c = (data && data.counts) ? data.counts : {};
  el.innerHTML = `
    <div class="kpi-card cargo">
      <div class="kpi-label">符合筛选的已登记发票</div>
      <div class="kpi-value">${data ? data.total : 0}<span class="unit">张</span></div>
      <div class="kpi-delta flat">本页 ${c.total || 0} 张 · 第 ${data ? data.page : 1}/${data ? data.totalPages : 1} 页</div>
    </div>
    <div class="kpi-card success">
      <div class="kpi-label">已派生收款时效</div>
      <div class="kpi-value">${c.available || 0}<span class="unit">张</span></div>
      <div class="kpi-delta flat">首末收款来自有效且币种一致、不早于开票日期的分摊收款</div>
    </div>
    <div class="kpi-card danger">
      <div class="kpi-label">未知 / 异常</div>
      <div class="kpi-value">${c.unavailable || 0} / ${c.anomalous || 0}<span class="unit">张</span></div>
      <div class="kpi-delta flat">未知不回落为 0；异常仅标注、不计入首末收款</div>
    </div>`;
}

function csictAnomalyHtml(item) {
  if (!item.isAnomalous) return '<span class="text-muted">—</span>';
  const parts = [];
  if (item.missingCount) parts.push(`缺收款单 ${item.missingCount}`);
  if (item.voidedCount) parts.push(`已作废 ${item.voidedCount}`);
  if (item.cancelledCount) parts.push(`收款单取消 ${item.cancelledCount}`);
  if (item.preInvoiceCount) parts.push(`早于开票 ${item.preInvoiceCount}`);
  if (item.currencyConflictCount) parts.push(`币种不一致 ${item.currencyConflictCount}`);
  return `<span class="status status-warning">异常</span>
    <div class="text-muted">${escapeHtml(parts.join(' / ') || '存在异常分摊行')}</div>`;
}

function csictRenderTable(list) {
  const el = document.getElementById('csict-table');
  if (!el) return;
  const rows = (list || []).map(i => `<tr>
      <td>${escapeHtml(i.identityText || '')}
        <div class="text-muted">${escapeHtml(i.invoiceType || '')} · ${escapeHtml(i.currency || '')}</div></td>
      <td>${escapeHtml(i.customerName || '')}
        <div class="text-muted">${escapeHtml(i.customerCode || '')}</div></td>
      <td>${fmtDate(i.invoiceDate)}</td>
      <td class="text-right">${escapeHtml(i.grossAmountText || '')}</td>
      <td>${i.firstReceiptNo ? escapeHtml(i.firstReceiptNo) : '未知'}
        <div class="text-muted">${csictDate(i.firstReceiptDate)} · ${csictDays(i.firstCollectionDays)}</div></td>
      <td>${i.lastReceiptNo ? escapeHtml(i.lastReceiptNo) : '未知'}
        <div class="text-muted">${csictDate(i.lastReceiptDate)} · ${csictDays(i.lastCollectionDays)}</div></td>
      <td class="text-right">${escapeHtml(i.comparableAllocatedText || '')}
        <div class="text-muted">剩余 ${escapeHtml(i.comparableRemainingText || '')} · ${Number(i.comparableAllocationCount || 0)} 条</div></td>
      <td>${csictAnomalyHtml(i)}</td>
      <td title="${escapeHtml(i.note || '')}">${escapeHtml(i.note || '')}</td>
    </tr>`).join('');
  el.innerHTML = `<table class="data-table"><thead><tr>
      <th>发票</th><th>客户</th><th>开票日期</th><th class="text-right">含税总额</th>
      <th>首张有效收款（日期 · 间隔）</th><th>末张有效收款（日期 · 间隔）</th>
      <th class="text-right">可比较已分摊 / 剩余</th><th>异常</th><th>说明</th>
    </tr></thead>
    <tbody>${rows || '<tr><td colspan="9" class="text-muted">没有符合筛选条件的已登记发票（可放宽客户 / 开票日期 / 关键字筛选）。</td></tr>'}</tbody></table>`;
}

function csictRenderRule(data) {
  const el = document.getElementById('csict-rule');
  if (!el) return;
  el.innerHTML = `<b>口径</b>：${escapeHtml((data && data.rule) || '')}
    <div class="text-muted">${escapeHtml((data && data.scopeNote) || '')}</div>
    <div class="text-muted">${escapeHtml((data && data.boundary) || '')}</div>`;
}


