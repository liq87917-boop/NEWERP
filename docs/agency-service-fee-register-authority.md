# 代理服务费对账单 / 收款分摊证据登记册实时授权与客户范围（ERP-437）

本文档说明 ERP-070「代理服务费对账单证据」与 ERP-071「客户收款单 → 代理服务费对账单收款分摊证据」两套登记模块的
**实时授权与服务端客户数据范围**口径。目标：让台账 / 来源候选 / 两侧候选 / 两侧汇总 / 明细 / 登记 / 作废等**每一条**证据路由，
与既有月度汇总（ERP-110）、协议证据（ERP-069）及既有收款单生命周期守卫（ERP-349 / ERP-378）使用**同一条**身份、菜单与数据范围口径，
绝不新增授权模型。

相关任务：`ERP-437`「Enforce live receipt and statement authority on agency service fee evidence registers」（阶段 3 收据与财务证据闭环）。
上游 / 同源任务：`ERP-070` / `ERP-071` / `ERP-072` / `ERP-110` / `ERP-180`（对账单 / 分摊 / 对账账龄 / 月度汇总 / 对账报表）、
`ERP-097`（业务员客户数据范围）、`ERP-349` / `ERP-350` / `ERP-378`（收款单生命周期、唯一同币种分摊额度、收款单行锁）。

## 1. 复用既有护栏（不新增授权）

唯一授权实现仍是既有的两段组合，本模块只**复用**，不复制、不新增：

- `CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(db, userId)` —— 既有「角色 → 菜单」授权读取；
- `AgencyServiceFeeReconciliationRules.RequiredMenuCode`（`customer`，既有「客户资料」功能菜单）—— 与**月度汇总路由完全同码同源**；
- `SalespersonDataScopeService.ResolveAsync(db, userId)`（ERP-097 唯一权威数据范围，每次请求重新解析、不缓存）。

ERP-437 新增的只是把上面的既有入口组合成一个专用于本登记册的**入口授权 + 范围收敛**口径，
放在 `AgencyServiceFeeReconciliationRules`（`EnsureLiveIdentityAsync` / `EnsureRegisterAuthorizedAsync` /
`EnsureCustomerInScope` / `EnsureStatementInScopeAsync` / `EnsureReceiptInScopeAsync` / `EnsureAllocationInScopeAsync`）：

- **不新增**任何菜单 / 角色 / 用户授权，不新增表 / 列 / 实体 / 权限模型；文档与代码均不引入第二套范围口径；
- 实现位于既有 `AgencyServiceFeeReconciliationRules`，与月度汇总路由共用同一个 `RequiredMenuCode`，
  绝不新增菜单 / 角色 / 用户授权，也不提供匿名 / 管理员回退。

## 2. 每条路由的实时授权

`AgencyServiceFeeStatementController`（`/api/agency-service-fee-statements`）与
`AgencyServiceFeeCollectionAllocationController`（`/api/agency-service-fee-collection-allocations`）的**每一条证据路由**
（`GetPaged`、`source-options` / `receipts` / `statements`、`GetById`、两侧 `summary` / `allocations`、
`Create`、`Update`、`record`、`void`）在任何读取 / 写入之前先调用
`AgencyServiceFeeReconciliationRules.EnsureRegisterAuthorizedAsync(_db, CurrentUserId())` 解析实时身份、既有功能菜单与权威客户范围：

| 身份 / 授权 | 既有受控错误 |
| --- | --- |
| 缺失 / 非法（非正整数）身份 | `Unauthorized`（2000） |
| 账号不存在 / 已删除 | `Unauthorized`（2000） |
| 账号已禁用 | `Forbidden`（2002） |
| 无角色 / 无 `customer` 菜单 / 菜单被撤销 | `Forbidden`（2002） |

客户端请求体中的任何字段都不能指定或扩大身份 / 范围（用户 Id 只来自 `ClaimTypes.NameIdentifier`）。
控制器**没有** `[AllowAnonymous]`，也**没有**任何角色白名单或「退化为管理员」的回退。
`metadata` 路由只返回与身份无关的静态口径文案，不含任何证据数据。

## 3. 资源解析 + 客户范围收敛

范围锚点是被引用的**持久化客户**：对账单 `AgencyServiceFeeStatement.CustomerId`、收款单 `FinanceReceipt.CustomerId`、
分摊行快照 `AgencyServiceFeeCollectionAllocation.CustomerId`；再由 `SalespersonDataScopeService` 判定是否在当前账号可见范围内。

- 台账（`GetPaged`）：先把查询按权威客户范围过滤（`SalespersonDataScopeService.FilterByCustomer`），**再**计数与明细读取。
- 来源候选（`source-options`）与两侧候选（`receipts` / `statements`）：显式给出的客户必须落在当前账号权威范围内，
  否则按同一条不披露错误拒绝；候选派生金额只来自持久化有效行。
