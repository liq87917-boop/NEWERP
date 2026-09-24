/* ============ 单据编辑页（独立页面：主表 + 副表明细） ============ */
let BILL_EDIT_OID = 0;

/* 副表（明细）通用字段 */
const DETAIL_COLUMNS = [
  { key: 'ProductId', label: '商品', type: 'product' },
  { key: 'ProductName', label: '商品名称', readonly: true },
  { key: 'Spec', label: '规格', readonly: true },
  { key: 'Quantity', label: '数量', type: 'number' },
  { key: 'Unit', label: '单位', readonly: true },
  { key: 'UnitPrice', label: '单价', type: 'number' },
  { key: 'Amount', label: '金额', type: 'number' },
];

/* 引用字段映射（字段 key -> 基础资料类型） */
const REF_FIELD_MAP = {
  CustId: 'customer', CustomerId: 'customer', SupplierId: 'supplier',
  EmpId: 'employee',
};

/* 基础资料接口配置 */
const REF_APIS = {
  customer: { api: '/api/base/customers', nameKey: 'customerName' },
  supplier: { api: '/api/base/suppliers', nameKey: 'supplierName' },
  employee: { api: '/api/base/employees', nameKey: 'employeeName' },
  product: { api: '/api/base/products', nameKey: 'productName' },
  warehouse: { api: '/api/base/warehouses', nameKey: 'warehouseName' },
  /* ERP-036：客户「指定货代」—— 选项接口只返回启用中、未删除的 Forwarder 字典项（数组形态，见 searchRef）。
     rowKey / availableKey：编辑回显使用列表 / 详情已返回的名称快照与可用性标注，
     字典项后来被停用 / 删除时仍显示当时的名称并标注「已停用/不可用」，而不是静默变空。
     clearOnEmpty：清空搜索框（且不重新选择）即视为清空指定货代，客户资料允许留空。 */
  forwarder: {
    api: '/api/base/customers/forwarder-options', nameKey: 'infoName',
    rowKey: 'forwarderName', availableKey: 'forwarderAvailable', clearOnEmpty: true,
  },
};

function escapeHtml(s) { return String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])); }

/* ============ Bill Hero chips：识别外贸行业字段并渲染对应徽章 ============ */
/* 规则：key 含 Currency/Incoterm/Port/Container 等关键词时自动适配对应 chip 类型 */
const HERO_CHIP_RULES = [
  { match: /Currency/i,                 type: 'currency', valueOf: v => String(v || '').toLowerCase() },
  { match: /Incoterm|TradeTerm|TrdTerm/i, type: 'incoterm', valueOf: v => String(v || '').toLowerCase() },
  { match: /LoadingPort|PortOfLoading|LoadPort/i,     type: 'port',    valueOf: v => String(v || '') },
  { match: /DischargePort|PortOfDischarge|DestPort/i, type: 'port',    valueOf: v => String(v || '') },
  { match: /Port(?![A-Z])/i,                       type: 'port',    valueOf: v => String(v || '') },
  { match: /ContainerType|Container/i,  type: 'container', valueOf: v => String(v || '').toLowerCase() },
  /* 义乌行业扩展 */
  { match: /HSCode|HS_Code|HsCode/i,                       type: 'hs',      valueOf: v => String(v || '') },
  { match: /ProductCategory|Category|GoodsType/i,          type: 'category', valueOf: v => String(v || '').toLowerCase() },
  { match: /CustomerRegion|Country|Region/i,               type: 'region',  valueOf: v => String(v || '').toLowerCase() },
  { match: /TransportMode|ShippingMode|LogisticsChannel/i, type: 'transport', valueOf: v => String(v || '').toLowerCase() },
  { match: /PaymentTerm|PayTerm|PaymentMethod/i,           type: 'payment', valueOf: v => String(v || '').toLowerCase() },
  { match: /Certificate|Cert/i,                            type: 'cert',    valueOf: v => String(v || '').toLowerCase() },
];
const CONTAINER_VARIANT = { '20gp': 'ft20', '20ft': 'ft20', '20': 'ft20', 'ft20': 'ft20',
                            '40gp': 'ft40', '40ft': 'ft40', '40': 'ft40', 'ft40': 'ft40',
                            '40hc': 'ft40hc', '40hq': 'ft40hc', 'ft40hc': 'ft40hc',
                            '45hc': 'ft45hc', '45hq': 'ft45hc', 'ft45hc': 'ft45hc' };

