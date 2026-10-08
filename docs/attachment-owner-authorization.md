# 附件证据归属授权说明（ERP-407）

- 任务：`ERP-407`「Enforce current business owner authority on attachment evidence and content」
- 权威实现：
  - `src/ERP.Application/Services/AttachmentOwnerAuthorizationRules.cs`（**唯一权威口径**：实时身份 + 既有父单据模块菜单 + 当前客户数据范围 + 权威归属解析 + 范围下推）
  - `src/ERP.Api/Controllers/AttachmentEvidenceController.cs`（每个普通 / 中心路由在读取元数据、计数、候选、摘要、内容、存储或写入之前解析权威访问上下文）
  - `src/ERP.Application/Services/AttachmentEvidenceService.cs`（普通路由与附件中心工作台在计数 / 分页 / 选项 / 摘要 / 存储访问之前应用归属类型与客户范围）

## 1. 授权口径

| 环节 | 口径 |
| --- | --- |
| 实时身份 | 每个路由在读取任何元数据 / 计数 / 文件名 / 摘要 / 内容 / 存储或写入之前重新解析身份：缺失 / 非法按未认证拒绝；账号不存在或已删除按未认证拒绝；账号已禁用按权限不足拒绝 |
| 既有菜单 | 归属类型 → 既有父单据模块菜单：销售订单 `sales-order`、采购订单 `purchase-order`、出口单证 `doc-center`、验货记录（**既有采购订单 QC 记录**）复用 `purchase-order`、样品 `sample`。复用既有「角色 → 菜单」授权（忽略按钮型菜单与已删除记录），撤销后下一次请求立即收敛（无缓存），**不新增**任何菜单 / 角色 / 用户授权 |
| 客户范围 | 复用 ERP-097 `SalespersonDataScopeService` 唯一权威口径：受限账号按其登录名精确匹配员工编码（`IsSalesman == true`），可见客户 = `BaseCustomer.EmpId == 本人`；未映射业务员 / 未分配客户 = 空集合（fail closed，看不到任何客户） |
| 特权账号 | 超级管理员 / 系统内置角色 / 显式配置的特权角色保留既有全部客户可见性（含历史无主父单据），**但仍然受既有菜单限制**（菜单是唯一收敛维度） |
| 身份来源 | 只来自已认证请求主体（`ClaimTypes.NameIdentifier`）；请求体**不包含也不接受**任何账号 / 角色 / 范围字段 |

## 2. 权威归属（绝不推断）

| 归属类型 | 权威归属字段 | 受限账号 fail closed 的情形 |
| --- | --- | --- |
| 销售订单 | 持久化 `SalesOrder.CustomerId` | 归属客户越界 |
| 采购订单 | 持久化 `PurchaseOrder.OwningCustomerId` **与**按 `OwningSalesOrderId` 精确解析的来源销售订单客户（复用 ERP-371 既有特权口径） | 两侧任一侧越界；既无显式归属、也无权威来源的无归属备货采购 |
| 验货记录 | 同采购订单（本仓库没有独立验货实体） | 同上 |
| 出口单证 | 持久化 `TradeDocument.CustomerId` | 归属缺失（`CustomerId` 为空）或越界 |
| 样品记录 | 持久化 `Sample.CustomerId` | 归属缺失或越界 |

一律**不**按归属单据号码快照 `OwnerNo`、单证「附件说明 / 存放位置」自由文本、供应商 / 客户名称或上传文件名推断归属；父单据缺失 / 已删除时受限账号同样 fail closed（历史证据不通过快照授予访问），特权账号保留既有历史可读性（标注归属不可用，绝不改派、绝不静默修复）。

## 3. 路由覆盖

| 路由 | 授权 + 范围 |
| --- | --- |
| `GET /api/attachment-evidences` | 台账范围在 `Count` 与分页之前下推 SQL（归属类型 + 客户范围），未授权类型与范围外归属既不计数也不返回 |
| `GET /api/attachment-evidences/metadata` | 要求实时启用身份（模块元数据是静态口径，不读取任何记录 / 计数 / 内容） |
| `GET /api/attachment-evidences/by-owner` | 归属类型菜单授权 + 归属单据存在性 + 权威归属客户范围（fail closed） |
| `GET /api/attachment-evidences/owner-options` | 归属类型菜单授权 + 候选范围下推（受限账号无可见客户 → 授权不足拒绝） |
| `GET /api/attachment-evidences/owner-summary` | 归属类型菜单授权 + 混合归属 Id 收敛：范围外 / 无权威归属的 Id **连零计数都不返回**（不泄露存在性） |
| `GET /api/attachment-evidences/{id}` | 正证据 Id 先复核归属类型菜单授权 + 权威归属客户范围，未授权一律按「不存在」 |
| `POST /api/attachment-evidences`（上传） | 解析授权 → 归属类型菜单 → 归属存在性 → 权威归属范围，**全部通过之后**才读取文件内容与写入任何字节 |
| `GET /api/attachment-evidences/{id}/content` | 正证据 Id 复核通过后才打开存储对象；保留长度 / SHA-256 一致性校验与防御性响应头 |
| `POST /api/attachment-evidences/{id}/void` | 正证据 Id 复核通过后才允许作废（只改状态与作废留痕） |
| `GET /api/attachment-evidences/center` | 当前账号已授权归属类型 + 客户范围（计数 / 分页之前下推） |
| `GET /api/attachment-evidences/center/summary` | 授权范围内按状态计数（未授权类型连计数行都没有） |
| `GET /api/attachment-evidences/center/{id}` | 归属类型授权 + 权威归属范围复核，未授权 / 范围外按「不存在」 |
| `GET /api/attachment-evidences/center/{id}/content` | 复核通过后才读取存储；保留安全下载响应头 |

