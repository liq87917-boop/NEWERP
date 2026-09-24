/* ============================================================
   ========== 商品图片库（只读，图片位 1~3）— ERP-039 ==========
   ============================================================
   定位：在既有商品资料的三个图片位（image1 / image2 / image3）之上提供只读浏览与筛选。
   边界（界面侧同样遵守）：
     1. 只调用 GET /api/base/product-images（只读端点，无任何写接口）；
     2. 不上传 / 覆盖 / 删除 OSS 对象，不读取存储凭据，也不改写商品图片字段；
     3. 服务端不抓取任何图片地址（不做存在性探测），浏览器按返回地址渲染；
     4. 只有「本站相对路径」与「HTTP(S) 绝对地址」会渲染成图片，其他引用只作不可用文本展示；
     5. 引用对象缺失 / 已删除（404）由 img onerror 显示安全占位，不影响其余商品渲染。
   文案与服务端 ProductImageRules.ReferenceStateText / FillStateText 保持一致（此处仅兜底显示）。
   ============================================================ */

const PIL_STATE_LABELS = {
  empty: '未维护图片',
  local: '本站相对路径（可渲染）',
  http: 'HTTP(S) 绝对地址（可渲染）',
  unsafe_scheme: '不可用（不安全的协议）',
  unsafe_markup: '不可用（可疑标记或控制字符）',
  unsupported: '不可用（无法安全渲染的地址）',
  too_long: '不可用（超出字段长度）',
};

const PIL_FILL_LABELS = { full: '全部填充', partial: '部分填充', none: '无图片' };

/* 每页条数选项（服务端上限 200，超出由服务端按上限截断） */
const PIL_PAGE_SIZES = [24, 48, 96, 200];

/* 页面状态：聚焦商品（来自商品列表「更多 → 图片库」行操作）与最近一次结果 */
let PIL_PRODUCT_ID = null;
let PIL_LAST = null;

/* 打开图片库页面（无参会打开全部商品；商品列表行操作会传入商品 Id） */
function openProductImageLibrary(productId) {
  PIL_PRODUCT_ID = (productId === undefined || productId === null || productId === '')
    ? null : Number(productId);
  PIL_LAST = null;
  CURRENT_PAGE_CODE = 'product-images';
  document.getElementById('header-title').textContent = '商品图片库（只读）';
  document.getElementById('content').innerHTML = `
    <div class="page-hero report-hero">
      <h2>🖼 商品图片库（只读）</h2>
      <p>复用商品资料已持久化的图片位 1~3 · 不上传 / 覆盖 / 删除 OSS 对象 · 服务端不抓取图片地址、不读取存储凭据、不改写商品图片字段</p>
    </div>

    <div class="toolbar">
      <div class="toolbar-left" style="flex-wrap:wrap;gap:8px;align-items:center">
        <label>商品关键字
          <input type="text" id="pil-keyword" style="width:200px" placeholder="商品编码 / 名称"
            onkeydown="if(event.key==='Enter')loadProductImageLibrary(1)">
        </label>
        <label>图片填充
          <select id="pil-image-state" style="min-width:215px">
            <option value="all">全部商品（不限图片填充）</option>
            <option value="has">有图片引用（全部填充 + 部分填充）</option>
            <option value="full">三个图片位都已填充</option>
            <option value="partial">部分填充（1~2 个图片位）</option>
            <option value="none">无图片引用</option>
          </select>
        </label>
        <label>商品状态
          <select id="pil-status" style="min-width:110px">
            <option value="">不限</option>
            <option value="1">仅启用</option>
            <option value="0">仅停用</option>
          </select>
        </label>
        <label>每页
          <select id="pil-pagesize">${PIL_PAGE_SIZES.map(n => `<option value="${n}">${n}</option>`).join('')}</select> 条
        </label>
        <span id="pil-focus" class="text-muted"></span>
      </div>
      <div class="toolbar-actions">
        <button class="btn btn-primary" onclick="loadProductImageLibrary(1)">查询</button>
        <button class="btn btn-neutral" onclick="pilResetFilters()">重置筛选</button>
      </div>
    </div>

    <div class="kpi-grid" id="pil-kpi"></div>
    <div id="pil-grid" style="display:grid;grid-template-columns:repeat(auto-fill,minmax(330px,1fr));gap:12px">
      <div class="skeleton-row"></div><div class="skeleton-row"></div>
    </div>
    <div class="pd-hint" id="pil-rule"></div>
    <div class="pagination" id="pil-pagination"></div>`;

  pilRenderFocus();
  loadProductImageLibrary(1);
}

/* 聚焦商品提示（行操作进入时显示；可一键取消只看该商品） */
function pilRenderFocus() {
  const el = document.getElementById('pil-focus');
  if (!el) return;
  if (!PIL_PRODUCT_ID) { el.innerHTML = ''; return; }
  el.innerHTML = `已锁定商品 #${PIL_PRODUCT_ID}
    <button class="btn btn-neutral btn-sm" onclick="pilClearFocus()" title="取消商品锁定（改为浏览全部商品）">× 取消</button>`;
}

