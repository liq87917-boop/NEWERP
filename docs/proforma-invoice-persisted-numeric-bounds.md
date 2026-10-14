# 形式发票 PI 持久化数值精度（汇率 / 数量 / 单价 / 金额 · 合计 · 定金，ERP-534）

形式发票 PI（`/api/sales/proforma-invoices`）是「询价单 → 报价单 → 形式发票 PI → 销售订单」销售链上
收定金与开信用证的金额凭据，也是报价单转 PI、定金收取与转销售订单流程共同解析的权威对象。其汇率
（`ExchangeRate`）、数量（`Quantity`）、单价（`UnitPrice`）、行金额（`Amount`）、总额（`TotalAmount`）、
折人民币总额（`TotalAmountCny`）、定金比例（`DepositRatio`）与定金金额（`DepositAmount`）由权威升级脚本
（`SchemaUpgrader` 第 21 段）声明为：

| 表 | 列 | 声明类型 |
|---|---|---|
| `db_owner.ProformaInvoices` | `ExchangeRate` | `DECIMAL(18,6)` |
| `db_owner.ProformaInvoices` | `TotalAmount` / `TotalAmountCny` / `DepositRatio` / `DepositAmount` | `DECIMAL(18,4)` |
| `db_owner.ProformaInvoiceDetails` | `Quantity` / `UnitPrice` / `Amount` | `DECIMAL(18,4)` |

## 1. 问题（本轮修复）

- EF 模型（`ErpDbContext.Config.cs`）此前**未**为 `ProformaInvoice` / `ProformaInvoiceDetail` 声明任何
  `HasPrecision`（该处只有 PI 单号唯一索引与明细外键），因此按 SQL Server 默认精度把上述八列映射为
  `decimal(18,2)`；
- `ProformaInvoiceCommercialValidationRules` 把该错误假设固化为常量（`DecimalPrecision = 18`、
  `DecimalScale = 2`、`MaxDecimal = 9_999_999_999_999_999.99m`）与 `IsRepresentable`（按 2 位小数判定），
  于是**声明列本可逐字存储**的六位小数汇率（如 `7.123456`）与四位小数定金比例（如 `30.1250`）被**错误拒绝**；
- 反向风险同样存在：任何绕过契约、带 3 / 4 位小数到达 EF 的值，会被 `decimal(18,2)` 映射**静默取整**。

## 2. 修复

| 位置 | 变更 |
|---|---|
| `src/ERP.Infrastructure/Data/ErpDbContext.Config.cs` | 为 `ProformaInvoice.ExchangeRate` 显式声明 `HasPrecision(18, 6)`；`ProformaInvoice.TotalAmount` / `TotalAmountCny` / `DepositRatio` / `DepositAmount` 与 `ProformaInvoiceDetail.Quantity` / `UnitPrice` / `Amount` 显式声明 `HasPrecision(18, 4)`（与建表脚本逐字一致） |
| `src/ERP.Application/Services/ProformaInvoiceCommercialValidationRules.cs` | 以 `ExchangeRateScale = 6` / `AmountScale = 4` / `MaxExchangeRate` / `MaxAmount` 取代原来的 2 位小数口径；`IsRepresentable` 改按 `DECIMAL(18,4)` 判定，新增 `IsExchangeRateRepresentable` 按 `DECIMAL(18,6)` 判定；汇率校验与逐行数量 / 单价 / 行金额、合计、定金比例 / 金额的文案同步为声明精度 |

- **只改映射与标量接受口径**：不新增 / 修改任何表、列、索引、菜单、权限、用户授权，不改任何列类型与 DDL，
  不读取环境变量，实体数据注解不变（精度只属于 fluent 映射）；
- 合法的六位小数汇率与四位小数数量 / 单价（含声明列上限 `999_999_999_999.999999` /
  `99_999_999_999_999.9999`）在真实列上**原样往返**，不再被静默取整；
- `CurrencyAmountRules` 的币种精度（JPY / KRW / VND / IDR 0 位、其余 2 位、0.5 进位）与
  「行金额 / 合计 / 折人民币一律由服务端从有效明细派生」的口径**完全不变**；显式正定金仍必须符合**币种精度**
  且不得超过权威总额，零定金仍按比例自动计算。

## 3. 护栏口径（共享契约的三种调用口径）

| 路径 | 护栏 | 时序 |
|---|---|---|
| 新增 `POST /api/sales/proforma-invoices` | `ValidateIncomingDraft` / `NormalizeIncomingDraft` | 授权 / 客户范围 / 来源血缘之后、单号预约与任何字段改写之前 |
| 修改 `PUT /{id}` | 锁内权威重读 → `ValidateIncomingDraft` | 改写锁内权威实体之前 |
| 已存记录复核 | `ValidatePersisted` | 只校验，不改写 |
| 提交 / 审核 / 转销售订单（下游承诺） | `ValidateOperational` | 既有行锁内、状态改写与任何写入之前 |
| 报价单转 PI `POST /api/sales/quotations/{id}/to-pi` | `ValidateIncomingDraft`（锁内构造 PI 草稿后） | 预约 PI 号与写入之前 |

