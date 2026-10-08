# 采购入库来源选择与剩余可收数量（ERP-375）

## 1. 目标

让普通操作员在**业务界面**上完成「采购入库单 → 来源采购订单」的**显式选择**：来源必须是当前账号客户数据范围之内
「已审核、未删除」的采购订单，选择后由服务端权威投影回填来源 Id / 单号 / 供应商与**逐商品剩余可收数量**，
操作员只录入**正数部分收货数量**。ERP-342 已提供「显式 `PurchaseOrderId` + 累计收货容量」护栏（审核时 fail closed），
ERP-033 提供成本兜底，但业务界面此前只能手工填写来源 Id，无法看到权威的「还能收多少」。

本任务只新增**只读有界候选 / 详情投影**与**表单选择入口**：不新增表 / 列 / 菜单 / 权限 / 用户授权，
不改成本口径（ERP-033）、不改收货容量口径（ERP-342）、不臆造价格 / 成本、不写库存。

## 2. 口径

- **候选来源**：`PurchaseOrder` 中 `IsDeleted = 0` 且 `Status = Approved`，且其权威归属客户落在当前账号的
  客户数据范围（ERP-371 `PurchaseOrderAuthorizationRules.ApplyScopeAsync`，覆盖显式 `OwningCustomerId` 与
  权威归属销售订单客户）。**范围外客户 / 未审核 / 已取消 / 已驳回 / 已删除的来源一律不返回**，
  范围外客户按「不存在」处理（不泄露单据归属）。
- **聚合键**：`(来源采购订单 Id, 商品 Id)`。
- **授权数量（基础单位）**：与 ERP-342 `TryBaseUnitQuantity` 同口径 —— 订单明细单位等于商品基础单位（`Unit`）
  直接使用；等于装箱单位（`PackageUnit`）按 `UnitsPerPackage` 折算；其它单位 / 缺失基础单位 / 无效装箱数 →
  **单位未知**（不可用）。**同商品出现多条明细 → 授权数量不唯一**（不可用，绝不猜测、绝不合计）。
- **已收货数量（基础单位）**：`StockIns.PurchaseOrderId = 来源订单`、`IsDeleted = 0`、`Status = Approved` 的
  入库明细数量按商品直接合计（与 ERP-342 `EnforceCumulativeAsync` 同一口径；入库明细在保存 / 审核前已折算基础单位）。
  未审核 / 已取消 / 已删除的入库**不计入**，因此取消后自动释放剩余可收数量。
- **剩余可收数量** = `授权数量 − 已收货数量`，下限 0。
- **不可用**：剩余可收 ≤ 0（容差 `0.0001`，与 ERP-342 同口径）、授权数量 ≤ 0、重复 / 歧义明细、
  单位未知、商品主数据缺失、负数量 → `available = false` + 服务端权威原因；界面**不得**将其作为可选项。
- **关键字**：只在订单号 / 供应商名 / 商品名称 / 规格内做有界匹配（大小写不敏感，长度截断 100），
  绝不用于推断来源；空串 = 不过滤。
- **有界**：候选先按客户数据范围收窄，再取最近 `MaxCandidateScan = 500` 张采购订单，最后返回至多
  `MaxCandidateTake = 200` 条（`take <= 0` 取默认 50；服务端钳制，绝不无界拉取）。

## 3. 接口（只读、有界）

| 方法 | 路由 | 说明 |
|---|---|---|
| GET | `/api/stock-ins/source-candidates?supplierId=&keyword=&take=` | 可收货来源候选（逐商品行）：`StockInSourceCandidateDto` 列表 |
| GET | `/api/stock-ins/source-candidates/{purchaseOrderId}` | 来源详情（表头 + 逐商品剩余可收行）：`StockInSourceDetailDto` |

`StockInSourceCandidateDto` 字段：`purchaseOrderId / orderNo / orderDate / supplierId / supplierName /
currency / exchangeRate / productId / productName / spec / baseUnit / authorizedBaseQuantity /
receivedBaseQuantity / remainingBaseQuantity / available / unavailableReason`。

`StockInSourceDetailDto` 字段：表头（`purchaseOrderId / orderNo / orderDate / supplierId / supplierName /
currency / exchangeRate`）+ `available` / `unavailableReason` + `lines`（同上候选行）。

排序：可用候选优先，其后按剩余可收数量、来源订单 Id 倒序、商品 Id 升序（稳定且确定）。

**授权口径**：既有的「采购入库」（`stock-in`）菜单 **与** 既有的「采购订单」（`purchase-order`）菜单都必须
实时具备（`StockInAuthorizationRules.EnsureSourceMenuAuthorizedAsync`），再叠加 ERP-371 权威客户数据范围；
两者都**先于**计数 / 取数判定。不新增用户授权，也不提供匿名 / 管理员降级（撤销任一授权后下一次请求立即收敛）。

**错误口径**

