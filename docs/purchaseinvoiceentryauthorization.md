# 供应商采购发票证据入口实时授权（与请求形状无关）

> 任务：`ERP-462`「Enforce supplier invoice evidence authorization independently of request path」（阶段 3 关键授权入口修复）。
> 关联：`ERP-382`（供应商采购发票并发与授权护栏，`docs/purchase-invoice-concurrency.md`）、
> `ERP-433`（供应商付款引用证据实时授权，`docs/supplier-payment-allocation-authority.md`）、
> `ERP-461`（只读商品工作台实时授权，`docs/product-read-workspace-authority.md`）、
> `ERP-097`（业务员客户数据范围，`docs/业务员数据范围说明.md`）。

## 1. 修复前的问题证据

`src/ERP.Api/Controllers/PurchaseInvoiceController.cs` 的实时授权门在修复前为：

```csharp
private bool RequiresLiveAuthorization()
{
    var http = ControllerContext?.HttpContext;
    if (http is null) return false;                                  // 无请求上下文：免授权
    return http.Request.Path.HasValue || CurrentUserId() is not null; // 空路径且无身份：免授权
}
```

三层放行条件都与**请求形状**有关：没有请求上下文、请求路径为空、没有可解析身份，都会**跳过**
身份 / 账号状态 / 既有「采购订单」菜单 / 权威来源客户范围判定。因此台账、详情、对账、账龄明细、候选订单、
关联预览、新增、修改、关联整体替换、登记、作废这 12 条入口在「空路径 / 无身份」的调用上都可能执行，
供应商采购发票证据路由缺少实时的身份 / 菜单 / 范围强制。

**2026-10-13 复核（recovery）追加证据**：首轮修复把授权门收窄为
`ControllerContext?.HttpContext is not null`，虽然移除了 `Request.Path` 与身份维度旁路，但对
**`HttpContext` 为 `null` 的调用**（进程内直调）仍然整段跳过菜单、范围内查询与发票 / 来源权威判定，
与验收条款「including no HttpContext 也必须 fail closed」不符。本次修复**彻底移除**该门。

## 2. 修复后的授权门（与请求形状 / 请求上下文 / 身份无关）

`RequiresLiveAuthorization()` **已彻底移除**：控制器不再存在任何请求形状 / 请求上下文 / 身份维度的授权门，
每一条路由入口都**无条件**执行实时授权；身份解析保持安全（缺失请求上下文 / 缺失身份声明 / 非数字一律解析为 `null`）：

```csharp
private long? CurrentUserId()
    => long.TryParse(
        ControllerContext?.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id)
        ? id
        : null;
```

- **空路径与已赋值路径口径完全一致**：不再读取 `Request.Path`，请求形状不再参与授权判定；
- **无请求上下文不再放行**：`HttpContext` 为 `null` 的纯进程内直调与真实请求口径一致，缺少身份时由既有规则
  在触达任何查询之前返回受控未认证（fail closed）；
- **缺少身份不再放行**：缺失 / 零 / 已删除身份一律 fail closed（未认证），禁用 / 撤销菜单 / 无菜单一律权限不足；
- 不读取环境变量、不读取数据库提供程序 / 内存库标识、不存在任何测试专用开关；
- 不存在 `[AllowAnonymous]`、角色白名单或「退化为管理员」的兜底（源码契约测试逐条断言）。

`PurchaseInvoiceAuthorizationRules.PathIndependenceText` 固化了同一口径，并与 `RuleText` 一起构成
「与请求形状 / 请求上下文无关」的可回归契约文案。

## 3. 逐路由先授权、后读写

