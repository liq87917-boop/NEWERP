# 菜单记录维护的实时授权、有界字段校验与父级完整性（ERP-455 / ERP-492）

菜单管理端点（`/api/sys/menus` 的读树、新增、修改、删除）在读取或写入 **任何** `SysMenus` 行之前，统一重新解析：

> 实时身份 → 账号状态 → 既有功能菜单授权

菜单编码（`SysMenu.MenuCode`）是每一条运营路由菜单授权所校验的权威、也是 `AuthService` 为每个登录账号
构建菜单树时读取的来源，因此定义菜单的维护端点不再能被任意已认证账号铸造菜单编码、重指向菜单路径或权限编码、
或软删除菜单，从而加宽 / 破坏运营路由与 `RoleController`（ERP-454）所依赖的菜单授权。任一缺失即 **fail closed**，
绝不返回、新增、改写或删除任何菜单行。新增 / 修改另在落库**之前**做有界字段校验与父级完整性校验。

**ERP-492：授权无条件，与请求形状完全无关。** 原「请求形状探针」（`RequiresLiveAuthorization()` 依据
`Request.Path.HasValue`）与它的两个条件护栏已**彻底移除** —— 每一条路由在读写前**无条件**调用
`SysMenuAuthorizationRules.EnsureRead/WriteAuthorizedAsync`，判定与请求路径是否赋值、请求形状、以及控制器是否绑定
`HttpContext` **完全无关**：空路径、已赋值路径与未绑定任何请求上下文的纯进程内直调对同一身份给出完全相同的判定。
因此不再存在「无请求路径 / 无请求上下文 / 无身份」的授权豁免，也绝不把缺失身份当作管理员。契约见
`SysMenuAuthorizationRules.PathIndependenceText`。

## 1. 授权（fail closed，精确复用既有功能菜单）

`SysMenuAuthorizationRules` 在**每一条**路由的最前面执行（先于任何读取 / 写入）：

| 路由 | 所需既有功能菜单 |
|---|---|
| `GET /api/sys/menus/tree` | 既有「用户权限」（`user-permission`）**或**既有「角色管理」（`role`）任一 |
| `POST /api/sys/menus` | 既有「用户权限」（`user-permission`） |
| `PUT /api/sys/menus/{id}` | 既有「用户权限」（`user-permission`） |
| `DELETE /api/sys/menus/{id}` | 既有「用户权限」（`user-permission`） |

| 场景 | 判定结果 |
|---|---|
| 无身份 / 为零 / 非法用户 Id | `2000` 未认证 |
| 账号不存在或已删除 | `2000` 未认证 |
| 账号已禁用 | `2002` 权限不足 |
| 缺少所需既有菜单授权（含被撤销） | `2002` 权限不足 |
| 具备所需既有菜单 | 放行既有读 / 写契约 |

- 读树接受既有「角色管理」（`role`）是因为它与既有 `user-permission` 同属系统设置模块，且该树正是
  `src/ERP.Api/wwwroot/js/roles2.js` 为角色授权界面读取的菜单树；新增 / 修改 / 删除只接受 `user-permission`。
- 菜单授权复用既有「角色 → 菜单」口径（`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`）；
  **每次请求重新查询**，撤销授权后下一次请求立即收敛（绝不缓存）。
- **不新增**任何表 / 列 / 实体 / 菜单 / 权限 / 用户授权；**无匿名 / 管理员回退**，绝不把空身份当作管理员，
  也不因特权（系统内置角色）而跳过菜单检查。
- 既有读树 / 新增 / 修改 / 删除路由与响应契约完全不变，只是每个路由在读写前多了一道实时授权判定。
- **请求形状无关**（ERP-492）：原「请求形状探针」（依据 `Request.Path` 是否赋值决定是否授权）与它的两个
  条件护栏已**彻底移除**。真实 HTTP 请求（空路径或已赋值路径）一律无条件授权，**未绑定任何 `HttpContext`
  的纯进程内直调同样无条件授权** —— 既不存在「空路径 / 无身份」的请求形状旁路，也**不存在**「未进入请求管线
  即免授权」的进程内旁路。缺失 / 为零 / 未知 / 已删除身份一律未认证，禁用 / 缺少或被撤销菜单一律权限不足；
  不读取环境变量、不区分数据库提供程序、无测试专用开关，绝不提供可被外部到达的降级。

