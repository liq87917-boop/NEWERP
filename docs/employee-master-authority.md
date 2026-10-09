# 员工资料的实时授权与有界字段校验（ERP-449）

员工资料端点（`/api/base/employees` 的分页、全部、按主键读取、新增、修改、删除、批量删除，以及
业务员下拉 `salesmen`）在读取或写入 **任何** `BaseEmployees` 行之前，统一重新解析：

> 实时身份 → 账号状态 → 既有「员工资料」（`employee`）功能菜单授权

任一缺失即 **fail closed**，绝不返回、新增或改写任何员工行。新增 / 修改另在落库**之前**做有界字段校验。
员工主数据是销售订单 / 客户 / 报价单 / PI / 装柜清单与 ERP-097 业务员数据范围所解析的**业务员身份**
来源，因此不再能被任意已认证账号铸造、改写或软删除。

## 1. 授权（fail closed，精确复用既有功能菜单）

`EmployeeAuthorizationRules.EnsureAuthorizedAsync` 在**每一条**路由的最前面执行（先于任何
`IGenericService` 读取 / 写入，含业务员下拉）：

| 场景 | 判定结果 |
|---|---|
| 无身份 / 非法用户 Id | `2000` 未认证 |
| 账号不存在或已删除 | `2000` 未认证 |
| 账号已禁用 | `2002` 权限不足 |
| 无角色 / 缺少既有 `employee` 菜单授权（含被撤销） | `2002` 权限不足 |
| 具备既有 `employee` 菜单 | 放行既有读 / 写契约与业务员下拉 |

- 菜单授权复用既有「角色 → 菜单」口径（`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`）；
  **每次请求重新查询**，撤销授权后下一次请求立即收敛（绝不缓存）。
- **不新增**任何表 / 列 / 实体 / 菜单 / 权限 / 用户授权；**无匿名 / 管理员回退**，绝不把空身份当作管理员。
- 既有分页 / 全部 / 按主键 / 新增 / 修改 / 删除 / 批量删除 / 业务员下拉路由与响应契约完全不变，
  只是每个路由在读写前多了一道实时授权判定。
- **业务员下拉口径不变**：仍只返回 `IsSalesman == true && Status == 1` 且未软删除的员工。
- **进程内直调边界**（与仓库 / 供应商既有 `RequiresLiveAuthorization` 口径同源）：真实 HTTP 请求
  （`Request.Path` 已赋值）一律执行授权，**真实匿名请求因处于请求管线内一律 fail closed（未认证）**；
  仅「既无任何登录身份、又不在 HTTP 请求管线内」的**进程内直接调用**（历史单元测试 / 内部派生读取，不可能由外部请求到达）
  沿用既有语义，绝不提供可被外部到达的测试专用降级，也绝不把缺失身份当作管理员。

## 2. 新增 / 修改前的有界字段校验（不夹取、不改写）

`EmployeeAuthorizationRules.Validate` 在**落库之前**校验持久化列的可存储范围，任一项不满足即按
`1001` 参数错误拒绝，被拒绝的写入**不落任何** `BaseEmployees` 行、也**不改写**任何既有行：

| 字段 | 规则（与 `BaseEmployee` / 实体注解同源） |
|---|---|
| `EmployeeCode` | 非空，长度 ≤ 50（`NVARCHAR(50)`，唯一索引语义不变） |
| `EmployeeName` | 非空，长度 ≤ 50（`NVARCHAR(50)`） |
| `Department` | 长度 ≤ 100 |
| `Position` | 长度 ≤ 100 |
| `Phone` | 长度 ≤ 50 |
| `Email` | 长度 ≤ 100 |
| `HireDate` | 可空；非空值须落在 `1753-01-01` ~ `9999-12-31`（SQL Server 日期列可存储区间，拒绝默认哨兵 `0001-01-01`） |
| `Status` | 仅接受 `1`（在职）或 `0`（离职） |

- **不静默截断 / 夹取**任何字段：越界即拒绝，绝不把超长文本裁短或把越界日期夹到边界后写入。
- **员工编码唯一索引语义不变**：`ErpDbContext.Config` 的 `BaseEmployee.EmployeeCode` 唯一索引与
  `GenericService` 的写入路径均未改动。
- **`GenericService` 契约不变**：授权与校验都位于控制器层，通用 CRUD 服务的既有语义保持。
- **被拒绝的写入不落任何行**，业务员下拉随后仍只返回在职业务员。

## 3. 语义边界

