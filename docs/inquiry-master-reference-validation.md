# 询价单「实时主数据引用」护栏（ERP-475）

## 1. 背景与目标

询价单是「**询价单 Inquiry** → 报价单 Quotation → 形式发票 PI → 销售订单」业务链的**来源单据**，
此前的写入路径由多个护栏分层保护：

| 护栏 | 保护维度 |
|---|---|
| ERP-402 `InquiryAuthorizationRules` | 实时身份 / 既有「询价单」菜单 / 权威客户数据范围（转报价另需既有「报价单」菜单） |
| ERP-403 `SalesDocumentSourceLineageRules` | 「询价单 → 报价单」来源血缘、来源行锁与锁内权威重读 |
| ERP-474 `InquiryCommercialValidationRules` | 币种 / 汇率 / 有效期天数 / 逐行数量 · 单价 / 行金额等商业条款 |
| ERP-402 `InquiryMutationRules` | 确定性询价单来源行锁 + 原子事务 + 锁内权威重读 |

但这些护栏都**没有**核对调用方提交的必填 `Inquiry.CustomerId`、可空 `Inquiry.SalesmanId` 与明细
`InquiryDetail.ProductId` 是否仍指向**实时主数据**：失效引用（不存在 / 已删除 / 已停用 / 已离职 / 非业务员）
可以被写进询价单，并在提交 / 审核 / 转报价单时成为可生成报价单的实时运营来源。

ERP-475 在**唯一写入与运营承诺入口**上补齐「实时主数据引用复核」，并保持历史读取、手工商品快照行与既有授权语义不变。

## 2. 唯一权威规则

`InquiryMasterReferenceRules`（`src/ERP.Application/Services/InquiryMasterReferenceRules.cs`）：

| 项 | 要求 | 失败码 |
|---|---|---|
| **必填客户** `CustomerId`（非空契约） | 必须为正整数且解析到**存在、未删除、启用（`Status = 1`）**的客户；零 / 负 / 不存在 / 已删除 / 已停用一律 fail closed；**既有实时客户范围授权仍然决定性且先执行** | 非正 `1001`；不存在 / 已删除 `1002`；已停用 `1004` |
| **可选业务员** `SalesmanId`（可空） | 留空（`null`）跳过；填写时必须为正整数并解析到**存在、未删除、在职（`Status = 1`）、业务员（`IsSalesman = true`）**的员工（可选业务员资格语义）；不存在 / 已删除 / 已离职 / 非业务员一律拒绝 | 非正 `1001`；不存在 / 已删除 `1002`；已离职 / 非业务员 `1004` |
| **明细商品** `ProductId`（非空 `long`） | 每条**有效（未软删除）**明细必须为正整数；商品资料**一次有界批量查询**（`ProductId` 去重集合，绝不逐行查库）；解析到既有商品行时必须**未删除 / 启用（`Status = 1`）**；**无对应商品行时**：自带受界商品文本（名称 / 规格 / 单位任一非空）的行按**手工商品快照行**保留（不回填、不改派），纯目录引用按**缺失引用**拒绝 | 非正 `1001`；已删除 / 无自带文本的缺失引用 `1002`；已停用 `1004` |

要点：

- **必填客户语义**：`Inquiry.CustomerId` 是既有非空存储契约，因此零 / 负 / 悬空一律拒绝（不接受空客户草稿）；
- **可选业务员语义**：留空仍然合法；一旦填写必须解析到在职且为业务员的员工，**绝不臆造业务员、绝不自动改派**；
- **手工商品快照行仍是第一等公民**：询价明细以自带商品文本为准（界面商品 Id 为手工录入字段），
  悬空商品 Id + 自带受界文本的行照常保存 / 提交 / 审核 / 转报价，且**绝不回填 / 绝不自动改派**；
  真正没有任何自带文本的纯目录引用（悬空且无文本）才是缺失引用，受控拒绝；
- **绝不臆造主数据**：本护栏只做纯判定与有界只读投影查询（客户一次单行、业务员一次单行、
  商品一次批量），不写客户 / 员工 / 商品主数据，也绝不要求或授予任何主数据维护权限。

