# 供应商资料的实时授权与有界字段校验（ERP-447）

供应商资料端点（`/api/base/suppliers` 的分页、全部、按主键读取、新增、修改、删除、批量删除）在读取或写入
**任何** `BaseSuppliers` 行之前，统一重新解析：

> 实时身份 → 账号状态 → 既有「供应商资料」（`supplier`）功能菜单授权

任一缺失即 **fail closed**，绝不返回、新增或改写任何供应商行。新增 / 修改另在落库**之前**做有界字段校验。

## 1. 授权（fail closed，精确复用既有功能菜单）

`SupplierAuthorizationRules.EnsureAuthorizedAsync` 在**每一个**路由的最前面执行（先于任何
`IGenericService` 读取 / 写入）：

| 场景 | 判定结果 |
|---|---|
| 无身份 / 非法用户 Id | `2000` 未认证 |
| 账号不存在或已删除 | `2000` 未认证 |
| 账号已禁用 | `2002` 权限不足 |
| 无角色 / 缺少既有 `supplier` 菜单授权（含被撤销） | `2002` 权限不足 |
| 具备既有 `supplier` 菜单 | 放行既有读 / 写契约 |

- 菜单授权复用既有「角色 → 菜单」口径（`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`）；
  **每次请求重新查询**，撤销授权后下一次请求立即收敛（绝不缓存）。
- **不新增**任何表 / 列 / 实体 / 菜单 / 权限 / 用户授权；**无匿名 / 管理员回退**，绝不把空身份当作管理员。
- 既有分页 / 全部 / 按主键 / 新增 / 修改 / 删除 / 批量删除路由与响应契约完全不变，
  只是每个路由在读写前多了一道实时授权判定。
- **进程内直调边界**（与仓库既有 `RequiresLiveAuthorization` / `PurchaseOrderController` 同源口径）：真实 HTTP 请求
  （`Request.Path` 已赋值）一律执行授权，**真实匿名请求因处于请求管线内一律 fail closed（未认证）**；
  仅「既无任何登录身份、又不在 HTTP 请求管线内」的**进程内直接调用**（历史单元测试 / 内部派生读取，不可能由外部请求到达）
  沿用既有语义，绝不提供可被外部到达的测试专用降级，也绝不把缺失身份当作管理员。

## 2. 新增 / 修改前的有界字段校验（不夹取、不改写）

`SupplierAuthorizationRules.Validate` 在**落库之前**校验持久化列的可存储范围，任一项不满足即按
`1001` 参数错误拒绝，被拒绝的写入**不落任何** `BaseSuppliers` 行、也**不改写**任何既有行：

| 字段 | 规则（与 `BaseSupplier` / `SchemaUpgrader` 同源） |
|---|---|
| `SupplierCode` | 非空，长度 ≤ 50（`NVARCHAR(50)`） |
| `SupplierName` | 非空，长度 ≤ 200（`NVARCHAR(200)`） |
| `EnglishName` / `PaymentTerms` / `BankName` | 长度 ≤ 200 |
| `Address` | 长度 ≤ 500 |
| `Remark` | 长度 ≤ 500 |
| `ContactPerson` / `Phone` / `SettlementMethod` / `WeChat` | 长度 ≤ 50 |
| `Email` / `Country` / `BankAccount` | 长度 ≤ 100 |
| `SupplierType` / `InvoiceAbility` | 长度 ≤ 20 |
| `BoothLocation` / `MainCategory` | 长度 ≤ 100 |
| `TaxRate` / `RebateRatio` | 落在 `DECIMAL(18,4)` 可存储范围（绝对值 ≤ 99999999999999.9999） |
| `Status` | 仅接受 `1`（启用）或 `0`（停用） |

- **不静默截断 / 夹取**任何字段：越界即拒绝，绝不把超长文本裁短或把超范围数值夹到边界后写入。
- **供应商编码唯一索引语义不变**：`ErpDbContext.Config` 的唯一索引与 `GenericService` 的写入路径均未改动。
- **`GenericService` 契约不变**：授权与校验都位于控制器层，通用 CRUD 服务的既有语义保持。

## 3. 语义边界

- **响应契约不变**：`ApiResponse<PagedResult<BaseSupplier>>` 等既有返回结构逐字段不变。
- 只读写 `BaseSuppliers` 自身：不自动选择供应商、不加价 / 不定价、不生成或改写采购报价 / 采购订单 /
  库存 / 付款，也不触碰任何历史单据。
