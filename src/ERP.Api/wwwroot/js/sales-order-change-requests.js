/* ==================================================================================
   ========== 销售订单变更申请登记册（ERP-047）— 只登记拟议变更，不批准、不套用 ==========
   ==================================================================================
   定位：把「某张既有销售订单希望改成什么」登记为一条带来源快照的申请，供人工审阅与留痕。
   边界（界面侧同样遵守）：
     1. 申请只有「草稿 / 已提交 / 已取消」三种状态：界面不出现、也不调用任何「批准 / 套用 / 生效」动作；
     2. 来源销售订单的主表值与明细行在登记时冻结为快照；之后来源变化只提示，界面不自动覆盖拟议值；
     3. 拟议金额（明细金额 / 总额 / 定金金额）全部由服务端按销售订单唯一权威算法重算，
        界面只展示服务端返回值，不自行累计、不改写任何来源单据；
     4. 更正走「取消（必须填原因）」，界面不提供删除；已提交申请不可编辑；
     5. 界面文案与服务端 SalesOrderChangeRequestRules / SalesOrderChangeRequestService 保持一致。
   数据全部走接口：
     GET  /api/sales-order-change-requests            台账（分页）
     GET  /api/sales-order-change-requests/metadata   模块口径与白名单
     GET  /api/sales-order-change-requests/source-options  来源销售订单候选
     GET  /api/sales-order-change-requests/{id}       详情（来源 vs 拟议对照）
     POST /api/sales-order-change-requests            登记草稿
     PUT  /api/sales-order-change-requests/{id}       编辑草稿
     POST /api/sales-order-change-requests/{id}/submit 提交（冻结拟议）
     POST /api/sales-order-change-requests/{id}/cancel 取消（必须填原因）
   ================================================================================== */

let SCR = {
  sourceOrderId: '', sourceOrderNo: '',
  filters: { status: '', keyword: '' },
  list: [], total: 0, page: 1, pageSize: 50,
  metadata: null,
  sources: [], sourceKeyword: '',
  createOpen: false, createSourceId: '', createReason: '',
  view: null,      /* 当前详情（服务端返回） */
  form: null,      /* 编辑中的草稿表单状态 */
  cancelId: null, cancelReason: '',
  busy: false
};

/* HTML 转义：所有来自接口的文本（单号 / 原因 / 对照文案）都必须转义后再进 innerHTML */
function scrEsc(value) {
  return String(value == null ? '' : value)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}

/* 状态短文案（与服务端状态码一一对应；「已提交」只是登记冻结，不是批准） */
const SCR_STATUS_LABELS = { 0: '草稿', 1: '已提交（未批准、未套用）', 2: '已取消' };

function scrStatusLabel(row) {
  return SCR_STATUS_LABELS[row.status] || row.statusText || ('未知状态(' + row.status + ')');
}

/* 列表行操作入口：crud.js 传入销售订单行 Id（销售订单页「变更申请」按钮） */
async function openSalesOrderChangeRequestsForCurrentModule(salesOrderId) {
  await openSalesOrderChangeRequests(salesOrderId, '');
}

/* ==================== 打开登记册 ==================== */

async function openSalesOrderChangeRequests(salesOrderId, salesOrderNo) {
  SCR = {
    sourceOrderId: salesOrderId ? String(salesOrderId) : '',
    sourceOrderNo: salesOrderNo || '',
    filters: { status: '', keyword: salesOrderNo || '' },
    list: [], total: 0, page: 1, pageSize: 50,
    metadata: null,
    sources: [], sourceKeyword: '',
    createOpen: false, createSourceId: '', createReason: '',
    view: null, form: null, cancelId: null, cancelReason: '', busy: false
  };

  const modal = document.getElementById('modal');
  modal.innerHTML = '<div style="padding:40px;text-align:center;color:#64748b">正在加载…</div>';
  modal.style.display = 'block';

  try {
    SCR.metadata = await api('/api/sales-order-change-requests/metadata');
  } catch (e) {
    toast('变更申请模块元数据加载失败：' + e.message, 'error');
  }

  await scrLoadSources('');
  await scrLoadList();
  scrRender();
}

/* 来源销售订单候选（只返回存在且未删除的订单；已作废订单不可选择） */
async function scrLoadSources(keyword) {
  try {
    SCR.sources = await api('/api/sales-order-change-requests/source-options?keyword='
      + encodeURIComponent(keyword || '') + '&take=200');
    if (!Array.isArray(SCR.sources)) SCR.sources = [];
  } catch (e) {
    SCR.sources = [];
    toast('来源销售订单候选加载失败：' + e.message, 'error');
  }
}

async function scrLoadList() {
  const f = SCR.filters;
  const params = ['page=' + SCR.page, 'pageSize=' + SCR.pageSize];
  if (SCR.sourceOrderId) params.push('salesOrderId=' + encodeURIComponent(SCR.sourceOrderId));
  if (f.status !== '') params.push('status=' + encodeURIComponent(f.status));
  if (f.keyword) params.push('keyword=' + encodeURIComponent(f.keyword));

  try {
    const res = await api('/api/sales-order-change-requests?' + params.join('&'));
    SCR.list = (res && res.items) ? res.items : [];
    SCR.total = (res && res.total) || 0;
  } catch (e) {
    SCR.list = []; SCR.total = 0;
    toast('变更申请台账加载失败：' + e.message, 'error');
  }

  /* 从销售订单行操作进入时用首条记录回填来源单号（只读展示，不猜测归属） */
  if (!SCR.sourceOrderNo && SCR.list.length) SCR.sourceOrderNo = SCR.list[0].salesOrderNo || '';
}

