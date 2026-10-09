# 规范销售订单（`api/sales-orders`）普通写入实时授权

> 任务：`ERP-420`「Authorize canonical sales order ordinary writes across manual and historical sources」（阶段 3 核心业务流程完整性）。
> 关联：`ERP-401`（销售订单普通表单保存来源血缘护栏，`docs/sales-order-source-lineage.md`）、
> `ERP-413`（执行证据入口实时授权，`docs/sales-order-execution-authority.md`）、`ERP-347` / `ERP-369` / `ERP-381`（取消护栏）、
> `ERP-097`（业务员客户数据范围，`docs/业务员数据范围说明.md`）。

## 1. 背景与问题证据（修复前）

规范销售订单控制器 `SalesOrderController`（路由 `api/sales-orders`）的**写入**入口此前授权口径不一致：

| 写入动作 | 修复前行为 |
| --- | --- |
| `POST /`（新增，显式链接**可解析**来源） | 复用 `SalesOrderSourceLineageRules.EnsureWriteAuthorizedAsync`（实时身份 + 既有「销售订单」菜单 + 客户范围） |
| `POST /`（新增，**手工无来源**） | **不解析任何授权**，只生成单号并写入 |
| `POST /`（新增，**显式来源 Id 全部无法解析**的历史值） | **不解析任何授权**，按显式历史值原样写库 |
| `PUT /{id}`（修改，**无来源 / 历史无法解析来源**） | **不解析任何授权**，直接改写字段与明细 |
| `POST /{id}/submit`、`POST /{id}/approve`（**未链接**订单） | **不解析任何授权**，直接状态流转 |
| `DELETE /{id}`（软删除） | 继承自 `DocumentControllerBase.Delete`，**无任何规范实体授权** |
| `POST /{id}/cancel` | 已有 `SalesOrderCancellationRules`（身份 / 菜单 / 客户范围 + 下游护栏） |

因此「无来源」与「历史无法解析来源」这两条路径（以及继承的软删除）存在授权缺口：任何已登录账号只要构造一个请求，
即可创建 / 改写 / 删除 / 提交流转**范围外客户**的销售订单 —— 运营记录入口的授权缺口，不是「再发布一份报表」。

## 2. 集中化的写入护栏（`SalesOrderMutationAuthorizationRules`）

新增 `src/ERP.Application/Services/SalesOrderMutationAuthorizationRules.cs`（**写入入口唯一权威口径**），调用方（控制器）在任何写入之前调用：

| 方法 | 作用 |
| --- | --- |
| `EnsureLiveIdentityAsync(db, userId)` | 实时身份：缺失 / 非法按未认证拒绝，账号不存在 / 已删除按未认证拒绝，已禁用按权限不足拒绝；随后解析 ERP-097 范围 |
| `EnsureWriteAuthorizedAsync(db, userId)` | 写入完整授权：实时身份 + 既有「销售订单」菜单（特权账号豁免菜单但仍须实时身份）+ ERP-097 客户范围 |
| `EnsureProposedCustomerAllowed(scope, customerId)` | 拟议客户（新增 / 改派）范围复核：受限账号下缺失 / 非正 / 越界一律 fail closed |
| `EnsureOrderAllowedAsync(db, scope, orderId)` | 持久化订单按**持久化 `SalesOrder.CustomerId`** 的权威归属复核（最小投影、有界只读），范围外 / 已删除 / 不存在返回同一非披露错误 |

口径（fail closed，绝不缓存，每次请求重新解析）：

1. **实时身份**：不做「无请求路径 / 匿名进程内调用」豁免——写入路径由控制器**无条件**调用，因此直接调用控制器的写入方法同样 fail closed
   （与只读入口的 `RequiresLiveAuthorization()` 进程内豁免口径**明确区分**）。
2. **既有菜单授权**：普通账号必须实时具备既有「销售订单」（`sales-order`）功能菜单；导出菜单 `sales-order-export` **绝不**当作模块权限；
   特权账号（超级管理员 / 系统内置角色 / 显式特权角色）沿用既有全部访问口径。**不新增任何菜单 / 角色 / 用户授权**。
