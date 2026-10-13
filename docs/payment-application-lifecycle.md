# 货款申请单生命周期护栏（ERP-380）

## 1. 目的

补齐货款申请单（`FinancePaymentApply`）与「引用它的付款单（`FinancePayment.PaymentApplyId`）」之间的运营完整性缺口：

- 原实现中货款申请单 `Create` 接受 `CustomerId / SalesOrderId / Amount / Currency / ExchangeRate` 却没有任何运营校验，
  `Update` 会直接覆盖这些商业字段而不检查是否有付款单引用，`Submit / Approve / Cancel / Delete` 走无条件基类状态逻辑，
  且控制器完全没有身份 / 菜单 / 客户数据范围护栏。
- 付款单侧（ERP-351 / ERP-379）已经从**可变**的货款申请单派生客户数据范围、币种与金额上限、引用证据额度，
  但申请单可以被并发取消 / 删除 / 改金额，导致付款单的来源证据失效或出现超额度 / 孤儿状态。

ERP-380 在不新增表 / 列 / 菜单 / 权限模型、不新增用户授权、不引入匿名或管理员兜底的前提下，通过既有业务 API / 服务补上护栏。

## 2. 关键不变量

1. **调用方闸门先于一切读取 / 行锁 / 单号生成（fail closed）**：列表 / 详情 / 已存储来源 / 来源候选 / 创建 / 修改 /
   提交 / 审核 / 取消 / 删除的**第一步**都是复核**实时启用身份**（账号存在、未删除且启用）与**既有货款申请单
   （`payment-apply`）菜单授权**；只有通过闸门之后才允许任何申请单行 / 客户 · 销售订单 · 付款单主数据读取、计数、
   取行、行锁或单号生成。
   - 因此**未知 Id 与既有 Id 对被拒调用方给出完全相同的受控拒绝**（缺失 / 已删除账号 → 受控未认证；已禁用 /
     无菜单 / 已撤销菜单 → 权限不足）：**绝不先读行再判权，绝不因 Id 是否存在而差异化**，也不泄露「不存在」。
   - 当前身份只来自已认证请求主体（`ClaimTypes.NameIdentifier`）；**缺失 / 非数字 / 非正数 / 未绑定任何
     `HttpContext`（纯进程内直调）一律安全解析为 `null`** 并返回受控未认证。这里**不是**免授权或测试兼容边界：
     不存在「无请求上下文 / 空路径 / 环境变量 / 内存库开关」豁免，也没有内部直调 / 改名旁路，绝不回退为匿名或管理员。
2. **闸门之后仍做三重授权与客户数据范围**：读取申请单（或创建 / 修改时读取客户）后，再按**权威客户**
   复核身份 / 菜单 / 客户数据范围（受限制业务员越界 → 权限不足且不泄露范围外客户）；创建 / 修改还必须在申请单行锁内
   同时校验**既有持久化客户**与**请求客户**都在当前范围内，被拒编辑绝不改写任何原始字段。列表的计数与取行仍在
   范围过滤之后。
3. **商业字段完整性**：客户必须存在、未删除、启用；币种必须在既有系统币种口径内；金额按币种精度取整（0.5 进位）后
   必须大于 0；汇率必须大于 0。
4. **来源销售订单精确资格（绝不猜来源）**：可空 `SalesOrderId` 为 `null` 时保留历史「未关联来源」语义；
   非空时必须按 Id 精确解析到**既有、未删除、未取消**的销售订单，且订单客户与申请单客户一致、币种兼容；
   悬空 / 已删除 → 不存在；已取消 / 跨客户 / 币种不兼容 → 规则冲突。绝不按订单号文本、金额或相似度匹配。
5. **引用付款护栏**：只要存在任一**未删除且未取消**的付款单引用本申请单，就拒绝取消 / 删除申请单，也拒绝修改
   客户 / 来源 / 币种 / 金额 / 汇率（非商业字段如申请日期 / 银行账户 / 收款方 / 事由 / 备注仍可修改）；
   要解除限制必须先**显式取消**相关付款单，取消只改状态、历史付款单与审计原样保留（绝不物理删除、绝不静默改写）。
6. **提交 / 审核**在申请单行锁内用既有规则复核**持久化**客户 / 币种 / 金额 / 汇率 / 来源链接（不新增审批要求、
   不新增单据状态），非法持久化数据不会被流转放行。

## 3. 原子性与并发（锁序）

- **调用方闸门先于锁**：修改 / 提交 / 审核 / 取消 / 删除都在开启事务与取得**任何**行锁**之前**先复核实时身份 +
  货款申请单菜单授权；被拒调用方（缺失 / 已删除 / 已禁用 / 无菜单）绝不开启事务、绝不加锁、绝不读取业务行。
  未知 Id 与既有 Id 的受控拒绝完全一致。
