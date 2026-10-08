# 销售退货来源选择与净可退容量（ERP-374）

## 1. 目标

让普通操作员在**业务界面**上完成「销售退货单 → 来源销售出库单」的**显式选择**：来源必须是当前账号客户数据范围之内
「已审核、未删除」的销售出库单，选择后由服务端权威投影回填来源 Id / 单号 / 客户 / 仓库与**逐商品净可退容量**，
操作员只录入**正数部分数量**。ERP-357 已提供「显式 `SourceStockOutId` + 累计可退容量」护栏，但业务界面此前只能
手工填写来源 Id / 单号，无法看到权威的「还能退多少」。

本任务只新增**只读有界候选 / 详情投影**与**表单选择入口**：不新增表 / 列 / 菜单 / 权限 / 用户授权，
不改库存成本口径，不臆造价格 / 成本或财务过账。

## 2. 口径

- **候选来源**：`StockOut` 中 `IsDeleted = 0` 且 `Status = Approved`，且其 `CustomerId` 属于当前账号的
  客户数据范围（`SalespersonDataScopeService`，ERP-097 唯一权威口径）。**范围外客户 / 未审核 / 已取消 /
  已驳回 / 已删除的来源一律不返回**，范围外客户按「不存在」处理（不泄露单据归属）。
- **聚合键**：`(来源销售出库单 Id, 商品 Id)`。同商品**重复出库行按基础单位合计**（绝不重复相乘、绝不重复计数）。
- **基础单位折算**：出库明细单位等于商品基础单位直接使用；等于装箱单位（`PackageUnit`）按 `UnitsPerPackage` 折算；
  其它单位、缺失基础单位、装箱数 ≤ 0 → **单位未知**（不可用）。
- **已生效退货**：`SalesReturns.SourceStockOutId = 来源单`、`IsDeleted = 0`、`Status = Approved` 的明细数量，
  同样按基础单位折算并**按商品保守聚合**（退货没有来源明细 Id，绝不猜测退货归属到哪一条出库明细）。
- **净可退容量** = `来源基础单位数量 − 已生效退货基础单位数量`，下限 0。
- **不可用**：净可退容量 ≤ 0（容差 `0.0001`，与 ERP-357 同口径）、来源数量 ≤ 0、单位未知、或存在负数量等损坏证据 →
  `available = false` 且带服务端权威原因；界面**不得**将其作为可选项。
- **关键字**：只在来源单号 / 客户名 / 商品名称 / 规格内做有界匹配（大小写不敏感，长度截断 100），
  绝不用于推断来源；空串 = 不过滤。
- **有界**：候选先按客户数据范围收窄，再取最近 `MaxCandidateScan = 500` 张来源出库单，最后返回至多
  `MaxCandidateTake = 200` 条（`take <= 0` 取默认 50；服务端钳制，绝不无界拉取）。

## 3. 接口（只读、有界）

| 方法 | 路由 | 说明 |
|---|---|---|
| GET | `/api/inventory/sales-returns/source-candidates?customerId=&warehouseId=&keyword=&take=` | 可退货来源候选（逐商品行）：`SalesReturnSourceCandidateDto` 列表 |
| GET | `/api/inventory/sales-returns/source-candidates/{sourceStockOutId}` | 来源详情（表头 + 逐商品可退行）：`SalesReturnSourceDetailDto` |

`SalesReturnSourceCandidateDto` 字段：`sourceStockOutId / sourceStockOutNo / sourceStockOutDate /
customerId / customerName / warehouseId / warehouseName / productId / productName / spec / baseUnit /
sourceBaseQuantity / effectiveReturnedBaseQuantity / remainingBaseQuantity / available / unavailableReason`。

`SalesReturnSourceDetailDto` 字段：表头（`sourceStockOutId / No / Date / customerId / customerName /
warehouseId / warehouseName`）+ `available` / `unavailableReason` + `lines`（同上候选行）。

