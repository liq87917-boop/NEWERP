/* ============ 客户销售订单价格历史（ERP-108：只读派生；按「客户 + 商品 + 规格 + 单位 + 币种」完全一致分组，保留原始单价与贸易条款） ============
   口径与后端 CustomerSalesPriceHistoryService / CustomerSalesPriceHistoryRules 一一对应：
   - 只统计「已审核、未删除」销售订单的「未删除」明细，业务员数据范围由服务端硬性过滤；
   - 仅相同「客户 + 商品 + 规格 + 单位 + 币种」归为一组，组内保留原始单价与贸易条款；
   - 单位 / 币种 / 规格 / 客户不同的证据各自成组单列，绝不跨口径比较、绝不做汇率换算、绝不合并不同币种金额；
   - 只读：不写库、不重定价、不改贸易条款。查询走既有只读接口 GET /api/sales-orders/price-history */

/* 从销售订单页打开：只读历史工作台，带商品 / 客户 / 订单日期筛选，并逐条链接到来源订单 */
function openCustomerSalesPriceHistory() {
  const modal = document.getElementById('modal');
  modal.innerHTML = `<div class="modal modal-lg" style="width:1080px;max-width:96vw;max-height:92vh;overflow:auto">
      <h3>📈 客户销售订单价格历史</h3>
      <div class="pd-hint">只读视图：仅统计<b>已审核</b>销售订单明细，按「客户 + 商品 + 规格 + 单位 + 币种」完全一致分组，保留原始单价与贸易条款，口径不同的证据分组成行单列；不写库、不改价、不改贸易条款。</div>
      <div class="form-grid" style="grid-template-columns:repeat(3,1fr);gap:10px;margin:8px 0">
        <label>商品 Id <input type="number" id="csph-product" min="1" style="width:100%"></label>
        <label>客户 Id <input type="number" id="csph-customer" min="1" style="width:100%"></label>
        <label>每页 <input type="number" id="csph-pagesize" value="50" min="1" max="200" style="width:80px"></label>
        <label>订单日期 <input type="date" id="csph-date-from" style="width:140px"> 至
          <input type="date" id="csph-date-to" style="width:140px"></label>
      </div>
      <div class="modal-footer">
        <button class="btn btn-primary" onclick="loadCustomerSalesPriceHistory(1)">查询</button>
        <button class="btn btn-neutral" onclick="closeModal()">关闭</button>
      </div>
      <div id="csph-result"></div>
    </div>`;
  modal.style.display = 'flex';
}

/* 发起只读查询并渲染结果（有界分页，翻页只请求本页） */
async function loadCustomerSalesPriceHistory(page) {
  const product = document.getElementById('csph-product').value.trim();
  const customer = document.getElementById('csph-customer').value.trim();
  const from = document.getElementById('csph-date-from').value;
  const to = document.getElementById('csph-date-to').value;
  const size = document.getElementById('csph-pagesize').value;
  if (!product) { toast('请填写商品 Id', 'error'); return; }
  const qs = [
    `productId=${encodeURIComponent(product)}`,
    customer ? `customerId=${encodeURIComponent(customer)}` : '',
    from ? `dateFrom=${encodeURIComponent(from)}` : '',
    to ? `dateTo=${encodeURIComponent(to)}` : '',
    `page=${page}`,
    `pageSize=${size || 50}`,
  ].filter(Boolean).join('&');
  try {
    const v = await api(`/api/sales-orders/price-history?${qs}`);
    document.getElementById('csph-result').innerHTML = customerSalesPriceHistoryHtml(v);
  } catch (err) { toast(err.message, 'error'); }
}

/* 渲染口径分组 + 来源订单链接（逐条链接回来源销售订单） */
function customerSalesPriceHistoryHtml(v) {
  const groupHtml = (v.groups || []).map(g => {
    const rows = (g.rows || []).map(r => `<tr>
      <td>${fmtDate(r.orderDate)}</td>
      <td><a href="javascript:void(0)" onclick="closeModal();openForm(${r.orderId})">${escapeHtml(r.orderNo || '')}</a></td>
      <td>${escapeHtml(r.tradeTerms || '')}</td>
      <td>${escapeHtml(r.spec || '')}</td>
      <td>${escapeHtml(r.unit || '')}</td>
      <td>${escapeHtml(r.currency || '')}</td>
      <td class="text-right">${fmtMoney(r.quantity)}</td>
      <td class="text-right">${fmtMoney(r.unitPrice)}</td>
      <td class="text-right">${fmtMoney(r.amount)}</td>
    </tr>`).join('');
    return `<div style="margin:10px 0;border:1px solid #e2e8f0;border-radius:8px;padding:10px">
      <div style="margin-bottom:6px"><b>${escapeHtml(g.basisText)}</b>
        <span class="text-muted"> · 共 ${g.rowCount} 行</span></div>
      <div class="table-wrap" style="max-height:30vh;overflow:auto">
        <table><thead><tr><th>订单日期</th><th>来源订单</th><th>贸易条款</th><th>规格</th><th>单位</th><th>币种</th><th>数量</th><th>单价</th><th>金额</th></tr></thead>
        <tbody>${rows || '<tr><td colspan="9" class="empty">无明细</td></tr>'}</tbody></table>
      </div>
    </div>`;
  }).join('');

  const emptyHtml = v.emptyText ? `<div class="empty">${escapeHtml(v.emptyText)}</div>` : '';
  const truncatedHtml = v.truncated
    ? `<div class="pd-hint" style="color:#b45309">⚠ 结果已按分页截断：符合条件共 ${v.totalCount} 行，本页显示 ${v.pageSize} 行。</div>` : '';
  const ruleHtml = `<div class="pd-hint">口径：${escapeHtml(v.ruleText || '')}</div>`;

  return `${ruleHtml}${truncatedHtml}${emptyHtml}${groupHtml || (!v.emptyText ? '<div class="empty">暂无价格历史</div>' : '')}`;
}
