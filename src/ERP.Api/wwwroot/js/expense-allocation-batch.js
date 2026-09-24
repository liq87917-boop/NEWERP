/* ==================================================================================
   ========== 多客户装柜费用分摊批次（ERP-042）— 批次与来源留痕 ==========
   ==================================================================================
   定位：在既有「拼柜分摊」之上补**批次留痕**——把一条**柜级来源费用单**按某装柜清单
         （一柜）**启用中的多客户参与方**分摊一次，并记录批次号、来源费用、装柜清单 / 柜号、
         参与方客户、分摊方法、基数种类与来源、基数值、比例、分摊金额。
   边界（界面侧同样遵守）：
     1. 分摊结果仍然是既有费用单行（/api/finance/expenses），本模块只调用归属留痕接口；
     2. 预览为只读（不写库）；生成在服务端一次事务内完成，失败不留部分行；
     3. 更正走「作废批次」（保留历史与逐行留痕），不删除、不改写来源费用与已生成费用单；
     4. 不改写装柜清单 / 装柜明细 / 参与方身份 / 物流跟踪值 / 单证 / 库存 / 订单，
        不记账、不生成凭证 / 收款 / 付款 / 结算单；
     5. 基数必须显式：按体积 / 重量 / 箱数 / 金额由用户逐行填写（缺失即拒绝），
        整柜法取装柜清单**持久化**总箱数 / 总毛重 / 总体积（缺失或为 0 即拒绝），不按经验推断。
   文案与服务端 ContainerExpenseAllocationRules 保持一致。
   ================================================================================== */

/* 当前状态：装柜清单 / 来源费用 / 上下文 / 预览 / 方法 / 整柜基数 / 台账 */
let EAB = {
  loadingListId: '', sourceExpenseId: '', context: null, preview: null,
  method: '按体积', wholeBasisKind: '箱数', wholeTargetId: '',
  tab: 'allocate', batches: [], statusFilter: '', remark: ''
};
let EAB_LISTS = [];        // 装柜清单下拉缓存

/** 费用单列表「分摊留痕」列：批次留痕 / 历史分摊（无批次留痕） / 未分摊 */
function expenseLineageCellHtml(row) {
  const lineage = row.allocationLineage || '';
  const text = row.allocationLineageText || '';
  if (lineage === 'Batch') {
    return `<span class="status status-info">批次</span>`
      + `<div class="text-muted">${escapeHtml(text)}</div>`;
  }
  if (lineage === 'Legacy') {
    return `<span class="status status-neutral">历史分摊</span>`
      + `<div class="text-muted">${escapeHtml(text)}</div>`;
  }
  return `<span class="text-muted">${escapeHtml(text || '未分摊')}</span>`;
}

/** 打开分摊批次弹窗（费用单模块工具栏「🧾 分摊批次」；行操作传入该行费用单 Id） */
async function openExpenseAllocationBatches(sourceExpenseId) {
  EAB = {
    loadingListId: '', sourceExpenseId: sourceExpenseId ? String(sourceExpenseId) : '',
    context: null, preview: null, method: '按体积', wholeBasisKind: '箱数', wholeTargetId: '',
    tab: 'allocate', batches: [], statusFilter: '', remark: ''
  };
  EAB_LISTS = [];
  const modal = document.getElementById('modal');
  modal.innerHTML = '<div style="padding:40px;text-align:center;color:#64748b">正在加载装柜清单…</div>';
  modal.style.display = 'block';
  try {
    const res = await api('/api/container/loading-lists?page=1&pageSize=500');
    EAB_LISTS = (res && res.items) ? res.items : (Array.isArray(res) ? res : []);
  } catch (e) {
    toast('装柜清单加载失败：' + e.message, 'error');
  }
  eabRender();
}

