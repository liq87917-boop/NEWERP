/* ============ 采购订单付款引用证据（ERP-050：只读派生，复用 ERP-049 持久化引用行与同一套分桶口径） ============ */

/* 依赖：app.js（api / toast / fmtMoney / fmtDate / escapeHtml / CURRENT_MODULE_CODE / CURRENT_MODULE）、
         crud.js（renderTable / window.__moduleRows / 模块派生列 render 回调）。
   契约：
   - 列表派生列「付款引用证据」为**虚拟列**：由本文件按页批量取数后渲染，不落库、不改写采购订单与付款单；
   - 每页只发起有界批量请求（GET /api/purchase-orders/payment-evidence-summaries?ids=…，单次 ≤ 200 张订单），
     绝不逐行查库（同一页的订单 Id 一次 / 分批取回，缓存后重渲染）；
   - 后端命中读取上限（truncated）或金额未知（null）时显示「未知」，绝不复用 0 顶替；
   - 本列与详情只是**付款引用证据**：不是银行付款凭证、不是应付余额、不是发票核销、不是结算结果或账龄，
     没有引用行时只显示「无付款引用证据」，绝不呈现为未付款 / 已付款 / 已结清 / 逾期。 */

const POPE_MAX_BATCH = 200;                 // 与后端 PurchaseOrderPaymentEvidenceSemantics.MaxBatchOrders 同口径
const POPE_BATCH_API = '/api/purchase-orders/payment-evidence-summaries';

let __popeRowsRef = null;                   // 当前列表页行数组（引用变化 = 列表已重新加载 → 缓存失效）
let __popeInFlight = null;                  // 在途批量请求签名（避免同一页重复请求）
let __popeFailed = false;                   // 本次列表加载失败标记（不无限重试）
const __popeCache = new Map();              // 采购订单 Id → 付款引用证据汇总

/* 列表派生列：付款引用证据徽标（crud.js 列 render 回调；虚拟列，不落库） */
function purchaseOrderPaymentEvidenceCellHtml(row) {
  const orderId = Number(row && row.id);
  if (!orderId) return '';
  popeInvalidateIfRowsChanged();
  if (__popeCache.has(orderId)) return popeBadgeHtml(__popeCache.get(orderId));
  if (__popeFailed) return '<span class="text-muted">未知（汇总加载失败）</span>';
  popeScheduleLoad();
  return '<span class="text-muted">…（付款引用证据加载中）</span>';
}

/* 列表重新加载（翻页 / 搜索 / 操作后刷新）会换掉 __moduleRows，引用变化即失效缓存，避免展示过期证据 */
function popeInvalidateIfRowsChanged() {
  const rows = window.__moduleRows;
  if (rows !== __popeRowsRef) {
    __popeRowsRef = rows;
    __popeCache.clear();
    __popeFailed = false;
  }
}

/* 批量取回本页（缺失的）订单付款引用证据：分批但**按页**，不逐行请求 */
async function popeScheduleLoad() {
  const rows = __popeRowsRef || [];
  const ids = [...new Set(rows.map(r => Number(r.id)).filter(id => id > 0 && !__popeCache.has(id)))];
  if (!ids.length) return;

  const signature = ids.join(',');
  if (__popeInFlight === signature) return;
  __popeInFlight = signature;

  try {
    for (let i = 0; i < ids.length; i += POPE_MAX_BATCH) {
      const chunk = ids.slice(i, i + POPE_MAX_BATCH);
      const data = await api(`${POPE_BATCH_API}?ids=${chunk.join(',')}`);
      (data.items || []).forEach(item => __popeCache.set(Number(item.purchaseOrderId), item));
    }
    if (__popeRowsRef === rows) popeRerender();
  } catch (err) {
    __popeFailed = true;
    toast('付款引用证据汇总加载失败：' + err.message, 'error');
    if (__popeRowsRef === rows) popeRerender();
  } finally {
    __popeInFlight = null;
  }
}

