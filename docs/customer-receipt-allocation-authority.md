# 客户收款引用证据授权与客户范围（ERP-434）

本文档说明 ERP-053「收款单 → 销售订单」与 ERP-073「收款单 → 客户销项发票证据」两套收款引用（分摊）证据登记模块的**实时授权与服务端客户数据范围**口径。目标：让收款引用台账 / 候选 / 汇总 / 详情 / 登记 / 作废与既有收款单生命周期守卫（ERP-349 / ERP-378）使用**同一条**身份、菜单与数据范围口径，绝不新增授权模型。

相关任务：`ERP-434`「Enforce live receipt permission and scope on customer receipt allocation evidence registers」（阶段 3 收款闭环）。
上游/同源任务：`ERP-349` / `ERP-350` / `ERP-378`（收款单生命周期与同单串行化）、`ERP-383`（唯一全局锁序）、`ERP-053`（收款单 → 销售订单证据）、`ERP-073`（收款单 → 客户销项发票证据）、`ERP-075`（发票侧证据暴露）。

## 1. 复用既有护栏（不新增授权）

唯一授权实现仍是 `CustomerReceiptLifecycleRules`（ERP-349 / ERP-378），本模块只**复用**，不复制、不新增：

- `EnsureMenuAuthorizedAsync(db, userId)`：实时启用身份 + 既有收款单（`receipt`）菜单授权；
- `SalespersonDataScopeService.ResolveAsync(db, userId)`（ERP-097 唯一权威数据范围）；
- 客户数据范围硬边界 `EnsureCustomerInScope`；
- 收款单行锁 `LockReceiptRowAsync`（ERP-378：先收款单行、后分摊行）。

ERP-434 新增的只是把上面的既有入口组合成引用证据专用的**解析 + 范围收敛**入口（`ResolveAuthorizedScopeAsync` / `EnsureReceiptInScopeAsync` / `ResolveAuthorizedReceiptAsync` / `EnsureHistoricalReceiptInScopeAsync` / `EnsureInvoiceInScopeAsync` / `EnsureHistoricalInvoiceInScopeAsync`）与统一的不披露文案 `AllocationNotFoundText`。

- **不新增**任何菜单 / 角色 / 用户授权，不新增表 / 列 / 实体 / 权限模型，文档与代码均不引入第二套范围口径；
- **不改变** ERP-053 / ERP-073 / ERP-075 的收款引用证据语义（服务端快照、两套证据共同占用同一张收款单的唯一同币种额度、作废保留历史、锁序与串行化口径全部不变）。

## 2. 每条路由的实时授权

`CustomerReceiptAllocationController`（`/api/customer-receipt-allocations`）与
`CustomerSalesInvoiceCollectionAllocationController`（`/api/customer-sales-invoice-collection-allocations`）的**每一条路由**
在任何读取 / 写入之前先调用 `CustomerReceiptLifecycleRules.EnsureMenuAuthorizedAsync`，
再由服务层按同一口径复核身份、菜单与客户范围：

| 身份 / 授权 | 既有受控错误 |
| --- | --- |
| 缺失 / 非法（非正整数）身份 | `Unauthorized`（1001） |
| 账号不存在 / 已删除 | `Unauthorized`（1001） |
| 账号已禁用 | `Forbidden`（2002） |
| 无角色 / 无 `receipt` 菜单 / 菜单被撤销 | `Forbidden`（2002） |

客户端请求体中的任何字段都不能指定或扩大身份 / 范围（用户 Id 只来自 `ClaimTypes.NameIdentifier`）。
控制器**没有** `[AllowAnonymous]`，也**没有**任何角色白名单或「退化为管理员」的回退。

> 与仓库既有口径一致（`PurchaseInvoiceController` / `PurchaseOrderController` / `SupplierPaymentAllocationController` 的
> `RequiresLiveAuthorization()`）：**真实 HTTP 请求**（MVC 绑定，`Request.Path` 已赋值）一律执行实时授权，
> 包括缺失身份的真实匿名请求（因处于请求管线内一律 fail closed）；仅「既无任何登录身份、又不在 HTTP 请求管线内」
> 的**进程内直接调用**（历史单元测试 / 内部派生读取，不可能由外部请求到达）沿用既有语义，绝不把缺失身份当作管理员。

## 3. 资源解析 + 客户范围收敛