/* 义乌 8 大品类映射（字段值 -> emoji + 文案） */
const CATEGORY_MAP = {
  'accessory': '🎀 饰品配件', 'jewelry': '💍 珠宝首饰', 'toy': '🧸 玩具公仔', 'sock': '🧦 袜品针织',
  'stationery': '📚 文具办公', 'bag': '👜 箱包皮具', 'hardware': '🔧 五金电器', 'craft': '🎨 工艺品',
  'cosmetic': '💄 美妆工具', 'xmas': '🎄 圣诞用品', 'electronic': '📱 数码配件', 'sport': '⚽ 运动器材',
  'kitchen': '🍴 厨房用品', 'pet': '🐾 宠物用品', 'auto': '🚗 汽车用品', 'baby': '👶 母婴用品',
};
/* 义乌出口区域（按国旗 emoji + 名称） */
const REGION_MAP = {
  'saudi': '🇸🇦 沙特', 'uae': '🇦🇪 阿联酋', 'iraq': '🇮🇶 伊拉克', 'iran': '🇮🇷 伊朗', 'egypt': '🇪🇬 埃及',
  'turkey': '🇹🇷 土耳其', 'nigeria': '🇳🇬 尼日利亚', 'kenya': '🇰🇪 肯尼亚', 'ghana': '🇬🇭 加纳',
  'usa': '🇺🇸 美国', 'uk': '🇬🇧 英国', 'germany': '🇩🇪 德国', 'france': '🇫🇷 法国', 'italy': '🇮🇹 意大利',
  'spain': '🇪🇸 西班牙', 'russia': '🇷🇺 俄罗斯', 'brazil': '🇧🇷 巴西', 'mexico': '🇲🇽 墨西哥',
  'vietnam': '🇻🇳 越南', 'thailand': '🇹🇭 泰国', 'indonesia': '🇮🇩 印尼', 'philippines': '🇵🇭 菲律宾',
  'malaysia': '🇲🇾 马来西亚', 'korea': '🇰🇷 韩国', 'japan': '🇯🇵 日本',
  'middleeast': '🕌 中东', 'africa': '🌍 非洲', 'europe': '🇪🇺 欧洲', 'america': '🌎 美洲', 'asia': '🌏 亚洲',
};
/* 多式联运通道 */
const TRANSPORT_MAP = {
  'ocean': '🚢 海运 FCL', 'lcl': '📦 海运 LCL 拼箱', 'air': '✈️ 国际空运', 'train': '🚂 中欧班列',
  'express': '📮 国际快递', 'dhl': '📮 DHL', 'fedex': '📮 FedEx', 'ups': '📮 UPS',
  'multimodal': '🔀 多式联运',
};
/* 付款方式 */
const PAYMENT_MAP = {
  'tt': '🏦 T/T 电汇', 'lc': '📜 L/C 信用证', 'oa': '📅 O/A 赊销', 'dp': '📋 D/P 付款交单',
  'da': '📋 D/A 承兑交单', 'western': '💵 西联汇款', 'paypal': '💳 PayPal', 'alipay': '💎 支付宝国际',
  'aliexpress': '🛒 速卖通', 'amazon': '📦 Amazon Pay',
};
/* 行业认证 */
const CERT_MAP = {
  'ce': '✅ CE 欧盟', 'fda': '✅ FDA 美国', 'ccc': '✅ CCC 中国', 'iso9001': '✅ ISO 9001',
  'iso14001': '✅ ISO 14001', 'fcc': '✅ FCC 美国', 'rohs': '✅ RoHS 欧盟', 'co': '📜 原产地证 CO',
  'fa': '🚔 熏蒸证书', 'coc': '📜 质量证书', 'sa': '🔬 安全检测',
};

