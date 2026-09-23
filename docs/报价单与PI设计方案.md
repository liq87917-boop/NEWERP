# 报价单（Quotation）与形式发票（PI）设计方案

> 版本：v1.2（2026-09-23）· 状态：**批次 1 ~ 批次 3 已实施**（批次 2 形式发票 PI 闭环由 **ERP-007** 交付；批次 3 报价 / PI → 销售订单由 **ERP-010** 交付；剩余缺口「共享打印三件套注册 / 有效期治理 / 成交率报表」由 **ERP-018** 交付）· 适用范围：WMERP 外贸 ERP（NEWERP）· 补正任务：**ERP-017**（文档补正）、**ERP-018**（打印注册与有效期治理）
>
> **实现状态图例（唯一口径，全文一致）**
>
> | 标记 | 含义 |
> |---|---|
> | ✅ **已实现** | 实体 + 持久化 + API + 页面/菜单齐备，并有单元测试或交付验证记录 |
> | 🧪 **浏览器验收延后** | 代码与测试已就绪，但真实 Edge 验收按 `completion_policy.defer_browser_during_development` 延后到 **FINAL-UI-ACCEPTANCE** 阶段，状态记 `browser_deferred`（**不等于失败**，也不等于已人工验收） |
> | ⏳ **未实现（已立项）** | 缺口明确且已排入任务队列（如 **ERP-018**、**ERP-019**），本轮**无**对应代码 |
> | ❌ **未实现（未立项）** | 已知缺口，尚无任务 |
>
> 说明：截至 2026-09-23，本链路中只有 **ERP-007**（PI 闭环）完成过真实 Edge 验收（6/6 通过、14 张截图）；**ERP-008 / 009 / 010** 的浏览器验收均为延后状态。

---

## 1. 为什么需要这两个单据

当前业务链已具备：**询价单（Inquiry）→ 销售订单（SalesOrder）→ 出运单据 → 结算**，缺链路上前后两个关键环节：

| 环节 | 现状 | 业务痛点 |
|---|---|---|
| 客户来询价 → 我方报价 | 只有「询价单」记录客户需求，**报价没有正式单据** | 报价靠 Excel / 微信，价格版本无从追溯；客户砍价时找不到上次报价依据 |
| 报价确认 → 客户开 PI 打定金 | **没有 PI** | PI 是外贸收定金/开信用证的标准凭据，目前手工做 Excel，格式不统一、银行信息易错 |
| PI → 正式销售订单 | 手工重新录一遍 | 重复录入，PI 数量与订单数量对不上时无法核对 |

结论：**报价单 + PI 是把「询价 → 订单」补全的低风险、高收益环节**，且不触碰任何现有单据的存储过程。

---

## 2. 设计决策（关键）

| 决策点 | 选择 | 理由 |
|---|---|---|
| 数据访问方式 | **EF Core 主子表 + 显式事务**（Controller 内一次 SaveChanges） | 现有销售订单走存储过程 `sp_Biz_*`，改动风险高；新单据独立实现，**完全不触碰现有 SP** |
| 单据编号 | 复用现有**单据字轨**（`SysBillRules` + `DocumentType`） | 与全系统编号规则统一，可在「单据编号规则」页自助配置前缀/日期格式/流水位数 |
| 枚举扩展 | `DocumentType` 新增 `Quotation = 17`、`ProformaInvoice = 18` | 现枚举最大 16（Complaint），只追加不修改，向后兼容 |
| 主子表结构 | 与 `Inquiry` / `InquiryDetails` 同构（主表存客户 + 汇总，明细存商品行） | 便于现有报表/打印习惯复用 |
| 打印 | 复用「打印预览 / 直接打印 / 打印设计」（`bill-print.js` + `print-design.js`） | 零新增打印框架，模板可自助设计 |
| 审核流 | `草稿 → 已审核`；审核后可「转 PI / 转销售订单」 | 与现有单据状态语义一致 |
| 多币种 | 主表 `Currency` + `ExchangeRate` + `TotalAmountCny` | 与「费用单」的多币种处理一致 |

---

## 3. 数据模型（批次 1 已实现 = 报价单）

### 3.1 报价单 `Quotations`

