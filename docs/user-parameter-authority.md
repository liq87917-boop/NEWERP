# 用户参数的实时授权与有界写入护栏（ERP-456）

> 阶段 3 · 访问控制收口 · 复用既有「用户参数」功能菜单与既有唯一 / 软删除语义

## 1. 目标

`api/sys/user-parameters`（`UserParameterController`）此前只有 `[Authorize]`，分页 / 主键详情 / 新增 /
修改 / 删除对 `SysUserParameters` 的读写**零身份 / 零菜单 / 零有界校验**：任意已认证账号即可列出全部用户参数
（含用户显示名）、读取任意行、改写任意账号的用户参数，并可对任意 / 不存在的 `UserId` 铸造新行。

本改动把该路由纳入与其它模块一致的**实时身份 + 既有功能菜单**护栏，并为新增 / 修改补充既有持久化列的有界校验与
**用户解析校验**：**读取 / 写入任何用户参数行之前**先解析 **实时身份 → 账号状态 → 既有「用户参数」（`user-parameter`）
功能菜单**，再对拟议写入做有界校验。

只**复用**既有分层架构与既有权限模型：**不新增**任何菜单 / 角色 / 用户授权 / 表 / 列，**无匿名 / 管理员兜底**，
分页与响应契约（`ApiResponse<PagedResult<UserParameterView>>` / 既有提示文案）、既有按用户参数键唯一检查与
软删除（`IsDeleted`）语义保持不变。

## 2. 口径

| 项 | 口径 |
|---|---|
| 身份 | 每次请求按 `ClaimTypes.NameIdentifier` 解析：缺失 / 非正整数 → `2000` 未认证；账号不存在或已 `IsDeleted` → `2000`；`Status != Enabled` → `2002` 权限不足 |
| 菜单 | 复用**既有**「用户参数」（`user-parameter`，与 `SeedData.Menus` 同源）：**每个账号都必须显式具备**，撤销后下一次请求立即收敛；**无特权 / 管理员兜底** |
| 保持契约 | 分页 `PagedResult<UserParameterView>`、主键详情、新增（`用户参数新增成功`）/ 修改（`用户参数更新成功`）/ 删除（`删除成功`）的响应体与提示文案不变 |
| 持久化列校验 | 仅新增 / 修改：`ParamKey` 非空且 ≤100，`ParamValue` ≤500；一律以既有受控错误拒绝（`1001`） |
| 用户解析校验 | 新增 / 修改：`UserId` 必须解析为**已知的非删除**用户（未知 / 已删除 / 非正 → `1001`），否则不落任何行 |
| 唯一语义 | **保持既有**：新增时同一用户不得存在重复的未删除 `ParamKey`（`1003`）；修改不改变既有唯一语义，也不改变行归属 |
| 软删除 | **保持既有**：删除只把 `IsDeleted` 置真（不物理删除），不再被详情 / 分页返回 |

## 3. 路由矩阵（全部先授权）

| 路由 | 先授权 | 有界校验 | 数据库写入 |
|---|---|---|---|
| `GET api/sys/user-parameters` | ✅ 身份 + 状态 + 菜单 | — | — |
| `GET api/sys/user-parameters/{id}` | ✅ 身份 + 状态 + 菜单 | — | — |
| `POST api/sys/user-parameters` | ✅ 身份 + 状态 + 菜单 | ✅ 键 / 值 / 用户 + 既有唯一 | 校验通过后新增 |
| `PUT api/sys/user-parameters/{id}` | ✅ 身份 + 状态 + 菜单 | ✅ 键 / 值 / 用户 | 校验通过后改写 |
| `DELETE api/sys/user-parameters/{id}` | ✅ 身份 + 状态 + 菜单 | — | 校验通过后软删除 |

授权判定严格先于任何实体读取与 `dbContext.Add` / `SaveChangesAsync`：被拒绝的请求不加载、不写入、不改写任何用户参数行。
授权**不依赖 `Request.Path` / 环境 / 空请求**：进程内直调与真实 HTTP 请求一律实时授权（空身份 fail closed）。

## 4. 持久化列 + 用户解析有界校验（新增 / 修改）

| 字段 | 既有持久化上界 | 规则 | 拒绝错误码 |
|---|---|---|---|
| `ParamKey` | 100 | 非空且不超上界 | `1001` |
| `ParamValue` | 500 | 不超上界（允许为空） | `1001` |
| `UserId` | — | 必须解析为已知的非删除用户（新增必填；修改若省略则按既有行所属用户校验） | `1001` |
| 重复 `(UserId, ParamKey)` | — | 保持既有：新增时未删除行不得重复 | `1003` |

