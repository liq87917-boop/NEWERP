using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Enums;

namespace ERP.Application.Services;

/// <summary>
/// 动态业务员提成证据报表（ERP-244）的纯规则：有限、有序字段白名单、字段 / 日期 / 分页 / 可选应用筛选校验、
/// 行投影与只读 / 边界 / 免责 / 原币 / 未知 / 未知利润 / 未知提成 / 当前参考比例 / 来源 / 来源上限文案。
/// 无数据库依赖，便于逐条单测。
/// <para>复用既有「业务员提成表」（sales-commission）菜单授权与 <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围；
/// 行口径与既有 <see cref="ReportService.GetSalesCommissionAsync"/>（ERP-243）完全一致：
/// 持久化业务员桶 × 原始原币证据行、金额仅在已知币种下签名小计、未知币种金额为 null、利润 / 利润率 / 提成额恒为未知（null）、
/// 提成比例为可空的「当前参考比例」、绝不跨币种合计。</para>
/// </summary>
public static class DynamicSalesCommissionReportRules
{
    // ==================== 0. 常量 ====================

    /// <summary>预览所需的既有菜单编码（复用业务员提成表菜单；与 SeedData / SchemaUpgrader 同源）</summary>
    public const string RequiredMenuCode = "sales-commission";

    /// <summary>预览所需菜单的中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "业务员提成表";

    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 20;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多业务员桶 × 原币证据行）</summary>
    public const int MaxPageSize = 200;

    /// <summary>允许的订单日期区间最大跨度（含首尾日历日）：366 天，与既有业务员提成口径一致</summary>
    public const int MaxDateRangeDays = 366;

    /// <summary>业务员姓名关键字筛选最大长度（有界：去首尾空白后最多 80 字符，超出直接拒绝）</summary>
    public const int MaxFilterKeywordLength = 80;

    /// <summary>支持的原币币种码列表（与 <see cref="Currency"/> 枚举 / <see cref="SalesCommissionEvidenceRules"/> 已知码同源）</summary>
    public const string SupportedCurrencyText = "CNY / USD / EUR / HKD / GBP / JPY";

    /// <summary>业务员桶 × 原币证据行上下文标签</summary>
    public const string ContextLabel = "业务员桶 × 原币证据行";

    /// <summary>证据依据口径：已审核·未删除·授权客户销售订单证据，非总 ERP 订单 / 产值 / 实际收入 / 出货 / 收款</summary>
    public const string SourceEvidenceBasis = "已审核·未删除·授权客户销售订单证据（非总ERP订单/产值/实际收入/出货/收款）";

    // ==================== 1. 文案 ====================

    /// <summary>只读声明（接口与文档统一声明）</summary>
    public const string ReadOnlyText =
        "只读业务员提成证据预览：仅按选定白名单字段与有界日期 / 筛选 / 分页读取当前账号数据范围内的已审核销售订单证据，不新增 / 修改 / 删除任何记录";

    /// <summary>边界口径文案</summary>
    public const string BoundaryText =
        "口径：字段仅限业务员提成证据字段白名单（业务员桶 / 原币 / 订单数 / 已知原币金额小计 / 未知利润 / 未知利润率 / 当前参考提成比例 / 未知提成额 / 显式来源依据）；" +
        "筛选仅限开始 / 结束日期（含首尾最多 366 天）、客户 Id（正整数）、业务员 Id（正整数）、业务员姓名关键字（去首尾空白最多 80 字符、字面文本、拒绝控制字符）与原币币种（" + SupportedCurrencyText + "），分页页码 ≥ 1、每页 1~200；" +
        "结果限定在当前账号业务员数据范围（特权账号不受限）；金额按业务员桶 × 原币独立小计、利润 / 利润率 / 提成额恒为未知，绝不跨币种合计；不执行任意 SQL、不做写入";

    /// <summary>免责文案</summary>
    public const string DisclaimerText =
        "本预览为只读业务员提成证据：金额取自已审核、未删除、授权客户的销售订单原币金额，非实际收款金额；利润 / 利润率 / 提成额未知，不回落为 0，不做当前价利润推断；提成比例为当前参考（非历史约定比例 / 实际提成 / 客户代理费 / 台账分录）";

    /// <summary>空页显式说明</summary>
    public const string EmptyText = "没有符合所选日期范围与数据范围的已审核销售订单（或记录已被软删除）";

