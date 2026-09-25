/* ============ 采购订单已分配付款引用证据（ERP-067：只读派生，复用 ERP-066「付款单 → 采购发票」引用行与 ERP-065 发票登记册） ============

   依赖：app.js（api / toast / fmtMoney / fmtDate / escapeHtml / CURRENT_MODULE_CODE / CURRENT_MODULE）、
         crud.js（renderTable / window.__moduleRows / 模块派生列 render 回调）。
   契约：
   - 列表派生列「付款发票证据」为**虚拟列**：由本文件按页批量取数后渲染，不落库、不改写采购订单、采购发票与付款单；
   - 每页只发起有界批量请求（GET /api/purchase-orders/invoice-payment-evidence-summaries?ids=…，单次 ≤ 200 张订单），
     绝不逐行查库、绝不逐行做存储访问（同一页的订单 Id 一次 / 分批取回，缓存后重渲染）；
   - 后端命中读取上限（truncated）或金额未知（null）时显示「未知」，绝不复用 0 顶替；
   - 「订单归属金额」只统计仅关联本订单的发票上的有效引用行；发票还被其他订单关联时只作**发票级金额**单列
     （不按比例摊派、不猜测归属），界面必须把两类分开标注；
   - 本列与详情只是**已分配付款引用证据**：不是总账、不是法定供应商对账单、不是税务申报、不是付款授权或结算确认，
     也绝不与「付款 → 采购订单」引用金额、订单金额、已开票金额相加减。 */

const POIP_MAX_BATCH = 200;                 // 与后端 PurchaseOrderInvoicePaymentEvidenceSemantics.MaxBatchOrders 同口径
const POIP_BATCH_API = '/api/purchase-orders/invoice-payment-evidence-summaries';

let __poipRowsRef = null;                   // 当前列表页行数组（引用变化 = 列表已重新加载 → 缓存失效）
let __poipInFlight = null;                  // 在途批量请求签名（避免同一页重复请求）
let __poipFailed = false;                   // 本次列表加载失败标记（不无限重试）
const __poipCache = new Map();              // 采购订单 Id → 已分配付款引用证据汇总

/* 列表派生列：已分配付款引用证据徽标（crud.js 列 render 回调；虚拟列，不落库） */
function purchaseOrderInvoicePaymentEvidenceCellHtml(row) {
  const orderId = Number(row && row.id);
  if (!orderId) return '';
  poipInvalidateIfRowsChanged();
  if (__poipCache.has(orderId)) return poipBadgeHtml(__poipCache.get(orderId));
  if (__poipFailed) return '<span class="text-muted">未知（汇总加载失败）</span>';
  poipScheduleLoad();
  return '<span class="text-muted">…（付款发票证据加载中）</span>';
}

/* 列表重新加载（翻页 / 搜索 / 操作后刷新）会换掉 __moduleRows，引用变化即失效缓存，避免展示过期证据 */
function poipInvalidateIfRowsChanged() {
  const rows = window.__moduleRows;
  if (rows !== __poipRowsRef) {
    __poipRowsRef = rows;
    __poipCache.clear();
    __poipFailed = false;
  }
}

/* 批量取回本页（缺失的）订单已分配付款引用证据：分批但**按页**，不逐行请求 */
async function poipScheduleLoad() {
  const rows = __poipRowsRef || [];
  const ids = [...new Set(rows.map(r => Number(r.id)).filter(id => id > 0 && !__poipCache.has(id)))];
  if (!ids.length) return;

  const signature = ids.join(',');
  if (__poipInFlight === signature) return;
  __poipInFlight = signature;

  try {
    for (let i = 0; i < ids.length; i += POIP_MAX_BATCH) {
      const chunk = ids.slice(i, i + POIP_MAX_BATCH);
      const data = await api(`${POIP_BATCH_API}?ids=${chunk.join(',')}`);
      (data.items || []).forEach(item => __poipCache.set(Number(item.purchaseOrderId), item));
    }
    if (__poipRowsRef === rows) poipRerender();
  } catch (err) {
    __poipFailed = true;
    toast('已分配付款引用证据汇总加载失败：' + err.message, 'error');
    if (__poipRowsRef === rows) poipRerender();
  } finally {
    __poipInFlight = null;
  }
}