- 两侧汇总（`receipts/{id}/summary`、`statements/{id}/summary`）与两侧明细（`*/allocations`）：先按权威范围解析并收敛
  被引用对账单 / 收款单，再做汇总 / 明细。
- 明细（`GetById`）：按分摊行快照客户或对账单持久化客户收敛，越范围 / 已删除 / 不存在一律同一条不披露错误。
- 写入（`Create` / `Update` / `record` / `void`）：先解析并授权被引用对账单与收款单（以及 `Update` 的拟提交客户），
  越范围 / 已删除 / 不存在一律拒绝，且**拒绝发生在写库之前**——绝不落任何对账单或分摊行。

## 4. 统一的不披露错误口径

被引用对账单、收款单与分摊行**缺失、已删除或越范围**时，一律返回**同一条**不披露存在性的错误（`NotFound` = 1002）：

```
代理服务费对账单 / 收款分摊证据不存在或已删除，或不在当前账号的数据范围内
```

即「不存在」与「不可见」不区分，错误文案不含范围外资源 Id、单号、金额或计数；
因此攻击者无法通过错误差异、响应体或计数旁路推断范围外对账单 / 收款单 / 分摊行的存在性与规模。

## 5. 读与写的差异（保持 ERP-070 / ERP-071 既有证据语义）

- **写入（登记 / 修改 / 登记 / 作废）**：使用**严格**解析（对账单 / 收款单必须既有且未删除）；越范围 / 已删除 /
  不存在一律拒绝，且拒绝发生在写库之前——**绝不落任何对账单或分摊行、绝不改写任何既有证据**。
- **读取（台账 / 候选 / 汇总 / 明细）**：范围在**计数与物化之前**下推；已作废历史仍保留可读，但越范围证据对受限账号不可见。
- 本模块**不**改写收款单、对账单、协议证据、客户、销售订单、装柜与单证、发票、库存、费用、退税与结算记录；
  收款单金额仍是同一张收款单的**唯一、同币种分摊额度**，由「收款单 → 销售订单」与「收款单 → 代理服务费对账单」
  两套证据在收款单行锁下共同占用（跨消费者唯一额度与并发语义不变）。

## 6. 验证

- 单元测试（内存库，`ERP.UnitTests`）：
  - `AgencyServiceFeeStatementTests`：缺失 / 非法 / 已删除身份 → `Unauthorized`；禁用 / 撤销菜单 / 无菜单 → `Forbidden`；
    越范围 / 已删除 / 不存在对账单同一条不披露错误；台账与来源候选范围过滤；越范围 / 已删除对账单拒绝登记、修改、登记与作废且证据零变更；
    被许可的对账单登记 / 修改 / 登记 / 作废生命周期；授权撤销后下一次请求立即收敛。
  - `AgencyServiceFeeCollectionAllocationTests`：同上，并覆盖越范围 / 已删除对账单与收款单同一条不披露错误、
    两侧候选与两侧汇总 / 明细的范围收敛、越范围分摊行拒绝登记与作废且证据零变更、被许可的分摊行登记 / 作废生命周期。
  - 既有 ERP-070 / ERP-071 / ERP-072 / ERP-110 / ERP-180 回归（`AgencyServiceFeeStatementTests` /
    `AgencyServiceFeeCollectionAllocationTests` / `AgencyServiceFeeReconciliationTests` /
    `AgencyServiceFeeMonthlySummaryTests` / `DynamicAgencyServiceFeeMonthly*`）保持绿。
- SQL Server 集成测试（真实 `(localdb)\NEWERP_AutoAcceptance` + GUID 后缀 `NEWERP_AUTOTEST` 库 + `Integrated Security`）：
  `AgencyServiceFeeRegisterAuthorizationSqlServerTests` 覆盖同一组身份 / 菜单 / 范围 / 零变更场景，以及被许可的对账单与分摊行走完整生命周期。

## 7. 明确边界（非目标）

- **不新增授权**：不新增菜单 / 角色 / 用户授权，不修改种子数据与既有权限模型，不提供匿名 / 管理员降级。
- **不改变业务语义**：对账单 / 分摊 / 汇总 / 候选 / 金额 / 币种口径与响应 DTO 契约保持不变。
- **不修改数据库结构**：不修改任何存储过程 / 迁移脚本 / `SchemaUpgrader` / 既有表结构，不新增表 / 列 / 索引。
- **不清理既有数据**：不删除 / 不清理任何既有业务行、审计留痕或既有失败日志；不执行生产凭据或生产数据操作。
- 浏览器验收按任务配置为 `browser_acceptance.required = false`：不运行真实 Edge / UI / 截图验收。
