# 装柜清单实时授权与权威客户数据范围（ERP-364）

## 1. 目标

把**既有**装柜清单（`api/container/loading-lists`）的**每一个读取与写入路由**都收敛到同一套实时授权口径，并解决
一柜多客户（ERP-041 参与方）与上游共享出运带来的**跨客户泄露**风险：受限账号必须对单据的**每一个有效参与方客户**
以及**显式上游（预装柜单 → 订柜信息）客户**同时具备数据范围权限，历史单客户单据按持久化 `CustomerId` 判定，
无权威归属一律 fail closed；特权账号保留历史访问。

复用既有分层架构与既有单据流转（创建 / 修改 / 提交 / 审核 / 取消 / 删除）、既有 ERP-041 参与方维护、
既有 ERP-348 累计已审核装柜数量护栏与 ERP-363 上游预装柜单链接护栏；不做报表、不做界面视觉 / 样式 / 导出打磨，
不写库存 / 财务，也不调用任何外部跟踪系统。

## 2. 口径

| 项 | 口径 |
|---|---|
| 适用路由 | 列表、详情、出运跟踪、出运时间线、费用分摊证据、参与方读取、参与方新增 / 修改 / 置主 / 停用 / 启用 / 删除、新增、修改、提交、审核、取消、删除 |
| 实时授权 | 每一路由先解析：身份缺失 / 非法 → 未认证；账号不存在 / 已删除 → 未认证；账号禁用 → 权限不足；非特权账号无既有 `loading-list` 菜单 → 权限不足；非特权账号未映射业务员 → 权限不足（fail closed） |
| 一柜多客户 | 存在「启用、未删除」参与方时按**参与方客户**判定：必须对**每一个**有效参与方客户都有权限（否则视为通过共享柜泄露他人客户） |
| 历史单客户 | 无有效参与方时按持久化 `ContainerLoadingList.CustomerId` 判定；`CustomerId <= 0` 视为**无权威归属**，受限账号 fail closed |
| 上游共享出运 | 显式 `ContainerLoadingList.PreLoadingId` → `ContainerPreLoading.BookingId` → `ContainerBooking.CustomerId`（均要求未删除）解析出的客户也必须在范围内；未链接视为无上游约束 |
| 数据范围 | 复用既有 ERP-097 `SalespersonDataScopeService`；特权账号（超级管理员 / 系统内置角色 / 显式特权角色）不受限，**保留历史访问** |
| 列表范围 | `ApplyScope` 在 **SQL 侧**把范围（参与方 + 上游 + 兼容客户字段）下推到 `Count` 与分页之前，绝不先查全量再内存过滤 |
| 校验时点 | 新增 / 修改在**写入任何字段与替换明细之前**校验「拟议」范围；参与方维护在**改写参与方行与兼容客户字段之前**校验「完整拟议」范围；提交 / 审核 / 取消 / 删除在**锁内重新加载后**复核范围，失败一律不改库 |
| 不猜测 | 只认显式参与方客户与显式 `PreLoadingId → BookingId → 订柜客户`：绝不按单号 / 柜号等自由文本反查来源，不补链接、不回填、不臆造归属 |
| 并发 | 修改 / 提交 / 审核 / 取消 / 删除 / 参与方维护都在 `IsolationLevel.Serializable` 事务内按既有锁序（来源预装柜单行 → 本单行，`UPDLOCK, HOLDLOCK`）串行化 |

## 3. 权威客户归属与数据库侧范围

`LoadingListAuthorizationRules`（ERP-364，纯只读判定）提供以下入口：

1. `EnsureAuthorizedAsync(db, userId)`：实时身份 → 账号状态 → 既有 `loading-list` 菜单 → 权威数据范围，返回 `SalespersonDataScope`；
2. `ApplyScope(source, db, scope)`：特权账号不过滤；受限账号只保留满足「无范围外有效参与方」且「（有有效参与方 或
   兼容客户在范围内）」且「显式上游客户在范围内」（`Exists` / `join` 全部下推到 SQL）的装柜清单；
3. `EnsureStoredScopeAllowedAsync(db, scope, entity)`：校验**库中已存储单据**的权威归属（读取 / 详情 / 时间线 /
   参与方读取 / 修改 / 删除 / 状态变更之前）；
4. `EnsureProposedScopeAllowedAsync(db, scope, entity, proposedCustomerId, proposedPreLoadingId)`：校验**本次提交的拟议单据**
   （新增 / 修改写入之前）；
5. `EnsureParticipantScopeAllowedAsync(db, scope, loadingList, proposedActiveCustomerIds, proposedCustomerId)`：校验参与方维护的
   **完整拟议范围**（新增 / 修改 / 置主 / 停用 / 启用 / 删除参与方之前）；
