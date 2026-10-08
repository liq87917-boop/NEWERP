# 客户销项发票串行化与收款分摊容量（ERP-383）

## 1. 目的

补上 ERP-055（客户销项发票证据）/ ERP-073（收款单 → 销项发票分摊）/ ERP-075 之后仍存在的运营完整性缺口：

- `CustomerSalesInvoiceEvidenceController` 只在台账 / 详情下推数据范围；`Create / Update / Record / Void /
  order-candidates / allocations/preview / allocations / commercial-invoice-candidates` 都没有实时授权，
  越权调用方可读到 / 改写范围外客户、范围外来源订单与范围外单证的发票；
- `CustomerSalesInvoiceEvidenceService.UpdateAsync / SaveAllocationsAsync / RecordAsync / VoidAsync` 仍是
  「先读、再判定、后落库」的**无锁 check-save** 写法：并发「草稿修改 vs 登记」「分摊整体替换 vs 登记」
  「作废 vs 收款分摊登记」可能互相覆盖或留下半成品分摊行；
- `CustomerSalesInvoiceCollectionAllocationService.CreateAsync / VoidAsync`（收款单 → 发票分摊）既没有事务也没有行锁：
  两张不同收款单可各自通过校验后同时落库，使同一发票的有效收款分摊**合计超过发票含税总额**；
- 发票「分摊到销售订单」当时只校验本发票 ≤ 含税总额，**没有任何订单侧容量口径**：多张发票可各自占用同一销售订单而不被发现。

ERP-383 在不新增表 / 列 / 菜单 / 权限模型、**不新增任何用户授权**的前提下，把客户销项发票的
**修改 / 分摊整体替换 / 登记 / 作废**与「收款单 → 发票」收款分摊证据的**登记 / 作废**统一到
**同一条确定性锁序与原子事务**上；并补齐**订单发票容量**与**发票收款容量**两个竞争口径，
同时把**每一路由**的实时授权（身份 / 既有销售订单菜单 / 权威客户数据范围）补齐。

## 2. 关键不变量

1. **实时启用身份 + 既有菜单授权 + 权威来源范围**：台账 / 详情 / 收款时效证据 / 商业发票候选 / 新增 / 修改 /
   可分摊订单候选 / 分摊预览 / 分摊整体替换 / 登记 / 作废**每一路由**都重新校验当前身份（存在、未删除、启用）、
   既有「销售订单」（`sales-order`）菜单授权与 `SalespersonDataScopeService`（ERP-097 唯一权威口径）客户数据范围；
   停用账号 / 撤销授权 / 越界一律 fail closed，**绝不退化为匿名或管理员**。
2. **发票归属覆盖全部权威客户来源**：已存储客户（`CustomerSalesInvoiceEvidence.CustomerId`）、
   **每一张**已存储分摊来源销售订单的客户（`CustomerSalesInvoiceAllocations` → `SalesOrders.CustomerId`）、
   与**显式交叉引用单证的归属客户**（`TradeDocument.CustomerId`，`SalesOrderNo` 是自由文本，刻意**不**作为权威来源）；
   混源（多客户来源）要求**每一个**相关客户都允许，任一越界即拒绝。
3. **不可证明来源 fail closed、历史证据显式保留**：受限账号不得读取 / 改写「来源销售订单已删除 / 不存在（无法证明归属）」
   的发票；不可证明归属的历史单证（未登记客户 / 已删除）不构成额外约束，发票自身客户仍须在范围内；
   特权（真正不受限）账号保留历史发票的可读 / 可操作能力。
4. **台账与候选范围下推**：台账在**计数 / 分页之前**把范围谓词下推数据库；商业发票候选只返回
   **归属客户已登记且在范围内的商业发票**，受限账号看不到无归属 / 范围外单证，绝不按单号文本或相似度猜测来源。
5. **发票同币种收款容量**：同一发票的**全部有效收款分摊行合计**不得超过发票含税总额；作废行保留历史但不占用容量；
   并发不同收款单在发票行锁下串行化，合计绝不突破容量。
6. **订单发票容量**：同一销售订单的**全部未作废发票分摊行合计**不得超过订单总额（同币种、不做汇率换算、不重复计算）；
   分摊整体替换与登记都在订单行锁内复核，并发多张发票绝不突破订单容量。
7. **ERP-350 共用收款额度 / ERP-073 证据维度分离保持不变**：收款单金额仍是收款单的唯一、同币种分摊额度，由
   「收款单 → 销售订单」与「收款单 → 代理服务费对账单」两套有效证据在收款单行锁下共同占用；本任务只让
   「收款单 → 发票」维度在同一发票行锁下独立受容量保护，绝不跨币种合计、绝不重复计算证据、绝不改写既有行。
