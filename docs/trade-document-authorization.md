# 单证中心实时授权与权威客户范围护栏说明（ERP-394）

- 任务：`ERP-394`「Enforce live trade document permissions and customer scope at every business entry」
- 权威实现：
  - `src/ERP.Application/Services/TradeDocumentAuthorizationRules.cs`（纯规则 + 实时授权 + 权威客户范围 + 写入校验）
  - `src/ERP.Api/Controllers/TradeDocumentController.cs`（完整 CRUD 覆盖 + `TradeDocumentRequestAuthorizationFilter` HTTP 入口强制门）
  - `src/ERP.Application/Services/TradeDocumentItemService.cs`（明细行读写先复核父单证归属）
  - `src/ERP.Api/Controllers/TradeDocumentGeneration.cs`（共用生成的来源 / 目标守卫）
  - `src/ERP.Api/Controllers/SalesOrderController.cs` / `ContainerLoadingListController.cs`（来源预填 / 直接生成入口强制门）

## 1. 授权口径

| 环节 | 口径 |
| --- | --- |
| 实时身份 | 每个路由在读取任何计数 / 生成单证编号 / 写入之前重新解析身份：缺失 / 非法按未认证拒绝；账号不存在或已删除按未认证拒绝；账号已禁用按权限不足拒绝 |
| 既有菜单 | 复用已部署的「单证中心」（`doc-center`）菜单：非特权账号必须显式具备，撤销后下一次请求立即收敛（无缓存） |
| 业务员映射 | 复用 ERP-097 `SalespersonDataScopeService` 唯一权威口径：受限账号按其登录名精确匹配员工编码；未映射业务员的受限账号 **fail closed**，绝不降级为全局 / 管理员可见 |
| 特权账号 | 超级管理员 / 系统内置角色 / 显式配置的特权角色保留既有全部访问（含历史无主行） |
| 身份来源 | 只来自已认证请求主体（`ClaimTypes.NameIdentifier`）；请求体**不包含也不接受**任何账号 / 角色 / 范围字段 |

HTTP 入口由 `[TradeDocumentRequestAuthorizationFilter]`（`IAsyncActionFilter`）统一执行身份 / 账号状态 / 菜单授权，并把本次请求的权威范围写入 `HttpContext.Items`，供动作方法做数据库侧范围下推与实体级复核；解析失败即 fail closed（不执行任何计数 / 单号生成 / 写入）。进程内直接调用动作方法（单元测试 / 内部派生读取）不经过过滤器，其范围只能为 `null`（= 既有内部口径），**绝不代表匿名或管理员**。

## 2. 权威客户归属

- **单证**：只按持久化 `TradeDocument.CustomerId` 判定；**绝不**按 `SalesOrderNo` / `RefNo` / `DeclareNo` / `CustomerName` 等自由文本推断。
  - 受限账号：`CustomerId` 为空 / 非正（无主行）或不在范围内 → 拒绝（fail closed，不泄露存在性）。
  - 特权账号：保留历史访问。
- **来源单据（生成入口）**：销售订单按 `SalesOrder.CustomerId`、装柜清单按 `ContainerLoadingList.CustomerId` 判定；受限账号的来源缺失权威归属或越界一律拒绝，且**生成目标草稿的 `CustomerId` 必须归属到该权威客户**（共用 `TradeDocumentGeneration.EnsureGeneratedTargetAuthorized` 守卫，无法绕过）。
- **列表 / 计数 / 导出 / 明细 / 打印**：范围在 **`Count` 与分页之前**下推 SQL（`ScopeFilter` / `ApplyScope`），导出与明细行导出同口径，绝不「先查全量再内存过滤」，也绝不泄露范围外单证的计数、明细或打印内容。

## 3. 路由覆盖

