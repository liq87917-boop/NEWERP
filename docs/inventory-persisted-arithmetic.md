# 库存增减 / 冲销的持久化算术护栏（ERP-527）

## 1. 目标

让库存加减的唯一入口 `InventoryService.IncreaseAsync` / `DecreaseAsync` / `ReverseAsync` 在**任何被跟踪实体变更之前**，
把「入参数量 / 成本、由它们计算出的移动金额、以及移动后的结存数量 / 可用数量 / 结存金额 / 加权平均成本」
统一收敛到**实际 EF 存储精度**内，关闭既有缺口：

1. `IncreaseAsync` 直接 `quantity * cost` 并把数量 / 金额累加到被跟踪库存行，既没有乘法溢出防护，
   也没有「结存数量 / 可用数量（`DECIMAL(18,2)`）、结存金额（`DECIMAL(18,4)`）、加权平均成本（`DECIMAL(18,6)`）」
   的可表示性判定；
2. `DecreaseAsync` 同样直接计算移动金额；出库清零按账面金额核减后**折算回成本单价** `amount / quantity`
   可能超过 `DECIMAL(18,6)`；
3. `ReverseAsync` 逐笔**先改库存再判断下一笔**：多笔冲销中后一笔越界时，前一笔已改的库存与流水标记会留在
   被跟踪状态里；红字流水直接把历史流水的数量 / 单价 / 金额搬到新行，历史被外部改写时不设防。

结果：单个源单据行自身在其列内合法（例如数量合法落在 `DECIMAL(18,4)`），仍可能让**累计结存**越过库存行
`DECIMAL(18,2)` 的上限，或在 SQL 写入时被静默取整 / 溢出，或抛出未处理的 `decimal` 溢出异常。

本护栏只补齐「持久化算术边界」，**不**改估值口径（移动加权平均）、成本缺省兜底、出库清零按账面金额核减、
冲销 = 严格逆操作、确定性锁定顺序与调用方事务归属。

## 2. 精度口径（与实际 EF 存储同源，只读检查、不猜小数位）

| 列 | 实际存储 | 规则常量 |
|---|---|---|
| `Stock.Quantity` / `AvailableQuantity` / `LockedQuantity` | `DECIMAL(18,2)`（**EF 默认精度**：模型未显式 `HasPrecision`；真实 `INFORMATION_SCHEMA` 亦为 `decimal(18,2)`） | `StockQuantityScale = 2`，`MaxStockQuantityValue = 9_999_999_999_999_999.99` |
| `StockMovement.BalanceQuantity`（结存数量快照） | `DECIMAL(18,4)`（显式声明） | `MovementScale = 4`，`MaxMovementValue = 99_999_999_999_999.9999` |
| `Stock.TotalCost` | `DECIMAL(18,4)`（`ErpDbContext.Config` 显式声明） | `MovementScale = 4`，`MaxMovementValue = 99_999_999_999_999.9999` |
| `Stock.AverageCost` | `DECIMAL(18,6)`（显式声明） | `CostScale = 6`，`MaxCostValue = 999_999_999_999.999999` |
| `StockMovement.Quantity` / `Amount` / `BalanceQuantity` / `BalanceAmount` | `DECIMAL(18,4)`（显式声明） | 同上 |
| `StockMovement.UnitCost` / `BalanceAverageCost` | `DECIMAL(18,6)`（显式声明） | 同上 |

- 精度常量与 `ErpDbContext.Config.cs` 的 `HasPrecision(...)` 显式声明、以及 `SchemaUpgrader` 建表脚本**一字同源**；
- 区间口径**复用**系统唯一标量边界规则 `FinancePersistedAmountRules`（ERP-508 起的 `IsWithinDecimal18` /
  `SafeMultiply`），**不新增第二套区间常量**；
- 取整口径复用既有 `InventoryService.RoundAmount`（4 位、`AwayFromZero`）与 `RoundCost`（6 位、`AwayFromZero`）：
  护栏只做「取整之后的逐位原样可表示判定」，**绝不**重新取整、**绝不**把值截断为更粗的小数位。

