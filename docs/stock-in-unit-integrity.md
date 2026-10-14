# 采购入库单位完整性护栏（ERP-522）

> 目标：关闭「手工 / 链接采购入库草稿可以携带**显式非空但不在商品当前基础 / 装箱单位内**的单位落库，
> 而库存数量始终按基础单位记账」的单位完整性缺口。
> 被拒绝的请求在**单号预约与任何表头 · 明细 · 来源 · 状态写入之前**返回既有受控错误，
> 且零单据、零明细、零库存、零流水、零单号消耗；不新增表 / 列 / 索引 / 菜单 / 权限，不改变库存成本口径。

## 1. 背景与缺口

`StockUnitConversion.NormalizeAsync` 只做一件事：明细单位等于商品 `PackageUnit` 且不同于 `BaseProduct.Unit`
时按 `UnitsPerPackage` 折算数量并改写为基础单位，**其他单位原样继续**。因此：

- 未链接（手工）草稿可以提交 `KG` / `SET` / 任意文本作为单位，落库后 `StockInDetail.Unit = "KG"`，
  但库存数量、库存流水数量、来源累计容量**全部按基础单位口径**处理 —— 单位与数量含义不一致；
- 只有**显式链接**采购订单的路径（`StockInOrderFulfillmentRules.IsStockInUnitCompatible`，ERP-342）
  会覆盖到「入库明细单位必须与基础单位兼容」；未链接的手工行没有任何等价校验；
- 提交 / 审核此前复核的是数值 / 主数据可用性（ERP-504），不复核**已存单位**是否仍落在基础 / 装箱单位内。

## 2. 规则层：`StockUnitConversion`（ERP-522 段）

| 成员 | 作用 |
| --- | --- |
| `UnitEligibilityRuleText` | 口径文案（接口 / 文档 / 测试同源） |
| `IsUnitEligible(lineUnit, baseUnit, packageUnit)` | **纯判定（无 IO）**：去首尾空白、大小写不敏感；空 / 空白单位 → 放行（既有回退）；等于非空基础单位 → 放行；等于非空装箱单位 → 放行；其他显式非空单位 → 拒绝 |
| `EnsureUnitsEligible(products, details)` | **纯判定批量复核**：给定商品快照逐行判定；商品缺失 / 无效 Id 的行跳过（由既有在用商品护栏负责），绝不越权判定 |
| `EnsureUnitsEligibleAsync(db, details, ct)` | **一次有界只读查询**加载当前在用商品快照后批量复核（提交 / 审核承诺边界使用） |
| `NormalizeAsync(db, details, ensureUnitEligible)` | 采购入库专用重载：**同一次批量商品读取**内先判定单位资格，再执行既有装箱折算（恰好一次） |

显式非空且越界的单位：`BusinessException.RuleConflict`（`1004`），文案形如

```text
商品 [箱装商品] 的明细单位 [KG] 不在当前有效单位口径内（基础单位 [PCS] / 装箱单位 [CTN]）：已拒绝，
绝不把任意单位按基础单位记账、绝不猜测单位换算
```

主数据缺失（基础单位 / 装箱单位均未维护）但请求显式提交非空单位时同样受控拒绝，
`UnitDisplay` 以「未设置」展示，**绝不**臆造 `PCS` 之类的新单位。

## 3. 控制流接线

| # | 路由 | 顺序变化 |
| --- | --- | --- |
| 1 | `POST /api/stock-ins`（`Create`） | … → `NormalizeAsync(Db, entity.Details, ensureUnitEligible: true)`（资格 + 折算）→ `Calculate` → `ValidateLinkAsync` → `EnsureDraftValidAsync` → 单号 → 落库 |
| 2 | `PUT /api/stock-ins/{id}`（`Update`） | … → `ApplyServerOwnedDetailIdentity` → `NormalizeAsync(..., ensureUnitEligible: true)` → `ValidateLinkAsync` → `EnsureDraftValidAsync` → **只有全部通过才**改写已存表头 / 整体替换明细 |
| 3 | `POST /api/stock-ins/{id}/submit`（`Submit`） | … → `EnsureCommitReadyAsync(..., "提交")` → **`EnsureUnitsEligibleAsync(..., 生效明细)`** → 基类状态流转 |
| 4 | `POST /api/stock-ins/{id}/approve`（`Approve`） | … → 可串行化事务 → 来源 / 单据锁 → 实时授权 → 幂等护栏 → `EnsureCommitReadyAsync(..., "审核")` → **`EnsureUnitsEligibleAsync(..., 生效明细)`** → `ValidateApprovalAsync` → 逐行记账 |
| 5 | `POST /api/stock-ins/{id}/cancel`（`Cancel`） | 既有口径**完全不变**（历史取消 / 冲销不新增任何闸门） |

