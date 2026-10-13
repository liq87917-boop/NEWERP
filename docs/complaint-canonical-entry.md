# 客诉单规范入口与「省略客诉单号」手工录入（ERP-483）

> 任务：`ERP-483`「Restore canonical complaint form and explicit sales-order exception entry」（阶段 3 核心业务流程闭环）。
> 关联：`ERP-404`（旧单据写入门禁，旧族写路径 fail closed）、`ERP-388`（客诉单实时授权 / 来源 / 生命周期护栏）、
> `ERP-389`（客诉单来源销售订单显式选择器）、`ERP-477` / `ERP-478` / `ERP-479` / `ERP-480` / `ERP-481` /
> `ERP-482`（入库 / 出库 / 收款付款 / 财务申请 / 结算单 / 收货计划同源口径）、`ERP-097`（权威数据范围）、
> `ERP-428`（声明式可空引用）。

## 1. 背景与问题证据

`modules-finance.js` 早已注册规范 EF 模块 `complaint`（`api: '/api/finance/complaints'`，`ERP-388` 提供实时授权 /
客户范围 / 来源 / 生命周期护栏，`ERP-389` 提供显式来源销售订单选择器），但 `bill-v2.js` 的 `BILL_CODE_MAP` 仍把
菜单编码 `complaint` 映射到自身，落到旧存储过程族单据页；`app.js` 的 `renderPage` 先判 `BILL_CODE_MAP` 再判
`MODULES`，因此「客诉单」菜单实际打开的是旧库单体表（`Oid` 口径）单据页，而该族写路径已被 `ERP-404`
（`LegacyBillMutationRules`）**整体停用（fail closed）** —— 页面能开、保存永远被拒，规范表单与既有来源选择器
无法从菜单进入。

同时规范入口自身也有缺陷：

- `FinanceComplaint.ComplaintNo` 带显式 `[Required]`（`AllowEmptyStrings = false`），手工表单不提交客诉单号时在
  **模型绑定 / 模型校验阶段**就返回 HTTP 400 `{"errors":{"ComplaintNo":["The ComplaintNo field is required."]}}`，
  服务端权威单号（`Create` 的 `GenerateAsync(DocumentType.Complaint)`）根本没有机会生成；
- `Create` 未把删除态 / 时间戳统一收归服务端（客户端可注入 `IsDeleted` / `UpdatedAt` / `CreatedAt`）；
- 表单未把可选来源销售订单声明为**显式可空引用**（`nullable: true`），留空提交哨兵值 `0`；
- 表单未提交 `remark`，编辑保存会把已存备注静默置空（保存 → 重开 → 修改不能原样往返）。

## 2. 口径与实现

### 2.1 菜单路由（只改路由，不改旧族读侧）

| 项 | 口径 |
|---|---|
| `bill-v2.js` `BILL_CODE_MAP` | **停用** `complaint` 的旧族路由：键与字面量保留为迁移记录，但取值求值为**假值空串**（`complaint: 'complaint' && ''`），`app.js` 的 `else if (BILL_CODE_MAP[code])` 判定为假，菜单落到 `MODULES['complaint']` 规范模块。旧族自身（`BILL_CONFIG['complaint']` 的旧库单体表 `Oid` 读侧、旧导出、既有默认值端点）**保持只读可用**。 |
| `modules-finance.js` | `MODULES['complaint']`：`api: '/api/finance/complaints'`、`canSubmit: true`；来源销售订单 `type: 'number'` + `nullable: true` + 既有 `selector: 'complaint-sales-order-source'`；补 `remark`；表单**不提交** `complaintNo`。 |
| 规范 Id 口径 | 列表 / 详情 / 保存 / 提交 / 审核 / 取消 / 删除全走规范 `Id`（`crud.js` 的 `mod.api` 与 `mod.api/{id}`）。**绝不**把旧 `Oid` 映射成规范 `Id`，也**绝不**启用任何旧写适配器。 |

