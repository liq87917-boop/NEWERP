/* ============ 通用单据表单与保存（存储过程版弹层；ERP-469 与独立表单页同一口径） ============
   菜单入口现由 bill-v2.js 的独立表单页（renderBillEdit 路径）承载；本弹层模块保留为历史 / 回滚参考，
   行为与独立表单页保持一致：
   - 新建：只从文档化默认值开始（绝不沿用上一次表单值）；默认币种 / 汇率取自既有（参数授权）默认值端点，
     失败时显式提示并保持为空 —— fail closed，绝不臆造汇率（用户手工录入的值始终可编辑）；
   - 有效期（天）：整数步长 + 前端整数校验，非整数天无法作为有效天数提交（服务端既有校验仍为权威）；
   - 提供「清空重填」一键恢复文档化默认值。
   共享纯函数（billNewDocumentDefaults / billDefaultsIntoFields / billIntegralDays）由 bill-v2.js 提供；
   本模块单独加载时安全跳过增强，绝不报错。 */
function billFormHasSharedHelpers() {
  return typeof billNewDocumentDefaults === 'function'
    && typeof billDefaultsIntoFields === 'function'
    && typeof billIntegralDays === 'function';
}

function openBillForm(oid) {
  const cfg = BILL_CONFIG[BILL_CODE];
  const isEdit = oid !== undefined && oid !== null;   // 只有显式传入单据 Id 才是编辑
  const fieldsHtml = cfg.fields.map(f => fieldHtml(f, null)).join('');
  document.getElementById('modal').innerHTML = `
    <div class="modal">
      <h3>${isEdit ? '编辑' : '新增'} - ${cfg.title}</h3>
      <div class="form-grid">${fieldsHtml}</div>
      <div class="modal-footer">
        <button class="btn btn-neutral" onclick="closeModal()">取消</button>
        ${isEdit ? '' : '<button class="btn btn-neutral" id="bill-modal-clear-all" onclick="clearBillModalFields()" title="清空并恢复文档化默认值（币种 / 汇率重新取既有系统参数默认值）">🧹 清空重填</button>'}
        <button class="btn btn-primary" onclick="saveBill(${isEdit ? oid : 'null'})">保存</button>
      </div>
    </div>`;
  document.getElementById('modal').style.display = 'flex';
  if (isEdit) loadBillIntoForm(oid);
  else billFormApplyDefaults();
}

/* 清空重填：主表恢复到文档化默认值，再重新取既有系统默认币种 / 汇率（一次性操作） */
function clearBillModalFields() {
  const cfg = BILL_CONFIG[BILL_CODE];
  if (!cfg) return;
  const defaults = billFormHasSharedHelpers()
    ? billNewDocumentDefaults(cfg)
    : {};
  cfg.fields.forEach(f => {
    const el = document.getElementById('f_' + f.key);
    if (!el) return;
    const value = defaults[f.key] === undefined ? '' : defaults[f.key];
    if (f.type === 'select') {
      el.value = value !== '' ? value : ((el.options && el.options.length) ? el.options[0].value : '');
    } else {
      el.value = value;
    }
  });
  toast('已恢复默认值');
  return billFormApplyDefaults();
}

/* 从既有默认值端点取默认币种 / 汇率并填充新建弹层；失败显式提示且保持为空（绝不臆造汇率） */
async function billFormApplyDefaults() {
  try {
    const d = await api(`/api/v2/bills/${BILL_CODE}/defaults`);
    const applied = billFormHasSharedHelpers()
      ? billDefaultsIntoFields(d, BILL_CONFIG[BILL_CODE])
      : { values: {}, warnings: [] };
    Object.keys(applied.values).forEach(key => {
      const el = document.getElementById('f_' + key);
      if (el) el.value = applied.values[key];
    });
    if (applied.warnings.length) toast(applied.warnings.join('；'), 'error');
  } catch (err) {
    const currencyEl = document.getElementById('f_Currency');
    const rateEl = document.getElementById('f_ExchangeRate');
    if (currencyEl) currencyEl.value = '';
    if (rateEl) rateEl.value = '';
    toast('未能获取系统默认币种 / 汇率：' + ((err && err.message) ? err.message : '请求失败')
      + '（已保持为空，绝不自动生成汇率，请手工录入或联系管理员维护系统参数）', 'error');
  }
  billFormApplyIntegralDaysControl();
}