/** 弹窗 HTML（两个页签：分摊生成 / 批次台账） */
function eabRender() {
  const modal = document.getElementById('modal');
  modal.innerHTML = `
    <div style="max-width:1180px;margin:3vh auto;background:#fff;border-radius:14px;padding:18px 20px;max-height:92vh;overflow:auto;box-shadow:0 20px 60px rgba(0,0,0,.25)">
      <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:10px">
        <h3 style="margin:0">🧾 装柜费用分摊批次
          <span style="font-size:13px;color:#64748b;font-weight:400;margin-left:10px">
            一柜多客户：按体积 / 重量 / 箱数 / 金额或整柜法分摊，并留下批次与来源留痕</span></h3>
        <button class="btn btn-neutral btn-sm" onclick="closeModal()">✕ 关闭</button>
      </div>

      <div style="display:flex;gap:8px;margin-bottom:10px">
        <button class="btn btn-sm ${EAB.tab === 'allocate' ? 'btn-primary' : 'btn-neutral'}" onclick="eabTab('allocate')">分摊预览 / 生成</button>
        <button class="btn btn-sm ${EAB.tab === 'ledger' ? 'btn-primary' : 'btn-neutral'}" onclick="eabTab('ledger')">批次台账 / 作废</button>
      </div>

      ${EAB.tab === 'allocate' ? eabAllocateHtml() : eabLedgerHtml()}
    </div>`;
  modal.style.display = 'block';
}

function eabTab(tab) {
  EAB.tab = tab;
  if (tab === 'ledger') { eabRender(); eabLoadBatches(); return; }
  eabRender();
}

/** 分摊页签：装柜清单 + 来源费用 + 方法与基数 + 预览结果 */
function eabAllocateHtml() {
  const ctx = EAB.context;
  const listOptions = EAB_LISTS.map(l =>
    `<option value="${l.id}" ${String(l.id) === String(EAB.loadingListId) ? 'selected' : ''}>`
    + `${escapeHtml(l.loadingListNo || '')} / 柜号 ${escapeHtml(l.containerNo || '—')}</option>`).join('');

  const methodOptions = (ctx && ctx.supportedMethods ? ctx.supportedMethods : ['按体积', '按重量', '按箱数', '按金额', '整柜'])
    .map(m => `<option value="${m}" ${m === EAB.method ? 'selected' : ''}>${m}</option>`).join('');

  const basisOptions = (ctx && ctx.wholeContainerBasisKinds ? ctx.wholeContainerBasisKinds : ['箱数', '毛重', '体积'])
    .map(k => `<option value="${k}" ${k === EAB.wholeBasisKind ? 'selected' : ''}>${k}</option>`).join('');

  return `
    <div style="display:grid;grid-template-columns:1fr 1fr;gap:10px;margin-bottom:8px">
      <div><label class="ea-lb">装柜清单（一柜）</label>
        <select id="eab-list" style="width:100%" onchange="eabSelectList(this.value)">
          <option value="">请选择装柜清单…</option>${listOptions}
        </select></div>
      <div><label class="ea-lb">分摊方法</label>
        <select id="eab-method" style="width:100%" onchange="eabSetMethod(this.value)">${methodOptions}</select></div>
      <div><label class="ea-lb">整柜法基数种类（仅整柜法使用，取装柜清单持久化值）</label>
        <select id="eab-basis" style="width:100%" onchange="eabSetBasisKind(this.value)">${basisOptions}</select></div>
      <div><label class="ea-lb">备注（写入批次与生成的费用单）</label>
        <input id="eab-remark" style="width:100%" value="${escapeHtml(EAB.remark)}"
          onchange="EAB.remark=this.value" placeholder="如：9 月拼柜报关费分摊"></div>
    </div>

    ${ctx ? eabContextHtml() : '<div style="padding:14px;color:#64748b;border:1px dashed #cbd5e1;border-radius:10px">请先选择装柜清单：系统会按该柜的参与方、持久化装柜总量与费用单资格生成上下文（只读）。</div>'}

    <div style="display:flex;justify-content:flex-end;gap:10px;margin-top:12px">
      <button class="btn btn-neutral" onclick="eabPreview()">🔍 预览分摊（只读）</button>
      <button class="btn btn-primary" onclick="eabGenerate()">✅ 生成分摊批次</button>
    </div>
    <div id="eab-result" style="margin-top:12px"></div>`;
}

