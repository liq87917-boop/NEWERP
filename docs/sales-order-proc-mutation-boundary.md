# 旧销售订单专用路由（`api/v2/sales-orders`）写入门禁与规范工作流边界

> 任务：`ERP-410`「Close dedicated legacy sales order mutation bypass through existing business policy」（阶段 3 核心业务流程完整性）。
> 关联：`ERP-404`（通用旧单据写入门禁，`api/v2/bills`）、`ERP-405` / `ERP-406`（旧单据读侧 / 操作历史）、
> `ERP-409`（前置依赖）、规范销售订单工作流（`api/sales-orders`，ERP-008 起）。

## 1. 背景与问题证据

`SalesOrderProcController`（路由 `api/v2/sales-orders`）是**独立于** ERP-404 通用控制器 `BillProcController`
（`api/v2/bills`）的旧库存储过程写路径，因此**未被 ERP-404 的写门禁覆盖**：

- `POST api/v2/sales-orders/save` 直接把请求体字段（含 `Oid` 与请求头 `TotalAmount` / `DepositAmount` / `DepositRatio`）
  透传为 `db_owner.sp_Biz_SalesOrder` 参数；
- `POST api/v2/sales-orders/{oid}/delete|audit|void|restore` 五种状态流转同样直调 `db_owner.sp_Biz_SalesOrder`；
- 控制器只有 `[Authorize]`：不校验实时身份 / 既有「销售订单」功能菜单 / 客户数据范围，也不做规范业务侧的数量、金额、
  来源链路、下游依赖与生命周期校验。

旧库单体表 `db_owner.SalesOrder` 以 **`Oid`** 为主键，规范 EF 实体 `db_owner.SalesOrders` 以 **`Id`** 为主键，
**二者没有权威身份映射**（与 ERP-404 结论一致）。结论：`api/v2/sales-orders` 可以在规范工作流（来源链路 / 数量金额 /
履约 / 取消守卫）之外直接改写业务单据。`ERP-410` 把这条专用写路径收敛到**同一份 ERP-404 有限变更策略**之下。

## 2. 复用的有限服务端变更策略（不新增第二套）

本任务**不新增**第二套策略或业务实现，直接复用 `src/ERP.Application/Services/LegacyBillMutationRules.cs`
中既有的旧单据族 `sales-order`：

| 族键 | 旧库单体表 | 旧存储过程 | 既有功能菜单（模块权限） | 有限规范业务路由 |
| --- | --- | --- | --- | --- |
| `sales-order` | `SalesOrder` | `db_owner.sp_Biz_SalesOrder` | `sales-order`（销售订单） | `/api/sales-orders` |

`SalesOrderProcController.LegacyFamilyKey = "sales-order"` 与 `BillProcController.Bills["sales-order"]` **同源**：
同一族目录、同一存储过程、同一既有功能菜单、同一 fail-closed 裁决。导出菜单（`sales-order-export`）
**绝不**当作模块权限；不新增 / 不修改任何菜单 / 角色 / 用户授权。

`LegacyBillMutationRules.AuthorizeAsync` 的顺序固定（本控制器在任何副作用之前调用）：

1. **有限策略解析**：未知 / 空白 / 畸形族标识在访问任何数据之前拒绝；
2. **实时身份 + 既有功能模块授权**：缺失 / 非法 / 已删除按未认证拒绝（`2000`），已禁用按权限不足拒绝（`2002`），
   普通账号必须具备既有「销售订单」菜单（`sales-order`），特权账号沿用既有全部访问口径；
3. **保留命令字段拒绝**：调用方字段不得覆盖 `@Action` / `@Oid` 等命令身份；
4. **有限策略裁决**：今天不存在任何已验证业务适配器（`HasValidatedAdapter` 恒为 `false`）→ fail closed（`1004`），
   返回稳定、可操作的业务错误并给出既有规范业务路由 `/api/sales-orders`。

## 3. 门禁顺序（Save 与每个状态流转）

`SalesOrderProcController.GuardLegacyMutationAsync(LegacyBillOperation)` 在**任何** `sp_Biz_SalesOrder` 调用、
默认值 / 单号写入或成功响应之前裁决：

| 动作 | 调用点 | 门禁动作标识 | 结果（今天） |
| --- | --- | --- | --- |
| `POST save` | `Save` | `LegacyBillOperation.Save` | 停用（fail closed，不构造参数、不调用 SP） |
| `POST {oid}/delete` | `Delete` → `ExecuteAction` | `LegacyBillOperation.Delete` | 停用（fail closed） |
| `POST {oid}/audit` | `Audit` → `ExecuteAction` | `LegacyBillOperation.Audit` | 停用（fail closed） |
| `POST {oid}/void` | `Void` → `ExecuteAction` | `LegacyBillOperation.Void` | 停用（fail closed） |
| `POST {oid}/restore` | `Restore` → `ExecuteAction` | `LegacyBillOperation.Restore` | 停用（fail closed） |

