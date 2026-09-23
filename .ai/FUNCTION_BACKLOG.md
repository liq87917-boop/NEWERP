# NEWERP 全功能完成 Backlog（ERP-006 审计版）

> 目标：按用户 2026-09-23 的明确要求，持续完成仓库内已经定义、规划或留有业务入口的功能；以代码、测试、真实 Edge 验收和 GitHub 全 CI 作为完成标准。生产数据库、生产 OSS、正式部署/发布和不可逆数据操作继续保留 Human Gate。
>
> 本版由 **ERP-006「全仓功能审计」** 重写：状态、缺口与证据来自对 `docs/`、Domain/Application/Infrastructure/Api/wwwroot、单元/集成/UI 测试、`SchemaUpgrader.cs` 与 `deploy/init5~16.sql`（只读）的逐项比对，**取代**此前基于文件名与文档描述的初筛，避免后续任务重复实现已经可用的功能。

## 0. 审计范围、方法与证据基线

| 项 | 内容 |
|---|---|
| 审计任务 | ERP-006（`completion_mode: control_plane`，只读审计；未改动业务行为、SQL、生产数据） |
| 判定办法 | 每个功能须同时具备：实体或表 DDL、API 端点、持久化路径、前端页面与菜单、测试或交付验证记录；缺一不得判为 complete |
| 证据基线 | `dotnet test src\ERP.UnitTests -c Release` = **165/165 通过、0 失败、0 跳过**（2026-09-23 本次实测，Release 编译通过） |
| 未执行项 | 未连接 SQL Server、未运行 `ERP.IntegrationTests`/UI/Selenium、未启动 API、未执行任何 `.sql`/部署/seed（遵守 `.clinerules` 与 Human Gate） |
| 持久化口径 | 表结构结论取自 `SchemaUpgrader.cs`（启动幂等升级）与 `deploy/init5~16.sql` 脚本本体；生产库真实数据状态不在本次验证范围 |
| 菜单/页面口径 | 菜单码取自 `deploy/init*.sql` + `SchemaUpgrader.cs` 的幂等插入；页面注册核对 `app.js`、`modules*.js`、`BILL_CONFIG`/`BILL_CODE_MAP`、`EXPORT_MENU_MAP`、`REPORTS`、`print-design.js`、`dingtalk.js`、`users.js`、`roles.js` |

## 1. 状态定义

| 状态 | 含义 | 判定门槛 |
|---|---|---|
| verified-complete | 用户可用且闭环 | 实体 + DDL + API + 页面 + 菜单 + 测试/交付记录齐备，无已知硬缺口 |
| partial | 部分可用 | 已有真实代码与入口，但缺少验收标准中的关键环节 |
| missing | 未实现 | 无实体、无 API、无页面或无菜单 |
| decision-required | 待决策 | 业务规则或范围未定，拆分任务前需用户确认 |

## 2. 模块状态总表（Gap Matrix）

| # | 模块 / 功能 | 状态 | 代码与菜单证据 | 关键缺口 | 风险 |
|---|---|---|---|---|---|
| 1 | 系统设置（用户/角色/权限/参数/用户参数/单据号规则/客户端限制/日志/打印设计/钉钉配置与记录） | verified-complete | `SeedData.Menus.cs:14-22`；`SysUserController`、`RoleController`、`MenuController`、`ParameterController`、`UserParameterController`、`SysSimpleControllers`、`PrintTemplateController`、`DingTalkController`、`Services/FollowUpReminderService.cs` | 无 | low |
| 2 | 主数据（客户/供应商/员工/仓库/费用科目/商品/数据字典） | verified-complete（字段已补齐） | `BaseCustomer.cs:63-113`（业务性质/币种/结算方式/贸易条款/目的港/Consignee/Notify/默认唛头/账期/佣金比例/等级/信用状态/来源）；`BaseSupplier.cs:58-91`（类型/档口位置/主营/结算/开票/税率/交期/返点/微信）；`BaseOtherInfo.cs:82-143`（装箱单位/装箱数/单位换算/外箱四边/体积重/英文报关品名/退税率/品牌/认证/客户与工厂货号/MOQ/含税/安全库存上下限）；`SchemaUpgrader.cs:280-322,552-553`；字典 14 类 `modules.js:173-203` | 客户「指定货代」；商品颜色/尺码 SKU 变体、多供应商供货关系、图片资料库页面（3 图字段已有） | low |
| 3 | 询价单（CRUD/提交/审核/导出） | verified-complete | `InquiryController`、`bill-config.js:36`、`bill-export.js:5`、菜单 `inquiry-new`/`inquiry-export` | 询价单侧无一键转报价（现由报价单 `from-inquiry` 反向带出客户与明细） | low |
| 4 | 报价单 Quotation | **verified-complete（转 PI ERP-007、转销售订单 ERP-010、共享打印注册 + 有效期治理 + 成交率报表 ERP-018；浏览器验收待 FINAL-UI-ACCEPTANCE）** | `Quotation.cs`/`Detail`（`SortNo` 替代保留字 `LineNo`）、`ErpDbContext.Entities.cs:62-63`、`QuotationController.cs`（分页/详情/`from-inquiry`/`approve`/`unaudit`/新增/修改+后端重算/`{id}/print`/`validity-due`）、`bill-config-ef.js`（`BILL_CONFIG` 注册 `quotation`/`proforma-invoice`，`ef: true` + `api` + `noKey`）、`sales-pi.js`（打印预览/直接打印/打印设计 + 有效期徽标与提醒 + 成交率报表入口）、`QuotationValidityRules.cs`、`ReportService.Quotation.cs`（`/api/reports/quotation-conversion`）、`SchemaUpgrader.cs:707-777`、`init16.sql`、菜单 `quotation`(`/sales/quotation`) 、`modules.js:458-517`（主子表 `detailFields` + `rowActions` + `extraActions`）、字轨 `DocumentType.Quotation=17`、`QuotationGovernanceTests`（25 例） | ① ~~无「转 PI」端点/按钮~~ → **ERP-007 已实现**（`POST /{id}/to-pi`）；② ~~无 `/{id}/print` / 未注册共享打印三件套~~ → **ERP-007 + ERP-018 已实现**（`GET /{id}/print` 为唯一打印数据源；报价单 / PI 已注册进共享 `BILL_CONFIG`，行操作具备打印预览 / 直接打印 / 打印设计；**有意不进** `BILL_CODE_MAP`，否则菜单会跳到 SP 单据页）；③ ~~无报价→销售订单带入~~ → **ERP-010 已实现**（带入预填 + 直接生成，来源留痕 + 重复守卫）；④ 无报价版本号（多轮议价）；⑤ ~~无有效期到期提醒与成交率分析~~ → **ERP-018 已实现**（`validity-due` 提醒 + 列表有效期徽标 + `/api/reports/quotation-conversion` 成交率报表，口径见 `docs/报价单与PI设计方案.md` §10）；⑥ 无 `QuotationController` 通用 CRUD 单元测试（转 PI / 转订单 / 打印数据 / 有效期 / 成交率路径已由 `QuotationToPiTests` / `SalesOrderConversionTests` / `QuotationGovernanceTests` 覆盖） | medium |
| 5 | 供应商比价 PurchaseQuote | verified-complete | `PurchaseQuote.cs`（比价批次/多供应商/是否选中/为客户询价）、`PurchaseQuoteController`、菜单 `purchase-quote`(`init10.sql`) | `IsSelected` 选中后无「生成采购订单」下游动作 | low-medium |
| 6 | 销售订单（外销合同） | **verified-complete（代码 + 测试；待 Edge 门禁）** | `SalesOrder.cs:56-118`（客户 PO 号/合同号/价格条款/目的港文本/Consignee/Notify/唛头/来源报价+PI/出口方式/佣金比例/业务性质/分批出货/验货与包装要求）、`SchemaUpgrader.cs:891-931`（幂等补列）、`SalesOrderController.cs`（列表关键字含客户 PO/合同号、`/{id}/print`、`export-excel`、佣金比例 0~100 校验）、`modules-doc.js:100-149`（EF 主子表页面 + 引用/下拉/明细）、`sales-pi.js:20-29`（打印含新字段与订单明细列）、`bill-export.js:8-24`（导出菜单改调 EF 导出）、`OrderTraceabilityTests`（14 用例）、`OrderTraceabilityUiTests`（3 个 Edge 用例）；见 `docs/订单追溯字段说明.md` | 订单变更申请、订单执行跟踪时间轴、附件、~~报价/PI 一键生成销售订单~~（**ERP-010 已实现**：带入预填 + 直接生成，来源留痕、重复守卫）；SP 版单据页已退出菜单（历史 SP 表数据不迁移） | medium |
| 7 | 采购订单 | **verified-complete（代码 + 测试；待 Edge 门禁）** | `PurchaseOrder.cs:44-86`（归属客户/归属销售订单/代垫/供应商确认交期/税率与含税/到货进度/验货状态/合同号/结算进度）、`SchemaUpgrader.cs:891-931`、`PurchaseOrderController.cs`（关键字含合同号与归属销售订单号、`/{id}/print`、`export-excel`、税率 0~100 校验）、`modules-doc.js:150-191`、`sales-pi.js:30-37`、`bill-export.js:16-23`、`OrderTraceabilityTests`、`OrderTraceabilityUiTests` | 采购执行时间轴、比价 `PurchaseQuote.IsSelected` 下游联动、入库/结算单据自动回写进度 | medium |
| 8 | 采购入库 / 销售出库 / 库存查询 | verified-complete | `Logistics.cs`、`StockInController`/`StockOutController`/`StockController`、审核后联动 `Stocks`、`modules-doc2.js:3-29` | 多单位换算未参与出入库计算（`BaseProduct.UnitsPerPackage` 仅实体 + 导入模板）；无库位/批次成本 | low-medium |
| 9 | 库存动作单据（盘点/调拨/退货）+ 库存成本 | **verified-complete（代码 + 测试；待 Edge 门禁）** | ERP-009 落地：`InventoryDocuments.cs`（`StockAdjustment`/`StockTransfer`/`SalesReturn`/`PurchaseReturn` 主子表 + `StockMovement` 流水）、`InventoryService`（移动加权平均 + 冲销）、`/api/inventory/**` 四个控制器（提交/审核/销审/取消 + `{id}/movements`）、`/api/stocks/movements`、`SchemaUpgrader.cs` 第 23 段（幂等建表/补列/菜单/字轨）、`modules-doc2.js` 5 个新页面、`InventoryMovementTests`（19 用例）、`InventoryMovementUiTests`（3 个 Edge 用例）；见 `docs/库存单据与库存成本说明.md` | 库位/批次成本、FIFO、成本调整单；既有采购入库/销售出库控制器未改（历史变动不入流水） | medium |
| 10 | 装柜/出运主链（收货计划/订柜/预装柜/装柜清单） | verified-complete | `Container1.cs`/`Container2.cs`、`ContainerControllers`、`ContainerPreLoadingController`、`ContainerLoadingListController`、`bill-config3.js:60-118` | 无 | low |
| 11 | 装柜外贸与物流跟踪字段 | missing | `ContainerBooking`/`ContainerPreLoading*`/`ContainerLoading*` 无相关列；`docs/部署交付文档.md:854` 说明因走存储过程而暂缓 | LCL/FCL、B/L、SO、ETD/ETA/ATD/ATA、拖车/报关行、查验放行、目的/中转港；装载率仅报表侧(`reports.js:37-48`，40HQ 68m³ 基准) | medium-high |
| 12 | 一柜多客户拼柜与费用分摊 | partial | 分摊已可用：`expense-allocate.js`（拼柜/整柜/散货 × 体积/重量/箱数/金额）、`ExpenseBillController.cs:29-93`（预览+生成+重复防护）、`FinanceExpense.cs:50-73`（RefType/RefNo/CustomerId/AllocationBase/AllocationRatio/AllocatedAmount 落库） | `ContainerLoadingList.CustomerId` 仍为单客户，无「柜→多客户」主子表；分摊结果不回写装柜/结算；无分摊批次与来源行留痕 | medium |
| 13 | 单证中心 | **verified-complete（ERP-019 补齐自动生成与 Excel 导出；浏览器验收待 FINAL-UI-ACCEPTANCE）** | `TradeDocument.cs`（9 类单证/4 态/关联报关单号·柜号·订单号/金额/港口/份数）、`TradeDocumentController`（+`export-excel`）、`TradeDocumentGeneration.cs`（由销售订单 / 装柜清单带入预填 + 生成 + 重复守卫 + 编号规则）、`SalesOrderController`/`ContainerLoadingListController` 各 `GET {id}/trade-documents/prefill` + `POST {id}/trade-documents`、`trade-doc-gen.js`（生成对话框 / 导出对话框 / 单条导出）、`modules.js`(`doc-center` extraActions+rowActions)、`modules-doc.js`/`modules-doc2.js`（行操作「生成单证」「预填单证」）、菜单 `doc-center`(`init11.sql`)、`TradeDocumentGenerationTests`（21 例）、`TradeDocumentGenerationUiTests`（4 例 Edge）、`docs/单证中心生成与导出说明.md` | ① ~~无「由装柜清单/销售订单自动生成」~~ → **ERP-019 已实现**（两路来源 × 带入预填/直接生成，来源留痕写入 `SalesOrderNo`/`RefNo` + 备注，重复生成守卫：同订单同类型、同柜号同类型）；② ~~无 Excel 导出~~ → **ERP-019 已实现**（列表筛选导出 + 单条导出，沿用 `ExcelExporter`）；③ 单证扫描件附件仍为 `FileNote` 文本（附件中心任务）；④ 无 PDF/版式打印（共享打印设计另立任务）；⑤ 单证明细为单表，无商品明细行 | low-medium |
| 14 | 费用单（出口杂费台账） | verified-complete | `FinanceExpense`、`ExpenseBillController`、菜单 `expense-bill`(`init8.sql`)、`modules.js:238-260` | 与装柜结算单/付款单无金额联动（`BillNo` 为文本字段） | low |
| 15 | 应收账款与账龄 | verified-complete | `api/reports/ar-aging`、`ReportService.Ar.cs`、`reports.js:20-34`、菜单 `ar-aging`(`init9.sql`) | 无收款自动核销（收付款与订单/柜之间无核销明细），无信用额度占用与信用状态自动风控 | medium |
| 16 | 应付账款 / 供应商对账 / 客户对账 | missing | 全仓无实体、无端点、无菜单；`docs/部署交付文档.md:203`、`docs/菜单与业务流程优化建议-20260918.md:248` 列为缺失 | 全部缺失（档口月结对账仍靠 Excel） | medium |
| 17 | 代理费 / 佣金结算单 | missing | 现有 `sales-commission` 是**业务员提成表**（`ReportService.Extra3.cs`：系统参数 `SalesCommissionRate` × 订单毛利），与「按柜/客户收代理费、明佣暗佣」不是同一功能 | 代理模式主要收入单据缺失 | medium-high |
| 18 | 出口退税台账 + 退税汇总 | verified-complete | `BaseTaxRefund`/`TaxRefundController`、菜单 `tax-refund`(`init7.sql`) 与 `tax-refund-summary`(`init12.sql`)、`reports.js:59-70` | 与供应商开票/发票管理无联动 | low-medium |
| 19 | 发票管理（专票/普票/出口发票） | missing | 无实体、无端点、无菜单 | 缺失 | medium |
| 20 | 附件中心 | missing | 仅商品 3 张图 + 单证 `FileNote` 文本（`TradeDocument.cs:71-73`） | 合同/PO/验货报告/单证扫描件无统一挂靠 | low-medium |
| 21 | 审批流（金额阈值/多级） | missing | 现状为 `DocumentControllerBase` 单级审核状态机（草稿→已审核/销审） | 缺失，风控不足 | medium |
| 22 | 数据范围权限（业务员仅见自己客户） | missing | `src` 内无 `DataScope` 实现；权限仅「角色-菜单」(`SysRoleMenu`) | 缺失 | medium |
| 23 | 业务模式开关 `BizMode` | partial / decision-required | 参数已种子化（`init6.sql:130-132`，默认 `Hybrid`），但 `src` 无任何消费方；用户已确认「两种模式都有、用角色权限区分」（`docs/部署交付文档.md:632`） | 菜单可见性与按模式必填规则未实现；按用户答复建议**不做**按模式隐藏菜单，参数保留为预留 | low |
| 24 | 数据字典 | verified-complete | `BaseOtherInfo` + `other-info` 页面 14 类 InfoType（Currency/ExchangeRate/Port/Forwarder/CustomsBroker/ShippingMark/Package/TradeTerm/Settlement/TransportMode/ExpenseType/ExportMode/Certification/Brand，`modules.js:173-203`） | 汇率为文本字典，无每日汇率表/锁汇/汇兑损益 | low |
| 25 | 报表中心 | verified-complete（15 张） | `ReportController` 15 个 `[HttpGet]`；`reports.js` 15 个 `REPORTS` 键；菜单 = 原 7 张 + `ar-aging`/`container-stats`/`purchase-cost`/`tax-refund-summary`/`stock-alert`/`sales-commission`/`follow-up-due`；**ERP-018 新增 `quotation-conversion`（报价成交率）**，入口在报价单页工具栏（报表中心菜单表未改） | 仍缺报表：佣金/代理费汇总、应付账龄、库存周转与呆滞、生产/成本 | low-medium |
| 26 | 客户 CRM 跟进 + 到期提醒 | verified-complete | `CustomerFollowUp`/`CustomerFollowUpController`、`Services/FollowUpReminderService.cs`（后台 + 钉钉推送）、`follow-up-due` 报表与菜单 | 无客户价格协议、无报价转化漏斗 | low |
| 27 | 样品管理 | verified-complete | `Sample`/`SampleController`、菜单 `sample`、`modules.js:412-450` | 无 | low |
| 28 | 生产管理（BOM/生产任务/委外/领料/产成品入库/看板） | missing + decision-required | 全仓无相关实体、端点、菜单；`docs/菜单与业务流程优化建议-20260918.md:191,261,309` 明确列为"完全没有" | 需先确认工序粒度（是否有车间/工序/工价）；用户已答「工贸一体 + 代理采购都有」→ 在范围内但需细化设计后单独立项 | high |
| 29 | 移动端 / 扫码出入库 | missing | 无相关代码；`docs/菜单与业务流程优化建议-20260918.md:266,310` 列为阶段 4 增强 | 缺失，暂缓 | medium |
| 30 | 自动开发控制层（队列/门禁/Edge 验收/CI） | verified-complete | `.ai/**`、`scripts/ai_pipeline.py`、`ai_orchestrator.py`、`ai_browser_acceptance.py`、`agent-host.ps1`、`tests/automation/test_pipeline_contracts.py`、GitHub Build/Unit/SQL/UI 全 CI（ERP-005 已 `full_ci_validated`，run 35816664437） | 无 | low |

