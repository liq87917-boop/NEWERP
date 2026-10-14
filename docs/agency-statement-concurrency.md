# 代理服务费对账单证据并发序列化（草稿行替换 / 登记冻结 / 作废）（ERP-520）

## 1. 目标

关闭代理服务费**对账单证据**（`AgencyServiceFeeStatements` + `AgencyServiceFeeStatementLines`，ERP-070 / ERP-437）的
**冻结证据竞态**：修改（草稿**整行替换**）先读一次草稿状态即校验 / 替换行，登记（表头 + 全部行冻结）与作废
（表头 + 全部行状态改写）同样「先读后写」，三者在**同一把对账单行锁**与**同一原子事务**之外各写各的，
因此可能出现

1. 一次**迟到的草稿行替换**落在已被并发**登记**的表头之下 —— 产生「已登记表头 + 事后新增 / 改写的草稿行」的
   混合证据，冻结的合计与行集合不再一致；
2. 两个并发**登记**互相覆盖对方写入的登记人 / 登记时间（赢家审计被输家改写）；
3. **登记 vs 作废**交错：一方冻结、另一方作废，导致状态与行状态 / 审计不匹配（部分冻结 / 部分作废）；
4. 登记时**某一行来源已失效**（来源被取消 / 删除、协议被作废、客户被停用）仍被冻结为「已登记」证据。

本任务只补齐上述**并发序列化与冻结前权威复校**：修改 / 登记 / 作废共用<strong>同一把既有对账单行锁 + 同一原子事务</strong>，
锁内重读权威状态 / 客户范围 / 全部行后才校验与写入；新增（草稿对账单）在同事务内完成校验与写入。
**不**新增表 / 列 / 索引 / 迁移 / 权限 / 菜单 / 角色 / 授权，**不**推断费用、**不**开票、**不**记账 / 收款 / 催收。

## 2. 护栏（`AgencyFeeEvidenceConcurrencyRules`，ERP-519 共用的同一类）

| 项 | 口径 |
|---|---|
| 事务 | `BeginMutationTransactionAsync`：仅关系型后端（SQL Server）开启真实事务；内存库等非关系型返回 `null`（等价无事务）。调用方已开启事务时**绝不嵌套**，由最外层提交 / 回滚（`CommitAsync(null)` 为空操作） |
| 对账单行锁 | `LockStatementRowAsync`：在调用方事务内对对账单行发出「技术审计字段 `UpdatedAt` 刷新」的 UPDATE，取得排它行锁（X 锁，持有至事务结束），语义等价于 `SELECT [Id] FROM [db_owner].[AgencyServiceFeeStatements] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {0}` |
| 协议行锁 | `LockAgreementRowAsync`（ERP-519）：`SELECT [Id] FROM [db_owner].[AgencyServiceFeeAgreements] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {0}` |
| 锁序 | 唯一确定性锁序：**① 协议证据行（同一 Id）→ ② 对账单证据行（同一 Id，ERP-520）→ 更下游的收款分摊 / 收款单行锁（ERP-521）**；同一事务需要同时锁定多级证据行时只能按此顺序，**绝不反向获取**。本任务的对账单写路由**只取对账单行锁**（不在对账单写路由内再取协议 / 来源 / 收款行锁），因此不存在交叉锁序与锁环 |
| 重试 | 并发方先提交使本地乐观并发令牌（`RowVersion`）过期 → 重读权威行（含新令牌）后**有界重试**（8 次），把「锁等待」实现为「阻塞后成功」，绝不误报业务拒绝；重试耗尽才返回受控冲突 |
| 拒绝清理 | `TryRollbackAsync`：**关系型后端**先清空变更跟踪器中的半成品变更再回滚事务（与既有 ERP-412 / ERP-415 / ERP-519 同口径），保证被拒请求零部分状态 / 零部分行 / 零审计漂移；非关系型提供程序没有真实事务与行锁，`TryRollbackAsync` 等价「无事务」并按既有内存语义不触碰跟踪器 |
| 受控写入 | `SaveStatementEvidenceAsync`：对账单 / 行唯一索引冲突（`UX_AgencyServiceFeeStatements_ActiveIdentity` / `UX_AgencyServiceFeeStatementLines_ActiveSource`，错误码 2601 / 2627）映射为对账单口径的稳定业务重复错误（`Duplicate`），**绝不**泄露 `SqlException` / 错误码 / 索引名 / 连接串；非唯一冲突原样上抛 |

