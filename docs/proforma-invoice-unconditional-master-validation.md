# 形式发票 PI 与「报价单 → PI」实时主数据引用复核的调用形状无关化（ERP-490）

## 1. 背景与目标

ERP-473 为形式发票 PI 补齐了「实时主数据引用」护栏（`ProformaInvoiceMasterReferenceRules`），
但当时把「是否执行复核」挂在一个**可执行的方法级豁免**上：

```csharp
// src/ERP.Api/Controllers/ProformaInvoiceController.cs（ERP-473，已删除）
private bool RequiresLiveMasterValidation()
    => ControllerContext?.HttpContext?.Request.Path.HasValue == true;

// src/ERP.Api/Controllers/QuotationController.cs（ERP-473，已删除）
private bool RequiresLivePiMasterValidation()
    => ControllerContext?.HttpContext?.Request.Path.HasValue == true;
```

这两个判定只依赖 `Request.Path` 是否赋值：只要一个**已授权**的调用不经过 MVC 请求管线（空路径 / 进程内派生
调用 / 内部再入），新增 / 修改 / 提交 / 审核 / 转销售订单 / 报价单转 PI 就会**跳过**实时主数据复核，
从而把已经删除 / 停用 / 不存在的商品、客户、业务员写进核心 PI，并让报价单 → PI 复制这些失效引用，
最终承诺成「可转销售订单」的实时履约。

ERP-490 删除这两个豁免判定，使既有的 `ProformaInvoiceMasterReferenceRules` 在**所有既定写入 / 承诺 / 转换
入口**上都**无条件**执行，**与请求路径 / 请求上下文 / 身份可解析性完全无关**；同时保留既有身份、菜单、权威客户
范围、来源行锁、原子事务、受控错误与审计，不新增任何主数据维护权限。

## 2. 唯一权威规则（保持不变）

护栏仍然只由 `ProformaInvoiceMasterReferenceRules`（`src/ERP.Application/Services/ProformaInvoiceMasterReferenceRules.cs`）
实现，本次**不新增第二套算法**：

| 项 | 要求 | 失败码 |
|---|---|---|
| 可选客户 `CustomerId`（可空） | 留空 = 未指定客户（既有菜单 / 客户范围授权仍决定性）；填写必须为正整数且解析到**存在、未删除、启用（`Status = 1`）**的客户 | 非正 `1001`；不存在 / 已删除 `1002`；已停用 `1004` |
| 可选业务员 `SalesmanId`（可空） | 留空跳过；填写必须为正整数；解析到既有员工行时必须**未删除 / 在职 / 业务员**；**无对应员工行的历史悬空 Id** 保留为既有手工快照 | 非正 `1001`；已删除 `1002`；已离职 / 非业务员 `1004` |
| 明细商品 `ProductId`（可空） | 留空 = **手工商品快照行**（受界商品文本为准）；填写必须为正整数，商品资料**一次有界批量查询**（去重集合，绝不逐行查库）；解析到既有商品行时不得已删除 / 已停用；无对应商品行时自带受界文本的手工快照行保留、纯目录引用按缺失引用拒绝 | 非正 `1001`；已删除 / 纯目录缺失引用 `1002`；已停用 `1004` |

**可空 / 手工草稿语义不变**：显式引用留空一律沿用既有语义（不要求主数据链接、不臆造单位 / 名称、不回填 / 不改派）；
本次只让**显式填写的引用**在任何调用形状下都被实时复核。

## 3. 无条件接入点与时序（`Request.Path` 不再参与判定）

| 路径 | 接入点 | 时序 |
|---|---|---|
| 新增 `POST /api/sales/proforma-invoices` | `ProformaInvoiceController.Create` | 授权 + 拟议客户范围 → 来源血缘（含报价单来源行锁）→ 商业条款校验 → **实时主数据引用复核** → PI 号预约 → 写入 |
| 修改 `PUT /api/sales/proforma-invoices/{id}` | `RunLockedMutationAsync` + `Update` | PI 行锁 + 锁内权威重读 → 下游冻结 / 状态资格 → 商业条款校验 → **实时主数据引用复核** → 改写 |
| 提交 / 审核 `POST /{id}/submit` · `/approve` | `RunLockedMutationAsync` + `Submit` / `Approve` | PI 来源行锁内重读 → 状态资格 → 已持久化商业条款校验 → **实时主数据引用复核** → 置状态 |
| 转销售订单 `POST /{id}/to-order` | `ProformaInvoiceController.ToSalesOrder` | PI 来源行锁内重读 → 转换资格 → 商业条款校验 → **`SalesOrderConversion.EnsureSourceMasterReferencesAsync`（无条件）** → 构造订单 → 订单号预约 |
| 报价单转 PI `POST /api/sales/quotations/{id}/to-pi` | `QuotationController.ToProformaInvoice` | 报价单行锁内重读 → 转换资格 → 来源商业条款校验 → 构造 PI 草稿 → PI 商业条款校验 → **草稿实时主数据引用复核（无条件）** → PI 号预约 |

- 复核**先于单号预约与任何表头 / 明细 / 订单 / PI 赋值**；失败即整体回滚（零写入、零占号、来源 / 状态一行不改）；
- 提交 / 审核 / 转销售订单 / 报价单转 PI 仍在**既有来源行锁 + 原子事务**内复核（锁序不变：报价单 → PI → 销售订单）；
- 授权永远最先且与调用形状无关：缺失 / 零 / 未知身份一律未认证，缺菜单 / 越界返回同一受控非披露错误；
- 只做**有界只读投影查询**：客户 / 业务员各一次单行查询、商品**一次批量查询**，不落库、不写主数据。

