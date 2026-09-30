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

  /* === ERP-018：报价成交率（询报价 → 形式发票 PI / 销售订单 转化分析） === */
  'quotation-conversion': { api: '/api/reports/quotation-conversion', title: '报价成交率分析',
    emoji: '📈', kpi: 'gold',
    summary: '按业务员：成交率 = 已转出数 ÷ 有效报价数 × 100（已转出 = 已转 PI / 已转销售订单 / 已完成；已作废不计入分母）',
    columns: [
      { key: 'salesmanName', label: '业务员' },
      { key: 'quotationCount', label: '有效报价数', type: 'number' },
      { key: 'convertedCount', label: '已转出数', type: 'number' },
      { key: 'conversionRate', label: '成交率%', type: 'number' },
      { key: 'expiredCount', label: '已过期未成交', type: 'number' },
      { key: 'cancelledCount', label: '已作废', type: 'number' },
      { key: 'totalAmount', label: '有效报价金额', type: 'money' },
      { key: 'convertedAmount', label: '已转出金额', type: 'money' },
      { key: 'avgConvertedAmount', label: '单笔成交均价', type: 'money' },
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
        ${rep.designer ? `<button class="btn btn-neutral" onclick="openFollowUpDueDesigner()" title="打开动态跟进提醒字段设计器（只读预览）">🎛 字段设计器</button>` : ''}
        <button class="btn btn-neutral" onclick="exportReportCSV()" title="导出为 CSV">📤 导出 CSV</button>
        <button class="btn btn-neutral" onclick="window.print()" title="打印报表">🖨 打印</button>
      </div>
    </div>

    ${rep.designer ? `<div id="fud-designer"></div>` : ''}

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

/* 组装有界预览请求体：字段只来自目录、筛选仅到期状态 / as-of 日期 / 提前天数、分页有界，绝不接受任意字段名或 SQL */
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

/* 预览结果（只读 / 边界 / 免责文案 + 摘要 + 空结果 + 表格 + 分页） */
function fudDesResultHtml(view) {
  const readOnly = view && view.readOnlyText ? `<div class="pd-hint">${fudDesEsc(view.readOnlyText)}</div>` : '';
  const boundary = view && view.boundaryText ? `<div class="pd-hint">${fudDesEsc(view.boundaryText)}</div>` : '';
  const disclaimer = view && view.disclaimerText ? `<div class="pd-hint" style="color:#64748b">${fudDesEsc(view.disclaimerText)}</div>` : '';
  const summary = view
    ? `<div class="text-muted" style="margin:6px 0">共 ${view.total} 行 · 第 ${view.page} 页 · 每页 ${view.pageSize} 行 · 共 ${view.totalPages} 页${view.truncated ? ' · 后续仍有分页' : ''}</div>`
    : '';
  const empty = view && (!view.rows || view.rows.length === 0) ? fudDesEmptyHtml(view) : '';
  return `${readOnly}${boundary}${disclaimer}${summary}${empty}${fudDesTableHtml(view)}${fudDesPagingHtml(view)}`;
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
    pageSize: val('fud-des-pagesize'),
    page: page || FUD_DYN.page || 1,
    maxPageSize: FUD_DYN.catalog && FUD_DYN.catalog.maxPageSize ? FUD_DYN.catalog.maxPageSize : 200,
  };
}

/* 预览：组装有界请求 → POST → 安全渲染列名与单元格；授权 / 无效 / 空 / 网络失败均可见 */
async function fudDesPreview(page) {
  const state = fudDesBuildState(page);
  const req = fudDesBuildRequest(state);
  FUD_DYN.page = req.page;

  fudDesRenderResult(fudDesLoadingHtml());

  try {
    const resp = await fudDesRequest('/api/dynamic-follow-up-due-report', 'POST', req);
    if (resp.code === 0) {
      FUD_DYN.view = resp.data;
      FUD_DYN.page = resp.data.page;
      fudDesRenderResult(fudDesResultHtml(resp.data));
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
        <label>as-of 日期 <input type="date" id="fud-des-asof" value="${today}"></label>
        <label>提前天数 <input type="number" id="fud-des-ahead" value="7" min="0" max="365" style="width:80px"></label>
        <label>每页 <input type="number" id="fud-des-pagesize" value="20" min="1" max="200" style="width:70px"></label>
        <span id="fud-designer-fields">正在加载字段目录…</span>
      </div>
      <div class="toolbar-actions">
        <button class="btn btn-neutral btn-sm" onclick="fudDesToggleAll(true)">全选</button>
        <button class="btn btn-neutral btn-sm" onclick="fudDesToggleAll(false)">清空</button>
        <button class="btn btn-primary" onclick="fudDesPreview(1)">预览</button>
      </div>
    </div>
    <div id="fud-designer-result"></div>`;
  loadFollowUpDueDesignerCatalog();
}
