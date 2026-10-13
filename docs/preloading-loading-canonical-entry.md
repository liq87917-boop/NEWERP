# 预装柜单 / 装柜清单规范入口与「省略单号」主子表手工录入（ERP-485）

> 任务：`ERP-485`「Restore canonical preloading and loading main-detail HTTP form entry」（阶段 3 核心业务流程闭环）。
> 关联：`ERP-040`（装柜三单改由 EF 页面承载 + 外贸 / 物流跟踪字段的权威记录）、`ERP-363` / `ERP-348`
> （预装柜单 / 装柜清单实时授权 · 客户数据范围 · 生命周期 · 来源锁）、`ERP-368`（预装柜明细显式需求计划证据链接）、
> `ERP-366` / `ERP-373`（装柜明细显式出运证据链接）、`ERP-041`（一柜多客户参与方）、
> `ERP-372`（主表单独更新不清空需求来源证据）、`ERP-484` / `ERP-482` / `ERP-477` / `ERP-478`（同源规范入口口径）、
> `ERP-404`（旧单据写入门禁，旧族写路径 fail closed）。

## 1. 背景与问题证据

`ERP-040` 已经把「预装柜单」（菜单编码 `pre-loading`）与「装柜清单」（`loading-list`）改由规范 EF 模块承载：
`modules-doc2.js` 注册 `MODULES['pre-loading']` / `MODULES['loading-list']`（`/api/container/pre-loadings`、
`/api/container/loading-lists`），`bill-v2.js` 的 `BILL_CODE_MAP` **不含**这两个键，`app.js` 的 `renderPage`
先判 `BILL_CODE_MAP` 再判 `MODULES`，因此菜单落到规范模块；旧存储过程族（`BILL_CONFIG` 的旧库单体表 `Oid` 读侧 /
`bill-edit.js` 的 `/api/v2/bills/.../save`）保留只读排查，写路径由 `ERP-404`（`LegacyBillMutationRules`）
整体停用（fail closed）。但规范入口自身仍有阻断缺陷：

- `ContainerPreLoading.PreLoadingNo` 与 `ContainerLoadingList.LoadingListNo` 带显式 `[Required]`
  （`AllowEmptyStrings = false`），手工表单不提交单号时在**模型绑定 / 模型校验阶段**就返回 HTTP 400
  `{"errors":{"PreLoadingNo":[...]}}` / `{"errors":{"LoadingListNo":[...]}}`，服务端权威单号
  （`Create` 的 `GenerateAsync(DocumentType.PreLoading / LoadingList)`）根本没有机会生成；
- 两个规范模块**未声明 `detailKey` / `detailFields`**，普通表单无法录入「主表 + 明细」；
  旧明细编辑器 `bill-edit.js` 走的是已被 ERP-404 停用的 `/api/v2/bills` 写入口，
  因此规范入口实际上是「无明细可录」的死路，`ERP-368` / `ERP-373` 的证据工作台也就无行可链接；
- `Create` 未把删除态 / 时间戳与明细身份统一收归服务端（客户端可注入 `IsDeleted` / 明细主键 / 明细删除态）；
- 装柜清单 `Create` 在**显式来源链接与出运证据链接校验之前**就占用了单据号流水
  （被拒请求会消耗一个装柜清单号，违反「先拒绝、后编号」）。

## 2. 口径与实现

### 2.1 允许省略单号（`Container2.cs`）

`ContainerPreLoading.PreLoadingNo` 与 `ContainerLoadingList.LoadingListNo` 的显式 `[Required]` 被移除
（与 `ERP-484` 订柜单号 / `ERP-482` 收货计划单号 / `ERP-477` 入库单号 / `ERP-478` 出库单号同源口径）：
省略 / 空串照常绑定并进入已授权的 `Create`；数据库列仍为 NOT NULL（非空 CLR 引用类型），长度仍受
`[MaxLength(50)]` 约束。单号 / 主键 / 状态 / 删除态一律由服务端权威覆盖（客户端伪造值不参与持久化）。

### 2.2 拒绝先于编号与持久化（控制器 `Create`）

