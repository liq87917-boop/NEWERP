/* ============ 报表模块（义乌小商品外贸行业化）============ */
const REPORTS = {
  /* === 通用财务报表 === */
  'product-sales-ranking': { api: '/api/reports/product-sales-ranking', title: '爆款 SKU 销售排行', designer: 'product-sales-ranking',
    emoji: '🔥', kpi: 'cargo',
    summary: 'TOP SKU · 已审核销售出库 · 当前授权客户 · 单位独立不合并 · 金额为当前价估算(币种未知，非实际发货收入)',
    columns: [
      { key: 'rank', label: '排名', type: 'number' },
      { key: 'productCode', label: '商品编码' },
      { key: 'productName', label: '商品名称' },
      { key: 'spec', label: '规格' },
      { key: 'unit', label: '单位' },
      { key: 'totalQuantity', label: '销售件数(签名)', type: 'number' },
      { key: 'totalAmount', label: '当前价估算金额(币种未知)', type: 'number' },
      { key: 'amountLabel', label: '金额口径' },
    ] },
  'order-profit': { api: '/api/reports/order-profit', title: '订单利润暂估表', designer: 'order-profit',
    emoji: '💹', kpi: 'gold',
    summary: '原币销售额 · 成本/利润未知(无历史成本依据) · 当前价估算(币种未知，仅估算)',
    columns: [
      { key: 'orderNo', label: '订单号' },
      { key: 'orderDate', label: '订单日期', type: 'date' },
      { key: 'customerName', label: '客户' },
      { key: 'currencyLabel', label: '原币币种' },
      { key: 'salesAmount', label: '销售额(原币)', type: 'money' },
      { key: 'costAmount', label: '成本金额', type: 'money' },
      { key: 'profit', label: '利润', type: 'money' },
      { key: 'profitRate', label: '利润率%', type: 'number' },
      { key: 'costEvidence', label: '成本证据' },
      { key: 'currentPriceEstimate', label: '当前价估算(币种未知)', type: 'money' },
      { key: 'currentPriceEstimateReason', label: '估算说明' },
    ] },
  'customer-shipment': { api: '/api/reports/customer-shipment', title: '客户出货量统计表',
    emoji: '🚢', kpi: 'ocean', summary: '客户出货量 · 按目的港口 / 区域' },
  'salesman-output': { api: '/api/reports/salesman-output', title: '业务员产值报表',
    emoji: '📊', kpi: 'lc', summary: '业务员产值 · 按月汇总' },
  'balance-sheet': { api: '/api/reports/balance-sheet', title: '资产负债表',
    emoji: '⚖️', kpi: 'port', summary: '资产 = 负债 + 所有者权益' },
  'income-statement': { api: '/api/reports/income-statement', title: '利润表',
    emoji: '📈', kpi: 'cargo', summary: '收入 - 成本 = 利润' },
  'cash-flow': { api: '/api/reports/cash-flow', title: '现金流量表',
    emoji: '🌊', kpi: 'ocean', summary: '经营 / 投资 / 筹资三大现金流' },

  /* === 阶段 2 新增：应收账龄（按订单逐笔，支持客户账期判断逾期）=== */
  'ar-aging': { api: '/api/reports/ar-aging', title: '应收账龄分析表', asOf: true,
    emoji: '⏳', kpi: 'gold', summary: '未收款订单逐笔账龄 · 按客户账期判断逾期 · 直接用于催收',
    columns: [
      { key: 'customerName', label: '客户' },
      { key: 'orderNo', label: '订单号' },
      { key: 'orderDate', label: '订单日期', type: 'date' },
      { key: 'currency', label: '币种' },
      { key: 'orderAmount', label: '订单金额', type: 'money' },
      { key: 'receivedAmount', label: '已收', type: 'money' },
      { key: 'balance', label: '未收余额', type: 'money' },
      { key: 'agingDays', label: '账龄(天)', type: 'number' },
      { key: 'creditDays', label: '账期(天)', type: 'number' },
      { key: 'bucket', label: '账龄区间' },
      { key: 'status', label: '状态' },
    ] },

  /* === 阶段 2 续：运营类报表 === */
  'container-stats': { api: '/api/reports/container-stats', title: '柜量与装柜利用率统计',
    emoji: '🚢', kpi: 'ocean', summary: '按柜聚合：客户数 / 箱数 / 重量 / 体积 / 装载率（40HQ 68m³ 基准）',
    columns: [
      { key: 'containerNo', label: '柜号' },
      { key: 'loadingDate', label: '装柜日期', type: 'date' },
      { key: 'typeText', label: '柜型' },
      { key: 'customerCount', label: '客户数', type: 'number' },
      { key: 'totalCartons', label: '箱数', type: 'number' },
      { key: 'totalWeight', label: '重量(kg)', type: 'number' },
      { key: 'totalVolume', label: '体积(m³)', type: 'number' },
      { key: 'utilization', label: '装载率%', type: 'number' },
    ] },
  'purchase-cost': { api: '/api/reports/purchase-cost', title: '采购成本分析表',
    emoji: '🏭', kpi: 'cargo', summary: '按供应商聚合：订单数 / 采购金额 / 平均单笔 / 最近下单',
    columns: [
      { key: 'supplierName', label: '供应商' },
      { key: 'supplierType', label: '类型' },
      { key: 'orderCount', label: '订单数', type: 'number' },
      { key: 'totalAmount', label: '采购金额', type: 'money' },
      { key: 'avgAmount', label: '平均单笔', type: 'money' },
      { key: 'lastOrderDate', label: '最近下单', type: 'date' },
    ] },
  'tax-refund-summary': { api: '/api/reports/tax-refund-summary', title: '退税汇总表', noDate: true,
    emoji: '🧾', kpi: 'gold', summary: '按退税期间：记录数 / 出口额 / 可退 / 已退 / 未退税额',
    columns: [
      { key: 'refundPeriod', label: '退税期间' },
      { key: 'recordCount', label: '记录数', type: 'number' },
      { key: 'declaredCount', label: '已申报数', type: 'number' },
      { key: 'refundedCount', label: '已退税数', type: 'number' },
      { key: 'exportAmount', label: '出口金额', type: 'money' },
      { key: 'refundableAmount', label: '可退税额', type: 'money' },
      { key: 'refundedAmount', label: '已退税额', type: 'money' },
      { key: 'unrefundedAmount', label: '未退税额', type: 'money' },
    ] },
  'stock-alert': { api: '/api/reports/stock-alert', title: '库存预警表', noDate: true,
    emoji: '⚠️', kpi: 'cargo', summary: '低于安全库存 / 超出库存上限（需先在商品资料填写安全库存与上限）',
    columns: [
      { key: 'productName', label: '商品' },
      { key: 'spec', label: '规格' },
      { key: 'unit', label: '单位' },
      { key: 'warehouseName', label: '仓库' },
      { key: 'quantity', label: '现存量', type: 'number' },
      { key: 'minStock', label: '安全库存', type: 'number' },
      { key: 'maxStock', label: '库存上限', type: 'number' },
      { key: 'diff', label: '差额', type: 'number' },
      { key: 'alertLevel', label: '预警级别' },
    ] },

  /* === 阶段 2 续：业务员提成 === */
  'sales-commission': { api: '/api/reports/sales-commission', title: '业务员提成表',
    emoji: '💰', kpi: 'gold', summary: '按业务员：销售额 / 毛利 / 毛利率 / 提成额（提成比例取自系统参数 SalesCommissionRate）',
    columns: [
      { key: 'salesmanName', label: '业务员' },
      { key: 'orderCount', label: '订单数', type: 'number' },
      { key: 'salesAmount', label: '销售额', type: 'money' },
      { key: 'profit', label: '毛利', type: 'money' },
      { key: 'profitRate', label: '毛利率%', type: 'number' },
      { key: 'commissionRate', label: '提成比例%', type: 'number' },
      { key: 'commissionAmount', label: '提成额', type: 'money' },
    ] },

  /* === 阶段 2 续：跟进提醒（客户回访清单） === */
  'follow-up-due': { api: '/api/reports/follow-up-due', title: '跟进提醒', asOf: true, designer: true,
    emoji: '🔔', kpi: 'gold', summary: '下次跟进日期已到期 / 未来 7 天内即将到期（按逾期天数排序，可直接当回访清单用）',
    columns: [
      { key: 'customerName', label: '客户' },
      { key: 'salesmanName', label: '跟进人' },
      { key: 'followDate', label: '上次跟进', type: 'date' },
      { key: 'result', label: '上次结果' },
      { key: 'nextFollowDate', label: '下次跟进', type: 'date' },
      { key: 'dueDays', label: '逾期天数', type: 'number' },
      { key: 'dueStatus', label: '状态' },
      { key: 'subject', label: '跟进主题' },
    ] },

  /* === ERP-018：报价成交率（询报价 → 形式发票 PI / 销售订单 转化分析；按业务员 × 原币分列，金额绝不跨币种合计） === */
  'quotation-conversion': { api: '/api/reports/quotation-conversion', title: '报价成交率分析', designer: 'quotation',
    emoji: '📈', kpi: 'gold',
    summary: '按业务员 × 原币币种：成交率 = 已转出数 ÷ 有效报价数 × 100（已转出 = 已转 PI / 已转销售订单 / 已完成；已作废不计入分母；金额按原币分列，不跨币种合计）',
    columns: [
      { key: 'salesmanName', label: '业务员' },
      { key: 'currency', label: '原币币种' },
      { key: 'quotationCount', label: '有效报价数', type: 'number' },
      { key: 'convertedCount', label: '已转出数', type: 'number' },
      { key: 'conversionRate', label: '成交率%', type: 'number' },
      { key: 'expiredCount', label: '已过期未成交', type: 'number' },
      { key: 'cancelledCount', label: '已作废', type: 'number' },
      { key: 'totalAmount', label: '有效报价金额(原币)', type: 'money' },
      { key: 'convertedAmount', label: '已转出金额(原币)', type: 'money' },
      { key: 'avgConvertedAmount', label: '单笔成交均价(原币)', type: 'money' },
    ] },
};

async function renderReport(rep, name) {
  document.getElementById('header-title').textContent = name;
  const content = document.getElementById('content');
  const defStart = new Date(Date.now() - 365 * 86400000).toISOString().slice(0, 10);
  const defEnd = new Date().toISOString().slice(0, 10);
  content.innerHTML = `
    <!-- 报表 Hero：行业化主题 -->
    <div class="page-hero report-hero">
      <h2>${rep.emoji} ${rep.title}</h2>
      <p>${rep.summary} · 期间：${defStart} 至 ${defEnd}</p>
    </div>

    <!-- KPI 占位：报表加载后填充 -->
    <div class="kpi-grid" id="report-kpi-grid">
      <div class="kpi-card ${rep.kpi}">
        <div class="kpi-label"><span class="kpi-emoji">${rep.emoji}</span>记录数</div>
        <div class="kpi-value" data-rkpi="rows">--<span class="unit">行</span></div>
        <div class="kpi-delta flat" data-rkpi="rows-tip">查询中…</div>
      </div>
      <div class="kpi-card gold">
        <div class="kpi-label"><span class="kpi-emoji">💰</span><span data-rkpi-label="total">合计金额</span></div>
        <div class="kpi-value" data-rkpi="total">--<span class="unit" data-rkpi-unit="total">USD</span></div>
        <div class="kpi-delta flat" data-rkpi="total-tip">本币合计</div>
      </div>
      <div class="kpi-card ocean">
        <div class="kpi-label"><span class="kpi-emoji">📈</span>TOP 项</div>
        <div class="kpi-value" data-rkpi="top" style="font-size:18px">--</div>
        <div class="kpi-delta flat" data-rkpi="top-tip">排名首位</div>
      </div>
    </div>

    <!-- 工具栏：日期范围 + 查询 -->
    <div class="toolbar">
      <div class="toolbar-left">
        <div class="date-range">
          <input type="date" id="rep-start" value="${defStart}">
          <span class="date-range-sep">至</span>
          <input type="date" id="rep-end" value="${defEnd}">
        </div>
        <button class="btn btn-primary" onclick="loadReport()">查询</button>
      </div>
      <div class="toolbar-actions">
        ${rep.designer === 'quotation'
          ? `<button class="btn btn-neutral" onclick="openQuotationConversionDesigner()" title="打开报价成交率字段设计器（只读预览，按业务员 × 原币分桶）">🎛 字段设计器</button>`
          : rep.designer === 'product-sales-ranking'
            ? `<button class="btn btn-neutral" onclick="openProductSalesRankingDesigner()" title="打开商品销量排名字段设计器（只读预览，发货数量证据，金额估算已排除）">🎛 字段设计器</button>`
            : rep.designer === 'order-profit'
              ? `<button class="btn btn-neutral" onclick="openOrderProfitEstimateDesigner()" title="打开订单利润暂估字段设计器（只读预览，原币销售额与未知成本利润证据，当前价估算独立标注）">🎛 字段设计器</button>`
              : rep.designer
                ? `<button class="btn btn-neutral" onclick="openFollowUpDueDesigner()" title="打开动态跟进提醒字段设计器（只读预览）">🎛 字段设计器</button>`
                : ''}
        <button class="btn btn-neutral" onclick="exportReportCSV()" title="导出为 CSV">📤 导出 CSV</button>
        <button class="btn btn-neutral" onclick="window.print()" title="打印报表">🖨 打印</button>
      </div>
    </div>

    ${rep.designer === 'quotation'
      ? `<div id="qcd-designer"></div>`
      : rep.designer === 'product-sales-ranking'
        ? `<div id="psr-designer"></div>`
        : rep.designer === 'order-profit'
          ? `<div id="opd-designer"></div>`
          : rep.designer ? `<div id="fud-designer"></div>` : ''}

    <div class="table-wrap" id="report-table">
      <div class="skeleton-row"></div><div class="skeleton-row"></div><div class="skeleton-row"></div>
    </div>`;
  await loadReport();
}

async function loadReport() {
  const title = document.getElementById('header-title').textContent;
  const code = Object.keys(REPORTS).find(k => REPORTS[k].title === title);
  if (!code) return;
  const rep = REPORTS[code];
  const start = document.getElementById('rep-start').value;
  const end = document.getElementById('rep-end').value;
  const qs = rep.noDate
    ? ''
    : ((rep.asOf || rep.api.includes('balance-sheet')) ? `asOfDate=${end}` : `start=${start}&end=${end}`);
  try {
    const data = await api(rep.api + (qs ? '?' + qs : ''));
    renderReportData(code, data);
    fillReportKpi(code, data);
  } catch (e) {
    document.getElementById('report-table').innerHTML = `<div class="empty"><div style="font-size:48px">⚠️</div><div>报表加载失败：${e.message}</div></div>`;
  }
}

function fillReportKpi(code, data) {
  const setR = (k, v) => { const el = document.querySelector(`[data-rkpi="${k}"]`); if (el) el.firstChild.nodeValue = String(v); };
  const setT = (k, v) => { const el = document.querySelector(`[data-rkpi="${k}"]`); if (el) el.textContent = String(v); };
  if (code === 'quotation-conversion') { fillQuotationConversionKpi(data); return; }
  if (code === 'product-sales-ranking') { fillProductSalesRankingKpi(data); return; }
  if (code === 'order-profit') { fillOrderProfitKpi(data); return; }
  if (code === 'balance-sheet' || code === 'income-statement' || code === 'cash-flow') {
    const lines = data.lines || [];
    const total = lines.reduce((s, l) => s + (Number(l.amount) || 0), 0);
    setR('rows', lines.length);
    setR('total', total.toFixed(2));
    setT('top-tip', lines[0]?.name || '暂无');
    setR('top', lines[0]?.name || '--');
  } else if (Array.isArray(data)) {
    const numericKeys = data.length ? Object.keys(data[0]).filter(k => typeof data[0][k] === 'number') : [];
    const total = data.reduce((s, r) => s + numericKeys.reduce((ss, k) => ss + (Number(r[k]) || 0), 0), 0);
    setR('rows', data.length);
    setR('total', total.toFixed(2));
    const firstName = data[0] ? (data[0].productName || data[0].customerName || data[0].salesmanName || data[0].name || 'TOP 1') : '--';
    setR('top', firstName);
  } else if (data && Array.isArray(data.items)) {
    setR('rows', data.items.length);
    setR('total', '--');
    setR('top', data.items[0]?.name || '--');
  }
}

