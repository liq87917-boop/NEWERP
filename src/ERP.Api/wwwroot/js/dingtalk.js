/* ============================================================
   ============ 钉钉通知：配置 + 发送记录 =====================
   ============================================================
   菜单入口：「系统设置 → 钉钉通知配置」「系统设置 → 钉钉发送记录」
   后端：/api/sys/dingtalk/config、/test、/logs、/logs/{id}/resend
   配置存放于系统参数（键 DingTalk_*）；业务单据在保存/审核/销审/作废/还原/删除时触发推送
   ============================================================ */

/* 可配置的触发动作 */
const DT_ACTION_OPTIONS = [
  { code: 'save', label: '新增/保存' },
  { code: 'audit', label: '审核' },
  { code: 'unaudit', label: '销审' },
  { code: 'void', label: '作废' },
  { code: 'restore', label: '还原' },
  { code: 'delete', label: '删除' },
];

let DT_CONFIG = null;

/* ============ 配置页 ============ */
async function renderDingTalkConfigModule() {
  CURRENT_LOADER = null;
  const content = document.getElementById('content');
  content.innerHTML = '<div class="card"><div class="skeleton-line w-30"></div><div class="skeleton-line w-90"></div><div class="skeleton-line w-70"></div></div>';
  try {
    DT_CONFIG = await api('/api/sys/dingtalk/config') || {};
  } catch (e) { DT_CONFIG = {}; toast('读取配置失败：' + e.message, 'error'); }
  const c = k => DT_CONFIG[k] || '';
  const enabled = (c('DingTalk_Enabled') || 'false') === 'true';
  const msgType = c('DingTalk_MsgType') || 'markdown';

  content.innerHTML = `
    <div class="dt-layout">
      <div class="card dt-card">
        <div class="card-title">🔔 钉钉机器人配置</div>
        <div class="dt-grid2">
          <div class="pd-field">
            <label>启用通知</label>
            <select id="dt-enabled">
              <option value="true" ${enabled ? 'selected' : ''}>启用（单据变化时推送）</option>
              <option value="false" ${enabled ? '' : 'selected'}>停用</option>
            </select>
            <div class="pd-hint">停用后所有推送静默跳过，不影响业务操作</div>
          </div>
          <div class="pd-field">
            <label>消息类型</label>
            <select id="dt-msgtype">
              <option value="markdown" ${msgType === 'markdown' ? 'selected' : ''}>Markdown（推荐）</option>
              <option value="text" ${msgType === 'text' ? 'selected' : ''}>纯文本 text</option>
            </select>
          </div>
        </div>
        <div class="pd-field">
          <label>Webhook 地址 *</label>
          <input type="text" id="dt-webhook" value="${escapeHtml(c('DingTalk_Webhook'))}" placeholder="https://oapi.dingtalk.com/robot/send?access_token=xxxx">
          <div class="pd-hint">钉钉群 → 群设置 → 智能群助手 → 添加机器人 → 自定义 → 复制 Webhook</div>
        </div>
        <div class="pd-field">
          <label>加签密钥 Secret（可选）</label>
          <input type="text" id="dt-secret" value="${escapeHtml(c('DingTalk_Secret'))}" placeholder="SECxxxxxxxxxxxx">
          <div class="pd-hint">安全设置选「加签」时必填；选「自定义关键词」时留空，并确保消息含该关键词</div>
        </div>
        <div class="dt-grid2">
          <div class="pd-field">
            <label>@ 手机号（可选）</label>
            <input type="text" id="dt-atmobiles" value="${escapeHtml(c('DingTalk_AtMobiles'))}" placeholder="13900000000,13800000000">
            <div class="pd-hint">多个用英文逗号分隔</div>
          </div>
          <div class="pd-field">
            <label>@ 所有人</label>
            <select id="dt-atall">
              <option value="false" ${c('DingTalk_AtAll') === 'true' ? '' : 'selected'}>否</option>
              <option value="true" ${c('DingTalk_AtAll') === 'true' ? 'selected' : ''}>是（谨慎使用）</option>
            </select>
          </div>
        </div>
        <div class="dt-actions">
          <button class="btn btn-primary" onclick="saveDingTalkConfig()">💾 保存配置</button>
          <button class="btn btn-neutral" onclick="testDingTalk()">📨 发送测试消息</button>
          <button class="btn btn-neutral" onclick="pushFollowUpReminder()" title="把已到期 / 逾期的客户跟进汇总推送到钉钉（不受每日定时限制）；每日定时推送在「系统参数」中用 FollowUpReminder_Enabled / FollowUpReminder_Time 配置">📣 立即推送跟进提醒</button>
          <button class="btn btn-neutral" onclick="renderDingTalkConfigModule()">↺ 重新载入</button>
        </div>
      </div>
      <div id="dt-rule-host"></div>
      <div id="dt-tpl-host"></div>
    </div>`;
  renderDingTalkRules(c('DingTalk_Rules'));
  renderDingTalkTemplate(c('DingTalk_Template'));
}

