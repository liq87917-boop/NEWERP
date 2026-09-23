/* ============ 通用单据（存储过程版）前端 ============ */
const BIZ_STATUS = {
  1: ['保存', 'status-info'],
  2: ['已审核', 'status-success'],
  '-1': ['已作废', 'status-danger'],
};
function bizStatusHtml(status) {
  const s = BIZ_STATUS[status];
  return s ? `<span class="status ${s[1]}">${s[0]}</span>` : (status ?? '');
}

/* 菜单编码 -> 单据类型映射 */
const BILL_CODE_MAP = {
  /* ERP-008：销售订单 / 采购订单改由 EF 主子表页面承载（modules-doc.js 的 'sales-order' / 'purchase-order'
     模块 + /api/sales-orders、/api/purchase-orders），以便保存与打印外贸合同、来源追溯与采购执行字段。
     SP 版单据页（BILL_CONFIG 中的同名配置）保留给历史数据排查/回滚参考，不再从菜单进入。 */
  'inquiry-new': 'inquiry',
  'stock-in': 'stock-in',
  'stock-out': 'stock-out',
  receipt: 'receipt',
  payment: 'payment',
  'deposit-apply': 'deposit-apply',
  'payment-apply': 'payment-apply',
  'container-settlement': 'container-settlement',
  'bulk-settlement': 'bulk-settlement',
  complaint: 'complaint',
  'receiving-plan': 'receiving-plan',
  booking: 'booking',
  'pre-loading': 'pre-loading',
  'loading-list': 'loading-list',
};

let BILL_CODE = '';
let BILL_CURRENT_OID = 0;

function renderBillV2(code) {
  const cfg = BILL_CONFIG[code];
  if (!cfg) { document.getElementById('content').innerHTML = '<div class="card empty">该单据未配置</div>'; return; }
  BILL_CODE = code;
  CURRENT_LOADER = loadBills;
  CURRENT_PAGE = 1;
  document.getElementById('content').innerHTML = `
    <div class="toolbar">
      <div class="toolbar-left">
        <input type="text" id="bill-keyword" placeholder="按单据号搜索..." onkeydown="if(event.key==='Enter')loadBills()">
        <select id="bill-status" onchange="loadBills()">
          <option value="">全部状态</option>
          <option value="1">保存</option>
          <option value="2">已审核</option>
          <option value="-1">已作废</option>
        </select>
        <button class="btn btn-neutral" onclick="loadBills()">搜索</button>
        <button class="btn btn-neutral btn-sm" onclick="showBillLogs(0,'')" title="查看该单据类型的操作日志">📜 操作日志</button>
      </div>
      <div class="toolbar-actions">
        <button class="btn btn-neutral" onclick="openBillImportDialog()" title="从 Excel 导入单据">📥 导入</button>
        <button class="btn btn-neutral" onclick="exportBillExcel()" title="导出为 Excel">📤 导出</button>
        <button class="btn btn-neutral" onclick="gotoPrintDesign(BILL_CODE)" title="打开样式设计，配置该单据的打印模板">🎨 打印设计</button>
        <button class="btn btn-neutral" onclick="batchPrintBills()" title="勾选表格左侧复选框后，可一次打印多张单据">🖨 批量打印</button>
        <button class="btn btn-primary" onclick="renderBillEdit()">+ 新增</button>
      </div>
    </div>
    <div class="table-wrap" id="table-wrap"></div>
    <div class="pagination" id="pagination"></div>`;
  loadBills();
}

async function loadBills() {
  const kw = document.getElementById('bill-keyword').value.trim();
  const st = document.getElementById('bill-status').value;
  let qs = `page=${CURRENT_PAGE}&pageSize=${PAGE_SIZE}`;
  if (kw) qs += '&keyword=' + encodeURIComponent(kw);
  if (st) qs += '&status=' + st;
  const data = await api(`/api/v2/bills/${BILL_CODE}?${qs}`);
  renderBillTable(data);
  renderPagination(data);
}

