/* ============ 物流 + 装柜模块 ============ */
/* ERP-009：库存单据选项集（与后端字符串口径一致，保存时按原值写入） */
const ADJUST_TYPE_OPTS = [
  { value: '盘点调整', label: '盘点调整' },
  { value: '报损', label: '报损（盘亏）' },
  { value: '报溢', label: '报溢（盘盈）' },
];
/* ERP-009：库存流水移动类型（后端 Program.cs 注册了 JsonStringEnumConverter，
   枚举按「名称」序列化，因此此处 value 取枚举名，前端把名称渲染为业务文案） */
const MOVEMENT_TYPE_OPTS = [
  { value: 'PurchaseIn', label: '采购入库' }, { value: 'SalesOut', label: '销售出库' },
  { value: 'Adjustment', label: '盘点调整' }, { value: 'TransferOut', label: '调拨出库' },
  { value: 'TransferIn', label: '调拨入库' }, { value: 'SalesReturn', label: '销售退货' },
  { value: 'PurchaseReturn', label: '采购退货' },
];
const DIRECTION_OPTS = [{ value: '1', label: '入库(+）' }, { value: '-1', label: '出库(-）' }];
const REVERSAL_OPTS = [{ value: 'false', label: '正常' }, { value: 'true', label: '红字冲销' }];
/* ERP-009：退货明细列（销售退货 / 采购退货共用：数量 × 退货单价 = 金额；成本单价用于库存计价） */
const RETURN_DETAIL_FIELDS = [
  { key: 'productId', label: '商品ID', type: 'number', width: '90px' },
  { key: 'productName', label: '商品名称', width: '170px' },
  { key: 'spec', label: '规格', width: '100px' },
  { key: 'unit', label: '单位', width: '60px' },
  { key: 'quantity', label: '数量', type: 'number', width: '80px' },
  { key: 'unitPrice', label: '退货单价', type: 'number', width: '90px' },
  { key: 'amount', label: '金额', type: 'number', width: '90px', readonly: true },
  { key: 'unitCost', label: '成本单价', type: 'number', width: '90px' },
  { key: 'remark', label: '备注', width: '110px' },
];
const RETURN_DETAIL_AMOUNT = { qty: 'quantity', price: 'unitPrice', amount: 'amount', totalId: 'detail-total' };

