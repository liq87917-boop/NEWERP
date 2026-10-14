# 仓库调拨单位完整性护栏（ERP-525）

> 目标：关闭「仓库调拨草稿可以携带**显式非空但不是商品当前基础单位**的单位落库，而审核又会把明细单位
> **静默改写为 `product.Unit` 并照原数量 / 原成本记账**」的单位完整性缺口 —— 即标注为 `CTN` / `KG` 的数量
> 可以悄悄变成基础单位数量（两仓同时被写入错误数量与错误金额）。
> 本流程**全程按商品基础单位计量**且**不实现任何包装折算**，因此选择「拒绝越界单位」而不是「臆造换算或改成本」：
> 被拒绝的请求在**单号预约、明细整体替换与任何落库之前**返回既有受控错误，零单据 · 零明细 · 零状态 · 零库存 ·
> 零流水 · 零单号消耗；提交 / 审核在**既有单据 / 库存锁内**用当前商品快照复核**已存单位**。
> 不新增表 / 列 / 索引 / 菜单 / 权限，不改库存成本口径，不改历史单据与销审语义。

## 1. 背景与缺口

`StockTransferPostingRules.EnsureLiveMasterDataAsync` 此前只校验「商品存在 / 未删除 / 已停用」，
**不校验明细单位**；`StockTransferController.Approve` 在锁内 `ResolveProductAsync` 之后执行

```csharp
d.ProductCode = product.Code;
d.ProductName = product.Name;
d.Spec = product.Spec;
d.Unit = product.Unit;      // ← 数量与成本原样保留，但单位被静默覆盖为基础单位
```

于是 `Unit = "CTN"`、`Quantity = 12`、`UnitCost = 5` 的调拨行会被当成 **12 个基础单位**记账：
调出仓少 12 个基础单位、调入仓多 12 个基础单位，两侧金额都是 60。数量和金额都"守恒"，
但含义已经被悄悄换掉（若 1 CTN = 12 PCS，真实应为 144 PCS）。既有流程里**没有任何**装箱折算，
所以正确的收敛方向是「拒绝非基础单位」，而不是在这条路径上发明换算或改成本。

## 2. 规则层：`StockTransferPostingRules`（ERP-525 段）

| 成员 | 作用 |
| --- | --- |
| `BaseUnitEligibilityRuleText` | 单位资格口径文案（接口 / 文档 / 测试同源） |
| `IsBaseUnitEligible(lineUnit, baseUnit)` | **共享纯判定（无 IO）**：去首尾空白、大小写不敏感；空 / 空白 → 放行（既有回退）；等于非空基础单位 → 放行；其余显式非空单位（**含商品自身装箱单位 `PackageUnit`**）→ 拒绝；基础单位未维护 → 显式非空单位拒绝 |
| `EnsureBaseUnitsEligible(products, details)` | **纯判定批量复核**（提交 / 审核用已在用商品快照）：商品缺失 / 无效 Id / 已删除的行跳过（由既有在用主数据护栏负责） |
| `EnsureStoredBaseUnitsEligibleAsync(db, details, ct)` | **一次有界只读查询**（`AsNoTracking` + `ToDictionaryAsync`）加载当前在用商品快照后批量复核（提交 / 审核承诺边界） |
| `EnsureLiveMasterDataAsync(...)`（既有成员） | 在**同一次**逐条商品读取内追加 `EnsureBaseUnitEligible(product, d.Unit)`：创建 / 修改 / 提交共用同一份快照，不额外查询 |

显式非空且越界的单位：`BusinessException.RuleConflict`（`1004`），文案形如

```text
商品 [箱装商品] 的明细单位 [CTN] 不是当前基础单位 [PCS]：仓库调拨按基础单位计量、流程内不做任何包装折算，
已拒绝（绝不把任意单位按基础单位记账、绝不猜测单位换算、绝不改写数量 / 成本）
```

基础单位未维护时以「未设置」展示并同样拒绝，**绝不**臆造 `PCS` 之类的新单位。空 / 空白单位继续走既有回退
（审核时按商品基础单位落库展示），不引入任何新单位、不引入任意单位之间的换算。

## 3. 控制流接线（`StockTransferController`）

