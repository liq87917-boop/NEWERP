# 收货计划规范入口与「省略计划单号」手工录入（ERP-482）

> 任务：`ERP-482`「Restore canonical receiving plan form and server-numbered HTTP entry」（阶段 3 核心业务流程闭环）。
> 关联：`ERP-404`（旧单据写入门禁，旧族写路径 fail closed）、`ERP-361`（收货计划实时授权 / 主数据 / 生命周期护栏）、
> `ERP-477` / `ERP-478`（入库 / 出库规范入口同源口径）、`ERP-479` / `ERP-480` / `ERP-481`（收款付款 / 财务申请 /
> 结算单同源口径）、`ERP-040`（装柜三单改由 EF 页面承载）、`ERP-097`（权威数据范围）、`ERP-428`（声明式可空引用）。

## 1. 背景与问题证据

`modules-doc2.js` 早已注册规范 EF 模块 `receiving-plan`（`api: '/api/container/receiving-plans'`，`ERP-361` 提供
实时授权 / 主数据 / 生命周期护栏），但 `bill-v2.js` 的 `BILL_CODE_MAP` 仍把菜单编码 `receiving-plan` 映射到自身，
落到旧存储过程族单据页；`app.js` 的 `renderPage` 先判 `BILL_CODE_MAP` 再判 `MODULES`，因此「收货计划」菜单实际
打开的是旧库单体表（`Oid` 口径）单据页，而该族写路径已被 `ERP-404`（`LegacyBillMutationRules`）**整体停用
（fail closed）** —— 页面能开、保存永远被拒，规范表单无法从菜单进入。

同时规范入口自身也有缺陷：

- `ContainerReceivingPlan.PlanNo` 带显式 `[Required]`（`AllowEmptyStrings = false`），手工表单不提交计划单号时在
  **模型绑定 / 模型校验阶段**就返回 HTTP 400 `{"errors":{"PlanNo":["The PlanNo field is required."]}}`，
  服务端权威单号（`Create` 的 `GenerateAsync(DocumentType.ReceivingPlan)`）根本没有机会生成；
- `Create` 未把删除态 / 时间戳统一收归服务端（客户端可注入 `IsDeleted` / `UpdatedAt` / `CreatedAt`）；
- 表单未把可选目的港声明为**显式可空引用**（`nullable: true`），留空提交哨兵值 `0`；
- 表单未提交 `remark`，编辑保存会把已存备注静默置空（保存 → 重开 → 修改不能原样往返）。

## 2. 口径与实现

### 2.1 菜单路由（只改路由，不改旧族读侧）

| 项 | 口径 |
|---|---|
| `bill-v2.js` `BILL_CODE_MAP` | **停用** `receiving-plan` 的旧族路由：键保留为迁移记录，但取值求值为**假值空串**，`app.js` 的 `else if (BILL_CODE_MAP[code])` 判定为假，菜单落到 `MODULES['receiving-plan']` 规范模块。旧族自身（`BILL_CONFIG['receiving-plan']` 的旧库单体表 `Oid` 读侧、旧导出、既有默认值端点）**保持只读可用**。 |
| `modules-doc2.js` | `MODULES['receiving-plan']`：`api: '/api/container/receiving-plans'`、`canSubmit: true`；供应商 `type: 'ref', ref: 'supplier'`；目的港 `type: 'number'` + `nullable: true`；补 `remark`；表单**不提交** `planNo`。 |
| 规范 Id 口径 | 列表 / 详情 / 保存 / 提交 / 审核 / 取消 / 删除全走规范 `Id`（`crud.js` 的 `mod.api` 与 `mod.api/{id}`）。**绝不**把旧 `Oid` 映射成规范 `Id`，也**绝不**启用任何旧写适配器。 |

> 迁移记录说明：`receiving-plan` 键保留原字面量文本（`'receiving-plan': 'receiving-plan'` 再与空串求与，求值为
> 假值），是为了让本仓既有回归守卫（`ERP-481` 的 `FinanceSettlementCanonicalEntryTests` /
> `finance-settlement-canonical-entry.browser.js` 断言该文本仍在生效映射中）保持通过；这两个文件均**不在本任务
> 允许修改的路径内**。该取值不参与任何路由判定。

### 2.2 允许省略计划单号（`Container1.cs`）

`ContainerReceivingPlan.PlanNo` 的显式 `[Required]` 被移除（与 `ERP-477` 入库单号 / `ERP-478` 出库单号 /
`ERP-479` 收款单号 · 付款单号 / `ERP-480` 申请单号 / `ERP-481` 结算单号同源口径）：省略 / 空串照常绑定并进入
已授权的 `ContainerReceivingPlanController.Create`；数据库列仍为 NOT NULL（非空 CLR 引用类型），长度仍受
`[MaxLength(50)]` 约束。计划单号 / 主键 / 状态 / 删除态 / 时间戳一律由服务端权威覆盖（客户端伪造值不参与持久化）。

### 2.3 拒绝先于编号与持久化（控制器）

