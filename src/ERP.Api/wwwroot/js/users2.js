/* ============ 用户管理模块（表单与操作） ============ */
async function openUserForm(id) {
  const isEdit = id !== undefined;
  const roles = await loadRoles();
  const roleCheckboxes = roles.map(r => `<label class="role-check"><input type="checkbox" value="${r.id}"> ${r.roleName}</label>`).join('');
  const modal = document.getElementById('modal');

  if (isEdit) {
    modal.innerHTML = `
      <div class="modal">
        <h3>编辑用户</h3>
        <div class="form-grid">
          <div class="form-item"><label>姓名</label><input id="uf_displayName"></div>
          <div class="form-item"><label>邮箱</label><input id="uf_email"></div>
          <div class="form-item"><label>手机</label><input id="uf_phone"></div>
          <div class="form-item"><label>状态</label><select id="uf_status"><option value="Enabled">启用</option><option value="Disabled">禁用</option></select></div>
          <div class="form-item full"><label>角色</label><div class="role-list" id="uf_roles">${roleCheckboxes}</div></div>
        </div>
        <div class="modal-footer">
          <button class="btn btn-neutral" onclick="closeModal()">取消</button>
          <button class="btn btn-primary" onclick="saveUser(${id})">保存</button>
        </div>
      </div>`;
    modal.style.display = 'flex';
    const u = await api(`/api/sys/users/${id}`);
    document.getElementById('uf_displayName').value = u.displayName || '';
    document.getElementById('uf_email').value = u.email || '';
    document.getElementById('uf_phone').value = u.phone || '';
    document.getElementById('uf_status').value = (u.status === 'Enabled' || u.status === 1) ? 'Enabled' : 'Disabled';
    (u.roleIds || []).forEach(rid => {
      const cb = document.querySelector(`#uf_roles input[value="${rid}"]`);
      if (cb) cb.checked = true;
    });
  } else {
    modal.innerHTML = `
      <div class="modal">
        <h3>新增用户</h3>
        <div class="form-grid">
          <div class="form-item"><label>用户名 *</label><input id="uf_userName"></div>
          <div class="form-item"><label>初始密码 *</label><input type="password" id="uf_password" placeholder="至少6位"></div>
          <div class="form-item"><label>姓名</label><input id="uf_displayName"></div>
          <div class="form-item"><label>邮箱</label><input id="uf_email"></div>
          <div class="form-item"><label>手机</label><input id="uf_phone"></div>
          <div class="form-item full"><label>角色</label><div class="role-list" id="uf_roles">${roleCheckboxes}</div></div>
        </div>
        <div class="modal-footer">
          <button class="btn btn-neutral" onclick="closeModal()">取消</button>
          <button class="btn btn-primary" onclick="saveUser(null)">保存</button>
        </div>
      </div>`;
    modal.style.display = 'flex';
  }
}

function getCheckedRoles() {
  return Array.from(document.querySelectorAll('#uf_roles input:checked')).map(cb => Number(cb.value));
}

async function saveUser(id) {
  try {
    if (id) {
      await api(`/api/sys/users/${id}`, 'PUT', {
        displayName: document.getElementById('uf_displayName').value,
        email: document.getElementById('uf_email').value,
        phone: document.getElementById('uf_phone').value,
        status: document.getElementById('uf_status').value,
        roleIds: getCheckedRoles(),
      });
    } else {
      const userName = document.getElementById('uf_userName').value.trim();
      const password = document.getElementById('uf_password').value;
      if (!userName) { toast('用户名不能为空', 'error'); return; }
      if (password.length < 6) { toast('密码长度不能少于6位', 'error'); return; }
      await api('/api/sys/users', 'POST', {
        userName, password,
        displayName: document.getElementById('uf_displayName').value,
        email: document.getElementById('uf_email').value,
        phone: document.getElementById('uf_phone').value,
        roleIds: getCheckedRoles(),
      });
    }
    closeModal(); toast('保存成功'); loadUserList();
  } catch (err) { toast(err.message, 'error'); }
}

async function resetPassword(id, userName) {
  const pwd = prompt(`为用户 [${userName}] 输入新密码（至少6位）：`);
  if (!pwd) return;
  if (pwd.length < 6) { toast('密码长度不能少于6位', 'error'); return; }
  try {
    await api(`/api/sys/users/${id}/reset-password`, 'POST', { newPassword: pwd });
    toast('密码重置成功');
  } catch (err) { toast(err.message, 'error'); }
}

async function toggleUserStatus(id) {
  try {
    await api(`/api/sys/users/${id}/toggle-status`, 'POST');
    toast('状态已切换');
    loadUserList();
  } catch (err) { toast(err.message, 'error'); }
}

async function deleteUser(id, userName) {
  if (!confirm(`确认删除用户 [${userName}]？`)) return;
  try {
    await api(`/api/sys/users/${id}`, 'DELETE');
    toast('删除成功'); loadUserList();
  } catch (err) { toast(err.message, 'error'); }
}
