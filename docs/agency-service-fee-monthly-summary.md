# 代理服务费对账单月度汇总（ERP-110）

## 1. 目标

为对账单证据册（ERP-070）提供一个**只读**的月度汇总：把未删除的代理服务费对账单证据按
「**对账日期所属年月 + 客户 + 原币**」分组，快速查看每个客户每个月在每个原币下的
已登记证据合计，以及草稿 / 已作废的单独计数。本功能**绝不改写任何对账单、协议、客户或单据**。

## 2. 口径（唯一权威口径）

- 数据来源：`AgencyServiceFeeStatements`（未删除）。
- 分组键：**对账日期所属年月（`StatementDate.Year` / `StatementDate.Month`）+ 客户 Id + 币种（原币）**。
- 原币合计：仅**未删除且已登记**（`Status == 1`）的对账单计入，合计直接来自服务端计算的持久化
  `TotalAmount`，**不重算、不换算、不把草稿 / 已作废金额并入**。
- 草稿（`Status == 0`）与已作废（`Status == 2`）**单独计数、金额单独列示**，绝不并入原币合计。
- 服务期间跨月**不按期间分摊**：一条对账单证据无论其服务期间跨越多个月，都**全额**计入其
  **对账日期**所属月份，不按天数 / 月份拆分、不重算、不跨月分摊。
- 币种隔离：不同币种分别成组、绝不合并、绝不换算，模型与界面**没有任何跨币种总额**。

## 3. 数据范围与边界

- 只读：不新增 / 不修改任何表与列、不写库、不迁移、不回填；不改写对账单证据（含合计 / 状态 /
  服务期间 / 行清单）、ERP-069 协议、客户主数据、销售订单、装柜清单、单证、发票、收款、库存、
  费用与结算记录；不开票、不记账、不收款或付款、不催收、不调用外部服务。
- 读取：分组键（年月 + 客户 + 币种）与分页在**一次**去重查询内完成，本页对账单再**一次**批量装载，
  固定次数数据集访问，与对账单张数 / 行数无关，**无逐行查库**。
- 分页有界：每页 1 ~ 200 个月份分组（超出按上限截断），命中截断显式标注；稳定按
  「年 + 月 + 客户 Id + 币种」排序。
- 不是收入确认、不是应收账款或应收余额、不是付款通知或催款函、不是税务申报或开票依据、
  不是结算或核销确认。

## 4. 接口

`GET /api/agency-service-fee-statements/monthly-summary`

| 参数 | 类型 | 说明 |
|---|---|---|
| `statementDateFrom` | date? | 对账日期开始（含当天） |
| `statementDateTo` | date? | 对账日期结束（含当天） |
| `customerId` | long? | 客户筛选（留空 = 全部客户） |
| `currency` | string? | 币种筛选（留空 = 全部币种） |
| `page` | int | 页码（从 1 开始） |
| `pageSize` | int | 每页条数（1 ~ 200） |

返回 `AgencyServiceFeeMonthlySummaryView`：`Total` / `Page` / `PageSize` / `TotalPages` /
`Truncated` / `GroupCount` / `Rows[]` / `RuleText` / `BoundaryText` / `ReadOnlyText` /
`NoProrationText` / `CurrencyIsolationText` / `EvidenceOnlyText`；每组含 `StatementYear` /
`StatementMonth` / `StatementMonthText` / `CustomerId` / `CustomerCode` / `CustomerName` /
`Currency` / `AmountDecimals` / `RegisteredCount` / `RegisteredTotalAmount` /
`RegisteredTotalAmountText` / `DraftCount` / `DraftTotalAmount` / `DraftTotalAmountText` /
`VoidedCount` / `VoidedTotalAmount` / `VoidedTotalAmountText` / `StatementCount`。

## 5. 前端

- 入口：对账单证据册（ERP-070）列表工具栏「📊 月度汇总」。
- 脚本：`wwwroot/js/agency-service-fee-monthly-summary.js`（`index.html` 注册）。
- 工作台：客户 / 币种 / 对账日期区间筛选 + 有界分页；界面只显示**证据数字**标签
  （`证据口径 / 只读 / 模块边界 / 币种隔离 / 服务期间跨月不按期间分摊`）。

## 6. 测试

`src/ERP.UnitTests/AgencyServiceFeeMonthlySummaryTests.cs` 覆盖：月份边界（月初 / 月末 / 跨年）、
币种（不同币种分组成行、无跨币种总额、JPY 0 位小数）、状态（仅已登记计入原币合计、草稿与已作废
单独计数）、软删除（已删除不计入）、服务期间跨月（不按期间分摊、全额计入对账日期所属月份）、
分页有界与截断、无写入语义（查询后无待保存变更）、参数校验（日期区间倒置 / 非法币种拒绝）、
接口路由与前端接线静态断言，以及规则 / 文档同源。
