# 采购订单（`api/purchase-orders`）普通草稿写入的原子性与确定性锁协议（ERP-425）

> 任务：`ERP-425`「Serialize all ordinary purchase order draft mutations with existing status and receipt locks」（阶段 3 核心业务流程完整性）。
> 关联：`ERP-371`（实时授权与数据范围，`docs/purchaseorder-authorization.md`）、
> `ERP-346` / `ERP-347`（显式归属销售订单链接，`docs/purchase-sales-order-link.md`）、
> `ERP-345`（取消护栏，`docs/purchase-order-cancellation.md`）、
> `ERP-418`（比价 → 采购订单转换原子性）、`ERP-382`（唯一全局锁序）。

## 1. 背景与缺陷面（修复前）

| 写入动作 | 修复前行为 |
| --- | --- |
| `POST /`（创建，**显式链接来源**） | 取「来源销售订单行 → 采购订单」行锁 + 可串行化事务，权威解析来源 |
| `POST /`（创建，**手工无归属备货**） | **不加锁、不开事务**（`CreateUnlinkedAsync`） |
| `PUT /{id}`（修改，**显式链接来源**） | 取「来源销售订单行 → 采购订单」行锁 + 可串行化事务，权威解析来源 |
| `PUT /{id}`（修改，**请求 `OwningSalesOrderId == null`**） | **不加锁、不开事务**（`UpdateUnlinkedAsync`）；且**仅凭请求 null** 就选择「无归属」路径 —— 即使持久化单据已链接，也会静默清除来源血缘 |
| `POST /{id}/submit`、`approve`、`DELETE /{id}`、`POST /{id}/cancel` | 已取「归属销售订单行 → 采购订单行」锁 + 可串行化事务（ERP-371） |

因此普通「手工 / 请求无归属」的创建 / 修改在**加锁 / 事务之外**读写：与提交 / 审核 / 删除 / 取消并发时
可能读到过期状态并放行、静默清除来源血缘，或在「删除旧明细 + 插入新明细」失败后残留半成品写入。

## 2. 统一协议（`PurchaseOrderMutationRules`）

新增 `src/ERP.Application/Services/PurchaseOrderMutationRules.cs`（普通草稿写入的**唯一权威协议**），
`Create` 与 `Update` **无论手工 / 已链接**都调用同一套方法：

| 成员 | 作用 |
| --- | --- |
| `BeginMutationTransactionAsync(db, ct)` | 复用既有 `TradeDocumentMutationRules.BeginMutationTransactionAsync`：关系型后端开真实事务，调用方已开启事务时**绝不嵌套**（返回 `null` 复用外层事务），非关系型提供程序等价无事务 |
| `MergeSalesOrderLockIds(params long?[])` | 持久化归属 ∪ 请求拟议归属 → 去重 + 仅正整数 + **Id 升序**（确定性锁序，绝无锁环） |
| `ReadPersistedSalesOrderPointerAsync(db, id, ct)` | **加锁前发现**：只读读取持久化归属来源指针（仅用于确定锁序；锁内必须以受跟踪重读的权威值再核对） |
| `NormalizeId(id)` / `PersistedSourceUnchanged(discovered, locked)` | 来源 Id 归一化与「发现指针 vs 锁内重读」一致性判定 |
| `EnsureEditable(status)` | **仅待提交**可修改；已提交 / 已审核 / 已驳回 / 已完成 / 已取消一律拒绝 |
| `PurchaseOrderRowLockSql` / `SalesOrderRowLockSql` | 目标采购订单行锁与归属销售订单行锁（与状态流转 / 取消 / 链接共用同一常量，锁身份同源） |
| `LockOrderText` / `RuleText` / `BoundaryText` / `SourceChangedUnderLockText` | 接口 / 文档同源口径文案 |

## 3. 确定性锁序（与既有模块共用同一把锁）

加锁顺序恒定：**全部必要来源销售订单行（`db_owner.SalesOrders`，去重 + 仅正整数 + **Id 升序**）→
本采购订单行（`db_owner.PurchaseOrders`）**，行锁语句为
`SELECT Id FROM <table> WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}`，持有至事务结束，**绝不反向获取下游锁**。

- `Create`（无持久化来源）：只对**请求拟议**来源加锁（不存在来源行绝不加锁）；
- `Update`：对**持久化来源 ∪ 请求拟议来源**的并集加锁 —— **绝不仅凭请求 null 选择安全路径**；
- 与 `ERP-371` 提交 / 审核 / 删除 / 取消、`ERP-346` 链接、销售订单取消共用同一把 `db_owner.SalesOrders` 行锁，
  因此并发在单一资源上串行化，**不存在锁环**。

## 4. 锁内权威重读与过期拒绝

`Update` 在取得锁**之后**重新读取：

1. **持久化表头 / 明细 / 状态**：`Include(Details)` 受跟踪重读；`EnsureEditable(锁内状态)` —— 并发提交 / 审核 / 删除 / 取消
   之后过期的编辑一律原子拒绝，**绝不复活**任何终止态；
