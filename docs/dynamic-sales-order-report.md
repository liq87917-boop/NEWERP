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
- `groupBy`：分组键（仅 `none` / `customer` / `month`，大小写不敏感；空 / 缺省 = `none`，未知取值 fail closed 拒绝）；
- `page`（默认 1）/ `pageSize`（默认 20，**上限 200**）。

响应（`DynamicSalesOrderReportPageDto`）：`columns`（按请求顺序的选定列）、`rows`（每行仅含选定字段值）、
`total` / `page` / `pageSize` / `totalPages`，以及 `readOnlyText` / `boundaryText` / `disclaimerText` 口径文案；
ERP-114 新增 `groupBy`（规范化后的分组键）与 `groups`（仅当分组时的「页面小计」）。

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

## 7. 前端设计器（ERP-113）

- 入口：销售订单页工具栏「📊 动态报表」（`modules-doc.js` 的 `extraActions`，`openDynamicSalesOrderReport()`）。
- 脚本：`wwwroot/js/dynamic-sales-order-report.js`（`index.html` 注册，加载于 `app.js` 之前）。

### 7.1 用户步骤

1. 登录后进入「订单管理 → 销售订单」列表页；
2. 点击工具栏「📊 动态报表」，设计器弹窗打开并调用 `GET /api/sales-orders/report` 加载字段白名单目录（无登录 / 无销售订单菜单授权 → 显示「权限不足 / 未登录」错误态，不返回任何字段）；
3. 「① 选择字段」区域按目录渲染 31 个字段复选框（全选 / 清空），无自由填写的字段名；
4. 「② 筛选」区域设置订单日期区间、客户（来自既有 `/api/base/customers`）、状态、币种（均为下拉 / 日期控件，无自由 SQL）；
5. 点击「预览」→ `POST /api/sales-orders/report`，只发送「白名单字段 + 有界筛选 + 有界分页（pageSize 1~200）」；
6. 结果区安全渲染返回的列名与单元格（全部 HTML 转义），并显示只读 / 边界 / 免责口径文案、总数与分页；空结果 / 无效请求 / 授权失败 / 网络失败分别显示可见提示，全程无写入。

### 7.2 安全与只读

- 字段选择器仅由 ERP-112 目录渲染，请求组装时再次按目录白名单过滤（`dsorSelectFields`），未知字段绝不进入请求；
- 筛选仅日期 / 客户 / 状态 / 币种，状态与币种取枚举下拉；无任意 SQL、无自由字段名输入；
- 预览为 `POST`，由既有 `OperationLogMiddleware` 记录审计；前端不写库、不迁移、不执行任意 SQL。

## 8. 分组与小计（ERP-114）

### 8.1 分组键（fail closed）

- 仅接受 `none` / `customer` / `month`（大小写不敏感）；空 / 缺省 = `none`（不分组）。
- 任何其它取值在**读取任何数据之前**即拒绝（`InvalidParameter`），绝不静默回落或猜测。

### 8.2 页面小计语义（关键）

- `groups` 是**当前预览页的小计**（page subtotal），**不是**全量合计：它只对「本次请求返回的、
  已通过 ERP-112 权限 + ERP-097 业务员数据范围校验的同一批有界行」聚合，换页后小计随之变化。
- 每个分组（客户 / 月份）内再**按币种分开**统计 `count` 与 `amount`；金额只对同币种求和，
  **绝不跨币种换算或相加**（沿用 ERP-112 免责口径「金额按订单原币呈现」）。
- 分组与小计为纯函数（`DynamicSalesOrderReportRules.BuildGroupSubtotals`），确定性排序：
  客户按 `customerId` 升序、月份按年月升序、组内币种按 `Currency` 枚举顺序；未知币种仍单独成行并排最后。
- 不分组（`none`）或空页时 `groups` 为空列表。
- 分组时服务端自动补齐计算所需的 `currency` / `totalAmount` 及分组键字段（`customerId` / `orderDate`），
  保证即使请求未选择这些字段也能得到正确、币种安全的小计。

### 8.3 与既有边界的关系

- 仍复用 ERP-112 的「角色 → 菜单」授权（`sales-order`）与 ERP-097 业务员数据范围：小计只来自授权可见行。
- 仍全程只读：无新增表 / 列 / 迁移，无写入，无任意 SQL；请求由既有 `OperationLogMiddleware` 记录审计。
- 不新增权限模型，不执行真实数据库操作，不读取或打印任何密钥 / 连接串 / `.env` 值。

## 9. 验证

- 前端 UI 逻辑单测：`tests/automation/dynamic_sales_order_report_ui.test.js`（`node tests/automation/dynamic_sales_order_report_ui.test.js`）；
- 语法检查：`node --check src/ERP.Api/wwwroot/js/dynamic-sales-order-report.js`；
- 分组与小计单测：`ERP.UnitTests/DynamicSalesOrderGroupingTests.cs`；
- 安全档构建 / 测试：`dotnet build NEWERP.sln -c Release` 与 `dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build`。