/* 商品销量排名 KPI：单位保持独立，绝不跨单位合计数量；金额仅当前价估算（币种未知，非实际发货收入） */
function fillProductSalesRankingKpi(data) {
  const setR = (k, v) => { const el = document.querySelector(`[data-rkpi="${k}"]`); if (el) el.firstChild.nodeValue = String(v); };
  const setT = (k, v) => { const el = document.querySelector(`[data-rkpi="${k}"]`); if (el) el.textContent = String(v); };
  const rows = Array.isArray(data) ? data : [];
  setR('rows', rows.length);
  setT('rows-tip', '商品 × 规格 × 单位分桶');

  const units = Array.from(new Set(rows.map(r => String(r.unit || '未知单位')))).sort();
  const label = document.querySelector('[data-rkpi-label="total"]');
  if (label) label.textContent = '单位组';
  const unit = document.querySelector('[data-rkpi-unit="total"]');
  if (unit) unit.textContent = '';
  setR('total', units.length ? units.join(' / ') : '--');
  setT('total-tip', '单位保持独立，禁止跨单位合计数量');

  const top = rows[0];
  setR('top', top ? ([top.productName, top.spec, top.unit].filter(Boolean).join(' ') || 'TOP 1') : '--');
  setT('top-tip', '排名首位（件数降序）');
}

/* 订单利润暂估 KPI：销售额为订单原币，成本/利润/利润率为未知，绝不跨币种合计、绝不展示实际利润口径 */
function fillOrderProfitKpi(data) {
  const setR = (k, v) => { const el = document.querySelector(`[data-rkpi="${k}"]`); if (el) el.firstChild.nodeValue = String(v); };
  const setT = (k, v) => { const el = document.querySelector(`[data-rkpi="${k}"]`); if (el) el.textContent = String(v); };
  const rows = Array.isArray(data) ? data : [];
  setR('rows', rows.length);
  setT('rows-tip', '订单逐笔 · 原币分别成行');

  const label = document.querySelector('[data-rkpi-label="total"]');
  if (label) label.textContent = '原币币种';
  const unit = document.querySelector('[data-rkpi-unit="total"]');
  if (unit) unit.textContent = '';
  const currencies = Array.from(new Set(rows.map(r => String(r.currency || '未知币种')))).sort();
  setR('total', currencies.length ? currencies.join(' / ') : '--');
  setT('total-tip', '原币分别成行，绝不跨币种合计（不提供跨币种总额 / 利润率）');

  const top = rows[0];
  setR('top', top ? `${top.orderNo || '--'} · ${top.currency || '未知币种'}` : '--');
  setT('top-tip', '排序首位（订单日期降序）');
}

/* 报价成交率 KPI：金额一律按原币分列，绝不跨币种相加/换算/默认币种；计数可汇总 */
function fillQuotationConversionKpi(data) {
  const setR = (k, v) => { const el = document.querySelector(`[data-rkpi="${k}"]`); if (el) el.firstChild.nodeValue = String(v); };
  const setT = (k, v) => { const el = document.querySelector(`[data-rkpi="${k}"]`); if (el) el.textContent = String(v); };
  const rows = Array.isArray(data) ? data : [];
  setR('rows', rows.length);
  setT('rows-tip', '业务员 × 原币分桶');

  /* 只对同币种金额求和；货币永远保留原币标签，绝不跨币种合计 */
  const byCurrency = {};
  rows.forEach(r => {
    const c = String(r.currency || '').trim().toUpperCase() || '未知币种';
    byCurrency[c] = (byCurrency[c] || 0) + (Number(r.convertedAmount) || 0);
  });
  const currencies = Object.keys(byCurrency).sort();
  const label = document.querySelector('[data-rkpi-label="total"]');
  if (label) label.textContent = '币种';
  const unit = document.querySelector('[data-rkpi-unit="total"]');
  if (unit) unit.textContent = '';
  setR('total', currencies.length ? currencies.join(' / ') : '--');
  setT('total-tip', Object.keys(byCurrency).sort()
    .map(c => `${c} ${fmtMoney(byCurrency[c])}`).join(' · ') || '暂无');

  const top = rows[0];
  setR('top', top ? `${top.salesmanName || '未指定业务员'} · ${top.currency || '未知币种'}` : '--');
  setT('top-tip', '排名首位');
}

