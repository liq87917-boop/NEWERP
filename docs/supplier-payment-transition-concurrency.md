# 供应商付款单提交 / 审核同单串行化（ERP-379）

## 1. 目的

补上 ERP-351 / ERP-378 之后仍存在的运营完整性缺口：`FinancePayment` 的**提交 / 审核**
（`FinancePaymentController.Submit` / `Approve`）当时仍是「先读、再判定、后落库」的无锁写法，
而同一模块的**修改 / 取消 / 删除**与两套付款引用证据写入
（ERP-049 `SupplierPaymentAllocations`、ERP-066 `SupplierPaymentInvoiceAllocations`）已经在**付款单行锁**下执行。

因此并发下可能出现：某次提交 / 审核与某次取消 / 删除 / 改金额交错，导致**已取消或已删除的付款单被陈旧写入复活**，
或审核放行了在锁外被改写成非法金额 / 币种 / 来源的持久化数据；「提交 vs 修改」「审核 vs 取消」也可能互相覆盖。

ERP-379 在不新增表 / 列 / 菜单 / 权限模型、**不新增任何审批要求**的前提下，把付款单的**五个生命周期动作**
（修改 / 提交 / 审核 / 取消 / 删除）统一到与两套引用写入**同一条锁序、同一个事务**上，
并在锁内用**实时身份 / 授权 / 数据范围**复核 + 既有规则复核持久化金额 / 币种 / 来源。

## 2. 关键不变量

1. **实时启用身份 + 既有菜单授权 + 来源客户数据范围**：每次请求都重新查询 `SysUsers`
   （账号必须存在、未删除且启用）、「角色 → 菜单」（`payment`「付款单」模块授权）与
   `SalespersonDataScopeService`（ERP-097 唯一权威口径，按**付款单权威来源**派生的客户判定）。
   停用账号、撤销授权、越界客户一律 fail closed，**绝不退化为匿名或管理员**；付款单未关联货款申请单时没有可派生客户，
   仅既有特权（系统内置角色）账号可通过，受限制账号 fail closed。
2. **单事务 + 付款单行锁**：修改 / 提交 / 审核 / 取消 / 删除都在**同一个事务**内先对付款单行加排它行锁
   （`SupplierPaymentLifecycleRules.LockPaymentRowAsync`），再加载权威状态与字段；任一校验失败整体回滚——
   状态、原始字段、软删除标记与审计时间戳都不变。
3. **持久化金额 / 币种 / 来源复核（不新增审批要求）**：提交 / 审核在锁内用**既有规则**
   （`EnsurePersistedPaymentConsistentAsync`：币种必须在既有系统币种口径内、金额按币种精度取整后大于 0、
   关联货款申请单（若有）必须仍可按 Id 解析到既有 / 未删除 / 未取消且币种一致、付款金额不超过申请金额）
   复核已被持久化的数据；未关联申请单的历史付款场景保持不变，不引入新阈值、新审批人或新单据状态。
4. **有效证据保护（ERP-351，保持不变）**：只要付款单存在任一**有效**（未作废、未删除）的 ERP-049 或 ERP-066
   引用证据，就拒绝取消 / 删除付款单，也拒绝以破坏证据的方式修改供应商 / 币种 / 金额；要解除限制必须先走
   既有显式作废服务（`SupplierPaymentAllocationService.VoidAsync` /
   `SupplierPaymentInvoiceAllocationService.VoidAsync`），作废保留原始证据，**绝不物理删除、绝不静默改写**。
5. **同一付款单的唯一同币种分摊额度（ERP-351，保持不变）**：付款单金额是唯一、同币种的分摊额度；
   「付款单 → 采购订单」与「付款单 → 供应商采购发票」两套有效行在付款单行锁下**共同占用**同一额度，
   每套证据各计一次、绝不跨币种合计、绝不重复计算。
6. **允许的状态流转与软删除语义（保持不变）**：提交仅 `Pending → Submitted`、审核仅 `Submitted → Approved`、
   取消把非取消单据置为 `Cancelled`、删除仅 `Pending` 可软删除（`IsDeleted = true`）；重复流转被拒绝且不改写审计。

