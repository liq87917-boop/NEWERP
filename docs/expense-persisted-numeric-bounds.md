# 费用单与装柜费用分摊标量声明列精度护栏（ERP-512 / ERP-538）

## 1. 目标

让既有费用单模块的**写入（费用 CRUD）与派生写入（传统「拼柜分摊」生成、ERP-042 分摊批次预览 / 生成）**
在**单号预约与任何 `Db.Add` 之前**，把「单个标量金额 / 汇率」收敛到该列的**声明存储精度**内。
ERP-538 进一步把**客户端可持久化标量**从「按 2 位小数取整」改为**按声明列精度逐位原样**：

1. 权威升级脚本 `db_owner.FinanceExpenses` 把 `Amount` / `AmountCny` / `TaxRate` / `TaxAmount` /
   `AllocationRatio` / `AllocatedAmount` 声明为 `DECIMAL(18,4)`、`ExchangeRate` 声明为 `DECIMAL(18,6)`
   （`src/ERP.Infrastructure/Data/SchemaUpgrader.cs`），但 ERP-538 之前 EF 模型**未**为这七列显式声明精度，
   因此按提供程序默认 `decimal(18,2)` 落库：真实可表示的 3 / 4 位小数金额与 3~6 位小数汇率被静默取整到 2 位；
2. `ExpenseAuthorizationRules.NormalizeAmount` / `NormalizeRate` 此前只做「按既有 2 位精度取整 + 大于 0」，
   把真实可存储的 4 位小数金额 / 6 位小数汇率**取整改写**；极大正金额 / 极大汇率（例如 `1e16`、
   `9_999_999_999_999_999.995` 取整进位后的 `1e16`、`decimal.MaxValue`）能通过应用层检查，却会在写入 SQL 时
   被截断 / 溢出，或被驱动直接拒绝；
3. `EnsureProposedExpenseValidAsync` 此前只校验拟议 `Amount` / `ExchangeRate`，**没有**校验客户端同样可以持久化的
   `AmountCny` / `TaxRate` / `TaxAmount` / `AllocationRatio` / `AllocatedAmount` 标量；
4. `ExpenseBillController` 的传统分摊在 `Calculate` 之后**内联**计算 `AmountCny = 分摊金额 × 汇率`，
   `ContainerExpenseAllocationRules.ConvertToCny` 与批次生成的服务端逐行金额同样可能超出
   `FinanceExpense.AmountCny` / `FinanceExpenseAllocationLine.AllocatedAmountCny` 的实际列容量（并可能触发 CLR 乘法溢出）；
5. 批次预览 / 生成此前没有复核**已存储来源费用**的金额 / 汇率 / 折人民币金额是否仍可在声明列精度内表示，
   也没有复核逐行派生的基数值 / 比例 / 分摊金额 / 折人民币金额。

只对齐**EF 映射与写入护栏的声明列精度**并补齐**标量与派生金额的存储边界**，**不**改币种 / 取整口径、
单据流转、授权、来源资格、分摊余差与比例算法、只读聚合报表合计与审计。

## 2. 精度口径（与实际 EF 存储同源）

| 列 | 实际存储 | 规则常量（`FinancePersistedAmountRules`） |
|---|---|---|
| `FinanceExpense.Amount` / `AmountCny` / `TaxRate` / `TaxAmount` / `AllocationRatio` / `AllocatedAmount` | `DECIMAL(18,4)`（ERP-538 EF 显式 `HasPrecision(18,4)`，与建表脚本同源） | `DecimalScale4 = 4`、`MaxDecimal18Scale4 = 99_999_999_999_999.9999` |
| `FinanceExpense.ExchangeRate` | `DECIMAL(18,6)`（ERP-538 EF 显式 `HasPrecision(18,6)`，与建表脚本同源） | `DecimalScale6 = 6`、`MaxDecimal18Scale6 = 999_999_999_999.999999` |
| `FinanceExpenseAllocationBatch.SourceAmount` / `AllocatedTotal` | `DECIMAL(18,2)`（模型显式 `HasPrecision(18,2)`；**批次表结构不变**） | `DecimalPrecision = 18`、`DecimalScale = 2`、`MaxDecimal18Scale2 = 9_999_999_999_999_999.99` |
| `FinanceExpenseAllocationBatch.ExchangeRate` | `DECIMAL(18,6)`（模型显式 `HasPrecision(18,6)`） | `DecimalScale6 = 6`、`MaxDecimal18Scale6 = 999_999_999_999.999999` |
| `FinanceExpenseAllocationLine.BasisValue` / `Ratio` | `DECIMAL(18,4)`（模型显式 `HasPrecision(18,4)`） | `DecimalScale4 = 4`、`MaxDecimal18Scale4 = 99_999_999_999_999.9999` |
| `FinanceExpenseAllocationLine.AllocatedAmount` / `AllocatedAmountCny` | `DECIMAL(18,2)`（模型显式 `HasPrecision(18,2)`） | 同 `DECIMAL(18,2)` |