| 字段 | 类型 | 说明 |
|---|---|---|
| `Id` / 审计列 | — | `BaseEntity`（CreatedAt/By、UpdatedAt/By、IsDeleted、RowVersion） |
| `QuotationNo` | nvarchar(50) | 报价单号（字轨生成，如 `QT2609180001`） |
| `QuotationDate` | datetime2 | 报价日期 |
| `ValidUntil` | datetime2? | **报价有效期**（到期可提醒客户） |
| `CustomerId` / `CustomerName` | bigint? / nvarchar | 客户（`REF_APIS.customer` 选择框） |
| `ContactPerson` / `Tel` / `Email` | nvarchar | 对接口径，转 PI / 发邮件时用 |
| `InquiryId` / `InquiryNo` | bigint? / nvarchar | 来源询价单（可空，支持直接报价） |
| `TradeTerms` | nvarchar | 贸易术语（FOB / CIF / EXW…） |
| `PortOfLoading` / `PortOfDestination` | nvarchar | 起运港 / 目的港 |
| `PaymentTerms` | nvarchar | 付款方式（T/T 30% 定金… / L/C） |
| `LeadTime` | nvarchar | 交货期（如 "35 days after deposit"） |
| `Currency` / `ExchangeRate` | nvarchar(10) / decimal | 币种 / 折算汇率 |
| `TotalAmount` / `TotalAmountCny` | decimal | 报价总额（原币 / 折人民币） |
| `SalesmanId` / `SalesmanName` | bigint? / nvarchar | 业务员 |
| `Status` | nvarchar | 草稿 / 已审核 / 已转 PI / 已转订单 / 已作废 |
| `Remark` | nvarchar | 备注 |

### 3.2 报价明细 `QuotationDetails`

| 字段 | 类型 | 说明 |
|---|---|---|
| `QuotationId` / `QuotationNo` | bigint / nvarchar | 主表外键 + 冗余单号（报表免关联） |
| `LineNo` | int | 行号 |
| `ProductId` / `ProductCode` / `ProductName` | bigint? / nvarchar | 商品（`REF_APIS.product`） |
| `Spec` / `Unit` | nvarchar | 规格 / 单位 |
| `Quantity` / `UnitPrice` / `Amount` | decimal | 数量 / 单价（原币）/ 金额 = 数量 × 单价 |
| `Moq` | nvarchar | 起订量说明（饰品/文具常见：500 pcs/款） |
| `Remark` | nvarchar | 备注（色号、包装等） |

### 3.3 形式发票 `ProformaInvoices` + `ProformaInvoiceDetails`（批次 2 · 已实现）
> 已实现：主子表建表（`SchemaUpgrader` 幂等）、`/api/sales/proforma-invoices` 增删改查 + 审核 / 销审 / 作废、
> 报价单 `POST /api/sales/quotations/{id}/to-pi`（同一报价单仅可转一次）、PI 打印预览与「样式设计」模板接入、
> `proforma-invoice` 菜单与 `PI` 字轨；银行信息默认取系统参数 `PI_BankInfo`，收货人 / 通知人 / 唛头取客户资料默认值。
主表在报价单字段基础上替换/新增：

| 字段 | 说明 |
|---|---|
| `PiNo` / `PiDate` | PI 号（如 `PI2609180001`）/ PI 日期 |
| `QuotationId` / `QuotationNo` | **来源报价单**（由报价单转 PI 时回填） |
| `BankInfo` | **银行信息（多行文本：Beneficiary / Bank / Account / SWIFT）** ← 独立字段 + 打印模板固定区 |
| `NotifyParty` / `Consignee` | 收货人 / 通知人（PI 必备） |
| `ShippingMarks` | 唛头 |
| `DepositRatio` / `DepositAmount` | 定金比例（%）/ 定金金额（比例自动带出，允许手改） |
| `Status` | 草稿 / 已审核 / 已转销售订单 / 已作废 |

---

## 4. 单据编号（字轨）

| DocumentType | 值 | 前缀 | 示例 | 备注 |
|---|---|---|---|---|
| `Quotation` | **17** | `QT` | `QT2609180001` | 报价单（可在「单据编号规则」页自助改前缀） |
| `ProformaInvoice` | **18** | `PI` | `PI2609180001` | 形式发票 |

初始化脚本**幂等插入**这两条规则（已存在则不动），并同步枚举。

---

