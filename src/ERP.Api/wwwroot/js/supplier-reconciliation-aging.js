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
        <button class="btn btn-neutral" onclick="openSupplierAgingDesigner()" title="按 ERP-140 白名单字段目录选择证据列，复用工作台当前筛选只读预览并导出所选列 CSV（无跨币种总额）">🎛 字段设计器</button>
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

/* ============ 供应商对账与账龄 · 证据字段设计器（ERP-141：只读、有界的前端字段 / 筛选设计器） ============
   口径与后端 ERP-140（DynamicSupplierAgingReportController / DynamicSupplierAgingReportRules）一一对应：
   - 字段选择器只由 GET /api/supplier-reconciliation-aging/report 返回的有限白名单目录渲染，绝无自由填写的字段名或 SQL；
   - 预览复用工作台当前筛选（供应商 / 币种 / 发票状态 / 分配状态 / 开票日期 / 到期日 / as-of / 关键字 / 每页），
     并 POST /api/supplier-reconciliation-aging/report，只发送「白名单字段 + 当前筛选 + 有界分页（pageSize 1~200）」，按请求顺序渲染返回的列名与单元格；
   - 未知到期日（dueDate / dueDateKnown / agingBucket / overdueDays 缺失）、未知剩余（remainingAmount 缺失）与未知计数一律显示「未知」，
     绝不回落为 0；不同币种分别成行、绝不合并，页面与导出都没有任何跨币种总额；
   - 全程只读：不写库、不迁移、不执行任意 SQL；授权 / 无效请求 / 空结果 / 网络失败都在界面可见，且不暴露范围外数据。 */

const SRA_DESIGNER_API = '/api/supplier-reconciliation-aging/report';

/* ERP-144 允许的分组键（有限、只读；后端 fail closed 拒绝非法取值，前端绝不发送范围外键） */
const SRA_GROUP_KEYS = [
  { key: 'none', label: '不分组（仅表格）' },
  { key: 'supplier', label: '按供应商' },
  { key: 'currency', label: '按币种' },
  { key: 'agingBucket', label: '按账龄分桶' },
  { key: 'allocationState', label: '按分配状态' },
];

/* ERP-146 允许的金额汇总模式（有限、只读；后端 fail closed 拒绝非法取值，前端绝不发送范围外键） */
const SRA_SUMMARY_MODES = [
  { key: 'none', label: '不汇总金额（仅表格 / 分组计数）' },
  { key: 'supplierCurrency', label: '按供应商 + 币种汇总金额' },
  { key: 'supplierCurrencyAging', label: '按供应商 + 币种 + 账龄分桶汇总金额' },
];

let SRA_DESIGNER = {
  catalog: null,      // GET /api/supplier-reconciliation-aging/report 返回的目录 DTO
  fields: [],         // 目录字段（白名单）
  selectedKeys: [],   // 当前勾选的字段键（默认全选）
  groupBy: 'none',    // 当前分组键（仅 ERP-144 白名单；非法值回落 none）
  summaryMode: 'none', // 当前金额汇总模式（仅 ERP-146 白名单；非法值回落 none）
  view: null,         // 最近一次预览结果
};

/* ==================== 纯函数（可在 Node 中逐条单测） ==================== */

