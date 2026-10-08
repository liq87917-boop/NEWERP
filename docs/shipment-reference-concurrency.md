# 装柜出运引用登记的实时授权与串行化（ERP-362）

> 阶段 3 · 装柜（container）核心闭环收口 · 复用既有 ERP-097 数据范围、ERP-360/361 授权口径与 ERP-353 行锁

## 1. 目标

把既有装柜出运引用登记（`ContainerShipmentReference`，`api/container/shipment-references`）的**每一个路由**
都纳入与其它单据一致的实时授权与客户数据范围护栏，并把「登记 / 修订 / 作废」三条写路径放进
**可序列化事务 + 行锁**，使并发不再能产生第二条有效引用、也不能改写已作废证据：

1. **登记**：在**源记录行锁**（`UPDLOCK, HOLDLOCK`）内完成「源记录有效性（存在 / 未删除 / 未取消）→
   客户数据范围 → 有效引用唯一性 → 证据字段校验 → 插入证据行」，同一源记录的并发登记只能成功一条；
2. **修订 / 作废**：在**本引用行锁**内串行化，修订逐版保留前值并递增修订号，作废后证据只读，
   重复作废与「作废后再修订」都被明确拒绝，任一步校验失败随事务整体回滚；
3. **读取**：台账 / 详情 / 修订留痕 / 源记录候选 / 报关行选项全部先授权，受限账号的客户数据范围
   **在计数与分页之前**下推到数据库。

只复用既有分层架构、既有权限模型与既有单据流转，**不新增**用户授权、表 / 列 / 菜单、外部系统调用，
也不改写源记录（订柜信息 / 预装柜单 / 装柜清单）与库存、财务、单证等任何下游数据。

## 2. 口径

| 项 | 口径 |
|---|---|
| 身份 | 每次请求按 `ClaimTypes.NameIdentifier` 解析：缺失 / 非正整数 → `2000` 未认证；账号不存在或已 `IsDeleted` → `2000`；`Status != Enabled` → `2002` 权限不足 |
| 源模块菜单 | 源记录类型 → 既有菜单：`booking` 订柜信息 / `pre-loading` 预装柜单 / `loading-list` 装柜清单（与 `SeedData` 同源）；**非特权账号**必须显式具备对应类型的既有菜单，撤销后下一次请求立即收敛；**特权账号**（超级管理员 / 系统内置角色 / 显式特权角色，与 ERP-097 同源）继承既有全部访问 |
| 数据范围 | 复用 `SalespersonDataScopeService`（ERP-097 唯一权威口径）；客户归属取**源记录的权威客户**：订柜信息 / 装柜清单取本单 `CustomerId`，预装柜单取所链接订柜信息的客户；**没有权威客户归属的历史预装柜单**与**未映射业务员的受限账号**一律 fail closed，绝不降级为全局 / 管理员可见 |
| 列表范围下推 | `ShipmentReferenceAuthorizationRules.ApplyScope` 在 `CountAsync` / 分页之前把「允许的源记录类型 + 客户范围」下推到数据库：受限制账号只统计 / 只返回本人客户的引用证据，绝不「先查全量再内存过滤」 |
| 源记录资格 | **新登记**要求源记录存在、未删除且**未取消**；不存在 → `1002`；已删除 / 已取消 → `1004`；历史证据照常可读并显式标注不可用 |
| 有效引用唯一性 | 同一源记录最多 1 条有效引用（库侧过滤唯一索引 `UX_ContainerShipmentReferences_ActiveSource` 兜底）；已作废行不占额度，作废后可重新登记 |
| 登记串行化 | 同一可序列化事务内先对**源记录行**加锁：`SELECT Id FROM db_owner.ContainerBookings|ContainerPreLoadings|ContainerLoadingLists WITH (UPDLOCK, HOLDLOCK) WHERE Id = @id`（只 `SELECT Id`，不改写源记录任何列） |
| 修订 / 作废串行化 | 同一可序列化事务内先对**本引用行**加锁：`SELECT Id FROM db_owner.ContainerShipmentReferences WITH (UPDLOCK, HOLDLOCK) WHERE Id = @id` |
| 修订留痕 | 每次成功修订先写一条只追加的「修订前原值」留痕（含必填原因），再写回新值并把 `RevisionNo` 递增 1；修订号单调、同引用内不重复 |
| 作废 | 必填原因，置 `Status = 2` 并记录 `VoidedAt` / `VoidReason`；原始证据字段、源记录快照与全部修订留痕保留；重复作废 → `1004`，已作废后修订 → `1004` |
| 失败即回滚 | 校验失败发生在写入之前；即便发生部分写入，`catch` 中 `RollbackAsync` 保证证据行与修订留痕都不产生半行 |
| 审计 | `CreatedBy` / `UpdatedBy` 记录真实登录用户 Id；不新增审计表 / 列 |

## 3. 路由矩阵（全部先授权）

| 路由 | 授权 | 数据范围 | 事务 / 行锁 |
|---|---|---|---|
| `GET api/container/shipment-references` | ✅ | ✅ 数据库侧（先于计数 / 分页） | — |
| `GET api/container/shipment-references/source-candidates` | ✅（按源类型） | ✅ 数据库侧（先于 `Take`） | — |
| `GET api/container/shipment-references/customs-broker-options` | ✅ | 字典项不按客户分范围 | — |
| `GET api/container/shipment-references/{id}` | ✅（按源类型） | ✅ 单条 | — |
| `GET api/container/shipment-references/{id}/revisions` | ✅（按源类型） | ✅ 单条 | — |
| `POST api/container/shipment-references` | ✅（按源类型） | ✅ 源记录客户 | ✅ 源记录行锁 + `Serializable` |
| `PUT api/container/shipment-references/{id}` | ✅（按源类型） | ✅ 源记录客户 | ✅ 引用行锁 + `Serializable` |
| `POST api/container/shipment-references/{id}/void` | ✅（按源类型） | ✅ 源记录客户 | ✅ 引用行锁 + `Serializable` |

