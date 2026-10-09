# 商品 / SKU 货源关系的实时授权（ERP-441）

商品侧货源关系端点（`/api/base/products/{productId}/suppliers` 的列表、可选用选项、新增、修改、停用、启用、
首选、删除）与供应商侧货源列表端点（`/api/base/suppliers/{supplierId}/sourcing`）在读取或写入**任何**
`BaseProductSuppliers` 行之前，统一重新解析：

> 实时身份 → 账号状态 → 既有「商品资料」（`product`）菜单授权 → 既有「供应商资料」（`supplier`）菜单授权

任一缺失即 **fail closed**，绝不返回、新增或改写任何货源关系（含供应商货号、采购单位、MOQ、交期与首选标记）。

## 1. 授权（fail closed，精确复用既有功能菜单）

`ProductSupplierRules.EnsureAuthorizedAsync` 在**每一个**路由的最前面执行（先于任何商品 / 规格 / 供应商读取）：

| 场景 | 判定结果 |
|---|---|
| 无身份 / 非法用户 Id | `2000` 未认证 |
| 账号不存在或已删除 | `2000` 未认证 |
| 账号已禁用 | `2002` 权限不足 |
| 无角色 / 缺少既有 `product` 菜单授权 | `2002` 权限不足 |
| 缺少既有 `supplier` 菜单授权 | `2002` 权限不足 |
| 同时具备 `product` + `supplier` 菜单 | 放行既有读 / 写契约 |

- 菜单授权复用既有「角色 → 菜单」口径（`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`）；
  **每次请求重新查询**，撤销授权后下一次请求立即收敛（绝不缓存）。
- **不新增**任何表 / 列 / 实体 / 菜单 / 权限 / 用户授权；**无匿名 / 管理员回退**，绝不把空身份当作管理员，
  也不因特权角色而跳过菜单检查（超级管理员在既有种子数据中已被授予全部菜单）。
- 商品侧与供应商侧**同源同一护栏**：供应商侧只读列表同样需要两项菜单，不存在只读旁路。

## 2. 写入前的权威引用校验（保持 ERP-037/038 语义）

授权通过后，写入仍在**落库之前**实时校验权威引用（由 ERP-038 既有服务口径执行，本任务不改其语义）：

- **商品**：必须存在、未删除且启用（`CreateAsync` / 重新启用）。
- **可选规格**：必须存在、未删除、启用且**属于该商品**（新选或更换时）；归属不符按参数错误拒绝。
- **供应商**：必须存在、未删除且启用（新增或更换时）。
- 任一路径被拒时**不落任何货源行、首选标记或状态变更**（fail closed）。
- 保持不变的既有语义：作用域键由服务端推导（商品级 `P` / 规格级 `V{Id}`）；
  同一「商品 + 作用域 + 供应商」唯一；同一作用域最多一条**启用中的**首选；
  「引用未变更」时允许继续编辑其他字段（即使供应商 / 规格后来停用或删除）——历史关系保持可维护、可读。

## 3. 语义边界

- **响应契约不变**：`ProductSupplierDto` 逐字段不变；停用关系历史仍可读，可选用视图仍只返回启用中的关系。
- **唯一性 / 首选不变**：`UX_BaseProductSuppliers_ScopeSupplier` 与 `UX_BaseProductSuppliers_ScopePreferred`
  过滤唯一索引与「显式设为首选、先释放旧首选再置新首选」口径完全不变。
- 不使用外键，软删除后历史引用照常可读。
- 除 `BaseProductSuppliers` 自身外不写任何数据：不报价 / 不定价、不生成或改写采购报价与采购订单、
  不改写库存成本与库存流水、不改动任何历史单据与主数据本身。

## 4. 单元测试（`src/ERP.UnitTests/ProductSupplierTests.cs`，内存库）

既有 ERP-038 契约（作用域键 / 规范化 / 唯一性 / 首选 / 有界列表 / 边界与结构契约）全部保留，
用例改经**实时授权测试身份**执行，并新增：

| 用例 | 断言 |
|---|---|
| 缺失 / 禁用 / 已删除 / 缺商品菜单 / 缺供应商菜单 / 无菜单（`[Theory]`） | 商品侧 8 个路由 + 供应商侧 1 个路由逐一拒绝（`2000` / `2002`），货源关系子表逐字节不变 |
| 同时具备两项菜单 | 既有读 / 写契约放行（新增 / 列表 / 可选用 / 首选 / 停用） |
| 请求之间撤销菜单授权 | 下一次请求 `2002`，零写入 |
| 授权身份下的外部商品 / 已删除规格 / 已停用供应商 | `1002` / `1001`，零写入 |
| 控制器源码契约 | 9 个路由全部先 `EnsureAuthorizedAsync(_db, CurrentUserId())` 再读写；复用既有 `product` / `supplier` 菜单 |

## 5. 真实 SQL Server 集成测试（`src/ERP.IntegrationTests/ProductSupplierSourcingAuthorizationSqlServerTests.cs`）

在 GUID 独占的 `NEWERP_AUTOTEST` 目标库上以**真实控制器 + 真实既有授权**自包含播种商品 / 规格 / 供应商 /
货源关系与真实授权账号：

| 场景 | 断言 |
|---|---|
| 缺失 / 禁用 / 已删除 / 缺商品菜单 / 缺供应商菜单（`[Theory]`） | 全部路由对应错误码；`BaseProductSuppliers` 快照逐字节不变（新增 / 修改 / 停用 / 启用 / 首选 / 删除均不落库） |
| 同时具备两项菜单 | 新增（商品级 / 规格级）、列表、可选用、首选切换、停用 / 启用 / 软删除、供应商侧只读全部符合既有契约 |
| 请求之间撤销菜单授权 | 下一次读取与删除均 `2002`，行未软删除 |
| 外部商品 / 归属不符规格 / 已删除规格 / 已停用供应商 / 不存在供应商 | `1002` / `1001`，零写入 |

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

- `src/ERP.Api/Controllers/ProductSupplierController.cs`：`ProductSupplierController` 与 `SupplierSourcingController`
  的 9 个路由均在读写前调用 `ProductSupplierRules.EnsureAuthorizedAsync(_db, CurrentUserId())`。
- `src/ERP.Application/Services/ProductSupplierRules.cs`：实时身份 / 账号状态 / 既有商品与供应商菜单三项判定。
- `src/ERP.Application/Services/ProductSupplierService.cs`：落库前的权威商品 / 规格 / 供应商引用校验（ERP-038 不变）。
- 复用既有：`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`（角色 → 菜单）、
  既有「商品资料」（`product`）与「供应商资料」（`supplier`）菜单（`SeedData.Menus`）。
