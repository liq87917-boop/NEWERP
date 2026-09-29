# 动态销售订单出货 / 财务进度报表预览（ERP-156）

只读、有界、已授权的「动态销售订单出货 / 财务进度报表预览」：销售用户从**有限的 ERP-032 订单证据字段白名单**中按序选择字段，并复用 ERP-032 销售订单出货 / 财务进度报表的既有筛选与稳定分页，预览当前账号可见的订单出货与收款链接证据页。

## 接口

| 方法 | 路由 | 说明 |
| --- | --- | --- |
| `GET` | `/api/sales-orders/dynamic-shipment-finance-report` | 返回 ERP-032 订单证据字段白名单目录（需登录 + 销售订单菜单授权） |
| `POST` | `/api/sales-orders/dynamic-shipment-finance-report` | 按选定字段与有界筛选预览当前页，稳定分页（单页上限 200） |
| `POST` | `/api/sales-orders/dynamic-shipment-finance-report/export` | 把当前页选定列导出为 xlsx（只读，复用有界授权预览与选定列顺序） |
| `POST` | `/api/sales-orders/dynamic-shipment-finance-report/pdf` | 把当前页选定列导出为分页中文 PDF（只读，复用有界授权预览与选定列顺序，宽列集跨页拆分） |

请求体（`DynamicShipmentFinanceReportRequest`）：`fields`（选定字段键，仅限白名单，留空 = 全部白名单字段）、
`customerId`、`currency`、`orderDateFrom` / `orderDateTo`、`shipmentStatus`（none / shipped）、
`financeLinkStatus`（linked / partial / unlinked）、`groupBy`（none / customer / currency / shipmentStatus / financeLinkStatus）、
`page`、`pageSize`。

## 授权（fail closed）

预览与目录都要求当前登录账号具备既有「销售订单」菜单授权（`sales-order`，与 ERP-032 及销售订单工作流同源）。
无身份 → `Unauthorized`；无该菜单授权 → `Forbidden`。每次请求都重新查询「角色 → 菜单」授权，不依赖缓存。

## 业务员数据范围（ERP-097，唯一权威口径）

预览在 ERP-032 源查询**内部**（计数与分页之前）应用 `SalespersonDataScopeService` 客户范围过滤：
特权账号（超级管理员 / 系统内置角色 / 显式特权角色）不过滤；受限制业务员只能看到 `BaseCustomer.EmpId == 本人` 的客户
及其销售订单，未映射到业务员时为空集合（fail closed）。每次请求重新解析，授权 / 员工 / 客户分配变更后立即收敛。

## 字段白名单（有限、有序）

字段全部来自 ERP-032 订单证据行（`SalesOrderShipmentFinanceOrder`），由 `DynamicShipmentFinanceReportRules` 统一供给。
未知字段一律拒绝（`InvalidParameter`），留空 = 返回全部白名单字段；选定列严格按请求顺序返回。

覆盖的证据类别：

- 订单身份：`orderId` / `orderNo` / `orderDate` / `status`；
- 客户：`customerId` / `customerName`；
- 币种与订单金额（原币）：`currency` / `orderAmount` / `recordedDepositAmount`；
- 出货数量与状态（未知用 null，绝不回落为 0）：`orderedQuantity` / `shippedQuantity` / `pendingShipmentQuantity` /
  `outstandingQuantity` / `shipmentStatus` / `hasApprovedShipment` / `shipmentDocumentCount` / `approvedShipmentCount`；
- 收款链接状态与金额（未知用 null，绝不回落为 0）：`financeLinkStatus` / `financeLinkReason` / `linkedAmount` /
  `uncoveredAmount` / `submittedAmount` / `otherCurrencyRecordCount` / `unapprovedRecordCount` /
  `unattributedRecordCount` / `overReceived`；
- 行级说明：`note`。

## 筛选与分页（源读取前校验）

- 客户 Id：非正数直接拒绝；币种：非法取值直接拒绝（复用 ERP-032 的严格币种口径）；
- 出货状态：仅 `none` / `shipped`；收款链接状态：仅 `linked` / `partial` / `unlinked`（`unknown` 只在命中派生上限时出现，无法用既有列条件等价表达，不提供筛选）；
- 订单日期区间：开始晚于结束直接拒绝；
- 每页条数必须在 `1 ~ 200`，超出直接拒绝（不静默截断）。

以上校验全部在调用 `SalesOrderShipmentFinanceReport.ForQueryAsync`（即源读取）**之前**完成；随后复用 ERP-032 的同一套
筛选 → 稳定分页 → 批量派生（分页按客户 + 币种 + 订单日期 + 单据 Id 稳定排序）。

