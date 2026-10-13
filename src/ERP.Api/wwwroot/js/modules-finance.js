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
      /* ERP-390：来源销售订单由「选择来源」有界选择器显式回填（也可手工填写 Id / 留空显式断开）；
         选择器只回填权威来源 Id，绝不臆造订单号、绝不自动改金额 / 汇率 / 币种。
         ERP-480：声明为显式可空引用（nullable: true）——留空提交 null（未关联来源的历史语义），
         绝不回落为哨兵值 0；清空来源即「显式断开」，最终仍由服务端按权威客户 / 币种 / 状态复核。 */
      { key: 'salesOrderId', label: '来源销售订单 ID（可留空 = 未关联；建议用「选择来源」）', type: 'number', nullable: true, selector: 'finance-apply-sales-order-source' },
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
      /* ERP-391：来源销售订单由「选择来源」有界选择器显式回填（也可手工填写 Id / 留空显式断开）；
         选择器与定金申请单共用同一份实现（按当前模块路由货款申请单接口），只回填权威来源 Id，
         绝不臆造订单号、绝不自动改金额 / 汇率 / 币种。
         ERP-480：同样声明为显式可空引用（nullable: true）——留空提交 null（未关联来源的历史语义），
         绝不回落为哨兵值 0；清空来源即「显式断开」，最终仍由服务端按权威客户 / 币种 / 状态复核。 */
      { key: 'salesOrderId', label: '来源销售订单 ID（可留空 = 未关联；建议用「选择来源」）', type: 'number', nullable: true, selector: 'finance-apply-sales-order-source' },
      { key: 'bankAccount', label: '银行账户' }, { key: 'payee', label: '收款方' },
      { key: 'reason', label: '申请事由', type: 'textarea' },
    ],
  },
  payment: {
    /* ERP-479：既有「付款单」菜单的规范 EF 模块（api/finance/payments，规范 Id 口径）——
       列表 / 详情 / 保存 / 提交 / 审核 / 取消 / 删除全走规范 Id，绝不把旧存储过程族 Oid 当作规范 Id。 */
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
      /* ERP-479：来源货款申请单是**可空引用**（实体 long?）——手工填写权威申请单 Id 即显式关联来源，
         留空提交 null（声明式可空引用，绝不回落为哨兵值 0）；服务端把 null / 0 都归一为「未关联来源」，
         并按 Id 解析既有、未删除、未取消的申请单（币种一致、付款金额不超过申请金额），
         绝不按申请单号文本或金额猜测来源，也绝不自动调整金额。 */
      { key: 'paymentApplyId', label: '来源货款申请单 ID（可留空 = 未关联来源）', type: 'number', nullable: true },
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
    /* ERP-479：从规范付款单行（规范 Id）打开既有付款引用工作台 —— 复用既有登记服务与只读汇总路径，
       不新增接口、不改写单据，也绝不把付款单 Id 当作采购订单 Id 传递（那是来源订单行操作的口径）。 */
    rowActions: [
      { label: '付款引用', icon: '💳', title: '打开既有付款引用工作台（按付款单候选查看本单已引用 / 未引用金额；只写引用证据，不会执行付款、不会结算或核销）', onclick: 'openSupplierPaymentAllocationRegisterForPayment' },
      { label: '付款发票引用', icon: '🧾', title: '打开既有付款发票引用工作台（按付款单候选查看本单已引用 / 未引用金额与发票侧未引用含税总额；只写引用证据，不会核销或认证）', onclick: 'openSupplierPaymentInvoiceAllocationRegisterForPayment' },
    ],
  },
  'container-settlement': {
    /* ERP-481：既有「装柜结算单」菜单的规范 EF 模块（api/finance/container-settlements，规范 Id 口径）——
       列表 / 详情 / 保存 / 提交 / 审核 / 取消 / 删除全走规范 Id，绝不把旧存储过程族 Oid 当作规范 Id。
       单号由服务端权威生成（表单不提交结算单号），来源装柜清单由既有 ERP-392 显式选择器回填。 */
    title: '装柜结算单', api: '/api/finance/container-settlements', canSubmit: true,
    columns: [
      { key: 'settlementNo', label: '结算单号' }, { key: 'settlementDate', label: '日期', type: 'date' },
      { key: 'customerId', label: '客户Id' }, { key: 'totalAmount', label: '总金额', type: 'money' }, { key: 'status', label: '状态', status: true },
    ],
    fields: [
      { key: 'settlementDate', label: '结算日期', type: 'date' }, { key: 'customerId', label: '客户Id', type: 'number' },
      /* ERP-392：来源装柜清单由「选择来源」有界选择器显式回填（也可手工填写 Id / 留空显式断开）；
         选择器只回填权威来源 Id，绝不臆造柜号 / 清单号、绝不自动改动总金额 / 海运费 / 其他费用。
         ERP-481：声明为显式可空引用（nullable: true）——留空提交 null（未关联来源的历史语义），
         绝不回落为哨兵值 0；清空来源即「显式断开」，最终仍由服务端 ERP-384 在锁内按权威客户 / 供应商
         参与方范围与清单资格复核。 */
      { key: 'loadingListId', label: '来源装柜清单 ID（可留空 = 未关联；建议用「选择来源」）', type: 'number', nullable: true, selector: 'container-settlement-loading-source' },
      { key: 'totalAmount', label: '结算总金额', type: 'number' }, { key: 'freightCost', label: '海运费', type: 'number' },
      { key: 'otherCost', label: '其他费用', type: 'number' },
      /* ERP-481：备注随表单提交，保证「保存 → 重开 → 修改」原样往返（不再因未声明字段而在编辑时被置空）。 */
      { key: 'remark', label: '备注', type: 'textarea' },
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
    /* ERP-481：既有「散货结算单」菜单的规范 EF 模块（api/finance/bulk-settlements，规范 Id 口径）。
       散货结算单没有来源单据与币种字段，因此保持**手工 / 未关联**语义：只录入客户与金额，
       绝不臆造来源单据、绝不按备注等自由文本推断装运 / 订单来源，也不做币种换算 / 跨币种聚合 / 合计公式。 */
    title: '散货结算单', api: '/api/finance/bulk-settlements', canSubmit: true,
    columns: [
      { key: 'settlementNo', label: '结算单号' }, { key: 'settlementDate', label: '日期', type: 'date' },
      { key: 'customerId', label: '客户Id' }, { key: 'totalAmount', label: '总金额', type: 'money' }, { key: 'status', label: '状态', status: true },
    ],
    fields: [
      { key: 'settlementDate', label: '结算日期', type: 'date' }, { key: 'customerId', label: '客户Id', type: 'number' },
      { key: 'totalAmount', label: '结算总金额', type: 'number' }, { key: 'freightCost', label: '海运费', type: 'number' },
      /* ERP-481：备注随表单提交，保证「保存 → 重开 → 修改」原样往返（不再因未声明字段而在编辑时被置空）。 */
      { key: 'remark', label: '备注', type: 'textarea' },
    ],
  },
  receipt: {
    /* ERP-479：既有「收款单」菜单的规范 EF 模块（api/finance/receipts，规范 Id 口径）——
       列表 / 详情 / 保存 / 提交 / 审核 / 取消 / 删除全走规范 Id，绝不把旧存储过程族 Oid 当作规范 Id。 */
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
    /* ERP-075：收款单 → 已登记销项发票的收款分摊证据（只读派生；展示本收款单分摊到的发票与剩余收款证据，
       不改写收款单 / 发票，也不是结算确认 / 应收账款余额 / 收款核销） */
    rowActions: [
      /* ERP-479：从规范收款单行（规范 Id）打开既有「收款单 → 销售订单」引用登记工作台，
         并复用既有只读汇总路径定位到本收款单（不新增接口、不把收款单 Id 当作销售订单 Id）。 */
      { label: '收款引用', icon: '🧾', title: '打开既有收款引用工作台（按收款单候选查看本单已引用 / 未引用金额；只写引用证据，不会真的收款、不会结算或核销）', onclick: 'openCustomerReceiptAllocationRegisterForReceipt' },
      { label: '收款分摊证据', icon: '🧾', title: '查看本收款单的收款分摊证据：本维度已分摊金额、剩余未分摊收款证据，以及分摊到哪些已登记销项发票（只读派生；不是到账凭证 / 应收余额 / 货款核销 / 客户对账单 / 结算确认）', onclick: 'openCustomerSalesInvoiceCollectionAllocationForReceipt' },
    ],
  },
  complaint: {
    /* ERP-483：既有「客诉单」菜单的规范 EF 模块（api/finance/complaints，规范 Id 口径）——
       列表 / 详情 / 保存 / 提交 / 审核 / 取消 / 删除全走规范 Id，绝不把旧存储过程族 Oid 当作规范 Id。
       客诉单号由服务端权威生成（表单不提交客诉单号），来源销售订单由既有 ERP-389 显式选择器回填 /
       显式断开。客诉单只是投诉 / 质量反馈追溯：绝不自动退款、绝不调整库存、绝不取消销售订单。 */
    title: '客诉单', api: '/api/finance/complaints', canSubmit: true,
    columns: [
      { key: 'complaintNo', label: '客诉单号' }, { key: 'complaintDate', label: '日期', type: 'date' },
      { key: 'customerId', label: '客户Id' }, { key: 'complaintType', label: '客诉类型' }, { key: 'status', label: '状态', status: true },
    ],
    fields: [
      { key: 'complaintDate', label: '客诉日期', type: 'date' }, { key: 'customerId', label: '客户Id', type: 'number' },
      /* ERP-389：来源销售订单由「选择来源」有界选择器显式回填（也可手工填写 Id / 留空显式断开）；
         选择器只回填权威来源 Id，绝不臆造订单号、绝不自动改数量 / 金额。
         ERP-483：声明为显式可空引用（nullable: true）——留空提交 null（未关联来源的历史语义），
         绝不回落为哨兵值 0；清空来源即「显式断开」，最终仍由服务端 ERP-388 在锁内按权威客户与来源资格复核。 */
      { key: 'salesOrderId', label: '来源销售订单 ID（可留空 = 未关联；建议用「选择来源」）', type: 'number', nullable: true, selector: 'complaint-sales-order-source' },
      { key: 'complaintType', label: '客诉类型' }, { key: 'responsibleDept', label: '责任部门' },
      { key: 'description', label: '客诉描述', type: 'textarea' }, { key: 'handleResult', label: '处理结果', type: 'textarea' },
      /* ERP-483：备注随表单提交，保证「保存 → 重开 → 修改」原样往返（不再因未声明字段而在编辑时被置空）。 */
      { key: 'remark', label: '备注', type: 'textarea' },
    ],
  },
});

