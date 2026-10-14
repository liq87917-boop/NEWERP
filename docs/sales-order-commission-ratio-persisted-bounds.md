# 销售订单佣金比例的持久化精度（ERP-536）

销售订单主表 `db_owner.SalesOrders.CommissionRatio`（佣金 / 回佣比例 %）是「报价单 → PI → 销售订单」转换、
销售订单变更申请、业务员提成 / 代理服务费与佣金证据**共同解析**的权威条款字段：

| 主数据 | 列 | 建表脚本声明 |
|---|---|---|
| `SalesOrders` | `CommissionRatio`（佣金 / 回佣比例 %） | `DECIMAL(18,4)`（`SchemaUpgrader` 第 22 段缺列补齐脚本） |

同一张表的其余小数列（`ExchangeRate` / `TotalAmount` / `DepositRatio` / `DepositAmount`）与
`SalesOrderDetails` 的 `Quantity` / `UnitPrice` / `Amount` 仍按 EF 默认精度 `DECIMAL(18,2)` 存储，本任务**不改**。

`docs/customer-master-authority.md`（`BaseCustomer.CommissionRatio`，ERP-531 已对齐四位小数）与
`docs/master-data-rate-persisted-bounds.md` 记录了转换会把客户档案的四位小数佣金比例原样带入销售订单。

## 1. 问题（本轮修复）

- EF 模型（`ErpDbContext.Config.cs`）此前**未**为 `SalesOrder` 声明任何 `HasPrecision`（该处只有 `OrderNo`
  唯一索引），因此按 SQL Server 默认精度把 `CommissionRatio` 映射为 `decimal(18,2)`；
- 唯一权威校验 `SalesOrderAmountRules`（ERP-422 / ERP-047）只判定佣金比例落在 `0~100`，**不**判定小数位；
- 结果是：合法的 3 / 4 位小数比例（如 `0.1250`、`12.3456`）通过校验，却在到达 SQL 前被**静默取整**
  （`12.3456 → 12.35`），与声明列不符，也改变了下游提成 / 代理服务费 / 佣金证据推导出的比例。

## 2. 修复

| 位置 | 变更 |
|---|---|
| `src/ERP.Infrastructure/Data/ErpDbContext.Config.cs` | 为 `SalesOrder.CommissionRatio` 显式声明 `HasPrecision(18, 4)`（只该一列，与 `SchemaUpgrader` 第 22 段同源；其余小数列口径不变） |
| `src/ERP.Application/Services/SalesOrderAmountRules.cs` | 新增 `CommissionRatioScale = 4`、纯判定 `IsCommissionRatioRepresentable` 与 `ValidateCommissionRatio`；`Validate` 由「仅 0~100」升级为「0~100 **且** 可在四位小数列内逐位原样存储」 |
| `src/ERP.Application/Services/SalesOrderMutationRules.cs` | `ValidatedTermsText` / `EnsureValidatedTerms` 口径文案同步为「佣金比例 0~100 且最多 4 位小数」 |
| `src/ERP.Api/Controllers/SalesOrderController.cs` | 新增 / 修改仍在**单号预约与任何字段改写之前**调用 `EnsureValidatedTerms`（仅补文档说明：含佣金比例四位小数逐位原样判定） |
| `src/ERP.Api/Controllers/SalesOrderConversion.cs` | 客户档案佣金比例原样带入的说明（随后的 `Revalidate` 按四位小数逐位复核，**拒绝而非取整**） |
| `src/ERP.Application/Services/SalesOrderChangeRequestService.cs` | 变更申请拟议佣金比例快照同口径说明（仍复用同一套唯一权威算法与校验） |

- **只声明精度**：不新增表 / 列 / 索引 / 菜单 / 权限 / 用户授权，不改任何列类型与 DDL，不读取环境变量。
- 合法的 4 位小数（含恰好 `0`、`100` 与既有两位小数边界值）在真实列上**原样往返**。

## 3. 写入护栏（拒绝而不取整）

新增 / 修改 / 提交 / 审核与变更申请登记、报价单 / PI 转换全部复用同一套唯一权威校验：在**单号预约与任何
字段 / 明细改写之前**对佣金比例判定

> 落在既有 0~100 政策区间（含端点）**且** 小数位 ≤ 4（`decimal.Round(value, 4, AwayFromZero) == value`）

任一项不满足即以既有受控错误 `1001 InvalidParameter` 拒绝，**绝不静默取整 / 截断 / 夹取**；被拒绝的写入
**不落任何** `SalesOrders` / `SalesOrderDetails` 行、**不预约 / 不消耗**单据号、**不改写**任何既有行。

| 输入 | 判定 |
|---|---|
| `0` / `3.5` / `0.1250` / `12.3456` / `99.9999` | 放行（4 位小数以内） |
| `100`（恰好上限） | 放行 |
| `0.12505` / `12.34567`（第 5 位小数） | 拒绝 |
| `99.99995`（半进位取整后恰好落到上限 100） | 拒绝（判定原值，不采信取整结果） |
| `-1` / `101`（超出 0~100 政策） | 拒绝（沿用既有「佣金比例必须在 0~100 之间」文案） |
| `100000000000000`（1e14，量级溢出） | 拒绝 |

## 4. 单元测试（`src/ERP.UnitTests/SalesOrderCommissionRatioPersistedBoundsTests.cs`，内存库）

