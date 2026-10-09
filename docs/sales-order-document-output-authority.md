# 规范销售订单（`api/sales-orders`）打印 / 单据导出实时授权与客户范围

> 任务：`ERP-424`「Protect canonical sales order print and document export with live customer scope」（阶段 3 核心业务流程完整性）。
> 关联：`ERP-413`（执行证据入口实时授权，`docs/sales-order-execution-authority.md`）、
> `ERP-420`（写入入口实时授权，`docs/sales-order-mutation-authority.md`）、
> `ERP-405`（旧单据读侧门禁，「普通读取不要求导出菜单」）、`ERP-308` / `ERP-323`（既有受控导出族目录与多菜单口径）、
> `ERP-097`（业务员客户数据范围，`docs/业务员数据范围说明.md`）。

## 1. 背景与问题证据（修复前）

规范销售订单控制器 `SalesOrderController`（路由 `api/sales-orders`）的三个**文档输出**路由此前都未真正收敛授权：

| 路由 | 修复前行为 |
| --- | --- |
| `GET {id}/print` | 只按 `Id` 取一张未删除订单 + 明细，**无任何授权 / 无客户范围**：任何已登录账号猜到 Id 即可读取范围外订单的合同 / 收货人 / 明细 / 金额 / 币种 |
| `GET export`（JSON 单据导出） | 返回**全部**匹配订单（含明细），**无客户范围**：范围外订单主表 + 明细直接导出 |
| `GET export-excel` | 只用 `SalespersonDataScopeService` 解析范围，**不校验账号是否存在 / 已删除 / 已禁用**，也不施加任何既有菜单授权：已删除账号（解析为空范围）返回空表而非 fail closed，且缺失功能 / 导出菜单的账号同样可导出 |

`ERP-413` 的执行证据护栏与 `ERP-420` 的写入门禁都**不覆盖**这三个独立运营输出；旧 `ERP-410` / `ERP-411` 护栏作用于
**另一个**旧 v2 控制器（`api/v2/sales-orders`）。这是运营记录出口的真实授权缺口。

**这不是「再发布一份报表」**：本任务只收敛既有出口授权，不新增报表、不做报表迁移 / 入口切换、不改写任何商业口径。

## 2. 集中化的文档输出护栏（`SalesOrderDocumentOutputAuthorizationRules`）

新增 `src/ERP.Application/Services/SalesOrderDocumentOutputAuthorizationRules.cs`（唯一权威口径），调用方（控制器）在任何读取与物化之前调用：

| 方法 | 作用 |
| --- | --- |
| `EnsureDocumentOutputAuthorizedAsync(db, userId)` | 输出入口完整授权：实时身份 + 既有「销售订单」功能菜单 + 既有「销售订单导出」导出菜单 + ERP-097 权威客户范围；特权账号豁免菜单但仍须通过实时身份 |
| `ApplyCustomerScope(source, scope)` | 把权威客户范围**下推到数据库**（复用 `SalespersonDataScopeService.FilterByCustomer`），先于关键字 / 状态 / 日期过滤与任何物化 |
| `EnsureOrderAllowedAsync(db, scope, orderId)` | 单张订单按**持久化 `SalesOrder.CustomerId`** 的权威归属复核（最小投影、有界只读，绝不装载明细），返回范围内客户 Id |

口径（fail closed，绝不缓存，每次请求重新解析）：

1. **实时身份**：`userId` 缺失 / 非法（非正整数）按未认证拒绝（`Err:Unauthorized`）；账号不存在 / 已删除按未认证拒绝；账号已禁用按权限不足拒绝（`Err:Forbidden`）。
   绝不把空身份当作匿名或管理员。
2. **既有菜单授权**：普通账号必须**同时**具备既有「销售订单」（`sales-order`）功能菜单与既有「销售订单导出」
   （`sales-order-export`）导出菜单 —— 与既有受控导出族目录 `LegacyBillExportCatalog` 的 `sales-order` 族
   （`RequiredMenuCodes = [sales-order, sales-order-export]`）以及既有销售单据打印模板族**逐字一致**；
   **仅持有导出菜单绝不构成模块权限（export-only menu never grants base order access）**，反之亦然。
   特权账号（超级管理员 / 系统内置角色 / 显式特权角色）沿用既有全部访问口径。**不新增任何菜单 / 角色 / 用户授权**。
