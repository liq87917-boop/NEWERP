# 来源出库单 / 来源入库单取消前的退货引用护栏（ERP-359）

## 1. 目标

在 ERP-357（销售退货 `SalesReturn.SourceStockOutId`）与 ERP-358（采购退货 `PurchaseReturn.SourceStockInId`）建立「显式来源 + 可退容量」护栏之后，补齐**闭环的另一端**：当来源单据（销售出库单 / 采购入库单）被**取消**时，必须先确认没有仍然生效的已审核退货单显式引用它，避免把退货依据（成交 / 入库证据）冲销掉，导致退货审计证据链与库存台账断裂。

只复用既有分层架构与既有单据流转（创建 / 提交 / 审核 / 销审 / 取消 / 删除），不做报表、不做 Agent、不做界面视觉 / 样式 / 导出打磨。

## 2. 口径

- **显式引用证据**：只读取既有的 `SalesReturn.SourceStockOutId` / `PurchaseReturn.SourceStockInId`（可空，刻意不建外键）。
  **绝不**按来源单号文本（`SourceStockOutNo` / `SourceStockInNo`）、金额或相似度推断引用；`null` 来源的无关退货**永不**阻断。
- **生效（live effective）**：引用的退货单必须 `IsDeleted = 0` 且 `Status = Approved`（已审核存活）。已销审（`Pending`）、已提交 / 待提交（`Submitted` / `Pending`）、已取消（`Cancelled`）都不构成生效引用。
- **判定时机**：在任何库存冲销与状态变更**之前**，且在**同一来源单据行锁 + 可串行化事务内**完成，被拒绝时单据 / 明细 / 状态 / 库存 / 流水全部保持原样。
- **成本 / 估值口径不变**：来源取消的库存冲销仍复用既有 `InventoryService`（ERP-009）记账并写红字流水。

## 3. 判定与拒绝文案

`ReturnSourceCancellationRules` 是唯一的只读判定口径：

| 方法 | 判定 |
|---|---|
| `EnsureNoEffectiveApprovedSalesReturnAsync(db, stockOutId)` | 存在 `SalesReturns.SourceStockOutId = 本出库单` 且未删除 + 已审核 → 抛 `RuleConflict` |
| `EnsureNoEffectiveApprovedPurchaseReturnAsync(db, stockInId)` | 存在 `PurchaseReturns.SourceStockInId = 本入库单` 且未删除 + 已审核 → 抛 `RuleConflict` |

拒绝文案明确给出**可执行的处置要求**（先销审 / 取消退货单）与阻断的退货单号 / Id：

```
来源销售出库单已被已审核销售退货单 [XTH...]（Id 123）显式引用，取消会破坏退货依据与库存台账：
请先对已审核退货单执行销审（红字冲销）或取消，再取消来源单据
```

被拒绝时**不做任何写入**：`StockInController.Cancel` / `StockOutController.Cancel` 在事务回滚后原样返回异常，单据状态、明细、库存数量与金额、库存流水（含 `IsReversed` / `IsReversal` 标记）全部保持拒绝前状态。

## 4. 确定性锁序与原子事务（与退货审核 / 销审共用同一把上游行锁）

`StockOutController.Cancel` 与 `SalesReturnController` 的审核 / 销审 / 取消 / 删除共用
`ReturnSourceCancellationRules.LockStockOutRowSql`：`SELECT Id FROM db_owner.StockOuts WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}`；
`StockInController.Cancel` 与 `PurchaseReturnController` 对应用 `LockStockInRowSql`。

取消端点执行顺序：

1. 事务前无锁预读仅取归属（客户 / 供应商 / 仓库 / 来源订单）做实时授权与数据范围判定；
2. 开启 `IsolationLevel.Serializable` 事务；
3. **确定性锁序「来源单据行 → 退货单行」**：先 `UPDLOCK, HOLDLOCK` 锁来源出库单 / 入库单行；
4. 锁内重读单据并**再次**复核实时授权 / 数据范围、状态幂等护栏（已取消拒绝）；
5. `ReturnSourceCancellationRules` 判定是否存在生效的已审核退货引用，存在即 fail closed；
6. 通过后才按既有口径冲销库存、置为已取消、提交事务。

