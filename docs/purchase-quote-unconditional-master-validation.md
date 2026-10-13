# 供应商比价「实时主数据引用」复核的调用形状无关化（ERP-491）

## 1. 背景与目标

ERP-476 为供应商比价（`PurchaseQuote`，`api/purchase/quotes`）补齐了「实时主数据引用」护栏
（`PurchaseQuoteMasterReferenceRules` + `PurchaseQuoteConversion.EnsureSourceMasterReferencesAsync`），
但当时把「是否执行复核」挂在一个**可执行的方法级豁免**上：

```csharp
// src/ERP.Api/Controllers/PurchaseQuoteController.cs（ERP-476，已删除）
private bool RequiresLiveMasterValidation()
    => ControllerContext?.HttpContext?.Request.Path.HasValue == true;

private async Task EnsureLiveMasterReferencesAsync(PurchaseQuote entity, bool commitment)
{
    if (entity is null || !RequiresLiveMasterValidation()) return;     // ← 空路径 / 无请求管线即跳过
    ...
}
```

同一判定还被复制到三条采购承诺入口：带入预填（`OrderPrefill`）、单行转采购订单（`ToPurchaseOrder`）、
批次转采购订单（`BatchToOrder`），只要调用方处于「已授权但 `Request.Path` 未赋值」的形状
（内部派生调用 / 进程内再入 / 空路径夹具），复核就被整体跳过：

- 失效的归属客户（`CustomerId` 已删除 / 已停用 / 悬空）可被写进比价行并**承诺**成采购订单的归客户；
- 缺失 / 零商品引用（`ProductId = 0`）会被**静默映射**为采购明细的零占位商品引用；
- 已删除 / 已停用的供应商 / 商品可绕过承诺级复核。

ERP-491 删除该方法级豁免，使既有 `PurchaseQuoteMasterReferenceRules` 在**所有既定写入 / 选择 / 承诺
入口**上**无条件**执行，**与请求路径 / 请求上下文 / 身份可解析性完全无关**；同时保留既有身份、菜单、
权威客户范围、来源行锁、原子事务、受控错误与审计，**不新增任何主数据维护权限**。

## 2. 唯一权威规则（保持不变）

护栏仍然只由 `PurchaseQuoteMasterReferenceRules`（`src/ERP.Application/Services/PurchaseQuoteMasterReferenceRules.cs`）
与 `PurchaseQuoteConversion.EnsureSourceMasterReferencesAsync`（唯一接线点）实现，本次**不新增第二套算法**：

| 场景 | 要求 | 失败码 |
|---|---|---|
| 草稿写入（新增 / 修改）· 归属客户 `CustomerId`（可空） | 留空 = 未指定客户（代理采购），既有菜单 / 客户范围授权仍最先且决定性；填写必须为正整数并解析到**存在、未删除、启用（`Status = 1`）**客户 | 非正 `1001`；不存在 / 已删除 `1002`；已停用 `1004` |
| 草稿写入 · 供应商 `SupplierId`（可空） | 留空 = 未维护供应商的比价草稿；填写必须为正整数；解析到既有档案时不得已删除 / 已停用；悬空弱引用保留既有口径 | 非正 `1001`；已删除 `1002`；已停用 `1004` |
| 草稿写入 · 商品 `ProductId`（可空） | 留空 = 手工 / 未引用档案商品；填写必须为正整数；解析到既有档案时不得已删除 / 已停用；悬空弱引用保留既有口径 | 非正 `1001`；已删除 `1002`；已停用 `1004` |
| 采购承诺（选中 / 带入预填 / 单行 / 批次转采购订单）· 供应商 / 商品 | 必须**显式存在且为正整数**，绝不把缺失 / `ProductId = 0` 静默当作实时采购引用；解析到既有档案时必须未删除 / 启用 | `1001` / `1002` / `1004` |
| 采购承诺 · 归属客户 | 填写时必须解析到实时客户（悬空 / 已删除 / 已停用一律拒绝） | 非正 `1001`；不存在 / 已删除 `1002`；已停用 `1004` |

