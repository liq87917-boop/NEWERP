# 形式发票 PI 商业条款校验（ERP-471）

## 1. 背景与目标

形式发票 PI 是「询价单 → 报价单 → PI → 销售订单」业务链上**收定金与开信用证**的金额凭据，
但此前的商业条款处理存在多处静默兜底（`ProformaInvoiceController.Normalize`）：

- `d.Quantity * d.UnitPrice` 与 `total` **未做任何合法性校验**，行金额与合计一律
  `Math.Round(..., 2)`（**固定两位小数**，无视币种精度：JPY 等无小数币种也被写成两位小数）；
- `e.ExchangeRate == 0 ? 1 : e.ExchangeRate` —— 汇率为 0 时**静默按 1 处理**，折人民币金额被无声改写；
- `if (e.DepositAmount <= 0) e.DepositAmount = depositByRatio;` —— **显式负定金被当作「未填写」**
  静默按比例重算，负值被无声抹掉；
- 提交 / 审核 / 转销售订单只做状态与来源资格复核，不复核**已持久化的商业条款**，
  非法历史数量 / 汇率因此可能成为实时履约并被传播成销售订单；
- 客户端提交的软删除明细行会被 `Normalize` 一并重排 / 重置主键后写入（软删除明细注入）。

ERP-471 把币种 / 汇率 / 数量 / 单价 / 金额 / 合计 / 定金收敛到**一份**应用层校验与整理契约，
被普通新增 / 修改、报价单转 PI 与提交 / 审核 / 转销售订单（下游承诺）共同复用，且全部发生在
**单号预约与任何写入之前**；`ProformaInvoiceController.Normalize` 保留为写入路径的稳定入口。

## 2. 唯一权威契约

`ProformaInvoiceCommercialValidationRules`（`src/ERP.Application/Services/ProformaInvoiceCommercialValidationRules.cs`）：

| 项 | 要求 |
|---|---|
| 币种 | `Currency` 枚举**已定义**值（不跨币种合计，未定义值一律拒绝） |
| 汇率 | `> 0` 且在汇率列实际精度 `DECIMAL(18,6)` 内可表示（**绝不**再静默按 1 处理） |
| 逐行数量 | `> 0` 且在数量列实际精度 `DECIMAL(18,4)` 内可表示（绝不把正数量静默舍入为 0） |
| 逐行单价 | `>= 0` 且在单价列实际精度 `DECIMAL(18,4)` 内可表示（保留既有「零单价」政策） |
| 明细注入 | 客户端明细中的软删除行（`IsDeleted = true`）**拒绝**，绝不静默写入 |
| 行金额 / 合计 | 由服务端按 `CurrencyAmountRules` 的币种精度（JPY / KRW / VND / IDR 为 0 位、其余 2 位、0.5 进位）从有效明细派生；`decimal` 溢出或超精度一律受控拒绝 |
| 折人民币合计 | `合计 × 汇率` 按 **CNY 精度（2 位）** 取整；放大后越界同样受控拒绝 |
| 定金比例 | `0 ~ 100`（含端点）且在定金比例列实际精度 `DECIMAL(18,4)` 内可表示 |
| 定金金额 | 显式 `0` = **保留既有「按比例自动计算」语义**（自动值同样按币种精度派生）；显式**负数一律拒绝**（绝不静默重算）；显式正数必须在币种精度内可表示且**不得超过权威总额** |

三种调用口径（同一份实现，不存在第二套算法）：

| 入口 | 允许空草稿 | 拒绝软删除注入 | 用途 |
|---|---|---|---|
| `ValidateIncomingDraft` / `NormalizeIncomingDraft` | 是（显式支持的空草稿仍可保存） | 是 | 新增 / 修改 / 带入预填后保存 |
| `ValidatePersisted` | 是（按**有效明细**校验，历史软删除行不是拒绝原因） | 否 | 已存记录复核 |
| `ValidateOperational` | 否（**至少一行有效明细**） | 否 | 提交 / 审核 / 转销售订单 |

