/* ============ 复用枚举选项 ============ */
const CURRENCY_OPTS = [
  { value: '1', label: '人民币 CNY' }, { value: '2', label: '美元 USD' },
  { value: '3', label: '欧元 EUR' }, { value: '4', label: '港币 HKD' },
];
/* 币种（枚举名口径）：EF 主子表单据（报价单 / PI）的接口按枚举名返回币种，
   选项值必须使用枚举名（USD/EUR…）才能正确回显与保存 */
const CURRENCY_NAME_OPTS = [
  { value: 'USD', label: 'USD 美元' }, { value: 'CNY', label: 'CNY 人民币' },
  { value: 'EUR', label: 'EUR 欧元' }, { value: 'HKD', label: 'HKD 港币' },
];
const CONTAINER_OPTS = [
  { value: '20', label: '20GP 小柜' }, { value: '40', label: '40GP 平柜' },
  { value: '41', label: '40HQ 高柜' }, { value: '45', label: '45HQ 高柜' }, { value: '0', label: '散货 LCL' },
];
const PAYMENT_OPTS = [
  { value: '1', label: '银行转账' }, { value: '2', label: '电汇' },
  { value: '3', label: '信用证' }, { value: '4', label: '现金' },
];

/* ============ 数据字典引用字段（ERP-036：客户「指定货代」） ============
   引用「其他资料」字典项的字段（如客户指定货代）在字典项被停用 / 删除后，
   历史引用仍要照常显示，但必须显式标注不可用，不能静默消失、也不能再被新选中。
   标注文案与服务端 CustomerForwarderRules.UnavailableMark 保持一致。 */
const REF_UNAVAILABLE_MARK = '（已停用/不可用）';

/* 引用字段的展示文案：可用时只显示名称，不可用时名称 + 显式标注 */
function refDisplayName(name, available) {
  if (!name) return '';
  return available === false ? name + REF_UNAVAILABLE_MARK : name;
}

/* 引用字段单元格 HTML：值来自服务端（字典项名称快照），必须转义后再拼接标注 */
function refCellHtml(name, available) {
  if (!name) return '';
  const mark = available === false ? `<span class="text-muted">${REF_UNAVAILABLE_MARK}</span>` : '';
  return `${escapeHtml(name)}${mark}`;
}

