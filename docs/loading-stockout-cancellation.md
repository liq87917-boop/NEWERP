# 来源销售出库单取消 / 冲销前的「已审核装柜清单引用」护栏（ERP-367）

## 1. 背景与目标

ERP-366 用可空证据列 `db_owner.ContainerLoadingDetails.SourceStockOutDetailId` 把装柜明细与
**已审核、未删除的销售出库明细**显式链接起来，并在装柜审核时以「来源出库数量 − 已生效退货」为容量上限。

但**反方向**此前没有护栏：`StockOutController.Cancel` 只校验「已审核销售退货显式引用」（ERP-359），
无法看到物理出运证据 —— 一旦来源出库单被取消 / 冲销，已审核装柜清单所依据的**物理出运证据与库存台账随之断裂**。

本任务（阶段 3 库存闭环）补齐取消方向：**只要仍存在「已审核、未删除」的装柜清单明细显式链接到本出库单明细，
来源出库单取消即 fail closed 拒绝**，并给出可执行的装柜撤销要求。

## 2. 判定口径（只认显式链接，不猜测）

`LoadingStockOutLinkRules.EnsureNoEffectiveApprovedLoadingAsync(db, stockOutId)` 是唯一只读判定口径：

| 项目 | 口径 |
| --- | --- |
| 证据通道 | **只**沿 `ContainerLoadingDetail.SourceStockOutDetailId → StockOutDetails.Id` 显式链接判定 |
| 生效条件 | 装柜清单 `IsDeleted = 0` **且** `Status = Approved`；装柜明细 `IsDeleted = 0`；链接目标属于本出库单明细 |
| 阻断范围 | 链接到**其它**出库单明细的行不阻断；`null` 来源（历史 / 无证据）明细不阻断 |
| 非生效 | 待提交 / 已提交 / 已取消 / 已删除的装柜清单一律不阻断 |
| 不猜测 | **绝不**按商品、出库单号、金额或相似度推断来源；绝不回填历史明细 |
| 只读 | 被拒绝时不改单据 / 明细 / 状态 / 库存 / 流水，也不改写装柜历史证据 |

拒绝文案（接口 / 文档同源，`LoadingStockOutLinkRules.LoadingReversalRequirementText`）：

```
来源销售出库单已被已审核装柜清单 [ZQ...]（Id 123，装柜明细 Id 456）显式引用，
冲销会破坏已证明出运证据与库存台账：请先取消已审核装柜清单（历史装柜证据与链接原样保留、不删除），再取消来源出库单
```

## 3. 锁序与原子事务（与装柜审核 / ERP-359 共用同一把来源行锁）

`StockOutController.Cancel` 在**取得来源出库单行锁的可串行化事务内**执行，

```
来源出库单行 UPDLOCK, HOLDLOCK（ReturnSourceCancellationRules.LockStockOutRowSql
                             = LoadingStockOutLinkRules.LockStockOutRowSql，同一语句）
  → 退货引用判定（ERP-359）
  → 装柜链接判定（ERP-367）
  → 库存红字冲销 / 历史单据基础单位恢复
  → 置为已取消 → 提交
```

- 装柜审核（ERP-366）也**先**对同一批上游销售出库单行按 `StockOutId` 升序取同一把 `UPDLOCK, HOLDLOCK`，
  因此并发「装柜审核」与「来源取消」被串行化在同一把行锁上，**只出现一种一致结果**；
- 任一步失败整体回滚：**状态既不改、库存与流水也不动**（含「后续库存冲销失败」场景）；
- 非关系型提供程序（内存库）无行锁语义，跳过锁语句，判定语义不变。

## 4. 释放与不变量

- **取消装柜清单即释放护栏**：`ContainerLoadingListController.Cancel` 只把状态置为「已取消」，
  装柜明细的 `SourceStockOutDetailId` 与历史数量**原样保留**（绝不回填 / 重写 / 删除）；
