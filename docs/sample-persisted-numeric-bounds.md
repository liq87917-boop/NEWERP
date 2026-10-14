# 样品管理持久化数值精度（数量 Quantity / 样品费 SampleFee，ERP-532）

样品管理（`/api/crm/samples`）是样品台账与 ERP-063 附件证据契约共同解析的权威对象。其数量（`Quantity`）与
样品费（`SampleFee`）由权威建表脚本（`SchemaUpgrader` 第 19 段）声明为 `DECIMAL(18,4)`（14 位整数 + 4 位小数），
`docs/sample-authority.md` 也将其记录为四位小数列。

## 1. 问题（本轮修复）

- `SampleAuthorizationRules` 之前只按**量级**（`MaxDecimalMagnitude = 99999999999999.9999`）判定可存储性，
  **不**判定小数位；
- EF 模型（`ErpDbContext.Config.cs`）此前**未**为 `Sample` 声明任何 `HasPrecision`，因此按 SQL Server 默认
  精度把这两列映射为 `decimal(18,2)`；
- 结果是：合法的 3 / 4 位小数（如数量 `0.0125`、样品费 `1.2345`）通过校验，却在到达 SQL 之前被**静默取整**
  到 2 位，与实际列声明不一致，改变样品成本证据。

## 2. 修复

| 位置 | 变更 |
|---|---|
| `src/ERP.Infrastructure/Data/ErpDbContext.Config.cs` | 为 `Sample` 的 `Quantity` / `SampleFee` 显式声明 `HasPrecision(18, 4)` |
| `src/ERP.Application/Services/SampleAuthorizationRules.cs` | 新增 `PersistedDecimalScale = 4` 与纯判定 `IsPersistedDecimalRepresentable`；`EnsureNonNegativePersistedDecimal` 由「仅量级」升级为「量级 + 小数位」同时判定 |
| `src/ERP.Api/Controllers/SampleController.cs` | 新增 / 修改仍在落库**之前**调用 `SampleAuthorizationRules.Validate`（路由与授权结构不变，仅补文档说明） |

- **只声明精度**：不新增表 / 列 / 索引 / 菜单 / 权限 / 用户授权，不改任何列类型与 DDL，不读取环境变量；
  实体特性（数据注解）不变，精度只属于 fluent 映射。
- 合法的 4 位小数（含恰好上限 `99999999999999.9999`）在真实列上**原样往返**。

## 3. 写入护栏（拒绝而不取整）

`SampleAuthorizationRules.Validate` 在新增 / 修改落库之前，对两个持久化 decimal 列判定：

> 绝对值 ≤ `99999999999999.9999` **且** 小数位 ≤ 4（`decimal.Round(value, 4, AwayFromZero) == value`）

任一项不满足即以既有受控错误 `1001 InvalidParameter` 拒绝，**绝不静默取整 / 截断 / 夹取**；被拒绝的写入
**不落任何** `Samples` 行、也**不改写**任何既有行。

| 输入 | 判定 |
|---|---|
| `0` / `1.5` / `0.0125` / `13.0555` / `0.0001` | 放行（4 位小数以内） |
| `99999999999999.9999`（恰好上限） | 放行 |
| `1.00005` / `0.00001`（第 5 位小数） | 拒绝 |
| `99999999999999.99985`（半进位取整后恰好落到上限） | 拒绝（判定原值，不采信取整结果） |
| `100000000000000`（1e14，量级溢出） | 拒绝 |
| 任意负数 | 拒绝（既有非负规则不变） |

## 4. 单元测试（`src/ERP.UnitTests/SamplePersistedNumericBoundsTests.cs`，内存库）

