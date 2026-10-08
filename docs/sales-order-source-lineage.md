# 销售订单普通表单保存的来源血缘护栏说明（ERP-401）

- 任务：`ERP-401`「Validate sales order source lineage during ordinary form save」
- 权威实现：
  - `src/ERP.Application/Services/SalesOrderSourceLineageRules.cs`（权威来源解析 + 实时身份 / 既有菜单 / 客户范围 + 既有转换资格 + 唯一目标 + 确定性来源行锁 + 原子事务 + 历史来源冻结）
  - `src/ERP.Api/Controllers/SalesOrderController.cs`（`POST /api/sales-orders` 新增、`PUT /{id}` 修改、`POST /{id}/submit`、`POST /{id}/approve` 全部落在同一血缘协议内）
  - `src/ERP.Api/Controllers/SalesOrderConversion.cs`（`FindOtherTargetAsync` 唯一目标复核与 `SourceLockOrderText` 锁序口径，与「带入预填 + 直接生成」同源）
  - `src/ERP.Application/Services/ProformaInvoiceMutationRules.cs`（`EnsureManualOrderLinkEligible` / `ManualLinkLockOrderText`：普通保存与 PI 转换同口径同锁）
  - `src/ERP.Application/Services/QuotationMutationRules.cs`（`EnsureManualOrderLinkEligible` / `ManualLinkLockOrderText`：普通保存与报价单转换同口径同锁）
  - `src/ERP.Application/Services/ProformaInvoiceAuthorizationRules.cs` / `QuotationAuthorizationRules.cs`（`EnsureSalesOrderLineageAuthorizedAsync`：复用实时身份阶梯 + 既有「销售订单」菜单授权）
- 继承关系：ERP-010 / ERP-398 / ERP-399 / ERP-400 保护的是**专用转换入口**（带入预填 / 直接生成）；
  ERP-401 把同一套权威来源、客户范围、转换资格与唯一目标口径推到**普通表单保存**（新增 / 修改 / 提交 / 审核），
  并把「预填后手工保存」与「直接转换」串行化在同一把来源行锁上。

## 1. 问题与修复口径

| 项 | 修复前 | 修复后 |
| --- | --- | --- |
| 新增（`POST /`） | 先按字轨生成单号，再**原样照抄**调用方提交的 `SourcePiId/No`、`SourceQuotationId/No` | 先**权威解析**来源（存在且未删除、客户一致、可解析 Id）、在来源行锁内复核授权 / 资格 / 唯一目标，**之后**才预约单号与写入 |
| 修改（`PUT /{id}`） | 直接把请求体的来源 Id 与来源号覆盖到已存实体（可静默清空或改绑伪造来源） | 未给出 Id → **保留**历史来源（绝不静默清除）；显式改绑 → 完整实时复核 + 下游冻结；来源号一律按来源行**规范化** |
| 提交 / 审核 | 仅状态流转 | 已登记来源的订单先做**持久化来源重查**（来源仍可解析、无其它目标），再在订单行锁 + 原子事务内流转 |
| 来源单号 | 视为权威 | 只是**非权威文本**：绝不因「看起来像 PI 号 / 报价单号」而建立链接；无 Id 时按文本保留，有 Id 时按来源行规范化 |

## 2. 权威解析（`ResolveAsync`）

