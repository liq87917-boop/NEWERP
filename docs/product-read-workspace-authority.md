# 商品只读工作台的实时授权（ERP-461）

商品资料页工具栏打开的两个**只读**工作区：

- 商品图片库 `/api/base/product-images`（ERP-039）
- 出口字段完整度 `/api/base/products/export-field-completeness`（ERP-107）

它们是与 `/api/base/products` **并列**的独立路由。此前两个控制器只有 `[ApiController]` /
`[Route(...)]` / `[Authorize]`，未解析身份、账号状态或任何菜单，因此任何已认证账号都能经由这两条
兄弟路由枚举整张商品主表（绕过 ERP-452 对 `api/base/products` 的「商品资料」菜单护栏）。

本任务为两条并列路由补上**与 `api/base/products` 完全同源**的实时授权，且两个工作台保持**严格只读**。

## 1. 授权（fail closed，精确复用既有功能菜单）

`ProductReadWorkspaceAuthorizationRules.EnsureAuthorizedAsync` 在**每一条**路由读取任何
`BaseProducts` 行**之前**执行，并**直接复用** `ProductAuthorizationRules.EnsureAuthorizedAsync`：

> 实时身份 → 账号状态 → 既有「商品资料」（`product`）功能菜单授权

| 场景 | 判定结果 |
|---|---|
| 无身份 / 非法用户 Id | `2000` 未认证 |
| 账号不存在或已删除 | `2000` 未认证 |
| 账号已禁用 | `2002` 权限不足 |
| 无角色 / 缺少既有 `product` 菜单授权（含被撤销） | `2002` 权限不足 |
| 具备既有 `product` 菜单 | 放行既有只读契约 |

- 复用既有「角色 → 菜单」口径（`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`）；
  **每次请求重新查询**，撤销授权后下一次请求立即收敛（绝不缓存）。
- **不新增**任何表 / 列 / 实体 / 菜单 / 权限 / 用户授权；**无匿名 / 管理员回退**，绝不把空身份当作管理员。
- 授权是**无条件**的：绝不依赖 `Request.Path`、环境、假身份或空请求绕过判定
  （与 `ProductVariantController` 的 ERP-444 口径同源；区别于 `ProductController` 的进程内直调边界）。
- 既有 ERP-452 的 `api/base/products` 契约**完全不变**：本任务只新增两条并列路由的读取前判定。

## 2. 严格只读

- 两个控制器都**只有 GET 端点**：没有任何 `Add` / `Update` / `Remove` / `SaveChanges`、没有任意 SQL
  （无 `FromSql`）、没有 OSS 服务依赖或图片抓取。
- 被拒绝的请求在读取任何 `BaseProducts` 行之前即 fail closed：不返回任何商品编码 / 名称 /
  图片引用 / 字段完整度状态，也不写 `BaseProducts` 或任何其它表。
- 商品身份 / 字段、ERP-039 图片库与 ERP-107 出口字段完整度的响应契约逐字段不变。

## 3. 单元测试（`src/ERP.UnitTests/ProductImageLibraryAuthorizationTests.cs`，内存库）

| 用例 | 断言 |
|---|---|
| 缺失 / 禁用 / 已删除 / 无菜单（`[Theory]`） | 两条并列路由逐一拒绝（`2000` / `2002`），`BaseProducts` 快照逐字节不变 |
| 被拒绝请求不读取商品 | 数据集访问记录不含 `BaseProducts` 且为空，零 `SaveChanges` |
| 具备既有 `product` 菜单 | 两个只读工作台的既有读契约放行 |
| 请求之间撤销菜单授权 | 下一次请求 `2002`，零写入 |
| 源码 / 菜单契约 | 两个控制器每条路由先无条件授权、复用既有 `product` 菜单常量、不新增菜单、无 `AllowAnonymous` / 角色回退 / `Request.Path` 依赖；路由模板与 ERP-452 契约不变 |

`src/ERP.UnitTests/ProductImageLibraryTests.cs`、`ProductExportFieldCompletenessTests.cs` 的既有读契约用例
已适配为在隔离的内存上下文中播种一个启用且已授予既有 `product` 菜单的身份，并在授权身份下重跑，
既有 ERP-039 / ERP-107 断言保持绿；`ProductMasterAuthorizationTests.cs`（ERP-452）新增一条并列路由
复用同一菜单、不新增菜单的契约用例，其余契约保持绿。

## 4. 真实 SQL Server 集成测试（`src/ERP.IntegrationTests/ProductReadWorkspaceAuthorizationSqlServerTests.cs`）

在 GUID 独占的 `NEWERP_AUTOTEST` 目标库上以**真实控制器 + 真实既有授权**自包含播种商品与真实授权账号：

| 场景 | 断言 |
|---|---|
| 缺失 / 禁用 / 已删除 / 无菜单（`[Theory]`） | 两条并列路由对应错误码；`BaseProducts` 快照逐字节不变 |
| 具备既有 `product` 菜单 | 两条工作台的既有只读契约全部符合既有契约 |
| 特权种子管理员（既有种子已授予全部菜单含 `product`） | 既有只读契约放行，不因特权跳过菜单检查 |
| 请求之间撤销菜单授权 | 下一次读取均 `2002`，商品行未改写 |

**真实 SQL 夹具安全口径**

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，且 `IntegratedSecurity=true`；
  实例名 / 库名前缀 / 集成安全在**访问数据库之前**校验（`AssertDedicatedTarget`，含单元级护栏用例）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；**绝不 drop / reset / 复用**任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings` / `.env` / 生产凭据。

## 5. 未执行 / 限制

- 本任务 `validation_profile = safe`、`completion_mode = build`：交付门槛为 **Release 构建 + 全量 `ERP.UnitTests`**；
  真实 SQL 集成用例在具备专用 localdb 的环境下运行，**构建通过不等于阶段验收通过**。
- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置，
  未新增任何表 / 列 / 实体 / 菜单 / 权限 / 连接字符串或密钥。
