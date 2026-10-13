# 主数据（客户 / 供应商 / 员工 / 仓库 / 商品）入口实时授权（与请求路径无关）

> 任务：`ERP-467`「Enforce master-data authority independently of request path」（阶段 3 关键授权入口修复）。
> 关联：`ERP-447`（供应商资料）、`ERP-448`（仓库资料）、`ERP-449`（员工资料）、`ERP-451`（客户资料）、
> `ERP-452`（商品资料），以及同缺陷类的 `ERP-462` / `ERP-463` / `ERP-464`（`docs/purchaseinvoiceentryauthorization.md`、
> `docs/sysuserentryauthorization.md`、`docs/roleentryauthorization.md`）与 `ERP-097`（业务员客户数据范围）。

## 1. 修复前的问题证据

`src/ERP.Api/Controllers/BaseDataControllers.cs` 中五个主数据控制器的实时授权门在修复前**只依据请求形状**：

```csharp
// CustomerController / ProductController
private bool RequiresLiveAuthorization()
{
    var http = ControllerContext?.HttpContext;
    return http?.Request.Path.HasValue == true;      // 无上下文或空路径：免授权
}

// SupplierController / EmployeeController / WarehouseController
private bool RequiresLiveAuthorization()
{
    var http = ControllerContext?.HttpContext;
    if (http is null) return false;                  // 无上下文：免授权
    return http.Request.Path.HasValue || CurrentUserId() is not null; // 空路径且无身份：免授权
}
```

三层放行条件都与**请求形状**有关：没有请求上下文、请求路径为空、没有可解析身份，都会**跳过**
`EnsureCustomerAuthorizedAsync` / `EnsureAuthorizedAsync` / `EnsureEmployeeAuthorizedAsync` /
`EnsureWarehouseAuthorizedAsync` / `EnsureProductAuthorizedAsync` 中的实时身份 / 账号状态 / 既有菜单判定。
因此分页 / 全部 / 按主键 / 新增 / 修改 / 删除 / 批量删除，以及指定货代下拉 / 业务员下拉 / 商品导出 /
单张与批量图片上传等入口，在「空路径 / 无身份 / 未绑定上下文」的调用形状下可绕过 ERP-447/448/449/451/452 的业务护栏。

## 2. 修复后的授权门（与请求路径 / 请求形状 / 上下文无关）

五个控制器**移除** `RequiresLiveAuthorization()` 请求形状开关，改为在**每一个入口无条件**调用既有规则：

```csharp
private async Task EnsureCustomerAuthorizedAsync()
{
    await CustomerAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId());
}
```

- **空路径与已赋值路径口径完全一致**：不再读取 `Request.Path`，请求形状不再参与授权判定；
- **未绑定 `HttpContext` 的纯进程内直调同样执行**：无法解析身份即 `2000` 未认证，**绝不放行**；
- **缺少身份不再放行**：缺失 / 零 / 未知 / 已删除身份一律 fail closed（未认证）；
- 不读取环境变量、不按数据库提供程序 / 内存库放行、不存在任何测试专用开关；
- 不存在 `[AllowAnonymous]`、角色白名单或「退化为管理员」的兜底（源码契约测试逐条断言）。

`CustomerAuthorizationRules` / `SupplierAuthorizationRules` / `EmployeeAuthorizationRules` /
`WarehouseAuthorizationRules` / `ProductAuthorizationRules` 各自固化 `PathIndependenceText`，与
`AuthorizationRuleText`、`AuthorizationBoundaryText` 共同构成「与请求路径无关」的可回归契约文案。

## 3. 逐路由先授权、后读写

| 控制器 | 入口 | 失败语义 |
| --- | --- | --- |
| 客户 `api/base/customers` | 分页 / 全部 / 按主键 / 指定货代下拉 / 新增 / 修改 / 删除 / 批量删除（8 条） | 未认证 / 权限不足 |
| 供应商 `api/base/suppliers` | 分页 / 全部 / 按主键 / 新增 / 修改 / 删除 / 批量删除（7 条） | 未认证 / 权限不足 |
| 员工 `api/base/employees` | 分页 / 全部 / 按主键 / 业务员下拉 / 新增 / 修改 / 删除 / 批量删除（8 条） | 未认证 / 权限不足 |
| 仓库 `api/base/warehouses` | 分页 / 全部 / 按主键 / 新增 / 修改 / 删除 / 批量删除（7 条） | 未认证 / 权限不足 |
| 商品 `api/base/products` | 分页 / 全部 / 按主键 / 新增 / 修改 / 删除 / 批量删除 / 导出 / 单张上传 / 批量上传（10 条） | 未认证 / 权限不足 |