## 2. 有界字段 / 父级校验（不截断、不改写）

新增 / 修改在**落库之前**校验持久化列的可存储范围与已知形状，任一项不满足即按 `1001` 参数错误拒绝，
被拒绝的写入**不落任何** `SysMenus` 行、也**不改写**任何既有行：

| 字段 / 载荷 | 规则（与 `SysMenu` 实体 `[MaxLength]` 同源） |
|---|---|
| `MenuCode` | 非空，长度 ≤ 50（`NVARCHAR(50)`）；唯一性仍由既有「非删除集合」重复判定保证 |
| `MenuName` | 非空，长度 ≤ 50（`NVARCHAR(50)`） |
| `Path` | 长度 ≤ 200（`NVARCHAR(200)`） |
| `Icon` | 长度 ≤ 50（`NVARCHAR(50)`） |
| `PermissionCode` | 长度 ≤ 100（`NVARCHAR(100)`） |
| `MenuType` | 必须为已知菜单类型（目录 / 菜单 / 按钮，`MenuType` 已定义值） |
| `ParentId` | 为 `0`（根）或有效正数；非零必须解析为**已知的非删除**菜单，否则整批拒绝 |

- **不静默截断 / 改写**任何字段：越界即拒绝，绝不把超长文本裁短或把未知菜单类型静默改写后写入。
- **父级完整性受控**：新增 / 修改在**任何落库之前**先解析非零 `ParentId`，伪造 / 未知 / 已删除的父级
  绝不持久化任何 `SysMenus` 行，也不改写任何既有行的 `ParentId`。
- **唯一性语义不变**：菜单编码重复仍为 `1003`（Duplicate），仅在同一「非删除集合」上判定，软删除行不占用编码。
- **子菜单删除保护不变**：存在未删除子菜单时删除仍为 `1004`（RuleConflict）；无子菜单时仍为软删除。

## 3. 语义边界

- **响应契约不变**：`ApiResponse<List<MenuTreeNode>>` 与成功消息逐字段不变。
- 只读写 `SysMenus` 自身：不改写任何用户、角色、角色菜单关联、业务单据、库存或财务记录。
- 不新增任何表 / 列 / 实体 / 索引 / 菜单 / 权限模型，不改变 ERP-097 数据范围与既有菜单授权读取口径。

## 4. 单元测试（`src/ERP.UnitTests/SysMenuAuthorizationTests.cs`，内存库）

| 用例 | 断言 |
|---|---|
| 缺失 / 为零 / 未知 / 禁用 / 已删除 / 无菜单（`[Theory]`） | 4 条路由逐一拒绝（`2000` / `2002`），`SysMenus` 快照逐字节不变 |
| 具备既有 `user-permission` 菜单 | 读树 / 新增 / 修改 / 删除既有契约放行 |
| 仅具备既有 `role` 菜单 | 读树放行；新增 / 修改 / 删除 `2002` 且零写入 |
| 特权但缺菜单 | 仍 `2002`（不因特权跳过菜单检查） |
| 请求之间撤销菜单授权 | 下一次 4 条路由立即 `2002`（含读树替代授权被撤销），零写入 |
| 请求形状无关（空路径 / 已赋值路径） | 同一身份判定完全一致：授权身份 4 条路由（读树 / 新增 / 修改 / 删除）放行，缺失身份 `2000` |
| 无 `HttpContext` 纯进程内直调 | 不再豁免：4 条路由 `2000`，`SysMenus` 快照逐字节不变 |
| 新增 / 修改非法载荷（编码 / 名称空值越界、路径 / 图标 / 权限编码越界、未知菜单类型、父级负数 / 未知 / 已删除） | `1001`，零写入 / 既有行与既有字段逐字节不变 |
| 边界值（编码 50、名称 50、路径 200、图标 50、权限编码 100） | 放行；超一位即 `1001` |
| 父级完整性（0 与已知非删除父级） | 放行并按 `ParentId` 持久化；伪造父级拒绝且零写入 |
| 既有契约 | 重复编码仍 `1003`、存在子菜单删除仍 `1004`、软删除编码可复用 |
| 控制器源码 / 菜单契约 | 4 条路由全部先 `EnsureMenuRead/WriteAuthorizedAsync()` 再读写；请求形状探针（`RequiresLiveAuthorization` / `Request.Path`）彻底移除；复用既有 `user-permission` / `role` 菜单；无 `AllowAnonymous` / 角色回退 / 环境变量开关 |

