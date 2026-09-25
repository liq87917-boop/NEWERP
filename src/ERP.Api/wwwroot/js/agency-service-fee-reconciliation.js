/* ============ 代理服务费对账与账龄工作台（ERP-072：只读派生；证据来自 ERP-070 对账单 + ERP-071 收款分摊行） ============

   口径与后端 AgencyServiceFeeReconciliationRules / AgencyServiceFeeReconciliationService 一一对应：
   - 算术剩余证据 = 对账单合计（服务端按已校验行计算的持久化 TotalAmount）− **有效**分摊合计
     （未作废 + 对账单仍已登记 + 收款单仍可读未取消 + 快照客户 / 币种一致）；
     有效已分摊超过对账单合计时按「无效证据」显示，绝不轧为 0、也不视为已结清；
   - 账龄只对**登记了显式到期日**的对账单计算，并以页面显式 as-of 日期为准：
     未到期 / 逾期 1~30 / 31~60 / 61~90 / 90 天以上（互斥且完整）；未登记到期日的对账单进入独立的
     「未知到期日」分组（不计算账龄、不并入任何账龄桶，绝不按客户账期或对账日期推算）；
   - 不同币种分别成行、绝不合并、绝不换算：本页与导出都没有任何跨币种总额；
   - 草稿 / 已作废对账单单独标注且不参与有效对账与账龄合计；已作废 / 无效分摊证据只作历史列可见；
   - 本页是仓库对账口径的只读证据视图：不是总账或应收余额、不是法定客户对账单、不是付款通知或催款、
     不是收入确认、不是税务判断、不是结算确认，也不得据以收款、开票或核销。
   数据全部走既有只读接口 GET /api/agency-service-fee-reconciliation；明细走
   GET /api/agency-service-fee-reconciliation/statements/{id}/detail（打开时后端重新校验身份与既有模块授权，
   未授权 / 来源已删除一律 fail closed，界面只显示拒绝原因、不显示任何证据）。 */

const ASFR_API = '/api/agency-service-fee-reconciliation';
let ASFR_DATA = null;

/* 账龄分桶中文（与后端 AgencyServiceFeeReconciliationRules.BucketText 同口径，仅作兜底显示） */
const ASFR_BUCKET_LABELS = {
  not_due: '未到期（as-of ≤ 显式到期日）',
  overdue_1_30: '逾期 1 ~ 30 天',
  overdue_31_60: '逾期 31 ~ 60 天',
  overdue_61_90: '逾期 61 ~ 90 天',
  overdue_over_90: '逾期 90 天以上',
  unknown_due_date: '未知到期日（不计算账龄，单独成组）',
};

/* 分配状态中文（与后端 AllocationStateText 同口径，仅作兜底显示） */
const ASFR_ALLOCATION_LABELS = {
  none: '无持久化分摊行（证据缺口）',
  historical_only: '仅有历史 / 无效分摊行',
  partial: '部分分摊',
  full: '全额分摊',
  over_allocated: '无效证据：超过对账单合计',
  unknown: '未知（命中读取上限）',
};

/* 工具栏 / 客户行操作入口：渲染独立工作台页（只读，不落库）。
   可选 customerId（客户行的行操作会传入）：加载客户下拉后预筛选该客户。 */
