# 报价单 / 形式发票 PI 普通表单保存的来源血缘护栏说明（ERP-403）

- 任务：`ERP-403`「Validate inquiry and quotation lineage in ordinary quotation and PI saves」
- 权威实现：
  - `src/ERP.Application/Services/SalesDocumentSourceLineageRules.cs`（权威来源解析 + 实时身份 / 既有来源 + 目标菜单 / 客户范围 + 既有转换资格 + 唯一目标 + 确定性来源行锁 + 原子事务 + 历史来源冻结）
  - `src/ERP.Api/Controllers/QuotationController.cs`（`POST /api/sales/quotations` 新增、`PUT /{id}` 修改、`POST /{id}/submit`、`POST /{id}/approve` 全部落在同一血缘协议内；来源 = 询价单）
  - `src/ERP.Api/Controllers/ProformaInvoiceController.cs`（`POST /api/sales/proforma-invoices` 新增、`PUT /{id}` 修改、`POST /{id}/submit`、`POST /{id}/approve`；来源 = 报价单，且修改**沿用持久化链接**）
  - `src/ERP.Api/Controllers/InquiryQuotationConversion.cs`（`SourceLineageLockOrderText`：直接转换与普通保存共用同一把询价单来源行锁的锁序口径）
  - `src/ERP.Application/Services/InquiryMutationRules.cs` / `QuotationMutationRules.cs` / `ProformaInvoiceMutationRules.cs`（`EnsureQuotationConversionEligible` / `EnsureProformaInvoiceConversionEligible` / 来源行锁：普通保存与直接转换**同口径同锁**）
  - `src/ERP.Application/Services/QuotationAuthorizationRules.cs` / `ProformaInvoiceAuthorizationRules.cs`（`EnsureInquiryLineageAuthorizedAsync` / `EnsureQuotationLineageAuthorizedAsync`：复用实时身份阶梯 + 既有来源 + 目标双菜单授权）
- 继承关系：ERP-402 / ERP-400 / ERP-399 保护的是**专用转换入口**（带入预填 / 直接生成）；ERP-401 把同一套口径推给**销售订单**普通保存，ERP-403 把同口径推给**报价单**（来源询价单）与**形式发票 PI**（来源报价单）的普通保存，并把「预填后手工保存」与「直接转换」串行化在同一把来源行锁上。

## 1. 问题与修复口径

| 项 | 修复前 | 修复后 |
| --- | --- | --- |
| 报价单新增（`POST /api/sales/quotations`） | 先按字轨生成单号，再**原样照抄**调用方提交的 `InquiryId/No` | 先**权威解析**来源询价单（存在且未删除、客户一致、可解析 Id）、在来源行锁内复核授权 / 资格 / 唯一目标，**之后**才预约单号与写入 |
| 报价单修改（`PUT /{id}`） | 直接把请求体的来源 Id 与来源号覆盖到已存实体（可静默清空或改绑伪造来源） | 未给出 Id → **保留**历史来源（绝不静默清除）；显式改绑 → 完整实时复核；来源号一律按来源行**规范化** |
| 报价单提交 / 审核 | 仅状态流转 | 已登记来源的报价单先做**持久化来源重查**（来源仍可解析），再在报价单行锁 + 原子事务内流转 |
| PI 新增（`POST /api/sales/proforma-invoices`） | 原样照抄调用方提交的 `QuotationId/No` | 同报价单：权威解析来源报价单 + 来源行锁 + 实时授权 / 资格 / 唯一目标（先于单号与写入） |
| PI 修改（`PUT /{id}`） | 源头已保留持久化 `QuotationId`，但未实时复核 | **沿用持久化链接**（调用方提交的来源字段不构成链接），并在改写前复核来源仍可解析（已删除 fail closed） |
| PI 提交 / 审核 | 仅状态流转 | 已登记来源的 PI 先做**持久化来源重查**（来源仍可解析、同一报价单无其它 PI），再流转 |
| 来源单号 | 视为权威 | 只是**非权威文本**：绝不因「看起来像询价单号 / 报价单号」而建立链接；无 Id 时按文本保留，有 Id 时按来源行规范化 |

## 2. 权威解析（`ResolveAsync`，按种类）

