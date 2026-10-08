# 供应商采购发票证据串行化与付款分摊容量（ERP-382）

## 1. 目的

补上 ERP-065（发票证据）/ ERP-351、ERP-380（付款证据与来源申请单）之后仍存在的运营完整性缺口：

- `PurchaseInvoiceService.UpdateAsync / SaveAllocationsAsync / RecordAsync / VoidAsync` 当时仍是「先读、再判定、后落库」的
  **无锁 check-save** 写法：并发「草稿修改 vs 登记」「关联整体替换 vs 登记」「作废 vs 付款引用登记」都可能互相覆盖或留下半成品关联；
- `SupplierPaymentInvoiceAllocationService.CreateAsync` 只对**付款单行**加锁、**没有对发票容量行加锁**：两张不同付款单可各自通过
  校验后同时落库，使同一发票的有效付款引用**合计超过发票含税总额**（不同付款单超额占用同一发票容量）；
- 发票作废与付款引用登记之间没有共同锁，作废与引用登记可能交错。
- 发票「关联到采购订单」当时只校验本发票 ≤ 含税总额，**没有任何订单侧容量口径**：多张发票可各自占用同一采购订单而不被发现。

ERP-382 在不新增表 / 列 / 菜单 / 权限模型、**不新增任何用户授权**的前提下，把供应商采购发票的
**修改 / 关联整体替换 / 登记 / 作废**与「付款单 → 发票」付款引用证据的**登记 / 作废**统一到
**同一条确定性锁序与原子事务**上；并补齐**订单发票容量**与**发票付款容量**两个竞争口径。

## 2. 关键不变量

1. **实时启用身份 + 既有菜单授权 + 权威来源范围**：台账 / 详情 / 可关联订单候选 / 关联预览 / 新增 / 修改 /
   关联整体替换 / 登记 / 作废**每一路由**都重新校验当前身份（存在、未删除、启用）、既有「采购订单」
   （`purchase-order`）菜单授权与 `SalespersonDataScopeService`（ERP-097 唯一权威口径）客户数据范围；
   停用账号 / 撤销授权 / 越界一律 fail closed，**绝不退化为匿名或管理员**。
2. **发票归属只认已持久化的「发票 → 采购订单」关联行**：关联的**每一张**采购订单的权威归属客户
   （显式 `OwningCustomerId` + 权威归属销售订单客户）都必须落在当前账号范围内，任一越界即拒绝；
   受限账号**不得读取 / 改写无来源发票**，而真正不受限（特权）的授权账号保留历史无来源发票的可读 / 可操作能力。
3. **关联替换两侧都校验范围**：保存关联前先复核「已存储来源」与「拟提议来源」的完整授权范围，再做整体替换——
   被拒绝的调用方绝不改写任何发票 / 关联行 / 采购订单。
4. **发票同币种付款容量**：同一发票的**全部有效付款引用行合计**不得超过发票含税总额；作废行保留历史但不占用容量；
   并发不同付款单在发票行锁下串行化，合计绝不突破容量。
5. **订单发票容量**：同一采购订单的**全部未作废发票关联行合计**不得超过订单总额（同币种、不做汇率换算、不重复计算）；
   保存关联与登记都在订单行锁内复核，并发多张发票绝不突破订单容量。
6. **ERP-351 共用付款额度 / ERP-380 来源申请单护栏保持不变**：「付款单 → 采购订单」与「付款单 → 采购发票」两套有效证据
   在付款单行锁 / 来源申请单行锁（先申请单、后付款单）下共同占用同一同币种额度，绝不跨币种合计、绝不重复计算。
7. **作废保留原因与冻结审计**：作废必须填原因，保留身份 / 金额 / 关联行 / 登记时间与审计，不物理删除、不改写已登记证据；
   重复作废在锁内被状态门拒绝。作废只让**发票证据失效**，绝不改写任何付款引用行原始值。

## 3. 原子性与并发（唯一全局锁序）