/* ==================== 渲染 ==================== */

function scrRender() {
  const modal = document.getElementById('modal');
  const meta = SCR.metadata;
  const statusOptions = meta ? meta.statusOptions : [{ value: '0', label: '草稿' }];

  const rows = SCR.list.map(row => scrRowHtml(row)).join('');
  const fixedSource = SCR.sourceOrderId
    ? `<div style="background:#f1f5f9;border-radius:8px;padding:8px 10px;font-size:12px;color:#334155;margin-bottom:8px">
         当前来源销售订单：<strong>${scrEsc(SCR.sourceOrderNo || SCR.sourceOrderId)}</strong>
         —— 只显示该订单的变更申请；新建时来源已固定。
       </div>`
    : '';

  modal.innerHTML = `
    <div style="max-width:1320px;margin:2vh auto;background:#fff;border-radius:14px;padding:16px 18px;max-height:94vh;overflow:auto;box-shadow:0 20px 60px rgba(0,0,0,.25)">
      <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:6px">
        <h3 style="margin:0">📝 销售订单变更申请登记册
          <span style="font-size:13px;color:#64748b;font-weight:400;margin-left:10px">
            只登记拟议变更（来源快照 + 拟议值）；<strong>不审核、不套用、不改写来源销售订单</strong></span></h3>
        <div>
          <button class="btn btn-primary btn-sm" onclick="scrOpenCreate()">＋ 新建变更申请</button>
          <button class="btn btn-neutral btn-sm" onclick="closeModal()">✕ 关闭</button>
        </div>
      </div>
      <div style="background:#fff7ed;border:1px solid #fed7aa;border-radius:8px;padding:8px 10px;font-size:12px;color:#9a3412;margin-bottom:10px">
        ${scrEsc(meta ? meta.approvalBoundaryText : '')}<br>${scrEsc(meta ? meta.boundaryText : '')}
      </div>
      <div style="display:flex;flex-wrap:wrap;gap:8px;align-items:flex-end;margin-bottom:10px">
        <label style="font-size:12px;color:#475569">状态<br>
          <select id="scr-fstatus" onchange="scrFiltersChanged()">
            <option value="">全部状态</option>
            ${statusOptions.map(o => `<option value="${scrEsc(o.value)}" ${String(o.value) === String(SCR.filters.status) ? 'selected' : ''}>${scrEsc(o.label)}</option>`).join('')}
          </select></label>
        <label style="font-size:12px;color:#475569;flex:1;min-width:200px">关键字（申请单号 / 来源订单号 / 变更原因）<br>
          <input id="scr-fkeyword" value="${scrEsc(SCR.filters.keyword)}" onkeydown="if(event.key==='Enter')scrFiltersChanged()"></label>
        <button class="btn btn-neutral btn-sm" onclick="scrFiltersChanged()">查询</button>
        <button class="btn btn-neutral btn-sm" onclick="scrResetFilters()">重置</button>
      </div>
      ${fixedSource}
      <div style="font-size:12px;color:#64748b;margin-bottom:6px">共 ${SCR.total} 条（每页 ${SCR.pageSize} 条，单次最多 ${meta ? meta.maxPageSize : 200} 条；无逐行查询）</div>
      <div id="scr-table">${rows || '<div style="padding:20px;text-align:center;color:#94a3b8">暂无变更申请记录</div>'}</div>
      <div style="display:flex;justify-content:flex-end;gap:8px;margin-top:10px">
        <button class="btn btn-neutral btn-sm" onclick="scrPage(-1)">上一页</button>
        <button class="btn btn-neutral btn-sm" onclick="scrPage(1)">下一页</button>
      </div>
      ${SCR.createOpen ? scrCreateHtml() : ''}
      ${SCR.view ? scrDetailHtml(SCR.view) : ''}
      ${SCR.form ? scrFormHtml() : ''}
      ${SCR.cancelId ? scrCancelHtml() : ''}
    </div>`;
}

