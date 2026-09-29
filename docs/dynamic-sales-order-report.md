# 动态销售订单报表预览（ERP-112）

## 1. 目标

授权业务员在自己的 ERP 数据范围内，选择销售订单字段并应用有界筛选，只读预览订单；无身份 / 无角色 /
无销售订单菜单授权 / 无效字段或筛选时一律拒绝（fail closed），且不泄露范围外数据。

## 2. 接口

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/sales-orders/report` | 返回有限白名单字段目录（需登录 + 销售订单菜单授权） |
| POST | `/api/sales-orders/report` | 按选定字段与有界筛选预览当前账号数据范围内的订单 |

请求体（POST，`DynamicSalesOrderReportRequest`）：

- `fields`：选定字段键（仅限白名单；留空 = 返回全部白名单字段，保持目录顺序）；
- `startDate` / `endDate`：订单日期区间（含当日）；
- `customerId`：客户 Id 筛选；
- `status`：状态筛选（`Pending` / `Submitted` / `Approved` / `Rejected` / `Completed` / `Cancelled`）；
- `currency`：币种筛选（`CNY` / `USD` / `EUR` / `HKD` / `GBP` / `JPY`）；
- `page`（默认 1）/ `pageSize`（默认 20，**上限 200**）。

响应（`DynamicSalesOrderReportPageDto`）：`columns`（按请求顺序的选定列）、`rows`（每行仅含选定字段值）、
`total` / `page` / `pageSize` / `totalPages`，以及 `readOnlyText` / `boundaryText` / `disclaimerText` 口径文案。

## 3. 数据边界与安全

1. **身份**：缺少登录用户 Id → 未认证（`2000`），不返回任何数据。
2. **菜单授权**：复用既有「角色 → 菜单」模块授权，要求 `sales-order`（销售订单）菜单；
   无角色 / 无该菜单授权 → 权限不足（`2002`）。授权每次请求都重新查询，回收后立即收敛。
3. **数据范围**：复用 `SalespersonDataScopeService`（ERP-097 唯一权威口径）：
   特权账号（超级管理员 / 系统内置角色 / 显式特权角色）不过滤；受限制业务员只看到
   `BaseCustomer.EmpId == 本人` 的客户及其销售订单，未映射业务员时为空集合（fail closed）。
4. **字段白名单**：字段仅限 `SalesOrder` 持久化字段的有限白名单（31 个），未知字段显式拒绝；
   不拼接、不接受任意字段名或 SQL。
5. **筛选白名单**：仅订单日期 / 客户 / 状态 / 币种；状态与币种只接受枚举取值，未知取值拒绝；
   开始日期晚于结束日期拒绝；`pageSize` 超出 `1~200` 拒绝（查询前校验）。
6. **只读**：全程 `AsNoTracking`，无 `Add` / `Update` / `Remove` / `SaveChanges`，
   不执行任意 SQL（无 `FromSql*` / 存储过程调用），不做任何写入。

## 4. 请求审计

- 预览为 `POST`，由既有 `OperationLogMiddleware` 按 HTTP 方法记录到 `SysOperationLog`
  （模块 `订单管理`），本查询自身不写任何操作日志、不新增审计模型。
- 目录为 `GET`，按既有中间件口径不落操作日志（沿用现有审计约定）。
- 查询代码本身零写入，审计由请求管道统一完成。

## 5. 只读保证

- 不新增表 / 列 / 索引，不执行迁移、生产 SQL 或真实数据库操作；
- 不改写销售订单、客户、库存、出货、发票、对账、财务或结算记录；
- 不读取或打印任何连接串、JWT / OSS 密钥或 `.env` 值。

## 6. 测试覆盖（`ERP.UnitTests/DynamicSalesOrderReportTests.cs`）

- 字段白名单目录（有限、白名单键、所需菜单与 200 上限）；
- 选定列与顺序、未指定字段默认全字段；
- 日期 / 客户 / 状态 / 币种筛选；
- 稳定分页（按 Id 升序）与单页 200 上限；
- 无身份（未认证）、无销售订单菜单授权（权限不足）、越界业务员（只返回自己客户、越界筛选返回空）；
- 无效字段 / 无效日期区间 / 无效状态与币种 / 页大小超限（查询前拒绝）；
- 只读不写库（`SaveChangesAsync` 调用次数恒为 0）。
