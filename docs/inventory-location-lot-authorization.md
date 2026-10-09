# 库位 + 批次库存余额与移动校验的实时授权（ERP-436）

库位 + 批次库存基础的两个端点——`GET /api/inventory/location-lot/balances`（库位级 + 批次级库存余额）与
`POST /api/inventory/location-lot/validate`（校验一次库存移动携带的「仓库库位 + 可选批次」身份）——在读取**任何**
仓库 / 商品 / 库位 / 批次数量或返回任何校验事实之前，统一重新解析：

> 实时身份 → 账号状态 → 既有「库存查询」（`stock-query`）菜单授权 → 权威数据范围

任一缺失即 **fail closed**，绝不返回任何库位 / 批次数量，也不泄露任何仓库、商品、库位或批次身份是否存在。

## 1. 授权（fail closed，精确复用库存查询口径）

两个端点都把当前登录身份（`ClaimTypes.NameIdentifier`）下推到 `InventoryLocationLotService`，
由 `InventoryLocationLotRules.EnsureAuthorizedAsync` **精确委托**既有
`StockQueryAuthorizationRules.EnsureAuthorizedAsync`（ERP-356），不复制判定逻辑、不新增任何权限模型：

| 场景 | 判定入口 | 结果 |
|---|---|---|
| 无身份 / 非法用户 Id | `StockQueryAuthorizationRules.EnsureAuthorizedAsync` | `2000` 未认证 |
| 账号不存在或已删除 | 同上 | `2000` 未认证 |
| 账号已禁用 | 同上 | `2002` 权限不足 |
| 无角色 / 无 `stock-query` 菜单授权 | 同上 | `2002` 权限不足 |
| 受限账号（非特权，含未映射业务员） | 同上 | `2002` 权限不足（无权威仓库级数据范围） |

- 复用既有「角色 → 菜单」口径与既有 `SalespersonDataScopeService`（ERP-097 唯一权威口径）；**每次请求重新查询**，
  撤销授权后下一次请求立即收敛（绝不缓存）。
- **不新增**任何表 / 列 / 菜单 / 权限 / 用户授权，**无匿名 / 管理员回退**，也绝不把空身份当作管理员。

## 2. 数据范围：无权威仓库级范围即拒绝

库存行（`Stocks`）与库存流水（`StockMovements`）以「仓库 + 商品」为粒度，**不存在**客户 / 业务员归属，系统也
**不存在**权威的仓库级数据范围映射。

- **特权账号**（超级管理员 / 系统内置角色 / 显式特权角色）：保留既有全量可见性，数量 / 成本口径完全不变。
- **受限账号**：没有权威范围 → **拒绝读取全局库存**（fail closed），绝不从客户 Id 反推仓库归属、
  也绝不把受限账号降级 / 放大为全局可见。
- `InventoryLocationLotRules.ApplyScope`（委托 `StockQueryAuthorizationRules.ApplyScope`）在**任何**求和 / 计数之前
  把已解析范围应用到库存行与库存流水查询；受限范围即使绕过授权入口也会被守卫拒绝（防御性）。

## 3. 语义边界（只新增读取前判定，不改业务口径）

- **响应契约不变**：`LocationLotBalanceReport`（`ProductTotals` / `LocationLines` / `LotLines` / `IsReconciled` /
  `Note`）与 `ValidatedMovementLocationLot` 逐字段不变。
- **校验语义不变**：批次 / 库位归一化（去首尾空白、超长拒绝）、数量必须为正、仓库必须存在、调拨两仓不同与守恒
  （数量 / 成本守恒 + 拒绝负库存）全部保持；校验幂等，不落库。
- **null 表示未知不变**：`warehouseId` / `productId` 为 `null` 表示不过滤；移动输入 `ProductId` 为 `null` 表示手工行。
- **请求筛选只能收窄**：显式 `warehouseId` / `productId` 不放大已授权范围。
- 两个端点均为只读 / 校验：无 Add / Update / Remove / `SaveChangesAsync`，不写库存 / 流水 / 单据，不执行任意 SQL。

## 4. 单元测试（`src/ERP.UnitTests/InventoryLocationLotFoundationTests.cs`，内存库）

既有 ERP-096 契约（纯规则、移动校验、余额派生与对账）全部保留，并新增实时授权用例：

| 用例 | 断言 |
|---|---|
| 缺失身份 | 余额 / 校验均 `2000` 未认证，不返回任何数量 |
| 禁用 / 已删除 / 无菜单 / 受限（`[Theory]`） | 分别 `2002` / `2000` / `2002` / `2002`，先于任何读取 |
| 特权身份 | 余额数量 / 商品总量与既有口径一致；移动校验归一化；库存 / 流水计数与数量不变，`ChangeTracker` 无未保存变更 |

## 5. 真实 SQL Server 集成测试（`src/ERP.IntegrationTests/InventoryLocationLotAuthorizationSqlServerTests.cs`）

在 GUID 独占的 `NEWERP_AUTOTEST` 目标库上以**真实控制器 + 真实服务 + 真实既有授权**自包含播种仓库 / 商品 /
库存行 / 流水与真实授权账号：

| 场景 | 断言 |
|---|---|
| 无身份 / 禁用 / 已删除 / 无菜单 / 受限（`[Theory]`） | 对应错误码；拒绝后库存行数量与流水计数不变，不返回任何余额 |
| 特权库存查询账号 | 库位 / 商品余额数量正确、可对账；范围外仓库 Id 返回空；移动校验归一化；零写入 |
| 请求之间撤销菜单授权 | 下一次请求 `2002`，库存不变 |
| 非法 / 跨仓输入 | 不存在仓库 / 非正数量 / 库位 / 批次超长 / 调拨两仓相同 / 调出仓现存量不足一律 `1001`；跨仓余额筛选返回空；零变更 |

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

- `src/ERP.Api/Controllers/InventoryLocationLotController.cs`：两个端点把当前身份下推到服务层。
- `src/ERP.Application/Services/InventoryLocationLotService.cs`：授权 / 范围先于任何仓库、商品、库位、批次或数量读取。
- `src/ERP.Application/Services/InventoryLocationLotRules.cs`：委托既有 `StockQueryAuthorizationRules` 的授权与范围守卫。
- 复用既有：`StockQueryAuthorizationRules`（ERP-356）、`SalespersonDataScopeService`（ERP-097）、
  `CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`（角色 → 菜单）。
