# 动态供应商采购敞口预览（ERP-148）

只读、有界、已授权的「动态供应商采购敞口预览」：采购用户从**有限的采购订单敞口证据字段白名单**中按序选择字段，并复用
ERP-031 供应商采购敞口报表的既有筛选与稳定分页，预览当前账号可见的采购订单敞口页。

## 接口

| 方法 | 路由 | 说明 |
| --- | --- | --- |
| `GET` | `/api/supplier-purchase-exposure/report` | 返回采购订单敞口证据字段白名单目录（需登录 + 采购订单菜单授权） |
| `POST` | `/api/supplier-purchase-exposure/report` | 按选定字段与有界筛选预览当前页，稳定分页（单页上限 200） |
| `POST` | `/api/supplier-purchase-exposure/report/export` | 导出当前选定页为 Excel（xlsx，只读，复用有界授权预览与选定列顺序） |

请求体（`DynamicSupplierExposureReportRequest`）：`fields`（选定字段键，仅限白名单）、`supplierId`、`currency`、
`orderDateFrom` / `orderDateTo`、`linkStatus`（linked / ambiguous / unavailable）、`groupBy`（分组键，仅
none / supplier / currency / linkStatus / receiptStatus）、`summaryMode`（金额汇总模式，仅
none / supplierCurrency / supplierCurrencyLink）、`keyword`、`page`、`pageSize`。

## 授权（fail closed）

预览与目录都要求当前登录账号具备既有「采购订单」菜单授权（`purchase-order`，与 ERP-031 及采购订单工作流同源）。
无身份 → `Unauthorized`；无该菜单授权 → `Forbidden`。每次请求都重新查询「角色 → 菜单」授权，不依赖缓存。

## 字段白名单（有限、有序）

字段全部来自 ERP-031 采购订单敞口证据行（`SupplierPurchaseExposureOrder`），由
`DynamicSupplierExposureReportRules` 统一供给。未知字段一律拒绝（`InvalidParameter`），留空 = 返回全部白名单字段；
选定列严格按请求顺序返回。

覆盖的证据类别：

- 订单身份：`orderId` / `orderNo` / `orderDate` / `status`；
- 供应商：`supplierId` / `supplierName`；
- 币种与订单金额（原币）：`currency` / `orderedAmount`；
- 结算引用链：`owningSalesOrderNo` / `recordedSettlementProgress`；
- 链接状态与结算金额（未知用 null，绝不回落为 0）：`linkStatus` / `linkReason` / `settledAmount` /
  `outstandingAmount` / `submittedAmount` / `overSettled`；
- 收货状态与数量（未知用 null，绝不回落为 0）：`receiptStatus` / `orderedQuantity` / `receivedQuantity` /
  `outstandingQuantity` / `pendingQuantity`；
- 行级说明：`note`。

## 筛选与分页（源读取前校验）

- 供应商 Id：非正数直接拒绝；币种：非法取值直接拒绝（复用 ERP-031 的严格币种口径）；链接状态：非法取值直接拒绝；
- 订单日期区间：开始晚于结束直接拒绝；关键字长度超过 50 直接拒绝；
- 每页条数必须在 `1 ~ 200`，超出直接拒绝（不静默截断）。

以上校验全部在调用 `SupplierPurchaseExposure.ForQueryAsync`（即源读取）**之前**完成；随后复用 ERP-031 的同一套
筛选 → 稳定分页 → 批量派生。

## 证据边界（重要）

- 金额一律按原币分别成行：`currency` 为原币，不同币种绝不合并、不做汇率换算；
- 链接不唯一（`ambiguous`）或缺失（`unavailable`）的订单，`settledAmount` / `outstandingAmount` / `submittedAmount`
  均为未知（null），绝不推算、绝不回落为 0，且只作「未链接敞口」；
- 收货数量命中有界派生上限时为未知（null），绝不推算或修复；
- 未知金额与未知数量在预览行中照实保留为 `null`，界面显式显示「未知」。

本预览**不是**应付账款台账、**不是**账龄表、**不是**付款授权、**不是**税务申报，也不构成结算确认。
「未链接敞口」只是尚未按既有引用归属到订单的订单金额，不得当作应付余额或据以付款。

## 只读与审计

- 全程只读：无 Add / Update / Remove / SaveChanges，不执行任意 SQL、不写库；
- 请求由既有 `OperationLogMiddleware` 按 HTTP 方法记录审计。

## Excel 导出（ERP-150）

- 路径：`POST /api/supplier-purchase-exposure/report/export`，请求体与预览完全相同（`DynamicSupplierExposureReportRequest`）。
- **复用预览**：每次导出都重新校验当前登录用户 Id、`purchase-order` 采购订单菜单授权、字段白名单、筛选与页大小
  （1~200），再按同一条有界预览查询读取当前页，全程只读、不执行任意 SQL、不写库。
