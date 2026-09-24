/* ==================================================================================
   ====== 装柜费用分摊证据（ERP-060）— **只读**呈现 ======
   ==================================================================================
   定位：把 ERP-042 已持久化的**分摊批次**（EAB-…）与**分摊行**按「装柜清单（一柜）→ 币种 → 客户」
         只读呈现：有效批次条数、各客户各币种的分摊金额与比例、分摊方法与基数种类 / 来源、
         未分摊参考（本柜柜级来源费用单里还没有有效批次的那些），以及已作废 / 历史异常批次的历史视图。
   为什么只读（ERP-060 审计结论）：ERP-042 的批次与分摊行已经是分摊证据的唯一权威登记册，
         因此本模块**不新建表、不新增列**，只按显式的装柜清单 Id / 结算单 Id 读取呈现。
   边界（界面侧同样遵守）：
     1. 只调用 GET 接口（装柜清单 / 结算单 /expense-allocation-evidence 与 /api/container/expense-allocation-evidence*），
        不写任何表、不改写装柜清单与明细、参与方、分摊批次 / 分摊行、费用单与结算单；
     2. 不同币种**不合并、不换算**，也不做跨币种合计；折人民币只展示批次留痕里已持久化的来源汇率结果；
     3. 缺失证据显示「无（未登记任何有效分摊批次）」或「未知」：不代表费用为零、已结清、应收、应付；
     4. 已作废 / 历史异常批次不计入有效合计，只在历史视图按原值显示（含作废原因）；
     5. 失效链接（参与方 / 客户 / 柜号）逐行显式标注：既不按客户名或柜号文本改派，也不做任何修复；
     6. 分摊行是操作性成本分配证据：不是会计记账 / 凭证、付款授权、税务处理，也不是结算确认或客户对账单；
        装柜结算单上的金额字段只是持久化原值的只读回显，分摊证据不参与结算金额计算。
   文案与服务端 ContainerExpenseAllocationEvidenceRules 保持一致。
   ================================================================================== */

/* 缺失 / 未知文案（与后端 ContainerExpenseAllocationEvidenceRules 一致） */
const CEA_UNKNOWN = '未知';
const CEA_MISSING = '无（未登记任何有效分摊批次）';

/* 入口模块 → 分摊证据接口（行操作入口：装柜清单 / 装柜结算单） */
const CEA_MODULE_ENDPOINTS = {
  'loading-list': '/api/container/loading-lists',
  'container-settlement': '/api/finance/container-settlements',
};

/* 当前状态：详情 / 工作台 / 单据 Id / 筛选 */
let CEA = {
  view: 'detail', id: '', detail: null, settlement: null, fromWorkspace: false,
  list: [], total: 0, page: 1, pageSize: 20,
  filters: {
    containerNo: '', loadingListNo: '', batchNo: '', currency: '',
    status: '', customerId: '', includeHistory: true, keyword: '',
  },
  hint: '',
};

/* 文案辅助：空白一律显示「未知」（不推断） */
function ceaText(v) {
  const s = String(v === null || v === undefined ? '' : v).trim();
  return s ? escapeHtml(s) : CEA_UNKNOWN;
}

/* 金额：原币 + 币种（0 位小数币种不补小数），金额一律原样展示、界面不重算 */
function ceaAmount(amount, currency, precision) {
  const value = Number(amount === null || amount === undefined ? 0 : amount);
  const digits = Number(precision === 0 ? 0 : 2);
  return `${escapeHtml(value.toFixed(digits))} ${escapeHtml(String(currency || ''))}`;
}