function heroChipsHtml(cfg) {
  if (!cfg || !cfg.fields) return '';
  const placeholders = [];
  cfg.fields.forEach(f => {
    for (const rule of HERO_CHIP_RULES) {
      if (rule.match.test(f.key)) {
        const def = f.default != null ? f.default : '';
        const cls = rule.type;
        const label = chipLabel(rule.type, def);
        placeholders.push(`<span class="chip-yiwu chip-yiwu-${cls} ${rule.valueOf(def)}" data-chip-key="${f.key}" data-chip-type="${cls}" title="字段：${f.label}">${label}</span>`);
        break;
      }
    }
  });
  return placeholders.join('');
}

function chipLabel(type, v) {
  const val = String(v || '').trim();
  if (!val) {
    const placeholder = {
      currency: '💱 币种 --', incoterm: '📜 贸易术语 --', port: '⚓ 港口 --', container: '📦 集装箱 --',
      hs: '🏷️ HS Code --', category: '🎁 品类 --', region: '🌍 区域 --',
      transport: '🚢 运输 --', payment: '💳 付款 --', cert: '✅ 认证 --',
    };
    return placeholder[type] || `${val.toUpperCase()} --`;
  }
  if (type === 'container') {
    return `${val.toUpperCase()}`;
  }
  if (type === 'currency') return val.toUpperCase();
  if (type === 'incoterm') return val.toUpperCase();
  if (type === 'port')     return val.toUpperCase();
  if (type === 'hs')       return val;
  if (type === 'category') return CATEGORY_MAP[val.toLowerCase()] || val;
  if (type === 'region')   return REGION_MAP[val.toLowerCase()]   || val.toUpperCase();
  if (type === 'transport')return TRANSPORT_MAP[val.toLowerCase()]|| val.toUpperCase();
  if (type === 'payment')  return PAYMENT_MAP[val.toLowerCase()]  || val.toUpperCase();
  if (type === 'cert')     return CERT_MAP[val.toLowerCase()]     || val.toUpperCase();
  return val.toUpperCase();
}

/* 主表字段 onChange 时实时更新 BillHero chip（chip 显示当前字段值） */
function updateBillHeroChips() {
  const wrap = document.getElementById('bill-hero-chips');
  if (!wrap) return;
  wrap.querySelectorAll('[data-chip-key]').forEach(el => {
    const key = el.dataset.chipKey;
    const type = el.dataset.chipType;
    const input = document.getElementById('f_' + key);
    if (!input) return;
    const v = input.value || '';
    const cls = type === 'container' ? `chip-yiwu chip-yiwu-container ${CONTAINER_VARIANT[v.toLowerCase()] || 'ft40'}` :
                type === 'currency'  ? `chip-yiwu chip-yiwu-currency ${v.toLowerCase()}` :
                `chip-yiwu chip-yiwu-${type}`;
    el.className = cls.trim();
    el.textContent = chipLabel(type, v);
  });
}

/* 渲染引用字段（模糊匹配搜索框） */
function renderRefField(f, refType) {
  return `<div class="form-item">
    <label>${f.label}</label>
    <div class="ref-select">
      <input type="text" id="f_${f.key}_search" placeholder="输入关键字搜索..." autocomplete="off" oninput="searchRef(this, '${f.key}', '${refType}')">
      <input type="hidden" id="f_${f.key}" value="">
      <div class="ref-dropdown" id="f_${f.key}_dd"></div>
    </div>
  </div>`;
}

/* 渲染只读引用字段（如业务员 EmpId，自动带出不可编辑） */
function renderRefFieldReadonly(f) {
  return `<div class="form-item">
    <label>${f.label}</label>
    <input type="text" id="f_${f.key}_search" value="" readonly placeholder="选择客户后自动带出" style="background:#f8fafc;cursor:not-allowed">
    <input type="hidden" id="f_${f.key}" value="">
  </div>`;
}

/* 搜索引用字段（模糊匹配）
   - 分页型基础资料接口（/api/base/customers 等）：按关键字服务端过滤，取前 10 条
   - 选项型接口（直接返回数组，如客户指定货代选项）：本地点过滤，天然只含当前可选用的字典项
   - ref.clearOnEmpty 为真时：清空搜索框即视为清空该引用（用于客户指定货代这类可留空字段） */