| 情形 | 口径 |
| --- | --- |
| 显式 PI Id 且存在且未删除 | 以 PI 为权威来源：规范化 `SourcePiNo`（来源行号 + 去空白 + 列宽截断）；其报价单祖先（`ProformaInvoice.QuotationId/QuotationNo`）一并规范化，形成「报价单 → PI → 销售订单」完整链 |
| 显式报价单 Id 且未给 PI | 要求报价单**本身可精确解析**（存在且未删除），规范化 `SourceQuotationNo` |
| 同时给出 PI 与报价单 | 报价单必须等于 PI 记录的**权威报价单祖先**，否则冲突来源对原子拒绝（`ConflictingPairText`） |
| 来源客户 | 必须存在且等于目标订单客户；跨客户链接 / 缺失归属一律拒绝（`ForeignCustomerText`），绝不按文本推断归属 |
| 来源已删除（行存在但 `IsDeleted`） | 无效来源，原子拒绝（`SourceDeletedText`）—— 已删除来源绝不被当作历史值沿用 |
| 显式 Id **全部无法解析**（不存在） | 按「显式历史值」原样保留（`IsUnresolvedLegacy`）：不构成实时链接（不参与资格 / 授权 / 唯一目标 / 加锁），也绝不静默改写或清除调用方提交的 Id 与单号文本 —— 历史 / 导入值不被破坏 |
| 未携带任何来源 Id | 显式未链接（手工订单完全有效）；未链接时不取任何来源锁、不开事务，单号文本按调用方提交保留 |

## 3. 授权与客户范围（写入之前）

| 项 | 口径 |
| --- | --- |
| 触发条件 | 仅当本次保存**携带可解析的显式来源**时执行；未链接手工订单保持既有 `[Authorize]` 口径不变 |
| 身份 | `ClaimTypes.NameIdentifier` 每次重新解析：缺失 / 非法 → `2000` 未认证；账号不存在或已删除 → `2000`；`Status != Enabled` → `2002` 权限不足 |
| 菜单 | 复用既有「销售订单」菜单 `sales-order`；**非特权账号**必须显式具备**且**已映射业务员，否则 fail closed；特权账号（超级管理员 / 系统内置角色 / 显式特权角色，ERP-097 同源）豁免菜单 |
| 客户范围 | 目标订单客户与来源客户**都必须**落在本次请求的实时客户范围内（`SalespersonDataScopeService` 唯一口径），越界 / 缺失归属 fail closed |
| 不新增授权 | 不新增表 / 列 / 菜单 / 角色 / 用户授权，不把空身份当作匿名或管理员，也不引入任何管理员回退 |

## 4. 锁协议与原子事务

| 项 | 口径 |
| --- | --- |
| 唯一来源锁 | 报价单来源行锁 `SELECT Id FROM db_owner.Quotations WITH (UPDLOCK, HOLDLOCK)`、PI 来源行锁 `... db_owner.ProformaInvoices ...`、目标订单行锁 `... db_owner.SalesOrders ...`（分别与 ERP-400 / ERP-399 / ERP-395 共用同一常量） |
| 锁实现 | 仅用 EF Core 基础 API：在调用方事务内对来源行发一条「审计时间戳刷新」`UPDATE`（`UpdatedAt` 是技术审计字段、不是商业证据），取得排它 X 锁持有至事务结束；并发方先提交造成乐观令牌过期时重读权威行后**有界重试**（`RowLockRetryAttempts = 8`） |
| 确定性锁序 | 恒定「**报价单来源行锁 → PI 来源行锁 → 销售订单目标行锁**」；绝不反向获取下游锁，因此不存在锁环；未链接手工订单不取任何锁 |
| 加锁时机 | 单号预约（`IDocumentNumberService.GenerateAsync`）与任何写入**之前**；被拒绝的调用方绝不消耗单据号 |
| 锁内重读 | 加来源行锁后**重新加载**权威来源（绝不复用加锁前的内存实体）并复核实时资格；修改时还会加目标订单行锁并在锁内重读持久化订单状态 |
| 原子事务 | `BeginMutationTransactionAsync`：关系型后端开启真实事务（默认隔离级别），任一步失败整体回滚；调用方已开启事务时**绝不嵌套**；内存库等非关系型提供程序等价无事务（声明式校验不变） |
| 回滚证据 | 拒绝时来源行锁的 `UpdatedAt` 刷新、状态 / 字段 / 明细 / 已预约单号与新建订单一起回滚（集成测试断言 `UpdatedAt` 与状态零变化） |

## 5. 唯一目标与历史来源