/* 单条台账记录：申请单号 / 来源 / 状态 / 差异摘要 / 来源变化提示（只读标注） */
function scrRowHtml(row) {
  const statusColor = row.isCancelled ? '#94a3b8' : (row.isDraft ? '#b45309' : '#0f766e');
  const sourceNote = row.sourceAvailable
    ? ''
    : `<div style="color:#b91c1c;font-size:11px">${scrEsc(row.sourceAvailabilityText)}</div>`;
  const changedNote = row.sourceChanged
    ? `<div style="color:#b45309;font-size:11px">⚠ ${scrEsc(row.sourceChangedText)}（${scrEsc(row.sourceChangeDetailText)}）</div>`
    : '';

  return `
  <div style="border:1px solid #e2e8f0;border-radius:10px;padding:8px 10px;margin-bottom:8px;background:${row.isCancelled ? '#f8fafc' : '#fff'}">
    <div style="display:flex;justify-content:space-between;gap:10px;flex-wrap:wrap">
      <div style="font-size:13px">
        <span style="font-weight:600;color:${statusColor}">[${scrEsc(scrStatusLabel(row))}] ${scrEsc(row.requestNo)}</span>
        <span style="color:#64748b">· 来源 ${scrEsc(row.salesOrderNo)}（登记时状态 ${scrEsc(row.sourceStatusText)}）</span>
      </div>
      <div style="font-size:12px;color:#475569;display:flex;gap:6px;flex-wrap:wrap">
        <button class="btn btn-neutral btn-sm" onclick="scrOpenDetail(${row.id})">查看对照</button>
        ${row.editable ? `<button class="btn btn-neutral btn-sm" onclick="scrOpenEdit(${row.id})">编辑拟议</button>` : ''}
        ${row.editable ? `<button class="btn btn-primary btn-sm" onclick="scrSubmit(${row.id})">提交</button>` : ''}
        ${row.isCancelled ? '' : `<button class="btn btn-neutral btn-sm" onclick="scrOpenCancel(${row.id})">取消</button>`}
      </div>
    </div>
    <div style="font-size:12px;color:#334155;margin-top:4px">
      变更原因：${scrEsc(row.reason)} · ${scrEsc(row.changeSummaryText)}
    </div>
    <div style="font-size:11px;color:#64748b;margin-top:2px">
      来源快照：${scrEsc(row.sourceSnapshotMarker)}<br>
      来源总额 ${row.sourceTotalAmount} → 拟议总额 ${row.proposedTotalAmount} ·
      定金 ${row.sourceDepositAmount} → ${row.proposedDepositAmount}
      ${row.submittedAt ? '· 提交时间 ' + scrEsc(row.submittedAt) : ''}
      ${row.cancelledAt ? '· 取消时间 ' + scrEsc(row.cancelledAt) + '（原因：' + scrEsc(row.cancelledReason) + '）' : ''}
    </div>
    ${sourceNote}${changedNote}
  </div>`;
}

/* ==================== 详情：来源 vs 拟议对照 ==================== */

async function scrOpenDetail(id) {
  try {
    SCR.view = await api('/api/sales-order-change-requests/' + id);
    SCR.form = null;
    SCR.cancelId = null;
    scrRender();
  } catch (e) {
    toast('变更申请详情加载失败：' + e.message, 'error');
  }
}

function scrDetailHtml(view) {
  const headerRows = view.headerComparisons.map(c => `
    <tr style="background:${c.changed ? '#fffbeb' : '#fff'}">
      <td style="padding:4px 6px;color:#475569">${scrEsc(c.label)}</td>
      <td style="padding:4px 6px;color:#334155">${scrEsc(c.sourceText)}</td>
      <td style="padding:4px 6px;color:${c.changed ? '#b45309' : '#334155'};font-weight:${c.changed ? '600' : '400'}">${scrEsc(c.proposedText)}</td>
      <td style="padding:4px 6px;color:${c.changed ? '#b45309' : '#94a3b8'}">${c.changed ? '不同（拟议）' : '一致'}</td>
    </tr>`).join('');

  const detailRows = view.details.map(d => `
    <tr style="background:${!d.hasSourceLine ? '#f0fdf4' : (d.proposedRemoved ? '#fef2f2' : (d.changed ? '#fffbeb' : '#fff'))}">
      <td style="padding:4px 6px;color:#64748b">${d.lineNo}</td>
      <td style="padding:4px 6px;color:#334155">${d.hasSourceLine ? scrEsc(d.sourceProductName) + (d.sourceSpec ? ' / ' + scrEsc(d.sourceSpec) : '') : '（无来源行）'}</td>
      <td style="padding:4px 6px;color:#334155">${d.hasSourceLine ? d.sourceQuantity + ' × ' + d.sourceUnitPrice + ' = ' + d.sourceAmount : '—'}</td>
      <td style="padding:4px 6px;color:${d.proposedRemoved ? '#b91c1c' : '#334155'}">${scrEsc(d.proposedProductName)}${d.proposedSpec ? ' / ' + scrEsc(d.proposedSpec) : ''}${d.proposedRemoved ? '（拟议移除）' : ''}</td>
      <td style="padding:4px 6px;color:${d.proposedRemoved ? '#94a3b8' : '#334155'}">${d.proposedRemoved ? '—' : d.proposedQuantity + ' × ' + d.proposedUnitPrice + ' = ' + d.proposedAmount}</td>
      <td style="padding:4px 6px;color:${d.changed ? '#b45309' : '#94a3b8'}">${scrEsc(d.comparisonText)}</td>
    </tr>`).join('');

  const changeBanner = view.sourceChanged
    ? `<div style="background:#fffbeb;border:1px solid #fcd34d;border-radius:8px;padding:8px 10px;font-size:12px;color:#92400e;margin-bottom:8px">
         ⚠ ${scrEsc(view.sourceChangedText)}<br>${scrEsc(view.sourceChangeDetailText)}</div>`
    : `<div style="background:#f0fdf4;border:1px solid #bbf7d0;border-radius:8px;padding:8px 10px;font-size:12px;color:#166534;margin-bottom:8px">
         ${scrEsc(view.sourceChangedText)}（${scrEsc(view.sourceChangeDetailText)}）</div>`;

  const availability = view.sourceAvailable
    ? ''
    : `<div style="color:#b91c1c;font-size:12px;margin-bottom:6px">${scrEsc(view.sourceAvailabilityText)}</div>`;

  return `
  <div style="border:1px solid #c7d2fe;background:#eef2ff;border-radius:10px;padding:10px;margin-top:12px">
    <div style="display:flex;justify-content:space-between;gap:10px;flex-wrap:wrap">
      <div style="font-weight:600;font-size:13px;color:#3730a3">
        ${scrEsc(view.requestNo)} · 来源 ${scrEsc(view.salesOrderNo)} · 状态：${scrEsc(scrStatusLabel(view))}
      </div>
      <button class="btn btn-neutral btn-sm" onclick="scrCloseDetail()">收起对照</button>
    </div>
    <div style="font-size:12px;color:#3730a3;margin-top:4px">
      来源快照：${scrEsc(view.sourceSnapshotMarker)}<br>
      变更原因：${scrEsc(view.reason)} · ${scrEsc(view.changeSummaryText)}<br>
      ${scrEsc(view.approvalBoundaryText)}
    </div>
    ${availability}${changeBanner}
    <div style="overflow:auto;max-height:280px;border:1px solid #c7d2fe;border-radius:8px;background:#fff">
      <table style="width:100%;border-collapse:collapse;font-size:12px">
        <thead><tr style="background:#e0e7ff;color:#3730a3">
          <th style="text-align:left;padding:4px 6px">字段</th>
          <th style="text-align:left;padding:4px 6px">来源快照</th>
          <th style="text-align:left;padding:4px 6px">拟议值（未批准、未套用）</th>
          <th style="text-align:left;padding:4px 6px">差异</th>
        </tr></thead>
        <tbody>${headerRows}</tbody>
      </table>
    </div>
    <div style="font-size:12px;color:#3730a3;margin:8px 0 4px">
      明细对照（来源 ${view.details.filter(d => d.hasSourceLine).length} 行 · 修改 ${view.changedLineCount} 行 ·
      新增 ${view.addedLineCount} 行 · 移除 ${view.removedLineCount} 行）：
    </div>
    <div style="overflow:auto;max-height:260px;border:1px solid #c7d2fe;border-radius:8px;background:#fff">
      <table style="width:100%;border-collapse:collapse;font-size:12px">
        <thead><tr style="background:#e0e7ff;color:#3730a3">
          <th style="text-align:left;padding:4px 6px">行</th>
          <th style="text-align:left;padding:4px 6px">来源商品</th>
          <th style="text-align:left;padding:4px 6px">来源数量 / 单价 / 金额</th>
          <th style="text-align:left;padding:4px 6px">拟议商品</th>
          <th style="text-align:left;padding:4px 6px">拟议数量 / 单价 / 金额</th>
          <th style="text-align:left;padding:4px 6px">对照</th>
        </tr></thead>
        <tbody>${detailRows || '<tr><td colspan="6" style="padding:8px;color:#94a3b8">（无明细行）</td></tr>'}</tbody>
      </table>
    </div>
    <div style="font-size:12px;color:#3730a3;margin-top:6px">
      来源总额 ${view.sourceTotalAmount} → 拟议总额 ${view.proposedTotalAmount} ·
      来源定金 ${view.sourceDepositAmount} → 拟议定金 ${view.proposedDepositAmount}（均由服务端按销售订单口径重算）
    </div>
  </div>`;
}

