# 销售出库规范入口与「省略出库单号」手工录入（ERP-478）

> 任务：`ERP-478`「Restore canonical sales stock-out form and server-numbered HTTP entry」（阶段 3 核心业务流程完整性）。
> 关联：`ERP-404`（旧单据写入门禁，旧族写路径 fail closed）、`ERP-376`（销售出库来源显式选择与剩余可发数量）、
> `ERP-477`（采购入库规范入口同源口径）、`ERP-343`（累计发货容量）、`ERP-359` / `ERP-367`（销售退货 / 装柜取消护栏）、
> `ERP-025`（库存记账与冲销）、`ERP-370`（实时授权与客户数据范围）。

## 1. 背景与问题证据

`modules-doc2.js` 早已注册规范 EF 主子表模块 `stock-out`（`api: '/api/stock-outs'`，含 ERP-376 来源选择器与明细列），
但 `bill-v2.js` 的 `BILL_CODE_MAP` 仍把菜单编码 `stock-out` 映射到旧存储过程族 `stock-out`。`app.js` 的 `renderPage`
先判 `BILL_CODE_MAP` 再判 `MODULES`，因此「销售出库」菜单实际打开的是旧库单体表（`Oid` 口径）单据页；
而该族的写路径已被 `ERP-404`（`LegacyBillMutationRules`）**整体停用（fail closed）**
（`BillProcController.Save` 在调用 `db_owner.sp_Biz_StockOut` 之前一律拒绝）—— 结果是「销售出库新建无法完成」：
页面能开，保存永远被拒，规范主子表表单与来源选择器根本无法从菜单进入。

同时规范入口自身也有缺陷：

- `StockOut.StockOutNo` 带显式 `[Required]`（`AllowEmptyStrings = false`），手工表单不提交出库单号时在**模型绑定阶段**
  就返回 HTTP 400 `{"errors":{"StockOutNo":["The StockOutNo field is required."]}}`，服务端权威单号根本没有机会生成；
- `StockOutController.Create` 在**单号预约之后**才做单位折算与来源链接校验（被拒绝的请求会白白消耗单号），
  且未把删除态 / 明细身份统一收归服务端（客户端可注入明细主键与删除态），`Details` 为 `null` 时会空引用（受控错误退化为 500）。

## 2. 口径与实现

### 2.1 菜单路由（只改路由，不改旧族读侧）

| 项 | 口径 |
|---|---|
| `bill-v2.js` `BILL_CODE_MAP` | **移除** `'stock-out': 'stock-out'` 映射（保留 ERP-478 注释说明原因）。旧族自身（`BILL_CONFIG['stock-out']` 的旧库单体表 `Oid` 读侧、旧导出、既有默认值端点）**保持只读可用**。 |
| `modules-doc2.js` `MODULES['stock-out']` | 规范 EF 主子表模块：`api: '/api/stock-outs'`、`canSubmit: true`、`detailKey: 'details'`、`detailFields: STOCK_OUT_DETAIL_FIELDS`，来源字段 `salesOrderId` 声明 `selector: 'stock-out-source'` + `nullable: true`。 |
| 规范 Id 口径 | 列表 / 详情 / 保存 / 回填 / 提交 / 审核 / 取消 / 流水全走规范 `Id`（`crud.js` 的 `mod.api` 与 `mod.api/{id}`）。**绝不**把旧 `Oid` 映射成规范 `Id`，也**绝不**启用任何旧写适配器。 |
| 页面呈现 | 继续复用既有业务单据页（`crud.js` 侧面板主子表表单）与基础资料抽屉（`type: 'ref'` 字段渲染），不新增页面 / 不重排布局。 |

### 2.2 允许省略出库单号（`Logistics.cs`）

`StockOut.StockOutNo` 的显式 `[Required]` 被移除（与 ERP-477 入库单号 / ERP-428 订单号 / ERP-474 询价单号同源口径）：
省略 / 空串照常绑定并进入已授权的 `StockOutController.Create`；数据库列仍为 NOT NULL（非空 CLR 引用类型 + 唯一索引），
长度仍受 `[MaxLength(50)]` 约束。单号 / 主键 / 状态 / 删除态一律由服务端权威覆盖（客户端伪造值不参与持久化）。

### 2.3 服务端权威身份与「拒绝先于编号」（`StockOutController`）

| 顺序 | 内容 |
|---|---|
| 1 | `NormalizeSalesOrderLink`（数字 `0` → `null`：表单「未选择来源」）+ `Details ??= new()` |
| 2 | `StockOutAuthorizationRules.EnsureCustomerAuthorizedAsync`（实时身份 / 账号状态 / 既有「销售出库」菜单 + ERP-097 权威客户范围） |
| 3 | `StockUnitConversion.NormalizeAsync`（装箱单位 → 基础单位）+ `Calculate`（合计）+ `StockOutOrderFulfillmentRules.ValidateLinkAsync`（来源权威性） |
| 4 | **只有全部校验通过之后**才：`entity.Id = 0`、`ApplyServerOwnedDetailIdentity`（明细主键 / 归属单据 / 删除态 / 时间戳一律服务端重置）、`GenerateAsync(DocumentType.StockOut)`、`Status = Pending`、`IsDeleted = false`、`CreatedAt = now`、`UpdatedAt = null`、落库 |

