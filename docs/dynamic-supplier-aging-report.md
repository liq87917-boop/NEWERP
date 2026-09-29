# 动态供应商对账与账龄报表（ERP-140）

只读、有界、已授权的「动态供应商对账与账龄报表」预览：采购用户从**有限的发票证据字段白名单**中按序选择字段，并复用
ERP-068 供应商对账与账龄工作台的既有筛选与稳定分页，预览当前账号可见的供应商发票证据页。

## 接口

| 方法 | 路由 | 说明 |
| --- | --- | --- |
| `GET` | `/api/supplier-reconciliation-aging/report` | 返回发票证据字段白名单目录（需登录 + 采购订单菜单授权） |
| `POST` | `/api/supplier-reconciliation-aging/report` | 按选定字段与有界筛选预览当前页，稳定分页（单页上限 200） |

请求体（`DynamicSupplierAgingReportRequest`）：`fields`（选定字段键，仅限白名单）、`supplierId`、`currency`、
`invoiceStatus`（recorded 默认 / draft / voided / all）、`allocationState`（none / historical_only / partial / full）、
`keyword`、`invoiceDateFrom` / `invoiceDateTo`、`dueDateFrom` / `dueDateTo`、`asOfDate`、`page`、`pageSize`。

## 授权（fail closed）

预览与目录都要求当前登录账号具备既有「采购订单」菜单授权（`purchase-order`，与 ERP-061 ~ ERP-068 工作流同源）。
无身份 → `Unauthorized`；无该菜单授权 → `Forbidden`。每次请求都重新查询「角色 → 菜单」授权，不依赖缓存。

## 字段白名单（有限、有序）

字段全部来自 ERP-068 发票证据行（`SupplierReconciliationAgingInvoice`），由
`DynamicSupplierAgingReportRules` 统一供给。未知字段一律拒绝（`InvalidParameter`），留空 = 返回全部白名单字段；
选定列严格按请求顺序返回。

覆盖的证据类别：

- 发票身份：`invoiceId` / `invoiceType` / `invoiceCode` / `invoiceNumber` / `invoiceIdentityText` / `invoiceDate`；
- 供应商：`supplierId` / `supplierCode` / `supplierName` / `supplierAvailable` / `supplierAvailabilityText`；
- 币种与金额（原币）：`currency` / `amountDecimals` / `netAmount` / `taxAmount` / `grossAmount`；
- 发票状态（草稿 / 已作废历史）：`invoiceStatus` / `invoiceStatusText` / `isActiveEvidence` / `isDraft` / `isVoided`；
- 显式到期日与账龄：`dueDate` / `dueDateKnown` / `dueDateText` / `paymentTerms` / `paymentTermsText` /
  `agingBucket` / `agingBucketText` / `overdueDays` / `agingText`；
- 分配证据：`allocationState` / `allocationStateText` / `activeAllocatedAmount` / `activeAllocationCount` /
  `activePaymentCount` / `remainingAmount` / `remainingState` / `remainingStateText` / `voidedAllocationCount` /
  `voidedAllocationAmount` / `invoiceInactiveAllocationCount` / `invoiceInactiveAllocationAmount` /
  `invalidAllocationCount` / `invalidAllocationAmount` / `unavailableAllocationCount` / `unavailableAllocationAmount` /
  `hasAllocationHistory` / `hasInvalidOrUnavailableEvidence` / `historicalEvidenceText` / `note`。

## 筛选与分页（源读取前校验）

- 供应商 Id：非正数直接拒绝；币种：非法取值直接拒绝（复用发票币种严格口径）；发票状态 / 分配状态：非法取值直接拒绝；
- 开票日期 / 到期日区间：开始晚于结束直接拒绝；到期日区间只命中登记了显式到期日的发票（未知到期日不可筛、绝不推算）；
- as-of 日期：早于 1900 直接拒绝；关键字长度超过 50 直接拒绝；
- 每页条数必须在 `1 ~ 200`，超出直接拒绝（不静默截断）。

以上校验全部在调用 `SupplierReconciliationAging.ForQueryAsync`（即源读取）**之前**完成；随后复用 ERP-068 的同一套
筛选 → 稳定分页 → 批量派生。

## 证据边界（重要）

- 金额一律按原币分别成行：`currency` 为原币，不同币种绝不合并、不做汇率换算；
- 未知到期日（`dueDateKnown=false`、`agingBucket=null`）**不计算账龄、单独成组**，绝不按开票日期或付款条件推算；
- 草稿 / 已作废发票单独标注，金额永不并入有效合计；
- 无效 / 无法确认分配证据保持可见，绝不修复、改派或合并；命中系统有界上限时分配证据按「未知」显示，绝不给部分合计。

本预览**不是**应付账款余额、**不是**法定供应商对账单、**不是**付款授权、**不是**税务申报，也不构成结算确认。

## 只读与审计

- 全程只读：无 Add / Update / Remove / SaveChanges，不执行任意 SQL、不写库；
- 请求由既有 `OperationLogMiddleware` 按 HTTP 方法记录审计。

## 文件地图

- `src/ERP.Application/DTOs/DynamicSupplierAgingReportDtos.cs`：目录 / 请求 / 结果 DTO；
- `src/ERP.Application/Services/DynamicSupplierAgingReportRules.cs`：字段白名单、校验与行投影（纯规则）；
- `src/ERP.Api/Controllers/DynamicSupplierAgingReportController.cs`：授权 + 复用 ERP-068 只读派生 + 选定列投影；
- `src/ERP.UnitTests/DynamicSupplierAgingReportTests.cs`：单元测试（内存库，不连 SQL Server、不启动 API）。
