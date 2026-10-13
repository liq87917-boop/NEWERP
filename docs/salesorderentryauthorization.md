# 销售订单读侧入口实时授权（与请求路径无关）

> 任务：`ERP-465`「Enforce canonical sales order read authority independently of request path」（阶段 3 关键授权入口修复）。
> 关联：`ERP-413`（执行证据入口授权，`docs/sales-order-execution-authority.md`）、
> `ERP-420`（规范写入授权，`docs/sales-order-mutation-authority.md`）、
> `ERP-424`（打印 / 导出文档输出授权，`docs/sales-order-document-output-authority.md`）、
> `ERP-462`（供应商采购发票证据入口，`docs/purchaseinvoiceentryauthorization.md`）、
> `ERP-463` / `ERP-464`（系统用户 / 角色管理入口）、`ERP-097`（业务员客户数据范围）。

## 1. 修复前的问题证据

`src/ERP.Api/Controllers/SalesOrderController.cs:34-39` 的实时授权门在修复前只依据**请求形状**：

```csharp
private bool RequiresLiveAuthorization()
{
    var http = ControllerContext?.HttpContext;
    if (http is null) return false;                                   // 无请求上下文：免授权
    return http.Request.Path.HasValue || CurrentUserId() is not null;  // 空路径且无身份：免授权
}
```

调用点在**空路径**（未赋值 `Request.Path`）或**无身份**时完全跳过授权：

- `EnsureExecutionEvidenceAuthorizedAsync()`（时间线 / 财务核对 / 进度 / 退货影响 / 收款与销项发票证据，
  含批量汇总）→ 跳过 `SalesOrderExecutionAuthorizationRules.EnsureReadAuthorizedAsync`；
- `ResolveListDetailScopeAsync()`（`GetPaged` / `GetById`）→ 跳过
  `SalesOrderExecutionAuthorizationRules.EnsureLiveIdentityAsync`。

因此同一台控制器在「真实 HTTP 请求」与「空路径 / 无身份请求形状」下的授权判定不一致，
使 ERP-413 / ERP-424 的读侧护栏可被请求形状绕过。

## 2. 修复后的授权门（与请求路径、请求上下文完全无关）

`RequiresLiveAuthorization()` 请求形状门已被**彻底删除**（不再存在于源码中）；读侧授权改由
`safe-parse 身份解析 + 无条件授权辅助` 构成：

```csharp
// ERP-465：缺少请求上下文 / 缺少身份声明 / 非数字 / 非正 → 一律安全解析为 null（绝不猜测、绝不回退管理员）
private new long? CurrentUserId()
{
    var value = ControllerContext?.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    return long.TryParse(value, out var id) && id > 0 ? id : null;
}

private Task<SalespersonDataScope> EnsureExecutionEvidenceAuthorizedAsync()
    => SalesOrderExecutionAuthorizationRules.EnsureReadAuthorizedAsync(Db, CurrentUserId());

private Task<SalespersonDataScope> ResolveListDetailScopeAsync()
    => SalesOrderExecutionAuthorizationRules.EnsureLiveIdentityAsync(Db, CurrentUserId());
```

- **不再读取请求路径**：请求路径是否赋值不再参与授权判定（源码中已无 `Request.Path` / 请求形状门）；
- **空路径、已赋值路径、缺失身份的真实请求、完全未绑定 `HttpContext` 的纯进程内直调口径完全一致**：
  一律实时授权，缺少身份在触达任何查询之前返回受控未认证（`Err:Unauthorized`）；
- **不存在任何免授权回退**：既不返回 `null` 身份 / `null` 范围，也没有「无请求上下文」豁免、
  兼容开关、测试专用开关或环境变量分支；
- 与已无条件的规范写入（`EnsureCanonicalWriteAuthorizedAsync`，ERP-420）与文档输出
  （`EnsureDocumentOutputAuthorizedAsync`，ERP-424）口径完全对齐；
- 不存在 `[AllowAnonymous]`、角色白名单或「退化为管理员」的兜底（源码契约测试逐条断言）。

`SalesOrderExecutionAuthorizationRules.PathIndependenceText` 固化同一口径，与既有 `RuleText` / `BoundaryText`
共同构成「与请求形状 / 请求上下文无关」的可回归契约文案。

## 3. 逐入口先授权、后读取