## 分组计数（ERP-160）

预览返回结果新增 `groupBy`（实际生效的分组键，默认 `none`）与 `groups`（当前授权预览页内的销售订单张数分布）。
分组键仅在 `none` / `customer` / `currency` / `shipmentStatus` / `financeLinkStatus` 之间选择（大小写不敏感，留空 = `none`），
未知取值在调用 `SalesOrderShipmentFinanceReport.ForQueryAsync`（源读取）**之前**显式拒绝（`InvalidParameter`，fail closed）。

- `customer` / `currency`：动态分组，只出现本页存在的取值，按客户 Id / 币种升序；币种为原币，不同币种分别成行、绝不合并或换算；
- `shipmentStatus`：固定证据分类 `none` / `partial` / `complete` / `over_shipped` / `unknown`，五类始终保留（计数可为 0）；
- `financeLinkStatus`：固定证据分类 `linked` / `partial` / `unlinked` / `unknown`，四类始终保留（计数可为 0）。

分组只统计**当前授权预览页**的销售订单张数（计数与分页在 ERP-032 源查询内部应用 `SalespersonDataScopeService` 客户范围之后、选定字段投影之前完成），
绝不跨页合并、绝不求和任何金额或数量、绝不跨币种合并或换算、绝不推断收款状态；`unknown` 出货 / 收款链接类别保持可见（不当作已出货 / 已收款 / 已收齐），
金额与数量未知证据仍照实保留为 `null`，`uncoveredAmount` 只作「未覆盖金额」，绝不当作应收余额或收款授权。空页时动态分组返回空列表、固定分类返回全 0。

## 证据边界（重要）

- 金额与数量一律按原币分别成行：`currency` 为原币，不同币种绝不合并、不做汇率换算；
- 无权威引用（`financeLinkStatus == unlinked`）或命中派生上限（`unknown`）的订单，`linkedAmount` / `uncoveredAmount` /
  `submittedAmount` 均为未知（null），绝不推算、绝不回落为 0，且只作「未覆盖金额」；
- 出货数量命中有界派生上限时为未知（null），绝不推算或修复；
- 未知金额与未知数量在预览行中照实保留为 `null`，界面显式显示「未知」。

本预览**不是**应收账款台账、**不是**账龄表、**不是**收款授权或结算结果，也**不推断**发票到订单的链接。
「未覆盖金额」只是订单金额与权威计入金额之差，不得当作应收余额或据以催收。

## 只读与审计

- 全程只读：无 Add / Update / Remove / SaveChanges，不执行任意 SQL、不写库；
- 请求由既有 `OperationLogMiddleware` 按 HTTP 方法记录审计（预览与后续下载端点共用）。

## 报表限制（有界）

- 单页最多 `200` 行，超出直接拒绝；`total` 为符合筛选条件且在当前业务员数据范围内的未删除销售订单总数；
- 未知金额 / 未知数量照实保留为 `null`，绝不回落为 0、绝不跨币种合并或换算；
- 不新增表 / 列 / 权限模型，不执行迁移、生产 SQL、真实数据库操作或部署。

## Excel 导出（ERP-158）

`POST /api/sales-orders/dynamic-shipment-finance-report/export` 请求体与预览完全相同
（`DynamicShipmentFinanceReportRequest`），复用同一有界、已授权预览管线（每次请求重新校验身份 / 销售订单菜单授权 /
字段 / 筛选 / 页大小 / 业务员数据范围），**只导出请求 `page` / `pageSize` 对应的当前页选定列**（单页上限 200，
超限直接拒绝），不是全量导出。

导出口径与导出限制：

- 数据工作表复用 `ERP.Infrastructure.Export.ExcelExporter`；列头 = 选定字段的中文标签，列顺序 = 请求 `fields` 顺序，仅当前页行；
- 未知金额（`linkedAmount` / `uncoveredAmount` / `submittedAmount` 为 null）与未知数量（`orderedQuantity` / `shippedQuantity` /
  `pendingShipmentQuantity` / `outstandingQuantity` 为 null）导出为**空单元格（未知）**，绝不回落为 0、不推算、不修复；
- 金额与数量一律按原币分别成行：`currency` 为原币，不同币种绝不合并、不做汇率换算、无跨币种总额；
- 公式注入防护：文本单元格以 `=` / `+` / `-` / `@` / 制表符 / 回车 / 换行开头时，前缀单引号转义为字面文本、
  不生成公式单元格（`DynamicShipmentFinanceReportRules.EscapeFormulaLeading`）；
