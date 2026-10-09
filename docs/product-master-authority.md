# 商品资料的实时授权与有界字段校验（ERP-452）

商品资料端点（`/api/base/products` 的分页、全部、按主键读取、新增、修改、删除、批量删除、导出、
单张上传、批量上传）在读取、写入或产出**任何**产物之前，统一重新解析：

> 实时身份 → 账号状态 → 既有「商品资料」（`product`）功能菜单授权

任一缺失即 **fail closed**，绝不返回、新增或改写任何 `BaseProducts` 行，也绝不生成任何导出文件或写入任何
OSS 对象。商品主数据是销售订单行 / 采购订单行 / 报价单行 / 库存单据与库存查询**共同解析**的权威对象，
因此不再能被任意已认证账号改写、导出或覆盖图片。新增 / 修改另在落库**之前**做有界字段校验。

## 1. 授权（fail closed，精确复用既有功能菜单）

`ProductAuthorizationRules.EnsureAuthorizedAsync` 在**每一个**路由的最前面执行（先于任何
`IGenericService` 读取 / 写入、先于导出模板读取与 OSS 上传）：

| 场景 | 判定结果 |
|---|---|
| 无身份 / 非法用户 Id | `2000` 未认证 |
| 账号不存在或已删除 | `2000` 未认证 |
| 账号已禁用 | `2002` 权限不足 |
| 无角色 / 缺少既有 `product` 菜单授权（含被撤销） | `2002` 权限不足 |
| 具备既有 `product` 菜单 | 放行既有读 / 写 / 导出 / 上传契约 |

- 菜单授权复用既有「角色 → 菜单」口径（`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`）；
  **每次请求重新查询**，撤销授权后下一次请求立即收敛（绝不缓存）。
- **不新增**任何表 / 列 / 实体 / 菜单 / 权限 / 用户授权；**无匿名 / 管理员回退**，绝不把空身份当作管理员。
- 既有分页 / 全部 / 按主键 / 新增 / 修改 / 删除 / 批量删除 / 导出 / 上传路由与响应契约完全不变，
  只是每个路由在读写 / 生成产物前多了一道实时授权判定。
- **进程内直调边界**（与仓库 / 客户既有 `RequiresLiveAuthorization` 口径同源）：真实 HTTP 请求
  （`Request.Path` 已赋值）一律执行授权，**真实匿名请求因处于请求管线内一律 fail closed（未认证）**；
  仅「既无任何登录身份、又不在 HTTP 请求管线内」的**进程内直接调用**（历史单元测试 / 内部派生读取，不可能由外部请求到达）
  沿用既有语义，绝不提供可被外部到达的测试专用降级，也绝不把缺失身份当作管理员。

## 2. 新增 / 修改前的有界字段校验（不夹取、不改写）

`ProductAuthorizationRules.Validate` 在**落库之前**校验持久化列的可存储范围，任一项不满足即按
`1001` 参数错误拒绝，被拒绝的写入**不落任何** `BaseProducts` 行、也**不改写**任何既有行：

| 字段 | 规则（与 `BaseProduct` / 实体注解 / 建表脚本同源） |
|---|---|
| `ProductCode` | 非空，长度 ≤ 50（`NVARCHAR(50)`） |
| `ProductName` | 非空，长度 ≤ 200（`NVARCHAR(200)`） |
| `EnglishName` / `Spec` / `EnglishDeclareName` | 长度 ≤ 200 |
| `Unit` / `PackageUnit` | 长度 ≤ 20 |
| `Category` / `UnitConversion` / `Barcode` / `Brand` / `CustomerItemNo` / `FactoryItemNo` | 长度 ≤ 100 |
| `HsCode` | 长度 ≤ 50 |
| `Image1` / `Image2` / `Image3` | 长度 ≤ 500 |
| `Certification` | 长度 ≤ 200 |
| `Remark` | 长度 ≤ 500 |
| 价格 / 尺寸 / 重量 / 体积 / 退税率 / 安全库存 / 库存上限等 decimal 列 | 落在 `DECIMAL(18,4)` 可存储范围内（`MaxDecimalMagnitude`），超出即拒绝 |
| `UnitsPerPackage` / `MinOrderQty` | 已知的非负形状（0 = 未指定），负数即拒绝 |
| `Status` | 仅接受 `1`（启用）或 `0`（停用） |

- **不静默截断 / 夹取**任何字段：越界即拒绝，绝不把超长文本裁短或把越界数值夹取后写入。
- **商品编码唯一索引语义不变**：`ErpDbContext.Config` 的 `BaseProduct.ProductCode` 唯一索引与
  `GenericService` 的写入路径均未改动。
