# 采购入库规范入口与「省略入库单号」手工录入（ERP-477）

> 任务：`ERP-477`「Restore canonical procurement stock-in form and server-numbered HTTP entry」（阶段 3 核心业务流程完整性）。
> 关联：`ERP-404`（旧单据写入门禁，旧族写路径 fail closed）、`ERP-375`（采购入库来源显式选择与剩余可收数量）、
> `ERP-474`（询价单规范入口同源口径）、`ERP-342`（累计收货容量）、`ERP-033`（入库成本来源）、`ERP-025`（库存记账与冲销）。

## 1. 背景与问题证据

`modules-doc2.js` 早已注册规范 EF 主子表模块 `stock-in`（`api: '/api/stock-ins'`，含 ERP-375 来源选择器与明细列），
但 `bill-v2.js` 的 `BILL_CODE_MAP` 仍把菜单编码 `stock-in` 映射到旧存储过程族 `stock-in`。`app.js` 的 `renderPage`
先判 `BILL_CODE_MAP` 再判 `MODULES`，因此「采购入库」菜单实际打开的是旧库单体表（`Oid` 口径）单据页；
而该族的写路径已被 `ERP-404`（`LegacyBillMutationRules`）**整体停用（fail closed）**
（`BillProcController.Save` 在调用 `db_owner.sp_Biz_StockIn` 之前一律拒绝）—— 结果是「采购入库新建无法完成」：
页面能开，保存永远被拒，规范主子表表单与来源选择器根本无法从菜单进入。

同时规范入口自身也有缺陷：

- `StockIn.StockInNo` 带显式 `[Required]`（`AllowEmptyStrings = false`），手工表单不提交入库单号时在**模型绑定阶段**
  就返回 HTTP 400 `{"errors":{"StockInNo":["The StockInNo field is required."]}}`，服务端权威单号根本没有机会生成；
- `StockInController.Create` / `Update` **未**把删除态 / 明细身份统一收归服务端（客户端可注入明细主键与删除态），
  且 `Details` 为 `null` 时会空引用（受控错误退化为 500）。

## 2. 口径与实现

### 2.1 菜单路由（只改路由，不改旧族读侧）

| 项 | 口径 |
|---|---|
| `bill-v2.js` `BILL_CODE_MAP` | **移除** `'stock-in': 'stock-in'` 映射（保留 ERP-477 注释说明原因）。旧族自身（`BILL_CONFIG['stock-in']` 的旧库单体表 `Oid` 读侧、旧导出、既有默认值端点）**保持只读可用**。 |
| `modules-doc2.js` `MODULES['stock-in']` | 规范 EF 主子表模块：`api: '/api/stock-ins'`、`canSubmit: true`、`detailKey: 'details'`、`detailFields: STOCK_IN_DETAIL_FIELDS`，来源字段 `purchaseOrderId` 声明 `selector: 'stock-in-source'` + `nullable: true`。 |
| 规范 Id 口径 | 列表 / 详情 / 保存 / 回填 / 提交 / 审核 / 取消 / 流水全走规范 `Id`（`crud.js` 的 `mod.api` 与 `mod.api/{id}`）。**绝不**把旧 `Oid` 映射成规范 `Id`，也**绝不**启用任何旧写适配器。 |
| 页面呈现 | 继续复用既有业务单据页（`crud.js` 侧面板主子表表单）与基础资料抽屉（`type: 'ref'` 字段渲染），不新增页面 / 不重排布局。 |

### 2.2 允许省略入库单号（`Logistics.cs`）

`StockIn.StockInNo` 的显式 `[Required]` 被移除（与 `ERP-428` 订单号 / `ERP-474` 询价单号同源口径）：
省略 / 空串照常绑定并进入已授权的 `StockInController.Create`；数据库列仍为 NOT NULL（非空 CLR 引用类型 + 唯一索引），
长度仍受 `[MaxLength(50)]` 约束。单号 / 主键 / 状态 / 删除态一律由服务端权威覆盖（客户端伪造值不参与持久化）。

### 2.3 服务端权威身份与「拒绝先于编号」（`StockInController`）

| 顺序 | 内容 |
|---|---|
| 1 | `NormalizePurchaseOrderLink`（数字 `0` → `null`：表单「未选择来源」）+ `Details ??= new()` |
| 2 | `StockInAuthorizationRules.EnsureAuthorizedAsync`（实时身份 / 禁用 / 既有「采购入库」菜单 / 供应商 · 仓库可用 / 来源归属客户范围） |
| 3 | `StockUnitConversion.NormalizeAsync`（装箱单位 → 基础单位）+ `Calculate`（合计）+ `StockInOrderFulfillmentRules.ValidateLinkAsync`（来源权威性） |
| 4 | **只有全部校验通过之后**才：`entity.Id = 0`、`ApplyServerOwnedDetailIdentity`（明细主键 / 归属单据 / 删除态 / 时间戳一律服务端重置）、`GenerateAsync(DocumentType.StockIn)`、`Status = Pending`、`IsDeleted = false`、`CreatedAt = now`、`UpdatedAt = null`、落库 |

