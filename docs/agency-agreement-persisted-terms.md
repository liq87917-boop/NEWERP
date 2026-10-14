# 代理服务费协议显式固定金额存储边界与登记前持久化条款复核（ERP-516）

## 1. 目标

把既有代理服务费协议证据（`AgencyServiceFeeAgreement`，ERP-069 / ERP-439）的**显式固定金额写入路径**与
**登记（草稿 → 已登记）冻结路径**收敛到费用条款列的**实际存储精度**：

1. 费率路径此前已把 `RatePercent` 收敛到 `(0, 100]` 并按 4 位小数（0.5 进位）取整（与 `DECIMAL(9,4)` 同源），
   但**固定金额路径**只做「按币种精度取整后必须大于 0」，**没有任何标量存储上界**：极大正金额
   （`1e16`、`9_999_999_999_999_999.995` 取整进位后的 `1e16`、`decimal.MaxValue`）能通过应用层检查，
   却会在写入 SQL 时被截断 / 溢出，或被驱动直接拒绝；
2. `FixedAmount` 列在 EF 模型中**显式** `HasPrecision(18,2)`（16 位整数 + 2 位小数）；
3. 登记此前只做状态机与登记人冻结，**没有**复核「已持久化费用条款」是否仍按权威口径原样可存储 ——
   过精度（例如旁路 SQL 写入的 3 位小数）会被静默取整，而不是按「不可原样存储」拒绝。

只补齐**固定金额标量存储边界**与**登记前只读一致性复核**，**不**改费率口径、计费方式互斥、币种口径、
客户 / 日期 / 唯一身份、状态机、授权、历史读取 / 作废语义与审计。

## 2. 精度口径（与实际 EF 存储同源）

| 列 | 实际存储 | 规则常量 / 来源 |
|---|---|---|
| `AgencyServiceFeeAgreement.FixedAmount` | `DECIMAL(18,2)`（EF 模型显式 `HasPrecision(18,2)`） | `FinancePersistedAmountRules.DecimalPrecision = 18`、`DecimalScale = 2`、`MaxDecimal18Scale2 = 9_999_999_999_999_999.99` |
| `AgencyServiceFeeAgreement.RatePercent` | `DECIMAL(9,4)`（EF 模型显式 `HasPrecision(9,4)`） | `AgencyServiceFeeAgreementRules.RateDecimals = 4`、`MaxRatePercent = 100` |

- 边界常量与真实列类型逐字同源：单元测试以 SQL Server 提供程序**仅构建模型**断言
  `IProperty.GetColumnType() == "decimal(18,2)"` / `"decimal(9,4)"`；集成测试再以真实
  `INFORMATION_SCHEMA.COLUMNS` 与 SQL Server 真实列写入（算术溢出 / 驱动拒绝）双重确认；
- 取整口径**原样**复用既有 `CurrencyAmountRules.RoundAmount`（`MidpointRounding.AwayFromZero`，
  零小数币种 `JPY / KRW / VND / IDR` 为 0 位、其余含未知币种为 2 位），守卫只在**取整之后**追加区间判定；
- 守卫**只**作用于「单个显式固定金额」：绝不用于 `CurrencyAmountRules.RoundAmount` 本身、只读权威金额
  或聚合报告合计（求和可能合法地超过单列容量），也不做汇率换算、不跨币种合并。

## 3. 护栏口径

| 项 | 口径 |
|---|---|
| 校验时点（写入） | 新增 / 修改在**校验阶段、任何已跟踪写入之前**完成归一化与边界判定；被拒请求零协议写入、零身份消耗 |
| 校验时点（登记） | 状态机资格（草稿）与客户范围之后、**冻结点状态 / 登记人 / 时间戳之前**只读复核已持久化条款 |
| 边界判定 | 取整后的固定金额必须满足 `decimal.Round(v,2) == v` 且 `MinDecimal18Scale2 <= v <= MaxDecimal18Scale2`，否则受控业务错误（`InvalidParameter`，文案含 `DECIMAL(18,2)`） |
| 取整进位越界 | USD `9_999_999_999_999_999.995` / `9_999_999_999_999_999.999` 取整为 `1e16`、JPY `9_999_999_999_999_999.6` 取整为 `1e16`：均在**写入之前**拒绝 |
| 精确上限放行 | 恰好 `9_999_999_999_999_999.99`（USD / CNY）与 `9_999_999_999_999_999`（JPY）正常落库并在真实列上原样往返 |
| 最小正数 | 取整后为 0 的金额仍按既有「必须大于 0」拒绝（`0.005 USD → 0.01` 放行） |
| 登记复核 | 已持久化 `FeeMethod` 必须显式受支持且口径互斥；费率必须 `(0,100]` 且 4 位小数可原样存储；固定金额必须按已持久化币种精度取整后 `> 0` 且可在 `DECIMAL(18,2)` 内原样存储；任何差异即受控拒绝 |
| 拒绝语义 | 登记被拒时协议、来源引用与审计**逐字段不变**：状态仍为草稿、`RecordedAt` / `RecordedBy` / `VoidedAt` / `VoidReason` / `UpdatedAt` 均不变 |
| 撤销语义 | **历史读取 / 作废不调用**登记闸门：非法存量条款仍可按既有规则显式作废，且**不**做任何回填 / 静默修复 |
| 不推断口径 | 费用条款只来自用户显式提交值；绝不从 `SalesCommissionRate`、客户 / 供应商主数据比例、历史订单、自由文本或金额相似度推断或补齐 |