/* 行操作入口：按当前模块（装柜清单 / 装柜结算单）的显式单据 Id 读取分摊证据（只读） */
async function showContainerAllocationEvidence(id) {
  const endpoint = CEA_MODULE_ENDPOINTS[CURRENT_MODULE_CODE];
  if (!endpoint) {
    toast('当前模块不是装柜清单 / 装柜结算单，无法按显式单据查看分摊证据', 'error');
    return;
  }

  CEA = {
    view: 'detail', id: String(id || ''), detail: null, settlement: null, fromWorkspace: CEA.fromWorkspace,
    list: [], total: 0, page: 1, pageSize: 20, filters: CEA.filters,
    hint: '',
  };

  const modal = document.getElementById('modal');
  modal.innerHTML = '<div style="padding:40px;text-align:center;color:#64748b">正在加载分摊证据…</div>';
  modal.style.display = 'flex';

  try {
    const data = await api(`${endpoint}/${id}/expense-allocation-evidence?includeHistory=true`);
    if (endpoint === '/api/finance/container-settlements') {
      CEA.settlement = data;
      CEA.detail = data.evidence || null;
      CEA.hint = data.comparisonText || '';
    } else {
      CEA.detail = data;
      CEA.hint = '';
    }
    ceaRender();
  } catch (e) {
    modal.innerHTML = `<div style="padding:40px;text-align:center;color:#b91c1c">分摊证据加载失败：${escapeHtml(e.message)}</div>`;
  }
}

/* ==================== 详情 / 工作台渲染骨架 ==================== */

function ceaRender() {
  const modal = document.getElementById('modal');
  if (CEA.view === 'list') {
    modal.innerHTML = ceaListHtml();
  } else {
    modal.innerHTML = ceaDetailHtml();
  }
  modal.style.display = 'flex';
}

function ceaShell(title, subtitle, body) {
  const back = CEA.fromWorkspace
    ? '<button class="btn btn-neutral btn-sm" onclick="ceaBackToList()">← 返回工作台</button>' : '';
  return `
    <div style="max-width:1280px;margin:2vh auto;background:#fff;border-radius:14px;padding:16px 18px;max-height:94vh;overflow:auto;box-shadow:0 20px 60px rgba(0,0,0,.25)">
      <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:8px">
        <h3 style="margin:0">${title}
          <span style="font-size:13px;color:#64748b;font-weight:400;margin-left:10px">${subtitle}</span></h3>
        <div style="display:flex;gap:8px">${back}
          <button class="btn btn-neutral btn-sm" onclick="closeModal()">✕ 关闭</button></div>
      </div>
      ${body}
    </div>`;
}

function ceaRow(label, value) {
  return `<tr><th style="width:180px;text-align:left">${label}</th><td>${value}</td></tr>`;
}

function ceaTextBlock(text, color) {
  return `<div style="margin:8px 0;padding:8px 10px;border:1px dashed #cbd5e1;border-radius:10px;font-size:13px;color:${color || '#475569'}">${escapeHtml(text || '')}</div>`;
}


/* ==================== 单据详情（装柜清单 / 装柜结算单） ==================== */

function ceaDetailHtml() {
  const d = CEA.detail;
  const settlement = CEA.settlement;

  const title = settlement ? '🧾 装柜结算单分摊证据（只读）' : '🧾 装柜清单分摊证据（只读）';
  const subtitle = settlement
    ? '结算单金额字段为持久化原值回显；分摊证据不参与结算计算，也不回写结算单'
    : '按装柜清单显式链接读取 ERP-042 分摊批次与分摊行；不同币种不合并、不换算';

  const head = settlement ? ceaSettlementTable(settlement) : ceaLoadingListTable(d);

  if (!settlement && !d) {
    return ceaShell(title, subtitle, ceaTextBlock('分摊证据不可用：未取到证据数据（未知，不推断）。'));
  }

  return ceaShell(title, subtitle, `
    ${head}
    ${(settlement && !d) ? ceaTextBlock(settlement.evidenceUnavailableText || '分摊证据不可用（未知）', '#b45309') : ''}
    ${d ? ceaEvidenceHtml(d) : ''}
    ${ceaHintHtml(d)}
  `);
}

/* 装柜清单信息（只读） */
function ceaLoadingListTable(d) {
  if (!d) return '';
  return `
    <table style="width:100%;margin-bottom:8px;font-size:13px">
      <tbody>
        ${ceaRow('装柜清单', `${ceaText(d.loadingListNo)}（Id=${escapeHtml(String(d.loadingListId || ''))}）`)}
        ${ceaRow('柜号', d.containerNoAvailable ? escapeHtml(d.containerNo) : CEA_UNKNOWN)}
        ${ceaRow('清单可用性', escapeHtml(d.loadingListAvailabilityText || ''))}
        ${ceaRow('证据状态', `<b>${escapeHtml(d.evidenceStatusText || '')}</b>`)}
      </tbody>
    </table>`;
}

