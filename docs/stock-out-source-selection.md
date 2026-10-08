# 销售出库来源选择与剩余可发数量（ERP-376）

## 1. 目标

让普通操作员在**业务界面**上完成「销售出库单 → 来源销售订单」的**显式选择**：来源必须是当前账号客户数据范围之内
「已审核、未删除」的销售订单，选择后由服务端权威投影回填来源 Id / 单号 / 权威客户与**逐商品剩余可发数量**，
操作员只录入**正数部分发货数量**。ERP-343 已提供「显式 `SalesOrderId` + 累计发货容量」护栏（审核时 fail closed），
但业务界面此前只能手工填写来源 Id，无法看到权威的「还能发多少」。

本任务只新增**只读有界候选 / 详情投影**与**表单选择入口**：不新增表 / 列 / 菜单 / 权限 / 用户授权，
不改发货容量口径（ERP-343）、不改取消护栏（ERP-359 退货引用 / ERP-367 装柜引用）、不臆造价格、不写库存，
也**绝不从销售退货恢复发货容量**。

## 2. 口径

- **候选来源**：`SalesOrder` 中 `IsDeleted = 0` 且 `Status = Approved`，且其权威客户落在当前账号的
  客户数据范围（ERP-097 `SalespersonDataScopeService.FilterByCustomer`，在取数 / 计数**之前**下推到数据库）。
  **范围外客户 / 未审核 / 已取消 / 已驳回 / 已删除的来源一律不返回**，范围外客户按「不存在」处理（不泄露单据归属）。
- **聚合键**：`(来源销售订单 Id, 商品 Id)`。
- **授权数量（基础单位）**：与 ERP-343 `TryBaseUnitQuantity` 同口径 —— 订单明细单位等于商品基础单位（`Unit`）
  直接使用；等于装箱单位（`PackageUnit`）按 `UnitsPerPackage` 折算；其它单位 / 缺失基础单位 / 无效装箱数 →
  **单位未知**（不可用）。**同商品出现多条明细 → 授权数量不唯一**（不可用，绝不猜测、绝不合计）。
- **已发货数量（基础单位）**：`StockOuts.SalesOrderId = 来源订单`、`IsDeleted = 0`、`Status = Approved` 的
  出库明细数量按商品直接合计（与 ERP-343 `EnforceCumulativeAsync` 同一口径；出库明细在保存 / 审核前已折算基础单位）。
  **待提交 / 已提交的预留不计入**（绝不当作已过账发货），已取消 / 已驳回 / 已删除的出库同样不计入，因此取消后自动释放剩余可发数量。
- **剩余可发数量** = `授权数量 − 已发货数量`，下限 0。
- **销售退货绝不恢复容量**：销售退货（`SalesReturn`）只影响库存与退货证据，本投影**不读取、不抵扣、不回加**退货数量，
  剩余可发数量仍严格按「已审核出库」口径计算。
- **不可用**：剩余可发 ≤ 0（容差 `0.0001`，与 ERP-343 同口径）、授权数量 ≤ 0、重复 / 歧义明细、
  单位未知、商品主数据缺失、负数量 → `available = false` + 服务端权威原因；界面**不得**将其作为可选项。
- **关键字**：只在订单号 / 客户名称 / 商品名称 / 规格内做有界匹配（大小写不敏感，长度截断 100），
  绝不用于推断来源；空串 = 不过滤。
- **有界**：候选先按客户数据范围收窄，再取最近 `MaxCandidateScan = 500` 张销售订单，最后返回至多
  `MaxCandidateTake = 200` 条（`take <= 0` 取默认 50；服务端钳制，绝不无界拉取）。
- **不暴露无关订单字段**：候选 / 详情只含 `销售订单 Id / 单号 / 日期 / 客户 Id / 客户名称` 与
  `商品 Id / 商品名称 / 规格 / 基础单位 / 授权数量 / 已发货数量 / 剩余可发数量 / 可用性 / 原因`，
  绝不返回币种、汇率、金额、贸易条款、唛头等无关字段。

