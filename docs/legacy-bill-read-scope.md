# 旧单据（`api/v2/bills`）读侧授权、行范围与详情边界

> 任务：`ERP-405`「Enforce live module and row scope on legacy document lists details and navigation」（阶段 3 核心业务流程完整性）。
> 关联：`ERP-404`（旧写入门禁，`docs/legacy-bill-mutation-boundary.md`）、`ERP-308`（旧单据导出族有限目录，Stage 2）、
> `ERP-403`（阶段 3 授权边界规划）、`ERP-406`（旧单据操作历史）。

## 1. 背景与问题证据

通用单据控制器 `BillProcController`（路由 `api/v2/bills`）承载 16 个旧单据族的读侧路由。**改造前**：

- `GET {billType}` 以 `SELECT *` 计数并分页（`OFFSET (page-1)*size`），只按 `BillNo` / `Status` 过滤；
- `GET {billType}/navigate` 的 `first/last/prev/next` 在**全表**上取相邻行；
- `GET {billType}/{oid}` 仅按 `Oid` 读取表头与副表明细（`SELECT *`），并返回全部列；
- `GET {billType}/defaults` 未做任何身份 / 授权校验。

该控制器上**只有类级 `[Authorize]`**：既没有实时启用身份校验，也没有既有功能模块授权，更没有业务员客户数据范围，
因此任何已登录账号都能读取全部旧单据行与任意 `Oid` 的详情（含越权客户 / 越权锚点）。

## 2. 读侧授权口径（复用既有权限模型，不新增授权）

新增 `LegacyBillAuthorizationRules.EnsureReadAuthorizedAsync`：

1. **有限族目录解析**：未知 / 空白 / 畸形族标识在访问任何数据之前以 `1001` 拒绝
   （严格复用 ERP-308 `LegacyBillExportCatalog` 的 16 族受控目录，不做数据库元数据发现）。
2. **实时身份**：缺失 / 非法身份 `2000`；账号不存在 / 已删除 `2000`；账号已禁用 `2002`（fail closed）。
3. **既有功能菜单授权**：普通账号必须实时具备该族**既有功能菜单**（`sales-order` / `purchase-order` / `inquiry` /
   `stock-in` / `stock-out` / `receipt` / `payment` / `deposit-apply` / `payment-apply` / `container-settlement` /
   `bulk-settlement` / `complaint` / `receiving-plan` / `booking` / `pre-loading` / `loading-list`）；
   **`*-export` 导出专用菜单绝不当作模块权限**，普通读取也**不要求**导出菜单（`sales-order` / `purchase-order` /
   `inquiry` 族的导出菜单与读侧授权解耦）。特权账号（超级管理员 / 系统内置角色）沿用既有全部访问口径，
   但仍须通过实时身份校验。
4. **业务员客户数据范围**：复用唯一权威口径 `SalespersonDataScopeService`（ERP-097），每次请求重新解析（不缓存）。

## 3. 显式行归属（受限账号 fail closed，绝不猜测）

新增 `LegacyBillReadCatalog`：表名与表头列**派生自** `LegacyBillExportCatalog`（ERP-308），
并按族**显式登记**行归属，登记结果必须命中该族授权表头白名单（否则类型初始化即失败）：

| 归属形状 | 族 | 归属列 | 受限账号行为 |
| --- | --- | --- | --- |
| 客户归属 | `sales-order` | `CustId` | 按客户数据范围约束 |
| 客户归属 | `inquiry` / `stock-out` / `receipt` / `deposit-apply` / `payment-apply` / `container-settlement` / `bulk-settlement` / `complaint` / `booking` / `loading-list` | `CustomerId` | 按客户数据范围约束 |
| **无权威归属** | `purchase-order` / `stock-in` / `payment` / `receiving-plan` / `pre-loading` | — | **受限账号 fail closed（`2002`）**；特权账号照常 |

**绝不**猜测「旧 `Oid` = 规范 `Id`」、**绝不**按客户名匹配、**绝不**在无法解析归属时放开为不受限；
受限账号在无归属族上仍可拿到的是明确的业务错误，而不是别的数据。

## 4. 范围先于读取（计数 / 分页 / 导航 / 详情同一子句）

`LegacyBillReadService.ResolveScopeClause`（纯函数，供单元测试直接验证）在打开连接之前裁决：

- 特权（`AllowedCustomerIds == null`）→ `1=1`（不加行约束）；
- 受限且有客户 → `[归属列] IN (@scope0, @scope1, …)`（**参数化**，按 Id 升序，稳定可复现）；
- 受限但可见客户集合为空 → `1=0`（恒假，看不到任何行）；
- 受限且族无权威归属 → `2002`（fail closed）；
- 受限且客户数超过受控上限（`MaxScopeCustomers = 1000`）→ `2002`（绝不生成无界条件）。