/* ============ 触发规则矩阵 ============ */
function renderDingTalkRules(rulesJson) {
  const host = document.getElementById('dt-rule-host');
  if (!host) return;
  const billCodes = Object.keys(BILL_CONFIG || {});
  const rows = billCodes.map(code => {
    const name = (BILL_CONFIG[code] || {}).title || code;
    return `<tr class="dt-rule-row" data-code="${code}">
        <td>${escapeHtml(name)}<div class="dt-code">${escapeHtml(code)}</div></td>
        ${DT_ACTION_OPTIONS.map(a => `<td class="text-center"><input type="checkbox" class="dt-act" value="${a.code}"></td>`).join('')}
      </tr>`;
  }).join('');
  host.innerHTML = `
    <div class="card dt-card">
      <div class="card-title">⚙️ 触发规则 <span class="card-title-tip">勾选「哪些单据的哪些动作」需要推送</span></div>
      <div class="dt-rule-actions">
        <button class="btn btn-neutral btn-sm" onclick="dtPresetAudit()">常用：仅 审核+作废</button>
        <button class="btn btn-neutral btn-sm" onclick="dtPresetAllBillAudit()">全部单据：仅审核</button>
        <button class="btn btn-neutral btn-sm" onclick="dtClearAll()">全部清空</button>
      </div>
      <div class="table-wrap dt-rule-wrap">
        <table>
          <thead><tr>
            <th>单据类型</th>
            ${DT_ACTION_OPTIONS.map(a => `<th class="text-center" style="width:80px">${a.label}</th>`).join('')}
          </tr></thead>
          <tbody>
            ${rows}
            <tr class="dt-rule-row dt-rule-wild" data-code="*">
              <td><b>全部单据（通配）</b><div class="dt-code">未单独配置的单据按此行生效</div></td>
              ${DT_ACTION_OPTIONS.map(a => `<td class="text-center"><input type="checkbox" class="dt-act" value="${a.code}"></td>`).join('')}
            </tr>
          </tbody>
        </table>
      </div>
      <div class="pd-hint">全部未勾选 = 所有单据所有动作都推送；勾选后仅推送勾选项（未单独配置的单据沿用「全部单据（通配）」）。</div>
    </div>`;
  applyDingTalkRules(rulesJson);
}

/* 规则 JSON → 复选框 */
function applyDingTalkRules(rulesJson) {
  let rules = {};
  try { rules = JSON.parse(rulesJson || '{}') || {}; } catch (e) { rules = {}; }
  const wild = rules['*'] || [];
  document.querySelectorAll('.dt-rule-row').forEach(row => {
    const code = row.dataset.code;
    const acts = rules[code] !== undefined ? rules[code] : (code === '*' ? [] : wild);
    row.querySelectorAll('input.dt-act').forEach(cb => { cb.checked = Array.isArray(acts) && acts.includes(cb.value); });
  });
}

/* 复选框 → 规则 JSON */
function buildRulesFromMatrix() {
  const rules = {};
  document.querySelectorAll('.dt-rule-row').forEach(row => {
    const code = row.dataset.code;
    const acts = Array.from(row.querySelectorAll('input.dt-act:checked')).map(cb => cb.value);
    if (acts.length) rules[code] = acts;
  });
  return JSON.stringify(rules);
}

function dtSetAll(checked) {
  document.querySelectorAll('.dt-rule-row input.dt-act').forEach(cb => { cb.checked = checked; });
}
function dtClearAll() { dtSetAll(false); toast('已清空全部规则'); }

/* 常用预设：所有单据仅「审核 + 作废」 */
function dtPresetAudit() {
  document.querySelectorAll('.dt-rule-row').forEach(row => {
    row.querySelectorAll('input.dt-act').forEach(cb => {
      cb.checked = (cb.value === 'audit' || cb.value === 'void');
    });
  });
  toast('已套用：所有单据 仅审核 + 作废');
}

