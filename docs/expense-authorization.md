# 费用单与装柜费用分摊授权护栏说明（ERP-385 / ERP-496）

- 任务：`ERP-385`「Enforce live expense ownership across CRUD and allocation evidence routes」
  + `ERP-496`「Enforce expense bill and allocation authority independently of the MVC request pipeline」
- 权威实现：
  - `src/ERP.Application/Services/ExpenseAuthorizationRules.cs`（纯规则 + 实时授权 + 权威客户范围 + 写入校验）
  - `src/ERP.Api/Controllers/ExpenseBillController.cs`（费用 CRUD / 传统分摊 / ERP-042 分摊批次路由，**每个动作内实时授权**）
  - `src/ERP.Api/Controllers/BaseCrudController.cs`（通用 CRUD 基类；ERP-496 起**不再**承载费用授权过滤器）
  - `src/ERP.Api/Controllers/ContainerExpenseAllocationEvidenceController.cs`（ERP-060 分摊证据读取）
  - `src/ERP.Application/Services/ContainerExpenseAllocationService.cs`（批次预览 / 生成 / 台账 / 详情 / 作废，范围参数必填）
  - `src/ERP.Application/Services/ContainerExpenseAllocationEvidenceService.cs`（分摊证据只读派生）

## 1. 授权口径

| 环节 | 口径 |
| --- | --- |
| 实时身份 | 每个路由动作在读取任何计数 / 参与方 / 生成单号 / 写入之前重新解析身份：缺失 / 非法 / 为零 / 未知 / 已删除按未认证拒绝；账号已禁用按权限不足拒绝 |
| 既有菜单 | 复用已部署的「费用单」（`expense-bill`）菜单：非特权账号必须显式具备，撤销后下一次请求立即收敛（无缓存） |
| 业务员映射 | 复用 ERP-097 `SalespersonDataScopeService` 唯一权威口径：受限账号按其登录名精确匹配员工编码；未映射业务员的受限账号 **fail closed**，绝不降级为全局 / 管理员可见 |
| 特权账号 | 超级管理员 / 系统内置角色 / 显式配置的特权角色保留既有全部访问（含历史无主行与共享柜） |
| 身份来源 | 只来自已认证请求主体（`ClaimTypes.NameIdentifier`）；请求体（`ContainerExpenseAllocationRequest` 等）**不包含也不接受**任何账号 / 角色 / 范围字段 |
| 请求形状无关 | 判定与 `Request.Path` 是否赋值、请求形状、以及控制器是否绑定 `HttpContext` **完全无关**：空路径、已赋值路径与未绑定任何请求上下文的纯进程内直调对同一身份给出完全相同的判定（契约见 `ExpenseAuthorizationRules.PathIndependenceText`） |

**ERP-496**：费用单控制器**每一个路由动作**的第一步都是
`ExpenseAuthorizationRules.EnsureMenuAuthorizedAsync(_db, CurrentUserId())`（`ExpenseBillController.EnsureExpenseAuthorizedAsync`），
解析出**非空**权威客户范围后才读取任何计数 / 行 / 金额 / 分摊基数或写入。原 HTTP 入口动作过滤器
`ExpenseRequestAuthorizationFilter`、它的 `HttpContext.Items` 范围通道（`ExpenseRequestAuthorizationFilter.ScopeItemKey`）与
`BaseCrudController.ExpenseRequestScope` 属性已**彻底移除**，「进程内调用范围只能为 `null`（= 不受限）」的旁路不复存在：
缺少请求上下文 / 缺少身份声明 / 非数字 / 非正数一律安全解析为 `null` 并由规则返回受控未认证。
`ExpenseScopeFilter` / `ApplyExpenseScope` / `BatchScopeFilter` / `EnsureBatchScopeAllowedAsync` /
`EnsureLoadingListScopeAllowedAsync` / `EnsureCustomerInScope` / `EnsureStoredExpenseScopeAllowed` /
`EnsureProposedExpenseValidAsync` / `EnsureCustomersValidAsync` 的范围参数**不再接受 `null`**（特权账号以
`AllowedCustomerIds is null` 表示「不限制」，绝不与「未解析身份」混同），`ContainerExpenseAllocationService`
的全部批次入口也把范围参数改为**必填**。**绝无匿名 / 管理员 / 环境 / 测试专用开关回退。**
仅分摊证据模块（ERP-060，本任务未改动其路由，已在各自控制器动作内先完成实时授权）的只读派生入口仍以
`dataScope = null` 表示「调用方已完成授权且不再额外下推」，该分支**不可由费用单路由到达**。

## 2. 权威客户归属

