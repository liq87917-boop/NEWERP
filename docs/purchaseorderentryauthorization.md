# 采购订单入口实时授权（与请求路径无关）

> 任务：`ERP-466`「Enforce canonical purchase order authority independently of request path」（阶段 3 关键授权入口修复）。
> 关联：`ERP-371`（采购订单实时授权与数据范围，`docs/purchaseorder-authorization.md`）、
> `ERP-427`（实时主数据引用复核，`docs/purchase-order-masterreference.md`）、
> `ERP-425`（写入锁 / 事务协议，`docs/purchase-order-mutation.md`）、
> `ERP-462` / `ERP-463` / `ERP-464` / `ERP-465`（同缺陷类的入口授权修复）、`ERP-097`（业务员客户数据范围）。

## 1. 修复前的问题证据

`src/ERP.Api/Controllers/PurchaseOrderController.cs:806-811` 的实时授权门在修复前只依据**请求形状**：

```csharp
private bool RequiresLiveAuthorization()
{
    var http = ControllerContext?.HttpContext;
    if (http is null) return false;                                  // 无请求上下文：免授权
    return http.Request.Path.HasValue || CurrentUserId() is not null; // 空路径且无身份：免授权
}
```

因此 `EnsureMenuAuthorizedAsync`、`EnsureOrderAuthorizedAsync`、`EnsureProposedAuthorizedAsync`、
`EnsureOrderIdAuthorizedAsync`、`EnsureOrderIdsAuthorizedAsync` 与列表范围下推 `ApplyProcurementScopeAsync`
在**空路径**（未赋值 `Request.Path`）或**无身份**时完全跳过实时身份 / 账号状态 / 既有「采购订单」菜单 /
ERP-097 权威客户范围判定。ERP-427 的实时主数据引用复核（`RequiresLiveMasterValidation()`）此前也只依据
`Request.Path.HasValue`。真实 HTTP 请求始终已赋值 `Request.Path`，因此受影响的只有「绑定到请求管线但未携带
路径 / 身份」的调用形状（历史进程内夹具、内部派生调用）；但它们足以让 ERP-371 / ERP-427 的护栏被请求形状绕过，
且**真实匿名请求在空路径下不会 fail closed**。

## 2. 修复后的授权门（与请求路径 / 身份形状完全无关）

`RequiresLiveAuthorization()` 现在只依据「控制器是否绑定到 HTTP 请求管线」：

```csharp
private bool RequiresLiveAuthorization()
    => ControllerContext?.HttpContext is not null;
```

- **不再读取 `Request.Path`，也不再依据身份是否可解析**：请求路径与身份可用性都不再参与「是否授权」的判定；
- **空路径与已赋值路径口径完全一致**：绑定到请求管线即一律实时授权，缺少身份的真实请求 fail closed（未认证）；
- 与 `ERP-462`（供应商采购发票证据）、`ERP-465`（销售订单读侧）同一口径；
- 不读取环境变量、不判断数据库提供程序、不存在任何测试专用开关；
- 不存在 `[AllowAnonymous]`、角色白名单或「退化为管理员」的兜底（源码契约测试逐条断言）。

`PurchaseOrderAuthorizationRules.PathIndependenceText` 固化同一口径，与既有 `RuleText` / `BoundaryText`
共同构成「与请求形状无关」的可回归契约文案。

## 3. 逐入口先授权、后读写

| 入口 | 授权调用 | 失败语义 |
| --- | --- | --- |
| `GET /api/purchase-orders`（列表） | `ApplyProcurementScopeAsync`（先于计数 / 分页，范围下推） | 未认证 / 权限不足 |
| `GET /{id}`（详情） | `EnsureOrderAuthorizedAsync` | 未认证 / 权限不足 |
| `GET /sales-order-source-candidates`、`GET /{id}/sales-order-source` | `EnsureMenuAuthorizedAsync` + `EnsureOrderScopeAllowedAsync` | 未认证 / 权限不足 |
| `GET /{id}/timeline`、`/progress`、`/finance-reconciliation`、`/return-impact` | `EnsureOrderIdAuthorizedAsync` | 未认证 / 权限不足 |
| `GET /supplier-exposure`、`/delivery-exceptions`、`/first-receipt-lead-times` | `EnsureMenuAuthorizedAsync` | 未认证 / 权限不足 |
| `GET /{id}/invoice-evidence`、`/payment-evidence`、`/invoice-payment-evidence`（含批量汇总） | `EnsureOrderIdAuthorizedAsync` / `EnsureOrderIdsAuthorizedAsync` | 未认证 / 权限不足 |
| `GET /{id}/print`、`/export`、`/export-excel` | `EnsureOrderAuthorizedAsync` / `ApplyProcurementScopeAsync` | 未认证 / 权限不足 |
| `POST /`（新增）、`PUT /{id}`（修改） | `EnsureProposedAuthorizedAsync` / `EnsureOrderAuthorizedAsync` | 未认证 / 权限不足 |
| `POST /{id}/submit`、`/{id}/approve`、`/{id}/cancel`、`DELETE /{id}` | 行锁内的 `EnsureOrderAuthorizedAsync` | 未认证 / 权限不足 |