| # | 路由 | 顺序 |
| --- | --- | --- |
| 1 | `POST /api/inventory/stock-transfers`（`Create`） | 授权 → `EnsureLineShape` → **`EnsureLiveMasterDataAsync`（含单位资格）** → 单号预约 → `Normalize` → 落库 |
| 2 | `PUT /api/inventory/stock-transfers/{id}`（`Update`） | 单据行锁 → 授权 → `EnsureLineShape` → **`EnsureLiveMasterDataAsync`（含单位资格）** → **只有全部通过才** `RemoveRange` + 整体替换明细 |
| 3 | `POST …/{id}/submit`（`Submit`） | 单据行锁 → 授权 → `EnsureLineShape` → `EnsureLiveMasterDataAsync` → **`EnsureStoredBaseUnitsEligibleAsync`（锁内复核已存单位）** → 状态流转 |
| 4 | `POST …/{id}/approve`（`Approve`） | 单据行锁 → 授权 → 幂等护栏 → `Normalize` → **两侧库存行确定性顺序加锁** → **`EnsureStoredBaseUnitsEligibleAsync`（库存锁内复核）** → 调出减少 / 调入增加（同一成本单价）→ 状态流转 |
| 5 | `…/unaudit`、`…/cancel`、`DELETE`、列表 / 详情 / 流水只读端点 | 既有口径**完全不变**（历史读取与冲销不新增任何资格闸门） |

关键不变式：

- 单位资格判定与在用主数据判定复用**同一次商品读取**（`EnsureLiveMasterDataAsync`），并且**先于**
  `IDocumentNumberService.GenerateAsync`（创建）与 `Db.StockTransferDetails.RemoveRange`（修改）；
- 提交 / 审核的复核位于既有 `UPDLOCK/HOLDLOCK` 单据行锁与**库存行锁之内**，且在**任何**库存增减 / 流水写入之前：
  被拒绝时整体回滚 —— 单据 / 明细 / 状态 / 库存 / 流水 / 单号全部不变；
- 数量与成本**永不因单位修正而改写**：本护栏只做「放行 / 拒绝」，不做任何折算。审核沿用既有
  `InventoryDocumentHelper.ResolveProductAsync` 语义：**空 / 空白单位**按商品基础单位兜底落库，
  已通过校验的基础单位（含大小写 / 首尾空白差异）**原样保留**，`Quantity` / `UnitCost` 一律不变。

## 4. 保持不变的既有语义

- **双仓守恒与成本口径**：审核仍然「调出仓减少 + 调入仓增加（同一成本单价）」，数量与金额双向守恒，
  ERP-009 / ERP-025 记帐与流水口径、`InventoryService` 估值规则不变。
- **并发与锁**：单据行锁、库存行确定性顺序（仓库 → 商品升序）、可串行化事务与幂等护栏全部保持；
  新增的复核只读取当前商品快照，不引入新锁、不改变锁顺序。
- **来源 / 单号 / 状态语义**：单号预约时机、状态机（Pending → Submitted → Approved → Pending）与受控错误码不变。
- **历史数据**：历史已审核 / 已取消单据可读，已存越界单位**原样可见**（绝不被静默改写）；销审 / 取消 / 删除
  不新增闸门，销审仍按既有红字流水冲销还原。
- **授权与审计**：实时身份 / 菜单（`stock-query`）授权、受控错误与既有审计保持不变；不新增权限 / 菜单。

## 5. 测试与验证

| 层次 | 位置 | 覆盖 |
| --- | --- | --- |
| 规则 / 控制器单元测试 | `src/ERP.UnitTests/StockTransferUnitIntegrityTests.cs` | 纯判定表（空 / 空白 / 大小写 / 装箱 / 任意 / 缺基础单位）、批量复核跳过语义、创建与修改的越界单位拒绝与零副作用、空单位回退与基础单位大小写、提交 / 审核锁内复核（基础单位被改写）、历史越界草稿审核拒绝与历史读取保留、两仓守恒与单位口径（空单位按基础单位兜底）、销审不新增闸门、源码契约（先校验再编号 / 记账） |
| 真实 SQL 集成测试 | `src/ERP.IntegrationTests/StockTransferUnitIntegritySqlServerTests.cs` | 与运行时同一调用路径（真实控制器 + 真实 HTTP 身份 + 真实 SQL Server）：拒绝零写入零占号、空 / 基础 / 大小写受理、主数据改写后提交 / 审核拒绝、合法审核恰好一次记账（空单位按基础单位兜底）、销审还原、历史草稿审核拒绝且可读 |
| 既有回归 | `StockTransferDraftValidationTests` / `StockTransferPostingTests` / `InventoryMovementTests` / `StockTransferDraftValidationSqlServerTests` / `StockTransferPostingSqlServerTests` 及相关 SQL 套件 | 全部保留原有业务断言（既有夹具单位本就是商品基础单位 `PCS`，无需改口径） |

