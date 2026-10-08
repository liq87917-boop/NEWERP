# 库存盘点/调整单过账 / 冲销原子化（ERP-355）

## 1. 目标

让**既有**库存盘点/调整单（`StockAdjustment` / `StockAdjustmentDetail`）的「审核（过账）、销审（冲销）、取消、提交、删除、修改」
统一到同一条**单据边界**上：审核必须基于**已验证的账面数量基线**（账面数量 == 权威当前库存），把全部差异调整、
库存流水与单据状态写进**一个可串行化事务**并**恰好一次**；账面数量与权威库存不一致（基线过期）时**拒绝过账**，
绝不用「实盘 − 客户端账面」的差额静默过账出虚构差异，也不再自动改写账面数量，而是要求在**待提交状态**显式重新盘点 / 修改。

只为既有盘点单补齐过账护栏：复用既有分层架构、既有 `InventoryService` 记账 / 冲销口径与既有单据流转，
不做报表、不改功能前端、不改主数据、不新增表 / 列 / 权限 / 菜单，也不做任何生产运维操作。

## 2. 口径

| 项 | 口径 |
|---|---|
| 授权 | 操作盘点单要求当前账号具备既有「库存查询（stock-query）」菜单授权；缺失 / 禁用 / 未映射身份一律 fail closed。**不新增权限模型，不把空身份当作管理员，也不新增用户授权** |
| 校验时点 | 读取 / 创建 / 修改 / 提交 / 审核 / 销审 / 取消 / 删除**每一路由**都重新解析身份与菜单；审核 / 销审 / 取消 / 提交 / 删除 / 修改还在**锁内**再解析一次，读取后撤销授权立即收敛 |
| 明细校验 | 商品 Id 必须有效、账面 / 实盘数量与成本单价**不得为负数**、同一商品**不得重复**出现在一张盘点单；仓库与每个商品必须**存在、未删除、已启用**（Status = 1）；任一不满足即拒绝，**先于任何库存写入** |
| 账面基线 | 审核时逐行读取**权威当前库存**（`InventoryService.GetCurrentQuantityAsync`，无库存行按 0），要求 `账面数量 == 当前库存`；不一致即判定「账面基线过期」并**拒绝过账**，库存 / 流水 / 状态与账面数量全部保持原样 |
| 修正路径 | 基线过期**不自动更正**：必须在**待提交状态**重新盘点或修改明细（`Update` 仅 `Pending` 允许），再提交审核；不提供任何「自动把账面改写成当前库存」的隐式修正 |
| 审核前置 | 先取本单行锁（`UPDLOCK, HOLDLOCK`）+ `IsolationLevel.Serializable` 事务，再在锁内重读权威状态（必须 `Submitted`）、有效流水（必须为 0，幂等护栏）与明细（必须非空） |
| 过账原子性 | 全部差异明细、库存流水与单据状态在**同一事务**内提交；任一步失败（含后续明细异常 / 库存不足）整体回滚，库存 / 流水 / 状态全部不变 |
| 成本口径 | 完全沿用 ERP-009 移动加权平均：`差异数量 = 实盘 − 账面`、`差异金额 = 差异数量 × 成本单价`（成本单价留 0 时取当前加权平均成本）；盘盈按差异数量入库、盘亏按差异数量出库。**本次不改动任何估值规则** |
| 销审 | 与审核共用同一把单据行锁；先按确定性顺序锁定受影响库存行，再按既有红字流水冲销；差异入库已被后续业务占用时拒绝，且余额 / 流水 / 状态全部不变；重复销审被状态门拒绝 |
| 取消 / 提交 / 删除 / 修改 | 与审核共用同一把单据行锁；已审核不可取消 / 删除 / 修改（须先销审）；提交仅 `Pending`、删除仅 `Pending`、修改仅 `Pending`（保持既有编辑规则） |
| 读取 | 列表 / 详情 / 流水同样要求当前身份与既有菜单授权 |

## 3. 实现

