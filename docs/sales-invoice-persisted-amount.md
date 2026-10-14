# 客户销项发票证据标量金额与登记算术存储边界护栏（ERP-511）

## 1. 目标

让既有客户销项发票证据登记模块的**写入（创建 / 修改 / 保存分摊）与承诺（登记 / 冻结）路径**在**任何表头 / 分摊行
写入之前**，把「按币种精度取整后的单个标量金额」收敛到该金额列的**实际存储精度**内：

1. `CustomerSalesInvoiceEvidenceRules.ValidateAmounts` 此前只做「非负 + 按币种精度取整 + 含税总额 > 0 + 等式」，**没有**
   任何标量存储上界：三个**各自为正、且等式成立**的极大输入（例如净额 `1e16` + 税额 `0` = 含税总额 `1e16`，
   或取整进位后越过单列上限的值）能通过应用层检查，却会在写入 SQL 时被截断 / 溢出，或被驱动直接拒绝；
   同时 `gross - net - tax` 差额、`net + tax` 求和本身在极大值下还可能触发 **CLR 溢出（`OverflowException`）**；
2. `CustomerSalesInvoiceEvidenceRules.NormalizeAllocationAmount` 同样只做「按币种精度取整 + 大于 0」，**没有**标量存储上界；
3. `CustomerSalesInvoiceEvidence.NetAmount / TaxAmount / GrossAmount` 与
   `CustomerSalesInvoiceAllocation.AllocatedAmount` 实际落库为 `DECIMAL(18,2)`（16 位整数 + 2 位小数），
   其中分摊金额列在 EF 模型中**显式** `HasPrecision(18, 2)`，发票三列按 EF 默认精度同为 `DECIMAL(18,2)`；
4. 登记（`RecordAsync`）此前只复核**已持久化分摊行**的资格与容量，**没有**复核发票自身已持久化的三个标量金额；
   被外部改写 / 历史遗留的越界或等式不一致快照会被登记动作固化。

只补齐**标量存储边界与登记前的只读金额复核**，**不**改币种 / 取整口径、单据流转、授权、来源资格、分摊额度、
只读聚合报表合计与审计。

## 2. 精度口径（与实际 EF 存储同源）

| 列 | 实际存储 | 规则常量（`FinancePersistedAmountRules`） |
|---|---|---|
| `CustomerSalesInvoiceEvidence.NetAmount` | `DECIMAL(18,2)`（EF 默认精度） | `DecimalPrecision = 18`、`DecimalScale = 2` |
| `CustomerSalesInvoiceEvidence.TaxAmount` | `DECIMAL(18,2)`（同上） | `MaxDecimal18Scale2 = 9_999_999_999_999_999.99`、`MinDecimal18Scale2 = -9_999_999_999_999_999.99` |
| `CustomerSalesInvoiceEvidence.GrossAmount` | `DECIMAL(18,2)`（同上） | 同上 |
| `CustomerSalesInvoiceAllocation.AllocatedAmount` | `DECIMAL(18,2)`（模型显式 `HasPrecision(18,2)`） | 同上 |

- 边界常量与真实列类型逐字同源：单元测试以 SQL Server 提供程序**仅构建模型**断言
  `IProperty.GetPrecision() == 18`、`GetScale() == 2` 且 `GetColumnType() == "decimal(18,2)"`；
  集成测试再以真实 `INFORMATION_SCHEMA.COLUMNS` 与 SQL Server 自身「算术溢出（错误号 8115）」双重确认；
- 边界常量**复用** ERP-508/ERP-509/ERP-510 既有的 `FinancePersistedAmountRules`
  （系统内不允许出现第二套标量金额边界规则）；
- 取整口径**原样**复用既有 `CurrencyAmountRules.RoundAmount`（`MidpointRounding.AwayFromZero`，
  零小数币种 `JPY / KRW / VND / IDR` 为 0 位、其余含未知币种为 2 位），守卫只在**取整之后**追加区间判定；