- 边界常量与真实列类型逐字同源：单元测试以 SQL Server 提供程序**仅构建模型**断言
  `IProperty.GetPrecision()` / `GetScale()` / `GetColumnType()`；集成测试再以真实 `INFORMATION_SCHEMA.COLUMNS`
  与 SQL Server 自身「算术溢出（错误号 8115）」双重确认；
- **客户端标量**（金额 / 折人民币金额 / 税率 / 税额 / 分摊比例 / 分摊金额 / 汇率）按声明列精度**逐位原样**判定：
  `decimal.Round(value, scale, MidpointRounding.AwayFromZero) == value` 且值在列区间内才放行 —— 可表示的值
  **原样落库往返**，第 5 位小数（金额类）/ 第 7 位小数（汇率）与量级溢出**一律受控拒绝**（绝不静默截断 / 取整）；
- **派生值**（传统分摊的逐行分摊金额 / 折人民币金额、ERP-042 的逐行分摊金额 / 折人民币金额）继续沿用既有货币精度
  取整之后再做声明列区间判定，因此「预览所示即生成结果」在存储边界上同样成立；
- **复用**既有 `FinancePersistedAmountRules`（ERP-508 起的唯一标量边界规则，系统内不允许出现第二套）；
- 币种取整口径**原样**复用既有 `CurrencyAmountRules.RoundAmount`（`AwayFromZero`，零小数币种
  `JPY / KRW / VND / IDR` 为 0 位、其余 2 位）；
- 守卫**只**作用于「单个标量金额 / 汇率」：绝不用于 `CurrencyAmountRules.RoundAmount` 本身、只读权威金额比对或
  动态报表聚合合计 —— 求和（例如两张各自恰好等于上限的金额合计 `19_999_999_999_999_999.98`）可能合法地超过
  单个存储列容量。

## 3. 护栏口径

