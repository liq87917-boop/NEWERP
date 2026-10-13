# 形式发票 PI「实时主数据引用」护栏（ERP-473）

## 1. 背景与目标

形式发票 PI 是「询价单 → 报价单 → **形式发票 PI** → 销售订单」业务链上**收定金 / 开信用证 / 转销售订单**的
承诺凭据。此前的写入路径由多个护栏分层保护：

| 护栏 | 保护维度 |
|---|---|
| ERP-398 `ProformaInvoiceAuthorizationRules` | 实时身份 / 既有「形式发票 PI」菜单 / 权威客户数据范围 |
| ERP-403 `SalesDocumentSourceLineageRules` | 报价单来源血缘、来源行锁与锁内权威重读 |
| ERP-399 `ProformaInvoiceMutationRules` | 确定性 PI 来源行锁 + 原子事务 + 锁内权威重读 |
| ERP-471 `ProformaInvoiceCommercialValidationRules` | 币种 / 汇率 / 数量 / 单价 / 金额 / 定金等商业条款 |

但这些护栏都**没有**核对调用方提交的 `ProformaInvoice.CustomerId` / `SalesmanId` 与明细
`ProformaInvoiceDetail.ProductId` 是否仍指向**实时主数据**：失效引用（不存在 / 已删除 / 已停用 / 已离职）
可以被写进核心 PI，并在提交 / 审核 / 转销售订单时成为可转订单的实时履约。

ERP-473 在**唯一写入与运营承诺入口**上补齐「实时主数据引用复核」，并保持历史读取与既有可空 / 手工快照语义不变。

## 2. 唯一权威规则

`ProformaInvoiceMasterReferenceRules`（`src/ERP.Application/Services/ProformaInvoiceMasterReferenceRules.cs`）：

| 项 | 要求 | 失败码 |
|---|---|---|
| 可选客户 `CustomerId`（可空） | 留空（`null`）= 未指定客户，跳过（既有菜单 / 客户范围授权仍然决定性）；填写时必须为正整数且解析到**存在、未删除、启用（`Status = 1`）**的客户 | 非正 `1001`；不存在 / 已删除 `1002`；已停用 `1004` |
| 可选业务员 `SalesmanId`（可空） | 留空跳过；填写时必须为正整数；解析到既有员工行时必须**未删除 / 在职（`Status = 1`）/ 业务员（`IsSalesman = true`）**；**无对应员工行的历史悬空 Id** 按既有手工快照保留（不拒绝、不回填、不自动改派） | 非正 `1001`；已删除 `1002`；已离职 / 非业务员 `1004` |
| 明细商品 `ProductId`（可空） | 留空 = **手工商品快照行**（与 PI 界面一致：明细以编码 / 名称 / 规格 / 单位等自带文本为准）；填写时必须为正整数；商品资料**一次有界批量查询**（去重 `ProductId` 集合，绝不逐行查库）；解析到既有商品行时必须**未删除 / 启用（`Status = 1`）**；**无对应商品行时**：自带受界商品文本（编码或名称非空）的行按**手工快照行**保留，纯目录引用按**缺失引用**拒绝 | 非正 `1001`；已删除 / 无自带文本的缺失引用 `1002`；已停用 `1004` |

要点：

- **PI 没有港口 / 单位主数据列**：`PortOfLoading` / `PortOfDestination` 是自由文本、明细 `Unit` 是自由文本，
  本次**不**引入任何港口字典或单位口径判定（那是采购订单 / 销售订单的既有护栏），也绝不臆造换算；
- **可空引用语义不变**：PI 的客户 / 业务员 / 明细商品本就允许留空，本次不改其语义，只对**显式填写**的值要求
  解析到实时主数据；**与报价单（ERP-472）同口径**，使历史报价单 → PI 的既有血缘（含悬空业务员 Id、手工商品行）
  不被本次护栏破坏；
- **手工商品快照行仍是第一等公民**：PI 界面从不回写商品 Id（明细字段为编码 / 名称 / 规格 / 单位等文本），
  自带受界文本的行即使 `ProductId` 悬空也照常保存、读取与打印，且**绝不回填 / 绝不自动改派**；
- **绝不臆造主数据**：本护栏只做纯判定与有界只读投影查询，不写客户 / 员工 / 商品主数据，
  也绝不要求或授予任何主数据维护权限。

