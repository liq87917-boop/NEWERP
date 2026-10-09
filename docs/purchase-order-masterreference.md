# 规范采购订单「实时主数据引用」护栏（ERP-427）

## 1. 背景与目标

规范采购订单写入（`POST /api/purchase-orders`、`PUT /api/purchase-orders/{id}`、`POST /api/purchase-orders/{id}/submit`、
`POST /api/purchase-orders/{id}/approve`）此前由多个护栏分层保护：

| 护栏 | 保护维度 |
|---|---|
| ERP-371 `PurchaseOrderAuthorizationRules` | 实时身份 / 既有「采购订单」菜单 / 权威归属客户与来源范围 |
| ERP-346 / ERP-393 `PurchaseSalesOrderLinkRules` | 显式归属销售订单来源解析与商品 / 单位兼容性 |
| ERP-425 `PurchaseOrderMutationRules` | 确定性行锁 + 原子事务 + 锁内权威重读 |
| ERP-426 `PurchaseOrderAmountRules.ValidateNewWrite` | 数量 / 单价 / 币种 / 汇率 / 税率 / 金额精度 |

但**手工采购**（无归属来源）的表头 `SupplierId`、`BuyerId`、`PortId` 与明细 `ProductId` / 自由文本单位此前都直接照抄
调用方提交值 —— 从未核对**实时主数据**是否存在、是否已删除、是否已停用。特权账号在**无范围限制**时会被直接放行，
因此手工采购可以把不存在 / 已删除 / 已停用的供应商与商品写进核心采购订单，并在提交 / 审核时把无效引用带进收货 / 应付需求。

ERP-427 在**唯一写入入口**上补齐「实时主数据引用复核」，并把新增 / 修改 / 提交 / 审核四条路径收敛到同一口径，
同时保持历史读取 / 打印完全只读、不自动回填或修正。

## 2. 唯一权威规则

`PurchaseOrderMasterReferenceRules`（`src/ERP.Application/Services/PurchaseOrderMasterReferenceRules.cs`）逐项要求：

| 项 | 要求 | 失败码 |
|---|---|---|
| 必填供应商 `SupplierId` | 正整数，且解析到**存在、未删除、启用（`Status = 1`）**的供应商 | 非正 `1001`；不存在 / 已删除 `1002`；已停用 `1004` |
| 可选采购员 `BuyerId` | 留空跳过；填写时为正整数，且解析到**存在、未删除、在职（`Status = 1`）**的员工 | 非正 `1001`；不存在 / 已删除 `1002`；已离职 / 停用 `1004` |
| 可选起运港 `PortId` | 留空跳过；填写时为正整数，且为**存在、未删除、启用**的港口字典项（`BaseOtherInfo.InfoType = Port`） | 非正 `1001`；不存在 / 已删除 `1002`；非港口字典项 `1001`；已停用 `1004` |
| 每条**有效（未软删除）**明细 `ProductId` | 正整数，且解析到**存在、未删除、启用（`Status = 1`）**的商品 | 非正 `1001`；不存在 / 已删除 `1002`；已停用 `1004` |
| 既有有效单位口径 | 明细单位（去首尾空白、大小写不敏感）必须等于商品**基础单位**（`BaseProduct.Unit`）或**合法装箱单位**（`BaseProduct.PackageUnit` 且 `UnitsPerPackage > 0`） | `1004` |

**单位口径与既有 ERP 同源**（`StockUnitConversion` / 既有库存换算 / 采购来源链接兼容性判定）：
合法 packaging / alternative 单位照常保留；**绝不臆造单位换算**，也**绝不假设明细单位等于默认基础单位**；
商品基础单位与装箱单位均为空（历史商品未维护单位）时不产生单位判定，保持可用。

## 3. 接入点与时序

| 路径 | 接入点 | 时序（相对既有步骤） |
|---|---|---|
| 新增 `POST /api/purchase-orders` | `PurchaseOrderController.Create` | 授权 → 开事务 → 来源加锁 → 来源血缘 / 转换资格 → **条款校验（ERP-426）** → **主数据引用复核** → 单号预约 → 字段 / 明细赋值 |
| 修改 `PUT /api/purchase-orders/{id}` | `PurchaseOrderController.Update` | 授权与归属 → 开事务 → 条款校验 → 锁内权威重读 → **来源血缘复核** → **主数据引用复核** → 字段 / 明细赋值 |
| 提交 `POST /api/purchase-orders/{id}/submit` | `PurchaseOrderController.Submit` | 采购订单行锁内重读 → 实时授权 → 来源血缘复核 → 已持久化条款校验 → **主数据引用复核** → 置状态 |
| 审核 `POST /api/purchase-orders/{id}/approve` | `PurchaseOrderController.Approve` | 同上 |

