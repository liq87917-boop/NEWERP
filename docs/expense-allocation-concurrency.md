# 费用分摊生成 / 作废与来源费用改动的串行化（ERP-386）

## 1. 目的

补上 ERP-385（费用单实时授权与权威客户范围）之后仍存在的并发完整性缺口：

- `ExpenseBillController.AllocateApply`（传统拼柜分摊生成）当时是「先查重复 → 再算当日最大单号 → 逐行插入」的
  **无锁 check-save** 写法：两条并发等价请求可以各自通过重复检查并同时插入，产生**两套重复费用单**；
  两条并发**不同**请求也可能算出**同一个** `EXP-yyyyMMdd-###` 单号（重复单号）；
- ERP-042 批次生成（`ContainerExpenseAllocationService.GenerateAsync`）虽然要求「一次 `SaveChanges` + 唯一索引兜底」，
  但没有与**来源费用单改动 / 装柜清单参与方维护**共享锁：生成读到的来源金额 / 币种 / 客户 / 参与方集合
  可能在判定与写入之间被并发改写，从而落出**陈旧证据**（例如基于已被软删除的来源费用生成分摊行）；
- 批次作废（`VoidAsync`）与生成之间没有共同锁，可能交错；
- 传统分摊的重复键使用自由文本 `RefNo`：它可以作为**业务去重口径**，但绝不能当作**权威来源身份**或并发锁键
  （否则可用任意文本制造互不冲突的锁键，绕过串行化）。

ERP-386 在不新增表 / 列 / 菜单 / 权限模型、**不新增任何用户授权**的前提下，把
**ERP-042 批次生成 / 显式作废**、**传统分摊生成**、**来源费用商业修改 / 删除**、**装柜清单参与方维护**
统一到**同一条确定性锁序与原子事务**上。

## 2. 关键不变量

1. **实时启用身份 + 既有菜单授权 + 权威客户范围**：传统分摊（预览 / 生成）、批次（预览 / 生成 / 台账 / 详情 / 作废）
   继续复用 ERP-385 的实时授权口径（`expense-bill` 菜单 + `SalespersonDataScopeService`）；停用账号 / 撤销授权立即收敛，
   绝不退化为匿名或管理员。
2. **一次原子事务**：生成（传统 / 批次）、作废、来源费用修改 / 删除、参与方维护都在**同一事务**内完成
   「条件判定 + 单号保留 + 写入」；任一步失败（无效行、权限过期、来源冲突、数据库失败）**整体回滚**：
   不留部分批次 / 分摊行 / 费用单行，也不「占用」任何已计算的单号（单号由数据推导，回滚即释放）。
3. **同一来源最多一套有效证据**：同一「来源费用 + 装柜清单」在有效期内最多一条批次（不区分分摊方法，业务判定 + 过滤唯一索引双兜底）；
   传统分摊同一「RefNo + 费用类型 + 日期」最多一套完整费用单；并发等价请求最多一方成功。
4. **单号绝不重复**：模块级单号键行锁把「当日最大单号 + 1」的保留串行化，因此并发**不同**请求的
   `EXP-yyyyMMdd-###` / `EAB-yyyyMMdd-###` 绝不冲突。
5. **来源证据不可陈旧**：生成在锁内**重读**权威来源费用 / 装柜清单 / 参与方并重新执行资格与授权校验；
   来源被软删除、金额 / 币种 / 客户 / 柜级身份被改写、参与方被停用都在写入之前 fail closed。
6. **有效批次来源受保护**：来源费用在批次有效期内**不得被删除**（先显式作废批次释放），
   也**不得改写权威分摊基数**（金额 / 币种 / 汇率 / 归属客户 / 费用类型 / 柜级身份）；
   备注 / 收款方 / 付款状态等非基数字段照常可改。
7. **作废显式且保留历史**：作废必须填原因，只改批次状态 + 原因 + 时间；不删除任何历史行、不改写已生成费用单、
   不改写来源费用、不改写装柜清单 / 明细 / 结算金额；重复作废被锁内状态门拒绝。
8. **金额与口径零改写**：生成只新增既有 `FinanceExpense` 行（一参与方一行），金额按**币种精度**取整
   （0.5 进位），余差归「基准值最大」参与方，**行合计恒等于来源费用金额**；不发明余额、不做汇率换算、不跨币种合并，
   也不改写装柜清单 / 明细数量与结算金额。

## 3. 原子性与并发（唯一全局锁序）

**唯一全局锁序（跨模块统一，绝不反向获取）**：

