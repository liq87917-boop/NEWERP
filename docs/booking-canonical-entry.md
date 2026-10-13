# 订柜信息规范入口与「省略订柜单号」手工录入（ERP-484）

> 任务：`ERP-484`「Restore server-numbered canonical booking HTTP form entry」（阶段 3 核心业务流程闭环）。
> 关联：`ERP-040`（装柜三单改由 EF 页面承载 + 外贸 / 物流跟踪字段的权威记录）、`ERP-360`（订柜信息实时授权 /
> 客户数据范围 / 主数据可用性护栏）、`ERP-353`（订柜取消与预装柜审核的并发协同）、`ERP-482` / `ERP-477` /
> `ERP-478` / `ERP-479` / `ERP-480` / `ERP-481`（收货计划 / 入库 / 出库 / 收款付款 / 财务申请 / 结算单同源口径）、
> `ERP-097`（权威数据范围）、`ERP-404`（旧单据写入门禁，旧族写路径 fail closed）。

## 1. 背景与问题证据

`ERP-040` 已经把「订柜信息」菜单（菜单编码 `booking`）改由规范 EF 模块承载：`modules-doc2.js` 注册
`MODULES['booking']`（`api: '/api/container/bookings'`），`bill-v2.js` 的 `BILL_CODE_MAP` **不再含** `booking` 键，
`app.js` 的 `renderPage` 先判 `BILL_CODE_MAP` 再判 `MODULES`，因此菜单落到规范模块；旧存储过程族
（`BILL_CONFIG['booking']` 的旧库单体表 `Oid` 读侧）保留只读可用，写路径由 `ERP-404`（`LegacyBillMutationRules`）
整体停用（fail closed）。

但规范入口自身仍有阻断缺陷：

- `ContainerBooking.BookingNo` 带显式 `[Required]`（`AllowEmptyStrings = false`），手工表单不提交订柜单号时在
  **模型绑定 / 模型校验阶段**就返回 HTTP 400 `{"errors":{"BookingNo":["The BookingNo field is required."]}}`，
  服务端权威单号（`Create` 的 `GenerateAsync(DocumentType.ContainerBooking)`）根本没有机会生成；
- `Create` 未把删除态 / 时间戳统一收归服务端（客户端可注入 `IsDeleted`）；
- 表单未把可选供应商 / 报关行引用声明为**显式可空引用**（`nullable: true`），也未在保存返回值中显式给出草稿状态。

## 2. 口径与实现

### 2.1 允许省略订柜单号（`Container1.cs`）

`ContainerBooking.BookingNo` 的显式 `[Required]` 被移除（与 `ERP-482` 收货计划单号 / `ERP-477` 入库单号 /
`ERP-478` 出库单号 / `ERP-479` 收款单号 · 付款单号 / `ERP-480` 申请单号 / `ERP-481` 结算单号同源口径）：
省略 / 空串照常绑定并进入已授权的 `ContainerBookingController.Create`；数据库列仍为 NOT NULL（非空 CLR 引用类型），
长度仍受 `[MaxLength(50)]` 约束。订柜单号 / 主键 / 状态 / 删除态一律由服务端权威覆盖（客户端伪造值不参与持久化）。

### 2.2 拒绝先于编号与持久化（控制器 `ContainerBookingController.Create`）

| 顺序 | 订柜信息 `Create` |
|---|---|
| 1 | 实时身份 + 账号状态 + 既有「订柜信息」（`booking`）菜单 + ERP-097 权威客户数据范围（未映射业务员的受限账号 fail closed） |
| 2 | 客户（必填正整数、存在、未删除、启用）；可选供应商（存在、未删除、启用）；ERP-040 跟踪字段（出运方式只接受 LCL / FCL / 未指定、B/L / S-O / 港口 / 拖车等去空白后有界、查验要求三态自洽、报关行必须是启用未删除的 `CustomsBroker` 字典项）；有界文本列由既有 `[MaxLength]` 在绑定层拒绝 |
| 3 | **只有全部校验通过之后**才：`Id = 0`、`GenerateAsync(DocumentType.ContainerBooking)`（`DG` 前缀）、`Status = Pending`、`IsDeleted = false`、落库；审计时间戳由既有 `ApplyAudit` 统一刷新 |