8. **作废保留原因与冻结审计**：作废必须填原因，保留身份 / 金额 / 分摊行 / 登记时间与审计，不物理删除、不改写已登记证据；
   重复作废在锁内被状态门拒绝。作废只让**发票证据**失效，绝不改写任何分摊行原始值。
9. **被拒绝者零副作用**：越权 / 冲突的编辑、分摊替换、登记、作废、收款分摊在写入之前整体拒绝，绝不改写任何
   发票 / 分摊行 / 销售订单 / 单证 / 收款单。

## 3. 原子性与并发（唯一全局锁序）

**唯一全局锁序（跨模块统一，绝不反向获取）**：

```
1) 来源销售订单行（SalesOrders，Id 升序；与 ERP-347 销售订单取消 / ERP-343 出库审核共用同一把行锁）
2) 客户销项发票行（CustomerSalesInvoiceEvidences）
3) 客户收款单行（FinanceReceipts；与 ERP-349 / ERP-378 收款单生命周期、ERP-053 / ERP-071 分摊写入共用）
4) 引用 / 分摊 / 证据行（CustomerSalesInvoiceAllocations / CustomerSalesInvoiceCollectionAllocations /
   CustomerReceiptAllocations，由持有上述行锁的事务顺带写入）
```

- **锁实现**：`CustomerSalesInvoiceConcurrencyRules.LockSalesOrderRowsAsync` / `LockInvoiceRowAsync` /
  `LockReceiptRowAsync` / `LockInvoiceAndReceiptRowsAsync` —— 在调用方事务内对目标行发一条「审计时间戳刷新」的
  `UPDATE`（仅用 EF Core 基础 API，不依赖关系型扩展），取得排它行锁（X 锁，持有至事务结束），
  语义等价于 `SELECT ... WITH (UPDLOCK, HOLDLOCK)`；`UpdatedAt` 是技术审计字段、不是商业证据，不构成静默改写。
- **行锁语义 = 阻塞后成功**：`RowVersion` 乐观并发令牌在并发方先提交后会过期，此时重读权威行（含新令牌）后
  **有界重试**（`LockRetryAttempts = 8`），绝不把纯粹的锁等待误报成业务拒绝；真实业务冲突仍由锁内校验判定。
- **锁调用者审计（全部遵守同一锁序，无反向加锁）**：
  - `CustomerSalesInvoiceEvidenceService.UpdateAsync`：发票行锁 → 锁内重读状态 / 客户 / 币种 / 分摊口径；
  - `CustomerSalesInvoiceEvidenceService.SaveAllocationsAsync`：订单行锁（已存储 ∪ 拟提议，Id 升序）→ 发票行锁 →
    计划校验（含订单容量）→ 整体替换；
  - `CustomerSalesInvoiceEvidenceService.RecordAsync`：订单行锁（已存储来源）→ 发票行锁 → 重读状态 + 分摊复核（含订单容量）；
  - `CustomerSalesInvoiceEvidenceService.VoidAsync`：发票行锁 → 重读状态 → 写状态 / 原因 / 审计；
  - `CustomerSalesInvoiceCollectionAllocationService.CreateAsync`：发票行锁 → 收款单行锁 → 锁内读权威发票 / 收款单 →
    容量校验 → 落行；
  - `CustomerSalesInvoiceCollectionAllocationService.VoidAsync`：发票行锁 → 收款单行锁 → 重读分摊行 → 作废；
  - `CustomerReceiptAllocationService`（ERP-053）/ `AgencyServiceFeeCollectionAllocationService`（ERP-071）：
    只取**收款单行锁**（第 3 段），不获取上游订单 / 发票行锁 → 与客户销项发票模块不构成反向加锁；
  - `CustomerReceiptLifecycleRules`（ERP-378）：收款单生命周期只取收款单行锁，同样无反向加锁；
  - `SalesOrderController.Cancel`（ERP-347）：可串行化事务内先取来源销售订单行锁（第 1 段）。
- **竞态结果**：并发不同收款单分摊同一发票、并发多张发票分摊同一订单**只允许合计不超容量的组合成功**
  （超容量方被业务规则拒绝，且不留下任何行）；「收款分摊登记 vs 发票作废」「草稿修改 vs 登记」「分摊整体替换 vs 登记」
  都给出唯一一致的串行化结果或原子拒绝，绝不出现半成品行、陈旧写入或超容量证据。
- 内存库等非关系型提供程序无行锁语义：`IsRelationalProvider` 为 false 时跳过事务与锁定（等价无事务），
  声明式校验与写入语义不变。

## 4. 接口

沿用既有路由，**不新增路由、不改变请求 / 响应结构**：

