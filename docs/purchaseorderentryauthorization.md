# 采购订单入口实时授权（与请求路径 / 请求形状 / 请求上下文无关）

> 任务：`ERP-466`「Enforce canonical purchase order authority independently of request path」（阶段 3 关键授权入口修复）。
> 关联：`ERP-371`（采购订单实时授权与数据范围，`docs/purchaseorder-authorization.md`）、
> `ERP-427`（实时主数据引用复核，`docs/purchase-order-masterreference.md`）、
> `ERP-425`（写入锁 / 事务协议，`docs/purchase-order-mutation.md`）、
> `ERP-462` / `ERP-463` / `ERP-464` / `ERP-465`（同缺陷类的入口授权修复）、`ERP-097`（业务员客户数据范围）。

## 1. 修复前的问题证据

`src/ERP.Api/Controllers/PurchaseOrderController.cs` 的实时授权门在修复前只依据**请求形状**：

```csharp
private bool RequiresLiveAuthorization()
{
    var http = ControllerContext?.HttpContext;
    if (http is null) return false;                                   // 无请求上下文：免授权
    return http.Request.Path.HasValue || CurrentUserId() is not null;  // 空路径且无身份：免授权
}

private bool RequiresLiveMasterValidation()
    => ControllerContext?.HttpContext?.Request.Path.HasValue == true;  // 缺路径即跳过主数据复核
```

因此 `EnsureMenuAuthorizedAsync`、`EnsureOrderAuthorizedAsync`、`EnsureProposedAuthorizedAsync`、
`EnsureOrderIdAuthorizedAsync`、`EnsureOrderIdsAuthorizedAsync` 与列表范围下推 `ApplyProcurementScopeAsync`
在**空路径**（未赋值 `Request.Path`）或**无身份**时完全跳过实时身份 / 账号状态 / 既有「采购订单」菜单 /
ERP-097 权威客户范围判定；ERP-427 的实时主数据引用复核此前也只依据 `Request.Path.HasValue`。
后续一次修复把授权门改成「绑定到请求管线即授权」（`ControllerContext?.HttpContext is not null`），
但仍保留了**「`HttpContext` 为 null 的进程内直调免授权」**这一无请求上下文兼容边界，且主数据复核门仍只看
`Request.Path`；两者都仍属请求形状旁路，必须彻底移除。

## 2. 修复后的口径：无条件执行，无任何形状 / 上下文门

`RequiresLiveAuthorization()` 与 `RequiresLiveMasterValidation()` **已完全删除**；控制器改为：

```csharp
/// 当前登录用户 Id：缺少请求上下文 / 缺少身份声明 / 非数字 / 非正数一律安全解析为 null
private new long? CurrentUserId()
{
    var value = ControllerContext?.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    return long.TryParse(value, out var id) && id > 0 ? id : null;
}

private Task<SalespersonDataScope> EnsureMenuAuthorizedAsync()
    => PurchaseOrderAuthorizationRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId());

private Task EnsureMasterReferencesAsync(PurchaseOrder order)
    => PurchaseOrderMasterReferenceRules.EnsureMasterReferencesAsync(Db, order);
```

- **不存在任何「是否授权」的门**：身份只来自已认证请求主体，缺失 / 零 / 未知 / 已删除身份安全解析为 `null`，
  由既有授权规则在任何读取 / 写入<strong>之前</strong>返回受控未认证（`Err:Unauthorized` 2000）；
- **空路径 / 已赋值路径 / 纯进程内直调三种形状判决完全一致**，包括 `ControllerContext.HttpContext` 为 null
  （外部请求无法构造，但同样 fail closed，绝无免授权边界）；
- **主数据引用复核无条件执行**：新增 / 修改 / 提交 / 审核一律在单号预约、任何表头 / 明细赋值之前复核
  实时供应商 / 商品 / 可选采购员 / 起运港 / 既有有效单位口径，失败即受控拒绝、不落库、不占单号；
- 与 `ERP-462`（供应商采购发票证据）、`ERP-465`（销售订单读侧）同一口径；
- 控制器源码**不出现** `Request.Path` / `http.Request` / `RequiresLiveAuthorization` / `RequiresLiveMasterValidation`
  / `[AllowAnonymous]` / 角色白名单 / 环境变量 / 数据库提供程序 / 测试专用开关（源码契约测试逐条断言），
  绝无匿名、管理员或「无请求上下文」回退。

