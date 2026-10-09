# 系统用户管理的实时授权、有界字段校验与受控角色分配（ERP-453）

系统用户端点（`/api/sys/users` 的分页、按主键读取、新增、修改、切换状态、重置密码、删除）
在读取或写入 **任何** `SysUsers` / `SysUserRoles` 行之前，统一重新解析：

> 实时身份 → 账号状态 → 既有「用户管理」（`user`）功能菜单授权

任一缺失即 **fail closed**，绝不返回、新增或改写任何用户行及其角色关联。新增 / 修改另在落库**之前**
做有界字段校验与角色 Id 解析校验，重置密码另做有界载荷校验。用户记录是每一次已认证请求、
ERP-097 业务员数据范围与每一条运营权限判定**共同解析**的权威对象，因此不再能被任意已认证账号
铸造用户、重置账号、禁用账号或把账号挂到任意角色上。

## 1. 授权（fail closed，精确复用既有功能菜单）

`SysUserAuthorizationRules.EnsureAuthorizedAsync` 在**每一条**路由的最前面执行（先于任何读取 / 写入）：

| 场景 | 判定结果 |
|---|---|
| 无身份 / 非法用户 Id | `2000` 未认证 |
| 账号不存在或已删除 | `2000` 未认证 |
| 账号已禁用 | `2002` 权限不足 |
| 无角色 / 缺少既有 `user` 菜单授权（含被撤销） | `2002` 权限不足 |
| 具备既有 `user` 菜单 | 放行既有读 / 写契约 |

- 菜单授权复用既有「角色 → 菜单」口径（`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`）；
  **每次请求重新查询**，撤销授权后下一次请求立即收敛（绝不缓存）。
- **不新增**任何表 / 列 / 实体 / 菜单 / 权限 / 用户授权；**无匿名 / 管理员回退**，绝不把空身份当作管理员，
  也不因特权（系统内置角色）而跳过菜单检查。
- 既有分页 / 按主键 / 新增 / 修改 / 切换状态 / 重置密码 / 删除路由与响应契约完全不变，
  只是每个路由在读写前多了一道实时授权判定。
- **进程内直调边界**（与仓库既有口径同源，键于请求路径）：真实 HTTP 请求（MVC 绑定，`Request.Path` 已赋值）
  一律执行授权，**真实匿名请求因处于请求管线内一律 fail closed（未认证）**；仅「未进入 HTTP 请求管线」的
  **进程内直接调用**（历史 ERP-453 之前 / 既有 `SysUserControllerTests` 单元测试、内部派生读取，无请求路径，
  不可能由外部请求到达）沿用既有语义，绝不提供可被外部到达的测试专用降级，也绝不把缺失身份当作管理员。

## 2. 有界字段 / 角色 / 重置密码校验（不截断、不改写）

新增 / 修改 / 重置在**落库之前**校验持久化列的可存储范围与已知形状，任一项不满足即按 `1001` 参数错误拒绝，
被拒绝的写入**不落任何** `SysUsers` / `SysUserRoles` 行、也**不改写**任何既有行：

| 字段 / 操作 | 规则（与 `SysUser` / `SysUserCreateRequest` / `SysUserUpdateRequest` 同源） |
|---|---|
| 新增 `UserName` | 非空，长度 ≤ 50（`NVARCHAR(50)`）；同一**非删除**用户集合内唯一（`ErpDbContext.Config` 唯一索引语义不变） |
| 新增 / 修改 `DisplayName` | 长度 ≤ 50（`NVARCHAR(50)`） |
| 新增 / 修改 `Email` | 长度 ≤ 100（`NVARCHAR(100)`） |
| 新增 / 修改 `Phone` | 长度 ≤ 30（`NVARCHAR(30)`） |
| 新增 `Password` / 重置 `NewPassword` | 长度 6 ~ 128（密码本身不落库，仅 PBKDF2 哈希 + 盐） |
| 修改 `Status` | 仅接受 `1`（启用）或 `0`（禁用） |
| 新增 / 修改 `RoleIds` | 每个角色 Id 必须为有效正数且解析为**已知的非删除**角色，否则整批拒绝 |

- **不静默截断 / 改写**任何字段：越界即拒绝，绝不把超长文本裁短或把未知状态静默映射后写入。
- **角色关联受控**：`SaveRolesAsync` 在移除 / 重建 `SysUserRoles` 之前再次解析角色 Id；
  新增 / 修改在**改写用户字段与第一次落库之前**先解析角色，因此角色非法时整批拒绝、零写入，绝不接受任意 `roleId`。
- **内置管理员保护不变**：`SeedData.AdminUserName`（`admin`）的禁用 / 删除保护在 `SysUserController.cs` 原样保留。
- **密码哈希语义不变**：`PasswordHasher`（PBKDF2 + 随机盐）逐字未改，重置密码仍置 `MustChangePassword = true`。
- **用户名唯一索引语义不变**：`ErpDbContext.Config` 的 `SysUser.UserName` 唯一索引未改动。

## 3. 语义边界

