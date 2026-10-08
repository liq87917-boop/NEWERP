# 预装柜单实时授权与权威客户数据范围（ERP-363）

## 1. 目标

把**既有**预装柜单（`api/container/pre-loadings`）的**每一个读取与写入路由**都收敛到同一套实时授权口径：
先解析实时身份 / 账号状态 / 既有「预装柜单（pre-loading）」菜单授权 / 权威客户数据范围，**再**读取计数、
来源字段、生成单据号或写入；并把受限账号的可见范围严格限定在「显式 `BookingId` → 订柜信息权威归属客户」上，
让**未关联或来源已删除的「无主」单据**对受限账号 fail closed，而特权账号保留历史访问。

本任务只复用既有分层架构、既有单据流转（创建 / 修改 / 提交 / 审核 / 取消 / 删除）、既有 ERP-353 上游链接护栏与
ERP-348 下游取消护栏；不做报表、不做 Agent、不做界面视觉 / 样式 / 导出打磨，不写库存 / 财务，
也不调用任何外部跟踪系统。

## 2. 口径

| 项 | 口径 |
|---|---|
| 适用路由 | 列表、详情、出运时间线、新增、修改、提交、审核、取消、删除 |
| 实时授权 | 每一路由先解析：身份缺失 / 非法 → 未认证；账号不存在 / 已删除 → 未认证；账号禁用 → 权限不足；非特权账号无既有 `pre-loading` 菜单 → 权限不足；非特权账号未映射业务员 → 权限不足（fail closed） |
| 客户归属 | 预装柜单自身**没有客户列**：唯一的上游权威引用是显式 `ContainerPreLoading.BookingId`；受限账号的范围只按 `ContainerBooking.CustomerId` 判定 |
| 数据范围 | 复用既有 ERP-097 `SalespersonDataScopeService`；特权账号（超级管理员 / 系统内置角色 / 显式特权角色）不受限，**保留历史访问**（含历史未关联单据） |
| 无主单据 | 受限账号对 `BookingId` 为空、或来源订柜不存在 / 已删除的预装柜单一律 fail closed（不泄露、不降级为全局可见） |
| 列表范围 | `ApplyScope` 在 **SQL 侧**把范围下推到 `Count` 与分页之前（受限账号只统计 / 只返回本人客户订柜下的预装柜单），绝不先查全量再内存过滤 |
| 校验时点 | 新增 / 修改在**替换字段与明细之前**先校验「拟议」与「已存储」两个来源范围；提交 / 审核 / 取消 / 删除在**锁内重新加载后**复核范围，失败一律不改库 |
| 不猜测 | 只认显式 `BookingId`：绝不按柜号 / 单号等自由文本反查来源，绝不补链接、不回填、不臆造归属 |
| 并发 | 修改 / 删除 / 提交 / 审核 / 取消都在 `IsolationLevel.Serializable` 事务内按既有锁序（来源订柜行 → 本单行，`UPDLOCK, HOLDLOCK`）串行化 |

## 3. 权威客户归属与数据库侧范围

`PreLoadingAuthorizationRules`（ERP-363，纯只读判定）提供四个入口：

1. `EnsureAuthorizedAsync(db, userId)`：实时身份 → 账号状态 → 既有 `pre-loading` 菜单 → 权威数据范围，
   返回本次请求的 `SalespersonDataScope`；
2. `ApplyScope(source, db, scope)`：特权账号不过滤；受限账号保留「`BookingId` 指向未删除且归属客户在范围内的订柜信息」
   的预装柜单（`join` + `FilterByCustomer` 全部下推到 SQL）；
3. `EnsureStoredScopeAllowedAsync(db, scope, entity)`：校验**库中已存储单据**的权威归属（读取 / 详情 / 时间线 /
   修改 / 删除 / 状态变更之前）；
4. `EnsureProposedScopeAllowedAsync(db, scope, entity)`：校验**本次提交的拟议单据**的权威归属（新增 / 修改写入之前）。

特权账号（`AllowedCustomerIds == null`）在上述 2/3/4 中一律放行 —— 历史未关联单据照常可读可流转，
既有行为保持不变。受限账号若把单据改派到范围外订柜、或清空 `BookingId`，都会在写入前被拒绝。

## 4. 锁定写路由与并发协同

修改 / 删除 / 提交 / 审核 / 取消统一按下列顺序执行（整体包在 `IsolationLevel.Serializable` 事务内）：

1. 事务外先解析身份 / 菜单 / 数据范围（fail closed 时不进事务、不加锁、不写库）；
2. 读取本单头取 `BookingId`；
3. 对**来源订柜行**加 `UPDLOCK/HOLDLOCK`（`SELECT Id FROM db_owner.ContainerBookings WITH (UPDLOCK, HOLDLOCK) WHERE Id = @id`；未链接不加锁）；
4. 对**本单行**加 `UPDLOCK/HOLDLOCK`（`SELECT Id FROM db_owner.ContainerPreLoadings WITH (UPDLOCK, HOLDLOCK) WHERE Id = @id`）；
5. 锁内重新加载本单（含明细）→ 校验状态 → 复核权威客户范围 → ERP-353 权威链接 / ERP-348 下游取消护栏；
6. 置状态 / 替换明细 / 软删除并提交事务；任一步失败即回滚，库中字段 / 明细 / 状态 / 审计时间戳全部保持不变。

