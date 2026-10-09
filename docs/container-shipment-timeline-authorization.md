# 装柜出运证据时间线实时授权与权威客户数据范围（ERP-435）

## 1. 目标

把**既有**装柜出运证据时间线 / 跟踪工作台（`api/container/shipment-timeline`）的**三个只读路由**收敛到与
ERP-362 出运引用登记、ERP-365 里程碑证据**同一套**实时授权口径，消除「任何已认证账号只要知道源记录类型 + Id
或出运引用 Id，就能读取任意柜的 B/L、S/O、承运人、货代、报关行、港口、计划时间与里程碑实际证据」的越权读取：

- `GET /api/container/shipment-timeline`
- `GET /api/container/shipment-timeline/sources/{sourceType}/{sourceId}`
- `GET /api/container/shipment-timeline/references/{referenceId}`

复用既有分层架构、既有 `ContainerShipmentReference` / `ContainerShipmentMilestone` 两张登记册与既有响应 DTO；
不新增表 / 列 / 菜单 / 权限模型，不做报表与界面打磨，不写任何单据，也不联系承运人 / 海关 / 货代或任何外部系统。

## 2. 口径

| 项 | 口径 |
|---|---|
| 实时授权 | 每一路由先解析：身份缺失 / 非法 → 未认证；账号不存在 / 已删除 → 未认证；账号禁用 → 权限不足；非特权账号无任何既有源模块菜单（`booking` / `pre-loading` / `loading-list`）→ 权限不足；非特权账号未映射业务员 → 权限不足（fail closed） |
| 来源类型菜单 | 必须命中**该来源类型**对应的既有菜单：订柜信息 `booking`、预装柜单 `pre-loading`、装柜清单 `loading-list`；未知来源类型先按非法参数拒绝（不静默忽略） |
| 数据范围 | 复用既有 ERP-097 `SalespersonDataScopeService`；特权账号（超级管理员 / 系统内置角色 / 显式特权角色）不受限，**保留既有访问** |
| 客户归属 | 一律取源记录的**权威客户**：订柜信息 / 装柜清单取本单 `CustomerId`；预装柜单取 `ContainerPreLoading.BookingId` 归属的订柜信息客户；无权威归属的受限账号 fail closed（不猜、不推断） |
| 工作台范围 | 权威客户范围与允许来源类型在 **SQL 侧**下推到 `Count` 与分页**之前**，绝不「先查全量再内存过滤」；里程碑事件筛选与状态计数同样只作用于范围内的引用 |
| 详情范围 | 按显式源记录 → 先 `RequireSourceType` + `EnsureScopeAllowsSourceAsync`；按显式引用 → 先按父引用**持久化**的源记录类型 + Id 走 `EnsureScopeAllowsParentReferenceAsync`；任一步越范围一律 fail closed |
| 响应契约 | 既有 `ContainerShipmentTimelineShipmentDto` / `DetailDto` / `EventDto` / `SummaryDto` 不变；币种 / 单位分离与「`null` = 未知」语义不变；拒绝时无任何行 / 计数 / 汇总 |
| 不猜测 | 只认显式来源类型 + Id / 引用 Id 与显式订单归属：绝不按柜号 / S/O / B/L 等自由文本反查来源，不补链接、不回填、不改派 |
| 匿名 / 管理员回退 | **没有**：缺失身份不降级为管理员，空菜单不降级为全局可见，越范围一律 fail closed |

## 3. 授权入口与下推

时间线控制器每个路由先调用
`ShipmentReferenceAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId())` 解析实时身份 / 账号状态 /
既有源模块菜单 / 权威客户数据范围，并把结果 `ShipmentReferenceAccess` 传入 `ContainerShipmentTimelineService`
（**不新增任何授权**）：

1. `RequireSourceType(access, sourceType)`：来源类型必须落在既有源模块菜单授权内；
2. `EnsureScopeAllowsSourceAsync(db, access, sourceType, sourceId)`：单条源记录的权威客户必须在范围内；
3. `EnsureScopeAllowsParentReferenceAsync(db, access, parent)`：按父出运引用持久化的源记录类型 + Id 复核菜单与范围；
4. `ApplyScope(db, source, access)`：把允许来源类型 + 权威客户范围下推到出运引用查询（工作台在 `Count` 与分页之前）。

特权账号（`AllowedCustomerIds == null` 且具备全部三种来源类型）不做行级过滤，行为与既有读取完全一致；
受限账号只能读取本人客户的源记录、出运引用、里程碑事件与状态计数。

