# 供应商比价「实时主数据引用」护栏（ERP-476）

## 1. 背景与目标

供应商比价（**PurchaseQuote**，`api/purchase/quotes`）是「采购需求 → 多家供应商比价 → 选中供应商 → **采购订单**」
链上的承诺来源：同一 `QuoteNo` 归为一批，每行 = 一家供应商对某需求的报价，选中 + 审批后可转采购订单。
此前的写入路径由多个护栏分层保护：

| 护栏 | 保护维度 |
|---|---|
| ERP-416 `PurchaseQuoteAuthorizationRules` | 实时身份 / 既有「供应商比价」菜单 / ERP-097 客户数据范围（转单另需既有「采购订单」菜单与权威目的地范围） |
| ERP-417 `PurchaseQuoteMutationRules` | 比价行锁 + 原子事务 + 锁内权威重读；对比草稿的数量 / 单价 / EF 精度 / 币种 / 状态 / 选中一致性 |
| ERP-418 `PurchaseQuoteConversionMutationRules` | 比价 → 采购订单的确定性锁序、批次成员不可变、转换资格与「档案确实存在但已停用 / 已删除」的受控拒绝 |
| ERP-427 `PurchaseOrderMasterReferenceRules` | **规范采购订单录入**的必填供应商 / 必填商品（存在 / 未删除 / 启用）与有效单位口径 |

但这些护栏都**没有**核对调用方提交的 `PurchaseQuote.SupplierId` / `ProductId` / `CustomerId` 是否仍指向
**实时主数据**：零 / 负数、已删除 / 已停用 / 悬空的引用因此可以被写进比价行；`ValidateDraft` 只校验商业条款，
`MapDraft` 又会把 `ProductId ?? 0` 映射进采购订单明细 —— 失效 / 缺失引用可以被「静默承诺」成采购订单
（包括把 `ProductId = 0` 当成实时商品引用）。

ERP-476 在**唯一写入与采购承诺入口**上补齐「实时主数据引用复核」，并保持历史读取、可空 / 未引用档案的
弱引用比价草稿语义与既有授权语义不变。

## 2. 唯一权威规则

`PurchaseQuoteMasterReferenceRules`（`src/ERP.Application/Services/PurchaseQuoteMasterReferenceRules.cs`）：

| 项 | 要求 | 失败码 |
|---|---|---|
| **归属客户** `CustomerId`（可空，硬引用） | 留空 = 未指定客户（既有可空草稿语义）；填写时必须为正整数且解析到**存在、未删除、启用（`Status = 1`）**的客户；**悬空一律拒绝**（客户是 ERP-097 权威范围与归属销售订单血缘的唯一锚点） | 非正 `1001`；不存在 / 已删除 `1002`；已停用 `1004` |
| **供应商** `SupplierId`（可空，弱引用） | 留空 = 未维护供应商的比价草稿（保持既有语义）；填写时必须为正整数；解析到既有档案时必须**未删除 / 启用**；**解析不到档案**时保持既有「未引用档案」弱引用口径（档口 / 现货比价仍可写 / 可转换，绝不臆造档案、绝不静默改派） | 非正 `1001`；已删除 `1002`；已停用 `1004` |
| **商品** `ProductId`（可空，弱引用） | 留空 = 手工 / 未引用商品档案的比价草稿；填写时必须为正整数；解析到既有档案时必须**未删除 / 启用**；解析不到档案时同供应商保持既有弱引用口径 | 非正 `1001`；已删除 `1002`；已停用 `1004` |
| **采购承诺**（选中 / 带入预填 / 转采购订单） | 供应商与商品引用必须**显式存在且为正整数** —— 绝不把缺失 / `0` 静默当作实时采购引用（`ProductId = 0` 不再作为实时采购明细落库），也绝不生成无供应商的采购订单；归属客户填写时必须解析到实时客户；供应商 / 商品解析到既有档案时必须未删除 / 启用 | 供应商 / 商品缺失或非正 `1004`；客户非正 `1001` / 已删除 `1002` / 已停用 `1004`；档案失效 `1004` |

要点：