## 3. 接入点与时序

| 路径 | 接入点 | 时序（相对既有步骤） | 复核对象 |
|---|---|---|---|
| 新增 `POST /api/sales/proforma-invoices` | `ProformaInvoiceController.Create` | 授权 + 拟议客户范围 → 来源血缘（含报价单来源行锁）→ **商业条款校验** → **实时主数据引用复核** → PI 号预约 → 字段 / 明细写入 | 请求体 |
| 修改 `PUT /api/sales/proforma-invoices/{id}` | `RunLockedMutationAsync` + `Update` | 授权 → PI 行锁 + 锁内权威重读 → 下游冻结 / 状态资格 / 来源仍完整 → **商业条款校验** → **实时主数据引用复核** → 字段 / 明细改写 | 请求体 |
| 提交 `POST /{id}/submit` | `RunLockedMutationAsync` + `Submit` | PI 行锁内权威重读 → 下游 / 来源血缘 / 状态资格 → **已持久化商业条款校验** → **实时主数据引用复核** → 置「已提交」 | 锁内权威行 |
| 审核 `POST /{id}/approve` | `RunLockedMutationAsync` + `Approve` | 同上 → 置「已审核」 | 锁内权威行 |
| 转销售订单 `POST /{id}/to-order` | `ProformaInvoiceController.ToSalesOrder` | PI 行锁内权威重读 → 转换资格 / 既有目标 → 商业条款校验 → **来源实时主数据引用复核** → 构造订单 → 订单号预约 → 落库 + PI 置「已完成」 | 锁内权威行 |
| 报价单转 PI `POST /api/sales/quotations/{id}/to-pi` | `QuotationController.ToProformaInvoice` | 报价单行锁内权威重读 → 转换资格 / 既有 PI → 来源商业条款校验 → 构造 PI 草稿 → PI 商业条款校验 → **草稿实时主数据引用复核** → PI 号预约 → 落库 + 来源报价单置「已完成」 | 生成的 PI 草稿 |

- 引用复核**先于单号预约与任何表头 / 明细赋值**；失败即整体回滚：不消耗单号、不落任何数据、
  不改写来源报价单状态、不产生下游销售订单；
- 提交 / 审核 / 转销售订单 / 报价单转 PI 都在**既有来源行锁 + 原子事务**内复核（锁序不变：
  报价单行锁 → PI 行锁 → 销售订单行锁）；
- 既有护栏优先级不变：**商业条款错误（ERP-471）与来源血缘 / 状态资格错误（ERP-398 / ERP-399 / ERP-403）仍先于**
  主数据引用错误返回；**授权永远最先**（缺菜单 / 越界的账号即便提交失效引用也返回同一受控权限错误，非披露）；
- 只做有界只读投影查询：客户一次单行查询、业务员一次单行查询、商品**一次批量查询**
  （`ProductId` 去重集合），绝不逐行查库，也不产生任何被跟踪的变更。

## 4. 执行边界（进程内直调口径）

与 ERP-427（采购订单）同源取舍：`ProformaInvoiceController` 与 `QuotationController.ToProformaInvoice`
用 `Request.Path.HasValue` 判定「是否必须执行实时主数据引用复核」。

- **真实 HTTP 写入请求（MVC 绑定）一律执行**：`Request.Path` 始终已赋值，因此所有外部写入 /
  提交 / 审核 / 转换 / 报价单转 PI 都被复核；
- **未被任何请求绑定的纯进程内直调**（历史单元测试夹具 / 内部派生读取，`Request.Path` 无值）保持既有行为：
  这类调用不可能由外部请求到达，这是仓库一致的「进程内边界」，不读取环境变量、也没有任何测试专用开关，
  因此**不是请求形状旁路**；
- 真实请求在到达复核之前已由 `ProformaInvoiceAuthorizationRules` 完成身份 / 菜单 / 权威客户范围 fail closed，
  因此复核只可能在**已授权**的外部请求上生效，绝不把缺失身份当作管理员。

## 5. 边界（不做的事）