function exportReportCSV() {
  const table = document.querySelector('#report-table table');
  if (!table) { toast('暂无可导出数据', 'warning'); return; }
  const rows = Array.from(table.querySelectorAll('tr'));
  const csv = rows.map(tr => Array.from(tr.querySelectorAll('th,td')).map(td => `"${td.textContent.replace(/"/g, '""')}"`).join(',')).join('\n');
  const blob = new Blob(['\uFEFF' + csv], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url; a.download = `${document.getElementById('header-title').textContent}.csv`;
  document.body.appendChild(a); a.click(); a.remove(); URL.revokeObjectURL(url);
  toast('CSV 已导出', 'success');
}

function renderReportData(code, data) {
  const el = document.getElementById('report-table');
  if (code === 'balance-sheet' || code === 'income-statement' || code === 'cash-flow') {
    const lines = (data.lines || []);
    if (!lines.length) { el.innerHTML = emptyReportHtml('暂无财务数据', '📊'); return; }
    const rows = lines.map(l => `<tr><td>${l.name}</td><td class="text-right">${fmtMoney(l.amount)}</td></tr>`).join('');
    el.innerHTML = `<table><thead><tr><th>项目</th><th class="text-right">金额</th></tr></thead><tbody>${rows}</tbody></table>`;
    return;
  }
  if (code === 'order-profit') { renderOrderProfitData(data); return; }
  const arr = Array.isArray(data) ? data : (data.items || []);
  if (!arr.length) { el.innerHTML = emptyReportHtml('暂无数据', '📭'); return; }
  const rep = REPORTS[code] || {};
  /* 优先使用报表自带的中文列定义；未配置时回退为按数据结构自动推导（保持原有行为） */
  const cols = (rep.columns && rep.columns.length)
    ? rep.columns
    : Object.keys(arr[0])
        .filter(k => k !== 'productId' && k !== 'customerId' && k !== 'salesmanId')
        .map(k => ({ key: k, label: k }));
  const head = cols.map(c => `<th class="${c.type === 'money' || c.type === 'number' ? 'text-right' : ''}">${c.label}</th>`).join('');
  const rows = arr.map(r => `<tr>${cols.map(c => {
    const v = r[c.key];
    if (c.type === 'money') return `<td class="text-right">${fmtMoney(v)}</td>`;
    if (c.type === 'date') return `<td>${fmtDate(v)}</td>`;
    if (c.type === 'number') return `<td class="text-right">${v ?? ''}</td>`;
    if (typeof v === 'number') return `<td class="text-right">${fmtMoney(v)}</td>`;
    return `<td>${v ?? ''}</td>`;
  }).join('')}</tr>`).join('');
  el.innerHTML = `<table><thead><tr>${head}</tr></thead><tbody>${rows}</tbody></table>`;
}

/* 订单利润暂估表：原币销售额 + 未知成本/利润 + 当前价估算（币种未知）；null 显式显示「未知」，文本安全转义 */
function renderOrderProfitData(data) {
  const el = document.getElementById('report-table');
  const rows = Array.isArray(data) ? data : [];
  if (!rows.length) { el.innerHTML = emptyReportHtml('暂无可显示数据', '💹'); return; }
  const esc = (v) => fudDesEsc(v);
  const num = (v) => (v === null || v === undefined ? '<span class="text-muted">未知</span>' : fmtMoney(v));
  const txt = (v) => (v === null || v === undefined || v === '' ? '<span class="text-muted">未知</span>' : esc(v));
  const reason = (v) => (v ? esc(v) : '');
  const body = rows.map(r => `<tr>
    <td>${esc(r.orderNo)}</td>
    <td>${fmtDate(r.orderDate)}</td>
    <td>${esc(r.customerName)}</td>
    <td>${esc(r.currencyLabel || r.currency || '未知币种')}</td>
    <td class="text-right">${fmtMoney(r.salesAmount)}</td>
    <td class="text-right">${num(r.costAmount)}</td>
    <td class="text-right">${num(r.profit)}</td>
    <td class="text-right">${num(r.profitRate)}</td>
    <td>${txt(r.costEvidence)}</td>
    <td class="text-right">${num(r.currentPriceEstimate)}</td>
    <td>${reason(r.currentPriceEstimateReason)}</td>
  </tr>`).join('');
  el.innerHTML = `<table><thead><tr>
    <th>订单号</th><th>订单日期</th><th>客户</th><th>原币币种</th>
    <th class="text-right">销售额(原币)</th>
    <th class="text-right">成本金额</th><th class="text-right">利润</th><th class="text-right">利润率%</th>
    <th>成本证据</th><th class="text-right">当前价估算(币种未知)</th><th>估算说明</th>
  </tr></thead><tbody>${body}</tbody></table>`;
}

/* 报表空状态：行业主题 emoji */
function emptyReportHtml(msg, emoji) {
  return `<div class="empty empty-foreign">
    <div class="empty-emoji">${emoji || '📭'}</div>
    <div class="empty-text">${msg}</div>
    <div class="empty-sub">试试调整日期范围或切换其他报表</div>
  </div>`;
}

/* ============ 动态跟进提醒字段设计器（ERP-194：只读、有界的前端字段选择与分页预览） ============
   口径与后端 ERP-193（DynamicFollowUpDueReportController / DynamicFollowUpDueReportRules）一一对应：
   - 入口复用在「跟进提醒」报表（reports.js 的 follow-up-due，designer: true），不新增菜单 / 架构 / 脚本注册；
   - 字段选择器只由 GET /api/dynamic-follow-up-due-report 返回的有限白名单目录渲染为复选框（name="fud-des-field"），
     绝无自由填写的字段名或 SQL；勾选状态经 fudDesSelectFields 规范化（去重、保持顺序、丢弃未知键）；
   - 筛选仅限到期状态（overdue / today / upcoming）、as-of 日期与提前天数（0~365），分页有界（页码 ≥ 1，每页 1~200），
     预览走 POST /api/dynamic-follow-up-due-report，只发送「白名单字段 + 有界筛选 + 有界分页」；
   - 结果按后端返回的列名与选定字段值渲染（fudDesTableHtml / fudDesResultHtml），全部 HTML 转义；
   - 空页 / 授权撤销（权限不足 / 未登录）/ 无效请求 / 网络失败分别可见，且不暴露范围外数据；
   - 全程只读：不写库、不迁移、不执行任意 SQL。 */

/* 字段设计器状态（纯数据；DOM 访问只在事件处理函数内部发生） */
let FUD_DYN = {
  catalog: null,      // GET /api/dynamic-follow-up-due-report 返回的目录 DTO
  fields: [],         // 目录字段（白名单）
  selectedKeys: [],   // 当前勾选的字段键（默认全选）
  view: null,         // 最近一次预览结果
  page: 1,            // 当前预览页（预览 / 翻页复用）
};

/* HTML 转义（本地独立实现，避免依赖全局 escapeHtml 的加载顺序） */
function fudDesEsc(v) {
  return String(v ?? '').replace(/[&<>"']/g, c => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}

/* 规范化选定字段（fail closed）：只保留目录白名单内的键、去重、保持请求顺序；未知键丢弃，绝不发送任意字段名 */
function fudDesSelectFields(catalogFields, selectedKeys) {
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

/* 到期状态白名单（overdue / today / upcoming；未知取值不回传，由后端 fail closed 兜底） */
function fudDesDueStatusKey(v) {
  const s = String(v || '').trim();
  return (s === 'overdue' || s === 'today' || s === 'upcoming') ? s : '';
}

/* 分组键白名单（与后端 NormalizeGroupBy 一致：none / dueStatus / salesman；未知取值不回传，由后端 fail closed 兜底） */
function fudDesGroupKey(v) {
  const s = String(v || '').trim();
  return (s === 'none' || s === 'dueStatus' || s === 'salesman') ? s : '';
}

/* 客户 Id 白名单校验：留空 = 不过滤；正整数才回传，否则返回 null（由 fudDesFilterError 先行提示） */
function fudDesCustomerId(v) {
  const s = String(v || '').trim();
  if (s === '') return null;
  const n = Number(s);
  return Number.isInteger(n) && n > 0 ? n : null;
}

/* 关键字规范化：去首尾空白，最多 80 字符；超长返回 null（由 fudDesFilterError 先行提示） */
function fudDesKeyword(v) {
  const s = String(v || '').trim();
  if (s === '') return null;
  return s.length > 80 ? null : s;
}

/* 客户 Id / 关键字客户端校验错误文案（与后端 ValidateCustomerId / NormalizeKeyword 一致；空串 = 通过） */
function fudDesFilterError(state) {
  const s = String(state && state.customerId || '').trim();
  if (s !== '') {
    const n = Number(s);
    if (!Number.isInteger(n) || n <= 0) return '客户 Id 必须是正整数';
  }
  const keyword = String(state && state.keyword || '').trim();
  if (keyword.length > 80) return '关键字最多 80 个字符';
  return '';
}

/* 组装有界预览请求体：字段只来自目录、筛选仅到期状态 / as-of 日期 / 提前天数、分组仅白名单、分页有界，绝不接受任意字段名或 SQL */
function fudDesBuildRequest(state) {
  const fields = fudDesSelectFields(state.catalogFields, state.selectedKeys);
  const page = Math.max(1, Math.floor(Number(state.page) || 1));
  const maxPageSize = Number(state.maxPageSize) || 200;
  let pageSize = Math.floor(Number(state.pageSize));
  if (!Number.isFinite(pageSize)) pageSize = 20;
  pageSize = Math.max(1, Math.min(maxPageSize, pageSize));

  const req = { fields, page, pageSize };

  if (state.asOfDate) req.asOfDate = String(state.asOfDate).slice(0, 10);
  let aheadDays = Math.floor(Number(state.aheadDays));
  if (!Number.isFinite(aheadDays)) aheadDays = 7;
  aheadDays = Math.max(0, Math.min(365, aheadDays));
  req.aheadDays = aheadDays;

  const dueStatus = fudDesDueStatusKey(state.dueStatus);
  if (dueStatus) req.dueStatus = dueStatus;

  const groupBy = fudDesGroupKey(state.groupBy);
  if (groupBy && groupBy !== 'none') req.groupBy = groupBy;

  const customerId = fudDesCustomerId(state.customerId);
  if (customerId) req.customerId = customerId;
  const keyword = fudDesKeyword(state.keyword);
  if (keyword) req.keyword = keyword;

  return req;
}

/* 单元格纯文本：日期截断为 YYYY-MM-DD、数字合理格式化、其余按字符串呈现（null 显示为空） */
function fudDesCellText(value, field) {
  const dataType = (field && field.dataType) || 'text';
  if (value === null || value === undefined) return '';
  if (dataType === 'date') return String(value).slice(0, 10);
  if (dataType === 'number') {
    const n = Number(value);
    if (Number.isFinite(n)) return Number.isInteger(n) ? String(n) : n.toFixed(2);
    return String(value);
  }
  return String(value);
}

/* 单元格 HTML（转义后安全渲染） */
function fudDesRenderCell(value, field) {
  return fudDesEsc(fudDesCellText(value, field));
}

/* 结果表格 HTML：表头为返回的列名、单元格为返回的选定字段值，全部经转义 */
function fudDesTableHtml(view) {
  const cols = (view && view.columns) || [];
  const rows = (view && view.rows) || [];
  if (!cols.length) return '';
  const align = c => (c.dataType === 'number') ? ' class="text-right"' : '';
  const head = cols.map(c => `<th${align(c)}>${fudDesEsc(c.label || c.key)}</th>`).join('');
  const body = rows.length
    ? rows.map(r => `<tr>${cols.map(c => `<td${align(c)}>${fudDesRenderCell(r[c.key], c)}</td>`).join('')}</tr>`).join('')
    : '';
  return `<div class="table-wrap" style="margin-top:8px"><table><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table></div>`;
}

/* 分页（有界、稳定）：当前页之外仍有记录时标注截断，翻页复用当前字段 / 筛选 / 每页条数 */
function fudDesPagingHtml(view) {
  if (!view) return '';
  const prevDisabled = view.page <= 1 ? ' disabled' : '';
  const nextDisabled = view.page >= view.totalPages ? ' disabled' : '';
  return `<div style="display:flex;justify-content:space-between;align-items:center;margin-top:8px">
      <span class="text-muted">第 ${view.page} 页 / 共 ${view.totalPages} 页${view.truncated ? '（仅当前页，后续仍有记录）' : ''}</span>
      <div>
        <button class="btn btn-neutral btn-sm" onclick="fudDesPage(-1)"${prevDisabled}>← 上一页</button>
        <button class="btn btn-neutral btn-sm" onclick="fudDesPage(1)"${nextDisabled}>下一页 →</button>
      </div></div>`;
}

/* 空结果提示（显式使用后端 emptyText） */
function fudDesEmptyHtml(view) {
  return `<div class="empty" style="margin:8px 0">${fudDesEsc((view && view.emptyText) || '没有符合筛选条件的跟进提醒证据')}</div>`;
}

/* 分组行数分布（ERP-197）：按当前授权预览页的分组行数渲染可访问列表（只统计本页，绝不外推整表总数）；
   未分配业务员标签由后端给出并清晰可见；标签与计数全部转义；none / 空分组不渲染 */
function fudDesGroupsHtml(view) {
  const groupBy = view && view.groupBy;
  const groups = (view && view.groups) || [];
  if (!groupBy || groupBy === 'none' || !Array.isArray(groups) || groups.length === 0) return '';
  const max = Math.max(1, ...groups.map(g => Number(g && g.count) || 0));
  const bars = groups.map(g => {
    const label = (g && g.label) || (g && g.key) || '';
    const count = Number(g && g.count) || 0;
    const pct = Math.round(count / max * 100);
    return `<li role="listitem" aria-label="${fudDesEsc(label)}：${count} 行" style="display:flex;align-items:center;gap:8px;margin:4px 0">
      <span style="flex:0 0 180px;text-align:right;color:#334155;overflow:hidden;text-overflow:ellipsis;white-space:nowrap" title="${fudDesEsc(label)}">${fudDesEsc(label)}</span>
      <span style="flex:1;background:#e2e8f0;border-radius:4px;height:16px;overflow:hidden;min-width:40px">
        <span style="display:block;height:100%;background:#2563eb;width:${pct}%"></span>
      </span>
      <span style="flex:0 0 72px;text-align:right;color:#0f172a">${count} 行</span>
    </li>`;
  }).join('');
  return `<div class="pd-hint" role="img" aria-label="本页跟进提醒行数分布（仅统计本页）" style="margin-top:8px">📊 本页行数分布（仅统计本页）</div>
    <ul role="list" style="list-style:none;padding:0 8px;margin:4px 0 8px">${bars}</ul>`;
}

/* 筛选集到期状态合计（ERP-201）＋ 状态钻取（ERP-202）：显示后端返回的「分页前全量」已逾期 / 今日到期 / 即将到期三项计数；
   与「本页」分组计数在口径上区分（合计为筛选集全量，分组仅当前页）；后端未返回 dueStatusTotals 时不渲染，
   三项计数全部转义（数字零值安全兜底）。ERP-202：每一项均为只读钻取入口，点击后仅把到期状态筛选设为该项、
   回到第 1 页并复用既有只读预览（其余筛选与字段保留）；activeDueStatus 命中时以 aria-current 标出当前状态，
   并提供「返回全部状态」出口。 */
function fudDesDueStatusTotalsHtml(view, activeDueStatus) {
  const totals = view && view.dueStatusTotals;
  if (!totals) return '';
  const label = (totals.label || '筛选集到期状态合计');
  const overdue = Number(totals.overdue) || 0;
  const today = Number(totals.today) || 0;
  const upcoming = Number(totals.upcoming) || 0;
  const total = overdue + today + upcoming;
  const active = fudDesDueStatusKey(activeDueStatus);
  const textOf = { overdue: '已逾期', today: '今日到期', upcoming: '即将到期' };
  const chip = (key, text, count) => {
    const isActive = active === key;
    const activeAttr = isActive ? ' aria-current="true"' : '';
    const activeStyle = isActive ? ' style="font-weight:700;border-color:#2563eb;color:#1d4ed8;background:#eff6ff"' : '';
    return `<button type="button" class="btn btn-neutral btn-sm"${activeAttr}${activeStyle} onclick="fudDesDrillDueStatus('${key}')" title="仅查看${fudDesEsc(text)}">${fudDesEsc(text)} <b>${count}</b></button>`;
  };
  const back = active
    ? ` <button type="button" class="btn btn-neutral btn-sm" onclick="fudDesResetDueStatus()" title="清除到期状态筛选，返回全部状态">↩ 返回全部状态</button>`
    : '';
  const activeNote = active ? `，当前仅查看${fudDesEsc(textOf[active] || active)}` : '';
  return `<div class="pd-hint" role="img" aria-label="${fudDesEsc(label)}：已逾期 ${overdue}，今日到期 ${today}，即将到期 ${upcoming}，合计 ${total}${activeNote}" style="margin-top:8px">🧮 ${fudDesEsc(label)}：${chip('overdue', '已逾期', overdue)} · ${chip('today', '今日到期', today)} · ${chip('upcoming', '即将到期', upcoming)}（合计 ${total}）${back}</div>`;
}

/* 状态钻取（ERP-202）：点击筛选集到期状态合计中的某一状态，仅设置该有限到期状态筛选并回到第 1 页；
   其余客户 Id / 关键字 / as-of / 提前天数 / 选定字段 / 分组全部保留；仍走既有只读预览（授权审计不变）。 */
function fudDesDrillDueStatus(status) {
  const key = fudDesDueStatusKey(status);
  if (!key) return; // fail closed：非白名单状态不钻取
  const el = document.getElementById('fud-des-due-status');
  if (el) el.value = key;
  fudDesPreview(1);
}

/* 返回全部状态（ERP-202）：清除到期状态筛选并回到第 1 页，其余筛选与字段保留 */
function fudDesResetDueStatus() {
  const el = document.getElementById('fud-des-due-status');
  if (el) el.value = '';
  fudDesPreview(1);
}

/* 错误提示（授权撤销 / 未登录 / 无效请求 / 网络失败分别可见，且不暴露任何数据） */
function fudDesErrorHtml(kind, message) {
  const labels = {
    forbidden: '权限不足',
    unauthorized: '未登录 / 登录已过期',
    invalid: '请求无效',
    empty: '导出内容为空',
    network: '网络请求失败',
    error: '预览失败',
  };
  return `<div class="pd-hint" style="color:#b91c1c;background:#fef2f2;border-color:#fecaca">
      <b>${fudDesEsc(labels[kind] || '预览失败')}</b>：${fudDesEsc(message || '')}</div>`;
}

/* 业务码 → 错误态分类 */
function fudDesKindOfCode(code) {
  if (code === 2002) return 'forbidden';
  if (code === 2000 || code === 2003) return 'unauthorized';
  if (code === 5000) return 'error';
  return 'invalid';
}

/* 预览结果（只读 / 边界 / 免责文案 + 摘要 + 空结果 + 表格 + 分页；activeDueStatus 用于标出钻取中的到期状态） */
function fudDesResultHtml(view, activeDueStatus) {
  const readOnly = view && view.readOnlyText ? `<div class="pd-hint">${fudDesEsc(view.readOnlyText)}</div>` : '';
  const boundary = view && view.boundaryText ? `<div class="pd-hint">${fudDesEsc(view.boundaryText)}</div>` : '';
  const disclaimer = view && view.disclaimerText ? `<div class="pd-hint" style="color:#64748b">${fudDesEsc(view.disclaimerText)}</div>` : '';
  const summary = view
    ? `<div class="text-muted" style="margin:6px 0">共 ${view.total} 行 · 第 ${view.page} 页 · 每页 ${view.pageSize} 行 · 共 ${view.totalPages} 页${view.truncated ? ' · 后续仍有分页' : ''}</div>`
    : '';
  const groups = fudDesGroupsHtml(view);
  const totals = fudDesDueStatusTotalsHtml(view, activeDueStatus);
  const empty = view && (!view.rows || view.rows.length === 0) ? fudDesEmptyHtml(view) : '';
  return `${readOnly}${boundary}${disclaimer}${summary}${totals}${groups}${empty}${fudDesTableHtml(view)}${fudDesPagingHtml(view)}`;
}

/* 字段选择器：仅由目录白名单渲染为复选框，无自由填写的字段名 */
function fudDesFieldChooserHtml(fields, selectedKeys) {
  const selected = new Set(selectedKeys || []);
  return (fields || []).map(f => {
    const checked = selected.has(f.key) ? 'checked' : '';
    return `<label style="display:inline-flex;align-items:center;gap:4px;margin:3px 6px 3px 0;padding:2px 8px;border:1px solid #e2e8f0;border-radius:6px;background:#f8fafc;cursor:pointer">
        <input type="checkbox" name="fud-des-field" value="${fudDesEsc(f.key)}" ${checked} onchange="fudDesSyncSelection()">
        <span>${fudDesEsc(f.label || f.key)}</span></label>`;
  }).join('');
}

function fudDesLoadingHtml() {
  return '<div class="pd-hint" style="text-align:center;color:#64748b">正在预览（只读查询）…</div>';
}

function fudDesRenderResult(html) {
  const el = document.getElementById('fud-designer-result');
  if (el) el.innerHTML = html;
}

/* 轻量请求封装：返回完整 ApiResponse 信封（保留 code），网络异常抛给调用方 */
async function fudDesRequest(path, method = 'GET', body = null) {
  const headers = { 'Content-Type': 'application/json' };
  const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
  if (token) headers['Authorization'] = 'Bearer ' + token;
  const opts = { method, headers };
  if (body) opts.body = JSON.stringify(body);
  const resp = await fetch(path, opts);
  return await resp.json();
}

/* 同步勾选状态到 selectedKeys（复选框 onchange） */
function fudDesSyncSelection() {
  const boxes = document.querySelectorAll('input[name="fud-des-field"]');
  FUD_DYN.selectedKeys = Array.from(boxes).filter(b => b.checked).map(b => b.value);
}

function fudDesToggleAll(checked) {
  const boxes = document.querySelectorAll('input[name="fud-des-field"]');
  FUD_DYN.selectedKeys = [];
  boxes.forEach(b => { b.checked = checked; if (checked) FUD_DYN.selectedKeys.push(b.value); });
}

/* 读取当前字段 / 筛选 / 分页状态（预览与翻页复用，单一来源） */
function fudDesBuildState(page) {
  const val = id => { const el = document.getElementById(id); return el ? el.value.trim() : ''; };
  return {
    catalogFields: FUD_DYN.fields,
    selectedKeys: FUD_DYN.selectedKeys,
    asOfDate: val('fud-des-asof'),
    aheadDays: val('fud-des-ahead'),
    dueStatus: val('fud-des-due-status'),
    groupBy: val('fud-des-group'),
    customerId: val('fud-des-customer-id'),
    keyword: val('fud-des-keyword'),
    pageSize: val('fud-des-pagesize'),
    page: page || FUD_DYN.page || 1,
    maxPageSize: FUD_DYN.catalog && FUD_DYN.catalog.maxPageSize ? FUD_DYN.catalog.maxPageSize : 200,
  };
}

/* 预览：组装有界请求 → POST → 安全渲染列名与单元格；授权 / 无效 / 空 / 网络失败均可见 */
async function fudDesPreview(page) {
  const state = fudDesBuildState(page);
  const filterError = fudDesFilterError(state);
  if (filterError) {
    fudDesRenderResult(fudDesErrorHtml('invalid', filterError));
    return;
  }
  const req = fudDesBuildRequest(state);
  FUD_DYN.page = req.page;

  fudDesRenderResult(fudDesLoadingHtml());

  try {
    const resp = await fudDesRequest('/api/dynamic-follow-up-due-report', 'POST', req);
    if (resp.code === 0) {
      FUD_DYN.view = resp.data;
      FUD_DYN.page = resp.data.page;
      fudDesRenderResult(fudDesResultHtml(resp.data, state.dueStatus));
    } else if (resp.code === 2000 || resp.code === 2003) {
      if (typeof logout === 'function') logout();
      fudDesRenderResult(fudDesErrorHtml('unauthorized', resp.message));
    } else {
      fudDesRenderResult(fudDesErrorHtml(fudDesKindOfCode(resp.code), resp.message));
    }
  } catch (err) {
    fudDesRenderResult(fudDesErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 导出当前页选定列为 Excel（ERP-195，只读）：复用预览请求体 POST /api/dynamic-follow-up-due-report/export；
   成功（xlsx 附件）触发下载；授权 / 无效 / 空结果 / 网络失败在结果区可见，不下载任何内容 */
async function fudDesExport() {
  if (!FUD_DYN.view || !FUD_DYN.view.columns || !FUD_DYN.view.columns.length) {
    fudDesRenderResult(fudDesErrorHtml('invalid', '请先预览后再导出 Excel'));
    return;
  }
  if (!FUD_DYN.view.rows || FUD_DYN.view.rows.length === 0) {
    fudDesRenderResult(fudDesErrorHtml('empty', '没有符合筛选条件的跟进提醒证据，无法导出（请先预览）'));
    return;
  }

  const state = fudDesBuildState(FUD_DYN.view.page);
  const filterError = fudDesFilterError(state);
  if (filterError) {
    fudDesRenderResult(fudDesErrorHtml('invalid', filterError));
    return;
  }
  const req = fudDesBuildRequest(state);
  fudDesRenderResult(fudDesLoadingHtml());

  try {
    const headers = { 'Content-Type': 'application/json' };
    const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
    if (token) headers['Authorization'] = 'Bearer ' + token;
    const resp = await fetch('/api/dynamic-follow-up-due-report/export', {
      method: 'POST',
      headers,
      body: JSON.stringify(req),
    });

    const contentType = (resp.headers.get('content-type') || '');
    if (contentType.indexOf('spreadsheetml') >= 0) {
      const blob = await resp.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      const dateStr = new Date().toISOString().slice(0, 10).replace(/-/g, '');
      a.href = url;
      a.download = '动态跟进提醒_' + dateStr + '.xlsx';
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
      fudDesRenderResult('<div class="pd-hint">已导出当前页为 Excel（xlsx），请查看下载。</div>');
      return;
    }

    let envelope = null;
    try { envelope = await resp.json(); } catch (e) { /* 忽略解析失败 */ }
    const code = envelope && envelope.code;
    const message = (envelope && envelope.message) || '导出失败';
    if (code === 2000 || code === 2003) {
      if (typeof logout === 'function') logout();
      fudDesRenderResult(fudDesErrorHtml('unauthorized', message));
      return;
    }
    fudDesRenderResult(fudDesErrorHtml(fudDesKindOfCode(code), message));
  } catch (err) {
    fudDesRenderResult(fudDesErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 导出当前页为中文 PDF（ERP-196，只读）：复用预览请求体 POST /api/dynamic-follow-up-due-report/pdf；
   成功（application/pdf 附件）触发下载；授权 / 无效 / 空结果 / 网络失败在结果区可见，不下载任何内容 */
async function fudDesExportPdf() {
  if (!FUD_DYN.view || !FUD_DYN.view.columns || !FUD_DYN.view.columns.length) {
    fudDesRenderResult(fudDesErrorHtml('invalid', '请先预览后再导出 PDF'));
    return;
  }
  if (!FUD_DYN.view.rows || FUD_DYN.view.rows.length === 0) {
    fudDesRenderResult(fudDesErrorHtml('empty', '没有符合筛选条件的跟进提醒证据，无法导出 PDF（请先预览）'));
    return;
  }

  const state = fudDesBuildState(FUD_DYN.view.page);
  const filterError = fudDesFilterError(state);
  if (filterError) {
    fudDesRenderResult(fudDesErrorHtml('invalid', filterError));
    return;
  }
  const req = fudDesBuildRequest(state);
  fudDesRenderResult(fudDesLoadingHtml());

  try {
    const headers = { 'Content-Type': 'application/json' };
    const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
    if (token) headers['Authorization'] = 'Bearer ' + token;
    const resp = await fetch('/api/dynamic-follow-up-due-report/pdf', {
      method: 'POST',
      headers,
      body: JSON.stringify(req),
    });

    const contentType = (resp.headers.get('content-type') || '');
    if (contentType.indexOf('application/pdf') >= 0) {
      const blob = await resp.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      const dateStr = new Date().toISOString().slice(0, 10).replace(/-/g, '');
      a.href = url;
      a.download = '动态跟进提醒_' + dateStr + '.pdf';
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
      fudDesRenderResult('<div class="pd-hint">已导出当前页为中文 PDF，请查看下载。</div>');
      return;
    }

    let envelope = null;
    try { envelope = await resp.json(); } catch (e) { /* 忽略解析失败 */ }
    const code = envelope && envelope.code;
    const message = (envelope && envelope.message) || '导出失败';
    if (code === 2000 || code === 2003) {
      if (typeof logout === 'function') logout();
      fudDesRenderResult(fudDesErrorHtml('unauthorized', message));
      return;
    }
    fudDesRenderResult(fudDesErrorHtml(fudDesKindOfCode(code), message));
  } catch (err) {
    fudDesRenderResult(fudDesErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 下载筛选集状态汇总为 Excel（ERP-203，只读）：复用当前筛选请求体 POST /api/dynamic-follow-up-due-report/status-summary；
   成功（xlsx 附件）触发下载；授权 / 无效 / 网络失败在结果区可见，不下载任何内容。不要求先预览：接口会重跑有界授权预览。 */
async function fudDesExportStatusSummary() {
  const state = fudDesBuildState(FUD_DYN.page);
  const filterError = fudDesFilterError(state);
  if (filterError) {
    fudDesRenderResult(fudDesErrorHtml('invalid', filterError));
    return;
  }
  const req = fudDesBuildRequest(state);
  fudDesRenderResult(fudDesLoadingHtml());

  try {
    const headers = { 'Content-Type': 'application/json' };
    const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
    if (token) headers['Authorization'] = 'Bearer ' + token;
    const resp = await fetch('/api/dynamic-follow-up-due-report/status-summary', {
      method: 'POST',
      headers,
      body: JSON.stringify(req),
    });

    const contentType = (resp.headers.get('content-type') || '');
    if (contentType.indexOf('spreadsheetml') >= 0) {
      const blob = await resp.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      const dateStr = new Date().toISOString().slice(0, 10).replace(/-/g, '');
      a.href = url;
      a.download = '动态跟进提醒状态汇总_' + dateStr + '.xlsx';
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
      fudDesRenderResult('<div class="pd-hint">已下载筛选集状态汇总 Excel（xlsx），请查看下载。</div>');
      return;
    }

    let envelope = null;
    try { envelope = await resp.json(); } catch (e) { /* 忽略解析失败 */ }
    const code = envelope && envelope.code;
    const message = (envelope && envelope.message) || '下载失败';
    if (code === 2000 || code === 2003) {
      if (typeof logout === 'function') logout();
      fudDesRenderResult(fudDesErrorHtml('unauthorized', message));
      return;
    }
    fudDesRenderResult(fudDesErrorHtml(fudDesKindOfCode(code), message));
  } catch (err) {
    fudDesRenderResult(fudDesErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 翻页（有界：最小第 1 页） */
function fudDesPage(delta) {
  const page = (FUD_DYN.view ? FUD_DYN.view.page : FUD_DYN.page) + delta;
  if (page < 1) return;
  fudDesPreview(page);
}

/* 加载字段目录（需登录 + 跟进提醒菜单授权；授权 / 网络失败 fail closed，不渲染任何字段） */
async function loadFollowUpDueDesignerCatalog() {
  try {
    const resp = await fudDesRequest('/api/dynamic-follow-up-due-report');
    if (resp.code === 2000 || resp.code === 2003) {
      if (typeof logout === 'function') logout();
      fudDesRenderResult(fudDesErrorHtml('unauthorized', resp.message));
      return;
    }
    if (resp.code !== 0) {
      fudDesRenderResult(fudDesErrorHtml(fudDesKindOfCode(resp.code), resp.message));
      return;
    }
    FUD_DYN.catalog = resp.data;
    FUD_DYN.fields = (resp.data && resp.data.fields) || [];
    FUD_DYN.selectedKeys = FUD_DYN.fields.map(f => f.key);
    const el = document.getElementById('fud-designer-fields');
    if (el) el.innerHTML = fudDesFieldChooserHtml(FUD_DYN.fields, FUD_DYN.selectedKeys);
  } catch (err) {
    fudDesRenderResult(fudDesErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 打开动态跟进提醒字段设计器（从「跟进提醒」报表工具栏进入） */
function openFollowUpDueDesigner() {
  const el = document.getElementById('fud-designer');
  if (!el) return;
  const today = new Date().toISOString().slice(0, 10);
  el.innerHTML = `
    <div class="pd-hint">🎛 字段设计器（只读预览）：勾选可见列 → 选择到期状态 / as-of 日期 / 提前天数 / 每页条数 → 预览授权有界结果；全程只读，不执行任意 SQL。</div>
    <div class="toolbar" style="margin-top:0">
      <div class="toolbar-left" style="flex-wrap:wrap;gap:6px;align-items:center;font-size:13px">
        <label>到期状态 <select id="fud-des-due-status" onchange="fudDesPreview(1)">
          <option value="">全部状态</option>
          <option value="overdue">已逾期</option>
          <option value="today">今日到期</option>
          <option value="upcoming">即将到期</option>
        </select></label>
        <label>分组 <select id="fud-des-group" onchange="fudDesPreview(1)">
          <option value="none">不分组</option>
          <option value="dueStatus">按到期状态</option>
          <option value="salesman">按业务员</option>
        </select></label>
        <label>客户Id <input type="number" id="fud-des-customer-id" min="1" style="width:90px" placeholder="全部客户"></label>
        <label>关键字 <input type="text" id="fud-des-keyword" maxlength="80" style="width:160px" placeholder="客户名称 / 主题"></label>
        <label>as-of 日期 <input type="date" id="fud-des-asof" value="${today}"></label>
        <label>提前天数 <input type="number" id="fud-des-ahead" value="7" min="0" max="365" style="width:80px"></label>
        <label>每页 <input type="number" id="fud-des-pagesize" value="20" min="1" max="200" style="width:70px"></label>
        <span id="fud-designer-fields">正在加载字段目录…</span>
      </div>
      <div class="toolbar-actions">
        <button class="btn btn-neutral btn-sm" onclick="fudDesToggleAll(true)">全选</button>
        <button class="btn btn-neutral btn-sm" onclick="fudDesToggleAll(false)">清空</button>
        <button class="btn btn-neutral" onclick="fudDesExport()" title="导出当前页为 Excel（选定列，复用当前筛选与分页）">📥 导出 Excel（当前页）</button>
        <button class="btn btn-neutral" onclick="fudDesExportPdf()" title="导出当前页为中文 PDF（选定列，复用当前筛选与分页）">📥 导出 PDF（当前页）</button>
        <button class="btn btn-neutral" onclick="fudDesExportStatusSummary()" title="下载当前筛选集的状态汇总 Excel（已逾期/今日到期/即将到期计数，不含明细行）">📥 状态汇总 Excel</button>
        <button class="btn btn-primary" onclick="fudDesPreview(1)">预览</button>
      </div>
    </div>
    <div id="fud-designer-result"></div>`;
  loadFollowUpDueDesignerCatalog();
}

/* ============ 动态报价成交率字段设计器（ERP-206：只读、有界的前端字段选择与分页预览 / Excel 导出） ============
   口径与后端 ERP-206（DynamicQuotationConversionReportController / DynamicQuotationConversionReportRules）一一对应：
   - 入口复用在「报价成交率分析」报表（reports.js 的 quotation-conversion，designer: 'quotation'），不新增菜单 / 架构 / 脚本注册；
   - 字段选择器只由 GET /api/dynamic-quotation-conversion-report 返回的有限白名单目录渲染为复选框（name="qcd-des-field"），
     绝无自由填写的字段名或 SQL；勾选状态经 qcdSelectFields 规范化（去重、保持顺序、丢弃未知键）；
   - 筛选仅限开始 / 结束日期（含首尾最多 366 天），分页有界（页码 ≥ 1，每页 1~200），
     预览走 POST /api/dynamic-quotation-conversion-report，只发送「白名单字段 + 有界日期 + 有界分页」；
   - 结果按后端返回的列名与选定字段值渲染（qcdTableHtml / qcdResultHtml），全部 HTML 转义；
   - 空页 / 授权撤销（权限不足 / 未登录）/ 无效请求 / 网络失败分别可见，且不暴露范围外数据；
   - 导出复用预览请求体 POST /api/dynamic-quotation-conversion-report/export，成功（xlsx 附件）触发下载；
   - 全程只读：不写库、不迁移、不执行任意 SQL。 */

/* 字段设计器状态（纯数据；DOM 访问只在事件处理函数内部发生） */
let QCD_DYN = {
  catalog: null,      // GET /api/dynamic-quotation-conversion-report 返回的目录 DTO
  fields: [],         // 目录字段（白名单）
  selectedKeys: [],   // 当前勾选的字段键（默认全选）
  view: null,         // 最近一次预览结果
  page: 1,            // 当前预览页（预览 / 翻页复用）
};

function qcdEsc(v) {
  return String(v ?? '').replace(/[&<>"']/g, c => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}

/* 规范化选定字段（fail closed）：只保留目录白名单内的键、去重、保持请求顺序；未知键丢弃，绝不发送任意字段名 */
function qcdSelectFields(catalogFields, selectedKeys) {
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

/* 日期窗口客户端校验（与后端 ValidateDateRange 一致）：必填、结束不早于开始、含首尾最多 366 天 */
function qcdDateError(state) {
  const start = String(state && state.start || '').trim();
  const end = String(state && state.end || '').trim();
  if (!start || !end) return '请填写开始与结束日期';
  const startMs = Date.parse(start);
  const endMs = Date.parse(end);
  if (!Number.isFinite(startMs) || !Number.isFinite(endMs)) return '日期格式无效';
  if (end < start) return '结束日期不能早于开始日期';
  if (Math.round((endMs - startMs) / 86400000) + 1 > 366) return '日期范围最多 366 天（含首尾）';
  return '';
}

/* 组装有界预览请求体：字段只来自目录、日期仅开始 / 结束、分页有界，绝不接受任意字段名或 SQL */
function qcdBuildRequest(state) {
  const fields = qcdSelectFields(state.catalogFields, state.selectedKeys);
  const page = Math.max(1, Math.floor(Number(state.page) || 1));
  const maxPageSize = Number(state.maxPageSize) || 200;
  let pageSize = Math.floor(Number(state.pageSize));
  if (!Number.isFinite(pageSize)) pageSize = 20;
  pageSize = Math.max(1, Math.min(maxPageSize, pageSize));
  const filter = {};
  const customerId = String(state.customerId || '').trim();
  if (customerId) filter.customerId = Number(customerId);
  const salesperson = String(state.salesperson || '').trim();
  if (salesperson) filter.salespersonName = salesperson;
  const currency = String(state.currency || '').trim();
  if (currency) filter.currency = currency;
  return {
    fields,
    page,
    pageSize,
    start: String(state.start).slice(0, 10),
    end: String(state.end).slice(0, 10),
    filter,
  };
}

/* 单元格纯文本：数字合理格式化（整数 / 2 位小数）、其余按字符串呈现（null 显示为空） */
function qcdCellText(value, field) {
  const dataType = (field && field.dataType) || 'text';
  if (value === null || value === undefined) return '';
  if (dataType === 'number') {
    const n = Number(value);
    if (Number.isFinite(n)) return Number.isInteger(n) ? String(n) : n.toFixed(2);
    return String(value);
  }
  return String(value);
}

/* 单元格 HTML（转义后安全渲染） */
function qcdRenderCell(value, field) {
  return qcdEsc(qcdCellText(value, field));
}

/* 结果表格 HTML：表头为返回的列名、单元格为返回的选定字段值，全部经转义 */
function qcdTableHtml(view) {
  const cols = (view && view.columns) || [];
  const rows = (view && view.rows) || [];
  if (!cols.length) return '';
  const align = c => (c.dataType === 'number') ? ' class="text-right"' : '';
  const head = cols.map(c => `<th${align(c)}>${qcdEsc(c.label || c.key)}</th>`).join('');
  const body = rows.length
    ? rows.map(r => `<tr>${cols.map(c => `<td${align(c)}>${qcdRenderCell(r[c.key], c)}</td>`).join('')}</tr>`).join('')
    : '';
  return `<div class="table-wrap" style="margin-top:8px"><table><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table></div>`;
}

/* 分页（有界、稳定）：当前页之外仍有记录时标注截断，翻页复用当前字段 / 日期 / 每页条数 */
function qcdPagingHtml(view) {
  if (!view) return '';
  const prevDisabled = view.page <= 1 ? ' disabled' : '';
  const nextDisabled = view.page >= view.totalPages ? ' disabled' : '';
  return `<div style="display:flex;justify-content:space-between;align-items:center;margin-top:8px">
      <span class="text-muted">第 ${view.page} 页 / 共 ${view.totalPages} 页${view.truncated ? '（仅当前页，后续仍有分页）' : ''}</span>
      <div>
        <button class="btn btn-neutral btn-sm" onclick="qcdPage(-1)"${prevDisabled}>← 上一页</button>
        <button class="btn btn-neutral btn-sm" onclick="qcdPage(1)"${nextDisabled}>下一页 →</button>
      </div></div>`;
}

/* 空结果提示（显式使用后端 emptyText） */
function qcdEmptyHtml(view) {
  return `<div class="empty" style="margin:8px 0">${qcdEsc((view && view.emptyText) || '没有符合所选日期范围的报价成交率数据')}</div>`;
}

/* 错误提示（授权撤销 / 未登录 / 无效请求 / 网络失败分别可见，且不暴露任何数据） */
function qcdErrorHtml(kind, message) {
  const labels = {
    forbidden: '权限不足',
    unauthorized: '未登录 / 登录已过期',
    invalid: '请求无效',
    empty: '导出内容为空',
    network: '网络请求失败',
    error: '预览失败',
  };
  return `<div class="pd-hint" style="color:#b91c1c;background:#fef2f2;border-color:#fecaca">
      <b>${qcdEsc(labels[kind] || '预览失败')}</b>：${qcdEsc(message || '')}</div>`;
}

/* 业务码 → 错误态分类 */
function qcdKindOfCode(code) {
  if (code === 2002) return 'forbidden';
  if (code === 2000 || code === 2003) return 'unauthorized';
  if (code === 5000) return 'error';
  return 'invalid';
}

/* ERP-209 分币种汇总：独立于当前页明细行渲染，仅渲染后端返回的 summary 列与指标，全部转义；空 / 无汇总不渲染 */
function qcdSummaryHtml(view) {
  const summary = view && view.summary;
  const cols = summary && summary.columns;
  const rows = summary && summary.rows;
  if (!cols || !cols.length || !rows || !rows.length) return '';
  const align = c => (c.dataType === 'number') ? ' class="text-right"' : '';
  const head = cols.map(c => `<th${align(c)}>${qcdEsc(c.label || c.key)}</th>`).join('');
  const body = rows.map(r => `<tr>${cols.map(c => `<td${align(c)}>${qcdRenderCell(r[c.key], c)}</td>`).join('')}</tr>`).join('');
  const coverage = summary.coverageText ? `<div class="text-muted" style="margin:6px 0">${qcdEsc(summary.coverageText)}</div>` : '';
  return `<div class="pd-hint" style="margin-top:10px"><b>分币种汇总</b>（按原币合并全部匹配分桶）</div>${coverage}<div class="table-wrap"><table><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table></div>`;
}

/* 预览结果（只读 / 边界 / 免责文案 + 摘要 + 分币种汇总 + 空结果 + 表格 + 分页） */
function qcdResultHtml(view) {
  const readOnly = view && view.readOnlyText ? `<div class="pd-hint">${qcdEsc(view.readOnlyText)}</div>` : '';
  const boundary = view && view.boundaryText ? `<div class="pd-hint">${qcdEsc(view.boundaryText)}</div>` : '';
  const disclaimer = view && view.disclaimerText ? `<div class="pd-hint" style="color:#64748b">${qcdEsc(view.disclaimerText)}</div>` : '';
  const summary = view
    ? `<div class="text-muted" style="margin:6px 0">共 ${view.total} 行 · 第 ${view.page} 页 · 每页 ${view.pageSize} 行 · 共 ${view.totalPages} 页${view.truncated ? ' · 后续仍有分页' : ''} · 期间 ${String(view.start || '').slice(0, 10)} 至 ${String(view.end || '').slice(0, 10)}</div>`
    : '';
  const currencySummary = qcdSummaryHtml(view);
  const empty = view && (!view.rows || view.rows.length === 0) ? qcdEmptyHtml(view) : '';
  return `${readOnly}${boundary}${disclaimer}${summary}${currencySummary}${empty}${qcdTableHtml(view)}${qcdPagingHtml(view)}`;
}

/* 字段选择器：仅由目录白名单渲染为复选框，无自由填写的字段名 */
function qcdFieldChooserHtml(fields, selectedKeys) {
  const selected = new Set(selectedKeys || []);
  return (fields || []).map(f => {
    const checked = selected.has(f.key) ? 'checked' : '';
    return `<label style="display:inline-flex;align-items:center;gap:4px;margin:3px 6px 3px 0;padding:2px 8px;border:1px solid #e2e8f0;border-radius:6px;background:#f8fafc;cursor:pointer">
        <input type="checkbox" name="qcd-des-field" value="${qcdEsc(f.key)}" ${checked} onchange="qcdSyncSelection()">
        <span>${qcdEsc(f.label || f.key)}</span></label>`;
  }).join('');
}

function qcdLoadingHtml() {
  return '<div class="pd-hint" style="text-align:center;color:#64748b">正在预览（只读查询）…</div>';
}

function qcdRenderResult(html) {
  const el = document.getElementById('qcd-designer-result');
  if (el) el.innerHTML = html;
}

/* 轻量请求封装：返回完整 ApiResponse 信封（保留 code），网络异常抛给调用方 */
async function qcdRequest(path, method = 'GET', body = null) {
  const headers = { 'Content-Type': 'application/json' };
  const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
  if (token) headers['Authorization'] = 'Bearer ' + token;
  const opts = { method, headers };
  if (body) opts.body = JSON.stringify(body);
  const resp = await fetch(path, opts);
  return await resp.json();
}

/* 同步勾选状态到 selectedKeys（复选框 onchange） */
function qcdSyncSelection() {
  const boxes = document.querySelectorAll('input[name="qcd-des-field"]');
  QCD_DYN.selectedKeys = Array.from(boxes).filter(b => b.checked).map(b => b.value);
}

function qcdToggleAll(checked) {
  const boxes = document.querySelectorAll('input[name="qcd-des-field"]');
  QCD_DYN.selectedKeys = [];
  boxes.forEach(b => { b.checked = checked; if (checked) QCD_DYN.selectedKeys.push(b.value); });
}

/* 读取当前字段 / 日期 / 分页状态（预览、翻页与导出复用，单一来源） */
function qcdBuildState(page) {
  const val = id => { const el = document.getElementById(id); return el ? el.value.trim() : ''; };
  return {
    catalogFields: QCD_DYN.fields,
    selectedKeys: QCD_DYN.selectedKeys,
    start: val('qcd-des-start'),
    end: val('qcd-des-end'),
    customerId: val('qcd-des-customer-id'),
    salesperson: val('qcd-des-salesperson'),
    currency: val('qcd-des-currency'),
    pageSize: val('qcd-des-pagesize'),
    page: page || QCD_DYN.page || 1,
    maxPageSize: QCD_DYN.catalog && QCD_DYN.catalog.maxPageSize ? QCD_DYN.catalog.maxPageSize : 200,
  };
}

/* 预览：组装有界请求 → POST → 安全渲染列名与单元格；授权 / 无效 / 空 / 网络失败均可见 */
async function qcdPreview(page) {
  const state = qcdBuildState(page);
  const dateError = qcdDateError(state);
  if (dateError) {
    qcdRenderResult(qcdErrorHtml('invalid', dateError));
    return;
  }
  const req = qcdBuildRequest(state);
  QCD_DYN.page = req.page;

  qcdRenderResult(qcdLoadingHtml());

  try {
    const resp = await qcdRequest('/api/dynamic-quotation-conversion-report', 'POST', req);
    if (resp.code === 0) {
      QCD_DYN.view = resp.data;
      QCD_DYN.page = resp.data.page;
      qcdRenderResult(qcdResultHtml(resp.data));
    } else if (resp.code === 2000 || resp.code === 2003) {
      if (typeof logout === 'function') logout();
      qcdRenderResult(qcdErrorHtml('unauthorized', resp.message));
    } else {
      qcdRenderResult(qcdErrorHtml(qcdKindOfCode(resp.code), resp.message));
    }
  } catch (err) {
    qcdRenderResult(qcdErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 导出当前页选定列为 Excel（ERP-206，只读）：复用预览请求体 POST /api/dynamic-quotation-conversion-report/export；
   成功（xlsx 附件）触发下载；授权 / 无效 / 空结果 / 网络失败在结果区可见，不下载任何内容 */
async function qcdExport() {
  if (!QCD_DYN.view || !QCD_DYN.view.columns || !QCD_DYN.view.columns.length) {
    qcdRenderResult(qcdErrorHtml('invalid', '请先预览后再导出 Excel'));
    return;
  }
  if (!QCD_DYN.view.rows || QCD_DYN.view.rows.length === 0) {
    qcdRenderResult(qcdErrorHtml('empty', '没有符合所选日期范围的报价成交率数据，无法导出（请先预览）'));
    return;
  }

  const state = qcdBuildState(QCD_DYN.view.page);
  const dateError = qcdDateError(state);
  if (dateError) {
    qcdRenderResult(qcdErrorHtml('invalid', dateError));
    return;
  }
  const req = qcdBuildRequest(state);
  qcdRenderResult(qcdLoadingHtml());

  try {
    const headers = { 'Content-Type': 'application/json' };
    const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
    if (token) headers['Authorization'] = 'Bearer ' + token;
    const resp = await fetch('/api/dynamic-quotation-conversion-report/export', {
      method: 'POST',
      headers,
      body: JSON.stringify(req),
    });

    const contentType = (resp.headers.get('content-type') || '');
    if (contentType.indexOf('spreadsheetml') >= 0) {
      const blob = await resp.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      const dateStr = new Date().toISOString().slice(0, 10).replace(/-/g, '');
      a.href = url;
      a.download = '报价成交率_' + dateStr + '.xlsx';
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
      qcdRenderResult('<div class="pd-hint">已导出当前页为 Excel（xlsx），请查看下载。</div>');
      return;
    }

    let envelope = null;
    try { envelope = await resp.json(); } catch (e) { /* 忽略解析失败 */ }
    const code = envelope && envelope.code;
    const message = (envelope && envelope.message) || '导出失败';
    if (code === 2000 || code === 2003) {
      if (typeof logout === 'function') logout();
      qcdRenderResult(qcdErrorHtml('unauthorized', message));
      return;
    }
    qcdRenderResult(qcdErrorHtml(qcdKindOfCode(code), message));
  } catch (err) {
    qcdRenderResult(qcdErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 导出当前页选定列为 PDF（ERP-207，只读）：复用预览请求体 POST /api/dynamic-quotation-conversion-report/pdf；
   成功（application/pdf 附件）触发下载；授权 / 无效 / 空结果 / 网络失败在结果区可见，不下载任何内容 */
async function qcdExportPdf() {
  if (!QCD_DYN.view || !QCD_DYN.view.columns || !QCD_DYN.view.columns.length) {
    qcdRenderResult(qcdErrorHtml('invalid', '请先预览后再导出 PDF'));
    return;
  }
  if (!QCD_DYN.view.rows || QCD_DYN.view.rows.length === 0) {
    qcdRenderResult(qcdErrorHtml('empty', '没有符合所选日期范围的报价成交率数据，无法导出 PDF（请先预览）'));
    return;
  }

  const state = qcdBuildState(QCD_DYN.view.page);
  const dateError = qcdDateError(state);
  if (dateError) {
    qcdRenderResult(qcdErrorHtml('invalid', dateError));
    return;
  }
  const req = qcdBuildRequest(state);
  qcdRenderResult(qcdLoadingHtml());

  try {
    const headers = { 'Content-Type': 'application/json' };
    const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
    if (token) headers['Authorization'] = 'Bearer ' + token;
    const resp = await fetch('/api/dynamic-quotation-conversion-report/pdf', {
      method: 'POST',
      headers,
      body: JSON.stringify(req),
    });

    const contentType = (resp.headers.get('content-type') || '');
    if (contentType.indexOf('application/pdf') >= 0) {
      const blob = await resp.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      const dateStr = new Date().toISOString().slice(0, 10).replace(/-/g, '');
      a.href = url;
      a.download = '报价成交率_' + dateStr + '.pdf';
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
      qcdRenderResult('<div class="pd-hint">已导出当前页为中文 PDF，请查看下载。</div>');
      return;
    }

    let envelope = null;
    try { envelope = await resp.json(); } catch (e) { /* 忽略解析失败 */ }
    const code = envelope && envelope.code;
    const message = (envelope && envelope.message) || '导出失败';
    if (code === 2000 || code === 2003) {
      if (typeof logout === 'function') logout();
      qcdRenderResult(qcdErrorHtml('unauthorized', message));
      return;
    }
    qcdRenderResult(qcdErrorHtml(qcdKindOfCode(code), message));
  } catch (err) {
    qcdRenderResult(qcdErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 下载分币种汇总 Excel（ERP-210，只读）：复用当前字段 / 日期 / 应用筛选组装请求体 POST
   /api/dynamic-quotation-conversion-report/export-summary；成功（xlsx 附件）触发下载；
   授权 / 无效 / 网络失败在结果区可见，不下载任何内容。区别于「导出 Excel（当前页）」：
   本按钮导出服务端在全部匹配分桶上派生的分币种汇总，无需先预览、绝不含明细行。 */
async function qcdExportSummary() {
  const state = qcdBuildState(QCD_DYN.page);
  const dateError = qcdDateError(state);
  if (dateError) {
    qcdRenderResult(qcdErrorHtml('invalid', dateError));
    return;
  }
  const req = qcdBuildRequest(state);
  qcdRenderResult(qcdLoadingHtml());

  try {
    const headers = { 'Content-Type': 'application/json' };
    const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
    if (token) headers['Authorization'] = 'Bearer ' + token;
    const resp = await fetch('/api/dynamic-quotation-conversion-report/export-summary', {
      method: 'POST',
      headers,
      body: JSON.stringify(req),
    });

    const contentType = (resp.headers.get('content-type') || '');
    if (contentType.indexOf('spreadsheetml') >= 0) {
      const blob = await resp.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      const dateStr = new Date().toISOString().slice(0, 10).replace(/-/g, '');
      a.href = url;
      a.download = '报价成交率分币种汇总_' + dateStr + '.xlsx';
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
      qcdRenderResult('<div class="pd-hint">已下载分币种汇总 Excel（按原币合并全部匹配分桶），请查看下载。</div>');
      return;
    }

    let envelope = null;
    try { envelope = await resp.json(); } catch (e) { /* 忽略解析失败 */ }
    const code = envelope && envelope.code;
    const message = (envelope && envelope.message) || '导出失败';
    if (code === 2000 || code === 2003) {
      if (typeof logout === 'function') logout();
      qcdRenderResult(qcdErrorHtml('unauthorized', message));
      return;
    }
    qcdRenderResult(qcdErrorHtml(qcdKindOfCode(code), message));
  } catch (err) {
    qcdRenderResult(qcdErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}
/* 下载分币种汇总 PDF（ERP-211，只读）：复用当前字段 / 日期 / 应用筛选组装请求体 POST
   /api/dynamic-quotation-conversion-report/export-summary-pdf；成功（pdf 附件）触发下载；
   授权 / 无效 / 网络失败在结果区可见，不下载任何内容。区别于「导出 PDF（当前页）」与「分币种汇总 Excel」：
   本按钮导出服务端在全部匹配分桶上派生的分币种汇总（无需先预览、绝不含明细行、绝不跨币种合计）。 */
async function qcdExportSummaryPdf() {
  const state = qcdBuildState(QCD_DYN.page);
  const dateError = qcdDateError(state);
  if (dateError) {
    qcdRenderResult(qcdErrorHtml('invalid', dateError));
    return;
  }
  const req = qcdBuildRequest(state);
  qcdRenderResult(qcdLoadingHtml());

  try {
    const headers = { 'Content-Type': 'application/json' };
    const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
    if (token) headers['Authorization'] = 'Bearer ' + token;
    const resp = await fetch('/api/dynamic-quotation-conversion-report/export-summary-pdf', {
      method: 'POST',
      headers,
      body: JSON.stringify(req),
    });

    const contentType = (resp.headers.get('content-type') || '');
    if (contentType.indexOf('application/pdf') >= 0) {
      const blob = await resp.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      const dateStr = new Date().toISOString().slice(0, 10).replace(/-/g, '');
      a.href = url;
      a.download = '报价成交率分币种汇总_' + dateStr + '.pdf';
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
      qcdRenderResult('<div class="pd-hint">已下载分币种汇总 PDF（按原币合并全部匹配分桶），请查看下载。</div>');
      return;
    }

    let envelope = null;
    try { envelope = await resp.json(); } catch (e) { /* 忽略解析失败 */ }
    const code = envelope && envelope.code;
    const message = (envelope && envelope.message) || '导出失败';
    if (code === 2000 || code === 2003) {
      if (typeof logout === 'function') logout();
      qcdRenderResult(qcdErrorHtml('unauthorized', message));
      return;
    }
    qcdRenderResult(qcdErrorHtml(qcdKindOfCode(code), message));
  } catch (err) {
    qcdRenderResult(qcdErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}



/* 翻页（有界：最小第 1 页） */
function qcdPage(delta) {
  const page = (QCD_DYN.view ? QCD_DYN.view.page : QCD_DYN.page) + delta;
  if (page < 1) return;
  qcdPreview(page);
}

/* 加载字段目录（需登录 + 报价单菜单授权；授权 / 网络失败 fail closed，不渲染任何字段） */
async function loadQuotationConversionDesignerCatalog() {
  try {
    const resp = await qcdRequest('/api/dynamic-quotation-conversion-report');
    if (resp.code === 2000 || resp.code === 2003) {
      if (typeof logout === 'function') logout();
      qcdRenderResult(qcdErrorHtml('unauthorized', resp.message));
      return;
    }
    if (resp.code !== 0) {
      qcdRenderResult(qcdErrorHtml(qcdKindOfCode(resp.code), resp.message));
      return;
    }
    QCD_DYN.catalog = resp.data;
    QCD_DYN.fields = (resp.data && resp.data.fields) || [];
    QCD_DYN.selectedKeys = QCD_DYN.fields.map(f => f.key);
    const el = document.getElementById('qcd-designer-fields');
    if (el) el.innerHTML = qcdFieldChooserHtml(QCD_DYN.fields, QCD_DYN.selectedKeys);
  } catch (err) {
    qcdRenderResult(qcdErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 打开动态报价成交率字段设计器（从「报价成交率分析」报表工具栏进入） */
function openQuotationConversionDesigner() {
  const el = document.getElementById('qcd-designer');
  if (!el) return;
  const defStart = new Date(Date.now() - 365 * 86400000).toISOString().slice(0, 10);
  const defEnd = new Date().toISOString().slice(0, 10);
  el.innerHTML = `
    <div class="pd-hint">🎛 字段设计器（只读预览）：勾选可见列 → 选择开始 / 结束日期与每页条数 → 预览授权有界结果（业务员 × 原币分桶，金额绝不跨币种合计）；全程只读，不执行任意 SQL。</div>
    <div class="toolbar" style="margin-top:0">
      <div class="toolbar-left" style="flex-wrap:wrap;gap:6px;align-items:center;font-size:13px">
        <label>开始日期 <input type="date" id="qcd-des-start" value="${defStart}"></label>
        <label>结束日期 <input type="date" id="qcd-des-end" value="${defEnd}"></label>
        <label>客户 Id <input type="number" id="qcd-des-customer-id" min="1" style="width:90px" placeholder="全部客户" onchange="qcdPreview(1)"></label>
        <label>业务员关键字 <input type="text" id="qcd-des-salesperson" maxlength="80" style="width:160px" placeholder="业务员姓名" onchange="qcdPreview(1)"></label>
        <label>币种 <select id="qcd-des-currency" onchange="qcdPreview(1)">
          <option value="">全部币种</option>
          <option value="CNY">CNY 人民币</option>
          <option value="USD">USD 美元</option>
          <option value="EUR">EUR 欧元</option>
          <option value="HKD">HKD 港币</option>
          <option value="GBP">GBP 英镑</option>
          <option value="JPY">JPY 日元</option>
          <option value="unknown">未知币种</option>
        </select></label>
        <label>每页 <input type="number" id="qcd-des-pagesize" value="20" min="1" max="200" style="width:70px"></label>
        <span id="qcd-designer-fields">正在加载字段目录…</span>
      </div>
      <div class="toolbar-actions">
        <button class="btn btn-neutral btn-sm" onclick="qcdToggleAll(true)">全选</button>
        <button class="btn btn-neutral btn-sm" onclick="qcdToggleAll(false)">清空</button>
        <button class="btn btn-neutral" onclick="qcdExport()" title="导出当前页为 Excel（选定列，复用当前日期与分页）">📥 导出 Excel（当前页）</button>
        <button class="btn btn-neutral" onclick="qcdExportPdf()" title="导出当前页为中文 PDF（选定列，复用当前日期与分页）">📥 导出 PDF（当前页）</button>
        <button class="btn btn-neutral" onclick="qcdExportSummary()" title="下载分币种汇总 Excel（按原币合并全部匹配分桶，不含明细行；区别于当前页导出）">📥 分币种汇总 Excel</button>
        <button class="btn btn-neutral" onclick="qcdExportSummaryPdf()" title="下载分币种汇总中文 PDF（按原币合并全部匹配分桶，复用当前字段与筛选；区别于当前页 PDF）">📥 分币种汇总 PDF</button>
        <button class="btn btn-primary" onclick="qcdPreview(1)">预览</button>
      </div>
    </div>
    <div id="qcd-designer-result"></div>`;
  loadQuotationConversionDesignerCatalog();
}

/* ============ 动态商品销量排名字段设计器（ERP-213：只读、有界的前端字段选择与 Top 预览 / Excel 导出） ============
   口径与后端 ERP-213（DynamicProductSalesRankingReportController / DynamicProductSalesRankingReportRules）一一对应：
   - 入口复用在「商品销量排名榜」报表（reports.js 的 product-sales-ranking，designer: 'product-sales-ranking'），不新增菜单 / 架构 / 脚本注册；
   - 字段选择器只由 GET /api/dynamic-product-sales-ranking-report 返回的有限白名单目录渲染为复选框（name="psr-des-field"），
     绝无自由填写的字段名或 SQL；勾选状态经 psrSelectFields 规范化（去重、保持顺序、丢弃未知键）；
   - 筛选仅限开始 / 结束日期（含首尾最多 366 天）与 Top（1~200），预览走 POST /api/dynamic-product-sales-ranking-report，
     只发送「白名单字段 + 有界日期 + 有界 Top」；金额估算已排除、单位不兼容不跨单位合计；
   - 结果按后端返回的列名与选定字段值渲染（psrTableHtml / psrResultHtml），全部 HTML 转义；
   - 空结果 / 授权撤销（权限不足 / 未登录）/ 无效请求 / 网络失败分别可见，且不暴露范围外数据；
   - 导出复用预览请求体 POST /api/dynamic-product-sales-ranking-report/export，成功（xlsx 附件）触发下载；
   - 下载复用当前字段 / 日期 / Top 组装请求体 POST /api/dynamic-product-sales-ranking-report/pdf，成功（application/pdf 附件）触发下载；
   - 全程只读：不写库、不迁移、不执行任意 SQL。 */

/* 字段设计器状态（纯数据；DOM 访问只在事件处理函数内部发生） */
let PSR_DYN = {
  catalog: null,      // GET /api/dynamic-product-sales-ranking-report 返回的目录 DTO
  fields: [],         // 目录字段（白名单）
  selectedKeys: [],   // 当前勾选的字段键（默认全选）
  view: null,         // 最近一次预览结果
};

function psrEsc(v) {
  return String(v ?? '').replace(/[&<>"']/g, c => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}

/* 规范化选定字段（fail closed）：只保留目录白名单内的键、去重、保持请求顺序；未知键丢弃，绝不发送任意字段名 */
function psrSelectFields(catalogFields, selectedKeys) {
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

/* 日期窗口客户端校验（与后端 ValidateDateRange 一致）：必填、结束不早于开始、含首尾最多 366 天 */
function psrDateError(state) {
  const start = String(state && state.start || '').trim();
  const end = String(state && state.end || '').trim();
  if (!start || !end) return '请填写开始与结束日期';
  const startMs = Date.parse(start);
  const endMs = Date.parse(end);
  if (Number.isNaN(startMs) || Number.isNaN(endMs)) return '日期格式无效';
  if (endMs < startMs) return '结束日期不能早于开始日期';
  const days = Math.floor((endMs - startMs) / 86400000) + 1;
  if (days > 366) return '日期范围最多 366 天（含首尾）';
  return '';
}

/* Top 客户端校验（与后端 ValidateTop 一致）：1 ~ 200，超出直接拒绝 */
function psrTopError(state) {
  const s = String(state && state.top || '').trim();
  if (s === '') return '';
  const n = Number(s);
  if (!Number.isInteger(n)) return 'Top 必须是整数';
  const maxTop = Number(state && state.maxTop) || 200;
  if (n < 1 || n > maxTop) return 'Top 必须在 1 到 ' + maxTop + ' 之间';
  return '';
}

/* 日期 / Top / 客户 Id / 商品 Id / 单位客户端校验错误文案（空串 = 通过） */
function psrFilterError(state) {
  const dateError = psrDateError(state);
  if (dateError) return dateError;
  const topError = psrTopError(state);
  if (topError) return topError;

  const customerId = String(state && state.customerId || '').trim();
  const productId = String(state && state.productId || '').trim();
  const unit = String(state && state.unit || '').trim();

  if (customerId !== '') {
    const c = Number(customerId);
    if (!Number.isInteger(c) || c <= 0) return '客户 Id 必须是正整数（大于 0）';
  }
  if (productId !== '') {
    const p = Number(productId);
    if (!Number.isInteger(p) || p <= 0) return '商品 Id 必须是正整数（大于 0）';
  }
  if (unit !== '') {
    if (unit.length > 30) return '单位筛选最多 30 个字符';
    for (const ch of unit) {
      const code = ch.codePointAt(0);
      if (code < 0x20 || code === 0x7f) return '单位筛选不能包含控制字符';
    }
  }
  return '';
}

/* 分组键白名单（与后端 NormalizeGroupBy 一致：none / unit；未知取值不回传，由后端 fail closed 兜底） */
function psrGroupKey(v) {
  const s = String(v || '').trim();
  return (s === 'none' || s === 'unit') ? s : '';
}

/* 读取当前字段 / 日期 / Top 状态（预览与导出复用，单一来源） */
function psrBuildState() {
  const val = id => { const el = document.getElementById(id); return el ? el.value.trim() : ''; };
  return {
    catalogFields: PSR_DYN.fields,
    selectedKeys: PSR_DYN.selectedKeys,
    start: val('psr-des-start'),
    end: val('psr-des-end'),
    top: val('psr-des-top'),
    customerId: val('psr-des-customer-id'),
    productId: val('psr-des-product-id'),
    unit: val('psr-des-unit'),
    groupBy: val('psr-des-group'),
    maxTop: PSR_DYN.catalog && PSR_DYN.catalog.maxTop ? PSR_DYN.catalog.maxTop : 200,
  };
}

/* 组装可选应用筛选（只发送规范化后的客户 Id / 商品 Id / 单位；全部留空 = null，保持既有排名行为） */
function psrBuildFilter(state) {
  const customerId = String(state && state.customerId || '').trim();
  const productId = String(state && state.productId || '').trim();
  const unit = String(state && state.unit || '').trim();
  const filter = {};
  if (customerId !== '') filter.customerId = Number(customerId);
  if (productId !== '') filter.productId = Number(productId);
  if (unit !== '') filter.unit = unit;
  return (filter.customerId === undefined && filter.productId === undefined && filter.unit === undefined)
    ? null
    : filter;
}

/* 组装有界预览请求体：字段只来自目录、日期有界、Top 有界（1~200）、筛选有界、分组仅白名单，绝不接受任意字段名或 SQL */
function psrBuildRequest(state) {
  const fields = psrSelectFields(state.catalogFields, state.selectedKeys);
  let top = Math.floor(Number(state.top));
  if (!Number.isFinite(top)) top = 10;
  const maxTop = Number(state.maxTop) || 200;
  top = Math.max(1, Math.min(maxTop, top));
  const req = {
    fields,
    start: String(state.start).slice(0, 10),
    end: String(state.end).slice(0, 10),
    top,
    filter: psrBuildFilter(state),
  };
  const groupBy = psrGroupKey(state.groupBy);
  if (groupBy && groupBy !== 'none') req.groupBy = groupBy;
  return req;
}

/* 单元格纯文本：数字合理格式化（整数 / 2 位小数）、其余按字符串呈现（null 显示为空） */
function psrCellText(value, field) {
  const dataType = (field && field.dataType) || 'text';
  if (value === null || value === undefined) return '';
  if (dataType === 'number') {
    const n = Number(value);
    if (Number.isFinite(n)) return Number.isInteger(n) ? String(n) : n.toFixed(2);
    return String(value);
  }
  return String(value);
}

/* 单元格 HTML（转义后安全渲染） */
function psrRenderCell(value, field) {
  return psrEsc(psrCellText(value, field));
}

/* 结果表格 HTML：表头为返回的列名、单元格为返回的选定字段值，全部经转义 */
function psrTableHtml(view) {
  const cols = (view && view.columns) || [];
  const rows = (view && view.rows) || [];
  if (!cols.length) return '';
  const align = c => (c.dataType === 'number') ? ' class="text-right"' : '';
  const head = cols.map(c => `<th${align(c)}>${psrEsc(c.label || c.key)}</th>`).join('');
  const body = rows.length
    ? rows.map(r => `<tr>${cols.map(c => `<td${align(c)}>${psrRenderCell(r[c.key], c)}</td>`).join('')}</tr>`).join('')
    : '';
  return `<div class="table-wrap" style="margin-top:8px"><table><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table></div>`;
}

/* 空结果提示（显式使用后端 emptyText） */
function psrEmptyHtml(view) {
  return `<div class="empty" style="margin:8px 0">${psrEsc((view && view.emptyText) || '没有符合日期范围与数据范围的已审核发货证据')}</div>`;
}

/* 按单位分组汇总（ERP-216）：仅针对当前 Top 结果（绝非完整日期范围），按精确单位独立呈现排名桶数与同单位签名数量小计；
   未知 / 空单位仅呈现桶数（数量显示为 —，null 语义）；绝不跨单位合计数量、绝不含金额；无跨单位总计与比较图；标签与数值全部转义 */
function psrGroupsHtml(view) {
  const groupBy = view && view.groupBy;
  const groups = (view && view.groups) || [];
  if (!groupBy || groupBy === 'none' || !Array.isArray(groups) || groups.length === 0) return '';
  const context = view && view.groupContextText
    ? `<div class="pd-hint" style="color:#b45309;background:#fffbeb;border-color:#fde68a">📊 ${psrEsc(view.groupContextText)}</div>`
    : '';
  const rows = groups.map(g => {
    const label = (g && g.label) || (g && g.unit) || '';
    const count = Number(g && g.rankingBucketCount) || 0;
    const hasQty = g && g.totalQuantity !== null && g.totalQuantity !== undefined;
    const qty = hasQty ? psrCellText(g.totalQuantity, { dataType: 'number' }) : '—';
    return `<tr>
      <td>${psrEsc(label)}</td>
      <td class="text-right">${count}</td>
      <td class="text-right">${psrEsc(qty)}</td>
    </tr>`;
  }).join('');
  return `${context}<div class="table-wrap" style="margin-top:8px"><table>
    <thead><tr><th>单位</th><th class="text-right">排名桶数</th><th class="text-right">同单位数量小计</th></tr></thead>
    <tbody>${rows}</tbody></table></div>`;
}

/* 错误提示（授权撤销 / 未登录 / 无效请求 / 网络失败分别可见，且不暴露任何数据） */
function psrErrorHtml(kind, message) {
  const labels = {
    forbidden: '权限不足',
    unauthorized: '未登录 / 登录已过期',
    invalid: '请求无效',
    empty: '导出内容为空',
    network: '网络请求失败',
    error: '预览失败',
  };
  return `<div class="pd-hint" style="color:#b91c1c;background:#fef2f2;border-color:#fecaca">
      <b>${psrEsc(labels[kind] || '预览失败')}</b>：${psrEsc(message || '')}</div>`;
}

/* 业务码 → 错误态分类 */
function psrKindOfCode(code) {
  if (code === 2002) return 'forbidden';
  if (code === 2000 || code === 2003) return 'unauthorized';
  if (code === 5000) return 'error';
  return 'invalid';
}

function psrLoadingHtml() {
  return '<div class="pd-hint" style="text-align:center;color:#64748b">正在预览（只读查询）…</div>';
}

function psrRenderResult(html) {
  const el = document.getElementById('psr-designer-result');
  if (el) el.innerHTML = html;
}

/* 轻量请求封装：返回完整 ApiResponse 信封（保留 code），网络异常抛给调用方 */
async function psrRequest(path, method = 'GET', body = null) {
  const headers = { 'Content-Type': 'application/json' };
  const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
  if (token) headers['Authorization'] = 'Bearer ' + token;
  const opts = { method, headers };
  if (body) opts.body = JSON.stringify(body);
  const resp = await fetch(path, opts);
  return await resp.json();
}

/* 字段选择器：仅由目录白名单渲染为复选框，无自由填写的字段名 */
function psrFieldChooserHtml(fields, selectedKeys) {
  const selected = new Set(selectedKeys || []);
  return (fields || []).map(f => {
    const checked = selected.has(f.key) ? 'checked' : '';
    return `<label style="display:inline-flex;align-items:center;gap:4px;margin:3px 6px 3px 0;padding:2px 8px;border:1px solid #e2e8f0;border-radius:6px;background:#f8fafc;cursor:pointer">
        <input type="checkbox" name="psr-des-field" value="${psrEsc(f.key)}" ${checked} onchange="psrSyncSelection()">
        <span>${psrEsc(f.label || f.key)}</span></label>`;
  }).join('');
}

/* 预览结果（只读 / 边界 / 免责 / Top 限定 / 发货证据 / 单位口径 / 空结果 + 表格） */
function psrResultHtml(view) {
  const readOnly = view && view.readOnlyText ? `<div class="pd-hint">${psrEsc(view.readOnlyText)}</div>` : '';
  const boundary = view && view.boundaryText ? `<div class="pd-hint">${psrEsc(view.boundaryText)}</div>` : '';
  const disclaimer = view && view.disclaimerText ? `<div class="pd-hint" style="color:#64748b">${psrEsc(view.disclaimerText)}</div>` : '';
  const topLine = view
    ? `<div class="text-muted" style="margin:6px 0">Top ${psrEsc(view.top)} 限定 · 返回 ${psrEsc(view.total)} 行${view.topLimited ? ' · 可能存在 Top 之外更多排名（不声称完整）' : ' · 已覆盖全部匹配排名'} · 期间 ${psrEsc(String(view.start || '').slice(0, 10))} 至 ${psrEsc(String(view.end || '').slice(0, 10))}</div>`
    : '';
  const filterLine = view && view.filterText ? `<div class="pd-hint" style="color:#0f766e;background:#f0fdfa;border-color:#99f6e4">🔍 ${psrEsc(view.filterText)}</div>` : '';
  const approved = view && view.approvedShipmentText ? `<div class="pd-hint">✅ ${psrEsc(view.approvedShipmentText)}</div>` : '';
  const unit = view && view.unitContextText ? `<div class="pd-hint" style="color:#b45309;background:#fffbeb;border-color:#fde68a">⚠️ ${psrEsc(view.unitContextText)}</div>` : '';
  const groups = psrGroupsHtml(view);
  const empty = view && (!view.rows || view.rows.length === 0) ? psrEmptyHtml(view) : '';
  return `${readOnly}${boundary}${disclaimer}${filterLine}${topLine}${approved}${unit}${groups}${empty}${psrTableHtml(view)}`;
}

/* 同步勾选状态到 selectedKeys（复选框 onchange） */
function psrSyncSelection() {
  const boxes = document.querySelectorAll('input[name="psr-des-field"]');
  PSR_DYN.selectedKeys = Array.from(boxes).filter(b => b.checked).map(b => b.value);
}

function psrToggleAll(checked) {
  const boxes = document.querySelectorAll('input[name="psr-des-field"]');
  PSR_DYN.selectedKeys = [];
  boxes.forEach(b => { b.checked = checked; if (checked) PSR_DYN.selectedKeys.push(b.value); });
}

/* 预览：组装有界请求 → POST → 安全渲染列名与单元格；授权 / 无效 / 空 / 网络失败均可见 */
async function psrPreview() {
  const state = psrBuildState();
  const filterError = psrFilterError(state);
  if (filterError) {
    psrRenderResult(psrErrorHtml('invalid', filterError));
    return;
  }
  const req = psrBuildRequest(state);
  psrRenderResult(psrLoadingHtml());

  try {
    const resp = await psrRequest('/api/dynamic-product-sales-ranking-report', 'POST', req);
    if (resp.code === 0) {
      PSR_DYN.view = resp.data;
      psrRenderResult(psrResultHtml(resp.data));
    } else if (resp.code === 2000 || resp.code === 2003) {
      if (typeof logout === 'function') logout();
      psrRenderResult(psrErrorHtml('unauthorized', resp.message));
    } else {
      psrRenderResult(psrErrorHtml(psrKindOfCode(resp.code), resp.message));
    }
  } catch (err) {
    psrRenderResult(psrErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 导出选定 Top 结果为 Excel（ERP-213 / ERP-217，只读）：复用当前字段 / 分组 / 开始 / 结束日期 / Top / 筛选组装请求体
   POST /api/dynamic-product-sales-ranking-report/export；接口会重新校验并重跑有界授权预览（不要求先预览），
   分组键随请求下发（none 时保持既有单工作表导出，unit 时服务端追加「单位汇总」工作表）；
   成功（xlsx 附件）触发下载；授权 / 无效 / 网络失败在结果区可见，不下载任何内容，且不清空 / 不覆盖设计器表单状态 */
async function psrExport() {
  const state = psrBuildState();
  const filterError = psrFilterError(state);
  if (filterError) {
    psrRenderResult(psrErrorHtml('invalid', filterError));
    return;
  }
  const req = psrBuildRequest(state);
  psrRenderResult(psrLoadingHtml());

  try {
    const headers = { 'Content-Type': 'application/json' };
    const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
    if (token) headers['Authorization'] = 'Bearer ' + token;
    const resp = await fetch('/api/dynamic-product-sales-ranking-report/export', {
      method: 'POST',
      headers,
      body: JSON.stringify(req),
    });

    const contentType = (resp.headers.get('content-type') || '');
    if (contentType.indexOf('spreadsheetml') >= 0) {
      const blob = await resp.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      const dateStr = new Date().toISOString().slice(0, 10).replace(/-/g, '');
      a.href = url;
      a.download = '商品销量排名_' + dateStr + '.xlsx';
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
      psrRenderResult('<div class="pd-hint">已导出 Top 结果为 Excel（xlsx），请查看下载。</div>');
      return;
    }

    let envelope = null;
    try { envelope = await resp.json(); } catch (e) { /* 忽略解析失败 */ }
    const code = envelope && envelope.code;
    const message = (envelope && envelope.message) || '导出失败';
    if (code === 2000 || code === 2003) {
      if (typeof logout === 'function') logout();
      psrRenderResult(psrErrorHtml('unauthorized', message));
      return;
    }
    psrRenderResult(psrErrorHtml(psrKindOfCode(code), message));
  } catch (err) {
    psrRenderResult(psrErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 下载选定 Top 结果为中文 PDF（ERP-214，只读）：复用当前字段 / 开始 / 结束日期 / Top 组装请求体
   POST /api/dynamic-product-sales-ranking-report/pdf；接口会重新校验并重跑有界授权预览（不要求先预览）；
   成功（application/pdf 附件）触发下载；授权 / 无效 / 字体缺失 / 网络失败在结果区可见，不下载任何内容 */
async function psrExportPdf() {
  const state = psrBuildState();
  const filterError = psrFilterError(state);
  if (filterError) {
    psrRenderResult(psrErrorHtml('invalid', filterError));
    return;
  }
  const req = psrBuildRequest(state);
  psrRenderResult(psrLoadingHtml());

  try {
    const headers = { 'Content-Type': 'application/json' };
    const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
    if (token) headers['Authorization'] = 'Bearer ' + token;
    const resp = await fetch('/api/dynamic-product-sales-ranking-report/pdf', {
      method: 'POST',
      headers,
      body: JSON.stringify(req),
    });

    const contentType = (resp.headers.get('content-type') || '');
    if (contentType.indexOf('application/pdf') >= 0) {
      const blob = await resp.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      const dateStr = new Date().toISOString().slice(0, 10).replace(/-/g, '');
      a.href = url;
      a.download = '商品销量排名_' + dateStr + '.pdf';
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
      psrRenderResult('<div class="pd-hint">已下载 Top 结果为中文 PDF，请查看下载。</div>');
      return;
    }

    let envelope = null;
    try { envelope = await resp.json(); } catch (e) { /* 忽略解析失败 */ }
    const code = envelope && envelope.code;
    const message = (envelope && envelope.message) || '下载失败';
    if (code === 2000 || code === 2003) {
      if (typeof logout === 'function') logout();
      psrRenderResult(psrErrorHtml('unauthorized', message));
      return;
    }
    psrRenderResult(psrErrorHtml(psrKindOfCode(code), message));
  } catch (err) {
    psrRenderResult(psrErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 加载字段目录（需登录 + 商品销量排名榜菜单授权；授权 / 网络失败 fail closed，不渲染任何字段） */
async function loadProductSalesRankingDesignerCatalog() {
  try {
    const resp = await psrRequest('/api/dynamic-product-sales-ranking-report');
    if (resp.code === 2000 || resp.code === 2003) {
      if (typeof logout === 'function') logout();
      psrRenderResult(psrErrorHtml('unauthorized', resp.message));
      return;
    }
    if (resp.code !== 0) {
      psrRenderResult(psrErrorHtml(psrKindOfCode(resp.code), resp.message));
      return;
    }
    PSR_DYN.catalog = resp.data;
    PSR_DYN.fields = (resp.data && resp.data.fields) || [];
    PSR_DYN.selectedKeys = PSR_DYN.fields.map(f => f.key);
    const el = document.getElementById('psr-designer-fields');
    if (el) el.innerHTML = psrFieldChooserHtml(PSR_DYN.fields, PSR_DYN.selectedKeys);
  } catch (err) {
    psrRenderResult(psrErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 打开动态商品销量排名字段设计器（从「商品销量排名榜」报表工具栏进入） */
function openProductSalesRankingDesigner() {
  const el = document.getElementById('psr-designer');
  if (!el) return;
  const defStart = new Date(Date.now() - 365 * 86400000).toISOString().slice(0, 10);
  const defEnd = new Date().toISOString().slice(0, 10);
  el.innerHTML = `
    <div class="pd-hint">🎛 字段设计器（只读预览）：勾选可见列 → 选择开始 / 结束日期与 Top → 预览授权有界发货数量证据；金额估算已排除，单位不兼容不跨单位合计；全程只读，不执行任意 SQL。</div>
    <div class="toolbar" style="margin-top:0">
      <div class="toolbar-left" style="flex-wrap:wrap;gap:6px;align-items:center;font-size:13px">
        <label>开始日期 <input type="date" id="psr-des-start" value="${defStart}"></label>
        <label>结束日期 <input type="date" id="psr-des-end" value="${defEnd}"></label>
        <label>Top <input type="number" id="psr-des-top" value="10" min="1" max="200" style="width:80px"></label>
        <label>客户 Id <input type="number" id="psr-des-customer-id" min="1" style="width:90px" placeholder="全部客户"></label>
        <label>商品 Id <input type="number" id="psr-des-product-id" min="1" style="width:90px" placeholder="全部商品"></label>
        <label>单位 <input type="text" id="psr-des-unit" maxlength="30" style="width:90px" placeholder="全部单位"></label>
        <label>分组 <select id="psr-des-group" onchange="psrPreview()">
          <option value="none">不分组</option>
          <option value="unit">按单位</option>
        </select></label>
        <span id="psr-designer-fields">正在加载字段目录…</span>
      </div>
      <div class="toolbar-actions">
        <button class="btn btn-neutral btn-sm" onclick="psrToggleAll(true)">全选</button>
        <button class="btn btn-neutral btn-sm" onclick="psrToggleAll(false)">清空</button>
        <button class="btn btn-neutral" onclick="psrExport()" title="导出 Top 结果为 Excel（选定列，复用当前日期与 Top）">📥 导出 Excel（Top 结果）</button>
        <button class="btn btn-neutral" onclick="psrExportPdf()" title="下载 Top 结果为中文 PDF（选定列，复用当前日期与 Top）">📄 下载 PDF（Top 结果）</button>
        <button class="btn btn-primary" onclick="psrPreview()">预览</button>
      </div>
    </div>
    <div id="psr-designer-result"></div>`;
  loadProductSalesRankingDesignerCatalog();
}

/* ============ 动态订单利润暂估字段设计器（ERP-221：只读、有界的前端字段选择与分页预览 / Excel 导出） ============
   口径与后端 ERP-221（DynamicOrderProfitEstimateReportController / DynamicOrderProfitEstimateReportRules）一一对应：
   - 入口复用在「订单利润暂估表」报表（reports.js 的 order-profit，designer: 'order-profit'），不新增菜单 / 架构 / 脚本注册；
   - 字段选择器只由 GET /api/dynamic-order-profit-estimate-report 返回的有限白名单目录渲染为复选框（name="opd-des-field"），
     绝无自由填写的字段名或 SQL；勾选状态经 opdSelectFields 规范化（去重、保持顺序、丢弃未知键）；
   - 筛选仅限开始 / 结束日期（含首尾最多 366 天），分页有界（页码 ≥ 1，每页 1~200），
     预览走 POST /api/dynamic-order-profit-estimate-report，只发送「白名单字段 + 有界日期 + 有界分页」；
   - 结果按后端返回的列名与选定字段值渲染（opdTableHtml / opdResultHtml），全部 HTML 转义，null 金额显示「未知」；
   - 页面覆盖 / 原币口径 / 未知依据始终作为上下文显示（即使对应列被取消选择），绝不展示跨币种总额或实际利润；
   - 空页 / 授权撤销（权限不足 / 未登录）/ 无效请求 / 网络失败分别可见，且不暴露范围外数据；
   - 导出复用预览请求体 POST /api/dynamic-order-profit-estimate-report/export，成功（xlsx 附件）触发下载；
   - 全程只读：不写库、不迁移、不执行任意 SQL。 */

/* 字段设计器状态（纯数据；DOM 访问只在事件处理函数内部发生） */
let OPD_DYN = {
  catalog: null,      // GET /api/dynamic-order-profit-estimate-report 返回的目录 DTO
  fields: [],         // 目录字段（白名单）
  selectedKeys: [],   // 当前勾选的字段键（默认全选）
  view: null,         // 最近一次预览结果
  page: 1,            // 当前预览页（预览 / 翻页复用）
};

function opdEsc(v) {
  return String(v ?? '').replace(/[&<>"']/g, c => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}

/* 规范化选定字段（fail closed）：只保留目录白名单内的键、去重、保持请求顺序；未知键丢弃，绝不发送任意字段名 */
function opdSelectFields(catalogFields, selectedKeys) {
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

/* 日期窗口客户端校验（与后端 ValidateDateRange 一致）：必填、结束不早于开始、含首尾最多 366 天 */
function opdDateError(state) {
  const start = String(state && state.start || '').trim();
  const end = String(state && state.end || '').trim();
  if (!start || !end) return '请填写开始与结束日期';
  const startMs = Date.parse(start);
  const endMs = Date.parse(end);
  if (!Number.isFinite(startMs) || !Number.isFinite(endMs)) return '日期格式无效';
  if (end < start) return '结束日期不能早于开始日期';
  if (Math.round((endMs - startMs) / 86400000) + 1 > 366) return '日期范围最多 366 天（含首尾）';
  return '';
}

/* 客户 Id / 原币币种客户端校验（与后端 NormalizeFilter 一致）：客户 Id 正整数、币种仅已知枚举码 */
function opdFilterError(state) {
  const customerId = String(state && state.customerId || '').trim();
  if (customerId !== '') {
    const c = Number(customerId);
    if (!Number.isInteger(c) || c <= 0) return '客户 Id 必须是正整数（大于 0）';
  }
  const currency = String(state && state.currency || '').trim();
  if (currency !== '') {
    if (!/^(CNY|USD|EUR|HKD|GBP|JPY)$/i.test(currency)) return '原币币种仅支持 CNY / USD / EUR / HKD / GBP / JPY';
  }
  return '';
}

/* 组装可选应用筛选（只发送规范化后的客户 Id / 原币币种；全部留空 = null，保持既有订单利润暂估行为） */
function opdBuildFilter(state) {
  const customerId = String(state && state.customerId || '').trim();
  const currency = String(state && state.currency || '').trim();
  const filter = {};
  if (customerId !== '') filter.customerId = Number(customerId);
  if (currency !== '') filter.currency = currency.toUpperCase();
  return (filter.customerId === undefined && filter.currency === undefined) ? null : filter;
}

/* 组装有界预览请求体：字段只来自目录、日期仅开始 / 结束、分页有界，绝不接受任意字段名或 SQL */
function opdBuildRequest(state) {
  const fields = opdSelectFields(state.catalogFields, state.selectedKeys);
  const page = Math.max(1, Math.floor(Number(state.page) || 1));
  const maxPageSize = Number(state.maxPageSize) || 200;
  let pageSize = Math.floor(Number(state.pageSize));
  if (!Number.isFinite(pageSize)) pageSize = 20;
  pageSize = Math.max(1, Math.min(maxPageSize, pageSize));
  return {
    fields,
    page,
    pageSize,
    start: String(state.start).slice(0, 10),
    end: String(state.end).slice(0, 10),
    filter: opdBuildFilter(state),
  };
}

/* 单元格纯文本：数字合理格式化（整数 / 2 位小数）、日期取 yyyy-MM-dd、其余按字符串呈现 */
function opdCellText(value, field) {
  const dataType = (field && field.dataType) || 'text';
  if (dataType === 'number') {
    const n = Number(value);
    if (Number.isFinite(n)) return Number.isInteger(n) ? String(n) : n.toFixed(2);
    return String(value);
  }
  if (dataType === 'date') return fmtDate(value);
  return String(value);
}

/* 单元格 HTML（转义后安全渲染；null 金额显式显示「未知」） */
function opdRenderCell(value, field) {
  if (value === null || value === undefined) return '<span class="text-muted">未知</span>';
  return opdEsc(opdCellText(value, field));
}

/* 结果表格 HTML：表头为返回的列名、单元格为返回的选定字段值，全部经转义 */
function opdTableHtml(view) {
  const cols = (view && view.columns) || [];
  const rows = (view && view.rows) || [];
  if (!cols.length) return '';
  const align = c => (c.dataType === 'number') ? ' class="text-right"' : '';
  const head = cols.map(c => `<th${align(c)}>${opdEsc(c.label || c.key)}</th>`).join('');
  const body = rows.length
    ? rows.map(r => `<tr>${cols.map(c => `<td${align(c)}>${opdRenderCell(r[c.key], c)}</td>`).join('')}</tr>`).join('')
    : '';
  return `<div class="table-wrap" style="margin-top:8px"><table><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table></div>`;
}

/* 分页（有界、稳定）：当前页之外仍有记录时标注截断，翻页复用当前字段 / 日期 / 每页条数 */
function opdPagingHtml(view) {
  if (!view) return '';
  const prevDisabled = view.page <= 1 ? ' disabled' : '';
  const nextDisabled = view.page >= view.totalPages ? ' disabled' : '';
  return `<div style="display:flex;justify-content:space-between;align-items:center;margin-top:8px">
      <span class="text-muted">第 ${view.page} 页 / 共 ${view.totalPages} 页${view.truncated ? '（仅当前页，后续仍有分页）' : ''}</span>
      <div>
        <button class="btn btn-neutral btn-sm" onclick="opdPage(-1)"${prevDisabled}>← 上一页</button>
        <button class="btn btn-neutral btn-sm" onclick="opdPage(1)"${nextDisabled}>下一页 →</button>
      </div></div>`;
}

/* 空结果提示（显式使用后端 emptyText） */
function opdEmptyHtml(view) {
  return `<div class="empty" style="margin:8px 0">${opdEsc((view && view.emptyText) || '没有符合所选日期范围与数据范围的已审核销售订单')}</div>`;
}

/* 错误提示（授权撤销 / 未登录 / 无效请求 / 网络失败分别可见，且不暴露任何数据） */
function opdErrorHtml(kind, message) {
  const labels = {
    forbidden: '权限不足',
    unauthorized: '未登录 / 登录已过期',
    invalid: '请求无效',
    empty: '导出内容为空',
    network: '网络请求失败',
    error: '预览失败',
  };
  return `<div class="pd-hint" style="color:#b91c1c;background:#fef2f2;border-color:#fecaca">
      <b>${opdEsc(labels[kind] || '预览失败')}</b>：${opdEsc(message || '')}</div>`;
}

/* 业务码 → 错误态分类 */
function opdKindOfCode(code) {
  if (code === 2002) return 'forbidden';
  if (code === 2000 || code === 2003) return 'unauthorized';
  if (code === 5000) return 'error';
  return 'invalid';
}

/* ERP-224 分币种汇总：独立于当前页明细行与选定列渲染，仅渲染后端返回的 summary 列与指标，全部转义；空 / 无汇总不渲染 */
function opdSummaryHtml(view) {
  const summary = view && view.summary;
  const cols = summary && summary.columns;
  const rows = summary && summary.rows;
  if (!cols || !cols.length || !rows || !rows.length) return '';
  const align = c => (c.dataType === 'number') ? ' class="text-right"' : '';
  const head = cols.map(c => `<th${align(c)}>${opdEsc(c.label || c.key)}</th>`).join('');
  const body = rows.map(r => `<tr>${cols.map(c => `<td${align(c)}>${opdRenderCell(r[c.key], c)}</td>`).join('')}</tr>`).join('');
  const coverage = summary.coverageText ? `<div class="text-muted" style="margin:6px 0">${opdEsc(summary.coverageText)}</div>` : '';
  return `<div class="pd-hint" style="margin-top:10px"><b>分币种汇总</b>（按订单原币合并全部匹配已审核订单）</div>${coverage}<div class="table-wrap"><table><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table></div>`;
}

/* 结果：先显示筛选 / 页面覆盖 / 原币 / 未知依据上下文（即使对应列被取消选择），再显示分币种汇总、表格与分页 */
function opdResultHtml(view) {
  const filterLine = view && view.filterText ? `<div class="pd-hint" style="color:#0f766e;background:#f0fdfa;border-color:#99f6e4">🔍 ${opdEsc(view.filterText)}</div>` : '';
  const ctx = `<div class="pd-hint" style="margin:8px 0">${opdEsc((view && view.pageOnlyText) || '')}</div>
    <div class="pd-hint" style="margin:0 0 8px">${opdEsc((view && view.currencyContextText) || '')}</div>
    <div class="pd-hint" style="margin:0 0 8px">${opdEsc((view && view.unknownBasisText) || '')}</div>`;
  const currencySummary = opdSummaryHtml(view);
  if (!view || !view.columns || !view.columns.length) return filterLine + ctx + currencySummary;
  if (!view.rows || !view.rows.length) return filterLine + ctx + currencySummary + opdEmptyHtml(view);
  return filterLine + ctx + currencySummary + opdTableHtml(view) + opdPagingHtml(view);
}

/* 字段选择器：仅由目录白名单渲染为复选框，无自由填写的字段名 */
function opdFieldChooserHtml(fields, selectedKeys) {
  const selected = new Set(selectedKeys || []);
  return (fields || []).map(f => {
    const checked = selected.has(f.key) ? 'checked' : '';
    return `<label style="display:inline-flex;align-items:center;gap:4px;margin:3px 6px 3px 0;padding:2px 8px;border:1px solid #e2e8f0;border-radius:6px;background:#f8fafc;cursor:pointer">
        <input type="checkbox" name="opd-des-field" value="${opdEsc(f.key)}" ${checked} onchange="opdSyncSelection()">
        <span>${opdEsc(f.label || f.key)}</span></label>`;
  }).join('');
}

function opdLoadingHtml() {
  return '<div class="pd-hint" style="text-align:center;color:#64748b">正在预览（只读查询）…</div>';
}

function opdRenderResult(html) {
  const el = document.getElementById('opd-designer-result');
  if (el) el.innerHTML = html;
}

/* 轻量请求封装：返回完整 ApiResponse 信封（保留 code），网络异常抛给调用方 */
async function opdRequest(path, method = 'GET', body = null) {
  const headers = { 'Content-Type': 'application/json' };
  const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
  if (token) headers['Authorization'] = 'Bearer ' + token;
  const opts = { method, headers };
  if (body) opts.body = JSON.stringify(body);
  const resp = await fetch(path, opts);
  return await resp.json();
}

/* 同步勾选状态到 selectedKeys（复选框 onchange） */
function opdSyncSelection() {
  const boxes = document.querySelectorAll('input[name="opd-des-field"]');
  OPD_DYN.selectedKeys = Array.from(boxes).filter(b => b.checked).map(b => b.value);
}

function opdToggleAll(checked) {
  const boxes = document.querySelectorAll('input[name="opd-des-field"]');
  OPD_DYN.selectedKeys = [];
  boxes.forEach(b => { b.checked = checked; if (checked) OPD_DYN.selectedKeys.push(b.value); });
}

/* 筛选变化时重置到第 1 页并清空旧结果（保留字段 / 日期 / 每页条数） */
function opdResetPage() {
  OPD_DYN.page = 1;
  OPD_DYN.view = null;
}

/* 读取当前字段 / 日期 / 分页状态（预览、翻页与导出复用，单一来源） */
function opdBuildState(page) {
  const val = id => { const el = document.getElementById(id); return el ? el.value.trim() : ''; };
  return {
    catalogFields: OPD_DYN.fields,
    selectedKeys: OPD_DYN.selectedKeys,
    start: val('opd-des-start'),
    end: val('opd-des-end'),
    customerId: val('opd-des-customer-id'),
    currency: val('opd-des-currency'),
    pageSize: val('opd-des-pagesize'),
    page: page || OPD_DYN.page || 1,
    maxPageSize: OPD_DYN.catalog && OPD_DYN.catalog.maxPageSize ? OPD_DYN.catalog.maxPageSize : 200,
  };
}

/* 预览：组装有界请求 → POST → 安全渲染列名与单元格；授权 / 无效 / 空 / 网络失败均可见 */
async function opdPreview(page) {
  const state = opdBuildState(page);
  const dateError = opdDateError(state);
  if (dateError) {
    opdRenderResult(opdErrorHtml('invalid', dateError));
    return;
  }
  const filterError = opdFilterError(state);
  if (filterError) {
    opdRenderResult(opdErrorHtml('invalid', filterError));
    return;
  }
  const req = opdBuildRequest(state);
  OPD_DYN.page = req.page;

  opdRenderResult(opdLoadingHtml());

  try {
    const resp = await opdRequest('/api/dynamic-order-profit-estimate-report', 'POST', req);
    if (resp.code === 0) {
      OPD_DYN.view = resp.data;
      OPD_DYN.page = resp.data.page;
      opdRenderResult(opdResultHtml(resp.data));
    } else if (resp.code === 2000 || resp.code === 2003) {
      if (typeof logout === 'function') logout();
      opdRenderResult(opdErrorHtml('unauthorized', resp.message));
    } else {
      opdRenderResult(opdErrorHtml(opdKindOfCode(resp.code), resp.message));
    }
  } catch (err) {
    opdRenderResult(opdErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 导出当前页选定列为 Excel（ERP-221，只读）：复用预览请求体 POST /api/dynamic-order-profit-estimate-report/export；
   成功（xlsx 附件）触发下载；授权 / 无效 / 空结果 / 网络失败在结果区可见，不下载任何内容 */
async function opdExport() {
  if (!OPD_DYN.view || !OPD_DYN.view.columns || !OPD_DYN.view.columns.length) {
    opdRenderResult(opdErrorHtml('invalid', '请先预览后再导出 Excel'));
    return;
  }
  if (!OPD_DYN.view.rows || OPD_DYN.view.rows.length === 0) {
    opdRenderResult(opdErrorHtml('empty', '没有符合所选日期范围的订单利润暂估数据，无法导出（请先预览）'));
    return;
  }

  const state = opdBuildState(OPD_DYN.view.page);
  const dateError = opdDateError(state);
  if (dateError) {
    opdRenderResult(opdErrorHtml('invalid', dateError));
    return;
  }
  const filterError = opdFilterError(state);
  if (filterError) {
    opdRenderResult(opdErrorHtml('invalid', filterError));
    return;
  }
  const req = opdBuildRequest(state);
  opdRenderResult(opdLoadingHtml());

  try {
    const headers = { 'Content-Type': 'application/json' };
    const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
    if (token) headers['Authorization'] = 'Bearer ' + token;
    const resp = await fetch('/api/dynamic-order-profit-estimate-report/export', {
      method: 'POST',
      headers,
      body: JSON.stringify(req),
    });

    const contentType = (resp.headers.get('content-type') || '');
    if (contentType.indexOf('spreadsheetml') >= 0) {
      const blob = await resp.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      const dateStr = new Date().toISOString().slice(0, 10).replace(/-/g, '');
      a.href = url;
      a.download = '订单利润暂估_' + dateStr + '.xlsx';
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
      opdRenderResult('<div class="pd-hint">已导出当前页为 Excel（xlsx），请查看下载。</div>');
      return;
    }

    let envelope = null;
    try { envelope = await resp.json(); } catch (e) { /* 忽略解析失败 */ }
    const code = envelope && envelope.code;
    const message = (envelope && envelope.message) || '导出失败';
    if (code === 2000 || code === 2003) {
      if (typeof logout === 'function') logout();
      opdRenderResult(opdErrorHtml('unauthorized', message));
      return;
    }
    opdRenderResult(opdErrorHtml(opdKindOfCode(code), message));
  } catch (err) {
    opdRenderResult(opdErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 下载当前页选定列为中文 PDF（ERP-222，只读）：复用预览请求体 POST /api/dynamic-order-profit-estimate-report/pdf；
   成功（application/pdf 附件）触发下载；授权 / 无效 / 空结果 / 字体缺失 / 网络失败在结果区可见，不下载任何内容，
   并保留当前字段 / 日期 / 分页状态（不清空 OPD_DYN.view） */
async function opdExportPdf() {
  if (!OPD_DYN.view || !OPD_DYN.view.columns || !OPD_DYN.view.columns.length) {
    opdRenderResult(opdErrorHtml('invalid', '请先预览后再下载 PDF'));
    return;
  }
  if (!OPD_DYN.view.rows || OPD_DYN.view.rows.length === 0) {
    opdRenderResult(opdErrorHtml('empty', '没有符合所选日期范围的订单利润暂估数据，无法下载 PDF（请先预览）'));
    return;
  }

  const state = opdBuildState(OPD_DYN.view.page);
  const dateError = opdDateError(state);
  if (dateError) {
    opdRenderResult(opdErrorHtml('invalid', dateError));
    return;
  }
  const filterError = opdFilterError(state);
  if (filterError) {
    opdRenderResult(opdErrorHtml('invalid', filterError));
    return;
  }
  const req = opdBuildRequest(state);
  opdRenderResult(opdLoadingHtml());

  try {
    const headers = { 'Content-Type': 'application/json' };
    const token = typeof localStorage !== 'undefined' ? (localStorage.getItem('erp_token') || '') : '';
    if (token) headers['Authorization'] = 'Bearer ' + token;
    const resp = await fetch('/api/dynamic-order-profit-estimate-report/pdf', {
      method: 'POST',
      headers,
      body: JSON.stringify(req),
    });

    const contentType = (resp.headers.get('content-type') || '');
    if (contentType.indexOf('application/pdf') >= 0) {
      const blob = await resp.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      const dateStr = new Date().toISOString().slice(0, 10).replace(/-/g, '');
      a.href = url;
      a.download = '订单利润暂估_' + dateStr + '.pdf';
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
      opdRenderResult('<div class="pd-hint">已下载当前页为中文 PDF，请查看下载。</div>');
      return;
    }

    let envelope = null;
    try { envelope = await resp.json(); } catch (e) { /* 忽略解析失败 */ }
    const code = envelope && envelope.code;
    const message = (envelope && envelope.message) || '下载失败';
    if (code === 2000 || code === 2003) {
      if (typeof logout === 'function') logout();
      opdRenderResult(opdErrorHtml('unauthorized', message));
      return;
    }
    opdRenderResult(opdErrorHtml(opdKindOfCode(code), message));
  } catch (err) {
    opdRenderResult(opdErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 翻页（有界：最小第 1 页） */
function opdPage(delta) {
  const page = (OPD_DYN.view ? OPD_DYN.view.page : OPD_DYN.page) + delta;
  if (page < 1) return;
  opdPreview(page);
}

/* 加载字段目录（需登录 + 订单利润暂估表菜单授权；授权 / 网络失败 fail closed，不渲染任何字段） */
async function loadOrderProfitEstimateDesignerCatalog() {
  try {
    const resp = await opdRequest('/api/dynamic-order-profit-estimate-report');
    if (resp.code === 2000 || resp.code === 2003) {
      if (typeof logout === 'function') logout();
      opdRenderResult(opdErrorHtml('unauthorized', resp.message));
      return;
    }
    if (resp.code !== 0) {
      opdRenderResult(opdErrorHtml(opdKindOfCode(resp.code), resp.message));
      return;
    }
    OPD_DYN.catalog = resp.data;
    OPD_DYN.fields = (resp.data && resp.data.fields) || [];
    OPD_DYN.selectedKeys = OPD_DYN.fields.map(f => f.key);
    const el = document.getElementById('opd-designer-fields');
    if (el) el.innerHTML = opdFieldChooserHtml(OPD_DYN.fields, OPD_DYN.selectedKeys);
  } catch (err) {
    opdRenderResult(opdErrorHtml('network', (err && err.message) || '无法连接到服务器'));
  }
}

/* 打开动态订单利润暂估字段设计器（从「订单利润暂估表」报表工具栏进入） */
function openOrderProfitEstimateDesigner() {
  const el = document.getElementById('opd-designer');
  if (!el) return;
  const defStart = new Date(Date.now() - 365 * 86400000).toISOString().slice(0, 10);
  const defEnd = new Date().toISOString().slice(0, 10);
  el.innerHTML = `
    <div class="pd-hint">🎛 字段设计器（只读预览）：勾选可见列 → 选择开始 / 结束日期与每页条数 → 预览授权有界结果（原币销售额与未知成本利润证据，当前价估算独立标注）；绝不跨币种合计，全程只读，不执行任意 SQL。</div>
    <div class="toolbar" style="margin-top:0">
      <div class="toolbar-left" style="flex-wrap:wrap;gap:6px;align-items:center;font-size:13px">
        <label>开始日期 <input type="date" id="opd-des-start" value="${defStart}"></label>
        <label>结束日期 <input type="date" id="opd-des-end" value="${defEnd}"></label>
        <label>客户 Id <input type="number" id="opd-des-customer-id" min="1" style="width:90px" placeholder="全部客户" onchange="opdResetPage()"></label>
        <label>原币 <select id="opd-des-currency" onchange="opdResetPage()">
          <option value="">全部币种</option>
          <option value="CNY">CNY 人民币</option>
          <option value="USD">USD 美元</option>
          <option value="EUR">EUR 欧元</option>
          <option value="HKD">HKD 港币</option>
          <option value="GBP">GBP 英镑</option>
          <option value="JPY">JPY 日元</option>
        </select></label>
        <label>每页 <input type="number" id="opd-des-pagesize" value="20" min="1" max="200" style="width:70px"></label>
        <span id="opd-designer-fields">正在加载字段目录…</span>
      </div>
      <div class="toolbar-actions">
        <button class="btn btn-neutral btn-sm" onclick="opdToggleAll(true)">全选</button>
        <button class="btn btn-neutral btn-sm" onclick="opdToggleAll(false)">清空</button>
        <button class="btn btn-neutral" onclick="opdExport()" title="导出当前页为 Excel（选定列，复用当前日期与分页）">📥 导出 Excel（当前页）</button>
        <button class="btn btn-neutral" onclick="opdExportPdf()" title="下载当前页为中文 PDF（选定列，复用当前日期与分页）">📄 下载 PDF（当前页）</button>
        <button class="btn btn-primary" onclick="opdPreview(1)">预览</button>
      </div>
    </div>
    <div id="opd-designer-result"></div>`;
  loadOrderProfitEstimateDesignerCatalog();
}





