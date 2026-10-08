# 销售订单变更申请「登记 / 编辑 / 提交 / 取消」原子性与确定性行锁说明（ERP-415）

- 任务：`ERP-415`「Serialize sales order proposal draft edits submission and cancellation」（阶段 3 核心业务流程完整性）。
- 关联：`ERP-047`（销售订单变更申请登记，`docs/销售订单变更申请登记说明.md`）、
  `ERP-414`（变更申请实时授权与来源实时归属，`docs/sales-order-change-request-authority.md`）、
  `ERP-413` / `ERP-347` / `ERP-343`（规范销售订单行锁协议）。
- 权威实现：
  - `src/ERP.Application/Services/SalesOrderChangeRequestMutationRules.cs`（**唯一权威口径**：原子事务帮助方法 + 锁语句常量 +
    锁序文案 + 锁内实时授权解析 + 受控写入映射）。
  - `src/ERP.Application/Services/SalesOrderChangeRequestService.cs`（`CreateDraftAsync` / `UpdateDraftAsync` / `SubmitAsync` /
    `CancelAsync` 全部在锁内重读实时授权与当前状态，先校验后写入，失败整体回滚）。
  - `src/ERP.Api/Controllers/SalesOrderChangeRequestController.cs`（在**同一个可串行化事务**内按确定性锁序执行既有规范
    来源行锁 + 申请行锁，再把真实身份交给服务层供锁内实时重解析；不做任何 DDL）。
  - `src/ERP.Application/Services/SalesOrderChangeRequestRules.cs`（新增提交前拟议快照完整性校验）。
  - 复用（**不修改**）：`SalesOrderChangeRequestAuthorizationRules`（ERP-414 三重护栏）、`SalesOrderAmountRules`（销售订单唯一
    权威金额算法）、`SalesPersonDataScopeService`（ERP-097 唯一权威数据范围）、`PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql`
    （既有规范销售订单行锁常量）、`ErpDbContext.Config` 的审计字段口径。

## 1. 背景与问题证据（修复前）

`SalesOrderChangeRequestService` 的登记 / 编辑 / 提交 / 取消此前**无事务、无行锁**：

| 路由 | 修复前行为 |
| --- | --- |
| 登记 `POST` | 先单独读取来源订单，再另行冻结快照 / 生成申请单号 / 保存 —— 来源订单在读取与落库之间被并发取消 / 改派 / 删除会产生**撕裂快照** |
| 编辑 `PUT {id}` | 先读状态再替换拟议明细，无锁 —— 与并发提交竞争时可能把「已提交」申请改回草稿状态或写入不一致的明细 |
| 提交 `POST {id}/submit` | 先读状态再翻转状态，无锁 —— 并发编辑 / 提交可能冻结**不完整或不一致**的拟议快照 |
| 取消 `POST {id}/cancel` | 先读状态再写原因 / 时间，无锁 —— 并发取消会**用后来的原因覆盖先提交的原因与时间** |

因此并发登记 / 编辑 / 提交 / 取消会出现：不完整冻结快照、丢失更新、覆盖原始取消原因、来源快照与来源订单不一致。

## 2. 原子性协议

| 操作 | 事务 / 锁 | 锁内复核 | 失败语义 |
| --- | --- | --- | --- |
| 登记 `POST /api/sales-order-change-requests` | 可串行化事务内先对**来源销售订单行**加锁（复用既有规范行锁，`UPDLOCK, HOLDLOCK` 语义） | 锁内重新解析实时身份 / 既有「销售订单」菜单 / ERP-097 客户范围 + 重读**权威来源主表 + 明细**（存在 / 未删除 / 未作废），**之后**才生成申请单号、冻结来源快照与拟议快照并写入 | 任何失败整体回滚（零部分写入，且绝不消耗申请单号） |
| 编辑 `PUT {id}` | 可串行化事务内按 **来源销售订单行 → 变更申请行** 顺序加锁 | 锁内重读申请（tracked，含明细）+ 实时重解析身份 / 权限 + 重读当前状态（仅草稿可编辑）+ 复核持久化 / 显式拟议来源归属，**之后**才整体替换拟议明细并按权威算法重算金额 | 并发提交 / 取消的**陈旧输家**被状态门拒绝（`RuleConflict`），绝不覆盖赢家冻结的拟议快照；失败整体回滚 |
| 提交 `POST {id}/submit` | 同上 | 锁内重读申请 + 实时重解析身份 / 权限 + 重读当前状态（仅草稿可提交）+ **完整正数快照校验**（`EnsureProposedSnapshotComplete`）+ 按 `SalesOrderAmountRules` 复核逐行金额 / 总额 / 定金及精度，**之后**才冻结拟议快照并记录提交时间 | 重复提交 / 已取消 / 并发输家一律拒绝；失败整体回滚 |
| 取消 `POST {id}/cancel` | 同上 | 锁内重读申请 + 实时重解析身份 / 权限 + 重读当前状态（草稿与已提交可取消，已取消拒绝） | 并发取消只有一个赢家；**输家绝不覆盖赢家已保留的原始取消原因与时间戳**；失败整体回滚 |