非关系型提供程序（内存库 / 单元测试）无法执行表提示，`IsRelational()` 为假时跳过加锁——
事务语义在这些测试里等价「无事务」，真实串行化由 SQL Server 集成测试覆盖。

## 4. 并发协同

### 4.1 同一源记录的并发登记（create / create）

1. 事务外先解析身份 / 账号状态 / 既有源模块菜单 / 客户数据范围（fail closed 时不进事务、不加锁、不写库）；
2. 开启 `IsolationLevel.Serializable` 事务；
3. 对**源记录行**加 `UPDLOCK/HOLDLOCK`（后到者在此等待，直到先到者提交 / 回滚）；
4. 锁内加载源记录（存在、未删除、未取消）并校验客户数据范围；
5. 锁内查询是否已有有效引用（`Status = 1`）：有 → 明确 `1003` 重复；
6. 写入证据行并提交，失败即回滚。

因此「两条独立连接同时登记同一源记录」只能成功一条：后到者在锁内看到已登记的有效引用并被明确拒绝，
库中始终只有一条有效引用（过滤唯一索引 `UX_ContainerShipmentReferences_ActiveSource` 作为第二道防线）。

### 4.2 同一引用的并发修订 / 作废（update / void）

1. 事务外先授权；
2. 开启 `IsolationLevel.Serializable` 事务并锁定**本引用行**；
3. 锁内重新加载本引用 → 复核状态机（只有已登记可修订 / 作废）；
4. 修订：写留痕 → 写新值 → `RevisionNo + 1`；作废：置状态 / 原因 / 时间；
5. 提交事务，失败即回滚。

结果：**作废一旦生效，任何并发或后到的修订都不能改写已作废证据**（后到者在锁内看到 `Status = 2`
并收到 `1004`）；**重复作废**同样收到 `1004`；**并发两次修订**在锁内串行化后全部成功，
修订号单调递增且每版留痕保留各自前值（无丢失、无重复修订号）。

## 5. 错误信息（API 直接返回）

| 场景 | 业务码 | 说明 |
|---|---|---|
| 未登录 / 身份非法 / 账号不存在或已删除 | `2000` | 一律 fail closed，绝不猜测身份 |
| 账号已禁用 | `2002` | 禁用账号不执行任何读取与写入 |
| 没有对应源记录类型的既有菜单 / 未映射业务员 / 客户范围外 | `2002` | fail closed，不泄露范围外证据 |
| 源记录不存在（新登记） | `1002` | 历史证据仍可读 |
| 源记录已删除 / 已取消（新登记） | `1004` | 历史证据仍可读，不能新登记 |
| 同一源记录已有有效引用 | `1003` | 提示改为修订或先作废再登记 |
| 重复作废 / 已作废后修订 / 改派源记录 | `1004` / `1001` | 明确拒绝，不改写已有证据 |
| 证据字段非法（长度 / 出运方式 / 计划时间 / 报关行） | `1001` | 校验先于写入，证据与留痕都不落库 |

## 6. 验收与测试

| 层次 | 覆盖 |
|---|---|
| 单元（`ERP.UnitTests`，内存库） | `ContainerShipmentReferenceTests`（登记 / 修订 / 作废 / 台账 / 候选 / 契约）与 `ShipmentReferenceConcurrencyTests`（两个独立上下文的并发登记、作废后重新登记、修订 vs 作废串行化、重复修订单调性、校验失败不落库、已取消 / 已删除 / 不存在源记录拒绝、受限账号菜单与客户范围、控制器行锁与授权接线契约） |
| 集成（`ERP.IntegrationTests`，真实 SQL Server） | `ShipmentReferenceConcurrencySqlServerTests`：**create/create**、**update/void**、**repeated revision** 三条真实两条独立连接竞争测试，全部经真实控制器与既有登录账号驱动 |

集成测试目标口径（安全护栏在**任何数据库访问之前**生效）：

- 实例必须为 `(localdb)\NEWERP_AutoAcceptance`，库名必须以 `NEWERP_AUTOTEST` 开头，必须集成安全；
- 每次运行只创建**全新 GUID 后缀库**，发现同名库已存在立即拒绝；
- **绝不** drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据；
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default`（若存在）或专用 localdb 默认值。

> 构建通过不等于阶段验收：本模块的证据链（单元 + 真实 SQL 竞争 + 文档）齐备后才视为收口。

## 7. 边界

- 只读写 `ContainerShipmentReferences` 与 `ContainerShipmentReferenceRevisions` 两张表；
- **不**改写源记录（订柜信息 / 预装柜单 / 装柜清单）任何列、状态与工作流，加锁只 `SELECT Id`；
- **不**写销售订单、采购订单、库存与库存成本、库存流水、单证中心、发票、费用与分摊、收付款、税务与结算记录；
- **不**按柜号 / 订单号 / 单证号等自由文本匹配或「猜」源记录（显式类型 + Id 是唯一关联方式）；
- **不**新增用户授权、表 / 列 / 菜单 / 权限模型，**不**做匿名 / 管理员回退，**不**调用承运人 / 海关 / 货代等外部系统。