- **服务端自有身份**：动作标识由路由 / 服务端确定，`Oid` 只作为**旧库标识**传入，绝不被推断为规范 `Id`；
  请求是强类型 DTO（无自由 `Fields`），因此不存在保留命令字段注入面，也绝不静默转换不支持的请求字段。
- **绝不信任请求头金额**：`TotalAmount` / `DepositAmount` / `DepositRatio` 等载荷在被拒时完全不参与任何数据库操作。
- **拒绝契约稳定**：被拒时返回既有信封 `{ code, message, data }`（HTTP 200 + 业务错误码），
  文案含旧表 / 存储过程与既有规范路由指引，前端可直接提示并提供跳转，而不是 500 或静默丢失数据。
- **零副作用**：绝不执行 `sp_Biz_*`、绝不占用单号、绝不写操作日志 / 钉钉通知、绝不改写任何业务 / 库存 / 财务记录。

## 4. 路由处置与兼容性影响

| 路由 | 处置 | 说明 |
| --- | --- | --- |
| `POST save` | **停用（fail closed）** | 写入门禁先于存储过程 |
| `POST {oid}/delete` / `{oid}/audit` / `{oid}/void` / `{oid}/restore` | **停用（fail closed）** | 五种状态流转同源门禁；不再先流转后校验 |
| `GET`（分页查询） | **保留** | 只读；读侧授权 / 迁移门槛不在本任务范围 |
| `GET navigate`（翻页导航） | **保留** | 只读；同上 |

### 4.1 专用路由的实际调用方（已核查）

- `src/ERP.Api/wwwroot/js/sales-order-v2.js`：列表 `GET /api/v2/sales-orders`、翻页 `GET .../navigate`、
  行内动作 `POST /api/v2/sales-orders/{oid}/{action}`（audit / delete / void / restore）。
- `src/ERP.Api/wwwroot/js/sales-order-v2-form.js`：`POST /api/v2/sales-orders/save`。

经核查，这两个脚本**未**被 `src/ERP.Api/wwwroot/index.html` 引用（该页面脚本清单中不存在 `sales-order-v2`），
即当前 SPA 不暴露该旧 v2 页面；实际在用的销售订单界面走规范路由 `api/sales-orders`。因此关闭其写路径
**不产生在线 UI 兼容性回归**，只影响直接以 HTTP 调用该专用路由的旧客户端（其将收到稳定业务错误而不是静默写入）。

- **核心规范工作流完全可用且不受影响**：`api/sales-orders`（创建 / 提交 / 审核 / 取消 / 删除 / 转换）
  不经过本门禁；报价单 / PI → 订单转换工作流（`SalesOrderConversion`）同样不经本控制器。
- **读 / 导出 / 报表端点保留**：本任务不改变任何旧报表 / 导出路由与 ERP-308 迁移退役门槛，
  也不改变本控制器的查询 / 导航路由。
- **无 schema / SP / 菜单改动**：不编辑任何存储过程 / 数据库结构 / 迁移脚本 / 种子数据；不新增菜单 / 角色 / 用户授权。

## 5. 验证证据

### 5.1 单元测试（`src/ERP.UnitTests/SalesOrderProcMutationTests.cs`，内存库 + 真实既有身份）

- **复用唯一策略**：族 `sales-order` 表名 / 存储过程 / 功能菜单 / 规范路由逐项一致，`HasValidatedAdapter == false`；
  控制器路由模板为 `api/v2/sales-orders`；有限目录中该族唯一。
- **五个写动作**：`save` / `delete` / `audit` / `void` / `restore` 全部 fail closed（`1004` + `sp_Biz_SalesOrder` + `/api/sales-orders`）。
- **实时身份 / 既有菜单**：缺失 / 非法 / 已删除 → `2000`；已禁用 → `2002`；无销售订单菜单 → `2002`；
  仅导出菜单 → `2002`；持有其他模块（外来）菜单 → `2002`；撤销菜单后下一次请求立即收敛 → `2002`；
  具备功能菜单与特权账号仍 fail closed（`1004`）。
- **零副作用**：五个写动作被拒后，`SalesOrders` / `SalesOrderDetails` / `Stocks` / `StockMovements`
  （库存来源单据审计）/ `SysDocumentNumberRules.CurrentSequence`（单号流水）/ `SysOperationLogs` / `SysDingTalkLogs`
  的只读快照完全不变。
