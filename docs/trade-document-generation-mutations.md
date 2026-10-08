# 来源单据生成单证的原子性护栏说明（ERP-397）

- 任务：`ERP-397`「Make source trade document generation atomic against duplicate requests and source invalidation」
- 依赖：`ERP-394`（实时授权与权威客户范围）、`ERP-395`（表头 / 明细行共享父单证行锁）、`ERP-396`（明细行编辑上下文）
- 权威实现：
  - `src/ERP.Application/Services/TradeDocumentGenerationMutationRules.cs`（**新增**：确定性来源行锁 + 原子事务 + 锁内权威复核 + 权威快照）
  - `src/ERP.Api/Controllers/TradeDocumentGeneration.cs`（`GenerateAtomicAsync`：锁内重读 → 重复检测 → 编号预约 → 表头 / 明细行构造 → 写入）
  - `src/ERP.Api/Controllers/SalesOrderController.cs`（销售订单生成入口 + 锁内权威重读 `ReloadSalesOrderSourceAsync`）
  - `src/ERP.Api/Controllers/ContainerLoadingListController.cs`（装柜清单生成入口 + 锁内权威重读 `ReloadLoadingListSourceAsync`；清单行锁语句与写路由共用同一常量）
  - `src/ERP.Application/Services/TradeDocumentMutationRules.cs`（`RowLockRetryAttempts`：与 ERP-395 同口径的有界重试）
  - `src/ERP.Application/Services/TradeDocumentAuthorizationRules.cs`（`EnsureLockedSourceScopeAllowed`：锁内来源客户实时范围复核）

## 1. 改造前的竞态（问题）

改造前 `TradeDocumentGeneration.GenerateAsync` 的顺序是「**先判定、后加锁**」：

1. 控制器先按路由**读取来源单据**（销售订单 / 装柜清单）、客户档案、来源明细与商品资料，并据此构造单证草稿与明细行快照；
2. 再在 `GenerateAsync` 内做重复生成检测（`FindExistingAsync`）、单证编号守卫（`EnsureUniqueDocNoAsync`）；
3. 最后才开启事务写库（表头 1 次 `SaveChanges` + 明细行 1 次 `SaveChanges`）。

由此产生的窗口：

- **并发等价请求**：两条同来源同类型的请求可以各自通过重复检测与编号守卫，落出**两套重复单证**（`DocNo` 无唯一索引，无法兜底）；
- **来源失效**：来源在「控制器读取」与「写库」之间被并发取消 / 删除，生成仍会落库，产生**以已失效来源为依据的单证**；
- **来源改写**：来源金额 / 数量 / 单位在读取与写入之间被并发改写，生成可能落出**陈旧或撕裂**的表头 / 明细行证据。

## 2. 生成协议（确定性来源行锁 + 原子事务 + 锁内权威复核）

| 环节 | 口径 |
| --- | --- |
| 确定性来源行锁 | `TradeDocumentGenerationMutationRules.LockSourceRowAsync`：先对**来源单据行**加排它行锁。销售订单行锁语句与销售订单取消 / 出库审核 / 预装柜流程**同一常量**（`PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql`）；装柜清单行锁语句与装柜清单修改 / 提交 / 审核 / 取消 / 删除 / 参与方维护**同一常量**（`LoadingListRowLockSql`，控制器写路由共用） |
| 锁实现 | 与 ERP-395 同源：仅用 EF Core 基础 API，在调用方事务内对来源行发一条「审计时间戳刷新」`UPDATE`（`UpdatedAt` 为技术审计字段），取得排它行锁（X 锁，持有至事务结束）；语义等价 `SELECT ... WITH (UPDLOCK, HOLDLOCK)`。并发方先提交使本地 `RowVersion` 过期时 `DbUpdateConcurrencyException` → 重读权威行后**有界重试**（`RowLockRetryAttempts = 8`），纯锁等待绝不被误报成业务拒绝 |
| 确定性锁定键 | `SourceLockKey(sourceType, sourceId)`（来源行锁键）与 `GenerationKey(sourceType, sourceId, docType)`（`来源 + 类型` 生成键）：同一来源 / 同一「来源 + 类型」的并发请求映射到同一个键 |
| 原子事务 | `BeginGenerationTransactionAsync` 复用 ERP-395 `BeginMutationTransactionAsync`：关系型后端开启真实事务；已在事务内**绝不嵌套**；内存库等价无事务（单元测试口径） |
| 锁内权威重读 | 取得来源行锁之后**重新读取来源单据**（`TradeDocumentGenerationSource`：来源身份 + 状态 + 客户 + 金额 + 明细数量 / 单位 + 绑定到本次重读实体的草稿 / 明细行构造委托）；**绝不**复用加锁前的内存快照 |
| 锁内复核 | 来源身份与本次请求一致（不一致 `NotFound`）、来源未作废（已作废 `RuleConflict`）、来源客户在实时范围内（越界 `Forbidden`）、表头客户 / 金额与明细行数量 / 单位必须来自本次权威重读（不一致 `RuleConflict` 并整体回滚） |
| 写入 | 重复检测 → 单证编号预约 → 表头与明细行构造全部在锁内完成，然后**同一事务**分两次 `SaveChanges`（明细行需要父单证 Id）；任一步失败整体回滚 |
| 锁序 | 来源单据行 → 新建单证行（`INSERT`，不取既有单证行锁）。来源生成不取父单证行锁，ERP-395 表头 / 明细行写路由也不取来源行锁，两者无锁环；销售订单取消（订单行）与装柜清单取消（预装柜单行 → 装柜清单行）同为「来源行先行」，同样不构成环 |