/* ============ ERP-479：规范收款单 / 付款单行 → 既有引用登记工作台 ============
   这些包装函数只被 modules-finance.js 的 rowActions 调用（rowActions 渲染时会传入行 Id，即规范收款单 / 付款单 Id）：
     - 只调用既有工作台入口函数，绝不新增接口、绝不改写任何单据；
     - 收款单：先打开既有「收款单 → 销售订单」工作台，再用其既有选择函数按**规范收款单 Id** 定位
       （craSelectReceipt 只属于收款单维度，绝不把收款单 Id 当作销售订单 Id）；
     - 付款单：只打开既有「付款单 → 采购订单」/「付款单 → 供应商采购发票」工作台，绝不把付款单 Id 传入
       以采购订单 Id 为参数的入口（那会臆造订单过滤）；工作台按既有付款单候选列表加载，
       刚保存的规范付款单即在候选内，可用既有「选择付款单」入口定位。 */
async function openCustomerReceiptAllocationRegisterForReceipt(receiptId) {
  await openCustomerReceiptAllocationRegister();
  if (receiptId && typeof craSelectReceipt === 'function') await craSelectReceipt(receiptId);
}

async function openSupplierPaymentAllocationRegisterForPayment() {
  return openSupplierPaymentAllocationRegister();
}

async function openSupplierPaymentInvoiceAllocationRegisterForPayment() {
  return openSupplierPaymentInvoiceAllocationRegister();
}