- **伪造旧标识与金额载荷**：`Oid=987654321` + 伪造 `TotalAmount` / `DepositAmount` / `DepositRatio` 仍被拒绝且零写入，
  既有业务行金额不变。
- **规范工作流回归**：规范 `SalesOrderController`（`api/sales-orders`）创建 → 提交 → 审核 → 取消流转保持可用
  （既有 `SalesOrderControllerTests` / `SalesOrderCancellationTests` / `SalesOrderConversionTests` 全套回归保持不变）。

### 5.2 真实隔离 SQL 测试（`src/ERP.IntegrationTests/SalesOrderProcMutationSqlServerTests.cs`）

- **专用目标护栏**：必须在任何数据库访问之前精确命中 `(localdb)\NEWERP_AutoAcceptance` + 库名前缀 `NEWERP_AUTOTEST`
  + `Integrated Security=true`；错误实例 / 错误库名 / 非集成安全的连接串一律在 `AssertDedicatedTarget` 阶段被拒绝
  （`SalesOrderProcMutationTargetGuardTests`）。每次运行只创建一个**全新 GUID 后缀库**，发现同名库已存在立即拒绝，
  **绝不 drop / reset / 复用**任何数据库，也绝不读取 `appsettings*.json` / `.env` / 生产凭据。
- **真实驱动**：以新播种的既有「销售订单」功能菜单授权驱动真实 `SalesOrderProcController`；
  不新增 / 不修改任何既有菜单 / 角色 / 用户授权，无匿名 / 管理员降级。
- **拒绝矩阵**：无功能菜单 / 仅导出菜单 / 外来模块 → `2002`，撤销菜单 → `2002`，无身份 → `2000`；
  五个写动作被拒 → `1004`。
- **零变更证据**：五个写动作被拒后，`SalesOrders` / `SalesOrderDetails` / `StockIns` / `StockInDetails` / `StockOuts` /
  `Stocks` / `StockMovements`（库存来源单据审计）/ `SysDocumentNumberRules.CurrentSequence`（单号流水）/
  `SysOperationLogs` / `SysDingTalkLogs` 的只读快照完全不变；伪造旧 `Oid` 未命中任何规范销售订单。
- **两个独立连接竞态**：`save × save` 与 `save × audit` 两条用例，各自新建两个独立 DbContext / 连接 / 控制器并门闩对齐并发；
  两条连接都必须 fail closed（无赢家、零写入）。**「规范 vs 旧」适配器竞态不适用**：今天不存在任何已验证适配器，
  旧写路径整体 fail closed，因而不存在规范与旧的双写竞争；若将来登记适配器，必须补齐真实的
  数量 / 金额 / 回滚与规范-vs-旧双连接竞态验收。
- 保留既有库存来源单据审计与原始失败日志：测试只读取计数 / 快照，不删除任何既有行。

### 5.3 构建与回归

- `.NET 8` Release 构建（`NEWERP.sln`）通过，0 警告 / 0 错误（`ERP.IntegrationTests` 启用了 `TreatWarningsAsErrors`）。
- 既有 `ERP.UnitTests` 全套回归与既有规范销售订单 / 转换 / 取消回归保持不变（本任务只新增文件 + 在旧写路径前加门禁，
  并在 `SalesOrderProcController` 构造函数注入既有 `IErpDbContext`）。
- **构建完成不等于阶段验收**：真实 SQL 用例只有在受控 localdb 上真实执行通过才构成验收证据。
- 浏览器验收按任务配置为 `browser_acceptance.required = false`：不运行真实 Edge / UI / 截图验收；
  准确阻塞项为「旧 v2 销售订单页面未接入 SPA，旧写路径关闭后其保存 / 状态流转动作应改为跳转规范
  `api/sales-orders` 工作流」，属后续 UI 任务范围。

## 6. 明确边界（非目标）

- 不编辑任何存储过程 / 数据库结构 / 迁移脚本 / 种子数据；不新增表 / 列 / 索引。
- 不新增权限模型（菜单 / 角色 / 用户授权），不提供匿名或管理员降级，不把导出菜单当模块权限。
- 不复制一套独立的 ERP 实现；规范业务服务（数量、金额、库存成本、来源链路、下游依赖、生命周期）保持唯一权威。
- 不改变旧报表 / 导出路由与 ERP-308 迁移退役门槛；不改写本控制器的查询 / 翻页 / 导航等只读路由。
- 不清理 / 不删除任何既有业务行、库存来源审计或既有失败日志；不执行生产凭据或生产数据操作。

