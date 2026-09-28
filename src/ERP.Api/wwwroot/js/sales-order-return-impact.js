/* ============ 销售订单退货影响（ERP-101：只读派生） ============
   口径与后端 SalesOrderReturnImpactSemantics 一一对应：
   - 毛出货：只统计「以本单为来源、未删除、已审核」的销售出库明细数量；
   - 有效退货：只统计「来源出库单属于本单且未删除、退货已审核、客户与本单一致」的退货明细数量；
   - 净出货 = 毛出货 − 有效退货，绝不静默钳制超退（净额可为负并标记）；
   - 未审核 / 客户不一致 / 来源已删除 / 来源不属于本单 / 未关联来源 的退货作为异常单列、不计入；
   - 证据不完整（命中上限）显示「未知」，绝不回落为 0；
   - 只读派生：不改写订单任何已登记进度，不执行迁移 / 生产 SQL / 部署。 */

const SRI_CLASSIFICATION_LABELS = {
  valid: '有效退货',
  non_approved: '未审核',
  wrong_customer: '客户不一致',
  missing_source: '来源已删除',
  unmatched: '来源不属于本单',
  unlinked: '未关联来源',
};
const SRI_ANOMALY_LABELS = {
  none: '正常',
  over_return: '超退',
  unknown: '未知',
};