/* 有效期（天）整数控件：整数步长 + 输入即归整（服务端既有校验仍为权威） */
function billFormApplyIntegralDaysControl() {
  if (!billFormHasSharedHelpers()) return;
  const el = document.getElementById('f_ValidDays');
  if (!el || typeof el.setAttribute !== 'function') return;
  el.setAttribute('type', 'number');
  el.setAttribute('step', '1');
  el.setAttribute('min', '0');
  el.setAttribute('inputmode', 'numeric');
  el.oninput = billFormGuardValidDaysInput;
  el.onchange = billFormGuardValidDaysInput;
  el.onwheel = function () { setTimeout(billFormGuardValidDaysInput, 0); };
}

function billFormGuardValidDaysInput() {
  const el = document.getElementById('f_ValidDays');
  if (!el) return;
  const checked = billIntegralDays(el.value);
  if (checked.ok) return;
  el.value = checked.value === '' ? '' : String(checked.value);
  if (checked.message) toast(checked.message, 'error');
}

async function loadBillIntoForm(oid) {
  // 通过列表接口查找当前单据并回填（编辑 / 复制等显式流程，绝不用于新建）
  try {
    const data = await api(`/api/v2/bills/${BILL_CODE}?page=1&pageSize=100`);
    const row = (data.items || []).find(x => x.Oid === oid);
    if (!row) return;
    const cfg = BILL_CONFIG[BILL_CODE];
    cfg.fields.forEach(f => {
      const el = document.getElementById('f_' + f.key);
      if (!el) return;
      let v = row[f.key];
      if (f.type === 'date') v = fmtDate(v);
      el.value = v ?? '';
    });
    billFormApplyIntegralDaysControl();
  } catch (e) { /* 回填失败不影响编辑 */ }
}

async function saveBill(oid) {
  const cfg = BILL_CONFIG[BILL_CODE];
  const fields = {};
  cfg.fields.forEach(f => {
    const el = document.getElementById('f_' + f.key);
    if (!el) return;
    let v = el.value;
    /* ERP-469：有效期（天）只接受整数天 —— 非整数天在提交前即被前端拒绝（服务端校验仍为权威） */
    if (f.key === 'ValidDays' && billFormHasSharedHelpers()) {
      const checked = billIntegralDays(v);
      if (!checked.ok) {
        if (checked.message) toast(checked.message, 'error');
        el.value = checked.value === '' ? '' : String(checked.value);
        throw new Error(checked.message || '有效期（天）只能录入整数天');
      }
      v = checked.value;
    }
    if (f.type === 'number') v = v === '' ? 0 : Number(v);
    if (f.type === 'date' && v) v = v + 'T00:00:00';
    if (v === '' || v === null) return;
    fields[f.key] = v;
  });
  try {
    const r = await api(`/api/v2/bills/${BILL_CODE}/save`, 'POST', { oid: oid || 0, fields });
    closeModal();
    toast(`保存成功，单据号：${r.billNo}`);
    loadBills();
  } catch (err) { toast(err.message, 'error'); }
}

/* Node 单测导出（浏览器中 module 为 undefined，自动跳过） */
if (typeof module !== 'undefined' && module.exports) {
  module.exports = {
    billFormHasSharedHelpers,
    openBillForm,
    clearBillModalFields,
    billFormApplyDefaults,
    billFormApplyIntegralDaysControl,
    saveBill,
  };
}
