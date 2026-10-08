# 销售出库单实时授权与数据范围护栏（ERP-370）

## 1. 目标

销售出库单（stock-out）的全部既有路由（列表 / 详情 / 库存流水 / 创建 / 修改 / 提交 / 审核 / 取消 / 删除）
都必须对**当前登录身份**做**实时**授权与数据范围复核，并把「库存记账」与「改单 / 状态流转」串行化在同一把来源行锁上：
- 缺失 / 非法 / 已删除身份、禁用账号、无既有「销售出库」授权、受限未映射业务员一律 fail closed；
- 列表在计数 / 分页之前把权威客户范围下推到数据库，详情 / 流水与列表同一口径，范围外单据一律按「不存在」拒绝（不泄露 Id / 单号）；
- 创建 / 修改同时校验「已存单据客户」与「请求客户」，被拒绝方绝不消耗单据号、绝不改写单据 / 明细 / 库存 / 流水；
- 修改 / 提交 / 删除 / 审核与取消共用**同一把**本出库单行锁（`UPDLOCK, HOLDLOCK`）与可串行化事务，已审核库存 / 流水不会被并发改单穿透；
- 保留 ERP-343（来源订单累计授权数量）、ERP-359（已审核销售退货引用）、ERP-367（已审核装柜清单显式引用）既有护栏与全部审计证据。

## 2. 口径

| 维度 | 判定 | 结果 |
|---|---|---|
| 身份 | `ClaimTypes.NameIdentifier` 缺失 / 非正整数 | `2000` 未认证（拒绝） |
| 账号 | 账号不存在或已 `IsDeleted` | `2000` 未认证（拒绝） |
| 账号状态 | `Status != Enabled` | `2002` 权限不足（拒绝） |
| 模块授权 | 已配置「角色 → 菜单」授权但不含既有 `stock-out`（销售出库） | `2002` 权限不足（拒绝） |
| 模块授权 | 已分配角色却没有任何「角色 → 菜单」授权（含撤销最后一个菜单） | `2002` 权限不足（拒绝） |
| 数据范围 | 受限账号未映射业务员（无员工编码映射） | `2002` 权限不足（拒绝） |
| 数据范围 | 权威 `StockOut.CustomerId` 不在当前账号可见客户内 | `1002` 按「不存在」拒绝（不泄露归属） |
| 列表范围 | 计数 / 分页之前把客户范围下推到 SQL | 只统计 / 只返回范围内单据，绝不「先查全量再内存过滤」 |
| 创建 / 修改 | 已存与请求两侧客户范围、来源链接（ERP-343）、明细折算全部通过后才落库 | 任一步失败整体回滚，原单据不变，单号不消耗 |
| 审核 / 取消 / 修改 / 提交 / 删除 | 本出库单行锁（`UPDLOCK, HOLDLOCK`）+ 可串行化事务，锁内复核实时授权 | 并发下只出现一种一致结果，库存不重复过账 |
| 主数据 / 成本 | 复用既有 `IInventoryService`（ERP-009 / ERP-025）与移动加权平均口径 | 数量 / 金额 / 流水均不变 |

**菜单授权口径（不新增任何授权）**：账号若已配置「角色 → 菜单」授权，则必须包含既有 `stock-out` 菜单，否则拒绝；
未配置任何菜单授权时，只有既有特权账号（生产端系统内置 / 超级管理员角色已被授予全部菜单）与未分配任何角色、
仅按员工编码映射的历史业务员账号沿用既有 ERP-097 权威数据范围；已分配角色却没有任何菜单授权（例如刚被撤销最后一个菜单）
同样 fail closed —— 绝不新增菜单或用户授权，绝不把空身份 / 空范围当作管理员，也绝不降级为全局可见。

## 3. 实现

- `src/ERP.Application/Services/StockOutAuthorizationRules.cs`（新增）：纯判定 + 有界只读查询 + 行锁 SQL 常量。
  - `EnsureMenuAuthorizedAsync`：身份 / 账号状态 / 既有 `stock-out` 菜单 / 未映射业务员 fail closed，返回权威数据范围；
  - `EnsureCustomerAuthorizedAsync`：单据级客户范围（范围外按不存在拒绝）；
  - `ApplyScopeAsync`：列表客户范围下推（计数 / 分页之前）；
  - `LockStockOutRowSql`：与 `ReturnSourceCancellationRules.LockStockOutRowSql` **同一**锁语句（同一把行锁）。