`Update` 同口径：已存 / 请求两侧客户范围校验 → 仅「待提交」可改 → 单据号 / 状态 / 删除态保持服务端值、明细身份重置。
被拒绝的请求：**零表头 · 明细 · 来源 · 状态变化、零单号消耗**。

### 2.4 来源显式选择与显式清空（`stock-out-source-picker.js`）

- 选择：复用 ERP-376 只读有界候选 / 详情接口（`/api/stock-outs/source-candidates[?…]`），按服务端权威**剩余可发数量**
  回填权威来源 Id / 客户与可发货商品行；保留用户所选仓库（绝不改写 `warehouseId`），不臆造价格 / 成本。
- 清空（ERP-478 新增）：弹窗提供「清空来源（未链接）」——只把来源引用字段置空（提交 `null`，服务端同样把原始 `0` 归一为
  `null`），**绝不删除用户已录入的明细行**、绝不臆造来源；非「待提交」状态一律只读（与后端一致）。
- 手工未链接：允许留空来源（省略 / `null` / `0`）保存，审核时不做累计校验（ERP-343 历史语义）。

### 2.5 保留的既有守卫（本任务不改口径）

| 守卫 | 口径 |
|---|---|
| 累计容量 | `ERP-343`：同一来源订单逐商品「已审核出库 + 本次 ≤ 授权数量」，行锁 `UPDLOCK/HOLDLOCK` + 可序列化事务串行化并发审核；取消后自动释放额度 |
| 库存充足 / 成本 | `ERP-025` / `ERP-009`：审核经 `IInventoryService` 按移动加权平均成本核减库存并写流水（幂等护栏 `CountActiveMovementsAsync`）；库存不足时整单回滚 |
| 记账与冲销 | 取消按红字流水冲销（重复取消被拒），历史无流水单据按基础单位原路恢复 |
| 来源失效 | 来源订单被取消 / 删除 / 取消审核 → 审核前 fail closed，零库存零流水 |
| 退货 / 装柜护栏 | `ERP-359`（存在生效的已审核销售退货引用时拒绝取消）、`ERP-367`（存在已审核装柜物理出运证据时拒绝取消）保持不变 |
| 授权与审计 | 实时身份 / 账号状态 / 既有「销售出库」菜单 + ERP-097 客户数据范围，全部路由复核；被拒绝方零副作用 |

## 3. 验证证据

| 验证 | 命令 | 结果 |
|---|---|---|
| Release 生成（分析器 / 警告即错误） | `dotnet build NEWERP.sln -c Release /p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true` | ✅ 0 警告 0 错误 |
| 全量单元测试 | `dotnet test src/ERP.UnitTests -c Release` | ✅ 7747 项通过（失败 0） |
| 新增单元测试 | `--filter "FullyQualifiedName~StockOutCanonicalEntryTests"` | ✅ 12 项通过 |
| 前端离线契约 | `node tests/stock-out-canonical-entry.browser.js` | ✅ 53 项断言通过 |
| 既有来源选择器回归 | `node tests/automation/stock-out-source-picker.test.js` | ✅ 91 项断言通过 |
| **真实隔离 SQL / HTTP 验收** | `dotnet test src/ERP.IntegrationTests -c Release --filter "FullyQualifiedName~StockOutCanonicalEntry"`（`ERP_AI_TEST_RUN=1` + `ASPNETCORE_ENVIRONMENT=Development`） | ✅ 15 项通过 = 11 项真实 HTTP / SQL 场景 + 4 项目标护栏 fail-closed（真实 Kestrel + 专用 localdb + 全新 GUID 库 `NEWERP_AUTOTEST_STOCKOUTCANONICAL_<guid>`） |

新增 / 更新测试：

- `src/ERP.UnitTests/StockOutCanonicalEntryTests.cs`（新增）：实体无显式必填 + 长度约束、省略 / 伪造单号与身份 / 状态 /
  时间戳 / 明细身份一律服务端覆盖、缺菜单 / 无身份 / 非法来源 / 越权客户零写入零占号、来源 `0` 与 `null` 均为未链接、
  修改显式清空来源后单号与状态不变、控制器接线顺序（校验先于 `GenerateAsync`）、菜单路由与模块 / 选择器接线契约。
- `src/ERP.UnitTests/StockOutControllerTests.cs`：空串出库单号 + 伪造身份 → 服务端权威覆盖；伪造出库单号被覆盖。
- `src/ERP.UnitTests/StockOutOrderFulfillmentTests.cs`：负数明细数量审核受控拒绝（零库存零流水、状态不变）、
  零数量明细（空草稿）审核不产生库存变动。
