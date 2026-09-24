/* ============ 供应商比价选中行 → 采购订单（带入预填 / 直接生成，ERP-020） ============ */

/* 来源模块 -> 采购订单转换配置（与后端 PurchaseQuoteConversion 的守卫规则一一对应） */
const PURCHASE_ORDER_SOURCE = {
  'purchase-quote': { api: '/api/purchase/quotes', label: '供应商比价（选中行）' },
};

/* 行操作：直接生成采购订单（POST {api}/{id}/to-order，落库一次，来源自动留痕） */
async function purchaseQuoteToOrder(id) {
  const cfg = PURCHASE_ORDER_SOURCE['purchase-quote'];
  if (!cfg) { toast('当前模块不支持转采购订单', 'error'); return; }
  if (!confirm(`确认按该${cfg.label}生成采购订单？同一比价行只生成一张，生成后来源自动留痕。`)) return;
  try {
    const result = await api(`${cfg.api}/${id}/to-order`, 'POST');
    toast(`已生成采购订单：${result.orderNo}`);
    if (CURRENT_LOADER) CURRENT_LOADER();
    return result;
  } catch (err) { toast(err.message, 'error'); }
}

/* 行操作：带入预填（GET {api}/{id}/order-prefill，不落库，打开采购订单新增表单核对后再保存） */
async function purchaseQuotePrefillOrder(id) {
  const cfg = PURCHASE_ORDER_SOURCE['purchase-quote'];
  if (!cfg) { toast('当前模块不支持带入采购订单', 'error'); return; }
  if (!confirm(`按该${cfg.label}带入一张新的采购订单？带入后可继续编辑，保存时由服务端复核数量、单价与合计。`)) return;
  try {
    const data = await api(`${cfg.api}/${id}/order-prefill`);
    const mod = MODULES['purchase-order'];
    if (!mod) { toast('采购订单模块未加载', 'error'); return; }
    gotoModulePage('purchase-order', mod.title);   // 切到采购订单页面（同步菜单高亮与标签页）
    openForm();
    await fillPurchaseOrderForm(data.order);
    toast(`已按${cfg.label}带入，请核对后保存`);
  } catch (err) { toast(err.message, 'error'); }
}

/* 把带入的采购订单草稿写入当前新增表单：字段按类型回填、引用字段同步名称、明细走 DETAIL_ROWS */
async function fillPurchaseOrderForm(order) {
  const mod = MODULES['purchase-order'];
  if (!mod || !order) throw new Error('采购订单带入数据为空');
  (mod.fields || []).forEach(f => {
    const el = document.getElementById('f_' + f.key);
    if (!el) return;
    const v = order[f.key];
    if (f.valueType === 'bool') { el.value = (v === true || String(v).toLowerCase() === 'true') ? 'true' : 'false'; return; }
    if (f.type === 'date') { el.value = v ? fmtDate(v) : ''; return; }
    el.value = (v === null || v === undefined) ? '' : v;
  });
  /* 引用字段（供应商 / 归属客户 / 采购员）：与 crud.js loadIntoForm 同口径，异步补齐搜索框名称 */
  for (const f of (mod.fields || []).filter(x => x.type === 'ref')) {
    const box = document.getElementById('f_' + f.key + '_search');
    const rid = order[f.key];
    if (!box) continue;
    if (!rid) { box.value = ''; continue; }
    try {
      const ref = REF_APIS[f.ref];
      const d = await api(`${ref.api}/${rid}`);
      box.value = d[ref.nameKey] || '';
    } catch (e) { /* 名称查询失败不影响带入 */ }
  }
  /* 明细带入（数量 × 单价 = 金额由服务端在保存时复核） */
  if (mod.detailFields && mod.detailFields.length) {
    DETAIL_ROWS = (order[mod.detailKey || 'details'] || []).map(d => Object.assign({}, d));
    detailRender();
  }
}
