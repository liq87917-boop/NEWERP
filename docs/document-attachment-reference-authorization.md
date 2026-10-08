# 业务单据附件引用登记授权说明（ERP-408）

- 任务：`ERP-408`「Protect business document attachment reference registration with live parent scope」
- 权威实现：
  - `src/ERP.Application/Services/AttachmentOwnerAuthorizationRules.cs`（**唯一权威口径**：复用 ERP-407 的实时身份 + 既有父单据模块菜单 + 当前客户数据范围，并扩展 `DocumentReferenceAccessContext` 与附件引用登记册的范围下推 / 权威父单据复核）
  - `src/ERP.Application/Services/DocumentAttachmentReferenceRules.cs`（父单据类型 → 既有模块菜单编码映射）
  - `src/ERP.Application/Services/DocumentAttachmentReferenceService.cs`（在每个服务入口把授权 / 范围前置到计数、分页、候选、精确读取与持久化之前）
  - `src/ERP.Api/Controllers/DocumentAttachmentReferenceController.cs`（每个路由在读取任何记录 / 计数 / 候选 / 父单据清单或写入之前解析权威访问上下文）

## 1. 授权口径

| 环节 | 口径 |
| --- | --- |
| 实时身份 | 每个路由在读取任何引用记录 / 计数 / 候选 / 父单据清单或写入之前重新解析身份：缺失 / 非法按未认证拒绝；账号不存在或已删除按未认证拒绝；账号已禁用按权限不足拒绝 |
| 既有菜单 | 父单据类型 → 既有父单据模块菜单：销售订单 `sales-order`、采购订单 `purchase-order`、装柜清单 `loading-list`、出口单证 `doc-center`。复用既有「角色 → 菜单」授权（忽略按钮型菜单与已删除记录），撤销后下一次请求立即收敛（无缓存），**不新增**任何菜单 / 角色 / 用户授权 |
| 客户范围 | 复用 ERP-097 `SalespersonDataScopeService` 唯一权威口径：受限账号按其登录名精确匹配员工编码（`IsSalesman == true`），可见客户 = `BaseCustomer.EmpId == 本人`；未映射业务员 / 未分配客户 = 空集合（fail closed，看不到任何客户） |
| 特权账号 | 超级管理员 / 系统内置角色 / 显式配置的特权角色保留既有全部客户可见性（含历史无主 / 已删除父单据），**但仍然受既有菜单限制**（菜单是唯一收敛维度） |
| 身份来源 | 只来自已认证请求主体（`ClaimTypes.NameIdentifier`）；请求体**不包含也不接受**任何账号 / 角色 / 范围字段；调用方的来源授权确认（`AuthorizedBy` / `SourceAuthorizationAcknowledged`）**不能**替代 ERP 授权 |
| 范围先于计数 | 台账 / 计数 / 分页 / 父单据候选 / 按父单据清单都把「父单据类型 + 客户范围」**下推到数据库**，绝不「先查全量再内存过滤」 |

## 2. 权威父单据归属（绝不推断）

| 父单据类型 | 权威归属字段 | 受限账号 fail closed 的情形 |
| --- | --- | --- |
| 销售订单 | 持久化 `SalesOrder.CustomerId` | 归属缺失（≤0）或越界 |
| 采购订单 | 持久化 `PurchaseOrder.OwningCustomerId` **与**按 `OwningSalesOrderId` 精确解析的来源销售订单客户（复用 ERP-371 既有口径） | 两侧任一侧越界；既无显式归属、也无权威来源的无归属备货采购 |
| 装柜清单 | 既有有效（启用、未删除）参与方客户集合 + 显式 `PreLoadingId → BookingId → 订柜客户`（复用 ERP-364 既有口径）；无有效参与方时按持久化 `CustomerId` | 任一有效参与方客户越界；显式上游订柜客户越界；两者都拿不到权威归属（无有效参与方且 `CustomerId ≤ 0`） |
| 出口单证 | 持久化 `TradeDocument.CustomerId` | 归属缺失（`CustomerId` 为空）或越界 |

一律**不**按父单据号码快照 `ParentNo`、自由文本、单证「附件说明 / 存放位置」或显示名推断归属；父单据缺失 / 已删除时受限账号同样 fail closed（历史引用不通过快照授予访问），特权账号保留既有历史可读性（标注归属不可用，绝不改派、绝不静默修复）。

## 3. 路由覆盖

| 路由 | 授权 + 范围 |
| --- | --- |
| `GET /api/document-attachment-references` | 台账范围在 `Count` 与分页之前下推 SQL（父单据类型 + 客户范围）；显式传入未授权类型返回**空页**（不披露该类型的记录与计数） |
| `GET /api/document-attachment-references/metadata` | 要求实时启用身份（模块元数据是静态口径，不读取任何记录 / 计数） |
| `GET /api/document-attachment-references/by-parent` | 父单据类型菜单授权 + 父单据权威归属客户范围（fail closed） |
| `GET /api/document-attachment-references/parent-options` | 父单据类型菜单授权 + 候选范围下推（受限账号无可见客户 → 空候选） |
| `GET /api/document-attachment-references/{id}` | 先按**持久化**父单据复核菜单授权 + 权威归属范围，未授权 / 范围外一律按「不存在」 |
| `POST /api/document-attachment-references`（登记） | 解析授权 → 父单据类型菜单 → 父单据存在性 → 原始权威父单据的**实时**归属范围，全部通过之后才持久化 |
| `POST /api/document-attachment-references/{id}/void` | 先按**持久化**父单据复核菜单授权 + 权威归属范围，通过之后才作废（只改状态与作废留痕） |

