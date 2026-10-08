# 订柜信息实时授权与客户数据范围（ERP-360）

> 阶段 3 · 装柜（container）核心闭环补强 · 复用既有 ERP-097 数据范围与 ERP-353 订柜行锁

## 1. 目标

把既有订柜信息（`ContainerBooking`，`api/container/bookings`）的**生命周期每一个路由**都纳入与其它单据一致的实时授权与
客户数据范围护栏：只要请求进入控制器，就先解析**实时身份 → 账号状态 → 既有「订柜信息」（booking）菜单授权 →
权威客户数据范围**，再读取任何计数 / 来源字段或生成单据号；新增 / 修改 / 状态变更前还要校验客户（必填）与
供应商（可选）是否真实可用；提交 / 审核 / 取消 / 删除共用 ERP-353 的订柜行锁 + 可串行化事务，避免并发状态竞争。

只复用既有分层架构、既有权限模型与既有单据流转，**不新增**用户授权、表 / 列 / 菜单、外部系统调用，也不改写历史单据。

## 2. 口径

| 项 | 口径 |
|---|---|
| 身份 | 每次请求按 `ClaimTypes.NameIdentifier` 解析：缺失 / 非正整数 → `2000` 未认证；账号不存在或已 `IsDeleted` → `2000`；`Status != Enabled` → `2002` 权限不足 |
| 菜单 | 复用既有「订柜信息」菜单 `booking`（`SeedData` 同源，`PreLoadingBookingLinkRules.BookingRequiredMenuCode`）；**非特权账号**必须显式具备，撤销后下一次请求立即收敛；**特权账号**（超级管理员 / 系统内置角色 / 显式特权角色，与 ERP-097 同源）继承既有全部访问 |
| 数据范围 | 复用 `SalespersonDataScopeService`（ERP-097 唯一权威口径）与订柜信息的**权威客户** `ContainerBooking.CustomerId`；受限账号只能读写 `BaseCustomer.EmpId == 本人` 的客户；**未映射业务员的受限账号 fail closed**，绝不降级为全局 / 管理员可见 |
| 列表范围下推 | `BookingAuthorizationRules.ApplyScope` 在 `CountAsync` / 分页之前把客户范围下推到数据库：受限制账号只统计 / 只返回本人客户的订柜信息，绝不「先查全量再内存过滤」 |
| 客户（必填） | 新增 / 修改 / 提交 / 审核 / 取消 / 删除前：客户必须存在、未删除且启用（`Status = 1`）；`CustomerId <= 0` → `1001`；不存在 / 已删除 → `1002`；已停用 → `1004` |
| 供应商（可选） | `null` / `<= 0` = 未指定（落库为 `null`）；填写时必须存在、未删除且启用，否则同上 fail closed |
| 修改不可越范围 | 修改时**原单客户与新客户都必须在当前账号范围内**：既不能编辑范围外订柜信息，也不能把本人客户的订柜信息改成范围外客户（fail closed，`2002`） |
| 历史读取 | 客户 / 供应商后来停用 / 删除**不阻断**读取：列表 / 详情 / 时间线照常返回，并在响应 `message` 追加显式不可用证据（`BookingAuthorizationRules.UnavailableEvidencePrefix`）；绝不回填、绝不改写、绝不按自由文本猜测归属 |
| 状态变更串行化 | 提交 / 审核 / 取消 / 删除共用同一把订柜行锁（`SELECT Id FROM db_owner.ContainerBookings WITH (UPDLOCK, HOLDLOCK) WHERE Id = @id`）+ `IsolationLevel.Serializable` 事务，与 ERP-353 的「预装柜审核 / 订柜取消」抢同一把锁 |
| 保留原有护栏 | 取消仍调用 `PreLoadingBookingLinkRules.ValidateBookingCancellationAsync`（已审核预装柜 / 下游已审核装柜清单阻断）；ERP-040 跟踪字段与报关行引用快照、历史审计保持原样 |

## 3. 路由矩阵（全部先授权）

| 路由 | 先授权 | 数据范围 | 实时主数据 | 行锁 + 可串行化事务 |
|---|---|---|---|---|
| `GET api/container/bookings` | ✅ | ✅ 数据库侧（先于计数 / 分页） | 读取：证据标注 | — |
| `GET api/container/bookings/{id}` | ✅ | ✅ 单条 | 读取：证据标注 | — |
| `GET api/container/bookings/{id}/shipment-timeline` | ✅ | ✅ 单条 | 读取：证据标注 | — |
| `GET api/container/bookings/customs-broker-options` | ✅ | 字典项不按客户分范围 | — | — |
| `POST api/container/bookings` | ✅ | ✅ 客户 | ✅ 客户 + 供应商 | —（单次写入） |
| `PUT api/container/bookings/{id}` | ✅ | ✅ 原客户 + 新客户 | ✅ 客户 + 供应商 | ✅ |
| `POST api/container/bookings/{id}/submit` | ✅ | ✅ 客户 | ✅ 客户 + 供应商 | ✅ |
| `POST api/container/bookings/{id}/approve` | ✅ | ✅ 客户 | ✅ 客户 + 供应商 | ✅ |
| `POST api/container/bookings/{id}/cancel` | ✅ | ✅ 客户 | ✅ 客户 + 供应商 | ✅（并保留 ERP-353 取消护栏） |
| `DELETE api/container/bookings/{id}` | ✅ | ✅ 客户 | ✅ 客户 + 供应商 | ✅ |

新增 / 修改的授权与主数据判定严格**先于** `ContainerShipmentTrackingService.ApplyAsync` 与单据号生成（`GenerateAsync`），
不合格不占用单据号流水、不落任何数据。

## 4. 并发协同（ERP-353 同一把订柜行锁）