- **可空草稿语义不变**：留空（`null`）的供应商 / 商品 / 客户仍按既有口径合法保存（未维护供应商 / 手工商品 /
  未指定客户的比价草稿），本次只对**显式填写**的值与**承诺**时点的引用做实时复核；
- **「未引用档案」弱引用口径不变**：ERP-418 既有口径（未引用主数据档案的比价行仍可转换）被显式保留，
  历史悬空引用**绝不回填、绝不自动改派**（这是「历史快照保持可读、不变」的边界）；
- **绝不臆造主数据**：本护栏只做纯判定与有界只读投影查询，不写供应商 / 商品 / 客户主数据，
  也绝不要求或授予任何主数据维护权限（无新菜单 / 角色 / 用户授权 / 表结构）；
- **有界批量查询**：承诺复核按去重的供应商 / 商品 / 客户 Id 集合**各一次**取回实时档案（最多 3 次查询，
  绝不逐行查库），任一行不合格即整批受控拒绝。

## 3. 接入点与时序

| 路径 | 接入点 | 时序（相对既有步骤） | 复核对象 |
|---|---|---|---|
| 新增 `POST /api/purchase/quotes` | `PurchaseQuoteController.Create` | 实时授权 + 拟议客户范围 → 伪造转换证据 / 草稿校验 → **实时主数据引用复核（含「已选中」承诺级）** → 单号 / 字段赋值 | 请求体 |
| 修改 `PUT /api/purchase/quotes/{id}` | `PurchaseQuoteController.Update` | 比价行锁 + 锁内权威重读 → 拟议客户范围 → 已转冻结 / 已决定冻结 → **实时主数据引用复核（含「已选中」承诺级）** → 字段改写 | 请求体 |
| 带入预填 `GET /{id}/order-prefill` | `PurchaseQuoteController.OrderPrefill` | 比价 + 目的地授权 → 来源归属复核 → **来源承诺级实时引用复核** → 只读草稿（不落库 / 不占号 / 不改来源） | 锁外权威快照 |
| 单行转采购订单 `POST /{id}/to-order` | `PurchaseQuoteController.ToPurchaseOrder` | 事务 → 归属销售订单行锁 → 比价行锁 → 锁内权威重读 → 资格 / 主数据（ERP-418）→ **承诺级实时引用复核** → 映射草稿 → 发号 → 落库 + 来源置「已转采购订单」 | 锁内权威行 |
| 批次转采购订单 `POST /batch-to-order` | `PurchaseQuoteController.BatchToOrder` | 同上（批次成员）→ 逐行预检 → 资格 / 主数据（ERP-418）→ **本批将写入行的承诺级实时引用复核** → 目的地归属 → 一次发号 + 写入 | 锁内权威行 |
| 审批决定 `POST /api/purchase/quote-decisions/decide` | 既有 ERP-095 / ERP-417 路由（**不在本任务允许改动路径内**） | 见 §4「审批时点的复核口径」 | — |

- 引用复核**先于单号预约与任何表头 / 明细赋值**；失败即整体回滚：不消耗采购单号、不落任何数据、
  不改写来源比价行状态、不产生下游副作用；
- 单行 / 批次转单都在**既有来源行锁 + 原子事务**内复核（锁序不变：归属销售订单行锁 → 比价行锁）；
- 既有护栏优先级不变：**授权永远最先**（缺菜单 / 越界的账号即便提交失效引用也返回同一受控权限错误，非披露），
  随后是来源血缘 / 状态资格（ERP-417 / ERP-418）与商业条款错误，最后才是本护栏的主数据引用错误；
- 只做有界只读投影查询：客户 / 供应商 / 商品单行或**一次去重批量查询**，绝不逐行查库，也不产生被跟踪的变更。

## 4. 执行边界（进程内直调与审批时点口径）

与 ERP-427（采购订单）/ ERP-473（PI）/ ERP-475（询价单）同源取舍：控制器用 `Request.Path.HasValue`
判定「是否必须执行实时主数据引用复核」。

- **真实 HTTP 写入 / 选择 / 带入预填 / 转换请求（MVC 绑定）一律执行**：`Request.Path` 始终已赋值，
  因此所有外部写入、选择、承诺与转换都被复核；