## 3. 各写路由口径

| 路由 | 事务 + 锁 | 锁内重读的权威依据 | 冻结 / 写入 |
|---|---|---|---|
| 新增（`CreateAsync`） | 同一事务（校验 + 表头 + 全部行插入） | 客户资格（存在 / 未删除 / 启用）、显式协议资格与客户 / 币种兼容、逐行来源资格与金额、唯一对账单身份、来源占用 | 写入草稿表头 + 全部行 + 服务端合计；并发同身份 / 同来源由既有过滤唯一索引收敛（输家受控重复错误并整体回滚） |
| 修改（`UpdateAsync`） | 同一事务 + **同一对账单行锁** | 权威状态（必须草稿）、权威客户范围、拟提交客户范围、协议资格与客户 / 币种兼容、逐行来源资格与金额、唯一对账单身份、来源占用 | 原草稿行**软删除** + 新行插入 + 表头快照 / 合计在**同一提交**内落库；**已登记 / 已作废一律拒绝**，迟到的草稿行替换绝不落库 |
| 登记（`RecordAsync`） | 同一事务 + **同一对账单行锁** | 权威状态（必须草稿）、权威客户范围、**已持久化表头与全部行的登记口径复校**（客户仍启用 / 协议仍「已登记」且客户币种一致 / 逐行来源仍可引用 / 行金额仍合法） | 以服务端按已校验行**重算**的合计冻结表头与全部行（状态 / 登记时间 / 登记人）；并发重复登记只有唯一赢家，输家绝不覆盖赢家审计；任一行在登记时已失效即整体回滚（**零部分冻结**） |
| 作废（`VoidAsync`） | 同一事务 + **同一对账单行锁** | 权威状态（未作废）、权威客户范围、作废原因 | 表头与全部行的作废状态 / 原因 / 时间戳在**同一提交**内落库；并发重复作废只有唯一赢家，输家绝不覆盖原始作废原因与时间戳；历史行 / 来源快照 / 客户与协议快照 / 登记审计全部保留 |

- **范围预检仍在取锁 / 写库之前**（`EnsureStatementInScopeAsync` / `EnsureCustomerInScope`）：
  越范围 / 已删除 / 不存在的对账单在**任何行锁刷新与写入之前**被拒绝，返回同一条不披露存在性的
  `RegisterNotFoundText`；授权解析（实时启用身份 + 既有「客户资料」菜单 + ERP-097 权威客户范围）
  由控制器在调用服务之前完成，服务在锁内再把已解析范围**重新施加到权威行**（并发归属变化不被旧快照掩盖）。
- 加锁只刷新既有的**技术审计字段** `UpdatedAt`，绝不为了加锁而改写对账单状态 / 行证据 / 客户与协议快照；
  被拒请求的加锁时间戳随事务回滚一并撤销。本任务**不**锁定协议 / 来源行，因此**不会**触碰 ERP-069 协议证据的任何字段。
- **金额与来源口径不变**：行金额与计费基础只来自授权用户显式提交；登记时的合计由服务端按**已持久化行**重算，
  绝不从协议费率、协议固定金额、客户账期 / 默认值、来源单据金额、自由文本或历史对账单推断、折算、补齐任何费用；
  来源只按「来源类型 + 持久化 Id」显式链接，绝不按单号文本 / 金额 / 日期 / 相似度匹配。

## 4. 明确不改写

- 不新增 / 不修改表、列、索引、schema、迁移或种子数据；不新增菜单 / 角色 / 用户授权；不做匿名 / 管理员回退；
- 不改对账单号与身份规范化、客户可用性、协议资格与兼容性、支持币种、金额精度 / 上限、服务期间 / 可选到期日、
  状态机（草稿 → 已登记 → 已作废）、唯一性判定、来源 allowlist 与受控错误码；
- 不改写 ERP-069 协议证据（含费率 / 固定金额 / 状态 / 审计）、客户主数据、销售订单、装柜与装柜清单、单证、
  发票、收款单及其引用行、库存与库存成本、库存流水、费用与退税、结算与余额记录、收款分摊证据；
- 不开票或作废税务发票、不记账或生成凭证、不收款或付款、不催收或联系客户、不调用任何外部服务；
- 不物理删除、不回填、不静默修复任何历史行 / 来源快照 / 作废证据 / 审计（更正仍走显式作废，原因必填）；
- 不生成收款分摊证据、不做收款分摊（ERP-521 另行处理）。