| 顺序 | 预装柜单 `Create` | 装柜清单 `Create` |
|---|---|---|
| 1 | 客户端注入主键归零（来源自排除以「尚未落库」为准）+ 实时身份 / 账号状态 / 既有「预装柜单」（`pre-loading`）菜单 / ERP-097 权威客户数据范围 + 拟议范围 | 实时身份 / 菜单 / 数据范围 + 主键归零 + 拟议范围（兼容客户字段 + 有效参与方 + 显式上游客户） |
| 2 | ERP-353 上游订柜链接（存在 / 未删除 / 已审核 / 范围 / 柜号链接不冲突） | ERP-348 预装柜上游链接（存在 / 未删除 / 已审核 / 商品 / 数量 / 单位一致） |
| 3 | ERP-368 显式需求计划证据链接（来源订单已审核未删除、商品 / 基础单位一致、客户等于权威订柜客户、数量为正） | ERP-366 显式出运证据链接（来源出库已审核未删除、商品 / 基础单位一致、客户在权威范围内、数量为正） |
| 4 | **只有全部通过之后**才：`Id = 0`、明细身份重置、`GenerateAsync(DocumentType.PreLoading)`（`YZ` 前缀）、`Status = Pending`、`IsDeleted = false`、按明细重算汇总、落库 | 同口径：`GenerateAsync(DocumentType.LoadingList)`（`ZQ` 前缀）+ 草稿状态 + 汇总重算 + 落库 |

被拒绝的请求：**零表头变化、零状态变化、零占号、零明细写入**。提交 / 审核 / 取消 / 删除沿用既有生命周期规则
（`ERP-363` / `ERP-348`，含既有行锁 `UPDLOCK, HOLDLOCK` 与可串行化事务）。装柜单据始终只是**物流证据**：
不创建库存移动 / 库存流水 / 财务 / 单证记录，也不按自由文本推断任何来源链接。

### 2.3 明细身份与显式证据边界

`ApplyServerOwnedDetailIdentity` 把明细的**主键 / 归属单据 / 删除态 / 时间戳**统一收归服务端
（客户端注入的明细主键、明细删除态不参与持久化），而**显式证据链接列原样保留**：
预装柜明细 `SourceSalesOrderDetailId`、装柜明细 `SourceStockOutDetailId` 只由对应工作台显式维护，
普通主表 / 明细编辑既不臆造链接，也绝不静默清空既有链接（`ERP-372` / `ERP-373` 语义不变）。

### 2.4 表单往返（`modules-doc2.js` / `crud.js`）

- 两个规范模块新增 `detailKey: 'details'` + `detailFields`（商品 / 数量 / 箱数 / 毛重 / 体积 / 备注），
  普通「新增 / 编辑」侧栏即可录入主表 + 明细，一次 `POST` / `PUT` 提交；
- 单号（`preLoadingNo` / `loadingListNo`）刻意不在表单字段里：省略 / 空串由服务端权威生成，
  保存成功返回 `{ id, preLoadingNo | loadingListNo, status }`（`Pending` 草稿），重开 / 修改一律用规范 `Id`；
- 可选订柜引用（`bookingId`）/ 可选预装柜引用（`preLoadingId`）是既有 `type: 'ref'` 引用：
  留空提交 `null`（未关联），绝不回落为哨兵值 `0`，也绝不按柜号等自由文本推断来源；
- `crud.js` 的明细收集保留**整行对象**：随回填携带的持久化证据链接（`sourceSalesOrderDetailId` /
  `sourceStockOutDetailId`）原样回传，普通编辑绝不静默清空；新增行不含这些键 = 显式「未链接」；
- 保存后的规范 `Id` 直接可达既有「需求来源」/「出运证据」工作台（`preloading-demand-links.js` /
  `loading-outbound-links.js`），且明细行 Id 为服务端持久化值。

## 3. 验证