| 用例 | 断言 |
|---|---|
| 可表示性纯判定 | 政策区间内四位小数（含 `0` / `100` / `99.9999` / `0.1250`）放行；第 5 位小数、半进位到上限、越界与量级溢出一律拒绝 |
| `Validate` / `ValidateNewWrite` 原值判定 | 四位小数逐位原样保留（校验不改写字段）；第 5 位小数返回 `CommissionRatioScaleText`；越界沿用既有 0~100 文案 |
| 实际 EF 列精度 | SQL Server 提供程序**仅构建模型**：`CommissionRatio` 为 `decimal(18,4)`（`GetPrecision() = 18`、`GetScale() = 4`），其余销售订单 / 明细小数列仍为 `decimal(18,2)` |
| 真实控制器新增 / 修改 | 五位小数 / 越界请求 `1001` 拒绝、零写入、`SysDocumentNumberRules` 零变化；四位小数原样往返；零佣金比例既有政策不变；被拒绝的修改既有行逐字段不变 |
| 源码契约 | EF 映射、`SchemaUpgrader` 声明、规则常量与「护栏先于占号与写入」的接线逐字同源 |

既有回归同时扩展：

- `SalesOrderWriteValidationTests`：佣金比例拒绝矩阵新增第 5 位小数 / 半进位到上限用例与「四位小数原样接受」
  用例，EF 精度契约改为「佣金比例四位小数、其余列两位小数」；
- `SalesOrderControllerTests`：新增佣金比例四位小数原样落库与五位小数受控拒绝用例（授权夹具与请求形状不变）；
- `SalesOrderConversionTests`：客户档案四位小数佣金比例转换原样保留；客户档案五位小数比例受控拒绝且不落库、
  来源单据状态不变；
- `SalesOrderWriteValidationSqlServerTests`：新增五位小数比例拒绝且零写入（含单号字轨零变化）。

## 5. 真实 SQL Server 集成测试（`src/ERP.IntegrationTests/SalesOrderCommissionRatioPersistedBoundsSqlServerTests.cs`）

在 GUID 独占的 `NEWERP_AUTOTEST` 目标库上以**真实控制器 + 真实既有授权**自包含播种：

| 场景 | 断言 |
|---|---|
| 实际列精度 | EF 模型与真实 `INFORMATION_SCHEMA` 双向确认 `SalesOrders.CommissionRatio` 为 `decimal(18,4)`，其余列仍为 `decimal(18,2)` |
| 四位小数原样往返 | `0` / `0.1250` / `12.3456` / `99.9999` / `100` 以 `CONVERT(varchar(64), CommissionRatio)` 逐位断言 |
| 恰好上限与半进位到上限 | `99.99995` 拒绝；恰好 `100` 放行并以真实列原样保存 |
| 第 5 位小数 / 越界 | 写入之前 `1001` 受控拒绝，`SalesOrders` 行数与单号字轨签名均不变 |
| 被拒修改 | 权威行的 `CommissionRatio` 真实列文本逐位不变 |
| 绕过护栏的原始 EF 写入 | 真实列静默取整（`12.34567 → 12.3457`），证明服务端护栏不可省略 |

**真实 SQL 夹具安全口径**

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，且 `IntegratedSecurity=true`；
  实例名 / 库名前缀 / 集成安全在**访问数据库之前**校验（`AssertDedicatedTarget`，含单元级护栏用例）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；**绝不 drop / reset / 复用**任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings` / `.env` / 生产凭据。

## 6. 未执行 / 限制

- 本任务 `validation_profile = safe`、`completion_mode = build`：交付门槛为 **Release 构建（分析器 + 警告视为错误）
  + 全量 `ERP.UnitTests`** 与相关真实 SQL 用例；**构建通过不等于阶段验收通过**，也不声明整阶段验收。
- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置，
  未新增任何表 / 列 / 实体 / 菜单 / 权限 / 连接字符串或密钥。
- 对已存在的历史库：本任务**只**对齐 EF 模型映射与写入护栏，**不**执行任何列类型迁移 / DDL，也不回填历史数据；
  历史库的列类型仍以其自身建表脚本为准（读取 / 打印路径不调用本校验，既有历史单据仍可读且不被修正）。

## 7. 相关实现

- `src/ERP.Application/Services/SalesOrderAmountRules.cs`：销售订单金额与业务校验的唯一权威口径
  （含 ERP-536 的四位小数佣金比例逐位原样判定与 `ValidateCommissionRatio`）。
- `src/ERP.Application/Services/SalesOrderMutationRules.cs`：规范写入（修改 / 删除 / 提交 / 审核）的确定性锁协议与
  「单号预约之前先校验」接线。
- `src/ERP.Infrastructure/Data/ErpDbContext.Config.cs`：`SalesOrder.CommissionRatio` 的 `HasPrecision(18, 4)`。
- `src/ERP.Api/Controllers/SalesOrderController.cs` / `SalesOrderConversion.cs`：新增 / 修改与转换带入的复核；
- `src/ERP.Application/Services/SalesOrderChangeRequestService.cs`：变更申请拟议快照复用同一套校验。
- 相关：`docs/quotation-persisted-numeric-bounds.md`（ERP-533）、`docs/proforma-invoice-persisted-numeric-bounds.md`
  （ERP-534）、`docs/master-data-rate-persisted-bounds.md`（ERP-531）、
  `src/ERP.Infrastructure/Data/SchemaUpgrader.cs`（`DECIMAL(18,4)` 列声明来源）。