| 入口 | 授权调用 | 失败语义 |
| --- | --- | --- |
| `GET /api/purchase-invoices`（台账） | `BuildScopePredicateAsync`（身份 / 菜单 / 范围下推到计数与分页之前） | 未认证 / 权限不足 |
| `GET /{id}`（详情） | `EnsureInvoiceIdAuthorizedAsync` → `EnsureInvoiceAuthorizedAsync` | 未认证 / 权限不足 |
| `GET /reconciliation`、`GET /reconciliation-aging` | `EnsureMenuAuthorizedAsync` | 未认证 / 权限不足 |
| `GET /reconciliation-aging/invoices/{invoiceId}` | `EnsureInvoiceIdAuthorizedAsync` | 未认证 / 权限不足 |
| `GET /{id}/order-candidates` | `EnsureInvoiceIdAuthorizedAsync` | 未认证 / 权限不足 |
| `POST /{id}/allocations/preview` | `EnsureInvoiceIdAuthorizedAsync` + `EnsureProposedAllocationsAuthorizedAsync` | 未认证 / 权限不足 |
| `POST /{id}/allocations`（关联整体替换） | 同上（已存储来源与拟提议来源两侧都复核） | 未认证 / 权限不足 |
| `POST /`（新增） | `EnsureMenuAuthorizedAsync` | 未认证 / 权限不足 |
| `PUT /{id}`（修改） | `EnsureInvoiceIdAuthorizedAsync` | 未认证 / 权限不足 |
| `POST /{id}/record`（登记） | `EnsureInvoiceIdAuthorizedAsync` | 未认证 / 权限不足 |
| `POST /{id}/void`（作废） | `EnsureInvoiceIdAuthorizedAsync` | 未认证 / 权限不足 |

授权先于任何明细 / 金额 / 计数读取与任何写入；**被拒绝的请求零写入**（不新增 / 不改写发票、关联行、
采购订单、付款证据，也不新增任何用户授权）。缺失 / 零 / 已删除身份 → `Err:Unauthorized`（2000）；
禁用账号、撤销既有菜单、缺少既有菜单 → `Err:Forbidden`（2002）。

## 4. 保留的既有口径（不改业务语义）

- **既有菜单授权**：仍复用既有「采购订单」（`purchase-order`）功能菜单（与 `SeedData.Menus` 同源），
  **不新增**任何菜单 / 角色 / 用户授权，也不修改生产种子权限；特权账号（超级管理员 / 系统内置角色 / 显式特权角色）
  仍须持有该既有菜单。
- **权威客户范围**：仍复用 `SalespersonDataScopeService`（ERP-097 唯一权威口径）。发票归属只认**已持久化的
  「发票 → 采购订单」关联行**；受限账号对「无来源采购订单」或「来源订单归属客户在本人范围外」的发票一律 fail closed，
  特权账号保留历史无来源发票的可读 / 可操作能力。
- **并发与事务**：`PurchaseInvoiceService` 的共享发票 / 付款容量事务锁、唯一全局锁序（来源采购订单行 → 供应商采购发票行 →
  付款单行）与 ERP-351 共用付款额度完全不变，本任务只改授权门。
- **金额 / 号码 / 审计**：币种精度取整、金额等式（含税 = 不含税 + 税额）、重复身份、到期日 / 付款条件、作废原因与冻结审计、
  登记前关联复核、非变更边界（不改写采购订单 / 库存 / 退税 / 供应商余额与付款状态）全部保持不变。

## 5. 验证证据

### 5.1 单元测试（内存库 + 真实既有身份 / 菜单 / 数据范围）

- `src/ERP.UnitTests/PurchaseInvoiceEntryAuthorizationTests.cs`（新增 / 扩展）：
  空路径与已赋值路径的**逐路由一致判定**（台账 / 详情 / 对账 / 账龄 / 账龄明细 / 候选 / 预览 / 关联 / 新增 / 修改 / 登记 / 作废）；
  缺失 / 零 / 未知 / 已删除身份 → 未认证；禁用 / 撤销菜单 → 权限不足；授权账号在**空路径**请求上完成
  新增 → 关联 → 登记 → 作废 全生命周期；受限业务员对范围外 / 无来源发票 fail closed 且零写入；
  新增 `Missing_request_context_is_unauthorized_on_every_route`（**完全无 `HttpContext`** 的进程内直调在 12 条入口上
  一律未认证且零写入）；以及源码契约（控制器不含 `RequiresLiveAuthorization` / `Request.Path` / `AllowAnonymous` /
  角色白名单 / 环境变量 / 内存库开关，并安全解析缺失上下文身份）。