function scrCloseDetail() {
  SCR.view = null;
  scrRender();
}

/* ==================== 新建草稿（先选来源 + 原因，缺省拟议值 = 来源快照） ==================== */

/* 拟议主表字段定义（与 SalesOrderChangeRequestSaveDto 一一对应；金额不在此列：服务端重算） */
const SCR_HEADER_FIELDS = [
  { key: 'orderDate', label: '拟议订单日期', type: 'date' },
  { key: 'customerId', label: '拟议客户 Id', type: 'number' },
  { key: 'salesmanId', label: '拟议业务员 Id（可空）', type: 'number' },
  { key: 'currency', label: '拟议币种', type: 'currency' },
  { key: 'exchangeRate', label: '拟议汇率（> 0）', type: 'number' },
  { key: 'depositRatio', label: '拟议定金比例 %（0~100）', type: 'number' },
  { key: 'commissionRatio', label: '拟议佣金比例 %（0~100）', type: 'number' },
  { key: 'deliveryDate', label: '拟议交货日期', type: 'date' },
  { key: 'shippingMethod', label: '拟议运输方式', type: 'text' },
  { key: 'portId', label: '拟议目的港 Id（可空）', type: 'number' },
  { key: 'customerPoNo', label: '拟议客户 PO 号', type: 'text' },
  { key: 'contractNo', label: '拟议外销合同号', type: 'text' },
  { key: 'tradeTerms', label: '拟议价格条款', type: 'text' },
  { key: 'destinationPort', label: '拟议目的港', type: 'text' },
  { key: 'exportMode', label: '拟议出口方式', type: 'text' },
  { key: 'businessNature', label: '拟议业务性质', type: 'text' },
  { key: 'splitShipment', label: '拟议分批出货', type: 'bool' },
  { key: 'paymentTerms', label: '拟议付款条件', type: 'textarea' },
  { key: 'consignee', label: '拟议收货人', type: 'textarea' },
  { key: 'notifyParty', label: '拟议通知人', type: 'textarea' },
  { key: 'shippingMarks', label: '拟议唛头', type: 'textarea' },
  { key: 'inspectionRequirement', label: '拟议验货要求', type: 'textarea' },
  { key: 'packagingRequirement', label: '拟议包装要求', type: 'textarea' },
  { key: 'remark', label: '拟议备注', type: 'textarea' }
];

function scrOpenCreate() {
  SCR.createOpen = true;
  SCR.createSourceId = SCR.sourceOrderId || (SCR.sources.length ? String(SCR.sources[0].salesOrderId) : '');
  SCR.createReason = '';
  SCR.view = null;
  SCR.form = null;
  scrRender();
}