Object.assign(MODULES, {
  'stock-in': {
    title: '采购入库', api: '/api/stock-ins', canSubmit: true,
    columns: [
      { key: 'stockInNo', label: '入库单号' }, { key: 'stockInDate', label: '日期', type: 'date' },
      { key: 'supplierId', label: '供应商Id' }, { key: 'warehouseId', label: '仓库Id' },
      { key: 'totalQuantity', label: '总数量', type: 'money' }, { key: 'status', label: '状态', status: true },
    ],
    fields: [
      { key: 'stockInDate', label: '入库日期', type: 'date' }, { key: 'supplierId', label: '供应商Id', type: 'number' },
      { key: 'warehouseId', label: '仓库Id', type: 'number' }, { key: 'totalQuantity', label: '总数量', type: 'number' },
      { key: 'totalWeight', label: '总毛重(kg)', type: 'number' }, { key: 'totalVolume', label: '总体积(m³)', type: 'number' },
    ],
  },
  'stock-out': {
    title: '销售出库', api: '/api/stock-outs', canSubmit: true,
    columns: [
      { key: 'stockOutNo', label: '出库单号' }, { key: 'stockOutDate', label: '日期', type: 'date' },
      { key: 'customerId', label: '客户Id' }, { key: 'warehouseId', label: '仓库Id' },
      { key: 'totalQuantity', label: '总数量', type: 'money' }, { key: 'status', label: '状态', status: true },
    ],
    fields: [
      { key: 'stockOutDate', label: '出库日期', type: 'date' }, { key: 'customerId', label: '客户Id', type: 'number' },
      { key: 'warehouseId', label: '仓库Id', type: 'number' }, { key: 'totalQuantity', label: '总数量', type: 'number' },
      { key: 'totalWeight', label: '总毛重(kg)', type: 'number' }, { key: 'totalVolume', label: '总体积(m³)', type: 'number' },
    ],
  },
  'stock-query': {
    title: '库存查询', api: '/api/stocks', readonly: true,
    columns: [
      { key: 'productCode', label: '商品编码' }, { key: 'productName', label: '商品名称' },
      { key: 'warehouseName', label: '仓库' }, { key: 'quantity', label: '库存数量', type: 'money' },
      { key: 'availableQuantity', label: '可用数量', type: 'money' },
    ],
    /* ERP-029：库存移动与呆滞报表入口（工具栏，始终可见；只读派生，不落库） */
    extraActions: [
      { label: '📉 库存移动 / 呆滞报表', onclick: 'openInventoryMovementReport', title: '按仓库 / 商品 / 截止日期 / 移动窗口 / 呆滞阈值查看基础单位出入库、最后移动日期与停滞天数（库存流水台账口径，未知显示「未知」）' },
    ],
    fields: [],
  },
  'receiving-plan': {
    title: '收货计划', api: '/api/container/receiving-plans', canSubmit: true,
    columns: [
      { key: 'planNo', label: '计划单号' }, { key: 'planDate', label: '日期', type: 'date' },
      { key: 'supplierId', label: '供应商Id' }, { key: 'containerNo', label: '柜号' },
      { key: 'totalQuantity', label: '总件数', type: 'money' }, { key: 'status', label: '状态', status: true },
    ],
    fields: [
      { key: 'planDate', label: '计划日期', type: 'date' }, { key: 'supplierId', label: '供应商Id', type: 'number' },
      { key: 'bookingNo', label: '订柜单号' }, { key: 'containerType', label: '柜型', type: 'select', options: CONTAINER_OPTS },
      { key: 'containerNo', label: '柜号' }, { key: 'expectedArrivalDate', label: '预计到货', type: 'date' },
      { key: 'destination', label: '目的地' }, { key: 'totalQuantity', label: '总件数', type: 'number' },
    ],
  },
  booking: {
    title: '订柜信息', api: '/api/container/bookings', canSubmit: true,
    columns: [
      { key: 'bookingNo', label: '订柜单号' }, { key: 'bookingDate', label: '日期', type: 'date' },
      { key: 'customerId', label: '客户Id' }, { key: 'shippingCompany', label: '船公司' },
      { key: 'departurePort', label: '起运港' }, { key: 'status', label: '状态', status: true },
    ],
    fields: [
      { key: 'bookingDate', label: '订柜日期', type: 'date' }, { key: 'customerId', label: '客户Id', type: 'number' },
      { key: 'containerType', label: '柜型', type: 'select', options: CONTAINER_OPTS },
      { key: 'shippingCompany', label: '船公司' }, { key: 'voyageNo', label: '航次' },
      { key: 'sailingDate', label: '开船日期', type: 'date' },
      { key: 'departurePort', label: '起运港' }, { key: 'destinationPort', label: '目的港' },
    ],
  },
  'pre-loading': {
    title: '预装柜单', api: '/api/container/pre-loadings', canSubmit: true,
    columns: [
      { key: 'preLoadingNo', label: '预装柜单号' }, { key: 'loadingDate', label: '日期', type: 'date' },
      { key: 'containerNo', label: '柜号' }, { key: 'sealNo', label: '封条号' },
      { key: 'totalVolume', label: '体积(m³)', type: 'money' }, { key: 'status', label: '状态', status: true },
    ],
    fields: [
      { key: 'loadingDate', label: '装柜日期', type: 'date' }, { key: 'containerNo', label: '柜号' },
      { key: 'sealNo', label: '封条号' }, { key: 'totalCartons', label: '总箱数', type: 'number' },
      { key: 'totalWeight', label: '总毛重(kg)', type: 'number' }, { key: 'totalVolume', label: '总体积(m³)', type: 'number' },
    ],
  },
  'loading-list': {
    title: '装柜清单', api: '/api/container/loading-lists', canSubmit: true,
    columns: [
      { key: 'loadingListNo', label: '装柜清单号' }, { key: 'loadingDate', label: '日期', type: 'date' },
      { key: 'customerId', label: '客户Id' }, { key: 'containerNo', label: '柜号' },
      { key: 'shippingMark', label: '唛头' }, { key: 'totalCartons', label: '总箱数', type: 'money' }, { key: 'status', label: '状态', status: true },
    ],
    fields: [
      { key: 'loadingDate', label: '装柜日期', type: 'date' }, { key: 'customerId', label: '客户Id', type: 'number' },
      { key: 'containerNo', label: '柜号' }, { key: 'shippingMark', label: '唛头' },
      { key: 'totalCartons', label: '总箱数', type: 'number' }, { key: 'totalWeight', label: '总毛重(kg)', type: 'number' },
      { key: 'totalVolume', label: '总体积(m³)', type: 'number' },
    ],
    /* ERP-019：由装柜清单生成单证中心记录（柜号写入「关联柜号/订舱号」，一柜一类单证只生成一张） */
    rowActions: [
      { label: '生成单证', icon: '📋', title: '由该装柜清单生成装箱单 / 提单 / 报关单 / 订舱确认等单证中心记录（同一柜号同一类型只生成一张）', onclick: 'generateTradeDocsFromSource', statuses: ['Pending', 'Submitted', 'Approved'] },
      { label: '预填单证', icon: '📝', title: '按该装柜清单带入单证草稿到单证中心新增表单（不落库，可编辑后再保存）', onclick: 'prefillTradeDocFromSource', statuses: ['Pending', 'Submitted', 'Approved'] },
    ],
  },

  /* ============ ERP-009：库存单据（盘点 / 调拨 / 退货）与库存流水 ============ */

  /* 库存盘点调整：审核按「实盘 - 账面」差异调整库存，销审按流水冲销还原 */
  'stock-adjustment': {
    title: '库存盘点调整', api: '/api/inventory/stock-adjustments', canSubmit: true,
    columns: [
      { key: 'adjustmentNo', label: '盘点单号' }, { key: 'adjustmentDate', label: '日期', type: 'date' },
      { key: 'warehouseName', label: '仓库' }, { key: 'adjustType', label: '调整类型' },
      { key: 'totalDiffQuantity', label: '差异数量', type: 'money' },
      { key: 'totalDiffAmount', label: '差异金额', type: 'money' },
      { key: 'status', label: '状态', status: true },
    ],
    fields: [
      { key: 'adjustmentDate', label: '盘点日期', type: 'date' },
      { key: 'warehouseId', label: '仓库', type: 'ref', ref: 'warehouse' },
      { key: 'adjustType', label: '调整类型', type: 'select', options: ADJUST_TYPE_OPTS },
      { key: 'remark', label: '备注', type: 'textarea' },
    ],
    rowActions: [
      { label: '销审', icon: '↩', title: '销审并冲销该单据已产生的库存流水', onclick: 'unauditRow', statuses: ['Approved'] },
    ],
    detailKey: 'details',
    detailTitle: '盘点明细（差异 = 实盘 - 账面，差异金额 = 差异 × 成本单价）',
    detailDiff: { from: 'bookQuantity', to: 'actualQuantity', diff: 'diffQuantity', unitCost: 'unitCost', amount: 'diffAmount', totalId: 'detail-diff-total' },
    detailFields: [
      { key: 'productId', label: '商品ID', type: 'number', width: '90px' },
      { key: 'productName', label: '商品名称', width: '170px' },
      { key: 'spec', label: '规格', width: '100px' },
      { key: 'unit', label: '单位', width: '60px' },
      { key: 'bookQuantity', label: '账面数量', type: 'number', width: '90px' },
      { key: 'actualQuantity', label: '实盘数量', type: 'number', width: '90px' },
      { key: 'diffQuantity', label: '差异', type: 'number', width: '80px', readonly: true },
      { key: 'unitCost', label: '成本单价', type: 'number', width: '90px' },
      { key: 'diffAmount', label: '差异金额', type: 'number', width: '100px', readonly: true },
      { key: 'remark', label: '备注', width: '110px' },
    ],
  },

  /* 仓库调拨：调出仓减、调入仓增，两侧成本单价一致（数量与金额守恒） */
  'stock-transfer': {
    title: '仓库调拨', api: '/api/inventory/stock-transfers', canSubmit: true,
    columns: [
      { key: 'transferNo', label: '调拨单号' }, { key: 'transferDate', label: '日期', type: 'date' },
      { key: 'fromWarehouseName', label: '调出仓' }, { key: 'toWarehouseName', label: '调入仓' },
      { key: 'totalQuantity', label: '调拨数量', type: 'money' },
      { key: 'totalAmount', label: '调拨金额', type: 'money' },
      { key: 'status', label: '状态', status: true },
    ],
    fields: [
      { key: 'transferDate', label: '调拨日期', type: 'date' },
      { key: 'fromWarehouseId', label: '调出仓库', type: 'ref', ref: 'warehouse' },
      { key: 'toWarehouseId', label: '调入仓库', type: 'ref', ref: 'warehouse' },
      { key: 'remark', label: '备注', type: 'textarea' },
    ],
    rowActions: [
      { label: '销审', icon: '↩', title: '销审并冲销该单据已产生的库存流水', onclick: 'unauditRow', statuses: ['Approved'] },
    ],
    detailKey: 'details',
    detailTitle: '调拨明细（数量 × 成本单价 = 金额；成本单价留 0 时取调出仓加权平均成本）',
    detailAmount: { qty: 'quantity', price: 'unitCost', amount: 'amount', totalId: 'detail-total' },
    detailFields: [
      { key: 'productId', label: '商品ID', type: 'number', width: '90px' },
      { key: 'productName', label: '商品名称', width: '170px' },
      { key: 'spec', label: '规格', width: '100px' },
      { key: 'unit', label: '单位', width: '60px' },
      { key: 'quantity', label: '调拨数量', type: 'number', width: '90px' },
      { key: 'unitCost', label: '成本单价', type: 'number', width: '90px' },
      { key: 'amount', label: '金额', type: 'number', width: '90px', readonly: true },
      { key: 'batchNo', label: '批次号', width: '90px' },
      { key: 'remark', label: '备注', width: '110px' },
    ],
  },

  /* 销售退货：退货入库（按原出库成本计价），可关联来源销售出库单溯源 */
  'sales-return': {
    title: '销售退货', api: '/api/inventory/sales-returns', canSubmit: true,
    columns: [
      { key: 'returnNo', label: '退货单号' }, { key: 'returnDate', label: '日期', type: 'date' },
      { key: 'customerName', label: '客户' }, { key: 'warehouseName', label: '入库仓库' },
      { key: 'sourceStockOutNo', label: '来源出库单' },
      { key: 'totalQuantity', label: '退货数量', type: 'money' },
      { key: 'totalAmount', label: '退货金额', type: 'money' },
      { key: 'status', label: '状态', status: true },
    ],
    fields: [
      { key: 'returnDate', label: '退货日期', type: 'date' },
      { key: 'customerId', label: '客户', type: 'ref', ref: 'customer' },
      { key: 'customerName', label: '客户名称（冗余，可留空）' },
      { key: 'warehouseId', label: '退货入库仓库', type: 'ref', ref: 'warehouse' },
      { key: 'sourceStockOutId', label: '来源销售出库单 ID（可留空）', type: 'number' },
      { key: 'sourceStockOutNo', label: '来源销售出库单号（留空时按 ID 自动带出）' },
      { key: 'returnReason', label: '退货原因' },
      { key: 'remark', label: '备注', type: 'textarea' },
    ],
    rowActions: [
      { label: '销审', icon: '↩', title: '销审并冲销该单据已产生的库存流水', onclick: 'unauditRow', statuses: ['Approved'] },
    ],
    detailKey: 'details',
    detailTitle: '退货明细（数量 × 退货单价 = 金额；成本单价留 0 时取来源出库成本 / 当前加权平均成本）',
    detailAmount: RETURN_DETAIL_AMOUNT,
    detailFields: RETURN_DETAIL_FIELDS,
  },

  /* 采购退货：退货出库（按来源入库成本计价），可关联来源采购入库单溯源 */
  'purchase-return': {
    title: '采购退货', api: '/api/inventory/purchase-returns', canSubmit: true,
    columns: [
      { key: 'returnNo', label: '退货单号' }, { key: 'returnDate', label: '日期', type: 'date' },
      { key: 'supplierName', label: '供应商' }, { key: 'warehouseName', label: '出库仓库' },
      { key: 'sourceStockInNo', label: '来源入库单' },
      { key: 'totalQuantity', label: '退货数量', type: 'money' },
      { key: 'totalAmount', label: '退货金额', type: 'money' },
      { key: 'status', label: '状态', status: true },
    ],
    fields: [
      { key: 'returnDate', label: '退货日期', type: 'date' },
      { key: 'supplierId', label: '供应商', type: 'ref', ref: 'supplier' },
      { key: 'supplierName', label: '供应商名称（冗余，可留空）' },
      { key: 'warehouseId', label: '退货出库仓库', type: 'ref', ref: 'warehouse' },
      { key: 'sourceStockInId', label: '来源采购入库单 ID（可留空）', type: 'number' },
      { key: 'sourceStockInNo', label: '来源采购入库单号（留空时按 ID 自动带出）' },
      { key: 'returnReason', label: '退货原因' },
      { key: 'remark', label: '备注', type: 'textarea' },
    ],
    rowActions: [
      { label: '销审', icon: '↩', title: '销审并冲销该单据已产生的库存流水', onclick: 'unauditRow', statuses: ['Approved'] },
    ],
    detailKey: 'details',
    detailTitle: '退货明细（数量 × 退货单价 = 金额；成本单价留 0 时取来源入库成本 / 当前加权平均成本）',
    detailAmount: RETURN_DETAIL_AMOUNT,
    detailFields: RETURN_DETAIL_FIELDS,
  },

  /* 库存流水：每一次库存变动的可审计记录（含红字冲销行），关键字支持来源单据号 */
  'stock-movement': {
    title: '库存流水', api: '/api/stocks/movements', readonly: true,
    columns: [
      { key: 'movementDate', label: '日期', type: 'date' },
      { key: 'movementType', label: '类型', type: 'map', options: MOVEMENT_TYPE_OPTS },
      { key: 'sourceDocNo', label: '来源单据号' },
      { key: 'warehouseName', label: '仓库' },
      { key: 'productName', label: '商品' },
      { key: 'direction', label: '方向', type: 'map', options: DIRECTION_OPTS },
      { key: 'quantity', label: '数量', type: 'money' },
      { key: 'unitCost', label: '成本单价', type: 'money' },
      { key: 'amount', label: '金额', type: 'money' },
      { key: 'balanceQuantity', label: '结存数量', type: 'money' },
      { key: 'balanceAmount', label: '结存金额', type: 'money' },
      { key: 'isReversal', label: '冲销', type: 'map', options: REVERSAL_OPTS },
    ],
    fields: [],
  },
});