/* 汇总到达后就地重渲染当前页（仍读缓存，不会再次发起请求） */
function poipRerender() {
  if (CURRENT_MODULE_CODE !== 'purchase-order') return;
  try {
    renderTable(CURRENT_MODULE, { items: __poipRowsRef || [] });
  } catch (err) {
    /* 重渲染失败不影响列表数据本身，忽略即可（数据仍可通过行操作详情查看） */
  }
}

/* 金额：null / undefined = 未知（命中后端有界上限或订单不可用），绝不显示为 0 */
function poipMoney(v) {
  return (v === null || v === undefined) ? '未知' : fmtMoney(v);
}

/* 分桶中文（与后端 PurchaseOrderInvoicePaymentEvidenceSemantics.BucketText 同口径，仅作兜底显示） */
const POIP_BUCKET_LABELS = {
  recorded: '有效已分配付款引用证据（计入有效合计）',
  voided: '已作废引用行（不计入有效合计）',
  invoice_inactive: '发票已失效（草稿 / 已作废，不计入有效合计）',
  invalid: '无效历史证据（不换算 / 不合并 / 不改派）',
  unavailable: '无法确认的证据（付款单或发票不存在 / 已删除）',
};

/* 分桶 → 徽标样式（有效 = 成功，已作废 = 中性，其余 = 警示） */
function poipBucketLevel(bucket) {
  if (bucket === 'recorded') return 'success';
  if (bucket === 'voided') return 'neutral';
  if (bucket === 'invoice_inactive') return 'warning';
  if (bucket === 'invalid') return 'danger';
  return 'warning';
}

/* 归属文案（订单归属金额 vs 只作发票级金额：两者绝不混为一谈） */
function poipAttributionHtml(line) {
  if (!line.isRecordedEvidence) return '<span class="text-muted">不计入有效合计</span>';
  return line.orderAttributable
    ? '<span class="status status-success">可归属本订单</span>'
    : '<span class="status status-warning">仅发票级（不按订单拆分）</span>';
}

