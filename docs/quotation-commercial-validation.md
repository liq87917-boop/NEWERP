# 报价单商业条款校验（ERP-470）

## 1. 背景与目标

报价单是「询价单 → 报价单 → PI → 销售订单」业务链的金额中枢，但此前的商业条款处理存在多处静默兜底：

- `QuotationLineRules.Normalize` 接受**负 / 零数量、负单价**，并把行金额与合计硬编码为
  `Math.Round(..., 2)`（**固定两位小数**，无视币种精度：JPY 等无小数币种也被写成两位小数）；
- `e.ExchangeRate == 0 ? 1 : e.ExchangeRate` —— 汇率为 0 时**静默按 1 处理**，报价折人民币金额因此被无声改写；
- `InquiryQuotationConversion` 另写一套 `Recalculate`（同样是固定两位小数），并再次
  `inquiry.ExchangeRate > 0 ? inquiry.ExchangeRate : 1m` **静默替换汇率**；
- 提交 / 审核 / 转 PI / 转销售订单只做状态与来源资格复核，不复核**已持久化的商业条款**，
  非法历史数量因此可能成为实时履约；
- 客户端提交的软删除明细行会被 `Normalize` 一并重排 / 重置主键后写入（软删除明细注入）。

ERP-470 把币种 / 汇率 / 数量 / 单价 / 金额 / 合计收敛到**一份**应用层校验与整理契约，
被普通新增 / 修改、询价转报价、版本复制与提交 / 审核 / 转换（下游承诺）共同复用，且全部发生在
**单号预约与任何写入之前**。

## 2. 唯一权威契约

`QuotationCommercialValidationRules`（`src/ERP.Application/Services/QuotationCommercialValidationRules.cs`）：

| 项 | 要求 |
|---|---|
| 币种 | `Currency` 枚举**已定义**值（不跨币种合计，未定义值一律拒绝） |
| 汇率 | `> 0` 且在**汇率列实际精度** `DECIMAL(18,6)` 内可表示（最多 6 位小数、量级有界；**绝不**再静默按 1 处理，也绝不静默取整） |
| 逐行数量 | `> 0` 且在**数量列实际精度** `DECIMAL(18,4)` 内可表示（最多 4 位小数、量级有界；绝不把正数量静默舍入为 0） |
| 逐行单价 | `>= 0` 且在**单价列实际精度** `DECIMAL(18,4)` 内可表示（最多 4 位小数、量级有界；保留既有「零单价」政策） |
| 明细注入 | 客户端明细中的软删除行（`IsDeleted = true`）**拒绝**，绝不静默写入 |
| 行金额 / 合计 | 由服务端按 `CurrencyAmountRules` 的币种精度（JPY / KRW / VND / IDR 为 0 位、其余 2 位、0.5 进位）从有效明细派生；`decimal` 溢出或超出 `DECIMAL(18,4)` 金额列容量一律受控拒绝 |
| 折人民币合计 | `Σ 行金额 × 汇率` 按 **CNY 精度（2 位）** 取整；放大后越界同样受控拒绝 |

三种调用口径（同一份实现，不存在第二套算法）：

| 入口 | 允许空草稿 | 拒绝软删除注入 | 用途 |
|---|---|---|---|
| `ValidateIncomingDraft` / `NormalizeIncomingDraft` | 是（显式支持的空草稿仍可保存） | 是 | 新增 / 修改 / 带入预填后保存 |
| `ValidatePersisted` | 是（按**有效明细**校验，历史软删除行不是拒绝原因） | 否 | 版本复制的源版本复核 |
| `ValidateOperational` | 否（**至少一行有效明细**） | 否 | 提交 / 审核 / 转 PI / 转销售订单 |

任一项不满足一律抛出 `BusinessException.InvalidParameter`（受控业务错误）；校验本身**不改写任何字段**。

## 3. 唯一权威整理

`QuotationLineRules.Normalize`（报价单写入路径的稳定入口）整体委托
`QuotationCommercialValidationRules.NormalizeIncomingDraft`：先校验，再按数组顺序重排行号为 `1..n`、
重置明细主键 / 外键、按**单据币种精度**派生行金额、复核合计与折人民币，并补齐报价日期 / 有效期默认值。
客户端提交的行号 / 行金额 / 合计一律不被信任。

