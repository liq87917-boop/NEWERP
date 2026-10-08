# 收货计划实时授权与生命周期护栏（ERP-361）

> 阶段 3 · 库存核心闭环补强 · 复用既有 ERP-097 数据范围与 ERP-353/360 的「同一把行锁 + 可串行化事务」口径

## 1. 目标

把既有收货计划（`ContainerReceivingPlan`，`api/container/receiving-plans`）的**生命周期每一个路由**都纳入与其它单据一致的
实时授权、主数据与边界护栏：请求进入控制器后，先解析**实时身份 → 账号状态 → 既有「收货计划」（`receiving-plan`）菜单授权 →
权威数据范围（复用 ERP-097）**，再读取任何计数或生成单据号；新增 / 修改 / 状态变更前还要校验供应商（必填）、目的港（可选）、
柜型与有界文本，提交 / 审核额外要求总件数为正数；提交 / 审核 / 取消 / 删除共用同一把收货计划行锁 + 可串行化事务。

只复用既有分层架构、既有权限模型与既有单据流转，**不新增**用户授权、表 / 列 / 菜单、外部系统调用，也不改写历史单据。

## 2. 口径

| 项 | 口径 |
|---|---|
| 身份 | 每次请求按 `ClaimTypes.NameIdentifier` 解析：缺失 / 非正整数 → `2000` 未认证；账号不存在或已 `IsDeleted` → `2000`；`Status != Enabled` → `2002` 权限不足 |
| 菜单 | 复用既有「收货计划」菜单 `receiving-plan`（与 `SeedData.Menus` 同源，`ReceivingPlanLifecycleRules.RequiredMenuCode`）；**非特权账号**必须显式具备，撤销后下一次请求立即收敛；**特权账号**（超级管理员 / 系统内置角色 / 显式特权角色，与 ERP-097 同源）继承既有全部访问 |
| 数据范围 | 复用 `SalespersonDataScopeService`（ERP-097 唯一权威口径）。收货计划（供应商计划）**没有客户 / 业务员归属列**，因此无法按归属过滤：只放行**特权账号**与**已映射业务员的受限账号**；**未映射业务员的受限账号 fail closed**，绝不降级为全局 / 管理员可见 |
| 供应商（必填） | 新增 / 修改 / 提交 / 审核 / 取消 / 删除前：`SupplierId` 必须为正整数（否则 `1001`），且供应商存在、未删除（`1002`）且启用 `Status = 1`（否则 `1004`） |
| 目的港（可选） | `null` = 未指定；填写时必须为正整数（否则 `1001`），且指向启用、未删除、`InfoType = Port` 的 `BaseOtherInfo` 港口字典项（不存在 / 已删除 `1002`；非港口类型 `1001`；已停用 `1004`） |
| 柜型 | 必须是已知 `ContainerType` 枚举（`20GP / 40GP / 40HQ / 45HQ / 散货(0)`），否则 `1001` |
| 有界文本 | `BookingNo`(50) / `ContainerNo`(50) / `Destination`(200) / `Remark`(500) 去首尾空白后不得超限（超限 `1001`） |
| 数量 | 总件数**仅在提交 / 审核**时要求为正数（`TotalQuantity > 0`，否则 `1001`）；草稿可暂存 0 / 负数 |
| 修改不可留痕 | 修改时**先校验完整的拟议内容**（供应商 / 目的港 / 柜型 / 有界文本），全部通过后才复制到被跟踪实体；被拒绝的供应商 / 数量 / 目的港 / 状态变更**不改动库中收货计划与状态** |
| 订柜单号 | `BookingNo` 只作**显式 legacy 文本**保留：绝不按单号反查 `ContainerBooking`、绝不臆造订柜链接，也绝不把收货计划审核当收货 / 入库 / 出运 |
| 历史读取 | 供应商 / 目的港后来停用 / 删除**不阻断**读取：列表 / 详情照常返回，并在响应 `message` 追加显式不可用证据（`ReceivingPlanLifecycleRules.UnavailableEvidencePrefix`）；绝不回填、绝不改写、绝不按自由文本猜测归属 |
| 状态变更串行化 | 提交 / 审核 / 取消 / 删除共用同一把收货计划行锁（`SELECT Id FROM db_owner.ContainerReceivingPlans WITH (UPDLOCK, HOLDLOCK) WHERE Id = @id`）+ `IsolationLevel.Serializable` 事务 |

## 3. 路由矩阵（全部先授权）

| 路由 | 先授权 | 实时主数据 | 行锁 + 可串行化事务 |
|---|---|---|---|
| `GET api/container/receiving-plans` | ✅（先于计数 / 分页） | 读取：证据标注 | — |
| `GET api/container/receiving-plans/{id}` | ✅ | 读取：证据标注 | — |
| `POST api/container/receiving-plans` | ✅ | ✅ 供应商 + 目的港 + 柜型 + 文本 | —（单次写入） |
| `PUT api/container/receiving-plans/{id}` | ✅ | ✅ 完整拟议内容（先校验后赋值） | — |
| `POST api/container/receiving-plans/{id}/submit` | ✅ | ✅ 数量为正 + 供应商 + 目的港 | ✅ |
| `POST api/container/receiving-plans/{id}/approve` | ✅ | ✅ 数量为正 + 供应商 + 目的港 | ✅ |
| `POST api/container/receiving-plans/{id}/cancel` | ✅ | ✅ 供应商 + 目的港 | ✅ |
| `DELETE api/container/receiving-plans/{id}` | ✅ | ✅ 供应商 + 目的港（仅待提交可删） | ✅ |

新增的授权与主数据判定严格**先于**单据号生成（`GenerateAsync(DocumentType.ReceivingPlan)`）与落库，不合格不占用单据号流水、不落任何数据。