## 4. 错误信息（API 直接返回）

全部通过既有 `BusinessException` 抛给既有异常中间件：

| 场景 | 错误码 | 文案 |
|---|---|---|
| 无身份 / 非法身份 | 2000 | `请先登录后再访问装柜出运引用登记` |
| 账号不存在 / 已删除 | 2000 | `登录账号不存在或已删除，禁止访问装柜出运引用登记` |
| 账号已禁用 | 2002 | `登录账号已禁用，禁止访问装柜出运引用登记（fail closed）` |
| 非特权账号无任何既有源模块菜单 | 2002 | `当前账号没有「订柜信息」（booking）/「预装柜单」（pre-loading）/「装柜清单」（loading-list）任一既有模块授权：拒绝访问装柜出运引用登记` |
| 来源类型未授权 | 2002 | `当前账号没有「…」（…）模块授权：拒绝访问…的出运引用证据` |
| 未知来源类型 | 1001 | `源记录类型「…」不受支持：只允许 booking / pre-loading / loading-list` |
| 越范围 / 无权威归属源记录 | 2002 | `当前账号的客户数据范围不包含该源记录的客户：拒绝操作（fail closed，不泄露范围外单据）` |
| 越范围 / 孤儿父引用 | 2002 | `当前账号的客户数据范围不包含该源记录的客户：拒绝操作…`（父引用缺失时追加「父出运引用已不存在，无法解析其源记录的权威客户归属」） |
| 引用不存在 / 已删除 | 1004 | `出运引用不存在（Id=…）` |

## 5. 不改写项

- **不新增用户授权 / 菜单 / 权限模型**：菜单沿用既有 `SeedData`，数据范围沿用 ERP-097 口径；
- **不新增表 / 列 / 外键 / schema**，不改 `SchemaUpgrader` 与种子数据；
- **不改写历史**：不重写历史出运引用 / 里程碑的柜号、计划时间、承运人、货代与状态，也不回填归属；
- **不写库存 / 库存流水 / 财务 / 结算记录**，不产生下游单据；
- **不调用船公司 / 海关 / 报关行 / 货代等外部系统**；
- **不按自由文本猜测归属**，不推断缺失事件（缺失一律显示「无 / 未知」）。

## 6. 测试

- 单元测试：`src/ERP.UnitTests/ContainerShipmentTimelineTests.cs`（内存库；`BuildController` 统一注入既有特权身份，
  既有 ERP-059 契约全部保留，并新增受限账号工作台范围先于计数、越范围源记录 / 引用详情拒绝、未映射客户 fail closed）；
- 集成测试：`src/ERP.IntegrationTests/ContainerShipmentTimelineAuthorizationSqlServerTests.cs`（GUID 独占
  `NEWERP_AUTOTEST` 护栏，真实 SQL Server，覆盖本人 / 他人客户工作台读取与计数、来源类型菜单命中 / 未知类型、
  预装柜单菜单、越范围引用、已删除 / 不存在引用、禁用 / 已删除 / 撤销菜单身份与缺失身份拒绝、拒绝不改动任何行）；
- 目标库护栏测试：`ContainerShipmentTimelineAuthorizationTargetGuardTests`（非专用实例 / 错误库名 / 非集成安全
  一律在任何数据库访问之前拒绝）。

## 7. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，且 `Integrated Security=true`；
  护栏在**任何数据库访问之前**校验失败即中止；
- 每次运行创建**全新的 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 不读取 `appsettings` / `.env` / 生产凭据；连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值；
- 测试种子一律使用 SQL Server 自增主键（不显式指定 Id），保留完整日志输出。

## 8. 边界说明

本任务的确定性交付门为「Release 构建 + `ERP.UnitTests` 全套通过」。构建通过仅代表编译成功，
**不代表本阶段验收通过**：真实 SQL 集成测试与阶段验收由调度器在专用目标上单独执行。

装柜三单详情入口（`/api/container/bookings/{id}/shipment-timeline`、`/api/container/pre-loadings/{id}/shipment-timeline`、
`/api/container/loading-lists/{id}/shipment-timeline`）已由各自控制器在调用前完成同一口径的实时身份 / 菜单 /
权威客户范围授权（ERP-364 / ERP-431），因此这些内部调用沿用 `access = null`；本任务的 `allowed_paths` 只覆盖
时间线控制器与时间线服务，不改动上述三个控制器。