被拒绝的请求：**零表头变化、零状态变化、零占号**（单号预约永不发生）。提交 / 审核 / 取消 / 删除沿用既有生命周期
规则（`ERP-360` / `ERP-353`，含同一把行锁 `UPDLOCK, HOLDLOCK` 与可串行化事务）。订柜信息始终只是**物流证据**：
不创建库存移动 / 装柜 / 单证 / 财务记录，也不按自由文本推断任何来源链接。

### 2.3 表单往返（`modules-doc2.js` / `crud.js`）

- 客户 = 既有主数据引用（`REF_APIS.customer`）；可选供应商 = 既有主数据引用并声明 `nullable: true`；
- 可选报关行 = 既有「其他资料」引用并声明 `nullable: true`：留空提交 `null`（未指定），显式数字 `0` / 非法 Id
  仍由服务端按「非法引用」fail closed 拒绝，名称快照只由服务端按字典项权威写入；
- 订柜日期 / 开船日期 / ETD·ETA·ATD·ATA / 查验日期 / 海关放行日期均为显式可空日期：留空提交 `null`（未知），
  服务端**绝不**由自由文本推断任何日期；
- ERP-040 跟踪字段（出运方式 / B/L / S-O / 中转港 / 拖车 / 查验三态）参与保存 → 重开 → 修改原样往返；
- 订柜单号不在表单字段里：省略 / 空串由服务端权威生成，保存成功返回 `{ id, bookingNo, status }`（`Pending` 草稿），
  重开 / 修改一律用规范 `Id`，**绝不做** legacy 单号映射。

## 3. 验证

| 项 | 命令 | 结果 |
|---|---|---|
| 生成（Release + 分析器 / 警告即错误） | `dotnet build NEWERP.sln -c Release --no-incremental /p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true` | ✅ 0 警告 / 0 错误（`.ai/logs/ERP-484/release-build.log`） |
| 新增单元测试 | `dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build --filter FullyQualifiedName~BookingCanonicalEntry --results-directory .ai/logs/ERP-484/units` | ✅ 7 项通过 / 失败 0（`.ai/logs/ERP-484/units/booking-canonical-entry-units.trx`） |
| 全量单元测试 | `dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build --results-directory .ai/logs/ERP-484/fullunits` | ✅ **7868 项通过 / 失败 0**（`.ai/logs/ERP-484/fullunits/erp484-fullunits.trx`） |
| 离线契约（Node） | `node tests/*.js`（16 个用例） | ✅ **15 / 16 exit 0**（含新增 `booking-canonical-entry.browser.js` exit 0）；唯一 exit 1 为 **ERP-480 已被 ERP-481 取代的作用域守卫**（`finance-apply-canonical-entry.browser.js:47` 断言 `container-settlement` 仍是生效路由），与 ERP-484 无关、与本仓既有记录一致（`.ai/logs/ERP-484/node-all.log`） |
| **真实隔离 SQL / HTTP 验收** | `dotnet test src/ERP.IntegrationTests/ERP.IntegrationTests.csproj -c Release --no-build --filter FullyQualifiedName~BookingCanonicalEntry --results-directory .ai/logs/ERP-484/sql`（`ERP_AI_TEST_RUN=1` + `ASPNETCORE_ENVIRONMENT=Development`） | ✅ **10 项通过 / 失败 0** = 5 项真实 HTTP 场景 + 5 项目标护栏 fail-closed（真实 Kestrel + 专用 localdb + 全新 GUID 库 `NEWERP_AUTOTEST_BOOKINGCANONICAL_<guid>`）（`.ai/logs/ERP-484/sql/erp484-sql.trx`） |
| 验证产物落点 | `--results-directory .ai/logs/ERP-484/...` | ✅ 全部 `*.trx` / 日志写入 `.ai/logs/ERP-484`，工作区根目录未新增任何测试结果文件 |

新增 / 修改测试：

- `src/ERP.UnitTests/BookingCanonicalEntryTests.cs`（新增）：实体无显式必填 + 长度约束、省略 / 空串 / 伪造订柜单号
  与伪造主键 · 状态 · 删除态 · 时间戳一律服务端覆盖、可空供应商 / 报关行引用与可选日期 / ERP-040 跟踪字段
  保存 → 重开 → 修改原样往返、拒绝先于编号（无身份 / 缺菜单 / 无效 · 已删除 · 已停用客户或供应商 / 非法出运方式 /
  超长 B/L / 自相矛盾查验 / 不存在 · 停用 · 类型不符报关行）零写入零占号、有效生命周期不产生预装柜 / 库存 /
  库存流水 / 财务记录、菜单路由与控制器接线契约。
