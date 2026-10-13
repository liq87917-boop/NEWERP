# 打印模板的实时授权与持久化列有界校验（ERP-450 / ERP-493）

> 阶段 3 · 文档输出收口 · 复用既有「样式设计」（print-design）功能菜单与既有 (BillType, TemplateName) 唯一索引与默认标记语义

## 1. 目标

`api/sys/print-templates`（`PrintTemplateController`）渲染**全部运营单据**（销售订单 / 采购订单 / 报价单 /
形式发票 PI / 库存与库存单据 / 装柜与单证）。该控制器此前只有 `[Authorize]`：清单 / 默认模板 / 保存 / 删除 /
Excel 导出 / Excel 导入对 `SysPrintTemplates` 的读写**零身份 / 零菜单 / 零取值校验**，任意已认证账号即可列出、
改写、导出与导入打印布局；`Save` 还会把越界 `FontSize` **静默改写**为 12，并静默截断其它外观列。

本改动把该路由纳入与其它模块一致的实时身份 + 既有功能菜单护栏，并为保存补充**持久化列有界校验**：
**读取 / 写入任何模板行之前**先解析 **实时身份 → 账号状态 → 既有「样式设计」（`print-design`）功能菜单**，
保存时再校验既有持久化列边界与受支持字号 —— **拒绝而不静默改写**。

ERP-493（同缺陷类）：原控制器用「请求形状探针」`RequiresLiveAuthorization()`（依据 `Request.Path.HasValue`
或是否携带身份判定是否需要授权）包住授权调用，导致**未绑定请求上下文或空 `Request.Path` 的调用绕过实时身份 /
账号状态 / 既有菜单判定**。该探针与它的条件护栏已**彻底移除**：授权在**每一个入口无条件**执行，与请求路径 /
请求形状 / 是否绑定 `HttpContext` 完全无关（见第 6 节，契约见
`PrintTemplateAuthorizationRules.PathIndependenceText`）。

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

授权判定严格先于任何实体读取与 `dbContext.Add` / `SaveChangesAsync`：被拒绝的请求不加载、不写入、
不改写任何模板行，也不产生任何下载构件 —— 与请求路径是否赋值、请求形状、是否绑定 `HttpContext` 完全无关
（ERP-493 已移除请求形状探针，见第 6 节）。

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
- **与请求形状无关（ERP-493）**：授权在**每一个入口无条件**执行 —— 空 `Request.Path`、已赋值 `Request.Path`
  与完全未绑定 `HttpContext` 的纯进程内直调对同一身份给出**完全相同**的判定（缺失 / 为零 / 未知 / 已删除身份
  一律未认证，禁用 / 撤销或缺少既有菜单一律权限不足）。原「请求形状探针」`RequiresLiveAuthorization()`
  与 `if (RequiresLiveAuthorization())` 条件护栏已**彻底移除**：不存在任何进程内直调豁免、匿名 / 管理员回退、
  环境变量 / 数据库提供程序或测试专用放行开关。契约见 `PrintTemplateAuthorizationRules.PathIndependenceText`。
- 每次请求重新解析（不缓存），账号停用 / 删除或菜单撤销后下一次请求立即收敛。

## 7. 测试与验证

| 层级 | 文件 | 覆盖 |
|---|---|---|
| 单元（内存库） | `src/ERP.UnitTests/PrintTemplateAuthorizationTests.cs` | 六个路由在缺失 / 为零 / 未知 / 已删除身份与无既有菜单身份下 fail closed 且不改写任何模板行；撤销菜单立即收敛；已授予既有菜单可读可写可删可导出；空路径 / 已赋值路径 / 未绑定 `HttpContext` 判定完全一致；控制器源码契约断言请求形状探针与 `Request.Path` 已移除；保存对非法单据类型 / 空 / 超长名称 / 越界字号 / 非法颜色 / 非法字段顺序 / 非法纸张 / 超长文本返回受控错误，且越界字号**不再静默改写**既有模板与默认标记 |
| 单元（既有回归，ERP-493 适配夹具） | `src/ERP.UnitTests/PrintTemplateTests.cs` | 既有 5 条契约保持：夹具改为绑定隔离内存测试库中的**真实启用身份**，该身份持有既有 `print-design` 菜单授权（与生产「角色 → 菜单」授予口径同源），不再依赖任何请求路径 / 免授权直调 |
| 单元（既有单证打印回归，ERP-493 适配夹具） | `src/ERP.UnitTests/TradeDocumentPrintTests.cs` | 单证中心打印模型 / 打印接口 / 前端接线断言**全部保持**：其中 3 条驱动 `PrintTemplateController` 的既有夹具改为绑定隔离内存测试库中的**真实启用身份**（持有既有 `print-design` 菜单授权，与 `PrintTemplateTests` 完全同一授予口径；默认空 `Request.Path`），不再依赖任何免授权直调 |
| SQL 集成（GUID 独占 `NEWERP_AUTOTEST`） | `src/ERP.IntegrationTests/PrintTemplateAuthorizationSqlServerTests.cs` | 真实控制器身份 / 菜单 / 撤销 / 放行（种子管理员与菜单授权操作员）/ 请求形状无关 / 持久化列校验矩阵，且被拒绝时不新增 / 不改写任何模板行、不产生下载构件；专用目标护栏单元级用例 |
| 安全 profile | `.ai/config.json` 的 `safe` | `dotnet restore` → Release 构建（`TreatWarningsAsErrors` + 分析器）→ `ERP.UnitTests` 全量 |