/* 行操作：查看销售订单退货影响（GET /api/sales-orders/{id}/return-impact，只读、不落库） */
async function showSalesOrderReturnImpact(id) {
  try {
    const p = await api(`/api/sales-orders/${id}/return-impact`);

    const productRows = (p.products || []).map(l => `<tr>
      <td>${escapeHtml(l.productName || '')}</td>
      <td>${escapeHtml(l.spec || '')}</td>
      <td>${escapeHtml(l.unit || '')}</td>
      <td>${fmtMoney(l.orderedQuantity)}</td>
      <td>${sriMoney(l.grossShipped)}</td>
      <td>${sriMoney(l.validReturned)}</td>
      <td>${sriMoney(l.netShipped)}</td>
      <td>${sriAnomalyHtml(l.anomaly)}</td>
      <td title="${escapeHtml(l.note || '')}">${escapeHtml(l.note || '')}</td>
    </tr>`).join('');

    const shipmentRows = (p.shipments || []).map(s => `<tr>
      <td>${escapeHtml(s.stockOutNo || '')}</td>
      <td>${escapeHtml(s.stockOutDate ? fmtDate(s.stockOutDate) : '')}</td>
      <td>${statusHtml(s.status)}</td>
      <td>${fmtMoney(s.totalQuantity)}</td>
      <td>${s.counted ? '计入毛出货' : '不计入'}</td>
      <td>${fmtMoney(s.returnedQuantity)}</td>
    </tr>`).join('');

    const returnRows = (p.returns || []).map(r => `<tr>
      <td>${escapeHtml(r.returnNo || '')}</td>
      <td>${escapeHtml(r.returnDate ? fmtDate(r.returnDate) : '')}</td>
      <td>${statusHtml(r.status)}</td>
      <td>${escapeHtml(r.customerName || '')}</td>
      <td>${escapeHtml(r.sourceStockOutNo || '')}</td>
      <td>${sriClassificationHtml(r.classification)}</td>
      <td title="${escapeHtml(r.note || '')}">${escapeHtml(r.note || '')}</td>
    </tr>`).join('');

    const exceptionRows = (p.exceptions || []).map(r => `<tr>
      <td>${escapeHtml(r.returnNo || '')}</td>
      <td>${escapeHtml(r.returnDate ? fmtDate(r.returnDate) : '')}</td>
      <td>${statusHtml(r.status)}</td>
      <td>${escapeHtml(r.customerName || '')}</td>
      <td>${escapeHtml(r.sourceStockOutNo || '')}</td>
      <td>${sriClassificationHtml(r.classification)}</td>
      <td title="${escapeHtml(r.note || '')}">${escapeHtml(r.note || '')}</td>
    </tr>`).join('');

    const truncatedHint = p.truncated
      ? '<div class="pd-hint">⚠️ 退货影响证据超过单次派生上限，数量按「未知」呈现（绝不回落为 0）。</div>' : '';

    const modal = document.getElementById('modal');
    modal.innerHTML = `<div class="modal modal-lg" style="width:1080px;max-width:96vw;max-height:92vh;overflow:auto">
      <h3>↩️ 销售订单退货影响：${escapeHtml(p.orderNo || '')}</h3>
      <div class="pd-hint">只读视图：只按显式链接「销售退货 → 来源出库单 → 本销售订单」派生毛出货 / 有效退货 / 净出货，<b>不</b>改写订单任何已登记进度，也<b>不</b>执行迁移 / 生产 SQL / 部署。</div>

      <div class="form-grid" style="grid-template-columns:repeat(4,1fr)">
        <div><b>客户</b>：${escapeHtml(p.customerName || ('客户 ' + p.customerId))}</div>
        <div><b>币种</b>：${escapeHtml(p.currency || '')}</div>
        <div><b>毛出货合计</b>：${sriMoney(p.grossShippedTotal)}</div>
        <div><b>有效退货合计</b>：${sriMoney(p.validReturnedTotal)}</div>
        <div><b>净出货合计</b>：${sriMoney(p.netShippedTotal)}（可负 = 超退）</div>
      </div>
      ${truncatedHint}

      <h4>商品退货影响（毛出货 / 有效退货 / 净出货）</h4>
      <div class="table-wrap" style="max-height:30vh;overflow:auto">
        <table><thead><tr><th>商品</th><th>规格</th><th>单位</th><th>订单数量</th><th>毛出货</th><th>有效退货</th><th>净出货</th><th>异常</th><th>说明</th></tr></thead>
        <tbody>${productRows || '<tr><td colspan="9" class="empty">无订单明细与退货证据</td></tr>'}</tbody></table>
      </div>

      <h4>出货来源单据（以本单为来源的销售出库单）</h4>
      <div class="table-wrap" style="max-height:22vh;overflow:auto">
        <table><thead><tr><th>出库单号</th><th>日期</th><th>状态</th><th>单据数量</th><th>是否计入毛出货</th><th>指向退货数量</th></tr></thead>
        <tbody>${shipmentRows || '<tr><td colspan="6" class="empty">暂无出库单</td></tr>'}</tbody></table>
      </div>

      <h4>退货证据（来源出库单属于本单）</h4>
      <div class="table-wrap" style="max-height:22vh;overflow:auto">
        <table><thead><tr><th>退货单号</th><th>日期</th><th>状态</th><th>客户</th><th>来源出库单</th><th>分类</th><th>说明</th></tr></thead>
        <tbody>${returnRows || '<tr><td colspan="7" class="empty">暂无关联退货</td></tr>'}</tbody></table>
      </div>

      <h4>异常退货（未关联 / 来源不属于本单，不计入净额）</h4>
      <div class="table-wrap" style="max-height:22vh;overflow:auto">
        <table><thead><tr><th>退货单号</th><th>日期</th><th>状态</th><th>客户</th><th>来源出库单</th><th>分类</th><th>说明</th></tr></thead>
        <tbody>${exceptionRows || '<tr><td colspan="7" class="empty">暂无异常退货</td></tr>'}</tbody></table>
      </div>

      <div class="pd-hint">口径：${escapeHtml(p.rule || '')}</div>
      <div class="pd-hint">范围：${escapeHtml(p.scopeNote || '')}</div>
      <div class="modal-footer"><button class="btn btn-neutral" onclick="closeModal()">关闭</button></div>
    </div>`;
    modal.style.display = 'flex';
    return p;
  } catch (err) { toast(err.message, 'error'); }
}

/* 数量：null / undefined 表示未知（证据不完整），绝不显示为 0 */
function sriMoney(v) {
  return (v === null || v === undefined) ? '未知' : fmtMoney(v);
}

function sriClassificationHtml(c) {
  const label = SRI_CLASSIFICATION_LABELS[c] || c || '未知';
  if (c === 'valid') return `<span class="status status-ok">${label}</span>`;
  return `<span class="status status-warn">${label}</span>`;
}

function sriAnomalyHtml(a) {
  const label = SRI_ANOMALY_LABELS[a] || a || '未知';
  if (a === 'none') return `<span class="text-muted">${label}</span>`;
  if (a === 'over_return') return `<span class="status status-danger">${label}</span>`;
  return `<span class="status status-warn">${label}</span>`;
}
