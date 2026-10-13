# 询价报价响应时间工作台（ERP-104）

## 1. 目标

给每张询价单提供一个**只读**的「报价响应时间」视角：询价日期到首张有效报价日期之间的日历天间隔，并把缺失 / 异常证据显式标注出来。用于回答「这张询价单过了多久才被报价」这类运营问题，**不**用于成交 / 交期 / 绩效 / 对账等结论。

## 2. 口径（与 `InquiryResponseRules` 一致，唯一权威）

| 规则 | 说明 |
|---|---|
| 显式链接 | 只按报价单上持久化的 `Quotation.InquiryId == Inquiry.Id` 建立链接；**绝不**按单号 / 文本 / 金额 / 相似度推断链接 |
| 版本链根单 | 只取 `RootQuotationId == null` 的初始版本（根单）；版本（`RootQuotationId != null`，且版本创建时复制了 `InquiryId`）**不重复计入** |
| 软删除 | 软删除的询价单与报价单一律排除，不参与响应计数 |
| 首张有效报价日期 | 显式链接且报价日期有效（年份 ≥ 1900，即非 `DateTime` 默认缺失值）的根单报价日期中的**最早者** |
| 间隔天数 | `(报价日期 − 询价日期)` 的日历天（按 `.Date` 截断到天） |
| 缺失 / 异常 | 缺失链接、缺失日期、报价日期早于询价日期（负间隔）分别作为独立证据标注，**绝不推断为已报价** |

## 3. 响应状态

| 状态 | 编码 | 级别 | 判定 |
|---|---|---|---|
| 已报价 | `quoted` | `success` | 存在首张有效报价日期且间隔 ≥ 0 |
| 未报价 | `unquoted` | `neutral` | 无有效报价单链接，或链接存在但报价日期缺失 / 无效 |
| 链接异常 | `inconsistent-link` | `warning` | 报价日期早于询价日期（间隔为负） |

## 4. 接口

`GET /api/inquiries/response-times`（类级 `[Authorize]`，JWT）

查询参数（全部可选）：

| 参数 | 说明 |
|---|---|
| `page` / `pageSize` | 分页（默认 20，单次上限 200），按**稳定询价单 Id 升序**分页 |
| `startDate` / `endDate` | 询价日期区间（含，按日期比较） |
| `customerId` | 客户 Id（显式等值；空值 = 不过滤） |
| `keyword` | 只命中询价单号 |

返回 `ApiResponse<InquiryResponseWorkspaceDto>`：`items`（行）、`total`、`page`、`pageSize`，以及 `readOnlyText` / `boundaryText` / `disclaimerText` 口径文案。

数据范围：与询价单列表同口径，复用 `SalespersonDataScopeService`（特权账号不过滤；受限制业务员仅可见其分配客户）。

## 4.1 实时授权（ERP-500，fail closed）

本工作台是与 `api/inquiries` **并列**的独立路由。此前控制器只有 `[ApiController]` / `[Route(...)]` / `[Authorize]`，
`GetPaged` 只解析业务员数据范围，**未**复核实时身份、账号状态或任何菜单，因此任何已认证账号（含禁用账号、
或持有系统角色但无询价单菜单的账号）都能经由这条兄弟路由枚举整张询价单台账。

本任务为这条并列路由补上**与 `api/inquiries` 完全同源**的实时授权（直接复用
`InquiryAuthorizationRules.EnsureAuthorizedAsync`，不复制判定逻辑、不新增任何权限模型）：

| 场景 | 判定结果 |
|---|---|
| 无身份 / 非法 / 零 / 负数用户 Id | `2000` 未认证 |
| 账号不存在或已删除 | `2000` 未认证 |
| 账号已禁用 | `2002` 权限不足 |
| 缺少既有 `inquiry` 菜单授权（含被撤销） | `2002` 权限不足 |
| 具备既有 `inquiry` 菜单（+ 映射业务员） | 放行既有只读契约 |

- 判定在**读取任何 `Inquiries` 计数或行之前**完成，并把权威客户数据范围（`InquiryAuthorizationRules.ApplyScope`）
  下推到 `Count` 与分页之前的 SQL；被拒绝的请求零计数、零行读取、零写入。
- **与请求形状无关**：判定不依赖 `Request.Path`、环境或是否绑定 `HttpContext`（空路径与已赋值路径口径一致，
  未绑定 `HttpContext` 的进程内直调同样执行护栏）。
- **不新增**任何表 / 列 / 索引 / 菜单 / 角色 / 用户授权；**无匿名 / 管理员 / 环境 / 测试专用兜底**，
  绝不把空身份当作管理员。
- 复用既有「角色 → 菜单」口径（`CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync`）：
  **每次请求重新查询**，账号停用 / 删除或菜单撤销后下一次请求立即收敛（绝不缓存）。
- 复用既有规则的特权口径（与 `api/inquiries` 完全同源，ERP-402 / ERP-097）：系统内置角色等特权账号保留既有
  全部访问；非特权账号必须显式具备 `inquiry` 菜单授权并映射业务员。**本任务不新增任何特权 / 管理员 / 环境 / 测试兜底。**

## 5. 只读保证

控制器与查询全部使用 `AsNoTracking`，无 `Add / Update / Remove / SaveChanges`，不新建任何表 / 列，不改写询价单 / 报价单 / 客户主数据。单页内客户名称与显式链接的根单报价日期通过**批量查询**装载（无逐行查库）。

## 6. 前端

- 脚本：`src/ERP.Api/wwwroot/js/inquiry-response-workspace.js`（`index.html` 引入）。
- 入口：询价单模块（`modules-doc.js` 的 `'inquiry-new'`）工具栏「⏱️ 报价响应时间」（`openInquiryResponseWorkspace()`）。
- 工作台页：日期区间 / 客户 / 关键字筛选 + KPI + 分页表格（询价单号 / 询价日期 / 客户 / 状态 / 首张有效报价日期 / 间隔天数 / 响应状态 / 证据说明）+ 口径 / 只读 / 免责文案。

## 7. 边界与免责

响应时间只是日期算术证据：不代表报价是否成交、不代表承诺交期或履约，不是 SLA / 绩效结论，也不是客户对账 / 结算 / 应收应付结论。

## 8. 验证

- 单元测试：`src/ERP.UnitTests/InquiryResponseWorkspaceTests.cs`（分页、日期 / 客户 / 关键字筛选、显式链接、版本链、缺失日期、负间隔、业务员数据范围、软删除、只读不写库、前端接线契约，以及 ERP-500 实时授权：缺失 / 非法 / 未知 / 已删除身份按未认证、禁用与缺菜单 / 撤销菜单按权限不足且零 `Inquiries` 读取、无 `HttpContext` 与空 / 已赋值 `Request.Path` 口径一致、授权身份保留既有读契约与源码契约）。
- 真实 SQL 集成测试：`src/ERP.IntegrationTests/InquiryResponseWorkspaceSqlServerTests.cs`（真实控制器 + 真实既有授权；GUID 独占 `NEWERP_AUTOTEST` 目标库；拒绝矩阵 / 撤销收敛 / 种子管理员与已授予既有菜单的操作员放行 / 请求路径与 `HttpContext` 无关；目标库护栏在访问之前拒绝非专用目标）。
- `validation_profile: safe`（Release 构建 + `ERP.UnitTests`）；真实 SQL 仅在专用 localdb 实例 `(localdb)\NEWERP_AutoAcceptance` 上以全新 GUID 库执行；构建通过不等于阶段验收通过。
