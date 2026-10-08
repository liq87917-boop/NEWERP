# 客户收款单生命周期护栏（ERP-349）

## 1. 目的

补齐客户收款单（`FinanceReceipt`）与两套既有收款分摊证据之间的运营完整性缺口：

- **ERP-053**：客户收款单 → 销售订单 收款引用证据（`CustomerReceiptAllocation`）。
- **ERP-071**：客户收款单 → 代理服务费对账单 收款分摊证据（`AgencyServiceFeeCollectionAllocation`）。

原实现中，收款单 `Create` 接受 `Amount / Currency / CustomerId` 却无运营校验，`Update` 会直接覆盖这些值而不检查
既有分摊证据，`Cancel / Delete` 走无条件基类状态逻辑，且两套分摊写入只在**创建时**以当时的收款单金额做上限，
没有与收款单生命周期互斥、也没有并发护栏。ERP-349 在不新增表 / 列 / 权限模型的前提下，通过既有业务 API / 服务补上护栏。

## 2. 关键不变量

1. **身份 / 菜单 / 客户数据范围**：创建 / 修改 / 提交 / 审核 / 取消 / 删除与读取每次都重新校验
   当前身份（缺失 → 未认证）、收款单（`receipt`）菜单授权（缺失 → 权限不足）与客户数据范围（越界 → 权限不足，
   不泄露范围外客户）。
2. **真实可用客户、受支持币种、正金额**：客户必须存在、未删除、启用；币种必须在系统币种口径内；金额按币种精度
   取整（0.5 进位）后必须大于 0。
3. **有效分摊证据保护**：只要收款单存在任一**有效**（未作废、未删除）的 ERP-053 或 ERP-071 分摊证据，就拒绝
   取消 / 删除收款单，也拒绝以破坏证据的方式修改客户 / 币种 / 金额。要解除限制必须先走既有显式作废服务
   （`CustomerReceiptAllocationService.VoidAsync` / `AgencyServiceFeeCollectionAllocationService.VoidAsync`），
   作废保留原始证据、绝不物理删除、绝不静默改写。
4. **证据维度分离**：两套分摊证据各自独立计算、绝不把金额相加；也绝不跨币种合计或重复计算证据。

## 3. 原子性与并发

- 收款单生命周期操作与两套分摊证据写入，都在**同一事务**内先对收款单行加**排它行锁**
  （`CustomerReceiptLifecycleRules.LockReceiptRowAsync`：用 EF Core 基础 API 对收款单行发一条审计时间戳刷新 UPDATE
  取得 X 锁，持有至事务结束，语义等价于 `SELECT ... WITH (UPDLOCK, HOLDLOCK)` 但不依赖关系型扩展），
  再读权威金额与状态后落库；若并发方已先落库触发 `RowVersion` 乐观并发冲突，则转成可读业务冲突拒绝（fail closed），
  绝不静默覆盖赢家。
- 两套分摊服务（`CustomerReceiptAllocationService.CreateAsync` / `AgencyServiceFeeCollectionAllocationService.CreateAsync`）
  在锁内重读收款单，并在收款单已取消时拒绝新增证据，从而与「收款单取消 / 改金额」互斥。
- 并发「登记证据 vs 取消」「登记证据 vs 改金额」「两笔并发分摊」不可能同时成功：先提交者生效，后提交者看到
  已变更的状态 / 已占用额度而拒绝，失败时原始收款单与分摊证据保持不变。

## 4. 接口

沿用既有收款单路由，不新增路由、不改变请求 / 响应结构：

- `GET /api/finance/receipts`（分页，按客户数据范围过滤）
- `GET /api/finance/receipts/{id}`（详情，越界 fail closed）
- `POST /api/finance/receipts`（创建，校验客户 / 币种 / 金额）
- `PUT /api/finance/receipts/{id}`（仅待提交可改；改客户 / 币种 / 金额前校验有效分摊证据）
- `POST /api/finance/receipts/{id}/submit` / `/approve` / `/cancel`
- `DELETE /api/finance/receipts/{id}`（仅待提交可删，先校验有效分摊证据）

## 5. 关键文件

- `src/ERP.Application/Services/CustomerReceiptLifecycleRules.cs`（纯规则 + 有界只读查询 + 收款单行锁）
- `src/ERP.Api/Controllers/FinanceReceiptComplaintControllers.cs`（`FinanceReceiptController` 生命周期护栏）
- `src/ERP.Application/Services/CustomerReceiptAllocationService.cs`（ERP-053 登记加锁 + 已取消拒绝）
- `src/ERP.Application/Services/AgencyServiceFeeCollectionAllocationService.cs`（ERP-071 登记加锁）
- `src/ERP.UnitTests/CustomerReceiptLifecycleTests.cs`（内存库）
- `src/ERP.IntegrationTests/CustomerReceiptLifecycleSqlServerTests.cs`（真实 SQL）

## 6. 测试与验证

- 单元测试：`src/ERP.UnitTests/CustomerReceiptLifecycleTests.cs`（内存库，`TestDbFactory`）。
- 安全验证档：`dotnet restore` + Release 构建（`TreatWarningsAsErrors` / 分析器开启）+ `ERP.UnitTests`。
- SQL Server 集成测试：`src/ERP.IntegrationTests/CustomerReceiptLifecycleSqlServerTests.cs`，
  覆盖有效收款引用拒绝改金额 / 取消、作废释放、有效代理服务费分摊拒绝改金额、并发分摊竞态与并发「分摊 vs 取消」竞态。

## 7. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST`、`Integrated Security`；
  错误目标在访问数据库**之前** fail closed（`AssertDedicatedTarget`）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings` / `.env` / 生产凭据。
- 使用 EF 生成的标识主键；构建完成不等于阶段验收，真实 SQL 场景需在受控 localdb 上通过。

## 8. 边界

本护栏只保护收款单生命周期与既有收款分摊证据：不会真的收款、不会记账或生成凭证、不会核销、不会移动资金，
也不改写销售订单、库存与库存成本、供应商付款与采购发票、装柜与单证、客户信用状态或任何其它既有单据。
