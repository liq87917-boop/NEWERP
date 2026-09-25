/* ============ 系统设置 + 询价 + 订单模块 ============ */
/* ERP-008：销售订单 / 采购订单选项集（与后端字符串字段口径一致，保存时按原值写入） */
const TRADE_TERM_OPTS = [
  { value: '', label: '（未指定）' },
  { value: 'FOB', label: 'FOB 离岸价' }, { value: 'CIF', label: 'CIF 到岸价' },
  { value: 'CFR', label: 'CFR 成本加运费' }, { value: 'EXW', label: 'EXW 工厂交货' },
  { value: 'DDP', label: 'DDP 完税后交货' }, { value: '其他', label: '其他' },
];
const EXPORT_MODE_OPTS = [
  { value: '', label: '（未指定）' },
  { value: '0110', label: '0110 一般贸易' }, { value: '1039', label: '1039 市场采购' },
  { value: '9610', label: '9610 跨境电商' }, { value: '9710', label: '9710 跨境电商 B2B' },
];
const BUSINESS_NATURE_OPTS = [
  { value: '', label: '（未指定）' },
  { value: '自营出口', label: '自营出口' }, { value: '代理出口', label: '代理出口' },
  { value: '内销', label: '内销' },
];
const YES_NO_OPTS = [{ value: 'false', label: '否' }, { value: 'true', label: '是' }];
const ARRIVAL_PROGRESS_OPTS = [
  { value: '', label: '（未指定）' },
  { value: '未到货', label: '未到货' }, { value: '部分到货', label: '部分到货' }, { value: '已到货', label: '已到货' },
];
const QC_STATUS_OPTS = [
  { value: '', label: '（未指定）' },
  { value: '未验货', label: '未验货' }, { value: '验货中', label: '验货中' },
  { value: '合格', label: '合格' }, { value: '不合格', label: '不合格' }, { value: '免验', label: '免验' },
];
const SETTLEMENT_PROGRESS_OPTS = [
  { value: '', label: '（未指定）' },
  { value: '未结算', label: '未结算' }, { value: '部分结算', label: '部分结算' }, { value: '已结算', label: '已结算' },
];
/* 订单明细列（销售订单 / 采购订单共用；金额 = 数量 × 单价 自动计算） */
const ORDER_DETAIL_FIELDS = [
  { key: 'productId', label: '商品ID', type: 'number', width: '90px' },
  { key: 'productName', label: '商品名称', width: '180px' },
  { key: 'spec', label: '规格', width: '120px' },
  { key: 'unit', label: '单位', width: '70px' },
  { key: 'quantity', label: '数量', type: 'number', width: '90px' },
  { key: 'unitPrice', label: '单价', type: 'number', width: '90px' },
  { key: 'amount', label: '金额', type: 'number', width: '100px', readonly: true },
  { key: 'remark', label: '备注', width: '130px' },
];
const ORDER_DETAIL_AMOUNT = { qty: 'quantity', price: 'unitPrice', amount: 'amount', totalId: 'detail-total' };

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
    rowActions: [
      { label: '预填报价单', icon: '📝', title: '按已审核询价单带入报价单新增表单，可核对修改后保存', onclick: 'inquiryPrefillQuotation', statuses: ['Approved'] },
      { label: '转报价单', icon: '📄', title: '按已审核询价单直接生成报价单，同一询价单仅一张', onclick: 'inquiryToQuotation', statuses: ['Approved'] },
    ],
  },
  /* 销售订单（外销合同）：EF 主子表接口（/api/sales-orders），承载 ERP-008 补齐的外贸合同 /
     运输 / 来源追溯字段；页面按主子表编辑，保存后可在账单与打印中直接使用。 */
  'sales-order': {
    title: '销售订单（外销合同）', api: '/api/sales-orders', canSubmit: true,
    columns: [
      { key: 'orderNo', label: '订单号' }, { key: 'orderDate', label: '日期', type: 'date' },
      { key: 'customerPoNo', label: '客户 PO 号' }, { key: 'contractNo', label: '合同号' },
      { key: 'tradeTerms', label: '价格条款' }, { key: 'destinationPort', label: '目的港' },
      { key: 'totalAmount', label: '总额', type: 'money' },
      { key: 'depositAmount', label: '定金', type: 'money' }, { key: 'status', label: '状态', status: true },
      /* ERP-054：收款引用证据派生列（virtual，不落库）：ERP-053 有效（未作废）收款引用行的已引用金额 /
         收款单张数 / 引用行条数；由 sales-order-receipt-evidence.js 按页有界批量取数后渲染（每页一次 / 分批请求，
         绝不逐行查库），已作废 / 无效 / 无法确认证据只以 ⚠ 提示，绝不计入有效合计，也不当作已收款或应收余额 */
      { key: 'receiptEvidence', label: '收款引用证据', virtual: true, render: row => salesOrderReceiptEvidenceCellHtml(row) },
      /* ERP-056：销项发票证据派生列（virtual，不落库）：ERP-055 有效（已登记且未作废）发票证据行的已分摊金额 /
         发票张数 / 分摊行条数；由 sales-order-invoice-evidence.js 按页有界批量取数后渲染（每页一次 / 分批请求，
         绝不逐行查库），草稿 / 已作废 / 无效 / 无法确认证据只以 ⚠ 提示，绝不计入有效合计，
         也不当作已开票金额、应交税金、应收余额或结算结果 */
      { key: 'invoiceEvidence', label: '销项发票证据', virtual: true, render: row => salesOrderInvoiceEvidenceCellHtml(row) },
    ],
    fields: [
      { key: 'orderDate', label: '订单日期', type: 'date' },
      { key: 'customerId', label: '客户', type: 'ref', ref: 'customer' },
      { key: 'salesmanId', label: '业务员', type: 'ref', ref: 'employee' },
      { key: 'customerPoNo', label: '客户 PO 号' },
      { key: 'contractNo', label: '外销合同号' },
      { key: 'tradeTerms', label: '价格条款', type: 'select', options: TRADE_TERM_OPTS },
      { key: 'destinationPort', label: '目的港（文本）' },
      { key: 'portId', label: '目的港 Id（港口字典，可留空）', type: 'number' },
      { key: 'consignee', label: '收货人 Consignee（提单用）', type: 'textarea' },
      { key: 'notifyParty', label: '通知人 Notify Party（提单用）', type: 'textarea' },
      { key: 'shippingMarks', label: '唛头 Shipping Marks', type: 'textarea' },
      { key: 'sourceQuotationId', label: '来源报价单 ID（报价转订单回填）', type: 'number' },
      { key: 'sourceQuotationNo', label: '来源报价单号' },
      { key: 'sourcePiId', label: '来源形式发票 PI ID', type: 'number' },
      { key: 'sourcePiNo', label: '来源 PI 号' },
      { key: 'exportMode', label: '出口方式', type: 'select', options: EXPORT_MODE_OPTS },
      { key: 'businessNature', label: '业务性质', type: 'select', options: BUSINESS_NATURE_OPTS },
      { key: 'commissionRatio', label: '佣金/回佣比例(%)', type: 'number', default: 0 },
      { key: 'splitShipment', label: '是否分批出货', type: 'select', valueType: 'bool', options: YES_NO_OPTS },
      { key: 'inspectionRequirement', label: '验货要求（SGS / 客户验货 / 免验）', type: 'textarea' },
      { key: 'packagingRequirement', label: '包装要求（如 12 pcs/箱）', type: 'textarea' },
      { key: 'currency', label: '币种', type: 'select', options: CURRENCY_NAME_OPTS },
      { key: 'exchangeRate', label: '汇率', type: 'number', default: 7.2 },
      { key: 'depositRatio', label: '定金比例(%)', type: 'number', default: 30 },
      { key: 'paymentTerms', label: '付款条件' },
      { key: 'deliveryDate', label: '交货日期', type: 'date' },
      { key: 'shippingMethod', label: '运输方式' },
      { key: 'remark', label: '备注', type: 'textarea' },
    ],
    /* 行操作：打印预览（打印模板按 billType=sales-order，含客户 PO / 合同 / 唛头等新字段）
       ERP-019：单证中心台账生成——「生成单证」直接落库，「预填单证」带入单证中心新增表单人工核对后再保存
       ERP-032：出货与收款进度（只读派生，数量来自已审核销售出库单，收款链接复用财务核对的既有引用规则）
       ERP-046：客户订单与收款核对报表（只读派生；收款单无订单级引用 → 单独作为未关联证据列出，绝不自动匹配）
       ERP-047：销售订单变更申请登记册（只登记拟议变更：来源快照 + 拟议值对照；不审核、不套用、不改写来源订单） */
    extraActions: [
      { label: '🚚 出货 / 财务进度', onclick: 'openSalesOrderShipmentFinanceReport', title: '按客户 + 币种查看订单的已订 / 已出 / 未出数量与收款链接金额（复用出库与财务核对的权威口径，未知显示「未知」；不是应收账款台账）' },
      { label: '🧾 订单 / 收款核对', onclick: 'openSalesOrderReceiptReconciliationReport', title: '按客户 + 币种核对销售订单与收款证据：已订 / 已出 / 未出数量 + 已关联收款金额 / 未覆盖金额，未关联收款单单独列出（只读派生；不是应收账款台账 / 客户对账单 / 收款授权 / 账龄表）' },
      { label: '📝 变更申请台账', onclick: 'openSalesOrderChangeRequests()', title: '查看销售订单变更申请登记册（只登记拟议变更与来源快照对照；不审核、不套用、不改写来源订单与下游记录）' },
      /* ERP-053：客户收款引用登记入口（工具栏，始终可见；登记「收款单指向哪几张销售订单」的引用证据，
         只允许同客户 + 同币种且未取消的销售订单；不到账凭证 / 应收账款台账 / 货款核销 / 客户对账单 / 税务判断） */
      { label: '🧾 收款引用', onclick: 'openCustomerReceiptAllocationRegister()', title: '登记客户收款单指向哪些销售订单的引用证据，并查看收款单侧已引用 / 未引用金额（只写引用证据，不会真的收款、不会结算或核销，也不改写收款单与销售订单）' },
      /* ERP-055：客户销项发票证据登记入口（工具栏，始终可见；登记普票 / 专票 / 出口发票证据并把含税总额
         分摊到同客户 + 同币种且未取消的销售订单；不是开票系统 / 税务申报 / 应收账款台账 / 收款核销） */
      { label: '🧾 销项发票', onclick: 'openCustomerSalesInvoiceRegister()', title: '登记客户销项发票证据（普票 / 专票 / 出口发票），并把含税总额分摊到同客户 + 同币种未取消的销售订单（只写证据与分摊，不开票、不报税、不记账，也不改写销售订单与客户数据）' },
    ],
    rowActions: [
      { label: '出货进度', icon: '🚚', title: '查看由销售出库单派生的已订 / 已出 / 未出数量，以及既有引用可用时的已关联 / 未覆盖收款金额', onclick: 'showSalesOrderProgress' },
      { label: '执行时间线', icon: '🕘', title: '查看由订单、出库、单证与客诉记录派生的执行时间线', onclick: 'showOrderTimeline' },
      { label: '财务核对', icon: '💰', title: '按既有引用字段核对本单与定金 / 货款申请单、付款单、费用单、客诉单、收款单与结算单（只读，金额未知不推断）', onclick: 'showOrderFinanceReconciliation' },
      { label: '打印预览', icon: '🖨', title: '按打印模板预览该销售订单（含客户 PO / 合同 / 唛头等）', onclick: 'previewSalesDocPrint' },
      { label: '生成单证', icon: '📋', title: '由该销售订单生成报关单 / 装箱单 / 商业发票 / 产地证 / 提单等单证中心记录（同一类型只生成一张）', onclick: 'generateTradeDocsFromSource', statuses: ['Pending', 'Submitted', 'Approved'] },
      { label: '预填单证', icon: '📝', title: '按该销售订单带入单证草稿到单证中心新增表单（不落库，可编辑后再保存）', onclick: 'prefillTradeDocFromSource', statuses: ['Pending', 'Submitted', 'Approved'] },
      /* ERP-045：登记 / 查看本销售订单的附件引用（仅元数据；不上传 / 下载 / 预览 / 抓取任何文件，作废保留历史） */
      { label: '附件引用', icon: '📎', title: '登记或查看本销售订单的附件引用元数据（分类 / 显示名 / 不透明引用标识 / 大小 / 校验和；不上传、不下载、不预览、不抓取文件，作废保留历史且不改写本单）', onclick: 'openDocumentAttachmentReferencesForCurrentModule' },
      /* ERP-061：本销售订单的附件内容证据（上传 PDF / PNG / JPEG 证据文件；摘要与长度由服务端生成，
         下载以附件方式返回，作废必填原因并保留原始元数据与历史；不是报关 / 报税 / 银行 / 承运人确认） */
      { label: '附件证据', icon: '📎', title: '上传或查看本销售订单的附件内容证据（PDF / PNG / JPEG，服务端按文件签名复核；下载以附件方式返回，作废必填原因并保留原始文件名 / 摘要 / 历史，不改写本单）', onclick: 'openAttachmentEvidencesForCurrentModule' },
      /* ERP-047：本单的变更申请登记册（只登记拟议变更与来源快照对照；不审核、不套用、不改写本单） */
      { label: '变更申请', icon: '📝', title: '登记或查看本销售订单的变更申请（来源快照 + 拟议值对照；提交只是登记冻结，系统不批准、不套用，也不改写本单与出库 / 装柜 / 收款 / 库存 / 财务记录）', onclick: 'openSalesOrderChangeRequestsForCurrentModule' },
      /* ERP-053：以本销售订单预筛选收款引用登记册（只显示指向本单的引用行；引用只指向同客户 + 同币种订单） */
      { label: '收款引用', icon: '🧾', title: '打开客户收款引用登记册，并只显示指向本销售订单的引用行（登记「收款单指向哪些销售订单」的引用证据；只写引用证据，不会真的收款、不会结算或核销）', onclick: 'openCustomerReceiptAllocationRegister' },
      /* ERP-054：本单的收款引用证据只读视图（只按 ERP-053 持久化引用行派生；不是银行入账 / 应收余额 / 核销 / 结算结果） */
      { label: '收款引用证据', icon: '🧾', title: '查看本销售订单的收款引用证据：有效（未作废）收款引用行的已引用金额、收款单张数与未指向本单金额，以及已作废 / 无效 / 无法确认证据的逐条明细（只读派生；不是银行入账凭证、应收余额、货款核销、客户对账单或结算结果）', onclick: 'showSalesOrderReceiptEvidence' },
      /* ERP-055：以本销售订单预筛选销项发票证据登记册（只显示分摊到本单的发票证据；分摊只指向同客户 + 同币种订单） */
      { label: '销项发票', icon: '🧾', title: '打开客户销项发票证据登记册，并只显示分摊到本销售订单的发票证据（登记普票 / 专票 / 出口发票证据与分摊；不开票、不报税、不记账，也不改写本单与客户数据）', onclick: 'openCustomerSalesInvoiceRegister' },
      /* ERP-056：本单的销项发票证据只读视图（只按 ERP-055 持久化发票证据行与分摊行派生；
         不是开票系统 / 税务申报或销项税金 / 应收余额 / 核销 / 结算结果） */
      { label: '销项发票证据', icon: '🧾', title: '查看本销售订单的销项发票证据：有效（已登记且未作废）分摊行的已分摊金额、发票张数与未指向本单金额，以及草稿 / 已作废 / 无效 / 无法确认证据的逐条明细（只读派生；不是发票开具系统、税务申报或销项税金、应收余额、货款核销、客户对账单或结算结果）', onclick: 'showSalesOrderInvoiceEvidence' },
    ],
    detailKey: 'details',
    detailTitle: '订单商品明细（数量 × 单价 = 金额，自动算合计）',
    detailAmount: ORDER_DETAIL_AMOUNT,
    detailFields: ORDER_DETAIL_FIELDS,
  },
  /* 采购订单：EF 主子表接口（/api/purchase-orders），承载 ERP-008 补齐的归属客户 / 归属销售订单 /
     代垫 / 供应商确认交期 / 税率与含税 / 到货与结算进度字段 */
  'purchase-order': {
    title: '采购订单', api: '/api/purchase-orders', canSubmit: true,
    columns: [
      { key: 'orderNo', label: '采购单号' }, { key: 'orderDate', label: '日期', type: 'date' },
      { key: 'supplierId', label: '供应商Id' }, { key: 'contractNo', label: '采购合同号' },
      { key: 'owningCustomerName', label: '归属客户' }, { key: 'owningSalesOrderNo', label: '归属销售订单' },
      { key: 'arrivalProgress', label: '到货进度' }, { key: 'settlementProgress', label: '结算进度' },
      { key: 'totalAmount', label: '总额', type: 'money' }, { key: 'status', label: '状态', status: true },
      /* ERP-048：发票证据派生列（virtual，不落库）：已登记（未作废）发票已开票金额 / 未开票金额 / 覆盖状态；
         由 purchase-order-invoice-evidence.js 按页有界批量取数后渲染（每页一次 / 分批请求，绝不逐行查库），
         历史 / 无效证据只以 ⚠ 提示，绝不计入已开票金额，也不当作应付余额或结算状态 */
      { key: 'invoiceEvidence', label: '发票证据', virtual: true, render: row => purchaseOrderInvoiceEvidenceCellHtml(row) },
      /* ERP-050：付款引用证据派生列（virtual，不落库）：ERP-049 有效（未作废）付款引用行合计 / 付款单张数 /
         未指向本单金额；由 purchase-order-payment-evidence.js 按页有界批量取数后渲染（每页一次 / 分批请求，
         绝不逐行查库），已作废 / 无效 / 无法确认证据只以 ⚠ 提示，绝不计入有效合计，也不当作已付款或应付余额 */
      { key: 'paymentEvidence', label: '付款引用证据', virtual: true, render: row => purchaseOrderPaymentEvidenceCellHtml(row) },
      /* ERP-067：已分配付款引用证据派生列（virtual，不落库）：ERP-066 有效（未作废、发票仍为已登记）的
         「付款单 → 采购发票」引用行合计（发票级）与可安全归属本订单的金额 / 付款单张数 / 未指向发票金额；
         由 purchase-order-invoice-payment-evidence.js 按页有界批量取数后渲染（每页一次 / 分批请求，绝不逐行查库），
         已作废 / 发票失效 / 无效 / 无法确认证据只以 ⚠ 提示，绝不计入有效合计，也不当作已付款、应付余额或结算结果，
         并绝不与上一列的「付款引用证据」相加 */
      { key: 'invoicePaymentEvidence', label: '付款发票证据', virtual: true, render: row => purchaseOrderInvoicePaymentEvidenceCellHtml(row) },
    ],
    fields: [
      { key: 'orderDate', label: '订单日期', type: 'date' },
      { key: 'supplierId', label: '供应商', type: 'ref', ref: 'supplier' },
      { key: 'buyerId', label: '采购员', type: 'ref', ref: 'employee' },
      { key: 'contractNo', label: '采购合同号' },
      { key: 'owningCustomerId', label: '归属客户', type: 'ref', ref: 'customer' },
      { key: 'owningCustomerName', label: '归属客户名称（冗余，可留空）' },
      { key: 'owningSalesOrderId', label: '归属销售订单 ID', type: 'number' },
      { key: 'owningSalesOrderNo', label: '归属销售订单号' },
      { key: 'advanceOnBehalf', label: '是否代垫货款', type: 'select', valueType: 'bool', options: YES_NO_OPTS },
      { key: 'currency', label: '币种', type: 'select', options: CURRENCY_NAME_OPTS },
      { key: 'exchangeRate', label: '汇率', type: 'number', default: 1 },
      { key: 'taxRate', label: '税率(%)', type: 'number', default: 0 },
      { key: 'taxIncluded', label: '单价是否含税', type: 'select', valueType: 'bool', options: YES_NO_OPTS },
      { key: 'paymentTerms', label: '付款条件' },
      { key: 'deliveryDate', label: '订单交货日期', type: 'date' },
      { key: 'supplierConfirmedDate', label: '供应商确认交期', type: 'date' },
      { key: 'arrivalProgress', label: '到货进度', type: 'select', options: ARRIVAL_PROGRESS_OPTS },
      { key: 'qcStatus', label: '验货状态', type: 'select', options: QC_STATUS_OPTS },
      { key: 'settlementProgress', label: '结算进度', type: 'select', options: SETTLEMENT_PROGRESS_OPTS },
      { key: 'portId', label: '起运港 Id（港口字典，可留空）', type: 'number' },
      { key: 'remark', label: '备注', type: 'textarea' },
    ],
    /* ERP-031：供应商采购敞口报表入口（工具栏，始终可见；只读派生，不落库，不作为应付账款台账） */
    extraActions: [
      { label: '🏭 供应商采购敞口', onclick: 'openSupplierPurchaseExposureReport', title: '按供应商 + 币种查看采购订单金额、链接可用的已结算 / 未结算金额与未链接敞口（复用执行进度 / 财务核对的权威引用口径，未知显示「未知」）' },
      /* ERP-043：供应商采购发票登记入口（工具栏，始终可见；运营证据台账，不是应付账款台账 / 税务申报 / 付款授权） */
      { label: '🧾 供应商发票', title: '登记普通发票 / 增值税专用发票 / 进口发票证据与可选的到期日 / 付款条件，并把含税总额全部或部分关联到既有采购订单（仅同供应商 + 同币种可关联；留空的到期日保持「未知」，不作应付账款台账、不作付款授权）', onclick: 'openPurchaseInvoiceRegister()' },
      /* ERP-044：供应商采购发票对账报表入口（工具栏，始终可见；只读派生，不是应付账款台账 / 付款授权 / 税务申报 / 账龄表） */
      { label: '📑 发票对账报表', title: '按供应商 + 币种核对采购订单金额、已开票金额与未开票余额，并单独显示发票未关联金额与已作废历史证据（只读派生，未知显示「未知」，不作为应付余额或付款依据）', onclick: 'openSupplierInvoiceReconciliationReport()' },
      /* ERP-049：供应商付款引用登记入口（工具栏，始终可见；登记「这笔付款指向哪几张采购订单」的引用证据，
         只允许同供应商 + 同币种且未取消的采购订单；不作付款凭证 / 应付账款核销 / 发票核销 / 税务判断 / 供应商余额） */
      { label: '💳 付款引用', title: '登记既有供应商付款单指向哪些采购订单的引用证据，并查看付款单侧已引用 / 未引用金额（只写引用证据，不会执行付款、不会结算或核销，也不改写付款单与采购订单）', onclick: 'openSupplierPaymentAllocationRegister()' },
      /* ERP-066：供应商付款 → 采购发票 引用登记入口（工具栏，始终可见；登记「这笔付款指向哪几张已登记采购发票」的引用证据，
         只允许同供应商 + 同币种的已登记发票；不作付款凭证 / 应付账款核销 / 发票认证 / 税务申报 / 供应商余额，
         且与 ERP-049 的采购订单引用金额分别记录、绝不相加） */
      { label: '🧾 付款发票引用', title: '登记既有供应商付款单指向哪些已登记采购发票的引用证据，并查看付款单侧已引用 / 未引用金额与发票侧未引用含税总额（只写引用证据，不会执行付款、不会核销或认证，也不改写付款单、发票与采购订单）', onclick: 'openSupplierPaymentInvoiceAllocationRegister()' },
    ],
    rowActions: [
      { label: '执行进度', icon: '📦', title: '查看由入库单派生的已订 / 已收 / 未收数量，以及既有引用可用时的已结算 / 未结算金额', onclick: 'showPurchaseOrderProgress' },
      { label: '执行时间线', icon: '🕘', title: '查看由订单、供应商确认交期与入库记录派生的执行时间线', onclick: 'showOrderTimeline' },
      { label: '财务核对', icon: '💰', title: '按既有引用字段核对本单与付款单、费用单、收款单与结算单（只读，金额未知不推断）', onclick: 'showOrderFinanceReconciliation' },
      /* ERP-043：以本采购订单的供应商 / 币种打开供应商发票登记册（只关联同供应商同币种订单） */
      { label: '供应商发票', icon: '🧾', title: '打开供应商采购发票登记册（普票 / 专票 / 进口，可登记可选到期日与付款条件），并以本单的供应商与币种预筛选与预填（发票只关联同供应商 + 同币种的采购订单）', onclick: 'openPurchaseInvoiceRegister' },
      /* ERP-048：本单发票证据详情（只读派生：已登记「已开票」金额 / 未开票金额 / 发票张数 / 覆盖状态，
         并逐条列出草稿 / 已作废 / 无效（供应商 / 币种不一致）/ 无法确认的历史证据；不改写本单，也不认定应付余额或付款） */
      { label: '发票证据', icon: '🧾', title: '查看本采购订单的发票证据：已登记（未作废）发票的已开票金额、未开票金额、发票张数与覆盖状态，以及草稿 / 已作废 / 无效 / 无法确认证据的逐条明细（只读派生；不是应付余额、付款授权、税务申报或结算状态）', onclick: 'showPurchaseOrderInvoiceEvidence' },
      /* ERP-050：本单付款引用证据详情（只读派生：ERP-049 有效引用行合计 / 付款单张数 / 未指向本单金额，
         并逐条列出已作废 / 无效（供应商 / 币种或快照不一致）/ 无法确认的历史证据；不改写本单，也不认定已付款或应付余额） */
      { label: '付款引用证据', icon: '💳', title: '查看本采购订单的付款引用证据：有效（未作废）付款引用行的已引用金额、付款单张数与未指向本单金额，以及已作废 / 无效 / 无法确认证据的逐条明细（只读派生；不是银行付款凭证、应付余额、发票核销或结算结果）', onclick: 'showPurchaseOrderPaymentEvidence' },
      /* ERP-067：本单已分配付款引用证据详情（只读派生：ERP-066 有效「付款单 → 采购发票」引用行合计（发票级）、
         可安全归属本订单的金额（仅关联本订单的发票）与付款单 / 发票张数，并逐条列出已作废 / 发票失效 / 无效 /
         无法确认证据；不改写本单、发票与付款单，也不认定已付款、应付余额或结算结果） */
      { label: '付款发票证据', icon: '🧾', title: '查看本采购订单的已分配付款引用证据：有效（未作废、发票仍为已登记）引用行的发票级金额、可安全归属本订单的金额、付款单与发票张数，以及已作废 / 发票失效 / 无效 / 无法确认证据的逐条明细（只读派生；不是总账、法定对账单、税务申报、付款授权或结算确认，也不与「付款引用证据」相加）', onclick: 'showPurchaseOrderInvoicePaymentEvidence' },
      /* ERP-049：以本采购订单预筛选付款引用登记册（只显示指向本单的引用行；引用只指向同供应商 + 同币种订单） */
      { label: '付款引用', icon: '💳', title: '打开供应商付款引用登记册，并只显示指向本采购订单的引用行（登记「付款单指向哪些采购订单」的引用证据；只写引用证据，不会执行付款、不会结算或核销）', onclick: 'openSupplierPaymentAllocationRegister' },
      /* ERP-066：以本采购订单的供应商与币种打开「付款 → 采购发票」引用登记册（只显示同供应商 + 同币种已登记发票的引用证据） */
      { label: '付款发票引用', icon: '🧾', title: '打开供应商付款发票引用登记册，并以本单的供应商与币种预筛选（只登记指向同供应商 + 同币种已登记采购发票的引用证据；不会执行付款、不会核销或认证）', onclick: 'openSupplierPaymentInvoiceAllocationRegister' },
      /* ERP-045：登记 / 查看本采购订单的附件引用（仅元数据；不上传 / 下载 / 预览 / 抓取任何文件，作废保留历史） */
      { label: '附件引用', icon: '📎', title: '登记或查看本采购订单的附件引用元数据（分类 / 显示名 / 不透明引用标识 / 大小 / 校验和；不上传、不下载、不预览、不抓取文件，作废保留历史且不改写本单）', onclick: 'openDocumentAttachmentReferencesForCurrentModule' },
      /* ERP-061：本采购订单的附件内容证据（上传 PDF / PNG / JPEG 证据文件；摘要与长度由服务端生成，
         下载以附件方式返回，作废必填原因并保留原始元数据与历史；不是报关 / 报税 / 银行 / 供应商确认） */
      { label: '附件证据', icon: '📎', title: '上传或查看本采购订单的附件内容证据（PDF / PNG / JPEG，服务端按文件签名复核；下载以附件方式返回，作废必填原因并保留原始文件名 / 摘要 / 历史，不改写本单）', onclick: 'openAttachmentEvidencesForCurrentModule' },
      /* ERP-063：本单的**验货记录**附件证据（同一附件证据模型，归属类型 QualityInspection，
         权威记录 = 既有采购订单上的 QC 字段；与上一行的「附件证据」是两个各自独立的归属类型，
         互不共享、互不改派；上传 / 读取 / 作废都不改写验货状态与到货进度，也不推断合格 / 不合格） */
      { label: '验货证据', icon: '🔍', title: '上传或查看本采购订单「验货记录」的附件内容证据（PDF / PNG / JPEG 报告或图片，服务端按文件签名复核；下载以附件方式返回，作废必填原因并保留原始文件名 / 摘要 / 历史；不改写验货状态与到货进度，也不把上传当作验货合格 / 不合格判定、质量认证或出运许可）', onclick: 'openQualityInspectionEvidencesForCurrentModule' },
      { label: '打印预览', icon: '🖨', title: '按打印模板预览该采购订单（含归属客户 / 执行进度等）', onclick: 'previewSalesDocPrint' },
    ],
    detailKey: 'details',
    detailTitle: '采购商品明细（数量 × 单价 = 金额，自动算合计）',
    detailAmount: ORDER_DETAIL_AMOUNT,
    detailFields: ORDER_DETAIL_FIELDS,
  },
});
