# 旧单据（`api/v2/bills`）写入门禁与规范工作流边界

> 任务：`ERP-404`「Prevent legacy stored-procedure document writes bypassing canonical lifecycle guards」（阶段 3 核心业务流程完整性）。
> 关联：`ERP-308`（旧单据导出族有限目录，Stage 2）、`ERP-403`（阶段 3 授权边界规划）、`ERP-405` / `ERP-406`（旧单据读侧与操作历史）。

## 1. 背景与问题证据

通用单据控制器 `BillProcController`（路由 `api/v2/bills`）以 **旧库存储过程** 驱动 16 个单据族：

- `POST api/v2/bills/{billType}/save` 直接把调用方 `Fields` 透传为 `sp_Biz_*` 参数（含 `@Action` / `@Oid`），
  不校验实时身份 / 既有菜单 / 客户范围，也不做规范业务侧的数量、金额、来源链路、下游依赖与生命周期校验；
- `POST api/v2/bills/{billType}/{oid}/{op}`（delete / audit / unaudit / void / restore）先调用存储过程完成状态流转，
  之后才做结果校验；且状态校验抛异常时旧实现返回 `true`（把「无法确认」当作成功）；
- `POST api/v2/bills/{billType}/import`（Excel 导入）在控制器内逐行重复调用 Save，同样绕过全部规范守卫；
- 旧库单体表以 **`Oid`** 为主键（`db_owner.SalesOrder` / `db_owner.StockIn` / …），而规范 EF 实体以 **`Id`** 为主键
  （`db_owner.SalesOrders` / `db_owner.StockIns` / …，复数表）。**二者没有权威身份映射**，旧的 `Oid` 绝不等于规范 `Id`。

结论：旧写路径可以在规范工作流（ERP-350 ~ ERP-403 已加装的来源链路、数量金额、下游依赖、行锁与生命周期守卫）之外
直接改写业务单据 / 库存 / 财务数据。`ERP-404` 把这些写入统一收敛到**服务端有限的变更策略**之下。

## 2. 有限服务端变更策略（全部 16 族）

新增 `src/ERP.Application/Services/LegacyBillMutationRules.cs`：显式登记全部 16 个旧单据族的
**表名 / 存储过程 / 既有功能菜单 / 有限规范业务路由**（编译期常量，绝不来自客户端，也不做数据库元数据发现）。

| 族键 (`billType`) | 旧库单体表 | 旧存储过程 | 既有功能菜单（模块权限） | 有限规范业务路由 |
| --- | --- | --- | --- | --- |
| `sales-order` | `SalesOrder` | `db_owner.sp_Biz_SalesOrder` | `sales-order`（销售订单） | `/api/sales-orders` |
| `purchase-order` | `PurchaseOrder` | `db_owner.sp_Biz_PurchaseOrder` | `purchase-order`（采购订单） | `/api/purchase-orders` |
| `inquiry` | `Inquiry` | `db_owner.sp_Biz_Inquiry` | `inquiry`（询价单） | `/api/inquiries` |
| `stock-in` | `StockIn` | `db_owner.sp_Biz_StockIn` | `stock-in`（采购入库） | `/api/stock-ins` |
| `stock-out` | `StockOut` | `db_owner.sp_Biz_StockOut` | `stock-out`（销售出库） | `/api/stock-outs` |
| `receipt` | `FinanceReceipt` | `db_owner.sp_Biz_FinanceReceipt` | `receipt`（收款单） | `/api/finance/receipts` |
| `payment` | `FinancePayment` | `db_owner.sp_Biz_FinancePayment` | `payment`（付款单） | `/api/finance/payments` |
| `deposit-apply` | `FinanceDepositApply` | `db_owner.sp_Biz_FinanceDepositApply` | `deposit-apply`（定金申请单） | `/api/finance/deposit-applies` |
| `payment-apply` | `FinancePaymentApply` | `db_owner.sp_Biz_FinancePaymentApply` | `payment-apply`（货款申请单） | `/api/finance/payment-applies` |
| `container-settlement` | `FinanceContainerSettlement` | `db_owner.sp_Biz_FinanceContainerSettlement` | `container-settlement`（装柜结算单） | `/api/finance/container-settlements` |
| `bulk-settlement` | `FinanceBulkSettlement` | `db_owner.sp_Biz_FinanceBulkSettlement` | `bulk-settlement`（散货结算单） | `/api/finance/bulk-settlements` |
| `complaint` | `FinanceComplaint` | `db_owner.sp_Biz_FinanceComplaint` | `complaint`（客诉单） | `/api/finance/complaints` |
| `receiving-plan` | `ContainerReceivingPlan` | `db_owner.sp_Biz_ContainerReceivingPlan` | `receiving-plan`（收货计划） | `/api/container/receiving-plans` |
| `booking` | `ContainerBooking` | `db_owner.sp_Biz_ContainerBooking` | `booking`（订柜信息） | `/api/container/bookings` |
| `pre-loading` | `ContainerPreLoading` | `db_owner.sp_Biz_ContainerPreLoading` | `pre-loading`（预装柜单） | `/api/container/pre-loadings` |
| `loading-list` | `ContainerLoadingList` | `db_owner.sp_Biz_ContainerLoadingList` | `loading-list`（装柜清单） | `/api/container/loading-lists` |

