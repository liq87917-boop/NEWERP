# 附件证据登记与作废原子性说明（ERP-409）

- 任务：`ERP-409`「Make attachment registration and explicit void atomic with business owner changes」
- 权威实现：
  - `src/ERP.Application/Services/AttachmentEvidenceMutationRules.cs`（**唯一权威口径**：原子事务 + 确定性行锁 + 有界内容补偿 + 内部完整性证据）
  - `src/ERP.Application/Services/AttachmentEvidenceService.cs`（`UploadAsync` / `VoidAsync` 全部落在同一事务与行锁内；锁内重新读取实时身份 / 权限 / 父单据状态）
  - `src/ERP.Application/Interfaces/IAttachmentContentStore.cs`（内容接缝新增**有界补偿**成员 `TryRemoveRequestOwnedAsync`，默认实现返回 `false`）
  - `src/ERP.Infrastructure/Storage/IsolatedLocalAttachmentContentStore.cs`（隔离本地存储的补偿实现：键形态校验 + 根目录复核 + 单文件删除）
  - `src/ERP.Api/Controllers/AttachmentEvidenceController.cs`（上传 / 作废把真实身份传入服务层，供锁内实时重解析）

## 1. 原子性协议

| 操作 | 事务 / 锁 | 锁内复核 | 失败语义 |
| --- | --- | --- | --- |
| 登记（`POST /api/attachment-evidences`） | 原子事务内先对**归属（父）业务单据行**加锁（`UPDLOCK, HOLDLOCK` 语义） | 按真实身份**重新解析**实时权限（菜单 / 账号状态 / 客户范围）+ 重读父单据存在 / 未删除 + 权威归属客户范围，**之后**才发布内容并提交元数据 | 元数据**确认回滚**后才做一次有界内容补偿；提交不确定（提交异常 / 取消 / 回滚失败）一律**保留**内容并保留内部证据 |
| 作废（`POST /api/attachment-evidences/{id}/void`） | 原子事务内先对**证据行**加锁（同一口径） | 锁内重读证据（tracked）+ 实时重解析身份 / 权限 + 权威归属授权复核；读到「已作废」即拒绝 | 输家 / 重复作废原子拒绝，**绝不覆盖**赢家保留的原始作废原因与时间戳 |

- **确定性锁序**：登记只锁归属行，作废只锁证据行（单锁，无锁环）；多锁场景一律按 **Id 升序**（去重、仅正整数）确定性获取（`MergeLockIds`）。
- **加锁只刷新技术字段 `UpdatedAt`**（与既有 `TradeDocumentMutationRules` / `PurchaseInvoiceConcurrencyRules` 同一口径，仅用 EF Core 基础 API）。`UpdatedAt` 是技术审计字段，**不**是商业证据，因此**绝不**为了加锁而改写父单据的状态 / 金额 / 数量 / 客户 / 明细（`No parent business field mutation merely to lock`）。
- **实时权限重读**：即使调用方携带的是加锁前解析的授权快照，锁内仍按真实身份（`actingUserId`）重新解析；菜单撤销、账号禁用 / 删除、归属客户变更在本请求提交前立即收敛（fail closed）。
- **历史不可变**：作废只改状态与作废留痕（`Status` / `VoidedAt` / `VoidReason`），保留原始文件名快照、摘要、媒体类型、长度、归属与上传人；**不**硬删除、**不**替换二进制、**不**改派归属，**不**删除任何已受理证据的内容。

## 2. 锁语义（与既有父单据变更锁一致）

- 登记在调用方事务内对目标父行发一条「审计时间戳刷新」的 `UPDATE`，取得排它行锁（X 锁，持有至事务结束），语义等价于
  `SELECT Id FROM <父表> WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}`。
- 并发方先提交使本地乐观并发令牌（`RowVersion`）过期时按「阻塞后成功」语义有界重试（`RowLockRetryAttempts = 8`）；重试耗尽才抛出并发冲突文案，绝不把纯锁等待误报成业务拒绝。

