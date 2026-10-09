# 只读补货工作台（ERP-106）

## 1. 定位

只读补货工作台把既有库存、商品最低 / 上限库存与启用中的商品货源关系按「仓库 + 商品」逐行只读呈现，
并在「低于最低库存且存在有效上限目标」时给出建议补货量。它是**开发 / 运营只读视图**，不做任何写操作。

## 2. 边界（重要）

- **只读**：接口与页面都不写任何表 —— 无 `Add / Update / Remove / SaveChanges`，不创建订单、不自动选择供应商、不改写库存。
- **逐行口径**：按「仓库 + 商品」一行一条，**不跨仓汇总**、**不做单位换算**。
- **补货建议 = 库存上限 − 现有库存**：仅在「现有库存低于最低库存」且「库存上限为有效目标（`MaxStock > 0` 且 `MaxStock >= MinStock`）」时给出。
- **货源仅作参考**：只展示启用中的货源关系与供应商参考信息，绝不据此自动选供应商、生成采购报价或采购订单、改变库存。

## 3. 阈值与建议判定

| 条件 | 建议状态 | 建议补货量 |
| --- | --- | --- |
| 最低库存有效、现有库存低于最低、上限有效目标 | `replenish` | `MaxStock − 现有库存` |
| 最低库存有效、现有库存低于最低、无有效上限目标 | `below-min-no-target` | 空（不推断） |
| 最低库存缺失（`MinStock <= 0`，或最低 / 上限都缺失） | `missing-threshold` | 空 |
| 上限低于最低库存（`MaxStock < MinStock`） | `invalid-threshold` | 空 |
| 现有库存不低于最低库存 | `adequate` | 空 |

- 现有库存恰好等于最低库存时判为「库存充足」，**不**触发补货。
- 商品资料缺失的历史库存行仍可读，商品名称退化为「商品#Id」，不静默变空。

## 4. 接口

- `GET /api/stocks/replenishment-worksheet`（`[Authorize]`）
  - 查询参数：`warehouseId`（必填）、`productId`（可选）、`page`、`pageSize`（默认 20，上限 200）。
  - 响应：`{ items, total, page, pageSize, readOnlyText, boundaryText, disclaimerText }`。
  - 每行字段：库存行 / 仓库名 / 商品编码 / 名称 / 规格 / 单位 / 现有库存 / 可用数量 / 最低库存 / 库存上限 /
    `belowMinimum` / `suggestedTopUp` / `recommendation` / `recommendationText` / 货源参考（`sourcing`）。

## 5. 读取前授权（ERP-438，fail closed）

`GET /api/stocks/replenishment-worksheet` 在读取**任何**库存数量、可用数量、最低 / 上限库存或货源关系之前，
先复用既有 `StockQueryAuthorizationRules`（ERP-356）重新解析：

> 实时身份 → 账号状态 → 既有「库存查询」（`stock-query`）菜单授权 → 权威数据范围

任一缺失即 **fail closed**，绝不返回任何数量 / 阈值 / 货源。

| 场景 | 判定入口 | 结果 |
|---|---|---|
| 无身份 / 非法用户 Id | `StockQueryAuthorizationRules.EnsureAuthorizedAsync` | `2000` 未认证 |
| 账号不存在或已删除 | 同上 | `2000` 未认证 |
| 账号已禁用 | 同上 | `2002` 权限不足 |
| 无角色 / 无 `stock-query` 菜单授权 | 同上 | `2002` 权限不足 |
| 受限账号（非特权，含未映射业务员） | 同上 | `2002` 权限不足（无权威仓库级数据范围） |

- 控制器与 `StockReplenishmentRules` **精确委托**既有 `StockQueryAuthorizationRules`，不复制判定逻辑、
  **不新增**任何表 / 列 / 菜单 / 权限 / 用户授权，**无匿名 / 管理员回退**，也绝不把空身份当作管理员。
- 授权通过后，`StockReplenishmentRules.ApplyScope`（委托 `StockQueryAuthorizationRules.ApplyScope`）在**任何**
  计数 / 分页之前把已解析范围应用到库存行查询；受限范围即使绕过授权入口也会被守卫拒绝（防御性）。
- **每次请求重新查询**：撤销授权后下一次请求立即收敛（绝不缓存）。
- 库存在本系统**不存在**权威的客户 / 业务员归属，因此受限账号一律拒绝读取全局库存，绝不从客户 Id 反推仓库归属。
- **不改业务口径**：`StockReplenishmentWorksheetDto`、补货建议、边界文案与 `null` 表示未知的行为完全不变；
  请求筛选（仓库 / 商品）只能收窄，绝不放大已授权范围。

## 6. 前端入口

- 库存查询页（`modules-doc2.js` 的 `stock-query` 模块）工具栏新增「📦 库存补货工作台」按钮，
  调用 `openStockReplenishmentWorksheet()` 打开独立只读工作台页。
- `index.html` 注册 `/js/stock-replenishment-worksheet.js`。
- 页面只提供「查询」操作，**不提供**任何创建订单 / 选择供应商 / 修改库存的按钮。

## 7. 测试与验收

- 单元测试见 `src/ERP.UnitTests/StockReplenishmentWorksheetTests.cs`，覆盖：
  阈值边界、缺上限目标、阈值缺失 / 无效、缺失商品、停用供应商、无货源关系、多仓库不跨仓汇总、
  稳定分页、仓库必填校验、只读不写库，以及接口与前端接线契约。
- ERP-438 新增实时授权用例：缺失身份、禁用 / 已删除 / 无菜单 / 受限身份（无权威范围）一律先于任何读取
  fail closed 且不返回任何行、不改写库存 / 商品 / 供应商 / 货源；撤销菜单授权下一次请求收敛；
  空仓 / 不存在（foreign）仓库输入返回空行集。
- 全部使用内存库（`TestDbFactory`），不连接 SQL Server、不执行任何 SQL / 迁移 / seed。
- 真实 SQL Server 集成测试见 `src/ERP.IntegrationTests/StockReplenishmentWorksheetAuthorizationSqlServerTests.cs`：
  在 GUID 独占的 `(localdb)\NEWERP_AutoAcceptance` / `NEWERP_AUTOTEST` 目标库上以**真实控制器 + 真实既有授权**
  自包含播种仓库 / 商品 / 库存 / 供应商 / 货源与授权账号，覆盖特权放行、拒绝矩阵、空 / foreign 仓库输入与撤销收敛。
  夹具在访问数据库前校验实例名 / 库名前缀 / 集成安全，只创建全新 GUID 后缀库，绝不 drop / reset / 复用任何库。

## 8. 关键文件

| 职责 | 文件 |
| --- | --- |
| 控制器 | `src/ERP.Api/Controllers/StockReplenishmentWorksheet.cs` |
| DTO | `src/ERP.Application/DTOs/StockReplenishmentDtos.cs` |
| 纯规则（含读取前授权委托） | `src/ERP.Application/Services/StockReplenishmentRules.cs` |
| 前端 | `src/ERP.Api/wwwroot/js/stock-replenishment-worksheet.js`、`modules-doc2.js`、`index.html` |
| 单元测试 | `src/ERP.UnitTests/StockReplenishmentWorksheetTests.cs` |
| 真实 SQL 集成测试 | `src/ERP.IntegrationTests/StockReplenishmentWorksheetAuthorizationSqlServerTests.cs` |