> **本轮（ERP-493 范围修复后）的实测证据**：Release 构建（`/p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true`）
> 为 **0 警告 / 0 错误**；全量 `ERP.UnitTests` 为**通过 7968 / 失败 0 / 总计 7968**（含此前失败的 3 条
> `TradeDocumentPrintTests` 打印模板夹具，现已改为绑定真实启用身份 + 既有 `print-design` 菜单授权）。
> 授权修复在被拒入口上的效果与既有失败原因一致：无身份调用抛 `BusinessException 2000`（`请先登录后再访问打印模板`），
> 即模板行**未被读取、未被写入**。**构建 + 单元全绿不等于阶段验收**：阶段验收仍需第 8 节受控 localdb 上的真实执行证据。

## 8. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且 `Integrated Security`；
  错误目标在访问数据库**之前** fail closed（`AssertDedicatedTarget`，含单元级护栏用例）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings*.json` /
  `.env` / 生产凭据。**构建完成不等于阶段验收**：只有受控 localdb 上真实执行通过才算验收证据。
- **本轮实测**：`dotnet test src/ERP.IntegrationTests -c Release --no-build --filter FullyQualifiedName~PrintTemplateAuthorization`
  在 `(localdb)\NEWERP_AutoAcceptance` 上真实执行 —— 目标护栏放行后创建**全新 GUID 后缀库**
  （`NEWERP_AUTOTEST_PRINTTEMPLATE_20261009_<guid>`，存在同名库即拒绝，永不 drop / reset），
  共 **通过 15 / 失败 0**（10 条真实控制器身份 / 菜单 / 请求形状 / 取值矩阵 + 5 条专用目标护栏），
  含 `Live_no_http_context_direct_calls_are_unauthenticated_without_mutation` 与
  `Live_request_shape_does_not_change_decisions`。**这不是阶段验收**：阶段验收仍需第 9 节的允许路径缺口收敛。

## 9. 既有单证打印夹具的范围修复（已完成）

实时授权改为**无条件执行**后，仍以「进程内直调且不携带实时身份」的方式驱动打印模板控制器的既有夹具会按新口径被
**正确拒绝**（这正是不再存在请求形状旁路的结果）。该夹具文件 `src/ERP.UnitTests/TradeDocumentPrintTests.cs` 已由
调度器**授权列入 `allowed_paths`**，并已按与第 7 节 `PrintTemplateTests` **完全同源**的口径完成适配：在隔离测试数据里
播种已启用身份 + 既有 `print-design` 菜单 + 角色授予，再以该身份驱动控制器（默认空 `Request.Path`）。**未新增任何
生产菜单 / 权限 / 用户授权**，也未引入任何测试专用放行。

| 夹具用例（已适配） | 适配前触发点 | 适配后 |
| --- | --- | --- |
| `TradeDocumentPrintTests.打印模板_单证中心内置默认模板_使用中文标题` | `new PrintTemplateController(db).GetDefault("doc-center")` | `NewPrintTemplateController(db)`（真实启用身份 + 既有菜单） |
| `TradeDocumentPrintTests.打印模板_既有可打印类型标题不受单证中心登记影响` | `new PrintTemplateController(db).GetDefault(...)` | 同上 |
| `TradeDocumentPrintTests.打印模板_保存单证中心模板_不影响其他单据模板` | `new PrintTemplateController(db).Save(...)` | 同上 |

单证打印的**业务 / 输出断言全部保留**（打印模型字段映射与顺序、缺值空白语义、单表台账无明细行、打印接口投影与
只读性、软删除不可打印、受限范围越界拒绝、前端接线契约），仅夹具的身份绑定被替换为经授权的真实身份。

**拒绝放宽的口径**：为迁就这些夹具而保留「空 `Request.Path` / 未绑定 `HttpContext` / 无身份即跳过授权」的旁路，
或按其未授权身份放行，都会重新引入本任务要修复的请求形状旁路（ERP-450 的缺口类），因此本任务**不**采用任何
此类降级，也不把缺失身份当作匿名或管理员。

## 10. 边界

- 不改变既有 `(BillType, TemplateName)` 唯一索引与默认标记（同类型互斥默认）语义，也不改变既有打印字段契约与渲染输出。
- 不新增任何用户授权，也不把空身份或特权当作管理员。
- 授权与校验只做纯判定与有界只读查询，绝不落库、绝不改写历史打印模板。

