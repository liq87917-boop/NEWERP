/* ============ 全局状态 ============ */
let TOKEN = localStorage.getItem('erp_token') || '';
let PROFILE = null;
let CURRENT_MODULE = null;
let CURRENT_MODULE_CODE = '';   // 当前基础资料/模块的菜单编码（用于打印模板与打印设计）
let CURRENT_PAGE = 1;
let CURRENT_KEYWORD = '';
let PAGE_SIZE = 100;        // 每页显示行数（默认 100）
let CURRENT_LOADER = null;  // 当前列表重新加载函数
let CURRENT_PAGE_CODE = ''; // 当前页面编码（顶部标签高亮依据；'home' = 首页门户）

/* ============ 工具函数 ============ */
async function api(path, method = 'GET', body = null) {
  const headers = { 'Content-Type': 'application/json' };
  if (TOKEN) headers['Authorization'] = 'Bearer ' + TOKEN;
  const opts = { method, headers };
  if (body) opts.body = JSON.stringify(body);
  const resp = await fetch(path, opts);
  const data = await resp.json();
  if (data.code === 2000 || data.code === 2003) { logout(); throw new Error('登录已过期'); }
  if (data.code !== 0) throw new Error(data.message || '操作失败');
  return data.data;
}

/* multipart/form-data 上传（图片等文件） */
async function uploadFile(path, formData) {
  const headers = {};
  if (TOKEN) headers['Authorization'] = 'Bearer ' + TOKEN;
  const resp = await fetch(path, { method: 'POST', headers, body: formData });
  const data = await resp.json();
  if (data.code === 2000 || data.code === 2003) { logout(); throw new Error('登录已过期'); }
  if (data.code !== 0) throw new Error(data.message || '操作失败');
  return data.data;
}

function toast(msg, type = 'success') {
  const el = document.getElementById('toast');
  el.textContent = msg;
  el.className = 'toast ' + type;
  el.style.display = 'block';
  setTimeout(() => { el.style.display = 'none'; }, 3000);
}

function fmtDate(v) { return v ? String(v).slice(0, 10) : ''; }
function fmtMoney(v) { return v === null || v === undefined ? '' : Number(v).toFixed(2); }

const STATUS_MAP = {
  Pending: ['待提交', 'status-info'], Submitted: ['已提交', 'status-warning'],
  Approved: ['已审核', 'status-success'], Rejected: ['已驳回', 'status-danger'],
  Completed: ['已完成', 'status-success'], Cancelled: ['已取消', 'status-neutral'],
  0: ['待提交', 'status-info'], 1: ['已提交', 'status-warning'],
  2: ['已审核', 'status-success'], 3: ['已驳回', 'status-danger'],
  4: ['已完成', 'status-success'], 5: ['已取消', 'status-neutral'],
};
function statusHtml(v) {
  const s = STATUS_MAP[v];
  return s ? `<span class="status ${s[1]}">${s[0]}</span>` : (v ?? '');
}

/* ============ 登录 ============ */
document.getElementById('login-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  const btn = document.getElementById('login-btn');
  btn.disabled = true;
  document.getElementById('login-error').textContent = '';
  try {
    const data = await api('/api/auth/login', 'POST', {
      userName: document.getElementById('login-username').value,
      password: document.getElementById('login-password').value,
    });
    TOKEN = data.token;
    localStorage.setItem('erp_token', TOKEN);
    await initApp();
  } catch (err) {
    document.getElementById('login-error').textContent = err.message;
  } finally { btn.disabled = false; }
});

document.getElementById('logout-btn').addEventListener('click', logout);
function logout() {
  TOKEN = ''; PROFILE = null;
  localStorage.removeItem('erp_token');
  openTabs = [];
  document.getElementById('tabs-bar').innerHTML = '';
  document.getElementById('login-page').style.display = 'flex';
  document.getElementById('app-page').style.display = 'none';
}

/* 点击左上角公司 LOGO：返回首页门户（键盘 Enter / 空格 同样生效） */
document.getElementById('sidebar-logo').addEventListener('click', goHome);
document.getElementById('sidebar-logo').addEventListener('keydown', e => {
  if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); goHome(); }
});

/* 侧边栏折叠/展开 */
document.getElementById('collapse-btn').addEventListener('click', () => {
  document.body.classList.toggle('sidebar-collapsed');
  const collapsed = document.body.classList.contains('sidebar-collapsed');
  document.getElementById('collapse-btn').title = collapsed ? '展开菜单' : '收起菜单';
});

/* ============ 初始化 ============ */
async function initApp() {
  try {
    PROFILE = await api('/api/auth/profile');
    document.getElementById('login-page').style.display = 'none';
    document.getElementById('app-page').style.display = 'flex';
    document.getElementById('header-user').textContent = PROFILE.displayName || PROFILE.userName;
    console.log('[initApp] PROFILE.menus:', (PROFILE.menus || []).length);
    renderMenu(PROFILE.menus || []);
    goHome();               // 初始进入首页门户（同时登记「首页」标签）
    loadLogo();
  } catch (err) {
    console.error('[initApp] 失败：', err);
    // 显示错误并提供重试入口
    document.getElementById('content').innerHTML = `
      <div class="card" style="text-align:center;padding:48px;margin-top:48px">
        <div style="font-size:48px;margin-bottom:16px">⚠️</div>
        <h3 style="margin-bottom:8px">应用初始化失败</h3>
        <p class="text-muted" style="margin-bottom:16px">${err.message || err}</p>
        <button class="btn btn-primary" onclick="location.reload()">🔄 重新加载</button>
        <button class="btn btn-neutral" onclick="logout()" style="margin-left:8px">🚪 重新登录</button>
      </div>`;
  }
}