## 3. 幂等与冲突语义（保持不变）

| 场景 | 结果 |
| --- | --- |
| 同一来源 + 同一类型并发 / 重复请求 | 锁内串行化：唯一赢家写入**完整一套**（表头 + 全部明细行）；其余请求在锁内检测到重复 → `RuleConflict`（文案与改造前一致：「该…已生成 …，不能重复生成；…」），**零新增** |
| 请求含已生成 + 未生成类型 | **整体拒绝**：未生成类型一张也不落库（无部分生成） |
| 单证编号被历史单证占用 | 锁内确定性回退（`-序号`，如 `PL-X-2`）：绝不覆盖 / 改写既有单证，也绝不产生重复 `DocNo` |
| 不相关来源 | 互不影响：各自的行锁独立，仍可正常生成 |
| 生成后人工维护 | 既有行为不变：单证状态「待制作」，明细行可在单证中心继续维护；`POST /api/trade/documents`（新增 / 复制式登记）语义不变，不做来源重复守卫 |

## 4. 权威复核明细（锁定实时身份与来源事实）

- **状态**：锁内 `Status == Cancelled` → 拒绝（`已作废的{销售订单 / 装柜清单}不能生成单证`），与生成前预检同文案；并发「取消先行提交」时生成必然被拒。
- **客户**：锁内重读的来源 `CustomerId` 必须是受限账号实时范围内的客户（复用 ERP-097 权威客户范围）；生成目标单证的 `CustomerId` 必须等于该权威客户，**绝不按 `RefNo` / `SalesOrderNo` 等自由文本推断归属**。
- **金额**：表头 `Amount` 必须等于锁内权威金额（销售订单 = 订单总额；装柜清单 = 0）。
- **数量 / 单位**：明细行数量合计必须等于锁内权威来源明细数量合计；每行单位必须落在锁内权威单位集合（来源明细单位 + 其引用商品资料单位）内。
- **快照不可变**：明细行快照仍是一次构造、一次写入；生成不改写任何既有明细行，也不回写表头金额。

## 5. 路由矩阵

| 路由 | 锁与事务 |
| --- | --- |
| `POST /api/sales-orders/{id}/trade-documents` | 生成前预检（存在 / 未作废 / 来源客户范围）→ 来源订单行锁 → 锁内权威重读 → 重复检测 → 编号预约 → 表头 / 明细行构造 → 同一事务写入 → 提交 / 回滚 |
| `POST /api/container/loading-lists/{id}/trade-documents` | 同上（来源清单行锁；共享装柜的多客户参与方口径不变，权威客户仍取清单兼容客户字段） |
| `GET .../trade-documents/prefill` | 只读、不落库、不加锁（既有行为不变） |
| `POST /api/trade/documents`、`PUT`、`DELETE`、`batch-delete`、明细行写路由 | 既有 ERP-395 父单证行锁协议不变（本任务不改写） |