`InquiryQuotationConversion.BuildDraftAsync` 删除 `Recalculate` 的第二套算法与汇率静默替换，改为调用同一契约；
`QuotationRevisionService.CreateRevisionAsync` 在复制前以 `ValidatePersisted` 复核源版本，并继续以
`QuotationLineRules.Normalize` 对**新版本**复算（源版本一行不改）。

## 4. 各路径接入点时序

| 路径 | 接入点 | 时序 |
|---|---|---|
| 新增 `POST /api/sales/quotations` | `QuotationController.Create` | 授权 → 拟议客户范围 → **条款校验** → 来源解析 / 加锁 → 单号预约 → 字段改写 → `Normalize` 复算 → 提交事务 |
| 修改 `PUT /api/sales/quotations/{id}` | `QuotationController.Update` | 授权 / 归属 / 版本只读 / 下游冻结 / 状态资格 → **条款校验** → 开事务 → 行锁与锁内权威重读 → 改写 → `Normalize` 复算 |
| 提交 `POST /{id}/submit` | `QuotationController.Submit` | 行锁内重读 → 下游 / 来源血缘 / 状态资格 → **已持久化条款校验（含至少一行有效明细）** → 置状态 |
| 审核 `POST /{id}/approve` | `QuotationController.Approve` | 同上（无有效明细沿用既有 `RuleConflict` 口径，非法条款按 `InvalidParameter` 拒绝） |
| 转 PI `POST /{id}/to-pi` | `QuotationController.ToProformaInvoice` | 行锁内重读 → 转换资格 / 既有来源 PI → **条款校验** → 构造 PI → 预约 PI 单号 |
| 转销售订单 `POST /{id}/to-order` | `QuotationController.ToSalesOrder` | 行锁内重读 → 转换资格 / 既有来源单据 → **条款校验** → 构造订单 → 预约订单单号 |
| 询价转报价 `POST /api/inquiries/{id}/to-quotation`、带入预填 | `InquiryQuotationConversion.BuildDraftAsync` | 询问单行锁内重读 → 转换资格 → **按契约校验来源条款并派生金额** → 预约报价单单号 |
| 创建版本 `POST /{id}/revisions` | `QuotationRevisionService.CreateRevisionAsync` | 源行锁内重读 → 状态资格 → **源版本条款复核** → 版本号 / 单号分配 → 复制 + `Normalize` 复算 |

被拒绝的请求：**表头、明细、来源单据、状态与单号预约全部不变**（新增 / 修改在事务与行锁之前拒绝；
承诺路径在 `SaveChanges` 之前拒绝，事务整体回滚，行锁只刷新技术审计时间戳 `UpdatedAt`）。

## 5. 实际 EF 精度（ERP-533：与声明列对齐）

报价单主表与明细（`db_owner.Quotations` / `db_owner.QuotationDetails`）由权威升级脚本（`SchemaUpgrader` 第 20 段）
声明为：`ExchangeRate DECIMAL(18,6)`、`TotalAmount` / `TotalAmountCny DECIMAL(18,4)`、
`Quantity` / `UnitPrice` / `Amount DECIMAL(18,4)`。
EF 模型（`ErpDbContext.Config.cs`）现已按 ERP-533 对上述六列显式声明 `HasPrecision(18, 6)` / `HasPrecision(18, 4)`，
与建表脚本逐字一致；契约以常量固化该口径（`DecimalPrecision = 18`、`ExchangeRateScale = 6`、`AmountScale = 4`、
`MaxExchangeRate = 999_999_999_999.999999`、`MaxAmount = 99_999_999_999_999.9999`），
并由单元测试用 SQL Server 提供程序**仅构建模型**（不打开连接）逐列断言与常量同源，
真实列精度另由专用 localdb 上的 `INFORMATION_SCHEMA` 集成用例确认。

## 6. 边界（不做的事）

- 不新增 / 修改表、列、索引、菜单、角色或用户授权，不改动任何数据库结构；
- 不做历史数据回填 / 自动修正：`GetById` / `GetPrint` / 有效期提醒 / 报表等只读路径仍原样返回持久化值，
  非法历史条款仍可读、不被改写（提交 / 审核 / 转换只**拒绝**，不修正）；