/* 从系统参数读取公司名称与 Logo（可自定义） */
async function loadLogo() {
  try {
    const data = await api('/api/sys/parameters?page=1&pageSize=100');
    const params = data.items || [];
    const map = {};
    params.forEach(p => { map[p.paramKey] = p.paramValue; });
    if (map.CompanyName) {
      document.getElementById('logo-title').textContent = map.CompanyName;
      document.getElementById('login-logo').textContent = map.CompanyName + ' 系统';
    }
    if (map.LogoUrl) {
      const el = document.getElementById('logo-icon');
      el.innerHTML = `<img src="${map.LogoUrl}" alt="logo" style="width:100%;height:100%;object-fit:cover;border-radius:50%">`;
      el.style.background = 'transparent';
    }
  } catch (e) { /* 参数加载失败时使用默认 LOGO */ }
}

/* ============ 菜单 ============ */
function renderMenu(menus) {
  const nav = document.getElementById('sidebar-nav');
  if (!nav) {
    document.getElementById('content').innerHTML = '<div class="card">⚠️ sidebar-nav 元素不存在</div>';
    return;
  }
  nav.innerHTML = '';
  const list = menus || [];
  if (list.length === 0) {
    nav.innerHTML = '<div style="padding:16px;color:#fbbf24;font-size:13px">⚠️ 当前用户没有可用菜单权限<br><small>请检查 SysRoleMenus / SysMenus 配置</small></div>';
    return;
  }
  list.forEach(m => {
    try {
      if (m.children && m.children.length) nav.appendChild(createNavGroup(m));
    } catch (err) {
      const errDiv = document.createElement('div');
      errDiv.style.cssText = 'padding:8px;color:#fca5a5;font-size:12px';
      errDiv.textContent = '菜单 ' + (m && m.menuCode) + ' 渲染失败: ' + (err.message || err);
      nav.appendChild(errDiv);
    }
  });
}

/* 菜单分组 emoji 映射（义乌小商品外贸行业化）—— 提到顶层避免重复定义 */
const GROUP_EMOJI = {
  '系统设置': '⚙️', '基础资料': '🗂', '询价管理': '🔍',
  '订单管理': '📋', '物流管理': '🚚', '装柜管理': '🚢',
  '账务管理': '💰', '报表管理': '📊', '客户管理': '🤝',
  '供应商管理': '🏭', '仓库管理': '🏬', '商品资料': '📦',
  '国际买家': '🌍', '市场分区': '🏬', '爆款 SKU': '🔥', '品类管理': '🎁',
  '拼箱管理': '📦', '整柜管理': '🚢', '跨境电商': '🛒', '报关单证': '📜',
  '认证管理': '✅', '外汇结算': '💱', '信用证': '📃', '货代管理': '⛵',
  /* 阶段 0 菜单重构后的新分组名 */
  '工作台': '🏠', '客户与市场': '🌍', '供应商与采购': '🏭',
  '商品中心': '📦', '询报价': '🔍', '订单中心': '📋',
  '库存管理': '🏬', '出运管理': '🚢', '财务结算': '💰',
};

function createNavGroup(m) {
  const group = document.createElement('div');
  group.className = 'nav-group';

  const emoji = GROUP_EMOJI[m.menuName] || '';   /* 修复：原来 emoji 变量未定义 */
  const title = document.createElement('div');
  title.className = 'nav-item nav-parent';
  title.innerHTML = `${iconOf(m.menuCode)}<span class="nav-label">${emoji ? emoji + ' ' : ''}${m.menuName}</span><svg class="nav-arrow" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><path d="m9 6 6 6-6 6"/></svg>`;
  group.appendChild(title);

  const children = document.createElement('div');
  children.className = 'nav-children';
  // 内层包裹容器：统一承载子菜单项，便于整体做高度过渡
  const inner = document.createElement('div');
  m.children.forEach(c => {
    try { inner.appendChild(createNavItem(c)); }
    catch (err) { console.error('[createNavGroup] 子菜单失败', c, err); }
  });
  children.appendChild(inner);
  group.appendChild(children);

  title.onclick = () => {
    // 折叠状态时点击图标：先展开侧边栏
    if (document.body.classList.contains('sidebar-collapsed')) {
      document.body.classList.remove('sidebar-collapsed');
      const cb = document.getElementById('collapse-btn');
      if (cb) cb.title = '收起菜单';
    }
    // 手风琴：展开当前一级菜单的同时，收起其它已展开的一级菜单
    const willOpen = !group.classList.contains('open');
    closeAllNavGroups(group);
    openNavGroup(group, willOpen);
  };
  return group;
}

/* 展开 / 收起一级菜单（手风琴：配合 closeAllNavGroups 使用；子菜单高度由 JS 精确设置以带动画） */
function openNavGroup(group, open) {
  if (!group) return;
  group.classList.toggle('open', !!open);
  const arrow = group.querySelector('.nav-arrow');
  if (arrow) arrow.style.transform = open ? 'rotate(90deg)' : 'rotate(0deg)';

  const children = group.querySelector('.nav-children');
  if (!children) return;
  if (open) {
    children.style.height = 'auto';
    const target = children.offsetHeight;          // 测量真实高度
    if (target > 0) {
      children.style.height = '0px';
      void children.offsetHeight;                  // 强制重排，确保从 0 开始过渡
      children.style.height = target + 'px';
      children.addEventListener('transitionend', function onEnd(e) {
        if (e.target !== children || e.propertyName !== 'height') return;
        children.removeEventListener('transitionend', onEnd);
        if (group.classList.contains('open')) children.style.height = 'auto';   // 动画结束放开高度，内容自适应
      });
    } else {
      children.style.height = 'auto';
    }
  } else {
    if (!children.style.height || children.style.height === 'auto') {
      children.style.height = children.offsetHeight + 'px';
      void children.offsetHeight;
    }
    children.style.height = '0px';
  }
}