**菜单口径**：只用 `SeedData.Menus` 里既有的**功能菜单**（与 ERP-308 导出口径一致）。销售订单 / 采购订单 / 询价单族
另有独立的 `sales-order-export` / `purchase-order-export` / `inquiry-export` 导出菜单 —— **导出菜单绝不当作模块权限**，
只持有导出菜单的账号在写路径上仍被拒绝（fail closed）。不新增任何菜单 / 角色 / 用户授权。

## 3. 写入门禁顺序（Save / 每个状态流转 / Excel 导入）

`LegacyBillMutationRules.AuthorizeAsync` 由控制器在**任何副作用之前**调用，顺序固定：

1. **有限策略解析**：未知 / 空白 / 畸形族标识在访问任何数据之前以 `1001 InvalidParameter` 拒绝；
2. **实时身份**（`LegacyBillAuthorizationRules.EnsureLiveIdentityAsync`）：缺失 / 非法身份 → `2000 Unauthorized`；
   账号不存在 / 已删除 → `2000`；账号已禁用 → `2002 Forbidden`；
3. **既有模块授权**（`EnsureModuleAuthorizedAsync`）：普通账号必须实时具备该族既有功能菜单（导出菜单不顶替）→ `2002`；
   特权账号（超级管理员 / 系统内置角色）沿用既有全部访问口径；
4. **保留命令字段**：调用方 `Fields` 中出现 `Action` / `Oid` / `Result` / `Msg` / `BillNo` / `DetailsJson`
   （大小写与首尾空白不敏感）→ `1001` 拒绝，命令身份与动作只能由服务端根据路由确定；
5. **有限策略裁决**：`HasValidatedAdapter` 今天**恒为 `false`** → `1004 RuleConflict` + 稳定、可操作的业务文案
   （明确「未执行任何 sp_Biz_*、未占用单号、未写操作日志、未发送通知」，并给出该族有限、既有的规范业务路由）。

任一步拒绝都发生在：默认值兜底（币种 / 汇率）、单号预约、任何 `sp_Biz_*` 调用、单据操作日志（`SysOperationLogs`）
与钉钉通知（`SysDingTalkLogs`）**之前**。控制器返回既有「HTTP 200 + 业务错误码」信封，保持前端兼容。

## 4. 为什么是 fail closed（而不是静默重映射）

- **没有权威身份映射**：旧 `Oid` ↔ 规范 `Id` 之间没有可验证的对应关系；
  `LegacyBillExportReadService`（ERP-308）同样把旧表 `Oid`-vs-`Id` / 缺表缺列显式标为 environment-blocked，
  绝不猜测或降级。写路径沿用同一判据。