**唯一全局锁序（跨模块统一，绝不反向获取）**：

```
1) 来源采购订单行（PurchaseOrders，Id 升序；与采购订单取消 / 状态流转共用同一行键的排它行锁）
2) 供应商采购发票行（PurchaseInvoices）
3) 来源货款申请单行（ERP-380：先来源申请单、后付款单）
4) 付款单行（FinancePayments）
5) 引用 / 证据行（发票关联行、付款引用行，由持有上述行锁的事务顺带写入）
```

- **锁实现**：`PurchaseInvoiceConcurrencyRules.LockPurchaseOrderRowsAsync` / `LockInvoiceRowAsync` —— 在调用方事务内
  对目标行发一条「审计时间戳刷新」的 `UPDATE`（仅用 EF Core 基础 API，不依赖关系型扩展），取得排它行锁（X 锁，持有至事务结束），
  语义等价于 `SELECT ... WITH (UPDLOCK, HOLDLOCK)`；`UpdatedAt` 是技术审计字段、不是商业证据，不构成静默改写。
- **行锁语义 = 阻塞后成功**：`RowVersion` 乐观并发令牌在并发方先提交后会过期，此时重读权威行（含新令牌）后**有界重试**
  （`LockRetryAttempts = 8`），绝不把纯粹的锁等待误报成业务拒绝；真实业务冲突仍由锁内校验判定。
- **锁调用者审计（全部遵守同一锁序）**：
  - `PurchaseInvoiceService.UpdateAsync`：发票行锁 → 锁内重读状态 / 供应商 / 币种 / 关联合计；
  - `PurchaseInvoiceService.SaveAllocationsAsync`：订单行锁（已存储 ∪ 拟提议，Id 升序）→ 发票行锁 → 计划校验 → 整体替换；
  - `PurchaseInvoiceService.RecordAsync`：订单行锁（已存储来源）→ 发票行锁 → 重读状态 + 关联复核（含订单容量）；
  - `PurchaseInvoiceService.VoidAsync`：发票行锁 → 重读状态 → 写状态 / 原因 / 审计；
  - `SupplierPaymentInvoiceAllocationService.CreateAsync`：发票行锁 → 来源申请单行锁 → 付款单行锁 → 锁内读权威发票 / 付款单 → 容量校验 → 落行；
  - `SupplierPaymentInvoiceAllocationService.VoidAsync`：发票行锁 → 申请单 / 付款单行锁 → 重读行 → 作废；
  - `SupplierPaymentAllocationService.CreateAsync`：订单行锁 → 申请单 / 付款单行锁；
  - `SupplierPaymentAllocationService.VoidAsync`：订单行锁 → 申请单 / 付款单行锁；
  - `PurchaseOrderController` 取消 / 状态流转（ERP-345 / ERP-342）：来源销售订单行 → 采购订单行（与本锁序首段同源）。
- **竞态结果**：并发不同付款单引用同一发票、并发多张发票引用同一订单**只允许合计不超容量的组合成功**（超容量方被业务规则拒绝，
  且不留下任何行）；「作废 vs 引用登记」「草稿修改 vs 登记」「关联替换 vs 登记」都给出唯一一致的串行化结果或原子拒绝，
  绝不出现半成品行、陈旧写入或超容量证据。
- 内存库等非关系型提供程序无行锁语义：`IsRelationalProvider` 为 false 时跳过事务与锁定（等价无事务），声明式校验与写入语义不变。

## 4. 接口

沿用既有路由，**不新增路由、不改变请求 / 响应结构**：

