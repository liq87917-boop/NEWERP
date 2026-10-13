# 系统日志的实时授权与跨模块操作历史非披露（ERP-499）

> 阶段 3 · 访问控制收口 · 复用既有「系统日志」功能菜单与既有分页 / 响应契约

## 1. 目标

`api/sys/logs`（`OperationLogController`，`src/ERP.Api/Controllers/SysSimpleControllers.cs`）此前只有
`[Authorize]`，`GetPaged` 直接以 `_db.SysOperationLogs` 查询（仅过滤软删除）：**任意已认证账号**都能读取
整个操作日志登记册，包括他人 `UserName`、跨模块 `Module` / `Action`、`Path`、`BillNo`、无界 `RequestBody`
与 `IpAddress` / `StatusCode` —— 即披露其它账号的请求载荷与跨模块业务历史。

本改动把该路由纳入与同文件 `ClientLimitController`（ERP-457）、`DocumentNumberRuleController`（ERP-445）
及 legacy 操作历史读取（ERP-406）一致的实时授权口径：**读取任何计数或行之前**先解析
**实时身份 → 账号状态 → 既有「系统日志」（`sys-log`）功能菜单**。

只**复用**既有分层架构与既有权限模型：**不新增**任何菜单 / 角色 / 用户授权 / 表 / 列 / 索引，
**无匿名 / 管理员 / 环境 / 测试专用兜底**；分页筛选与 `ApiResponse<PagedResult<SysOperationLog>>`
响应契约、以及同一文件中 `ClientLimitController` / `DocumentNumberRuleController` 的行为完全不变。

## 2. 口径

| 项 | 口径 |
|---|---|
| 身份 | 每次请求按 `ClaimTypes.NameIdentifier` 解析：缺失 / 非数字 / 非正整数 → `2000` 未认证；账号不存在或已 `IsDeleted` → `2000`；`Status != Enabled` → `2002` 权限不足 |
| 菜单 | 复用**既有**「系统日志」（`sys-log`，与 `SeedData.Menus` 同源）：**每个账号都必须显式具备**，撤销后下一次请求立即收敛；**无特权 / 管理员兜底**（系统内置角色同样需要该既有功能菜单） |
| 保持契约 | 分页 `PagedResult` 结构、模块 / 用户 / 单据号 / 关键字 / 时间范围筛选（起始**包含**、结束日**次日排他**）、软删除行排除与页码 / 每页归一均不变 |
| 调用形状无关 | 与请求路径 / 请求形状 / 是否绑定 `HttpContext` 完全无关；未绑定任何请求上下文的纯进程内直调同样先授权并 fail closed |
| 非披露 | 被拒绝的调用**不读取、不计数、不返回**任何日志行、计数、`UserName`、`Path`、`BillNo` 或 `RequestBody` |

## 3. 路由矩阵

| 路由 | 先授权 | 日志读取 |
|---|---|---|
| `GET api/sys/logs` | ✅ 身份 + 状态 + 既有 `sys-log` 菜单 | 授权通过后才计数 / 分页 |

授权判定严格先于任何 `SysOperationLogs` 计数与行读取：被拒绝的请求零日志数据集访问（单元测试以
`DispatchProxy` 计数代理断言 `SysOperationLogs` 从未被访问）。

## 4. 错误信息（API 直接返回）

| 场景 | 错误码 | 说明 |
|---|---|---|
| 无身份 / 非法身份 | `2000` | `OperationLogAuthorizationRules.UnauthorizedText` |
| 账号不存在 / 已删除 | `2000` | `UserDeletedText` |
| 账号已禁用 | `2002` | `UserDisabledText` |
| 缺少既有「系统日志」菜单（含特权账号缺菜单） | `2002` | `MenuDeniedText`（文案含「模块授权」） |

拒绝一律 fail closed，不泄露任何日志数据，也不降级为匿名 / 管理员。

## 5. 实现与边界

- `src/ERP.Application/Services/OperationLogAuthorizationRules.cs`（新增）：纯判定与有界只读查询
  （身份 / 账号 / 菜单相关数据集），绝不读取 `SysOperationLogs`。
- `src/ERP.Api/Controllers/SysSimpleControllers.cs`：`OperationLogController.GetPaged` 首行先授权，
  再按既有分页 / 响应契约返回；**类声明行 `public class OperationLogController : ControllerBase` 逐字节不变**，
  `ClientLimitController` 与 `DocumentNumberRuleController` 行为不变。
- 不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权；无 `[AllowAnonymous]`、无 `[Authorize(Roles=…)]`、
  不读取 `Request.Path`、不读取环境变量、不区分数据库提供程序、无测试专用开关。
- 身份只来自已认证请求主体；请求体 / 查询字符串**不包含**任何账号或角色字段。
- 每次请求重新解析（不缓存），账号停用 / 删除或菜单撤销后下一次请求立即收敛。

## 6. 测试与验证

| 层级 | 文件 | 覆盖 |
|---|---|---|
| 单元（内存库） | `src/ERP.UnitTests/OperationLogTests.cs`（适配） | 历史筛选契约改由**真实启用身份且持有既有 `sys-log` 菜单授权**的夹具驱动，分页筛选与响应契约全绿 |
| 单元（内存库） | `src/ERP.UnitTests/OperationLogAuthorizationTests.cs`（新增） | 缺失 / 零 / 非法 / 已删除身份 → `2000`，禁用 / 缺菜单 / 撤销菜单 / 特权缺菜单 → `2002`；空 / 已赋值 `Request.Path`、空 / 已赋值筛选、未绑定 `HttpContext` 的直调口径一致；拒绝路径 `SysOperationLogs` **零访问**且拒绝文案不含任何日志数据；放行账号保留既有筛选（模块 / 用户 / 单据号（含 `Path`）/ 关键字 / 起始包含与次日排他 / 软删除排除 / 页码归一）与响应契约；源码 / 菜单契约 |
| 单元（既有回归） | `src/ERP.UnitTests/ClientLimitAuthorizationTests.cs`（既有） | 同一文件中 `OperationLogController` 类声明逐字节不变、`ClientLimitController` 契约全绿 |
| SQL 集成（GUID 独占 `NEWERP_AUTOTEST`） | `src/ERP.IntegrationTests/OperationLogAuthorizationSqlServerTests.cs`（新增） | 真实控制器身份 / 状态 / 菜单拒绝矩阵、撤销收敛、种子管理员与菜单授权操作员的放行与筛选契约、无 `HttpContext` 直调拒绝、拒绝路径零写入；目标护栏单元级校验 |
| 安全 profile | `.ai/config.json` 的 `safe` | `dotnet restore` → Release 构建（`TreatWarningsAsErrors` + 分析器）→ `ERP.UnitTests` 全量 |

## 7. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且 `Integrated Security`；
  错误目标在访问数据库**之前** fail closed（`AssertDedicatedTarget`，含单元级护栏用例）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings*.json` /
  `.env` / 生产凭据。**构建完成不等于阶段验收**：只有受控 localdb 上真实执行通过才算验收证据。
- 测试输出 / TRX / 结果目录一律写入 `.ai/logs/ERP-499`，绝不写入仓库根的 `TestResults`。

## 8. 边界

- 不新增任何用户授权、菜单、表、列或索引，也不把空身份或特权当作管理员。
- 不改变 `BaseCrudController` 的通用软删除契约，也不改变任何其它控制器行为。
- 授权只做纯判定与有界只读查询，绝不落库、绝不改写任何历史日志行。
- 浏览器验收按任务配置为 `browser_acceptance.required = false`，本改动不涉及前端。
