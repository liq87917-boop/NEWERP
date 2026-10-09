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

## 2. 修复后的授权门（与请求路径无关）

`RequiresLiveAuthorization()` 现在只依据「控制器是否绑定到 HTTP 请求管线」：

```csharp
private bool RequiresLiveAuthorization()
    => ControllerContext?.HttpContext is not null;
```

- **空路径与已赋值路径口径完全一致**：不再读取 `Request.Path`，请求形状不再参与授权判定；
- **缺少身份不再放行**：绑定到请求管线但身份缺失 / 为零 / 已删除时一律 fail closed（未认证）；
- 不读取环境变量、不读取数据库提供程序 / 内存库标识、不存在任何测试专用开关；
- 不存在 `[AllowAnonymous]`、角色白名单或「退化为管理员」的兜底（源码契约测试逐条断言）。

`PurchaseInvoiceAuthorizationRules.PathIndependenceText` 固化了同一口径，并与 `RuleText` 一起构成
「与请求形状无关」的可回归契约文案。

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

- `src/ERP.UnitTests/PurchaseInvoiceEntryAuthorizationTests.cs`（新增）：
  空路径与已赋值路径的**逐路由一致判定**（台账 / 详情 / 对账 / 账龄 / 账龄明细 / 候选 / 预览 / 关联 / 新增 / 修改 / 登记 / 作废）；
  缺失 / 零 / 未知 / 已删除身份 → 未认证；禁用 / 撤销菜单 → 权限不足；授权账号在**空路径**请求上完成
  新增 → 关联 → 登记 → 作废 全生命周期；受限业务员对范围外 / 无来源发票 fail closed 且零写入；
  以及源码契约（控制器不含 `Request.Path` / `AllowAnonymous` / 角色白名单 / 环境变量 / 内存库开关）。
- `src/ERP.UnitTests/PurchaseInvoiceConcurrencyTests.cs`（扩展）：新增
  `Empty_request_path_enforces_the_same_authority_as_a_populated_path`（空路径 + 无身份 → 未认证；
  空路径 + 既有授权身份 → 与已赋值路径一致放行），并在接线契约中断言控制器不再读取 `Request.Path`。
- 既有夹具按「真实启用身份 + 既有菜单授权 + 不设置 `Request.Path`」适配：
  `PurchaseOrderInvoiceEvidenceTests`、`SupplierInvoiceReconciliationTests`。
- 既有 52 个 `PurchaseInvoiceTests`（进程内直调用例）与全部既有回归保持绿色。

### 5.2 真实隔离 SQL 测试

`src/ERP.IntegrationTests/PurchaseInvoiceEntryAuthorizationSqlServerTests.cs`（新增）以真实控制器 + 真实既有身份 /
菜单 / 数据范围驱动：空路径与已赋值路径一致判定、缺失 / 零 / 未知 / 已删除身份未认证、禁用与撤销菜单权限不足、
授权账号全生命周期、被拒绝调用零写入与零授权扩张，以及专用目标护栏的 fail-closed 覆盖。

安全口径：目标必须是专用实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且 `Integrated Security=true`；
每次运行只创建**全新 GUID 后缀库**，发现同名库已存在立即拒绝，**绝不 drop / reset / 复用**任何数据库；
连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。

## 6. 授权判定边界

修复后，**任何绑定到 HTTP 请求管线的调用**都执行实时授权：真实 HTTP 请求由 ASP.NET Core 请求管线提供
`ControllerContext.HttpContext`，因此生产与集成环境中的**每一条**入口（空路径与已赋值路径、匿名与已认证）
都强制走身份 / 菜单 / 范围判定，**空路径不再构成旁路**，缺少身份的真实请求一律 fail closed。

唯一不执行实时授权的情形是：控制器**完全未被任何请求绑定**（`HttpContext` 为 null 的纯进程内直接调用）。
这类调用不可能由任何外部请求到达（不存在可被外部构造的「无请求上下文」HTTP 调用），并且该边界**不**读取
请求路径、环境变量、数据库提供程序或任何测试专用开关。既有进程内夹具（例如 `PurchaseInvoiceTests`
直接 `new PurchaseInvoiceController(db)`）属于这一类。

## 7. 残留边界与后续动作（显式声明，不隐藏）

验收条款要求「including no HttpContext」也必须 fail closed。要让**进程内无请求上下文**的直调也一并 fail closed，
必须把既有进程内夹具 `src/ERP.UnitTests/PurchaseInvoiceTests.cs`（52 个直调用例，通过 `BuildController(db) => new(db)`
构造控制器）适配为「真实启用身份 + 既有菜单授权 + 请求上下文」。该文件**不在本任务的 `allowed_paths` 清单内**，
因此本任务**未**改动它（也未为了让其保持绿色而保留任何请求形状 / 身份 / 环境 / 测试专用放行开关），
本任务移除了控制器侧的请求路径与身份维度旁路。

后续动作（建议，需要先授权该路径）：把 `RequiresLiveAuthorization()` 改为无条件启用（直接返回 `true` 或移除该门），
并把 `PurchaseInvoiceTests.BuildController` 适配为注入真实启用身份与既有「采购订单」菜单的请求上下文；
届时「无 HttpContext」的直调与空路径请求同样是 fail closed，本文件的第 6 节边界随之消失。