/* 行操作：查看采购订单已分配付款引用证据详情（GET /api/purchase-orders/{id}/invoice-payment-evidence，只读、不落库） */
async function showPurchaseOrderInvoicePaymentEvidence(id) {
  try {
    const data = await api(`/api/purchase-orders/${id}/invoice-payment-evidence`);
    const s = data.summary || {};

    const lineRows = (data.lines || []).map(l => `<tr>
      <td>${escapeHtml(l.paymentNo || '')}${l.paymentAvailable ? '' : ' <span class="status status-warning">付款单不可用</span>'}</td>
      <td>${escapeHtml(l.paymentDate ? fmtDate(l.paymentDate) : '—')}</td>
      <td>${escapeHtml(l.paymentStatusText || '')}</td>
      <td>${escapeHtml(l.invoiceIdentityText || '')}<div class="text-muted">${escapeHtml(l.invoiceTypeText || '')}</div></td>
      <td>${escapeHtml(l.invoiceDate ? fmtDate(l.invoiceDate) : '—')}</td>
      <td>${escapeHtml(l.invoiceStatusText || '')}${l.invoiceAvailable ? '' : ' <span class="status status-warning">发票不可用</span>'}<div class="text-muted">到期日：${escapeHtml(l.invoiceDueDateText || '')}</div></td>
      <td>${escapeHtml(l.supplierName || '')}（快照 Id=${l.supplierId}）</td>
      <td class="text-right">${fmtMoney(l.allocatedAmount)} ${escapeHtml(l.currency || '')}</td>
      <td><span class="status status-${poipBucketLevel(l.bucket)}">${escapeHtml(l.bucketText || POIP_BUCKET_LABELS[l.bucket] || '')}</span></td>
      <td>${poipAttributionHtml(l)}</td>
      <td class="text-right">${poipMoney(l.paymentUnallocatedAmount)}</td>
      <td>${escapeHtml(l.allocatedAt ? fmtDate(l.allocatedAt) : '—')}${l.isVoided && l.voidReason ? '<div class="text-muted">作废原因：' + escapeHtml(l.voidReason) + '</div>' : ''}</td>
      <td>${escapeHtml(l.reason || '')}</td>
    </tr>`).join('');

    const modal = document.getElementById('modal');
    modal.innerHTML = `<div class="modal modal-lg" style="width:1460px;max-width:97vw;max-height:92vh;overflow:auto">
      <h3>🧾 采购订单已分配付款引用证据：${escapeHtml(s.orderNo || '')}</h3>
      <div class="pd-hint">四类证据严格分列、绝不轧差：①订单金额（已订）②收货数量（已收，见执行进度）③供应商发票证据（已开票，见「发票证据」）④已分配付款引用证据（本页）。本页不是总账、不是法定供应商对账单、不是税务申报、不是付款授权，也不是结算确认。</div>
      <div class="form-grid" style="grid-template-columns:repeat(4,1fr)">
        <div><b>订单可用性</b>：${escapeHtml(s.orderStateText || '')}（当前状态 ${escapeHtml(s.orderStatusText || '')}）</div>
        <div><b>订单金额（已订）</b>：${poipMoney(s.orderedAmount)} ${escapeHtml(s.orderCurrency || '')}</div>
        <div><b>关联到本订单的发票张数</b>：${poipMoney(s.linkedInvoiceCount)}</div>
        <div><b>证据短标签</b>：${escapeHtml(s.evidenceLabel || '')}</div>
        <div><b>有效已分配付款引用（发票级）</b>：${poipMoney(s.activeAllocatedPaymentAmount)} ${escapeHtml(s.orderCurrency || '')}</div>
        <div><b>其中可安全归属本订单</b>：${poipMoney(s.attributableAllocatedPaymentAmount)} ${escapeHtml(s.orderCurrency || '')}</div>
        <div><b>仅发票级（不按订单拆分）</b>：${poipMoney(s.unattributableAllocatedPaymentAmount)} ${escapeHtml(s.orderCurrency || '')}
          <div class="text-muted">${poipMoney(s.unattributableInvoiceCount)} 张发票还被其他采购订单关联</div></div>
        <div><b>有效引用行 / 付款单</b>：${poipMoney(s.activeAllocationCount)} 条 / ${poipMoney(s.activePaymentCount)} 张
          <div class="text-muted">其中可归属发票 ${poipMoney(s.attributableInvoiceCount)} 张</div></div>
        <div><b>已作废引用行</b>：${poipMoney(s.voidedCount)} 条 / ${poipMoney(s.voidedAmount)}</div>
        <div><b>发票已失效（草稿 / 已作废）</b>：${poipMoney(s.invoiceInactiveCount)} 条 / ${poipMoney(s.invoiceInactiveAmount)}</div>
        <div><b>无效历史证据</b>：${poipMoney(s.invalidCount)} 条 / ${poipMoney(s.invalidAmount)}</div>
        <div><b>无法确认的证据</b>：${poipMoney(s.unavailableCount)} 条 / ${poipMoney(s.unavailableAmount)}</div>
      </div>

      <div class="pd-hint">证据说明：${escapeHtml(s.evidenceNote || '')}</div>

      <h4>逐条已分配付款引用证据（${data.lineCount || 0} 条；只按 ERP-066 的持久化引用行派生，只经 ERP-043 / ERP-065 的关联行归属）</h4>
      <div class="table-wrap" style="max-height:42vh;overflow:auto">
        <table><thead><tr>
          <th>付款单号</th><th>付款日期</th><th>付款状态</th><th>供应商发票身份</th><th>开票日期</th><th>发票状态 / 到期日</th>
          <th>供应商（引用行快照）</th><th class="text-right">引用金额</th><th>证据分桶</th><th>订单归属</th>
          <th class="text-right">付款单未指向发票金额</th><th>登记 / 作废</th><th>说明（为什么计入 / 不计入）</th>
        </tr></thead>
        <tbody>${lineRows || '<tr><td colspan="13" class="empty">该采购订单没有任何「付款单 → 采购发票」引用行（已分配付款引用证据缺口，不代表未付款 / 已付款 / 已结清 / 逾期）</td></tr>'}</tbody></table>
      </div>

      <div class="pd-hint">派生口径：${escapeHtml(data.rule || '')}</div>
      <div class="pd-hint">范围说明：${escapeHtml(data.scopeNote || '')}</div>
      <div class="pd-hint">边界：${escapeHtml(data.boundary || '')}</div>
      <div class="pd-hint">维度分离：${escapeHtml(data.separateDimension || '')} ${escapeHtml(data.evidenceDimensionRule || '')}</div>
      <div class="pd-hint">引用行登记口径：${escapeHtml(data.allocationRule || '')}</div>
      <div class="pd-hint">引用行登记册边界：${escapeHtml(data.allocationBoundary || '')}</div>
      <div class="pd-hint">发票 → 采购订单 关联口径：${escapeHtml(data.invoiceLinkageRule || '')}</div>
      <div class="pd-hint">发票金额等式：${escapeHtml(data.amountEquation || '')}</div>
      <div class="modal-footer"><button class="btn btn-neutral" onclick="closeModal()">关闭</button></div>
    </div>`;
    modal.style.display = 'flex';
    return data;
  } catch (err) { toast(err.message, 'error'); }
}

