/* ============ 单据配置（补充：货款申请/结算/客诉/装柜管理） ============ */
Object.assign(BILL_CONFIG, {
  'payment-apply': {
    billType: 'payment-apply', title: '货款申请单',
    columns: [
      { key: 'BillNo', label: '单据号' }, { key: 'ApplyDate', label: '日期', type: 'date' },
      { key: 'CustomerId', label: '客户' }, { key: 'Amount', label: '金额', type: 'money' },
      { key: 'Status', label: '状态', status: true },
    ],
    fields: [
      { key: 'ApplyDate', label: '申请日期', type: 'date' }, { key: 'CustomerId', label: '客户', type: 'number' },
      { key: 'SalesOrderId', label: '销售订单Id', type: 'number' }, { key: 'Amount', label: '金额', type: 'number' },
      { key: 'Currency', label: '币种', type: 'select', options: CURRENCY_OPTS },
      { key: 'ExchangeRate', label: '汇率', type: 'number' }, { key: 'BankAccount', label: '银行账户' },
      { key: 'Payee', label: '收款方' }, { key: 'Reason', label: '申请事由', type: 'textarea' },
      { key: 'Remark', label: '备注', type: 'textarea' },
    ],
  },
  'container-settlement': {
    billType: 'container-settlement', title: '装柜结算单',
    columns: [
      { key: 'BillNo', label: '单据号' }, { key: 'SettlementDate', label: '日期', type: 'date' },
      { key: 'CustomerId', label: '客户' }, { key: 'TotalAmount', label: '总金额', type: 'money' },
      { key: 'Status', label: '状态', status: true },
    ],
    fields: [
      { key: 'SettlementDate', label: '结算日期', type: 'date' }, { key: 'CustomerId', label: '客户', type: 'number' },
      { key: 'LoadingListId', label: '装柜清单Id', type: 'number' }, { key: 'TotalAmount', label: '结算总金额', type: 'number' },
      { key: 'FreightCost', label: '海运费', type: 'number' }, { key: 'OtherCost', label: '其他费用', type: 'number' },
      { key: 'Remark', label: '备注', type: 'textarea' },
    ],
  },
  'bulk-settlement': {
    billType: 'bulk-settlement', title: '散货结算单',
    columns: [
      { key: 'BillNo', label: '单据号' }, { key: 'SettlementDate', label: '日期', type: 'date' },
      { key: 'CustomerId', label: '客户' }, { key: 'TotalAmount', label: '总金额', type: 'money' },
      { key: 'Status', label: '状态', status: true },
    ],
    fields: [
      { key: 'SettlementDate', label: '结算日期', type: 'date' }, { key: 'CustomerId', label: '客户', type: 'number' },
      { key: 'TotalAmount', label: '结算总金额', type: 'number' }, { key: 'FreightCost', label: '海运费', type: 'number' },
      { key: 'Remark', label: '备注', type: 'textarea' },
    ],
  },
  complaint: {
    billType: 'complaint', title: '客诉单',
    columns: [
      { key: 'BillNo', label: '单据号' }, { key: 'ComplaintDate', label: '日期', type: 'date' },
      { key: 'CustomerId', label: '客户' }, { key: 'ComplaintType', label: '客诉类型' },
      { key: 'Status', label: '状态', status: true },
    ],
    fields: [
      { key: 'ComplaintDate', label: '客诉日期', type: 'date' }, { key: 'CustomerId', label: '客户', type: 'number' },
      { key: 'SalesOrderId', label: '销售订单Id', type: 'number' }, { key: 'ComplaintType', label: '客诉类型' },
      { key: 'ResponsibleDept', label: '责任部门' }, { key: 'Description', label: '客诉描述', type: 'textarea' },
      { key: 'HandleResult', label: '处理结果', type: 'textarea' }, { key: 'Remark', label: '备注', type: 'textarea' },
    ],
  },
  'receiving-plan': {
    billType: 'receiving-plan', title: '收货计划',
    columns: [
      { key: 'BillNo', label: '单据号' }, { key: 'PlanDate', label: '日期', type: 'date' },
      { key: 'SupplierId', label: '供应商' }, { key: 'ContainerNo', label: '柜号' },
      { key: 'Status', label: '状态', status: true },
    ],
    fields: [
      { key: 'PlanDate', label: '计划日期', type: 'date' }, { key: 'SupplierId', label: '供应商', type: 'number' },
      { key: 'BookingNo', label: '订柜单号' }, { key: 'ContainerType', label: '柜型', type: 'select', options: CONTAINER_OPTS },
      { key: 'ContainerNo', label: '柜号' }, { key: 'ExpectedArrivalDate', label: '预计到货日期', type: 'date' },
      { key: 'PortId', label: '目的港Id', type: 'number' }, { key: 'Destination', label: '目的地' },
      { key: 'TotalQuantity', label: '总件数', type: 'number' }, { key: 'Remark', label: '备注', type: 'textarea' },
    ],
  },
  booking: {
    billType: 'booking', title: '订柜信息',
    columns: [
      { key: 'BillNo', label: '单据号' }, { key: 'BookingDate', label: '日期', type: 'date' },
      { key: 'CustomerId', label: '客户' }, { key: 'ContainerType', label: '柜型' },
      { key: 'Status', label: '状态', status: true },
    ],
    fields: [
      { key: 'BookingDate', label: '订柜日期', type: 'date' }, { key: 'CustomerId', label: '客户', type: 'number' },
      { key: 'SupplierId', label: '供应商', type: 'number' }, { key: 'ContainerType', label: '柜型', type: 'select', options: CONTAINER_OPTS },
      { key: 'ShippingCompany', label: '船公司' }, { key: 'VoyageNo', label: '航次' },
      { key: 'SailingDate', label: '开船日期', type: 'date' }, { key: 'DeparturePort', label: '起运港' },
      { key: 'DestinationPort', label: '目的港' }, { key: 'Remark', label: '备注', type: 'textarea' },
    ],
  },
  'pre-loading': {
    billType: 'pre-loading', title: '预装柜单',
    columns: [
      { key: 'BillNo', label: '单据号' }, { key: 'LoadingDate', label: '日期', type: 'date' },
      { key: 'ContainerNo', label: '柜号' }, { key: 'TotalCartons', label: '总箱数', type: 'money' },
      { key: 'Status', label: '状态', status: true },
    ],
    fields: [
      { key: 'LoadingDate', label: '装柜日期', type: 'date' }, { key: 'BookingId', label: '订柜Id', type: 'number' },
      { key: 'ContainerNo', label: '柜号' }, { key: 'SealNo', label: '封条号' },
      { key: 'TotalCartons', label: '总箱数', type: 'number' }, { key: 'TotalWeight', label: '总毛重', type: 'number' },
      { key: 'TotalVolume', label: '总体积', type: 'number' }, { key: 'Remark', label: '备注', type: 'textarea' },
    ],
  },
  'loading-list': {
    billType: 'loading-list', title: '装柜清单',
    columns: [
      { key: 'BillNo', label: '单据号' }, { key: 'LoadingDate', label: '日期', type: 'date' },
      { key: 'CustomerId', label: '客户' }, { key: 'TotalCartons', label: '总箱数', type: 'money' },
      { key: 'Status', label: '状态', status: true },
    ],
    fields: [
      { key: 'LoadingDate', label: '装柜日期', type: 'date' }, { key: 'PreLoadingId', label: '预装柜单Id', type: 'number' },
      { key: 'ContainerNo', label: '柜号' }, { key: 'CustomerId', label: '客户', type: 'number' },
      { key: 'ShippingMark', label: '唛头' }, { key: 'TotalCartons', label: '总箱数', type: 'number' },
      { key: 'TotalWeight', label: '总毛重', type: 'number' }, { key: 'TotalVolume', label: '总体积', type: 'number' },
      { key: 'Remark', label: '备注', type: 'textarea' },
    ],
  },
});