/* 常用预设：全部单据（通配）仅审核 */
function dtPresetAllBillAudit() {
  dtSetAll(false);
  const wild = document.querySelector('.dt-rule-row[data-code="*"]');
  if (wild) {
    const cb = wild.querySelector('input.dt-act[value="audit"]');
    if (cb) cb.checked = true;
  }
  toast('已套用：全部单据 仅审核（通配）');
}

/* ============ 消息模板 ============ */
const DT_DEFAULT_TEMPLATE = '### 🔔 单据状态变更提醒\n- **单据类型**：{billType}\n- **单据号**：{billNo}\n- **操作动作**：{action}\n- **操作人**：{user}\n- **操作时间**：{time}';

function renderDingTalkTemplate(template) {
  const host = document.getElementById('dt-tpl-host');
  if (!host) return;
  host.innerHTML = `
    <div class="card dt-card">
      <div class="card-title">📝 消息模板 <span class="card-title-tip">支持占位符替换</span></div>
      <div class="dt-tpl-grid">
        <div class="pd-field">
          <label>模板内容（Markdown / 纯文本）</label>
          <textarea id="dt-template" class="dt-textarea">${escapeHtml(template || DT_DEFAULT_TEMPLATE)}</textarea>
          <div class="dt-actions" style="margin-top:10px">
            <button class="btn btn-neutral btn-sm" onclick="dtResetTemplate()">恢复默认模板</button>
          </div>
        </div>
        <div>
          <div class="pd-field">
            <label>可用占位符</label>
            <div class="dt-vars">
              <div><code>{billType}</code> 单据类型（中文名）</div>
              <div><code>{billNo}</code> 单据号</div>
              <div><code>{action}</code> 操作动作（审核 / 作废…）</div>
              <div><code>{user}</code> 操作人</div>
              <div><code>{time}</code> 操作时间</div>
              <div><code>{code}</code> 单据编码（sales-order…）</div>
            </div>
          </div>
          <div class="pd-field">
            <label>消息预览（示例数据）</label>
            <pre class="dt-preview" id="dt-preview"></pre>
          </div>
        </div>
      </div>
    </div>`;
  const tpl = document.getElementById('dt-template');
  if (tpl) tpl.addEventListener('input', updateDtPreview);
  updateDtPreview();
}

function updateDtPreview() {
  const el = document.getElementById('dt-template');
  const pre = document.getElementById('dt-preview');
  if (!el || !pre) return;
  const now = new Date();
  const pad = n => String(n).padStart(2, '0');
  const time = now.getFullYear() + '-' + pad(now.getMonth() + 1) + '-' + pad(now.getDate())
    + ' ' + pad(now.getHours()) + ':' + pad(now.getMinutes()) + ':' + pad(now.getSeconds());
  pre.textContent = (el.value || '')
    .replace(/\{billType\}/g, '销售订单')
    .replace(/\{billNo\}/g, 'SO202609180001')
    .replace(/\{action\}/g, '审核')
    .replace(/\{user\}/g, (PROFILE && (PROFILE.displayName || PROFILE.userName)) || '系统管理员')
    .replace(/\{time\}/g, time)
    .replace(/\{code\}/g, 'sales-order');
}

function dtResetTemplate() {
  const el = document.getElementById('dt-template');
  if (el) { el.value = DT_DEFAULT_TEMPLATE; updateDtPreview(); toast('已恢复默认模板'); }
}

/* ============ 保存 / 测试发送 ============ */
function collectDingTalkConfig() {
  const val = id => { const el = document.getElementById(id); return el ? el.value : ''; };
  const cfg = {};
  cfg.DingTalk_Enabled = val('dt-enabled') || 'false';
  cfg.DingTalk_Webhook = (val('dt-webhook') || '').trim();
  cfg.DingTalk_Secret = (val('dt-secret') || '').trim();
  cfg.DingTalk_MsgType = val('dt-msgtype') || 'markdown';
  cfg.DingTalk_AtMobiles = (val('dt-atmobiles') || '').trim();
  cfg.DingTalk_AtAll = val('dt-atall') || 'false';
  cfg.DingTalk_Rules = buildRulesFromMatrix();
  cfg.DingTalk_Template = val('dt-template') || '';
  return cfg;
}

async function saveDingTalkConfig() {
  const cfg = collectDingTalkConfig();
  if (cfg.DingTalk_Enabled === 'true' && !cfg.DingTalk_Webhook) {
    toast('启用通知前请先填写 Webhook 地址', 'error'); return;
  }
  try {
    await api('/api/sys/dingtalk/config', 'POST', cfg);
    DT_CONFIG = Object.assign({}, DT_CONFIG, cfg);
    toast('配置已保存');
  } catch (e) { toast(e.message, 'error'); }
}