- `src/ERP.UnitTests/StockOutSourceSelectionTests.cs`：显式清空来源 → 未链接语义 + 单据号 / 状态 / 明细保留。
- `src/ERP.UnitTests/StockOutWriteScopeRegressionTests.cs`：缺既有「销售出库」菜单的映射业务员创建 fail closed 且零写入。
- `src/ERP.UnitTests/StockOutTestAuthorization.cs`：新增既有菜单 / 客户 / 特权与受限出库操作员播种脚手架（不新增权限模型）。
- `src/ERP.IntegrationTests/StockOutCanonicalEntrySqlServerTests.cs`（新增）：真实 HTTP + 真实隔离 SQL 场景与目标护栏
  （省略 / 伪造单号、主子表回填修改、手工未链接审核 + 取消红字冲销、显式来源剩余可发 / 超发拒绝 / 取消释放额度、
  非法来源与库存不足零写入零占号、无身份 401、缺菜单 2002、旧族写路径 fail closed）。
- `tests/stock-out-canonical-entry.browser.js`（新增）：真实浏览器端到端验收的**离线契约**部分（路由 / 模块 / 明细字段 /
  可空来源引用 / 规范 Id 接线 / 旧族分离 / 来源选择器清空语义）。

## 4. 环境受限说明（不声称整阶段验收）

- 本任务 `validation_profile = safe`、`completion_mode = build`、`browser_acceptance.required = false`：
  投递门禁为 **Release 生成 + ERP.UnitTests**（已通过：0 警告 0 错误、7747 项单元测试全通过）。
- 真实隔离 SQL / HTTP 用例已在 `StockOutCanonicalEntrySqlServerTests` 真实执行通过（11 项场景 + 4 项目标护栏 = 15 项）：目标为专用实例
  `(localdb)\NEWERP_AutoAcceptance` + 库名前缀 `NEWERP_AUTOTEST` + `Integrated Security=true` 的**全新 GUID 后缀库**
  （`NEWERP_AUTOTEST_STOCKOUTCANONICAL_<guid>`，发现同名库已存在立即拒绝，绝不 drop / reset / 复用）。夹具为被测 ERP.Api 进程注入
  **进程内临时随机 JWT 签名密钥**（不继承仓库内空白 `Jwt:Key`、不落盘、不写日志），登录 / 鉴权链路保持真实；
  测试账号仅为既有种子账号，未新增 / 未持久化凭据。
- **真实浏览器 UI 工作流未执行**（本环境无浏览器验收执行器，任务也未要求）：`browser_acceptance.required = false`，
  因此**不声称**浏览器端到端验收或整阶段验收通过；`tests/stock-out-canonical-entry.browser.js` 仅为离线契约验证，
  不构成「页面点击 → 真实 API 写入成功」的证据。真实 HTTP 写入证据来自上述 IntegrationTests（真实 Kestrel + 真实隔离库 + 真实 JWT 认证）。
- 未连接生产数据库、未使用生产凭据、未读取 `appsettings*.json` / `.env` / 生产连接串；未 drop / reset / 复用任何数据库。
- **验证产物落点**：本任务的全部验证日志与测试结果（`*.log` / `*.trx`）一律写入进程临时目录（`%TEMP%`，配合 `--results-directory`），
  **不在仓库工作区**留下任何测试结果文件：交付路径守卫只应看到 `allowed_paths` 内的改动，因此既不新增也不保留 `TestResults/*.trx`
  （上一次尝试的守卫失败原因正是仓库内残留的 `TestResults/*.trx`；该残留已移出工作区，本次已复验 `git status --porcelain` 仅含允许路径）。
- 未执行迁移 / 部署 / 任意 SQL；未新增权限、表 / 列 / 索引 / 菜单 / 角色 / 用户授权（仅复用既有菜单与既有接口）。

## 5. 相关实现

- `src/ERP.Domain/Entities/Logistics.cs`：`StockOut.StockOutNo` 去掉显式 `[Required]`（服务端权威单号口径）。
- `src/ERP.Api/Controllers/StockOutController.cs`：新增 / 修改的服务端权威身份覆盖与明细身份重置、拒绝先于编号与持久化。
- `src/ERP.Api/wwwroot/js/bill-v2.js`：移除 `stock-out` → 旧存储过程族映射（旧族读侧保留只读）。
- `src/ERP.Api/wwwroot/js/modules-doc2.js`：规范主子表模块声明（来源 `selector` + 可空引用）。
- `src/ERP.Api/wwwroot/js/stock-out-source-picker.js`：显式选择 + 显式清空来源（未链接）语义。
- 复用既有：`StockOutAuthorizationRules`（ERP-370）、`StockOutOrderFulfillmentRules`（ERP-343 / ERP-376）、
  `InventoryService`（ERP-025 / ERP-009）、`crud.js`（主子表表单与可空引用提交）。