async function searchRef(input, key, refType) {
  const ref = REF_APIS[refType];
  const kw = input.value.trim();
  const dd = document.getElementById(`f_${key}_dd`);
  if (!kw) {
    dd.style.display = 'none';
    if (ref.clearOnEmpty) document.getElementById(`f_${key}`).value = '';
    return;
  }
  try {
    const data = await api(`${ref.api}?page=1&pageSize=10&keyword=${encodeURIComponent(kw)}`);
    const items = Array.isArray(data)
      ? data.filter(x => String(x[ref.nameKey] ?? '').toLowerCase().includes(kw.toLowerCase())).slice(0, 10)
      : (data.items || []);
    dd.innerHTML = items.map(item =>
      `<div class="ref-item" onclick="selectRef('${key}', ${item.id}, '${escapeHtml(item[ref.nameKey])}')">${escapeHtml(item[ref.nameKey])}</div>`
    ).join('') || '<div class="ref-empty">无匹配结果</div>';
    dd.style.display = 'block';
  } catch (e) { dd.style.display = 'none'; }
}

/* 选择引用项 */
function selectRef(key, id, name) {
  document.getElementById(`f_${key}`).value = id;
  document.getElementById(`f_${key}_search`).value = name;
  document.getElementById(`f_${key}_dd`).style.display = 'none';
  // 选择客户后，自动带出客户的业务员 EmpId
  if (key === 'CustId' || key === 'CustomerId') loadCustomerEmp(id);
}

/* 根据客户带出其业务员 EmpId 与定金比例 DepositRatio */
async function loadCustomerEmp(customerId) {
  try {
    const cust = await api(`/api/base/customers/${customerId}`);
    // 带出业务员 EmpId
    const empIdEl = document.getElementById('f_EmpId');
    if (empIdEl && cust.empId) {
      empIdEl.value = cust.empId;
      const empSearchEl = document.getElementById('f_EmpId_search');
      if (empSearchEl) {
        const emp = await api(`/api/base/employees/${cust.empId}`);
        empSearchEl.value = emp.employeeName || '';
      }
    }
    // 带出定金比例 DepositRatio（客户已设置时）
    if (cust.depositRatio != null && cust.depositRatio > 0) {
      const ratioEl = document.getElementById('f_DepositRatio');
      if (ratioEl) ratioEl.value = cust.depositRatio;
    }
  } catch (e) { /* 忽略带出失败 */ }
}

function renderBillEdit(oid) {
  const cfg = BILL_CONFIG[BILL_CODE];
  BILL_EDIT_OID = oid || 0;
  const isEdit = oid !== undefined;
  const fieldsHtml = cfg.fields.map(f => {
    const refType = REF_FIELD_MAP[f.key];
    if (refType) {
      // 业务员 EmpId 只读（选择客户后自动带出），其他引用字段可搜索选择
      return f.key === 'EmpId' ? renderRefFieldReadonly(f) : renderRefField(f, refType);
    }
    return fieldHtml(f, null);
  }).join('');

  document.getElementById('content').innerHTML = `
    <!-- Hero 标题区 -->
    <div class="bill-hero">
      <div>
        <h2>${cfg.title}</h2>
        <div class="bill-hero-meta">
          <span class="bill-no-label">单据号</span>
          <span class="bill-no-value">${isEdit ? '编辑中' : '新建'}</span>
          <span class="bill-no-label" style="margin-left:14px">单据类型</span>
          <span class="doc-badge doc-bl">${cfg.billType}</span>
        </div>
        <div class="bill-hero-chips" id="bill-hero-chips">
          ${heroChipsHtml(cfg)}
        </div>
      </div>
      <div class="bill-hero-actions">
        <button class="btn" onclick="renderBillV2(BILL_CODE)">← 返回列表</button>
        ${isEdit ? statusButtons(oid) : ''}
        <button class="btn" onclick="openPrintDesign(BILL_CODE)" title="设计打印模板">🎨 打印设计</button>
        <button class="btn btn-primary" onclick="saveBillEdit()">💾 保存</button>
      </div>
    </div>

    <!-- 主表 -->
    <div class="card bill-card">
      <div class="card-title">📋 主表信息</div>
      <div class="form-grid">${fieldsHtml}</div>
    </div>

    <!-- 副表 -->
    <div class="card bill-card">
      <div class="card-title">📦 明细信息 <span class="card-title-tip">（副表）</span></div>
      <div class="detail-editor">
        <table>
          <thead><tr>
            ${DETAIL_COLUMNS.map(c => `<th>${c.label}</th>`).join('')}
            <th style="width:50px">操作</th>
          </tr></thead>
          <tbody id="detail-tbody"></tbody>
        </table>
      </div>
      <div class="detail-footer">
        <button class="btn btn-neutral btn-sm" onclick="addDetailRow()">+ 添加明细行</button>
        <span class="detail-summary" id="detail-summary"></span>
      </div>
    </div>`;

  addDetailRow();
  updateDetailSummary();
  bindHeroChipLiveUpdate();
  if (isEdit) loadBillEditIntoForm(oid);
  else applyBillDefaults().then(updateBillHeroChips);
}

