# 收款单生命周期同单串行化（ERP-378）

## 1. 目的

补上 ERP-349 / ERP-350 之后仍存在的运营完整性缺口：`FinanceReceipt` 的**修改 / 提交 / 审核 / 取消 / 删除**
当时仍是「先读、再判定、后落库」的无锁写法（`FinanceReceiptController.Update/Submit/Approve/Cancel/Delete`），
而两套收款分摊证据写入（ERP-053 `CustomerReceiptAllocations`、ERP-071 `AgencyServiceFeeCollectionAllocations`）
与 ERP-350 唯一同币种额度计算已经在**收款单行锁**下执行。

因此并发下可能出现：某次取消 / 删除或改变客户 / 币种 / 金额的操作与某次分摊写入交错，导致**有效证据指向已被取消
或被改写的收款单**；审核与取消、提交与修改之间也可能互相覆盖。

ERP-378 在不新增表 / 列 / 菜单 / 权限模型的前提下，把收款单的**五个生命周期动作**统一到与两套分摊证据写入、
显式作废服务**同一条锁序、同一个事务**上，并在锁内用**实时身份 / 授权 / 数据范围**复核。

## 2. 关键不变量

1. **实时启用身份 + 既有菜单授权 + 客户数据范围**：每次请求都重新查询 `SysUsers`（账号必须存在、未删除且启用）、
   「角色 → 菜单」（`receipt`「收款单」模块授权）与 `SalespersonDataScopeService`（ERP-097 唯一权威口径）。
   停用账号、撤销授权、越界客户一律 fail closed，**绝不退化为匿名或管理员**。
2. **单事务 + 收款单行锁**：修改 / 提交 / 审核 / 取消 / 删除都在**同一个事务**内先对收款单行加排它行锁，
   再加载权威状态 / 客户 / 币种 / 金额；任一校验失败整体回滚——状态、原始字段与审计时间戳都不变。
3. **有效证据保护**：只要收款单存在任一**有效**（未作废、未删除）的 ERP-053 或 ERP-071 分摊证据，就拒绝
   取消 / 删除收款单，也拒绝以破坏证据的方式修改客户 / 币种 / 金额；要解除限制必须先走既有显式作废服务
   （`CustomerReceiptAllocationService.VoidAsync` / `AgencyServiceFeeCollectionAllocationService.VoidAsync`），
   作废保留原始金额、快照与审计历史，**绝不物理删除、绝不静默改写**。
4. **同一收款单的唯一同币种分摊额度（ERP-350，保持不变）**：收款单金额是唯一、同币种的分摊额度；
   「收款单 → 销售订单」与「收款单 → 代理服务费对账单」两套有效行在收款单行锁下**共同占用**同一额度，
   每套证据各计一次、绝不跨币种合计、绝不重复计算。
5. **允许的状态流转与软删除语义（保持不变）**：提交仅 `Pending → Submitted`、审核仅 `Submitted → Approved`、
   取消把非取消单据置为 `Cancelled`、删除仅 `Pending` 可软删除（`IsDeleted = true`）。

## 3. 原子性与并发（锁序）

- **唯一锁**：`CustomerReceiptLifecycleRules.LockReceiptRowAsync`——在调用方事务内对收款单行发一条「审计时间戳
  刷新」的 `UPDATE`（仅用 EF Core 基础 API，不依赖关系型扩展），取得排它行锁（X 锁）并持有至事务结束，
  语义等价于 `SELECT ... WITH (UPDLOCK, HOLDLOCK)`。`RowVersion` 乐观并发令牌保证「锁竞争失败方」以可读业务冲突
  收尾（fail closed，绝不静默覆盖赢家）。
- **统一锁序（先收款单行、后分摊行）**：
  - 收款单生命周期：`FinanceReceiptController.Update/Submit/Approve/Cancel/Delete`（先锁 → 读权威 → 复核 → 写 → 提交）；
  - 证据写入：`CustomerReceiptAllocationService.CreateAsync`、`AgencyServiceFeeCollectionAllocationService.CreateAsync`；
  - 证据作废：`CustomerReceiptAllocationService.VoidAsync`、`AgencyServiceFeeCollectionAllocationService.VoidAsync`
    （ERP-378 补齐：作废现在也在同一事务内先取收款单行锁，再读分摊行做权威判定与写入）。
  全模块不反向获取下游锁。
- **竞态结果**：并发「登记证据 vs 取消」「修改 vs 提交」「审核 vs 取消」只能串行执行——
  「登记证据 vs 取消」必然只有一方成功（另一方看到已取消 / 已有有效证据而拒绝）；
  「修改 vs 提交」「审核 vs 取消」给出唯一一致的串行化结果，且**没有孤儿分摊、超额资金或半成品写入**。
