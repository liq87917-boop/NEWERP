/* ============ 销售订单销项发票证据（ERP-056：只读派生，复用 ERP-055 持久化发票证据行与同一套分桶口径） ============ */

/* 依赖：app.js（api / toast / fmtMoney / fmtDate / escapeHtml / CURRENT_MODULE_CODE / CURRENT_MODULE）、
         crud.js（renderTable / window.__moduleRows / 模块派生列 render 回调）。
   契约：
   - 列表派生列「销项发票证据」为**虚拟列**：由本文件按页批量取数后渲染，不落库、不改写销售订单与发票证据；
   - 每页只发起有界批量请求（GET /api/sales-orders/invoice-evidence-summaries?ids=…，单次 ≤ 200 张订单），
     绝不逐行查库（同一页的订单 Id 一次 / 分批取回，缓存后重渲染）；
   - 后端命中读取上限（truncated）或金额未知（null）时显示「未知」，绝不复用 0 顶替；
   - 本列与详情只是**销项发票证据**（发票含税总额分摊到本订单的登记证据）：不是发票开具系统、不是税务申报或
     销项税金计算、不是应收余额、不是货款核销、不是客户对账单、不是结算结果或账龄；
     没有分摊行时只显示「无销项发票证据」，绝不呈现为未开票 / 已开票 / 欠税 / 已收款 / 已结清 / 逾期。 */

const SOIE_MAX_BATCH = 200;                 // 与后端 SalesOrderInvoiceEvidenceSemantics.MaxBatchOrders 同口径
const SOIE_BATCH_API = '/api/sales-orders/invoice-evidence-summaries';

let __soieRowsRef = null;                   // 当前列表页行数组（引用变化 = 列表已重新加载 → 缓存失效）
let __soieInFlight = null;                  // 在途批量请求签名（避免同一页重复请求）
let __soieFailed = false;                   // 本次列表加载失败标记（不无限重试）
const __soieCache = new Map();              // 销售订单 Id → 销项发票证据汇总

/* 列表派生列：销项发票证据徽标（crud.js 列 render 回调；虚拟列，不落库） */
function salesOrderInvoiceEvidenceCellHtml(row) {
  const orderId = Number(row && row.id);
  if (!orderId) return '';
  soieInvalidateIfRowsChanged();
  if (__soieCache.has(orderId)) return soieBadgeHtml(__soieCache.get(orderId));
  if (__soieFailed) return '<span class="text-muted">未知（汇总加载失败）</span>';
  soieScheduleLoad();
  return '<span class="text-muted">…（销项发票证据加载中）</span>';
}

/* 列表重新加载（翻页 / 搜索 / 操作后刷新）会换掉 __moduleRows，引用变化即失效缓存，避免展示过期证据 */
function soieInvalidateIfRowsChanged() {
  const rows = window.__moduleRows;
  if (rows !== __soieRowsRef) {
    __soieRowsRef = rows;
    __soieCache.clear();
    __soieFailed = false;
  }
}

/* 批量取回本页（缺失的）订单销项发票证据：分批但**按页**，不逐行请求 */
async function soieScheduleLoad() {
  const rows = __soieRowsRef || [];
  const ids = [...new Set(rows.map(r => Number(r.id)).filter(id => id > 0 && !__soieCache.has(id)))];
  if (!ids.length) return;

  const signature = ids.join(',');
  if (__soieInFlight === signature) return;
  __soieInFlight = signature;

  try {
    for (let i = 0; i < ids.length; i += SOIE_MAX_BATCH) {
      const chunk = ids.slice(i, i + SOIE_MAX_BATCH);
      const data = await api(`${SOIE_BATCH_API}?ids=${chunk.join(',')}`);
      (data.items || []).forEach(item => __soieCache.set(Number(item.salesOrderId), item));
    }
    if (__soieRowsRef === rows) soieRerender();
  } catch (err) {
    __soieFailed = true;
    toast('销项发票证据汇总加载失败：' + err.message, 'error');
    if (__soieRowsRef === rows) soieRerender();
  } finally {
    __soieInFlight = null;
  }
}

/* 汇总到达后就地重渲染当前页（仍读缓存，不会再次发起请求） */
function soieRerender() {
  if (CURRENT_MODULE_CODE !== 'sales-order') return;
  try {
    renderTable(CURRENT_MODULE, { items: __soieRowsRef || [] });
  } catch (err) {
    /* 重渲染失败不影响列表数据本身，忽略即可（数据仍可通过行操作详情查看） */
  }
}

/* 金额：null / undefined = 未知（命中后端有界上限或订单不可用），绝不显示为 0 */
function soieMoney(v) {
  return (v === null || v === undefined) ? '未知' : fmtMoney(v);
}

/* 证据状态中文（与后端 SalesOrderInvoiceEvidenceSemantics.EvidenceLabel 同口径，仅作兜底显示） */
const SOIE_STATUS_LABELS = {
  recorded: '有销项发票证据',
  historical_only: '仅有草稿 / 作废 / 无效销项发票证据',
  none: '无销项发票证据',
  unknown: '未知（超出有界读取上限）',
};

