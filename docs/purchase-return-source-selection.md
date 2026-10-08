# 采购退货来源选择与净可退容量（ERP-377）

## 1. 目标

让普通操作员在**业务界面**上完成「采购退货单 → 来源采购入库单」的**显式选择**：来源必须是当前账号采购入库
归属范围内「已审核、未删除」的采购入库单，选择后由服务端权威投影回填来源 Id / 单号 / 供应商 / 仓库与
**逐商品净可退容量**，操作员只录入**正数部分数量**。ERP-358 已提供「显式 `SourceStockInId` + 累计可退容量」
护栏，但业务界面此前只能手工填写来源 Id / 单号，无法看到权威的「还能退多少」。

本任务只新增**只读有界候选 / 详情投影**与**表单选择入口**：不新增表 / 列 / 菜单 / 权限 / 用户授权，
不改库存成本口径（移动加权平均 / 来源入库成本兜底），不改写来源单据审计，不臆造价格 / 成本或财务过账。

## 2. 口径

- **候选来源**：`StockIn` 中 `IsDeleted = 0` 且 `Status = Approved`，且通过既有采购入库归属范围
  （ERP-352 `StockInAuthorizationRules.ApplyScopeAsync`：特权账号不过滤；已映射入库操作员可见「链接采购订单
  归属客户在其范围内」的入库单，以及未链接入库单）。**未审核 / 已取消 / 已驳回 / 已删除的来源一律不返回**，
  归属范围外按「不存在」处理（不泄露单据归属）。
- **聚合键**：`(来源采购入库单 Id, 商品 Id)`。同商品**重复入库行按基础单位合计**（绝不重复相乘）。
- **基础单位折算**：入库明细单位等于商品基础单位（`Unit`）直接使用；等于装箱单位（`PackageUnit`）按
  `UnitsPerPackage` 折算；其它单位、缺失基础单位、装箱数 ≤ 0 → **单位未知**（不可用）。
- **已生效退货**：`PurchaseReturns.SourceStockInId = 来源单`、`IsDeleted = 0`、`Status = Approved` 的明细数量，
  同样按基础单位折算并**按商品保守聚合**（退货没有来源明细 Id，绝不猜测退货归属到哪一条入库明细）。
- **净可退容量** = `来源基础单位数量 − 已生效退货基础单位数量`，下限 0。
- **不可用**：净可退容量 ≤ 0（容差 `0.0001`，与 ERP-358 同口径）、来源数量 ≤ 0、单位未知、负数量 →
  `available = false` + 服务端权威原因；界面**不得**将其作为可选项。
- **关键字**：只在来源单号 / 供应商名 / 商品名称 / 规格内做有界匹配（大小写不敏感，长度截断 100），
  绝不用于推断来源；空串 = 不过滤。
- **有界**：候选先按采购入库归属范围收窄，再取最近 `MaxCandidateScan = 500` 张来源入库单，最后返回至多
  `MaxCandidateTake = 200` 条（`take <= 0` 取默认 50；服务端钳制，绝不无界拉取）。

## 3. 接口（只读、有界）

| 方法 | 路由 | 说明 |
|---|---|---|
| GET | `/api/inventory/purchase-returns/source-candidates?supplierId=&warehouseId=&keyword=&take=` | 可退货来源候选（逐商品行）：`PurchaseReturnSourceCandidateDto` 列表 |
| GET | `/api/inventory/purchase-returns/source-candidates/{sourceStockInId}` | 来源详情（表头 + 逐商品可退行）：`PurchaseReturnSourceDetailDto` |

`PurchaseReturnSourceCandidateDto` 字段：`sourceStockInId / sourceStockInNo / sourceStockInDate /
supplierId / supplierName / warehouseId / warehouseName / productId / productName / spec / baseUnit /
sourceBaseQuantity / effectiveReturnedBaseQuantity / remainingBaseQuantity / available / unavailableReason`。

`PurchaseReturnSourceDetailDto` 字段：表头（`sourceStockInId / No / Date / supplierId / supplierName /
warehouseId / warehouseName`）+ `available` / `unavailableReason` + `lines`（同上候选行）。

排序：可用候选优先，其后按净可退容量、来源入库单 Id 倒序、商品 Id 升序（稳定且确定）。

**授权口径**：既有的「采购退货」（`purchase-return`）菜单 **与** 既有的「采购入库」（`stock-in`）菜单都必须
实时具备（`PurchaseReturnSourceRules.EnsureSourceMenuAuthorizedAsync`），再叠加既有采购入库归属范围；
两者都**先于**计数 / 取数判定。不新增用户授权，也不提供匿名 / 管理员降级（撤销任一授权后下一次请求立即收敛）。

**错误口径**

