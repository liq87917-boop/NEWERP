# 装柜清单草稿写入校验（ERP-506）

> 目标：关闭「装柜清单手工 / 链接草稿在**写入边界**与**承诺边界**缺少明细分行 / 在用商品护栏」的缺口。
> 被拒绝的请求在**单号预约与任何表头 · 明细 · 来源 · 状态写入之前**返回既有受控错误，
> 且零单据、零明细、零单号消耗、零库存 / 财务 / 下游单证变化。不新增表 / 列 / 索引 / 菜单 / 权限，
> 不引入币种 / 单位混算，也不改变任何库存 / 财务过账路径。

## 1. 背景与缺口

`ContainerLoadingListController.Create` / `Update` 原先只复用既有权威校验：实时身份 / 菜单 / 权威客户数据范围
（`LoadingListAuthorizationRules`，ERP-364）、上游预装柜来源链接（`ContainerLoadingFulfillmentRules`，ERP-348）、
显式出运证据链接（`LoadingStockOutLinkRules`，ERP-366 / ERP-373）。而 `LoadingStockOutLinkRules.ValidateLinksAsync`
只校验**显式链接**（`SourceStockOutDetailId is > 0`）的行，`linked.Count == 0`（无显式链接的**手工 / 历史行**）
时**直接返回**，因此：

- **手工（未链接）行**可以带着 `0` / 负数 / 超出 `DECIMAL(18,2)` 的数量、负箱数 / 负毛重 / 负体积，
  或缺失 / 不存在 / 已软删除 / 已停用商品的明细行落库；
- `ContainerLoadingListController.Calculate` 直接对 `Cartons` / `Weight` / `Volume` 求和，
  表头合计不可表示 / 溢出时同样会被落库静默取整；
- 服务端持久化时会把客户端提交的每一行明细强制为「正常」行（`ApplyServerOwnedDetailIdentity` 覆盖 `IsDeleted = false`，ERP-485），
  因此客户端 `IsDeleted = true` 并不能真正隐藏行 —— 被隐藏的坏行会在落库后被重新启用。

## 2. 规则层：复用共享 `ContainerLoadingDraftValidationRules`

ERP-505 已创建**共享**护栏类 `src/ERP.Application/Services/ContainerLoadingDraftValidationRules.cs`：
预装柜明细（`ContainerPreLoadingDetail`）与装柜明细（`ContainerLoadingDetail`）数值 / 主数据形状完全同源
（商品 / 数量 / 箱数 / 毛重 / 体积 + 删除态），两个单据统一投影为 `ContainerLoadingDraftLine` 并共用**同一份**判定。
ERP-506 只**新增两个薄适配**，不复制任何判定口径：

| 成员 | 作用 |
| --- | --- |
| `EnsureLoadingListDraftValidAsync(db, entity)` | **写入边界**：对 `entity.Details`（**全部提交行**，含客户端 `IsDeleted=true` 的行）投影后调用 `EnsureDraftValidAsync`（逐行形状 / 实际存储精度 / 与 `Calculate` 同源的表头合计 + 批量在用商品） |
| `EnsureLoadingListCommitReadyAsync(db, entity, action)` | **承诺边界**：对**已存明细**调用 `EnsureCommitReadyAsync`（至少一行有效明细 + 有效行形状 / 表头合计 + 当前在用商品） |
| `QuantityPrecision` / `QuantityScale` / `MaxDecimal18Scale2` / `IsQuantityRepresentable` | 与 EF 实际存储精度同源：`ContainerLoadingDetail` 的 `Quantity` / `Cartons` / `Weight` / `Volume`、`ContainerLoadingList` 的 `TotalCartons` / `TotalWeight` / `TotalVolume` 未显式声明精度，EF 默认 `DECIMAL(18,2)` |
| `DraftGuardRuleText` | 口径文案（接口 / 文档同源，装柜与预装柜共用） |

受控错误沿用既有分类（`ErrorCodes`）：

