# 角色管理入口实时授权（与请求路径无关）

> 任务：`ERP-464`「Enforce system role authority independently of request path」（阶段 3 关键授权入口修复）。
> 关联：`ERP-454`（角色管理实时授权与有界字段校验，`docs/role-master-authority.md`）、
> `ERP-463`（系统用户管理入口实时授权，`docs/sysuserentryauthorization.md`）、
> `ERP-462`（供应商采购发票证据入口实时授权，`docs/purchaseinvoiceentryauthorization.md`）、
> `ERP-097`（业务员客户数据范围）。

## 1. 修复前的问题证据

`src/ERP.Api/Controllers/RoleController.cs:44-51` 的实时授权门在修复前只依据请求路径是否赋值：

```csharp
private bool RequiresLiveAuthorization()
{
    var http = ControllerContext?.HttpContext;
    return http?.Request.Path.HasValue == true;   // 仅「请求路径已赋值」才授权
}
```

因此分页 / 全部 / 按主键读取 / 角色菜单 / 新增 / 修改 / 删除这 7 条入口，以及角色 → 菜单批量写入入口
（`SaveRoleMenusAsync`），在**空路径**（未赋值 `Request.Path`）或**未绑定 `HttpContext`** 的调用形状下会
**完全跳过**实时身份 / 账号状态 / 既有「角色管理」（`role`）菜单判定，使 ERP-454 的业务授权护栏可被请求形状绕过，
并可能在没有实时账号 / 菜单证据的情况下改写 `SysRoles` / `SysRoleMenus`。

## 2. 修复后的授权门（与请求路径完全无关）

授权门与请求路径、请求形状、以及控制器是否绑定 `HttpContext` **完全无关**：

```csharp
private async Task EnsureRoleAuthorizedAsync()
{
    await RoleAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId());
}
```

- **不再存在** `RequiresLiveAuthorization()` / `Request.Path` / `HasValue` 之类的请求形状开关（源码契约测试逐条断言）；
- **空路径与已赋值路径口径完全一致**：两种形状对同一身份给出完全相同的判定结果；
- **完全未绑定 `HttpContext`** 的纯进程内直调同样执行本护栏：无法解析身份即 `2000` 未认证，**绝不放行**；
- **缺失 / 零 / 非法 / 已删除身份** → `Err:Unauthorized`（2000）；**禁用账号 / 撤销或缺少既有菜单** →
  `Err:Forbidden`（2002），且全部发生在任何敏感读取 / 写入**之前**；
- 不读取环境变量、不按数据库提供程序 / 内存库放行、不存在任何测试专用开关，也**绝无**匿名 / 管理员回退。

`RoleAuthorizationRules.PathIndependenceText` 固化同一口径，与 `AuthorizationRuleText`、`AuthorizationBoundaryText`
共同构成「与请求路径无关」的可回归契约文案；`RoleAuthorizationRules.EnsureAuthorizedAsync` 的文档明确要求
**每一个路由入口 / 每一个角色菜单写入入口**无条件调用。

## 3. 逐入口先授权、后读写

| 入口 | 授权调用 | 失败语义 |
| --- | --- | --- |
| `GET /api/sys/roles`（分页） | `EnsureRoleAuthorizedAsync`（先于计数 / 分页） | 未认证 / 权限不足 |
| `GET /api/sys/roles/all`（全部） | `EnsureRoleAuthorizedAsync`（先于读取） | 未认证 / 权限不足 |
| `GET /api/sys/roles/{id}`（按主键读取） | `EnsureRoleAuthorizedAsync`（先于读取角色） | 未认证 / 权限不足 |
| `GET /api/sys/roles/{id}/menus`（角色菜单） | `EnsureRoleAuthorizedAsync`（先于读取关联） | 未认证 / 权限不足 |
| `POST /api/sys/roles`（新增） | `EnsureRoleAuthorizedAsync` + 有界字段校验 + 菜单 Id 解析 | 未认证 / 权限不足 / `1001` |
| `PUT /api/sys/roles/{id}`（修改） | 同上 | 未认证 / 权限不足 / `1001` / `1002` |
| `DELETE /api/sys/roles/{id}`（删除，软删除） | `EnsureRoleAuthorizedAsync`（`IsSystem` 内置角色保护不变） | 未认证 / 权限不足 / `1004` |
| 角色 → 菜单批量写入（`SaveRoleMenusAsync`，由新增 / 修改触发） | `EnsureRoleAuthorizedAsync` + `EnsureMenuIdsResolvedAsync` | 未认证 / 权限不足 / `1001` |

**被拒绝的调用零写入**：不新增 / 不改写任何 `SysRoles` / `SysRoleMenus` 行。

## 4. 保留的既有口径（不改业务语义）

- **既有菜单授权**：仍复用既有「角色管理」（`role`）功能菜单（与 `SeedData.Menus` 同源），
  **不新增**任何菜单 / 角色 / 用户授权，也不修改生产种子权限；特权账号（系统内置角色）同样必须持有该既有菜单。