- **费用单**：只按持久化 `FinanceExpense.CustomerId` 判定；**绝不**按 `RefNo` / `CustomerName` 等自由文本推断。
  - 受限账号：`CustomerId` 为空 / 非正（无主行）或不在范围内 → 拒绝（fail closed，不泄露存在性）。
  - 特权账号：保留历史访问。
- **分摊批次**：权威归属由三个**显式持久化引用**共同证明，任一越界即整条拒绝：
  1. `LoadingListId` → 装柜清单的每一个有效参与方客户与显式上游（预装柜单 → 订柜信息）客户（复用 ERP-364 口径）；
  2. `SourceExpenseId` → 来源费用的归属客户（若已登记）；
  3. `FinanceExpenseAllocationLines` 的**逐行生成客户**（分摊行是唯一权威登记册）。
- **列表 / 台账 / 工作台**：范围在 **`Count` 与分页之前**下推到 SQL（`ExpenseScopeFilter` / `BatchScopeFilter`），绝不「先查全量再内存过滤」。

## 3. 路由覆盖

| 模块 | 路由 | 授权 + 范围 |
| --- | --- | --- |
| 费用单 | `GET /api/finance/expenses` | 动作内实时授权 + 列表范围下推 + 读取侧留痕标注 |
| 费用单 | `GET /api/finance/expenses/all` | 动作内实时授权 + 范围下推（下拉用） |
| 费用单 | `GET /api/finance/expenses/{id}` | 动作内实时授权 + 已存储 `CustomerId` 范围复核 |
| 费用单 | `POST /api/finance/expenses` | 动作内实时授权 + 拟议客户范围 / 启用客户 / 币种 / 金额 / 汇率校验；忽略客户端批次留痕 |
| 费用单 | `PUT /api/finance/expenses/{id}` | 动作内实时授权 + 已存储范围 + 拟议范围与数据校验 + 保留批次留痕（审计不可被客户端改写） |
| 费用单 | `DELETE /api/finance/expenses/{id}` | 动作内实时授权 + 已存储范围复核后软删除 |
| 费用单 | `POST /api/finance/expenses/batch-delete` | 动作内实时授权 + 逐行范围复核（任一行越界即整体拒绝） |
| 传统分摊 | `POST .../allocate-preview` | 动作内实时授权 + 明细客户范围复核（只读） |
| 传统分摊 | `POST .../allocate-apply` | 动作内实时授权 + 明细客户范围 / 启用客户 / 币种 / 金额 / 汇率校验后才检查重复、生成单号与写入（锁内再次实时复核） |
| ERP-042 | `GET .../allocation-context` | 动作内实时授权 + 装柜清单（参与方 / 上游客户）范围；受限账号不展示范围外归属的来源费用与批次 |
| ERP-042 | `POST .../allocation-preview` | 动作内实时授权 + 装柜清单范围 + 来源费用归属范围（只读计算） |
| ERP-042 | `POST .../allocation-generate` | 动作内实时授权 + 同上，全部校验通过后才写入批次 / 逐行留痕 / 费用单行 |
| ERP-042 | `GET .../allocation-batches` | 动作内实时授权 + 批次范围下推（计数 / 分页之前） |
| ERP-042 | `GET .../allocation-batches/{batchId}` | 动作内实时授权 + 批次完整范围复核 |
| ERP-042 | `POST .../allocation-batches/{batchId}/void` | 动作内实时授权 + 批次完整范围复核后才改状态（拒绝时状态 / 审计 / 留痕不变） |
| ERP-060 | `GET /api/container/expense-allocation-evidence` | 动作内实时授权 + 批次范围下推 |
| ERP-060 | `GET .../loading-lists/{loadingListId}` | 动作内实时授权 + 装柜清单范围 + 每一个被分摊客户范围 |
| ERP-060 | `GET .../settlements/{settlementId}` | 动作内实时授权 + 结算客户范围 + 关联清单参与方 / 上游 / 被分摊客户范围 |

## 4. 写入校验（改写任何字段或生成任何行之前）

1. **客户范围**：受限账号的拟议 / 已存储客户必须落在权威范围内；受限账号不得提交无主（`CustomerId` 为空）费用单。
2. **真实启用客户**：`BaseCustomer` 必须存在、未删除且 `Status` 为启用（停用 / 删除一律拒绝）。
3. **受支持币种**：只允许 `CNY / USD / EUR / HKD / GBP / JPY`（未知币种一律拒绝，不做汇率换算）。
4. **金额**：按既有存储精度（`decimal(18,2)`，2 位小数、0.5 进位）取整后必须大于 0。
5. **汇率**：按既有存储精度取整后必须大于 0。
6. **批次审计不可变**：批次生成的费用单留痕列（`AllocationBatchNo` / `AllocationSourceExpenseId` / `AllocationSourceExpenseNo`）只能由分摊批次服务端写入；新增忽略客户端同名值，编辑从已存储行恢复。
7. **显式作废语义**：分摊更正仍走 ERP-042 的显式作废（`Status = 0` + 作废原因 + 时间），保留批次、逐行留痕与已生成费用单行；被拒绝的作废不改写状态与审计。

