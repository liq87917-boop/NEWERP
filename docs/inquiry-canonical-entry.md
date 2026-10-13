# 询价单规范入口与商业承诺校验（ERP-474）

> 任务：`ERP-474`「Restore canonical inquiry form entry and validate inquiry commercial commitment」（阶段 3 核心业务流程完整性）。
> 关联：`ERP-404`（旧单据写入门禁，旧族写路径 fail closed）、`ERP-469`（网页手工测试回归：默认值端点 / 整数天 / 标签栏）、
> `ERP-470` / `ERP-471` / `ERP-472` / `ERP-473`（报价单 / PI 商业条款与实时主数据引用契约）。

## 1. 背景与问题证据

`modules-doc.js` 早已注册规范 EF 主子表模块 `inquiry-new`（`api: '/api/inquiries'`），但 `bill-v2.js` 的
`BILL_CODE_MAP` 仍把菜单编码 `inquiry-new` 映射到旧存储过程族 `inquiry`。`app.js` 的 `renderPage` 先判 `BILL_CODE_MAP`
再判 `MODULES`，因此菜单实际打开的是旧库单体表（`Oid` 口径）单据页；而该族的写路径已被 `ERP-404`
（`LegacyBillMutationRules`）**整体停用（fail closed）** —— 结果就是「询价单新建无法完成」：页面能开，保存永远被拒。

同时规范入口自身也有缺陷：

- `Inquiry.InquiryNo` 带显式 `[Required]`（`AllowEmptyStrings = false`），手工表单不提交询价单号时在**模型绑定阶段**
  就返回 HTTP 400，服务端权威单号根本没有机会生成；
- `InquiryController.Create` / `Update` 用 `d.Amount = d.Quantity * d.UnitPrice` **未校验地**相乘，
  未校验币种 / 汇率 / 有效期（未定义币种、0 / 负 / 超精度汇率、越界天数一律落库），且在**校验之前**就预约消耗单据号；
- 提交 / 审核 / 转报价单不复核已持久化商业条款，非法历史条款可成为实时履约。

## 2. 口径与实现

### 2.1 菜单路由（只改路由，不改旧族读侧）

| 项 | 口径 |
|---|---|
| `bill-v2.js` `BILL_CODE_MAP` | **移除** `'inquiry-new': 'inquiry'` 映射（保留注释说明原因）。旧族自身（`BILL_CONFIG.inquiry` 的旧库单体表 `Oid` 读侧、旧导出、既有默认值端点）**保持只读可用**。 |
| `modules-doc.js` `MODULES['inquiry-new']` | 规范 EF 主子表模块：`api: '/api/inquiries'`，新增 `detailKey: 'details'`、`detailTitle`、`detailAmount` 与 `detailFields: INQUIRY_DETAIL_FIELDS`（`productId / productName / spec / unit / quantity / unitPrice / amount(只读) / remark`），使新增 / 编辑表单成为可用的**主表 + 明细**表单。 |
| 规范 Id 口径 | 列表 / 详情 / 保存 / 回填 / 提交 / 审核 / 预填 / 转报价单全部走规范 `Id`（`crud.js` 的 `mod.api` + `crud.js`/`sales-pi.js` 的 `api/inquiries/{id}/...`）。**绝不**把旧 `Oid` 映射成规范 `Id`，也**绝不**启用任何旧写适配器。 |

### 2.2 允许省略询价单号（`Inquiry.cs`）

`InquiryNo` 的显式 `[Required]` 被移除（与 `ERP-428` 的订单号同源口径）：省略 / 空串照常绑定并进入已授权的
`InquiryController.Create`；数据库列仍为 NOT NULL（非空 CLR 引用类型 + 唯一索引），长度仍受 `[MaxLength(50)]` 约束。
单号 / 主键 / 状态 / 删除态一律由服务端权威覆盖（客户端伪造值不参与持久化）。

### 2.3 唯一权威商业条款契约（`InquiryCommercialValidationRules`）

新增 `src/ERP.Application/Services/InquiryCommercialValidationRules.cs`，询价单手工写入与运营承诺**共用同一契约**：

| 项 | 规则 |
|---|---|
| 币种 | 必须是 `Currency` 已定义值（未定义值拒绝） |
| 汇率 | `> 0` 且在 `DECIMAL(18,2)` 内可表示（**绝不**把 0 / 负数静默替换为 1） |
| 有效期（天） | 必须是 **1 ~ 3650 的整数天**（拒绝 0 / 负数 / 超范围，**绝不**静默回退默认天数） |
| 逐行 | 数量 `> 0`、单价 `>= 0`，且均在 `DECIMAL(18,2)` 内可表示 |
| 行金额 | 一律由服务端按 `CurrencyAmountRules` 的币种精度（JPY / KRW / VND / IDR 为 0 位、其余 2 位、0.5 进位）从有效明细**派生**，绝不采信客户端行金额 |
| 软删除行注入 | 客户端提交 `IsDeleted = true` 的明细行一律拒绝 |

两种调用口径：`NormalizeIncomingDraft`（新增 / 修改：允许显式支持的**空草稿**，但被提交的行必须合法，并派生行金额）
与 `ValidateOperational`（提交 / 审核 / 转报价单：**至少一行有效明细**，非法历史条款绝不进入履约）。

### 2.4 控制器接入点（拒绝先于编号与持久化）

