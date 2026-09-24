/* ============ 销售订单收款引用证据（ERP-054：只读派生，复用 ERP-053 持久化引用行与同一套分桶口径） ============ */

/* 依赖：app.js（api / toast / fmtMoney / fmtDate / escapeHtml / CURRENT_MODULE_CODE / CURRENT_MODULE）、
         crud.js（renderTable / window.__moduleRows / 模块派生列 render 回调）。
   契约：
   - 列表派生列「收款引用证据」为**虚拟列**：由本文件按页批量取数后渲染，不落库、不改写销售订单与收款单；
   - 每页只发起有界批量请求（GET /api/sales-orders/receipt-evidence-summaries?ids=…，单次 ≤ 200 张订单），
     绝不逐行查库（同一页的订单 Id 一次 / 分批取回，缓存后重渲染）；
   - 后端命中读取上限（truncated）或金额未知（null）时显示「未知」，绝不复用 0 顶替；
   - 本列与详情只是**收款引用证据**：不是银行入账 / 到账凭证、不是应收余额、不是货款核销、不是客户对账单、
     不是结算结果或账龄；没有引用行时只显示「无收款引用证据」，绝不呈现为未收款 / 已收款 / 已结清 / 逾期。 */

const SORE_MAX_BATCH = 200;                 // 与后端 SalesOrderReceiptEvidenceSemantics.MaxBatchOrders 同口径
const SORE_BATCH_API = '/api/sales-orders/receipt-evidence-summaries';

let __soreRowsRef = null;                   // 当前列表页行数组（引用变化 = 列表已重新加载 → 缓存失效）
let __soreInFlight = null;                  // 在途批量请求签名（避免同一页重复请求）
let __soreFailed = false;                   // 本次列表加载失败标记（不无限重试）
const __soreCache = new Map();              // 销售订单 Id → 收款引用证据汇总

/* 列表派生列：收款引用证据徽标（crud.js 列 render 回调；虚拟列，不落库） */
function salesOrderReceiptEvidenceCellHtml(row) {
  const orderId = Number(row && row.id);
  if (!orderId) return '';
  soreInvalidateIfRowsChanged();
  if (__soreCache.has(orderId)) return soreBadgeHtml(__soreCache.get(orderId));
  if (__soreFailed) return '<span class="text-muted">未知（汇总加载失败）</span>';
  soreScheduleLoad();
  return '<span class="text-muted">…（收款引用证据加载中）</span>';
}

/* 列表重新加载（翻页 / 搜索 / 操作后刷新）会换掉 __moduleRows，引用变化即失效缓存，避免展示过期证据 */
function soreInvalidateIfRowsChanged() {
  const rows = window.__moduleRows;
  if (rows !== __soreRowsRef) {
    __soreRowsRef = rows;
    __soreCache.clear();
    __soreFailed = false;
  }
}

/* 批量取回本页（缺失的）订单收款引用证据：分批但**按页**，不逐行请求 */
async function soreScheduleLoad() {
  const rows = __soreRowsRef || [];
  const ids = [...new Set(rows.map(r => Number(r.id)).filter(id => id > 0 && !__soreCache.has(id)))];
  if (!ids.length) return;

  const signature = ids.join(',');
  if (__soreInFlight === signature) return;
  __soreInFlight = signature;

  try {
    for (let i = 0; i < ids.length; i += SORE_MAX_BATCH) {
      const chunk = ids.slice(i, i + SORE_MAX_BATCH);
      const data = await api(`${SORE_BATCH_API}?ids=${chunk.join(',')}`);
      (data.items || []).forEach(item => __soreCache.set(Number(item.salesOrderId), item));
    }
    if (__soreRowsRef === rows) soreRerender();
  } catch (err) {
    __soreFailed = true;
    toast('收款引用证据汇总加载失败：' + err.message, 'error');
    if (__soreRowsRef === rows) soreRerender();
  } finally {
    __soreInFlight = null;
  }
}

/* 汇总到达后就地重渲染当前页（仍读缓存，不会再次发起请求） */
function soreRerender() {
  if (CURRENT_MODULE_CODE !== 'sales-order') return;
  try {
    renderTable(CURRENT_MODULE, { items: __soreRowsRef || [] });
  } catch (err) {
    /* 重渲染失败不影响列表数据本身，忽略即可（数据仍可通过行操作详情查看） */
  }
}

/* 金额：null / undefined = 未知（命中后端有界上限或订单不可用），绝不显示为 0 */
function soreMoney(v) {
  return (v === null || v === undefined) ? '未知' : fmtMoney(v);
}

