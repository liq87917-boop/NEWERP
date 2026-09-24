/* ============ 采购订单执行进度（ERP-026：只读派生，收货数量来自既有入库单，结算金额仅在既有引用可用时暴露） ============ */

/* 行操作：查看采购订单执行进度（GET /api/purchase-orders/{id}/progress，不落库） */
async function showPurchaseOrderProgress(id) {
  try {
    const p = await api(`/api/purchase-orders/${id}/progress`);
    const lineRows = (p.lines || []).map(l => `<tr>
      <td>${escapeHtml(l.productName || '')}</td>
      <td>${escapeHtml(l.spec || '')}</td>
      <td>${escapeHtml(l.unit || '')}</td>
      <td>${fmtMoney(l.orderedQuantity)}</td>
      <td>${fmtMoney(l.receivedQuantity)}</td>
      <td>${fmtMoney(l.pendingQuantity)}</td>
      <td>${fmtMoney(l.outstandingQuantity)}</td>
      <td>${fmtMoney(l.overReceivedQuantity)}</td>
      <td>${escapeHtml(progressReceiptLabel(l.receiptStatus))}</td>
    </tr>`).join('');

    const unmatchedRows = (p.unmatchedReceipts || []).map(u => `<tr>
      <td>${escapeHtml(u.productName || '')}</td>
      <td>${u.productId}</td>
      <td>${fmtMoney(u.receivedQuantity)}</td>
    </tr>`).join('');

    const receiptRows = (p.receipts || []).map(r => `<tr>
      <td>${escapeHtml(r.stockInNo || '')}</td>
      <td>${escapeHtml(r.stockInDate ? fmtDate(r.stockInDate) : '')}</td>
      <td>${statusHtml(r.status)}</td>
      <td>${fmtMoney(r.totalQuantity)}</td>
      <td>${r.counted ? '计入已收' : '不计入'}</td>
    </tr>`).join('');

    const s = p.settlement || {};
    const settlementRows = (s.documents || []).map(d => `<tr>
      <td>${escapeHtml(d.paymentNo || '')}</td>
      <td>${escapeHtml(d.paymentDate ? fmtDate(d.paymentDate) : '')}</td>
      <td>${fmtMoney(d.amount)}</td>
      <td>${escapeHtml(d.currency || '')}</td>
      <td>${statusHtml(d.status)}</td>
      <td>${escapeHtml(d.paymentApplyNo || '')}</td>
      <td>${d.counted ? '计入结算' : '不计入'}</td>
    </tr>`).join('');

    const consistent = p.arrivalProgressConsistent === null || p.arrivalProgressConsistent === undefined
      ? '' : (p.arrivalProgressConsistent ? '（与派生结果一致）' : '（与派生结果不一致，请核对）');

    const modal = document.getElementById('modal');
    modal.innerHTML = `<div class="modal modal-lg" style="width:1080px;max-width:96vw;max-height:92vh;overflow:auto">
      <h3>📦 采购订单执行进度：${escapeHtml(p.orderNo || '')}</h3>
      <div class="form-grid" style="grid-template-columns:repeat(4,1fr)">
        <div><b>订单总额</b>：${fmtMoney(p.orderAmount)} ${escapeHtml(p.currency || '')}</div>
        <div><b>已订数量</b>：${fmtMoney(p.orderedQuantity)}</div>
        <div><b>已收数量</b>：${fmtMoney(p.receivedQuantity)}（已审核入库）</div>
        <div><b>未收数量</b>：${fmtMoney(p.outstandingQuantity)}</div>
        <div><b>待审数量</b>：${fmtMoney(p.pendingQuantity)}（未计入已收）</div>
        <div><b>订单外已收</b>：${fmtMoney(p.unmatchedReceivedQuantity)}</div>
        <div><b>派生收货状态</b>：${escapeHtml(progressReceiptLabel(p.receiptStatus))}</div>
        <div><b>人工到货进度</b>：${escapeHtml(p.arrivalProgressRecorded || '（未登记）')}${consistent}</div>
      </div>

      <h4>明细收货进度</h4>
      <div class="table-wrap" style="max-height:32vh;overflow:auto">
        <table><thead><tr><th>商品</th><th>规格</th><th>单位</th><th>订单数量</th><th>已收</th><th>待审</th><th>未收</th><th>超收</th><th>行状态</th></tr></thead>
        <tbody>${lineRows || '<tr><td colspan="9" class="empty">无订单明细</td></tr>'}</tbody></table>
      </div>

      ${unmatchedRows ? `<h4>订单外商品已收（显式单列，不并入订单行）</h4>
      <div class="table-wrap" style="max-height:20vh;overflow:auto">
        <table><thead><tr><th>商品</th><th>商品Id</th><th>已收数量</th></tr></thead><tbody>${unmatchedRows}</tbody></table>
      </div>` : ''}

      <h4>收货来源单据（以本单为来源的采购入库单）</h4>
      <div class="table-wrap" style="max-height:24vh;overflow:auto">
        <table><thead><tr><th>入库单号</th><th>日期</th><th>状态</th><th>单据数量</th><th>是否计入</th></tr></thead>
        <tbody>${receiptRows || '<tr><td colspan="5" class="empty">暂无入库单</td></tr>'}</tbody></table>
      </div>

      <h4>结算进度</h4>
      <div class="form-grid" style="grid-template-columns:repeat(3,1fr)">
        <div><b>人工登记结算进度</b>：${escapeHtml(s.recordedProgress || '（未登记）')}</div>
        <div><b>引用状态</b>：${escapeHtml(progressSettlementLinkLabel(s.linkStatus))}</div>
        <div><b>归属销售订单</b>：${escapeHtml(s.owningSalesOrderNo || '（未关联）')}</div>
        <div><b>已结算</b>：${settlementMoney(s.settledAmount)}</div>
        <div><b>未结算</b>：${settlementMoney(s.outstandingAmount)}</div>
        <div><b>已提交未审核</b>：${settlementMoney(s.submittedAmount)}${s.overSettled ? '（已超付，请核对）' : ''}</div>
      </div>
      <div class="pd-hint">${escapeHtml(s.linkReason || '')}</div>
      <div class="table-wrap" style="max-height:26vh;overflow:auto">
        <table><thead><tr><th>付款单号</th><th>日期</th><th>金额</th><th>币种</th><th>状态</th><th>货款申请单</th><th>是否计入</th></tr></thead>
        <tbody>${settlementRows || '<tr><td colspan="7" class="empty">无可链接的付款单</td></tr>'}</tbody></table>
      </div>

      <div class="pd-hint">收货口径：${escapeHtml(p.receiptRule || '')}</div>
      <div class="pd-hint">结算口径：${escapeHtml(p.settlementRule || '')}</div>
      <div class="modal-footer"><button class="btn btn-neutral" onclick="closeModal()">关闭</button></div>
    </div>`;
    modal.style.display = 'flex';
    return p;
  } catch (err) { toast(err.message, 'error'); }
}

/* 收货状态中文（与后端 PurchaseOrderProgress 常量一一对应） */
function progressReceiptLabel(status) {
  const labels = { none: '未收货', partial: '部分收货', complete: '已收齐', over_received: '超收' };
  return labels[status] || status || '';
}

/* 结算引用状态中文（linked / ambiguous / unavailable） */
function progressSettlementLinkLabel(status) {
  const labels = { linked: '引用可用', ambiguous: '引用不唯一（不推断）', unavailable: '无可用引用（未知）' };
  return labels[status] || status || '';
}

/* 结算金额：null / undefined 表示未知（无可用既有引用），绝不显示为 0 */
function settlementMoney(v) {
  return (v === null || v === undefined) ? '未知' : fmtMoney(v);
}
