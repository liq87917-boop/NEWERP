# 费用科目的实时授权与有界字段校验（ERP-498）

费用科目端点（`/api/base/expense-accounts` 的分页、全部、按主键读取、新增、修改、删除、批量删除）在读取或写入
**任何** `BaseExpenseAccounts` 行之前，统一重新解析：

> 实时身份 → 账号状态 → 既有「费用科目」（`expense-account`）功能菜单授权

任一缺失即 **fail closed**，绝不返回、新增或改写任何费用科目行。新增 / 修改另在落库**之前**做有界字段校验、
「科目编码未删除行内唯一」与「父级科目必须存在且未删除」判定。费用单 / 费用分摊与打印快照所解析的费用科目主数据，
因此不再能被任意已认证账号读取、擅自新增或软删除。

## 1. 修复前的证据与缺陷类

`src/ERP.Api/Controllers/BaseDataControllers.cs` 的 `ExpenseAccountController` 在修复前只有
`[ApiController]` / `[Route("api/base/expense-accounts")]` / `[Authorize]`，没有任何 override：
全部分页 / 全部 / 按主键 / 新增 / 修改 / 删除 / 批量删除都从
`src/ERP.Api/Controllers/BaseCrudController.cs` 无护栏继承，直接调用 `IGenericService`，
零身份 / 账号状态 / 菜单判定。这与同一文件中已修复的 **ERP-451**（客户）、**ERP-447**（供应商）、
**ERP-449**（员工）、**ERP-448**（仓库）、**ERP-452**（商品）属**同一缺陷类**（阶段 3 主数据授权缺口），
并与 **ERP-443**（其他资料）、**ERP-457**（客户端限制）、**ERP-467**（主数据路径无关）同源。

## 2. 授权（fail closed，精确复用既有功能菜单）

`ExpenseAccountAuthorizationRules.EnsureAuthorizedAsync` 在**每一个**路由的最前面执行（先于任何
`IGenericService` 读取 / 写入，也先于 `BaseExpenses` 计数、行读取、唯一性探测与写入）：

| 场景 | 判定结果 |
|---|---|
| 无身份 / 非法用户 Id（含非正 / 未知） | `2000` 未认证 |
| 账号不存在或已删除 | `2000` 未认证 |
| 账号已禁用 | `2002` 权限不足 |
| 无角色 / 缺少既有 `expense-account` 菜单授权（含被撤销） | `2002` 权限不足 |
| 具备既有 `expense-account` 菜单 | 放行既有读 / 写契约 |

- 菜单授权复用既有「角色 → 菜单」口径（`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`）；
  **每次请求重新查询**，撤销授权后下一次请求立即收敛（绝不缓存）。
- **不新增**任何表 / 列 / 实体 / 菜单 / 权限 / 用户授权；**无匿名 / 管理员回退**，绝不把空身份当作管理员。
- 判定与请求路径 / 请求形状 / 是否绑定 `HttpContext` **完全无关**（`PathIndependenceText`，沿用 ERP-467）：
  空路径与已赋值路径口径完全一致，未绑定任何请求上下文的纯进程内直调同样执行本护栏。
- 既有分页 / 全部 / 按主键 / 新增 / 修改 / 删除 / 批量删除路由与响应契约完全不变，
  只是每个路由在读写前多了一道实时授权判定。

## 3. 新增 / 修改前的有界字段校验（不夹取、不改写）

`ExpenseAccountAuthorizationRules.Validate` 在**落库之前**校验持久化列的可存储范围，任一项不满足即按
`1001` 参数错误拒绝；`EnsureWritableAsync` 另校验唯一性与父级引用（重复编码 `1003` 数据重复）。
被拒绝的写入**不落任何** `BaseExpenseAccounts` 行、也**不改写**任何既有行：

| 字段 | 规则（与 `BaseExpenseAccount` / 实体注解同源） |
|---|---|
| `AccountCode` | 非空，长度 ≤ 50（`NVARCHAR(50)`），且在**未删除行中唯一**（修改时排除自身） |
| `AccountName` | 非空，长度 ≤ 100（`NVARCHAR(100)`） |
| `AccountType` | 仅接受 `1`（收入）或 `2`（支出） |
| `Description` | 长度 ≤ 500（`NVARCHAR(500)`） |
| `ParentId` | 可空；非空时必须指向一条**未删除**的费用科目（且不能指向自身） |
| `Status` | 仅接受 `1`（启用）或 `0`（停用） |

- **不静默截断 / 夹取**任何字段：越界即拒绝，绝不把超长文本裁短后写入。
- **科目编码唯一索引语义不变**：`ErpDbContext.Config` 的 `BaseExpenseAccount.AccountCode` 唯一索引（既有、未加
  过滤条件）与 `GenericService` 的写入路径均未改动；控制器护栏按验收口径在写入前做「未删除行内唯一」的受控拒绝
  （已删除的历史行不再占用编码）。既有未过滤唯一索引可能额外拒绝「复用已软删除行的编码」，这属于**未改动的既有
  数据库约束**，本任务不新增 / 不修改任何索引。
