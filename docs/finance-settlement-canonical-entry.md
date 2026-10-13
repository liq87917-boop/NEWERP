# 装柜结算单 / 散货结算单规范入口与「省略结算单号」手工录入（ERP-481）

> 任务：`ERP-481`「Restore canonical container and bulk settlement form entry」（阶段 3 核心业务流程闭环）。
> 关联：`ERP-404`（旧单据写入门禁，旧族写路径 fail closed）、`ERP-480`（定金 / 货款申请单同源口径）、
> `ERP-479`（收款单 / 付款单同源口径）、`ERP-477` / `ERP-478`（入库 / 出库规范入口同源口径）、
> `ERP-384`（装柜结算单生命周期与来源清单锁序）、`ERP-387`（散货结算单生命周期）、
> `ERP-392`（装柜结算单来源装柜清单选择器）、`ERP-370`（实时授权与客户数据范围）、`ERP-428`（声明式可空引用）。

## 1. 背景与问题证据

`modules-finance.js` 早已注册规范 EF 模块 `container-settlement`（`api: '/api/finance/container-settlements'`）与
`bulk-settlement`（`api: '/api/finance/bulk-settlements'`），但 `bill-v2.js` 的 `BILL_CODE_MAP` 仍把菜单编码
`container-settlement` / `bulk-settlement` 分别映射到自身，落到旧存储过程族单据页；`app.js` 的 `renderPage`
先判 `BILL_CODE_MAP` 再判 `MODULES`，因此「装柜结算单」「散货结算单」菜单实际打开的是旧库单体表
（`Oid` 口径）单据页，而该族写路径已被 `ERP-404`（`LegacyBillMutationRules`）**整体停用（fail closed）** ——
页面能开、保存永远被拒，规范表单与既有装柜清单来源选择器工作台无法从菜单进入。

同时规范入口自身也有缺陷：

- `FinanceContainerSettlement.SettlementNo` / `FinanceBulkSettlement.SettlementNo` 带显式 `[Required]`
  （`AllowEmptyStrings = false`），手工表单不提交结算单号时在**模型绑定 / 模型校验阶段**就返回 HTTP 400
  `{"errors":{"SettlementNo":["The SettlementNo field is required."]}}`，服务端权威单号根本没有机会生成；
- 两个 `Create` 未把删除态 / 时间戳统一收归服务端（客户端可注入 `IsDeleted` / `UpdatedAt`）；
- 装柜结算单表单未把来源装柜清单声明为**显式可空引用**（留空仍提交哨兵值 `0`），「显式清空来源」语义不够清晰；
- 装柜结算单 / 散货结算单表单未提交 `remark`，编辑保存会把已存备注静默置空（保存 → 重开 → 修改不能原样往返）。

## 2. 口径与实现

### 2.1 菜单路由（只改路由，不改旧族读侧）

| 项 | 口径 |
|---|---|
| `bill-v2.js` `BILL_CODE_MAP` | **停用** `'container-settlement': 'container-settlement'` 与 `'bulk-settlement': 'bulk-settlement'` 两条旧族路由（原地停用为迁移记录，不再参与任何路由判定；ERP-481 注释说明原因）。旧族自身（`BILL_CONFIG['container-settlement']` / `BILL_CONFIG['bulk-settlement']` 的旧库单体表 `Oid` 读侧、旧导出、既有默认值端点）**保持只读可用**。 |
| `modules-finance.js` | `MODULES['container-settlement']`：`api: '/api/finance/container-settlements'`、`canSubmit: true`，来源 `loadingListId` 为 `type: 'number'` + `nullable: true` + `selector: 'container-settlement-loading-source'`；`MODULES['bulk-settlement']`：`api: '/api/finance/bulk-settlements'`、`canSubmit: true`，**无来源字段**（手工 / 未关联客户语义）。 |
| 规范 Id 口径 | 列表 / 详情 / 保存 / 提交 / 审核 / 取消 / 删除全走规范 `Id`（`crud.js` 的 `mod.api` 与 `mod.api/{id}`）。**绝不**把旧 `Oid` 映射成规范 `Id`，也**绝不**启用任何旧写适配器。 |
| 既有来源选择器 | 复用既有 ERP-392 实现（`container-settlement-loading-source.js`，按字段 `selector` 接线）：只回填权威来源 `Id`，共享柜越范围 / 已取消候选不可选，可显式断开，绝不自动改总额 / 费用。**不新增接口、不新增取数通道。** |
| 散货结算单 | 没有来源单据字段：只保留**手工 / 未关联**录入，绝不臆造来源、绝不按备注等自由文本推断装运 / 订单来源。 |