| 情形 | 口径 |
| --- | --- |
| 报价单来源 = 询价单：显式 `InquiryId` 存在且未删除 | 以询价单为权威来源：规范化 `Quotation.InquiryNo`（来源行号 + 去空白 + 列宽截断） |
| PI 来源 = 报价单：显式 `QuotationId` 存在且未删除 | 以报价单为权威来源：规范化 `ProformaInvoice.QuotationNo`；报价单本身还可作为后续「报价单 → PI / 销售订单」追溯的祖先 |
| 来源客户 | 必须存在且等于目标单据客户；跨客户链接 / 缺失归属一律拒绝（`ForeignCustomerText`），绝不按文本推断归属 |
| 来源已删除（行存在但 `IsDeleted`） | 无效来源，原子拒绝（`SourceDeletedText`）—— 已删除来源绝不被当作历史值沿用 |
| 显式 Id **无法解析**（不存在） | 按「显式历史值」原样保留（`IsUnresolvedLegacy`）：不构成实时链接（不参与资格 / 授权 / 唯一目标 / 加锁），也绝不静默改写或清除调用方提交的 Id 与单号文本 |
| 未携带任何来源 Id | 显式未链接（手工单据完全有效）；未链接时不取任何来源锁、不开事务，单号文本按调用方提交保留 |

## 3. 授权与客户范围（写入之前）

| 项 | 口径 |
| --- | --- |
| 触发条件 | 仅当本次保存**携带可解析的显式来源**时执行；未链接手工单据保持既有 `[Authorize]` + 目标菜单口径不变 |
| 身份 | `ClaimTypes.NameIdentifier` 每次重新解析：缺失 / 非法 → `2000` 未认证；账号不存在或已删除 → `2000`；`Status != Enabled` → `2002` 权限不足 |
| 菜单 | 报价单：既有「报价单」（`quotation`）+「询价单」（`inquiry`）；PI：既有「形式发票 PI」（`proforma-invoice`）+「报价单」（`quotation`）。**非特权账号**必须两者显式具备**且**已映射业务员，否则 fail closed；特权账号豁免菜单 |
| 客户范围 | 目标单据客户与来源客户**都必须**落在本次请求的实时客户范围内（`SalespersonDataScopeService` ERP-097 唯一口径），越界 / 缺失归属 fail closed |
| 不新增授权 | 不新增表 / 列 / 菜单 / 角色 / 用户授权，不把空身份当作匿名或管理员，也不引入任何管理员回退 |

## 4. 锁协议与原子事务

| 项 | 口径 |
| --- | --- |
| 唯一来源锁 | 报价单来源 = 询价单行锁 `SELECT ... db_owner.Inquiries WITH (UPDLOCK, HOLDLOCK)`（与 ERP-402 转报价单同一把）；PI 来源 = 报价单行锁 `... db_owner.Quotations ...`（与 ERP-400 生命周期 / 版本 / 转换、ERP-399 转 PI 同一把） |
| 锁实现 | 仅用 EF Core 基础 API：在调用方事务内对来源行发一条「审计时间戳刷新」`UPDATE`（`UpdatedAt` 是技术审计字段、不是商业证据），取得排它 X 锁持有至事务结束；并发方先提交造成乐观令牌过期时重读权威行后**有界重试**（`RowLockRetryAttempts = 8`） |
| 确定性锁序 | 恒定「**来源行锁 → 目标单据行锁**」；绝不反向获取上游锁，因此不存在锁环；未链接手工单据不取任何锁 |
| 加锁时机 | 单号预约（`IDocumentNumberService.GenerateAsync`）与任何写入**之前**；被拒绝的调用方绝不消耗单据号 |
| 锁内重读 | 加来源行锁后**重新加载**权威来源（绝不复用加锁前的内存实体）并复核实时资格；报价单修改还会加报价单目标行锁并在锁内重读持久化状态 |
| 原子事务 | `BeginWriteTransactionAsync`：关系型后端开启真实事务（默认隔离级别），任一步失败整体回滚；调用方已开启事务时**绝不嵌套**；内存库等非关系型提供程序等价无事务（声明式校验不变） |
| 并发串行化 | 普通保存与直接转换（`to-quotation` / `to-pi`）在同一把来源行锁上串行化，因此同一来源至多一张有效目标单据（既有唯一目标规则） |

## 5. 状态机 / 版本 / 转换口径（保持不变）