## 4. 边界

- 不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，不新增第二套存储或第二个附件登记表，不把空身份当作管理员，不伪造任何授权。
- 不改写销售订单 / 采购订单的状态、金额、明细与备注，不改写单证状态与明细行，不改写样品台账，不生成库存移动，不改动库存 / 财务 / 发票 / 税务 / 费用与结算记录。
- 不清理、改派或硬删除任何历史证据；不解析、抓取或回填单证既有的「附件说明 / 存放位置」自由文本。
- 被拒绝的读取 / 候选 / 摘要 / 详情 / 下载 / 上传 / 作废不改写任何行，也**绝不**读取或写入任何内容字节。
- 仅使用隔离的非生产内容存储；生产对象存储（OSS）的凭据、桶配置、迁移与激活仍属生产 OSS Human Gate。

## 5. 并发

- **竞态一**：两条独立连接并发以越界客户伪造上传 → 二者都 fail closed、零落库、零落盘。
- **竞态二**：越界证据下载（拒绝）与本人证据作废（允许）并发 → 越界始终拒绝且不读取字节；本人作废生效，归属 Id 从未被改写（无撕裂状态）。
- 授权不复用缓存：菜单 / 账号状态 / 客户分配 / 归属改写的变更在下一次请求立即生效。

## 6. 测试证据

| 层 | 文件 | 覆盖 |
| --- | --- | --- |
| 单元（内存库） | `src/ERP.UnitTests/AttachmentOwnerAuthorizationTests.cs`（新增） | 缺失 / 非法 / 已删除 / 已禁用身份；无菜单与撤销菜单收敛；未分配客户 fail closed；两客户下台账 / 按归属 / 候选 / 摘要 / 详情 / 上传 / 下载 / 作废的越界拒绝；混合归属摘要不泄露范围外零计数；采购 / 验货权威归属两侧与无归属备货采购；单证 / 样品权威客户缺失；附件中心台账 / 摘要 / 详情 / 下载；**计数存储间谍**证明拒绝路径零字节；被拒请求不改写父单据 / 库存 / 财务；控制器契约（每个普通路由解析权威上下文、无 PUT / DELETE） |
| 单元（既有回归） | `AttachmentEvidenceTests.cs` / `TradeDocumentAttachmentEvidenceTests.cs` / `QualityInspectionSampleAttachmentEvidenceTests.cs` / `AttachmentCenterWorkspaceTests.cs` | 上传 / 格式 / 大小 / 命名 / 下载 / 作废 / 台账 / 摘要 / 候选 / 工作台与控制器契约全部保留；控制器身份播种为**实时启用**账号（系统内置角色 = 特权数据范围），工作台数据集访问次数改为「与页内行数无关」的恒定断言 |
| SQL Server 集成（受控 localdb） | `src/ERP.IntegrationTests/AttachmentOwnerAuthorizationSqlServerTests.cs`（新增） | 真实身份 / 菜单（撤销 / 禁用）；真实控制器的本人 / 越界与直接知道证据 Id；附件中心；采购 / 验货权威归属与特权历史；**两个独立连接竞态**；计数存储间谍；专用目标护栏 fail-closed |
| 安全 profile | `.ai/config.json` 的 `safe` | `dotnet restore` → Release 构建（`TreatWarningsAsErrors`）→ `ERP.UnitTests` 全量 |

### 6.1 SQL Server 集成目标护栏

- 实例必须精确为 `(localdb)\NEWERP_AutoAcceptance`，库名前缀必须为 `NEWERP_AUTOTEST`，且必须为 `Integrated Security`；不满足在任何库访问之前直接拒绝（`AssertDedicatedTarget`）。
- 每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings*.json` / `.env` / 生产凭据。
- 保留既有库存来源单据审计与原始失败日志：集成测试只新增自己的证据行，不清理 / 不删除任何既有行。

## 7. 已知限制 / 阻断

- 真实浏览器验收按任务配置为 `not_required`（未执行，也不声明通过）；附件证据界面不在本任务范围内改动。
- SQL Server 集成测试需要本机可用的 `(localdb)\NEWERP_AutoAcceptance` 实例；本机环境缺失时该文件不构成阶段验收证据（构建通过不等于阶段验收）。
- 存储层验收仍只覆盖隔离的非生产本地存储（测试内计数存储间谍 / 隔离本地存储），生产 OSS 未实现、未注册、未激活。