- `src/ERP.UnitTests/PurchaseInvoiceConcurrencyTests.cs`（扩展）：新增
  `Empty_request_path_enforces_the_same_authority_as_a_populated_path`（空路径 + 无身份 → 未认证；
  空路径 + 既有授权身份 → 与已赋值路径一致放行）与 `Missing_request_context_enforces_authority_on_every_route`
  （**完全无 `HttpContext`** → 未认证且零写入），并在接线契约中断言控制器不再读取 `Request.Path`、
  不再存在 `RequiresLiveAuthorization` / `ControllerContext?.HttpContext is not null` 门。
- 既有夹具按「真实启用身份 + 既有菜单授权」适配：`PurchaseOrderInvoiceEvidenceTests`、
  `SupplierInvoiceReconciliationTests`；`PurchaseInvoiceTests` 的 52 个进程内直调用例改为
  `PurchaseInvoiceTestIdentities.ForUser(db, SeedAuthorizedUser(db, privileged: true))`（真实启用身份 +
  既有「采购订单」菜单授权，隔离内存测试数据内播种，不设置 `Request.Path`）后保持绿色。

### 5.2 真实隔离 SQL 测试

`src/ERP.IntegrationTests/PurchaseInvoiceEntryAuthorizationSqlServerTests.cs`（新增 / 扩展）以真实控制器 + 真实既有身份 /
菜单 / 数据范围驱动：空路径与已赋值路径一致判定、缺失 / 零 / 未知 / 已删除身份未认证、禁用与撤销菜单权限不足、
授权账号全生命周期、被拒绝调用零写入与零授权扩张、
新增 `Missing_request_context_is_unauthorized_on_every_route`（**完全无 `HttpContext`** 的 12 条入口一律未认证且零写入），
以及专用目标护栏的 fail-closed 覆盖。

`src/ERP.IntegrationTests/PurchaseInvoiceConcurrencySqlServerTests.cs`（扩展）新增
`Missing_request_context_denies_every_write_route_without_mutation`：**完全无 `HttpContext`** 时
台账 / 详情 / 关联预览 / 关联替换 / 登记 / 作废 / 新增一律未认证，且发票状态与关联行零改动
（原有双连接容量竞态与全局锁序验证完全保留）。

安全口径：目标必须是专用实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且 `Integrated Security=true`；
每次运行只创建**全新 GUID 后缀库**，发现同名库已存在立即拒绝，**绝不 drop / reset / 复用**任何数据库；
连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。

## 6. 授权判定边界

修复后，**每一个控制器入口无条件**执行实时授权：真实 HTTP 请求由 ASP.NET Core 请求管线提供
`ControllerContext.HttpContext`，因此生产与集成环境中的**每一条**入口（空路径与已赋值路径、匿名与已认证）
都强制走身份 / 菜单 / 范围判定，**空路径不再构成旁路**；而**完全没有请求上下文**（`HttpContext` 为 `null`
的纯进程内直调）的调用同样先解析出 `null` 身份并 fail closed——缺少身份一律受控未认证。

**不存在任何免授权边界**：控制器不读取请求路径、环境变量、数据库提供程序或任何测试专用开关，
不会因为「无请求上下文 / 空身份」而执行任何读取或写入。

## 7. 残留边界（已关闭）

首轮修复曾在本文档第 7 节显式声明「`HttpContext` 为 `null` 的进程内直调不执行实时授权」这一残留边界。
本次修复把 `RequiresLiveAuthorization()` 与所有「无上下文 / 空身份」豁免**完全移除**，并把既有进程内夹具
`src/ERP.UnitTests/PurchaseInvoiceTests.cs`（52 个直调用例）适配为注入真实启用身份与既有「采购订单」菜单授权的
请求上下文，因此：

- 「无 `HttpContext`」的直调与空路径请求**同样 fail closed**（缺少身份 → 受控未认证），第 6 节描述的免授权边界**已消失**；
- 不存在任何请求形状 / 请求上下文 / 身份 / 环境变量 / 数据库提供程序 / 测试专用开关维度的放行；
- 不新增任何菜单 / 角色 / 用户授权，不修改生产种子权限，不改写任何发票 / 关联行 / 采购订单 / 付款证据。