- **未被任何请求绑定的纯进程内直调**（历史单元测试夹具 / 内部派生读取，`Request.Path` 无值）保持既有行为：
  该判定只决定「是否额外加载实时主数据」，绝不改变既有身份 / 菜单 / 客户范围授权（授权与主数据复核互不替代）；
- **审批时点的复核口径**：`api/purchase/quote-decisions/decide` 的实现文件不在本任务允许改动的路径内，
  因此审批时**不额外**回读主数据；但审批后的承诺仍受两道确定性闸门保护 ——
  (1) 有效审批决定会把比价行冻结为「商业条款只读」（ERP-417），批准后引用无法被静默改派；
  (2) 真正的采购写入提交（单行 / 批次转采购订单）在既有来源行锁内、发号与落库之前按本护栏**重新复核**
  「已选中 / 已批准」来源的实时引用，任一失效即整批受控拒绝。也就是说：**审批之后、承诺之前的任何主数据失效
  都在发号之前被拦截**，绝不产生部分采购订单。

## 5. 边界（不做的事）

- 不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，不新增迁移，不写主数据，不要求或授予主数据维护权限；
- 不做历史数据回填 / 自动修正：列表 / 详情 / 价格历史 / 价格差异 / 转化漏斗等读取路径原样返回持久化值，
  引用失效的历史比价行照常可读，且**绝不**被自动改派到其它供应商 / 商品 / 客户；
- 不触碰库存 / 财务 / 采购订单既有写入路径，不改写审批决定历史（append-only）；
- 既有「比价 → 采购订单」来源留痕（`RefOrderNo` + 备注来源标记）、批次分组与金额重算口径完全不变。

## 6. 验收证据

| 类型 | 文件 | 覆盖 |
|---|---|---|
| 单元（新增） | `src/ERP.UnitTests/PurchaseQuoteMasterReferenceTests.cs` | 客户 / 供应商 / 商品的非正整数、悬空 / 不存在 / 已删除 / 已停用受控错误；可空与「未引用档案」弱引用草稿保持可写；承诺级「供应商 / 商品缺失或非正」拒绝（含 `ProductId = 0` 绝不静默占位）；承诺级整批一行不合格即拒绝；HTTP 绑定的新增 / 修改 / 选择失效引用零写入、锁内原子拒绝；带入预填 / 单行 / 批次转换失效引用零发号零写入、来源状态不变；合法实时主数据正向生成并留痕；授权先于主数据（非披露）；进程内直调保持既有行为；源码契约 |
| 单元（扩展） | `src/ERP.UnitTests/PurchaseQuoteConversionTests.cs` | 真实 HTTP 承诺请求下 `ProductId = null` 不再按零占位生成采购订单（在发号与写入之前受控拒绝）；合法实时主数据正向转换；既有 ERP-020 映射 / 预填 / 重复守卫回归保持不变 |
| 单元（扩展） | `src/ERP.UnitTests/PurchaseQuoteMutationTests.cs` | 源码契约：控制器接通唯一复核入口，且不新增权限 / 不改写既有事务与锁 |
| 单元（扩展） | `src/ERP.UnitTests/PurchaseQuoteApprovalTests.cs` | 已批准来源在审批后主数据失效 → 承诺转换受控拒绝，且审批决定历史 / 来源状态不变 |
| 单元（扩展） | `src/ERP.UnitTests/PurchaseQuoteConversionBatchTests.cs` | 批次内一行失效 → 整批零发号零写入，不产生部分采购组 |
| 单元（扩展） | `src/ERP.UnitTests/PurchaseQuoteConversionMutationTests.cs` | 源码契约：单行 / 批次共用唯一承诺复核入口 |
| 集成（真实 SQL） | `src/ERP.IntegrationTests/PurchaseQuoteMasterReferenceSqlServerTests.cs` | 专用 localdb 上的真实控制器：失效引用零写入 / 零占号；合法主数据完整落库；草稿后主数据失效 → 转单拒绝且来源 / 审批决定不变；批次一行失效 → 整批拒绝；手工 / 可空草稿语义；历史读取只读；授权非披露；有界批量查询计数；目标护栏 fail-closed |
| 回归 | `PurchaseQuoteConversionTests` / `PurchaseQuoteConversionBatchTests` / `PurchaseQuoteMutationTests` / `PurchaseQuoteApprovalTests` / `PurchaseQuoteAuthorizationTests` 等 | 既有价格 / 选择 / 历史 / 转换口径保持不变（含 ERP-418 的「未引用档案弱引用可转换」口径） |