- **响应契约不变**：`ApiResponse<PagedResult<SysUserView>>` 等既有返回结构逐字段不变。
- 只读写 `SysUsers` / `SysUserRoles` 自身：不改写任何业务单据、库存、财务记录或历史行。
- 不新增任何表 / 列 / 实体 / 索引 / 菜单 / 权限模型，不改变 ERP-097 业务员数据范围的读取口径。

## 4. 单元测试（`src/ERP.UnitTests/SysUserMasterAuthorizationTests.cs`，内存库）

| 用例 | 断言 |
|---|---|
| 缺失 / 禁用 / 已删除 / 缺用户菜单（`[Theory]`） | 7 条路由逐一拒绝（`2000` / `2002`），`SysUsers` + `SysUserRoles` 快照逐字节不变 |
| 具备既有 `user` 菜单（普通角色 / 系统内置角色） | 分页 / 按主键 / 新增 / 修改 / 切换状态 / 重置密码 / 删除既有契约放行，角色绑定正确 |
| 请求之间撤销菜单授权 | 下一次请求 `2002`，零写入 |
| 进程内直调（无请求路径） | 沿用既有历史单元测试语义（真实 HTTP 请求仍 fail closed） |
| 新增 / 修改 / 重置非法载荷（用户名空值越界、文本越界、状态越界、密码越界、角色负数 / 未知 / 已删除） | `1001`，新增不落行、修改不改写既有行、重置不改写哈希 |
| 边界值（用户名 50、名称 50、邮箱 100、手机 30、密码 6 / 128、状态 0 / 1） | 放行；超一位即 `1001` |
| 内置管理员保护 | 授权身份下禁用 / 删除仍 `1004`，零写入 |
| 控制器源码契约 | 7 条路由全部先 `EnsureUserAuthorizedAsync()` 再读写；复用既有 `user` 菜单；无 `AllowAnonymous` / 角色回退 |

## 5. 真实 SQL Server 集成测试（`src/ERP.IntegrationTests/SysUserMasterAuthorizationSqlServerTests.cs`）

在 GUID 独占的 `NEWERP_AUTOTEST` 目标库上以**真实控制器 + 真实既有授权**自包含播种用户与真实授权账号：

| 场景 | 断言 |
|---|---|
| 缺失 / 禁用 / 已删除 / 无菜单（`[Theory]`） | 全部 7 条路由对应错误码；`SysUsers` + `SysUserRoles` 快照逐字节不变 |
| 具备既有 `user` 菜单 | 分页 / 按主键 / 新增 / 修改 / 切换状态 / 重置密码 / 软删除全部符合既有契约 |
| 特权种子管理员（含 `user` 菜单） | 既有只读契约放行（不因特权跳过菜单检查） |
| 请求之间撤销菜单授权 | 下一次读取、详情、删除与新增均 `2002`，行未软删除 |
| 授权后非法载荷（用户名空值越界、文本越界、状态越界、密码越界、角色负数 / 未知 / 已删除） | `1001`，零写入，既有密码哈希不变 |

**真实 SQL 夹具安全口径**

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，且 `IntegratedSecurity=true`；
  实例名 / 库名前缀 / 集成安全在**访问数据库之前**校验（`AssertDedicatedTarget`，含单元级护栏用例）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；**绝不 drop / reset / 复用**任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings` / `.env` / 生产凭据。
- **本次实测**：在专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance` 上以全新 GUID 库
  `NEWERP_AUTOTEST_SYSUSERMASTERAUTH_*` 真实执行 —— 8 通过 / 0 失败（全部身份 / 菜单放行、撤销收敛、
  非法载荷零写入场景），另有 4 例目标库护栏单元通过。**构建通过不等于阶段验收通过**；
  被拒路径均未落任何行、未改写任何既有行，未 drop / reset / 复用任何数据库，未读取任何生产设置或凭据。

## 6. 未执行 / 限制

- 本任务 `validation_profile = safe`、`completion_mode = build`：交付门槛为 **Release 构建 + 全量 `ERP.UnitTests`**；
  真实 SQL 集成用例在具备专用 localdb 的环境下运行，**构建通过不等于阶段验收通过**。
- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置，
  未新增任何表 / 列 / 实体 / 菜单 / 权限 / 连接字符串或密钥。

## 7. 相关实现

- `src/ERP.Api/Controllers/SysUserController.cs`：7 条路由均在读写前调用 `EnsureUserAuthorizedAsync()`，
  新增 / 修改另调用 `SysUserAuthorizationRules.ValidateCreate` / `ValidateUpdate` / `EnsureRoleIdsResolvedAsync`，
  重置密码另调用 `ValidateResetPassword`。
- `src/ERP.Api/Controllers/SysUserController.Helpers.cs`：`SaveRolesAsync` 写入前解析角色 Id，拒绝未知 / 已删除角色。
- `src/ERP.Application/Services/SysUserAuthorizationRules.cs`：实时身份 / 账号状态 / 既有用户菜单判定、
  有界字段校验、重置密码载荷校验与角色 Id 解析。
- 复用既有：`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`（角色 → 菜单）、
  既有「用户管理」（`user`）菜单（`SeedData.Menus`）、`PasswordHasher`（PBKDF2）与
  `ErpDbContext.Config` 的 `SysUser.UserName` 唯一索引。