async function testDingTalk() {
  const webhook = (document.getElementById('dt-webhook') || {}).value || '';
  if (!webhook) { toast('请先填写 Webhook 地址', 'error'); return; }
  const btn = (event && event.target) || null;
  try {
    if (btn) { btn.disabled = true; btn.textContent = '发送中…'; }
    await api('/api/sys/dingtalk/test', 'POST', { webhook });
    toast('测试消息已发送，请查看钉钉群');
  } catch (e) { toast('发送失败：' + e.message, 'error'); }
  finally { if (btn) { btn.disabled = false; btn.textContent = '📨 发送测试消息'; } }
}

/* 手动推送一次「客户跟进提醒」（已到期 / 逾期的客户跟进汇总）；每日定时推送由系统参数控制 */
async function pushFollowUpReminder() {
  const btn = (event && event.target) || null;
  try {
    if (btn) { btn.disabled = true; btn.textContent = '推送中…'; }
    await api('/api/sys/dingtalk/follow-up-reminder', 'POST');
    toast('已推送跟进提醒，请查看钉钉群');
  } catch (e) { toast('推送失败：' + e.message, 'error'); }
  finally { if (btn) { btn.disabled = false; btn.textContent = '📣 立即推送跟进提醒'; } }
}

/* ============ 发送记录页 ============ */
let DT_LOG_FILTER = { success: '', actionCode: '' };

async function renderDingTalkLogModule() {
  CURRENT_LOADER = loadDingTalkLogs;
  CURRENT_PAGE = 1;
  CURRENT_KEYWORD = '';
  DT_LOG_FILTER = { success: '', actionCode: '' };
  document.getElementById('content').innerHTML = `
    <div class="toolbar">
      <div class="toolbar-left">
        <input type="text" id="dtl-keyword" placeholder="搜索单据号 / 操作人 / 内容..." onkeydown="if(event.key==='Enter')searchDingTalkLogs()">
        <select id="dtl-success" onchange="searchDingTalkLogs()">
          <option value="">全部状态</option>
          <option value="true">仅成功</option>
          <option value="false">仅失败</option>
        </select>
        <select id="dtl-action" onchange="searchDingTalkLogs()">
          <option value="">全部动作</option>
          ${DT_ACTION_OPTIONS.map(a => `<option value="${a.code}">${a.label}</option>`).join('')}
        </select>
        <button class="btn btn-neutral" onclick="searchDingTalkLogs()">搜索</button>
      </div>
      <div class="toolbar-actions">
        <button class="btn btn-neutral" onclick="loadDingTalkLogs()">🔄 刷新</button>
        <button class="btn btn-neutral" onclick="renderDingTalkConfigModule()">🔔 通知配置</button>
      </div>
    </div>
    <div class="table-wrap" id="table-wrap"></div>
    <div class="pagination" id="pagination"></div>`;
  loadDingTalkLogs();
}

function searchDingTalkLogs() {
  CURRENT_KEYWORD = (document.getElementById('dtl-keyword').value || '').trim();
  DT_LOG_FILTER.success = document.getElementById('dtl-success').value;
  DT_LOG_FILTER.actionCode = document.getElementById('dtl-action').value;
  CURRENT_PAGE = 1;
  loadDingTalkLogs();
}

async function loadDingTalkLogs() {
  let qs = `page=${CURRENT_PAGE}&pageSize=${PAGE_SIZE}`;
  if (CURRENT_KEYWORD) qs += '&keyword=' + encodeURIComponent(CURRENT_KEYWORD);
  if (DT_LOG_FILTER.success) qs += '&success=' + DT_LOG_FILTER.success;
  if (DT_LOG_FILTER.actionCode) qs += '&actionCode=' + DT_LOG_FILTER.actionCode;
  const data = await api(`/api/sys/dingtalk/logs?${qs}`);
  renderDingTalkLogTable(data);
  renderPagination(data);
}

