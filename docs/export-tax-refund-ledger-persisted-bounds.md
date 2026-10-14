# 出口退税台账金额 / 退税率 / 汇率的持久化精度护栏（ERP-529）

## 1. 目标

让出口退税台账（`db_owner.BaseTaxRefunds`，`api/base/tax-refunds`）的写入在**改写任何字段之前**，把会落库的数值收敛到
**权威建表脚本声明的实际列精度**内，关闭既有缺口：

1. `db_owner.BaseTaxRefunds` 由权威建表脚本（`SchemaUpgrader` 第 12.4 段）以
   `ExportAmount DECIMAL(18,4)`、`ExchangeRate DECIMAL(18,6)`、`RefundRate DECIMAL(18,4)`、
   `RefundableAmount DECIMAL(18,4)`、`RefundedAmount DECIMAL(18,4)` 创建；
2. `ErpDbContext.Config.cs` 此前**没有** `BaseTaxRefund` 的精度声明，EF 按 SQL Server 默认精度把这五列映射为
   `decimal(18,2)` —— 合法的 3 / 4 位小数金额与 3..6 位小数汇率会在到达 SQL 之前被**静默取整**到 2 位；
3. `TaxRefundLedgerRules.Validate` 此前只校验三个金额非负、退税率 0..100、已退税不超过可退税、币种 / 期间有界与
   申报 / 到账日期一致；**从未**读取 `ExchangeRate`，也**从未**判定任一标量能否逐位原样存储 ——
   零 / 负 / 过精度 / 超容量汇率与超容量金额都能通过应用层，然后在真实列上被取整或溢出。

本护栏把模型映射与权威列声明对齐，并在任何字段写入之前校验每一个标量，**绝不取整、截断、归零或回填**。

## 2. 精度口径（与权威列声明同源）

| 列 | 实际存储 | 规则常量（`TaxRefundLedgerRules`） |
|---|---|---|
| `BaseTaxRefunds.ExportAmount` | `DECIMAL(18,4)` | `AmountScale = 4`、`MaxAmount = 99_999_999_999_999.9999`、`MinAmount = -99_999_999_999_999.9999` |
| `BaseTaxRefunds.RefundRate` | `DECIMAL(18,4)`（且必须落在 0..100） | 同 `DECIMAL(18,4)` |
| `BaseTaxRefunds.RefundableAmount` | `DECIMAL(18,4)` | 同 `DECIMAL(18,4)` |
| `BaseTaxRefunds.RefundedAmount` | `DECIMAL(18,4)` | 同 `DECIMAL(18,4)` |
| `BaseTaxRefunds.ExchangeRate` | `DECIMAL(18,6)`（且必须严格大于 0） | `ExchangeRateScale = 6`、`MaxExchangeRate = 999_999_999_999.999999` |

- 精度常量**复用**系统唯一标量边界口径 `FinancePersistedAmountRules`（ERP-508），**不新增第二套边界规则**；
- 判定口径 = **逐位原样可存储**：`decimal.Round(value, scale, MidpointRounding.AwayFromZero) == value` 且值在列区间内。
  第 5 位小数（金额 / 退税率）、第 7 位小数（汇率）与量级溢出（`1e14` / `1e12` / `decimal.MaxValue`）一律拒绝；
- **半进位取整后恰好落到列上限**的过小数位值（如 `99_999_999_999_999.99985 → …9999`）**仍然拒绝**：
  护栏判定的是**原值**，绝不采信「取整之后才可存储」。恰好等于上限本身的值照常放行；
- 零值可选语义保持：`0` / `0.0000` / `0.000000` 可表示（金额非负与汇率严格为正的既有规则分别裁定）。

## 3. 护栏口径

| 项 | 口径 |
|---|---|
| 校验时点 | 新增 / 修改在既有实时身份 + `tax-refund` 菜单 + ERP-097 客户范围（与拟议客户真实性）之后、**任何写入之前**调用 `Validate`；被拒请求零台账行、零改写（含审计时间戳） |
| 金额 | `ExportAmount` / `RefundableAmount` / `RefundedAmount` 非负且可在 `DECIMAL(18,4)` 内逐位原样存储 |
| 退税率 | 落在 0..100 且可在 `DECIMAL(18,4)` 内逐位原样存储 |
| 汇率 | **严格大于 0**（零 / 负拒绝）且可在 `DECIMAL(18,6)` 内逐位原样存储 |
| 拒绝语义 | 复用既有受控 `1001 InvalidParameter`（字段标签 + 精度文案），**绝不**取整 / 截断 / 归零 / 回填 |
| 顺序 | 既有非负 / 0..100 / 已退不超过可退 / 币种 / 期间 / 日期文案与顺序**不变**；精度判定紧跟在对应字段的既有判定之后 |
| 授权不变 | 实时启用非删除身份、账号状态、既有 `tax-refund` 菜单、ERP-097 权威客户范围仍在任何计数 / 读取 / 写入之前解析；`CustomerId` 为空的历史无主行仍只对特权账号可读；被拒调用不落任何台账行 |
| 只读不变 | 只读退税汇总报表（`ReportController.TaxRefundSummary`）聚合语义**不动**；历史行照常可读，不做静默修复 / 回填 |

