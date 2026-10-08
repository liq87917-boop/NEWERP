# 销售订单变更申请（`api/sales-order-change-requests`）实时授权与来源实时归属

> 任务：`ERP-414`「Authorize sales order change request proposals through live source ownership」（阶段 3 核心业务流程完整性）。
> 关联：`ERP-413`（规范销售订单执行证据入口实时授权，`docs/sales-order-execution-authority.md`）、
> `ERP-047`（销售订单变更申请登记，`docs/销售订单变更申请登记说明.md`）、`ERP-401`（销售订单来源血缘护栏，
> `docs/sales-order-source-lineage.md`）、`ERP-097`（业务员客户数据范围，`docs/业务员数据范围说明.md`）。

## 1. 背景与问题证据（修复前）

变更申请控制器 `SalesOrderChangeRequestController` 只有类级 `[Authorize]`，所有数据与状态路由都直接调用
`SalesOrderChangeRequestService`，**不解析**实时身份 / 既有「销售订单」功能菜单 / 业务员客户数据范围：

| 路由 | 修复前行为 |
| --- | --- |
| `GET`（台账） | 直接 `ListAsync`，返回**全部**来源订单的申请（含金额与客户归属），无范围过滤 |
| `GET source-options` | 直接返回**全部**未删除订单候选，无客户范围 |
| `GET by-source` | 按显式 `salesOrderId` 直接返回该来源订单的申请，无归属复核 |
| `GET {id}` | 直接 `GetAsync`，按 Id 返回任意申请详情 |
| `POST`（登记） | 直接 `CreateDraftAsync`，可对**任意**存在的销售订单登记申请并**先发号** |
| `PUT {id}`（编辑） | 直接替换拟议明细，无归属复核 |
| `POST {id}/submit`、`POST {id}/cancel` | 直接状态流转，无归属复核 |

因此任何已登录账号只要**猜到一张范围外订单 / 申请 Id**，就能读取或登记其变更申请
（含来源快照、拟议金额与客户归属），或篡改他人的变更申请状态。

**这是运营记录入口的授权缺口，不是「再发布一份报表」**：本任务只收敛入口授权，不新增报表、不改写任何业务口径。

## 2. 集中化的变更申请护栏（`SalesOrderChangeRequestAuthorizationRules`）

新增 `src/ERP.Application/Services/SalesOrderChangeRequestAuthorizationRules.cs`（**唯一权威口径**），
调用方（控制器 / 服务）在任何读取 / 写入之前调用：

| 方法 | 作用 |
| --- | --- |
| `EnsureAccessAuthorizedAsync(db, userId)` | 数据 / 状态入口完整授权：复用 `SalesOrderExecutionAuthorizationRules.EnsureReadAuthorizedAsync`（ERP-413 规范销售订单口径）= 实时身份 + 既有「销售订单」菜单 + ERP-097 数据范围 |
| `ApplySourceScope(db, scope, query)` | 按**来源订单实时归属**把台账范围下推到数据库（相关子查询 `SalesOrders.Any(o => o.Id == r.SalesOrderId && !o.IsDeleted && allowed.Contains(o.CustomerId))`） |
| `EnsureSourceOrderAllowedAsync(db, scope, salesOrderId)` | 显式来源订单 Id 的归属复核（登记 / 指定来源清单之前） |
| `EnsureRequestAllowedAsync(db, scope, requestId)` | 已存申请的归属复核（详情 / 编辑 / 提交 / 取消之前），返回在库申请 |

口径（fail closed，绝不缓存，每次请求重新解析）：

1. **实时身份 + 既有菜单**：`userId` 缺失 / 非法 / 账号不存在 / 已删除按未认证拒绝（`Err:Unauthorized`）；
   账号已禁用按权限不足拒绝（`Err:Forbidden`）；非特权账号必须实时具备既有「销售订单」（`sales-order`）功能菜单，
   **导出专用菜单 `sales-order-export` 绝不当作模块权限**。**不新增任何菜单 / 角色 / 用户授权**。
2. **唯一权威数据范围**：只复用 `SalespersonDataScopeService`（ERP-097）；归属严格按**来源销售订单当前持久化的
   `SalesOrder.CustomerId`** 判定 —— 绝不按单号文本、金额或相似度推断。
3. **拟议字段不授予归属**：拟议客户（`ProposedCustomerId`）与来源客户历史快照（`SourceCustomerId`）**都不是**
   授权依据；来源被改派后一律以**实时来源订单**为准。
4. **来源不可解析 fail closed**：受限账号不能读取「实时来源订单缺失 / 已删除 / 无法解析归属」的申请；
   特权账号保留既有不受限历史访问（显式声明于 §4）。
5. **非披露错误**：范围外 / 已删除 / 不存在的来源与申请返回**同一**受控错误「变更申请或来源销售订单不存在」
   （`Err:NotFound`），授权先于任何计数 / 分页 / 发号 / 明细替换 / 状态变更。

## 3. 逐入口接线（先授权、后读取 / 写入）