/* 证据状态中文（与后端 SalesOrderReceiptEvidenceSemantics.AllocationStatus / EvidenceLabel 同口径，仅作兜底显示） */
const SORE_STATUS_LABELS = {
  recorded: '有收款引用证据',
  historical_only: '仅有历史 / 无效收款引用证据',
  none: '无收款引用证据',
  unknown: '未知（超出有界读取上限）',
};

/* 分桶中文（与后端 SalesOrderReceiptEvidenceSemantics.BucketText 同口径，仅作兜底显示） */
const SORE_BUCKET_LABELS = {
  recorded: '有效收款引用证据（计入有效合计）',
  voided: '已作废历史证据（不计入有效合计）',
  invalid: '无效历史证据（客户 / 币种或快照不一致：不换算 / 不合并 / 不改派）',
  unavailable: '无法确认的证据（收款单或订单不存在 / 已删除）',
};

/* 分桶 → 徽标样式（有效=成功，已作废=中性，无效=警示，无法确认=警示） */
function soreBucketLevel(bucket) {
  if (bucket === 'recorded') return 'success';
  if (bucket === 'voided') return 'neutral';
  if (bucket === 'invalid') return 'danger';
  return 'warning';
}

/* 状态 → 徽标样式 */
function soreStatusLevel(status) {
  if (status === 'recorded') return 'success';
  if (status === 'none') return 'neutral';
  if (status === 'historical_only') return 'warning';
  return 'danger';
}

/* 列表徽标：有效收款引用金额 + 收款单张数；历史 / 无效 / 无法确认只以 ⚠ 与悬停说明提示，绝不计入有效合计 */
function soreBadgeHtml(s) {
  if (!s) return '<span class="text-muted">未知</span>';

  const title = `${s.evidenceLabel || ''} ${s.orderStateText || ''} ${s.evidenceNote || ''}`.trim();

  if (s.truncated) {
    return `<span class="status status-warning" title="${escapeHtml(title)}">未知（超出有界读取上限）</span>`;
  }

  const parts = [];
  if (s.recordedAllocatedAmount !== null && s.recordedAllocatedAmount !== undefined && s.recordedAllocatedAmount > 0) {
    parts.push(`<span class="status status-success">收款引用 ${soreMoney(s.recordedAllocatedAmount)}</span>`);
    if (s.recordedReceiptCount !== null && s.recordedReceiptCount !== undefined) {
      parts.push(`${s.recordedReceiptCount} 张收款单`);
    }
    if (s.unreferencedReceiptAmount !== null && s.unreferencedReceiptAmount !== undefined) {
      parts.push(`未指向本单 ${soreMoney(s.unreferencedReceiptAmount)}`);
    }
  } else if (s.hasReceiptAllocationEvidence) {
    parts.push('<span class="status status-warning">仅有历史 / 无效证据</span>');
  } else {
    parts.push('<span class="status status-neutral">无收款引用证据</span>');
  }
  if (s.hasHistoricalEvidence) parts.push('⚠ 含历史证据');

  return `<span title="${escapeHtml(title)}">${parts.join(' · ')}</span>`;
}

