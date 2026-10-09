# 规范销售订单（`api/sales-orders`）普通写入的原子性与确定性锁协议

> 任务：`ERP-421`「Serialize canonical manual sales order draft and status mutations」（阶段 3 核心业务流程完整性）。
> 关联：`ERP-420`（普通写入实时授权，`docs/sales-order-mutation-authority.md`）、
> `ERP-401`（普通表单保存来源血缘护栏，`docs/sales-order-source-lineage.md`）、
> `ERP-347` / `ERP-369`（取消护栏，`docs/sales-order-cancellation.md`、`docs/salesorder-preloading-cancellation.md`）、
> `ERP-397`（单证生成，`docs/trade-document-generation-mutations.md`）、
> `ERP-399` / `ERP-400`（PI / 报价单 → 销售订单转换）、`ERP-381`（统一定金申请锁序）。

## 1. 背景与问题证据（修复前）

规范销售订单控制器 `SalesOrderController`（路由 `api/sales-orders`）的普通写入入口此前**锁口径不一致**：

| 写入动作 | 修复前行为 |
| --- | --- |
| `PUT /{id}`（修改，**已解析实时来源**） | 取「报价单 → PI → 销售订单行」来源行锁 + 原子事务，锁内权威重读 |
| `PUT /{id}`（修改，**手工无来源 / 历史无法解析来源**） | **不取任何订单行锁、不开事务**，直接改写表头与明细 |
| `POST /{id}/submit`、`POST /{id}/approve`（**未链接**订单） | **不取锁、不开事务**，直接流转状态 |
| `POST /{id}/submit`、`POST /{id}/approve`（**已链接**订单） | 只取订单行锁，但**沿用加锁前装载的实体**、不显式复核最新状态 |
| `DELETE /{id}`（软删除） | 继承自 `DocumentControllerBase.Delete`，**不取锁、不开事务、不在锁内复核状态** |
| `POST /{id}/cancel` | 已在可串行化事务内取订单行锁（ERP-347 / -369） |
| `POST /{id}/trade-documents/generate` | 已取来源订单行锁（ERP-397） |

因此「手工订单」与「历史无法解析来源」两条路径以及与 `DocumentControllerBase.Delete` 的软删除，
在**加锁 / 事务之外**读写：与提交 / 审核 / 取消 / 单证生成并发时可能读到过期状态并放行，
或在「删除旧明细 + 插入新明细」失败后残留半成品写入（进程内被跟踪、事务外被部分持久化）。

## 2. 统一协议（`SalesOrderMutationRules`）

新增 `src/ERP.Application/Services/SalesOrderMutationRules.cs`（普通写入的**唯一权威协议**），
`Update` / `Delete` / `Submit` / `Approve` 无论手工 / 历史 / 已解析来源都调用同一套方法：

| 成员 | 作用 |
| --- | --- |
| `BeginMutationTransactionAsync(db, ct)` | 复用既有 `SalesOrderSourceLineageRules.BeginWriteTransactionAsync`：关系型后端开真实事务，**绝不嵌套**（已在外层事务时返回 `null`），非关系型提供程序等价无事务 |
| `ResolveLiveSourceLockScopeAsync(db, customerId, quotationId, piId, ct)` | 从**持久化来源 Id** 权威解析需加锁的**实时来源行**（只读、有界）；只有**确实解析到行**的来源才返回 Id（PI 的报价单祖先行缺失时只锁 PI），缺失 / 已删除 / 异客户按既有口径拒绝 |
| `TryResolveLiveSourceLockScopeAsync(...)` | 同上，但把「来源缺失 / 已删除 / 异客户」收敛为「无可加锁来源」（`None`）—— 用于提交 / 审核 / 软删除：来源合法性交给锁内复核，且绝不因来源失效而阻断**软删除** |
| `DescribeLiveScope(lineage)` | 由权威解析结果抽取「确实解析到行」的来源 Id；未链接 / 无法解析历史值 → `None` |
| `ScopeCovers(scope, quotationId, piId)` | 本次加锁范围是否覆盖锁内**重新解析**出的实时来源（不覆盖 = 来源已过期，必须拒绝） |
| `PersistedSourceUnchanged(preQ, prePi, lockedQ, lockedPi)` | 加锁前读到的持久化来源是否与锁内权威重读一致（任一变化 = 来源已过期） |
| `IsLegalTransition` / `EnsureEditable` / `EnsureDeletable` / `EnsureTransitionAllowed` | 合法流转与「已删除 / 已取消不可复活」的纯判定 |
| `LockOrderText` / `RuleText` / `BoundaryText` | 接口 / 文档同源口径文案 |