### 2.2 允许省略结算单号（`Finance2.cs`）

`FinanceContainerSettlement.SettlementNo` 与 `FinanceBulkSettlement.SettlementNo` 的显式 `[Required]` 被移除
（与 `ERP-477` 入库单号 / `ERP-478` 出库单号 / `ERP-479` 收款单号 · 付款单号 / `ERP-480` 申请单号同源口径）：
省略 / 空串照常绑定并进入已授权的 `FinanceContainerSettlementController.Create` /
`FinanceBulkSettlementController.Create`；数据库列仍为 NOT NULL（非空 CLR 引用类型），长度仍受 `[MaxLength(50)]` 约束。
结算单号 / 主键 / 状态 / 删除态 / 时间戳一律由服务端权威覆盖（客户端伪造值不参与持久化）。

### 2.3 拒绝先于编号与持久化（控制器）

| 顺序 | 装柜结算单 `Create` | 散货结算单 `Create` |
|---|---|---|
| 1 | 三个金额按既有存储精度取整并校验（总金额 > 0、海运费 / 其他费用 >= 0，无币种 / 无汇率 / 无合计公式） | 两个金额同样取整并校验（总金额 > 0、海运费 >= 0） |
| 2 | 实时身份 + 既有「装柜结算单」（`container-settlement`）菜单 + ERP-097 客户数据范围 | 实时身份 + 既有「散货结算单」（`bulk-settlement`）菜单 + 客户数据范围 |
| 3 | 客户存在 / 未删除 / 启用；可空来源装柜清单按 `Id` 精确解析（既有、未删除、未取消，且客户为该清单权威客户，复用 ERP-364 参与方 / 上游范围），空来源保留历史语义 | 客户存在 / 未删除 / 启用（散货结算单无来源单据） |
| 4 | **只有全部校验通过之后**才：`Id = 0`、`GenerateAsync(DocumentType.ContainerSettlement)`、`Status = Pending`、`IsDeleted = false`、`UpdatedAt = null`、`CreatedAt = now`、落库（关系型后端在同一事务内先取来源清单行锁、锁内复核来源资格） | 同左（`GenerateAsync(DocumentType.BulkSettlement)`） |

被拒绝的请求：**零表头变化、零来源改写（含来源清单状态 / 删除态）、零状态变化、零占号**（单号预约永不发生）。
提交 / 审核 / 取消 / 删除仍沿用既有生命周期规则（ERP-384 / ERP-387）：授权与持久化字段复核先于状态写入，失败整体回滚。

### 2.4 可空来源引用与显式选择 / 清空

| 项 | 口径 |
|---|---|
| 实体 | `FinanceContainerSettlement.LoadingListId` 为 `long?`（既有定义，未改动 schema）；散货结算单无来源字段。 |
| 表单 | `loadingListId` 声明 `nullable: true`：留空提交 `null`（声明式可空引用，绝不回落为哨兵值 `0`）；「清空」即显式断开来源。 |
| 服务端 | 空来源（`null`）保留历史未链接语义；显式非正数 `Id`（0 / 负数）按「非法引用」fail closed 拒绝且零写入，绝不静默改写为未关联；显式 `Id` 必须解析到既有、未删除、未取消且客户匹配的装柜清单，绝不按柜号 / 单号文本猜测。 |
| 重开口径 | 已存储来源由既有只读接口（`/{id}/loading-list-source`）按规范 `Id` 展示；已取消 / 不可用来源原样保留并显式标注，**不静默清除 / 不重绑定**。 |
| 备注往返 | 两个模块声明 `remark` 字段并随表单提交：编辑保存不再把已存备注置空（保存 → 重开 → 修改原样往返）。 |

### 2.5 保持不变的既有护栏

