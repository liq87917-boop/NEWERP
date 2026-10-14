# 定金申请单 / 货款申请单汇率存储边界护栏（ERP-517）

## 1. 目标

让既有定金申请单（`FinanceDepositApply`）与货款申请单（`FinancePaymentApply`）的**写入与承诺路径**在**单号预约与
任何表头 / 来源 / 状态写入之前**，把「已按既有 6 位小数归一化的汇率」收敛到该汇率列的**实际存储精度**内：

1. 两个申请单的 `ExchangeRate` 列在 EF 模型中**未**显式 `HasPrecision`，按 EF SQL Server 默认精度落库为
   `DECIMAL(18,2)`（16 位整数 + 2 位小数）；
2. `FinanceDepositApplyLifecycleRules.NormalizeExchangeRate` /
   `FinancePaymentApplyLifecycleRules.NormalizeExchangeRate` 此前只做「原始值大于 0 → 按 6 位小数取整」，
   **没有**任何存储边界判定：极小正汇率（如 `0.0000001`）取整后为 `0`，正汇率 `0.001` / `1.234568` 会被真实列
   分别静默收敛为 `0.00` / `1.23`（静默改写小数位），极大汇率（如 `1e16`、`decimal.MaxValue`）会在写入时溢出 ——
   这些都必须在**触及单号与任何写入之前**受控拒绝；
3. 提交 / 审核经既有持久化字段复核（`EnsurePersistedApplyConsistentAsync`）与既有行锁自动继承同一汇率口径。

只补齐**汇率存储边界**，**不**改既有 6 位小数归一化口径、币种 / 金额口径、单据流转、授权、来源资格、聚合报表合计与
审计；**不**做任何汇率换算、**不**推断汇率、**不**新增 schema / 迁移。

## 2. 精度口径（与实际 EF 存储同源）

| 列 | 实际存储 | 规则常量（`FinancePersistedAmountRules`） |
|---|---|---|
| `FinanceDepositApply.ExchangeRate` | `DECIMAL(18,2)`（EF 默认，模型未显式声明） | `DecimalPrecision = 18`、`DecimalScale = 2` |
| `FinancePaymentApply.ExchangeRate` | `DECIMAL(18,2)`（同上） | `MaxDecimal18Scale2 = 9_999_999_999_999_999.99`、`MinDecimal18Scale2 = -9_999_999_999_999_999.99` |

- 边界常量与真实列类型逐字同源：单元测试以 SQL Server 提供程序**仅构建模型**断言
  `IProperty.GetColumnType() == "decimal(18,2)"`（且 `GetPrecision()/GetScale()` 为 `null`，即未显式声明）；
  集成测试再以真实 `INFORMATION_SCHEMA.COLUMNS`（`decimal(18,2)`）与 SQL Server 自身「算术溢出（错误号 8115）」
  双重确认；
- 归一化口径**原样**复用既有 `ExchangeRateDecimals = 6`（`MidpointRounding.AwayFromZero`），
  `FinancePersistedAmountRules.SafeRoundRate` 只负责把 CLR 刻度 / 范围溢出（`OverflowException`）转换为受控业务错误；
  守卫只在**归一化之后**追加「仍大于 0 且可在 `DECIMAL(18,2)` 内**原样**表示」判定；
- 守卫**只**作用于「单个文档汇率标量」：绝不用于 `AuthoritativeExchangeRate`（只读历史比对）、聚合报表合计或任何
  只读权威口径，也绝不用于销售订单 / 报价单 / 形式发票 / 费用单等其他模块的汇率列。

## 3. 护栏口径

| 项 | 口径 |
|---|---|
| 校验时点 | **创建**：授权 / 客户 / 来源之前先归一化并判定，且**单号预约与任何已跟踪写入之前**；**修改**：单据行锁内、任何字段改写之前；**提交 / 审核**：同一事务的既有行锁内、置状态之前 |
| 既有语义保留 | 原始值 `<= 0` 仍按既有「汇率必须大于 0」拒绝；可表示汇率仍按 6 位小数（`AwayFromZero`）归一化并返回 |
| 归一化为 0 | 极小正汇率（如 `0.0000001`）取整后为 `0`：受控拒绝（`InvalidParameter`），**绝不静默归零** |
| 去精度 / 静默改写 | 真实列会收敛为 `0.00`（`0.001`）或改写小数位（`1.234568`、`7.123456`）的正汇率：受控拒绝；**绝不**静默接受更粗的小数位 |
| 越界 / 溢出 | `MaxDecimal18Scale2 + 0.01`、`1e16`、`decimal.MaxValue`：受控拒绝（文案含 `DECIMAL(18,2)`），且 CLR 归一化溢出同样转为受控错误 |
| 精确放行 | 恰好 `9_999_999_999_999_999.99`、`7.2`、`2.5`、极小但可表示的 `0.01` 正常落库并原样往返 |
| 提交 / 审核复核 | 在两个申请单既有行锁内复核**已持久化**汇率（`EnsurePersistedApplyConsistentAsync` 的同一调用点）；非法历史快照**只被拒绝、绝不静默修复 / 改写** |
| 拒绝语义 | 被拒请求**零单号消耗**、零表头 / 来源字段 / 状态 / 审计变化；修改被拒时既有单据逐字段不变 |
| 撤销语义 | **历史读取 / 取消 / 删除 / 作废不调用本护栏**，不新增任何草稿有效性闸门；`AuthoritativeExchangeRate` 读 / 比对口径不变 |
| 保留项 | 实时身份 / 菜单 / 客户数据范围、真实可用客户、受支持币种、来源销售订单资格与归属、引用付款单护栏、确定性锁序、受控错误码与审计一律原样保留 |