同一子句同时约束 **COUNT**、**分页 SELECT**、**导航** 与 **表头详情读取**；副表明细只在**表头已通过范围校验**后
按外键读取（关联父表），因此明细永远不会越过表头范围。

## 5. 有限、参数化的受控只读查询（无 `SELECT *`）

新增 `ILegacyBillReadService` / `LegacyBillReadService`（`src/ERP.Infrastructure/Reports/`）：

- **表头列白名单**：`Oid` + 该族 ERP-308 授权导出列（`LegacyBillExportCatalog.Columns`），
  绝不 `SELECT *`，因此新增的敏感列不会被旧路由顺带泄露；
- **副表定义白名单**：仅登记旧结构中确定存在的副表 / 外键 / 列（`sales-order → SalesOrderDetail(SalesOrderId)`、
  `pre-loading → ContainerPreLoadingDetail(PreLoadingId)`、`loading-list → ContainerLoadingDetail(LoadingListId)`）；
- **稳定排序**：表头与副表均按 `Oid` 升/降序稳定排序（`ORDER BY [Oid]`），分页稳定可复现；
- **参数化**：关键字（`BillNo LIKE @kw`）、状态（`Status = @st`）、分页（`@off` / `@size`）、导航锚点（`@oid`）
  与范围集合（`@scopeN`）全部为参数；表名 / 列名 / 外键只来自受控常量，绝不承载用户 SQL / 联接 / 标识符；
- **原值语义**：`null` / 原币 / 原单位 / 金额原样保留（`IsDBNull` → `null`，不做换算或格式化）；
- **只读**：不写库、不调用任何存储过程、不写日志 / 通知（拒绝只写一条结构化告警，不改数据）。

**有意兼容省略（显式记录）**：

1. 响应只包含上述有限白名单列（改造前是整行），业务未使用的敏感列不再返回；
2. 列表每页上限固定 `MaxPageSize = 200`（页大小 `0` / 负数 / `> 200` → `1001`）；改造前 `PageQuery.Normalize`
   允许最大 `100000`（前端「不限」），该行为**有意收敛**，需要更大导出时使用既有 `GET {billType}/export`
   （ERP-308 迁移退役门槛不变）；
3. 未登记副表明细的族（13 族）详情只返回表头（`details` 为空集合），不猜测旧副表结构。

## 6. 分页与算术溢出

`page >= 1`、`1 <= pageSize <= 200`、`offset = (long)(page-1) * pageSize` 并拒绝 `offset > int.MaxValue`
（`1001`「分页偏移超出安全范围」）。全部校验发生在**打开连接之前**。

## 7. 缺表 / 缺列 = 显式 environment-blocked（绝不回退规范表）

SQL Server `208`（无效对象名）/ `207`（无效列名，旧库 `Oid`-vs-规范 `Id` 差异）映射为
`ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported`（`5002`，文案含 `environment-blocked`），
并明确「拒绝读取，绝不回退到规范表」：旧路由绝不静默返回规范（EF 复数）表 `SalesOrders` / `PurchaseOrders` / …
的记录，也绝不静默返回空集冒充「没有数据」。其他 `SqlException` 同样显式 environment-blocked。

## 8. 验证证据

### 8.1 单元测试（`src/ERP.UnitTests/LegacyBillReadTests.cs`，内存库 + 真实既有身份 / 菜单 / 范围）

- **有限目录**：读侧恰好覆盖 `BillProcController.Bills` 全部 16 族；表名与表头列逐一等于 ERP-308 授权导出白名单
  （`Oid` 打头）；族键等于既有功能菜单编码且**不含** `export`；未知 / 畸形族标识拒绝。
- **显式行归属**：客户归属列必命中授权白名单；无归属族（`purchase-order` / `stock-in` / `payment` /
  `receiving-plan` / `pre-loading`）显式 `None`；仅 3 族登记副表明细。
- **范围子句（纯函数）**：特权 `1=1`；受限按归属列参数化（`[CustId] IN (@scope0, @scope1)` 且值升序）；
  空集合 `1=0`；无归属族 `2002`（特权照常 `1=1`）；客户数超上限 `2002`。
- **边界先于查询**：页大小 `0` / `-1` / `201` / `100000` 与页码 `0` / `int.MaxValue`（偏移溢出）→ `1001`；
  未知族 / 非法方向 / 非法 `Oid` → `1001`；旧结构不可达 → `5002`（`environment-blocked`）。
- **读侧授权**：缺失 / 非法身份 `2000`，已删除 `2000`，已禁用 `2002`，无菜单 `2002`，**仅导出菜单 `2002`**，
  未知族 `1001`；受限账号解析出实时客户范围（可见客户在集合内、隐藏客户不在），特权账号仍为全量。