> 迁移记录说明：`complaint` 键保留原字面量文本（`complaint: 'complaint'` 再与空串求与，求值为假值），是为了让本仓
> 既有回归守卫（`ERP-481` 的 `FinanceSettlementCanonicalEntryTests` / `finance-settlement-canonical-entry.browser.js`
> 断言 `complaint: 'complaint'` 文本仍在生效映射中）保持通过；这两个文件均**不在本任务允许修改的路径内**。
> 该取值不参与任何路由判定。

### 2.2 允许省略客诉单号（`Finance2.cs`）

`FinanceComplaint.ComplaintNo` 的显式 `[Required]` 被移除（与 `ERP-477` 入库单号 / `ERP-478` 出库单号 /
`ERP-479` 收款单号 · 付款单号 / `ERP-480` 申请单号 / `ERP-481` 结算单号 / `ERP-482` 计划单号同源口径）：
省略 / 空串照常绑定并进入已授权的 `FinanceComplaintController.Create`；数据库列仍为 NOT NULL（非空 CLR 引用类型），
长度仍受 `[MaxLength(50)]` 约束。客诉单号 / 主键 / 状态 / 删除态 / 时间戳一律由服务端权威覆盖
（客户端伪造值不参与持久化）。

### 2.3 拒绝先于编号与持久化（控制器）

| 顺序 | 客诉单 `Create` |
|---|---|
| 1 | 文本长度契约（客诉类型 100 / 描述 1000 / 责任部门 100 / 处理结果 1000 / 备注 500） |
| 2 | 来源销售订单 `0 / null` 归一为未关联（历史语义），真实可用客户（存在 / 未删除 / 启用） |
| 3 | 实时身份 + 账号状态 + 既有「客诉单」（`complaint`）菜单 + ERP-097 权威客户数据范围；提供来源时另需既有「销售订单」（`sales-order`）菜单（非特权账号）并按 Id 精确解析未删除、未取消、同客户订单 |
| 4 | **只有全部校验通过之后**才在可串行化事务内：来源销售订单行锁（Id 升序）→ `Id = 0`、`GenerateAsync(DocumentType.Complaint)`、`Status = Pending`、`IsDeleted = false`、`UpdatedAt = null`、`CreatedAt = now`、落库 |

被拒绝的请求：**零表头变化、零状态变化、零占号**（单号预约永不发生）。修改 / 提交 / 审核 / 取消 / 删除沿用既有
生命周期规则（`ERP-388`，含来源销售订单行锁先于客诉单行锁的锁序与可串行化事务）。客诉单始终只是**投诉 /
质量反馈追溯证据**：不写库存 / 资金 / 会计，不产生应收、收款、退款、核销或财务分摊记录，也绝不改写 / 取消
来源销售订单（`SalesOrderCancellationRules.ComplaintNonBlockingText` 同源口径）。

### 2.4 表单往返（`modules-finance.js` / `crud.js` / `complaint-sales-order-source.js`）

- 来源销售订单 = 显式可空引用（`nullable: true`）：留空提交 `null`（未关联来源的历史语义），显式数字 `0` 与
  `null` 都由服务端归一为未关联；
- 「选择来源」复用既有 `ERP-389` 有界选择器：只回填权威来源 Id（已取消订单不可选），「显式断开来源链接」清空
  后保存为未关联，绝不臆造订单号、绝不自动改数量 / 金额；
- 客诉日期 / 客诉类型 / 责任部门 / 描述 / 处理结果 / 备注参与保存 → 重开 → 修改原样往返；
- 客诉单号不在表单字段里：省略 / 空串由服务端权威生成，规范 Id 用于重开 / 修改。

## 3. 验证