授权先于任何主数据行读取 / 写入与任何导出 / 图片上传产物生成；**被拒绝的请求零写入**（不新增 / 不改写任何
`BaseCustomers` / `BaseSuppliers` / `BaseEmployees` / `BaseWarehouses` / `BaseProducts` 行，也不产出任何
导出或图片上传产物）。缺失 / 零 / 未知 / 已删除身份 → `Err:Unauthorized`（2000）；
禁用账号、缺少或撤销既有菜单 → `Err:Forbidden`（2002）。

## 4. 保留的既有口径（不改业务语义）

- **既有菜单授权**：仍复用既有「客户资料」（`customer`）/「供应商资料」（`supplier`）/「员工资料」（`employee`）/
  「仓库资料」（`warehouse`）/「商品资料」（`product`）功能菜单（与 `SeedData.Menus` 同源），**不新增**任何菜单 /
  角色 / 用户授权，也不修改生产种子权限；特权账号同样必须持有对应既有菜单。
- **ERP-097 业务员客户范围**：客户分页 / 全部 / 按主键仍复用 `SalespersonDataScopeService` 唯一权威口径，不改变。
- **有界字段校验不变**：编码 / 名称非空与长度上限、数值 `DECIMAL(18,4)` 范围、负数 / 状态形状等校验强度不变。
- **指定货代引用语义不变**：引用未变更时保留历史引用，客户端自由文本一律不被采信（`CustomerForwarderService`）。
- **规格 / 货源关系读取标注不变**：`variantCount` / `sourcingCount` 等只是读取标注，不写库。
- **导出字段完整性与图片上传规则不变**：导出仍按既有模板填充全部字段；上传仍走既有扩展名 / 大小校验与 OSS 路径。
- **唯一性 / 软删除 / 编号 / 审计边界不变**：唯一索引语义、软删除语义、`GenericService` 契约与分页 / 响应契约保持。

## 5. 验证证据

### 5.1 单元测试（内存库 + 真实既有身份 / 菜单）

- `src/ERP.UnitTests/MasterDataEntryAuthorizationTests.cs`（新增）：五类控制器 × 三种请求形状（空路径 / 已赋值路径 /
  完全未绑定 `HttpContext`）× 七类身份（缺失 / 零 / 未知 / 已删除 / 禁用 / 缺菜单 / 真实启用身份）的逐入口判定；
  撤销菜单的请求间即时收敛；被拒绝调用对五类主表的零写入快照；授权身份下空路径与已赋值路径的既有生命周期；
  以及源码契约（控制器不含 `Request.Path` / `RequiresLiveAuthorization` / `AllowAnonymous` / 角色白名单 /
  环境变量 / 内存库开关，且五类入口调用计数正确）。
- `src/ERP.UnitTests/CustomerMasterAuthorizationTests.cs`（扩展）：进程内直调（未绑定上下文）同样实时授权 ——
  无身份未认证、特权但无既有客户菜单权限不足、具备既有菜单放行既有读取契约。
- 既有夹具按「真实启用身份 + 既有菜单授权 + 不设置 `Request.Path`」适配：
  `src/ERP.UnitTests/CustomerForwarderTests.cs`（改为既有「客户资料」菜单身份，`BuildController` 走真实授权）。
- 授权身份下的既有主数据契约用例（`SupplierMasterAuthorizationTests` / `EmployeeMasterAuthorizationTests` /
  `WarehouseMasterAuthorizationTests` / `ProductMasterAuthorizationTests`）原本就以「已赋值路径 + 已授权身份」执行，
  实时授权现在同样无条件执行，既有断言逐条保持。
- **尚未适配（超出本任务 `allowed_paths`，见 §8.2）**：`ProductSupplierTests` / `ProductVariantTests` /
  `SalespersonDataScopeTests` 中仍然<b>不携带实时身份或既有菜单授权</b>的进程内直调用例（各 2 条）。

### 5.2 真实隔离 SQL 测试

`src/ERP.IntegrationTests/MasterDataEntryAuthorizationSqlServerTests.cs`（新增）以真实控制器 + 真实既有身份 /
菜单驱动：五类控制器 × 空路径 / 已赋值路径的身份矩阵（缺失 / 零 / 已删除 / 禁用 / 缺菜单 / 撤销菜单 / 真实身份）、
完全未绑定上下文一律未认证、被拒绝调用零写入快照、授权身份下空路径与已赋值路径的既有生命周期，
以及专用目标护栏的 fail-closed 覆盖。