| 项 | 口径 |
| --- | --- |
| 报价单可改 | 仅待提交；已审核 / 已转订单 / 已作废 / 已被后续版本取代的历史版本拒绝（ERP-035 / ERP-400 不变） |
| 报价单提交 / 审核 | 状态机与下游冻结口径不变；额外做持久化来源重查 |
| PI 可改 | 仅待提交；已审核 / 已转订单 / 已作废 / 存在下游销售订单链接拒绝（ERP-399 不变） |
| PI 修改 | 沿用持久化来源报价单链接（`QuotationId/No` 由锁内已存实体回写，调用方提交值不构成链接） |
| 唯一目标 | 报价单以 `InquiryId` 唯一化（排除本单所在**版本链**，版本链合法共享同一来源询价单）；PI 以 `QuotationId` 唯一化（排除本单） |
| 版本链 | 创建版本（`POST /{id}/revisions`）**不经过**本血缘入口；新版本携带来源 Id 属合法历史，提交 / 审核的持久化重查对报价单不重复做唯一目标（版本链与本单合法共享来源） |
| 商业口径 | 数量 / 单价 / 金额 / 合计 / 定金 / 币种 / 汇率 / 单位一律复用 `QuotationLineRules.Normalize` / `ProformaInvoiceController.Normalize` 唯一权威，本护栏绝不改写 |

## 6. 路由接线

| 路由 | 口径 |
| --- | --- |
| `POST /api/sales/quotations` | 显式来源询价单 → 权威解析 + 来源行锁 + 原子事务 + 实时授权 / 客户范围 + 既有转换资格 + 唯一目标（全部先于单号与写入）；未链接手工报价单保持既有口径 |
| `PUT /api/sales/quotations/{id}` | 加锁顺序「询价单来源行 → 报价单目标行」+ 锁内重读；未给出来源 Id 保留历史；显式改绑完整复核；来源号一律权威规范化 |
| `POST /api/sales/quotations/{id}/submit` · `/approve` | 已登记来源 → 报价单行锁 + 持久化来源重查（来源仍可解析）再流转；未链接报价单保持既有流转 |
| `POST /api/sales/proforma-invoices` | 显式来源报价单 → 权威解析 + 报价单来源行锁 + 原子事务 + 实时授权 / 客户范围 + 既有转换资格 + 唯一目标（先于单号与写入） |
| `PUT /api/sales/proforma-invoices/{id}` | 沿用持久化来源链接（调用方提交的来源字段不改绑）+ 持久化来源重查 + 既有 PI 行锁 / 上游冻结 |
| `POST /api/sales/proforma-invoices/{id}/submit` · `/approve` | 已登记来源 → PI 行锁 + 持久化来源重查（来源可解析、同一报价单无其它 PI）再流转 |
| 其他读取 / 取消 / 删除 / 版本 / 转换 | 不改写（取消 / 版本 / 转换仍走 ERP-402 / ERP-400 / ERP-399 既有可串行化事务与下游阻断） |

## 7. 边界

- 不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权；不把空身份当作匿名或管理员，也不伪造任何授权。
- 不改写数量 / 单价 / 金额 / 合计 / 定金 / 币种 / 汇率 / 单位：一律复用 `QuotationLineRules` / `ProformaInvoiceController.Normalize` 唯一权威口径（服务端重算）。
- 不改写来源询价单 / 报价单 / 客户主数据 / 库存与库存流水 / 财务记录，不做任何财务 / 库存过账，不删除任何历史证据。
- 保留库存来源单据审计与原始失败日志：被拒绝 / 回滚的请求只回滚自己的写入。
- 行锁只刷新来源行与目标行的技术审计时间戳 `UpdatedAt`（非商业证据，不参与任何金额 / 状态判定）。
- 失败一律 fail closed：解析失败、授权失败、资格不符、唯一目标冲突、写入失败都整体回滚，绝不留下半成品单据或已占号。

## 8. 测试证据

