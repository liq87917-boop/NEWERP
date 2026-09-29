/* ============ 供应商报价 → 采购订单转化漏斗（ERP-103：只读派生；按比价批次分组六类证据，不写库、不转单、不改审批） ============ */

let funnelPage = 1;
let funnelDateFrom = '';
let funnelDateTo = '';
let funnelSupplierId = '';

/* 漏斗阶段中文（与后端 PurchaseQuoteConversionFunnel 的 stage 一一对应） */
function funnelStageLabel(stage) {
  const labels = { quoted: '已报价', selected: '已选中', approved: '已批准', converted: '已转采购订单', rejected: '已拒绝', unresolved: '未解决' };
  return labels[stage] || stage || '';
}

/* 漏斗阶段配色（复用状态胶囊配色） */
function funnelStageClass(stage) {
  const map = { quoted: 'status-neutral', selected: 'status-info', approved: 'status-success', converted: 'status-success', rejected: 'status-danger', unresolved: 'status-danger' };
  return map[stage] || 'status-neutral';
}

/* 打开只读转化漏斗：按批次号分组展示 已报价 / 已选中 / 已批准 / 已转采购订单 / 已拒绝 / 未解决 六类证据 */
async function showPurchaseQuoteConversionFunnel() {
  funnelPage = 1;
  funnelDateFrom = '';
  funnelDateTo = '';
  funnelSupplierId = '';

  const modal = document.getElementById('modal');
  modal.innerHTML = `<div class="modal modal-lg" style="width:1080px;max-width:96vw;max-height:92vh;overflow:auto">
    <h3>🔎 供应商报价 → 采购订单转化漏斗</h3>
    <div class="form-grid" style="grid-template-columns:repeat(4,1fr)">
      <div><label>开始日期</label><input id="funnelDateFrom" type="date"></div>
      <div><label>结束日期</label><input id="funnelDateTo" type="date"></div>
      <div><label>供应商 Id（可选）</label><input id="funnelSupplierId" type="number" min="1"></div>
      <div style="align-self:end"><button class="btn btn-primary" onclick="funnelSearch()">查询</button></div>
    </div>
    <div id="funnelResult"></div>
    <div class="modal-footer"><button class="btn btn-neutral" onclick="closeModal()">关闭</button></div>
  </div>`;
  modal.style.display = 'flex';
  await funnelLoad();
}

/* 读取筛选条件后重新查询（第 1 页） */
function funnelSearch() {
  funnelPage = 1;
  funnelDateFrom = (document.getElementById('funnelDateFrom').value || '').trim();
  funnelDateTo = (document.getElementById('funnelDateTo').value || '').trim();
  funnelSupplierId = (document.getElementById('funnelSupplierId').value || '').trim();
  return funnelLoad();
}

async function funnelLoad() {
  try {
    const params = new URLSearchParams();
    if (funnelDateFrom) params.set('dateFrom', funnelDateFrom);
    if (funnelDateTo) params.set('dateTo', funnelDateTo);
    if (funnelSupplierId) params.set('supplierId', funnelSupplierId);
    params.set('page', String(funnelPage));
    params.set('pageSize', '20');
    const qs = params.toString() ? '?' + params.toString() : '';
    const v = await api(`/api/purchase/quotes/conversion-funnel${qs}`);
    funnelRender(v);
    return v;
  } catch (err) { toast(err.message, 'error'); }
}

function funnelRender(v) {
  const el = document.getElementById('funnelResult');

  const summary = `<div class="form-grid" style="grid-template-columns:repeat(4,1fr)">
    <div><b>批次总数</b>：${v.totalBatchCount}</div>
    <div><b>报价行总数</b>：${v.totalLineCount}</div>
    <div><b>本页批次</b>：${(v.batches || []).length}</div>
    <div><b>第 ${v.page} 页 / 每页 ${v.pageSize}</b></div>
  </div>`;

  const emptyHtml = v.emptyText ? `<div class="empty">${escapeHtml(v.emptyText)}</div>` : '';
  const truncatedHtml = v.truncated
    ? `<div class="pd-hint" style="color:#b45309">⚠ 结果已按分页截断：符合条件共 ${v.totalBatchCount} 个批次，本页显示 ${v.pageSize} 个。</div>` : '';

  const batchesHtml = (v.batches || []).map(b => {
    const linesHtml = (b.lines || []).map(r => `<tr>
      <td>${fmtDate(r.quoteDate)}</td>
      <td>${escapeHtml(r.supplierName || '')}</td>
      <td>${escapeHtml(r.productName || '')}</td>
      <td class="text-right">${fmtMoney(r.quotePrice)}</td>
      <td>${r.isSelected ? '✅' : ''}</td>
      <td><span class="status ${funnelStageClass(r.stage)}">${funnelStageLabel(r.stage)}</span></td>
      <td>${escapeHtml(r.orderNo || '—')}</td>
      <td>${escapeHtml(r.stageReason || '')}</td>
    </tr>`).join('');

    return `<details open style="margin:8px 0;border:1px solid #e2e8f0;border-radius:8px;padding:8px">
      <summary style="cursor:pointer;font-weight:600">${escapeHtml(b.quoteNo)} · ${fmtDate(b.quoteDate)} ·
        行 ${b.lineCount} · 已报价 ${b.quotedCount} → 已选中 ${b.selectedCount} → 已批准 ${b.approvedCount} → 已转 ${b.convertedCount} ·
        已拒绝 ${b.rejectedCount} · 未解决 ${b.unresolvedCount}</summary>
      <div class="table-wrap" style="max-height:40vh;overflow:auto;margin-top:6px">
        <table><thead><tr>
          <th>报价日期</th><th>供应商</th><th>商品</th><th class="text-right">报价单价</th><th>选中</th><th>阶段</th><th>采购单号</th><th>未解决原因</th>
        </tr></thead>
        <tbody>${linesHtml || '<tr><td colspan="8" class="empty">无报价行</td></tr>'}</tbody></table>
      </div>
    </details>`;
  }).join('');

  const pager = `<div style="margin:8px 0;display:flex;gap:8px">
    <button class="btn btn-neutral" ${funnelPage <= 1 ? 'disabled' : ''} onclick="funnelPrev()">上一页</button>
    <button class="btn btn-neutral" ${!v.truncated ? 'disabled' : ''} onclick="funnelNext()">下一页</button>
  </div>`;

  el.innerHTML = summary + emptyHtml + truncatedHtml + pager
    + (batchesHtml || (!v.emptyText ? '<div class="empty">暂无转化漏斗数据</div>' : ''))
    + `<div class="pd-hint">${escapeHtml(v.ruleText || '')}</div>`;
}

function funnelPrev() { if (funnelPage > 1) { funnelPage--; funnelLoad(); } }
function funnelNext() { funnelPage++; funnelLoad(); }
