# 供应商比价（`api/purchase/quotes`）与比价审批（`api/purchase/quote-decisions`）实时授权

> 任务：`ERP-416`「Authorize supplier comparison procurement sources and decisions」（阶段 3 核心业务流程完整性）。
> 关联：`ERP-371`（采购订单实时授权与数据范围，`docs/purchaseorder-authorization.md`）、
> `ERP-097`（业务员客户数据范围，`docs/业务员数据范围说明.md`）、
> `ERP-095`（供应商比价审批与供应商选择历史）、`ERP-020` / `ERP-027`（比价 → 采购订单带入预填 / 单行与批次转单）、
> `ERP-098` / `ERP-103` / `ERP-105`（报价历史 / 转化漏斗 / 价格差异只读派生）。

## 1. 背景与问题证据（修复前）

`PurchaseQuoteController` 继承通用 `BaseCrudController<PurchaseQuote>`，列表 / 全部 / 详情 / 新增 / 修改 / 删除 /
批量删除**直接调用通用 CRUD 服务**，带入预填 / 单行转单 / 批次计划 / 批次转单 / 报价历史 / 价格差异 / 转化漏斗
只有类级 `[Authorize]`；`PurchaseQuoteDecisionController`（批次审批状态 / 记录决定）同样只有类级 `[Authorize]`：

| 入口 | 修复前行为 |
| --- | --- |
| `GET`（列表）/ `GET all` | 返回**全部**比价行（含报价、供应商、客户归属、审批选中） |
| `GET {id}` | 按 Id 返回**任意**比价行详情 |
| `POST` / `PUT {id}` / `DELETE {id}` / `POST batch-delete` | 可对**任意**已存在比价行新增 / 改写 / 软删除 |
| `GET {id}/order-prefill`、`POST {id}/to-order` | 可对**任意**已批准比价行带入预填 / 生成采购订单（消耗单据号） |
| `GET batch-order-plan`、`POST batch-to-order` | 可对**任意**批次生成计划 / 合并转采购订单 |
| `GET price-history` / `{id}/price-history` / `order-price-variance` / `{id}/order-price-variance` / `conversion-funnel` | 返回**全部**比价行的派生证据 |
| `GET api/purchase/quote-decisions/batch`、`POST decide` | 可读**任意**批次审批状态（含全量决定历史）、对**任意**比价行追加审批决定 |

因此任何已登录账号只要猜到一条范围外比价行 Id（或一个批次号），就能读取其报价 / 审批 / 派生证据，
或直接对其审批、改写、软删除、甚至转出采购订单并消耗单据号。

**这是运营记录入口的授权缺口，不是「再发布一份报表」**：本任务只收敛入口授权，不新增报表、不改写任何业务口径。

## 2. 集中化的比价护栏（`PurchaseQuoteAuthorizationRules`）

新增 `src/ERP.Application/Services/PurchaseQuoteAuthorizationRules.cs`（**唯一权威口径**），
调用方（控制器）在读取 / 写入之前调用：

| 方法 | 作用 |
| --- | --- |
| `EnsureLiveIdentityAsync(db, userId)` | 实时身份：缺失 / 非法 / 账号不存在 / 已删除按未认证；已禁用按权限不足；随后复用 `SalespersonDataScopeService` |
| `EnsureAccessAuthorizedAsync(db, userId)` | 数据入口完整授权：实时身份 + 既有「供应商比价」（`purchase-quote`）菜单 + ERP-097 数据范围 |
| `EnsureDestinationAuthorizedAsync(db, userId)` | 比价 → 采购订单目的地授权：复用 `PurchaseOrderAuthorizationRules.EnsureMenuAuthorizedAsync`（实时身份 + 既有「采购订单」菜单 + 权威范围），缺菜单统一为 `DestinationMenuDeniedText` |
| `ApplyScope(scope, query)` | 按**持久化 `CustomerId`** 把范围下推到数据库（受限账号只剩范围内且非空归属行） |
| `EnsureQuoteAllowedAsync(db, scope, quoteId)` | 单条比价行归属复核（详情 / 审批 / 带入预填 / 单行转单 / 单行派生） |
| `EnsureQuotesAllowedAsync(db, scope, ids)` | 一批显式 Id 归属复核（批量删除），任一不合格即整批拒绝 |
| `EnsureProposedCustomerAllowed(scope, customerId)` | 新增 / 改派的**拟提交**归属客户复核（绝不采信 `CustomerName`） |
| `EnsureBatchAllowedAsync(db, scope, quoteNo, lineId, lineIds)` | 批次整批归属复核（批次计划 / 批次审批状态 / 批次转单） |
| `EnsureDestinationScopeAllowedAsync(db, destinationScope, draft)` | 生成草稿的权威目的地范围复核（显式归属客户 + 权威归属销售订单客户） |