/* 结算单字段（只读回显）+ 对照口径 */
function ceaSettlementTable(s) {
  return `
    <table style="width:100%;margin-bottom:8px;font-size:13px">
      <tbody>
        ${ceaRow('结算单号', `${ceaText(s.settlementNo)}（Id=${escapeHtml(String(s.settlementId || ''))}）`)}
        ${ceaRow('结算日期 / 状态', `${escapeHtml(fmtDate(s.settlementDate))} · ${ceaText(s.settlementStatusText)}`)}
        ${ceaRow('客户（结算单客户 Id）', `${ceaText(s.settlementCustomerDisplay)}（Id=${escapeHtml(String(s.settlementCustomerId || ''))}）<div class="text-muted">${escapeHtml(s.settlementCustomerAvailabilityText || '')}</div>`)}
        ${ceaRow('关联装柜清单', s.loadingListId ? `${ceaText(s.loadingListNo)}（Id=${escapeHtml(String(s.loadingListId))}）` : CEA_UNKNOWN)}
        ${ceaRow('结算总金额 / 海运费 / 其他费用', `${escapeHtml(String(s.totalAmount))} · ${escapeHtml(String(s.freightCost))} · ${escapeHtml(String(s.otherCost))}`)}
      </tbody>
    </table>
    ${ceaTextBlock(s.settlementTotalsText || '', '#475569')}
    ${ceaTextBlock(s.comparisonText || '', s.comparisonComparable ? '#0f766e' : '#475569')}`;
}

/* 分摊证据主体（只读） */
function ceaEvidenceHtml(d) {
  return `
    ${ceaSummaryHtml(d)}
    ${ceaGroupsHtml(d)}
    ${ceaBatchesHtml(d)}
    ${ceaUnallocatedHtml(d)}
    ${ceaTextBlock(d.evidenceMissingText || '', '#b45309')}
    ${ceaTextBlock(d.unallocatedContextText || '', '#475569')}
    ${ceaTextBlock(d.legacyAllocationText || '', '#475569')}
    ${ceaTextBlock(d.invalidLinkText || '', d.invalidLinkCount > 0 ? '#b45309' : '#475569')}
    ${ceaTextBlock(d.boundaryText || '', '#475569')}
    ${ceaTextBlock(d.disclaimerText || '', '#b91c1c')}
    ${ceaTextBlock(d.readOnlyText || '', '#0f766e')}`;
}


/* 证据概览（有效批次 / 已作废 / 历史异常 / 有效行 / 客户 / 币种） */
function ceaSummaryHtml(d) {
  return `
    <div style="display:grid;grid-template-columns:repeat(6,1fr);gap:8px;margin:8px 0">
      ${ceaStat('有效分摊批次', d.activeBatchCount)}
      ${ceaStat('已作废批次', d.voidedBatchCount)}
      ${ceaStat('历史异常状态', d.unknownStatusBatchCount)}
      ${ceaStat('有效分摊行', d.activeLineCount)}
      ${ceaStat('有效客户数', d.activeCustomerCount)}
      ${ceaStat('币种数（不合并）', (d.currencies || []).length)}
    </div>
    <div style="font-size:13px;color:#475569">币种：${(d.currencies || []).length ? (d.currencies || []).map(escapeHtml).join('、') : CEA_MISSING}</div>
    ${d.evidenceTruncated ? ceaTextBlock('证据明细超过有界扫描上限：界面已截断显示（合计仍取自批次持久化列，未用截断行重算）', '#b45309') : ''}`;
}

function ceaStat(label, value) {
  return `<div style="border:1px solid #e2e8f0;border-radius:10px;padding:6px 8px">
    <div style="font-size:12px;color:#64748b">${escapeHtml(label)}</div>
    <div style="font-size:16px;font-weight:600">${escapeHtml(String(value === null || value === undefined ? 0 : value))}</div></div>`;
}