function scrCloseCreate() {
  SCR.createOpen = false;
  scrRender();
}

function scrCreateHtml() {
  const options = SCR.sources.map(s => `
    <option value="${s.salesOrderId}" ${s.selectable ? '' : 'disabled'} ${String(s.salesOrderId) === String(SCR.createSourceId) ? 'selected' : ''}>
      ${scrEsc(s.orderNo)}（${scrEsc(s.statusText)}）${s.selectable ? '' : '— 不可选择'}
    </option>`).join('');

  return `
  <div style="border:1px solid #bfdbfe;background:#eff6ff;border-radius:10px;padding:10px;margin-top:12px">
    <div style="font-weight:600;font-size:13px;color:#1d4ed8">新建变更申请草稿（只登记拟议，不批准、不套用）</div>
    <div style="display:grid;grid-template-columns:repeat(auto-fit,minmax(260px,1fr));gap:8px;margin-top:8px;font-size:12px">
      <label>来源销售订单（存在且未删除、未作废）<br>
        <select id="scr-source" onchange="scrCreateSourceChanged()">${options || '<option value="">（无可用销售订单）</option>'}</select></label>
      <label>变更原因（必填）<br>
        <input id="scr-reason" value="${scrEsc(SCR.createReason)}" style="width:100%" placeholder="例如：客户要求改数量与交期"></label>
    </div>
    <div style="font-size:11px;color:#1e40af;margin-top:6px">
      草稿建立时服务端会冻结来源销售订单的主表与明细快照（含状态 / 最后更新时间 / 明细签名），
      并把拟议值初始化为「与来源一致」；之后你可逐项修改拟议值，来源快照不会被覆盖。
    </div>
    <div style="margin-top:8px;display:flex;gap:8px">
      <button class="btn btn-primary btn-sm" onclick="scrCreateDraft()">建立草稿</button>
      <button class="btn btn-neutral btn-sm" onclick="scrCloseCreate()">取消</button>
    </div>
  </div>`;
}

function scrCreateSourceChanged() {
  SCR.createSourceId = document.getElementById('scr-source').value;
  SCR.createReason = document.getElementById('scr-reason').value || '';
}

async function scrCreateDraft() {
  if (SCR.busy) return;
  scrCreateSourceChanged();
  if (!SCR.createSourceId) { toast('请选择来源销售订单', 'error'); return; }
  if (!SCR.createReason.trim()) { toast('请填写变更原因', 'error'); return; }

  SCR.busy = true;
  try {
    const created = await api('/api/sales-order-change-requests', 'POST', {
      salesOrderId: Number(SCR.createSourceId),
      reason: SCR.createReason
    });
    toast('变更申请草稿已登记（缺省拟议值 = 来源快照）；可继续编辑拟议值');
    SCR.createOpen = false;
    SCR.view = created;
    SCR.form = scrFormFromDto(created);
    await scrLoadList();
    scrRender();
  } catch (e) {
    toast('登记失败：' + e.message, 'error');
  } finally {
    SCR.busy = false;
  }
}

/* ==================== 编辑草稿表单 ==================== */

/* 编辑：从详情接口取原始拟议值回填（不用对照文案，避免把格式化值写回） */
async function scrOpenEdit(id) {
  try {
    const dto = await api('/api/sales-order-change-requests/' + id);
    if (!dto.editable) { toast('该申请已提交或已取消，不能再编辑', 'error'); return; }
    SCR.view = dto;
    SCR.form = scrFormFromDto(dto);
    SCR.createOpen = false;
    scrRender();
  } catch (e) {
    toast('变更申请加载失败：' + e.message, 'error');
  }
}

function scrCloseForm() {
  SCR.form = null;
  scrRender();
}

function scrFormFromDto(dto) {
  const p = dto.proposed;
  return {
    id: dto.id,
    requestNo: dto.requestNo,
    reason: dto.reason,
    header: {
      orderDate: scrDateInput(p.orderDate),
      customerId: p.customerId,
      salesmanId: p.salesmanId === null ? '' : p.salesmanId,
      currency: p.currency,
      exchangeRate: p.exchangeRate,
      depositRatio: p.depositRatio,
      commissionRatio: p.commissionRatio,
      deliveryDate: scrDateInput(p.deliveryDate),
      shippingMethod: p.shippingMethod,
      portId: p.portId === null ? '' : p.portId,
      customerPoNo: p.customerPoNo,
      contractNo: p.contractNo,
      tradeTerms: p.tradeTerms,
      destinationPort: p.destinationPort,
      exportMode: p.exportMode,
      businessNature: p.businessNature,
      splitShipment: p.splitShipment,
      paymentTerms: p.paymentTerms,
      consignee: p.consignee,
      notifyParty: p.notifyParty,
      shippingMarks: p.shippingMarks,
      inspectionRequirement: p.inspectionRequirement,
      packagingRequirement: p.packagingRequirement,
      remark: p.remark
    },
    details: dto.details.map(d => ({
      hasSourceLine: d.hasSourceLine,
      removed: d.proposedRemoved,
      sourceText: d.hasSourceLine
        ? d.sourceProductName + (d.sourceSpec ? ' / ' + d.sourceSpec : '')
          + ' · ' + d.sourceQuantity + ' × ' + d.sourceUnitPrice + ' = ' + d.sourceAmount
        : '（来源没有对应行：拟议新增行）',
      productId: d.proposedProductId,
      productName: d.proposedProductName,
      spec: d.proposedSpec,
      unit: d.proposedUnit,
      quantity: d.proposedQuantity,
      unitPrice: d.proposedUnitPrice,
      deliveryDate: scrDateInput(d.proposedDeliveryDate),
      remark: d.proposedRemark
    }))
  };
}

