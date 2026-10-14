# 单证明细行与表头金额的实际存储精度护栏（ERP-518）

## 1. 目标

让单证中心（`TradeDocument` / `TradeDocumentItem`）的**三条写入路径**在**单证编号预约 / 任何已跟踪写入之前**，
把会落库的数值收敛到**实际列精度**内：

1. **明细行手动新增 / 修改**（`TradeDocumentItemService.CreateAsync` / `UpdateAsync`）此前只校验「数量 > 0、
   单价非负、最多 4 位小数、重量非负」，**没有**任何 18 位存储上界：极大正数量 / 单价 / 重量（例如 `1e14`、
   `1e16`、`decimal.MaxValue`）能通过应用层检查，却会在写入 SQL 时被截断 / 溢出；
2. `TradeDocumentItemRules.ComputeLineAmount` 此前**直接** `数量 × 单价` 再按币种取整：既可能触发 CLR 十进制
   乘法溢出（未处理异常），也可能产出超出 `TradeDocumentItem.LineAmount`（`decimal(18,2)`）容量的行金额；
3. **来源快照生成**（销售订单 / 装柜清单 → 商业发票 / 装箱单）此前沿用的 `NormalizeQuantity` /
   `NormalizeUnitPrice` / `GrossWeightOf` 只做符号与 4 位小数判定，没有存储上界；
4. **单证表头金额**（`TradeDocumentController.Create` / `Update`）此前直接把客户端 `Amount` 落库，
   没有任何实际列容量判定。

只补齐**标量存储边界与派生金额的存储边界**，**不**改币种 / 取整口径、单据流转、授权、来源资格、
只读聚合展示与审计。

## 2. 精度口径（与实际 EF / 数据库列同源）

| 列 | 实际存储 | 规则常量（`TradeDocumentItemRules`） |
|---|---|---|
| `TradeDocumentItem.Quantity` / `UnitPrice` / `NetWeight` / `GrossWeight` | `DECIMAL(18,4)`（模型显式 `HasPrecision(18,4)`） | `ItemScalarPrecision = 18`、`ItemScalarScale = 4`（= `MaxAmountDecimals`） |
| `TradeDocumentItem.LineAmount` | `DECIMAL(18,2)`（模型显式 `HasPrecision(18,2)`） | `LineAmountPrecision = 18`、`LineAmountScale = 2` |
| `TradeDocument.Amount`（单证表头金额） | `DECIMAL(18,2)`（EF 默认精度，模型**未**显式声明） | `HeaderAmountPrecision = 18`、`HeaderAmountScale = 2` |

- 边界常量与真实列类型逐字同源：单元测试以 SQL Server 提供程序**仅构建模型**断言
  `IProperty.GetPrecision()` / `GetScale()` / `GetColumnType()`；集成测试再以真实 `INFORMATION_SCHEMA.COLUMNS`
  与 SQL Server 自身「算术溢出（错误号 8115）」双重确认；
- 标量判定**唯一复用**既有 `FinancePersistedAmountRules`（ERP-508 起的系统唯一标量边界规则，
  `IsWithinDecimal18` / `EnsureWithinDecimal18` / `SafeMultiply`），系统内不出现第二套边界规则；
- 币种取整口径**原样**复用既有 `CurrencyAmountRules.RoundAmount`（`AwayFromZero`，零小数币种
  `JPY / KRW / VND / IDR` 为 0 位、其余 2 位）：行金额只在**取整之后**追加 `DECIMAL(18,2)` 区间判定；
- 判定顺序与 SQL Server 一致：按目标列小数位取整（`AwayFromZero`）后再判上界，因此「取整进位后越过上限」的
  值同样被拒绝，而取整后仍在上界内的值照常放行（**绝不静默截断**）；
- 本护栏**只**作用于「会落库的单个标量」：**绝不**施加到 `CurrencyAmountRules.RoundAmount` 本身，也绝不施加到
  只读聚合合计（清单读取的行金额 / 箱数 / 重量合计）——求和（例如两个各自恰好等于上限的行金额合计
  `19_999_999_999_999_999.98`）可能合法地超过单个存储列容量。

