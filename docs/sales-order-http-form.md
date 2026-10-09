# 手工销售订单录入：服务端权威单号 + 可选数值留空（ERP-428）

## 1. 背景与原始失败证据

「订单管理 → 销售订单」手工录入页不提交订单号（单号由服务端生成），但真实浏览器验收在保存时被
**HTTP 400** 挡在控制器之前：

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.",
 "status":400,"errors":{"OrderNo":["The OrderNo field is required."]},"traceId":"..."}
```

- 失败原因为 `SalesOrder.OrderNo` 上的显式 `[Required]`：MVC 在**模型校验**阶段就把省略的单号判为非法，
  `SalesOrderController.Create` 根本没有机会执行「授权 → 校验 → 预约单号 → 落库」。
- 同一轮验收还暴露出第二个问题：手工表单里可留空的「目的港 Id」（`PortId`）被前端序列化为 `0`
  （而不是 `null`），服务端随即按「显式非法引用」拒绝。
- 原始失败响应体 / 截图保留在 `.ai/logs/heartbeat-20261009T0140-sales-manual-browser-response-diagnosis/`
  与 `.ai/logs/heartbeat-20261009T0140-sales-manual-browser-nullable-port-repair/`，**不被改写**。

## 2. HTTP 绑定契约（服务端自有单号允许省略）

`src/ERP.Domain/Entities/SalesOrder.cs`：`OrderNo` **去掉显式 `[Required]`**，保留 `[MaxLength(50)]`。

| 维度 | 修复后口径 |
|---|---|
| 绑定 / 校验 | 省略 / 空串 `orderNo` 绑定为 `string.Empty`；MVC 对非空引用类型的隐式必填是 `AllowEmptyStrings = true`，因此不再返回 400 |
| 数据库 | 列仍是 **NOT NULL**（非空 CLR 引用类型 + 唯一索引），长度仍为 50（`[MaxLength(50)]` 经 EF 模型体现），由单元测试用 SQL Server 提供程序**仅构建模型**断言 |
| 权威单号 | `Create` 在既有授权（`EnsureCanonicalWriteAuthorizedAsync`）、条款校验（`SalesOrderMutationRules.EnsureValidatedTerms`）、来源血缘与实时主数据校验（`SalesOrderMasterReferenceRules.EnsureMasterReferencesAsync`）**之后**才 `entity.OrderNo = await _noService.GenerateAsync(DocumentType.SalesOrder)`：调用方提交的值只可能出现在绑定结果里，**绝不决定持久化单号** |
| 未放宽的护栏 | 非法正文仍按绑定错误拒绝；必填客户 / 明细商品 / 单位 / 数量金额条款校验、状态 / 来源 / 审计行为保持不变；**不做任何全局校验抑制**（`MvcOptions.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes` 未被使用） |

## 3. 前端可选数值序列化（逐字段选择加入）

`src/ERP.Api/wwwroot/js/crud.js`：

```js
if (f.type === 'number') v = v === '' ? (f.nullable === true ? null : 0) : Number(v);
```

`src/ERP.Api/wwwroot/js/modules-doc.js`：

```js
{ key: 'portId', label: '目的港 Id（港口字典，可留空）', type: 'number', nullable: true },
```

- **选择加入**：只有显式声明 `nullable: true` 的数值字段留空时才提交 `null`；全站模块目录里仅销售订单
  目的港这一处，其它模块的必填数值字段仍保持「留空 = 0」的既有口径。
- 正整数目的港原样提交（绑定值精确相等）；`0` / 负数 / 不存在的港口仍由既有
  `SalesOrderMasterReferenceRules` 在服务端拒绝（`1001` / `1002`），拒绝时零写入。

## 4. 真实浏览器夹具修复（ERP-428）

`src/ERP.IntegrationTests/OrderTraceabilityUiTests.cs`：

| 问题 | 修复 |
|---|---|
| `ExecuteScript` 返回的 JavaScript 布尔字符串是 `"true"`，夹具却按 `"True"` 断言 | `FunctionExists` / `SetSelectValue` / `HasPrintPage` 统一改为 `"true"`（真实浏览器断言不再被字符串大小写误判） |
| 明细硬编码 `productId = 1`（隔离库里不存在，会被 ERP-423 主数据护栏拒绝） | 新增 `CreateProductViaApi`，经既有 `/api/base/products` 新建真实商品，并把 Id 传给 `AddDetailRow` 与接口建单助手；三个场景全部改用活商品 Id |
| 保存被拒时只留下「等待超时」，原始拒绝体丢失 | `WaitToastContains` 在错误 toast（或失败请求）出现时立刻 `CaptureEvidence("sales-order-save-rejection")` 并抛出包含 **原始响应体 + 明细快照**的异常；`InstallApiIssueRecorder` 记录 `message | title | body` |
| 手工保存 / 服务端单号 / 重新打开回显 / 打印 / Excel | 保持原有验收路径与检查不变（`SO` 前缀、字段回显、`.print-page`、Excel `spreadsheetml` 与体积下限） |

## 5. 验收证据

| 类型 | 文件 | 覆盖 |
|---|---|---|
| 单元 | `src/ERP.UnitTests/SalesOrderHttpFormContractTests.cs` | **真实 MVC 正文绑定器 + 真实 `IObjectModelValidator`** 证明省略 / 空串单号通过绑定与校验且不产生 `OrderNo` 错误、伪造单号绑定后由服务端覆盖、可选目的港留空 / 显式 `null` 绑定为 `null` 且正整数精确、非法正文仍按绑定错误拒绝；EF 模型断言列仍 NOT NULL / 长度 50；`0` / 负数 / 不存在目的港与无身份 / 缺菜单仍拒绝且零写入；前端「逐字段选择加入」与控制器时序（授权 → 主数据 → 权威单号）源码契约 |
| 集成 | `src/ERP.IntegrationTests/SalesOrderHttpFormSqlServerTests.cs` | **真实 HTTP**（真实 ERP.Api 进程 + 真实 JWT + 真实 Kestrel/MVC 管线）在**全新 GUID `NEWERP_AUTOTEST` 库**上：省略单号 200 且服务端单号落库（直接 SQL 复核）、伪造单号不决定持久化号码、可选目的港留空持久化为 `NULL`、目的港 `0` / 负数 / 不存在与不存在商品受控拒绝且零写入、匿名 401、无菜单账号 403 且零写入；目标护栏 fail-closed |
| 浏览器 | `src/ERP.IntegrationTests/OrderTraceabilityUiTests.cs` | 手工销售订单保存 → 服务端单号 → 重新打开回显 → 打印预览 → Excel 导出（活商品 Id、正确的布尔断言、失败即保留原始证据） |
| 文档 | `docs/sales-order-http-form.md` | 本文件 |

## 6. 真实 HTTP / SQL 夹具安全口径

- 目标必须是专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且
  `Integrated Security=true`；错误实例 / 错误库名 / 非集成安全的连接串一律在 `AssertDedicatedTarget` 阶段
  于**任何数据库访问之前**被拒绝（含 fail-closed 覆盖）。
- 只使用**全新 GUID 后缀库**：访问前先 `SELECT DB_ID`，发现同名库已存在立即拒绝；
  **绝不 drop / reset / 复用**任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，
  绝不读取 `appsettings*.json` / `.env` / 生产凭据；JWT 只存活在内存，绝不写入日志 / 证据。
- 只使用**既有**登录 / 用户管理 / 主数据 / 销售订单接口，不新增菜单 / 角色 / 用户授权、不做身份绕过；
  受限账号经既有 `/api/sys/users` 新建且不分配任何角色。
- 浏览器 / HTTP 验收前先确认端口空闲，并保留 ERP.Api 控制台尾部输出与失败截图；原始失败日志与拒绝响应体
  原样保留。
- **构建完成不等于阶段验收**：上述真实 HTTP / SQL / 浏览器场景必须在专用 localdb 上真实执行通过才算验收证据。

## 7. 边界（不做的事）

- 不改写状态机、来源血缘、数量 / 单价 / 金额 / 币种 / 汇率 / 比例等商业语义与审计字段；
- 不启用任何全局模型校验抑制，不放宽必填客户 / 商品 / 单位 / 条款校验，不放宽身份与菜单授权；
- 不改动其它模块的数值序列化语义（保留「留空 = 0」默认，选择加入仅销售订单目的港一处）；
- 不做旧单据读侧切换，不新增菜单 / 角色 / 用户授权，不做部署 / 生产库访问 / 破坏性数据操作。