`PurchaseOrderAuthorizationRules.PathIndependenceText` 固化同一口径，与既有 `RuleText` / `BoundaryText`
共同构成「与请求形状无关」的可回归契约文案。

## 3. 逐入口先授权、后读写

| 入口 | 授权调用 | 失败语义 |
| --- | --- | --- |
| `GET /api/purchase-orders`（列表） | `ApplyProcurementScopeAsync`（先于计数 / 分页，范围下推） | 未认证 / 权限不足 |
| `GET /{id}`（详情）、`GET /{id}/print` | 先 `EnsureMenuAuthorizedAsync`（身份 / 菜单），再读单据，再 `EnsureOrderScopeAllowedAsync` | 未认证 / 权限不足 |
| `GET /sales-order-source-candidates`、`GET /{id}/sales-order-source` | `EnsureMenuAuthorizedAsync`（先于任何单据读取）+ `EnsureOrderScopeAllowedAsync` | 未认证 / 权限不足 |
| `GET /{id}/timeline`、`/progress`、`/finance-reconciliation`、`/return-impact`、`/invoice-evidence`、`/payment-evidence`、`/invoice-payment-evidence` | `EnsureOrderIdAuthorizedAsync`（身份 / 菜单先于任何读取，再逐单范围判定） | 未认证 / 权限不足 |
| `GET /invoice-evidence-summaries`、`/payment-evidence-summaries`、`/invoice-payment-evidence-summaries` | `EnsureOrderIdsAuthorizedAsync`（**空 Id 集合也绝不豁免**身份 / 菜单） | 未认证 / 权限不足 |
| `GET /supplier-exposure`、`/delivery-exceptions`、`/first-receipt-lead-times` | `EnsureMenuAuthorizedAsync` | 未认证 / 权限不足 |
| `GET /export`、`/export-excel` | `ApplyProcurementScopeAsync`（先于过滤与物化） | 未认证 / 权限不足 |
| `POST /`（新增）、`PUT /{id}`（修改） | 入口先 `EnsureMenuAuthorizedAsync`；随后 `EnsureProposedAuthorizedAsync` / `EnsureOrderAuthorizedAsync` + **无条件** `EnsureMasterReferencesAsync` | 未认证 / 权限不足 / 主数据受控错误 |
| `POST /{id}/submit`、`/{id}/approve`、`/{id}/cancel`、`DELETE /{id}` | 入口先 `EnsureMenuAuthorizedAsync`（先于事务与任何单据读取），锁内再 `EnsureOrderAuthorizedAsync` | 未认证 / 权限不足 |

列表在计数 / 分页之前把权威范围下推数据库；详情 / 打印 / 派生只读 / 批量汇总在返回任何字节之前完成实时身份 /
菜单 / 归属判定（详情与打印在读取单据字节之前先完成身份 / 菜单 fail closed）；写入（新增 / 修改 / 提交 / 审核 /
取消 / 删除）在事务开启、行锁、单号预约与任何字段 / 明细赋值之前完成身份 / 菜单判定，并在主数据引用复核之前完成
归属范围判定。缺失 / 零 / 未知 / 已删除身份 → `Err:Unauthorized`（2000）；禁用账号、缺少或撤销既有「采购订单」
（`purchase-order`）菜单 → `Err:Forbidden`（2002）。**被拒绝的调用零写入、零授权扩张且不消耗单据号**。

## 4. 保留的既有口径（不改业务语义）

- **既有菜单授权**：仍复用既有「采购订单」（`purchase-order`）功能菜单（与 `SeedData.Menus` 同源），
  **不新增**任何菜单 / 角色 / 用户授权，也不修改生产种子权限；特权账号（系统内置角色）沿用既有全部访问口径。
- **权威客户范围**：仍复用 `SalespersonDataScopeService`（ERP-097 唯一权威口径），范围同时覆盖显式归属客户
  （`OwningCustomerId`）与权威归属销售订单客户（`OwningSalesOrderId` 精确解析）；受限账号对无归属备货采购
  fail closed；范围外 / 已删除 / 不存在订单返回既有受控错误，**不泄露**范围外单据归属。
- **主数据引用（ERP-427）**：必填供应商、每条有效明细的必填商品、可选采购员 / 起运港与既有有效单位口径的判定
  逐字未改，只移除「是否执行」的形状门（不再依据 `Request.Path`、不再依据是否绑定请求上下文），绝不削弱任何校验器。
