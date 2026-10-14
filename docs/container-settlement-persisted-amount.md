# 装柜结算单标量金额存储边界护栏（ERP-514）

## 1. 目标

让既有装柜结算单（`FinanceContainerSettlement`）的**写入与承诺路径**在**单号预约与任何表头 / 来源写入之前**，
把三个金额标量收敛到该金额列的**实际存储精度**内：

1. `FinanceContainerSettlementLifecycleRules.NormalizeTotalAmount` / `NormalizeCost` 此前只做「按既有存储精度取整 +
   正负校验」，**没有**任何标量存储上界：极大正金额（`1e16`、`9_999_999_999_999_999.995` 取整进位后的 `1e16`、
   `decimal.MaxValue`）能通过应用层检查，却会在写入 SQL 时被截断 / 溢出，或被驱动直接拒绝；
2. 三个金额列在 EF 模型中**未**显式 `HasPrecision`，按 SQL Server 提供程序默认精度落库为 `DECIMAL(18,2)`
   （16 位整数 + 2 位小数）；
3. 提交 / 审核此前只把**已持久化**金额重新走一遍「取整 + 正负」判定：过精度（例如直接 SQL 写入的 3 位小数）会被
   静默取整，而不是按「不可原样存储」拒绝。

只补齐**标量存储边界与持久化一致性复核**，**不**改币种 / 取整口径、单据流转、授权、来源资格、聚合报表合计与审计。

## 2. 精度口径（与实际 EF 存储同源）

| 列 | 实际存储 | 规则常量（`FinancePersistedAmountRules`） |
|---|---|---|
| `FinanceContainerSettlement.TotalAmount` | `DECIMAL(18,2)`（EF 默认，模型未显式声明） | `DecimalPrecision = 18`、`DecimalScale = 2` |
| `FinanceContainerSettlement.FreightCost` | `DECIMAL(18,2)`（同上） | `MaxDecimal18Scale2 = 9_999_999_999_999_999.99` |
| `FinanceContainerSettlement.OtherCost` | `DECIMAL(18,2)`（同上） | `MinDecimal18Scale2 = -9_999_999_999_999_999.99` |

- 边界常量与真实列类型逐字同源：单元测试以 SQL Server 提供程序**仅构建模型**断言
  `IProperty.GetColumnType() == "decimal(18,2)"`（且 `GetPrecision()/GetScale()` 为 `null`，即未显式声明）；
  集成测试再以真实 `INFORMATION_SCHEMA.COLUMNS`（`decimal(18,2)`）与 SQL Server 自身「算术溢出（错误号 8115）」
  双重确认；
- 取整口径**原样**保留既有「按既有存储精度 2 位小数、`MidpointRounding.AwayFromZero`」；
  守卫只在**取整之后**追加区间判定，绝不重新取整、绝不改写调用方提交值；
- 守卫**只**作用于「本单单个金额列」：绝不施加到聚合求和 / 报表合计（`MaxDecimal18Scale2 + MaxDecimal18Scale2`
  合法地超过单列容量），也不做币种换算、不发明 `total = sum(costs)` 公式。

## 3. 护栏口径

| 项 | 口径 |
|---|---|
| 校验时点 | **创建**：身份 / 菜单 / 客户范围之前先归一化，且**单号预约与任何已跟踪写入之前**完成；**修改**：同一事务的既有行锁之前完成归一化，任何字段改写之前；**提交 / 审核**：同一事务的既有来源清单行锁 + 结算单行锁内、置状态之前 |
| 边界判定 | 取整后的标量必须满足 `decimal.Round(v,2) == v` 且 `MinDecimal18Scale2 <= v <= MaxDecimal18Scale2`（复用 `FinancePersistedAmountRules.EnsureRepresentable` / `IsRepresentable`），否则受控业务错误（`InvalidParameter`，文案含 `DECIMAL(18,2)`） |
| 取整进位越界 | `9_999_999_999_999_999.995` 取整为 `10_000_000_000_000_000.00`、`9_999_999_999_999_999.999` 取整为 `1e16`：均在**单号 / 写入之前**拒绝 |
| 精确上限放行 | 恰好 `9_999_999_999_999_999.99`（含半进位恰好进位到上限）正常落库，并在真实 `decimal(18,2)` 列上原样往返 |
| 零费用 / 无合计公式 | `FreightCost` / `OtherCost` 允许为 `0`；`total` 与两项费用之间**不施加**任何等式，费用可合法远大于总金额 |
| 提交 / 审核复核 | 既有行锁内只读复核 `EnsurePersistedAmountsConsistent(total, freight, other)`：三个标量都必须可在 `DECIMAL(18,2)` 内原样存储（过精度 / 越界拒绝），且保留既有 `total > 0`、费用 `>= 0` 语义；**绝不取整 / 截断 / 改写历史快照** |
| 拒绝语义 | 被拒请求**零单号序列消耗**、零表头写入、零来源清单改写（状态 / 删除标记 / 审计时间戳不变）；被拒修改时既有结算单逐字段（含 `UpdatedAt`）不变 |
| 撤销语义 | **历史读取 / 取消 / 删除不调用金额护栏**（`validateAmounts: false`），不新增任何草稿有效性闸门 |
| 来源口径 | 显式来源只回填权威装柜清单 Id；来源转成结算单时金额走**同一**既有边界（`ContainerSettlementLoadingSourceService.SettlementAmountBoundaryText`），候选查询本身绝不返回金额 |

