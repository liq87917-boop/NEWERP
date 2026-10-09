# 规范销售订单（`api/sales-orders`）执行证据入口实时授权

> 任务：`ERP-413`「Authorize canonical sales order execution evidence at every record entry」（阶段 3 核心业务流程完整性）。
> 关联：`ERP-412`（附件引用登记 / 作废与父单据变更串行化）、`ERP-401`（销售订单普通表单保存来源血缘护栏，`docs/sales-order-source-lineage.md`）、
> `ERP-047`（销售订单变更申请登记）、`ERP-410` / `ERP-411`（**旧** v2 专用路由 `api/v2/sales-orders` 写入门禁与读侧范围，
> `docs/sales-order-proc-mutation-boundary.md` / `docs/sales-order-proc-read-scope.md`）、`ERP-097`（业务员客户数据范围，`docs/业务员数据范围说明.md`）。

## 1. 背景与问题证据（修复前）

规范销售订单控制器 `SalesOrderController`（路由 `api/sales-orders`）的执行证据**单张**路由此前直接派生，
**不解析**实时身份 / 既有「销售订单」功能菜单 / 业务员客户数据范围：

| 路由 | 修复前行为 |
| --- | --- |
| `GET {id}/timeline` | 直接调用 `OrderExecutionTimeline.ForSalesOrderAsync`，无任何授权 |
| `GET {id}/finance-reconciliation` | 直接调用 `OrderFinanceReconciliation.ForSalesOrderAsync`，无任何授权 |
| `GET {id}/progress` | 直接调用 `SalesOrderProgress.ForSalesOrderAsync`，无任何授权 |
| `GET {id}/return-impact` | 直接调用 `SalesOrderReturnImpact.ForOrderAsync`，无任何授权 |
| `GET {id}/receipt-evidence` | 直接调用 `SalesOrderReceiptEvidence.ForOrderAsync`，无任何授权 |
| `GET {id}/invoice-evidence` | 直接调用 `SalesOrderInvoiceEvidence.ForOrderAsync`，无任何授权 |
| `GET receipt-evidence-summaries` / `invoice-evidence-summaries` | 按显式 Id 批量派生，无任何授权、混入越权 Id 仍返回其汇总行 |

只有类级 `[Authorize]`，且 `GetPaged` / `GetById` 仅有继承的 `SalespersonDataScopeService` 范围解析（`GetById` 按持久化客户归属 fail closed）。
因此任何已登录账号只要**猜到一张范围外订单 Id**，就能读取该订单的出运时间线、财务核对、出货 / 收款进度、退货影响、
收款引用证据与销项发票证据（含金额 / 币种 / 单号 / 客户快照），并可显式批量拉取任意订单的汇总。

**这是运营记录入口的授权缺口，不是「再发布一份报表」**：本任务只收敛入口授权，不新增报表、不改写任何业务口径。

## 2. 集中化的规范订单护栏（`SalesOrderExecutionAuthorizationRules`）

新增 `src/ERP.Application/Services/SalesOrderExecutionAuthorizationRules.cs`（**唯一权威口径**），调用方（控制器）在任何证据派生之前调用：

| 方法 | 作用 |
| --- | --- |
| `EnsureReadAuthorizedAsync(db, userId)` | 执行证据入口完整授权：实时身份 + 既有「销售订单」菜单 + ERP-097 数据范围；特权账号豁免菜单但仍须通过实时身份 |
| `EnsureLiveIdentityAsync(db, userId)` | 列表 / 详情入口的实时身份 + 范围（不额外要求模块菜单，见 §6） |
| `EnsureOrderAllowedAsync(db, scope, orderId)` | 单张订单按**持久化 `SalesOrder.CustomerId`** 的权威归属复核（最小投影，有界只读） |
| `EnsureOrdersAllowedAsync(db, scope, orderIds)` | 批量显式 Id 的整批归属复核（任一不可访问即整批拒绝，绝不返回部分行 / 计数） |

口径（fail closed，绝不缓存，每次请求重新解析）：

