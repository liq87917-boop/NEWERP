# 询价单生命周期与转报价单的授权 / 并发护栏说明（ERP-402）

- 任务：`ERP-402`「Make inquiry lifecycle and quotation creation authorized and atomic」
- 权威实现：
  - `src/ERP.Application/Services/InquiryAuthorizationRules.cs`（实时身份 / 既有 inquiry 菜单 / 权威客户范围 / 转报价单另需既有 quotation 菜单）
  - `src/ERP.Application/Services/InquiryMutationRules.cs`（确定性询价单来源行锁 + 原子事务 + 锁内权威复核 + 生命周期 / 下游冻结 / 转换资格守卫）
  - `src/ERP.Api/Controllers/InquiryController.cs`（列表 / 详情 / 新增 / 修改 / 提交 / 审核 / 取消 / 删除 / 批量删除 / 导出 / 明细带入 / 转报价单全部落在同一授权与锁协议内）
  - `src/ERP.Api/Controllers/InquiryQuotationConversion.cs`（`BuildDraftAsync(db, inquiry)`：按锁内权威重读的询价单映射报价单草稿；`BuildDraftAsync(db, id)`：只读资格 + 映射）
  - `src/ERP.Application/Services/QuotationMutationRules.cs`（`InquiryRowLockSql` / `InquiryToQuotationLockOrderText`：与报价单锁共享同一锁序常量）
- 继承关系：ERP-398 / ERP-399 / ERP-400 / ERP-401 已把「实时身份 / 既有菜单 / 权威客户范围 + 行锁」推到 PI、报价单与销售订单；
  ERP-402 把同一口径推广到**询价单**（业务链最上游），并把「生命周期变更 / 批量删除 / 转报价单」串行化为不可撕裂的业务操作。

## 1. 授权与客户范围（读取 / 写入 / 计数之前）

| 项 | 口径 |
| --- | --- |
| 身份 | 每次请求按 `ClaimTypes.NameIdentifier` 解析：缺失 / 非正整数 → `2000` 未认证；账号不存在或已 `IsDeleted` → `2000`；`Status != Enabled` → `2002` 权限不足 |
| 菜单 | 复用既有「询价单」菜单 `inquiry`（`InquiryAuthorizationRules.RequiredMenuCode`）；**非特权账号**必须显式具备且已映射业务员（`BaseEmployee.EmployeeCode == SysUser.UserName` 且 `IsSalesman`），撤销后下一次请求立即收敛；**特权账号**（超级管理员 / 系统内置角色 / 显式特权角色，与 ERP-097 同源）继承既有全部访问 |
| 转换目标菜单 | 转报价单 / 明细带入额外要求既有 `quotation`（ERP-400 同源）。**询价单可见绝不等于报价单授权**，也绝不新增任何用户授权 |
| 权威归属 | 只按持久化 `Inquiry.CustomerId` 判定，绝不按 `InquiryNo` / 联系人等自由文本推断 |
| 列表 / 计数 / 导出 | `ApplyScope`（`ScopeFilter`）在 `Count` 与分页、以及导出读取**之前**把范围下推到 SQL，绝不「先查全量再内存过滤」 |
| 写入校验 | 新增 / 修改在改写任何字段**之前**校验拟议客户；修改同时复核**已存客户**与**拟议客户**两侧 |
| 身份来源唯一 | 请求体任何字段都不能指定或扩大账号身份；`null` 范围只表示「未经过 MVC 授权管线的进程内调用」，绝不代表匿名或管理员 |

## 2. 锁协议