## 5. 边界

- 不新增菜单 / 角色 / 用户授权，不新增表 / 列 / 索引，不把空身份当作管理员，不伪造任何授权。
- 不写库存 / 库存流水 / 资金 / 会计凭证，不产生收款、付款、核销、分摊入账或对账结论。
- 被拒绝的编辑 / 删除 / 预览 / 生成 / 作废不改写原费用行、批次状态、审计时间戳与任何关联证据。
- 不改写装柜清单 / 明细 / 参与方 / 订柜跟踪值 / 单证，也不改写来源销售出库单等库存来源单据的审计（ERP-364 / ERP-384 / ERP-060 既有护栏全部保留）。
- 分摊批次的并发串行化由 ERP-386（`ExpenseAllocationConcurrencyRules`）负责；本任务只保证授权 / 范围 / 校验与既有唯一索引兜底。

## 6. 测试证据

| 层 | 文件 | 覆盖 |
| --- | --- | --- |
| 单元（内存库） | `src/ERP.UnitTests/ExpenseAuthorizationTests.cs` | 币种 / 金额 / 汇率规则；身份 / 账号状态 / 菜单 / 未映射业务员 fail closed；**ERP-496 逐路由矩阵（15 条路由 × 缺失 / 为零 / 未知 / 已删除 / 禁用 / 缺少或撤销菜单 / 空路径 / 已赋值路径 / 未绑定 HttpContext）**；受限身份本人范围可读写与共享柜 / 越界拒绝且零改写；费用列表 / 详情 / 新增 / 修改 / 删除 / 批量删除的范围与数据校验；传统分摊越界与非法取值；批次预览 / 生成 / 台账 / 详情 / 作废的共享柜范围与「拒绝不改写」；分摊证据范围下推与结算客户范围；边界与接线契约（过滤器 / 范围属性 / null 旁路已移除） |
| 单元（内存库） | `src/ERP.UnitTests/ExpenseAllocationConcurrencyTests.cs` | 真实启用身份（受限 / 特权，均持有既有 `expense-bill` 菜单）驱动的传统分摊、来源费用改动与批次路由；锁序 / 单号键 / 原子回滚 |
| 单元（内存库） | `src/ERP.UnitTests/ContainerExpenseAllocationTests.cs` | 真实启用特权身份夹具（既有菜单授权）驱动的批次预览 / 生成 / 作废 / 台账 / 上下文全量回归 |
| SQL Server 集成（受控 localdb） | `src/ERP.IntegrationTests/ExpenseAuthorizationSqlServerTests.cs` | 真实控制器 / 服务下的本人 / 他人 / 混源 / 无主 / 撤销授权 / 禁用 / 删除账号；被拒绝的编辑 / 删除 / 批量删除 / 作废不改写；两个独立连接竞态（同一来源并发生成只落一条有效批次；范围外修改 vs 删除并发均 fail closed）；专用目标护栏 fail-closed |
| SQL Server 集成（受控 localdb） | `src/ERP.IntegrationTests/ExpenseAllocationConcurrencySqlServerTests.cs` | 真实控制器动作下的实时身份（授予 / 撤销菜单 / 禁用 / 删除 / 为零 / 未知 / 匿名 / 未绑定请求上下文）+ **全部 15 条路由请求形状无关性**与拒绝零改写 |
| 安全 profile | `.ai/config.json` 的 `safe` | `dotnet restore` → Release 构建（`TreatWarningsAsErrors`）→ `ERP.UnitTests` 全量 |

### 6.1 SQL Server 集成目标护栏

- 实例必须精确为 `(localdb)\NEWERP_AutoAcceptance`，库名前缀必须为 `NEWERP_AUTOTEST`，且必须为 `Integrated Security`；不满足在任何库访问之前直接拒绝。
- 每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings*.json` / `.env` / 生产凭据。

> 构建完成不等于阶段验收：只有受控 localdb 上真实执行通过、或给出准确 blocker，才构成阶段验收证据。真实浏览器验收按任务配置为 `not_required`（未执行，也不声明通过）。