1. **实时身份**：`userId` 缺失 / 非法按未认证拒绝（`Err:Unauthorized`）；账号不存在 / 已删除按未认证拒绝；账号已禁用按权限不足拒绝（`Err:Forbidden`）。
   绝不把空身份当作匿名或管理员，也绝不做管理员降级。
2. **既有菜单授权**：普通账号必须实时具备既有「销售订单」（`sales-order`）功能菜单；**导出专用菜单 `sales-order-export` 绝不当作模块权限**；
   特权账号（超级管理员 / 系统内置角色 / 显式特权角色）沿用既有全部访问口径。**不新增任何菜单 / 角色 / 用户授权**。
3. **权威客户范围**：只复用唯一权威口径 `SalespersonDataScopeService`（ERP-097）；归属严格按**持久化 `SalesOrder.CustomerId`** 判定，
   **绝不**按订单号 / 客户名等自由文本、金额或相似度推断，也**绝不**把旧库 `Oid` 推断为规范 `Id`。
4. **非披露错误**：范围外订单、已删除订单与不存在订单返回**同一**受控错误「销售订单不存在」（`Err:NotFound`）——
   授权先于任何证据读取，错误差异 / 计数 / 部分行都不透露不可访问订单的存在性。

## 3. 逐入口接线（先授权、后派生）

- **单张执行证据**（`timeline` / `finance-reconciliation` / `progress` / `return-impact` / `receipt-evidence` / `invoice-evidence`）：
  `EnsureExecutionEvidenceAuthorizedAsync()` → `EnsureOrderEvidenceAuthorizedAsync(id, scope)` → 才调用既有派生服务。
- **列表 / 批量显式 Id 证据汇总**（`receipt-evidence-summaries` / `invoice-evidence-summaries`）：
  先整批复核显式 Id 的权威归属，**混入任何不存在 / 已删除 / 范围外订单即整批拒绝**（不返回部分行或计数，也不泄露哪些 Id 不可访问）；
  空 Id 清单视为「未请求任何订单」，正常返回空集合。
- **进程内直调边界**：真实 HTTP 请求（`Request.Path` 已赋值）一律执行授权；仅「既无任何登录身份、又不在 HTTP 请求管线内」的
  进程内调用免授权（这类调用不可能由外部请求到达，见 `RequiresLiveAuthorization()`，与既有 ERP-371 采购订单同源口径）。
  **真实匿名请求因处于请求管线内一律 fail closed（未认证）**。

## 4. 保留既有运营证据口径（不改写、不新增）

- 授权只发生在派生之前，**不改写**任何派生算法与响应 DTO 契约：数量 / 单价 / 金额 / 合计 / 定金 / 币种 / 汇率 / 单位、
  `null`-as-unknown、部分出货 / 部分退货 / 部分收款 / 部分开票语义、`Truncated`（按未知）与证据分桶口径全部保持不变。
- 被拒绝的请求**零写入**：不改写销售订单 / 明细 / 出库 / 退货 / 收款 / 发票 / 财务 / 客户主数据，不产生操作 / 通知日志，
  不删除历史证据与审计留痕。每个被拒请求都有界（单张 / 有界批量），不携带任何财务载荷或密钥。
- 每个被拒请求都由既有全局中间件 `ExceptionHandlingMiddleware`（`Program.cs` 注册）记录**有界**告警：
  仅记录错误文案 / 错误码 / 请求路径，绝不回声请求载荷、订单证据、金额或密钥；返回的受控信封同样只含
  `{ code, message, data: null }`，不含任何证据行 / 计数。
- **不新增**表 / 列 / 索引 / 菜单 / 权限模型，**不新增任何用户授权**，不做报表迁移 / 入口切换，也不触碰库存来源单据审计与原始失败日志。

## 5. 验证证据

### 5.1 单元测试（`src/ERP.UnitTests/SalesOrderExecutionAuthorizationTests.cs`，内存库 + 真实既有身份 / 菜单 / 范围）

