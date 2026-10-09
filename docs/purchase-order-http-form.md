# 手工 / 关联采购订单录入：服务端权威单号 + 可选引用留空（ERP-429）

## 1. 背景与原始失败证据

「订单管理 → 采购订单」手工录入页不提交采购单号（单号由服务端生成），且可留空的「归属销售订单 ID」「起运港 Id」在旧
前端里被序列化为 `0`。旧实现因此**根本无法到达已校验的生命周期**：

- `PurchaseOrder.OrderNo` 上有显式 `[Required]`：MVC 在**模型校验**阶段就把省略的单号判为非法，返回
  `HTTP 400 {"errors":{"OrderNo":["The OrderNo field is required."]}}`，`PurchaseOrderController.Create`
  没有机会执行「授权 → 条款 / 主数据校验 → 预约单号 → 落库」。
- 旧前端把留空的可选数值字段序列化为 `0`：`0` 会被既有 ERP-425 来源校验（显式非法来源）与 ERP-427 主数据护栏
  （起运港必须为正整数 / 存在 / 启用）按「显式非法引用」拒绝，因此手工未关联采购无法保存。

原始失败证据（**不被改写**）：
`.ai/logs/heartbeat-20261009T0215-purchase-manual-browser-prepared-repair/`（原始商品 / 来源不匹配拒绝）、
`.ai/logs/heartbeat-20261009T0215-purchase-manual-browser-lawful-source-repair/`（使用合法已审核来源后通过的关联场景）、
`.ai/logs/heartbeat-20261009T0250-unlinked-purchase-browser/`（隔离库中手工未关联采购真实浏览器保存 / 重开 / 打印通过，
3 张截图）、`.ai/logs/heartbeat-20261009T0250-unlinked-purchase-prepared/`（隔离补丁与全量单元日志）。

## 2. HTTP 绑定契约（服务端自有单号允许省略）

`src/ERP.Domain/Entities/PurchaseOrder.cs`：`OrderNo` **去掉显式 `[Required]`**，保留 `[MaxLength(50)]`。

| 维度 | 修复后口径 |
|---|---|
| 绑定 / 校验 | 省略 / 空串 `orderNo` 绑定为 `string.Empty`；MVC 对非空引用类型的隐式必填是 `AllowEmptyStrings = true`，因此不再返回 400 |
| 数据库 | 列仍是 **NOT NULL**（非空 CLR 引用类型），长度仍为 50（`[MaxLength(50)]` 经 EF 模型体现），由单元测试用 SQL Server 提供程序**仅构建模型**断言 |
| 权威单号 | `Create` 在既有授权（`EnsureProposedAuthorizedAsync` / 来源锁与 `ApplyLinkAsync`）、条款校验（`PurchaseOrderMutationRules.EnsureValidatedTerms`）与实时主数据校验（`PurchaseOrderMasterReferenceRules.EnsureMasterReferencesAsync`）**之后**才 `entity.OrderNo = await _noService.GenerateAsync(DocumentType.PurchaseOrder)`：调用方提交的值只可能出现在绑定结果里，**绝不决定持久化单号**（伪造单号经直接 SQL 复核被证明不落库） |
| 未放宽的护栏 | 非法正文仍按绑定错误拒绝；必填供应商 / 明细商品 / 单位 / 数量 / 单价 / 税率 / 币种 / 汇率校验、来源血缘、状态与审计行为保持不变；**不做任何全局校验抑制**（`MvcOptions.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes` 未被使用） |

## 3. 前端可选数值序列化（逐字段选择加入）

`src/ERP.Api/wwwroot/js/crud.js`（ERP-428 起已就位，采购复用同一口径）：

```js
if (f.type === 'number') v = v === '' ? (f.nullable === true ? null : 0) : Number(v);
```

`src/ERP.Api/wwwroot/js/modules-doc.js`（精确有限白名单，全站共 3 处）：

```js
{ key: 'portId', label: '目的港 Id（港口字典，可留空）', type: 'number', nullable: true },              // 销售订单
{ key: 'owningSalesOrderId', label: '归属销售订单 ID', type: 'number', nullable: true, selector: 'purchase-order-sales-order-source' },
{ key: 'portId', label: '起运港 Id（港口字典，可留空）', type: 'number', nullable: true },              // 采购订单
```

- **选择加入**：只有显式声明 `nullable: true` 的数值字段留空时才提交 `null`；其它数值字段（汇率 / 税率 / 明细数量与
  单价 / 比例等）仍保持「留空 = 0」的既有默认零值口径，**绝不**扩散为全局可空转换。
- 正整数归属来源 / 起运港原样提交（绑定值精确相等）；`0` / 负数 / 不存在的引用仍由既有 ERP-425 / ERP-427 在服务端
  拒绝（`1001` / `1004`），拒绝时零写入、不占单号。
- 更新时未触碰来源字段 → 前端提交 `null`（省略语义），服务端在既有 ERP-425 行锁协议下**保留已存血缘**，绝不静默清除；
  显式 `0` / 负数仍原子拒绝且血缘与状态不变。

## 4. 真实夹具修复（ERP-429）

`src/ERP.IntegrationTests/OrderTraceabilityUiTests.cs`：

