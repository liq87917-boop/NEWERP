/* ERP-022：销售 / 采购订单执行时间线（只读，数据来自既有订单与关联单据） */
async function showOrderTimeline(id) {
  const code = CURRENT_MODULE_CODE;
  if (code !== 'sales-order' && code !== 'purchase-order') {
    toast('当前模块不支持执行时间线', 'error');
    return;
  }
  const apiRoot = code === 'sales-order' ? '/api/sales-orders' : '/api/purchase-orders';
  try {
    const events = await api(`${apiRoot}/${id}/timeline`);
    const rows = (events || []).map(item => `<tr>
      <td>${escapeHtml(item.occurredAt ? new Date(item.occurredAt).toLocaleString() : '')}</td>
      <td>${escapeHtml(timelineEventLabel(item.eventType))}</td>
      <td>${escapeHtml(item.referenceNo || '')}</td>
      <td>${escapeHtml(item.status || '')}</td>
      <td>${escapeHtml(item.source || '')}</td>
      <td>${escapeHtml(item.detail || '')}</td>
    </tr>`).join('');
    const modal = document.getElementById('modal');
    modal.innerHTML = `<div class="modal modal-lg" style="width:1040px;max-width:96vw">
      <h3>🕘 订单执行时间线</h3>
      <div class="table-wrap" style="max-height:62vh;overflow:auto">
        <table><thead><tr><th>时间</th><th>事件</th><th>单号/引用</th><th>状态</th><th>来源</th><th>说明</th></tr></thead>
        <tbody>${rows || '<tr><td colspan="6" class="empty">暂无可追溯事件</td></tr>'}</tbody></table>
      </div>
      <div class="modal-footer"><button class="btn btn-neutral" onclick="closeModal()">关闭</button></div>
    </div>`;
    modal.style.display = 'flex';
  } catch (err) { toast(err.message, 'error'); }
}

function timelineEventLabel(type) {
  const labels = {
    sales_order_created: '销售订单创建', sales_order_updated: '销售订单更新',
    purchase_order_created: '采购订单创建', purchase_order_updated: '采购订单更新',
    supplier_confirmed_delivery: '供应商确认交期', stock_in: '采购入库', stock_out: '销售出库',
    trade_document: '出口单证', complaint: '客户投诉',
  };
  return labels[type] || type || '';
}