3. **权威客户范围**：只复用唯一权威口径 `SalespersonDataScopeService`（ERP-097）；归属严格按**持久化 `SalesOrder.CustomerId`**
   判定，**绝不**按单号 / 客户名等自由文本、金额或相似度推断，也**绝不**把旧库 `Oid` 推断为规范 `Id`。
4. **非披露错误**：范围外订单、已删除订单与不存在订单返回**同一**受控错误「销售订单不存在」（`Err:NotFound`）——
   授权先于任何明细 / 金额读取，错误差异 / 计数 / 部分行 / 导出字节都不透露不可访问订单的存在性。
5. **无豁免**：输出入口**不做**「无请求路径 / 匿名进程内调用」豁免（与只读执行证据 `RequiresLiveAuthorization()` 口径明确区分）——
   直接调用控制器同样 fail closed。

## 3. 逐入口接线（先授权、后读取）

- **打印**（`GetPrint`）：`EnsureDocumentOutputAuthorizedAsync()` → `EnsureOrderAllowedAsync(id)`（**先取持久化 CustomerId**）
  → 才装载主表 + 明细，且明细只保留**未删除**行（与既有打印契约一致）。
- **JSON 单据导出**（`Export`）：先授权 → `ApplyCustomerScope` 下推范围 → 日期过滤 → 物化；明细只保留**未删除**行。
- **Excel 导出**（`ExportExcel`）：先授权 → `ApplyCustomerScope` 下推范围 → 关键字 / 状态 / 日期过滤 → 物化；
  列定义与文件名**保持不变**。

## 4. 保留既有输出契约（不改写、不新增）

- 授权只发生在读取 / 物化之前，**不改写**任何列与字段契约：JSON / Excel 列、打印字段、原始金额 / 币种 / 单位、
  `null`-as-unknown 与响应 DTO 全部保持不变；不新增合计、不做汇率换算、不执行任意 SQL、不新增报表、不迁移旧路由。
- 被拒绝的请求**零写入**：不改写销售订单 / 明细 / 出库 / 库存 / 收款 / 财务记录，不产生操作日志，不删除历史证据与审计留痕；
  受控信封只含 `{ code, message, data: null }`，不含任何订单号 / 计数 / 明细 / 导出字节。
- **不新增**表 / 列 / 索引 / 菜单 / 权限模型，**不新增任何用户授权**，不做报表迁移 / 入口切换，
  也不触碰库存来源单据审计与原始失败日志。

## 5. 验证证据

### 5.1 单元测试（`src/ERP.UnitTests/SalesOrderDocumentOutputAuthorizationTests.cs`，内存库 + 真实既有身份 / 菜单 / 范围）

- **菜单口径同源**：断言护栏菜单编码 / 文案与 `LegacyBillExportCatalog.Resolve("sales-order").RequiredMenuCodes` 完全一致；
- **本人订单可读**：受限业务员在打印 / JSON 导出 / Excel 导出全部可读；打印保留**未删除**明细（已删除明细行被剔除），
  金额 / 币种 / 单位原值保留；
- **统一非披露错误**：他人 / 已删除 / 不存在 / 非正 Id 订单打印返回同一 `Err:NotFound` + 同一文案；
- **JSON / Excel 范围下推**：仅含范围内父订单；**解码真实 xlsx 工作簿**断言越界订单号与金额绝不出现；
- **无主订单**：`CustomerId` 缺省订单对受限账号 fail closed、对特权账号保留既有全量口径；
- **身份 / 菜单拒绝矩阵**：无身份 / 畸形 / 非正身份 / 已删除账号 → `Err:Unauthorized`；已禁用 / 无菜单 / 仅导出菜单 /
  仅功能菜单 / 已撤销菜单 → `Err:Forbidden`（逐入口断言）；撤销后下一次请求立即收敛；
