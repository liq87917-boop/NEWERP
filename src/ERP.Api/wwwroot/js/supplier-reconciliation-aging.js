/* ============ 供应商对账与账龄工作台（ERP-068：只读派生；证据来自 ERP-065 发票 + ERP-066 付款引用行） ============

   口径与后端 SupplierReconciliationAgingSemantics 一一对应：
   - 剩余证据 = 含税总额 − 有效（未作废且经 ERP-066 资格判定）已分配金额；命中后端有界上限时金额为「未知」，
     绝不用 0 顶替；有效已分配超过含税总额时按「无效证据」显示，绝不轧为 0、也不视为已结清；
   - 账龄只对**登记了显式到期日**的发票计算，并以页面显式 as-of 日期为准：
     未到期 / 逾期 1~30 / 31~60 / 61~90 / 90 天以上（互斥且完整）；未登记到期日的发票进入独立的
     「未知到期日」分组（不计算账龄、不并入任何账龄桶，绝不按开票日期或默认账期推算）；
   - 不同币种分别成行、绝不合并、绝不换算：本页与导出都没有任何跨币种总额；
   - 草稿 / 已作废发票单独标注且不参与有效应付证据合计；已作废 / 无效 / 无法确认引用证据只作历史列可见；
   - 本页是仓库对账口径的只读证据视图：不是总账或应付余额、不是法定供应商对账单、不是付款授权、
     不是税务申报、不是结算确认，也不得据以付款或核销。
   数据全部走既有只读接口 GET /api/purchase-invoices/reconciliation-aging；明细走
   GET /api/purchase-invoices/reconciliation-aging/invoices/{id}（打开时后端重新校验身份与既有模块授权，
   未授权 / 来源已删除一律 fail closed，界面只显示拒绝原因、不显示任何证据）。 */

const SRA_API = '/api/purchase-invoices/reconciliation-aging';
let SRA_DATA = null;

/* 账龄分桶中文（与后端 SupplierReconciliationAgingSemantics.BucketText 同口径，仅作兜底显示） */
const SRA_BUCKET_LABELS = {
  not_due: '未到期（as-of ≤ 显式到期日）',
  overdue_1_30: '逾期 1 ~ 30 天',
  overdue_31_60: '逾期 31 ~ 60 天',
  overdue_61_90: '逾期 61 ~ 90 天',
  overdue_over_90: '逾期 90 天以上',
  unknown_due_date: '未知到期日（不计算账龄，单独成组）',
};

/* 分配状态中文（与后端 AllocationStateText 同口径，仅作兜底显示） */
const SRA_ALLOCATION_LABELS = {
  none: '无持久化付款引用行（证据缺口）',
  historical_only: '仅有历史 / 无效引用行',
  partial: '部分分配',
  full: '整笔分配',
  over_allocated: '无效证据：超过含税总额',
  unknown: '未知（命中读取上限）',
};

/* 工具栏 / 报表入口：渲染独立工作台页（只读，不落库）。
   可选 supplierId（供应商行的行操作会传入）：加载供应商下拉后预筛选该供应商，方便按供应商对账。 */