/* 收起除 except 之外的其它一级菜单（保证同时只展开一个） */
function closeAllNavGroups(except) {
  document.querySelectorAll('.nav-group.open').forEach(g => {
    if (except && g === except) return;
    openNavGroup(g, false);
  });
}

function createNavItem(m) {
  const item = document.createElement('div');
  item.className = 'nav-item nav-child';
  item.dataset.code = m.menuCode;
  item.innerHTML = `${iconOf(m.menuCode)}<span>${m.menuName}</span>`;
  item.onclick = () => {
    document.querySelectorAll('.nav-item.nav-child').forEach(x => x.classList.remove('active'));
    item.classList.add('active');
    navigate(m.menuCode, m.menuName);
  };
  return item;
}

/* 菜单编码 -> 图标名映射 */
const CODE_ICON = {
  system: 'settings', user: 'users', role: 'shield', 'user-permission': 'key',
  'sys-parameter': 'sliders', 'user-parameter': 'user-cog', 'doc-rule': 'hash',
  'client-limit': 'lock', 'sys-log': 'scroll', base: 'database',
  /* 阶段 0 菜单重构新增的一级分组与「我的工作台」 */
  workbench: 'layers', crm: 'users', purchase: 'factory', goods: 'boxes', home: 'landmark',
  /* 阶段 1 新增：出口退税台账 */
  'tax-refund': 'banknote',
  /* 阶段 2 新增：费用单 */
  'expense-bill': 'file-text',
  /* 阶段 2 新增：供应商比价 */
  'purchase-quote': 'scale',
  /* 阶段 3 新增：报价单 */
  quotation: 'receipt',
  /* 阶段 2 新增：单证中心 */
  'doc-center': 'scroll',
  /* 阶段 2 新增：客户跟进记录 / 业务员提成表 */
  'customer-follow': 'user-round',
  'sales-commission': 'trending-up',
  /* 阶段 2 新增：样品管理 / 跟进提醒 */
  sample: 'package',
  'follow-up-due': 'calendar',
  customer: 'building', supplier: 'factory', employee: 'user-round',
  'expense-account': 'receipt', warehouse: 'warehouse', product: 'package', 'other-info': 'layers',
  inquiry: 'search', 'inquiry-new': 'file-plus', 'inquiry-export': 'download',
  order: 'cart', 'sales-order': 'clipboard', 'sales-order-export': 'download',
  'purchase-order': 'clipboard', 'purchase-order-export': 'download',
  logistics: 'truck', 'stock-in': 'package-plus', 'stock-out': 'package-minus', 'stock-query': 'boxes',
  container: 'container', 'receiving-plan': 'calendar', booking: 'anchor',
  'pre-loading': 'container', 'loading-list': 'clipboard-check',
  finance: 'landmark', 'deposit-apply': 'file-text', 'payment-apply': 'file-text',
  payment: 'banknote', 'container-settlement': 'ship', 'bulk-settlement': 'box',
  receipt: 'wallet', complaint: 'alert', report: 'bar-chart',
  'product-sales-ranking': 'trending-up', 'order-profit': 'chart-line',
  'customer-shipment': 'pie-chart', 'salesman-output': 'user-round',
  'balance-sheet': 'scale', 'income-statement': 'chart-line', 'cash-flow': 'waves',
};

function iconOf(code) {
  return icon(CODE_ICON[code] || 'dot');
}

let openTabs = []; // 最近打开的标签（最多 3 个）[{code, name}]

function renderPage(code, name) {
  CURRENT_PAGE_CODE = code;
  document.getElementById('header-title').textContent = name;
  if (code === 'home') renderHome();                                                // 首页 · 义乌国际商贸城门户
  else if (code === 'user') renderUserModule();
  else if (code === 'role') renderRoleModule();
  else if (code === 'user-permission') renderUserPermissionModule();
  else if (code === 'user-parameter') renderUserParamModule();
  else if (code === 'print-design') renderTplCenterModule(window.__pdPreSelect);      // 样式设计（单据模板中心：Excel 式网格设计器）
  else if (code === 'print-quick-style') renderPrintDesignModule();                   // 快速样式配置（字体/字号/颜色/行高）
  else if (code === 'dingtalk-config') renderDingTalkConfigModule();                 // 钉钉通知配置
  else if (code === 'dingtalk-log') renderDingTalkLogModule();                       // 钉钉发送记录
  else if (EXPORT_MENU_MAP[code]) renderBillExport(code);
  else if (BILL_CODE_MAP[code]) renderBillV2(BILL_CODE_MAP[code]);
  else {
    const mod = MODULES[code];
    if (mod) { CURRENT_MODULE_CODE = code; renderModule(mod); }
    else if (REPORTS[code]) renderReport(REPORTS[code], name);
    else document.getElementById('content').innerHTML = '<div class="card empty">该功能开发中</div>';
  }
  // 触发内容区淡入上移动画（每次重新挂载都重启动画）
  const content = document.getElementById('content');
  content.classList.remove('page-enter');
  void content.offsetWidth;                              // 强制 reflow 重启动画
  content.classList.add('page-enter');
  renderTabs();
}