被引用的**客户收款单是唯一范围锚点**（`FinanceReceipt.CustomerId`；发票证据按 `CustomerSalesInvoiceEvidence.CustomerId` 收敛），
再由 `SalespersonDataScopeService` 判定是否在当前账号可见范围内：

- 台账（GetPaged）：先把查询按权威客户范围过滤（`SalespersonDataScopeService.FilterByCustomer`，按分摊行 `CustomerId`），**再**计数与明细读取；已作废 / 已删除收款单的历史行仍保留可读，但越范围行对受限账号不可见。
- 收款单侧（summary / allocations / order-candidates）：先解析并授权该收款单，再做汇总 / 明细 / 候选。
- 发票侧（invoices/{id}/summary 与 /allocations，ERP-073）：先解析并授权该发票证据，再读取。
- 候选（receipts / invoices）：显式给出的客户必须落在当前账号权威范围内，否则按同一条不披露错误拒绝；候选派生金额只来自持久化有效行。
- 明细（GetById）：按**历史**口径收敛被引用收款单与发票证据（软删除后历史仍可读、可用性只作只读标注），但仍受客户范围约束。

## 4. 统一的不披露错误口径

被引用收款单 / 销售订单 / 发票证据**缺失、已删除或越范围**（含越权作废 / 登记时所属收款单已删除）时，
一律返回**同一条**不披露存在性的错误（`NotFound` = 1002）：

```
收款单不存在或已删除，或不在当前账号的数据范围内
```

即「不存在」与「不可见」不区分，错误文案不含范围外资源 Id、单号、金额或计数；
因此攻击者无法通过错误差异或响应体旁路推断范围外收款单 / 订单 / 发票的存在性与规模。

## 5. 读与写的差异（保持 ERP-053 / ERP-073 既有证据语义）

- **写入（登记 / 作废）**：使用**严格**解析（收款单必须既有且未删除）。越范围 / 已删除 / 不存在的收款单一律拒绝，
  且拒绝发生在写库之前——**绝不落任何引用行、绝不改写任何引用行**。
- **历史证据读取（详情 / 台账）**：使用**历史**解析（`EnsureHistoricalReceiptInScopeAsync` /
  `EnsureHistoricalInvoiceInScopeAsync`）——被引用收款单 / 发票证据即使已软删除，历史引用行仍**保留可读**
  （可用性只作只读标注），但仍受客户范围约束：越范围的历史行对受限账号依旧不可见。
  作废行 / 已删除行保留历史，不作为有效额度占用。
- 本模块**不**改写收款单、销售订单、发票证据、客户、库存、退税、费用与信用状态，也**不**执行收款、记账、核销或结算；
  收款单金额仍是同一张收款单的**唯一、同币种分摊额度**，由两套证据在收款单行锁下共同占用（跨消费者唯一额度与并发语义不变）。

## 6. 验证

- 单元测试（内存库，`ERP.UnitTests`）：
  - `CustomerReceiptAllocationTests`：缺失 / 非法 / 已删除身份 → `Unauthorized`；禁用 / 撤销菜单 / 无菜单 → `Forbidden`；
    越范围 / 已删除 / 不存在收款单同一条不披露错误；台账与候选范围过滤；越范围 / 已删除收款单拒绝登记与作废且证据零变更；
    被许可的范围内登记 / 作废生命周期。
  - `CustomerSalesInvoiceCollectionAllocationTests`：同上，并额外覆盖发票侧范围收敛（越范围 / 已删除发票证据同一条不披露错误）
    与 `metadata` 路由的实时授权。
  - 既有 ERP-053 / ERP-073 / ERP-075 回归（`SalesOrderReceiptEvidenceTests` / `CustomerReceiptSharedFundingTests` /
    `CustomerSalesInvoiceReceiptAllocationExposureTests` / `CustomerSalesInvoiceConcurrencyTests` /
    `ReceiptLifecycleConcurrencyTests` / `CustomerReceiptLifecycleTests` / `CustomerReceiptFundingEvidenceTests`）保持绿。
- SQL Server 集成测试（真实 `(localdb)\NEWERP_AutoAcceptance` + GUID 后缀 `NEWERP_AUTOTEST` 库 + `Integrated Security`）：
  `CustomerReceiptLifecycleSqlServerTests` 的「认证控制器」区域，覆盖同一组身份 / 菜单 / 范围 / 零变更场景，
  并保持既有两套证据精确合计、跨消费者唯一额度与并发回归绿。
