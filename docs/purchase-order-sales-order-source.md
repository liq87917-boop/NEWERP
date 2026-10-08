# 采购订单归属来源销售订单显式选择（ERP-393）

## 1. 目标与范围

采购订单表单中的「归属销售订单 ID / 归属销售订单号」此前只能手工填写，容易填错、也容易与归属客户脱节
（既没有有界候选，也没有权威来源解析）。ERP-393 在**不新增表 / 列 / 菜单 / 权限 / 用户授权**、
**不提供匿名或管理员降级**的前提下，为该字段提供：

1. **有界只读候选接口**：只返回当前账号客户数据范围内「已审核、未删除、未取消」的销售订单；
2. **精确已存储来源解析接口**：按采购订单持久化的 `OwningSalesOrderId` 精确解析，显式标注
   未关联 / 已关联 / 来源已取消 / 来源未审核 / 来源不可用；
3. **前端显式选择器**：接入既有采购订单新增 / 编辑 / 详情表单，按**权威来源**回填归属销售订单
   Id / 单号与归属客户；归属客户变更即失效、陈旧响应不回填、可显式断开。

**最终保存仍以服务端为权威**：`PurchaseSalesOrderLinkRules.ApplyLinkAsync` 在行锁 + 可串行化事务内
按 Id 精确解析来源、复核身份 / 菜单 / 客户数据范围 / 权威客户一致性与商品 / 单位兼容性，
伪造或失效的来源一律 fail closed（候选选择不等于授权）。

边界：本功能**不要求币种一致**（币种仅供展示，不做汇率换算），**不改写**采购订单的原始商业币种 / 汇率 /
单价 / 金额 / 明细，不改写销售订单与客户主数据，不动库存 / 流水 / 财务，也不执行任何迁移 / 生产 SQL / 部署。

## 2. 接口

| 方法 | 路由 | 说明 |
|---|---|---|
| GET | `/api/purchase-orders/sales-order-source-candidates?customerId=&keyword=&page=&pageSize=` | 有界分页候选：`PurchaseOrderSalesOrderSourceCandidatePageDto`（可选精确归属客户过滤） |
| GET | `/api/purchase-orders/{id}/sales-order-source` | 已存储来源只读解析（详情 / 重开）：`PurchaseOrderSalesOrderSourceViewDto` |

两条路由都**先**做实时身份（缺失 / 已删除按未认证拒绝）、账号启用状态（禁用按权限不足拒绝）、
既有「采购订单」（`purchase-order`）菜单授权（撤销最后一个菜单立即收敛）与 `SalespersonDataScopeService`
（ERP-097 唯一权威口径）客户数据范围校验；解析接口还按「显式归属客户 + 权威归属销售订单客户」两侧复核本单范围。

### 候选口径

- 只返回**当前账号客户数据范围之内**、`Status == Approved`、`!IsDeleted` 的销售订单：
  资格判定**复用** `PurchaseSalesOrderLinkRules.IsEligibleNewSource`（未审核 / 已取消 / 已驳回 / 已完成一律不返回）；
- 归属客户档案不存在 / 已删除的行**保留但显式标记** `Eligible = false` + 原因（绝不臆造客户）；
- **不强制币种一致**：`Currency` 只作展示字段，既不作为可选性门槛，也不做汇率换算；
- 关键字（订单号 / 客户 PO / 合同号）、页码、每页条数**先归一化再计数**，分页有界（默认 20、上限 100、页码上限 10000）；
- **客户数据范围先于计数 / 分页下推到数据库**，绝不「先查全量再内存过滤」；
- 受限账号未映射为业务员（采购操作员）时与列表口径一致 fail closed。

### 已存储来源解析口径

- 未关联（历史）→ `Linked = false` + 「未关联…」（绝不回填）；
- 来源有效（已审核）→ 订单号 / 日期 / 客户 / 币种 / 状态 + `EligibleForNewLink = true`；
- 来源已取消 / 未审核 → `Unavailable = false` 但 `EligibleForNewLink = false` + 「只读保留」标注（历史原样可读）；
- 来源已删除 / 无法解析 / **不在当前账号客户数据范围内** → `Unavailable = true`，
  且 `OrderNo` / `OrderDate` / `CustomerId` / `CustomerName` / `Currency` / `Status` **一律为空**，
  **绝不披露范围外（foreign）来源的客户 / 订单字段**。