/* ============ 全局按钮 Ripple 涟漪 ============ */
document.addEventListener('pointerdown', e => {
  const btn = e.target.closest('.btn');
  if (!btn || btn.disabled) return;
  const rect = btn.getBoundingClientRect();
  const size = Math.max(rect.width, rect.height);
  const ripple = document.createElement('span');
  ripple.className = 'ripple';
  ripple.style.width  = size + 'px';
  ripple.style.height = size + 'px';
  ripple.style.left = (e.clientX - rect.left - size / 2) + 'px';
  ripple.style.top  = (e.clientY - rect.top  - size / 2) + 'px';
  btn.appendChild(ripple);
  setTimeout(() => ripple.remove(), 620);
});

/* ============ 顶部 header 切换控件：主题 / 语言 / 在线状态 / 时区 ============ */
const THEME_KEY = 'erp_theme';        // 'auto' | 'light' | 'dark'
const LANG_KEY  = 'erp_lang';         // 'zh' | 'en'
const THEME_ICON = { auto: '🌓', light: '☀️', dark: '🌙' };
const LANG_OPT   = { zh: { flag: '🇨🇳', label: '中文' }, en: { flag: '🇺🇸', label: 'English' } };

function applyTheme(mode) {
  const root = document.documentElement;
  root.classList.remove('theme-auto', 'theme-dark');
  if (mode === 'dark') root.classList.add('theme-dark');
  else if (mode === 'light') { /* 保留默认浅色 token */ }
  else root.classList.add('theme-auto');   // auto：跟随系统
  localStorage.setItem(THEME_KEY, mode);
  const btn = document.getElementById('theme-toggle');
  if (btn) btn.textContent = THEME_ICON[mode] || '🌙';
}

function cycleTheme() {
  const cur = localStorage.getItem(THEME_KEY) || 'auto';
  const order = ['auto', 'light', 'dark'];
  const next = order[(order.indexOf(cur) + 1) % order.length];
  applyTheme(next);
  const labelMap = { auto: '跟随系统', light: '浅色', dark: '深色' };
  showToast(`已切换主题：${labelMap[next]}`, 'info');
}

function applyLang(lang) {
  localStorage.setItem(LANG_KEY, lang);
  const opt = LANG_OPT[lang];
  const flag = document.querySelector('#lang-switch .lang-flag');
  const lbl = document.getElementById('lang-label');
  if (flag) flag.textContent = opt.flag;
  if (lbl) lbl.textContent = opt.label;
}

function cycleLang() {
  const cur = localStorage.getItem(LANG_KEY) || 'zh';
  const next = cur === 'zh' ? 'en' : 'zh';
  applyLang(next);
  const labelMap = { zh: '中文', en: 'English' };
  showToast(`已切换语言：${labelMap[next]}（界面元素 i18n 后续接入）`, 'info');
}

/* 时区 badge：实时刷新 UTC+8 当前时刻（按分钟） */
function refreshTzBadge() {
  const el = document.querySelector('.tz-badge');
  if (!el) return;
  try {
    const now = new Date();
    const utc8 = new Date(now.getTime() + (8 - -now.getTimezoneOffset() / 60) * 3600000);
    const hh = String(utc8.getUTCHours()).padStart(2, '0');
    const mm = String(utc8.getUTCMinutes()).padStart(2, '0');
    el.textContent = `UTC+8 · ${hh}:${mm}`;
    el.title = `服务器时区：UTC+8\n本地时区：UTC${-now.getTimezoneOffset() / 60 >= 0 ? '+' : ''}${-now.getTimezoneOffset() / 60}`;
  } catch (e) { /* 静默失败保留默认文本 */ }
}

/* 在线状态点：每 30s ping 一次接口，未连通时降级为离线灰 */
function pingOnline() {
  const dot = document.querySelector('.header-right .dot');
  if (!dot) return;
  // 先乐观标绿，2s 后端没响应再降级
  dot.className = 'dot dot-online';
  dot.title = '在线';
  fetch('/api/auth/profile', { headers: { Authorization: 'Bearer ' + TOKEN } })
    .then(r => { if (!r.ok) throw new Error(); })
    .catch(() => {
      dot.className = 'dot dot-offline';
      dot.title = '离线';
    });
}

/* ============ 全局图片灯箱 ============ */
function showLightbox(src) {
  let lb = document.getElementById('lightbox');
  if (!lb) {
    lb = document.createElement('div');
    lb.id = 'lightbox';
    lb.className = 'lightbox';
    lb.innerHTML = '<div class="lightbox-mask"></div><img class="lightbox-img" alt="图片预览"><div class="lightbox-tip">点击空白处关闭</div>';
    document.body.appendChild(lb);
    lb.addEventListener('click', () => lb.classList.remove('show'));
  }
  lb.querySelector('.lightbox-img').src = src;
  lb.classList.add('show');
}
function hideLightbox() {
  const lb = document.getElementById('lightbox');
  if (lb) lb.classList.remove('show');
}

/* ============ 启动绑定（DOM ready 后） ============ */
function bindHeaderControls() {
  const themeBtn = document.getElementById('theme-toggle');
  if (themeBtn) themeBtn.addEventListener('click', cycleTheme);
  const langBtn = document.getElementById('lang-switch');
  if (langBtn) langBtn.addEventListener('click', cycleLang);
  applyTheme(localStorage.getItem(THEME_KEY) || 'auto');
  applyLang(localStorage.getItem(LANG_KEY) || 'zh');
  refreshTzBadge();
  setInterval(refreshTzBadge, 30 * 1000);
  pingOnline();
  setInterval(pingOnline, 30 * 1000);

  // 登录页：粒子 / ticker / 快捷登录 / tab 切换 / 密码可见
  spawnLoginParticles();
  loadLoginTicker();
  setInterval(loadLoginTicker, 30 * 1000);
  document.querySelectorAll('.login-tab').forEach(tab => {
    tab.addEventListener('click', () => {
      if (tab.disabled) return;
      document.querySelectorAll('.login-tab').forEach(t => t.classList.remove('active'));
      tab.classList.add('active');
      showToast(`${tab.textContent.trim()} 即将上线`, 'info');
    });
  });
}
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', bindHeaderControls);
} else {
  bindHeaderControls();
}