- **响应契约不变**：`ApiResponse<PagedResult<BaseEmployee>>` 等既有返回结构逐字段不变。
- 只读写 `BaseEmployees` 自身：不新增业务员、不自动映射账号 → 业务员、不改写销售订单 / 客户 / 报价单 /
  PI / 装柜清单的业务员引用，也不触碰任何历史行。
- 不新增任何表 / 列 / 实体 / 索引 / 权限模型，不使用外键，软删除后历史引用照常可读。

## 4. 单元测试（`src/ERP.UnitTests/EmployeeMasterAuthorizationTests.cs`，内存库）

| 用例 | 断言 |
|---|---|
| 缺失 / 禁用 / 已删除 / 缺员工菜单（`[Theory]`） | 8 条路由逐一拒绝（`2000` / `2002`），`BaseEmployees` 快照逐字节不变 |
| 具备既有 `employee` 菜单 | 分页 / 全部 / 按主键 / 新增 / 修改 / 删除 / 批量删除既有契约放行 |
| 业务员下拉 | 只返回未删除、在职（`Status=1`）且 `IsSalesman` 的员工 |
| 请求之间撤销菜单授权 | 下一次请求 `2002`，零写入 |
| 非法编码 / 姓名 / 文本越界 / 入职日期越界 / 状态越界（`[Theory]`） | `1001`，新增不落行、修改不改写既有行 |
| 被拒绝写入后的业务员下拉 | 仍只返回在职业务员，非法行不出现 |
| 边界值（编码 50、姓名 50、可选文本等于上限、入职日期在范围内、状态 0 / 1） | 放行；超一位即 `1001` |
| 控制器源码契约 | 8 条路由全部先 `EnsureEmployeeAuthorizedAsync()` 再读写；复用既有 `employee` 菜单；无 `AllowAnonymous` / 角色回退 |

`src/ERP.UnitTests/GenericServiceTests.cs` 另有 `GenericService_员工_既有CRUD契约保持不变` 用例，
确认授权护栏只位于控制器层、通用服务契约保持绿。

## 5. 真实 SQL Server 集成测试（`src/ERP.IntegrationTests/EmployeeMasterAuthorizationSqlServerTests.cs`）

在 GUID 独占的 `NEWERP_AUTOTEST` 目标库上以**真实控制器 + 真实既有授权**自包含播种员工与真实授权账号：

| 场景 | 断言 |
|---|---|
| 缺失 / 禁用 / 已删除 / 无菜单（`[Theory]`） | 全部路由对应错误码；`BaseEmployees` 快照逐字节不变 |
| 具备既有 `employee` 菜单（含特权种子管理员） | 分页 / 全部 / 按主键 / 新增 / 修改 / 软删除 / 批量删除全部符合既有契约 |
| 业务员下拉 | 只返回在职、未删除业务员 |
| 请求之间撤销菜单授权 | 下一次读取、删除与业务员下拉均 `2002`，行未软删除 |
| 授权后非法载荷（编码 / 姓名空值、文本越界、入职日期越界、状态越界） | `1001`，零写入 |

**真实 SQL 夹具安全口径**

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，且 `IntegratedSecurity=true`；
  实例名 / 库名前缀 / 集成安全在**访问数据库之前**校验（`AssertDedicatedTarget`，含单元级护栏用例）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；**绝不 drop / reset / 复用**任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings` / `.env` / 生产凭据。

## 6. 未执行 / 限制

- 本任务 `validation_profile = safe`、`completion_mode = build`：交付门槛为 **Release 构建 + 全量 `ERP.UnitTests`**；
  真实 SQL 集成用例在具备专用 localdb 的环境下运行，**构建通过不等于阶段验收通过**。
- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置，
  未新增任何表 / 列 / 实体 / 菜单 / 权限 / 连接字符串或密钥。

## 7. 相关实现

- `src/ERP.Api/Controllers/BaseDataControllers.cs`：`EmployeeController` 的 8 条路由均在读写前调用
  `EnsureEmployeeAuthorizedAsync()`，新增 / 修改另调用 `EmployeeAuthorizationRules.Validate`。
- `src/ERP.Application/Services/EmployeeAuthorizationRules.cs`：实时身份 / 账号状态 / 既有员工菜单判定与有界字段校验。
- 复用既有：`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`（角色 → 菜单）、
  既有「员工资料」（`employee`）菜单（`SeedData.Menus`）与既有 `GenericService<TEntity>`。