async function openAgencyServiceFeeReconciliationWorkspace(customerId) {
  CURRENT_PAGE_CODE = 'agency-service-fee-reconciliation';
  document.getElementById('header-title').textContent = '代理服务费对账与账龄工作台';
  document.getElementById('content').innerHTML = `
    <div class="page-hero report-hero">
      <h2>🧮 代理服务费对账与账龄工作台</h2>
      <p>按客户 + 币种对账：对账单合计证据 / ERP-071 有效收款分摊 / 算术剩余证据（只读派生，未知不推断）</p>
    </div>

    <div class="pd-hint" style="margin-bottom:10px">
      ⚠️ <b>仓库对账口径的只读证据视图</b>：<b>不是</b>总账或应收账款余额、<b>不是</b>法定客户对账单、
      <b>不是</b>付款通知或催款函、<b>不是</b>收款授权、<b>不是</b>收入确认、<b>不是</b>税务申报、
      <b>不是</b>结算确认 —— 「算术剩余证据」是算术派生值，不得当作欠款金额或据以收款 / 开票；
      账龄只按显式到期日与显式 as-of 日期计算，未知到期日单独成组。
    </div>

    <div class="toolbar">
      <div class="toolbar-left" style="flex-wrap:wrap;gap:8px;align-items:center">
        <label>客户 <select id="asfr-customer" style="min-width:180px"><option value="">全部客户</option></select></label>
        <label>币种 <select id="asfr-currency" style="min-width:110px"><option value="">全部币种</option></select></label>
        <label>对账单状态 <select id="asfr-statement-status" style="min-width:170px">
          <option value="recorded">仅已登记证据（默认）</option>
          <option value="draft">仅草稿</option>
          <option value="voided">仅已作废历史证据</option>
          <option value="all">全部状态</option>
        </select></label>
        <label>分配状态 <select id="asfr-allocation-state" style="min-width:170px">
          <option value="">全部分配状态</option>
          <option value="none">无持久化分摊行</option>
          <option value="historical_only">仅有历史 / 无效分摊行</option>
          <option value="partial">部分分摊</option>
          <option value="full">全额分摊</option>
        </select></label>
        <label>服务来源 <select id="asfr-source-type" style="min-width:130px">
          <option value="">全部来源</option>
          <option value="sales-order">销售订单</option>
          <option value="loading-list">装柜清单</option>
        </select></label>
        <label>协议 Id <input type="number" id="asfr-agreement" style="width:90px" placeholder="持久化 Id"></label>
        <label>对账单 Id <input type="number" id="asfr-statement" style="width:90px" placeholder="持久化 Id"></label>
        <label>对账日期 <input type="date" id="asfr-date-from" style="width:140px"> 至
          <input type="date" id="asfr-date-to" style="width:140px"></label>
        <label>到期日 <input type="date" id="asfr-due-from" style="width:140px"> 至
          <input type="date" id="asfr-due-to" style="width:140px"></label>
        <label>账龄基准日 <input type="date" id="asfr-as-of" style="width:140px"></label>
        <label>关键字 <input type="text" id="asfr-keyword" style="width:190px" placeholder="对账单号 / 客户 / 协议号 / 备注"></label>
        <label>每页 <input type="number" id="asfr-pagesize" value="50" min="1" max="200" style="width:70px"></label>
      </div>
      <div class="toolbar-actions">
        <button class="btn btn-primary" onclick="loadAgencyServiceFeeReconciliation(1)">查询</button>
        <button class="btn btn-neutral" onclick="exportAgencyServiceFeeReconciliationCsv()" title="导出本页（筛选、币种与未知到期日语义与屏幕完全一致，无跨币种总额）">📤 导出 CSV</button>
      </div>
    </div>

    <div class="pd-hint" style="margin-bottom:10px">
      到期日筛选<b>只命中登记了显式到期日</b>的对账单：未登记到期日的对账单没有到期日可筛（账龄不计算、单独成组）。
    </div>

    <div class="kpi-grid" id="asfr-kpi"></div>
    <div class="table-wrap" id="asfr-currency-table"></div>
    <div class="table-wrap" id="asfr-aging-table"></div>
    <div class="table-wrap" id="asfr-unknown-table"></div>
    <div class="table-wrap" id="asfr-statement-table">
      <div class="skeleton-row"></div><div class="skeleton-row"></div>
    </div>
    <div class="pd-hint" id="asfr-rule"></div>
    <div class="pagination" id="asfr-pagination"></div>`;

  asfrLoadCurrencies();
  await asfrLoadCustomers(customerId);
  await loadAgencyServiceFeeReconciliation(1);
}

/* 币种下拉：与单据币种同一枚举口径（枚举名），不同币种绝不合并汇总 */
function asfrLoadCurrencies() {
  const sel = document.getElementById('asfr-currency');
  if (!sel) return;
  (typeof CURRENCY_NAME_OPTS !== 'undefined' ? CURRENCY_NAME_OPTS : []).forEach(o => {
    const opt = document.createElement('option');
    opt.value = o.value;
    opt.textContent = o.label;
    sel.appendChild(opt);
  });
}

/* 客户下拉：既有基础资料接口；失败不阻断工作台（仍可留空或只用其它筛选） */
async function asfrLoadCustomers(customerId) {
  try {
    const res = await api('/api/base/customers?page=1&pageSize=500');
    const items = (res && res.items) ? res.items : (Array.isArray(res) ? res : []);
    const sel = document.getElementById('asfr-customer');
    if (!sel) return;
    items.forEach(c => {
      const opt = document.createElement('option');
      opt.value = c.id;
      opt.textContent = `${c.customerCode || ''} ${c.customerName || ''}`.trim() || ('客户 ' + c.id);
      sel.appendChild(opt);
    });
    if (customerId && String(customerId) !== '0') sel.value = String(customerId);
  } catch (e) { /* 忽略：客户下拉失败不影响报表查询 */ }
}