| 项 | 口径 |
|---|---|
| 声明列精度 | **客户端标量不取整**：`Amount` / `AmountCny` / `TaxRate` / `TaxAmount` / `AllocationRatio` / `AllocatedAmount` 必须**逐位原样**落在 `DECIMAL(18,4)` 内（≤ 4 位小数且绝对值 ≤ `99_999_999_999_999.9999`），`ExchangeRate` 落在 `DECIMAL(18,6)` 内；第 5 位小数（金额类）/ 第 7 位小数（汇率）与量级溢出**一律受控拒绝**，绝不静默取整 |
| 费用 CRUD | 授权 / 客户之后完成；`Amount`（> 0）/ `ExchangeRate`（> 0）按声明列精度逐位原样判定，`AmountCny` / `TaxRate` / `TaxAmount` / `AllocationRatio` / `AllocatedAmount` **非负**（零值照常放行）且同精度判定；全部通过后才进入 `Service.CreateAsync` / `UpdateAsync`（**任何已跟踪写入之前**），被拒请求零写入 / 零改写 |
| 零值可选字段 | 税率 / 税额 / 分摊比例 / 分摊金额 / 未传折人民币金额为 `0` 时照常放行（系统不为税率发明任何等式，也不做自动税率反推）；**负数一律拒绝** |
| 传统分摊 | `Calculate` 是预览与生成的**共用**派生边界：分摊总额按声明列精度（18,4）逐位原样判定，加权比例、分摊金额、折人民币金额（既有 `Math.Round(分摊金额 × 汇率, 2)` 表达式，仅追加溢出与区间判定）全部在单号生成之前预计算并判定；被拒请求**零单号消耗、零写入** |
| 批次预览 / 生成 | `BuildPlanAsync → ComposePlan` 是预览与生成的**共用**计算入口：逐行基数值（18,4）/ 比例（18,4）/ 分摊金额（18,2）/ 折人民币金额（18,2）与表头来源金额（18,2）/ 分摊合计（18,2）/ 汇率（18,6）全部在**模块单号键行锁内、单号预约与 `Db.Add` 之前**判定 |
| 派生折人民币 | `EnsureConvertedToCnyRepresentable` 是写入侧唯一入口（CNY 原样取整、其余币种先受控乘法再判定）；只读派生（分摊证据 / 台账回显）继续沿用纯计算 `ConvertToCny`，**历史读取不受写入边界影响** |
| 已存储来源复核 | `EnsureStoredExpenseScalarsRepresentable` 在 `BuildPlanAsync` 内只读复核来源费用的金额（18,4）/ 汇率（18,6）/ 折人民币金额（18,4）；历史或外部改写的过精度 / 越界值按规则冲突受控拒绝，**绝不静默截断、绝不改写历史行**（更正仍需显式改写或改选来源费用） |
| 乘法 / 求和溢出 | `MultiplyScalar` / `SafeMultiply` 把 CLR 十进制乘法溢出转换为受控业务错误；`Distribute` 先判定每个基准值区间再求和；传统分摊的权重合计溢出同样受控拒绝 —— 绝不以 `OverflowException` 崩溃 |
| 余差与比例算法 | **原样保留**：四舍五入余差仍归基准值最大（并列取参与方 Id 较小者）的参与方，行合计恒等于来源金额、比例合计恒为 100；仅在中间乘积本身溢出 CLR 时改用等价的除法优先顺序（正常取值路径逐位一致） |
| 拒绝语义 | 被拒请求零费用行 / 零批次 / 零分摊行 / 零单号消耗；修改与生成被拒时既有行逐字段（含 `UpdatedAt` / 状态 / 批次留痕）不变 |
| 历史 / 作废语义 | **历史读取 / 作废 / 分摊证据不调用本护栏**，不新增任何草稿有效性闸门；显式作废（状态 0 + 原因）与历史留痕照常可读 |
| 保留项 | 既有实时授权（身份 / 「费用单」（`expense-bill`）菜单 / 客户数据范围）、受支持币种、来源资格、客户与装柜清单归属、确定性锁序（ERP-386）、受控错误码与审计一律原样保留；**不要求任何额外的主数据维护授权** |

## 4. 实现

- `src/ERP.Infrastructure/Data/ErpDbContext.Config.cs`（ERP-538）
  - 为 `FinanceExpense` 的七个标量列显式声明精度：`Amount` / `AmountCny` / `TaxRate` / `TaxAmount` /
    `AllocationRatio` / `AllocatedAmount` 为 `HasPrecision(18, 4)`、`ExchangeRate` 为 `HasPrecision(18, 6)` ——
    只对齐映射精度，**不新增表 / 列 / 索引，不改任何 DDL 与批次表结构**。
- `src/ERP.Application/Services/FinancePersistedAmountRules.cs`
  - 新增 `DecimalScale4` / `DecimalScale6` 与 `MaxDecimal18Scale4` / `MaxDecimal18Scale6`（及最小值）常量；
  - 新增 `IsWithinDecimal18(value, scale)` / `EnsureWithinDecimal18(value, scale, label)`（按目标列小数位取整后判上界）；
  - 新增 `SafeMultiply(left, right, label)` 与 `MultiplyScalar(left, right, scale, label)`（CLR 乘法溢出 → 受控拒绝）。