`Update` 同口径：来源归属两侧校验 → 仅「待提交」可改 → 单据号 / 状态 / 删除态保持服务端值、明细身份重置。
被拒绝的请求：**零表头 · 明细 · 来源 · 状态变化、零单号消耗**。

### 2.4 来源显式选择与显式清空（`stock-in-source-picker.js`）

- 选择：复用 ERP-375 只读有界候选 / 详情接口（`/api/stock-ins/source-candidates[?…]`），按服务端权威**剩余可收数量**
  回填权威来源 Id / 供应商与可收货商品行；保留用户所选仓库（绝不改写 `warehouseId`），不臆造成本 / 单价。
- 清空（ERP-477 新增）：弹窗提供「清空来源（未链接）」——只把来源引用字段置空（提交 `null`，服务端同样把原始 `0` 归一为
  `null`），**绝不删除用户已录入的明细行**、绝不臆造来源；非「待提交」状态一律只读（与后端一致）。
- 手工未链接：允许留空来源（省略 / `null` / `0`）保存，审核时不做累计校验（ERP-342 历史语义）。

### 2.5 保留的既有守卫（本任务不改口径）

| 守卫 | 口径 |
|---|---|
| 成本来源 | 审核仍走 `ERP-033 PurchaseStockInCostSource`（来源订单唯一兼容明细行单价 → 折算基础单位 → 非占位汇率换算），语义不明确时回退移动加权平均，绝不臆造成本 |
| 累计容量 | `ERP-342`：同一来源订单逐商品「已审核入库 + 本次 ≤ 授权数量」，行锁 `UPDLOCK/HOLDLOCK` + 可序列化事务串行化并发审核 |
| 记账与冲销 | `ERP-025`：审核经 `IInventoryService` 写库存 + 流水（幂等护栏 `CountActiveMovementsAsync`）；取消按红字流水冲销（重复取消被拒），历史无流水单据原路冲回 |
| 来源失效 | 提交后来源被取消 / 删除 / 取消审核 → 审核前 fail closed，零库存零流水 |
| 退货耦合 | 存在仍然生效的已审核采购退货引用时，取消来源入库单被拒绝（`ReturnSourceCancellationRules`） |

## 3. 验证证据

| 证据 | 命令 | 结果 |
|---|---|---|
| Release 生成（含分析器 / 警告即错误） | `dotnet build NEWERP.sln -c Release` | ✅ 0 警告 0 错误 |
| 采购入库单元测试 | `dotnet test src/ERP.UnitTests -c Release --filter "FullyQualifiedName~StockIn"` | ✅ 106 项通过 |
| 全量单元测试 | `dotnet test src/ERP.UnitTests -c Release` | ✅ 7729 项通过（失败 0） |
| 前端规范入口契约（离线） | `node tests/stock-in-canonical-entry.browser.js` | ✅ 53 项断言通过 |
| 既有来源选择器回归 | `node tests/automation/stock-in-source-picker.test.js` | ✅ 89 项断言通过 |
| 前端回归（全部 `tests/**/*.test.js`） | `node tests/<每个用例>.js` | ✅ 全部 exit 0 |
| **真实隔离 SQL / HTTP 验收** | `dotnet test src/ERP.IntegrationTests -c Release --filter "FullyQualifiedName~StockInCanonicalEntrySqlServerTests"`（`ERP_AI_TEST_RUN=1` + `ASPNETCORE_ENVIRONMENT=Development`） | ✅ 11 项通过 |
| 目标护栏 fail-closed | `--filter "FullyQualifiedName~StockInCanonicalEntryTargetGuardTests"` | ✅ 4 项通过（不访问数据库） |

真实隔离 SQL / HTTP 验收（`(localdb)\NEWERP_AutoAcceptance` + 全新 GUID 库
`NEWERP_AUTOTEST_STOCKINCANONICAL_<guid>` + `Integrated Security=true`，拒绝既有库 / 错误实例 / 非集成安全，
不 drop / reset / 复用任何库）覆盖：

- **省略入库单号**：真实 HTTP 新增成功（不再 400「StockInNo field is required」）→ 服务端权威 `RK…` 单号 + 规范 `Id` 落库、
  明细 1 行、状态 `Pending`、未链接来源 `NULL`；列表按单号可检索到同一条规范 `Id`。
- **伪造入库单号**：被服务端权威单号覆盖（不落伪造值）。
- **主子表保存 / 回填 / 修改**：详情回填明细，修改数量后回读一致（合计重算）。
- **手工未链接入库**：提交 → 审核过账**恰好一次**（1 条入库流水；重复审核受控拒绝且零重复流水）→ 取消产生红字冲销
  （2 条流水、Σ 金额 = 0、库存归零）。
- **显式来源**：ERP-375 候选 / 详情给出权威剩余可收 10 → 按部分数量 4 保存并审核（来源单价 5 → 流水金额 20）→
  超收 7 被受控拒绝（零库存 / 零流水、状态保持已提交）→ 取消第一张释放额度后第二张过账（库存 7）。
