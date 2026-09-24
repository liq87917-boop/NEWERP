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
/* ERP-040：出运方式（与后端 ContainerShipmentTrackingRules 的取值域一致；留空 = 未指定 = 未知） */
const SHIPMENT_MODE_OPTS = [
  { value: '', label: '未指定（未知）' },
  { value: 'LCL', label: 'LCL 拼箱' },
  { value: 'FCL', label: 'FCL 整箱' },
];
/* ERP-040：查验要求三态（valueType: 'bool?' → 提交 null / false / true，未知绝不回落为「不需要查验」） */
const INSPECTION_REQUIRED_OPTS = [
  { value: '', label: '未知（未标注）' },
  { value: 'false', label: '不需要查验' },
  { value: 'true', label: '需要查验' },
];
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
      /* ERP-034：库存库龄与成本估值报表入口（只读派生；库龄按台账 FIFO 分层，未知一律显示「未知」） */
      { label: '⏳ 库存库龄 / 成本估值报表', onclick: 'openInventoryAgingReport', title: '按仓库 / 商品 / 截止日期查看 0-30 / 31-60 / 61-90 / 91-180 / 180 天以上库龄分层与库存金额（台账 FIFO 分层 + 库存行持久化加权平均成本，库龄未知与成本未知单列）' },
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
  /* ERP-040：订柜信息 = 外贸 / 物流跟踪字段的**权威记录**（出运方式 / B/L / S-O / 中转港 / ETD·ETA·ATD·ATA /
     拖车 / 报关行 / 查验要求与日期 / 放行日期）。列表与详情只回显本单持久化的值，未知一律显示「未知」；
     报关行引用停用 / 删除后仍显示当时的名称快照并标注不可用。 */
  booking: {
    title: '订柜信息', api: '/api/container/bookings', canSubmit: true,
    columns: [
      { key: 'bookingNo', label: '订柜单号' }, { key: 'bookingDate', label: '日期', type: 'date' },
      { key: 'customerId', label: '客户Id' }, { key: 'containerType', label: '柜型' },
      { key: 'shippingCompany', label: '船公司' },
      { key: 'shipmentMode', label: '出运方式', render: row => shipmentModeText(row.shipmentMode) },
      { key: 'billOfLadingNo', label: '提单号', render: row => trackingText(row.billOfLadingNo) },
      { key: 'destinationPort', label: '目的港', render: row => trackingText(row.destinationPort) },
      { key: 'etd', label: 'ETD', render: row => trackingDate(row.etd) },
      { key: 'eta', label: 'ETA', render: row => trackingDate(row.eta) },
      { key: 'customsBrokerName', label: '报关行', render: row => trackingBrokerHtml(row.customsBrokerName, row.customsBrokerAvailable) },
      { key: 'inspectionRequired', label: '查验要求', render: row => inspectionRequiredText(row.inspectionRequired) },
      { key: 'status', label: '状态', status: true },
    ],
    fields: [
      { key: 'bookingDate', label: '订柜日期', type: 'date' },
      { key: 'customerId', label: '客户', type: 'ref', ref: 'customer' },
      { key: 'supplierId', label: '供应商（可留空）', type: 'ref', ref: 'supplier' },
      { key: 'containerType', label: '柜型', type: 'select', options: CONTAINER_OPTS },
      { key: 'shippingCompany', label: '船公司' }, { key: 'voyageNo', label: '航次' },
      { key: 'sailingDate', label: '开船日期', type: 'date' },
      { key: 'departurePort', label: '起运港（可留空）' }, { key: 'destinationPort', label: '目的港（可留空）' },
      /* ERP-040：外贸与物流跟踪（可留空 = 未知；服务端不推断任何日期） */
      { key: 'shipmentMode', label: '出运方式（可留空）', type: 'select', options: SHIPMENT_MODE_OPTS },
      { key: 'billOfLadingNo', label: '提单号 B/L' }, { key: 'shippingOrderNo', label: '订舱号 S/O' },
      { key: 'transitPort', label: '中转港（可留空）' },
      { key: 'etd', label: '预计开船 ETD', type: 'date' }, { key: 'eta', label: '预计到港 ETA', type: 'date' },
      { key: 'atd', label: '实际开船 ATD', type: 'date' }, { key: 'ata', label: '实际到港 ATA', type: 'date' },
      { key: 'truckerName', label: '拖车 / 集卡公司' },
      { key: 'customsBrokerId', label: '报关行（可留空）', type: 'ref', ref: 'customsBroker' },
      { key: 'inspectionRequired', label: '查验要求', type: 'select', valueType: 'bool?', options: INSPECTION_REQUIRED_OPTS },
      { key: 'inspectionDate', label: '查验日期（可留空）', type: 'date' },
      { key: 'customsReleaseDate', label: '海关放行日期（可留空）', type: 'date' },
      { key: 'remark', label: '备注', type: 'textarea' },
    ],
  },
  /* ERP-040：预装柜单按**持久化订柜引用**（bookingId）只读回显订柜信息上的外贸 / 物流跟踪值；
     本单不保存第二份跟踪值，也不按柜号等自由文本匹配。 */
  'pre-loading': {
    title: '预装柜单', api: '/api/container/pre-loadings', canSubmit: true,
    columns: [
      { key: 'preLoadingNo', label: '预装柜单号' }, { key: 'loadingDate', label: '日期', type: 'date' },
      { key: 'containerNo', label: '柜号' }, { key: 'sealNo', label: '封条号' },
      { key: 'totalVolume', label: '体积(m³)', type: 'money' }, { key: 'status', label: '状态', status: true },
    ],
    fields: [
      { key: 'loadingDate', label: '装柜日期', type: 'date' }, { key: 'containerNo', label: '柜号' },
      /* ERP-040：订柜引用（留空 = 未关联，物流跟踪一律显示「未知」；跟踪值只按本引用读取） */
      { key: 'bookingId', label: '订柜信息（关联）', type: 'ref', ref: 'booking' },
      { key: 'sealNo', label: '封条号' }, { key: 'totalCartons', label: '总箱数', type: 'number' },
      { key: 'totalWeight', label: '总毛重(kg)', type: 'number' }, { key: 'totalVolume', label: '总体积(m³)', type: 'number' },
    ],
    /* ERP-040：只读查看权威跟踪值（任何状态都可查看；未关联订柜信息时全部显示「未知」） */
    rowActions: [
      { label: '物流跟踪', icon: '🚢', title: '按持久化订柜引用只读查看该柜的外贸与物流跟踪值（未关联订柜信息时显示「未知」）', onclick: 'showShipmentTracking' },
    ],
  },
  'loading-list': {
    title: '装柜清单', api: '/api/container/loading-lists', canSubmit: true,
    columns: [
      { key: 'loadingListNo', label: '装柜清单号' }, { key: 'loadingDate', label: '日期', type: 'date' },
      { key: 'customerId', label: '客户Id' },
      /* ERP-041：一柜多客户参与方（启用 / 总数 + 主参与方）。0 条 = 历史单客户视图，沿用客户Id 字段 */
      { key: 'participantCount', label: '客户参与方', render: row => loadingListParticipantCellHtml(row) },
      { key: 'containerNo', label: '柜号' },
      { key: 'shippingMark', label: '唛头' }, { key: 'totalCartons', label: '总箱数', type: 'money' }, { key: 'status', label: '状态', status: true },
    ],
    fields: [
      { key: 'loadingDate', label: '装柜日期', type: 'date' },
      { key: 'customerId', label: '客户', type: 'ref', ref: 'customer' },
      { key: 'containerNo', label: '柜号' }, { key: 'shippingMark', label: '唛头' },
      /* ERP-040：预装柜单引用（→ 订柜信息）：物流跟踪值按该持久化引用链读取，留空 = 未关联 */
      { key: 'preLoadingId', label: '预装柜单（关联）', type: 'ref', ref: 'pre-loading' },
      { key: 'totalCartons', label: '总箱数', type: 'number' }, { key: 'totalWeight', label: '总毛重(kg)', type: 'number' },
      { key: 'totalVolume', label: '总体积(m³)', type: 'number' },
    ],
    /* ERP-019：由装柜清单生成单证中心记录（柜号写入「关联柜号/订舱号」，一柜一类单证只生成一张）
       ERP-040：物流跟踪只读回显（装柜清单 → 预装柜单 → 订柜信息 的持久化引用链，未关联显示「未知」）
       ERP-041：一柜多客户参与方维护（只维护客户归属与兼容客户字段；不按体积 / 重量 / 金额分摊费用） */
    rowActions: [
      { label: '多客户参与方', icon: '👥', title: '维护该柜的参与客户与主参与方（只改客户归属与兼容客户字段：不分摊费用、不改动装柜明细 / 跟踪值 / 单证 / 库存）', onclick: 'openLoadingListParticipants' },
      { label: '物流跟踪', icon: '🚢', title: '按持久化引用链只读查看该柜的外贸与物流跟踪值（未关联订柜信息时显示「未知」）', onclick: 'showShipmentTracking' },
      { label: '生成单证', icon: '📋', title: '由该装柜清单生成装箱单 / 提单 / 报关单 / 订舱确认等单证中心记录（同一柜号同一类型只生成一张）', onclick: 'generateTradeDocsFromSource', statuses: ['Pending', 'Submitted', 'Approved'] },
      { label: '预填单证', icon: '📝', title: '按该装柜清单带入单证草稿到单证中心新增表单（不落库，可编辑后再保存）', onclick: 'prefillTradeDocFromSource', statuses: ['Pending', 'Submitted', 'Approved'] },
      /* ERP-045：登记 / 查看本装柜清单的附件引用（仅元数据；不上传 / 下载 / 预览 / 抓取任何文件，作废保留历史） */
      { label: '附件引用', icon: '📎', title: '登记或查看本装柜清单的附件引用元数据（分类 / 显示名 / 不透明引用标识 / 大小 / 校验和；不上传、不下载、不预览、不抓取文件，作废保留历史且不改写本单）', onclick: 'openDocumentAttachmentReferencesForCurrentModule' },
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