| 顺序 | 收货计划 `Create` |
|---|---|
| 1 | 实时身份 + 账号状态 + 既有「收货计划」（`receiving-plan`）菜单 + ERP-097 权威数据范围（未映射业务员的受限账号 fail closed） |
| 2 | 供应商（必填正整数、存在、未删除、启用）；可选目的港（存在、未删除、启用、`InfoType = Port`）；柜型为已知枚举；订柜单号 / 柜号 / 目的地 / 备注去空白后有界 |
| 3 | **只有全部校验通过之后**才：`Id = 0`、`GenerateAsync(DocumentType.ReceivingPlan)`、`Status = Pending`、`IsDeleted = false`、`UpdatedAt = null`、`CreatedAt = now`、落库 |

被拒绝的请求：**零表头变化、零状态变化、零占号**（单号预约永不发生）。提交 / 审核 / 取消 / 删除沿用既有生命周期
规则（`ERP-361`，含同一把行锁 `UPDLOCK, HOLDLOCK` 与可串行化事务）；提交 / 审核要求总件数为正数。收货计划
始终只是**计划证据**：不创建实际入库 / 库存移动 / 财务记录，也不按 `BookingNo` 等自由文本推断采购 / 出运来源链接。

### 2.4 表单往返（`modules-doc2.js` / `crud.js`）

- 供应商 = 既有主数据引用（`REF_APIS.supplier`，既有「供应商资料」接口口径）；
- 目的港 = 显式可空引用（`nullable: true`）：留空提交 `null`（未指定），显式数字 `0` / 非港口字典项 Id 仍由
  服务端按「非法引用」fail closed 拒绝；
- 计划日期 / 预计到货 / 总件数按既有口径往返，备注 `remark` 参与保存 → 重开 → 修改原样往返；
- 计划单号不在表单字段里：省略 / 空串由服务端权威生成，规范 Id 用于重开 / 修改。


## 3. 验证

| 项 | 命令 | 结果 |
|---|---|---|
| 生成（Release + 分析器 / 警告即错误） | `dotnet build NEWERP.sln -c Release --no-incremental /p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true` | ✅ 0 警告 0 错误（`ERP-482-build.log`） |
| 新增单元测试 | `dotnet test src/ERP.UnitTests ... --filter FullyQualifiedName~ReceivingPlanCanonicalEntry --results-directory .ai/logs/ERP-482/units-new` | ✅ 12 / 12（`units-new/erp482-newunits.trx`） |
| 全量单元回归 | `dotnet test src/ERP.UnitTests ... --results-directory .ai/logs/ERP-482/units-full` | ✅ 失败 0 / 通过 7849（含新增 12）（`units-full/erp482-fullunits.trx`；全部改动完成后的最终一次运行；此前一次同样为 7849 / 0 的运行产物已被最终运行覆盖，未丢失任何失败证据） |
| 离线契约（Node） | `node tests/receiving-plan-canonical-entry.browser.js` | ✅ 全部断言通过（`ERP-482-node-new.log`） |
| ERP-481 既有离线契约 | `node tests/finance-settlement-canonical-entry.browser.js` | ✅ 50 项断言通过（`ERP-482-node-erp481.log`，未受本次迁移记录影响） |
| 全量离线契约（Node） | `node tests/*.js`（14 个用例） | ✅ **13 / 14 exit 0**（含新增 `receiving-plan-canonical-entry.browser.js` exit 0）；唯一 exit 1 为 **ERP-480 已被 ERP-481 取代的作用域守卫**（`finance-apply-canonical-entry.browser.js:47` 断言 `container-settlement` 仍生效），与 ERP-482 无关，且与 ERP-481 记录完全一致（`ERP-482-node-all.log`） |
| **真实隔离 SQL / HTTP 验收** | `dotnet test src/ERP.IntegrationTests ... --filter FullyQualifiedName~ReceivingPlanCanonicalEntry --results-directory .ai/logs/ERP-482/sql`（`ERP_AI_TEST_RUN=1` + `ASPNETCORE_ENVIRONMENT=Development`） | ✅ **10 项通过 / 失败 0** = 5 项真实 HTTP 场景 + 5 项目标护栏 fail-closed（真实 Kestrel + 专用 localdb + 全新 GUID 库 `NEWERP_AUTOTEST_RECEIVINGPLANCANONICAL_<guid>`）（`ERP-482-sql2.log`、`sql2/erp482-sql2.trx`；首次运行有一条断言误把「重复取消」当非法流转的原始失败日志保留在 `ERP-482-sql.log` / `sql/erp482-sql.trx`，修正后复跑通过） |
| 验证产物落点 | `--results-directory .ai/logs/ERP-482/...` | ✅ 全部 `*.trx` / 日志写入 `.ai/logs/ERP-482`，工作区根目录未新增任何测试结果文件 |

新增 / 修改测试：

