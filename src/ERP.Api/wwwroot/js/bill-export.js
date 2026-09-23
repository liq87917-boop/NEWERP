/* ============ 单据导出模块（询价单/销售订单/采购订单导出） ============ */

/* 导出菜单编码 -> 单据类型（BILL_CODE_MAP 映射） */
const EXPORT_MENU_MAP = {
  'inquiry-export': { billType: 'inquiry', title: '询价单导出', dateField: 'InquiryDate' },
  'sales-order-export': { billType: 'sales-order', title: '销售订单导出', dateField: 'OrderDate' },
  'purchase-order-export': { billType: 'purchase-order', title: '采购订单导出', dateField: 'OrderDate' },
};

/* 当前导出菜单编码 */
let EXPORT_CURRENT_CODE = '';

function renderBillExport(code) {
  const cfg = EXPORT_MENU_MAP[code];
  if (!cfg) { document.getElementById('content').innerHTML = '<div class="card empty">该导出未配置</div>'; return; }
  EXPORT_CURRENT_CODE = code;

  const defStart = new Date(Date.now() - 30 * 86400000).toISOString().slice(0, 10);
  const defEnd = new Date().toISOString().slice(0, 10);
  document.getElementById('content').innerHTML = `
    <div class="card">
      <div class="card-title">📤 ${cfg.title}</div>
      <p class="text-muted" style="margin:8px 0 16px">选择日期范围与单据状态，导出为 Excel 文件。单据号支持模糊搜索。</p>
      <div class="form-grid">
        <div class="form-item">
          <label>开始日期</label>
          <input type="date" id="ex-start" value="${defStart}">
        </div>
        <div class="form-item">
          <label>结束日期</label>
          <input type="date" id="ex-end" value="${defEnd}">
        </div>
        <div class="form-item">
          <label>单据状态</label>
          <select id="ex-status">
            <option value="">全部状态</option>
            <option value="1">保存</option>
            <option value="2">已审核</option>
            <option value="-1">已作废</option>
          </select>
        </div>
        <div class="form-item">
          <label>单据号关键字</label>
          <input type="text" id="ex-keyword" placeholder="按单据号模糊搜索">
        </div>
      </div>
      <div style="margin-top:16px;display:flex;gap:10px">
        <button class="btn btn-primary" onclick="doExport()">📥 导出 Excel</button>
      </div>
    </div>`;
}

async function doExport() {
  const cfg = EXPORT_MENU_MAP[EXPORT_CURRENT_CODE];
  const start = document.getElementById('ex-start').value;
  const end = document.getElementById('ex-end').value;
  const status = document.getElementById('ex-status').value;
  const keyword = document.getElementById('ex-keyword').value.trim();

  const qs = new URLSearchParams({ dateField: cfg.dateField });
  if (start) qs.set('start', start + 'T00:00:00');
  if (end) qs.set('end', end + 'T23:59:59');
  if (status) qs.set('status', status);
  if (keyword) qs.set('keyword', keyword);

  try {
    const resp = await fetch(`/api/v2/bills/${cfg.billType}/export?${qs.toString()}`, {
      headers: { Authorization: 'Bearer ' + TOKEN },
    });
    if (!resp.ok) {
      let msg = '导出失败';
      try { msg = (await resp.json()).message || msg; } catch (e) { /* 忽略解析失败 */ }
      toast(msg, 'error');
      return;
    }
    const blob = await resp.blob();
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    const dateStr = new Date().toISOString().slice(0, 10).replace(/-/g, '');
    a.href = url;
    a.download = `${cfg.title}_${dateStr}.xlsx`;
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
    toast('导出成功');
  } catch (err) {
    toast('导出失败：' + err.message, 'error');
  }
}
