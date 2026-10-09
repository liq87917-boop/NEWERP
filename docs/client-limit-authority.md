# 客户端限制的实时授权与限制形状有界校验（ERP-457）

> 阶段 3 · 访问控制收口 · 复用既有「客户端限制」功能菜单与既有软删除契约

## 1. 目标

`api/sys/client-limits`（`ClientLimitController`）此前只有 `[Authorize]`，分页 / 全部 / 详情 /
新增 / 修改 / 删除 / 批量删除经由 `BaseCrudController<SysClientLimit>` 直接委托 `IGenericService`，
**零授权 / 零菜单 / 零限制形状校验**：任意已认证账号即可列出、新增、修改或软删除系统设置页「客户端限制」
管理的访问限制行。

本改动把该路由纳入与其它模块一致的实时身份 + 既有功能菜单护栏，并为新增 / 修改补充既有持久化列的限制形状有界校验：
**读取 / 计数 / 写入任何一行之前**先解析 **实时身份 → 账号状态 → 既有「客户端限制」（`client-limit`）功能菜单**。

只**复用**既有分层架构与既有权限模型：**不新增**任何菜单 / 角色 / 用户授权 / 表 / 列，**无匿名 / 管理员兜底**，
分页与响应契约（`ApiResponse<PagedResult<…>>` / 既有提示文案）保持不变，`BaseCrudController` 的通用软删除契约
与同一文件中 `OperationLogController` 的既有行为完全不变。

## 2. 口径

| 项 | 口径 |
|---|---|
| 身份 | 每次请求按 `ClaimTypes.NameIdentifier` 解析：缺失 / 非正整数 → `2000` 未认证；账号不存在或已 `IsDeleted` → `2000`；`Status != Enabled` → `2002` 权限不足 |
| 菜单 | 复用**既有**「客户端限制」（`client-limit`，与 `SeedData.Menus` 同源）：**每个账号都必须显式具备**，撤销后下一次请求立即收敛；**无特权 / 管理员兜底**（系统内置角色同样需要该既有功能菜单，绝不把空身份或特权当作管理员） |
| 保持契约 | 分页 `PagedResult` 结构、`all` / 详情 / 新增 / 修改 / 删除 / 批量删除的响应体与提示文案（新增成功 / 更新成功 / 删除成功 / 批量删除成功）均不变 |
| 限制形状校验 | 仅新增 / 修改：`LimitType` 必须是已知客户端限制类型，`LimitValue` 非空且不超过既有持久化上界，`Remark` 不超过既有持久化上界；一律以既有受控错误拒绝（形状 / 取值 `1001`） |
| 通用契约 | `ClientLimitController` 只 **override** 七个继承路由，`BaseCrudController` 与其它派生控制器的软删除契约完全不变 |

## 3. 路由矩阵（全部先授权）

| 路由 | 先授权 | 限制形状校验 | 数据库写入 |
|---|---|---|---|
| `GET api/sys/client-limits` | ✅ 身份 + 状态 + 菜单 | — | — |
| `GET api/sys/client-limits/all` | ✅ 身份 + 状态 + 菜单 | — | — |
| `GET api/sys/client-limits/{id}` | ✅ 身份 + 状态 + 菜单 | — | — |
| `POST api/sys/client-limits` | ✅ 身份 + 状态 + 菜单 | ✅ | 校验通过后新增 |
| `PUT api/sys/client-limits/{id}` | ✅ 身份 + 状态 + 菜单 | ✅ | 校验通过后改写 |
| `DELETE api/sys/client-limits/{id}` | ✅ 身份 + 状态 + 菜单 | — | 软删除 |
| `POST api/sys/client-limits/batch-delete` | ✅ 身份 + 状态 + 菜单 | — | 批量软删除 |

授权判定严格先于任何实体读取、计数与 `dbContext.Add` / `SaveChangesAsync`：被拒绝的请求不加载、不写入、
不改写任何限制行；**非空批量删除 id 列表在被拒绝时不会删除任何行**。`Update` 复用既有 `CopyProperties`
（跳过 `IsDeleted`），**不复活**任何已软删除行。

## 4. 限制形状有界校验（新增 / 修改）