命令：

```text
dotnet build NEWERP.sln -c Release /p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true
dotnet test src/ERP.UnitTests -c Release
dotnet test src/ERP.IntegrationTests -c Release --filter "FullyQualifiedName~StockTransferUnitIntegrity"
```

真实 SQL 目标：`(localdb)\NEWERP_AutoAcceptance` + 全新 GUID 独占 `NEWERP_AUTOTEST_*` 库（集成安全）。
连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值；
发现同名库已存在立即拒绝，**绝不** drop / reset / 复用任何数据库，也绝不读取 `appsettings*.json` / `.env*` / 生产凭据。

### 5.1 本轮验证记录（本机安全档；每次调用独立日志 / TRX，原始失败产物原样保留）

| 步骤 | 命令要点 | 结果 | 证据（`.ai/logs/ERP-525/`） |
| --- | --- | --- | --- |
| Release 构建（分析器 + 警告即错误） | `dotnet build NEWERP.sln -c Release --no-incremental /p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true` | **0 警告 / 0 错误** | `erp525-attempt1-build-release-01.log`（本 attempt 首次构建，含全部改动） |
| 同上（修正测试断言后复跑） | `dotnet build NEWERP.sln -c Release /p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true` | **0 警告 / 0 错误** | `erp525-attempt1-build-release-02.log` |
| 新增单位完整性单元测试 | `dotnet test src/ERP.UnitTests -c Release --no-build --filter "FullyQualifiedName~StockTransferUnitIntegrity"` | 首次 **40 通过 / 3 失败**（断言与既有 `ResolveProductAsync` 空白兜底语义、错误文案口径不一致），修正断言后 **43 通过 / 0 失败** | `…-unitnew-01.log` + `trx/…-unitnew-01.trx`（**失败原始产物保留**）、`…-unitnew-02.log` + `trx/…-unitnew-02.trx` |
| 全量单元测试（safe 档主体） | `dotnet test src/ERP.UnitTests -c Release --no-build` | **8899 通过 / 0 失败 / 0 跳过** | `…-units-full-01.log`、`trx/…-units-full-01.trx` |
| 调拨单位完整性真实 SQL | `--filter "FullyQualifiedName~StockTransferUnitIntegrity"`（子进程 `ERP_AI_TEST_RUN=1` + `ASPNETCORE_ENVIRONMENT=Development`；目标 `(localdb)\NEWERP_AutoAcceptance` + 全新 GUID `NEWERP_AUTOTEST_STOCKTRANSFERUNIT_20261014_*`） | **21 通过 / 0 失败** | `…-sql-unitintegrity-01.log`、`trx/…-sql-unitintegrity-01.trx` |
| 既有调拨真实 SQL 回归（草稿护栏 + 过账 / 冲销 / 并发） | `--filter "FullyQualifiedName~StockTransferDraftValidation&#124;FullyQualifiedName~StockTransferPosting"` | **38 通过 / 0 失败** | `…-sql-transferregression-01.log`、`trx/…-sql-transferregression-01.trx` |

如实记录：首次新增单元测试运行的 3 个失败**原始日志与 TRX 原样保留**（未被覆盖 / 未被裁剪），复跑使用**全新文件名**；
本次未覆盖、未复用、未删除任何既有验证产物。真实 SQL 仅使用子进程内显式测试上下文与专用 localdb，
未设置 `ERP_AI_ALLOW_HIGH_RISK_TESTS`、未削弱任何目标护栏、未 drop / reset / 复用任何数据库、未读取 `appsettings*.json` / `.env*` / 生产配置，
也未执行任何生产库操作。**不主张整阶段验收**：仅上述命令覆盖的范围已完成。

## 6. 明确的限制

- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置。
- 未新增 / 未修改任何数据库 schema、表 / 列 / 索引 / 菜单 / 权限；既有 `.ai/logs/` 日志原样保留。
- 不提供任意单位之间的换算（如 `CTN` ↔ `PCS`）、不做单位自动映射、不改写历史单位；
  装箱单位在调拨路径上**一律拒绝**（因为该流程没有折算实现），这属于 fail closed 而非新功能。