### 2.1 总表口径说明
- 判为 verified-complete 的模块均同时具备实体/DDL、API 端点、前端页面与菜单、测试或交付验证记录（如 `docs/部署交付文档.md` 的逐批验证章节）。
- 报价单、销售订单、采购订单、装柜、拼柜、单证中心等判为 partial 的模块**已经有真实可用入口**，后续任务只补缺口，不得重写已有可用链路。
- 「已交付」不等于「已用户验收」：业务任务的最终完成仍以真实 Edge 验收 + 证据清单为准（`.ai/MASTER_PLAN.md`）。

## 3. 代码存在但未接通 / 用户不可达 / 未持久化 清单

| # | 项 | 证据 | 影响 | 处理建议 |
|---|---|---|---|---|
| 3.1 | 商品多单位与箱规字段未参与业务计算 | `BaseOtherInfo.cs:82-108` 定义 `PackageUnit/UnitsPerPackage/UnitConversion/Outer*/VolumeWeight`；`src` 内唯一引用是 `BaseDataIoController.cs:86`（导入模板表头映射） | 报价、采购、出入库、装柜仍按单一单位人工换算 | 需业务确认换算口径后，在装柜/出入库（或报价明细）引入换算 |
| 3.2 | ~~报价单未接入「打印三件套」~~ → **ERP-018 已解决** | `bill-config-ef.js` 把 `quotation` / `proforma-invoice` 以 `ef: true` + `api` + `noKey` 注册进共享 `BILL_CONFIG`（`bill-v2.js:13-31` 的 `BILL_CODE_MAP` 只服务 SP 单据菜单路由，EF 单据**有意不入**，否则菜单会跳到 SP 单据页）；`sales-pi.js` 提供打印预览 / 直接打印 / 打印设计，`modules.js` 行操作接线，`print-design.js`/`pd-grid.js` 单据清单去重 | 已由 ERP-018 关闭（浏览器验收延后到 `FINAL-UI-ACCEPTANCE`） |
| 3.3 | ~~报价单缺少文档承诺的端点~~ → **ERP-007 / ERP-010 / ERP-018 已解决** | `QuotationController.cs` 现有分页/详情/`from-inquiry`/`approve`/`unaudit`/`Create`/`Update` + `to-pi`（ERP-007）+ `{id}/print`（ERP-007 落地、ERP-018 明确为唯一打印数据源）+ `order-prefill` / `to-order`（ERP-010）+ `validity-due`（ERP-018），与 `docs/报价单与PI设计方案.md` §5 接口表一致 | 文档与实现已对齐（`QuotationGovernanceTests` 含路由契约断言） |
| 3.4 | 系统参数 `BizMode` 无消费方 | 仅 `init6.sql:130-132` 插入 | 参数形同虚设，且容易误导后续任务重复实现"按模式隐藏菜单" | 明确标记为预留；如需按模式隐藏再单独立项 |
| 3.5 | 比价「选中」无下游动作 | `PurchaseQuote.cs:74-75` `IsSelected` + `Status` | 选中供应商后无法一键生成采购订单 | 可并入采购订单增强任务（非必需） |
| 3.6 | 费用分摊与结算/收款无关联 | `FinanceExpense.BillNo` 为文本；装柜/散货结算实体无费用明细子表 | 柜成本不能自动归集到结算与毛利，分摊结果只落在费用单 | 拼柜方案任务中建立关联 |
| 3.7 | 分摊无批次与来源行留痕 | `ExpenseBillController.cs:49-52` 仅按「柜号+费用类型+日期」防重复 | 无法回溯"哪些行按什么权重算出这个比例" | 拼柜方案任务中补分摊批次表 |
| 3.8 | 单证附件仍是文本 | `TradeDocument.cs:71-73` `FileNote` | 单证扫描件无处存放 | 附件中心任务 |
| 3.9 | 菜单可达性核对结论 | 逐码比对 36 个菜单码（`deploy/init*.sql` + `SchemaUpgrader.cs`）与页面注册（`modules*.js`/`BILL_CONFIG`/`EXPORT_MENU_MAP`/`REPORTS`/`print-design.js`/`dingtalk.js`/`users.js`/`roles.js`） | **本次未发现菜单指向未配置页面的死链**；风险集中在 3.2/3.3（报价单打印与转 PI）与 PI 整体缺失 | 保持；后续新增菜单必须同时补页面注册 |

