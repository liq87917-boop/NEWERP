# 规范销售订单「实时主数据引用」护栏（ERP-423）

## 1. 背景与目标

规范销售订单写入（`POST /api/sales-orders`、`PUT /api/sales-orders/{id}`、`POST /api/sales-orders/{id}/submit`、
`POST /api/sales-orders/{id}/approve`）此前由多个护栏分层保护：

| 护栏 | 保护维度 |
|---|---|
| ERP-420 `SalesOrderMutationAuthorizationRules` | 实时身份 / 既有「销售订单」菜单 / 权威客户范围 |
| ERP-401 `SalesOrderSourceLineageRules` | 报价单 / PI 来源血缘与转换资格 |
| ERP-421 `SalesOrderMutationRules` | 确定性行锁 + 原子事务 + 锁内权威重读 |
| ERP-422 `SalesOrderAmountRules.ValidateNewWrite` | 数量 / 单价 / 币种 / 汇率 / 比例 / 金额精度 |

但**手工订单**（无来源）的表头 `CustomerId`、`SalesmanId`、`PortId` 与明细 `ProductId` / 单位此前都直接照抄
调用方提交值 —— 从未核对**实时主数据**是否存在、是否已删除、是否已停用。ERP-420 在**无范围限制**（特权）账号下
会直接放行，因此来源为空的订单可以把不存在 / 已删除 / 已停用的客户 / 商品 / 业务员 / 港口写进核心手工订单，
并在提交 / 审核时把无效引用带进运营需求。

ERP-423 在**唯一写入入口**上补齐「实时主数据引用复核」，并把新增 / 修改 / 提交 / 审核四条路径收敛到同一口径，
同时保持历史读取 / 打印完全只读、不自动回填或修正。

## 2. 唯一权威规则

`SalesOrderMasterReferenceRules`（`src/ERP.Application/Services/SalesOrderMasterReferenceRules.cs`）逐项要求：

| 项 | 要求 | 失败码 |
|---|---|---|
| 必填客户 `CustomerId` | 正整数，且解析到**存在、未删除、启用（`Status = 1`）**的客户 | 非正 `1001`；不存在 / 已删除 `1002`；已停用 `1004` |
| 可选业务员 `SalesmanId` | 留空跳过；填写时为正整数，且解析到**存在、未删除、在职（`Status = 1`）**的员工 | 非正 `1001`；不存在 / 已删除 `1002`；已离职 / 停用 `1004` |
| 可选目的港 `PortId` | 留空跳过；填写时为正整数，且为**存在、未删除、启用**的港口字典项（`BaseOtherInfo.InfoType = Port`） | 非正 `1001`；不存在 / 已删除 `1002`；非港口字典项 `1001`；已停用 `1004` |
| 每条**有效（未软删除）**明细 `ProductId` | 正整数，且解析到**存在、未删除、启用（`Status = 1`）**的商品 | 非正 `1001`；不存在 / 已删除 `1002`；已停用 `1004` |
| 既有有效单位口径 | 明细单位（去首尾空白、大小写不敏感）必须等于商品**基础单位**（`BaseProduct.Unit`）或**合法装箱单位**（`BaseProduct.PackageUnit` 且 `UnitsPerPackage > 0`） | `1004` |

**单位口径与既有 ERP 同源**（`StockUnitConversion` / `StockOutOrderFulfillmentRules.TryBaseUnitQuantity`）：
合法 packaging / alternative 单位照常保留；**绝不臆造单位换算**，也**绝不假设明细单位等于默认基础单位**；
商品基础单位与装箱单位均为空（历史商品未维护单位）时不产生单位判定，保持可用。

## 3. 接入点与时序

