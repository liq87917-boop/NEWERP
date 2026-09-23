# 报价单（Quotation）与形式发票（PI）设计方案

> 版本：v1（2026-09-18）· 状态：**已按建议默认值实施批次 1** · 适用范围：WMERP 外贸 ERP（NEWERP）

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

### 3.3 形式发票 `ProformaInvoices` + `ProformaInvoiceDetails`（批次 2）
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
| 菜单 | 「销售管理」下新增 **报价单**、**形式发票 PI**（`init16/17.sql` 幂等插入 + 自动授权） |
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
| **1（本次实施）** | 报价单：主子表 + 审核 + 打印 + 从询价单带入明细 + 转 PI 按钮（PI 未上线前按钮隐藏） |
| 2 | PI：主子表 + 银行信息 + 审核 + 打印 + 报价单转 PI |
| 3 | PI → 销售订单（带入预填）、报价有效期到期提醒、报价成交率分析 |

---

## 8. 影响面与风险控制

| 项 | 说明 |
|---|---|
| 只新增，不改旧 | 新增表 / 菜单 / 字轨规则；**不修改任何现有表结构与存储过程** |
| 脚本幂等 | 每个 `initN.sql` 可重复执行；schema 自动探测（兼容 `dbo` 与 `db_owner`） |
| 上线前快照 | 沿用 `backup\WMERP_Data-snapshot-*` 方式，保留回滚脚本 |
| 解耦 | 报价单 / PI 停用不影响询价与订单流程 |
| 每批交付验收 | `dotnet build` 0 错误 → 单元测试 165 项全绿 → 生产库幂等执行 → 写入回读零差异 → 重新打包 rN |

---

## 9. 采纳的默认选项（如需改动随时告诉我）

| # | 项 | 采用值 |
|---|---|---|
| 1 | 编号前缀 | `QT` / `PI`（可在「单据编号规则」页自助修改） |
| 2 | PI 银行信息 | **系统参数存默认 + PI 可覆盖**（避免重复录入与出错） |
| 3 | PI → 销售订单 | **带入预填**（不改存储过程）；若需一键生成正式订单，需确认「允许调用现有销售订单存储过程」 |