## 4. 实现

- `src/ERP.Infrastructure/Data/ErpDbContext.Config.cs`：新增 `BaseTaxRefund` 五列的
  `HasPrecision(18,4)`（金额 / 退税率）与 `HasPrecision(18,6)`（汇率）；**只声明精度**，不新增表 / 列 / 索引 /
  菜单 / 权限，不改任何列类型与 DDL。
- `src/ERP.Application/Services/TaxRefundLedgerRules.cs`：
  - 新增 `AmountScale` / `ExchangeRateScale` / `MaxAmount` / `MinAmount` / `MaxExchangeRate` 常量（复用 `FinancePersistedAmountRules`）；
  - 新增纯判定 `IsAmountRepresentable` / `IsExchangeRateRepresentable` 与受控校验
    `EnsureAmountRepresentable` / `EnsureExchangeRateRepresentable`（无副作用，绝不取整）；
  - `Validate` 在既有规则之上追加四个小数位金额 / 退税率与严格正六位小数汇率判定，并同步类文档与 `RuleText`。
- `src/ERP.Api/Controllers/TaxRefundController.cs`：仅补充 XML 契约说明；调用路径、授权与读取顺序**不改**。

## 5. 不改写项

- 不新增 / 不修改任何表、列、索引、schema、迁移或种子数据；不新增权限、菜单、角色、用户授权；无匿名 / 管理员回退；
- 不改既有非负 / 0..100 / 已退不超过可退 / 币种与期间长度 / 日期一致性规则，不改身份 / 菜单 / 客户范围解析与锁序；
- 不改只读退税汇总报表聚合、导出列语义与审计口径；不做任何历史数据修复 / 回填；
- 不执行生产 SQL、不部署、不读取任何密钥 / 连接串配置。

## 6. 测试

- 单元测试 `src/ERP.UnitTests/TaxRefundLedgerPersistedNumericBoundsTests.cs`（内存库 + 真实控制器 + 真实身份）：
  实际 EF 列精度与规则常量逐字同源（仅以 SQL Server 提供程序只构建模型）、四位小数金额与六位小数汇率的可表示边界、
  半进位取整落到上限仍拒绝、第 5 / 第 7 位小数与量级溢出拒绝、零 / 负汇率拒绝、`Validate` 拒绝时逐字段不变、
  控制器新增 / 修改的零写入与边界值原样落库、源码契约（映射同源 + 护栏先于写入 + 规则层不取整）。
- 单元测试 `src/ERP.UnitTests/TaxRefundLedgerTests.cs`：ERP-529 追加段验证既有授权 / 菜单 / 范围口径**不变**且
  优先级更高（无身份 / 撤销菜单 / 越范围一律先拒），被拒写入零台账行，合法 4 位小数连同 6 位小数汇率照常落库。
- 集成测试 `src/ERP.IntegrationTests/TaxRefundLedgerPersistedNumericBoundsSqlServerTests.cs`（NEWERP_AUTOTEST 护栏，
  真实 SQL Server）：真实 `INFORMATION_SCHEMA` 列精度、4 位小数金额与 6 位小数汇率（含恰好上限）经真实受权控制器
  **原样往返**（`CONVERT(varchar, …)` 逐位断言）、越界 / 零负 / 溢出零写入、半进位取整到上限仍拒绝、被拒修改不改权威行、
  绕过护栏的原始 EF 写入被真实列静默取整而超容量值被 SQL Server 以算术溢出（8115）拒绝。
- 既有回归：`TaxRefundLedgerTests`（ERP-442 授权 / 范围 / CRUD）与
  `TaxRefundLedgerAuthorizationSqlServerTests`（真实控制器授权回归）保持通过。

## 7. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，
  且 `Integrated Security=true`；护栏在**任何数据库访问之前**校验失败即中止；
- 每次运行创建**全新的 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 不读取 `appsettings` / `.env` / 生产凭据；连接串只来自进程环境变量或专用 localdb 默认值；
- 测试种子一律使用 SQL Server 自增主键（不显式指定 Id）。

## 8. 环境证据

- 单元测试与真实 SQL 集成测试的输出 / TRX 记录于 `.ai/logs/ERP-529/`（保留原始失败日志）；
- 说明：本文件的完成只代表 Release 构建（含分析器 / 警告即错误）与单元测试通过，**构建完成不代表阶段验收完成**；
  真实 SQL 集成场景与最终验收由调度器 / 验收运行器按各自门禁执行。
