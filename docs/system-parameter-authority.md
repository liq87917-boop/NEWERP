# 系统参数的实时授权与运营取值护栏（ERP-446）

> 阶段 3 · 运营配置收口 · 复用既有「系统参数」功能菜单与既有参数驱动默认值口径

## 1. 目标

`api/sys/parameters`（`ParameterController`）此前只有 `[Authorize]`，分页 / 主键详情 / 按键详情 / 新增 /
修改对 `SysParameters` 的读写**零身份 / 零菜单 / 零取值校验**：任意已认证账号即可列出、新增、改写系统参数。
其中 `DefaultCurrency` 与 `ExchangeRate` 会被 `BillProcController.LoadCurrencyDefaultsAsync` /
`ApplyCurrencyDefaultsAsync` 当作**权威兜底**，在运营单据缺省币种或汇率时写入新保存的文档，
因此无护栏的写入可静默改变新单据的币种与汇率默认值。

本改动把该路由纳入与其它模块一致的实时身份 + 既有功能菜单护栏，并为新增 / 修改补充既有持久化列的有界校验与
**运营取值护栏**：**读取 / 写入任何参数行之前**先解析 **实时身份 → 账号状态 → 既有「系统参数」（`sys-parameter`）功能菜单**。

只**复用**既有分层架构与既有权限模型：**不新增**任何菜单 / 角色 / 用户授权 / 表 / 列，**无匿名 / 管理员兜底**，
分页与响应契约（`ApiResponse<PagedResult<SysParameter>>` / 既有提示文案）保持不变，
`BillProcController` 的既有参数驱动默认币种 / 汇率回退行为完全不变。

## 2. 口径

| 项 | 口径 |
|---|---|
| 身份 | 每次请求按 `ClaimTypes.NameIdentifier` 解析：缺失 / 非正整数 → `2000` 未认证；账号不存在或已 `IsDeleted` → `2000`；`Status != Enabled` → `2002` 权限不足 |
| 菜单 | 复用**既有**「系统参数」（`sys-parameter`，与 `SeedData.Menus` 同源）：**每个账号都必须显式具备**，撤销后下一次请求立即收敛；**无特权 / 管理员兜底**（系统内置角色同样需要该既有功能菜单，绝不把空身份或特权当作管理员） |
| 保持契约 | 分页 `PagedResult` 结构、主键 / 按键详情、新增（`参数新增成功`）/ 修改（`参数更新成功`）的响应体与提示文案均不变 |
| 持久化列校验 | 仅新增 / 修改：`ParamKey` / `ParamName` 非空且有界，`ParamKey` 在未删除参数中唯一，`ParamValue` / `Description` 有界；一律以既有受控错误拒绝（形状 / 取值 `1001`，键重复 `1003`） |
| 运营取值护栏 | `DefaultCurrency` 只接受受支持币种代码（`CNY / USD / EUR / HKD / GBP / JPY`，大小写无关），`ExchangeRate` 只接受正的可解析十进制数（`> 0`） |
| 默认回退 | 不改变 `BillProcController`：`DefaultCurrency` 缺失 / 无效时仍回退美元、`ExchangeRate` 缺失 / 无效时仍回退 `1` |

## 3. 路由矩阵（全部先授权）

| 路由 | 先授权 | 持久化列 / 运营取值校验 | 数据库写入 |
|---|---|---|---|
| `GET api/sys/parameters` | ✅ 身份 + 状态 + 菜单 | — | — |
| `GET api/sys/parameters/{id}` | ✅ 身份 + 状态 + 菜单 | — | — |
| `GET api/sys/parameters/key/{key}` | ✅ 身份 + 状态 + 菜单 | — | — |
| `POST api/sys/parameters` | ✅ 身份 + 状态 + 菜单 | ✅ | 校验通过后新增 |
| `PUT api/sys/parameters/{id}` | ✅ 身份 + 状态 + 菜单 | ✅ | 校验通过后改写 |

授权判定严格先于任何实体读取与 `dbContext.Add` / `SaveChangesAsync`：被拒绝的请求不加载、不写入、不改写任何参数行；
`Update` **不修改** `ParamKey`（沿用既有语义），仅刷新 `ParamValue` / `ParamName` / `Description` 与 `UpdatedAt`。

## 4. 持久化列 + 运营取值有界校验（新增 / 修改）