## 4. 实现

- `src/ERP.Application/Services/AgencyServiceFeeAgreementRules.cs`
  - `ValidateFeeTerms` 固定金额分支：既有「按币种精度取整 + 大于 0」之后复用既有标量守卫
    `FinancePersistedAmountRules.EnsureRepresentable(roundedAmount, FixedAmountLabel)`（**不**新增第二套边界口径）；
  - 新增 `FixedAmountLabel` 与 `StoredFeeTermsInconsistentText` 常量（接口 / 界面 / 文档同源）；
  - 新增 `EnsureStoredFeeTermsConsistent(AgencyServiceFeeAgreement)`：**只读**把已持久化计费方式 / 费率 / 固定金额
    重新过一遍与新增 / 修改**完全相同**的 `ValidateFeeTerms`，并要求存储值与权威重算值逐字相等，
    否则抛受控 `InvalidParameter`（绝不写库、绝不改写入参）；
  - `FeeTermsRuleText` 同步补充固定金额实际列精度口径。
- `src/ERP.Application/Services/AgencyServiceFeeAgreementService.cs`
  - `RecordAsync`：在既有 `EnsureRecordable`（状态机）之后、**写入状态 / 登记人 / 时间戳之前**调用
    `EnsureStoredFeeTermsConsistent`；类文档同步。
- `src/ERP.Api/Controllers/AgencyServiceFeeAgreementController.cs`
  - 仅类 / 路由文档同步（授权、范围、事务与调用点**字面不变**）。
- `src/ERP.Application/Services/FinancePersistedAmountRules.cs`：**未改动**（直接复用既有标量守卫）。
- `src/ERP.Domain` / `ErpDbContext` / `SchemaUpgrader`：**未改动**（`FixedAmount DECIMAL(18,2)` / `RatePercent DECIMAL(9,4)`
  已是既有列定义，本任务不新增 / 不修改 schema）。

## 5. 不改写项

- 不新增 / 不修改任何表、列、索引、schema、迁移或种子数据；不新增权限、菜单、角色、用户授权；
- 不改费率 (0,100] 与 4 位小数口径、计费方式互斥、币种支持范围、客户可用性、生效日期区间、唯一身份判定；
- 不改实时身份 / 菜单授权 / 客户数据范围、确定性事务与锁、受控错误码与审计；
- 不把单列边界施加到 `CurrencyAmountRules.RoundAmount`、只读权威金额比对或聚合报表合计；
- 不推断任何费用条款，不自动发布服务对账单 / 收款 / 记账，不产生发票 / 付款 / 结算 / 法律动作；
- 不静默修复历史快照：存量非法条款在登记被拒的同时原样保留，历史读取与显式作废语义不变。

## 6. 测试

- 新增单元测试 `src/ERP.UnitTests/AgencyAgreementPersistedTermsTests.cs`（内存库 + 真实 HTTP 身份）：
  存储常量与实际列精度同源、全局取整不扩散单列上限、精确上限与半进位放行、取整进位越界 / `1e16` /
  `decimal.MaxValue` / 零小数币种进位越界受控拒绝、既有正数与互斥口径不变、`EnsureStoredFeeTermsConsistent`
  合法放行与非法 / 精度不符拒绝、复核纯只读不改写入参、创建 / 修改越界拒绝（零写入、逐字段审计不变）、
  登记前复核（非法存量行被拒且状态 / 审计零变更）、合法登记与历史读取 / 作废照常、未认证先于条款复核 fail closed。