/* 汇总到达后就地重渲染当前页（仍读缓存，不会再次发起请求） */
function popeRerender() {
  if (CURRENT_MODULE_CODE !== 'purchase-order') return;
  try {
    renderTable(CURRENT_MODULE, { items: __popeRowsRef || [] });
  } catch (err) {
    /* 重渲染失败不影响列表数据本身，忽略即可（数据仍可通过行操作详情查看） */
  }
}

/* 金额：null / undefined = 未知（命中后端有界上限或订单不可用），绝不显示为 0 */
function popeMoney(v) {
  return (v === null || v === undefined) ? '未知' : fmtMoney(v);
}

/* 分桶中文（与后端 PurchaseOrderPaymentEvidenceSemantics.BucketText 同口径，仅作兜底显示） */
const POPE_BUCKET_LABELS = {
  recorded: '有效付款引用证据（计入有效合计）',
  voided: '已作废历史证据（不计入有效合计）',
  invalid: '无效历史证据（不换算 / 不合并 / 不改派）',
  unavailable: '无法确认的证据（付款单不存在 / 已删除）',
};

/* 分桶 → 徽标样式（有效=成功，已作废=中性，无效 / 无法确认=警示） */
function popeBucketLevel(bucket) {
  if (bucket === 'recorded') return 'success';
  if (bucket === 'voided') return 'neutral';
  if (bucket === 'invalid') return 'danger';
  return 'warning';
}

/* 列表徽标：有效付款引用金额 + 付款单张数；历史 / 无效 / 无法确认只以 ⚠ 与悬停说明提示，绝不计入有效合计 */
function popeBadgeHtml(s) {
  if (!s) return '<span class="text-muted">未知</span>';

  const title = `${s.evidenceLabel || ''} ${s.orderStateText || ''} ${s.evidenceNote || ''}`.trim();

  if (s.truncated) {
    return `<span class="status status-warning" title="${escapeHtml(title)}">未知（超出有界读取上限）</span>`;
  }

  const parts = [];
  if (s.recordedAllocatedAmount !== null && s.recordedAllocatedAmount !== undefined && s.recordedAllocatedAmount > 0) {
    parts.push(`<span class="status status-success">付款引用 ${popeMoney(s.recordedAllocatedAmount)}</span>`);
    if (s.recordedPaymentCount !== null && s.recordedPaymentCount !== undefined) {
      parts.push(`${s.recordedPaymentCount} 张付款单`);
    }
    if (s.unallocatedPaymentAmount !== null && s.unallocatedPaymentAmount !== undefined) {
      parts.push(`未指向本单 ${popeMoney(s.unallocatedPaymentAmount)}`);
    }
  } else if (s.hasPaymentEvidence) {
    parts.push('<span class="status status-warning">仅有历史 / 无效证据</span>');
  } else {
    parts.push('<span class="status status-neutral">无付款引用证据</span>');
  }
  if (s.hasHistoricalEvidence) parts.push('⚠ 含历史证据');

  return `<span title="${escapeHtml(title)}">${parts.join(' · ')}</span>`;
}