## 3. 有界内容补偿与失败语义

内容接缝新增成员（唯一扩展，且默认实现**不删除任何内容**）：

```
Task<bool> TryRemoveRequestOwnedAsync(string? storageKey, CancellationToken cancellationToken = default)
    => Task.FromResult(false);   // 默认实现：不支持补偿 → 上层保留内部证据
```

| 场景 | 行为 |
| --- | --- |
| 元数据写入失败且回滚**已确认** | 只对**本次请求新建**的内容键做一次性有界补偿；补偿成功即无孤儿内容，失败则保留内容 + 抛出内部完整性异常 |
| 提交结果不确定（提交异常 / 回滚失败） | **绝不删除**可能已提交的内容；抛内部完整性异常并保留可恢复证据 |
| 请求取消 | 同上：**绝不删除**；保留内容与内部证据 |
| 补偿失败（键非法 / 不存在 / 删除失败 / 不支持） | 视为「清理未成功」，保留内容 + 内部完整性异常，绝不谎报已清理 |
| 验证通过后的正常失败（如格式 / 大小 / 权限拒绝） | 尚未落盘或已完成补偿，抛出原始业务异常，零落库零落盘 |

- **有界性**：补偿只可能删除**单个文件**，且该键必须是隔离存储 `SaveAsync` 生成的不透明形态
  `yyyyMMdd/{32 位小写十六进制}{.pdf|.png|.jpg|.jpeg}`；空键 / 超长 / 绝对路径 / 盘符 / 反斜杠 / 路径穿越 / 目录形态一律拒绝。
- **根目录复核**：解析后再确认位于隔离根目录之内（双层防护），**绝不**做目录扫荡、通配删除、任意路径删除，也**绝不**触碰既有键或已受理证据的内容。
- **内部证据**（`AttachmentEvidenceIntegrityException`）：用户可见响应只使用**安全**文案（不含存储键 / 路径）；
  精确内部证据（归属、提供程序、长度、摘要、存储键、阶段）保存在 `InternalEvidence` 属性，并通过 `ToString()` 追加，
  使服务端日志（异常渲染）完整留存、可恢复、可核对。该异常刻意**不是** `BusinessException`：
  全局异常中间件对非业务异常返回固定通用文案，因此存储键 / 路径**绝不**可能出现在用户可见响应中。
- **不新增**任何存储 / 数据库 / 实体 / 表列：补偿复用既有 `IAttachmentContentStore` 接缝；生产对象存储（OSS）
  仍未实现、未注册、未激活（其激活属生产 OSS Human Gate）。

## 4. 边界

- 不新增菜单 / 角色 / 用户授权，不把空身份当作匿名或管理员，不伪造任何授权；身份只来自已认证请求主体（`ClaimTypes.NameIdentifier`）。
- 不改写归属单据的状态、金额、数量、客户、明细与备注，不生成库存移动，不改动库存 / 财务 / 发票 / 税务 / 费用与结算记录；父单据生命周期与库存来源单据审计保持原样。
- 不清理 / 改派 / 硬删除任何历史证据；作废保留原始元数据与原始原因 / 时间戳；已受理证据的内容永不删除。
- 补偿仅针对「本次登记请求新建且确认回滚」的内容键；不一致 / 不确定时一律保留并留证。

## 5. 测试证据