- **仅导当前页**：只导出请求 `page` / `pageSize` 对应的那一页选定列（不是全量导出），单页仍受 200 行上限约束。
- **原样保留**：供应商 / 采购单号 / 归属销售订单号等标识与金额**原币**原样保留，不做跨币种换算或汇总；
  链接不唯一（`ambiguous`）/ 无引用（`unavailable`）的未知结算金额与未知收货数量照实保留（空单元格 = 未知，绝不回落为 0）。
- **公式注入防护**：文本单元格以 `=` / `+` / `-` / `@` / 制表符 / 回车 / 换行开头时，前缀单引号转义为字面文本，
  不生成公式单元格。
- **只读 + 审计**：无写入、无任意 SQL；`POST` 由既有 `OperationLogMiddleware` 记录审计（动作「导出」）。
- 文件名 `SupplierPurchaseExposure_yyyyMMddHHmmss.xlsx`；内容类型
  `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`。

## PDF 导出（ERP-151）

- 路径：`POST /api/supplier-purchase-exposure/report/pdf`，请求体与预览完全相同（`DynamicSupplierExposureReportRequest`）。
- **复用预览**：每次导出都重新校验当前登录用户 Id、`purchase-order` 采购订单菜单授权、字段白名单、筛选与页大小
  （1~200），再按同一条有界预览查询读取当前页，全程只读、不执行任意 SQL、不写库。
- **仅导当前页**：只导出请求 `page` / `pageSize` 对应的那一页选定列（不是全量导出），单页仍受 200 行上限约束。
- **分页中文 PDF**：使用 PDFsharp 6.2.4 与共享黑体解析器（`SimHeiPdfFontResolver`）渲染 A4 页面；选定字段、中文标签、
  原币（不同币种分别成行、绝不换算或合并）与链接不唯一（`ambiguous`）/ 无引用（`unavailable`）的未知结算金额、未知收货
  数量照实保留（null →「未知」，绝不回落为 0）；宽列集按可用页宽拆成多个「列页」、行数超出按「行页」拆分，避免列被裁切。
- **字体前提**：中文字体固定使用 Windows 黑体（SimHei，simhei.ttf）；字体缺失时显式失败（不产出乱码或缺字 PDF）。
- **只读 + 审计**：无写入、无任意 SQL；`POST` 由既有 `OperationLogMiddleware` 记录审计（动作「导出」）。
- 文件名 `SupplierPurchaseExposure_yyyyMMddHHmmss.pdf`；内容类型 `application/pdf`。

## 前端字段设计器（ERP-149）：视觉字段选择 + 所选列预览与 CSV 导出

前端工作台 `wwwroot/js/supplier-purchase-exposure.js` 在既有「供应商采购敞口报表」工具栏新增「🎛 字段设计器」入口，
保持静态视图，只读、有界、可单测：

- 打开设计器时先 `GET /api/supplier-purchase-exposure/report` 拉取有限字段白名单目录，目录加载失败 / 未登录 / 无采购订单菜单授权一律 fail closed（整页可见拒绝原因，不渲染任何字段）；
- 字段选择器只由目录白名单渲染为复选框（`name="spe-des-field"`），**没有自由填写的字段名 / SQL**；勾选状态经 `speDesSelectFields` 规范化（去重、保持顺序、丢弃未知键）；
- 预览复用工作台**当前筛选**（供应商 / 币种 / 订单日期 / 链接状态 / 关键字 / 每页），并 `POST /api/supplier-purchase-exposure/report`，只发送「白名单字段 + 当前筛选 + 有界分页（pageSize 1~200）」；请求体由 `speDesBuildRequest` 组装，未知字段 / 非法分页绝不进入请求；
- 结果渲染 `speDesResultHtml` / `speDesTableHtml`：按后端返回的列名与顺序渲染选定列，单元格经 HTML 转义（无 unsafe HTML）；链接状态（linked / ambiguous / unavailable）、收货状态（unknown）与未知结算 / 收货数量（null）显式显示「未知」，绝不回落为 0；不同币种分别成行、绝不合并或换算；口径 / 边界 / 免责文案照实展示；
- 所选列 CSV 由 `speDesCsv` 生成：表头为返回的列名、行内仅选定字段；未知值保留「未知」、公式首字符（= + - @ 制表符 / 回车）转义、双引号转义、CRLF + BOM；空页可见错误，不下载仅表头文件；
- 加载 / 空结果 / 授权 / 未登录 / 无效请求 / 网络失败状态均在结果区可见（fail closed，不暴露范围外数据）。

翻页复用 `speDesPage`（有界：最小第 1 页），每次预览都重新走 ERP-148 授权与校验；全程只读，无写入。
CSV 只导出当前预览页选定列证据，`POST` 由既有 `OperationLogMiddleware` 记录审计。