/* 按币种分组的客户分摊证据（不同币种永不合并） */
function ceaGroupsHtml(d) {
  const groups = d.groups || [];
  if (!groups.length) {
    return ceaTextBlock(`按币种分组的客户分摊金额：${CEA_MISSING}（未登记任何有效分摊批次）`, '#b45309');
  }

  return groups.map(g => `
    <div style="margin:10px 0;border:1px solid #e2e8f0;border-radius:10px;padding:8px 10px">
      <div style="display:flex;justify-content:space-between;align-items:center">
        <b>${escapeHtml(g.currencyLabel || g.currency)}</b>
        <span style="font-size:13px;color:#475569">有效批次 ${escapeHtml(String(g.activeBatchCount))} · 有效行 ${escapeHtml(String(g.activeLineCount))} · 分摊合计 ${ceaAmount(g.allocatedTotal, g.currency, g.amountPrecision)}（折人民币 ${escapeHtml(String(g.allocatedTotalCny))}）</span>
      </div>
      <div class="text-muted" style="font-size:12px">${escapeHtml(g.totalsText || '')}</div>
      <div class="text-muted" style="font-size:12px">${escapeHtml(g.conversionText || '')}</div>
      <div class="text-muted" style="font-size:12px">来源金额合计 ${ceaAmount(g.sourceTotal, g.currency, g.amountPrecision)} ${g.totalsConsistent ? '（与分摊合计一致）' : '（历史异常：与分摊合计不一致，照实呈现、不修正）'}</div>
      <div class="table-wrap" style="max-height:36vh;overflow:auto;margin-top:6px">
        <table>
          <thead><tr><th>客户</th><th>分摊金额</th><th>比例合计</th><th>行数</th><th>批次</th><th>基数（来源）</th><th>链接可用性</th></tr></thead>
          <tbody>${(g.customers || []).map(ceaCustomerRow).join('')}</tbody>
        </table>
      </div>
      ${g.customerDetailTruncated ? ceaTextBlock('该币种的客户明细超过有界上限被截断（合计仍取自批次持久化列）', '#b45309') : ''}
    </div>`).join('');
}

function ceaCustomerRow(c) {
  const badge = c.customerAvailable ? '' : ` <span class="status status-warning">不可用</span>`;
  const invalid = c.hasInvalidLink
    ? `<div style="color:#b45309">${escapeHtml(c.linkStatusText || '')}</div>` : '';
  return `<tr>
    <td>${ceaText(c.customerDisplay)}（Id=${escapeHtml(String(c.customerId || ''))}）${badge}
      <div class="text-muted">${escapeHtml(c.customerCode || '')} · ${escapeHtml(c.customerAvailabilityText || '')}</div></td>
    <td>${ceaAmount(c.allocatedAmount, c.currency, c.amountPrecision)}
      <div class="text-muted">折人民币 ${escapeHtml(String(c.allocatedAmountCny))}</div></td>
    <td>${escapeHtml(String(c.ratioTotal))}%</td>
    <td>${escapeHtml(String(c.lineCount))}</td>
    <td>${escapeHtml((c.batchNos || []).join('、')) || CEA_UNKNOWN}</td>
    <td>${escapeHtml(c.methodsText || CEA_UNKNOWN)}<div class="text-muted">${escapeHtml(c.basisText || '')}</div></td>
    <td>${invalid || escapeHtml(c.linkStatusText || '')}</td>
  </tr>`;
}