## 4. 边界（既有契约全部保留）

- 保留既有不透明引用标识（`ReferenceId`）口径、显示名 / 内容类型 / 字节数 / 校验和 / 备注有界校验、来源授权确认（`SourceAuthorizationAcknowledged` + `SourceAuthorizationNote`）、
  同一父单据（类型 + Id）+ 分类 + 引用标识的**有效身份唯一**规则、服务端权威写入的父单据号码 / 类型快照，以及**必须显式填写的作废原因**。
- 父单据删除后**保留历史行**（不重挂、不静默修复、不物理删除）；不可用归属对受限账号 fail closed。
- 未知父单据类型 / 不存在父单据 / 越界父单据给出**稳定且不披露存在性**的失败（授权不足或数据不存在，绝不回传范围外记录、计数或父单据清单）。
- 本登记册**只登记元数据**：不上传 / 下载 / 预览 / 抓取任何对象，不把引用标识变成可下载 URL，不做服务端抓取，不嵌入不可信标记。
- **不新增**文件存储 / 表 / 列 / 索引 / 菜单 / 角色 / 用户授权，**不新增**第二张附件表，不做任何库存 / 财务 / 出运 / 审批数据变更，也不改动任何报表入口开关。
- 被拒绝的登记 / 作废 / 读取 / 候选既不改写任何引用行，也绝不改写任何父单据。

## 5. 并发

- **竞态一**：两条独立连接并发以越界父单据伪造登记 → 二者都 fail closed、零落库。
- **竞态二**：越界引用作废（拒绝）与本人引用作废（允许）并发 → 越界始终拒绝且不产生任何写入；本人作废生效，父单据归属与类型从未被改写（无撕裂状态）。
- 授权不复用缓存：菜单 / 账号状态 / 客户分配 / 父单据归属改写的变更在下一次请求立即生效。

## 6. 测试证据

| 层 | 文件 | 覆盖 |
| --- | --- | --- |
| 单元（内存库） | `src/ERP.UnitTests/DocumentAttachmentReferenceAuthorizationTests.cs`（新增） | 缺失 / 非法 / 已删除 / 已禁用身份；无菜单与撤销菜单立即收敛；两客户下台账计数 / 父单据候选 / 详情 / 按父单据清单 / 登记 / 作废的范围收敛与拒绝零落库；四类父单据（含采购订单两侧归属与无归属备货采购、装柜清单参与方与显式上游订柜、出口单证客户缺失）；父单据删除后历史行保留且受限账号 fail closed、特权账号只读标注不可用；重复登记拒绝；显式作废保留原始证据；未知类型 / 不存在父单据的稳定失败；控制器契约（无 PUT / DELETE / PATCH，类级 `[Authorize]`） |
| 单元（既有回归） | `src/ERP.UnitTests/DocumentAttachmentReferenceTests.cs` | ERP-045 四种父单据快照 / 有界元数据 / 不安全引用 / 来源授权确认 / 作废与历史保留 / 有效身份唯一 / 台账过滤分页 / 候选 / 模块元数据 / 非变更边界 / 控制器与结构升级契约全部保留；控制器身份播种为**实时启用**账号（系统内置角色 = 特权数据范围） |
| SQL Server 集成（受控 localdb） | `src/ERP.IntegrationTests/DocumentAttachmentReferenceAuthorizationSqlServerTests.cs`（新增） | 真实身份 / 菜单（撤销 / 禁用）；真实控制器的本人 / 越界与直接知道父单据 / 引用 Id；四类父单据与共享柜 / 上游越界；拒绝零落库；授权登记 + 显式作废保留证据 + 重复登记拒绝 + 作废后重登记；**两个独立连接竞态**；专用目标护栏 fail-closed |
| 安全 profile | `.ai/config.json` 的 `safe` | `dotnet restore` → Release 构建（`TreatWarningsAsErrors`）→ `ERP.UnitTests` 全量 |

### 6.1 SQL Server 集成目标护栏

- 实例必须精确为 `(localdb)\NEWERP_AutoAcceptance`，库名前缀必须为 `NEWERP_AUTOTEST`，且必须为 `Integrated Security`；不满足在任何库访问之前直接拒绝（`AssertDedicatedTarget`）。
- 每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings*.json` / `.env` / 生产凭据。
- 保留既有库存来源单据审计与原始失败日志：集成测试只新增自己的引用行，不清理 / 不删除任何既有行。

## 7. 已知限制 / 阻断

- 真实浏览器验收按任务配置为 `not_required`（未执行，也不声明通过）；附件引用界面不在本任务范围内改动。
- SQL Server 集成测试需要本机可用的 `(localdb)\NEWERP_AutoAcceptance` 实例；本机环境缺失时该文件不构成阶段验收证据（构建通过不等于阶段验收）。
- 本模块仍只覆盖**元数据引用登记**：未实现任何文件上传 / 下载 / 预览 / 抓取通道，也未新增对象存储集成（属另行人工审批的活动）。