| 项 | 命令 | 结果 |
|---|---|---|
| 生成（Release + 分析器 / 警告即错误） | `dotnet build NEWERP.sln -c Release --no-incremental /p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true` | ✅ 0 警告 / 0 错误（`.ai/logs/ERP-485/release-build-3.log`） |
| 新增单元测试 | `dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build --filter FullyQualifiedName~PreLoadingLoadingCanonicalEntry --logger "trx;LogFileName=preloading-loading-canonical-entry-units.trx" --results-directory .ai/logs/ERP-485/units` | ✅ 12 项通过 / 失败 0（`.ai/logs/ERP-485/units/preloading-loading-canonical-entry-units.trx`） |
| 回归单元测试（预装柜 / 装柜） | `--filter "FullyQualifiedName~PreLoading\|FullyQualifiedName~Loading"` + `--results-directory .ai/logs/ERP-485/regression` | ✅ **279 项通过 / 失败 0**（`.ai/logs/ERP-485/regression/erp485-preloading-loading-regression.trx`） |
| 全量单元测试 | `dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build --logger "trx;LogFileName=erp485-fullunits.trx" --results-directory .ai/logs/ERP-485/fullunits` | ✅ **7880 项通过 / 失败 0**（`.ai/logs/ERP-485/fullunits/erp485-fullunits.trx`） |
| 离线契约（Node） | `node tests/preloading-loading-canonical-entry.browser.js` | ✅ exit 0，37 项断言通过（`.ai/logs/ERP-485/node-preloading-loading.log`） |
| 离线契约全量（Node） | `node tests/*.js`（17 个用例，逐个记录退出码） | ✅ **16 / 17 exit 0**（含新增用例）；唯一 exit 1 为 `finance-apply-canonical-entry.browser.js:47` 的 **ERP-480 已被 ERP-481 取代的旧作用域守卫**，与本任务无关、与本仓既有记录一致（`.ai/logs/ERP-485/node-*.log`） |
| **真实隔离 SQL / HTTP 验收** | `dotnet test src/ERP.IntegrationTests/ERP.IntegrationTests.csproj -c Release --no-build --filter FullyQualifiedName~PreLoadingLoadingCanonicalEntry --logger "trx;LogFileName=erp485-sql.trx" --results-directory .ai/logs/ERP-485/sql`（`ERP_AI_TEST_RUN=1` + `ASPNETCORE_ENVIRONMENT=Development`） | ✅ **8 项通过 / 失败 0** = 3 项真实 HTTP 场景 + 5 项目标护栏 fail-closed（真实 Kestrel + 专用 localdb + 全新 GUID 库 `NEWERP_AUTOTEST_PRELOADINGLOADING_<guid>`）（`.ai/logs/ERP-485/sql/erp485-sql.trx`） |
| 验证产物落点 | `--results-directory .ai/logs/ERP-485/...`（显式指定） | ✅ 全部 `*.trx` / 日志写入 `.ai/logs/ERP-485`，工作区根目录未新增任何测试结果文件 |

新增 / 修改测试：

- `src/ERP.UnitTests/PreLoadingLoadingCanonicalEntryTests.cs`（新增）：实体无显式必填 + 长度约束、
  省略 / 空串 / 伪造单号与伪造主键 · 状态 · 删除态 · 时间戳 · 明细身份一律服务端覆盖、
  主子表保存 → 重开 → 修改往返（含伪造明细身份被覆盖、显式证据随行保留、证据工作台按规范 Id 可达）、
  拒绝先于编号（无身份 / 无效订柜链接 / 非正预装柜来源）零写入零占号、有效生命周期不产生库存 · 财务 · 单证记录、
  模块 / 表单 / 控制器顺序接线契约。
- `src/ERP.IntegrationTests/PreLoadingLoadingCanonicalEntrySqlServerTests.cs`（新增）：真实 HTTP（真实 Kestrel +
  MVC 绑定层 + JWT 认证）证明「省略单号」不再返回 HTTP 400、主子表规范化 Id 与草稿状态往返、
  显式需求来源证据按规范 Id 在既有工作台可读且主表单独更新不清空、有效提交 / 审核 / 取消生命周期、
  匿名 / 缺菜单 / 无效来源零写入零占号；夹具为被测 API 进程注入**进程内临时随机 JWT 签名密钥**
  （不继承仓库内空白 `Jwt:Key`、不落盘、不写日志）。
