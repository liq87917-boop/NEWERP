using ERP.Application.Common;
using ERP.Application.DTOs;

namespace ERP.Application.Services;

/// <summary>
/// 销售单据打印快照列（有限、只读）：列键 / 中文标签 / 字段类型 / 行粒度 / 源实体属性名。
/// <para><see cref="Grain"/> 仅取 <see cref="ReportSalesDocumentGrain"/> 的有限取值：identity（表头与明细共有身份键）/
/// header（仅表头行）/ detail（仅明细行）。<see cref="Property"/> 为读取该列所用的权威持久化实体属性名
/// （表头列取报价单 / PI 主表属性；明细列取明细行属性）。</para>
/// </summary>
public sealed record ReportSalesDocumentColumn(string Key, string Title, string Type, string Grain, string Property);

/// <summary>销售单据打印快照列的行粒度（有限、与通用组合口径对齐）。</summary>
public static class ReportSalesDocumentGrain
{
    public const string Identity = "identity";
    public const string Header = "header";
    public const string Detail = "detail";
}

/// <summary>
/// 单个销售单据打印族的受控数据集稳定元数据（族键 / 数据集键 / 标题 / 菜单授权 / 有序列白名单）。
/// </summary>
public sealed record ReportSalesDocumentFamilyDefinition(
    string FamilyKey,
    string DatasetKey,
    string Title,
    IReadOnlyList<string> RequiredMenuCodes,
    string RequiredMenuText,
    IReadOnlyList<ReportSalesDocumentColumn> Columns);

/// <summary>
/// 销售单据打印族的受控目录（ERP-318 Stage 2）：报价单 / 形式发票 PI 两个既有 EF 主子表单据打印族的
/// 服务端唯一受控映射。每个族一一对应一个精确受控数据集键（<c>sales-document:{familyKey}</c>）与既有
/// 销售单据菜单授权，并携带有序的有限字段白名单（与 <see cref="ERP.Domain.Entities.Quotation"/> /
/// <see cref="ERP.Domain.Entities.ProformaInvoice"/> 及其明细行的持久打印快照完全同名 / 同序）。
/// 全部为服务端编译期常量，绝不来自客户端、绝不落到数据库全量元数据发现。
/// </summary>
public static class ReportConfigurationSalesDocumentCatalog
{
    /// <summary>销售单据打印族受控数据集键前缀。</summary>
    public const string DatasetKeyPrefix = "sales-document:";

    /// <summary>
    /// 销售单据打印快照金额 / 单位口径：金额按原币、数量按基础单位，绝不跨币种换算或合并。
    /// <para>迁移登记册 / 受控数据集适配器 / 预设共用同一常量，parity 的币种单位语义校验按此精确匹配。</para>
    /// </summary>
    public const string CurrencyUnitSemantics = "金额按原币呈现；数量按基础单位；不跨币种换算或合并";

    /// <summary>2 个销售单据打印族（顺序稳定：报价单 → 形式发票 PI）。</summary>
    public static readonly IReadOnlyList<ReportSalesDocumentFamilyDefinition> Families = BuildFamilies();

    private static readonly Dictionary<string, ReportSalesDocumentFamilyDefinition> ByFamilyKey =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, ReportSalesDocumentFamilyDefinition> ByDatasetKey =
        new(StringComparer.OrdinalIgnoreCase);

    static ReportConfigurationSalesDocumentCatalog()
    {
        foreach (var family in Families)
        {
            ByFamilyKey[family.FamilyKey] = family;
            ByDatasetKey[family.DatasetKey] = family;
        }
    }

    /// <summary>按族键解析（未知 / 空白 / 畸形一律拒绝；在打开任何查询之前调用）。</summary>
    public static ReportSalesDocumentFamilyDefinition Resolve(string? familyKey)
    {
        if (string.IsNullOrWhiteSpace(familyKey))
            throw BusinessException.InvalidParameter("销售单据打印族标识不能为空");

        var key = familyKey.Trim();
        if (key.Length > 64 || ContainsUnsafeIdentifier(key))
            throw BusinessException.InvalidParameter($"销售单据打印族标识非法：{key}");

        if (!ByFamilyKey.TryGetValue(key, out var family))
            throw BusinessException.InvalidParameter($"未知的销售单据打印族标识：{key}");

        return family;
    }

    /// <summary>按族键解析（失败返回 false，不抛异常）。</summary>
    public static bool TryResolve(string? familyKey, out ReportSalesDocumentFamilyDefinition family)
    {
        family = null!;
        if (string.IsNullOrWhiteSpace(familyKey))
            return false;

        var key = familyKey.Trim();
        if (key.Length > 64 || ContainsUnsafeIdentifier(key))
            return false;

        return ByFamilyKey.TryGetValue(key, out family!);
    }

