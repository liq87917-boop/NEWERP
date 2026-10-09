# 代理服务费协议证据登记册实时授权与客户范围（ERP-439）

本文档说明 ERP-069「代理服务费协议证据」登记模块的**实时授权与服务端客户数据范围**口径。目标：让
台账 / 详情 / 新增 / 修改 / 登记 / 作废等**每一条**证据路由，与既有月度汇总（ERP-110）、对账单 / 收款分摊登记册
（ERP-437）及既有协议语义（ERP-069）使用**同一条**身份、菜单与数据范围口径，绝不新增授权模型。

相关任务：`ERP-439`「Enforce live customer scope and menu authority on agency service fee agreement evidence register」
（阶段 3 收据与财务证据闭环）。
上游 / 同源任务：`ERP-069`（协议证据语义）/ `ERP-070` / `ERP-072`（对账单 / 对账账龄）/ `ERP-097`（业务员客户数据范围）/
`ERP-437`（对账单与收款分摊登记册授权）。

## 1. 复用既有护栏（不新增授权）

唯一授权实现仍是既有的两段组合，本模块只**复用**，不复制、不新增：

- `CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(db, userId)` —— 既有「角色 → 菜单」授权读取；
- `AgencyServiceFeeReconciliationRules.RequiredMenuCode`（`customer`，既有「客户资料」功能菜单）—— 与**月度汇总与
  ERP-437 登记册路由完全同码同源**；
- `SalespersonDataScopeService.ResolveAsync(db, userId)`（ERP-097 唯一权威数据范围，每次请求重新解析、不缓存）。

入口授权直接复用 ERP-437 已抽取的 `AgencyServiceFeeReconciliationRules.EnsureRegisterAuthorizedAsync`；
协议证据的**资源收敛**新增在 `AgencyServiceFeeAgreementRules`
（`AgreementNotFoundText` / `EnsureCustomerInScope` / `EnsureAgreementInScopeAsync`）：

- **不新增**任何菜单 / 角色 / 用户授权，不新增表 / 列 / 实体 / 权限模型；文档与代码均不引入第二套范围口径；
- 绝不提供匿名 / 管理员回退。

## 2. 每条路由的实时授权

`AgencyServiceFeeAgreementController`（`/api/agency-service-fee-agreements`）的**每一条证据路由**
（`GetPaged`、`GetById`、`Create`、`Update`、`record`、`void`）在任何读取 / 写入之前先调用
`AgencyServiceFeeReconciliationRules.EnsureRegisterAuthorizedAsync(_db, CurrentUserId())` 解析实时身份、既有功能菜单与权威客户范围：

| 身份 / 授权 | 既有受控错误 |
| --- | --- |
| 缺失 / 非法（非正整数）身份 | `Unauthorized`（2000） |
| 账号不存在 / 已删除 | `Unauthorized`（2000） |
| 账号已禁用 | `Forbidden`（2002） |
| 无角色 / 无 `customer` 菜单 / 菜单被撤销 | `Forbidden`（2002） |

客户端请求体中的任何字段都不能指定或扩大身份 / 范围（用户 Id 只来自 `ClaimTypes.NameIdentifier`）。
控制器**没有** `[AllowAnonymous]`，也**没有**任何角色白名单或「退化为管理员」的回退。

## 3. 资源解析 + 客户范围收敛

范围锚点是协议证据的**持久化客户** `AgencyServiceFeeAgreement.CustomerId`（服务端在登记时写入客户快照），
再由 `SalespersonDataScopeService` 判定是否在当前账号可见范围内。

- 台账（`GetPaged`）：先把查询按权威客户范围过滤（`SalespersonDataScopeService.FilterByCustomer`），**再**计数与分页读取
  （范围在 `Count` / 分页 / 物化之前下推，受限账号绝不返回范围外协议或其计数）。
- 详情（`GetById`）：按协议持久化客户收敛，越范围 / 已删除 / 不存在一律同一条不披露错误。
- 写入（`Create` / `Update` / `record` / `void`）：先解析并授权被引用协议（`Update` 同时校验**拟提交客户**），
  越范围 / 已删除 / 不存在一律拒绝，且**拒绝发生在写库之前**——绝不落任何协议行、绝不改变任何协议状态。

## 4. 统一的不披露错误口径

协议证据**缺失、已删除或越范围**时，一律返回**同一条**不披露存在性的错误（`NotFound` = 1002）：

```
代理服务费协议证据不存在或已删除，或不在当前账号的数据范围内
```

即「不存在」与「不可见」不区分，错误文案不含范围外协议 Id、协议号、客户、金额或费率；
因此攻击者无法通过错误差异、响应体或计数旁路推断范围外协议的存在性与规模。

## 5. 读与写的差异（保持 ERP-069 既有证据语义）

- **写入（新增 / 修改 / 登记 / 作废）**：使用**严格**解析（协议必须既有且未删除）；越范围 / 已删除 / 不存在一律拒绝，
  且拒绝发生在写库之前——**绝不落任何协议行、绝不改变任何状态或登记字段**。
- **读取（台账 / 详情）**：范围在**计数与物化之前**下推；已作废历史仍保留可读，但越范围证据对受限账号不可见。
- 本模块**不**改写客户主数据（含佣金比例与信用状态）、销售订单（含佣金比例与金额）、装柜与单证、收款单及其引用行、
  销项发票证据、库存与库存成本、费用与退税记录；业务员提成报表保持**独立的模型与口径**。

## 6. 验证

- 单元测试（内存库，`ERP.UnitTests`）：
  - `AgencyServiceFeeAgreementTests`：缺失 / 非法 / 已删除身份 → `Unauthorized`；禁用 / 撤销菜单 / 无菜单 → `Forbidden`；
    越范围 / 已删除 / 不存在协议同一条不披露错误；台账范围下推；越范围客户拒绝新增与修改、越范围协议拒绝修改 / 登记 / 作废
    且证据零变更；被许可的草稿 → 登记 → 作废生命周期；授权撤销后下一次请求立即收敛；六条路由入口授权与范围下推的源码契约。
  - 既有 ERP-069 回归（`AgencyServiceFeeAgreementTests`）与 ERP-070 / ERP-072 回归
    （`AgencyServiceFeeStatementTests` / `AgencyServiceFeeReconciliationTests` / `AgencyServiceFeeMonthlySummaryTests`）
    保持绿。
- SQL Server 集成测试（真实 `(localdb)\NEWERP_AutoAcceptance` + GUID 后缀 `NEWERP_AUTOTEST` 库 + `Integrated Security`）：
  `AgencyServiceFeeAgreementAuthorizationSqlServerTests` 覆盖同一组身份 / 菜单 / 范围 / 零变更场景，以及被许可协议走完整生命周期。

## 7. 明确边界（非目标）

- **不新增授权**：不新增菜单 / 角色 / 用户授权，不修改种子数据与既有权限模型，不提供匿名 / 管理员降级。
- **不改变业务语义**：协议身份 / 客户快照 / 生效区间 / 币种 / 费用条款 / 状态机与响应 DTO 契约保持不变
  （ERP-069 / ERP-070 / ERP-071 / ERP-072 语义不变）。
- **不修改数据库结构**：不修改任何存储过程 / 迁移脚本 / `SchemaUpgrader` / 既有表结构，不新增表 / 列 / 索引。
- **不清理既有数据**：不删除 / 不清理任何既有业务行、审计留痕或既有失败日志；不执行生产凭据或生产数据操作。
- 浏览器验收按任务配置为 `browser_acceptance.required = false`：不运行真实 Edge / UI / 截图验收。