- **唯一目标**：`SalesOrderConversion.FindOtherTargetAsync`（与转换守卫同一实现）按持久化来源字段唯一化 ——
  同一 PI 以 `SourcePiId` 唯一化；同一报价单以「`SourceQuotationId` 且未绑定 PI」唯一化
  （PI 来源订单会同时留痕报价单祖先，**不**因此误判为重复报价单目标）；修改时排除本单自身。
  只按显式 Id 判定，绝不按来源单号等自由文本推断。
- **既有转换资格**：普通保存显式链接来源时复用 `ProformaInvoiceMutationRules.EnsureManualOrderLinkEligible` /
  `QuotationMutationRules.EnsureManualOrderLinkEligible`（与「PI → 销售订单」「报价单 → 销售订单」直接转换**同一实现**）：
  已作废 / 未审核 / 无有效明细 / 已完成 / 已转 PI 一律拒绝；**不新增任何价格相等性要求**。
- **历史来源不被静默改写**：修改时请求未给出任何来源 Id → 继续沿用历史来源（`IsClearingAttempt`）；
  显式改绑（与历史不同）→ 完整实时复核 + 目标订单已存在未删除变更申请 / 销售出库时**冻结拒绝**（`RebindFrozenText`）；
  改绑到无法解析的来源 → 拒绝（`RebindUnknownSourceText`）。
- **不伪造来源状态**：普通保存不把来源 PI / 报价单置「已完成」，也不消耗单据号 ——
  来源状态只由既有直接转换工作流改写（保留显式历史，绝不做反向冲销）。

## 6. 路由守卫矩阵

| 路由 | 新增守卫（相对既有口径） |
| --- | --- |
| `POST /api/sales-orders` | 显式来源 → 权威解析 + 来源行锁 + 原子事务 + 实时授权 / 客户范围 + 既有转换资格 + 唯一目标（全部先于单号与写入）；未链接手工订单保持既有口径 |
| `PUT /{id}` | 加锁顺序「来源行 → 订单行」+ 锁内重读；未给出来源 Id 保留历史；显式改绑完整复核 + 下游冻结；来源号一律权威规范化 |
| `POST /{id}/submit` | 已登记来源 → 订单行锁 + 持久化来源重查（来源仍可解析、无其它目标）再流转；未链接订单保持既有流转 |
| `POST /{id}/approve` | 同 `submit`（`Submitted → Approved`） |
| 其他读取 / 取消 / 删除等 | 不改写（取消仍走 ERP-347 / ERP-369 既有可串行化事务与下游阻断） |

## 7. 边界

- 不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权；不把空身份当作匿名或管理员，也不伪造任何授权。
- 不改写金额 / 合计 / 定金 / 币种 / 汇率 / 单位 / 明细数量：一律复用 `SalesOrderAmountRules` 唯一权威口径（服务端重算）。
- 不改写来源报价单 / PI / 客户主数据 / 库存与库存流水 / 财务记录，不做任何财务 / 库存过账，不删除任何历史证据。
- 行锁只刷新来源行与目标订单行的技术审计时间戳 `UpdatedAt`（非商业证据，不参与任何金额 / 状态判定）。
- 保留库存来源单据审计与原始失败日志：被拒绝 / 回滚的请求只回滚自己的写入。
- 失败一律 fail closed：解析失败、授权失败、资格不符、唯一目标冲突、写入失败都整体回滚，绝不留下半成品订单或已占号。

## 8. 测试证据