/* 批次明细（有效批次 + 已作废 / 历史异常批次历史视图；逐行留痕可展开） */
function ceaBatchesHtml(d) {
  const active = d.activeBatches || [];
  const history = d.historyBatches || [];

  const activeHtml = active.length
    ? active.map(b => ceaBatchBlock(b, false)).join('')
    : ceaTextBlock(`有效分摊批次明细：${CEA_MISSING}`, '#b45309');

  const historyHtml = history.length
    ? `
      <div style="margin-top:10px">
        <b>已作废 / 历史异常批次（不计入有效合计，只作历史呈现）</b>
        ${d.historyTruncated ? `<div class="text-muted" style="font-size:12px">历史批次共 ${escapeHtml(String(d.historyTotal))} 条，本次按上限 ${escapeHtml(String(d.historyTake))} 条显示（截断）</div>` : ''}
        ${history.map(b => ceaBatchBlock(b, true)).join('')}
      </div>`
    : ceaTextBlock('已作废 / 历史异常批次：无（没有已作废批次）', '#475569');

  return `<div style="margin-top:10px"><b>分摊批次明细（来源 / 方法 / 基数 / 客户）</b>${activeHtml}${historyHtml}</div>`;
}

function ceaBatchBlock(b, history) {
  const badge = history
    ? `<span class="status status-warning">${escapeHtml(b.statusText || '')}</span>`
    : '<span class="status status-info">有效</span>';
  const voided = b.isVoided
    ? `<div class="text-muted" style="font-size:12px">作废时间 ${b.voidedAt ? escapeHtml(String(b.voidedAt)) : CEA_UNKNOWN} · 作废原因：${ceaText(b.voidReason)}</div>`
    : '';
  const linkWarning = b.containerLinkConsistent ? '' : `<div style="color:#b45309;font-size:12px">${escapeHtml(b.containerLinkText || '')}</div>`;

  return `
    <div style="border:1px solid #e2e8f0;border-left:4px solid ${history ? '#f59e0b' : '#0ea5e9'};border-radius:10px;padding:8px 10px;margin-top:8px">
      <div style="display:flex;justify-content:space-between;align-items:center">
        <div><b>${ceaText(b.batchNo)}</b> ${badge}
          <span class="text-muted" style="font-size:12px">来源费用 ${ceaText(b.sourceExpenseNo)} · 清单 ${ceaText(b.loadingListNo)} · 柜号 ${b.containerNo ? escapeHtml(b.containerNo) : CEA_UNKNOWN}</span></div>
        <span style="font-size:13px;color:#475569">${escapeHtml(b.allocationMethod || CEA_UNKNOWN)} / 基数 ${escapeHtml(b.basisKind || CEA_UNKNOWN)} · ${ceaAmount(b.allocatedTotal, b.currency, b.amountPrecision)}</span>
      </div>
      <div class="text-muted" style="font-size:12px">${escapeHtml(b.totalsText || '')}</div>
      <div class="text-muted" style="font-size:12px">客户：${escapeHtml(b.customerSummaryText || '')}</div>
      <div class="text-muted" style="font-size:12px">${escapeHtml(b.remark || '')}</div>
      ${voided}${linkWarning}
      <details style="margin-top:6px">
        <summary style="cursor:pointer;font-size:13px">逐行留痕（${escapeHtml(String(b.storedLineCount))} / ${escapeHtml(String(b.lineCount))} 行）</summary>
        <div class="table-wrap" style="max-height:34vh;overflow:auto">
          <table>
            <thead><tr><th>客户</th><th>分摊金额</th><th>比例</th><th>基数（来源）</th><th>费用单</th><th>链接</th></tr></thead>
            <tbody>${(b.lines || []).map(ceaLineRow).join('') || '<tr><td colspan="6" class="text-muted">无逐行留痕</td></tr>'}</tbody>
          </table>
        </div>
        ${b.linesTruncated ? ceaTextBlock('该批次逐行留痕超过有界上限被截断（合计仍取自批次持久化列）', '#b45309') : ''}
      </details>
    </div>`;
}

function ceaLineRow(l) {
  return `<tr>
    <td>${ceaText(l.customerDisplay)}（Id=${escapeHtml(String(l.customerId || ''))}）${l.isPrimary ? ' <span class="status status-info">主参与方</span>' : ''}</td>
    <td>${ceaAmount(l.allocatedAmount, l.currency, l.amountPrecision)}</td>
    <td>${escapeHtml(String(l.ratio))}%</td>
    <td>${escapeHtml(l.basisKind || CEA_UNKNOWN)} / ${escapeHtml(String(l.basisValue))}<div class="text-muted">${escapeHtml(l.basisEvidenceText || '')}</div></td>
    <td>${ceaText(l.expenseNo)}</td>
    <td>${l.linkInvalid
      ? `<span style="color:#b45309">${escapeHtml(l.linkStatusText || '')}</span>`
      : escapeHtml(l.linkStatusText || '')}</td>
  </tr>`;
}