- 主数据复核**先于单号预约与任何表头 / 明细赋值**，任一项失败即整体回滚：不消耗单号、不落任何数据、不产生下游副作用。
- 来源血缘错误（未审核 / 异客户 / 无法解析 / 重复目标等）保持**既有优先级**，不受本护栏影响。
- 提交 / 审核在**同一把采购订单行锁内**重查引用 —— 供应商 / 商品 / 可选采购员 / 起运港在提交 / 审核前被删除 / 停用，
  或单位口径失效时**原子拒绝**，绝不让无效引用提交运营需求。
- 只做**有界只读查询**：供应商 / 采购员 / 起运港各一次单行查询，商品一次批量查询（`ProductId` 去重集合），
  绝不逐行查库，也不产生任何被跟踪的变更。

### 3.1 执行范围（与既有控制器约定一致）

复核只在**真实 HTTP 写入请求**上执行（MVC 绑定下 `Request.Path` 已赋值），与 `RequiresLiveAuthorization()`
的既有取舍同源：进程内直接调用（历史单元测试 / 内部派生读取，无 HTTP 请求管线）保持既有行为，
这类调用不可能由外部请求到达。真实请求在到达本复核之前已由 `EnsureProposedAuthorizedAsync` /
`EnsureOrderAuthorizedAsync` 完成身份 / 菜单 / 权威归属范围 fail closed（非披露），因此本复核只可能在
**已授权**的外部请求上生效，绝不把缺失身份当作管理员，也绝不新增任何授权 / 菜单 / 权限模型。
真实 SQL 集成测试同样以 `Request.Path` 已赋值的真实控制器驱动，复核在真实数据库上真实生效。

## 4. 边界（不做的事）

- 不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，不新增实体 / 服务拆分，不写主数据；
- 不臆造单位换算，不假设明细单位等于默认基础单位，不修改供应商 / 数量 / 单价 / 金额 / 币种 / 汇率 / 单位等商业语义；
- 不做历史数据回填 / 自动修正：`GetById` / `GetPrint` / 导出 / 进度 / 派生只读视图等读取路径仍原样返回已持久化值，
  即使引用主数据已删除 / 停用也可读、不被改写；
- 归属销售订单 / 客户的历史链接仍由 ERP-346 链接规则治理，本护栏**绝不**把来源与本次供应商 / 商品归属混为一谈；
- 授权仍先于主数据复核：缺菜单 / 越界的账号即便提交无效引用也返回同一受控权限错误（非披露），不因
  「供应商 / 商品是否存在」而差异化响应。

## 5. 验收证据

| 类型 | 文件 | 覆盖 |
|---|---|---|
| 单元 | `src/ERP.UnitTests/PurchaseOrderMasterReferenceTests.cs` | 供应商 / 商品 / 可选采购员 / 可选起运港缺失 / 已删除 / 已停用 / 非正整数的受控错误、既有有效单位口径（基础单位 / 装箱单位 / 无效装箱数 / 未维护单位）、控制器新增 / 修改零写入零单号、提交 / 审核引用失效原子拒绝、合法手工采购完整生命周期、历史读取 / 打印不被回填、授权前置非披露 |
| 集成 | `src/ERP.IntegrationTests/PurchaseOrderMasterReferenceSqlServerTests.cs` | 以上场景在**真实 SQL Server**（专用 localdb）上的等价执行 + 目标护栏 fail-closed |
| 回归 | `PurchaseOrderAuthorizationTests` / `PurchaseOrderControllerTests` / `PurchaseOrderCancellationTests` / `PurchaseOrderMutationTests` / `PurchaseOrderWriteValidationTests` / `OrderTraceabilityTests` 及既有采购只读派生单元测试 | 既有正常流程与来源血缘 / 条款校验 / 锁协议 / 授权范围契约保持不变（按既有 HTTP 请求管线口径播种合法主数据，绝不削弱生产校验） |

## 6. 真实 SQL 夹具安全口径

- 目标必须是专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且
  `Integrated Security=true`；错误实例 / 错误库名 / 非集成安全的连接串一律在 `AssertDedicatedTarget` 阶段
  于**任何数据库访问之前**被拒绝（含 fail-closed 单元覆盖）。
- 每次运行只创建一个**全新 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，
  绝不读取 `appsettings*.json` / `.env` / 生产凭据；只使用既有认证授权，不新增菜单 / 角色 / 用户授权。
- **构建完成不等于阶段验收**：真实 SQL 场景须在专用 localdb 上真实执行通过才算验收证据。

