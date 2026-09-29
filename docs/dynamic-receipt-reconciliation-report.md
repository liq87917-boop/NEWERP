# 动态客户订单与收款核对报表（ERP-164）

只读、有界的客户订单与收款核对报表预览：销售用户按有限字段白名单与有界筛选，读取当前账号数据范围内的
ERP-046 客户订单与收款核对证据。本功能为**开发期只读派生**，复用 ERP-046 的权威口径，不新增任何表 / 列 /
权限模型，不执行任意 SQL / 迁移 / 生产 SQL / 真实数据库写入，也不做部署。

> 定位：这是**运营性的订单 / 收款证据核对视图**。它**不是**应收账款台账、**不是**客户对账单、
> **不是**收款授权或结算结果，也**不是**账龄表。

## 1. 接口

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/sales-orders/dynamic-receipt-reconciliation-report` | 返回 ERP-046 订单证据字段白名单目录 + 独立的未关联收款证据字段目录（需登录 + `sales-order` 销售订单菜单授权） |
| POST | `/api/sales-orders/dynamic-receipt-reconciliation-report` | 按选定字段与有界筛选预览当前账号数据范围内的订单与收款证据，稳定分页；未关联收款证据按独立的收款字段目录单独投影，绝不并入订单行 |

预览与下载请求由既有 `OperationLogMiddleware` 按 HTTP 方法记录审计，本控制器自身不写任何操作日志。

## 2. 授权（每次请求重新校验，fail closed）

1. **身份**：`ClaimTypes.NameIdentifier` 缺失或非正整数 → 拒绝（未认证，错误码 2001），绝不猜测身份。
2. **菜单授权**：复用既有「角色 → 菜单」模块授权，要求 `sales-order`（销售订单）菜单；
   无角色 / 无该菜单授权 → 权限不足（错误码 2002）。授权每次请求都重新查询，回收后立即收敛。
3. **业务员数据范围（ERP-097 唯一权威口径）**：`SalespersonDataScopeService.ResolveAsync` 每次请求重新解析；
   特权账号（超级管理员 / 系统内置角色 / 显式特权角色）不过滤；受限制业务员只能看到
   `BaseCustomer.EmpId == 本人` 的客户及其销售订单，未映射到业务员时为空集合（fail closed）。

## 3. 数据范围与源查询顺序

数据范围过滤在 ERP-046 源查询（`SalesOrderReceiptReconciliation.ForQueryAsync` 的 `ApplyFilters`）内部完成，
**先于计数与分页**：`total` 只统计范围内订单，分页只在本页范围内稳定排序
（客户 Id + 币种 + 订单日期 + 单据 Id），翻页不重不漏。

未关联收款证据只由**本页范围内客户**的收款单派生（`FinanceReceipt.CustomerId` 只到客户级），
绝不猜测收款单到订单的匹配关系，绝不把范围外客户的收款单带进来。

## 4. 字段白名单（有限、有序）

目录由 `DynamicReceiptReconciliationReportRules.GetCatalogDto()` 统一供给（界面 / 接口共用同一份口径）。
未知字段显式拒绝（错误码 2004）；空 / 留空 = 返回全部白名单字段（目录顺序）；去重并保持请求顺序。

白名单覆盖以下 ERP-046 订单证据字段（数据类别为 `number` / `text` / `date` / `boolean` / `enum`）：

- 订单身份：`orderId` / `orderNo` / `orderDate` / `status`
- 客户与币种：`customerId` / `customerName` / `currency` / `amountDecimals`
- 订单金额（原币）：`orderAmount` / `recordedDepositAmount`
- 出货数量证据：`orderedQuantity` / `shippedQuantity` / `pendingShipmentQuantity` /
  `outstandingQuantity` / `overShippedQuantity` / `shipmentStatus` / `hasApprovedShipment` /
  `shipmentDocumentCount` / `approvedShipmentCount`
- 收款申请链接证据（定金 / 货款申请单的 `SalesOrderId` 权威引用）：`receiptCoverageStatus` /
  `receiptCoverageText` / `receiptCoverageKnown` / `linkedReceiptAmount` / `pendingReceiptAmount` /
  `uncoveredAmount` / `otherCurrencyReceiptCount` / `unapprovedReceiptCount` /
  `unattributedReceiptCount` / `unattributedReceiptsTruncated` / `overReceived`
- 收款引用登记证据（ERP-054：ERP-053 持久化引用行）：`receiptAllocationStatus` /
  `receiptAllocationEvidenceLabel` / `receiptAllocationCount` / `recordedReceiptAllocationAmount` /
  `recordedReceiptCount` / `voidedReceiptAllocationCount` / `invalidReceiptAllocationCount` /
  `unavailableReceiptAllocationCount` / `receiptAllocationTruncated` / `unreferencedOrderAmount` /
  `receiptAllocationNote`
- 销项发票登记证据（ERP-056：ERP-055 持久化发票证据行 + 分摊行）：`invoiceEvidenceStatus` /
  `invoiceEvidenceLabel` / `invoiceAllocationCount` / `recordedInvoicedAmount` /
  `recordedInvoiceCount` / `recordedInvoiceGrossAmount` / `unreferencedInvoiceAmount` /
  `invoiceUnreferencedOrderAmount` / `draftInvoiceAllocationCount` / `voidedInvoiceAllocationCount` /
  `invalidInvoiceAllocationCount` / `unavailableInvoiceAllocationCount` /
  `invoiceEvidenceTruncated` / `invoiceEvidenceNote`
- 行级说明：`note`

### 4.1 未关联收款证据字段目录（独立、有限、只读）

未关联收款证据字段目录与订单证据字段目录**完全独立**，由 `DynamicReceiptReconciliationReportRules.GetReceiptCatalog()`
统一供给（界面 / 接口共用同一份口径）。请求体通过 `ReceiptFields` 选择收款证据列：空 / 留空 = 返回全部收款证据字段
（目录顺序）；去重并保持请求顺序；未知收款证据列显式拒绝（错误码 2004），**在读取 ERP-046 源数据之前**即失败（fail closed）。

未关联收款证据字段目录（数据类别为 `number` / `text` / `date` / `enum`）：

- 收款单身份：`receiptId` / `receiptNo` / `receiptDate`
- 客户与币种：`customerId` / `customerName` / `currency`（原币，绝不合并 / 换算）
- 金额：`amount`（收款单自身已落库金额，按原币原样列出；未知照实保留 `null`，绝不回落为 0）
- 付款与状态：`paymentMethod` / `status` / `evidenceStatus`（`active` / `pending` / `historical`）/ `evidenceText`
- 链接证据：`receiptLinkageStatus`（恒为 `unlinked`）/ `receiptLinkageText` / `referenceField`（=`FinanceReceipt.CustomerId`）
- 行级说明：`note`

预览响应中收款证据以 `ReceiptColumns` + `ReceiptRows` 单独投影，与订单 `Columns` / `Rows` 分开；
`UnlinkedReceiptTruncated` 保留源查询的「不完整」截断标记（命中上限时显式标注，绝不静默截断）。
收款证据绝不并入订单行、绝不与订单金额相加、绝不猜测收款单到订单的匹配关系。

## 5. 筛选校验（全部在读取源数据之前完成，非法取值 fail closed）

| 筛选 | 取值 | 校验 |
|---|---|---|
| `CustomerId` | 正整数 | 非正数显式拒绝（2004），且仅在当前数据范围内生效 |
| `Currency` | 币种枚举名（CNY / USD / EUR / HKD / GBP / JPY） | 非法取值显式拒绝（2004） |
| `OrderDateFrom` / `OrderDateTo` | 含首尾当天 | 开始晚于结束显式拒绝（2004） |
| `ShipmentStatus` | `none` / `shipped` | 非法取值显式拒绝（2004） |
| `ReceiptLinkStatus` | `linked` / `partial` / `unlinked` | 非法取值显式拒绝（2004）；`unknown` 只在命中派生上限时出现，不提供筛选 |
| `ReceiptStatus` | `active`（默认）/ `pending` / `historical` / `all` | 非法取值显式拒绝（2004） |
| `OrderStatus` | `active`（默认，排除已取消）/ `cancelled` / `all` | 非法取值显式拒绝（2004） |
| `Keyword` | 匹配订单号 / 外销合同号 / 客户 PO 号 | 长度 > 50 显式拒绝（2004） |
| `PageSize` | 1 ~ 200 | 超出上限显式拒绝（2004），不做静默截断 |

## 6. 证据边界（四类证据相互独立，绝不合并 / 相加 / 推断）

1. **收款申请链接证据**：只来自「定金 / 货款申请单的 `SalesOrderId`」这一权威引用，且只有
   「已审核 + 同币种」计入 `linkedReceiptAmount`；`uncoveredAmount = 订单金额 − 已关联金额`，
   只作「未覆盖金额」，**不是**应收余额或未收款金额。
2. **收款引用登记证据（ERP-054）**：ERP-053 持久化引用行，独立标注「收款单指向本订单」的有效引用金额 /
   引用行 / 收款单张数，并与已作废 / 无效 / 无法确认证据分桶；与「已关联收款金额」是两类独立证据。
3. **销项发票登记证据（ERP-056）**：ERP-055 持久化发票证据行 + 分摊行，独立标注已分摊金额与
   草稿 / 作废 / 无效 / 无法确认分桶；与上面三类证据相互独立。
4. **客户级未关联收款证据**：收款单（`FinanceReceipt`）只有 `CustomerId`、没有订单级引用，
   链接状态恒为 `unlinked`，金额只按收款单自身原币原样列出，绝不并入订单侧合计，绝不匹配到任何订单。

未知金额与未知数量一律 `null`（不是 0）；币种保持原币，不同币种分别成行、绝不合并或换算。

## 7. 只读保证

- 全程只读：无 Add / Update / Remove / SaveChanges，不执行任意 SQL。
- 不新增任何表 / 列 / 权限模型，不改写销售订单、出库单、收款单、收款申请、客户信用、库存、财务与税务记录。
- 不执行迁移 / 生产 SQL / 真实数据库操作 / 部署，不改写订单状态与已登记进度。

## 8. 相关文件

- `src/ERP.Application/DTOs/DynamicReceiptReconciliationReportDtos.cs`
- `src/ERP.Application/Services/DynamicReceiptReconciliationReportRules.cs`
- `src/ERP.Api/Controllers/DynamicReceiptReconciliationReportController.cs`
- `src/ERP.Api/Controllers/SalesOrderReceiptReconciliation.cs`（ERP-046 源查询，新增 `SalespersonDataScope? scope` 参数）
- `src/ERP.UnitTests/DynamicReceiptReconciliationReportTests.cs`
- `src/ERP.UnitTests/DynamicReceiptReconciliationUnlinkedTests.cs`
- `src/ERP.UnitTests/SalesOrderReceiptReconciliationTests.cs`