## 3. 接口（只读、有界）

| 方法 | 路由 | 说明 |
|---|---|---|
| GET | `/api/stock-outs/source-candidates?customerId=&keyword=&take=` | 可发货来源候选（逐商品行）：`StockOutSourceCandidateDto` 列表 |
| GET | `/api/stock-outs/source-candidates/{salesOrderId}` | 来源详情（表头 + 逐商品剩余可发行）：`StockOutSourceDetailDto` |

`StockOutSourceCandidateDto` 字段：`salesOrderId / orderNo / orderDate / customerId / customerName /
productId / productName / spec / baseUnit / authorizedBaseQuantity / shippedBaseQuantity /
remainingBaseQuantity / available / unavailableReason`。

`StockOutSourceDetailDto` 字段：表头（`salesOrderId / orderNo / orderDate / customerId / customerName`）+
`available` / `unavailableReason` + `lines`（同上候选行）。

排序：可用候选优先，其后按剩余可发数量、来源订单 Id 倒序、商品 Id 升序（稳定且确定）。

**授权口径**：既有的「销售出库」（`stock-out`）菜单 **与** 既有的「销售订单」（`sales-order`）菜单都必须
实时具备（`StockOutAuthorizationRules.EnsureSourceMenuAuthorizedAsync`），再叠加 ERP-097 权威客户数据范围；
两者都**先于**计数 / 取数判定。不新增用户授权，也不提供匿名 / 管理员降级（撤销任一授权后下一次请求立即收敛）。

**错误口径**

| 场景 | 结果 |
|---|---|
| 来源不存在 / 不在客户数据范围 / 已删除 | `1002` 按「不存在」拒绝（不泄露归属） |
| 来源未审核 / 已取消 / 已驳回 | `1005` 冲突拒绝（必须重新显式选择来源） |
| 来源 Id ≤ 0 | `1004` 参数错误 |
| 无身份 | `2000` 未认证 |
| 账号不存在 / 已删除 | `2000` 未认证 |
| 账号禁用 / 缺既有「销售出库」菜单 / 缺既有「销售订单」菜单 | `2002` 权限不足 |

候选与详情是**只读投影**：不落库、不改单据 / 库存 / 流水、不消耗单据号、不新增表 / 列 / 菜单 / 权限 / 用户授权，
也绝不臆造价格。

## 4. 业务表单选择器（`src/ERP.Api/wwwroot/js/stock-out-source-picker.js`）

- 入口（两个出库业务界面都覆盖）：
  1. **通用模块表单**（`modules-doc2.js` 的销售出库字段 `salesOrderId` 声明 `selector: 'stock-out-source'`）：
     选择器脚本在加载时**包裹既有 `fieldHtml` / `openForm`**，为带该标记的字段追加
     「选择来源销售订单（按剩余可发数量过滤）」按钮（只影响该字段，其它单据不受影响）；
  2. **SP 单据编辑页**（`bill-edit.js` 的 `renderBillEdit`）：对 `BILL_CODE === 'stock-out'` 在明细区之前注入同一入口
     （显式传入 `BILL_EDIT_OID`）。
  未加载 crud.js 时安装钩子安全返回 `false`，未加载选择器脚本时页面保持原样，均绝不报错。
- 候选区：服务端权威候选按「来源销售订单」去重展示（来源 Id / 单号 / 客户 / 可发行数 / 剩余合计），
  另有「逐商品可发证据」明细（含不可用行与原因）；重复 / 歧义 / 单位未知 / 满额的行不提供选择按钮。
- 选择后：以服务端 `ResolveSourceDetailAsync` 的权威投影回填 `salesOrderId` 与**权威客户** `customerId`，
  并写入**权威商品 / 规格 / 基础单位**明细行（数量默认剩余可发，操作员可改为**正数部分数量**）；
  单价 / 金额一律留 0（**绝不臆造价格**，出库成本仍由服务端移动加权平均口径兜底）。
