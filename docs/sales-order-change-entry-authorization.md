# 销售订单变更申请入口实时授权（与请求形状无关，ERP-489）

> 任务：`ERP-489`「Require live sales change authorization before every register and lifecycle operation」。
> 关联：`ERP-414`（变更申请登记册实时授权与来源实时归属，`docs/sales-order-change-request-authority.md`）、
> `ERP-415`（登记 / 编辑 / 提交 / 取消原子事务与确定性行锁，`docs/sales-order-change-request-atomicity.md`）、
> `ERP-413` / `ERP-465`（规范销售订单入口路径无关授权，`docs/salesorderentryauthorization.md`）、
> `ERP-487` / `ERP-488`（同缺陷类修复：登记 / 作废不再退化为无授权服务直调）、`ERP-097`（业务员客户数据范围）。

## 1. 修复前的问题证据

`src/ERP.Api/Controllers/SalesOrderChangeRequestController.cs` 的实时授权门在修复前只依据**请求形状**：

```csharp
private bool RequiresLiveAuthorization()
{
    var http = ControllerContext?.HttpContext;
    if (http is null) return false;                                   // 无请求上下文：免授权
    return http.Request.Path.HasValue || CurrentUserId() is not null; // 空路径且无身份：免授权
}

private async Task<SalespersonDataScope?> EnsureAuthorizedAsync()
    => RequiresLiveAuthorization()
        ? await SalesOrderChangeRequestAuthorizationRules.EnsureAccessAuthorizedAsync(_db, CurrentUserId())
        : null;   // 免授权 → scope 为 null
```

因此：

- 台账 / 来源候选 / 指定来源清单 / 登记在缺失 `HttpContext` 或「空 `Request.Path` 且无身份」时，`scope` 为 `null`，
  整段绕过实时身份 / 既有「销售订单」（`sales-order`）菜单 / ERP-097 权威客户数据范围判定；
- 详情 / 编辑 / 提交 / 取消在同样条件下跳过 `EnsureRequestAllowedAsync`，且服务层收到 `null` 范围（不受限），
  任何已登录账号只要猜到一张范围外订单 / 申请 Id，就能读取其来源快照、拟议金额与客户归属，甚至登记 / 编辑 / 提交 /
  取消其变更申请；
- 该口径与 `ERP-487` / `ERP-488` 的「任何调用形状都无条件实时授权」不一致。

## 2. 修复后的授权门（与请求形状无关）

- 彻底**移除** `RequiresLiveAuthorization()` 与 `scope` 为 `null` 的免授权回退；`EnsureAuthorizedAsync()` 返回
  **非空** `SalespersonDataScope`，**无条件**调用既有的
  `SalesOrderChangeRequestAuthorizationRules.EnsureAccessAuthorizedAsync`：

  ```csharp
  private async Task<SalespersonDataScope> EnsureAuthorizedAsync()
      => await SalesOrderChangeRequestAuthorizationRules.EnsureAccessAuthorizedAsync(_db, CurrentUserId());
  ```

- **详情**入口在授权之后**无条件**复核申请与实时来源归属（不再有 `if (scope is not null)` 回退）：

  ```csharp
  var scope = await EnsureAuthorizedAsync();
  await SalesOrderChangeRequestAuthorizationRules.EnsureRequestAllowedAsync(_db, scope, id);
  ```

- **未绑定任何请求（`HttpContext` 为 null）时同样 fail closed**：不再存在「无请求管线且无身份」或
  「空请求路径且无身份」的免授权服务直调旁路。
- 控制器**不读取 `Request.Path`**、不读取环境变量、不使用任何测试专用开关、不按数据库提供程序放行，
  也不存在 `[AllowAnonymous]`、角色白名单或「退化为管理员」的兜底（源码契约测试逐条断言）。
- 契约文案与实现同源：`SalesOrderChangeRequestAuthorizationRules.PathIndependenceText`。

## 3. 逐入口先授权、后读写

`api/sales-order-change-requests` 的**每一条数据 / 状态入口**都在任何计数、分页、候选、来源快照读取与写入之前完成授权：