- `GET /api/purchase-invoices`（台账：身份 / 菜单 / 来源范围谓词在**计数与分页之前**下推数据库）
- `GET /api/purchase-invoices/{id}`（详情，越界 / 无来源 fail closed）
- `GET /api/purchase-invoices/{id}/order-candidates`（候选，先授权再返回）
- `POST /api/purchase-invoices/{id}/allocations/preview`（预览：发票范围 + 拟提议来源范围授权，不写库）
- `POST /api/purchase-invoices/{id}/allocations`（**ERP-382：已存储 + 拟提议两侧授权 → 订单行锁 → 发票行锁 → 订单 / 发票容量校验 → 整体替换**）
- `POST /api/purchase-invoices/{id}/record`（**ERP-382：订单行锁 → 发票行锁 → 重读状态 + 订单容量复核**）
- `POST /api/purchase-invoices/{id}/void`（**ERP-382：发票行锁 → 作废原因与冻结审计保留**）
- `PUT /api/purchase-invoices/{id}`（**ERP-382：发票行锁内重读状态 / 供应商 / 币种 / 关联合计**）
- `GET /api/purchase-invoices/reconciliation` / `reconciliation-aging` / `reconciliation-aging/invoices/{id}`
  （只读派生报表：同样先做身份 / 菜单授权，明细再复核来源范围）
- `POST /api/supplier-payment-invoice-allocations` 与 `POST /api/supplier-payment-invoice-allocations/{id}/void`、
  以及 `POST /api/supplier-payment-allocations` 与 `POST /api/supplier-payment-allocations/{id}/void`：
  按唯一全局锁序在**发票 / 订单行锁**下登记与作废（路由与报文结构不变）。

## 5. 关键文件

- `src/ERP.Application/Services/PurchaseInvoiceAuthorizationRules.cs`（身份 / 菜单 / 权威来源范围 + 台账范围谓词）
- `src/ERP.Application/Services/PurchaseInvoiceConcurrencyRules.cs`（唯一全局锁序 + 订单 / 发票行锁 + 申请单 / 付款单行锁组合）
- `src/ERP.Application/Services/PurchaseInvoiceService.cs`（四条写路由的原子事务 + 行锁 + 订单发票容量校验 + 锁内复核）
- `src/ERP.Api/Controllers/PurchaseInvoiceController.cs`（每一路由的实时授权接入）
- `src/ERP.Application/Services/SupplierPaymentInvoiceAllocationService.cs`（发票行锁 + 登记 / 作废原子化）
- `src/ERP.Application/Services/SupplierPaymentAllocationService.cs`（订单行锁 + 登记 / 作废原子化）
- `src/ERP.Application/Services/SupplierPaymentLifecycleRules.cs` / `PurchaseOrderCancellationRules.cs`（锁序文案同源）
- `src/ERP.UnitTests/PurchaseInvoiceConcurrencyTests.cs`（内存库：授权、容量、原子拒绝、接线契约）
- `src/ERP.IntegrationTests/PurchaseInvoiceConcurrencySqlServerTests.cs`（真实 SQL：真实控制器 + 双连接竞态 + 专用目标护栏）
- `docs/purchase-invoice-concurrency.md`（本文档）

## 6. 测试与验证

- 单元测试（内存库 `TestDbFactory`，无 SQL）：
  `dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build --filter FullyQualifiedName~PurchaseInvoiceConcurrencyTests`
  覆盖：缺失身份 / 禁用账号 / 撤销菜单 / 无 `purchase-order` 菜单在**全部路由** fail closed 且零副作用；受限业务员只读 / 只写
  来源属于本人客户的发票（范围外、无来源、混源一律拒绝且不改写）；特权账号保留历史无来源发票可读；拟提议来源越界在**替换之前**拒绝；
  作废保留原因与冻结审计、重复作废拒绝、作废后不能再登记付款引用；订单发票容量与发票付款容量；ERP-351 共用付款额度；
  以及锁 / 授权接线源码契约与锁序文档同源。
