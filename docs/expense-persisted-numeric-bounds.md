# 费用单与装柜费用分摊标量金额实际列精度护栏（ERP-512）

## 1. 目标

让既有费用单模块的**写入（费用 CRUD）与派生写入（传统「拼柜分摊」生成、ERP-042 分摊批次预览 / 生成）**
在**单号预约与任何 `Db.Add` 之前**，把「按既有口径取整后的单个标量金额」收敛到该金额列的**实际存储精度**内：

1. `ExpenseAuthorizationRules.NormalizeAmount` / `NormalizeRate` 此前只做「按既有 2 位精度取整 + 大于 0」，
   **没有**任何标量存储上界：极大正金额 / 极大汇率（例如 `1e16`、`9_999_999_999_999_999.995` 取整进位后的 `1e16`、
   `decimal.MaxValue`）能通过应用层检查，却会在写入 SQL 时被截断 / 溢出，或被驱动直接拒绝；
2. `EnsureProposedExpenseValidAsync` 此前只校验拟议 `Amount` / `ExchangeRate`，**没有**校验客户端同样可以持久化的
   `AmountCny` / `TaxRate` / `TaxAmount` / `AllocationRatio` / `AllocatedAmount` 标量；
3. `ExpenseBillController` 的传统分摊在 `Calculate` 之后**内联**计算 `AmountCny = 分摊金额 × 汇率`，
   `ContainerExpenseAllocationRules.ConvertToCny` 与批次生成的服务端逐行金额同样可能超出
   `FinanceExpense.AmountCny` / `FinanceExpenseAllocationLine.AllocatedAmountCny` 的实际列容量（并可能触发 CLR 乘法溢出）；
4. 批次预览 / 生成此前没有复核**已存储来源费用**的金额 / 汇率 / 折人民币金额是否仍可在实际列精度内表示，
   也没有复核逐行派生的基数值 / 比例 / 分摊金额 / 折人民币金额。

只补齐**标量存储边界与派生金额的存储边界**，**不**改币种 / 取整口径、单据流转、授权、来源资格、分摊余差与比例算法、
只读聚合报表合计与审计。

## 2. 精度口径（与实际 EF 存储同源）

| 列 | 实际存储 | 规则常量（`FinancePersistedAmountRules`） |
|---|---|---|
| `FinanceExpense.Amount` / `AmountCny` / `TaxRate` / `TaxAmount` / `AllocationRatio` / `AllocatedAmount` / `ExchangeRate` | `DECIMAL(18,2)`（EF 默认精度，模型未显式声明） | `DecimalPrecision = 18`、`DecimalScale = 2`、`MaxDecimal18Scale2 = 9_999_999_999_999_999.99` |
| `FinanceExpenseAllocationBatch.SourceAmount` / `AllocatedTotal` | `DECIMAL(18,2)`（模型显式 `HasPrecision(18,2)`） | 同上 |
| `FinanceExpenseAllocationBatch.ExchangeRate` | `DECIMAL(18,6)`（模型显式 `HasPrecision(18,6)`） | `DecimalScale6 = 6`、`MaxDecimal18Scale6 = 999_999_999_999.999999` |
| `FinanceExpenseAllocationLine.BasisValue` / `Ratio` | `DECIMAL(18,4)`（模型显式 `HasPrecision(18,4)`） | `DecimalScale4 = 4`、`MaxDecimal18Scale4 = 99_999_999_999_999.9999` |
| `FinanceExpenseAllocationLine.AllocatedAmount` / `AllocatedAmountCny` | `DECIMAL(18,2)`（模型显式 `HasPrecision(18,2)`） | 同 `DECIMAL(18,2)` |

- 边界常量与真实列类型逐字同源：单元测试以 SQL Server 提供程序**仅构建模型**断言
  `IProperty.GetPrecision()` / `GetScale()` / `GetColumnType()`；集成测试再以真实 `INFORMATION_SCHEMA.COLUMNS`
  与 SQL Server 自身「算术溢出（错误号 8115）」双重确认；
- 判定顺序与 SQL Server 一致：按目标列小数位取整（`MidpointRounding.AwayFromZero`）后再判上界，
  因此「取整进位后越过上限」的值同样被拒绝，而取整后仍在上界内的值照常放行（**绝不静默截断**）；
- **复用**既有 `FinancePersistedAmountRules`（ERP-508 起的唯一标量边界规则，系统内不允许出现第二套）；
- 币种取整口径**原样**复用既有 `CurrencyAmountRules.RoundAmount`（`AwayFromZero`，零小数币种
  `JPY / KRW / VND / IDR` 为 0 位、其余 2 位），守卫只在**取整之后**追加区间判定（`AmountDecimals = 2` 与既有
  「费用金额 / 汇率固定 2 位小数」口径一致）；
- 守卫**只**作用于「单个标量金额 / 汇率」：绝不用于 `CurrencyAmountRules.RoundAmount` 本身、只读权威金额比对或
  动态报表聚合合计 —— 求和（例如两张各自恰好等于上限的金额合计 `19_999_999_999_999_999.98`）可能合法地超过
  单个存储列容量。