function scrFormHtml() {
  const f = SCR.form;
  const meta = SCR.metadata;
  const currencyOptions = (meta ? meta.currencyOptions : []).map(o =>
    `<option value="${scrEsc(o.value)}" ${String(o.value) === String(f.header.currency) ? 'selected' : ''}>${scrEsc(o.label)}</option>`).join('');

  const headerInputs = SCR_HEADER_FIELDS.map(field => {
    const id = 'scr-h-' + field.key;
    const value = f.header[field.key];
    if (field.type === 'currency') {
      return `<label>${scrEsc(field.label)}<br><select id="${id}">${currencyOptions}</select></label>`;
    }
    if (field.type === 'bool') {
      return `<label style="display:flex;align-items:center;gap:6px">
        <input id="${id}" type="checkbox" ${value ? 'checked' : ''}> ${scrEsc(field.label)}</label>`;
    }
    if (field.type === 'textarea') {
      return `<label style="grid-column:1/-1">${scrEsc(field.label)}<br>
        <input id="${id}" value="${scrEsc(value)}" style="width:100%"></label>`;
    }
    const inputType = field.type === 'date' ? 'date' : (field.type === 'number' ? 'number' : 'text');
    const step = field.type === 'number' ? ' step="any"' : '';
    return `<label>${scrEsc(field.label)}<br>
      <input id="${id}" type="${inputType}"${step} value="${scrEsc(value)}" style="width:100%"></label>`;
  }).join('');

  const detailRows = f.details.map((d, i) => scrDetailRowHtml(d, i)).join('');
  const sourceRowCount = f.details.filter(d => d.hasSourceLine).length;

  return `
  <div style="border:1px solid #a5b4fc;background:#f5f3ff;border-radius:10px;padding:10px;margin-top:12px">
    <div style="display:flex;justify-content:space-between;gap:10px;flex-wrap:wrap">
      <div style="font-weight:600;font-size:13px;color:#4338ca">
        编辑变更申请拟议值 · ${scrEsc(f.requestNo || '（未落库）')}
      </div>
      <button class="btn btn-neutral btn-sm" onclick="scrCloseForm()">收起表单</button>
    </div>
    <div style="font-size:11px;color:#4338ca;margin-top:4px">
      ${scrEsc(meta ? meta.amountPolicyText : '')}<br>${scrEsc(meta ? meta.snapshotPolicyText : '')}
    </div>
    <div style="font-size:12px;color:#4338ca;margin:8px 0 4px">变更原因（必填）</div>
    <input id="scr-h-reason" value="${scrEsc(f.reason)}" style="width:100%">
    <div style="font-size:12px;color:#4338ca;margin:8px 0 4px">拟议主表值（缺省 = 来源快照；金额由服务端重算，不在此填写）</div>
    <div style="display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:8px;font-size:12px">
      ${headerInputs}
    </div>
    <div style="font-size:12px;color:#4338ca;margin:10px 0 4px">
      拟议明细（前 ${sourceRowCount} 行对应来源行，可标记移除；之后为新增行；金额 = 数量 × 单价，由服务端重算）
    </div>
    <div style="overflow:auto;border:1px solid #a5b4fc;border-radius:8px;background:#fff">
      <table style="width:100%;border-collapse:collapse;font-size:12px">
        <thead><tr style="background:#e0e7ff;color:#3730a3">
          <th style="text-align:left;padding:4px 6px">行</th>
          <th style="text-align:left;padding:4px 6px">来源行快照（只读）</th>
          <th style="text-align:left;padding:4px 6px">拟议商品名称</th>
          <th style="text-align:left;padding:4px 6px">规格</th>
          <th style="text-align:left;padding:4px 6px">单位</th>
          <th style="text-align:left;padding:4px 6px">数量</th>
          <th style="text-align:left;padding:4px 6px">单价</th>
          <th style="text-align:left;padding:4px 6px">金额</th>
          <th style="text-align:left;padding:4px 6px">操作</th>
        </tr></thead>
        <tbody>${detailRows || '<tr><td colspan="9" style="padding:8px;color:#94a3b8">（无明细行：保存后拟议回到来源快照）</td></tr>'}</tbody>
      </table>
    </div>
    <div style="margin-top:8px;display:flex;gap:8px;flex-wrap:wrap">
      <button class="btn btn-neutral btn-sm" onclick="scrAddDetailRow()">＋ 新增拟议明细行</button>
      <button class="btn btn-primary btn-sm" onclick="scrSaveForm()">保存草稿拟议值</button>
      <button class="btn btn-neutral btn-sm" onclick="scrCloseForm()">取消</button>
    </div>
    <div style="font-size:11px;color:#4338ca;margin-top:6px">
      保存只写入本申请：来源销售订单、报价单、出库、装柜、收款与发票、佣金、库存与财务记录都不会被改写；
      可选日期字段留空按「沿用当前拟议值」处理。
    </div>
  </div>`;
}

