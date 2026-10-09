# 单据号规则的实时授权与编号形状有界校验（ERP-445）

> 阶段 3 · 单据控制收口 · 复用既有「单据号规则」功能菜单与既有编号语义

## 1. 目标

`api/sys/document-number-rules`（`DocumentNumberRuleController`）此前只有 `[Authorize]`，分页 / 全部 / 详情 /
新增 / 修改 / 删除 / 批量删除经由 `BaseCrudController<SysDocumentNumberRule>` 直接委托 `IGenericService`，
**零授权 / 零菜单 / 零编号形状校验**：任意已认证账号即可列出、新增、修改、删除 `DocumentNumberService`
在生成销售 / 采购 / 出入库 / 调拨 / 盘点 / 装柜 / 收款 / 付款等**每一种**运营单据号时读取的规则
（`DocumentNumberService` 按 `DocumentType` 取规则并推进 `CurrentSequence`）。

本改动把该路由纳入与其它模块一致的实时身份 + 既有功能菜单护栏，并为新增 / 修改补充既有持久化列的编号形状有界校验：
**读取 / 计数 / 写入任何一行之前**先解析 **实时身份 → 账号状态 → 既有「单据号规则」（`doc-rule`）功能菜单**。

只**复用**既有分层架构与既有权限模型：**不新增**任何菜单 / 角色 / 用户授权 / 表 / 列，**无匿名 / 管理员兜底**，
分页与响应契约（`ApiResponse<PagedResult<…>>` / 既有提示文案）保持不变，
`DocumentNumberService` 的默认兜底（`prefix + yyyyMMdd + 4 位流水`）与流水号自增语义完全不变。

## 2. 口径

| 项 | 口径 |
|---|---|
| 身份 | 每次请求按 `ClaimTypes.NameIdentifier` 解析：缺失 / 非正整数 → `2000` 未认证；账号不存在或已 `IsDeleted` → `2000`；`Status != Enabled` → `2002` 权限不足 |
| 菜单 | 复用**既有**「单据号规则」（`doc-rule`，与 `SeedData.Menus` 同源）：**每个账号都必须显式具备**，撤销后下一次请求立即收敛；**无特权 / 管理员兜底**（系统内置角色同样需要该既有功能菜单，绝不把空身份或特权当作管理员） |
| 保持契约 | 分页 `PagedResult` 结构、`all` / 详情 / 新增 / 修改 / 删除 / 批量删除的响应体与提示文案（新增成功 / 更新成功 / 删除成功 / 批量删除成功）均不变 |
| 编号形状校验 | 仅新增 / 修改：`DocumentType` 必须是已知单据类型，`RuleCode` 非空且不与既有未删除规则重复，`Prefix` / `Separator` / `DateFormat` 有界且日期格式为受支持形状，`SerialLength` 落在 `[1, 10]`，`CurrentSequence` 非负；一律以既有受控错误拒绝（形状 / 取值 `1001`，编码重复 `1003`） |
| 默认兜底 | 空白日期格式归一为 `null`（`NormalizeForWrite`），使其回到 `DocumentNumberService` 的 `?? "yyyyMMdd"` 既有默认兜底；不改变任何既有流水语义 |

## 3. 路由矩阵（全部先授权）

| 路由 | 先授权 | 编号形状校验 | 数据库写入 |
|---|---|---|---|
| `GET api/sys/document-number-rules` | ✅ 身份 + 状态 + 菜单 | — | — |
| `GET api/sys/document-number-rules/all` | ✅ 身份 + 状态 + 菜单 | — | — |
| `GET api/sys/document-number-rules/{id}` | ✅ 身份 + 状态 + 菜单 | — | — |
| `POST api/sys/document-number-rules` | ✅ 身份 + 状态 + 菜单 | ✅ | 校验通过后新增 |
| `PUT api/sys/document-number-rules/{id}` | ✅ 身份 + 状态 + 菜单 | ✅ | 校验通过后改写 |
| `DELETE api/sys/document-number-rules/{id}` | ✅ 身份 + 状态 + 菜单 | — | 软删除 |
| `POST api/sys/document-number-rules/batch-delete` | ✅ 身份 + 状态 + 菜单 | — | 批量软删除 |

授权判定严格先于任何实体读取、计数与 `dbContext.Add` / `SaveChangesAsync`：被拒绝的请求不加载、不写入、
不改写任何规则行，也**不消耗任何单据号**（`CurrentSequence` 不变）；`Update` 复用既有 `CopyProperties`（跳过 `IsDeleted`），
**不复活**任何已软删除行。

## 4. 编号形状有界校验（新增 / 修改）

| 字段 | 既有持久化上界 | 规则 | 拒绝错误码 |
|---|---|---|---|
| `DocumentType` | 枚举 `DocumentType`（1..23） | 必须是已知单据类型（`Enum.IsDefined`） | `1001` |
| `RuleCode` | 50 | 非空、不超上界，且不与既有未删除规则重复（修改自身不占用编码） | `1001` / `1003` |
| `RuleName` | 100 | 非空且不超上界 | `1001` |
| `Prefix` | 20 | 不超上界（允许为空） | `1001` |
| `DateFormat` | 20 | 空白归一为 `null`（走既有默认 `yyyyMMdd`）；否则不超上界且为受支持形状 | `1001` |
| `SerialLength` | 整数 | 落在 `[1, 10]` | `1001` |
| `Separator` | 5 | 不超上界（允许为空） | `1001` |
| `CurrentSequence` | `long` | 非负 | `1001` |
| `Remark` | 500 | 不超上界 | `1001` |

