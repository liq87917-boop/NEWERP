/* ============ 角色管理模块（表单与菜单权限） ============ */
async function loadMenuTree() {
  if (ROLE_MENUS.length) return ROLE_MENUS;
  ROLE_MENUS = await api('/api/sys/menus/tree');
  return ROLE_MENUS;
}

function renderMenuTreeNodes(menus, checkedIds) {
  return menus.map(m => {
    const checked = checkedIds.includes(m.id) ? 'checked' : '';
    const childrenHtml = (m.children && m.children.length)
      ? `<div class="menu-tree-children">${renderMenuTreeNodes(m.children, checkedIds)}</div>` : '';
    return `<div class="menu-tree-node">
      <label class="menu-check-label"><input type="checkbox" class="menu-check" value="${m.id}" ${checked} onchange="onMenuCheck(this)"> ${m.menuName}</label>
      ${childrenHtml}
    </div>`;
  }).join('');
}

function onMenuCheck(cb) {
  // 勾选/取消父节点时联动其所有后代节点
  const node = cb.closest('.menu-tree-node');
  node.querySelectorAll(':scope > .menu-tree-children .menu-check').forEach(c => { c.checked = cb.checked; });
}

function getCheckedMenuIds() {
  return Array.from(document.querySelectorAll('.menu-check:checked')).map(cb => Number(cb.value));
}

async function openRoleForm(id) {
  const isEdit = id !== undefined;
  const menus = await loadMenuTree();
  let checkedIds = [];
  if (isEdit) checkedIds = await api(`/api/sys/roles/${id}/menus`);
  const menuTreeHtml = renderMenuTreeNodes(menus, checkedIds);
  const modal = document.getElementById('modal');
  modal.innerHTML = `
    <div class="modal">
      <h3>${isEdit ? '编辑角色' : '新增角色'}</h3>
      <div class="form-grid">
        <div class="form-item"><label>角色名称 *</label><input id="rf_roleName"></div>
        <div class="form-item"><label>角色编码 *</label><input id="rf_roleCode" ${isEdit ? 'disabled' : ''}></div>
        <div class="form-item full"><label>描述</label><input id="rf_description"></div>
        <div class="form-item full"><label>菜单权限</label><div class="menu-tree" id="menu-tree">${menuTreeHtml}</div></div>
      </div>
      <div class="modal-footer">
        <button class="btn btn-neutral" onclick="closeModal()">取消</button>
        <button class="btn btn-primary" onclick="saveRole(${isEdit ? id : 'null'})">保存</button>
      </div>
    </div>`;
  modal.style.display = 'flex';
  if (isEdit) {
    const r = await api(`/api/sys/roles/${id}`);
    document.getElementById('rf_roleName').value = r.roleName || '';
    document.getElementById('rf_roleCode').value = r.roleCode || '';
    document.getElementById('rf_description').value = r.description || '';
  }
}

function assignMenus(id, name) {
  openRoleForm(id); // 分配权限与编辑共用同一表单
}

async function saveRole(id) {
  try {
    const body = {
      roleName: document.getElementById('rf_roleName').value.trim(),
      roleCode: document.getElementById('rf_roleCode').value.trim(),
      description: document.getElementById('rf_description').value,
      menuIds: getCheckedMenuIds(),
    };
    if (!body.roleName) { toast('角色名称不能为空', 'error'); return; }
    if (!body.roleCode) { toast('角色编码不能为空', 'error'); return; }
    if (id) await api(`/api/sys/roles/${id}`, 'PUT', body);
    else await api('/api/sys/roles', 'POST', body);
    closeModal(); toast('保存成功'); loadRoleList();
  } catch (err) { toast(err.message, 'error'); }
}
