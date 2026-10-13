# 报价单「实时主数据引用」护栏（ERP-472）

## 1. 背景与目标

报价单是「询价单 → **报价单 Quotation** → 形式发票 PI → 销售订单」业务链上的第一张**对外承诺凭据**，
此前的写入路径由多个护栏分层保护：

| 护栏 | 保护维度 |
|---|---|
| ERP-400 `QuotationAuthorizationRules` | 实时身份 / 既有「报价单」菜单 / 权威客户数据范围 |
| ERP-403 `SalesDocumentSourceLineageRules` | 询价单来源血缘、来源行锁与锁内权威重读 |
| ERP-470 `QuotationCommercialValidationRules` | 币种 / 汇率 / 数量 / 单价 / 金额 / 合计等商业条款 |
| ERP-400 `QuotationMutationRules` | 确定性报价单来源行锁 + 原子事务 + 锁内权威重读 |

但这些护栏都**没有**核对调用方提交的 `Quotation.CustomerId` / `Quotation.SalesmanId` 与明细
`QuotationDetail.ProductId` 是否仍指向**实时主数据**：失效引用（不存在 / 已删除 / 已停用 / 已离职）可以被写进
核心报价单，并在提交 / 审核 / 创建版本 / 询价转报价时成为可转 PI / 可转销售订单的实时履约。

ERP-472 在**唯一写入与运营承诺入口**上补齐「实时主数据引用复核」，并保持历史读取与既有手工 / 可空语义不变。

## 2. 唯一权威规则

`QuotationMasterReferenceRules`（`src/ERP.Application/Services/QuotationMasterReferenceRules.cs`）：

| 项 | 要求 | 失败码 |
|---|---|---|
| 可选客户 `CustomerId`（可空） | 留空（`null`）= 未指定客户，跳过（既有菜单 / 客户范围授权仍然决定性）；填写时必须为正整数且解析到**存在、未删除、启用（`Status = 1`）**的客户 | 非正 `1001`；不存在 / 已删除 `1002`；已停用 `1004` |
| 可选业务员 `SalesmanId`（可空） | 留空跳过；填写时必须为正整数；解析到既有员工行时必须**未删除 / 在职（`Status = 1`）/ 业务员（`IsSalesman = true`）**；**无对应员工行的历史悬空 Id** 按既有手工快照保留（不拒绝、不回填、不自动改派） | 非正 `1001`；已删除 `1002`；已离职 / 非业务员 `1004` |
| 明细商品 `ProductId`（可空） | 留空 = **手工商品快照行**（与界面一致：明细以编码 / 名称 / 规格 / 单位等自带文本为准）；填写时必须为正整数；商品资料**一次有界批量查询**（去重 `ProductId` 集合，绝不逐行查库）；解析到既有商品行时必须**未删除 / 启用（`Status = 1`）**；**无对应商品行时**：自带受界商品文本（编码或名称非空）的行按**手工快照行**保留，纯目录引用按**缺失引用**拒绝 | 非正 `1001`；已删除 / 无自带文本的缺失引用 `1002`；已停用 `1004` |

要点：

- **可空引用语义不变**：报价单的客户 / 业务员 / 明细商品本就允许留空（`null`），本次不改其语义，
  只对**显式填写**的值要求解析到实时主数据；
- **手工商品快照行仍是第一等公民**：报价单界面从不回写商品 Id（明细字段为编码 / 名称 / 规格 / 单位等文本），
  自带受界文本的行即使 `ProductId` 悬空也照常保存、读取与打印，且**绝不回填 / 绝不自动改派**；
- **绝不臆造主数据**：本护栏只做纯判定与有界只读投影查询，不写客户 / 员工 / 商品主数据，
  也绝不要求或授予任何主数据维护权限。

## 3. 接入点与时序

| 路径 | 接入点 | 时序（相对既有步骤） |
|---|---|---|
| 新增 `POST /api/sales/quotations` | `QuotationController.Create` | 授权 + 拟议客户范围 → **商业条款校验** → **实时主数据引用复核** → 来源血缘（含来源行锁）→ 单号预约 → 字段 / 明细写入 |
| 修改 `PUT /api/sales/quotations/{id}` | `QuotationController.Update` | 授权 + 归属 + 下游冻结 + 状态资格 → **商业条款校验** → **实时主数据引用复核** → 来源行锁 → 报价单行锁 + 锁内权威重读 → 字段 / 明细写入 |
| 提交 `POST /{id}/submit` | `RunLockedMutationAsync` | 报价单行锁内权威重读 → 下游 / 来源血缘 / 状态资格 → **已持久化商业条款校验** → **实时主数据引用复核** → 置「已提交」 |
| 审核 `POST /{id}/approve` | `RunLockedMutationAsync` | 同上 → 置「已审核」 |
| 创建版本 `POST /{id}/revisions` | `QuotationRevisionService.CreateRevisionAsync` | 报价单行锁内权威重读 → 源版本资格 → 已持久化商业条款校验 → **源版本实时主数据引用复核** → 版本号 / 单号分配 → 复制明细 |
| 询价转报价 `POST /api/inquiries/{id}/to-quotation` | `InquiryQuotationConversion.BuildDraftAsync` | 询价单来源行锁内重读 → 转换资格 → 商业条款校验 / 整理 → **草稿实时主数据引用复核** → 报价单号预约 → 落库 + 来源置「已完成」 |
| 带入预填 `GET /api/inquiries/{id}/quotation-prefill` | 同上（只读草稿） | 只读草稿也先复核引用：非法来源引用连草稿都不产出（不落库 / 不占号 / 不改来源状态） |
| 明细带入 `GET /api/sales/quotations/from-inquiry/{id}` | 只读投影（不在本护栏内） | 仅返回既有询价数据；**写入路径（新增 / 修改）仍会复核实时主数据引用**，因此草稿失效引用无法落库 |