| 场景 | 错误 |
| --- | --- |
| 商品 Id 缺失（`<= 0`）/ 商品不存在或已删除 | `InvalidParameter`（1001） |
| 商品已停用 | `RuleConflict`（1004） |
| 数量 `<= 0`、数量 / 箱数 / 毛重 / 体积超出存储精度 | `InvalidParameter`（1001） |
| 箱数 / 毛重 / 体积为负 | `InvalidParameter`（1001） |
| 表头合计不可表示 / 溢出 | `InvalidParameter`（1001） |
| 空草稿头提交 / 审核 | `RuleConflict`（1004） |

**货币边界**：`ContainerLoadingDetail` / `ContainerLoadingList` **没有**客户端单价 / 成本 / 币种 / 金额字段，
因此不存在可校验的「货币合计」，本护栏**绝不**引入币种 / 单位混算、**绝不**臆造金额、**绝不**改写既有商业条款；
库存 / 财务仍由既有显式过账路径单独承担。

## 3. 控制器接线与顺序

| # | 路由 | 顺序 |
| --- | --- | --- |
| 1 | `POST /api/container/loading-lists`（`Create`） | `EnsureAuthorizedAsync` → `EnsureProposedScopeAllowedAsync` → `ValidateLinkAsync`（预装柜）→ `ValidateLinksAsync`（出运证据）→ **`EnsureLoadingListDraftValidAsync`** → `ApplyServerOwnedDetailIdentity` → `GenerateAsync(DocumentType.LoadingList)`（单号）→ `Status = Pending` → `Calculate` → 落库 |
| 2 | `PUT /api/container/loading-lists/{id}`（`Update`） | 授权 → 可串行化事务（预装柜行 → 本单行锁）→ 锁内重载 → 状态判定 → 已存 / 拟议范围 + 预装柜链接 + 证据链接 → **请求携带明细时先 `EnsureLoadingListDraftValidAsync`** → **只有全部通过才** `RemoveRange`（明细整体替换）并改写表头 → 落库。<br>请求**未携带明细**（`null` / 空集合）保持既有「仅更新主表」语义：既有明细与其显式证据链接原样保留，不新增闸门 |
| 3 | `POST /api/container/loading-lists/{id}/submit`（`Submit`） | 授权 → 可串行化事务（预装柜行 → 本单行锁）→ 锁内重载 → 状态判定 → 范围 + 预装柜链接 + 证据链接 → **`EnsureLoadingListCommitReadyAsync(..., "提交")`** → 置为已提交 |
| 4 | `POST /api/container/loading-lists/{id}/approve`（`Approve`） | 授权 → 可串行化事务（**上游出库行（升序）→ 预装柜行 → 本单行**锁）→ 锁内重载 → 状态判定 → 范围 + `ValidateApprovalAsync` + 出运证据链接 / 累计容量 → **`EnsureLoadingListCommitReadyAsync(..., "审核")`** → 置为已审核 |
| 5 | `POST /api/container/loading-lists/{id}/cancel`（`Cancel`） | 既有口径**完全不变** —— 历史取消 / 解除路径**不新增任何校验闸门**（商品失效仍可取消，证据链接作为历史证据原样保留） |
| 6 | `POST /api/container/loading-lists/{id}/trade-documents`（`GenerateTradeDocuments`，ERP-397） | 既有「来源行锁 + 锁内权威重读 + 原子事务」口径**完全不变**，生成只新增单证与明细行快照，**不改写**装柜明细；本任务不新增 / 不修改单证生成的任何判定或权限 |

关键不变式：

- 创建 / 修改的校验**先于** `IDocumentNumberService.GenerateAsync` 与任何 `SaveChanges`；
- 修改的校验**先于** `ContainerLoadingDetails.RemoveRange`（明细整体替换），且仅主表更新不触发替换；
- 提交 / 审核的复核在既有可串行化事务与来源 / 单据行锁**之内**，被拒绝时整体回滚：
  单据状态、明细、出运证据链接、库存 / 财务与单号全部不变；
- 客户端提交的主键 / 单号 / 状态 / 删除态 / 时间戳与明细身份仍由服务端权威覆盖；
- 行上的显式出运证据链接（`SourceStockOutDetailId`）**既不被臆造、也不被静默清空**。

## 4. 保持不变的既有语义