写入前 `NormalizeForWrite` 只对拟议入参去空白（`ParamKey` 去首尾空白，可空 `ParamValue` 归一为空字符串），
**不静默截断 / 改写**任何已存储行；被拒绝的写入不落任何行。修改**不修改** `UserId`（行归属不变），仅刷新
`ParamKey` / `ParamValue` 与 `UpdatedAt`。

## 5. 错误信息（API 直接返回）

| 场景 | 错误码 | 说明 |
|---|---|---|
| 无身份 / 非法身份 | `2000` | `SysUserParameterAuthorizationRules.UnauthorizedText` |
| 账号不存在 / 已删除 | `2000` | `UserDeletedText` |
| 账号已禁用 | `2002` | `UserDisabledText` |
| 缺少既有「用户参数」菜单（含特权账号缺菜单） | `2002` | `MenuDeniedText`（文案含「模块授权」） |
| 空 / 超长参数键、超长参数值、未知 / 已删除用户 Id | `1001` | 受控校验文案 |
| 同一用户参数键重复 | `1003` | `ParamKeyDuplicatedText` |

拒绝一律 fail closed，不泄露任何数据，也不降级为匿名 / 管理员。

## 6. 实现与边界

- `src/ERP.Application/Services/SysUserParameterAuthorizationRules.cs`（新增）：纯判定与有界只读查询。
- `src/ERP.Api/Controllers/UserParameterController.cs`：五条路由全部先授权，新增 / 修改再规范化 + 校验，响应契约不变。
- 不新增表 / 列 / 菜单 / 角色 / 用户授权，无 `[AllowAnonymous]`、无 `[Authorize(Roles=…)]`、无匿名 / 管理员兜底。
- 身份只来自已认证请求主体；请求提交体**不包含也不接受**任何账号 / 角色字段来扩大身份（`UserId` 只是被维护数据的归属，不参与授权）。
- 每次请求重新解析（不缓存），账号停用 / 删除或菜单撤销后下一次请求立即收敛。
- 授权与校验只做纯判定与有界只读查询，绝不落库、绝不改写历史用户参数。

## 7. 测试与验证

| 层级 | 文件 | 覆盖 |
|---|---|---|
| 单元（内存库） | `src/ERP.UnitTests/SysUserParameterAuthorizationTests.cs`（新增） | 五条路由在缺失 / 非法 / 已删除身份与禁用 / 无既有菜单身份下 fail closed 且不改写任何用户参数行；进程内直调无请求路径同样 fail closed；**特权账号缺既有菜单仍拒绝**（无管理员兜底）；撤销菜单立即收敛；已授予既有菜单可读可写；空 / 超长键、超长值、未知 / 已删除用户拒绝且不落 / 不改写；边界长度接受；软删除语义不变；源码 / 菜单契约 |
| 单元（既有回归） | `src/ERP.UnitTests/SystemParameterAuthorizationTests.cs`、`SysUserMasterAuthorizationTests.cs` | 既有系统参数与用户管理授权契约保持绿色 |
| SQL 集成（GUID 独占 `NEWERP_AUTOTEST`） | `src/ERP.IntegrationTests/SysUserParameterAuthorizationSqlServerTests.cs`（新增） | 真实控制器身份 / 菜单 / 撤销 / 放行（种子管理员与菜单授权操作员）/ 有界校验与用户解析矩阵，且被拒绝时不新增 / 不改写任何用户参数行；软删除只标记 |
| 安全 profile | `.ai/config.json` 的 `safe` | `dotnet restore` → Release 构建（`TreatWarningsAsErrors` + 分析器）→ `ERP.UnitTests` 全量 |

## 8. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且 `Integrated Security`；
  错误目标在访问数据库**之前** fail closed（`AssertDedicatedTarget`，含单元级护栏用例）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings*.json` /
  `.env` / 生产凭据。**构建完成不等于阶段验收**：只有受控 localdb 上真实执行通过才算验收证据。

## 9. 边界

- 不改变既有按用户参数键唯一检查与软删除（`IsDeleted`）语义。
- 不新增任何用户授权，也不把空身份或特权当作管理员。
- 授权与校验只做纯判定与有界只读查询，绝不落库、绝不改写历史用户参数。
