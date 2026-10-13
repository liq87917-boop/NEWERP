# 客户销项发票 / 收款分摊证据入口实时授权（与请求形状无关）

> 任务：`ERP-487`「Enforce customer invoice and receipt invoice-allocation authority at every entry」。
> 关联：`ERP-383`（销项发票实时授权与并发护栏，`docs/customer-sales-invoice-concurrency.md`）、
> `ERP-434`（销项发票入口授权与客户数据范围）、`ERP-468`（同缺陷类修复：收款单 / 付款单分摊证据入口，
> `docs/receiptpaymentallocationentryauthorization.md`）、`ERP-486`（同缺陷类修复：供应商比价入口）、
> `ERP-073`（收款单 → 销项发票证据）、`ERP-349` / `ERP-350` / `ERP-378`（收款单生命周期与共享额度）、
> `ERP-097`（业务员客户数据范围）。

## 1. 修复前的问题证据

`src/ERP.Api/Controllers/CustomerSalesInvoiceEvidenceController.cs` 与
`src/ERP.Api/Controllers/CustomerSalesInvoiceCollectionAllocationController.cs` 的实时授权门在修复前只依据**请求形状**：

```csharp
private bool RequiresLiveAuthorization()
{
    var http = ControllerContext?.HttpContext;
    if (http is null) return false;                                   // 无请求上下文：免授权
    return http.Request.Path.HasValue || CurrentUserId() is not null; // 空路径且无身份：免授权
}
```

因此：

- 客户销项发票的 `BuildScopePredicateAsync` / `BuildTradeDocumentScopePredicateAsync` /
  `EnsureInvoiceIdAuthorizedAsync` / `EnsureProposedEvidenceScopeAuthorizedAsync` /
  `EnsureProposedAllocationsAuthorizedAsync` 在**缺失 `HttpContext`** 或**空 `Request.Path` 且无身份**时返回 `null`
  或直接返回，跳过身份 / 既有「销售订单」（`sales-order`）菜单 / 权威客户与来源范围判定；
- 收款分摊证据的 `Create` / `Void` 在同样条件下**直接调用无授权的服务方法**
  （`CustomerSalesInvoiceCollectionAllocationService.CreateAsync` / `VoidAsync`），
  `ERP-073` 的收款单 / 发票 / 客户同一条不披露拒绝被整段绕过。

## 2. 修复后的授权门（与请求形状无关）

- **收款分摊证据**：彻底移除 `RequiresLiveAuthorization()` 与进程内服务直调分支，登记 / 作废**无条件**先调用
  `CustomerReceiptLifecycleRules.EnsureMenuAuthorizedAsync`，再走授权服务重载
  `CreateAuthorizedAsync` / `VoidAuthorizedAsync`（包含被引用收款单的权威解析与越界非披露拒绝）：

  ```csharp
  await CustomerReceiptLifecycleRules.EnsureMenuAuthorizedAsync(_db, CurrentUserId());
  await CustomerSalesInvoiceCollectionAllocationService.CreateAuthorizedAsync(
      _db, dto, CurrentUserName(), CurrentUserId());
  ```

  **未绑定任何请求（`HttpContext` 为 null）时同样 fail closed**：不再存在「无请求管线且无身份」的服务直调旁路。

- **客户销项发票**：授权门只依据「控制器是否绑定到 HTTP 请求管线」
  （`ControllerContext?.HttpContext is not null`），**不再读取请求路径字段，也不再依据是否携带可解析身份**：

  ```csharp
  private bool RequiresLiveAuthorization()
      => ControllerContext?.HttpContext is not null;
  ```

  空路径与已赋值路径对同一身份给出完全相同的判定；绑定到请求管线但身份缺失 / 为零 / 未知 / 已删除一律未认证
  （2000），禁用账号 / 缺少或被撤销既有「销售订单」菜单一律权限不足（2002），受限账号越界客户 / 来源一律受控
  非披露拒绝，且全部先于任何计数、候选、快照、预览与写入。

- 两个控制器都**不读取 `Request.Path`**、不读取环境变量、不使用任何测试专用开关、不按数据库提供程序放行，
  也不存在 `[AllowAnonymous]`、角色白名单或「退化为管理员」的兜底（源码契约测试逐条断言）。

契约文案与实现同源：
`CustomerSalesInvoiceAuthorizationRules.PathIndependenceText`、
`CustomerReceiptLifecycleRules.CollectionAllocationPathIndependenceText`（并与既有
`CustomerReceiptLifecycleRules.PathIndependenceText` 一致）。

## 3. 逐入口先授权、后读写

### 3.1 客户销项发票（`api/customer-sales-invoices`）