- **统一锁序（ERP-380，跨模块）**：**先来源货款申请单行、后付款单行（再引用行）**，绝不反向获取。
  - 申请单的修改 / 提交 / 审核 / 取消 / 删除：同一事务内先取**申请单行锁**
    （`FinancePaymentApplyLifecycleRules.LockApplyRowAsync`：EF Core 基础 API 对申请单行发一条审计时间戳刷新 UPDATE
    取得 X 锁，持有至事务结束，语义等价于 `SELECT ... WITH (UPDLOCK, HOLDLOCK)` 但不依赖关系型扩展）。
  - 付款单的创建 / 修改 / 提交 / 审核 / 取消 / 删除：先按持久化 `PaymentApplyId` 取**来源申请单行锁**，再取付款单行锁
    （`SupplierPaymentLifecycleRules.LockPaymentRowAsync`），并在锁内复核来源链接未被并发改写
    （`FinancePaymentController.LockPaymentWithSourceAsync`）。
  - 两套付款引用证据写入（`SupplierPaymentAllocationService.CreateAsync` /
    `SupplierPaymentInvoiceAllocationService.CreateAsync`）：同样先取来源申请单行锁、再取付款单行锁，并在锁内
    重新复核来源申请单仍**未删除且未取消**（`EnsureSourceApplyUsableAsync`）——分配合格性绝不基于陈旧或孤立的来源。
  - 若并发方已先落库触发 `RowVersion` 乐观并发冲突，则转成可读业务冲突拒绝（fail closed），绝不静默覆盖赢家。
- **竞态结论**：
  - 「申请单取消 vs 付款单创建」：串行化后恰好一个成功——取消先赢则付款创建在锁内重解析来源时被拒（不产生新付款单），
    付款创建先赢则取消看到未取消的引用付款单而拒绝（申请单保持原状态）。
  - 「申请单金额改动 vs 付款单创建」：串行化后恰好一个成功——改动先赢则超额的付款创建被拒，创建先赢则商业改动被护栏拒绝；
    绝不出现「付款金额 > 申请金额」或半成品付款单 / 引用证据。
- 失败整体回滚：状态、原始字段、删除标记与审计不变；本护栏不产生任何资金记账 / 结算 / 库存流水。

## 4. 接口

沿用既有货款申请单路由，不新增路由、不改变请求 / 响应结构；**定金申请单路由保持不变**。
每条路由都先过调用方闸门（实时启用身份 + 既有 `payment-apply` 菜单），通过后才计数 / 取行 / 生成单号 / 加锁：

- `GET /api/finance/payment-applies`（分页，身份 / 菜单授权 + 客户数据范围过滤在计数与取行之前）
- `GET /api/finance/payment-applies/{id}`（详情，闸门先于申请单读取；未知 Id 与既有 Id 对无菜单 / 停用 / 缺失账号同样拒绝）
- `GET /api/finance/payment-applies/{id}/sales-order-source`（已存储来源只读展示，闸门先于读取）
- `GET /api/finance/payment-applies/sales-order-candidates`（来源候选，闸门 + 客户范围先于候选取数）
- `POST /api/finance/payment-applies`（创建，闸门先于客户 / 来源主数据读取，校验后才生成单号）
- `PUT /api/finance/payment-applies/{id}`（仅待提交可改；闸门先于申请单行锁；商业改动受引用付款护栏约束）
- `POST /api/finance/payment-applies/{id}/submit` / `/approve` / `/cancel`
- `DELETE /api/finance/payment-applies/{id}`（仅待提交可删，且受引用付款护栏约束）

## 5. 关键文件

- `src/ERP.Application/Services/FinancePaymentApplyLifecycleRules.cs`（纯规则 + 有界只读查询 + 申请单行锁）
- `src/ERP.Api/Controllers/FinanceApplyControllers.cs`（仅 `FinancePaymentApplyController` 生命周期护栏；定金申请单路由不变）
- `src/ERP.Api/Controllers/FinancePaymentController.cs`（ERP-380 锁序：先来源申请单行、后付款单行 + 锁内来源复核）
- `src/ERP.Application/Services/SupplierPaymentLifecycleRules.cs`（付款单行锁与锁序文档口径）
- `src/ERP.Application/Services/SupplierPaymentAllocationService.cs` / `SupplierPaymentInvoiceAllocationService.cs`
  （引用证据写入的申请单行锁与来源复核）
- `src/ERP.UnitTests/FinancePaymentApplyLifecycleTests.cs`（内存库）
- `src/ERP.IntegrationTests/FinancePaymentApplyLifecycleSqlServerTests.cs`（真实 SQL）

## 6. 测试与验证

- 单元测试：`src/ERP.UnitTests/FinancePaymentApplyLifecycleTests.cs`（内存库，`TestDbFactory`），覆盖实时启用身份 /
  revoked 菜单 / 停用账号 / 受限制业务员范围（列表计数在范围过滤之后、范围外详情与流转拒绝）、不受支持币种 /
  精度取整后非正金额 / 非正汇率 / 不可用或越界客户、来源订单的悬空 / 已删除 / 已取消 / 跨客户 / 币种不兼容拒绝与
  空来源历史语义放行、商业字段按币种精度取整、引用付款单对取消 / 删除 / 商业改动的拒绝与显式取消 / 软删除后的释放、
  已驳回付款单仍受保护、非商业改动放行、被拒编辑不改写原始字段、提交 / 审核的持久化商业字段与来源复核，
  以及**调用方闸门先于读取**矩阵：未认证 / 已删除 / 停用 / 无菜单（含已撤销菜单）账号在**全部路由**（列表 / 详情 /
  已存储来源 / 来源候选 / 创建 / 修改 / 提交 / 审核 / 取消 / 删除）上，对**未知 Id 与既有 Id** 得到完全一致的受控拒绝、
  零业务查询零占号零写入，**未绑定 `HttpContext` 的纯进程内直调**同样受控未认证（无免授权 / 测试兼容豁免），
  以及控制器源码契约（闸门先于申请单读取、行锁与单号生成，且未触碰定金申请单控制器）。
