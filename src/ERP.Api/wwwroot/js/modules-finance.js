/* ============ 账务管理模块 ============ */
Object.assign(MODULES, {
  'deposit-apply': {
    title: '定金申请单', api: '/api/finance/deposit-applies', canSubmit: true,
    columns: [
      { key: 'applyNo', label: '申请单号' }, { key: 'applyDate', label: '日期', type: 'date' },
      { key: 'customerId', label: '客户Id' }, { key: 'amount', label: '金额', type: 'money' }, { key: 'status', label: '状态', status: true },
    ],
    fields: [
      { key: 'applyDate', label: '申请日期', type: 'date' }, { key: 'customerId', label: '客户Id', type: 'number' },
      { key: 'amount', label: '申请金额', type: 'number' },
      { key: 'currency', label: '币种', type: 'select', options: CURRENCY_OPTS },
      { key: 'exchangeRate', label: '汇率', type: 'number', default: 1 },
      { key: 'bankAccount', label: '银行账户' }, { key: 'payee', label: '收款方' },
      { key: 'reason', label: '申请事由', type: 'textarea' },
    ],
  },
  'payment-apply': {
    title: '货款申请单', api: '/api/finance/payment-applies', canSubmit: true,
    columns: [
      { key: 'applyNo', label: '申请单号' }, { key: 'applyDate', label: '日期', type: 'date' },
      { key: 'customerId', label: '客户Id' }, { key: 'amount', label: '金额', type: 'money' }, { key: 'status', label: '状态', status: true },
    ],
    fields: [
      { key: 'applyDate', label: '申请日期', type: 'date' }, { key: 'customerId', label: '客户Id', type: 'number' },
      { key: 'amount', label: '申请金额', type: 'number' },
      { key: 'currency', label: '币种', type: 'select', options: CURRENCY_OPTS },
      { key: 'exchangeRate', label: '汇率', type: 'number', default: 1 },
      { key: 'bankAccount', label: '银行账户' }, { key: 'payee', label: '收款方' },
      { key: 'reason', label: '申请事由', type: 'textarea' },
    ],
  },
  payment: {
    title: '付款单', api: '/api/finance/payments', canSubmit: true,
    columns: [
      { key: 'paymentNo', label: '付款单号' }, { key: 'paymentDate', label: '日期', type: 'date' },
      { key: 'supplierId', label: '供应商Id' }, { key: 'amount', label: '金额', type: 'money' }, { key: 'status', label: '状态', status: true },
    ],
    fields: [
      { key: 'paymentDate', label: '付款日期', type: 'date' }, { key: 'supplierId', label: '供应商Id', type: 'number' },
      { key: 'amount', label: '付款金额', type: 'number' },
      { key: 'currency', label: '币种', type: 'select', options: CURRENCY_OPTS },
      { key: 'paymentMethod', label: '付款方式', type: 'select', options: PAYMENT_OPTS },
      { key: 'bankAccount', label: '银行账户' },
    ],
    /* ERP-049：付款引用登记入口（工具栏，始终可见；登记「这笔付款指向哪几张采购订单」的引用证据，
       只允许同供应商 + 同币种且未取消的采购订单；不是付款凭证 / 应付账款核销 / 发票核销 / 税务判断 / 供应商余额） */
    extraActions: [
      { label: '💳 付款引用', title: '登记本页付款单指向的采购订单引用证据，并查看付款单侧已引用 / 未引用金额（只写引用证据，不会执行付款、不会结算或核销）', onclick: 'openSupplierPaymentAllocationRegister()' },
      /* ERP-066：付款发票引用登记入口（工具栏，始终可见；登记「这笔付款指向哪几张已登记采购发票」的引用证据，
         只允许同供应商 + 同币种的已登记发票；不是付款凭证 / 应付账款核销 / 发票认证 / 税务申报 / 供应商余额，
         且与 ERP-049 的采购订单引用金额分别记录、绝不相加） */
      { label: '🧾 付款发票引用', title: '登记本页付款单指向的已登记采购发票引用证据，并查看付款单侧已引用 / 未引用金额与发票侧未引用含税总额（只写引用证据，不会执行付款、不会核销或认证，也不改写付款单与发票）', onclick: 'openSupplierPaymentInvoiceAllocationRegister()' },
    ],
  },
  'container-settlement': {
    title: '装柜结算单', api: '/api/finance/container-settlements', canSubmit: true,
    columns: [
      { key: 'settlementNo', label: '结算单号' }, { key: 'settlementDate', label: '日期', type: 'date' },
      { key: 'customerId', label: '客户Id' }, { key: 'totalAmount', label: '总金额', type: 'money' }, { key: 'status', label: '状态', status: true },
    ],
    fields: [
      { key: 'settlementDate', label: '结算日期', type: 'date' }, { key: 'customerId', label: '客户Id', type: 'number' },
      { key: 'totalAmount', label: '结算总金额', type: 'number' }, { key: 'freightCost', label: '海运费', type: 'number' },
      { key: 'otherCost', label: '其他费用', type: 'number' },
    ],
    /* ERP-060：分摊证据（只读）——本单持久化金额字段只作原值回显，分摊证据不参与结算计算、也不回写本单 */
    rowActions: [
      { label: '分摊证据', icon: '🧾', title: '按本装柜结算单显式关联的装柜清单只读查看 ERP-042 费用分摊证据（按币种分组的客户分摊金额与比例、分摊方法与基数、未分摊参考、已作废历史；结算金额只作原值回显，分摊证据不参与结算计算，也不是结算确认 / 应收应付结论）', onclick: 'showContainerAllocationEvidence' },
    ],
    extraActions: [
      { label: '🧾 分摊证据工作台', title: '打开只读分摊证据工作台（按显式柜号 / 装柜清单号 / 批次号 / 币种 / 批次状态 / 客户 Id 筛选，分页有界；按币种分组、不合并不换算，缺失显示「无 / 未知」，不改写任何单据、不回填历史）', onclick: 'openContainerAllocationEvidenceWorkspace()' },
    ],
  },
  'bulk-settlement': {
    title: '散货结算单', api: '/api/finance/bulk-settlements', canSubmit: true,
    columns: [
      { key: 'settlementNo', label: '结算单号' }, { key: 'settlementDate', label: '日期', type: 'date' },
      { key: 'customerId', label: '客户Id' }, { key: 'totalAmount', label: '总金额', type: 'money' }, { key: 'status', label: '状态', status: true },
    ],
    fields: [
      { key: 'settlementDate', label: '结算日期', type: 'date' }, { key: 'customerId', label: '客户Id', type: 'number' },
      { key: 'totalAmount', label: '结算总金额', type: 'number' }, { key: 'freightCost', label: '海运费', type: 'number' },
    ],
  },
  receipt: {
    title: '收款单', api: '/api/finance/receipts', canSubmit: true,
    columns: [
      { key: 'receiptNo', label: '收款单号' }, { key: 'receiptDate', label: '日期', type: 'date' },
      { key: 'customerId', label: '客户Id' }, { key: 'amount', label: '金额', type: 'money' }, { key: 'status', label: '状态', status: true },
    ],
    fields: [
      { key: 'receiptDate', label: '收款日期', type: 'date' }, { key: 'customerId', label: '客户Id', type: 'number' },
      { key: 'amount', label: '收款金额', type: 'number' },
      { key: 'currency', label: '币种', type: 'select', options: CURRENCY_OPTS },
      { key: 'paymentMethod', label: '付款方式', type: 'select', options: PAYMENT_OPTS },
      { key: 'bankAccount', label: '银行账户' },
    ],
    /* ERP-053：收款引用登记入口（工具栏，始终可见；登记「这笔收款指向哪几张销售订单」的引用证据，
       只允许同客户 + 同币种且未取消的销售订单；不是到账凭证 / 应收账款台账 / 货款核销 / 客户对账单 / 税务判断） */
    extraActions: [
      { label: '🧾 收款引用', title: '登记本页收款单指向的销售订单引用证据，并查看收款单侧已引用 / 未引用金额（只写引用证据，不会真的收款、不会结算或核销，也不改写收款单与销售订单）', onclick: 'openCustomerReceiptAllocationRegister()' },
    ],
  },
  complaint: {
    title: '客诉单', api: '/api/finance/complaints', canSubmit: true,
    columns: [
      { key: 'complaintNo', label: '客诉单号' }, { key: 'complaintDate', label: '日期', type: 'date' },
      { key: 'customerId', label: '客户Id' }, { key: 'complaintType', label: '客诉类型' }, { key: 'status', label: '状态', status: true },
    ],
    fields: [
      { key: 'complaintDate', label: '客诉日期', type: 'date' }, { key: 'customerId', label: '客户Id', type: 'number' },
      { key: 'complaintType', label: '客诉类型' }, { key: 'responsibleDept', label: '责任部门' },
      { key: 'description', label: '客诉描述', type: 'textarea' }, { key: 'handleResult', label: '处理结果', type: 'textarea' },
    ],
  },
});
