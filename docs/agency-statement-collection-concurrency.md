# 代理服务费对账单「共享分摊额度 + 作废 ↔ 收款分摊证据」并发序列化（ERP-521）

## 1. 目标

关闭**对账单侧**的资金容量与状态竞态（ERP-070 / ERP-071 / ERP-520 之后的最后一环）：

1. `AgencyServiceFeeCollectionAllocationService.CreateAsync` 之前只对**收款单行**加锁，再读对账单合计与既有分摊行；
   两张**不同收款单**同时分摊**同一张对账单**时，彼此不阻塞 —— 两把不同的收款单行锁无法串行化**共享的
   StatementId 未分摊额度**，可能超分摊（合计超过对账单合计）或同时通过「重复有效对」与「行数上限」判定。
2. `AgencyServiceFeeStatementService.VoidAsync` 之前不与分摊登记协调 —— 可以在仍有**有效**收款分摊证据指向
   该对账单时把它作废，形成「已作废对账单 + 有效分摊证据」的不一致证据集合。

本任务复用**既有**共享护栏（`AgencyFeeEvidenceConcurrencyRules`，ERP-519 / ERP-520 同一类）把「对账单未分摊额度」
收敛到**同一把对账单证据行锁**上：登记 / 作废分摊行都先取该锁（再取收款单行锁），对账单作废在同一把锁内
拒绝「仍存在有效分摊证据」的情形。**不**新增表 / 列 / 索引 / 迁移 / 权限 / 菜单 / 角色 / 授权，**不**推断费用、
**不**开票、**不**记账 / 收款 / 结算。

## 2. 唯一确定性锁序（ERP-519 → ERP-520 → ERP-521，绝不反向获取）

| 段 | 行 | 说明 |
|---|---|---|
| ① | 代理服务费协议证据行（`AgencyServiceFeeAgreements`，同一 Id） | ERP-519 |
| ② | 代理服务费对账单证据行（`AgencyServiceFeeStatements`，同一 Id） | ERP-520 / ERP-521：**共享对账单未分摊额度** |
| ③ | 客户收款单行（`FinanceReceipts`，同一 Id） | ERP-378 / ERP-521：**共享收款单唯一同币种分摊额度** |
| ④ | 收款分摊证据行（`AgencyServiceFeeCollectionAllocations`） | ERP-071 / ERP-521 |

- 登记分摊（`CreateAsync`）与作废分摊（`VoidAsync`）都按 **② → ③ → ④** 固定顺序加锁，**绝不反向获取**；
- 对账单作废（`VoidAsync`）**只取 ②**，在锁内**只读**该对账单的有效分摊证据计数，**不取收款单行**，
  因此不引入「A 持对账单等收款单、B 持收款单等对账单」的反向锁序；收款单生命周期 / ERP-053 分摊写入都只取 ③
  且不取 ②，所以也不存在反向加锁与死锁环；
- 锁序文案与 T-SQL 契约同源：`AgencyFeeEvidenceConcurrencyRules.GlobalLockOrderText` /
  `CollectionAllocationLockOrderText` / `StatementRowLockSql` / `ReceiptRowLockSql`。

## 3. 各写路由口径

| 路由 | 事务 + 锁 | 锁内重读的权威依据 | 写入 |
|---|---|---|---|
| 登记分摊（`AgencyServiceFeeCollectionAllocationService.CreateAsync`） | 同一原子事务（`BeginMutationTransactionAsync`）+ ② 对账单行锁 → ③ 收款单行锁 | 对账单实体（未删除 / **已登记**）与合计、收款单实体（未删除 / 未取消）与金额、**同一对账单**有效分摊合计、**同一收款单**两套证据合计（ERP-350）、两侧行数上限、重复有效对、客户 / 币种兼容 | 一行有效分摊证据（服务端快照 + 登记人 / 时间戳）；任一步失败整体回滚并丢弃半成品变更（**零部分写入**） |
| 作废分摊（`VoidAsync`） | 同一事务 + ② → ③ → ④（④ 为受跟踪行） | 轻量投影先取 StatementId / ReceiptId（非权威），取锁后重读分摊行做权威作废判定（有效 → 已作废） | 只改状态 / 作废原因 / 时间戳；原始金额、双方快照、登记人与时间戳**全部保留** |
| 对账单作废（`AgencyServiceFeeStatementService.VoidAsync`） | 同一事务 + ② | 权威状态（未作废）、权威客户范围、**该对账单有效收款分摊证据计数必须为 0**（`EnsureNoActiveCollectionAllocationsAsync`） | 表头与全部行的作废状态 / 原因 / 时间戳；拒绝发生在**任何状态写入之前** |