/** 上下文面板：装柜总量 / 参与方基准值 / 来源费用资格 / 现存批次（全部只读） */
function eabContextHtml() {
  const ctx = EAB.context;
  const participants = ctx.participants || [];
  const sources = ctx.sourceExpenses || [];
  const whole = EAB.method === '整柜';

  const participantRows = participants.map(p => {
    const name = `${p.customerCode || ''} ${p.customerName || ''}`.trim()
      + (p.customerAvailable === false ? '（已停用/不可用）' : '');
    if (!p.selectable) {
      return `<tr style="border-top:1px solid #e2e8f0;color:#94a3b8">
        <td style="padding:6px">${escapeHtml(name)}</td>
        <td style="padding:6px">${escapeHtml(p.statusText || '停用')}</td>
        <td colspan="2" style="padding:6px">${escapeHtml(p.availabilityText || '不参与分摊')}</td></tr>`;
    }
    return `<tr style="border-top:1px solid #e2e8f0">
      <td style="padding:6px">${escapeHtml(name)}${p.isPrimary ? ' <span class="text-muted">（主参与方）</span>' : ''}</td>
      <td style="padding:6px">${escapeHtml(p.statusText || '启用')}</td>
      <td style="padding:6px;width:150px"><input id="eab-b-${p.id}" type="number" step="0.0001" style="width:100%"
        placeholder="必填" title="基准值必须显式填写，缺失即拒绝"></td>
      <td style="padding:6px;width:150px">${escapeHtml(p.availabilityText || '')}</td>
    </tr>`;
  }).join('');

  const sourceRows = sources.length === 0
    ? '<tr><td colspan="5" style="padding:8px;color:#64748b">该柜暂无可选费用单（费用单「归属类型」需为整柜 / 拼柜 / 散货，「归属单号」需等于本柜柜号）</td></tr>'
    : sources.map(s => `<tr style="border-top:1px solid #e2e8f0">
        <td style="padding:6px"><input type="radio" name="eab-src" value="${s.id}"
          ${String(s.id) === String(EAB.sourceExpenseId) ? 'checked' : ''} ${s.eligible ? '' : 'disabled'}
          onchange="EAB.sourceExpenseId=this.value;EAB.preview=null;eabRenderResult()"></td>
        <td style="padding:6px">${escapeHtml(s.expenseNo || '')} <span class="text-muted">${escapeHtml(s.expenseType || '')}</span></td>
        <td style="padding:6px;text-align:right">${fmtMoney(s.amount)} ${escapeHtml(s.currency || '')}</td>
        <td style="padding:6px">${escapeHtml(s.lineageText || '')}</td>
        <td style="padding:6px">${s.eligible ? '✅ 可作为分摊来源' : '⛔ ' + escapeHtml(s.eligibilityText || '')}</td>
      </tr>`).join('');

  const activeBatches = (ctx.activeBatches || []).map(b =>
    `<span class="status status-info">${escapeHtml(b.batchNo)}</span> ${escapeHtml(b.allocationMethod)}`
    + ` / ${b.lineCount} 行 / ${fmtMoney(b.allocatedTotal)} ${escapeHtml(b.currency)}`).join('　');
  const voidedBatches = (ctx.voidedBatches || []).map(b =>
    `<span class="status status-neutral">${escapeHtml(b.batchNo)}（已作废）</span>`
    + ` ${escapeHtml(b.voidReason || '')}`).join('　');

  return `
    <div style="border:1px solid #e2e8f0;border-radius:10px;padding:10px 12px;margin-bottom:10px;font-size:13px">
      <div><strong>柜号 ${escapeHtml(ctx.containerNo || '—')}</strong>（${escapeHtml(ctx.loadingListNo || '')}）
        · 持久化装柜总量：总箱数 ${ctx.persistedTotalCartons} / 总毛重 ${ctx.persistedTotalWeight} kg
        / 总体积 ${ctx.persistedTotalVolume} m³
        ${ctx.hasPersistedEvidence ? '' : '<span style="color:#b45309">（持久化证据为 0：整柜法会被拒绝）</span>'}</div>
      <div class="text-muted" style="margin-top:4px">${escapeHtml(ctx.participantScopeText || '')}</div>
      <div class="text-muted" style="margin-top:4px">余差规则：${escapeHtml(ctx.remainderRuleText || '')}</div>
      <div class="text-muted" style="margin-top:4px">边界：${escapeHtml(ctx.boundaryText || '')}</div>
      ${activeBatches ? `<div style="margin-top:6px">现存有效批次：${activeBatches}</div>` : ''}
      ${voidedBatches ? `<div style="margin-top:4px" class="text-muted">已作废批次：${voidedBatches}</div>` : ''}
    </div>

    <div style="display:flex;align-items:center;gap:10px;margin:10px 0 6px">
      <strong style="font-size:14px">参与方与基准值</strong>
      <span class="text-muted" style="font-size:12px">${whole
        ? '整柜法：全额（100%）归「目标参与方」，基数取装柜清单持久化值（不使用逐行基准值）'
        : '逐行显式填写基准值：缺失即拒绝，服务端不会按经验值 / 历史比例 / 列表顺序推断'}</span>
      ${whole ? `<select id="eab-target" style="margin-left:auto" onchange="EAB.wholeTargetId=this.value">
          <option value="">（未指定：仅当该柜恰好一条启用参与方时可用）</option>
          ${participants.filter(p => p.selectable).map(p => `<option value="${p.id}"
            ${String(p.id) === String(EAB.wholeTargetId) ? 'selected' : ''}>
            ${escapeHtml(((p.customerCode || '') + ' ' + (p.customerName || '')).trim())}</option>`).join('')}
        </select>` : ''}
    </div>
    <table style="width:100%;border-collapse:collapse;font-size:13px">
      <thead><tr style="background:#f8fafc">
        <th style="padding:6px;text-align:left">参与方客户</th>
        <th style="padding:6px;text-align:left">状态</th>
        <th style="padding:6px;text-align:left">基准值</th>
        <th style="padding:6px;text-align:left">可用性 / 说明</th>
      </tr></thead>
      <tbody>${participantRows || '<tr><td colspan="4" style="padding:8px;color:#64748b">该清单没有参与方：请先在「多客户参与方」维护参与客户</td></tr>'}</tbody>
    </table>

    <div style="margin:12px 0 6px"><strong style="font-size:14px">来源费用单（柜级总额，单选）</strong></div>
    <table style="width:100%;border-collapse:collapse;font-size:13px">
      <thead><tr style="background:#f8fafc">
        <th style="padding:6px;text-align:left;width:40px">选择</th>
        <th style="padding:6px;text-align:left">费用单</th>
        <th style="padding:6px;text-align:right">金额</th>
        <th style="padding:6px;text-align:left">分摊留痕</th>
        <th style="padding:6px;text-align:left">资格</th>
      </tr></thead>
      <tbody>${sourceRows}</tbody>
    </table>`;
}

