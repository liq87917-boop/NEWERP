using ERP.Application.Common;
using ERP.Application.DTOs;

namespace ERP.Application.Services;

/// <summary>
/// 基础资料打印族的受控目录（ERP-317 Stage 2）：7 个既有基础资料打印族（客户 / 供应商 / 员工 / 费用科目 /
/// 仓库 / 商品 / 其他资料）的服务端唯一受控映射。每个族一一对应一个精确受控数据集键（<c>master:{familyKey}</c>）
/// 与既有基础资料菜单授权，并携带有序的有限字段白名单（与 <c>js/modules.js</c> 的基础资料打印列、Base* 实体属性
/// 完全同名 / 同序 / 同标签）。全部为服务端编译期常量，绝不来自客户端、绝不落到数据库全量元数据发现。
/// </summary>
public sealed record ReportMasterDataColumn(string Key, string Title, string Type, string Property);

/// <summary>
/// 单个基础资料打印族的受控数据集稳定元数据（族键 / 数据集键 / 菜单授权 / 有序列白名单）。
/// </summary>
public sealed record ReportMasterDataFamilyDefinition(
    string FamilyKey,
    string DatasetKey,
    string Title,
    IReadOnlyList<string> RequiredMenuCodes,
    string RequiredMenuText,
    IReadOnlyList<ReportMasterDataColumn> Columns);

public static class ReportConfigurationMasterDataCatalog
{
    /// <summary>基础资料打印族受控数据集键前缀。</summary>
    public const string DatasetKeyPrefix = "master:";

    /// <summary>
    /// 基础资料纯字段口径：无金额 / 币种 / 单位换算，数值与 null 原样保留。
    /// <para>迁移登记册条目与受控数据集适配器必须使用同一常量，parity 的币种单位语义校验按此精确匹配。</para>
    /// </summary>
    public const string CurrencyUnitSemantics = "基础资料纯字段：无金额/币种/单位换算；数值与 null 原样保留";

    /// <summary>7 个基础资料打印族（顺序稳定：客户 → 供应商 → 员工 → 费用科目 → 仓库 → 商品 → 其他资料）。</summary>
    public static readonly IReadOnlyList<ReportMasterDataFamilyDefinition> Families = BuildFamilies();

    private static readonly Dictionary<string, ReportMasterDataFamilyDefinition> ByFamilyKey =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, ReportMasterDataFamilyDefinition> ByDatasetKey =
        new(StringComparer.OrdinalIgnoreCase);

    static ReportConfigurationMasterDataCatalog()
    {
        foreach (var family in Families)
        {
            ByFamilyKey[family.FamilyKey] = family;
            ByDatasetKey[family.DatasetKey] = family;
        }
    }

    /// <summary>按族键解析（未知 / 空白 / 畸形一律拒绝；在打开任何查询之前调用）。</summary>
    public static ReportMasterDataFamilyDefinition Resolve(string? familyKey)
    {
        if (string.IsNullOrWhiteSpace(familyKey))
            throw BusinessException.InvalidParameter("基础资料打印族标识不能为空");

        var key = familyKey.Trim();
        if (key.Length > 64 || ContainsUnsafeIdentifier(key))
            throw BusinessException.InvalidParameter($"基础资料打印族标识非法：{key}");

        if (!ByFamilyKey.TryGetValue(key, out var family))
            throw BusinessException.InvalidParameter($"未知的基础资料打印族标识：{key}");

        return family;
    }

    /// <summary>按族键解析（失败返回 false，不抛异常）。</summary>
    public static bool TryResolve(string? familyKey, out ReportMasterDataFamilyDefinition family)
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
    public static ReportMasterDataFamilyDefinition ResolveByDatasetKey(string? datasetKey)
    {
        if (string.IsNullOrWhiteSpace(datasetKey))
            throw BusinessException.InvalidParameter("基础资料打印数据集键不能为空");

        var key = datasetKey.Trim();
        if (key.Length > 64 || ContainsUnsafeIdentifier(key))
            throw BusinessException.InvalidParameter($"基础资料打印数据集键非法：{key}");

        if (!ByDatasetKey.TryGetValue(key, out var family))
            throw BusinessException.InvalidParameter($"未知的基础资料打印数据集键：{key}");

        return family;
    }