## 3. 确定性锁序（与既有模块共用同一把锁）

加锁顺序恒定：**报价单来源行（`db_owner.Quotations`）→ PI 来源行（`db_owner.ProformaInvoices`）
→ 销售订单目标行（`db_owner.SalesOrders`）**，全程仅用 EF Core 基础 API 的「审计时间戳刷新」`UPDATE`
（语义等价 `SELECT ... WITH (UPDLOCK, HOLDLOCK)`，持有至事务结束），**绝不反向获取下游锁**（不存在锁环）。

- 已解析实时来源：按上述顺序取来源行锁 + 订单行锁；
- **手工订单**与**无法解析的历史来源**：不取任何来源行锁，只取订单行锁（不存在的来源行绝不加锁）；
- 与 `ERP-347` / `ERP-369` 取消、`ERP-397` 单证生成、`ERP-399` / `ERP-400` 转换、`ERP-346` 采购归属关联
  共用同一把 `db_owner.SalesOrders` 行锁，因此并发在单一资源上串行化。

新增动作（`Create`）也在**同一原子事务**内写入表头与明细，显式来源时先取来源行锁再写入
（`EnsureCreatedAsync` 语义不变，仅补上手工 / 历史路径的事务边界）。

## 4. 锁内权威重读与过期拒绝

任一普通写入在取得订单行锁**之后**，都要**重新读取**：

1. **实时权限**：`EnsureCanonicalWriteAuthorizedAsync`（实时身份 + 既有「销售订单」菜单 + ERP-097 客户范围）
   与持久化订单归属 `EnsurePersistedOrderAllowedAsync` —— 撤权 / 停用 / 改派在下一次请求立即收敛；
2. **持久化表头 / 明细 / 状态 / 来源**：修改读 `Include(Details)`，删除 / 状态流转读表头；
3. **状态合法性**：`EnsureEditable` / `EnsureDeletable` / `EnsureTransitionAllowed` 在锁内重读到的状态上复核
   —— 并发提交 / 审核 / 取消后过期的编辑 / 删除 / 流转一律拒绝（已取消 / 已删除不可复活）；
4. **来源未过期**：`PersistedSourceUnchanged(preliminary, locked)` 命中的「加锁前来源」若被并发方改写，
   以 `SourceChangedUnderLockText` 原子拒绝；修改还额外用 `ScopeCovers` 确认本次加锁范围覆盖锁内重新解析出的来源
   —— 绝不把来源改写到本次并未加锁的来源行，也绝不对未加锁来源写入。

## 5. 合法状态流转

| 动作 | 允许前置状态 | 结果 |
| --- | --- | --- |
| 修改 `PUT /{id}` | 待提交 | 表头 / 明细整体替换，金额按 `SalesOrderAmountRules` 重算 |
| 删除 `DELETE /{id}` | 待提交 | 软删除（`IsDeleted = true`），绝不物理删除 |
| 提交 `POST /{id}/submit` | 待提交 | 已提交 |
| 审核 `POST /{id}/approve` | 已提交 | 已审核 |

其它组合（含已驳回 / 已完成 / 已取消 / 已删除）一律非法；`EnsureTransitionAllowed` 同时校验「当前 == 期望前置」
与「前置 → 目标」属于合法边，因此**只按锁内最新状态放行**。

## 6. 来源血缘不被静默改写

