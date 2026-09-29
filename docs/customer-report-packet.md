# 客户报告包预览（ERP-122）

## 1. 目标

授权业务员从客户销项发票登记册打开报告包，只读预览同一客户的 ①销售订单、②发票 + 显式收款分摊证据，
两者作为两个**独立有界分区**返回，各自独立计数、金额按原币呈现、剩余证据保留 known / unknown / over_allocated 标签。
两个分区分别重检各自的既有菜单授权与 `SalespersonDataScopeService`（ERP-097）数据范围，任一失败即拒绝整个响应
（fail closed，绝不返回部分结果）；绝不推断发票到销售订单的链接、不结算、不计算账户余额或催收状态。

## 2. 接口

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/api/customer-report-packet` | 按正整数客户 Id + 有界日期 / 分页筛选预览两个独立分区 |

请求体（`CustomerReportPacketRequest`）：

- `customerId`：客户 Id（**必填、正整数**，非正整数拒绝）；
- `startDate` / `endDate`：日期区间（含当日；同时作用于销售订单订单日期与发票开票日期）；
- `page`（默认 1，小于 1 归一到第 1 页）/ `pageSize`（默认 20，**上限 100**，超出拒绝）。

响应（`CustomerReportPacketDto`）：

- `customerId`：客户 Id；
- `salesOrders`：销售订单分区（`DynamicSalesOrderReportPageDto`，独立 `columns` / `rows` / `total` / `page` / `pageSize` / `totalPages`）；
- `receivableEvidence`：发票 / 收款分摊证据分区（`DynamicReceivableReportPageDto`，同上）；
- `readOnlyText` / `boundaryText` / `disclaimerText`：只读 / 边界 / 免责口径文案。

## 3. 分区口径

- **销售订单分区**：复用 ERP-112 `IDynamicSalesOrderReportQuery`，字段为销售订单持久化字段白名单，
  携带 `currency` / `totalAmount` 等原币字段。
- **发票 / 收款分摊证据分区**：复用 ERP-117 `IDynamicReceivableReportQuery`，字段来自 ERP-074 对账证据行，
  携带 `currency` / `grossAmount` / `effectiveAmount` / `remainingAmount` / `remainingState` / `remainingStateText`
  与 `allocationState` / `allocationStateText` 等字段。
- **两个分区绝不合并、绝不推导**：响应不含任何「发票 → 订单」的链接、结算、余额或催收字段；
  剩余证据状态仅保留 `known` / `unknown` / `over_allocated`，`unknown` / `over_allocated` 金额为 null，绝不轧成假余额。

## 4. 授权与数据边界

1. **身份**：缺少登录用户 Id → 未认证（`2000`）。
2. **双菜单授权**：端点级要求同时具备「销售订单」菜单（`sales-order`）与「客户资料」菜单（`customer`），
   任一缺失 → 权限不足（`2002`），拒绝整个响应；授权每次请求都重新查询。
3. **分区独立重检**：两个分区各自的查询接口还会独立重检本分区的菜单授权与业务员数据范围，
   即使端点级双授权通过，任一分区内部失败同样拒绝整个响应。
4. **数据范围**：复用 `SalespersonDataScopeService`（ERP-097 唯一权威口径）；范围过滤在 `Count` / `Skip` / `Take`
   之前应用，越界客户绝不进入计数与结果页，两分区均为空（fail closed）。
5. **有界校验**：客户 Id 非正、开始日期晚于结束日期、`pageSize` 超出 `1~100` 均在读取前拒绝。
6. **只读**：全程 `AsNoTracking`，无 `Add` / `Update` / `Remove` / `SaveChanges`，不执行任意 SQL，不做任何写入。

## 5. 请求审计

预览为 `POST`，由既有 `OperationLogMiddleware` 按 HTTP 方法记录到 `SysOperationLog`，查询自身不写任何操作日志。

## 6. 只读保证

- 不新增表 / 列 / 索引，不执行迁移、生产 SQL 或真实数据库操作；
- 不改写客户、发票证据、收款分摊、收款单、销售订单、库存、财务或结算记录；
- 不读取或打印任何连接串、JWT / OSS 密钥或 `.env` 值。

## 7. 关键文件

- `src/ERP.Application/DTOs/CustomerReportPacketDtos.cs`：请求 / 结果 DTO；
- `src/ERP.Application/Services/CustomerReportPacketRules.cs`：双菜单授权、有界校验与纯映射（纯规则）；
- `src/ERP.Api/Controllers/CustomerReportPacketController.cs`：HTTP 控制器；
- `src/ERP.Api/wwwroot/js/customer-report-packet.js`：前端预览（入口 `openCustomerReportPacket`）；
- `src/ERP.Api/wwwroot/js/customer-sales-invoices.js`：登记册工具栏 / 详情工具栏入口；
- `src/ERP.UnitTests/CustomerReportPacketTests.cs`：预览单元测试；
- `tests/automation/customer_report_packet_ui.test.js`：前端 UI 逻辑单测。

## 8. 测试覆盖

- **双权限**：销售订单 / 客户资料菜单任一缺失均拒绝整个响应；无身份未认证拒绝。
- **有界校验**：正整数客户 Id、日期区间倒置拒绝、`pageSize` 超限拒绝、页码小于 1 归一。
- **数据范围**：受限业务员越界客户时两分区都空、不泄露。
- **独立分区**：两分区各自独立计数、原币字段、剩余证据状态字段；分区字段互不串用（不推断跨单链接）。
- **只读**：`SaveChangesAsync` 调用次数恒为 0。

## 9. 验证

```
dotnet build NEWERP.sln -c Release --no-restore --no-incremental
dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build --filter FullyQualifiedName~CustomerReportPacketTests
node tests/automation/customer_report_packet_ui.test.js
```