## 3. 护栏口径

| 项 | 口径 |
|---|---|
| 校验时点 | 库存在被跟踪实体**变更之前**：入参 → 成本单价 → 移动金额 → 结存提案，逐项判定通过后才写入库存行 / 追加流水 |
| 入参数量 | 必须为正（既有口径）且能在库存流水数量列 `DECIMAL(18,4)` 内**逐位原样**表示；更细小数位（如 `1.00001`）与越界（如 `1e14`）均受控拒绝 |
| 成本单价 | 显式成本或缺省兜底均价先按既有 `InventoryService.RoundCost`（6 位、`AwayFromZero`）取整（唯一取整入口不变），护栏只在该取整之后判定实际存储精度；出库清零折算回的单价同样受此判定（如 `1e12 / 0.01 = 1e14` 直接拒绝） |
| 移动金额 | `RoundAmount(数量 × 成本单价)`：乘法溢出与越界统一转换为受控业务错误，结果必须能在 `DECIMAL(18,4)` 内逐位原样表示 |
| 结存提案 | 结存数量必须**同时**能在库存行数量列 `DECIMAL(18,2)` 与库存流水结存数量快照列 `DECIMAL(18,4)` 内逐位原样表示（因此实际可落库上界是二者交集：小数位 ≤ 2 时为 `99_999_999_999_999.99`，最小单位 `0.01`）；可用数量 `DECIMAL(18,2)`；结存金额 `DECIMAL(18,4)`；加权平均成本 `DECIMAL(18,6)`（数量归零时金额与成本一并归零的既有语义保留） |
| 冲销 | 先对**全部**未冲销流水算出各自结存提案并逐笔校验（同一库存行按提案值继续推算），全部通过后才改库存 / 标记原流水 / 追加红字；红字保留数量原值、方向取反、金额取反与既有符号 |
| 溢出 | `decimal` 乘 / 加 / 减 / 除的 `OverflowException`（及折算除零）一律转换为受控业务错误（`InvalidParameter`），绝不以未处理异常崩溃、绝不落库截断值 |
| 失败零变更 | 失败时库存行、库存流水、原流水 `IsReversed` 标记全部保持原样；`IncreaseAsync` / `DecreaseAsync` 若已在本次调用中新建库存行，会把它移出跟踪（`ChangeTracker` 不留半截数据） |
| 调用方事务 | 调用方（入库 / 出库 / 调拨 / 盘点 / 退货控制器）保留**全行原子事务**：任一行失败即整体回滚，含同单据先前已记账的行；盘点 / 调拨等控制器失败时同时丢弃跟踪中的半成品变更 |
| 权限 / 审计 / 锁 | 不改任何身份 / 菜单 / 客户范围授权，不改确定性库存行锁定顺序、行锁（`UPDLOCK/HOLDLOCK`）与审计字段；不新增权限、菜单、表、列、索引 |

## 4. 实现

- `src/ERP.Application/Services/InventoryService.cs`
  - 新增同文件内的纯函数规则类 `InventoryPersistedArithmeticRules`：精度 / 区间常量（复用
    `FinancePersistedAmountRules`）、`IsRepresentable` / `EnsureRepresentable`（逐位原样可表示）、
    `EnsureMovementQuantity` / `EnsureMovementAmount` / `EnsureUnitCost` / `EnsureStockQuantity` /
    `EnsureBalanceQuantity`（结存数量同时受库存行数量列与流水结存快照列约束）/ `EnsureStockAmount` /
    `EnsureAverageCost`、`ComputeMovementAmount`（受控乘法 + 既有 4 位取整）、
    `SafeAdd` / `SafeSubtract` / `SafeMultiply` / `SafeDivide`（CLR 溢出与除零 → 受控 `InvalidParameter`）；
  - `IncreaseAsync` / `DecreaseAsync`：入参 → 成本（先按既有 `RoundCost` 取整再判定区间）→ 移动金额 →
    结存提案（`StockBalance`）逐项判定通过后，才由 `ApplyBalance` 写入被跟踪库存行并 `BuildMovement` 追加流水；
    `catch` 中 `DiscardIfNewlyCreated` 把本次新建（`EntityState.Added`）的库存行移出跟踪；
  - `ReverseAsync`：第一遍只**计算并校验**每笔冲销后的结存（同一库存行用 `shadows` 提案继续推算，
    不触碰实体），第二遍才 `ApplyBalance` + 标记 `IsReversed` + 追加红字流水；
  - 私有 `AverageOf(Stock)` 替换为等价的纯提案 `Settle(quantity, availableQuantity, totalCost)`
    （数量 ≤ 0 时金额与成本一并归零，否则 `RoundCost(金额 / 数量)`），语义与副作用时机与既有实现一致。