- 修改未给出任何来源 `Id`：**保留**历史来源（绝不静默清除）；
- 显式改绑到可精确解析来源：完整实时复核（授权 / 转换资格 / 唯一目标）且下游已有证据（变更申请 / 销售出库）时冻结；
- 显式来源全部无法解析：按**显式历史值**原样保留，不构成实时链接，也不占用任何来源锁；
- 提交 / 审核 / 软删除：`EnsurePersistedSourceIntactAsync` 只对**已登记来源**做锁内重查（缺失 / 已删除 fail closed），
  无法解析的历史值原样保留。

## 7. 原子性与回滚

- 整个写入在**一个** `DbContext` 事务内完成；`DbUpdateConcurrencyException` 统一转成
  `ConcurrentMutationText`（「请刷新后重试，原始证据均未改变」）；
- 任一失败（含明细替换保存失败）整体回滚，并调用 `DiscardTrackedChanges` **清空变更跟踪器中的半成品变更**
  （已删除旧明细 + 已新增新明细一并丢弃），绝不残留部分写入 / 孤儿明细；
- 软删除只改 `IsDeleted` 与审计时间戳，绝不物理删除，也绝不动明细 / 金额 / 来源 / 下游记录。

## 8. 验证证据

| 层 | 文件 | 覆盖 |
| --- | --- | --- |
| 单元 | `src/ERP.UnitTests/SalesOrderMutationTests.cs` | 锁序 / 锁语句同源、事务不嵌套、合法流转、加锁范围解析（手工 / 历史 / PI 祖先缺失）、过期判定、手工订单进入协议、过期 / 已取消 / 已删除不可复活、校验失败回滚并清空跟踪器、控制器源码协议契约 |
| 单元 | `src/ERP.UnitTests/SalesOrderSourceLineageTests.cs` | 既有血缘 / 授权 / 转换资格口径（含控制器协议契约更新） |
| 集成 | `src/ERP.IntegrationTests/SalesOrderMutationSqlServerTests.cs` | **两条独立连接**竞态：手工「编辑 vs 提交」「编辑 vs 删除」「提交 vs 删除」、「审核 vs 取消」、关联来源与无法解析历史来源的状态竞态、**强制明细替换保存失败整体回滚**；专用 `(localdb)\NEWERP_AutoAcceptance` + 全新 GUID `NEWERP_AUTOTEST` 库 |
| 集成 | `src/ERP.IntegrationTests/TradeDocumentGenerationMutationSqlServerTests.cs` | 单证生成与普通写入 / 取消共用同一把订单行锁（锁身份契约 + 既有竞态） |
| 集成 | `src/ERP.IntegrationTests/SalesOrderSourceLineageSqlServerTests.cs` | ERP-401 既有来源血缘与转换 / 作废竞态（回归） |

集成测试的安全口径与既有一致：访问数据库前先复核目标必须是专用实例 `(localdb)\NEWERP_AutoAcceptance`、
库名前缀 `NEWERP_AUTOTEST`、`Integrated Security=true`；每次只创建**全新 GUID 后缀库**，发现同名库已存在立即拒绝；
绝不 drop / reset / 复用数据库，也绝不读取 appsettings / `.env` / 生产凭据。**构建完成不等于阶段验收**。

## 9. 边界（不做的事）

- 不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，不新增 HTTP 身份绕过；
- 不把空身份当作匿名或管理员，不伪造任何授权；
- 不改写数量 / 单价 / 金额 / 合计 / 定金 / 币种 / 汇率 / 单位等原始商业语义
  （`SalesOrderAmountRules` 唯一权威）；
- 不改写来源报价单 / PI / 客户主数据 / 库存 / 财务记录，不删除任何历史证据与审计留痕；
- 行锁只刷新来源行的技术审计时间戳 `UpdatedAt`（非商业证据，失败随事务回滚）；
- 不做财务 / 库存过账；取消 / 冲销 / 作废 / 解除链接只走既有显式工作流。