| 场景 | 结果 |
|---|---|
| 来源不存在 / 不在采购入库归属范围 / 已删除 | `1002` 按「不存在」拒绝（不泄露归属） |
| 来源未审核 / 已取消 / 已驳回 | `1005` 冲突拒绝（必须重新显式选择来源） |
| 来源 Id ≤ 0 | `1004` 参数错误 |
| 无身份 | `2000` 未认证 |
| 账号不存在 / 已删除 | `2000` 未认证 |
| 账号禁用 / 缺既有「采购退货」菜单 / 缺既有「采购入库」菜单 | `2002` 权限不足 |

候选与详情是**只读投影**：不落库、不改单据 / 库存 / 流水、不消耗单据号、不新增表 / 列 / 菜单 / 权限 / 用户授权，
也绝不臆造价格 / 成本。

## 4. 业务表单选择器（`src/ERP.Api/wwwroot/js/purchase-return-source-picker.js`）

- 入口（两个退货业务界面都覆盖）：
  1. **通用模块表单**（`modules-doc2.js` 的采购退货字段 `sourceStockInId` 声明 `selector: 'purchase-return-source'`）：
     选择器脚本在加载时**包裹既有 `fieldHtml` / `openForm`**，为带该标记的字段追加
     「选择来源采购入库单（按净可退容量过滤）」按钮（只影响该字段，其它单据不受影响）；
  2. **SP 单据编辑页**（`bill-edit.js` 的 `renderBillEdit`）：对 `BILL_CODE === 'purchase-return'`
     在明细区之前注入同一入口（显式传入 `BILL_EDIT_OID`）。
  未加载 crud.js 时安装钩子安全返回 `false`，未加载选择器脚本时页面保持原样，均绝不报错。
- 候选区：服务端权威候选按「来源入库单」去重展示（来源 Id / 单号 / 供应商 / 仓库 / 可退行数 / 剩余合计），
  另有「逐商品可退证据」明细（含不可用行与原因）；零容量 / 单位未知 / 负数量的行不提供选择按钮。
- 选择后：以服务端 `ResolveSourceDetailAsync` 详情回填表单表头（来源 Id / 单号 / 供应商 / 仓库）与明细行：
  商品 / 规格 / 单位一律取**权威基础单位**，`unitPrice = 0`、`unitCost = 0`、`amount = 0`
  （**绝不臆造价格 / 成本**；出库成本由服务端在审核时按来源入库成本兜底）。
- 数量：默认填入该商品净可退容量，操作员可下调；必须为**正数**且 ≤ 净可退容量，任一行不合法则**整体拒绝**
  并保留原输入（不写入表单）。
- 重开：打开选择器时按表单上持久化的 `sourceStockInId` 拉取详情只读回显（保存 → 重开 → 来源 Id / 单号不变）；
  未选来源（`null`）的历史退货保持「无来源」，绝不回填、绝不按来源单号文本猜测。
- 只读：非「待提交（草稿）」单据（已提交 / 已审核 / 已驳回 / 已完成 / 已取消）来源只读，服务端同样 fail closed。
- 保存失败（授权 / 归属范围 / 来源失效 / 状态冲突）：只显示服务端原因，**保留表单全部输入**，不清空草稿。

## 5. 保存 / 提交 / 审核：锁序与容量

- 创建 / 修改 / 提交：在既有「**来源入库单行 → 退货单行**」锁序（`UPDLOCK, HOLDLOCK`）与可串行化事务内
  重复**资格**判定（`PurchaseReturnSourceRules.ValidateLinkAsync`：存在 / 未删除 / 已审核 / 供应商一致 /
  仓库一致 / 快照一致 / 商品与单位可折算），失败即整体回滚：不写库、不消耗单据号、原单据 / 库存 / 流水保持不变。
- 审核 / 销审：在同一把来源行锁内重复资格判定 **+ 累计可退容量**判定
  （`ValidateApprovalAsync`：`已审核退货（重复商品行按基础单位合计） + 本次 ≤ 已审核入库数量 + 0.0001 容差`），
  并发退货被串行化，后到者能看到先到者已提交的累计退货数量，**绝不超退**；销审释放容量。
- 保留 ERP-358 既有语义：**草稿 / 已提交单据允许暂时超额**（操作员可先登记待退数量），
  最终**过账上限**由审核 / 销审在来源行锁内裁定 —— 因此创建 / 提交不会替服务端「预扣」容量，
  也不会因为并发草稿而误判。
- 本选择器**不**产生任何合成 AP 付款、库存或财务过账；库存增减仍只由既有审核 / 销审经由
  `InventoryService`（ERP-009）在来源行锁内恰好一次执行。

## 6. 不改写项与边界

