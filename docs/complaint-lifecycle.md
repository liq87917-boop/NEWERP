# 客诉单生命周期护栏（ERP-388）

客诉单（`FinanceComplaint`，路由 `api/finance/complaints`）的生命周期护栏：把列表 / 详情 / 创建 / 修改 / 提交 /
审核 / 取消 / 删除统一到同一条「实时启用身份 → 既有「客诉单」（`complaint`）菜单授权 → 当前权威客户数据范围 →
（有来源时）来源销售订单行锁 → 客诉单行锁」的可串行化口径上。

## 1. 实时授权（先授权，后计数 / 单号 / 写入）

- 每个路由都重新解析**实时身份**（缺失 / 非正整数 → 未认证 `2000`）、**账号状态**（账号不存在 / 已删除 → 未认证；
  已禁用 → 权限不足 `2002`）、既有「客诉单」（`complaint`）菜单授权（非特权账号必须显式具备）与权威客户数据范围
  （复用 ERP-097 `SalespersonDataScopeService`；未映射业务员的受限账号 fail closed，可见客户为空）。
- 每次请求重新查询（无缓存）：账号停用或授权撤销后**下一次请求立即收敛**。
- 绝不新增菜单 / 角色 / 用户授权，也绝不把空身份当作管理员或匿名放行。

| 路由 | 授权与校验时点 |
|---|---|
| `GET /api/finance/complaints` | 菜单 / 范围先于计数与取行（受限账号看不到范围外客诉单） |
| `GET /api/finance/complaints/{id}` | 按**权威客户**复核范围（越界 fail closed）；消息附带来源链接显式状态 |
| `POST /api/finance/complaints` | 文本长度 → 客户可用性 → 菜单 / 范围 → 来源资格，**全部先于单号生成与写入** |
| `PUT /api/finance/complaints/{id}` | 锁内复核授权 / 范围 / 状态 / 持久化字段 + 请求字段与来源 |
| `POST .../{id}/submit` `.../approve` | 锁内复核授权 / 范围 / 状态 / 持久化文本与来源链接 |
| `POST .../{id}/cancel` `DELETE .../{id}` | 锁内复核授权 / 范围 / 状态（取消幂等、仅待提交可删） |

## 2. 客户权威性

- 创建 / 修改：客诉客户必须**存在、未删除、启用**（不存在 / 已删除 → `1002`，停用 → `1004`），且在当前账号客户数据范围内。
- 读取 / 状态变更 / 删除：复核**库中已存储**客诉单的客户归属仍在范围内；受限账号对无权威归属一律 fail closed。
- 冲突 / 被拒操作**整体回滚**：状态、原始字段与审计时间戳（`UpdatedAt`）保持不变。

## 3. 可空来源销售订单（`SalesOrderId`）

- 历史「未关联来源」（`null`）语义**原样保留**：绝不回填、绝不要求历史客诉补链接，也**不要求**「销售订单」菜单授权。
- 新建 / 变更链接时提供 Id：必须按 Id **精确解析**到既有、未删除、**未取消**、客户一致（同客户）的销售订单，
  并要求当前账号具备既有「销售订单」（`sales-order`）菜单授权（非特权账号）。
  - 不存在 / 已删除（悬空、伪造）→ `1002`；已取消 → `1004`；客户不一致 → `1004`；缺来源菜单 → `2002`。
  - 绝不按订单号文本、金额或相似度猜测来源。
- **已记录的来源**（例如来源订单事后被取消）保持**只读可读**，详情消息给出显式状态：
  未关联 / 已关联 + 订单号 / `来源销售订单已取消（历史链接只读保留，绝不静默重绑定 / 清除）` / `来源不可用`。
  绝不静默重绑定或清除链接。
- 状态流转（提交 / 审核）锁内复核持久化来源：仍可解析且同客户即放行；**已取消的来源不阻断流转**，
  仅已删除 / 悬空或客户不一致才 fail closed。

## 4. 文本长度契约（与实体 `[MaxLength]` 同源）

