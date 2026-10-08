# 旧单据（`api/v2/bills`）操作历史数据授权、精确单据身份与有限路径边界

> 任务：`ERP-406`「Prevent foreign document operation history disclosure through legacy bill routes」（阶段 3 核心业务流程完整性）。
> 关联：`ERP-405`（旧单据读侧授权 / 行范围，`docs/legacy-bill-read-scope.md`）、`ERP-404`（旧写入门禁，`docs/legacy-bill-mutation-boundary.md`）、
> `ERP-308`（旧单据有限导出族目录，Stage 2）。
>
> 本任务是对**既有业务端点数据授权漏洞的修复**，不是审计类补齐、也不是新增监控模块：只改读侧授权与过滤，不新增权限模型。

## 1. 背景与问题证据（修复前）

`BillProcController` 的操作历史端点 `GET api/v2/bills/{billType}/{oid}/logs` 在**类级 `[Authorize]`** 之外
存在稳定的数据授权漏洞：

- 未做实时身份校验（已删除 / 已禁用账号仍可读）、未做该族**既有功能菜单**授权、未做业务员**客户数据范围**；
- `ReadBillNoAsync(meta.Table, oid)` 直接按路由 `Oid` 读取 `db_owner.{table}` 的 `BillNo`，
  **不校验该行是否在调用方数据范围内**，因此任意已登录账号可通过命令式探测拿到别的客户 / 别的族的单号线索；
- 过滤条件是 `l.BillNo == billNo || l.Path == "/api/v2/bills/{billType}/{oid}"`：
  `BillNo` 单独匹配时**不限定族（Module）与客户**，**同单号**在别的族 / 别的客户的历史会被一并返回；
- `oid = 0`（未传）时退回 `Path.StartsWith("/api/v2/bills/{billType}/")`，即**返回该族全部历史**（全局历史）。

## 2. 修复口径（复用既有权限模型，不新增授权）

`GetBillLogs` 现在严格按下列顺序执行（`src/ERP.Api/Controllers/BillProcController.Log.cs`）：

1. **有限族解析**：`Bills` / `BillTitles` 之外的族标识在访问任何数据之前以 `1001` 拒绝。
2. **复用 ERP-405 读侧门禁**：`LegacyBillAuthorizationRules.EnsureReadAuthorizedAsync`
   （实时身份：缺失 / 非法 / 已删除 `2000`，已禁用 `2002`；普通账号必须具备该族**既有功能菜单**，`*-export` 绝不顶替；
   特权账号沿用既有全部访问口径但仍须通过实时身份；返回 `SalespersonDataScope`）——
   该门禁在任何**单号读取 / 计数 / 历史读取之前**完成。
3. **正数 Oid**：`LegacyBillHistoryRules.BuildDocumentPaths` 对 `Oid <= 0` 以 `1001` 拒绝
   （**拒绝零 / 负数全局历史**；既有全局日志入口是 `api/sys/logs`，本路由绝不为普通业务账号放宽）。
4. **权威旧库行必须在调用方数据范围内**：`ILegacyBillReadService.ReadAuthoritativeHeaderAsync`
   （真实实现为**只读表头的有限参数化查询**，仅授权表头列、绝不读取副表）返回调用方范围内的权威行；
   越权 / 不存在返回 `null` → `1002`「单据不存在」，**不返回任何单号、客户提示或历史**；
   缺表 / 缺列映射为显式 `environment-blocked`（`5002`）。
5. **精确文档 / 有限模块与路径身份**：历史行必须同时满足
   `Module == 该族既有模块标题` **且** `BillNo == 权威单号` **且** `Path ∈ 有限精确路径集合`；
   计数与分页共用同一过滤，全部在 `COUNT` 之前施加。
6. **有界分页**：`LegacyBillHistoryRules.ResolvePaging` 收敛页码 / 页大小（默认 50、上限 200）并用 64 位检查运算，
   偏移越界以 `1001` 拒绝。

