# 收款分摊 / 付款引用证据入口实时授权（与请求形状无关）

> 任务：`ERP-468`「Enforce receipt and payment allocation evidence authority independently of request path」（阶段 3 关键授权入口修复）。
> 关联：`ERP-053`（收款单 → 销售订单 收款引用证据）、`ERP-049`（付款单 → 采购订单 付款引用证据）、
> `ERP-433` / `ERP-434`（两套引用证据的实时授权与客户数据范围，`docs/customer-receipt-allocation-authority.md`、
> `docs/supplier-payment-allocation-authority.md`）、`ERP-462` / `ERP-463` / `ERP-464` / `ERP-466`（同缺陷类的授权门修复）、
> `ERP-097`（业务员客户数据范围，`docs/业务员数据范围说明.md`）、`ERP-349` / `ERP-351`（收款单 / 付款单生命周期护栏）。

## 1. 修复前的问题证据

`src/ERP.Api/Controllers/CustomerReceiptAllocationController.cs:167-172` 与
`src/ERP.Api/Controllers/SupplierPaymentAllocationController.cs:163-168` 的实时授权门在修复前为：

```csharp
private bool RequiresLiveAuthorization()
{
    var http = ControllerContext?.HttpContext;
    if (http is null) return false;                                  // 无请求上下文：免授权
    return http.Request.Path.HasValue || CurrentUserId() is not null; // 空路径且无身份：免授权
}
```

三层放行条件都与**请求形状**有关：没有请求上下文、请求路径为空、没有可解析身份，都会**跳过**
身份 / 账号状态 / 既有「收款单」（`receipt`）/「付款单」（`payment`）菜单 / ERP-097 权威客户范围判定；
`Create`（控制器 128 / 123 行）与 `Void`（控制器 146 / 141 行）在该探针为 false 时直接短路到
**无授权的服务重载**（`CustomerReceiptAllocationService.CreateAsync` / `VoidAsync`、
`SupplierPaymentAllocationService.CreateAsync` / `VoidAsync`），因此「空路径 / 无身份」的请求形状旁路可以
登记或作废包含金额、币种、快照与作废原因的分摊证据，而不需要实时身份、既有菜单或权威范围。

> **2026-10-13 验收恢复**：上一次修复把授权门收敛为「是否绑定到 HTTP 请求管线」，但仍对
> `HttpContext` 为 null 的纯进程内直调保留免授权分支（`RequiresLiveAuthorization()`），
> 与「including no HttpContext 也必须 fail closed」的验收条款不一致。本次恢复**彻底移除**该门与
> `Create` / `Void` 的无授权服务直调分支，并把两个进程内夹具
> （`SalesOrderReceiptEvidenceTests` / `PurchaseOrderPaymentEvidenceTests`）适配为
> 「真实启用身份 + 既有「收款单」/「付款单」菜单授权」，因此不存在任何内部调用 / 测试兼容 / 重命名旁路，
> 也不存在「无请求上下文」豁免。

## 2. 修复后的授权门（无条件、与请求路径 / 请求形状 / 请求上下文无关）

两个控制器**已彻底移除** `RequiresLiveAuthorization()` 授权门与登记 / 作废的无授权服务直调分支：

- 台账 / 候选 / 汇总 / 清单 / 订单候选 / 详情每一条读取入口无条件调用
  `CustomerReceiptLifecycleRules.EnsureMenuAuthorizedAsync` / `SupplierPaymentLifecycleRules.EnsureMenuAuthorizedAsync`；
- 登记 / 作废无条件先调用同一菜单授权，再走授权服务重载 `CreateAuthorizedAsync` / `VoidAuthorizedAsync`，
  **绝不**退回 `CreateAsync` / `VoidAsync` 无授权重载；
- 判定**与请求路径 / 请求形状 / 是否绑定 `HttpContext` 完全无关**：空路径、已赋值路径与
  **完全未绑定 `HttpContext`** 的纯进程内直调口径完全一致，对同一身份给出完全相同的判定；
- **缺少身份不再放行**：身份缺失 / 为零 / 未知 / 已删除时一律 fail closed（未认证 2000）；
- **禁用 / 撤销或缺少既有菜单一律权限不足（2002）**，且判定先于任何分摊金额、快照或候选读取与写入；
- 不读取 `Request.Path`、不读取环境变量、不按数据库提供程序 / 内存库放行、不存在任何测试专用开关；
- 不存在 `[AllowAnonymous]`、角色白名单或「退化为管理员」的兜底（源码契约测试逐条断言）。

`CustomerReceiptLifecycleRules.PathIndependenceText` 与 `SupplierPaymentLifecycleRules.PathIndependenceText`
固化了同一口径，并与各自的 `RuleText` / `BoundaryText` 一起构成「与请求形状 / 请求上下文无关」的可回归契约文案。