function asfrVal(id) {
  const el = document.getElementById(id);
  return el ? String(el.value || '').trim() : '';
}

/* 金额：null / undefined = 未知，绝不显示为 0 */
function asfrMoney(v, decimals) {
  if (v === null || v === undefined) return '未知';
  const digits = (decimals === null || decimals === undefined) ? 2 : Number(decimals);
  return Number(v).toFixed(digits);
}

/* 计数：null / undefined = 未知，绝不显示为 0 */
function asfrInt(v) {
  return (v === null || v === undefined) ? '未知' : String(v);
}

/* 当前筛选（与后端 AgencyServiceFeeReconciliationQuery 同名参数；导出复用同一份参数） */
function asfrQuery(page) {
  const q = new URLSearchParams();
  if (asfrVal('asfr-customer')) q.set('customerId', asfrVal('asfr-customer'));
  if (asfrVal('asfr-currency')) q.set('currency', asfrVal('asfr-currency'));
  if (asfrVal('asfr-statement-status')) q.set('statementStatus', asfrVal('asfr-statement-status'));
  if (asfrVal('asfr-allocation-state')) q.set('allocationState', asfrVal('asfr-allocation-state'));
  if (asfrVal('asfr-source-type')) q.set('sourceType', asfrVal('asfr-source-type'));
  if (asfrVal('asfr-agreement')) q.set('agreementId', asfrVal('asfr-agreement'));
  if (asfrVal('asfr-statement')) q.set('statementId', asfrVal('asfr-statement'));
  if (asfrVal('asfr-date-from')) q.set('statementDateFrom', asfrVal('asfr-date-from'));
  if (asfrVal('asfr-date-to')) q.set('statementDateTo', asfrVal('asfr-date-to'));
  if (asfrVal('asfr-due-from')) q.set('dueDateFrom', asfrVal('asfr-due-from'));
  if (asfrVal('asfr-due-to')) q.set('dueDateTo', asfrVal('asfr-due-to'));
  if (asfrVal('asfr-as-of')) q.set('asOfDate', asfrVal('asfr-as-of'));
  if (asfrVal('asfr-keyword')) q.set('keyword', asfrVal('asfr-keyword'));
  if (asfrVal('asfr-pagesize')) q.set('pageSize', asfrVal('asfr-pagesize'));
  q.set('page', page || 1);
  return q;
}

/* 加载本页（只读；所有金额、分桶与汇总都由后端派生，界面只做展示） */
async function loadAgencyServiceFeeReconciliation(page) {
  try {
    const data = await api(`${ASFR_API}?${asfrQuery(page).toString()}`);
    ASFR_DATA = data;
    asfrRenderKpi(data);
    asfrRenderCurrencyTable(data);
    asfrRenderAgingTable(data);
    asfrRenderUnknownDueTable(data);
    asfrRenderStatementTable(data);
    asfrRenderRules(data);
    asfrRenderPagination(data);
  } catch (err) {
    toast('对账与账龄数据加载失败：' + (err.message || ''), 'error');
  }
}

function asfrRenderKpi(data) {
  const el = document.getElementById('asfr-kpi');
  if (!el) return;
  const card = (label, value, sub) =>
    `<div class="kpi-card"><div class="kpi-label">${escapeHtml(label)}</div>`
    + `<div class="kpi-value">${value}</div><div class="text-muted">${escapeHtml(sub || '')}</div></div>`;

  el.innerHTML =
    card('本页 / 全部对账单', `${data.pageStatementCount || 0} / ${data.total || 0}`, `账龄基准日：${data.asOfDateText || ''}`)
    + card('有效证据（已登记）', String(data.activeEvidencePageStatementCount || 0),
      `草稿 ${data.draftPageStatementCount || 0} / 已作废 ${data.voidedPageStatementCount || 0}（不计入有效合计）`)
    + card('有 / 未知 到期日', `${data.knownDueDatePageStatementCount || 0} / ${data.unknownDueDatePageStatementCount || 0}`,
      '未知到期日不计算账龄、单独成组')
    + card('无分摊 / 仅历史', `${data.noAllocationPageStatementCount || 0} / ${data.historicalOnlyPageStatementCount || 0}`,
      '证据缺口 ≠ 未付款；历史证据绝不并入有效合计')
    + card('无效 / 超额证据', `${data.invalidEvidencePageStatementCount || 0} / ${data.overAllocatedPageStatementCount || 0}`,
      '保留可见，绝不修复、改派或轧为 0');
}