6. `ResolveUpstreamCustomerIdAsync` / `LoadActiveParticipantCustomerIdsAsync`：有界只读查询。

特权账号（`AllowedCustomerIds == null`）在上述入口中一律放行 —— 历史单客户、共享柜与无主单据照常可读可流转，
既有行为保持不变。受限账号把单据改派到范围外客户 / 上游、清空兼容客户字段、或新增 / 改派范围外参与方，
都会在写入前被拒绝。

## 4. 锁定写路由与并发协同

修改 / 提交 / 审核 / 取消 / 删除 / 参与方维护统一按下列顺序执行：

1. 事务外先解析身份 / 账号状态 / 菜单 / 数据范围（fail closed 时不进事务、不加锁、不写库）；
2. 读取本单头取 `PreLoadingId`；
3. 对**来源预装柜单行**加 `UPDLOCK/HOLDLOCK`（`SELECT Id FROM db_owner.ContainerPreLoadings WITH (UPDLOCK, HOLDLOCK) WHERE Id = @id`；未链接不加锁）；
4. 对**本装柜清单行**加 `UPDLOCK/HOLDLOCK`（`SELECT Id FROM db_owner.ContainerLoadingLists WITH (UPDLOCK, HOLDLOCK) WHERE Id = @id`）；
5. 锁内重新加载本单（含明细）→ 校验状态 → 复核权威客户范围 / 拟议范围 → ERP-348 累计数量与 ERP-363 上游链接护栏；
6. 置状态 / 替换明细 / 改写参与方 / 软删除并提交事务；任一步失败即回滚，库中字段 / 明细 / 参与方 / 状态 / 审计时间戳全部保持不变。

锁序固定为**「预装柜单行 → 装柜清单行」**：与既有装柜审核共用同一把预装柜单行锁，并与 ERP-363 上游预装柜写入
保持同一锁序，任何流程都不反向持锁，因此不会形成锁环。

保留的既有护栏：

- **ERP-348**：显式 `PreLoadingId` 必须存在 / 未删除 / 已审核，且明细商品在来源内、数量为正，审核时累计已审核装柜数量
  不得超过来源授权数量；未链接的历史单据不做累计校验；
- **ERP-363**：上游预装柜单的权威链接与客户范围护栏（本任务在装柜侧再次以「显式上游客户」复核，绝不通过共享出运泄露他人客户）；
- **ERP-041**：参与方唯一性、主参与方唯一性与兼容客户字段同步语义全部保留。

## 5. 错误信息（API 直接返回）

全部通过既有 `BusinessException` 抛给既有异常中间件：

| 场景 | 错误码 | 文案 |
|---|---|---|
| 无身份 / 非法身份 | 2000 | `请先登录后再访问装柜清单` |
| 账号已删除 | 2000 | `登录账号不存在或已删除，禁止访问装柜清单` |
| 账号已禁用 | 2002 | `登录账号已禁用，禁止访问装柜清单（fail closed）` |
| 非特权账号无 `loading-list` 菜单 | 2002 | `当前账号没有「装柜清单」（loading-list）模块授权：拒绝访问装柜清单（fail closed，不返回 / 不修改任何装柜清单数据）` |
| 非特权账号未映射业务员 | 2002 | `当前账号未映射为业务员（装柜清单操作员），不能访问装柜清单（fail closed，不泄露任何范围外单据）` |
| 有效参与方客户越范围 | 2002 | `当前账号的客户数据范围不包含该装柜清单的有效参与方客户：拒绝操作（fail closed，不泄露共享柜中其他客户的数据）` |
| 显式上游订柜客户越范围 | 2002 | `当前账号的客户数据范围不包含该装柜清单显式上游（预装柜单 → 订柜信息）客户：拒绝操作（fail closed，不泄露共享出运的其他客户）` |
| 兼容客户字段越范围 | 2002 | `当前账号的客户数据范围不包含该装柜清单的客户：拒绝操作（fail closed，不泄露范围外单据）` |
| 无权威客户归属（无有效参与方且 CustomerId 非正） | 2002 | `装柜清单没有可判定的权威客户归属（无有效参与方且兼容客户字段缺失）：受限账号拒绝访问（fail closed，不泄露无主单据）` |
| ERP-348 链接 / 累计超装 | 1001 / 1004 | 见 `docs/container-loading-fulfillment.md` |

## 6. 不改写项

