/* ============ 订单财务核对（ERP-028：只读派生，只用既有引用字段；金额未知显示「未知」，绝不显示为 0） ============ */

/* 行操作：查看销售 / 采购订单财务核对（GET /api/{sales|purchase}-orders/{id}/finance-reconciliation，不落库） */
async function showOrderFinanceReconciliation(id) {
  const code = CURRENT_MODULE_CODE;
  if (code !== 'sales-order' && code !== 'purchase-order') {
    toast('当前模块不支持财务核对', 'error');
    return;
  }
  const apiRoot = code === 'sales-order' ? '/api/sales-orders' : '/api/purchase-orders';
  try {
    const data = await api(`${apiRoot}/${id}/finance-reconciliation`);

    const sectionHtml = (data.sections || []).map(s => {
      const rows = (s.records || []).map(r => `<tr>
        <td>${escapeHtml(r.documentNo || '')}</td>
        <td>${escapeHtml(r.documentDate ? fmtDate(r.documentDate) : '')}</td>
        <td>${reconMoney(r.amount)}</td>
        <td>${escapeHtml(r.currency || '（无币种列）')}</td>
        <td>${reconStatusHtml(r.status)}</td>
        <td>${r.counted ? '计入' : '不计入'}</td>
        <td>${escapeHtml(r.referenceField || '')}</td>
        <td>${escapeHtml(r.note || '')}</td>
      </tr>`).join('');
      const unsummed = s.unsummedRecordCount
        ? `（其中 ${s.unsummedRecordCount} 条他币种或无币种列未汇总）` : '';
      return `<h4>${escapeHtml(reconRoleLabel(s.role))} · ${escapeHtml(reconLinkLabel(s.linkStatus))}</h4>
      <div class="form-grid" style="grid-template-columns:repeat(4,1fr)">
        <div><b>方向</b>：${escapeHtml(reconDirectionLabel(s.direction))}</div>
        <div><b>计入合计</b>：${reconMoney(s.countedAmount)}</div>
        <div><b>已提交未审核</b>：${reconMoney(s.submittedAmount)}</div>
        <div><b>仅列出</b>：${fmtMoney(s.listedAmount)}（${s.listedRecordCount || 0} 条）${unsummed}</div>
      </div>
      <div class="pd-hint">引用依据：${escapeHtml(s.referenceField || '')}｜${escapeHtml(s.linkReason || '')}</div>
      <div class="table-wrap" style="max-height:22vh;overflow:auto">
        <table><thead><tr><th>单号</th><th>日期</th><th>金额</th><th>币种</th><th>状态</th><th>计入</th><th>引用依据</th><th>说明</th></tr></thead>
        <tbody>${rows || '<tr><td colspan="8" class="empty">无相关记录</td></tr>'}</tbody></table>
      </div>`;
    }).join('');

    const modal = document.getElementById('modal');
    modal.innerHTML = `<div class="modal modal-lg" style="width:1180px;max-width:96vw;max-height:92vh;overflow:auto">
      <h3>💰 订单财务核对：${escapeHtml(data.orderNo || '')}</h3>
      <div class="form-grid" style="grid-template-columns:repeat(4,1fr)">
        <div><b>订单金额</b>：${fmtMoney(data.orderAmount)} ${escapeHtml(data.currency || '')}</div>
        <div><b>权威计入（同方向）</b>：${reconMoney(data.linkedAmount)}</div>
        <div><b>未覆盖金额</b>：${reconMoney(data.unlinkedAmount)}</div>
        <div><b>已提交未审核</b>：${reconMoney(data.submittedAmount)}</div>
        <div><b>反方向权威计入</b>：${reconMoney(data.counterpartLinkedAmount)}</div>
        <div><b>金额状态</b>：${escapeHtml(reconAmountLabel(data.amountStatus))}</div>
        <div><b>单据状态</b>：${reconStatusHtml(data.status)}</div>
        <div><b>订单类型</b>：${escapeHtml(reconOrderTypeLabel(data.orderType))}</div>
      </div>
      <div class="pd-hint">金额口径：${escapeHtml(data.amountNote || '')}</div>
      ${sectionHtml}
      <div class="pd-hint">核对口径：${escapeHtml(data.rule || '')}</div>
      <div class="modal-footer"><button class="btn btn-neutral" onclick="closeModal()">关闭</button></div>
    </div>`;
    modal.style.display = 'flex';
    return data;
  } catch (err) { toast(err.message, 'error'); }
}

/* 金额：null / undefined 表示未知（无可用既有引用），绝不显示为 0 */
function reconMoney(v) {
  return (v === null || v === undefined) ? '未知' : fmtMoney(v);
}

/* 状态：枚举名走既有状态样式，文本状态（如费用单付款状态）按文本安全显示 */
function reconStatusHtml(v) {
  return STATUS_MAP[v] ? statusHtml(v) : escapeHtml(v || '');
}

/* 分组角色中文（与后端 OrderFinanceReconciliation 的 Role 常量一一对应） */
function reconRoleLabel(role) {
  const labels = {
    deposit_apply: '定金申请单', payment_apply: '货款申请单', supplier_payment: '付款单',
    settlement: '结算（付款单）', sales_order_apply: '归属销售订单的收款申请',
    expense: '费用单', complaint: '客诉单', receipt: '收款单',
    container_settlement: '装柜结算单', bulk_settlement: '散货结算单',
  };
  return labels[role] || role || '';
}

/* 引用状态中文（linked / declared / unattributed / ambiguous / unavailable） */
function reconLinkLabel(status) {
  const labels = {
    linked: '权威引用', declared: '声明引用（非权威）', unattributed: '无法归属（仅列出）',
    ambiguous: '引用不唯一（不推断）', unavailable: '无可用引用（未知）',
  };
  return labels[status] || status || '';
}

/* 资金方向中文 */
function reconDirectionLabel(direction) {
  const labels = { in: '收款方向', out: '付款 / 成本方向', none: '无金额方向' };
  return labels[direction] || direction || '';
}

/* 金额状态中文（linked / partial / unknown） */
function reconAmountLabel(status) {
  const labels = {
    linked: '按权威引用可完整核对', partial: '部分可归属（其余未知）', unknown: '未知（无可用引用）',
  };
  return labels[status] || status || '';
}

function reconOrderTypeLabel(orderType) {
  return orderType === 'sales' ? '销售订单（收款方向）' : '采购订单（付款方向）';
}
