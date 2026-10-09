# 样品管理的实时授权、数据范围与有界字段校验（ERP-459）

样品管理端点（`/api/crm/samples` 的分页、全部、按主键读取、新增、修改、删除、批量删除）
在读取或写入 **任何** `Samples` 行之前，统一重新解析：

> 实时身份 → 账号状态 → 既有「样品管理」（`sample`）功能菜单授权 → ERP-097 业务员数据范围

任一缺失即 **fail closed**，绝不返回、新增、改写或软删除任何样品。新增 / 修改另在落库**之前**做有界字段
校验与客户 / 商品 / 业务员引用与范围校验。样品管理此前在 `SampleController` 上只有类级 `[Authorize]`，
每条路由都从 `BaseCrudController<Sample>` 原样继承（`IGenericService`、无身份 / 菜单 / 客户范围解析），
因此任何已认证账号都能列出、铸造、改写或软删除所有客户的样品及其样品费数据；本次是本阶段 Stage 3 访问控制
闭环的一部分。

## 1. 授权（fail closed，精确复用既有功能菜单）

`SampleAuthorizationRules.EnsureAuthorizedAsync` 在**每一条**路由的最前面执行（先于任何
`IGenericService` 读取 / 写入），对 HTTP 请求与进程内控制器调用**一律**生效：

| 场景 | 判定结果 |
|---|---|
| 无身份 / 非法用户 Id | `2000` 未认证 |
| 账号不存在或已删除 | `2000` 未认证 |
| 账号已禁用 | `2002` 权限不足 |
| 无角色 / 缺少既有 `sample` 菜单授权（含被撤销） | `2002` 权限不足 |
| 具备既有 `sample` 菜单 | 放行既有读 / 写契约 |

- 菜单授权复用既有「角色 → 菜单」口径（`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`）；
  **每次请求重新查询**，撤销授权后下一次请求立即收敛（绝不缓存）。
- 复用既有菜单 `sample`（`SchemaUpgrader` 的「样品管理」，权限码 `crm:sample`），
  **不新增**任何表 / 列 / 实体 / 菜单 / 权限 / 用户授权；**无匿名 / 管理员回退**，绝不把空身份当作管理员，
  特权账号同样必须实时具备该菜单。
- 授权**不**按 `Request.Path` / 环境变量 / 空请求 / 伪造身份降级，也不提供测试专用旁路；
  进程内直接调用同样执行实时授权。

## 2. ERP-097 业务员数据范围（唯一权威口径）

`SalespersonDataScopeService`（ERP-097）为读取与写入提供同一份数据范围：

- **读取**（分页 / 全部 / 按主键）在数据库侧按 `Sample.CustomerId` 下推过滤；
  受限业务员只看到 `BaseCustomer.EmpId == 本人` 的客户样品（无客户的样品对受限账号不可见）；
  越界按主键读取按「不存在」fail closed，**不加载任何行**。
- **写入**（新增 / 修改）要求请求的 `CustomerId` 非空且落在当前账号范围内（范围外按权限不足拒绝，不泄露归属）。
- 特权账号（超级管理员 / 系统内置角色 / 显式配置的特权角色）不受范围限制，`AllowedCustomerIds == null`。
- 删除 / 批量删除保持既有软删除语义与其它派生控制器一致，只是每个路由在写入前多了一道实时授权判定。

## 3. 新增 / 修改前的有界字段校验（不夹取、不改写）

`SampleAuthorizationRules.Validate` 与 `EnsureWritableReferencesAsync` 在**落库之前**校验，
任一项不满足即按 `1001` 参数错误（越界客户按 `2002`）拒绝，被拒绝的写入**不落任何** `Samples` 行、
也**不改写**任何既有行：

| 字段 | 规则（与 `Sample` / `SchemaUpgrader` 同源） |
|---|---|
| `SampleNo` | 非空，长度 ≤ 50（`NVARCHAR(50)`） |
| `SampleType` / `Result` | 长度 ≤ 30 |
| `Unit` / `Currency` | 长度 ≤ 20 |
| `Express` / `TrackingNo` | 长度 ≤ 50 |
| `CustomerName` / `ProductName` / `Spec` | 长度 ≤ 200 |
| `SalesmanName` | 长度 ≤ 50 |
| `Remark` | 长度 ≤ 500 |
| `Quantity` / `SampleFee` | 非负且 ≤ `99999999999999.9999`（`DECIMAL(18,4)`） |
| `CustomerId` | 必须非空为正、落在当前账号数据范围内，且指向已知的未删除客户 |
| `ProductId` | 可空（`null` / `0` 视为未指定）；非空时必须指向已知的未删除商品 |
| `SalesmanId` | 可空（`null` / `0` 视为未指定）；非空时必须指向已知的未删除员工 |

- **不静默截断 / 夹取**任何字段：越界即拒绝，绝不把超长文本裁短或把超范围数值夹取后写入。
- **`GenericService` 契约与软删除语义不变**：授权与校验都位于控制器层，通用 CRUD 服务的既有语义保持，
  其它派生自 `BaseCrudController` 的控制器不受影响。