/* 取消商品锁定并重新查询（只改筛选，不改任何数据） */
function pilClearFocus() {
  PIL_PRODUCT_ID = null;
  pilRenderFocus();
  loadProductImageLibrary(1);
}

/* 重置筛选（保留当前商品锁定状态），只重新查询、不写任何数据 */
function pilResetFilters() {
  const kw = document.getElementById('pil-keyword');
  const state = document.getElementById('pil-image-state');
  const status = document.getElementById('pil-status');
  if (kw) kw.value = '';
  if (state) state.value = 'all';
  if (status) status.value = '';
  loadProductImageLibrary(1);
}

/* 查询参数：全部由页面筛选控件组装（留空即不传，由服务端取默认值 / 按「不限」处理） */
function pilQuery(page) {
  const val = id => { const el = document.getElementById(id); return el ? el.value.trim() : ''; };
  const q = new URLSearchParams();
  if (val('pil-keyword')) q.set('keyword', val('pil-keyword'));
  if (PIL_PRODUCT_ID) q.set('productId', PIL_PRODUCT_ID);
  if (val('pil-status')) q.set('status', val('pil-status'));
  q.set('imageState', val('pil-image-state') || 'all');
  q.set('page', page || 1);
  q.set('pageSize', val('pil-pagesize') || String(PIL_PAGE_SIZES[0]));
  return q.toString();
}
/* 加载图片库（只读 GET；失败时只替换本区域内容，不影响页面其余部分） */
async function loadProductImageLibrary(page) {
  const grid = document.getElementById('pil-grid');
  if (!grid) return;
  grid.innerHTML = '<div class="skeleton-row"></div><div class="skeleton-row"></div>';
  try {
    const data = await api('/api/base/product-images?' + pilQuery(page));
    PIL_LAST = data;
    pilRenderKpi(data);
    pilRenderGrid(data);
    pilRenderRule(data);
    pilRenderPagination(data);
  } catch (e) {
    grid.innerHTML = `<div class="empty"><div style="font-size:48px">⚠️</div><div>图片库加载失败：${escapeHtml(e.message)}</div></div>`;
  }
}

/* 本页计数（合计只统计本页，与服务端 scopeNote 口径一致） */
function pilRenderKpi(data) {
  const el = document.getElementById('pil-kpi');
  if (!el) return;
  el.innerHTML = `
    <div class="kpi-card cargo">
      <div class="kpi-label">符合筛选的商品</div>
      <div class="kpi-value">${Number(data.total || 0)}<span class="unit">个</span></div>
      <div class="kpi-delta flat">本页 ${(data.items || []).length} 个 · 第 ${data.page}/${data.totalPages} 页 · ${escapeHtml(data.imageStateText || '')}</div>
    </div>
    <div class="kpi-card gold">
      <div class="kpi-label">本页图片位填充</div>
      <div class="kpi-value" style="font-size:16px">全部 ${Number(data.fullFillCount || 0)} · 部分 ${Number(data.partialFillCount || 0)} · 无图片 ${Number(data.noImageCount || 0)}</div>
      <div class="kpi-delta flat">三张图片位：有引用 ${Number(data.populatedSlotCount || 0)} 个（其中可渲染 ${Number(data.renderableSlotCount || 0)} 个）</div>
    </div>
    <div class="kpi-card lc">
      <div class="kpi-label">本页不可用引用</div>
      <div class="kpi-value">${Number(data.unusableReferenceCount || 0)}<span class="unit">个</span></div>
      <div class="kpi-delta flat">涉及 ${Number(data.unusableProductCount || 0)} 个商品：不安全协议 / 可疑标记 / 无法安全渲染 / 超长的引用不渲染，仅在卡片上作文本展示</div>
    </div>`;
}

/* 商品卡片网格：商品编码 / 名称 / 规格 + 三个图片位（未维护与不可用都能一眼区分） */
function pilRenderGrid(data) {
  const el = document.getElementById('pil-grid');
  if (!el) return;
  const items = data.items || [];
  if (!items.length) {
    el.innerHTML = '<div class="card empty" style="grid-column:1/-1">没有符合条件的商品（可清空关键字、放开图片填充 / 状态筛选后重试）</div>';
    return;
  }
  el.innerHTML = items.map(pilCardHtml).join('');
}

function pilFillClass(fillState) {
  if (fillState === 'full') return 'status-success';
  if (fillState === 'partial') return 'status-warning';
  return 'status-neutral';
}

