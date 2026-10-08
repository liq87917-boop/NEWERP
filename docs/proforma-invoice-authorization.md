# 形式发票 PI 实时授权、权威客户范围与转换护栏说明（ERP-398）

- 任务：`ERP-398`「Enforce live PI permissions and customer scope through sales order conversion」
- 权威实现：
  - `src/ERP.Application/Services/ProformaInvoiceAuthorizationRules.cs`（纯规则 + 实时授权 + 权威客户范围 + 转换护栏）
  - `src/ERP.Api/Controllers/ProformaInvoiceController.cs`（列表 / 详情 / 新增 / 修改 / 提交 / 审核 / 销审 / 取消 / 作废 / 删除 / 批量删除 / 打印 / 带入预填 / 转销售订单完整覆盖）
  - `src/ERP.Api/Controllers/SalesOrderConversion.cs`（`EnsureConversionScopeAuthorized`：来源 / 目标客户范围守卫）

## 1. 授权口径

| 环节 | 口径 |
| --- | --- |
| 实时身份 | 每个路由在读取任何计数 / 生成单据号 / 写入之前重新解析身份：缺失 / 非法按未认证拒绝；账号不存在或已删除按未认证拒绝；账号已禁用按权限不足拒绝 |
| 既有菜单 | 复用已部署的「形式发票 PI」（`proforma-invoice`）菜单：非特权账号必须显式具备，撤销后下一次请求立即收敛（无缓存） |
| 转换菜单 | PI → 销售订单（带入预填 / 直接生成）额外要求非特权账号具备既有「销售订单」（`sales-order`）菜单授权 |
| 业务员映射 | 复用 ERP-097 `SalespersonDataScopeService` 唯一权威口径：受限账号按其登录名精确匹配员工编码；未映射业务员的受限账号 **fail closed**，绝不降级为全局 / 管理员可见 |
| 特权账号 | 超级管理员 / 系统内置角色 / 显式配置的特权角色保留既有全部访问（含历史无主行） |
| 身份来源 | 只来自已认证请求主体（`ClaimTypes.NameIdentifier`）；请求体**不包含也不接受**任何账号 / 角色 / 范围字段，`null` 范围只表示进程内调用，**绝不代表匿名或管理员** |

## 2. 权威客户归属

- **PI**：只按持久化 `ProformaInvoice.CustomerId` 判定；**绝不**按 `PiNo` / `CustomerName` / `QuotationNo` 等自由文本推断。
  - 读取（详情 / 打印 / 预填）：受限账号 `CustomerId` 为空 / 非正（无主行）或不在范围内 → 按「不存在」拒绝（不泄露存在性）。
  - 写入（新增 / 修改 / 状态流转 / 批量）：受限账号缺失归属或越界 → 权限不足拒绝（fail closed）。
  - 特权账号：保留历史访问。
- **列表 / 计数**：范围在 **`Count` 与分页之前**下推 SQL（`ScopeFilter` / `ApplyScope`），绝不「先查全量再内存过滤」，也绝不泄露范围外 PI 的计数、明细或打印内容。
- **修改**：同时复核**已存客户**（`existing.CustomerId`）与**拟议客户**（请求体 `CustomerId`）两侧；任一侧越界 / 无主即拒绝，绝不改写任何字段。
- **转换来源 / 目标**：来源 PI 客户与目标销售订单客户**独立**复核；来源可见**绝不**授予目标客户权限（`EnsureSourceCustomerInScope` / `EnsureTargetCustomerInScope`）。

## 3. 路由覆盖

| 路由 | 授权 + 范围 |
| --- | --- |
| `GET /api/sales/proforma-invoices` | 身份 / 菜单门 + 列表范围下推（计数 / 分页之前） |
| `GET /api/sales/proforma-invoices/{id}` | 身份 / 菜单门 + 已存储 `CustomerId` 范围复核 |
| `POST /api/sales/proforma-invoices` | 身份 / 菜单门 + 拟议客户实时范围（**先于**单号生成与落库） |
| `PUT /api/sales/proforma-invoices/{id}` | 身份 / 菜单门 + 已存储范围 + 拟议范围复核后改写 |
| `POST /api/sales/proforma-invoices/{id}/submit` | 身份 / 菜单门 + 已存储范围复核后流转 |
| `POST /api/sales/proforma-invoices/{id}/approve` | 身份 / 菜单门 + 已存储范围复核后审核 |
| `POST /api/sales/proforma-invoices/{id}/unaudit` | 身份 / 菜单门 + 已存储范围复核后销审 |
| `POST /api/sales/proforma-invoices/{id}/cancel` | 身份 / 菜单门 + 已存储范围复核后取消 |
| `POST /api/sales/proforma-invoices/{id}/void` | 身份 / 菜单门 + 已存储范围复核后作废 |
| `DELETE /api/sales/proforma-invoices/{id}` | 身份 / 菜单门 + 已存储范围复核后软删除 |
| `POST /api/sales/proforma-invoices/batch-delete` | 身份 / 菜单门 + 逐行范围复核（任一行越界 / 无主即整体拒绝、无部分删除） |
| `GET /api/sales/proforma-invoices/{id}/print` | 身份 / 菜单门 + 已存储范围复核（含明细） |
| `GET /api/sales/proforma-invoices/{id}/order-prefill` | PI 菜单 + 销售订单菜单 + 来源 / 目标范围复核（不落库、不占号） |
| `POST /api/sales/proforma-invoices/{id}/to-order` | PI 菜单 + 销售订单菜单 + 来源 / 目标范围复核后生成 |