function renderDingTalkLogTable(data) {
  closeRowMenu();
  const wrap = document.getElementById('table-wrap');
  if (!wrap) return;
  const items = data.items || [];
  window.__dtLogs = items;                 // 供「查看详情」使用
  if (!items.length) { wrap.innerHTML = '<div class="empty">暂无发送记录</div>'; return; }
  const rows = items.map(l => {
    const time = l.sentAt ? String(l.sentAt).replace('T', ' ').slice(0, 19)
      : (l.createdAt ? String(l.createdAt).replace('T', ' ').slice(0, 19) : '');
    const status = l.success
      ? '<span class="status status-success">成功</span>'
      : '<span class="status status-danger">失败</span>';
    return `<tr>
      <td>${escapeHtml(time)}</td>
      <td>${escapeHtml(l.billTypeName || l.billType || '')}</td>
      <td>${escapeHtml(l.billNo || '-')}</td>
      <td>${escapeHtml(l.actionName || l.actionCode || '')}</td>
      <td>${escapeHtml(l.operator || '-')}</td>
      <td>${status}</td>
      <td class="text-center">${l.retryCount || 0}</td>
      <td class="dt-err">${escapeHtml(l.errorMessage || '')}</td>
      <td>
        <div class="row-actions">
          <button class="btn btn-neutral btn-sm" onclick="viewDingTalkLog(${l.id})">查看</button>
          <button class="btn btn-neutral btn-sm row-more" onclick="toggleRowMenu(event, this)" title="更多操作">更多</button>
        </div>
        <div class="row-menu" hidden>
          <button class="row-menu-item" onclick="resendDingTalkLog(${l.id})"><span class="rmi-ico">📨</span><span class="rmi-txt">重新发送</span></button>
          <div class="row-menu-sep"></div>
          <button class="row-menu-item danger" onclick="deleteDingTalkLog(${l.id})"><span class="rmi-ico">🗑</span><span class="rmi-txt">删除记录</span></button>
        </div>
      </td>
    </tr>`;
  }).join('');
  wrap.innerHTML = `<table><thead><tr>
      <th style="width:158px">发送时间</th><th style="width:110px">单据类型</th><th style="width:150px">单据号</th>
      <th style="width:76px">动作</th><th style="width:96px">操作人</th><th style="width:76px">状态</th>
      <th style="width:56px" class="text-center">重试</th><th>失败原因</th><th style="width:150px">操作</th>
    </tr></thead><tbody>${rows}</tbody></table>`;
}

/* 查看发送详情（含消息正文） */
function viewDingTalkLog(id) {
  const item = (window.__dtLogs || []).find(x => x.id === id);
  if (!item) { toast('未找到该记录，请刷新后重试', 'error'); return; }
  const time = item.sentAt ? String(item.sentAt).replace('T', ' ').slice(0, 19)
    : (item.createdAt ? String(item.createdAt).replace('T', ' ').slice(0, 19) : '');
  document.getElementById('modal').innerHTML = `<div class="modal" style="width:720px">
    <h3>📨 发送详情</h3>
    <div class="dt-detail">
      <div><b>单据</b>：${escapeHtml(item.billTypeName || '')} ${escapeHtml(item.billNo || '')}</div>
      <div><b>动作</b>：${escapeHtml(item.actionName || '')}　<b>操作人</b>：${escapeHtml(item.operator || '')}</div>
      <div><b>发送时间</b>：${escapeHtml(time)}　<b>状态</b>：${item.success ? '<span class="status status-success">成功</span>' : '<span class="status status-danger">失败</span>'}</div>
      <div><b>消息类型</b>：${escapeHtml(item.msgType || '')}　<b>重试次数</b>：${item.retryCount || 0}</div>
      <div><b>Webhook</b>：${escapeHtml(item.webhook || '')}</div>
      ${item.errorMessage ? `<div class="dt-err-line"><b>失败原因</b>：${escapeHtml(item.errorMessage)}</div>` : ''}
    </div>
    <div class="pd-field"><label>消息内容</label><pre class="dt-preview">${escapeHtml(item.content || '')}</pre></div>
    <div class="modal-footer">
      <button class="btn btn-neutral" onclick="closeModal()">关闭</button>
      <button class="btn btn-primary" onclick="resendDingTalkLog(${item.id}, true)">📨 重新发送</button>
    </div>
  </div>`;
  document.getElementById('modal').style.display = 'flex';
}

/* 重新发送（closeAfter = 发送后关闭详情弹窗） */
async function resendDingTalkLog(id, closeAfter) {
  try {
    await api(`/api/sys/dingtalk/logs/${id}/resend`, 'POST');
    toast('重发成功');
    if (closeAfter) closeModal();
    if (CURRENT_LOADER) CURRENT_LOADER();
  } catch (e) { toast('重发失败：' + e.message, 'error'); }
}

async function deleteDingTalkLog(id) {
  if (!confirm('确认删除该发送记录？')) return;
  try {
    await api(`/api/sys/dingtalk/logs/${id}`, 'DELETE');
    toast('已删除');
    if (CURRENT_LOADER) CURRENT_LOADER();
  } catch (e) { toast(e.message, 'error'); }
}