| 项 | 口径 |
| --- | --- |
| 唯一来源锁 | 询价单来源行锁：`SELECT Id FROM db_owner.Inquiries WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}`（`InquiryMutationRules.InquiryRowLockSql`，契约 / 文档同源） |
| 锁实现 | 仅用 EF Core 基础 API：在调用方事务内对询价单行发一条「审计时间戳刷新」`UPDATE`（`UpdatedAt` 是技术审计字段、不是商业证据），取得排它 X 锁，持有至事务结束；语义等价 `UPDLOCK, HOLDLOCK`。并发方先提交使本地乐观令牌 `RowVersion` 过期时，重读权威行后**有界重试**（`RowLockRetryAttempts`，与 ERP-400 / ERP-399 同源） |
| 目标单据 | 转报价单在询价单行锁内**只读复核**既有来源报价单（持久化 `Quotation.InquiryId`），随后 `INSERT` 全新报价单 —— 目标没有既有行可加锁，重复生成由「来源行锁 + 锁内重读」唯一化 |
| 跨单据锁序 | 恒定「**询价单行锁 → 报价单行锁 → PI 行锁 / 销售订单行锁**」；`QuotationMutationRules.InquiryRowLockSql` 引用同一常量，写路径绝不反向获取上游锁，因此不存在锁环（`QuotationMutationRules.InquiryToQuotationLockOrderText`） |
| 批量删除 | 全部询价单行按 **Id 升序**确定性加锁（`MergeLockIds` 去重、仅正整数），与单行生命周期路由共用同一把锁 |
| 原子事务 | `BeginMutationTransactionAsync`：关系型后端开启真实事务，任一步失败整体回滚；调用方已开启事务时**绝不嵌套**；内存库等非关系型提供程序等价无事务（声明式校验不变） |
| 事务内回滚证据 | 行锁的 `UpdatedAt` 刷新、状态流转、字段改写、明细整体替换、已预约的报价单编号与新建报价单都在同一事务内；失败时全部回滚（集成测试断言 `UpdatedAt` 与状态零变化） |

## 3. 锁内权威复核

取得询价单行锁之后，控制器**重新加载**询价单（绝不复用加锁前的内存实体），并逐项复核：

1. **持久化生命周期状态**（`Status`）；并发方已提交的结果以锁内重读为准，绝不按陈旧状态放行；
2. **权威归属与实时授权**：实时身份 + 既有 `inquiry` 菜单 + 权威客户范围（ERP-097），转换额外要求既有 `quotation` 菜单；
3. **实时下游存在性**：`FindLiveQuotationAsync` 只按持久化 `Quotation.InquiryId` 与 `!IsDeleted` 判定「是否存在实时报价单下游」；
4. **`RowVersion`**：乐观令牌不一致 / 并发改写时以 `DbUpdateConcurrencyException` 收尾并转为可读的业务冲突（`StaleRowVersionText`），绝不覆盖赢家；
5. **有效明细**：`ActiveDetailCount`（未删除明细）用于转换资格；
6. **既有转换资格**：锁内重读的询价单 + 锁内查得的既有来源报价单共同判定（重复生成守卫），并以锁内权威询价单重新构造报价单草稿。

## 4. 生命周期与守卫矩阵（锁内判定）

| 路由 | 额外守卫（锁内） | 备注 |
| --- | --- | --- |
| `GET /` `GET /{id}` `GET /export` | 实时授权 + 权威范围下推 / 可见复核 | 计数、分页与导出在范围之后 |
| `POST /` | 实时授权 + **拟议客户范围**先于单号生成 | 被拒绝的调用方绝不消耗单据号 |
| `PUT /{id}` | 已存 + 拟议归属；无实时下游；仅待提交可改 | 明细整体替换，金额 `Σ 数量 × 单价` 按既有服务端口径计算 |
| `POST /{id}/submit` | 无实时下游；待提交 → 已提交 | 与既有状态机同口径 |
| `POST /{id}/approve` | 无实时下游；已提交 → 已审核 | 与既有状态机同口径（草稿不可直接审核） |
| `POST /{id}/cancel` | 无实时下游 | 既有「不限状态可取消」保留；已转出的来源一律冻结 |
| `DELETE /{id}` | 无实时下游；仅待提交可删 | 软删除 |
| `POST /batch-delete` | 全部询价单 Id 升序加锁；逐行归属 / 下游 / 状态复核 | 混合允许 / 越界 / 非待提交**整体拒绝**，不做部分删除 |
| `GET /{id}/quotation-prefill` | 实时授权（inquiry + quotation）+ 归属 + 既有转换资格 | **只读**：不加锁、不开事务、不占号、不写库 |
| `POST /{id}/to-quotation` | 实时授权（inquiry + quotation）+ 锁内重读 + 既有转换资格 | 重复检测 / 单号生成 / 草稿构造全部在询价单行锁内 |

**已转换来源一律冻结**（`DownstreamLinkedText`）：修改 / 提交 / 审核 / 取消 / 删除 / 批量删除在存在实时报价单下游时全部拒绝，
保留显式历史；**绝不**用「取消报价单」的反向自动冲销伪造一致性；**无下游链接时保留既有允许的全部流转**（不新增状态机限制）。

## 5. 转换一致性与预填

