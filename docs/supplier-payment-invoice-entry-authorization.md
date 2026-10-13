# 供应商付款发票引用证据入口实时授权（与请求形状无关，ERP-488）

> 任务：`ERP-488`「Enforce supplier payment invoice-allocation authority at every business entry」。
> 关联：`ERP-433`（付款引用证据实时授权与客户范围，`docs/supplier-payment-allocation-authority.md`）、
> `ERP-382`（唯一全局锁序与共享额度）、`ERP-487`（同缺陷类修复：客户销项发票 / 收款分摊证据入口，
> `docs/customer-sales-invoice-entry-authorization.md`）、`ERP-468`（同缺陷类修复：付款单 → 采购订单引用入口）、
> `ERP-066`（付款单 → 供应商采购发票付款引用证据）、`ERP-351` / `ERP-379` / `ERP-380`（付款单生命周期护栏）、
> `ERP-097`（业务员客户数据范围）。

## 1. 修复前的问题证据

`src/ERP.Api/Controllers/SupplierPaymentInvoiceAllocationController.cs` 的实时授权门在修复前只依据**请求形状**：

```csharp
private bool RequiresLiveAuthorization()
{
    var http = ControllerContext?.HttpContext;
    if (http is null) return false;                                   // 无请求上下文：免授权
    return http.Request.Path.HasValue || CurrentUserId() is not null; // 空路径且无身份：免授权
}
```

因此：

- `Create` 在缺失 `HttpContext` 或「空 `Request.Path` 且无身份」时**直接调用无授权的服务方法**
  `SupplierPaymentInvoiceAllocationService.CreateAsync`，整段绕过实时身份 / 既有「付款单」（`payment`）菜单 /
  ERP-097 权威客户数据范围（含被引用付款单与来源货款申请单归属客户）判定；
- `Void` 在同样条件下**直接调用** `SupplierPaymentInvoiceAllocationService.VoidAsync`，绕过引用行所属付款单的
  权威解析与越界非披露拒绝；
- 其余读取入口（台账 / 候选 / 汇总 / 清单 / 详情）已经走 `EnsureMenuAuthorizedAsync` + 授权服务重载，
  但缺陷类本身的「请求形状旁路」口径与 `ERP-468` / `ERP-487` 不一致。

`ERP-468` 已修复「付款单 → 采购订单」的 `SupplierPaymentAllocationController`，而本控制器（ERP-066
「付款单 → 供应商采购发票」）长期保留该旁路。

## 2. 修复后的授权门（与请求形状无关）

- **登记 / 作废**：彻底移除 `RequiresLiveAuthorization()` 与进程内服务直调分支，`Create` / `Void` **无条件**
  先调用 `SupplierPaymentLifecycleRules.EnsureMenuAuthorizedAsync`，再走授权服务重载
  `CreateAuthorizedAsync` / `VoidAuthorizedAsync`（包含被引用付款单的权威解析与越界非披露拒绝）：

  ```csharp
  await SupplierPaymentLifecycleRules.EnsureMenuAuthorizedAsync(_db, CurrentUserId());
  await SupplierPaymentInvoiceAllocationService.CreateAuthorizedAsync(
      _db, dto, CurrentUserId(), CurrentUserName());
  ```

  **未绑定任何请求（`HttpContext` 为 null）时同样 fail closed**：不再存在「无请求管线且无身份」或
  「空请求路径且无身份」的服务直调旁路。

- 控制器**不读取 `Request.Path`**、不读取环境变量、不使用任何测试专用开关、不按数据库提供程序放行，
  也不存在 `[AllowAnonymous]`、角色白名单或「退化为管理员」的兜底（源码契约测试逐条断言）。

- 契约文案与实现同源：`SupplierPaymentLifecycleRules.InvoiceAllocationPathIndependenceText`
  （并与既有 `SupplierPaymentLifecycleRules.PathIndependenceText` 一致）。

## 3. 逐入口先授权、后读写