- **`GenericService` 契约不变**：授权与校验都位于控制器层，通用 CRUD 服务（含全部其它派生控制器的
  软删除 / 批量删除契约）的既有语义保持。

## 4. 语义边界

- **响应契约不变**：`ApiResponse<PagedResult<BaseExpenseAccount>>` 等既有返回结构逐字段不变。
- 只读写 `BaseExpenseAccounts` 自身：不自动创建 / 选择科目，不改写任何费用单据、费用分摊或历史行。
- 不新增任何表 / 列 / 实体 / 索引 / 权限模型，不使用外键，软删除后历史引用照常可读。

## 5. 单元测试（`src/ERP.UnitTests/ExpenseAccountAuthorizationTests.cs`，内存库）

| 用例 | 断言 |
|---|---|
| 缺失 / 零 / 未知 / 已删除 / 禁用 / 缺菜单 / 被撤销（`[Theory]` × 请求形状） | 7 条路由逐一拒绝（`2000` / `2002`），`BaseExpenseAccounts` 快照逐字节不变 |
| 路径无关（空路径 / 已赋值路径 / 未绑定上下文） | 三种形状对同一身份判定完全一致 |
| 拒绝先于任何读取 | 对既有未删除行按主键读取在拒绝时返回受控授权错误而非 `1002`，不泄露行数据 |
| 具备既有 `expense-account` 菜单 | 分页 / 全部 / 按主键 / 新增 / 修改 / 删除 / 批量删除既有契约放行 |
| 请求之间撤销菜单授权 | 下一次读取 / 新增 / 删除均 `2002`，零写入 |
| 非法编码 / 名称 / 说明越界 / 类型越界 / 状态越界（`[Theory]`） | `1001`，新增不落行、修改不改写既有行 |
| 编码重复（未删除行） | `1003`，零写入；已删除行不占用编码 |
| 父级不存在 / 非法 / 已删除 | `1001`，零写入；指向既有未删除科目放行 |
| 边界值（编码 50、名称 100、说明 500、类型 1/2、状态 0/1） | 放行；超一位即 `1001` |
| 控制器源码契约 | 7 条路由全部先 `EnsureExpenseAccountAuthorizedAsync()` 再读写；复用既有 `expense-account` 菜单；无 `AllowAnonymous` / 角色 / 环境 / 提供程序回退 |

`src/ERP.UnitTests/MasterDataEntryAuthorizationTests.cs` 已扩展为覆盖**六类**主数据（客户 / 供应商 / 员工 /
仓库 / 商品 / 费用科目）的路径无关入口授权，其真实启用身份夹具保持不变。

## 6. 真实 SQL Server 集成测试（`src/ERP.IntegrationTests/ExpenseAccountAuthorizationSqlServerTests.cs`）

在 GUID 独占的 `NEWERP_AUTOTEST` 目标库上以**真实控制器 + 真实既有授权**自包含播种费用科目与真实授权账号：

| 场景 | 断言 |
|---|---|
| 缺失 / 零 / 未知 / 已删除 / 禁用 / 无菜单 / 被撤销（`[Theory]`） | 全部路由对应错误码；`BaseExpenseAccounts` 快照逐字节不变 |
| 具备既有 `expense-account` 菜单（含特权种子管理员） | 分页 / 全部 / 按主键 / 新增 / 修改 / 软删除 / 批量删除全部符合既有契约 |
| 请求之间撤销菜单授权 | 下一次读取与删除均 `2002`，行未软删除 |
| 授权后非法载荷（编码 / 名称空值、文本越界、类型 / 状态越界、编码重复、父级不存在） | `1001` / `1003`，零写入 |

**真实 SQL 夹具安全口径**

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，且 `IntegratedSecurity=true`；
  实例名 / 库名前缀 / 集成安全在**访问数据库之前**校验（`AssertDedicatedTarget`，含单元级护栏用例）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；**绝不 drop / reset / 复用**任何数据库。
- 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 `appsettings` / `.env` / 生产凭据。

## 7. 未执行 / 限制

- 本任务 `validation_profile = safe`、`completion_mode = build`：交付门槛为 **Release 构建 + 全量 `ERP.UnitTests`**；
  真实 SQL 集成用例在具备专用 localdb 的环境下运行，**构建通过不等于阶段验收通过**。
- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置，
  未新增任何表 / 列 / 实体 / 菜单 / 权限 / 连接字符串或密钥。

## 8. 相关实现

- `src/ERP.Api/Controllers/BaseDataControllers.cs`：`ExpenseAccountController` 的 7 条路由均在读写前调用
  `EnsureExpenseAccountAuthorizedAsync()`，新增 / 修改另调用 `ExpenseAccountAuthorizationRules.EnsureWritableAsync`。
- `src/ERP.Application/Services/ExpenseAccountAuthorizationRules.cs`：实时身份 / 账号状态 / 既有费用科目菜单判定与
  有界字段 / 唯一性 / 父级校验。
- 复用既有：`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`（角色 → 菜单）、
  既有「费用科目」（`expense-account`）菜单（`SeedData.Menus`）与既有 `GenericService<TEntity>`。