## 4. 实现

- `src/ERP.Application/Services/FinanceContainerSettlementLifecycleRules.cs`
  - 新增 `TotalAmountLabel` / `FreightCostLabel` / `OtherCostLabel` / `PersistedAmountStorageText` 常量；
  - `NormalizeTotalAmount` / `NormalizeCost`：既有两位小数取整与正负校验之后，复用
    `FinancePersistedAmountRules.EnsureRepresentable` 追加实际列精度判定；
  - 新增 `EnsurePersistedAmountsConsistent(total, freight, other)`（只读，供提交 / 审核在锁内调用）与私有
    `EnsurePersistedAmountStorage`（复用 `FinancePersistedAmountRules.IsRepresentable`）；
  - `RuleText` / `BoundaryText` / 类文档同步既有权威口径。
- `src/ERP.Application/Services/ContainerSettlementLoadingSourceService.cs`
  - 仅新增来源转结算金额边界文案 `SettlementAmountBoundaryText` 与类文档说明（只读候选**不返回金额**，也**不引入**第二套边界）。
- `src/ERP.Api/Controllers/FinanceSettlementControllers.cs`（仅装柜结算单路径）
  - `TransitionAsync` 的 `validateAmounts` 分支由「重新归一化」改为锁内只读 `EnsurePersistedAmountsConsistent`；
  - 装柜结算单类文档同步；**散货结算单控制器与路由不变**。


## 5. 不改写项

- 不新增 / 不修改任何表、列、索引、schema、迁移或种子数据；不新增权限、菜单、角色、用户授权；
- 不改实时身份 / 菜单授权 / 客户数据范围、来源装柜清单精确资格与参与方 / 上游客户范围、确定性锁序
  （先来源清单行、后结算单行）、受控错误码与审计；
- 不改装柜清单取消协同（存在未删除且未取消的结算单时阻断取消，显式取消后释放）；
- 不把单列边界施加到聚合 / 报表合计、只读权威金额比对或费用分摊证据；
- 不写库存 / 资金 / 会计，不产生收款、付款、核销、分摊入账或对账结论；
- 散货结算单（`bulk-settlement`）路由与该控制器**保持原样**，其标量边界由后续任务处理。

## 6. 测试

- 新增单元测试 `src/ERP.UnitTests/ContainerSettlementPersistedAmountTests.cs`（内存库 + 真实 HTTP 身份，25 个用例）：
  纯规则常量 / 标签 / 文案同源、实际 EF 列精度（仅构建模型）、精确上限与半进位放行、越界 / 取整进位越界 /
  `1e16` / 极大十进制受控拒绝、零费用与聚合求和不受单列边界约束、已持久化一致性（过精度 / 越界 / 零总金额 /
  负费用）判定、创建（手工 / 显式链接来源）越界拒绝（零写入 / 零占号 / 来源零改写）、未认证优先拒绝、
  修改越界拒绝且逐字段审计不变、提交 / 审核越界与过精度拒绝且状态不变、精确上限全生命周期放行、
  取消 / 删除不新增草稿有效性闸门。
- 新增真实 SQL 集成测试 `src/ERP.IntegrationTests/ContainerSettlementPersistedAmountSqlServerTests.cs`
  （GUID 独占 `NEWERP_AUTOTEST_ERP514_*` 目标，16 个用例）：真实列精度（EF 模型 + `INFORMATION_SCHEMA`）、
  精确上限原样往返、越界原始 EF 写入被真实列 / 驱动拒绝、SQL Server 自身转换越界报算术溢出（8115）、
  创建 / 修改越界拒绝且零写入 / 零序列消耗（同一受权控制器随后合法创建成功）、显式链接来源越界拒绝、
  合法「创建 → 提交 → 审核」原样持久化边界金额、直接 SQL 写入的存量零总金额 / 负费用在提交 / 审核被拒
  （状态 / 序列零变化、历史读取与取消照常）、列精度取整行为证据，以及非专用目标库在访问前 fail closed。
