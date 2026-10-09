# 其他资料数据字典实时授权与字段有界校验（ERP-443）

> 阶段 3 · 运营主数据收口 · 复用既有「其他资料」功能菜单与既有字典语义

## 1. 目标

`api/base/other-infos`（`OtherInfoController`）此前只有 `[Authorize]`，且分页 / 全部 / 详情 / 按类型查询 /
新增 / 修改 / 删除 / 批量删除经由 `BaseCrudController<BaseOtherInfo>` 直接委托 `IGenericService`，
**零授权 / 零菜单 / 零字段校验**：任意已认证账号即可列出、新增、修改、删除其它模块视为权威的字典项
（Currency / Port / Forwarder / CustomsBroker / ShippingMark / Package / TradeTerm 等）。

本改动把该路由纳入与其它模块一致的实时身份 + 既有功能菜单护栏，并为新增 / 修改补充既有持久化列的有界校验：
**读取 / 计数 / 写入任何一行之前**先解析 **实时身份 → 账号状态 → 既有「其他资料」（`other-info`）功能菜单**。

只**复用**既有分层架构与既有权限模型：**不新增**任何菜单 / 角色 / 用户授权 / 表 / 列，**无匿名 / 管理员兜底**，
分页与响应契约（`ApiResponse<PagedResult<…>>` / 既有提示文案）保持不变，既有字典项语义不变。

## 2. 口径

| 项 | 口径 |
|---|---|
| 身份 | 每次请求按 `ClaimTypes.NameIdentifier` 解析：缺失 / 非正整数 → `2000` 未认证；账号不存在或已 `IsDeleted` → `2000`；`Status != Enabled` → `2002` 权限不足 |
| 菜单 | 复用**既有**「其他资料」（`other-info`，与 `SeedData.Menus` 同源）：**非特权账号**必须显式具备，撤销后下一次请求立即收敛；**特权账号**（超级管理员 / 系统内置角色 / 显式特权角色，与 ERP-097 同源）继承既有全部访问 |
| 按类型查询 | `by-type/{infoType}` 与其它路由同等授权，再按既有 `InfoType == infoType && Status == 1` 口径返回 |
| 保持契约 | 分页 `PagedResult` 结构、`all` / 详情 / 新增 / 修改 / 删除 / 批量删除的响应体与提示文案（新增成功 / 更新成功 / 删除成功 / 批量删除成功）均不变 |
| 字段校验 | 仅新增 / 修改：`InfoType` 必须是既有已知有界字典类型，`InfoCode` / `InfoName` 非空且有界，`EnglishName` / `Remark` 有界，`Status` 只能是 1 / 0；一律以既有受控校验错误（`1001`）拒绝 |

## 3. 路由矩阵（全部先授权）

| 路由 | 先授权 | 字段校验 | 数据库写入 |
|---|---|---|---|
| `GET api/base/other-infos` | ✅ 身份 + 状态 + 菜单 | — | — |
| `GET api/base/other-infos/all` | ✅ 身份 + 状态 + 菜单 | — | — |
| `GET api/base/other-infos/{id}` | ✅ 身份 + 状态 + 菜单 | — | — |
| `GET api/base/other-infos/by-type/{infoType}` | ✅ 身份 + 状态 + 菜单 | — | — |
| `POST api/base/other-infos` | ✅ 身份 + 状态 + 菜单 | ✅ | 校验通过后新增 |
| `PUT api/base/other-infos/{id}` | ✅ 身份 + 状态 + 菜单 | ✅ | 校验通过后改写 |
| `DELETE api/base/other-infos/{id}` | ✅ 身份 + 状态 + 菜单 | — | 软删除 |
| `POST api/base/other-infos/batch-delete` | ✅ 身份 + 状态 + 菜单 | — | 批量软删除 |

授权判定严格先于任何实体读取、计数与 `dbContext.Add` / `SaveChangesAsync`：被拒绝的请求不加载、不写入、
不改写任何字典行；`Update` 复用既有 `CopyProperties`（跳过 `IsDeleted`），**不复活**任何已软删除行。

## 4. 字段有界校验（新增 / 修改）

| 字段 | 既有持久化上界 | 规则 | 拒绝错误码 |
|---|---|---|---|
| `InfoType` | 50 | 非空；必须是既有「其他资料」页面的已知有界字典类型（忽略大小写与首尾空白） | `1001` |
| `InfoCode` | 50 | 非空且不超过上界 | `1001` |
| `InfoName` | 100 | 非空且不超过上界 | `1001` |
| `EnglishName` | 100 | 可空；不超过上界 | `1001` |
| `Remark` | 500 | 可空；不超过上界 | `1001` |
| `Status` | 1 = 启用 / 0 = 停用 | 只能是 1 或 0 | `1001` |

