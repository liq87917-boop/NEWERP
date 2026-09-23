/* ============ 用户管理模块（列表） ============ */
let ALL_ROLES = [];

async function loadRoles() {
  if (ALL_ROLES.length) return ALL_ROLES;
  ALL_ROLES = await api('/api/sys/roles/all');
  return ALL_ROLES;
}

function renderUserModule() {
  CURRENT_LOADER = loadUserList;
  CURRENT_PAGE = 1;
  CURRENT_KEYWORD = '';
  document.getElementById('content').innerHTML = `
    <div class="toolbar">
      <div class="toolbar-left">
        <input type="text" id="search-input" placeholder="搜索用户名/姓名..." onkeydown="if(event.key==='Enter')searchUserList()">
        <button class="btn btn-neutral" onclick="searchUserList()">搜索</button>
      </div>
      <div><button class="btn btn-primary" onclick="openUserForm()">+ 新增用户</button></div>
    </div>
    <div class="table-wrap" id="table-wrap"></div>
    <div class="pagination" id="pagination"></div>`;
  loadUserList();
}

function searchUserList() {
  CURRENT_KEYWORD = document.getElementById('search-input').value.trim();
  CURRENT_PAGE = 1;
  loadUserList();
}

async function loadUserList() {
  const qs = `page=${CURRENT_PAGE}&pageSize=${PAGE_SIZE}${CURRENT_KEYWORD ? '&keyword=' + encodeURIComponent(CURRENT_KEYWORD) : ''}`;
  const data = await api(`/api/sys/users?${qs}`);
  renderUserTable(data);
  renderPagination(data);
}

function userStatusHtml(status) {
  const enabled = status === 'Enabled' || status === 1;
  return enabled ? '<span class="status status-success">启用</span>' : '<span class="status status-neutral">禁用</span>';
}

function renderUserTable(data) {
  closeRowMenu();                       // 重渲染前收起可能残留的行操作菜单
  const wrap = document.getElementById('table-wrap');
  if (!data.items || !data.items.length) { wrap.innerHTML = '<div class="empty">暂无数据</div>'; return; }
  const rows = data.items.map(u => {
    const enabled = u.status === 'Enabled' || u.status === 1;
    const roles = (u.roleNames || []).join(', ') || '-';
    return `<tr>
      <td>${u.userName}</td>
      <td>${u.displayName || '-'}</td>
      <td>${u.email || '-'}</td>
      <td>${u.phone || '-'}</td>
      <td>${userStatusHtml(u.status)}</td>
      <td>${roles}</td>
      <td>${fmtDate(u.lastLoginTime)}</td>
      <td>
        <div class="row-actions">
          <button class="btn btn-neutral btn-sm" onclick="openUserForm(${u.id})">编辑</button>
          <button class="btn btn-neutral btn-sm row-more" onclick="toggleRowMenu(event, this)" title="更多操作">更多</button>
        </div>
        <div class="row-menu" hidden>
          <button class="row-menu-item" onclick="resetPassword(${u.id}, '${u.userName}')"><span class="rmi-ico">🔑</span><span class="rmi-txt">重置密码</span></button>
          <button class="row-menu-item" onclick="toggleUserStatus(${u.id})"><span class="rmi-ico">${enabled ? '⛔' : '✅'}</span><span class="rmi-txt">${enabled ? '禁用' : '启用'}</span></button>
          <div class="row-menu-sep"></div>
          <button class="row-menu-item danger" onclick="deleteUser(${u.id}, '${u.userName}')"><span class="rmi-ico">🗑</span><span class="rmi-txt">删除</span></button>
        </div>
      </td>
    </tr>`;
  }).join('');
  wrap.innerHTML = `<table><thead><tr>
    <th>用户名</th><th>姓名</th><th>邮箱</th><th>手机</th><th>状态</th><th>角色</th><th>最近登录</th><th style="width:150px">操作</th>
  </tr></thead><tbody>${rows}</tbody></table>`;
}
