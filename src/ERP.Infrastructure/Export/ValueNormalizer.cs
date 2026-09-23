namespace ERP.Infrastructure.Export;

/// <summary>
/// Excel 导入值规范化：将用户填写的文本转换为存储过程/实体可用的参数值，
/// 统一处理系统字段跳过、日期、币种、是否、数值（千分位、货币符号）等。
/// </summary>
public static class ValueNormalizer
{
    /// <summary>由系统自动维护、导入时应忽略的字段</summary>
    private static readonly HashSet<string> SystemFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "Oid", "BillNo", "BillID", "Id", "Status", "CreatedAt", "CreatedBy",
        "UpdatedAt", "UpdatedBy", "IsDeleted", "RowVersion"
    };

    /// <summary>命中以下关键字的字段按数值解析</summary>
    private static readonly string[] NumericKeywords =
    {
        "Amount", "Price", "Quantity", "Qty", "Weight", "Volume", "Rate", "Ratio",
        "Total", "Cost", "Cartons", "Days", "Freight", "Limit", "SortOrder", "Length", "Width", "Height"
    };

    /// <summary>币种文本 → 枚举值（与 ERP.Domain.Enums.Currency 一致）</summary>
    private static readonly Dictionary<string, int> CurrencyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CNY"] = 1, ["人民币"] = 1, ["RMB"] = 1, ["￥"] = 1,
        ["USD"] = 2, ["美元"] = 2, ["$"] = 2, ["US$"] = 2,
        ["EUR"] = 3, ["欧元"] = 3, ["€"] = 3,
        ["HKD"] = 4, ["港币"] = 4, ["HK$"] = 4,
        ["GBP"] = 5, ["英镑"] = 5,
        ["JPY"] = 6, ["日元"] = 6
    };

    /// <summary>判断是否为系统字段（导入时忽略）</summary>
    public static bool IsSystemField(string key) => SystemFields.Contains(key);

    /// <summary>
    /// 将单元格文本转换为参数值；返回 null 表示该字段不参与导入（空值或系统字段）
    /// </summary>
    /// <param name="key">字段键</param>
    /// <param name="raw">单元格原始文本</param>
    public static object? ToParameter(string key, string? raw)
    {
        if (IsSystemField(key)) return null;
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var text = raw.Trim();
        if (text.Length == 0) return null;

        // 币种：文本/符号 → 枚举值
        if (key.Equals("Currency", StringComparison.OrdinalIgnoreCase))
        {
            if (CurrencyMap.TryGetValue(text, out var currency)) return currency;
            return int.TryParse(text, out var code) ? code : null;
        }

        // 是否类字段 → 1 / 0
        if (key.StartsWith("Is", StringComparison.OrdinalIgnoreCase))
        {
            if (text is "是" or "Y" or "y" or "true" or "TRUE" or "True" or "1") return 1;
            if (text is "否" or "N" or "n" or "false" or "FALSE" or "False" or "0") return 0;
        }

        // 日期类字段
        if (IsDateField(key))
        {
            if (DateTime.TryParse(text, out var date)) return date;
            return text;
        }

        // 数值类字段：去除千分位与货币符号后解析
        if (IsNumericField(key))
        {
            var cleaned = text.Replace(",", string.Empty).Replace("￥", string.Empty)
                .Replace("$", string.Empty).Replace("€", string.Empty).Trim();
            if (decimal.TryParse(cleaned, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var number))
                return number;
        }

        return text;
    }

    /// <summary>是否为日期字段（字段名以 Date / Time 结尾）</summary>
    private static bool IsDateField(string key) =>
        key.EndsWith("Date", StringComparison.OrdinalIgnoreCase) ||
        key.EndsWith("Time", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("SailingDate", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否为数值字段（命中数值关键字）</summary>
    private static bool IsNumericField(string key) =>
        NumericKeywords.Any(k => key.Contains(k, StringComparison.OrdinalIgnoreCase));
}