- `tests/preloading-loading-canonical-entry.browser.js`（新增）：真实浏览器端到端验收的**离线契约**部分
  （菜单路由 / 路由优先级 / 主子表模块 / 不提交单号 / 规范 Id 接线 / 旧族分离）。
- 保留既有回归夹具：`PreLoadingAuthorizationTests`、`PreLoadingBookingLinkTests`、`PreLoadingDemandWorkflowTests`、
  `LoadingListAuthorizationTests`、`LoadingOutboundWorkflowTests`、`ContainerLoadingFulfillmentTests`、
  `ContainerLoadingParticipantTests` 均未改动，继续覆盖授权 / 客户范围 / 来源锁 / 生命周期 / 证据保留。

## 4. 环境受限说明（不声称整阶段验收）

- 本任务 `validation_profile = safe`、`completion_mode = build`、`browser_acceptance.required = false`：
  投递门禁为 **Release 生成 + ERP.UnitTests**。
- **真实浏览器 UI 工作流未执行**（本环境无浏览器验收执行器，任务也未要求）：因此**不声称**浏览器端到端验收
  或整阶段验收通过；`tests/preloading-loading-canonical-entry.browser.js` 仅为离线契约验证，不构成
  「页面点击 → 真实 API 写入成功」的证据。真实 HTTP 写入证据来自
  `PreLoadingLoadingCanonicalEntrySqlServerTests`（真实 Kestrel + 真实隔离库 + 真实 JWT 认证），
  其执行情况在本文件第 3 节如实记录。
- 未连接生产数据库、未使用生产凭据、未读取 `appsettings*.json` / `.env` / 生产连接串；真实 SQL 只在
  `(localdb)\NEWERP_AutoAcceptance` + `Integrated Security=true` + 全新 GUID `NEWERP_AUTOTEST` 库上执行，
  发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
- 未执行迁移 / 部署 / 任意 SQL；未新增权限、表 / 列 / 索引 / 菜单 / 角色 / 用户授权（仅复用既有菜单与既有接口），
  未做任何台账回填，也未创建任何库存 / 出库 / 单证 / 财务记录。

## 5. 相关实现

- `src/ERP.Domain/Entities/Container2.cs`：`PreLoadingNo` / `LoadingListNo` 去掉显式 `[Required]`（保留 `[MaxLength(50)]`）。
- `src/ERP.Api/Controllers/ContainerPreLoadingController.cs`：`Create` 服务端权威身份覆盖（主键 / 单号 / 状态 /
  删除态 / 时间戳）+ 明细身份重置 + 返回 `{ id, preLoadingNo, status }`；`Update` 复用同一明细身份助手，
  保留显式需求来源证据链接。
- `src/ERP.Api/Controllers/ContainerLoadingListController.cs`：`Create` 把单号预约与写入移到全部校验之后
  （拒绝零占号）+ 服务端权威身份覆盖 + 返回 `{ id, loadingListNo, status }`；`Update` 复用明细身份助手，
  保留显式出运证据链接。
- `src/ERP.Api/wwwroot/js/modules-doc2.js`：两个规范模块补充 `detailKey` / `detailFields` 与 ERP-485 口径说明。
- `src/ERP.Api/wwwroot/js/crud.js`：ERP-485 口径说明（主子表一次提交、可空引用、证据链接原样回传；渲染 / 提交行为不变）。
- 复用既有（未改动）：`PreLoadingAuthorizationRules` / `LoadingListAuthorizationRules`（ERP-363 / ERP-364）、
  `PreLoadingBookingLinkRules`（ERP-353）、`PreLoadingSalesOrderLinkRules`（ERP-368）、
  `ContainerLoadingFulfillmentRules`（ERP-348）、`LoadingStockOutLinkRules`（ERP-366）、
  `ContainerLoadingParticipantService`（ERP-041）、`SalespersonDataScopeService`（ERP-097）、单据号服务、
  `LegacyBillMutationRules`（ERP-404 门禁）。
