# 旧销售订单专用路由（`api/v2/sales-orders`）读侧授权、行范围与翻页导航边界

> 任务：`ERP-411`「Scope dedicated legacy sales order paging and navigation through controlled read service」（阶段 3 核心业务流程完整性）。
> 关联：`ERP-410`（专用旧写入门禁，`docs/sales-order-proc-mutation-boundary.md`）、`ERP-405`（旧单据读侧受控服务，`docs/legacy-bill-read-scope.md`）、
> `ERP-404`（通用旧单据写入门禁）、`ERP-308`（旧单据导出族有限目录）、`ERP-097`（业务员客户数据范围）。

## 1. 背景与问题证据（修复前）

`SalesOrderProcController`（路由 `api/v2/sales-orders`）是**独立于** ERP-405 通用读侧 `BillProcController`
（`api/v2/bills`）的旧库读路径；`ERP-410` 只关闭了它的五个写动作，读侧仍在绕过授权：

- `GET api/v2/sales-orders`（`GetPaged`）直接以原生 SQL `SELECT COUNT(*)` / `OFFSET (page-1)*size` 计数并分页
  `db_owner.SalesOrder`，只按 `BillNo` / `Status` 过滤；
- `GET api/v2/sales-orders/navigate`（`Navigate`）在**全表**上取 `first/last/prev/next` 相邻行；
- 控制器只有类级 `[Authorize]`：不校验实时启用身份 / 既有「销售订单」功能菜单 / 业务员客户数据范围。

因此任何已登录账号都能读取**全部客户**的旧销售订单计数、金额与旧 `Oid` 标识，并可通过越权 `Oid` 在他人数据间翻页。

## 2. 复用的读侧门禁与受控服务（不新增第二套）

本任务**不新增**任何授权模型或读实现，直接复用 `ERP-405` 已落地的两项：

| 复用项 | 位置 | 作用 |
| --- | --- | --- |
| 读侧门禁 | `LegacyBillAuthorizationRules.EnsureReadAuthorizedAsync`（族 `sales-order`） | 实时身份 + 既有「销售订单」功能菜单 + 业务员客户数据范围 |
| 受控只读服务 | `ILegacyBillReadService` / `LegacyBillReadService`（`src/ERP.Infrastructure/Reports/`） | 有限参数化计数 / 分页 / 导航，范围先于读取 |

`SalesOrderProcController.LegacyFamilyKey = "sales-order"` 与 `BillProcController.Bills["sales-order"]`、
`LegacyBillReadCatalog` **同源**：同一旧库单体表 `SalesOrder`、同一既有功能菜单 `sales-order`、同一客户归属列 `CustId`。
导出菜单（`sales-order-export`）**绝不**当作模块权限；不新增 / 不修改任何菜单 / 角色 / 用户授权。

`EnsureReadAuthorizedAsync` 的顺序固定（控制器在任何计数 / 取行之前调用）：

1. **有限族解析**：读侧目录解析族键（本控制器族键为服务端常量，绝不来自请求体）；
2. **实时身份 + 既有功能模块授权**：缺失 / 非法 / 已删除按未认证拒绝（`2000`），已禁用按权限不足拒绝（`2002`），
   普通账号必须具备既有「销售订单」菜单（`sales-order`），特权账号（超级管理员 / 系统内置角色）沿用既有全部访问口径，
   但仍须通过实时身份校验；授权撤销 / 账号停用后下一次请求立即收敛（无缓存）。
3. **业务员客户数据范围**：复用唯一权威口径 `SalespersonDataScopeService`（ERP-097），每次请求重新解析。

## 3. 行归属与范围先于读取（受限账号 fail closed）

旧销售订单表按 **`CustId`** 显式登记客户归属（与 `docs/legacy-bill-read-scope.md` 第 3 节一致）：

