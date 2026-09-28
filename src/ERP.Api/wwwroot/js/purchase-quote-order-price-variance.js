/* ============ 供应商比价 → 采购订单价格差异（ERP-105：只读派生；仅相同商品+规格+单位+币种+含税口径内计算价差，不重定价、不改审批） ============ */

/* 差异值着色：正值（采购更贵）红色，负值（采购更便宜）绿色，零 / 空灰色 */
function priceVarianceDeltaClass(v) {
  if (v == null) return 'text-muted';
  if (v > 0) return 'text-danger';
  if (v < 0) return 'text-success';
  return 'text-muted';
}

/* 差异单元格：未解决显示「—」，已核对显示带符号金额 */
function priceVarianceDelta(v) {
  if (v == null) return '<span class="text-muted">—</span>';
  const sign = v > 0 ? '+' : '';
  return `<span class="${priceVarianceDeltaClass(v)}">${sign}${fmtMoney(v)}</span>`;
}

/* 行操作：从某个已转采购订单的比价行打开价格差异（GET /api/purchase/quotes/{id}/order-price-variance，不落库） */
async function showPurchaseQuoteOrderPriceVariance(id) {
  try {
    const v = await api(`/api/purchase/quotes/${id}/order-price-variance`);

    const rows = (v.rows || []).map(r => {
      const verdict = r.resolved
        ? '<span class="status status-success">已核对</span>'
        : `<span class="status status-danger" title="${escapeHtml(r.reason || '')}">未解决</span>`;
      return `<tr>
        <td>${fmtDate(r.quoteDate)}</td>
        <td>${escapeHtml(r.quoteNo || '')}</td>
        <td>${escapeHtml(r.supplierName || '')}</td>
        <td>${escapeHtml(r.productName || '')}</td>
        <td>${escapeHtml(r.spec || '')}</td>
        <td>${escapeHtml(r.unit || '')}</td>
        <td>${escapeHtml(r.currency || '')}</td>
        <td>${r.taxIncluded ? '含税' : '不含税'}</td>
        <td class="text-right">${fmtMoney(r.quotePrice)}</td>
        <td class="text-right">${fmtMoney(r.quoteAmount)}</td>
        <td>${escapeHtml(r.orderNo || '—')}</td>
        <td class="text-right">${r.orderUnitPrice == null ? '—' : fmtMoney(r.orderUnitPrice)}</td>
        <td class="text-right">${r.orderAmount == null ? '—' : fmtMoney(r.orderAmount)}</td>
        <td class="text-right">${priceVarianceDelta(r.unitPriceDelta)}</td>
        <td class="text-right">${priceVarianceDelta(r.amountDelta)}</td>
        <td>${escapeHtml(r.reason || '')}</td>
      </tr>`;
    }).join('');

    const summary = `<div class="form-grid" style="grid-template-columns:repeat(3,1fr)">
      <div><b>已核对</b>：${v.resolvedCount}</div>
      <div><b>未解决</b>：${v.unresolvedCount}</div>
      <div><b>比价口径</b>：商品 + 规格 + 单位 + 币种 + 含税</div>
    </div>`;

    const modal = document.getElementById('modal');
    modal.innerHTML = `<div class="modal modal-lg" style="width:1180px;max-width:96vw;max-height:92vh;overflow:auto">
      <h3>📉 比价 → 采购订单价格差异核对</h3>
      ${summary}
      <div class="table-wrap" style="max-height:56vh;overflow:auto">
        <table><thead><tr>
          <th>报价日期</th><th>比价批次</th><th>供应商</th><th>商品</th><th>规格</th><th>单位</th><th>币种</th><th>含税</th>
          <th class="text-right">报价单价</th><th class="text-right">报价总额</th>
          <th>采购单号</th><th class="text-right">采购单价</th><th class="text-right">采购金额</th>
          <th class="text-right">单价差</th><th class="text-right">金额差</th><th>未解决原因</th>
        </tr></thead>
        <tbody>${rows || '<tr><td colspan="16" class="empty">没有可核对的价格差异行</td></tr>'}</tbody></table>
      </div>
      <div class="pd-hint">${escapeHtml(v.ruleText || '')}</div>
      <div class="modal-footer"><button class="btn btn-neutral" onclick="closeModal()">关闭</button></div>
    </div>`;
    modal.style.display = 'flex';
    return v;
  } catch (err) { toast(err.message, 'error'); }
}