**日期格式受支持形状**（`DocumentNumberRuleAuthorizationRules.IsSupportedDateFormat`）：允许日期记号 `y / M / d`、
时间记号 `H / h / m / s / f / F / g / t` 与分隔符 `- / . : _` 及空格，**必须包含日期部分**（纯时间 / 纯分隔符拒绝），
并以区域性无关口径真实格式化一次（格式化异常拒绝）。因此 `yyyyMMdd`、`yyyy-MM-dd`、`yyMM`、`yyyyMMddHHmmss` 接受，
`q`、`abc`、`HH:mm:ss`、空 / 超长拒绝。

校验**只判定不改写实体**（`NormalizeForWrite` 仅对拟议入参去空白 / 空白日期格式归 `null`）：一律**拒绝而不静默截断、
删除或复活**任何规则行；被拒绝的写入不落任何行，也不消耗任何单据号。

## 5. 错误信息（API 直接返回）

| 场景 | 错误码 | 说明 |
|---|---|---|
| 无身份 / 非法身份 | `2000` | `DocumentNumberRuleAuthorizationRules.UnauthorizedText` |
| 账号不存在 / 已删除 | `2000` | `UserDeletedText` |
| 账号已禁用 | `2002` | `UserDisabledText` |
| 缺少既有「单据号规则」菜单（含特权账号缺菜单） | `2002` | `MenuDeniedText`（文案含「模块授权」） |
| 未知单据类型 / 空或超长编码 / 超长前缀 / 分隔符 / 日期格式 / 不受支持日期形状 / 越界流水位 / 负当前流水 / 超长名称 / 备注 | `1001` | `DocumentTypeUnknownText` 等受控校验文案 |
| 规则编码与既有未删除规则重复 | `1003` | `RuleCodeDuplicatedText` |

拒绝一律 fail closed，不泄露任何数据，也不降级为匿名 / 管理员。

## 6. 实现与边界

- `src/ERP.Application/Services/DocumentNumberRuleAuthorizationRules.cs`（新增）：纯判定与有界只读查询。
- `src/ERP.Api/Controllers/SysSimpleControllers.cs`：`DocumentNumberRuleController` 的七个继承路由全部先授权，
  新增 / 修改再规范化 + 校验编号形状，分页 / 响应契约不变。
- `src/ERP.Application/Services/DocumentNumberService.cs`：**不改动**（默认兜底与流水语义保持不变）。
- 不新增表 / 列 / 菜单 / 角色 / 用户授权，无 `[AllowAnonymous]`、无 `[Authorize(Roles=…)]`、无匿名 / 管理员兜底。
- 身份只来自已认证请求主体；请求提交体**不包含也不接受**任何账号 / 角色字段。
- 每次请求重新解析（不缓存），账号停用 / 删除或菜单撤销后下一次请求立即收敛。

## 7. 测试与验证

| 层级 | 文件 | 覆盖 |
|---|---|---|
| 单元（内存库） | `src/ERP.UnitTests/DocumentNumberRuleAuthorizationTests.cs`（新增） | 七个路由在缺失 / 已删除 / 禁用身份与无既有菜单身份下 fail closed 且不改写任何规则行；**特权账号缺既有菜单仍拒绝**（无管理员兜底）；撤销菜单立即收敛；已授予既有菜单的账号与具备菜单的特权账号可读可写；新增 / 修改对未知单据类型、空 / 超长 / 重复编码、超长前缀 / 分隔符 / 日期格式、不受支持日期形状、越界流水位、负当前流水返回受控错误且不落行；被拒写入不消耗单据号；源码 / 菜单契约 |
| 单元（既有回归） | `src/ERP.UnitTests/DocumentNumberServiceTests.cs`（扩展） | 既有四条契约保持；新增「规则日期格式为空仍回退 `yyyyMMdd`」与「未配置规则默认前缀 + `yyyyMMdd` + 4 位流水」两条，锁定默认兜底不变 |
| SQL 集成（GUID 独占 `NEWERP_AUTOTEST`） | `src/ERP.IntegrationTests/DocumentNumberRuleAuthorizationSqlServerTests.cs`（新增） | 真实控制器身份 / 菜单 / 撤销 / 放行（种子管理员与菜单授权操作员）/ 编号形状校验矩阵，且被拒绝时不新增 / 不改写任何规则行、不消耗任何单据号 |
| 安全 profile | `.ai/config.json` 的 `safe` | `dotnet restore` → Release 构建（`TreatWarningsAsErrors` + 分析器）→ `ERP.UnitTests` 全量 |

## 8. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且 `Integrated Security`；
  错误目标在访问数据库**之前** fail closed（`AssertDedicatedTarget`，含单元级护栏用例）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings*.json` /
  `.env` / 生产凭据。**构建完成不等于阶段验收**：只有受控 localdb 上真实执行通过才算验收证据。

## 9. 边界

- 不改变 `DocumentNumberService` 的默认兜底（`prefix + yyyyMMdd + 4 位流水`）与既有流水号自增语义。
- 不新增任何用户授权，也不把空身份或特权当作管理员。
- 授权与校验只做纯判定与有界只读查询，绝不落库、绝不改写历史单据号规则。