/* 单张商品卡片 */
function pilCardHtml(row) {
  const filled = Number(row.populatedSlotCount || 0);
  const renderable = Number(row.renderableSlotCount || 0);
  const unusable = Number(row.unusableReferenceCount || 0);
  const fillText = row.fillStateText || PIL_FILL_LABELS[row.fillState] || row.fillState || '';
  const slots = (row.images || []).map(pilSlotHtml).join('');
  const unusableLine = unusable > 0
    ? `<div style="color:#b42318">不可用 ${unusable} 个（不渲染，仅作文本展示）</div>`
    : '<div>不可用 0 个</div>';
  return `<div class="card" style="margin:0;padding:12px">
    <div style="display:flex;justify-content:space-between;gap:8px;align-items:flex-start">
      <div>
        <div style="font-weight:600">${escapeHtml(row.productCode || '')} ${escapeHtml(row.productName || '')}</div>
        <div class="text-muted">规格：${escapeHtml(row.spec || '—')} · 单位：${escapeHtml(row.unit || '—')} · 分类：${escapeHtml(row.category || '未分类')}</div>
      </div>
      <div style="text-align:right;white-space:nowrap">
        <span class="status ${Number(row.status) === 1 ? 'status-info' : 'status-neutral'}">${escapeHtml(row.statusText || '')}</span>
        <div style="margin-top:4px"><span class="status ${pilFillClass(row.fillState)}">${escapeHtml(fillText)}</span></div>
      </div>
    </div>
    <div style="display:grid;grid-template-columns:repeat(3,1fr);gap:6px;margin-top:8px">${slots}</div>
    <div class="text-muted" style="margin-top:6px">图片位 ${filled}/3 有引用 · 可渲染 ${renderable} 个</div>
    <div class="text-muted">${unusableLine}</div>
    ${row.note ? `<div class="text-muted">说明：${escapeHtml(row.note)}</div>` : ''}
  </div>`;
}
/* 单个图片位：可渲染 → img（加载失败转占位）；不可渲染 → 占位 + 原值纯文本（绝不当作标记执行） */
function pilSlotHtml(slot) {
  const label = escapeHtml(slot.slotLabel || ('图片 ' + slot.slot));
  const stateText = escapeHtml(slot.stateText || PIL_STATE_LABELS[slot.state] || slot.state || '');
  const reference = escapeHtml(slot.reference || '');
  if (slot.renderable && slot.source) {
    return `<div class="pil-slot">
      <img src="${escapeHtml(slot.source)}" alt="${label}" loading="lazy" referrerpolicy="no-referrer"
        style="width:100%;height:96px;object-fit:cover;border-radius:6px;background:#f5f6f8;cursor:zoom-in"
        onclick="showLightbox(this.src)" onerror="pilImageError(this)">
      <div class="text-muted" style="margin-top:4px">${label} · ${stateText}</div>
    </div>`;
  }

  const placeholder = escapeHtml(slot.placeholderText || '图片引用不可用');
  const referenceLine = reference
    ? `<div class="text-muted" style="margin-top:4px;word-break:break-all">存储值：<code>${reference}</code></div>`
    : '';
  return `<div class="pil-slot">
    <div style="height:96px;display:flex;align-items:center;justify-content:center;text-align:center;border:1px dashed #d0d5dd;border-radius:6px;background:#fafbfc;color:#98a2b3;padding:4px">${placeholder}</div>
    <div class="text-muted" style="margin-top:4px">${label} · ${stateText}</div>
    ${referenceLine}
  </div>`;
}

/* 加载失败（对象不存在 / 已删除 / 不可访问）→ 只替换当前图片位为安全占位，不影响其余商品与图片位 */
function pilImageError(img) {
  const note = document.createElement('div');
  note.style.cssText = 'height:96px;display:flex;align-items:center;justify-content:center;text-align:center;'
    + 'border:1px dashed #f0b4b4;border-radius:6px;background:#fdf3f3;color:#b42318;padding:4px';
  note.textContent = '图片加载失败（对象不存在或不可访问）';
  img.replaceWith(note);
}

/* 口径说明（渲染安全 + 只读边界 + 统计范围，与服务端返回文案同源） */
function pilRenderRule(data) {
  const el = document.getElementById('pil-rule');
  if (!el) return;
  el.textContent = '口径：' + (data.rule || '') + ' ' + (data.readOnlyRule || '') + ' ' + (data.scopeNote || '');
}

/* 分页（有界：服务端每页上限 200） */
function pilRenderPagination(data) {
  const el = document.getElementById('pil-pagination');
  if (!el) return;
  const page = Number(data.page || 1);
  const totalPages = Number(data.totalPages || 1);
  el.innerHTML = `
    <button class="btn btn-neutral btn-sm" ${page <= 1 ? 'disabled' : ''} onclick="loadProductImageLibrary(${page - 1})">上一页</button>
    <span style="margin:0 10px">第 ${page} / ${totalPages} 页（共 ${Number(data.total || 0)} 个商品）</span>
    <button class="btn btn-neutral btn-sm" ${page >= totalPages ? 'disabled' : ''} onclick="loadProductImageLibrary(${page + 1})">下一页</button>`;
}
