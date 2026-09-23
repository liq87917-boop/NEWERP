/* ============ 单据配置（补充：出库/收款/付款/定金申请） ============ */
Object.assign(BILL_CONFIG, {
  'stock-out': {
    billType: 'stock-out', title: '销售出库',
    columns: [
      { key: 'BillNo', label: '单据号' }, { key: 'StockOutDate', label: '日期', type: 'date' },
      { key: 'CustomerId', label: '客户' }, { key: 'TotalQuantity', label: '总数量', type: 'money' },
      { key: 'Status', label: '状态', status: true },
    ],
    fields: [
      { key: 'StockOutDate', label: '出库日期', type: 'date' }, { key: 'CustomerId', label: '客户', type: 'number' },
      { key: 'WarehouseId', label: '仓库Id', type: 'number' }, { key: 'SalesOrderId', label: '销售订单Id', type: 'number' },
      { key: 'TotalQuantity', label: '总数量', type: 'number' }, { key: 'TotalWeight', label: '总毛重', type: 'number' },
      { key: 'TotalVolume', label: '总体积', type: 'number' }, { key: 'Remark', label: '备注', type: 'textarea' },
    ],
  },
  receipt: {
    billType: 'receipt', title: '收款单',
    columns: [
      { key: 'BillNo', label: '单据号' }, { key: 'ReceiptDate', label: '日期', type: 'date' },
      { key: 'CustomerId', label: '客户' }, { key: 'Amount', label: '金额', type: 'money' },
      { key: 'Status', label: '状态', status: true },
    ],
    fields: [
      { key: 'ReceiptDate', label: '收款日期', type: 'date' }, { key: 'CustomerId', label: '客户', type: 'number' },
      { key: 'Amount', label: '金额', type: 'number' },
      { key: 'Currency', label: '币种', type: 'select', options: CURRENCY_OPTS },
      { key: 'PaymentMethod', label: '付款方式', type: 'select', options: PAYMENT_OPTS },
      { key: 'BankAccount', label: '银行账户' }, { key: 'Remark', label: '备注', type: 'textarea' },
    ],
  },
  payment: {
    billType: 'payment', title: '付款单',
    columns: [
      { key: 'BillNo', label: '单据号' }, { key: 'PaymentDate', label: '日期', type: 'date' },
      { key: 'SupplierId', label: '供应商' }, { key: 'Amount', label: '金额', type: 'money' },
      { key: 'Status', label: '状态', status: true },
    ],
    fields: [
      { key: 'PaymentDate', label: '付款日期', type: 'date' }, { key: 'SupplierId', label: '供应商', type: 'number' },
      { key: 'PaymentApplyId', label: '申请单Id', type: 'number' }, { key: 'Amount', label: '金额', type: 'number' },
      { key: 'Currency', label: '币种', type: 'select', options: CURRENCY_OPTS },
      { key: 'PaymentMethod', label: '付款方式', type: 'select', options: PAYMENT_OPTS },
      { key: 'BankAccount', label: '银行账户' }, { key: 'Remark', label: '备注', type: 'textarea' },
    ],
  },
  'deposit-apply': {
    billType: 'deposit-apply', title: '定金申请单',
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
});
