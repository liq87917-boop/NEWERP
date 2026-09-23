/* ============ 拼柜 / 整柜费用分摊（一柜多客户，按体积 / 重量 / 箱数 / 金额分摊）============ */
/* 依赖：app.js（api / toast / escapeHtml / fmtMoney）、crud.js（CURRENT_MODULE / loadList / closeModal） */
/* 入口：费用单模块工具栏「📦 拼柜分摊」（modules.js 的 extraActions 配置） */

let EA_ROWS = [];          // 分摊明细行
let EA_CUSTOMERS = [];     // 客户下拉缓存
let EA_PREVIEW = null;     // 最近一次预览结果

/** 打开拼柜分摊弹窗 */
async function openExpenseAllocate() {
  const modal = document.getElementById('modal');
  modal.innerHTML = `
    <div style="max-width:1000px;margin:4vh auto;background:#fff;border-radius:14px;padding:20px 22px;max-height:90vh;overflow:auto;box-shadow:0 20px 60px rgba(0,0,0,.25)">
      <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:12px">
        <h3 style="margin:0">📦 拼柜费用分摊<span style="font-size:13px;color:#64748b;font-weight:400;margin-left:10px">一柜多客户时，按体积 / 重量 / 箱数 / 金额把柜费分摊到客户</span></h3>
        <button class="btn btn-neutral btn-sm" onclick="closeModal()">✕ 关闭</button>
      </div>

      <div style="display:grid;grid-template-columns:repeat(4,1fr);gap:10px">
        <div><label class="ea-lb">归属类型</label>
          <select id="ea-refType" style="width:100%"><option>拼柜</option><option>整柜</option><option>散货</option></select></div>
        <div><label class="ea-lb">柜号 / 订舱号</label>
          <input id="ea-refNo" placeholder="如 CZ-PIN-2026-0918" style="width:100%"></div>
        <div><label class="ea-lb">费用类型</label>
          <select id="ea-type" style="width:100%">
            <option>报关费</option><option>拖车费</option><option>THC</option><option>文件费</option>
            <option>港杂费</option><option>仓储费</option><option>快递费</option><option>查验费</option>
            <option>订舱费</option><option>其他</option>
          </select></div>
        <div><label class="ea-lb">费用总额（人民币）</label>
          <input id="ea-total" type="number" step="0.01" value="0" style="width:100%"></div>
        <div><label class="ea-lb">分摊基数</label>
          <select id="ea-base" style="width:100%">
            <option>按体积</option><option>按重量</option><option>按箱数</option><option>按金额</option>
          </select></div>
        <div><label class="ea-lb">费用日期</label>
          <input id="ea-date" type="date" value="${new Date().toISOString().slice(0, 10)}" style="width:100%"></div>
        <div><label class="ea-lb">收款方</label>
          <input id="ea-payee" placeholder="报关行 / 货代 / 车队" style="width:100%"></div>
        <div><label class="ea-lb">备注</label>
          <input id="ea-remark" style="width:100%"></div>
      </div>

      <div style="display:flex;align-items:center;gap:10px;margin:14px 0 8px">
        <strong style="font-size:14px">客户明细</strong>
        <button class="btn btn-neutral btn-sm" onclick="eaAddRow()">+ 添加客户行</button>
        <span style="font-size:12px;color:#64748b">只需填写与所选「分摊基数」对应的那一列（体积 / 重量 / 箱数 / 金额）</span>
      </div>
      <div id="ea-rows"></div>

      <div style="display:flex;justify-content:flex-end;gap:10px;margin-top:14px">
        <button class="btn btn-neutral" onclick="eaPreview()">🔍 预览分摊</button>
        <button class="btn btn-primary" onclick="eaApply()">✅ 确认生成费用单</button>
      </div>
      <div id="ea-result" style="margin-top:14px"></div>
    </div>`;
  modal.style.display = 'block';

  await eaLoadCustomers();
  EA_ROWS = [{ customerId: '', volume: 0, weight: 0, cartons: 0, amount: 0 }];
  EA_PREVIEW = null;
  eaRenderRows();
}

