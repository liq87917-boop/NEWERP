# 采购订单规范运营读取（详情 / 打印 / JSON 导出）只含未删除明细（ERP-430）

## 1. 背景与目标

采购订单的规范运营快照由三个只读入口呈现：

| 入口 | 路由 | 控制器方法 |
|---|---|---|
| 详情 | `GET /api/purchase-orders/{id}` | `PurchaseOrderController.GetById` |
| 打印 | `GET /api/purchase-orders/{id}/print` | `PurchaseOrderController.GetPrint` |
| JSON 运营导出 | `GET /api/purchase-orders/export` | `PurchaseOrderController.Export` |

此前 `GetPrint` 已按 `!IsDeleted` 过滤明细，但 `GetById` 与 `Export` 直接 `Include(o => o.Details)`，
把**已软删除的明细行**一并返回。结果是同一张采购订单在详情 / JSON 导出里出现已作废（tombstones）子行，
与打印口径不一致，运营快照可能误导下游对账 / 复核。

ERP-430 把三者收敛到**同一权威读取口径**：只返回**未删除父单**的**未删除明细行**，且过滤在**物化之前**
下推到数据库；同时严格保持历史存储表头金额与来源 / 审计快照原样，读取侧绝不重算、绝不改写被软删除的行。

## 2. 唯一权威读取口径

`PurchaseOrderController.OperationalReadQuery()`（`src/ERP.Api/Controllers/PurchaseOrderController.cs`）：

```csharp
private IQueryable<PurchaseOrder> OperationalReadQuery()
    => Set.AsNoTracking().Include(o => o.Details.Where(d => !d.IsDeleted));
```

- 明细 `IsDeleted = 0` 过滤是 **EF Core filtered include**，在生成 SQL 时即生效（**先于物化**），
  被软删除的明细行**绝不进入内存**，绝不「先查全量再内存过滤」；
- 父单未删除由调用方补 `!o.IsDeleted`；
- 详情 / 打印 / JSON 导出三处**共用**该方法，天然保证三者对有效明细行一致；
- 只读：`AsNoTracking`，不写库、不改单据 / 明细 / 状态 / 库存 / 财务，不消耗任何单号。

## 3. 接入点与时序

| 路径 | 接入点 | 口径 |
|---|---|---|
| 详情 `GET /api/purchase-orders/{id}` | `GetById` | `OperationalReadQuery()` + `!o.IsDeleted` → `EnsureOrderAuthorizedAsync`（ERP-371） |
| 打印 `GET /api/purchase-orders/{id}/print` | `GetPrint` | 同上（不再使用 `entity.Details = entity.Details.Where(...)` 的内存过滤） |
| JSON 导出 `GET /api/purchase-orders/export` | `Export` | `ApplyProcurementScopeAsync(OperationalReadQuery().Where(o => !o.IsDeleted))`，客户范围先于日期过滤与物化下推 |

- **授权不变**：仍复用 ERP-371 实时身份 / 账号状态 / 既有「采购订单」菜单 + 权威归属客户范围
  （显式归属客户 + 权威归属销售订单客户），**不新增菜单 / 角色 / 用户授权，不做身份旁路**；
- **范围先于物化**：导出客户范围在计数 / 日期过滤 / 物化之前下推到数据库，绝不「先查全量再内存过滤」；
- **父单未删除**：已删除父单在详情 / 打印被非披露拒绝（`1002`），且在导出结果中不出现。

## 4. 边界（不做的事）

- 不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，不新增实体 / 服务，不新增任意 JOIN / 任意 SQL；
- **不重算**币种总额：表头 `TotalAmount` 等历史存储值原样返回，即使已删除明细不在结果中也绝不回落 / 重算；
- **不改写 tombstoned 行**：被软删除的明细行物理保留、字段原样（`IsDeleted` / 数量 / 单价 / 金额 / 时间戳），
  读取绝不物理删除、绝不回填；
- 保持不变：数量 / 金额校验、主数据校验、来源锁、原子事务与审计留痕（ERP-425 / 426 / 427）一律不动；
- Excel 导出契约（`ExportExcel` 列白名单与工作簿结构）保持不变；
- 不做历史数据回填 / 自动修正，不切换任何旧报表入口，不部署、不访问生产。

## 5. 验收证据

| 类型 | 文件 | 覆盖 |
|---|---|---|
| 单元 | `src/ERP.UnitTests/PurchaseOrderOperationalDetailTests.cs` | 详情 / 打印 / JSON 导出有效明细一致；软删除明细不进运营快照；表头历史金额与来源快照保留；已删除父单非披露拒绝并排除导出；受限账号范围内只读 / 越界 fail closed；缺失 / 畸形 / 禁用 / 撤销菜单身份 fail closed 且零副作用；源码契约（单一 filtered include，无内存过滤） |
| 集成 | `src/ERP.IntegrationTests/PurchaseOrderOperationalDetailSqlServerTests.cs` | 以上场景在**真实 SQL Server**（专用 localdb，全新 GUID 库）上的等价执行 + 目标护栏 fail-closed + 来源 / 库存 / 应付 / 付款证据不变 |
| 回归 | `PurchaseOrderAuthorizationTests` / `PurchaseOrderControllerTests` / `PurchaseOrderMasterReferenceTests` / `PurchaseOrderWriteValidationTests` 等 | 既有授权 / 校验 / 审计契约保持不变（安全验证档：Release 构建 + `ERP.UnitTests` 全量） |

## 6. 真实 SQL 夹具安全口径

- 目标必须是专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且
  `Integrated Security=true`；错误实例 / 错误库名 / 非集成安全的连接串一律在 `AssertDedicatedTarget` 阶段
  **于任何数据库访问之前**被拒绝（含 fail-closed 单元覆盖）；
- 每次运行只创建一个**全新 GUID 后缀库**，发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库；
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，
  绝不读取 `appsettings*.json` / `.env` / 生产凭据；只使用既有认证授权，不新增菜单 / 角色 / 用户授权；
- **构建完成不等于阶段验收**：真实 SQL / HTTP 场景须在专用 localdb 上真实执行通过才算验收证据。