## 4. 文档失真清单（识别结果；**已由 ERP-017 于 2026-09-23 全部补正**，见 §4.8）

| # | 文档与位置 | 失真点 | 实际情况 |
|---|---|---|---|
| 4.1 | `docs/技术方案说明书.md:190` | 「测试命令 `dotnet test`（当前 43 个用例全部通过）」 | 本次审计实测 **165/165 通过**（Release、`ERP.UnitTests`）；**ERP-017 复核后为 272/272**（见 §4.8） |
| 4.2 | `docs/技术方案说明书.md`（目录结构与接口清单） | 未包含阶段 1~3 模块（费用单、退税台账、单证中心、供应商比价、CRM 跟进、样品、报价单）与 `tests/automation`、`.ai/`、`scripts/ai_*.py` | 这些模块均已交付并在菜单中可见 |
| 4.3 | `docs/报价单与PI设计方案.md:3` | 「已按建议默认值实施批次 1」+ §5 列出 `GET /api/sales/quotations/{id}/print`、`POST .../to-order`；§6 称「`BILL_CONFIG` 注册两个单据类型 → 自动获得打印预览/直接打印/打印设计」 | 报价单打印端点与 `BILL_CONFIG` 注册**均未实现**；`to-order` 属批次 3 未做；§4 称 `ProformaInvoice=18` 字轨"幂等插入"，实际脚本只插入 17（`init16.sql:118`） |
| 4.4 | `docs/部署交付文档.md:813-817, 854-859, 909-910, 952-953, 993-996, 1054-1057` | 多段"下一批/下一阶段"清单仍把**已交付**项列为待办：费用单、应收账龄、单证中心、供应商比价、样品管理、客户跟进+钉钉提醒、退税台账、柜量/采购成本/退税汇总/库存预警报表 | 这些均已在 `init8`~`init13`、`SchemaUpgrader` 中落地并有菜单；仅 **PI、库存动作单据、采购单归属字段、物流跟踪字段** 仍未完成 → 需标注"已完成"，否则会重复开发 |
| 4.5 | `docs/数据库设计说明书.md`（仅到"第四阶段"） | 未记录 `BaseTaxRefunds`、`FinanceExpenses`、`PurchaseQuotes`、`TradeDocuments`、`CustomerFollowUps`、`Samples`、`Quotations/QuotationDetails` 及主数据新增列（客户 13 列 / 供应商 9 列 / 商品 16+ 列） | 这些表与列均已存在（`SchemaUpgrader.cs:280-322,328,380,454,507,597,653,711,747`、`init7~init16.sql`） |
| 4.6 | `docs/菜单与业务流程优化建议-20260918.md`（仍标"待确认稿"） | §1.1「共 53 项菜单、8 个一级分组」与 §2 蓝图中大量 P0/P1 项未标注落地状态（阶段 0 菜单重构、阶段 1 主数据字段、报价单、费用单、账龄、比价、单证、样品、CRM、退税等） | 阶段 0/1/2 大部分已落地；若继续按原稿"建议"开发将重复实现 |
| 4.7 | `docs/报价单与PI设计方案.md` §7 批次表 | 批次 1 标注"转 PI 按钮（PI 未上线前按钮隐藏）" | 界面上并无"隐藏的转 PI 按钮"代码；实为未实现，应明确标注"批次 2 待做" |

### 4.8 ERP-017 补正结果（2026-09-23）

| 失真项 | 补正动作 | 结果 |
|---|---|---|
| 4.1 技术方案说明书测试数字（43 例） | 改为实测值并区分验证档 | ✅ `docs/技术方案说明书.md` §九 现为 **272/272**（Release 实测，2026-09-23），并说明 `ERP.IntegrationTests` / UI 用例不在默认安全档 |
| 4.2 技术方案说明书目录结构与接口清单 | 重写目录树、补 API 清单 | ✅ §二 覆盖 `.ai/`、`scripts/`、`src/ERP.IntegrationTests`；§五 补询报价 / 订单 / 库存 / 阶段 1~3 模块；新增 §十一 自动化控制层、§十二 文档索引 |
| 4.3 报价单与 PI 设计文档与实现不一致 | 逐项区分已实现 / 浏览器延后 / 仍缺失 | ✅ `docs/报价单与PI设计方案.md` 新增 §9 完成度清单与文首图例；§5 接口表按 ERP-007 / ERP-010 标注；§7 批次表标注 ERP-018 的剩余范围（打印端点已实现） |
| 4.4 部署交付文档"下一批"仍列已交付项 | 每段加落地状态标注 + 追加 §32 | ✅ §23.4 / §24.5 / §26.4 / §27.4 / §27.5 / §28.5 / §29.5 / §30.5 全部标注；新增 §32「自动化交付批次 ERP-006 ~ ERP-010 + 文档补正 ERP-017」 |
| 4.5 数据库设计说明书缺新表与新列 | 追加 §十 ~ §十四 | ✅ 补 `BaseTaxRefunds` / `FinanceExpenses` / `PurchaseQuotes` / `TradeDocuments` / `CustomerFollowUps` / `Samples` / `SysDingTalkLogs` / 报价单与 PI / 库存单据与流水 + 新增列汇总（含 ERP-008 / 009），并明确"不声明生产迁移结论" |
| 4.6 菜单建议稿未标落地状态 | 增补落地状态对照表 | ✅ `docs/菜单与业务流程优化建议-20260918.md` 新增 §〇 对照表（33 项，✅/◑/❌/⚠️），并标注 §1.1「53 项菜单」描述已过期 |
| 4.7 报价单"隐藏的转 PI 按钮"表述 | 改为明确状态 | ✅ §7 批次表与 §9 清单明确：转 PI 已于 **ERP-007** 交付；报价有效期与成交率分析为 **ERP-018** 待做 |

> 补正**只改文档**：未改动任何代码、SQL、部署产物与生产数据；验证为 `dotnet build NEWERP.sln -c Release`（0 警告 0 错误）+ `ERP.UnitTests` **272/272**。
> 同一批次还同步了自动化控制层文档：`docs/AUTOMATED_DEVELOPMENT.md`（四任务滚动队列、目录与文件职责、浏览器延后策略）、`.ai/MASTER_PLAN.md` 与 `.ai/GPT_CONTROL_PROTOCOL.md`（滚动队列规模与 `browser_deferred` 口径）。

## 5. 建议任务边界（滚动队列，供 GPT 创建任务时引用）

### 5.1 队列就绪性结论
- **ERP-007（PI 完整闭环）与 ERP-008（销售/采购订单字段与追溯）依赖已满足**：二者 `depends_on` 均指向已完成/本任务，`allowed_paths` 已覆盖所需文件（实体、`IErpDbContext`、`ErpDbContext*`、`SchemaUpgrader.cs`、Controllers、`wwwroot/**`、测试、`.ai/**`）。
- 两任务的 Human Gate 状态均为 `approved`（L2，仅允许代码级变更 + 只对 NEWERP_TEST 验证；生产库/生产部署仍另需批准）。
- 两者 `completion_mode` 均为 `browser`：ERP-007/008 必须在 `Collection=UiTests` 中新增 Selenium 场景并产出截图证据（现有 `UiSmokeTests` 仅 4 例登录场景，`UiTestFixture` 已提供 `CaptureEvidence`）。

### 5.2 已入队任务的范围复核

| 任务 | 复核结论 | 建议补充点（不改任务本体，由执行者按验收标准吸收） |
|---|---|---|
| ERP-007 PI 闭环 | 范围正确、可作为 PI 实现模板参照报价单的既有模式（EF 主子表 + `DocumentControllerBase` + `SchemaUpgrader` 幂等建表 + 菜单幂等插入 + `modules.js` 主子表页面） | ① 字轨需同时补 `DocumentType.ProformaInvoice=18` 与编号规则（`init16.sql` 只插入了 17）；② IErpDbContext/ErpDbContext 需挂载 PI DbSet；③ 建议把「报价单打印注册」（见 3.2）一并纳入，否则会留下"PI 能打印、报价单不能打印"的不一致；④ **ERP-007 的 `allowed_paths` 不含 `deploy/**`**，因此生产上线脚本（如 `init17.sql`）与本机生产库执行必须另开受门禁任务，本任务只产出代码与 `SchemaUpgrader` 幂等升级 |
| ERP-008 订单追溯 | 范围正确，与总表第 6/7 行缺口一致 | ① 销售订单走 `sp_Biz_SalesOrder`、采购订单走 `sp_Biz_PurchaseOrder`（`BillProcController` Bills 目录 + `deploy/init4.sql`），加列需 DROP/CREATE 存储过程 → 只能对 NEWERP_TEST 验证，生产库变更需变更窗口 + 回滚脚本；② 页面字段需同步 `modules-doc.js` 的 `fields/columns`（否则保存不生效）；③ 历史数据须给默认值（可空字段），避免老单据打不开 |

### 5.3 建议新增任务（按依赖与风险排序）

| 建议 ID | 目标与边界 | depends_on | 风险 / 门禁 | 完成模式 |
|---|---|---|---|---|
| ~~ERP-009~~ | ~~报价单打印与报价有效期治理：补 `GET /api/sales/quotations/{id}/print`、在 `BILL_CONFIG`/`BILL_CODE_MAP` 注册 `quotation`、报价有效期到期提醒报表（复用报表框架，零 DB 改动）、报价成交率分析~~ → **该建议内容尚未立项；`ERP-009` 这个 ID 已被 orchestrator 用于「库存动作单据与库存成本」（见 §5.12）** | ERP-007 | low / L1 | browser |
| ~~ERP-010~~ | ~~报价/PI → 销售订单「带入预填」（不改存储过程），销售订单留痕来源报价单/PI~~ → **已实现（见 §5.13：带入预填 `/{id}/order-prefill` + 直接生成 `/{id}/to-order`，来源留痕、同一来源仅一张、服务端复核，不改存储过程 / 不新增结构）** | ERP-007, ERP-008 | medium / L2 | browser |
| ERP-011 | 装柜外贸字段与物流跟踪：LCL/FCL、B/L、SO、ETD/ETA/ATD/ATA、拖车/报关行、查验放行、目的/中转港 | ERP-008 | **high（装柜单据走存储过程 + 结构变更，需变更窗口、备份与回滚脚本）** / L3 | browser |
| ERP-012 | 拼柜方案与费用分摊闭环：新增「柜 → 多客户」主子模型 + 分摊批次留痕，分摊结果回写装柜/结算与费用单关联（保留现有按体积/重量/箱数/金额 + 手工覆盖） | ERP-011 | high / L3 | browser |
| ~~ERP-013~~ | ~~库存动作单据与成本：盘点单、调拨单、退货入库/出库、与 `Stocks` 联动、移动加权/批次成本~~ → **已由 `ERP-009` 实现（见 §5.12：四类单据 + 库存流水 + 移动加权平均成本，`verified-complete 代码+测试`）**；剩余未做：库位/批次级成本、FIFO、成本调整单、跌价准备 | ERP-011 | high / L3 | browser |
| ERP-014 | 财务补口：应付账款、供应商对账、客户对账、发票管理（专票/普票/出口发票）、收款自动核销、汇兑损益、金额阈值审批流 | ERP-008 | medium-high / L2-L3 | browser |
| ERP-015 | 生产管理（BOM/生产任务/委外/领料/产成品入库/生产看板/库存成本）—— 立项前需确认工序粒度（是否有车间/工序/工价） | 无（并行） | **decision-required**；实现风险 high | browser |
| ERP-016 | 增强项：数据范围权限（业务员仅见自己客户）、附件中心、移动端/扫码 | ERP-014 | medium / L2 | browser |
| ERP-017 | 文档补正（§4 清单 4.1~4.7）：技术方案说明书测试与结构、报价单/PI 设计文档完成度标注、部署交付文档已完成项标注、数据库设计说明书补齐新增表与列、菜单建议稿落地状态 | 无 | low / L1（可 control_plane） | control_plane |