`api/supplier-payment-invoice-allocations` 的**每一条入口**都在任何计数、候选、快照、金额读取与写入之前完成授权：

| 入口 | 授权调用 | 失败语义 |
| --- | --- | --- |
| `GET`（台账分页） | `EnsureMenuAuthorizedAsync` + 服务层 `ResolveAuthorizedScopeAsync` + `ApplyCustomerScopeAsync` | 未认证 / 权限不足 |
| `GET /payments`（付款单候选） | 同上（范围过滤在计数前） | 未认证 / 权限不足 |
| `GET /payments/{id}/summary`、`/allocations`、`/invoice-candidates` | `EnsureMenuAuthorizedAsync` + `ResolveAuthorizedScopeAsync` + `EnsurePaymentInScopeAsync` | 未认证 / 权限不足 / 不存在 |
| `GET /invoices/{id}/summary`、`/allocations` | `EnsureMenuAuthorizedAsync` + `ResolveAuthorizedScopeAsync` + `EnsureInvoiceInScopeAsync` | 未认证 / 权限不足 / 不存在 |
| `GET /{id}`（详情） | `EnsureMenuAuthorizedAsync` + `ResolveAuthorizedScopeAsync` + 引用行所属付款单范围 | 未认证 / 权限不足 / 不存在 |
| `POST`（登记） | `EnsureMenuAuthorizedAsync` + `CreateAuthorizedAsync`（`ResolveAuthorizedPaymentAsync`） | 未认证 / 权限不足 / 不存在 |
| `POST /{id}/void`（作废） | `EnsureMenuAuthorizedAsync` + `VoidAuthorizedAsync`（引用行所属付款单范围） | 未认证 / 权限不足 / 不存在 |

缺失 / 零 / 未知 / 已删除身份 → `Err:Unauthorized`（2000）；禁用账号、缺少或撤销既有「付款单」
（`payment`）菜单 → `Err:Forbidden`（2002）；受限业务员越界付款单 / 来源 / 发票 → 与「不存在」同一条不披露错误
（`SupplierPaymentLifecycleRules.AllocationNotFoundText`）。**被拒绝的调用零写入**：不新增 / 不改写任何引用行、
付款单、发票、状态或号码记录，且不泄露任何范围外单号、供应商名或金额。

## 4. 保留的既有口径（不改业务语义）

- **既有菜单授权**：仍复用既有「付款单」（`payment`）功能菜单（与 `SeedData.Menus` 同源），**不新增**任何菜单 /
  角色 / 用户授权。
- **权威客户范围**：仍复用 `SalespersonDataScopeService`（ERP-097 唯一权威口径）；付款单归属客户由其关联的
  货款申请单（`FinancePaymentApply.CustomerId`）派生，台账 / 候选在计数与分页之前按范围下推。
- **金额 / 币种 / 共享额度**：`ERP-066` 发票未引用含税总额上限、`ERP-351` / `ERP-382` 付款单唯一同币种共享额度、
  币种精度取整、重复有效行拒绝、作废保留原始金额与历史、显式作废释放额度，全部逐字未改。
- **并发与锁序**：采购发票行 → 来源货款申请单行 → 付款单行 → 引用行的既有锁序、可串行化事务与来源可用性复核
  完全不变。
- **审计与身份**：登记人 / 作废人一律取登录账号（客户端字段不作授权依据）；被拒绝的调用不产生任何审计变更。
- 未新增任何表 / 列 / 索引 / 权限模型，也未触发任何付款、记账、核销或税务动作。

## 5. 测试与验证

### 5.1 单元测试（内存库 + 真实既有身份 / 菜单 / 数据范围）

