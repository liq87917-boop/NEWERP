# 形式发票 PI 生命周期与销售订单转换的并发护栏说明（ERP-399）

- 任务：`ERP-399`「Serialize PI lifecycle and sales order conversion as one business operation」
- 权威实现：
  - `src/ERP.Application/Services/ProformaInvoiceMutationRules.cs`（确定性 PI 来源行锁 + 原子事务 + 锁内权威复核 + 生命周期 / 转换资格守卫）
  - `src/ERP.Api/Controllers/ProformaInvoiceController.cs`（修改 / 提交 / 审核 / 销审 / 取消 / 作废 / 删除 / 批量删除 / 转销售订单全部落在同一锁协议内）
  - `src/ERP.Api/Controllers/SalesOrderConversion.cs`（`EnsureDraftMatchesSource`：转换结果与来源权威口径逐项一致）
  - `src/ERP.Application/Services/ProformaInvoiceAuthorizationRules.cs`（ERP-398 实时授权与客户范围，锁内复用）
- 继承关系：ERP-398 已把「实时身份 / 既有菜单 / 权威客户范围」推到每个 PI 路由；ERP-399 在此基础上把
  **写操作串行化**，使「生命周期变更」与「PI → 销售订单转换」成为一个不可撕裂的业务操作。

## 1. 锁协议

| 项 | 口径 |
| --- | --- |
| 唯一来源锁 | PI 来源行锁：`SELECT Id FROM db_owner.ProformaInvoices WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}`（`ProformaInvoiceMutationRules.PiRowLockSql`，契约 / 文档同源） |
| 锁实现 | 仅用 EF Core 基础 API：在调用方事务内对 PI 行发一条「审计时间戳刷新」`UPDATE`（`UpdatedAt` 是技术审计字段、不是商业证据），取得排它 X 锁，持有至事务结束；语义等价 `UPDLOCK, HOLDLOCK`。并发方先提交使本地乐观令牌 `RowVersion` 过期时，重读权威行后**有界重试**（`RowLockRetryAttempts = 8`），绝不把纯粹锁等待误报成业务拒绝 |
| 目标订单 | 转换在 PI 行锁内**只读复核**既有来源销售订单（持久化 `SourcePiId`），随后 `INSERT` 全新订单 —— 目标订单没有既有行可加锁，重复生成由「来源行锁 + 锁内重读」唯一化；共享 `PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql` 常量保证与销售订单取消 / 出库审核 / 单证生成同一把来源订单锁，且**绝不反向获取下游锁** |
| 锁序 | 单行路由：PI 行 → （转换时）新建销售订单行 `INSERT`。批量删除：全部 PI 行按 **Id 升序**确定性加锁（去重、仅正整数）。不存在锁环 |
| 原子事务 | `BeginMutationTransactionAsync`：关系型后端开启真实事务，任一步失败整体回滚；调用方已开启事务时**绝不嵌套**；内存库等非关系型提供程序等价无事务（声明式校验不变） |
| 事务内回滚证据 | 行锁的 `UpdatedAt` 刷新、状态流转、字段改写、明细整体替换、已预约的销售订单编号与新订单都在同一事务内；失败时全部回滚（集成测试断言 `UpdatedAt` 与状态零变化） |

## 2. 锁内权威复核

取得 PI 行锁之后，控制器**重新加载** PI（绝不复用加锁前的内存实体），并逐项复核：

1. **持久化生命周期状态**（`Status`）；并发方已提交的结果以锁内重读为准，绝不按陈旧状态放行；
2. **`RowVersion`**：乐观令牌不一致 / 并发改写时以 `DbUpdateConcurrencyException` 收尾并转为可读的业务冲突（`StaleRowVersionText`），绝不覆盖赢家；
3. **实时授权**：实时身份 + 既有「形式发票 PI」（`proforma-invoice`）菜单 + 权威客户范围（ERP-097），转换额外要求既有「销售订单」（`sales-order`）菜单与**来源 / 目标客户独立复核**；
4. **有效明细**：`ActiveDetailCount`（未删除明细）用于审核与转换资格；
5. **既有转换资格**：按持久化 `SourcePiId` 复核是否已存在未删除销售订单（重复生成守卫），并以锁内重读的 PI 重新构造订单草稿。

## 3. 生命周期守卫矩阵（锁内判定）

| 路由 | 额外守卫（锁内） | 备注 |
| --- | --- | --- |
| `PUT /{id}` | 已存归属 + **拟议归属**范围；无下游链接；仅待提交 / 已提交可改 | 明细整体替换，金额 / 合计 / 定金按服务端口径重算 |
| `POST /{id}/submit` | 无下游链接；仅待提交 | 保留既有口径 |
| `POST /{id}/approve` | 无下游链接；已作废 / 已完成 / 已审核拒绝；**无有效明细拒绝** | 保留既有口径 |
| `POST /{id}/unaudit` | 无下游链接；仅已审核可销审 | 重新打开 |
| `POST /{id}/cancel` | 无下游链接 | 既有允许口径保持不变（无状态限制） |
| `POST /{id}/void` | 无下游链接；已作废 / 已转销售订单拒绝 | 保留既有口径 |
| `DELETE /{id}` | 无下游链接；仅待提交可删 | 软删除 |
| `POST /batch-delete` | 按 Id 升序加锁；逐行范围 / 链接 / 状态复核；**任一行不满足即整体拒绝、零部分删除** | 混合允许 / 越界 / 无主 / 已链接批次整体拒绝 |
| `POST /{id}/to-order` | 锁内转换资格（已作废 / 重复生成 / 已完成 / 未审核 / 无有效明细）；来源 + 目标范围；`EnsureDraftMatchesSource` | 只新增订单、绝不覆盖既有订单 |

