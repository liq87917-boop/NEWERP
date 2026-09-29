# 动态采购订单报表预览（ERP-125）

## 1. 目标

授权的采购用户在既有采购订单可见范围内，选择采购订单字段并应用有界筛选，只读预览订单；无身份 / 无角色 /
无采购订单菜单授权 / 无效字段或筛选时一律拒绝（fail closed），且不泄露范围外数据。

## 2. 接口

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/purchase-orders/report` | 返回有限白名单字段目录（需登录 + 采购订单菜单授权） |
| POST | `/api/purchase-orders/report` | 按选定字段与有界筛选预览当前账号可见（未删除）的采购订单 |

请求体（POST，`DynamicPurchaseOrderReportRequest`）：

- `fields`：选定字段键（仅限白名单；留空 = 返回全部白名单字段，保持目录顺序）；
- `startDate` / `endDate`：订单日期区间（含当日）；
- `supplierId`：供应商 Id 筛选；
- `status`：状态筛选（`Pending` / `Submitted` / `Approved` / `Rejected` / `Completed` / `Cancelled`）；
- `currency`：币种筛选（`CNY` / `USD` / `EUR` / `HKD` / `GBP` / `JPY`）；
- `page`（默认 1）/ `pageSize`（默认 20，**上限 100**）。

响应（`DynamicPurchaseOrderReportPageDto`）：`columns`（按请求顺序的选定列）、`rows`（每行仅含选定字段值）、
`total` / `page` / `pageSize` / `totalPages`，以及 `readOnlyText` / `boundaryText` / `disclaimerText` 口径文案。

## 3. 数据边界与安全

1. **身份**：缺少登录用户 Id → 未认证（`2000`），不返回任何数据。
2. **菜单授权**：复用既有「角色 → 菜单」模块授权，要求 `purchase-order`（采购订单）菜单；
   无角色 / 无该菜单授权 → 权限不足（`2002`）。授权每次请求都重新查询，回收后立即收敛。
3. **可见范围**：复用既有采购订单列表可见性（仅 `!IsDeleted`，同 `PurchaseOrderController.GetPaged` 口径），
   **不**引入更宽的角色或数据范围策略（不做业务员数据范围）。
4. **字段白名单**：字段仅限 `PurchaseOrder` 持久化字段的有限白名单（24 个），未知字段显式拒绝；
   不拼接、不接受任意字段名或 SQL。
5. **筛选白名单**：仅供应商 / 订单日期 / 状态 / 币种；状态与币种只接受枚举取值，未知取值拒绝；
   开始日期晚于结束日期拒绝；`pageSize` 超出 `1~100` 拒绝（查询前校验）。
6. **只读**：全程 `AsNoTracking`，无 `Add` / `Update` / `Remove` / `SaveChanges`，
   不执行任意 SQL（无 `FromSql*` / 存储过程调用），不做任何写入。

## 4. 请求审计

- 预览为 `POST`，由既有 `OperationLogMiddleware` 按 HTTP 方法记录到 `SysOperationLog`，本查询自身不写任何操作日志、不新增审计模型。
- 目录为 `GET`，按既有中间件口径不落操作日志（沿用现有审计约定）。

## 5. 只读保证

- 不新增表 / 列 / 索引，不执行迁移、生产 SQL 或真实数据库操作；
- 不改写采购订单、供应商、库存、入库、发票、付款、财务或结算记录；
- 不读取或打印任何连接串、JWT / OSS 密钥或 `.env` 值。

## 6. 权限与财务证据边界

- **权限边界**：仅复用既有 `purchase-order` 菜单授权，不新增角色、不新增数据范围策略；
  未删除的采购订单即为完整可见范围（与既有采购订单列表一致）。
- **财务证据边界**：本报表是「原始持久化证据清单」，供应商与订单引用（`SupplierId` / `OrderNo` /
  `ContractNo` / `OwningSalesOrderNo` 等）仅为回显，**不**计算应付余额、**不**做结算；
  因此 `SettlementProgress`（结算进度）字段刻意**不**纳入白名单，金额按订单原币呈现、不做跨币种换算或汇总。

## 7. 测试覆盖（`ERP.UnitTests/DynamicPurchaseOrderReportTests.cs`）

- 字段白名单目录（有限、白名单键、所需菜单与 100 上限、不含 `settlementProgress`）；
- 选定列与顺序、未指定字段默认全字段；
- 供应商 / 日期 / 状态 / 币种筛选；
- 稳定分页（按 Id 升序）与单页 100 上限；
- 软删除订单被过滤；
- 无身份（未认证）、无采购订单菜单授权（权限不足）；
- 无效字段 / 无效日期区间 / 无效状态与币种 / 页大小超限（查询前拒绝）；
- 只读不写库（`SaveChangesAsync` 调用次数恒为 0）。

## 8. 验证

- 安全档构建 / 测试：`dotnet build NEWERP.sln -c Release` 与 `dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build`。