- **可空预装柜来源**：`PreLoadingId` 为 `null` 的手工单据仍按既有口径处理（不额外要求上游来源）。
- **显式出运证据链接**：链接校验（ERP-366 / ERP-373）与累计容量（审核时按「来源出库单 + 商品」聚合，扣减已生效退货）
  口径完全不变；本护栏只补「商品 / 数值形状」，绝不参与链接判定。
- **重复商品行**：同一商品允许出现多行（累计数量语义），不引入任何任意唯一性规则。
- **空草稿头**：0 行明细的草稿仍可保存（`Total* = 0`、`Pending`），但提交 / 审核要求至少一行有效明细。
- **历史数据**：历史已审核单据、历史快照、历史未链接行仍可读取；取消路径不新增闸门。
- **授权与审计**：ERP-364 / ERP-348 / ERP-366 的实时身份 · 菜单 · 客户数据范围、事务、行锁、受控错误与审计全部保持。
- **库存 / 财务 / 单证**：本护栏不锁库、不预留库存、不过账、不生成单证与费用，也不改变装柜到单证的生成权威。

## 5. 测试与验证

| 层次 | 位置 | 覆盖 |
| --- | --- | --- |
| 规则 / 控制器单元测试 | `src/ERP.UnitTests/LoadingListDraftValidationTests.cs` | 未链接 / 链接草稿非法行、表头合计溢出、客户端删除态隐藏坏行、空草稿头、重复商品行、链接证据创建、链接 + 停用商品、修改零改动与合法修改、仅主表保留明细 / 链接、携带明细保留链接、提交 / 审核承诺边界（空明细、主数据失效、已存数值被改写）、历史取消不清链接、EF 实际精度与源码接线契约 |
| 真实 SQL 集成测试 | `src/ERP.IntegrationTests/LoadingListDraftValidationSqlServerTests.cs` | 与运行时同一调用路径（真实控制器 + 真实 HTTP 身份 + 真实 SQL Server）：未链接 / 链接草稿非法行同口径拒绝且零写入零占号、表头合计溢出、隐藏坏行、空草稿头与重复商品行、合法链接提交 + 审核无副作用（零库存 / 财务 / 下游单据）、仅主表保留链接、修改拒绝零改动、主数据失效后提交实时拒绝、已存数值被改写后审核拒绝、历史取消不新增闸门；专用 localdb 目标护栏 |
| 既有回归 | `ContainerLoadingFulfillmentTests` / `LoadingStockOutLinkTests` / `LoadingStockOutCancellationTests` / `LoadingOutboundWorkflowTests` / `LoadingListAuthorizationTests` / `ContainerLoadingParticipantTests` / `TradeDocumentGenerationTests` / `TradeDocumentLineSnapshotTests` / `TradeDocumentGenerationMutationTests` / `TradeDocumentAuthorizationTests` / 对应 `*SqlServerTests` | 全部保留原有业务断言；仅按 ERP-506 补齐**真实在用商品**主数据（既有脚手架幂等播种，不新增授权、不改断言口径） |

命令：

```text
dotnet build NEWERP.sln -c Release /p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true
dotnet test src/ERP.UnitTests -c Release
dotnet test src/ERP.IntegrationTests -c Release --filter "FullyQualifiedName~LoadingListDraftValidation"
```

真实 SQL 目标：`(localdb)\NEWERP_AutoAcceptance` + 全新 GUID 独占 `NEWERP_AUTOTEST_*` 库（集成安全）。
连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值；
发现同名库已存在立即拒绝，**绝不** drop / reset / 复用任何数据库，也绝不读取 `appsettings*.json` / `.env*` / 生产凭据。

## 6. 明确的限制

- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置。
- 未新增 / 未修改任何数据库 schema、表 / 列 / 索引 / 菜单 / 权限；既有 `.ai/logs/` 日志原样保留。
- 装柜明细没有客户端价格 / 成本 / 币种字段，因此不存在可校验的「货币合计」；本任务未做任何金额口径改动。
- 零数量明细行不再是「可提交的空行」：写入边界即拒绝；空草稿头（0 行明细）仍受支持，
  但提交 / 审核要求至少一行有效明细。
- 本护栏在共享规则上接线，判定口径与预装柜侧（ERP-505）**完全同源**，不存在「一套规则两处实现」的分叉。

