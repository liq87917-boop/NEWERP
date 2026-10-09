# 供应商比价生命周期与审批决定的原子性 / 并发护栏（ERP-417 / ERP-419）

> 任务：`ERP-417`「Freeze supplier comparison commercial terms under atomic approval decisions」、
> `ERP-419`「Preserve historical supplier approval deletion freeze without actor metadata exemption」（阶段 3 核心业务流程完整性）。
> 关联：`ERP-416`（比价与审批实时授权，`docs/purchase-quote-authority.md`）、`ERP-095`（比价审批决定 append-only）、
> `ERP-020` / `ERP-027`（比价 → 采购订单带入预填 / 单行与批次转单）、
> `ERP-395`（父单证行锁协议，`TradeDocumentMutationRules`）、`ERP-097`（业务员客户数据范围）。

## 1. 背景与问题证据（修复前）

| 入口 | 修复前行为 |
| --- | --- |
| `POST /api/purchase/quotes` | 通用 CRUD 直接落库：数量 / 单价 / 总额 / 币种 / 状态 / 选中随意写入，可伪造「已转采购订单」状态与 `RefOrderNo` 采购单号「转换证据」，也可伪造主键 / 审计 / 软删除字段 |
| `PUT /api/purchase/quotes/{id}` | 未加锁、未复核审批决定：**已批准选中供应商之后**仍可改写供应商 / 价格 / 数量 / 币种 / 选中状态 / 批次 / 客户（可「批准一家、采购另一家」） |
| `DELETE /{id}`、`POST batch-delete` | 可删除**已存在审批决定**或**已转采购订单**的比价行，破坏审批历史；批量删除逐行 `SaveChanges`，可能部分写入 |
| `POST /api/purchase/quote-decisions/decide` | 无事务 / 无行锁：并发「批准 vs 拒绝」可能双写或依赖唯一索引抛原始异常；`DecidedBy` / `DecidedByName` **直接采信请求体**（可伪造决定人） |
| `POST /{id}/to-order`、`POST batch-to-order` | 转换与修改 / 删除 / 审批互不串行；`BuildDraftAsync` 只校验决定为已批准，**不校验当前供应商是否等于已批准选中供应商** |

## 2. 权威实现

| 层 | 文件 | 职责 |
| --- | --- | --- |
| 共享护栏（新增） | `src/ERP.Application/Services/PurchaseQuoteMutationRules.cs` | 比价行锁 + 原子事务 + 待比较草稿校验（正数量 / 非负价格 / EF 精度 / 受支持币种 / 选中与状态一致 / 服务端重算总额）+ 伪造转换证据拒绝 + 已决定行仅备注白名单 |
| 授权（扩展） | `src/ERP.Application/Services/PurchaseQuoteAuthorizationRules.cs` | 新增 `LiveActor` / `EnsureLiveActorAsync`：审批决定人一律取自登录账号 |
| 控制器 | `src/ERP.Api/Controllers/PurchaseQuoteController.cs` | 新增 / 修改 / 删除 / 批量删除 / 单行转单 / 批次转单全部落在「比价行锁 + 原子事务 + 锁内权威重读」内 |
| 审批决定 | `src/ERP.Api/Controllers/PurchaseQuoteApproval.cs` | `DecideAsync(db, request, actor)`：行锁内复核 + 追加**恰好一条**决定 + 事务提交；新增 `EnsureApprovedSupplierCoherentAsync` |
| 审批入口 | `src/ERP.Api/Controllers/PurchaseQuoteDecisionController.cs` | 实时认证请求解析可信操作人并传入；伪造决定人字段被忽略 |
| 请求模型 | `src/ERP.Api/Controllers/PurchaseQuoteDecisionModels.cs` | 明确 `DecidedBy` / `DecidedByName` 在实时请求下被忽略 |

## 3. 锁协议

