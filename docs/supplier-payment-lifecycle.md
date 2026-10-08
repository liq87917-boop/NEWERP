# 供应商付款单生命周期护栏（ERP-351）

## 1. 目的

补齐供应商付款单（`FinancePayment`）与两套既有付款引用证据之间的运营完整性缺口：

- **ERP-049**：供应商付款单 → 采购订单 付款引用证据（`SupplierPaymentAllocation`）。
- **ERP-066**：供应商付款单 → 供应商采购发票 付款引用证据（`SupplierPaymentInvoiceAllocation`）。

原实现中，付款单 `Create` 直接接受 `Amount / Currency / SupplierId / PaymentApplyId` 却无运营校验，`Update` 会直接覆盖这些值而不检查
既有引用证据，`Cancel / Delete` 走无条件基类状态逻辑，且两套引用写入只在**创建时**以当时的付款单金额做各自维度的上限，
没有与付款单生命周期互斥、也没有并发护栏。ERP-351 在不新增表 / 列 / 权限模型的前提下，通过既有业务 API / 服务补上护栏，
并把两套引用写入的「付款单金额上限」升级为**同一张付款单的唯一、同币种分摊额度**：两套有效引用行在付款单行锁下**共同占用**同一额度。

## 2. 关键不变量

1. **身份 / 菜单 / 客户数据范围**：创建 / 修改 / 提交 / 审核 / 取消 / 删除与读取每次都重新校验当前身份（缺失 → 未认证）、
   付款单（`payment`）菜单授权（缺失 → 权限不足）与客户数据范围（越界 → 权限不足，不泄露范围外付款单）。
   付款单未关联货款申请单时没有可派生的客户，仅特权账号可通过，受限制账号 fail closed。
2. **真实可用供应商、受支持币种、正金额**：供应商必须存在、未删除、启用；币种必须在系统币种口径内；金额按币种精度
   取整（0.5 进位）后必须大于 0。
3. **货款申请单来源按 Id 解析**：提供 `PaymentApplyId` 时必须按 Id 解析到既有、未删除且未取消的申请单，并校验币种一致、
   付款金额不超过申请金额、客户在数据范围内；Id 不存在 / 已删除即按「来源不存在」拒绝（dangling / forged），
   绝不按申请单号文本或金额猜测来源。未提供 Id 时保留有效的历史未关联付款场景。
4. **有效付款引用证据保护**：只要付款单存在任一**有效**（未作废、未删除）的 ERP-049 或 ERP-066 引用证据，就拒绝
   取消 / 删除付款单，也拒绝以破坏证据的方式修改供应商 / 币种 / 金额。要解除限制必须先走既有显式作废服务
   （`SupplierPaymentAllocationService.VoidAsync` / `SupplierPaymentInvoiceAllocationService.VoidAsync`），
   作废保留原始证据、绝不物理删除、绝不静默改写。
5. **同一付款单的唯一同币种分摊额度**：同一张付款单的金额是唯一、同币种的分摊额度；
   「付款单 → 采购订单」与「付款单 → 供应商采购发票」两套**有效（未删除、未作废）**引用行在付款单行锁下**共同占用**
   同一额度，任一写入都先把两套有效行合计后与付款单权威金额比较（每个维度各计一次、绝不重复计算）。
   发票侧额度仍只扣减发票维度（`SupplierPaymentInvoiceAllocations`）的有效引用。

## 3. 原子性与并发

- 付款单生命周期操作（修改供应商 / 币种 / 金额、取消、删除）与两套引用证据写入，都在**同一事务**内先对付款单行加**排它行锁**
  （`SupplierPaymentLifecycleRules.LockPaymentRowAsync`：用 EF Core 基础 API 对付款单行发一条审计时间戳刷新 UPDATE
  取得 X 锁，持有至事务结束，语义等价于 `SELECT ... WITH (UPDLOCK, HOLDLOCK)` 但不依赖关系型扩展），
  再读权威金额与状态后落库；若并发方已先落库触发 `RowVersion` 乐观并发冲突，则转成可读业务冲突拒绝（fail closed），
  绝不静默覆盖赢家。