function openSupplierReconciliationAgingWorkspace(supplierId) {
  CURRENT_PAGE_CODE = 'supplier-reconciliation-aging';
  document.getElementById('header-title').textContent = '供应商对账与账龄工作台';
  document.getElementById('content').innerHTML = `
    <div class="page-hero report-hero">
      <h2>🧮 供应商对账与账龄工作台</h2>
      <p>按供应商 + 币种对账：发票含税总额 / ERP-066 有效已分配付款引用 / 算术剩余证据（只读派生，未知不推断）</p>
    </div>

    <div class="pd-hint" style="margin-bottom:10px">
      ⚠️ <b>仓库对账口径的只读证据视图</b>：<b>不是</b>总账或应付账款余额、<b>不是</b>法定供应商对账单、
      <b>不是</b>付款授权、<b>不是</b>税务申报、<b>不是</b>结算确认 —— 「剩余证据」是算术派生值，
      不得当作欠款金额或据以付款；账龄只按显式到期日与显式 as-of 日期计算，未知到期日单独成组。
    </div>

    <div class="toolbar">
      <div class="toolbar-left" style="flex-wrap:wrap;gap:8px;align-items:center">
        <label>供应商 <select id="sra-supplier" style="min-width:170px"><option value="">全部供应商</option></select></label>
        <label>币种 <select id="sra-currency" style="min-width:120px"><option value="">全部币种</option></select></label>
        <label>发票状态 <select id="sra-invoice-status" style="min-width:150px">
          <option value="recorded">仅已登记证据（默认）</option>
          <option value="draft">仅草稿</option>
          <option value="voided">仅已作废历史证据</option>
          <option value="all">全部状态</option>
        </select></label>
        <label>分配状态 <select id="sra-allocation-state" style="min-width:170px">
          <option value="">全部分配状态</option>
          <option value="none">无持久化付款引用行</option>
          <option value="historical_only">仅有历史 / 无效引用行</option>
          <option value="partial">部分分配</option>
          <option value="full">整笔分配</option>
        </select></label>
        <label>开票日期 <input type="date" id="sra-invoice-date-from" style="width:140px"> 至
          <input type="date" id="sra-invoice-date-to" style="width:140px"></label>
        <label>到期日 <input type="date" id="sra-due-date-from" style="width:140px"> 至
          <input type="date" id="sra-due-date-to" style="width:140px"></label>
        <label>账龄基准日 <input type="date" id="sra-as-of" style="width:140px"></label>
        <label>关键字 <input type="text" id="sra-keyword" style="width:180px" placeholder="发票号码 / 代码 / 供应商 / 付款条件"></label>
        <label>每页 <input type="number" id="sra-pagesize" value="50" min="1" max="200" style="width:70px"></label>
      </div>
      <div class="toolbar-actions">
        <button class="btn btn-primary" onclick="loadSupplierReconciliationAging(1)">查询</button>
        <button class="btn btn-neutral" onclick="exportSraCsv()" title="导出本页（筛选、币种与未知到期日语义与屏幕完全一致，无跨币种总额）">📤 导出 CSV</button>
      </div>
    </div>

    <div class="pd-hint" style="margin-bottom:10px">
      到期日筛选<b>只命中登记了显式到期日</b>的发票：未登记到期日的发票没有到期日可筛（账龄不计算、单独成组）。
    </div>

    <div class="kpi-grid" id="sra-kpi"></div>
    <div class="table-wrap" id="sra-currency-table"></div>
    <div class="table-wrap" id="sra-aging-table"></div>
    <div class="table-wrap" id="sra-unknown-table"></div>
    <div class="table-wrap" id="sra-invoice-table">
      <div class="skeleton-row"></div><div class="skeleton-row"></div>
    </div>
    <div class="pd-hint" id="sra-rule"></div>
    <div class="pagination" id="sra-pagination"></div>`;

  loadSraCurrencies();
  loadSraSuppliers().then(() => {
    if (supplierId) {
      const sel = document.getElementById('sra-supplier');
      if (sel && String(supplierId) !== '0') sel.value = String(supplierId);
    }
    loadSupplierReconciliationAging(1);
  });
}

/* 币种下拉：与单据币种同一枚举口径（枚举名），不同币种绝不合并汇总 */
function loadSraCurrencies() {
  const sel = document.getElementById('sra-currency');
  if (!sel) return;
  (typeof CURRENCY_NAME_OPTS !== 'undefined' ? CURRENCY_NAME_OPTS : []).forEach(o => {
    const opt = document.createElement('option');
    opt.value = o.value;
    opt.textContent = o.label;
    sel.appendChild(opt);
  });
}

/* 供应商下拉：既有基础资料接口；失败不阻断工作台（仍可留空或只用其它筛选） */
async function loadSraSuppliers() {
  try {
    const data = await api('/api/base/suppliers?page=1&pageSize=200');
    const sel = document.getElementById('sra-supplier');
    if (!sel) return;
    (data.items || []).forEach(s => {
      const opt = document.createElement('option');
      opt.value = s.id;
      opt.textContent = s.supplierName || ('供应商 ' + s.id);
      sel.appendChild(opt);
    });
  } catch (e) { /* 忽略：供应商下拉失败不影响报表查询 */ }
}

function sraVal(id) {
  const el = document.getElementById(id);
  return el ? String(el.value || '').trim() : '';
}