## 3. 护栏口径

| 项 | 口径 |
|---|---|
| 明细行新增 / 修改 | 授权 / 归属 / 状态 / 类型之后，**任何已跟踪写入之前**（`Add` 之前、字段赋值之前）校验数量 / 单价 / 净重 / 毛重（18,4）与**服务端计算**的行金额（18,2）；越界或乘法溢出受控拒绝（`InvalidParameter`），零写入 / 零改写 |
| 行金额计算 | `ComputeLineAmount` = `SafeMultiply(数量, 单价)` → `CurrencyAmountRules.RoundAmount(币种)` → `EnsureLineAmountRepresentable`（18,2）；CLR 溢出转换为受控业务错误，绝不以未处理异常崩溃 |
| 零值 / 可空语义 | 数量必须大于 0；单价 / 净重 / 毛重非负；未登记的箱数 / 净重 / 毛重保持 `null`（**不臆造为 0**）；装箱单行单价恒为 0、行金额恒为 0（零值照常放行） |
| 单证表头金额 | `Create` 在落库前、`Update` 在**锁定父行 + 生命周期判定之后**校验拟议 `Amount` 可在 `DECIMAL(18,2)` 内表示；越界受控拒绝且零单证 / 逐字段不变（含审计时间戳） |
| 来源快照生成 | `NormalizeQuantity` / `NormalizeUnitPrice` 增加 18,4 判定（越界返回 `null`，由生成入口 fail closed 拒绝）；装柜清单毛重经 `IsGrossWeightRepresentable` 判定，越界即受控拒绝（**绝不静默留空丢弃来源重量**） |
| 整批原子性 | 生成入口在**编号预约（`EnsureUniqueDocNoAsync`）与 `Db.Add` 之前**调用 `TradeDocumentGenerationMutationRules.EnsurePersistedBatchRepresentable(draft, lines)`：表头金额 + **每一行**明细的数量 / 单价 / 净重 / 毛重 / 行金额全部通过后才预约编号与写入；任一不可表示即整批拒绝，**零单证 / 零明细 / 零编号占用 / 零来源改写** |
| 只读聚合 | 清单读取（`TradeDocumentItemService.ListAsync`）的行金额合计与箱数 / 重量合计是只读派生展示，**刻意不设单列上界**；历史越界行照常可读，**不做静默修复 / 回填** |
| 不改写项 | 不改币种 / 取整口径、来源资格、生命周期、锁序、授权与审计；不新增表 / 列 / 索引 / 迁移 / 权限 / 菜单；不做生产运维与不可逆数据操作 |

## 4. 实现

- `src/ERP.Application/Services/TradeDocumentItemRules.cs`
  - 新增实际列精度常量（`ItemScalarScale` / `LineAmountScale` / `HeaderAmountScale` 与对应 precision / 字段名）；
  - 新增 `IsItemScalarRepresentable` / `IsLineAmountRepresentable` / `IsHeaderAmountRepresentable`
    与 `EnsureItemScalarRepresentable` / `EnsureLineAmountRepresentable` / `EnsureHeaderAmountRepresentable`
    （复用 `FinancePersistedAmountRules`，纯判定不改写值）；
  - 新增 `EnsurePersistedLineRepresentable(line)`（单行持久化标量整批判定，可空重量保持 `null`）；
  - `ComputeLineAmount` 改为「受控乘法 → 既有币种取整 → 18,2 区间判定」；
  - `NormalizeQuantity` / `NormalizeUnitPrice` / `NormalizeWeight` 追加 18,4 区间判定；`RuleText` 同步口径。
- `src/ERP.Application/Services/TradeDocumentLineSnapshotRules.cs`
  - `NormalizeQuantity` / `NormalizeUnitPrice` 追加 18,4 判定（越界返回 `null`）；
  - 新增 `IsGrossWeightRepresentable(roundedWeight)`；`BuildFromLoadingList` 对越界来源毛重 fail closed；
  - `FailInvalidNumber` / 新增 `FailWeightOutOfRange` 文案说明实际列精度与「不截断 / 不臆造」。