- 空页仍返回仅含表头的工作簿（下载限制：不下载空数据表，前端空结果显示可见错误、不触发下载）；
- 文件名 `ShipmentFinanceReport_yyyyMMddHHmmss.xlsx`；内容类型 `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`；
- 只读 + 审计：无写入、无任意 SQL；`POST` 由既有 `OperationLogMiddleware` 记录审计（动作「导出」）。

## PDF 导出（ERP-159）

`POST /api/sales-orders/dynamic-shipment-finance-report/pdf` 请求体与预览完全相同
（`DynamicShipmentFinanceReportRequest`），复用同一有界、已授权预览管线（每次请求重新校验身份 / 销售订单菜单授权 /
字段 / 筛选 / 页大小 / 业务员数据范围），**只导出请求 `page` / `pageSize` 对应的当前页选定列**（单页上限 200，
超限直接拒绝），不是全量导出。生成字节前完成全部授权与校验（fail closed），字体缺失在任何文件字节返回前即失败。

导出口径与导出限制：

- 复用 `ERP.Infrastructure.Export.DynamicShipmentFinancePdfExporter`，以 PDFsharp 6.2.4 分页渲染；
- 表头与数据行都按 `page.Columns`（= 请求选定字段顺序）排列，与预览同源；宽列集按可用页宽贪心拆成多个「列页」、
  行数超出单页可用高度时拆成多个「行页」，每个列页总宽不超页宽，**列不被裁切**；标题 / 元信息标注「列 X/Y · 行页 A/B」；
- 中文标签照实渲染（订单号 / 订单金额 / 已关联金额 / 出货状态 / 收款链接状态等）；
- 金额与数量一律按原币分别成行：`currency` 为原币，不同币种绝不合并、不做汇率换算、无跨币种总额；
- 未知金额（`linkedAmount` / `uncoveredAmount` / `submittedAmount` 为 null）与未知数量（`orderedQuantity` / `shippedQuantity` /
  `pendingShipmentQuantity` / `outstandingQuantity` 为 null）照实渲染为「未知」，绝不回落为 0、不推算、不修复；
- 出货状态 / 收款链接状态 / 单据状态映射中文文案（如「已出齐」「未链接（金额未知）」「已审核」），布尔显示 是 / 否；
- 空页仍返回仅含表头与空态提示的 PDF（下载限制：前端空结果可见、不触发下载）；
- 文件名 `ShipmentFinanceReport_yyyyMMddHHmmss.pdf`；内容类型 `application/pdf`；
- 只读 + 审计：无写入、无任意 SQL；`POST` 由既有 `OperationLogMiddleware` 记录审计（动作「导出」）。

### 字体前置条件（Windows · 缺失显式失败）

PDF 中文一律使用 Windows 黑体 **SimHei**（`simhei.ttf`），由共享解析器
`ERP.Infrastructure.Export.SimHeiPdfFontResolver` 定位并注册（与其它报表 PDF 共用）。运行时若未找到
`simhei.ttf`，导出**显式失败**（`ErrorCodes.InternalError`，前端在结果区可见「未找到中文字体 SimHei」错误），
**绝不产出乱码或缺字的 PDF**、也不替换为其它字体。部署机器需在 Windows 字体目录安装黑体（默认随 Windows 中文版提供）。

## 文件地图

- `src/ERP.Application/DTOs/DynamicShipmentFinanceReportDtos.cs`：目录 / 请求 / 结果 DTO；
- `src/ERP.Application/Services/DynamicShipmentFinanceReportRules.cs`：字段白名单、校验、行投影、目录（纯规则）；
- `src/ERP.Api/Controllers/DynamicShipmentFinanceReportController.cs`：授权 + 业务员数据范围 + 复用 ERP-032 只读派生 + 选定列投影；
- `src/ERP.Infrastructure/Export/DynamicShipmentFinancePdfExporter.cs`：ERP-159 PDF 导出（分页中文 PDF、列页 / 行页拆分、原币与未知证据、SimHei 缺失显式失败）；
- `src/ERP.Api/Controllers/SalesOrderShipmentFinanceReport.cs`：ERP-032 权威派生，新增可选 `SalespersonDataScope` 参数（在源查询内部先于计数与分页过滤客户范围）；
- `src/ERP.UnitTests/DynamicShipmentFinanceReportTests.cs`：预览单元测试（内存库，不连 SQL Server、不启动 API）；
- `src/ERP.UnitTests/DynamicShipmentFinanceGroupingTests.cs`：ERP-160 分组计数单元测试（分组键、客户 / 币种 / 出货状态 / 收款链接状态、unknown 保留、空页 / 分页、授权拒绝、不写库）；
- `src/ERP.UnitTests/DynamicShipmentFinanceExcelTests.cs`：ERP-158 Excel 导出单元测试（列顺序、页上限、数据范围、未知值、币种、公式安全、不写库、下载限制）；
- `src/ERP.UnitTests/DynamicShipmentFinancePdfTests.cs`：ERP-159 PDF 导出单元测试（签名、页面边界、字段顺序、原币与未知证据、SimHei 嵌入与缺失失败、授权 / 校验拒绝、不写库）；
- `src/ERP.UnitTests/SalesOrderShipmentFinanceReportTests.cs`：ERP-032 报表单元测试（含 ERP-156 复用的源查询范围过滤）。