## 5. 实现位置

| 文件 | 变更 |
|---|---|
| `src/ERP.Application/Services/AgencyFeeEvidenceConcurrencyRules.cs` | 新增 `StatementRowLockSql` / `LockStatementRowAsync` / `SaveStatementEvidenceAsync` / `StatementActiveEvidenceConflictText`，并把确定性锁序文案扩展为「协议行 → 对账单行 → 收款分摊 / 收款单行」 |
| `src/ERP.Application/Services/AgencyServiceFeeStatementService.cs` | `CreateAsync` 校验 + 表头 + 全部行同事务、受控写入；`UpdateAsync` / `RecordAsync` / `VoidAsync` 各自拆为「范围预检（写库前）」+「核心（同事务 + 同一对账单行锁 + 锁内重读 + 登记前权威复校）」；新增 `LockStatementAuthoritativeAsync` / `EnsureAuthoritativeScope` / `BuildPersistedSaveDto` |
| `src/ERP.Api/Controllers/AgencyServiceFeeStatementController.cs` | 未改动（调用点、授权解析与错误码字面不变） |
| `docs/agency-statement-concurrency.md`（新增） | 本文件 |
| `src/ERP.UnitTests/AgencyStatementConcurrencyTests.cs`（新增） | 护栏契约 + 生命周期串行化 + 冻结前权威复校 + 范围 / 菜单撤销 + 源码契约单元测试 |
| `src/ERP.IntegrationTests/AgencyStatementConcurrencySqlServerTests.cs`（新增） | 真两条独立连接竞态 + 多行迟到失效 + 范围撤销 + 真实生命周期 + 目标库护栏 |

未改动：`AgencyServiceFeeStatementRules`（复用既有状态机 / 资格 / 身份 / 金额 / 来源规则）、
`AgencyServiceFeeReconciliationRules`（复用既有实时授权与权威客户范围）、`ErpDbContext` /
`SchemaUpgrader`（不新增 / 不修改任何表列索引）、前端资源、权限与菜单种子。

## 6. 测试口径

- **单元测试**（内存库 + 真实 HTTP 身份）：护栏常量与锁序同源、关系型判定、对账单行锁在非关系型等价无操作、
  唯一索引冲突识别不误报；已登记 / 已作废拒绝迟到的草稿行替换并逐字段与行集合不变；重复登记只有唯一赢家且
  赢家审计不被覆盖；登记前复校在「客户停用 / 协议作废 / 多行中一行来源已取消」时整体回滚（表头保持草稿、
  合计与全部行状态不变）；跨范围与菜单撤销后修改 / 登记 / 作废 fail closed 且零字段变更；
  源码契约断言四条写路由共用同一事务 + 同一对账单行锁、锁先于权威校验、控制器每条路由仍先授权。
- **真实 SQL 集成测试**（专用 localdb + 全新 GUID 库 + **两条独立连接门闩竞态**，绝不使用时间睡眠）：
  ① 草稿行替换 vs 登记；② 草稿行替换 vs 作废；③ 登记 vs 登记（唯一赢家 + 赢家审计）；
  ④ 登记 vs 作废（唯一赢家）；⑤ 多行中一行来源在登记前被取消（零部分冻结）；⑥ 授权范围撤销后写路由拒绝。
  每条竞态都断言落库结果**恰好等于一个串行顺序**（状态 + 合计 + 行集合 + 登记 / 作废审计），并确认本测试身份下
  只有一条对账单；另有真实生命周期「新建 → 修改（整行替换）→ 登记 → 作废 → 历史读取」。

  对应测试（`src/ERP.IntegrationTests/AgencyStatementConcurrencySqlServerTests.cs`）：

  | 场景 | 测试 |
  |---|---|
  | ① 草稿行替换 vs 登记 | `Race_draft_line_replacement_versus_record_is_serialized_without_mixed_evidence` |
  | ② 草稿行替换 vs 作废 | `Race_draft_line_replacement_versus_void_is_serialized_without_mixed_evidence` |
  | ③ 重复登记唯一赢家 | `Race_duplicate_record_has_exactly_one_winner_and_never_overwrites_winner_audit` |
  | ④ 登记 vs 作废 | `Race_record_versus_void_is_serialized_and_keeps_winner_record_audit_inside_void_evidence` |
  | ⑤ 多行迟到失效（确定性 + 竞态） | `Record_with_a_cancelled_source_line_in_multi_line_statement_rolls_back_with_zero_partial_freeze`、`Race_record_versus_source_cancellation_never_freezes_partial_or_stale_evidence` |
  | ⑥ 授权 / 范围撤销（确定性 + 竞态） | `Scope_and_menu_revocation_after_draft_creation_fails_closed_with_zero_mutation`、`Race_record_versus_scope_revocation_stays_serialized_with_zero_partial_freeze` |
  | 真实生命周期 | `Lifecycle_create_replace_record_void_keeps_history_and_releases_sources` |
  | 目标库护栏 fail-closed | `AgencyStatementConcurrencyTargetGuardTests`（错误实例 / 错误库名 / 非集成安全在建库前拒绝） |