/* 金额：null / undefined = 未知（命中后端有界上限或无效证据），绝不显示为 0 */
function sraMoney(v, decimals) {
  if (v === null || v === undefined) return '未知';
  const digits = (decimals === null || decimals === undefined) ? 2 : Number(decimals);
  return Number(v).toFixed(digits);
}

/* 计数：null / undefined = 未知（命中上限），绝不显示为 0 */
function sraInt(v) {
  return (v === null || v === undefined) ? '未知' : String(v);
}

function sraQuery(page) {
  const q = new URLSearchParams();
  if (sraVal('sra-supplier')) q.set('supplierId', sraVal('sra-supplier'));
  if (sraVal('sra-currency')) q.set('currency', sraVal('sra-currency'));
  if (sraVal('sra-invoice-status')) q.set('invoiceStatus', sraVal('sra-invoice-status'));
  if (sraVal('sra-allocation-state')) q.set('allocationState', sraVal('sra-allocation-state'));
  if (sraVal('sra-invoice-date-from')) q.set('invoiceDateFrom', sraVal('sra-invoice-date-from'));
  if (sraVal('sra-invoice-date-to')) q.set('invoiceDateTo', sraVal('sra-invoice-date-to'));
  if (sraVal('sra-due-date-from')) q.set('dueDateFrom', sraVal('sra-due-date-from'));
  if (sraVal('sra-due-date-to')) q.set('dueDateTo', sraVal('sra-due-date-to'));
  if (sraVal('sra-as-of')) q.set('asOfDate', sraVal('sra-as-of'));
  if (sraVal('sra-keyword')) q.set('keyword', sraVal('sra-keyword'));
  q.set('page', page || 1);
  q.set('pageSize', sraVal('sra-pagesize') || '50');
  return q.toString();
}

/* 加载本页（只读；所有金额与分桶都由后端派生，界面只做展示） */
async function loadSupplierReconciliationAging(page) {
  const el = document.getElementById('sra-invoice-table');
  if (!el) return;
  el.innerHTML = '<div class="skeleton-row"></div><div class="skeleton-row"></div>';
  try {
    const data = await api(`${SRA_API}?${sraQuery(page)}`);
    SRA_DATA = data;
    sraRenderKpi(data);
    sraRenderCurrencyTable(data);
    sraRenderAgingTable(data);
    sraRenderUnknownDueTable(data);
    sraRenderInvoiceTable(data);
    sraRenderRules(data);
    sraRenderPagination(data);
  } catch (err) {
    toast('对账与账龄工作台加载失败：' + err.message, 'error');
    el.innerHTML = '<div class="pd-hint">加载失败（未显示任何证据；请检查筛选条件后重试）</div>';
  }
}

function sraRenderKpi(data) {
  const el = document.getElementById('sra-kpi');
  if (!el) return;
  el.innerHTML = `
    <div class="kpi-card"><div class="kpi-label">账龄基准日（as-of）</div><div class="kpi-value">${escapeHtml(data.asOfDateText || '')}</div></div>
    <div class="kpi-card"><div class="kpi-label">符合筛选的发票总数</div><div class="kpi-value">${data.total || 0}</div>
      <div class="kpi-sub">本页 ${data.pageInvoiceCount || 0} 张（第 ${data.page || 1} / ${data.totalPages || 1} 页）</div></div>
    <div class="kpi-card"><div class="kpi-label">本页有效证据发票</div><div class="kpi-value">${data.activeEvidencePageInvoiceCount || 0}</div>
      <div class="kpi-sub">草稿 ${data.draftPageInvoiceCount || 0} · 已作废 ${data.voidedPageInvoiceCount || 0}（不计入有效合计）</div></div>
    <div class="kpi-card"><div class="kpi-label">本页账龄 / 未知到期日</div><div class="kpi-value">${data.knownDueDatePageInvoiceCount || 0} / ${data.unknownDueDatePageInvoiceCount || 0}</div>
      <div class="kpi-sub">未知到期日不计算账龄，单独成组</div></div>
    <div class="kpi-card"><div class="kpi-label">本页证据缺口 / 历史无效</div><div class="kpi-value">${data.noAllocationPageInvoiceCount || 0} / ${data.invalidEvidencePageInvoiceCount || 0}</div>
      <div class="kpi-sub">缺口不代表未付款；历史 / 无效证据不并入有效合计</div></div>
    <div class="kpi-card"><div class="kpi-label">本页剩余证据未知 / 无效（超额）</div><div class="kpi-value">${data.unknownRemainingPageInvoiceCount || 0} / ${data.overAllocatedPageInvoiceCount || 0}</div>
      <div class="kpi-sub">${escapeHtml(data.truncated ? (data.truncatedNote || '命中读取上限：金额按未知显示') : '未命中读取上限')}</div></div>`;
}