## 3. 接入点与时序

| 路径 | 接入点 | 时序（相对既有步骤） |
|---|---|---|
| 新增 `POST /api/inquiries` | `InquiryController.Create` | 实时授权 + 拟议客户范围 → **商业条款校验 / 整理** → **实时主数据引用复核** → 单号预约 → 字段 / 明细写入 |
| 修改 `PUT /api/inquiries/{id}` | `RunLockedMutationAsync` | 询价单来源行锁 + 锁内权威重读 → 拟议客户范围 → 下游冻结 → 状态资格 → **实时主数据引用复核** → 字段 / 明细写入 |
| 提交 `POST /{id}/submit` | `RunLockedMutationAsync` | 询价单行锁 + 锁内权威重读 → 下游 / 状态资格 → 已持久化商业条款校验 → **实时主数据引用复核** → 置「已提交」 |
| 审核 `POST /{id}/approve` | `RunLockedMutationAsync` | 同上 → **实时主数据引用复核** → 置「已审核」 |
| 带入预填 `GET /{id}/quotation-prefill` | `InquiryQuotationConversion.BuildDraftAsync` | 询价单来源行锁内重读 → 转换资格 → 商业条款校验 → **来源实时主数据引用复核** → 只读草稿（不落库 / 不占号 / 不改来源状态） |
| 转报价单 `POST /{id}/to-quotation` | 同上 | 询价单来源行锁内重读 → 转换资格 → 商业条款校验 → **来源实时主数据引用复核** → 报价单号预约 → 落库 + 来源置「已完成」 |

- 引用复核**先于单号预约与任何表头 / 明细赋值**；失败即整体回滚：不消耗询价单号 / 报价单号、
  不落任何数据、不改写来源状态、不产生下游副作用；
- 提交 / 审核 / 转报价都在**既有来源行锁 + 原子事务**内复核（锁序不变：
  询价单行锁 → 报价单行锁 → PI / 销售订单行锁）；
- 既有护栏优先级不变：**商业条款错误（ERP-474）与来源血缘 / 状态资格错误（ERP-402 / ERP-403）仍先于**
  主数据引用错误返回；**授权永远最先**（缺菜单 / 越界的账号即便提交失效引用也返回同一受控权限错误，非披露）。

## 4. 边界（不做的事）

- 不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，不新增迁移，不写主数据，不要求或授予主数据维护权限；
- 不做历史数据回填 / 自动修正：`GetById` / 列表 / 导出 / 报价响应时间工作台等读取路径原样返回持久化值，
  引用失效（客户 / 商品已删除 / 已停用、业务员已离职）的历史询价单照常可读，且**绝不**被自动改派到其它客户 / 商品 / 业务员；
- 不触碰库存 / 财务 / 客户主数据，不改写报价单 / PI / 销售订单；
- 既有「询价单 → 报价单」血缘仍由 `SalesDocumentSourceLineageRules` 治理，本护栏**绝不**把来源与本次客户 / 商品归属混淆；
- 报价单侧的实时主数据引用由 ERP-472（`QuotationMasterReferenceRules`）承接；本任务只在**询价单来源**上接入。

## 5. 验收证据

