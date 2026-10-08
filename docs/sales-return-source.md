# 销售退货显式来源与可退容量护栏（ERP-357）

## 1. 目标

让销售退货单通过**既有** `SalesReturn.SourceStockOutId` 显式衔接来源销售出库单，并在创建 / 修改 / 提交 / 审核时用权威的「来源出库数量」与「累计已审核退货数量」防止超退。只复用既有分层架构与既有单据流转（创建 / 修改 / 提交 / 审核 / 销审 / 取消 / 删除），不做报表、不做 Agent、不做界面视觉 / 样式 / 导出打磨。

## 2. 口径

- **来源链接**：`SalesReturn.SourceStockOutId`（可空，刻意不建外键）。未链接（`null`）的历史退货**保持显式「无来源」**：不做任何来源校验，也**绝不**按 `SourceStockOutNo` 文本、金额或相似度猜测链接。
- **权威链接**：出库单存在、未删除、`Status = Approved`（已审核存活）、客户一致、出库仓库与退货入库仓库一致、来源单号快照一致，且每条退货明细的商品都能在来源出库单找到**单位可折算为基础单位**的出库证据。
- **已审核出库数量**：来源出库单中该商品的明细数量之和（重复商品行合计），按商品基础单位折算（等于基础单位直接使用，等于装箱单位按 `UnitsPerPackage` 折算）。
- **已审核退货数量**：以同一来源出库单为来源、`IsDeleted = 0`、`Status = Approved` 的**其它**退货单明细数量之和（重复商品行合计），同样折算为基础单位。
- **上限判定**：逐商品比较 `已审核退货 + 本次退货 ≤ 已审核出库数量 + 0.0001 容差`；跨单位、跨单据不臆造授权数量。

## 3. 链接权威性判定（无效链接 fail closed 拒绝）

`SalesReturnSourceRules.ResolveLinkAsync` 是创建 / 修改 / 提交 / 审核**共用**的同一份权威判定。显式链接（`SourceStockOutId` 非空）必须构成权威来源，否则抛 `BusinessException`，**不落库、不消耗单据号、不改库存 / 流水 / 状态**；未链接（`null`）不做任何校验。以下任一情况视为无效链接：

| 判定项 | 权威要求 |
|---|---|
| 出库单可用性 | 存在、未删除且 `Status = Approved`（已取消 / 已驳回 / 待提交 / 已提交均拒绝） |
| 客户一致 | `SalesReturn.CustomerId == StockOut.CustomerId` |
| 仓库一致 | `SalesReturn.WarehouseId == StockOut.WarehouseId`（权威来源仓库） |
| 来源快照一致 | 显式填写的 `SourceStockOutNo` 必须与权威来源单号一致（冲突即拒绝，绝不静默改写） |
| 商品身份 | 每条退货明细必须指定商品，且商品存在、未删除 |
| 出库证据 | 来源出库单必须存在该商品的出库明细 |
| 单位证据 | 退货明细与来源出库明细的单位都必须是商品基础单位或装箱单位（装箱数 > 0）；其他 / 空基础单位一律拒绝 |
| 数值证据 | 来源出库数量与退货数量不得为负（负数量视为证据损坏，fail closed） |

## 4. 审核累计上限、并发串行化与原子过账

`SalesReturnController` 按以下顺序执行（修改 / 提交 / 审核 / 销审 / 取消 / 删除共用同一套锁与事务口径）：

1. 实时授权（身份 / 账号状态 / 既有 `sales-return` 菜单）与客户数据范围；
2. 开启 `IsolationLevel.Serializable` 事务；
3. **确定性锁序「来源出库单行 → 退货单行」**（`SELECT ... WITH (UPDLOCK, HOLDLOCK)`）：把**同一来源出库单**上的并发退货串行化，后到者能看到先到者已提交的累计退货数量；未链接（`null`）不加来源锁；
4. 锁内重读权威状态、幂等护栏（已产生有效库存流水的单据拒绝重复审核）；
5. `SalesReturnSourceRules.ValidateApprovalAsync` 判定链接权威性并做累计上限校验；
6. 逐行写入退货入库库存与库存流水、回填实际成本、置为已审核、提交事务。