- 转换资格顺序（与既有 `BuildDraftAsync` 守卫同口径）：已存在实时来源报价单（重复生成，提示「不能重复转换」）→ 已完成（终态）→ 未审核（提示「仅已审核的询价单可转为报价单」）→ 无有效明细。任一不满足即原子拒绝，不消耗单号、不写目标、不改来源状态。
- 报价单草稿逐项映射来源权威口径：来源 `InquiryId` / `InquiryNo`、客户与联系人（空值回退客户主数据）、贸易术语 / 目的港 / 付款方式、币种、汇率（`<= 0` 回退 1）、有效期（`ValidDays`）、业务员、明细（`SortNo` / 商品 / 规格 / 单位 / 数量 / 单价 / `Amount = Round(数量 × 单价, 2)` / 备注）与合计（`TotalAmount` / `TotalAmountCny`）。
- **带入预填保持只读**；最终保存以 `POST /{id}/to-quotation` 为权威，同一询价单至多一张完整报价单。
- 不产生任何财务 / 库存过账：转换只新增报价单与明细（集成测试断言库存出库单与收款单计数不变）。

## 6. 边界

- 不新增菜单 / 角色 / 用户授权 / 表 / 列 / 索引；不把空身份当作匿名或管理员，不伪造任何授权（全部复用既有 ERP 权限模型）。
- 不改写询价单编号 / 明细行 / 数量 / 单价 / 金额 / 币种 / 汇率 / 单位等原始商业语义；行锁只刷新技术审计时间戳 `UpdatedAt`。
- 不改写报价单 / PI / 销售订单、客户主数据、库存与库存流水、发票、费用或财务记录。
- 保留库存来源单据审计与原始失败日志：被拒绝 / 回滚的请求只回滚自己的写入，不清理 / 不删除任何既有行。
- 失败一律 fail closed：资格不符、范围越界、单号生成失败、写入失败都整体回滚，绝不留下半成品目标、孤儿明细或已占号。

## 7. 测试证据

| 层 | 文件 | 覆盖 |
| --- | --- | --- |
| 单元（内存库，新增） | `src/ERP.UnitTests/InquiryMutationTests.cs` | 锁 / 事务契约（`InquiryRowLockSql`、与报价单锁同源、`MergeLockIds` Id 升序、非关系型等价无操作）；生命周期守卫逐条；拒绝矩阵（无身份 / 缺 inquiry 菜单 / 未映射业务员 / 停用 / 越界 / 拟议越界 / 混合批量）；受限账号列表-详情-导出 fail closed；转换另需 quotation 菜单授权；零写入 / 不占号证据；转换资格与唯一化；已转换来源冻结且零改写；预填只读；控制器接线契约 |
| 单元（既有回归，更新） | `src/ERP.UnitTests/InquiryQuotationConversionTests.cs` | ERP-024 既有「已审核询价单 → 报价单」映射 / 合计 / 明细、非法来源状态、重复转换、预填只读与前端接线口径保持通过；补实时身份与来源冻结回归 |
| SQL Server 集成（受控 localdb，新增） | `src/ERP.IntegrationTests/InquiryMutationSqlServerTests.cs` | 真实既有授权（无身份 / 缺菜单 / 撤销 / 停用 / 越界 / 混合批量拒绝，双菜单本人放行且数量-单价-金额-币种-汇率-单位快照一致、无过账）；**两个独立连接竞态**：转报价单 / 转报价单、取消 / 转报价单、编辑 / 审核、混合批量删除 —— 恰好一个合法赢家且失败侧整体回滚；编号生成失败整体回滚（不落半成品、不占号、来源状态与审计零变化）；专用目标护栏 fail-closed |
| 安全 profile | `.ai/config.json` 的 `safe` | `dotnet restore` → Release 构建 → `ERP.UnitTests` 全量 |

### 7.1 SQL Server 集成目标护栏

- 实例必须精确为 `(localdb)\NEWERP_AutoAcceptance`，库名前缀必须为 `NEWERP_AUTOTEST`，且必须为 `Integrated Security`；不满足在任何库访问之前直接拒绝（`AssertDedicatedTarget`）。
- 每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings*.json` / `.env` / 生产凭据。

> 构建完成不等于阶段验收：只有受控 localdb 上真实执行通过、或给出准确 blocker，才构成阶段验收证据。
> 真实浏览器验收按任务配置为 `not_required`（未执行，也不声明通过）。