| 字段 | 既有持久化上界 | 规则 | 拒绝错误码 |
|---|---|---|---|
| `ParamKey` | 100 | 非空、不超上界，且不与既有未删除参数重复（修改自身不占用键） | `1001` / `1003` |
| `ParamName` | 100 | 非空且不超上界 | `1001` |
| `ParamValue` | 500 | 不超上界（允许为空） | `1001` |
| `Description` | 500 | 不超上界（允许为空） | `1001` |
| `DefaultCurrency` 值 | 500 | 必须是受支持币种代码（与 `BillProcController.MapCurrencyCode` 同源） | `1001` |
| `ExchangeRate` 值 | 500 | 必须是正的可解析十进制数（`> 0`） | `1001` |

写入前 `NormalizeForWrite` 只对拟议入参去空白（`ParamKey` / `ParamName` 去首尾空白，可空值归一为空字符串），
**不静默截断 / 改写**任何已存储行；被拒绝的写入不落任何行。

## 5. 错误信息（API 直接返回）

| 场景 | 错误码 | 说明 |
|---|---|---|
| 无身份 / 非法身份 | `2000` | `SystemParameterAuthorizationRules.UnauthorizedText` |
| 账号不存在 / 已删除 | `2000` | `UserDeletedText` |
| 账号已禁用 | `2002` | `UserDisabledText` |
| 缺少既有「系统参数」菜单（含特权账号缺菜单） | `2002` | `MenuDeniedText`（文案含「模块授权」） |
| 空 / 超长参数键、空 / 超长名称、超长值 / 说明、非法币种 / 汇率 | `1001` | 受控校验文案 |
| 参数键与既有未删除参数重复 | `1003` | `ParamKeyDuplicatedText` |

拒绝一律 fail closed，不泄露任何数据，也不降级为匿名 / 管理员。

## 6. 实现与边界

- `src/ERP.Application/Services/SystemParameterAuthorizationRules.cs`（新增）：纯判定与有界只读查询。
- `src/ERP.Api/Controllers/ParameterController.cs`：五个路由全部先授权，新增 / 修改再规范化 + 校验，响应契约不变。
- `src/ERP.Api/Controllers/BillProcController.cs`：**不改动**（参数驱动默认值读取 / 回退保持不变）。
- 不新增表 / 列 / 菜单 / 角色 / 用户授权，无 `[AllowAnonymous]`、无 `[Authorize(Roles=…)]`、无匿名 / 管理员兜底。
- 身份只来自已认证请求主体；请求提交体**不包含也不接受**任何账号 / 角色字段。
- 每次请求重新解析（不缓存），账号停用 / 删除或菜单撤销后下一次请求立即收敛。

## 7. 测试与验证

| 层级 | 文件 | 覆盖 |
|---|---|---|
| 单元（内存库） | `src/ERP.UnitTests/SystemParameterAuthorizationTests.cs`（新增） | 五个路由在缺失 / 已删除 / 禁用身份与无既有菜单身份下 fail closed 且不改写任何参数行；**特权账号缺既有菜单仍拒绝**（无管理员兜底）；撤销菜单立即收敛；已授予既有菜单可读可写；新增 / 修改对空 / 超长 / 重复参数键、空 / 超长名称、超长值 / 说明返回受控错误；运营键 `DefaultCurrency` / `ExchangeRate` 非法取值拒绝且既有默认值参数不变；源码 / 菜单 / BillProc 契约 |
| 单元（既有回归） | `src/ERP.UnitTests/ParameterControllerTests.cs`（更新） | 既有 7 条契约保持；统一注入已授予既有菜单的启用账号，锁定分页 / 唯一性 / 详情 / 更新口径 |
| SQL 集成（GUID 独占 `NEWERP_AUTOTEST`） | `src/ERP.IntegrationTests/SystemParameterAuthorizationSqlServerTests.cs`（新增） | 真实控制器身份 / 菜单 / 撤销 / 放行（种子管理员与菜单授权操作员）/ 持久化列与运营取值校验矩阵，且被拒绝时不新增 / 不改写任何参数行 |
| 安全 profile | `.ai/config.json` 的 `safe` | `dotnet restore` → Release 构建（`TreatWarningsAsErrors` + 分析器）→ `ERP.UnitTests` 全量 |

## 8. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且 `Integrated Security`；
  错误目标在访问数据库**之前** fail closed（`AssertDedicatedTarget`，含单元级护栏用例）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings*.json` /
  `.env` / 生产凭据。**构建完成不等于阶段验收**：只有受控 localdb 上真实执行通过才算验收证据。

## 9. 边界

- 不改变 `BillProcController` 的参数驱动默认币种 / 汇率回退行为（币种未知仍回退美元、汇率无效仍回退 `1`）。
- 不新增任何用户授权，也不把空身份或特权当作管理员。
- 授权与校验只做纯判定与有界只读查询，绝不落库、绝不改写历史系统参数。