## 4. 并发协同

`Submit` / `Approve` / `Cancel` / `Delete` 按同一顺序执行：

1. 事务外先解析身份 / 菜单 / 数据范围（fail closed 时不进事务、不加锁、不写库）；
2. 开启 `IsolationLevel.Serializable` 事务；
3. 对**本收货计划行**加 `UPDLOCK/HOLDLOCK`（非关系型提供程序 / 内存库跳过，事务等价无事务）；
4. 锁内重新加载本单，复核状态机（提交 `Pending → Submitted`；审核 `Submitted → Approved`；删除仅 `Pending`；取消任意状态）；
5. 复核数量（仅提交 / 审核）与供应商 / 目的港实时可用性；
6. 置状态 / 软删除并提交事务，任一步失败即回滚。

并发下「提交 vs 删除」「双重审核」等竞争只能成功其一：先到者提交后，后到者在锁内看到已提交的状态 / 软删除标记并 fail closed。

## 5. 错误信息（API 直接返回）

全部通过既有 `BusinessException` 抛给既有异常中间件：

- 无身份：`请先登录后再访问收货计划`（2000）；
- 账号不存在 / 已删除：`登录账号不存在或已删除，禁止访问收货计划`（2000）；
- 账号禁用：`登录账号已禁用，禁止访问收货计划（fail closed）`（2002）；
- 无菜单：`当前账号没有「收货计划」（receiving-plan）模块授权：拒绝访问收货计划（fail closed，不返回 / 不修改任何收货计划数据）`（2002）；
- 未映射业务员：`当前账号未映射为业务员（收货计划操作员），不能访问收货计划（fail closed，不泄露任何范围外单据）`（2002）；
- 供应商非法：`收货计划必须指定供应商（SupplierId 必须为正整数）`（1001）/ `供应商（Id=…）不存在或已删除…`（1002）/
  `供应商「…」已停用，不能用于收货计划的新增 / 修改 / 状态变更（历史收货计划仍可读取）`（1004）；
- 目的港非法：正整数 / 非港口字典项（1001）、不存在 / 已删除（1002）、已停用（1004）；
- 柜型 / 文本 / 数量非法：`1001`（数量文案：`收货计划提交 / 审核前总件数必须为正数（TotalQuantity > 0）`）；
- 历史读取证据：响应 `message` 追加 `历史收货计划来源主数据不可用（只读照常返回，不可用于新增 / 修改 / 状态变更）：…`。

## 6. 不改写项

- 不新增表 / 列 / 外键 / schema，不改 `SchemaUpgrader` 与种子数据；不回填 / 改写历史收货计划的供应商、目的港、数量与备注；
- 不新增权限、菜单、用户授权或管理员兜底；数据范围沿用既有 ERP-097 口径，不扩权；
- 不写库存 / 库存流水 / 财务 / 结算 / 单证，不调用船公司 / 海关 / 货代等外部系统，不按订柜号推断链接；
- 保留收货计划原有的单据号规则、审计时间戳（`CreatedAt` / `UpdatedAt`）与软删除语义。

## 7. 实现落点

| 文件 | 职责 |
|---|---|
| `src/ERP.Application/Services/ReceivingPlanLifecycleRules.cs` | 新增：身份 / 账号状态 / receiving-plan 菜单 / 数据范围四重校验、供应商与目的港实时主数据、柜型与有界文本、数量门槛、完整拟议内容校验快照、历史读取不可用证据；纯只读判定，不落库 |
| `src/ERP.Api/Controllers/ContainerControllers.cs` | `ContainerReceivingPlanController` 八个路由全部先授权；新增校验先于单据号生成；修改先校验后赋值；提交 / 审核 / 取消 / 删除共用收货计划行锁 + 可串行化事务；只读路由追加不可用证据 |

## 8. 测试

- 单元测试 `src/ERP.UnitTests/ReceivingPlanLifecycleTests.cs`（内存库）：无身份 / 已删除 / 禁用 / 无菜单 / 未映射业务员在
  **每一个**路由 fail closed；撤销菜单后立即收敛；新增的供应商 / 目的港 / 柜型 / 文本边界与「失败不占单据号不落库」；
  提交 / 审核零 / 负数量拒绝且状态不变；修改先校验后赋值（失败的原单完全不变）；状态变更 / 取消 / 删除的主数据与状态机；
  历史读取在主数据停用时照常可读并带显式证据；订柜单号只作 legacy 文本；规则纯只读与控制器全部路由先授权的源码契约。
- 集成测试 `src/ERP.IntegrationTests/ReceivingPlanLifecycleSqlServerTests.cs`（真实 SQL Server + 真实控制器）：
  已映射业务员 + 既有菜单可读、撤销菜单立即拒绝、禁用身份无变更、停用供应商 / 停用目的港 / 非港口字典项阻止新增与状态变更、
  失败编辑不改动库中单据、零数量阻止提交 / 审核，以及**两条独立连接**的「提交 vs 删除」「双重审核」竞争只允许一方成功；
  另含目标库护栏单元级校验（错误实例 / 错误库名前缀 / 非集成安全一律在访问数据库之前拒绝）。

## 9. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，且 `Integrated Security=true`；
  护栏在**任何数据库访问之前**校验失败即中止；
- 每次运行使用**全新的 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 不读取 `appsettings` / `.env` / 生产凭据；连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值；
- 测试种子一律使用 SQL Server 自增主键（不显式指定 Id），保留完整控制台输出（含目标库护栏放行与集成场景就绪日志）。

**构建通过不等于阶段验收通过**：本任务只交付上述实现与测试证据，最终验收由调度器的阶段验收流程决定。
