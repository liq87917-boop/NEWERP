# 单证中心实时授权与权威客户范围护栏说明（ERP-394 / ERP-497）

- 任务：`ERP-394`「Enforce live trade document permissions and customer scope at every business entry」
- 继续任务：`ERP-497`「Enforce trade document authority independently of the MVC request pipeline」
- 权威实现：
  - `src/ERP.Application/Services/TradeDocumentAuthorizationRules.cs`（纯规则 + 实时授权 + 权威客户范围 + 写入校验）
  - `src/ERP.Api/Controllers/TradeDocumentController.cs`（完整 CRUD 覆盖 + **每个路由动作内无条件实时授权**）
  - `src/ERP.Application/Services/TradeDocumentItemService.cs`（明细行读写先复核父单证归属）
  - `src/ERP.Api/Controllers/TradeDocumentGeneration.cs`（共用生成的来源 / 目标守卫）
  - `src/ERP.Api/Controllers/SalesOrderController.cs` / `ContainerLoadingListController.cs`（来源预填 / 直接生成入口动作内实时授权）

## 1. 授权口径

| 环节 | 口径 |
| --- | --- |
| 实时身份 | 每个路由在读取任何计数 / 生成单证编号 / 写入之前重新解析身份：缺失 / 非法按未认证拒绝；账号不存在或已删除按未认证拒绝；账号已禁用按权限不足拒绝 |
| 既有菜单 | 复用已部署的「单证中心」（`doc-center`）菜单：非特权账号必须显式具备，撤销后下一次请求立即收敛（无缓存） |
| 业务员映射 | 复用 ERP-097 `SalespersonDataScopeService` 唯一权威口径：受限账号按其登录名精确匹配员工编码；未映射业务员的受限账号 **fail closed**，绝不降级为全局 / 管理员可见 |
| 特权账号 | 超级管理员 / 系统内置角色 / 显式配置的特权角色保留既有全部访问（含历史无主行） |
| 身份来源 | 只来自已认证请求主体（`ClaimTypes.NameIdentifier`）；请求体**不包含也不接受**任何账号 / 角色 / 范围字段 |

**ERP-497（与请求管线 / 请求形状无关）**：单证中心台账（分页 / 全部 / 详情 / 新增 / 修改 / 删除 / 批量删除 / 打印 / 导出 / 明细行读取 / 新增 / 修改 / 删除）与四个生成入口路由**各自在动作内无条件**调用 `TradeDocumentAuthorizationRules.EnsureMenuAuthorizedAsync` 解析实时启用身份 / 账号状态 / 既有「单证中心」菜单与 ERP-097 权威客户范围，并把返回的**非空**范围下推到 SQL 与实体级复核。

- **已移除**：原 HTTP 入口动作过滤器 `TradeDocumentRequestAuthorizationFilter` 在全部路由上的**应用**、它的 `ScopeFrom` 方法、`HttpContext.Items` 范围通道，以及「未经过 MVC 管线（进程内调用）范围只能为 `null` = 不受限」的旁路；`TradeDocumentAuthorizationRules.ScopeFilter` / `ApplyScope` 不再接受 `null` 范围（原先对 `null` 返回匹配全部谓词的分支已删除，`ArgumentNullException` fail closed）。
- **因此**：空路径 / 已赋值 `Request.Path` / 完全未绑定 `HttpContext` 的纯进程内直调，对同一身份给出完全相同的判定；缺失 / 为零 / 未知 / 已删除身份一律未认证，禁用账号 / 撤销或缺少既有菜单一律权限不足；绝无匿名 / 管理员 / 环境变量 / 数据库提供程序 / 测试专用放行。
- **遗留常量**：`TradeDocumentRequestAuthorizationFilter` 现仅为**惰性常量占位**（不含任何过滤行为），供不在本任务改动范围内的一行历史 SQL 集成测试文件（`TradeDocumentMutationSqlServerTests`，其夹具仍需按 ERP-497 口径改用真实身份）保持可编译；该常量**没有任何生产读取方**，不构成授权输入或旁路。


## 2. 权威客户归属

- **单证**：只按持久化 `TradeDocument.CustomerId` 判定；**绝不**按 `SalesOrderNo` / `RefNo` / `DeclareNo` / `CustomerName` 等自由文本推断。
  - 受限账号：`CustomerId` 为空 / 非正（无主行）或不在范围内 → 拒绝（fail closed，不泄露存在性）。
  - 特权账号：保留历史访问。