- 不新增表 / 列 / 外键 / schema / 菜单 / 权限模型 / 用户授权；不提供匿名或管理员兜底。
- 不改库存成本口径（来源入库成本 → 移动加权平均兜底）、不改写商品 / 供应商 / 仓库 / 入库单主数据、
  不改价格 / 金额 / 财务过账。
- 不重写退货原始数量与历史库存流水；来源单据审计（`StockMovement.SourceDocType` / `SourceDocId` /
  `SourceDocNo`）与红字冲销轨迹原样保留；`SourceStockInId` 仍为可空（历史未链接退货兼容）。
- 本选择器不做库存预留 / 锁定、不生成单证 / 费用、不执行任意 SQL。
- `.ai/logs/` 下既有失败 / 验证日志原样保留（不删除、不覆盖、不重写）。

## 7. 测试

| 层 | 文件 | 覆盖 |
|---|---|---|
| 单元（内存库） | `src/ERP.UnitTests/PurchaseReturnSourceSelectionTests.cs` | 候选聚合（重复商品行合一）、净可退容量与已生效退货扣减、装箱折算、零容量 / 单位未知 / 负数量 / 商品缺失显式不可用、只含未删除且已审核来源、供应商 / 仓库筛选与有界关键字、详情 fail closed、归属范围外按不存在、显式选择保存后重开保留权威来源与部分数量（价格留 0）、授权 fail closed（无身份 / 缺采购入库菜单 / 撤销菜单）、条数 / 关键字钳制 |
| 真实 SQL | `src/ERP.IntegrationTests/PurchaseReturnSourceSelectionSqlServerTests.cs` | 真实权限（缺「采购退货」/ 缺「采购入库」/ 禁用 / 无身份）、真实候选聚合与部分退货剩余、单位未知显式不可用、来源取消（stale cancellation）后候选收敛且审核 fail closed 且不写库存 / 流水、**两条独立连接竞争**（同来源两张退货 / 同一张退货重复审核）、目标库护栏单元校验 |
| 前端（可执行） | `tests/automation/purchase-return-source-picker.test.js` | 可用性过滤、数量正数与上限、明细回填权威基础单位且价格 / 成本留 0、安全转义、应用闸门、接口与脚本接线契约（候选 / 详情接口、`modules-doc2` 声明、`index.html` 注册、`bill-edit.js` 仅对 purchase-return 注入入口、不执行 SQL、不解析文本 Id） |

运行：

```powershell
node tests/automation/purchase-return-source-picker.test.js
dotnet build NEWERP.sln -c Release
dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release
```

真实 SQL 集成用例要求专用目标库：实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST`、
`Integrated Security=true`；连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，
在访问数据库之前校验；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset / 复用，
也绝不读取 `appsettings` / `.env` / 生产凭据。

## 8. 未执行 / 限制

- 本任务 `validation_profile = safe`、`completion_mode = build`：交付门槛为 **Release build + 全量 `ERP.UnitTests`**；
  真实 SQL 集成用例在具备专用 localdb 的环境下运行（实现门槛内未运行）。
- 未运行真实浏览器 UI 验收（`browser_acceptance.required = false`），未启动 API、未部署、未改动生产库或生产设置。
- **构建通过不等于阶段验收通过**：阶段验收仍需在具备专用 localdb 的环境中执行真实 SQL 集成用例与
  （如需要）真实浏览器验收。

## 9. 相关实现

- `src/ERP.Application/Services/PurchaseReturnSourceRules.cs`：`EnsureSourceMenuAuthorizedAsync`
  （既有「采购退货」+ 既有「采购入库」菜单，实时 fail closed）、`QuerySourceCandidatesAsync` /
  `ResolveSourceDetailAsync`（只读有界候选与详情）、`ClampTake` / `NormalizeKeyword`，
  以及既有 `ValidateLinkAsync` / `ValidateApprovalAsync`（资格 + 累计容量）。
- `src/ERP.Application/Services/StockInAuthorizationRules.cs`：`ApplyScopeAsync`（既有 ERP-352 采购入库归属范围下推）。
- `src/ERP.Api/Controllers/PurchaseReturnController.cs`：候选 / 详情端点（实时授权 → 双菜单 → 归属范围 → 取数），
  创建 / 修改 / 提交的「来源入库单行 → 退货单行」锁序与可串行化事务。
- `src/ERP.Api/wwwroot/js/purchase-return-source-picker.js`、`modules-doc2.js`（字段声明）、
  `bill-edit.js`（SP 单据编辑页入口）、`index.html`（脚本注册）。
- 复用既有：`SalespersonDataScopeService`（ERP-097 数据范围）、`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`
  （角色 → 菜单）、`InventoryService`（ERP-009 记账与冲销）。