3. **权威客户范围**：只复用唯一权威口径 `SalespersonDataScopeService`（ERP-097）；归属只按**持久化 `CustomerId`** 判定，
   **绝不**按来源单号 / 客户名 / 业务员快照、金额或相似度推断，也**绝不**把旧库 `Oid` 推断为规范 `Id`。
4. **非披露错误**：范围外订单、已删除订单与不存在订单返回**同一**受控错误「销售订单不存在」（`Err:NotFound`）。
5. **锁内复查**：在修改 / 提交 / 审核 / 取消已持有订单行锁（`UPDLOCK, HOLDLOCK`）时**重新读取实时权限与持久化归属**，
   授权撤销 / 账号停用 / 客户改派在下一次请求立即收敛，绝不按授权前读到的陈旧范围放行。

## 3. 逐入口接线（先授权、后读写）

- **新增**（`POST /`）：`EnsureCanonicalWriteAuthorizedAsync()` → `EnsureProposedCustomerAllowed(scope, entity.CustomerId)` → 才解析来源 / 预约单号 / 写入。
  无来源手工订单与显式历史无法解析来源同样适用；显式可解析来源保留 ERP-401 的权威解析 / 资格 / 唯一目标 / 确定性锁序与原子事务。
- **修改**（`PUT /{id}`）：持久化归属（先于读取明细 / 暴露状态）与拟议客户范围先复核；显式来源分支在订单行锁内再次复核实时权限与归属后才改写。
- **提交 / 审核**（`POST /{id}/submit|approve`）：先复核写入授权与持久化归属（先于暴露状态）；未链接手工订单走既有口径，已登记来源的订单在行锁内复查后再流转。
- **删除**（`DELETE /{id}`）：重写继承入口，先复核写入授权与持久化归属，再沿用基类「仅待提交状态可软删除」的既有口径。
- **取消**（`POST /{id}/cancel`）：在可串行化事务的订单行锁内复核写入授权与持久化归属，**保留**既有 `SalesOrderCancellationRules.ValidateCancellationAsync` 下游护栏
  （已审核未冲销出库 / 未取消采购履约 / 有效收款引用 / 未取消定金申请 / 已审核预装柜需求证据都拒绝）。

## 4. 保留既有来源血缘（不改写、不新增）

- **来源血缘不变**：显式可解析来源仍走 `SalesOrderSourceLineageRules` 的权威解析 / 规范化 / 既有转换资格 / 唯一目标 / 历史来源冻结；
  历史来源**绝不静默清除**，显式改绑仍需完整实时复核且下游已有证据时冻结；审计留痕原样保留。
- **拒绝零副作用**：授权先于任何单号预约 / 明细替换 / 状态流转 / 软删除；被拒请求零写入——
  不改写销售订单 / 明细 / 报价单 / PI / 出库 / 库存 / 收款 / 发票 / 财务 / 客户主数据，不产生任何库存 / 财务副作用，
  不删除历史证据与审计留痕。每个被拒请求都由既有全局中间件记录**有界**告警（仅错误文案 / 错误码 / 路径，绝不回声载荷或密钥）。
- **不新增**表 / 列 / 索引 / 菜单 / 权限模型，**不新增任何用户授权**，不做旧库 `Oid` → 规范 `Id` 推断。

## 5. 验证证据

### 5.1 单元测试（`src/ERP.UnitTests/SalesOrderMutationAuthorizationTests.cs`，内存库 + 真实既有身份 / 菜单 / 范围）