/* 单行拟议明细编辑（来源行显示只读快照 + 拟议输入；新增行只有拟议输入） */
function scrDetailRowHtml(d, index) {
  const disabled = d.removed ? 'disabled' : '';
  return `
  <tr style="background:${d.removed ? '#fef2f2' : (d.hasSourceLine ? '#fff' : '#f0fdf4')}">
    <td style="padding:4px 6px;color:#64748b">${index + 1}</td>
    <td style="padding:4px 6px;color:#475569">${scrEsc(d.sourceText)}</td>
    <td style="padding:4px 6px"><input id="scr-d-${index}-productName" value="${scrEsc(d.productName)}" ${disabled} style="width:120px"></td>
    <td style="padding:4px 6px"><input id="scr-d-${index}-spec" value="${scrEsc(d.spec)}" ${disabled} style="width:90px"></td>
    <td style="padding:4px 6px"><input id="scr-d-${index}-unit" value="${scrEsc(d.unit)}" ${disabled} style="width:60px"></td>
    <td style="padding:4px 6px"><input id="scr-d-${index}-quantity" type="number" step="any" value="${scrEsc(d.quantity)}" ${disabled} oninput="scrUpdateAmount(${index})" style="width:80px"></td>
    <td style="padding:4px 6px"><input id="scr-d-${index}-unitPrice" type="number" step="any" value="${scrEsc(d.unitPrice)}" ${disabled} oninput="scrUpdateAmount(${index})" style="width:80px"></td>
    <td style="padding:4px 6px;color:#475569" id="scr-d-${index}-amount">${d.removed ? '—' : scrAmountText(d.quantity, d.unitPrice)}</td>
    <td style="padding:4px 6px">
      ${d.hasSourceLine
        ? `<button class="btn btn-neutral btn-sm" onclick="scrToggleRemove(${index})">${d.removed ? '恢复来源行' : '标记移除'}</button>`
        : `<button class="btn btn-neutral btn-sm" onclick="scrDropNewRow(${index})">删除新增行</button>`}
    </td>
  </tr>`;
}

/* 金额仅为界面预览；保存时服务端按销售订单唯一权威算法重算并回传 */
function scrAmountText(quantity, unitPrice) {
  const q = Number(quantity) || 0;
  const p = Number(unitPrice) || 0;
  return (q * p).toFixed(4) + '（预览，服务端重算）';
}

function scrUpdateAmount(index) {
  const q = document.getElementById('scr-d-' + index + '-quantity').value;
  const p = document.getElementById('scr-d-' + index + '-unitPrice').value;
  const cell = document.getElementById('scr-d-' + index + '-amount');
  if (cell) cell.textContent = scrAmountText(q, p);
}

function scrReadDetails() {
  SCR.form.details = SCR.form.details.map((d, i) => {
    if (d.removed) return d;
    const read = (suffix, fallback) => {
      const el = document.getElementById('scr-d-' + i + '-' + suffix);
      return el ? el.value : fallback;
    };
    return Object.assign({}, d, {
      productName: read('productName', d.productName),
      spec: read('spec', d.spec),
      unit: read('unit', d.unit),
      quantity: read('quantity', d.quantity),
      unitPrice: read('unitPrice', d.unitPrice)
    });
  });
}

function scrToggleRemove(index) {
  scrReadDetails();
  const row = SCR.form.details[index];
  if (!row) return;
  row.removed = !row.removed;
  /* 移除行按服务端语义一律回到来源快照；界面也同步显示来源值，避免展示假差异 */
  if (row.removed) {
    row.productName = ''; row.spec = ''; row.unit = '';
    row.quantity = 0; row.unitPrice = 0;
  }
  scrRender();
}

function scrAddDetailRow() {
  scrReadDetails();
  SCR.form.details.push({
    hasSourceLine: false, removed: false,
    sourceText: '（来源没有对应行：拟议新增行）',
    productId: 0, productName: '', spec: '', unit: '',
    quantity: 1, unitPrice: 0, deliveryDate: '', remark: ''
  });
  scrRender();
}

function scrDropNewRow(index) {
  scrReadDetails();
  const row = SCR.form.details[index];
  if (!row || row.hasSourceLine) return;
  SCR.form.details.splice(index, 1);
  scrRender();
}

/* ==================== 保存 / 提交 / 取消 ==================== */

function scrReadHeader() {
  const h = SCR.form.header;
  SCR_HEADER_FIELDS.forEach(field => {
    const el = document.getElementById('scr-h-' + field.key);
    if (!el) return;
    h[field.key] = field.type === 'bool' ? !!el.checked : el.value;
  });
  const reasonEl = document.getElementById('scr-h-reason');
  if (reasonEl) SCR.form.reason = reasonEl.value;
}