/** 选择装柜清单 → 重置并加载上下文（只读） */
async function eabSelectList(id) {
  EAB.loadingListId = id || '';
  EAB.context = null;
  EAB.preview = null;
  EAB.sourceExpenseId = '';
  if (!EAB.loadingListId) { eabRender(); return; }
  try {
    EAB.context = await api('/api/finance/expenses/allocation-context?loadingListId=' + EAB.loadingListId);
    const first = (EAB.context.sourceExpenses || []).find(s => s.eligible);
    if (first) EAB.sourceExpenseId = String(first.id);
    eabRender();
  } catch (e) {
    toast('分摊上下文加载失败：' + e.message, 'error');
    eabRender();
  }
}

/** 切换分摊方法：清空旧预览（预览与生成都按当前方法重新计算） */
function eabSetMethod(method) {
  EAB.method = method;
  EAB.preview = null;
  eabRender();
}

/** 切换整柜法基数种类（取装柜清单持久化总箱数 / 总毛重 / 总体积） */
function eabSetBasisKind(kind) {
  EAB.wholeBasisKind = kind;
  EAB.preview = null;
  eabRenderResult();
}

/** 收集请求体（与服务端 ContainerExpenseAllocationRequest 对应） */
function eabCollect() {
  const ctx = EAB.context;
  const lines = ((ctx && ctx.participants) || []).filter(p => p.selectable).map(p => {
    const el = document.getElementById('eab-b-' + p.id);
    const raw = el ? el.value : '';
    return { participantId: p.id, basisValue: raw === '' ? null : Number(raw) };
  });
  const target = document.getElementById('eab-target');
  return {
    sourceExpenseId: Number(EAB.sourceExpenseId || 0),
    loadingListId: Number(EAB.loadingListId || 0),
    allocationMethod: EAB.method,
    wholeContainerBasisKind: EAB.wholeBasisKind,
    wholeContainerParticipantId: target && target.value ? Number(target.value) : null,
    remark: EAB.remark || '',
    lines: EAB.method === '整柜' ? [] : lines
  };
}