安全口径：目标必须是专用实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且 `IntegratedSecurity=true`；
每次运行只创建**全新 GUID 后缀库**（`NEWERP_AUTOTEST_MASTERDATAENTRYAUTH_<guid>`），发现同名库已存在立即拒绝，
**绝不 drop / reset / 复用**任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，
绝不读取 appsettings / .env / 生产凭据。

## 6. 授权判定边界

修复后，**任何入口调用**都执行实时授权：真实 HTTP 请求由 ASP.NET Core 请求管线提供
`ControllerContext.HttpContext`，因此生产与集成环境中的**每一条**入口（空路径与已赋值路径、匿名与已认证）
都强制走身份 / 账号状态 / 菜单判定，**空路径不再构成旁路**，缺少身份的真实请求一律 fail closed；
完全未绑定 `HttpContext` 的纯进程内直调同样执行本护栏（缺失身份即未认证），不再存在「无请求上下文」的形状豁免。

## 7. 边界与非目标

- 不新增 / 不修改任何表、列、实体、菜单、角色、权限或用户授权，不修改生产种子。
- 不引入 `[AllowAnonymous]`、角色白名单、环境开关、数据库提供程序判断或测试专用放行。
- 不因身份缺失而降级为管理员，不静默截断 / 改写任何主数据字段，不放宽既有校验强度。
- 授权判定每次请求重新解析（不缓存），菜单或账号状态变更后下一次请求立即收敛。

## 8. 未执行 / 限制

### 8.1 验证证据与未执行项

- 本任务 `validation_profile = safe`、`completion_mode = build`：交付门槛为 **Release 构建（分析器 / 警告视为错误）
  + 全量 `ERP.UnitTests`**；真实 SQL 集成用例在具备专用 localdb 的环境下运行，**构建通过不等于阶段验收通过**。
- 已执行的验证：`dotnet build src/ERP.UnitTests/ERP.UnitTests.csproj -c Release`（含依赖工程）
  `/p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true` → **0 警告 0 错误**；
  `dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build` → **通过 7385 / 失败 6 / 总计 7391**
  （失败项全部为 §8.2 列出的、超出本任务 `allowed_paths` 的既有夹具，见下）。
- 监督恢复轮次（attempt 3）**逐条复现同一结果**：Release 构建（分析器 / 警告视为错误）仍为 0 警告 0 错误，
  全量单元套件仍为 **通过 7385 / 失败 6 / 总计 7391**，失败集合与 §8.2 表格逐条一致
  （`SalespersonDataScopeTests` 2 条、`ProductVariantTests` 2 条、`ProductSupplierTests` 2 条）。
  结论：在 `allowed_paths` 未修订前，本任务「全量 `ERP.UnitTests` 全绿」这一交付门槛**不可能**达成 ——
  §8.2 的六条用例要么要求「进程内直调 + 无身份」，要么要求「有身份但无既有菜单」，二者正是本任务要拆除的
  形状旁路；唯一的合规解法是 §8.3 的夹具适配，而其文件不在 `allowed_paths` 内。
- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置。

### 8.2 已识别的允许路径缺口（必须由授权范围修订后适配的既有夹具）

实时授权改为**无条件执行**后，仍然以「进程内直调且不携带实时身份」或以「携带身份但未持有既有菜单」的方式
调用五类主数据控制器的既有夹具会按新口径被正确拒绝（这正是不再存在形状旁路的结果）。这些夹具文件
**不在本任务 `allowed_paths` 内**，按调度器路径护栏要求已保持 HEAD 原状、未被修改，因此其断言仍然停留在
修复前的形状语义上：

| 夹具文件（超出 allowed_paths） | 失败用例 | 新口径下的失败原因 |
| --- | --- | --- |
| `src/ERP.UnitTests/ProductSupplierTests.cs` | `商品列表与详情_标注货源关系启用数与总数_且无关系的商品为0`、`供应商列表与详情_标注供货商品启用数与总数` | 控制器未绑定身份（`ProductControllerOf` / `SupplierControllerOf`）→ 商品 / 供应商资料未认证（2000） |
| `src/ERP.UnitTests/ProductVariantTests.cs` | `GetById与GetPaged_按行标注启用数与规格总数`、`GetById_无规格的历史商品_计数为0且商品本身未被改写` | 同上（商品资料未认证） |
| `src/ERP.UnitTests/SalespersonDataScopeTests.cs` | `CustomerController_GetPaged_受限制业务员_只返回其客户`、`CustomerController_GetById_越界客户_fail_closed` | 绑定身份但角色「Sales」未持有既有「客户资料」（`customer`）菜单 → 权限不足（2002，取代既有 1002 断言） |