| 入口 | 授权调用 | 失败语义 |
| --- | --- | --- |
| `GET`（台账分页） | `EnsureAccessAuthorizedAsync` + 服务层 `ApplySourceScope`（计数 / 分页之前下推） | 未认证 / 权限不足 |
| `GET /source-options`（来源候选） | 同上（范围过滤在候选读取之前） | 未认证 / 权限不足 |
| `GET /by-source`（指定来源清单） | `EnsureAccessAuthorizedAsync` + 服务层 `EnsureSourceOrderAllowedAsync` | 未认证 / 权限不足 / 不存在 |
| `GET /{id}`（详情） | `EnsureAccessAuthorizedAsync` + `EnsureRequestAllowedAsync`（申请 + 实时来源归属） | 未认证 / 权限不足 / 不存在 |
| `POST`（登记） | `EnsureAccessAuthorizedAsync` + 锁内 `ResolveLiveScopeAsync` + `EnsureSourceOrderAllowedAsync` | 未认证 / 权限不足 / 不存在 |
| `PUT /{id}`（编辑草稿） | 同上（锁内重读实时授权与当前状态） | 未认证 / 权限不足 / 不存在 / 冲突 |
| `POST /{id}/submit`（提交） | 同上（提交冻结拟议快照） | 未认证 / 权限不足 / 不存在 / 冲突 |
| `POST /{id}/cancel`（取消） | 同上（保留原始取消原因与时间戳） | 未认证 / 权限不足 / 不存在 / 冲突 |

缺失 / 非法 / 未知 / 已删除身份 → `Err:Unauthorized`（2000）；禁用账号、缺少或撤销既有「销售订单」
（`sales-order`）菜单 → `Err:Forbidden`（2002）；受限业务员越界来源 / 申请 → 与「不存在」同一条不披露错误
（`SalesOrderChangeRequestAuthorizationRules.NotFoundText`）。**被拒绝的调用零写入**：不消耗变更申请单号，
不新增 / 不改写任何申请、拟议明细、状态、取消原因 / 时间戳、来源订单或审计记录，且不泄露任何计数、来源单号或拟议快照。

## 4. 保留的既有口径（不改业务语义）

- **既有菜单授权**：仍复用既有「销售订单」（`sales-order`）功能菜单（与 `SeedData.Menus` 同源），**不新增**任何
  菜单 / 角色 / 用户授权；导出专用菜单 `sales-order-export` 绝不当作模块权限。
- **权威客户范围**：仍复用 `SalespersonDataScopeService`（ERP-097 唯一权威口径）；归属只按**来源销售订单当前持久化的
  `CustomerId`** 判定，台账在 `Count` / 分页 / 来源候选之前把范围**下推**到数据库；拟议客户与来源客户历史快照都不是
  授权依据。
- **来源实时归属复核**：许可的显式来源变更在既有可串行化事务 + 确定性锁序「来源销售订单行 → 变更申请行」内
  **重读实时授权与当前来源**（`ResolveLiveScopeAsync` + `EnsureSourceOrderAllowedAsync`），绝无隐式来源改派。
- **原子性与并发**：登记 / 编辑 / 提交 / 取消的原子事务、来源 / 申请行锁、锁内状态门、受控写冲突映射逐字未改。
- **快照与状态语义**：来源 / 拟议快照不可变、仅草稿可编辑、提交冻结、取消保留原因与历史、nullable 手工草稿在既有
  业务规则允许处保持为 null —— 全部不变；**绝不**自动把拟议套用到原始销售订单。
- **审计与身份**：登记 / 提交 / 取消的操作者一律取登录账号（客户端字段不作授权依据）；被拒绝的调用不产生任何审计变更。
- 未新增任何表 / 列 / 索引 / 权限模型，也未改写来源销售订单与报价单 / 出库 / 装柜与出运 / 收款与发票 / 佣金 / 库存 /
  单证 / 财务记录（错误文案与结构性受控错误保持既有受控错误）。

## 5. 测试与验证

### 5.1 单元测试（内存库 + 真实既有身份 / 菜单 / 数据范围）

- `src/ERP.UnitTests/SalesOrderChangeRequestEntryAuthorizationTests.cs`（新增）：空路径 / 已赋值路径 / 完全未绑定
  `HttpContext` 的**逐入口一致判定**；缺失 / 零 / 未知 / 已删除身份 → 未认证；禁用 / 无菜单 / 仅导出菜单 / 已撤销
  菜单 → 权限不足；受限业务员越界来源与申请 → 同一非披露错误且不发号、零变更；授权身份完成既有「草稿 → 编辑 →
  提交 → 取消」生命周期且来源订单不变；控制器源码契约（不再读取 `Request.Path`、无 `RequiresLiveAuthorization` /
  `AllowAnonymous` / 角色白名单 / 环境变量 / 内存库开关、无 `scope is not null` 回退）。