## 5. 接口设计（RESTful，与现有风格一致）

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/sales/quotations?page=&pageSize=&keyword=&customerId=&status=` | 分页查询 |
| GET | `/api/sales/quotations/{id}` | 主表 + 明细（编辑页一次取回） |
| POST | `/api/sales/quotations` | 新增（主表 + 明细一次事务保存，金额后端复核） |
| PUT | `/api/sales/quotations/{id}` | 修改（事务内先删明细再插入） |
| POST | `/api/sales/quotations/{id}/audit` / `unaudit` | 审核 / 销审 |
| POST | `/api/sales/quotations/{id}/to-pi` | **转为 PI**（复制主表 + 明细，回填来源报价单号，原单状态改「已转 PI」） |
| GET | `/api/sales/quotations/{id}/order-prefill` | **带入预填销售订单**（ERP-010）：返回未落库的销售订单草稿（不占用单号、不写库），前端切到「销售订单 → 新增」表单继续编辑后按 `/api/sales-orders` 保存 |
| POST | `/api/sales/quotations/{id}/to-order` | **转为销售订单**（ERP-010）：按已审核报价单生成一张销售订单（EF 主子表路径），来源留痕，同一报价单仅一张 |
| GET | `/api/sales/quotations/{id}/print` | 打印数据（主表 + 未删除明细，按 `SortNo` 排序）：**打印预览 / 直接打印 / 打印设计共用**，与报价单工作流保存的数据同源（服务端不重算、不落库，打印件与页面逐字一致） |
| GET | `/api/sales/quotations/validity-due?asOfDate=&aheadDays=7` | **有效期到期提醒**（ERP-018）：返回已过期与提醒窗口内到期的报价单（已作废、未设置有效期的不提醒），按紧急度（已过期 &gt; 今日到期 &gt; 即将到期）排序；口径见 §10.2 |
| POST | `/api/sales/proforma-invoices/...` | PI 同上一套；`POST /{id}/to-order`（PI → 销售订单）、`GET /{id}/order-prefill`（带入预填）与报价单同口径 |
| GET | `/api/reports/quotation-conversion?start=&end=` | **报价成交率分析**（ERP-018，报表框架 `ReportController`）：按业务员聚合，计算口径见 §10.3 |

---

## 6. 前端

| 项 | 做法 |
|---|---|
| 菜单 | 「询报价」分组下新增 **报价单**、**形式发票 PI**（`SchemaUpgrader` 幂等插入 + 自动授权；生产上线脚本 `deploy/init17.sql` 属受门禁任务，单独产出） |
| 列表 | `modules.js` 新增 `quotation`、`proforma-invoice`（列 = 单号 / 日期 / 客户 / 有效期 / 币种 / 金额 / 业务员 / 状态） |
| 编辑 | 复用主子表单据编辑（明细表格：加行、删行、`数量 × 单价 = 金额`、主表合计自动汇总；后端复核防篡改） |
| 打印 | **ERP-018 已落地**：`bill-config-ef.js` 把 `quotation` / `proforma-invoice` 注册进共享 `BILL_CONFIG`（`ef: true` + `api` + `noKey` + 打印字段中文标签），行操作提供「打印预览 / 直接打印 / 打印设计」（`sales-pi.js` 的 `previewSalesDocPrint` / `printSalesDocDirect` / `designSalesDocPrint` 共用 `GET {api}/{id}/print` 与打印模板）；两类单据**不加入** `BILL_CODE_MAP`（该映射把菜单码路由到存储过程版单据页，EF 单据挂进去会让菜单跳到 SP 页） |
| 有效期治理 | 列表新增「有效期状态」派生列（`crud.js` 列 `render` 回调 + `sales-pi.js` 的 `quotationValidityBadge`，与后端 `QuotationValidityRules` 同口径）；工具栏「⏰ 有效期提醒」弹窗调用 `GET /api/sales/quotations/validity-due`，按紧急度列出已过期 / 今日到期 / 即将到期的报价单（窗口 7/15/30 天可切换） |
| 报表入口 | 工具栏「📈 成交率报表」直接渲染报表框架中的「报价成交率分析」（与报表中心同一套渲染与 CSV 导出）；因报表中心菜单由数据库菜单表驱动（本任务不改菜单结构），入口统一放在报价单页 |
| 便捷操作 | 报价单列表行操作：**转 PI**、**预填销售订单 / 转销售订单**（ERP-010）；PI 列表行操作：**预填销售订单 / 转销售订单**；均由 `rowActions` 机制渲染（`crud.js` 的「更多」菜单） |

---

## 7. 流转关系

```
客户询价 ──► 询价单 Inquiry ──「带入明细」──► 报价单 Quotation ──「转 PI」──► 形式发票 PI ──「转销售订单」──► 销售订单
                 │                              │
                 │                              └── 报价有效期到期 ──► 建议加「报价到期提醒」报表（第三批）
                 └── 无询价单也可直接开报价单（InquiryId 为空）