```
1) 来源费用单行（FinanceExpenses，Id 升序；与费用单商业修改 / 删除共用同一把行锁）
2) 装柜清单行（ContainerLoadingLists；与 ERP-364 参与方维护 / ERP-384 装柜结算共用同一把行锁）
3) 参与方行（ContainerLoadingListParticipants，Id 升序）
4) 分摊批次行（FinanceExpenseAllocationBatches；作废路径）
5) 模块级单号键行（既有「费用单」expense-bill 菜单行 —— 重复检查 / 单号保留的唯一汇聚点）
6) 批次 / 分摊行 / 生成费用单行（由持有上述行锁的事务顺带写入）
```

- **锁实现**：`ExpenseAllocationConcurrencyRules.LockSourceExpenseRowsAsync` /
  `LockLoadingListRowsAsync` / `LockParticipantRowsAsync` / `LockBatchRowAsync` / `LockModuleNumberKeyAsync` ——
  在调用方事务内对目标行发一条「审计时间戳刷新」的 `UPDATE`（仅用 EF Core 基础 API，不依赖关系型扩展，
  也不执行任何任意 SQL），取得排它行锁（X 锁，持有至事务结束），语义等价于 `SELECT ... WITH (UPDLOCK, HOLDLOCK)`；
  `UpdatedAt` 是技术审计字段、不是商业证据，不构成静默改写。
- **行锁语义 = 阻塞后成功**：`RowVersion` 乐观并发令牌在并发方先提交后会过期，此时重读权威行（含新令牌）后
  **有界重试**（`LockRetryAttempts = 8`），绝不把纯粹的锁等待误报成业务拒绝；真实业务冲突仍由锁内校验判定。
- **绝不嵌套事务**：`BeginTransactionIfRelationalAsync` 在调用方（例如装柜清单参与方路由既有的
  `IsolationLevel.Serializable` 事务）**已经开启事务时直接复用**既有事务与锁并返回 `null`，
  由最外层调用方统一提交 / 回滚 —— 既不重复开事务，也绝不提前提交半成品。
- **单号键不是自由文本**：模块级单号键行是既有 `expense-bill` 菜单行（与 ERP-385 授权同源），
  绝不使用自由文本 `RefNo` / 柜号构造锁键，也绝不按自由文本反查权威来源；传统分摊不引用任何权威来源单据，
  批次 / 分摊行的权威来源身份始终是持久化的 `SourceExpenseId` / `LoadingListId` 与来源费用行本身。
- **锁调用者审计（全部遵守同一锁序）**：
  - `ContainerExpenseAllocationService.GenerateAsync`：来源费用单行锁 → 装柜清单行锁 → 参与方行锁 →（锁内重读 + 重新授权 + 资格校验）→ 模块单号键行锁 → 锁内重复检查 → 单号保留 → 一次 `SaveChanges` → 提交；
  - `ContainerExpenseAllocationService.VoidAsync`：来源费用单行锁 → 装柜清单行锁 → 批次行锁 → 锁内重读状态 / 原因校验 → 提交；
  - `ExpenseBillController.AllocateApply`：模块单号键行锁 →（锁内复核授权与客户可用性）→ 重复检查 → 单号保留 → 批量插入 → 提交；
  - `ExpenseBillController.Update` / `Delete` / `BatchDelete`：来源费用单行锁（Id 升序）→ 锁内重读权威行 → 有效批次来源护栏（改基数 / 删除否决）→ 写入 → 提交；
  - `ContainerLoadingParticipantService.CreateAsync / UpdateAsync / SetPrimaryAsync / SetStatusAsync / DeleteAsync`：
    装柜清单行锁 →（有既有行时）参与方行锁 → 校验 + 写入 → 提交；
  - ERP-384 装柜结算、ERP-364 参与方维护继续使用同一把装柜清单行锁，因此不会出现「A 持清单等来源、B 持来源等清单」的死锁环。
- **竞态结果**：
  - 并发**等价**传统分摊请求 → 恰好一方成功（另一方在单号键行锁内看到已提交的重复证据并被原子拒绝），只留下一套完整费用单；
  - 并发**不同**传统分摊请求 → 都成功且当日单号互不相同；
  - 并发同一来源批次生成 → 最多一条有效批次，另一方被原子拒绝且不留部分行；
  - **来源费用删除 vs 生成** → 删除先提交则生成被拒绝（0 批次 / 0 分摊行 / 0 生成费用单）；生成先提交则删除被「有效批次来源」护栏拒绝，来源行保持有效；
  - **来源费用金额修改 vs 生成** → 批次 `SourceAmount` 恒等于**持久化来源金额**，生成行合计恒等于 `SourceAmount`（要么修改被拒绝，要么按新金额分摊）；
  - **生成 vs 作废** → 作废必定成功并保留原因 / 审计，生成要么按重复规则被拒绝（旧批次仍唯一），要么在旧批次作废后生成一条新的完整批次；
  - **参与方维护 vs 生成** → 生成使用「生成时点」的启用参与方集合；停用先提交时生成原子拒绝，遗留证据数为 0。