锁序固定为**「订柜行 → 预装柜单行」**：与既有 ERP-353「预装柜审核 / 订柜取消」共用同一把订柜行锁，
且与 ERP-348 装柜清单审核的本单行锁兼容（没有任何流程反向持锁），因此不会形成锁环。

保留的既有护栏：

- **ERP-353**：显式 `BookingId` 必须指向存在 / 未删除 / 已审核的订柜信息，且在菜单授权与客户范围之内；
  同一订柜下不同柜号的权威柜号链接冲突一律拒绝；
- **ERP-348**：取消预装柜单前，若存在「以本单为来源、未删除、已审核」的装柜清单则拒绝，解除只走既有显式取消流程。

## 5. 错误信息（API 直接返回）

全部通过既有 `BusinessException` 抛给既有异常中间件：

| 场景 | 错误码 | 文案 |
|---|---|---|
| 无身份 / 非法身份 / 账号已删除 | 2000 | `请先登录后再访问预装柜单` |
| 账号已禁用 | 2002 | `登录账号已禁用，不能访问预装柜单（fail closed）` |
| 非特权账号无 `pre-loading` 菜单 | 2002 | `当前账号没有「预装柜单」（pre-loading）模块授权：拒绝操作预装柜单（fail closed，不执行任何写入）` |
| 非特权账号未映射业务员 | 2002 | `当前账号未映射为业务员（预装柜单操作员），不能访问预装柜单（fail closed，不泄露任何范围外单据）` |
| 未关联 / 来源已删除的无主单据 | 2002 | `预装柜单未关联显式订柜信息（或其来源订柜已删除）：受限账号无权威客户归属，拒绝访问（fail closed，不泄露无主单据）` |
| 越客户范围 | 2002 | `当前账号的客户数据范围不包含该预装柜单来源订柜信息的客户：拒绝操作（fail closed，不泄露范围外单据）` |
| ERP-353 链接无效 / 柜号冲突 | 1001 / 1004 | 见 `docs/preloading-booking-link.md` |
| ERP-348 下游已审核装柜清单 | 1004 | 见 `docs/container-loading-fulfillment.md` |

## 6. 不改写项

- **不新增用户授权 / 菜单 / 权限模型**：菜单与数据范围沿用既有 `SeedData` 与 ERP-097 口径，测试只用既有 `pre-loading` 菜单；
- **不新增表 / 列 / 外键 / schema**，不改 `SchemaUpgrader` 与种子数据；不需要数据库迁移；
- **不改写历史**：不重写历史预装柜单的柜号、封条号、明细、状态与审计时间戳；不为历史单据补链接或回填；
- **保留来源单据审计与原始失败日志**：失败的编辑 / 状态变更只回滚，不产生半更新，也不清除既有审计痕迹；
- **不写库存 / 库存流水 / 财务 / 结算记录**，不产生下游单据；
- **不调用船公司 / 海关 / 报关行 / 货代等外部系统**，不做 EDI / API 对接；
- **不按单号等自由文本猜测归属**，不臆造封条号与参与方。

## 7. 测试

- 单元测试：`src/ERP.UnitTests/PreLoadingAuthorizationTests.cs`（内存库，覆盖 10 个路由的身份 / 账号状态 / 菜单
  fail closed、未映射业务员不降级、受限业务员列表数据库侧范围、未关联 / 来源缺失无主单据 fail closed、
  编辑他人或清空 / 改派范围外被拒绝且原单不变、状态变更与删除被拒绝且状态不变、特权账号保留历史访问、
  规则纯只读源码契约、控制器全部路由先授权与列表先范围后计数、授权仅继承基类）。
- 集成测试：`src/ERP.IntegrationTests/PreLoadingAuthorizationSqlServerTests.cs`（GUID 独占 `NEWERP_AUTOTEST` 护栏，
  真实 SQL Server，覆盖本人 / 他人客户读取、无主单据拒绝、禁用账号与撤销菜单、失败编辑不改动来源 / 明细 / 状态 / 审计，
  以及**两条独立连接**的并发编辑（不留撕裂状态）与并发审核（恰好一方成功））。
- 目标库护栏测试：`PreLoadingAuthorizationTargetGuardTests`（非专用实例 / 错误库名 / 非集成安全一律在任何数据库访问之前拒绝）。

## 8. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，
  且 `Integrated Security=true`；护栏在**任何数据库访问之前**校验失败即中止；
- 每次运行创建**全新的 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 不读取 `appsettings` / `.env` / 生产凭据；连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值；
- 测试种子一律使用 SQL Server 自增主键（不显式指定 Id），保留完整日志输出；
- 构建通过仅代表编译成功，**不代表本阶段验收通过**：真实 SQL 集成测试与阶段验收由调度器在专用目标上单独执行。