/* 币种汇总：每币种一行；**没有**任何跨币种总额行 */
function asfrRenderCurrencyTable(data) {
  const el = document.getElementById('asfr-currency-table');
  if (!el) return;
  const rows = (data.currencies || []).map(c => `<tr>
      <td><b>${escapeHtml(c.currency || '')}</b></td>
      <td class="text-right">${c.statementCount || 0}</td>
      <td class="text-right">${c.activeEvidenceStatementCount || 0}</td>
      <td class="text-right">${c.draftStatementCount || 0} / ${c.voidedStatementCount || 0}</td>
      <td class="text-right">${asfrMoney(c.statementAmount, c.amountDecimals)}</td>
      <td class="text-right">${asfrMoney(c.activeAllocatedAmount, c.amountDecimals)}</td>
      <td class="text-right">${asfrMoney(c.remainingAmount, c.amountDecimals)}</td>
      <td class="text-right">${asfrInt(c.unknownRemainingStatementCount)} / ${asfrInt(c.overAllocatedStatementCount)}</td>
    </tr>`).join('');
  el.innerHTML = `<table>
    <thead><tr>
      <th>币种</th><th class="text-right">本页对账单张数</th><th class="text-right">有效证据张数</th>
      <th class="text-right">草稿 / 已作废</th><th class="text-right">对账单合计证据</th>
      <th class="text-right">有效已分摊（ERP-071）</th><th class="text-right">算术剩余证据</th>
      <th class="text-right">剩余未知 / 无效（超额）</th>
    </tr></thead>
    <tbody>${rows || '<tr><td colspan="8" class="text-muted">本页没有对账单证据</td></tr>'}</tbody></table>
    <div class="pd-hint">不同币种绝不相加、合并或换算：本表每行一个币种，系统不提供任何跨币种总额。</div>`;
}

/* 账龄分桶表：每币种 × 五个互斥桶（未到期 / 1~30 / 31~60 / 61~90 / >90）；未知到期日不在此表 */
function asfrRenderAgingTable(data) {
  const el = document.getElementById('asfr-aging-table');
  if (!el) return;
  const rows = [];
  (data.currencies || []).forEach(c => (c.buckets || []).forEach(b => {
    rows.push(`<tr>
      <td><b>${escapeHtml(c.currency || '')}</b></td>
      <td>${escapeHtml(ASFR_BUCKET_LABELS[b.bucket] || b.bucketText || b.bucket || '')}</td>
      <td class="text-right">${b.statementCount || 0}</td>
      <td class="text-right">${asfrMoney(b.statementAmount, c.amountDecimals)}</td>
      <td class="text-right">${asfrMoney(b.activeAllocatedAmount, c.amountDecimals)}</td>
      <td class="text-right">${asfrMoney(b.remainingAmount, c.amountDecimals)}</td>
      <td class="text-right">${asfrInt(b.unknownRemainingStatementCount)}</td>
    </tr>`);
  }));
  el.innerHTML = `<table>
    <thead><tr>
      <th>币种</th><th>账龄分桶（互斥，只对有显式到期日的对账单）</th><th class="text-right">对账单张数</th>
      <th class="text-right">对账单合计证据</th><th class="text-right">有效已分摊</th>
      <th class="text-right">算术剩余证据</th><th class="text-right">剩余未知张数</th>
    </tr></thead>
    <tbody>${rows.join('') || '<tr><td colspan="7" class="text-muted">本页没有带显式到期日的对账单证据</td></tr>'}</tbody></table>
    <div class="pd-hint">
      账龄只按显式到期日与页面 as-of 日期计算（未到期 = as-of ≤ 到期日；逾期天数 = as-of − 到期日）；
      边界取含：30 / 60 / 90 天归入本桶，次日起归入下一桶；未登记到期日的对账单不计算账龄、不并入任何桶。
    </div>`;
}

/* 未知到期日独立分组（账龄不计算；仍按客户 + 币种隔离） */
function asfrRenderUnknownDueTable(data) {
  const el = document.getElementById('asfr-unknown-table');
  if (!el) return;
  const rows = (data.unknownDueDateGroups || []).map(g => `<tr>
      <td>${escapeHtml(g.customerName || '')}${g.customerCode ? `（${escapeHtml(g.customerCode)}）` : ''}</td>
      <td><b>${escapeHtml(g.currency || '')}</b></td>
      <td class="text-right">${g.statementCount || 0}</td>
      <td class="text-right">${asfrMoney(g.statementAmount, g.amountDecimals)}</td>
      <td class="text-right">${asfrMoney(g.activeAllocatedAmount, g.amountDecimals)}</td>
      <td class="text-right">${asfrMoney(g.remainingAmount, g.amountDecimals)}</td>
      <td>${escapeHtml((g.statementIdentities || []).join('；'))}</td>
    </tr>`).join('');
  el.innerHTML = `<table>
    <thead><tr>
      <th>客户</th><th>币种</th><th class="text-right">对账单张数</th>
      <th class="text-right">对账单合计证据</th><th class="text-right">有效已分摊</th>
      <th class="text-right">算术剩余证据</th><th>对账单身份（有界：本次返回页内）</th>
    </tr></thead>
    <tbody>${rows || '<tr><td colspan="7" class="text-muted">本页没有未知到期日的对账单</td></tr>'}</tbody></table>
    <div class="pd-hint">
      ⚠️ 独立分组：这些对账单未登记显式到期日，账龄<b>不计算</b>；系统不按客户账期、协议文字或对账日期推算到期日，
      也不把它们当作当天到期或已逾期。
    </div>`;
}

