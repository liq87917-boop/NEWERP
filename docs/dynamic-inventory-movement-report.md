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
| `POST` | `/api/dynamic-inventory-movement-report/export` | 导出当前页为 Excel（xlsx，只读，复用有界授权预览与选定列顺序） |
| `POST` | `/api/dynamic-inventory-movement-report/pdf` | 导出当前页为分页中文 PDF（只读，复用有界授权预览与选定列顺序） |

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

## 8. 前端字段设计器（ERP-131）

只读、有界的**库存移动字段设计器**：在既有库存移动报表页（`openInventoryMovementReport()`，`wwwroot/js/inventory-movement-report.js`）内，新增字段选择器与当前页 CSV 导出，全程只读不写库。

- **入口与目录**：进入库存移动报表页即加载字段目录 `GET /api/dynamic-inventory-movement-report`（需登录 + `stock-query` 库存查询菜单授权）。字段选择器只由目录返回的 18 项有限白名单渲染为复选框，**无自由填写的字段名或 SQL**；目录 / 授权失败 fail closed，不渲染任何字段并显示错误态。
- **筛选复用**：字段设计器复用页面上方既有筛选控件——仓库 `imr-warehouse`、商品 `imr-product`、截止日期 `imr-asof`、移动窗口 `imr-window-start` / `imr-window-end`、呆滞阈值 `imr-inactive`、每页 `imr-pagesize`（关键字与「仅现存量 > 0」等 ERP-029 扩展口径不进入预览，避免放宽数据可见性）。
- **预览**：点「预览」按白名单字段 + 有界筛选 + 有界分页（page 1 起、pageSize 1~200）`POST /api/dynamic-inventory-movement-report`，按请求顺序渲染返回列名与单元格；列名来自目录（`unit` = 基础单位），无台账的 `lastMovementDate` / `inactivityDays` 显示「未知」，`historyStatus` / `classification` 映射中文文案，全程转义、不暴露范围外数据。
- **当前页 CSV**：仅导出当前预览页，表头与数据行都按返回列（= 选定字段顺序）排列；对 `=` `+` `-` `@` 制表 / 回车开头的文本加单引号转义（公式注入防护），未知值原样保留为「未知」；带 UTF-8 BOM；无数据时不下载空表。
- **状态**：加载、空结果、授权（权限不足 / 未登录）、校验失败（倒置日期 / 非法阈值 / 页大小超限）、网络失败均在结果区可见，全程无写入。
- **审计与只读**：预览 `POST` 由既有 `OperationLogMiddleware` 记录操作日志（读操作），`GET` 目录沿用既有只读约定；本设计器不新增 / 修改 / 删除任何记录，不执行任意 SQL。

## 9. 分组行数分布（ERP-132）

在既有字段设计器工具栏内新增「分组」选择器（`imr-dyn-group`），把当前授权预览页按 `warehouse` / `classification` / `history` 分组，渲染**只读、可访问的行数分布图**。全程只读、只计数，绝不跨不同商品 / 基础单位求和任何数量。

- **分组键（fail closed）**：`none`（默认，不分组）/ `warehouse`（按仓库）/ `classification`（按呆滞分类：`active` / `stagnant` / `unknown`）/ `history`（按台账状态：`ledger` / `window_empty` / `no_history`）。空 / 留空 = `none`；任何其它取值在读取任何数据之前即拒绝（`InvalidParameter`，fail closed），不返回任何数据。
- **页面行数语义**：分组计数只对「当前授权预览页」的库存行计算（复用同一批有界、已授权预览行），**不是全量合计**；翻页 / 改每页条数后按新页重算。`total` 仍为符合筛选条件的库存行总数。
- **只计数不求和**：`Groups[]` 每项只有 `key` / `label` / `count`，不存在任何数量字段；不同商品、不同基础单位（如 PCS / KG）只按行计数，绝不求和为同一个数量。
- **固定分类始终保留**：`classification` / `history` 为固定分类，空分类与未知历史分类（`unknown` / `no_history`）即使计数为 0 也保留在响应中；`warehouse` 为动态分组，只出现本页存在的仓库（按仓库 Id 升序），空页返回空列表。
- **接口**：`POST /api/dynamic-inventory-movement-report` 请求体新增可选 `groupBy`；响应新增 `groupBy` 与 `groups[]`（`none` 或未分组时 `groups` 为空）。
- **前端渲染**：分组结果渲染为可访问的横向条形图（`role="list"` / `role="listitem"`，每项带可见标签与计数），标签与计数全部转义、不注入 HTML；空页 / 不分组不渲染图表，仍显示空结果提示。

## 10. 当前页 Excel 导出（ERP-133）

只读、有界的**当前页 xlsx 导出**：把 ERP-130/131 的「有界、已授权预览」与选定列顺序直接导出为 Excel（xlsx），复用既有 `ERP.Infrastructure.Export.ExcelExporter`。