- 适配既有测试（原断言全部保留）：
  - `src/ERP.UnitTests/FinanceContainerSettlementLifecycleTests.cs`：创建拒绝用例追加越界标量、提交 / 审核用例追加
    存量越界与过精度值、规则契约与控制器源码契约追加边界与锁内只读复核断言；
  - `src/ERP.UnitTests/FinanceSettlementCanonicalEntryTests.cs`：追加「超出存储精度金额创建拒绝（零写入零占号）」用例；
  - `src/ERP.UnitTests/ContainerSettlementLoadingSourceTests.cs`：追加「显式来源生成结算单仍走同一既有标量存储边界」用例；
  - `src/ERP.IntegrationTests/FinanceContainerSettlementLifecycleSqlServerTests.cs`：在既有夹具（真实菜单授权 +
    业务员员工映射 + 真实客户 / 装柜清单）上追加「越界拒绝零占号 + 上限金额全生命周期」真实 SQL 用例，
    并新增只读单号序列 / 最新结算单助手（不改写原有断言）。
- 相关既有授权 / 来源 / 并发 / 取消 SQL 回归照常执行：
  `FinanceContainerSettlementLifecycleSqlServerTests`、`ContainerSettlementLoadingSourceSqlServerTests`
  在同一 `ContainerSettlement` 过滤下真实运行通过（含三个按既有设计跳过的旧存储过程用例）。

## 7. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST_ERP514_`，
  且 `Integrated Security=true`；护栏在**任何数据库访问之前**校验失败即中止；
- 每次运行创建**全新的 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 不读取 `appsettings` / `.env` / 生产凭据；连接串只来自进程环境变量或专用 localdb 默认值；
- 测试种子一律使用 SQL Server 自增主键（不显式指定 Id），并复用种子数据中的既有菜单 / 单据号规则。

## 8. 环境证据

- 全部输出 / TRX 记录于 `.ai/logs/ERP-514/`（保留原始失败日志与首次诊断痕迹：真实 schema 为
  `db_owner.FinanceContainerSettlements`、原始 SQL 写入须使用独立 `DbContext` 两处失败均已如实留存）；
- 交付门禁（safe profile）实测：
  - `dotnet restore NEWERP.sln` → exit 0；
  - `dotnet build NEWERP.sln -c Release --no-restore --no-incremental /p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true`
    → **0 警告 / 0 错误 / exit 0**；
  - `dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build` → **8564 通过 / 0 失败 / 37 s**；
  - `dotnet test src/ERP.IntegrationTests -c Release --no-build --filter "FullyQualifiedName~ContainerSettlement"`
    → **37 通过 / 0 失败 / 3 跳过**（跳过的 3 个为既有旧存储过程用例，按既有设计跳过）；
  - `--filter "FullyQualifiedName~FinanceSettlementCanonicalEntrySqlServerTests"`（真实 Kestrel + 真实隔离库 +
    真实 JWT 身份的结算单规范入口）→ **19 通过 / 0 失败**；
  - `--filter "FullyQualifiedName~FinanceBulkSettlementLifecycle"` → **12 通过 / 0 失败**（散货结算单未受影响）。
- 环境阻塞（如实记录）：`FinanceSettlementCanonicalEntrySqlServerTests` 的夹具在**缺少显式测试上下文**时按既有
  `TestDatabaseSafetyGuard` fail closed（首次尝试 19 个用例全部因
  `缺少显式测试上下文（ERP_AI_TEST_RUN=1 且 ASPNETCORE_ENVIRONMENT=Development）` 中止，证据见
  `.ai/logs/ERP-514/erp514-sql-related.trx`）；随后按护栏说明的受控方式在进程内显式设置该测试上下文后重跑并通过。
  未连接生产数据库、未使用生产凭据、未读取 `appsettings*.json` / `.env` / 生产连接串。
- 说明：本文件的完成只代表 Release 构建（含分析器 / 警告即错误）与单元测试通过、装柜结算单相关真实 SQL 用例
  在受控 localdb 上真实执行；**构建完成不等于阶段验收完成**，全阶段验收由调度器 / 验收运行器按各自门禁执行。