| 入口 | 授权调用 | 失败语义 |
| --- | --- | --- |
| `GET`（台账分页） | `BuildScopePredicateAsync`（范围**先于**计数 / 分页下推数据库） | 未认证 / 权限不足 |
| `GET /collection-timing` | `ResolveAuthorizedScopeAsync` → `CustomerInvoiceCollectionTimingService` 客户范围 | 未认证 / 权限不足 |
| `GET /commercial-invoice-candidates` | `BuildTradeDocumentScopePredicateAsync`（无权威归属不进入候选） | 未认证 / 权限不足 |
| `GET /{id}`、`GET /{id}/order-candidates` | `EnsureInvoiceIdAuthorizedAsync`（已存储客户 + 全部来源订单 + 交叉引用单证归属） | 未认证 / 权限不足 / 不存在 |
| `POST`（新增）、`PUT /{id}`（修改） | `EnsureProposedEvidenceScopeAuthorizedAsync`（+ 已存储发票先复核） | 未认证 / 权限不足 |
| `POST /{id}/allocations/preview`、`POST /{id}/allocations` | `EnsureInvoiceIdAuthorizedAsync` + `EnsureProposedAllocationsAuthorizedAsync` | 未认证 / 权限不足 |
| `POST /{id}/record`、`POST /{id}/void` | `EnsureInvoiceIdAuthorizedAsync`（先于状态变更与任何写入） | 未认证 / 权限不足 / 不存在 |

### 3.2 收款分摊证据（`api/customer-sales-invoice-collection-allocations`）

台账 / 元数据 / 收款单候选 / 发票候选 / 收款单侧汇总 / 收款单侧清单 / 发票侧汇总 / 发票侧清单 / 详情
**每一条入口**都先调用 `CustomerReceiptLifecycleRules.EnsureMenuAuthorizedAsync`；登记 / 作废在同一授权门下
再走 `CreateAuthorizedAsync` / `VoidAuthorizedAsync`。

缺失 / 零 / 未知 / 已删除身份 → `Err:Unauthorized`（2000）；禁用账号、缺少或撤销既有「收款单」
（`receipt`）/「销售订单」（`sales-order`）菜单 → `Err:Forbidden`（2002）；受限业务员越界收款单 / 发票 /
客户 → 与「不存在」同一条不披露错误。**被拒绝的调用零写入**：不新增 / 不改写任何发票、分摊行、收款单、
销售订单、单证或状态，且不泄露任何范围外单据号、客户名或金额。

## 4. 保留的既有口径（不改业务语义）

- **既有菜单授权**：仍复用既有「销售订单」（`sales-order`）与「收款单」（`receipt`）功能菜单（与
  `SeedData.Menus` 同源），**不新增**任何菜单 / 角色 / 用户授权。
- **权威客户范围**：仍复用 `SalespersonDataScopeService`（ERP-097 唯一权威口径）；发票归属同时覆盖已存储客户、
  全部已存储销售订单分摊来源与显式交叉引用单证的归属客户（混源要求全部允许），台账范围在计数 / 分页之前下推。
- **金额 / 币种 / 共享额度**：`ERP-350` 收款单唯一同币种额度、`ERP-073` 发票含税总额上限、币种精度取整、
  重复有效行拒绝、作废保留原始金额与历史、显式作废释放额度，全部逐字未改。
- **并发与锁序**：发票行 → 收款单行 → 分摊行的既有锁序、可串行化事务、来源可用性复核与审计留痕完全不变。
- **审计与身份**：登记人 / 作废人一律取登录账号（客户端字段不作授权依据）；被拒绝的调用不产生任何审计变更。
- 未新增任何表 / 列 / 索引 / 权限模型，也未触发任何记账、开票、核销或税务动作。

## 5. 测试与验证

### 5.1 单元测试（内存库 + 真实既有身份 / 菜单 / 数据范围）

- `src/ERP.UnitTests/CustomerSalesInvoiceEntryAuthorizationTests.cs`（新增）：
  空路径与已赋值路径的**逐入口一致判定**（含未认证文案逐字一致）；缺失 / 零 / 未知 / 已删除身份 → 未认证；
  禁用账号 / 撤销菜单 → 权限不足且撤销后立即收敛；受限业务员越界客户 / 收款单 / 销售订单 → 受控非披露拒绝；
  授权身份完成既有「新建草稿 → 分摊 → 登记 → 作废」与「收款分摊 登记 → 作废」生命周期；
  被拒调用零写入（发票状态 / 金额 / 行数 / 分摊行数不变）；以及控制器源码契约
  （不再读取 `Request.Path`、无 `AllowAnonymous` / 角色白名单 / 环境变量 / 内存库开关；
  收款分摊控制器不再存在 `RequiresLiveAuthorization` 与无授权服务直调）。
- 既有夹具 `src/ERP.UnitTests/CustomerSalesInvoiceCollectionAllocationTests.cs` 调整为真实授权口径：
  「无身份时登记人记未知用户」改为**受控未认证拒绝且零写行**（登记人取登录账号的契约仍由带真实身份的
  夹具 `BuildController(db, "张三")` 覆盖），不新增任何测试授权。
