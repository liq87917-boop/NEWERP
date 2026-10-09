# 角色管理的实时授权、有界字段校验与受控菜单分配（ERP-454）

角色管理端点（`/api/sys/roles` 的分页、全部、按主键读取、角色菜单、新增、修改、删除）
在读取或写入 **任何** `SysRoles` / `SysRoleMenus` 行之前，统一重新解析：

> 实时身份 → 账号状态 → 既有「角色管理」（`role`）功能菜单授权

任一缺失即 **fail closed**，绝不返回、新增或改写任何角色行及其角色菜单绑定。
角色 → 菜单绑定（`SysRoleMenus`）是每一条运营路由菜单授权、`AuthService` 的
`BuildCurrentUserAsync` / `BuildMenuTreeAsync` 与每一次运营权限判定**共同解析**的权威来源，
因此不再能被任意已认证账号铸造角色、任意挂接菜单（含系统菜单）或以角色编码 / 描述越界载荷写入。
新增 / 修改另在落库**之前**做有界字段校验与菜单 Id 解析校验。

## 1. 授权（fail closed，精确复用既有功能菜单）

`RoleAuthorizationRules.EnsureAuthorizedAsync` 在**每一条**路由的最前面执行（先于任何读取 / 写入）：

| 场景 | 判定结果 |
|---|---|
| 无身份 / 非法用户 Id | `2000` 未认证 |
| 账号不存在或已删除 | `2000` 未认证 |
| 账号已禁用 | `2002` 权限不足 |
| 无角色 / 缺少既有 `role` 菜单授权（含被撤销） | `2002` 权限不足 |
| 具备既有 `role` 菜单 | 放行既有读 / 写契约 |

- 菜单授权复用既有「角色 → 菜单」口径（`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`）；
  **每次请求重新查询**，撤销授权后下一次请求立即收敛（绝不缓存）。
- **不新增**任何表 / 列 / 实体 / 菜单 / 权限 / 用户授权；**无匿名 / 管理员回退**，绝不把空身份当作管理员，
  也不因特权（系统内置角色）而跳过菜单检查。
- 既有分页 / 全部 / 按主键 / 角色菜单 / 新增 / 修改 / 删除路由与响应契约完全不变，
  只是每个路由在读写前多了一道实时授权判定。
- **进程内直调边界**（与仓库既有口径同源，键于请求路径）：真实 HTTP 请求（MVC 绑定，`Request.Path` 已赋值）
  一律执行授权，**真实匿名请求因处于请求管线内一律 fail closed（未认证）**；仅「未进入 HTTP 请求管线」的
  **进程内直接调用**（历史 `RoleControllerTests` 单元测试 / 内部派生读取，无请求路径，
  不可能由外部请求到达）沿用既有语义，绝不提供可被外部到达的测试专用降级，也绝不把缺失身份当作管理员。

## 2. 有界字段 / 菜单校验（不截断、不改写）

新增 / 修改在**落库之前**校验持久化列的可存储范围与已知形状，任一项不满足即按 `1001` 参数错误拒绝，
被拒绝的写入**不落任何** `SysRoles` / `SysRoleMenus` 行、也**不改写**任何既有行：

| 字段 / 载荷 | 规则（与 `SysRole` / `RoleRequest` 同源） |
|---|---|
| `RoleName` | 非空，长度 ≤ 50（`NVARCHAR(50)`） |
| `RoleCode` | 非空，长度 ≤ 50（`NVARCHAR(50)`）；唯一性仍由既有唯一索引与既有重复判定保证 |
| `Description` | 长度 ≤ 200（`NVARCHAR(200)`） |
| `MenuIds` | 每个菜单 Id 必须为有效正数且解析为**已知的非删除**菜单，否则整批拒绝 |

- **不静默截断 / 改写**任何字段：越界即拒绝，绝不把超长文本裁短或把未知菜单静默丢弃后写入。
- **菜单分配受控**：`SaveRoleMenusAsync` 在移除 / 重建 `SysRoleMenus` 之前再次解析菜单 Id；
  新增 / 修改在**第一次落库之前**先解析菜单，因此伪造 / 未知 / 已删除菜单 Id 时整批拒绝、零写入，
  绝不接受任意 `menuId`，也绝不落任何 `SysRoleMenus` 关联。
- **唯一索引语义不变**：`ErpDbContext.Config` 的 `SysRole.RoleCode` 唯一索引逐字未改，重复编码仍为 `1003`。
- **内置角色删除保护不变**：`IsSystem` 角色删除仍为 `1004` 业务规则冲突。
- **全删全建语义不变**：角色菜单替换仍为「先删全部既有 `SysRoleMenus`，再为每个去重后的菜单 Id 建一条」，
  在一次 `SaveChanges` 内完成；本护栏只在写入之前追加校验，不改变该原子口径。

## 3. 语义边界

