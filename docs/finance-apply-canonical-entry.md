# 定金申请单 / 货款申请单规范入口与「省略申请单号」手工录入（ERP-480）

> 任务：`ERP-480`「Restore canonical deposit and payment application form entry」（阶段 3 核心业务流程闭环）。
> 关联：`ERP-404`（旧单据写入门禁，旧族写路径 fail closed）、`ERP-479`（收款单 / 付款单同源口径）、
> `ERP-477` / `ERP-478`（入库 / 出库规范入口同源口径）、`ERP-380`（货款申请单生命周期与锁序）、
> `ERP-381`（定金申请单生命周期与锁序）、`ERP-390`（定金申请单来源销售订单选择器）、
> `ERP-391`（货款申请单来源销售订单选择器）、`ERP-370`（实时授权与客户数据范围）、`ERP-428`（声明式可空引用）。

## 1. 背景与问题证据

`modules-finance.js` 早已注册规范 EF 模块 `deposit-apply`（`api: '/api/finance/deposit-applies'`）与
`payment-apply`（`api: '/api/finance/payment-applies'`），但 `bill-v2.js` 的 `BILL_CODE_MAP` 仍把菜单编码
`deposit-apply` / `payment-apply` 分别映射到自身，落到旧存储过程族单据页；`app.js` 的 `renderPage`
先判 `BILL_CODE_MAP` 再判 `MODULES`，因此「定金申请单」「货款申请单」菜单实际打开的是旧库单体表
（`Oid` 口径）单据页，而该族写路径已被 `ERP-404`（`LegacyBillMutationRules`）**整体停用（fail closed）** ——
页面能开、保存永远被拒，规范表单与既有来源选择器工作台无法从菜单进入。

同时规范入口自身也有缺陷：

- `FinanceDepositApply.ApplyNo` / `FinancePaymentApply.ApplyNo` 带显式 `[Required]`（`AllowEmptyStrings = false`），
  手工表单不提交申请单号时在**模型绑定 / 模型校验阶段**就返回 HTTP 400
  `{"errors":{"ApplyNo":["The ApplyNo field is required."]}}`，服务端权威单号根本没有机会生成；
- 新增入口未把删除态 / 时间戳统一收归服务端（客户端可注入 `IsDeleted` / `UpdatedAt`）；
- 申请单表单未把来源销售订单声明为**显式可空引用**（留空仍提交哨兵值 `0`），「显式清空来源」语义不够清晰。

## 2. 口径与实现

### 2.1 菜单路由（只改路由，不改旧族读侧）

| 项 | 口径 |
|---|---|
| `bill-v2.js` `BILL_CODE_MAP` | **停用** `'deposit-apply': 'deposit-apply'` 与 `'payment-apply': 'payment-apply'` 两条旧族路由（原地停用为迁移记录，不再参与任何路由判定；ERP-480 注释说明原因）。旧族自身（`BILL_CONFIG['deposit-apply']` / `BILL_CONFIG['payment-apply']` 的旧库单体表 `Oid` 读侧、旧导出、既有默认值端点）**保持只读可用**。 |
| `modules-finance.js` | `MODULES['deposit-apply']`：`api: '/api/finance/deposit-applies'`、`canSubmit: true`；`MODULES['payment-apply']`：`api: '/api/finance/payment-applies'`、`canSubmit: true`；两者的来源字段 `salesOrderId` 改为 `type: 'number'` + `nullable: true` + `selector: 'finance-apply-sales-order-source'`。 |
| 规范 Id 口径 | 列表 / 详情 / 保存 / 提交 / 审核 / 取消 / 删除全走规范 `Id`（`crud.js` 的 `mod.api` 与 `mod.api/{id}`）。**绝不**把旧 `Oid` 映射成规范 `Id`，也**绝不**启用任何旧写适配器。 |
| 既有来源选择器 | 复用既有 ERP-390 / ERP-391 共享实现（`finance-apply-sales-order-source.js`，按当前模块路由对应申请单接口）：只回填权威来源 `Id`，已取消 / 币种不一致候选不可选，可显式断开，绝不自动改金额 / 汇率 / 币种。**不新增接口、不新增取数通道。** |

### 2.2 允许省略申请单号（`Finance1.cs`）

`FinanceDepositApply.ApplyNo` 与 `FinancePaymentApply.ApplyNo` 的显式 `[Required]` 被移除（与 `ERP-477` 入库单号 /
`ERP-478` 出库单号 / `ERP-479` 收款单号 · 付款单号同源口径）：省略 / 空串照常绑定并进入已授权的
`FinanceDepositApplyController.Create` / `FinancePaymentApplyController.Create`；数据库列仍为 NOT NULL
（非空 CLR 引用类型），长度仍受 `[MaxLength(50)]` 约束。单号 / 主键 / 状态 / 删除态 / 时间戳一律由服务端权威覆盖
（客户端伪造值不参与持久化）。

### 2.3 拒绝先于编号与持久化（控制器）

