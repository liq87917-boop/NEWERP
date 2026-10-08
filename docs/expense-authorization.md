# 费用单与装柜费用分摊授权护栏说明（ERP-385）

- 任务：`ERP-385`「Enforce live expense ownership across CRUD and allocation evidence routes」
- 权威实现：
  - `src/ERP.Application/Services/ExpenseAuthorizationRules.cs`（纯规则 + 实时授权 + 权威客户范围 + 写入校验）
  - `src/ERP.Api/Controllers/BaseCrudController.cs`（`ExpenseRequestAuthorizationFilter` HTTP 入口强制门）
  - `src/ERP.Api/Controllers/ExpenseBillController.cs`（费用 CRUD / 传统分摊 / ERP-042 分摊批次路由）
  - `src/ERP.Api/Controllers/ContainerExpenseAllocationEvidenceController.cs`（ERP-060 分摊证据读取）
  - `src/ERP.Application/Services/ContainerExpenseAllocationService.cs`（批次预览 / 生成 / 台账 / 详情 / 作废）
  - `src/ERP.Application/Services/ContainerExpenseAllocationEvidenceService.cs`（分摊证据只读派生）

## 1. 授权口径

| 环节 | 口径 |
| --- | --- |
| 实时身份 | 每个路由在读取任何计数 / 参与方 / 生成单号 / 写入之前重新解析身份：缺失 / 非法按未认证拒绝；账号不存在或已删除按未认证拒绝；账号已禁用按权限不足拒绝 |
| 既有菜单 | 复用已部署的「费用单」（`expense-bill`）菜单：非特权账号必须显式具备，撤销后下一次请求立即收敛（无缓存） |
| 业务员映射 | 复用 ERP-097 `SalespersonDataScopeService` 唯一权威口径：受限账号按其登录名精确匹配员工编码；未映射业务员的受限账号 **fail closed**，绝不降级为全局 / 管理员可见 |
| 特权账号 | 超级管理员 / 系统内置角色 / 显式配置的特权角色保留既有全部访问（含历史无主行与共享柜） |
| 身份来源 | 只来自已认证请求主体（`ClaimTypes.NameIdentifier`）；请求体（`ContainerExpenseAllocationRequest` 等）**不包含也不接受**任何账号 / 角色 / 范围字段 |

HTTP 入口由 `[ExpenseRequestAuthorizationFilter]`（`IAsyncActionFilter`）统一执行身份 / 账号状态 / 菜单授权，并把本次请求的权威范围写入 `HttpContext.Items`，供动作方法做数据库侧范围下推与实体级复核；解析失败即 fail closed（不执行任何计数 / 单号生成 / 写入）。进程内直接调用动作方法（单元测试 / 内部派生读取）不经过过滤器，其范围只能为 `null`（= 既有内部口径），**绝不代表匿名或管理员**。

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
| 费用单 | `GET /api/finance/expenses` | 过滤器的身份 / 菜单门 + 列表范围下推 + 读取侧留痕标注 |
| 费用单 | `GET /api/finance/expenses/all` | 过滤器 + 范围下推（下拉用） |
| 费用单 | `GET /api/finance/expenses/{id}` | 过滤器 + 已存储 `CustomerId` 范围复核 |
| 费用单 | `POST /api/finance/expenses` | 过滤器 + 拟议客户范围 / 启用客户 / 币种 / 金额 / 汇率校验；忽略客户端批次留痕 |
| 费用单 | `PUT /api/finance/expenses/{id}` | 过滤器 + 已存储范围 + 拟议范围与数据校验 + 保留批次留痕（审计不可被客户端改写） |
| 费用单 | `DELETE /api/finance/expenses/{id}` | 过滤器 + 已存储范围复核后软删除 |
| 费用单 | `POST /api/finance/expenses/batch-delete` | 过滤器 + 逐行范围复核（任一行越界即整体拒绝） |
| 传统分摊 | `POST .../allocate-preview` | 过滤器 + 明细客户范围复核（只读） |
| 传统分摊 | `POST .../allocate-apply` | 过滤器 + 明细客户范围 / 启用客户 / 币种 / 金额 / 汇率校验后才检查重复、生成单号与写入 |
| ERP-042 | `GET .../allocation-context` | 过滤器 + 装柜清单（参与方 / 上游客户）范围；受限账号不展示范围外归属的来源费用与批次 |
| ERP-042 | `POST .../allocation-preview` | 过滤器 + 装柜清单范围 + 来源费用归属范围（只读计算） |
| ERP-042 | `POST .../allocation-generate` | 过滤器 + 同上，全部校验通过后才写入批次 / 逐行留痕 / 费用单行 |
| ERP-042 | `GET .../allocation-batches` | 过滤器 + 批次范围下推（计数 / 分页之前） |
| ERP-042 | `GET .../allocation-batches/{batchId}` | 过滤器 + 批次完整范围复核 |
| ERP-042 | `POST .../allocation-batches/{batchId}/void` | 过滤器 + 批次完整范围复核后才改状态（拒绝时状态 / 审计 / 留痕不变） |
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
| 单元（内存库） | `src/ERP.UnitTests/ExpenseAuthorizationTests.cs` | 币种 / 金额 / 汇率规则；身份 / 账号状态 / 菜单 / 未映射业务员 fail closed；HTTP 入口过滤器拒绝与范围注入；费用列表 / 详情 / 新增 / 修改 / 删除 / 批量删除的范围与数据校验；传统分摊越界与非法取值；批次预览 / 生成 / 台账 / 详情 / 作废的共享柜范围与「拒绝不改写」；分摊证据范围下推与结算客户范围；边界与接线契约 |
| SQL Server 集成（受控 localdb） | `src/ERP.IntegrationTests/ExpenseAuthorizationSqlServerTests.cs` | 真实控制器 / 服务下的本人 / 他人 / 混源 / 无主 / 撤销授权 / 禁用 / 删除账号；被拒绝的编辑 / 删除 / 批量删除 / 作废不改写；两个独立连接竞态（同一来源并发生成只落一条有效批次；范围外修改 vs 删除并发均 fail closed）；专用目标护栏 fail-closed |
| 安全 profile | `.ai/config.json` 的 `safe` | `dotnet restore` → Release 构建（`TreatWarningsAsErrors`）→ `ERP.UnitTests` 全量 |

### 6.1 SQL Server 集成目标护栏

- 实例必须精确为 `(localdb)\NEWERP_AutoAcceptance`，库名前缀必须为 `NEWERP_AUTOTEST`，且必须为 `Integrated Security`；不满足在任何库访问之前直接拒绝。
- 每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings*.json` / `.env` / 生产凭据。

> 构建完成不等于阶段验收：只有受控 localdb 上真实执行通过、或给出准确 blocker，才构成阶段验收证据。真实浏览器验收按任务配置为 `not_required`（未执行，也不声明通过）。