/** 预览结果表（只读结果；生成后展示生成费用单号） */
function eabRenderResult(generated) {
  const el = document.getElementById('eab-result');
  if (!el) return;
  const preview = EAB.preview;
  if (!preview) { el.innerHTML = ''; return; }

  const rows = (preview.lines || []).map(l => `<tr style="border-top:1px solid #e2e8f0">
    <td style="padding:6px">${escapeHtml(l.customerDisplay || '')}
      ${l.customerAvailable === false ? '<span class="text-muted">（客户已停用/不可用）</span>' : ''}</td>
    <td style="padding:6px">${escapeHtml(l.allocationMethod || '')}</td>
    <td style="padding:6px">${escapeHtml(l.basisKind || '')}
      <div class="text-muted">${escapeHtml(l.basisEvidence || '')}</div></td>
    <td style="padding:6px;text-align:right">${l.basisValue}</td>
    <td style="padding:6px;text-align:right">${Number(l.ratio || 0).toFixed(4)}</td>
    <td style="padding:6px;text-align:right">${fmtMoney(l.allocatedAmount)} ${escapeHtml(l.currency || '')}</td>
    <td style="padding:6px">${l.remainderCarrier ? '<span class="status status-info">承接余差</span>' : ''}</td>
    <td style="padding:6px">${escapeHtml(l.expenseNo || '—')}</td>
  </tr>`).join('');

  el.innerHTML = `
    <div style="border:1px solid #e2e8f0;border-radius:10px;overflow:hidden">
      <div style="background:#f1f5f9;padding:8px 12px;font-size:13px;font-weight:600">
        ${generated ? '✅ 已生成分摊批次' : '🔍 分摊预览（只读，未写库）'}
        · 来源费用 ${escapeHtml(preview.sourceExpenseNo || '')}（${fmtMoney(preview.sourceAmount)} ${escapeHtml(preview.currency)}）
        · 方法 ${escapeHtml(preview.allocationMethod || '')} · 基数种类 ${escapeHtml(preview.basisKind || '')}
        · 精度 ${preview.amountPrecision} 位 · 合计 ${fmtMoney(preview.allocatedTotal)} ${escapeHtml(preview.currency)}
        · 比例合计 ${Number(preview.ratioTotal || 0).toFixed(4)}% · 余差 ${preview.remainder}
      </div>
      <div style="padding:8px 12px;font-size:12px;color:#64748b">
        ${escapeHtml(preview.scopeText || '')}<br>${escapeHtml(preview.remainderRuleText || '')}
      </div>
      <table style="width:100%;border-collapse:collapse;font-size:13px">
        <thead><tr style="background:#f8fafc">
          <th style="padding:8px;text-align:left">参与方客户</th>
          <th style="padding:8px;text-align:left">方法</th>
          <th style="padding:8px;text-align:left">基数种类 / 来源</th>
          <th style="padding:8px;text-align:right">基数值</th>
          <th style="padding:8px;text-align:right">比例 %</th>
          <th style="padding:8px;text-align:right">分摊金额</th>
          <th style="padding:8px;text-align:left">余差</th>
          <th style="padding:8px;text-align:left">生成费用单号</th>
        </tr></thead>
        <tbody>${rows}</tbody>
      </table>
    </div>`;
}

