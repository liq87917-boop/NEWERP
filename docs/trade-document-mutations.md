# 单证表头生命周期与明细行变更并发护栏说明（ERP-395）

- 任务：`ERP-395`「Serialize trade document item changes with header lifecycle and deletion」
- 依赖：`ERP-394`（实时授权与权威客户范围护栏）
- 权威实现：
  - `src/ERP.Application/Services/TradeDocumentMutationRules.cs`（共享父单证行锁 + 原子事务 + 表头生命周期判定，新增）
  - `src/ERP.Application/Services/TradeDocumentItemService.cs`（明细行新增 / 修改 / 删除先取父行锁并在锁内复核）
  - `src/ERP.Application/Services/TradeDocumentAuthorizationRules.cs`（`LoadLockedDocumentAsync`：锁内重新加载并复核实时授权口径）
  - `src/ERP.Application/Services/TradeDocumentItemRules.cs`（已知 / 冻结状态集合与可维护状态判定）
  - `src/ERP.Api/Controllers/TradeDocumentController.cs`（表头修改 / 删除 / 批量删除在事务与父行锁内）

## 1. 共享父单证行锁与原子事务协议

| 环节 | 口径 |
| --- | --- |
| 共享行锁 | 明细行新增 / 修改 / 删除与表头状态 / 商业字段修改 / 软删除 / 批量删除都先取**同一把父单证行锁**（`TradeDocumentMutationRules.LockDocumentRowAsync`）；等价 T-SQL 形式为 `SELECT Id FROM db_owner.TradeDocuments WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}`（`LockDocumentRowSql` 常量，契约 / 文档同源） |
| 锁实现 | 仅用 EF Core 基础 API（不依赖关系型扩展，内存库可运行）：在调用方事务内对父单证行发一条「审计时间戳刷新」的 UPDATE，取得排它行锁（X 锁，持有至事务结束），语义与 `UPDLOCK, HOLDLOCK` 一致；`UpdatedAt` 是技术审计字段、不是商业证据，因此不构成对金额 / 状态 / 客户等表头字段的静默改写 |
| 乐观令牌 | 并发方先提交会使本地 `RowVersion` 过期（UPDATE 影响 0 行）→ 重读权威行（含新令牌）后**有界重试**（`LockRetryAttempts = 8`）；行锁语义 = 阻塞后成功，绝不把纯粹锁等待误报成业务拒绝；真实业务冲突仍由锁内校验判定 |
| 原子事务 | `BeginMutationTransactionAsync`：关系型后端开启真实事务，任一步失败整体回滚；调用方已开启事务时**绝不嵌套**，由最外层提交 / 回滚 |
| 非关系型 | 内存库等无行锁语义：锁定与事务等价无操作，声明式校验与状态判定不变（单元测试口径） |

## 2. 确定性锁序与锁内复核

- **子 → 父解析**：明细行修改 / 删除先按 `itemId` **有界读取**解析其父单证 Id（`AsNoTracking`，不改写任何行），再进事务加父行锁；锁内重新加载明细行并复核其仍属于同一父单证。
- **批量删除锁序**：`MergeDocumentLockIds` 去重、仅保留正整数并按 **Id 升序**返回，`LockDocumentRowsAsync` 依序加锁，绝不反向获取其它锁（无锁环）。
- **锁内复核（先锁后读）**：取得父行锁之后**重新加载父单证**（`LoadLockedDocumentAsync`）并复核 —— 存在 / 未删除、权威客户范围（实时身份）；再复核允许的单证类型（商业发票 / 装箱单）、可维护状态、重复行序、服务端数量 / 金额 / 单位 / 币种规则。任一失败即原子拒绝，绝不改库。

## 3. 表头生命周期（状态前进 / 冻结 / 未知 fail closed）

已知状态集合：`待制作` → `已制作` → `已提交客户` → `已使用`（`TradeDocumentItemRules.KnownStatuses`）；冻结状态集合：`已提交客户` / `已使用`（`FrozenStatuses`）。

| 变更 | 口径 |
| --- | --- |
| 新增单证 | 拟议状态归一化（空 → `待制作`）后必须在已知口径内，否则拒绝（`InvalidParameter`，fail closed） |
| 修改单证 | 锁内加载后：持久化状态必须在已知口径内（未知 → 拒绝）；拟议状态必须在已知口径内；状态**只允许前进**（回退 → 拒绝）；**冻结状态**下商业字段（金额 / 币种 / 客户 / 单证号 / 类型 / 日期 / 份数 / 备注等）不得改写（仅允许向 `已使用` 前进的状态变更） |
| 删除 / 批量删除 | 同一事务内加父行锁后复核权威归属；批量删除任一行越界 / 无主 / 缺失即整体拒绝、无部分删除 |
| 未知状态 | 一律 fail closed（拒绝而不放行、不改写任何行） |

## 4. 明细行写入（三式）与快照 / 差异口径

- **新增**：父行锁 → 锁内复核父单证 —— 行序留空由服务端追加 `Max(LineNo)+1`（锁内计算，串行化后不重复）；显式行序重复一律拒绝；行数 / 行序有界；行金额服务端按「数量 × 单价」与币种精度计算。
- **修改 / 删除**：同一套校验；商品引用未变化时**保留已登记快照文本**（绝不静默刷新），显式改指商品资料时才重新取权威快照；软删除保留审计字段。
- **保留既有行为**：不改写商品 / 销售订单 / 采购订单 / 装柜清单 / 库存与流水 / 发票 / 退税 / 费用 / 财务记录；**不自动重算 / 改写表头金额**，既有「行合计 vs 表头金额」差异只作提示。