/* 行操作：查看采购订单付款引用证据详情（GET /api/purchase-orders/{id}/payment-evidence，只读、不落库） */
async function showPurchaseOrderPaymentEvidence(id) {
  try {
    const data = await api(`/api/purchase-orders/${id}/payment-evidence`);
    const s = data.summary || {};

    const lineRows = (data.lines || []).map(l => `<tr>
      <td>${escapeHtml(l.paymentNo || '')}${l.paymentAvailable ? '' : ' <span class="status status-warning">付款单不可用</span>'}</td>
      <td>${escapeHtml(l.paymentDate ? fmtDate(l.paymentDate) : '—')}</td>
      <td>${escapeHtml(l.paymentStatusText || '')}</td>
      <td class="text-right">${fmtMoney(l.paymentAmount)} ${escapeHtml(l.currency || '')}</td>
      <td class="text-right">${fmtMoney(l.allocatedAmount)}</td>
      <td><span class="status status-${popeBucketLevel(l.bucket)}">${escapeHtml(l.bucketText || POPE_BUCKET_LABELS[l.bucket] || '')}</span></td>
      <td>${escapeHtml(l.statusText || '')}</td>
      <td>${escapeHtml(l.supplierName || '')}（快照 Id=${l.supplierId}）</td>
      <td>${escapeHtml(l.orderNo || '')}（${escapeHtml(l.orderCurrency || '')} · ${escapeHtml(l.orderStatusText || '')}）</td>
      <td class="text-right">${popeMoney(l.paymentUnallocatedAmount)}</td>
      <td>${escapeHtml(l.allocatedAt ? fmtDate(l.allocatedAt) : '—')}${l.voidedAt ? ' → ' + escapeHtml(fmtDate(l.voidedAt)) : ''}${l.voidReason ? '（' + escapeHtml(l.voidReason) + '）' : ''}</td>
      <td>${escapeHtml(l.reason || '')}</td>
    </tr>`).join('');

    const modal = document.getElementById('modal');
    modal.innerHTML = `<div class="modal modal-lg" style="width:1440px;max-width:96vw;max-height:92vh;overflow:auto">
      <h3>💳 采购订单付款引用证据：${escapeHtml(s.orderNo || '')}</h3>
      <div class="pd-hint">
        ⚠️ 只读的<b>付款引用证据</b>（付款单指向本订单的引用行）：<b>不是</b>银行付款凭证、<b>不是</b>应付余额、<b>不是</b>发票核销、
        <b>不是</b>结算结果或账龄 —— 不判断是否已付款 / 已结清 / 逾期，也不得据以付款。
      </div>
      <div class="form-grid" style="grid-template-columns:repeat(4,1fr)">
        <div><b>证据结论</b>：${escapeHtml(s.evidenceLabel || '')}</div>
        <div><b>有效已引用（付款引用证据）</b>：${popeMoney(s.recordedAllocatedAmount)} ${escapeHtml(s.orderCurrency || '')}</div>
        <div><b>付款单张数（有效行去重）</b>：${popeMoney(s.recordedPaymentCount)}</div>
        <div><b>引用行条数（含历史）</b>：${popeMoney(s.allocationCount)}</div>
        <div><b>参与证据的付款单金额快照</b>：${popeMoney(s.recordedPaymentAmount)} ${escapeHtml(s.orderCurrency || '')}</div>
        <div><b>其中未指向本订单</b>：${popeMoney(s.unallocatedPaymentAmount)} ${escapeHtml(s.orderCurrency || '')}</div>
        <div><b>已作废历史证据</b>：${popeMoney(s.voidedAllocationCount)} 条 / ${popeMoney(s.voidedAllocatedAmount)}</div>
        <div><b>无效历史证据</b>：${popeMoney(s.invalidAllocationCount)} 条 / ${popeMoney(s.invalidAllocatedAmount)}</div>
        <div><b>无法确认的证据</b>：${popeMoney(s.unavailableAllocationCount)} 条 / ${popeMoney(s.unavailableAllocatedAmount)}</div>
        <div><b>订单金额</b>：${popeMoney(s.orderedAmount)} ${escapeHtml(s.orderCurrency || '')}</div>
        <div><b>订单可用性</b>：${escapeHtml(s.orderStateText || '')}（当前状态 ${escapeHtml(s.orderStatusText || '')}）</div>
        <div><b>币种精度</b>：${popeMoney(s.amountDecimals)} 位小数</div>
      </div>

      <div class="pd-hint">证据说明：${escapeHtml(s.evidenceNote || '')}</div>

      <h4>逐条付款引用证据（${data.lineCount || 0} 条；只按 ERP-049 持久化引用行派生）</h4>
      <div class="table-wrap" style="max-height:40vh;overflow:auto">
        <table><thead><tr>
          <th>付款单号</th><th>付款日期</th><th>付款单状态</th><th class="text-right">付款单金额</th>
          <th class="text-right">引用金额</th><th>证据分桶</th><th>引用行状态</th><th>供应商快照</th>
          <th>订单快照</th><th class="text-right">该付款单未指向本单</th><th>登记 / 作废</th><th>说明（为什么计入 / 不计入）</th>
        </tr></thead>
        <tbody>${lineRows || '<tr><td colspan="12" class="empty">该采购订单没有任何付款引用行（付款引用证据缺口，不代表未付款 / 已付款 / 已结清 / 逾期）</td></tr>'}</tbody></table>
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