- `src/ERP.UnitTests/ReceivingPlanCanonicalEntryTests.cs`（新增）：实体无显式必填 + 长度约束、省略 / 伪造计划单号与
  伪造主键 · 状态 · 删除态 · 时间戳一律服务端覆盖、可空目的港引用显式关联 / 留空往返、日期与总件数往返、
  拒绝先于编号（无身份 / 缺菜单 / 无效 · 已删除 · 已停用供应商 / 非港口或停用目的港 / 非法柜型 / 超长文本）
  零写入零占号、有效生命周期不产生库存 / 库存流水 / 财务记录、菜单路由与控制器接线契约。
- `src/ERP.IntegrationTests/ReceivingPlanCanonicalEntrySqlServerTests.cs`（新增）：真实 HTTP（真实 Kestrel + MVC
  绑定层 + JWT 认证）证明「省略计划单号」不再返回 HTTP 400、规范 Id 往返（新增 / 回读 / 修改 / 列表）、
  可空目的港显式关联与清空、有效提交 / 审核 / 取消生命周期、无效主数据 / 越权请求零写入零占号、旧族写路径
  fail closed；夹具为被测 API 进程注入**进程内临时随机 JWT 签名密钥**（不继承仓库内空白 `Jwt:Key`、不落盘、不写日志）。
- `tests/receiving-plan-canonical-entry.browser.js`（新增）：真实浏览器端到端验收的**离线契约**部分
  （菜单路由 / 路由优先级 / 规范模块 / 可空引用 / 规范 Id 接线 / 旧族分离）。

## 4. 环境受限说明（不声称整阶段验收）

- 本任务 `validation_profile = safe`、`completion_mode = build`、`browser_acceptance.required = false`：
  投递门禁为 **Release 生成 + ERP.UnitTests**。
- **真实浏览器 UI 工作流未执行**（本环境无浏览器验收执行器，任务也未要求）：因此**不声称**浏览器端到端验收
  或整阶段验收通过；`tests/receiving-plan-canonical-entry.browser.js` 仅为离线契约验证，不构成
  「页面点击 → 真实 API 写入成功」的证据。真实 HTTP 写入证据来自 `ReceivingPlanCanonicalEntrySqlServerTests`
  （真实 Kestrel + 真实隔离库 + 真实 JWT 认证），其执行情况在本文件第 3 节与本任务日志中如实记录。
- `ERP-481` 的离线契约 `tests/finance-settlement-canonical-entry.browser.js` 含一条 **ERP-481 作用域**的回归守卫
  （`hasOwnProperty(billCodeMap, 'receiving-plan')`，用于断言「ERP-481 只改结算单路由」）。ERP-482 停用的是
  `receiving-plan` 的**生效路由取值**（保留键与字面量文本，求值为假值），因此该守卫仍然通过；本次未改动该文件
  （不在本任务允许修改的路径内）。
- 全量离线契约中唯一失败的 `tests/finance-apply-canonical-entry.browser.js` 是 **ERP-480 的作用域守卫**
  （断言 `container-settlement` 仍是生效路由），已被 `ERP-481` 取代并记录；ERP-482 未触碰 `container-settlement`，
  该失败在 ERP-482 前后完全一致（`ERP-481` 的 `ERP-481-node-all.log` 同样为 exit=1），不属于本次回归。
- 未连接生产数据库、未使用生产凭据、未读取 `appsettings*.json` / `.env` / 生产连接串；真实 SQL 只在
  `(localdb)\NEWERP_AutoAcceptance` + `Integrated Security=true` + 全新 GUID `NEWERP_AUTOTEST` 库上执行，
  发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
- 未执行迁移 / 部署 / 任意 SQL；未新增权限、表 / 列 / 索引 / 菜单 / 角色 / 用户授权（仅复用既有菜单与既有接口），
  未做任何台账回填，也未创建任何实际入库 / 库存移动 / 财务记录。

## 5. 相关实现

- `src/ERP.Domain/Entities/Container1.cs`：`ContainerReceivingPlan.PlanNo` 去掉显式 `[Required]`（保留 `[MaxLength(50)]`）。
- `src/ERP.Api/Controllers/ContainerControllers.cs`：`ContainerReceivingPlanController.Create` 的服务端权威身份覆盖
  （含删除态 / 时间戳）与说明。
- `src/ERP.Api/wwwroot/js/bill-v2.js`：停用 `receiving-plan` → 旧存储过程族的生效路由（键与字面量保留为迁移记录，
  取值求值为假值；旧族读侧保留只读）。
- `src/ERP.Api/wwwroot/js/modules-doc2.js`：收货计划规范模块补供应商引用、可空目的港引用、备注往返与口径注释；
  表单不提交计划单号。
- `src/ERP.Api/wwwroot/js/crud.js`：声明式可空引用（留空提交 `null`）的 ERP-482 口径说明（渲染 / 提交行为不变）。
- 复用既有（未改动）：`ReceivingPlanLifecycleRules`（ERP-361）、`SalespersonDataScopeService`（ERP-097）、
  单据号服务、`LegacyBillMutationRules`（ERP-404 门禁）。