- 守卫**只**作用于「单个文档标量金额」：绝不用于 `CurrencyAmountRules.RoundAmount` 本身、`LinkageText` /
  只读权威金额比对、收款时效证据 / 对账 / 动态应收等聚合报表合计 ——
  求和（例如两张各自恰好等于上限的发票合计 `19_999_999_999_999_999.98`）可能合法地超过单个存储列容量。

## 3. 护栏口径

| 项 | 口径 |
|---|---|
| 校验时点 | **创建**：授权 / 客户 / 币种之后、**任何已跟踪写入之前**；**修改**：发票行锁内、任何字段改写之前；**保存分摊**：订单 / 发票行锁内、替换分摊行之前；**登记**：同一事务的既有订单 / 发票行锁内、置状态之前 |
| 边界判定 | 取整后的标量金额必须满足 `decimal.Round(v,2) == v` 且 `MinDecimal18Scale2 <= v <= MaxDecimal18Scale2`，否则受控业务错误（`InvalidParameter`，文案含 `DECIMAL(18,2)`） |
| 算术溢出防护 | 存储边界判定**先于** `net + tax` 求和与 `gross - net - tax` 差额计算：极大值（含 `decimal.MaxValue`）在计算之前即被受控拒绝，绝不以 `OverflowException` 崩溃 |
| 取整进位越界 | 例如 USD `9_999_999_999_999_999.995` 取整为 `1e16`、JPY `9_999_999_999_999_999.6` 取整为 `1e16`：均在**任何表头 / 分摊行写入之前**拒绝 |
| 精确上限放行 | 恰好 `9_999_999_999_999_999.99`（USD/CNY）与 `9_999_999_999_999_999`（JPY）正常落库 |
| 零税额 / 零净额 | 既有语义保留：`net = 0, tax = gross > 0` 与 `tax = 0, net = gross > 0` 均照常放行 |
| 最小正数 | 取整后为 0 的金额仍按既有「必须大于 0」拒绝（`0.005 USD → 0.01` 放行） |
| 分摊金额 | `NormalizeAllocationAmount` 在取整 + 正数校验之后调用同一守卫；合计不超过含税总额、来源订单容量与「其他未作废发票已分摊」口径一律不变 |
| 登记复核 | `RecordAsync` 在既有订单 / 发票行锁内、改状态之前调用 `EnsurePersistedAmountsConsistent`：**只读**复核已持久化金额的非负、`DECIMAL(18,2)` 可表示、已按币种取整、含税总额 > 0 与等式；不一致即受控拒绝（`RuleConflict`） |
| 拒绝语义 | 被拒请求**零表头 / 零分摊行写入**；修改 / 登记被拒时既有发票逐字段（含 `UpdatedAt` / `RecordedAt` / `Status`）不变 |
| 撤销语义 | **历史读取 / 作废不调用本护栏**，不新增任何草稿有效性闸门 |
| 保留项 | 既有授权（实时身份 / 「销售订单」（`sales-order`）菜单 / 客户数据范围）、受支持币种、来源资格与容量、客户与订单归属、确定性锁序、受控错误码与审计一律原样保留 |

## 4. 实现

- `src/ERP.Application/Services/CustomerSalesInvoiceEvidenceRules.cs`
  - `ValidateAmounts`：取整之后、等式与差额之前，对三项标量调用既有守卫
    `FinancePersistedAmountRules.EnsureRepresentable(...)`；
  - `NormalizeAllocationAmount`：取整 + 正数校验之后调用同一守卫；
  - 新增 `EnsurePersistedAmountsConsistent(net, tax, gross, currency, identity)`：登记前的**只读**一致性复核
    （非负 / 可表示 / 已取整 / 含税总额 > 0 / 等式），**不取整、不改写**任何已存储值；
  - `AmountEquationText` / 类文档同步既有权威口径文案。
- `src/ERP.Application/Services/CustomerSalesInvoiceEvidenceService.cs`
  - `RecordAsync`：锁内 `EnsureRecordable` 之后、`RevalidatePersistedAllocationsAsync` 与置状态之前，调用
    `CustomerSalesInvoiceEvidenceRules.EnsurePersistedAmountsConsistent(...)`；创建 / 修改 / 保存分摊调用点**不变**
    （已通过既有归一化覆盖）。