**两条独立连接竞争**（退货审核 vs 来源取消）：双方先取同一把来源单据行锁，故被串行化。若退货审核先提交，来源取消在锁内看到已审核退货 → 拒绝；若来源取消先提交，退货审核在锁内看到来源已取消（非已审核）→ 拒绝。**只允许一种一致结果**。

销审 / 取消退货单后，来源取消回到既有流程（放行）；重复取消来源单据仍按既有规则拒绝（`已取消`），不重复冲销库存。

## 5. 授权与数据范围（不新增权限模型）

| 端点 | 实时判定入口 | 说明 |
|---|---|---|
| 来源销售出库单取消 | `SalespersonDataScopeService`（ERP-097） | 沿用既有无菜单门槛的客户数据范围；范围外按「不存在」拒绝 |
| 来源采购入库单取消 | `StockInAuthorizationRules`（ERP-352） | 既有「采购入库」菜单 + 真实可用供应商 / 仓库 + 来源采购订单归属客户范围 |
| 退货审核 / 销审 | `SalesReturnSourceRules` / `PurchaseReturnSourceRules` | 既有菜单 + 客户数据范围（来源取消与其共用同一把锁） |

- **不新增**任何表 / 列 / 菜单 / 权限模型，不新增用户授权，也绝不把空身份当作管理员。
- 每次请求重新解析授权与数据范围，撤销授权后下一次请求立即收敛（绝不缓存）。

## 6. 不改写项

- 不重写退货原始数量、不改写历史库存流水与金额、不改财务分摊历史、不改动单据编号与快照；
- 不新增表 / 列 / 外键 / schema，不新增报表 / Agent / 权限 / 菜单；
- 来源单据审计（`SourceDocType` / `SourceDocId` / `SourceDocNo`）与红字冲销轨迹原样保留；
- `.ai/logs/` 下既有失败 / 验证日志原样保留（不删除、不覆盖、不重写）。

## 7. 单元测试（`src/ERP.UnitTests/ReturnSourceCancellationTests.cs`，11 例，全部通过）

全部使用内存库（`TestDbFactory`），不连接 SQL Server；身份为既有特权入库 / 出库操作员与真实业务员（既有菜单授权 + 真实客户数据范围），不使用匿名 / 管理员兜底。

| 用例 | 断言 |
|---|---|
| 来源出库单：存在已审核关联销售退货 | 拒绝（`1005` 冲突）；文案含 `销审` 与退货单号；单据 / 明细 / 状态 / 库存 / 流水全部不变 |
| 来源出库单：null 来源 / 已提交退货 | 不阻断，按既有流程冲销；无关 / 未完审核退货原样保留 |
| 来源出库单：不按来源单号文本推断 | null 来源但来源单号文本相同 → 不阻断，退货未被改写 |
| 来源出库单：关联退货已销审（真实控制器流程） | 放行；红字冲销轨迹（退货销审 + 出库取消）保留 |
| 来源出库单：关联退货已取消（真实控制器流程） | 放行；既有流程不变 |
| 来源出库单：重复取消 | 按既有规则 `已取消` 拒绝，不重复冲销 |
| 来源入库单：存在已审核关联采购退货 | 拒绝（`1005` 冲突）；全部原始证据不变 |
| 来源入库单：null 来源 / 已提交退货 | 不阻断，按既有流程冲销 |
| 来源入库单：关联退货已销审（真实控制器流程） | 放行；红字冲销流水保留 |
| 规则层：锁语句与拒绝文案 | 锁语句为 `UPDLOCK, HOLDLOCK` 的 `StockOuts` / `StockIns` 行锁；文案可执行 |
| 规则层：内存库 | 不视为关系型提供程序 |

`src/ERP.UnitTests/StockInOutMovementLedgerTests.cs` 追加 2 例护栏回归（采购入库 / 销售出库存在已审核关联退货时取消被拒、库存与流水不变），保留原有全部数量 / 成本 / 冲销断言。

## 8. 真实 SQL Server 集成测试（`src/ERP.IntegrationTests/ReturnSourceCancellationSqlServerTests.cs`）

