/* ============ 销售订单（v2）表单与保存 ============ */
function openSoForm(oid) {
  const modal = document.getElementById('modal');
  const isEdit = oid !== undefined;
  modal.innerHTML = `
    <div class="modal">
      <h3>${isEdit ? '编辑销售订单' : '新增销售订单'}</h3>
      <div class="form-grid">
        <div class="form-item"><label>订单日期</label><input type="date" id="sof_orderDate" value="${new Date().toISOString().slice(0, 10)}"></div>
        <div class="form-item"><label>客户Id *</label><input type="number" id="sof_custId"></div>
        <div class="form-item"><label>业务员Id</label><input type="number" id="sof_salesmanId"></div>
        <div class="form-item"><label>币种</label><select id="sof_currency">${CURRENCY_OPTS.map(o => `<option value="${o.value}">${o.label}</option>`).join('')}</select></div>
        <div class="form-item"><label>汇率</label><input type="number" id="sof_exchangeRate" value="1" step="0.01"></div>
        <div class="form-item"><label>定金比例(%)</label><input type="number" id="sof_depositRatio" value="30" step="0.01"></div>
        <div class="form-item"><label>订单总额</label><input type="number" id="sof_totalAmount" step="0.01"></div>
        <div class="form-item"><label>定金金额</label><input type="number" id="sof_depositAmount" step="0.01"></div>
        <div class="form-item"><label>交货日期</label><input type="date" id="sof_deliveryDate"></div>
        <div class="form-item"><label>运输方式</label><input id="sof_shippingMethod"></div>
        <div class="form-item full"><label>备注</label><textarea id="sof_remark" rows="2"></textarea></div>
      </div>
      <div class="modal-footer">
        <button class="btn btn-neutral" onclick="closeModal()">取消</button>
        <button class="btn btn-primary" onclick="saveSo(${isEdit ? oid : 'null'})">保存</button>
      </div>
    </div>`;
  modal.style.display = 'flex';
  if (isEdit) loadSoIntoForm(oid);
}

async function loadSoIntoForm(oid) {
  const data = await api(`/api/v2/sales-orders?keyword=${encodeURIComponent('')}`);
  // 通过列表接口查找（简化：从当前列表数据回填）
  toast('编辑回填功能开发中');
}

async function saveSo(oid) {
  const body = {
    oid: oid || 0,
    orderDate: document.getElementById('sof_orderDate').value + 'T00:00:00',
    custId: Number(document.getElementById('sof_custId').value) || 0,
    salesmanId: Number(document.getElementById('sof_salesmanId').value) || null,
    currency: Number(document.getElementById('sof_currency').value),
    exchangeRate: Number(document.getElementById('sof_exchangeRate').value) || 1,
    depositRatio: Number(document.getElementById('sof_depositRatio').value) || 0,
    totalAmount: Number(document.getElementById('sof_totalAmount').value) || 0,
    depositAmount: Number(document.getElementById('sof_depositAmount').value) || 0,
    deliveryDate: document.getElementById('sof_deliveryDate').value || null,
    shippingMethod: document.getElementById('sof_shippingMethod').value || null,
    remark: document.getElementById('sof_remark').value || null,
  };
  if (!body.custId) { toast('请填写客户Id', 'error'); return; }
  try {
    const r = await api('/api/v2/sales-orders/save', 'POST', body);
    closeModal();
    toast(`保存成功，单据号：${r.billNo}`);
    loadSalesOrders();
  } catch (err) { toast(err.message, 'error'); }
}
