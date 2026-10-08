# 报价单生命周期、版本与转换的并发护栏说明（ERP-400）

- 任务：`ERP-400`「Protect quotation revisions and conversions with live authority and atomic source lifecycle」
- 权威实现：
  - `src/ERP.Application/Services/QuotationAuthorizationRules.cs`（实时身份 / 既有菜单 / 权威客户范围 / 转换目标授权）
  - `src/ERP.Application/Services/QuotationMutationRules.cs`（确定性报价单来源行锁 + 原子事务 + 锁内权威复核 + 生命周期 / 转换资格守卫）
  - `src/ERP.Api/Controllers/QuotationController.cs`（列表 / 详情 / 新增 / 修改 / 提交 / 审核 / 销审 / 取消 / 作废 / 删除 / 批量删除 / 创建版本 / 版本链 / 有效期提醒 / 打印 / 询价带入 / 预填 / 转 PI / 转销售订单全部落在同一锁协议内）
  - `src/ERP.Api/Controllers/SalesOrderConversion.cs`（`EnsureQuotationDraftMatchesSource`：转换结果与来源权威口径逐项一致）
  - `src/ERP.Application/Services/ProformaInvoiceMutationRules.cs`（`QuotationRowLockSql` / `CrossDocumentLockOrderText`：与 PI 锁共享同一把报价单行锁与确定锁序）
  - `src/ERP.Application/Services/QuotationRevisionRules.cs`（`EnsureSourceEligible`：版本来源资格纯规则）
- 继承关系：ERP-398 / ERP-399 已把「实时身份 / 既有菜单 / 权威客户范围 + PI 行锁」推到 PI 每个路由；
  ERP-400 把同一口径推广到**报价单**，并把「生命周期变更 / 版本创建 / 转 PI / 转销售订单」串行化为不可撕裂的业务操作。

## 1. 授权与客户范围（读取 / 写入 / 计数之前）

| 项 | 口径 |
| --- | --- |
| 身份 | 每次请求按 `ClaimTypes.NameIdentifier` 解析：缺失 / 非正整数 → `2000` 未认证；账号不存在或已 `IsDeleted` → `2000`；`Status != Enabled` → `2002` 权限不足 |
| 菜单 | 复用既有「报价单」菜单 `quotation`（`QuotationAuthorizationRules.RequiredMenuCode`）；**非特权账号**必须显式具备，撤销后下一次请求立即收敛；**特权账号**（超级管理员 / 系统内置角色 / 显式特权角色，与 ERP-097 同源）继承既有全部访问 |
| 转换目标菜单 | 转 PI 额外要求既有 `proforma-invoice`；转销售订单额外要求既有 `sales-order`。**来源可见绝不等于目标授权** |
| 权威归属 | 只按持久化 `Quotation.CustomerId` 判定，绝不按单号 / 客户名 / 询价单号等自由文本推断；受限账号访问 `CustomerId` 为空的「无主」报价单一律 fail closed |
| 列表 / 计数 | `ApplyScope` 在 `Count` 与分页**之前**把范围下推到 SQL，绝不「先查全量再内存过滤」 |
| 版本链可见性 | 链内每一张版本都必须落在实时范围内（`EnsureChainCustomerInScope`），存在一个可见版本不得泄露范围外版本的金额 / 状态 |
| 写入校验 | 新增 / 修改在改写任何字段**之前**校验拟议客户；修改同时复核已存与拟议两侧 |
| 身份来源唯一 | 请求体任何字段都不能指定或扩大账号身份；`null` 范围只表示「未经过 MVC 授权管线的进程内调用」，绝不代表匿名或管理员 |

## 2. 锁协议

