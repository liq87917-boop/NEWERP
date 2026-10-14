# 库存盘点单数值边界护栏（ERP-507）

## 1. 目标

让既有库存盘点/调整单（`StockAdjustment` / `StockAdjustmentDetail`）的**写入路径**在**单号预约与任何表头 / 明细写入之前**，
把「账面 / 实盘数量、成本单价、由它们计算出的**有符号**差异数量与差异金额、以及表头差异合计」统一收敛到**实际存储精度**内，
关闭两类既有缺口：

1. `StockAdjustmentPostingRules.EnsureLineShape` 只校验「非负」与「商品不重复」，不校验实际存储精度；
   `StockAdjustmentController.Create` 又在这些校验**之前**预约单号；
2. `StockAdjustmentController.Normalize` 直接计算 `实盘 − 账面`、`差异数量 × 成本单价` 与合计，
   既没有上界（`decimal(18,4)` / `decimal(18,6)`）校验，也不处理乘法 / 求和溢出；
   同时 `EnsureLineShape` 只遍历**未删除**行、`Normalize` 与 `ResetDetailIds` 遍历**全部**行 ——
   客户端用 `IsDeleted = true` 标记的坏行会被校验跳过，却仍被持久化并参与合计。

只补齐数值边界与「服务端权威子行语义」，**不**改估值口径、单据流转、库存权限、加减记账与冲销算法。

## 2. 精度口径（与实际 EF 存储同源）

| 列 | 实际存储 | 规则常量 |
|---|---|---|
| `StockAdjustmentDetail.BookQuantity` / `ActualQuantity` / `DiffQuantity` / `DiffAmount` | `DECIMAL(18,4)` | `QuantityScale = 4`，`MaxDecimal18Scale4 = 99_999_999_999_999.9999` |
| `StockAdjustmentDetail.UnitCost` | `DECIMAL(18,6)` | `CostScale = 6`，`MaxDecimal18Scale6 = 999_999_999_999.999999` |
| `StockAdjustment.TotalDiffQuantity` / `TotalDiffAmount` | `DECIMAL(18,4)` | 同上 |

- 精度常量与 `ErpDbContext.Config` 中这些属性的 `HasPrecision(...)` 显式声明**一字同源**；
- 取整口径复用既有 `InventoryService.RoundAmount`（**4 位小数、`MidpointRounding.AwayFromZero`**），
  差异金额的**唯一**计算入口是 `StockAdjustmentPostingRules.ComputeSignedDifference(book, actual, cost)`
  （`Normalize` 与护栏共用，保证「校验的值」与「落库的值」逐分一致）。

## 3. 护栏口径

| 项 | 口径 |
|---|---|
| 校验时点 | **创建 / 修改**：授权之后、**单号预约之前**、**任何已跟踪表头 / 明细写入之前**；**提交 / 审核**：既有单据行锁 + 可串行化事务内、任何写入之前 |
| 逐行校验 | 商品 Id 有效；`BookQuantity` / `ActualQuantity` **非负**且在实际 `DECIMAL(18,4)` 内可表示；`UnitCost` **非负**且在实际 `DECIMAL(18,6)` 内可表示；同一商品不得重复（**保留原有重复商品禁令**） |
| 有符号差异 | `DiffQuantity = 实盘 − 账面`（正 = 盘盈、负 = 盘亏），`DiffAmount = RoundAmount(DiffQuantity × UnitCost)`；逐行校验差异数量与差异金额在 `DECIMAL(18,4)` 内可表示（含乘法溢出） |
| 表头合计 | 按与 `Normalize` 同源的公式重算 `TotalDiffQuantity`（有符号差异求和）与 `TotalDiffAmount`（金额求和后取整），任一不可表示（含求和溢出）即拒绝 |
| 溢出 | `decimal` 乘法 / 加法 `OverflowException` 一律转换为受控业务错误（`InvalidParameter`），绝不落库为截断值 |
| 服务端权威子行语义 | 创建 / 修改时服务端把**每一行**都强制为「正常」行（`Id = 0`、`IsDeleted = false`），客户端 `IsDeleted` **不可能**隐藏坏行后再被重新启用；因此请求侧校验覆盖**全部提交行** |
| 缺省 / 空草稿头 | `Details` 缺省或为空仍受支持（0 行明细），提交亦不新增「非空」闸门；**是否要求非空有效明细由既有审核承诺边界决定**（`盘点单无明细，不能审核`） |
| 提交 / 审核复核 | 提交在锁内复核**当前生效**（未删除）已存明细的数值边界 / 实际存储精度 / 在用主数据；审核在同一把单据行锁内复核明细与**已验证账面数量基线**（账面必须等于权威当前库存）后才记账 |
| 零值语义 | 零账面 / 零实盘 / 零成本都是合法值：保留 `0` 差异、盘盈与盘亏的**真实符号**，绝不绝对化或静默取整为 0 |
| 拒绝语义 | 被拒请求**零单号消耗**，零单据 / 明细 / 库存 / 流水 / 状态变化；修改被拒时既有单据与明细**逐字段不变** |