- **数值 / 单号 / 事务 / 并发边界不变**：数量 / 单价 / 金额 / 币种 / 汇率 / 单位、单号生成与预约、
  「归属销售订单 → 本采购订单」确定性行锁序、可串行化事务、收货容量口径、取消 / 审核 / 删除护栏与审计留痕完全不变。
- 未新增任何表 / 列 / 索引 / 权限模型。

## 5. 测试与验证

### 5.1 单元测试（内存库 + 真实既有身份 / 菜单 / 数据范围）

- `src/ERP.UnitTests/PurchaseOrderEntryAuthorizationTests.cs`：空路径与已赋值路径的**逐入口一致判定**
  （列表 / 详情 / 打印 / 导出 / Excel 导出 / 时间线 / 进度 / 财务核对 / 退货影响 / 发票 / 付款 / 发票付款证据 /
  新增 / 修改 / 提交 / 审核 / 取消 / 删除）；缺失 / 零 / 未知 / 已删除身份 → 未认证；禁用 / 缺少或撤销「采购订单」
  菜单 → 权限不足；授权身份在空路径请求上完成既有生命周期；受限业务员按 ERP-097 权威范围收口；拒绝调用零写入、
  不消耗单据号。**本次补齐**：
  - `Absent_request_context_is_unauthorized_on_every_core_route`：**完全没有 `HttpContext`** 的纯进程内直调
    在每条核心入口一律未认证、零写入、零单号消耗（无请求上下文豁免彻底移除）；
  - `Empty_path_write_enforces_live_master_references_identically`：**空路径与已赋值路径**对不存在 / 已停用供应商
    给出完全相同的受控拒绝（`NotFound` / `RuleConflict`），且零写入、零单号消耗（主数据复核不再看 `Request.Path`）；
  - 控制器源码契约：`Request.Path` / `http.Request` / `RequiresLiveAuthorization` / `RequiresLiveMasterValidation`
    必须**不存在**；身份解析必须安全返回 `null`；无 `AllowAnonymous` / 角色白名单 / 环境变量 / 内存库开关；
    主数据复核 `PurchaseOrderMasterReferenceRules.EnsureMasterReferencesAsync(Db, order)` 必须存在。
- **既有进程内夹具改为真实启用身份 + 既有「采购订单」菜单授权 + 既有合法主数据（隔离测试数据）**，
  并**刻意不设置 `Request.Path`**（空路径请求）：`PurchaseOrderControllerTests`、`PurchaseOrderMutationTests`、
  `PurchaseOrderWriteValidationTests`、`PurchaseOrderMasterReferenceTests`、`PurchaseOrderEntryAuthorizationTests`、
  `PurchaseSalesOrderLinkTests`、`OrderTraceabilityTests`（共享 `PurchaseOrderTestIdentities` 身份脚手架）；
  只补既有合法主数据（供应商 / 商品 / 在职采购员）缺失行，**不新增任何生产菜单 / 角色 / 用户授权**，
  也不改写任何校验口径。
- 只读派生接口夹具补齐既有启用身份：`PurchaseOrderProgressTests`、`PurchaseOrderReturnImpactTests`、
  `PurchaseOrderInvoiceEvidenceTests`、`PurchaseOrderInvoicePaymentEvidenceTests`、`PurchaseOrderPaymentEvidenceTests`、
  `PurchaseOrderDeliveryExceptionTests`、`SupplierFirstReceiptLeadTimeTests`、`SupplierPurchaseExposureTests`。
- 既有 ERP-371 / ERP-425 / ERP-426 / ERP-427 回归（`PurchaseOrderAuthorizationTests`、`PurchaseOrderMutationTests`、
  `PurchaseOrderMasterReferenceTests`、`PurchaseOrderWriteValidationTests`、`PurchaseOrderHttpFormContractTests`、
  `PurchaseOrderCancellationTests`、`PurchaseOrderOperationalDetailTests`、`PurchaseSalesOrderLinkTests`、
  `PurchaseInvoiceConcurrencyTests` 及既有采购只读派生单元测试）全部保持绿色。
- 交付门槛（`validation_profile = safe`）：**Release 构建 + 分析器 + 警告视为错误**，且**全量 `ERP.UnitTests`
  通过**（全量 7955 个用例，0 失败）；构建通过本身不构成整阶段验收。