在 GUID 独占的 `NEWERP_AUTOTEST` 目标库上自包含播种主数据 / 来源单据 / 退货单与真实授权账号，**直接执行真实控制器**（`StockOutController` / `StockInController` / `SalesReturnController` / `PurchaseReturnController` + `ReturnSourceCancellationRules` + `InventoryService`），不复制一份测试专用实现：

| 场景 | 断言 |
|---|---|
| 来源出库单：已审核销售退货引用 | 冲突拒绝、文案含 `销审` 与退货单号；单据 / 明细 / 状态 / 库存 / 流水全不变 |
| 来源出库单：null 来源同号 / 已提交退货 | 放行；无关 / 未完审核退货原样保留 |
| 来源出库单：销审后退货（真实流程） | 放行；红字冲销流水保留（2 条冲销行） |
| 来源出库单：重复取消 | 既有限制 `已取消`；流水仍为 2 条、库存复原 |
| 来源入库单：已审核采购退货引用 | 冲突拒绝；全部原始证据不变 |
| 来源入库单：null 来源 / 已提交退货 | 放行；无关 / 未完审核退货原样保留 |
| **两条独立连接竞争**：销售退货审核 vs 来源出库单取消 | 只允许一种一致结果；退货已审核（来源保持已审核、库存 +4）**异或** 来源已取消（退货保持已提交、库存按既有兜底 +10） |
| **两条独立连接竞争**：采购退货审核 vs 来源入库单取消 | 只允许一种一致结果；退货已审核（来源保持已审核、库存 -4）**异或** 来源已取消（退货保持已提交、库存按既有兜底 -10） |
| 目标库护栏（`[Theory]` 3 例） | 非专用目标在访问数据库之前被拒绝 |

**执行记录（本任务实现门槛内实际运行）**

- `dotnet test src/ERP.IntegrationTests/ERP.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~ReturnSourceCancellation"` → **11 通过 / 0 失败**（含两例两条独立连接竞争与三例目标库护栏）。
- 目标为专用实例 `(localdb)\NEWERP_AutoAcceptance`，每次运行只新建 GUID 后缀 `NEWERP_AUTOTEST_*` 库；未 drop / reset / 复用任何数据库；未读取 `appsettings` / `.env` / 生产凭据。

**真实 SQL 夹具安全口径**

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，且 `IntegratedSecurity=true`；实例名 / 库名前缀 / 集成安全在**访问数据库之前**校验（`AssertDedicatedTarget`，含单元级护栏用例）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；**绝不 drop / reset / 复用**任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings` / `.env` / 生产凭据。

## 9. 未执行 / 限制

- 本任务 `validation_profile = safe`、`completion_mode = build`：交付门槛为 **Release build + 全量 `ERP.UnitTests`**；真实 SQL 集成用例另外在专用 localdb 上运行通过（11 例），**构建通过不等于阶段验收通过**。
- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置。
- 未新增 / 未修改任何数据库 schema、未新增表 / 列 / 菜单 / 权限；`.ai/logs/` 下既有失败日志原样保留。

## 10. 相关实现

- `src/ERP.Application/Services/ReturnSourceCancellationRules.cs`：来源取消前的退货引用判定 + 归一化锁语句与文案（纯判定、只读）。
- `src/ERP.Api/Controllers/StockOutController.cs`：取消端点新增「来源出库单行锁 + 可串行化事务 + 退货引用护栏」（与销售退货审核 / 销审同一把锁）。
- `src/ERP.Api/Controllers/StockInController.cs`：取消端点新增「来源入库单行锁 + 可串行化事务 + 退货引用护栏」（与采购退货审核 / 销审同一把锁）。
- `src/ERP.Api/Controllers/SalesReturnController.cs` / `PurchaseReturnController.cs`：来源行锁改为引用 `ReturnSourceCancellationRules` 的唯一定义，保证两侧取到同一把上游行锁。
- 复用既有：`SalesReturnSourceRules` / `PurchaseReturnSourceRules`（来源权威性与容量）、`SalespersonDataScopeService`（ERP-097 数据范围）、`StockInAuthorizationRules`（ERP-352）、`InventoryService`（ERP-009 记账与冲销）。