| 问题 | 修复 |
|---|---|
| 场景 2 的采购归属来源是**未审核**销售订单（既有 `PurchaseSalesOrderLinkRules` 要求「已审核、未删除、未取消」） | `CreateSalesOrderViaApi` 经既有 `/submit` 与 `/approve` 接口产生真实审核状态（绝不伪造审核，不新增任何授权 / 菜单），并继续使用与采购明细**同一个活商品**（杜绝来源商品不匹配的初始拒绝被削弱） |
| 清理时对**已审核**销售来源调用 `DELETE`（既有取消策略不允许删除已审核单据） | 改为先软删除待提交采购单，再经既有 `POST /api/sales-orders/{id}/cancel` 取消来源；仍被来源引用的客户 / 供应商保留在隔离库中 |
| 手工未关联采购在浏览器中无覆盖 | **追加**场景 2b（`采购订单_手工未关联_省略单号与可选来源_保存重新打开与打印`）：留空归属来源 / 单号 / 起运港，保存 → 服务端 `PO` 单号 → 重新打开回显 → 打印预览，全程断言无失败请求；已链接场景（场景 2）原样保留，隔离补丁变体**绝不**整体替换活动夹具 |
| 保存被拒时只留下「等待超时」 | ERP-428 起 `WaitToastContains` 在错误 toast / 失败请求出现时立即 `CaptureEvidence` 并抛出包含原始响应体与明细快照的异常（保留） |

## 5. 验收证据

| 类型 | 文件 | 覆盖 |
|---|---|---|
| 单元 | `src/ERP.UnitTests/PurchaseOrderHttpFormContractTests.cs` | **真实 MVC 正文绑定器 + 真实 `IObjectModelValidator`** 证明省略 / 空串单号通过绑定与校验且不产生 `OrderNo` 错误、伪造单号绑定后由服务端覆盖、归属来源 / 起运港省略或显式 `null` 绑定为 `null` 且正整数精确、非法正文仍按绑定错误拒绝；EF 模型断言列仍 NOT NULL / 长度 50；显式来源 `0` / 负数与起运港 `0` / 负数 / 不存在、无效供应商、非法数量 / 单价 / 税率仍拒绝且零写入（含单号流水未被消耗）；已审核来源的权威快照、未审核来源与来源商品不匹配拒绝、受限业务员链接**外来客户**来源按权威数据范围拒绝且零写入（自有范围内仍可用）；修改省略来源保留血缘、显式 `0` 拒绝；无身份 / 缺菜单 fail closed；前端逐字段白名单与控制器时序（授权 → 条款 → 主数据 → 权威单号）源码契约 |
| 单元 | `src/ERP.UnitTests/SalesOrderHttpFormContractTests.cs` | 前置销售契约测试与采购可选字段兼容：把「全站仅 1 处可选数值字段」的**过期单值断言**替换为**精确有限白名单**（销售目的港 + 采购归属来源 / 起运港共 3 处），断言其余数值字段保持默认零值；销售字段绑定、权限、非法正文与权威单号断言全部保留 |
| 集成 | `src/ERP.IntegrationTests/PurchaseOrderHttpFormSqlServerTests.cs` | **真实 HTTP**（真实 ERP.Api 进程 + 真实 JWT + 真实 Kestrel/MVC 管线）在**全新 GUID `NEWERP_AUTOTEST` 库**上：省略单号 200 且服务端单号落库（直接 SQL 复核）、伪造单号不决定持久化号码、可选来源 / 起运港留空持久化为 `NULL`、真实已审核来源（既有销售接口新建 → 提交 → 审核 + 同一活商品）的关联采购落库并复核权威来源 / 客户快照、未审核来源与来源 `0` / 负数、起运港 `0` / 负数 / 不存在、供应商 / 商品不存在、数量 / 单价 / 税率非法均受控拒绝且零写入、匿名 401、无菜单账号 403 且零写入；目标护栏 fail-closed |
| 浏览器 | `src/ERP.IntegrationTests/OrderTraceabilityUiTests.cs` | 手工未关联采购（新增）与已链接采购（保留）保存 → 服务端单号 → 重新打开回显 → 打印预览；合法已审核来源经既有接口产生，清理按既有取消策略 |
| 文档 | `docs/purchase-order-http-form.md` | 本文件 |

## 6. 真实 HTTP / SQL 夹具安全口径

- 目标必须是专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且
  `Integrated Security=true`；错误实例 / 错误库名 / 非集成安全的连接串一律在 `AssertDedicatedTarget` 阶段
  于**任何数据库访问之前**被拒绝（含 fail-closed 覆盖）。
- 只使用**全新 GUID 后缀库**：访问前先 `SELECT DB_ID`，发现同名库已存在立即拒绝；
  **绝不 drop / reset / 复用**任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，
  绝不读取 `appsettings*.json` / `.env` / 生产凭据；JWT 只存活在内存，绝不写入日志 / 证据。
- 只使用**既有**登录 / 用户管理 / 主数据 / 销售订单 / 采购订单接口，不新增菜单 / 角色 / 用户授权、不做身份绕过；
  受限账号经既有 `/api/sys/users` 新建且不分配任何角色。
- 浏览器 / HTTP 验收前先确认端口空闲（采购 HTTP 夹具默认端口 `5278`，可用 `ERP_429_HTTP_PORT` 覆盖），并保留
  ERP.Api 控制台尾部输出与失败截图；原始失败日志与拒绝响应体原样保留。
- **构建完成不等于阶段验收**：上述真实 HTTP / SQL / 浏览器场景必须在专用 localdb 上真实执行通过才算验收证据。

## 7. 边界（不做的事）

- 不改写状态机、来源血缘、数量 / 单价 / 金额 / 币种 / 汇率 / 税率等商业语义与审计字段；
- 不启用任何全局模型校验抑制，不放宽必填供应商 / 商品 / 单位 / 条款校验，不放宽身份与菜单授权；
- 不改动其它模块的数值序列化语义（保留「留空 = 0」默认，选择加入仅销售目的港与采购归属来源 / 起运港三处）；
- 不做旧单据读侧切换，不新增菜单 / 角色 / 用户授权，不做部署 / 生产库访问 / 破坏性数据操作。

