# 客户销售订单价格历史（ERP-108）

## 1. 目标

为客户提供一个**只读**的销售订单价格历史工作台：按商品查看该商品在**已审核**销售订单明细中
持久化的历史价格，用于比对历史成交单价与贸易条款。本功能**绝不改写任何价格、单据或贸易条款**。

## 2. 口径（唯一权威口径）

- 数据来源：`SalesOrderDetails`（未删除）+ 其所属 `SalesOrders`（未删除且 `Status == Approved`）。
- 分组键：**客户 + 商品 + 规格 + 单位 + 币种** 完全一致时才归为同一口径组。
- 组内保留**原始单价（UnitPrice）与贸易条款（TradeTerms）**，原样回显、绝不重定价。
- 口径不一致的证据（单位 / 币种 / 规格 / 客户不同）**分组成行单列**，
  绝不跨口径比较、绝不做汇率换算、绝不合并不同币种金额。

## 3. 数据范围与边界

- 业务员数据范围是**硬边界**：复用 ERP-097 `SalespersonDataScopeService`，
  受限制业务员只能看到其被分配客户的历史价格；特权账号不受限。
- 只读：不写库，不执行迁移 / 生产 SQL / 真实数据库操作 / 部署，不改写订单状态与已登记进度。
- 分页有界：每页 1 ~ 200 条（超出按上限截断），命中截断显式标注；稳定按
  「订单日期 + 订单 Id + 明细 Id」排序。
- 不是定价工具、不是报价依据、不是客户对账单，也不推算账期或应收。

## 4. 接口

`GET /api/sales-orders/price-history`

| 参数 | 类型 | 说明 |
|---|---|---|
| `productId` | long | 商品 Id（必填） |
| `customerId` | long? | 客户筛选（留空 = 全部客户） |
| `dateFrom` | date? | 订单日期开始（含当天） |
| `dateTo` | date? | 订单日期结束（含当天） |
| `page` | int | 页码（从 1 开始） |
| `pageSize` | int | 每页条数（1 ~ 200） |

返回 `CustomerSalesPriceHistoryView`：`TotalCount` / `Page` / `PageSize` / `Truncated` / `GroupCount` /
`RuleText` / `EmptyText` / `Groups[]`；每组含 `BasisText` / `RowCount` / `Rows[]`（每行含
`OrderId` / `OrderNo` / `OrderDate` / `CustomerId` / `CustomerName` / `ProductId` / `ProductName` /
`Spec` / `Unit` / `Currency` / `UnitPrice` / `Quantity` / `Amount` / `TradeTerms`）。

## 5. 前端

- 入口：销售订单页工具栏「📈 价格历史」（`modules-doc.js` 的 `extraActions`）。
- 脚本：`wwwroot/js/customer-sales-price-history.js`（`index.html` 注册）。
- 工作台：商品 / 客户 / 订单日期筛选 + 有界分页；逐条以来源订单号链接回来源销售订单（`openForm`）。

## 6. 测试

`src/ERP.UnitTests/CustomerSalesPriceHistoryTests.cs` 覆盖：口径分组、原始单价与贸易条款保留、
状态（仅已审核）、软删除（订单 / 明细）、业务员数据范围、分页有界与截断、无写入语义、
参数校验、接口路由与前端接线静态断言。
