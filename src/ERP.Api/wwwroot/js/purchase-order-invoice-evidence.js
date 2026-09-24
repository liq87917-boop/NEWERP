/* ============ 采购订单发票证据（ERP-048：只读派生，复用 ERP-043 持久化关联行与 ERP-044 覆盖口径） ============ */

/* 依赖：app.js（api / toast / fmtMoney / fmtDate / escapeHtml / statusHtml / CURRENT_MODULE_CODE / CURRENT_MODULE）、
         crud.js（renderTable / window.__moduleRows / 模块派生列 render 回调）。
   契约：
   - 列表派生列「发票证据」为**虚拟列**：由本文件按页批量取数后渲染，不落库、不改写采购订单；
   - 每页只发起有界批量请求（GET /api/purchase-orders/invoice-evidence-summaries?ids=…，单次 ≤ 200 张订单），
     绝不逐行查库（同一页的订单 Id 一次 / 分批取回，缓存后重渲染）；
   - 后端命中读取上限（truncated）或金额未知（null）时显示「未知」，绝不复用 0 顶替；
   - 本列与详情只是**采购发票证据**：不是应付余额、付款授权、税务申报判断，也不是结算状态或账龄。 */

const POIE_MAX_BATCH = 200;                 // 与后端 PurchaseOrderInvoiceEvidenceSemantics.MaxBatchOrders 同口径
const POIE_BATCH_API = '/api/purchase-orders/invoice-evidence-summaries';

let __poieRowsRef = null;                   // 当前列表页行数组（引用变化 = 列表已重新加载 → 缓存失效）
let __poieInFlight = null;                  // 在途批量请求签名（避免同一页重复请求）
let __poieFailed = false;                   // 本次列表加载失败标记（不无限重试）
const __poieCache = new Map();              // 采购订单 Id → 发票证据汇总

/* 列表派生列：发票证据徽标（crud.js 列 render 回调；虚拟列，不落库） */
function purchaseOrderInvoiceEvidenceCellHtml(row) {
  const orderId = Number(row && row.id);
  if (!orderId) return '';
  poieInvalidateIfRowsChanged();
  if (__poieCache.has(orderId)) return poieBadgeHtml(__poieCache.get(orderId));
  if (__poieFailed) return '<span class="text-muted">未知（汇总加载失败）</span>';
  poieScheduleLoad();
  return '<span class="text-muted">…（发票证据加载中）</span>';
}

/* 列表重新加载（翻页 / 搜索 / 操作后刷新）会换掉 __moduleRows，引用变化即失效缓存，避免展示过期证据 */
function poieInvalidateIfRowsChanged() {
  const rows = window.__moduleRows;
  if (rows !== __poieRowsRef) {
    __poieRowsRef = rows;
    __poieCache.clear();
    __poieFailed = false;
  }
}

/* 批量取回本页（缺失的）订单发票证据：分批但**按页**，不逐行请求 */
async function poieScheduleLoad() {
  const rows = __poieRowsRef || [];
  const ids = [...new Set(rows.map(r => Number(r.id)).filter(id => id > 0 && !__poieCache.has(id)))];
  if (!ids.length) return;

  const signature = ids.join(',');
  if (__poieInFlight === signature) return;
  __poieInFlight = signature;

  try {
    for (let i = 0; i < ids.length; i += POIE_MAX_BATCH) {
      const chunk = ids.slice(i, i + POIE_MAX_BATCH);
      const data = await api(`${POIE_BATCH_API}?ids=${chunk.join(',')}`);
      (data.items || []).forEach(item => __poieCache.set(Number(item.purchaseOrderId), item));
    }
    if (__poieRowsRef === rows) poieRerender();
  } catch (err) {
    __poieFailed = true;
    toast('发票证据汇总加载失败：' + err.message, 'error');
    if (__poieRowsRef === rows) poieRerender();
  } finally {
    __poieInFlight = null;
  }
}

/* 汇总到达后就地重渲染当前页（仍读缓存，不会再次发起请求） */
function poieRerender() {
  if (CURRENT_MODULE_CODE !== 'purchase-order') return;
  try {
    renderTable(CURRENT_MODULE, { items: __poieRowsRef || [] });
  } catch (err) {
    /* 重渲染失败不影响列表数据本身，忽略即可（数据仍可通过行操作详情查看） */
  }
}