- 不重置主键、不重建身份、不为「校验一条既有记录」而重写历史快照；
- 不伪造授权，不把空身份当作匿名或管理员；行锁 / 事务 / 行锁内权威重读沿用既有 ERP-400 口径；
- 不触碰库存、财务、客户主数据与来源询价单的商业字段（询价转报价只按既有语义把来源置「已完成」）；
- 不新增迁移、不读取 `appsettings*.json` / `.env` / 生产凭据。

## 7. 验收证据

| 类型 | 文件 | 覆盖 |
|---|---|---|
| 单元（新增） | `src/ERP.UnitTests/QuotationCommercialValidationTests.cs` | 契约边界（数量 / 单价 / 币种 / 汇率 / 软删除注入 / 空草稿 / 溢出 / 最大可表示边界）、JPY 与普通币种取整、客户端篡改合计被覆盖、EF 精度契约、控制器新增 / 修改零写入零占号、提交 / 审核 / 转 PI / 转订单原子拒绝、合法单据正常流转 |
| 单元（适配） | `src/ERP.UnitTests/QuotationCrudRegressionTests.cs` | 既有「零汇率静默按 1」失效夹具改为「受控拒绝 + 零写入」，日期 / 金额默认值场景改用合法汇率 |
| 单元（扩展） | `src/ERP.UnitTests/QuotationMutationTests.cs` / `QuotationRevisionTests.cs` / `QuotationGovernanceTests.cs` / `InquiryQuotationConversionTests.cs` | 契约覆盖全部写入 / 承诺路径的接线断言；非法源版本创建版本零新版本；历史非法条款打印照原样且提交不回填；询价转报价来源条款非法 / 零汇率拒绝与 JPY 派生 |
| 集成（真实 SQL） | `src/ERP.IntegrationTests/QuotationCommercialValidationSqlServerTests.cs` | 专用 localdb 上的真实控制器：非法条款零写入零占号、伪造合计被重算且 `Σ 明细 == 合计` 精确落库、JPY 0 位小数落库、提交 / 审核 / 转 PI / 转订单非法历史条款原子拒绝、询价转报价 JPY 派生与来源快照不变、目标护栏 fail-closed |
| 单元（ERP-533 新增） | `src/ERP.UnitTests/QuotationPersistedNumericBoundsTests.cs` | 声明列精度与常量同源（EF 模型）、六位小数汇率 / 四位小数数量 · 单价与量级边界放行、第 7 位小数汇率 / 第 5 位小数数量 · 单价与量级越界拒绝、服务端派生金额与半进位越界拒绝、新增 / 修改 / 提交 / 转 PI 零写入零占号与原证据不变、源码契约 |
| 集成（ERP-533 真实 SQL） | `src/ERP.IntegrationTests/QuotationPersistedNumericBoundsSqlServerTests.cs` | 专用 localdb 上的真实控制器：`INFORMATION_SCHEMA` 确认 `decimal(18,6)` / `decimal(18,4)`、六位小数汇率与四位小数数量 · 单价（含声明列上限）以 `CONVERT(varchar, ...)` 原样往返、第 7 位小数 / 第 5 位小数 / 量级越界零行零占号、被拒修改不改写权威行、目标护栏 fail-closed |

- 安全 profile：`dotnet restore` → Release 构建（`TreatWarningsAsErrors=true` + `RunAnalyzersDuringBuild=true`）→
  `ERP.UnitTests` 全量；
- 真实 SQL 夹具安全口径：实例必须精确为 `(localdb)\NEWERP_AutoAcceptance`、库名前缀必须为 `NEWERP_AUTOTEST`、
  必须为 `Integrated Security`；每次运行只创建一个**全新 GUID 后缀库**，发现同名库已存在立即拒绝；
  **绝不 drop / reset / 复用**任何数据库，只新增自己的夹具行；
- **构建完成不等于阶段验收**：真实 SQL 用例只有在受控 localdb 上真实执行通过才构成验收证据；
  浏览器验收按任务配置为 `browser_acceptance.required = false`：不运行真实 Edge / UI / 截图验收。