/* 对账单行（证据三类分列：合计 / 有效已分摊 / 算术剩余；历史证据单独成列） */
function asfrStatementRow(r) {
  const statusHtml = r.isDraft
    ? '<span class="status status-info">草稿（不计入有效合计）</span>'
    : (r.isVoided
      ? '<span class="status status-neutral">已作废（不计入有效合计）</span>'
      : '<span class="status status-success">已登记（计入有效合计）</span>');
  const allocHtml = r.allocationState === 'none'
    ? '<span class="status status-warning">证据缺口</span>'
    : ((r.allocationState === 'over_allocated' || r.linkState === 'unavailable')
      ? '<span class="status status-danger">无效 / 无法确认</span>'
      : `<span class="text-muted">${escapeHtml(ASFR_ALLOCATION_LABELS[r.allocationState] || r.allocationStateText || '')}</span>`);
  const dueHtml = r.dueDateKnown
    ? `${escapeHtml(fmtDate(r.dueDate))}<div class="text-muted">${escapeHtml(r.agingBucketText || '')}</div>`
    : `<span class="text-muted">${escapeHtml(r.dueDateText || '未登记（到期日未知：不推算）')}</span>`;

  return `<tr>
      <td>${escapeHtml(r.identityText || '')}<div class="text-muted">${escapeHtml(fmtDate(r.statementDate))} · ${escapeHtml(r.currency || '')}</div></td>
      <td>${escapeHtml(r.customerName || '')}${r.customerCode ? `（${escapeHtml(r.customerCode)}）` : ''}
        <div class="text-muted">${escapeHtml(r.customerAvailabilityText || '')}</div></td>
      <td>${statusHtml}<div class="text-muted">${escapeHtml(r.servicePeriodText || '')}</div></td>
      <td>${dueHtml}</td>
      <td class="text-right">${asfrMoney(r.statementAmount, r.amountDecimals)}</td>
      <td class="text-right">${asfrMoney(r.activeAllocatedAmount, r.amountDecimals)}
        <div class="text-muted">${asfrInt(r.activeAllocationCount)} 条 / ${asfrInt(r.activeReceiptCount)} 张收款单</div></td>
      <td class="text-right">${asfrMoney(r.remainingAmount, r.amountDecimals)}
        <div class="text-muted">${escapeHtml(r.remainingStateText || '')}</div></td>
      <td>${allocHtml}<div class="text-muted">${escapeHtml(r.historicalEvidenceText || '')}</div></td>
      <td>${escapeHtml(r.sourceSummaryText || '')}<div class="text-muted">${escapeHtml((r.sourceIdentities || []).join('；'))}</div></td>
      <td><button class="btn btn-neutral btn-sm" onclick="showAgencyServiceFeeReconciliationDetail(${r.statementId})">明细</button></td>
    </tr>`;
}