/* ============ 登录页科技感装饰 ============ */
function spawnLoginParticles() {
  const host = document.getElementById('login-particles');
  if (!host) return;
  const N = 36;
  const colors = ['#22d3ee', '#a78bfa', '#ec4899', '#fb923c', '#34d399'];
  for (let i = 0; i < N; i++) {
    const p = document.createElement('span');
    p.className = 'particle';
    const size = 3 + Math.random() * 6;
    p.style.width  = size + 'px';
    p.style.height = size + 'px';
    p.style.left   = (Math.random() * 100) + '%';
    p.style.background = `radial-gradient(circle, ${colors[i % colors.length]} 0%, transparent 70%)`;
    p.style.animationDuration = (8 + Math.random() * 10) + 's';
    p.style.animationDelay    = (-Math.random() * 12) + 's';
    host.appendChild(p);
  }
}

async function loadLoginTicker() {
  const setText = (id, v) => { const el = document.getElementById(id); if (el) el.textContent = v; };
  const fmt = n => (n == null || isNaN(n)) ? '--' : Number(n).toLocaleString();
  try {
    const today = new Date().toISOString().slice(0, 10);
    const r = await Promise.race([
      fetch('/api/reports/salesman-output?start=' + today + '&end=' + today, { headers: { Authorization: 'Bearer ' + TOKEN } }),
      new Promise((_, rej) => setTimeout(() => rej(new Error('timeout')), 3000)),
    ]);
    if (r.ok) {
      const data = await r.json();
      const arr = Array.isArray(data) ? data : (data.items || []);
      setText('tk-orders', fmt(arr.length));
    }
  } catch (e) { setText('tk-orders', '--'); }
  try {
    const r = await Promise.race([
      fetch('/api/base/customers?page=1&pageSize=1', { headers: { Authorization: 'Bearer ' + TOKEN } }),
      new Promise((_, rej) => setTimeout(() => rej(new Error('timeout')), 3000)),
    ]);
    if (r.ok) {
      const data = await r.json();
      setText('tk-customers', fmt(data.total || (data.items || []).length));
    }
  } catch (e) { setText('tk-customers', '--'); }
  setText('tk-containers', '128');   // 在途柜量暂用占位（无专门接口）
  try {
    const mStart = new Date(); mStart.setDate(1);
    const start = mStart.toISOString().slice(0, 10);
    const end   = new Date().toISOString().slice(0, 10);
    const r = await Promise.race([
      fetch('/api/reports/salesman-output?start=' + start + '&end=' + end, { headers: { Authorization: 'Bearer ' + TOKEN } }),
      new Promise((_, rej) => setTimeout(() => rej(new Error('timeout')), 3000)),
    ]);
    if (r.ok) {
      const data = await r.json();
      const arr = Array.isArray(data) ? data : (data.items || []);
      const total = arr.reduce((s, x) => s + (Number(x.amount) || Number(x.totalAmount) || Number(x.salesAmount) || 0), 0);
      setText('tk-sales', fmt(total.toFixed(2)));
    }
  } catch (e) { setText('tk-sales', '--'); }
}

function togglePasswordVisibility() {
  const el = document.getElementById('login-password');
  if (!el) return;
  el.type = el.type === 'password' ? 'text' : 'password';
}

function quickLogin(u, p) {
  document.getElementById('login-username').value = u;
  document.getElementById('login-password').value = p;
  document.getElementById('login-form').dispatchEvent(new Event('submit', { cancelable: true }));
}

/* ============ Toast 增强：支持 success/error/warning/info 四种类型 + 进度条自动消失 ============ */
function showToast(msg, type = 'success') {
  const t = document.getElementById('toast');
  t.textContent = msg;
  t.className = 'toast ' + type + ' fade-in';
  t.style.display = 'flex';
  setTimeout(() => { t.style.display = 'none'; t.className = 'toast'; }, 3000);
}

function navigate(code, name) {
  renderPage(code, name);
  addTab(code, name);
}

function addTab(code, name) {
  openTabs = openTabs.filter(t => t.code !== code); // 去重
  openTabs.unshift({ code, name }); // 最新打开的排最前
  if (openTabs.length > 3) openTabs = openTabs.slice(0, 3); // 最多保留 3 个
  renderTabs();
}

function renderTabs() {
  const bar = document.getElementById('tabs-bar');
  const currentTitle = document.getElementById('header-title').textContent;
  bar.innerHTML = openTabs.map(t => {
    // 优先按页面编码匹配（首页标题为「首页 · 义乌国际商贸城门户」，按标题文本匹配会失效）
    const active = (CURRENT_PAGE_CODE && t.code === CURRENT_PAGE_CODE)
      || (!CURRENT_PAGE_CODE && t.name === currentTitle) ? ' active' : '';
    return `<div class="tab${active}" onclick="switchTab('${t.code}','${t.name}')">
      <span class="tab-name">${t.name}</span>
      <span class="tab-close" onclick="event.stopPropagation();closeTab('${t.code}')">×</span>
    </div>`;
  }).join('');
}

