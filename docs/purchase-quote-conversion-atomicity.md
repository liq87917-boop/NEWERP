# 供应商比价 → 采购订单转换的原子性与并发协议（ERP-418）

本文记录**供应商比价（PurchaseQuote）单行 / 批次转换为采购订单（PurchaseOrder）** 的共享事务协议、
确定性锁序、锁内权威复核、受控输家语义、币种与合计口径，以及阶段验收边界。
接口与文档同源的常量集中在 `src/ERP.Application/Services/PurchaseQuoteConversionMutationRules.cs`
（`QuoteRowLockSql` / `OwningSalesOrderRowLockSql` / `LockOrderText` / `BoundaryText` / 各拒绝文案）。

## 1. 背景与缺陷面

实现前的转换路径（`PurchaseQuoteController.ToPurchaseOrder` 与
`PurchaseQuoteConversion.ConvertBatchAsync`）存在以下口径缺口：

1. 单行转换只锁**来源比价行**，未锁定其**归属销售订单**：与「销售订单取消 / 普通采购归属链接」在同一来源上
   并发时可能出现「来源已取消但采购订单照常生成」；
2. 批次转换在**未加锁**的数据上构建草稿（`AsNoTracking` 快照），随后另行按 Id 重查受跟踪行并写入：
   锁定前解析出的成员与锁定后落库的成员可能不是同一批数据；
3. 单行与批次两条路径各自实现守卫，存在语义漂移风险；
4. `ParseCurrency` 对无法识别的币种**静默回退 CNY**：会把客户原币金额改写成另一币种，属于不可接受的会计口径漂移；
5. 批次计划 / 结果的 `TotalAmount` 把不同币种的金额**相加成一个数**，前端 `batchPlanText`
   又把它按单一金额展示（"混合币种金额被当成钱"）。

## 2. 唯一协议（单行与批次逐字共用）

入口：`PurchaseQuoteConversionMutationRules.LockAndReloadAsync(db, requestedLineIds, expectedBatchNo, ct)`。

| 步骤 | 动作 | 失败语义 |
| --- | --- | --- |
| 0 | 调用方开启原子事务（`BeginConversionTransactionAsync`，复用 ERP-417 / ERP-395 口径，绝不嵌套） | 关系型后端真实回滚；内存库等价无事务 |
| 1 | 解析**不可变批次成员**（比价行 Id 集合：去重、仅正整数、升序）并做加锁前只读快照 | 行不存在 / 已删除 → `NotFound("比价记录不存在")`；成员不属于给定批次 → `InvalidParameter` |
| 2 | 解析每条来源行 `RefOrderNo`（转换前语义 = **关联销售订单号**）指向的**权威归属销售订单** | 命中但已删除 → `OwnershipUnavailableText`；已取消 → `OwnershipCancelledText`；未审核 / 已驳回 → `OwnershipNotApprovedText`；**绝不静默生成无归属或错误归属的订单** |
| 3 | 对归属销售订单按 **SalesOrderId 升序**取排它行锁：`SELECT Id FROM db_owner.SalesOrders WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}`（实现为审计时间戳刷新的 `UPDATE`），与既有**销售订单取消 / 预装柜 / 普通采购归属链接**共用同一把行锁 | 并发删除 → 受控拒绝 `OwnershipUnavailableText` |
| 4 | 对来源比价行按 **Id 升序**取排它行锁：`SELECT Id FROM db_owner.PurchaseQuotes WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}`，与 ERP-417 **比价修改 / 删除 / 批量删除 / 审批决定**共用同一把行锁 | 行被并发删除 → `RuleConflict(StaleSourceText)` |
| 5 | **锁内重新读取**受跟踪权威行（不复用加锁前副本），并逐字核对成员集合、批次号 `QuoteNo`、`RefOrderNo` | 任一漂移 → `RuleConflict(MembershipChangedText)` / `StaleSourceText` |
| 6 | 锁内复核：实时权限（比价 + 目的地菜单 + 权威客户数据范围）、资格（已选中 / 未放弃 / 未转换 / 已批准决定 / 批准供应商一致）、**币种可识别**、主数据引用与单位、数量与单价精度、目的地归属规则（`PurchaseSalesOrderLinkRules`） | 任一不满足 → 受控业务拒绝，**发号之前**中止 |
| 7 | 取号 → 新增订单（含明细）→ 逐行来源留痕（状态「已转采购订单」+ `RefOrderNo` 回写采购单号 + 备注来源标记）→ **单次 `SaveChanges`** → 提交 | 任一步失败 → 整体回滚，**不残留孤儿订单 / 明细 / 有标记无订单的半成品** |

**锁序不可逆**：归属销售订单恒先于来源比价行；任一侧多把锁一律按 Id 升序获取，绝不反向获取下游锁，
因此不存在锁环。未关联销售订单的比价行（`RefOrderNo` 为空或不是任何既有销售订单号）不取销售订单锁，
保持既有口径（归属字段留空）。

## 3. 受控输家（并发单行 / 批次 / 重叠批次）

- **单行 vs 单行**、**单行 vs 批次**、**重叠批次**：同一来源行最终只有**一次**成功提交；
  先提交者写入订单与留痕并提交事务，输家在第 4/5/6 步被串行化后重新判定的资格守卫挡住，
  得到受控的**重复 / 陈旧**业务拒绝（`该比价行已生成采购订单：…` / `已转为采购订单` / `StaleSourceText`），
  **不产生第二张订单**，也不产生部分写入。
- 并发**批准 / 拒绝决定**：与转换共用同一把比价行锁；批准赢家先提交时，转换在锁内看到的是已批准条款；
  转换赢家先提交时，决定侧看到「已转采购订单」冻结并受控拒绝。
