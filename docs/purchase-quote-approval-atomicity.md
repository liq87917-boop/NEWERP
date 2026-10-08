# 供应商比价生命周期与审批决定的原子性 / 并发护栏（ERP-417）

> 任务：`ERP-417`「Freeze supplier comparison commercial terms under atomic approval decisions」（阶段 3 核心业务流程完整性）。
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
   由实时审批生命周期追加的**归属决定**（`CreatedBy` = 登录账号 / 请求决定人）还额外冻结**软删除**（见 6.1）；
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
| `DELETE /{id}` | 已转换 → 拒绝；已存在**归属**审批决定 → 拒绝（保留审批历史） | 软删除，绝不硬删除；未归属历史决定见 6.1 |
| `POST batch-delete` | 全部 Id 升序加锁 + 逐行「未转换且无归属决定」 | 任一不合格即**整批拒绝**，全有或全无 |
| `POST /{id}/to-order` | 比价行锁 + 目的地菜单 / 权威目的地范围 + 既有转换资格 + **批准供应商与当前供应商一致** | 发号 / 落库全部在锁内；被拒不发号 |
| `POST batch-to-order` | 批次全部来源行升序加锁 + 整批归属 + 目的地范围 + 既有分组 / 唯一单号守卫 | 与单行路径共用同一把锁 |

**审批决定（`POST /decide`）**：实时认证请求解析 `LiveActor`（登录账号 Id + 显示名）→ 比价行锁 → 锁内重读实时身份 / 菜单 / 客户范围 →
批次一致 / 陈旧 / 已转换 / 已放弃 / 重复守卫 → 追加**恰好一条**决定（`SelectedSupplierId` / `SelectedSupplierName` 取自锁内权威比价行，
`DecidedBy` / `DecidedByName` 取自登录账号，请求体伪造字段被忽略）→ 提交。append-only：无修改 / 删除 / 重新打开 / 覆盖已批准决定的接口。
决定同时写入**操作人归属** `CreatedBy`（实时认证请求 = 登录账号 Id；无 HTTP 管线的进程内直调回退 `DecidedBy`），
作为「删除冻结」的唯一判据。

### 6.1 删除冻结口径与 ERP-416 兼容（路径护栏内修复）

- **归属决定（attributed decision）**：`PurchaseQuoteMutationRules.IsAttributedDecision` = 决定存在且 `CreatedBy` 为正值
  （实时审批生命周期追加、带操作人归属）。这类决定**冻结**所在比价行的软删除：单条删除与批量删除都拒绝
  （`PurchaseQuoteMutationRules.DecidedNoDeleteText`），失败整体回滚、零部分删除。
- **未归属决定**：`CreatedBy` 为空的历史 / 种子 / 导入数据保留 ERP-416 既有契约——本人行仍可**软删除**（只隐藏比价行），
  `PurchaseQuoteDecision` 审批证据行绝不被删除 / 修改，血缘完整；系统不存在任何硬删除 / 重开 / 覆盖已批准决定的接口。
- **为什么必须这样定界**：ERP-416 的既有单元测试 `src/ERP.UnitTests/PurchaseQuoteAuthorizationTests.cs` **不在本任务 `allowed_paths` 内**，
  其既有断言要求「本人行（含既有决定）可软删除」。若把删除冻结无差别施加到**全部**决定，该断言必然失败；
  而修改该文件会被路径护栏拒绝（上一轮 `path_guard_failure` 的根因）。
  因此本修复把删除冻结精确到**归属决定**：实时审批生命周期产生的新证据不可销毁，同时既有 ERP-416 契约与全量单元套件保持绿灯，
  且不触碰任何 `allowed_paths` 之外的文件。
- **商业条款冻结不区分归属**：拒绝改派 / 价格 / 数量 / 币种 / 选中 / 批次、仅允许备注白名单的口径对**任何**有效决定都生效
  （防止「批准一家供应商却按另一家条款采购」）；删除冻结才需要区分归属（新证据 vs 既有契约）。

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
| 单元（内存库，新增） | `src/ERP.UnitTests/PurchaseQuoteMutationTests.cs` | 锁 / 事务契约（`QuoteRowLockSql`、`MergeLockIds` Id 升序、非关系型等价无操作、状态常量与既有转换口径逐字一致）；待比较草稿校验逐条（正数量 / 非负价格 / EF 精度 / 币种 / 选中与状态一致 / 服务端重算总额）；伪造「已转采购订单」状态 / 采购单号式 `RefOrderNo` / 审计字段拒绝与忽略；已决定行冻结（商业条款 / 改派 / 选中拒绝，仅备注允许）；已转换行冻结；删除 / 批量删除（归属决定冻结、未归属历史决定保留 ERP-416 软删除契约、全有或全无）；重复决定抛 `RuleConflict`；可信操作人覆盖伪造输入；审批通过后转单保留审批参考；控制器接线契约 |
| 单元（内存库，更新） | `src/ERP.UnitTests/PurchaseQuoteApprovalTests.cs` | 既有批准 / 拒绝 / 重复 / 陈旧 / 跨批次 / 批次状态 / 路由契约回归保持通过；新增「实时请求伪造决定人被忽略（含 `CreatedBy` 归属）」「已批准后供应商被改写再转单拒绝」 |
| 单元（既有回归，**未修改**） | `src/ERP.UnitTests/PurchaseQuoteAuthorizationTests.cs` | ERP-416 授权矩阵**原样保持绿灯**：该文件不在 `allowed_paths` 内，本轮修复未做任何改动（上一轮的 `path_guard_failure` 即因修改它）；删除冻结精确到**归属决定**（见 6.1），既有「本人行可软删除」契约不变 |
| SQL Server 集成（受控 localdb，新增） | `src/ERP.IntegrationTests/PurchaseQuoteMutationSqlServerTests.cs` | 真实既有授权 + 真实控制器：**两个独立连接竞态**（审批 vs 拒绝 → 恰好一条决定 / 受控输家；审批 vs 商业编辑 → 批准快照不变或编辑先行条款）；审批 vs 删除恰好一个赢家；混合批量删除全有或全无；伪造决定人被登录账号覆盖；强制保存失败整体回滚（锁刷新 + 新增决定都不落库）；专用目标护栏 fail-closed |
| 安全 profile | `.ai/config.json` 的 `safe` | `dotnet restore` → Release 构建（warnings-as-errors + analyzers）→ `ERP.UnitTests` 全量 |

### 9.1 SQL Server 集成目标护栏

- 目标实例必须精确为 `(localdb)\NEWERP_AutoAcceptance`，库名前缀必须为 `NEWERP_AUTOTEST`，且必须 `Integrated Security=true`；
  错误实例 / 错误库名 / 非集成安全在**访问数据库之前**即被拒绝（`PurchaseQuoteAuthorizationSqlServerFixture.AssertDedicatedTarget`）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何库，也绝不读取 `appsettings` / `.env` / 生产凭据。
- 集成竞态使用两条**独立真实连接**（两个 `ErpDbContext`）+ 同步闸门强制并发；被拒绝 / 回滚的请求只回滚自己的写入，保留既有行。

## 10. 本轮验证证据（safe profile + 受控 localdb 集成）

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

> 构建完成不等于阶段验收：只有上述受控 localdb 上真实执行通过、或给出准确 blocker，才构成阶段验收证据。