/** 预览分摊（只读，不写库） */
async function eabPreview() {
  const req = eabCollect();
  if (!req.loadingListId) { toast('请选择装柜清单', 'warning'); return; }
  if (!req.sourceExpenseId) { toast('请选择来源费用单', 'warning'); return; }
  try {
    EAB.preview = await api('/api/finance/expenses/allocation-preview', 'POST', req);
    eabRenderResult(false);
    toast('预览完成（未写库）');
  } catch (e) { toast('预览失败：' + e.message, 'error'); }
}

/** 生成分摊批次（服务端一次事务写入批次 + 逐行留痕 + 既有费用单行） */
async function eabGenerate() {
  const req = eabCollect();
  if (!req.loadingListId) { toast('请选择装柜清单', 'warning'); return; }
  if (!req.sourceExpenseId) { toast('请选择来源费用单', 'warning'); return; }
  if (!EAB.preview) {
    await eabPreview();
    if (!EAB.preview) return;
  }
  if (!confirm('确认按预览结果生成分摊批次？将新增费用单行并记录批次与来源留痕；'
    + '不改写来源费用单、装柜清单与明细、参与方、单证、库存与订单，也不产生任何收付款 / 记账动作。')) return;
  try {
    const res = await api('/api/finance/expenses/allocation-generate', 'POST', req);
    toast(`已生成分摊批次 ${res.batchNo}（${res.lineCount} 条费用单）`, 'success');
    EAB.preview = Object.assign({}, EAB.preview, { lines: res.lines, allocatedTotal: res.allocatedTotal });
    EAB.context = await api('/api/finance/expenses/allocation-context?loadingListId=' + EAB.loadingListId);
    eabRender();
    eabRenderResult(true);
    eabLoadBatches();
    if (typeof loadList === 'function') loadList();
  } catch (e) { toast('生成失败：' + e.message, 'error'); }
}