/* 未分摊参考（只由显式持久化集合比对得出，按币种单列） */
function ceaUnallocatedHtml(d) {
  const rows = d.unallocated || [];
  if (!rows.length) {
    return ceaTextBlock('未分摊参考：无（本柜柜级来源费用单当前均属于有效分摊批次——不代表费用为零或已结清）', '#475569');
  }

  return `
    <div style="margin-top:10px"><b>未分摊参考（本柜柜级来源费用单中不属于任何有效分摊批次的那些）</b>
      <div class="table-wrap" style="max-height:30vh;overflow:auto">
        <table>
          <thead><tr><th>币种</th><th>条数</th><th>金额合计</th><th>其中批次已作废</th><th>费用单号（有界）</th></tr></thead>
          <tbody>${rows.map(u => `<tr>
            <td>${escapeHtml(u.currency || '')}</td>
            <td>${escapeHtml(String(u.sourceExpenseCount))}</td>
            <td>${ceaAmount(u.sourceAmount, u.currency, u.amountPrecision)}</td>
            <td>${escapeHtml(String(u.voidedBatchSourceCount))}</td>
            <td>${escapeHtml((u.sourceExpenseNos || []).join('、'))}${u.truncated ? ' …（截断）' : ''}</td>
          </tr>`).join('')}</tbody>
        </table>
      </div>
      ${rows.map(u => ceaTextBlock(u.text || '', '#475569')).join('')}
    </div>`;
}

function ceaHintHtml(d) {
  if (!CEA.hint) return '';
  return ceaTextBlock(CEA.hint, '#0f766e');
}


/* ==================== 分摊证据工作台（只读、显式字段筛选、分页有界） ==================== */

/* 工具栏入口：装柜清单 / 装柜结算单模块的「🧾 分摊证据工作台」（不依赖当前单据） */
async function openContainerAllocationEvidenceWorkspace() {
  CEA = {
    view: 'list', id: '', detail: null, settlement: null, fromWorkspace: false,
    list: [], total: 0, page: 1, pageSize: 20,
    filters: {
      containerNo: '', loadingListNo: '', batchNo: '', currency: '',
      status: '', customerId: '', includeHistory: true, keyword: '',
    },
    hint: '',
  };

  const modal = document.getElementById('modal');
  modal.innerHTML = '<div style="padding:40px;text-align:center;color:#64748b">正在加载分摊证据工作台…</div>';
  modal.style.display = 'flex';

  await ceaLoadList();
  ceaRender();
}

function ceaSetFilter(key, value) { CEA.filters[key] = value; }

async function ceaLoadList() {
  const f = CEA.filters;
  const params = ['page=' + CEA.page, 'pageSize=' + CEA.pageSize];
  if (f.containerNo) params.push('containerNo=' + encodeURIComponent(f.containerNo));
  if (f.loadingListNo) params.push('loadingListNo=' + encodeURIComponent(f.loadingListNo));
  if (f.batchNo) params.push('batchNo=' + encodeURIComponent(f.batchNo));
  if (f.currency) params.push('currency=' + encodeURIComponent(f.currency));
  if (f.status !== '') params.push('status=' + encodeURIComponent(f.status));
  if (f.customerId) params.push('customerId=' + encodeURIComponent(f.customerId));
  if (f.keyword) params.push('keyword=' + encodeURIComponent(f.keyword));
  params.push('includeHistory=' + (f.includeHistory ? 'true' : 'false'));

  try {
    const page = await api('/api/container/expense-allocation-evidence?' + params.join('&'));
    CEA.list = (page && page.items) || [];
    CEA.total = (page && page.total) || 0;
    CEA.workspaceTexts = page || null;
  } catch (e) {
    CEA.list = [];
    CEA.total = 0;
    CEA.workspaceTexts = null;
    toast('分摊证据工作台加载失败：' + e.message, 'error');
  }
}