## 3. 原子性与并发（锁序）

- **唯一锁**：`SupplierPaymentLifecycleRules.LockPaymentRowAsync`——在调用方事务内对付款单行发一条「审计时间戳
  刷新」的 `UPDATE`（仅用 EF Core 基础 API，不依赖关系型扩展），取得排它行锁（X 锁）并持有至事务结束，
  语义等价于 `SELECT ... WITH (UPDLOCK, HOLDLOCK)`。`RowVersion` 乐观并发令牌保证「锁竞争失败方」以可读业务冲突
  收尾（fail closed，绝不静默覆盖赢家）。
- **统一锁序（先付款单行、后引用行）**：付款单生命周期（`FinancePaymentController.Update/Submit/Approve/Cancel/Delete`）
  与两套付款引用证据写入（`SupplierPaymentAllocationService.CreateAsync` /
  `SupplierPaymentInvoiceAllocationService.CreateAsync`）都必须先取本锁、再读写引用行；全模块不反向获取下游锁。
- **竞态结果**：
  - 「提交 vs 修改」：给出唯一一致的串行化结果——修改要么整笔生效、要么完全保持原值（无半成品写入），
    提交结果与最终状态严格一致，状态不会停留在非法值；
  - 「提交 vs 删除」：绝不允许出现「已删除 + 已提交」的陈旧写入把已删除付款单复活，删除成功与 `IsDeleted` 严格一致；
  - 「审核 vs 取消」：取消为终态，取消成功时最终状态必为 `Cancelled`，取消被行锁击败时审核必须已生效（`Approved`）；
  - 三者都不会新增引用行、都会保持付款单金额 / 银行账户 / 备注不变，且**不产生任何新的资金记账 / 结算 / 库存流水**。
- 内存库等非关系型提供程序无行锁语义：`IsRelationalProvider` 为 false 时跳过事务与锁定（等价无事务），
  声明式业务校验与写入语义不受影响。

## 4. 接口

沿用既有付款单路由，**不新增路由、不改变请求 / 响应结构**：

- `GET /api/finance/payments`（分页，按客户数据范围过滤）
- `GET /api/finance/payments/{id}`（详情，越界 fail closed）
- `POST /api/finance/payments`（创建，校验供应商 / 币种 / 金额 / 货款申请单来源 / 权限）
- `PUT /api/finance/payments/{id}`（仅待提交可改；改供应商 / 币种 / 金额前校验有效引用证据）
- `POST /api/finance/payments/{id}/submit` / `/approve` / `/cancel`（**ERP-379：提交 / 审核改为行锁 + 单事务内复核**）
- `DELETE /api/finance/payments/{id}`（仅待提交可删，先校验有效引用证据）

## 5. 关键文件

- `src/ERP.Application/Services/SupplierPaymentLifecycleRules.cs`
  （实时启用身份校验 + 持久化金额 / 币种 / 来源复核 + 付款单行锁 / 锁序文档）
- `src/ERP.Api/Controllers/FinancePaymentController.cs`（提交 / 审核的事务 + 行锁 + 锁内复核）
- `src/ERP.UnitTests/SupplierPaymentTransitionConcurrencyTests.cs`（内存库：实时身份 / 授权 / 范围、非法来源、重复流转、
  失败不落库、ERP-351 证据 / 额度 / 作废）
- `src/ERP.IntegrationTests/SupplierPaymentTransitionConcurrencySqlServerTests.cs`（真实 SQL：真实控制器 + 三条双连接竞态 +
  专用目标护栏）
- `docs/supplier-payment-lifecycle.md`（ERP-351 原有说明，继续保持有效）

## 6. 测试与验证

- 单元测试（内存库 `TestDbFactory`，无 SQL）：
  `dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build --filter FullyQualifiedName~SupplierPayment`
  覆盖：停用账号 / 撤销菜单 / 来源客户越界 / 未关联来源的受限账号 fail closed；悬空 / 已取消 / 币种不兼容 / 超额来源
  拒绝提交 / 审核；重复提交 / 审核被拒绝且状态与审计不变；已取消不可再提交、已删除按不存在处理且删除标记不变；
  ERP-351 有效证据拒绝取消 / 删除、显式作废释放且保留历史行、两套证据共享同一同币种额度、流转不新增引用行与记账。