/* 列表徽标：有效已分配付款引用金额 + 可归属本订单金额 + 付款单张数；
   历史 / 无效 / 发票失效 / 无法确认只以 ⚠ 与悬停说明提示，绝不计入有效合计；发票级金额单独标注 */
function poipBadgeHtml(s) {
  if (!s) return '<span class="text-muted">未知</span>';

  const title = `${s.evidenceLabel || ''} ${s.orderStateText || ''} ${s.evidenceNote || ''}`.trim();

  if (s.truncated) {
    return `<span class="status status-warning" title="${escapeHtml(title)}">未知（超出有界读取上限）</span>`;
  }

  const parts = [];
  const active = s.activeAllocatedPaymentAmount;
  const attributable = s.attributableAllocatedPaymentAmount;
  if (active !== null && active !== undefined && active > 0) {
    parts.push(`<span class="status status-success">付款发票证据 ${poipMoney(active)}</span>`);
    if (attributable !== null && attributable !== undefined && attributable > 0) {
      parts.push(`可归属本单 ${poipMoney(attributable)}`);
    }
    if (s.unattributableAllocatedPaymentAmount !== null
      && s.unattributableAllocatedPaymentAmount !== undefined
      && s.unattributableAllocatedPaymentAmount > 0) {
      parts.push('含仅发票级金额');
    }
    if (s.activePaymentCount !== null && s.activePaymentCount !== undefined) {
      parts.push(`${s.activePaymentCount} 张付款单`);
    }
  } else if (s.hasEvidence) {
    parts.push('<span class="status status-warning">仅有历史 / 无效 / 发票失效证据</span>');
  } else {
    parts.push('<span class="status status-neutral">无已分配付款引用证据</span>');
  }
  if (s.hasHistoricalEvidence) parts.push('⚠ 含历史证据');

  return `<span title="${escapeHtml(title)}">${parts.join(' · ')}</span>`;
}