| 项 | 口径 |
| --- | --- |
| 唯一来源锁 | `SELECT Id FROM db_owner.PurchaseQuotes WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}`（`PurchaseQuoteMutationRules.QuoteRowLockSql`，契约 / 文档同源） |
| 锁实现 | 仅用 EF Core 基础 API：在调用方事务内对比价行发一条「审计时间戳刷新」`UPDATE`（`UpdatedAt` 是技术审计字段、不是商业证据），取得排它 X 锁，持有至事务结束；语义等价 `UPDLOCK, HOLDLOCK`。并发方先提交使本地乐观令牌 `RowVersion` 过期时重读权威行后**有界重试**（`RowLockRetryAttempts = 8`） |
| 共用同一把锁 | 修改 / 删除 / 批量删除 / 审批决定 / 单行转单 / 批次转单都取同一把比价行锁（转换不再与生命周期变更竞态） |
| 确定性锁序 | 批量删除与批次转单按比价行 **Id 升序**确定性加锁（`MergeLockIds` 去重、仅正整数）；绝不反向获取其它锁，因此不存在锁环 |
| 原子事务 | `BeginMutationTransactionAsync`：关系型后端开启真实事务，任一步失败整体回滚；调用方已开启事务时**绝不嵌套**；内存库等非关系型提供程序等价无事务（声明式校验不变） |
| 失败即回滚 | 行锁的 `UpdatedAt` 刷新、字段改写、软删除、新增决定、转换来源留痕与生成的采购订单都在同一事务内；失败侧整体回滚（集成测试以「强制保存失败」与竞态断言零部分写入） |

## 4. 锁内权威复核

取得比价行锁之后，控制器 / 审批逻辑**重新加载**比价行（绝不复用加锁前的内存实体），逐项复核：

1. **实时身份 / 既有菜单 / 权威客户范围**（`EnsureAccessAuthorizedAsync` + `EnsureQuoteAllowedAsync`）在锁内重读：授权撤销 / 账号停用 / 范围变化在锁内立即收敛；
2. **存在 / 未删除**：锁返回 `false` 或锁内重读为空一律按「比价记录不存在」非披露拒绝；
3. **已转换冻结**：持久化状态为「已转采购订单」时，修改 / 删除 / 批量删除 / 再审批一律拒绝；
4. **已决定冻结**：存在有效审批决定时，仅允许安全非商业备注修改，其余商业条款 / 改派 / 批次 / 选中状态变更一律拒绝；
   任何有效审批决定（已批准 / 已拒绝）都额外冻结**软删除**——与操作人归属无关（见 6.1）；
5. **重复决定**：追加前锁内重读「是否已有有效决定」，并以 `UX_PurchaseQuoteDecisions_QuoteId`（`IsDeleted = 0` 过滤唯一索引）作并发兜底；`DbUpdateException` 转为可读的 `RuleConflict`（受控输家）。

## 5. 对比草稿校验与伪造拒绝（服务端唯一权威）

| 项 | 口径 |
| --- | --- |
| 批次号 | 去空白后非空且 ≤ 50 字符 |
| 数量 | 必须为正且可在 `DECIMAL(18,4)` 内表示（≤ 4 位小数、18 位有效数字；`decimal` 本身不含 NaN / Infinity） |
| 单价 | 必须为非负且可在 `DECIMAL(18,4)` 内表示 |
| 币种 | 仅接受既有口径 `CNY` / `USD`（归一化为大写；不引入汇率换算） |
| 选中 / 状态 | 状态仅接受 待比较 / 已选中 / 已放弃；`IsSelected` 与「状态 = 已选中」必须一致 |
| 总额 | **服务端按「单价 × 数量」在 EF 精度重算**并覆盖客户端 `TotalAmount`（绝不采信客户端总额） |
| 伪造「已转采购订单」 | 客户端提交 `Status = 已转采购订单` 一律 `InvalidParameter`（该状态只由转换路径写入） |
| 伪造 `RefOrderNo` 转换证据 | `RefOrderNo` 指向既有未删除采购单号即 `RuleConflict`（转换前该列仅表示关联销售订单号） |
| 伪造审计字段 | 新增时主键 / `CreatedAt` / `CreatedBy` / `UpdatedAt` / `UpdatedBy` / `IsDeleted` / `RowVersion` 一律由服务端重置；修改时只写可编辑商业字段，绝不触碰审计 / 主键 / 并发令牌 |
| 可选主数据引用 | `ProductId` / `SupplierId` / `CustomerId` 保持可选，**绝不**自动创建主数据或臆造来源身份；归属客户仍须落在实时客户数据范围内 |

## 6. 生命周期与守卫矩阵（锁内判定）

