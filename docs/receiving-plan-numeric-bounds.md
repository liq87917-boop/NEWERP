# 收货计划总件数实际列精度护栏（ERP-513）

> 阶段 3 · 库存核心闭环补强 · 复用 ERP-361 收货计划生命周期护栏与 ERP-482 规范入口的既有校验顺序

## 1. 目标

补齐既有收货计划（`ContainerReceivingPlan`，`api/container/receiving-plans`）**总件数（`TotalQuantity`）的存储完整性缺口**：

1. `ReceivingPlanLifecycleRules.ValidateProposedAsync` 此前把 `proposed.TotalQuantity` **原样复制**进拟议快照，
   新增 / 修改草稿对数量没有任何存储边界判定；
2. `EnsureQuantityPositive` 此前只拒绝提交 / 审核时的 `<= 0`，**没有**任何精度 / 上界判定；
3. `ContainerReceivingPlan.TotalQuantity` 在 EF 模型中**未**显式 `HasPrecision`，因此按 SQL Server 提供程序默认精度
   落库为 `DECIMAL(18,2)`（16 位整数 + 2 位小数）。于是**正数但过精度**的值（例如 `12.345`）会通过「大于 0」检查
   却被 SQL 静默取整，**巨大正数**（例如 `1e16`、`decimal.MaxValue`）能通过应用层检查却在写入时被驱动 / SQL 拒绝
   （算术溢出 8115）；
4. 上述失败发生在**单号已经预约之后**：草稿写入会先消耗一个服务端收货计划单号（`GenerateAsync(DocumentType.ReceivingPlan)`），
   再在写入时才暴露错误 —— 既占号又留下未落库的失败。

只补齐**总件数的存储表示边界**：新增 / 修改（草稿）在任何写入与单号生成之前判定，提交 / 审核在**既有行锁内**判定，
**不**改数量正负口径、单据流转、授权、主数据门槛、单号规则、行锁与事务、历史读取与取消 / 作废语义。

## 2. 精度口径（与实际 EF / SQL 存储同源）

| 列 | 实际存储 | 规则常量（`ReceivingPlanLifecycleRules`） |
|---|---|---|
| `ContainerReceivingPlan.TotalQuantity` | `DECIMAL(18,2)`（EF 模型未显式声明精度 → SQL Server 提供程序默认） | `QuantityPrecision = 18`、`QuantityScale = 2`、`MaxQuantity = 9_999_999_999_999_999.99`、`MinQuantity = -9_999_999_999_999_999.99` |

- 判定为**纯函数**：`IsQuantityRepresentable` / `EnsureQuantityRepresentable` 不取整、不截断、不改写任何值；
  「可在列内原样表示」= 小数位不超过 2 位（`decimal.Round(value, 2) == value`）且落在 `[MinQuantity, MaxQuantity]`；
- 数值型边界与真实列类型逐字同源：单元测试以 SQL Server 提供程序**仅构建模型**断言 `GetColumnType() == "decimal(18,2)"`；
  集成测试再以真实 `INFORMATION_SCHEMA.COLUMNS` 与 SQL Server 自身「算术溢出（错误号 8115）」双重确认；
- **不改写**：绝不把 `12.345` 静默变成 `12.35`，也绝不用取整后的值代替调用方提交值 —— 过精度一律受控拒绝；
- **不借用财务金额口径**：不复用 / 不改造 `FinancePersistedAmountRules`、`CurrencyAmountRules` 等财务金额归一化来改写
  物流数量，也不从 legacy `BookingNo` 反推任何来源 / 订柜链接。

## 3. 护栏口径

| 项 | 口径 |
|---|---|
| 新增 / 修改（草稿） | 授权 / 状态检查之后、`ValidateProposedAsync` 内、**单据号生成与任何被跟踪写入之前**判定：过精度 / 溢出 → `1001` 受控拒绝，零写入、零占号 |
| 草稿正负口径 | **原样保留既有文档口径**：草稿可暂存可表示的 `0` 与**负数**（含 `-7.25` 等 2 位小数负值）；本护栏**不**为草稿发明正数要求 |
| 提交 / 审核（承诺） | 在既有收货计划行锁（`UPDLOCK, HOLDLOCK`）+ `IsolationLevel.Serializable` 事务**内**复核：先 `EnsureQuantityRepresentable`，再要求 `TotalQuantity > 0`；被拒时计划数量 / 状态 / 单号 / 下游证据一律不变 |
| 合法零 / 负草稿 | 照常可读、可取消 / 删除（历史取消 / 读取闸门**不变**），但**不能**提交 / 审核 |
| 单号 | 拒绝严格先于 `GenerateAsync(DocumentType.ReceivingPlan)`：过精度 / 溢出请求**零单号消耗** |
| legacy `BookingNo` | 只作显式 legacy 文本保留：绝不按单号反查 `ContainerBooking`、绝不臆造订柜链接、绝不因数量或单号产生任何库存 / 财务 / 单证写入 |
| 历史读取 / 取消 / 作废 | **不新增**任何草稿有效性闸门：列表 / 详情照常返回，`Cancel` / `Delete` 行为与既有 ERP-361 一致 |
| 保留项 | 既有实时授权（身份 / 账号状态 / `receiving-plan` 菜单 / ERP-097 数据范围）、供应商与目的港实时主数据、柜型枚举、文本有界、确定性行锁、可串行化事务、受控错误码与审计一律原样保留 |