| 层 | 文件 | 覆盖 |
| --- | --- | --- |
| 单元（内存库，新增） | `src/ERP.UnitTests/SalesOrderSourceLineageTests.cs` | 锁语句 / 锁序与 ERP-399 / ERP-400 同源；事务与行锁非关系型等价无操作；来源合并（未给 Id 保留历史 / 显式改绑 / 归一化）；权威解析（已审核 PI 规范化来源号与祖先、冲突来源对、异客户、已删除、完全无法解析的历史值、未携带来源）；唯一目标唯一化与 PI 祖先不误判；新增（权威来源号落库且不伪造来源状态、手工订单有效、重复目标拒绝、未审核 / 异客户拒绝、历史值原样保留）；修改（保留历史来源、显式改绑、改绑冻结、改绑到无法解析来源、改绑到被占用来源）；提交 / 审核（未链接既有口径、已链接重查通过、来源已删除冻结）；实时授权（无身份 / 无菜单 / 越范围 / 禁用 / 范围内放行）；控制器源码契约（先解析血缘后预约单号、来源字段不再照抄提交文本） |
| 单元（既有回归，扩展） | `src/ERP.UnitTests/SalesOrderConversionTests.cs`（+3 例） | 普通保存与直接转换共用同一把来源行锁与唯一目标口径；普通保存链接报价单与报价单直接转换唯一目标一致；PI 带入预填草稿经服务端复核后可保存并留痕权威来源（来源状态不被改写） |
| 单元（既有回归，未修改） | `OrderTraceabilityTests` / `SalesOrderControllerTests` / `ProformaInvoiceControllerTests` / `QuotationToPiTests` | 既有追溯字段、CRUD、状态流转与转换口径保持通过 |
| SQL Server 集成（受控 localdb，新增） | `src/ERP.IntegrationTests/SalesOrderSourceLineageSqlServerTests.cs` | 真实既有授权 fail closed（无身份 / 无菜单 / 禁用，零写入且来源 `UpdatedAt` 回滚）；伪造 / 异客户 / 已删除 / 未审核 / 冲突来源对原子拒绝且来源零改写；权威来源号落库与未链接手工订单有效；拒绝整体回滚；**三条独立连接竞态**：普通保存 vs 直接转换、普通保存 vs 来源作废、两张并发普通保存 —— 结果唯一且来源 / 目标历史一致；锁语句契约；专用目标护栏 fail-closed 4 例 |
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
| 全量单元测试 | `dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build` | **6110 通过 / 0 失败 / 0 跳过** |
| ERP-401 真实 SQL | `dotnet test src/ERP.IntegrationTests/ERP.IntegrationTests.csproj -c Release --no-build --filter "FullyQualifiedName~SalesOrderSourceLineage"` | **14 通过 / 0 失败**（专用实例 `(localdb)\NEWERP_AutoAcceptance` + 全新 GUID 后缀 `NEWERP_AUTOTEST_*` 库；含 3 条双连接竞态、拒绝矩阵、权威规范化、回滚证据与专用目标护栏 4 例） |
| 浏览器验收 | 任务配置 `browser_acceptance.required = false` | 未执行，也不声明通过 |

#### 实际执行结果

- 目标：专用实例 `(localdb)\NEWERP_AutoAcceptance` + 全新 GUID 后缀 `NEWERP_AUTOTEST_*` 库（夹具输出确认「目标库护栏放行 / 集成场景就绪」）。
- 结果：**14 通过 / 0 失败 / 0 跳过（总时长 13 s）**，其中：
  - 无身份 / 无既有「销售订单」菜单 / 禁用账号：3 例 fail closed，零订单写入，来源 `UpdatedAt` 随事务回滚；
  - 异客户 / 已删除 / 未审核 / 冲突来源对：1 例聚合断言，零订单、来源状态与审计零改写；
  - 权威来源号规范化 + 未链接手工订单有效：1 例；
  - 拒绝整体回滚（来源 `UpdatedAt` / 状态 / 订单）：1 例；
  - **两条独立连接竞态 ×3**：普通保存 vs PI 直接转换、普通保存 vs PI 作废、两张并发普通保存 —— 每例恰好一方成功且同一来源至多一张目标订单 / 历史一致；
  - 锁语句与 ERP-399 / ERP-400 同源：1 例；专用目标护栏 fail-closed：4 例。
- 验证产物（日志 / TRX）一律写到工作区之外的临时目录；工作区变更集严格限于本任务 `allowed_paths`。

> 构建完成不等于阶段验收：只有受控 localdb 上真实执行通过、或给出准确 blocker，才构成阶段验收证据。
