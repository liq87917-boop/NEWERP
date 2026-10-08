# 仓库调拨单过账 / 冲销原子化（ERP-354）

## 1. 目标

让**既有**仓库调拨单（`StockTransfer` / `StockTransferDetail`）的「审核（过账）、销审（冲销）、取消、删除」统一到
同一条**单据边界**上：审核必须**恰好一次**把「调出仓减少 + 调入仓增加（两侧同一成本单价）、全部明细成本、
库存流水与单据状态」写进**一个可串行化事务**；销审按既有红字流水冲销；取消 / 删除与审核共用同一把
**调拨单行锁**，因此并发「审核 / 审核」或「审核 / 取消」不可能同时成功，也绝不产生重复或已取消单据的库存。

只复用既有分层架构、既有 `InventoryService` 记账口径与既有单据流转，不做报表、不改功能前端、不改主数据、
不新增表 / 列 / 权限 / 菜单，也不做任何生产运维操作。

## 2. 口径

| 项 | 口径 |
|---|---|
| 授权 | 操作调拨单要求当前账号具备既有「库存查询（stock-query）」菜单授权；缺失 / 禁用 / 未映射身份一律 fail closed。**不新增权限模型，不把空身份当作管理员，也不新增用户授权** |
| 校验时点 | 读取 / 创建 / 修改 / 提交 / 审核 / 销审 / 取消 / 删除**每一路由**都重新解析身份与菜单；审核 / 销审 / 取消 / 删除还在**锁内**再解析一次，读取后撤销授权立即收敛 |
| 审核前置 | 先取本单行锁（`UPDLOCK, HOLDLOCK`）+ `IsolationLevel.Serializable` 事务，再在锁内重读权威状态（必须 `Submitted`）、有效流水（必须为 0）与明细（必须非空、数量 > 0、两仓不同） |
| 过账原子性 | 两侧库存、全部明细成本、库存流水与单据状态在**同一事务**内提交；任一步失败（含后续明细库存不足）整体回滚，两侧余额 / 流水 / 状态全部不变 |
| 成本口径 | 完全沿用 ERP-009 移动加权平均：调出取调出仓当前加权平均成本（或明细指定成本），调入沿用同一成本单价，保证「调出金额 = 调入金额」、总量与总金额守恒。**本次不改动任何估值规则，也不移除任何数量 / 成本断言** |
| 销审 | 按既有红字流水冲销；调入仓货物已被后续业务占用（数量或金额不足）时拒绝，且两侧余额 / 流水 / 状态全部不变；重复销审被状态门拒绝 |
| 取消 / 删除 / 提交 / 修改 | 与审核共用同一把单据行锁；已审核不可取消 / 删除 / 修改（须先销审），提交仅 `Pending`、删除仅 `Pending`、修改仅 `Pending`（保持既有编辑规则） |
| 读取 | 列表 / 详情 / 流水同样要求当前身份与既有菜单授权 |

## 3. 实现

- `src/ERP.Application/Services/StockTransferPostingRules.cs`（新增）：纯判定与有界只读查询 ——
  `EnsureMenuAuthorizedAsync`（身份 / 账号状态 / 既有菜单三重 fail closed）、`IsRelationalProvider`、
  `StockKeys`（按两侧仓库 × 去重商品构造待锁库存行集合）。不落库、不改单据 / 库存 / 流水。
- `src/ERP.Application/Services/InventoryService.cs`：新增 `StockIdentity`（仓库 + 商品）与纯函数
  `InventoryService.OrderStockIdentities`（过滤非法键、去重后按「仓库 Id 升序 → 商品 Id 升序」排序），
  为跨单据提供**确定性库存行锁定顺序**；既有 `IncreaseAsync` / `DecreaseAsync` / `ReverseAsync`
  的估价与冲销算法一字未改。
- `src/ERP.Api/Controllers/StockTransferController.cs`：
  - `AuthorizeAsync` → 实时授权（fail closed）；
  - `LockTransferRowAsync` → `SELECT Id FROM db_owner.StockTransfers WITH (UPDLOCK, HOLDLOCK) WHERE Id = @id`（内存库跳过）；
  - `LockTransferStocksAsync` → 按 `InventoryService.OrderStockIdentities(StockTransferPostingRules.StockKeys(...))`
    的顺序逐行 `SELECT Id FROM db_owner.Stocks WITH (UPDLOCK, HOLDLOCK)`；
  - `Approve` / `Unaudit` / `Cancel` / `Submit` / `Delete` / `Update` → 一律「可串行化事务 → 单据行锁 → 锁内授权 →
    锁内重读 → 业务判定 → 写入 → 提交」，失败 `Rollback` 并丢弃跟踪中的半成品变更；
  - `Create` → 授权**先于**单号生成，被拒绝方绝不消耗单据号。

## 4. 并发与锁顺序

`Approve` / `Unaudit` / `Cancel` / `Submit` / `Delete` / `Update` 按如下顺序执行，整体包在
`IsolationLevel.Serializable` 事务内，并**共用同一把调拨单行锁**
（`SELECT Id FROM db_owner.StockTransfers WITH (UPDLOCK, HOLDLOCK) WHERE Id = @id`）：