### 2.1 精确路径与有限动作段（`LegacyBillHistoryRules`）

允许的历史路径 = `基础路径` `+ 基础路径 + "/" + 有限动作段`：

- 基础路径：`/api/v2/bills/{billType}/{oid}`；
- 有限动作段（服务端常量）：`save` / `delete` / `audit` / `unaudit` / `void` / `restore`；
- 过滤为 `Path IN (有限精确集合)`，因此：
  - **前缀碰撞绝不匹配**：`…/{oid}` 不会命中 `…/{oid}{digit}`（如 `…/8001` 不命中 `…/80010`）；
  - **非有限动作段绝不匹配**：`…/{oid}/audit-extra` 不匹配（不是有限动作段）；
  - **绝不按单号单独匹配、绝不任意查询 / 用户提供路径**。

**同单号在别的族 / 别的客户绝不匹配**：跨族行 `Module` 不同（族模块标题各异），
跨客户行 `Path` 的 `Oid` 不同（指向其自身单据），二者都被精确路径 + 模块 + 权威单号交叉条件排除。

### 2.2 历史缺少权威记录标识时省略（显式记录的来源限制）

只返回「精确路径 + 权威单号」可核验的历史行。旧日志若缺少可核验的单据路径
（例如仅有单号、或路径不含 `Oid`）、或未记录权威单号，一律**省略**而不是按单号单独放行；
这是**有意**的取材限制：宁可少返回，也不把无法证明归属的历史行暴露给其它族 / 其它客户。

### 2.3 响应字段与零泄露

只返回业务时间线字段 `Id / UserName / Module / Action / BillNo / Path / IpAddress / CreatedAt / DurationMs / StatusCode`，
按 `Id` 稳定降序；**绝不**返回 `RequestBody` 等原始载荷，也绝不含令牌 / 密钥。

## 3. 验证证据

### 3.1 单元测试（`src/ERP.UnitTests/LegacyBillHistoryTests.cs`，6 条，内存库 + 真实既有身份 / 菜单 / 客户范围 + 范围语义替身）

- **精确身份**：同单号跨族（`stock-out`）/ 跨客户 / 前缀碰撞 `/80010` / 非有限动作段 `/audit-extra` /
  无记录标识 `/save` / 单号不符 / 已删除行**全部不返回**；`total` 与行只含精确路径集合内的行。
- **拒绝侧零读取**：零 / 负数 Oid `1001` 且**未触发任何权威行读取**；缺失 / 越权 Oid `1002` 且 `Data` 为 `null`
  （序列化不含权威单号）；未授权 / 已删除 `2000`，已禁用 / 无菜单 / 仅导出菜单 `2002`；撤销菜单后立即收敛，恢复后恢复读取。
- **有界分页**：`pageSize = 100000 → 200`，`page = 0 / pageSize = 0` 收敛，`page = int.MaxValue` 偏移溢出 `1001`；
  分页不重不漏、按 `Id` 降序；无 `RequestBody` 字段。
- **不可用历史源**：缺结构显式 `environment-blocked`（`5002`）。
- **零写入零通知**：拒绝 / 读取路径 `SysOperationLogs` / `SysDingTalkLogs` 不变且 `ChangeTracker` 无待写变更。
- **纯函数**：精确路径集合、前缀碰撞排除、非法族标识、有界分页与偏移检查运算。

### 3.2 真实隔离 SQL 测试（`src/ERP.IntegrationTests/LegacyBillHistorySqlServerTests.cs`，7 条 + 4 条护栏，已在受控 localdb 真实执行通过）

- **专用目标护栏（任何库访问之前）**：精确命中 `(localdb)\NEWERP_AutoAcceptance` + 库名前缀 `NEWERP_AUTOTEST`
  + `Integrated Security=true`；错误实例 / 错误库名 / 非集成安全一律 `AssertDedicatedTarget` 拒绝
  （`LegacyBillHistoryTargetGuardTests`）。每次运行只创建一个**全新 GUID 后缀库**，发现同名库已存在立即拒绝，
  **绝不 drop / reset / 复用**任何库，也绝不读取 `appsettings*.json` / `.env` / 生产凭据（连接串只来自进程环境变量或专用 localdb 默认值）。