| 路由 | 额外守卫（锁内） | 备注 |
| --- | --- | --- |
| `POST /` | 实时授权 + 拟议客户范围 + 对比草稿校验 + 伪造转换证据拒绝 | 单一 `SaveChanges`（原子），服务端重置审计并重算总额 |
| `PUT /{id}` | 已存 + 拟议归属；已转换 → 拒绝；已决定 → **仅备注白名单**；否则草稿校验后整体替换商业字段 | 被拒整体回滚，零部分写入 |
| `DELETE /{id}` | 已转换 → 拒绝；已存在**任何有效审批决定**（已批准 / 已拒绝，与操作人归属无关）→ 拒绝（保留审批历史） | 软删除，绝不硬删除；历史 / 缺失操作人元数据见 6.1 |
| `POST batch-delete` | 全部 Id 升序加锁 + 逐行「未转换且无有效决定」 | 任一行不合格（含历史决定）即**整批拒绝**，全有或全无 |
| `POST /{id}/to-order` | 比价行锁 + 目的地菜单 / 权威目的地范围 + 既有转换资格 + **批准供应商与当前供应商一致** | 发号 / 落库全部在锁内；被拒不发号 |
| `POST batch-to-order` | 批次全部来源行升序加锁 + 整批归属 + 目的地范围 + 既有分组 / 唯一单号守卫 | 与单行路径共用同一把锁 |

**审批决定（`POST /decide`）**：实时认证请求解析 `LiveActor`（登录账号 Id + 显示名）→ 比价行锁 → 锁内重读实时身份 / 菜单 / 客户范围 →
批次一致 / 陈旧 / 已转换 / 已放弃 / 重复守卫 → 追加**恰好一条**决定（`SelectedSupplierId` / `SelectedSupplierName` 取自锁内权威比价行，
`DecidedBy` / `DecidedByName` 取自登录账号，请求体伪造字段被忽略）→ 提交。append-only：无修改 / 删除 / 重新打开 / 覆盖已批准决定的接口。
决定同时写入**操作人归属** `CreatedBy`（实时认证请求 = 登录账号 Id；无 HTTP 管线的进程内直调回退 `DecidedBy`），
仅用于审计溯源；删除冻结判据是「该比价行存在任何有效决定」，与 `CreatedBy` 是否缺失无关（见 6.1）。

### 6.1 删除冻结口径（ERP-419：与操作人归属无关）

- **唯一判据**：`PurchaseQuoteMutationRules.IsDecisionFreezingDeletion` = 存在一条**未删除**且决定为
  `Approved` / `Rejected` 的持久化决定（口径常量 `ApprovedDecision` / `RejectedDecision` 与
  `PurchaseQuoteApproval.Approved` / `Rejected` 逐字一致，契约测试断言同源）。
  只要命中即**冻结**所在比价行的软删除：单条删除与批量删除都拒绝（`PurchaseQuoteMutationRules.DecidedNoDeleteText`），
  失败整体回滚、零部分删除。
- **与 `CreatedBy` 无关**：`CreatedBy` 为空 / 为零（历史 / 种子 / 导入数据）只表示**未知归属**，
  **绝不**构成删除已批准 / 已拒绝来源的许可；缺失的操作人元数据原样保留，绝不回溯臆造操作人、也绝不自动修复审计。
- **为什么必须这样定界（ERP-419 修复 ERP-417 缺陷）**：ERP-417 曾以「归属决定」（`CreatedBy > 0`）为删除冻结判据，
  结果「有效批准决定 + 缺失创建人元数据」的历史来源可被软删除（审批来源消失），违背「审批历史不可销毁」。
  ERP-419 删除该豁免：删除冻结对**任何**有效决定生效。
- **ERP-416 授权夹具的冲突前提已修正**：`src/ERP.UnitTests/PurchaseQuoteAuthorizationTests.cs` 的原始夹具把
  「本人可删除」的断言压在一条**带批准决定**的本人行上（`AddQuote` 默认 `selected: true` 会追加无 `CreatedBy` 的批准决定）。
  ERP-419 在本任务 `allowed_paths` 内把该夹具修正为：**允许的本人删除必须是真正未决定 / 未转换的草稿**，
  同时显式断言「已批准历史（缺失创建人）删除被拒且来源与决定保留」。
  ⚠️ **ERP-417 曾两度失败**：attempt1 因越界修改该文件触发 `path_guard_failure`；
  attempt2 回退夹具改动、改用 `IsAttributedDecision` 豁免历史决定，导致上述业务完整性缺陷。本轮两者都修：夹具在允许路径内修正，且不再有归属豁免。