- `src/ERP.Application/Services/ExpenseAuthorizationRules.cs`
  - 新增 `PersistedAmountScale = 4` / `PersistedExchangeRateScale = 6`（与建表脚本 / EF 映射同源）；
  - `NormalizeAmount` / `NormalizeRate`（ERP-538）：改为按声明列精度**逐位原样**判定（不再按 2 位小数取整），
    过精度 / 越界 / 非正一律受控拒绝，可表示值**原样返回**；
  - 新增 `IsWithinDeclaredPrecision` / `EnsureWithinDeclaredPrecision`（逐位原样可存储判定的唯一入口）；
  - `EnsureScalarRepresentable` / `IsScalarRepresentable`（18,4）、新增 `EnsureNonNegativeScalar`（非负 + 18,4）；
  - `EnsureProposedExpenseValidAsync`：`Amount` / `ExchangeRate` 走新口径，`AmountCny` / `TaxRate` / `TaxAmount` /
    `AllocationRatio` / `AllocatedAmount` 走 `EnsureNonNegativeScalar`（**任何已跟踪写入之前**）；
  - `EnsureAllocateResultRepresentable` / `DeriveCnyAmount` 改为 18,4 区间判定（派生取整口径不变）；
  - `EnsureStoredExpenseScalarsRepresentable`：金额 / 折人民币金额按 18,4、汇率按 18,6 只读复核。
- `src/ERP.Application/Services/ContainerExpenseAllocationRules.cs`
  - `Distribute`：来源金额与基准值区间判定（18,2 / 18,4）、按实际列精度判定比例与分摊金额、余差金额判定；
  - 新增 `ExactShareAmount`（保留既有运算顺序，仅在中间乘积溢出时改用等价顺序）；
  - 新增 `EnsureConvertedToCnyRepresentable`（折人民币派生边界；`ConvertToCny` 保持纯计算供只读复用）。
- `src/ERP.Api/Controllers/ExpenseBillController.cs`
  - `AllocateApply`：在重复检查 / 单号生成 / `Db.Add` 之前预计算全部派生金额（分摊金额 / 比例 / 折人民币金额）；
  - `Calculate`：权重求和溢出防护、分摊总额（18,4）与逐行派生标量判定（预览与生成共用）；
  - 类文档同步既有权威口径。
- `src/ERP.Application/Services/ContainerExpenseAllocationService.cs`
  - `BuildPlanAsync`：复核已存储来源费用标量（18,4 / 18,6 / 18,4）；
  - `ComposePlan`：逐行与表头派生标量判定 + 折人民币改用 `EnsureConvertedToCnyRepresentable`（预览与生成共用计划），
    批次表头来源金额 / 分摊合计仍为既有 `DECIMAL(18,2)` 快照列（**批次表结构不变**）。
- `src/ERP.Api/Controllers/ExpenseAllocateDtos.cs`：仅文档说明，请求形状 / 路由与只读语义**不变**。

## 5. 不改写项

- 不新增 / 不修改任何表、列、索引、schema、迁移或种子数据（ERP-538 **只**新增 EF 模型 `HasPrecision` 声明，
  **不**执行任何列类型迁移 / DDL）；不新增权限、菜单、角色、用户授权，不做自动主数据变更；
- 不改币种口径、**派生金额**的取整口径（`AwayFromZero`、零小数币种）、汇率语义、来源资格、余差与比例算法、
  确定性锁序、受控错误码与审计；不新增聚合总额上限、不做跨币种合计；
- 不改既有折人民币**赋值表达式**的取整方式：传统分摊仍沿用既有 `Math.Round(分摊金额 × 汇率, 2)`，
  ERP-042 批次仍沿用既有 `ConvertToCny`（`AwayFromZero`）—— 本次只在两者之上追加溢出与声明列精度判定；
- 不把单列边界施加到 `CurrencyAmountRules.RoundAmount`、只读权威金额比对或聚合报表合计；
- 已存储越界历史值**不做**静默修复 / 回填；更正仍走显式改写或先作废批次（ERP-042）的既有路径；
- 不改装柜清单 / 明细 / 参与方 / 单证 / 库存 / 采购订单 / 销售订单，不做生产运维与不可逆数据操作。