- `src/ERP.UnitTests/SupplierPaymentInvoiceEntryAuthorizationTests.cs`（新增）：空路径与已赋值路径的**逐入口一致判定**
  （含未认证 / 权限不足文案逐字一致）；缺失 / 零 / 未知 / 已删除身份 → 未认证；禁用账号 / 撤销菜单 → 权限不足；
  受限业务员越界付款单 → 受控非披露拒绝且越权作废不改写证据；授权身份完成既有「登记 → 作废」生命周期；
  被拒调用零写入（引用行 / 付款单 / 发票 / 状态不变）；以及控制器源码契约（不再读取 `Request.Path`、
  无 `RequiresLiveAuthorization` / `AllowAnonymous` / 角色白名单 / 环境变量 / 内存库开关、
  不再存在无授权服务直调）。
- 既有夹具 `src/ERP.UnitTests/SupplierPaymentInvoiceAllocationTests.cs`：新增「无请求上下文与空路径无身份时
  登记作废一律未认证且零写行」用例，覆盖完全未绑定 `HttpContext` 的纯进程内直调；既有身份 / 菜单 / 范围 /
  容量 / 取整用例全部保持，登记人取登录账号的契约继续由带真实身份的夹具覆盖。
- `src/ERP.UnitTests/SupplierPaymentLifecycleTests.cs` 继续以既有特权身份与既有菜单授权驱动两套证据的共享额度
  与生命周期回归，逐字未改。

### 5.2 真实隔离 SQL 测试

`src/ERP.IntegrationTests/SupplierPaymentInvoiceEntryAuthorizationSqlServerTests.cs`（新增）以真实控制器 + 真实既有
身份 / 菜单 / 数据范围驱动：空路径与已赋值路径一致判定、缺失 / 零 / 未知 / 已删除身份在全部 10 条路由未认证、
禁用与撤销菜单在全部路由权限不足、授权身份完成登记 / 作废生命周期、**纯进程内 `HttpContext` 为 null 时登记 /
作废仍 fail closed**、被拒调用零写入且绝不新增授权行。

安全口径：目标必须是专用实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且
`Integrated Security=true`；每次运行只创建**全新 GUID 后缀库**，发现同名库已存在立即拒绝，**绝不 drop / reset /
复用**任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。

## 6. 授权判定边界（显式声明，不隐藏）

- 修复后**任何调用形状**都执行实时授权，包括未绑定任何请求的纯进程内直调；空路径、缺失身份与空上下文
  都不再构成旁路。这是与 `ERP-487` 对收款分摊证据入口采取的一致口径。
- 本任务 `validation_profile = safe`、`completion_mode = build`：交付门槛为 **Release 构建（分析器 / 警告视为错误）+
  全量 `ERP.UnitTests`**；真实 SQL 集成用例为附加证据，构建或单元测试通过不等于整阶段验收。

## 7. 未执行 / 限制

- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置，
  未新增任何表 / 列 / 实体 / 菜单 / 权限 / 连接字符串或密钥。
- 未运行真实 SQL 集成用例（需要显式测试上下文与专用 localdb）；其通过与否以实际执行为准，不得据构建结果声称
  整阶段验收。

## 8. 相关实现

- `src/ERP.Api/Controllers/SupplierPaymentInvoiceAllocationController.cs`：移除 `RequiresLiveAuthorization` 与无授权
  服务直调；登记 / 作废无条件走 `EnsureMenuAuthorizedAsync` + `CreateAuthorizedAsync` / `VoidAuthorizedAsync`。
- `src/ERP.Application/Services/SupplierPaymentLifecycleRules.cs`：新增
  `InvoiceAllocationPathIndependenceText` 与 `UnauthorizedText` / `DeletedIdentityText` / `DisabledIdentityText` /
  `MenuDeniedText` 公共文案常量（`EnsureMenuAuthorizedAsync` 逐字沿用既有文案，判定逻辑不变）。
- 复用既有：`SalespersonDataScopeService`（ERP-097）、`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`
  （角色 → 菜单）、`SupplierPaymentInvoiceAllocationService`、`SupplierPaymentLifecycleRules`
  （`ResolveAuthorizedScopeAsync` / `ResolveAuthorizedPaymentAsync` / `EnsurePaymentInScopeAsync` /
  `EnsureInvoiceInScopeAsync` / `AllocationNotFoundText`）。