## 5. 路由矩阵（写路由的锁与事务）

| 路由 | 锁与事务 |
| --- | --- |
| `POST /api/trade/documents` | 拟议客户实时范围 + 真实启用客户校验；未知状态 fail closed（新建无既有父行，无需父行锁） |
| `PUT /api/trade/documents/{id}` | 事务 + 父行锁 → 锁内复核归属 / 状态前进 / 冻结商业字段 → 拟议范围校验 |
| `DELETE /api/trade/documents/{id}` | 事务 + 父行锁 → 锁内复核归属后软删除 |
| `POST /api/trade/documents/batch-delete` | 事务 + 父行 Id 升序确定性加锁 → 锁内逐行范围内复核（整体拒绝） |
| `POST /api/trade/documents/{id}/items` | 事务 + 父行锁 → 锁内复核归属 / 状态 / 类型 / 币种 / 行序后写入行快照 |
| `PUT /api/trade/documents/items/{itemId}` | 有界读取解析父单证 → 事务 + 父行锁 → 锁内复核后修改 |
| `DELETE /api/trade/documents/items/{itemId}` | 有界读取解析父单证 → 事务 + 父行锁 → 锁内复核后软删除 |

## 6. 并发与竞态矩阵（两个独立连接，真实 SQL）

| 竞态 | 确定性不变式 |
| --- | --- |
| 同单并发「同显式行序新增」 | 唯一赢家写入该行序；另一侧按**重复行序**合法冲突拒绝并**整体回滚**（不留第二行） |
| 同单并发「自动行序新增」 | 父行锁串行化后两行都落库且行序唯一（1 / 2），不重复、不丢失 |
| 同单并发「明细行修改 vs 删除」 | 串行化：删除必定成功；若修改先行则最终为「修改后删除」，否则为「未改即删除」，无撕裂状态 |
| 同单并发「表头冻结（已提交客户） vs 明细行新增」 | 冻结只前进状态必成功；明细行**只可能在冻结提交前**写入，冻结提交后的写入一律被拒（`RuleConflict`） |
| 同单并发「表头删除 vs 明细行新增」 | 删除必成功；明细行**只可能在删除提交前**写入，删除提交后的写入一律被拒（`NotFound`） |
| 受限 / 越界 / 撤销 | 越界客户单证的明细行写入 fail closed 且零落库；撤销既有 `doc-center` 菜单后实时授权立即收敛 |
| 失败回滚 | 冲突输家与受限请求不留任何明细行、状态或审计改写 |

## 7. 边界

- 不新增菜单 / 角色 / 用户授权，不新增表 / 列 / 索引，不把空身份当作匿名或管理员，也不伪造任何授权；仍复用既有 `doc-center` 菜单与 ERP-097 权威客户范围。
- 不改写商品资料、销售订单、采购订单、装柜清单、库存与库存流水、发票、退税、费用或财务记录，不做财务 / 库存过账。
- 不改写明细行历史商品快照，不自动重算 / 改写表头金额，保留既有差异提示与库存来源单据审计。
- 行锁只刷新父单证技术审计时间戳 `UpdatedAt`（非商业证据）；被拒绝的请求不改写任何业务字段与历史证据。

## 8. 测试证据

| 层 | 文件 | 覆盖 |
| --- | --- | --- |
| 单元（内存库） | `src/ERP.UnitTests/TradeDocumentMutationTests.cs`（新增） | 状态已知 / 冻结集合与未知状态 fail closed；表头状态前进 / 回退 / 冻结商业字段拒绝；明细行与父单证未知状态 / 删除一致性；批量删除整体拒绝与空集合；锁序合并去重排序；控制器 / 服务接线契约（事务 + 父行锁 + 锁内复核） |
| 单元（回归补充） | `src/ERP.UnitTests/TradeDocumentItemTests.cs` | 明细行写入在父单证冻结 / 删除后一律拒绝且不改写快照；明细行写路径先取父行锁并在锁内复核 |
| 既有回归 | `TradeDocumentLineSnapshotTests.cs` / `TradeDocumentGenerationTests.cs` / `TradeDocumentPrintTests.cs` / `TradeDocumentAuthorizationTests.cs` | 明细行快照 / 生成 / 打印 / 授权口径保持不变 |
| SQL Server 集成（受控 localdb） | `src/ERP.IntegrationTests/TradeDocumentMutationSqlServerTests.cs`（新增） | **两个独立连接竞态**：同显式行序 / 自动行序 / 明细行修改 vs 删除 / 表头冻结 vs 明细行写入 / 表头删除 vs 明细行写入；受限 / 撤销请求不可修改；失败整体回滚；专用目标护栏 fail-closed |
| 安全 profile | `.ai/config.json` 的 `safe` | Release 构建（`TreatWarningsAsErrors`）→ `ERP.UnitTests` 全量 |

### 8.1 SQL Server 集成目标护栏

- 实例必须精确为 `(localdb)\NEWERP_AutoAcceptance`，库名前缀必须为 `NEWERP_AUTOTEST`，且必须为 `Integrated Security`；不满足在任何库访问之前直接拒绝（`AssertDedicatedTarget`）。
- 每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings*.json` / `.env` / 生产凭据。
- 集成夹具只新增自己的证据行，不清理 / 删除任何既有行（保留既有库存来源单据审计与原始失败日志）。

> 构建完成不等于阶段验收：只有受控 localdb 上真实执行通过、或给出准确 blocker，才构成阶段验收证据。真实浏览器验收按任务配置为 `not_required`（未执行，也不声明通过）。