- 两套引用服务（`SupplierPaymentAllocationService.CreateAsync` / `SupplierPaymentInvoiceAllocationService.CreateAsync`）
  在锁内重读付款单，并在付款单已取消时拒绝新增证据，从而与「付款单取消 / 改金额」互斥。
- 付款单侧额度是**跨两套证据共享**的：任一写入都在付款单行锁内把两套有效引用行合计后与付款单权威金额比较。
- 并发「登记证据 vs 取消」「登记证据 vs 改金额」「两笔并发引用（含跨维度）」不可能同时成功：先提交者生效，
  后提交者看到已变更的状态 / 已占用额度而拒绝，失败时原始付款单与引用证据保持不变。

## 4. 接口

沿用既有付款单路由，不新增路由、不改变请求 / 响应结构：

- `GET /api/finance/payments`（分页，按客户数据范围过滤）
- `GET /api/finance/payments/{id}`（详情，越界 fail closed）
- `POST /api/finance/payments`（创建，校验供应商 / 币种 / 金额 / 货款申请单来源 / 权限）
- `PUT /api/finance/payments/{id}`（仅待提交可改；改供应商 / 币种 / 金额前校验有效引用证据）
- `POST /api/finance/payments/{id}/submit` / `/approve` / `/cancel`
- `DELETE /api/finance/payments/{id}`（仅待提交可删，先校验有效引用证据）

## 5. 关键文件

- `src/ERP.Application/Services/SupplierPaymentLifecycleRules.cs`（纯规则 + 有界只读查询 + 付款单行锁 + 唯一分摊额度）
- `src/ERP.Api/Controllers/FinancePaymentController.cs`（`FinancePaymentController` 生命周期护栏）
- `src/ERP.Application/Services/SupplierPaymentAllocationService.cs`（ERP-049 登记加锁 + 已取消拒绝 + 跨维度额度）
- `src/ERP.Application/Services/SupplierPaymentInvoiceAllocationService.cs`（ERP-066 登记加锁 + 已取消拒绝 + 跨维度额度）
- `src/ERP.UnitTests/SupplierPaymentLifecycleTests.cs`（内存库）
- `src/ERP.IntegrationTests/SupplierPaymentLifecycleSqlServerTests.cs`（真实 SQL）

## 6. 测试与验证

- 单元测试：`src/ERP.UnitTests/SupplierPaymentLifecycleTests.cs`（内存库，`TestDbFactory`）。
- 安全验证档：`dotnet restore` + Release 构建（`TreatWarningsAsErrors` / 分析器开启）+ `ERP.UnitTests`。
- SQL Server 集成测试：`src/ERP.IntegrationTests/SupplierPaymentLifecycleSqlServerTests.cs`，
  覆盖两套引用类型、精确合计 / 超额、作废释放、未关联付款、不兼容申请单 / 供应商 / 币种、拒绝 / 撤销调用方、
  失败保持原始金额与证据、付款取消 vs 引用与跨消费者并发引用竞态。

## 7. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST`、`Integrated Security`；
  错误目标在访问数据库**之前** fail closed（`AssertDedicatedTarget`）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings` / `.env` / 生产凭据。
- 使用 EF 生成的标识主键；构建完成不等于阶段验收，真实 SQL 场景需在受控 localdb 上通过。

## 8. 边界

本护栏只保护付款单生命周期与既有付款引用证据：不会真的付款、不会记账或生成凭证、不会核销、不会移动资金，
也不改写采购订单状态 / 到货进度 / 金额与明细 / 结算进度、供应商采购发票、采购订单引用证据、发票引用证据、
库存与库存成本、库存流水、退税记录、费用与供应商余额。
