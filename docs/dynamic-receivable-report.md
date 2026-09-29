# 动态客户应收账款证据报表预览（ERP-117）

## 1. 目标

授权业务员在自己的 ERP 数据范围内，选择客户销项发票证据字段并应用有界筛选，只读预览
①发票含税总额证据、②显式收款分摊证据、③算术剩余证据；无身份 / 无角色 / 无客户资料菜单授权 /
无效字段或筛选 / 页大小超限时一律拒绝（fail closed），且不泄露范围外客户与发票数据。

## 2. 接口

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/dynamic-receivable-report` | 返回有限白名单字段目录（需登录 + 客户资料菜单授权） |
| POST | `/api/dynamic-receivable-report` | 按选定字段与有界筛选预览当前账号数据范围内的发票证据 |
| POST | `/api/dynamic-receivable-report/export` | 导出当前选定页为 Excel（xlsx，只读，复用有界授权预览与选定列顺序） |

请求体（POST，`DynamicReceivableReportRequest`）：

- `fields`：选定字段键（仅限白名单；留空 = 返回全部白名单字段，保持目录顺序）；
- `startDate` / `endDate`：发票日期区间（含当日）；
- `customerId`：客户 Id 筛选（非正整数拒绝）；
- `currency`：币种筛选（系统支持币种，如 `CNY` / `USD` / `EUR`，非法取值拒绝）；
- `allocationState`：分配状态筛选（仅 `none` / `historical_only` / `partial` / `full`，非法取值拒绝）；
- `invoiceStatus`：发票状态筛选（仅 `recorded` / `draft` / `voided` / `all`，默认 `recorded`，非法取值拒绝）；
- `page`（默认 1）/ `pageSize`（默认 20，**上限 100**）。

响应（`DynamicReceivableReportPageDto`）：`columns`（按请求顺序的选定列）、`rows`（每行仅含选定字段值）、
`total` / `page` / `pageSize` / `totalPages`，以及 `readOnlyText` / `boundaryText` / `disclaimerText` 口径文案。

## 3. 字段白名单（有限、只读）

字段来自 ERP-074 对账证据行 `CustomerReceivableReconciliationInvoiceRow` 的有限白名单，共 29 个：

- 发票身份与金额：`invoiceId` / `invoiceType` / `invoiceCode` / `invoiceNumber` / `identityText` /
  `invoiceDate` / `grossAmount` / `status` / `statusText` / `isDraft` / `isVoided` / `isActiveEvidence`；
- 客户与币种：`customerId` / `customerCode` / `customerName` / `currency` / `amountDecimals`；
- 显式收款分摊证据：`totalRowCount` / `activeRowCount` / `activeAmount` / `voidedRowCount` /
  `effectiveCount` / `effectiveAmount` / `effectiveReceiptCount` / `allocationState` / `allocationStateText`；
- 算术剩余证据：`remainingAmount` / `remainingState` / `remainingStateText`。

**明确不暴露**账龄 / 到期日字段（`agingBucket` / `agingBucketText` / `dueDateKnown`）——本报表
<strong>不主张</strong>权威应收账款余额、收款 / 核销结算、到期日账龄或催收状态。

## 4. 数据边界与安全

1. **身份**：缺少登录用户 Id → 未认证（`2000`），不返回任何数据。
2. **菜单授权**：复用既有「角色 → 菜单」模块授权，要求 `customer`（客户资料）菜单；
   无角色 / 无该菜单授权 → 权限不足（`2002`）。授权每次请求都重新查询，回收后立即收敛。
3. **数据范围**：复用 `SalespersonDataScopeService`（ERP-097 唯一权威口径）：
   特权账号（超级管理员 / 系统内置角色 / 显式特权角色）不过滤；受限制业务员只看到
   `BaseCustomer.EmpId == 本人` 的客户及其销项发票证据，未映射业务员时为空集合（fail closed）。
   范围过滤在 `Count` / `Skip` / `Take` 之前应用，越界客户绝不进入计数与结果页。
4. **字段白名单**：字段仅限上述 29 个白名单，未知字段显式拒绝；不拼接、不接受任意字段名或 SQL。
5. **筛选白名单**：仅客户 / 发票日期 / 币种 / 分配状态 / 发票状态（复用 ERP-074 口径）；
   币种、分配状态与发票状态只接受白名单取值，未知取值拒绝；
   客户 Id 非正、开始日期晚于结束日期、`pageSize` 超出 `1~100` 均在读取发票 / 分摊证据之前拒绝。
6. **只读**：全程 `AsNoTracking`，无 `Add` / `Update` / `Remove` / `SaveChanges`，
   不执行任意 SQL（无 `FromSql*` / 存储过程调用），不做任何写入。

## 5. 剩余证据口径（复用 ERP-074）

算术剩余证据 = 发票含税总额 − 有效已分摊金额，仅保留三种状态：

- `known`：证据可确认（`remainingAmount` = 含税总额 − 有效分摊）；
- `unknown`：非有效证据（草稿 / 已作废）或命中有界读取上限（`remainingAmount` 为 null，绝不用 0 顶替）；
- `over_allocated`：有效已分摊金额 > 发票含税总额（源规则矛盾，`remainingAmount` 为 null，绝不轧为 0 或负数）。

分配状态同样只按持久化 ERP-073 收款分摊行派生：`none` / `historical_only` / `partial` / `full`，
`over_allocated` 与 `unknown` 仅在读取时派生，不提供为筛选。

## 6. 请求审计

- 预览为 `POST`，由既有 `OperationLogMiddleware` 按 HTTP 方法记录到 `SysOperationLog`，查询自身不写任何操作日志。
- 目录为 `GET`，按既有中间件口径不落操作日志（沿用现有审计约定）。
- 查询代码零写入，审计由请求管道统一完成。

## 7. 只读保证

- 不新增表 / 列 / 索引，不执行迁移、生产 SQL 或真实数据库操作；
- 不改写客户、发票证据、收款分摊、收款单、销售订单、库存、财务或结算记录；
- 不读取或打印任何连接串、JWT / OSS 密钥或 `.env` 值。

## 8. 关键文件

- `src/ERP.Application/DTOs/DynamicReceivableReportDtos.cs`：目录 / 请求 / 结果 DTO；
- `src/ERP.Application/Services/DynamicReceivableReportRules.cs`：字段白名单、校验与行映射（纯规则）；
- `src/ERP.Application/Interfaces/IDynamicReceivableReportQuery.cs`：查询接缝；
- `src/ERP.Infrastructure/Reports/DynamicReceivableReportQuery.cs`：只读查询实现；
- `src/ERP.Application/Services/CustomerReceivableReconciliationService.cs`：新增 `ForScopedPreviewAsync`（作用域化派生）；
- `src/ERP.Api/Controllers/DynamicReceivableReportController.cs`：HTTP 控制器；
- `src/ERP.Application/Services/DynamicReceivableReportRules.cs`：字段白名单、校验、行映射与 Excel 公式注入转义（纯规则）；
- `src/ERP.UnitTests/DynamicReceivableReportTests.cs`：预览单元测试；
- `src/ERP.UnitTests/DynamicReceivableExcelTests.cs`：Excel 导出单元测试。

## 9. 测试覆盖（`ERP.UnitTests/DynamicReceivableReportTests.cs`）

- 字段白名单目录（29 个字段、不含账龄字段、所需菜单与 100 上限）；
- 无身份（未认证）、无客户资料菜单授权（权限不足）；
- 越界业务员（只返回自己客户、越界筛选返回空）、未映射业务员（空范围，不泄露任何客户）；
- 无效字段 / 无效客户 Id / 无效日期区间 / 无效币种、分配状态与发票状态 / 页大小超限（读取前拒绝，100 本身通过）；
- 已知 / 未知 / 超额分摊三种剩余证据状态（含 `allocationState` 派生、草稿发票 unknown）；
- 只读不写库（`SaveChangesAsync` 调用次数恒为 0）。

## 10. 验证

安全档构建 / 测试：

```
dotnet build NEWERP.sln -c Release --no-restore --no-incremental /p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true
dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build
```

## 11. 前端设计器（ERP-118）

### 11.1 入口与脚本

- 入口：客户销项发票证据登记册工具栏「📊 应收证据报表」按钮（`customer-sales-invoices.js` 列表工具栏，
  点击调用 `openDynamicReceivableReport(CSI.filters.customerId || null)`，把当前登记的客户筛选带入设计器）。
- 脚本：`wwwroot/js/dynamic-receivable-report.js`（`index.html` 注册，加载于 `app.js` 之前）。

### 11.2 用户步骤

1. 登录后进入「客户销项发票证据登记册」列表页；
2. 点击工具栏「📊 应收证据报表」，设计器弹窗打开并调用 `GET /api/dynamic-receivable-report` 加载字段白名单目录
   （无登录 / 无「客户资料」菜单授权 → 显示「未登录 / 权限不足」错误态，不返回任何字段）；
3. 「① 选择字段」区域按目录渲染 29 个字段复选框（全选 / 清空），无自由填写的字段名；
4. 「② 筛选」区域设置开票日期区间、客户（来自既有 `/api/base/customers`）、币种、分配状态、发票状态
   （均为下拉 / 日期控件，币种 / 分配状态 / 发票状态只接受枚举取值，无自由 SQL）；
5. 点击「预览」→ `POST /api/dynamic-receivable-report`，只发送「白名单字段 + 有界筛选 + 有界分页（pageSize 1~100）」；
6. 结果区安全渲染返回的列名与单元格（全部 HTML 转义），并对剩余证据状态 `known` / `unknown` / `over_allocated`
   与分配状态单独着色标注，绝不把 null 金额回落为 0；显示只读 / 边界 / 免责口径文案、总数与分页；
   空结果 / 无效请求 / 授权失败 / 网络失败分别显示可见提示，全程无写入。

### 11.3 安全与只读

- 字段选择器仅由 ERP-117 目录渲染，请求组装时再次按目录白名单过滤（`dsrSelectFields`），未知字段绝不进入请求；
- 筛选仅客户 / 开票日期 / 币种 / 分配状态 / 发票状态，币种、分配状态与发票状态只接受枚举取值（`dsrBuildRequest`
  再次校验，非法取值直接丢弃、绝不进入请求）；无任意 SQL、无自由字段名输入；
- 预览为 `POST`，由既有 `OperationLogMiddleware` 记录审计；前端不写库、不迁移、不执行任意 SQL。

### 11.4 验证

- UI 逻辑单测：`node tests/automation/dynamic_receivable_report_ui.test.js`（覆盖字段白名单选择、请求边界、
  安全单元格渲染、剩余证据状态标注、空结果与授权 / 网络 / 无效失败态、前端接线契约与无任意 SQL）；
- 语法检查：`node --check src/ERP.Api/wwwroot/js/dynamic-receivable-report.js`；
- 安全档构建 / 测试：`dotnet build NEWERP.sln -c Release` 与
  `dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build`。

## 12. Excel 导出（ERP-119）

### 12.1 接口

- `POST /api/dynamic-receivable-report/export`：请求体与预览完全相同（`DynamicReceivableReportRequest`），
  只导出「当前页」的选定列；数据工作表复用 `ERP.Infrastructure.Export.ExcelExporter`。
- 文件名 `CustomerReceivableEvidence_yyyyMMddHHmmss.xlsx`；
  内容类型 `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`。

### 12.2 安全与边界（导出时重新校验）

1. **身份 / 菜单授权**：导出与预览同源，复用 `DynamicReceivableReportQuery.PreviewAsync`，每次请求重新校验
   登录用户与 `customer`（客户资料）菜单授权（fail closed），不缓存权限。
2. **数据范围**：复用 ERP-097 业务员数据范围，只导出当前账号可见行；越界筛选导出为空（仅表头工作簿）。
3. **字段 / 筛选 / 页大小**：与预览相同的 29 个白名单字段、客户 / 发票日期 / 币种 / 分配状态 / 发票状态枚举、
   日期区间与 `1~100` 页大小校验，无效请求在读取前拒绝。
4. **只导当前页**：只导出请求 `page` / `pageSize` 对应的那一页（单页上限 100），不是全量导出。
5. **原币与剩余证据状态**：金额按发票 / 分摊证据原币保留，绝不汇率换算或跨币种求和；`known` / `unknown` /
   `over_allocated` 三种剩余证据状态原样保留，`remainingAmount` 为 null 时单元格为空（不轧为 0 或负数）。
6. **公式注入防护**：文本单元格以 `=` / `+` / `-` / `@` / 制表符 / 回车 / 换行开头时，前缀单引号转义，
   使单元格保持字面文本、不被当作公式执行（`DynamicReceivableReportRules.EscapeFormulaLeading`）。
7. **只读 + 审计**：无写入、无任意 SQL；`POST` 由既有 `OperationLogMiddleware` 记录审计（动作「导出」）。

### 12.3 限制（证据边界）

- 单次导出最多 `pageSize ≤ 100` 行（有界分页），不提供全量导出。
- 空结果页返回仅含表头的工作簿（数据工作表 0 数据行）。
- 导出不含权威应收账款余额、收款 / 核销结算、到期日账龄或催收结论，也不含跨币种合计或换算。

### 12.4 前端（设计器）

- 「📥 导出 Excel」按钮与预览共用同一请求体（当前字段 / 筛选 / 页码 / 每页条数）；
- 成功（xlsx 附件）触发浏览器下载；授权失败（`2000/2002/2003`）与网络失败在结果区可见；空页正常下载仅表头文件。

### 12.5 测试覆盖（`ERP.UnitTests/DynamicReceivableExcelTests.cs`）

- 导出当前页的列顺序、行数与单元格值（含原币保留）；
- 公式前导文本转义（`=1+1` 等保持字面、非公式单元格）；
- `known` / `unknown` / `over_allocated` 三种剩余证据状态与 null 剩余金额（不轧为 0）；
- 无身份 / 无菜单授权 / 越界业务员（含越界客户空页）/ 页大小超限拒绝；
- 空页仅表头、仅导出当前页受页大小上限约束、以及只读不写库。

## 13. 分组与小计（ERP-120）

### 13.1 接口

- `POST /api/dynamic-receivable-report` 请求体新增 `groupBy`（可选）：仅 `none` / `customer` / `month`；
  空 / 缺省 = `none`（不分组）；任何其它取值在读取任何数据之前即拒绝（`InvalidParameter`，fail closed）。
- 响应 `DynamicReceivableReportPageDto` 新增 `groupBy`（回显规范化的分组键）与 `groups`：
  仅当请求分组时，`groups` 才给出「当前预览页」按分组键 + 币种分开的页面小计；默认 `none` 时为空列表。

### 13.2 页面小计语义（关键）

- `groups` 是**当前预览页的小计**（page subtotal），**不是**全量合计：它只对「本次请求返回的、
  已通过 ERP-117 权限 + ERP-097 业务员数据范围校验的同一批有界行」聚合，换页后小计随之变化。
- 每个分组（客户 / 月份）内再**按币种分开**统计 `count`、`grossAmount`（发票含税总额）与
  `effectiveAllocatedAmount`（有效已分摊金额）；金额只对同币种求和，**绝不跨币种换算或相加**。
- 剩余证据 `remainingAmount` / `remainingState` 只在组内全部行都「可确认」（`known`）时给出金额；
  任一行为 `unknown` / `over_allocated` 时，该币种小计的剩余证据标注为 `unknown` / `over_allocated`
  且 `remainingAmount` 为 `null`（**绝不轧为假余额**）。优先级：`over_allocated` &gt; `unknown` &gt; `known`。
- 分组与小计为纯函数（`DynamicReceivableReportRules.BuildGroupSubtotals`），确定性排序：
  客户按 `customerId` 升序、月份按年月升序、组内币种按 `Currency` 枚举顺序；未知币种仍单独成行并排最后。
- 分组时服务端自动补齐计算所需的 `currency` / `grossAmount` / `effectiveAmount` / `remainingAmount` /
  `remainingState` 及分组键字段（`customerId` / `invoiceDate`），保证即使请求未选择这些字段也能得到正确、币种安全的小计。

### 13.3 前端设计器

- 「② 筛选」区域新增「分组」下拉（`none` / `customer` / `month`，与后端 `NormalizeGroupBy` 一致）；
- 结果区在汇总下方渲染「本页小计（按币种，仅当前页）」，明确标注小计只针对当前页；
  剩余证据仅当可确认时显示金额，否则显示「剩余未知 / 剩余超额分摊（无效）」而不显示假余额。

### 13.4 安全与只读

- 复用 ERP-117 的「角色 → 菜单」授权（`customer`）与 ERP-097 业务员数据范围，小计只来自授权可见行；
- 全程只读：无新增表 / 列 / 迁移，无写入，无任意 SQL；请求由既有 `OperationLogMiddleware` 记录审计；
- 不读取或打印任何连接串 / JWT / OSS 密钥或 `.env` 值。

### 13.5 测试覆盖（`ERP.UnitTests/DynamicReceivableGroupingTests.cs`）

- 混合币种分开小计（绝不合并）、月份边界不跨月、未选择字段时服务端补齐分组字段仍可小计；
- 未知 / 超额分摊证据下组内剩余证据标注未知 / 超额分摊且金额为 null（不轧为假余额）；
- 空页无分组小计、越界业务员仅本人客户分组、无效分组键拒绝、以及只读不写库。