    /// <summary>按数据集键解析（未知 / 空白 / 畸形一律拒绝）。</summary>
    public static ReportSalesDocumentFamilyDefinition ResolveByDatasetKey(string? datasetKey)
    {
        if (string.IsNullOrWhiteSpace(datasetKey))
            throw BusinessException.InvalidParameter("销售单据打印数据集键不能为空");

        var key = datasetKey.Trim();
        if (key.Length > 64 || ContainsUnsafeIdentifier(key))
            throw BusinessException.InvalidParameter($"销售单据打印数据集键非法：{key}");

        if (!ByDatasetKey.TryGetValue(key, out var family))
            throw BusinessException.InvalidParameter($"未知的销售单据打印数据集键：{key}");

        return family;
    }

    internal static bool ContainsUnsafeIdentifier(string value)
        => value.Any(ch => !(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == ':'));

    private static IReadOnlyList<ReportSalesDocumentFamilyDefinition> BuildFamilies()
        => new ReportSalesDocumentFamilyDefinition[]
        {
            Family("quotation", "报价单", QuotationColumns()),
            Family("proforma-invoice", "形式发票 PI", ProformaInvoiceColumns()),
        };

    private static ReportSalesDocumentFamilyDefinition Family(
        string familyKey, string title, IReadOnlyList<ReportSalesDocumentColumn> columns)
        => new(
            familyKey,
            DatasetKeyPrefix + familyKey,
            title,
            new[] { familyKey },
            title,
            columns);

    private static ReportSalesDocumentColumn C(string key, string title, string type, string grain, string property)
        => new(key, title, type, grain, property);

    private const string Text = ReportConfigurationConstants.TypeText;
    private const string Number = ReportConfigurationConstants.TypeNumber;
    private const string Date = ReportConfigurationConstants.TypeDate;

    private static IReadOnlyList<ReportSalesDocumentColumn> IdentityColumns(
        string docNoProperty, string docDateProperty)
        => new ReportSalesDocumentColumn[]
        {
            C("id", "单据Id", Number, ReportSalesDocumentGrain.Identity, "Id"),
            C("docNo", "单据号", Text, ReportSalesDocumentGrain.Identity, docNoProperty),
            C("docDate", "单据日期", Date, ReportSalesDocumentGrain.Identity, docDateProperty),
            C("customerId", "客户Id", Number, ReportSalesDocumentGrain.Identity, "CustomerId"),
            C("customerName", "客户名称", Text, ReportSalesDocumentGrain.Identity, "CustomerName"),
            C("status", "状态", Text, ReportSalesDocumentGrain.Identity, "Status"),
            C("currency", "币种", Text, ReportSalesDocumentGrain.Identity, "Currency"),
        };

    private static IReadOnlyList<ReportSalesDocumentColumn> SharedDetailColumns()
        => new ReportSalesDocumentColumn[]
        {
            C("sortNo", "行序", Number, ReportSalesDocumentGrain.Detail, "SortNo"),
            C("lineId", "明细Id", Number, ReportSalesDocumentGrain.Detail, "Id"),
            C("productId", "商品Id", Number, ReportSalesDocumentGrain.Detail, "ProductId"),
            C("productCode", "商品编码", Text, ReportSalesDocumentGrain.Detail, "ProductCode"),
            C("productName", "商品名称", Text, ReportSalesDocumentGrain.Detail, "ProductName"),
            C("spec", "规格", Text, ReportSalesDocumentGrain.Detail, "Spec"),
            C("unit", "单位", Text, ReportSalesDocumentGrain.Detail, "Unit"),
            C("quantity", "数量", Number, ReportSalesDocumentGrain.Detail, "Quantity"),
            C("unitPrice", "单价", Number, ReportSalesDocumentGrain.Detail, "UnitPrice"),
            C("amount", "金额", Number, ReportSalesDocumentGrain.Detail, "Amount"),
            C("moq", "起订量", Text, ReportSalesDocumentGrain.Detail, "Moq"),
            C("lineRemark", "行备注", Text, ReportSalesDocumentGrain.Detail, "Remark"),
        };