排序：可用候选优先，其后按剩余容量、来源 Id 倒序、商品 Id 升序（稳定且确定）。

**错误口径**

| 场景 | 结果 |
|---|---|
| 来源不存在 | `1002` 按「不存在」拒绝 |
| 来源不在客户数据范围 | `1002` 按「不存在」拒绝（不泄露归属） |
| 来源已删除 | `1002` 按「不存在」拒绝 |
| 来源未审核 / 已取消 / 已驳回 | `1005` 冲突拒绝（必须重新显式选择来源） |
| 来源 Id ≤ 0 | `1004` 参数错误 |
| 无身份 | `2000` 未认证 |
| 账号不存在 / 已删除 | `2000` 未认证 |
| 账号禁用 / 缺既有「销售退货」菜单 / 缺既有「销售出库」菜单 | `2002` 权限不足 |

候选与详情是**只读投影**：不落库、不改单据 / 库存 / 流水、不消耗单据号、不新增表 / 列 / 菜单 / 权限 / 用户授权，
也不提供匿名 / 管理员降级（每次都实时重查菜单与数据范围，撤销授权后下一次请求立即收敛）。

## 4. 业务表单选择器（`src/ERP.Api/wwwroot/js/sales-return-source-picker.js`）

- 入口：`modules-doc2.js` 的销售退货字段 `sourceStockOutId` 声明
  `selector: 'sales-return-source'`；选择器脚本在加载时**包裹既有 `fieldHtml` / `openForm`**，
  为带该标记的字段追加「选择来源出库单（按可退容量过滤）」按钮（只影响该字段，其它单据不受影响）。
  未加载 crud.js 时安装钩子安全返回 `false`，绝不影响页面其它功能。
- 候选区：服务端权威候选按「来源单据」去重展示（来源 Id / 单号 / 客户 / 仓库 / 可退行数 / 剩余合计），
  另有「逐商品可退证据」明细（含不可用行与原因）；零容量 / 损坏证据的行不提供选择按钮。
- 选择后：以服务端 `ResolveSourceDetailAsync` 详情回填表单表头（来源 Id / 单号 / 客户 / 仓库）与明细行：
  商品 / 规格 / 单位一律取**权威基础单位**，`unitPrice = 0`、`unitCost = 0`、`amount = 0`
  （**绝不臆造价格 / 成本 / 金额**；成本由服务端在审核时按来源出库成本兜底）。
- 数量：默认填入该商品剩余可退容量，操作员可下调；必须为**正数**且 ≤ 剩余可退容量，任一行不合法则**整体拒绝**
  并保留原输入（不写入表单）。
- 重开：打开选择器时按表单上持久化的 `sourceStockOutId` 拉取详情只读回显（保存 → 重开 → 来源 Id / 单号不变）；
  未选来源（`null`）的历史退货保持「无来源」，绝不回填、绝不按来源单号文本猜测。
- 只读：非「待提交（草稿）」单据（已提交 / 已审核 / 已驳回 / 已完成 / 已取消）来源只读，服务端同样 fail closed。
- 保存失败（授权 / 数据范围 / 来源失效 / 状态冲突）：只显示服务端原因，**保留表单全部输入**，不清空草稿。

## 5. 保存 / 提交 / 审核：锁序与容量

- 创建 / 修改 / 提交：在既有「**来源出库单行 → 退货单行**」锁序（`UPDLOCK, HOLDLOCK`）与可串行化事务内
  重复**资格**判定（`SalesReturnSourceRules.ValidateLinkAsync`：存在 / 未删除 / 已审核 / 客户一致 / 仓库一致 /
  快照一致 / 商品与单位可折算），失败即整体回滚：不写库、不消耗单据号、原单据 / 库存 / 流水保持不变。