- `src/ERP.Api/Controllers/StockOutController.cs`：
  - `GetPaged` → `ApplyScopeAsync`（身份 / 菜单 / 客户范围先于 `CountAsync`）；
  - `GetById` / `GetMovements` → `EnsureCustomerAuthorizedAsync(entity.CustomerId)`；
  - `Create` → 授权先于 `IDocumentNumberService.GenerateAsync`；
  - `Update` → 行锁 + 可串行化事务内先复核「已存 + 请求」客户，校验请求内容（明细折算 + ERP-343 来源链接）通过后才改写已存单据；
  - `Submit` / `Delete` → 行锁 + 可串行化事务内复核授权后走基类状态流转 / 软删除；
  - `Approve` → 本出库单行锁 → 锁内复核授权 → 来源订单行锁（ERP-343）→ 幂等护栏 → 库存扣减（恰好一次）；
  - `Cancel` → 来源出库单行锁 → 锁内复核授权 → ERP-359 退货引用 + ERP-367 装柜引用护栏 → 冲销 / 状态变更（锁序不变）。
- 未新增表 / 列 / 菜单 / 权限；未改动 `SchemaUpgrader` / `SeedData`。

## 4. 测试

- `src/ERP.UnitTests/StockOutAuthorizationTests.cs`（新增，内存库 + 真实 HTTP 身份）：
  覆盖缺失身份 / 禁用账号 / 撤销菜单 / 受限未映射账号在九条路由上 fail closed 且零副作用；
  受限业务员自有客户放行、他人客户在列表 / 详情 / 流水 / 创建 / 修改两侧拒绝；
  修改失败（来源链接无效）后单据 / 明细 / 库存 / 流水保持原样；
  审核恰好一次过账、重复审核被拒；审核 / 取消 / 修改 / 提交 / 删除的行锁与事务形状断言（含锁序）。
- `src/ERP.UnitTests/StockInOutMovementLedgerTests.cs` 与 `src/ERP.UnitTests/OrderTraceabilityTests.cs`：
  既有出库履约（ERP-343）与库存流水台账断言原样保留并通过（授权接入后行为不变）。
- 既有 `StockOutControllerTests` / `StockOutOrderFulfillmentTests` / `StockOutWriteScopeRegressionTests` /
  `ReturnSourceCancellationTests` / `StockUnitConversionTests` / `LoadingStockOutCancellationTests`
  **不改动**并保持通过（特权测试身份与既有 `stock-out` 菜单授权的合法账号照常放行）。
- `src/ERP.IntegrationTests/StockOutAuthorizationSqlServerTests.cs`（新增，真实 SQL Server 专用 localdb）：
  专用目标护栏、无身份 / 禁用 / 无菜单拒绝且库存与流水不变、种子管理员（既有全部菜单授权）合法放行、受限操作员范围外拒绝，
  以及**两条独立连接**的竞争用例：并发「审核 vs 改单」与并发「提交 vs 删除」，经同一把出库单行锁 + 可串行化事务
  串行化后只出现一种一致结果，库存 / 流水不重复过账。

## 5. 安全（真实 SQL 夹具口径）

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST`、`Integrated Security`；
  错误目标在访问数据库**之前** fail closed（`AssertDedicatedTarget` / `Guard`）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；**绝不 drop / reset / 复用任何数据库**。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings` / `.env` /
  生产凭据，也绝不执行生产库；**构建通过不等于阶段验收通过**。
- 全部使用既有种子身份与既有菜单授权（`SeedData` 为超级管理员授予全部菜单），不新增任何用户授权；
  `.ai/logs/` 下原始失败日志原样保留。

## 6. 边界

本护栏只保护销售出库单生命周期与库存写入：不改变库存成本口径（移动加权平均），不改写销售订单 / 客户 / 仓库 / 商品主数据，
不删除历史单据与库存流水审计证据，不做报表、界面或生产运维操作；数量护栏仍为 ERP-343，取消护栏仍为 ERP-359 / ERP-367。

## 7. 未执行 / 限制

- 本机验证档为 `safe`：Release 编译（0 警告 / 0 错误）+ `ERP.UnitTests` 全量通过（5566/5566，含本次新增 15 例）。
- `src/ERP.IntegrationTests/StockOutAuthorizationSqlServerTests.cs` 属真实 SQL 集成档，**本次未在本机执行**
  （需专用 localdb 环境与批准）；**构建通过不等于阶段验收通过**。
- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置。
- 未新增 / 未修改任何数据库 schema、表 / 列 / 菜单 / 权限；原始失败日志与历史审计证据原样保留。