- **没有已验证业务适配器**：任何「旧请求 → 规范业务服务」的适配器都必须同时证明来源链路、数量、金额、
  下游依赖与生命周期语义与规范服务**逐项一致**，并通过真实 SQL 的规范-vs-旧双连接竞态验收。
  在这样一份证据落地之前，适配器一律不予登记（`HasValidatedAdapter = false`）。
- **绝不执行 `sp_Biz_*`、绝不静默重映射 ID、绝不复制一套独立 ERP 实现、绝不编辑存储过程 / 数据库结构。**

因此今天的旧写路径整体**停用**（fail closed），而不是「半吊子放行」。

## 5. 实际旧页面调用方与兼容性影响

控制器路由（`src/ERP.Api/Controllers/BillProcController*.cs`）逐条处置：

| 路由 | 处置 | 说明 |
| --- | --- | --- |
| `POST {billType}/save` | **停用（fail closed）** | 写入门禁在任何副作用之前拒绝；不再调用 `sp_Biz_*` |
| `POST {billType}/{oid}/{op}`（delete / audit / unaudit / void / restore） | **停用（fail closed）** | 五种状态流转同源门禁；不再先流转后校验 |
| `POST {billType}/import` | **停用（fail closed）** | 与 Save 同源；不再解析 / 逐行写入 |
| `GET {billType}` / `GET {billType}/navigate` / `GET {billType}/{oid}` | **保留** | 旧单据查询 / 翻页 / 详情（读侧授权与范围收敛属 ERP-405） |
| `GET {billType}/defaults` | **保留** | 默认币种 / 汇率 |
| `GET {billType}/import-template` | **保留** | 导入模板下载（只读，不写库） |
| `GET {billType}/export` | **保留** | 旧报表 / 导出路由与 ERP-308 迁移退役门槛完全不变（`STAGE3_PRIORITY_AUTHORIZED.preserve_legacy_entry_gate`） |
| `GET {billType}/{oid}/logs` | **保留** | 单据操作历史（数据授权修复属 ERP-406） |

- **核心规范工作流完全可用且不受影响**：`api/sales-orders` / `api/purchase-orders` / `api/inquiries` /
  `api/stock-ins` / `api/stock-outs` / `api/finance/*` / `api/container/*` 等规范路由不经过本门禁。
- **拒绝是稳定、可操作的业务错误**：调用方收到既有信封 + 明确文案（含该族规范路由），前端可直接提示并提供跳转，
  而不是 500 或静默丢失数据。
- **精确阻断项（本任务范围之外，需后续任务处理）**：`SalesOrderProcController`（路由 `api/v2/sales-orders`，
  保存 / 审核同样直调 `db_owner.sp_Biz_SalesOrder`）不在本任务 `allowed_paths` 内，
  因此本任务**未修改**该路由；它仍是一条会绕过规范守卫的旧写路径。修复它需要在后续任务中显式纳入范围，
  并复用同一 `LegacyBillMutationRules` 门禁。

## 6. 验证证据

### 6.1 单元测试（`src/ERP.UnitTests/LegacyBillMutationTests.cs`，内存库 + 真实既有身份）

- **有限目录**：恰好覆盖 `BillProcController.Bills` 全部 16 族（表名 / 存储过程逐项一致）；导出菜单不被当作模块权限；
  今日无任何已验证适配器；未知 / 畸形族标识在访问数据之前拒绝；操作标识解析（大小写不敏感）。
- **保留命令字段**：`Action` / `Oid` 等（含大小写 / 空白变体）识别与拒绝；剥离后仅保留业务字段。
- **写前门禁**：缺失 / 已删除身份 → `2000`；已禁用 → `2002`；无功能菜单 → `2002`；仅导出菜单 → `2002`；
  具备功能菜单 / 特权账号仍 fail closed（`1004` + 规范路由 + `sp_Biz_` 文案）；保留字段注入 → `1001`。
- **控制器零副作用**：Save / 五种状态流转 / Excel 导入被拒后，业务表、明细、单号规则、`SysOperationLogs`、
  `SysDingTalkLogs` 全部零变更；未知单据类型 / 非法操作仍返回既有错误；`VerifyActionAsync` 在状态读取异常时
  fail closed（返回 `false`，修复旧实现的「异常即成功」口径）。