- 引用复核**先于单号预约与任何表头 / 明细赋值**；失败即整体回滚：不消耗单号、不落任何数据、
  不改写来源询价单状态、不产生下游副作用；
- 提交 / 审核 / 创建版本 / 转报价都在**既有来源行锁 + 原子事务**内复核（锁序不变：
  询价单行锁 → 报价单行锁 → PI / 销售订单行锁）；
- 既有护栏优先级不变：**商业条款错误（ERP-470）与来源血缘 / 状态资格错误（ERP-400 / ERP-403）仍先于**
  主数据引用错误返回；**授权永远最先**（缺菜单 / 越界的账号即便提交失效引用也返回同一受控权限错误，非披露）；
- 只做有界只读投影查询：客户一次单行查询、业务员一次单行查询、商品**一次批量查询**
  （`ProductId` 去重集合），绝不逐行查库，也不产生任何被跟踪的变更。

## 4. 边界（不做的事）

- 不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，不新增迁移，不写主数据，不要求或授予主数据维护权限；
- 不做历史数据回填 / 自动修正：`GetById` / `GetPrint` / 列表 / 版本链 / 报表等读取路径原样返回持久化值，
  引用失效（客户 / 商品已删除 / 已停用、业务员已离职）的历史报价单照常可读、可打印、可查版本链，
  且**绝不**被自动改派到其它客户 / 商品 / 业务员；
- 不回填、不改写既有历史版本与来源单据，不触碰库存 / 财务 / 客户主数据；
- 来源询价单血缘仍由既有 `SalesDocumentSourceLineageRules` 治理，本护栏**绝不**把来源与本次客户 / 商品归属混淆；
- 报价单 → PI / 销售订单的**下游转换**引用护栏由 ERP-473（PI 实时主数据引用）承接：本任务只在报价单自身的
  「询价转报价」转换上接入。

## 5. 验收证据

| 类型 | 文件 | 覆盖 |
|---|---|---|
| 单元（新增） | `src/ERP.UnitTests/QuotationMasterReferenceTests.cs` | 客户 / 业务员 / 商品缺失 / 已删除 / 已停用 / 非正整数的受控错误、业务员资格（在职 + 是业务员）、历史悬空业务员 Id 保留、手工商品快照行（`null` 与「悬空 Id + 自带文本」）、商品一次有界批量查询契约、新增 / 修改零写入零占号、提交 / 审核锁内重查（草稿后被删除 / 停用）原子拒绝且状态不变、创建版本不复制失效引用、询价转报价受控拒绝且来源状态 / 单号不变、合法转换正向、历史读取 / 打印不被回填、授权前置非披露 |
| 单元（适配） | `src/ERP.UnitTests/QuotationRevisionTests.cs` / `QuotationMutationTests.cs` | 夹具按需补齐被引用的实时客户行（`EnsureLiveCustomer`），既有版本链与生命周期断言全部保持 |
| 集成（真实 SQL） | `src/ERP.IntegrationTests/QuotationMasterReferenceSqlServerTests.cs` | 专用 localdb 上的真实控制器：失效引用零写入零占号、合法手工报价单完整生命周期、提交 / 审核锁内重查、创建版本 / 询价转报价失败不改写来源、历史读取只读、商品批量查询计数、目标护栏 fail-closed |
| 回归 | `QuotationCrudRegressionTests` / `QuotationGovernanceTests` / `QuotationCommercialValidationTests` / `QuotationConversionScopeTests` / `SalesDocumentSourceLineageTests` 等 | 既有商业条款 / 来源血缘 / 授权 / 转换契约保持不变（手工快照行与可空引用照旧通过） |

## 6. 真实 SQL 夹具安全口径

- 目标必须精确为专用实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且
  `Integrated Security=true`；错误实例 / 错误库名 / 非集成安全的连接串一律在 `AssertDedicatedTarget`
  阶段于**任何数据库访问之前**被拒绝（含 fail-closed 单元覆盖）；
- 每次运行只创建一个**全新 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，
  绝不读取 `appsettings*.json` / `.env` / 生产凭据；只使用既有认证授权，不新增菜单 / 角色 / 用户授权；
- **构建完成不等于阶段验收**：真实 SQL 场景须在专用 localdb 上真实执行通过才算验收证据；
  真实环境不可用时按环境阻塞单独记录，不做降级或伪造。