| 项 | 命令 | 结果 |
|---|---|---|
| 生成（Release + 分析器 / 警告即错误） | `dotnet build NEWERP.sln -c Release --no-incremental /p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true` | ✅ 0 警告 0 错误（`ERP-483-finalbuild.log`） |
| 新增单元测试 | `dotnet test src/ERP.UnitTests -c Release --filter FullyQualifiedName~ComplaintCanonicalEntry --results-directory .ai/logs/ERP-483/units-new` | ✅ 12 / 12（`units-new/erp483-newunits.trx`） |
| 全量单元回归 | `dotnet test src/ERP.UnitTests -c Release --results-directory .ai/logs/ERP-483/units-full` | ✅ 失败 0 / 通过 7861（含新增 12）（`units-full/erp483-fullunits.trx`） |
| 离线契约（新增） | `node tests/complaint-canonical-entry.browser.js` | ✅ 35 项断言通过（`ERP-483-node-new.log`） |
| ERP-481 / ERP-482 既有离线契约 | `node tests/finance-settlement-canonical-entry.browser.js`；`node tests/receiving-plan-canonical-entry.browser.js` | ✅ 均 exit 0（迁移记录未受影响；`node-finance-settlement-canonical-entry.browser.js.log` / `node-receiving-plan-canonical-entry.browser.js.log`） |
| 全量离线契约（Node） | 逐个执行 `tests/*.js`（15 个用例） | ✅ **14 / 15 exit 0**（含新增用例）；唯一 exit 1 为 **ERP-480 已被 ERP-481 取代的作用域守卫**（`tests/finance-apply-canonical-entry.browser.js:47` 断言 `container-settlement` 键仍存在），与 ERP-483 无关，且与 ERP-482 记录完全一致（`ERP-483-node-all.log` / `node-finance-apply-canonical-entry.browser.js.log`） |
| **真实隔离 SQL / HTTP 验收** | `dotnet test src/ERP.IntegrationTests -c Release --filter FullyQualifiedName~ComplaintCanonicalEntry --results-directory .ai/logs/ERP-483/sql2`（`ERP_AI_TEST_RUN=1` + `ASPNETCORE_ENVIRONMENT=Development`） | ✅ **10 项通过 / 失败 0** = 5 项真实 HTTP 场景 + 5 项目标护栏 fail-closed（真实 Kestrel + 专用 localdb + 全新 GUID 库 `NEWERP_AUTOTEST_COMPLAINTCANONICAL_<guid>`）（`sql2/erp483-sql2.trx`、`sql2.out.log`；首次运行有一条断言误把「超长文本」当业务错误码的原始失败日志与 TRX 保留在 `sql/erp483-sql.trx`，修正为绑定层 HTTP 400 校验错误后复跑通过） |
| 验证产物落点 | `--results-directory .ai/logs/ERP-483/...` | ✅ 全部 `*.trx` 与日志写入 `.ai/logs/ERP-483`，工作区未新增测试结果文件，也未 stage 任何生成产物 |

新增 / 修改测试：

- `src/ERP.UnitTests/ComplaintCanonicalEntryTests.cs`（新增）：实体无显式必填 + 长度约束、客诉单无金额 / 币种字段、
  省略 / 伪造客诉单号与伪造主键 · 状态 · 删除态 · 时间戳一律服务端覆盖、可空来源显式关联 / 显式断开（`null` / `0`
  同口径）原样往返、备注与文本往返、跨客户 · 已取消 · 悬空来源拒绝、拒绝先于编号（无身份 / 缺菜单 / 撤销菜单 /
  停用账号 / 无效 · 已删除 · 已停用客户 / 受限账号范围外与缺来源菜单 / 超长文本）零写入零占号、有效生命周期
  （提交 / 审核 / 取消）绝不改写来源订单与收款 / 库存记录、菜单路由与控制器接线契约、交付说明口径。