任一项不满足一律抛出 `BusinessException.InvalidParameter`（受控业务错误）；校验本身**不改写任何字段**。

## 3. 唯一权威整理

`ProformaInvoiceController.Normalize` 整体委托 `ProformaInvoiceCommercialValidationRules.NormalizeIncomingDraft`：
先校验，再按数组顺序重排行号为 `1..n`、重置明细主键 / 外键、按**单据币种精度**派生行金额、
复核合计与折人民币，并**仅在定金为 0 时**按比例自动补齐定金。客户端提交的行号 / 行金额 / 合计 /
自动定金一律不被信任（显式正定金保留）。

## 4. 各路径接入点时序

| 路径 | 接入点 | 时序 |
|---|---|---|
| 新增 `POST /api/sales/proforma-invoices` | `ProformaInvoiceController.Create` | 授权 / 拟议客户范围 / 来源血缘与来源行锁 → **条款校验（含软删除注入检查）** → 单号预约 → 字段改写 → `Normalize` 复算 → 提交事务 |
| 修改 `PUT /api/sales/proforma-invoices/{id}` | `ProformaInvoiceController.Update` → `ApplyUpdate` | 授权 / 归属 / 下游冻结 / 状态资格 → PI 行锁与锁内权威重读 → 沿用持久化来源与定金比例口径 → **条款校验成功后才改写锁内权威实体** → `Normalize` 复算 |
| 提交 `POST /{id}/submit` | `ProformaInvoiceController.Submit` | 行锁内重读 → 下游 / 来源血缘 / 状态资格 → **已持久化条款校验（含至少一行有效明细）** → 置状态 |
| 审核 `POST /{id}/approve` | `ProformaInvoiceController.Approve` | 同上（无有效明细沿用既有 `RuleConflict` 口径，非法条款按 `InvalidParameter` 拒绝） |
| 转销售订单 `POST /{id}/to-order` | `ProformaInvoiceController.ToSalesOrder` | 行锁内重读 → 转换资格 / 既有来源订单 → **条款校验** → 构造订单 → 一致性复核 → 预约销售订单单号 |
| 带入预填 `GET /{id}/order-prefill` | `SalesOrderConversion.FromProformaInvoiceAsync` | 只读，但构造草稿前同样按契约复核（非法来源商业值连草稿也不产出） |
| 报价单转 PI `POST /api/sales/quotations/{id}/to-pi` | `QuotationController.ToProformaInvoice` | 报价单行锁内重读 → 报价单条款校验 → 构造 PI 草稿 → **PI 条款校验（含定金比例 / 金额）** → 预约 PI 单号 → `Normalize` |

被拒绝的请求：**表头、明细、来源单据、状态与单号预约全部不变**（新增 / 修改在单号预约与字段改写之前拒绝；
承诺路径在 `SaveChanges` 之前拒绝，事务整体回滚，行锁只刷新技术审计时间戳 `UpdatedAt`）。

## 5. 实际 EF 精度

PI 主表与明细（`db_owner.ProformaInvoices` / `db_owner.ProformaInvoiceDetails`）由权威升级脚本
（`SchemaUpgrader` 第 21 段）声明为：`ExchangeRate` 为 `DECIMAL(18,6)`，`TotalAmount` / `TotalAmountCny` /
`DepositRatio` / `DepositAmount` 与明细 `Quantity` / `UnitPrice` / `Amount` 为 `DECIMAL(18,4)`。

ERP-534 起 EF 模型（`ErpDbContext.Config.cs`）为上述八列**逐列显式声明 `HasPrecision`**，与建表脚本逐字一致，
契约以常量固化同一口径（`DecimalPrecision = 18`、`ExchangeRateScale = 6`、`AmountScale = 4`、
`MaxExchangeRate = 999_999_999_999.999999m`、`MaxAmount = 99_999_999_999_999.9999m`），
并由单元测试用 SQL Server 提供程序**仅构建模型**（不打开连接）逐列断言与常量同源；
详见 `docs/proforma-invoice-persisted-numeric-bounds.md`。修复前该模型未声明精度、按 EF 默认
`decimal(18,2)` 映射，导致合法的六位小数汇率与四位小数数量 / 单价 / 定金被错误拒绝或在写入前被静默取整。

