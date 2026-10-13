# 收款单 / 付款单规范入口与「省略单据号」手工录入（ERP-479）

> 任务：`ERP-479`「Restore canonical customer receipt and supplier payment form entry」（阶段 3 核心业务流程闭环）。
> 关联：`ERP-404`（旧单据写入门禁，旧族写路径 fail closed）、`ERP-478` / `ERP-477`（出库 / 入库规范入口同源口径）、
> `ERP-349`（收款单生命周期护栏）、`ERP-351` / `ERP-379` / `ERP-380`（付款单生命周期与锁序）、
> `ERP-378`（收款单同单串行化）、`ERP-350`（收款单唯一同币种分摊额度）、`ERP-049` / `ERP-053` / `ERP-066` / `ERP-071`（引用证据）、
> `ERP-468`（引用证据实时授权与请求形状无关）、`ERP-370`（实时授权与客户数据范围）。

## 1. 背景与问题证据

`modules-finance.js` 早已注册规范 EF 模块 `receipt`（`api: '/api/finance/receipts'`）与 `payment`
（`api: '/api/finance/payments'`），但 `bill-v2.js` 的 `BILL_CODE_MAP` 仍把菜单编码 `receipt` / `payment`
映射到旧存储过程族；`app.js` 的 `renderPage` 先判 `BILL_CODE_MAP` 再判 `MODULES`，因此「收款单」「付款单」菜单
实际打开的是旧库单体表（`Oid` 口径）单据页，而该族写路径已被 `ERP-404`（`LegacyBillMutationRules`）**整体停用
（fail closed）** —— 页面能开、保存永远被拒，规范表单与既有引用登记工作台无法从菜单进入。

同时规范入口自身也有缺陷：

- `FinanceReceipt.ReceiptNo` 与 `FinancePayment.PaymentNo` 带显式 `[Required]`（`AllowEmptyStrings = false`），
  手工表单不提交单据号时在**模型绑定 / 模型校验阶段**就返回 HTTP 400
  `{"errors":{"ReceiptNo":["The ReceiptNo field is required."]}}`，服务端权威单号根本没有机会生成；
- 新增入口未把删除态 / 时间戳统一收归服务端（客户端可注入 `IsDeleted` / `CreatedAt` / `UpdatedAt`）；
- 付款单表单未声明来源货款申请单（`long?` 可空引用），无法显式关联 / 断开来源。

## 2. 口径与实现

### 2.1 菜单路由（只改路由，不改旧族读侧）

| 项 | 口径 |
|---|---|
| `bill-v2.js` `BILL_CODE_MAP` | **移除** `receipt: 'receipt'` 与 `payment: 'payment'` 两条映射（保留 ERP-479 注释说明原因）。旧族自身（`BILL_CONFIG['receipt']` / `BILL_CONFIG['payment']` 的旧库单体表 `Oid` 读侧、旧导出、既有默认值端点）**保持只读可用**。 |
| `modules-finance.js` | `MODULES['receipt']`：`api: '/api/finance/receipts'`、`canSubmit: true`；`MODULES['payment']`：`api: '/api/finance/payments'`、`canSubmit: true`，并新增来源字段 `paymentApplyId`（`type: 'number'` + `nullable: true`）。 |
| 规范 Id 口径 | 列表 / 详情 / 保存 / 提交 / 审核 / 取消 / 删除全走规范 `Id`（`crud.js` 的 `mod.api` 与 `mod.api/{id}`）。**绝不**把旧 `Oid` 映射成规范 `Id`，也**绝不**启用任何旧写适配器。 |
| 引用工作台 | 规范收款单行操作按规范 `Id` 打开既有收款引用工作台（`openCustomerReceiptAllocationRegisterForReceipt` → 既有 `craSelectReceipt`）；规范付款单行操作打开既有付款引用 / 付款发票引用工作台。**只复用既有工作台入口与其既有服务路径**，不新增接口、不新增取数通道，也绝不把付款单 Id 当作采购订单 Id 传递。 |

### 2.2 允许省略收款单号 / 付款单号（`Finance2.cs` / `Finance1.cs`）

`FinanceReceipt.ReceiptNo`（`Finance2.cs`）与 `FinancePayment.PaymentNo`（`Finance1.cs`）的显式 `[Required]`
被移除（与 `ERP-477` 入库单号 / `ERP-478` 出库单号 / `ERP-428` 订单号同源口径）：省略 / 空串照常绑定并进入已授权的
`FinanceReceiptController.Create` / `FinancePaymentController.Create`；数据库列仍为 NOT NULL（非空 CLR 引用类型），
长度仍受 `[MaxLength(50)]` 约束。单号 / 主键 / 状态 / 删除态 / 时间戳一律由服务端权威覆盖（客户端伪造值不参与持久化）。

### 2.3 拒绝先于编号与持久化（控制器）

