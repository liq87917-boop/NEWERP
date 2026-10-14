# 代理服务费协议证据并发序列化（草稿修改 / 登记冻结 / 作废）（ERP-519）

## 1. 目标

关闭代理服务费协议证据（`AgencyServiceFeeAgreements`，ERP-069 / ERP-439 / ERP-516）的
**冻结证据竞态**：修改只读一次草稿状态即编辑，登记 / 作废同样「先读后写」，三者之间**没有共同事务与行锁**，
因此可能出现

1. 一条迟到保存的**草稿修改**覆盖（或事后改写）已被并发**登记 / 作废**冻结的证据；
2. 两个并发**登记**互相覆盖对方写入的登记人 / 登记时间（赢家审计被输家改写）；
3. 两个并发**作废**互相覆盖原始作废原因 / 时间戳；
4. 越范围（授权撤销后收敛）的请求在「已读取、未写入」窗口内仍然落库。

本任务只补齐上述**并发序列化**：修改 / 登记 / 作废共用<strong>同一把既有实体行锁 + 同一原子事务</strong>，
锁内重读权威状态 / 费用条款 / 客户范围后才写入；新增（登记草稿）在同事务内完成校验与插入，
并发同身份登记仍由既有过滤唯一索引收敛。**不**新增表 / 列 / 索引 / 迁移 / 权限 / 菜单 / 角色 / 授权，
**不**生成对账单、**不**推断费用、**不**做记账 / 收款 / 开票动作。

## 2. 护栏（`AgencyFeeEvidenceConcurrencyRules`）

| 项 | 口径 |
|---|---|
| 事务 | `BeginMutationTransactionAsync`：仅关系型后端（SQL Server）开启真实事务；内存库等非关系型返回 `null`（等价无事务）。调用方已开启事务时**绝不嵌套**，由最外层提交 / 回滚（`CommitAsync(null)` 为空操作） |
| 行锁 | `LockAgreementRowAsync`：在调用方事务内对协议行发出「技术审计字段 `UpdatedAt` 刷新」的 UPDATE，取得排它行锁（X 锁，持有至事务结束），语义等价于 `SELECT [Id] FROM [db_owner].[AgencyServiceFeeAgreements] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {0}` |
| 锁序 | 唯一确定性锁序：**协议行（同一 Id）→ 由该事务顺带写入证据字段**；更下游的对账单 / 收款分摊 / 收款单行锁（ERP-520 / ERP-521）只允许在协议行锁**之后**获取，**绝不反向获取**，因此不存在锁环 |
| 重试 | 并发方先提交使本地乐观并发令牌（`RowVersion`）过期 → 重读权威行（含新令牌）后**有界重试**（8 次），把「锁等待」实现为「阻塞后成功」，绝不误报业务拒绝；重试耗尽才返回受控冲突 |
| 拒绝清理 | `TryRollbackAsync`：**关系型后端**先清空变更跟踪器中的半成品变更再回滚事务（与既有 ERP-412 / ERP-415 同口径），保证被拒请求零部分状态 / 零部分行 / 零审计漂移；非关系型提供程序没有真实事务与行锁，`TryRollbackAsync` 等价「无事务」并按既有内存语义不触碰跟踪器 |
| 受控写入 | `SaveEvidenceAsync`：唯一索引冲突（2601 / 2627）映射为稳定业务重复错误（`Duplicate`），**绝不**泄露 `SqlException` / 错误码 / 索引名 / 连接串；非唯一冲突原样上抛 |

## 3. 各写路由口径

| 路由 | 事务 + 锁 | 锁内重读的权威依据 | 冻结 / 写入 |
|---|---|---|---|
| 新增（`CreateAsync`） | 同一事务（校验 + 插入） | 客户资格（存在 / 未删除 / 启用）、唯一身份 | 写入草稿 + 服务端客户快照；并发同身份由既有过滤唯一索引 `UX_AgencyServiceFeeAgreements_ActiveIdentity` 收敛为「恰好一条有效身份」，输家受控重复错误并整体回滚 |
| 修改（`UpdateAsync`） | 同一事务 + **同一协议行锁** | 权威状态（必须草稿）、权威客户范围、拟提交客户范围、唯一身份、费用条款 | 仅更新协议字段与条款；**已登记 / 已作废一律拒绝**，迟到的草稿编辑绝不落库 |
| 登记（`RecordAsync`） | 同一事务 + **同一协议行锁** | 权威状态（必须草稿）、权威客户范围、**已持久化费用条款复核（ERP-516）** | 冻结状态 / 登记时间 / 登记人；并发重复登记只有唯一赢家，输家绝不覆盖赢家审计 |
| 作废（`VoidAsync`） | 同一事务 + **同一协议行锁** | 权威状态（未作废）、权威客户范围、作废原因 | 写入已作废 + 原因 + 时间戳；并发重复作废只有唯一赢家，输家绝不覆盖原始作废原因与时间戳 |