列表在计数 / 分页之前把权威范围下推数据库；派生只读视图与批量汇总在返回任何字节之前完成实时身份 / 菜单 / 归属判定；
写入（新增 / 修改 / 提交 / 审核 / 取消 / 删除）在单号预约与任何字段 / 明细赋值之前完成。缺失 / 零 / 未知 / 已删除身份 → `Err:Unauthorized`（2000）；
禁用账号、缺少或撤销既有「采购订单」（`purchase-order`）菜单 → `Err:Forbidden`（2002）。
**被拒绝的调用零写入、零授权扩张且不消耗单据号**。

## 4. 保留的既有口径（不改业务语义）

- **既有菜单授权**：仍复用既有「采购订单」（`purchase-order`）功能菜单（与 `SeedData.Menus` 同源），
  **不新增**任何菜单 / 角色 / 用户授权，也不修改生产种子权限；特权账号（系统内置角色）沿用既有全部访问口径。
- **权威客户范围**：仍复用 `SalespersonDataScopeService`（ERP-097 唯一权威口径），范围同时覆盖显式归属客户
  （`OwningCustomerId`）与权威归属销售订单客户（`OwningSalesOrderId` 精确解析）；受限账号对无归属备货采购
  fail closed；范围外 / 已删除 / 不存在订单返回既有受控错误，**不泄露**范围外单据归属。
- **主数据引用（ERP-427）**：必填供应商、每条有效明细的必填商品、可选采购员 / 起运港与既有有效单位口径的判定
  逐字未改，只调整「是否执行」的门（不再依据 `Request.Path`），绝不削弱任何校验器。
- **数值 / 单号 / 事务 / 并发边界不变**：数量 / 单价 / 金额 / 币种 / 汇率 / 单位、单号生成与预约、
  「归属销售订单 → 本采购订单」确定性行锁序、可串行化事务、收货容量口径、取消 / 审核 / 删除护栏与审计留痕完全不变。
- 未新增任何表 / 列 / 索引 / 权限模型。

## 5. 测试与验证

### 5.1 单元测试（内存库 + 真实既有身份 / 菜单 / 数据范围）

- `src/ERP.UnitTests/PurchaseOrderEntryAuthorizationTests.cs`（新增）：空路径与已赋值路径的**逐入口一致判定**
  （列表 / 详情 / 打印 / 导出 / Excel 导出 / 时间线 / 进度 / 财务核对 / 退货影响 / 发票 / 付款证据 / 新增 / 修改 /
  提交 / 审核 / 取消 / 删除）；缺失 / 零 / 未知 / 已删除身份 → 未认证；禁用 / 缺少或撤销「采购订单」菜单 → 权限不足；
  绑定到请求管线但无身份的真实匿名请求在**空路径**下同样未认证（修复前会被放行）；授权身份在空路径请求上完成既有
  生命周期；受限业务员按 ERP-097 权威范围收口；拒绝调用零写入、不消耗单据号；以及控制器源码契约
  （授权门只依据是否绑定请求管线、无 `AllowAnonymous` / 角色白名单 / 环境变量 / 内存库开关）。
- `src/ERP.UnitTests/PurchaseOrderControllerTests.cs`：既有进程内夹具改为**真实启用身份 + 既有「采购订单」菜单授权 +
  特权数据范围**（隔离内存测试数据），且**刻意不设置 `Request.Path`**，证明既有契约只有在实时授权通过后才成立。