- **真实驱动 + 既有授权**：在本隔离 GUID 库内补齐完整 NEWERP 结构与种子数据，再建受控旧库结构 `db_owner.SalesOrder`
  与两位客户的旧库行；以**新播种的既有功能菜单授权 + 业务员客户数据范围**驱动真实 `BillProcController` 与
  真实 `LegacyBillReadService`；不新增 / 不修改任何既有菜单 / 角色 / 用户授权，无匿名 / 管理员降级。
- **逐项证明**：两位客户同单号（可见 8001 与隐藏 8101 均为 `SO-DUP`）、跨族同单号（`stock-out` 日志行）、
  精确路径 vs 前缀碰撞 / 非有限动作段 / 无记录标识；受限业务员只见授权客户单据的 6 条精确历史，越权 Oid 返回 `1002`
  且不含单号；特权账号沿用既有全量口径（可读隐藏单据历史）；零 / 负数 Oid `1001`，缺失 Oid `1002`，
  已删除 `2000`，已禁用 / 无菜单 / 仅导出菜单 / 撤销授权 `2002`；`payment` / `stock-out` 等族刻意不建旧表 → `5002`；
  有界分页与偏移溢出。旧库结构完好（注入式输入按参数化处理）。
- **零写入证据**：每次拒绝 / 读取前后对 `SysOperationLogs` / `SysDingTalkLogs` / 规范业务表 `SalesOrders` /
  `StockIns` / `StockMovements`（**保留库存来源单据审计**）与旧库 `SalesOrder` 行数做只读快照，全部不变
  （本任务只新增 / 加固只读路径，不存在任何写入或降级）。
- **两个独立连接竞态**：每条竞态用例新建两个独立 `DbContext` / 连接 / 控制器并门闩对齐并发：
  ①两条受限分页连接返回**一致**的范围内 `total` 与 `Id` 集合；
  ②越权历史与无菜单账号的读取在并发下分别一致 `1002` / `2002`；收尾快照证明零写入。
- **保留既有库存来源单据审计与原始失败日志**：测试只读取计数与只读快照，不删除 / 不清理任何既有行。

### 3.3 构建与回归

- `.NET 8` Release 构建（`NEWERP.sln`）通过，0 警告 / 0 错误（`ERP.IntegrationTests` 启用 `TreatWarningsAsErrors`）。
- 既有 `ERP.UnitTests` 全套回归通过（`6238` 通过 / `0` 失败 / `0` 跳过），ERP-405 读侧与旧单据导出 / 报表迁移回归保持不变。
- **构建完成不等于阶段验收**：真实 SQL 用例只有在受控 localdb 上真实执行通过才构成验收证据。

## 4. 明确边界（非目标）

- 不编辑任何存储过程 / 数据库结构 / 迁移脚本 / 种子数据；不新增表 / 列 / 索引。
- 不新增权限模型（菜单 / 角色 / 用户授权），不提供匿名或管理员降级，不把 `*-export` 导出菜单当模块权限。
- 不改变旧单据列表 / 导航 / 详情 / 导出路由（`GET {billType}`、`{billType}/navigate`、`{billType}/{oid}`、
  `{billType}/export`）与旧写入门禁；全局操作日志仍走既有 `api/sys/logs`，本路由**拒绝**零 / 负数 Oid。
- 不触碰规范业务路由（`api/sales-orders` / `api/purchase-orders` / `api/inquiries` / `api/stock-ins` /
  `api/stock-outs` / `api/finance/*` / `api/container/*`）与规范生命周期守卫。
- 不清理 / 不删除任何既有业务行、库存来源审计或既有失败日志；不执行生产凭据或生产数据操作。