/* 对账单级证据表（按客户 + 币种分组；金额只按原币汇总，绝无跨币种总额） */
function asfrRenderStatementTable(data) {
  const el = document.getElementById('asfr-statement-table');
  if (!el) return;
  const body = (data.groups || []).map(g => `
    <tr class="group-title"><td colspan="10">
      <b>${escapeHtml(g.customerName || '')}</b>${g.customerCode ? `（${escapeHtml(g.customerCode)}）` : ''}
      · 币种 <b>${escapeHtml(g.currency || '')}</b>
      —— 有效证据 ${g.activeEvidenceStatementCount || 0} 张（草稿 ${g.draftStatementCount || 0} / 已作废 ${g.voidedStatementCount || 0}）；
      合计 ${asfrMoney(g.statementAmount, g.amountDecimals)}，有效已分摊 ${asfrMoney(g.activeAllocatedAmount, g.amountDecimals)}，
      算术剩余 ${asfrMoney(g.remainingAmount, g.amountDecimals)}
      <div class="text-muted">${escapeHtml(g.customerAvailabilityText || '')}</div>
    </td></tr>
    ${(g.statements || []).map(asfrStatementRow).join('')}`).join('');

  el.innerHTML = `<table>
    <thead><tr>
      <th>对账单 / 对账日期 / 币种</th><th>客户</th><th>状态 / 服务期间</th><th>到期日 / 账龄</th>
      <th class="text-right">对账单合计证据</th><th class="text-right">有效已分摊（ERP-071）</th>
      <th class="text-right">算术剩余证据</th><th>分配状态 / 历史证据</th><th>服务来源</th><th>操作</th>
    </tr></thead>
    <tbody>${body || '<tr><td colspan="10" class="text-muted">本页没有对账单证据</td></tr>'}</tbody></table>
    <div class="pd-hint">
      三类金额严格分列、互不轧差；「算术剩余证据」是仓库内算术派生值，<b>不是</b>应收余额、欠款金额、付款通知
      或收入确认，也不得据以收款 / 开票 / 核销。
    </div>`;
}

function asfrRenderRules(data) {
  const el = document.getElementById('asfr-rule');
  if (!el) return;
  el.innerHTML = `
    对账口径：${escapeHtml(data.ruleText || '')}<br>
    账龄口径：${escapeHtml(data.agingRuleText || '')}<br>
    币种隔离：${escapeHtml(data.noCrossCurrencyText || '')}<br>
    范围：${escapeHtml(data.scopeText || '')}<br>
    边界：${escapeHtml(data.boundaryText || '')}<br>
    历史证据：${escapeHtml(data.historicalEvidenceText || '')}<br>
    fail closed：${escapeHtml(data.sourceFailClosedText || '')}<br>
    导出：${escapeHtml(data.exportNote || '')}<br>
    只读：${escapeHtml(data.readOnlyText || '')}`;
}

function asfrRenderPagination(data) {
  const el = document.getElementById('asfr-pagination');
  if (!el) return;
  const page = data.page || 1;
  const totalPages = data.totalPages || 1;
  el.innerHTML = `<button class="btn btn-neutral btn-sm" ${page <= 1 ? 'disabled' : ''} onclick="loadAgencyServiceFeeReconciliation(${page - 1})">上一页</button>
    <span style="margin:0 10px">第 ${page} / ${totalPages} 页（共 ${data.total || 0} 张对账单）</span>
    <button class="btn btn-neutral btn-sm" ${page >= totalPages ? 'disabled' : ''} onclick="loadAgencyServiceFeeReconciliation(${page + 1})">下一页</button>`;
}

/* 导出本页 CSV：与屏幕同一筛选 / 分页 / 币种隔离 / 未知到期日语义；
   **不写任何跨币种总额**，也不写「应收 / 欠款 / 已对账 / 逾期确认」等法律或账务断言 */
