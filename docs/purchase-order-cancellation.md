# 采购订单取消护栏（ERP-345）

## 1. 目标

在既有采购订单取消路由（`POST /api/purchase-orders/{id}/cancel`）上增加 fail-closed 护栏：

- 校验当前身份、采购订单（`purchase-order`）菜单授权与客户数据范围；
- 校验实时单据状态（重复取消 / 终止态拒绝）；
- 在仍有「已审核且未冲销」的采购入库履约或「有效」供应商付款 / 发票引用证据时拒绝取消；
- 只列出授权范围内的简明原因，绝不按单号 / 金额 / 字符串猜测链接，也绝不跨币种合计金额。

## 2. 有效证据口径

严格复用既有证据口径（ERP-050 / ERP-067）：

| 证据 | 视为「有效」的条件（任一不满足即不阻断取消） |
| --- | --- |
| 采购入库单（`StockIn`） | `PurchaseOrderId == 本单` 且 `!IsDeleted` 且 `Status == Approved`（已取消 = 已冲销） |
| 付款单 → 采购订单（`SupplierPaymentAllocation`） | `Status == 1（有效）`、付款单可用（`!IsDeleted`）、引用行金额为正、供应商 / 币种 / 订单币种快照自相一致，且订单供应商 / 币种与付款单一致 |
| 付款单 → 采购发票（`SupplierPaymentInvoiceAllocation`） | 发票经 `PurchaseInvoiceAllocation`（`!IsDeleted`）关联到本订单；引用行 `Status == 1`、付款单可用、发票仍为「已登记」（非草稿 / 非作废）、金额不超两侧快照上限、供应商 / 币种 / 快照自相一致 |

作废（`Status == 2`）、冲销（入库单 `Cancelled`）与软删除（`IsDeleted`）证据只有在既有权威工作流显式标记其失效后才被忽略。

## 3. 原子性与并发

- 取消的「拒绝判定 + 状态变更」在**同一个可串行化事务**内完成，并对采购订单行加 `UPDLOCK, HOLDLOCK`（`db_owner.PurchaseOrders`），与 ERP-342 入库审核使用同一把锁。
- 并发「入库审核通过」与「来源取消」不可能同时成功：入库审核先提交 → 取消看到已审核入库而拒绝；取消先提交 → 入库审核看到来源订单已取消而拒绝。

## 4. 非目标（边界）

- 取消只把状态置为 `Cancelled`，**不改动**订单明细 / 金额 / 供应商 / 币种等原始字段；
- 取消本身**不冲销**库存或财务；冲销只走既有冲销服务（入库单取消、付款引用作废等）；
- 不新增报表 / Agent / 表结构 / 权限模型，不执行生产库变更。

## 5. 测试

- 单元测试：`src/ERP.UnitTests/PurchaseOrderCancellationTests.cs`（内存库）。
- 集成测试：`src/ERP.IntegrationTests/PurchaseOrderCancellationSqlServerTests.cs`（专用 `(localdb)\NEWERP_AutoAcceptance` + `NEWERP_AUTOTEST` 前缀库）。