**已完成且存在下游链接的 PI 一律冻结**（`DownstreamLinkedText`）：修改 / 提交 / 审核 / 销审 / 取消 / 作废 / 删除 / 批量删除全部拒绝，
保留显式历史；**绝不**用「取消目标订单」的反向冲销伪造一致性；**无下游链接时保留既有允许的全部流转**（不新增状态机限制）。

## 4. 转换一致性与预填

- 转换资格顺序：已作废 → 已存在来源订单（重复生成，提示既有单号）→ 已完成 → 未审核 → 无有效明细；任一不满足即原子拒绝，不消耗单号、不写订单、不改来源状态。
- `SalesOrderConversion.EnsureDraftMatchesSource` 逐项复核落库订单与来源权威口径：显式 `SourcePiId` / 来源 PI 号、币种、汇率、明细数量合计、合计金额（`Σ 数量 × 单价`）与定金（`总额 × 定金比例 %`）完全一致。
- **带入预填保持只读**（`GET /{id}/order-prefill`：不加锁、不开事务、不占号、不写库）；最终保存以销售订单新增路由为权威，PI 侧一致性由 `to-order` 的来源行锁协议保证。
- 不产生任何财务 / 库存过账：转换只新增销售订单与明细（集成测试断言库存出库单与收款单计数不变）。

## 5. 边界

- 不新增菜单 / 角色 / 用户授权 / 表 / 列 / 索引；不把空身份当作匿名或管理员，不伪造任何授权（全部复用既有 ERP 权限模型）。
- 不改写 PI 编号 / 金额 / 合计 / 定金 / 币种 / 汇率 / 单位等原始商业语义；行锁只刷新技术审计时间戳 `UpdatedAt`。
- 不改写来源报价单、销售订单（既不覆盖也不冲销）、客户主数据、库存与库存流水、发票、退税、费用或财务记录。
- 保留库存来源单据审计与原始失败日志：被拒绝 / 回滚的请求只回滚自己的写入，不清理 / 不删除任何既有行。
- 失败一律 fail closed：资格不符、范围越界、单号生成失败、写入失败都整体回滚，绝不留下半成品订单、孤儿明细或已占号。

## 6. 测试证据

| 层 | 文件 | 覆盖 |
| --- | --- | --- |
| 单元（内存库，新增） | `src/ERP.UnitTests/ProformaInvoiceMutationTests.cs` | 锁 / 事务契约（`PiRowLockSql`、共享来源订单行锁常量、Id 升序加锁、非关系型等价无操作）；生命周期守卫逐条；下游链接冻结与「无链接保留既有流转」；`RowVersion` 复核；转换资格；`EnsureDraftMatchesSource` 逐项篡改即拒绝；控制器锁内护栏（已链接 PI 的取消 / 作废 / 销审 / 编辑 / 提交 / 审核 / 删除 / 批量删除全部冻结且零改写）；合法转换只生成一张完整订单且口径一致；预填只读；控制器接线契约 |
| 单元（既有回归） | `ProformaInvoiceControllerTests.cs`（+2 例）/ `SalesOrderConversionTests.cs`（+1 例）/ `ProformaInvoiceAuthorizationTests.cs` | 既有创建 / 详情 / 分页 / 修改 / 流转 / 打印与转换映射、金额复核、重复守卫、ERP-398 授权口径保持通过；新增「已链接 PI 取消冻结 / 无链接仍放行」与「转换草稿权威一致性」回归 |
| SQL Server 集成（受控 localdb，新增） | `src/ERP.IntegrationTests/ProformaInvoiceMutationSqlServerTests.cs` | 真实既有授权（无身份 / 缺菜单 / 越界拒绝，双菜单本人放行且口径一致、无过账）；**两个独立连接竞态**：转换 / 转换、转换 / 作废、转换 / 销审、编辑 / 审核、混合批量删除 —— 恰好一个合法赢家且失败侧整体回滚；编号生成失败整体回滚（不落半成品订单、不占号、来源状态与审计零变化）；专用目标护栏 fail-closed |
| 安全 profile | `.ai/config.json` 的 `safe` | `dotnet restore` → Release 构建 → `ERP.UnitTests` 全量 |

### 6.1 SQL Server 集成目标护栏

- 实例必须精确为 `(localdb)\NEWERP_AutoAcceptance`，库名前缀必须为 `NEWERP_AUTOTEST`，且必须为 `Integrated Security`；不满足在任何库访问之前直接拒绝（`AssertDedicatedTarget`）。
- 每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings*.json` / `.env` / 生产凭据。

> 构建完成不等于阶段验收：只有受控 localdb 上真实执行通过、或给出准确 blocker，才构成阶段验收证据。
> 真实浏览器验收按任务配置为 `not_required`（未执行，也不声明通过）。