## 3. 前端接线

- 脚本：`wwwroot/js/purchase-order-sales-order-source.js`（`index.html` 注册，加载于 `crud.js` 之后）；
- 声明式入口：`modules-doc.js` 的采购订单字段 `owningSalesOrderId` 标记
  `selector: 'purchase-order-sales-order-source'`；脚本在加载时**包裹既有 `fieldHtml` / `openForm`**，
  只为该字段追加「选择来源销售订单」按钮（不影响其它模块 / 字段）；
- 回填字段（`POS_WRITTEN_FIELDS`）：`owningSalesOrderId` / `owningSalesOrderNo` / `owningCustomerId` /
  `owningCustomerName` —— 均为**权威来源派生**；**绝不**触碰币种 / 汇率 / 单价 / 金额 / 明细；
- **归属客户变更即失效**：`owningCustomerId`（或搜索框）发生 input / change，或 `selectRef` 选中归属客户时，
  先前的来源选择立即失效并清除，在途候选响应一并作废；
- **陈旧响应不回填**：候选请求带单调递增序号，客户 / 关键字变化后旧响应（含旧失败）一律丢弃；
- **显式断开**：仅「待提交」可断开（服务端同样 fail closed），断开只清空来源 Id / 单号，保留用户已填归属客户；
- 失败（网络 / 权限 / 服务端）保留表单既有输入并显式提示，不清空用户已填内容。

## 4. 测试与验收

| 层级 | 文件 | 覆盖 |
|---|---|---|
| 单元（内存库） | `src/ERP.UnitTests/PurchaseOrderSalesOrderSourceTests.cs`（18 例） | 归一化、资格口径复用、候选只含范围内「已审核 / 未删除 / 未取消」、可选客户过滤与关键字、分页与总数、归属客户缺失显式不可选、无身份 / 无菜单 / 未映射业务员 fail closed、已存储来源未关联 / 有效 / 已取消 / 已删除、范围外**绝不披露**、接口与前端接线契约 |
| 前端（可执行） | `tests/purchase-order-sales-order-source.test.js`（96 断言） | 可用性过滤、denied candidate、只回填归属来源 / 归属客户（不含金额 / 汇率 / 数量）、归属客户变更即失效、陈旧响应不回填、重开保留历史链接、不可用不泄露、显式断开、失败保留表单、create / edit / reopen / customer switch / stale / unlink / error 行为与接线契约 |
| 集成（真实 SQL） | `src/ERP.IntegrationTests/PurchaseOrderSalesOrderSourceSqlServerTests.cs` | 真实权限（missing / no-menu / disabled）、own / foreign / revoked 收敛、cancelled / unapproved / deleted 标注、最终保存权威派生与 save denial（伪造 / 已取消）、**两条独立连接竞争**：并发「取消来源」vs「新建链接」与并发「取消来源」vs「修改链接」各只成功其一；目标护栏拒绝非专用目标 |

- 交付门（safe）：Release 构建通过 + `ERP.UnitTests` 全量通过；前端行为测试
  `node tests/purchase-order-sales-order-source.test.js` 通过。
- **构建完成不等于阶段验收**：SQL 集成测试只有在受控 localdb 上真实执行通过才算验收证据；
  本次若未执行真实 SQL 或浏览器验收，应以**准确 blocker** 说明（不臆造通过）。

## 5. 安全口径

- 真实 SQL 只允许指向精确实例 `(localdb)\NEWERP_AutoAcceptance` + `Integrated Security` +
  全新 GUID 归属库名（前缀 `NEWERP_AUTOTEST`）；连接串只来自进程环境变量或专用 localdb 默认值，
  **绝不读取** `appsettings*.json` / `.env*` / 生产凭据；发现同名库已存在立即拒绝，绝不 drop / reset / 复用。
- 使用**既有**登录身份与既有菜单授权，**不新增用户授权**，也不提供匿名 / 管理员兜底。
- **保留**既有库存来源单据审计与原始失败日志（本功能不删除任何历史单据 / 日志 / 审计证据）。
- 浏览器 / 真实 UI 验收在 `browser_acceptance.required = false` 时不做；不执行部署、生产数据库变更或破坏性数据操作。
