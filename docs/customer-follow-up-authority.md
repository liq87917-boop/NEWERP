# 客户跟进记录的实时授权、数据范围与有界字段校验（ERP-458）

客户跟进记录端点（`/api/crm/follow-ups` 的分页、全部、按主键读取、新增、修改、删除、批量删除）
在读取或写入 **任何** `CustomerFollowUps` 行之前，统一重新解析：

> 实时身份 → 账号状态 → 既有「客户跟进记录」（`customer-follow`）功能菜单授权 → ERP-097 业务员数据范围

任一缺失即 **fail closed**，绝不返回、新增、改写或软删除任何跟进记录。新增 / 修改另在落库**之前**做有界字段
校验与客户 / 跟进人引用与范围校验。客户跟进记录此前可被任意已认证账号铸造、改写或软删除，本次是本阶段
Stage 3 访问控制闭环的一部分。

## 1. 授权（fail closed，精确复用既有功能菜单）

`CustomerFollowUpAuthorizationRules.EnsureAuthorizedAsync` 在**每一条**路由的最前面执行（先于任何
`IGenericService` 读取 / 写入），对 HTTP 请求与进程内控制器调用**一律**生效：

| 场景 | 判定结果 |
|---|---|
| 无身份 / 非法用户 Id | `2000` 未认证 |
| 账号不存在或已删除 | `2000` 未认证 |
| 账号已禁用 | `2002` 权限不足 |
| 无角色 / 缺少既有 `customer-follow` 菜单授权（含被撤销） | `2002` 权限不足 |
| 具备既有 `customer-follow` 菜单 | 放行既有读 / 写契约 |

- 菜单授权复用既有「角色 → 菜单」口径（`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`）；
  **每次请求重新查询**，撤销授权后下一次请求立即收敛（绝不缓存）。
- 复用既有菜单 `customer-follow`（`SchemaUpgrader` 的「客户跟进记录」，权限码 `crm:customer-follow`），
  **不新增**任何表 / 列 / 实体 / 菜单 / 权限 / 用户授权；**无匿名 / 管理员回退**，绝不把空身份当作管理员，
  特权账号同样必须实时具备该菜单。
- 授权**不**按 `Request.Path` / 环境变量 / 空请求 / 伪造身份降级，也不提供测试专用旁路；
  进程内直接调用同样执行实时授权。

## 2. ERP-097 业务员数据范围（唯一权威口径）

`SalespersonDataScopeService`（ERP-097）为读取与写入提供同一份数据范围：

- **读取**（分页 / 全部 / 按主键）在数据库侧按 `CustomerFollowUp.CustomerId` 下推过滤；
  受限业务员只看到 `BaseCustomer.EmpId == 本人` 的客户跟进记录（无客户的记录对受限账号不可见）；
  越界按主键读取按「不存在」fail closed，**不加载任何行**。
- **写入**（新增 / 修改）要求请求的 `CustomerId` 非空且落在当前账号范围内（范围外按权限不足拒绝，不泄露归属）。
- 特权账号（超级管理员 / 系统内置角色 / 显式配置的特权角色）不受范围限制，`AllowedCustomerIds == null`。
- 删除 / 批量删除保持既有软删除语义与其它派生控制器一致，只是每个路由在写入前多了一道实时授权判定。

## 3. 新增 / 修改前的有界字段校验（不夹取、不改写）

`CustomerFollowUpAuthorizationRules.Validate` 与 `EnsureWritableReferencesAsync` 在**落库之前**校验，
任一项不满足即按 `1001` 参数错误（越界客户按 `2002`）拒绝，被拒绝的写入**不落任何** `CustomerFollowUps` 行、
也**不改写**任何既有行：

| 字段 | 规则（与 `CustomerFollowUp` / `SchemaUpgrader` 同源） |
|---|---|
| `FollowNo` | 非空，长度 ≤ 50（`NVARCHAR(50)`） |
| `CustomerName` | 长度 ≤ 200 |
| `FollowType` / `Result` | 长度 ≤ 30 |
| `ContactPerson` | 长度 ≤ 100 |
| `SalesmanName` | 长度 ≤ 50 |
| `Subject` | 长度 ≤ 100 |
| `Content` | 长度 ≤ 1000 |
| `Remark` | 长度 ≤ 500 |
| `CustomerId` | 必须非空为正、落在当前账号数据范围内，且指向已知的未删除客户 |
| `SalesmanId` | 可空（`null` / `0` 视为未指定）；非空时必须指向已知的未删除员工 |

- **不静默截断 / 夹取**任何字段：越界即拒绝，绝不把超长文本裁短后写入。
- **`GenericService` 契约与软删除语义不变**：授权与校验都位于控制器层，通用 CRUD 服务的既有语义保持，
  其它派生自 `BaseCrudController` 的控制器不受影响。
- **被拒绝的写入不落任何行**，业务员读取范围随后照常生效。

## 4. 语义边界

