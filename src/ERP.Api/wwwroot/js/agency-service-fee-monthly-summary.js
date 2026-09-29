/* ============ 代理服务费对账单月度汇总（ERP-110：只读派生；按「对账日期所属年月 + 客户 + 原币」分组） ============
   口径与后端 AgencyServiceFeeMonthlySummaryRules / AgencyServiceFeeMonthlySummaryService 一一对应：
   - 仅统计**未删除**的对账单证据，按「对账日期所属年月 + 客户 + 原币」分组；
   - 仅**未删除且已登记**的对账单计入原币合计（合计直接来自服务端持久化 TotalAmount，不重算、不换算）；
   - 草稿与已作废对账单单独计数、金额单独列示，绝不并入原币合计；
   - 服务期间跨月**不按期间分摊**：全额计入对账日期所属月份；
   - 不同币种分别成组、绝不合并，本页没有任何跨币种总额；
   - 只读：不写库、不迁移、不回填，不改写对账单证据 / 协议 / 客户 / 订单 / 装柜清单 / 单证 / 发票 / 收款 / 库存 / 费用与结算。
   数据全部走既有只读接口 GET /api/agency-service-fee-statements/monthly-summary。 */

let ASFMS = {
  customers: [],
  filters: { customerId: '', currency: '', from: '', to: '', page: 1, pageSize: 50 },
  view: null
};

const ASFMS_CURRENCIES = ['CNY', 'USD', 'EUR', 'HKD', 'GBP', 'JPY'];

/* 从对账单证据册（ERP-070）打开：只读月度汇总，带客户 / 币种 / 对账日期区间筛选；可选 customerId 预筛选 */
async function openAgencyServiceFeeMonthlySummary(customerId) {
  ASFMS = {
    customers: [],
    filters: {
      customerId: customerId ? String(customerId) : '',
      currency: '', from: '', to: '', page: 1, pageSize: 50
    },
    view: null
  };

  const modal = document.getElementById('modal');
  modal.innerHTML = '<div style="padding:40px;text-align:center;color:#64748b">正在加载…</div>';
  modal.style.display = 'block';

  try {
    const res = await api('/api/base/customers?page=1&pageSize=500');
    ASFMS.customers = (res && res.items) ? res.items : (Array.isArray(res) ? res : []);
  } catch (e) {
    toast('客户列表加载失败：' + e.message, 'error');
  }

  await asfmsLoad(1);
  asfmsRender();
}

function asfmsRender() {
  const modal = document.getElementById('modal');
  const f = ASFMS.filters;
  const customerOptions = ASFMS.customers.map(c =>
    `<option value="${c.id}" ${String(c.id) === String(f.customerId) ? 'selected' : ''}>`
    + `${escapeHtml(c.customerCode || '')} ${escapeHtml(c.customerName || '')}</option>`).join('');

  modal.innerHTML = `
    <div class="modal-content" style="max-width:1280px">
      <div class="modal-header">
        <h3>代理服务费对账单月度汇总（ERP-110）</h3>
        <button class="modal-close" onclick="closeModal()">✕</button>
      </div>
      <div style="padding:6px 10px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;color:#334155;font-size:12px;margin-bottom:8px">
        <b>只读证据汇总</b>：仅<b>未删除且已登记</b>的对账单计入<b>原币合计</b>；草稿与已作废单独计数；
        服务期间跨月<b>不按期间分摊</b>；<b>不是</b>收入确认 / 应收账款或应收余额 / 付款通知 / 税务申报 / 结算确认。
      </div>
      <div style="display:flex;gap:6px;flex-wrap:wrap;align-items:end;margin-bottom:8px">
        <div><label class="ea-lb">客户</label>
          <select id="asfms-f-customer" style="min-width:180px">
            <option value="">全部客户</option>${customerOptions}
          </select></div>
        <div><label class="ea-lb">币种</label>
          <select id="asfms-f-currency" style="min-width:110px">
            <option value="">全部币种</option>
            ${ASFMS_CURRENCIES.map(c => `<option value="${c}" ${f.currency === c ? 'selected' : ''}>${c}</option>`).join('')}
          </select></div>
        <div><label class="ea-lb">对账日期从</label>
          <input type="date" id="asfms-f-from" value="${escapeHtml(f.from)}"></div>
        <div><label class="ea-lb">到</label>
          <input type="date" id="asfms-f-to" value="${escapeHtml(f.to)}"></div>
        <div><label class="ea-lb">每页</label>
          <input type="number" id="asfms-f-pagesize" value="${Number(f.pageSize)}" min="1" max="200" style="width:70px"></div>
        <button class="btn btn-primary btn-sm" onclick="asfmsSearch()">🔍 查询</button>
        <button class="btn btn-neutral btn-sm" onclick="openAgencyServiceFeeStatementRegister()">← 返回对账单台账</button>
      </div>
      <div id="asfms-result">${asfmsResultHtml()}</div>
    </div>`;
  modal.style.display = 'block';
}