/* 新建单据时，从系统参数读取默认币种与默认汇率并填充表单（允许用户手动修改） */
async function applyBillDefaults() {
  try {
    const d = await api(`/api/v2/bills/${BILL_CODE}/defaults`);
    const curEl = document.getElementById('f_Currency');
    if (curEl && d.currency != null) curEl.value = String(d.currency);
    const rateEl = document.getElementById('f_ExchangeRate');
    if (rateEl && d.exchangeRate != null) rateEl.value = d.exchangeRate;
  } catch (e) { /* 忽略默认值获取失败，沿用字段原有默认值 */ }
}

function statusButtons(oid) {
  return `
    <button class="btn btn-success" onclick="billAction(${oid},'audit')">审核</button>
    <button class="btn btn-orange" onclick="billAction(${oid},'unaudit')" title="撤销审核，单据回到保存状态">销审</button>
    <button class="btn btn-danger" onclick="billAction(${oid},'void')">作废</button>
    <button class="btn" onclick="billAction(${oid},'restore')">还原</button>
    <button class="btn" onclick="previewBillPrint(${oid})">打印预览</button>
    <button class="btn" onclick="printBill(${oid})">打印</button>`;
}

function updateDetailSummary() {
  const n = document.querySelectorAll('#detail-tbody tr').length;
  const el = document.getElementById('detail-summary');
  if (el) el.textContent = `共 ${n} 行明细`;
}

function addDetailRow(data) {
  const tbody = document.getElementById('detail-tbody');
  const tr = document.createElement('tr');
  tr.dataset.rowIndex = tbody.querySelectorAll('tr').length;
  tr.innerHTML = DETAIL_COLUMNS.map(c => {
    if (c.type === 'product') return renderProductCell(data);
    const val = data ? (data[c.key] ?? '') : '';
    const ro = c.readonly ? ' readonly style="background:#f8fafc;cursor:not-allowed"' : '';
    const type = c.type === 'number' ? 'number' : 'text';
    const calcEvt = (c.key === 'Quantity' || c.key === 'UnitPrice') ? ` oninput="updateAmount(this.closest('tr'))"` : '';
    return `<td><input class="detail-input" data-key="${c.key}" type="${type}" value="${escapeHtml(val)}" step="0.01"${ro}${calcEvt}></td>`;
  }).join('') + `<td><button class="btn btn-danger btn-sm" onclick="removeDetailRow(this)">×</button></td>`;
  tbody.appendChild(tr);
  updateDetailSummary();
  return tr;
}

function removeDetailRow(btn) {
  btn.closest('tr').remove();
  updateDetailSummary();
}

async function loadBillEditIntoForm(oid) {
  const cfg = BILL_CONFIG[BILL_CODE];
  const data = await api(`/api/v2/bills/${BILL_CODE}/${oid}`);
  const row = data.main || {};
  cfg.fields.forEach(async f => {
    const refType = REF_FIELD_MAP[f.key];
    if (refType) {
      // 引用字段：填隐藏 ID + 查名称回填搜索框
      const id = row[f.key];
      if (id) {
        document.getElementById('f_' + f.key).value = id;
        const ref = REF_APIS[refType];
        try {
          const d = await api(`${ref.api}/${id}`);
          document.getElementById('f_' + f.key + '_search').value = d[ref.nameKey] || '';
        } catch (e) { /* 忽略名称查询失败 */ }
      }
      return;
    }
    const el = document.getElementById('f_' + f.key);
    if (!el) return;
    let v = row[f.key];
    if (f.type === 'date') v = fmtDate(v);
    el.value = v ?? '';
  });
  // 回填明细（副表）
  const tbody = document.getElementById('detail-tbody');
  if (data.details && data.details.length) {
    tbody.innerHTML = '';
    data.details.forEach(d => addDetailRow(d));
  }
}