- **范围预检仍在取锁 / 写库之前**（`EnsureAgreementInScopeAsync` / `EnsureCustomerInScope`）：
  越范围 / 已删除 / 不存在的协议在**任何行锁刷新与写入之前**被拒绝，返回同一条不披露存在性的
  `AgreementNotFoundText`；授权解析（实时启用身份 + 既有「客户资料」菜单 + ERP-097 权威客户范围）
  由控制器在调用服务之前完成，服务在锁内再把已解析范围**重新施加到权威行**（并发归属变化不被旧快照掩盖）。
- 加锁只刷新既有的**技术审计字段** `UpdatedAt`，绝不为了加锁而改写条款 / 状态 / 客户快照；
  被拒请求的加锁时间戳随事务回滚一并撤销。

## 4. 明确不改写

- 不新增 / 不修改表、列、索引、schema、迁移或种子数据；不新增菜单 / 角色 / 用户授权；不做匿名 / 管理员回退；
- 不改计费方式互斥、费率 `(0,100]` 与 4 位小数、固定金额按币种精度取整与 `DECIMAL(18,2)` 边界、
  币种支持范围、客户可用性、生效日期区间、唯一身份判定与受控错误码；
- 不改写客户主数据（含佣金比例与信用状态）、销售订单、装柜与单证、对账单证据、收款分摊证据、收款单、
  销项发票证据、库存与库存成本、费用与退税记录；
- 不开票、不记账、不生成凭证 / 收款 / 付款 / 结算单、不授权或发起任何付款、不调用任何外部服务；
- 不物理删除、不回填、不静默修复任何历史证据与审计（更正仍走显式作废，原因必填）；
- 不生成对账单、不推断或折算任何费用条款（ERP-520 / ERP-521 另行处理对账单与收款分摊）。

## 5. 实现位置

| 文件 | 变更 |
|---|---|
| `src/ERP.Application/Services/AgencyFeeEvidenceConcurrencyRules.cs`（新增） | 共用并发护栏：事务帮助（非嵌套）/ 协议行锁 / 拒绝清理 / 唯一索引冲突映射 + 锁序、边界与受控错误文案 |
| `src/ERP.Application/Services/AgencyServiceFeeAgreementService.cs` | `CreateAsync` 校验 + 插入同事务；`UpdateAsync` / `RecordAsync` / `VoidAsync` 各自拆为「范围预检（写库前）」+「核心（同事务 + 同行锁 + 锁内重读）」；新增 `LockAuthoritativeAsync` / `EnsureAuthoritativeScope` |
| `src/ERP.Api/Controllers/AgencyServiceFeeAgreementController.cs` | 仅类 / 路由文档同步（授权解析、事务与锁位置说明；调用点、授权与错误码字面不变） |
| `docs/agency-agreement-concurrency.md`（新增） | 本文件 |
| `src/ERP.UnitTests/AgencyAgreementConcurrencyTests.cs`（新增） | 护栏契约 + 生命周期串行化 + 范围 + 源码契约单元测试 |
| `src/ERP.IntegrationTests/AgencyAgreementConcurrencySqlServerTests.cs`（新增） | 真两条独立连接竞态 + 确定性拒绝 + 真实生命周期 + 目标库护栏 |

未改动：`AgencyServiceFeeAgreementRules`（复用既有条款 / 状态机 / 身份 / 范围规则）、
`ErpDbContext` / `SchemaUpgrader`（不新增 / 不修改任何表列索引）、前端资源、权限与菜单种子。

## 6. 测试口径

- **单元测试**（内存库 + 真实 HTTP 身份）：护栏常量与锁序同源、关系型判定、事务帮助在非关系型等价无操作、
  唯一索引冲突识别不误报；已登记 / 已作废拒绝迟到草稿修改并逐字段不变；重复登记 / 重复作废只有唯一赢家且
  赢家审计不被覆盖；跨范围登记 / 作废 / 修改与重复身份创建受控拒绝且零新增行；真实生命周期（含固定金额口径）；
  源码契约断言四条写路由共用同一事务 + 同一行锁、锁前仍先做范围预检、控制器每条路由仍先授权。