| 场景 | 结果 |
|---|---|
| 来源不存在 / 不在客户数据范围 / 已删除 | `1002` 按「不存在」拒绝（不泄露归属） |
| 来源未审核 / 已取消 / 已驳回 | `1005` 冲突拒绝（必须重新显式选择来源） |
| 来源 Id ≤ 0 | `1004` 参数错误 |
| 无身份 | `2000` 未认证 |
| 账号不存在 / 已删除 | `2000` 未认证 |
| 账号禁用 / 缺既有「采购入库」菜单 / 缺既有「采购订单」菜单 | `2002` 权限不足 |

候选与详情是**只读投影**：不落库、不改单据 / 库存 / 流水、不消耗单据号、不新增表 / 列 / 菜单 / 权限 / 用户授权，
也绝不臆造价格 / 成本。

## 4. 业务表单选择器（`src/ERP.Api/wwwroot/js/stock-in-source-picker.js`）

- 入口（两个入库业务界面都覆盖）：
  1. **通用模块表单**（`modules-doc2.js` 的采购入库字段 `purchaseOrderId` 声明 `selector: 'stock-in-source'`）：
     选择器脚本在加载时**包裹既有 `fieldHtml` / `openForm`**，为带该标记的字段追加
     「选择来源采购订单（按剩余可收数量过滤）」按钮（只影响该字段，其它单据不受影响）；
  2. **SP 单据编辑页**（`bill-edit.js` 的 `renderBillEdit`，菜单 `/logistics/stock-in` 实际打开的界面）：
     对 `BILL_CODE === 'stock-in'` 在明细区之前注入同一入口（显式传入 `BILL_EDIT_OID`）。
  未加载 crud.js 时安装钩子安全返回 `false`，未加载选择器脚本时页面保持原样，均绝不报错。
- 候选区：服务端权威候选按「来源订单」去重展示（来源 Id / 单号 / 供应商 / 币种 / 可收行数 / 剩余合计），
  另有「逐商品可收证据」明细（含不可用行与原因）；重复 / 歧义 / 单位未知 / 满额的行不提供选择按钮。
- 选择后：以服务端 `ResolveSourceDetailAsync` 详情回填表单表头（来源 Id / 单号 / **权威供应商**）与明细行：
  商品 / 规格 / 单位一律取**权威基础单位**，数量默认该商品剩余可收数量。
  **本页不录入单价 / 成本**（成本由服务端在审核时按 ERP-033 口径兜底），绝不臆造成本；
  两个界面分别按各自既有字段口径写入（通用模块 camelCase + `DETAIL_ROWS`；SP 单据页 PascalCase + `#detail-tbody`）。
- **保留用户所选仓库**：选择器绝不改写仓库字段（`warehouseId` / `WarehouseId` 均不在写入范围；
  写入字段清单 `SIS_WRITTEN_FIELDS = ['purchaseOrderId','supplierId']` 不含仓库）。
- 数量：必须为**正数**且 ≤ 剩余可收数量，任一行不合法则**整体拒绝**并保留原输入（不写入表单）；
  找不到可写入的明细区域时也整体拒绝（绝不只回填表头的半成品）。
- 重开：打开选择器时按表单上持久化的来源 Id（camelCase / PascalCase 两种字段命名都读）拉取详情只读回显
  （保存 → 重开 → 来源 Id 不变）；未选来源（表单留空 = 数字 0，服务端归一为 `null`）的历史入库保持「无来源」，
  绝不回填、绝不按订单号文本猜测。
- 只读：非「待提交（草稿）」单据（已提交 / 已审核 / 已驳回 / 已完成 / 已取消）来源只读，服务端同样 fail closed。
- 保存失败（授权 / 数据范围 / 来源失效 / 状态冲突）：只显示服务端原因，**保留表单全部输入**，不清空草稿。

## 5. 保存 / 提交 / 审核：锁序与容量

- 创建 / 修改：`StockInController` 把表单「未选择来源」（数字 `0`）归一为 `null`（保留历史无来源语义，负数仍 fail closed 拒绝），
  再按 ERP-352 校验实时授权与来源归属，按 ERP-342 `ValidateLinkAsync` 校验显式来源权威性
  （存在 / 未删除 / 已审核 / 供应商一致 / 每商品唯一兼容明细行 / 单位兼容），失败即整体拒绝、不消耗单据号、不落库。
- 审核：在既有「来源采购订单行 `UPDLOCK, HOLDLOCK`」锁与可串行化事务内**重复**授权 / 来源 / 单位判定
  （`ValidateApprovalAsync`）与**累计收货容量**判定（`已审核入库（基础单位） + 本次 ≤ 授权数量 + 0.0001 容差`），
  并发同单审核被串行化，后到者能看到先到者已提交的数量，**绝不超收**；取消释放容量。
- 旧语义保持不变：待提交 / 已提交单据允许暂时超额登记，最终**过账上限**由审核在来源行锁内裁定。
- 成本：审核仍走 ERP-033 `PurchaseStockInCostSource`（来源订单唯一兼容明细行单价 → 折算基础单位 → 非占位汇率换算，
  语义不明确时回退移动加权平均），候选选择**不改变**成本来源判定，也不写入任何价格。

## 6. 不改写项与边界