function renderBillTable(data) {
  closeRowMenu();                       // 重渲染前收起可能残留的行操作菜单
  const cfg = BILL_CONFIG[BILL_CODE];
  const wrap = document.getElementById('table-wrap');
  if (!data.items || !data.items.length) { wrap.innerHTML = '<div class="empty">暂无数据</div>'; return; }
  // 记录首行数据，供「打印设计 → 效果预览」作为样例
  window.__billSampleRow = data.items[0];
  const head = `<th class="text-center" style="width:40px"><input type="checkbox" title="全选本页" onclick="toggleAllBillRows(this)"></th>`
    + cfg.columns.map(c => `<th>${c.label}</th>`).join('');
  const rows = data.items.map(r => {
    const tds = cfg.columns.map(c => {
      const v = r[c.key];
      if (c.status) return `<td>${bizStatusHtml(v)}</td>`;
      if (c.type === 'money') return `<td class="text-right">${fmtMoney(v)}</td>`;
      if (c.type === 'date') return `<td>${fmtDate(v)}</td>`;
      return `<td>${v ?? ''}</td>`;
    }).join('');
    const st = r.Status;
    // 操作列：仅保留「编辑」，其余操作收进「更多」下拉菜单
    let more = '';
    if (st === 1) {
      more += `<button class="row-menu-item" onclick="billAction(${r.Oid},'audit')"><span class="rmi-ico">✅</span><span class="rmi-txt">审核</span></button>
               <button class="row-menu-item" onclick="copyBill(${r.Oid})"><span class="rmi-ico">📄</span><span class="rmi-txt">复制</span></button>`;
    }
    if (st === 2) {
      more += `<button class="row-menu-item" onclick="billAction(${r.Oid},'unaudit')" title="撤销审核，单据回到保存状态"><span class="rmi-ico">↩️</span><span class="rmi-txt">销审</span></button>`;
    }
    if (st === 1 || st === 2) {
      more += `<button class="row-menu-item" onclick="previewBillPrint(${r.Oid})" title="打印预览"><span class="rmi-ico">🖨</span><span class="rmi-txt">打印预览</span></button>
               <button class="row-menu-item" onclick="printBill(${r.Oid})" title="直接打印"><span class="rmi-ico">🖨</span><span class="rmi-txt">打印</span></button>`;
    }
    more += `<div class="row-menu-sep"></div>
             <button class="row-menu-item" onclick="showBillLogs(${r.Oid},'${r.BillNo || ''}')" title="查看操作日志"><span class="rmi-ico">📜</span><span class="rmi-txt">日志</span></button>`;
    if (st === 1) more += `<button class="row-menu-item danger" onclick="billAction(${r.Oid},'delete')"><span class="rmi-ico">🗑</span><span class="rmi-txt">删除</span></button>`;
    if (st === 2) more += `<button class="row-menu-item danger" onclick="billAction(${r.Oid},'void')"><span class="rmi-ico">🚫</span><span class="rmi-txt">作废</span></button>`;
    if (st === -1) more += `<button class="row-menu-item" onclick="billAction(${r.Oid},'restore')"><span class="rmi-ico">♻️</span><span class="rmi-txt">还原</span></button>`;
    const acts = `<div class="row-actions">
        <button class="btn btn-neutral btn-sm" onclick="renderBillEdit(${r.Oid})">编辑</button>
        <button class="btn btn-neutral btn-sm row-more" onclick="toggleRowMenu(event, this)" title="更多操作">更多</button>
      </div>
      <div class="row-menu" hidden>${more}</div>`;
    return `<tr><td class="text-center"><input type="checkbox" class="bill-row-chk" value="${r.Oid}"></td>${tds}<td>${acts}</td></tr>`;
  }).join('');
  wrap.innerHTML = `<table><thead><tr>${head}<th style="width:150px">操作</th></tr></thead><tbody>${rows}</tbody></table>`;
}

async function billAction(oid, action) {
  if (action === 'delete' && !confirm('确认删除该单据？')) return;
  try {
    await api(`/api/v2/bills/${BILL_CODE}/${oid}/${action}`, 'POST');
    toast('操作成功');
    // 列表页刷新列表；编辑页（无列表工具栏）回到列表
    if (document.getElementById('bill-keyword')) loadBills();
    else renderBillV2(BILL_CODE);
  } catch (err) { toast(err.message, 'error'); }
}