## 4. 错误信息（API 直接返回）

全部通过既有 `BusinessException`（`InvalidParameter`，错误码 `1001`）抛给既有异常中间件：

- 存储边界：`收货计划总件数（TotalQuantity）必须可在实际列精度 DECIMAL(18,2) 内原样存储（至多 2 位小数且有界）：已拒绝，绝不静默截断或取整（当前值 …）`
  （文案常量 `ReceivingPlanLifecycleRules.QuantityStorageText`）；
- 承诺正数：`收货计划提交 / 审核前总件数必须为正数（TotalQuantity > 0）`（既有文案，原样保留）。

## 5. 不改写项

- 不新增表 / 列 / 外键 / schema / 迁移，不改 `SchemaUpgrader` 与种子数据；不回填 / 不改写历史收货计划的数量；
- 不新增权限、菜单、用户授权或管理员兜底；数据范围沿用既有 ERP-097 口径；
- 不写库存 / 库存流水 / 财务 / 结算 / 单证，不调用外部系统，不从 legacy 文本推断任何链接；
- 不重排既有校验顺序，不改变单号规则、审计时间戳（`CreatedAt` / `UpdatedAt`）、软删除语义与行锁 / 事务契约。

## 6. 实现落点

| 文件 | 职责 |
|---|---|
| `src/ERP.Application/Services/ReceivingPlanLifecycleRules.cs` | 新增 `QuantityPrecision` / `QuantityScale` / `MaxQuantity` / `MinQuantity` / `QuantityStorageText` 常量与纯函数 `IsQuantityRepresentable` / `EnsureQuantityRepresentable`；`ValidateProposedAsync` 对总件数调用守卫；`EnsureQuantityPositive` 追加同一表示区间判定 |
| `src/ERP.Api/Controllers/ContainerControllers.cs` | `ContainerReceivingPlanController`：既有「校验先于单号生成」（新增）与「锁内复核」（提交 / 审核）顺序不变，仅补充口径文档；无功能改写 |

## 7. 测试

- 单元测试 `src/ERP.UnitTests/ReceivingPlanNumericBoundsTests.cs`（内存库 + 计数型单号服务）：纯函数边界（`0` / 可表示负数 / 恰好上限 / 过精度 / `1e16` / `decimal.MaxValue`）、
  EF 模型列类型 `decimal(18,2)`、新增 / 修改过精度与溢出**零写入零占号且原单不变**、草稿 `0` / 负数照常落库、提交 / 审核正负与不可存储拒绝且状态数量不变、
  历史读取与取消不新增闸门、源码契约（守卫在单号生成前、规则纯只读）；
- 集成测试 `src/ERP.IntegrationTests/ReceivingPlanNumericBoundsSqlServerTests.cs`（真实 SQL Server + 真实控制器）：
  真实列类型与边界值原样往返、SQL 自身拒绝越界转换（8115）、受权拒绝过精度 / 溢出且零写入零占号、草稿 `0` / 负数落库、合法零 / 负草稿禁止承诺、
  正数生命周期数量原样保存、历史取消 / 读取不变；
- 既有 `ReceivingPlanLifecycleTests.cs` / `ReceivingPlanCanonicalEntryTests.cs` / `ReceivingPlanLifecycleSqlServerTests.cs`
  （授权 / 主数据 / 状态机 / 并发状态变更）保持原断言并回归通过。

## 8. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，且 `Integrated Security=true`；
  护栏在**任何数据库访问之前**校验失败即中止；
- 每次运行使用**全新的 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 不读取 `appsettings` / `.env` / 生产凭据；连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值；
- 种子一律使用 SQL Server 自增主键（不显式指定 Id），保留完整控制台输出。

**构建通过不等于阶段验收通过**：本任务只交付上述实现与测试证据，最终验收由调度器的阶段验收流程决定。
