# 动态供应商对账与账龄报表（ERP-140）

只读、有界、已授权的「动态供应商对账与账龄报表」预览：采购用户从**有限的发票证据字段白名单**中按序选择字段，并复用
ERP-068 供应商对账与账龄工作台的既有筛选与稳定分页，预览当前账号可见的供应商发票证据页。

## 接口

| 方法 | 路由 | 说明 |
| --- | --- | --- |
| `GET` | `/api/supplier-reconciliation-aging/report` | 返回发票证据字段白名单目录（需登录 + 采购订单菜单授权） |
| `POST` | `/api/supplier-reconciliation-aging/report` | 按选定字段与有界筛选预览当前页，稳定分页（单页上限 200） |
| `POST` | `/api/supplier-reconciliation-aging/report/export` | 导出当前选定页为 Excel（xlsx，只读，复用有界授权预览与选定列顺序） |

请求体（`DynamicSupplierAgingReportRequest`）：`fields`（选定字段键，仅限白名单）、`supplierId`、`currency`、
`invoiceStatus`（recorded 默认 / draft / voided / all）、`allocationState`（none / historical_only / partial / full）、
`keyword`、`invoiceDateFrom` / `invoiceDateTo`、`dueDateFrom` / `dueDateTo`、`asOfDate`、`page`、`pageSize`。

## 授权（fail closed）

预览与目录都要求当前登录账号具备既有「采购订单」菜单授权（`purchase-order`，与 ERP-061 ~ ERP-068 工作流同源）。
无身份 → `Unauthorized`；无该菜单授权 → `Forbidden`。每次请求都重新查询「角色 → 菜单」授权，不依赖缓存。

## 字段白名单（有限、有序）

字段全部来自 ERP-068 发票证据行（`SupplierReconciliationAgingInvoice`），由
`DynamicSupplierAgingReportRules` 统一供给。未知字段一律拒绝（`InvalidParameter`），留空 = 返回全部白名单字段；
选定列严格按请求顺序返回。

覆盖的证据类别：

- 发票身份：`invoiceId` / `invoiceType` / `invoiceCode` / `invoiceNumber` / `invoiceIdentityText` / `invoiceDate`；
- 供应商：`supplierId` / `supplierCode` / `supplierName` / `supplierAvailable` / `supplierAvailabilityText`；
- 币种与金额（原币）：`currency` / `amountDecimals` / `netAmount` / `taxAmount` / `grossAmount`；
- 发票状态（草稿 / 已作废历史）：`invoiceStatus` / `invoiceStatusText` / `isActiveEvidence` / `isDraft` / `isVoided`；
- 显式到期日与账龄：`dueDate` / `dueDateKnown` / `dueDateText` / `paymentTerms` / `paymentTermsText` /
  `agingBucket` / `agingBucketText` / `overdueDays` / `agingText`；
- 分配证据：`allocationState` / `allocationStateText` / `activeAllocatedAmount` / `activeAllocationCount` /
  `activePaymentCount` / `remainingAmount` / `remainingState` / `remainingStateText` / `voidedAllocationCount` /
  `voidedAllocationAmount` / `invoiceInactiveAllocationCount` / `invoiceInactiveAllocationAmount` /
  `invalidAllocationCount` / `invalidAllocationAmount` / `unavailableAllocationCount` / `unavailableAllocationAmount` /
  `hasAllocationHistory` / `hasInvalidOrUnavailableEvidence` / `historicalEvidenceText` / `note`。

## 筛选与分页（源读取前校验）

- 供应商 Id：非正数直接拒绝；币种：非法取值直接拒绝（复用发票币种严格口径）；发票状态 / 分配状态：非法取值直接拒绝；
- 开票日期 / 到期日区间：开始晚于结束直接拒绝；到期日区间只命中登记了显式到期日的发票（未知到期日不可筛、绝不推算）；
- as-of 日期：早于 1900 直接拒绝；关键字长度超过 50 直接拒绝；
- 每页条数必须在 `1 ~ 200`，超出直接拒绝（不静默截断）。

以上校验全部在调用 `SupplierReconciliationAging.ForQueryAsync`（即源读取）**之前**完成；随后复用 ERP-068 的同一套
筛选 → 稳定分页 → 批量派生。

## 证据边界（重要）