## 4. 实现

- `src/ERP.Application/Services/FinancePersistedAmountRules.cs`
  - 新增 `ExchangeRateOverflowText` 常量；
  - 新增 `SafeRoundRate(decimal rawExchangeRate, int scale, string rateLabel)`（既有归一化 + CLR 溢出受控转换）；
  - 新增 `IsRateRepresentable(decimal)` 与 `EnsureRateRepresentable(decimal normalizedExchangeRate, string rateLabel)`
    （纯判定，原样返回入参，绝不取整 / 截断 / 换算）。
- `src/ERP.Application/Services/FinanceDepositApplyLifecycleRules.cs` /
  `src/ERP.Application/Services/FinancePaymentApplyLifecycleRules.cs`
  - `NormalizeExchangeRate`：既有「大于 0 + 6 位小数归一化」之后追加「归一化结果仍大于 0」与
    `EnsureRateRepresentable` 存储边界判定（创建 / 修改 / 提交 / 审核经既有调用点自动生效）；
  - `AuthoritativeExchangeRate` **不改动**（只读历史比对口径）；
  - `RuleText` / 类文档 / 持久化复核文档同步既有权威口径文案。
- `src/ERP.Api/Controllers/FinanceApplyControllers.cs`
  - 仅类文档说明；两个申请单控制器创建 / 修改 / 提交 / 审核的调用点**不变**
    （已通过既有归一化与持久化复核覆盖，且都在单号预约与任何写入之前）。

## 5. 不改写项

- 不新增 / 不修改任何表、列、索引、schema、迁移或种子数据；不新增权限、菜单、角色、用户授权，不做自动主数据变更；
- 不改既有 6 位小数归一化口径、币种 / 金额取整口径、单据流转、授权、来源资格 / 容量、确定性锁序、受控错误码与审计；
- 不做任何隐式汇率换算、不推断汇率、不把单列边界施加到 `AuthoritativeExchangeRate`、聚合报表合计或其他模块汇率列；
- 不静默修复历史快照；历史读取 / 取消 / 删除与既有撤销语义原样保留；不做生产运维与不可逆数据操作。

## 6. 测试

- 单元测试 `src/ERP.UnitTests/FinanceApplyPersistedRateTests.cs`（内存库 + 真实 HTTP 身份）：
  汇率常量与可表示判定、`SafeRoundRate` 溢出受控、极小正汇率归一化为 0 拒绝、真实列会收敛为 `0.00` / 改写小数位的
  正汇率拒绝、精确上限 / 极大值边界、6 位小数归一化与 `AuthoritativeExchangeRate` 口径不变、实际 EF 汇率列精度同源、
  创建 / 修改拒绝（零写入、零占号、逐字段不变）、提交 / 审核复核已持久化非法汇率被拒且状态 / 审计不变、取消语义不变、
  合法汇率（含关联来源与未关联历史场景）全生命周期放行。
- 集成测试 `src/ERP.IntegrationTests/FinanceApplyPersistedRateSqlServerTests.cs`（NEWERP_AUTOTEST 护栏，真实 SQL Server）：
  真实汇率列精度（含 `INFORMATION_SCHEMA`）与可表示汇率原样往返、真实列对 `0.001` / `1.234568` 的物理收敛与对越界值的
  算术溢出（8115）、创建 / 修改越界拒绝且零写入 / 零占号（同一受权控制器随后合法创建成功）、关联 / 未关联「创建 → 提交 →
  审核」生命周期、提交 / 审核拒绝非法历史快照且不改写、取消不受非法历史汇率影响、非专用目标库在访问前被拒绝。
- 既有定金 / 货款申请生命周期、来源、并发与取消回归测试全部照常通过（`FinanceDepositApplyLifecycleTests`、
  `FinancePaymentApplyLifecycleTests`、`DepositApplySalesOrderSourceTests`、`PaymentApplySalesOrderSourceTests`、
  `FinanceApplyCanonicalEntryTests` 及对应真实 SQL 回归）。

## 7. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，
  且 `Integrated Security=true`；护栏在**任何数据库访问之前**校验失败即中止；
- 每次运行创建**全新的 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 不读取 `appsettings` / `.env` / 生产凭据；连接串只来自进程环境变量或专用 localdb 默认值；
- 测试种子一律使用 SQL Server 自增主键（不显式指定 Id），并复用种子数据中的既有菜单 / 单据号规则。

## 8. 环境证据

- 单元测试与真实 SQL 集成测试的输出 / TRX 记录于 `.ai/logs/ERP-517/`（保留原始失败日志）；
- 说明：本文件的完成只代表 Release 构建（含分析器 / 警告即错误）与单元测试通过，**构建完成不代表阶段验收完成**；
  真实 SQL 集成场景与最终验收由调度器 / 验收运行器按各自门禁执行。