- **端点**：`POST /api/dynamic-inventory-movement-report/export`，请求体与预览完全相同（`DynamicInventoryMovementReportRequest`），只导出请求 `page` / `pageSize` 对应的**当前页**（单页上限 200，超限直接拒绝），不是全量导出。
- **复用有界授权预览**：每次下载都重新校验身份 + `stock-query` 库存查询菜单授权 + 字段 / 筛选 / 页大小，非法取值在读取任何数据之前即拒绝；只读、不写库、不执行任意 SQL。
- **选定列顺序**：数据工作表列头与数据行都按 `page.Columns`（= 请求选定字段顺序）排列，与预览同源。
- **基础单位 / 未知历史证据**：`unit` 保留基础单位标签（如 PCS / KG），`lastMovementDate` / `inactivityDays` 无台账时保持空（null），`historyStatus` / `classification` 显式保留 `no_history` / `unknown`，不臆造日期、比率，不推断成本 / 金额、不跨不同基础单位聚合。
- **公式注入防护**：文本单元格以 `=` / `+` / `-` / `@` / 制表符 / 回车 / 换行开头时前缀单引号转义（OWASP），保持字面文本、不被当作公式执行（`DynamicInventoryMovementReportRules.EscapeFormulaLeading`）。
- **文件名**：`InventoryMovement_yyyyMMddHHmmss.xlsx`；内容类型 `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`。
- **前端**：字段设计器工具栏新增「📥 导出 Excel（选定列）」，复用当前字段 / 筛选 / 分页组装请求后 `POST` 导出；空数据显示可见错误、不下载空表，授权 / 校验 / 网络失败均在结果区可见。
- **审计与只读**：`POST` 由既有 `OperationLogMiddleware` 记录操作日志（动作「导出」，读操作）；本导出不新增 / 修改 / 删除任何记录，不执行任意 SQL。

## 11. 当前页分页中文 PDF 导出（ERP-134）

只读、有界的**当前页分页中文 PDF 导出**：把 ERP-130/131 的「有界、已授权预览」与选定列顺序直接导出为 PDF，复用既有 `PDFsharp` 与共享中文字体解析器 `SimHeiPdfFontResolver`。

- **端点**：`POST /api/dynamic-inventory-movement-report/pdf`，请求体与预览完全相同（`DynamicInventoryMovementReportRequest`），只导出请求 `page` / `pageSize` 对应的**当前页**（单页上限 200，超限直接拒绝），不是全量导出。
- **复用有界授权预览**：每次下载都重新校验身份 + `stock-query` 库存查询菜单授权 + 字段 / 筛选 / 页大小，非法取值在读取任何数据之前即拒绝，**在任何文件字节返回之前完成**；只读、不写库、不执行任意 SQL。
- **选定列顺序与跨页渲染**：PDF 表头与数据行都按 `page.Columns`（= 请求选定字段顺序）排列，与预览同源；宽列集（如选中全部 18 项字段）按可用页宽拆分为多个「列页」，每个列页总宽不超页宽，**列不被裁切**；行数超过单页可用高度时拆分为多个「行页」，行列页在页头标注「列 X/Y · 行页 A/B」。
- **中文字体与显式失败**：中文字体固定使用 Windows 黑体（SimHei，`simhei.ttf`），与销售订单 / 应收 / 采购订单 / 客户报告包 PDF 共用同一共享解析器，避免进程内互相覆盖为缺字字体；**字体缺失（未安装 SimHei）显式失败**（`ErrorCodes.InternalError`，返回「未找到中文字体 SimHei」），不产出乱码或缺字 PDF。**字体前置条件**：部署机需在 Windows 字体目录安装 `simhei.ttf`。
- **基础单位 / 未知历史证据**：`unit` 保留基础单位标签（如 PCS / KG）；`lastMovementDate` / `inactivityDays` 无台账时显式显示「未知」（绝不回落为 0），`historyStatus` / `classification` 显式保留并映射中文（`no_history` → 「无台账（历史库存 · 未知）」、`unknown` → 「无法判定」）；不臆造日期、比率，不推断成本 / 金额。
- **可选页面行数分布（只计数不求和）**：请求分组（`warehouse` / `classification` / `history`）时，页头追加「本页行数分布（仅统计本页 · 只计数不求和）」；只渲染各分组 `label` + `count`，绝不跨不同商品 / 基础单位（如 PCS / KG）求和任何数量。
- **文件名 / 内容类型**：`InventoryMovement_yyyyMMddHHmmss.pdf`；内容类型 `application/pdf`。
- **前端**：字段设计器工具栏新增「📄 导出 PDF（选定列）」，复用当前字段 / 筛选 / 分页组装请求后 `POST` 导出；空数据显示可见错误、不下载空表；授权（未登录 / 权限不足）、校验、**字体缺失**与网络失败均在结果区可见。
- **审计与只读**：`POST` 由既有 `OperationLogMiddleware` 记录操作日志（动作「导出」，读操作）；本导出不新增 / 修改 / 删除任何记录，不执行任意 SQL。