- `src/ERP.IntegrationTests/BookingCanonicalEntrySqlServerTests.cs`（新增）：真实 HTTP（真实 Kestrel + MVC
  绑定层 + JWT 认证）证明「省略订柜单号」不再返回 HTTP 400、规范 Id 与草稿状态往返（新增 / 回读 / 修改 / 列表）、
  可空引用显式关联与清空、有效提交 / 审核 / 取消生命周期、无效主数据 / 越权请求零写入零占号、旧族写路径
  fail closed；夹具为被测 API 进程注入**进程内临时随机 JWT 签名密钥**（不继承仓库内空白 `Jwt:Key`、不落盘、不写日志）。
- `tests/booking-canonical-entry.browser.js`（新增）：真实浏览器端到端验收的**离线契约**部分
  （菜单路由 / 路由优先级 / 规范模块 / 可空引用 / 规范 Id 接线 / 旧族分离）。
- 保留既有回归夹具：`BookingAuthorizationTests`、`PreLoadingBookingLinkTests`、`ContainerShipmentTrackingTests`
  均未改动，继续覆盖 ERP-360 / ERP-353 / ERP-040 的授权、客户范围、主数据可用性、来源锁与生命周期。

## 4. 环境受限说明（不声称整阶段验收）

- 本任务 `validation_profile = safe`、`completion_mode = build`、`browser_acceptance.required = false`：
  投递门禁为 **Release 生成 + ERP.UnitTests**。
- **真实浏览器 UI 工作流未执行**（本环境无浏览器验收执行器，任务也未要求）：因此**不声称**浏览器端到端验收
  或整阶段验收通过；`tests/booking-canonical-entry.browser.js` 仅为离线契约验证，不构成
  「页面点击 → 真实 API 写入成功」的证据。真实 HTTP 写入证据来自 `BookingCanonicalEntrySqlServerTests`
  （真实 Kestrel + 真实隔离库 + 真实 JWT 认证），其执行情况在本文件第 3 节与本任务日志中如实记录。
- 未连接生产数据库、未使用生产凭据、未读取 `appsettings*.json` / `.env` / 生产连接串；真实 SQL 只在
  `(localdb)\NEWERP_AutoAcceptance` + `Integrated Security=true` + 全新 GUID `NEWERP_AUTOTEST` 库上执行，
  发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
- 未执行迁移 / 部署 / 任意 SQL；未新增权限、表 / 列 / 索引 / 菜单 / 角色 / 用户授权（仅复用既有菜单与既有接口），
  未做任何台账回填，也未创建任何库存 / 预装柜 / 装柜 / 单证 / 财务记录。

## 5. 相关实现

- `src/ERP.Domain/Entities/Container1.cs`：`ContainerBooking.BookingNo` 去掉显式 `[Required]`（保留 `[MaxLength(50)]`）。
- `src/ERP.Api/Controllers/ContainerControllers.cs`：`ContainerBookingController.Create` 的服务端权威身份覆盖
  （主键 / 单号 / 状态 / 删除态）、返回 `{ id, bookingNo, status }` 与 ERP-484 说明；校验顺序保持「先拒绝、后编号」。
- `src/ERP.Api/wwwroot/js/modules-doc2.js`：订柜信息规范模块补充可选供应商 / 报关行 `nullable: true` 与 ERP-484 口径；
  表单**不提交订柜单号**（既有口径不变）。
- `src/ERP.Api/wwwroot/js/crud.js`：声明式可空引用（留空提交 `null`）的 ERP-484 口径说明（渲染 / 提交行为不变）。
- 复用既有（未改动）：`BookingAuthorizationRules`（ERP-360）、`ContainerShipmentTrackingRules` /
  `ContainerShipmentTrackingService`（ERP-040）、`PreLoadingBookingLinkRules`（ERP-353 / ERP-372）、
  `SalespersonDataScopeService`（ERP-097）、单据号服务、`LegacyBillMutationRules`（ERP-404 门禁）。

