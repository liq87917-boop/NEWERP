/* ============ 销售订单（v2 存储过程版）完整生命周期 ============ */
const BIZ_STATUS = {
  1: ['保存', 'status-info'],
  2: ['已审核', 'status-success'],
  '-1': ['已作废', 'status-danger'],
};
function bizStatusHtml(status) {
  const s = BIZ_STATUS[status];
  return s ? `<span class="status ${s[1]}">${s[0]}</span>` : (status ?? '');
}

let SO_PAGE = 1;
let SO_CURRENT_OID = 0;

function renderSalesOrderV2() {
  SO_PAGE = 1;
  document.getElementById('content').innerHTML = `
    <div class="toolbar">
      <div class="toolbar-left">
        <input type="text" id="so-keyword" placeholder="按单据号搜索..." onkeydown="if(event.key==='Enter')loadSalesOrders()">
        <select id="so-status" onchange="loadSalesOrders()">
          <option value="">全部状态</option>
          <option value="1">保存</option>
          <option value="2">已审核</option>
          <option value="-1">已作废</option>
        </select>
        <button class="btn btn-neutral" onclick="loadSalesOrders()">搜索</button>
      </div>
      <div><button class="btn btn-primary" onclick="openSoForm()">+ 新增</button></div>
    </div>
    <div class="table-wrap" id="table-wrap"></div>
    <div class="pagination" id="pagination"></div>`;
  loadSalesOrders();
}

async function loadSalesOrders() {
  const kw = document.getElementById('so-keyword').value.trim();
  const st = document.getElementById('so-status').value;
  let qs = `page=${SO_PAGE}&pageSize=10`;
  if (kw) qs += '&keyword=' + encodeURIComponent(kw);
  if (st) qs += '&status=' + st;
  const data = await api(`/api/v2/sales-orders?${qs}`);
  renderSoTable(data);
  renderPagination(data);
}

function renderSoTable(data) {
  closeRowMenu();                       // 重渲染前收起可能残留的行操作菜单
  const wrap = document.getElementById('table-wrap');
  if (!data.items || !data.items.length) { wrap.innerHTML = '<div class="empty">暂无数据</div>'; return; }
  const rows = data.items.map(r => `<tr>
    <td>${r.billNo || '-'}</td>
    <td>${fmtDate(r.orderDate)}</td>
    <td>${r.custId}</td>
    <td class="text-right">${fmtMoney(r.totalAmount)}</td>
    <td class="text-right">${fmtMoney(r.depositAmount)}</td>
    <td>${bizStatusHtml(r.status)}</td>
    <td>
      <div class="row-actions">
        <button class="btn btn-neutral btn-sm" onclick="openSoForm(${r.oid})">编辑</button>
        <button class="btn btn-neutral btn-sm row-more" onclick="toggleRowMenu(event, this)" title="更多操作">更多</button>
      </div>
      <div class="row-menu" hidden>${soActions(r)}</div>
    </td>
  </tr>`).join('');
  wrap.innerHTML = `<table><thead><tr>
    <th>单据号</th><th>日期</th><th>客户Id</th><th class="text-right">总额</th><th class="text-right">定金</th><th>状态</th><th style="width:150px">操作</th>
  </tr></thead><tbody>${rows}</tbody></table>`;
}

/* 行内「更多」菜单项（样式统一为 .row-menu-item） */
function soActions(r) {
  let html = '';
  if (r.status === 1) {
    html += `<button class="row-menu-item" onclick="soAction(${r.oid},'audit')"><span class="rmi-ico">✅</span><span class="rmi-txt">审核</span></button>`;
    html += `<button class="row-menu-item" onclick="copyOrder(${r.oid})"><span class="rmi-ico">📄</span><span class="rmi-txt">复制</span></button>`;
    html += `<div class="row-menu-sep"></div>`;
    html += `<button class="row-menu-item danger" onclick="soAction(${r.oid},'delete')"><span class="rmi-ico">🗑</span><span class="rmi-txt">删除</span></button>`;
  }
  if (r.status === 2) html += `<button class="row-menu-item danger" onclick="soAction(${r.oid},'void')"><span class="rmi-ico">🚫</span><span class="rmi-txt">作废</span></button>`;
  if (r.status === -1) html += `<button class="row-menu-item" onclick="soAction(${r.oid},'restore')"><span class="rmi-ico">♻️</span><span class="rmi-txt">还原</span></button>`;
  return html;
}

async function soAction(oid, action) {
  const names = { audit: '审核', void: '作废', restore: '还原', delete: '删除' };
  if (action === 'delete' && !confirm('确认删除该单据？')) return;
  try {
    await api(`/api/v2/sales-orders/${oid}/${action}`, 'POST');
    toast((names[action] || '操作') + '成功');
    loadSalesOrders();
  } catch (err) { toast(err.message, 'error'); }
}

async function navOrder(oid, direction) {
  try {
    const d = await api(`/api/v2/sales-orders/navigate?oid=${oid}&direction=${direction}`);
    if (d && d.oid) {
      SO_CURRENT_OID = d.oid;
      document.getElementById('so-keyword').value = d.billNo || '';
      loadSalesOrders();
    } else { toast('没有更多单据', 'error'); }
  } catch (err) { toast(err.message, 'error'); }
}

function copyOrder(oid) {
  toast('复制功能：已打开新单据表单');
  openSoForm();
}