function switchTab(code, name) {
  renderPage(code, name); // 切换页面（不改变标签顺序）
}

function closeTab(code) {
  openTabs = openTabs.filter(t => t.code !== code);
  renderTabs();
}

/* 返回首页门户（点击左上角公司 LOGO、点顶部「首页」标签均走这里） */
function goHome() { navigate('home', '首页'); }

function renderHome() {
  CURRENT_PAGE_CODE = 'home';
  document.getElementById('header-title').textContent = '首页 · 义乌国际商贸城门户';
  const userName = PROFILE.displayName || PROFILE.userName;
  const hour = new Date().getHours();
  const greeting = hour < 6 ? '凌晨好' : hour < 12 ? '上午好' : hour < 14 ? '中午好' : hour < 18 ? '下午好' : '晚上好';
  document.getElementById('content').innerHTML = `
    <!-- 门户 Hero：义乌行业 + 科技感 -->
    <div class="portal-hero">
      <div class="portal-hero-bg"></div>
      <div class="portal-hero-grid"></div>
      <div class="portal-hero-content">
        <div class="portal-greet">
          <span class="portal-greet-icon">👋</span>
          <span class="portal-greet-text">${greeting}, <b>${userName}</b></span>
          <span class="portal-greet-tag">YIWU · FOREIGN TRADE</span>
        </div>
        <h1 class="portal-headline">
          <span class="portal-line">义乌国际商贸城</span>
          <span class="portal-line portal-line-accent">全球小商品出口 <b>数字孪生平台</b></span>
        </h1>
        <p class="portal-sub">饰品 · 玩具 · 袜品 · 文具 · 箱包 · 五金 · 工艺品 · 日用品 · 美妆 · 圣诞用品 —— 8 大品类 · 180 万 SKU · 销往 200+ 国家与地区</p>
        <div class="portal-stats">
          <div class="portal-stat"><span class="portal-stat-num" data-ps="customers">--</span><span class="portal-stat-label">活跃国际买家</span></div>
          <div class="portal-stat-sep"></div>
          <div class="portal-stat"><span class="portal-stat-num" data-ps="skus">--</span><span class="portal-stat-label">在售 SKU</span></div>
          <div class="portal-stat-sep"></div>
          <div class="portal-stat"><span class="portal-stat-num" data-ps="countries">200+</span><span class="portal-stat-label">出口国家与地区</span></div>
          <div class="portal-stat-sep"></div>
          <div class="portal-stat"><span class="portal-stat-num" data-ps="containers">128</span><span class="portal-stat-label">今日在途 TEU</span></div>
        </div>
      </div>
      <div class="portal-hero-deco">🌐</div>
      <div class="portal-hero-deco portal-2">🚢</div>
      <div class="portal-hero-deco portal-3">🚂</div>
      <div class="portal-hero-deco portal-4">✈️</div>
    </div>

    <!-- 行业化 KPI（义乌 8 大品类 + 多式联运） -->
    <div class="kpi-grid">
      <div class="kpi-card ocean tech-card">
        <span class="tech-card-corner tl"></span><span class="tech-card-corner tr"></span>
        <span class="tech-card-corner bl"></span><span class="tech-card-corner br"></span>
        <div class="kpi-label"><span class="kpi-emoji">🎀</span>饰品配件 · 今日订单</div>
        <div class="kpi-value" data-ykpi="cat-acc">--<span class="unit">单</span></div>
        <div class="kpi-delta up" data-ykpi="cat-acc-d">↑ 12% 同比</div>
      </div>
      <div class="kpi-card cargo tech-card">
        <span class="tech-card-corner tl"></span><span class="tech-card-corner tr"></span>
        <span class="tech-card-corner bl"></span><span class="tech-card-corner br"></span>
        <div class="kpi-label"><span class="kpi-emoji">🧸</span>玩具公仔 · 今日订单</div>
        <div class="kpi-value" data-ykpi="cat-toy">--<span class="unit">单</span></div>
        <div class="kpi-delta up" data-ykpi="cat-toy-d">↑ 8% 同比</div>
      </div>
      <div class="kpi-card port tech-card">
        <span class="tech-card-corner tl"></span><span class="tech-card-corner tr"></span>
        <span class="tech-card-corner bl"></span><span class="tech-card-corner br"></span>
        <div class="kpi-label"><span class="kpi-emoji">🧦</span>袜品针织 · 今日订单</div>
        <div class="kpi-value" data-ykpi="cat-sock">--<span class="unit">单</span></div>
        <div class="kpi-delta up" data-ykpi="cat-sock-d">↑ 5% 同比</div>
      </div>
      <div class="kpi-card lc tech-card">
        <span class="tech-card-corner tl"></span><span class="tech-card-corner tr"></span>
        <span class="tech-card-corner bl"></span><span class="tech-card-corner br"></span>
        <div class="kpi-label"><span class="kpi-emoji">📚</span>文具办公 · 今日订单</div>
        <div class="kpi-value" data-ykpi="cat-stat">--<span class="unit">单</span></div>
        <div class="kpi-delta flat" data-ykpi="cat-stat-d">-- 同比</div>
      </div>
      <div class="kpi-card gold tech-card">
        <span class="tech-card-corner tl"></span><span class="tech-card-corner tr"></span>
        <span class="tech-card-corner bl"></span><span class="tech-card-corner br"></span>
        <div class="kpi-label"><span class="kpi-emoji">💰</span>本月销售（USD）</div>
        <div class="kpi-value" data-ykpi="sales-month">--<span class="unit">USD</span></div>
        <div class="kpi-delta up" data-ykpi="sales-month-d">↑ 较上月</div>
      </div>
    </div>

    <!-- 多式联运 · 物流时效看板 -->
    <div class="card tech-card">
      <span class="tech-card-corner tl"></span><span class="tech-card-corner tr"></span>
      <span class="tech-card-corner bl"></span><span class="tech-card-corner br"></span>
      <div class="card-title">🚢✈️🚂 多式联运 · 义乌出口物流看板 <span class="card-title-tip">实时</span></div>
      <div class="multi-transport">
        <div class="transport-card ocean">
          <div class="transport-icon">🚢</div>
          <div class="transport-name">海运 · 宁波港</div>
          <div class="transport-route">NINGBO → 全球 200+ 港口</div>
          <div class="transport-time">⏱ 15-25 天</div>
          <div class="transport-price">💵 ¥2,800 / CBM</div>
        </div>
        <div class="transport-card cargo">
          <div class="transport-icon">🚂</div>
          <div class="transport-name">中欧班列 · 义乌</div>
          <div class="transport-route">YIWU → 莫斯科 / 伦敦 / 马德里</div>
          <div class="transport-time">⏱ 18-22 天</div>
          <div class="transport-price">💵 ¥3,500 / CBM</div>
        </div>
        <div class="transport-card port">
          <div class="transport-icon">✈️</div>
          <div class="transport-name">国际空运 · 萧山机场</div>
          <div class="transport-route">HGH → 迪拜 / 法兰克福 / 纽约</div>
          <div class="transport-time">⏱ 3-5 天</div>
          <div class="transport-price">💵 ¥28 / KG</div>
        </div>
        <div class="transport-card lc">
          <div class="transport-icon">📦</div>
          <div class="transport-name">国际快递 · 集运</div>
          <div class="transport-route">YIWU → DHL/FedEx/UPS 全球</div>
          <div class="transport-time">⏱ 5-10 天</div>
          <div class="transport-price">💵 ¥65 / KG</div>
        </div>
      </div>
    </div>

    <!-- 客户区域分布 + 爆款品类 双列 -->
    <div class="portal-cols">
      <div class="card tech-card">
        <span class="tech-card-corner tl"></span><span class="tech-card-corner tr"></span>
        <span class="tech-card-corner bl"></span><span class="tech-card-corner br"></span>
        <div class="card-title">🌍 客户区域分布 <span class="card-title-tip">本月 TOP</span></div>
        <div class="region-list">
          <div class="region-row"><span class="region-flag">🕌</span><span class="region-name">中东（沙特/阿联酋/伊拉克）</span><span class="region-bar"><span class="region-fill ocean"  style="width:92%"></span></span><span class="region-pct">28%</span></div>
          <div class="region-row"><span class="region-flag">🌍</span><span class="region-name">非洲（尼日利亚/肯尼亚/埃及）</span><span class="region-bar"><span class="region-fill cargo" style="width:78%"></span></span><span class="region-pct">22%</span></div>
          <div class="region-row"><span class="region-flag">🌎</span><span class="region-name">欧美（美/英/德/法）</span><span class="region-bar"><span class="region-fill port"  style="width:64%"></span></span><span class="region-pct">18%</span></div>
          <div class="region-row"><span class="region-flag">🌏</span><span class="region-name">东盟（越/泰/印尼/菲）</span><span class="region-bar"><span class="region-fill lc"    style="width:55%"></span></span><span class="region-pct">15%</span></div>
          <div class="region-row"><span class="region-flag">🗾</span><span class="region-name">日韩</span><span class="region-bar"><span class="region-fill gold"  style="width:35%"></span></span><span class="region-pct">10%</span></div>
          <div class="region-row"><span class="region-flag">🌐</span><span class="region-name">其他</span><span class="region-bar"><span class="region-fill" style="width:25%"></span></span><span class="region-pct">7%</span></div>
        </div>
      </div>

      <div class="card tech-card">
        <span class="tech-card-corner tl"></span><span class="tech-card-corner tr"></span>
        <span class="tech-card-corner bl"></span><span class="tech-card-corner br"></span>
        <div class="card-title">🔥 义乌爆款品类 <span class="card-title-tip">本月 TOP</span></div>
        <div class="hot-cat">
          <div class="hot-cat-item"><span class="hot-rank">1</span><span class="hot-emoji">🎀</span><span class="hot-name">节日饰品 / 圣诞用品</span><span class="hot-trend up">↑ 36%</span></div>
          <div class="hot-cat-item"><span class="hot-rank">2</span><span class="hot-emoji">🧸</span><span class="hot-name">毛绒玩具 / IP 衍生品</span><span class="hot-trend up">↑ 28%</span></div>
          <div class="hot-cat-item"><span class="hot-rank">3</span><span class="hot-emoji">🧦</span><span class="hot-name">功能性袜品 / 瑜伽袜</span><span class="hot-trend up">↑ 22%</span></div>
          <div class="hot-cat-item"><span class="hot-rank">4</span><span class="hot-emoji">📱</span><span class="hot-name">手机配件 / 数据线</span><span class="hot-trend up">↑ 18%</span></div>
          <div class="hot-cat-item"><span class="hot-rank">5</span><span class="hot-emoji">🪞</span><span class="hot-name">化妆镜 / 美妆工具</span><span class="hot-trend up">↑ 15%</span></div>
          <div class="hot-cat-item"><span class="hot-rank">6</span><span class="hot-emoji">🎄</span><span class="hot-name">圣诞装饰 / 派对用品</span><span class="hot-trend up">↑ 12%</span></div>
        </div>
      </div>
    </div>

    <!-- 行业模块入口（义乌市场分区映射） -->
    <div class="card tech-card">
      <span class="tech-card-corner tl"></span><span class="tech-card-corner tr"></span>
      <span class="tech-card-corner bl"></span><span class="tech-card-corner br"></span>
      <div class="card-title">🏬 义乌国际商贸城 · 5 大区行业映射 <span class="card-title-tip">点击进入对应模块</span></div>
      <div class="market-grid">
        <a class="market-tile ocean"  onclick="navigate('customer', '客户资料')">
          <span class="market-zone">一区</span>
          <span class="market-emoji">🤝</span>
          <span class="market-name">国际买家中心</span>
          <span class="market-sub">中东 / 非洲 / 欧美批发商档案</span>
        </a>
        <a class="market-tile cargo" onclick="navigate('product', '商品资料')">
          <span class="market-zone">二区</span>
          <span class="market-emoji">🎁</span>
          <span class="market-name">饰品 / 玩具 / 五金</span>
          <span class="market-sub">180 万 SKU · 多语言品名 · HS Code</span>
        </a>
        <a class="market-tile port"  onclick="navigate('sales-order', '销售订单')">
          <span class="market-zone">三区</span>
          <span class="market-emoji">📋</span>
          <span class="market-name">订单履约中心</span>
          <span class="market-sub">PI / SC / 拼箱 / 整柜 / FOB/CIF</span>
        </a>
        <a class="market-tile lc"    onclick="navigate('container', '装柜管理')">
          <span class="market-zone">四区</span>
          <span class="market-emoji">🚢</span>
          <span class="market-name">物流装柜中心</span>
          <span class="market-sub">订舱 / 装柜清单 / 报关 / 提单</span>
        </a>
        <a class="market-tile gold"  onclick="navigate('receipt', '收款单')">
          <span class="market-zone">五区</span>
          <span class="market-emoji">💰</span>
          <span class="market-name">外汇结算中心</span>
          <span class="market-sub">T/T · L/C · 西联 · PayPal · 速卖通</span>
        </a>
      </div>
    </div>

    <!-- 行业小贴士（外贸流程） -->
    <div class="card tech-card empty-tip">
      <span class="tech-card-corner tl"></span><span class="tech-card-corner tr"></span>
      <span class="tech-card-corner bl"></span><span class="tech-card-corner br"></span>
      <div class="empty-tip-emoji">🚢</div>
      <div class="empty-tip-title">义乌小商品 · 外贸出口标准流程</div>
      <ul class="empty-tip-list">
        <li><b>询盘 → 报价 → 形式发票 PI → 客户付款定金 → 生产备货 → 验货 → 订舱 / 报关 → 装柜 → 提单 B/L → 收全款 → 寄单 / 放单</b></li>
        <li>💡 拼箱 <b>LCL</b> 适合 SKU 多 / 单量小的义乌小商品；整柜 <b>FCL</b>（20GP/40HQ）适合单 SKU 体量大的采购</li>
        <li>💡 <b>中欧班列</b> 从义乌直达莫斯科/马德里/伦敦，18-22 天，海运备货慢时可作为优质替代</li>
        <li>💡 出口信用证 <b>L/C</b> 对中东/非洲大单有效，可规避汇率与收款风险</li>
        <li>💡 报关 HS Code 必须正确（饰品 7117 / 玩具 9503 / 袜品 6115），影响退税率与海关查验</li>
      </ul>
    </div>`;
  loadPortalData();
  renderTabs();
}

