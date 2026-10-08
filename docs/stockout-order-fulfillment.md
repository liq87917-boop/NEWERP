# 销售出库衔接来源订单并防止累计超发（ERP-343）

## 1. 目标

让销售出库单通过既有 `StockOut.SalesOrderId` 显式衔接来源销售订单，并在审核时用权威的「累计已审核出库数量」防止对同一订单累计超发。只复用既有分层架构与既有单据流转（创建 / 更新 / 提交 / 审核 / 取消），不做报表、不做 Agent、不做界面视觉 / 样式 / 导出打磨。

## 2. 口径

- **来源链接**：`StockOut.SalesOrderId`（可空，刻意不建外键）。未链接（null）的历史单据**不做任何校验**，行为与改造前完全一致。
- **权威链接**：订单存在、未删除、`Status = Approved`、客户一致，且本单每条明细都能在订单里找到**唯一**且可折算的商品明细行。
- **已审核出库数量**：以该订单为来源、`IsDeleted = 0`、`Status = Approved` 的出库单明细数量之和；明细数量已由 `StockUnitConversion.NormalizeAsync` 折算为商品基础单位。
- **订单授权数量**：来源销售订单中该商品的**唯一**明细行数量，按商品基础 / 包装单位折算为基础单位（等于基础单位直接使用，等于装箱单位按 `UnitsPerPackage` 折算）。
- **上限判定**：仅当链接权威时，逐商品比较 `已审核数量（不含本单） + 本单数量 ≤ 订单授权数量 + 0.0001 容差`；跨单位、跨币种**不合计**，每张单据只和自己的来源订单比较。

## 3. 链接权威性判定（无效链接 fail closed 拒绝）

`StockOutOrderFulfillmentRules.ValidateApprovalAsync` 与新增的 `ValidateLinkAsync`（创建 / 更新时调用）共用同一份权威判定：显式链接（`SalesOrderId` 非空）必须构成权威来源，否则**抛 `BusinessException.RuleConflict`，拒绝履约 / 拒绝保存**；未链接（null）的历史单据不做任何校验，行为保持不变。绝不猜测不明确的重复订单行，也绝不臆造授权数量。以下任一情况视为无效链接：

| 判定项 | 权威要求 |
|---|---|
| 订单可用性 | 存在、未删除且 `Status = Approved` |
| 客户一致 | `StockOut.CustomerId == SalesOrder.CustomerId` |
| 商品身份 | 每条出库明细必须指定商品，且商品存在、未删除 |
| 订单行唯一 | 来源订单里该商品**有且仅有一行**明细：零行 = 缺证据，多行 = 语义不唯一，均**不任选一行** |
| 订单行单位 | 订单行单位必须是商品基础单位或装箱单位（装箱数 > 0） |
| 出库明细单位 | 出库明细单位必须与商品基础单位兼容（空单位按基础单位处理；装箱单位已在折算阶段改为基础单位） |

销售员 / 客户数据范围与菜单权限**保持不变**，不新增权限、不扩权。

## 4. 审核累计上限与并发串行化

`StockOutController.Approve` 按以下顺序执行，整体包在 `IsolationLevel.Serializable` 事务内：

1. 折算本单明细单位并重算汇总；
2. 开启可串行化事务；
3. 对来源订单行执行 `SELECT Id FROM db_owner.SalesOrders WITH (UPDLOCK, HOLDLOCK) WHERE Id = @id` 取得更新锁，把**同一订单**的并发审核串行化（未链接订单不加锁；非关系型提供程序跳过）；
4. 幂等护栏：已产生有效库存流水的单据拒绝重复审核；
5. `StockOutOrderFulfillmentRules.ValidateApprovalAsync` 判定链接权威性并做累计上限校验；
6. 逐行扣减库存与写入库存流水、置为已审核、提交事务。

任一步失败即回滚：库存、库存流水、单据状态都保持不变。已取消 / 已驳回 / 待提交 / 已提交的出库单不计入已审核数量，取消（`Status = Cancelled`）后自动释放剩余额度。

## 5. 错误信息（API 直接返回）

累计超限时通过既有 `BusinessException.RuleConflict` 抛给既有异常中间件，消息含商品与数量明细，例如：

- 「商品 [X] 累计出库数量 11 超过来源销售订单授权数量 10（已审核 6 + 本次 5）」

无效显式链接在审核前抛 `BusinessException.RuleConflict`，消息说明具体失效原因（订单不存在 / 已删除 / 未审核 / 客户不一致 / 商品或单位不兼容 / 重复明细）；有效权威来源仍按基础单位累计校验，不因来源身份失效而静默禁用数量护栏。

## 6. 不改写项

- 不重写订单原始数量、不改写历史流水数量与金额、不改历史出库单、不新增表列 / 外键 / schema；
- 不新增报表、Agent、权限、菜单；销售员 / 客户数据范围保持现状。

## 7. 测试

- 单元测试：`src/ERP.UnitTests/StockOutOrderFulfillmentTests.cs`（内存库，覆盖链接权威性、部分 / 满量批次、累计超限、取消释放、重复审核、包装单位、权限无扩展）。
- 集成测试：`src/ERP.IntegrationTests/StockOutOrderFulfillmentSqlServerTests.cs`（NEWERP_AUTOTEST 护栏，真实 SQL Server，覆盖非权威链接、累计、取消、包装、同单并发审核串行化）。