### 5.2 真实隔离 SQL 测试

`src/ERP.IntegrationTests/PurchaseOrderEntryAuthorizationSqlServerTests.cs` 以真实控制器 + 真实既有身份 /
菜单 / 数据范围驱动：空路径与已赋值路径一致判定、缺失 / 零 / 未知 / 已删除身份未认证、禁用与撤销菜单权限不足、
授权身份读侧生命周期（含新增 → 修改 → 提交 → 审核）与实时主数据复核、被拒绝调用零写入 / 零授权扩张。
本次补齐：**完全没有请求上下文**（`ControllerContext.HttpContext` 为 null）时每条核心入口未认证且零写入 / 零单号消耗；
**空路径与已赋值路径的写入**对不存在 / 已停用供应商与已删除商品给出完全相同的受控拒绝且零写入 / 零单号消耗。

安全口径：目标必须是专用实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且
`Integrated Security=true`；每次运行只创建**全新 GUID 后缀库**，发现同名库已存在立即拒绝，**绝不 drop / reset /
复用**任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。

## 6. 授权判定边界（显式声明）

修复后**不存在任何授权 / 复核的形状门或上下文门**：

- 生产与集成环境中的**每一条**入口（空路径与已赋值路径、匿名与已认证）都强制走身份 / 账号状态 / 既有菜单 /
  权威范围判定；`ControllerContext.HttpContext` 为 null 的纯进程内直调同样 fail closed（受控未认证），
  既不读取请求路径，也不读取环境变量 / 数据库提供程序，更不存在测试专用开关或内部兼容边界。
- ERP-427 主数据引用复核与授权门同源：**无条件执行**，与请求路径 / 请求形状 / 是否绑定请求上下文完全无关。
- 既有进程内夹具不再依赖任何免授权边界：它们改为携带真实启用身份 + 既有菜单授权 + 既有合法主数据
  （仅隔离测试数据），因此「空路径请求同样受实时授权与主数据复核约束」在测试中同样成立。

## 7. 未执行 / 限制

- 本任务 `validation_profile = safe`、`completion_mode = build`：交付门槛为 **Release 构建（分析器 / 警告视为错误）+
  全量 `ERP.UnitTests`**；真实 SQL 集成用例为附加证据，构建或单元测试通过不等于整阶段验收。
- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置，
  未新增任何表 / 列 / 实体 / 菜单 / 权限 / 连接字符串或密钥。

## 8. 相关实现

- `src/ERP.Api/Controllers/PurchaseOrderController.cs`：删除 `RequiresLiveAuthorization()` /
  `RequiresLiveMasterValidation()`；新增安全身份解析 `CurrentUserId()`；全部授权辅助与列表范围下推无条件执行；
  详情 / 打印 / 来源解析改为「先身份 / 菜单 → 再读单据 → 再范围判定」；写入入口先做身份 / 菜单判定，
  主数据复核改为无条件转发 `PurchaseOrderMasterReferenceRules.EnsureMasterReferencesAsync`。
- `src/ERP.Application/Services/PurchaseOrderAuthorizationRules.cs`：`PathIndependenceText` 更新为
  「无条件执行、与请求路径 / 请求形状 / 请求上下文无关」契约文案；既有 `EnsureMenuAuthorizedAsync` /
  `EnsureOrderAuthorizedAsync` / `EnsureProposedAuthorizedAsync` / `EnsureOrdersAuthorizedAsync` /
  `EnsureOrderScopeAllowedAsync` / `LoadOwnershipAsync` / `ApplyScopeAsync` 判定口径与
  `RuleText` / `BoundaryText` 保持不变（未削弱任何校验器）。
- `src/ERP.Application/Services/PurchaseOrderMasterReferenceRules.cs`：判定口径逐字未改（本次未修改该文件内容）。
- `src/ERP.UnitTests/PurchaseOrderEntryAuthorizationTests.cs`：共享身份脚手架 `PurchaseOrderTestIdentities`
  （既有启用账号 + 既有「采购订单」菜单 + 空路径控制器）与新增回归用例。
- 复用既有：`SalespersonDataScopeService`（ERP-097）、`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`
  （角色 → 菜单）、既有「采购订单」（`purchase-order`）菜单（`SeedData.Menus`）、
  `PurchaseOrderCancellationRules` / `PurchaseOrderMutationRules`（行锁与事务）。