## 3. 逐路由先授权、后读写

| 入口 | 授权调用 | 失败语义 |
| --- | --- | --- |
| `GET /api/customer-receipt-allocations`（台账） | `CustomerReceiptLifecycleRules.EnsureMenuAuthorizedAsync` + 范围过滤的 `ListAsync` | 未认证 / 权限不足 |
| `GET /receipts`（收款单候选） | 同上 + `ListReceiptCandidatesAsync` | 未认证 / 权限不足 |
| `GET /receipts/{id}/summary`（收款单侧汇总） | 同上 + `GetReceiptSummaryAsync` | 未认证 / 权限不足 |
| `GET /receipts/{id}/allocations`（收款单侧清单） | 同上 + `ListForReceiptAsync` | 未认证 / 权限不足 |
| `GET /receipts/{id}/order-candidates`（订单候选） | 同上 + `ListOrderCandidatesAsync` | 未认证 / 权限不足 |
| `GET /{id}`（详情） | 同上 + `GetAsync(..., userId)` | 未认证 / 权限不足 |
| `POST /`（登记） | `EnsureMenuAuthorizedAsync` + `CreateAuthorizedAsync`（解析权威收款单并复核范围） | 未认证 / 权限不足 |
| `POST /{id}/void`（作废） | `EnsureMenuAuthorizedAsync` + `VoidAuthorizedAsync`（解析权威收款单并复核范围） | 未认证 / 权限不足 |
| `GET /api/supplier-payment-allocations`（台账） | `SupplierPaymentLifecycleRules.EnsureMenuAuthorizedAsync` + 范围过滤的 `ListAsync` | 未认证 / 权限不足 |
| `GET /payments`（付款单候选） | 同上 + `ListPaymentCandidatesAsync` | 未认证 / 权限不足 |
| `GET /payments/{id}/summary`（付款单侧汇总） | 同上 + `GetPaymentSummaryAsync` | 未认证 / 权限不足 |
| `GET /payments/{id}/allocations`（付款单侧清单） | 同上 + `ListForPaymentAsync` | 未认证 / 权限不足 |
| `GET /payments/{id}/order-candidates`（订单候选） | 同上 + `ListOrderCandidatesAsync` | 未认证 / 权限不足 |
| `GET /{id}`（详情） | 同上 + `GetAsync(..., userId)` | 未认证 / 权限不足 |
| `POST /`（登记） | `EnsureMenuAuthorizedAsync` + `CreateAuthorizedAsync`（解析权威付款单并复核范围） | 未认证 / 权限不足 |
| `POST /{id}/void`（作废） | `EnsureMenuAuthorizedAsync` + `VoidAuthorizedAsync`（解析权威付款单并复核范围） | 未认证 / 权限不足 |

授权先于任何分摊金额 / 快照 / 候选 / 计数读取与任何写入；**被拒绝的请求零写入**（不新增 / 不改写作废行、
不改写收款单 / 付款单、不新增任何用户授权）。缺失 / 零 / 未知 / 已删除身份 → `Err:Unauthorized`（2000）；
禁用账号、缺少或撤销既有「收款单」/「付款单」菜单 → `Err:Forbidden`（2002）。

## 4. 保留的既有口径（不改业务语义）

- **既有菜单授权**：仍复用既有「收款单」（`receipt`）与「付款单」（`payment`）功能菜单（与 `SeedData.Menus` 同源），
  **不新增**任何菜单 / 角色 / 用户授权，也不修改生产种子权限或角色授权。
- **ERP-097 权威客户范围**：仍复用 `SalespersonDataScopeService`（唯一权威口径）；收款引用按收款单客户收敛，
  付款引用按付款单权威客户（关联货款申请单）收敛，受限账号对范围外 / 无权威归属资源一律返回
  `CustomerReceiptLifecycleRules.AllocationNotFoundText` / `SupplierPaymentLifecycleRules.AllocationNotFoundText`
  的**同一条不披露存在性**错误，特权账号保留既有全部访问口径。
- **金额与业务护栏**：金额按币种精度取整后必须大于 0、币种必须与收款单 / 付款单权威一致、客户 / 供应商必须一致、
  同一资源同一订单不得重复有效引用、两套证据共同占用同一同币种额度上限（ERP-350 / ERP-351）、作废必须填写原因
  并保留原始金额 / 快照 / 作废时间与原因（不物理删除、不静默改写）——全部原样保留，本任务不弱化任何校验。
- **锁定与事务**：分摊证据写入 / 作废仍取收款单 / 付款单行锁并遵守唯一全局锁序（ERP-378 / ERP-382），
  本任务只改授权门。