| 路由 | 接入点 |
|---|---|
| `POST api/inquiries` | 授权 + 拟议客户范围 → `NormalizeIncomingDraft` → 之后才 `GenerateAsync(Inquiry)` / 覆盖 `Id = 0` / `Status = Pending` / `IsDeleted = false` / 写入 |
| `PUT api/inquiries/{id}` | `NormalizeIncomingDraft`（在开事务 / 加锁之前）→ 锁内 `EnsureProposedCustomerInScope` / `EnsureNoLiveDownstream` / `EnsureEditAllowed` → 改写 |
| `POST {id}/submit`、`{id}/approve` | 锁内权威重读后、状态改写之前 `ValidateOperational` |
| `POST {id}/to-quotation`、`GET {id}/quotation-prefill` | `InquiryQuotationConversion.BuildDraftAsync` 在映射 / 占号 / 任何写入之前 `ValidateOperational(inquiry)` |

被拒绝的请求：**零表头 / 明细 / 来源 / 状态变化、零单号消耗、整体回滚**；既有实时身份 / 菜单 / 客户范围授权、
来源行锁、原子事务、受控错误与审计全部保留（本任务不新增权限、表 / 列 / 索引 / 菜单 / 角色、生产访问或任意 SQL）。

前端 `ERP-469` 口径**保持不变**：旧族页面仍复用既有（参数授权）默认值端点 `GET /api/v2/bills/{billType}/defaults`、
整数天输入（`billIntegralDays` / `step=1` / `min=0`）与「清空重填」，标签栏与新单据隔离行为不变。

## 3. 验证证据

| 证据 | 命令 | 结果 |
|---|---|---|
| 前端路由 / 主子表接线契约（离线） | `node tests/inquiry-canonical-entry.browser.js` | ✅ 48 项断言通过 |
| 既有前端回归（ERP-469 等） | `node tests/*.test.js` | ✅ 全部通过（exit 0） |
| 询价单单元测试 | `dotnet test src/ERP.UnitTests -c Release --filter "FullyQualifiedName~Inquiry"` | ✅ 98 项通过 |
| 全量单元测试 | `dotnet test src/ERP.UnitTests -c Release` | ✅ 7637 项通过 |
| Release 生成（含分析器 / 警告即错误） | `dotnet build NEWERP.sln -c Release` | ✅ 0 警告 0 错误 |

新增 / 更新单元测试：

- `src/ERP.UnitTests/InquiryCommercialValidationTests.cs`（新增）：契约纯规则（币种 / 汇率 / 有效期边界 / 数量 / 单价 /
  软删除注入 / 金额精度与溢出 / EF `DECIMAL(18,2)` 模型契约）+ 控制器行为（新增 / 修改非法条款零写入零占号、
  空草稿可保存、伪造单号与身份状态被服务端覆盖、提交 / 审核 / 转报价单承诺前复核已持久化条款）。
- `InquiryControllerTests.cs`：`InquiryNo` 不再显式必填（反射）、省略单号的创建、非法条款在单号生成之前拒绝。
- `InquiryMutationTests.cs`：控制器接线（`NormalizeIncomingDraft` / `ValidateOperational` 先于单号与写入）、
  菜单路由不再落到旧存储过程族且规范模块为主子表。
- `InquiryQuotationConversionTests.cs`：转换来源汇率拒绝文案改由 `InquiryCommercialValidationRules` 唯一权威契约给出
  （同一拒绝语义与错误码，行为不变）。

真实隔离 SQL / HTTP 与浏览器测试：

- `src/ERP.IntegrationTests/InquiryCanonicalEntrySqlServerTests.cs`（新增）：真实 ERP.Api 进程 + 全新 GUID
  `NEWERP_AUTOTEST` 库，覆盖空白单号新增（返回服务端单号与规范 `Id`）、伪造单号覆盖、主子表保存 / 回填 / 修改、
  非法条款（未定义币种 / 0 与超精度汇率 / 0 数量 / 负单价 / 0 天有效期 / 软删除明细注入）零写入零占号、
  无身份 401 / 无菜单 403 零写入、授权状态流（提交 → 审核 → 转报价单）、旧族写路径 fail closed 而规范入口不受影响；
  以及专用目标护栏（错误实例 / 库名 / 非集成安全在访问数据库之前拒绝）。
- `tests/inquiry-canonical-entry.browser.js`（新增）：真实浏览器端到端验收的**离线契约**部分（路由 / 模块 / 明细字段 /
  规范 Id 接线 / 旧族分离）——真实浏览器 + 真实测试库 + 真实授权身份的端到端场景由上述 IntegrationTests
  （`Collection=UiTests` / 专用 localdb）执行，本文件绝不伪造成功业务写入。

## 4. 环境受限说明（不声称整阶段验收）

- 本任务 `validation_profile = safe`、`completion_mode = build`、`browser_acceptance.required = false`：
  投递门禁为 **Release 生成 + ERP.UnitTests**。真实 SQL / 浏览器端到端用例只有在受控
  `(localdb)\NEWERP_AutoAcceptance` + 全新 GUID `NEWERP_AUTOTEST` 库上真实执行通过才构成验收证据；
  本次未在真实浏览器 / 真实隔离库上执行端到端场景，因此**不声称**浏览器或整阶段验收通过。
- 未连接生产数据库、未使用生产凭据、未读取 `appsettings*.json` / `.env` / 生产连接串；未 drop / reset / 复用任何数据库。
- 未执行迁移 / 部署 / 任意 SQL；未新增权限、表 / 列 / 索引 / 菜单 / 角色 / 用户授权。