1. 取得调拨单行锁（行不存在 → 业务「不存在」拒绝）；
2. 锁内实时授权；
3. 锁内重读本单（含明细）与有效流水（审核时 `CountActiveMovementsAsync` 必须为 0）；
4. 审核 / 销审时按**确定性顺序**（仓库 Id 升序 → 商品 Id 升序）对两侧库存行加 `UPDLOCK/HOLDLOCK`；
5. `InventoryService` 记账（增加 / 减少 / 红字冲销）并写流水；
6. 置状态并提交事务；任一步失败即回滚（内存库由控制器丢弃跟踪中的半成品变更）。

由此得到：

- **并发审核 / 审核**：先到者提交后，后到者拿到锁时看到状态已是 `Approved` / 已产生流水，被状态门或幂等护栏拒绝 → 只过账一次；
- **并发审核 / 取消**：审核先提交 → 取消被「已审核」拒绝；取消先提交 → 审核看到已取消而拒绝 → 绝不产生任何库存；
- **对向调拨（A→B 与 B→A）**：两侧库存行都按「仓库 → 商品」升序取锁，不形成环形等待，不会互相死锁。

## 5. 错误信息（API 直接返回，均走既有异常中间件）

- 无身份：`请先登录后再操作仓库调拨单`（`Unauthorized`）；
- 未映射菜单 / 禁用账号：`当前账号没有「库存查询」（stock-query）模块授权…` / `登录账号已禁用…`（`Forbidden`）；
- 校验失败：`仅已提交的调拨单可审核` / `仅已审核的调拨单可销审` / `已审核的调拨单不能取消，请先销审`（`RuleConflict`）；
- 库存不足：`商品 [x] 库存不足：当前库存 n，需要 m`（`RuleConflict`，整单回滚）；
- 销审受阻：`商品 [x] 当前库存 n 不足以冲销 m，该入库已被后续业务占用，无法销审`（`RuleConflict`，两侧不变）。

## 6. 不改写项

- 不新增表 / 列 / 索引 / 外键 / schema，不改 `SchemaUpgrader` 与种子数据；不新增权限、菜单、角色或用户授权；
- 不改库存估价口径（移动加权平均、同一成本单价、金额 = 数量 × 成本、4 位金额 / 6 位单价取整）与
  「Σ 流水金额 = 库存余额」核对关系；不删除或重写任何历史库存流水（冲销只追加红字流水）；
- 不改调拨以外的任何单据、主数据、报表、财务或前端；不做生产运维与不可逆数据操作。

## 7. 测试

- 单元测试 `src/ERP.UnitTests/StockTransferPostingTests.cs`（内存库 + 真实 HTTP 身份）：
  授权操作员审核恰好一次（两仓守恒、成本单价一致）与重复审核拒绝；销审还原与重复销审拒绝；
  无身份 / 无菜单 / 禁用账号 fail closed 且不落库存、创建不消耗单号；后续明细失败与销审调入被消耗时
  两侧余额 / 流水 / 状态全部不变；已审核不可取消 / 删除 / 修改、待提交保持既有规则；
  确定性锁定顺序（去重、仓库 → 商品升序、对向同序）；控制器「先锁单据行再读状态流水、按确定性顺序锁库存行」的源代码契约。
- 既有 `src/ERP.UnitTests/InventoryMovementTests.cs`：调拨用例改经授权测试身份执行，保留全部原有数量 / 成本 / 冲销断言。
- 集成测试 `src/ERP.IntegrationTests/StockTransferPostingSqlServerTests.cs`（NEWERP_AUTOTEST 护栏，真实 SQL Server）：
  实时授权拒绝时库存 / 流水 / 状态不变、审核恰好一次并销审还原、
  **两条独立连接**「审核 vs 审核」只允许一方过账、「审核 vs 取消」只允许一方成功、
  对向调拨并发审核不死锁。

## 8. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，
  且 `Integrated Security=true`；护栏在**任何数据库访问之前**校验失败即中止；
- 每次运行创建**全新的 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 不读取 `appsettings` / `.env` / 生产凭据；连接串只来自进程环境变量或专用 localdb 默认值；
- 测试种子一律使用 SQL Server 自增主键（不显式指定 Id），保留完整日志输出。

> 说明：本文件的完成只代表 Release 构建与单元测试通过，**构建完成不代表阶段验收完成**；
> 真实 SQL 集成场景与最终验收由调度器 / 验收运行器按各自门禁执行。


## SQL 并发验证整改

真实双连接对向调拨暴露 SQL Server 库存扫描范围锁死锁（原始证据：ERP-354-independent-sql.log）。确定性业务键排序不能保证无覆盖索引时实际扫描锁的顺序。调拨事务现在先取得数据库内 transaction-owned application lock，再锁单据和库存；事务提交或回滚自动释放。此边界将不同调拨单过账串行化，保留数量、成本和权限检查，不修改其他业务事务协议。锁超时拒绝操作，禁止忽略 SQL 错误或伪造成功。SQL 测试使用共享锁实现验证协议；真实 HTTP 端到端验收仍单独保留。