| 字段 | 既有持久化上界 | 规则 | 拒绝错误码 |
|---|---|---|---|
| `LimitType` | 枚举 `ClientLimitType`（`IP=1`、`MachineCode=2`） | 必须是已知客户端限制类型（`Enum.IsDefined`） | `1001` |
| `LimitValue` | 200 | 非空且不超上界（写入前去首尾空白） | `1001` |
| `Remark` | 500 | 不超上界（允许为空，写入前去首尾空白） | `1001` |

校验**只判定不改写实体**（`NormalizeForWrite` 仅对拟议入参去空白）：一律**拒绝而不静默截断、删除或复活**
任何限制行；被拒绝的写入不落任何行。

## 5. 错误信息（API 直接返回）

| 场景 | 错误码 | 说明 |
|---|---|---|
| 无身份 / 非法身份 | `2000` | `ClientLimitAuthorizationRules.UnauthorizedText` |
| 账号不存在 / 已删除 | `2000` | `UserDeletedText` |
| 账号已禁用 | `2002` | `UserDisabledText` |
| 缺少既有「客户端限制」菜单（含特权账号缺菜单） | `2002` | `MenuDeniedText`（文案含「模块授权」） |
| 未知限制类型 / 空或超长限制值 / 超长备注 | `1001` | `LimitTypeUnknownText` / `LimitValueRequiredText` / `LimitValueTooLongText` / `RemarkTooLongText` |

拒绝一律 fail closed，不泄露任何数据，也不降级为匿名 / 管理员。

## 6. 实现与边界

- `src/ERP.Application/Services/ClientLimitAuthorizationRules.cs`（新增）：纯判定与有界只读查询。
- `src/ERP.Api/Controllers/SysSimpleControllers.cs`：`ClientLimitController` 的七个继承路由全部先授权，
  新增 / 修改再规范化 + 校验限制形状，分页 / 响应契约不变；`DocumentNumberRuleController` 与
  `OperationLogController` 行为保持不变。
- 不新增表 / 列 / 菜单 / 角色 / 用户授权，无 `[AllowAnonymous]`、无 `[Authorize(Roles=…)]`、无匿名 / 管理员兜底。
- 身份只来自已认证请求主体；请求提交体**不包含也不接受**任何账号 / 角色字段。
- 每次请求重新解析（不缓存），账号停用 / 删除或菜单撤销后下一次请求立即收敛。

## 7. 测试与验证

| 层级 | 文件 | 覆盖 |
|---|---|---|
| 单元（内存库） | `src/ERP.UnitTests/ClientLimitAuthorizationTests.cs`（新增） | 七个路由在缺失 / 已删除 / 禁用身份与无既有菜单身份下 fail closed 且不新增 / 不改写 / 不软删除任何限制行；**特权账号缺既有菜单仍拒绝**（无管理员兜底）；撤销菜单立即收敛；已授予既有菜单的账号与具备菜单的特权账号可读可写；新增 / 修改对未知限制类型、空 / 超长限制值、超长备注返回受控错误且不落行；被拒批量删除不删除任何行；源码 / 菜单 / 通用软删除契约 |
| 单元（既有回归） | `src/ERP.UnitTests/OperationLogTests.cs`（既有） | `OperationLogController` 与同一文件中的既有契约保持全绿 |
| SQL 集成（GUID 独占 `NEWERP_AUTOTEST`） | `src/ERP.IntegrationTests/ClientLimitAuthorizationSqlServerTests.cs`（新增） | 真实控制器身份 / 菜单 / 撤销 / 放行（种子管理员与菜单授权操作员）/ 限制形状校验矩阵，且被拒绝时不新增 / 不改写 / 不软删除任何限制行 |
| 安全 profile | `.ai/config.json` 的 `safe` | `dotnet restore` → Release 构建（`TreatWarningsAsErrors` + 分析器）→ `ERP.UnitTests` 全量 |

## 8. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且 `Integrated Security`；
  错误目标在访问数据库**之前** fail closed（`AssertDedicatedTarget`，含单元级护栏用例）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings*.json` /
  `.env` / 生产凭据。**构建完成不等于阶段验收**：只有受控 localdb 上真实执行通过才算验收证据。

## 9. 边界

- 不新增任何用户授权、菜单、表或列，也不把空身份或特权当作管理员。
- 不改变 `BaseCrudController` 的通用软删除契约，也不改变任何其它派生控制器（含 `OperationLogController`）行为。
- 授权与校验只做纯判定与有界只读查询，绝不落库、绝不改写历史客户端限制行。