| 层 | 文件 | 覆盖 |
| --- | --- | --- |
| 单元（内存库 + 测试内存储） | `src/ERP.UnitTests/AttachmentEvidenceMutationTests.cs`（新增） | 登记成功不改写父单据商业字段；父单据已删除 / 越界客户 → 零落库零落盘；元数据写入失败 → **确认回滚后只补偿本次新建键**且无孤儿；补偿失败 → 保留内容 + 内部证据 + 用户文案不含键 / 路径；取消 → 保留可能已提交内容；锁内按真实身份重解析（旧快照不能替代实时菜单）；重复作废不覆盖原始原因与时间戳；作废不存在 / 越界零改写；确定性锁序；隔离本地存储真实文件层面的补偿（单文件、拒绝穿越 / 绝对路径 / 非法形态、越界文件不动、不做目录扫荡、默认实现拒绝删除） |
| 单元（既有回归） | `AttachmentEvidenceTests.cs` 等 | 上传 / 格式 / 大小 / 命名 / 下载 / 作废 / 台账 / 工作台 / 控制器契约全部保留 |
| SQL Server 集成（受控 localdb） | `src/ERP.IntegrationTests/AttachmentEvidenceMutationSqlServerTests.cs`（新增） | **两个独立连接竞态**：① 登记 vs 父单据软删除；② 登记 vs 归属客户变更；确定性顺序：归属越界 / 提交前菜单撤销 → fail closed 零落库零落盘；**并发作废**（恰好一个赢家 + 原始原因 / 时间戳保留 + 历史元数据不可变）；元数据写入失败 → 确认回滚补偿 + 零落库 + 无孤儿；真实成功登记（落库与落盘一致、父单据金额 / 数量 / 客户不变）；既有已受理证据前后计数 / 摘要 / 原始作废原因不变；专用目标护栏 fail-closed |
| 安全 profile | `.ai/config.json` 的 `safe` | `dotnet restore` → Release 构建（`TreatWarningsAsErrors`）→ `ERP.UnitTests` 全量 |

### 5.1 一致性不变量

- **无孤儿内容**：隔离存储内的每个内容文件都必须被某条证据行引用（`AssertNoOrphanContentAsync`）。
- **无幽灵证据**：每条证据引用的内容文件必须真实存在（真实成功登记用例断言 `ContentFileExists`）。
- **父状态不撕裂**：登记 / 作废路径绝不改写父单据金额、押金、客户与明细数量（单元 + 集成断言）。

### 5.2 SQL Server 集成目标护栏

- 实例必须精确为 `(localdb)\NEWERP_AutoAcceptance`，库名前缀必须为 `NEWERP_AUTOTEST`，且必须为 `Integrated Security`；不满足在任何库访问之前直接拒绝（`AssertDedicatedTarget`）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings*.json` / `.env` / 生产凭据。
- 保留既有库存来源单据审计与原始失败日志：集成测试只新增自己的证据行，不清理 / 不删除任何既有行。

### 5.3 验证产物与路径纪律（ERP-409 修复）

- 验证输出只写入**仓库已忽略**的日志文件（`TestResults/*.log`，由 `.gitignore` 的 `*.log` 覆盖）；
  **不**写 `*.trx` 等未被忽略的测试结果产物，`bin/` 与 `obj/` 同样被忽略。
- 因此一次成功的 Release 构建 + 单元 / 集成测试运行只会改动 `allowed_paths` 内的文件，
  不会在允许范围之外留下任何需要提交或清理的产物（这是 ERP-409 修复的核心纪律）。
- 真实 SQL 集成测试只在受控 localdb 实例上创建 `NEWERP_AUTOTEST_*` 数据库（机器上的实例目录），
  **不**在仓库内产生任何数据库文件或数据产物。

## 6. 已知限制 / 阻断

- 真实浏览器验收按任务配置为 `not_required`（未执行，也不声明通过）；本任务不涉及前端 / 样式改动。
- SQL Server 集成测试需要本机可用的 `(localdb)\NEWERP_AutoAcceptance` 实例；本机环境缺失时该文件不构成阶段验收证据（构建通过**不等于**阶段验收）。
- 存储层验收仍只覆盖隔离的非生产本地存储；生产 OSS 未实现、未注册、未激活。
- 补偿只覆盖「本次请求新建的内容键」；对**历史遗留**的孤儿内容不做任何扫描 / 清理（避免任意删除与目录扫荡）。

- 内存库等非关系型提供程序无行锁语义：锁定等价无操作（存在性仍由锁内重新加载判定），事务等价无事务，业务校验口径不变。