- 拒绝清理：`TryRollbackAsync` 在关系型后端先清空变更跟踪器中的半成品实体再回滚事务（与 ERP-412 / ERP-415 /
  ERP-519 / ERP-520 同口径）——被拒请求零部分行 / 零部分状态 / 零审计漂移；
- 嵌套事务：`BeginMutationTransactionAsync` 在调用方已开启事务时**绝不嵌套**（返回 `null`，由最外层提交 / 回滚），
  `CommitAsync(null)` 为空操作；内存库等非关系型提供程序返回 `null`（等价无事务、无行锁），既有内存语义不变；
- 加锁只刷新既有**技术审计字段** `UpdatedAt`（不构成对商业证据的静默改写），被拒请求的加锁时间戳随事务回滚撤销。

## 4. 明确不改写

- 不新增 / 不修改表、列、索引、schema、迁移或种子数据；不新增菜单 / 角色 / 用户授权；不做匿名 / 管理员回退；
- 不改分摊状态机（有效 → 已作废）、金额精度与上限、单侧有效行数上限（对账单 / 收款单各 50）、重复有效对判定、
  客户与币种兼容、支持币种、对账单 / 收款单资格与状态机、受控错误码；
- 不改写收款单（金额 / 币种 / 客户 / 状态 / 付款方式 / 银行账户 / 备注）、对账单证据（合计 / 状态 / 登记与作废审计）、
  ERP-069 协议证据、客户主数据、销售订单、装柜与单证、发票、库存与费用、结算与余额记录；
- 收款单侧唯一同币种分摊额度（ERP-350）保持：`收款单 → 销售订单`（ERP-053）与 `收款单 → 代理服务费对账单`
  （ERP-071）两套**有效**证据共同占用同一额度，绝不跨币种合计、绝不重复计算、绝不静默改派；
- 作废证据与历史读取保持不变：不物理删除、不回填、不重写快照；要解除限制必须先走**既有显式作废服务**。

## 5. 测试

- **单元测试**（`src/ERP.UnitTests/AgencyStatementCollectionConcurrencyTests.cs`，内存库）：
  护栏锁序 / 受控错误文案同源；有效分摊证据存在时对账单作废被拒且表头逐字段不变；显式作废分摊后对账单可作废
  且作废证据保留原始金额 / 快照 / 审计；同一对账单**共享未分摊额度**跨两张收款单扣减、超额被拒且零写入；
  跨客户 / 跨币种受控拒绝；源码契约（分摊写路由先取对账单行锁再取收款单行锁、同一事务 + 拒绝清理、
  作废只取对账单行锁、控制器每条路由仍先实时授权）。
- **真实 SQL 集成测试**（`src/ERP.IntegrationTests/AgencyStatementCollectionConcurrencySqlServerTests.cs`，专用 localdb +
  全新 GUID 库 + **两条独立连接门闩竞态**，绝不使用时间睡眠）：
  | 场景 | 测试 |
  |---|---|
  | ① 两张收款单争抢同一对账单剩余额度 | `Race_two_receipts_for_one_remaining_statement_capacity_has_exactly_one_winner` |
  | ② 分摊登记 vs 对账单作废 | `Race_allocation_create_versus_statement_void_is_serialized_without_active_allocation_on_voided_statement` |
  | ③ 分摊作废 vs 所属对账单作废 | `Race_allocation_void_versus_parent_statement_void_is_serialized_and_keeps_void_evidence` |
  | ④ 两个对账单争抢同一收款单额度 | `Race_two_statements_for_one_receipt_shared_budget_has_exactly_one_winner` |
  | 跨证据族收款单唯一额度 | `Existing_order_family_allocation_keeps_receipt_unique_budget_for_agency_family` |
  | 未授权 / 越范围 / 跨币种拒绝 | `Denied_authority_ownership_and_currency_allocate_nothing` |
  | 显式作废释放流程 | `Explicit_allocation_void_releases_statement_and_allows_statement_void_with_evidence_retained` |
  | 目标库护栏 fail-closed | `AgencyStatementCollectionConcurrencyTargetGuardTests` |

  每条竞态都断言落库结果**恰好等于一个串行顺序**（对账单状态 / 有效分摊合计 / 收款单额度 / 作废审计），
  且确认只有一个容量赢家、无重复有效对、无越额度 / 越上限证据。

## 6. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，且
  `Integrated Security=true`；护栏在**任何数据库访问之前**校验失败即中止；