| 字段 | 上限 |
|---|---|
| `ComplaintNo` 客诉单号 | 50 |
| `ComplaintType` 客诉类型 | 100 |
| `Description` 客诉描述 | 1000 |
| `ResponsibleDept` 责任部门 | 100 |
| `HandleResult` 处理结果 | 1000 |
| `Remark` 备注 | 500 |

超长一律按参数错误 `1001` 拒绝（不静默截断）；创建被拒绝不消耗单号，修改被拒不改写任何字段。

## 5. 原子性与并发（锁序）

- 修改 / 提交 / 审核 / 取消 / 删除都在**同一事务**内先取来源销售订单行锁、再取客诉单行锁；失败整体回滚。
- 锁实现（仅用 EF Core 基础 API，不依赖关系型扩展）：
  - 来源销售订单行锁复用 `PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql`
    （`SELECT Id FROM db_owner.SalesOrders WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}`），多个来源按 **Id 升序** 获取
    （与销售订单取消 / 出库审核 / 预装柜流程共用同一把上游订单行锁）；
  - 客诉单行锁 `FinanceComplaintLifecycleRules.LockComplaintRowAsync`：在调用方事务内对客诉单行发一条
    「审计时间戳刷新」`UPDATE`，取得排它行锁（X 锁，持有至事务结束），语义等价于 `SELECT ... WITH (UPDLOCK, HOLDLOCK)`。
    `UpdatedAt` 是技术审计字段、不是商业字段。
- 非关系型提供程序（内存库）无行锁语义，锁定与事务等价无操作（判断口径 `FinanceComplaintLifecycleRules.IsRelationalProvider`）。
- **两个独立连接的竞态**（`ERP.IntegrationTests/FinanceComplaintLifecycleSqlServerTests.cs`，各自独立 DbContext / 连接 / 事务）：
  1. 并发「修改 vs 提交」经客诉单行锁串行化，最终状态与字段对应成功方，绝不出现丢失更新或半成品；
  2. 并发「来源销售订单取消 vs 新客诉链接」收敛为一致可用来源结果：取消永远成功（客诉不阻断取消），
     新链接要么在取消前成功（历史链接只读保留），要么在取消后按显式状态被拒绝，被拒时不留半成品。

## 6. 边界（不做的事）

- 不写库存 / 库存流水 / 资金 / 会计凭证；不产生销售数量、收款、付款、核销、应收或分摊入账；不改写销售订单。
- **取消来源销售订单绝不因存在客诉而拒绝**（`SalesOrderCancellationRules.ComplaintNonBlockingText`）：
  客诉只是投诉 / 质量反馈追溯，不是库存出运 / 采购履约 / 收款引用 / 定金申请 / 预装柜需求承诺证据；
  取消原样保留客诉历史与其来源链接。
- 取消客诉单只把状态置为「已取消」，**不改动**客户 / 来源 / 文本等原始字段，也不删除任何记录（删除为软删除，仅待提交）。

## 7. 关键文件

- `src/ERP.Application/Services/FinanceComplaintLifecycleRules.cs`（护栏纯判定 / 有界只读 / 行锁 / 来源解析）
- `src/ERP.Application/Services/SalesOrderCancellationRules.cs`（`ComplaintNonBlockingText`：客诉不阻断取消）
- `src/ERP.Api/Controllers/FinanceReceiptComplaintControllers.cs`（`FinanceComplaintController` 真实路由）
- `src/ERP.UnitTests/FinanceComplaintLifecycleTests.cs`（内存库单元测试）
- `src/ERP.IntegrationTests/FinanceComplaintLifecycleSqlServerTests.cs`（`NEWERP_AUTOTEST` SQL Server 集成测试 + 两个独立连接竞态）

## 8. 真实验收口径

- 安全验收：`Release` 构建 + `ERP.UnitTests`（含本护栏 27 个用例）。
- 真实 SQL：目标必须为专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance`、库名前缀 `NEWERP_AUTOTEST` 且
  `Integrated Security`；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在即拒绝，绝不 drop / reset / 复用，
  绝不读取 appsettings / .env / 生产凭据。**构建完成不等于阶段验收**：只有受控 localdb 上真实执行通过才算验收证据。