- `src/ERP.Application/Services/StockAdjustmentPostingRules.cs`（新增）：纯判定与有界只读查询 ——
  `EnsureMenuAuthorizedAsync`（身份 / 账号状态 / 既有菜单三重 fail closed）、`IsRelationalProvider`、
  `StockKeys`（按仓库 × 明细商品构造待锁库存行集合）、`EnsureLineShape`（非负数量 / 成本、商品 Id、商品不重复）、
  `EnsureLiveMasterDataAsync`（仓库与商品在用）、`IsStaleBookQuantity` / `EnsureFreshBookQuantity`（账面基线核验）。
  不落库、不改单据 / 库存 / 流水、不改主数据。
- `src/ERP.Application/Services/InventoryService.cs`：新增只读 `GetCurrentQuantityAsync`
  （权威当前库存数量，无库存行按 0，不建行、不改成本），供盘点过账前的账面基线核验；
  `IncreaseAsync` / `DecreaseAsync` / `ReverseAsync` 与既有 `StockIdentity` / `OrderStockIdentities`
  的估价、冲销与确定性锁定顺序一字未改。
- `src/ERP.Api/Controllers/StockAdjustmentController.cs`：
  - `AuthorizeAsync` → 实时授权（fail closed），`Create` 中授权**先于**单号生成；
  - `LockAdjustmentRowAsync` → `SELECT Id FROM db_owner.StockAdjustments WITH (UPDLOCK, HOLDLOCK) WHERE Id = @id`（内存库跳过）；
  - `LockAdjustmentStocksAsync` → 按 `InventoryService.OrderStockIdentities(StockAdjustmentPostingRules.StockKeys(...))`
    的顺序逐行 `SELECT Id FROM db_owner.Stocks WITH (UPDLOCK, HOLDLOCK)`；
  - `Approve` → 「可串行化事务 → 单据行锁 → 锁内授权 → 锁内重读 → 明细 / 主数据校验 → 确定性库存行锁 →
    账面基线核验 → 差异记账（增 / 减）→ 置状态 → 提交」，失败 `Rollback` 并丢弃跟踪中的半成品变更；
  - `Unaudit` / `Cancel` / `Submit` / `Delete` / `Update` → 一律「可串行化事务 → 单据行锁 → 锁内授权 →
    锁内重读 → 业务判定 → 写入 → 提交」，共用同一把单据行锁。

## 4. 并发与锁顺序

`Approve` / `Unaudit` / `Cancel` / `Submit` / `Delete` / `Update` 按如下顺序执行，整体包在
`IsolationLevel.Serializable` 事务内，并**共用同一把盘点单行锁**
（`SELECT Id FROM db_owner.StockAdjustments WITH (UPDLOCK, HOLDLOCK) WHERE Id = @id`）：

1. 取得盘点单行锁（行不存在 → 业务「不存在」拒绝）；
2. 锁内实时授权；
3. 锁内重读本单（含明细）与有效流水（审核时 `CountActiveMovementsAsync` 必须为 0）；
4. 审核 / 销审时按**确定性顺序**（仓库 Id 升序 → 商品 Id 升序）对受影响库存行加 `UPDLOCK/HOLDLOCK`；
5. 审核时逐行核验**账面基线**（账面数量必须等于权威当前库存），随后 `InventoryService` 记账（增 / 减）并写流水；
6. 置状态并提交事务；任一步失败即回滚（内存库由控制器丢弃跟踪中的半成品变更）。

由此得到：

- **并发审核 / 审核**：先到者提交后，后到者拿到锁时看到状态已是 `Approved` / 已产生流水，被状态门或幂等护栏拒绝 → 只过账一次；
- **并发审核 / 取消**：审核先提交 → 取消被「已审核」拒绝；取消先提交 → 审核看到已取消而拒绝 → 绝不产生任何库存；
- **多明细 / 多单据**：受影响库存行都按「仓库 → 商品」升序取锁，不形成环形等待，不会互相死锁。

## 5. 错误信息（API 直接返回，均走既有异常中间件）