async function navBill(oid, direction) {
  try {
    const d = await api(`/api/v2/bills/${BILL_CODE}/navigate?oid=${oid}&direction=${direction}`);
    if (d && d.Oid) {
      BILL_CURRENT_OID = d.Oid;
      document.getElementById('bill-keyword').value = d.BillNo || '';
      loadBills();
    } else { toast('没有更多单据', 'error'); }
  } catch (err) { toast(err.message, 'error'); }
}

function copyBill(oid) {
  toast('复制功能：已打开新单据表单');
  renderBillEdit();          // 打开新建表单（Oid=0，保存即为新增）
  loadBillEditIntoForm(oid); // 用原单数据填充新表单，实现复制
}

/* ============ 批量打印（勾选多张单据 → 生成多页打印任务） ============ */
function toggleAllBillRows(el) {
  document.querySelectorAll('.bill-row-chk').forEach(c => { c.checked = !!(el && el.checked); });
}

async function batchPrintBills() {
  const ids = Array.from(document.querySelectorAll('.bill-row-chk:checked'))
    .map(c => Number(c.value)).filter(n => n > 0);
  if (!ids.length) { toast('请先勾选要打印的单据（表格最左侧复选框）', 'error'); return; }
  if (ids.length > 50) { toast('一次最多批量打印 50 张单据', 'error'); return; }

  ensurePrintStyle();
  const btn = event && event.target;
  try {
    if (btn) { btn.disabled = true; btn.textContent = '生成中…'; }
    const tplRaw = await api(`/api/sys/print-templates/${encodeURIComponent(BILL_CODE)}`);
    const tpl = normalizeTemplate(tplRaw);
    const labels = billFieldLabels(BILL_CODE);
    const billTitle = (BILL_CONFIG[BILL_CODE] && BILL_CONFIG[BILL_CODE].title) || BILL_CODE;
    const operator = (PROFILE && (PROFILE.displayName || PROFILE.userName)) || '';

    let pages = '';
    for (let i = 0; i < ids.length; i++) {
      const d = await api(`/api/v2/bills/${BILL_CODE}/${ids[i]}`);
      const main = Object.assign({}, d.main || {}, { __operator: operator, Operator: operator });
      const details = d.details || [];
      pages += tpl.LayoutJson
        ? buildGridPrintHtml(tpl, BILL_CODE, main, details, { page: i + 1, total: ids.length })
        : buildPrintHtml({ code: BILL_CODE, billTitle, labels, template: tpl, main, details });
    }

    const win = window.open('', '_blank', 'width=1000,height=800');
    win.document.write(`<!DOCTYPE html><html lang="zh-CN"><head><meta charset="utf-8">
      <title>批量打印 - ${escapeHtml(billTitle)}（${ids.length} 张）</title>
      <style>${PRINT_STYLE}
      .print-page { position: relative; page-break-after: always; }
      .print-page:last-child { page-break-after: auto; }
      .pw-layer { position: absolute; inset: 0; display: grid; grid-template-columns: repeat(4, 1fr);
        align-items: center; justify-items: center; pointer-events: none; overflow: hidden; z-index: 0; }
      .pw-layer.pw-center { display: flex; align-items: center; justify-content: center; }
      .pw-layer span { font-size: var(--pw-size, 20px); font-weight: 700; white-space: nowrap; letter-spacing: 2px; }
      .pc-print-grid, .pc-print-detail, .pc-print-footer { position: relative; z-index: 1; }
      body { margin: 0; } @page { margin: 8mm; }</style></head>
    <body>${pages}</body></html>`);
    win.document.close();
    setTimeout(() => { try { win.focus(); win.print(); } catch (e) { /* 忽略 */ } }, 600);
    toast('已生成 ' + ids.length + ' 张单据的打印页');
  } catch (e) {
    toast('批量打印失败：' + e.message, 'error');
  } finally {
    if (btn) { btn.disabled = false; btn.textContent = '🖨 批量打印'; }
  }
}