- 释放后来源出库单按既有流程恢复库存并写红字流水，重复取消按既有规则 `已取消` 幂等拒绝，不重复冲销；
- **不禁止合法出运后销售退货**：本护栏只作用于「出库单取消」，不影响销售退货的登记 / 提交 / 审核；
  后续装柜容量仍按「来源数量 − 已生效退货 − 已链接装载」计算（ERP-366）；
- 既有 ERP-359 退货护栏不被绕过：装柜释放后若仍有已审核销售退货显式引用，取消继续被拒绝。

## 5. 授权与数据范围（不新增权限模型）

| 端点 | 实时判定入口 | 说明 |
| --- | --- | --- |
| 来源销售出库单取消 | `SalespersonDataScopeService`（ERP-097）+ 既有 `StockOutController` 授权 | 事务前与锁内各复核一次客户数据范围，范围外按「不存在」拒绝 |
| 装柜清单取消（释放） | `LoadingListAuthorizationRules`（ERP-364）+ 既有「装柜清单」菜单 | 仅改状态，不改写链接证据 |
| 装柜审核 | `LoadingStockOutLinkRules`（ERP-366） | 既有「装柜清单」+「销售出库」菜单 |

- **不新增**任何表 / 列 / 菜单 / 权限模型，不新增用户授权，也绝不把空身份当作管理员；
- 每次请求重新解析授权与数据范围，撤销授权后下一次请求立即收敛（绝不缓存）。

## 6. 不改写项

- 不改变库存成本口径（移动加权平均）、不改写历史库存流水与金额；
- 不重写装柜历史数量与状态、不删除任何装柜链接与审计证据；
- 不改写商品 / 客户 / 销售订单 / 出库单主数据与来源单据审计；
- 不新增表 / 列 / 外键 / schema，不新增报表 / Agent / 权限 / 菜单；
- `.ai/logs/` 下既有失败 / 验证日志原样保留（不删除、不覆盖、不重写）。

## 7. 单元测试（`src/ERP.UnitTests/LoadingStockOutCancellationTests.cs`，内存库）

全部使用内存库（`TestDbFactory`）+ **真实控制器**（`StockOutController` / `ContainerLoadingListController` / `SalesReturnController`）
与既有授权身份（既有菜单授权 + 真实数据范围），不使用匿名 / 管理员兜底。

| 用例 | 断言 |
| --- | --- |
| 已审核装柜清单显式引用 | 拒绝（`1005` 冲突）；文案含可执行装柜撤销要求与阻断清单号；单据 / 明细 / 状态 / 库存 / 流水 / 装柜证据全部不变 |
| 待提交 / 已提交 / 已取消 / 已删除装柜清单 | 不阻断；按既有流程取消并恢复库存；非生效装柜证据原样保留 |
| 历史未链接（`null`）/ 链接其它出库单 | 不阻断；绝不按商品 / 号猜测来源；未链接历史数量与链接不被改写 |
| 装柜清单取消后 | 护栏释放且链接 / 数量证据保留；来源取消放行（红字冲销流水保留） |
| 重复取消来源单据 | 既有规则 `已取消` 幂等拒绝，不重复冲销 |
| 装柜释放但已审核销售退货仍在 | ERP-359 退货护栏继续拒绝（文案含「销审」），台账不变 |
| 合法出运后销售退货 | 仍可登记 / 提交 / 审核；后续装柜容量扣除已生效退货（溢出拒绝） |
| 规则层 | 取消护栏与装柜审核锁语句**完全相同**；文案可执行；内存库非关系型 |
| 控制器接入 | `IsolationLevel.Serializable`；「来源行锁 → 装柜护栏 → 库存冲销」顺序成立 |

`src/ERP.UnitTests/StockInOutMovementLedgerTests.cs` 追加 2 例台账回归（已审核装柜引用时取消被拒且库存 / 流水不变；
装柜取消后放行且红字冲销流水与装柜链接证据保留）。