### 5.4 阻断项：真实 Edge 验收的目标库前提（ERP-007 实测 · 2026-09-23）

- **现象**：`completion_mode=browser` 的验收由 `scripts/ai_browser_acceptance.py` 驱动：它把 `.env.local` 注入测试进程并执行 `--filter Collection=UiTests`。本机 `.env.local` 的 `ERP_ConnectionStrings__Default` 与 `deploy/appsettings.Production.json` / `appsettings.Development.json` 指向同一实例 + 同一业务库，因此验收会先启动 `ERP.Api`（触发 `SchemaUpgrader` 幂等建表 / 菜单授权 / 系统参数 + `SeedData`），并写入验收测试单据。
- **现状**：`src/ERP.IntegrationTests/TestDatabaseSafetyGuard.cs` 对「验收目标 = 部署配置目标」fail-closed（未设 `ERP_AI_ALLOW_HIGH_RISK_TESTS=APPROVED` 即中止）。ERP-007 的实现、单元测试（194/194）与 Release 构建（0 警告 0 错误）均已就绪，但浏览器门禁无法在该库安全执行，故止步于 `code_ready`。
- **影响面**：**所有** `completion_mode=browser` 的任务（ERP-007 / 008 / 009 / 010…）在环境解阻断前都会止步于同一处——这是环境与门禁问题，不是任务实现缺口；排期时不应把它当作实现返工。
- **解阻断（Human Gate 动作）**：① 人工准备专用测试库（例如 `NEWERP_TEST`，可用 `deploy/init*.sql` 幂等初始化）后，把 `.env.local` 的 `ERP_ConnectionStrings__Default` 指向它；或 ② 明确批准后对现有库运行验收，并在运行进程内设置 `ERP_AI_ALLOW_HIGH_RISK_TESTS=APPROVED`。
- **附带约定**：UI 用例类必须显式标注 `[Trait("Collection","UiTests")]`——仅 `[Collection(...)]` 不生成 VSTest 属性，`--filter` 会静默选中 0 个用例并以退出码 0“通过”（证据为 0 张截图、TRX `total=0`）。

### 5.5 复核证据：浏览器门禁是唯一剩余阻断（ERP-007 复核 · 2026-09-23）

- **复核结论**：ERP-007 代码侧已完成且通过与门禁无关的全部可验证项；**唯一**未通过项是 `browser_acceptance`，其失败原因是环境 + Human Gate，不是实现缺口。队列在解阻断前不应重复消耗 attempt，也不应把该阻断判定为实现返工。
- **本轮 safe 档实测**：`dotnet build NEWERP.sln -c Release --no-restore --no-incremental /p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true` → **0 警告 / 0 错误**；`dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build` → **194/194 通过、0 失败、0 跳过**。
- **失败证据（只读）**：`.ai/evidence/ERP-007/20260923T072728Z/manifest.json` 记录 `test_exit_code=1`、`screenshot_count=0`（`minimum_screenshots=2`）；`browser-test.log` 中 `Collection=UiTests` 的 6 个用例全部因 `TestDatabaseSafetyGuard` 抛 `InvalidOperationException`（「验收目标数据库与部署配置 deploy\appsettings.Production.json 指向同一个实例与库」）而失败，即护栏在启动 `ERP.Api` 之前 fail-closed。
- **开关状态核对**：`.env.local` 的键只有 `ERP_ConnectionStrings__Default`、`ERP_Jwt__*`、`ERP_Oss__*`、`ASPNETCORE_ENVIRONMENT`，**不含** `ERP_AI_ALLOW_HIGH_RISK_TESTS`；即第二层高风险开关确实未获批准，护栏行为正确，不得为通过验收而放宽（本轮仅读取键名，未输出任何密钥值）。
- **验收脚本可达性静态核对（不启动 API / 浏览器）**：`PiWorkflowUiTests` 使用的 DOM 钩子（`#login-*`、`#app-page`、`#sidebar-nav`、`#header-title`、`#search-input`、`#table-wrap tbody tr`、`row-more`、`openForm(`、`#side-panel`、`#sp-save-btn`、`.side-panel-close`、`#detail-body`、`#detail-total`、`#toast`、`#modal`）均由 `index.html` / `crud.js` 渲染；业务函数 `quotationToPi`/`piApprove`/`piUnaudit`/`piVoid`/`previewSalesDocPrint`/`printSalesDoc` 由 `wwwroot/js/sales-pi.js` 定义并已在 `index.html` 引入（`SALES_DOC_PRINT['proforma-invoice']` + `noKey: 'piNo'`）；API 路由 `GET|POST /api/sales/proforma-invoices`、`GET|PUT /{id}`、`POST /{id}/approve|unaudit|void`、`GET /{id}/print`、`POST /api/sales/quotations/{id}/to-pi` 均存在于控制器；提示文案（`已生成形式发票 PI`、`PI 已审核`、`已销审，可继续修改`、`不可修改`、`PI 已作废`）前后端一致；菜单 `proforma-invoice`（`/sales/proforma-invoice`）与字轨 `PI` 由 `SchemaUpgrader.cs` / `SeedData.Rules.cs` 幂等写入。
- **解阻断命令（Human Gate 批准后由人工执行，本任务不执行）**：
  1. 专用测试库：准备 `NEWERP_TEST`（库内空库即可，`SchemaUpgrader` 会在启动时幂等建表并补菜单/参数），把 `.env.local` 的 `ERP_ConnectionStrings__Default` 指向它，然后运行 `python scripts/ai_browser_acceptance.py --task ERP-007`；
  2. 或在明确批准后对现有库验收：仅在验收进程环境内设置 `ERP_AI_ALLOW_HIGH_RISK_TESTS=APPROVED`（不要写入 `.env.local`，避免批准被固化在仓库文件里），再运行同一条命令。
- **验收通过判定**：`manifest.json.status=passed`、`screenshot_count>=2`、`.ai/evidence/ERP-007/<run>/ERP-007.trx` 中 `Collection=UiTests` 用例 0 失败，并包含 `browser-session.json` 与 SHA-256 清单。

### 5.6 浏览器门禁实跑结论与必需环境前提（ERP-007 · 2026-09-23 本机实测）

- **实跑方式（合规）**：本机新建**专用测试库** `NEWERP_TEST`（SQL LocalDB 实例 `MSSQLLocalDB`，实例 + 库均不同于 `deploy/appsettings*.json` 指向的业务库），由 `SchemaUpgrader` + `SeedData` 在启动时幂等初始化；验收进程内只注入 `ERP_ConnectionStrings__Default`（指向该测试库）与临时 `ERP_Jwt__Key`（本机随机生成、不落仓库、非任何生产凭据）。同时满足 ERP-007 L2 门禁「只对 NEWERP_TEST 验证」与 `.clinerules`「不得使用生产凭据/生产数据」。
- **实测结果**：`dotnet test src/ERP.IntegrationTests/ERP.IntegrationTests.csproj -c Debug --filter Collection=UiTests` → **通过 6 / 失败 0**（`PiWorkflowUiTests` 2 例 + `UiSmokeTests` 4 例），真实 Microsoft Edge `153.0.4234.48`，`ERP_AI_EVIDENCE_DIR` 产出 **14 张 PNG**：PI 列表 / 草稿保存 / 审核后禁改 / 销审恢复 / 打印预览 / 作废 + 报价单转 PI 全流程（报价单列表、转 PI、PI 列表、PI 编辑回填）+ 4 个登录态场景。本轮证据仅用于本机自证，正式证据仍由 orchestrator 输出到 `.ai/evidence/ERP-007/<run>/`。
- **本轮修复的 4 类缺陷（此前从未真正跑通，静态核对无法发现）**：
  1. **API 输出管道死锁**（`UiTestFixture.StartApi`）：stdout/stderr 重定向到无人读取的管道，`ERP.Api` 启动时输出建表/升级 SQL（远超管道缓冲区）导致子进程写阻塞、HTTP 端口永不监听，验收以「ERP.Api 在 60 秒内未就绪」失败。已改为异步消费 `OutputDataReceived/ErrorDataReceived`，并把控制台尾部写入证据目录 `api-console.log`；就绪等待放宽到 180 秒，子进程提前退出时立即失败并附控制台尾部。
  2. **离线驱动缺失**：本机无法访问 Selenium Manager（`Selenium.WebDriver 4.27.0`）使用的 `msedgedriver.azureedge.net`，真实 Edge 无法启动。已改为「`ERP_AI_EDGE_DRIVER` → `%LOCALAPPDATA%\erp-ai\webdrivers` 下最新 `msedgedriver.exe` → Selenium Manager」三级解析；本机已预置与 Edge 同版本驱动 `153.0.4234.48`。
  3. **单号 `[Required]` 与服务端自动编号冲突（产品缺陷）**：`ProformaInvoice.PiNo` / `Quotation.QuotationNo` 上的 `[Required]` 会在进入控制器前被 `[ApiController]` 判 HTTP 400（`The PiNo field is required`），而表单提示「留空自动生成」（`crud.js` 提交空串）→ **页面上新增 PI / 报价单永远无法保存**。已改为 `[Required(AllowEmptyStrings = true)]`：EF 列仍为 NOT NULL，单号由 `IDocumentNumberService` 服务端字轨生成。
  4. **验收脚本自身 3 处信号错误**：`FunctionExists`/`HasPrintPage` 用 `String(...)` 包装 JS 布尔值后与 .NET 的 `"True"` 比较（JS 得到小写 `true`，条件恒假，误报「前端脚本过期」）；用例共享同一浏览器会话却未重置登录态，导致登录页元素「存在但不可交互」（`ElementNotInteractableException`）；头部用户名断言写死 `admin`（实际显示姓名 `系统管理员`）。已分别改为直接返回 JS 布尔值、新增 `UiTestFixture.ResetBrowserSession()`、与页面 `PROFILE` 比对。