function countByCode(menus) {
  let n = 0;
  (menus || []).forEach(m => { n += 1; if (m.children) n += m.children.length; });
  return n;
}

async function loadPortalData() {
  // 客户总数（拉真实接口）
  try {
    const r = await api('/api/base/customers?page=1&pageSize=1');
    const t = document.querySelector('[data-ps="customers"]');
    if (t) t.textContent = (r.total || 0).toLocaleString();
  } catch (e) { /* 保留占位 */ }
  // SKU 总数（商品资料总数）
  try {
    const r = await api('/api/base/products?page=1&pageSize=1');
    const t = document.querySelector('[data-ps="skus"]');
    if (t) t.textContent = ((r.total || 0) * 1).toLocaleString();
  } catch (e) { /* 保留占位 */ }
  // 本月销售额
  try {
    const start = new Date(); start.setDate(1);
    const s = start.toISOString().slice(0, 10);
    const e = new Date().toISOString().slice(0, 10);
    const r = await api(`/api/reports/salesman-output?start=${s}&end=${e}`);
    const arr = Array.isArray(r) ? r : (r.items || []);
    const total = arr.reduce((s2, x) => s2 + (Number(x.amount) || Number(x.totalAmount) || Number(x.salesAmount) || 0), 0);
    const t = document.querySelector('[data-ykpi="sales-month"]');
    if (t) t.firstChild.nodeValue = total.toFixed(2);
  } catch (e) { /* 保留占位 */ }
}

async function loadHomeKpis() {
  // 兼容旧接口调用（保留不再报错）
}

function sumSales(r) {
  if (!Array.isArray(r)) return 0;
  return r.reduce((s, x) => s + (Number(x.amount) || Number(x.totalAmount) || Number(x.salesAmount) || 0), 0).toFixed(2);
}
function setKpi(key, val) {
  const el = document.querySelector(`[data-kpi="${key}"]`);
  if (el) el.firstChild.nodeValue = String(val);
}
function todayISO() { return new Date().toISOString().slice(0, 10); }
function monthStartISO() {
  const d = new Date(); d.setDate(1);
  return d.toISOString().slice(0, 10);
}

/* ============ 启动 ============ */
if (TOKEN) initApp().catch(() => {});