## 6. 测试

- 单元测试 `src/ERP.UnitTests/ExpenseDeclaredPrecisionTests.cs`（ERP-538，内存库 + 真实 HTTP 身份）：
  七个标量的声明列常量与 EF 列精度同源（SQL Server 提供程序**仅构建模型**）、`IsWithinDeclaredPrecision` 的
  4 位 / 6 位小数与上限放行 · 第 5 / 第 7 位小数与量级越界（含「半进位取整恰好落到上限」）拒绝、
  `NormalizeAmount` / `NormalizeRate` / `EnsureNonNegativeScalar` 的原样返回与非正 / 负数 / 过精度 / 越界拒绝、
  真实控制器**逐列**过精度与量级越界拒绝且零写入、声明列上限与 4 位小数金额 · 6 位小数汇率逐位原样往返（新增 + 修改）、
  传统分摊总额按声明列精度判定（预览与生成同边界、零写入）且**派生**金额仍按货币精度取整、
  无实时身份仍 fail closed 且不写入。
- 单元测试 `src/ERP.UnitTests/ExpensePersistedNumericBoundsTests.cs`（ERP-512 回归 + ERP-538 对齐）：
  标量常量与 2 / 4 / 6 三种列精度判定、实际 EF 列精度与规则常量同源、受控乘法（合法乘积 / 乘积越界 / CLR 溢出 /
  全局取整无单列上限）、费用规则的过精度与量级越界、CRUD 拒绝（零写入 / 零改写）与零值可选字段 + 边界金额放行、
  传统分摊的预览 / 生成同边界（折人民币越界、总额过精度 / 越界、零单号消耗）与合法金额 / 单号 / 折人民币口径、
  批次预览 / 生成的越界来源 / 折人民币越界 / 基准值越界（零批次 / 零分摊行 / 零费用行）与合法生成的余差 / 比例 / 行合计算术、
  历史越界行仍可读、纯规则的基准值与来源金额边界、以及接线与文档同源契约。
- 集成测试 `src/ERP.IntegrationTests/ExpenseDeclaredPrecisionSqlServerTests.cs`（ERP-538，NEWERP_AUTOTEST 护栏，真实 SQL Server）：
  真实 `INFORMATION_SCHEMA` 列类型（六列 `decimal(18,4)`、汇率 `decimal(18,6)`）与 EF 模型双向确认、
  4 位小数金额 · 比例 · 税额与 6 位小数汇率（含声明列上限）经真实控制器落库后以 `CONVERT(varchar, ...)` 逐位原样往返、
  第 5 / 第 7 位小数与量级越界 / 负数在写入之前受控拒绝且零费用行、
  绕过护栏的原始 EF 写入无法承载第 5 位小数（被真实列取整 / 截断为 4 位） / 超容量值被 SQL Server 以算术溢出（8115）拒绝、
  非专用目标库在访问前被拒绝。
- 集成测试 `src/ERP.IntegrationTests/ExpensePersistedNumericBoundsSqlServerTests.cs`（ERP-512 回归 + ERP-538 对齐）：
  真实列精度（含 `INFORMATION_SCHEMA`）与边界值原样往返、原始 EF 写入越界被真实列 / 驱动拒绝、
  SQL Server 自身转换越界报算术溢出（8115）、费用 CRUD 越界拒绝且零写入（同一受权控制器随后合法创建成功）、
  传统分摊越界拒绝且零占号、批次生成的越界来源 / 基准值拒绝且零批次 / 零分摊行、合法「来源 → 分摊生成」流转与折人民币落库。
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

- 单元测试与真实 SQL 集成测试的输出 / TRX 记录于 `.ai/logs/ERP-512/`（ERP-512）与 `.ai/logs/ERP-538/`（ERP-538），
  保留原始失败日志与每一次独立调用的 TRX；
- 说明：本文件的完成只代表 Release 构建（含分析器 / 警告即错误）与单元测试通过，**构建完成不代表阶段验收完成**；
  真实 SQL 集成场景与最终浏览器验收由调度器 / 验收运行器按各自门禁执行。