- SQL Server 集成测试（受控 localdb，真实控制器 + 两个独立连接）：
  `dotnet test src/ERP.IntegrationTests/ERP.IntegrationTests.csproj -c Release --no-build --filter FullyQualifiedName~SupplierPaymentTransition`
  覆盖：状态流转与失败方保留审计；停用账号 / 撤销菜单 / 来源客户越界 fail closed；悬空 / 已取消 / 币种不兼容来源拒绝；
  有效证据保护与显式作废释放；共享额度超额拒绝且无孤儿引用行；以及三条双连接竞态：
  「提交 vs 修改」「提交 vs 删除」「审核 vs 取消」。
- 安全验证档（本任务交付门）：Release 构建（`TreatWarningsAsErrors` / 分析器开启）+ `ERP.UnitTests` 全量。
- **验证证据落点（只写仓库之外）**：所有 `dotnet test` 结果文件一律用
  `--results-directory <仓库外临时目录>`（例如 `%TEMP%\erp379-...`）写出，绝不落到仓库工作区。
  本任务只允许改动第 5 节列出的文件，仓库内新增的 `TestResults/*.trx` 属于越界工作区改动（`*.log` 被忽略但 `*.trx` 不会），
  因此本任务的测试产物（TRX：`erp379-unit-new.trx` / `erp379-unit-full.trx` / `erp379-sql.trx` / `erp379-sql2.trx`，
  以及本次运行的临时日志）都保存在仓库外临时目录，仓库内不留下任何本任务测试产物。
- 本次（修复尝试）实测证据：Release 构建 `0 警告 / 0 错误`；`ERP.UnitTests` 全量 `5681 通过 / 0 失败`；
  付款单相关单测筛选 `88 通过 / 0 失败`；真实 SQL 集成筛选 `13 通过 / 0 失败`，并在同一专用 localdb 上
  用**第二次独立运行**（另一全新 GUID 库）重复得到 `13 通过 / 0 失败`，覆盖三条双连接竞态
  （提交 vs 修改 / 提交 vs 删除 / 审核 vs 取消）与目标护栏 fail-closed。
- **构建完成不等于阶段验收**：真实 SQL 场景必须在专用 localdb 上真实执行通过才算验收证据；本任务
  `browser_acceptance.required = false`，不运行浏览器 / UI 验收。

## 7. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST`、
  `Integrated Security`；错误目标在访问数据库**之前** fail closed（`AssertDedicatedTarget`）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝（`SELECT DB_ID`）；绝不 drop / reset /
  复用任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings` /
  `.env` / 生产凭据。
- 使用 EF 生成的标识主键；夹具初始化 = `EnsureCreated` + `SchemaUpgrader` + `SeedData` + `SchemaUpgrader`。

## 8. 边界与保留项

- 本护栏只保护付款单生命周期（重点：提交 / 审核）与既有付款引用证据：不会真的付款、不会记账或生成凭证、不会核销、
  不会移动资金，也不改写采购订单状态 / 到货进度 / 金额与明细 / 结算进度、供应商采购发票、采购订单与发票引用证据、
  库存与库存成本、库存流水、退税记录、费用与供应商余额。
- **权限模型不变**：只复用既有 `payment` 菜单与既有数据范围口径（含既有系统内置角色特权），不新增用户 / 角色 /
  菜单 / 权限，也不引入匿名或管理员回退。
- **保留项**：库存来源单据审计（ERP-375 / ERP-376 / ERP-377 的来源选择与来源审计链路）与原始失败日志
  （`run.log` / `TestResults` / `.ai/evidence`）一律保持原样，不删除、不重写；本改动不触碰任何库存来源工作流。
- **依赖与兼容**：ERP-351 / ERP-378 的既有行为、路由、审计与证据语义全部保留；
  `docs/supplier-payment-lifecycle.md` 仍然有效，本文档只补充 ERP-379 的锁内复核与双连接竞态口径。