| 顺序 | 定金申请单 `Create` | 货款申请单 `Create` |
|---|---|---|
| 1 | 来源 `SalesOrderId` 显式断开归一化（`> 0` 才保留）、币种规范化 + 支持范围校验、金额按币种精度取整后 > 0、汇率 > 0 | 同左 |
| 2 | 客户存在 / 未删除 / 启用（不存在按不存在拒绝、停用按规则冲突拒绝） | 同左 |
| 3 | 实时身份 + 既有「定金申请单」（`deposit-apply`）菜单 + ERP-097 客户数据范围 | 实时身份 + 既有「货款申请单」（`payment-apply`）菜单 + 客户数据范围 |
| 4 | 来源销售订单按 `Id` 精确解析（未删除、未取消、客户一致、币种兼容；空来源保留历史未关联语义） | 同左 |
| 5 | **只有全部校验通过之后**才：`Id = 0`、`GenerateAsync(DocumentType.DepositApply)`、`Status = Pending`、`IsDeleted = false`、`UpdatedAt = null`、`CreatedAt = now`、落库（关系型后端在同一事务内先取来源销售订单行锁、锁内复核来源资格） | 同左（`GenerateAsync(DocumentType.PaymentApply)`） |

被拒绝的请求：**零表头变化、零来源改写、零状态变化、零占号**（单号预约永不发生）。
提交 / 审核 / 取消 / 删除仍沿用既有生命周期规则：授权与持久化字段复核先于状态写入，失败整体回滚。

### 2.4 可空来源引用与显式选择 / 清空

| 项 | 口径 |
|---|---|
| 实体 | `FinanceDepositApply.SalesOrderId` / `FinancePaymentApply.SalesOrderId` 为 `long?`（既有定义，未改动 schema）。 |
| 表单 | `salesOrderId` 声明 `nullable: true`：留空提交 `null`（声明式可空引用，绝不回落为哨兵值 `0`）；「清空」即显式断开来源。 |
| 服务端 | 控制器把 `null` / `0` 都归一为「未关联来源」（历史未链接语义）；显式 `Id` 必须按 Id 解析到既有、未删除、未取消、客户一致且币种兼容的销售订单。 |
| 选择器 | 复用既有 ERP-390 / ERP-391 选择器：候选严格按精确客户 + 归一化币种返回（已取消 / 币种不一致显式标记不可选），只回填权威来源 Id；客户 / 币种变更即失效、陈旧异步响应丢弃。 |
| 历史只读 | 来源事后被取消 / 删除时，既有 `{id}/sales-order-source` 只读接口按规范 Id 原样回显并显式标注，绝不静默清除 / 重绑定。 |

### 2.5 保留的既有守卫（本任务不改口径）

| 守卫 | 口径 |
|---|---|
| 定金申请单生命周期 | `ERP-381`：来源销售订单行锁 → 申请单行锁（`UPDLOCK/HOLDLOCK` 同一把上游订单锁），`SalesOrderCancellationRules` 与申请单证据协同。 |
| 货款申请单生命周期 | `ERP-380`：申请单行锁与引用付款护栏（存在未删除且未取消的引用付款单时拒绝取消 / 删除 / 商业改动）。 |
| 实时授权 | `ERP-370`：列表 / 详情 / 创建 / 修改 / 提交 / 审核 / 取消 / 删除每次都重新校验实时身份、既有菜单授权与客户数据范围。 |
| 金额 / 币种 / 汇率 | `CurrencyAmountRules`：按币种精度取整（0.5 进位）后金额 > 0、汇率 > 0、币种必须在既有系统口径内（不做汇率换算）。 |
| 审计与受控错误 | 失败整体回滚，状态、原始字段、删除标记与审计不变；错误仍为既有业务错误码。 |
| 业务单据页 / 基础资料抽屉 | `crud.js` 主子表单与基础资料抽屉行为不变（本次只在既有可空引用注释中补充说明，未改渲染 / 提交语义）。 |

## 3. 验证

| 项 | 命令 | 结果 |
|---|---|---|
| Release 生成（分析器 + 警告即错误） | `dotnet build NEWERP.sln -c Release /p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true` | ✅ 0 警告 0 错误 |
| 新增单元测试 | `dotnet test src/ERP.UnitTests -c Release --filter "FullyQualifiedName~FinanceApplyCanonicalEntryTests"` | ✅ 36 项通过（失败 0） |
| 全量单元测试 | `dotnet test src/ERP.UnitTests -c Release` | ✅ 7809 项通过（失败 0 / 跳过 0，24 s） |
| 前端离线契约 | `node tests/finance-apply-canonical-entry.browser.js` | ✅ 51 项离线断言通过 |
| 既有来源选择器 / 路由回归 | `node tests/*.js`（12 个既有用例，含 `deposit-apply-sales-order-source`、`payment-apply-sales-order-source`、`receipt-payment-canonical-entry.browser`） | ✅ 全部 exit 0 |
| **真实隔离 SQL / HTTP 验收** | `dotnet test src/ERP.IntegrationTests -c Release --filter "FullyQualifiedName~FinanceApplyCanonicalEntry"`（`ERP_AI_TEST_RUN=1` + `ASPNETCORE_ENVIRONMENT=Development`） | ✅ 27 项通过 = 23 项真实 HTTP / SQL 场景 + 4 项目标护栏 fail-closed（真实 Kestrel + 专用 localdb + 全新 GUID 库 `NEWERP_AUTOTEST_FINANCEAPPLYCANONICAL_<guid>`） |
| 验证产物落点 | `--results-directory .ai/logs/ERP-480/...` | ✅ 全部 `*.trx` / 日志写入 `.ai/logs/ERP-480`，工作区根目录未新增任何测试结果文件 |