- **控制器**：未授权身份**不调用**读服务（零读取）；受限账号分页 / 详情只含授权客户（`total` 与行都被范围约束）；
  越权锚点导航与越权 `Oid` 详情返回「没有更多单据」/「单据不存在」（不泄露）；默认值同样先授权；
  拒绝路径 `SysOperationLogs` / `SysDingTalkLogs` 为空且无待写入变更。


### 8.2 真实隔离 SQL 测试（`src/ERP.IntegrationTests/LegacyBillReadSqlServerTests.cs`）

- **专用目标护栏**：必须在任何数据库访问之前精确命中 `(localdb)\NEWERP_AutoAcceptance` + 库名前缀
  `NEWERP_AUTOTEST` + `Integrated Security=true`；错误实例 / 错误库名 / 非集成安全的连接串一律在
  `AssertDedicatedTarget` 阶段被拒绝（`LegacyBillReadTargetGuardTests`）。每次运行只创建一个**全新 GUID 后缀库**，
  发现同名库已存在立即拒绝，**绝不 drop / reset / 复用**任何数据库，也绝不读取 `appsettings*.json` / `.env` /
  生产凭据（连接串只来自进程环境变量或专用 localdb 默认值）。
- **真实驱动 + 既有授权**：只在本隔离 GUID 库内补齐受控旧库结构（`db_owner.SalesOrder` /
  `db_owner.SalesOrderDetail` / `db_owner.PurchaseOrder`）与两位客户的旧库行；以**新播种的既有功能菜单授权 + 业务员
  客户数据范围**驱动真实 `BillProcController` 与真实 `LegacyBillReadService`；不新增 / 不修改任何既有菜单 / 角色 /
  用户授权，无匿名 / 管理员降级。`payment` 等族刻意不建旧表 → `5002`。
- **逐项证明**：受限业务员的分页 `total` 与行只含授权客户（3 行）且不跨页串行；特权账号保留既有全量口径（5 行）；
  `first/last/prev/next` 均被范围约束，越权锚点与「没有更多」返回同一 `1002`（不泄露）；范围内详情返回关联副表明细
  （1 行），越权 `Oid` 返回 `1002` 且不读取副表；无归属族 `2002`（含详情）；缺失 / 已删除身份 `2000`，
  已禁用 / 无菜单 / 仅导出菜单 `2002`，未知族 `1001`，默认值同样先授权；页大小 `100000` 与页码 `int.MaxValue` 拒
  `1001`；注入式关键字按参数化处理（0 行，旧库结构完好）。
- **零写入证据**：每次拒绝 / 读取前后对 `SysOperationLogs` / `SysDingTalkLogs` / 规范业务表 `SalesOrders` /
  `StockIns` / `StockMovements`（**保留库存来源单据审计**）与旧库 `SalesOrder` / `SalesOrderDetail` 行数做只读快照，
  全部不变（本任务只新增只读路径，不存在任何写入或降级）。
- **两个独立连接竞态**：每条竞态用例新建两个独立 `DbContext` / 连接 / 控制器并门闩对齐并发：
  ①两条受限分页连接返回**一致**的范围内 `total` 与 `Oid` 集合（且都不含隐藏客户）；
  ②越权详情与仅导出菜单账号的导航在并发下分别一致 `1002` / `2002`，收尾快照证明零写入。
- **保留既有库存来源单据审计与原始失败日志**：测试只读取计数与只读快照，不删除 / 不清理任何既有行。

### 8.3 构建与回归

- `.NET 8` Release 构建（`NEWERP.sln`）通过，0 警告 / 0 错误（`ERP.IntegrationTests` 启用了 `TreatWarningsAsErrors`）。
- 既有 `ERP.UnitTests` 全套回归与既有旧单据导出 / 报表迁移回归保持不变（本任务只新增只读路径 + 在 4 个读侧端点前加门禁）。
- **构建完成不等于阶段验收**：真实 SQL 用例只有在受控 localdb 上真实执行通过才构成验收证据。

## 9. 明确边界（非目标）

- 不编辑任何存储过程 / 数据库结构 / 迁移脚本 / 种子数据；不新增表 / 列 / 索引。
- 不新增权限模型（菜单 / 角色 / 用户授权），不提供匿名或管理员降级，不把导出菜单当模块权限。
- 不改变旧报表 / 导出路由与 ERP-308 迁移退役门槛（`GET {billType}/export` 完全不变）；
  不改变导入模板下载路由；旧单据写路径仍由 `ERP-404` 门禁 fail closed；旧单据操作历史（`{oid}/logs`）的数据授权属 `ERP-406`。
- 不触碰规范业务路由（`api/sales-orders` / `api/purchase-orders` / `api/inquiries` / `api/stock-ins` /
  `api/stock-outs` / `api/finance/*` / `api/container/*`）与规范生命周期守卫。
- 不清理 / 不删除任何既有业务行、库存来源审计或既有失败日志；不执行生产凭据或生产数据操作。