| 顺序 | 收款单 `Create` | 付款单 `Create` |
|---|---|---|
| 1 | 币种规范化 + 支持范围校验、金额按币种精度取整后 > 0 | 币种规范化 + 支持范围校验、金额按币种精度取整后 > 0 |
| 2 | 客户存在 / 未删除 / 启用（不存在按不存在拒绝、停用按规则冲突拒绝） | 供应商存在 / 未删除 / 启用 |
| 3 | 实时身份 + 既有「收款单」（`receipt`）菜单 + ERP-097 客户数据范围 | 来源货款申请单按 Id 解析（未删除、未取消、币种一致、付款金额 ≤ 申请金额，绝不按单号文本或金额猜测来源）→ 实时身份 + 既有「付款单」（`payment`）菜单 + 权威来源客户数据范围 |
| 4 | **只有全部校验通过之后**才：`Id = 0`、`GenerateAsync(DocumentType.Receipt)`、`Status = Pending`、`IsDeleted = false`、`CreatedAt = now`、`UpdatedAt = null`、落库 | 同左（`GenerateAsync(DocumentType.Payment)`），来源申请单行锁与付款单行锁按 `ERP-380` 锁序先来源申请单、后付款单 |

被拒绝的请求：**零表头变化、零来源改写、零状态变化、零占号**（单号预约永不发生）。

### 2.4 可空来源引用（付款单）

| 项 | 口径 |
|---|---|
| 实体 | `FinancePayment.PaymentApplyId` 为 `long?`（既有定义，未改动 schema）。 |
| 表单 | `paymentApplyId` 声明 `nullable: true`：留空提交 `null`（声明式可空引用，绝不回落为哨兵值 `0`）。 |
| 服务端 | `SupplierPaymentLifecycleRules.ResolvePaymentApplyAsync` 把 `null` / `0` 都归一为「未关联来源」（历史未链接语义），显式 Id 必须解析到既有、未删除、未取消且币种一致、金额不超申请金额的申请单。 |
| 修改 | 修改可显式断开来源（提交 `null`）；单据号 / 状态 / 删除态保持服务端值。 |

### 2.5 保留的既有守卫（本任务不改口径）

| 守卫 | 口径 |
|---|---|
| 收款单生命周期 | `ERP-349` / `ERP-378`：修改 / 提交 / 审核 / 取消 / 删除同一事务内先取收款单行锁，锁内复核实时身份 / 菜单 / 客户范围 |
| 付款单生命周期与锁序 | `ERP-351` / `ERP-379` / `ERP-380`：先来源货款申请单行、后付款单行；锁内复核来源链接未被并发改写 |
| 共享分摊预算 | `ERP-350`：收款单 / 付款单金额是同一张单据的**唯一、同币种分摊额度**，「订单引用」与「发票引用」两套有效证据共同占用；存在有效证据时拒绝取消 / 删除或改客户 / 供应商 / 币种 / 金额 |
| 引用证据授权 | `ERP-468`：所有引用证据入口（台账 / 候选 / 汇总 / 清单 / 登记 / 作废）只依据「控制器是否绑定到 HTTP 请求管线」实时授权，与请求路径 / 请求形状无关 |
| 审计与受控错误 | 失败整体回滚（关系型后端），状态、原始字段、删除标记与审计不变；错误仍为既有业务错误码 |

## 3. 验证

| 项 | 命令 | 结果 |
|---|---|---|
| Release 生成（分析器 + 警告即错误） | `dotnet build NEWERP.sln -c Release /p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true` | ✅ 0 警告 0 错误 |
| 全量单元测试 | `dotnet test src/ERP.UnitTests -c Release` | ✅ 7773 项通过（失败 0 / 跳过 0，25 s） |
| 新增单元测试 | `--filter "FullyQualifiedName~ReceiptPaymentCanonicalEntryTests"` | ✅ 26 项通过（失败 0） |
| 前端离线契约 | `node tests/receipt-payment-canonical-entry.browser.js` | ✅ 41 项离线断言通过 |
| **真实隔离 SQL / HTTP 验收** | `dotnet test src/ERP.IntegrationTests -c Release --filter "FullyQualifiedName~ReceiptPaymentCanonicalEntry"`（`ERP_AI_TEST_RUN=1` + `ASPNETCORE_ENVIRONMENT=Development`） | ✅ 19 项通过（15 项真实 HTTP / SQL 场景 + 4 项目标护栏 fail-closed；真实 Kestrel + 专用 localdb + 全新 GUID 库 `NEWERP_AUTOTEST_RECEIPTPAYMENTCANONICAL_<guid>`） |
| 验证产物落点 | `--results-directory %TEMP%/erp479-*` | ✅ 全部 `*.trx` / 日志写入进程临时目录，工作区未新增任何测试结果文件 |

新增测试：