| 路由 | 授权 + 范围 |
| --- | --- |
| `GET /api/trade/documents` | 过滤器身份 / 菜单门 + 列表范围下推（计数 / 分页之前） |
| `GET /api/trade/documents/all` | 过滤器 + 范围下推（下拉用） |
| `GET /api/trade/documents/{id}` | 过滤器 + 已存储 `CustomerId` 范围复核 |
| `POST /api/trade/documents` | 过滤器 + 拟议客户实时范围 + 真实启用客户校验 |
| `PUT /api/trade/documents/{id}` | 过滤器 + 已存储范围 + 拟议范围与真实性校验 |
| `DELETE /api/trade/documents/{id}` | 过滤器 + 已存储范围复核后软删除 |
| `POST /api/trade/documents/batch-delete` | 过滤器 + 逐行范围复核（任一行越界 / 无主即整体拒绝、无部分删除） |
| `GET /api/trade/documents/{id}/print` | 过滤器 + 已存储范围复核（含明细快照） |
| `GET /api/trade/documents/export-excel` | 过滤器 + 范围下推（表头与 `layout=lines` 明细行同口径） |
| `GET /api/trade/documents/{id}/items` | 过滤器 + 父单证归属复核 |
| `POST /api/trade/documents/{id}/items` | 过滤器 + 父单证归属复核后写入行快照 |
| `PUT /api/trade/documents/items/{itemId}` | 过滤器 + 父单证归属复核后修改 |
| `DELETE /api/trade/documents/items/{itemId}` | 过滤器 + 父单证归属复核后软删除 |
| `GET /api/sales-orders/{id}/trade-documents/prefill` | 过滤器（doc-center 菜单门）+ 来源权威客户范围复核 |
| `POST /api/sales-orders/{id}/trade-documents` | 过滤器 + 来源范围复核 + 目标归属守卫 |
| `GET /api/container/loading-lists/{id}/trade-documents/prefill` | 过滤器 + 来源权威客户范围复核 |
| `POST /api/container/loading-lists/{id}/trade-documents` | 过滤器 + 来源范围复核 + 目标归属守卫 |

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
| 单元（内存库） | `src/ERP.UnitTests/TradeDocumentAuthorizationTests.cs`（新增） | 身份 / 账号状态 / 菜单 / 未映射业务员 fail closed 与撤销收敛；范围过滤下推（无主与越界不泄露）；列表 / 详情 / 打印 / 导出 / 明细的范围复核与不泄露；新增 / 修改 / 删除 / 批量删除的范围与真实性校验（混合批次整体拒绝、无部分删除）；生成入口来源范围与目标守卫；完整覆盖与强制门接线契约 |
| 单元（回归补充） | `TradeDocumentItemTests.cs` / `TradeDocumentPrintTests.cs` / `TradeDocumentGenerationTests.cs` | 保留既有明细行 / 打印 / 生成回归，并新增受限范围下明细读写、打印、来源生成 fail closed 的聚焦覆盖 |
| SQL Server 集成（受控 localdb） | `src/ERP.IntegrationTests/TradeDocumentAuthorizationSqlServerTests.cs`（新增） | 真实身份 / 菜单（撤销 / 禁用）；真实控制器下的本人 / 他人 / 无主；自定义路由（详情 / 打印 / 导出 / 明细）；伪造新增 / 修改；批量删除整体拒绝；来源生成越界拒绝 / 本人成功且目标归属权威客户；**两个独立连接竞态**；专用目标护栏 fail-closed |
| 安全 profile | `.ai/config.json` 的 `safe` | `dotnet restore` → Release 构建（`TreatWarningsAsErrors`）→ `ERP.UnitTests` 全量 |

### 6.1 SQL Server 集成目标护栏

- 实例必须精确为 `(localdb)\NEWERP_AutoAcceptance`，库名前缀必须为 `NEWERP_AUTOTEST`，且必须为 `Integrated Security`；不满足在任何库访问之前直接拒绝（`AssertDedicatedTarget`）。
- 每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings*.json` / `.env` / 生产凭据。

> 构建完成不等于阶段验收：只有受控 localdb 上真实执行通过、或给出准确 blocker，才构成阶段验收证据。真实浏览器验收按任务配置为 `not_required`（未执行，也不声明通过）。