/** 载入客户下拉 */
async function eaLoadCustomers() {
  try {
    const list = await api('/api/base/customers/all');
    EA_CUSTOMERS = Array.isArray(list) ? list : [];
  } catch (e) {
    EA_CUSTOMERS = [];
  }
}

/** 渲染明细行表格 */
function eaRenderRows() {
  const el = document.getElementById('ea-rows');
  if (!el) return;
  const custOpts = cid => '<option value="">（选择客户）</option>' + EA_CUSTOMERS.map(c =>
    `<option value="${c.id}" ${String(cid) === String(c.id) ? 'selected' : ''}>${escapeHtml(c.customerName)}</option>`).join('');
  el.innerHTML = `
    <table style="width:100%;border-collapse:collapse;font-size:13px">
      <thead><tr style="background:#f8fafc">
        <th style="padding:8px;text-align:left">客户</th>
        <th style="padding:8px;width:110px">体积 m³</th>
        <th style="padding:8px;width:110px">重量 kg</th>
        <th style="padding:8px;width:90px">箱数</th>
        <th style="padding:8px;width:110px">金额</th>
        <th style="padding:8px;width:56px"></th>
      </tr></thead>
      <tbody>
        ${EA_ROWS.map((r, i) => `<tr style="border-top:1px solid #e2e8f0">
          <td style="padding:6px"><select id="ea-c-${i}" style="width:100%">${custOpts(r.customerId)}</select></td>
          <td style="padding:6px"><input id="ea-v-${i}" type="number" step="0.001" value="${r.volume}" style="width:100%"></td>
          <td style="padding:6px"><input id="ea-w-${i}" type="number" step="0.001" value="${r.weight}" style="width:100%"></td>
          <td style="padding:6px"><input id="ea-k-${i}" type="number" step="1" value="${r.cartons}" style="width:100%"></td>
          <td style="padding:6px"><input id="ea-a-${i}" type="number" step="0.01" value="${r.amount}" style="width:100%"></td>
          <td style="padding:6px;text-align:center">
            <button class="btn btn-neutral btn-sm" onclick="eaRemoveRow(${i})" title="删除该行">✕</button>
          </td>
        </tr>`).join('')}
      </tbody>
    </table>`;
}

/** 从界面回读当前明细（避免重绘丢数据） */
function eaSyncRows() {
  EA_ROWS = EA_ROWS.map((r, i) => {
    const c = document.getElementById('ea-c-' + i);
    if (!c) return r;
    return {
      customerId: c.value,
      volume: Number(document.getElementById('ea-v-' + i).value || 0),
      weight: Number(document.getElementById('ea-w-' + i).value || 0),
      cartons: Number(document.getElementById('ea-k-' + i).value || 0),
      amount: Number(document.getElementById('ea-a-' + i).value || 0)
    };
  });
}

/** 添加一行 */
function eaAddRow() {
  eaSyncRows();
  EA_ROWS.push({ customerId: '', volume: 0, weight: 0, cartons: 0, amount: 0 });
  eaRenderRows();
}

/** 删除一行 */
function eaRemoveRow(i) {
  eaSyncRows();
  EA_ROWS.splice(i, 1);
  if (!EA_ROWS.length) EA_ROWS.push({ customerId: '', volume: 0, weight: 0, cartons: 0, amount: 0 });
  eaRenderRows();
  document.getElementById('ea-result').innerHTML = '';
  EA_PREVIEW = null;
}