- **`GenericService` 契约不变**：授权与校验都位于控制器层，通用 CRUD 服务的既有语义保持。

## 3. 语义边界

- **响应契约不变**：`ApiResponse<PagedResult<BaseProduct>>` 等既有返回结构与 `Export()` 的
  `File(...)` 下载契约、`Upload` / `UploadBatch` 的 `{ url }` / `{ urls }` 结构逐字段不变。
- **ERP-037 / ERP-038 关系语义不变**：`ProductVariantController`（规格变体）与
  `ProductSupplierController`（货源关系）分别由 ERP-444 / ERP-441 的 `ProductVariantAuthorizationRules` /
  `ProductSupplierRules` 独立护栏保护，本任务只读标注计数（`VariantCount` / `SourcingCount` 等），
  不改变其维护口径。
- 只读写 `BaseProducts` 自身与既有导出 / OSS 上传：不新增商品、不改写单据 / 库存 / 流水 / 图片以外的任何状态。
- 不新增任何表 / 列 / 实体 / 索引 / 权限模型，不使用外键，软删除后历史引用照常可读。

## 4. 单元测试（`src/ERP.UnitTests/ProductMasterAuthorizationTests.cs`，内存库）

| 用例 | 断言 |
|---|---|
| 缺失 / 禁用 / 已删除 / 缺商品菜单（`[Theory]`） | 10 条路由逐一拒绝（`2000` / `2002`），`BaseProducts` 快照逐字节不变；导出与上传在授权之前即被拒，无可下载产物、无 OSS 写入 |
| 具备既有 `product` 菜单 | 分页 / 全部 / 按主键 / 新增 / 修改 / 删除 / 批量删除既有契约放行 |
| 具备既有 `product` 菜单（导出 / 上传） | 导出产出可下载 xlsx；上传空 / 非法载荷仍按 `1001` 拒绝（不触达 OSS） |
| 请求之间撤销菜单授权 | 下一次请求 `2002`，零写入 |
| 非法编码 / 名称 / 文本越界 / decimal 越界 / 整数负数 / 状态越界（`[Theory]`） | `1001`，新增不落行、修改不改写既有行 |
| 边界值（编码 50、名称 200、备注 500、decimal 上限、状态 0 / 1） | 放行；超一位即 `1001` |
| 控制器源码契约 | 10 条路由全部先 `EnsureProductAuthorizedAsync()` 再读写 / 生成产物；复用既有 `product` 菜单；无 `AllowAnonymous` / 角色回退 |

`src/ERP.UnitTests/ProductVariantTests.cs` 与 `src/ERP.UnitTests/ProductSupplierTests.cs` 的既有
ERP-037 / ERP-038 契约用例保持绿（进程内直调 `ProductController.GetPaged` / `GetById` 的读取标注语义不变）。

## 5. 真实 SQL Server 集成测试（`src/ERP.IntegrationTests/ProductMasterAuthorizationSqlServerTests.cs`）

在 GUID 独占的 `NEWERP_AUTOTEST` 目标库上以**真实控制器 + 真实既有授权**自包含播种商品与真实授权账号：

| 场景 | 断言 |
|---|---|
| 缺失 / 禁用 / 已删除 / 无菜单（`[Theory]`） | 全部路由（含导出与图片上传）对应错误码；`BaseProducts` 快照逐字节不变 |
| 具备既有 `product` 菜单 | 分页 / 全部 / 按主键 / 新增 / 修改 / 软删除 / 批量删除全部符合既有契约 |
| 特权种子管理员（既有种子已授予全部菜单含 `product`） | 既有只读契约放行，不因特权跳过菜单检查 |
| 请求之间撤销菜单授权 | 下一次读取 / 删除 / 导出 / 上传均 `2002`，行未软删除 |
| 授权后非法载荷（编码 / 名称空值、文本越界、decimal 越界、整数负数、状态越界） | `1001`，零写入 |

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

- `src/ERP.Api/Controllers/BaseDataControllers.cs`：`ProductController` 的 10 条路由均在读写 / 产物生成前调用
  `EnsureProductAuthorizedAsync()`，新增 / 修改另调用 `ProductAuthorizationRules.Validate`。
- `src/ERP.Application/Services/ProductAuthorizationRules.cs`：实时身份 / 账号状态 / 既有商品菜单判定与有界字段校验。
- 复用既有：`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`（角色 → 菜单）、
  既有「商品资料」（`product`）菜单（`SeedData.Menus`）、既有 `GenericService<TEntity>`、
  `ProductExcelExporter` 与 `OssStorageService`。