`src/ERP.UnitTests/MenuControllerTests.cs`（编码唯一、子菜单不可删、三层菜单树）与
`src/ERP.UnitTests/AuthServiceTests.cs` 仍全绿：前者按 ERP-492 改造为**真实启用身份 + 既有 `user-permission`
菜单授权**驱动（菜单 / 角色 / 账号只播种在隔离测试数据里，不新增任何生产授权），授权判定与请求路径 / 请求形状 /
是否绑定 `HttpContext` 完全无关。

## 5. 真实 SQL Server 集成测试（`src/ERP.IntegrationTests/SysMenuAuthorizationSqlServerTests.cs`）

在 GUID 独占的 `NEWERP_AUTOTEST` 目标库上以**真实控制器 + 真实既有授权**自包含播种菜单与真实授权账号：

| 场景 | 断言 |
|---|---|
| 缺失 / 为零 / 未知 / 禁用 / 已删除 / 无菜单（`[Theory]`） | 全部 4 条路由对应错误码；`SysMenus` 快照逐字节不变 |
| 请求形状无关（空路径 / 已赋值路径） | 同一授权身份判定完全一致：4 条路由（读树 / 新增 / 修改 / 删除）放行；无身份 `2000` |
| 无 `HttpContext` 纯进程内直调 | 不再豁免：全部 4 条路由 `2000`，`SysMenus` 快照逐字节不变 |
| 具备既有 `user-permission` 菜单 | 读树 / 新增（非零父级）/ 修改 / 软删除全部符合既有契约 |
| 仅具备既有 `role` 菜单 | 读树放行，写操作 `2002` 且零写入 |
| 特权种子管理员（既有种子已含全部菜单） | 既有只读契约放行 |
| 请求之间撤销菜单授权 | 全部 4 条路由下一次均 `2002`，行未软删除 |
| 授权后非法载荷（编码 / 名称空值越界、路径 / 图标 / 权限编码越界、未知菜单类型、父级负数 / 未知 / 已删除） | `1001`，零写入，既有行未被改写 |

**真实 SQL 夹具安全口径**

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，且 `IntegratedSecurity=true`；
  实例名 / 库名前缀 / 集成安全在**访问数据库之前**校验（`AssertDedicatedTarget`，含单元级护栏用例）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；**绝不 drop / reset / 复用**任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings` / `.env` / 生产凭据。

## 6. 未执行 / 限制

- 本任务 `validation_profile = safe`、`completion_mode = build`：交付门槛为 **Release 构建 + 全量 `ERP.UnitTests`**；
  真实 SQL 集成用例在具备专用 localdb 的环境下运行，**构建通过不等于阶段验收通过**。
- ERP-492 实施期间已在本机专用实例 `(localdb)\NEWERP_AutoAcceptance` 上对**全新 GUID 后缀库**执行
  `SysMenuAuthorizationSqlServerTests`（38 例全绿，未 drop / reset 任何数据库）；本文件不据此宣称整阶段验收。
- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置，
  未新增任何表 / 列 / 实体 / 菜单 / 权限 / 连接字符串或密钥。

## 7. 相关实现

- `src/ERP.Api/Controllers/MenuController.cs`：4 条路由**无条件**在读写前调用 `EnsureMenuReadAuthorizedAsync()` /
  `EnsureMenuWriteAuthorizedAsync()`（ERP-492 已删除请求形状探针 `RequiresLiveAuthorization()` 与两个条件护栏），
  新增 / 修改另调用 `SysMenuAuthorizationRules.Validate` / `EnsureParentResolvedAsync`。
- `src/ERP.Application/Services/SysMenuAuthorizationRules.cs`：实时身份 / 账号状态 / 既有功能菜单判定、
  有界字段校验与父级完整性解析；公共契约 `PathIndependenceText`（ERP-492：与请求路径 / 请求形状 /
  是否绑定 `HttpContext` 完全无关）。
- 复用既有：`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`（角色 → 菜单）、
  既有「用户权限」（`user-permission`）与「角色管理」（`role`）菜单（`SeedData.Menus`）。