- **响应契约不变**：`ApiResponse<PagedResult<CustomerFollowUp>>` 等既有返回结构逐字段不变。
- 只读写 `CustomerFollowUps` 自身：不改写客户 / 员工主数据，也不触碰任何历史行。
- 不新增任何表 / 列 / 实体 / 索引 / 权限模型，不使用外键，软删除后历史引用照常可读。

## 5. 单元测试（`src/ERP.UnitTests/CustomerFollowUpAuthorizationTests.cs`，内存库）

| 用例 | 断言 |
|---|---|
| 缺失 / 禁用 / 已删除 / 缺跟进菜单（`[Theory]`） | 7 条路由逐一拒绝（`2000` / `2002`），跟进记录快照逐字节不变 |
| 具备既有 `customer-follow` 菜单 | 分页 / 全部 / 按主键 / 新增 / 修改 / 删除 / 批量删除既有契约放行 |
| 授权身份下的受限业务员 | 只返回自己客户的跟进记录，越界读取按「不存在」，越界写入 `2002`（ERP-097 范围不变） |
| 请求之间撤销菜单授权 | 下一次请求 `2002`，零写入 |
| 非法跟进编号 / 文本越界 / 客户与跟进人引用非法（`[Theory]`） | `1001`，新增不落行、修改不改写既有行 |
| 边界值（各文本字段等于持久化上限） | 放行；超一位即 `1001` |
| 控制器源码契约 | 7 条路由全部先 `EnsureAuthorizedScopeAsync()` 再读写；复用既有 `customer-follow` 菜单；无 `AllowAnonymous` / 角色回退 / `Request.Path` / 环境回退 |

既有 `src/ERP.UnitTests/FollowUpDueScopeTests.cs`（跟进提醒报表读取范围）与
`src/ERP.UnitTests/CustomerMasterAuthorizationTests.cs`（客户资料授权与校验）契约保持绿色。

## 6. 真实 SQL Server 集成测试（`src/ERP.IntegrationTests/CustomerFollowUpAuthorizationSqlServerTests.cs`）

在 GUID 独占的 `NEWERP_AUTOTEST` 目标库上以**真实控制器 + 真实既有授权**自包含播种客户与真实授权账号：

| 场景 | 断言 |
|---|---|
| 缺失 / 禁用 / 已删除 / 无菜单（`[Theory]`） | 全部 7 条路由对应错误码；`CustomerFollowUps` 快照逐字节不变 |
| 既有种子管理员具备 `customer-follow` 菜单 | 分页 / 全部 / 按主键 / 新增 / 修改 / 软删除 / 批量删除全部符合既有契约 |
| 受限业务员 | 只读到 / 只写入自己客户的跟进记录；越界读取按「不存在」，越界写入 `2002` |
| 请求之间撤销菜单授权 | 下一次读取、新增、删除与批量删除均 `2002`，行未软删除 |
| 授权后非法载荷（编号空值、文本越界、客户 / 跟进人引用非法） | `1001`，零写入 |

**真实 SQL 夹具安全口径**

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，且 `IntegratedSecurity=true`；
  实例名 / 库名前缀 / 集成安全在**访问数据库之前**校验（`AssertDedicatedTarget`，含单元级护栏用例）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；**绝不 drop / reset / 复用**任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings` / `.env` / 生产凭据。
- **本次实测**：在专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance` 上以全新 GUID 库
  `NEWERP_AUTOTEST_CUSTOMERFOLLOWUPAUTH_*` 真实执行 —— 24 通过 / 0 失败（含 4 例目标库护栏、拒绝矩阵、
  放行、撤销收敛与非法载荷零写入场景）。**构建通过不等于阶段验收通过**；被拒路径均未落任何行、未改写任何既有行，
  未 drop / reset / 复用任何数据库，未读取任何生产设置或凭据。

## 7. 未执行 / 限制

- 本任务 `validation_profile = safe`、`completion_mode = build`：交付门槛为 **Release 构建 + 全量 `ERP.UnitTests`**；
  真实 SQL 集成用例在具备专用 localdb 的环境下运行，**构建通过不等于阶段验收通过**。
- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置，
  未新增任何表 / 列 / 实体 / 菜单 / 权限 / 连接字符串或密钥。

## 8. 相关实现

- `src/ERP.Api/Controllers/CustomerFollowUpController.cs`：7 条路由均在读写前调用 `EnsureAuthorizedScopeAsync()`，
  读取按范围下推，新增 / 修改另调用 `CustomerFollowUpAuthorizationRules.Validate` 与 `EnsureWritableReferencesAsync`。
- `src/ERP.Application/Services/CustomerFollowUpAuthorizationRules.cs`：实时身份 / 账号状态 / 既有跟进菜单判定、
  ERP-097 范围解析、有界字段校验与客户 / 跟进人引用校验。
- 复用既有：`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`（角色 → 菜单）、
  `SalespersonDataScopeService`（ERP-097 唯一权威数据范围）、既有「客户跟进记录」（`customer-follow`）菜单
  与既有 `GenericService<TEntity>`。