- **零副作用拒绝**：负数来源（1001）/ 供应商不存在（1002）/ 未审核来源（1004）一律受控拒绝，且入库单 / 明细 / 流水计数
  与采购入库单号序列零变化（被拒绝的请求绝不消耗单号）。
- **权限**：无身份 → HTTP 401；无既有「采购入库」菜单账号 → 2002（权限不足）且零写入。
- **旧族分离**：`POST /api/v2/bills/stock-in/save`（ERP-404 门禁）fail closed（code ≠ 0），同一进程内规范入口
  `POST /api/stock-ins` 正常写入。
- **夹具真实认证**：被测 ERP.Api 进程由夹具注入**进程内临时随机 JWT 签名密钥**（不继承仓库内空白 `Jwt:Key`、
  不落盘、不写日志），登录 / 鉴权链路保持真实；测试账号仅为既有种子账号，未新增 / 未持久化任何凭据。

新增 / 更新测试：

- `src/ERP.UnitTests/StockInCanonicalEntryTests.cs`（新增）：实体无显式必填 + 长度约束、省略 / 伪造单号与身份 / 状态 /
  时间戳 / 明细身份一律服务端覆盖、缺菜单 / 无身份 / 非法来源 / 供应商不存在零写入零占号、来源 `0` 与 `null` 均为未链接、
  修改显式清空来源后单号与状态不变、控制器接线顺序（校验先于 `GenerateAsync`）、菜单路由与模块 / 选择器接线契约。
- `src/ERP.UnitTests/StockInControllerTests.cs`：空串入库单号 + 伪造身份 → 服务端权威覆盖。
- `src/ERP.UnitTests/StockInOrderFulfillmentTests.cs`：负数明细数量审核受控拒绝（零库存零流水、状态不变）、
  零数量明细（空草稿）审核不产生库存变动。
- `src/ERP.UnitTests/StockInSourceSelectionTests.cs`：显式清空来源 → 未链接语义 + 单据号 / 状态 / 明细保留 + 只读候选不受影响。
- `src/ERP.IntegrationTests/StockInCanonicalEntrySqlServerTests.cs`（新增）：上述真实 HTTP + 真实隔离 SQL 场景与目标护栏。
- `tests/stock-in-canonical-entry.browser.js`（新增）：真实浏览器端到端验收的**离线契约**部分（路由 / 模块 / 明细字段 /
  可空来源引用 / 规范 Id 接线 / 旧族分离 / 来源选择器清空语义）——真实浏览器 + 真实测试库 + 真实授权身份的端到端场景由
  上述 IntegrationTests（真实 Kestrel + 真实隔离库 + 真实 JWT 认证）执行，本文件绝不伪造成功业务写入。

## 4. 环境受限说明（不声称整阶段验收）

- 本任务 `validation_profile = safe`、`completion_mode = build`、`browser_acceptance.required = false`：
  投递门禁为 **Release 生成 + ERP.UnitTests**。
- 真实隔离 SQL / HTTP 用例已在受控 `(localdb)\NEWERP_AutoAcceptance` + 全新 GUID `NEWERP_AUTOTEST` 库上真实执行通过。
- **真实浏览器 UI 工作流未执行**（本环境无浏览器验收执行器，任务也未要求）：`browser_acceptance.required = false`，
  因此**不声称**浏览器端到端验收或整阶段验收通过；`tests/stock-in-canonical-entry.browser.js` 仅为离线契约验证，
  不构成「页面点击 → 真实 API 写入成功」的证据。
- 未连接生产数据库、未使用生产凭据、未读取 `appsettings*.json` / `.env` / 生产连接串；未 drop / reset / 复用任何数据库。
- 未执行迁移 / 部署 / 任意 SQL；未新增权限、表 / 列 / 索引 / 菜单 / 角色 / 用户授权（仅复用既有菜单与既有接口）。

## 5. 相关实现

- `src/ERP.Domain/Entities/Logistics.cs`：`StockIn.StockInNo` 去掉显式 `[Required]`（服务端权威单号口径）。
- `src/ERP.Api/Controllers/StockInController.cs`：新增 / 修改的服务端权威身份覆盖与明细身份重置、拒绝先于编号与持久化。
- `src/ERP.Api/wwwroot/js/bill-v2.js`：移除 `stock-in` → 旧存储过程族映射（旧族读侧保留只读）。
- `src/ERP.Api/wwwroot/js/modules-doc2.js`：规范主子表模块声明（来源 `selector` + 可空引用）。
- `src/ERP.Api/wwwroot/js/stock-in-source-picker.js`：显式选择 + 显式清空来源（未链接）语义。
- 复用既有：`StockInAuthorizationRules`（ERP-352）、`StockInOrderFulfillmentRules`（ERP-342 / ERP-375）、
  `PurchaseStockInCostSource`（ERP-033）、`InventoryService`（ERP-025 / ERP-009）、`crud.js`（主子表表单与可空引用提交）。