/** 批次台账页签：状态过滤 + 批次卡片（含逐行留痕与作废入口） */
function eabLedgerHtml() {
  const batches = EAB.batches || [];
  const cards = batches.length === 0
    ? '<div style="padding:14px;color:#64748b;border:1px dashed #cbd5e1;border-radius:10px">暂无分摊批次（含已作废历史）</div>'
    : batches.map(b => {
      const lines = (b.lines || []).map(l => `<tr style="border-top:1px solid #e2e8f0">
          <td style="padding:5px">${escapeHtml(l.customerDisplay || '')}</td>
          <td style="padding:5px">${escapeHtml(l.allocationMethod || '')} / ${escapeHtml(l.basisKind || '')}
            <div class="text-muted">${escapeHtml(l.basisEvidence || '')}</div></td>
          <td style="padding:5px;text-align:right">${l.basisValue}</td>
          <td style="padding:5px;text-align:right">${Number(l.ratio || 0).toFixed(4)}</td>
          <td style="padding:5px;text-align:right">${fmtMoney(l.allocatedAmount)} ${escapeHtml(l.currency || '')}</td>
          <td style="padding:5px">${escapeHtml(l.expenseNo || '—')}</td>
        </tr>`).join('');
      return `
      <div style="border:1px solid #e2e8f0;border-radius:10px;margin-bottom:10px;overflow:hidden">
        <div style="background:#f8fafc;padding:8px 12px;display:flex;align-items:center;gap:10px;font-size:13px">
          <span class="status ${b.isActive ? 'status-info' : 'status-neutral'}">${escapeHtml(b.statusText || '')}</span>
          <strong>${escapeHtml(b.batchNo || '')}</strong>
          <span class="text-muted">来源费用 ${escapeHtml(b.sourceExpenseNo || '')} · 柜号 ${escapeHtml(b.containerNo || '')}
            · ${escapeHtml(b.allocationMethod || '')}（${escapeHtml(b.basisKind || '')}）
            · ${b.lineCount} 行 · ${fmtMoney(b.allocatedTotal)} ${escapeHtml(b.currency)}</span>
          ${b.isVoided ? `<span class="text-muted">作废：${escapeHtml(b.voidReason || '')}</span>` : ''}
          ${b.isActive ? `<button class="btn btn-neutral btn-sm" style="margin-left:auto"
            onclick="eabVoidBatch(${b.id}, '${escapeHtml(b.batchNo || '')}')">作废（保留历史）</button>` : ''}
        </div>
        <div style="padding:6px 12px;font-size:12px;color:#64748b">${escapeHtml(b.boundaryText || '')}</div>
        <table style="width:100%;border-collapse:collapse;font-size:13px">
          <thead><tr style="background:#fff">
            <th style="padding:6px;text-align:left">参与方客户</th>
            <th style="padding:6px;text-align:left">方法 / 基数</th>
            <th style="padding:6px;text-align:right">基数值</th>
            <th style="padding:6px;text-align:right">比例 %</th>
            <th style="padding:6px;text-align:right">分摊金额</th>
            <th style="padding:6px;text-align:left">费用单号</th>
          </tr></thead>
          <tbody>${lines}</tbody>
        </table>
      </div>`;
    }).join('');

  const option = (value, label) =>
    `<option value="${value}" ${String(EAB.statusFilter) === String(value) ? 'selected' : ''}>${label}</option>`;

  return `
    <div style="display:flex;align-items:center;gap:10px;margin-bottom:10px">
      <label class="ea-lb">批次状态</label>
      <select onchange="EAB.statusFilter=this.value;eabLoadBatches()">
        ${option('', '全部（含已作废历史）')}${option('1', '仅有效批次')}${option('0', '仅已作废批次')}
      </select>
      <button class="btn btn-neutral btn-sm" onclick="eabLoadBatches()">↻ 刷新</button>
      <span class="text-muted" style="font-size:12px">作废只改批次状态并记录原因：保留批次与逐行留痕，不改写来源费用与已生成费用单，也不产生任何收付款 / 记账动作</span>
    </div>
    ${cards}`;
}

/** 加载批次台账（按状态过滤；只读） */
async function eabLoadBatches() {
  try {
    const filter = EAB.statusFilter === '' ? '' : '&status=' + EAB.statusFilter;
    const res = await api(`/api/finance/expenses/allocation-batches?page=1&pageSize=50${filter}`);
    EAB.batches = (res && res.items) ? res.items : [];
    if (EAB.tab === 'ledger') eabRender();
  } catch (e) {
    toast('批次台账加载失败：' + e.message, 'error');
  }
}

/** 作废分摊批次（必须填写作废原因；保留历史，不删除、不改写费用单与来源费用） */
async function eabVoidBatch(batchId, batchNo) {
  const reason = prompt(`作废分摊批次「${batchNo}」？请填写作废原因`
    + '（作废保留批次与逐行留痕，不改写来源费用与已生成费用单，也不产生任何收付款记录）');
  if (reason === null) return;
  if (!reason.trim()) { toast('必须填写作废原因', 'warning'); return; }
  try {
    await api(`/api/finance/expenses/allocation-batches/${batchId}/void`, 'POST', { reason: reason.trim() });
    toast('批次已作废（历史与逐行留痕保留）');
    await eabLoadBatches();
    if (EAB.loadingListId) {
      EAB.context = await api('/api/finance/expenses/allocation-context?loadingListId=' + EAB.loadingListId);
      if (EAB.tab === 'allocate') eabRender();
    }
  } catch (e) { toast('作废失败：' + e.message, 'error'); }
}