## 4. 边界

- 不新增菜单 / 角色 / 用户授权，不新增表 / 列 / 索引，不把空身份当作管理员，不伪造任何授权。
- 不改写 PI 编号 / 状态 / 明细行 / 金额 / 合计 / 定金 / 币种 / 汇率 / 单位等原始商业语义，保留既有服务端计算与转换资格。
- 不改写来源报价单、销售订单、客户主数据、库存与库存流水；保留库存来源单据审计与原始失败日志，不删除历史证据。
- 被拒绝的读取 / 打印 / 新增 / 修改 / 状态流转 / 删除 / 批量删除 / 转换绝不消耗单据号、绝不改写任何行与审计。

## 5. 并发与原子性

- **批量删除**：先按 Id 取出全部未删除 PI 的权威归属投影（`LoadOwnershipAsync`），逐行复核范围；任一行越界 / 无主 / 不存在即整体拒绝，未做任何部分删除；全部通过后才一次性软删除。
- **修改**：先复核「已存」PI 的权威归属，再校验「拟议」客户；失败时永不执行字段改写，原行不变。
- **转换**：来源 / 目标范围与销售订单菜单授权均在消耗单号与落库之前复核，拒绝时不写任何订单、不改来源 PI 状态。
- **竞态**（真实 SQL 集成测试）：两独立连接并发伪造新增越界客户 PI 二者都拒绝、零落库；同一 PI 上「改写到越界客户」与「合法删除」并发时改写始终拒绝、删除生效，归属从未被改写（无撕裂状态）。

## 6. 测试证据

| 层 | 文件 | 覆盖 |
| --- | --- | --- |
| 单元（内存库） | `src/ERP.UnitTests/ProformaInvoiceAuthorizationTests.cs`（新增） | 身份 / 账号状态 / PI 菜单 / 未映射业务员 fail closed 与撤销收敛；范围下推（他人 / 无主不泄露）；新增 / 修改（已存 + 拟议）/ 状态流转 / 批量删除的越界拒绝与零副作用；转换来源-only / 目标-only 拒绝与合法放行（保留原金额 / 币种 / 留痕）；完整覆盖与既有菜单编码接线契约 |
| 单元（既有回归） | `ProformaInvoiceControllerTests.cs` / `SalesOrderConversionTests.cs` / `QuotationConversionScopeTests.cs` | 既有 PI 创建 / 详情 / 分页 / 修改 / 状态流转 / 打印与转换映射、金额复核、重复守卫保持通过；夹具统一使用真实特权身份（系统内置角色） |
| SQL Server 集成（受控 localdb） | `src/ERP.IntegrationTests/ProformaInvoiceAuthorizationSqlServerTests.cs`（新增） | 真实身份 / 菜单（撤销 / 禁用）；真实控制器下的本人 / 他人 / 无主；伪造新增 / 修改 / 批量删除整体拒绝；来源-only / 目标-only 转换拒绝与双权限合法放行；**两个独立连接竞态**；专用目标护栏 fail-closed |
| 安全 profile | `.ai/config.json` 的 `safe` | `dotnet restore` → Release 构建（`TreatWarningsAsErrors`）→ `ERP.UnitTests` 全量 |

### 6.1 SQL Server 集成目标护栏

- 实例必须精确为 `(localdb)\NEWERP_AutoAcceptance`，库名前缀必须为 `NEWERP_AUTOTEST`，且必须为 `Integrated Security`；不满足在任何库访问之前直接拒绝（`AssertDedicatedTarget`）。
- 每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
- 连接串只来自进程环境变量 `ERP_ConnectionStrings__Default` 或专用 localdb 默认值，绝不读取 `appsettings*.json` / `.env` / 生产凭据。

> 构建完成不等于阶段验收：只有受控 localdb 上真实执行通过、或给出准确 blocker，才构成阶段验收证据。真实浏览器验收按任务配置为 `not_required`（未执行，也不声明通过）。
