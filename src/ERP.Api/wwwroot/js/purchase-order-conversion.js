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

/* ============ 比价批次 → 多张采购订单（ERP-027：兼容行合并 / 不合格行明确跳过） ============ */

/* 行操作：把该行所属比价批次内所有「已选中」行按供应商 + 币种等表头口径合并生成采购订单 */
async function purchaseQuoteBatchToOrder(id) {
  return convertPurchaseQuoteBatch({ lineId: id });
}

/* 工具栏：按比价批次号批量生成采购订单（不必先找到某一行「已选中」的报价行） */
async function purchaseQuoteBatchToOrderByNo() {
  const quoteNo = (prompt('请输入比价批次号（同一需求的各家报价共用，如 PQ-20260924-001）：') || '').trim();
  if (!quoteNo) return;
  return convertPurchaseQuoteBatch({ quoteNo });
}

/* 批次转换共用流程：先取只读计划 → 展示将生成的张数 / 合计 / 会被跳过的行 → 确认后一次落库 */
async function convertPurchaseQuoteBatch(target) {
  const cfg = PURCHASE_ORDER_SOURCE['purchase-quote'];
  if (!cfg) { toast('当前模块不支持批次转采购订单', 'error'); return; }
  const query = target.quoteNo
    ? `quoteNo=${encodeURIComponent(target.quoteNo)}`
    : `lineId=${encodeURIComponent(target.lineId)}`;
  try {
    const plan = await api(`${cfg.api}/batch-order-plan?${query}`);
    const groups = plan.groups || [];
    if (!groups.length) {                       // 无合格行：明确告知原因，不落库
      toast(`比价批次 ${plan.sourceNo} 没有可转换的「已选中」行${batchSkipText(plan.skipped)}`, 'error');
      return plan;
    }
    if (!confirm(batchPlanText(plan, groups))) return null;

    const result = await api(`${cfg.api}/batch-to-order`, 'POST',
      target.quoteNo ? { quoteNo: target.quoteNo } : { lineId: target.lineId });
    const skipped = (result.skipped || []).length;
    toast(`已生成 ${result.orderCount} 张采购订单：${(result.orders || []).map(o => o.orderNo).join('、')}`
      + (skipped ? `（跳过 ${skipped} 行不合格比价行）` : ''));
    if (CURRENT_LOADER) CURRENT_LOADER();
    return result;
  } catch (err) { toast(err.message, 'error'); }
}

/* 计划的确认文案：把「将发生什么」讲清楚（张数 / 每组行数与重算合计 / 跳过行 / 总合计） */
function batchPlanText(plan, groups) {
  const lines = groups.map(g =>
    `· ${g.supplierName || ('供应商#' + g.supplierId)} / ${g.currency} / ${g.lineCount} 行 / 合计 ${fmtMoney(g.totalAmount)}`).join('\n');
  const skipped = plan.skipped || [];
  const skipText = skipped.length
    ? `\n另有 ${skipped.length} 行不合格将被跳过（${skipped.map(s => s.reason).join('；')}）。`
    : '';
  return `比价批次 ${plan.sourceNo}：将把 ${plan.eligibleLineCount} 行「已选中」按供应商 + 币种合并生成 `
    + `${groups.length} 张采购订单：\n${lines}${skipText}\n合计（服务端重算）${fmtMoney(plan.totalAmount)}。确认生成？`;
}

/* 不合格行提示：批次内没有可转换行时列出原因（未选中 / 已放弃 / 已转采购订单 / 未维护供应商 …） */
function batchSkipText(skipped) {
  const list = skipped || [];
  return list.length
    ? `（${list.length} 行不合格：${list.map(s => s.reason).join('；')}）`
    : '（该批次没有「已选中」的比价行）';
}