/* 金额：null / undefined = 未知（命中后端有界上限或订单不可用），绝不显示为 0 */
function poieMoney(v) {
  return (v === null || v === undefined) ? '未知' : fmtMoney(v);
}

/* 覆盖状态中文（与后端 SupplierInvoiceReconciliationSemantics 短标签同口径，仅作兜底显示） */
const POIE_COVERAGE_LABELS = {
  fully_invoiced: '已全额开票',
  partially_invoiced: '部分开票',
  not_invoiced: '未开票',
  unknown: '未知',
};

/* 证据分桶中文（与后端 PurchaseOrderInvoiceEvidenceSemantics.BucketText 同口径，仅作兜底显示） */
const POIE_BUCKET_LABELS = {
  recorded: '已登记证据（计入已开票金额）',
  draft: '草稿证据（不计入已开票金额）',
  voided: '已作废历史证据（不计入已开票金额）',
  invalid: '无效历史证据（不换算 / 不合并 / 不改派）',
  unavailable: '无法确认的证据（金额未知）',
};

/* 分桶 → 徽标样式（已登记=成功，草稿=中性，已作废=中性，无效 / 无法确认=警示） */
function poieBucketLevel(bucket) {
  if (bucket === 'recorded') return 'success';
  if (bucket === 'draft') return 'info';
  if (bucket === 'voided') return 'neutral';
  if (bucket === 'invalid') return 'danger';
  return 'warning';
}

/* 列表徽标：覆盖状态 + 已开票 / 未开票金额；历史 / 无效证据只以 ⚠ 与悬停说明提示，绝不计入已开票金额 */
function poieBadgeHtml(s) {
  if (!s) return '<span class="text-muted">未知</span>';

  const title = `${s.coverageText || ''} ${s.orderStateText || ''} ${s.evidenceNote || ''}`.trim();

  if (s.truncated) {
    return `<span class="status status-warning" title="${escapeHtml(title)}">未知（超出有界读取上限）</span>`;
  }

  const coverage = s.coverageStatus || 'unknown';
  const level = coverage === 'fully_invoiced' ? 'success'
    : coverage === 'partially_invoiced' ? 'info'
      : coverage === 'not_invoiced' ? 'neutral' : 'warning';

  const parts = [escapeHtml(s.coverageLabel || POIE_COVERAGE_LABELS[coverage] || coverage)];
  if (s.recordedAllocatedAmount !== null && s.recordedAllocatedAmount !== undefined) {
    parts.push(`已开票 ${poieMoney(s.recordedAllocatedAmount)}`);
  }
  if (s.remainingUninvoicedAmount !== null && s.remainingUninvoicedAmount !== undefined) {
    parts.push(`未开票 ${poieMoney(s.remainingUninvoicedAmount)}`);
  }
  if (s.hasHistoricalEvidence) parts.push('⚠ 含历史证据');

  return `<span class="status status-${level}" title="${escapeHtml(title)}">${parts.join(' · ')}</span>`;
}