- **逐族覆盖（全部 16 族）**：对每一族分别以该族既有功能菜单授权，验证 Save 与全部六种操作
  （delete / audit / unaudit / void / restore / import）均 fail closed 且带该族规范路由文案；
  另以真实控制器逐族验证 Save 路由与 Excel 导入路由均被拒绝且零写入（`SysOperationLogs` / `SysDingTalkLogs` 为空）。

### 6.2 真实隔离 SQL 测试（`src/ERP.IntegrationTests/LegacyBillMutationSqlServerTests.cs`）

- **专用目标护栏**：必须在任何数据库访问之前精确命中 `(localdb)\NEWERP_AutoAcceptance` + 库名前缀 `NEWERP_AUTOTEST`
  + `Integrated Security=true`；错误实例 / 错误库名 / 非集成安全的连接串一律在 `AssertDedicatedTarget` 阶段被拒绝
  （`LegacyBillMutationTargetGuardTests`）。每次运行只创建一个**全新 GUID 后缀库**，发现同名库已存在立即拒绝，
  **绝不 drop / reset / 复用**任何数据库，也绝不读取 `appsettings*.json` / `.env` / 生产凭据。
- **真实驱动**：以新播种的既有功能菜单授权驱动真实 `BillProcController`；不新增 / 不修改任何既有菜单 / 角色 / 用户授权，
  无匿名 / 管理员降级。
- **零变更证据**：Save / 五种状态流转 / Excel 导入被拒后，`SalesOrders` / `SalesOrderDetails` / `StockIns` /
  `StockInDetails` / `StockOuts` / `Stocks` / `StockMovements`（库存来源单据审计）/ `SysDocumentNumberRules.CurrentSequence`
  （单号流水）/ `SysOperationLogs` / `SysDingTalkLogs` 的只读快照完全不变。
- **两个独立连接竞态**：每条竞态用例新建两个独立 DbContext / 连接 / 控制器并门闩对齐并发；
  两条连接都必须 fail closed（无赢家、零写入）。**「规范 vs 旧」适配器竞态不适用**：今天不存在任何已验证适配器，
  旧写路径整体 fail closed，因而不存在规范与旧的双写竞争；若将来登记适配器，必须补齐真实的
  数量 / 金额 / 回滚与规范-vs-旧双连接竞态验收。
- **逐族真实 SQL 覆盖**：逐族以该族既有功能菜单授权在真实库上尝试 Save，全部 `1004` 且带规范路由，
  收尾快照证明零变更。
- 保留既有库存来源单据审计与原始失败日志：测试只读取计数 / 快照，不删除任何既有行。

### 6.3 构建与回归

- `.NET 8` Release 构建（`NEWERP.sln`）通过，0 警告 / 0 错误（`ERP.IntegrationTests` 启用了 `TreatWarningsAsErrors`）。
- 既有 `ERP.UnitTests` 全套回归与既有旧单据导出 / 报表迁移回归保持不变（本任务只新增文件 + 在旧写路径前加门禁）。
- **构建完成不等于阶段验收**：真实 SQL 用例只有在受控 localdb 上真实执行通过才构成验收证据。

## 7. 明确边界（非目标）

- 不编辑任何存储过程 / 数据库结构 / 迁移脚本 / 种子数据；不新增表 / 列 / 索引。
- 不新增权限模型（菜单 / 角色 / 用户授权），不提供匿名或管理员降级，不把导出菜单当模块权限。
- 不复制一套独立的 ERP 实现；规范业务服务（数量、金额、库存成本、来源链路、下游依赖、生命周期）保持唯一权威。
- 不改变旧报表 / 导出路由与 ERP-308 迁移退役门槛；不改变旧单据查询 / 翻页 / 详情 / 默认值 / 模板 / 历史路由。
- 不清理 / 不删除任何既有业务行、库存来源审计或既有失败日志；不执行生产凭据或生产数据操作。