| 项 | 口径 |
| --- | --- |
| 唯一来源锁 | 报价单来源行锁：`SELECT Id FROM db_owner.Quotations WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}`（`QuotationMutationRules.QuotationRowLockSql`，契约 / 文档同源） |
| 锁实现 | 仅用 EF Core 基础 API：在调用方事务内对报价单行发一条「审计时间戳刷新」`UPDATE`（`UpdatedAt` 是技术审计字段、不是商业证据），取得排它 X 锁，持有至事务结束；语义等价 `UPDLOCK, HOLDLOCK`。并发方先提交使本地乐观令牌 `RowVersion` 过期时，重读权威行后**有界重试**（`RowLockRetryAttempts = 8`） |
| 目标单据 | 转换在报价单行锁内**只读复核**既有来源 PI（持久化 `QuotationId`）与既有来源销售订单（持久化 `SourceQuotationId`），随后 `INSERT` 全新目标 —— 目标没有既有行可加锁，重复生成由「来源行锁 + 锁内重读」唯一化 |
| 跨单据锁序 | 恒定「**报价单行锁 → PI 行锁 / 销售订单行锁**」；`ProformaInvoiceMutationRules.QuotationRowLockSql` 引用同一常量，写路径绝不反向获取上游锁，因此不存在锁环 |
| 批量删除 | 全部报价单行按 **Id 升序**确定性加锁（去重、仅正整数） |
| 原子事务 | `BeginMutationTransactionAsync`：关系型后端开启真实事务，任一步失败整体回滚；调用方已开启事务时**绝不嵌套**；内存库等非关系型提供程序等价无事务（声明式校验不变） |
| 事务内回滚证据 | 行锁的 `UpdatedAt` 刷新、状态流转、字段改写、明细整体替换、已预约的目标单据编号与新建目标都在同一事务内；失败时全部回滚（集成测试断言 `UpdatedAt` 与状态零变化） |

## 3. 锁内权威复核

取得报价单行锁之后，控制器**重新加载**报价单（绝不复用加锁前的内存实体），并逐项复核：

1. **持久化生命周期状态**（`Status`）；并发方已提交的结果以锁内重读为准，绝不按陈旧状态放行；
2. **历史版本只读**：`QuotationRevisionService.IsSupersededAsync`（链内存在指向它的下一版本）→ 修改 / 提交 / 审核 / 销审 / 取消 / 作废 / 删除一律拒绝（版本创建**允许**从历史版本分支，既有 ERP-035 口径不变）；
3. **`RowVersion`**：乐观令牌不一致 / 并发改写时以 `DbUpdateConcurrencyException` 收尾并转为可读的业务冲突（`StaleRowVersionText`），绝不覆盖赢家；
4. **实时授权**：实时身份 + 既有 `quotation` 菜单 + 权威客户范围（ERP-097），转换额外要求既有 `proforma-invoice` / `sales-order` 菜单与**来源 / 目标客户独立复核**；
5. **有效明细**：`ActiveDetailCount`（未删除明细）用于审核与转换资格；
6. **既有转换资格**：按持久化 `QuotationId` / `SourceQuotationId` 复核是否已存在未删除目标（重复生成守卫），并以锁内重读的报价单重新构造目标草稿。

## 4. 生命周期与转换守卫矩阵（锁内判定）

| 路由 | 额外守卫（锁内） | 备注 |
| --- | --- | --- |
| `GET /` `GET /{id}` `GET /{id}/print` `GET /{id}/revisions` | 实时授权 + 权威范围下推 / 可见复核 | 计数与分页在范围之后 |
| `GET /validity-due` | 实时授权 + 范围下推 | 只读现有 `ValidUntil`，不新增结构 |
| `GET /from-inquiry/{inquiryId}` | 实时授权 + 询价单归属客户范围 | 带入预填只读，不落库 |
| `POST /` | 实时授权 + **拟议客户范围**先于单号生成 | 被拒绝的调用方绝不消耗单据号 |
| `PUT /{id}` | 已存 + 拟议归属；历史版本只读；无下游链接；仅待提交 / 已提交可改 | 明细整体替换，金额 / 合计按服务端口径重算 |
| `POST /{id}/submit` | 历史版本只读；无下游链接；仅待提交 | 保留既有口径 |
| `POST /{id}/approve` | 历史版本只读；无下游链接；已作废 / 已转订单 / 已审核拒绝；**无有效明细拒绝** | 草稿可直接审核 |
| `POST /{id}/unaudit` | 历史版本只读；无下游链接；仅已审核可销审 | 重新打开 |
| `POST /{id}/cancel` | 历史版本只读；无下游链接 | 既有允许口径保持不变（无状态限制） |
| `POST /{id}/void` | 历史版本只读；无下游链接；已作废 / 已转订单拒绝 | 保留既有口径 |
| `DELETE /{id}` | 历史版本只读；无下游链接；仅待提交可删 | 软删除 |
| `POST /batch-delete` | 按 Id 升序加锁；逐行范围 / 链接 / 状态复核；**任一行不满足即整体拒绝、零部分删除** | 混合允许 / 越界 / 无主 / 已链接批次整体拒绝 |
| `POST /{id}/revisions` | 实时授权 + 归属复核 + 来源行锁 + 锁内重读；`EnsureSourceEligible`（已作废拒绝） | 源版本一行不改；新版本一律草稿；并发版本号由唯一索引 + 锁兜底 |
| `GET /{id}/order-prefill` | 实时授权 + 来源 / 目标范围 | 只读（不加锁、不开事务、不占号、不写库） |
| `POST /{id}/to-pi` | 锁内转换资格（已作废 / 已完成 / 未审核 / 无有效明细 / 已生成 PI）；来源 + 目标范围 | 只新增 PI、绝不覆盖既有 PI |
| `POST /{id}/to-order` | 锁内转换资格（已作废 / 重复生成 / 已转 PI / 未审核 / 无有效明细）；来源 + 目标范围；`EnsureQuotationDraftMatchesSource` | 只新增订单、绝不覆盖既有订单 |