| 路径 | 接入点 | 时序（相对既有步骤） |
|---|---|---|
| 新增 `POST /api/sales-orders` | `SalesOrderController.Create` | 授权 → 开事务 → 条款校验 → **来源血缘 / 转换资格** → **主数据引用复核** → 单号预约 → 字段 / 明细赋值 |
| 修改 `PUT /api/sales-orders/{id}` | `SalesOrderController.Update` | 授权与归属 → 开事务 → 条款校验 → 锁内权威重读 → **来源血缘复核** → **主数据引用复核** → 字段 / 明细赋值 |
| 提交 `POST /api/sales-orders/{id}/submit` | `RunLineageGuardedStatusChangeAsync` | 订单行锁内重读 → 状态 / 来源血缘复核 → 已持久化条款校验 → **主数据引用复核** → 置状态 |
| 审核 `POST /api/sales-orders/{id}/approve` | 同上 | 同上 |

- 主数据复核**先于单号预约与任何表头 / 明细赋值**，任一项失败即整体回滚：不消耗单号、不落任何数据、不产生下游副作用。
- 来源血缘错误（未审核 / 异客户 / 无法解析 / 重复目标等）保持**既有优先级**，不受本护栏影响。
- 提交 / 审核在**同一把订单行锁内**重查引用 —— 客户 / 商品 / 业务员 / 港口在提交前被删除 / 停用，
  或单位口径失效时**原子拒绝**，绝不让无效引用提交运营需求。
- 只做**有界只读投影查询**：客户 / 业务员 / 港口各一次单行查询，商品一次批量查询（`ProductId` 去重集合），
  绝不逐行查库，也不产生任何被跟踪的变更。

## 4. 边界（不做的事）

- 不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，不新增实体 / 服务拆分，不写主数据；
- 不臆造单位换算，不假设明细单位等于默认基础单位，不修改数量 / 单价 / 金额 / 币种 / 汇率 / 单位等商业语义；
- 不做历史数据回填 / 自动修正：`GetById` / `GetPrint` / 导出 / 时间线等读取路径仍原样返回已持久化值，
  即使引用主数据已删除 / 停用也可读、不被改写；
- 来源报价单 / PI 的历史链接仍由 ERP-401 血缘规则治理，本护栏**绝不**把来源租户 / 来源 Id 与本次客户 / 商品归属混为一谈；
- 授权仍先于主数据复核：缺菜单 / 越界的账号即便提交无效引用也返回同一受控权限错误（非披露），不因
  「客户 / 商品是否存在」而差异化响应。

## 5. 验收证据

| 类型 | 文件 | 覆盖 |
|---|---|---|
| 单元 | `src/ERP.UnitTests/SalesOrderMasterReferenceTests.cs` | 客户 / 商品 / 可选业务员 / 可选目的港缺失 / 已删除 / 已停用 / 非正整数的受控错误、既有有效单位口径（基础单位 / 装箱单位 / 无效装箱数 / 未维护单位）、控制器新增 / 修改零写入零单号、提交 / 审核来源失效原子拒绝、合法手工订单完整生命周期、历史读取 / 打印不被回填、授权前置非披露 |
| 集成 | `src/ERP.IntegrationTests/SalesOrderMasterReferenceSqlServerTests.cs` | 以上场景在**真实 SQL Server**（专用 localdb）上的等价执行 + 目标护栏 fail-closed |
| 回归 | `SalesOrderControllerTests` / `SalesOrderMutationTests` / `SalesOrderWriteValidationTests` / `SalesOrderSourceLineageTests` / `SalesOrderConversionTests` / `SalesOrderMutationAuthorizationTests` / `SalesOrderProcMutationTests` / `OrderTraceabilityTests` / `SalesOrderWriteValidationSqlServerTests` 等 | 既有正常流程与来源血缘 / 条款校验 / 转换契约保持不变（播种既有合法主数据，绝不削弱生产校验） |

## 6. 真实 SQL 夹具安全口径

- 目标必须是专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且
  `Integrated Security=true`；错误实例 / 错误库名 / 非集成安全的连接串一律在 `AssertDedicatedTarget` 阶段
  于**任何数据库访问之前**被拒绝（含 fail-closed 单元覆盖）。
- 每次运行只创建一个**全新 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，
  绝不读取 `appsettings*.json` / `.env` / 生产凭据；只使用既有认证授权，不新增菜单 / 角色 / 用户授权。
- **构建完成不等于阶段验收**：真实 SQL 场景须在专用 localdb 上真实执行通过才算验收证据。