- 控制器（`StockIn` / `StockOut` / `StockTransfer` / `StockAdjustment` / `SalesReturn` / `PurchaseReturn`）：
  **不改**调用路径、事务、锁与授权；它们的既有 `catch → 回滚（+ 丢弃跟踪）` 现在保证「被拒绝的行不留任何
  库存 / 流水 / 状态变化，含同单据先前已记账的行」。

## 5. 不改写项

- 不新增 / 不修改任何表、列、索引、schema、迁移或种子数据；不新增权限、菜单、角色、用户授权；
- 不改估值口径（移动加权平均、成本单价缺省取当前加权平均、金额 4 位 / 成本 6 位 `AwayFromZero` 取整）；
- 不改出库成本下限保护（按账面金额核减、清零不留残额）、库存不足规则、确定性锁定顺序与红字冲销语义；
- 不改 `StockMovement` 的历史读取 / 查询 / 报表与「Σ 流水金额 = 库存余额」核对关系；
- 不做任何历史数据修复 / 回填，不执行生产 SQL、不部署、不发布。

## 6. 测试

- 单元测试 `src/ERP.UnitTests/InventoryPersistedArithmeticTests.cs`（内存库 + 真实服务 / 真实控制器）：
  - 实际 EF 列精度与规则常量一字同源（含真实 `decimal(18,2)` 的库存数量列）与边界量子；
  - 入库：结存数量精确上限放行、上限 + 一个最小单位（`0.01`）拒绝；入参数量 / 成本过精度与越界、
    乘法越界、结存数量过精度、加权平均成本不可表示一律受控拒绝且零跟踪；
  - 出库：清零按账面金额核减后折算成本单价越界拒绝；库存不足保留既有受控错误且不新建库存行；
  - 冲销：合法红字方向取反 / 金额取反 / 数量原值 / 结存还原；单笔结存越界、多笔中任一越界、
    历史流水被改写为不可表示时全部零变更；多行调拨单调入侧溢出时整单回滚（含先前已调出的来源行）。
- 集成测试 `src/ERP.IntegrationTests/InventoryPersistedArithmeticSqlServerTests.cs`（NEWERP_AUTOTEST 护栏，真实 SQL Server）：
  `INFORMATION_SCHEMA` 实际列精度、精确上限原样往返、上限 + 最小单位零写入、
  多行盘点 / 调拨单溢出整单回滚、冲销溢出零变更。
- 既有相关回归：`InventoryMovementTests`、`StockAdjustment*`、`StockTransfer*`、`StockIn*`、`StockOut*`、
  `SalesReturn*`、`PurchaseReturn*` 等单元测试与对应真实 SQL 回归全部保持通过。

## 7. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，
  且 `Integrated Security=true`；护栏在**任何数据库访问之前**校验失败即中止；
- 每次运行创建**全新的 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 不读取 `appsettings` / `.env` / 生产凭据；连接串只来自进程环境变量或专用 localdb 默认值；
- 测试种子一律使用 SQL Server 自增主键（不显式指定 Id）。

> 说明：本文件的完成只代表 Release 构建与单元测试通过，**构建完成不代表阶段验收完成**；
> 真实 SQL 集成场景与最终验收由调度器 / 验收运行器按各自门禁执行。


