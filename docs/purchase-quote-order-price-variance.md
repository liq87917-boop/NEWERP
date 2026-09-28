# 供应商比价 → 采购订单价格差异（ERP-105）

## 1. 目的

为「已转采购订单」的采购报价（`PurchaseQuote`）提供一个**只读**的价格差异核对工作台：把比价行与
其生成的那张采购订单（`PurchaseOrder`）逐行对照，仅在相同比价口径内计算单价差与金额差，帮助采购人员
核对「报价 → 下单」过程中价格是否被改动，但**不重定价、不改审批、不改任何单据**。

## 2. 边界（重要）

- **只读**：不写库、不改报价 / 采购订单 / 价格 / 审批 / 库存 / 收付款。
- **不重定价、不改审批**：审批决定（`PurchaseQuoteDecision`，ERP-095）与采购订单价格只作证据回显，绝不被本视图改写。
- **不做汇率换算、不合并不同币种金额**：价格差异只在相同口径内计算。

## 3. 比价口径

同一比价行与采购订单明细，仅当以下五项**完全一致**时才计算价格差异：

| 字段 | 来源比价行 | 采购订单侧 | 说明 |
|---|---|---|---|
| 商品 Product | `ProductId`（未引用按 0） | 明细 `ProductId` | 身份一致 |
| 规格 Spec | `Spec` | 明细 `Spec` | 去首尾空白、区分大小写 |
| 单位 Unit | `Unit` | 明细 `Unit` | 去首尾空白、区分大小写 |
| 币种 Currency | `Currency`（文本） | 订单表头 `Currency`（枚举） | 文本按枚举名解析后不区分大小写比较 |
| 是否含税 TaxIncluded | `TaxIncluded` | 订单表头 `TaxIncluded` | 含税 / 不含税 |

口径不一致、明细重复歧义、明细缺失、陈旧链接、未链接，一律显式标注为**未解决**，不产生数值差异。

## 4. 链接解析

比价行 → 采购订单的权威链接沿用转换留痕（ERP-020 / ERP-027）：

1. **持久化订单链接**：比价行 `RefOrderNo`（转换后写回的采购单号）。
2. **来源标记**：采购订单备注中的 `来源比价 {QuoteNo}（比价行 #{Id}）`。

判定顺序：

- `RefOrderNo` 为空 → **未链接**；
- 按单号找不到未删除采购订单 → **不存在或已删除**；
- 同一单号对应多张未删除订单 → **链接歧义**；
- 订单备注不含来源标记 → **陈旧链接**；
- 明细内来源标记（批次转换逐行留痕）优先定位，其次按「商品 + 规格 + 单位」口径定位；
  定位不唯一 → **明细歧义**，定位不到 → **无匹配明细**。

## 5. 接口

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/purchase/quotes/order-price-variance` | 通用查询：按可选 `dateFrom` / `dateTo` / `supplierId` / `page` / `pageSize` 筛选「已转采购订单」比价行 |
| GET | `/api/purchase/quotes/{id}/order-price-variance` | 从某个比价行打开：只返回该行的来源与采购订单对照 |

两者都：

- 排除软删除（`IsDeleted`）比价行 / 采购订单 / 明细；
- 按「报价日期 + 行 Id」稳定排序；
- 分页有界（默认 50、上限 200），命中截断时显式返回 `truncated=true`；
- 无匹配时返回显式 `emptyText`。

## 6. 响应要点

- `rows[]`：每条「来源比价行 ↔ 采购订单明细」对照，含来源证据（比价批次 / 日期 / 供应商 / 商品 /
  规格 / 单位 / 币种 / 含税 / 报价单价 / 报价总额 / `refOrderNo`）、订单证据（采购单号 / 订单日期 /
  明细 Id / 采购单价 / 采购金额 / 采购币种 / 采购含税）与结论。
- `resolved=true` 时：`unitPriceDelta = 采购单价 − 报价单价`、`amountDelta = 采购金额 − 报价总额`。
- `resolved=false` 时：`unitPriceDelta` / `amountDelta` 为 `null`，`reason` 显式给出未解决原因。
- `resolvedCount` / `unresolvedCount`：本页已核对 / 未解决行数。
- `ruleText`：口径与边界说明（与后端 `PurchaseQuoteOrderPriceVarianceRules.RuleText` 同源）。

## 7. 前端

- 供应商比价页行操作新增「📉 价格差异」（`modules.js` → `purchase-quote-order-price-variance.js`），
  仅在状态为「已转采购订单」的行显示。
- 点击后调用 `GET /api/purchase/quotes/{id}/order-price-variance`，在弹层中只读展示来源 / 订单证据
  与单价差 / 金额差；未解决行显式标注原因，弹层不含任何转换 / 编辑 / 保存动作。

## 8. 验证

单元测试 `src/ERP.UnitTests/PurchaseQuoteOrderPriceVarianceTests.cs`（内存数据库，不连接 SQL Server、
不启动 API）覆盖：单行转换、批次转换（重复商品按来源标记唯一定位）、重复商品无标记的明细歧义、
单位 / 币种 / 含税口径不一致、陈旧链接、订单删除 / 不存在、未链接、分页有界与截断、供应商 / 日期筛选、
稳定排序、无写入语义、从比价行打开、日期区间参数校验、接口路由与前端接线契约。
