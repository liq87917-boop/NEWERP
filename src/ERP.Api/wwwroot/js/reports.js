/* ============ 报表模块（义乌小商品外贸行业化）============ */
const REPORTS = {
  /* === 通用财务报表 === */
  'product-sales-ranking': { api: '/api/reports/product-sales-ranking', title: '爆款 SKU 销售排行',
    emoji: '🔥', kpi: 'cargo', summary: 'TOP SKU · 按销售件数 · 义乌爆款挖掘' },
  'order-profit': { api: '/api/reports/order-profit', title: '订单利润暂估表',
    emoji: '💹', kpi: 'gold', summary: '订单毛利 · FOB/CIF/DDP 利润核算' },
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
  'follow-up-due': { api: '/api/reports/follow-up-due', title: '跟进提醒', asOf: true,
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
        <div class="kpi-label"><span class="kpi-emoji">💰</span>合计金额</div>
        <div class="kpi-value" data-rkpi="total">--<span class="unit">USD</span></div>
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
        <button class="btn btn-neutral" onclick="exportReportCSV()" title="导出为 CSV">📤 导出 CSV</button>
        <button class="btn btn-neutral" onclick="window.print()" title="打印报表">🖨 打印</button>
      </div>
    </div>

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

/* 报表空状态：行业主题 emoji */
function emptyReportHtml(msg, emoji) {
  return `<div class="empty empty-foreign">
    <div class="empty-emoji">${emoji || '📭'}</div>
    <div class="empty-text">${msg}</div>
    <div class="empty-sub">试试调整日期范围或切换其他报表</div>
  </div>`;
}
