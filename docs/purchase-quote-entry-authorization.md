# 供应商比价 / 比价审批入口实时授权（与请求路径 / 请求上下文无关）

> 任务：`ERP-486`「Enforce supplier comparison and decision authority at every business entry」。
> 关联：`ERP-416`（比价实时授权与数据范围，`docs/purchase-quote-authority.md`）、
> `ERP-476`（比价实时主数据引用复核，`docs/purchase-quote-master-reference-validation.md`）、
> `ERP-417` / `ERP-418` / `ERP-419`（原子审批 / 转单 / 删除冻结）、
> `ERP-466`（采购订单入口同缺陷类修复）、`ERP-097`（业务员客户数据范围）。

## 1. 修复前的问题证据

`src/ERP.Api/Controllers/PurchaseQuoteController.cs` 与
`src/ERP.Api/Controllers/PurchaseQuoteDecisionController.cs` 的实时授权门在修复前只依据**请求形状**：

```csharp
private bool RequiresLiveAuthorization()
{
    var http = ControllerContext?.HttpContext;
    if (http is null) return false;                                   // 无请求上下文：免授权
    return http.Request.Path.HasValue || CurrentUserId() is not null; // 空路径且无身份：免授权
}
```

因此 `EnsureAuthorizedAsync` / `EnsureDestinationAuthorizedAsync` / `ReauthorizeAsync` 在**缺失 `HttpContext`**
或**空 `Request.Path` 且无身份**时返回 `null`（不受限范围），审批控制器 `Decide` 亦跳过实时身份 / 既有菜单 /
权威客户范围与实时可信操作人判定。受影响形状（历史进程内夹具、内部派生调用）足以让 ERP-416 的护栏被请求形状绕过，
并允许对采购决定 / 历史 / 转单入口忽略当前业务归属。

## 2. 修复后的授权门（与调用形状完全无关）

两个控制器都移除了 `RequiresLiveAuthorization()`，每一条入口都**无条件**调用既有规则：

```csharp
private Task<SalespersonDataScope> EnsureAuthorizedAsync()
    => PurchaseQuoteAuthorizationRules.EnsureAccessAuthorizedAsync(_db, CurrentUserId());

private Task<SalespersonDataScope> EnsureDestinationAuthorizedAsync()
    => PurchaseQuoteAuthorizationRules.EnsureDestinationAuthorizedAsync(_db, CurrentUserId());

private Task<SalespersonDataScope> ReauthorizeAsync()
    => PurchaseQuoteAuthorizationRules.EnsureAccessAuthorizedAsync(_db, CurrentUserId());
```

- **不再依据 `Request.Path`、是否存在 `HttpContext`、或身份是否可解析来决定「是否授权」**；
- **空路径、已赋值路径与完全无 `HttpContext` 口径一致**：一律实时身份 + 既有菜单 + 权威客户范围判定，
  缺失 / 零 / 未知 / 已删除身份 fail closed（未认证），空上下文不再豁免授权；
- 不读取环境变量、不判断数据库提供程序、不存在任何测试专用开关；
- 不存在 `[AllowAnonymous]`、角色白名单或「退化为管理员」的兜底（源码契约测试逐条断言）。

`PurchaseQuoteAuthorizationRules.PathIndependenceText` 固化同一口径契约文案。

## 3. 逐入口先授权、后读写

| 入口 | 授权调用 | 失败语义 |
| --- | --- | --- |
| `GET /api/purchase/quotes`（列表 / 全部 / 详情） | `EnsureAuthorizedAsync`（列表范围先于计数 / 分页下推） | 未认证 / 权限不足 |
| `POST`（新增）、`PUT /{id}`（修改）、`DELETE /{id}`、`POST /batch-delete` | `EnsureAuthorizedAsync`（+ 锁内 `ReauthorizeAsync`） | 未认证 / 权限不足 |
| `GET /{id}/order-prefill`、`POST /{id}/to-order` | `EnsureAuthorizedAsync` + `EnsureDestinationAuthorizedAsync` | 未认证 / 权限不足 |
| `GET /batch-order-plan`、`POST /batch-to-order` | `EnsureAuthorizedAsync` + `EnsureDestinationAuthorizedAsync` + 批次整批归属 | 未认证 / 权限不足 |
| `GET /price-history`、`/{id}/price-history`、`/order-price-variance`、`/{id}/order-price-variance`、`/conversion-funnel` | `EnsureAuthorizedAsync`（范围先于计数 / 分组下推） | 未认证 / 权限不足 |
| `GET /api/purchase/quote-decisions/batch`（批次审批状态） | `EnsureAuthorizedAsync` + 批次整批归属 | 未认证 / 权限不足 |
| `POST /api/purchase/quote-decisions/decide`（记录决定） | `EnsureAuthorizedAsync` + `EnsureLiveActorAsync` + 单行归属 | 未认证 / 权限不足 |

缺失 / 零 / 未知 / 已删除身份 → `Err:Unauthorized`（2000）；禁用账号、缺少或撤销既有「供应商比价」
（`purchase-quote`）菜单 → `Err:Forbidden`（2002）；带入预填 / 转单另须既有「采购订单」（`purchase-order`）菜单
（`DestinationMenuDeniedText`）。**被拒绝的调用零写入、零占号、零状态变更，且不泄露任何供应商 / 商品 / 客户 /
商业快照**：授权先于任何证据读取、单据号消耗与写入。