## 6. 边界（不做的事）

- 不新增 / 修改表、列、索引、菜单、角色或用户授权，不改动任何数据库结构；
- 不做历史数据回填 / 自动修正：`GetById` / `GetPrint` / 列表 / 报表等只读路径仍原样返回持久化值，
  非法历史条款仍可读、不被改写（提交 / 审核 / 转换只**拒绝**，不修正）；
- 不重置主键、不重建身份、不为「校验一条既有记录」而重写历史快照；
- 不伪造授权，不把空身份当作匿名或管理员；行锁 / 事务 / 行锁内权威重读沿用既有 ERP-399 口径；
- 不触碰库存、财务、客户主数据与来源报价单的商业字段（报价转 PI 只按既有语义把来源置「已完成」）；
- 不新增迁移、不读取 `appsettings*.json` / `.env` / 生产凭据。

## 7. 验收证据

| 类型 | 文件 | 覆盖 |
|---|---|---|
| 单元（新增） | `src/ERP.UnitTests/ProformaInvoiceCommercialValidationTests.cs` | 契约边界（数量 / 单价 / 币种 / 汇率 / 软删除注入 / 空草稿 / 溢出 / 定金比例 · 金额 · 负数 · 超精度 · 超总额）、JPY 与普通币种取整、客户端篡改金额 / 合计被覆盖、零定金自动计算与显式正定金保留、EF 精度契约、控制器新增 / 修改零写入零占号、提交 / 审核 / 转订单原子拒绝、合法单据全流程正常 |
| 单元（适配） | `src/ERP.UnitTests/ProformaInvoiceControllerTests.cs` | 显式负定金受控拒绝、零汇率受控拒绝且不消耗单号、JPY 0 位小数派生 |
| 单元（扩展） | `src/ERP.UnitTests/QuotationToPiTests.cs` / `SalesOrderConversionTests.cs` / `ProformaInvoiceMutationTests.cs` | 报价转 PI 不传播非法来源定金比例（零 PI、来源状态与占号不变）、报价单 JPY 转 PI 0 位小数派生；PI 带入预填 / 转换对非法已存条款受控拒绝且不落库；控制器接线与「校验先于单号预约」断言 |
| 集成（真实 SQL） | `src/ERP.IntegrationTests/ProformaInvoiceCommercialValidationSqlServerTests.cs` | 专用 localdb 上的真实控制器：非法条款零写入零占号、伪造合计被重算且 `Σ 明细 == 合计` 精确落库、JPY 0 位小数落库、显式正定金保留 / 零定金自动计算、修改非法明细原表头明细不变、提交 / 审核 / 转订单非法历史条款原子拒绝、合法 PI 全流程订单与来源一致、报价转 PI 不传播非法来源商业值、目标护栏 fail-closed |

- 安全 profile：`dotnet restore` → Release 构建（`TreatWarningsAsErrors=true` + `RunAnalyzersDuringBuild=true`）→
  `ERP.UnitTests` 全量；
- 真实 SQL 夹具安全口径：实例必须精确为 `(localdb)\NEWERP_AutoAcceptance`、库名前缀必须为 `NEWERP_AUTOTEST`、
  必须为 `Integrated Security`；每次运行只创建一个**全新 GUID 后缀库**，发现同名库已存在立即拒绝；
  **绝不 drop / reset / 复用**任何数据库，只新增自己的夹具行；
- **构建完成不等于阶段验收**：真实 SQL 用例只有在受控 localdb 上真实执行通过才构成验收证据；
  浏览器验收按任务配置为 `browser_acceptance.required = false`：不运行真实 Edge / UI / 截图验收。
