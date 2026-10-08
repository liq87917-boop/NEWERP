# 库存查询授权与数据范围（ERP-356）

库存查询的三个只读端点——`GET /api/stocks`（分页列表）、`GET /api/stocks/movements`（库存流水证据）、
`GET /api/stocks/summary`（汇总）——在读取**任何**数量、成本或来源单据字段之前，统一重新解析：

> 实时身份 → 账号状态 → 既有「库存查询」（`stock-query`）菜单授权 → 权威数据范围

任一缺失即 **fail closed**，绝不返回任何数量 / 成本 / 来源单据字段，也不泄露任何计数或聚合值。

## 1. 授权（fail closed）

| 场景 | 判定入口 | 结果 |
|---|---|---|
| 无身份 / 非法用户 Id | `StockQueryAuthorizationRules.EnsureAuthorizedAsync` | `2000` 未认证 |
| 账号不存在或已删除 | 同上 | `2000` 未认证 |
| 账号已禁用 | 同上 | `2002` 权限不足 |
| 无角色 / 无 `stock-query` 菜单授权 | 同上 | `2002` 权限不足 |
| 受限账号（非特权，含未映射业务员） | 同上 | `2002` 权限不足（无权威仓库级数据范围） |

- 复用既有「角色 → 菜单」口径（`SysUserRoles → SysRoleMenus → SysMenus`，忽略按钮型菜单与被删除角色 / 菜单），
  由既有 `CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync` 读取；**每次请求重新查询**，
  撤销授权后下一次请求立即收敛（绝不缓存）。
- 复用既有 `SalespersonDataScopeService`（ERP-097 **唯一权威口径**）判定特权 / 受限身份。
- **不新增**任何表 / 列 / 菜单 / 权限模型，不新增用户授权，也绝不把空身份当作管理员。

## 2. 数据范围口径：无权威范围即拒绝

库存行（`Stocks`）与库存流水（`StockMovements`）以「仓库 + 商品」为粒度，**不存在**客户 / 业务员归属，
系统也**不存在**权威的仓库级数据范围映射。

- **特权账号**（超级管理员 / 系统内置角色 / 显式特权角色，`IsPrivileged == true`）：保留既有全量可见性，
  数量、可用数量、锁定数量、加权平均成本与库存金额语义完全不变。
- **受限账号**：没有权威范围 → **拒绝读取全局库存**（fail closed）；绝不从客户 Id 反推仓库归属，
  也绝不把受限账号降级 / 放大为全局可见。
- `StockQueryAuthorizationRules.ApplyScope` 对受限范围同样拒绝（防御性守卫），保证列表、流水证据与汇总
  使用**同一套**授权 / 范围策略，即使未来放宽授权入口也不会被静默放大为全局可见性。

## 3. 一致性：同一策略先于计数 / 分页 / 聚合

三个端点共用同一个授权 / 范围入口，并在**任何** `Count` / `Sum` / `Distinct` / 分页之前完成判定：

| 端点 | 返回 |
|---|---|
| `GET /api/stocks` | 分页列表：仓库 / 商品名称、数量 / 可用数量 / 锁定数量、加权平均成本、库存金额 |
| `GET /api/stocks/movements` | 库存流水证据：来源单据类型 / Id / 单号、方向、数量、成本基准、结存快照、红字冲销标记 |
| `GET /api/stocks/summary` | 汇总：总数量、可用数量、仓库数 |

- 请求筛选（`warehouseId` / `productId` / `sourceDocNo` / `movementType` / `keyword`）语义不变，**只能收窄，绝不放大**已授权范围。
- 响应契约不变：`StockView` / `StockMovement` / 汇总字段结构不变，对已授权用户的响应与既有实现逐字段一致。
- 未授权时在计数与聚合**之前**拒绝，不泄露任何数量 / 成本 / 来源单据证据。

## 4. 只读边界与审计

- 三个端点均为 `GET`，无 Add / Update / Remove / `SaveChangesAsync`，不执行任意 SQL、不写库、不改单据。
- 授权失败不改动任何库存行、库存流水或单据（单测与真实 SQL 断言均覆盖）。
- 库存流水的**来源单据审计**（`SourceDocType` / `SourceDocId` / `SourceDocNo`）与红字冲销轨迹原样保留，不做裁剪。
- 请求审计沿用既有 `OperationLogMiddleware` 的只读约定。
- `.ai/logs/` 下既有的失败 / 验证日志**原样保留**（不删除、不覆盖、不重写）。