- 审核 / 销审：在同一把来源行锁内重复资格判定 **+ 累计可退容量**判定
  （`ValidateApprovalAsync`：`已审核退货（重复商品行按基础单位合计） + 本次 ≤ 已审核出库数量 + 0.0001 容差`），
  并发退货被串行化，后到者能看到先到者已提交的累计退货数量，**绝不超退**；销审释放容量。
- 保留 ERP-357 既有语义：**草稿 / 已提交单据允许暂时超额**（操作员可先登记待退数量），
  最终**过账上限**由审核 / 销审在来源行锁内裁定 —— 因此创建 / 提交不会替服务端「预扣」容量，
  也不会因为并发草稿而误判；这条边界与 `SalesReturnSourceTests` 既有用例（先超退保存、审核时拒绝）一致。

## 6. 不改写项与边界

- 不新增表 / 列 / 外键 / schema / 菜单 / 权限模型 / 用户授权；不提供匿名或管理员兜底。
- 不改库存成本口径（移动加权平均）、不改写商品 / 客户 / 仓库 / 出库单主数据、不改价格 / 金额 / 财务过账。
- 不重写退货原始数量与历史库存流水；来源单据审计（`SourceDocType` / `SourceDocId` / `SourceDocNo`）与
  红字冲销轨迹原样保留；`SourceStockOutId` 仍为可空（历史未链接退货兼容）。
- 本选择器不做库存预留 / 锁定、不生成单证 / 费用、不执行任意 SQL。
- `.ai/logs/` 下既有失败 / 验证日志原样保留（不删除、不覆盖、不重写）。

## 7. 测试

| 层 | 文件 | 覆盖 |
|---|---|---|
| 单元（内存库） | `src/ERP.UnitTests/SalesReturnSourceSelectionTests.cs` | 候选范围 / 状态过滤、重复商品行合一、净可退容量、零容量 / 单位未知 / 负数量显式不可用、装箱折算、关键字有界匹配、条数钳制、详情 fail closed、显式选择保存后重开保留权威来源与部分数量（价格留 0）、授权 fail closed（无身份 / 缺菜单 / 撤销菜单）与客户范围 |
| 真实 SQL | `src/ERP.IntegrationTests/SalesReturnSourceSelectionSqlServerTests.cs` | 真实权限（缺「销售退货」/ 缺「销售出库」/ 禁用 / 无身份）、真实候选聚合与部分退货剩余、来源取消（stale cancellation）后候选收敛且审核 fail closed、**两条独立连接竞争**（同来源两张退货 / 同一张退货重复审核）、目标库护栏单元校验 |
| 前端（可执行） | `tests/automation/sales-return-source-picker.test.js` | 可用性过滤、数量正数 / 上限、明细回填权威单位与价格留 0、安全转义、应用闸门、接口与脚本接线契约（不执行 SQL、不解析文本 Id） |

运行：

```powershell
node tests/automation/sales-return-source-picker.test.js
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

- `src/ERP.Application/Services/SalesReturnSourceRules.cs`：`EnsureSourceMenuAuthorizedAsync`（既有「销售出库」菜单）、
  `QuerySourceCandidatesAsync` / `ResolveSourceDetailAsync`（只读有界候选与详情）、`ClampTake` / `NormalizeKeyword`、
  以及既有 `ValidateLinkAsync` / `ValidateApprovalAsync`（资格 + 累计容量）。
- `src/ERP.Api/Controllers/SalesReturnController.cs`：候选 / 详情端点（实时授权 → 既有双菜单 → 客户数据范围 → 取数），
  创建 / 修改 / 提交的「来源出库单行 → 退货单行」锁序与可串行化事务。
- `src/ERP.Api/wwwroot/js/sales-return-source-picker.js`、`modules-doc2.js`（字段声明）、`index.html`（脚本注册）。
- 复用既有：`SalespersonDataScopeService`（ERP-097 数据范围）、`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`
  （角色 → 菜单）、`InventoryService`（ERP-009 记账与冲销）。

