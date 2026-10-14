# 退货单位完整性护栏（ERP-524）

> 目标：关闭「手工 / 链接销售退货、采购退货草稿可以携带**显式非空但不在商品当前基础 / 装箱单位内**的单位落库，
> 而库存数量、来源可退容量始终按基础单位记账」的单位完整性缺口。
> 与采购入库（ERP-522）、销售出库（ERP-523）复用**同一套**共享单位资格判定；被拒绝的请求在
> **单号预约与任何表头 · 明细 · 来源 · 状态写入之前**返回既有受控错误，且零单据、零明细、零库存、零流水、零单号消耗；
> 提交 / 审核在**既有单据 / 来源行锁内**用当前商品快照复核仍然生效的已存单位资格。
> 不新增表 / 列 / 索引 / 菜单 / 权限，不改变库存成本口径，不引入任何自动退款 / 红冲 / 来源推断。

## 1. 背景与缺口

`StockUnitConversion.NormalizeAsync` 只做一件事：明细单位等于商品 `PackageUnit` 且不同于 `BaseProduct.Unit`
时按 `UnitsPerPackage` 折算数量并改写为基础单位，**其他单位原样继续**。ERP-522 / ERP-523 已把它接入采购入库与销售出库，
但退货族此前调用的是**不启用资格**的 2 参重载，因此：

- 未链接（手工）退货草稿可以提交 `KG` / `SET` / 任意文本作为单位，落库后明细单位与数量含义不一致；
- 只有**显式链接**来源的路径（`SalesReturnSourceRules` / `PurchaseReturnSourceRules` 的来源证据折算）
  会间接覆盖到「单位可折算为基础单位」，未链接的手工行没有任何等价校验；
- 提交 / 审核此前复核的是数值 / 主数据可用性（ERP-503），不复核**已存单位**是否仍落在基础 / 装箱单位内。

## 2. 规则层：`StockUnitConversion`（ERP-524 段）

| 成员 | 作用 |
| --- | --- |
| `ReturnUnitEligibilityRuleText` | 销售退货 / 采购退货单位资格口径文案（接口 / 文档 / 测试同源；与采购入库 / 销售出库同一套判定与受控拒绝口径） |
| `IsUnitEligible(lineUnit, baseUnit, packageUnit)` | **共享纯判定（无 IO）**：去首尾空白、大小写不敏感；空 / 空白单位 → 放行（既有回退）；等于非空基础单位 → 放行；等于非空装箱单位 → 放行；其他显式非空单位 → 拒绝 |
| `EnsureUnitsEligible(products, details)` | **共享纯判定批量复核**：ERP-524 新增 `IEnumerable<SalesReturnDetail>` / `IEnumerable{PurchaseReturnDetail}` 重载；商品缺失 / 无效 Id 的行跳过（由既有在用商品护栏负责） |
| `EnsureUnitsEligibleAsync(db, details, ct)` | **一次有界只读查询**加载当前在用商品快照后批量复核（提交 / 审核承诺边界使用）。ERP-524 新增退货明细重载 |
| `NormalizeAsync(db, details, ensureUnitEligible)` | 退货专用重载：**同一次批量商品读取**内先判定单位资格，再执行既有装箱折算（恰好一次） |

显式非空且越界的单位：`BusinessException.RuleConflict`（`1004`），文案形如

```text
商品 [箱装商品] 的明细单位 [KG] 不在当前有效单位口径内（基础单位 [PCS] / 装箱单位 [CTN]）：已拒绝，
绝不把任意单位按基础单位记账、绝不猜测单位换算
```

非法 / 缺失主数据同样 fail closed：装箱数非正 → 「每包装数量必须大于 0」；只维护装箱单位而缺基础单位却提交装箱单位
→ 「未设置基础单位，不能执行包装换算」；基础 / 装箱单位均未维护而提交非空单位 → 越界口径错误，
`UnitDisplay` 以「未设置」展示，**绝不**臆造 `PCS` 之类的新单位。

## 3. 控制流接线