/* 行操作：查看销售订单收款引用证据详情（GET /api/sales-orders/{id}/receipt-evidence，只读、不落库） */
async function showSalesOrderReceiptEvidence(id) {
  try {
    const data = await api(`/api/sales-orders/${id}/receipt-evidence`);
    const s = data.summary || {};

    const lineRows = (data.lines || []).map(l => `<tr>
      <td>${escapeHtml(l.receiptNo || '')}${l.receiptAvailable ? '' : ' <span class="status status-warning">收款单不可用</span>'}</td>
      <td>${escapeHtml(l.receiptDate ? fmtDate(l.receiptDate) : '—')}</td>
      <td>${escapeHtml(l.receiptStatusText || '')}</td>
      <td class="text-right">${fmtMoney(l.receiptAmount)} ${escapeHtml(l.currency || '')}</td>
      <td class="text-right">${fmtMoney(l.allocatedAmount)}</td>
      <td><span class="status status-${soreBucketLevel(l.bucket)}">${escapeHtml(l.bucketText || SORE_BUCKET_LABELS[l.bucket] || '')}</span></td>
      <td>${escapeHtml(l.statusText || '')}</td>
      <td>${escapeHtml(l.customerName || '')}（快照 Id=${l.customerId}）</td>
      <td>${escapeHtml(l.orderNo || '')}（${escapeHtml(l.orderCurrency || '')} · ${escapeHtml(l.orderStatusText || '')}）</td>
      <td class="text-right">${soreMoney(l.receiptUnreferencedAmount)}</td>
      <td>${escapeHtml(l.allocatedAt ? fmtDate(l.allocatedAt) : '—')}${l.voidedAt ? ' → ' + escapeHtml(fmtDate(l.voidedAt)) : ''}${l.voidReason ? '（' + escapeHtml(l.voidReason) + '）' : ''}</td>
      <td>${escapeHtml(l.reason || '')}</td>
    </tr>`).join('');

    const modal = document.getElementById('modal');
    modal.innerHTML = `<div class="modal modal-lg" style="width:1440px;max-width:96vw;max-height:92vh;overflow:auto">
      <h3>🧾 销售订单收款引用证据：${escapeHtml(s.orderNo || '')}</h3>
      <div class="pd-hint">
        ⚠️ 只读的<b>收款引用证据</b>（既有客户收款单指向本订单的引用行）：<b>不是</b>银行入账 / 到账凭证、<b>不是</b>应收余额、
        <b>不是</b>货款核销、<b>不是</b>客户对账单、<b>不是</b>结算结果或账龄 —— 不判断是否已收款 / 已结清 / 逾期，也不得据以催收。
      </div>
      <div class="form-grid" style="grid-template-columns:repeat(4,1fr)">
        <div><b>证据结论</b>：<span class="status status-${soreStatusLevel(s.allocationStatus)}">${escapeHtml(s.evidenceLabel || '')}</span></div>
        <div><b>有效已引用（收款引用证据）</b>：${soreMoney(s.recordedAllocatedAmount)} ${escapeHtml(s.orderCurrency || '')}</div>
        <div><b>收款单张数（有效行去重）</b>：${soreMoney(s.recordedReceiptCount)}</div>
        <div><b>引用行条数（含历史）</b>：${soreMoney(s.allocationCount)}</div>
        <div><b>参与证据的收款单金额快照</b>：${soreMoney(s.recordedReceiptAmount)} ${escapeHtml(s.orderCurrency || '')}</div>
        <div><b>其中未指向本订单</b>：${soreMoney(s.unreferencedReceiptAmount)} ${escapeHtml(s.orderCurrency || '')}</div>
        <div><b>订单金额中未被引用证据指向</b>：${soreMoney(s.unreferencedOrderAmount)} ${escapeHtml(s.orderCurrency || '')}</div>
        <div><b>订单金额</b>：${soreMoney(s.orderedAmount)} ${escapeHtml(s.orderCurrency || '')}</div>
        <div><b>已作废历史证据</b>：${soreMoney(s.voidedAllocationCount)} 条 / ${soreMoney(s.voidedAllocatedAmount)}</div>
        <div><b>无效历史证据</b>：${soreMoney(s.invalidAllocationCount)} 条 / ${soreMoney(s.invalidAllocatedAmount)}</div>
        <div><b>无法确认的证据</b>：${soreMoney(s.unavailableAllocationCount)} 条 / ${soreMoney(s.unavailableAllocatedAmount)}</div>
        <div><b>订单可用性</b>：${escapeHtml(s.orderStateText || '')}（当前状态 ${escapeHtml(s.orderStatusText || '')}）</div>
      </div>

      <div class="pd-hint">证据说明：${escapeHtml(s.evidenceNote || '')}</div>

      <h4>逐条收款引用证据（${data.lineCount || 0} 条；只按 ERP-053 持久化引用行派生，含已作废历史）</h4>
      <div class="table-wrap" style="max-height:40vh;overflow:auto">
        <table><thead><tr>
          <th>收款单号</th><th>收款日期</th><th>收款单状态</th><th class="text-right">收款单金额</th>
          <th class="text-right">引用金额</th><th>证据分桶</th><th>引用行状态</th><th>客户快照</th>
          <th>订单快照</th><th class="text-right">该收款单未指向本单</th><th>登记 / 作废</th><th>说明（为什么计入 / 不计入）</th>
        </tr></thead>
        <tbody>${lineRows || '<tr><td colspan="12" class="empty">本销售订单没有任何收款引用行（收款引用证据缺口，不代表未收款 / 已收款 / 已结清 / 逾期）</td></tr>'}</tbody></table>
      </div>

      <div class="pd-hint">派生口径：${escapeHtml(data.rule || '')}</div>
      <div class="pd-hint">范围说明：${escapeHtml(data.scopeNote || '')}</div>
      <div class="pd-hint">登记口径：${escapeHtml(data.linkageRule || '')}</div>
      <div class="pd-hint">登记册边界：${escapeHtml(data.allocationBoundary || '')}</div>
      <div class="pd-hint">边界：${escapeHtml(data.boundary || '')}</div>
      <div class="modal-footer"><button class="btn btn-neutral" onclick="closeModal()">关闭</button></div>
    </div>`;
    modal.style.display = 'flex';
    return data;
  } catch (err) { toast(err.message, 'error'); }
}