`Submit` / `Approve` / `Cancel` / `Delete` 与既有 `ContainerPreLoadingController.Approve`、原 `Cancel` 按同一顺序执行：

1. 事务外先解析身份 / 菜单 / 数据范围（fail closed 时不进事务、不加锁、不写库）；
2. 开启 `IsolationLevel.Serializable` 事务；
3. 对**本订柜信息行**加 `UPDLOCK/HOLDLOCK`（非关系型提供程序 / 内存库跳过，事务等价无事务）；
4. 锁内重新加载本单，复核状态机（提交 `Pending → Submitted`；审核 `Submitted → Approved`；删除仅 `Pending`）；
5. 复核客户数据范围与客户 / 供应商实时可用性；
6. 置状态 / 软删除并提交事务，任一步失败即回滚。

并发下「提交 vs 删除」「双重审核」等竞争只能成功其一：先到者提交后，后到者在锁内看到已提交的状态 / 软删除标记并 fail closed。

## 5. 错误信息（API 直接返回）

全部通过既有 `BusinessException` 抛给既有异常中间件：

- 无身份：`请先登录后再访问订柜信息`（2000）；
- 账号不存在 / 已删除：`登录账号不存在或已删除，禁止访问订柜信息`（2000）；
- 账号禁用：`登录账号已禁用，禁止访问订柜信息（fail closed）`（2002）；
- 无菜单：`当前账号没有「订柜信息」（booking）模块授权：拒绝访问订柜信息（fail closed，不返回 / 不修改任何订柜数据）`（2002）；
- 未映射业务员：`当前账号未映射为业务员（订柜信息操作员），不能访问订柜信息（fail closed，不泄露任何范围外单据）`（2002）；
- 越客户范围：`当前账号的客户数据范围不包含该订柜信息的客户：拒绝操作（fail closed，不泄露范围外单据）`（2002）；
- 客户非法：`订柜信息必须指定客户（CustomerId 必须为正整数）`（1001）/ `客户（Id=…）不存在或已删除…`（1002）/
  `客户「…」已停用，不能用于订柜信息的新增 / 修改 / 状态变更（历史订柜信息仍可读取）`（1004）；
- 供应商非法：与客户同口径（1002 / 1004）；
- 历史读取证据：响应 `message` 追加 `历史订柜信息来源主数据不可用（只读照常返回，不可用于新增 / 修改 / 状态变更）：…`。

## 6. 不改写项

- 不新增表 / 列 / 外键 / schema，不改 `SchemaUpgrader` 与种子数据；不回填 / 改写历史订柜信息的客户、供应商、跟踪字段与备注；
- 不新增权限、菜单、用户授权或管理员兜底；数据范围沿用既有 ERP-097 口径，不扩权；
- 不写库存 / 库存流水 / 财务 / 结算 / 单证，不调用船公司 / 海关 / 货代等外部系统；
- 保留 ERP-353 的「预装柜单 ↔ 订柜信息」来源护栏与显式取消流程、ERP-040 的报关行引用快照与只读回显语义。

## 7. 实现落点

| 文件 | 职责 |
|---|---|
| `src/ERP.Application/Services/BookingAuthorizationRules.cs` | 新增：身份 / 账号状态 / booking 菜单 / 数据范围四重校验、数据库侧范围下推、客户与供应商实时主数据校验、历史读取不可用证据；纯只读判定，不落库 |
| `src/ERP.Application/Services/PreLoadingBookingLinkRules.cs` | 订柜取消的身份 / 菜单 / 范围校验统一委托 `BookingAuthorizationRules`，同一模块只有一份授权口径 |
| `src/ERP.Api/Controllers/ContainerControllers.cs` | `ContainerBookingController` 十个路由全部先授权；提交 / 审核 / 取消 / 删除共用订柜行锁 + 可串行化事务；只读路由追加不可用证据 |

## 8. 测试

- 单元测试 `src/ERP.UnitTests/BookingAuthorizationTests.cs`（内存库）：无身份 / 已删除 / 禁用 / 无菜单 / 未映射业务员在
  **每一个**路由 fail closed；受限业务员列表在计数前按数据库侧范围过滤、范围外详情 / 时间线 / 写路由拒绝且状态不变；
  新增 / 修改 / 状态变更前的客户与供应商实时可用性；修改不能把订柜信息移入范围外客户且原单不变；历史读取在主数据停用 /
  删除时照常可读并带显式证据；规则纯只读与控制器全部路由先授权的源码契约。
- 回归：`src/ERP.UnitTests/ContainerShipmentTrackingTests.cs`（ERP-040 跟踪语义）、
  `src/ERP.UnitTests/PreLoadingBookingLinkTests.cs`（ERP-353 链接与取消协同）。
- 集成测试 `src/ERP.IntegrationTests/BookingAuthorizationSqlServerTests.cs`（真实 SQL Server + 真实控制器）：
  本人 / 他人身份范围矩阵、禁用身份、撤销既有菜单、失败修改不改动、停用客户阻止状态变更但历史可读且带证据、
  新增要求启用客户，以及**两条独立连接**的「提交 vs 删除」「双重审核」竞争只允许一方成功；
  另含目标库护栏单元级校验（错误实例 / 错误库名前缀 / 非集成安全一律在访问数据库之前拒绝）。

## 9. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，且 `Integrated Security=true`；
  护栏在**任何数据库访问之前**校验失败即中止；
- 每次运行使用**全新的 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 不读取 `appsettings` / `.env` / 生产凭据；连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值；
- 测试种子一律使用 SQL Server 自增主键（不显式指定 Id），保留完整控制台输出（含目标库护栏放行与集成场景就绪日志）。

**构建通过不等于阶段验收通过**：本任务只交付上述实现与测试证据，最终验收由调度器的阶段验收流程决定。