## 3. 护栏口径

| 项 | 口径 |
|---|---|
| 费用 CRUD | 授权 / 客户之后完成；`Amount` / `ExchangeRate` 经既有取整后判定，`AmountCny` / `TaxRate` / `TaxAmount` / `AllocationRatio` / `AllocatedAmount` 按同一实际列精度判定；全部通过后才进入 `Service.CreateAsync` / `UpdateAsync`（**任何已跟踪写入之前**） |
| 零值可选字段 | 税率 / 税额 / 分摊比例 / 分摊金额 / 未传折人民币金额为 `0` 时照常放行（系统不为税率发明任何等式，也不做自动税率反推） |
| 传统分摊 | `Calculate` 是预览与生成的**共用**派生边界：分摊总额、加权比例、分摊金额、折人民币金额（既有 `Math.Round(分摊金额 × 汇率, 2)` 表达式，仅追加溢出与区间判定）全部在单号生成之前预计算并判定；被拒请求**零单号消耗、零写入** |
| 批次预览 / 生成 | `BuildPlanAsync → ComposePlan` 是预览与生成的**共用**计算入口：逐行基数值（18,4）/ 比例（18,4）/ 分摊金额（18,2）/ 折人民币金额（18,2）与表头来源金额（18,2）/ 分摊合计（18,2）/ 汇率（18,6）全部在**模块单号键行锁内、单号预约与 `Db.Add` 之前**判定 |
| 派生折人民币 | `EnsureConvertedToCnyRepresentable` 是写入侧唯一入口（CNY 原样取整、其余币种先受控乘法再判定）；只读派生（分摊证据 / 台账回显）继续沿用纯计算 `ConvertToCny`，**历史读取不受写入边界影响** |
| 已存储来源复核 | `EnsureStoredExpenseScalarsRepresentable` 在 `BuildPlanAsync` 内只读复核来源费用的金额 / 汇率 / 折人民币金额；历史或外部改写的越界值按规则冲突受控拒绝，**绝不静默截断、绝不改写历史行**（更正仍需显式改写或改选来源费用） |
| 乘法 / 求和溢出 | `MultiplyScalar` / `SafeMultiply` 把 CLR 十进制乘法溢出转换为受控业务错误；`Distribute` 先判定每个基准值区间再求和；传统分摊的权重合计溢出同样受控拒绝 —— 绝不以 `OverflowException` 崩溃 |
| 余差与比例算法 | **原样保留**：四舍五入余差仍归基准值最大（并列取参与方 Id 较小者）的参与方，行合计恒等于来源金额、比例合计恒为 100；仅在中间乘积本身溢出 CLR 时改用等价的除法优先顺序（正常取值路径逐位一致） |
| 拒绝语义 | 被拒请求零费用行 / 零批次 / 零分摊行 / 零单号消耗；修改与生成被拒时既有行逐字段（含 `UpdatedAt` / 状态 / 批次留痕）不变 |
| 历史 / 作废语义 | **历史读取 / 作废 / 分摊证据不调用本护栏**，不新增任何草稿有效性闸门；显式作废（状态 0 + 原因）与历史留痕照常可读 |
| 保留项 | 既有实时授权（身份 / 「费用单」（`expense-bill`）菜单 / 客户数据范围）、受支持币种、来源资格、客户与装柜清单归属、确定性锁序（ERP-386）、受控错误码与审计一律原样保留；**不要求任何额外的主数据维护授权** |

## 4. 实现

- `src/ERP.Application/Services/FinancePersistedAmountRules.cs`
  - 新增 `DecimalScale4` / `DecimalScale6` 与 `MaxDecimal18Scale4` / `MaxDecimal18Scale6`（及最小值）常量；
  - 新增 `IsWithinDecimal18(value, scale)` / `EnsureWithinDecimal18(value, scale, label)`（按目标列小数位取整后判上界）；
  - 新增 `SafeMultiply(left, right, label)` 与 `MultiplyScalar(left, right, scale, label)`（CLR 乘法溢出 → 受控拒绝）。
- `src/ERP.Application/Services/ExpenseAuthorizationRules.cs`
  - `NormalizeAmount` / `NormalizeRate`：既有取整 + 正数校验之后追加标量区间判定；
  - `EnsureProposedExpenseValidAsync`：追加 `AmountCny` / `TaxRate` / `TaxAmount` / `AllocationRatio` / `AllocatedAmount` 判定；
  - 新增 `EnsureScalarRepresentable` / `IsScalarRepresentable` / `EnsureAllocateResultRepresentable` /
    `DeriveCnyAmount`（传统分摊折人民币派生 + 边界）与 `EnsureStoredExpenseScalarsRepresentable`（已存储来源只读复核）。
- `src/ERP.Application/Services/ContainerExpenseAllocationRules.cs`
  - `Distribute`：来源金额与基准值区间判定（18,2 / 18,4）、按实际列精度判定比例与分摊金额、余差金额判定；
  - 新增 `ExactShareAmount`（保留既有运算顺序，仅在中间乘积溢出时改用等价顺序）；
  - 新增 `EnsureConvertedToCnyRepresentable`（折人民币派生边界；`ConvertToCny` 保持纯计算供只读复用）。
