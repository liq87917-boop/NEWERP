# 收款单与定金申请单标量金额存储边界护栏（ERP-508）

## 1. 目标

让既有客户收款单（`FinanceReceipt`）与定金申请单（`FinanceDepositApply`）的**写入与承诺路径**在**单号预约与任何
表头 / 来源写入之前**，把「按币种精度取整后的单个标量金额」收敛到该金额列的**实际存储精度**内：

1. `CustomerReceiptLifecycleRules.NormalizeReceiptAmount` / `FinanceDepositApplyLifecycleRules.NormalizeApplyAmount`
   只做「按币种精度取整 + 大于 0」，**没有**任何标量存储上界：极大正金额（例如 `1e16`、
   `9_999_999_999_999_999.995` 取整进位后的 `1e16`、`decimal.MaxValue`）能通过应用层检查，
   却会在写入 SQL 时被截断 / 溢出，或被驱动直接拒绝；
2. 两个金额列在 EF 模型中**未**显式 `HasPrecision`，按 EF SQL Server 默认值落库为 `DECIMAL(18,2)`
   （16 位整数 + 2 位小数）；
3. 收款单的提交 / 审核此前**没有**复核「已持久化金额」的存储可表示性（定金申请单经
   `EnsurePersistedApplyConsistentAsync` 已有持久化字段复核心跳，本次自动继承新边界）。

只补齐**标量存储边界**，**不**改币种 / 取整口径、单据流转、授权、来源资格、分摊额度、聚合报表合计与审计。

## 2. 精度口径（与实际 EF 存储同源）

| 列 | 实际存储 | 规则常量（`FinancePersistedAmountRules`） |
|---|---|---|
| `FinanceReceipt.Amount` | `DECIMAL(18,2)`（EF 默认，模型未显式声明） | `DecimalPrecision = 18`、`DecimalScale = 2` |
| `FinanceDepositApply.Amount` | `DECIMAL(18,2)`（同上） | `MaxDecimal18Scale2 = 9_999_999_999_999_999.99`、`MinDecimal18Scale2 = -9_999_999_999_999_999.99` |

- 边界常量与真实列类型逐字同源：单元测试以 SQL Server 提供程序**仅构建模型**断言
  `IProperty.GetColumnType() == "decimal(18,2)"`（且 `GetPrecision()/GetScale()` 为 `null`，即未显式声明）；
  集成测试再以真实 `INFORMATION_SCHEMA.COLUMNS`（`decimal(18,2)`）与 SQL Server 自身「算术溢出（错误号 8115）」
  双重确认；
- 取整口径**原样**复用既有 `CurrencyAmountRules.RoundAmount`（`MidpointRounding.AwayFromZero`，
  零小数币种 `JPY / KRW / VND / IDR` 为 0 位、其余含未知币种为 2 位），守卫只在**取整之后**追加区间判定；
- 守卫**只**作用于「单个文档标量金额」：绝不用于 `CurrencyAmountRules.RoundAmount` 本身、只读权威金额
  （`AuthoritativeReceiptAmount` / `AuthoritativeApplyAmount`）或聚合报告合计 —— 求和可能合法地超过单个存储列容量。

## 3. 护栏口径

| 项 | 口径 |
|---|---|
| 校验时点 | **创建**：授权 / 客户 / 来源之后、**单号预约与任何已跟踪写入之前**；**修改**：单据行锁内、任何字段改写之前；**提交 / 审核**：同一事务的既有行锁内、置状态之前 |
| 边界判定 | 取整后的标量金额必须满足 `decimal.Round(v,2) == v` 且 `MinDecimal18Scale2 <= v <= MaxDecimal18Scale2`，否则受控业务错误（`InvalidParameter`，文案含 `DECIMAL(18,2)`） |
| 取整进位越界 | 例如 USD `9_999_999_999_999_999.995` 取整为 `10_000_000_000_000_000.00`、JPY `9_999_999_999_999_999.6` 取整为 `1e16`：均在**单号 / 写入之前**拒绝 |
| 精确上限放行 | 恰好 `9_999_999_999_999_999.99`（USD/CNY）与 `9_999_999_999_999_999`（JPY）正常落库 |
| 最小正数 | 取整后为 0 的金额仍按既有「金额必须大于 0」拒绝（`0.005 USD → 0.01` 放行） |
| 提交 / 审核复核 | 收款单在既有收款单行锁内复核**已持久化**金额（`EnsurePersistedReceiptAmountConsistent`）；定金申请单经既有 `EnsurePersistedApplyConsistentAsync` 复核持久化商业字段时同口径判定 |
| 拒绝语义 | 被拒请求**零单号消耗**、零收款单 / 申请单 / 来源字段 / 状态 / 审计变化；修改被拒时既有单据逐字段不变 |
| 撤销语义 | **历史读取 / 取消 / 删除 / 作废 / 冲销不调用本护栏**，不新增任何草稿有效性闸门 |

