/* ============ 单据配置（补充 · ERP-018：EF 主子表单据的共享打印注册） ============
   报价单（quotation）与形式发票 PI（proforma-invoice）由 **EF 主子表接口**驱动
   （/api/sales/quotations、/api/sales/proforma-invoices），页面入口在 MODULES（modules.js），
   因此**不加入 BILL_CODE_MAP**：该映射把「菜单码 → 存储过程版单据页 /api/v2/bills/*」做路由
   （app.js 的 renderPage 优先判定 BILL_CODE_MAP），把 EF 单据挂进去会让「报价单」菜单跳到
   SP 版单据页并因缺少对应存储过程而 404。

   本文件只注册**打印相关**配置（ef: true 标记 + api + noKey + 打印字段中文标签），
   让两类单据与其余 16 种单据共用同一份 BILL_CONFIG：
     · bill-print.js 的 billFieldLabels() 优先取 BILL_CONFIG → 打印字段中文标签与 SP 单据同口径；
     · print-design.js / pd-grid.js 的「单据清单」把它们归入「业务单据」分组（不再落进基础资料分组）；
     · 打印模板仍按单据类型 code（quotation / proforma-invoice）读写，与 ERP-007 已保存的模板完全兼容；
     · 打印数据一律取自 EF 端点 `GET {api}/{id}/print`（见 QuotationController/ProformaInvoiceController），
       不使用 SP 打印路径，故 BILL_CONFIG 中的 SP 版字段（BillNo/OrderDate…）在此不适用。
   ============ */
Object.assign(BILL_CONFIG, {
  /* 报价单：有效期至 / 来源询价单 / 贸易条款 / 明细数量·单价·金额 全部来自 EF 主子表 */
  quotation: {
    billType: 'quotation', title: '报价单', ef: true,
    api: '/api/sales/quotations', noKey: 'quotationNo',
    columns: [
      { key: 'quotationNo', label: '报价单号' }, { key: 'quotationDate', label: '报价日期', type: 'date' },
      { key: 'validUntil', label: '有效期至', type: 'date' }, { key: 'customerName', label: '客户' },
      { key: 'currency', label: '币种' }, { key: 'totalAmount', label: '报价总额', type: 'money' },
      { key: 'salesmanName', label: '业务员' }, { key: 'status', label: '状态', status: true },
    ],
    fields: [
      { key: 'quotationNo', label: '报价单号' }, { key: 'quotationDate', label: '报价日期', type: 'date' },
      { key: 'validUntil', label: '有效期至', type: 'date' }, { key: 'customerName', label: '客户名称' },
      { key: 'contactPerson', label: '客户对接人' }, { key: 'contactPhone', label: '联系电话' },
      { key: 'contactEmail', label: '邮箱' }, { key: 'inquiryNo', label: '来源询价单号' },
      { key: 'tradeTerms', label: '贸易术语' }, { key: 'portOfLoading', label: '起运港' },
      { key: 'portOfDestination', label: '目的港' }, { key: 'paymentTerms', label: '付款方式' },
      { key: 'leadTime', label: '交货期' }, { key: 'currency', label: '币种' },
      { key: 'exchangeRate', label: '汇率', type: 'number' }, { key: 'totalAmount', label: '报价总额', type: 'number' },
      { key: 'totalAmountCny', label: '折人民币', type: 'number' }, { key: 'salesmanName', label: '业务员' },
      { key: 'remark', label: '备注', type: 'textarea' },
    ],
  },
  /* 形式发票 PI：在报价单字段基础上增加收货人 / 通知人 / 唛头 / 银行信息 / 定金比例与金额 */
  'proforma-invoice': {
    billType: 'proforma-invoice', title: '形式发票 PI', ef: true,
    api: '/api/sales/proforma-invoices', noKey: 'piNo',
    columns: [
      { key: 'piNo', label: 'PI 号' }, { key: 'piDate', label: 'PI 日期', type: 'date' },
      { key: 'customerName', label: '客户' }, { key: 'quotationNo', label: '来源报价单' },
      { key: 'currency', label: '币种' }, { key: 'totalAmount', label: 'PI 总额', type: 'money' },
      { key: 'depositAmount', label: '定金金额', type: 'money' }, { key: 'salesmanName', label: '业务员' },
      { key: 'status', label: '状态', status: true },
    ],
    fields: [
      { key: 'piNo', label: 'PI 号' }, { key: 'piDate', label: 'PI 日期', type: 'date' },
      { key: 'quotationNo', label: '来源报价单号' }, { key: 'customerName', label: '客户名称' },
      { key: 'contactPerson', label: '客户对接人' }, { key: 'contactPhone', label: '联系电话' },
      { key: 'contactEmail', label: '邮箱' }, { key: 'consignee', label: '收货人 Consignee' },
      { key: 'notifyParty', label: '通知人 Notify Party' }, { key: 'shippingMarks', label: '唛头 Shipping Marks' },
      { key: 'bankInfo', label: '银行信息' }, { key: 'tradeTerms', label: '贸易术语' },
      { key: 'portOfLoading', label: '起运港' }, { key: 'portOfDestination', label: '目的港' },
      { key: 'paymentTerms', label: '付款方式' }, { key: 'shippingTerms', label: '运输方式 / 条款' },
      { key: 'leadTime', label: '交货期' }, { key: 'currency', label: '币种' },
      { key: 'exchangeRate', label: '汇率', type: 'number' }, { key: 'totalAmount', label: 'PI 总额', type: 'number' },
      { key: 'totalAmountCny', label: '折人民币', type: 'number' },
      { key: 'depositRatio', label: '定金比例%', type: 'number' },
      { key: 'depositAmount', label: '定金金额', type: 'number' },
      { key: 'salesmanName', label: '业务员' }, { key: 'remark', label: '备注', type: 'textarea' },
    ],
  },
});
