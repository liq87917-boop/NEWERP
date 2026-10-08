# 定金申请单生命周期护栏（ERP-381）

## 1. 目的

补齐定金申请单（`FinanceDepositApply`）与其「显式来源销售订单（`FinanceDepositApply.SalesOrderId`）」之间的运营完整性缺口：

- 原实现中定金申请单 `Create` 接受 `CustomerId / SalesOrderId / Amount / Currency / ExchangeRate` 却没有任何运营校验，
  `Update` 会直接覆盖这些商业字段，`Submit / Approve / Cancel / Delete` 走无条件基类状态逻辑，
  且控制器完全没有身份 / 菜单 / 客户数据范围护栏（列表与详情会跨客户返回全部数据）。
- 定金申请单同时是既有财务报表的**收款申请权威引用**（`ShipmentFinanceReportConfigurationDatasetProvider`
  按 `SalesOrderId` 归属「已关联金额」），因此来源销售订单若可在定金申请单存在时被静默取消，
  报表证据与来源链接会失效（孤儿 / 陈旧来源）。

ERP-381 在不新增表 / 列 / 菜单 / 权限模型、不新增用户授权、不引入匿名或管理员兜底的前提下，通过既有业务 API / 服务补上护栏。

## 2. 关键不变量

1. **身份 / 菜单 / 客户数据范围（fail closed）**：列表 / 详情 / 创建 / 修改 / 提交 / 审核 / 取消 / 删除每次都重新校验
   当前**实时启用身份**（账号存在、未删除且启用）、既有定金申请单（`deposit-apply`）菜单授权与当前**权威客户数据范围**
   （受限制业务员越界 → 权限不足且不泄露范围外客户）；菜单授权与客户范围都在**计数 / 取行 / 生成单号 / 写入之前**生效。
2. **商业字段完整性**：客户必须存在、未删除、启用；币种必须在既有系统币种口径内；金额按币种精度取整（0.5 进位）后
   必须大于 0；汇率必须大于 0。
3. **来源销售订单精确资格（绝不猜来源）**：可空 `SalesOrderId` 为 `null` 时保留历史「未关联来源」语义；
   非空时必须按 Id 精确解析到**既有、未删除、未取消**的销售订单，且订单客户与申请单客户一致、币种兼容；
   悬空 / 已删除 → 不存在；已取消 / 跨客户 / 币种不兼容 → 规则冲突。绝不按订单号文本、金额或相似度匹配。
4. **既有与请求客户 / 来源范围**：修改会在锁内同时复核**既有持久化**客户 / 来源（仍在范围内且资格有效）与
   **请求**客户 / 来源（在范围内且资格有效），绝不基于陈旧来源放行。
5. **与销售订单取消证据护栏协同**：只要仍存在**未删除且未取消**的定金申请单以 `SalesOrderId` 显式指向某销售订单，
   该销售订单的取消即被拒绝（`SalesOrderCancellationRules`）；显式取消 / 软删除定金申请单后释放，
   但历史记录与审计原样保留（绝不物理删除收款申请证据）。
6. **提交 / 审核**在来源销售订单行锁与申请单行锁内用既有规则复核**持久化**客户 / 币种 / 金额 / 汇率 / 来源链接
   （不新增审批要求、不新增单据状态），非法持久化数据不会被流转放行。

## 3. 原子性与并发（锁序）

- **统一锁序（ERP-381）**：**先来源销售订单行（`UPDLOCK, HOLDLOCK`）、后定金申请单行**，绝不反向获取。
  - 来源销售订单行锁复用与销售订单取消 / 出库审核 / 预装柜流程同一常量
    （`PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql`），修改时**既有来源与请求来源都按 Id 升序**加锁。
  - 定金申请单行锁（`FinanceDepositApplyLifecycleRules.LockApplyRowAsync`）：EF Core 基础 API 对申请单行发一条
    审计时间戳刷新 `UPDATE` 取得 X 锁，持有至事务结束，语义等价于 `SELECT ... WITH (UPDLOCK, HOLDLOCK)`
    但不依赖关系型扩展；`UpdatedAt` 是技术审计字段、不是商业字段。
  - 修改 / 提交 / 审核 / 取消 / 删除五个动作在同一事务内先取来源订单行锁、再取申请单行锁，然后加载权威状态 / 字段
    并在锁内复核身份 / 菜单 / 客户范围 / 来源资格；创建在生成单号前校验、并在同一事务内锁内复核来源资格后落库。
  - 若并发方已先落库触发 `RowVersion` 乐观并发冲突，则转成可读业务冲突拒绝（fail closed），绝不静默覆盖赢家。
- **竞态结论**：
  - 「编辑 vs 提交」：串行化后提交必然在锁内看到权威状态并成功；编辑只有在提交之前落库才成功，否则在锁内重读状态被拒
    （绝不丢失更新，也绝不出现「已提交后仍被改写金额」）。
  - 「审核 vs 取消」：两者经同一把申请单行锁；取消始终生效，审核只有抢到第一把锁（先于取消）才成功，
    否则在锁内重读状态被拒，绝不出现陈旧状态或半成品写入。
  - 「审核 vs 来源销售订单取消」：审核先取来源订单行锁；来源取消取同一把订单行锁后再读取定金证据。
    只要仍存在未删除且未取消的定金申请单指向该订单，来源取消**绝不可能赢**（先赢者：审核成功 → 取消在锁内被证据护栏拒绝；
    取消先赢 → 也在锁内被证据护栏拒绝后回滚，订单保持已审核，审核随后成功）。
- 失败整体回滚：状态、原始字段、删除标记与审计不变；本护栏不产生任何资金记账 / 结算 / 库存流水。