- **本人订单可读**：受限业务员（既有「销售订单」菜单 + 本人客户）在六个单张入口与两个批量汇总入口全部可读；
- **统一非披露错误**：他人订单 / 已删除订单 / 不存在订单在**每个入口**返回同一 `Err:NotFound` + 同一文案（`GetById` 同口径）；
- **批量整批拒绝**：`本人 + 他人` / `本人 + 不存在` 混入即整批拒绝（无部分行）；仅本人与空清单正常返回；
- **身份拒绝矩阵**：无菜单 / 仅导出菜单 / 撤销菜单 → `Err:Forbidden`；已禁用 → `Err:Forbidden`；已删除账号 / 真实匿名请求 → `Err:Unauthorized`；
  撤销菜单后下一次请求立即收敛；
- **特权账号**：保留既有全量口径（可读他人订单证据）；
- **零写入**：拒绝前后 `SalesOrders` / `SalesOrderDetails` / `StockOuts` / `StockMovements` / `FinanceReceipts` /
  `CustomerReceiptAllocations` / `CustomerSalesInvoiceEvidences` 行数与订单状态 / 软删 / 金额不变，`SysOperationLogs` 为空；
- **允许派生保留既有语义**：部分出货（4/10）与部分退货（净 3）在 `progress` / `return-impact` 上不变；
  部分收款（350）与部分开票（350）在 `receipt-evidence` / `invoice-evidence` 上不变，且与直接派生服务结果逐项一致；
- **进程内直调**：无 HTTP 管线且无身份的进程内直调保持既有免授权口径（执行证据入口），详情入口沿用既有 fail closed 行为。

### 5.2 真实隔离 SQL 测试（`src/ERP.IntegrationTests/SalesOrderExecutionAuthorizationSqlServerTests.cs`）

- **专用目标护栏**：必须在任何数据库访问之前精确命中 `(localdb)\NEWERP_AutoAcceptance` + 库名前缀 `NEWERP_AUTOTEST`
  + `Integrated Security=true`；错误实例 / 错误库名 / 非集成安全的连接串一律在 `AssertDedicatedTarget` 阶段被拒绝
  （`SalesOrderExecutionAuthorizationTargetGuardTests`）。每次运行只创建一个**全新 GUID 后缀库**，发现同名库已存在立即拒绝，
  **绝不 drop / reset / 复用**任何数据库，也绝不读取 `appsettings*.json` / `.env` / 生产凭据。
- **真实驱动 + 既有授权**：只在本隔离 GUID 库内播种既有「销售订单」功能菜单授权 + 业务员客户数据范围 + 两位客户订单证据；
  以真实 `SalesOrderController` 驱动，不复制测试专用实现，不新增 / 不修改任何既有菜单 / 角色 / 用户授权，无匿名 / 管理员降级。
- **逐入口证明**：受限业务员下本人订单六个入口可读且与直接派生结果**逐项一致**（部分出货 4/10、部分退货净 3、
  部分收款 350、部分开票 350）；他人 / 已删除 / 不存在订单在每个入口返回同一非披露错误；批量显式 Id 混入不可访问订单整批拒绝。
- **身份 / 菜单拒绝矩阵**：无身份 / 已删除账号 → 未认证；已禁用 / 无菜单 / 仅导出菜单 / 已撤销菜单 → 权限不足（逐入口断言）。
- **特权账号**：保留既有全量口径（可读他人订单证据）。
- **零写入证据**：每次拒绝 / 读取前后对 `SalesOrders` / `SalesOrderDetails` / `StockOuts` / `StockOutDetails` /
  `StockMovements`（**保留库存来源单据审计**）/ `CustomerReceiptAllocations` / `CustomerSalesInvoiceEvidences` /
  `CustomerSalesInvoiceAllocations` / `FinanceDepositApplies` / `SysOperationLogs` 做只读快照，全部不变。
- **两个独立连接竞态**：① 两条独立连接并发读取同一本人订单证据 → 结果一致；② 一条合法本人读取与一条他人越权并发 →
  合法读取成功、越权 fail closed，收尾快照证明零写入。