- SQL Server 集成测试（受控 localdb，真实控制器 + 两个独立连接）：
  `dotnet test src/ERP.IntegrationTests/ERP.IntegrationTests.csproj -c Release --no-build --filter FullyQualifiedName~PurchaseInvoiceConcurrencySqlServerTests`
  覆盖**两个独立连接竞态**：（1）两张不同付款单并发引用同一发票 → 同币种容量只允许一方成功；
  （2）两张不同草稿发票并发关联同一采购订单 → 订单发票容量只允许一方成功；
  （3）付款引用登记 vs 发票作废；（4）草稿修改 vs 登记；（5）关联整体替换 vs 登记；
  以及真实身份授权（本人客户可读、范围外 / 无来源 / 无身份 fail closed、撤销菜单立即收敛且零改写）。
- 安全验证档（本任务交付门）：`dotnet restore` + Release 构建（`TreatWarningsAsErrors` / 分析器开启）+ `ERP.UnitTests` 全量。
- 本次实测证据（受控 localdb 真实执行，三次独立运行）：
  - Release 构建 `0 警告 / 0 错误`；
  - `ERP.UnitTests` 全量 **5732 通过 / 0 失败**；本任务新增单元筛选 **16 通过 / 0 失败**；
  - 真实 SQL 集成筛选 **6 通过 / 0 失败**，连续 **3 次独立运行**（各自全新 GUID `NEWERP_AUTOTEST` 库）结果一致；
  - 既有供应商付款 SQL 集成筛选 **28 通过 / 0 失败**（证明新增订单 / 发票行锁未破坏 ERP-049 / ERP-066 / ERP-351 / ERP-379 / ERP-380 语义）；
  - 目标护栏 fail-closed 单元覆盖 **4 通过 / 0 失败**。
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
- 测试只**新增**自己的证据行（唯一前缀 `INT_PI382_`），不清理、不删除、不重写任何既有行与原始失败日志。
- 夹具初始化 = `EnsureCreated` + `SchemaUpgrader` + `SeedData` + `SchemaUpgrader`；真实身份复用既有
  `purchase-order` 菜单与既有销售员客户范围口径，**不新增任何用户 / 角色 / 菜单 / 权限授权**。

## 8. 边界与保留项

- 本护栏只保护供应商采购发票证据与付款分摊容量：不改写发票身份 / 金额 / 状态 / 关联证据 / 作废原因与审计，不改写采购订单
  状态 / 到货进度 / 金额与明细 / 结算进度、付款单与货款申请单、库存与库存成本、库存流水、退税记录、费用与供应商余额；
  **不记账、不生成凭证 / 收付款 / 结算单、不做汇率换算、不跨币种合并、不重复计算证据、绝不物理删除或静默改写历史证据**。
- **权限模型不变**：只复用既有 `purchase-order` 菜单与既有数据范围口径（含既有系统内置角色特权），不新增用户 / 角色 /
  菜单 / 权限，也不引入匿名或管理员回退。
- **保留项**：库存来源单据审计（ERP-375 / ERP-376 / ERP-377 的来源选择与来源审计链路）与原始失败日志
  （`run.log` / `erp380-fullunit.log` / `TestResults` / `.ai/logs`）一律保持原样，不删除、不重写；本改动不触碰任何库存来源工作流。
- **依赖与兼容**：ERP-065（发票证据）/ ERP-066（付款 → 发票引用）/ ERP-049（付款 → 订单引用）/ ERP-351（共用付款额度）/
  ERP-380（来源申请单锁序）/ ERP-345（采购订单取消护栏）的既有行为、路由、审计与证据语义全部保留；
  `docs/purchase-order-cancellation.md`、`docs/supplier-payment-transition-concurrency.md`、`docs/payment-application-lifecycle.md`
  仍然有效，本文档只补充 ERP-382 的锁序、容量口径与双连接竞态。
- `RequiresLiveAuthorization` 只对「既无任何登录身份、又不在 HTTP 请求管线内」的进程内调用免授权（历史单元测试 /
  内部派生读取）：这类调用不可能由外部请求到达，且绝不把缺失身份当作管理员；真实匿名 HTTP 请求一律 fail closed。