- **允许的写入仍可用**：受限业务员（既有「销售订单」菜单 + 本人客户）可新增（含无法解析历史来源并原样保留显式历史值）/ 修改 / 提交 / 审核 / 删除 / 取消；
- **无来源与历史来源路径 fail closed**：手工无来源与显式历史无法解析来源的新增在越界 / null 客户上一律 `Err:Forbidden` 且零写入（修复前绕过）；
- **持久化归属先决**：修改 / 提交 / 审核 / 删除 / 取消在他人 / 已删除 / 不存在订单上返回**同一** `Err:NotFound` + 同一文案，明细 / 状态 / 软删位不变；
- **拟议客户先决**：改派到越界客户被 `Err:Forbidden` 拒绝且客户不变；
- **身份 / 菜单拒绝矩阵**：无身份 / 非法（非数字声明）/ 已删除 → `Err:Unauthorized`；已禁用 / 缺菜单 / 已撤销菜单 → `Err:Forbidden`（均零写入，直接调用控制器即生效）；
- **取消保留既有下游护栏**：本人订单存在「已审核未冲销」出库时仍 `Err:RuleConflict`（而非 `Err:NotFound`），状态不变。

### 5.2 真实隔离 SQL 测试（`src/ERP.IntegrationTests/SalesOrderMutationAuthorizationSqlServerTests.cs`）

- **专用目标护栏**：必须在任何数据库访问之前精确命中 `(localdb)\NEWERP_AutoAcceptance` + 库名前缀 `NEWERP_AUTOTEST`
  + `Integrated Security=true`；错误实例 / 错误库名 / 非集成安全的连接串一律在 `AssertDedicatedTarget` 阶段被拒绝
  （`SalesOrderMutationAuthorizationTargetGuardTests`）。每次运行只创建一个**全新 GUID 后缀库**，发现同名库已存在立即拒绝，
  **绝不 drop / reset / 复用**任何数据库，也绝不读取 `appsettings*.json` / `.env` / 生产凭据。
- **真实驱动 + 既有授权**：只在本隔离 GUID 库内播种既有「销售订单」功能菜单授权 + 业务员客户数据范围；
  以真实 `SalesOrderController` 驱动，不复制测试专用实现，不新增 / 不修改任何既有菜单 / 角色 / 用户授权，无匿名 / 管理员降级。
- **覆盖全部写入路由与来源形态**：手工无来源 / 无法解析历史来源 × 本人 / 他人 / null 客户；新增 / 修改 / 删除 / 提交 / 审核 / 取消；
  身份拒绝矩阵（无身份 / 非法 / 已删除 → 未认证；已禁用 / 无菜单 / 仅导出菜单 / 已撤销菜单 → 权限不足）；客户改派；特权账号保留既有口径。
- **零写入证据**：每次拒绝前后对 `SalesOrders` / `SalesOrderDetails` / `StockOuts` / `StockMovements`（**保留库存来源单据审计**）
  / `ProformaInvoices` / `Quotations` / `SysOperationLogs` 做只读快照，全部不变；取消的下游护栏在真实 SQL 上仍生效。

### 5.3 构建与回归

- `.NET 8` Release 构建（`NEWERP.sln`）通过，0 警告 / 0 错误。
- `ERP.UnitTests` 新增 `SalesOrderMutationAuthorizationTests`，既有销售订单 / 业务员数据范围 / 取消 / 来源血缘回归保持不变。
- **构建完成不等于阶段验收**：真实 SQL 用例只有在受控 localdb 上真实执行通过才构成验收证据。

## 6. 明确边界（非目标）

- 不修改任何既有存储过程 / 数据库结构 / 迁移脚本 / 种子数据；不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权。
- 不放宽任何既有授权，也不提供匿名 / 管理员降级；不把导出菜单当模块权限，不做旧库 `Oid` → 规范 `Id` 推断。
- 只读入口（列表 / 详情 / 执行证据）沿用 ERP-413 口径不变；旧 v2 专用路由（ERP-410 / ERP-411）不在本任务范围。
- 不清理 / 不删除任何既有业务行、库存来源审计或既有失败日志；不执行生产凭据或生产数据操作。
- 浏览器验收按任务配置为 `browser_acceptance.required = false`：不运行真实 Edge / UI / 截图验收。