- `src/ERP.Application/Services/TradeDocumentGenerationMutationRules.cs`
  - 新增 `EnsurePersistedBatchRepresentable(draft, lines)`（编号预约与写入之前的整批持久化标量复核）。
- `src/ERP.Api/Controllers/TradeDocumentGeneration.cs`
  - `BuildDocumentAsync`：先构造明细行并做整批判定，**再**预约唯一编号与 `Db.Add`（校验先于编号与写入）。
- `src/ERP.Api/Controllers/TradeDocumentController.cs`
  - `Create` / `Update`：拟议表头金额 `EnsureHeaderAmountRepresentable` 先于 `Service.CreateAsync` / `Service.UpdateAsync`。
- `src/ERP.Application/Services/TradeDocumentItemService.cs`：仅类文档与注释（读取侧只读聚合合计不设单列上限）。
- `src/ERP.Application/Services/FinancePersistedAmountRules.cs`：**未改动**（复用既有标量边界规则）。

## 5. 不改写项

- 不新增 / 不修改任何表、列、索引、schema、迁移或种子数据；不新增权限、菜单、角色、用户授权，不做自动主数据变更；
- 不改币种口径、取整口径（`AwayFromZero`、零小数币种）、来源资格、历史差异提示、生命周期与确定性锁序、
  受控错误码与审计；
- 不把单列边界施加到 `CurrencyAmountRules.RoundAmount`、只读权威比对或只读聚合合计；
- 已存储越界历史值**不做**静默修复 / 回填；历史读取 / 作废 / 取消与既有撤销语义保持不变；
- 不改来源销售订单 / 装柜清单、商品资料、库存、发票、退税、费用或财务记录。

## 6. 测试

- 单元测试 `src/ERP.UnitTests/TradeDocumentPersistedNumericBoundsTests.cs`（内存库 + 真实 HTTP 身份 + 真实主数据）：
  实际 EF 列精度与规则常量同源、精确合法边界与小数位口径、行金额受控乘法 / 币种取整 / 溢出与越界受控拒绝、
  明细行新增 / 修改越界拒绝且零写入 / 零改写、装箱单零价格与可空重量语义、单证表头新增 / 修改越界拒绝、
  清单读取只读合计不被单列上界限制、来源快照精度规则与生成整批原子拒绝 / 合法落库与来源链接保持、
  控制器 / 规则源码契约与文档同源。
- 集成测试 `src/ERP.IntegrationTests/TradeDocumentPersistedNumericBoundsSqlServerTests.cs`（NEWERP_AUTOTEST 护栏，真实 SQL Server）：
  真实 `INFORMATION_SCHEMA` 列精度、边界值与 4 / 2 位小数原样往返、原始 EF 越界写入被真实列 / 驱动拒绝、
  SQL Server 自身算术溢出（8115）、明细行 / 表头金额越界拒绝且零写入、生成整批拒绝且零单证 / 零明细、
  合法生成落库与非专用目标库在访问前被拒绝。
- 既有单证回归测试照常通过（`TradeDocumentItemTests`、`TradeDocumentLineSnapshotTests`、
  `TradeDocumentGenerationTests`、`TradeDocumentGenerationMutationTests`、`TradeDocumentMutationTests`、
  `TradeDocumentAuthorizationTests`、`TradeDocumentPrintTests` 与对应 SQL 集成回归）。

## 7. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，
  且 `Integrated Security=true`；护栏在**任何数据库访问之前**校验失败即中止；
- 每次运行创建**全新的 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 不读取 `appsettings` / `.env` / 生产凭据；连接串只来自进程环境变量或专用 localdb 默认值；
- 测试种子一律使用 SQL Server 自增主键（不显式指定 Id），并复用种子数据中的既有菜单 / 单据号规则。

## 8. 环境证据

- 单元测试与真实 SQL 集成测试的输出 / TRX 记录于 `.ai/logs/ERP-518/`（保留原始失败日志）；
- 说明：本文件的完成只代表 Release 构建（含分析器 / 警告即错误）与单元测试通过，**构建完成不代表阶段验收完成**；
  真实 SQL 集成场景与最终浏览器验收由调度器 / 验收运行器按各自门禁执行。

