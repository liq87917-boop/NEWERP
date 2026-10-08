# 采购订单实时授权与数据范围护栏（ERP-371）

## 1. 目标

采购订单（`purchase-order`）的全部核心路由（列表 / 详情 / 导出 / 打印 / 创建 / 修改 / 提交 / 审核 / 取消 / 删除，
以及归属 / 进度 / 财务核对 / 发票与付款证据等只读派生视图）都必须对**当前登录身份**做**实时**授权与数据范围复核：

- 缺失 / 已删除身份、禁用账号、无既有「采购订单」授权（含被撤销最后一个菜单）一律 fail closed；
- 列表在计数 / 分页之前把权威客户范围下推到数据库，绝不「先查全量再内存过滤」；
- 范围同时覆盖**显式归属客户**与**权威归属销售订单客户**，两者只要有一方在范围外即拒绝；
- **受限账号不得访问无归属备货采购**（既无显式归属客户、也未链接已审核销售订单），
  而**特权账号的合规备货采购（procurement-for-stock）保持可用**；
- 创建 / 修改同时校验「已存」与「请求」两侧归属，先校验再分配字段 / 替换明细 / 消耗单据号，
  被拒绝方绝不改写任何单据 / 明细 / 状态（denied edits preserve totals/details/audit）；
- 提交 / 审核 / 删除 / 取消 / 改单共用「**归属销售订单 → 采购订单**」行锁（`UPDLOCK, HOLDLOCK`）与可串行化事务，
  **审核在锁内复核来源销售订单仍为权威可用**（存在、未删除、已审核、未取消），消除「审核 vs 来源失效」竞争；
- 保留 ERP-346 权威快照、ERP-345 取消护栏与全部审计证据；不改变供应商 / 币种 / 单位 / 单价等原始商业语义。

## 2. 口径

| 维度 | 判定 | 结果 |
|---|---|---|
| 身份 | `ClaimTypes.NameIdentifier` 缺失 / 非正整数 | `2000` 未认证（拒绝） |
| 账号 | 账号不存在或已 `IsDeleted` | `2000` 未认证（拒绝） |
| 账号状态 | `Status != Enabled` | `2002` 权限不足（拒绝） |
| 模块授权 | 「角色 → 菜单」不含既有 `purchase-order`（含被撤销最后一个菜单） | `2002` 权限不足（拒绝） |
| 数据范围 | 受限账号的显式归属客户或权威归属销售订单客户不在可见客户内 | `2002` 权限不足（拒绝，不泄露范围外单据） |
| 数据范围 | 受限账号访问**无归属备货采购**（无显式归属、也未链接已审核销售订单） | `2002` 权限不足（拒绝） |
| 特权备货采购 | 特权账号（系统内置 / 超级管理员角色，且持有既有菜单）的无归属备货采购 | 放行（既有行为不变） |
| 列表 / 导出 | 计数 / 分页之前把客户范围下推到 SQL | 只统计 / 只返回范围内单据 |
| 创建 / 修改 | 已存与请求两侧归属、显式链接来源（ERP-346）全部通过后才落库 | 任一步失败整体回滚，原单据 / 明细 / 总额不变，单号不消耗 |
| 提交 / 审核 / 删除 / 取消 / 改单 | 「归属销售订单 → 采购订单」行锁 + 可串行化事务，锁内复核实时授权 | 并发下只出现一种一致结果 |
| 审核 | 锁内复核来源销售订单仍为权威可用 | 来源已取消 / 未审核 / 已删除时拒绝审核 |
| 主数据 / 商业语义 | 供应商 / 币种 / 单位 / 单价与 Excel 导出列不变 | 值均不变 |

**菜单授权口径**：本模块沿用既有 ERP-346 的**严格菜单要求**——账号必须持有既有 `purchase-order` 菜单授权，
否则一律拒绝；不新增任何菜单或用户授权，也绝不把空身份 / 空范围当作管理员。


## 3. 实现

- `src/ERP.Application/Services/PurchaseOrderAuthorizationRules.cs`（新增）：纯判定 + 有界只读查询。
  - `EnsureMenuAuthorizedAsync`：身份（缺失 / 已删除 / 禁用）/ 既有 `purchase-order` 菜单 fail closed，返回权威数据范围；
  - `EnsureOrderAuthorizedAsync` / `EnsureOrdersAuthorizedAsync`：单据级（含批量）权威归属客户范围；
  - `EnsureProposedAuthorizedAsync`：请求侧（创建 / 修改提交的归属）范围，先于字段分配 / 明细替换 / 单号消耗；
  - `ApplyScopeAsync`：列表 / 导出客户范围下推（计数 / 分页之前）；
  - `ResolveOwningCustomerIdsAsync` / `ResolveSalesOrderCustomerIdAsync`：显式归属客户 + 按 Id 精确解析的权威来源客户（不按单号 / 名称 / 金额猜测）。