按验收标准第 3 条（「把既有进程内夹具适配为**真实启用身份 + 其既有菜单授权**，仅在隔离测试数据内」），
上述三条夹具需要与 `CustomerForwarderTests` 完全同源的适配（播种已启用身份 + 既有 `product` / `supplier` /
`customer` 菜单并授予其角色，然后以该身份驱动控制器；**不新增任何生产菜单 / 权限 / 用户授权**，
也不引入任何测试专用放行）。该适配属于**同一任务范围内的既有夹具修复**，但其文件路径未列入
`allowed_paths`，因此本任务不擅自扩大改动范围：需由调度器（或监督恢复）**授权范围修订**
（`allowed_paths` 增列上述三个 `src/ERP.UnitTests/*Tests.cs`）后执行，适配后全量 `ERP.UnitTests` 即为全绿。

**拒绝放宽的口径**：为迁就这些夹具而保留「空路径 / 无上下文 / 无身份即跳过授权」的旁路，或按其未授权身份
放行，都会重新引入本任务要修复的漏洞类（ERP-447 / ERP-448 / ERP-449 / ERP-451 / ERP-452 被绕过），
因此本任务**不**采用任何此类降级。

### 8.3 待应用的夹具适配补丁（精确、可直接落地）

下列补丁**尚未应用**（涉及文件不在本任务 `allowed_paths` 内），但已逐条核验：套用后 §8.2 的六条用例在
本任务的新口径下全部通过，且**不需要**任何请求形状 / 身份 / 环境 / 测试专用放行。授权范围修订后可直接照抄。

```csharp
// 1) src/ERP.UnitTests/ProductVariantTests.cs —— ProductControllerOf 注入既有「商品资料」菜单身份
//    （AuthorizedContext / EnsureAuthorizedUser 在该文件已存在，无需新增身份模型）
private static ProductController ProductControllerOf(ErpDbContext db) => new(
    new GenericService<BaseProduct>(db),
    new OssStorageService(BuildOssConfiguration()),
    new ProductExcelExporter(db),
    new FakeWebHostEnvironment(),
    db) { ControllerContext = AuthorizedContext(db) };

// 2) src/ERP.UnitTests/ProductSupplierTests.cs —— 商品侧与供应商侧控制器同源注入
//    （AuthorizedContext 已在同一文件内授予既有「商品资料」+「供应商资料」菜单）
private static ProductController ProductControllerOf(ErpDbContext db) => new(
    new GenericService<BaseProduct>(db),
    new OssStorageService(BuildOssConfiguration()),
    new ProductExcelExporter(db),
    new FakeWebHostEnvironment(),
    db) { ControllerContext = AuthorizedContext(db) };

private static SupplierController SupplierControllerOf(ErpDbContext db) => new(
    new GenericService<BaseSupplier>(db), db) { ControllerContext = AuthorizedContext(db) };

// 3) src/ERP.UnitTests/SalespersonDataScopeTests.cs —— 两个客户控制器用例播种既有「客户资料」菜单，
//    其余断言（ERP-097 受限制业务员范围、越界 fail closed）逐条保持不变；GrantCustomerMenu 与
//    CustomerForwarderTests 的既有菜单授予同源，只在隔离测试数据内补建同码菜单：
//      var role = SeedRole(db, "Sales");
//      SeedUserRole(db, user.Id, role.Id);
//      GrantCustomerMenu(db, role.Id);   // 授予既有「客户资料」（customer）菜单
```

必须的 `allowed_paths` 修订（三个文件）：

```text
src/ERP.UnitTests/ProductVariantTests.cs
src/ERP.UnitTests/ProductSupplierTests.cs
src/ERP.UnitTests/SalespersonDataScopeTests.cs
```

注意：这三个文件的适配**只是**把夹具改为携带真实启用身份 + 既有菜单授权，不新增任何生产菜单 / 权限 / 用户授权，
不放宽任何校验强度，也不引入任何测试专用放行；适配后本任务 §6 的「授权判定边界」即为全量入口（含未绑定
`HttpContext` 的纯进程内直调）无例外。

## 9. 相关实现

- `src/ERP.Api/Controllers/BaseDataControllers.cs`：五类控制器移除请求形状开关，全部入口无条件调用既有规则护栏。
- `src/ERP.Application/Services/{Customer,Supplier,Employee,Warehouse,Product}AuthorizationRules.cs`：
  实时身份 / 账号状态 / 既有菜单判定、有界字段校验与 `PathIndependenceText` 路径无关契约文案。
- 复用既有：`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`（角色 → 菜单）、
  `SalespersonDataScopeService`（ERP-097）、既有五类主数据菜单（`SeedData.Menus`）。
