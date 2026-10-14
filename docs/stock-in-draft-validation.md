# 采购入库草稿写入校验（ERP-504）

> 目标：关闭「采购入库手工 / 链接草稿在**写入边界**与**承诺边界**缺少明细分行 / 在用商品护栏」的缺口。
> 被拒绝的请求在**单号预约与任何表头 · 明细 · 来源 · 状态写入之前**返回既有受控错误，
> 且零单据、零明细、零库存、零流水、零单号消耗。不新增表 / 列 / 索引 / 菜单 / 权限，不改变任何库存成本口径。

## 1. 背景与缺口

`StockInController.Create` / `Update` 原先只复用既有权威校验：实时身份 / 菜单 / 供应商 · 仓库可用性
（`StockInAuthorizationRules`，ERP-352）、单位折算（`StockUnitConversion.NormalizeAsync`）、表头合计（`Calculate`）、
可选来源链接（`StockInOrderFulfillmentRules.ValidateLinkAsync`，ERP-342）。而 `ValidateLinkAsync` 对**未链接**（`null`）
的历史语义直接返回，因此：

- 未链接草稿可以带着 `0` / 负数 / 超出 `DECIMAL(18,2)` 的数量、负毛重 / 负体积、
  或缺失 / 不存在 / 已软删除 / 已停用商品的明细行落库；
- 提交（`Submit`）与审核（`Approve`）此前只做状态流转与幂等护栏，**空明细**单据可以被提交，
  历史 / 被改写为非法数值的已存明细也能进入过账循环；
- 服务端持久化时会把客户端提交的每一行明细强制为「正常」行
  （`ApplyServerOwnedDetailIdentity` 覆盖 `IsDeleted = false`），因此客户端 `IsDeleted = true`
  并不能真正隐藏行 —— 被隐藏的坏行会在落库后被重新启用。

## 2. 规则层：`StockInAuthorizationRules`（ERP-504 段）

| 成员 | 作用 |
| --- | --- |
| `QuantityPrecision` / `QuantityScale` / `MaxDecimal18Scale2` | 与 EF 实际存储精度同源：`StockInDetail.Quantity` / `Weight` / `Volume` 与 `StockIn.TotalQuantity` / `TotalWeight` / `TotalVolume` 未显式声明精度，EF 默认 `DECIMAL(18,2)`（与 ERP-502 销售出库同口径） |
| `IsQuantityRepresentable(decimal)` | 最多 2 位小数且在 `[-9_999_999_999_999_999.99, 9_999_999_999_999_999.99]` 内 |
| `EnsureRequestLineShape(details)` | **请求侧**：逐行校验**全部提交行**（含客户端 `IsDeleted=true` 的行，服务端会恢复为正常行），再按与 `Calculate` **同源**公式重算并校验表头合计（含 `decimal` 加法溢出） |
| `EnsureEffectiveLineShape(details)` | **承诺边界**：逐行校验仍然生效（未删除）的已存行，表头合计仍对全部已存行重算（口径与 `Calculate` / 记账一致） |
| `EnsureHasEffectiveLine(details, action)` | 提交 / 审核至少一行有效明细；空草稿头可保存但不可提交 / 审核 |
| `EnsureProductsAvailableAsync(db, details)` | **一次有界只读查询**批量校验在用商品：存在、未删除、已启用（绝不逐行查库） |
| `EnsureDraftValidAsync(db, details)` | 创建 / 修改共享护栏：`EnsureRequestLineShape` → `EnsureProductsAvailableAsync` |
| `EnsureCommitReadyAsync(db, details, action)` | 提交 / 审核共享护栏：`EnsureHasEffectiveLine` → `EnsureEffectiveLineShape` → 在用商品（批量） |
| `DraftGuardRuleText` | 口径文案（接口 / 文档同源） |

受控错误沿用既有分类（`ErrorCodes`）：