| 层 | 文件 | 覆盖 |
| --- | --- | --- |
| 单元（内存库，新增） | `src/ERP.UnitTests/SalesDocumentSourceLineageTests.cs` | 锁语句 / 锁序与 ERP-402 / ERP-400 同源；事务与行锁非关系型等价无操作；来源合并（未给 Id 保留历史 / 显式改绑 / 归一化）；权威解析（询价单 / 报价单来源规范化、已删除、异客户、完全无法解析的历史值、未携带来源）；唯一目标唯一化与版本链排除；报价单新增（权威来源号落库且不伪造来源状态、手工未链接有效、未审核拒绝、重复目标拒绝）；报价单修改（保留历史来源、显式改绑、改绑到无法解析来源）；报价单提交 / 审核（未链接既有口径、已链接重查通过、来源已删除冻结）；PI 新增（权威来源号、未链接有效、未审核拒绝、重复目标拒绝）；PI 修改（沿用持久化链接、调用方改绑被忽略）；PI 提交（来源已删除冻结）；实时授权（缺来源菜单 / 越范围 / 无身份 / 双菜单放行）；控制器源码契约 |
| 单元（既有回归，扩展） | `src/ERP.UnitTests/InquiryQuotationConversionTests.cs`（+2 例）、`QuotationToPiTests.cs`（+3 例）、`ProformaInvoiceControllerTests.cs`（+3 例） | 普通报价单保存与询价直接转换共用同一把来源行锁 / 同一唯一目标口径；带入预填草稿经服务端复核后可保存并留痕权威来源（来源状态不被改写）；普通 PI 保存与报价单直接转换共用同一把来源行锁 / 唯一目标口径；PI 修改沿用持久化来源；显式来源报价单未审核拒绝 |
| 单元（既有回归，未修改） | `InquiryMutationTests` / `QuotationMutationTests` / `QuotationRevisionTests` / `ProformaInvoiceMutationTests` / `ProformaInvoiceAuthorizationTests` / `SalesOrderSourceLineageTests` | 既有追溯字段、CRUD、状态流转、版本与转换口径保持通过 |
| SQL Server 集成（受控 localdb，新增） | `src/ERP.IntegrationTests/SalesDocumentSourceLineageSqlServerTests.cs` | 真实既有授权 fail closed（无身份 / 缺来源菜单 / 越范围，零写入）；伪造 / 异客户 / 已删除 / 未审核 / 重复目标原子拒绝且来源零改写（报价单 + PI）；权威来源号落库与未链接手工单据有效；拒绝整体回滚（来源 `UpdatedAt` / 状态）；**四条独立连接竞态**：普通报价单保存 vs 询价直接转换、普通 PI 保存 vs 报价单直接转换、普通报价单保存 vs 来源询价单取消、两张并发普通报价单保存 —— 结果唯一且历史一致；锁语句契约；专用目标护栏 fail-closed 4 例 |
| 安全 profile | `.ai/config.json` 的 `safe` | `dotnet restore` → Release 构建 → `ERP.UnitTests` 全量 |

### 8.1 SQL Server 集成目标护栏

- 实例必须精确为 `(localdb)\NEWERP_AutoAcceptance`，库名前缀必须为 `NEWERP_AUTOTEST`，且必须为 `Integrated Security`；
  不满足在任何库访问之前直接拒绝（`AssertDedicatedTarget`）。
- 每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings*.json` / `.env` / 生产凭据。

### 8.2 验证记录

| 项 | 命令 / 目标 | 结果 |
| --- | --- | --- |
| Release 构建 | `dotnet build NEWERP.sln -c Release --no-restore --no-incremental /p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true` | **0 警告 / 0 错误** |
| 全量单元测试 | `dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build` | **6182 通过 / 0 失败 / 0 跳过** |
| ERP-403 真实 SQL | `dotnet test src/ERP.IntegrationTests/ERP.IntegrationTests.csproj -c Release --no-build --filter "FullyQualifiedName~SalesDocumentSourceLineage"` | **14 通过 / 0 失败**（专用实例 `(localdb)\NEWERP_AutoAcceptance` + 全新 GUID 后缀 `NEWERP_AUTOTEST_*` 库；含 4 条双连接竞态、拒绝矩阵、权威规范化、回滚证据与专用目标护栏 4 例） |
| 浏览器验收 | 任务配置 `browser_acceptance.required = false` | 未执行，也不声明通过 |

#### 实际执行结果

- 目标：专用实例 `(localdb)\NEWERP_AutoAcceptance` + 全新 GUID 后缀 `NEWERP_AUTOTEST_*` 库（每次测试内 `Guard()` 复核实例名 / 库名前缀 / 集成安全）。
- 结果：**14 通过 / 0 失败 / 0 跳过**，其中：
  - 无身份 / 缺来源询价单菜单 / 越客户范围：1 例聚合断言 fail closed，零报价单写入；
  - 异客户 / 已删除 / 未审核 / 重复目标（报价单 + PI）：2 例聚合断言，零目标单据、来源状态与审计零改写；
  - 权威来源号规范化 + 未链接手工单据有效：2 例（报价单 + PI）；
  - **四条独立连接竞态**：普通报价单保存 vs 询价直接转换、普通 PI 保存 vs 报价单直接转换、普通报价单保存 vs 来源询价单取消、两张并发普通报价单保存 —— 每例恰好一方成功且同一来源至多一张目标单据 / 历史一致；
  - 锁语句与 ERP-402 / ERP-400 同源：1 例；专用目标护栏 fail-closed：4 例。
- 验证产物（日志 / TRX）一律写到工作区之外的临时目录；工作区变更集严格限于本任务 `allowed_paths`。

> 构建完成不等于阶段验收：只有受控 localdb 上真实执行通过、或给出准确 blocker，才构成阶段验收证据。