- **锁序恒定**：「**来源销售订单行 → 变更申请行**」，跨模块统一、绝不反向获取（因此不存在锁环）。
- **登记只锁来源订单行**（申请行尚不存在）；编辑 / 提交 / 取消先锁来源订单行、再锁申请行，二者都持有至事务结束。
- **受控写入**：任何写入失败（明细替换 / 快照写入 / 并发令牌过期）由 `SaveProposalAsync` 把 `DbUpdateException` 映射为
  **稳定的业务冲突**（`WriteConflictText`），随后调用方在同一事务上**完整回滚**（`TryRollbackAsync`：先清空变更跟踪器，再回滚事务）
  → **零部分写入**；**绝不**把 `SqlException` / SQL 错误码 / 索引名 / 连接串暴露给调用方。

## 3. 锁语义与「不为加锁而改写来源」

- 来源销售订单行锁**直接复用既有规范销售订单行锁协议**（`SalesOrderChangeRequestMutationRules.SalesOrderRowLockSql`
  等同 `PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql`），与销售订单取消 / 出库审核 / 预装柜流程 / 单证生成
  **共用同一把来源行锁**；执行方式为 `SELECT Id FROM db_owner.SalesOrders WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}`，
  持有至事务结束。
- **加锁不改变来源订单的任何字段（含 `UpdatedAt`）**：来源快照 `SourceUpdatedAt` 始终是登记当时的**真实**值，
  绝不为加锁而改写来源的状态 / 金额 / 数量 / 客户 / 明细。因此「来源是否已变化」检测不会被加锁自身污染。
- 变更申请行锁口径一致：`SELECT Id FROM db_owner.SalesOrderChangeRequests WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}`。
- **非关系型**（内存库等）无行锁 / 事务语义：锁定与事务等价无操作，声明式校验与状态判定口径不变（单元测试口径）；
  真实 SQL Server 由控制器在同一可串行化事务内执行锁提示。

## 4. 生命周期与冻结语义

- **登记**捕获**权威来源主表 + 明细**为不可变快照（`Source*` 列），并生成拟议快照基线（默认与来源一致）；来源快照列在后续
  编辑中**永不被覆盖**，`SalesOrderId`（来源身份）也不可更改（改挂来源被拒绝，需取消后重新登记）。
- **编辑**为「整体替换拟议明细」：来源行（`HasSourceLine = true`）永远保留且快照列不变，旧的拟议新增行按软删除留痕，
  新的拟议新增行作为新行插入；金额一律由 `SalesOrderAmountRules` 重算（同一权威算法，不接受客户端合计）。
- **提交**冻结**完整且为正**的拟议快照：至少保留一行有效明细、逐行数量 > 0、单价非负，金额按权威算法与 EF 精度重算后落库；
  提交后不可编辑（本模块不提供「重新提交」），已提交申请仍可取消。
- **取消**保留原始与拟议证据（不做硬删除），必须填写原因；并发取消只有一个赢家，输家绝不覆盖原始原因与时间戳。
- **来源变化只提示、不刷新**：读取侧显式给出「来源在快照之后已变化」提示，**绝不**覆盖 / 合并 / 静默刷新历史拟议值。

## 5. 边界（非目标）

- **不新增审批 / 套用语义**：状态仍只有「草稿 / 已提交 / 已取消」，没有审批阈值、审批人、生效时间，也没有「已批准 / 已套用」
  状态与硬删除；本任务不添加 approve / apply 动作，也绝不自动套用变更。
- **不改写来源与下游**：登记 / 编辑 / 提交 / 取消都只写 `SalesOrderChangeRequests` / `SalesOrderChangeRequestDetails` 两张表，
  不改写来源销售订单主表 / 明细，也不生成库存移动、出库、装柜与出运、收款与发票、佣金、单证中心与财务记录。
- **不修改结构 / 权限**：不新增表 / 列 / 索引（建表 / 索引由 `SchemaUpgrader` 既有幂等段补齐，本任务未修改 SchemaUpgrader），
  不新增菜单 / 角色 / 用户授权，不把空身份当作匿名或管理员。
- **不清理既有数据**：不删除任何既有业务行、库存来源单据审计或既有失败日志；不读取 `appsettings*.json` / `.env` / 生产凭据。

