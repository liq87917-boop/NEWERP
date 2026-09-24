/* ============ 装柜外贸与物流跟踪（ERP-040：只读回显订柜信息的权威跟踪值） ============ */
/* 口径：订柜信息 ContainerBooking 是本套跟踪值的**唯一权威记录**；
   预装柜单 / 装柜清单只按持久化引用（PreLoading.BookingId / LoadingList.PreLoadingId）只读回显，
   不复制、不各自维护第二份跟踪值，也**不**按柜号等自由文本去匹配订柜记录。
   未知一律显示「未知」，绝不回落为空、0 或今天。 */
/* 依赖：app.js（api / toast / fmtDate / CURRENT_MODULE_CODE）、crud.js（closeModal）、
         modules.js（refCellHtml 及不可用标注）、bill-edit.js（escapeHtml） */

/* 未知文案（与后端 ContainerShipmentTrackingRules.UnknownText 一致） */
const TRACKING_UNKNOWN = '未知';

/* 只读跟踪文本：空值显示「未知」（已转义） */
function trackingText(v) {
  const s = String(v === null || v === undefined ? '' : v).trim();
  return s ? escapeHtml(s) : TRACKING_UNKNOWN;
}

/* 只读跟踪日期：空值显示「未知」，不回落为空白或今天 */
function trackingDate(v) { return v ? fmtDate(v) : TRACKING_UNKNOWN; }

/* 出运方式文案（与后端 ContainerShipmentTrackingRules.ShipmentModeText 同口径） */
function shipmentModeText(mode) {
  const v = String(mode || '').trim().toUpperCase();
  if (v === 'LCL') return '拼箱 LCL';
  if (v === 'FCL') return '整箱 FCL';
  return TRACKING_UNKNOWN;
}

/* 查验要求三态文案：null / undefined = 未知（绝不显示为「不需要查验」） */
function inspectionRequiredText(v) {
  if (v === true) return '需要查验';
  if (v === false) return '不需要查验';
  return TRACKING_UNKNOWN;
}

/* 报关行引用：不可用（字典项已停用 / 删除 / 改类型）时显示当时的名称快照 + 显式标注 */
function trackingBrokerHtml(name, available) {
  const n = String(name === null || name === undefined ? '' : name).trim();
  if (!n) return TRACKING_UNKNOWN;
  return refCellHtml(n, available);
}

/* 当前模块 -> 只读跟踪接口（订柜信息本身即权威记录，不需要回显接口） */
const SHIPMENT_TRACKING_SOURCES = {
  'pre-loading': '/api/container/pre-loadings',
  'loading-list': '/api/container/loading-lists',
};

/* 行操作：只读查看该单据按持久化引用取到的权威跟踪值 */
async function showShipmentTracking(id) {
  const apiRoot = SHIPMENT_TRACKING_SOURCES[CURRENT_MODULE_CODE];
  if (!apiRoot) { toast('当前模块不支持物流跟踪', 'error'); return; }
  if (!id) { toast('请先保存单据后再查看物流跟踪', 'error'); return; }
  try {
    const data = await api(`${apiRoot}/${id}/shipment-tracking`);
    const rows = trackingRows(data)
      .map(([label, value]) => `<tr><th style="width:180px;text-align:left">${label}</th><td>${value}</td></tr>`)
      .join('');
    const linkedNote = data.linked
      ? `权威记录：订柜信息 <b>${escapeHtml(data.bookingNo || '')}</b>（只读回显，不在本单上另存跟踪值）`
      : escapeHtml(data.notLinkedReason || '未关联订柜信息，跟踪字段一律显示「未知」');
    const modal = document.getElementById('modal');
    modal.innerHTML = `<div class="modal modal-lg" style="width:780px;max-width:96vw">
      <h3>🚢 外贸 / 物流跟踪（只读）</h3>
      <p class="text-muted" style="margin:8px 0 12px">${linkedNote}</p>
      <div class="table-wrap" style="max-height:58vh;overflow:auto">
        <table><tbody>${rows}</tbody></table>
      </div>
      <div class="modal-footer"><button class="btn btn-neutral" onclick="closeModal()">关闭</button></div>
    </div>`;
    modal.style.display = 'flex';
  } catch (err) { toast(err.message, 'error'); }
}

/* 只读跟踪值（标签 + 值）：未关联时所有业务字段显示「未知」 */
function trackingRows(data) {
  const t = data || {};
  const linked = !!t.linked;
  const g = (key) => (linked ? t[key] : null);
  return [
    ['订柜单号', linked ? trackingText(t.bookingNo) : TRACKING_UNKNOWN],
    ['出运方式', linked ? escapeHtml(t.shipmentModeText || shipmentModeText(t.shipmentMode)) : TRACKING_UNKNOWN],
    ['提单号 B/L', trackingText(g('billOfLadingNo'))],
    ['订舱号 S/O', trackingText(g('shippingOrderNo'))],
    ['起运港', trackingText(g('departurePort'))],
    ['目的港', trackingText(g('destinationPort'))],
    ['中转港', trackingText(g('transitPort'))],
    ['ETD 预计开船', trackingDate(g('etd'))],
    ['ETA 预计到港', trackingDate(g('eta'))],
    ['ATD 实际开船', trackingDate(g('atd'))],
    ['ATA 实际到港', trackingDate(g('ata'))],
    ['拖车 / 集卡公司', trackingText(g('truckerName'))],
    ['报关行', linked ? trackingBrokerHtml(t.customsBrokerName, t.customsBrokerAvailable) : TRACKING_UNKNOWN],
    ['查验要求', linked ? escapeHtml(t.inspectionRequiredText || inspectionRequiredText(t.inspectionRequired)) : TRACKING_UNKNOWN],
    ['查验日期', trackingDate(g('inspectionDate'))],
    ['海关放行日期', trackingDate(g('customsReleaseDate'))],
  ];
}