- **保留用户所选仓库**：选择器绝不写入 `warehouseId` / `WarehouseId`（仓库必须是有界选项中的合法仓库）。
- **失败保留表单输入**：任一数量非法（空 / 0 / 负数 / 非数字 / 超剩余可发）或页面没有可写明细区域时，
  整体拒绝写入、显示服务端 / 本地权威原因，**不清空草稿**。
- **防止重复点击**：应用期间置 `SOS.applying` 闸门并禁用「应用到表单」按钮，重复点击直接返回。
- 重开：从表单读取已持久化的 `salesOrderId` 并加载权威详情；**留空（null）= 历史未链接**，
  入口文案与弹窗显式标注「未选择 = 历史未链接」，绝不静默回填来源。

## 5. 保存 / 提交 / 审核：锁序与容量

- 创建 / 修改：`StockOutController` 把表单「未选择来源」（数字 `0`）归一为 `null`（保留历史无来源语义，
  负数仍 fail closed 拒绝），再按 ERP-370 校验实时授权与「已存 / 请求」两侧客户，按 ERP-343 `ValidateLinkAsync`
  校验显式来源权威性（存在 / 未删除 / 已审核 / 客户一致 / 每商品唯一兼容明细行 / 单位兼容），
  失败即整体拒绝、不消耗单据号、不落库；修改在可串行化事务内对**同一把**出库单行锁后整体回滚，
  已审核库存 / 流水不被穿透，单据 / 明细保持原样。
- 审核：在既有「本出库单行 + 来源销售订单行 `UPDLOCK, HOLDLOCK`」锁与可串行化事务内**重复**授权 / 来源 / 单位判定
  （`ValidateApprovalAsync`）与**累计发货容量**判定（`已审核出库（基础单位） + 本次 ≤ 授权数量 + 0.0001 容差`），
  并发同单审核被串行化，后到者能看到先到者已提交的数量，**绝不超发**；取消释放容量。
  陈旧候选（选择后被取消 / 驳回 / 删除的来源）在审核时 fail closed，绝不静默放行。
- 旧语义保持不变：待提交 / 已提交单据允许暂时超额登记，最终**过账上限**由审核在来源行锁内裁定。
- 取消护栏（不改写）：ERP-359 存在「已审核、未删除」销售退货显式引用本出库单时拒绝取消；
  ERP-367 存在「已审核、未删除」装柜清单明细显式链接（`SourceStockOutDetailId`）时拒绝取消。

## 6. 不改写项与边界

- 不新增表 / 列 / 外键 / schema / 菜单 / 权限模型 / 用户授权；不提供匿名或管理员兜底。
- 不改库存成本口径（ERP-009 / ERP-025 移动加权平均）、不改写销售订单 / 商品 / 客户 / 仓库主数据、不改价格 / 金额 / 财务过账。
- 不重写出库原始数量与历史库存流水；来源单据审计（`StockMovement.SourceDocType` / `SourceDocId` / `SourceDocNo`）与
  红字冲销轨迹原样保留；`SalesOrderId` 仍为可空（历史未链接出库兼容，界面显式可见为未链接）。
- 本选择器不做库存预留 / 锁定、不生成单证 / 费用、不执行任意 SQL；
  **不从销售退货恢复发货容量**。
- `.ai/logs/` 下既有失败 / 验证日志原样保留（不删除、不覆盖、不重写）。

## 7. 测试