/* ============ 基础资料模块配置 ============ */
const MODULES = {
  customer: {
    title: '客户资料', api: '/api/base/customers',
    columns: [
      { key: 'customerCode', label: '客户编码' }, { key: 'customerName', label: '客户名称' },
      { key: 'contactPerson', label: '联系人' }, { key: 'phone', label: '电话' },
      { key: 'country', label: '国家' }, { key: 'currency', label: '币种' },
      { key: 'tradeTerms', label: '贸易条款' },
      /* ERP-036：指定货代（主数据指引）。字典项被停用 / 删除后仍显示当时的名称快照，并显式标注不可用 */
      { key: 'forwarderName', label: '指定货代', render: row => refCellHtml(row.forwarderName, row.forwarderAvailable) },
      { key: 'creditLimit', label: '信用额度', type: 'money' },
      { key: 'creditDays', label: '账期(天)' }, { key: 'depositRatio', label: '定金比例%' },
    ],
    fields: [
      { key: 'customerCode', label: '客户编码', required: true },
      { key: 'customerName', label: '客户名称', required: true },
      { key: 'englishName', label: '英文名称' }, { key: 'contactPerson', label: '联系人' },
      { key: 'phone', label: '电话' }, { key: 'email', label: '邮箱' },
      { key: 'country', label: '国家' },
      { key: 'businessNature', label: '业务性质', type: 'select', options: [
        { value: '', label: '（未指定）' }, { value: '自营出口', label: '自营出口' },
        { value: '代理出口', label: '代理出口' }, { value: '内销', label: '内销' }] },
      { key: 'currency', label: '默认币种（如 USD）' },
      { key: 'settlementMethod', label: '结算方式（按协商填写，如 T/T 30%+70%、L/C）' },
      { key: 'tradeTerms', label: '贸易条款（FOB / CIF / EXW 等）' },
      { key: 'destinationPort', label: '目的港' },
      /* ERP-036：指定货代（可选，选项来自「其他资料」中启用的 Forwarder 字典项；留空 = 未指定） */
      { key: 'forwarderId', label: '指定货代（可留空）', type: 'ref', ref: 'forwarder' },
      { key: 'creditLimit', label: '信用额度', type: 'number' },
      { key: 'creditDays', label: '账期天数（0 或留空=现结）', type: 'number' },
      { key: 'depositRatio', label: '定金比例(%)', type: 'number' },
      { key: 'commissionRatio', label: '佣金/回佣比例(%)', type: 'number' },
      { key: 'customerLevel', label: '客户等级', type: 'select', options: [
        { value: '', label: '（未指定）' }, { value: 'A', label: 'A 级' },
        { value: 'B', label: 'B 级' }, { value: 'C', label: 'C 级' }] },
      { key: 'creditStatus', label: '信用状态', type: 'select', options: [
        { value: '正常', label: '正常' }, { value: '预警', label: '预警' }, { value: '暂停', label: '暂停' }] },
      { key: 'source', label: '客户来源（展会 / 平台 / 转介绍 等）' },
      { key: 'consignee', label: '收货人 Consignee（提单用）', type: 'textarea' },
      { key: 'notifyParty', label: '通知人 Notify Party（提单用）', type: 'textarea' },
      { key: 'defaultShippingMark', label: '默认唛头', type: 'textarea' },
      { key: 'empId', label: '业务员', type: 'ref', ref: 'employee' },
      { key: 'paymentTerms', label: '付款条件（备注性说明）' }, { key: 'taxNumber', label: '税号' },
      { key: 'address', label: '地址', type: 'textarea' },
    ],
  },
  supplier: {
    title: '供应商资料', api: '/api/base/suppliers',
    columns: [
      { key: 'supplierCode', label: '供应商编码' }, { key: 'supplierName', label: '供应商名称' },
      { key: 'supplierType', label: '类型' }, { key: 'boothLocation', label: '档口位置' },
      { key: 'contactPerson', label: '联系人' }, { key: 'phone', label: '电话' },
      { key: 'settlementMethod', label: '结算方式' },
      /* ERP-038：供货商品数（启用 / 总数）。0 = 未维护货源指引，采购流程不受影响 */
      { key: 'sourcingCount', label: '供货商品', render: row => supplierSourcingCellHtml(row) },
    ],
    fields: [
      { key: 'supplierCode', label: '供应商编码', required: true },
      { key: 'supplierName', label: '供应商名称', required: true },
      { key: 'englishName', label: '英文名称' }, { key: 'contactPerson', label: '联系人' },
      { key: 'phone', label: '电话' }, { key: 'email', label: '邮箱' },
      { key: 'weChat', label: '微信 / WhatsApp' },
      { key: 'supplierType', label: '供应商类型', type: 'select', options: [
        { value: '', label: '（未指定）' }, { value: '工厂', label: '工厂' },
        { value: '档口', label: '档口（国际商贸城）' }, { value: '贸易商', label: '贸易商' },
        { value: '货代', label: '货代' }, { value: '报关行', label: '报关行' }] },
      { key: 'boothLocation', label: '档口位置（如 一区 1F-0123）' },
      { key: 'mainCategory', label: '主营品类' },
      { key: 'country', label: '国家' }, { key: 'address', label: '地址', type: 'textarea' },
      { key: 'settlementMethod', label: '结算方式（现结 / 月结30天 / 月结60天 …）' },
      { key: 'deliveryDays', label: '常规交期(天)', type: 'number' },
      { key: 'rebateRatio', label: '返点/佣金比例(%)', type: 'number' },
      { key: 'invoiceAbility', label: '开票能力（当前可不要求）', type: 'select', options: [
        { value: '不票', label: '不票' }, { value: '普票', label: '增值税普通发票' },
        { value: '专票', label: '增值税专用发票' }] },
      { key: 'taxRate', label: '开票税率(%)', type: 'number' },
      { key: 'bankName', label: '开户银行' }, { key: 'bankAccount', label: '银行账号' },
      { key: 'paymentTerms', label: '付款条件（备注性说明）' },
    ],
    /* ERP-038：供应商侧只读的「供货商品」货源关系视图（有界列表）。
       货源关系只是比价与下单前的指引：本入口不改动采购报价 / 采购订单 / 库存与任何历史单据。 */
    rowActions: [
      { label: '供货商品', icon: '🏭', title: '查看该供应商为哪些商品 / 规格供货（只读，含停用关系的历史记录；不触达采购与库存）', onclick: 'openSupplierSourcing' },
    ],
  },
  employee: {
    title: '员工资料', api: '/api/base/employees',
    columns: [
      { key: 'employeeCode', label: '员工编码' }, { key: 'employeeName', label: '姓名' },
      { key: 'department', label: '部门' }, { key: 'position', label: '职位' }, { key: 'phone', label: '电话' },
    ],
    fields: [
      { key: 'employeeCode', label: '员工编码', required: true },
      { key: 'employeeName', label: '姓名', required: true },
      { key: 'department', label: '部门' }, { key: 'position', label: '职位' },
      { key: 'phone', label: '电话' }, { key: 'email', label: '邮箱' },
      { key: 'hireDate', label: '入职日期', type: 'date' },
    ],
  },
  'expense-account': {
    title: '费用科目', api: '/api/base/expense-accounts', tree: true,
    columns: [
      { key: 'accountCode', label: '科目编码' }, { key: 'accountName', label: '科目名称' }, { key: 'accountType', label: '类型' },
    ],
    fields: [
      { key: 'accountCode', label: '科目编码', required: true },
      { key: 'accountName', label: '科目名称', required: true },
      { key: 'parentId', label: '上级科目', type: 'parent' },
      { key: 'accountType', label: '类型', type: 'select', options: [{ value: '1', label: '收入' }, { value: '2', label: '支出' }] },
      { key: 'description', label: '说明', type: 'textarea' },
    ],
  },
  warehouse: {
    title: '仓库资料', api: '/api/base/warehouses',
    columns: [
      { key: 'warehouseCode', label: '仓库编码' }, { key: 'warehouseName', label: '仓库名称' },
      { key: 'manager', label: '负责人' }, { key: 'phone', label: '电话' },
    ],
    fields: [
      { key: 'warehouseCode', label: '仓库编码', required: true },
      { key: 'warehouseName', label: '仓库名称', required: true },
      { key: 'manager', label: '负责人' }, { key: 'phone', label: '电话' },
    ],
  },
  product: {
    title: '商品资料', api: '/api/base/products', export: true,
    columns: [
      { key: 'image1', label: '图片', type: 'image' },
      { key: 'productCode', label: '商品编码' }, { key: 'productName', label: '商品名称' },
      { key: 'spec', label: '规格' }, { key: 'unit', label: '单位' },
      { key: 'unitsPerPackage', label: '装箱数' },
      { key: 'salePrice', label: '销售价', type: 'money' }, { key: 'costPrice', label: '成本价', type: 'money' },
      { key: 'refundRate', label: '退税率%' },
      { key: 'minStock', label: '安全库存' },
      /* ERP-037：规格数（启用 / 总数）。0 = 单规格商品，历史行为不变 */
      { key: 'variantCount', label: '规格数', render: row => productVariantCellHtml(row) },
      /* ERP-038：货源数（启用 / 总数）。0 = 未维护货源指引，采购流程不受影响 */
      { key: 'sourcingCount', label: '货源数', render: row => productSourcingCellHtml(row) },
    ],
    fields: [
      { key: 'productCode', label: '商品编码', required: true },
      { key: 'productName', label: '商品名称', required: true },
      { key: 'englishName', label: '英文名称' },
      { key: 'englishDeclareName', label: '英文报关品名' },
      { key: 'spec', label: '规格' },
      { key: 'unit', label: '计量单位（如 个 / 打 / 箱）' }, { key: 'category', label: '分类' },
      { key: 'hsCode', label: 'HS 编码' }, { key: 'barcode', label: '条形码' },
      { key: 'brand', label: '品牌' }, { key: 'certification', label: '认证（CE / ROHS / EN71 等）' },
      { key: 'customerItemNo', label: '客户货号 / 款号' }, { key: 'factoryItemNo', label: '工厂货号' },
      { key: 'minOrderQty', label: '起订量 MOQ', type: 'number' },
      { key: 'purchasePrice', label: '采购价', type: 'number' }, { key: 'salePrice', label: '销售价', type: 'number' },
      { key: 'costPrice', label: '成本价', type: 'number' }, { key: 'taxIncluded', label: '价格含税', type: 'select', options: [
        { value: '', label: '否' }, { value: 'true', label: '是' }] },
      { key: 'refundRate', label: '出口退税率(%)', type: 'number' },
      { key: 'minStock', label: '安全库存（低于此值触发库存预警）', type: 'number' },
      { key: 'maxStock', label: '库存上限（高于此值触发预警）', type: 'number' },
      { key: 'packageUnit', label: '装箱单位（如 箱）' },
      { key: 'unitsPerPackage', label: '每箱数量（装箱数）', type: 'number' },
      { key: 'unitConversion', label: '单位换算说明（如 1箱=12打=144个）' },
      { key: 'weight', label: '单件毛重(kg)', type: 'number' },
      { key: 'volume', label: '单件体积(m³)', type: 'number' },
      { key: 'outerLength', label: '外箱长(cm)', type: 'number' },
      { key: 'outerWidth', label: '外箱宽(cm)', type: 'number' },
      { key: 'outerHeight', label: '外箱高(cm)', type: 'number' },
      { key: 'outerWeight', label: '外箱毛重(kg)', type: 'number' },
      { key: 'volumeWeight', label: '体积重(kg)', type: 'number' },
      { key: 'image1', label: '产品图片1', type: 'image' },
      { key: 'image2', label: '产品图片2', type: 'image' },
      { key: 'image3', label: '产品图片3', type: 'image' },
      { key: '_batch', label: '批量上传图片', type: 'image-batch' },
    ],
    /* ERP-039：只读商品图片库入口（图片位 1~3 的浏览与筛选）。
       只读取商品资料已持久化的图片引用：不上传 / 覆盖 / 删除 OSS 对象、不读取存储凭据、
       服务端不抓取任何图片地址、不改写商品图片字段。 */
    extraActions: [
      { label: '🖼 图片库', onclick: 'openProductImageLibrary()', title: '打开只读商品图片库（图片位 1~3；可按商品编码 / 名称与填充状态筛选，不触达 OSS 与商品图片字段）' },
    ],

    /* ERP-037：颜色 / 尺码 SKU 规格维护（主数据子表，可选）。
       只维护规格自身：不改写历史询价 / 报价 / 订单 / 库存与库存流水，也不拆分已有库存。 */
    /* ERP-038：供应商货源关系维护（主数据关系，可选）。
       只维护货源指引：不自动选供应商、不定价，不改写采购报价 / 采购订单 / 库存与任何历史单据。 */
    rowActions: [
      { label: '颜色/尺码规格', icon: '🎨', title: '维护该商品的颜色 / 尺码 SKU 规格（可选；没有规格即单规格商品，不触达历史单据与库存）', onclick: 'openProductVariants' },
      { label: '供应商货源', icon: '🏭', title: '维护该商品（或其规格）的多供应商货源关系（仅供参考：不自动选供应商、不定价、不改写采购订单与库存）', onclick: 'openProductSuppliers' },
      { label: '图片库', icon: '🖼', title: '查看该商品的图片位 1~3（只读；不可用引用仅作文本展示，不上传 / 删除 OSS 对象、不改写图片字段）', onclick: 'openProductImageLibrary' },
    ],
  },
  'other-info': {
    title: '其他资料（数据字典）', api: '/api/base/other-infos',
    columns: [
      { key: 'infoType', label: '资料类型' }, { key: 'infoCode', label: '编码' },
      { key: 'infoName', label: '名称' }, { key: 'englishName', label: '英文名称' },
    ],
    fields: [
      { key: 'infoType', label: '资料类型', required: true, type: 'select', options: [
        { value: 'Currency', label: 'Currency 币种' },
        { value: 'ExchangeRate', label: 'ExchangeRate 汇率' },
        { value: 'Port', label: 'Port 港口 / 航线' },
        { value: 'Forwarder', label: 'Forwarder 货代' },
        { value: 'CustomsBroker', label: 'CustomsBroker 报关行' },
        { value: 'ShippingMark', label: 'ShippingMark 唛头模板' },
        { value: 'Package', label: 'Package 包装单位' },
        { value: 'TradeTerm', label: 'TradeTerm 贸易条款（FOB/CIF/EXW）' },
        { value: 'Settlement', label: 'Settlement 结算方式（T/T、L/C、D/P）' },
        { value: 'TransportMode', label: 'TransportMode 运输方式（海运/空运/快递/铁路）' },
        { value: 'ExpenseType', label: 'ExpenseType 费用类型（报关费/拖车费/港杂费…）' },
        { value: 'ExportMode', label: 'ExportMode 出口方式（0110/1039/9610/9710）' },
        { value: 'Certification', label: 'Certification 认证类型' },
        { value: 'Brand', label: 'Brand 品牌' },
        { value: 'Other', label: 'Other 其他' }] },
      { key: 'infoCode', label: '编码', required: true },
      { key: 'infoName', label: '名称', required: true },
      { key: 'englishName', label: '英文名称' },
      { key: 'isDefault', label: '设为默认', type: 'select', options: [
        { value: '', label: '否' }, { value: 'true', label: '是' }] },
      { key: 'sortOrder', label: '排序号', type: 'number' },
    ],
  },
  /* 出口退税台账（阶段 1 新增，挂在「财务结算」菜单下） */
  'tax-refund': {
    title: '出口退税台账', api: '/api/base/tax-refunds',
    columns: [
      { key: 'refundNo', label: '台账编号' }, { key: 'refundPeriod', label: '退税期间' },
      { key: 'declareNo', label: '报关单号' }, { key: 'customerName', label: '客户' },
      { key: 'exportAmount', label: '出口金额', type: 'money' }, { key: 'currency', label: '币种' },
      { key: 'refundRate', label: '退税率%' },
      { key: 'refundableAmount', label: '可退税额', type: 'money' },
      { key: 'refundedAmount', label: '已退税额', type: 'money' },
      { key: 'status', label: '状态' },
    ],
    fields: [
      { key: 'refundNo', label: '台账编号', required: true },
      { key: 'refundPeriod', label: '退税所属期间（如 2026-08）' },
      { key: 'declareNo', label: '报关单号' },
      { key: 'invoiceNo', label: '出口发票号' },
      { key: 'salesOrderNo', label: '关联销售订单号' },
      { key: 'customerId', label: '客户', type: 'ref', ref: 'customer' },
      { key: 'customerName', label: '客户名称（可覆盖）' },
      { key: 'exportAmount', label: '出口金额（原币）', type: 'number' },
      { key: 'currency', label: '币种（USD / EUR / CNY）' },
      { key: 'exchangeRate', label: '汇率', type: 'number' },
      { key: 'refundRate', label: '退税率(%)', type: 'number' },
      { key: 'refundableAmount', label: '可退税额（人民币）', type: 'number' },
      { key: 'refundedAmount', label: '已退税额（人民币）', type: 'number' },
      { key: 'declareDate', label: '申报日期', type: 'date' },
      { key: 'refundDate', label: '退税到账日期', type: 'date' },
      { key: 'status', label: '状态', type: 'select', options: [
        { value: '待申报', label: '待申报' }, { value: '已申报', label: '已申报' },
        { value: '已退税', label: '已退税' }, { value: '异常', label: '异常' }] },
      { key: 'remark', label: '备注', type: 'textarea' },
    ],
  },
  /* 费用单（阶段 2 新增，挂在「财务结算」菜单下） */
  'expense-bill': {
    title: '费用单', api: '/api/finance/expenses',
    /* 工具栏扩展：拼柜/整柜费用分摊（一柜多客户，按体积/重量/箱数/金额分摊） */
    extraActions: [
      { label: '📦 拼柜分摊', title: '一柜多客户：按体积/重量/箱数/金额分摊费用并生成费用单', onclick: 'openExpenseAllocate()' },
      /* ERP-042：分摊批次与来源留痕（分摊结果仍是本模块费用单行，额外记录批次 / 来源 / 基数 / 比例） */
      { label: '🧾 分摊批次', title: '按装柜清单的多客户参与方分摊柜级来源费用，并记录批次 / 来源 / 基数留痕；可查台账与作废（不记账、不生成收付款单）', onclick: 'openExpenseAllocationBatches()' },
    ],
    /* ERP-042：行操作——以该费用单为来源费用打开分摊批次（不可分摊的费用单会在面板中显式说明原因） */
    rowActions: [
      { label: '分摊批次', icon: '🧾', title: '以本费用单为柜级来源费用，按装柜清单的启用参与方分摊并留痕（只读预览 + 事务性生成 + 可作废）', onclick: 'openExpenseAllocationBatches' },
    ],
    columns: [
      { key: 'expenseNo', label: '费用单号' }, { key: 'expenseDate', label: '日期', type: 'date' },
      { key: 'expenseType', label: '费用类型' }, { key: 'amount', label: '金额', type: 'money' },
      { key: 'currency', label: '币种' }, { key: 'amountCny', label: '折人民币', type: 'money' },
      { key: 'refType', label: '归属' }, { key: 'refNo', label: '归属单号' },
      { key: 'allocationBase', label: '分摊基数' },
      /* ERP-042：分摊留痕（批次留痕 / 历史分摊（无批次留痕） / 未分摊；读取侧只读标注，不回填） */
      { key: 'allocationLineage', label: '分摊留痕', render: row => expenseLineageCellHtml(row) },
      { key: 'paymentStatus', label: '付款状态' },
    ],
    fields: [
      { key: 'expenseNo', label: '费用单号', required: true },
      { key: 'expenseDate', label: '费用日期', type: 'date' },
      { key: 'expenseType', label: '费用类型', type: 'select', options: [
        { value: '报关费', label: '报关费' }, { value: '拖车费', label: '拖车费' },
        { value: 'THC', label: 'THC 码头操作费' }, { value: '文件费', label: '文件费' },
        { value: '港杂费', label: '港杂费' }, { value: '仓储费', label: '仓储费' },
        { value: '快递费', label: '快递费' }, { value: '查验费', label: '查验费' },
        { value: '订舱费', label: '订舱费' }, { value: '其他', label: '其他' }] },
      { key: 'amount', label: '金额（原币）', type: 'number' },
      { key: 'currency', label: '币种', type: 'select', options: [
        { value: 'CNY', label: 'CNY 人民币' }, { value: 'USD', label: 'USD 美元' },
        { value: 'EUR', label: 'EUR 欧元' }, { value: 'HKD', label: 'HKD 港币' }] },
      { key: 'exchangeRate', label: '汇率', type: 'number' },
      { key: 'amountCny', label: '折合人民币', type: 'number' },
      { key: 'payee', label: '收款方（报关行 / 货代 / 车队 / 仓库）' },
      { key: 'refType', label: '归属类型', type: 'select', options: [
        { value: '整柜', label: '整柜' }, { value: '拼柜', label: '拼柜' },
        { value: '散货', label: '散货' }, { value: '订单', label: '订单' },
        { value: '客户', label: '客户' }, { value: '不分摊', label: '不分摊' }] },
      { key: 'refNo', label: '归属单号（柜号 / 订舱号 / 订单号）' },
      { key: 'customerId', label: '归属客户', type: 'ref', ref: 'customer' },
      { key: 'customerName', label: '客户名称（可覆盖）' },
      { key: 'allocationBase', label: '分摊基数（可后续按此规则分摊）', type: 'select', options: [
        { value: '不分摊', label: '不分摊' }, { value: '按体积', label: '按体积分摊' },
        { value: '按重量', label: '按重量分摊' }, { value: '按金额', label: '按金额分摊' },
        { value: '按箱数', label: '按箱数分摊' }, { value: '手工分摊', label: '手工分摊' }] },
      { key: 'allocationRatio', label: '手工分摊比例(%)', type: 'number' },
      { key: 'allocatedAmount', label: '分摊金额（人民币）', type: 'number' },
      { key: 'paymentStatus', label: '付款状态', type: 'select', options: [
        { value: '未付', label: '未付' }, { value: '部分', label: '部分付款' }, { value: '已付', label: '已付' }] },
      { key: 'payDate', label: '付款日期', type: 'date' },
      { key: 'paymentMethod', label: '付款方式（现金 / 转账 / 微信 …）' },
      { key: 'billNo', label: '关联付款单号' },
      { key: 'taxRate', label: '税率(%)（预留）', type: 'number' },
      { key: 'taxAmount', label: '税额（预留）', type: 'number' },
      { key: 'remark', label: '备注', type: 'textarea' },
    ],
  },
  /* 供应商比价（阶段 2 新增，挂在「供应商与采购」菜单下） */
  'purchase-quote': {
    title: '供应商比价', api: '/api/purchase/quotes',
    columns: [
      { key: 'quoteNo', label: '比价批次' }, { key: 'quoteDate', label: '报价日期', type: 'date' },
      { key: 'productName', label: '商品' }, { key: 'spec', label: '规格' },
      { key: 'quantity', label: '数量', type: 'number' },
      { key: 'supplierName', label: '供应商' }, { key: 'supplierType', label: '类型' },
      { key: 'quotePrice', label: '报价单价', type: 'money' },
      { key: 'totalAmount', label: '报价总额', type: 'money' },
      { key: 'deliveryDays', label: '交期(天)', type: 'number' },
      { key: 'status', label: '状态' },
    ],
    fields: [
      { key: 'quoteNo', label: '比价批次号（同一需求的各家报价共用）', required: true },
      { key: 'quoteDate', label: '报价日期', type: 'date' },
      { key: 'productId', label: '商品', type: 'ref', ref: 'product' },
      { key: 'productName', label: '商品名称（可覆盖）' },
      { key: 'spec', label: '规格' }, { key: 'unit', label: '单位' },
      { key: 'quantity', label: '需求数量', type: 'number' },
      { key: 'supplierId', label: '供应商', type: 'ref', ref: 'supplier' },
      { key: 'supplierName', label: '供应商名称（可覆盖）' },
      { key: 'supplierType', label: '供应商类型', type: 'select', options: [
        { value: '', label: '（未指定）' }, { value: '工厂', label: '工厂' },
        { value: '档口', label: '档口（国际商贸城）' }, { value: '贸易商', label: '贸易商' }] },
      { key: 'quotePrice', label: '报价单价', type: 'number' },
      { key: 'totalAmount', label: '报价总额（= 单价 × 数量）', type: 'number' },
      { key: 'currency', label: '币种', type: 'select', options: [
        { value: 'CNY', label: 'CNY 人民币' }, { value: 'USD', label: 'USD 美元' }] },
      { key: 'taxIncluded', label: '报价含税', type: 'select', options: [
        { value: '', label: '否' }, { value: 'true', label: '是' }] },
      { key: 'deliveryDays', label: '交期(天)', type: 'number' },
      { key: 'minOrderQty', label: '起订量 MOQ', type: 'number' },
      { key: 'paymentTerms', label: '付款条件（现结 / 月结30天 …）' },
      { key: 'isSelected', label: '选中该供应商', type: 'select', options: [
        { value: '', label: '否' }, { value: 'true', label: '是（最终选用）' }] },
      { key: 'status', label: '比价状态', type: 'select', options: [
        { value: '待比较', label: '待比较' }, { value: '已选中', label: '已选中' },
        { value: '已放弃', label: '已放弃' },
        { value: '已转采购订单', label: '已转采购订单（已生成采购订单）' }] },
      { key: 'customerId', label: '为客户询价（代理采购）', type: 'ref', ref: 'customer' },
      { key: 'customerName', label: '客户名称（可覆盖）' },
      { key: 'refOrderNo', label: '关联销售订单号（转换后为生成的采购单号）' },
      { key: 'remark', label: '选中理由 / 备注', type: 'textarea' },
    ],
    /* 行操作（仅在「已选中」的比价行显示；服务端仍会二次校验资格与重复生成）
       ERP-020：选中行 → 采购订单（带入预填打开采购订单表单 / 直接生成采购订单）
       ERP-027：批次转采购订单（该行所属批次内所有「已选中」行按供应商 + 币种合并，不合格行明确跳过） */
    extraActions: [
      { label: '📦 批次转采购订单', onclick: 'purchaseQuoteBatchToOrderByNo()', title: '按比价批次号把该批次内所有「已选中」报价行按供应商 + 币种合并生成采购订单（先取只读计划确认，不合格行明确跳过）' },
    ],
    rowActions: [
      { label: '生成采购订单', icon: '📦', title: '按该选中比价行直接生成一张采购订单（同一比价行只生成一张，来源自动留痕）', onclick: 'purchaseQuoteToOrder', statuses: ['已选中'] },
      { label: '预填采购订单', icon: '🧾', title: '按该选中比价行带入采购订单草稿到采购订单新增表单（不落库，可编辑后再保存）', onclick: 'purchaseQuotePrefillOrder', statuses: ['已选中'] },
      { label: '批次转采购订单', icon: '🧩', title: '把该行所属比价批次内所有「已选中」行按供应商 + 币种 + 归属客户合并生成采购订单（同组多行合并为一张订单，不合格行明确跳过）', onclick: 'purchaseQuoteBatchToOrder', statuses: ['已选中'] },
    ],
  },
  /* 单证中心（阶段 2 新增，挂在「出运管理」菜单下） */
  'doc-center': {
    title: '单证中心', api: '/api/trade/documents',
    /* ERP-019：列表按条件导出 Excel（与销售订单 / 采购订单导出一致的中文列头与 xlsx 附件） */
    extraActions: [
      { label: '📤 导出 Excel', onclick: 'openTradeDocExportDialog()', title: '按单证类型 / 状态 / 出具日期 / 关键字导出单证台账为 Excel' },
      /* ERP-045：附件引用登记册入口（仅元数据；不上传 / 下载 / 预览 / 抓取任何文件） */
      { label: '📎 附件引用册', onclick: 'openDocumentAttachmentReferences()', title: '查看 / 登记销售订单、采购订单、装柜清单与出口单证的附件引用元数据（分类 / 显示名 / 不透明引用标识；不上传、不下载、不预览、不抓取文件）' },
    ],
    /* ERP-019：行操作支持单条单证导出，便于把某一票单证单独发给客户或报关行
       ERP-030：单条单证接入共享打印（打印预览 / 直接打印），打印模板与打印设计沿用
       「样式设计」中登记的唯一一份 doc-center 类型模板（见 trade-doc-print.js）
       ERP-045：为本单证登记 / 查看附件引用（仅元数据；既有 FileNote 保持原样，不自动导入） */
    rowActions: [
      { label: '导出该单证', icon: '📤', title: '仅导出本行单证为 Excel（便于单独归档或发送）', onclick: 'exportTradeDocument' },
      /* ERP-052：按明细行导出本单证（每行一条记录 + 行合计；老单证无明细行时照常导出一行） */
      { label: '导出明细行', icon: '📤', title: '按明细行导出本单证：每行一条记录 + 行金额按币种分开的合计 + 箱数 / 重量合计；未登记的箱数 / 净重 / 毛重留空而不是 0；无明细行的老单证照常导出一行', onclick: 'exportTradeDocumentLines' },
      /* ERP-051：维护本单证的商品明细行快照（只有商业发票 / 装箱单允许；单证处于待制作 / 已制作时可维护） */
      { label: '商品明细行', icon: '📦', title: '维护本单证的商品明细行快照：只有商业发票（含单价与金额）与装箱单（含箱数与重量）允许；单证处于待制作 / 已制作时可新增 / 修改 / 删除，提交客户后冻结；行金额由服务端计算，不改写商品资料与来源单据', onclick: 'manageTradeDocumentItems' },
      { label: '打印预览该单证', icon: '🖨', title: '按单证打印模板预览本单证打印效果（空白字段表示台账未登记该值，系统不做推测）', onclick: 'previewTradeDocPrint' },
      { label: '直接打印该单证', icon: '🖨', title: '不弹预览，按打印模板直接输出本单证并调起浏览器打印', onclick: 'printTradeDocDirect' },
      { label: '附件引用', icon: '📎', title: '登记或查看本单证的附件引用元数据（既有「附件说明 / 存放位置」FileNote 保持原样，不自动导入、不当作已授权附件）', onclick: 'openDocumentAttachmentReferencesForCurrentModule' },
    ],
    columns: [
      { key: 'docNo', label: '单证编号' }, { key: 'docType', label: '单证类型' },
      { key: 'issueDate', label: '出具日期', type: 'date' },
      { key: 'declareNo', label: '报关单号' }, { key: 'refNo', label: '柜号/订舱号' },
      { key: 'customerName', label: '客户' },
      { key: 'amount', label: '金额', type: 'money' }, { key: 'currency', label: '币种' },
      { key: 'status', label: '状态' },
    ],
    fields: [
      { key: 'docNo', label: '单证编号', required: true },
      { key: 'docType', label: '单证类型', type: 'select', options: [
        { value: '报关单', label: '报关单' },
        { value: '装箱单', label: '装箱单 Packing List' },
        { value: '商业发票', label: '商业发票 CI' },
        { value: '形式发票', label: '形式发票 PI' },
        { value: '产地证', label: '产地证（CO / Form A / Form E）' },
        { value: '提单', label: '提单 B/L' },
        { value: '订舱确认', label: '订舱确认 SO' },
        { value: '外汇核销单', label: '外汇核销单' },
        { value: '其他', label: '其他' }] },
      { key: 'issueDate', label: '出具 / 签发日期', type: 'date' },
      { key: 'declareNo', label: '关联报关单号' },
      { key: 'refNo', label: '关联柜号 / 订舱号' },
      { key: 'salesOrderNo', label: '关联销售订单号' },
      { key: 'customerId', label: '客户', type: 'ref', ref: 'customer' },
      { key: 'customerName', label: '客户名称（可覆盖）' },
      { key: 'amount', label: '单证金额（发票金额等）', type: 'number' },
      { key: 'currency', label: '币种', type: 'select', options: [
        { value: 'USD', label: 'USD 美元' }, { value: 'CNY', label: 'CNY 人民币' },
        { value: 'EUR', label: 'EUR 欧元' }, { value: 'HKD', label: 'HKD 港币' },
        { value: 'GBP', label: 'GBP 英镑' }, { value: 'JPY', label: 'JPY 日元' }] },
      { key: 'departurePort', label: '起运港' },
      { key: 'destinationPort', label: '目的港' },
      { key: 'issuedBy', label: '制作人 / 出证机构' },
      { key: 'copies', label: '份数', type: 'number' },
      { key: 'status', label: '状态', type: 'select', options: [
        { value: '待制作', label: '待制作' }, { value: '已制作', label: '已制作' },
        { value: '已提交客户', label: '已提交客户' }, { value: '已使用', label: '已使用' }] },
      { key: 'fileNote', label: '附件说明 / 存放位置' },
      { key: 'remark', label: '备注', type: 'textarea' },
    ],
  },
  /* 客户跟进记录（阶段 2 新增，挂在「客户与市场」菜单下） */
  'customer-follow': {
    title: '客户跟进记录', api: '/api/crm/follow-ups',
    columns: [
      { key: 'followNo', label: '跟进编号' }, { key: 'followDate', label: '跟进日期', type: 'date' },
      { key: 'customerName', label: '客户' }, { key: 'followType', label: '方式' },
      { key: 'salesmanName', label: '跟进人' }, { key: 'subject', label: '主题' },
      { key: 'result', label: '结果' }, { key: 'nextFollowDate', label: '下次跟进', type: 'date' },
    ],
    fields: [
      { key: 'followNo', label: '跟进编号', required: true },
      { key: 'followDate', label: '跟进日期', type: 'date' },
      { key: 'customerId', label: '客户', type: 'ref', ref: 'customer' },
      { key: 'customerName', label: '客户名称（可覆盖）' },
      { key: 'followType', label: '跟进方式', type: 'select', options: [
        { value: '电话', label: '电话' }, { value: '微信', label: '微信 / WhatsApp' },
        { value: '邮件', label: '邮件' }, { value: '面谈拜访', label: '面谈 / 拜访' },
        { value: '展会', label: '展会（广交会 / 义乌展会）' }, { value: '寄样', label: '寄样' },
        { value: '其他', label: '其他' }] },
      { key: 'contactPerson', label: '客户方对接人' },
      { key: 'salesmanId', label: '跟进人（业务员）', type: 'ref', ref: 'employee' },
      { key: 'salesmanName', label: '跟进人姓名（可覆盖）' },
      { key: 'subject', label: '跟进主题' },
      { key: 'content', label: '跟进内容 / 会谈纪要', type: 'textarea' },
      { key: 'result', label: '跟进结果', type: 'select', options: [
        { value: '有意向', label: '有意向' }, { value: '待跟进', label: '待跟进' },
        { value: '已报价', label: '已报价' }, { value: '已成交', label: '已成交' },
        { value: '已放弃', label: '已放弃' }] },
      { key: 'nextFollowDate', label: '下次跟进日期', type: 'date' },
      { key: 'remark', label: '备注', type: 'textarea' },
    ],
  },
  /* 样品管理（阶段 2 新增，挂在「客户与市场」菜单下） */
  sample: {
    title: '样品管理', api: '/api/crm/samples',
    columns: [
      { key: 'sampleNo', label: '样品编号' }, { key: 'sampleDate', label: '样品日期', type: 'date' },
      { key: 'customerName', label: '客户' }, { key: 'productName', label: '样品名称' },
      { key: 'sampleType', label: '类型' }, { key: 'quantity', label: '数量', type: 'number' },
      { key: 'sampleFee', label: '样品费', type: 'money' }, { key: 'currency', label: '币种' },
      { key: 'result', label: '客户反馈' },
    ],
    fields: [
      { key: 'sampleNo', label: '样品编号', required: true },
      { key: 'sampleDate', label: '样品日期', type: 'date' },
      { key: 'customerId', label: '客户', type: 'ref', ref: 'customer' },
      { key: 'customerName', label: '客户名称（可覆盖）' },
      { key: 'productId', label: '商品', type: 'ref', ref: 'product' },
      { key: 'productName', label: '样品名称（可覆盖）' },
      { key: 'spec', label: '规格' },
      { key: 'sampleType', label: '样品类型', type: 'select', options: [
        { value: '打样', label: '打样（我方制作）' }, { value: '寄样', label: '寄样（寄给客户）' },
        { value: '借样', label: '借样' }, { value: '客户来样', label: '客户来样' }] },
      { key: 'quantity', label: '数量', type: 'number' },
      { key: 'unit', label: '单位' },
      { key: 'sampleFee', label: '样品费（0 = 免费）', type: 'number' },
      { key: 'currency', label: '币种', type: 'select', options: [
        { value: 'CNY', label: 'CNY 人民币' }, { value: 'USD', label: 'USD 美元' },
        { value: 'EUR', label: 'EUR 欧元' }, { value: 'HKD', label: 'HKD 港币' }] },
      { key: 'feeSettled', label: '样品费是否已结算', type: 'select', options: [
        { value: '', label: '未结算' }, { value: 'true', label: '已结算' }] },
      { key: 'sendDate', label: '寄出日期', type: 'date' },
      { key: 'express', label: '快递公司' }, { key: 'trackingNo', label: '运单号' },
      { key: 'result', label: '客户反馈结果', type: 'select', options: [
        { value: '待反馈', label: '待反馈' }, { value: '满意', label: '满意' },
        { value: '需修改', label: '需修改' }, { value: '已下单', label: '已下单' },
        { value: '未采用', label: '未采用' }] },
      { key: 'salesmanId', label: '业务员', type: 'ref', ref: 'employee' },
      { key: 'salesmanName', label: '业务员姓名（可覆盖）' },
      { key: 'remark', label: '备注', type: 'textarea' },
    ],
  },
  /* 报价单（阶段 3 新增，挂在「询报价」菜单下；主子表：主表 + 报价明细） */
  quotation: {
    title: '报价单', api: '/api/sales/quotations',
    columns: [
      { key: 'quotationNo', label: '报价单号' }, { key: 'quotationDate', label: '报价日期', type: 'date' },
      { key: 'customerName', label: '客户' }, { key: 'validUntil', label: '有效期至', type: 'date' },
      /* ERP-018：有效期治理——列表直接可见「有效期状态」（与后端 QuotationValidityRules 同口径）
         virtual: true = 派生列（接口不返回该字段），列表打印时跳过，避免出现空列 */
      { key: 'validityStatus', label: '有效期状态', virtual: true, render: row => quotationValidityBadge(row) },
      /* ERP-035：版本链——列表直接可见版本号 / 版本链根单号 / 上一版本（历史版本不隐藏，均按版本号排列出现） */
      { key: 'revisionNumber', label: '版本', virtual: true, render: row => quotationRevisionBadge(row) },
      { key: 'rootQuotationNo', label: '版本链根单号', virtual: true, render: row => quotationRootNo(row) },
      { key: 'previousRevisionNo', label: '上一版本', virtual: true, render: row => quotationPreviousNo(row) },
      { key: 'inquiryNo', label: '来源询价单' }, { key: 'currency', label: '币种' },
      { key: 'totalAmount', label: '报价总额', type: 'money' }, { key: 'totalAmountCny', label: '折人民币', type: 'money' },
      { key: 'salesmanName', label: '业务员' }, { key: 'status', label: '状态', status: true },
    ],
    fields: [
      { key: 'quotationNo', label: '报价单号（留空自动生成）' },
      { key: 'quotationDate', label: '报价日期', type: 'date' },
      { key: 'validUntil', label: '有效期至', type: 'date' },
      { key: 'customerId', label: '客户', type: 'ref', ref: 'customer' },
      { key: 'customerName', label: '客户名称' },
      { key: 'contactPerson', label: '客户对接人' },
      { key: 'contactPhone', label: '联系电话' },
      { key: 'contactEmail', label: '邮箱' },
      { key: 'tradeTerms', label: '贸易术语', type: 'select', options: [
        { value: 'FOB', label: 'FOB 离岸价' }, { value: 'CIF', label: 'CIF 到岸价' },
        { value: 'CFR', label: 'CFR 成本加运费' }, { value: 'EXW', label: 'EXW 工厂交货' },
        { value: 'DDP', label: 'DDP 完税后交货' }, { value: '其他', label: '其他' }] },
      { key: 'portOfLoading', label: '起运港' },
      { key: 'portOfDestination', label: '目的港' },
      { key: 'paymentTerms', label: '付款方式' },
      { key: 'leadTime', label: '交货期' },
      { key: 'currency', label: '币种', type: 'select', options: CURRENCY_NAME_OPTS },
      { key: 'exchangeRate', label: '汇率', type: 'number', default: 7.2 },
      { key: 'salesmanId', label: '业务员', type: 'ref', ref: 'employee' },
      { key: 'salesmanName', label: '业务员姓名（可覆盖）' },
      { key: 'inquiryId', label: '来源询价单 ID（可留空）', type: 'number' },
      { key: 'inquiryNo', label: '来源询价单号' },
      /* ERP-035：版本链（只读展示；版本号与链根 / 上一版本由服务端分配与维护，
         客户端提交这三个字段不会被写入 —— Update 只接受可议价业务字段） */
      { key: 'revisionNumber', label: '版本号（只读，服务端分配）', type: 'number' },
      { key: 'rootQuotationNo', label: '版本链根单号（只读）' },
      { key: 'previousRevisionNo', label: '上一版本（只读）' },
      { key: 'remark', label: '备注', type: 'textarea' },
    ],
    /* 行操作扩展（crud.js 的 rowActions）：草稿可审核（审核后才能转 PI），已审核可销审与转 PI */
    rowActions: [
      { label: '审核', icon: '✅', title: '审核通过（审核后才能转 PI）', onclick: 'quotationApprove', statuses: ['Pending', 'Submitted'] },
      { label: '销审', icon: '↩️', title: '退回草稿，可继续修改', onclick: 'quotationUnaudit', statuses: ['Approved'] },
      { label: '转 PI', icon: '📄', title: '把已审核的报价单转为形式发票 PI（同一报价单只能转一次）', onclick: 'quotationToPi', statuses: ['Approved'] },
      /* ERP-010：报价单 → 销售订单（带入预填打开销售订单表单 / 直接生成销售订单） */
      { label: '预填销售订单', icon: '🧾', title: '按报价单带入客户 / 币种 / 贸易与付款条款 / 目的港 / 明细，打开销售订单新增表单（可编辑后再保存）', onclick: 'quotationPrefillOrder', statuses: ['Approved'] },
      { label: '转销售订单', icon: '📦', title: '按已审核报价单直接生成一张销售订单（来源自动留痕，同一报价单仅一张）', onclick: 'quotationToOrder', statuses: ['Approved'] },
      { label: '打印预览', icon: '🖨', title: '按打印模板预览该报价单', onclick: 'previewSalesDocPrint' },
      /* ERP-018：补齐共享打印三件套——直接打印（不先弹预览）+ 打印设计（跳到样式设计并预选本单据） */
      { label: '直接打印', icon: '🖨', title: '直接调起浏览器打印该报价单（不用先看预览）', onclick: 'printSalesDocDirect' },
      { label: '打印设计', icon: '🎨', title: '打开样式设计，为「报价单」配置打印模板（抬头 / 纸张 / 字体 / 字段顺序）', onclick: 'designSalesDocPrint' },
      /* ERP-035：多轮议价——创建新版本（源版本转为只读历史）+ 版本历史（完整版本链，不隐藏历史版本） */
      { label: '创建新版本', icon: '🆕', title: '以该报价单为源复制一张新的草稿版本：服务端分配版本号并复算合计，源版本成为只读历史（审核状态与下游转换不复制）', onclick: 'quotationCreateRevision', statuses: ['Pending', 'Submitted', 'Approved', 'Completed'] },
      { label: '版本历史', icon: '🧬', title: '查看该报价单所属的完整版本链（根单号 / 版本号 / 上一版本 / 状态 / 是否已转出）并跳转到任一版本', onclick: 'quotationRevisionHistory' },
    ],
    /* 工具栏扩展按钮（ERP-018：有效期治理与成交率报表的入口，始终可见） */
    extraActions: [
      { label: '⏰ 有效期提醒', onclick: 'openQuotationValidityReminder', title: '列出已过期与即将到期（默认未来 7 天）的报价单，按紧急度排序' },
      { label: '📈 成交率报表', onclick: 'openQuotationConversionReport', title: '按业务员统计报价成交率（已转 PI / 已转销售订单 / 已完成 ÷ 有效报价单）' },
    ],
    /* 明细（主子表）：数量 × 单价 = 金额，自动算合计；主表与明细一次性保存 */
    detailKey: 'details',
    detailTitle: '报价明细（数量 × 单价 = 金额，自动算合计）',
    detailAmount: { qty: 'quantity', price: 'unitPrice', amount: 'amount', totalId: 'detail-total' },
    detailFields: [
      { key: 'productCode', label: '商品编码', width: '110px' },
      { key: 'productName', label: '商品名称', width: '170px' },
      { key: 'spec', label: '规格', width: '130px' },
      { key: 'unit', label: '单位', width: '70px' },
      { key: 'quantity', label: '数量', type: 'number', width: '90px' },
      { key: 'unitPrice', label: '单价', type: 'number', width: '90px' },
      { key: 'amount', label: '金额', type: 'number', width: '100px', readonly: true },
      { key: 'moq', label: '起订量', width: '90px' },
      { key: 'remark', label: '备注', width: '130px' },
    ],
  },

  /* 形式发票 PI（阶段 3 新增，挂在「询报价」菜单下；主子表：主表 + PI 明细）
     业务链：询价单 Inquiry（新建询价单）→ 报价单 Quotation → 形式发票 PI（本单据）→ 销售订单 */
  'proforma-invoice': {
    title: '形式发票 PI', api: '/api/sales/proforma-invoices',
    columns: [
      { key: 'piNo', label: 'PI 号' }, { key: 'piDate', label: 'PI 日期', type: 'date' },
      { key: 'customerName', label: '客户' }, { key: 'quotationNo', label: '来源报价单' },
      { key: 'currency', label: '币种' }, { key: 'totalAmount', label: 'PI 总额', type: 'money' },
      { key: 'depositRatio', label: '定金比例%' }, { key: 'depositAmount', label: '定金金额', type: 'money' },
      { key: 'salesmanName', label: '业务员' }, { key: 'status', label: '状态', status: true },
    ],
    fields: [
      { key: 'piNo', label: 'PI 号（留空自动生成）' },
      { key: 'piDate', label: 'PI 日期', type: 'date' },
      { key: 'quotationId', label: '来源报价单 ID（由报价单转 PI 自动回填）', type: 'number' },
      { key: 'quotationNo', label: '来源报价单号' },
      { key: 'customerId', label: '客户', type: 'ref', ref: 'customer' },
      { key: 'customerName', label: '客户名称' },
      { key: 'contactPerson', label: '客户对接人' },
      { key: 'contactPhone', label: '联系电话' },
      { key: 'contactEmail', label: '邮箱' },
      { key: 'consignee', label: '收货人 Consignee', type: 'textarea' },
      { key: 'notifyParty', label: '通知人 Notify Party', type: 'textarea' },
      { key: 'shippingMarks', label: '唛头 Shipping Marks', type: 'textarea' },
      { key: 'bankInfo', label: '银行信息（Beneficiary / Bank / Account / SWIFT）', type: 'textarea' },
      { key: 'tradeTerms', label: '贸易术语', type: 'select', options: [
        { value: 'FOB', label: 'FOB 离岸价' }, { value: 'CIF', label: 'CIF 到岸价' },
        { value: 'CFR', label: 'CFR 成本加运费' }, { value: 'EXW', label: 'EXW 工厂交货' },
        { value: 'DDP', label: 'DDP 完税后交货' }, { value: '其他', label: '其他' }] },
      { key: 'portOfLoading', label: '起运港' },
      { key: 'portOfDestination', label: '目的港' },
      { key: 'paymentTerms', label: '付款方式' },
      { key: 'shippingTerms', label: '运输方式 / 条款（如 By sea, FCL）' },
      { key: 'leadTime', label: '交货期' },
      { key: 'currency', label: '币种', type: 'select', options: CURRENCY_NAME_OPTS },
      { key: 'exchangeRate', label: '汇率', type: 'number', default: 7.2 },
      { key: 'depositRatio', label: '定金比例%（0~100）', type: 'number', default: 30 },
      { key: 'depositAmount', label: '定金金额（留 0 按比例自动计算）', type: 'number' },
      { key: 'salesmanId', label: '业务员', type: 'ref', ref: 'employee' },
      { key: 'salesmanName', label: '业务员姓名（可覆盖）' },
      { key: 'remark', label: '备注', type: 'textarea' },
    ],
    /* 行操作扩展（crud.js 的 rowActions）：草稿可审核/作废，已审核可销审，任何状态可打印预览 */
    rowActions: [
      { label: '审核', icon: '✅', title: '审核通过（草稿 → 已审核）', onclick: 'piApprove', statuses: ['Pending', 'Submitted'] },
      { label: '销审', icon: '↩️', title: '退回草稿，可继续修改', onclick: 'piUnaudit', statuses: ['Approved'] },
      { label: '作废', icon: '🚫', title: '作废该 PI（已转销售订单不可作废）', onclick: 'piVoid', statuses: ['Pending', 'Submitted', 'Approved'] },
      /* ERP-010：PI → 销售订单（带入预填打开销售订单表单 / 直接生成销售订单） */
      { label: '预填销售订单', icon: '🧾', title: '按 PI 带入收货人 / 通知人 / 唛头 / 币种 / 条款 / 明细，打开销售订单新增表单（可编辑后再保存）', onclick: 'piPrefillOrder', statuses: ['Approved'] },
      { label: '转销售订单', icon: '📦', title: '按已审核 PI 直接生成一张销售订单（来源 PI 与报价单一并留痕，同一 PI 仅一张）', onclick: 'piToOrder', statuses: ['Approved'] },
      { label: '打印预览', icon: '🖨', title: '按打印模板预览该 PI', onclick: 'previewSalesDocPrint' },
      /* ERP-018：补齐共享打印三件套——直接打印 + 打印设计 */
      { label: '直接打印', icon: '🖨', title: '直接调起浏览器打印该 PI（不用先看预览）', onclick: 'printSalesDocDirect' },
      { label: '打印设计', icon: '🎨', title: '打开样式设计，为「形式发票 PI」配置打印模板', onclick: 'designSalesDocPrint' },
    ],
    /* 明细（主子表）：数量 × 单价 = 金额，自动算合计；主表与明细一次性保存 */
    detailKey: 'details',
    detailTitle: 'PI 商品明细（数量 × 单价 = 金额，自动算合计）',
    detailAmount: { qty: 'quantity', price: 'unitPrice', amount: 'amount', totalId: 'detail-total' },
    detailFields: [
      { key: 'productCode', label: '商品编码', width: '110px' },
      { key: 'productName', label: '商品名称', width: '170px' },
      { key: 'spec', label: '规格', width: '130px' },
      { key: 'unit', label: '单位', width: '70px' },
      { key: 'quantity', label: '数量', type: 'number', width: '90px' },
      { key: 'unitPrice', label: '单价', type: 'number', width: '90px' },
      { key: 'amount', label: '金额', type: 'number', width: '100px', readonly: true },
      { key: 'moq', label: '起订量', width: '90px' },
      { key: 'remark', label: '备注', width: '130px' },
    ],
  },

};