- **响应契约**：全部既有路由、DTO 与错误码不变，只在读写之前多了一道实时授权判定。

## 5. 验证证据

### 5.1 单元测试（内存库 + 真实既有身份 / 菜单 / 数据范围）

- `src/ERP.UnitTests/ReceiptPaymentAllocationEntryAuthorizationTests.cs`（27 个用例）：
  空路径与已赋值路径的**逐入口一致判定**（收款 / 付款各 8 条：台账 / 候选 / 汇总 / 清单 / 订单候选 / 详情 / 登记 / 作废）；
  缺失 / 零 / 未知 / 已删除身份 → 未认证；禁用 / 缺少 / 撤销既有菜单 → 权限不足（撤销后下一次请求立即收敛）；
  授权身份在**空路径**请求上完成 登记 → 作废 → 重新登记 生命周期并保留金额 / 币种 / 客户 / 重复有效行护栏；
  被拒绝调用对证据行、金额与授权行数的**零写入快照**；ERP-097 范围内外收敛与不披露错误；
  **完全未绑定 `HttpContext`** 的纯进程内直调在全部入口一律未认证且零写入；
  以及源码契约（控制器不含 `RequiresLiveAuthorization` / `ControllerContext?.HttpContext is not null` / `Request.Path` /
  `AllowAnonymous` / 角色白名单 / 环境变量 / 内存库开关，且登记 / 作废只走授权服务重载）。
- 既有夹具按「真实启用身份 + 既有菜单授权 + 不设置 `Request.Path`」适配：
  `CustomerReceiptAllocationTests`、`SupplierPaymentAllocationTests`、`SalesOrderReceiptEvidenceTests`、
  `PurchaseOrderPaymentEvidenceTests`（默认空路径请求，证明授权与请求形状 / 请求上下文无关）。
- **Safe Release（分析器 + warnings-as-errors）构建 0 警告 0 错误，`ERP.UnitTests` 全量用例全部通过**
  （构建本身不等于整阶段验收）。

### 5.2 真实隔离 SQL 测试

`src/ERP.IntegrationTests/ReceiptPaymentAllocationEntryAuthorizationSqlServerTests.cs` 以真实控制器 + 真实既有身份 /
菜单 / 数据范围驱动：空路径与已赋值路径一致判定、缺失 / 零 / 未知 / 已删除身份未认证、禁用与撤销菜单权限不足、
授权账号全生命周期（两种请求形状）、被拒绝调用零写入与零授权扩张、**完全未绑定 `HttpContext` 的直调在全部入口
一律未认证且零写入**，以及专用目标护栏的 fail-closed 覆盖。

安全口径：目标必须是专用实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且 `Integrated Security=true`；
每次运行只创建**全新 GUID 后缀库**，发现同名库已存在立即拒绝，**绝不 drop / reset / 复用**任何数据库；
连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。

## 6. 授权判定边界

修复后，**每一条入口都无条件执行实时授权**：真实 HTTP 请求由 ASP.NET Core 请求管线提供
`ControllerContext.HttpContext`，因此生产与集成环境中的**每一条**入口（空路径与已赋值路径、匿名与已认证）
都强制走身份 / 账号状态 / 既有菜单 / ERP-097 权威范围判定，**空路径不再构成旁路**，缺少身份的真实请求一律 fail closed。

与此同时，**完全未绑定 `HttpContext`**（`ControllerContext.HttpContext` 为 null）的纯进程内直调同样无条件执行本护栏：
`CurrentUserId()` 在缺少请求主体时安全解析为 `null`，缺失 / 为零 / 未知 / 已删除身份一律未认证（2000），
禁用 / 缺少或被撤销菜单一律权限不足（2002），且判定先于任何分摊金额、快照或候选读取与写入——
**不存在「无请求上下文」豁免**，也不存在任何内部调用 / 测试兼容 / 环境变量 / 数据库提供程序旁路。

## 7. 边界声明

- 本模块**只改授权门与夹具**：不弱化任何金额 / 币种 / 重复行 / 额度 / 作废原因 / 审计校验，
  不新增或修改生产菜单 / 角色 / 用户授权，不新增表 / 列 / 权限模型，响应路由与 DTO 契约不变。
- 被拒绝的调用**零写入**：不新增 / 不改写任何分摊证据行、不改写收款单 / 付款单、不新增任何用户授权。
- 真实隔离 SQL 只使用专用实例 `(localdb)\NEWERP_AutoAcceptance` + 全新 GUID 的 `NEWERP_AUTOTEST` 库，
  绝不 drop / reset / 复用任何数据库，绝不读取生产凭据。