- **来源单据（生成入口）**：销售订单按 `SalesOrder.CustomerId`、装柜清单按 `ContainerLoadingList.CustomerId` 判定；受限账号的来源缺失权威归属或越界一律拒绝，且**生成目标草稿的 `CustomerId` 必须归属到该权威客户**（共用 `TradeDocumentGeneration.EnsureGeneratedTargetAuthorized` 守卫，无法绕过）。
- **列表 / 计数 / 导出 / 明细 / 打印**：范围在 **`Count` 与分页之前**下推 SQL（`ScopeFilter` / `ApplyScope`），导出与明细行导出同口径，绝不「先查全量再内存过滤」，也绝不泄露范围外单证的计数、明细或打印内容。

## 3. 路由覆盖

下表每一行的「动作内实时授权门」均指：该动作在读取任何计数 / 单证行 / 明细快照 / 工作簿字节或写入之前，**无条件**调用 `TradeDocumentAuthorizationRules.EnsureMenuAuthorizedAsync`（ERP-497）。

| 路由 | 授权 + 范围 |
| --- | --- |
| `GET /api/trade/documents` | 动作内实时授权门 + 列表范围下推（计数 / 分页之前） |
| `GET /api/trade/documents/all` | 动作内实时授权门 + 范围下推（下拉用） |
| `GET /api/trade/documents/{id}` | 动作内实时授权门 + 已存储 `CustomerId` 范围复核 |
| `POST /api/trade/documents` | 动作内实时授权门 + 拟议客户实时范围 + 真实启用客户校验 |
| `PUT /api/trade/documents/{id}` | 动作内实时授权门 + 已存储范围 + 拟议范围与真实性校验 |
| `DELETE /api/trade/documents/{id}` | 动作内实时授权门 + 已存储范围复核后软删除 |
| `POST /api/trade/documents/batch-delete` | 动作内实时授权门 + 逐行范围复核（任一行越界 / 无主即整体拒绝、无部分删除） |
| `GET /api/trade/documents/{id}/print` | 动作内实时授权门 + 已存储范围复核（含明细快照） |
| `GET /api/trade/documents/export-excel` | 动作内实时授权门 + 范围下推（表头与 `layout=lines` 明细行同口径） |
| `GET /api/trade/documents/{id}/items` | 动作内实时授权门 + 父单证归属复核 |
| `POST /api/trade/documents/{id}/items` | 动作内实时授权门 + 父单证归属复核后写入行快照 |
| `PUT /api/trade/documents/items/{itemId}` | 动作内实时授权门 + 父单证归属复核后修改 |
| `DELETE /api/trade/documents/items/{itemId}` | 动作内实时授权门 + 父单证归属复核后软删除 |
| `GET /api/sales-orders/{id}/trade-documents/prefill` | 动作内实时授权门（doc-center 菜单 + 非空权威范围）+ 来源权威客户范围复核 |
| `POST /api/sales-orders/{id}/trade-documents` | 动作内实时授权门 + 来源范围复核 + 目标归属守卫 |
| `GET /api/container/loading-lists/{id}/trade-documents/prefill` | 动作内实时授权门 + 来源权威客户范围复核 |
| `POST /api/container/loading-lists/{id}/trade-documents` | 动作内实时授权门 + 来源范围复核 + 目标归属守卫 |

## 4. 边界

- 不新增菜单 / 角色 / 用户授权，不新增表 / 列 / 索引，不把空身份当作管理员，不伪造任何授权。
- 不改写单证身份 / 单证编号 / 状态 / 明细行快照 / 审计字段；保留既有明细行服务端计算、状态冻结（已提交客户 / 已使用不可删改）与库存来源单据审计。
- 不改写销售订单、装柜清单、库存与库存流水、发票、退税、费用或财务数据，不删除历史证据。
- 被拒绝的读取 / 打印 / 导出 / 新增 / 修改 / 删除 / 批量删除 / 生成不改写任何原行、明细行与审计。

## 5. 并发与原子性

- **批量删除**：先按 Id 取出全部未删除单证的权威归属投影（`LoadOwnershipAsync`），逐行复核范围；任一行越界 / 无主即整体拒绝，未做任何部分删除；全部通过后才调用一次软删除写入。
- **修改**：先复核「已存储」单证的权威归属，再校验「拟议」客户；失败时 `Service.UpdateAsync` 永不执行，原行不变。
- **竞态**（真实 SQL 集成测试）：两独立连接并发伪造新增越界客户单证二者都拒绝、零落库；同一单证上「改写到越界客户」与「合法删除」并发时改写始终拒绝、删除生效，归属从未被改写（无撕裂状态）。