- **商业条款冻结口径不变**：拒绝改派 / 价格 / 数量 / 币种 / 选中 / 批次、仅允许备注白名单的口径对**任何**有效决定都生效
  （防止「批准一家供应商却按另一家条款采购」）；真正未决定 / 未转换的草稿仍可修改与软删除。

## 7. 转换一致性（比价 → 采购订单）

- 转换与生命周期变更 / 删除 / 审批决定共用同一把比价行锁：并发的「审批 vs 转换 / 转换 vs 删除」被串行化在同一事务内。
- 转换在**锁内权威重读**比价行之后，除既有「已选中 + 已批准 + 未转换 + 未放弃 + 已维护供应商」守卫外，
  新增 `PurchaseQuoteApproval.EnsureApprovedSupplierCoherentAsync`：当前持久化供应商必须等于已批准决定的选中供应商，
  否则拒绝（杜绝「批准一家供应商却采购另一家」）。该判定在既有资格守卫**之后**执行，保持既有提示口径不变。
- 生成采购订单、来源留痕（状态 `已转采购订单` + `RefOrderNo` 采购单号 + 备注来源标记）与既有审批参考保留都在同一事务内；
  被拒不消耗单据号、不落半成品。
- 转换只新增采购订单，**不**改写比价行的审批决定历史，**不**做任何库存 / 财务过账。

## 8. 边界

- 不新增菜单 / 角色 / 用户授权 / 表 / 列 / 索引；不把空身份当作匿名或管理员，审批决定人一律来自既有登录账号（无匿名 / 管理员降级）。
- 不移除既有 `UX_PurchaseQuoteDecisions_QuoteId` 唯一索引（作为并发兜底），也不要求任何新迁移。
- 行锁只刷新比价行技术审计时间戳 `UpdatedAt`，不构成对商业字段的静默改写。
- 不改写审批决定历史（append-only）、采购订单、客户 / 商品 / 供应商主数据、库存与库存流水、发票 / 退税 / 费用或财务记录，不删除历史证据。
- 保留库存来源单据审计与原始失败日志：被拒绝 / 回滚的请求只回滚自己的写入，不清理 / 不删除任何既有行。

## 9. 测试证据