/* 行操作：查看采购订单发票证据详情（GET /api/purchase-orders/{id}/invoice-evidence，只读、不落库） */
async function showPurchaseOrderInvoiceEvidence(id) {
  try {
    const data = await api(`/api/purchase-orders/${id}/invoice-evidence`);
    const s = data.summary || {};

    const lineRows = (data.lines || []).map(l => `<tr>
      <td>${escapeHtml(l.identityText || '')}</td>
      <td>${escapeHtml(l.invoiceDate ? fmtDate(l.invoiceDate) : '—')}</td>
      <td>${escapeHtml(l.invoiceStatusText || '')}</td>
      <td><span class="status status-${poieBucketLevel(l.bucket)}">${escapeHtml(l.bucketText || POIE_BUCKET_LABELS[l.bucket] || '')}</span></td>
      <td class="text-right">${fmtMoney(l.allocatedAmount)} ${escapeHtml(l.currency || '')}</td>
      <td>${escapeHtml(l.supplierName || '')}（发票供应商 Id=${l.supplierId}｜关联行快照 Id=${l.allocationSupplierId}）</td>
      <td>${escapeHtml(l.reason || '')}</td>
    </tr>`).join('');

    const modal = document.getElementById('modal');
    modal.innerHTML = `<div class="modal modal-lg" style="width:1280px;max-width:96vw;max-height:92vh;overflow:auto">
      <h3>🧾 采购订单发票证据：${escapeHtml(s.orderNo || '')}</h3>
      <div class="form-grid" style="grid-template-columns:repeat(4,1fr)">
        <div><b>覆盖状态</b>：<span class="status status-${s.coverageStatus === 'fully_invoiced' ? 'success' : s.coverageStatus === 'partially_invoiced' ? 'info' : s.coverageStatus === 'not_invoiced' ? 'neutral' : 'warning'}">${escapeHtml(s.coverageLabel || POIE_COVERAGE_LABELS[s.coverageStatus] || '')}</span></div>
        <div><b>已开票（发票证据）</b>：${poieMoney(s.recordedAllocatedAmount)} ${escapeHtml(s.orderCurrency || '')}</div>
        <div><b>未开票</b>：${poieMoney(s.remainingUninvoicedAmount)} ${escapeHtml(s.orderCurrency || '')}</div>
        <div><b>订单金额</b>：${poieMoney(s.orderedAmount)} ${escapeHtml(s.orderCurrency || '')}</div>
        <div><b>已登记发票张数</b>：${poieMoney(s.recordedInvoiceCount)}</div>
        <div><b>草稿证据</b>：${poieMoney(s.draftInvoiceCount)} 张 / ${poieMoney(s.draftAllocatedAmount)}</div>
        <div><b>已作废历史证据</b>：${poieMoney(s.voidedInvoiceCount)} 张 / ${poieMoney(s.voidedAllocatedAmount)}</div>
        <div><b>无效历史证据</b>：${poieMoney(s.invalidAllocationCount)} 条 / ${poieMoney(s.invalidAllocatedAmount)}</div>
        <div><b>无法确认的证据</b>：${poieMoney(s.unavailableAllocationCount)} 条 / ${poieMoney(s.unavailableAllocatedAmount)}</div>
        <div><b>关联行条数</b>：${poieMoney(s.allocationCount)}</div>
        <div><b>订单可用性</b>：${escapeHtml(s.orderStateText || '')}（当前状态 ${escapeHtml(s.orderStatusText || '')}）</div>
        <div><b>覆盖口径</b>：${escapeHtml(s.coverageText || '')}</div>
      </div>

      <div class="pd-hint">证据说明：${escapeHtml(s.evidenceNote || '')}</div>

      <h4>逐条发票证据（${data.lineCount || 0} 条；只按 ERP-043 持久化关联行派生）</h4>
      <div class="table-wrap" style="max-height:40vh;overflow:auto">
        <table><thead><tr>
          <th>发票身份</th><th>开票日期</th><th>发票状态</th><th>证据分桶</th>
          <th class="text-right">关联金额</th><th>发票供应商 / 关联行快照</th><th>说明（为什么计入 / 不计入）</th>
        </tr></thead>
        <tbody>${lineRows || '<tr><td colspan="7" class="empty">该采购订单没有任何发票关联行（发票证据缺口，不代表已付款 / 已结清 / 逾期）</td></tr>'}</tbody></table>
      </div>

      <div class="pd-hint">派生口径：${escapeHtml(data.rule || '')}</div>
      <div class="pd-hint">范围说明：${escapeHtml(data.scopeNote || '')}</div>
      <div class="pd-hint">金额等式：${escapeHtml(data.amountEquation || '')}</div>
      <div class="pd-hint">关联口径：${escapeHtml(data.linkageRule || '')}</div>
      <div class="pd-hint">边界：${escapeHtml(data.boundary || '')}</div>
      <div class="modal-footer"><button class="btn btn-neutral" onclick="closeModal()">关闭</button></div>
    </div>`;
    modal.style.display = 'flex';
    return data;
  } catch (err) { toast(err.message, 'error'); }
}
