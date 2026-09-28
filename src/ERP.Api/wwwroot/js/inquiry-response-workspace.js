/* ============ 询价报价响应时间工作台（ERP-104：只读派生） ============
   口径与后端 InquiryResponseRules 一一对应：
   - 只按报价单上持久化的 InquiryId 显式链接（绝不按单号 / 文本 / 金额 / 相似度推断链接）；
   - 只取报价单版本链根单（RootQuotationId == null 的初始版本），版本不重复计入；
   - 软删除的询价单与报价单一律排除；
   - 首张有效报价日期 = 显式链接且日期有效（非缺失）的根单报价日期中的最早者；
   - 间隔天数 = 报价日期 − 询价日期（日历天），报价日期早于询价日期时为负（链接异常）；
   - 缺失链接 / 缺失日期 / 链接异常 分别作为独立证据展示，绝不推断为已报价；
   - 只读：不写任何表、不改写询价单 / 报价单 / 客户主数据，不生成 / 修改 / 删除任何记录。 */

const IRW_API = '/api/inquiries/response-times';

/* 工具栏入口：渲染独立工作台页（只读，不落库）。 */
function openInquiryResponseWorkspace() {
  CURRENT_PAGE_CODE = 'inquiry-response-workspace';
  document.getElementById('header-title').textContent = '询价报价响应时间工作台';
  document.getElementById('content').innerHTML = `
    <div class="page-hero report-hero">
      <h2>⏱️ 询价报价响应时间工作台</h2>
      <p>按询价日期区间 / 客户查看每张询价单到首张有效报价的日历天间隔（只读派生，缺失 / 异常不推断）</p>
    </div>
    <div class="pd-hint" style="margin-bottom:10px">
      ⚠️ <b>只读证据视图</b>：仅按报价单上持久化的 <b>InquiryId 显式链接</b>，只取<b>版本链根单</b>（版本不重复计入），
      软删除行一律排除；「未报价 / 链接异常」是缺失或异常证据，<b>不是</b>成交结论、不是交期承诺、不是 SLA 或绩效结论。
    </div>
    <div class="toolbar">
      <div class="toolbar-left" style="flex-wrap:wrap;gap:8px;align-items:center">
        <label>询价日期 <input type="date" id="irw-start" style="width:140px"> 至
          <input type="date" id="irw-end" style="width:140px"></label>
        <label>客户Id <input type="number" id="irw-customer" style="width:110px" placeholder="可选"></label>
        <label>关键字 <input type="text" id="irw-keyword" style="width:180px" placeholder="询价单号"></label>
        <label>每页 <input type="number" id="irw-pagesize" value="20" min="1" max="200" style="width:70px"></label>
      </div>
      <div class="toolbar-actions">
        <button class="btn btn-primary" onclick="loadInquiryResponseTimes(1)">查询</button>
      </div>
    </div>
    <div class="kpi-grid" id="irw-kpi"></div>
    <div class="table-wrap" id="irw-table">
      <div class="skeleton-row"></div><div class="skeleton-row"></div>
    </div>
    <div class="pd-hint" id="irw-rule"></div>
    <div class="pagination" id="irw-pagination"></div>`;
  loadInquiryResponseTimes(1);
}

async function loadInquiryResponseTimes(page) {
  const params = new URLSearchParams();
  params.set('page', page);
  params.set('pageSize', document.getElementById('irw-pagesize').value || 20);
  const start = document.getElementById('irw-start').value;
  const end = document.getElementById('irw-end').value;
  const customer = document.getElementById('irw-customer').value;
  const keyword = document.getElementById('irw-keyword').value.trim();
  if (start) params.set('startDate', start);
  if (end) params.set('endDate', end);
  if (customer) params.set('customerId', customer);
  if (keyword) params.set('keyword', keyword);

  try {
    const data = await api(IRW_API + '?' + params.toString());
    renderInquiryResponseTimes(data, page);
  } catch (e) {
    toast(e.message || '加载失败', 'error');
  }
}

function renderInquiryResponseTimes(data, page) {
  document.getElementById('irw-kpi').innerHTML = `
    <div class="kpi"><div class="kpi-value">${data.total}</div><div class="kpi-label">询价单总数（当前筛选）</div></div>
    <div class="kpi"><div class="kpi-value">${data.items.filter(r => r.responseState === 'quoted').length}</div><div class="kpi-label">本页已报价</div></div>
    <div class="kpi"><div class="kpi-value">${data.items.filter(r => r.responseState === 'unquoted').length}</div><div class="kpi-label">本页未报价</div></div>
    <div class="kpi"><div class="kpi-value">${data.items.filter(r => r.responseState === 'inconsistent-link').length}</div><div class="kpi-label">本页链接异常</div></div>`;

  const rows = data.items.map(r => `
    <tr>
      <td>${escapeHtml(r.inquiryNo)}</td>
      <td>${fmtDate(r.inquiryDate)}</td>
      <td>${escapeHtml(r.customerName || '')}<div class="muted">#${r.customerId}</div></td>
      <td>${statusHtml(r.status)}</td>
      <td>${r.firstQuotationDate ? fmtDate(r.firstQuotationDate) : '—'}</td>
      <td>${r.elapsedDays === null || r.elapsedDays === undefined ? '—' : r.elapsedDays}</td>
      <td><span class="status status-${r.responseStateLevel}">${escapeHtml(r.responseStateText)}</span></td>
      <td class="muted">${escapeHtml(r.evidenceText || '')}</td>
    </tr>`).join('');

  document.getElementById('irw-table').innerHTML = `
    <table class="table">
      <thead><tr>
        <th>询价单号</th><th>询价日期</th><th>客户</th><th>状态</th>
        <th>首张有效报价日期</th><th>间隔(天)</th><th>响应状态</th><th>证据说明</th>
      </tr></thead>
      <tbody>${rows || '<tr><td colspan="8" class="muted">暂无数据</td></tr>'}</tbody>
    </table>`;

  document.getElementById('irw-rule').innerHTML =
    `📌 ${escapeHtml(data.boundaryText || '')}<br>📌 ${escapeHtml(data.readOnlyText || '')}<br>📌 ${escapeHtml(data.disclaimerText || '')}`;

  renderInquiryResponsePagination(data);
}

function renderInquiryResponsePagination(data) {
  const totalPages = data.pageSize <= 0 ? 0 : Math.ceil(data.total / data.pageSize);
  const el = document.getElementById('irw-pagination');
  if (totalPages <= 1) { el.innerHTML = ''; return; }
  let html = '';
  html += `<button class="btn btn-neutral" ${data.page <= 1 ? 'disabled' : ''} onclick="loadInquiryResponseTimes(${data.page - 1})">上一页</button>`;
  html += `<span class="muted" style="margin:0 8px">第 ${data.page} / ${totalPages} 页（共 ${data.total} 条）</span>`;
  html += `<button class="btn btn-neutral" ${data.page >= totalPages ? 'disabled' : ''} onclick="loadInquiryResponseTimes(${data.page + 1})">下一页</button>`;
  el.innerHTML = html;
}