- `src/ERP.UnitTests/ReceiptPaymentCanonicalEntryTests.cs`（新增）：实体无显式必填 + 长度约束、省略 / 空串 /
  伪造单据号与身份一律服务端覆盖、无身份 / 缺菜单 / 客户或供应商不可用 / 非法币种与金额 / 越权客户 /
  来源申请单悬空 · 已删除 · 已取消 · 币种不一致 · 超申请金额一律零写入零占号、可空来源引用显式关联与清空、
  规范 Id 打开既有收款 / 付款引用工作台汇总与候选、有效引用证据下取消被拒且零状态变化、菜单路由与控制器接线契约。
- `src/ERP.IntegrationTests/ReceiptPaymentCanonicalEntrySqlServerTests.cs`（新增）：真实 HTTP（真实 Kestrel +
  MVC 绑定层 + JWT 认证）证明「省略单据号」不再返回 HTTP 400、规范 Id 往返（新增 / 回读 / 修改 / 列表）、
  显式来源货款申请单、既有引用工作台汇总与候选按规范 Id 可达、越权 / 非法请求零写入零占号、旧族写路径 fail closed；
  夹具为被测 API 进程注入**进程内临时随机 JWT 签名密钥**（不继承仓库内空白 `Jwt:Key`、不落盘、不写日志）。
- `tests/receipt-payment-canonical-entry.browser.js`（新增）：真实浏览器端到端验收的**离线契约**部分
  （菜单路由 / 路由优先级 / 规范模块 / 可空来源引用 / 既有工作台接线 / 规范 Id 接线 / 旧族分离）。

## 4. 环境受限说明（不声称整阶段验收）

- 本任务 `validation_profile = safe`、`completion_mode = build`、`browser_acceptance.required = false`：
  投递门禁为 **Release 生成 + ERP.UnitTests**。
- **真实浏览器 UI 工作流未执行**（本环境无浏览器验收执行器，任务也未要求）：因此**不声称**浏览器端到端验收
  或整阶段验收通过；`tests/receipt-payment-canonical-entry.browser.js` 仅为离线契约验证，不构成
  「页面点击 → 真实 API 写入成功」的证据。真实 HTTP 写入证据来自 `ReceiptPaymentCanonicalEntrySqlServerTests`
  （真实 Kestrel + 真实隔离库 + 真实 JWT 认证）。
- 未连接生产数据库、未使用生产凭据、未读取 `appsettings*.json` / `.env` / 生产连接串；真实 SQL 只在
  `(localdb)\NEWERP_AutoAcceptance` + `Integrated Security=true` + 全新 GUID `NEWERP_AUTOTEST` 库上执行，
  发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
- 未执行迁移 / 部署 / 任意 SQL；未新增权限、表 / 列 / 索引 / 菜单 / 角色 / 用户授权（仅复用既有菜单与既有接口），
  未做任何台账回填或自动财务指派。
- **范围边界（如实记录）**：集成夹具为构造付款单的**显式来源**，经既有 `api/finance/payment-applies` 接口创建货款申请单时
  **显式携带 `applyNo`**（服务端仍按权威口径覆盖申请单号，测试只依赖其规范 `Id`）——「货款申请单省略申请单号」的绑定口径
  属该菜单自身任务，本任务不扩大范围改动 `FinancePaymentApply` 的实体契约；收款单 / 付款单的「省略单据号」绑定
  才是本任务修复并由真实 HTTP 绑定层证明的部分。

## 5. 相关实现

- `src/ERP.Domain/Entities/Finance2.cs`：`FinanceReceipt.ReceiptNo` 去掉显式 `[Required]`（服务端权威单号口径）。
- `src/ERP.Domain/Entities/Finance1.cs`：`FinancePayment.PaymentNo` 去掉显式 `[Required]`。
- `src/ERP.Api/Controllers/FinanceReceiptComplaintControllers.cs`：收款单新增的服务端权威身份覆盖（含删除态 / 时间戳）。
- `src/ERP.Api/Controllers/FinancePaymentController.cs`：付款单新增的服务端权威身份覆盖与来源货款申请单保留。
- `src/ERP.Api/wwwroot/js/bill-v2.js`：移除 `receipt` / `payment` → 旧存储过程族映射（旧族读侧保留只读）。
- `src/ERP.Api/wwwroot/js/modules-finance.js`：规范模块注释、付款单可空来源字段、既有引用工作台行操作与包装函数。
- 复用既有：`crud.js`（规范 Id 主子表单与声明式可空引用）、`CustomerReceiptLifecycleRules`（ERP-349 / ERP-378 / ERP-350）、
  `SupplierPaymentLifecycleRules`（ERP-351 / ERP-379 / ERP-380）、`CustomerReceiptAllocationService`（ERP-053）、
  `SupplierPaymentAllocationService`（ERP-049）、`CustomerReceiptAllocationController` / `SupplierPaymentAllocationController`（ERP-468）。