/* HTML 转义（本地独立实现，避免依赖全局 escapeHtml 的加载顺序） */
function sraDesEsc(v) {
  return String(v == null ? '' : v).replace(/[&<>"']/g, c => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}

/* 规范化选定字段（fail closed）：只保留目录白名单内的键、去重、保持请求顺序；未知键丢弃（绝不进入请求） */
function sraDesSelectFields(catalogFields, selectedKeys) {
  const valid = new Set((catalogFields || []).map(f => f && f.key).filter(Boolean));
  const seen = new Set();
  const result = [];
  for (const k of (Array.isArray(selectedKeys) ? selectedKeys : [])) {
    if (typeof k !== 'string') continue;
    const key = k.trim();
    if (!key || !valid.has(key) || seen.has(key)) continue;
    seen.add(key);
    result.push(key);
  }
  return result;
}

/* 分组键规范化（fail closed）：只保留 ERP-144 允许的分组键，缺失 / 空白 / 非法值一律回落 none（绝不进入请求） */
function sraDesGroupKey(value) {
  const key = String(value == null ? '' : value).trim();
  return SRA_GROUP_KEYS.some(g => g.key === key) ? key : 'none';
}

/* 金额汇总模式规范化（fail closed）：只保留 ERP-146 允许的模式，缺失 / 空白 / 非法值一律回落 none（绝不进入请求） */
function sraDesSummaryMode(value) {
  const key = String(value == null ? '' : value).trim();
  return SRA_SUMMARY_MODES.some(m => m.key === key) ? key : 'none';
}

/* 组装有界预览请求体：字段只来自目录、分页有界、筛选仅复用工作台当前筛选，绝不接受任意字段名或 SQL */
function sraDesBuildRequest(state) {
  const fields = sraDesSelectFields(state.catalogFields, state.selectedKeys);
  const page = Math.max(1, Math.floor(Number(state.page) || 1));
  const maxPageSize = Number(state.maxPageSize) || 200;
  let pageSize = Math.floor(Number(state.pageSize));
  if (!Number.isFinite(pageSize)) pageSize = 50;
  pageSize = Math.max(1, Math.min(maxPageSize, pageSize));

  const req = { fields, page, pageSize };
  req.groupBy = sraDesGroupKey(state.groupBy);
  req.summaryMode = sraDesSummaryMode(state.summaryMode);

  const supplierId = Number(state.supplierId);
  if (Number.isFinite(supplierId) && supplierId > 0) req.supplierId = supplierId;
  if (state.currency) req.currency = state.currency;
  if (state.invoiceStatus) req.invoiceStatus = state.invoiceStatus;
  if (state.allocationState) req.allocationState = state.allocationState;
  if (state.keyword) req.keyword = state.keyword;

  const invoiceDateFrom = state.invoiceDateFrom ? String(state.invoiceDateFrom).slice(0, 10) : null;
  const invoiceDateTo = state.invoiceDateTo ? String(state.invoiceDateTo).slice(0, 10) : null;
  if (invoiceDateFrom) req.invoiceDateFrom = invoiceDateFrom;
  if (invoiceDateTo) req.invoiceDateTo = invoiceDateTo;

  const dueDateFrom = state.dueDateFrom ? String(state.dueDateFrom).slice(0, 10) : null;
  const dueDateTo = state.dueDateTo ? String(state.dueDateTo).slice(0, 10) : null;
  if (dueDateFrom) req.dueDateFrom = dueDateFrom;
  if (dueDateTo) req.dueDateTo = dueDateTo;

  if (state.asOfDate) req.asOfDate = String(state.asOfDate).slice(0, 10);
  return req;
}
/* 单元格纯文本：null / undefined = 「未知」（未知到期日 / 未知剩余 / 未知计数，绝不回落为 0）；布尔显示 是/否；日期截断到日 */
function sraDesCellText(value, field) {
  const dataType = (field && field.dataType) || 'text';
  if (value === null || value === undefined) return '未知';
  if (dataType === 'boolean') return (value === true || value === 'true' || value === 1 || value === '1') ? '是' : '否';
  if (dataType === 'date') return String(value).slice(0, 10);
  return String(value);
}

/* 单元格 HTML（转义后安全渲染） */
function sraDesRenderCell(value, field) {
  return sraDesEsc(sraDesCellText(value, field));
}

/* CSV 单元格：未知值保留「未知」，并转义以 = + - @ 或制表符 / 回车开头的文本（防公式注入） */
function sraDesCsvCell(value, field) {
  let text = sraDesCellText(value, field);
  if (/^[-=+@\t\r]/.test(text)) text = "'" + text;
  return text;
}

/* 当前预览页的所选列 CSV（纯字符串）：表头为返回的列名，行内仅选定字段；未知值保留、公式首字符转义、引号转义、CRLF + BOM */
function sraDesCsv(view) {
  const cols = (view && view.columns) || [];
  const rows = (view && view.rows) || [];
  const q = (v) => '"' + String(v).replace(/"/g, '""') + '"';
  const header = cols.map(c => q(c.label || c.key)).join(',');
  const body = rows.map(r => cols.map(c => q(sraDesCsvCell(r[c.key], c))).join(',')).join('\r\n');
  const lines = [header];
  if (body) lines.push(body);
  return '\uFEFF' + lines.join('\r\n');
}

/* 结果表格 HTML：表头为返回的列名、单元格为返回的选定字段值，全部经转义 */
function sraDesTableHtml(view) {
  const cols = (view && view.columns) || [];
  const rows = (view && view.rows) || [];
  if (!cols.length) return '';
  const align = c => (c.dataType === 'number' || c.dataType === 'boolean') ? ' class="text-right"' : '';
  const head = cols.map(c => `<th${align(c)}>${sraDesEsc(c.label || c.key)}</th>`).join('');
  const body = rows.map(r =>
    `<tr>${cols.map(c => `<td${align(c)}>${sraDesRenderCell(r[c.key], c)}</td>`).join('')}</tr>`).join('');
  const emptyRow = `<tr><td colspan="${cols.length}" class="empty">本页没有符合条件的发票证据</td></tr>`;
  const prevDisabled = !view || view.page <= 1 ? ' disabled' : '';
  const nextDisabled = !view || view.page >= view.totalPages ? ' disabled' : '';
  const paging = `<div style="display:flex;justify-content:space-between;align-items:center;margin-top:8px">
      <span class="text-muted">第 ${view ? view.page : 1} 页 / 共 ${view ? view.totalPages : 0} 页</span>
      <div>
        <button class="btn btn-neutral btn-sm" onclick="sraDesPage(-1)"${prevDisabled}>← 上一页</button>
        <button class="btn btn-neutral btn-sm" onclick="sraDesPage(1)"${nextDisabled}>下一页 →</button>
      </div></div>`;
  return `<div class="table-wrap" style="margin-top:8px"><table><thead><tr>${head}</tr></thead><tbody>${body || emptyRow}</tbody></table></div>${paging}`;
}

/* 空结果提示 */
function sraDesEmptyHtml() {
  return '<div class="empty" style="margin:8px 0">没有符合条件的供应商发票证据（当前账号数据范围内的只读快照）。</div>';
}

/* 错误提示（授权 / 未登录 / 无效请求 / 空导出 / 网络失败分别可见，且不暴露任何数据） */
function sraDesErrorHtml(kind, message) {
  const labels = {
    forbidden: '权限不足',
    unauthorized: '未登录 / 登录已过期',
    invalid: '请求无效',
    empty: '导出内容为空',
    network: '网络请求失败',
    error: '预览失败',
  };
  return `<div class="pd-hint" style="color:#b91c1c;background:#fef2f2;border-color:#fecaca">
      <b>${sraDesEsc(labels[kind] || '预览失败')}</b>：${sraDesEsc(message || '')}</div>`;
}

/* 分组计数条形图（仅当前预览页）：标签转义渲染、计数未知不回落 0；固定分类空类计数为 0 仍显示、动态分组空页可见提示 */
function sraDesGroupChartHtml(view) {
  const groupBy = (view && view.groupBy) || 'none';
  if (groupBy === 'none' || !view || !Array.isArray(view.groups)) return '';
  const groups = view.groups;
  const titles = {
    supplier: '按供应商分组 · 本页发票张数',
    currency: '按币种分组 · 本页发票张数',
    agingBucket: '按账龄分桶分组 · 本页发票张数',
    allocationState: '按分配状态分组 · 本页发票张数',
  };
  const title = titles[groupBy] || '本页发票张数分组';
  const counts = groups.map(g => (g && typeof g.count === 'number' && Number.isFinite(g.count)) ? g.count : 0);
  const max = Math.max(1, ...counts);
  const rows = groups.map(g => {
    const label = sraDesEsc((g && g.label) || (g && g.key) || '未知');
    const unknown = !g || g.count === null || g.count === undefined || !Number.isFinite(Number(g.count));
    const count = unknown ? '未知' : String(g.count);
    const pct = unknown ? 0 : Math.max(0, Math.round((Number(g.count) / max) * 100));
    return `<div style="display:flex;align-items:center;gap:8px;margin:3px 0">`
      + `<span style="min-width:180px;text-align:right;font-size:13px">${label}</span>`
      + `<div style="flex:1;background:#e2e8f0;border-radius:4px;height:12px;overflow:hidden">`
      + `<div style="height:12px;background:#2563eb;width:${pct}%"></div></div>`
      + `<span style="min-width:48px;font-variant-numeric:tabular-nums;font-size:13px">${sraDesEsc(count)}</span></div>`;
  }).join('');
  const empty = groups.length === 0
    ? '<div class="text-muted" style="margin:4px 0">本页没有可分组计数的发票证据（空页）。</div>'
    : '';
  return `<div class="pd-hint" style="margin:8px 0">`
    + `<div style="font-weight:600;margin-bottom:6px">📊 ${sraDesEsc(title)}（仅当前预览页，非全量合计）</div>`
    + `${rows}${empty}</div>`;
}

/* 汇总金额显示：null / undefined = 未知（命中上限或无效证据，绝不回落 0） */
function sraDesMoney(v) {
  if (v === null || v === undefined) return '未知';
  const n = Number(v);
  if (!Number.isFinite(n)) return '未知';
  return String(Math.round(n * 100) / 100);
}

/* 汇总计数显示：null / undefined = 未知 */
function sraDesInt(v) {
  if (v === null || v === undefined) return '未知';
  return String(v);
}

/* 当前页金额汇总（ERP-146，仅当前预览页）：供应商 + 币种（可选账龄分桶）的已知有效金额；
   未知值绝不回落 0，草稿 / 已作废金额绝不并入，不同币种绝不合并或换算 */
function sraDesSummaryHtml(view) {
  const mode = sraDesSummaryMode(view && view.summaryMode);
  if (!view || mode === 'none' || !Array.isArray(view.summaries)) return '';
  const summaries = view.summaries;
  const withBucket = mode === 'supplierCurrencyAging';
  if (summaries.length === 0) {
    return `<div class="pd-hint" style="margin:8px 0">`
      + `<div style="font-weight:600;margin-bottom:6px">💰 当前页金额汇总（仅当前预览页，非全量合计）</div>`
      + `<div class="text-muted">本页没有可汇总金额的有效发票证据（空页，或本页发票均为草稿 / 已作废）。草稿 / 已作废金额绝不并入有效合计。</div>`
      + `</div>`;
  }
  const head = `<th>供应商</th><th>币种</th>`
    + (withBucket ? `<th>账龄分桶</th>` : '')
    + `<th class="text-right">发票张数</th>`
    + `<th class="text-right">含税总额证据</th>`
    + `<th class="text-right">有效已分配</th>`
    + `<th class="text-right">算术剩余证据</th>`
    + `<th class="text-right">剩余未知 / 无效（超额）</th>`;
  const rows = summaries.map(s => {
    const name = sraDesEsc((s && s.supplierName) || '未知');
    const code = sraDesEsc((s && s.supplierCode) || '');
    const supplier = code ? `${name}（${code}）` : name;
    const bucket = withBucket ? `<td>${sraDesEsc((s && s.agingBucketText) || (s && s.agingBucket) || '未知')}</td>` : '';
    const unknownCount = sraDesInt(s && s.unknownRemainingInvoiceCount);
    const overCount = sraDesInt(s && s.overAllocatedInvoiceCount);
    return `<tr>`
      + `<td>${supplier}</td>`
      + `<td><b>${sraDesEsc((s && s.currency) || '未知')}</b></td>`
      + bucket
      + `<td class="text-right">${sraDesInt(s && s.invoiceCount)}</td>`
      + `<td class="text-right">${sraDesMoney(s && s.grossAmount)}</td>`
      + `<td class="text-right">${sraDesMoney(s && s.activeAllocatedAmount)}</td>`
      + `<td class="text-right">${sraDesMoney(s && s.remainingAmount)}</td>`
      + `<td class="text-right">${sraDesEsc(unknownCount)} / ${sraDesEsc(overCount)}</td>`
      + `</tr>`;
  }).join('');
  return `<div class="pd-hint" style="margin:8px 0">`
    + `<div style="font-weight:600;margin-bottom:6px">💰 当前页金额汇总（仅当前预览页，非全量合计）</div>`
    + `<table style="width:100%;margin-bottom:6px"><thead><tr>${head}</tr></thead><tbody>${rows}</tbody></table>`
    + `<div class="text-muted">口径：只汇总计入有效应付证据合计（已登记未作废）的发票金额；草稿 / 已作废金额绝不并入；不同币种绝不合并或换算；有效已分配或算术剩余证据未知（命中上限）或无效（超额）时合计保持「未知」。</div>`
    + `</div>`;
}

/* 预览结果（口径 / 边界 / 免责文案 + 分组计数 + 汇总 + 空结果 + 表格） */
function sraDesResultHtml(view) {
  const readOnly = view && view.readOnlyText ? `<div class="pd-hint">${sraDesEsc(view.readOnlyText)}</div>` : '';
  const boundary = view && view.boundaryText ? `<div class="pd-hint">${sraDesEsc(view.boundaryText)}</div>` : '';
  const disclaimer = view && view.disclaimerText ? `<div class="pd-hint" style="color:#64748b">${sraDesEsc(view.disclaimerText)}</div>` : '';
  const summary = view
    ? `<div class="text-muted" style="margin:6px 0">共 ${view.total} 条 · 第 ${view.page} 页 · 每页 ${view.pageSize} 条 · 本页 ${(view.rows || []).length} 行证据</div>`
    : '';
  const empty = view && (!view.rows || view.rows.length === 0) ? sraDesEmptyHtml() : '';
  return `${readOnly}${boundary}${disclaimer}${summary}${sraDesGroupChartHtml(view)}${sraDesSummaryHtml(view)}${empty}${sraDesTableHtml(view)}`;
}

/* 字段选择器：仅由目录白名单渲染为复选框，无自由填写的字段名 */
function sraDesFieldChooserHtml(fields, selectedKeys) {
  const selected = new Set(selectedKeys || []);
  return (fields || []).map(f => {
    const checked = selected.has(f.key) ? 'checked' : '';
    return `<label style="display:inline-flex;align-items:center;gap:4px;margin:3px 6px 3px 0;padding:2px 8px;border:1px solid #e2e8f0;border-radius:6px;background:#f8fafc;cursor:pointer">
        <input type="checkbox" name="sra-des-field" value="${sraDesEsc(f.key)}" ${checked} onchange="sraDesSyncSelection()">
        <span>${sraDesEsc(f.label || f.key)}</span></label>`;
  }).join('');
}
/* 分组键选择器：仅 ERP-144 允许的分组键（fail closed，无自由输入） */
function sraDesGroupSelectHtml(groupBy) {
  const selected = sraDesGroupKey(groupBy);
  const opts = SRA_GROUP_KEYS.map(g =>
    `<option value="${sraDesEsc(g.key)}" ${g.key === selected ? 'selected' : ''}>${sraDesEsc(g.label)}</option>`).join('');
  return `<select id="sra-des-groupby" style="min-width:180px">${opts}</select>`;
}

/* 金额汇总模式选择器：仅 ERP-146 允许的模式（fail closed，无自由输入） */
function sraDesSummarySelectHtml(summaryMode) {
  const selected = sraDesSummaryMode(summaryMode);
  const opts = SRA_SUMMARY_MODES.map(m =>
    `<option value="${sraDesEsc(m.key)}" ${m.key === selected ? 'selected' : ''}>${sraDesEsc(m.label)}</option>`).join('');
  return `<select id="sra-des-summarymode" style="min-width:260px">${opts}</select>`;
}

/* ==================== 状态 / 请求 / 渲染 ==================== */

/* 轻量请求封装：返回完整 ApiResponse 信封（保留 code），网络异常抛给调用方 */
async function sraDesRequest(path, method = 'GET', body = null) {
  const headers = { 'Content-Type': 'application/json' };
  const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
  if (token) headers['Authorization'] = 'Bearer ' + token;
  const opts = { method, headers };
  if (body) opts.body = JSON.stringify(body);
  const resp = await fetch(path, opts);
  return await resp.json();
}

/* 业务码 → 错误态分类 */
function sraDesKindOfCode(code) {
  if (code === 2002) return 'forbidden';
  if (code === 2000 || code === 2003) return 'unauthorized';
  return 'invalid';
}

/* 目录加载失败 / 授权失败时的整页错误态（带关闭） */
function sraDesErrorModalHtml(kind, message) {
  return `<div class="modal modal-lg">
    <h3>🎛 供应商对账与账龄 · 证据字段设计器</h3>
    ${sraDesErrorHtml(kind, message)}
    <div class="modal-footer"><button class="btn btn-neutral" onclick="closeModal()">关闭</button></div>
  </div>`;
}

function sraDesLoadingHtml() {
  return '<div class="pd-hint" style="text-align:center;color:#64748b">正在预览（只读查询）…</div>';
}

function sraDesRenderResult(html) {
  const el = document.getElementById('sra-des-result');
  if (el) el.innerHTML = html;
}

/* 同步勾选状态到 selectedKeys（复选框 onchange） */
function sraDesSyncSelection() {
  const boxes = document.querySelectorAll('input[name="sra-des-field"]');
  SRA_DESIGNER.selectedKeys = Array.from(boxes).filter(b => b.checked).map(b => b.value);
}

function sraDesToggleAll(checked) {
  const boxes = document.querySelectorAll('input[name="sra-des-field"]');
  SRA_DESIGNER.selectedKeys = [];
  boxes.forEach(b => { b.checked = checked; if (checked) SRA_DESIGNER.selectedKeys.push(b.value); });
}

/* 当前筛选摘要（只读展示工作台当前筛选，预览与导出复用这些值） */
function sraDesFilterSummaryHtml() {
  const rows = [];
  const add = (label, v) => { if (v) rows.push(`<div><span class="text-muted">${label}</span>：<b>${sraDesEsc(v)}</b></div>`); };
  add('供应商', sraVal('sra-supplier'));
  add('币种', sraVal('sra-currency'));
  add('发票状态', sraVal('sra-invoice-status'));
  add('分配状态', sraVal('sra-allocation-state'));
  add('开票日期', [sraVal('sra-invoice-date-from'), sraVal('sra-invoice-date-to')].filter(Boolean).join(' ~ '));
  add('到期日', [sraVal('sra-due-date-from'), sraVal('sra-due-date-to')].filter(Boolean).join(' ~ '));
  add('账龄基准日(as-of)', sraVal('sra-as-of'));
  add('关键字', sraVal('sra-keyword'));
  add('每页', sraVal('sra-pagesize') || '50');
  return rows.length
    ? `<div class="pd-hint" style="color:#64748b">${rows.join('')}</div>`
    : '<div class="pd-hint" style="color:#64748b">未设置筛选（全部供应商 / 全部币种 / 已登记证据）</div>';
}

/* 渲染设计器（字段选择器 + 当前筛选摘要 + 预览 / 导出按钮 + 结果区） */
function sraDesRender() {
  document.getElementById('modal').innerHTML = `
  <div class="modal modal-lg" style="max-width:1100px">
    <h3>🎛 供应商对账与账龄 · 证据字段设计器（只读预览）</h3>
    <div class="pd-hint">只读：仅按 ERP-140 白名单字段与工作台当前筛选预览当前账号可见的供应商发票证据；不新增 / 修改 / 删除任何记录，不执行任意 SQL。</div>

    <div style="margin:10px 0">
      <div style="font-weight:600;margin-bottom:6px">① 选择证据字段（仅 ERP-140 白名单目录，无自由字段名）</div>
      <div style="margin-bottom:6px">
        <button class="btn btn-neutral btn-sm" onclick="sraDesToggleAll(true)">全选</button>
        <button class="btn btn-neutral btn-sm" onclick="sraDesToggleAll(false)">清空</button>
      </div>
      <div style="max-height:180px;overflow:auto;border:1px solid #e2e8f0;border-radius:8px;padding:8px">${sraDesFieldChooserHtml(SRA_DESIGNER.fields, SRA_DESIGNER.selectedKeys)}</div>
    </div>

    <div style="margin:10px 0">
      <div style="font-weight:600;margin-bottom:6px">② 分组计数（可选，仅当前预览页发票张数）</div>
      ${sraDesGroupSelectHtml(SRA_DESIGNER.groupBy)}
      <div class="text-muted" style="margin-top:4px">只统计当前预览页发票张数，绝不求和金额、绝不跨币种合并或换算；未知到期日与无效 / 未知分配证据保持可见。</div>
    </div>

    <div style="margin:10px 0">
      <div style="font-weight:600;margin-bottom:6px">③ 金额汇总（ERP-146，可选，仅当前预览页已知有效金额）</div>
      ${sraDesSummarySelectHtml(SRA_DESIGNER.summaryMode)}
      <div class="text-muted" style="margin-top:4px">只汇总当前预览页计入有效应付证据合计的发票金额；草稿 / 已作废金额绝不并入，未知 / 无效分配证据保持「未知」，不同币种绝不合并或换算。</div>
    </div>

    <div style="margin:10px 0">
      <div style="font-weight:600;margin-bottom:6px">④ 当前筛选（复用工作台，只读）</div>
      ${sraDesFilterSummaryHtml()}
    </div>

    <div class="modal-footer">
      <button class="btn btn-primary" onclick="sraDesPreview(1)">预览</button>
      <button class="btn btn-neutral" onclick="sraDesExport()">📤 导出所选列 CSV</button>
      <button class="btn btn-neutral" onclick="sraDesExportXlsx()" title="导出当前预览页为 Excel（xlsx，只读）：复用当前字段 / 筛选 / 分页，未知值保留、公式首字符转义、无跨币种总额">📥 导出 Excel（xlsx）</button>
      <button class="btn btn-neutral" onclick="sraDesExportPdf()" title="导出当前预览页为分页中文 PDF（只读）：复用当前字段 / 筛选 / 分页，宽列集跨页拆分、未知证据显式保留、中文字体缺失时显式失败">📄 导出 PDF（分页中文）</button>
      <button class="btn btn-neutral" onclick="closeModal()">关闭</button>
    </div>
    <div id="sra-des-result"></div>
  </div>`;
}
/* 读取当前字段 / 工作台当前筛选 / 分页状态（预览与分页复用，单一来源） */
function sraDesBuildState(page) {
  return {
    catalogFields: SRA_DESIGNER.fields,
    selectedKeys: SRA_DESIGNER.selectedKeys,
    supplierId: sraVal('sra-supplier'),
    currency: sraVal('sra-currency'),
    invoiceStatus: sraVal('sra-invoice-status'),
    allocationState: sraVal('sra-allocation-state'),
    invoiceDateFrom: sraVal('sra-invoice-date-from'),
    invoiceDateTo: sraVal('sra-invoice-date-to'),
    dueDateFrom: sraVal('sra-due-date-from'),
    dueDateTo: sraVal('sra-due-date-to'),
    asOfDate: sraVal('sra-as-of'),
    keyword: sraVal('sra-keyword'),
    pageSize: sraVal('sra-pagesize') || '50',
    page: page || 1,
    groupBy: sraVal('sra-des-groupby'),
    summaryMode: sraVal('sra-des-summarymode'),
    maxPageSize: SRA_DESIGNER.catalog && SRA_DESIGNER.catalog.maxPageSize ? SRA_DESIGNER.catalog.maxPageSize : 200,
  };
}

/* 预览：组装有界请求 → POST → 安全渲染列名与单元格；授权 / 无效 / 空 / 网络失败均可见 */
async function sraDesPreview(page) {
  const state = sraDesBuildState(page);
  if (state.invoiceDateFrom && state.invoiceDateTo && state.invoiceDateFrom > state.invoiceDateTo) {
    sraDesRenderResult(sraDesErrorHtml('invalid', '开票日期开始不能晚于结束日期'));
    return;
  }
  if (state.dueDateFrom && state.dueDateTo && state.dueDateFrom > state.dueDateTo) {
    sraDesRenderResult(sraDesErrorHtml('invalid', '到期日开始不能晚于结束日期'));
    return;
  }

  const req = sraDesBuildRequest(state);
  sraDesRenderResult(sraDesLoadingHtml());

  try {
    const resp = await sraDesRequest(SRA_DESIGNER_API, 'POST', req);
    if (resp.code === 0) {
      SRA_DESIGNER.view = resp.data;
      sraDesRenderResult(sraDesResultHtml(resp.data));
    } else if (resp.code === 2000 || resp.code === 2003) {
      if (typeof logout === 'function') logout();
      sraDesRenderResult(sraDesErrorHtml('unauthorized', resp.message));
    } else {
      sraDesRenderResult(sraDesErrorHtml(sraDesKindOfCode(resp.code), resp.message));
    }
  } catch (err) {
    sraDesRenderResult(sraDesErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 翻页（有界：最小第 1 页） */
function sraDesPage(delta) {
  const view = SRA_DESIGNER.view;
  const page = (view ? view.page : 1) + delta;
  if (page < 1) return;
  sraDesPreview(page);
}

/* 导出当前预览页的所选列 CSV（只读）：未知值保留、公式首字符转义、无跨币种总额；空结果可见错误，不下载仅表头的 CSV */
function sraDesExport() {
  const view = SRA_DESIGNER.view;
  if (!view || !view.rows || view.rows.length === 0) {
    sraDesRenderResult(sraDesErrorHtml('empty', '当前预览页没有发票证据，无法导出'));
    return;
  }
  const csv = sraDesCsv(view);
  const blob = new Blob([csv], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  const dateStr = new Date().toISOString().slice(0, 10).replace(/-/g, '');
  a.href = url;
  a.download = '供应商对账与账龄_所选列_' + dateStr + '.csv';
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
  toast('所选列 CSV 已导出（仅当前预览页，未知值保留，无跨币种总额）', 'success');
}

/* 从工作台打开设计器（加载目录，渲染字段选择器与当前筛选；授权失败 fail closed，不返回任何字段） */
async function openSupplierAgingDesigner() {
  SRA_DESIGNER = { catalog: null, fields: [], selectedKeys: [], groupBy: 'none', summaryMode: 'none', view: null };
  const modal = document.getElementById('modal');
  if (!modal) return;
  modal.innerHTML = '<div class="modal modal-lg" style="max-width:1100px"><div class="pd-hint" style="text-align:center;color:#64748b">正在加载证据字段目录…</div></div>';
  modal.style.display = 'flex';

  try {
    const resp = await sraDesRequest(SRA_DESIGNER_API);
    if (resp.code === 2000 || resp.code === 2003) {
      if (typeof logout === 'function') logout();
      modal.innerHTML = sraDesErrorModalHtml('unauthorized', resp.message);
      return;
    }
    if (resp.code !== 0) {
      modal.innerHTML = sraDesErrorModalHtml(sraDesKindOfCode(resp.code), resp.message);
      return;
    }
    SRA_DESIGNER.catalog = resp.data;
  } catch (err) {
    modal.innerHTML = sraDesErrorModalHtml('network', (err && err.message) || '无法连接到服务器');
    return;
  }

  SRA_DESIGNER.fields = (SRA_DESIGNER.catalog && SRA_DESIGNER.catalog.fields) || [];
  SRA_DESIGNER.selectedKeys = SRA_DESIGNER.fields.map(f => f.key);
  sraDesRender();
}

/* 触发浏览器下载 xlsx 附件：授权 / 无效失败解析业务错误信封，网络失败抛错（绝不下载非 xlsx 内容） */
async function sraDesDownloadXlsx(path, body) {
  const headers = { 'Content-Type': 'application/json' };
  const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
  if (token) headers['Authorization'] = 'Bearer ' + token;
  const resp = await fetch(path, { method: 'POST', headers, body: JSON.stringify(body) });

  const contentType = resp.headers.get('content-type') || '';
  const isXlsx = contentType.indexOf('spreadsheetml') >= 0;
  if (!isXlsx) {
    let message = '导出失败';
    let kind = 'error';
    try {
      const data = await resp.json();
      if (data && typeof data === 'object') {
        if (data.code === 2000 || data.code === 2003) kind = 'unauthorized';
        else if (data.code === 2002) kind = 'forbidden';
        else if (data.code && data.code !== 0) kind = 'invalid';
        if (data.message) message = data.message;
      }
    } catch (e) { /* 忽略非 JSON 响应体 */ }
    if (kind === 'unauthorized' && typeof logout === 'function') logout();
    const err = new Error(message);
    err.kind = kind;
    throw err;
  }

  const blob = await resp.blob();
  const disposition = resp.headers.get('content-disposition') || '';
  const match = /filename[^;=\n]*=((['"]).*?\2|[^;\n]*)/i.exec(disposition);
  const filename = (match && match[1] ? match[1].replace(/['"]/g, '') : '') || 'SupplierAging.xlsx';
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
  sraDesRenderResult('<div class="pd-hint">已导出当前页为 Excel（xlsx），请查看下载。</div>');
}

/* 导出当前预览页为 Excel（xlsx，只读）：复用当前字段 / 工作台筛选 / 分页组装有界请求后 POST 导出；
   未知到期日 / 未知剩余 / 未知计数保留、公式首字符转义、无跨币种总额；空页可见错误，不下载仅表头文件；
   授权 / 无效 / 网络失败均在结果区可见。 */
async function sraDesExportXlsx() {
  const view = SRA_DESIGNER.view;
  if (!view || !view.rows || view.rows.length === 0) {
    sraDesRenderResult(sraDesErrorHtml('empty', '当前预览页没有发票证据，无法导出'));
    return;
  }

  const state = sraDesBuildState(view.page || 1);
  if (state.invoiceDateFrom && state.invoiceDateTo && state.invoiceDateFrom > state.invoiceDateTo) {
    sraDesRenderResult(sraDesErrorHtml('invalid', '开票日期开始不能晚于结束日期'));
    return;
  }
  if (state.dueDateFrom && state.dueDateTo && state.dueDateFrom > state.dueDateTo) {
    sraDesRenderResult(sraDesErrorHtml('invalid', '到期日开始不能晚于结束日期'));
    return;
  }

  const req = sraDesBuildRequest(state);
  sraDesRenderResult(sraDesLoadingHtml());
  try {
    await sraDesDownloadXlsx(SRA_DESIGNER_API + '/export', req);
  } catch (err) {
    sraDesRenderResult(sraDesErrorHtml((err && err.kind) || 'network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 触发浏览器下载 pdf 附件：授权 / 无效失败解析业务错误信封，网络失败抛错（绝不下载非 pdf 内容） */
async function sraDesDownloadPdf(path, body) {
  const headers = { 'Content-Type': 'application/json' };
  const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
  if (token) headers['Authorization'] = 'Bearer ' + token;
  const resp = await fetch(path, { method: 'POST', headers, body: JSON.stringify(body) });

  const contentType = resp.headers.get('content-type') || '';
  const isPdf = contentType.indexOf('application/pdf') >= 0;
  if (!isPdf) {
    let message = '导出失败';
    let kind = 'error';
    try {
      const data = await resp.json();
      if (data && typeof data === 'object') {
        if (data.code === 2000 || data.code === 2003) kind = 'unauthorized';
        else if (data.code === 2002) kind = 'forbidden';
        else if (data.code && data.code !== 0) kind = 'invalid';
        if (data.message) message = data.message;
      }
    } catch (e) { /* 忽略非 JSON 响应体 */ }
    if (kind === 'unauthorized' && typeof logout === 'function') logout();
    const err = new Error(message);
    err.kind = kind;
    throw err;
  }

  const blob = await resp.blob();
  const disposition = resp.headers.get('content-disposition') || '';
  const match = /filename[^;=\n]*=((['"]).*?\2|[^;\n]*)/i.exec(disposition);
  const filename = (match && match[1] ? match[1].replace(/['"]/g, '') : '') || 'SupplierAging.pdf';
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
  sraDesRenderResult('<div class="pd-hint">已导出当前页为分页中文 PDF，请查看下载。</div>');
}

/* 导出当前预览页为分页中文 PDF（只读）：复用当前字段 / 工作台筛选 / 分页组装有界请求后 POST 导出；
   未知到期日 / 未知剩余 / 未知 / 无效分配证据显式保留、不同币种分别成行、宽列集跨页拆分、无跨币种总额；
   空页可见错误，不下载仅表头文件；授权 / 无效 / 字体缺失 / 网络失败均在结果区可见。 */
async function sraDesExportPdf() {
  const view = SRA_DESIGNER.view;
  if (!view || !view.rows || view.rows.length === 0) {
    sraDesRenderResult(sraDesErrorHtml('empty', '当前预览页没有发票证据，无法导出'));
    return;
  }

  const state = sraDesBuildState(view.page || 1);
  if (state.invoiceDateFrom && state.invoiceDateTo && state.invoiceDateFrom > state.invoiceDateTo) {
    sraDesRenderResult(sraDesErrorHtml('invalid', '开票日期开始不能晚于结束日期'));
    return;
  }
  if (state.dueDateFrom && state.dueDateTo && state.dueDateFrom > state.dueDateTo) {
    sraDesRenderResult(sraDesErrorHtml('invalid', '到期日开始不能晚于结束日期'));
    return;
  }

  const req = sraDesBuildRequest(state);
  sraDesRenderResult(sraDesLoadingHtml());
  try {
    await sraDesDownloadPdf(SRA_DESIGNER_API + '/pdf', req);
  } catch (err) {
    sraDesRenderResult(sraDesErrorHtml((err && err.kind) || 'network', (err && err.message) || '无法连接到服务器'));
  }
}

/* Node 单测导出（浏览器中 module 为 undefined，自动跳过） */
if (typeof module !== 'undefined' && module.exports) {
  module.exports = {
    SRA_DESIGNER_API,
    SRA_GROUP_KEYS,
    SRA_SUMMARY_MODES,
    sraDesEsc,
    sraDesSelectFields,
    sraDesGroupKey,
    sraDesSummaryMode,
    sraDesBuildRequest,
    sraDesCellText,
    sraDesRenderCell,
    sraDesCsvCell,
    sraDesCsv,
    sraDesTableHtml,
    sraDesEmptyHtml,
    sraDesErrorHtml,
    sraDesGroupSelectHtml,
    sraDesGroupChartHtml,
    sraDesSummarySelectHtml,
    sraDesSummaryHtml,
    sraDesMoney,
    sraDesInt,
    sraDesResultHtml,
    sraDesFieldChooserHtml,
    sraDesKindOfCode,
    sraDesErrorModalHtml,
    sraDesPreview,
    sraDesPage,
    sraDesExport,
    sraDesDownloadXlsx,
    sraDesExportXlsx,
    sraDesDownloadPdf,
    sraDesExportPdf,
  };
}