| 场景 | 错误 |
| --- | --- |
| 商品 Id 缺失（`<= 0`）/ 商品不存在或已删除 | `InvalidParameter`（1001） |
| 商品已停用 | `RuleConflict`（1004） |
| 数量 `<= 0`、数量 / 毛重 / 体积超出存储精度 | `InvalidParameter`（1001） |
| 毛重 / 体积为负 | `InvalidParameter`（1001） |
| 表头合计不可表示 / 溢出 | `InvalidParameter`（1001） |
| 空草稿头提交 / 审核 | `RuleConflict`（1004） |

**价格 / 成本边界**：`StockInDetail` **没有**客户端单价 / 成本 / 金额字段，成本一律由 ERP-033 既有权威来源解析
（`PurchaseStockInCostSource`）并在审核时落账，因此本护栏没有可校验的价格字段，也**绝不**臆造来源、
**绝不**改写已审核采购价；库存流水成本精度仍由既有 `InventoryService` / `StockMovement`（`DECIMAL(18,6)`）兜底。

## 3. 控制器接线与顺序

| # | 路由 | 顺序 |
| --- | --- | --- |
| 1 | `POST /api/stock-ins`（`Create`） | `NormalizePurchaseOrderLink` → `EnsureMenuAuthorizedAsync` → `EnsureAuthorizedAsync`（供应商 / 仓库 / 来源归属）→ `NormalizeAsync`（单位折算）→ `Calculate` → `ValidateLinkAsync`（可选来源）→ **`EnsureDraftValidAsync`** → `ApplyServerOwnedDetailIdentity` → `GenerateAsync`（单号）→ `Status = Pending` → 落库 |
| 2 | `PUT /api/stock-ins/{id}`（`Update`） | 闸门 → 加载已存单据 → 可见性 + 请求侧授权 → 状态判定 → `ApplyServerOwnedDetailIdentity`（请求对象）→ 单位折算 → `ValidateLinkAsync` → **`EnsureDraftValidAsync`** → **只有全部通过才**改写已存表头字段、整体替换明细、`Calculate` → 落库 |
| 3 | `POST /api/stock-ins/{id}/submit`（`Submit`） | 闸门 → 加载单据（含明细）→ 可见性 → **`EnsureCommitReadyAsync(..., "提交")`** → 基类状态流转（`Pending → Submitted`） |
| 4 | `POST /api/stock-ins/{id}/approve`（`Approve`） | 闸门 → 可见性 → 状态判定 → 单位折算 + `Calculate` → 成本来源解析 → **可串行化事务** → 来源订单行锁 → 锁内实时授权 → 幂等护栏（已有流水）→ **`EnsureCommitReadyAsync(..., "审核")`** → `ValidateApprovalAsync`（ERP-342 累计容量）→ 逐行 `_inventory.IncreaseAsync` → `Status = Approved` |
| 5 | `POST /api/stock-ins/{id}/cancel`（`Cancel`） | 既有口径**完全不变** —— 历史取消 / 冲销（含无流水历史单据按基础单位原路冲回）**不新增任何校验闸门** |

关键不变式：

- 创建 / 修改的校验**先于** `IDocumentNumberService.GenerateAsync` 与任何 `SaveChanges`；
- 修改的校验**先于** `StockInDetails.RemoveRange`（明细整体替换）；
- 审核的复核在既有可串行化事务与来源 / 单据行锁**之内**，被拒绝时整体回滚：
  来源剩余容量、单据状态、成本、库存、流水与单号全部不变；
- 客户端提交的主键 / 单号 / 状态 / 删除态 / 时间戳与明细身份仍由服务端权威覆盖。

## 4. 保持不变的既有语义

- **可空来源语义**：`PurchaseOrderId` 的 `0`（表单「未选择来源」）与 `null` 仍归一为未链接；未链接草稿不做累计容量校验。
- **供应商 / 仓库权威**：仍由 `StockInAuthorizationRules.EnsureAuthorizedAsync` 校验（存在、未删除、已启用）。
- **单位折算**：包装单位先按 `UnitsPerPackage` 折算为基础单位（保留 4 位小数），护栏在折算**之后**校验；
  折算结果超出 `DECIMAL(18,2)` 时同样受控拒绝，绝不静默截断。