- 每次运行创建**全新的 GUID 后缀库**（默认 `NEWERP_AUTOTEST_ERP521_STATEMENTCOLLECTIONCONCURRENCY_<guid>`），
  发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 不读取 `appsettings` / `.env` / 生产凭据；连接串只来自进程环境变量或专用 localdb 默认值；
- 种子一律使用 SQL Server 自增主键（不显式指定 Id），复用种子数据中的既有「客户资料」菜单与既有授权口径，
  不新增任何菜单 / 角色 / 用户授权。

## 7. 环境证据

（第 1 次尝试的构建 / 单元 / 真实 SQL 运行证据与失败记录见 `.ai/logs/ERP-521/`，每次执行使用独立未复用文件名。）

| 项 | 输出 |
|---|---|
| Release 还原（整解决方案） | `.ai/logs/ERP-521/erp521-restore-09.log` |
| Release 生成（分析器 + 警告即错误，整解决方案） | `.ai/logs/ERP-521/erp521-solution-build-11.log`、`erp521-solution-build-11.err.log`（**0 警告 / 0 错误**） |
| 全量单元测试（**8717 通过 / 0 失败**） | `.ai/logs/ERP-521/erp521-units-12.log`、`.ai/logs/ERP-521/trx/erp521-units-12.trx` |
| ERP-521 真实 SQL 双连接竞态（**12 通过 / 0 失败**，两次独立运行、各自全新 GUID 库） | `.ai/logs/ERP-521/erp521-dev-sql-08.log` / `trx/erp521-dev-sql-08.trx`、`.ai/logs/ERP-521/erp521-sql-race-13.log` / `trx/erp521-sql-race-13.trx` |
| 既有对账单 / 协议 / 收款分摊 / 收款单生命周期 SQL 回归（**34 通过 / 0 失败**：`AgencyStatementConcurrencySqlServerTests` 9、`AgencyServiceFeeRegisterAuthorizationSqlServerTests` 3、`CustomerReceiptSharedFundingSqlServerTests` 7、`CustomerReceiptFundingEvidenceSqlServerTests` 6、`CustomerReceiptLifecycleSqlServerTests` 9） | `.ai/logs/ERP-521/erp521-sql-regression-14.log`、`trx/erp521-sql-regression-14.trx` |
| 开发过程迭代（新增单元 11 通过；相关单元回归 148 通过；分项目 Release 构建） | `erp521-dev-app-build-01.log`、`erp521-dev-units-build-02.log`、`erp521-dev-new-units-03.log` / `trx/erp521-new-units-03.trx`、`erp521-dev-regression-units-04.log` / `trx/erp521-dev-regression-units-04.trx`、`erp521-dev-int-build-05.log`、`erp521-dev-int-build-07.log` |

### 失败 / 中断证据（原样保留，未覆盖 / 未复用）

- `.ai/logs/ERP-521/erp521-dev-sql-06.log` 是前台 `dotnet test` 经管道捕获时的**部分输出**：PowerShell 把
  xUnit 的 stderr 当作错误流，在首个失败行之后提前结束管道，因此该文件只有 466 字节；
  **完整的失败证据**是同一尝试的 `.ai/logs/ERP-521/trx/erp521-dev-sql-06.trx`（其中含
  `Assert.Contains() Failure ... String: "收款单正在被并发修改，本次操作未生效：请刷新后重试（原始收款单与分摊证据均未改变）"`）。
  该失败源于**测试断言**：竞态④（两个对账单争抢同一收款单额度）的输家被**既有收款单行锁**（ERP-378，阻塞后并发冲突）
  受控拒绝，而不是走到「可分摊余额不足」判定；生产护栏行为正确（恰好一个赢家、额度守恒、零写入），
  因此修正断言为接受两类可读业务冲突并重新独立运行（`erp521-dev-sql-08.log`，12 通过 / 0 失败）。
- `.ai/logs/ERP-521/erp521-solution-build-10.log` 是整解决方案 Release 生成在前台调用中被工具 30 秒超时**中断**的
  **部分输出**（已生成到 `ERP.IntegrationTests`，未完成 `ERP.UnitTests`，未打印结果摘要）；
  完整且成功的证据是随后以独立文件名执行的 `.ai/logs/ERP-521/erp521-solution-build-11.log`（0 警告 / 0 错误）。

真实 SQL 运行只作用于 `(localdb)\NEWERP_AutoAcceptance` 上每次全新创建的 GUID 库，未 drop / reset / 复用任何数据库；
构建完成不等于阶段验收，浏览器 / UI 验收本任务未启用（`browser_acceptance.required = false`）。