- 金额一律按原币分别成行：`currency` 为原币，不同币种绝不合并、不做汇率换算；
- 未知到期日（`dueDateKnown=false`、`agingBucket=null`）**不计算账龄、单独成组**，绝不按开票日期或付款条件推算；
- 草稿 / 已作废发票单独标注，金额永不并入有效合计；
- 无效 / 无法确认分配证据保持可见，绝不修复、改派或合并；命中系统有界上限时分配证据按「未知」显示，绝不给部分合计。

本预览**不是**应付账款余额、**不是**法定供应商对账单、**不是**付款授权、**不是**税务申报，也不构成结算确认。

## 只读与审计

- 全程只读：无 Add / Update / Remove / SaveChanges，不执行任意 SQL、不写库；
- 请求由既有 `OperationLogMiddleware` 按 HTTP 方法记录审计。

## 前端字段设计器（ERP-141）：视觉字段选择 + 所选列预览与 CSV 导出

前端工作台 `wwwroot/js/supplier-reconciliation-aging.js` 在既有「供应商对账与账龄工作台」工具栏新增「🎛 字段设计器」入口，
保持静态视图，只读、有界、可单测：

- 打开设计器时先 `GET /api/supplier-reconciliation-aging/report` 拉取有限字段白名单目录，目录加载失败 / 未登录 / 无采购订单菜单授权一律 fail closed（整页可见拒绝原因，不渲染任何字段）；
- 字段选择器只由目录白名单渲染为复选框（`name="sra-des-field"`），**没有自由填写的字段名 / SQL**；勾选状态经 `sraDesSelectFields` 规范化（去重、保持顺序、丢弃未知键）；
- 预览复用工作台**当前筛选**（供应商 / 币种 / 发票状态 / 分配状态 / 开票日期 / 到期日 / as-of / 关键字 / 每页），并 `POST /api/supplier-reconciliation-aging/report`，只发送「白名单字段 + 当前筛选 + 有界分页（pageSize 1~200）」；请求体由 `sraDesBuildRequest` 组装，页码最小 1、每页钳制到目录 `maxPageSize`（≤200）；
- 渲染只按返回的列名与选定字段值（`sraDesTableHtml` / `sraDesResultHtml`），全部 HTML 转义；未知到期日 / 未知剩余 / 未知计数一律显示「未知」（`sraDesCellText`），绝不回落为 0；不同币种分别成行、绝不合并、无跨币种总额；
- 加载（`sraDesLoadingHtml`）、空结果（`sraDesEmptyHtml`）、权限不足 / 未登录 / 无效请求 / 网络失败（`sraDesErrorHtml`）均为可见状态；
- 导出（`sraDesExport`）仅导出**当前预览页**的所选列 CSV（`sraDesCsv`）：未知值保留「未知」、以 `= + - @` 或制表符 / 回车开头的文本加 `'` 前缀（防公式注入）、引号转义、CRLF + BOM；空结果不下载仅表头的 CSV。
- 导出 Excel（`sraDesExportXlsx`）复用当前预览页与所选列，`POST /api/supplier-reconciliation-aging/report/export` 下载 xlsx（只读、有界、重新校验授权 / 字段 / 筛选 / 页大小）；未知到期日 / 未知剩余 / 未知计数保留为「未知」或空单元格、绝不回落为 0，公式首字符转义、无跨币种总额；空页 / 权限 / 无效 / 网络失败均在结果区可见，不下载仅表头文件。

### 工作流

1. 采购用户进入「供应商对账与账龄工作台」，设置供应商 / 币种 / 发票状态 / 分配状态 / 日期 / as-of / 关键字等当前筛选；
2. 点击工具栏「🎛 字段设计器」，目录加载成功后按需勾选 / 清空证据字段（默认全选）；
3. 点击「预览」→ 复用当前筛选 + 选定字段调用 ERP-140 只读预览，按请求顺序渲染当前授权页；
4. 用「← 上一页 / 下一页 →」翻页（有界），点击「📤 导出所选列 CSV」下载当前预览页 CSV，或点击「📥 导出 Excel（xlsx）」下载当前预览页 xlsx。

### 前端单测