## 7. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，
  且 `Integrated Security=true`；护栏在**任何数据库访问之前**校验失败即中止；
- 每次运行创建**全新的 GUID 后缀库**（默认 `NEWERP_AUTOTEST_ERP520_STATEMENTCONCURRENCY_<guid>`），
  发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 不读取 `appsettings` / `.env` / 生产凭据；连接串只来自进程环境变量或专用 localdb 默认值；
- 种子一律使用 SQL Server 自增主键（不显式指定 Id），复用种子数据中的既有「客户资料」菜单与既有授权口径，
  不新增任何菜单 / 角色 / 用户授权。

## 8. 环境证据

（构建 / 单元 / 真实 SQL 运行证据与失败记录见 `.ai/logs/ERP-520/`。）

第 2 次尝试的工程验证输出（全部为真实执行产物，失败日志同样保留，绝不覆盖 / 复用文件名）：

| 项 | 输出 |
|---|---|
| Release 还原 | `.ai/logs/ERP-520/erp520-attempt2-restore-04.log` |
| Release 生成（分析器 + 警告即错误，整解决方案） | `.ai/logs/ERP-520/erp520-attempt2-solution-build-04.log`、`.ai/logs/ERP-520/erp520-attempt2-solution-build-09.log`（各 0 警告 / 0 错误；后者为全部编辑完成后的最终复核） |
| 全量单元测试（8706 通过 / 0 失败） | `.ai/logs/ERP-520/erp520-attempt2-units-06.log`、`.ai/logs/ERP-520/trx/erp520-units-06.trx` |
| ERP-520 真实 SQL 双连接竞态（14 通过 / 0 失败，两次独立运行） | `.ai/logs/ERP-520/erp520-attempt2-sql-race-03.log`、`.ai/logs/ERP-520/erp520-attempt2-sql-race-08.log`、`.ai/logs/ERP-520/trx/erp520-statement-concurrency-sql-03.trx`、`.ai/logs/ERP-520/trx/erp520-statement-concurrency-sql-08.trx` |
| 既有对账单 / 协议 SQL 回归（43 通过 / 0 失败） | `.ai/logs/ERP-520/erp520-attempt2-statement-sql-regression-07.log`、`.ai/logs/ERP-520/trx/erp520-statement-sql-regression-07.trx` |
| 失败保留（第 1 次尝试） | `.ai/logs/ERP-520/probe-units-build-1.log`、`.ai/logs/ERP-520/probe-units-build-2.log`、`.ai/logs/ERP-520/erp520-attempt2-sql-race-02.log`（PowerShell 引号错误，非代码缺陷） |

- 同一尝试内的中断证据也原样保留：`.ai/logs/ERP-520/erp520-attempt2-units-05.log` 是前台命令在工具超时时被中断的
  **部分输出**（未完成、未被任何后续运行复用；完整单元证据以上表的 `...units-06.log` / `...units-06.trx` 为准）。

真实 SQL 运行只作用于 `(localdb)\NEWERP_AutoAcceptance` 上每次全新创建的 GUID 库，未 drop / reset / 复用任何数据库；
构建完成不等于阶段验收，浏览器 / UI 验收本任务未启用。

最终复核（与最后一次 Release 生成产物同批，均为独立未复用文件名）：
`.ai/logs/ERP-520/erp520-attempt2-units-10.log` / `trx/erp520-units-10.trx`（8706 通过 / 0 失败）、
`.ai/logs/ERP-520/erp520-attempt2-sql-race-10.log` / `trx/erp520-statement-concurrency-sql-10.trx`（14 通过 / 0 失败）。
