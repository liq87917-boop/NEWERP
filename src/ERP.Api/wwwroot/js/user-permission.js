/* ============ 用户权限模块（按用户分配角色） ============ */
/* 独立定义 HTML 转义（避免依赖 bill-edit.js 的加载顺序） */
function escapeHtmlUp(s) { return String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])); }

let PERM_ROLES = [];     // 全部角色缓存
let PERM_CURRENT_USER = null; // 当前选中用户

async function renderUserPermissionModule() {
  CURRENT_LOADER = loadPermissionUserList;
  CURRENT_PAGE = 1;
  CURRENT_KEYWORD = '';
  PERM_ROLES = await api('/api/sys/roles/all');
  document.getElementById('content').innerHTML = `
    <div class="toolbar">
      <div class="toolbar-left">
        <input type="text" id="search-input" placeholder="搜索用户名/姓名..." onkeydown="if(event.key==='Enter')searchPermissionUserList()">
        <button class="btn btn-neutral" onclick="searchPermissionUserList()">搜索</button>
      </div>
    </div>
    <div class="table-wrap" id="table-wrap"></div>
    <div class="pagination" id="pagination"></div>`;
  loadPermissionUserList();
}

function searchPermissionUserList() {
  CURRENT_KEYWORD = document.getElementById('search-input').value.trim();
  CURRENT_PAGE = 1;
  loadPermissionUserList();
}

async function loadPermissionUserList() {
  const qs = `page=${CURRENT_PAGE}&pageSize=${PAGE_SIZE}${CURRENT_KEYWORD ? '&keyword=' + encodeURIComponent(CURRENT_KEYWORD) : ''}`;
  const data = await api(`/api/sys/users?${qs}`);
  renderPermissionUserTable(data);
  renderPagination(data);
}

function renderPermissionUserTable(data) {
  closeRowMenu();                       // 重渲染前收起可能残留的行操作菜单
  const wrap = document.getElementById('table-wrap');
  if (!data.items || !data.items.length) { wrap.innerHTML = '<div class="empty">暂无数据</div>'; return; }
  const rows = data.items.map(u => {
    const roles = (u.roleNames || []).join(', ') || '<span class="text-muted">未分配</span>';
    return `<tr>
      <td>${u.userName}</td>
      <td>${u.displayName || '-'}</td>
      <td>${roles}</td>
      <td>
        <button class="btn btn-primary btn-sm" onclick="openPermissionForm(${u.id}, '${escapeHtmlUp(u.userName)}')">分配角色</button>
      </td>
    </tr>`;
  }).join('');
  wrap.innerHTML = `<table><thead><tr>
    <th>用户名</th><th>姓名</th><th>当前角色</th><th style="width:140px">操作</th>
  </tr></thead><tbody>${rows}</tbody></table>`;
}

async function openPermissionForm(id, userName) {
  const roleCheckboxes = PERM_ROLES.map(r =>
    `<label class="role-check"><input type="checkbox" class="perm-role" value="${r.id}"> ${r.roleName}</label>`).join('');
  const modal = document.getElementById('modal');
  modal.innerHTML = `
    <div class="modal">
      <h3>分配角色 - ${userName}</h3>
      <p class="text-muted" style="margin-bottom:12px">勾选该用户拥有的角色，保存后立即生效</p>
      <div class="role-list" id="perm-role-list">${roleCheckboxes || '<span class="text-muted">暂无角色</span>'}</div>
      <div class="modal-footer">
        <button class="btn btn-neutral" onclick="closeModal()">取消</button>
        <button class="btn btn-primary" onclick="savePermission(${id})">保存</button>
      </div>
    </div>`;
  modal.style.display = 'flex';
  const u = await api(`/api/sys/users/${id}`);
  PERM_CURRENT_USER = u;
  (u.roleIds || []).forEach(rid => {
    const cb = document.querySelector(`#perm-role-list input[value="${rid}"]`);
    if (cb) cb.checked = true;
  });
}

async function savePermission(id) {
  const roleIds = Array.from(document.querySelectorAll('.perm-role:checked')).map(cb => Number(cb.value));
  try {
    await api(`/api/sys/users/${id}`, 'PUT', {
      displayName: PERM_CURRENT_USER.displayName || '',
      email: PERM_CURRENT_USER.email || '',
      phone: PERM_CURRENT_USER.phone || '',
      status: PERM_CURRENT_USER.status,
      roleIds,
    });
    closeModal(); toast('角色分配成功'); loadPermissionUserList();
  } catch (err) { toast(err.message, 'error'); }
}