## 5. 单元测试覆盖（`src/ERP.UnitTests/StockQueryAuthorizationTests.cs`）

全部使用内存库（`TestDbFactory`），不连接 SQL Server、不触碰业务库；库存行 / 流水显式播种，便于断言「拒绝时无泄露、无写入」。

| 用例 | 断言 |
|---|---|
| 缺失身份 | 列表 / 流水 / 汇总三个端点一律 `2000` 未认证 |
| 禁用 / 已删除 / 无菜单 / 受限身份（`[Theory]`） | 三个端点分别 `2002` / `2000` / `2002` / `2002`，且先于任何计数 |
| 未映射受限身份（有 `stock-query` 菜单） | `2002`，报文含「权威」说明；明确不授予全局可见性 |
| 请求仓库筛选不能为受限账号扩大范围 | 显式 `warehouseId` 亦拒绝 |
| 撤销菜单授权后 | 下一次请求立即 `2002`（每次请求重新解析） |
| 特权库存查询用户 | 列表数量 / 可用 / 加权平均成本 / 库存金额与既有语义一致 |
| 特权库存查询用户 | 流水证据保留 `SourceDocNo` / `SourceDocType` / 移动类型 |
| 特权库存查询用户 | 汇总返回既有 `totalQuantity` / `totalAvailable` / `warehouseCount` |
| 特权库存查询用户 | `warehouseId` 筛选只能收窄（全量 2 行 → 指定仓库 1 行） |
| 拒绝读取 | 库存 / 流水计数与数量不变，`ChangeTracker` 无未保存变更 |
| 规则层 | 空上下文抛 `ArgumentNullException`；受限范围 `ApplyScope` 一律拒绝；特权范围原样返回 |

既有 `src/ERP.UnitTests/InventoryMovementTests.cs` 的库存流水查询用例改经
`StockQueryTestAuthorization` 注入**真实特权库存查询身份**（含既有 `stock-query` 菜单授权），
保留原有全部数量 / 成本 / 冲销断言（不绕过授权、不放宽可见性）。

## 6. 真实 SQL Server 集成测试（`src/ERP.IntegrationTests/StockQueryAuthorizationSqlServerTests.cs`）

在 GUID 独占的 `NEWERP_AUTOTEST` 目标库上自包含播种仓库 / 商品 / 库存行 / 流水与真实授权账号：

| 场景 | 断言 |
|---|---|
| 无身份 / 禁用 / 已删除 / 无菜单 / 受限身份（`[Theory]`） | 对应错误码；库存行数量与流水计数不变 |
| 特权库存查询用户（SQL 生成身份） | 列表 2 行、指定仓库 1 行；加权平均成本 / 库存金额；来源单据号与类型保留；汇总 14 / 14 / 2 |
| 请求之间撤销菜单授权 | 下一次请求 `2002`，库存不变 |
| **两条独立连接竞争**：并发授权读取 | 两条独立连接结果完全一致，库存 / 流水计数与数量不变 |
| **两条独立连接竞争**：撤销与读取分离 | 连接 A 读取成功 → 连接 B 撤销 → 连接 A 后续读取收敛为拒绝，库存不变 |

**真实 SQL 夹具安全口径**

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，且 `IntegratedSecurity=true`；
  实例名 / 库名前缀 / 集成安全在**访问数据库之前**校验（`AssertDedicatedTarget`，含单元级护栏用例）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；**绝不 drop / reset / 复用**任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings` / `.env` / 生产凭据。
- 身份键一律由 SQL 生成，不硬编码 Id；测试数据带 `SQ-` / `PD-IT-` 前缀便于定位。

## 7. 未执行 / 限制

- 本任务 `validation_profile = safe`、`completion_mode = build`：交付门槛为 **Release build + 全量 `ERP.UnitTests`**；
  真实 SQL 集成用例在具备专用 localdb 的环境下运行（本机未运行），**构建通过不等于阶段验收通过**。
- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置。
- 既有 `InventoryMovementUiTests`（真实 Edge）在**特权管理员**会话下访问 `/api/stocks`，行为与既有全量可见性一致。

## 8. 相关实现

- `src/ERP.Application/Services/StockQueryAuthorizationRules.cs`：授权 / 范围判定与 `ApplyScope` 守卫（纯判定、只读）。
- `src/ERP.Api/Controllers/StockController.cs`：三个只读端点在计数 / 分页 / 聚合**之前**调用统一入口。
- 复用既有：`SalespersonDataScopeService`（ERP-097 数据范围）、`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`（角色 → 菜单）。