function ceaListHtml() {
  const f = CEA.filters;
  const rows = (CEA.list || []).map(ceaRowHtml).join('');
  const body = rows || '<tr><td colspan="8" class="text-muted">没有符合条件的装柜清单（老柜子没有分摊批次时不会凭空出现，也不需要任何回填）</td></tr>';
  const texts = CEA.workspaceTexts || {};

  return ceaShell(
    '🧾 装柜费用分摊证据工作台（只读）',
    `按显式字段筛选：不同币种不合并、不换算；缺失证据显示「${CEA_MISSING}」或「${CEA_UNKNOWN}」`,
    `
    <div style="display:grid;grid-template-columns:repeat(4,1fr);gap:8px;margin-bottom:8px">
      <div><label class="ea-lb">柜号（精确匹配）</label>
        <input style="width:100%" value="${escapeHtml(f.containerNo)}" onchange="ceaSetFilter('containerNo', this.value)"></div>
      <div><label class="ea-lb">装柜清单号（精确匹配）</label>
        <input style="width:100%" value="${escapeHtml(f.loadingListNo)}" onchange="ceaSetFilter('loadingListNo', this.value)"></div>
      <div><label class="ea-lb">分摊批次号（精确匹配）</label>
        <input style="width:100%" value="${escapeHtml(f.batchNo)}" onchange="ceaSetFilter('batchNo', this.value)"></div>
      <div><label class="ea-lb">币种（精确匹配，不合并）</label>
        <input style="width:100%" value="${escapeHtml(f.currency)}" onchange="ceaSetFilter('currency', this.value)"></div>
      <div><label class="ea-lb">批次状态</label>
        <select style="width:100%" onchange="ceaSetFilter('status', this.value)">
          <option value="" ${f.status === '' ? 'selected' : ''}>（全部，含已作废历史）</option>
          <option value="1" ${f.status === '1' ? 'selected' : ''}>有效</option>
          <option value="0" ${f.status === '0' ? 'selected' : ''}>已作废</option>
        </select></div>
      <div><label class="ea-lb">客户 Id（显式 Id，不按名称合并）</label>
        <input style="width:100%" value="${escapeHtml(f.customerId)}" onchange="ceaSetFilter('customerId', this.value)"></div>
      <div><label class="ea-lb">关键字（柜号 / 清单号 / 批次号 / 来源费用单号 / 客户编码·名称）</label>
        <input style="width:100%" value="${escapeHtml(f.keyword)}" onchange="ceaSetFilter('keyword', this.value)"></div>
      <div style="display:flex;align-items:flex-end">
        <label style="font-size:13px;color:#475569">
          <input type="checkbox" ${f.includeHistory ? 'checked' : ''}
            onchange="ceaSetFilter('includeHistory', this.checked)"> 包含已作废 / 历史异常批次</label></div>
      <div style="display:flex;align-items:flex-end;gap:8px">
        <button class="btn btn-primary btn-sm" onclick="ceaSearch()">查询</button>
        <button class="btn btn-neutral btn-sm" onclick="ceaResetFilters()">重置筛选</button>
      </div>
    </div>
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <div style="font-size:13px;color:#475569">共 ${escapeHtml(String(CEA.total))} 个装柜清单（每页 ${escapeHtml(String(CEA.pageSize))} 条；分页有界，汇总按页批量取数、不逐行查库）</div>
      <div style="display:flex;gap:8px">
        <button class="btn btn-neutral btn-sm" onclick="ceaPage(-1)">上一页</button>
        <span style="font-size:13px;color:#475569;line-height:32px">第 ${escapeHtml(String(CEA.page))} 页</span>
        <button class="btn btn-neutral btn-sm" onclick="ceaPage(1)">下一页</button>
      </div>
    </div>
    <div class="table-wrap" style="max-height:52vh;overflow:auto">
      <table>
        <thead><tr><th>装柜清单 / 柜号</th><th>有效分摊（按币种，不合并）</th><th>客户</th><th>基数 / 方法</th>
          <th>批次（有效 / 作废 / 异常）</th><th>证据状态</th><th>链接</th><th>操作</th></tr></thead>
        <tbody>${body}</tbody>
      </table>
    </div>
    ${ceaTextBlock(texts.scanBoundedText || '', '#475569')}
    ${ceaTextBlock(texts.groupingText || '', '#475569')}
    ${ceaTextBlock(texts.unallocatedRuleText || '', '#475569')}
    ${ceaTextBlock(texts.disclaimerText || '', '#b91c1c')}
    ${ceaTextBlock(texts.readOnlyText || '', '#0f766e')}`);
}


