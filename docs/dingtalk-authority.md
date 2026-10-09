# 钉钉通知的实时授权与白名单配置取值护栏（ERP-460）

> 阶段 3 · 访问控制收口 · 复用既有「钉钉通知配置」「钉钉发送记录」功能菜单

## 1. 目标

`api/sys/dingtalk`（`DingTalkController`）此前只有 `[Authorize]`，配置读取 / 保存 / 测试发送 / 手动推送提醒与
发送记录列表 / 重发 / 删除**零身份、零菜单**：任意已认证账号即可读取共享 Webhook 与加签密钥、改写通知配置、
向任意地址发送消息、重发或软删除发送记录。`GetConfig` 直接返回 `DingTalk_Webhook` / `DingTalk_Secret` 明文，
`Test` 可向调用方自带的 Webhook 发送消息，`Resend` / `DeleteLog` 可驱动或清理发送记录。

本改动把全部**七个路由**纳入与其它模块一致的实时身份 + 既有功能菜单护栏，并为配置保存补充**白名单键**与
既有持久化列的有界校验：**读取配置、发送消息或读取 / 重发 / 软删除任何发送记录之前**先解析
**实时身份 → 账号状态 → 既有功能菜单**（配置侧 `dingtalk-config`、记录侧 `dingtalk-log`）。

只**复用**既有分层架构与既有权限模型：**不新增**任何菜单 / 角色 / 用户授权 / 表 / 列，**无匿名 / 管理员兜底**，
既有响应契约（`ApiResponse` 提示文案与失败响应体）与发送记录**脱敏写入** / **软删除**语义保持不变。

## 2. 口径

| 项 | 口径 |
|---|---|
| 身份 | 每次请求按 `ClaimTypes.NameIdentifier` 解析：缺失 / 非正整数 → `2000` 未认证；账号不存在或已 `IsDeleted` → `2000`；`Status != Enabled` → `2002` 权限不足 |
| 菜单 | 复用**既有**「钉钉通知配置」（`dingtalk-config`）与「钉钉发送记录」（`dingtalk-log`，与 `SchemaUpgrader` 同源）：**每个账号都必须显式具备**，撤销后下一次请求立即收敛；**无特权 / 管理员兜底** |
| 保持契约 | `GetConfig` 返回全部配置键；`SaveConfig` / `DeleteLog` 返回既有成功提示；`Test` / `Resend` / `SendFollowUpReminder` 的失败响应体（`ApiResponse.Fail`）不变 |
| 白名单校验 | 保存只接受 `DingTalk_Enabled / DingTalk_Webhook / DingTalk_Secret / DingTalk_MsgType / DingTalk_AtMobiles / DingTalk_AtAll / DingTalk_Rules / DingTalk_Template`，且每个值不超 `SysParameter.ParamValue` 上界（500） |
| 保存语义 | 既有白名单保存语义不变：提交的键缺失即建、已有即改写（软删除行不复用），未提交的键不动 |
| 脱敏 / 软删除 | 发送记录写入仍脱敏（`MaskWebhook`）；删除仍为 `IsDeleted` 软删除，且**先校验记录存在且未删除** |

## 3. 路由矩阵（全部先授权）

| 路由 | 所需既有菜单 | 校验 | 副作用 |
|---|---|---|---|
| `GET api/sys/dingtalk/config` | `dingtalk-config` | — | — |
| `POST api/sys/dingtalk/config` | `dingtalk-config` | 白名单键 + 值长度 | 校验通过后写入 `SysParameter` |
| `POST api/sys/dingtalk/test` | `dingtalk-config` | — | 校验通过后按既有语义发送 |
| `POST api/sys/dingtalk/follow-up-reminder` | `dingtalk-config` | — | 校验通过后按既有语义推送 |
| `GET api/sys/dingtalk/logs` | `dingtalk-log` | — | — |
| `POST api/sys/dingtalk/logs/{id}/resend` | `dingtalk-log` | — | 校验通过后按既有语义重发 |
| `DELETE api/sys/dingtalk/logs/{id}` | `dingtalk-log` | 记录存在且未删除 | 校验通过后软删除 |

授权判定严格先于任何配置读取、消息发送与记录读取 / 重发 / 软删除：被拒绝的请求不加载、不写入、不改写任何
`SysParameter` 或 `SysDingTalkLog` 行，也不返回任何 Webhook / 访问令牌 / 加签密钥。

## 4. 白名单 + 有界校验（保存）

| 字段 | 既有来源 | 规则 | 拒绝错误码 |
|---|---|---|---|
| 配置键 | `DingTalkService.AllKeys` | 必须是既有 8 个白名单键之一（精确匹配），否则拒绝 | `1001` |
| 配置值 | `SysParameter.ParamValue` `MaxLength(500)` | 不超 500（允许为空），否则拒绝 | `1001` |

