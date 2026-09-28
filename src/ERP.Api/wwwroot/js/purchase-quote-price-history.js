/* ============ 供应商报价价格历史（ERP-098：只读派生；仅相同口径内比价，不写库、不自动选供应商） ============ */

/* 审批状态中文（Approved / Rejected / Pending，与后端 PurchaseQuoteApproval 常量一一对应） */
function quoteHistoryApprovalLabel(state) {
  const labels = { Approved: '已批准', Rejected: '已拒绝', Pending: '待审批' };
  return labels[state] || state || '';
}

/* 审批状态颜色（复用状态胶囊配色） */
function quoteHistoryApprovalClass(state) {
  const map = { Approved: 'status-success', Rejected: 'status-danger', Pending: 'status-info' };
  return map[state] || 'status-neutral';
}

/* 行操作：从某个比价行打开报价价格历史（GET /api/purchase/quotes/{id}/price-history，不落库） */
async function showPurchaseQuotePriceHistory(id) {
  try {
    const v = await api(`/api/purchase/quotes/${id}/price-history`);

    const groupHtml = (v.groups || []).map(g => {
      const mark = g.comparable
        ? '<span style="color:#0f766e;font-weight:600">🟢 可同比价</span>'
        : '<span style="color:#b45309;font-weight:600">🟡 口径不同（仅单列，不参与比价）</span>';
      const rows = (g.rows || []).map(r => `<tr>
        <td>${fmtDate(r.quoteDate)}</td>
        <td>${escapeHtml(r.quoteNo || '')}</td>
        <td>${escapeHtml(r.supplierName || '')}</td>
        <td class="text-right">${fmtMoney(r.quotePrice)}</td>
        <td class="text-right">${fmtMoney(r.totalAmount)}</td>
        <td>${escapeHtml(r.currency || '')}</td>
        <td>${r.taxIncluded ? '含税' : '不含税'}</td>
        <td>${r.isSelected ? '✅ 已选中' : ''} ${escapeHtml(r.status || '')}</td>
        <td><span class="status ${quoteHistoryApprovalClass(r.approvalState)}">${quoteHistoryApprovalLabel(r.approvalState)}</span></td>
        <td>${escapeHtml(r.decidedByName || '')}</td>
      </tr>`).join('');

      return `<div style="margin:10px 0;border:1px solid #e2e8f0;border-radius:8px;padding:10px">
        <div style="display:flex;flex-wrap:wrap;gap:8px;align-items:center;margin-bottom:6px">
          <b>${escapeHtml(g.basisText)}</b> ${mark}
          <span class="text-muted">共 ${g.rowCount} 行 · 最低 ${fmtMoney(g.minPrice)} · 最高 ${fmtMoney(g.maxPrice)} · 最新 ${fmtMoney(g.latestPrice)} · 价差 ${fmtMoney(g.priceSpread)}</span>
        </div>
        <div class="table-wrap" style="max-height:34vh;overflow:auto">
          <table><thead><tr><th>报价日期</th><th>比价批次</th><th>供应商</th><th>报价单价</th><th>报价总额</th><th>币种</th><th>含税</th><th>状态</th><th>审批</th><th>决定人</th></tr></thead>
          <tbody>${rows || '<tr><td colspan="10" class="empty">无报价行</td></tr>'}</tbody></table>
        </div>
      </div>`;
    }).join('');

    const emptyHtml = v.emptyText ? `<div class="empty">${escapeHtml(v.emptyText)}</div>` : '';
    const truncatedHtml = v.truncated
      ? `<div class="pd-hint" style="color:#b45309">⚠ 结果已按分页截断：符合条件共 ${v.totalCount} 行，本页显示 ${v.pageSize} 行。</div>` : '';
    const referenceHtml = v.referenceBasisText
      ? `<div class="pd-hint">参照口径：${escapeHtml(v.referenceBasisText)}</div>` : '';

    const modal = document.getElementById('modal');
    modal.innerHTML = `<div class="modal modal-lg" style="width:1080px;max-width:96vw;max-height:92vh;overflow:auto">
      <h3>📊 报价价格历史：${escapeHtml(v.productName || '')}</h3>
      <div class="form-grid" style="grid-template-columns:repeat(3,1fr)">
        <div><b>报价行总数</b>：${v.totalCount}</div>
        <div><b>比价口径组数</b>：${v.groupCount}</div>
        <div><b>本页显示</b>：${v.pageSize} 行（第 ${v.page} 页）</div>
      </div>
      ${referenceHtml}
      ${truncatedHtml}
      ${emptyHtml}
      ${groupHtml || (!v.emptyText ? '<div class="empty">暂无报价历史</div>' : '')}
      <div class="pd-hint">比价口径：${escapeHtml(v.ruleText || '')}</div>
      <div class="modal-footer"><button class="btn btn-neutral" onclick="closeModal()">关闭</button></div>
    </div>`;
    modal.style.display = 'flex';
    return v;
  } catch (err) { toast(err.message, 'error'); }
}
