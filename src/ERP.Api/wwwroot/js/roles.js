/* ============ 角色管理模块（列表） ============ */
let ROLE_MENUS = []; // 缓存菜单树

function renderRoleModule() {
  CURRENT_LOADER = loadRoleList;
  CURRENT_PAGE = 1;
  CURRENT_KEYWORD = '';
  document.getElementById('content').innerHTML = `
    <div class="toolbar">
      <div class="toolbar-left">
        <input type="text" id="search-input" placeholder="搜索角色名称/编码..." onkeydown="if(event.key==='Enter')searchRoleList()">
        <button class="btn btn-neutral" onclick="searchRoleList()">搜索</button>
      </div>
      <div><button class="btn btn-primary" onclick="openRoleForm()">+ 新增角色</button></div>
    </div>
    <div class="table-wrap" id="table-wrap"></div>
    <div class="pagination" id="pagination"></div>`;
  loadRoleList();
}

function searchRoleList() {
  CURRENT_KEYWORD = document.getElementById('search-input').value.trim();
  CURRENT_PAGE = 1;
  loadRoleList();
}

async function loadRoleList() {
  const qs = `page=${CURRENT_PAGE}&pageSize=${PAGE_SIZE}${CURRENT_KEYWORD ? '&keyword=' + encodeURIComponent(CURRENT_KEYWORD) : ''}`;
  const data = await api(`/api/sys/roles?${qs}`);
  renderRoleTable(data);
  renderPagination(data);
}

function renderRoleTable(data) {
  closeRowMenu();                       // 重渲染前收起可能残留的行操作菜单
  const wrap = document.getElementById('table-wrap');
  if (!data.items || !data.items.length) { wrap.innerHTML = '<div class="empty">暂无数据</div>'; return; }
  const rows = data.items.map(r => {
    const systemTag = r.isSystem ? '<span class="status status-info">系统内置</span>' : '<span class="status status-neutral">自定义</span>';
    return `<tr>
      <td>${r.roleName}</td>
      <td>${r.roleCode}</td>
      <td>${r.description || '-'}</td>
      <td>${systemTag}</td>
      <td>
        <div class="row-actions">
          <button class="btn btn-neutral btn-sm" onclick="openRoleForm(${r.id})">编辑</button>
          <button class="btn btn-neutral btn-sm row-more" onclick="toggleRowMenu(event, this)" title="更多操作">更多</button>
        </div>
        <div class="row-menu" hidden>
          <button class="row-menu-item" onclick="assignMenus(${r.id}, '${r.roleName}')"><span class="rmi-ico">🔑</span><span class="rmi-txt">分配权限</span></button>
          <div class="row-menu-sep"></div>
          <button class="row-menu-item danger" onclick="deleteRole(${r.id}, '${r.roleName}', ${r.isSystem})"><span class="rmi-ico">🗑</span><span class="rmi-txt">删除</span></button>
        </div>
      </td>
    </tr>`;
  }).join('');
  wrap.innerHTML = `<table><thead><tr>
    <th>角色名称</th><th>角色编码</th><th>描述</th><th>类型</th><th style="width:150px">操作</th>
  </tr></thead><tbody>${rows}</tbody></table>`;
}

async function deleteRole(id, name, isSystem) {
  if (isSystem) { toast('系统内置角色不可删除', 'error'); return; }
  if (!confirm(`确认删除角色 [${name}]？`)) return;
  try {
    await api(`/api/sys/roles/${id}`, 'DELETE');
    toast('删除成功'); loadRoleList();
  } catch (err) { toast(err.message, 'error'); }
}