| 入口 | 授权调用 | 失败语义 |
| --- | --- | --- |
| `GET /api/sales-orders`（分页） | `ResolveListDetailScopeAsync` → 实时身份 / 账号状态 / ERP-097 范围（**不要求菜单**） | 未认证 / 权限不足 |
| `GET /api/sales-orders/{id}`（详情） | `ResolveListDetailScopeAsync` + `EnsureOrderEvidenceAuthorizedAsync` | 未认证 / 权限不足 / 非披露「销售订单不存在」 |
| `GET /price-history` | `ResolveListDetailScopeAsync`（与分页 / 详情同一口径，**不要求菜单**） | 未认证 / 权限不足 |
| `GET /{id}/timeline`、`/{id}/finance-reconciliation`、`/{id}/progress`、`/{id}/return-impact` | `EnsureExecutionEvidenceAuthorizedAsync` → 实时身份 / 账号状态 / 既有「销售订单」菜单 / ERP-097 范围 + 单张归属复核 | 未认证 / 权限不足 / 非披露 |
| `GET /delivery-exceptions`、`/shipment-finance-report`、`/receipt-reconciliation-report` | `EnsureExecutionEvidenceAuthorizedAsync` | 未认证 / 权限不足 |
| `GET /{id}/receipt-evidence`、`/{id}/invoice-evidence` | `EnsureExecutionEvidenceAuthorizedAsync` + 单张归属复核 | 未认证 / 权限不足 / 非披露 |
| `GET /receipt-evidence-summaries`、`/invoice-evidence-summaries` | `EnsureExecutionEvidenceAuthorizedAsync` + 整批显式 Id 归属复核 | 未认证 / 权限不足 / 非披露（整批拒绝） |

授权先于任何订单 / 明细 / 执行证据 / 时间线字节读取；缺失 / 零 / 未知 / 已删除身份 → `Err:Unauthorized`（2000）；
禁用账号、缺少或撤销既有「销售订单」菜单 → `Err:Forbidden`（2002）。**被拒绝的请求零写入、零授权扩张**。

## 4. 保留的既有口径（不改业务语义）

- **列表 / 详情菜单口径不变**：列表 / 详情只要求实时身份 / 账号状态 / ERP-097 权威范围，**不新增**模块菜单要求
  （与既有实现一致）；只有执行证据入口要求既有「销售订单」（`sales-order`）功能菜单。
- **既有菜单授权**：仍复用既有「销售订单」（`sales-order`）功能菜单（与 `SeedData.Menus` 同源），
  **不新增**任何菜单 / 角色 / 用户授权，也不修改生产种子权限；特权账号（超级管理员 / 系统内置角色 / 显式特权角色）
  沿用既有全部访问口径，但仍须通过实时身份校验。
- **权威客户范围**：仍复用 `SalespersonDataScopeService`（ERP-097 唯一权威口径），归属严格按持久化
  `SalesOrder.CustomerId` 判定；范围外 / 已删除 / 不存在订单返回**同一**受控错误。
- **业务与数值边界不变**：数量 / 单价 / 金额 / 合计 / 定金 / 币种 / 汇率 / 单位、`null-as-unknown`、
  单号生成、事务与锁序（ERP-420 / ERP-421）、审计留痕与状态流转完全不变；本任务只调整「是否执行读侧授权」。
- 未新增任何表 / 列 / 索引 / 权限模型。

## 5. 验证证据

### 5.1 单元测试（内存库 + 真实既有身份 / 菜单 / 数据范围）

- `src/ERP.UnitTests/SalesOrderEntryAuthorizationTests.cs`（新增）：空路径与已赋值路径的**逐入口一致判定**
  （分页 / 详情 / 时间线 / 财务核对 / 进度 / 交付异常 / 退货影响 / 出运财务 / 收款核对 / 收款证据 / 发票证据
  及批量汇总 / 价格历史）；缺失 / 零 / 未知 / 已删除 → 未认证；禁用 / 缺少或撤销菜单 → 权限不足；缺少菜单时列表 / 详情仍可读
  而证据入口权限不足（证明未新增菜单要求）；允许身份在两种请求形状上读取本人订单证据并对外单 / 混入批量单返回同一
  非披露错误；特权账号保留既有全量口径；**完全未绑定 `HttpContext` 的纯进程内直调一律未认证且零写入**
  （`Missing_request_context_is_unauthorized_on_every_read_entry`）；拒绝路径零写入、零授权扩张；以及控制器源码契约
  （无 `Request.Path` / `RequiresLiveAuthorization` / 空 `scope` 回退 / 无上下文豁免 / `AllowAnonymous` / 角色白名单 /
  环境变量 / 内存库开关，且身份经 `ControllerContext?.HttpContext?.User?` 安全解析）。
