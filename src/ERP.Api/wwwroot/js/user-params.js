/* ============ 用户参数管理模块 ============ */
/* 独立定义 HTML 转义（避免依赖 bill-edit.js 的加载顺序） */
function escapeHtmlUp(s) { return String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])); }

async function renderUserParamModule() {
  CURRENT_LOADER = loadUserParamList;
  CURRENT_PAGE = 1;
  CURRENT_KEYWORD = '';
  document.getElementById('content').innerHTML = `
    <div class="toolbar">
      <div class="toolbar-left">
        <input type="text" id="search-input" placeholder="搜索参数键/参数值..." onkeydown="if(event.key==='Enter')searchUserParamList()">
        <button class="btn btn-neutral" onclick="searchUserParamList()">搜索</button>
      </div>
      <div><button class="btn btn-primary" onclick="openUserParamForm()">+ 新增参数</button></div>
    </div>
    <div class="table-wrap" id="table-wrap"></div>
    <div class="pagination" id="pagination"></div>`;
  loadUserParamList();
}

function searchUserParamList() {
  CURRENT_KEYWORD = document.getElementById('search-input').value.trim();
  CURRENT_PAGE = 1;
  loadUserParamList();
}

async function loadUserParamList() {
  const qs = `page=${CURRENT_PAGE}&pageSize=${PAGE_SIZE}${CURRENT_KEYWORD ? '&keyword=' + encodeURIComponent(CURRENT_KEYWORD) : ''}`;
  const data = await api(`/api/sys/user-parameters?${qs}`);
  renderUserParamTable(data);
  renderPagination(data);
}

function renderUserParamTable(data) {
  closeRowMenu();                       // 重渲染前收起可能残留的行操作菜单
  const wrap = document.getElementById('table-wrap');
  if (!data.items || !data.items.length) { wrap.innerHTML = '<div class="empty">暂无数据</div>'; return; }
  const rows = data.items.map(p => `<tr>
    <td>${p.userName || p.userId}</td>
    <td>${p.paramKey}</td>
    <td>${p.paramValue}</td>
    <td>${fmtDate(p.updatedAt)}</td>
    <td>
      <div class="row-actions">
        <button class="btn btn-neutral btn-sm" onclick="openUserParamForm(${p.id})">编辑</button>
        <button class="btn btn-neutral btn-sm row-more" onclick="toggleRowMenu(event, this)" title="更多操作">更多</button>
      </div>
      <div class="row-menu" hidden>
        <button class="row-menu-item danger" onclick="deleteUserParam(${p.id}, '${escapeHtmlUp(p.paramKey)}')"><span class="rmi-ico">🗑</span><span class="rmi-txt">删除</span></button>
      </div>
    </td>
  </tr>`).join('');
  wrap.innerHTML = `<table><thead><tr>
    <th>用户</th><th>参数键</th><th>参数值</th><th>更新时间</th><th style="width:150px">操作</th>
  </tr></thead><tbody>${rows}</tbody></table>`;
}

async function openUserParamForm(id) {
  const isEdit = id !== undefined;
  const users = await api('/api/sys/users?page=1&pageSize=100000');
  const userOpts = (users.items || []).map(u =>
    `<option value="${u.id}">${u.displayName || u.userName}</option>`).join('');
  const modal = document.getElementById('modal');
  modal.innerHTML = `
    <div class="modal">
      <h3>${isEdit ? '编辑' : '新增'}用户参数</h3>
      <div class="form-grid">
        <div class="form-item"><label>用户 *</label><select id="upf_userId">${userOpts}</select></div>
        <div class="form-item"><label>参数键 *</label><input id="upf_paramKey"></div>
        <div class="form-item full"><label>参数值</label><input id="upf_paramValue"></div>
      </div>
      <div class="modal-footer">
        <button class="btn btn-neutral" onclick="closeModal()">取消</button>
        <button class="btn btn-primary" onclick="saveUserParam(${isEdit ? id : 'null'})">保存</button>
      </div>
    </div>`;
  modal.style.display = 'flex';
  if (isEdit) {
    const p = await api(`/api/sys/user-parameters/${id}`);
    document.getElementById('upf_userId').value = String(p.userId);
    document.getElementById('upf_paramKey').value = p.paramKey || '';
    document.getElementById('upf_paramValue').value = p.paramValue || '';
  }
}

async function saveUserParam(id) {
  const body = {
    userId: Number(document.getElementById('upf_userId').value) || 0,
    paramKey: document.getElementById('upf_paramKey').value.trim(),
    paramValue: document.getElementById('upf_paramValue').value,
  };
  if (!body.userId) { toast('请选择用户', 'error'); return; }
  if (!body.paramKey) { toast('参数键不能为空', 'error'); return; }
  try {
    if (id) await api(`/api/sys/user-parameters/${id}`, 'PUT', body);
    else await api('/api/sys/user-parameters', 'POST', body);
    closeModal(); toast('保存成功'); loadUserParamList();
  } catch (err) { toast(err.message, 'error'); }
}

async function deleteUserParam(id, key) {
  if (!confirm(`确认删除用户参数 [${key}]？`)) return;
  try {
    await api(`/api/sys/user-parameters/${id}`, 'DELETE');
    toast('删除成功'); loadUserParamList();
  } catch (err) { toast(err.message, 'error'); }
}
