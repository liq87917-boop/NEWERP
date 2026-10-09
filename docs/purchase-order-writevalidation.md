# 规范采购订单「新写入条款」校验（ERP-426）

## 1. 背景与目标

采购订单既有唯一权威合计口径（`总额 = Σ 明细数量 × 单价`），由
`PurchaseOrderController.Calculate` 提供，并被**供应商比价 → 采购订单**（ERP-020 / ERP-418，
`PurchaseQuoteConversion`）复用。但普通写入路径（`POST /api/purchase-orders`、
`PUT /api/purchase-orders/{id}`）此前只做「税率 0~100」一项业务校验，并在服务端重算金额时使用
**未校验**的数量 / 单价，且 `Calculate` 为 unchecked 求和：

- 负数量、零数量、过小数量（正数但会被 `DECIMAL(18,2)` 落库取整为 0）可以进入核心手工采购生命周期；
- 负单价、超出存储精度的单价、未定义 `Currency`、汇率为 0 或超出精度同样放行；
- 行金额 / 总额在 `decimal` 溢出或超出实际 EF 精度时可能被**静默取整**，导致落库明细金额之和
  与主表总额不一致；
- 提交 / 审核只做状态流转与来源血缘复核，不校验**已持久化条款**是否仍然合法；
- 比价转换（ERP-418）只用自定义 `Revalidate` 校验数量 / 单价并 `Math.Round` 行金额，与页面录入并非同一口径。

ERP-426 把**金额算法与精度校验**统一抽到唯一权威 `PurchaseOrderAmountRules`，并把新增 / 修改 /
提交 / 审核四条路径全部收敛到同一口径，同时保持历史读取 / 打印完全只读、不自动修正。

## 2. 唯一权威校验

`PurchaseOrderAmountRules.ValidateNewWrite(PurchaseOrder)`（ERP-426）逐项要求：

| 项 | 要求 |
|---|---|
| 有效明细 | 未软删除的明细**至少一行**（`Detail.IsDeleted = false`） |
| 数量 | `> 0` 且可在实际 EF 精度内表示（**绝不**把正数量静默舍入为 0） |
| 单价 | `>= 0` 且可在实际 EF 精度内表示（保留既有「零单价」政策） |
| 币种 | `Currency` 枚举**已定义**值（不跨币种合计） |
| 汇率 | `> 0` 且可在实际 EF 精度内表示 |
| 税率 | `0 ~ 100`（含端点） |
| 行金额 / 总额 | 逐行 `数量 × 单价`、`Σ 行金额` 全部 **checked** 且可在实际 EF 精度内表示 |

任一项不满足一律抛出 `BusinessException.InvalidParameter`（受控业务错误）：`decimal` 乘法 / 加法
溢出被捕获后转换为同一受控错误，绝不落库截断值，也绝不写入与明细合计不一致的金额。校验只做判定、
不改写任何字段，且**先于**任何单据号预约与字段 / 明细赋值。

「不跨单位 / 币种合计」：订单只有单一 `Currency`，合计只累加**货币金额**，从不跨单位累加数量。

## 3. 实际 EF 精度

采购订单主表与明细（`db_owner.PurchaseOrders` / `db_owner.PurchaseOrderDetails`）在 EF 模型中**未**
显式 `HasPrecision`，因此数量 / 单价 / 行金额 / 合计 / 汇率 / 税率一律按 EF 默认的 **`DECIMAL(18,2)`**
落库（`SchemaUpgrader` 也只对这些表追加列，不改精度）。

`PurchaseOrderAmountRules` 以常量固化该口径：`DecimalPrecision = 18`、`DecimalScale = 2`、
`MaxDecimal18Scale2 = 9_999_999_999_999_999.99m`，并提供 `IsRepresentable(decimal)` /
`IsLineAmountRepresentable(decimal, decimal)`。单元测试 `PurchaseOrderWriteValidationTests`
用 SQL Server 提供程序**仅构建 EF 模型**（不打开任何连接）断言上述列的实际存储类型与常量逐项一致。

**通过校验后**，`ApplyDetailAmounts` / `Calculate` 的结果可被 EF 原样持久化：明细金额之和与主表总额
逐分一致，不存在落库取整差（交付物：`Σ 明细金额 == 主表总额`）。

## 4. 四条写入路径的接入点