| 用例 | 断言 |
|---|---|
| 实际 EF 列精度 | SQL Server 提供程序**仅构建模型**：两列 `GetPrecision() = 18`、`GetScale() = 4`、`GetColumnType() = decimal(18,4)`（不连接数据库） |
| 可表示性纯判定（`[Theory]`） | 四位小数 / 量级边界放行；第 5 位小数、半进位到上限、量级溢出拒绝 |
| `Validate` 逐列第 5 位小数 / 量级溢出 / 负数（`[Theory]` × 2） | 每个 decimal 列各自 `1001`，且字段值不变 |
| `Validate` 逐列 4 位小数与上限（`[Theory]` × 2） | 放行且逐位原样 |
| 既有可接受边界值 | `0` / 1 位小数 / 恰好上限继续放行且不改写字段 |
| 真实控制器新增 / 修改 | 不可表示值零写入、既有行不变；合法 4 位小数原样往返 |
| 源码契约 | EF 映射为两列声明 `HasPrecision(18, 4)` |

既有 `SampleAuthorizationTests` 的授权 / 字段边界矩阵新增「第 5 位小数」与「半进位到上限」拒绝用例；
ERP-459 授权语义、ERP-097 读取范围、文本长度、非负数值、客户 / 商品 / 业务员引用规则均保持不变。

## 5. 真实 SQL Server 集成测试（`src/ERP.IntegrationTests/SamplePersistedNumericBoundsSqlServerTests.cs`）

在 GUID 独占的 `NEWERP_AUTOTEST` 目标库上以**真实控制器 + 真实既有授权**自包含播种客户与授权账号：

| 场景 | 断言 |
|---|---|
| 实际列精度 | EF 模型与真实 `INFORMATION_SCHEMA` 双向确认两列均为 `decimal(18,4)` |
| 合法 4 位小数原样往返 | 两列以 `CONVERT(varchar, ...)` 逐位断言（如 `1234.5678` / `0.0125` / `99999999999999.9999`） |
| 第 5 位小数 / 量级溢出 / 负数 | 写入之前 `1001` 受控拒绝，`Samples` 行数不变；同一受权控制器随后合法创建成功 |
| 半进位取整到上限 | 仍拒绝；恰好上限本身放行并以真实列原样保存 |
| 被拒修改 | 权威行的 decimal 字段与审计时间戳逐字段不变 |
| 绕过护栏的原始 EF 写入 | 真实列静默取整（`0.01255 → 0.0126`、`1.00005 → 1.0001`）；超容量值被 SQL Server 以算术溢出（8115）拒绝 |

**真实 SQL 夹具安全口径**

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，且 `IntegratedSecurity=true`；
  实例名 / 库名前缀 / 集成安全在**访问数据库之前**校验（`AssertDedicatedTarget`，含单元级护栏用例）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；**绝不 drop / reset / 复用**任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings` / `.env` / 生产凭据。

## 6. 未执行 / 限制

- 本任务 `validation_profile = safe`、`completion_mode = build`：交付门槛为 **Release 构建 + 全量 `ERP.UnitTests`**
  与相关真实 SQL 用例；**构建通过不等于阶段验收通过**，也不声明整阶段验收。
- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置，
  未新增任何表 / 列 / 实体 / 菜单 / 权限 / 连接字符串或密钥。
- 对已存在的历史库：本任务**只**对齐 EF 模型映射与写入护栏，**不**执行任何列类型迁移 / DDL，也不回填历史数据；
  历史库的列类型仍以其自身建表脚本为准。

## 7. 相关实现

- `src/ERP.Application/Services/SampleAuthorizationRules.cs`：实时身份 / 菜单授权与有界字段校验（含 ERP-532 的
  四位小数逐位原样判定）。
- `src/ERP.Infrastructure/Data/ErpDbContext.Config.cs`：`Sample.Quantity` / `Sample.SampleFee` 的 `HasPrecision(18, 4)`。
- `src/ERP.Api/Controllers/SampleController.cs`：7 条路由在读写前授权，新增 / 修改另调用
  `SampleAuthorizationRules.Validate` 与 `EnsureWritableReferencesAsync`。
- 相关：`docs/sample-authority.md`（ERP-459 授权与边界）、
  `src/ERP.Infrastructure/Data/SchemaUpgrader.cs`（`DECIMAL(18,4)` 列声明来源）。