/* 币种汇总：每币种一行；**没有**任何跨币种总额行 */
function sraRenderCurrencyTable(data) {
  const el = document.getElementById('sra-currency-table');
  if (!el) return;
  const rows = (data.currencies || []).map(c => `<tr>
      <td><b>${escapeHtml(c.currency || '')}</b></td>
      <td class="text-right">${c.supplierCount || 0}</td>
      <td class="text-right">${c.invoiceCount || 0}</td>
      <td class="text-right">${c.activeEvidenceInvoiceCount || 0}</td>
      <td class="text-right">${sraMoney(c.grossAmount, c.amountDecimals)}</td>
      <td class="text-right">${sraMoney(c.activeAllocatedAmount, c.amountDecimals)}</td>
      <td class="text-right">${sraMoney(c.remainingAmount, c.amountDecimals)}</td>
      <td class="text-right">${c.knownDueDateInvoiceCount || 0} / ${c.unknownDueDateInvoiceCount || 0}</td>
      <td class="text-right">${c.invalidEvidenceInvoiceCount || 0}</td>
    </tr>`).join('');
  el.innerHTML = `<table>
    <thead><tr>
      <th>币种（分别成行）</th><th class="text-right">供应商数</th><th class="text-right">发票张数</th>
      <th class="text-right">有效证据张数</th><th class="text-right">含税总额证据</th>
      <th class="text-right">有效已分配（ERP-066）</th><th class="text-right">算术剩余证据</th>
      <th class="text-right">有 / 无到期日</th><th class="text-right">无效证据张数</th>
    </tr></thead>
    <tbody>${rows || '<tr><td colspan="9" class="text-muted">本页没有发票证据</td></tr>'}</tbody></table>
    <div class="pd-hint">不同币种绝不相加、合并或换算：本表每行一个币种，系统不提供任何跨币种总额。</div>`;
}

/* 账龄分桶表：每币种 × 五个互斥桶（未到期 / 1~30 / 31~60 / 61~90 / >90）；未知到期日不在此表 */
function sraRenderAgingTable(data) {
  const el = document.getElementById('sra-aging-table');
  if (!el) return;
  const rows = [];
  (data.currencies || []).forEach(c => (c.buckets || []).forEach(b => {
    rows.push(`<tr>
      <td><b>${escapeHtml(c.currency || '')}</b></td>
      <td>${escapeHtml(SRA_BUCKET_LABELS[b.bucket] || b.bucketText || b.bucket || '')}</td>
      <td class="text-right">${b.invoiceCount || 0}</td>
      <td class="text-right">${sraMoney(b.grossAmount, c.amountDecimals)}</td>
      <td class="text-right">${sraMoney(b.activeAllocatedAmount, c.amountDecimals)}</td>
      <td class="text-right">${sraMoney(b.remainingAmount, c.amountDecimals)}</td>
      <td class="text-right">${b.unknownRemainingInvoiceCount || 0} / ${b.overAllocatedInvoiceCount || 0}</td>
    </tr>`);
  }));
  el.innerHTML = `<table>
    <thead><tr>
      <th>币种</th><th>账龄分桶（互斥，只对有显式到期日的发票）</th><th class="text-right">发票张数</th>
      <th class="text-right">含税总额证据</th><th class="text-right">有效已分配</th><th class="text-right">算术剩余证据</th>
      <th class="text-right">剩余未知 / 无效（超额）</th>
    </tr></thead>
    <tbody>${rows.join('') || '<tr><td colspan="7" class="text-muted">本页没有带显式到期日的发票证据</td></tr>'}</tbody></table>
    <div class="pd-hint">
      账龄只按显式到期日与页面 as-of 日期计算（未到期 = as-of ≤ 到期日；逾期天数 = as-of − 到期日）；
      边界取含：30 / 60 / 90 天归入本桶，次日起归入下一桶；未登记到期日的发票不计算账龄、不并入任何桶。
    </div>`;
}