- `src/ERP.Api/Controllers/PurchaseOrderController.cs`：
  - `GetPaged` / `Export` / `ExportExcel` → `ApplyProcurementScopeAsync`（计数 / 分页之前）；
  - `GetById` / `GetPrint` / `Timeline` / `Progress` / `FinanceReconciliation` / `ReturnImpact` /
    `InvoiceEvidence` / `PaymentEvidence` / `InvoicePaymentEvidence` 与三组批量证据汇总 → 单据级范围复核；
  - 供应商敞口 / 交期异常 / 首收交期报表 → 身份 + 菜单复核（跨单聚合的范围下推仍由既有报表口径负责，本次不改写其服务）；
  - 采购订单控制器没有独立的 `options` 端点（供应商 / 客户 / 币种等下拉选项由既有基础资料接口提供），
    因此「options」在采购订单侧不构成独立受保护路由，未新增或改动该路由；
  - `Create` / `CreateUnlinkedAsync`、`Update` / `UpdateUnlinkedAsync` → 先复核「已存 + 请求」归属再落库；
  - `Submit` / `Approve` / `Delete` 覆写：行锁 + 可串行化事务 + 锁内复核授权（`Approve` 另复核来源仍为权威可用）；
  - `Cancel` 沿用 ERP-345 判定，改经 `AcquireOrderStateLocksAsync`（来源行锁 → 采购订单行锁，锁序与创建 / 更新一致）；
  - 授权入口统一经 `RequiresLiveAuthorization()`：**真实 HTTP 请求**（MVC 绑定，`Request.Path` 已赋值）一律执行；
    仅「既无任何登录身份、又不在 HTTP 请求管线内」的**进程内直接调用**（历史单元测试 / 内部派生读取）免授权——
    这类调用不可能由外部请求到达，真实匿名请求因处于请求管线内一律 fail closed，绝不把缺失身份当作管理员。
- `src/ERP.Application/Services/PurchaseSalesOrderLinkRules.cs`：身份 / 账号状态 / 菜单校验收敛到
  `PurchaseOrderAuthorizationRules.EnsureMenuAuthorizedAsync`；新增 `EnsureSourceLinkStillValidAsync` 供审核锁内复核；
  ERP-346 权威快照与商品 / 单位兼容性校验原样保留。
- `src/ERP.Application/Services/PurchaseOrderCancellationRules.cs`：身份 / 菜单 / 范围校验收敛到
  `PurchaseOrderAuthorizationRules.EnsureOrderAuthorizedAsync`（范围覆盖权威来源客户），ERP-345 履约 / 付款证据护栏不变。
- 未新增表 / 列 / 菜单 / 权限；未改动 `SchemaUpgrader` / `SeedData`。

## 4. 测试

- `src/ERP.UnitTests/PurchaseOrderAuthorizationTests.cs`（新增，内存库 + 真实 HTTP 身份，15 例）：
  缺失身份 / 禁用账号 / 无菜单 / 撤销菜单在十三条核心路由上 fail closed 且零副作用；
  受限业务员列表 / 详情 / 导出 / 打印只读自有客户，越界与无归属拒绝；受限账号不得创建 / 修改无归属备货；
  特权账号备货采购保持可用；显式链接按权威来源客户范围收敛；
  修改失败（来源失效 / 越界）不动总额 / 明细 / 状态；审核在来源失效后被拒且状态保持已提交；
  控制器形状（可串行化事务 + 行锁 + 来源复核 + 范围下推）。
- `src/ERP.UnitTests/PurchaseSalesOrderLinkTests.cs`、`OrderTraceabilityTests.cs`、`PurchaseOrderCancellationTests.cs`、
  `PurchaseOrderControllerTests.cs`、`PurchaseOrderProgressTests.cs`、`SupplierPurchaseExposureTests.cs` 等既有回归
  **不改动并保持通过**（既有特权 / 合法授权身份照常放行，历史直接实例化调用按上述进程内调用口径免 HTTP 授权）。
- `src/ERP.IntegrationTests/PurchaseOrderAuthorizationSqlServerTests.cs`（新增，真实 SQL Server 专用 localdb）：
  专用目标护栏、无身份 / 禁用 / 无菜单拒绝且单据不变、种子管理员（既有全部菜单）合法放行与导出、
  受限操作员自有 / 他人 / 无归属访问与导出收敛、撤销菜单立即收敛、失败改单原子性，
  以及**两条独立连接**的竞争用例：并发「提交 vs 删除」只成功其一，并发「来源取消（持锁）vs 采购审核」拒绝审核。

## 5. 安全（真实 SQL 夹具口径）

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST`、`Integrated Security`；
  错误目标在访问数据库**之前** fail closed（`AssertDedicatedTarget` / `Guard`）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；**绝不 drop / reset / 复用任何数据库**。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings` / `.env` /
  生产凭据，也绝不执行生产库；**构建通过不等于阶段验收通过**。
- 全部使用既有种子身份与既有菜单授权（`SeedData` 为超级管理员授予全部菜单），不新增任何用户授权；
  `.ai/logs/` 下原始失败日志原样保留；不做生产操作，不做财务 / 库存过账。

## 6. 边界

本护栏只保护采购订单生命周期：不改写供应商 / 币种 / 单位 / 单价等原始商业语义，不改写归属销售订单、客户、商品主数据，
不删除历史单据与审计证据；显式销售链接的权威快照仍由 ERP-346 负责，取消护栏（入库履约 / 付款 / 发票证据）仍为 ERP-345；
入库、付款、库存与财务口径不变。

## 7. 未执行 / 限制

- 本机验证档为 `safe`：Release 编译（0 警告 / 0 错误）+ `ERP.UnitTests` 全量通过。
- `src/ERP.IntegrationTests/PurchaseOrderAuthorizationSqlServerTests.cs` 属真实 SQL 集成档，**本次未在本机执行**
  （需专用 localdb 环境与批准）；**构建通过不等于阶段验收通过**。
- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置。
- 未新增 / 未修改任何数据库 schema、表 / 列 / 菜单 / 权限；原始失败日志与历史审计证据原样保留。