- 内存库等非关系型提供程序无行锁语义：`IsRelationalProvider` 为 false 时跳过事务与锁定（等价无事务），声明式校验与写入语义不变。

## 4. 接口

沿用既有路由，**不新增路由、不改变请求 / 响应结构**：

- `POST /api/finance/expenses/allocate-preview`（传统分摊预览；只读）
- `POST /api/finance/expenses/allocate-apply`（传统分摊生成；原子事务 + 模块单号键行锁）
- `PUT /api/finance/expenses/{id}` / `DELETE /api/finance/expenses/{id}` / `POST /api/finance/expenses/batch-delete`
  （来源费用商业修改 / 删除；来源费用单行锁 + 有效批次来源护栏）
- ERP-042 既有路由：分摊上下文 / 预览 / 生成 / 台账 / 详情 / 作废（生成与作废新增原子事务与锁序）
- 装柜清单参与方既有路由：新增 / 修改 / 设为主参与方 / 启用停用 / 删除（新增清单行锁 + 原子事务）

## 5. 变更文件

- `src/ERP.Application/Services/ExpenseAllocationConcurrencyRules.cs`（**新增**：唯一锁序、原子事务、行锁、单号键、来源证据护栏）
- `src/ERP.Application/Services/ContainerExpenseAllocationService.cs`（生成 / 作废的原子事务 + 锁序 + 锁内重读重校验）
- `src/ERP.Api/Controllers/ExpenseBillController.cs`（传统分摊原子化；来源费用修改 / 删除 / 批量删除加锁与护栏）
- `src/ERP.Application/Services/ContainerLoadingParticipantService.cs`（参与方维护加锁与原子事务）
- `src/ERP.UnitTests/ExpenseAllocationConcurrencyTests.cs`（内存库：原子回滚、重复 / 单号、来源护栏、金额精度、锁序契约）
- `src/ERP.IntegrationTests/ExpenseAllocationConcurrencySqlServerTests.cs`（真实 SQL：六组两个独立连接竞态 + 实时授权 + 专用目标护栏）
- `docs/expense-allocation-concurrency.md`（本文档）

## 6. 测试与验证

- 单元测试（内存库 `TestDbFactory`，无 SQL）：
  `dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build --filter FullyQualifiedName~ExpenseAllocationConcurrencyTests`
  覆盖：事务 / 锁序源码契约（生成、作废、传统分摊、来源费用改动、参与方维护的调用顺序与锁键来源）；
  传统分摊无效明细整批拒绝且不占用单号、等价请求最多一套完整费用单、不同请求单号绝不重复；
  批次重复生成拒绝、作废必须填原因、作废保留历史与原始证据且可重新生成；已删除来源 / 金额为 0 / 柜号不一致 /
  零基数 / 缺失基数 / 清单外参与方一律原子拒绝且零副作用；币种精度（CNY 2 位 / JPY 0 位）与余差分布使
  行合计恒等于来源金额；有效批次来源费用禁止删除 / 禁止改写权威基数（作废后释放）；参与方停用后按最新集合判定；
  越界范围预览 / 生成 / 作废 fail closed 且零副作用。
- SQL Server 集成测试（受控 localdb，真实授权管线 + 六个两个独立连接竞态）：
  `dotnet test src/ERP.IntegrationTests/ERP.IntegrationTests.csproj -c Release --no-build --filter "FullyQualifiedName~ExpenseAllocationConcurrencySqlServerTests|FullyQualifiedName~ExpenseAllocationConcurrencyTargetGuardTests"`
  覆盖：① 两条等价传统分摊请求 → 恰好一方成功且只有一套完整费用单；② 两条不同传统分摊请求 → 都成功且当日单号绝不重复；
  ③ 来源费用删除 vs 批次生成；④ 来源费用金额修改 vs 批次生成（证据恒等于持久化来源金额）；⑤ 生成 vs 作废；
  ⑥ 参与方停用 vs 生成；以及既有可串行化事务复用（参与方路由外层事务回滚 / 提交都随外层生效，绝不嵌套）、
  真实身份授权（既有菜单授权成功、撤销菜单 / 停用账号 / 匿名 HTTP 一律 fail closed
  且零改写、不新增任何用户 / 角色 / 授权）与专用目标护栏 fail-closed。