async function saveBillEdit() {
  const cfg = BILL_CONFIG[BILL_CODE];
  const fields = {};
  cfg.fields.forEach(f => {
    const el = document.getElementById('f_' + f.key);
    if (!el) return;
    let v = el.value;
    if (f.type === 'number') v = v === '' ? 0 : Number(v);
    if (f.type === 'date' && v) v = v + 'T00:00:00';
    if (v === '' || v === null) return;
    fields[f.key] = v;
  });
  const details = collectDetails();
  try {
    const r = await api(`/api/v2/bills/${BILL_CODE}/save`, 'POST', { oid: BILL_EDIT_OID, fields, details });
    toast(`保存成功，单据号：${r.billNo}`);
    renderBillV2(BILL_CODE);
  } catch (err) { toast(err.message, 'error'); }
}

/* 收集副表明细行 */
function collectDetails() {
  const details = [];
  document.querySelectorAll('#detail-tbody tr').forEach(tr => {
    const d = {};
    let hasValue = false;
    tr.querySelectorAll('.detail-input').forEach(inp => {
      let v = inp.value;
      if (inp.dataset.key === 'ProductId') v = v === '' ? 0 : Number(v);
      else if (inp.type === 'number') v = v === '' ? 0 : Number(v);
      d[inp.dataset.key] = v;
      if (v !== '' && v !== 0) hasValue = true;
    });
    if (hasValue) details.push(d);
  });
  return details;
}

/* ============ 销售订单明细：商品模糊匹配 + 查询选择（多选） ============ */
let PRODUCT_PICKER_ITEMS = [];
let PRODUCT_PICKER_TARGET = null;

function renderProductCell(data) {
  const pid = data ? (data.ProductId ?? '') : '';
  const pname = data ? (data.ProductName ?? '') : '';
  return `<td>
    <div class="product-cell">
      <input type="hidden" class="detail-input" data-key="ProductId" value="${escapeHtml(pid)}">
      <input type="text" class="product-search" placeholder="输入商品编码/名称" value="${escapeHtml(pname)}" autocomplete="off"
             oninput="searchProductInRow(this)" onblur="setTimeout(()=>hideProductDropdown(this),200)">
      <button type="button" class="btn btn-neutral btn-sm" onclick="openProductPicker(this)">查询</button>
      <div class="product-dd"></div>
    </div>
  </td>`;
}

async function searchProductInRow(input) {
  const tr = input.closest('tr');
  const dd = tr.querySelector('.product-dd');
  const kw = input.value.trim();
  if (!kw) { dd.style.display = 'none'; return; }
  try {
    const data = await api(`/api/base/products?page=1&pageSize=10&keyword=${encodeURIComponent(kw)}`);
    const items = data.items || [];
    dd.innerHTML = items.map(p =>
      `<div class="ref-item" onmousedown="selectProductInRow(this, ${p.id}, '${escapeHtml(p.productCode)}', '${escapeHtml(p.productName)}', '${escapeHtml(p.spec)}', '${escapeHtml(p.unit)}', ${p.salePrice ?? 0})">${escapeHtml(p.productCode)} - ${escapeHtml(p.productName)}</div>`
    ).join('') || '<div class="ref-empty">无匹配结果</div>';
    dd.style.display = 'block';
  } catch (e) { dd.style.display = 'none'; }
}

function hideProductDropdown(input) {
  const dd = input.closest('tr')?.querySelector('.product-dd');
  if (dd) dd.style.display = 'none';
}

function selectProductInRow(el, id, code, name, spec, unit, salePrice) {
  fillProductRow(el.closest('tr'), { id, productCode: code, productName: name, spec, unit, salePrice });
}