## 6. 并发与竞态矩阵（两个独立连接，真实 SQL）

| 竞态 | 确定性不变式 |
| --- | --- |
| 同来源同类型并发生成（销售订单 / 装柜清单） | 唯一赢家写入完整一套（表头 + 明细行）；另一侧合法冲突拒绝，零新增、无孤儿明细行、无重复 `DocNo` |
| 来源取消 vs 生成 | 取消必成功；生成只可能**在取消提交前**获得来源行锁并落出完整一套，取消提交后一律被拒（零单证） |
| 来源编辑（数量 / 金额）vs 生成 | 表头金额与明细行数量必须来自**同一个**已提交版本（20/10 或 60/30），绝不撕裂 |
| 编号被历史单证占用时并发生成 | 锁内确定性回退到 `-2`，唯一一套，全局无重复编号，历史单证原样保留 |
| 多类型请求含已生成类型 | 整体拒绝，未生成类型零落库 |
| 受限 / 越界来源 | 生成前预检与锁内复核都 fail closed，零落库、不消耗单证编号 |

## 7. 边界

- 不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权；无匿名 / 管理员兜底，仍复用既有 `doc-center` 菜单与 ERP-097 权威客户范围。
- 不改写来源销售订单 / 装柜清单的商业字段、商品资料、库存与库存流水、发票 / 退税 / 费用 / 财务记录，不做财务 / 库存过账。
- 行锁只刷新来源单据的技术审计时间戳 `UpdatedAt`（非商业证据，不参与任何金额 / 数量 / 状态判定）；被拒绝的请求不改写任何业务字段与历史证据。
- 保留既有库存来源单据审计与原始失败日志：测试只新增自己的证据行，不清理 / 不删除任何既有行。
- 真实浏览器验收按任务配置为 `not_required`（未执行，也不声明通过）。

## 8. 测试证据

| 层 | 文件 | 覆盖 |
| --- | --- | --- |
| 单元（内存库） | `src/ERP.UnitTests/TradeDocumentGenerationMutationTests.cs`（新增） | 确定性锁定键 / 锁语句与既有取消、装柜写路由同一把锁；来源作废 / 身份不一致 / 陈旧金额 / 陈旧数量 / 单位不符 fail closed；控制器原子生成完整落库（销售订单 / 装柜清单）、未知类型与已作废 / 已删除 / 越界来源零落库、重复生成整体拒绝、编号确定性回退、不相关来源可用、写入失败向上传播；接线契约（加锁 → 重读 → 重复检测 → 构造 → 写入顺序，不新增授权、不改 schema） |
| 既有回归 | `TradeDocumentGenerationTests.cs` / `TradeDocumentLineSnapshotTests.cs` / `TradeDocumentPrintTests.cs` / `TradeDocumentMutationTests.cs` / `TradeDocumentItemTests.cs` / `TradeDocumentAuthorizationTests.cs` | 生成字段映射 / 编号 / 重复守卫 / 明细行快照与打印 / 表头与明细行并发护栏 / 授权口径保持不变 |
| SQL Server 集成（受控 localdb） | `src/ERP.IntegrationTests/TradeDocumentGenerationMutationSqlServerTests.cs`（新增） | **两个独立连接竞态**：同来源同类型并发生成（销售订单 / 装柜清单）、来源取消 vs 生成、编号占用下的确定性回退、来源编辑 vs 生成不撕裂；多类型整体拒绝；真实既有 `doc-center` 授权；专用目标护栏 fail-closed |
| 安全 profile | `.ai/config.json` 的 `safe` | Release 构建（`TreatWarningsAsErrors` + 分析器）→ `ERP.UnitTests` 全量 |

### 8.1 SQL Server 集成目标护栏

- 实例必须精确为 `(localdb)\NEWERP_AutoAcceptance`，库名前缀必须为 `NEWERP_AUTOTEST`，且必须为 `Integrated Security`；不满足在任何库访问之前直接拒绝（`AssertDedicatedTarget`）。
- 每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings*.json` / `.env` / 生产凭据。

> 构建完成不等于阶段验收：只有受控 localdb 上真实执行通过、或给出准确 blocker，才构成阶段验收证据。