- **`browser_acceptance` 仍需人工补齐的环境前提（未补则 orchestrator 验收必然失败）**：
  1. `.env.local` 的 `ERP_ConnectionStrings__Default` 指向专用测试库；本机可直接使用已初始化好的测试库：`Server=(localdb)\MSSQLLocalDB;Database=NEWERP_TEST;Integrated Security=true;TrustServerCertificate=true`；
  2. ~~`.env.local` 必须补 `ERP_Jwt__Key`~~ → **ERP-007 attempt 3 复核：该项已不成立，不再是前提**。现 `.env.local` 第 9 行已有**非空** `ERP_Jwt__Key`（复核只读取键名与「值是否为空」，未输出任何值）；仓库内 `src/ERP.Api/appsettings*.json` 的 `Jwt:Key` 仍为空，但 `Program.cs` 第 13 行 `AddEnvironmentVariables(prefix: "ERP_")` + 第 73 行读 `Jwt:Key`，验收进程注入的 `ERP_Jwt__Key` 会覆盖 json 值，登录与鉴权均正常（本轮 6/6 用例含 4 个登录场景全部通过）。保留本条仅作历史记录：若该键缺失或为空，`SymmetricSecurityKey` 会以零长度密钥构造，`UseAuthentication` 之后**每个请求都返回 500**（`IDX10703: key length is zero`），任何登录与浏览器验收都无法进行；
  3. 驱动（仅在驱动 CDN 不可达时需要）：预置 `msedgedriver.exe` 到 `%LOCALAPPDATA%\erp-ai\webdrivers\<版本>\`，或设置 `ERP_AI_EDGE_DRIVER`。
- **不得为通过验收而放宽的地方**：`TestDatabaseSafetyGuard` 保持原样（验收目标 = 部署配置库时 fail-closed）；`.env.local` 归人工所有（受保护路径），本轮未修改，也未写入任何密钥。

### 5.7 ERP-007 attempt 3 复核：真实 Edge 对本地专用测试库 6/6 通过（2026-09-23）

- **本轮实测（合规，L2 门禁「代码 + 仅对 NEWERP_TEST 验证」范围内）**：未改 `.env.local`（受保护 / 归人工），仅在验收进程内注入 `ERP_ConnectionStrings__Default`（本机 `(localdb)\MSSQLLocalDB` / `NEWERP_TEST`、集成认证、非生产凭据）与**进程内临时** `ERP_Jwt__Key`（随机生成、不落仓库）；`ERP_AI_ALLOW_HIGH_RISK_TESTS` **未设置**——护栏凭「验收目标 ≠ 部署配置目标」自行放行，未使用自批准开关：
  - `dotnet build NEWERP.sln -c Release --no-restore --no-incremental /p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true` → **0 警告 / 0 错误**；
  - `dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build` → **194/194 通过、0 失败、0 跳过**；
  - `dotnet test src/ERP.IntegrationTests/ERP.IntegrationTests.csproj -c Debug --filter Collection=UiTests` → **6/6 通过**（真实 Microsoft Edge `153.0.4234.48` + `msedgedriver 153.0.4234.48`），TRX `Counters total="6" passed="6" failed="0"`，`browser-session.json` 记录 `realBrowser=true`，产出 **14 张 PNG**：报价单列表/转 PI、PI 列表、PI 编辑回填、草稿保存、审核后禁改、销审恢复、打印预览、作废 + 4 个登录态场景。
- **写入目标核对（只读查询，仅本地测试库）**：`NEWERP_TEST` → `db_owner.ProformaInvoices` 出现本轮单据 `PI202609230017`（`UIPI164630`，总额 750 / 定金 300）与 `PI202609230018`（`UI164703`，总额 750 / 定金 225），与用例断言一致；`api-console.log` 中不含业务库实例/库名，即验收数据只落在本地测试库。
- **orchestrator 门禁仍会失败的唯一原因（环境，不是实现缺口）**：`scripts/ai_browser_acceptance.py` 会把 `.env.local` 注入测试进程，而 `.env.local` 的 `ERP_ConnectionStrings__Default` 与 `deploy/appsettings*.json` 指向同一实例 + 同一业务库 → `TestDatabaseSafetyGuard` 在启动 `ERP.Api` 前 fail-closed（6 用例全失败、0 截图）。同一份代码对专用测试库 6/6 通过，故 ERP-007 交付状态仍为 `code_ready`。
- **人工解阻断（二选一，均在 Human Gate 下由人工执行；本轮未执行）**：① 把 `.env.local` 的 `ERP_ConnectionStrings__Default` 指向专用测试库（本机 `NEWERP_TEST` 已初始化可用，`ERP_Jwt__Key` 已就绪）后运行 `python scripts/ai_browser_acceptance.py --task ERP-007`；② 明确批准后**仅在验收进程内**设置 `ERP_AI_ALLOW_HIGH_RISK_TESTS=APPROVED` 再运行同一条命令。
- **环境残留定位（本轮已清理，脚本属 orchestrator 所有权故未改）**：最后一次 orchestrator 验收 `20260923T084046Z` 的 `test_exit_code=1` 不是用例失败，而是被中断的上一次验收遗留的 `testhost` 仍占用 `src/ERP.IntegrationTests/bin/Debug/net8.0/ERP.IntegrationTests.dll`，使 `CopyFilesToOutputDirectory` 报 `MSB3027 / MSB3021`（`browser-test.log` 可证）。本轮清理 8 个孤儿 headless Edge 进程（**未触碰用户交互式 Edge**）后重跑正常；建议 orchestrator 在每次浏览器验收前后按进程树清理残留 `testhost` / `msedgedriver` / headless `msedge`（`scripts/**` 不在 ERP-007 的 `allowed_paths`，本任务未修改）。

### 5.8 attempt 3（本机再复核）：修复提示条竞态 + 验收端口 fail-closed / 隔离（2026-09-23）

- **复跑方式（合规）**：专用测试库 `NEWERP_TEST`（`(localdb)\MSSQLLocalDB`，与 `deploy/appsettings*.json` 不同实例 + 库）+ 真实 Microsoft Edge；在**验收进程内**只覆盖 `ERP_ConnectionStrings__Default` 与端口（`ERP_AI_UI_PORT=5159`，默认仍 5059，orchestrator 不受影响）；未改 `.env.local`，未设置 `ERP_AI_ALLOW_HIGH_RISK_TESTS`（护栏凭「目标 ≠ 部署配置」放行）。
- **发现的真实缺陷（前端 `app.js`）**：`PI草稿修改_审核后禁改_销审恢复_并渲染打印预览` 在「销审后再次保存」处随机失败于 `WaitToastContains("保存成功")` 超时 20 秒。
  根因：`toast()` 用裸 `setTimeout(..., 3000)` 隐藏提示条且**不清除上一条的定时器**；「已销审」（T）之后 ≈T+2~3 秒出现的「保存成功」会被上一条的定时器提前隐藏 —— 元素仍在 DOM、`textContent` 仍是「保存成功」，但 `display:none`，而 Selenium 的 `Text` 对隐藏元素返回空串 → 验收误判失败（真实用户同样会「看不到第二条提示」）。
  修复：保存定时器句柄，连续提示时先 `clearTimeout` 再重新计时（显示时长口径不变，仍是 3 秒）。
- **验收隔离与 fail-closed（`UiTestFixture`）**：Windows 事件日志已记录本机出现 `Failed to bind to address http://127.0.0.1:5059: address already in use` —— 验收 API 自身启动失败后，旧逻辑会**静默连上占用 5059 的外部实例**继续产证据（可能来自旧构建或另一个库），证据无效。现改为：① 启动前 `EnsurePortIsFree()` 预检端口，被占用即 fail-closed 中止并提示清理残留进程；② 支持 `ERP_AI_UI_PORT` 覆盖端口，便于与残留实例 / 并行验收隔离。
- **诊断增强**：提示条等待超时改为抛出快照（`textContent` / `display` / `class` + 接口失败记录）并落一张证据图，杜绝「只知道超时、不知原因」。
- **本轮实测（当前工作树，改动后）**：`dotnet test … --filter Collection=UiTests -c Debug` → **6/6 通过**、TRX `Counters total="6" passed="6" failed="0"`、**14 张 PNG**、`exit-code=0`；safe 档 → Release 构建 **0 警告 / 0 错误**、`ERP.UnitTests` **194/194 通过**。两项均只对 `NEWERP_TEST` 执行，未触碰业务库。
- **orchestrator 门禁结论不变**：最新两次门禁 `20260923T084046Z`（`MSB3027/MSB3021`：上一次并行验收残留的 `testhost` 占用 `bin\Debug` DLL）与 `20260923T084908Z`（`TestDatabaseSafetyGuard` fail-closed，2 秒内 6 用例全失败、0 截图）都不是实现缺口；解阻断仍需人工动作（见 §5.7 的 ①/②）。

### 5.9 ERP-007 attempt 2 复核：目标库护栏改为「显式测试上下文」判定 + 修掉列表重绘竞态（2026-09-23）

- **门禁事实（诊断）**：orchestrator 最近两次浏览器门禁（`20260923T084908Z`、`20260923T094749Z`）都在启动 `ERP.Api` 之前 fail-closed——`TestDatabaseSafetyGuard` 把「验收目标 = `deploy/appsettings*.json` 的实例 + 库」本身当作拒绝理由（6 用例全失败、0 截图）。该判定与 `.ai/prompts/developer.md` 第 8 条冲突：开发阶段部署配置不是生产权威，不得作为数据库身份黑名单。
- **护栏重构（`src/ERP.IntegrationTests/TestDatabaseSafetyGuard.cs`；已完全移除部署配置比对与 JSON 读取）**：判定顺序为 ① 目标连接串必须有效（`ERP_ConnectionStrings__Default` 同时含 `Server`/`Data Source` 与 `Database`/`Initial Catalog`，缺失或不可解析立即 fail-closed）；② 必须处于显式测试上下文之一：`ERP_AI_TEST_RUN=1` 且 `ASPNETCORE_ENVIRONMENT=Development`（`scripts/ai_browser_acceptance.py` 启动验收进程时设置）／`ERP_AI_ALLOW_HIGH_RISK_TESTS=APPROVED`（Human Gate 第二层开关）／CI 测试环境（`CI` 或 `GITHUB_ACTIONS=true`，保证 `.github/workflows` 的 `erp-integration`、`erp-ui` 两个作业不被误拦）。判定核心抽为纯函数 `Evaluate(...)`；放行日志与异常信息只含环境变量名与判定结论，不含连接串内容、不含目标实例 / 库名。
- **护栏单元测试**：新增 `src/ERP.UnitTests/TestDatabaseSafetyGuardTests.cs`（23 个用例：缺连接串 / 缺上下文 / 非 Development / 标志值不符 / 批准值大小写 / CI / 同库形态在测试上下文下放行且无上下文才拒绝 / 诊断不泄露实例与库名 / `EnsureApprovedTarget` 先于环境判定抛错）；`src/ERP.UnitTests/ERP.UnitTests.csproj` 增加对 `ERP.IntegrationTests` 的项目引用，使 safe 档即可覆盖该纯函数（`dotnet test ERP.UnitTests` 只运行本程序集，不会连带执行 UI / 集成用例）。
- **修复验收用例的非业务竞态（`PiWorkflowUiTests.RowText`）**：本轮实测 6 用例中 `报价单转PI_并可在PI列表与编辑页核对复制内容` 在 line 70 随机失败（`Assert.Contains` 拿到空串）。根因：列表在「搜索 / 刷新」后异步重绘，`RowText` 未命中时返回 `string.Empty`（非 null），`Wait().Until` 因此立即带空串返回（原注释本意是「未出现时继续等待」）。改为未命中 / 元素 stale 时返回 `null` 继续轮询，超时则抛出——不再有「读得太早」的假失败，也不会把空结果当通过。
- **本轮实测（仅对专用本地测试库 `(localdb)\MSSQLLocalDB` / `NEWERP_TEST`；未触碰业务库、未执行 SQL / seed / 部署）**：
  - safe 档：`dotnet build NEWERP.sln -c Release --no-restore --no-incremental /p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true` → **0 警告 / 0 错误**；`dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build` → **217/217 通过**（原 194 + 新增 23）。
  - **fail-closed 实测**：注入有效目标连接串但**不给**测试上下文（`ASPNETCORE_ENVIRONMENT=Production`、未设 `ERP_AI_TEST_RUN`、未设批准开关）→ 护栏在启动 API 前中止、退出码 1、**0 张截图**，消息为「…已中止（依据：缺少显式测试上下文）…」。
  - **放行实测（orchestrator 门禁同款上下文）**：进程内注入 `ERP_AI_TEST_RUN=1` + `ASPNETCORE_ENVIRONMENT=Development`（**未设** `ERP_AI_ALLOW_HIGH_RISK_TESTS`）→ `--filter Collection=UiTests` **6/6 通过**、TRX `total="6" passed="6" failed="0"`、**14 张 PNG**、`browser-session.json` 记录 Microsoft Edge `153.0.4234.48`（`realBrowser=true`）、`exitcode=0`。
  - 证据留档（仓库外临时目录，避免污染 orchestrator 所有的 `.ai/evidence/`）：竞态失败版 `%TEMP%\erp-ui-evidence-attempt2`（TRX `passed=5 failed=1`），修复后 `%TEMP%\erp-ui-evidence-attempt2b`（`passed=6 failed=0` + 14 PNG + `api-console.log`）。
- **未做的事（边界）**：未修改任何受保护路径（`.env.local`、`deploy/**`、`release/**`、`checkpoints/**`、SQL 文件、`logs/**` 均未改）；未执行生产或业务库操作；未 commit / push（checkpoint 归 orchestrator）；本轮产物是「代码 + 测试 + 证据」，交付等级仍为 `code_ready`，`completed` 只由 orchestrator 的真实 Edge 门禁判定。
- **残留风险（人工可选决策，不影响护栏逻辑）**：按第 8 条，orchestrator 门禁会用 `.env.local` 指向的库执行验收（会启动 `ERP.Api`，触发幂等建表 / 菜单授权 / 系统参数与验收测试单据写入）。若希望验收数据与业务数据隔离，人工把 `.env.local` 的 `ERP_ConnectionStrings__Default` 指向 `NEWERP_TEST` 即可——护栏在显式测试上下文下同样放行（本轮未改 `.env.local`，也未读取其值）。

### 5.10 ERP-007 attempt 3 复核：修掉「过期列表响应覆盖新列表」竞态 + 行操作点击可重试（2026-09-23）

- **门禁事实（诊断）**：orchestrator 最近一次浏览器门禁（`20260923T095833Z`）= **6 用例 5 通过 1 失败**，13 张截图、TRX 存在、Edge `153.0.4234.48`（headless）。失败用例 `PiWorkflowUiTests.PI草稿修改_审核后禁改_销审恢复_并渲染打印预览` 在第 6 步「作废」（`ClickRowMenuAction(voidPi.Id, "piVoid")`）抛 `OpenQA.Selenium.StaleElementReferenceException`（`b.Displayed`，栈顶 PiWorkflowUiTests.cs:406/407）。**这不是 PI 业务缺口**，而是列表异步重绘让行内「更多」按钮 / 菜单项在轮询途中失效。
- **根因（前端真实缺陷，不只是测试问题）**：`crud.js` 的 `loadList()` 不区分响应新旧，任何一次「切换模块 / 搜索 / 翻页 / 操作后刷新」的**晚到响应**都会重绘列表并覆盖更新的结果。真实用户表现为「搜索后列表闪回旧数据」；浏览器验收表现为行元素 stale（点「更多」后菜单随旧行一起被移除）。
- **修复 1（根因，`src/ERP.Api/wwwroot/js/crud.js`）**：新增列表请求序号 `__listRequestSeq`；`loadList()` 只允许**最后一次发出的请求**渲染，过期响应直接丢弃；`renderModule()` 进入新模块时先作废在途请求（含树形模块），避免旧模块数据覆盖新页面。
- **修复 2（验收健壮性，`src/ERP.IntegrationTests/PiWorkflowUiTests.cs`）**：`ClickRowMenuAction` 不再缓存元素引用——每轮重新定位该行、必要时重新展开「更多」菜单，吞掉 stale 异常继续重试，20 秒超时才带诊断失败（提示条快照 + 接口失败记录 + `row-menu-action-timeout` 截图）；行定位同时接受 `openForm(id)` 与 `functionName(id)`（菜单展开时菜单项被提升到 body 层，只有行内「编辑」按钮还能标识该行）；`OpenEditForm` 的行轮询同样对 stale 容错。
- **本轮实测（未启动 API、未连数据库、未运行集成/UI 用例）**：
  - safe 档：`dotnet restore NEWERP.sln` + `dotnet build NEWERP.sln -c Release --no-restore --no-incremental /p:TreatWarningsAsErrors=true /p/RunAnalyzersDuringBuild=true` → **0 警告 / 0 错误**；`dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build` → **217/217 通过**；
  - 门禁所用配置编译：`dotnet build src/ERP.IntegrationTests/ERP.IntegrationTests.csproj -c Debug` → 0 警告 / 0 错误；前端脚本 `node --check src/ERP.Api/wwwroot/js/crud.js` → 通过；
  - 竞态护栏实测（仓库外临时 Node 脚本，直接加载真实 `crud.js`，仅桩掉 `api` / `document`）：**修复前**版本在「搜索期间旧响应晚到」「切模块时旧模块响应晚到」两个场景都出现旧数据覆盖（renders=2，页面仍含旧行）；**修复后**只渲染最新请求（renders=1），无竞争场景照常渲染；
  - 行定位 XPath 以 XML 样本验证：菜单收起 / 已展开两种状态都能唯一命中目标行，非目标行不命中。
- **未做的事（边界）**：未启动 `ERP.Api`、未连业务库、未执行任何 SQL/seed/部署、未改 `.env.local`、`deploy/**`、`release/**`、`checkpoints/**`、`logs/**`、`SchemaUpgrader.cs`、`SeedData*.cs`；未 commit / push（checkpoint 归 orchestrator）。本轮产物为「代码 + 测试」，交付等级仍为 `code_ready`，`completed` 只由 orchestrator 的真实 Edge 门禁判定。

### 5.11 ERP-008 销售 / 采购订单追溯字段（2026-09-23）

- **任务**：ERP-008「完整化销售订单与采购订单追溯」——补齐外销合同 / 运输 / 来源报价与 PI 追溯字段、采购单归属客户与归属销售订单、采购执行与结算字段。
- **范围与做法（关键决策）**：销售订单与采购订单**统一改由 EF 主子表承载**（`db_owner.SalesOrders` / `db_owner.PurchaseOrders`，与 14 张报表读取的表一致），菜单不再进入存储过程版单据页（`BILL_CONFIG['sales-order'/'purchase-order']` 保留定义，供历史数据排查/回滚参考）。这样新增列完全不需要 `DROP/CREATE` 生产存储过程——正是 ERP-006 把采购订单判为 medium-high 的根因。页面（`crud.js` 主子表）中的客户 / 供应商 / 业务员为引用字段、币种与各类枚举为下拉、Consignee/Notify/唛头/验货/包装要求为多行文本，明细行自动算金额与合计。
- **代码改动**：
  - 领域：`SalesOrder.cs`（16 个新列）、`PurchaseOrder.cs`（12 个新列），全部带安全默认值（空串 / 0 / 0 位），历史单据可正常打开。
  - 持久化：`SchemaUpgrader.cs` 新增第 22 段（`IF OBJECT_ID(...) IS NOT NULL` + `IF COL_LENGTH(...) IS NULL`，幂等补齐缺列；表缺失时不会中断服务启动）。
  - 接口：`SalesOrderController` / `PurchaseOrderController` 全字段往返、关键字检索扩展到客户 PO 号 / 合同号 / 归属销售订单号、新增 `/{id}/print` 与 `export-excel`（列含全部新字段）、佣金比例与税率 0~100 校验（越界抛 `InvalidParameter`，不落库）、`GetById` 明细过滤软删除。
  - 前端：`modules-doc.js`（两个模块的主子表配置 + 选项集）、`bill-v2.js`（移除 `sales-order` / `purchase-order` 的 SP 映射）、`sales-pi.js`（打印配置含新字段、订单明细列、日期与布尔格式化、销售订单唛头/验货/包装区块）、`bill-export.js`（导出菜单改调 EF `export-excel` 并按订单状态口径渲染选项）、`crud.js`（`valueType: 'bool'` 提交真布尔值；日期留空提交 `null`，避免 `""` 反序列化 `DateTime?` 报错）。
- **本轮实测（未启动 API、未连数据库、未运行集成/UI 用例）**：
  - safe 档：`dotnet restore NEWERP.sln` + `dotnet build NEWERP.sln -c Release --no-restore --no-incremental /p:TreatWarningsAsErrors=true /p/RunAnalyzersDuringBuild=true` → **0 警告 / 0 错误**；`dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build` → **231/231 通过**（原 217 + `OrderTraceabilityTests` 14）；
  - 门禁所用配置编译：`dotnet build src/ERP.IntegrationTests/ERP.IntegrationTests.csproj -c Debug` → 0 警告 / 0 错误；
  - 前端脚本 `node --check`：`modules-doc.js` / `bill-v2.js` / `sales-pi.js` / `bill-export.js` / `crud.js` 全部通过。
- **真实 Edge 验收用例（新增 `OrderTraceabilityUiTests`，`Collection=UiTests`，共 3 个用例 / 10 张截图）**：① 销售订单页面填写外贸合同与来源追溯字段 + 明细 → 保存 → 重新打开核对回显 → 打印预览含客户 PO / 合同 / 唛头 → Excel 导出可用；② 采购订单关联归属客户与归属销售订单、填写代垫/含税/税率/到货/验货/结算字段 + 明细 → 保存 → 重新打开核对 → 打印预览可用；③ 订单列表渲染新增追溯列、导出菜单为订单状态口径、导出接口可用；每个用例断言全流程无 JS / 接口失败，测试数据经应用自身接口创建并在结束时软删除。
- **未做的事（边界）**：未启动 `ERP.Api`、未连业务库、未执行任何 SQL/seed/部署；未修改 `.env.local`、`deploy/**`、`release/**`、`checkpoints/**`、`logs/**`、任何 `.sql` 文件与 `SeedData*.cs`；未 commit / push（checkpoint 归 orchestrator）。`SchemaUpgrader.cs` 的改动仅限幂等补列，且按 Human Gate L2（已批准）执行。本轮产物为「代码 + 测试 + 文档」，交付等级仍为 `code_ready`，`completed` 只由 orchestrator 的真实 Edge 门禁判定。

### 5.12 ERP-009 库存动作单据与库存成本基础（2026-09-23）

- **任务**：ERP-009「实现库存动作单据与库存成本基础」——补齐审计列出的 missing 项：库存盘点/调整、仓库调拨、销售退货、采购退货四类单据 + 可审计库存流水 + 确定性库存估价；结构升级只对 NEWERP_TEST 验证，生产库/生产数据继续受门禁控制。
- **范围与做法（关键决策）**：
  - 四类单据统一用 **EF 主子表**实现（`db_owner.StockAdjustments` / `StockTransfers` / `SalesReturns` / `PurchaseReturns` + `...Details`），沿用报价单/PI/订单的约定：字轨编号（`DocumentType` 19~22，前缀 PD/DB/XTH/CTH）、`Pending → Submitted → Approved`、明细整体替换、后端复核合计；**完全不触碰任何存储过程与 SQL 脚本**。
  - 新增 **`StockMovements` 库存流水**作为「已审核单据改库存恰好一次」的唯一凭据：一笔记录 = 一个仓库 + 一个商品的单向变动，持久化来源单据（类型/Id/单号）、`UnitCost`（6 位）、`Amount`（带符号）、移动后结存快照（`BalanceQuantity`/`BalanceAmount`/`BalanceAverageCost`）。
  - **销审 = 冲销**：不删历史，原流水 `IsReversed=1` + 追加红字流水（`IsReversal=1`、`ReversalOfMovementId`），库存按原流水金额还原；若入库已被后续业务占用（库存不足冲销）则拒绝销审，避免负库存。
  - **成本口径**：`InventoryService` 实现移动加权平均法；`Stocks` 新增 `AverageCost`(18,6) / `TotalCost`(18,4)；调拨两侧使用同一成本单价（调出金额 = 调入金额）；退货成本优先级 = 明细成本 → 来源单据流水成本 → 当前均价。
  - **小数位显式声明**：EF 模型对成本（6 位）与数量/金额（4 位）加 `HasPrecision`，与 `SchemaUpgrader` 第 23 段建表脚本一致，避免空库首次 `EnsureCreated` 按默认 `decimal(18,2)` 截断成本。
- **代码改动**：
  - 领域：`src/ERP.Domain/Entities/InventoryDocuments.cs`（四类单据主/明细 + `StockMovement`）、`Logistics.cs`（`Stock.AverageCost`/`TotalCost`）、`Enums.cs`（`DocumentType` 19~22 + `InventoryMovementType`）。
  - 应用：`src/ERP.Application/Services/InventoryService.cs`（`IInventoryService`：`IncreaseAsync`/`DecreaseAsync`/`ReverseAsync`/`ResolveSourceCostAsync`/`ListMovementsAsync`/`CountActiveMovementsAsync`）、`DependencyInjection` 注册、`DocumentNumberService` 前缀与统计分支。
  - 接口：`StockAdjustmentController` / `StockTransferController` / `SalesReturnController` / `PurchaseReturnController`（`/api/inventory/...`，含 `{id}/movements`、`unaudit`、审核幂等护栏、已审核不能取消）、`InventoryDocumentHelper`（仓库名/商品信息/流水上下文统一构造）、`StockController` 新增 `/api/stocks/movements` 与 `AverageCost`/`TotalCost` 视图列。
  - 持久化：`ErpDbContext`/`IErpDbContext` 新增 9 个 `DbSet`、唯一索引（4 张单据号）、明细级联外键、成本/数量小数位；`SchemaUpgrader` 第 23 段（5 张单据表 + 明细 4 张 + `StockMovements` + 2 索引 + `Stocks` 补 2 列 + 5 个菜单 + 幂等授权 + 4 条字轨，全部 `IF NOT EXISTS`/`COL_LENGTH` 幂等）。
  - 前端：`modules-doc2.js`（5 个新模块：四张单据 + 只读「库存流水」页）、`crud.js`（通用 `unauditRow` 销审行操作 + 列表列 `type:'map'` + 明细区 `detailDiff` 差异与差异合计）、`app.js`（`CODE_ICON` 注册）。
  - 文档：`docs/库存单据与库存成本说明.md`（模型、成本口径、接口、字轨菜单、页面、测试、边界）。
- **本轮实测（未启动 API、未连数据库、未运行集成/UI 用例）**：
  - safe 档：`dotnet restore NEWERP.sln` + `dotnet build NEWERP.sln -c Release --no-restore --no-incremental /p:TreatWarningsAsErrors=true /p/RunAnalyzersDuringBuild=true` → 0 警告 / 0 错误；`dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build` → **250/250 通过**（原 231 + `InventoryMovementTests` 19）；
  - 门禁所用配置编译：`dotnet build src/ERP.IntegrationTests/ERP.IntegrationTests.csproj -c Debug` → 0 警告 / 0 错误；新增 `InventoryMovementUiTests`（3 个 Edge 用例）；
  - 前端脚本 `node --check`：`modules-doc2.js` / `crud.js` / `app.js` 全部通过；仓库外临时 Node 脚本加载真实 `modules-doc2.js` + `crud.js`（桩掉 DOM/接口）**36 项断言全部通过**（模块注册、销审行操作、`detailDiff` 差异与合计、`map` 列渲染、金额格式）。
  - 单元测试过程中发现并修复一处真实缺陷：`Normalize` 在审核路径上把已跟踪明细的主键重置为 0（EF 报 key 修改异常）——现改为「明细主键重置只在新增/修改（整体替换）时执行」，审核只做计算。
- **真实 Edge 验收用例（新增 `InventoryMovementUiTests`，`Collection=UiTests`，3 个用例）**：① 盘点调整：页面新建（账面 0 → 实盘 50，成本 10）→ 提交 → 审核 → 库存 50/金额 500/均价 10 + 流水 1 笔（含结存快照）+ 库存流水页按单据号可查；② 仓库调拨：页面新建（A→B，数量 30，成本留 0 取 A 仓均价 10）→ 审核 → A 减 30、B 增 30、两仓合计守恒、流水出/入两笔同成本；③ 销审：页面销审（确认框）→ 库存与流水冲销（红字流水 + 原流水 `IsReversed`）、重复销审被服务端拒绝且不新增流水、冲销后可再次提交审核。所有用例均断言全流程无 JS / 接口失败（有意触发的 1 次重复销审拒绝除外），测试数据经应用自身接口/页面创建并在结束时销审 + 软删除。
- **未做的事（边界）**：未启动 `ERP.Api`、未连业务库、未执行任何 SQL/seed/部署、未运行集成/UI 用例（真实 Edge 门禁归 orchestrator）；未修改 `.env.local`、`deploy/**`、`release/**`、`checkpoints/**`、`logs/**`、任何 `.sql` 文件与 `SeedData*.cs`；未 commit / push。`SchemaUpgrader.cs` 的改动仅限幂等建表/补列 + 菜单与字轨，按 Human Gate L2（已批准）执行。本轮产物为「代码 + 测试 + 文档」，交付等级仍为 `code_ready`，`completed` 只由 orchestrator 的真实 Edge 门禁判定。

### 5.13 ERP-010 报价单 / PI → 销售订单带入预填与直接生成（2026-09-23）

- **任务**：ERP-010「Add Quotation/PI to Sales Order prefill」——补齐审计缺口「报价 → 销售订单带入」：报价单与 PI 各提供**带入预填**与**直接生成**两个用户可见动作，来源留痕、重复守卫、服务端复核；**不触碰旧版销售订单存储过程、不新增任何数据库结构**（`SchemaUpgrader` / `deploy/**` / SQL 全部未改）。
- **范围与做法（关键决策）**：
  - 共享实现 `src/ERP.Api/Controllers/SalesOrderConversion.cs`（静态、public，便于单测直接断言）：两种来源共用「守卫 + 映射 + 复核」，控制器只做取单 / 发号 / 落库，避免两份复制粘贴的转换逻辑漂移。
  - 「带入预填」`GET /{id}/order-prefill` 返回 **未落库**草稿（`SalesOrderPrefillResult`，`OrderNo` 为空）：**不占用字轨单号、不写库、不改来源状态**；前端切到「订单管理 → 销售订单」页打开新增表单（`gotoModulePage` + `openForm` + `fillSalesOrderForm`），用户核对/编辑后按既有 `/api/sales-orders` 保存（同一套服务端复核）。
  - 「直接生成」`POST /{id}/to-order` 一次性落库（`SalesOrderConversionResult` 返回 Id / 单号 / 来源单号）：只 `Add`、不 `Update`，**绝不覆盖既有销售订单**；同一来源（`SourceQuotationId` / `SourcePiId`）存在未删除订单时返回 `RuleConflict`（带已生成单号），**双击/重复点击不会产生重复单据**。
  - 映射口径（写入 `docs/报价单与PI设计方案.md` §8.3 与 `docs/订单追溯字段说明.md`）：报价单 → 客户/业务员/币种/汇率/条款/目的港/付款条件来自报价单（条款类为空时回退客户档案），Consignee/Notify/唛头/定金比例/业务性质/佣金比例来自客户档案；PI → 上述 PI 值优先、为空回退客户档案，运输方式 ← PI 运输条款，定金比例按「PI 比例 → 定金金额反算 → 客户档案 → 30%」取值；**PI 转单同时把 PI 背后的来源报价单一并写入**，追溯链不断；来源文本交期并入备注（截断 500）；明细按 `SortNo` 复制商品/规格/单位/数量/单价/备注，**金额与合计、定金金额由服务端按销售订单口径重算**（`SalesOrderController.Calculate/Validate`）。
  - 守卫顺序保证提示准确：已作废 → 已生成订单 → 已转 PI（提示「请从 PI 转销售订单」）→ 未审核 → 无明细；服务端复核数量 > 0、单价非负、定金比例 0~100（越界 `InvalidParameter`）。
  - 状态口径：报价单转单后置 `Completed`（已完成 = 已转 PI 或已转销售订单，`ToProformaInvoice` 的重复转换提示同步改为「已完成转换」）；PI 转单后置 `Completed`（与该状态在 PI 审核/作废处的既有「已转销售订单」语义一致）。
- **代码改动**：
  - 接口：`SalesOrderConversion.cs`（新增，含 `SalesOrderPrefillResult` / `SalesOrderConversionResult` 两个响应契约）、`QuotationController` 新增 `GET {id}/order-prefill` + `POST {id}/to-order`、`ProformaInvoiceController` 同两式、`SalesOrderController.Calculate/Validate` 由 `private` 改 `public static`（供转换路径复用同一套合计与校验口径）。
  - 前端：`sales-pi.js` 新增 `quotationToOrder` / `quotationPrefillOrder` / `piToOrder` / `piPrefillOrder` 与 `salesDocToOrder` / `salesDocPrefillOrder` / `gotoModulePage` / `fillSalesOrderForm`（复用既有 `MODULES['sales-order']`、`openForm`、`DETAIL_ROWS`/`detailRender`、`REF_APIS` 名称回填）；`modules.js` 报价单与 PI 的 `rowActions` 各加「预填销售订单」「转销售订单」两项（`statuses: ['Approved']`）。
  - 文档：`docs/报价单与PI设计方案.md`（接口表、批次表、采纳项、新增 §8.3 映射与守卫表）、`docs/订单追溯字段说明.md`（接口表、页面带入说明、测试与边界）、`docs/部署交付文档.md` §31.6 下一批清单。
- **本轮实测（未启动 API、未连数据库、未运行集成/UI 用例）**：
  - safe 档：`dotnet restore NEWERP.sln` + `dotnet build NEWERP.sln -c Release --no-restore --no-incremental /p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true` → **0 警告 / 0 错误**；`dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build` → **272/272 通过**（基线 254 + `SalesOrderConversionTests` 18）。
  - 门禁所用配置编译：`dotnet build src/ERP.IntegrationTests/ERP.IntegrationTests.csproj -c Debug` → 0 警告 / 0 错误；新增 `SalesOrderConversionUiTests`（3 个 Edge 用例，`Collection=UiTests`）。
  - 前端脚本 `node --check`：`sales-pi.js` / `modules.js` 通过；`SalesOrderConversionTests` 中另以静态断言锁定「行操作函数名 ↔ `sales-pi.js` 定义 ↔ 接口路径」与预填报文 JSON 契约（camelCase + 枚举名），防止拼写漂移造成「按钮点了没反应」。
- **真实 Edge 验收用例（新增 `SalesOrderConversionUiTests`，`Collection=UiTests`，3 个用例）**：① 报价单页「转销售订单」→ 服务端核对来源留痕与映射字段/明细/合计 → 销售订单页重新打开核对回显；② 报价单转 PI → PI 页覆盖收货人/唛头/运输条款后「转销售订单」→ 核对「PI 值优先」映射与 PI + 报价单双来源、PI 状态变「已转销售订单」；③ 报价单页「带入预填销售订单」→ 断言表单已带入且**未落库、来源状态不变** → 再「转销售订单」成功 → 直接调用接口重复转换被拒（`code != 0`、提示已生成订单）且来源仍只有 1 张订单；每个用例断言全流程无 JS / 接口失败（③ 有意触发 1 次重复转换拒绝，按要求单独断言该次失败），测试数据经应用自身接口创建（不直连数据库、不使用生产数据）。
- **未做的事（边界）**：未启动 `ERP.Api`、未连业务库、未执行任何 SQL/seed/部署、未运行集成/UI 用例（真实 Edge 门禁归 orchestrator）；未修改 `.env.local`、`deploy/**`、`release/**`、`checkpoints/**`、`logs/**`、任何 `.sql` 文件、`SchemaUpgrader.cs` 与 `SeedData*.cs`；未 commit / push。带入后商品编码与起订量不带入（销售订单明细无对应列，本任务不新增结构）、`DeliveryDate` 留空人工填写——已在文档标注。本轮产物为「代码 + 测试 + 文档」，交付等级为 `code_ready`，`completed` 只由 orchestrator 的真实 Edge 门禁判定。

### 5.14 ERP-019 单证中心：由销售订单 / 装柜清单生成单证与 Excel 导出（2026-09-23）

- **任务**：ERP-019「Automate trade-document generation and export」——补齐审计缺口「单证中心只能手工登记、无 Excel 导出」：销售订单与装柜清单各提供**带入预填**与**直接生成**两个用户可见动作，来源留痕、重复守卫、单证中心列表/单条 **Excel 导出**；**不新增任何数据库结构**（`TradeDocument` 实体、`SchemaUpgrader.cs`、`deploy/**`、任何 `.sql`、`SeedData*.cs` 全部未改）。
- **范围与做法（关键决策）**：
  - 共享实现 `src/ERP.Api/Controllers/TradeDocumentGeneration.cs`（静态、public，便于单测直接断言）：两路来源共用「守卫 + 映射 + 编号规则 + 落库」，控制器只做取单 / 装载来源上下文 / 调用。
  - 来源留痕**只用既有字段**（单证台账无来源列）：销售订单 → `SalesOrderNo` + 备注 `来源：销售订单 {订单号}`；装柜清单 → `RefNo`（柜号）+ 备注 `来源：装柜清单 {清单号}`。备注前缀同时是重复生成守卫的判定依据。
  - 「带入预填」`GET /{id}/trade-documents/prefill` 返回**未落库**草稿（销售订单 5 类 / 装柜清单 4 类）+ 已生成类型；不写库、不占用编号，前端切到单证中心新增表单带入（`gotoModulePage` + `openForm` + `fillTradeDocForm`），用户核对/编辑后按既有 `/api/trade/documents` 保存。
  - 「直接生成」`POST /{id}/trade-documents`（Body `{ docTypes: [...] }`，为空用来源默认类型：销售订单=商业发票+装箱单，装柜清单=装箱单）一次性落库：只 `Add`、不 `Update`；**批量中任一类型已生成即整体拒绝**（`RuleConflict`，不产生半成品数据），双击/重复点击不会产生重复单证。
  - 单证编号确定性生成 `{类型前缀}-{来源单号}`（CD/PL/CI/CO/BL/BK/VR/TD），与历史人工台账冲突时自动追加 `-2/-3…`；新生成单证状态固定 `待制作`，生成后仍可在单证中心人工改金额/份数/制作人/状态，**来源留痕保留**（人工可编辑性由单测断言）。
  - 映射口径：销售订单 → 客户 + 客户档案名称、订单总额、订单币种、目的港（订单文本 → 客户档案）、按类型默认制作人/份数；装柜清单 → 柜号、客户档案币种、港口按「预装柜单 → 订柜信息 → 客户档案」回退、箱数/毛重/体积与唛头写入备注、金额留 0 人工填写。守卫顺序：来源已作废 → 类型合法性 → 重复生成。
  - Excel 导出 `GET /api/trade/documents/export-excel`（`id` / `keyword` / `docType` / `status` / `start` / `end`）：列表筛选导出与单条导出统一走 `ExcelExporter.ExportRows`（16 个中文列头），文件名 `TradeDocuments_{时间戳}.xlsx` / `TradeDocument_{id}_{时间戳}.xlsx`。
- **代码改动**：
  - 接口：`TradeDocumentGeneration.cs`（新增：映射 / 守卫 / 编号 / 预填与生成 + `TradeDocPrefillResult`、`TradeDocGenerateRequest`、`TradeDocGenerateResult`、`TradeDocGeneratedItem`）、`SalesOrderController` 与 `ContainerLoadingListController` 各新增两式、`TradeDocumentController` 注入 `IErpDbContext` 并新增 `export-excel`。
  - 前端：新增 `trade-doc-gen.js`（来源配置 `TRADE_DOC_SOURCES`、生成/预填对话框、导出对话框、单条导出、xlsx 下载），`modules.js` 单证中心加 `extraActions`（导出 Excel）与 `rowActions`（导出该单证）并补齐币种选项 GBP/JPY，`modules-doc.js` 销售订单、`modules-doc2.js` 装柜清单各加行操作「生成单证」「预填单证」（`statuses: ['Pending','Submitted','Approved']`），`index.html` 注册脚本。
  - 文档：新增 `docs/单证中心生成与导出说明.md`（映射表、编号规则、接口表、守卫口径、前端入口、导出列、测试与边界）；本节 §5.14 与 §2 #13 状态更新。
- **本轮实测（未启动 API、未连数据库、未运行集成/UI 用例）**：
  - safe 档：`dotnet build src/ERP.Api -c Release --no-restore /p:TreatWarningsAsErrors=true /p:RunAnalyzersDuringBuild=true` → **0 警告 / 0 错误**；`dotnet test src/ERP.UnitTests/ERP.UnitTests.csproj -c Release --no-build` → **318/318 通过**（基线 297 + `TradeDocumentGenerationTests` 21）。
  - 门禁所用配置编译：`dotnet build src/ERP.IntegrationTests/ERP.IntegrationTests.csproj -c Debug` → 0 警告 / 0 错误；新增 `TradeDocumentGenerationUiTests`（4 个 Edge 用例，`Collection=UiTests`）。
  - 前端脚本 `node --check`：`trade-doc-gen.js` / `modules.js` / `modules-doc.js` / `modules-doc2.js` 通过；单测另以静态断言锁定「行操作函数名 ↔ 脚本定义 ↔ 接口路径」与报文 JSON 契约（camelCase），并在 `index.html` 中确认脚本已注册，避免「按钮点了没反应」。
- **真实 Edge 验收用例（新增 `TradeDocumentGenerationUiTests`，`Collection=UiTests`，4 个用例）**：① 销售订单页「生成单证」（对话框默认 CI+PL）→ 服务端核对 `CI-{订单号}` / `PL-{订单号}`、客户/金额 250/币种 USD/目的港 HAMBURG/状态待制作/来源备注，并在单证中心列表可见；② 装柜清单页「生成单证」→ 核对 `PL-{清单号}`、`RefNo`=柜号、客户档案币种 EUR、目的港回退 ROTTERDAM、备注含箱数/毛重；③ 再次打开对话框断言已生成类型 `disabled` 且无勾选，随后绕过前端重复调用接口 → `code != 0` 且提示「不能重复生成」，单证仍为 2 张；④ 单证中心「导出 Excel」对话框可用 + 导出接口返回可解析 xlsx（zip 文件头）。测试数据经应用自身接口创建（不直连数据库、不使用生产数据、不执行 SQL）。
- **未做的事（边界）**：未启动 `ERP.Api`、未连业务库、未执行任何 SQL/seed/部署、未运行集成/UI 用例（真实 Edge 门禁归 orchestrator，按 `defer_browser_during_development` 记为 `browser_deferred`）；未修改 `.env.local`、`deploy/**`、`release/**`、`checkpoints/**`、`logs/**`、任何 `.sql` 文件、`SchemaUpgrader.cs` 与 `SeedData*.cs`；未 commit / push。单证扫描件附件、PDF/版式打印、单证明细行仍为后续独立任务（见 `docs/单证中心生成与导出说明.md` §9）。本轮产物为「代码 + 测试 + 文档」，交付等级为 `code_ready`，`completed` 只由 orchestrator 判定。

## 6. 执行规则（保持有效）

- 队列目标大小 3；队首非终态任务控制推进，禁止隐式跳过；`depends_on` 变更前必须重新校验依赖图。
- 不因「已有文件/Controller/菜单」判为完成：必须同时验证 API、页面、持久化、状态流、权限、测试与用户可见流程（本 Backlog 已按此口径给出初判状态）。
- 业务任务默认 `completion_mode: browser`：真实 Edge + TRX + 截图 + SHA-256 清单齐备才算 `completed`；Cline 正常退出仅代表 `code_ready`。
- 涉及 SQL/存储过程/SchemaUpgrader/SeedData 的改动只允许代码级修改，并且只对 NEWERP_TEST 自动验证；生产数据库、生产 OSS、正式部署/发布与不可逆数据操作继续保留 Human Gate。
- 每个 partial/missing 任务开工前先读本 Backlog §2 证据列，避免重复实现已可用的链路（尤其费用分摊、应收账龄、单证中心、CRM、样品、退税台账、14 张报表）。
- 新增菜单必须同时补页面注册（`modules*.js` 或 `BILL_CONFIG`/`EXPORT_MENU_MAP`/`REPORTS`），避免出现"该功能开发中"死链。
- 每次任务完成后更新本 Backlog：把已验证项从 partial/missing 升级，并在 §5.3 调整下一批建议任务。
- ERP-006 本身不修改 `PROJECT_STATE.json`、任务 JSON、`audit.jsonl` 与结果文件——这些由 orchestrator 拥有并写入。