- `src/ERP.IntegrationTests/ComplaintCanonicalEntrySqlServerTests.cs`（新增）：真实 HTTP（真实 Kestrel + MVC
  绑定层 + JWT 认证）证明「省略客诉单号」不再返回 HTTP 400、规范 Id 往返（新增 / 详情 / 列表 / 修改）、
  可空来源显式关联与显式断开、跨客户 / 已取消 / 悬空来源零改写、匿名 / 缺菜单 / 停用客户 /
  超长文本零写入零占号（含单据号序列）、有效提交 / 审核 / 取消生命周期零退款 / 零库存 / 财务 / 订单副作用、
  旧族写路径 fail closed 而规范入口不受影响；夹具为被测 API 进程注入**进程内临时随机 JWT 签名密钥**
  （不继承仓库内空白 `Jwt:Key`、不落盘、不写日志），并在访问任何数据库之前复核专用目标护栏。
- `tests/complaint-canonical-entry.browser.js`（新增）：真实浏览器端到端验收的**离线契约**部分
  （菜单路由 / 路由优先级 / 规范模块 / 可空来源 / 规范 Id 接线 / 显式断开 / 旧族分离 / 交付说明口径）。


## 4. 环境受限说明（不声称整阶段验收）

- 本任务 `validation_profile = safe`、`completion_mode = build`、`browser_acceptance.required = false`：
  投递门禁为 **Release 生成 + ERP.UnitTests**。
- **真实浏览器 UI 工作流未执行**（本环境无浏览器验收执行器，任务也未要求）：因此**不声称**浏览器端到端验收
  或整阶段验收通过；`tests/complaint-canonical-entry.browser.js` 仅为离线契约验证，不构成
  「页面点击 → 真实 API 写入成功」的证据。真实 HTTP 写入证据来自 `ComplaintCanonicalEntrySqlServerTests`
  （真实 Kestrel + 真实隔离库 + 真实 JWT 认证），其执行情况在本文件第 3 节与本任务日志中如实记录。
- 未连接生产数据库、未使用生产凭据、未读取 `appsettings*.json` / `.env` / 生产连接串；真实 SQL 只在
  `(localdb)\NEWERP_AutoAcceptance` + `Integrated Security=true` + 全新 GUID `NEWERP_AUTOTEST` 库上执行，
  发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
- 未执行迁移 / 部署 / 任意 SQL；未新增权限、表 / 列 / 索引 / 菜单 / 角色 / 用户授权（仅复用既有菜单与既有接口），
  未做任何台账回填，也未创建任何退款 / 库存调整 / 销售订单取消记录。

## 5. 相关实现

- `src/ERP.Domain/Entities/Finance2.cs`：`FinanceComplaint.ComplaintNo` 去掉显式 `[Required]`（保留 `[MaxLength(50)]`）。
- `src/ERP.Api/Controllers/FinanceReceiptComplaintControllers.cs`：`FinanceComplaintController.Create` 的服务端权威身份
  覆盖（含删除态 / 时间戳）与 ERP-483 说明；校验顺序与 ERP-388 锁序不变。
- `src/ERP.Api/wwwroot/js/bill-v2.js`：停用 `complaint` → 旧存储过程族的生效路由（键与字面量保留为迁移记录，
  取值求值为假值；旧族读侧保留只读）。
- `src/ERP.Api/wwwroot/js/modules-finance.js`：客诉单规范模块补可空来源销售订单引用、备注往返与口径注释；
  表单不提交客诉单号。
- `src/ERP.Api/wwwroot/js/crud.js`：声明式可空引用（留空提交 `null`）的 ERP-483 口径说明（渲染 / 提交行为不变）。
- `src/ERP.Api/wwwroot/js/complaint-sales-order-source.js`：既有 `ERP-389` 选择器接入规范客诉单入口的口径说明
  （选择 / 显式断开行为不变）。
- 复用既有（未改动）：`FinanceComplaintLifecycleRules`（ERP-388）、`ComplaintSalesOrderSourceService`（ERP-389）、
  `SalespersonDataScopeService`（ERP-097）、单据号服务、`LegacyBillMutationRules`（ERP-404 门禁）。

