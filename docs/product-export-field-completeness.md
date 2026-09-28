# 只读出口字段完整度工作台（ERP-107）

## 1. 定位

只读出口字段完整度工作台把**启用商品资料**里与导出文档直接相关的既有字段按商品逐行只读呈现，
只报告这些字段「已填写 / 空白 / 为 0 / 无效值」与缺口情况。它是**开发 / 运营只读视图**，
不做任何写操作，也不做报关合规、退税资格或税率结论。

覆盖的字段（均为 `BaseProduct` 上已存在、且被导出文档使用的字段）：

| 字段分组 | 字段 | 说明 |
| --- | --- | --- |
| 英文报关品名 | `EnglishDeclareName` | 英文申报名称 |
| 装箱信息 | `PackageUnit` / `UnitsPerPackage` | 装箱单位 / 每箱数量 |
| 外箱尺寸 / 重量 | `OuterLength` / `OuterWidth` / `OuterHeight` / `OuterWeight` | 外箱长宽高 / 毛重 |
| 退税率 | `RefundRate` | 出口退税率（%） |

## 2. 边界（重要）

- **只读**：接口与页面都不写任何表 —— 无 `Add / Update / Remove / SaveChanges`，不新增表 / 列 / 迁移。
- **不读取图片**：只读取上述文本 / 数值字段，不读取 `Image1` / `Image2` / `Image3`，不触达 OSS。
- **不改写商品 / 单证 / 报关单**：不修改商品、单证、报关单或出口退税台账。
- **不做合规 / 税务结论**：字段齐全不等于可以报关或退税，字段缺失也不代表不可报关或退税；
  本工作台只报告字段填写与缺口，请以海关 / 税务官方口径为准。
- **无外部查询**：全部数据来自本地数据库，不调用任何外部服务。

## 3. 字段状态判定

每个字段的 `State` 只取以下四值之一，**不生成通过 / 不通过结论**：

| 状态 | 含义 |
| --- | --- |
| `present` | 已填写（文本非空白；正数值 > 0；退税率 0 < 值 ≤ 100） |
| `blank` | 文本字段空白未填写 |
| `zero` | 数值字段为 0（未填写） |
| `unknown` | 无效值（如负数尺寸 / 数量，负数或超过 100 的退税率） |

- 文本字段（`EnglishDeclareName` / `PackageUnit`）只区分 `present` / `blank`。
- 数值字段区分 `present` / `zero` / `unknown`，其中「为 0」与「无效（负数等）」显式分开，绝不混淆。
- 商品的整体完整度：所有字段均为 `present` 时为 `complete`，否则为 `incomplete`（仅报告缺口数量）。

## 4. 完整度分组

`group` 参数用于筛选商品：

| 值 | 含义 |
| --- | --- |
| `all`（默认） | 全部启用商品 |
| `complete` | 所有字段均已填写 |
| `incomplete` | 至少一个字段未填写 / 为 0 / 无效 |
| `declaration` | 英文报关品名缺失 |
| `packing` | 装箱单位或每箱数量缺失 |
| `dimensions` | 外箱尺寸 / 重量缺失 |
| `refund-rate` | 退税率缺失 |

未知取值一律拒绝（`BusinessException.InvalidParameter`）。

## 5. 接口

- `GET /api/base/products/export-field-completeness`（`[Authorize]`）
  - 查询参数：`keyword`（商品编码 / 名称模糊匹配）、`group`、`page`、`pageSize`（默认 20，上限 200）。
  - 只返回未删除、启用（`Status = 1`）的商品；按稳定商品 `Id` 升序分页。
  - 响应：`{ items, total, page, pageSize, readOnlyText, boundaryText, disclaimerText }`。
  - 每行字段：`productId` / `productCode` / `productName` / `spec` / `unit` /
    `completeness` / `gapCount` / `fieldCount` / `fields`（每字段 `key` / `label` / `state` / `text` / `present`）。

## 6. 前端入口

- 商品资料页（`modules.js` 的 `product` 模块）工具栏新增「🧾 出口字段完整度」按钮，
  调用 `openProductExportFieldCompleteness()` 打开独立只读工作台页。
- `index.html` 注册 `/js/product-export-field-completeness.js`。
- 工作台行内提供「编辑」按钮（`openProductEditor(productId)`），跳转到既有商品编辑流程补齐字段。
- 页面只提供「查询 / 编辑」操作，**不提供**任何写库、合规 / 退税结论或删除操作。

## 7. 测试与验收

- 单元测试见 `src/ERP.UnitTests/ProductExportFieldCompletenessTests.cs`，覆盖：
  稀疏商品与完整商品、无效尺寸、退税率边界、软删除 / 停用排除、关键字与完整度分组筛选、
  稳定分页、只读不写库、未知分组拒绝，以及接口与前端接线契约。
- 全部使用内存库（`TestDbFactory`），不连接 SQL Server、不执行任何 SQL / 迁移 / seed。

## 8. 关键文件

| 职责 | 文件 |
| --- | --- |
| 控制器 | `src/ERP.Api/Controllers/ProductExportFieldCompleteness.cs` |
| DTO | `src/ERP.Application/DTOs/ProductExportFieldCompletenessDtos.cs` |
| 纯规则 | `src/ERP.Application/Services/ProductExportFieldCompletenessRules.cs` |
| 前端 | `src/ERP.Api/wwwroot/js/product-export-field-completeness.js`、`modules.js`、`index.html` |
| 测试 | `src/ERP.UnitTests/ProductExportFieldCompletenessTests.cs` |
