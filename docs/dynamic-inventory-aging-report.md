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

---

## 8. 前端字段设计器（ERP-136）

在既有「库存库龄与成本估值报表」（ERP-034，`openInventoryAgingReport`）页面内新增**只读字段设计器**（预览）：库存查询用户勾选 ERP-135 有限白名单字段，配合仓库 / 商品 / 截止日期筛选，预览有界的库龄台账与成本估值证据，并导出当前页 CSV。既有静态报表保持不变。

### 8.1 入口与控件

- 设计器直接嵌入 ERP-034 报表页（`wwwroot/js/inventory-aging-report.js`），无新增菜单 / 脚本注册。
- 字段选择器只由 `GET /api/dynamic-inventory-aging-report` 返回的有限白名单目录（32 个字段）渲染为复选框（`name="iar-dyn-field"`），绝无自由填写的字段名或 SQL。
- 仓库 / 商品 / 截止日期 / 每页筛选复用上方既有控件（`iar-warehouse` / `iar-product` / `iar-asof` / `iar-pagesize`）；关键字与「仅现存量 > 0」等 ERP-034 扩展口径不进入本预览。

### 8.2 预览请求与响应

- 预览走 `POST /api/dynamic-inventory-aging-report`，请求体只含 `{ fields, warehouseId, productId, asOfDate, page, pageSize }`：
  - `fields` 只来自目录白名单（去重、保持请求顺序、丢弃未知键）；
  - `page` 最小 1，`pageSize` 钳制在 1 ~ 目录 `maxPageSize`（200）之间。
- 响应按请求顺序返回 `columns[]`（选定字段）与 `rows[]`（仅含选定字段值的字典行），前端按返回列名与单元格顺序渲染，不重排。

### 8.3 渲染口径（与 ERP-135 一致）

- 未知库龄：`evidenceStatus`（full / partial / none）映射为「有台账分层依据 / 部分数量无依据 / 无台账分层依据」；`unknownAgeQuantity`（库龄未知数量）按数值显示。
- 未知成本：`costStatus = unknown` 映射「成本未知」；成本 / 金额类字段（`averageCost` 与 `*Amount`）为 `null` 时显示「未知」，绝不回落为 0。
- CNY 币种：`costCurrency` 恒为 `CNY`，表格与汇总均显式展示。
- 固定库龄分层顺序：0-30 / 31-60 / 61-90 / 91-180 / 180 天以上（`IAR_DYN_BUCKET_KEYS`），绝不重排。
- 全部列名与单元格经 HTML 转义后渲染，不注入脚本。

### 8.4 状态与导出

- 状态：加载（`正在预览…`）、空结果（`没有符合条件`）、授权失败（`权限不足`）、未登录（`未登录 / 登录已过期`）、无效请求（`请求无效`）、网络失败（`网络请求失败`）均可见。
- 导出：`📤 导出 CSV（选定列）` 仅导出当前预览页、按选定列顺序、对公式前导文本（= + - @ 制表 / 回车）加单引号转义、未知值原样保留为「未知」。

### 8.5 验证

- 前端逻辑单测：`node tests/automation/dynamic_inventory_aging_report_ui.test.js`（字段选择、分页上限、渲染、CSV、失败态、接线契约）；
- 语法检查：`node --check src/ERP.Api/wwwroot/js/inventory-aging-report.js`；
- 后端只读 / 授权口径由 `ERP.UnitTests/DynamicInventoryAgingReportTests.cs`（ERP-135）覆盖。

---

## 9. 分组行数分布（ERP-137）

在既有字段设计器工具栏内新增「分组」选择器（`iar-dyn-group`），把当前授权预览页按 `warehouse` / `ageEvidence` / `costEvidence` 分组，渲染**只读、可访问的行数分布图**。全程只读、只计数，绝不跨不同商品 / 基础单位求和任何数量、不把未知成本金额当作 0 求和、不臆造库龄分层、不重算估值。

- **分组键（fail closed）**：`none`（默认，不分组）/ `warehouse`（按仓库）/ `ageEvidence`（按库龄依据：`full` / `partial` / `none`）/ `costEvidence`（按成本依据：`known` / `unknown`）。空 / 留空 = `none`；任何其它取值在读取任何数据之前即拒绝（`InvalidParameter`，fail closed），不返回任何数据。
- **页面行数语义**：分组计数只对「当前授权预览页」的库存库龄行计算（复用同一批有界、已授权预览行），**不是全量合计**；翻页 / 改每页条数后按新页重算。`total` 仍为符合筛选条件的库存行总数。
- **只计数不求和**：`Groups[]` 每项只有 `key` / `label` / `count`，不存在任何数量 / 金额字段；不同商品、不同基础单位（如 PCS / KG）只按行计数，绝不求和为同一个数量；未知成本（金额 null）只按行计数、绝不回落为 0 后求和，也不重算估值。
- **固定证据分类始终保留**：`ageEvidence`（full / partial / none）与 `costEvidence`（known / unknown）为固定证据分类，空分类即使计数为 0 也保留在响应中（确定性排序：full → partial → none；known → unknown）；`warehouse` 为动态分组，只出现本页存在的仓库（按仓库 Id 升序），空页返回空列表。
- **接口**：`POST /api/dynamic-inventory-aging-report` 请求体新增可选 `groupBy`；响应新增 `groupBy` 与 `groups[]`（`none` 或未分组时 `groups` 为空）。
- **审计与只读**：分组仍走同一 `POST` 预览端点，由既有 `OperationLogMiddleware` 按 HTTP 方法记录操作日志（读操作）；本设计器不新增 / 修改 / 删除任何记录，不执行任意 SQL。
- **前端渲染**：分组结果渲染为可访问的横向条形图（`role="list"` / `role="listitem"`，每项带可见标签与计数），标签与计数全部转义、不注入 HTML；空页 / 不分组不渲染图表，仍显示空结果提示。
- **验证**：后端由 `ERP.UnitTests/DynamicInventoryAgingGroupingTests.cs`（ERP-137）覆盖分组键、仓库拆分、缺失证据、空页、权限与只读；前端由 `node tests/automation/dynamic_inventory_aging_grouping_ui.test.js` 覆盖分组键白名单、仓库 / 证据分布渲染、安全图表文本与接线契约。
