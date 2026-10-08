# 业务单据附件引用登记与作废原子性说明（ERP-412）

- 任务：`ERP-412`「Serialize attachment reference registration and void with authoritative parent changes」
- 权威实现：
  - `src/ERP.Application/Services/DocumentAttachmentReferenceMutationRules.cs`（**唯一权威口径**：原子事务 + 确定性行锁 + 唯一口径冲突映射）
  - `src/ERP.Application/Services/DocumentAttachmentReferenceService.cs`（`CreateAsync` / `VoidAsync` 全部落在同一事务与行锁内；锁内重新读取实时身份 / 菜单 / 客户范围与权威父单据）
  - `src/ERP.Api/Controllers/DocumentAttachmentReferenceController.cs`（把真实身份交给服务层，供锁内实时重解析；不做 DDL）
  - 复用（**不修改**）：`AttachmentOwnerAuthorizationRules`（ERP-407 / ERP-408 三重护栏）、`ErpDbContext.Config` 的既有过滤唯一索引 `UX_DocumentAttachmentReferences_ActiveIdentity`、`SchemaUpgrader` 第 32 段幂等建表 / 建索引

## 1. 原子性协议

| 操作 | 事务 / 锁 | 锁内复核 | 失败语义 |
| --- | --- | --- | --- |
| 登记（`POST /api/document-attachment-references`） | 原子事务内先对**权威父单据行**加锁（`UPDLOCK, HOLDLOCK` 语义） | 按真实身份**重新解析**实时权限（菜单 / 账号状态 / 客户范围）+ 重读权威父单据（存在 / 未删除 + 号码 / 类型快照）+ 复核父单据类型菜单授权与权威归属范围 + 判定有效身份唯一，**之后**才写快照与登记行 | 任何失败整体回滚（零部分写入）；并发输家由既有过滤唯一索引拒绝并映射为 `Duplicate`（稳定文案） |
| 作废（`POST /api/document-attachment-references/{id}/void`） | 原子事务内先对**附件引用行**加锁（同一口径） | 锁内重读引用（tracked）+ 实时重解析身份 / 权限 + 权威归属授权复核；读到「已作废」即拒绝 | 输家 / 重复作废原子拒绝，**绝不覆盖**赢家保留的原始作废原因与时间戳 |

- **确定性锁序**：登记只锁父单据行，作废只锁引用行（单锁，无锁环）；多锁场景一律按 **Id 升序**（去重、仅正整数）确定性获取（`MergeLockIds`），绝不反向获取其它锁。
- **加锁只刷新技术字段 `UpdatedAt`**（与既有 ERP-409 `AttachmentEvidenceMutationRules` / `TradeDocumentMutationRules` 同一口径，仅用 EF Core 基础 API）。`UpdatedAt` 是技术审计字段、**不是**商业证据，因此**绝不**为了加锁而改写父单据的状态 / 金额 / 数量 / 客户 / 明细（`No parent commercial/status fields changed merely to lock`）。
- **权威父单据身份不变**：登记写入的是**锁内重新读取**的权威父单据的号码 / 类型快照；父单据改名 / 停用 / 软删除都不影响历史登记可读，本模块也从不回写父单据。
- **实时权限重读**：即使调用方携带的是加锁前解析的授权快照，锁内仍按真实身份重新解析；菜单撤销、账号禁用 / 删除、归属客户变更在本请求提交前立即收敛（fail closed）。
- **历史不可变**：作废只改状态与作废留痕（`Status` / `VoidedAt` / `VoidReason`），保留原始元数据、来源授权确认与授权留痕；**不**硬删除、**不**静默替换、**不**删除任何远端对象。

## 2. 锁语义（与既有父单据变更锁一致）

- 登记在调用方事务内对目标父单据行发一条「审计时间戳刷新」的 `UPDATE`，取得排它行锁（X 锁，持有至事务结束），语义等价于
  `SELECT Id FROM <父表> WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}`；父表按类型映射：`SalesOrders` / `PurchaseOrders` /
  `ContainerLoadingLists` / `TradeDocuments`（父单据类型不在白名单 → 拒绝加锁，绝不放过）。
- 作废在调用方事务内对 `db_owner.DocumentAttachmentReferences` 目标行发同一口径的 `UPDATE` 取锁。
- 并发方先提交使本地乐观并发令牌（`RowVersion`）过期时按「阻塞后成功」语义有界重试（`RowLockRetryAttempts = 8`）；重试耗尽才抛出并发冲突文案，绝不把纯锁等待误报成业务拒绝。
- **非关系型**（内存库等）无行锁语义：锁定等价无操作，事务等价无事务；存在性仍由锁内**重新读取权威父单据**判定，声明式校验与状态判定口径不变。

## 3. 唯一口径与并发输家的可读映射

- 有效身份唯一复用**既有过滤唯一索引**
  `UX_DocumentAttachmentReferences_ActiveIdentity(ParentType, ParentId, Category, ReferenceId) WHERE IsDeleted = 0 AND Status = 0`；
  已作废记录保留可读但不占用身份，因此作废后可重新登记同一引用标识（新身份，历史留痕不变）。
- 登记在**锁内**先做只读重复判定（`EnsureIdentityAvailableAsync`，得到可读的重复详情）；索引作为**并发最后防线**。
- 并发输家（另一个同身份登记先提交）在插入时被索引拒绝 → `SaveReferenceAsync` 捕获唯一冲突并映射为
  **稳定的业务冲突** `BusinessException.Duplicate(ActiveIdentityConflictText)`，随后调用方在同一事务上
  **完整回滚**（`TryRollbackAsync`：先清空变更跟踪器，再回滚事务）→ **零部分写入**。
