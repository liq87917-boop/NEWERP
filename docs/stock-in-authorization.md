# 采购入库单实时授权与数据范围护栏（ERP-352）

## 1. 目标

采购入库单的全部既有路由（读取 / 创建 / 修改 / 提交 / 审核 / 取消 / 删除 / 库存流水）都必须对**当前登录身份**做**实时**授权与数据范围复核，缺失 / 禁用 / 未映射身份与缺失「采购入库」菜单授权一律 fail closed；链接采购订单的归属客户不得暴露到当前账号客户范围之外；拒绝方绝不产生任何单据、库存、流水、状态或单据号副作用，也绝不泄露范围外单据标识。

## 2. 口径

- **身份**：无身份（或非法身份）按未认证拒绝（`Unauthorized`）；账号不存在 / 已删除同样按未认证拒绝。
- **账号状态**：非 `Enabled`（禁用）按权限不足拒绝（`Forbidden`）。
- **模块授权**：必须具备既有「采购入库」（菜单编码 `stock-in`，与 `SeedData.Menus` 同源）菜单授权；未映射该菜单按权限不足拒绝。**不新增任何权限模型**，也不把空身份当作管理员。
- **主数据可用性**（创建 / 修改）：供应商与仓库必须真实可用（存在、未删除、已启用），否则拒绝且不落库。
- **来源归属（数据范围）**：
  - 链接采购订单的入库单：来源订单归属客户必须落在当前账号客户数据范围（既有 `SalespersonDataScopeService` 口径）之内，否则拒绝且不泄露范围外客户；
  - 合法的未链接入库单：仅允许**既有已映射入库操作员**访问（未映射到业务员的账号 fail closed）。
- **写入前校验、单号不浪费**：创建时授权与来源校验先于单号生成——被拒绝的调用方绝不消耗单据号（`SysDocumentNumberRules.CurrentSequence` 不前进）。
- **已存与请求两侧归属**：修改（`PUT`）同时校验「已存单据」与「请求内容」的来源归属；既有来源越界或新来源越界都拒绝，且原单据来源保持不变。
- **事务内复核**：审核 / 取消把实时授权与来源归属复核放进与库存写入同一个可串行化事务（来源订单 `UPDLOCK/HOLDLOCK` 串行化同单并发），读取之后撤销授权立即收敛。
- **读取作用域一致**：分页列表只返回「链接订单归属客户在范围内」的入库单与未链接入库单；详情 / 流水与列表同一口径，范围外单据一律拒绝（不泄露 Id / 单号）。
- **写入复用既有服务**：库存增减与冲销全部复用既有 `IInventoryService`（ERP-009 / ERP-025），成本口径仍为 ERP-033（订单单价）/ ERP-342 / ERP-344 既有规则，本次不改变任何数量或成本算法。
- **审计保留**：单据状态、`UpdatedAt`、库存流水与成本来源备注均按既有写入路径保留，不删除历史证据。

## 3. 实现

- `src/ERP.Application/Services/StockInAuthorizationRules.cs`：纯判定 + 有界只读查询（身份 / 状态 / 菜单、供应商与仓库可用性、按 Id 解析来源订单归属客户、列表作用域），不落库、不改单据 / 库存 / 主数据、不新增权限。
- `src/ERP.Api/Controllers/StockInController.cs`：
  - `GetPaged` → `ApplyScopeAsync`（含身份 / 菜单 fail closed）；
  - `GetById` / `GetMovements` → `EnsureAuthorizedAsync(entity)`；
  - `Create` → 授权 + 主数据 + 来源范围校验**先于** `IDocumentNumberService.GenerateAsync`；
  - `Update` / `Submit` / `Delete` → 先校验已存单据归属，`Update` 再校验请求侧归属；
  - `Approve` / `Cancel` → 事务内再复核授权与来源，失败整体回滚（库存 / 流水 / 状态不变）。

## 4. 测试

- `src/ERP.UnitTests/StockInAuthorizationTests.cs`：内存库 + **真实 HTTP 身份**（`StockInTestAuthorization` 播种 `SuperAdmin` / 受限制入库操作员、`stock-in` 菜单、供应商 / 仓库 / 订单 / 客户）。覆盖缺失 / 未映射菜单 / 禁用账号、全部九条路由对拒绝方零副作用且不消耗单号、授权未链接入库单放行、范围外客户订单在创建与读取两侧拒绝、编辑把来源改到范围外被拒绝且已存来源不变、读取与审核 / 取消之间撤销授权（撤菜单 / 禁用账号）立即收敛、分页 / 详情 / 流水作用域一致，以及授权路径的基础单位数量 / 订单成本 / 冲销一致。
- `src/ERP.UnitTests/PurchaseStockInCostTests.cs` 等旧夹具：改为经 `StockInTestAuthorization` / `StockInLegacyTestFixture` 注入**真实已授权测试身份**并播种真实供应商 / 仓库，保留全部原有数量 / 成本 / 冲销断言（不绕过授权、不放宽来源校验）。
- `src/ERP.UnitTests/StockInMissingContextTests.cs`：无 HTTP 上下文时必须按未认证拒绝，绝不退化为管理员。
- `src/ERP.IntegrationTests/StockInAuthorizationSqlServerTests.cs`：真实 SQL Server（专用 localdb）覆盖无身份 / 禁用 / 无菜单拒绝且库存与流水不变、种子管理员的合法放行、受限制操作员的范围内 / 未链接放行与范围外拒绝、读取后撤销菜单授权立即 fail closed。

## 5. 安全（真实 SQL 夹具口径）

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST`、`Integrated Security`；错误目标在访问数据库**之前** fail closed（`AssertDedicatedTarget` / `Guard`）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；**绝不 drop / reset / 复用任何数据库**。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings` / `.env` / 生产凭据，也不执行生产库。
- 种子数据使用 SQL 生成身份（EF 自增键），不硬编码主键；日志完整保留。

## 6. 边界

本护栏只保护采购入库单生命周期与库存写入：不改变库存成本口径，不改写采购订单 / 供应商 / 仓库 / 客户主数据，不删除历史单据，不做报表、界面或生产运维操作。