## 前端字段设计器（ERP-157）

在既有「销售订单出货 / 财务进度报表」页（`openSalesOrderShipmentFinanceReport`）工具栏提供「🧩 字段设计器」入口
（`openSalesOrderShipmentFinanceFieldDesigner`），复用 ERP-156 只读预览，实现可视化的字段 / 筛选设计器。

### 工作流

1. **打开设计器**：从出货 / 财务进度报表页点击「🧩 字段设计器」，前端先 `GET /api/sales-orders/dynamic-shipment-finance-report`
   拉取 ERP-156 有限字段白名单目录（27 个字段）；授权 / 未登录 / 网络失败时 fail closed 并整页显示错误态，不渲染任何字段。
2. **字段选择**：仅由目录白名单渲染为复选框（`dsf-field`），无自由填写的字段名；`dsfSelectFields` 只保留白名单键、去重、保持请求顺序、丢弃未知键。
3. **有界筛选**：客户（`/api/base/customers`）/ 币种（复用 `CURRENCY_NAME_OPTS`）/ 订单日期（起止）/ 出货状态（none / shipped）/
   收款链接状态（linked / partial / unlinked），与 ERP-156 请求体一致；关键字不在 ERP-156 筛选面内，不提供。
4. **预览**：`POST /api/sales-orders/dynamic-shipment-finance-report`，请求体只含「白名单字段 + 有界筛选 + 有界分页（1 ~ 200）」；
   当前账号数据范围由服务端（`SalespersonDataScopeService`）在预览内解析，前端不做任何客户范围推断。
5. **渲染**：按请求顺序渲染返回列名与单元格；未知金额 / 未知数量（null）显式显示「未知」、绝不回落为 0；金额按原币成行、绝不跨币种合并或换算；
   权限不足 / 未登录 / 无效请求 / 网络失败 / 空结果 / 加载中各自可见。
6. **导出 CSV**：导出当前页选定列（`dsfCsv`），未知保留「未知」、公式前导（`= + - @` 或含制表 / 换行）加单引号防注入、内部引号翻倍。
   预览与导出均由既有 `OperationLogMiddleware` 按 HTTP 方法记录审计。
7. **导出 Excel**：工具栏「📥 导出 Excel」复用当前字段 / 筛选 / 分页组装请求后 `POST /api/sales-orders/dynamic-shipment-finance-report/export`
    （`dsfExportExcel`）；成功（xlsx 附件）触发下载，空结果 / 授权 / 校验 / 网络失败均在结果区可见、不下载任何内容；未知金额 / 数量保持空单元格（未知）。
8. **导出 PDF**：工具栏「📄 导出 PDF」复用当前字段 / 筛选 / 分页组装请求后 `POST /api/sales-orders/dynamic-shipment-finance-report/pdf`
    （`dsfExportPdf`）；成功（`application/pdf` 附件）触发下载；空结果 / 授权 / 校验 / 字体缺失（`SimHei` 缺失返回 `5000` 内部错误，结果区可见「未找到中文字体 SimHei」）/
    网络失败均在结果区可见、不下载任何内容；未知金额 / 数量显式渲染为「未知」。


### 前端单测

`tests/automation/dynamic_shipment_finance_ui.test.js`（`node tests/automation/dynamic_shipment_finance_ui.test.js`）覆盖：
字段选择、请求边界与分页、单元格渲染（未知 = 「未知」）、CSV（未知保留 + 公式前导转义）、失败态，以及前端接线契约（无任意 SQL / 自由字段名）。

### 前端文件

- `src/ERP.Api/wwwroot/js/sales-order-progress.js`：新增 `DSF_*` 常量、`dsf*` 纯函数 / 渲染 / 请求与设计器入口
  （`openSalesOrderShipmentFinanceFieldDesigner`），并在报表页工具栏注册「🧩 字段设计器」按钮。
