# 客户报告包预览（ERP-122）

## 1. 目标

授权业务员从客户销项发票登记册打开报告包，只读预览同一客户的 ①销售订单、②发票 + 显式收款分摊证据，
两者作为两个**独立有界分区**返回，各自独立计数、金额按原币呈现、剩余证据保留 known / unknown / over_allocated 标签。
两个分区分别重检各自的既有菜单授权与 `SalespersonDataScopeService`（ERP-097）数据范围，任一失败即拒绝整个响应
（fail closed，绝不返回部分结果）；绝不推断发票到销售订单的链接、不结算、不计算账户余额或催收状态。

## 2. 接口

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/api/customer-report-packet` | 按正整数客户 Id + 有界日期 / 分页筛选预览两个独立分区 |
| POST | `/api/customer-report-packet/export` | 把同一客户的两个分区导出为含两个独立工作表的 Excel（xlsx，只读，仅导出当前页） |
| POST | `/api/customer-report-packet/export/pdf` | 把同一客户的两个分区导出为含两个独立分页章节的 PDF（只读，仅导出当前页） |

请求体（`CustomerReportPacketRequest`）：

- `customerId`：客户 Id（**必填、正整数**，非正整数拒绝）；
- `startDate` / `endDate`：日期区间（含当日；同时作用于销售订单订单日期与发票开票日期）；
- `page`（默认 1，小于 1 归一到第 1 页）/ `pageSize`（默认 20，**上限 100**，超出拒绝）。

响应（`CustomerReportPacketDto`）：

- `customerId`：客户 Id；
- `salesOrders`：销售订单分区（`DynamicSalesOrderReportPageDto`，独立 `columns` / `rows` / `total` / `page` / `pageSize` / `totalPages`）；
- `receivableEvidence`：发票 / 收款分摊证据分区（`DynamicReceivableReportPageDto`，同上）；
- `readOnlyText` / `boundaryText` / `disclaimerText`：只读 / 边界 / 免责口径文案。

### 2.1 Excel 导出（ERP-123）

下载同一客户的两个分区为 xlsx：`POST /api/customer-report-packet/export`，请求体与预览同构（`CustomerReportPacketRequest`），
返回 `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`，文件名 `CustomerReportPacket_yyyyMMddHHmmss.xlsx`。

导出口径与导出限制：

- **复用有界双授权预览**：导出时重新校验身份、双菜单授权、正整数客户 Id、日期区间、页大小（1 ~ 100）与业务员数据范围，
  任一失败即拒绝（不发字节）；两个分区仍各自独立重检本分区菜单授权与数据范围。
- **两个独立工作表**：`销售订单`（销售订单白名单字段，含 `orderNo` / `currency` / `totalAmount` 等原币与标识）与
  `应收证据`（ERP-074 证据字段，含 `invoiceNumber` / `currency` / `grossAmount` / `remainingState` / `remainingStateText` 等）。
- **原币与显式剩余状态**：金额按原币呈现，`remainingState` 保留 `known` / `unknown` / `over_allocated`，
  `unknown` / `over_allocated` 金额为空文本，绝不轧成假余额。
- **绝不跨单拼接 / 混合币种合计**：不推断发票到订单的链接，不生成任何合计 / 余额 / 催收结论行。
- **公式注入安全**：以 `=` `+` `-` `@` 或制表符 / 回车 / 换行开头的文本单元格前缀单引号，保持字面文本。
- **仅导出当前页**：行数受当前 `page` / `pageSize`（上限 100）约束，超出部分不导出。
- **审计**：导出为 `POST`，由既有 `OperationLogMiddleware` 记录审计；全程只读，不写库、不执行任意 SQL。

### 2.2 PDF 导出（ERP-124）

下载同一客户的两个分区为分页中文 PDF：`POST /api/customer-report-packet/export/pdf`，请求体与预览同构（`CustomerReportPacketRequest`），
返回 `application/pdf`，文件名 `CustomerReportPacket_yyyyMMddHHmmss.pdf`。

导出口径与导出限制：

- **复用有界双授权预览**：导出时重新校验身份、双菜单授权、正整数客户 Id、日期区间、页大小（1 ~ 100）与业务员数据范围，
  任一失败即拒绝（不发字节）；两个分区仍各自独立重检本分区菜单授权与数据范围。
- **两个独立分页章节**：`一、销售订单` 与 `二、发票 / 收款分摊证据` 各自独立分页（应收证据章节另起一页），续页重复章节标题与列标题；
  行数受当前 `page` / `pageSize`（上限 100）约束，仅导出当前页。
- **原币与显式剩余状态**：金额按原币文本呈现；`remainingState` 显式保留 `known`（剩余可确认）/ `unknown`（剩余未知）/
  `over_allocated`（剩余超额分摊（无效））标签，绝不轧成假余额。
- **绝不跨单拼接 / 混合币种合计**：不推断发票到订单的链接，不生成任何合计 / 余额 / 催收结论行。
- **字体前提（Windows）**：中文字体固定使用 Windows 黑体（SimHei），与销售订单 PDF（ERP-116）/ 应收账款 PDF（ERP-121）
  共用同一共享解析器（`SimHeiPdfFontResolver`）；字体缺失时显式失败（`500`），**绝不产出乱码或缺字的 PDF**，也不替换为其它字体。
- **审计**：导出为 `POST`，由既有 `OperationLogMiddleware` 记录审计；全程只读，不写库、不执行任意 SQL。

## 3. 分区口径

- **销售订单分区**：复用 ERP-112 `IDynamicSalesOrderReportQuery`，字段为销售订单持久化字段白名单，
  携带 `currency` / `totalAmount` 等原币字段。
- **发票 / 收款分摊证据分区**：复用 ERP-117 `IDynamicReceivableReportQuery`，字段来自 ERP-074 对账证据行，
  携带 `currency` / `grossAmount` / `effectiveAmount` / `remainingAmount` / `remainingState` / `remainingStateText`
  与 `allocationState` / `allocationStateText` 等字段。
- **两个分区绝不合并、绝不推导**：响应不含任何「发票 → 订单」的链接、结算、余额或催收字段；
  剩余证据状态仅保留 `known` / `unknown` / `over_allocated`，`unknown` / `over_allocated` 金额为 null，绝不轧成假余额。

## 4. 授权与数据边界

1. **身份**：缺少登录用户 Id → 未认证（`2000`）。
2. **双菜单授权**：端点级要求同时具备「销售订单」菜单（`sales-order`）与「客户资料」菜单（`customer`），
   任一缺失 → 权限不足（`2002`），拒绝整个响应；授权每次请求都重新查询。
3. **分区独立重检**：两个分区各自的查询接口还会独立重检本分区的菜单授权与业务员数据范围，
   即使端点级双授权通过，任一分区内部失败同样拒绝整个响应。
4. **数据范围**：复用 `SalespersonDataScopeService`（ERP-097 唯一权威口径）；范围过滤在 `Count` / `Skip` / `Take`
   之前应用，越界客户绝不进入计数与结果页，两分区均为空（fail closed）。
5. **有界校验**：客户 Id 非正、开始日期晚于结束日期、`pageSize` 超出 `1~100` 均在读取前拒绝。
6. **只读**：全程 `AsNoTracking`，无 `Add` / `Update` / `Remove` / `SaveChanges`，不执行任意 SQL，不做任何写入。

## 5. 请求审计

预览为 `POST`，由既有 `OperationLogMiddleware` 按 HTTP 方法记录到 `SysOperationLog`，查询自身不写任何操作日志。

## 6. 只读保证

- 不新增表 / 列 / 索引，不执行迁移、生产 SQL 或真实数据库操作；
- 不改写客户、发票证据、收款分摊、收款单、销售订单、库存、财务或结算记录；
- 不读取或打印任何连接串、JWT / OSS 密钥或 `.env` 值。

## 7. 关键文件

- `src/ERP.Application/DTOs/CustomerReportPacketDtos.cs`：请求 / 结果 DTO；
- `src/ERP.Application/Services/CustomerReportPacketRules.cs`：双菜单授权、有界校验与纯映射（纯规则）；
- `src/ERP.Api/Controllers/CustomerReportPacketController.cs`：HTTP 控制器（预览 / Excel / PDF 三个端点）；
- `src/ERP.Infrastructure/Export/CustomerReportPacketPdfExporter.cs`：PDF 导出（ERP-124，复用共享 SimHei 解析器，独立分页章节）；
- `src/ERP.Api/wwwroot/js/customer-report-packet.js`：前端预览 + Excel / PDF 下载（入口 `openCustomerReportPacket`）；
- `src/ERP.Api/wwwroot/js/customer-sales-invoices.js`：登记册工具栏 / 详情工具栏入口；
- `src/ERP.UnitTests/CustomerReportPacketTests.cs`：预览单元测试；
- `src/ERP.UnitTests/CustomerReportPacketExcelTests.cs`：Excel 导出单元测试（ERP-123）；
- `src/ERP.UnitTests/CustomerReportPacketPdfTests.cs`：PDF 导出单元测试（ERP-124）；
- `tests/automation/customer_report_packet_ui.test.js`：前端 UI 逻辑单测。

## 8. 测试覆盖

- **双权限**：销售订单 / 客户资料菜单任一缺失均拒绝整个响应；无身份未认证拒绝。
- **有界校验**：正整数客户 Id、日期区间倒置拒绝、`pageSize` 超限拒绝、页码小于 1 归一。
- **数据范围**：受限业务员越界客户时两分区都空、不泄露。
- **独立分区**：两分区各自独立计数、原币字段、剩余证据状态字段；分区字段互不串用（不推断跨单链接）。
- **只读**：`SaveChangesAsync` 调用次数恒为 0。
- **Excel 导出（ERP-123）**：双权限 / 越界客户拒绝、两个工作表 / 原币 / 显式剩余证据状态、公式前导文本转义、
  行数上限与仅导出当前页、只读不写库。
- **PDF 导出（ERP-124）**：PDF 签名 / 内容类型 / A4 尺寸、嵌入 SimHei（非缺字字体）、字体缺失显式失败、
  两个独立分页章节（各自独立分页、页大小上限 100）、原币文本原样与显式剩余证据状态标签、双权限 / 越界客户拒绝、只读不写库。

## 9. 验证

```
dotnet build NEWERP.sln -c Release --no-restore --no-incremental
dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build --filter "FullyQualifiedName~CustomerReportPacket"
node tests/automation/customer_report_packet_ui.test.js
```