口径（fail closed，绝不缓存，每次请求重新解析）：

1. **实时身份 + 既有菜单**：身份缺失 / 非法 / 账号不存在 / 已删除 → 未认证（`Err:Unauthorized`）；
   已禁用 / 缺「供应商比价」菜单 → 权限不足（`Err:Forbidden`）。**不新增任何菜单 / 角色 / 用户授权**，
   不把「采购订单」菜单当作比价模块权限，且比价 → 采购订单另须既有「采购订单」（`purchase-order`）菜单。
2. **唯一权威数据范围**：只复用 `SalespersonDataScopeService`（ERP-097）；归属严格按比价行**持久化
   `PurchaseQuote.CustomerId`** 判定 —— 绝不按 `CustomerName`、金额或 `RefOrderNo` 文本推断。
3. **null 归属只在显式不受限口径下可用**：`CustomerId` 为空时，受限账号一律 fail closed；
   特权账号（`AllowedCustomerIds == null`）保留既有不受限口径。
4. **批次整批判定**：批次计划 / 批次审批状态 / 批次转单要求该批次**全部未删除行**都在范围内，
   混入任何范围外 / 空归属行即**整批拒绝**，绝不返回隐藏行的计数 / 跳过原因 / 部分行，也绝不产生部分写入或单号消耗。
5. **RefOrderNo 文本不授予归属**：转换后的 `RefOrderNo` 可能是采购单号、转换前是关联销售订单号；
   归属只取持久化 `CustomerId`，权威转换证据仍由既有 `PurchaseQuoteConversion.FindGeneratedOrderAsync` 负责。
6. **非披露错误**：范围外 / 已删除 / 不存在的比价行（与批次）返回**同一**受控错误「比价记录不存在」
   （`Err:NotFound`），授权先于任何计数 / 分页 / 分组 / 发号 / 字段替换 / 追加决定 / 转单。

## 3. 逐入口接线（先授权、后读取 / 写入）

- **列表 / 全部 / 详情**：先 `EnsureAuthorizedAsync()` 解析范围，再通过 `ScopeFilter` 把范围下推到
  `GenericService`（`Count` / 分页 / 关键字之前）；详情按 Id 复核持久化归属，范围外 / 已删除 / 不存在返回同一非披露错误。
- **新增 / 修改**：先复核**拟提交**归属客户（`EnsureProposedCustomerAllowed`），修改另复核**已存**行归属；
  被拒请求**绝不写入、绝不改派、绝不改写既有行**。
- **删除 / 批量删除**：先复核持久化归属；批量混入任何范围外 / 不存在 Id 即**整批拒绝**（无部分删除）。
- **带入预填 / 单行转单**：先 `EnsureAuthorizedAsync` + `EnsureDestinationAuthorizedAsync`，再复核来源归属；
  真实转单另在**发号与落库之前**用 `EnsureDestinationScopeAllowedAsync` 复核生成草稿的权威目的地范围。
- **批次计划 / 批次转单**：先整批归属复核，再在发号 / 落库之前对计划草稿逐组做目的地范围复核。
- **报价历史 / 价格差异 / 转化漏斗**：先授权，再把范围作为**可选参数**下推到只读派生（先于计数 / 分页 / 分组 /
  订单链接解析）；保留既有 `QueryAsync(db, query)` / `ForQuoteAsync(db, id)` 重载（等价 `scope = null`，既有不受限口径）。
- **审批批次状态 / 记录决定**：先授权；批次状态要求整批行在范围内，记录决定先复核被审批比价行的持久化归属
  （请求中的 `DecidedBy` / `DecidedByName` 只作留痕，不是授权依据）。
- **进程内直调边界**：真实 HTTP 请求（`Request.Path` 已赋值）一律执行授权；仅「既无任何登录身份、又不在 HTTP
  请求管线内」的进程内调用免授权（不可能由外部请求到达，见 `RequiresLiveAuthorization()`，与 ERP-371 / ERP-413 / ERP-414
  同源口径）。**真实匿名请求因处于请求管线内一律 fail closed（未认证）**，绝不提供可被外部到达的测试专用降级。

## 4. 保留既有商业口径与边界（不改写、不新增）

- 授权只发生在读取 / 写入之前，**不改写**任何既有转换 / 审批算法：选中与审批守卫、重复生成守卫（`RefOrderNo`
  链接 + 备注来源标记）、审批参考号、金额由 `PurchaseOrderController.Calculate` / `Validate` 重算等全部保持不变。
- 被拒绝的请求**零写入**：不改写比价行 / 审批决定 / 采购订单与明细 / 库存与库存成本 / 财务记录，
  不删除历史证据与审计留痕，也不消耗供应商比价相关单据号。
