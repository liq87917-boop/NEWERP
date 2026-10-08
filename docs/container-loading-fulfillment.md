# 装柜清单衔接已审核预装柜单并防止累计超装（ERP-348）

## 1. 目标

让装柜清单通过既有 `ContainerLoadingList.PreLoadingId` 显式衔接来源预装柜单，并在保存 / 提交 / 审核时用权威的「累计已审核装柜数量」防止对同一预装柜单累计超装。只复用既有分层架构与既有单据流转（创建 / 更新 / 提交 / 审核 / 取消），不做报表、不做 Agent、不做界面视觉 / 样式 / 导出打磨，也不写库存 / 财务。

## 2. 口径

- **来源链接**：`ContainerLoadingList.PreLoadingId`（可空，刻意不建外键）。未链接（null）的历史单据**不做来源 / 商品 / 数量校验**，行为与改造前完全一致（仍校验当前账号身份 / 客户数据范围）。
- **权威来源**：预装柜单存在、未删除且 `Status = Approved`。已删除 / 未审核 / 已取消 / 已驳回 / 待提交 / 已提交 / 已完成的预装柜单均不能作为装柜依据。
- **商品数量口径**：装柜数量一律取 `ContainerLoadingDetail.Quantity`（商品基础单位件数），**绝不把箱数 `Cartons` 当件数**、**绝不推断出库 / 发货**。箱数 / 毛重 / 体积只做「不得为负」的字段校验，不参与累计上限判定。
- **来源授权数量**：预装柜单中该商品的明细行 `Quantity` **按商品聚合**（重复来源行先求和，不臆造、不猜行）。
- **已审核装柜数量**：以该预装柜单为来源、`IsDeleted = 0`、`Status = Approved` 的装柜清单明细数量按商品聚合。
- **上限判定**：仅当链接权威时，逐商品比较 `已审核数量（不含本单） + 本单数量 ≤ 预装柜单授权数量 + 0.0001 容差`；本单内重复商品行先聚合，跨商品不合计。

## 3. 链接权威性判定（无效链接 fail closed 拒绝）

`ContainerLoadingFulfillmentRules.ValidateLinkAsync`（创建 / 更新 / 提交）与 `ValidateApprovalAsync`（审核）共用同一份权威判定：显式链接（`PreLoadingId` 非空）必须构成权威来源，否则抛 `BusinessException` 拒绝保存 / 履约；未链接（null）的历史单据不校验来源。以下任一情况视为无效链接：

| 判定项 | 权威要求 |
|---|---|
| 来源 Id | 必须为正整数（非正 / 缺失拒绝） |
| 来源可用性 | 存在、未删除且 `Status = Approved`（已删除 / 已取消 / 未审核拒绝） |
| 商品身份 | 每条装柜明细必须指定商品，且商品存在、未删除 |
| 商品在来源中 | 来源明细必须包含该商品（商品不在来源拒绝） |
| 数量 | `Quantity` 必须为正数（非正拒绝） |
| 箱数 / 毛重 / 体积 | `Cartons` / `Weight` / `Volume` 不得为负 |

身份 / 菜单 / 客户数据范围：装柜清单操作要求当前账号具备既有「装柜清单（loading-list）」菜单授权（特权账号继承既有全部访问），且客户数据范围必须包含本单 `CustomerId`；预装柜单取消要求「预装柜单（pre-loading）」菜单授权（来源无客户字段，不适用客户范围）。**不新增权限、不扩权、绝不臆造客户 / 销售归属。**

## 4. 审核累计上限与并发串行化

`ContainerLoadingListController.Approve` 按以下顺序执行，整体包在 `IsolationLevel.Serializable` 事务内：

1. 按主键读取本单头（用于取 `PreLoadingId`）；
2. 开启可串行化事务；
3. 对来源预装柜单行执行 `SELECT Id FROM db_owner.ContainerPreLoadings WITH (UPDLOCK, HOLDLOCK) WHERE Id = @id` 取得更新锁，把**同一来源**的并发审核与来源取消串行化（未链接不加锁；非关系型提供程序跳过）；
4. 锁内重新加载本单（含明细），校验状态仍为 `Submitted`（同单重复审核 / 并发审核在锁内被状态门拒绝）；
5. `ContainerLoadingFulfillmentRules.ValidateApprovalAsync` 判定链接权威性并做累计上限校验；
6. 置为已审核、提交事务。

任一步失败即回滚：来源 / 目标状态与明细都保持不变。已取消 / 已驳回 / 待提交 / 已提交的装柜清单不计入已审核数量，取消（`Status = Cancelled`）后自动释放剩余额度，且**不写库存 / 财务**。

`ContainerPreLoadingController.Cancel` 同样在可串行化事务内先对预装柜单行加 `UPDLOCK/HOLDLOCK`，再调用 `ValidateSourceCancellationAsync`：存在「以本单为来源、未删除、已审核」的装柜清单时拒绝取消；已取消 / 已驳回 / 待提交 / 已提交 / 已删除的装柜清单不算有效引用，不阻断取消。并发下「装柜审核」与「来源取消」使用同一把行锁，只能成功其一。

## 5. 错误信息（API 直接返回）

累计超限时通过既有 `BusinessException.RuleConflict` 抛给既有异常中间件，消息含商品与数量明细，例如：

- 「商品 [X] 累计装柜数量 11 超过预装柜单授权数量 10（已审核 6 + 本次 5）」

无效显式链接在保存 / 审核前抛 `BusinessException`，消息说明具体失效原因（来源不存在 / 已删除 / 未审核 / 商品缺失或不在来源 / 数量非正 / 箱数·毛重·体积为负）。

## 6. 不改写项

- 不重写预装柜单原始数量 / 明细、不改写历史装柜清单、不新增表列 / 外键 / schema；
- 不新增报表、Agent、权限、菜单；销售员 / 客户数据范围保持现状；
- 不写库存 / 库存流水 / 财务 / 结算记录；不推断出库 / 发货。

## 7. 测试

- 单元测试：`src/ERP.UnitTests/ContainerLoadingFulfillmentTests.cs`（内存库，覆盖链接权威性、部分 / 满量批次、重复行聚合、累计超限、取消释放、来源取消护栏、无身份 / 无菜单 / 越客户范围、权限无扩展）。
- 集成测试：`src/ERP.IntegrationTests/ContainerLoadingFulfillmentSqlServerTests.cs`（NEWERP_AUTOTEST 护栏，真实 SQL Server，覆盖非权威链接、累计、重复行、取消释放、箱数不当作件数、同源并发审核与来源取消串行化）。

## 8. 真实 SQL 夹具安全口径

- 目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`，库名前缀 `NEWERP_AUTOTEST`，且 `Integrated Security=true`，护栏在**任何数据库访问之前**校验失败即中止；
- 每次运行创建**全新的 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 不读取 `appsettings` / `.env` / 生产凭据；连接串只来自进程环境变量或专用 localdb 默认值；
- 测试种子一律使用 SQL Server 自增主键（不显式指定 Id），保留完整日志输出。
