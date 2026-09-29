# 动态库存移动报表预览（ERP-130）

只读、有界的库存移动报表**字段选择预览**：当前库存查询用户从 ERP-029 库存移动报表的**有限既有字段白名单**中选择列，配合仓库 / 商品 / 日期 / 呆滞阈值筛选，预览有界的库存流水台账证据。全程只读，不落库、不改单据、不做成本 / 金额估值。

## 1. 授权（fail closed）

- 端点 `[Authorize]`，且必须在服务端再次校验**当前用户具备既有「库存查询」菜单**（`stock-query`，与 `SeedData.Menus` 同源）。
- 无身份 → `ErrorCodes.Unauthorized`；有身份但无 `stock-query` 菜单授权 → `ErrorCodes.Forbidden`。任一缺失即拒绝，**不返回任何数据**，绝不猜测身份。
- 菜单校验复用既有「角色 → 菜单」口径（`SysUserRoles → SysRoleMenus → SysMenus`，忽略按钮型菜单与被删除角色 / 菜单），每次请求重新查询，撤销授权后立即收敛。

## 2. 接口

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/api/dynamic-inventory-movement-report` | 字段白名单目录（需登录 + 库存查询菜单授权） |
| `POST` | `/api/dynamic-inventory-movement-report` | 按选定字段与有界筛选预览（只读，复用 ERP-029） |

目录返回：`{ fields[], requiredMenuCode, requiredMenuText, maxPageSize, readOnlyText, boundaryText }`。

预览请求体：`{ fields[], asOfDate, windowStart, windowEnd, warehouseId, productId, inactiveDays, page, pageSize }`。

预览响应：`{ columns[], rows[], total, page, pageSize, totalPages, asOfDate, windowStart, windowEnd, inactiveDays, readOnlyText, boundaryText, disclaimerText }`；`rows[]` 每行仅含选定字段（字典键序 = 请求顺序）。

## 3. 字段白名单（有限、有序，共 18 项）

全部来自 ERP-029 `InventoryMovementItem` 的既有字段，不存在任意字段 / 任意 SQL：

`warehouseId`、`warehouseName`、`productId`、`productCode`、`productName`、`spec`、`unit`（基础单位）、`currentQuantity`（基础单位现存量）、`lastMovementDate`（未知 = null）、`inboundQuantity`、`outboundQuantity`、`netQuantity`、`movementCount`、`reversalCount`、`inactivityDays`（未知 = null）、`historyStatus`（`ledger` / `window_empty` / `no_history`）、`classification`（`active` / `stagnant` / `unknown`）、`note`。

未知字段显式拒绝（`InvalidParameter`）；留空 = 返回全部白名单字段（目录顺序）；去重并保持请求顺序。

## 4. 筛选与校验（全部在报告读取之前完成）

- 仓库 `warehouseId` / 商品 `productId`（可空 = 全部）。
- 截止日期 `asOfDate`（可空 = 今天）、移动窗口 `windowStart` / `windowEnd`（可空，按 ERP-029 口径默认推算）。
- 呆滞阈值 `inactiveDays`（默认 90，必须 >= 1）。
- 分页 `page`（>= 1）、`pageSize`（1 ~ 200，**超过 200 直接拒绝**）。

非法取值在**调用报告服务之前**即失败：未知字段、倒置日期（`windowStart > windowEnd`）、非法阈值（`< 1`）、页大小超限（`> 200` 或 `< 1`）→ `ErrorCodes.InvalidParameter`。

## 5. 读取与台账口径（复用 ERP-029）

- 复用 `IReportService.GetInventoryMovementReportAsync`（ERP-029），仓库 / 商品 / 日期 / 阈值筛选与稳定分页全部由既有只读服务完成，本预览不自行拼 SQL、不逐行查库。
- **基础单位**：数量一律为库存流水已落库的基础单位数量，不做事后单位换算。
- **未知历史显式保留**：截止日期前无台账的库存行，`lastMovementDate` / `inactivityDays` 为 null，`historyStatus = no_history`、`classification = unknown`；不臆造日期 / 比率。
- **不推断成本 / 金额**：本预览不估算库存成本或库存价值，仅返回 ERP-029 既有字段值。

## 6. 只读边界与审计

- 控制器只有 `GET`（目录）与 `POST`（预览）；无 Add / Update / Remove / `SaveChangesAsync`，不执行任意 SQL、不做任何写入。
- **审计**：请求由既有 `OperationLogMiddleware` 按 HTTP 方法记录——`POST` 预览落操作日志（读操作，记录用户 / 模块 / 动作 / 路径 / 状态码 / 耗时），`GET` 目录沿用既有只读约定不落日志。
- 不新增 / 修改任何表 / 列 / 权限模型，不做迁移、不回填。

## 7. 限制

- 单页上限 200 行；`total` 为符合筛选条件的库存行总数，本页合计与分类计数沿用 ERP-029「仅统计本页」口径。
- 不提供关键字、导出、分组等 ERP-029 之外的扩展能力，避免放宽数据可见性。