- `GET /api/customer-sales-invoices`（台账：身份 / 菜单 / 权威来源范围谓词在**计数与分页之前**下推数据库）
- `GET /api/customer-sales-invoices/collection-timing`（收款时效证据：身份 / 菜单 + 客户范围过滤）
- `GET /api/customer-sales-invoices/commercial-invoice-candidates`（候选：按既有单证权威归属客户范围下推）
- `GET /api/customer-sales-invoices/{id}`（详情，越界 / 来源不可证明 fail closed）
- `GET /api/customer-sales-invoices/{id}/order-candidates`（可分摊订单候选）
- `POST /api/customer-sales-invoices`（新增草稿：拟提议客户 / 单证范围校验在写入之前）
- `PUT /api/customer-sales-invoices/{id}`（修改草稿：已存储 + 拟提议范围双重校验）
- `POST /api/customer-sales-invoices/{id}/allocations/preview`（分摊预览：已存储 + 拟提议来源范围校验）
- `POST /api/customer-sales-invoices/{id}/allocations`（分摊整体替换：订单行锁 → 发票行锁）
- `POST /api/customer-sales-invoices/{id}/record`（登记：订单行锁 → 发票行锁 → 订单容量复核）
- `POST /api/customer-sales-invoices/{id}/void`（作废：发票行锁 + 原因 / 审计保留）
- `POST /api/customer-sales-invoice-collection-allocations`（收款单 → 发票分摊登记：发票行锁 → 收款单行锁）
- `POST /api/customer-sales-invoice-collection-allocations/{id}/void`（分摊作废：发票行锁 → 收款单行锁）

## 5. 实现清单

- `src/ERP.Application/Services/CustomerSalesInvoiceAuthorizationRules.cs`（新增：实时授权 / 数据范围 / 范围下推）
- `src/ERP.Application/Services/CustomerSalesInvoiceConcurrencyRules.cs`（新增：唯一全局锁序与行锁实现）
- `src/ERP.Api/Controllers/CustomerSalesInvoiceEvidenceController.cs`（每一路由的实时授权接入）
- `src/ERP.Application/Services/CustomerSalesInvoiceEvidenceService.cs`（发票行锁 / 订单行锁 + 订单发票容量 + 事务）
- `src/ERP.Application/Services/CustomerSalesInvoiceCollectionAllocationService.cs`（发票行锁 → 收款单行锁 + 登记 / 作废原子化）
- `src/ERP.Application/Services/CustomerReceiptLifecycleRules.cs`（锁序文案与收款单侧审计同源）
- `src/ERP.Application/Services/CustomerReceiptAllocationService.cs` / `AgencyServiceFeeCollectionAllocationService.cs`（锁序审计声明）
- `src/ERP.Application/Services/SalesOrderCancellationRules.cs`（锁序首段文案同源）
- `src/ERP.UnitTests/CustomerSalesInvoiceConcurrencyTests.cs`（内存库：授权、容量、原子拒绝、接线契约）
- `src/ERP.IntegrationTests/CustomerSalesInvoiceConcurrencySqlServerTests.cs`（真实 SQL：真实控制器 + 双连接竞态 + 专用目标护栏）
- `docs/customer-sales-invoice-concurrency.md`（本文档）

## 6. 测试与验证

- 单元测试（内存库 `TestDbFactory`，无 SQL）：
  `dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --filter FullyQualifiedName~CustomerSalesInvoiceConcurrencyTests`
  覆盖：缺失身份 / 禁用账号 / 无 `sales-order` 菜单 / 角色无任何菜单授权（系统已部署该菜单）/ 撤销菜单在**全部路由** fail closed
  且零副作用；受限业务员只读 / 只写「已存储客户 + 全部来源订单 + 交叉引用单证」都在范围内的发票（越界、混源、
  来源订单已删除、单证归属越界一律拒绝且不改写）；特权账号保留历史发票可读；拟提议客户 / 单证 / 订单越界在**写入之前**拒绝；
  商业发票候选按单证权威归属客户范围下推；发票收款容量与订单发票容量；作废保留原因与冻结审计、重复作废拒绝、
  作废后不能再登记收款分摊；ERP-073 证据维度分离与 ERP-350 共用额度保持不变；锁 / 授权接线源码契约与锁序文档同源。
- SQL Server 集成测试（受控 localdb，真实控制器 + 两个独立连接）：
  `dotnet test src/ERP.IntegrationTests/ERP.IntegrationTests.csproj -c Release --filter FullyQualifiedName~CustomerSalesInvoiceConcurrency`
  覆盖**两个独立连接竞态**：（1）两张不同收款单并发分摊同一发票 → 同币种收款容量只允许一方成功；
  （2）两张不同草稿发票并发分摊同一销售订单 → 订单发票容量只允许一方成功；
  （3）收款分摊登记 vs 发票作废；（4）草稿修改 vs 登记；（5）分摊整体替换 vs 登记；
  以及真实身份授权（本人客户可读、范围外 / 混源 / 交叉引用单证归属越界 fail closed、无身份 / 禁用账号 fail closed、
  特权账号保留历史可读、撤销菜单立即收敛且零改写）。
