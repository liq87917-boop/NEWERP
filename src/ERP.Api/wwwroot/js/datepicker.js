/* ============================================================
 * 轻量日期选择器（原生 JS，零第三方依赖）
 * ------------------------------------------------------------
 * 作用：接管页面上所有 <input type="date">，用统一风格的自定义
 *      日历浮层替代浏览器原生日期控件外观。
 * 特性：日 / 月 / 年三级视图、今天·本月初·本月末快捷、清空、
 *      键盘增减日期、min/max 范围限制、动态表单自动接管。
 * 约定：输入框 value 始终保持 yyyy-MM-dd 文本格式，
 *      因此既有业务代码（saveForm、loadReport 等）无需改动。
 * ============================================================ */
(function () {
  'use strict';

  /** 星期表头（周一为一周起始，符合国内习惯） */
  var WEEK_LABELS = ['一', '二', '三', '四', '五', '六', '日'];
  /** 月份视图文案 */
  var MONTH_LABELS = ['1 月', '2 月', '3 月', '4 月', '5 月', '6 月', '7 月', '8 月', '9 月', '10 月', '11 月', '12 月'];
  /** 已接管标记（data-erp-dp） */
  var FLAG = 'erpDp';

  var panel = null;         // 浮层单例节点
  var activeInput = null;   // 当前关联的输入框
  var viewYear = 0;         // 浮层展示的年份
  var viewMonth = 0;        // 浮层展示的月份（0-11）
  var viewMode = 'day';     // 浮层视图：day / month / year

  /* ==================== 日期工具 ==================== */

  /** 补零 */
  function pad2(n) { return (n < 10 ? '0' : '') + n; }

  /** Date 转 yyyy-MM-dd 文本 */
  function format(date) {
    return date.getFullYear() + '-' + pad2(date.getMonth() + 1) + '-' + pad2(date.getDate());
  }

  /** 宽松解析日期文本：支持 yyyy-MM-dd、yyyy/M/d、yyyy.M.d、yyyyMMdd；非法返回 null */
  function parse(text) {
    if (!text) return null;
    var str = String(text).trim().slice(0, 10);
    var matched = str.match(/^(\d{4})[-\/.](\d{1,2})[-\/.](\d{1,2})$/) || str.match(/^(\d{4})(\d{2})(\d{2})$/);
    if (!matched) return null;
    var year = Number(matched[1]);
    var month = Number(matched[2]);
    var day = Number(matched[3]);
    if (month < 1 || month > 12 || day < 1 || day > 31) return null;
    var date = new Date(year, month - 1, day);
    // 校验溢出日期（如 2 月 30 日会被自动进位，需判定为非法）
    if (date.getFullYear() !== year || date.getMonth() !== month - 1 || date.getDate() !== day) return null;
    return date;
  }

  /** 判断是否同一天 */
  function isSameDay(a, b) {
    return !!a && !!b && a.getFullYear() === b.getFullYear()
      && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
  }

  /** 读取输入框的 min / max 限制（沿用原生属性语义） */
  function limitOf(input, attrName) {
    return input ? parse(input.getAttribute(attrName)) : null;
  }

  /** 判断日期是否超出 min/max 限制 */
  function isOutOfRange(date, min, max) {
    if (min && date < new Date(min.getFullYear(), min.getMonth(), min.getDate())) return true;
    if (max && date > new Date(max.getFullYear(), max.getMonth(), max.getDate())) return true;
    return false;
  }

  /** 箭头图标（单/双向 chevron） */
  function chevron(direction, isDouble) {
    var single = direction === 'left' ? 'M10.5 3.5L6 8l4.5 4.5' : 'M5.5 3.5L10 8l-4.5 4.5';
    var doubled = direction === 'left'
      ? '<path d="M8 3.5L3.5 8 8 12.5"/><path d="M13 3.5L8.5 8l4.5 4.5"/>'
      : '<path d="M3 3.5L7.5 8 3 12.5"/><path d="M8 3.5L12.5 8 8 12.5"/>';
    var inner = isDouble ? doubled : '<path d="' + single + '"/>';
    return '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.7" '
      + 'stroke-linecap="round" stroke-linejoin="round">' + inner + '</svg>';
  }
  /* ==================== 浮层渲染 ==================== */

  /** 创建浮层单例 */
  function createPanel() {
    panel = document.createElement('div');
    panel.className = 'erp-dp-panel';
    panel.setAttribute('role', 'dialog');
    panel.setAttribute('aria-label', '日期选择');
    // 点击浮层时保持输入框聚焦，避免面板因失焦而抖动
    panel.addEventListener('mousedown', function (e) { e.preventDefault(); });
    panel.addEventListener('click', onPanelClick);
    document.body.appendChild(panel);
  }

  /** 渲染浮层（按当前视图分派） */
  function render() {
    if (viewMode === 'month') { renderMonthView(); return; }
    if (viewMode === 'year') { renderYearView(); return; }
    renderDayView();
  }

  /** 页脚（快捷操作），三个视图共用 */
  function footHtml() {
    return '<div class="erp-dp-foot">'
      + '<div class="erp-dp-quick">'
      + '<button type="button" class="erp-dp-link" data-act="today">今天</button>'
      + '<button type="button" class="erp-dp-link" data-act="month-first">本月初</button>'
      + '<button type="button" class="erp-dp-link" data-act="month-last">本月末</button>'
      + '</div>'
      + '<button type="button" class="erp-dp-link is-muted" data-act="clear">清空</button>'
      + '</div>';
  }

  /** 日视图：6 行 × 7 列日期网格 */
  function renderDayView() {
    var selected = parse(activeInput ? activeInput.value : '');
    var today = new Date();
    var min = limitOf(activeInput, 'min');
    var max = limitOf(activeInput, 'max');

    // 计算本月首日在网格中的偏移（周一为起始列）
    var firstDay = new Date(viewYear, viewMonth, 1);
    var offset = (firstDay.getDay() + 6) % 7;
    var gridStart = new Date(viewYear, viewMonth, 1 - offset);

    var cells = '';
    for (var i = 0; i < 42; i++) {
      var date = new Date(gridStart.getFullYear(), gridStart.getMonth(), gridStart.getDate() + i);
      var weekday = date.getDay();
      var classes = ['erp-dp-cell'];
      if (date.getMonth() !== viewMonth) classes.push('is-other');
      if (weekday === 0 || weekday === 6) classes.push('is-weekend');
      if (isSameDay(date, today)) classes.push('is-today');
      if (isSameDay(date, selected)) classes.push('is-selected');
      var disabled = isOutOfRange(date, min, max);
      if (disabled) classes.push('is-disabled');
      cells += '<button type="button" class="' + classes.join(' ') + '"'
        + (disabled ? ' disabled' : ' data-act="pick-day" data-date="' + format(date) + '"')
        + '>' + date.getDate() + '</button>';
    }

    panel.innerHTML = ''
      + '<div class="erp-dp-head">'
      + '<button type="button" class="erp-dp-nav" data-act="prev-year" title="上一年">' + chevron('left', true) + '</button>'
      + '<button type="button" class="erp-dp-nav" data-act="prev-month" title="上一月">' + chevron('left') + '</button>'
      + '<button type="button" class="erp-dp-title" data-act="to-month" title="选择月份">'
      + viewYear + ' 年 ' + (viewMonth + 1) + ' 月</button>'
      + '<button type="button" class="erp-dp-nav" data-act="next-month" title="下一月">' + chevron('right') + '</button>'
      + '<button type="button" class="erp-dp-nav" data-act="next-year" title="下一年">' + chevron('right', true) + '</button>'
      + '</div>'
      + '<div class="erp-dp-weeks">' + WEEK_LABELS.map(function (w) { return '<span>' + w + '</span>'; }).join('') + '</div>'
      + '<div class="erp-dp-grid">' + cells + '</div>'
      + footHtml();
  }

  /** 月视图：3 列 × 4 行月份网格 */
  function renderMonthView() {
    var selected = parse(activeInput ? activeInput.value : '');
    var today = new Date();
    var cells = MONTH_LABELS.map(function (label, index) {
      var classes = ['erp-dp-cell'];
      if (selected && selected.getFullYear() === viewYear && selected.getMonth() === index) classes.push('is-selected');
      else if (today.getFullYear() === viewYear && today.getMonth() === index) classes.push('is-today');
      return '<button type="button" class="' + classes.join(' ') + '" data-act="pick-month" data-month="' + index + '">'
        + label + '</button>';
    }).join('');

    panel.innerHTML = ''
      + '<div class="erp-dp-head">'
      + '<button type="button" class="erp-dp-nav" data-act="prev-year" title="上一年">' + chevron('left') + '</button>'
      + '<button type="button" class="erp-dp-title" data-act="to-year" title="选择年份">' + viewYear + ' 年</button>'
      + '<button type="button" class="erp-dp-nav" data-act="next-year" title="下一年">' + chevron('right') + '</button>'
      + '</div>'
      + '<div class="erp-dp-grid is-block">' + cells + '</div>'
      + footHtml();
  }

  /** 年视图：一屏 12 年 */
  function renderYearView() {
    var selected = parse(activeInput ? activeInput.value : '');
    var today = new Date();
    var startYear = viewYear - (viewYear % 12);
    var cells = '';
    for (var i = 0; i < 12; i++) {
      var year = startYear + i;
      var classes = ['erp-dp-cell'];
      if (selected && selected.getFullYear() === year) classes.push('is-selected');
      else if (today.getFullYear() === year) classes.push('is-today');
      cells += '<button type="button" class="' + classes.join(' ') + '" data-act="pick-year" data-year="' + year + '">'
        + year + '</button>';
    }

    panel.innerHTML = ''
      + '<div class="erp-dp-head">'
      + '<button type="button" class="erp-dp-nav" data-act="prev-page" title="前 12 年">' + chevron('left') + '</button>'
      + '<button type="button" class="erp-dp-title">' + startYear + ' - ' + (startYear + 11) + '</button>'
      + '<button type="button" class="erp-dp-nav" data-act="next-page" title="后 12 年">' + chevron('right') + '</button>'
      + '</div>'
      + '<div class="erp-dp-grid is-block">' + cells + '</div>'
      + footHtml();
  }
  /* ==================== 交互事件 ==================== */

  /** 浮层点击事件（事件委托，按 data-act 分派） */
  function onPanelClick(e) {
    var target = e.target.closest ? e.target.closest('[data-act]') : null;
    if (!target || !panel.contains(target)) return;
    var act = target.getAttribute('data-act');

    switch (act) {
      case 'prev-year': shiftYear(-1); break;
      case 'next-year': shiftYear(1); break;
      case 'prev-month': shiftMonth(-1); break;
      case 'next-month': shiftMonth(1); break;
      case 'prev-page': shiftYear(-12); break;
      case 'next-page': shiftYear(12); break;
      case 'to-month': viewMode = 'month'; render(); reposition(); break;
      case 'to-year': viewMode = 'year'; render(); reposition(); break;
      case 'pick-year':
        viewYear = Number(target.getAttribute('data-year'));
        viewMode = 'month'; render(); reposition(); break;
      case 'pick-month':
        viewMonth = Number(target.getAttribute('data-month'));
        viewMode = 'day'; render(); reposition(); break;
      case 'pick-day':
        apply(target.getAttribute('data-date'));
        close();
        break;
      case 'today':
        pickShortcut(new Date());
        break;
      case 'month-first':
        pickShortcut(new Date(viewYear, viewMonth, 1));
        break;
      case 'month-last':
        pickShortcut(new Date(viewYear, viewMonth + 1, 0));
        break;
      case 'clear':
        apply('');
        close();
        break;
      default: break;
    }
  }

  /** 快捷选择（受 min/max 限制约束） */
  function pickShortcut(date) {
    if (isOutOfRange(date, limitOf(activeInput, 'min'), limitOf(activeInput, 'max'))) return;
    apply(format(date));
    close();
  }

  /** 切换展示年份 */
  function shiftYear(step) {
    viewYear += step;
    render();
    reposition();
  }

  /** 切换展示月份（跨年自动进位） */
  function shiftMonth(step) {
    var date = new Date(viewYear, viewMonth + step, 1);
    viewYear = date.getFullYear();
    viewMonth = date.getMonth();
    render();
    reposition();
  }

  /** 写回输入框并派发事件，保证依赖 change/input 的业务逻辑正常触发 */
  function apply(value) {
    if (!activeInput) return;
    activeInput.value = value;
    activeInput.dataset.erpDpLast = value;
    activeInput.dispatchEvent(new Event('input', { bubbles: true }));
    activeInput.dispatchEvent(new Event('change', { bubbles: true }));
  }

  /** 浮层定位：优先输入框下方，空间不足时翻转到上方；宽度跟随输入框保持一致 */
  function reposition() {
    if (!activeInput || !panel) return;
    var rect = activeInput.getBoundingClientRect();
    // 输入框已滚出可视区域则收起浮层
    if (rect.bottom < 0 || rect.top > window.innerHeight) { close(); return; }
    // 面板宽度与输入框宽度一致（设 220px 下限，保证 7 列日期网格内容可读）
    panel.style.width = Math.max(rect.width, 220) + 'px';
    var width = panel.offsetWidth;
    var height = panel.offsetHeight;
    var top = rect.bottom + 6;
    var left = rect.left;
    if (top + height > window.innerHeight - 8) {
      var flipped = rect.top - height - 6;
      top = flipped >= 8 ? flipped : Math.max(8, window.innerHeight - height - 8);
    }
    if (left + width > window.innerWidth - 8) left = Math.max(8, window.innerWidth - width - 8);
    panel.style.top = Math.round(top) + 'px';
    panel.style.left = Math.round(left) + 'px';
  }

  /** 打开浮层 */
  function open(input) {
    if (!panel) createPanel();
    if (input.disabled || input.readOnly) return;
    if (activeInput && activeInput !== input) activeInput.classList.remove('is-active');
    activeInput = input;
    activeInput.classList.add('is-active');
    var current = parse(input.value) || new Date();
    viewYear = current.getFullYear();
    viewMonth = current.getMonth();
    viewMode = 'day';
    panel.classList.add('is-open');
    render();
    reposition();
  }

  /** 收起浮层 */
  function close() {
    if (!panel) return;
    panel.classList.remove('is-open');
    if (activeInput) activeInput.classList.remove('is-active');
    activeInput = null;
  }

  /** 判断浮层是否处于打开状态 */
  function isOpen() {
    return !!panel && panel.classList.contains('is-open');
  }
  /* ==================== 输入框接管 ==================== */

  /** 接管单个日期输入框 */
  function enhance(input) {
    if (!input || input.dataset[FLAG] === '1') return;
    input.dataset[FLAG] = '1';
    // 切换为文本类型以屏蔽浏览器原生日期面板（value 仍是 yyyy-MM-dd）
    if ((input.getAttribute('type') || '').toLowerCase() === 'date') input.setAttribute('type', 'text');
    input.classList.add('erp-date-input');
    input.setAttribute('autocomplete', 'off');
    input.setAttribute('inputmode', 'numeric');
    if (!input.getAttribute('placeholder')) input.setAttribute('placeholder', '年-月-日');
    input.dataset.erpDpLast = input.value || '';

    input.addEventListener('focus', function () { open(input); });
    input.addEventListener('click', function () { if (activeInput !== input) open(input); });
    input.addEventListener('keydown', onInputKeydown);
    input.addEventListener('blur', function () { normalize(input); });
  }

  /** 键盘操作：ESC 收起、回车确认、上下键增减一天、翻页键增减一月 */
  function onInputKeydown(e) {
    var input = e.currentTarget;
    var base = parse(input.value) || new Date();
    var step = 0;
    if (e.key === 'Escape') { close(); return; }
    if (e.key === 'Enter') {
      e.preventDefault();
      normalize(input);
      close();
      return;
    }
    if (e.key === 'ArrowUp') step = 1;
    else if (e.key === 'ArrowDown') step = -1;
    else if (e.key === 'PageUp') step = 30;
    else if (e.key === 'PageDown') step = -30;
    else return;

    e.preventDefault();
    var next = new Date(base.getFullYear(), base.getMonth(), base.getDate() + step);
    if (isOutOfRange(next, limitOf(input, 'min'), limitOf(input, 'max'))) return;
    if (activeInput !== input) open(input);
    apply(format(next));
    viewYear = next.getFullYear();
    viewMonth = next.getMonth();
    viewMode = 'day';
    render();
    reposition();
  }

  /** 失焦时规范化手工录入的内容：合法则补齐格式，非法则还原上次有效值 */
  function normalize(input) {
    if (input.value === '') { input.dataset.erpDpLast = ''; return; }
    var date = parse(input.value);
    if (date) {
      input.value = format(date);
      input.dataset.erpDpLast = input.value;
    } else {
      input.value = input.dataset.erpDpLast || '';
    }
  }

  /** 批量接管指定范围内的日期输入框（含节点自身） */
  function enhanceAll(root) {
    var scope = root && root.querySelectorAll ? root : document;
    if (scope.matches && scope.matches('input[type="date"]')) enhance(scope);
    var list = scope.querySelectorAll('input[type="date"]');
    for (var i = 0; i < list.length; i++) enhance(list[i]);
  }

  /* ==================== 全局事件与初始化 ==================== */

  /** 点击浮层与输入框之外的区域时收起 */
  function onDocumentMouseDown(e) {
    if (!isOpen()) return;
    if (panel.contains(e.target) || e.target === activeInput) return;
    close();
  }

  function init() {
    enhanceAll(document);

    // 动态渲染的表单（弹窗、侧滑面板、报表工具栏）自动接管
    var observer = new MutationObserver(function (records) {
      for (var i = 0; i < records.length; i++) {
        var added = records[i].addedNodes;
        for (var j = 0; j < added.length; j++) {
          if (added[j].nodeType === 1) enhanceAll(added[j]);
        }
      }
    });
    observer.observe(document.body, { childList: true, subtree: true });

    document.addEventListener('mousedown', onDocumentMouseDown, true);
    document.addEventListener('keydown', function (e) { if (e.key === 'Escape') close(); });
    // 容器滚动（侧滑面板/弹窗内部滚动）与窗口尺寸变化时重新定位
    window.addEventListener('scroll', function () { if (isOpen()) reposition(); }, true);
    window.addEventListener('resize', function () { if (isOpen()) reposition(); });
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
  else init();

  // 对外暴露少量方法，便于业务代码手动接管或联动
  window.ErpDatePicker = {
    enhance: enhance,
    enhanceAll: enhanceAll,
    open: open,
    close: close,
    format: format,
    parse: parse
  };
})();