/* 未知到期日独立分组（账龄不计算；仍按供应商 + 币种隔离） */
function sraRenderUnknownDueTable(data) {
  const el = document.getElementById('sra-unknown-table');
  if (!el) return;
  const rows = (data.unknownDueDateGroups || []).map(g => `<tr>
      <td>${escapeHtml(g.supplierName || '')}${g.supplierCode ? `（${escapeHtml(g.supplierCode)}）` : ''}</td>
      <td><b>${escapeHtml(g.currency || '')}</b></td>
      <td class="text-right">${g.invoiceCount || 0}</td>
      <td class="text-right">${sraMoney(g.grossAmount, g.amountDecimals)}</td>
      <td class="text-right">${sraMoney(g.activeAllocatedAmount, g.amountDecimals)}</td>
      <td class="text-right">${sraMoney(g.remainingAmount, g.amountDecimals)}</td>
      <td>${escapeHtml((g.invoiceIdentities || []).join('；'))}</td>
    </tr>`).join('');
  el.innerHTML = `<table>
    <thead><tr>
      <th>供应商</th><th>币种</th><th class="text-right">发票张数</th>
      <th class="text-right">含税总额证据</th><th class="text-right">有效已分配</th><th class="text-right">算术剩余证据</th>
      <th>发票身份（有界：本次返回页内）</th>
    </tr></thead>
    <tbody>${rows || '<tr><td colspan="7" class="text-muted">本页没有未知到期日的发票</td></tr>'}</tbody></table>
    <div class="pd-hint">
      ⚠️ 独立分组：这些发票未登记显式到期日，账龄<b>不计算</b>；系统不按开票日期、付款条件或默认账期推算到期日，
      也不把它们当作当天到期或已逾期。
    </div>`;
}

/* 发票明细行（证据三类分列：含税总额 / 有效已分配 / 算术剩余；历史证据单独成列） */
function sraInvoiceRow(r) {
  const statusHtml = r.isDraft
    ? '<span class="status status-info">草稿（不计入有效合计）</span>'
    : (r.isVoided
      ? '<span class="status status-neutral">已作废（不计入有效合计）</span>'
      : '<span class="status status-success">已登记（计入有效合计）</span>');
  const allocStatus = r.allocationState === 'none'
    ? '<span class="status status-warning">证据缺口</span>'
    : (r.allocationState === 'over_allocated'
      ? '<span class="status status-danger">无效证据（超额）</span>'
      : (r.allocationState === 'unknown'
        ? '<span class="status status-warning">未知</span>'
        : '<span class="status status-success">'
          + escapeHtml(SRA_ALLOCATION_LABELS[r.allocationState] || r.allocationStateText || '') + '</span>'));
  return `<tr>
    <td>${escapeHtml(r.invoiceIdentityText || '')}<div class="text-muted">开票 ${escapeHtml(fmtDate(r.invoiceDate))} · ${escapeHtml(r.invoiceStatusText || '')}</div></td>
    <td>${escapeHtml(r.supplierName || '')}${r.supplierAvailable ? '' : ' <span class="status status-warning">供应商不可用</span>'}<div class="text-muted">${escapeHtml(r.supplierCode || '')}</div></td>
    <td><b>${escapeHtml(r.currency || '')}</b></td>
    <td>${escapeHtml(r.dueDateKnown ? fmtDate(r.dueDate) : '未知')}<div class="text-muted">${escapeHtml(r.dueDateText || '')}</div></td>
    <td>${escapeHtml(r.agingBucketText || '')}<div class="text-muted">${escapeHtml(r.agingText || '')}</div></td>
    <td class="text-right">${sraMoney(r.grossAmount, r.amountDecimals)}</td>
    <td class="text-right">${sraMoney(r.activeAllocatedAmount, r.amountDecimals)}<div class="text-muted">${sraInt(r.activeAllocationCount)} 条 / ${sraInt(r.activePaymentCount)} 张付款单</div></td>
    <td class="text-right">${sraMoney(r.remainingAmount, r.amountDecimals)}<div class="text-muted">${escapeHtml(r.remainingStateText || '')}</div></td>
    <td>${statusHtml}<div class="text-muted">${allocStatus}</div></td>
    <td>${escapeHtml(r.historicalEvidenceText || '')}</td>
    <td><button class="btn btn-neutral btn-sm" onclick="showSupplierReconciliationAgingDetail(${r.invoiceId})">明细</button></td>
  </tr>`;
}