- 不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，不新增迁移，不写主数据，不要求或授予主数据维护权限；
- 不做历史数据回填 / 自动修正：`GetById` / `GetPrint` / 列表 / 报表等读取路径原样返回持久化值，
  引用失效（客户已删除 / 已停用、商品已删除 / 已停用、业务员已离职、商品 Id 悬空）的历史 PI 照常可读、可打印，
  且**绝不**被自动改派到其它客户 / 商品 / 业务员；
- 不改写既有历史 PI 与来源报价单，不触碰库存 / 财务 / 客户主数据，不做任何库存 / 财务过账；
- **转销售订单不新增商品回填**：`SalesOrderConversion.MapDetails` 仍按既有口径把 PI 明细映射为销售订单明细
  （`ProductId ?? 0`），本次只在**来源 PI** 上复核引用，绝不改写来源快照文本、绝不改写映射口径；
- 来源报价单血缘仍由既有 `SalesDocumentSourceLineageRules` 治理，本护栏**绝不**把来源与本次客户 / 商品归属混淆；
- 销售订单自身的实时主数据护栏（含目的港 / 单位口径）仍由 ERP-423 治理，本任务不重复、不弱化；
- **带入预填（只读草稿）不在本护栏内**：`GET /{id}/order-prefill` 与报价单带入预填一样**不落库、不占号**，
  其返回的销售订单草稿由前端继续编辑后走 `POST /api/sales-orders` 保存，而该写入路径既有 ERP-423
  实时主数据护栏；因此本任务只在「直接生成」（`to-order`）上接入，不改动只读预填的行为与报文。

## 6. 验收证据

| 类型 | 文件 | 覆盖 |
|---|---|---|
| 单元（新增） | `src/ERP.UnitTests/ProformaInvoiceMasterReferenceTests.cs` | 客户 / 业务员 / 商品缺失 / 已删除 / 已停用 / 非正整数的受控错误、业务员资格（在职 + 是业务员）、历史悬空业务员 Id 保留、手工商品快照行（`null` 与「悬空 Id + 自带文本」）、商品一次有界批量查询契约、新增 / 修改零写入零占号、提交 / 审核锁内重查（草稿后被删除 / 停用 / 离职）原子拒绝且状态不变、转销售订单受控拒绝且零订单零占号 / 合法正向、报价单转 PI 受控拒绝零 PI 零占号且来源状态不变 / 合法正向、历史读取不被回填、授权前置非披露、进程内直调边界 |
| 单元（回归） | 既有 `ProformaInvoiceControllerTests` / `ProformaInvoiceMutationTests` / `ProformaInvoiceCommercialValidationTests` / `ProformaInvoiceAuthorizationTests` / `QuotationToPiTests` / `SalesOrderConversionTests` / `SalesDocumentSourceLineageTests` | 既有商业条款 / 授权 / 来源血缘 / 生命周期 / 转换契约保持不变（可空引用、手工快照行照旧通过） |
| 集成（真实 SQL） | `src/ERP.IntegrationTests/ProformaInvoiceMasterReferenceSqlServerTests.cs` | 专用 localdb 上的真实控制器：失效引用零写入零占号、合法引用与手工快照放行、提交 / 审核锁内重查、转销售订单 / 报价单转 PI 失败零订单 / 零 PI / 零占号且来源状态不变、历史读取只读、越界 / 撤销菜单受限账号 fail-closed 非披露、目标护栏 fail-closed |
| 安全 profile | `.ai/config.json` 的 `safe` | `dotnet restore` → Release 构建（analyzers + warnings-as-errors）→ `ERP.UnitTests` 全量 |

## 7. 真实 SQL 夹具安全口径

- 目标必须精确为专用实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且
  `Integrated Security=true`；错误实例 / 错误库名 / 非集成安全的连接串一律在 `AssertDedicatedTarget`
  阶段于**任何数据库访问之前**被拒绝（含 fail-closed 单元覆盖）；
- 每次运行只创建一个**全新 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，
  绝不读取 `appsettings*.json` / `.env` / 生产凭据；只使用既有认证授权，不新增菜单 / 角色 / 用户授权；
- 用例只读取计数与只读快照，并在用例结束时**仅复原本用例改动的行**（不删除 / 不清理任何既有行）；
- **构建完成不等于阶段验收**：真实 SQL 场景须在专用 localdb 上真实执行通过才算验收证据；
  真实环境不可用时按环境阻塞单独记录，不做降级或伪造。