## 8. 真实 SQL Server 集成测试（`src/ERP.IntegrationTests/LoadingStockOutCancellationSqlServerTests.cs`）

在 GUID 独占的 `NEWERP_AUTOTEST` 目标库上自包含播种主数据 / 出库单 / 装柜清单 / 授权账号，
**直接执行真实控制器**（`StockOutController` / `ContainerLoadingListController` + `LoadingStockOutLinkRules` + `InventoryService`）：

| 场景 | 断言 |
| --- | --- |
| 已审核装柜清单显式引用 | 冲突拒绝；文案含可执行要求与清单号；单据 / 明细 / 状态 / 库存 / 流水（0 条）全部不变 |
| 装柜清单取消（真实控制器） | 释放护栏；来源取消放行、库存按基础单位原路恢复（10 + 5）；链接证据保留；重复取消幂等 |
| 历史未链接 / 未审核 / 已取消 / 已删除装柜 | 不阻断；全部装柜证据原样保留 |
| 装柜释放后仍存在已审核销售退货 | ERP-359 护栏继续拒绝（文案含「销审」）；库存 / 流水不变 |
| **两条独立连接竞争①**：装柜审核 vs 来源取消 | 只允许一种一致结果：装柜已审核（来源保持已审核、库存 0）**异或** 来源已取消（装柜保持已提交、库存 10） |
| **两条独立连接竞争②**：释放后两条并发取消 | 恰好一次成功、一次 `已取消` 拒绝；库存恰好恢复一次（10 + 5）；链接证据保留 |
| 后续库存冲销失败（人为软删除库存行） | 冲突拒绝且**整体回滚**：状态仍为已审核、正向流水未被标记已冲销、无红字流水 |
| 目标库护栏（`[Theory]` 4 例 + 接受 1 例） | 非专用目标在访问数据库之前被拒绝 |

**执行记录（本任务实现门槛内实际运行）**

- `dotnet test src/ERP.IntegrationTests/ERP.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~LoadingStockOutCancellation"`
  → **12 通过 / 0 失败**（含两例两条独立连接竞争、回滚例与 5 例目标库护栏）。
- 目标为专用实例 `(localdb)\NEWERP_AutoAcceptance`，每次运行只新建 GUID 后缀 `NEWERP_AUTOTEST_*` 库；
  未 drop / reset / 复用任何数据库；未读取 `appsettings` / `.env` / 生产凭据。

## 9. 未执行 / 限制

- 本任务 `validation_profile = safe`、`completion_mode = build`：交付门槛为 **Release build + 全量 `ERP.UnitTests`**；
  真实 SQL 集成用例另外在专用 localdb 上运行通过（12 例），**构建通过不等于阶段验收通过**。
- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置。
- 未新增 / 未修改任何数据库 schema、未新增表 / 列 / 菜单 / 权限；`.ai/logs/` 下既有失败日志原样保留。

## 10. 相关实现

- `src/ERP.Application/Services/LoadingStockOutLinkRules.cs`：新增 `EnsureNoEffectiveApprovedLoadingAsync` 只读判定、
  可执行处置文案 `LoadingReversalRequirementText`、口径 / 边界文案与（与 ERP-359 完全相同的）来源行锁语句定义。
- `src/ERP.Api/Controllers/StockOutController.cs`：取消端点在**同一来源行锁 + 可串行化事务**内、在任何库存冲销前追加装柜护栏。
- `src/ERP.Api/Controllers/ContainerLoadingListController.cs`：取消端点只改状态，装柜链接与历史数量原样保留（释放护栏）。
- 复用既有：`ReturnSourceCancellationRules`（ERP-359 退货引用护栏）、`LoadingStockOutLinkRules`（ERP-366 链接与容量）、
  `InventoryService`（ERP-009 记账与红字冲销）、`SalespersonDataScopeService`（ERP-097）、`LoadingListAuthorizationRules`（ERP-364）。