/* 发票级证据表（按供应商 + 币种分组；金额只按原币汇总） */
function sraRenderInvoiceTable(data) {
  const el = document.getElementById('sra-invoice-table');
  if (!el) return;
  const rows = [];
  (data.groups || []).forEach(g => {
    rows.push(`<tr class="group-row"><td colspan="11">
      <b>${escapeHtml(g.supplierName || '')}</b>（${escapeHtml(g.supplierCode || '')}） · 币种 <b>${escapeHtml(g.currency || '')}</b>
      · 发票 ${g.invoiceCount || 0} 张（有效证据 ${g.activeEvidenceInvoiceCount || 0} / 草稿 ${g.draftInvoiceCount || 0} / 已作废 ${g.voidedInvoiceCount || 0}）
      · 含税总额 ${sraMoney(g.grossAmount, g.amountDecimals)} · 有效已分配 ${sraMoney(g.activeAllocatedAmount, g.amountDecimals)}
      · 算术剩余 ${sraMoney(g.remainingAmount, g.amountDecimals)}
      · 未知到期日 ${g.unknownDueDateInvoiceCount || 0} 张 · 证据缺口 ${g.noAllocationInvoiceCount || 0} 张
      ${escapeHtml(g.supplierAvailable ? '' : '（供应商已停用 / 已删除，快照照常可读）')}
    </td></tr>`);
    rows.push(`<tr class="group-head"><th>发票身份</th><th>供应商</th><th>币种</th><th>到期日</th><th>账龄</th>
      <th class="text-right">含税总额证据</th><th class="text-right">有效已分配（ERP-066）</th>
      <th class="text-right">算术剩余证据</th><th>状态 / 分配</th><th>历史 / 无效证据</th><th>操作</th></tr>`);
    (g.invoices || []).forEach(r => rows.push(sraInvoiceRow(r)));
  });
  el.innerHTML = `<table>
    <thead><tr><th colspan="11">发票级证据（按供应商 + 币种分组；三类金额严格分列、绝不轧差）</th></tr></thead>
    <tbody>${rows.join('') || '<tr><td colspan="11" class="text-muted">本页没有符合条件的发票证据</td></tr>'}</tbody></table>`;
}

function sraRenderRules(data) {
  const el = document.getElementById('sra-rule');
  if (!el) return;
  el.innerHTML = `
    <div>📐 对账口径：${escapeHtml(data.rule || '')}</div>
    <div>🗓️ 账龄口径：${escapeHtml(data.agingRule || '')}</div>
    <div>💱 ${escapeHtml(data.currencyIsolationNote || '')}</div>
    <div>📊 范围：${escapeHtml(data.scopeNote || '')}</div>
    <div>🔒 明细授权：${escapeHtml(data.sourceFailClosedNote || '')}</div>
    <div>📤 导出：${escapeHtml(data.exportNote || '')}</div>
    <div>⛔ 边界：${escapeHtml(data.boundary || '')}</div>`;
}

function sraRenderPagination(data) {
  const el = document.getElementById('sra-pagination');
  if (!el) return;
  const page = data.page || 1;
  const totalPages = data.totalPages || 1;
  el.innerHTML = `
    <button class="btn btn-neutral btn-sm" ${page <= 1 ? 'disabled' : ''} onclick="loadSupplierReconciliationAging(${page - 1})">上一页</button>
    <span style="margin:0 10px">第 ${page} / ${totalPages} 页（共 ${data.total || 0} 张发票）</span>
    <button class="btn btn-neutral btn-sm" ${page >= totalPages ? 'disabled' : ''} onclick="loadSupplierReconciliationAging(${page + 1})">下一页</button>`;
}

/* 导出本页 CSV：与屏幕同一筛选 / 分页 / 币种隔离 / 未知到期日语义；
   **不写任何跨币种总额**，也不写「应付 / 欠款 / 已对账 / 逾期确认」等法律或账务断言 */