- **有界字段校验不变**：角色名称 / 编码非空且 ≤ 50、描述 ≤ 200，超界按 `1001` 拒绝且不静默截断 / 改写字段。
- **菜单分配受控不变**：`EnsureMenuIdsResolvedAsync` 仍要求每个菜单 Id 为正数且解析为**已知的非删除**菜单，
  否则整批 `1001` 拒绝、零写入；**不扩权**、不支持任意 `menuId`（含系统菜单伪造）。
- **既有业务边界不变**：`SysRole.RoleCode` 唯一索引语义、`IsSystem` 内置角色删除保护、
  「全删全建」角色菜单替换语义、`ApiResponse<PagedResult<SysRole>>` 等响应契约、审计字段
  （`CreatedAt` / `UpdatedAt`）与软删除语义全部保持。
- 未涉及金额 / 数量 / 单号 / 事务口径：本次修复只调整「是否执行授权」，不改任何业务计算或校验强度。

## 5. 测试与验证

- `src/ERP.UnitTests/RoleControllerTests.cs`：既有业务用例夹具改为**真实的启用身份 + 既有 `role` 菜单授权**
  （隔离内存测试数据），且**刻意不设置** `Request.Path`，证明这些既有契约只有在实时授权通过后才成立。
- `src/ERP.UnitTests/RoleMasterAuthorizationTests.cs`：新增空路径 / 已赋值路径口径一致性断言、
  未绑定 `HttpContext`（纯进程内直调）一律未认证且零写入，以及控制器**源码契约**
  （无 `Request.Path` / `RequiresLiveAuthorization` / `Environment.GetEnvironmentVariable`，
  角色菜单批量写入入口同样先授权）。
- `src/ERP.UnitTests/RoleEntryAuthorizationTests.cs`（新增）：三种请求形状（空路径 / 已赋值路径 /
  完全未绑定上下文）× 七类身份（缺失 / 零 / 已删除 / 禁用 / 缺菜单 / 撤销菜单 / 真实启用身份），
  逐入口断言判定结果、零写入快照、允许身份下的完整既有生命周期与「全删全建」菜单替换，
  以及角色菜单批量写入入口被拒绝身份零授权、授权身份下非法菜单整批拒绝。
- `src/ERP.IntegrationTests/RoleEntryAuthorizationSqlServerTests.cs`（新增，真实 SQL）：
  同样的路径 / 身份矩阵在真实 SQL Server 上运行，另含请求之间撤销菜单 / 禁用 / 已删除身份的实时收敛、
  拒绝后零写入快照、授权身份的既有生命周期、菜单分配入口零写入与特权种子管理员只读放行。
- **隔离目标护栏**：集成夹具只使用精确的 `(localdb)\NEWERP_AutoAcceptance` 实例、`Integrated Security=true`
  与全新 GUID 独占库名（`NEWERP_AUTOTEST_ROLEENTRYAUTH_<guid>`）；访问数据库**之前**先复核实例名 /
  库名前缀 / 集成安全，发现同名库已存在立即拒绝；**绝不 drop / reset / 复用**任何数据库，
  绝不读取 `appsettings*` / `.env` / 生产凭据或生产数据。失败时保留完整日志。
- **交付门**：Release 构建（分析器 / 警告视为错误）通过 + `ERP.UnitTests` 全量单元测试通过；
  真实 SQL 集成测试为附加证据，构建本身不等于整阶段验收。

## 6. 边界与非目标

- 不新增 / 不修改任何表、列、实体、菜单、角色、权限或用户授权，不修改生产种子。
- 不引入 `[AllowAnonymous]`、角色白名单、环境开关、数据库提供程序判断或测试专用放行。
- 不因身份缺失而降级为管理员，不静默截断 / 改写任何角色字段，不放宽既有校验强度。
- 授权判定每次请求重新解析（不缓存），菜单或账号状态变更后下一次请求立即收敛。

## 7. 未执行 / 限制

- 本任务 `validation_profile = safe`、`completion_mode = build`：交付门槛为 **Release 构建 + 全量 `ERP.UnitTests`**；
  真实 SQL 集成用例在具备专用 localdb 的环境下运行，**构建通过不等于阶段验收通过**。
- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置，
  未新增任何表 / 列 / 实体 / 菜单 / 权限 / 连接字符串或密钥。

## 8. 相关实现

- `src/ERP.Api/Controllers/RoleController.cs`：7 条路由均在读写前调用 `EnsureRoleAuthorizedAsync()`，
  新增 / 修改另调用 `RoleAuthorizationRules.Validate` / `EnsureMenuIdsResolvedAsync`，
  `SaveRoleMenusAsync` 写入前再次独立复检授权并解析菜单 Id。
- `src/ERP.Application/Services/RoleAuthorizationRules.cs`：实时身份 / 账号状态 / 既有角色菜单判定、
  有界字段校验、菜单 Id 解析与 `PathIndependenceText` 路径无关契约文案。
- 复用既有：`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`（角色 → 菜单）、
  既有「角色管理」（`role`）菜单（`SeedData.Menus`）与 `ErpDbContext.Config` 的 `SysRole.RoleCode` 唯一索引。