| # | 路由 | 顺序变化 |
| --- | --- | --- |
| 1 | `POST /api/inventory/sales-returns`（`Create`） | … → `NormalizeAsync(Db, entity.Details, ensureUnitEligible: true)`（资格 + 折算）→ `EnsureDraftValidAsync` → `ValidateLinkAsync` → 单号 → 落库 |
| 2 | `PUT /api/inventory/sales-returns/{id}`（`Update`） | … → `NormalizeAsync(..., ensureUnitEligible: true)` → `EnsureDraftValidAsync` → `ValidateLinkAsync` → **只有全部通过才**改写已存表头 / 整体替换明细 |
| 3 | `POST /api/inventory/sales-returns/{id}/submit`（`Submit`） | … → `EnsureHasEffectiveLine(..., "提交")` → `EnsureLineShape` → **`EnsureUnitsEligibleAsync(..., 生效明细)`** → `ValidateLinkAsync` → 状态流转 |
| 4 | `POST /api/inventory/sales-returns/{id}/approve`（`Approve`） | … → 可串行化事务 → 来源出库单行 → 退货单行锁 → 实时授权 / 客户范围 → 幂等护栏 → **`EnsureUnitsEligibleAsync(..., 生效明细)`** → `ValidateApprovalAsync`（来源权威性 + 累计可退容量）→ 逐行记账 |
| 5 | `POST /api/inventory/purchase-returns…` | 与销售退货**同源同口径**（来源采购入库单行 → 退货单行锁、上游客户范围、`ValidateApprovalAsync`） |
| 6 | `…/unaudit`、`…/cancel`、`DELETE`、只读端点 | 既有口径**完全不变**（历史销审 / 取消 / 冲销不新增任何闸门） |

关键不变式：

- 创建 / 修改的资格判定与折算**复用同一次商品读取**，且**先于** `IDocumentNumberService.GenerateAsync`
  与任何 `SaveChanges`、先于 `RemoveRange`（明细整体替换）；手工未链接与显式链接来源的草稿**同口径**；
- 装箱单位按**当前**正数 `UnitsPerPackage` **恰好折算一次**（保留 4 位小数），改写为基础单位后
  提交 / 审核不会再折算（不重复折算），基础单位数量保持权威、来源容量按折算后的基础单位累计；
- 审核的复核在既有可序列化事务与单据 / 来源行锁**之内**，被拒绝时整体回滚：
  来源剩余容量、单据状态、库存、流水与单号全部不变。

## 4. 保持不变的既有语义

- **启用边界**：仅新增**退货族**的显式启用；采购入库（ERP-522）、销售出库（ERP-523）保持原样，不做跨族顺手改造。
- **空单位回退**：空 / 空白单位保持既有回退（库存流水按商品基础单位兜底），不放行任何臆造换算。
- **来源语义**：`SourceStockOutId` / `SourceStockInId` 的显式链接与未链接历史语义不变；绝不臆造来源、
  不做自动来源推断、不引入自动退款 / 红冲 / 成本重估。
- **容量与成本**：ERP-357 / ERP-358 累计可退容量按折算后的基础单位累计；ERP-033 成本口径与 ERP-025 流水口径不变。
- **历史数据**：历史已审核 / 已取消单据可读；销审 / 取消 / 冲销路径不新增闸门，绝不改写历史单位。
- **授权与审计**：实时身份 / 菜单 / 客户与上游范围、共享草稿护栏 `ReturnDocumentDraftRules`（声明于
  `SalesReturnSourceRules.cs`）、确定性锁序、受控错误与操作日志全部保持。

## 5. 测试与验证

| 层次 | 位置 | 覆盖 |
| --- | --- | --- |
| 规则 / 控制器单元测试 | `src/ERP.UnitTests/ReturnUnitIntegrityTests.cs` | 未链接 / 链接草稿的越界单位拒绝与零副作用、空白回退、基础 / 装箱单位大小写与空白不敏感、装箱折算恰好一次、修改路径拒绝且零替换、缺失 / 改写主数据、装箱数非法与缺基础单位、提交 / 审核承诺边界复核、链接来源累计容量与不重复折算、纯判定表、接线契约 |
| 真实 SQL 集成测试 | `src/ERP.IntegrationTests/ReturnUnitIntegritySqlServerTests.cs` | 与运行时同一调用路径（真实控制器 + 真实 HTTP 身份 + 真实 SQL Server）：拒绝零写入零占号、空白 / 基础 / 装箱口径、折算一次、主数据改写后提交 / 审核拒绝、链接累计容量与销审释放 |
| 既有回归 | `SalesReturnSourceTests` / `PurchaseReturnSourceTests` / `*SourceSelectionTests` / `ReturnDocumentDraftValidationTests` / `ReturnSourceCancellationTests` / `StockInUnitIntegrityTests` / `StockOutUnitIntegrityTests` / `StockUnitConversionTests` 及相关 SQL 套件 | 全部保留原有业务断言（既有夹具单位本就是基础 / 装箱单位，无需改口径） |