## 6. 测试证据

| 层 | 文件 | 覆盖 |
| --- | --- | --- |
| 单元（内存库，新增 18 例） | `src/ERP.UnitTests/SalesOrderChangeRequestMutationTests.cs` | 行锁契约（复用规范来源行锁、无 DDL、受控写入文案不含错误码 / 驱动名）；接线契约（控制器四条生命周期写入都落原子事务，锁序来源行 → 申请行，服务锁内状态门）；非关系型等价无操作；登记锁内冻结来源快照且不改写来源 `UpdatedAt`；编辑整体替换但来源快照列与来源订单不变；并发提交后的陈旧编辑被状态门拒绝且不覆盖冻结快照；提交冻结完整正数快照并按权威算法重算；拟议快照为空 / 数量非正拒绝；并发取消保留首次原因 / 时间；写入失败受控业务冲突且零部分写入；锁内实时权限重读（提交前菜单撤销 fail closed） |
| 单元（既有回归） | `SalesOrderChangeRequestTests.cs`（32 例） / `SalesOrderChangeRequestAuthorizationTests.cs`（11 例） | ERP-047 登记口径、ERP-414 实时授权与来源归属全部保留（`safe` profile 全量 `ERP.UnitTests`） |
| SQL Server 集成（受控 localdb，新增 11 例） | `src/ERP.IntegrationTests/SalesOrderChangeRequestMutationSqlServerTests.cs` | **两个独立连接竞态**：① 编辑 vs 提交 → 最终一致提交、陈旧输家受控冲突；② 并发取消 → 恰好一个赢家、保留原始原因与时间；③ 登记 vs 来源取消 → 快照一致或 fail closed 零写入；④ 登记 vs 来源改派 → 受限账号 fail closed 或一致登记；⑤ 提交 vs 实时权限撤销 → fail closed 或提交先行；登记不改写来源 `UpdatedAt` / 状态 / 总额并零下游变更；强制明细写入失败 → 受控业务冲突 + 完整回滚；专用目标护栏 fail-closed |
| 安全 profile | `.ai/config.json` 的 `safe` | `dotnet restore` → Release 构建（`TreatWarningsAsErrors`）→ `ERP.UnitTests` 全量 |

### 6.1 一致性不变量

- **来源快照不撕裂**：`SourceStatus` / `SourceUpdatedAt` / `SourceTotalAmount` / 明细签名恒为登记当时的权威值；
  加锁不改写来源任何字段。
- **冻结快照完整且自洽**：提交后逐行 `ProposedAmount = ProposedQuantity × ProposedUnitPrice`，总额 = 有效行金额之和，
  定金 = 总额 × 定金比例%（0~100），与 `SalesOrderAmountRules` 完全一致。
- **取消证据不可替换**：并发取消恰好一个赢家，`CancelledReason` / `CancelledAt` 保留赢家的原始值。
- **零部分写入**：任何被拒绝 / 失败的登记 / 编辑 / 提交 / 取消都不留下半成品申请或明细行，来源订单与库存 / 财务不变。

### 6.2 SQL Server 集成目标护栏

- 实例必须精确为 `(localdb)\NEWERP_AutoAcceptance`，库名前缀必须为 `NEWERP_AUTOTEST`，且必须为 `Integrated Security`；
  不满足在任何库访问之前直接拒绝（`AssertDedicatedTarget`）。
- 每次运行只创建一个**全新 GUID 后缀库**（`NEWERP_AUTOTEST_SOCREQMUT_<guid>`），发现同名库已存在立即拒绝；
  **绝不 drop / reset / 复用**任何数据库，也绝不读取 `appsettings*.json` / `.env` / 生产凭据。

### 6.3 实测证据

- Release 构建（`NEWERP.sln`，含 `TreatWarningsAsErrors` 的 `ERP.IntegrationTests`）：**0 警告 / 0 错误**。
- `ERP.UnitTests`（本机实测 `dotnet test src/ERP.UnitTests -c Release`）：新增 `SalesOrderChangeRequestMutationTests` 18 例全绿，
  既有 `SalesOrderChangeRequestTests`（32 例）与 `SalesOrderChangeRequestAuthorizationTests`（11 例）全部通过。
- `ERP.IntegrationTests` 新增 `SalesOrderChangeRequestMutationSqlServerTests` 在受控 `(localdb)\NEWERP_AutoAcceptance` 上
  真实执行通过（7 例场景 + 4 例专用目标护栏 = 11 例）。
- **构建完成不等于阶段验收**：真实 SQL 用例只有在受控 localdb 上真实执行通过才构成验收证据；浏览器验收按任务配置为
  `browser_acceptance.required = false`：不运行真实 Edge / UI / 截图验收。