- 既有夹具 `src/ERP.UnitTests/SalesOrderChangeRequestTests.cs`：`BuildController` 改为绑定**真实的既有启用身份 +
  既有「销售订单」菜单授权**（空路径），既有登记 / 快照 / 校验 / 提交 / 取消 / 台账契约逐字保持。
- `src/ERP.UnitTests/SalesOrderChangeRequestAuthorizationTests.cs`：既有身份 / 菜单 / 范围矩阵保持；原「进程内无身份
  直调免授权」用例改为**一律未认证且零写入**，并断言授权身份（空路径）照常成功。
- `src/ERP.UnitTests/SalesOrderChangeRequestMutationTests.cs`：既有原子性 / 锁序 / 并发 / 锁内重读权限回归全部保持
  （夹具本就使用真实启用身份 + 既有菜单 + 本人客户来源）。

### 5.2 真实隔离 SQL 测试

`src/ERP.IntegrationTests/SalesOrderChangeRequestEntryAuthorizationSqlServerTests.cs`（新增）以真实控制器 + 真实既有
身份 / 菜单 / 数据范围驱动：空路径与已赋值路径一致判定、缺失 / 零 / 未知 / 已删除身份在全部 8 条入口未认证、
禁用与撤销菜单在全部入口权限不足、受限业务员越界来源与申请非披露拒绝且不发号、授权身份完成登记 → 编辑 → 提交 →
取消生命周期且来源订单状态 / 总额 / 更新时间 / 明细行数不变、**纯进程内 `HttpContext` 为 null 时同样 fail closed**、
被拒调用零写入（含 `SysOperationLogs`）且绝不新增授权行。

安全口径：目标必须是专用实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且
`Integrated Security=true`；每次运行只创建**全新 GUID 后缀库**，发现同名库已存在立即拒绝，**绝不 drop / reset /
复用**任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。

## 6. 授权判定边界（显式声明，不隐藏）

- 修复后**任何调用形状**都执行实时授权，包括未绑定任何请求的纯进程内直调；空路径、缺失身份与空上下文都不再构成旁路。
  这是与 `ERP-487` / `ERP-488` 对同类入口采取的一致口径（比 `ERP-465` 的销售订单读侧更严：不再保留进程内边界）。
- 本任务 `validation_profile = safe`、`completion_mode = build`：交付门槛为 **Release 构建（分析器 / 警告视为错误）+
  全量 `ERP.UnitTests`**；真实 SQL 集成用例为附加证据，构建或单元测试通过不等于整阶段验收。

## 7. 未执行 / 限制

- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置，
  未新增任何表 / 列 / 实体 / 菜单 / 权限 / 连接字符串或密钥。
- 真实 SQL 集成用例已在专用 `(localdb)\NEWERP_AutoAcceptance` 上真实执行通过（全新 GUID `NEWERP_AUTOTEST` 库）：
  新增入口授权用例 8/8 通过，既有变更申请 SQL 用例 30/30 通过；但**构建或单次测试通过不等于整阶段验收**，
  阶段结论由调度器按证据裁定。
- 未新增任何权限 / 菜单 / 角色 / 用户授权，也未改动生产或非专用数据库目标。

## 8. 相关实现

- `src/ERP.Api/Controllers/SalesOrderChangeRequestController.cs`：移除 `RequiresLiveAuthorization` 与 `scope` 为
  `null` 的免授权回退；八条入口无条件 `EnsureAuthorizedAsync()`，详情无条件 `EnsureRequestAllowedAsync`。
- `src/ERP.Application/Services/SalesOrderChangeRequestAuthorizationRules.cs`：新增 `PathIndependenceText`
  公共文案常量（判定逻辑与 `EnsureAccessAuthorizedAsync` / `ApplySourceScope` / `EnsureSourceOrderAllowedAsync` /
  `EnsureRequestAllowedAsync` 逐字未改）。
- 复用既有：`SalesOrderExecutionAuthorizationRules.EnsureReadAuthorizedAsync`（实时身份 + 既有菜单 + 范围）、
  `SalespersonDataScopeService`（ERP-097）、`SalesOrderChangeRequestService`、
  `SalesOrderChangeRequestMutationRules`（`ResolveLiveScopeAsync` / 事务 / 行锁）。