**可空 / 手工草稿语义原样保留**：显式引用留空一律沿用既有语义（不要求主数据链接、不臆造名称 / 单位、
不回填 / 不改派）；本次只让**显式填写的引用**与**采购承诺**在任何调用形状下都被实时复核。

## 3. 无条件接入点与时序（`Request.Path` 不再参与判定）

| 路径 | 接入点 | 时序 |
|---|---|---|
| 新增 `POST /api/purchase/quotes` | `PurchaseQuoteController.Create` | 授权 + 拟议客户范围 → 事务 → 防伪转换证据 / 草稿校验 → **实时主数据引用复核（含选择承诺）** → 规范化 → 写入 |
| 修改 `PUT /api/purchase/quotes/{id}` | `PurchaseQuoteController.Update` | 授权 → 比价行锁 + 原子事务 → 锁内权威重读 → 下游冻结 / 决定资格 → 草稿校验 → **实时主数据引用复核（含选择承诺）** → 改写字段 |
| 带入预填 `GET /{id}/order-prefill` | `OrderPrefill` | 授权（比价 + 目的地）→ 来源范围复核 → **`EnsureSourceMasterReferencesAsync`（无条件）** → 构造未落库草稿 |
| 单行转采购订单 `POST /{id}/to-order` | `ToPurchaseOrder` | 来源行锁 + 原子事务 → 锁内重授权 → 资格复核 → **`EnsureSourceMasterReferencesAsync`（无条件）** → 映射草稿 → 批准供应商一致性 → 采购单号预约 → 写入 |
| 批次转采购订单 `POST /batch-to-order` | `BatchToOrder` | 批次成员锁定 + 原子事务 → 计划 / 资格复核 → **对**本批将写入的每一行**无条件复核（任一行不合格即整批拒绝）** → 采购单号预约 → 写入 |

- 复核**先于采购单号预约与任何表头 / 明细 / 来源赋值**；失败即整体回滚（零写入、零占号、来源 / 状态一行不改）；
- 承诺入口仍在**既有来源行锁 + 原子事务**内复核（锁序不变：归属销售订单 → 来源比价行），主数据读取为**有界批量**：
  承诺复核按去重的供应商 / 商品 / 客户 Id 集合**各一次**取回（最多 3 次查询，绝不逐行查库），
  任一行不合格即整批受控拒绝；
- 授权永远最先且与调用形状无关：缺失 / 零 / 未知 / 已删除身份一律未认证，缺菜单 / 越界返回同一受控非披露错误；
- 只做**纯判定与有界只读投影查询**：不落库、不改写任何主数据或比价行字段，也不消耗单据号。

## 4. 与历史读取 / 既有护栏的关系

- 历史读取（列表 / 详情 / 打印 / 报价历史 / 价格差异 / 转化漏斗）完全只读：引用失效的历史比价行照常可读，
  **绝不**被回填或静默改派；
- 既有护栏优先级不变：**授权 → 来源血缘 / 状态资格 → 实时主数据引用**（承诺级附加「显式引用」要求），
  错误差异 / 计数 / 部分行都不透露不可访问单据的存在性（非披露）；
- ERP-418 既有的「解析到档案但已删除 / 已停用」锁定复核保持不变，本任务在其之上补齐
  「显式引用缺失（`ProductId = 0`）」与「归属客户必须实时」两项承诺级判定；
- **不新增 / 不修改任何菜单 / 角色 / 用户授权**，不新增表 / 列 / 索引 / 迁移，也不要求或授予任何主数据维护权限。

## 5. 既有夹具适配（真实启用主数据，绝无请求形状旁路）

删除豁免后，历史单元夹具中「占位 `CustomerId = 7` / `SupplierId = 88` / `ProductId = 310` 但没有对应主数据行」
的采购承诺用例会按新口径被拒绝，因此按「真实启用身份 + 既有菜单 / 数据范围 + 实时主数据行」的口径**适配既有夹具**
（不断言被删除的豁免行为）：