function asfmsSearch() {
  ASFMS.filters.customerId = (document.getElementById('asfms-f-customer') || {}).value || '';
  ASFMS.filters.currency = (document.getElementById('asfms-f-currency') || {}).value || '';
  ASFMS.filters.from = (document.getElementById('asfms-f-from') || {}).value || '';
  ASFMS.filters.to = (document.getElementById('asfms-f-to') || {}).value || '';
  const size = (document.getElementById('asfms-f-pagesize') || {}).value;
  if (size) ASFMS.filters.pageSize = Math.max(1, Math.min(200, Number(size) || 50));
  asfmsLoad(1);
}

async function asfmsLoad(page) {
  ASFMS.filters.page = page;
  const f = ASFMS.filters;
  const qs = new URLSearchParams();
  qs.set('page', f.page);
  qs.set('pageSize', f.pageSize);
  if (f.customerId) qs.set('customerId', f.customerId);
  if (f.currency) qs.set('currency', f.currency);
  if (f.from) qs.set('statementDateFrom', f.from);
  if (f.to) qs.set('statementDateTo', f.to);
  try {
    ASFMS.view = await api('/api/agency-service-fee-statements/monthly-summary?' + qs.toString());
  } catch (e) {
    ASFMS.view = null;
    toast('月度汇总加载失败：' + e.message, 'error');
  }
  asfmsRender();
}

function asfmsResultHtml() {
  const v = ASFMS.view;
  if (!v) return '<div class="text-muted" style="padding:8px">加载失败或暂无结果。</div>';

  const rule = `<div class="pd-hint">${escapeHtml(v.ruleText || '')}</div>`;
  const boundary = `<div class="pd-hint">${escapeHtml(v.boundaryText || '')}</div>`;
  const noProration = `<div class="pd-hint">${escapeHtml(v.noProrationText || '')}</div>`;
  const currencyNote = `<div class="pd-hint">${escapeHtml(v.currencyIsolationText || '')}</div>`;
  const readOnly = `<div class="pd-hint">${escapeHtml(v.readOnlyText || '')}</div>`;

  const empty = v.emptyText ? `<div class="empty">${escapeHtml(v.emptyText)}</div>` : '';
  const truncated = v.truncated
    ? `<div class="pd-hint" style="color:#b45309">⚠ 结果已分页截断：符合条件共 ${v.total} 个月份分组，本页显示 ${v.groupCount} 组。</div>`
    : '';

  const rows = (v.rows || []).map(r => `
    <tr>
      <td>${escapeHtml(r.statementMonthText || '')}</td>
      <td>${escapeHtml(r.customerCode || '')} ${escapeHtml(r.customerName || '')}</td>
      <td>${escapeHtml(r.currency || '')}</td>
      <td class="text-right">${Number(r.registeredCount || 0)}</td>
      <td class="text-right">${escapeHtml(r.registeredTotalAmountText || '')}</td>
      <td class="text-right">${Number(r.draftCount || 0)}</td>
      <td class="text-right">${escapeHtml(r.draftTotalAmountText || '')}</td>
      <td class="text-right">${Number(r.voidedCount || 0)}</td>
      <td class="text-right">${escapeHtml(r.voidedTotalAmountText || '')}</td>
    </tr>`).join('');

  const table = `
    <div class="table-wrap" style="margin-top:8px">
      <table>
        <thead><tr>
          <th>对账日期所属月份</th><th>客户</th><th>币种</th>
          <th class="text-right">已登记张数</th><th class="text-right">已登记原币合计</th>
          <th class="text-right">草稿张数</th><th class="text-right">草稿金额（不计入合计）</th>
          <th class="text-right">已作废张数</th><th class="text-right">已作废金额（不计入合计）</th>
        </tr></thead>
        <tbody>${rows || '<tr><td colspan="9" class="text-muted">没有符合条件的月度汇总（证据数字，不代表收入或应收）。</td></tr>'}</tbody>
      </table>
    </div>`;

  const paging = `
    <div style="display:flex;justify-content:space-between;align-items:center;margin-top:8px">
      <div class="text-muted">共 ${v.total} 个月份分组 · 第 ${v.page} 页 · 每页 ${v.pageSize} 组</div>
      <div>
        <button class="btn btn-neutral btn-sm" onclick="asfmsPage(-1)">← 上一页</button>
        <button class="btn btn-neutral btn-sm" onclick="asfmsPage(1)">下一页 →</button>
      </div>
    </div>`;

  return `${rule}${boundary}${noProration}${currencyNote}${readOnly}${truncated}${empty}${table}${paging}`;
}

function asfmsPage(delta) {
  const v = ASFMS.view;
  const page = v ? v.page + delta : ASFMS.filters.page;
  if (page < 1) return;
  asfmsLoad(page);
}