- **台账 / 来源候选 / 指定来源清单 / 详情**：先 `EnsureAuthorizedAsync()` 解析范围，再把范围下推给服务；
  台账在 `CountAsync` 与分页**之前**应用 `ApplySourceScope`（`source-options` 在 `Take` 之前按客户范围过滤
  `SalesOrders`），绝不「先查全量再内存过滤」。
- **登记**：`EnsureSourceOrderAllowedAsync(拟议来源)` → 才冻结快照、**发号**与写入；
  被拒请求**绝不发号、绝不落任何行**。
- **编辑**：先复核**持久化来源**与（如显式提交）**拟议来源**归属 → 才替换拟议明细。
- **提交 / 取消**：先复核持久化来源归属 → 才状态流转并返回成功响应。
- **进程内直调边界**：真实 HTTP 请求（`Request.Path` 已赋值）一律执行授权；仅「既无任何登录身份、又不在 HTTP
  请求管线内」的进程内调用免授权（不可能由外部请求到达，见 `RequiresLiveAuthorization()`，与 ERP-413 / ERP-371 同源口径）。
  **真实匿名请求因处于请求管线内一律 fail closed（未认证）**。

## 4. 保留既有登记口径与边界（不改写、不新增）

- 授权只发生在读取 / 写入之前，**不改写**任何既有拟议算法与响应 DTO 契约：金额仍由
  `SalesOrderAmountRules`（销售订单唯一权威算法）重算，来源快照、差异对照、来源变化提示与状态机（草稿 / 已提交 / 已取消）
  全部保持不变。
- **ERP-047 拟议边界保持**：不审核、不套用、不自动转换、不发号生效、不提供硬删除；已取消与已提交的申请对其有权限账号
  仍是只读证据。
- 被拒绝的请求**零写入**：不改写来源销售订单与报价单 / 出库 / 装柜与出运 / 收款与发票 / 佣金 / 库存 / 单证 / 财务记录，
  不删除历史证据与审计留痕，也不产生任何申请行或消耗申请单号。
- **不新增**表 / 列 / 索引 / 菜单 / 权限模型，**不新增任何用户授权**，不提供匿名或管理员降级，不做旧库 `Oid` → 规范 `Id` 推断。
- 每个被拒请求都由既有全局中间件 `ExceptionHandlingMiddleware` 记录**有界**告警（仅错误文案 / 错误码 / 请求路径），
  绝不回声请求载荷、金额或密钥。

## 5. 验证证据

### 5.1 单元测试（`src/ERP.UnitTests/SalesOrderChangeRequestAuthorizationTests.cs`，内存库 + 真实既有身份 / 菜单 / 范围）

11 例（全绿）：

- **台账范围下推**：4 张申请（本人 / 他人来源 / 来源已删除 / 来源缺失）只返回本人来源的 1 张，`Total == 1`；
  后三张的来源客户快照与拟议客户都刻意写成**受限账号可见**的本人客户，证明二者都不是授权依据；
- **详情统一非披露错误**：他人来源 / 来源已删除 / 来源缺失 / 申请不存在在 `GetById` 返回同一 `Err:NotFound` + 同一文案；
- **指定来源清单 / 来源候选**：`by-source` 本人放行、范围外 / 已删除 / 不存在来源同一非披露错误；`source-options`
  只返回本人客户、未删除的订单；
- **登记 / 编辑 / 提交 / 取消**：本人来源成功（拟议 200 / 定金 60，编辑后提交冻结、取消留痕）；
  范围外 / 已删除 / 不存在 / 伪造拟议来源全部同一非披露错误，被拒请求**零写入**（申请行数只增本人那一次的 1 行，
  来源订单与明细行数、金额、数量不变）；
- **授权先于发号**：被拒的登记请求**绝不推进变更申请单号流水**（`SysDocumentNumberRules.CurrentSequence` 不变），
  仅本人来源登记推进一次；
- **来源改派**：来源订单改派他人客户后，历史来源客户快照仍是本人客户，但受限账号立即 fail closed（详情 + 台账都不可见），
  特权账号保留既有不受限历史访问；
- **身份 / 菜单拒绝矩阵**：无身份 / 已删除 → 未认证；已禁用 / 无菜单 / 仅导出菜单 / 已撤销菜单 → 权限不足，
  逐入口（台账 / 详情 / 来源候选 / 指定来源 / 登记 / 编辑 / 提交 / 取消）断言错误码与文案，且收尾快照零变化；
- **特权账号**：保留既有全量口径（可读他人 / 来源缺失申请、可在他人来源登记）；
- **进程内直调**：无 HTTP 管线且无身份的进程内直调保持既有免授权口径；
- **接线契约**：控制器 8 条数据 / 状态路由都解析实时授权（无 `AllowAnonymous`），服务按
  `ApplySourceScope` / `EnsureSourceOrderAllowedAsync` 下推，规则类复用 ERP-413 与 ERP-097 且归属使用来源订单**实时** `CustomerId`。