| 项 | 口径 |
|---|---|
| 实时授权 | `ERP-370`：列表 / 详情 / 创建 / 修改 / 提交 / 审核 / 取消 / 删除每次都重新校验实时身份、既有菜单授权与客户数据范围。 |
| 金额语义 | 只按既有存储精度（`decimal(18,2)`，2 位小数，0.5 进位）校验；两类结算单都没有币种字段，因此不换算、不跨币种聚合、不施加合计公式。 |
| 来源锁与生命周期 | `ERP-384`：锁序「先来源装柜清单行、后结算单行」；`ERP-387`：仅结算单行锁。状态流转与来源链接变更全部在锁内复核后写入。 |
| 审计与受控错误 | 失败整体回滚，状态、原始字段、删除标记与审计不变；错误仍为既有业务错误码。 |
| 边界 | 不写库存 / 资金 / 会计凭证，不产生记账、核销、分摊入账或对账结论；不新增权限、表 / 列 / 菜单 / 角色；不执行任意 SQL。 |

## 3. 验证

完整构建 / 测试输出与 TRX 全部写在 `.ai/logs/ERP-481/`（工作区根目录未新增任何测试结果文件）。

| 项 | 命令 | 结果 |
|---|---|---|
| Release 生成（分析器 + 警告即错误） | `dotnet build NEWERP.sln -c Release --no-incremental /p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true` | ✅ 0 警告 0 错误（`ERP-481-build.log`） |
| 新增单元测试 | `dotnet test src/ERP.UnitTests -c Release --filter "FullyQualifiedName~FinanceSettlementCanonicalEntryTests" --results-directory .ai/logs/ERP-481/units-new` | ✅ 28 项通过 / 失败 0（`ERP-481-newunits.log`、`units-new/erp481-newunits.trx`） |
| 全量单元测试 | `dotnet test src/ERP.UnitTests -c Release --results-directory .ai/logs/ERP-481/units-full` | ✅ 7837 项通过 / 失败 0 / 跳过 0（30 s）（`ERP-481-fullunits.log`、`units-full/erp481-fullunits.trx`） |
| 前端离线契约（本任务） | `node tests/finance-settlement-canonical-entry.browser.js` | ✅ 50 项离线断言通过（`ERP-481-node-new.log`） |
| 既有前端回归 | `node tests/*.js`（13 个用例，含 `container-settlement-loading-source.test.js`、`stock-in/out-canonical-entry.browser.js`、`receipt-payment-canonical-entry.browser.js`） | ✅ 12 / 13 exit 0；唯一 exit 1 为 **ERP-480 已被取代的作用域守卫**（见第 4 节），其余全部通过（`ERP-481-node-all.log`） |
| **真实隔离 SQL / HTTP 验收** | `dotnet test src/ERP.IntegrationTests -c Release --filter "FullyQualifiedName~FinanceSettlementCanonicalEntry" --results-directory .ai/logs/ERP-481/sql`（`ERP_AI_TEST_RUN=1` + `ASPNETCORE_ENVIRONMENT=Development`） | ✅ **23 项通过 / 失败 0** = 19 项真实 HTTP + SQL 场景 + 4 项目标护栏 fail-closed（真实 Kestrel + 专用 localdb + 全新 GUID 库 `NEWERP_AUTOTEST_FINANCESETTLEMENTCANONICAL_<guid>`）（`ERP-481-sql2.log`、`sql/erp481-sql.trx`；首次运行夹具读回 SQL 写错列名的原始失败日志保留在 `ERP-481-sql.log`） |
| 验证产物落点 | `--results-directory .ai/logs/ERP-481/...` | ✅ 全部 `*.trx` / 日志写入 `.ai/logs/ERP-481`，工作区根目录未新增任何测试结果文件 |

新增测试：

- `src/ERP.UnitTests/FinanceSettlementCanonicalEntryTests.cs`（新增）：实体无显式必填 + 长度约束 + 无币种 / 汇率字段、
  省略 / 空串 / 伪造结算单号与伪造主键 · 状态 · 删除态 · 时间戳一律服务端覆盖、
  无身份 / 缺菜单 / 账号停用 / 客户不可用或越范围 / 非法金额 / 来源清单悬空 · 已删除 · 已取消 · 跨客户一律零写入零占号、
  可空来源引用显式关联 / 显式清空 / 非正数来源 Id 拒绝且零改写、往返（详情 / 列表 / 修改）、
  有效生命周期与非法流转零状态变化、菜单路由与控制器接线契约。