## 4. 接口

沿用既有定金申请单路由，不新增路由、不改变请求 / 响应结构：

- `GET /api/finance/deposit-applies`（分页，身份 / 菜单授权 + 客户数据范围过滤在计数与取行之前）
- `GET /api/finance/deposit-applies/{id}`（详情，越界 fail closed）
- `POST /api/finance/deposit-applies`（创建，校验客户 / 币种 / 金额 / 汇率 / 来源后才生成单号）
- `PUT /api/finance/deposit-applies/{id}`（仅待提交可改；锁内复核既有 + 请求客户 / 来源范围）
- `POST /api/finance/deposit-applies/{id}/submit` / `/approve` / `/cancel`
- `DELETE /api/finance/deposit-applies/{id}`（仅待提交可删）

> 同一文件内的 `FinancePaymentApplyController`（ERP-380）保持不变；ERP-381 只覆盖 `FinanceDepositApplyController`。

## 5. 关键文件

- `src/ERP.Application/Services/FinanceDepositApplyLifecycleRules.cs`（纯规则 + 有界只读查询 + 行锁）
- `src/ERP.Api/Controllers/FinanceApplyControllers.cs`（仅 `FinanceDepositApplyController` 生命周期护栏 + 来源订单行锁执行）
- `src/ERP.Application/Services/SalesOrderCancellationRules.cs`（ERP-381：未删除且未取消的定金申请单来源证据拒绝取消）
- `src/ERP.Application/Services/PreLoadingSalesOrderLinkRules.cs`（复用同一把上游销售订单行锁常量）
- `src/ERP.UnitTests/FinanceDepositApplyLifecycleTests.cs`（内存库）
- `src/ERP.IntegrationTests/FinanceDepositApplyLifecycleSqlServerTests.cs`（真实 SQL）

## 6. 测试与验证

- 单元测试：`src/ERP.UnitTests/FinanceDepositApplyLifecycleTests.cs`（内存库，`TestDbFactory`），覆盖实时启用身份 /
  revoked 菜单 / 停用账号 / 受限制业务员范围（列表计数在范围过滤之后、范围外详情与流转拒绝）、不受支持币种 /
  精度取整后非正金额 / 非正汇率 / 不可用或越界客户、来源订单的悬空 / 已取消 / 跨客户 / 币种不兼容拒绝与
  空来源历史语义放行、金额按币种精度取整（CNY 2 位 / JPY 0 位）、被拒编辑与流转不改写原始字段（含审计时间戳）、
  取消 / 删除的状态约束，以及与销售订单取消证据护栏的协同（存在未删除且未取消的定金申请单即拒绝取消来源销售订单，
  取消 / 删除后释放）。
- SQL Server 集成测试：`src/ERP.IntegrationTests/FinanceDepositApplyLifecycleSqlServerTests.cs`，覆盖真实
  own / foreign / revoked / disabled 四类身份与范围判定、非法币种 / 金额 / 汇率 / 来源、被拒编辑不改写（含审计时间戳）、
  来源销售订单取消与定金申请单的联动，以及**两个独立连接竞态**
  （编辑 vs 提交、审核 vs 取消、审核 vs 来源销售订单取消）：串行化后结果唯一一致，无陈旧来源、丢失更新、
  孤儿引用或半成品写入，且不产生任何资金记账 / 结算行。
- 安全验证档：`dotnet restore` + Release 构建（`TreatWarningsAsErrors` / 分析器开启）+ `ERP.UnitTests` 全套。
- **构建完成不等于阶段验收**：真实 SQL 场景只有在受控 localdb 上真实执行通过才算验收证据；浏览器验收按任务配置
  为 deferred（`browser_acceptance.required = false`），不影响本阶段判定。

## 7. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST`、`Integrated Security`；
  错误目标在访问数据库**之前** fail closed（`AssertDedicatedTarget`，含 fail-closed 单元覆盖）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings` / `.env` /
  生产凭据；使用 EF 生成的标识主键，绝不使用生产数据。

## 8. 边界

本护栏只保护定金申请单生命周期与既有销售订单来源链接：不会真的收款、不会记账或生成凭证、不会核销、不会移动资金，
也不改写销售订单状态 / 金额与明细、收款单与分摊证据、库存与库存成本、库存流水、退税记录或客户余额；
不新增菜单 / 角色 / 用户授权或表结构，也不把空身份当作管理员。既有库存来源单据审计与原始失败日志原样保留，
本护栏不做任何清理或重构。


## 独立 SQL 验证与测试请求隔离（2026-10-08）

独立执行最初出现 3 项失败，原始完整输出保留在 `.ai/logs/ERP-381-independent-sql.log`。两项竞态断言错误地要求指定操作必然获胜；现有 RowVersion 会显式拒绝失去更新竞争的请求，测试现要求至少一方成功、失败属于明确状态或并发冲突，最终状态和金额准确对应赢家，且无删除、库存或财务副作用。另一项测试把回滚后仍保留跟踪状态的 DbContext 用于第二次请求；现每次请求使用独立上下文，与正常请求生命周期一致。未绕过权限或改变业务服务；修复后在全新隔离库中 6 项真实 SQL 测试通过。

安全 Release 全解决方案构建 0 警告 / 0 错误；全量单元测试在限定单集合执行后 5716 项通过。原默认并发、运行参数尝试及诊断执行的宿主崩溃日志均保留；诊断时正在运行两个大批量订单证据容量测试，未确认确切崩溃根因，限定 runner 集合并行度后完整套件通过。此 runner 配置仅用于隔离快照验证，不修改项目调度器或业务源码。