    /// <summary>页面覆盖口径：仅当前页，绝不声称一次性返回全部行</summary>
    public const string PageOnlyText =
        "页面覆盖：本页仅展示当前分页内的业务员桶 × 原币证据行；Total 为范围内证据行总数（分页前，服务端派生），本页之外的行不在此展示";

    /// <summary>原币口径：金额按业务员桶 × 原币独立小计，绝不跨币种合计</summary>
    public const string CurrencyContextText =
        "原币口径：金额按业务员桶 × 原币独立小计，绝不跨币种合计、不折算、不默认币种、不推断汇率";

    /// <summary>未知口径：未知 / 无效币种金额与未知利润 / 利润率 / 提成额以未知呈现，绝不回落为 0</summary>
    public const string UnknownContextText =
        "未知口径：未知 / 无效币种金额与未知利润 / 利润率 / 提成额为未知（仅订单头计数证据）；绝不回落为 0";

    /// <summary>利润口径：利润 / 利润率恒为未知（null），绝不回落为 0、不做当前价利润推断</summary>
    public const string ProfitContextText =
        "利润口径：利润 / 利润率恒为未知（null）；当前商品售价 / 成本价不能证明历史可比较成本 / 利润，绝不回落为 0";

    /// <summary>提成口径：提成额未知；提成比例为当前参考，非历史约定比例 / 实际提成 / 客户代理费 / 台账分录</summary>
    public const string CommissionContextText =
        "提成口径：提成额未知（历史可比较成本 / 利润缺失）；提成比例为当前参考（非历史约定比例、非实际提成、非客户代理费、非台账分录）";

    /// <summary>当前参考比例口径：系统参数仅为可空的当前参考，缺失 / 重复 / 非法一律未知</summary>
    public const string RateContextText =
        "当前参考比例：系统参数 SalesCommissionRate 仅为可空的当前参考比例（缺失 / 重复 / 非法一律未知），非历史约定比例、非实际提成、非客户代理费、非台账分录";

    /// <summary>来源口径：已审核、未删除、授权客户销售订单证据</summary>
    public const string SourceContextText =
        "来源证据：已审核、未删除、当前账号数据范围内的销售订单证据（非总 ERP 订单 / 产值 / 实际收入 / 出货 / 收款）";

    /// <summary>来源上限口径：有界读取，超出即 fail closed</summary>
    public const string SourceLimitText =
        "来源上限：仅已审核、未删除、当前账号数据范围内的销售订单头；单次来源上限 500 张订单，超出即 fail closed";

    // ==================== 2. 字段白名单（有限、有序） ====================

    private sealed record FieldDef(
        string Key, string Label, string DataType, bool Filterable,
        Func<ReportDtos.SalesCommissionItem, object?> Selector);

    private static readonly IReadOnlyList<FieldDef> Fields = new List<FieldDef>
    {
        new("salesmanId", "业务员 Id", "number", false, r => r.SalesmanId),
        new("salesmanName", "业务员", "string", false, r => r.SalesmanName),
        new("salesmanIdentityEvidence", "业务员身份依据", "string", false, r => r.SalesmanIdentityEvidence),
        new("currency", "原币币种", "string", false, r => r.Currency),
        new("currencyLabel", "原币币种标签", "string", false, r => r.CurrencyLabel),
        new("orderCount", "已审核订单数", "number", false, r => r.OrderCount),
        new("salesAmount", "已知原币金额小计", "money", false, r => r.SalesAmount),
        new("amountLabel", "金额口径", "string", false, r => r.AmountLabel),
        new("currencyEvidence", "币种证据", "string", false, r => r.CurrencyEvidence),
        new("profit", "利润(未知)", "money", false, r => r.Profit),
        new("profitEvidence", "利润依据", "string", false, r => r.ProfitEvidence),
        new("profitRate", "利润率%(未知)", "number", false, r => r.ProfitRate),
        new("profitRateEvidence", "利润率依据", "string", false, r => r.ProfitRateEvidence),
        new("commissionRate", "提成比例%(当前参考)", "number", false, r => r.CommissionRate),
        new("commissionRateEvidence", "提成比例依据", "string", false, r => r.CommissionRateEvidence),
        new("commissionAmount", "提成额(未知)", "money", false, r => r.CommissionAmount),
        new("commissionEvidence", "提成依据", "string", false, r => r.CommissionEvidence),
        new("sourceLabel", "来源依据", "string", false, r => r.SourceLabel),
    };

