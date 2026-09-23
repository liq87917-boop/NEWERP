/* ============ 单据配置（存储过程驱动单据） ============ */
const BILL_CONFIG = {
  'sales-order': {
    billType: 'sales-order', title: '销售订单',
    columns: [
      { key: 'BillNo', label: '单据号' }, { key: 'OrderDate', label: '日期', type: 'date' },
      { key: 'CustId', label: '客户' }, { key: 'TotalAmount', label: '总额', type: 'money' },
      { key: 'Status', label: '状态', status: true },
    ],
    fields: [
      { key: 'OrderDate', label: '订单日期', type: 'date' }, { key: 'CustId', label: '客户', type: 'number' },
      { key: 'EmpId', label: '业务员', type: 'number' },
      { key: 'Currency', label: '币种', type: 'select', options: CURRENCY_OPTS },
      { key: 'ExchangeRate', label: '汇率', type: 'number' }, { key: 'DepositRatio', label: '定金比例%', type: 'number' },
      { key: 'TotalAmount', label: '总额', type: 'number' }, { key: 'DepositAmount', label: '定金', type: 'number' },
      { key: 'DeliveryDate', label: '交货日期', type: 'date' }, { key: 'ShippingMethod', label: '运输方式' },
      { key: 'Remark', label: '备注', type: 'textarea' },
    ],
  },
  'purchase-order': {
    billType: 'purchase-order', title: '采购订单',
    columns: [
      { key: 'BillNo', label: '单据号' }, { key: 'OrderDate', label: '日期', type: 'date' },
      { key: 'SupplierId', label: '供应商' }, { key: 'TotalAmount', label: '总额', type: 'money' },
      { key: 'Status', label: '状态', status: true },
    ],
    fields: [
      { key: 'OrderDate', label: '订单日期', type: 'date' }, { key: 'SupplierId', label: '供应商', type: 'number' },
      { key: 'EmpId', label: '采购员', type: 'number' },
      { key: 'Currency', label: '币种', type: 'select', options: CURRENCY_OPTS },
      { key: 'ExchangeRate', label: '汇率', type: 'number' }, { key: 'TotalAmount', label: '总额', type: 'number' },
      { key: 'PaymentTerms', label: '付款条件' }, { key: 'DeliveryDate', label: '交货日期', type: 'date' },
      { key: 'Remark', label: '备注', type: 'textarea' },
    ],
  },
  inquiry: {
    billType: 'inquiry', title: '询价单',
    columns: [
      { key: 'BillNo', label: '单据号' }, { key: 'InquiryDate', label: '日期', type: 'date' },
      { key: 'CustomerId', label: '客户' }, { key: 'ValidDays', label: '有效期' },
      { key: 'Status', label: '状态', status: true },
    ],
    fields: [
      { key: 'InquiryDate', label: '询价日期', type: 'date' }, { key: 'CustomerId', label: '客户', type: 'number' },
      { key: 'ContactPerson', label: '联系人' }, { key: 'ContactPhone', label: '联系电话' },
      { key: 'EmpId', label: '业务员', type: 'number' },
      { key: 'Currency', label: '币种', type: 'select', options: CURRENCY_OPTS },
      { key: 'ExchangeRate', label: '汇率', type: 'number' }, { key: 'ValidDays', label: '有效期(天)', type: 'number' },
      { key: 'Remark', label: '备注', type: 'textarea' },
    ],
  },
  'stock-in': {
    billType: 'stock-in', title: '采购入库',
    columns: [
      { key: 'BillNo', label: '单据号' }, { key: 'StockInDate', label: '日期', type: 'date' },
      { key: 'SupplierId', label: '供应商' }, { key: 'TotalQuantity', label: '总数量', type: 'money' },
      { key: 'Status', label: '状态', status: true },
    ],
    fields: [
      { key: 'StockInDate', label: '入库日期', type: 'date' }, { key: 'SupplierId', label: '供应商', type: 'number' },
      { key: 'WarehouseId', label: '仓库Id', type: 'number' }, { key: 'PurchaseOrderId', label: '采购订单Id', type: 'number' },
      { key: 'TotalQuantity', label: '总数量', type: 'number' }, { key: 'TotalWeight', label: '总毛重', type: 'number' },
      { key: 'TotalVolume', label: '总体积', type: 'number' }, { key: 'Remark', label: '备注', type: 'textarea' },
    ],
  },
};