| 层 | 文件 | 覆盖 |
|---|---|---|
| 单元（内存库） | `src/ERP.UnitTests/StockOutSourceSelectionTests.cs` | 候选范围 / 状态过滤、ERP-343 剩余可发（待提交预留不计入）、装箱折算、重复歧义 / 单位未知 / 商品缺失 / 满额显式不可用、**销售退货绝不恢复容量**、供应商 / 客户筛选与有界关键字、条数 / 关键字钳制、详情 fail closed、数据范围（范围外按不存在）、显式选择保存后重开保留权威来源与部分数量、`0 → null` 保留历史语义、授权 fail closed（无身份 / 缺「销售订单」菜单 / 禁用） |
| 真实 SQL | `src/ERP.IntegrationTests/StockOutSourceSelectionSqlServerTests.cs` | 真实权限（缺「销售出库」/ 缺「销售订单」/ 禁用 / 无身份）、真实候选投影与 ERP-343 剩余可发、重复 / 单位未知 / 商品缺失 / 满额不可用、详情 fail closed 与范围外、来源取消（stale cancellation）后审核 fail closed 且无写入、**两条独立连接竞争**（同来源两张出库单不超发 / 同一张出库单重复审核恰好一次）、目标库护栏单元校验 |
| 前端（可执行） | `tests/automation/stock-out-source-picker.test.js` | 可用性过滤、数量正数 / 上限、明细回填权威单位且**不臆造价格**（通用模块 camelCase + SP 单据页 PascalCase 两套口径）、**保留用户所选仓库**、应用闸门（只读 / 加载中 / **正在应用** 防止重复点击）、安全转义、接口与脚本接线契约（候选 / 详情接口、`modules-doc2` 声明、`index.html` 注册、`bill-edit.js` 仅对 stock-out 注入入口、不执行 SQL、不解析文本 Id、不写仓库字段） |

运行：

```powershell
node tests/automation/stock-out-source-picker.test.js
dotnet build NEWERP.sln -c Release
dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release
```

真实 SQL 集成用例要求专用目标库：实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST`、
`Integrated Security=true`；连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，
在访问数据库之前校验；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset / 复用，
也绝不读取 `appsettings` / `.env` / 生产凭据。

## 8. 未执行 / 限制

- 本任务 `validation_profile = safe`、`completion_mode = build`：交付门槛为 **Release build + 全量 `ERP.UnitTests`**
  （本次 5649 项全部通过）。
- 真实 SQL 集成用例**已在专用环境执行**：目标 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST`、
  `Integrated Security=true`，每次运行创建全新 GUID 后缀库（绝不 drop / reset / 复用），
  本次 `StockOutSourceSelectionSqlServerTests` **12 项全部通过**（含两条独立连接竞争与目标库护栏校验）。
- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置。
- **构建通过不等于阶段验收通过**：阶段验收仍需结合真实 SQL 集成用例与（如需要）真实浏览器验收结论。

## 9. 相关实现

- `src/ERP.Application/Services/StockOutOrderFulfillmentRules.cs`：`QuerySourceCandidatesAsync` /
  `ResolveSourceDetailAsync`（只读有界候选与详情）、`ClampTake` / `NormalizeKeyword`、候选口径常量，
  以及既有 `ValidateLinkAsync` / `ValidateApprovalAsync`（资格 + 累计容量）。
- `src/ERP.Application/Services/StockOutAuthorizationRules.cs`：`EnsureSourceMenuAuthorizedAsync`
  （既有「销售出库」+ 既有「销售订单」菜单，实时 fail closed，返回权威数据范围）。
- `src/ERP.Application/Services/SalespersonDataScopeService.cs`：`FilterByCustomer`（ERP-097 权威客户范围下推）。
- `src/ERP.Api/Controllers/StockOutController.cs`：候选 / 详情端点（实时授权 → 双菜单 → 客户范围 → 取数），
  创建 / 修改把 `0 → null` 归一，审核沿用 ERP-343 来源订单行锁 + 可串行化事务。
- `src/ERP.Api/wwwroot/js/stock-out-source-picker.js`、`modules-doc2.js`（字段 + 明细声明）、
  `bill-edit.js`（SP 单据编辑页入口）、`index.html`（脚本注册）。
- 复用既有：`SalespersonDataScopeService`（ERP-097 数据范围）、`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`
  （角色 → 菜单）、`InventoryService`（ERP-009 / ERP-025 记账与冲销）、`StockUnitConversion`（单位折算）、
  `ReturnSourceCancellationRules`（ERP-359）/ `LoadingStockOutLinkRules`（ERP-367）取消护栏（不改写）。