function exportAgencyServiceFeeReconciliationCsv() {
  const data = ASFR_DATA;
  if (!data) { toast('暂无可导出数据', 'warning'); return; }

  const rows = [];
  const money = (v) => (v === null || v === undefined) ? '未知' : String(v);
  const push = (cells) => rows.push(
    cells.map(c => `"${String(c === null || c === undefined ? '' : c).replace(/"/g, '""')}"`).join(','));

  push(['代理服务费对账与账龄工作台（只读派生；按币种分别成行，无跨币种总额）']);
  push(['账龄基准日（as-of）', data.asOfDateText || '']);
  push(['对账单状态', data.statementStatusText || '']);
  push(['分配状态筛选', data.allocationStateText || '']);
  push(['服务来源筛选', data.sourceTypeText || '']);
  push(['本页 / 总页', `${data.page || 1} / ${data.totalPages || 1}`, '符合筛选的对账单总数', data.total || 0]);
  push([]);

  push(['一、账龄分桶（只统计已登记且登记了显式到期日的对账单；未知到期日不计算账龄、不并入任何桶）']);
  push(['币种', '分桶', '对账单张数', '对账单合计证据', '有效已分摊', '算术剩余证据', '剩余未知张数']);
  (data.currencies || []).forEach(c => (c.buckets || []).forEach(b => push([
    c.currency, ASFR_BUCKET_LABELS[b.bucket] || b.bucketText || b.bucket,
    b.statementCount || 0, money(b.statementAmount), money(b.activeAllocatedAmount),
    money(b.remainingAmount), b.unknownRemainingStatementCount || 0,
  ])));
  push([]);

  push(['二、未知到期日独立分组（账龄不计算；绝不并入任何账龄桶）']);
  push(['客户', '币种', '对账单张数', '对账单合计证据', '有效已分摊', '算术剩余证据', '对账单身份']);
  (data.unknownDueDateGroups || []).forEach(g => push([
    g.customerName, g.currency, g.statementCount || 0, money(g.statementAmount),
    money(g.activeAllocatedAmount), money(g.remainingAmount), (g.statementIdentities || []).join('；'),
  ]));
  push([]);

  push(['三、对账单级证据（按客户 + 币种；三类金额严格分列，草稿 / 已作废不计入有效合计）']);
  push(['客户', '币种', '对账单身份', '对账日期', '服务期间', '到期日', '账龄分桶', '逾期天数',
    '对账单合计证据', '有效已分摊', '算术剩余证据', '对账单状态', '分配状态', '历史 / 无效证据', '服务来源', '说明']);
  (data.groups || []).forEach(g => (g.statements || []).forEach(r => push([
    r.customerName, r.currency, r.identityText, fmtDate(r.statementDate), r.servicePeriodText,
    r.dueDateKnown ? fmtDate(r.dueDate) : '未知', r.agingBucketText,
    (r.overdueDays === null || r.overdueDays === undefined) ? '未知' : r.overdueDays,
    money(r.statementAmount), money(r.activeAllocatedAmount), money(r.remainingAmount),
    r.statementStatusText, ASFR_ALLOCATION_LABELS[r.allocationState] || r.allocationStateText,
    r.historicalEvidenceText, r.sourceSummaryText, r.note,
  ])));

  const csv = '\uFEFF' + rows.join('\n');
  const blob = new Blob([csv], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = '代理服务费对账与账龄工作台.csv';
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
  toast('CSV 已导出（与屏幕同一筛选与币种隔离语义，无跨币种总额）', 'success');
}

/* 行操作：打开单张对账单的对账证据明细。
   后端会重新校验登录身份与既有「角色 → 菜单」模块授权，并重新读取权威来源：
   未认证 / 未授权 / 对账单不存在或已删除时一律拒绝（fail closed）—— 此时界面只显示拒绝原因，不显示任何证据。 */
async function showAgencyServiceFeeReconciliationDetail(statementId) {
  const modal = document.getElementById('modal');
  modal.innerHTML = '<div style="padding:40px;text-align:center;color:#64748b">正在加载对账证据明细…</div>';
  modal.style.display = 'block';

  const asOf = asfrVal('asfr-as-of');
  const url = `${ASFR_API}/statements/${statementId}/detail`
    + (asOf ? `?asOfDate=${encodeURIComponent(asOf)}` : '');

  try {
    const data = await api(url);
    const r = data.row || {};
    const allocationRows = (data.allocations || []).map(a => `<tr>
      <td>${escapeHtml(a.receiptNo || '')}<div class="text-muted">Id ${a.receiptId}</div></td>
      <td>${escapeHtml(fmtDate(a.receiptDate))}<div class="text-muted">${escapeHtml(a.receiptStatusText || '')}</div></td>
      <td class="text-right">${escapeHtml(a.amountText || asfrMoney(a.allocatedAmount))}</td>
      <td>${escapeHtml(a.statusText || '')}<div class="text-muted">${escapeHtml(a.effectivenessText || '')}</div></td>
      <td>${escapeHtml(fmtDate(a.allocatedAt))} · ${escapeHtml(a.allocatedBy || '')}
        ${a.voidedAt ? `<div class="text-muted">作废 ${escapeHtml(fmtDate(a.voidedAt))}：${escapeHtml(a.voidReason || '')}</div>` : ''}</td>
    </tr>`).join('');

    modal.innerHTML = `<div class="modal modal-lg" style="width:1180px;max-width:97vw;max-height:92vh;overflow:auto">
      <h3>🧮 对账单对账与账龄证据明细：${escapeHtml(r.identityText || '')}</h3>
      <div class="pd-hint">只读派生证据（三类金额严格分列、绝不轧差）。${escapeHtml(data.boundaryText || '')}</div>
      <div class="form-grid" style="grid-template-columns:repeat(4,1fr)">
        <div><b>客户</b>：${escapeHtml(r.customerName || '')}（${escapeHtml(r.customerCode || '')}）<div class="text-muted">${escapeHtml(r.customerAvailabilityText || '')}</div></div>
        <div><b>币种</b>：${escapeHtml(r.currency || '')}</div>
        <div><b>对账日期</b>：${escapeHtml(fmtDate(r.statementDate))}</div>
        <div><b>服务期间</b>：${escapeHtml(r.servicePeriodText || '')}</div>
        <div><b>对账单状态</b>：${escapeHtml(r.statementStatusText || '')}</div>
        <div><b>到期日（显式证据）</b>：${escapeHtml(r.dueDateKnown ? fmtDate(r.dueDate) : '未知')}<div class="text-muted">${escapeHtml(r.dueDateText || '')}</div></div>
        <div><b>账龄（as-of ${escapeHtml(fmtDate(data.asOfDate))}）</b>：${escapeHtml(r.agingBucketText || '')}<div class="text-muted">${escapeHtml(r.agingText || '')}</div></div>
        <div><b>协议</b>：${escapeHtml(r.agreementNo || '')}<div class="text-muted">${escapeHtml(r.agreementAvailabilityText || '')}</div></div>
        <div><b>对账单合计证据</b>：${asfrMoney(r.statementAmount, r.amountDecimals)}</div>
        <div><b>有效已分摊（ERP-071）</b>：${asfrMoney(r.activeAllocatedAmount, r.amountDecimals)}<div class="text-muted">${asfrInt(r.activeAllocationCount)} 条 / ${asfrInt(r.activeReceiptCount)} 张收款单</div></div>
        <div><b>算术剩余证据</b>：${asfrMoney(r.remainingAmount, r.amountDecimals)}<div class="text-muted">${escapeHtml(r.remainingStateText || '')}</div></div>
        <div><b>分配状态</b>：${escapeHtml(ASFR_ALLOCATION_LABELS[r.allocationState] || r.allocationStateText || '')}</div>
        <div><b>已作废分摊</b>：${asfrInt(r.voidedAllocationCount)} 条 / ${asfrMoney(r.voidedAllocationAmount, r.amountDecimals)}</div>
        <div><b>无效 / 无法确认</b>：${asfrInt(r.invalidAllocationCount)} 条 / ${asfrMoney(r.invalidAllocationAmount, r.amountDecimals)}</div>
        <div><b>服务来源</b>：${escapeHtml(r.sourceSummaryText || '')}<div class="text-muted">${escapeHtml((r.sourceIdentities || []).join('；'))}</div></div>
        <div><b>链接状态</b>：${escapeHtml(r.linkStateText || '')}</div>
      </div>
      <div class="table-wrap" style="margin-top:8px">
        <table>
          <thead><tr><th>收款单</th><th>收款日期 / 状态</th><th class="text-right">分摊金额</th>
            <th>分摊行状态 / 是否计入有效合计</th><th>登记留痕</th></tr></thead>
          <tbody>${allocationRows || '<tr><td colspan="5" class="text-muted">本对账单没有任何持久化分摊行（证据缺口，不代表未付 / 已付）</td></tr>'}</tbody>
        </table>
        <div class="pd-hint">
          ${data.allocationsTruncated
            ? `⚠️ 分摊行清单命中系统有界上限，仅展示前 ${(data.allocations || []).length} 条（上方金额与计数以数据集侧聚合为准，不受展示上限影响）。`
            : '分摊行清单完整展示（含已作废历史）；已作废与无效证据绝不并入有效合计。'}
        </div>
      </div>
      <div class="pd-hint">行说明：${escapeHtml(r.note || '')}</div>
      <div class="pd-hint">对账口径：${escapeHtml(data.ruleText || '')}</div>
      <div class="pd-hint">账龄口径：${escapeHtml(data.agingRuleText || '')}</div>
      <div class="pd-hint">无效 / 无法确认证据：${escapeHtml(data.invalidEvidenceNote || '')}</div>
      <div class="pd-hint">授权复核：${escapeHtml(data.authorizationNote || '')}</div>
      <div class="modal-footer"><button class="btn btn-neutral" onclick="closeModal()">关闭</button></div>
    </div>`;
    modal.style.display = 'flex';
  } catch (err) {
    modal.innerHTML = `<div class="modal" style="width:760px;max-width:95vw">
      <h3>⛔ 明细打开被拒绝（fail closed）</h3>
      <div class="pd-hint">拒绝原因：${escapeHtml(err.message || '')}</div>
      <div class="pd-hint">${escapeHtml((ASFR_DATA && ASFR_DATA.sourceFailClosedText)
        || '未认证 / 未获模块授权 / 来源已删除时一律拒绝，不返回任何部分证据，也不做来源修复或改派。')}</div>
      <div class="modal-footer"><button class="btn btn-neutral" onclick="closeModal()">关闭</button></div>
    </div>`;
    modal.style.display = 'flex';
    toast('明细打开失败（fail closed）：' + (err.message || ''), 'error');
  }
}