- `CustomerSalesInvoiceEvidenceTests` / `CustomerSalesInvoiceConcurrencyTests` /
  `CustomerSalesInvoiceReceiptAllocationExposureTests` 继续以既有特权身份与既有菜单授权驱动全部路由。

### 5.2 真实隔离 SQL 测试

`src/ERP.IntegrationTests/CustomerSalesInvoiceEntryAuthorizationSqlServerTests.cs`（新增）以真实控制器 + 真实既有
身份 / 菜单 / 数据范围驱动：空路径与已赋值路径一致判定、缺失 / 零 / 未知 / 已删除身份在全部路由未认证、
禁用与撤销菜单在全部路由权限不足、授权身份完成读侧与写入生命周期、**纯进程内 `HttpContext` 为 null 时收款分摊
登记 / 作废仍 fail closed**、越界收款单与「不存在」同一条不披露错误、被拒调用零写入且绝不新增授权行。

安全口径：目标必须是专用实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且
`Integrated Security=true`；每次运行只创建**全新 GUID 后缀库**，发现同名库已存在立即拒绝，**绝不 drop / reset /
复用**任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。

## 6. 授权判定边界（显式声明，不隐藏）

- **收款分摊证据**：修复后**任何调用形状**都执行实时授权，包括未绑定任何请求的纯进程内直调，
  空上下文不再构成旁路。
- **客户销项发票**：绑定到 HTTP 请求管线的调用（生产与集成环境的每一条外部入口，无论空路径 / 已赋值路径、
  匿名 / 已认证）都执行实时身份 / 账号状态 / 既有菜单 / 权威来源范围判定；
  仅**未绑定任何请求上下文**（`ControllerContext.HttpContext` 为 null，外部请求无法构造）的纯进程内直调
  沿用仓库一致的进程内边界。
  保留该边界的原因：既有非本任务授权路径之外的进程内夹具
  `src/ERP.UnitTests/SalesOrderInvoiceEvidenceTests.cs` 直接实例化控制器并调用
  `Create` / `SaveAllocations` / `Record` / `Void`（无任何请求上下文），而该文件不在本任务的 `allowed_paths` 内，
  无法按「播种真实身份」的方式改造；因此本次不删除该边界以免发布门（全量 `ERP.UnitTests`）失败。
  该边界不读取请求路径 / 环境变量 / 测试开关，也不能由任何外部请求到达。

## 7. 未执行 / 限制

- 本任务 `validation_profile = safe`、`completion_mode = build`：交付门槛为 **Release 构建（分析器 / 警告视为错误）+
  全量 `ERP.UnitTests`**；真实 SQL 集成用例为附加证据，构建或单元测试通过不等于整阶段验收。
- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置，
  未新增任何表 / 列 / 实体 / 菜单 / 权限 / 连接字符串或密钥。
- 客户销项发票入口保留上述「未绑定请求上下文的纯进程内直调边界」，与 `ERP-468` 对收款单 / 付款单分摊证据入口
  采取的口径一致；若要彻底移除，需要同时改造
  `src/ERP.UnitTests/SalesOrderInvoiceEvidenceTests.cs`（当前任务范围外）。

## 8. 相关实现

- `src/ERP.Api/Controllers/CustomerSalesInvoiceEvidenceController.cs`：授权门改为
  `ControllerContext?.HttpContext is not null`（不再读取请求路径 / 依据可解析身份）；台账 / 单证候选范围谓词与
  单据 / 拟提议客户 / 拟提议来源复核在绑定请求管线时**无条件**执行，且请求体缺失时仍先解析实时身份 / 菜单。
- `src/ERP.Api/Controllers/CustomerSalesInvoiceCollectionAllocationController.cs`：移除 `RequiresLiveAuthorization`
  与无授权服务直调；登记 / 作废无条件走 `EnsureMenuAuthorizedAsync` +
  `CreateAuthorizedAsync` / `VoidAuthorizedAsync`。
- `src/ERP.Application/Services/CustomerSalesInvoiceAuthorizationRules.cs`：新增 `PathIndependenceText` 与
  `UnauthorizedText` / `DeletedIdentityText` / `DisabledIdentityText` / `MenuDeniedText` / `ScopeDeniedText`
  公共文案常量（实现与测试 / 文档同源），判定逻辑不变。
- `src/ERP.Application/Services/CustomerReceiptLifecycleRules.cs`：新增
  `CollectionAllocationPathIndependenceText` 与 `UnauthorizedText` / `DeletedIdentityText` /
  `DisabledIdentityText` / `MenuDeniedText` 公共文案常量（`EnsureMenuAuthorizedAsync` 逐字沿用既有文案）。
- 复用既有：`SalespersonDataScopeService`（ERP-097）、`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`
  （角色 → 菜单）、`CustomerSalesInvoiceEvidenceService`、`CustomerSalesInvoiceCollectionAllocationService`、
  `CustomerSalesInvoiceConcurrencyRules`（锁序 / 额度）。