- 不新增任何表 / 列 / 实体 / 索引 / 权限模型，不使用外键，软删除后历史引用照常可读。

## 4. 单元测试（`src/ERP.UnitTests/SupplierMasterAuthorizationTests.cs`，内存库）

| 用例 | 断言 |
|---|---|
| 缺失 / 禁用 / 已删除 / 缺供应商菜单（`[Theory]`） | 7 条路由逐一拒绝（`2000` / `2002`），`BaseSuppliers` 快照逐字节不变 |
| 具备既有 `supplier` 菜单 | 分页 / 全部 / 按主键 / 新增 / 修改 / 删除 / 批量删除既有契约放行 |
| 请求之间撤销菜单授权 | 下一次请求 `2002`，零写入 |
| 非法编码 / 名称 / 文本越界 / 税率与返点超 `DECIMAL(18,4)` / 状态越界（`[Theory]`） | `1001`，新增不落行、修改不改写既有行 |
| 边界值（编码 50、名称 200、税率 / 返点等于上限、状态 0 / 1） | 放行；超一位即 `1001` |
| 控制器源码契约 | 7 条路由全部先 `EnsureAuthorizedAsync()` 再读写；复用既有 `supplier` 菜单；无 `AllowAnonymous` / 角色回退 |

`src/ERP.UnitTests/GenericServiceTests.cs` 另有 `GenericService_供应商_既有CRUD契约保持不变` 用例，
确认授权护栏只位于控制器层、通用服务契约保持绿。

## 5. 真实 SQL Server 集成测试（`src/ERP.IntegrationTests/SupplierMasterAuthorizationSqlServerTests.cs`）

在 GUID 独占的 `NEWERP_AUTOTEST` 目标库上以**真实控制器 + 真实既有授权**自包含播种供应商与真实授权账号：

| 场景 | 断言 |
|---|---|
| 缺失 / 禁用 / 已删除 / 无菜单（`[Theory]`） | 全部路由对应错误码；`BaseSuppliers` 快照逐字节不变 |
| 具备既有 `supplier` 菜单（含特权种子管理员） | 分页 / 全部 / 按主键 / 新增 / 修改 / 软删除 / 批量删除全部符合既有契约 |
| 请求之间撤销菜单授权 | 下一次读取与删除均 `2002`，行未软删除 |
| 授权后非法载荷（编码 / 名称空值、文本越界、税率超范围、状态越界） | `1001`，零写入 |

**真实 SQL 夹具安全口径**

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，且 `IntegratedSecurity=true`；
  实例名 / 库名前缀 / 集成安全在**访问数据库之前**校验（`AssertDedicatedTarget`，含单元级护栏用例）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；**绝不 drop / reset / 复用**任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings` / `.env` / 生产凭据。
- **本次实测**：在专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance` 上以全新 GUID 库
  `NEWERP_AUTOTEST_SUPPLIERMASTERAUTH_*` 真实执行 —— 12 通过 / 0 失败（含 4 例目标库护栏、特权种子管理员 /
  菜单授权放行、全部身份 / 菜单 / 撤销收敛 / 非法载荷零写入场景）。**构建通过不等于阶段验收通过**；被拒路径均未落任何行、
  未改写任何既有行，未 drop / reset / 复用任何数据库，未读取任何生产设置或凭据。

## 6. 未执行 / 限制

- 本任务 `validation_profile = safe`、`completion_mode = build`：交付门槛为 **Release 构建 + 全量 `ERP.UnitTests`**；
  真实 SQL 集成用例在具备专用 localdb 的环境下运行，**构建通过不等于阶段验收通过**。
- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置，
  未新增任何表 / 列 / 实体 / 菜单 / 权限 / 连接字符串或密钥。

## 7. 相关实现

- `src/ERP.Api/Controllers/BaseDataControllers.cs`：`SupplierController` 的 7 条路由均在读写前调用
  `EnsureAuthorizedAsync()`，新增 / 修改另调用 `SupplierAuthorizationRules.Validate`。
- `src/ERP.Application/Services/SupplierAuthorizationRules.cs`：实时身份 / 账号状态 / 既有供应商菜单判定与有界字段校验。
- 复用既有：`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`（角色 → 菜单）、
  既有「供应商资料」（`supplier`）菜单（`SeedData.Menus`）与既有 `GenericService<TEntity>`。