- SQL Server 集成测试：`src/ERP.IntegrationTests/FinancePaymentApplyLifecycleSqlServerTests.cs`，覆盖真实
  own / foreign / revoked / disabled 四类身份与范围判定、非法币种 / 金额 / 汇率 / 来源、被拒编辑不改写（含审计时间戳）、
  显式取消付款单释放护栏、分配合格性在锁内复核来源申请单，**调用方闸门先于读取**（缺失 / 已删除 / 已禁用 / 已撤销菜单 /
  无菜单账号对**未知 Id 与既有 Id** 的受控拒绝完全一致且零写入、零审计变化），以及**两个独立连接竞态**
  （申请单取消 vs 付款单创建、申请单金额改动 vs 付款单创建）：串行化后结果唯一一致，无陈旧来源、超额付款、
  孤儿引用或半成品写入，且不产生任何资金记账 / 结算行。
- 安全验证档：`dotnet restore` + Release 构建（`TreatWarningsAsErrors` / 分析器开启）+ `ERP.UnitTests` 全套。
- **构建完成不等于阶段验收**：真实 SQL 场景只有在受控 localdb 上真实执行通过才算验收证据。

## 7. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST`、`Integrated Security`；
  错误目标在访问数据库**之前** fail closed（`AssertDedicatedTarget`，含 fail-closed 单元覆盖）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings` / `.env` /
  生产凭据；使用 EF 生成的标识主键，绝不使用生产数据。

## 8. 边界

本护栏只保护货款申请单生命周期与既有付款来源链接：不会真的付款、不会记账或生成凭证、不会核销、不会移动资金，
也不改写销售订单状态 / 金额与明细、付款单与两套付款引用证据、库存与库存成本、库存流水、退税记录或供应商余额；
不新增菜单 / 角色 / 用户授权，也不把空身份当作管理员。定金申请单（`FinanceDepositApply`）路由与语义保持不变。

## 9. 2026-10-13 验收恢复：调用方闸门先于读取

原实现中 `FinancePaymentApplyController.GetById`（`FinanceApplyControllers.cs`）**先加载申请单行、再调用**
`FinancePaymentApplyLifecycleRules.EnsureAuthorizedAsync`：未知 Id 返回 `NotFound`，而既有 Id 对缺失 / 已禁用 /
已删除 / 无菜单调用方返回受控未认证 / 权限不足 —— 同一被拒调用方因「Id 是否存在」得到**可区分的响应**，
违反「身份 / 菜单先于行」的原始口径；修改 / 提交 / 审核 / 取消 / 删除也**先加锁并读取业务行、再判权**。

本次恢复（仅改动共享文件中的 `FinancePaymentApplyController`，`FinanceDepositApplyController` 原样不变）：

- 每个路由的**第一步**都是 `FinancePaymentApplyLifecycleRules.EnsureMenuAuthorizedAsync`（实时启用身份 + 既有
  `payment-apply` 菜单）；通过之后才计数 / 取行 / 读客户 · 来源主数据 / 取申请单行锁 / 生成单号。
- 身份解析改为控制器内的 `private new long? CurrentUserId()`：只读取已认证主体的 `ClaimTypes.NameIdentifier`，
  **缺失 / 非数字 / 非正 / 未绑定 `HttpContext` 一律安全解析为 `null`** → 由闸门返回受控未认证；
  **不存在**「无请求上下文 / 空路径 / 环境变量 / 数据库提供程序 / 内存库开关」豁免，**没有**内部直调或改名旁路。
- 闸门之后仍保留既有三重授权（权威客户范围）、商业字段完整性、按 Id 精确来源资格、引用付款单取消护栏与
  **先来源申请单行、后付款单行**的统一锁序；持久化来源链接在锁内复核，历史空来源语义与显式取消付款单释放语义不变。
- 证据：单元测试新增「未认证 / 已删除 / 停用 / 无菜单 / 无 `HttpContext`」×「全部路由」×「未知 Id vs 既有 Id」
  受控拒绝一致矩阵 + 控制器源码契约断言；真实 SQL 新增
  `Controller_PreReadGate_UnknownAndExistingId_SameControlledRejection_NoWrite`（own / disabled / revoked / no-menu /
  deleted 五类真实启用身份，未知 Id 与既有 Id 拒绝一致、被拒请求零写入零审计变化），
  与既有两个独立连接竞态用例一并执行。