**已完成且存在下游链接的报价单一律冻结**（`DownstreamLinkedText`）：修改 / 提交 / 审核 / 销审 / 取消 / 作废 / 删除 / 批量删除全部拒绝，
保留显式历史；**绝不**用「取消目标单据」的反向冲销伪造一致性；**无下游链接时保留既有允许的全部流转**（不新增状态机限制）。

## 5. 转换一致性与预填

- 转 PI 资格顺序：已作废 → 已完成 → 未审核 → 无有效明细 → 已存在来源 PI；转销售订单资格顺序：已作废 → 已存在来源订单（提示既有单号）→ 已转 PI → 未审核 → 无有效明细。任一不满足即原子拒绝，不消耗单号、不写目标、不改来源状态。
- `SalesOrderConversion.EnsureQuotationDraftMatchesSource` 逐项复核落库订单与来源权威口径：显式 `SourceQuotationId` / 来源报价单号、币种、汇率、明细数量合计、合计金额（`Σ 数量 × 单价`）与定金（`总额 × 定金比例 %`）完全一致。
- **带入预填保持只读**（`GET /{id}/order-prefill`：不加锁、不开事务、不占号、不写库）；最终保存以销售订单新增路由为权威，报价单侧一致性由 `to-order` 的来源行锁协议保证。
- 不产生任何财务 / 库存过账：转换只新增 PI / 销售订单与明细（集成测试断言库存出库单与收款单计数不变）。

## 6. 边界

- 不新增菜单 / 角色 / 用户授权 / 表 / 列 / 索引；不把空身份当作匿名或管理员，不伪造任何授权（全部复用既有 ERP 权限模型）。
- 不改写报价单编号 / 状态 / 明细行 / 金额 / 合计 / 币种 / 汇率 / 单位等原始商业语义；行锁只刷新技术审计时间戳 `UpdatedAt`。
- 不改写来源询价单、PI（既不覆盖也不冲销）、销售订单、客户主数据、库存与库存流水、发票、费用或财务记录。
- 保留库存来源单据审计与原始失败日志：被拒绝 / 回滚的请求只回滚自己的写入，不清理 / 不删除任何既有行。
- 失败一律 fail closed：资格不符、范围越界、单号生成失败、写入失败都整体回滚，绝不留下半成品目标、孤儿明细或已占号。

## 7. 测试证据