    /// <summary>是否为受控基础资料打印族（用于打印模板族目录 / 迁移登记册语义派生）。</summary>
    public static bool IsMasterFamily(string? familyKey)
        => !string.IsNullOrWhiteSpace(familyKey) && ByFamilyKey.ContainsKey(familyKey.Trim());

    internal static bool ContainsUnsafeIdentifier(string value)
        => value.Any(ch => !(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == ':'));

    private static List<ReportMasterDataFamilyDefinition> BuildFamilies()
    {
        const string text = ReportConfigurationConstants.TypeText;
        const string number = ReportConfigurationConstants.TypeNumber;

        return new List<ReportMasterDataFamilyDefinition>
        {
            Family("customer", "客户资料", new[]
            {
                C("customerCode", "客户编码", text, "CustomerCode"),
                C("customerName", "客户名称", text, "CustomerName"),
                C("contactPerson", "联系人", text, "ContactPerson"),
                C("phone", "电话", text, "Phone"),
                C("country", "国家", text, "Country"),
                C("currency", "币种", text, "Currency"),
                C("tradeTerms", "贸易条款", text, "TradeTerms"),
                C("forwarderName", "指定货代", text, "ForwarderName"),
                C("creditLimit", "信用额度", number, "CreditLimit"),
                C("creditDays", "账期(天)", number, "CreditDays"),
                C("depositRatio", "定金比例%", number, "DepositRatio"),
            }),
            Family("supplier", "供应商资料", new[]
            {
                C("supplierCode", "供应商编码", text, "SupplierCode"),
                C("supplierName", "供应商名称", text, "SupplierName"),
                C("supplierType", "类型", text, "SupplierType"),
                C("boothLocation", "档口位置", text, "BoothLocation"),
                C("contactPerson", "联系人", text, "ContactPerson"),
                C("phone", "电话", text, "Phone"),
                C("settlementMethod", "结算方式", text, "SettlementMethod"),
            }),
            Family("employee", "员工资料", new[]
            {
                C("employeeCode", "员工编码", text, "EmployeeCode"),
                C("employeeName", "姓名", text, "EmployeeName"),
                C("department", "部门", text, "Department"),
                C("position", "职位", text, "Position"),
                C("phone", "电话", text, "Phone"),
            }),
            Family("expense-account", "费用科目", new[]
            {
                C("accountCode", "科目编码", text, "AccountCode"),
                C("accountName", "科目名称", text, "AccountName"),
                C("accountType", "类型", number, "AccountType"),
            }),
            Family("warehouse", "仓库资料", new[]
            {
                C("warehouseCode", "仓库编码", text, "WarehouseCode"),
                C("warehouseName", "仓库名称", text, "WarehouseName"),
                C("manager", "负责人", text, "Manager"),
                C("phone", "电话", text, "Phone"),
            }),
            Family("product", "商品资料", new[]
            {
                C("productCode", "商品编码", text, "ProductCode"),
                C("productName", "商品名称", text, "ProductName"),
                C("spec", "规格", text, "Spec"),
                C("unit", "单位", text, "Unit"),
                C("unitsPerPackage", "装箱数", number, "UnitsPerPackage"),
                C("salePrice", "销售价", number, "SalePrice"),
                C("costPrice", "成本价", number, "CostPrice"),
                C("refundRate", "退税率%", number, "RefundRate"),
                C("minStock", "安全库存", number, "MinStock"),
            }),
            Family("other-info", "其他资料", new[]
            {
                C("infoType", "资料类型", text, "InfoType"),
                C("infoCode", "编码", text, "InfoCode"),
                C("infoName", "名称", text, "InfoName"),
                C("englishName", "英文名称", text, "EnglishName"),
            }),
        };
    }

    private static ReportMasterDataFamilyDefinition Family(
        string familyKey, string title, IReadOnlyList<ReportMasterDataColumn> columns)
        => new(
            familyKey,
            DatasetKeyPrefix + familyKey,
            title,
            new[] { familyKey },
            title,
            columns);

    private static ReportMasterDataColumn C(string key, string title, string type, string property)
        => new(key, title, type, property);
}
