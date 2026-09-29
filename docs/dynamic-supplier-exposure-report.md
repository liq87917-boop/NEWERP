# 动态供应商采购敞口预览（ERP-148）

只读、有界、已授权的「动态供应商采购敞口预览」：采购用户从**有限的采购订单敞口证据字段白名单**中按序选择字段，并复用
ERP-031 供应商采购敞口报表的既有筛选与稳定分页，预览当前账号可见的采购订单敞口页。

## 接口

| 方法 | 路由 | 说明 |
| --- | --- | --- |
| `GET` | `/api/supplier-purchase-exposure/report` | 返回采购订单敞口证据字段白名单目录（需登录 + 采购订单菜单授权） |
| `POST` | `/api/supplier-purchase-exposure/report` | 按选定字段与有界筛选预览当前页，稳定分页（单页上限 200） |

请求体（`DynamicSupplierExposureReportRequest`）：`fields`（选定字段键，仅限白名单）、`supplierId`、`currency`、
`orderDateFrom` / `orderDateTo`、`linkStatus`（linked / ambiguous / unavailable）、`keyword`、`page`、`pageSize`。

## 授权（fail closed）

预览与目录都要求当前登录账号具备既有「采购订单」菜单授权（`purchase-order`，与 ERP-031 及采购订单工作流同源）。
无身份 → `Unauthorized`；无该菜单授权 → `Forbidden`。每次请求都重新查询「角色 → 菜单」授权，不依赖缓存。

## 字段白名单（有限、有序）

字段全部来自 ERP-031 采购订单敞口证据行（`SupplierPurchaseExposureOrder`），由
`DynamicSupplierExposureReportRules` 统一供给。未知字段一律拒绝（`InvalidParameter`），留空 = 返回全部白名单字段；
选定列严格按请求顺序返回。

覆盖的证据类别：

- 订单身份：`orderId` / `orderNo` / `orderDate` / `status`；
- 供应商：`supplierId` / `supplierName`；
- 币种与订单金额（原币）：`currency` / `orderedAmount`；
- 结算引用链：`owningSalesOrderNo` / `recordedSettlementProgress`；
- 链接状态与结算金额（未知用 null，绝不回落为 0）：`linkStatus` / `linkReason` / `settledAmount` /
  `outstandingAmount` / `submittedAmount` / `overSettled`；
- 收货状态与数量（未知用 null，绝不回落为 0）：`receiptStatus` / `orderedQuantity` / `receivedQuantity` /
  `outstandingQuantity` / `pendingQuantity`；
- 行级说明：`note`。

## 筛选与分页（源读取前校验）

- 供应商 Id：非正数直接拒绝；币种：非法取值直接拒绝（复用 ERP-031 的严格币种口径）；链接状态：非法取值直接拒绝；
- 订单日期区间：开始晚于结束直接拒绝；关键字长度超过 50 直接拒绝；
- 每页条数必须在 `1 ~ 200`，超出直接拒绝（不静默截断）。

以上校验全部在调用 `SupplierPurchaseExposure.ForQueryAsync`（即源读取）**之前**完成；随后复用 ERP-031 的同一套
筛选 → 稳定分页 → 批量派生。

## 证据边界（重要）

- 金额一律按原币分别成行：`currency` 为原币，不同币种绝不合并、不做汇率换算；
- 链接不唯一（`ambiguous`）或缺失（`unavailable`）的订单，`settledAmount` / `outstandingAmount` / `submittedAmount`
  均为未知（null），绝不推算、绝不回落为 0，且只作「未链接敞口」；
- 收货数量命中有界派生上限时为未知（null），绝不推算或修复；
- 未知金额与未知数量在预览行中照实保留为 `null`，界面显式显示「未知」。

本预览**不是**应付账款台账、**不是**账龄表、**不是**付款授权、**不是**税务申报，也不构成结算确认。
「未链接敞口」只是尚未按既有引用归属到订单的订单金额，不得当作应付余额或据以付款。

## 只读与审计

- 全程只读：无 Add / Update / Remove / SaveChanges，不执行任意 SQL、不写库；
- 请求由既有 `OperationLogMiddleware` 按 HTTP 方法记录审计。

## 文件地图

- `src/ERP.Application/DTOs/DynamicSupplierExposureReportDtos.cs`：目录 / 请求 / 结果 DTO；
- `src/ERP.Application/Services/DynamicSupplierExposureReportRules.cs`：字段白名单、校验与行投影（纯规则）；
- `src/ERP.Api/Controllers/DynamicSupplierExposureReportController.cs`：授权 + 复用 ERP-031 只读派生 + 选定列投影；
- `src/ERP.UnitTests/DynamicSupplierExposureReportTests.cs`：单元测试（内存库，不连 SQL Server、不启动 API）。