| 层 | 文件 | 覆盖 |
| --- | --- | --- |
| 单元（内存库，新增） | `src/ERP.UnitTests/PurchaseQuoteMutationTests.cs` | 锁 / 事务契约（`QuoteRowLockSql`、`MergeLockIds` Id 升序、非关系型等价无操作、状态常量与既有转换口径逐字一致）；待比较草稿校验逐条（正数量 / 非负价格 / EF 精度 / 币种 / 选中与状态一致 / 服务端重算总额）；伪造「已转采购订单」状态 / 采购单号式 `RefOrderNo` / 审计字段拒绝与忽略；已决定行冻结（商业条款 / 改派 / 选中拒绝，仅备注允许）；已转换行冻结；删除 / 批量删除（任何有效批准 / 拒绝决定都冻结软删除，含缺失 / 零 `CreatedBy` 的历史决定；全有或全无；真正未决定草稿仍可删除）；重复决定抛 `RuleConflict`；可信操作人覆盖伪造输入；审批通过后转单保留审批参考；控制器接线契约 |
| 单元（内存库，更新） | `src/ERP.UnitTests/PurchaseQuoteApprovalTests.cs` | 既有批准 / 拒绝 / 重复 / 陈旧 / 跨批次 / 批次状态 / 路由契约回归保持通过；新增「实时请求伪造决定人被忽略（含 `CreatedBy` 归属）」「已批准后供应商被改写再转单拒绝」；ERP-419 新增「历史批准决定缺失创建人 → 删除仍被拒且来源 / 决定保留」 |
| 单元（内存库，**本轮按 ERP-419 修正夹具**） | `src/ERP.UnitTests/PurchaseQuoteAuthorizationTests.cs` | ERP-416 授权矩阵（范围外 / 空归属 / 已删除 / 无身份 / 已删除 / 已禁用 / 无菜单 / 混入范围外整批拒绝等）语义原样保持绿灯；ERP-419 修正「本人行可删除」的冲突前提：允许的本人删除改用**真正未决定 / 未转换草稿**，并显式断言「已批准历史（缺失创建人）删除被拒且来源 / 决定保留」（不再以 `CreatedBy` 空 / 零作为放行许可） |
| SQL Server 集成（受控 localdb，新增） | `src/ERP.IntegrationTests/PurchaseQuoteMutationSqlServerTests.cs` | 真实既有授权 + 真实控制器：**两个独立连接竞态**（审批 vs 拒绝 → 恰好一条决定 / 受控输家；审批 vs 商业编辑 → 批准快照不变或编辑先行条款）；审批 vs 删除恰好一个赢家；混合批量删除全有或全无；伪造决定人被登录账号覆盖；强制保存失败整体回滚（锁刷新 + 新增决定都不落库）；**ERP-419**：有效批准 / 拒绝历史决定（`CreatedBy` 空 / 零）单条删除一律被拒且来源 / 决定 / 创建人元数据原样；两条独立连接并发删除无赢家、第三条连接确认已批准来源未消失；混入历史决定行的批量删除整批回滚；未决定未转换草稿仍可软删除；专用目标护栏 fail-closed |
| SQL Server 集成（受控 localdb，ERP-418 / ERP-419） | `src/ERP.IntegrationTests/PurchaseQuoteConversionMutationSqlServerTests.cs` | 单行 / 批次转换竞态与受控拒绝回归；**ERP-419**：并发「转换 vs 历史删除尝试」不再出现删除赢家——转换放行并落订单血缘，删除受控拒绝，来源、审批决定与订单都不消失 |
| 安全 profile | `.ai/config.json` 的 `safe` | `dotnet restore` → Release 构建（warnings-as-errors + analyzers）→ `ERP.UnitTests` 全量 |

### 9.1 SQL Server 集成目标护栏

- 目标实例必须精确为 `(localdb)\NEWERP_AutoAcceptance`，库名前缀必须为 `NEWERP_AUTOTEST`，且必须 `Integrated Security=true`；
  错误实例 / 错误库名 / 非集成安全在**访问数据库之前**即被拒绝（`PurchaseQuoteAuthorizationSqlServerFixture.AssertDedicatedTarget`）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何库，也绝不读取 `appsettings` / `.env` / 生产凭据。
- 集成竞态使用两条**独立真实连接**（两个 `ErpDbContext`）+ 同步闸门强制并发；被拒绝 / 回滚的请求只回滚自己的写入，保留既有行。

## 10. ERP-417 验证证据（原始记录，保留）

| 步骤 | 命令 | 结果 |
| --- | --- | --- |
| 还原 | `dotnet restore NEWERP.sln` | 成功（所有项目均是最新的，无法还原） |
| Release 构建（warnings-as-errors + analyzers，no-incremental） | `dotnet build NEWERP.sln -c Release --no-restore --no-incremental /p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true` | **0 警告 / 0 错误** |
| 全量单元套件 | `dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build --verbosity minimal` | **6398 通过 / 0 失败 / 0 跳过**（含 ERP-416 `PurchaseQuoteAuthorizationTests` 原样绿灯） |
| 本任务单元筛选 | 同上 + `--filter "FullyQualifiedName~PurchaseQuote"` | **139 通过 / 0 失败** |
| 真实 SQL 集成（受控 localdb） | `dotnet test src/ERP.IntegrationTests/ERP.IntegrationTests.csproj -c Release --no-build --filter "FullyQualifiedName~PurchaseQuoteMutationSqlServerTests|FullyQualifiedName~PurchaseQuoteAuthorizationSqlServerTests"` | **18 通过 / 0 失败**（本任务新增 11 + ERP-416 既有 7） |

- 集成目标为精确 `(localdb)\NEWERP_AutoAcceptance` + `Integrated Security` + 全新 GUID 后缀 `NEWERP_AUTOTEST` 库；
  只创建、不 drop / reset / 复用任何既有库，也未读取 `appsettings*.json` / `.env` / 生产凭据。