## 4. 实现

- `src/ERP.Application/Services/StockAdjustmentPostingRules.cs`
  - 新增精度常量与 `IsQuantityRepresentable` / `IsCostRepresentable`；
  - 新增 `ComputeSignedDifference(book, actual, cost)`（有符号差异 + `RoundAmount` 取整的唯一入口）；
  - 重写 `EnsureLineShape`：**不再按 `IsDeleted` 过滤**，逐行校验非负 / 可表示性 / 商品不重复 / 有符号差异 /
    差异金额，并校验表头差异合计可表示性（`AssertTotalRepresentable`），统一把溢出收敛为受控业务错误。
- `src/ERP.Api/Controllers/StockAdjustmentController.cs`
  - `Create`：`ApplyServerOwnedDetailIdentity` → `EnsureLineShape` → `EnsureLiveMasterDataAsync` → **之后**才预约单号与落库；
  - `Update`：`ApplyServerOwnedDetailIdentity` → `EnsureLineShape` → `EnsureLiveMasterDataAsync` → **之后**才改写已跟踪表头与整体替换明细；
  - `Submit`：`Include(o => o.Details)` 后在锁内复核当前生效明细（形状 / 精度 / 在用主数据）再置状态；
  - `Normalize`：差异数量 / 金额改用 `ComputeSignedDifference`（同源），保留符号与取整口径；
  - `ResetDetailIds` → `ApplyServerOwnedDetailIdentity`：明细主键置 0 **且**删除态强制为正常。

## 5. 不改写项

- 不新增 / 不修改任何表、列、索引、schema、迁移或种子数据；不新增权限、菜单、角色、用户授权，不扩大库存权限；
- 不改库存估值口径（移动加权平均、`差异金额 = 差异数量 × 成本单价`、4 位金额 / 6 位单价取整）与
  「Σ 流水金额 = 库存余额」核对关系；
- 不改单据状态门（仅已提交可审核 / 仅已审核可销审 / 已审核不可取消删除）、确定性锁定顺序、
  账面基线核验、红字冲销与历史流水保留语义；**历史读取 / 取消 / 销审 / 冲销不新增任何草稿有效性闸门**；
- 不改其它单据、主数据、报表、财务或前端；不做生产运维与不可逆数据操作。

## 6. 测试

- 单元测试 `src/ERP.UnitTests/StockAdjustmentNumericBoundsTests.cs`（内存库 + 真实 HTTP 身份）：
  实际 EF 列精度与规则常量同源、可表示边界 / 4 位与 6 位小数、负值、乘法与合计溢出、
  客户端删除态隐藏坏行仍拒绝、隐藏有效行被恢复为正常行并计入合计、零值 / 有符号差异、
  空草稿头仍可创建与提交（审核仍要求有效明细）、修改被拒后既有单据明细逐字段不变、
  提交复核已存明细被拒后状态 / 库存 / 流水 / 单号序列不变、合法盘点按符号过账、控制器源码契约
  （护栏先于单号预约与明细替换）。
- 既有 `src/ERP.UnitTests/StockAdjustmentPostingTests.cs` / `src/ERP.UnitTests/InventoryMovementTests.cs`：
  保留原过账 / 冲销 / 并发 / 确定性锁定断言与重复商品禁令，全部照常通过。
- 集成测试 `src/ERP.IntegrationTests/StockAdjustmentNumericBoundsSqlServerTests.cs`（NEWERP_AUTOTEST 护栏，真实 SQL Server）：
  `INFORMATION_SCHEMA` 实际列精度、边界值与 4 / 6 位小数原样落库、非法数值与隐藏行零写入、单号序列不推进、
  合法盘点（盘盈 + / 盘亏 −）过账与红字冲销还原。

## 7. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，
  且 `Integrated Security=true`；护栏在**任何数据库访问之前**校验失败即中止；
- 每次运行创建**全新的 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 不读取 `appsettings` / `.env` / 生产凭据；连接串只来自进程环境变量或专用 localdb 默认值；
- 测试种子一律使用 SQL Server 自增主键（不显式指定 Id）。

> 说明：本文件的完成只代表 Release 构建与单元测试通过，**构建完成不代表阶段验收完成**；
> 真实 SQL 集成场景与最终验收由调度器 / 验收运行器按各自门禁执行。