- `src/ERP.UnitTests/SalesOrderExecutionAuthorizationTests.cs`（扩展）：新增
  `空路径与已赋值路径_缺少身份一律未认证`、`空路径与已赋值路径_同一身份判定完全一致`、
  `未绑定任何请求上下文_一律未认证_不存在免授权回退`、`控制器源码_读侧授权与请求形状无关且无兜底`。
- 既有夹具按「真实启用身份 + 既有菜单授权 + 不设置 `Request.Path`」适配：
  `SalesOrderControllerTests`、`SalesOrderMutationTests`；派生证据只读夹具
  （`SalesOrderDeliveryExceptionTests`、`SalesOrderInvoiceEvidenceTests`、`SalesOrderReceiptEvidenceTests`、
  `SalesOrderReceiptReconciliationTests`、`SalesOrderReturnImpactTests`、`SalesOrderShipmentFinanceReportTests`）
  由免授权直调改为绑定隔离内存库中的真实启用身份（既有特权角色口径）。
- 既有销售订单读侧回归全部保持绿色。

### 5.2 真实隔离 SQL 测试

`src/ERP.IntegrationTests/SalesOrderEntryAuthorizationSqlServerTests.cs`（新增）以真实控制器 + 真实既有身份 /
菜单 / 数据范围驱动：空路径与已赋值路径一致判定、缺失 / 零 / 未知 / 已删除身份未认证、禁用与撤销菜单权限不足、
缺少菜单时列表 / 详情放行而证据入口权限不足、**完全未绑定 `HttpContext` 的纯进程内直调逐入口未认证且零写入 /
零授权扩张**、授权身份读侧生命周期与非披露、被拒绝调用零写入与零授权扩张，以及专用目标护栏的 fail-closed 覆盖。

安全口径：目标必须是专用实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且
`Integrated Security=true`；每次运行只创建**全新 GUID 后缀库**，发现同名库已存在立即拒绝，**绝不 drop / reset /
复用**任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。

## 6. 授权判定边界

修复后，**每一条读侧入口在任何调用形状下**都执行实时授权：真实 HTTP 请求由 ASP.NET Core 请求管线提供
`ControllerContext.HttpContext`，因此生产与集成环境中的每一条读侧入口（空路径与已赋值路径、匿名与已认证）
都强制走身份 / 账号状态 / 既有菜单 / 权威范围判定，**空路径、缺失身份与未绑定 `HttpContext` 都不再构成旁路**。

**不存在任何不执行实时授权的情形**：控制器不再保留「无请求上下文」进程内豁免（该豁免已在 ERP-465 恢复修复中
彻底移除，也不以内部 / 测试兼容边界的形式保留或改名）；未绑定请求上下文时身份安全解析为 `null`，
既有规则在触达任何订单 / 明细 / 证据查询之前返回受控未认证。既有进程内夹具改为绑定真实启用身份，
不再依赖任何免授权调用形状。该判定始终不读取请求路径、环境变量、数据库提供程序或任何测试专用开关。

## 7. 相关实现

- `src/ERP.Api/Controllers/SalesOrderController.cs`：移除 `RequiresLiveAuthorization()`；新增安全解析身份的
  `CurrentUserId()` 并以此驱动无条件的 `EnsureExecutionEvidenceAuthorizedAsync` / `ResolveListDetailScopeAsync`
  （含分页 / 详情 / 价格历史）；写入（`EnsureCanonicalWriteAuthorizedAsync`）与文档输出
  （`EnsureDocumentOutputAuthorizedAsync`）保持无条件调用。
- `src/ERP.Application/Services/SalesOrderExecutionAuthorizationRules.cs`：`PathIndependenceText`
  明确「空路径 / 已赋值路径 / 无请求上下文口径一致、无免授权回退」；`EnsureReadAuthorizedAsync` /
  `EnsureLiveIdentityAsync` 文档明确要求每个入口无条件调用。
- `src/ERP.Application/Services/SalesOrderMutationAuthorizationRules.cs` / `SalesOrderDocumentOutputAuthorizationRules.cs`：
  与读侧口径对齐的文档说明。
- 复用既有：`SalespersonDataScopeService`（ERP-097）、`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`
  （角色 → 菜单）、既有「销售订单」（`sales-order`）菜单（`SeedData.Menus`）。