## 6. 测试证据

| 层 | 文件 | 覆盖 |
| --- | --- | --- |
| 单元（内存库） | `src/ERP.UnitTests/TradeDocumentAuthorizationTests.cs` | 规则层身份 / 账号状态 / 菜单 / 未映射业务员 fail closed 与撤销收敛；**路由层身份形状**（缺失 / 为零 / 未知 / 已删除 → 未认证，禁用 / 撤销菜单 → 权限不足，未绑定 `HttpContext` 与空 / 已赋值 `Request.Path` 口径一致）；范围过滤下推（无主与越界不泄露，且 `ScopeFilter(null)` 已 fail closed）；列表 / 详情 / 打印 / 导出 / 明细的范围复核与不泄露；新增 / 修改 / 删除 / 批量删除的范围与真实性校验（混合批次整体拒绝、无部分删除）；四个生成入口的来源范围、目标归属守卫、缺失身份零落库；完整覆盖与「不再应用请求管线过滤器 + 每动作无条件解析」接线契约 |
| 单元（回归补充） | `TradeDocumentItemTests.cs` / `TradeDocumentPrintTests.cs` / `TradeDocumentGenerationTests.cs` / `TradeDocumentMutationTests.cs` / `TradeDocumentLineSnapshotTests.cs` / `TradeDocumentGenerationMutationTests.cs` | 夹具全部改用**真实启用身份**（特权账号用既有系统内置角色；受限覆盖用真实业务员账号 + 既有 `doc-center` 菜单授权），保留全部既有明细行 / 打印 / 生成 / 变更 / 快照业务断言 |
| SQL Server 集成（受控 localdb） | `src/ERP.IntegrationTests/TradeDocumentAuthorizationSqlServerTests.cs` / `TradeDocumentGenerationMutationSqlServerTests.cs` | 真实身份（账号 Id 直接来自隔离库）/ 菜单（撤销 / 禁用）；真实控制器下的本人 / 他人 / 无主；自定义路由（详情 / 打印 / 导出 / 明细）；伪造新增 / 修改；批量删除整体拒绝；来源生成越界拒绝 / 本人成功且目标归属权威客户；**两个独立连接竞态**；专用目标护栏 fail-closed；夹具不再经 `HttpContext.Items` 注入范围 |
| 安全 profile | `.ai/config.json` 的 `safe` | `dotnet restore` → Release 构建（`TreatWarningsAsErrors` + 分析器）→ `ERP.UnitTests` 全量 |

### 6.1 SQL Server 集成目标护栏

- 实例必须精确为 `(localdb)\NEWERP_AutoAcceptance`，库名前缀必须为 `NEWERP_AUTOTEST`，且必须为 `Integrated Security`；不满足在任何库访问之前直接拒绝（`AssertDedicatedTarget`）。
- 每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings*.json` / `.env` / 生产凭据。
- 本次实测（ERP-497）：`TradeDocumentAuthorizationSqlServerTests` 8/8、`TradeDocumentGenerationMutationSqlServerTests` 7/7 在专用 localdb 上真实通过（各创建全新 GUID 库，未 drop / reset）。

### 6.2 已知的跨文件后续项（不在本任务改动范围内）

- `src/ERP.IntegrationTests/TradeDocumentMutationSqlServerTests.cs` 的夹具仍向 `HttpContext.Items` 写入已废弃的惰性常量（无任何读取方），且其 `NewController(db, scope)` 未绑定真实身份 —— 该文件不在 `ERP-497` 的 `allowed_paths` 内，无法在本任务内改写；其 ERP-497 口径的最小改写为：`NewController(db, userId)` 绑定真实身份并删除 Items 注入，用已播种的真实业务员账号取代 `Restricted(...)` 范围。其单元对应件 `TradeDocumentMutationTests.cs` 已按本任务适配并全绿。
- 该差异不改变单证中心与四个生成入口的授权口径：任何真实 HTTP 请求与任何进程内直调都必须先解析实时身份 / 账号状态 / 既有菜单 / 权威范围。

> 构建完成不等于阶段验收：只有受控 localdb 上真实执行通过、或给出准确 blocker，才构成阶段验收证据。真实浏览器验收按任务配置为 `not_required`（未执行，也不声明通过）。