- **绝不暴露内部路径**：`SqlException` / SQL 错误码（2601 / 2627）/ 索引名 / 表名 / 连接串**不**进入用户可见文案；
  非唯一性冲突（如结构缺失）原样上抛，由全局异常中间件返回通用文案，不被误报成「重复」。
- **失败请求零副作用**：被拒绝的登记 / 作废既不改写任何附件引用行（零部分写入），也不改写父单据与库存 /
  财务记录；不以引用标识发起任何网络 / 文件系统访问，不触碰任何远端对象与文件存储。

## 4. 边界

- 不新增菜单 / 角色 / 用户授权，不把空身份当作匿名或管理员；身份只来自已认证请求主体（`ClaimTypes.NameIdentifier`）。
- **不新增任何表 / 列 / 索引**：复用既有过滤唯一索引与既有实体映射；本任务不产生任何 DDL（`SchemaUpgrader` 未修改）。
- 不改写父单据的状态、金额、数量、客户、明细与备注，不生成库存移动，不改动库存 / 财务 / 发票 / 税务 / 费用与结算记录；父单据生命周期与库存来源单据审计保持原样。
- 不清理 / 改派 / 硬删除任何历史引用；作废保留原始元数据与原始原因 / 时间戳。

## 5. 测试证据

| 层 | 文件 | 覆盖 |
| --- | --- | --- |
| 单元（内存库） | `src/ERP.UnitTests/DocumentAttachmentReferenceMutationTests.cs`（新增） | 登记锁内重读权威父单据并写入服务端快照、父单据商业字段不变、库存 / 财务零写入；父单据已删除 → 零写入拒绝；归属越界 / 旧授权快照不能替代锁内实时重解析 → fail closed；并发唯一索引冲突 → 稳定 `Duplicate` 且不泄露 SqlException / 索引名 / 错误码；元数据写入失败 → 完整回滚零部分写入；重复 / 竞态作废 → 拒绝且保留原始原因与时间戳；作废后重新登记生效且历史不可变；非关系型事务与行锁等价无操作、确定性锁键序稳定；护栏 / 服务源契约（锁先于重读与写入） |
| 单元（既有回归） | `DocumentAttachmentReferenceTests.cs` / `DocumentAttachmentReferenceAuthorizationTests.cs` / `AttachmentOwnerAuthorizationTests.cs` / `AttachmentEvidenceTests.cs` 等 | 登记 / 作废 / 台账 / 候选 / 授权 / 附件内容证据全部保留（`safe` profile 全量 `ERP.UnitTests`） |
| SQL Server 集成（受控 localdb） | `src/ERP.IntegrationTests/DocumentAttachmentReferenceMutationSqlServerTests.cs`（新增） | **两个独立连接竞态**：① 同身份并发登记 → 恰好一条有效身份 + 输家稳定业务冲突；② 登记 vs 父单据归属变更 → 合法一致登记或零写入拒绝；③ 同行并发作废 → 恰好一个赢家 + 原始原因 / 时间戳保留 + 输家不覆盖；确定性顺序：父单据已删除 / 归属越界 / 提交前菜单撤销 → fail closed 零落库；作废后显式重新登记生效且历史不可变；元数据写入失败 → 完整回滚零部分写入、父单据 / 库存 / 财务不变；专用目标护栏 fail-closed |
| 安全 profile | `.ai/config.json` 的 `safe` | `dotnet restore` → Release 构建（`TreatWarningsAsErrors`）→ `ERP.UnitTests` 全量 |

### 5.1 一致性不变量

- **恰好一条有效身份**：同一「父单据 + 分类 + 引用标识」在有效记录内恒为 0 或 1 条（索引 + 锁内判定双重保证）。
- **零部分写入**：任何被拒绝 / 失败的登记或作废都不得留下半成品引用行，也不得残留父单据加锁期间的技术字段刷新。
- **父状态不撕裂**：登记 / 作废路径绝不改写父单据金额、押金、客户与状态（单元 + 集成断言）。

### 5.2 SQL Server 集成目标护栏

- 实例必须精确为 `(localdb)\NEWERP_AutoAcceptance`，库名前缀必须为 `NEWERP_AUTOTEST`，且必须为 `Integrated Security`；不满足在任何库访问之前直接拒绝（`AssertDedicatedTarget`）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings*.json` / `.env` / 生产凭据。
- 保留既有库存来源单据审计与原始失败日志：集成测试只新增自己的引用行，不清理 / 不删除任何既有行。

### 5.3 验证产物与路径纪律

- 验证输出只写入**仓库已忽略**的日志文件（`TestResults/*.log`）；`bin/` 与 `obj/` 同样被忽略。
- 真实 SQL 集成测试只在受控 localdb 实例上创建 `NEWERP_AUTOTEST_*` 数据库（机器实例目录），**不**在仓库内产生数据库文件。

## 6. 已知限制 / 阻断

- 真实浏览器验收按任务配置为 `not_required`（未执行，也不声明通过）；本任务不涉及前端 / 样式改动。
- SQL Server 集成测试需要本机可用的 `(localdb)\NEWERP_AutoAcceptance` 实例；本机环境缺失时该文件不构成阶段验收证据（**构建通过不等于阶段验收**）。
- 本模块仍只登记**元数据引用**：不校验文件是否存在、内容是否安全或是否已获下载授权；生产对象存储（OSS）未实现、未注册、未激活。
- 内存库无真实行锁与事务：并发语义以 SQL Server 集成测试为准；单元测试覆盖锁内重读、冲突映射与零写入不变量。