任一步失败即回滚：库存、库存流水、单据状态都保持不变（内存库等无事务提供程序由控制器显式丢弃变更跟踪器中的半成品变更保证「全不变」）。**销审**按既有红字流水冲销并**释放可退容量**（仅已审核退货计入累计）；冲销不安全时拒绝且余额 / 流水 / 状态全部不变。

## 5. 错误信息（API 直接返回）

- 累计超限：「商品 [X] 累计退货数量 11 超过来源销售出库单可退数量 10（已审核退货 6 + 本次 5）」
- 无效显式链接：来源出库单不存在 / 已删除 / 未审核（或已取消）/ 客户不一致 / 仓库不一致 / 快照不一致 / 商品无出库证据 / 单位无法折算 / 负数量证据。

## 6. 授权与数据范围（不新增权限模型）

| 场景 | 判定入口 | 结果 |
|---|---|---|
| 无身份 / 非法用户 Id | `SalesReturnSourceRules.EnsureMenuAuthorizedAsync` | `2000` 未认证 |
| 账号不存在或已删除 | 同上 | `2000` 未认证 |
| 账号已禁用 | 同上 | `2002` 权限不足 |
| 无 `sales-return` 菜单授权 | 同上 | `2002` 权限不足 |
| 客户不在数据范围 | `EnsureCustomerScopeAsync` | `1002` 按「不存在」拒绝（不泄露归属） |

- 复用既有「角色 → 菜单」口径（`SysUserRoles → SysRoleMenus → SysMenus`，忽略按钮型菜单与被删除记录），由既有 `CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync` 读取；**每次请求重新查询**，撤销授权后下一次请求立即收敛（绝不缓存）。
- 复用既有 `SalespersonDataScopeService`（ERP-097 唯一权威口径）判定客户可见范围。
- **不新增**任何表 / 列 / 菜单 / 权限模型，不新增用户授权，也绝不把空身份当作管理员。

## 7. 不改写项

- 不重写退货原始数量、不改写历史库存流水与金额、不改历史出库单、不新增表列 / 外键 / schema；
- 不新增报表、Agent、权限、菜单；来源单据审计（`SourceDocType` / `SourceDocId` / `SourceDocNo`）与红字冲销轨迹原样保留；
- `.ai/logs/` 下既有失败 / 验证日志原样保留（不删除、不覆盖、不重写）。

## 8. 单元测试（`src/ERP.UnitTests/SalesReturnSourceTests.cs`）

全部使用内存库（`TestDbFactory`），不连接 SQL Server；身份是**非特权业务员账号**（既有 `sales-return` 菜单 + 真实客户数据范围），不使用管理员兜底。

| 用例 | 断言 |
|---|---|
| 创建：关联已审核出库单（同客户同仓库） | 保存成功并回填权威来源单号 / 行号 |
| 创建：未关联来源 | 保持历史行为，来源单号文本显式保留（绝不猜链接） |
| 创建：来源不存在 | `1005` 冲突；不写库且**不消耗单据号** |
| 创建：来源未审核 / 已取消 / 已删除 | 冲突拒绝 |
| 创建：客户或仓库不一致 | 冲突拒绝 |
| 创建：来源单号快照冲突 | 冲突拒绝 |
| 创建：商品无出库证据 / 单位无法折算 | 冲突拒绝 |
| 创建：装箱单位 | 按 `UnitsPerPackage` 折算为基础单位 |
| 审核：未超数量 | 入库恰好一次（成本 / 流水 / 状态），重复审核拒绝 |
| 审核：跨单据超退 | 冲突拒绝，库存 / 流水 / 状态不变 |
| 审核：精确边界 | 允许 |
| 审核：重复商品行合计超限 | 冲突拒绝，不产生库存 / 流水 |
| 审核：后续行超限 | 整单回滚，第一行也不入库 |
| 审核：来源出库单客户被改（source edit） | 冲突拒绝 |
| 销审：释放可退容量 | 红字冲销保留历史，后续退货可再次审核 |
| 审核：负数量损坏证据 | 拒绝且不写库 |
| 授权：无身份 / 禁用 / 无菜单 | `2000` / `2002` / `2002` fail closed |
| 授权：撤销菜单 | 下一次请求立即收敛 |
| 数据范围：客户不在范围 | 按不存在拒绝 |
| 未链接历史退货 | 仍可提交审核入库 |
| 规则层纯函数 | 空 / 基础 / 装箱 / 其它单位的基础单位折算 |

