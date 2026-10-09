# 出口退税台账实时授权与权威客户范围护栏说明（ERP-442）

- 任务：`ERP-442`「Authorize export tax refund ledger writes with live finance permission and amount guards」
- 权威实现：
  - `src/ERP.Application/Services/TaxRefundLedgerRules.cs`（纯规则 + 实时授权 + 权威客户范围 + 金额 / 税率 / 期间 / 日期校验）
  - `src/ERP.Api/Controllers/TaxRefundController.cs`（完整 CRUD 覆盖：分页 / 全部 / 详情 / 新增 / 修改 / 删除 / 批量删除）
  - `src/ERP.UnitTests/TaxRefundLedgerTests.cs`（内存库单元测试 + 接线契约）
  - `src/ERP.IntegrationTests/TaxRefundLedgerAuthorizationSqlServerTests.cs`（专用 localdb 真实控制器集成测试）

## 1. 背景

`TaxRefundController` 此前只声明类级 `[Authorize]` 并继承 `BaseCrudController<BaseTaxRefund>`，
其继承的通用路由（`GetPaged` / `GetAll` / `GetById` / `Create` / `Update` / `Delete` / `batch-delete`）
直接委托 `IGenericService` 且**没有任何**实时身份、菜单授权或客户数据范围复核：任何已登录账号只要猜到一条
台账行 Id，就能读取、创建、改写或软删除**任意**出口退税台账行，且 `BaseTaxRefund` 的
`ExportAmount` / `RefundRate` / `RefundableAmount` / `RefundedAmount` / `Currency` / `RefundPeriod` 与
申报 / 到账日期没有任何服务端边界。

本改动把全部路由纳入与其它模块一致的实时授权与写入护栏，复用**既有**授权与数据范围，**不新增**任何授权。

## 2. 授权口径

| 环节 | 口径 |
| --- | --- |
| 实时身份 | 每个路由在读取任何计数 / 写入任何字段之前重新解析身份：缺失 / 非法 / 账号不存在 / 已删除按未认证拒绝（`2000`）；账号已禁用按权限不足拒绝（`2002`） |
| 既有菜单 | 复用已部署的「出口退税台账」（`tax-refund`，`SchemaUpgrader` 第 12.5 段幂等种子）功能菜单：非特权账号必须显式具备，撤销后下一次请求立即收敛（无缓存） |
| 业务员映射 | 复用 ERP-097 `SalespersonDataScopeService` 唯一权威口径：受限账号按其登录名精确匹配员工编码（`IsSalesman`）；未映射业务员的受限账号 **fail closed**，绝不降级为全局 / 管理员可见 |
| 特权账号 | 超级管理员 / 系统内置角色 / 显式配置的特权角色保留既有全部访问（含历史无主行） |
| 身份来源 | 只来自已认证请求主体（`ClaimTypes.NameIdentifier`）；请求体**不包含也不接受**任何账号 / 角色 / 范围字段 |

控制器在每条路由动作的第一行调用 `TaxRefundLedgerRules.EnsureMenuAuthorizedAsync(_db, CurrentUserId())`，
在**任何读取 / 写入之前**完成上述判定；**无** `[AllowAnonymous]`、**无** `[Authorize(Roles=…)]`、
**无**匿名 / 管理员回退，绝不把空身份当作管理员。

## 3. 权威客户归属

- 台账归属只按持久化 `BaseTaxRefund.CustomerId` 判定；**绝不**按 `CustomerName` / `RefundNo` /
  `DeclareNo` / `InvoiceNo` / `SalesOrderNo` 等自由文本推断。
- 受限账号：`CustomerId` 为空 / 非正（历史无主行）或不在范围内 → 拒绝（fail closed，不泄露存在性）。
- 特权账号：保留历史访问（含 `CustomerId` 为空的历史行）。
- 列表 / 全部：范围在 **`Count` 与分页之前**下推到 SQL（`ScopeFilter` / `ApplyScope`），
  绝不「先查全量再内存过滤」，计数与当前页绝不泄露范围外行或无主行。
- 详情：读取后复核已存行的权威归属；越界 / 无主一律拒绝。
- 新增 / 修改：先复核拟议 `CustomerId` 的实时范围（受限账号无主 / 越界 fail closed），
  已登记客户必须是真实存在且启用的客户。
- 删除：先复核已存行归属；批量删除逐行复核，**任一行越界 / 无主即整批拒绝**、不做部分删除。

## 4. 路由覆盖

| 路由 | 授权 + 范围 |
| --- | --- |
| `GET /api/base/tax-refunds` | 实时身份 + `tax-refund` 菜单 + 列表范围下推（计数 / 分页之前） |
| `GET /api/base/tax-refunds/all` | 同上 + 范围下推（下拉用） |
| `GET /api/base/tax-refunds/{id}` | 同上 + 已存储 `CustomerId` 范围复核 |
| `POST /api/base/tax-refunds` | 同上 + 拟议客户范围 / 真实性 + 字段校验 |
| `PUT /api/base/tax-refunds/{id}` | 同上 + 已存储范围 + 拟议范围 / 真实性与字段校验 |
| `DELETE /api/base/tax-refunds/{id}` | 同上 + 已存储范围复核后软删除 |
| `POST /api/base/tax-refunds/batch-delete` | 同上 + 逐行范围复核（任一行越界 / 无主即整批拒绝、无部分删除） |

## 5. 字段护栏（新增 / 修改）

以既有受控校验错误 `1001 InvalidParameter` 拒绝，**绝不静默截断、取整或回填**任何字段：

| 字段 | 规则 |
| --- | --- |
| `ExportAmount` / `RefundableAmount` / `RefundedAmount` | 必须非负 |
| `RefundRate` | 必须落在 `0..100`（百分比） |
| `RefundedAmount` vs `RefundableAmount` | 已退税不得超过可退税 |
| `Currency` | 非空且长度 ≤ 20（既有 `MaxLength(20)`） |
| `RefundPeriod` | 非空且长度 ≤ 20（既有 `MaxLength(20)`） |
| `DeclareDate` / `RefundDate` | 均为合理日期（不早于 1900-01-01）；两者同时提供时到账日期不得早于申报日期 |

## 6. 边界

- 不新增菜单 / 角色 / 用户授权，不新增表 / 列 / 索引，不改变实体与既有存储精度，不把空身份当作管理员。
- 不记账、不生成凭证，不写资金 / 库存 / 会计数据。
- **不改变**既有只读退税汇总报表（`ReportController.TaxRefundSummary` / `IReportService.GetTaxRefundSummaryAsync`）
  的聚合语义。
- 被拒绝的读取 / 新增 / 修改 / 删除 / 批量删除**零写入**：不落任何台账行，也不改写原行、软删除标记与审计时间戳。

## 7. 验证

- 安全档位：Release 构建 + 完整 `ERP.UnitTests`（含 `TaxRefundLedgerTests`：字段校验、身份 / 菜单 / 范围、
  控制器 CRUD、接线契约）。
- 真实 SQL 集成测试：`TaxRefundLedgerAuthorizationSqlServerTests` 只在专用 localdb 实例
  `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且 `Integrated Security` 的全新 GUID 库上执行，
  发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何库，也绝不读取 `appsettings` / `.env` / 生产凭据。
- **构建完成不等于阶段验收**：只有上述集成测试在受控 localdb 上真实执行通过才算验收证据。