命令：

```text
dotnet build NEWERP.sln -c Release /p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true
dotnet test src/ERP.UnitTests -c Release
dotnet test src/ERP.IntegrationTests -c Release --filter "FullyQualifiedName~ReturnUnitIntegrity"
```

真实 SQL 目标：`(localdb)\NEWERP_AutoAcceptance` + 全新 GUID 独占 `NEWERP_AUTOTEST_*` 库（集成安全）。
连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值；
发现同名库已存在立即拒绝，**绝不** drop / reset / 复用任何数据库，也绝不读取 `appsettings*.json` / `.env*` / 生产凭据。

### 5.1 本轮验证记录（本机安全档；每次调用独立日志 / TRX，原始失败产物原样保留）

| 步骤 | 命令要点 | 结果 | 证据（`.ai/logs/ERP-524/`） |
| --- | --- | --- | --- |
| Release 构建（分析器 + 警告即错误） | `dotnet build NEWERP.sln -c Release --no-incremental /p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true` | **0 警告 / 0 错误**（exit 0） | `erp524-attempt2-build-release-02.log` |
| 全量单元测试（safe 档主体） | `dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build` | **8856 通过 / 0 失败 / 0 跳过**（exit 0） | `erp524-attempt2-units-01.log`、`trx/erp524-attempt2-units-01.trx` |
| 退货单位完整性真实 SQL | `--filter "FullyQualifiedName~ReturnUnitIntegrity"`（子进程 `ERP_AI_TEST_RUN=1` + `ASPNETCORE_ENVIRONMENT=Development`；目标 `(localdb)\NEWERP_AutoAcceptance` + 全新 GUID `NEWERP_AUTOTEST_*`） | **24 通过 / 0 失败**（exit 0） | `erp524-attempt2-returnsql-01.log`、`trx/erp524-attempt2-returnsql-01.trx` |
| 既有来源 / 草稿 / 取消 / 单位完整性真实 SQL 回归 | `--filter "FullyQualifiedName~PurchaseReturnSource|…~SalesReturnSource|…~ReturnDocumentDraftValidation|…~ReturnSourceCancellation|…~StockInUnitIntegrity|…~StockOutUnitIntegrity"` | **165 通过 / 0 失败**（exit 0，含 16 个既有套件与目标护栏套件） | `erp524-attempt2-sqlregression-01.log`、`trx/erp524-attempt2-sqlregression-01.trx` |

如实记录：首次 Release 构建调用因当前终端单命令 30 秒上限被中断（构建尚未包含 `ERP.UnitTests`），
其**部分日志原样保留**在 `erp524-attempt2-build-release-01.log`，随后用**新文件名**（`…-02.log`）完整重跑并成功；
此前编排器的失败 / 通过日志（`erp524-units-01/02`、`erp524-returnsql-01/02/03`、`erp524-sqlregression-01` 及其 TRX）**全部原样保留**，
本次未覆盖、未复用、未删除任何既有验证产物。真实 SQL 仅使用子进程内显式测试上下文与专用 localdb，
未设置 `ERP_AI_ALLOW_HIGH_RISK_TESTS`、未削弱任何目标护栏、未 drop / reset / 复用任何数据库、未读取生产配置。

## 6. 明确的限制

- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置。
- 未新增 / 未修改任何数据库 schema、表 / 列 / 索引 / 菜单 / 权限；既有 `.ai/logs/` 日志原样保留。
- 不提供任意单位之间的换算（如 `KG` ↔ `PCS`）、不做单位自动映射、不改写历史单位；
  也不新增历史读取 / 取消 / 销审闸门。