- 特权账号（`AllowedCustomerIds == null`）→ `1=1`（不加行约束，沿用既有全部访问口径）；
- 受限业务员 → `[CustId] IN (@scope0, @scope1, …)`（**参数化**，按 Id 升序，稳定可复现）；
- 受限但可见客户集合为空 → `1=0`（恒假，看不到任何行）。

同一范围子句同时约束 **COUNT**、**分页 SELECT** 与 **导航**；`prev` / `next` 会在同一范围内先核验锚点，
不可访问（越权 / 不存在）锚点与「范围内没有更多」返回**同一结果**（`1002`），绝不泄露锚点存在性。

## 4. 有限参数化查询与专用响应投影（保持不变）

- **列白名单**：计数 / 分页 / 导航只读服务端常量列（`Oid` + ERP-308 授权导出列），绝无 `SELECT *` / 任意 SQL / 标识符 / 联接；
- **参数化**：关键字（`BillNo LIKE @kw`）、状态（`Status = @st`）、分页（`@off` / `@size`）、导航锚点（`@oid`）
  与范围集合（`@scopeN`）全部为参数；
- **稳定排序**：按 `Oid` 稳定排序（列表 `DESC`，导航 `first/last/prev/next` 语义不变），分页稳定可复现；
- **专用投影**：`api/v2/sales-orders` 的响应字段集与改造前**逐项一致**——
  列表投影 `Oid / BillNo / OrderDate / CustId / TotalAmount / DepositAmount / Status`（含定金），
  导航投影 `Oid / BillNo / OrderDate / CustId / TotalAmount / Status`（不含定金）；
  金额 / 币种 / 单位 / `null` 原值原样保留（`IsDBNull` → `null`，不做换算或格式化），绝不新增白名单之外的列。

## 5. 缺表 / 缺列 = 显式 environment-blocked（绝不回退规范表）

SQL Server `208`（无效对象名）/ `207`（无效列名，旧库 `Oid`-vs-规范 `Id` 差异）由受控服务映射为
`ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported`（`5002`，文案含 `environment-blocked`），
并明确「拒绝读取，绝不回退到规范（EF 复数）表」。本控制器绝不把旧 `Oid` 推断为规范 `Id`，也绝不静默返回空集冒充「没有数据」。

## 6. 验证证据

### 6.1 单元测试（`src/ERP.UnitTests/SalesOrderProcReadTests.cs`，内存库 + 真实既有身份 / 菜单 / 范围）

- **读侧门禁先于读取**：无身份不调用读服务（零读取，`SysOperationLogs` / `SysDingTalkLogs` 为空，无待写入变更）；
- **范围受限**：受限账号分页 `total` 与行只含授权客户，越权客户行绝不出现；专用投影字段集保持不变（列表含定金、导航不含定金），
  缺失授权列按 `null` 保留键；
- **翻页导航**：`first / last / prev / next` 全部受范围约束，范围内边界、越权锚点与不存在锚点均返回 `1002`（不泄露）；
- **拒绝矩阵**：已删除 → `2000`，已禁用 / 无菜单 / **仅导出菜单** → `2002`，撤销菜单后下一次请求立即收敛 → `2002`；
- **特权账号**：沿用既有全量口径（可见 + 隐藏客户行都在结果内，范围 `IsPrivileged`）；
- **专用 vs 通用一致**：同一授权记录下 `api/v2/sales-orders` 与 `api/v2/bills` 返回同一 `total`、`Oid` 与 `BillNo` 集合；
- **畸形 / 溢出**：页大小 `100000`、页码 `int.MaxValue`（偏移溢出）、注入式方向与非法 `Oid` 在打开查询之前拒绝（`1001`），零写入；
- **旧结构不可达**：真实受控服务在缺表 / 缺列时返回 `5002`（`environment-blocked`），绝不回退规范表。

### 6.2 真实隔离 SQL 测试（`src/ERP.IntegrationTests/SalesOrderProcReadSqlServerTests.cs`）