| 路径 | 接入点 | 时序 |
|---|---|---|
| 新增 `POST /api/purchase-orders` | `PurchaseOrderController.Create` | 开事务 → **条款校验** → 来源 / 归属加锁 → 单号预约 → 字段与明细改写 → 服务端重算 → 锁内再校验 → 提交事务 |
| 修改 `PUT /api/purchase-orders/{id}` | `PurchaseOrderController.Update` | 开事务 → **条款校验** → 加锁 → 锁内权威重读 → 字段与明细改写 → 服务端重算 → 锁内再校验 |
| 提交 `POST /api/purchase-orders/{id}/submit` | `PurchaseOrderController.Submit` | 行锁内权威重读（含明细）→ **已持久化条款校验** → 置状态 |
| 审核 `POST /api/purchase-orders/{id}/approve` | `PurchaseOrderController.Approve` | 同上（并在来源血缘复核之后、置状态之前） |

- 新增 / 修改：校验在**单号预约与任何字段 / 明细改写之前**完成，失败既不消耗单号也不落库，且整体回滚并
  清空变更跟踪器中的半成品；合法请求中客户端提交的 `Amount` / `TotalAmount` / `Status` / `OrderNo` /
  `CreatedAt` 一律被服务端按明细重算或重置覆盖，客户端合计 / 状态 / 审计字段**不能**覆盖服务端权威。
- 提交 / 审核：在**既有采购订单行锁内**对持久化表头 + 明细复核同一套条款；非法存储条款原子拒绝、状态与
  原始证据均不变，且不产生入库 / 库存 / 财务任何下游变更。
- 校验入口由 `PurchaseOrderMutationRules.EnsureValidatedTerms`（委托唯一权威 `ValidateNewWrite`，口径文案见
  `ValidatedTermsText`）提供，避免出现第二套规则。`PurchaseOrderController.Calculate` / `Validate`
  改为转发同一权威实现。

## 5. 带入 / 转换复用

- **供应商比价 → 采购订单**（`PurchaseQuoteConversion.Revalidate`）：带入预填与直接生成都改为
  `PurchaseOrderAmountRules.ApplyDetailAmounts` + `ValidateNewWrite`，与页面手工录入**完全同一套**服务端规则，
  不再使用自定义的逐行 `Math.Round` 口径；来源选择 / 审批 / 唯一目标守卫保持 ERP-418 既有口径不变。
- 供应商比价行状态、审批决定、重复生成判据（`RefOrderNo`）等来源证据**绝不改写**。

## 6. 边界（不做的事）

- 不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，不改变既有 `DECIMAL(18,2)` 精度；
- 不做任何历史数据回填 / 自动修正：`GetById` / `GetPrint` / 导出等读取路径仍原样返回已持久化值，
  即使历史条款非法也可读、不被改写；
- 不伪造授权，不把空身份当作匿名或管理员；不新增权限、不提供 HTTP / 测试身份旁路；
- 不触碰库存 / 入库 / 库存成本 / 应付发票 / 付款 / 财务记录；提交 / 审核本身不过账；
- 不修改 `.env*` / `appsettings*.json` / 连接串 / 生产库结构或数据。

## 7. 验收证据

| 类型 | 文件 | 覆盖 |
|---|---|---|
| 单元 | `src/ERP.UnitTests/PurchaseOrderWriteValidationTests.cs` | 负 / 零 / 过小数量、负 / 超精度单价、未定义币种、零 / 超精度汇率、越界税率、乘法溢出、行金额不可表示、边界精度与零单价、EF 精度契约；新增 / 修改 / 提交 / 审核的真实控制器行为（零写入、不消耗单号、伪造合计 / 状态 / 审计被覆盖、非法存储条款原子拒绝、精确落库、血缘不变、历史读取不被修正） |
| 集成 | `src/ERP.IntegrationTests/PurchaseOrderWriteValidationSqlServerTests.cs` | 真实 SQL Server 上同样场景 + 目标护栏 fail-closed |
| 回归 | `OrderTraceabilityTests`（采购创建补有效明细）、`PurchaseOrderControllerTests`、`PurchaseOrderMutationTests`、`PurchaseOrderAuthorizationTests`、`PurchaseQuoteConversionTests`、`PurchaseQuoteConversionBatchTests` 等 | 既有正常流程与来源血缘 / 比价转换 / 采购执行契约保持不变 |

## 8. 真实 SQL 夹具安全口径

- 目标必须是专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且
  `Integrated Security=true`；错误实例 / 错误库名 / 非集成安全的连接串一律在 `AssertDedicatedTarget`
  阶段于**任何数据库访问之前**被拒绝（含 fail-closed 单元覆盖）。
- 每次运行只创建一个**全新 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，
  绝不读取 `appsettings*.json` / `.env` / 生产凭据。
- 保留来源单据审计与原始失败日志：夹具只追加种子行，不删除 / 不清理任何既有行。
- **构建完成不等于阶段验收**：真实 SQL 场景须在专用 localdb 上真实执行通过才算验收证据。