- 无身份：`请先登录后再操作库存盘点单`（`Unauthorized`）；
- 未映射菜单 / 禁用账号：`当前账号没有「库存查询」（stock-query）模块授权…` / `登录账号已禁用…`（`Forbidden`）；
- 明细非法：`商品 [x] 的账面数量不能为负数` / `实盘数量不能为负数` / `成本单价不能为负数` /
  `商品 [x] 在同一张盘点单中重复出现，请合并为一行` / `盘点明细 [x] 缺少有效商品 Id，无法盘点`（`InvalidParameter`）；
- 主数据不可用：`盘点仓库不存在或已删除，无法盘点` / `仓库 [x] 已停用，禁止盘点调整（fail closed）` /
  `商品 [x] 不存在或已删除，无法盘点` / `商品 [x] 已停用，禁止盘点调整（fail closed）`（`InvalidParameter` / `RuleConflict`）；
- **账面基线过期**：`商品 [x] 的账面数量 n 与当前库存 m 不一致：账面基线已过期，禁止按虚构差异过账；请在待提交状态重新盘点或修改后再提交审核`（`RuleConflict`，库存 / 流水 / 状态不变）；
- 状态门：`仅已提交的盘点单可审核` / `仅已审核的盘点单可销审` / `已审核的盘点单不能取消，请先销审`（`RuleConflict`）；
- 库存不足：`商品 [x] 库存不足：当前库存 n，需要 m`（`RuleConflict`，整单回滚）；
- 销审受阻：`商品 [x] 当前库存 n 不足以冲销 m，该入库已被后续业务占用，无法销审`（`RuleConflict`，全部不变）。

## 6. 不改写项

- 不新增表 / 列 / 索引 / 外键 / schema，不改 `SchemaUpgrader` 与种子数据；不新增权限、菜单、角色或用户授权；
- 不改库存估价口径（移动加权平均、金额 = 数量 × 成本、4 位金额 / 6 位单价取整）与「Σ 流水金额 = 库存余额」核对关系；
- **不删除、不重写任何历史库存流水**（冲销只追加红字流水）；不做任何「自动更正持久化历史记录」的写回；
- 不改盘点以外的任何单据、主数据、报表、财务或前端；不做生产运维与不可逆数据操作。

## 7. 测试

- 单元测试 `src/ERP.UnitTests/StockAdjustmentPostingTests.cs`（内存库 + 真实 HTTP 身份）：
  授权操作员按已验证账面基线审核恰好一次、重复审核拒绝；账面基线过期拒绝且不静默更正账面数量；
  待提交状态修改账面后重新提交可审核；非负数量 / 成本、商品重复、商品缺失、仓库缺失、主数据停用等校验；
  无身份 / 无菜单 / 禁用账号 fail closed（审核 / 提交 / 取消 / 删除一律拒绝，创建不消耗单号）；
  销审红字冲销还原、重复销审拒绝、货物被占用时全部不变；已审核不可取消 / 删除、待提交可取消 / 删除；
  确定性锁定顺序（去重、仓库 → 商品升序）与控制器 / 规则源代码契约。
- 既有 `src/ERP.UnitTests/InventoryMovementTests.cs`：盘点用例改经授权测试身份执行（同时播种在用仓库 / 商品），
  保留原有全部数量 / 成本 / 差异 / 冲销断言。
- 集成测试 `src/ERP.IntegrationTests/StockAdjustmentPostingSqlServerTests.cs`（NEWERP_AUTOTEST 护栏，真实 SQL Server）：
  实时授权拒绝时库存 / 流水 / 状态不变；授权操作员按已验证账面基线审核恰好一次并销审还原；
  **两条独立连接**「审核 vs 审核」只允许一方过账、「审核 vs 取消」只允许一方成功。

## 8. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，
  且 `Integrated Security=true`；护栏在**任何数据库访问之前**校验失败即中止；
- 每次运行创建**全新的 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 不读取 `appsettings` / `.env` / 生产凭据；连接串只来自进程环境变量或专用 localdb 默认值；
- 测试种子一律使用 SQL Server 自增主键（不显式指定 Id），保留完整日志输出。

> 说明：本文件的完成只代表 Release 构建与单元测试通过，**构建完成不代表阶段验收完成**；
> 真实 SQL 集成场景与最终验收由调度器 / 验收运行器按各自门禁执行。