## 4. 实现

- `src/ERP.Application/Services/FinancePersistedAmountRules.cs`（新增，纯判定 / 无数据库依赖）
  - `DecimalPrecision` / `DecimalScale` / `MaxDecimal18Scale2` / `MinDecimal18Scale2` 常量；
  - `IsRepresentable(decimal)` 与 `EnsureRepresentable(decimal currencyRoundedAmount, string amountLabel)`
    （不可表示时抛 `InvalidParameter` 并原样返回入参，绝不取整 / 截断）。
- `src/ERP.Application/Services/CustomerReceiptLifecycleRules.cs`
  - `NormalizeReceiptAmount`：取整 + 正数校验之后调用标量守卫；
  - 新增 `EnsurePersistedReceiptAmountConsistent(FinanceReceipt)`（只读复核，供提交 / 审核在锁内调用）；
  - `RuleText` 同步既有权威口径文案。
- `src/ERP.Application/Services/FinanceDepositApplyLifecycleRules.cs`
  - `NormalizeApplyAmount`：取整 + 正数校验之后调用标量守卫（创建 / 修改 / 提交 / 审核经既有调用点自动生效）；
  - `RuleText` 同步既有权威口径文案。
- `src/ERP.Api/Controllers/FinanceReceiptComplaintControllers.cs`
  - 收款单 `Submit` / `Approve`：在既有收款单行锁内、置状态之前复核已持久化金额；
  - 客诉单路由**不变**。
- `src/ERP.Api/Controllers/FinanceApplyControllers.cs`
  - 仅类文档说明；定金申请单创建 / 修改 / 提交 / 审核的调用点**不变**（已通过既有归一化与持久化复核覆盖）；
  - 货款申请单（`payment-apply`）路由**不变**（其标量边界由后续任务处理）。

## 5. 不改写项

- 不新增 / 不修改任何表、列、索引、schema、迁移或种子数据；不新增权限、菜单、角色、用户授权，不做自动主数据变更；
- 不改币种口径、取整口径（`AwayFromZero`、零小数币种）、汇率、来源资格 / 容量（销售订单归属、收款分摊额度、
  定金剩余容量）、确定性锁序、受控错误码与审计；
- 不把单列边界施加到 `CurrencyAmountRules.RoundAmount`、只读权威金额比对或聚合报表合计；
- 不改客诉单 / 付款单 / 收款分摊 / 发票 / 来源单据行为；不做生产运维与不可逆数据操作。

## 6. 测试

- 单元测试 `src/ERP.UnitTests/ReceiptDepositPersistedAmountTests.cs`（内存库 + 真实 HTTP 身份）：
  标量常量与可表示判定、全局取整 / 只读权威金额**无**单列上限、精确上限与半进位进位放行、取整进位越界 /
  `1e16` / 极大十进制拒绝、零小数币种与最小正数、实际 EF 列精度同源、创建 / 修改越界拒绝（零写入、零占号）、
  提交 / 审核复核已持久化越界金额被拒且状态 / 审计不变、撤销（取消）语义不变、合法边界金额全生命周期放行。
- 集成测试 `src/ERP.IntegrationTests/ReceiptDepositPersistedAmountSqlServerTests.cs`（NEWERP_AUTOTEST 护栏，真实 SQL Server）：
  真实列精度（含 `INFORMATION_SCHEMA`）与边界值原样往返、原始 EF 写入越界被真实列 / 驱动拒绝、
  SQL Server 自身转换越界报算术溢出（8115）、创建 / 修改越界拒绝且零写入 / 零占号（同一受权控制器随后合法创建成功）、
  小数位与零小数币种取整落库、收款单 / 定金申请单「创建 → 提交 → 审核」合法流转、非专用目标库在访问前被拒绝。
- 既有收款 / 分摊 / 发票 / 来源 / 定金 / 并发回归测试全部照常通过（`CustomerReceiptLifecycleTests`、
  `ReceiptLifecycleConcurrencyTests`、`FinanceDepositApplyLifecycleTests`、`CustomerReceiptAllocationTests` 等）。

## 7. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，
  且 `Integrated Security=true`；护栏在**任何数据库访问之前**校验失败即中止；
- 每次运行创建**全新的 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 不读取 `appsettings` / `.env` / 生产凭据；连接串只来自进程环境变量或专用 localdb 默认值；
- 测试种子一律使用 SQL Server 自增主键（不显式指定 Id），并复用种子数据中的既有菜单 / 单据号规则。

## 8. 环境证据

- 单元测试与真实 SQL 集成测试的输出 / TRX 记录于 `.ai/logs/ERP-508/`（保留原始失败日志）；
- 说明：本文件的完成只代表 Release 构建（含分析器 / 警告即错误）与单元测试通过，**构建完成不代表阶段验收完成**；
  真实 SQL 集成场景与最终验收由调度器 / 验收运行器按各自门禁执行。