- 内存库等非关系型提供程序无行锁语义：`IsRelationalProvider` 为 false 时跳过事务与锁定（等价无事务），
  声明式业务校验与写入语义不受影响。

## 4. 接口

沿用既有收款单路由，**不新增路由、不改变请求 / 响应结构**：

- `GET /api/finance/receipts`（分页，按客户数据范围过滤）
- `GET /api/finance/receipts/{id}`（详情，越界 fail closed）
- `POST /api/finance/receipts`（创建，校验客户 / 币种 / 正金额）
- `PUT /api/finance/receipts/{id}`（仅待提交可改；改客户 / 币种 / 金额前校验有效分摊证据）
- `POST /api/finance/receipts/{id}/submit` / `/approve` / `/cancel`
- `DELETE /api/finance/receipts/{id}`（仅待提交可删，先校验有效分摊证据）

同一文件中的客诉单控制器（`FinanceComplaintController`）**未被改动**。

## 5. 关键文件

- `src/ERP.Application/Services/CustomerReceiptLifecycleRules.cs`（实时启用身份校验 + 纯规则 + 有界只读查询 + 收款单行锁 / 锁序）
- `src/ERP.Api/Controllers/FinanceReceiptComplaintControllers.cs`（五个生命周期动作的事务 + 行锁 + 锁内复核）
- `src/ERP.Application/Services/CustomerReceiptAllocationService.cs`（ERP-053 写入加锁 + 作废加锁）
- `src/ERP.Application/Services/AgencyServiceFeeCollectionAllocationService.cs`（ERP-071 写入加锁 + 作废加锁）
- `src/ERP.UnitTests/ReceiptLifecycleConcurrencyTests.cs`（内存库：实时身份 / 授权 / 范围、失败方不改写、证据保护与作废释放、ERP-350 共享额度）
- `src/ERP.IntegrationTests/ReceiptLifecycleConcurrencySqlServerTests.cs`（真实 SQL：真实控制器 + 两个独立连接竞态）
- `docs/customer-receipt-lifecycle.md`（ERP-349/350 原有说明，继续保持有效）

## 6. 测试与验证

- 单元测试（内存库 `TestDbFactory`，无 SQL）：
  `dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build --filter FullyQualifiedName~ReceiptLifecycleConcurrencyTests`
  覆盖：停用账号 / 撤销授权 / 越界客户时五个动作全部拒绝且状态、原始字段与审计不变；失败方（审核未提交、
  重复取消）不改写状态与审计；有效证据拒绝取消 / 删除 / 改金额 / 改客户 / 改币种，显式作废释放且保留历史行；
  ERP-350 唯一同币种额度跨两套消费者共享、超额拒绝、作废后释放、无孤儿分摊；允许的状态流转。
- SQL Server 集成测试（受控 localdb，真实控制器 + 两个独立连接）：
  `dotnet test src/ERP.IntegrationTests/ERP.IntegrationTests.csproj -c Release --no-build --filter FullyQualifiedName~ReceiptLifecycleConcurrencySqlServerTests`
  覆盖：有效证据保护与作废释放（真实控制器）、停用账号 / 撤销授权 fail closed 且原始状态 / 字段 / 审计不变、
  状态流转与失败审核不改写审计，以及三条双连接竞态：「登记分摊 vs 取消」「修改 vs 提交」「审核 vs 取消」。
- 安全验证档（本任务交付门）：Release 构建（`/p:TreatWarningsAsErrors=true`，分析器开启）+ `ERP.UnitTests` 全量。
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

- 本护栏只保护收款单生命周期与既有收款分摊证据：不会真的收款、不会记账或生成凭证、不会核销、不会移动资金，
  也不改写销售订单、库存与库存成本、库存流水、采购与供应商付款、装柜与单证、客户信用状态或任何其它既有单据。
- **权限模型不变**：只复用既有 `receipt` 菜单与既有数据范围口径，不新增用户 / 角色 / 菜单 / 权限，也不引入
  匿名或管理员回退。
- **保留项**：库存来源单据审计（ERP-375 / ERP-376 / ERP-377 的来源选择与来源审计链路）与原始失败日志
  （`run.log` / `TestResults` / `.ai/evidence`）一律保持原样，不删除、不重写；本改动不触碰任何库存来源工作流。
- **依赖与兼容**：ERP-349 / ERP-350 的既有行为、路由、审计与证据语义全部保留；`docs/customer-receipt-lifecycle.md`
  仍然有效，本文档只补充 ERP-378 的锁内复核与双连接竞态口径。