    // ==================== 3. 校验 / 规范化 ====================

    /// <summary>
    /// 规范化选定字段键：仅限白名单；未知 / 重复 / 空键由服务端 fail closed 拒绝。
    /// 留空返回全部白名单字段（保持目录顺序）。
    /// </summary>
    public static IReadOnlyList<string> NormalizeFields(List<string>? fields)
    {
        if (fields is null || fields.Count == 0)
            return Fields.Select(f => f.Key).ToList();

        var valid = new HashSet<string>(Fields.Select(f => f.Key), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(fields.Count);
        foreach (var raw in fields)
        {
            var key = raw?.Trim() ?? string.Empty;
            if (key.Length == 0 || !valid.Contains(key))
                throw BusinessException.InvalidParameter($"未知字段键：{raw ?? string.Empty}");
            if (!seen.Add(key))
                throw BusinessException.InvalidParameter($"字段键重复：{key}");
            result.Add(key);
        }

        return result;
    }

    /// <summary>
    /// 规范化日期窗口（fail closed）：只取日期部分；结束不得早于开始；含首尾最多 <see cref="MaxDateRangeDays"/> 天；
    /// 结束日为最大日期时显式拒绝（结束日次日溢出防护，先于任何源读取）。
    /// </summary>
    public static (DateTime Start, DateTime End) ValidateDateRange(DateTime? start, DateTime? end)
    {
        var startDate = (start ?? DateTime.Today).Date;
        var endDate = (end ?? DateTime.Today).Date;

        if (endDate < startDate)
            throw BusinessException.InvalidParameter("业务员提成证据报表的结束日期不能早于开始日期");

        var inclusiveDays = (endDate - startDate).Days + 1;
        if (inclusiveDays > MaxDateRangeDays)
            throw BusinessException.InvalidParameter(
                $"业务员提成证据报表的日期范围最多 {MaxDateRangeDays} 天（含首尾）");

        if (endDate == DateTime.MaxValue.Date)
            throw BusinessException.InvalidParameter("业务员提成证据报表的结束日期无效（结束日次日溢出）");

        return (startDate, endDate);
    }

    /// <summary>校验分页边界（fail closed）：页码 ≥ 1，每页 1 ~ 200。</summary>
    public static void ValidatePageBounds(int page, int pageSize)
    {
        if (page < 1)
            throw BusinessException.InvalidParameter("页码必须 ≥ 1");
        if (pageSize < 1 || pageSize > MaxPageSize)
            throw BusinessException.InvalidParameter($"每页条数必须在 1 ~ {MaxPageSize} 之间");
    }

    // ==================== 3.1 可选应用筛选（fail closed） ====================

    /// <summary>目录口径：支持的筛选能力说明（仅能力说明，不含任何业务员 / 订单 / 金额数据）</summary>
    public const string SupportedFilterText =
        "可选应用筛选：客户 Id（正整数）、业务员 Id（正整数）、业务员姓名关键字（去首尾空白最多 80 字符、字面文本）与原币币种（" + SupportedCurrencyText + "）；留空 = 不过滤（保留全部已审核销售订单证据）";

    /// <summary>
    /// 规范化可选应用筛选（fail closed）：客户 Id / 业务员 Id 必须为正整数、业务员姓名关键字去首尾空白后最多
    /// <see cref="MaxFilterKeywordLength"/> 字符且不含控制字符、原币币种仅接受空白（全部）/ 已知 <see cref="Currency"/> 枚举码；
    /// 非法 / 数字 / 未知取值直接拒绝，绝不静默丢弃或回退币种。四项全部留空时返回 null（表示不过滤）。
    /// </summary>
    public static SalesCommissionFilterDto? NormalizeFilter(SalesCommissionFilterDto? filter)
    {
        if (filter is null)
            return null;

        var customerId = ValidateFilterCustomerId(filter.CustomerId);
        var salesmanId = ValidateFilterSalesmanId(filter.SalesmanId);
        var salesmanName = NormalizeSalesmanNameKeyword(filter.SalesmanName);
        var currency = NormalizeCurrencyFilter(filter.Currency);

        if (customerId is null && salesmanId is null && salesmanName is null && currency is null)
            return null;

        return new SalesCommissionFilterDto
        {
            CustomerId = customerId,
            SalesmanId = salesmanId,
            SalesmanName = salesmanName,
            Currency = currency,
        };
    }

    /// <summary>校验客户 Id 筛选（可选）：提供时必须是正整数（&gt;0），否则 fail closed 拒绝；留空 = 不过滤。</summary>
    public static long? ValidateFilterCustomerId(long? customerId)
    {
        if (customerId is <= 0)
            throw BusinessException.InvalidParameter("客户 Id 筛选必须是正整数（大于 0）");
        return customerId;
    }

    /// <summary>校验业务员 Id 筛选（可选）：提供时必须是正整数（&gt;0），否则 fail closed 拒绝；留空 = 不过滤（仅订单属性，非权限边界）。</summary>
    public static long? ValidateFilterSalesmanId(long? salesmanId)
    {
        if (salesmanId is <= 0)
            throw BusinessException.InvalidParameter("业务员 Id 筛选必须是正整数（大于 0）");
        return salesmanId;
    }

    /// <summary>
    /// 规范化业务员姓名关键字（fail closed）：留空 / 全空白 = 不过滤；否则去首尾空白，长度最多
    /// <see cref="MaxFilterKeywordLength"/> 字符、不得包含控制字符，超出 / 非法直接拒绝（先于任何订单读取）。
    /// <c>%</c> / <c>_</c> 保留为字面文本，不做 SQL 通配符语义；大小写沿用数据库既有排序规则，不做额外归一化承诺。
    /// </summary>
    public static string? NormalizeSalesmanNameKeyword(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return null;

        var trimmed = keyword.Trim();
        if (trimmed.Length > MaxFilterKeywordLength)
            throw BusinessException.InvalidParameter(
                $"业务员姓名关键字筛选最多 {MaxFilterKeywordLength} 个字符（收到 {trimmed.Length} 个字符）");

        if (trimmed.Any(char.IsControl))
            throw BusinessException.InvalidParameter("业务员姓名关键字筛选不能包含控制字符");

        return trimmed;
    }

    /// <summary>
    /// 规范化原币币种筛选（fail closed）：留空 = 全部；已知 <see cref="Currency"/> 枚举码（大小写不敏感）去首尾空白并
    /// 大写归一化为枚举名；纯数字与未知取值直接拒绝（绝不回退为 CNY 或任何默认币种）。不提供「未知币种」桶。
    /// </summary>
    public static string? NormalizeCurrencyFilter(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
            return null;

        var value = currency.Trim();
        if (value.All(char.IsDigit))
            throw BusinessException.InvalidParameter(
                $"无效的原币币种筛选: {currency}（可选：{SupportedCurrencyText}）");

        if (Enum.TryParse<Currency>(value, true, out var parsed) && Enum.IsDefined(parsed))
            return parsed.ToString();

        throw BusinessException.InvalidParameter(
            $"无效的原币币种筛选: {currency}（可选：{SupportedCurrencyText}）");
    }

    /// <summary>把已规范化的应用筛选渲染为上下文文案（客户 Id / 业务员 Id / 业务员姓名关键字 / 原币币种）；无筛选时返回空串。</summary>
    public static string BuildFilterContext(SalesCommissionFilterDto? filter)
    {
        if (filter is null)
            return string.Empty;

        var parts = new List<string>();
        if (filter.CustomerId.HasValue)
            parts.Add($"客户 Id {filter.CustomerId.Value}");
        if (filter.SalesmanId.HasValue)
            parts.Add($"业务员 Id {filter.SalesmanId.Value}");
        if (!string.IsNullOrEmpty(filter.SalesmanName))
            parts.Add($"业务员姓名关键字 {filter.SalesmanName}");
        if (!string.IsNullOrEmpty(filter.Currency))
            parts.Add($"原币币种 {filter.Currency}");

        return parts.Count == 0 ? string.Empty : string.Join("；", parts);
    }

    // ==================== 4. 目录 / 字段 ====================

    /// <summary>返回有限字段白名单目录 DTO。</summary>
    public static DynamicSalesCommissionReportCatalogDto GetCatalogDto() => new(
        Fields.Select(f => new DynamicSalesCommissionReportFieldDto(f.Key, f.Label, f.DataType, f.Filterable)).ToList(),
        RequiredMenuCode,
        RequiredMenuText,
        MaxPageSize,
        DefaultPageSize,
        ReadOnlyText,
        BoundaryText,
        SupportedFilterText);

    /// <summary>按键取字段定义（键不存在返回 null）。</summary>
    public static DynamicSalesCommissionReportFieldDto? GetField(string key)
    {
        var def = Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));
        return def is null ? null : new DynamicSalesCommissionReportFieldDto(def.Key, def.Label, def.DataType, def.Filterable);
    }

    /// <summary>按选定字段顺序投影单行（键为字段键、值为白名单字段值；未知金额 / 利润 / 利润率 / 提成额 / 提成比例保持 null）。</summary>
    public static Dictionary<string, object?> BuildRow(
        ReportDtos.SalesCommissionItem item, IReadOnlyList<string> fieldKeys)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(fieldKeys);

        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in fieldKeys)
        {
            var def = Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal))
                ?? throw BusinessException.InvalidParameter($"未知字段键：{key}");
            row[key] = def.Selector(item);
        }

        return row;
    }

    // ==================== 5. 页面装配 ====================

    /// <summary>
    /// 把有界、作用域化的业务员桶 × 原币证据行装配为预览结果页（纯函数）：
    /// 稳定分页（来源已稳定排序）、投影选定列、派生 Total / TotalPages / PageOnly 与去重业务员桶 / 已审核订单上下文。
    /// </summary>
    public static DynamicSalesCommissionReportPageDto BuildPage(
        IReadOnlyList<ReportDtos.SalesCommissionItem> items,
        IReadOnlyList<string> fieldKeys,
        int page,
        int pageSize,
        DateTime start,
        DateTime end,
        string filterText = "")
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(fieldKeys);

        var total = items.Count;
        var totalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize);
        var skip = (page - 1) * pageSize;
        var pageItems = items.Skip(skip).Take(pageSize).ToList();
        var truncated = skip + pageItems.Count < total;
        var pageOnly = skip > 0 || truncated;

        var columns = fieldKeys.Select(k => GetField(k)!).ToList();
        var rows = pageItems.Select(x => BuildRow(x, fieldKeys)).ToList();

        var context = new DynamicSalesCommissionReportContextDto(
            ContextLabel,
            total,
            items.Select(x => x.SalesmanId).Distinct().Count(),
            items.Sum(x => x.OrderCount),
            SourceEvidenceBasis);

        // ERP-247：在分页 / 选定列投影之前，基于同一份完整有界作用域化列表纯派生「全匹配」原币汇总。
        var summary = DynamicSalesCommissionSummaryRules.BuildSummary(items);

        return new DynamicSalesCommissionReportPageDto(
            columns,
            rows,
            total,
            page,
            pageSize,
            totalPages,
            truncated,
            pageOnly,
            PageOnlyText,
            rows.Count == 0 ? EmptyText : string.Empty,
            ReadOnlyText,
            BoundaryText,
            DisclaimerText,
            start,
            end,
            CurrencyContextText,
            UnknownContextText,
            ProfitContextText,
            CommissionContextText,
            RateContextText,
            SourceContextText,
            SourceLimitText,
            filterText,
            context,
            summary);
    }

    // ==================== 6. Excel 导出 ====================

    /// <summary>Excel 数据工作表名（选定字段证据）</summary>
    public const string DataSheetName = "业务员提成证据";

    /// <summary>Excel 报表口径上下文工作表名</summary>
    public const string ContextSheetName = "报表口径";

    /// <summary>未知 / null 金额与利润 / 利润率 / 提成比例 / 提成额的显式展示文本（未知，而非数值 0）</summary>
    public const string UnknownValueText = "未知";

    /// <summary>上下文表「开始日期」行标签</summary>
    public const string ContextStartLabel = "开始日期";

    /// <summary>上下文表「结束日期」行标签</summary>
    public const string ContextEndLabel = "结束日期";

    /// <summary>上下文表「分页」行标签</summary>
    public const string ContextPageLabel = "分页";

    /// <summary>上下文表「来源上限」行标签</summary>
    public const string ContextSourceLimitLabel = "来源上限";

    /// <summary>上下文表「来源计数」行标签</summary>
    public const string ContextSourceCountLabel = "来源计数";

    /// <summary>上下文表「币种口径」行标签</summary>
    public const string ContextCurrencyLabel = "币种口径";

    /// <summary>上下文表「未知口径」行标签</summary>
    public const string ContextUnknownLabel = "未知口径";

    /// <summary>上下文表「利润口径」行标签</summary>
    public const string ContextProfitLabel = "利润口径";

    /// <summary>上下文表「提成口径」行标签</summary>
    public const string ContextCommissionLabel = "提成口径";

    /// <summary>上下文表「当前参考比例」行标签</summary>
    public const string ContextRateLabel = "当前参考比例";

    /// <summary>上下文表「来源证据」行标签</summary>
    public const string ContextSourceLabel = "来源证据";

    /// <summary>上下文表「页面覆盖」行标签</summary>
    public const string ContextPageOnlyLabel = "页面覆盖";

    /// <summary>上下文表「只读声明」行标签</summary>
    public const string ContextReadOnlyLabel = "只读声明";

    /// <summary>上下文表「应用筛选」行标签</summary>
    public const string ContextFilterLabel = "应用筛选";

    /// <summary>上下文表「空页说明」行标签</summary>
    public const string ContextEmptyLabel = "空页说明";

    /// <summary>上下文表「无筛选」显示文案</summary>
    public const string ContextNoFilterText = "无筛选";

    /// <summary>null 金额 / 利润 / 利润率 / 提成比例 / 提成额字段键（这些字段 null = 显式未知，绝非数值 0）</summary>
    private static readonly HashSet<string> UnknownNumericFields = new(StringComparer.Ordinal)
    {
        "salesAmount", "profit", "profitRate", "commissionRate", "commissionAmount",
    };

    /// <summary>字段键的 null 值是否表示「显式未知」（金额 / 利润 / 利润率 / 提成比例 / 提成额）；业务员 Id null 表示未指定业务员桶，非未知。</summary>
    public static bool IsUnknownNumericField(string key)
        => UnknownNumericFields.Contains(key);

    /// <summary>把预览页分页信息格式化为上下文文本（日期 / 页 / 条数 / 行总数 / 总页数）。</summary>
    public static string BuildPageContext(DynamicSalesCommissionReportPageDto page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return $"第 {page.Page} 页 · 每页 {page.PageSize} 条 · 业务员桶×原币行总数 {page.Total} · 共 {page.TotalPages} 页";
    }

    /// <summary>把范围上下文格式化为上下文文本（业务员桶 × 原币证据行 / 去重业务员桶 / 已审核订单 / 证据依据）。</summary>
    public static string BuildSourceCountContext(DynamicSalesCommissionReportContextDto context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return $"{context.Label} {context.SalesmanCurrencyRows} 行 · 去重业务员桶 {context.UniqueSalesmanBuckets} · 已审核订单 {context.ApprovedOrders} · {context.EvidenceBasis}";
    }

    /// <summary>电子表格公式注入风险首字符（OWASP：= / + / - / @ 及制表符 / 回车 / 换行）</summary>
    private static bool IsFormulaLeadingChar(char c)
        => c is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n';

    /// <summary>文本是否以电子表格公式字符开头（会触发 Excel 公式注入）</summary>
    public static bool IsFormulaLeading(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        return IsFormulaLeadingChar(value[0]);
    }

    /// <summary>
    /// 转义 Excel 公式前导文本：以危险字符开头的文本前缀单引号，使单元格保持字面文本、不被当作公式执行。
    /// 仅对字符串生效；数值 / 日期等类型原样返回（由导出端按其类型写入对应单元格）。
    /// </summary>
    public static object? EscapeFormulaLeading(object? value)
    {
        if (value is string s && IsFormulaLeading(s))
            return "'" + s;
        return value;
    }

    /// <summary>
    /// 把一页预览行转成导出行：null 金额 / 利润 / 利润率 / 提成比例 / 提成额显式转为「未知」（绝不写成数值 0），
    /// 业务员 Id null 保留为空（未指定业务员桶，非未知）；其余字符串做公式注入转义，数值 / 计数原样保留。
    /// </summary>
    public static Dictionary<string, object?> BuildExportRow(
        Dictionary<string, object?> row, IReadOnlyList<string> fieldKeys)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(fieldKeys);

        var export = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in fieldKeys)
        {
            var value = row.TryGetValue(key, out var v) ? v : null;
            export[key] = value is null && IsUnknownNumericField(key)
                ? UnknownValueText
                : EscapeFormulaLeading(value);
        }
        return export;
    }
}