## 4. 保留的既有口径（不改业务语义）

- **既有菜单授权**：仍复用既有「供应商比价」（`purchase-quote`）与「采购订单」（`purchase-order`）功能菜单
  （与 `SeedData.Menus` 同源），**不新增**任何菜单 / 角色 / 用户授权；特权账号（系统内置角色）沿用既有全部访问口径。
- **权威客户范围**：仍复用 `SalespersonDataScopeService`（ERP-097 唯一权威口径），归属只按比价行持久化
  `CustomerId` 判定，范围先于计数 / 分页 / 分组下推数据库；范围外 / 已删除 / 不存在返回同一非披露错误。
- **主数据引用（ERP-476）**：`RequiresLiveMasterValidation`（`Request.Path` 驱动）与来源 / 承诺引用校验逐字未改，
  真实 HTTP 写入请求一律执行；本次只调整**授权门**（不再依据请求形状）。
- **数值 / 单号 / 事务 / 并发边界不变**：数量 / 单价 / 金额 / 币种 / 单位、单号预约、行锁序、可串行化事务、
  审批 / 转单 / 删除护栏与审计留痕完全不变。
- 未新增任何表 / 列 / 索引 / 权限模型。

## 5. 测试与验证

### 5.1 单元测试（内存库 + 真实既有身份 / 菜单 / 数据范围）

- `src/ERP.UnitTests/PurchaseQuoteEntryAuthorizationTests.cs`（新增）：空路径 / 已赋值路径 / **完全无 `HttpContext`**
  三种调用形状的**逐入口一致判定**；缺失 / 零 / 未知 / 已删除身份 → 未认证；禁用 / 缺少或撤销「供应商比价」菜单 →
  权限不足且撤销后立即收敛；受限业务员按 ERP-097 权威范围收口；转单另须既有「采购订单」菜单；
  授权身份完成既有读取 / 写入 / 带入预填与真实转单且发号留痕不变；被拒调用零写入 / 零发号 / 零决定；
  以及控制器源码契约（不再存在 `RequiresLiveAuthorization` / `AllowAnonymous`）。
- 既有夹具（`PurchaseQuoteApprovalTests`、`PurchaseQuoteMutationTests`、`PurchaseQuoteConversionTests`、
  `PurchaseQuoteConversionBatchTests`、`PurchaseQuoteConversionMutationTests`、`PurchaseQuoteMasterReferenceTests`）
  改为**真实启用身份 + 既有菜单授权**（隔离内存测试数据，`PurchaseQuoteTestAuthority`），
  且**刻意不设置 `Request.Path`**，证明既有契约只有在实时授权通过后才成立。
- `PurchaseQuoteAuthorizationTests`：进程内无身份直调改为断言 fail closed 且零写入。

### 5.2 真实隔离 SQL 测试

`src/ERP.IntegrationTests/PurchaseQuoteEntryAuthorizationSqlServerTests.cs`（新增）以真实控制器 + 真实既有身份 /
菜单 / 数据范围驱动：空路径与已赋值路径一致判定、缺失 / 零 / 未知 / 已删除身份未认证、禁用与撤销菜单权限不足、
授权身份读侧生命周期、被拒绝调用零写入 / 零发号。

安全口径：目标必须是专用实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且
`Integrated Security=true`；每次运行只创建**全新 GUID 后缀库**，发现同名库已存在立即拒绝，**绝不 drop / reset /
复用**任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。

## 6. 授权判定边界（显式声明，不隐藏）

修复后，**任何调用**都执行实时授权：真实 HTTP 请求由 ASP.NET Core 请求管线提供 `ControllerContext.HttpContext`，
因此生产与集成环境中的每一条入口（空路径与已赋值路径、匿名与已认证）都强制走身份 / 账号状态 / 既有菜单 /
权威范围判定；**空路径、缺失身份与缺失 `HttpContext` 都不再构成旁路**。

## 7. 未执行 / 限制

- 本任务 `validation_profile = safe`、`completion_mode = build`：交付门槛为 **Release 构建（分析器 / 警告视为错误）+
  全量 `ERP.UnitTests`**；真实 SQL 集成用例为附加证据，构建或单元测试通过不等于整阶段验收。
- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置，
  未新增任何表 / 列 / 实体 / 菜单 / 权限 / 连接字符串或密钥。

## 8. 相关实现

- `src/ERP.Api/Controllers/PurchaseQuoteController.cs`：`EnsureAuthorizedAsync` / `EnsureDestinationAuthorizedAsync` /
  `ReauthorizeAsync` 无条件执行；全部入口在读写前调用。
- `src/ERP.Api/Controllers/PurchaseQuoteDecisionController.cs`：`BatchStatus` / `Decide` 无条件执行。
- `src/ERP.Application/Services/PurchaseQuoteAuthorizationRules.cs`：新增 `PathIndependenceText`；
  既有 `EnsureAccessAuthorizedAsync` / `EnsureDestinationAuthorizedAsync` / `EnsureQuoteAllowedAsync` /
  `EnsureQuotesAllowedAsync` / `EnsureBatchAllowedAsync` / `EnsureLiveActorAsync` 判定口径保持不变。
- 复用既有：`SalespersonDataScopeService`（ERP-097）、`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`
  （角色 → 菜单）、`PurchaseOrderAuthorizationRules`（既有「采购订单」菜单）、`PurchaseQuoteMasterReferenceRules`（ERP-476）。