新增测试：

- `src/ERP.UnitTests/FinanceApplyCanonicalEntryTests.cs`（新增）：实体无显式必填 + 长度约束、
  省略 / 空串 / 伪造申请单号与伪造主键 · 状态 · 删除态 · 时间戳一律服务端覆盖、无身份 / 缺菜单 / 客户不可用 /
  非法币种与金额与汇率 / 来源悬空 · 已取消 · 跨客户 · 币种不一致一律零写入零占号、可空来源引用显式关联与清空、
  往返（详情 / 列表 / 修改）、有效生命周期与非法流转零状态变化、来源候选按精确客户、菜单路由与控制器接线契约。
- `src/ERP.IntegrationTests/FinanceApplyCanonicalEntrySqlServerTests.cs`（新增）：真实 HTTP（真实 Kestrel +
  MVC 绑定层 + JWT 认证）证明「省略申请单号」不再返回 HTTP 400、规范 Id 往返（新增 / 回读 / 修改 / 列表）、
  显式来源销售订单关联与清空、有效提交 / 审核 / 取消生命周期、越权 / 非法请求零写入零占号、旧族写路径 fail closed；
  夹具为被测 API 进程注入**进程内临时随机 JWT 签名密钥**（不继承仓库内空白 `Jwt:Key`、不落盘、不写日志）。
- `tests/finance-apply-canonical-entry.browser.js`（新增）：真实浏览器端到端验收的**离线契约**部分
  （菜单路由 / 路由优先级 / 规范模块 / 可空来源引用 / 既有选择器接线 / 规范 Id 接线 / 旧族分离）。

## 4. 环境受限说明（不声称整阶段验收）

- 本任务 `validation_profile = safe`、`completion_mode = build`、`browser_acceptance.required = false`：
  投递门禁为 **Release 生成 + ERP.UnitTests**。
- **真实浏览器 UI 工作流未执行**（本环境无浏览器验收执行器，任务也未要求）：因此**不声称**浏览器端到端验收
  或整阶段验收通过；`tests/finance-apply-canonical-entry.browser.js` 仅为离线契约验证，不构成
  「页面点击 → 真实 API 写入成功」的证据。真实 HTTP 写入证据来自 `FinanceApplyCanonicalEntrySqlServerTests`
  （真实 Kestrel + 真实隔离库 + 真实 JWT 认证）。
- 未连接生产数据库、未使用生产凭据、未读取 `appsettings*.json` / `.env` / 生产连接串；真实 SQL 只在
  `(localdb)\NEWERP_AutoAcceptance` + `Integrated Security=true` + 全新 GUID `NEWERP_AUTOTEST` 库上执行，
  发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
- 未执行迁移 / 部署 / 任意 SQL；未新增权限、表 / 列 / 索引 / 菜单 / 角色 / 用户授权（仅复用既有菜单与既有接口），
  未做任何台账回填或自动财务指派。

## 5. 相关实现

- `src/ERP.Domain/Entities/Finance1.cs`：`FinanceDepositApply.ApplyNo` / `FinancePaymentApply.ApplyNo` 去掉显式 `[Required]`。
- `src/ERP.Api/Controllers/FinanceApplyControllers.cs`：两个 `Create` 的服务端权威身份覆盖（含删除态 / 时间戳）与说明。
- `src/ERP.Api/wwwroot/js/bill-v2.js`：停用 `deposit-apply` / `payment-apply` → 旧存储过程族映射（旧族读侧保留只读）。
- `src/ERP.Api/wwwroot/js/modules-finance.js`：来源销售订单字段改声明式可空引用（`nullable: true`）并补充口径注释。
- `src/ERP.Api/wwwroot/js/crud.js`：声明式可空引用（留空提交 `null`）的口径说明（渲染 / 提交行为不变）。
- `src/ERP.Api/wwwroot/js/finance-apply-sales-order-source.js`：复用既有 ERP-390 / ERP-391 共享选择器（本任务未改动）。
- 复用既有：`FinanceDepositApplyLifecycleRules`（ERP-381）、`FinancePaymentApplyLifecycleRules`（ERP-380）、
  `FinanceApplySalesOrderSourceService`（ERP-390 / ERP-391）、`SalespersonDataScopeService`（ERP-097）。