- 新增真实 SQL 集成测试 `src/ERP.IntegrationTests/AgencyAgreementPersistedTermsSqlServerTests.cs`
  （GUID 独占 `NEWERP_AUTOTEST_ERP516_*` 目标）：真实列精度（EF 模型 + `INFORMATION_SCHEMA`）、
  真实列自身的小数位取整行为、精确上限原样往返、旁路守卫的原始 EF 写入被真实列 / 驱动拒绝、
  创建 / 修改越界拒绝且零写入（同一受权控制器随后合法创建成功）、真实 SQL 存量非法条款登记被拒且零变更、
  合法固定 / 费率协议「创建 → 登记 → 读取 → 作废」原样持久化，以及非专用目标库在访问前 fail closed。
- 既有相关测试全部保持原断言并通过：`AgencyServiceFeeAgreementTests`（含模型 / Schema 契约）、
  `AgencyServiceFeeStatementTests`、`AgencyServiceFeeCollectionAllocationTests`、`AgencyServiceFeeReconciliationTests`、
  `AgencyServiceFeeMonthlySummaryTests`，以及真实 SQL 回归 `AgencyServiceFeeAgreementAuthorizationSqlServerTests`（ERP-439）
  与 `AgencyServiceFeeRegisterAuthorizationSqlServerTests`（ERP-437）；本次改动未要求修改任何既有夹具断言
  （既有夹具已使用真实菜单 / 角色 / 启用账号 / 真实客户主数据，无需替换为占位身份）。

## 7. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST_ERP516_`，
  且 `Integrated Security=true`；护栏在**任何数据库访问之前**校验失败即中止；
- 每次运行创建**全新的 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 不读取 `appsettings` / `.env` / 生产凭据；连接串只来自进程环境变量或专用 localdb 默认值；
- 测试种子一律使用 SQL Server 自增主键（不显式指定 Id），并复用种子数据中的既有「客户资料」菜单与真实客户主数据。

## 8. 环境证据

- 全部输出 / TRX 记录于 `.ai/logs/ERP-516/`（未做删改）：
  - `erp516-build.log`：`dotnet build NEWERP.sln -c Release --no-restore --no-incremental
    /p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true` → **0 警告 / 0 错误 / 成功**；
  - `erp516-units.log` + `erp516-units.trx`（另含 `.out.log` / `.err.log` / `.pid`）：
    `dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build` →
    **8628 通过 / 0 失败 / 0 跳过**（较 ERP-515 的 8597 净增 31 个用例，即本任务新增单元测试）；
  - `erp516-sql-terms.log` + `erp516-sql-terms.trx`：
    `--filter "FullyQualifiedName~AgencyAgreementPersistedTerms"` → **22 通过 / 0 失败 / 0 跳过**
    （真实 localdb + GUID 独占 `NEWERP_AUTOTEST_ERP516_*` 库，每次运行新建库）；
  - `erp516-sql-agency-regression.log` + `erp516-sql-agency-regression.trx`：
    `--filter "FullyQualifiedName~AgencyServiceFee"` → **15 通过 / 0 失败 / 0 跳过**
    （既有 ERP-439 / ERP-437 代理服务费协议「范围 / 条款 / 登记 / 作废」真实 SQL 回归）；
  - `erp516-attempt1-notes.md`：**首次**真实 SQL 集成运行的失败记录（如实说明）。
- 首次运行的失败均为**测试脚手架缺陷**（`CreatedId` 泛型断言类型不一致、旁路写入用例误用绝对计数 `0`），
  已修复并重跑通过；原始失败输出与 TRX 因**紧随其后的重跑被 VSTest 覆盖**而未能保留，已在
  `erp516-attempt1-notes.md` 中按当时提取到的原始断言消息与堆栈逐条复述（未做美化或删减），
  详见该文件（本任务未遇到环境阻塞：localdb `NEWERP_AutoAcceptance` 可用，真实 SQL 用例全部真实执行通过）。
- 说明：本文件的完成只代表 Release 构建（含分析器 / 警告即错误）、全量单元测试与代理服务费协议相关真实 SQL
  用例在受控 localdb 上真实执行通过；**构建完成不等于阶段验收完成**，全阶段验收由调度器 / 验收运行器按各自门禁执行。
- 本任务未执行浏览器 / UI 验收（`browser_acceptance.required = false`），未做部署、生产数据库变更或破坏性数据操作，
  未新增权限 / schema / 迁移 / 种子数据，未读取 `.env*` / `appsettings*.json` / 生产连接串。
