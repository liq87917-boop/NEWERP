# 销售订单取消护栏（ERP-347）

## 1. 目标

在既有销售订单取消路由（`POST /api/sales-orders/{id}/cancel`）上增加 fail-closed 护栏：

- 校验当前身份、销售订单（`sales-order`）菜单授权与客户数据范围；
- 校验实时单据状态（重复取消 / 终止态拒绝）；
- 在仍有「已审核且未冲销」的销售出库履约、「未删除且未取消」的采购订单履约（归属销售订单）或「有效（未作废）」客户收款引用证据时拒绝取消；
- 只列出授权范围内的简明原因，绝不按单号 / 金额 / 字符串猜测链接，也绝不跨币种合计金额。

## 2. 有效履约 / 证据口径

严格复用既有显式引用字段：

| 证据 | 视为「阻断取消」的条件（任一不满足即不阻断） |
| --- | --- |
| 销售出库单（`StockOut`） | `SalesOrderId == 本单` 且 `!IsDeleted` 且 `Status == Approved`（已取消 = 已冲销） |
| 采购订单（`PurchaseOrder`） | `OwningSalesOrderId == 本单` 且 `!IsDeleted` 且 `Status != Cancelled`（已取消 / 已删除释放） |
| 客户收款引用（`CustomerReceiptAllocation`） | `SalesOrderId == 本单` 且 `!IsDeleted` 且 `Status == 1（有效）`（作废释放） |

已取消 / 已冲销 / 已作废 / 软删除证据只有在既有权威工作流显式标记其失效后才被忽略。

## 3. 原子性与并发

- 取消的「拒绝判定 + 状态变更」在**同一个可串行化事务**内完成，并对销售订单行加 `UPDLOCK, HOLDLOCK`（`db_owner.SalesOrders`），与 ERP-343 出库审核使用同一把锁。
- 采购归属关联（ERP-346）创建 / 更新在显式归属销售订单时，同样在可串行化事务内对来源销售订单行加 `UPDLOCK, HOLDLOCK`。
- 并发「出库审核通过 / 采购归属关联」与「来源取消」不可能同时成功：履约先提交 → 取消看到有效履约而拒绝；取消先提交 → 履约侧看到来源订单已取消而拒绝。
- 收款引用登记（ERP-053）在登记前重新读取销售订单并拒绝已取消订单；取消侧在取消前拒绝仍有有效收款引用证据的订单（顺序正确性，二者共享同一份实时订单状态校验）。

## 4. 非目标（边界）

- 取消只把状态置为 `Cancelled`，**不改动**订单明细 / 金额 / 客户 / 币种等原始字段；
- 取消本身**不静默取消采购**、**不冲销**库存或财务；冲销只走出库单取消、采购单取消 / 删除、收款引用作废等既有显式工作流；
- 不新增报表 / Agent / 表结构 / 权限模型，不执行生产库变更。

## 5. 测试

- 单元测试：`src/ERP.UnitTests/SalesOrderCancellationTests.cs`（内存库）。
- 集成测试：`src/ERP.IntegrationTests/SalesOrderCancellationSqlServerTests.cs`（专用 `(localdb)\NEWERP_AutoAcceptance` + `NEWERP_AUTOTEST` 前缀库）。