- **保留既有库存来源单据审计与原始失败日志**：测试只读取计数与只读快照，不删除 / 不清理任何既有行。

### 5.3 构建与回归

- `.NET 8` Release 构建（`NEWERP.sln`）通过，0 警告 / 0 错误（`ERP.IntegrationTests` 启用了 `TreatWarningsAsErrors`）。
- `ERP.UnitTests` 新增 `SalesOrderExecutionAuthorizationTests`（13 例全绿）与既有销售订单 / 业务员数据范围 / 订单追溯回归保持不变。
- **构建完成不等于阶段验收**：真实 SQL 用例只有在受控 localdb 上真实执行通过才构成验收证据；
  浏览器验收按任务配置为 `browser_acceptance.required = false`：不运行真实 Edge / UI / 截图验收。

## 6. 明确边界（非目标）

- **列表 / 详情**（`GetPaged` / `GetById`）沿用既有 ERP-097 范围口径并补上**实时身份校验**（缺失 / 已删除 / 已禁用 fail closed）；
  为保持既有锁定契约（`SalespersonDataScopeTests.SalesOrderController_GetById_越界订单_fail_closed` 期望越界返回同一 `Err:NotFound`），
  这两个入口**不额外要求模块菜单**（模块菜单要求只施加于执行证据入口）。
- 不修改任何既有存储过程 / 数据库结构 / 迁移脚本 / 种子数据；不新增表 / 列 / 索引。
- 不新增权限模型（菜单 / 角色 / 用户授权），不提供匿名或管理员降级，不把导出菜单当模块权限，不做旧库 `Oid` → 规范 `Id` 推断。
- 不改写销售订单普通表单工作流（`ERP-401` 来源血缘护栏）、取消护栏（`ERP-347`）、转换工作流，也不改变旧 v2 专用路由
  （`ERP-410` / `ERP-411`）的既有门禁与读侧范围。
- 不清理 / 不删除任何既有业务行、库存来源审计或既有失败日志；不执行生产凭据或生产数据操作。
- 浏览器验收按任务配置为 `browser_acceptance.required = false`：不运行真实 Edge / UI / 截图验收。

## 7. ERP-432：运营核对读取路由（`delivery-exceptions` / `shipment-finance-report` / `receipt-reconciliation-report`）

ERP-413 只收敛了**单张**执行证据（时间线 / 财务核对 / 进度 / 退货影响 / 收款与发票证据，含列表批量汇总）；
同一控制器里三条**运营核对读取**路由仍直接派生，不解析实时身份 / 既有菜单 / 客户范围：

| 路由 | 修复前行为 |
| --- | --- |
| `GET /api/sales-orders/delivery-exceptions` | 直接调用 `SalesOrderDeliveryExceptions.ForQueryAsync`，无任何授权 |
| `GET /api/sales-orders/shipment-finance-report` | 直接调用 `SalesOrderShipmentFinanceReport.ForQueryAsync`，无任何授权 |
| `GET /api/sales-orders/receipt-reconciliation-report` | 直接调用 `SalesOrderReceiptReconciliation.ForQueryAsync`，无任何授权 |

任何已登录账号只要调用这三条路由，就能读取**全部客户**的交期 / 出货 / 收款 / 发票计数与金额。

**修复（复用既有唯一权威口径，不新增任何授权）**：

- 三条路由在**任何派生之前**统一调用既有 `EnsureExecutionEvidenceAuthorizedAsync()`（= 实时身份 + 既有「销售订单」`sales-order`
  菜单 + ERP-097 权威客户范围，特权账号豁免菜单但仍须通过实时身份；口径与 ERP-413 单张证据入口**逐字一致**）。
- **出货 / 财务进度报表与订单收款核对报表**：解析出的 `SalespersonDataScope` 直接作为两个派生助手的既有可选 `scope`
  参数下推（`SalesOrderShipmentFinanceReport.ForQueryAsync` / `SalesOrderReceiptReconciliation.ForQueryAsync`），在
  `ApplyFilters` 内、`Count` / 分组 / 分页与任何物化**之前**按 `SalespersonDataScopeService.FilterByCustomer` 过滤：
  受限账号只命中范围内客户，绝不返回范围外客户的订单计数 / 数量 / 出货数量 / 收款金额。