```

| 批次 | 内容 |
|---|---|
| **1（已完成）** | 报价单：主子表 + 审核 / 销审 + 打印预览与打印设计 + 从询价单带入明细 + 行操作「转 PI」 |
| **2（已完成 · ERP-007）** | PI：主子表 + 银行信息（系统参数默认，单据可覆盖）+ 收货人 / 通知人 / 唛头 + 定金比例与金额 + 审核 / 销审 / 作废 + 打印预览 + 报价单转 PI |
| **3（已完成 · ERP-010）** | 报价单 / PI → 销售订单：**带入预填**（打开销售订单新增表单，可编辑后再保存）+ **直接生成**（服务端守卫，同一来源仅一张，来源报价单 / PI 留痕）；明细数量 / 单价 / 金额 / 合计与定金由服务端复核 |
| **3（已完成 · ERP-018）** | 报价单 / PI **共享打印三件套**（注册进 `BILL_CONFIG`：打印预览 / 直接打印 / 打印设计）、**有效期到期提醒**（列表徽标 + ⏰ 弹窗 + `validity-due` 接口）、**报价成交率报表**（`/api/reports/quotation-conversion`，按业务员聚合）全部落地；浏览器验收按开发阶段策略记 `browser_deferred` |

---

## 8. 影响面与风险控制

| 项 | 说明 |
|---|---|
| 只新增，不改旧 | 新增表 / 菜单 / 字轨规则；**不修改任何现有表结构与存储过程** |
| 脚本幂等 | 每个 `initN.sql` 可重复执行；schema 自动探测（兼容 `dbo` 与 `db_owner`） |
| 上线前快照 | 沿用 `backup\WMERP_Data-snapshot-*` 方式，保留回滚脚本 |
| 解耦 | 报价单 / PI 停用不影响询价与订单流程 |
| 每批交付验收 | `dotnet build -c Release`（含分析器，0 警告 0 错误）→ 单元测试全绿（批次 2 / ERP-007 交付实测 **217/217**）→ 幂等升级只在专用测试库（NEWERP_TEST）执行 → 写入回读零差异 → 重新打包 rN |

### 8.1 真实 Edge 验收的目标库护栏（ERP-007 更新 · 2026-09-23）

| 项 | 说明 |
|---|---|
| 护栏文件 | `src/ERP.IntegrationTests/TestDatabaseSafetyGuard.cs`（由 `UiTestFixture` / `IntegrationTestFixture` 在启动 `ERP.Api` 前调用）：不满足下述判定即抛 `InvalidOperationException` 中止，**不启动 API、不写任何数据**（fail-closed；只输出环境变量名与判定结论，绝不输出连接串内容） |
| 判定①：目标连接串有效 | `ERP_ConnectionStrings__Default` 必须同时含 `Server`/`Data Source` 与 `Database`/`Initial Catalog`；缺失或不可解析立即中止（无法确认写入目标） |
| 判定②：显式测试上下文 | 满足其一即放行：① `ERP_AI_TEST_RUN=1` 且 `ASPNETCORE_ENVIRONMENT=Development`（`scripts/ai_browser_acceptance.py` 启动验收进程时设置）；② `ERP_AI_ALLOW_HIGH_RISK_TESTS=APPROVED`（Human Gate 第二层开关）；③ CI 测试环境（`CI=true` / `GITHUB_ACTIONS=true`，`erp-integration` / `erp-ui` 只注入测试环境密钥）。三者皆不满足立即中止 |
| 不再使用部署配置做数据库身份黑名单 | 开发阶段 `deploy/appsettings*.json` 不是生产权威；本机开发库与部署配置同实例 + 同库是当前开发环境的既定事实，护栏不得据此单独拒绝验收（依据 `.ai/prompts/developer.md` 第 8 条；原先的 `deploy/appsettings.*.json` 比对逻辑已移除） |
| 本机 ad-hoc 运行 | 直接 `dotnet test --filter Collection=UiTests` 且未设置上述变量时立即 fail-closed，防止把建表 / 菜单授权 / 系统参数 / 测试单据写入非测试库 |
| 建议的隔离做法（可选） | 把 `.env.local` 的 `ERP_ConnectionStrings__Default` 指向专用测试库（例如本机 `NEWERP_TEST`）；验收在显式测试上下文下照常放行 |
| 护栏单元测试 | 判定逻辑抽为纯函数 `TestDatabaseSafetyGuard.Evaluate(...)`，由 `src/ERP.UnitTests/TestDatabaseSafetyGuardTests.cs` 覆盖每条分支（`ERP.UnitTests` 现为 **217/217** 全绿） |
| UI 用例筛选 | `Collection=UiTests` 必须同时是 xUnit **用例属性**：`[Trait("Collection","UiTests")]`。仅写 `[Collection(...)]` 不会生成 VSTest 属性，`--filter` 会静默选中 0 个用例并以退出码 0“通过”（表现为 0 张截图、TRX `total=0`） |
| attempt 3 实测（合规，2026-09-23） | 以**专用本地测试库** `(localdb)\MSSQLLocalDB` / `NEWERP_TEST` 跑真实 Edge：`--filter Collection=UiTests` → **6/6 通过**（`PiWorkflowUiTests` 2 例：报价单转 PI 回填核对、PI 草稿修改/审核禁改/销审恢复/打印预览/作废；`UiSmokeTests` 4 例登录态），产出 **14 张 PNG** + `browser-session.json`（Microsoft Edge `153.0.4234.48`，`realBrowser=true`）+ TRX `passed="6" failed="0"`；注入仅在验收进程内（`ERP_ConnectionStrings__Default` + 临时 `ERP_Jwt__Key`），未改 `.env.local`、未设置 `ERP_AI_ALLOW_HIGH_RISK_TESTS`。因此实现侧验收标准已达成。**attempt 2 复核**：在新护栏（显式测试上下文判定）下以同一路径再次 **6/6 通过**、14 张 PNG，且未设置 `ERP_AI_ALLOW_HIGH_RISK_TESTS` |
| 结论 | 护栏已按 `.ai/prompts/developer.md` 第 8 条重构，orchestrator 的真实 Edge 门禁不再因「验收目标与部署配置同实例 + 同库」被拦截；`ERP-007`（L2：仅代码 + NEWERP_TEST 验证）交付等级仍为 `code_ready`，`completed` 由 orchestrator 的真实 Edge 门禁判定；本轮未执行任何 SQL / 部署 / seed |

### 8.2 验收实现与前端契约（静态核对结论）

| 项 | 结论 |
|---|---|
| 接口 | `POST /api/sales/quotations`（返回 `{ id, quotationNo }`）、`POST .../{id}/approve`、`POST .../{id}/to-pi`（返回 `{ id, piNo, quotationNo }`）、`/api/sales/proforma-invoices` CRUD + `/{id}/approve|unaudit|void|print` 齐备 |
| 前端 | `sales-pi.js` 暴露 `quotationToPi` / `piApprove` / `piUnaudit` / `piVoid` / `previewSalesDocPrint`，与 `modules.js` 的 `rowActions` 一一对应；`crud.js` 的「更多」菜单（`.row-more`）渲染这些行操作 |
| 枚举口径 | `Program.cs` 注册 `JsonStringEnumConverter`，状态以 `Pending` / `Approved` / `Cancelled` 等**枚举名**输出，`rowActions.statuses` 与前端币种选项（`CURRENCY_NAME_OPTS`）据此对齐 |
| 打印 | `PrintTemplateController` 的 `PrintableTitles` 已登记 `quotation` / `proforma-invoice`；未维护模板时返回内置默认模板，不会产生失败请求 |

### 8.3 报价单 / PI → 销售订单（ERP-010 · 已实现）

| 项 | 结论 |
|---|---|
| 共享实现 | `src/ERP.Api/Controllers/SalesOrderConversion.cs`：两种来源共用「守卫 + 映射 + 复核」，控制器（`QuotationController` / `ProformaInvoiceController`）只负责取单、生成单号、落库 |
| 状态守卫 | 报价单：已作废 → 拒绝；已转 PI → 提示「请从 PI 转销售订单」；未审核 → 拒绝；无明细 → 拒绝。PI：已作废 → 拒绝；未审核 → 拒绝；无明细 → 拒绝 |
| 重复守卫 | 以销售订单的来源字段为准（`SourceQuotationId` / `SourcePiId`，仅统计未删除订单）：同一来源已存在订单时再次「转入预填」或「直接生成」都返回 `RuleConflict` 并给出已生成单号；**只新增、不更新**，绝不覆盖既有销售订单（已软删除的历史订单不阻断重新生成） |
| 转换后状态 | 报价单 → `Completed`（「已完成」，即已转 PI 或已转销售订单，转 PI 处的提示文案同步改为「已完成转换」）；PI → `Completed`（即「已转销售订单」，与其在审核 / 作废处的既有语义一致） |
| 主表映射（报价单） | 客户 / 业务员 / 币种 / 汇率 ← 报价单；价格条款 ← 报价单贸易术语（为空回退客户档案）；目的港 ← 报价单目的港（为空回退客户档案）；付款条件 ← 报价单（为空回退客户档案）；收货人 / 通知人 / 唛头 ← 客户档案默认值；定金比例 ← 客户档案（未维护按 30%）；业务性质 / 佣金比例 ← 客户档案（佣金仅取 0~100，越界按未维护）；来源报价单号 ← 报价单 |
| 主表映射（PI） | 客户 / 业务员 / 币种 / 汇率 ← PI；条款 / 目的港 / 付款条件 ← PI（为空回退客户档案）；**收货人 / 通知人 / 唛头 ← PI 值优先**（为空回退客户档案）；运输方式 ← PI 运输条款；定金比例 ← PI 比例 → PI 定金金额反算 → 客户档案 → 30%；来源 PI **与该 PI 背后的来源报价单一并写入**；报价单/ PI 的文本交期（无对应列）合并进备注并截断到 500 字符 |
| 明细映射 | 行序按来源 `SortNo`；商品 Id / 名称 / 规格 / 单位 / 数量 / 单价 / 备注逐行复制；**金额按 `数量 × 单价` 服务端重算**；商品编码与起订量在销售订单明细中无对应列（ERP-008 结构），本任务不新增结构，故不带入 |
| 服务端复核 | 明细数量必须 > 0、单价不得为负、定金比例必须在 0~100（越界抛 `InvalidParameter`）；合计与定金复用销售订单口径 `SalesOrderController.Calculate`（总额 = Σ 数量×单价，定金 = 总额 × 比例%）与 `Validate`（佣金 0~100），预填后经页面保存时同样走这套复核 |
| 单号 | 预填**不生成**销售订单号（不消耗字轨）；直接生成与页面保存都由 `IDocumentNumberService` 按 `DocumentType.SalesOrder` 发号 |
| 前端 | `sales-pi.js` 暴露 `quotationToOrder` / `quotationPrefillOrder` / `piToOrder` / `piPrefillOrder`，与 `modules.js` 的 `rowActions`（仅「已审核」可见）一一对应；带入预填经 `gotoModulePage('sales-order')` 切到销售订单页并用 `fillSalesOrderForm` 写入表单（引用字段同步名称、明细写入 `DETAIL_ROWS`） |
| 测试 | `src/ERP.UnitTests/SalesOrderConversionTests.cs`（18 例：两种来源映射、双来源留痕、定金反算、重复与非法状态守卫、服务端复核、预填不落库、接口路由与前端接线静态断言、预填报文 JSON 契约）；`src/ERP.IntegrationTests/SalesOrderConversionUiTests.cs`（3 个 Edge 场景，`Collection=UiTests`） |

---

## 9. 完成度与实现状态清单（ERP-018 更新 · 2026-09-23）

> 本节是判断"做什么、还缺什么"的唯一入口，与 §7 批次表、`.ai/FUNCTION_BACKLOG.md` 保持一致；图例见文首。

### 9.1 已实现（代码 + 单元测试）

| 能力 | 状态 | 证据 |
|---|---|---|
| 报价单主子表、审核 / 销审、询价单带入 | ✅ 已实现 | `QuotationController`、`Quotation.cs`、`modules.js` 的 `quotation` 模块、`BillCatalogTests` |
| 报价单转 PI（同一报价单仅一次） | ✅ 已实现（ERP-007） | `POST /api/sales/quotations/{id}/to-pi`、`QuotationToPiTests` |
| PI 主子表 + 审核 / 销审 / 作废 + 银行信息 / 收货人 / 通知人 / 唛头 / 定金 | ✅ 已实现（ERP-007） | `ProformaInvoiceController`、`ProformaInvoice.cs`、`ProformaInvoiceControllerTests`（20 例） |
| 报价单 / PI 打印数据端点 | ✅ 已实现（ERP-007） | `GET /api/sales/quotations/{id}/print`、`GET /api/sales/proforma-invoices/{id}/print`；`PrintTemplateController.PrintableTitles` 已登记 `quotation` / `proforma-invoice` |
| 报价单 / PI 行操作「打印预览」（按打印模板渲染） | ✅ 已实现（ERP-007） | `sales-pi.js`（`SALES_DOC_PRINT` + `previewSalesDocPrint`）、`modules.js` 的 `rowActions` |
| 报价单 / PI **共享打印三件套**（打印预览 / 直接打印 / 打印设计） | ✅ 已实现（ERP-018） | `bill-config-ef.js`（注册进 `BILL_CONFIG`，`ef: true` + `api`）、`sales-pi.js`（`printSalesDocDirect` / `designSalesDocPrint` 共用 `openSalesDocPrintWindow`）、`modules.js` 行操作、`print-design.js` / `pd-grid.js` 单据清单去重、`QuotationGovernanceTests` |
| 报价单**有效期治理**（状态分类 + 到期提醒 + 列表徽标） | ✅ 已实现（ERP-018） | `QuotationValidityRules`（五态分类 / 窗口归一化 / 紧急度）、`GET /api/sales/quotations/validity-due`、`sales-pi.js`（`quotationValidityBadge` / `openQuotationValidityReminder`）、`crud.js` 列 `render` 钩子 |
| **报价成交率报表**（按业务员聚合，口径见 §10.3） | ✅ 已实现（ERP-018） | `ReportService.Quotation.cs`、`GET /api/reports/quotation-conversion`、`reports.js` 的 `quotation-conversion`、`sales-pi.js` 的 `openQuotationConversionReport` |
| 报价单 / PI → 销售订单（带入预填 + 直接生成、来源留痕、重复守卫） | ✅ 已实现（ERP-010） | `SalesOrderConversion.cs`、`SalesOrderConversionTests`（18 例）、§8.3 |
| 回归测试 | ✅ 272/272 通过（2026-09-23 实测，Release） | `dotnet build NEWERP.sln -c Release`（0 警告 0 错误）+ `dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build` |

### 9.2 浏览器验收延后（代码就绪，尚未做真实 Edge 验收）

| 场景 | 状态 |
|---|---|
| 报价单 → PI → 打开 PI → 草稿改 / 审核禁改 / 销审恢复 / 打印预览 / 作废 | 🧪 **ERP-007 已完成真实 Edge 验收**（6/6 通过、14 张截图、TRX + `browser-session.json`，2026-09-23） |
| 报价单 / PI 转销售订单（ERP-010）、订单追溯（ERP-008）、库存单据（ERP-009）等场景 | 🧪 **`browser_deferred`**：按 `completion_policy.defer_browser_during_development` 延后到 **`FINAL-UI-ACCEPTANCE`** 阶段统一执行 `Collection=UiTests`，届时以真实 Edge + TRX + 截图 + SHA-256 清单为准 |
| 报价单打印预览/直接打印/打印设计、有效期提醒与成交率报表（ERP-018） | 🧪 **`browser_deferred`**：用例已写入 `src/ERP.IntegrationTests/QuotationGovernanceUiTests.cs`（3 个场景：打印三件套入口、有效期治理徽标 + 提醒弹窗、成交率报表），留待 `FINAL-UI-ACCEPTANCE` 统一执行 |

> `browser_deferred` **既不等于已验收，也不等于失败**：既有 UI 用例（`PiWorkflowUiTests`、`SalesOrderConversionUiTests`、`OrderTraceabilityUiTests`、`InventoryMovementUiTests` 等）与浏览器基础设施保持原样，待最终 UI 验收阶段批量执行。

### 9.3 仍缺失

| 缺口 | 状态 | 备注 |
|---|---|---|
| 报价版本号（多轮议价版本留痕） | ❌ 未实现，未立项 | ERP-006 审计缺口④ |
| 报价单通用 CRUD 的控制器级单元测试 | ◑ 部分覆盖 | 转 PI / 转订单 / 打印数据 / 有效期 / 成交率路径已有测试（`QuotationToPiTests` / `SalesOrderConversionTests` / `QuotationGovernanceTests`），通用 CRUD（分页 / 详情 / 新增 / 修改）未单测 |
| 报表中心菜单键 `quotation-conversion` | ◑ 未加菜单 | 报表已注册进 `ReportController` 与 `reports.js`，入口在报价单页工具栏「📈 成交率报表」；数据库菜单表由受门禁脚本维护（`SchemaUpgrader` / `deploy/*.sql`），本任务不改结构 |
| 报价单 / PI 进入 `BILL_CODE_MAP` | ✅ **有意不入** | 该映射把菜单码路由到存储过程版单据页（`/api/v2/bills/*`）；两类单据是 EF 主子表（页面在 `MODULES`），挂进去会让菜单跳到 SP 页并 404。共享打印配置改由 `BILL_CONFIG`（`bill-config-ef.js`）承载 |

---

## 10. ERP-018：打印注册与有效期治理（2026-09-23 实施）

### 10.1 共享打印三件套注册

| 项 | 做法 |
|---|---|
| 注册文件 | `src/ERP.Api/wwwroot/js/bill-config-ef.js`（`Object.assign(BILL_CONFIG, {...})`，在 `index.html` 中紧跟 `bill-config3.js` 之后加载） |
| 注册内容 | `quotation` / `proforma-invoice`：`billType`、`title`、`ef: true`、`api`（`/api/sales/quotations`、`/api/sales/proforma-invoices`）、`noKey`（`quotationNo` / `piNo`）与打印字段中文标签（`columns` / `fields`） |
| 收益 | ① `bill-print.js` 的 `billFieldLabels()` 优先取 `BILL_CONFIG` → 打印字段中文标签与其余 16 种单据同口径；② 「样式设计」单据清单把它们归入「业务单据」分组（`print-design.js` / `pd-grid.js` 对已在 `BILL_CONFIG` 的同名单据去重，避免重复出现）；③ `PD_PREFERRED` 补充报价 / PI 常用字段，智能推荐同样生效 |
| 不做的事 | **不加入 `BILL_CODE_MAP`**（只用于 SP 单据菜单路由）；**不改打印模板存储**（模板仍按 `billType` = 菜单码读写，与 ERP-007 已保存模板完全兼容）；**不新增任何数据库结构** |
| 入口 | 报价单 / PI 行操作「打印预览 / 直接打印 / 打印设计」（`rowActions` → `previewSalesDocPrint` / `printSalesDocDirect` / `designSalesDocPrint`），数据统一取 `GET {api}/{id}/print` |

### 10.2 有效期治理口径（`QuotationValidityRules`，单一事实来源）

| 项 | 规则 |
|---|---|
| 判定基准 | 剩余天数 = `ValidUntil`（取日期） − 基准日（取日期）；基准日默认今天，接口可传 `asOfDate` |
| 五态分类 | 未设置有效期（`ValidUntil` 为空）/ 已过期（剩余 &lt; 0）/ 今日到期（剩余 = 0）/ 即将到期（0 &lt; 剩余 ≤ 窗口）/ 有效（剩余 &gt; 窗口） |
| 提醒窗口 | 默认 **7 天**（`DefaultAheadDays`）；入参为负按默认值处理（避免"永不提醒"的隐式行为）；弹窗可切 7 / 15 / 30 天 |
| 提醒范围 | 已过期 + 窗口内到期；**已作废不提醒**（`Status = Cancelled` 直接排除）、**未设置有效期不提醒**；已转出（已转 PI / 已转销售订单 / 已完成）仍列出但标记 `Converted`，便于判断是否还需催单 |
| 排序 | 紧急度降序（已过期 3 &gt; 今日到期 2 &gt; 即将到期 1），同级按 `ValidUntil` 升序 |
| 可见性 | 报价单列表「有效期状态」派生列（`crud.js` 列 `render` 回调 + `sales-pi.js` 的 `quotationValidityBadge`，样式类 `status-danger` / `status-warning` / `status-success` / `status-neutral`，与后端 `ValidityLevel` 一一对应）；工具栏「⏰ 有效期提醒」弹窗列出明细 |
| 结构影响 | 只读现有 `Quotations.ValidUntil` 与来源外键（`ProformaInvoices.QuotationId`、`SalesOrders.SourceQuotationId`），**不新增列 / 表**；前端徽标与后端同口径（窗口常量两处同步：`QuotationValidityRules.DefaultAheadDays` 与 `sales-pi.js` 的 `QUOTATION_VALIDITY_AHEAD_DAYS`） |

### 10.3 报价成交率计算口径（`GET /api/reports/quotation-conversion`）

| 项 | 规则 |
|---|---|
| 统计范围 | `Quotations` 中 `QuotationDate` ∈ [`start`, `end`] 且未软删除的报价单 |
| 分母「有效报价数」 | 范围内 **未作废**（`Status ≠ Cancelled`）的报价单数；已作废只计入 `cancelledCount`，不参与成交率 |
| 分子「已转出数」 | 分母中满足任一：存在未删除 `ProformaInvoices.QuotationId`（已转 PI）／存在未删除 `SalesOrders.SourceQuotationId`（已转销售订单）／报价单状态为「已完成」（兜底，覆盖外键回填前的历史数据） |
| 成交率 | `已转出数 ÷ 有效报价数 × 100`，保留 2 位小数；分母为 0 时按 0 处理（除零保护） |
| 已过期未成交 | 分母中 `ValidUntil` 早于**报表期间结束日**且未转出的报价单数（与 §10.2 同一分类口径） |
| 金额口径 | 均为报价单**原币**金额（`totalAmount`）合计，不做汇率折算，避免期间内汇率波动影响可比性；`avgConvertedAmount` = 已转出金额 ÷ 已转出数（保留 2 位，无成交为 0） |
| 分组与排序 | 按业务员聚合（未填写业务员归入「未指定业务员」），成交率降序 → 报价数降序 → 业务员升序 |
| 验证 | `QuotationGovernanceTests`（口径 / 边界 / 分母保护 / 期间与软删除过滤 / 状态兜底 / 端点路由），见 `docs/部署交付文档.md` §33 |

---

## 11. 采纳的默认选项（如需改动随时告诉我）

| # | 项 | 采用值 |
|---|---|---|
| 1 | 编号前缀 | `QT` / `PI`（可在「单据编号规则」页自助修改） |
| 2 | PI 银行信息 | **系统参数存默认 + PI 可覆盖**（避免重复录入与出错） |
| 3 | PI → 销售订单 | **带入预填 + 直接生成**（ERP-010 已实现）：预填返回未落库草稿供人工编辑，直接生成走 EF 销售订单接口并留痕来源；**不调用旧版销售订单存储过程**，`SourceQuotationId/No`、`SourcePiId/No` 由服务端写入 |
