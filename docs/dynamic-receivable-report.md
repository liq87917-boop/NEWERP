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
- `src/ERP.UnitTests/DynamicReceivableReportTests.cs`：单元测试。

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