- **专用目标护栏**：必须在任何数据库访问之前精确命中 `(localdb)\NEWERP_AutoAcceptance` + 库名前缀 `NEWERP_AUTOTEST`
  + `Integrated Security=true`；错误实例 / 错误库名 / 非集成安全的连接串一律在 `AssertDedicatedTarget` 阶段被拒绝
  （`SalesOrderProcReadTargetGuardTests`）。每次运行只创建一个**全新 GUID 后缀库**，发现同名库已存在立即拒绝，
  **绝不 drop / reset / 复用**任何数据库，也绝不读取 `appsettings*.json` / `.env` / 生产凭据。
- **真实驱动 + 既有授权**：只在本隔离 GUID 库内补齐完整 NEWERP 结构与受控旧库结构（`db_owner.SalesOrder`）与两位客户的旧库行；
  以新播种的既有「销售订单」功能菜单授权 + 业务员客户数据范围驱动真实 `SalesOrderProcController` 与真实 `LegacyBillReadService`；
  不新增 / 不修改任何既有菜单 / 角色 / 用户授权，无匿名 / 管理员降级。
- **逐项证明**：受限业务员分页 `total` 与行只含授权客户（3 行）且不含隐藏客户；特权账号保留既有全量口径（5 行）；
  `first/last/prev/next` 均受范围约束，越权 / 不存在锚点与「没有更多」返回同一 `1002`（不泄露）；专用 vs 通用旧读侧一致；
  缺失 / 已删除身份 `2000`，已禁用 / 无菜单 / 仅导出菜单 / 已撤销菜单 `2002`；页大小 `100000` 与页码 `int.MaxValue` 拒绝 `1001`；
  注入式关键字按参数化处理（0 行，旧库结构完好）。
- **零写入证据**：每次拒绝 / 读取前后对 `SysOperationLogs` / `SysDingTalkLogs` / 规范业务表 `SalesOrders` / `StockIns` /
  `StockMovements`（**保留库存来源单据审计**）与旧库 `SalesOrder` 行数做只读快照，全部不变（本任务只新增只读路径）。
- **两个独立连接竞态**：①两条受限分页连接返回**一致**的范围内 `total` 与 `Oid` 集合（且都不含隐藏客户）；
  ②越权导航与仅导出菜单账号的查询在并发下分别一致 `1002` / `2002`，收尾快照证明零写入。
- **保留既有库存来源单据审计与原始失败日志**：测试只读取计数与只读快照，不删除 / 不清理任何既有行。

### 6.3 构建与回归

- `.NET 8` Release 构建（`NEWERP.sln`）通过，0 警告 / 0 错误（`ERP.IntegrationTests` 启用了 `TreatWarningsAsErrors`）。
- `ERP.UnitTests` 新增 `SalesOrderProcReadTests`（8 例全绿）与既有旧单据读 / 写、销售订单规范工作流回归保持不变。
- **构建完成不等于阶段验收**：真实 SQL 用例只有在受控 localdb 上真实执行通过才构成验收证据。

## 7. 明确边界（非目标）

- 不编辑任何存储过程 / 数据库结构 / 迁移脚本 / 种子数据；不新增表 / 列 / 索引。
- 不新增权限模型（菜单 / 角色 / 用户授权），不提供匿名或管理员降级，不把导出菜单当模块权限。
- 不改变旧报表 / 导出路由与 ERP-308 迁移退役门槛；不改变规范销售订单工作流（`api/sales-orders`）与转换工作流；
  本控制器的 ERP-410 写入门禁保持 fail closed。
- 不清理 / 不删除任何既有业务行、库存来源审计或既有失败日志；不执行生产凭据或生产数据操作。
- 浏览器验收按任务配置为 `browser_acceptance.required = false`：不运行真实 Edge / UI / 截图验收；
  旧 v2 销售订单页面（`wwwroot/js/sales-order-v2*.js`）未接入 SPA，本次只读路由收敛不产生在线 UI 回归。