校验在写入任何配置行**之前**整体完成（先校验后写入），任一键 / 值不满足即拒绝：
**绝不静默截断、绝不改写、绝不回声提交的敏感取值**；被拒绝的保存不落任何 `SysParameter` 行。
发送记录删除前校验目标记录**存在且未删除**（`NotFound`），被拒绝的删除不产生任何 `SysDingTalkLog` 变更。

## 5. 错误信息（API 直接返回）

| 场景 | 错误码 | 说明 |
|---|---|---|
| 无身份 / 非法身份 | `2000` | `DingTalkAuthorizationRules.UnauthorizedText` |
| 账号不存在 / 已删除 | `2000` | `UserDeletedText` |
| 账号已禁用 | `2002` | `UserDisabledText` |
| 缺少既有 `dingtalk-config` 菜单（含特权账号缺菜单） | `2002` | `ConfigMenuDeniedText`（文案含「模块授权」） |
| 缺少既有 `dingtalk-log` 菜单（含特权账号缺菜单） | `2002` | `LogMenuDeniedText`（文案含「模块授权」） |
| 保存提交非白名单键 / 超长取值 | `1001` | `UnknownConfigKeyText` / `ParamValueTooLongText` |
| 删除不存在 / 已软删除记录 | `1002` | `记录不存在` |

拒绝一律 fail closed，不泄露任何数据，也不降级为匿名 / 管理员。

## 6. 实现与边界

- `src/ERP.Application/Services/DingTalkAuthorizationRules.cs`（新增）：纯判定与有界只读查询。
- `src/ERP.Api/Controllers/DingTalkController.cs`：七个路由全部先授权（配置侧 4 个、记录侧 3 个），保存再校验白名单 + 值长度。
- `src/ERP.Api/Services/DingTalkService.cs`：**不改动**（白名单保存语义、脱敏写入、加签与发送口径保持不变）。
- `src/ERP.Infrastructure/Data/SchemaUpgrader.cs`：**不改动**（既有 `dingtalk-config` / `dingtalk-log` 菜单与幂等授权保持不变）。
- 不新增表 / 列 / 菜单 / 角色 / 用户授权，无 `[AllowAnonymous]`、无 `[Authorize(Roles=…)]`、无匿名 / 管理员兜底。
- 身份只来自已认证请求主体；请求提交体**不包含也不接受**任何账号 / 角色字段；绝不依据 `Request.Path` / 环境 / 空请求绕过检查。
- 每次请求重新解析（不缓存），账号停用 / 删除或菜单撤销后下一次请求立即收敛。

## 7. 测试与验证

| 层级 | 文件 | 覆盖 |
|---|---|---|
| 单元（内存库） | `src/ERP.UnitTests/DingTalkAuthorizationTests.cs`（新增） | 七个路由在缺失 / 已删除 / 禁用身份与无既有菜单身份下 fail closed 且不改写任何行；**特权账号缺既有菜单仍拒绝**（无管理员兜底）；撤销菜单立即收敛；已授予既有菜单可读可写 / 重发 / 软删除；保存对未知键、白名单键与未知键混合、超长取值拒绝且不落任何配置行；删除不存在 / 已软删除记录拒绝；被拒绝的读取不泄露 Webhook / 加签密钥；源码 / 白名单 / 菜单契约 |
| 单元（既有回归） | `src/ERP.UnitTests/SystemParameterAuthorizationTests.cs` | 既有系统参数契约保持绿（本改动未触及 `ParameterController` / `SystemParameterAuthorizationRules`） |
| SQL 集成（GUID 独占 `NEWERP_AUTOTEST`） | `src/ERP.IntegrationTests/DingTalkAuthorizationSqlServerTests.cs`（新增） | 真实控制器身份 / 菜单 / 撤销 / 放行（种子管理员与菜单授权操作员）/ 白名单与有界取值校验 / 记录存在性，且被拒绝时不新增 / 不改写 / 不软删除任何行 |
| 安全 profile | `.ai/config.json` 的 `safe` | `dotnet restore` → Release 构建 → `ERP.UnitTests` 全量 |

## 8. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且 `Integrated Security`；
  错误目标在访问数据库**之前** fail closed（`AssertDedicatedTarget`，含单元级护栏用例）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings*.json` /
  `.env` / 生产凭据。**构建完成不等于阶段验收**：只有受控 localdb 上真实执行通过才算验收证据。

## 9. 边界

- 不改变钉钉配置既有白名单保存语义（缺失项即建、已有项改写、软删除行不复用）。
- 不改变发送记录脱敏写入与软删除语义；不引入新的菜单 / 权限 / 授权，也不把空身份或特权当作管理员。
- 授权与校验只做纯判定与有界只读查询；被拒绝的请求绝不落库、绝不发送消息、绝不改写历史发送记录。

