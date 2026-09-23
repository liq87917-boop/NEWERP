/* ============ 物流 + 装柜模块 ============ */
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
  },
});
