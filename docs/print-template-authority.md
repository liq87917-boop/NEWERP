# 打印模板的实时授权与持久化列有界校验（ERP-450）

> 阶段 3 · 文档输出收口 · 复用既有「样式设计」（print-design）功能菜单与既有 (BillType, TemplateName) 唯一索引与默认标记语义

## 1. 目标

`api/sys/print-templates`（`PrintTemplateController`）渲染**全部运营单据**（销售订单 / 采购订单 / 报价单 /
形式发票 PI / 库存与库存单据 / 装柜与单证）。该控制器此前只有 `[Authorize]`：清单 / 默认模板 / 保存 / 删除 /
Excel 导出 / Excel 导入对 `SysPrintTemplates` 的读写**零身份 / 零菜单 / 零取值校验**，任意已认证账号即可列出、
改写、导出与导入打印布局；`Save` 还会把越界 `FontSize` **静默改写**为 12，并静默截断其它外观列。

本改动把该路由纳入与其它模块一致的实时身份 + 既有功能菜单护栏，并为保存补充**持久化列有界校验**：
**读取 / 写入任何模板行之前**先解析 **实时身份 → 账号状态 → 既有「样式设计」（`print-design`）功能菜单**，
保存时再校验既有持久化列边界与受支持字号 —— **拒绝而不静默改写**。

只**复用**既有分层架构与既有权限模型：**不新增**任何菜单 / 角色 / 用户授权 / 表 / 列 / 实体，**无匿名 / 管理员兜底**，
既有 `(BillType, TemplateName)` 唯一索引与默认标记（同类型互斥默认）语义保持不变。

## 2. 口径

| 项 | 口径 |
|---|---|
| 身份 | 每次请求按 `ClaimTypes.NameIdentifier` 解析：缺失 / 非正整数 → `2000` 未认证；账号不存在或已 `IsDeleted` → `2000`；`Status != Enabled` → `2002` 权限不足 |
| 菜单 | 复用**既有**「样式设计」（`print-design`，与 `SchemaUpgrader` 第 5 / 6 步同源）：**每个账号都必须显式具备**，撤销后下一次请求立即收敛；**无特权 / 管理员兜底** |
| 保持契约 | 清单 / 默认模板 / 保存 / 删除的响应体与提示文案（`保存成功` / `删除成功`）、`(BillType, TemplateName)` 唯一索引与默认标记语义均不变 |
| 保存校验 | `BillType` 必须在既有可打印单据类型目录内，`TemplateName` 非空且有界，其余文本 / 颜色列有界且为已知格式，三个字号在受支持范围内；一律以既有受控错误 `1001` 拒绝 |
| 拒绝副作用 | 被拒绝的读取 / 保存 / 删除 / 导出 / 导入不加载、不写入、不改写任何模板行，也不产生任何下载构件 |

## 3. 路由矩阵（全部先授权）

| 路由 | 先授权 | 持久化列校验 | 数据库写入 / 构件 |
|---|---|---|---|
| `GET api/sys/print-templates` | ✅ 身份 + 状态 + 菜单 | — | — |
| `GET api/sys/print-templates/{billType}` | ✅ 身份 + 状态 + 菜单 | — | — |
| `POST api/sys/print-templates` | ✅ 身份 + 状态 + 菜单 | ✅ | 校验通过后新增 / 改写 |
| `DELETE api/sys/print-templates/{id}` | ✅ 身份 + 状态 + 菜单 | — | 校验通过后软删除 |
| `GET api/sys/print-templates/{id}/export-excel` | ✅ 身份 + 状态 + 菜单 | — | 校验通过后返回 `.xlsx` |
| `POST api/sys/print-templates/import-excel` | ✅ 身份 + 状态 + 菜单 | — | 校验通过后解析并返回布局 JSON（不落库） |

授权判定严格先于任何实体读取与 `dbContext.Add` / `SaveChangesAsync`：被拒绝的**真实 HTTP 请求**不加载、不写入、
不改写任何模板行，也不产生任何下载构件（进程内直调边界见第 6 节）。

## 4. 保存的持久化列有界校验

| 字段 | 既有持久化上界 | 规则 | 拒绝错误码 |
|---|---|---|---|
| `BillType` | 50 | 非空、属于既有可打印单据类型目录（`BillProcController.BillTitles` + 基础资料 / 报价单 / PI / 单证中心） | `1001` |
| `TemplateName` | 100 | 非空且有界 | `1001` |
| `Title` / `CompanyName` | 200 | 有界（允许为空） | `1001` |
| `CompanyAddress` | 300 | 有界（允许为空） | `1001` |
| `CompanyPhone` | 100 | 有界（允许为空） | `1001` |
| `PaperSize` | 20 | 空白回落 `A4`；非空必须是 `A4 / A5 / A4-L / 80mm` | `1001` |
| `FieldKeys` | 2000 | 有界；非空必须是字段键的 JSON 字符串数组 | `1001` |
| `FooterText` | 500 | 有界（允许为空） | `1001` |
| `FontFamily` | 50 | 空白回落 `Microsoft YaHei`；非空有界 | `1001` |
| 颜色列（`TitleColor` / `CompanyColor` / `TextColor` / `HeaderBgColor` / `BorderColor`） | 20 | 空白回落默认色；非空必须是 `#RGB` / `#RRGGBB` | `1001` |
| `FontSize` | — | `8 ~ 24`（**不再静默改写为 12**） | `1001` |
| `TitleFontSize` | — | `8 ~ 48`（**不再静默 clamp**） | `1001` |
| `CompanyFontSize` | — | `8 ~ 48`（**不再静默 clamp**） | `1001` |

