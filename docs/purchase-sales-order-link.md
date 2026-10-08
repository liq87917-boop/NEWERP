# 采购订单归属销售订单链接（ERP-346）

## 1. 目标

采购订单在创建 / 更新时，若显式填写 `OwningSalesOrderId`（归属销售订单 Id），服务端必须把它解析为**权威来源**并统一来源快照，杜绝悬空链接与冲突快照；未填写时保持未关联备货行为，绝不臆造销售链接。

## 2. 口径

- **显式链接**：`OwningSalesOrderId` 非空即视为显式链接，必须为正整数。
- **权威来源**：来源销售订单必须存在、未删除、已审核且未被取消（`Approved`）。
- **授权**：当前账号必须具备采购订单（`purchase-order`）模块授权，且其客户数据范围覆盖来源销售订单客户（fail closed）。
- **兼容性**：采购订单归属客户须与来源客户一致（未填写时由来源派生）；采购明细商品必须能在来源销售订单明细中找到，且单位兼容（忽略大小写、去首尾空白；任一侧单位缺失时不判定为不兼容）。
- **快照统一**：`OwningSalesOrderNo` / `OwningCustomerId` / `OwningCustomerName` 一律由来源销售订单与客户档案权威派生，绝不采信客户端冲突快照。
- **未关联**：`OwningSalesOrderId` 为 null 时保持历史行为，不校验、不派生、不臆造。
- **金额口径**：原始商业币种 / 单价保持不变，采购总额仍由既有 `PurchaseOrderController.Calculate`（Σ 数量 × 单价）计算，不强制币种相等、不跨币种合计。
- **冻结**：更新只允许待提交（`Pending`）单据；更新时重新解析来源与权限。

## 3. 实现

- `src/ERP.Application/Services/PurchaseSalesOrderLinkRules.cs`：纯内存判定 + 有界只读查询（按 Id 精确解析单个来源订单、按来源客户 Id 精确读取客户档案），不落库、不改销售 / 库存 / 财务、不开启事务。
- `src/ERP.Api/Controllers/PurchaseOrderController.cs`：`Create` / `Update` 在保存前调用 `ApplyLinkAsync`；更新先解析来源与权限，失败不改动现有单据。

## 4. 测试

- `src/ERP.UnitTests/PurchaseSalesOrderLinkTests.cs`：内存库覆盖授权链接、未关联、非正 / 不存在 / 已删除 / 已取消 / 未审核、无身份 / 无菜单 / 范围外、归属客户冲突、商品 / 单位不兼容、伪造快照、更新时来源变更。
- `src/ERP.UnitTests/OrderTraceabilityTests.cs`：既有追溯夹具改为使用真实授权来源，替换悬空 `555L` / `666L` 假 Id。
- `src/ERP.IntegrationTests/PurchaseSalesOrderLinkSqlServerTests.cs`：真实 SQL Server（`NEWERP_AUTOTEST` 护栏）覆盖有界解析、授权链接、拒绝 / 未映射、伪造快照、商品 / 单位不兼容、未关联、来源编辑与保存之间变更，以及目标库护栏拒绝非专用目标。

## 5. 安全

SQL Server 集成测试仅允许专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST`、Integrated Security；每次使用全新 GUID 库名，若库已存在则拒绝而非删除 / 重置；绝不读取 appsettings / .env / 生产凭据。