- 不新增表 / 列 / 外键 / schema / 菜单 / 权限模型 / 用户授权；不提供匿名或管理员兜底。
- 不改库存成本口径（ERP-033 移动加权平均兜底）、不改写采购订单 / 商品 / 供应商 / 仓库主数据、不改价格 / 金额 / 财务过账。
- 不重写入库原始数量与历史库存流水；来源单据审计（`StockMovement.SourceDocType` / `SourceDocId` / `SourceDocNo`）与
  红字冲销轨迹原样保留；`PurchaseOrderId` 仍为可空（历史未链接入库兼容）。
- 本选择器不做库存预留 / 锁定、不生成单证 / 费用、不执行任意 SQL。
- `.ai/logs/` 下既有失败 / 验证日志原样保留（不删除、不覆盖、不重写）。

## 7. 测试

| 层 | 文件 | 覆盖 |
|---|---|---|
| 单元（内存库） | `src/ERP.UnitTests/StockInSourceSelectionTests.cs` | 候选范围 / 状态过滤、ERP-342 剩余可收、装箱折算、重复歧义 / 单位未知 / 商品缺失 / 满额显式不可用、供应商筛选与有界关键字、条数 / 关键字钳制、详情 fail closed、数据范围（范围外按不存在）、显式选择保存后重开保留权威来源与部分数量、`0 → null` 保留历史语义、授权 fail closed（无身份 / 缺「采购订单」菜单） |
| 真实 SQL | `src/ERP.IntegrationTests/StockInSourceSelectionSqlServerTests.cs` | 真实权限（缺「采购入库」/ 缺「采购订单」/ 禁用 / 无身份）、真实候选投影与 ERP-342 剩余可收、重复 / 单位未知 / 满额不可用、详情 fail closed 与范围外、来源取消（stale cancellation）后审核 fail closed 且无写入、**两条独立连接竞争**（同来源两张入库单 / 同一张入库单重复审核）、目标库护栏单元校验 |
| 前端（可执行） | `tests/automation/stock-in-source-picker.test.js` | 可用性过滤、数量正数 / 上限、明细回填权威单位且**不臆造成本**（通用模块 camelCase + SP 单据页 PascalCase 两套口径）、**保留用户所选仓库**、安全转义、应用闸门、接口与脚本接线契约（候选 / 详情接口、`modules-doc2` 声明、`index.html` 注册、`bill-edit.js` 仅对 stock-in 注入入口、不执行 SQL、不解析文本 Id、不写仓库字段） |

运行：

```powershell
node tests/automation/stock-in-source-picker.test.js
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

- `src/ERP.Application/Services/StockInOrderFulfillmentRules.cs`：`QuerySourceCandidatesAsync` /
  `ResolveSourceDetailAsync`（只读有界候选与详情）、`ClampTake` / `NormalizeKeyword`、候选口径常量，
  以及既有 `ValidateLinkAsync` / `ValidateApprovalAsync`（资格 + 累计容量）。
- `src/ERP.Application/Services/StockInAuthorizationRules.cs`：`EnsureSourceMenuAuthorizedAsync`
  （既有「采购入库」+ 既有「采购订单」菜单，实时 fail closed）。
- `src/ERP.Application/Services/PurchaseOrderAuthorizationRules.cs`：`ApplyScopeAsync`（ERP-371 权威客户范围下推）。
- `src/ERP.Api/Controllers/StockInController.cs`：候选 / 详情端点（实时授权 → 双菜单 → 客户范围 → 取数），
  创建 / 修改把 `0 → null` 归一，审核沿用 ERP-342 行锁 + 可串行化事务。
- `src/ERP.Api/wwwroot/js/stock-in-source-picker.js`、`modules-doc2.js`（字段 + 明细声明）、
  `bill-edit.js`（SP 单据编辑页入口）、`index.html`（脚本注册）。
- 复用既有：`SalespersonDataScopeService`（ERP-097 数据范围）、`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`
  （角色 → 菜单）、`PurchaseStockInCostSource`（ERP-033 成本）、`InventoryService`（ERP-009 / ERP-025 记账与冲销）。

## 2026-10-08 独立 SQL 验证

- 全新 GUID `NEWERP_AUTOTEST` 专用 LocalDB：真实控制器 10 项通过；可执行 JS 89 项断言通过；Release 全量构建 0 警告 / 0 错误，单元测试 5638 项通过。
- 原始 SQL 执行发现 8 项失败：正向测试账号缺采购订单菜单，且共享数据库的历史库存/流水污染全库空值与总量断言。整改只修改隔离测试：正向场景复用既有种子管理员及其已有授权，不更改业务权限；拒绝请求比较请求前后基线，并发过账断言限定当前仓库/商品/来源单据及 PurchaseIn 流水类型。
- 日志：`.ai/logs/ERP-375-independent-sql.log`（完整失败原文保留）、`ERP-375-fixture-repair-sql.log`、`ERP-375-independent-js.log`、`ERP-375-fixture-repair-safe-build.log`、`ERP-375-fixture-repair-safe-tests.log`。不访问生产环境、不重置已有数据库。
- 实际浏览器工作流及第三阶段整体验收仍待完成；以上证据不代表阶段验收。