/* 分桶中文（与后端 SalesOrderInvoiceEvidenceSemantics.BucketText 同口径，仅作兜底显示） */
const SOIE_BUCKET_LABELS = {
  recorded: '有效销项发票证据（计入有效合计）',
  draft: '草稿发票证据（发票未登记：不计入有效合计）',
  voided: '已作废历史证据（不计入有效合计）',
  invalid: '无效历史证据（客户 / 币种 / 金额等式或快照不一致：不换算 / 不合并 / 不改派）',
  unavailable: '无法确认的证据（发票证据或订单不存在 / 已删除）',
};

/* 分桶 → 徽标样式（有效=成功，草稿 / 已作废=中性，无效=警示，无法确认=警示） */
function soieBucketLevel(bucket) {
  if (bucket === 'recorded') return 'success';
  if (bucket === 'voided' || bucket === 'draft') return 'neutral';
  if (bucket === 'invalid') return 'danger';
  return 'warning';
}

/* 状态 → 徽标样式 */
function soieStatusLevel(status) {
  if (status === 'recorded') return 'success';
  if (status === 'none') return 'neutral';
  if (status === 'historical_only') return 'warning';
  return 'danger';
}

/* 列表徽标：有效销项发票已分摊金额 + 发票张数；草稿 / 已作废 / 无效 / 无法确认只以 ⚠ 与悬停说明提示 */
function soieBadgeHtml(s) {
  if (!s) return '<span class="text-muted">未知</span>';

  const title = `${s.invoiceEvidenceLabel || ''} ${s.orderStateText || ''} ${s.invoiceEvidenceNote || ''}`.trim();

  if (s.truncated) {
    return `<span class="status status-warning" title="${escapeHtml(title)}">未知（超出有界读取上限）</span>`;
  }

  const parts = [];
  if (s.recordedInvoicedAmount !== null && s.recordedInvoicedAmount !== undefined && s.recordedInvoicedAmount > 0) {
    parts.push(`<span class="status status-success">销项发票 ${soieMoney(s.recordedInvoicedAmount)}</span>`);
    if (s.recordedInvoiceCount !== null && s.recordedInvoiceCount !== undefined) {
      parts.push(`${s.recordedInvoiceCount} 张发票`);
    }
    if (s.unreferencedInvoiceAmount !== null && s.unreferencedInvoiceAmount !== undefined) {
      parts.push(`未指向本单 ${soieMoney(s.unreferencedInvoiceAmount)}`);
    }
  } else if (s.hasInvoiceEvidence) {
    parts.push('<span class="status status-warning">仅有草稿 / 作废 / 无效证据</span>');
  } else {
    parts.push('<span class="status status-neutral">无销项发票证据</span>');
  }
  if (s.hasNonActiveEvidence) parts.push('⚠ 含非有效证据');

  return `<span title="${escapeHtml(title)}">${parts.join(' · ')}</span>`;
}

