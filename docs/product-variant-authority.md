# 商品规格变体的实时授权与权威引用守卫（ERP-444）

商品规格变体端点（`/api/base/products/{productId}/variants` 的列表、可选用选项、新增、修改、停用、启用、删除）
在读取或写入**任何** `BaseProductVariants` 行之前，统一重新解析：

> 实时身份 → 账号状态 → 既有「商品资料」（`product`）菜单授权 → 权威商品引用（存在、未删除、启用）→ 提交载荷

任一缺失即 **fail closed**，绝不返回、新增、改写或软删除任何规格（含编码、颜色 / 尺码、状态与启用 / 停用流转）。

## 1. 授权（fail closed，精确复用既有功能菜单）

`ProductVariantAuthorizationRules.EnsureAuthorizedAsync` 在**每一个**路由的最前面执行（先于任何商品 / 规格读取）：

| 场景 | 判定结果 |
|---|---|
| 无身份 / 非法用户 Id | `2000` 未认证 |
| 账号不存在或已删除 | `2000` 未认证 |
| 账号已禁用 | `2002` 权限不足 |
| 无角色 / 缺少既有 `product` 菜单授权（含被撤销授予） | `2002` 权限不足 |
| 具备既有 `product` 菜单 | 放行既有读 / 写契约 |

- 菜单授权复用既有「角色 → 菜单」口径（`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`）；
  **每次请求重新查询**，撤销授权后下一次请求立即收敛（绝不缓存）。
- **不新增**任何表 / 列 / 实体 / 菜单 / 权限 / 用户授权；**无匿名 / 管理员回退**，绝不把空身份当作管理员。
- 列表（`List`，含 `activeOnly`）、可选用视图（`Options`）与全部写入路由**同源同一护栏**，不存在只读旁路。

## 2. 读取 / 写入前的权威引用与载荷校验（保持 ERP-037 语义）

授权通过后，仍在**落库之前**实时校验权威商品引用与提交载荷：

- **商品**：必须存在、未删除且**启用**（`ProductVariantAuthorizationRules.EnsureLiveProductAsync`）；
  外部 / 已删除商品 → `1002` 不存在，已停用商品 → `1001` 参数校验失败（读取与写入都拦截）。
- **载荷**：编码（必填、字符集、长度、规范化后同商品内唯一）、颜色 / 尺码（至少一项、长度）、
  状态（只接受 `1` 启用 / `0` 停用）—— 非法值一律 `1001`，且**不落任何规格行或状态变更**。
- 任一路径被拒时**不落任何规格行、软删除或状态流转**（fail closed）。

## 3. 语义边界

- **响应契约不变**：`ProductVariantDto` 逐字段不变；停用规格历史仍可读，可选用视图仍只返回启用中的规格。
- **唯一性 / 上下限不变**：`UX_BaseProductVariants_ProductCode`（编码）与
  `UX_BaseProductVariants_ProductColorSize`（启用中颜色 + 尺码组合）过滤唯一索引，
  以及 `MaxVariantsPerProduct`（每商品 200 条）与状态取值（`0` / `1`）完全不变。
- 不使用外键，商品 / 规格软删除后历史引用照常可读。
- 除 `BaseProductVariants` 自身外不写任何数据：不改写询价 / 报价 / PI / 订单 / 库存 / 库存流水，
  不拆分或重算已有库存，不改动商品本身，也不触达外部系统。

## 4. 单元测试（`src/ERP.UnitTests`，内存库）

既有 ERP-037 契约（`ProductVariantTests.cs`：规范化 / 唯一性 / 状态 / 有界视图 / 边界与结构契约）全部保留，
用例改经**实时授权测试身份**执行；并新增 `ProductVariantAuthorizationTests.cs`：

| 用例 | 断言 |
|---|---|
| 缺失 / 禁用 / 已删除 / 缺商品菜单 / 无菜单（`[Theory]`） | 7 个路由逐一拒绝（`2000` / `2002`），规格子表逐字节不变 |
| 具备既有商品菜单 | 既有读 / 写契约放行（新增 / 列表 / 可选用 / 修改 / 停用 / 启用 / 删除） |
| 请求之间撤销菜单授权 | 下一次请求 `2002`，零写入 |
| 授权身份下的外部 / 已删除 / 已停用商品与非法载荷 | `1002` / `1001`，零写入 |
| 控制器源码契约 | 7 个路由全部先 `EnsureAuthorizedAsync(_db, CurrentUserId())` 再读写；复用既有 `product` 菜单 |

## 5. 真实 SQL Server 集成测试（`src/ERP.IntegrationTests/ProductVariantAuthorizationSqlServerTests.cs`）

在 GUID 独占的 `NEWERP_AUTOTEST` 目标库上以**真实控制器 + 真实既有授权**自包含播种商品 / 规格与真实授权账号：

| 场景 | 断言 |
|---|---|
| 缺失 / 禁用 / 已删除 / 缺商品菜单（`[Theory]`） | 全部路由对应错误码；`BaseProductVariants` 快照逐字节不变 |
| 具备既有商品菜单 | 新增 / 列表 / 可选用 / 修改 / 停用 / 启用 / 软删除符合既有契约 |
| 请求之间撤销菜单授权 | 下一次读取与删除均 `2002`，行未软删除 |
| 外部 / 已删除 / 已停用商品与非法载荷 | `1002` / `1001`，零写入 |

**真实 SQL 夹具安全口径**

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，且 `IntegratedSecurity=true`；
  实例名 / 库名前缀 / 集成安全在**访问数据库之前**校验（`AssertDedicatedTarget`，含单元级护栏用例）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；**绝不 drop / reset / 复用**任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings` / `.env` / 生产凭据。

## 6. 未执行 / 限制

- 本任务 `validation_profile = safe`、`completion_mode = build`：交付门槛为 **Release 构建 + 全量 `ERP.UnitTests`**；
  真实 SQL 集成用例在具备专用 localdb 的环境下运行，**构建通过不等于阶段验收通过**。
- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置，
  未新增任何表 / 列 / 实体 / 权限 / 连接字符串或密钥。

## 7. 相关实现

- `src/ERP.Api/Controllers/ProductVariantController.cs`：7 个路由均在读写前调用
  `ProductVariantAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId())`。
- `src/ERP.Application/Services/ProductVariantAuthorizationRules.cs`：实时身份 / 账号状态 / 既有商品菜单与权威商品引用判定。
- `src/ERP.Application/Services/ProductVariantService.cs`：落库前的商品引用（`EnsureLiveProductAsync`）与载荷 / 唯一性校验（ERP-037 不变）。
- 复用既有：`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`（角色 → 菜单）、
  既有「商品资料」（`product`）菜单（`SeedData.Menus`）、`SalespersonDataScopeService` 无关（本模块不做数据范围细分）。