### 单元测试

`tests/automation/dynamic_supplier_exposure_ui.test.js`（Node，无需浏览器）覆盖：字段选择（仅白名单、去重、丢弃未知键）、
请求边界（分页有界、筛选仅复用工作台当前筛选）、未知证据渲染（链接 / 收货 / 结算 / 数量 null 不回落 0）、
表格渲染（转义、不同币种分行、分页接线）、空结果与失败态、所选列 CSV（未知保留 + 公式转义 + 引号转义），以及前端接线契约。

### Excel 导出测试（`ERP.UnitTests/DynamicSupplierExposureExcelTests.cs`）

- 导出当前页的选定列顺序与行值（仅选定字段、保持请求顺序）；
- 供应商 / 采购单号等标识与原币保留（不做换算）、金额数值单元格；
- 公式前导文本转义（`=` / `+` / `-` / `@` 开头前缀单引号、非公式单元格）；
- 仅导出当前页（`page` / `pageSize`，单页受 200 上限约束）；
- 链接不唯一 / 无引用订单的未知结算金额保持未知（空单元格、绝不回落为 0），链接状态分列保留；
- 无身份 / 无采购订单菜单授权 / 页大小超限 / 未知字段拒绝（先于发送字节）；
- 空页仅表头；只读不写库（`SaveChangesAsync` 调用次数恒为 0）。

## 分组计数（ERP-152）

采购用户可选择一个**有限分组键**，预览响应额外返回「当前授权预览页」的采购订单张数分布（`groupBy` + `groups`）。

- 分组键仅接受 `none` / `supplier` / `currency` / `linkStatus` / `receiptStatus`（大小写不敏感），
  留空 = `none`（不分组）；其他取值在**读取任何源数据之前**即 `InvalidParameter` fail closed 拒绝。
- 计数只统计「当前授权预览页」的采购订单张数，绝不求和任何金额或数量、绝不跨币种合并或换算；`total` 仍是符合筛选条件的订单总数。
- `supplier` / `currency` 为**动态分组**：只出现本页存在的取值，按供应商 Id / 币种升序。
- `linkStatus` 为**固定证据分类**（linked / ambiguous / unavailable）：三类始终保留（计数可为 0），
  `ambiguous`（链接不唯一）与 `unavailable`（无可用链接）保持可见，绝不因计数为 0 而省略。
- `receiptStatus` 为**固定证据分类**（none / partial / complete / over_received / unknown）：五类始终保留（计数可为 0），
  `unknown`（收货数量命中派生上限，未知）保持可见。
- 计数结果 `DynamicSupplierExposureReportGroupDto` 只含 `key` / `label` / `count`，**没有**金额 / 数量字段，
  从结构上杜绝跨币种、跨单位求和。
- 授权、只读与审计与预览完全一致：每次请求重新校验当前登录用户与 `purchase-order` 采购订单菜单授权（fail closed），
  全程只读不写库，`POST` 由既有 `OperationLogMiddleware` 记录审计。

## 分组计数可视化（ERP-153）

采购用户在 ERP-149 字段设计器内可视化 ERP-152 的「当前授权预览页」采购订单张数分布，全程只读、只按当前页计数。

- 设计器新增「② 分组计数」选择器（`id="spe-des-groupby"`），只提供 ERP-152 允许的分组键：`none` / `supplier` /
  `currency` / `linkStatus` / `receiptStatus`（`SPE_GROUP_KEYS` 白名单，无自由输入）；非法 / 缺失分组键经
  `speDesGroupKey` fail closed 回落 `none`，绝不进入请求；
- 预览请求体由 `speDesBuildRequest` 始终携带 `groupBy`（= 选中的白名单键），复用既有有界、已授权预览
  （每次请求重新校验身份 / `purchase-order` 采购订单菜单授权 / 字段 / 筛选 / 页大小），全程只读不写库；
- 图表 `speDesGroupChartHtml` 渲染为安全条形图：供应商 / 币种为动态分组（只出现本页存在的取值，按供应商 Id / 币种升序），
  链接状态（linked / ambiguous / unavailable）与收货状态（none / partial / complete / over_received / unknown）为固定证据分类，
  空分类计数为 0 仍显示，「链接不唯一」「无可用链接」与「未知（超出派生上限）」保持可见；标签与计数全部转义、绝不注入 HTML，
  未知计数不回落 0；动态分组空页显示可见提示，翻页后图表随 `view.groups` 重渲染；
- 图表标注「仅当前预览页，非全量合计」：**计数只是当前预览页采购订单张数**，不是全量合计、绝不求和任何金额或数量、
  绝不跨币种合并或换算，也从结构上杜绝应付余额 / 付款授权推断；
