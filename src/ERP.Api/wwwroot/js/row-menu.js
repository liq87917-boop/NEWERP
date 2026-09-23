/* ============================================================
   ============ 列表「操作列」更多操作下拉菜单 ================
   ============================================================
   背景：列表操作列只保留一个主操作（通常为「编辑」），其余操作
         收进「更多 ▾」下拉菜单。

   为什么菜单要用 JS 提升到 body 层：表格容器 .table-wrap 保留
   overflow-x:auto（宽表必须能横向滚动，见设计规范 11 节），
   overflow 会裁切单元格内的绝对定位弹层；且行入场动画在 <tr> 上
   留有效果 transform，会让单元格内的 position:fixed 也失效。
   因此展开时把菜单节点临时移到 body 下的定位层，用 fixed 跟随
   按钮定位；关闭时再归位到原单元格，保证表格结构始终完整。
   ============================================================ */

let __rowMenuOpen = null;   // { menu, btn } 当前打开的菜单

/* 打开/收起某个行的「更多」菜单（按钮 onclick="toggleRowMenu(event, this)"） */
function toggleRowMenu(ev, btn) {
  if (ev) { ev.stopPropagation(); ev.preventDefault(); }
  const td = btn.closest('td');
  const menu = td ? td.querySelector('.row-menu') : null;
  if (!menu) return;

  const isSame = __rowMenuOpen && __rowMenuOpen.menu === menu;
  closeRowMenu();
  if (isSame) return;                       // 再次点击同一按钮 = 收起

  let layer = document.getElementById('row-menu-layer');
  if (!layer) {
    layer = document.createElement('div');
    layer.id = 'row-menu-layer';
    document.body.appendChild(layer);
  }

  menu.__rowHome = menu.parentElement;      // 记住原位（关闭时归位）
  layer.appendChild(menu);
  menu.hidden = false;
  btn.classList.add('is-open');

  // 先按左上角渲染一次，拿到真实尺寸后再精确定位
  menu.style.left = '0px';
  menu.style.top = '0px';
  positionRowMenu(menu, btn);

  __rowMenuOpen = { menu, btn };
}

/* 定位：右对齐按钮；下方空间不足时自动向上弹出 */
function positionRowMenu(menu, btn) {
  const r = btn.getBoundingClientRect();
  const mw = menu.offsetWidth || 156;
  const mh = menu.offsetHeight || 200;
  const vw = window.innerWidth;
  const vh = window.innerHeight;

  let left = r.right - mw;                                  // 与按钮右边缘对齐
  if (left < 8) left = 8;
  if (left + mw > vw - 8) left = Math.max(8, vw - mw - 8);

  let top = r.bottom + 6;
  if (top + mh > vh - 8) top = Math.max(8, r.top - mh - 6);  // 空间不足则向上弹

  menu.style.left = Math.round(left) + 'px';
  menu.style.top = Math.round(top) + 'px';
}

/* 关闭当前菜单并归位到原单元格 */
function closeRowMenu() {
  if (!__rowMenuOpen) return;
  const { menu, btn } = __rowMenuOpen;
  menu.hidden = true;
  btn.classList.remove('is-open');
  if (menu.__rowHome && menu.__rowHome.isConnected) menu.__rowHome.appendChild(menu);
  __rowMenuOpen = null;
}

/* 点击菜单项：内联 onclick 先执行，冒泡到此再收起菜单 */
document.addEventListener('click', (e) => {
  const t = e.target;
  if (t && t.closest && t.closest('.row-menu-item')) { closeRowMenu(); return; }
  if (t && t.closest && t.closest('.row-more')) return;      // 交给 toggleRowMenu 处理
  closeRowMenu();
});
/* 滚动（含表格横向滚动）、窗口尺寸变化、Esc 时收起菜单 */
window.addEventListener('scroll', closeRowMenu, true);
window.addEventListener('resize', closeRowMenu);
document.addEventListener('keydown', (e) => { if (e.key === 'Escape') closeRowMenu(); });