- **重复商品行**：同一商品允许出现多行（累计数量语义），不引入任何任意唯一性规则。
- **空草稿头**：0 行明细的草稿仍可保存（`Total* = 0`、`Pending`），但提交 / 审核要求至少一行有效明细。
- **成本 / 流水**：ERP-033 成本来源解析与 ERP-025 库存流水口径不变。
- **累计容量 / 取消释放**：ERP-342 口径不变（已取消 / 未审核单据不占用剩余可收数量）。
- **历史数据**：历史已审核单据、历史快照仍可读取；取消 / 冲销路径不新增闸门。
- **授权与审计**：ERP-352 统一闸门、可见性非披露、事务、行锁、受控错误与操作日志全部保持。

## 5. 测试与验证

| 层次 | 位置 | 覆盖 |
| --- | --- | --- |
| 规则 / 控制器单元测试 | `src/ERP.UnitTests/StockInDraftValidationTests.cs` | 非法明细（缺失 / 不存在 / 已删除 / 已停用商品，零 / 负 / 不可表示 / 溢出数量，负 / 不可表示毛重体积）、表头合计溢出、客户端删除态隐藏坏行、空草稿头、重复商品行、折算后不可表示、提交 / 审核复核（空明细、已存数值被改写、商品失效）、修改零改动与合法修改、来源累计与取消释放、历史取消不新增闸门、EF 实际精度与源码接线契约 |
| 真实 SQL 集成测试 | `src/ERP.IntegrationTests/StockInDraftValidationSqlServerTests.cs` | 与运行时同一调用路径（真实控制器 + 真实 HTTP 身份 + 真实 SQL Server）：手工未链接与显式链接来源同口径拒绝并零写入零占号、空草稿头、重复商品行、合法审核与取消冲销、修改零改动、提交 / 审核实时复核；专用 localdb 目标护栏 |
| 既有回归 | `StockInAuthorizationTests` / `StockInCanonicalEntryTests` / `StockInControllerTests` / `StockInOrderFulfillmentTests` / `StockInOutMovementLedgerTests` / `PurchaseStockInCostTests` / `ReturnSourceCancellationTests` / `StockOutDraftValidationTests` / `StockUnitConversionTests`，以及 `StockInAuthorizationSqlServerTests` / `StockInSourceSelectionSqlServerTests` / `ReturnSourceCancellationSqlServerTests` | 全部保留原有业务断言；仅按 ERP-504 补齐**在用商品 / 仓库**等真实主数据（既有测试脚手架幂等播种，不新增授权、不改断言口径） |

命令：

```text
dotnet build NEWERP.sln -c Release /p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true
dotnet test src/ERP.UnitTests -c Release
dotnet test src/ERP.IntegrationTests -c Release --filter "FullyQualifiedName~StockInDraftValidation"
```

真实 SQL 目标：`(localdb)\NEWERP_AutoAcceptance` + 全新 GUID 独占 `NEWERP_AUTOTEST_*` 库（集成安全）。
连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值；
发现同名库已存在立即拒绝，**绝不** drop / reset / 复用任何数据库，也绝不读取 `appsettings*.json` / `.env*` / 生产凭据。

## 6. 明确的限制

- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置。
- 未新增 / 未修改任何数据库 schema、表 / 列 / 索引 / 菜单 / 权限；既有 `.ai/logs/` 日志原样保留。
- 采购入库明细没有客户端价格 / 成本字段，因此「负 / 不可表示价格」在本单据上不存在可校验字段；
  该约束由既有权威成本来源解析与既有采购价口径承担，本次未做任何改动。
- 零数量明细行不再是「可提交的空行」：写入边界即拒绝；空草稿头（0 行明细）仍受支持，
  但提交 / 审核要求至少一行有效明细。