- 并发**来源取消（销售订单）**：与转换共用同一把销售订单行锁，**取消与转换只能成功其一**。
- 并发**来源编辑 / 删除**：与转换共用同一把比价行锁；编辑赢家先提交时转换在锁内读到新条款（并以新条款复核），
  删除赢家先提交时转换受控拒绝且不写入。

## 4. 币种与合计口径（ERP-418 收紧）

- **币种唯一权威解析**：`PurchaseQuoteConversionMutationRules.TryParseCurrency` /
  `RequireCurrency`（`PurchaseQuoteConversion.ParseCurrency` 为其 Api 侧同源包装）：
  去首尾空白、不区分大小写、必须是**受支持枚举名**（CNY / USD），且拒绝 `"0"` 这类纯数字文本。
  **无法识别一律抛 `InvalidParameter`，绝不回退人民币**；单行与批次（含批次内任一来源行）同样拒绝。
- **跨币种绝不合计**：
  - `PurchaseQuoteBatchPlan` / `PurchaseQuoteBatchConversionResult` 的 `TotalAmount` 为 `decimal?`：
    **只有单一币种时才有值**；跨币种时为 `null`（显式未知），并置 `MixedCurrency = true`；
  - 新增 `TotalAmountByCurrency: List<PurchaseQuoteCurrencyTotal>`（`{ Currency, TotalAmount, LineCount }`），
    同币种才相加、按币种逐一列示；
  - 单张订单 / 分组的 `TotalAmount` 仍是标量：分组键已包含币种，一张订单永远只有一种币种；
  - 前端 `batchPlanText` → `batchTotalText` 只读 `plan.totalAmountByCurrency` 与 `plan.mixedCurrency`，
    跨币种时展示为「合计（服务端重算，按币种分列，不做跨币种合计）：CNY …；USD …」，
    **绝不**再出现 `fmtMoney(plan.totalAmount)`。
- **按单核算保持**：每张生成的采购订单保留自己的币种、汇率（比价表无汇率列按 1）与明细单位，
  服务端合计只在该订单内部累加（`PurchaseOrderController.Calculate` 唯一权威）。

## 5. 受控拒绝文案（接口 / 文档 / 测试同源）

| 场景 | 常量 | 语义 |
| --- | --- | --- |
| 来源行被并发删除 / 成员变化 | `StaleSourceText` / `MembershipChangedText` | 本次转换整体未生效，请刷新后重试 |
| 归属销售订单被删除 | `OwnershipUnavailableText` | 拒绝静默生成无归属 / 变更归属订单 |
| 归属销售订单已取消 | `OwnershipCancelledText` | 不能作为采购备货的归属来源 |
| 归属销售订单未审核 / 已驳回 | `OwnershipNotApprovedText` | 不能作为采购备货的归属来源 |
| 币种无法识别 | `UnknownCurrencyText` | 拒绝转换，绝不回退默认人民币 |
| 供应商 / 商品档案停用或删除 | `SupplierMasterText` / `ProductMasterText` | fail closed，不采信比价行快照 |
| 单位缺失 / 超长 | `UnitText` | 拒绝生成采购订单 |

## 6. 只读路径

代入预填（`GET {id}/order-prefill`）、批次计划（`GET batch-order-plan`）与批次审批状态保持**只读**：
不写库、不占用单据号、不改来源状态 / `RefOrderNo` / 审计时间戳、不新增或修改审批决定；
归属销售订单的受控判定与真实转换同源（不可用来源在只读路径同样显式拒绝或显式跳过，绝不静默给出"看似可转"的草稿）。

## 7. 边界（不做的事）

- 不新增 / 不修改任何表、列、索引、菜单、角色或用户授权；不伪造授权，**不把空身份当作匿名或管理员**
  （实时身份 + 既有「供应商比价」/「采购订单」菜单 + ERP-097 客户数据范围，全程 fail closed）；
- 不改写审批决定历史（append-only）、不改写采购订单 / 销售订单商业字段；
- **不写库存 / 库存流水 / 发票 / 退税 / 费用 / 财务记录**，不触发任何库存或财务过账；
- 不删除任何历史证据；行锁只刷新归属销售订单与来源比价行的技术审计时间戳 `UpdatedAt`（非商业证据）。

## 8. 验收证据

- **单元测试（内存库）**：`PurchaseQuoteConversionTests`、`PurchaseQuoteConversionBatchTests`（既有 +
  跨币种合计与严格币种口径更新）、`PurchaseQuoteConversionMutationTests`（锁序 / 受控拒绝 / 主数据 /
  跨币种合计 / 只读 / 强制失败回滚）、`PurchaseQuoteConversionUiTests`（前端接线与嵌套 DTO 契约）。
- **真实 SQL Server（两个独立连接）**：`PurchaseQuoteConversionMutationSqlServerTests`
  —— 竞争单行、重叠批次、来源删除、币种无法识别、非法主数据、强制中途失败回滚，
  并校验生成的表头 / 明细 / 来源链接数量、库存 / 财务零写入、目的地授权与服务端合计。
  该套件只允许访问精确实例 `(localdb)\NEWERP_AutoAcceptance` + 全新 GUID 库 `NEWERP_AUTOTEST*` +
  `Integrated Security=true`；**访问数据库之前**拒绝错误实例 / 错误库名 / 非集成安全，绝不 drop / reset
  既有库，也不读取生产配置。
- **构建完成不等于阶段验收**：Release 构建 + 安全单元测试通过只说明可交付；阶段验收以受控 localdb 上
  真实执行的并发 / 原子性证据为准。