- `src/ERP.Api/Controllers/CustomerSalesInvoiceEvidenceController.cs`：仅类文档说明；创建 / 修改 / 分摊 / 登记 / 作废路由
  与语义**不变**。
- `src/ERP.Application/Services/FinancePersistedAmountRules.cs`：**复用** ERP-508 既有纯判定边界（本次未改动实现）。

## 5. 不改写项

- 不新增 / 不修改任何表、列、索引、schema、迁移或种子数据；不新增权限、菜单、角色、用户授权，不做自动主数据变更；
- 不改币种口径、取整口径（`AwayFromZero`、零小数币种）、汇率、来源资格 / 容量（订单发票容量）、确定性锁序、
  受控错误码与审计；不新增聚合总额上限、不做跨币种合计；
- 不把单列边界施加到 `CurrencyAmountRules.RoundAmount`、只读权威金额比对或聚合报表合计；
- 登记**不**静默修复 / 改写任何历史快照；更正仍走**显式作废 → 重新登记**的既有路径；
- 不改销售订单 / 客户信用状态 / 收款单与其引用行 / 库存 / 退税 / 费用，不做生产运维与不可逆数据操作。

## 6. 测试

- 单元测试 `src/ERP.UnitTests/SalesInvoicePersistedAmountTests.cs`（内存库 + 真实 HTTP 身份）：
  标量常量与可表示判定、全局取整 / 只读聚合**无**单列上限、精确上限与半进位进位放行、零税额 / 零净额、
  取整进位越界 / `1e16` / 极大十进制 / 净额 + 税额 CLR 溢出防护、零小数币种取整、实际 EF 列精度同源、
  创建 / 修改 / 保存分摊越界拒绝（零写入 / 零分摊行）、登记锁内复核已持久化越界 / 等式不一致 / 未取整金额被拒且
  状态与审计不变、读取与作废语义不变、合法边界金额登记放行。
- 集成测试 `src/ERP.IntegrationTests/SalesInvoicePersistedAmountSqlServerTests.cs`（NEWERP_AUTOTEST 护栏，真实 SQL Server）：
  真实列精度（含 `INFORMATION_SCHEMA`）与边界值原样往返、原始 EF 写入越界被真实列 / 驱动拒绝、
  SQL Server 自身转换越界报算术溢出（8115）、创建 / 修改越界拒绝且零写入（同一受权控制器随后合法创建成功）、
  小数位与零小数币种取整落库、合法「创建 → 分摊 → 登记」流转、非专用目标库在访问前被拒绝。
- 既有客户销项发票 / 收款分摊 / 作废 / 并发 / 授权回归测试照常通过（`CustomerSalesInvoiceEvidenceTests`、
  `CustomerSalesInvoiceConcurrencyTests`、`CustomerSalesInvoiceEntryAuthorizationTests`、
  `CustomerSalesInvoiceCollectionAllocationTests`、`CustomerSalesInvoiceReceiptAllocationExposureTests`、
  `CustomerInvoiceCollectionTimingTests` 等）。

## 7. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，
  且 `Integrated Security=true`；护栏在**任何数据库访问之前**校验失败即中止；
- 每次运行创建**全新的 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 不读取 `appsettings` / `.env` / 生产凭据；连接串只来自进程环境变量或专用 localdb 默认值；
- 测试种子一律使用 SQL Server 自增主键（不显式指定 Id），并复用种子数据中的既有菜单 / 单据号规则。

## 8. 环境证据

- 单元测试与真实 SQL 集成测试的输出 / TRX 记录于 `.ai/logs/ERP-511/`（保留原始失败日志）；
- 说明：本文件的完成只代表 Release 构建（含分析器 / 警告即错误）与单元测试通过，**构建完成不代表阶段验收完成**；
  真实 SQL 集成场景与最终验收由调度器 / 验收运行器按各自门禁执行。

