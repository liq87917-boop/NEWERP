# 询价报价响应时间工作台（ERP-104）

## 1. 目标

给每张询价单提供一个**只读**的「报价响应时间」视角：询价日期到首张有效报价日期之间的日历天间隔，并把缺失 / 异常证据显式标注出来。用于回答「这张询价单过了多久才被报价」这类运营问题，**不**用于成交 / 交期 / 绩效 / 对账等结论。

## 2. 口径（与 `InquiryResponseRules` 一致，唯一权威）

| 规则 | 说明 |
|---|---|
| 显式链接 | 只按报价单上持久化的 `Quotation.InquiryId == Inquiry.Id` 建立链接；**绝不**按单号 / 文本 / 金额 / 相似度推断链接 |
| 版本链根单 | 只取 `RootQuotationId == null` 的初始版本（根单）；版本（`RootQuotationId != null`，且版本创建时复制了 `InquiryId`）**不重复计入** |
| 软删除 | 软删除的询价单与报价单一律排除，不参与响应计数 |
| 首张有效报价日期 | 显式链接且报价日期有效（年份 ≥ 1900，即非 `DateTime` 默认缺失值）的根单报价日期中的**最早者** |
| 间隔天数 | `(报价日期 − 询价日期)` 的日历天（按 `.Date` 截断到天） |
| 缺失 / 异常 | 缺失链接、缺失日期、报价日期早于询价日期（负间隔）分别作为独立证据标注，**绝不推断为已报价** |

## 3. 响应状态

| 状态 | 编码 | 级别 | 判定 |
|---|---|---|---|
| 已报价 | `quoted` | `success` | 存在首张有效报价日期且间隔 ≥ 0 |
| 未报价 | `unquoted` | `neutral` | 无有效报价单链接，或链接存在但报价日期缺失 / 无效 |
| 链接异常 | `inconsistent-link` | `warning` | 报价日期早于询价日期（间隔为负） |

## 4. 接口

`GET /api/inquiries/response-times`（类级 `[Authorize]`，JWT）

查询参数（全部可选）：

| 参数 | 说明 |
|---|---|
| `page` / `pageSize` | 分页（默认 20，单次上限 200），按**稳定询价单 Id 升序**分页 |
| `startDate` / `endDate` | 询价日期区间（含，按日期比较） |
| `customerId` | 客户 Id（显式等值；空值 = 不过滤） |
| `keyword` | 只命中询价单号 |

返回 `ApiResponse<InquiryResponseWorkspaceDto>`：`items`（行）、`total`、`page`、`pageSize`，以及 `readOnlyText` / `boundaryText` / `disclaimerText` 口径文案。

数据范围：与询价单列表同口径，复用 `SalespersonDataScopeService`（特权账号不过滤；受限制业务员仅可见其分配客户）。

## 5. 只读保证

控制器与查询全部使用 `AsNoTracking`，无 `Add / Update / Remove / SaveChanges`，不新建任何表 / 列，不改写询价单 / 报价单 / 客户主数据。单页内客户名称与显式链接的根单报价日期通过**批量查询**装载（无逐行查库）。

## 6. 前端

- 脚本：`src/ERP.Api/wwwroot/js/inquiry-response-workspace.js`（`index.html` 引入）。
- 入口：询价单模块（`modules-doc.js` 的 `'inquiry-new'`）工具栏「⏱️ 报价响应时间」（`openInquiryResponseWorkspace()`）。
- 工作台页：日期区间 / 客户 / 关键字筛选 + KPI + 分页表格（询价单号 / 询价日期 / 客户 / 状态 / 首张有效报价日期 / 间隔天数 / 响应状态 / 证据说明）+ 口径 / 只读 / 免责文案。

## 7. 边界与免责

响应时间只是日期算术证据：不代表报价是否成交、不代表承诺交期或履约，不是 SLA / 绩效结论，也不是客户对账 / 结算 / 应收应付结论。

## 8. 验证

- 单元测试：`src/ERP.UnitTests/InquiryResponseWorkspaceTests.cs`（分页、日期 / 客户 / 关键字筛选、显式链接、版本链、缺失日期、负间隔、业务员数据范围、软删除、只读不写库、前端接线契约）。
- `validation_profile: safe`（Release 构建 + `ERP.UnitTests`）。