| 层 | 文件 | 覆盖 |
| --- | --- | --- |
| 单元（内存库，新增） | `src/ERP.UnitTests/QuotationMutationTests.cs` | 锁 / 事务契约（`QuotationRowLockSql`、与 PI / 销售订单锁同源、Id 升序加锁、非关系型等价无操作）；生命周期守卫逐条；拒绝矩阵（无身份 / 缺菜单 / 停用 / 越界 / 拟议越界）；受限账号列表-详情-打印-版本 fail closed；转换另需目标菜单授权；下游链接冻结且零改写；审核无明细拒绝；审核-销审-修改状态机；批量删除混合批次整体拒绝；转 PI / 转销售订单重复生成唯一化与来源留痕；转换结果权威不一致拒绝；预填只读；控制器接线契约 |
| 单元（既有回归，更新） | `QuotationToPiTests.cs` / `SalesOrderConversionTests.cs`（补实时身份）/ `QuotationRevisionTests.cs` / `QuotationGovernanceTests.cs` / `QuotationCrudRegressionTests.cs` / `QuotationConversionScopeTests.cs`（+2 例范围谓词 / 归属复核） | ERP-024 / ERP-035 / ERP-018 既有 CRUD、版本链、打印、有效期与成交率口径保持通过；新增报价单范围谓词与 fail-closed 覆盖 |
| SQL Server 集成（受控 localdb，新增） | `src/ERP.IntegrationTests/QuotationMutationSqlServerTests.cs` | 真实既有授权（无身份 / 缺菜单 / 撤销菜单 / 停用 / 越界 / 混合批量拒绝，双菜单本人放行且数量-币种-汇率-单位-合计-定金快照一致、无过账）；**两个独立连接竞态**：转 PI / 转 PI、转销售订单 / 转销售订单、转销售订单 / 取消、转销售订单 / 创建版本、编辑 / 审核、创建版本 / 创建版本、混合批量删除 —— 恰好一个合法赢家且失败侧整体回滚；编号生成失败整体回滚（不落半成品、不占号、来源状态与审计零变化）；专用目标护栏 fail-closed |
| 安全 profile | `.ai/config.json` 的 `safe` | `dotnet restore` → Release 构建 → `ERP.UnitTests` 全量 |

### 7.1 SQL Server 集成目标护栏

- 实例必须精确为 `(localdb)\NEWERP_AutoAcceptance`，库名前缀必须为 `NEWERP_AUTOTEST`，且必须为 `Integrated Security`；不满足在任何库访问之前直接拒绝（`AssertDedicatedTarget`）。
- 每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings*.json` / `.env` / 生产凭据。

> 构建完成不等于阶段验收：只有受控 localdb 上真实执行通过、或给出准确 blocker，才构成阶段验收证据。
> 真实浏览器验收按任务配置为 `not_required`（未执行，也不声明通过）。

### 7.2 验证记录（attempt 2 复验）

| 项 | 命令 / 目标 | 结果 |
| --- | --- | --- |
| 安全 profile 还原 | `dotnet restore NEWERP.sln` | 成功（exit 0） |
| Release 构建 | `dotnet build NEWERP.sln -c Release --no-restore --no-incremental /p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true` | **0 警告 / 0 错误**（Domain / Application / Infrastructure / Api / IntegrationTests / UnitTests 全部重新生成） |
| 全量单元测试 | `dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build` | **6071 通过 / 0 失败 / 0 跳过** |
| ERP-400 真实 SQL | `dotnet test src/ERP.IntegrationTests/ERP.IntegrationTests.csproj -c Release --no-build --filter "FullyQualifiedName~QuotationMutation"` | **13 通过 / 0 失败**（专用实例 `(localdb)\NEWERP_AutoAcceptance` + 全新 GUID 后缀 `NEWERP_AUTOTEST_*` 库；含转 PI ×2、转销售订单 ×2、转销售订单 / 取消、转销售订单 / 创建版本、编辑 / 审核、创建版本 ×2、混合批量删除竞态，以及专用目标护栏 4 例） |
| ERP-399 PI 回归 | 同上 `--filter "FullyQualifiedName~ProformaInvoiceMutation"` | **11 通过 / 0 失败**（共享报价单行锁常量未破坏既有 PI 协议） |

- **验证产物不写入工作区**：日志与 TRX 一律落在工作区之外的临时目录，绝不产生 `allowed_paths` 之外的改动（`TestResults/*.trx` 正是上一次路径守卫失败的根因）；本次工作区变更集严格限于本任务 `allowed_paths`。
- 数据库使用：只创建全新 GUID 后缀库，不 drop / reset / 复用；被拒绝或回滚的请求只回滚自己的写入，保留库存来源单据审计与原始失败日志。

