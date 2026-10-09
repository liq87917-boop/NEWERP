# 供应商付款引用证据授权与客户范围（ERP-433）

本文档说明 ERP-049「付款单 → 采购订单」与 ERP-066「付款单 → 供应商采购发票」两套付款引用（分摊）证据登记模块的**实时授权与服务端客户数据范围**口径。目标：让付款引用台账 / 候选 / 汇总 / 详情 / 登记 / 作废与既有付款单生命周期守卫使用**同一条**身份、菜单与数据范围口径，绝不新增授权模型。

## 1. 复用既有护栏（不新增授权）

- 唯一授权实现仍是 `SupplierPaymentLifecycleRules`（ERP-351/379/380/382），本模块只**复用**，不复制、不新增：
  - `EnsureMenuAuthorizedAsync(db, userId)`：实时启用身份 + 既有付款单（`payment`）菜单授权；
  - `SalespersonDataScopeService.ResolveAsync(db, userId)`（ERP-097 唯一权威数据范围）；
  - `EnsureCustomerInScope` / `ResolvePaymentCustomerAsync` / 付款单行锁 `LockPaymentRowAsync` 等既有判定。
- 新增的只是把上面的既有入口组合成引用证据专用的**解析 + 范围收敛**入口（`ResolveAuthorizedScopeAsync` / `EnsurePaymentInScopeAsync` / `EnsureHistoricalPaymentInScopeAsync` / `ResolveAuthorizedPaymentAsync` / `EnsureInvoiceInScopeAsync`）。
- **不新增**任何菜单 / 角色 / 用户授权，不新增表 / 列 / 实体 / 权限模型，文档与代码均不引入第二套范围口径。

## 2. 每条路由的实时授权

`SupplierPaymentAllocationController`（`/api/supplier-payment-allocations`）与
`SupplierPaymentInvoiceAllocationController`（`/api/supplier-payment-invoice-allocations`）的**每一条路由**
在任何读取 / 写入之前先调用 `SupplierPaymentLifecycleRules.EnsureMenuAuthorizedAsync`，
再由服务层按同一口径复核身份、菜单与客户范围：

| 身份 / 授权 | 既有受控错误 |
| --- | --- |
| 缺失 / 非法（非正整数）身份 | `Unauthorized`（1001） |
| 账号不存在 / 已删除 | `Unauthorized`（1001） |
| 账号已禁用 | `Forbidden`（2002） |
| 无角色 / 无 `payment` 菜单 / 菜单被撤销 | `Forbidden`（2002） |

客户端请求体中的任何字段都不能指定或扩大身份 / 范围（用户 Id 只来自 `ClaimTypes.NameIdentifier`）。
控制器**没有** `[AllowAnonymous]`，也**没有**任何角色白名单或「退化为管理员」的回退。

> 与仓库既有口径一致（`PurchaseInvoiceController` / `PurchaseOrderController` / `SalesOrderController` 的
> `RequiresLiveAuthorization()`）：**真实 HTTP 请求**（MVC 绑定，`Request.Path` 已赋值）一律执行实时授权，
> 包括缺失身份的真实匿名请求（因处于请求管线内一律 fail closed）；仅「既无任何登录身份、又不在 HTTP 请求管线内」
> 的**进程内直接调用**（历史单元测试 / 内部派生读取，不可能由外部请求到达）沿用既有语义，绝不把缺失身份当作管理员。

## 3. 资源解析 + 客户范围收敛

被引用的供应商付款单是**唯一范围锚点**：其权威归属客户由付款单关联的货款申请单（`FinancePaymentApply.CustomerId`）派生，
再由 `SalespersonDataScopeService` 判定是否在当前账号可见范围内；未关联货款申请单（无权威归属）时仅特权账号（系统内置角色 / 显式特权角色）可通过。

- 台账（GetPaged）、付款单候选（payments）：先把查询按权威客户范围过滤（`ApplyCustomerScopeAsync`），**再**计数与明细读取。
- 付款单侧（summary / allocations / order-candidates / invoice-candidates）：先解析并授权该付款单，再做汇总 / 明细 / 候选。
- 发票侧（invoices/{id}/summary 与 /allocations，ERP-066）：先解析发票，受限账号还要求该发票已被
  **范围内付款单**的引用行引用；引用行本身仍按付款单范围过滤，绝不跨范围计数或合计。
- 发票候选按付款单的供应商 + 币种筛选（授权通过后），候选派生金额只来自持久化有效行。

## 4. 统一的不披露错误口径

被引用付款单 / 订单 / 发票**缺失、已删除或越范围**（含越权作废 / 登记时所属付款单已删除）时，
一律返回**同一条**不披露存在性的错误（`NotFound` = 1002）：

```
付款单不存在或已删除，或不在当前账号的数据范围内
```

即「不存在」与「不可见」不区分，错误文案不含范围外付款单 Id、单号、金额或计数；
因此攻击者无法通过错误差异或响应体旁路推断范围外付款单 / 订单 / 发票的存在性与规模。

## 5. 读与写的差异（保持 ERP-049 / ERP-066 既有证据语义）

- **写入（登记 / 作废）**：使用**严格**解析（付款单必须既有且未删除）。越范围 / 已删除 / 不存在的付款单一律拒绝，
  且拒绝发生在写库之前——**绝不落任何引用行、绝不改写任何引用行**。
- **历史证据读取（详情 / 台账）**：使用**历史**解析（`EnsureHistoricalPaymentInScopeAsync`）——
  被引用付款单即使已软删除，历史引用行仍**保留可读**（可用性只作只读标注），但仍受客户范围约束：
  越范围的历史行对受限账号依旧不可见。作废行 / 已删除行保留历史，不作为有效额度占用。
- 本模块**不**改写付款单、采购订单、供应商采购发票、库存、退税、费用与供应商余额，也**不**执行付款、记账、核销或结算；
  付款单金额仍是同一张付款单的**唯一、同币种分摊额度**，由两套证据在付款单行锁下共同占用（跨消费者唯一额度与并发语义不变）。

## 6. 验证

- 单元测试（内存库，`ERP.UnitTests`）：
  - `SupplierPaymentAllocationTests`：缺失 / 非法 / 已删除身份 → `Unauthorized`；禁用 / 撤销菜单 → `Forbidden`；
    越范围 / 不存在付款单同一条不披露错误；台账与候选范围过滤；越范围 / 已删除付款单拒绝登记与作废且证据零变更；被许可的范围内登记 / 作废生命周期。
  - `SupplierPaymentInvoiceAllocationTests`：同上，并额外覆盖发票侧范围收敛（仅被范围内付款单引用的发票对受限账号可见）。
- SQL Server 集成测试（真实 `(localdb)\NEWERP_AutoAcceptance` + GUID 后缀 `NEWERP_AUTOTEST` 库 + `Integrated Security`）：
  `SupplierPaymentLifecycleSqlServerTests` 的「认证控制器」区域，覆盖同一组身份 / 菜单 / 范围 / 零变更场景，
  并保持既有两套证据精确合计、跨消费者唯一额度与并发回归绿。