- 权限 / 网络失败与预览一致（fail closed，界面可见拒绝原因，不暴露范围外数据）。

## 当前页金额汇总（ERP-154）

`POST /api/supplier-purchase-exposure/report` 的请求体新增 `summaryMode`（仅 `none` / `supplierCurrency` / `supplierCurrencyLink`；
无效取值在读取任何源数据之前即拒绝 `InvalidParameter`，fail closed，默认 `none`）。当请求汇总时，响应页新增：

- `summaryMode`：归一化后的金额汇总模式（默认 `none`）；
- `summaries`：`[{ supplierId, supplierName, currency, linkStatus, linkStatusText, orderCount, orderedAmount,
  linkedOrderCount, linkedOrderedAmount, ambiguousOrderCount, ambiguousOrderedAmount, unavailableOrderCount,
  unavailableOrderedAmount, settledAmount, outstandingAmount, submittedAmount, unknownSettlementOrderCount }]`，
  只给出「当前授权预览页」的**金额**汇总，绝不跨币种合并或换算。

汇总口径（算术证据边界）：

- 按 `supplier + currency` 分组（不同币种分别成组、绝不合并）；`supplierCurrencyLink` 再按链接状态拆分
  （linked / ambiguous / unavailable，仅出现本页存在的类别）；
- `orderedAmount` 来自采购订单已落库总额（恒可确认，直接求和），只统计当前页；
- `settledAmount` / `outstandingAmount` / `submittedAmount` 只汇总「链接可用且金额已知」的订单；没有链接可用订单
  或任一行金额未知（null）即对应合计为「未知」（null），绝不轧为 0、绝不给出部分合计；
- `ambiguousOrderedAmount` / `unavailableOrderedAmount` 保持独立，绝不并入权威已结算合计、绝不推断为应付余额；
- `unknownSettlementOrderCount` 记录本组链接可用订单中结算金额未知的订单张数，用于显式标注算术证据限制。

`none` 或空页时 `summaries` 为空；汇总只统计当前页、非全量合计，收货数量等非金额证据绝不进入汇总。
全程只读、无写入，`POST` 由既有 `OperationLogMiddleware` 记录审计。

## 文件地图

- `src/ERP.Application/DTOs/DynamicSupplierExposureReportDtos.cs`：目录 / 请求 / 结果 DTO（含 ERP-152 分组计数 DTO 与 ERP-154 金额汇总 DTO）；
- `src/ERP.Application/Services/DynamicSupplierExposureReportRules.cs`：字段白名单、校验、行投影、Excel 公式转义、分组计数（ERP-152）与金额汇总（ERP-154）（纯规则）；
- `src/ERP.Api/Controllers/DynamicSupplierExposureReportController.cs`：授权 + 复用 ERP-031 只读派生 + 选定列投影 + 分组计数（ERP-152）+ 金额汇总（ERP-154）+ xlsx 导出（ERP-150）+ PDF 导出（ERP-151）；
- `src/ERP.Infrastructure/Export/DynamicSupplierExposurePdfExporter.cs`：分页中文 PDF 导出（ERP-151，PDFsharp + 共享黑体解析器）；
- `src/ERP.UnitTests/DynamicSupplierExposureReportTests.cs`：预览单元测试（内存库，不连 SQL Server、不启动 API）；
- `src/ERP.UnitTests/DynamicSupplierExposureGroupingTests.cs`：分组计数单元测试（ERP-152，内存库，不连 SQL Server、不启动 API）；
- `src/ERP.UnitTests/DynamicSupplierExposureAmountSummaryTests.cs`：金额汇总单元测试（ERP-154，内存库，不连 SQL Server、不启动 API）；
- `src/ERP.UnitTests/DynamicSupplierExposureExcelTests.cs`：Excel 导出单元测试（ERP-150，内存库，不连 SQL Server、不启动 API）；
- `src/ERP.UnitTests/DynamicSupplierExposurePdfTests.cs`：PDF 导出单元测试（ERP-151，内存库，不连 SQL Server、不启动 API）；
- `src/ERP.Api/wwwroot/js/supplier-purchase-exposure.js`：ERP-031 工作台 + ERP-149 前端字段设计器 + ERP-150 Excel 导出（`speDesExportExcel`）+ ERP-151 PDF 导出（`speDesExportPdf`，纯函数可 Node 单测）+ ERP-153 分组计数可视化（`speDesGroupChartHtml` / `speDesGroupSelectHtml`）；
- `tests/automation/dynamic_supplier_exposure_ui.test.js`：ERP-149 前端 UI 逻辑单测（Node，无需浏览器）；
- `tests/automation/dynamic_supplier_exposure_grouping_ui.test.js`：ERP-153 前端分组计数 UI 逻辑单测（Node，无需浏览器）。