- `src/ERP.IntegrationTests/FinanceSettlementCanonicalEntrySqlServerTests.cs`（新增）：真实 HTTP（真实 Kestrel +
  MVC 绑定层 + JWT 认证）证明「省略结算单号」不再返回 HTTP 400、规范 Id 往返（新增 / 回读 / 修改 / 列表）、
  显式来源装柜清单关联 · 候选 · 已存储展示 · 显式清空、有效提交 / 审核 / 取消生命周期、越权 / 非法请求零写入零占号、
  旧族写路径 fail closed；夹具为被测 API 进程注入**进程内临时随机 JWT 签名密钥**（不继承仓库内空白 `Jwt:Key`、不落盘、不写日志）。
- `tests/finance-settlement-canonical-entry.browser.js`（新增）：真实浏览器端到端验收的**离线契约**部分
  （菜单路由 / 路由优先级 / 规范模块 / 可空来源引用 / 既有选择器接线 / 规范 Id 接线 / 旧族分离）。

## 4. 环境受限说明（不声称整阶段验收）

- 本任务 `validation_profile = safe`、`completion_mode = build`、`browser_acceptance.required = false`：
  投递门禁为 **Release 生成 + ERP.UnitTests**。
- **真实浏览器 UI 工作流未执行**（本环境无浏览器验收执行器，任务也未要求）：因此**不声称**浏览器端到端验收
  或整阶段验收通过；`tests/finance-settlement-canonical-entry.browser.js` 仅为离线契约验证，不构成
  「页面点击 → 真实 API 写入成功」的证据。真实 HTTP 写入证据来自 `FinanceSettlementCanonicalEntrySqlServerTests`
  （真实 Kestrel + 真实隔离库 + 真实 JWT 认证），其执行情况在本文件第 3 节与本任务日志中如实记录。
- ERP-480 的离线契约 `tests/finance-apply-canonical-entry.browser.js` 含一条 **ERP-480 作用域**的回归守卫
  （`hasOwnProperty(billCodeMap, 'container-settlement')`，用于断言「ERP-480 只改定金 / 货款申请单路由」）。
  ERP-481 正是要停用 `container-settlement` / `bulk-settlement` 的旧族路由，因此该守卫**必然被本次变更取代**；
  该文件不在本任务允许修改的路径内，故未改动，其执行结果在任务日志中如实记录（不作为本任务回归证据）。
- 未连接生产数据库、未使用生产凭据、未读取 `appsettings*.json` / `.env` / 生产连接串；真实 SQL 只在
  `(localdb)\NEWERP_AutoAcceptance` + `Integrated Security=true` + 全新 GUID `NEWERP_AUTOTEST` 库上执行，
  发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
- 未执行迁移 / 部署 / 任意 SQL；未新增权限、表 / 列 / 索引 / 菜单 / 角色 / 用户授权（仅复用既有菜单与既有接口），
  未做任何台账回填或自动财务指派。

## 5. 相关实现

- `src/ERP.Domain/Entities/Finance2.cs`：`FinanceContainerSettlement.SettlementNo` / `FinanceBulkSettlement.SettlementNo` 去掉显式 `[Required]`。
- `src/ERP.Api/Controllers/FinanceSettlementControllers.cs`：两个 `Create` 的服务端权威身份覆盖（含删除态 / 时间戳）与说明。
- `src/ERP.Api/wwwroot/js/bill-v2.js`：停用 `container-settlement` / `bulk-settlement` → 旧存储过程族映射（旧族读侧保留只读）。
- `src/ERP.Api/wwwroot/js/modules-finance.js`：装柜结算单来源字段改声明式可空引用（`nullable: true`）并补充口径注释；两个模块补 `remark` 往返字段。
- `src/ERP.Api/wwwroot/js/crud.js`：声明式可空引用（留空提交 `null`）的口径说明（渲染 / 提交行为不变）。
- `src/ERP.Api/wwwroot/js/container-settlement-loading-source.js`：复用既有 ERP-392 选择器（本次只补接线说明，未改判定逻辑）。
- 复用既有：`FinanceContainerSettlementLifecycleRules`（ERP-384）、`FinanceBulkSettlementLifecycleRules`（ERP-387）、
  `ContainerSettlementLoadingSourceService`（ERP-392）、`SalespersonDataScopeService`（ERP-097）、`LegacyBillMutationRules`（ERP-404 门禁）。