/** 收集请求体（与后端 ExpenseAllocateRequest 对应） */
function eaCollect() {
  const rows = EA_ROWS.map((_, i) => {
    const cid = document.getElementById('ea-c-' + i).value;
    const cname = cid ? ((EA_CUSTOMERS.find(c => String(c.id) === String(cid)) || {}).customerName || '') : '';
    return {
      customerId: cid ? Number(cid) : null,
      customerName: cname,
      volume: Number(document.getElementById('ea-v-' + i).value || 0),
      weight: Number(document.getElementById('ea-w-' + i).value || 0),
      cartons: Number(document.getElementById('ea-k-' + i).value || 0),
      amount: Number(document.getElementById('ea-a-' + i).value || 0)
    };
  });
  return {
    refType: document.getElementById('ea-refType').value,
    refNo: document.getElementById('ea-refNo').value.trim(),
    expenseType: document.getElementById('ea-type').value,
    totalAmount: Number(document.getElementById('ea-total').value || 0),
    currency: 'CNY',
    exchangeRate: 1,
    allocationBase: document.getElementById('ea-base').value,
    expenseDate: document.getElementById('ea-date').value,
    payee: document.getElementById('ea-payee').value.trim(),
    remark: document.getElementById('ea-remark').value.trim(),
    details: rows.filter(r => r.customerId || r.volume || r.weight || r.cartons || r.amount)
  };
}

/** 分摊结果表 */
function eaRenderResult(items, generated) {
  const el = document.getElementById('ea-result');
  if (!el || !items) return;
  const total = items.reduce((s, x) => s + Number(x.allocatedAmount || 0), 0);
  const ratioSum = items.reduce((s, x) => s + Number(x.ratio || 0), 0);
  el.innerHTML = `
    <div style="border:1px solid #e2e8f0;border-radius:10px;overflow:hidden">
      <div style="background:#f1f5f9;padding:8px 12px;font-size:13px;font-weight:600">
        ${generated ? '✅ 已生成费用单' : '🔍 分摊预览'} · 合计 ${fmtMoney(total)} 元 · 比例合计 ${ratioSum.toFixed(2)}%
      </div>
      <table style="width:100%;border-collapse:collapse;font-size:13px">
        <thead><tr style="background:#f8fafc">
          <th style="padding:8px;text-align:left">客户</th>
          <th style="padding:8px;width:120px;text-align:right">权重</th>
          <th style="padding:8px;width:110px;text-align:right">比例 %</th>
          <th style="padding:8px;width:130px;text-align:right">分摊金额</th>
        </tr></thead>
        <tbody>${items.map(x => `<tr style="border-top:1px solid #e2e8f0">
          <td style="padding:6px">${escapeHtml(x.customerName || '—')}</td>
          <td style="padding:6px;text-align:right">${x.weightValue}</td>
          <td style="padding:6px;text-align:right">${Number(x.ratio).toFixed(2)}</td>
          <td style="padding:6px;text-align:right">${fmtMoney(x.allocatedAmount)}</td>
        </tr>`).join('')}</tbody>
      </table>
    </div>`;
}

/** 预览分摊（只计算不写库） */
async function eaPreview() {
  eaSyncRows();
  const req = eaCollect();
  if (!req.details.length) { toast('请至少填写一行客户明细', 'warning'); return; }
  if (!req.totalAmount) { toast('请填写费用总额', 'warning'); return; }
  try {
    EA_PREVIEW = await api('/api/finance/expenses/allocate-preview', 'POST', req);
    eaRenderResult(EA_PREVIEW, false);
  } catch (e) {
    toast('预览失败：' + e.message, 'error');
  }
}

/** 确认生成：按明细为每个客户生成一条费用单 */
async function eaApply() {
  eaSyncRows();
  const req = eaCollect();
  if (!req.refNo) { toast('请填写柜号 / 订舱号', 'warning'); return; }
  if (!req.details.length) { toast('请至少填写一行客户明细', 'warning'); return; }
  if (!req.totalAmount) { toast('请填写费用总额', 'warning'); return; }
  if (!EA_PREVIEW) {
    await eaPreview();
    if (!EA_PREVIEW) return;
  }
  try {
    const res = await api('/api/finance/expenses/allocate-apply', 'POST', req);
    eaRenderResult(EA_PREVIEW, true);
    toast(`已生成 ${res.created} 条费用单`, 'success');
    setTimeout(() => {
      closeModal();
      if (typeof loadList === 'function') loadList();
    }, 900);
  } catch (e) {
    toast('生成失败：' + e.message, 'error');
  }
}