/* 报文构造：日期 / 数值留空按「沿用当前拟议值」处理（发 null），文本留空按空串（显式清空） */
function scrPayload() {
  const f = SCR.form;
  const h = f.header;
  const numOrNull = value => (value === '' || value === null || value === undefined) ? null : Number(value);

  return {
    salesOrderId: 0,
    reason: f.reason || '',
    orderDate: h.orderDate || null,
    customerId: Number(h.customerId) || 0,
    salesmanId: numOrNull(h.salesmanId),
    currency: h.currency || null,
    exchangeRate: numOrNull(h.exchangeRate),
    depositRatio: numOrNull(h.depositRatio),
    paymentTerms: h.paymentTerms || '',
    deliveryDate: h.deliveryDate || null,
    shippingMethod: h.shippingMethod || '',
    portId: numOrNull(h.portId),
    remark: h.remark || '',
    customerPoNo: h.customerPoNo || '',
    contractNo: h.contractNo || '',
    tradeTerms: h.tradeTerms || '',
    destinationPort: h.destinationPort || '',
    consignee: h.consignee || '',
    notifyParty: h.notifyParty || '',
    shippingMarks: h.shippingMarks || '',
    exportMode: h.exportMode || '',
    commissionRatio: numOrNull(h.commissionRatio),
    businessNature: h.businessNature || '',
    splitShipment: !!h.splitShipment,
    inspectionRequirement: h.inspectionRequirement || '',
    packagingRequirement: h.packagingRequirement || '',
    details: f.details.map((d, i) => ({
      lineNo: i + 1,
      removed: !!d.removed,
      productId: Number(d.productId) || 0,
      productName: d.productName || '',
      spec: d.spec || '',
      unit: d.unit || '',
      quantity: Number(d.quantity) || 0,
      unitPrice: Number(d.unitPrice) || 0,
      deliveryDate: d.deliveryDate || null,
      remark: d.remark || ''
    }))
  };
}

async function scrSaveForm() {
  if (SCR.busy) return;
  scrReadHeader();
  scrReadDetails();
  if (!SCR.form.reason || !SCR.form.reason.trim()) { toast('请填写变更原因', 'error'); return; }

  SCR.busy = true;
  try {
    const updated = await api('/api/sales-order-change-requests/' + SCR.form.id, 'PUT', scrPayload());
    toast('草稿拟议值已保存（金额由服务端重算；来源订单与下游记录未改写）');
    SCR.view = updated;
    /* 用服务端返回值重建表单，保证界面显示的就是权威拟议值 */
    SCR.form = scrFormFromDto(updated);
    await scrLoadList();
    scrRender();
  } catch (e) {
    toast('保存失败：' + e.message, 'error');
  } finally {
    SCR.busy = false;
  }
}

async function scrSubmit(id) {
  if (SCR.busy) return;
  if (!window.confirm('提交后拟议快照将冻结、不可再编辑（仍可取消）。\n系统不会批准或套用该变更，也不会改写来源销售订单。确认提交？')) return;

  SCR.busy = true;
  try {
    const dto = await api('/api/sales-order-change-requests/' + id + '/submit', 'POST', {});
    toast('变更申请已提交（仅登记冻结：未批准、未套用）');
    SCR.view = dto;
    SCR.form = null;
    await scrLoadList();
    scrRender();
  } catch (e) {
    toast('提交失败：' + e.message, 'error');
  } finally {
    SCR.busy = false;
  }
}

function scrOpenCancel(id) {
  SCR.cancelId = id;
  SCR.cancelReason = '';
  scrRender();
}

function scrCloseCancel() {
  SCR.cancelId = null;
  SCR.cancelReason = '';
  scrRender();
}

async function scrSubmitCancel() {
  const reason = document.getElementById('scr-cancel-reason').value || '';
  if (!reason.trim()) { toast('请填写取消原因', 'error'); return; }

  try {
    const dto = await api('/api/sales-order-change-requests/' + SCR.cancelId + '/cancel', 'POST', { reason: reason });
    toast('变更申请已取消（原始与拟议证据保留，可读）');
    SCR.cancelId = null;
    SCR.view = dto;
    SCR.form = null;
    await scrLoadList();
    scrRender();
  } catch (e) {
    toast('取消失败：' + e.message, 'error');
  }
}

function scrCancelHtml() {
  return `
  <div style="border:1px solid #fecaca;background:#fef2f2;border-radius:10px;padding:10px;margin-top:12px">
    <div style="font-weight:600;font-size:13px;color:#b91c1c">取消变更申请（保留原始与拟议证据，不做硬删除）</div>
    <div style="font-size:12px;color:#7f1d1d;margin:6px 0">取消原因（必填）：</div>
    <input id="scr-cancel-reason" value="${scrEsc(SCR.cancelReason)}" style="width:100%" placeholder="例如：客户撤回变更要求 / 拟议值填错，重新登记">
    <div style="margin-top:8px;display:flex;gap:8px">
      <button class="btn btn-primary btn-sm" onclick="scrSubmitCancel()">确认取消</button>
      <button class="btn btn-neutral btn-sm" onclick="scrCloseCancel()">返回</button>
    </div>
  </div>`;
}

/* ==================== 筛选与分页 ==================== */

function scrFiltersChanged() {
  const statusEl = document.getElementById('scr-fstatus');
  const keywordEl = document.getElementById('scr-fkeyword');
  SCR.filters.status = statusEl ? statusEl.value : '';
  SCR.filters.keyword = keywordEl ? (keywordEl.value || '') : '';
  SCR.page = 1;
  scrLoadList().then(() => scrRender());
}

function scrResetFilters() {
  SCR.filters.status = '';
  SCR.filters.keyword = '';
  SCR.page = 1;
  scrLoadList().then(() => scrRender());
}

function scrPage(delta) {
  const next = SCR.page + delta;
  if (next < 1) return;
  if (delta > 0 && (next - 1) * SCR.pageSize >= SCR.total) return;
  SCR.page = next;
  scrLoadList().then(() => scrRender());
}

/* 日期输入值（yyyy-MM-dd；空值返回空串） */
function scrDateInput(value) {
  if (!value) return '';
  return String(value).slice(0, 10);
}