- **原始失败日志与修复前证据全部保留**：上一轮 `path_guard_failure` 的完整日志仍在 `.ai/logs/ERP-417-attempt-1.jsonl`，
  受保护工作副本在 `.ai/logs/ERP-417-preserved-work.diff`；本轮未清理、未删除任何既有行或日志。
- 未使用生产凭据 / 生产数据，未执行任何破坏性数据操作；未提交 / 未推送（Git 与检查点由编排器负责）。
- 真实浏览器验收按任务配置 `browser_acceptance.required = false`（未执行，也不声明通过）。

- 全量单元套件 6398 通过是 ERP-417 的原始记录：它与 ERP-417 的 `IsAttributedDecision` 归属豁免一起被 ERP-419 判定为**业务完整性缺陷**
  （有效批准决定 + 缺失创建人元数据的历史来源可被软删除）。原始日志 / 工作副本原样保留在 `.ai/logs/ERP-417-*`
  与 `.ai/logs/ERP-419-prepared/original/`（本轮未清理、未改写、未删除）。

> 构建完成不等于阶段验收：只有上述受控 localdb 上真实执行通过、或给出准确 blocker，才构成阶段验收证据。

## 11. ERP-419 验证证据（本轮：safe profile + 受控 localdb 集成）

| 步骤 | 命令 | 结果 |
| --- | --- | --- |
| Release 构建（warnings-as-errors + analyzers，no-incremental） | `dotnet build NEWERP.sln -c Release --no-incremental /p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true` | **0 警告 / 0 错误** |
| 全量单元套件 | `dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build --verbosity minimal` | **6425 通过 / 0 失败 / 0 跳过**（日志 `erp419-fullunits.log`） |
| 本任务单元筛选 | 同上 + `--filter "FullyQualifiedName~PurchaseQuote"` | **166 通过 / 0 失败** |
| 删除冻结真实 SQL（受控 localdb） | `dotnet test src/ERP.IntegrationTests/ERP.IntegrationTests.csproj -c Release --no-build --filter "FullyQualifiedName~PurchaseQuoteMutationSqlServerTests"` | **18 通过 / 0 失败**（日志 `erp419-sql-mutation.log`） |
| 转换 / 授权真实 SQL（受控 localdb） | 同上 + `--filter "FullyQualifiedName~PurchaseQuoteConversionMutationSqlServerTests\|FullyQualifiedName~PurchaseQuoteAuthorizationSqlServerTests"` | **25 通过 / 0 失败**（日志 `erp419-sql-conv-auth.log`） |

- 三个测试类各自创建**一个全新 GUID 库**：`NEWERP_AUTOTEST_PQSOURCEAUTH_7b9cbb91…`（2026-10-09 07:56:13）、
  `NEWERP_AUTOTEST_PQSOURCEAUTH_2713a453…` / `_0031f1a6…`（07:56:43）；`sqlcmd` 只读核验：91 张表、
  `db_owner.PurchaseQuotes` 24 行、`db_owner.PurchaseQuoteDecisions` 18 行 → 真实 SQL 确实执行（非内存模拟）。
- 目标为精确 `(localdb)\NEWERP_AutoAcceptance` + `Integrated Security` + 全新 GUID 后缀 `NEWERP_AUTOTEST` 库；
  只创建、不 drop / reset / 复用任何既有库，未读取 `appsettings*.json` / `.env` / 生产凭据，未执行任何生产数据库变更。
- 未使用既有授权之外的新授权：真实既有菜单（`purchase-quote` / `purchase-order`）+ 业务员客户数据范围驱动真实控制器，
  无匿名 / 管理员降级、无测试身份旁路；操作人归属仍只取实时认证账号。
- **原始失败与历史证据全部保留**：ERP-417 两次尝试、`.ai/logs/ERP-419-prepared/original/…` 原样保留，本轮未删除 / 未改写任何既有行或日志。
- 未提交 / 未推送（Git 与检查点由编排器负责）；真实浏览器验收按任务配置 `browser_acceptance.required = false`（未执行，也不声明通过）。

> 构建完成不等于阶段验收：本任务 `completion_mode = build`，因此**不声明完整阶段验收**，仅交付上述可复现证据
> （受控 localdb 真实执行通过 + 全量单元绿灯 + Release 构建零告警）。