## 7. 真实 SQL 夹具安全口径

- 目标必须精确为专用实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且
  `Integrated Security=true`；错误实例 / 错误库名 / 非集成安全的连接串一律在 `AssertDedicatedTarget`
  阶段于**任何数据库访问之前**被拒绝（含 fail-closed 单元覆盖）；
- 每次运行只创建一个**全新 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，
  绝不读取 `appsettings*.json` / `.env` / 生产凭据；只使用既有认证授权，不新增菜单 / 角色 / 用户授权；
- **构建完成不等于阶段验收**：真实 SQL 场景须在专用 localdb 上真实执行通过才算验收证据；
  真实环境不可用时按环境阻塞单独记录，不做降级或伪造。

## 8. 本次交付的验收执行记录（如实记录，不宣称整阶段验收）

| 步骤 | 命令 | 结果 |
|---|---|---|
| 安全 Release 构建（分析器 + 警告即错误） | `dotnet build NEWERP.sln -c Release --no-restore /p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true` | 成功，0 警告 / 0 错误 |
| 全量单元回归 | `dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build` | 通过：7713 / 7713（失败 0） |
| 本任务真实 SQL（专用 localdb） | `dotnet test src/ERP.IntegrationTests/ERP.IntegrationTests.csproj -c Release --no-build --filter FullyQualifiedName~PurchaseQuoteMasterReference` | 通过：14 / 14（失败 0）；目标为 `(localdb)\NEWERP_AutoAcceptance` + 全新 GUID `NEWERP_AUTOTEST_PQMASTERREF_*` 库、`Integrated Security=true`，未 drop / reset 任何库 |
| 既有比价 SQL 回归（同口径） | `dotnet test src/ERP.IntegrationTests/ERP.IntegrationTests.csproj -c Release --no-build --filter "FullyQualifiedName~PurchaseQuoteMutationSqlServerTests\|FullyQualifiedName~PurchaseQuoteConversionMutationSqlServerTests\|FullyQualifiedName~PurchaseQuoteAuthorizationSqlServerTests\|FullyQualifiedName~PurchaseOrderMasterReferenceSqlServerTests"` | 通过：54 / 54（失败 0）—— 既有 ERP-416 / ERP-417 / ERP-418 的锁 / 事务 / 授权 / 转换（含「未引用档案弱引用可转换」）口径不变 |
| 浏览器验收 | 本任务 `browser_acceptance.required = false` | 未执行（无需 UI / 样式验收） |

说明：真实 SQL 证据限于**本任务新增 / 相关**的供应商比价主数据引用场景；
本阶段其它既有集成 / 浏览器套件未在本任务内重跑，因此**不宣称整阶段验收通过**。

### 受阻 / 边界记录（如实记录）

- **审批决定时点的护栏接入受阻于允许改动路径**：`api/purchase/quote-decisions/decide` 的实现文件
  （`PurchaseQuoteDecisionController.cs` / `PurchaseQuoteApproval.cs`）不在本任务 `allowed_paths` 内，
  因此审批时未额外回读主数据；该缺口由 §4 第 (1)(2) 条两道确定性闸门覆盖（批准后商业条款冻结 +
  承诺转换在来源行锁内发号之前重新复核）——若后续任务获准改动该路径，可直接复用
  `PurchaseQuoteMasterReferenceRules.EnsureCommitmentReferencesAsync`（无需新增任何权限 / 表结构）。
- **既有 ERP-418「未引用档案」弱引用口径被显式保留**：既有 `PurchaseQuoteMutationSqlServerTests` /
  `PurchaseQuoteConversionMutationSqlServerTests` / `PurchaseQuoteAuthorizationSqlServerTests` 夹具以
  未引用任何主数据档案的比价行（`SupplierId = 88` / `ProductId = 310`）驱动既有路径，
  本任务按「保留既有转换回归 + 绝不静默改派来源引用」的要求保留该口径，只对**显式引用值**与**承诺时点**收紧，
  并已在文档与源码中显式说明。