    private static IReadOnlyList<ReportSalesDocumentColumn> QuotationColumns()
    {
        var columns = new List<ReportSalesDocumentColumn>();
        columns.AddRange(IdentityColumns("QuotationNo", "QuotationDate"));
        columns.AddRange(new[]
        {
            C("validUntil", "有效期至", Date, ReportSalesDocumentGrain.Header, "ValidUntil"),
            C("inquiryNo", "来源询价单号", Text, ReportSalesDocumentGrain.Header, "InquiryNo"),
            C("contactPerson", "联系人", Text, ReportSalesDocumentGrain.Header, "ContactPerson"),
            C("contactPhone", "联系电话", Text, ReportSalesDocumentGrain.Header, "ContactPhone"),
            C("contactEmail", "联系邮箱", Text, ReportSalesDocumentGrain.Header, "ContactEmail"),
            C("tradeTerms", "贸易术语", Text, ReportSalesDocumentGrain.Header, "TradeTerms"),
            C("portOfLoading", "起运港", Text, ReportSalesDocumentGrain.Header, "PortOfLoading"),
            C("portOfDestination", "目的港", Text, ReportSalesDocumentGrain.Header, "PortOfDestination"),
            C("paymentTerms", "付款方式", Text, ReportSalesDocumentGrain.Header, "PaymentTerms"),
            C("leadTime", "交货期", Text, ReportSalesDocumentGrain.Header, "LeadTime"),
            C("exchangeRate", "汇率", Number, ReportSalesDocumentGrain.Header, "ExchangeRate"),
            C("totalAmount", "报价总额", Number, ReportSalesDocumentGrain.Header, "TotalAmount"),
            C("totalAmountCny", "报价总额(折人民币)", Number, ReportSalesDocumentGrain.Header, "TotalAmountCny"),
            C("salesmanId", "业务员Id", Number, ReportSalesDocumentGrain.Header, "SalesmanId"),
            C("salesmanName", "业务员姓名", Text, ReportSalesDocumentGrain.Header, "SalesmanName"),
            C("remark", "备注", Text, ReportSalesDocumentGrain.Header, "Remark"),
        });
        columns.AddRange(SharedDetailColumns());
        return columns;
    }

    private static IReadOnlyList<ReportSalesDocumentColumn> ProformaInvoiceColumns()
    {
        var columns = new List<ReportSalesDocumentColumn>();
        columns.AddRange(IdentityColumns("PiNo", "PiDate"));
        columns.AddRange(new[]
        {
            C("quotationNo", "来源报价单号", Text, ReportSalesDocumentGrain.Header, "QuotationNo"),
            C("contactPerson", "联系人", Text, ReportSalesDocumentGrain.Header, "ContactPerson"),
            C("contactPhone", "联系电话", Text, ReportSalesDocumentGrain.Header, "ContactPhone"),
            C("contactEmail", "联系邮箱", Text, ReportSalesDocumentGrain.Header, "ContactEmail"),
            C("consignee", "收货人", Text, ReportSalesDocumentGrain.Header, "Consignee"),
            C("notifyParty", "通知人", Text, ReportSalesDocumentGrain.Header, "NotifyParty"),
            C("shippingMarks", "唛头", Text, ReportSalesDocumentGrain.Header, "ShippingMarks"),
            C("bankInfo", "银行信息", Text, ReportSalesDocumentGrain.Header, "BankInfo"),
            C("tradeTerms", "贸易术语", Text, ReportSalesDocumentGrain.Header, "TradeTerms"),
            C("portOfLoading", "起运港", Text, ReportSalesDocumentGrain.Header, "PortOfLoading"),
            C("portOfDestination", "目的港", Text, ReportSalesDocumentGrain.Header, "PortOfDestination"),
            C("paymentTerms", "付款方式", Text, ReportSalesDocumentGrain.Header, "PaymentTerms"),
            C("shippingTerms", "运输方式与条款", Text, ReportSalesDocumentGrain.Header, "ShippingTerms"),
            C("leadTime", "交货期", Text, ReportSalesDocumentGrain.Header, "LeadTime"),
            C("exchangeRate", "汇率", Number, ReportSalesDocumentGrain.Header, "ExchangeRate"),
            C("totalAmount", "PI总额", Number, ReportSalesDocumentGrain.Header, "TotalAmount"),
            C("totalAmountCny", "PI总额(折人民币)", Number, ReportSalesDocumentGrain.Header, "TotalAmountCny"),
            C("depositRatio", "定金比例%", Number, ReportSalesDocumentGrain.Header, "DepositRatio"),
            C("depositAmount", "定金金额", Number, ReportSalesDocumentGrain.Header, "DepositAmount"),
            C("salesmanId", "业务员Id", Number, ReportSalesDocumentGrain.Header, "SalesmanId"),
            C("salesmanName", "业务员姓名", Text, ReportSalesDocumentGrain.Header, "SalesmanName"),
            C("remark", "备注", Text, ReportSalesDocumentGrain.Header, "Remark"),
        });
        columns.AddRange(SharedDetailColumns());
        return columns;
    }
}