2. **来源未过期**：`PersistedSourceUnchanged(发现指针, 锁内指针)` 不成立时以 `SourceChangedUnderLockText` 原子拒绝
   —— 绝不把来源改写到本次并未加锁的来源行；
3. **实时权限**：`EnsureOrderAuthorizedAsync`（已存归属）与 `EnsureProposedAuthorizedAsync`（请求归属）在锁内复核，
   撤权 / 停用 / 改派在下一次请求立即收敛。

状态流转路径（`Submit` / `Approve` / `Delete` / `Cancel`）继续使用可串行化事务 + `AcquireOrderStateLocksAsync`：
其**加锁前发现**以 `READUNCOMMITTED` 无锁只读读取来源指针（可串行化事务内的普通 SELECT 会保留共享锁，
必须显式避免），加锁后再锁内重读核对。

## 5. 来源血缘不被静默清除

- 请求**未给出**归属销售订单 Id（`null`）：**保留**已存来源，并按权威来源重新解析（`ApplyLinkAsync`）与校验实时已审核来源；
- 请求**显式给出非法** Id（`0` / 负）：直接拒绝（`InvalidParameter`），绝不静默清除血缘；
- 请求**显式改绑**到另一来源：按权威来源重新解析并派生归属客户 / 客户名称 / 归属销售订单号快照；
- 创建时显式给出非法 Id 同样直接拒绝（不消耗单据号、不落库）。

## 6. 原子性与回滚

- 创建 / 修改表头与明细、**单据号预约**与归属赋值在**一个** `DbContext` 事务内完成；
- 任一失败（含明细替换保存失败、数据库约束拒绝）整体回滚，并调用 `DiscardTrackedChanges`
  **清空变更跟踪器中的半成品变更**（「已删除旧明细 + 已新增新明细」一并丢弃），绝不残留部分写入 / 孤儿明细；
- 行锁只刷新行锁语句命中的行（`UPDLOCK, HOLDLOCK` 的 SELECT 不写任何字段），
  **绝不改写来源销售订单的商业字段或其审计时间戳**。

## 7. 验证证据

| 层 | 文件 | 覆盖 |
| --- | --- | --- |
| 单元 | `src/ERP.UnitTests/PurchaseOrderMutationTests.cs` | 锁序 / 锁语句同源、来源并集去重升序、归一化、仅待提交可修改、指针一致性、事务协议（非关系型等价无事务）、手工创建 / 改单、提交 / 删除 / 取消后过期编辑不复活、明细整体替换完整性、已链接单据「请求未给出来源」保留血缘、显式改绑权威快照、显式非法 Id 拒绝、失败回滚并清空跟踪器、控制器源码接线契约 |
| 集成 | `src/ERP.IntegrationTests/PurchaseOrderMutationSqlServerTests.cs` | **两条独立连接**竞态：手工「改单 vs 提交」「改单 vs 删除」「改单 vs 取消」；已链接「请求未给出来源」保留血缘且原始来源审计不变；「来源取消（持来源行锁）vs 采购改单」受控输家与血缘保留；显式改绑权威快照更新；**强制明细替换保存失败整体回滚**；合法普通生命周期（链接创建 → 改单 → 提交 → 审核）；专用 `(localdb)\NEWERP_AutoAcceptance` + 全新 GUID `NEWERP_AUTOTEST` 库 |
| 回归 | `src/ERP.UnitTests/PurchaseOrderAuthorizationTests.cs`、`OrderTraceabilityTests.cs`、`PurchaseSalesOrderLinkTests.cs`、`PurchaseOrderCancellationTests.cs` 等 | 既有授权 / 链接 / 追溯 / 取消口径保持通过（既有特权 / 合法身份照常放行，历史直接实例化调用按既有进程内调用口径免 HTTP 授权） |

集成测试安全口径：访问数据库前先复核目标必须是专用实例 `(localdb)\NEWERP_AutoAcceptance`、
库名前缀 `NEWERP_AUTOTEST`、`Integrated Security=true`；每次只创建**全新 GUID 后缀库**，发现同名库已存在立即拒绝；
绝不 drop / reset / 复用数据库，也绝不读取 appsettings / `.env` / 生产凭据。使用既有种子管理员身份
（`SeedData` 已授予全部菜单），不新增任何用户授权、不使用 HTTP / 测试身份绕过。**构建完成不等于阶段验收**。

## 8. 边界（不做的事）

- 不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，不新增 HTTP 身份绕过；
- 不把空身份当作匿名或管理员，不伪造任何授权；
- 不改写供应商 / 币种 / 汇率 / 单位 / 单价 / 数量 / 金额等原始商业语义；
- 不改写归属销售订单 / 客户 / 商品主数据，不改写来源销售订单审计，不做库存 / 财务过账；
- 不删除任何历史单据与审计证据；取消 / 冲销 / 作废只走既有显式工作流。