判定口径：

> 汇率：`> 0` 且在小数位 6 位、量级 `|x| ≤ 999_999_999_999.999999` 内可逐字表示；
> 数量：`> 0`；单价：`≥ 0`；数量 / 单价 / 行金额 / 合计 / 折人民币 / 定金比例 / 定金金额均在小数位 4 位、
> 量级 `|x| ≤ 99_999_999_999_999.9999` 内可逐字表示。

任一项不满足即以既有受控错误 `InvalidParameter` 拒绝，**绝不静默取整 / 截断 / 夹取**；被拒绝的请求
零 `ProformaInvoice` / `ProformaInvoiceDetail` 写入、零单号消耗、零状态变更、零下游单据。

## 4. 不改写项

- 不新增 / 不修改任何表、列、索引、schema、迁移或种子数据；不新增权限、菜单、角色、用户授权；
- 不改路由、菜单、ERP-097 业务员数据范围、软删除明细注入拒绝、运营承诺「至少一行有效明细」、
  `Currency` 枚举与单币种、客户端行金额 / 合计由服务端覆盖、来源血缘与锁协议；
- 不改币种取整口径与定金语义（零定金自动计算、定金不超过总额、显式负定金拒绝）；
- 不改 PI 报表路由 / 模板，不做历史数据回填或自动修正（历史读取 / 打印照原样返回持久化值）；
- 不读取 `appsettings*.json` / `.env` / 生产凭据，不触碰生产环境。

## 5. 单元测试（`src/ERP.UnitTests/ProformaInvoicePersistedNumericBoundsTests.cs`，内存库 + 真实控制器）

| 用例 | 断言 |
|---|---|
| 实际 EF 列精度 | SQL Server 提供程序**仅构建模型**：`ExchangeRate` 为 `decimal(18,6)`，其余七列为 `decimal(18,4)`（不连接数据库） |
| 可表示性纯判定（`[Theory]` × 4） | 4 / 6 位小数与量级边界放行；第 5 / 第 7 位小数与量级越界拒绝 |
| 契约边界 | `7.123456` 汇率与 `0.0125` 单价被原样接受且金额按币种精度派生；汇率 / 数量 / 单价声明列上限接受；四位小数定金比例 `30.1250` 接受并自动派生定金；半进位取整恰好落在可接受上界放行、越过上界拒绝 |
| 新增 / 修改（`[Theory]` + `[Fact]`） | 5 组超精度 / 越界请求零写入、零占号；被拒修改逐字段不变 |
| 承诺（`[Fact]` × 2） | 已持久化第 7 位小数汇率 / 第 5 位小数数量在提交 / 转销售订单时原子拒绝且状态 / 下游 / 单号预约不变 |
| 源码契约 | EF 映射八列 `HasPrecision` 与规则常量逐字同源，且契约校验先于单号预约与写入 |

既有 `ProformaInvoiceCommercialValidationTests`（边界矩阵与 EF 精度契约适配）、`ProformaInvoiceMutationTests`、
`ProformaInvoiceControllerTests`、`QuotationToPiTests`、`SalesOrderConversionTests` 等照常通过。

## 6. 真实 SQL Server 集成测试（`src/ERP.IntegrationTests/ProformaInvoicePersistedNumericBoundsSqlServerTests.cs`）

- 以 EF 模型与真实 `INFORMATION_SCHEMA` 双向确认八列精度；
- 六位小数汇率（`7.123456`）与四位小数数量 / 单价（`4.0000` / `1.2345`）经真实控制器创建后，
  以 `CONVERT(varchar, ...)` 断言**逐位原样往返**（绝不收敛为 2 位）；
- 声明列上限（`999999999999.999999`、`99999999999999.9999` 与定金比例 `30.1250`）同样原样往返；
- 第 7 位小数汇率、第 5 位小数数量 · 单价 · 比例与量级越界在写入之前受控拒绝：零 PI、零明细、零占号；
- 被拒修改不改变权威行的任何商业字段；
- 非专用目标（错误实例 / 错误库名 / 非集成安全）在访问数据库之前被拒绝（fail closed）。

## 7. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，
  且 `Integrated Security=true`；护栏在**任何数据库访问之前**校验失败即中止；
- 夹具每次运行创建**全新的 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 不读取 `appsettings` / `.env` / 生产凭据；连接串只来自进程环境变量或专用 localdb 默认值；
- 测试只新增自己的夹具行（SQL Server 自增主键），复用种子数据中的既有菜单 / 单据号规则。

## 8. 环境证据与边界

- 单元测试与真实 SQL 集成测试的输出 / TRX 记录于 `.ai/logs/ERP-534/`（保留原始失败日志）；
- 说明：本文件的完成只代表 Release 构建（含分析器 / 警告即错误）与单元测试通过，
  **构建完成不代表阶段验收完成**；真实 SQL 集成场景与最终浏览器验收由调度器 / 验收运行器按各自门禁执行。
