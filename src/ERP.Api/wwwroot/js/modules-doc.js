/* ============ 系统设置 + 询价 + 订单模块 ============ */
Object.assign(MODULES, {
  'sys-parameter': {
    title: '系统参数', api: '/api/sys/parameters',
    columns: [
      { key: 'paramKey', label: '参数键' }, { key: 'paramValue', label: '参数值' },
      { key: 'paramName', label: '参数名称' }, { key: 'description', label: '说明' },
    ],
    fields: [
      { key: 'paramKey', label: '参数键', required: true },
      { key: 'paramValue', label: '参数值', required: true },
      { key: 'paramName', label: '参数名称' }, { key: 'description', label: '说明', type: 'textarea' },
    ],
  },
  'doc-rule': {
    title: '单据号规则', api: '/api/sys/document-number-rules',
    columns: [{ key: 'ruleCode', label: '规则编码' }, { key: 'ruleName', label: '规则名称' }, { key: 'prefix', label: '前缀' }],
    fields: [
      { key: 'ruleCode', label: '规则编码', required: true }, { key: 'ruleName', label: '规则名称', required: true },
      { key: 'prefix', label: '前缀' }, { key: 'dateFormat', label: '日期格式' },
      { key: 'serialLength', label: '流水位数', type: 'number' }, { key: 'separator', label: '分隔符' },
    ],
  },
  'client-limit': {
    title: '客户端限制', api: '/api/sys/client-limits',
    columns: [{ key: 'limitType', label: '限制类型' }, { key: 'limitValue', label: '限制值' }],
    fields: [
      { key: 'limitType', label: '限制类型', type: 'select', options: [{ value: '1', label: 'IP 地址' }, { value: '2', label: '机器码' }] },
      { key: 'limitValue', label: '限制值', required: true },
    ],
  },
  'sys-log': {
    title: '系统日志', api: '/api/sys/logs', readonly: true,
    columns: [
      { key: 'userName', label: '用户' }, { key: 'module', label: '模块' }, { key: 'action', label: '操作' },
      { key: 'path', label: '路径' }, { key: 'ipAddress', label: 'IP' }, { key: 'createdAt', label: '时间', type: 'date' },
    ],
    fields: [],
  },
  'inquiry-new': {
    title: '询价单', api: '/api/inquiries', canSubmit: true,
    columns: [
      { key: 'inquiryNo', label: '询价单号' }, { key: 'inquiryDate', label: '日期', type: 'date' },
      { key: 'customerId', label: '客户Id' }, { key: 'salesmanId', label: '业务员Id' }, { key: 'status', label: '状态', status: true },
    ],
    fields: [
      { key: 'inquiryDate', label: '询价日期', type: 'date' }, { key: 'customerId', label: '客户Id', type: 'number' },
      { key: 'contactPerson', label: '联系人' }, { key: 'contactPhone', label: '联系电话' },
      { key: 'salesmanId', label: '业务员Id', type: 'number' },
      { key: 'currency', label: '币种', type: 'select', options: CURRENCY_OPTS },
      { key: 'exchangeRate', label: '汇率', type: 'number', default: 1 },
      { key: 'validDays', label: '有效期(天)', type: 'number', default: 30 },
      { key: 'remark', label: '备注', type: 'textarea' },
    ],
  },
  'sales-order': {
    title: '销售订单', api: '/api/sales-orders', canSubmit: true,
    columns: [
      { key: 'orderNo', label: '订单号' }, { key: 'orderDate', label: '日期', type: 'date' },
      { key: 'customerId', label: '客户Id' }, { key: 'totalAmount', label: '总额', type: 'money' },
      { key: 'depositAmount', label: '定金', type: 'money' }, { key: 'status', label: '状态', status: true },
    ],
    fields: [
      { key: 'orderDate', label: '订单日期', type: 'date' }, { key: 'customerId', label: '客户Id', type: 'number' },
      { key: 'salesmanId', label: '业务员Id', type: 'number' },
      { key: 'currency', label: '币种', type: 'select', options: CURRENCY_OPTS },
      { key: 'exchangeRate', label: '汇率', type: 'number', default: 1 },
      { key: 'depositRatio', label: '定金比例(%)', type: 'number', default: 30 },
      { key: 'paymentTerms', label: '付款条件' }, { key: 'deliveryDate', label: '交货日期', type: 'date' },
      { key: 'shippingMethod', label: '运输方式' }, { key: 'remark', label: '备注', type: 'textarea' },
    ],
  },
  'purchase-order': {
    title: '采购订单', api: '/api/purchase-orders', canSubmit: true,
    columns: [
      { key: 'orderNo', label: '采购单号' }, { key: 'orderDate', label: '日期', type: 'date' },
      { key: 'supplierId', label: '供应商Id' }, { key: 'totalAmount', label: '总额', type: 'money' }, { key: 'status', label: '状态', status: true },
    ],
    fields: [
      { key: 'orderDate', label: '订单日期', type: 'date' }, { key: 'supplierId', label: '供应商Id', type: 'number' },
      { key: 'buyerId', label: '采购员Id', type: 'number' },
      { key: 'currency', label: '币种', type: 'select', options: CURRENCY_OPTS },
      { key: 'exchangeRate', label: '汇率', type: 'number', default: 1 },
      { key: 'paymentTerms', label: '付款条件' }, { key: 'deliveryDate', label: '交货日期', type: 'date' },
      { key: 'remark', label: '备注', type: 'textarea' },
    ],
  },
});