| 类型 | 文件 | 覆盖 |
|---|---|---|
| 单元（新增） | `src/ERP.UnitTests/InquiryMasterReferenceTests.cs` | 必填客户 / 可选业务员 / 明细商品的非正整数、不存在 / 已删除、已停用 / 已离职 / 非业务员受控错误；手工商品快照行（悬空 Id + 名称 / 规格 / 单位文本）保留；无自带文本的缺失引用拒绝；软删除行不参与；商品一次有界批量查询契约（源码 + 行为）；新增 / 修改零写入零占号；草稿后客户被删除 / 商品被停用 → 提交 / 审核原子拒绝且状态不变；来源失效 → 转报价单 / 带入预填在占号与改写来源之前拒绝；合法来源正向转换并保留来源文本；当前客户范围被撤销仍 fail closed；缺菜单账号非披露拒绝；历史读取只读不回填 |
| 单元（扩展） | `src/ERP.UnitTests/InquiryQuotationConversionTests.cs` | 来源客户被删除 → 转换 / 带入预填不落库、不耗报价单号、来源状态不变；来源明细为手工快照行时转换后来源与目标文本原样保留 |
| 单元（适配） | `src/ERP.UnitTests/InquiryControllerTests.cs` / `InquiryMutationTests.cs` | 夹具按需补齐被引用的实时主数据行（`SeedLiveCustomer` / `SeedLiveEmployee` / `SeedLiveProduct` / `SeedProduct`），既有控制器 / 生命周期 / 范围 / 转换断言全部保持 |
| 单元（适配） | `src/ERP.UnitTests/InquiryCommercialValidationTests.cs` | ERP-474 的两个「新增」用例（伪造单号覆盖、空草稿保存）原本以 `CustomerId = 1` 作为占位填充值；ERP-475 后**必填客户必须解析到实时主数据行**，故这两个用例的夹具改为播种既有合法客户（`SeedCustomer`）——用例断言本身（服务端权威覆盖 / 空草稿保存）保持不变 |
| 集成（真实 SQL） | `src/ERP.IntegrationTests/InquiryMasterReferenceSqlServerTests.cs` | 专用 localdb 上的真实控制器：失效引用零写入零占号、合法主数据完整落库、手工快照行保存 / 提交 / 审核、草稿后客户删除 / 商品停用 → 提交 / 审核拒绝且状态不变、来源失效 → 转报价失败不改写来源与报价单号、合法转换正向、历史读取只读、商品批量查询计数、目标护栏 fail-closed |
| 回归 | `QuotationMasterReferenceTests` / `SalesDocumentSourceLineageTests` / `InquiryResponseWorkspaceTests` / `InquiryCommercialValidationTests` 等 | 既有商业条款 / 来源血缘 / 授权 / 转换契约保持不变（手工快照行与可空业务员照旧通过） |

## 6. 真实 SQL 夹具安全口径

- 目标必须精确为专用实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且
  `Integrated Security=true`；错误实例 / 错误库名 / 非集成安全的连接串一律在 `AssertDedicatedTarget`
  阶段于**任何数据库访问之前**被拒绝（含 fail-closed 单元覆盖）；
- 每次运行只创建一个**全新 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，
  绝不读取 `appsettings*.json` / `.env` / 生产凭据；只使用既有认证授权，不新增菜单 / 角色 / 用户授权；
- **构建完成不等于阶段验收**：真实 SQL 场景须在专用 localdb 上真实执行通过才算验收证据；
  真实环境不可用时按环境阻塞单独记录，不做降级或伪造。

## 7. 本次交付的验收执行记录（如实记录，不宣称整阶段验收）

| 步骤 | 命令 | 结果 |
|---|---|---|
| 安全 Release 构建（分析器 + 警告即错误） | `dotnet build NEWERP.sln -c Release --no-restore /p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true` | 成功，0 警告 / 0 错误 |
| 全量单元回归 | `dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build` | 通过：7675 / 7675（失败 0） |
| 本任务真实 SQL（专用 localdb） | `dotnet test src/ERP.IntegrationTests/ERP.IntegrationTests.csproj -c Release --no-build --filter FullyQualifiedName~InquiryMasterReference` | 通过：13 / 13（失败 0）；目标为 `(localdb)\NEWERP_AutoAcceptance` + 全新 GUID `NEWERP_AUTOTEST_*` 库、`Integrated Security=true`，未 drop / reset 任何库 |
| 浏览器验收 | 本任务 `browser_acceptance.required = false` | 未执行（无需 UI / 样式验收） |

说明：真实 SQL 证据限于**本任务新增 / 相关**的询价单主数据引用场景；
本阶段其它既有集成 / 浏览器套件未在本任务内重跑，因此**不宣称整阶段验收通过**。