- **无请求路径豁免**：无请求路径 + 无身份 / 缺菜单的直接控制器调用同样 fail closed；
- **零写入**：拒绝与允许前后 `SalesOrders` / `SalesOrderDetails` / 客户 / 菜单 / 角色菜单行数不变。

### 5.2 真实隔离 SQL 测试（`src/ERP.IntegrationTests/SalesOrderDocumentOutputAuthorizationSqlServerTests.cs`）

- **专用目标护栏**：必须在任何数据库访问之前精确命中 `(localdb)\NEWERP_AutoAcceptance` + 库名前缀 `NEWERP_AUTOTEST`
  + `Integrated Security=true`；错误实例 / 错误库名 / 非集成安全的连接串一律在 `AssertDedicatedTarget` 阶段被拒绝
  （`SalesOrderDocumentOutputAuthorizationTargetGuardTests`）。每次运行只创建一个**全新 GUID 后缀库**，发现同名库已存在立即拒绝，
  **绝不 drop / reset / 复用**任何数据库，也绝不读取 `appsettings*.json` / `.env` / 生产凭据。
- **真实驱动 + 既有授权**：只在本隔离 GUID 库内播种既有「销售订单」+「销售订单导出」菜单授权 + 业务员客户数据范围 + 本人 /
  他人 / 已删除 / 无主订单；以真实 `SalesOrderController` 驱动，不复制测试专用实现，不新增 / 不修改任何既有菜单 / 角色 / 用户授权。
- **逐入口证明**：受限业务员下本人订单打印保留未删除明细；JSON 导出只返回范围内父订单；Excel 导出**解码真实工作簿**核对越界订单号 / 金额不出现。
- **身份 / 菜单拒绝矩阵**：无身份 / 已删除账号 → 未认证；已禁用 / 无菜单 / 仅导出菜单 / 仅功能菜单 / 已撤销菜单 → 权限不足（逐入口断言）。
- **特权账号**：保留既有全量口径（可读他人订单并导出）。
- **零写入证据**：每次拒绝 / 读取前后对 `SalesOrders` / `SalesOrderDetails` / `StockOuts` / `StockMovements`
  （**保留库存来源单据审计**） / `FinanceReceipts` / `SysOperationLogs` 做只读快照，全部不变。
- **两个独立连接竞态**：① 两条独立连接并发打印同一本人订单 → 结果一致；② 一条合法打印与一条他人越权并发 →
  合法打印成功、越权 fail closed，收尾快照证明零写入。

### 5.3 构建与回归

- `.NET 8` Release 构建（`NEWERP.sln`）通过，0 警告 / 0 错误（`ERP.IntegrationTests` 启用了 `TreatWarningsAsErrors`）。
- `ERP.UnitTests` 全量套件通过；既有打印 / 导出调用方回归（`OrderTraceabilityTests`、`SalesOrderMasterReferenceTests`、
  `SalesOrderWriteValidationTests`、`SalesOrderControllerTests`、`SalespersonDataScopeTests`）保持不变。
- **构建完成不等于阶段验收**：真实 SQL 用例只有在受控 localdb 上真实执行通过才构成验收证据；
  浏览器验收按任务配置为 `browser_acceptance.required = false`：不运行真实 Edge / UI / 截图验收。

## 6. 明确边界（非目标）

- 不修改任何既有存储过程 / 数据库结构 / 迁移脚本 / 种子数据；不新增表 / 列 / 索引。
- 不新增权限模型（菜单 / 角色 / 用户授权），不提供匿名或管理员降级，不把导出菜单当模块权限，不做旧库 `Oid` → 规范 `Id` 推断。
- 不改写列表 / 详情（`GetPaged` / `GetById`）的既有 ERP-097 范围口径与实时身份口径，不改写执行证据（`ERP-413`）与写入（`ERP-420`）护栏，
  也不改变旧 v2 专用路由（`ERP-410` / `ERP-411`）的既有门禁与读侧范围。
- 不清理 / 不删除任何既有业务行、库存来源审计或既有失败日志；不执行生产凭据或生产数据操作。
- 浏览器验收按任务配置为 `browser_acceptance.required = false`：不运行真实 Edge / UI / 截图验收。