- 安全验证档（本任务交付门）：`dotnet restore` + Release 构建（`TreatWarningsAsErrors` / 分析器开启）+ `ERP.UnitTests` 全量。
- 本次实测证据（受控 localdb 真实执行）：
  - Release 构建 `0 警告 / 0 错误`（ERP.Api / ERP.UnitTests / ERP.IntegrationTests 全部重建通过）；
  - `ERP.UnitTests` 全量 **5751 通过 / 0 失败**；本任务新增单元筛选 **19 通过 / 0 失败**；
  - 真实 SQL 集成筛选 **10 通过 / 0 失败**（5 个双连接竞态 + 1 个真实身份授权 + 4 个目标护栏 fail-closed），
    连续 **3 次独立运行**（各自全新 GUID `NEWERP_AUTOTEST` 库）结果一致；
  - 测试产物（TRX / 日志）一律写到仓库**之外**的临时目录；仓库内不留下本任务的测试产物。
- **构建完成不等于阶段验收**：真实 SQL 场景必须在专用 localdb 上真实执行通过才算验收证据；本任务
  `browser_acceptance.required = false`，不运行浏览器 / UI 验收，也未运行任何部署或生产库操作。

## 7. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST`、
  `Integrated Security`；错误目标在访问数据库**之前** fail closed（`AssertDedicatedTarget`，含 fail-closed 单元覆盖）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝（`SELECT DB_ID`）；绝不 drop / reset /
  复用任何数据库，也不做任何破坏性数据操作。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings` /
  `.env` / 生产凭据。
- 测试只**新增**自己的证据行（唯一前缀 `INT_CSI383_`），不清理、不删除、不重写任何既有行与原始失败日志。
- 夹具初始化 = `EnsureCreated` + `SchemaUpgrader` + `SeedData` + `SchemaUpgrader`；真实身份复用既有
  `sales-order` 菜单与既有销售员客户范围口径，**不新增任何用户 / 角色 / 菜单 / 权限授权**。

## 8. 边界与保留项

- 本护栏只保护客户销项发票证据与收款分摊容量：不改写发票身份 / 金额 / 状态 / 分摊证据 / 作废原因与审计，
  不改写销售订单状态 / 出货进度 / 金额与明细、客户信用状态、收款单与既有分摊行、单证中心单证、库存与库存成本、
  库存流水、佣金 / 回佣、费用与退税记录；**不开发票、不连税务局、不做税务申报 / 销项税金计算、不收款、不记账、
  不生成凭证 / 结算单、不做汇率换算、不跨币种合并、不重复计算证据、绝不物理删除或静默改写历史证据**。
- **权限模型不变**：只复用既有 `sales-order` 菜单与既有数据范围口径（含既有系统内置角色特权），不新增用户 / 角色 /
  菜单 / 权限，也不引入匿名或管理员回退。
- **保留项**：库存来源单据审计（ERP-375 / ERP-376 / ERP-377 的来源选择与来源审计链路）与原始失败日志
  （`run.log` / `erp380-fullunit.log` / `TestResults` / `.ai/logs`）一律保持原样，不删除、不重写；本改动不触碰任何库存来源工作流。
- **依赖与兼容**：ERP-055（发票证据）/ ERP-073（收款 → 发票分摊）/ ERP-075（收款分摊暴露）/ ERP-347（销售订单取消）/
  ERP-349 / ERP-378（收款单生命周期）/ ERP-350（唯一收款额度）/ ERP-071（代理服务费分摊）的既有行为、路由、
  审计与证据语义全部保留；`docs/客户销项发票登记说明.md`、`docs/customer-receipt-lifecycle.md`、
  `docs/receipt-lifecycle-concurrency.md` 仍然有效，本文档只补充 ERP-383 的锁序、容量口径与双连接竞态。
- **菜单口径说明**：账号已配置任何菜单授权时必须显式包含 `sales-order`（撤销后下一次请求立即收敛）；
  未配置任何菜单授权且账号已分配角色、且系统已部署该菜单时同样 fail closed；只有既有特权账号与
  未分配角色、仅按员工编码映射的历史业务员账号沿用既有 ERP-097 权威数据范围（无新增授权、无管理员兜底）。
- `RequiresLiveAuthorization` 只对「既无任何登录身份、又不在 HTTP 请求管线内」的进程内调用免授权（历史单元测试 /
  内部派生读取）：这类调用不可能由外部请求到达，且绝不把缺失身份当作管理员；真实匿名 HTTP 请求一律 fail closed。