/* 行操作：查看销售订单销项发票证据详情（GET /api/sales-orders/{id}/invoice-evidence，只读、不落库） */
async function showSalesOrderInvoiceEvidence(id) {
  try {
    const data = await api(`/api/sales-orders/${id}/invoice-evidence`);
    const s = data.summary || {};

    const lineRows = (data.lines || []).map(l => `<tr>
      <td>${escapeHtml(l.invoiceIdentityText || '')}${l.invoiceAvailable ? '' : ' <span class="status status-warning">发票证据不可用</span>'}</td>
      <td>${escapeHtml(l.invoiceDate ? fmtDate(l.invoiceDate) : '—')}</td>
      <td>${escapeHtml(l.invoiceStatusText || '')}</td>
      <td class="text-right">${fmtMoney(l.netAmount)} + ${fmtMoney(l.taxAmount)} = ${fmtMoney(l.grossAmount)} ${escapeHtml(l.currency || '')}</td>
      <td class="text-right">${fmtMoney(l.allocatedAmount)}</td>
      <td><span class="status status-${soieBucketLevel(l.bucket)}">${escapeHtml(l.bucketText || SOIE_BUCKET_LABELS[l.bucket] || '')}</span></td>
      <td>${escapeHtml(l.customerName || '')}（快照 Id=${l.customerId}）</td>
      <td>${escapeHtml(l.orderNo || '')}（${escapeHtml(l.orderCurrency || '')} · ${escapeHtml(l.orderStatusText || '')}）</td>
      <td class="text-right">${soieMoney(l.invoiceUnreferencedAmount)}</td>
      <td>${escapeHtml(l.allocatedAt ? fmtDate(l.allocatedAt) : '—')}${l.voidedAt ? ' → ' + escapeHtml(fmtDate(l.voidedAt)) : ''}${l.voidReason ? '（' + escapeHtml(l.voidReason) + '）' : ''}</td>
      <td>${escapeHtml(l.reason || '')}</td>
    </tr>`).join('');

    const modal = document.getElementById('modal');
    modal.innerHTML = `<div class="modal modal-lg" style="width:1440px;max-width:96vw;max-height:92vh;overflow:auto">
      <h3>🧾 销售订单销项发票证据：${escapeHtml(s.orderNo || '')}</h3>
      <div class="pd-hint">
        ⚠️ 只读的<b>销项发票证据</b>（既有客户销项发票证据把含税总额分摊到本订单的分摊行）：<b>不是</b>发票开具系统、
        <b>不是</b>税务申报或销项税金计算、<b>不是</b>应收余额、<b>不是</b>货款核销、<b>不是</b>客户对账单、<b>不是</b>结算结果或账龄 ——
        不判断是否已开票 / 已收款 / 已结清 / 逾期，也不得据以催收；它与订单金额、「已关联收款金额」、「收款引用登记证据」是相互独立的证据类别，绝不相加。
      </div>
      <div class="form-grid" style="grid-template-columns:repeat(4,1fr)">
        <div><b>证据结论</b>：<span class="status status-${soieStatusLevel(s.invoiceEvidenceStatus)}">${escapeHtml(s.invoiceEvidenceLabel || '')}</span></div>
        <div><b>有效已分摊（销项发票证据）</b>：${soieMoney(s.recordedInvoicedAmount)} ${escapeHtml(s.orderCurrency || '')}</div>
        <div><b>发票张数（有效行去重）</b>：${soieMoney(s.recordedInvoiceCount)}</div>
        <div><b>分摊行条数（含草稿 / 历史）</b>：${soieMoney(s.invoiceAllocationCount)}</div>
        <div><b>参与证据的发票含税总额快照</b>：${soieMoney(s.recordedInvoiceGrossAmount)} ${escapeHtml(s.orderCurrency || '')}</div>
        <div><b>其中未指向本订单</b>：${soieMoney(s.unreferencedInvoiceAmount)} ${escapeHtml(s.orderCurrency || '')}</div>
        <div><b>订单金额中未被发票证据分摊</b>：${soieMoney(s.invoiceUnreferencedOrderAmount)} ${escapeHtml(s.orderCurrency || '')}</div>
        <div><b>订单金额</b>：${soieMoney(s.orderedAmount)} ${escapeHtml(s.orderCurrency || '')}</div>
        <div><b>草稿发票证据（未登记）</b>：${soieMoney(s.draftInvoiceAllocationCount)} 条 / ${soieMoney(s.draftInvoiceAllocatedAmount)}</div>
        <div><b>已作废历史证据</b>：${soieMoney(s.voidedInvoiceAllocationCount)} 条 / ${soieMoney(s.voidedInvoiceAllocatedAmount)}</div>
        <div><b>无效历史证据</b>：${soieMoney(s.invalidInvoiceAllocationCount)} 条 / ${soieMoney(s.invalidInvoiceAllocatedAmount)}</div>
        <div><b>无法确认的证据</b>：${soieMoney(s.unavailableInvoiceAllocationCount)} 条 / ${soieMoney(s.unavailableInvoiceAllocatedAmount)}</div>
      </div>

      <div class="pd-hint">证据说明：${escapeHtml(s.invoiceEvidenceNote || '')}</div>

      <h4>逐条销项发票证据（${data.lineCount || 0} 条；只按 ERP-055 持久化分摊行派生，含草稿与已作废历史）</h4>
      <div class="table-wrap" style="max-height:40vh;overflow:auto">
        <table><thead><tr>
          <th>发票身份（类型 / 代码 / 号码）</th><th>开票日期</th><th>发票状态</th>
          <th class="text-right">净额 + 税额 = 含税总额</th><th class="text-right">本单分摊金额</th>
          <th>证据分桶</th><th>客户快照</th><th>订单快照</th><th class="text-right">该发票未指向本单</th>
          <th>登记 / 作废</th><th>说明（为什么计入 / 不计入）</th>
        </tr></thead>
        <tbody>${lineRows || '<tr><td colspan="11" class="empty">本销售订单没有任何销项发票分摊行（销项发票证据缺口，不代表未开票 / 已开票 / 欠税 / 已收款）</td></tr>'}</tbody></table>
      </div>

      <div class="pd-hint">派生口径：${escapeHtml(data.rule || '')}</div>
      <div class="pd-hint">范围说明：${escapeHtml(data.scopeNote || '')}</div>
      <div class="pd-hint">登记口径：${escapeHtml(data.linkageRule || '')}</div>
      <div class="pd-hint">金额等式口径：${escapeHtml(data.amountEquationRule || '')}</div>
      <div class="pd-hint">登记册边界：${escapeHtml(data.invoiceRegisterBoundary || '')}</div>
      <div class="pd-hint">边界：${escapeHtml(data.boundary || '')}</div>
      <div class="modal-footer"><button class="btn btn-neutral" onclick="closeModal()">关闭</button></div>
    </div>`;
    modal.style.display = 'flex';
    return data;
  } catch (err) { toast(err.message, 'error'); }
}