关键不变式：

- 创建 / 修改的资格判定与折算**复用同一次商品读取**，且**先于** `IDocumentNumberService.GenerateAsync`
  与任何 `SaveChanges`、先于 `StockInDetails.RemoveRange`（明细整体替换）；
- 装箱单位按**当前**正数 `UnitsPerPackage` **恰好折算一次**（保留 4 位小数），改写为基础单位后
  提交 / 审核不会再折算（不重复折算）；
- 审核的复核在既有可串行化事务与来源 / 单据行锁**之内**，被拒绝时整体回滚：
  来源剩余容量、单据状态、成本、库存、流水与单号全部不变。

## 4. 保持不变的既有语义

- **启用边界**：仅**采购入库**路径显式启用；销售出库 / 销售退货 / 采购退货沿用既有重载
  （`ensureUnitEligible: false`），保持既有语义直至各自任务显式接入等价校验，**不做跨族顺手改造**。
- **空单位回退**：空 / 空白单位保持既有回退（库存流水按商品基础单位兜底），不放行任何臆造换算。
- **来源语义**：`PurchaseOrderId` 的 `0` / `null` 归一与未链接历史语义不变；绝不臆造来源。
- **容量与成本**：ERP-342 累计容量按折算后的基础单位累计；ERP-033 成本口径与 ERP-025 流水口径不变。
- **历史数据**：历史已审核 / 已取消单据可读；取消 / 冲销路径不新增闸门，绝不改写历史单位。
- **授权与审计**：ERP-352 统一闸门、非披露可见性、事务、行锁、受控错误与操作日志全部保持。

## 5. 测试与验证

| 层次 | 位置 | 覆盖 |
| --- | --- | --- |
| 规则 / 控制器单元测试 | `src/ERP.UnitTests/StockInUnitIntegrityTests.cs` | 未链接 / 链接草稿的越界单位拒绝与零副作用、空白回退、基础 / 装箱单位大小写与空白不敏感、装箱折算恰好一次、重复商品行累计、缺失 / 改写主数据、装箱数非法与换算溢出、提交 / 审核承诺边界复核、链接来源累计容量与取消释放、纯判定表、接线契约 |
| 真实 SQL 集成测试 | `src/ERP.IntegrationTests/StockInUnitIntegritySqlServerTests.cs` | 与运行时同一调用路径（真实控制器 + 真实 HTTP 身份 + 真实 SQL Server）：拒绝零写入零占号、空白 / 基础 / 装箱口径、折算一次、主数据改写后提交 / 审核拒绝、链接累计容量 |
| 既有回归 | `StockUnitConversionTests` / `StockInDraftValidationTests` / `StockInOrderFulfillmentTests` / `StockInOutMovementLedgerTests` / `PurchaseStockInCostTests` / `StockInCanonicalEntryTests` / `ReturnSourceCancellationTests` 及相关 SQL 套件 | 全部保留原有业务断言（既有夹具单位本就是基础 / 装箱单位，无需改口径） |

命令：

```text
dotnet build NEWERP.sln -c Release /p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true
dotnet test src/ERP.UnitTests -c Release
dotnet test src/ERP.IntegrationTests -c Release --filter "FullyQualifiedName~StockInUnitIntegrity"
```

真实 SQL 目标：`(localdb)\NEWERP_AutoAcceptance` + 全新 GUID 独占 `NEWERP_AUTOTEST_*` 库（集成安全）。
连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值；
发现同名库已存在立即拒绝，**绝不** drop / reset / 复用任何数据库，也绝不读取 `appsettings*.json` / `.env*` / 生产凭据。

## 6. 明确的限制

- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置。
- 未新增 / 未修改任何数据库 schema、表 / 列 / 索引 / 菜单 / 权限；既有 `.ai/logs/` 日志原样保留。
- 其他单据族（销售出库 / 销售退货 / 采购退货）**本轮不启用**等价单位资格校验；共享闸门已就绪，
  由各自后续任务显式接入。
- 不提供任意单位之间的换算（如 `KG` ↔ `PCS`）、不做单位自动映射、不改写历史单位。