- **不新增用户授权 / 菜单 / 权限模型**：菜单与数据范围沿用既有 `SeedData` 与 ERP-097 口径，测试只用既有 `loading-list` 菜单；
- **不新增表 / 列 / 外键 / schema**，不改 `SchemaUpgrader` 与种子数据；不需要数据库迁移；
- **不改写历史**：不重写历史装柜清单的客户、柜号、明细、参与方、状态与审计时间戳；不为历史单据补链接或回填；
- **保留来源单据审计与原始失败日志**：失败的编辑 / 状态变更 / 参与方维护只回滚，不产生半更新，也不清除既有审计痕迹；
- **不写库存 / 库存流水 / 财务 / 结算记录**，不产生下游单据，也不按数量 / 体积 / 金额分摊费用；
- **不调用船公司 / 海关 / 报关行 / 货代等外部系统**，不做 EDI / API 对接；
- **不按单号等自由文本猜测归属**，不臆造封条号、参与方与主客户。

## 7. 测试

- 单元测试：`src/ERP.UnitTests/LoadingListAuthorizationTests.cs`（内存库，覆盖 17 个路由的身份 / 账号状态 / 菜单 fail closed、
  未映射业务员不降级、受限业务员列表数据库侧范围（参与方 / 上游）、多客户单据缺任一参与方拒绝、上游共享出运越范围拒绝、
  无权威归属 fail closed、被拒绝的参与方维护 / 拟议改派 / 状态变更 / 删除不改动参与方·字段·明细·状态·审计、
  特权账号保留历史访问、规则纯只读源码契约、控制器全部路由先授权与列表先范围后计数且写路由共用锁与事务、授权仅继承基类）；
  `ContainerLoadingParticipantTests` / `ContainerLoadingFulfillmentTests` 统一注入既有特权身份并保留既有语义。
- 集成测试：`src/ERP.IntegrationTests/LoadingListAuthorizationSqlServerTests.cs`（GUID 独占 `NEWERP_AUTOTEST` 护栏，
  真实 SQL Server，覆盖本人 / 他人客户读取、多客户共享柜拒绝、撤销菜单与禁用账号、失败参与方维护不改动原行与审计，
  以及**两条独立连接**的「参与方置主 与 审核」竞争（不留撕裂状态）与并发审核（恰好一方成功））。
- 目标库护栏测试：`LoadingListAuthorizationTargetGuardTests`（非专用实例 / 错误库名 / 非集成安全一律在任何数据库访问之前拒绝）。
- ERP-431：`PreLoadingAuthorizationTests` / `LoadingListAuthorizationTests`（出运跟踪路由的本人可读、他人 / 无主 / 共享柜 /
  上游越范围拒绝、已删除 / 不存在受控未找到、禁用 / 已删除 / 撤销菜单身份与无菜单 fail closed、特权账号历史只读、
  授权先于单据解析的源码契约，以及被拒绝时预装柜 / 装柜清单 / 参与方 / 订柜 / 出运引用 / 里程碑不变）与
  `PreLoadingAuthorizationSqlServerTests` / `LoadingListAuthorizationSqlServerTests`（真实 SQL 上的同类场景）覆盖。

## 8. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，且 `Integrated Security=true`；
  护栏在**任何数据库访问之前**校验失败即中止；
- 每次运行创建**全新的 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 不读取 `appsettings` / `.env` / 生产凭据；连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值；
- 测试种子一律使用 SQL Server 自增主键（不显式指定 Id），保留完整日志输出；
- 构建通过仅代表编译成功，**不代表本阶段验收通过**：真实 SQL 集成测试与阶段验收由调度器在专用目标上单独执行。

## 9. 边界说明

本任务的确定性交付门为「Release 构建 + `ERP.UnitTests` 全套通过」。

ERP-431 已把**出运跟踪（`{id}/shipment-tracking`）**路由按本文同一口径接入本护栏：`ContainerPreLoadingController` 复用
`PreLoadingAuthorizationRules`、`ContainerLoadingListController` 复用 `LoadingListAuthorizationRules`（不新增任何授权 / 菜单 /
表列），先实时授权与权威客户范围，再解析持久化单据与订柜引用；范围外 / 已删除 / 不存在单据与出运时间线返回同一受控错误，
绝不泄露柜号 / 订柜号 / ETD / ETA / 报关行值，且 `ContainerShipmentTrackingDto`、`null = 未知` 语义与只读行为完全不变。
`ContainerShipmentTrackingTests`（在本任务 `allowed_paths` 内）改为注入既有特权身份，ERP-040 只读回显语义与既有断言全部保留；
受限范围 / 拒绝 / 未找到场景在 `PreLoadingAuthorizationTests` / `LoadingListAuthorizationTests` 与两条真实 SQL 集成测试中覆盖。

仍暂未接入本护栏的是**单证带入 / 生成**路由（`TradeDocumentGenerationTests` / `TradeDocumentLineSnapshotTests` 以无身份控制器
直接调用，且不在本任务 `allowed_paths` 内）；后续如放开 `allowed_paths`，可对上述路由按同一口径补齐。

