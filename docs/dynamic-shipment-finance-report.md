# 动态销售订单出货 / 财务进度报表预览（ERP-156）

只读、有界、已授权的「动态销售订单出货 / 财务进度报表预览」：销售用户从**有限的 ERP-032 订单证据字段白名单**中按序选择字段，并复用 ERP-032 销售订单出货 / 财务进度报表的既有筛选与稳定分页，预览当前账号可见的订单出货与收款链接证据页。

## 接口

| 方法 | 路由 | 说明 |
| --- | --- | --- |
| `GET` | `/api/sales-orders/dynamic-shipment-finance-report` | 返回 ERP-032 订单证据字段白名单目录（需登录 + 销售订单菜单授权） |
| `POST` | `/api/sales-orders/dynamic-shipment-finance-report` | 按选定字段与有界筛选预览当前页，稳定分页（单页上限 200） |

请求体（`DynamicShipmentFinanceReportRequest`）：`fields`（选定字段键，仅限白名单，留空 = 全部白名单字段）、
`customerId`、`currency`、`orderDateFrom` / `orderDateTo`、`shipmentStatus`（none / shipped）、
`financeLinkStatus`（linked / partial / unlinked）、`page`、`pageSize`。

## 授权（fail closed）

预览与目录都要求当前登录账号具备既有「销售订单」菜单授权（`sales-order`，与 ERP-032 及销售订单工作流同源）。
无身份 → `Unauthorized`；无该菜单授权 → `Forbidden`。每次请求都重新查询「角色 → 菜单」授权，不依赖缓存。

## 业务员数据范围（ERP-097，唯一权威口径）

预览在 ERP-032 源查询**内部**（计数与分页之前）应用 `SalespersonDataScopeService` 客户范围过滤：
特权账号（超级管理员 / 系统内置角色 / 显式特权角色）不过滤；受限制业务员只能看到 `BaseCustomer.EmpId == 本人` 的客户
及其销售订单，未映射到业务员时为空集合（fail closed）。每次请求重新解析，授权 / 员工 / 客户分配变更后立即收敛。

## 字段白名单（有限、有序）

字段全部来自 ERP-032 订单证据行（`SalesOrderShipmentFinanceOrder`），由 `DynamicShipmentFinanceReportRules` 统一供给。
未知字段一律拒绝（`InvalidParameter`），留空 = 返回全部白名单字段；选定列严格按请求顺序返回。

覆盖的证据类别：

- 订单身份：`orderId` / `orderNo` / `orderDate` / `status`；
- 客户：`customerId` / `customerName`；
- 币种与订单金额（原币）：`currency` / `orderAmount` / `recordedDepositAmount`；
- 出货数量与状态（未知用 null，绝不回落为 0）：`orderedQuantity` / `shippedQuantity` / `pendingShipmentQuantity` /
  `outstandingQuantity` / `shipmentStatus` / `hasApprovedShipment` / `shipmentDocumentCount` / `approvedShipmentCount`；
- 收款链接状态与金额（未知用 null，绝不回落为 0）：`financeLinkStatus` / `financeLinkReason` / `linkedAmount` /
  `uncoveredAmount` / `submittedAmount` / `otherCurrencyRecordCount` / `unapprovedRecordCount` /
  `unattributedRecordCount` / `overReceived`；
- 行级说明：`note`。

## 筛选与分页（源读取前校验）

- 客户 Id：非正数直接拒绝；币种：非法取值直接拒绝（复用 ERP-032 的严格币种口径）；
- 出货状态：仅 `none` / `shipped`；收款链接状态：仅 `linked` / `partial` / `unlinked`（`unknown` 只在命中派生上限时出现，无法用既有列条件等价表达，不提供筛选）；
- 订单日期区间：开始晚于结束直接拒绝；
- 每页条数必须在 `1 ~ 200`，超出直接拒绝（不静默截断）。

以上校验全部在调用 `SalesOrderShipmentFinanceReport.ForQueryAsync`（即源读取）**之前**完成；随后复用 ERP-032 的同一套
筛选 → 稳定分页 → 批量派生（分页按客户 + 币种 + 订单日期 + 单据 Id 稳定排序）。

## 证据边界（重要）

- 金额与数量一律按原币分别成行：`currency` 为原币，不同币种绝不合并、不做汇率换算；
- 无权威引用（`financeLinkStatus == unlinked`）或命中派生上限（`unknown`）的订单，`linkedAmount` / `uncoveredAmount` /
  `submittedAmount` 均为未知（null），绝不推算、绝不回落为 0，且只作「未覆盖金额」；
- 出货数量命中有界派生上限时为未知（null），绝不推算或修复；
- 未知金额与未知数量在预览行中照实保留为 `null`，界面显式显示「未知」。

本预览**不是**应收账款台账、**不是**账龄表、**不是**收款授权或结算结果，也**不推断**发票到订单的链接。
「未覆盖金额」只是订单金额与权威计入金额之差，不得当作应收余额或据以催收。

## 只读与审计

- 全程只读：无 Add / Update / Remove / SaveChanges，不执行任意 SQL、不写库；
- 请求由既有 `OperationLogMiddleware` 按 HTTP 方法记录审计（预览与后续下载端点共用）。

## 报表限制（有界）

- 单页最多 `200` 行，超出直接拒绝；`total` 为符合筛选条件且在当前业务员数据范围内的未删除销售订单总数；
- 未知金额 / 未知数量照实保留为 `null`，绝不回落为 0、绝不跨币种合并或换算；
- 不新增表 / 列 / 权限模型，不执行迁移、生产 SQL、真实数据库操作或部署。

## 文件地图

- `src/ERP.Application/DTOs/DynamicShipmentFinanceReportDtos.cs`：目录 / 请求 / 结果 DTO；
- `src/ERP.Application/Services/DynamicShipmentFinanceReportRules.cs`：字段白名单、校验、行投影、目录（纯规则）；
- `src/ERP.Api/Controllers/DynamicShipmentFinanceReportController.cs`：授权 + 业务员数据范围 + 复用 ERP-032 只读派生 + 选定列投影；
- `src/ERP.Api/Controllers/SalesOrderShipmentFinanceReport.cs`：ERP-032 权威派生，新增可选 `SalespersonDataScope` 参数（在源查询内部先于计数与分页过滤客户范围）；
- `src/ERP.UnitTests/DynamicShipmentFinanceReportTests.cs`：预览单元测试（内存库，不连 SQL Server、不启动 API）；
- `src/ERP.UnitTests/SalesOrderShipmentFinanceReportTests.cs`：ERP-032 报表单元测试（含 ERP-156 复用的源查询范围过滤）。