function fillProductRow(tr, p) {
  const pidEl = tr.querySelector('input[data-key="ProductId"]');
  if (pidEl) pidEl.value = p.id;
  const search = tr.querySelector('.product-search');
  if (search) search.value = p.productName || p.productCode || '';
  setCellValue(tr, 'ProductName', p.productName || '');
  setCellValue(tr, 'Spec', p.spec || '');
  setCellValue(tr, 'Unit', p.unit || '');
  setCellValue(tr, 'UnitPrice', p.salePrice ?? 0);
  const dd = tr.querySelector('.product-dd');
  if (dd) dd.style.display = 'none';
  updateAmount(tr);
}

function setCellValue(tr, key, value) {
  const el = tr.querySelector(`input[data-key="${key}"]`);
  if (el) el.value = value ?? '';
}

function updateAmount(tr) {
  const qty = Number(tr.querySelector('input[data-key="Quantity"]')?.value) || 0;
  const price = Number(tr.querySelector('input[data-key="UnitPrice"]')?.value) || 0;
  const amt = tr.querySelector('input[data-key="Amount"]');
  if (amt) amt.value = (qty * price).toFixed(2);
}

function openProductPicker(btn) {
  PRODUCT_PICKER_TARGET = btn.closest('tr');
  const modal = document.getElementById('modal');
  modal.innerHTML = `
    <div class="modal modal-lg">
      <h3>选择商品</h3>
      <div class="toolbar" style="margin-bottom:8px">
        <input type="text" id="pp-keyword" placeholder="输入商品编码/名称搜索" onkeydown="if(event.key==='Enter')loadProductPickerList()">
        <button class="btn btn-neutral" onclick="loadProductPickerList()">搜索</button>
      </div>
      <div class="table-wrap" id="pp-list" style="max-height:420px;overflow:auto"></div>
      <div class="modal-footer">
        <button class="btn btn-neutral" onclick="closeModal()">取消</button>
        <button class="btn btn-primary" onclick="confirmProductPicker()">选择</button>
      </div>
    </div>`;
  modal.style.display = 'flex';
  loadProductPickerList();
}

async function loadProductPickerList() {
  const kwEl = document.getElementById('pp-keyword');
  const kw = kwEl ? kwEl.value.trim() : '';
  const data = await api(`/api/base/products?page=1&pageSize=100&keyword=${encodeURIComponent(kw)}`);
  PRODUCT_PICKER_ITEMS = data.items || [];
  document.getElementById('pp-list').innerHTML = `
    <table><thead><tr>
      <th style="width:40px"><input type="checkbox" id="pp-check-all" onchange="toggleAllProducts(this)"></th>
      <th>图片</th>
      <th>商品编码</th><th>商品名称</th><th>规格</th><th>单位</th><th>销售价</th>
    </tr></thead><tbody>
    ${PRODUCT_PICKER_ITEMS.map(p => `<tr>
      <td><input type="checkbox" class="pp-check" value="${p.id}"></td>
      <td>${p.image1 ? `<img class="thumb-img" src="${escapeHtml(p.image1)}" onclick="showLightbox(this.src)">` : ''}</td>
      <td>${escapeHtml(p.productCode)}</td>
      <td>${escapeHtml(p.productName)}</td>
      <td>${escapeHtml(p.spec)}</td>
      <td>${escapeHtml(p.unit)}</td>
      <td class="text-right">${fmtMoney(p.salePrice)}</td>
    </tr>`).join('')}
    </tbody></table>`;
}

function toggleAllProducts(cb) {
  document.querySelectorAll('.pp-check').forEach(c => { c.checked = cb.checked; });
}

function confirmProductPicker() {
  const checkedIds = Array.from(document.querySelectorAll('.pp-check:checked')).map(c => c.value);
  if (!checkedIds.length) { toast('请勾选商品', 'error'); return; }
  const selected = checkedIds
    .map(id => PRODUCT_PICKER_ITEMS.find(p => String(p.id) === String(id)))
    .filter(Boolean);
  selected.forEach((p, i) => {
    const tr = (i === 0 && PRODUCT_PICKER_TARGET && PRODUCT_PICKER_TARGET.isConnected)
      ? PRODUCT_PICKER_TARGET
      : addDetailRow();
    fillProductRow(tr, p);
  });
  closeModal();
  toast(`已添加 ${selected.length} 个商品`);
}