已知有界字典类型（与 `src/ERP.Api/wwwroot/js/modules.js` 的 `other-info` 页面 InfoType 选项**同源，不新增取值**）：

`Currency`、`ExchangeRate`、`Port`、`Forwarder`、`CustomsBroker`、`ShippingMark`、`Package`、`TradeTerm`、
`Settlement`、`TransportMode`、`ExpenseType`、`ExportMode`、`Certification`、`Brand`、`Other`。

校验**只判定不改写实体**：一律**拒绝而不静默截断、删除或复活**任何字段 / 字典行；被拒绝的写入不落任何行。
既有字典行的读取（货代 / 报关行 / 港口 / 币种等下游引用）语义与口径完全不变。

## 5. 错误信息（API 直接返回）

| 场景 | 错误码 | 说明 |
|---|---|---|
| 无身份 / 非法身份 | `2000` | `OtherInfoAuthorizationRules.UnauthorizedText` |
| 账号不存在 / 已删除 | `2000` | `UserDeletedText` |
| 账号已禁用 | `2002` | `UserDisabledText` |
| 非特权账号缺少既有「其他资料」菜单 | `2002` | `MenuDeniedText`（文案含「模块授权」） |
| 未知资料类型 / 空或有界的编码 / 名称 / 英文名称 / 备注 / 非法状态 | `1001` | `InfoTypeUnknownText` 等受控校验文案 |

拒绝一律 fail closed，不泄露任何数据，也不降级为匿名 / 管理员。

## 6. 实现与边界

- `src/ERP.Application/Services/OtherInfoAuthorizationRules.cs`（新增）：纯判定与有界只读查询。
- `src/ERP.Api/Controllers/OtherInfoController.cs`：七个继承路由 + `by-type` 全部先授权，新增 / 修改再校验字段，
  分页 / 响应契约不变。
- 不新增表 / 列 / 菜单 / 角色 / 用户授权，无 `[AllowAnonymous]`、无 `[Authorize(Roles=…)]`、无匿名 / 管理员兜底。
- 身份只来自已认证请求主体；请求提交体**不包含也不接受**任何账号 / 角色字段。
- 每次请求重新解析（不缓存），账号停用 / 删除或菜单撤销后下一次请求立即收敛。

## 7. 测试与验证

| 层级 | 文件 | 覆盖 |
|---|---|---|
| 单元（内存库） | `src/ERP.UnitTests/OtherInfoAuthorizationTests.cs`（新增） | 八个路由在缺失 / 已删除 / 禁用身份与无既有菜单身份下 fail closed 且不改写任何行；撤销菜单立即收敛；特权账号保留既有访问；已授予既有菜单的非特权账号可读可写；`by-type` 只返回启用行；未知类型 / 空或有界的编码与名称 / 超长英文名称 / 备注 / 非法状态返回 `1001` 且不新增 / 不改写 |
| 单元（既有回归） | `src/ERP.UnitTests/CustomerForwarderTests.cs` | 新增字典写入校验不放宽 / 不收紧既有货代字典语义；`forwarder-options` 仍只返回启用货代 |
| SQL 集成（GUID 独占 `NEWERP_AUTOTEST`） | `src/ERP.IntegrationTests/OtherInfoAuthorizationSqlServerTests.cs`（新增） | 真实控制器身份 / 菜单 / 撤销 / 放行 / 字段校验矩阵，且被拒绝时不新增 / 不改写任何字典行 |
| 安全 profile | `.ai/config.json` 的 `safe` | `dotnet restore` → Release 构建（`TreatWarningsAsErrors` + 分析器）→ `ERP.UnitTests` 全量 |

## 8. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且 `Integrated Security`；
  错误目标在访问数据库**之前** fail closed（`AssertDedicatedTarget`）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings*.json` /
  `.env` / 生产凭据。**构建完成不等于阶段验收**：只有受控 localdb 上真实执行通过才算验收证据。

## 9. 边界

- 不改变既有字典项（币种 / 港口 / 货代 / 报关行 / 唛头 / 包装 / 贸易条款等）的读取语义与下游引用口径。
- 不新增任何用户授权，也不把空身份当作管理员。
- 授权与校验只做纯判定与有界只读查询，绝不落库、绝不改写历史主数据。