function exportSraCsv() {
  const data = SRA_DATA;
  if (!data) { toast('暂无可导出数据', 'warning'); return; }

  const rows = [];
  const money = (v) => (v === null || v === undefined) ? '未知' : String(v);
  const push = (cells) => rows.push(cells.map(c => `"${String(c === null || c === undefined ? '' : c).replace(/"/g, '""')}"`).join(','));

  push(['供应商对账与账龄工作台（只读派生；按币种分别成行，无跨币种总额）']);
  push(['账龄基准日（as-of）', data.asOfDateText || '']);
  push(['发票状态', data.invoiceStatusText || '']);
  push(['分配状态筛选', data.allocationStateText || '']);
  push(['本页 / 总页', `${data.page || 1} / ${data.totalPages || 1}`, '符合筛选的发票总数', data.total || 0]);
  push([]);

  push(['一、账龄分桶（只统计有效证据发票；未知到期日不计算账龄、不并入任何桶）']);
  push(['币种', '分桶', '发票张数', '含税总额证据', '有效已分配', '算术剩余证据', '剩余未知张数', '无效（超额）张数']);
  (data.currencies || []).forEach(c => (c.buckets || []).forEach(b => push([
    c.currency, SRA_BUCKET_LABELS[b.bucket] || b.bucketText || b.bucket,
    b.invoiceCount || 0, money(b.grossAmount), money(b.activeAllocatedAmount),
    money(b.remainingAmount), b.unknownRemainingInvoiceCount || 0, b.overAllocatedInvoiceCount || 0,
  ])));
  push([]);

  push(['二、未知到期日独立分组（账龄不计算；绝不并入任何账龄桶）']);
  push(['供应商', '币种', '发票张数', '含税总额证据', '有效已分配', '算术剩余证据', '发票身份']);
  (data.unknownDueDateGroups || []).forEach(g => push([
    g.supplierName, g.currency, g.invoiceCount || 0, money(g.grossAmount),
    money(g.activeAllocatedAmount), money(g.remainingAmount), (g.invoiceIdentities || []).join('；'),
  ]));
  push([]);

  push(['三、发票级证据（按供应商 + 币种；三类金额严格分列，草稿 / 已作废不计入有效合计）']);
  push(['供应商', '币种', '发票身份', '开票日期', '到期日', '账龄分桶', '逾期天数',
    '含税总额证据', '有效已分配', '算术剩余证据', '发票状态', '分配状态', '历史 / 无效证据', '说明']);
  (data.groups || []).forEach(g => (g.invoices || []).forEach(r => push([
    r.supplierName, r.currency, r.invoiceIdentityText, fmtDate(r.invoiceDate),
    r.dueDateKnown ? fmtDate(r.dueDate) : '未知', r.agingBucketText,
    (r.overdueDays === null || r.overdueDays === undefined) ? '未知' : r.overdueDays,
    money(r.grossAmount), money(r.activeAllocatedAmount), money(r.remainingAmount),
    r.invoiceStatusText, SRA_ALLOCATION_LABELS[r.allocationState] || r.allocationStateText,
    r.historicalEvidenceText, r.note,
  ])));

  const csv = '\uFEFF' + rows.join('\n');
  const blob = new Blob([csv], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = '供应商对账与账龄工作台.csv';
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
  toast('CSV 已导出（与屏幕同一筛选与币种隔离语义，无跨币种总额）', 'success');
}

/* 行操作：打开单张发票的对账与账龄证据明细。
   后端会重新校验登录身份与既有「角色 → 菜单」模块授权，并重新读取权威来源：
   未认证 / 未授权 / 发票不存在或已删除时一律拒绝（fail closed）—— 此时界面只显示拒绝原因，不显示任何证据。 */
async function showSupplierReconciliationAgingDetail(invoiceId) {
  const modal = document.getElementById('modal');
  if (!modal) return;
  const asOf = sraVal('sra-as-of');
  const url = `${SRA_API}/invoices/${invoiceId}` + (asOf ? `?asOfDate=${encodeURIComponent(asOf)}` : '');
  try {
    const data = await api(url);
    const r = data.row || {};
    modal.innerHTML = `<div class="modal modal-lg" style="width:1180px;max-width:97vw;max-height:92vh;overflow:auto">
      <h3>🧮 发票对账与账龄证据明细：${escapeHtml(r.invoiceIdentityText || '')}</h3>
      <div class="pd-hint">只读派生证据（三类金额严格分列、绝不轧差）。${escapeHtml(data.boundary || '')}</div>
      <div class="form-grid" style="grid-template-columns:repeat(4,1fr)">
        <div><b>供应商</b>：${escapeHtml(r.supplierName || '')}（${escapeHtml(r.supplierCode || '')}）<div class="text-muted">${escapeHtml(r.supplierAvailabilityText || '')}</div></div>
        <div><b>币种</b>：${escapeHtml(r.currency || '')}</div>
        <div><b>开票日期</b>：${escapeHtml(fmtDate(r.invoiceDate))}</div>
        <div><b>发票状态</b>：${escapeHtml(r.invoiceStatusText || '')}</div>
        <div><b>到期日（显式证据）</b>：${escapeHtml(r.dueDateKnown ? fmtDate(r.dueDate) : '未知')}<div class="text-muted">${escapeHtml(r.dueDateText || '')}</div></div>
        <div><b>付款条件</b>：${escapeHtml(r.paymentTermsText || '')}</div>
        <div><b>账龄（as-of ${escapeHtml(fmtDate(data.asOfDate))}）</b>：${escapeHtml(r.agingBucketText || '')}<div class="text-muted">${escapeHtml(r.agingText || '')}</div></div>
        <div><b>含税总额证据</b>：${sraMoney(r.grossAmount, r.amountDecimals)}<div class="text-muted">净额 ${sraMoney(r.netAmount, r.amountDecimals)} + 税额 ${sraMoney(r.taxAmount, r.amountDecimals)}</div></div>
        <div><b>有效已分配（ERP-066）</b>：${sraMoney(r.activeAllocatedAmount, r.amountDecimals)}<div class="text-muted">${sraInt(r.activeAllocationCount)} 条 / ${sraInt(r.activePaymentCount)} 张付款单</div></div>
        <div><b>算术剩余证据</b>：${sraMoney(r.remainingAmount, r.amountDecimals)}<div class="text-muted">${escapeHtml(r.remainingStateText || '')}</div></div>
        <div><b>分配状态</b>：${escapeHtml(SRA_ALLOCATION_LABELS[r.allocationState] || r.allocationStateText || '')}</div>
        <div><b>已作废引用行</b>：${sraInt(r.voidedAllocationCount)} 条 / ${sraMoney(r.voidedAllocationAmount, r.amountDecimals)}</div>
        <div><b>发票已失效（草稿 / 已作废）</b>：${sraInt(r.invoiceInactiveAllocationCount)} 条 / ${sraMoney(r.invoiceInactiveAllocationAmount, r.amountDecimals)}</div>
        <div><b>无效证据</b>：${sraInt(r.invalidAllocationCount)} 条 / ${sraMoney(r.invalidAllocationAmount, r.amountDecimals)}</div>
        <div><b>无法确认证据</b>：${sraInt(r.unavailableAllocationCount)} 条 / ${sraMoney(r.unavailableAllocationAmount, r.amountDecimals)}</div>
        <div><b>历史 / 无效证据</b>：${escapeHtml(r.historicalEvidenceText || '')}</div>
      </div>
      <div class="pd-hint">行说明：${escapeHtml(r.note || '')}</div>
      <div class="pd-hint">对账口径：${escapeHtml(data.rule || '')}</div>
      <div class="pd-hint">账龄口径：${escapeHtml(data.agingRule || '')}</div>
      <div class="pd-hint">授权复核：${escapeHtml(data.authorizationNote || '')}</div>
      <div class="modal-footer">
        <button class="btn btn-neutral" onclick="closeModal()">关闭</button>
      </div>
    </div>`;
    modal.style.display = 'flex';
  } catch (err) {
    modal.innerHTML = `<div class="modal" style="width:760px;max-width:95vw">
      <h3>⛔ 明细打开被拒绝（fail closed）</h3>
      <div class="pd-hint">拒绝原因：${escapeHtml(err.message || '')}</div>
      <div class="pd-hint">${escapeHtml((SRA_DATA && SRA_DATA.sourceFailClosedNote) || '未认证 / 未获模块授权 / 来源已删除时一律拒绝，不返回任何部分证据，也不做来源修复或改派。')}</div>
      <div class="modal-footer"><button class="btn btn-neutral" onclick="closeModal()">关闭</button></div>
    </div>`;
    modal.style.display = 'flex';
    toast('明细打开失败（fail closed）：' + (err.message || ''), 'error');
  }
}