`tests/automation/dynamic_supplier_aging_report_ui.test.js`（Node，无需浏览器）覆盖：字段选择（去重 / 顺序 / 丢弃未知键）、
请求边界（仅白名单字段、页码 / 每页有界、筛选仅复用当前筛选、无任意字段名）、未知证据渲染（到期日 / 剩余 / 逾期天数）、
表格渲染（未知 + 币种分行 + 转义）、空结果与失败态（权限 / 未登录 / 网络 / 无效请求）、所选列 CSV（BOM / 未知保留 / 公式转义 / 引号转义），
以及前端接线契约（入口、接口路径、复选框、无任意 SQL、无跨币种总额）。

运行：`node tests/automation/dynamic_supplier_aging_report_ui.test.js`

## 当前页 Excel 导出（ERP-142）

`POST /api/supplier-reconciliation-aging/report/export`，请求体与预览完全相同（`DynamicSupplierAgingReportRequest`），复用同一有界、已授权预览管线（每次请求重新校验身份 / 采购订单菜单授权 / 字段 / 供应商 / 币种 / 发票状态 / 分配状态 / 日期 / as-of / 关键字 / 页大小），**只导出请求 `page` / `pageSize` 对应的当前页**（单页上限 200，超限直接拒绝），不是全量导出。

### 导出口径与限制

- **仅当前页 + 选定列**：数据工作表列头与数据行都按 `page.Columns`（= 请求选定字段顺序）排列，与预览同源；只导出当前页，不导出全量。
- **原币分行**：金额一律按原币分别成行，`currency` 为原币，不同币种绝不合并、不做汇率换算、无跨币种总额。
- **显式未知**：未知到期日（`dueDate` / `overdueDays` / `agingBucket` 为 null）、未知剩余证据（`remainingAmount` 为 null）与未知 / 无效 / 无法确认分配证据照实保留为**空单元格（未知）**，绝不回落为 0、不推算、不修复；草稿 / 已作废发票单独标注，金额永不并入有效合计。
- **公式注入防护**：文本单元格以 `=` / `+` / `-` / `@` / 制表符 / 回车 / 换行开头时前缀单引号转义（OWASP），保持字面文本、不被当作公式执行（`DynamicSupplierAgingReportRules.EscapeFormulaLeading`）。
- **只读 + 审计**：无写入、无任意 SQL；`POST` 由既有 `OperationLogMiddleware` 记录审计（动作「导出」）。
- 文件名 `SupplierAging_yyyyMMddHHmmss.xlsx`；内容类型 `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`。

### 前端

字段设计器工具栏新增「📥 导出 Excel（xlsx）」：复用当前字段 / 工作台筛选 / 分页组装有界请求后 `POST` 导出；空页显示可见错误、不下载仅表头文件，授权 / 校验 / 网络失败均在结果区可见。

### 单元测试

`src/ERP.UnitTests/DynamicSupplierAgingExcelTests.cs`（内存库，不连 SQL Server、不启动 API）覆盖：选定列顺序与行值、仅导出当前页（页大小上限）、未知到期日 / 未知分配证据保留为空单元格、不同币种分别成行、公式注入转义、权限 / 身份 fail closed、页大小超限拒绝、以及只读不写库。

## 文件地图

- `src/ERP.Application/DTOs/DynamicSupplierAgingReportDtos.cs`：目录 / 请求 / 结果 DTO；
- `src/ERP.Application/Services/DynamicSupplierAgingReportRules.cs`：字段白名单、校验与行投影（纯规则）；
- `src/ERP.Api/Controllers/DynamicSupplierAgingReportController.cs`：授权 + 复用 ERP-068 只读派生 + 选定列投影；
- `src/ERP.UnitTests/DynamicSupplierAgingReportTests.cs`：单元测试（内存库，不连 SQL Server、不启动 API）；
- `src/ERP.UnitTests/DynamicSupplierAgingExcelTests.cs`：ERP-142 Excel 导出单元测试（内存库，不连 SQL Server、不启动 API）；
- `src/ERP.Api/wwwroot/js/supplier-reconciliation-aging.js`：ERP-068 工作台 + ERP-141 前端字段设计器（`openSupplierAgingDesigner`，纯函数可 Node 单测）；
- `tests/automation/dynamic_supplier_aging_report_ui.test.js`：ERP-141 前端 UI 逻辑单测（Node，无需浏览器）。
