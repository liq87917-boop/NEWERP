# 动态客户订单与收款核对报表（ERP-164）

只读、有界的客户订单与收款核对报表预览：销售用户按有限字段白名单与有界筛选，读取当前账号数据范围内的
ERP-046 客户订单与收款核对证据。本功能为**开发期只读派生**，复用 ERP-046 的权威口径，不新增任何表 / 列 /
权限模型，不执行任意 SQL / 迁移 / 生产 SQL / 真实数据库写入，也不做部署。

> 定位：这是**运营性的订单 / 收款证据核对视图**。它**不是**应收账款台账、**不是**客户对账单、
> **不是**收款授权或结算结果，也**不是**账龄表。

## 1. 接口

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/sales-orders/dynamic-receipt-reconciliation-report` | 返回 ERP-046 订单证据字段白名单目录 + 独立的未关联收款证据字段目录（需登录 + `sales-order` 销售订单菜单授权） |
| POST | `/api/sales-orders/dynamic-receipt-reconciliation-report` | 按选定字段与有界筛选预览当前账号数据范围内的订单与收款证据，稳定分页；未关联收款证据按独立的收款字段目录单独投影，绝不并入订单行 |
| POST | `/api/sales-orders/dynamic-receipt-reconciliation-report/export` | 导出当前页为 Excel（xlsx，只读，复用有界授权预览；订单证据与未关联收款证据写入两个独立工作表，仅导出当前页） |
| POST | `/api/sales-orders/dynamic-receipt-reconciliation-report/export-pdf` | 导出当前页为 PDF（只读，复用有界授权预览；订单证据与未关联收款证据渲染为两个独立分区，宽列自动分页，仅导出当前页） |

预览与下载请求由既有 `OperationLogMiddleware` 按 HTTP 方法记录审计，本控制器自身不写任何操作日志。

## 2. 授权（每次请求重新校验，fail closed）

1. **身份**：`ClaimTypes.NameIdentifier` 缺失或非正整数 → 拒绝（未认证，错误码 2001），绝不猜测身份。
2. **菜单授权**：复用既有「角色 → 菜单」模块授权，要求 `sales-order`（销售订单）菜单；
   无角色 / 无该菜单授权 → 权限不足（错误码 2002）。授权每次请求都重新查询，回收后立即收敛。
3. **业务员数据范围（ERP-097 唯一权威口径）**：`SalespersonDataScopeService.ResolveAsync` 每次请求重新解析；
   特权账号（超级管理员 / 系统内置角色 / 显式特权角色）不过滤；受限制业务员只能看到
   `BaseCustomer.EmpId == 本人` 的客户及其销售订单，未映射到业务员时为空集合（fail closed）。

## 3. 数据范围与源查询顺序

数据范围过滤在 ERP-046 源查询（`SalesOrderReceiptReconciliation.ForQueryAsync` 的 `ApplyFilters`）内部完成，
**先于计数与分页**：`total` 只统计范围内订单，分页只在本页范围内稳定排序
（客户 Id + 币种 + 订单日期 + 单据 Id），翻页不重不漏。

未关联收款证据只由**本页范围内客户**的收款单派生（`FinanceReceipt.CustomerId` 只到客户级），
绝不猜测收款单到订单的匹配关系，绝不把范围外客户的收款单带进来。

## 4. 字段白名单（有限、有序）

目录由 `DynamicReceiptReconciliationReportRules.GetCatalogDto()` 统一供给（界面 / 接口共用同一份口径）。
未知字段显式拒绝（错误码 2004）；空 / 留空 = 返回全部白名单字段（目录顺序）；去重并保持请求顺序。

白名单覆盖以下 ERP-046 订单证据字段（数据类别为 `number` / `text` / `date` / `boolean` / `enum`）：

- 订单身份：`orderId` / `orderNo` / `orderDate` / `status`
- 客户与币种：`customerId` / `customerName` / `currency` / `amountDecimals`
- 订单金额（原币）：`orderAmount` / `recordedDepositAmount`
- 出货数量证据：`orderedQuantity` / `shippedQuantity` / `pendingShipmentQuantity` /
  `outstandingQuantity` / `overShippedQuantity` / `shipmentStatus` / `hasApprovedShipment` /
  `shipmentDocumentCount` / `approvedShipmentCount`
- 收款申请链接证据（定金 / 货款申请单的 `SalesOrderId` 权威引用）：`receiptCoverageStatus` /
  `receiptCoverageText` / `receiptCoverageKnown` / `linkedReceiptAmount` / `pendingReceiptAmount` /
  `uncoveredAmount` / `otherCurrencyReceiptCount` / `unapprovedReceiptCount` /
  `unattributedReceiptCount` / `unattributedReceiptsTruncated` / `overReceived`
- 收款引用登记证据（ERP-054：ERP-053 持久化引用行）：`receiptAllocationStatus` /
  `receiptAllocationEvidenceLabel` / `receiptAllocationCount` / `recordedReceiptAllocationAmount` /
  `recordedReceiptCount` / `voidedReceiptAllocationCount` / `invalidReceiptAllocationCount` /
  `unavailableReceiptAllocationCount` / `receiptAllocationTruncated` / `unreferencedOrderAmount` /
  `receiptAllocationNote`
- 销项发票登记证据（ERP-056：ERP-055 持久化发票证据行 + 分摊行）：`invoiceEvidenceStatus` /
  `invoiceEvidenceLabel` / `invoiceAllocationCount` / `recordedInvoicedAmount` /
  `recordedInvoiceCount` / `recordedInvoiceGrossAmount` / `unreferencedInvoiceAmount` /
  `invoiceUnreferencedOrderAmount` / `draftInvoiceAllocationCount` / `voidedInvoiceAllocationCount` /
  `invalidInvoiceAllocationCount` / `unavailableInvoiceAllocationCount` /
  `invoiceEvidenceTruncated` / `invoiceEvidenceNote`
- 行级说明：`note`

### 4.1 未关联收款证据字段目录（独立、有限、只读）

未关联收款证据字段目录与订单证据字段目录**完全独立**，由 `DynamicReceiptReconciliationReportRules.GetReceiptCatalog()`
统一供给（界面 / 接口共用同一份口径）。请求体通过 `ReceiptFields` 选择收款证据列：空 / 留空 = 返回全部收款证据字段
（目录顺序）；去重并保持请求顺序；未知收款证据列显式拒绝（错误码 2004），**在读取 ERP-046 源数据之前**即失败（fail closed）。

未关联收款证据字段目录（数据类别为 `number` / `text` / `date` / `enum`）：

- 收款单身份：`receiptId` / `receiptNo` / `receiptDate`
- 客户与币种：`customerId` / `customerName` / `currency`（原币，绝不合并 / 换算）
- 金额：`amount`（收款单自身已落库金额，按原币原样列出；未知照实保留 `null`，绝不回落为 0）
- 付款与状态：`paymentMethod` / `status` / `evidenceStatus`（`active` / `pending` / `historical`）/ `evidenceText`
- 链接证据：`receiptLinkageStatus`（恒为 `unlinked`）/ `receiptLinkageText` / `referenceField`（=`FinanceReceipt.CustomerId`）
- 行级说明：`note`

预览响应中收款证据以 `ReceiptColumns` + `ReceiptRows` 单独投影，与订单 `Columns` / `Rows` 分开；
`UnlinkedReceiptTruncated` 保留源查询的「不完整」截断标记（命中上限时显式标注，绝不静默截断）。
收款证据绝不并入订单行、绝不与订单金额相加、绝不猜测收款单到订单的匹配关系。

## 5. 筛选校验（全部在读取源数据之前完成，非法取值 fail closed）

| 筛选 | 取值 | 校验 |
|---|---|---|
| `CustomerId` | 正整数 | 非正数显式拒绝（2004），且仅在当前数据范围内生效 |
| `Currency` | 币种枚举名（CNY / USD / EUR / HKD / GBP / JPY） | 非法取值显式拒绝（2004） |
| `OrderDateFrom` / `OrderDateTo` | 含首尾当天 | 开始晚于结束显式拒绝（2004） |
| `ShipmentStatus` | `none` / `shipped` | 非法取值显式拒绝（2004） |
| `ReceiptLinkStatus` | `linked` / `partial` / `unlinked` | 非法取值显式拒绝（2004）；`unknown` 只在命中派生上限时出现，不提供筛选 |
| `ReceiptStatus` | `active`（默认）/ `pending` / `historical` / `all` | 非法取值显式拒绝（2004） |
| `OrderStatus` | `active`（默认，排除已取消）/ `cancelled` / `all` | 非法取值显式拒绝（2004） |
| `Keyword` | 匹配订单号 / 外销合同号 / 客户 PO 号 | 长度 > 50 显式拒绝（2004） |
| `PageSize` | 1 ~ 200 | 超出上限显式拒绝（2004），不做静默截断 |

## 6. 证据边界（四类证据相互独立，绝不合并 / 相加 / 推断）

1. **收款申请链接证据**：只来自「定金 / 货款申请单的 `SalesOrderId`」这一权威引用，且只有
   「已审核 + 同币种」计入 `linkedReceiptAmount`；`uncoveredAmount = 订单金额 − 已关联金额`，
   只作「未覆盖金额」，**不是**应收余额或未收款金额。
2. **收款引用登记证据（ERP-054）**：ERP-053 持久化引用行，独立标注「收款单指向本订单」的有效引用金额 /
   引用行 / 收款单张数，并与已作废 / 无效 / 无法确认证据分桶；与「已关联收款金额」是两类独立证据。
3. **销项发票登记证据（ERP-056）**：ERP-055 持久化发票证据行 + 分摊行，独立标注已分摊金额与
   草稿 / 作废 / 无效 / 无法确认分桶；与上面三类证据相互独立。
4. **客户级未关联收款证据**：收款单（`FinanceReceipt`）只有 `CustomerId`、没有订单级引用，
   链接状态恒为 `unlinked`，金额只按收款单自身原币原样列出，绝不并入订单侧合计，绝不匹配到任何订单。

未知金额与未知数量一律 `null`（不是 0）；币种保持原币，不同币种分别成行、绝不合并或换算。

## 7. 只读保证

- 全程只读：无 Add / Update / Remove / SaveChanges，不执行任意 SQL。
- 不新增任何表 / 列 / 权限模型，不改写销售订单、出库单、收款单、收款申请、客户信用、库存、财务与税务记录。
- 不执行迁移 / 生产 SQL / 真实数据库操作 / 部署，不改写订单状态与已登记进度。

## 8. 相关文件

- `src/ERP.Application/DTOs/DynamicReceiptReconciliationReportDtos.cs`
- `src/ERP.Application/Services/DynamicReceiptReconciliationReportRules.cs`
- `src/ERP.Api/Controllers/DynamicReceiptReconciliationReportController.cs`
- `src/ERP.Api/Controllers/SalesOrderReceiptReconciliation.cs`（ERP-046 源查询，新增 `SalespersonDataScope? scope` 参数）
- `src/ERP.Infrastructure/Export/DynamicReceiptReconciliationPdfExporter.cs`
- `src/ERP.UnitTests/DynamicReceiptReconciliationReportTests.cs`
- `src/ERP.UnitTests/DynamicReceiptReconciliationUnlinkedTests.cs`
- `src/ERP.UnitTests/DynamicReceiptReconciliationGroupingTests.cs`
- `src/ERP.UnitTests/DynamicReceiptReconciliationExcelTests.cs`
- `src/ERP.UnitTests/DynamicReceiptReconciliationPdfTests.cs`
- `src/ERP.UnitTests/SalesOrderReceiptReconciliationTests.cs`

## 9. 前端设计器（ERP-166，只读、有界）

`src/ERP.Api/wwwroot/js/sales-order-receipt-reconciliation.js` 在既有「客户订单与收款核对报表」页工具栏提供「🔧 动态设计器」入口，
打开目录驱动的订单证据与未关联收款证据字段 / 筛选设计器（开发期只读派生，不写库、不迁移、不执行任意 SQL）。

### 9.1 工作流

1. 销售订单页 →「客户订单与收款核对报表」→ 工具栏「🔧 动态设计器」。
2. 设计器先 `GET /api/sales-orders/dynamic-receipt-reconciliation-report` 拉取字段目录（订单证据 `fields` 与
   未关联收款证据 `receiptFields` 两个**独立**白名单），并尽力拉取 `/api/base/customers` 客户下拉；
   目录加载失败（未登录 / 无 `sales-order` 菜单 / 网络失败）时整页 fail closed，不渲染任何字段选择器、不暴露任何数据。
3. 用户分别勾选订单证据字段与未关联收款证据字段（复选框，仅由目录白名单渲染，无自由字段名 / SQL），
   并填写有界筛选（客户 / 币种 / 订单日期 / 出货状态 / 收款链接状态 / 收款证据状态 / 订单状态 / 关键字 / 每页）。
4. 「预览」以 `POST /api/sales-orders/dynamic-receipt-reconciliation-report` 发送「选定字段 + 有界筛选 + 有界分页」，
   按请求顺序渲染订单证据列与单元格；未关联收款证据以**独立分区**渲染（原币、状态、截断警告），绝不并入订单行。
5. 「📤 导出订单证据 CSV」与「📤 导出未关联收款 CSV」分别导出当前页的两类证据：null 未知保留为空（绝不回落为 0），
   文本类首字符为 `=` / `+` / `-` / `@` / 制表符 / 回车时前缀单引号防公式注入，含逗号 / 引号 / 换行按 RFC4180 加引号。
6. 加载 / 空结果 / 权限不足 / 网络失败均在结果区可见；翻页有界（最小第 1 页，单页上限 200 由目录 `maxPageSize` 供给并钳制）。

### 9.2 与既有核对报表的关系

设计器与既有静态核对报表共用同一权威口径（ERP-046 / ERP-164 / ERP-165），二者均只读、均把收款申请链接证据、
收款引用登记证据、销项发票登记证据与客户级未关联收款证据作为**相互独立证据**呈现，绝不合并 / 相加 / 推断；
未关联收款单只记录客户（`FinanceReceipt.CustomerId`）、没有订单级引用，链接状态恒为 `unlinked`。

### 9.3 测试

- 前端纯逻辑单测：`tests/automation/dynamic_receipt_reconciliation_ui.test.js`（Node，覆盖字段选择、请求边界、
  分页渲染、单元格渲染、CSV 导出与失败态；运行 `node tests/automation/dynamic_receipt_reconciliation_ui.test.js`）。
- 前端 PDF 下载接线单测：`tests/automation/dynamic_receipt_reconciliation_pdf_ui.test.js`（Node，覆盖 PDF 按钮、
  export-pdf 接口路径、application/pdf 下载、空 / 授权 / 网络失败可见、两类证据分开导出与无任意 SQL）。
- 后端契约单测：`DynamicReceiptReconciliationReportTests.cs` / `DynamicReceiptReconciliationUnlinkedTests.cs`。

## 10. Excel 导出（ERP-167，只读、有界）

`POST /api/sales-orders/dynamic-receipt-reconciliation-report/export`：请求体与预览完全相同
（`DynamicReceiptReconciliationReportRequest`），复用同一有界、已授权预览管线（每次请求重新校验身份 / `sales-order` 销售订单菜单授权 /
字段 / 筛选 / 页大小 / 业务员数据范围），**只导出请求 `page` / `pageSize` 对应的当前页**（单页上限 200，超限直接拒绝），不是全量导出。

- 生成**两个独立工作表**：`订单证据`（选定订单列）与 `未关联收款证据`（选定收款列），列顺序与请求一致；两个工作表绝不合并、绝不推导收款单到订单的匹配关系。
- 金额保留原币、不同币种分别成行、绝不换算或跨币种合计；未知金额 / 数量为 null → 导出为空文本（**绝不回落 0**）；收款证据状态（active / pending / historical）与收款链接状态（unlinked）显式保留；命中未关联收款读取上限时，收款证据工作表尾行显式写入截断警告。
- 文本单元格以 `=` / `+` / `-` / `@` / 制表符 / 回车开头时前缀单引号转义（`DynamicReceiptReconciliationReportRules.EscapeFormulaLeading`），保持字面文本、不被当作公式执行。
- 文件名 `ReceiptReconciliation_yyyyMMddHHmmss.xlsx`；内容类型 `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`。
- 只读 + 审计：无写入、无任意 SQL；`POST` 由既有 `OperationLogMiddleware` 记录审计（动作「导出」）。

### 10.1 前端

设计器工具栏「📥 导出 Excel（选定列）」复用当前字段 / 筛选 / 分页组装请求后 `POST` 导出；空数据 / 未预览 / 授权 / 校验 / 网络失败均在结果区可见，不下载空表。

### 10.2 测试

- 后端契约单测：`DynamicReceiptReconciliationExcelTests.cs`（覆盖作用域、列顺序、页大小上限、两个独立证据工作表、未知金额 null、原币、公式注入防护与只读不写库）。

## 11. PDF 导出（ERP-169，只读、有界）

`POST /api/sales-orders/dynamic-receipt-reconciliation-report/export-pdf`：请求体与预览完全相同
（`DynamicReceiptReconciliationReportRequest`），复用同一有界、已授权预览管线（每次请求重新校验身份 / `sales-order` 销售订单菜单授权 /
字段 / 筛选 / 页大小 / 业务员数据范围），**只导出请求 `page` / `pageSize` 对应的当前页**（单页上限 200，超限直接拒绝），不是全量导出。

- 生成**两个独立分区**：`一、订单证据`（选定订单列）与 `二、未关联收款证据`（选定收款列），列顺序与请求一致；两个分区绝不合并、绝不推导收款单到订单的匹配关系。
- 金额保留原币、不同币种分别成行、绝不换算或跨币种合计；未知金额 / 数量为 null → 渲染为空文本（**绝不回落 0**）；收款证据状态（active / pending / historical）与收款链接状态（unlinked）显式保留；命中未关联收款读取上限时，收款证据分区标题下显式渲染截断警告。
- **宽列集自动分页**：选定列超过单页可用宽度（按每列有界宽度 20~45mm）时按「列页」拆分，每个列页重复表头，避免挤压 / 裁切；行超出页高时纵向分页，表头重复。
- **字体前提**：中文字体固定使用 Windows 黑体 `SimHei`（`SimHeiPdfFontResolver` 与其它报表 PDF 共享解析器）；字体缺失时显式失败（错误码 5000，提示安装 `simhei.ttf`），不产出乱码 / 缺字 PDF。
- 文件名 `ReceiptReconciliation_yyyyMMddHHmmss.pdf`；内容类型 `application/pdf`。
- 只读 + 审计：无写入、无任意 SQL；`POST` 由既有 `OperationLogMiddleware` 记录审计（动作「导出」）。

### 11.1 前端

设计器工具栏「📄 导出 PDF（当前页）」复用当前字段 / 筛选 / 分页组装请求后 `POST` 导出；空数据 / 未预览 / 授权 / 校验 / 网络失败均在结果区可见，不下载空 PDF。

### 11.2 测试

- 后端契约单测：`DynamicReceiptReconciliationPdfTests.cs`（覆盖 PDF 签名与内容类型、两类证据独立分区、行分页、宽列列页分页、嵌入 SimHei、字体缺失显式失败、未知 null / 原币 / 状态格式化、授权拒绝与只读不写库）。
- 前端接线单测：`tests/automation/dynamic_receipt_reconciliation_pdf_ui.test.js`。

## 12. 当前页计数分组（ERP-170，只读、有界）

预览请求体新增有限分组键 `GroupBy`，让销售用户按分组键查看**当前预览页**的订单证据与未关联收款证据分布。分组**只统计当前页**（同一批有界、已授权行），**绝不统计全量**；只输出计数，**绝不求和金额、绝不跨币种合并、绝不推断收款单与订单的匹配关系**。

### 12.1 分组键白名单（有限、fail closed）

`DynamicReceiptReconciliationReportRules.NormalizeGroupBy` 仅接受（大小写不敏感）：

| 键 | 说明 |
|---|---|
| `none`（默认） | 不分组；`orderGroups` / `receiptGroups` 为空列表 |
| `customer` | 按客户计数（订单证据与未关联收款证据各自按客户分组） |
| `currency` | 按币种计数（不同币种分别成行，绝不合并） |
| `receiptCoverageStatus` | 订单侧按收款覆盖状态计数：`linked` / `partial` / `unlinked` / `unknown` 显式保留；未关联收款侧不适用（返回空） |
| `receiptEvidenceStatus` | 未关联收款侧按收款证据状态计数：`active` / `pending` / `historical` 显式保留；订单侧不适用（返回空） |

未知分组键在**读取 ERP-046 源数据之前**显式拒绝（错误码 2004，fail closed）；业务员数据范围仍在 ERP-046 源查询内**先于计数与分页**生效。

### 12.2 响应字段

`DynamicReceiptReconciliationReportPageDto` 新增三个字段（保持原有预览 / 导出字段不变）：

- `GroupBy`（`string`）：归一化后的实际分组键（默认 `none`）。
- `OrderGroups`（`List<DynamicReceiptReconciliationReportOrderGroupDto>?`）：当前页订单计数分组，每项含 `Key` / `Label` / `OrderCount`。
- `ReceiptGroups`（`List<DynamicReceiptReconciliationReportReceiptGroupDto>?`）：当前页未关联收款计数分组，每项含 `Key` / `Label` / `ReceiptCount` / `Truncated`。

分组键与组序为确定性（客户按 Id 升序、币种按 `Currency` 枚举顺序、覆盖状态按 linked → partial → unlinked → unknown、证据状态按 active → pending → historical）。未关联收款命中单次查询上限时，`ReceiptGroups` 各项 `Truncated = true`（显式保留截断语义，绝不静默截断）。

### 12.3 口径边界（与预览同源）

- **只统计当前页**：分组来自与预览同一批有界、已授权源行，`total` 仍为符合筛选条件的全量订单数，但分组计数只覆盖本页（单页上限 200，超限直接拒绝）。
- **不求和金额**：分组 DTO 只有计数，不含金额 / 合计 / 余额，也绝不把订单金额与未关联收款金额相加。
- **不合并币种**：`currency` 分组下不同币种分别成行，绝不换算或合并。
- **不推断匹配**：未关联收款只按收款单自身客户 / 币种 / 证据状态计数，绝不按客户名、单号文本、日期或金额相似度匹配到订单。
- **状态显式保留**：收款覆盖状态 `unknown`、收款证据状态 `pending` / `historical` 作为独立分组显式列出，不回落到其它桶、不并入有效合计。

### 12.4 测试

- 后端契约单测：`DynamicReceiptReconciliationGroupingTests.cs`（覆盖分组键白名单与非法拒绝、无菜单授权拒绝、受限制业务员范围、空页、active / pending / historical 与 linked / partial / unlinked / unknown 状态隔离、币种不合并、当前页上限与只读不写库）。
- 前端计数面板接线（ERP-171）：`tests/automation/dynamic_receipt_reconciliation_grouping_ui.test.js`。

## 13. 前端当前页计数面板（ERP-171，只读、有界）

设计器工具栏新增**当前页分组**选择器（`id="drr-groupby"`），仅提供 ERP-170 服务端白名单键
（`none` / `customer` / `currency` / `receiptCoverageStatus` / `receiptEvidenceStatus`，`drrGroupKey` 规范化，
缺失 / 空白 / 非法值一律回落 `none`，fail closed，绝不发送范围外键）。预览与导出复用既有已授权有界预览请求，
并在请求体携带选中的分组键 `GroupBy`（`drrBuildRequest` 组装，`drrBuildState` 从选择器读取）。

预览成功后，结果区在既有两个独立证据分区（订单证据 / 未关联收款证据）**之上**渲染两个独立计数面板
（`drrOrderGroupPanelHtml` / `drrReceiptGroupPanelHtml`），明确标注**仅当前预览页，非全量合计**：

- 订单计数面板只统计本页订单张数（`orderGroups` 的 `orderCount`），绝不求和金额 / 数量、绝不跨币种合并或换算。
- 未关联收款计数面板只统计本页未关联收款张数（`receiptGroups` 的 `receiptCount`），
  active / pending / historical 证据状态显式保留；命中读取上限时各项 `truncated` 显式渲染「（截断）」标记。
- `receiptCoverageStatus` 分组下收款覆盖状态 `unknown` 作为独立分类显式保留（不回落到其它桶）；
  收款证据状态分组只作用于未关联收款侧、收款覆盖状态分组只作用于订单侧，不适用侧显示可见提示（不静默清空）。
- 分组标签全部经 `drrEsc` HTML 转义；计数未知（null）绝不回落为 0，显示「未知」；空页显示可见提示。
- 权限不足 / 未登录 / 网络失败 / 无效请求复用既有 `drrErrorHtml` 失败态（fail closed，不渲染任何分组数据）。

### 13.1 前端测试

- `tests/automation/dynamic_receipt_reconciliation_grouping_ui.test.js`：覆盖分组键规范化与选择器白名单、
  scope-safe 请求携带分组键、两个独立计数面板渲染（仅当前预览页、unknown / 截断 / 转义 / 空态）、错误态与无任意 SQL。