function ceaRowHtml(r) {
  const groups = (r.groups || []).map(g =>
    `${escapeHtml(g.currency)} ${escapeHtml(String(g.allocatedTotal))}（${escapeHtml(String(g.activeBatchCount))} 批次）`).join('；');
  const customers = (r.groups || []).map(g => g.customerNamesText).filter(Boolean).join('｜');
  const link = r.invalidLinkCount > 0
    ? `<span style="color:#b45309">${escapeHtml(r.invalidLinkText || '')}</span>`
    : escapeHtml(r.containerLinkText || '');

  return `<tr>
    <td>${ceaText(r.loadingListNo)}<div class="text-muted">柜号 ${r.containerNoAvailable ? escapeHtml(r.containerNo) : CEA_UNKNOWN} · Id=${escapeHtml(String(r.loadingListId))}</div>
      <div class="text-muted">${escapeHtml(r.loadingListAvailabilityText || '')}</div></td>
    <td>${groups || CEA_MISSING}</td>
    <td>${escapeHtml(customers) || CEA_MISSING}
      <div class="text-muted">有效客户 ${escapeHtml(String(r.activeCustomerCount))} 个 / 有效行 ${escapeHtml(String(r.activeLineCount))} 行</div></td>
    <td>${escapeHtml(r.basisAndMethodText || '')}</td>
    <td>${escapeHtml(String(r.activeBatchCount))} / ${escapeHtml(String(r.voidedBatchCount))} / ${escapeHtml(String(r.unknownStatusBatchCount))}</td>
    <td>${escapeHtml(r.evidenceStatusText || '')}</td>
    <td>${link}</td>
    <td><button class="btn btn-neutral btn-sm" onclick="ceaOpenDetail(${escapeHtml(String(r.loadingListId))})">分摊证据</button></td>
  </tr>`;
}

/* 工作台 → 明细（仍走只读 GET；工作台入口不依赖当前单据，故按装柜清单接口读取） */
async function ceaOpenDetail(loadingListId) {
  CEA.fromWorkspace = true;
  CEA.view = 'detail';
  CEA.settlement = null;

  const modal = document.getElementById('modal');
  modal.innerHTML = '<div style="padding:40px;text-align:center;color:#64748b">正在加载分摊证据…</div>';
  modal.style.display = 'flex';

  try {
    CEA.detail = await api(`/api/container/loading-lists/${loadingListId}/expense-allocation-evidence?includeHistory=true`);
    ceaRender();
  } catch (e) {
    modal.innerHTML = `<div style="padding:40px;text-align:center;color:#b91c1c">分摊证据加载失败：${escapeHtml(e.message)}</div>`;
  }
}

function ceaBackToList() {
  CEA.view = 'list';
  CEA.fromWorkspace = false;
  ceaRender();
}

async function ceaSearch() { CEA.page = 1; await ceaLoadList(); ceaRender(); }

async function ceaResetFilters() {
  CEA.page = 1;
  CEA.filters = {
    containerNo: '', loadingListNo: '', batchNo: '', currency: '',
    status: '', customerId: '', includeHistory: true, keyword: '',
  };
  await ceaLoadList();
  ceaRender();
}

async function ceaPage(delta) {
  const next = CEA.page + delta;
  if (next < 1) return;
  CEA.page = next;
  await ceaLoadList();
  ceaRender();
}