- **ERP-063 附件证据归属（`OwnerType = Sample`）契约不变**：附件证据只读样品台账（编号 / 类型 / 客户反馈原文），
  本次改动不改变任何归属类型、`AttachmentEvidences` 字段或下载 / 作废口径。
- **被拒绝的写入不落任何行**，业务员读取范围随后照常生效。

## 4. 语义边界

- **响应契约不变**：`ApiResponse<PagedResult<Sample>>` 等既有返回结构逐字段不变。
- 只读写 `Samples` 自身：不改写客户 / 商品 / 员工主数据，也不触碰任何历史行。
- 不新增任何表 / 列 / 实体 / 索引 / 权限模型，不使用外键，软删除后历史引用照常可读。

## 5. 单元测试（`src/ERP.UnitTests/SampleAuthorizationTests.cs`，内存库）

| 用例 | 断言 |
|---|---|
| 缺失 / 禁用 / 已删除 / 缺样品菜单（`[Theory]`） | 7 条路由逐一拒绝（`2000` / `2002`），样品快照逐字节不变 |
| 具备既有 `sample` 菜单 | 分页 / 全部 / 按主键 / 新增 / 修改 / 删除 / 批量删除既有契约放行 |
| 授权身份下的受限业务员 | 只返回自己客户的样品，越界读取按「不存在」，越界写入 `2002`（ERP-097 范围不变） |
| 请求之间撤销菜单授权 | 下一次请求 `2002`，零写入 |
| 非法样品编号 / 文本越界 / 数值为负或超范围 / 客户、商品与业务员引用非法（`[Theory]`） | `1001`，新增不落行、修改不改写既有行 |
| 边界值（各文本字段等于持久化上限、数值等于 `DECIMAL(18,4)` 上限） | 放行；超一位即 `1001` |
| 控制器源码契约 | 7 条路由全部先 `EnsureAuthorizedScopeAsync()` 再读写；复用既有 `sample` 菜单；无 `AllowAnonymous` / 角色回退 / `Request.Path` / 环境回退 |

既有 `src/ERP.UnitTests/QualityInspectionSampleAttachmentEvidenceTests.cs`（ERP-063 验货 / 样品附件证据契约）与
`src/ERP.UnitTests/AttachmentOwnerAuthorizationTests.cs`（ERP-407 附件证据归属授权契约）保持绿色。

## 6. 真实 SQL Server 集成测试（`src/ERP.IntegrationTests/SampleAuthorizationSqlServerTests.cs`）

在 GUID 独占的 `NEWERP_AUTOTEST` 目标库上以**真实控制器 + 真实既有授权**自包含播种客户、商品、员工与真实授权账号：

| 场景 | 断言 |
|---|---|
| 缺失 / 禁用 / 已删除 / 无菜单（`[Theory]`） | 全部 7 条路由对应错误码；`Samples` 快照逐字节不变 |
| 既有种子管理员具备 `sample` 菜单 | 分页 / 全部 / 按主键 / 新增 / 修改 / 软删除 / 批量删除全部符合既有契约 |
| 受限业务员 | 只读到 / 只写入自己客户的样品；越界读取按「不存在」，越界写入 `2002` |
| 请求之间撤销菜单授权 | 下一次读取、新增、删除与批量删除均 `2002`，行未软删除 |
| 授权后非法载荷（编号空值、文本越界、数值非法、客户 / 商品 / 业务员引用非法） | `1001`，零写入 |

**真实 SQL 夹具安全口径**

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，且 `IntegratedSecurity=true`；
  实例名 / 库名前缀 / 集成安全在**访问数据库之前**校验（`AssertDedicatedTarget`，含单元级护栏用例）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；**绝不 drop / reset / 复用**任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings` / `.env` / 生产凭据。

## 7. 未执行 / 限制

- 本任务 `validation_profile = safe`、`completion_mode = build`：交付门槛为 **Release 构建 + 全量 `ERP.UnitTests`**；
  真实 SQL 集成用例在具备专用 localdb 的环境下运行，**构建通过不等于阶段验收通过**。
- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置，
  未新增任何表 / 列 / 实体 / 菜单 / 权限 / 连接字符串或密钥。

## 8. 相关实现

- `src/ERP.Api/Controllers/SampleController.cs`：7 条路由均在读写前调用 `EnsureAuthorizedScopeAsync()`，
  读取按范围下推，新增 / 修改另调用 `SampleAuthorizationRules.Validate` 与 `EnsureWritableReferencesAsync`。
- `src/ERP.Application/Services/SampleAuthorizationRules.cs`：实时身份 / 账号状态 / 既有样品菜单判定、
  ERP-097 范围解析、有界字段校验与客户 / 商品 / 业务员引用校验。
- 复用既有：`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`（角色 → 菜单）、
  `SalespersonDataScopeService`（ERP-097 唯一权威数据范围）、既有「样品管理」（`sample`）菜单
  与既有 `GenericService<TEntity>`。