- 安全验证档（本任务交付门）：`dotnet build NEWERP.sln -c Release`（`TreatWarningsAsErrors` / 分析器开启）+
  `ERP.UnitTests` 全量。
- 本次实测证据（受控 localdb 真实执行）：
  - Release 构建 `0 警告 / 0 错误`；
  - `ERP.UnitTests` 全量 **5801 通过 / 0 失败**（本任务新增单元筛选 **13 通过 / 0 失败**）；
  - 真实 SQL 集成筛选 **12 通过 / 0 失败**（8 个场景 + 4 条目标护栏 fail-closed 用例），连续 **3 次独立运行**
    （各自全新 GUID `NEWERP_AUTOTEST` 库）结果一致；
  - 既有 ERP-364 / ERP-385 授权 SQL 集成筛选 **9 通过 / 0 失败**（证明参与方路由的可串行化事务复用未破坏既有语义）。
- 测试产物（TRX / 日志）一律写到仓库**之外**的临时目录；仓库内不留下本任务的测试产物。
- **构建完成不等于阶段验收**：真实 SQL 场景必须在专用 localdb 上真实执行通过才算验收证据；本任务
  `browser_acceptance.required = false`，不运行浏览器 / UI 验收，也未运行任何部署或生产库操作。

## 7. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST`、
  `Integrated Security`；错误目标在访问数据库**之前** fail closed（`AssertDedicatedTarget`，含 fail-closed 单元覆盖）。
- 每次运行只创建一个**全新 GUID 后缀库**（`NEWERP_AUTOTEST_EXPENSEALLOCATIONCONCURRENCY_<日期>_<GUID>`）；
  发现同名库已存在立即拒绝（`SELECT DB_ID`）；绝不 drop / reset / 复用任何数据库，也不做任何破坏性数据操作。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings` /
  `.env` / 生产凭据。
- 测试只**新增**自己的证据行（唯一前缀 `INT_EA386_`），不清理、不删除、不重写任何既有行与原始失败日志。
- 夹具初始化 = `EnsureCreated` + `SchemaUpgrader` + `SeedData` + `SchemaUpgrader`；真实身份复用既有
  `expense-bill` 菜单与既有销售员客户范围口径，**不新增任何用户 / 角色 / 菜单 / 权限授权**。

## 8. 边界与保留项

- 本护栏只保护费用分摊生成 / 作废与来源费用商业改动的并发完整性：不改写来源费用单的金额 / 归属 / 付款状态
  （唯一例外是「有效批次来源」在批次有效期内被拒绝改写权威基数），不改写装柜清单与明细数量 / 箱数 / 重量 / 体积、
  参与方身份、订柜外贸与物流跟踪值、单证、装柜结算金额、库存与库存成本、库存流水、采购订单与销售订单，
  也**不记账、不生成凭证 / 收付款 / 结算单、不做汇率换算、不跨币种合并、绝不物理删除或静默改写历史证据**。
- **权限模型不变**：只复用既有 `expense-bill` 菜单与既有数据范围口径（含既有系统内置角色特权），
  不新增用户 / 角色 / 菜单 / 权限，也不引入匿名或管理员回退。
- **保留项**：库存来源单据审计（ERP-375 / ERP-376 / ERP-377 的来源选择与来源审计链路）与原始失败日志
  （`run.log` / `TestResults` / `.ai/logs`）一律保持原样，不删除、不重写；本改动不触碰任何库存来源工作流。
- **依赖与兼容**：ERP-042（分摊批次与逐行留痕）/ ERP-060（分摊证据读取）/ ERP-364（装柜清单参与方）/
  ERP-383 / ERP-384 / ERP-385（实时授权与权威客户范围）的既有行为、路由、审计与证据语义全部保留；
  `docs/装柜费用分摊批次说明.md`、`docs/装柜费用分摊证据视图说明.md`、`docs/expense-authorization.md`、
  `docs/loading-list-authorization.md` 仍然有效，本文档只补充 ERP-386 的锁序、单号键与双连接竞态。
- **更正路径不变**：分摊批次只以**显式作废**（状态 0 + 原因 + 原审计）更正，历史与逐行留痕保持不变；
  来源费用只有在批次作废后才可删除 / 改写权威基数。