- **真实 SQL 集成测试**（专用 localdb + 全新 GUID 库 + **两条独立连接门闩竞态**，绝不使用时间睡眠）：
  ① 修改 vs 登记；② 修改 vs 作废；③ 登记 vs 登记（唯一赢家 + 赢家审计）；④ 登记 vs 作废；
  ⑤ 同身份并发创建（唯一有效身份 + 受控 `Duplicate` 且不含驱动 / 索引细节）。
  每条竞态都断言落库结果**恰好等于一个串行顺序**（状态 + 条款 + 登记 / 作废审计），并确认本测试身份下**只有一行**；
  另有确定性用例：越范围（授权收敛）、菜单撤销、软删除协议行的登记 / 修改 / 作废一律受控拒绝且零字段变更
  （含加锁时间戳随回滚撤销），以及真实生命周期「新建 → 修改 → 登记 → 作废 → 历史读取」。

## 7. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，
  且 `Integrated Security=true`；护栏在**任何数据库访问之前**校验失败即中止；
- 每次运行创建**全新的 GUID 后缀库**（默认 `NEWERP_AUTOTEST_ERP519_AGREEMENTCONCURRENCY_<guid>`），
  发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 不读取 `appsettings` / `.env` / 生产凭据；连接串只来自进程环境变量或专用 localdb 默认值；
- 种子一律使用 SQL Server 自增主键（不显式指定 Id），复用种子数据中的既有「客户资料」菜单与既有授权口径，
  不新增任何菜单 / 角色 / 用户授权。


## 8. 环境证据

- 全部输出 / TRX / 失败记录于 `.ai/logs/ERP-519/`（未删改既有失败记录）：
  - `erp519-build.log`：`dotnet build NEWERP.sln -c Release /p:TreatWarningsAsErrors=true
    /p:RunAnalyzersDuringBuild=true` → **0 警告 / 0 错误 / 成功**；
  - `erp519-full-units.trx` / `.out.log`：`dotnet test src/ERP.UnitTests -c Release --no-build` →
    **8689 通过 / 0 失败 / 0 跳过**（ERP-518 基线 8675 + 本任务新增 14）；
  - `erp519-agency-units.trx` / `.out.log`：`--filter "FullyQualifiedName~Agency"` → **348 通过 / 0 失败**（既有代理单元回归）；
  - `erp519-new-units.trx` / `.out.log`：`--filter "FullyQualifiedName~AgencyAgreementConcurrency"`（UnitTests）→
    **14 通过 / 0 失败**；
  - `erp519-sql.trx` / `.out.log`：`--filter "FullyQualifiedName~AgencyAgreementConcurrency"`（IntegrationTests）→
    **13 通过 / 0 失败**（8 例真实竞态 / 生命周期 / 拒绝 + 5 例目标库护栏；真实 localdb + 每次运行新建 GUID 库）；
  - `erp519-sql-regressions.trx` / `.out.log`：既有代理协议真实 SQL 回归
    （ERP-516 条款边界 + ERP-439 授权）→ **37 通过 / 0 失败**；
  - `erp519-attempt1-notes.md`：首次真实运行的失败记录（**逐字复述**原始断言与堆栈，见下）。
- 首次失败均为**新代码 / 新脚手架缺陷**，已修复并重跑通过；原 TRX 因紧随其后的重跑被 VSTest 覆盖，
  原始失败消息已在 `erp519-attempt1-notes.md` 中逐字保留（未做美化 / 删减）：
  1. 拒绝路径曾无条件清空变更跟踪器 → 内存库既有用例的已跟踪实体被分离（
     `Expected: 9.9 Actual: 3.5` @ `AgencyServiceFeeAgreementTests.cs:989`）→ 改为**仅关系型后端**丢弃，
     既有断言一字未改；
  2. 新 SQL 竞态脚手架使用**全表计数**（共享 Fixture 库累积行）→ 4 例在最后一行计数断言失败
     （`Expected: 1 Actual: 4/6/7/3`）→ 改为按本用例「客户 + 规范化协议号」精确计数，业务不变量断言未被放宽。
- 本任务未执行浏览器 / UI 验收（`browser_acceptance.required = false`），未做部署、生产数据库变更或破坏性数据操作，
  未新增权限 / schema / 迁移 / 种子数据，未读取 `.env*` / `appsettings*.json` / 生产连接串。
- **构建与测试通过不等于阶段验收完成**，也不声称整个阶段或相邻任务（ERP-520 / ERP-521）已被本任务覆盖。