写入前 `NormalizeForWrite` 只对拟议入参去空白并对空白纸张 / 字体族 / 对齐 / 边框样式回落受支持默认值，
**不静默截断 / 改写**任何已存储行；被拒绝的保存不落任何行，既有模板与默认标记不变。

## 5. 错误信息（API 直接返回）

| 场景 | 错误码 | 说明 |
|---|---|---|
| 无身份 / 非法身份 | `2000` | `PrintTemplateAuthorizationRules.UnauthorizedText` |
| 账号不存在 / 已删除 | `2000` | `UserDeletedText` |
| 账号已禁用 | `2002` | `UserDisabledText` |
| 缺少既有「样式设计」菜单 | `2002` | `MenuDeniedText`（文案含「模块授权」） |
| 非法单据类型 / 空 / 超长名称 / 越界字号 / 非法颜色 / 非法字段顺序 / 非法纸张 / 超长文本 | `1001` | 受控校验文案 |

拒绝一律 fail closed，不泄露任何数据，也不降级为匿名 / 管理员。

## 6. 实现与边界

- `src/ERP.Application/Services/PrintTemplateAuthorizationRules.cs`（新增）：纯判定与有界只读查询。
- `src/ERP.Api/Controllers/PrintTemplateController.cs`：六个路由全部先授权；保存规范化 + 校验后再写入。
- `src/ERP.Domain/Entities/SysPrintTemplate.cs`：**不改动**（既有 `[MaxLength]` 即校验上界来源）。
- 不新增表 / 列 / 菜单 / 角色 / 用户授权（`SchemaUpgrader` / `SeedData` **不改动**），无 `[AllowAnonymous]`、
  无 `[Authorize(Roles=…)]`、无匿名 / 管理员兜底。
- 身份只来自已认证请求主体；请求提交体**不包含也不接受**任何账号 / 角色字段。
- **进程内直调边界**（`RequiresLiveAuthorization()`，与 `EmployeeController` / `SalesOrderChangeRequestController`
  以及既有仓库 / 供应商 / 采购订单模块同源）：**真实 HTTP 请求**（MVC 绑定，`Request.Path` 已赋值）一律执行授权，
  **真实匿名请求因处于请求管线内一律 fail closed（未认证）**；仅「既无任何登录身份、又不在 HTTP 请求管线内」的
  **进程内直接调用**（历史单元测试 / 内部派生读取）沿用既有语义 —— 这类调用不可能由外部请求到达，
  也绝不把缺失身份当作管理员，绝不提供可被外部到达的测试专用降级。
- 每次请求重新解析（不缓存），账号停用 / 删除或菜单撤销后下一次请求立即收敛。

## 7. 测试与验证

| 层级 | 文件 | 覆盖 |
|---|---|---|
| 单元（内存库） | `src/ERP.UnitTests/PrintTemplateAuthorizationTests.cs`（新增） | 六个路由在缺失 / 已删除 / 禁用身份与无既有菜单身份下 fail closed 且不改写任何模板行；撤销菜单立即收敛；已授予既有菜单可读可写可删可导出；保存对非法单据类型 / 空 / 超长名称 / 越界字号 / 非法颜色 / 非法字段顺序 / 非法纸张 / 超长文本返回受控错误，且越界字号**不再静默改写**既有模板与默认标记 |
| 单元（既有回归） | `src/ERP.UnitTests/PrintTemplateTests.cs`（**不改动**） | 既有 5 条契约保持：这些用例为进程内无身份直调（无 HTTP 请求管线），沿用既有免授权语义，不新增 / 不改写测试文件 |
| SQL 集成（GUID 独占 `NEWERP_AUTOTEST`） | `src/ERP.IntegrationTests/PrintTemplateAuthorizationSqlServerTests.cs`（新增） | 真实控制器身份 / 菜单 / 撤销 / 放行（种子管理员与菜单授权操作员）/ 持久化列校验矩阵，且被拒绝时不新增 / 不改写任何模板行、不产生下载构件；专用目标护栏单元级用例 |
| 安全 profile | `.ai/config.json` 的 `safe` | `dotnet restore` → Release 构建（`TreatWarningsAsErrors` + 分析器）→ `ERP.UnitTests` 全量 |

## 8. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且 `Integrated Security`；
  错误目标在访问数据库**之前** fail closed（`AssertDedicatedTarget`，含单元级护栏用例）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings*.json` /
  `.env` / 生产凭据。**构建完成不等于阶段验收**：只有受控 localdb 上真实执行通过才算验收证据。

## 9. 边界

- 不改变既有 `(BillType, TemplateName)` 唯一索引与默认标记（同类型互斥默认）语义，也不改变既有打印字段契约与渲染输出。
- 不新增任何用户授权，也不把空身份或特权当作管理员。
- 授权与校验只做纯判定与有界只读查询，绝不落库、绝不改写历史打印模板。