## 4. 与历史读取 / 既有护栏的关系

- 历史读取（列表 / 详情 / 打印 / 报表）完全只读：引用失效的历史 PI 照常可读、可打印，**绝不**被回填或静默改派；
- 既有护栏优先级不变：**授权 → 来源血缘 / 状态资格 → 商业条款 → 实时主数据引用**，
  错误差异 / 计数 / 部分行都不透露不可访问单据的存在性（非披露）；
- **不新增 / 不修改任何菜单 / 角色 / 用户授权**，不新增表 / 列 / 索引 / 迁移，也不要求或授予任何主数据维护权限；
- 来源报价单血缘仍由既有 `SalesDocumentSourceLineageRules` 治理；销售订单自身的主数据护栏仍由 ERP-423 治理。

## 5. 既有夹具适配（真实启用主数据，绝无请求形状旁路）

删除豁免后，历史单元 / 集成夹具中「占位 `CustomerId = 1` 但没有对应客户主数据行」的用例会按新口径被拒绝，
因此按「真实启用身份 + 既有菜单 / 数据范围 + 实时主数据行」的口径**适配既有夹具**（不断言被删除的豁免行为）：

| 文件 | 适配 |
|---|---|
| `src/ERP.UnitTests/ProformaInvoiceControllerTests.cs` | 控制器工厂播种既有**启用**客户（`Id = 1`，与占位口径一致，幂等） |
| `src/ERP.UnitTests/ProformaInvoiceMutationTests.cs` | 同上 |
| `src/ERP.UnitTests/ProformaInvoiceCommercialValidationTests.cs` | 同上 |
| `src/ERP.UnitTests/QuotationToPiTests.cs` | `SeedQuotation` 同步播种既有启用客户并以其 Id 落库；两个原先自建客户行的用例改为在**同一**实时客户行上补齐带入字段 / 越界定金比例（语义不变） |
| `src/ERP.UnitTests/ProformaInvoiceMasterReferenceTests.cs` | 「进程内直调无路径保持既有行为」用例改为**同样受控拒绝且零写入零占号**，并新增「进程内直调 + 真实启用身份 + 实时主数据 → 照常放行」的对照用例 |

适配只改动夹具对**主数据行**的播种与断言，**不改变**用例本身要验证的业务语义（服务端权威覆盖、空草稿保存、
汇总 / 定金口径、来源血缘、生命周期等），也绝不引入请求路径 / 环境变量 / 测试专用开关旁路。

## 6. 验收证据

| 类型 | 文件 | 覆盖 |
|---|---|---|
| 单元（新增） | `src/ERP.UnitTests/ProformaInvoiceUnconditionalMasterReferenceTests.cs` | 规则：手工快照草稿放行且不臆造主数据 / 显式失效拒绝；调用形状无关：同一载荷 + 同一身份下「空 `Request.Path`」与「已赋值 `Request.Path`」拒绝结论与零变更**完全一致**；新增 / 修改 / 提交 / 审核 / 转销售订单 / 报价单转 PI 的空路径正向与失效负向（零写入 / 零占号 / 状态与来源不变）；接线断言控制器不再含请求路径豁免 |
| 单元（适配） | `ProformaInvoiceControllerTests` / `ProformaInvoiceMutationTests` / `ProformaInvoiceCommercialValidationTests` / `QuotationToPiTests` / `ProformaInvoiceMasterReferenceTests` | 既有业务契约保持不变；进程内直调与真实请求同一口径 |
| 集成（真实 SQL） | `src/ERP.IntegrationTests/ProformaInvoiceUnconditionalMasterReferenceSqlServerTests.cs` | 专用 localdb 上的真实控制器：空路径与真实路径**同结论**且零写入零占号；空路径合法客户 + 手工快照行放行；提交 / 审核锁内复核失效引用（客户已删除 / 商品已停用）；转销售订单 / 报价单转 PI 零订单 / 零 PI / 零占号且来源状态不变；合法来源草稿生成 |
| 安全 profile | `.ai/config.json` 的 `safe` | `dotnet restore` → Release 构建（analyzers + warnings-as-errors）→ `ERP.UnitTests` 全量 |

## 7. 真实 SQL 夹具安全口径

- 目标必须精确为专用实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且 `Integrated Security=true`；
  错误实例 / 错误库名 / 非集成安全的连接串一律在**任何数据库访问之前**由 `AssertDedicatedTarget` 拒绝
  （含 fail-closed 单元覆盖，见 `ProformaInvoiceMasterReferenceTargetGuardTests`）；
- 每次运行只创建一个**全新 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings*.json` /
  `.env` / 生产凭据；只使用既有认证授权，不新增菜单 / 角色 / 用户授权；
- 用例只读取计数与只读快照（并断言全部字轨序列之和不变以判定「零占号」），不删除 / 不清理任何既有行；
- **构建完成不等于阶段验收**：真实 SQL 场景须在专用 localdb 上真实执行通过才算验收证据；
  真实环境不可用时按环境阻塞单独记录，不做降级或伪造。