既有 `src/ERP.UnitTests/InventoryMovementTests.cs` 的销售退货用例改经 `SalesReturnTestAuthorization` 注入真实业务员身份并播种权威来源出库单，保留原有全部数量 / 成本 / 冲销断言（不绕过鉴权、不放宽可见性）。

## 9. 真实 SQL Server 集成测试（`src/ERP.IntegrationTests/SalesReturnSourceSqlServerTests.cs`）

在 GUID 独占的 `NEWERP_AUTOTEST` 目标库上自包含播种主数据 / 来源出库单 / 退货单与真实授权账号，**直接执行真实控制器**（`SalesReturnController` + `SalesReturnSourceRules` + `InventoryService`），不复制一份测试专用实现：

| 场景 | 断言 |
|---|---|
| 无身份 / 无菜单 / 禁用身份（`[Theory]`） | 对应错误码；库存行数量、流水计数与单据状态不变 |
| 权威来源审核（4 + 6 == 10 精确边界） | 恰好一次入库、来源单号回填；超退（+1）冲突拒绝且库存 / 流水 / 状态不变 |
| 来源出库单客户被外部改动（source edit） | 冲突拒绝且不产生库存 / 流水 |
| 撤销菜单授权（permission revocation） | 下一次审核立即 `2002` 权限不足且不产生库存 / 流水 |
| 后续行超限 | 整单回滚，两商品均无库存 / 流水，状态保持已提交 |
| **两条独立连接竞争**：并发退货（各 6，合计 12 > 出库 10） | 只允许一方过账；已审核退货合计 ≤ 出库数量，库存恰好 6，仅一笔流水 |
| **两条独立连接竞争**：并发审核同一张退货单 | 只允许一方过账；库存恰好一次，仅一笔流水 |
| 目标库护栏 | 非专用目标在访问数据库之前被拒绝 |

**真实 SQL 夹具安全口径**

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，且 `IntegratedSecurity=true`；实例名 / 库名前缀 / 集成安全在**访问数据库之前**校验（`AssertDedicatedTarget`，含单元级护栏用例）。
- 每次运行只创建一个**全新 GUID 后缀库**；发现同名库已存在立即拒绝；**绝不 drop / reset / 复用**任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings` / `.env` / 生产凭据。

## 10. 未执行 / 限制

- 本任务 `validation_profile = safe`、`completion_mode = build`：交付门槛为 **Release build + 全量 `ERP.UnitTests`**；真实 SQL 集成用例在具备专用 localdb 的环境下运行（实现门槛内未运行），**构建通过不等于阶段验收通过**。
- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置。

## 11. 相关实现

- `src/ERP.Application/Services/SalesReturnSourceRules.cs`：授权 / 来源权威性 / 可退容量判定与基础单位折算（纯判定、只读）。
- `src/ERP.Api/Controllers/SalesReturnController.cs`：全部端点的实时授权 + 客户范围 + 「来源出库单行 → 退货单行」确定性锁序 + 可串行化事务 + 原子过账。
- `src/ERP.Api/Controllers/StockUnitConversion.cs`：新增销售退货明细的装箱 → 基础单位折算重载。
- 复用既有：`SalespersonDataScopeService`（ERP-097 数据范围）、`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`（角色 → 菜单）、`InventoryService`（ERP-009 记账与冲销）。
