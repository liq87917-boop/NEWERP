# 动态库存库龄与成本估值报表预览（ERP-135）

只读、有界的库存库龄与成本估值报表**字段选择预览**：当前库存查询用户从 ERP-034 库存库龄与成本估值报表的**有限既有字段白名单**中选择列，配合仓库 / 商品 / 截止日期筛选，预览有界的库存库龄台账与成本估值证据。全程只读，不落库、不改单据、不重算历史成本、不做跨币种合并或汇率换算。

## 1. 授权（fail closed）

- 端点 `[Authorize]`，且必须在服务端再次校验**当前用户具备既有「库存查询」菜单**（`stock-query`，与 `SeedData.Menus` 同源）。
- 无身份 → `ErrorCodes.Unauthorized`；有身份但无 `stock-query` 菜单授权 → `ErrorCodes.Forbidden`。任一缺失即拒绝，**不返回任何数据**，绝不猜测身份。
- 菜单校验复用既有「角色 → 菜单」口径（`SysUserRoles → SysRoleMenus → SysMenus`，忽略按钮型菜单与被删除角色 / 菜单），每次请求重新查询，撤销授权后立即收敛。

## 2. 接口

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/api/dynamic-inventory-aging-report` | 字段白名单目录（需登录 + 库存查询菜单授权） |
| `POST` | `/api/dynamic-inventory-aging-report` | 按选定字段与有界筛选预览（只读，复用 ERP-034） |

目录返回：`{ fields[], requiredMenuCode, requiredMenuText, maxPageSize, readOnlyText, boundaryText }`。

预览请求体：`{ fields[], asOfDate, warehouseId, productId, page, pageSize }`。

预览响应：`{ columns[], rows[], total, page, pageSize, totalPages, asOfDate, costCurrency, readOnlyText, boundaryText, disclaimerText }`；`rows[]` 每行仅含选定字段（字典键序 = 请求顺序）。

## 3. 字段白名单（有限、有序，共 32 项）

全部来自 ERP-034 `InventoryAgingItem` 的既有字段，不存在任意字段 / 任意 SQL：

- **标量字段（22 项）**：`warehouseId`、`warehouseName`、`productId`、`productCode`、`productName`、`spec`、`unit`（基础单位）、`currentQuantity`（基础单位现存量）、`knownAgedQuantity`（有台账分层依据数量）、`unknownAgeQuantity`（库龄未知数量）、`evidenceStatus`（`full` / `partial` / `none`）、`averageCost`（移动加权平均成本）、`costStatus`（`known` / `unknown`）、`costCurrency`（库存行持久化的库存成本币种 `CNY`）、`authoritativeAmount`（权威库存金额 `Stocks.TotalCost`，未知 = null）、`agedAmount`（分层金额合计，未知 = null）、`unknownAgeAmount`（库龄未知金额，未知 = null）、`unknownCostQuantity`（成本未知数量）、`ledgerDeficitQuantity`、`unpairedQuantity`、`reversalCount`、`note`。
- **固定 5 格库龄分层字段（10 项）**：`bucket0To30Quantity` / `bucket0To30Amount`、`bucket31To60Quantity` / `bucket31To60Amount`、`bucket61To90Quantity` / `bucket61To90Amount`、`bucket91To180Quantity` / `bucket91To180Amount`、`bucketOver180Quantity` / `bucketOver180Amount`（顺序与 `InventoryAgingSemantics.BucketKeys` 一致；金额在成本未知时为 null）。

未知字段显式拒绝（`InvalidParameter`）；留空 = 返回全部白名单字段（目录顺序）；去重并保持请求顺序。

## 4. 筛选与校验（全部在报告读取之前完成）

- 仓库 `warehouseId` / 商品 `productId`（可空 = 全部；非空必须为正整数）。
- 截止日期 `asOfDate`（可空 = 今天；年份 < 1900 视为无效）。
- 分页 `page`（>= 1，小于 1 按 1 处理）、`pageSize`（1 ~ 200，**超过 200 直接拒绝**）。

非法取值在**调用报告服务之前**即失败：未知字段、非法日期（年份 < 1900）、非法仓库 / 商品 Id（<= 0）、页大小超限（> 200 或 < 1）→ `ErrorCodes.InvalidParameter`。

## 5. 读取与口径（复用 ERP-034）

- 复用 `IReportService.GetInventoryAgingReportAsync`（ERP-034），仓库 / 商品 / 截止日期筛选与稳定分页全部由既有只读服务完成，本预览不自行拼 SQL、不逐行查库。
- **权威 FIFO 库龄分层**：库龄分层由库存流水（`StockMovements`）按 FIFO 派生；0-30 / 31-60 / 61-90 / 91-180 / 180 天以上（均为闭区间）；红字冲销按 `ReversalOfMovementId` 权威配对（入库冲销扣回原层、出库冲销按原出库日期回补），原流水不删除、不改写。
- **未知库龄证据分离**：没有台账分层依据的数量单列为「库龄未知」（`unknownAgeQuantity`），不放进任何分层、不臆造入库日期。
- **未知成本证据分离**：估值只使用库存行持久化的移动加权平均成本与库存金额（`Stocks.AverageCost` / `Stocks.TotalCost`）；成本依据缺失时数量与金额记为未知（`costStatus = unknown`，金额 null），**绝不回落为 0、不估算成本**。
- **持久化币种口径**：金额币种为库存行 / 库存流水没有币种列、持久化成本历来以人民币计价的 `CNY`（`costCurrency`）；不做跨币种合并、不从文本字典推断汇率。
- **只列正向库存**：沿用 ERP-034 默认的 `OnlyPositiveQuantity`（只列当前现存量 > 0 的库存行）；关键字与其它 ERP-034 扩展口径不在此预览暴露，避免放宽数据可见性。

## 6. 只读边界与审计

- 控制器只有 `GET`（目录）与 `POST`（预览）；无 Add / Update / Remove / `SaveChangesAsync`，不执行任意 SQL、不做任何写入。
- **审计**：请求由既有 `OperationLogMiddleware` 按 HTTP 方法记录——`POST` 预览落操作日志（读操作，记录用户 / 模块 / 动作 / 路径 / 状态码 / 耗时），`GET` 目录沿用既有只读约定不落日志。
- 不新增 / 修改任何表 / 列 / 权限模型，不做迁移、不回填。

## 7. 限制与证据边界

- 单页上限 200 行；`total` 为符合筛选条件的库存行总数，本页合计与分层合计沿用 ERP-034「仅统计本页」口径。
- 本预览为只读快照：不替代库存库龄与成本估值报表主口径，不重算、不重建历史成本、不计提跌价准备；不提供关键字、导出、分组等 ERP-034 之外的扩展能力。