- **不新增授权**：不新增菜单 / 角色 / 用户授权，不修改种子数据与既有权限模型，不提供匿名 / 管理员降级。
- **不修改数据库结构**：不修改任何存储过程 / 迁移脚本 / `SchemaUpgrader` / 既有表结构，不新增表 / 列 / 索引。
- **不清理既有数据**：不删除 / 不清理任何既有业务行、库存来源单据审计或既有失败日志。

## 5. 验证证据

### 5.1 单元测试（`src/ERP.UnitTests/PurchaseQuoteAuthorizationTests.cs`，内存库 + 真实既有身份 / 菜单 / 范围）

- **读取入口**：受限业务员台账 / 全部只返回本人客户行（`Total` 只计可访问行），范围外 / 空归属 / 已删除 / 不存在
  在详情返回同一非披露错误；空归属行的 `CustomerName` 刻意写成可见客户名，证明其不是授权依据。
- **写入口**：新增 / 改派按拟提交归属授权（范围外 / 空归属拒绝且零写入），删除 / 批量删除混入范围外即整批拒绝。
- **转单**：缺「采购订单」菜单时带入预填与真实转单都拒绝且不产生单据；补齐菜单后本人来源放行、范围外 / 空归属非披露拒绝；
  归属销售订单客户越界时按 `PurchaseOrderAuthorizationRules` 目的地范围拒绝且不发号、不写入。
- **批次**：混合批次（本人行 + 他人行）的计划 / 转单 / 显式行清单都整批非披露拒绝，零单据零决定；纯本人批次放行。
- **审批**：混合批次状态拒绝、纯本人批次放行；范围外 / 空归属 / 已删除比价行记录决定被拒且不追加任何决定。
- **派生只读**：报价历史 / 价格差异 / 转化漏斗只统计范围内行（隐藏行不进计数）。
- **身份 / 菜单拒绝矩阵**：无身份 / 已删除 → 未认证；已禁用 / 无菜单 / 仅「采购订单」菜单 → 权限不足，逐入口断言且零写入。
- **特权 / 进程内**：特权账号保留既有不受限口径（可见空归属与范围外行、可转单）；进程内无身份直调保持既有免授权口径。

### 5.2 真实隔离 SQL 测试（`src/ERP.IntegrationTests/PurchaseQuoteAuthorizationSqlServerTests.cs`）

- **专用目标护栏**：必须在**任何数据库访问之前**精确命中 `(localdb)\NEWERP_AutoAcceptance` + 库名前缀 `NEWERP_AUTOTEST`
  + `Integrated Security=true`；错误实例 / 错误库名 / 非集成安全的连接串一律在 `AssertDedicatedTarget` 阶段被拒绝。
  每次运行只创建一个**全新 GUID 后缀库**（`NEWERP_AUTOTEST_PQSOURCEAUTH_<guid>`），发现同名库已存在立即拒绝，
  **绝不 drop / reset / 复用**任何数据库，也绝不读取 `appsettings*.json` / `.env` / 生产凭据。
- **真实驱动 + 既有授权**：只在本隔离 GUID 库内播种既有「供应商比价」/「采购订单」功能菜单授权 + 业务员客户数据范围 +
  两张客户 / 本人·他人·空归属比价行 / 混合批次；以真实控制器驱动，不复制测试专用实现，不新增 / 不修改任何既有权限模型。
- **两个独立连接竞态**：① 两条独立连接 / DbContext 并发读取同一本人比价行 → 结果一致；
  ② 一条合法本人读取与一条他人越权并发 → 合法读取成功、越权 fail closed，收尾快照证明零写入。
- **零写入证据**：每次拒绝 / 读取前后对 `PurchaseQuotes` / `PurchaseQuoteDecisions` / `PurchaseOrders` /
  `PurchaseOrderDetails` / `StockMovements`（**保留库存来源单据审计**） / `SysOperationLogs` 做只读快照，全部不变。
- **构建完成不等于阶段验收**：只有本文件在受控 `(localdb)\NEWERP_AutoAcceptance` 上真实执行通过才构成验收证据；
  浏览器验收按任务配置为 `browser_acceptance.required = false`。

## 6. 明确边界（非目标）

- **不新增授权**：不新增菜单 / 角色 / 用户授权，不修改种子数据与既有权限模型，不提供匿名 / 管理员降级。
- **不改变业务语义**：比价转换与审批守卫、只读派生口径（`null` 视为未知）与响应 DTO 契约保持不变。
- **不修改数据库结构**：不修改任何存储过程 / 迁移脚本 / `SchemaUpgrader` / 既有表结构，不新增表 / 列 / 索引。
- **不清理既有数据**：不删除 / 不清理任何既有业务行、库存来源单据审计或既有失败日志；不执行生产凭据或生产数据操作。
- 浏览器验收按任务配置为 `browser_acceptance.required = false`：不运行真实 Edge / UI / 截图验收。