- **响应契约不变**：`ApiResponse<PagedResult<SysRole>>` 等既有返回结构逐字段不变。
- 只读写 `SysRoles` / `SysRoleMenus` 自身：不改写任何用户、角色关联读取、业务单据、库存或财务记录。
- 不新增任何表 / 列 / 实体 / 索引 / 菜单 / 权限模型，不改变 ERP-097 数据范围与既有菜单授权读取口径。

## 4. 单元测试（`src/ERP.UnitTests/RoleMasterAuthorizationTests.cs`，内存库）

| 用例 | 断言 |
|---|---|
| 缺失 / 禁用 / 已删除 / 缺角色菜单（`[Theory]`） | 7 条路由逐一拒绝（`2000` / `2002`），`SysRoles` + `SysRoleMenus` 快照逐字节不变 |
| 具备既有 `role` 菜单（普通角色 / 系统内置角色） | 分页 / 全部 / 按主键 / 角色菜单 / 新增 / 修改 / 删除既有契约放行，全删全建替换正确 |
| 特权但缺菜单 | 仍 `2002`（不因特权跳过菜单检查） |
| 请求之间撤销菜单授权 | 下一次 7 条路由立即 `2002`，零写入 |
| 进程内直调（无请求路径） | 沿用既有历史单元测试语义（真实 HTTP 请求仍 fail closed） |
| 新增非法载荷（名称 / 编码空值越界、描述越界、菜单负数 / 未知 / 已删除） | `1001`，新增不落行、伪造菜单不落关联 |
| 修改非法载荷（名称 / 编码越界、描述越界、菜单负数 / 未知 / 已删除） | `1001`，既有行与既有菜单关联均逐字节不变 |
| 边界值（名称 50、编码 50、描述 200） | 放行；超一位即 `1001` |
| 既有契约 | 重复编码仍 `1003`、`IsSystem` 删除仍 `1004`，零写入 |
| 控制器源码契约 | 7 条路由全部先 `EnsureRoleAuthorizedAsync()` 再读写；复用既有 `role` 菜单；无 `AllowAnonymous` / 角色回退 |

`src/ERP.UnitTests/RoleControllerTests.cs`（角色编码唯一、内置不可删、全删全建、`GetRoleMenus`）与
`src/ERP.UnitTests/AuthServiceTests.cs` 仍全绿（进程内直调沿用既有语义）。

## 5. 真实 SQL Server 集成测试（`src/ERP.IntegrationTests/RoleMasterAuthorizationSqlServerTests.cs`）

在 GUID 独占的 `NEWERP_AUTOTEST` 目标库上以**真实控制器 + 真实既有授权**自包含播种角色与真实授权账号：

| 场景 | 断言 |
|---|---|
| 缺失 / 禁用 / 已删除 / 无菜单（`[Theory]`） | 全部 7 条路由对应错误码；`SysRoles` + `SysRoleMenus` 快照逐字节不变 |
| 具备既有 `role` 菜单 | 分页 / 全部 / 按主键 / 角色菜单 / 新增 / 修改 / 软删除全部符合既有契约 |
| 特权种子管理员（既有种子已含 `role` 菜单） | 既有只读契约放行（不因特权跳过菜单检查） |
| 请求之间撤销菜单授权 | 全部 7 条路由下一次均 `2002`，行未软删除 |
| 授权后非法载荷（名称 / 编码空值越界、描述越界、菜单负数 / 未知 / 已删除） | `1001`，零写入，既有菜单关联未被清空 |

**真实 SQL 夹具安全口径**

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，且 `IntegratedSecurity=true`；
  实例名 / 库名前缀 / 集成安全在**访问数据库之前**校验（`AssertDedicatedTarget`，含单元级护栏用例）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；**绝不 drop / reset / 复用**任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings` / `.env` / 生产凭据。

## 6. 未执行 / 限制

- 本任务 `validation_profile = safe`、`completion_mode = build`：交付门槛为 **Release 构建 + 全量 `ERP.UnitTests`**；
  真实 SQL 集成用例在具备专用 localdb 的环境下运行，**构建通过不等于阶段验收通过**。
- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置，
  未新增任何表 / 列 / 实体 / 菜单 / 权限 / 连接字符串或密钥。

## 7. 相关实现

- `src/ERP.Api/Controllers/RoleController.cs`：7 条路由均在读写前调用 `EnsureRoleAuthorizedAsync()`，
  新增 / 修改另调用 `RoleAuthorizationRules.Validate` / `EnsureMenuIdsResolvedAsync`，
  `SaveRoleMenusAsync` 写入前再次解析菜单 Id。
- `src/ERP.Application/Services/RoleAuthorizationRules.cs`：实时身份 / 账号状态 / 既有角色菜单判定、
  有界字段校验与菜单 Id 解析。
- 复用既有：`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`（角色 → 菜单）、
  既有「角色管理」（`role`）菜单（`SeedData.Menus`）与 `ErpDbContext.Config` 的 `SysRole.RoleCode` 唯一索引。
