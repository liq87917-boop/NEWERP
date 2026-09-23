# 报价单（Quotation）与形式发票（PI）设计方案

> 版本：v1（2026-09-18）· 状态：**批次 1 + 批次 2 已实施**（批次 2 形式发票 PI 闭环由 **ERP-007** 于 2026-09-23 交付）· 适用范围：WMERP 外贸 ERP（NEWERP）

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
| POST | `/api/sales/quotations/{id}/to-order` | 转为销售订单（第三批，先做带入预填） |
| GET | `/api/sales/quotations/{id}/print` | 打印数据（供打印模板使用） |
| POST | `/api/sales/proforma-invoices/...` | PI 同上一套（`to-order` 用于 PI → 销售订单） |

---

## 6. 前端

| 项 | 做法 |
|---|---|
| 菜单 | 「询报价」分组下新增 **报价单**、**形式发票 PI**（`SchemaUpgrader` 幂等插入 + 自动授权；生产上线脚本 `deploy/init17.sql` 属受门禁任务，单独产出） |
| 列表 | `modules.js` 新增 `quotation`、`proforma-invoice`（列 = 单号 / 日期 / 客户 / 有效期 / 币种 / 金额 / 业务员 / 状态） |
| 编辑 | 复用主子表单据编辑（明细表格：加行、删行、`数量 × 单价 = 金额`、主表合计自动汇总；后端复核防篡改） |
| 打印 | `BILL_CONFIG` 注册两个单据类型 → 自动获得「打印预览 / 直接打印 / 打印设计」 |
| 便捷操作 | 报价单列表工具栏：**转 PI**；PI 列表工具栏：**转销售订单**（`extraActions` 机制） |

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
| 3（待做） | PI → 销售订单（带入预填）、报价有效期到期提醒、报价成交率分析 |

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

---

## 9. 采纳的默认选项（如需改动随时告诉我）

| # | 项 | 采用值 |
|---|---|---|
| 1 | 编号前缀 | `QT` / `PI`（可在「单据编号规则」页自助修改） |
| 2 | PI 银行信息 | **系统参数存默认 + PI 可覆盖**（避免重复录入与出错） |
| 3 | PI → 销售订单 | **带入预填**（不改存储过程）；若需一键生成正式订单，需确认「允许调用现有销售订单存储过程」 |
