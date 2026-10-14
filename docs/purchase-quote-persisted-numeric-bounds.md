# 供应商比价数量 / 单价 / 重算总额的持久化精度护栏（ERP-528）

## 1. 目标

让供应商比价（`db_owner.PurchaseQuotes`，`api/purchase/quotes`）的数量 / 单价 / 服务端重算总额在**任何写入之前**
收敛到与实际列声明同源的 `DECIMAL(18,4)` 精度内，关闭既有缺口：

1. `db_owner.PurchaseQuotes` 由权威建表脚本（`SchemaUpgrader` 第 15 段）以 `Quantity DECIMAL(18,4)`、
   `QuotePrice DECIMAL(18,4)`、`TotalAmount DECIMAL(18,4)` 创建；
2. `PurchaseQuoteMutationRules` 的草稿护栏以 `DecimalPrecision = 18` / `DecimalScale = 4` /
   `MaxDecimal18Scale4 = 99_999_999_999_999.9999` 判定数量 / 单价 / 重算总额；
3. EF 模型此前**未**显式声明 `PurchaseQuote` 的精度，`ErpDbContext` 按 SQL Server 默认精度把这
   三列映射为 `decimal(18,2)` —— 于是服务端归一化后的合法 3 / 4 位小数（**含服务端重算总额**）会在到达 SQL 之前
   被静默取整到 2 位，而护栏却接受 4 位小数。

本护栏把模型映射与权威列声明对齐（与 `BaseProductSupplier.MinOrderQty`、`Stock` 系列同一处理），并让草稿护栏
对**不可逐位原样存储**的数量 / 单价 / 重算总额一律受控拒绝，**绝不静默取整**。

## 2. 精度口径（与实际列声明同源，只读检查、不猜小数位）

| 列 | 实际存储 | 规则常量 |
|---|---|---|
| `PurchaseQuotes.Quantity` | `DECIMAL(18,4)`（`SchemaUpgrader` 第 15 段建表 + `ErpDbContext.Config` 显式 `HasPrecision(18,4)`） | `DecimalPrecision = 18`、`DecimalScale = 4`、`MaxDecimal18Scale4 = 99_999_999_999_999.9999` |
| `PurchaseQuotes.QuotePrice` | 同上 | 同上 |
| `PurchaseQuotes.TotalAmount`（服务端重算） | 同上 | 同上 |

- 精度常量与 `ErpDbContext.Config.cs` 的 `HasPrecision(18, 4)` 显式声明、以及 `SchemaUpgrader` 建表脚本**一字同源**；
- 区间口径复用既有 `PurchaseQuoteMutationRules.IsRepresentable` / `EnsureRepresentable`，
  **不新增第二套区间常量**，也**不**重新取整；
- 取整口径复用既有 `CalculateTotalAmount`（单价 × 数量，4 位、`AwayFromZero`）——护栏只做「取整之后的逐位
  原样可表示判定」，**绝不**改写该唯一权威重算。

## 3. 护栏口径

| 项 | 口径 |
|---|---|
| 校验时点 | 新增：`ValidateDraft` 先于 `Service.CreateAsync`（任何写入之前）；修改：在比价行锁内、`ApplyEditableFields` 改写任何商业字段之前；两者被拒都整体回滚（零部分写入、零单号消耗） |
| 数量 | 必须严格大于 0，且可在 `DECIMAL(18,4)` 内**逐位原样**表示（`1.00005` 等第 5 位小数与 `1e14` 等越界量受控拒绝） |
| 单价 | 必须大于等于 0，且可在 `DECIMAL(18,4)` 内逐位原样表示 |
| 重算总额 | 由服务端按「单价 × 数量」在 `AwayFromZero` 下取整后判定；越界（如上限 × 上限）以既有 `TotalText` 受控拒绝 |
| 拒绝语义 | 沿用既有受控 `InvalidParameter` 文案（`QuantityText` / `PriceText` / `TotalText`），**绝不**接受后静默取整 / 截断 |
| 零副作用 | 被拒请求不新增 / 不修改任何 `PurchaseQuote` / `PurchaseQuoteDecision` 行，不消耗单号，不改写状态与审计字段（修改的锁时间戳刷新随事务回滚） |
| 权限 / 审计 / 锁 | 不改任何身份 / 菜单 / 客户范围授权，不改确定性 Id 升序行锁协议与审计字段；不新增权限、菜单、表、列、索引 |