- `src/ERP.Api/Controllers/ExpenseBillController.cs`
  - `AllocateApply`：在重复检查 / 单号生成 / `Db.Add` 之前预计算全部派生金额（分摊金额 / 比例 / 折人民币金额）；
  - `Calculate`：权重求和溢出防护、分摊总额与逐行派生标量判定（预览与生成共用）；
  - 类文档同步既有权威口径。
- `src/ERP.Application/Services/ContainerExpenseAllocationService.cs`
  - `BuildPlanAsync`：复核已存储来源费用标量；
  - `ComposePlan`：逐行与表头派生标量判定 + 折人民币改用 `EnsureConvertedToCnyRepresentable`（预览与生成共用计划）。
- `src/ERP.Api/Controllers/ExpenseAllocateDtos.cs` / `ContainerExpenseAllocationEvidenceController.cs`：仅文档说明，
  请求形状 / 路由与只读语义**不变**。

## 5. 不改写项

- 不新增 / 不修改任何表、列、索引、schema、迁移或种子数据；不新增权限、菜单、角色、用户授权，不做自动主数据变更；
- 不改币种口径、取整口径（`AwayFromZero`、零小数币种）、汇率语义、来源资格、余差与比例算法、确定性锁序、
  受控错误码与审计；不新增聚合总额上限、不做跨币种合计；
- 不改既有折人民币**赋值表达式**的取整方式：传统分摊仍沿用既有 `Math.Round(分摊金额 × 汇率, 2)`，
  ERP-042 批次仍沿用既有 `ConvertToCny`（<c>AwayFromZero</c>）—— 本次只在两者之上追加溢出与实际列精度判定；
- 不把单列边界施加到 `CurrencyAmountRules.RoundAmount`、只读权威金额比对或聚合报表合计；
- 已存储越界历史值**不做**静默修复 / 回填；更正仍走显式改写或先作废批次（ERP-042）的既有路径；
- 不改装柜清单 / 明细 / 参与方 / 单证 / 库存 / 采购订单 / 销售订单，不做生产运维与不可逆数据操作。

## 6. 测试

- 单元测试 `src/ERP.UnitTests/ExpensePersistedNumericBoundsTests.cs`（内存库 + 真实 HTTP 身份）：
  标量常量与 2 / 4 / 6 三种实际列精度判定、实际 EF 列精度与规则常量同源、受控乘法（合法乘积 / 乘积越界 / CLR 溢出 /
  全局取整无单列上限）、费用规则的极小正数与取整进位越界、CRUD 越界拒绝（零写入 / 零改写）与零值可选字段 + 边界金额放行、
  传统分摊的预览 / 生成同边界（乘法溢出、总额越界、零单号消耗）与合法金额 / 单号 / 折人民币口径、
  批次预览 / 生成的越界来源 / 乘法溢出 / 基准值越界（零批次 / 零分摊行 / 零费用行）与合法生成的余差 / 比例 / 行合计算术、
  历史越界行仍可读、纯规则的基准值与来源金额边界、以及接线与文档同源契约。
- 集成测试 `src/ERP.IntegrationTests/ExpensePersistedNumericBoundsSqlServerTests.cs`（NEWERP_AUTOTEST 护栏，真实 SQL Server）：
  真实列精度（含 `INFORMATION_SCHEMA`）与边界值原样往返、原始 EF 写入越界被真实列 / 驱动拒绝、
  SQL Server 自身转换越界报算术溢出（8115）、费用 CRUD 越界拒绝且零写入（同一受权控制器随后合法创建成功）、
  传统分摊越界拒绝且零占号、批次生成的越界来源 / 基准值拒绝且零批次 / 零分摊行、合法「来源 → 分摊生成」流转与折人民币落库、
  非专用目标库在访问前被拒绝。
- 既有费用 / 分摊 / 授权 / 并发回归测试照常通过（`ExpenseAuthorizationTests`、`ContainerExpenseAllocationTests`、
  `ExpenseAllocationConcurrencyTests`、`ContainerExpenseAllocationEvidenceTests`、
  `ExpenseAuthorizationSqlServerTests`、`ExpenseAllocationConcurrencySqlServerTests` 等）。

## 7. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，
  且 `Integrated Security=true`；护栏在**任何数据库访问之前**校验失败即中止；
- 每次运行创建**全新的 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 不读取 `appsettings` / `.env` / 生产凭据；连接串只来自进程环境变量或专用 localdb 默认值；
- 测试种子一律使用 SQL Server 自增主键（不显式指定 Id），并复用种子数据中的既有菜单 / 单据号规则。

## 8. 环境证据

- 单元测试与真实 SQL 集成测试的输出 / TRX 记录于 `.ai/logs/ERP-512/`（保留原始失败日志）；
- 说明：本文件的完成只代表 Release 构建（含分析器 / 警告即错误）与单元测试通过，**构建完成不代表阶段验收完成**；
  真实 SQL 集成场景与最终浏览器验收由调度器 / 验收运行器按各自门禁执行。
