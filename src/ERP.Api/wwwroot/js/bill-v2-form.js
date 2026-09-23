/* ============ 通用单据表单与保存 ============ */
function openBillForm(oid) {
  const cfg = BILL_CONFIG[BILL_CODE];
  const isEdit = oid !== undefined;
  const fieldsHtml = cfg.fields.map(f => fieldHtml(f, null)).join('');
  document.getElementById('modal').innerHTML = `
    <div class="modal">
      <h3>${isEdit ? '编辑' : '新增'} - ${cfg.title}</h3>
      <div class="form-grid">${fieldsHtml}</div>
      <div class="modal-footer">
        <button class="btn btn-neutral" onclick="closeModal()">取消</button>
        <button class="btn btn-primary" onclick="saveBill(${isEdit ? oid : 'null'})">保存</button>
      </div>
    </div>`;
  document.getElementById('modal').style.display = 'flex';
  if (isEdit) loadBillIntoForm(oid);
}

async function loadBillIntoForm(oid) {
  // 通过列表接口查找当前单据并回填
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
  } catch (e) { /* 回填失败不影响编辑 */ }
}

async function saveBill(oid) {
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
  try {
    const r = await api(`/api/v2/bills/${BILL_CODE}/save`, 'POST', { oid: oid || 0, fields });
    closeModal();
    toast(`保存成功，单据号：${r.billNo}`);
    loadBills();
  } catch (err) { toast(err.message, 'error'); }
}