| 文件 | 适配 |
|---|---|
| `src/ERP.UnitTests/PurchaseQuoteConversionTests.cs` | `SeedSelectedQuote` 幂等播种与占位 Id 对齐的**真实启用**供应商 88 / 商品 310 / 客户 7；「商品未引用档案」用例改为断言**承诺前受控拒绝且绝不零占位**（原「零占位落库」口径已被 ERP-491 显式取代） |
| `src/ERP.UnitTests/PurchaseQuoteConversionBatchTests.cs` | 受影响用例播种真实启用客户 7（幂等），其余业务语义不变 |
| `src/ERP.UnitTests/PurchaseQuoteConversionMutationTests.cs` | 受影响用例播种真实启用客户 7（幂等），其余业务语义不变 |
| `src/ERP.UnitTests/PurchaseQuoteMasterReferenceTests.cs` | 「进程内直调空路径保持既有行为」用例改为**失效引用同样受控拒绝且零写入**，并新增「实时主数据齐备 → 与真实请求一致放行」对照；契约断言改为**不存在** `RequiresLiveMasterValidation` / `Request.Path.HasValue` |
| `src/ERP.UnitTests/PurchaseQuoteMutationTests.cs` | 契约断言改为**不存在** `RequiresLiveMasterValidation` |

适配只改动夹具对**主数据行**的播种与断言，**不改变**用例本身要验证的业务语义（服务端权威覆盖、空草稿保存、
汇总 / 币种口径、来源血缘、生命周期等），也绝不引入请求路径 / 环境变量 / 测试专用开关旁路。

## 6. 验收证据

| 类型 | 文件 | 覆盖 |
|---|---|---|
| 单元（新增） | `src/ERP.UnitTests/PurchaseQuoteUnconditionalMasterReferenceTests.cs` | 可空草稿语义（留空放行且不臆造主数据 / 显式零与悬空客户拒绝）；调用形状无关：同一载荷 + 同一身份下「空 `Request.Path`」与「已赋值 `Request.Path`」拒绝结论与零写入**完全一致**；选择承诺、带入预填、单行转单、批次整批拒绝（零写入 / 零占号 / 状态与来源不变）；合法实时主数据正向落库；**无 `HttpContext`** 进程内直调逐入口未认证且零写入；接线断言控制器不再含请求路径豁免且承诺入口无条件调用 |
| 单元（适配） | `PurchaseQuoteConversionTests` / `PurchaseQuoteConversionBatchTests` / `PurchaseQuoteConversionMutationTests` / `PurchaseQuoteMasterReferenceTests` / `PurchaseQuoteMutationTests` | 既有业务契约保持不变；进程内直调与真实请求同一口径 |
| 集成（真实 SQL） | `src/ERP.IntegrationTests/PurchaseQuoteUnconditionalMasterReferenceSqlServerTests.cs` | 专用 localdb 上的真实控制器：空路径与真实路径**同结论**且零写入零占号；空路径合法客户 + 显式实时引用放行；携带 `ProductId = 0` 的承诺拒绝；承诺前归属客户被删除（无 `HttpContext`）拒绝；批次整批拒绝 |
| 安全 profile | `.ai/config.json` 的 `safe` | `dotnet restore` → Release 构建（analyzers + warnings-as-errors）→ `ERP.UnitTests` 全量 |

## 7. 真实 SQL 夹具安全口径

- 目标必须精确为专用实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且 `Integrated Security=true`；
  错误实例 / 错误库名 / 非集成安全的连接串一律在**任何数据库访问之前**由 `AssertDedicatedTarget` 拒绝
  （复用既有 `PurchaseQuoteMasterReferenceSqlServerFixture` 的 fail-closed 单元覆盖）；
- 每次运行只创建一个**全新 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings*.json` /
  `.env` / 生产凭据；只使用既有认证授权，不新增菜单 / 角色 / 用户授权；
- 用例只读取计数与只读快照（并断言全部采购单号序列之和不变以判定「零占号」），不删除 / 不清理任何既有行；
- **构建完成不等于阶段验收**：真实 SQL 场景须在专用 localdb 上真实执行通过才算验收证据；
  真实环境不可用时按环境阻塞单独记录，不做降级或伪造。