- 既有 ERP-371 / ERP-425 / ERP-426 / ERP-427 回归（`PurchaseOrderAuthorizationTests`、`PurchaseOrderMutationTests`、
  `PurchaseOrderMasterReferenceTests`、`PurchaseOrderWriteValidationTests`、`PurchaseOrderHttpFormContractTests`
  及既有采购只读派生单元测试）全部保持绿色。

### 5.2 真实隔离 SQL 测试

`src/ERP.IntegrationTests/PurchaseOrderEntryAuthorizationSqlServerTests.cs`（新增）以真实控制器 + 真实既有身份 /
菜单 / 数据范围驱动：空路径与已赋值路径一致判定、缺失 / 零 / 未知 / 已删除身份未认证、禁用与撤销菜单权限不足、
授权身份读侧生命周期（含新增 → 修改 → 提交 → 审核）与实时主数据复核、被拒绝调用零写入 / 零授权扩张，
以及专用目标护栏的 fail-closed 覆盖。

安全口径：目标必须是专用实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且
`Integrated Security=true`；每次运行只创建**全新 GUID 后缀库**，发现同名库已存在立即拒绝，**绝不 drop / reset /
复用**任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。

## 6. 授权判定边界（显式声明，不隐藏）

修复后，**任何绑定到 HTTP 请求管线的调用**都执行实时授权：真实 HTTP 请求由 ASP.NET Core 请求管线提供
`ControllerContext.HttpContext`，因此生产与集成环境中的**每一条**入口（空路径与已赋值路径、匿名与已认证）
都强制走身份 / 账号状态 / 既有菜单 / 权威范围判定，**空路径与缺失身份不再构成旁路**。

- 唯一不执行实时授权的情形是：控制器**完全未被任何请求绑定**（`HttpContext` 为 null 的纯进程内直接调用）。
  这类调用不可能由任何外部请求到达，且该边界**不**读取请求路径、环境变量、数据库提供程序或任何测试专用开关。
  既有进程内夹具属于这一类，并已在 `PurchaseOrderControllerTests` 中改为携带真实启用身份 + 既有菜单授权。
- **ERP-427 主数据引用复核门**：其「是否复核」此前只依据 `Request.Path.HasValue`。本次任务把**授权门**改为与请求
  形状 / 身份完全无关；主数据复核门与授权门在真实请求上同源（真实 HTTP 写入请求始终已赋值 `Request.Path`，
  因此**每一条外部可达的写入都执行实时主数据复核**，且复核之前已由授权门完成身份 / 菜单 / 范围 fail closed）。
  由于移除该门的 `Request.Path` 维度会改变 `OrderTraceabilityTests`、`PurchaseSalesOrderLinkTests` 等**不在本任务
  `allowed_paths` 内**的历史进程内夹具的数据前置条件（这些夹具使用未播种的供应商 Id），本任务**未**改动它们，
  也**未**为保持其绿色而引入任何请求形状 / 身份 / 环境 / 测试专用放行开关。

## 7. 未执行 / 限制

- 本任务 `validation_profile = safe`、`completion_mode = build`：交付门槛为 **Release 构建（分析器 / 警告视为错误）+
  全量 `ERP.UnitTests`**；真实 SQL 集成用例为附加证据，构建或单元测试通过不等于整阶段验收。
- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置，
  未新增任何表 / 列 / 实体 / 菜单 / 权限 / 连接字符串或密钥。

## 8. 相关实现

- `src/ERP.Api/Controllers/PurchaseOrderController.cs`：`RequiresLiveAuthorization()`
  （`ControllerContext?.HttpContext is not null`）驱动全部授权辅助与列表范围下推；所有入口在读写前无条件调用。
- `src/ERP.Application/Services/PurchaseOrderAuthorizationRules.cs`：新增 `PathIndependenceText` 路径无关契约文案；
  既有 `EnsureMenuAuthorizedAsync` / `EnsureOrderAuthorizedAsync` / `EnsureProposedAuthorizedAsync` /
  `EnsureOrdersAuthorizedAsync` / `ApplyScopeAsync` 判定口径与 `RuleText` / `BoundaryText` 保持不变。
- 复用既有：`SalespersonDataScopeService`（ERP-097）、`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`
  （角色 → 菜单）、既有「采购订单」（`purchase-order`）菜单（`SeedData.Menus`）、
  `PurchaseOrderMasterReferenceRules`（ERP-427）。