### 5.2 真实隔离 SQL 测试（`src/ERP.IntegrationTests/SalesOrderChangeRequestAuthorizationSqlServerTests.cs`）

11 例通过（7 例真实控制器用例 + 4 例专用目标护栏 Theory）：

- **专用目标护栏**：必须在任何数据库访问之前精确命中 `(localdb)\NEWERP_AutoAcceptance` + 库名前缀 `NEWERP_AUTOTEST`
  + `Integrated Security=true`；错误实例 / 错误库名 / 非集成安全的连接串一律在 `AssertDedicatedTarget` 阶段被拒绝。
  每次运行只创建一个**全新 GUID 后缀库**（`NEWERP_AUTOTEST_SOCREQAUTH_<guid>`），发现同名库已存在立即拒绝，
  **绝不 drop / reset / 复用**任何数据库，也绝不读取 `appsettings*.json` / `.env` / 生产凭据。
- **真实驱动 + 既有授权**：只在本隔离 GUID 库内播种既有「销售订单」功能菜单授权 + 业务员客户数据范围 +
  两位客户 / 三张来源订单 / 四张变更申请；以真实 `SalesOrderChangeRequestController` 驱动，不复制测试专用实现，
  不新增 / 不修改任何既有菜单 / 角色 / 用户授权，无匿名 / 管理员降级。
- **逐入口证明**：受限业务员下本人申请可读、范围外 / 来源已删除 / 来源缺失 / 不存在返回同一非披露错误；
  `by-source` 与 `source-options` 按范围收敛；登记 / 编辑 / 提交 / 取消只作用于本人来源，被拒侧同一非披露错误。
- **身份 / 菜单拒绝矩阵**：无身份 / 已删除账号 → 未认证；已禁用 / 无菜单 / 仅导出菜单 / 已撤销菜单 → 权限不足（逐入口断言）。
- **来源改派**：改派来源客户后受限账号立即 fail closed，特权账号保留既有不受限历史访问。
- **零写入证据**：每次拒绝 / 读取前后对 `SalesOrders` / `SalesOrderDetails` / `StockOuts` / `StockMovements`
  （**保留库存来源单据审计**） / `Quotations` / `FinanceReceipts` / `SalesOrderChangeRequests` /
  `SalesOrderChangeRequestDetails` / `SysOperationLogs` 做只读快照，全部不变；被拒的登记请求也**绝不推进**
  `SysDocumentNumberRules.CurrentSequence` 变更申请单号流水（授权先于发号）。
- **两个独立连接竞态**：① 两条独立连接并发读取同一本人申请 → 结果一致；② 一条合法本人读取与一条他人越权并发 →
  合法读取成功、越权 fail closed，收尾快照证明零写入。

### 5.3 构建与回归

- `.NET 8` Release 构建（`NEWERP.sln`）通过，0 警告 / 0 错误（`ERP.IntegrationTests` 启用了 `TreatWarningsAsErrors`）。
- `ERP.UnitTests`（本机实测 `dotnet test src/ERP.UnitTests -c Release --no-build` = **6340/6340 通过、0 失败、0 跳过**）：
  既有 `SalesOrderChangeRequestTests`（32 例）与新增 `SalesOrderChangeRequestAuthorizationTests`（11 例）全部通过；
  既有销售订单 / 业务员数据范围 / 订单追溯回归保持不变。
- `ERP.IntegrationTests` 新增 `SalesOrderChangeRequestAuthorizationSqlServerTests` 在受控
  `(localdb)\NEWERP_AutoAcceptance` 上真实执行通过（11 例）。
- **构建完成不等于阶段验收**：真实 SQL 用例只在受控 `(localdb)\NEWERP_AutoAcceptance` 上真实执行通过才构成验收证据；
  浏览器验收按任务配置为 `browser_acceptance.required = false`：不运行真实 Edge / UI / 截图验收。

## 6. 明确边界（非目标）

- **不新增授权**：不新增菜单 / 角色 / 用户授权，不修改种子数据与既有权限模型，不提供匿名 / 管理员降级，
  不把导出菜单当模块权限，不做旧库 `Oid` → 规范 `Id` 推断。
- **不改变业务语义**：拟议金额仍由 `SalesOrderAmountRules` 唯一权威算法重算；来源快照、差异对照、来源变化提示与
  状态机保持不变；不审核、不套用、不自动转换、不硬删除，也不改写来源销售订单与任何下游记录。
- **不修改数据库结构**：不修改任何存储过程 / 迁移脚本 / `SchemaUpgrader` / 既有表结构，不新增表 / 列 / 索引。
- **不清理既有数据**：不删除 / 不清理任何既有业务行、库存来源审计或既有失败日志；不执行生产凭据或生产数据操作。
- 模块元数据（`metadata`）为**静态口径文案**（不含任何订单 / 客户数据），沿用既有同步只读契约，不纳入本次授权收敛。
- 浏览器验收按任务配置为 `browser_acceptance.required = false`：不运行真实 Edge / UI / 截图验收。