## 4. 实现

- `src/ERP.Infrastructure/Data/ErpDbContext.Config.cs`：新增 `PurchaseQuote.Quantity` / `QuotePrice` /
  `TotalAmount` 的 `HasPrecision(18, 4)`（仅声明精度，不改任何列类型与 DDL）；模型映射由此与权威列声明逐字一致。
- `src/ERP.Application/Services/PurchaseQuoteMutationRules.cs`：
  - 新增纯函数 `EnsureRepresentable(value, errorText)`（无副作用，不可表示即抛既有 `InvalidParameter` 文案）；
  - `ValidateDraft` 改用 `EnsureRepresentable` 逐项判定数量 / 单价 / 服务端重算总额，语义与错误文案保持不变；
  - 新增 `PersistedNumericText` 口径文案（接口 / 文档同源）。
- `src/ERP.Api/Controllers/PurchaseQuoteController.cs`：只补充 XML 契约说明（新增 / 修改的持久化精度保证），
  调用路径、锁、事务与授权**不改**。

## 5. 不改写项

- 不新增 / 不修改任何表、列、索引、schema、迁移或种子数据；不新增权限、菜单、角色、用户授权；无匿名 / 管理员降级；
- 不改数量必须为正、单价非负、CNY / USD 白名单、状态与 `IsSelected` 一致性、`QuoteNo` / `Status` / `Currency` /
  `TotalAmount` 服务端归一化、新增的空 / 零主键与审计重置、已决定 / 已转换行编辑与删除冻结、Id 升序行锁协议；
- 不做任何历史数据修复 / 回填，不执行生产 SQL、不部署、不发布，也不读取任何密钥或环境配置。

## 6. 测试

- 单元测试 `src/ERP.UnitTests/PurchaseQuotePersistedNumericBoundsTests.cs`（内存库 + 真实启用身份 + 真实控制器）：
  - 实际 EF 列精度与规则常量一字同源（仅以 SQL Server 提供程序**只构建模型**，不连接数据库）：三列
    `decimal(18,4)`；
  - 可表示性边界：4 位小数与量级边界放行、第 5 位小数与越界拒绝、`EnsureRepresentable` 复用既有受控错误；
  - 草稿校验：4 位小数原样接受、第 5 位小数（数量 / 单价）拒绝、量级精确上限放行 / 最小单位越界拒绝、
    重算总额溢出拒绝；
  - 控制器新增 / 修改：越界零比价行零审批行、权威行逐字段（含审计时间戳 / `RowVersion`）不变、合法 4 位小数原样落库；
  - 源码契约：`ErpDbContext.Config` 三处 `HasPrecision(18, 4)` 与权威列声明同源、护栏先于任何写入、规则不落库。
- 集成测试 `src/ERP.IntegrationTests/PurchaseQuotePersistedNumericBoundsSqlServerTests.cs`（NEWERP_AUTOTEST 护栏，真实 SQL Server）：
  真实 `INFORMATION_SCHEMA` 列精度、4 位小数经真实控制器创建后**原样往返**（`CONVERT(varchar, ...)` 逐位断言）、
  第 5 位小数 / 量级越界 / 重算溢出零写入（比价行 / 审批决定 / 采购单计数不变）、被拒修改不改权威行。
- 既有回归：`PurchaseQuoteMutationTests`、`PurchaseQuoteAuthorizationTests`、`PurchaseQuoteEntryAuthorizationTests`、
  `PurchaseQuoteApprovalTests`、`PurchaseQuoteMasterReferenceTests`、`PurchaseQuoteConversion*` 等单元测试与对应真实
  SQL 回归全部保持通过。

## 7. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，
  且 `Integrated Security=true`；护栏在**任何数据库访问之前**校验失败即中止；
- 每次运行创建**全新的 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 不读取 `appsettings` / `.env` / 生产凭据；连接串只来自进程环境变量或专用 localdb 默认值；
- 测试种子一律使用 SQL Server 自增主键（不显式指定 Id）。

> 说明：本文件的完成只代表 Release 构建与单元测试通过，**构建完成不代表阶段验收完成**；
> 真实 SQL 集成场景与最终验收由调度器 / 验收运行器按各自门禁执行。