- **交期异常工作台**：其既有唯一客户筛选是单一 `SalesOrderDeliveryExceptionQuery.CustomerId`，无法表达「范围内全部客户」
  的集合过滤（该派生助手的既有两参签名不在本任务允许改动范围内，不新增第二个过滤口径）。因此控制器复用新增的**纯判定**
  `SalespersonDataScopeService.TryResolveScopedCustomerFilter(scope, requestedCustomerId, out effectiveCustomerId)`
  把权威范围归约为该单客户筛选，并在派生之前下推：
  - 特权账号：**不过滤**，原样保留请求值（既有全量口径）。
  - 受限账号**显式指定**客户：范围内 → 按该客户派生；范围外 → **fail closed**（不派生）。
  - 受限账号**未指定**客户：范围恰为单一客户时其「全部客户」即该客户（按该客户派生）；范围非单一客户 → **fail closed**
    （无法由单客户筛选安全表达「范围内全部客户」，绝不冒然带范围外条件派生）。
  - fail closed 一律返回与本工作台**同口径的空报表**（`Total = 0`、空 `Items`、空 `Counts`），不透露不可访问客户的存在性。
- **拒绝语义**：身份 / 菜单拒绝返回既有受控非披露错误（`Err:Unauthorized` / `Err:Forbidden`）与既有文案，
  **不返回任何行或计数**；范围外 / 无法表达的客户筛选返回**空集与 0 计数**，不透露不可访问客户的存在性。
- **保留契约**：响应 DTO、币种 / 单位分离、`null`-as-unknown、`Truncated` 与证据分桶口径**全部不变**；
  拒绝路径**零写入**（不改销售订单 / 出库 / 收款 / 分摊 / 发票 / 财务 / 库存行，不产生操作日志）。
- **进程内直调边界**：与 ERP-413 只读证据入口同源 —— 真实 HTTP 请求（`Request.Path` 已赋值）一律执行授权；
  仅「既无任何登录身份、又不在 HTTP 请求管线内」的进程内调用保持既有免授权口径（既有静态派生单测据此沿用两参调用）。

### 7.1 验证证据（`ERP-432`）

- **单元测试**（`src/ERP.UnitTests/SalesOrderExecutionAuthorizationTests.cs` §9）：受限业务员（单一分配客户）三条路由只返回
  范围内客户；筛选范围外客户返回空且无计数；受限多客户未指定客户时交期异常工作台 fail closed（空报表）而两个集合范围报表
  仍只返回范围内全部客户；显式指定范围内客户时三条路由只返回该客户；特权账号保留既有全量口径；
  `TryResolveScopedCustomerFilter` 的纯判定分支（特权 / 显式范围内 / 显式范围外 / 单一客户默认 / 多客户与空范围 fail closed）
  逐条断言；无身份 / 已删除 / 已禁用 / 无菜单 / 仅导出菜单 / 已撤销菜单六类身份在三条路由上**逐条** fail closed 且零写入。
- **真实隔离 SQL 测试**（`src/ERP.IntegrationTests/SalesOrderExecutionAuthorizationSqlServerTests.cs` §7）：同样只使用
  全新 GUID 的 `(localdb)\NEWERP_AutoAcceptance` / `NEWERP_AUTOTEST_*` / `Integrated Security=true` 库，
  `AssertDedicatedTarget` 在任何数据库访问之前放行；以真实 `SalesOrderController` 逐条驱动三条路由证明
  受限本人可见 / 范围外为空 / 特权全量 / 拒绝矩阵 fail closed（零写入快照）。
- **构建与回归**：`.NET 8` Release 构建 0 警告 0 错误；`ERP.UnitTests` 全量回归保持绿色。
  构建完成不等于阶段验收：真实 SQL 用例必须在受控 localdb 上真实执行通过才算验收证据。

