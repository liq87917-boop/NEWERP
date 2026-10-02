using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;

namespace ERP.Application.Services;

/// <summary>
/// 动态柜量与装柜利用率证据报表（ERP-252）的纯规则：有限、有序字段白名单、字段 / 日期 / 分页 / 可选应用筛选校验、
/// 行投影与只读 / 边界 / 免责 / 来源 / 来源上限 / 数量单位 / 未知实际容积 / 柜型 / 出运文案。无数据库依赖，便于逐条单测。
/// <para>复用既有「柜量与装柜利用率统计」（container-stats）菜单授权与 <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围；
/// 行口径与既有 <see cref="ReportService.GetContainerStatsAsync"/>（ERP-251）完全一致：装柜日历日 × 精确原始非空白柜号证据桶，
/// 空白 / 纯空白柜号按装柜清单 Id 独立成桶；签名持久化箱数 / 毛重 / 体积求和作为头证据；装载率与柜型恒为未知。</para>
/// </summary>
public static class DynamicContainerStatsReportRules
{
    // ==================== 0. 常量 ====================

    /// <summary>预览所需的既有菜单编码（复用柜量与装柜利用率统计菜单；与 SeedData / SchemaUpgrader 同源）</summary>
    public const string RequiredMenuCode = "container-stats";

    /// <summary>预览所需菜单的中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "柜量与装柜利用率统计";

    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 20;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多证据桶）</summary>
    public const int MaxPageSize = 200;

    /// <summary>允许的装柜日期区间最大跨度（含首尾日历日）：366 天，与既有柜量统计口径一致</summary>
    public const int MaxDateRangeDays = 366;

    /// <summary>柜号关键字筛选最大长度（有界：去首尾空白后最多 80 字符，超出直接拒绝）</summary>
    public const int MaxFilterKeywordLength = 80;

    /// <summary>装载率 / 柜型未知的展示文本（两者都无权威证据，恒为未知）</summary>
    public const string UnknownUtilizationTypeText = "未知";

    /// <summary>证据桶上下文标签</summary>
    public const string ContextLabel = "装柜日历日 × 原始非空白柜号证据桶";

    /// <summary>证据依据口径：已审核·未删除·授权范围装柜清单头证据，非实际发货 / 实体柜 / 收入 / 装载率 / 柜型权威</summary>
    public const string SourceEvidenceBasis = "已审核·未删除·授权范围装柜清单头证据（非实际发货/实体柜/收入/装载率/柜型权威）";

    // ==================== 1. 文案 ====================

    /// <summary>只读声明（接口与文档统一声明）</summary>
    public const string ReadOnlyText =
        "只读柜量与装柜利用率证据预览：仅按选定白名单字段与有界日期 / 筛选 / 分页读取当前账号数据范围内的已审核装柜清单头证据，不新增 / 修改 / 删除任何记录";

    /// <summary>边界口径文案</summary>
    public const string BoundaryText =
        "口径：字段仅限柜量与装柜利用率证据字段白名单（装柜日历日 / 原始柜号 / 装柜清单数 / 授权范围客户数 / 箱数 / 毛重 / 体积 / 未知装载率柜型 / 未知原因）；" +
        "筛选仅限开始 / 结束日期（含首尾最多 366 天）、客户 Id（正整数）与柜号关键字（去首尾空白最多 80 字符、字面文本、拒绝控制字符），分页页码 ≥ 1、每页 1~200；" +
        "结果限定在当前账号业务员数据范围（特权账号不受限）；装载率 / 柜型恒为未知，绝不按 68m³ 估算、绝不按范围客户数推断整柜 / 拼柜；不执行任意 SQL、不做写入";

    /// <summary>免责文案</summary>
    public const string DisclaimerText =
        "本预览为只读柜量与装柜利用率证据：箱数 / 毛重 / 体积取自签名持久化的装柜清单头，非实际发货、实体柜、实际收入或收款；装载率 / 柜型未知（缺少权威容积 / 整柜 / 满柜证据），不回落为 0，不推断满载状态";

    /// <summary>空页显式说明</summary>
    public const string EmptyText = "没有符合所选日期范围、数据范围与筛选的已审核装柜清单头（或记录已被软删除）";

    /// <summary>页面覆盖口径：仅当前页，绝不声称一次性返回全部行</summary>
    public const string PageOnlyText =
        "页面覆盖：本页仅展示当前分页内的证据桶；Total 为范围内证据桶总数（分页前，服务端派生），本页之外的行不在此展示";

    /// <summary>来源口径：已审核、未删除、授权范围装柜清单头证据</summary>
    public const string SourceContextText =
        "来源证据：已审核、未删除、当前账号数据范围内的装柜清单头证据（非实际发货 / 实体柜 / 收入 / 装载率 / 柜型权威）";

    /// <summary>来源上限口径：有界读取，超出即 fail closed</summary>
    public const string SourceLimitText =
        "来源上限：仅已审核、未删除、当前账号数据范围内的装柜清单头；单次来源上限 500 张装柜清单，超出即 fail closed";

    /// <summary>数量单位口径：签名持久化头箱数 / 毛重 / 体积，单位独立，绝不跨单位合计</summary>
    public const string UnitContextText =
        "数量单位口径：箱数（cartons）、毛重（kg）、体积（m³）为签名持久化装柜清单头证据，单位独立，绝不跨单位合计、绝不按金额格式化";

    /// <summary>未知实际容积口径：无权威容积 / 整柜 / 满柜证据，装载率未知</summary>
    public const string UnknownCapacityContextText =
        "未知实际容积：缺少权威容积 / 整柜 / 满柜证据，装载率未知（不按 68m³ 估算、不回落为 0）";

    /// <summary>柜型口径：不推断整柜 / 拼柜 / 满载状态，柜型未知</summary>
    public const string TypeContextText =
        "柜型口径：范围客户数不是整柜 / 拼柜的权威依据，柜型未知（不推断整柜 / 拼柜 / 满载状态）";

    /// <summary>出运口径：非实际发货 / 实体柜 / 收入 / 收款证据</summary>
    public const string ShippingContextText =
        "出运口径：箱数 / 毛重 / 体积为装柜清单头证据，非实际发货 / 实体柜 / 收入 / 收款，绝不据此推断实体柜容量或出运状态";

    /// <summary>客户范围口径：仅当前账号业务员数据范围（特权账号不受限；受限制业务员仅其被分配客户），绝不泄露范围外客户 / 记录</summary>
    public const string CustomerScopeContextText =
        "客户范围口径：仅当前账号业务员数据范围（特权账号不受限；受限制业务员仅其被分配客户）；绝不泄露范围外客户 / 记录，绝不据此推断整柜 / 拼柜 / 装载率 / 柜型 / 出运状态";

    /// <summary>分组身份口径：装柜日历日 × 精确原始非空白柜号证据桶（空白 / 纯空白柜号按装柜清单 Id 独立成桶）</summary>
    public const string GroupingContextText =
        "分组身份口径：装柜日历日 × 精确原始非空白柜号证据桶（空白 / 纯空白柜号按装柜清单 Id 独立成桶，绝不并入伪造共享柜）";

    /// <summary>目录口径：支持的筛选能力说明（仅能力说明，不含任何客户 / 装柜清单数据）</summary>
    public const string SupportedFilterText =
        "可选应用筛选：客户 Id（正整数）与柜号关键字（去首尾空白最多 80 字符、字面文本、拒绝控制字符）；留空 = 不过滤（保留全部已审核装柜清单头证据）";

    // ==================== 2. 字段白名单（有限、有序） ====================

    private sealed record FieldDef(
        string Key, string Label, string DataType, bool Filterable,
        Func<ReportDtos.ContainerStatsItem, object?> Selector);

    private static readonly IReadOnlyList<FieldDef> Fields = new List<FieldDef>
    {
        new("loadingDate", "装柜日历日", "date", false, r => r.LoadingDate),
        new("containerNo", "原始柜号", "string", false, r => r.ContainerNo),
        new("loadingListCount", "装柜清单数", "number", false, r => r.LoadingListCount),
        new("authorizedCustomerCount", "授权范围客户数", "number", false, r => r.AuthorizedCustomerCount),
        new("totalCartons", "箱数(cartons)", "number", false, r => r.TotalCartons),
        new("totalWeight", "毛重(kg)", "number", false, r => r.TotalWeight),
        new("totalVolume", "体积(m³)", "number", false, r => r.TotalVolume),
        new("utilizationType", "装载率/柜型(未知)", "string", false, _ => UnknownUtilizationTypeText),
        new("reasons", "未知原因", "string", false, BuildUnknownReasons),
    };

    /// <summary>装载率 / 柜型未知原因合并文本（两者都是显式未知依据，绝不空缺）</summary>
    private static string BuildUnknownReasons(ReportDtos.ContainerStatsItem item)
    {
        var reasons = new[] { item.UtilizationReason, item.TypeReason }
            .Where(x => !string.IsNullOrEmpty(x));
        return string.Join("；", reasons);
    }

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
            throw BusinessException.InvalidParameter("柜量与装柜利用率证据报表的结束日期不能早于开始日期");

        var inclusiveDays = (endDate - startDate).Days + 1;
        if (inclusiveDays > MaxDateRangeDays)
            throw BusinessException.InvalidParameter(
                $"柜量与装柜利用率证据报表的日期范围最多 {MaxDateRangeDays} 天（含首尾）");

        if (endDate == DateTime.MaxValue.Date)
            throw BusinessException.InvalidParameter("柜量与装柜利用率证据报表的结束日期无效（结束日次日溢出）");

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

    /// <summary>
    /// 规范化可选应用筛选（fail closed）：客户 Id 必须为正整数、柜号关键字去首尾空白后最多
    /// <see cref="MaxFilterKeywordLength"/> 字符且不含控制字符。两项全部留空时返回 null（表示不过滤）。
    /// </summary>
    public static DynamicContainerStatsReportFilterDto? NormalizeFilter(DynamicContainerStatsReportFilterDto? filter)
    {
        if (filter is null)
            return null;

        var customerId = ValidateFilterCustomerId(filter.CustomerId);
        var containerNo = NormalizeContainerNoKeyword(filter.ContainerNo);

        if (customerId is null && containerNo is null)
            return null;

        return new DynamicContainerStatsReportFilterDto
        {
            CustomerId = customerId,
            ContainerNo = containerNo,
        };
    }

    /// <summary>校验客户 Id 筛选（可选）：提供时必须是正整数（&gt;0），否则 fail closed 拒绝；留空 = 不过滤。</summary>
    public static long? ValidateFilterCustomerId(long? customerId)
    {
        if (customerId is <= 0)
            throw BusinessException.InvalidParameter("客户 Id 筛选必须是正整数（大于 0）");
        return customerId;
    }

    /// <summary>
    /// 规范化柜号关键字（fail closed）：留空 / 全空白 = 不过滤；否则去首尾空白，长度最多
    /// <see cref="MaxFilterKeywordLength"/> 字符、不得包含控制字符，超出 / 非法直接拒绝（先于任何装柜清单读取）。
    /// <c>%</c> / <c>_</c> 保留为字面文本，不做 SQL 通配符语义；大小写沿用数据库既有排序规则，不做额外归一化承诺。
    /// </summary>
    public static string? NormalizeContainerNoKeyword(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return null;

        var trimmed = keyword.Trim();
        if (trimmed.Length > MaxFilterKeywordLength)
            throw BusinessException.InvalidParameter(
                $"柜号关键字筛选最多 {MaxFilterKeywordLength} 个字符（收到 {trimmed.Length} 个字符）");

        if (trimmed.Any(char.IsControl))
            throw BusinessException.InvalidParameter("柜号关键字筛选不能包含控制字符");

        return trimmed;
    }

    /// <summary>把已规范化的应用筛选渲染为上下文文案（客户 Id / 柜号关键字）；无筛选时返回空串。</summary>
    public static string BuildFilterContext(DynamicContainerStatsReportFilterDto? filter)
    {
        if (filter is null)
            return string.Empty;

        var parts = new List<string>();
        if (filter.CustomerId.HasValue)
            parts.Add($"客户 Id {filter.CustomerId.Value}");
        if (!string.IsNullOrEmpty(filter.ContainerNo))
            parts.Add($"柜号关键字 {filter.ContainerNo}");

        return parts.Count == 0 ? string.Empty : string.Join("；", parts);
    }

    // ==================== 4. 目录 / 字段 ====================

    /// <summary>返回有限字段白名单目录 DTO。</summary>
    public static DynamicContainerStatsReportCatalogDto GetCatalogDto() => new(
        Fields.Select(f => new DynamicContainerStatsReportFieldDto(f.Key, f.Label, f.DataType, f.Filterable)).ToList(),
        RequiredMenuCode,
        RequiredMenuText,
        MaxPageSize,
        DefaultPageSize,
        ReadOnlyText,
        BoundaryText,
        SupportedFilterText);

    /// <summary>按键取字段定义（键不存在返回 null）。</summary>
    public static DynamicContainerStatsReportFieldDto? GetField(string key)
    {
        var def = Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));
        return def is null ? null : new DynamicContainerStatsReportFieldDto(def.Key, def.Label, def.DataType, def.Filterable);
    }

    /// <summary>按选定字段顺序投影单行（键为字段键、值为白名单字段值）。</summary>
    public static Dictionary<string, object?> BuildRow(
        ReportDtos.ContainerStatsItem item, IReadOnlyList<string> fieldKeys)
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
    /// 把有界、作用域化的证据桶装配为预览结果页（纯函数）：稳定分页（来源已稳定排序）、投影选定列、
    /// 派生 Total / TotalPages / PageOnly 与证据桶 / 已审核清单上下文。上下文字段即使对应列被隐藏也始终呈现。
    /// </summary>
    public static DynamicContainerStatsReportPageDto BuildPage(
        IReadOnlyList<ReportDtos.ContainerStatsItem> items,
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

        var context = new DynamicContainerStatsReportContextDto(
            ContextLabel,
            total,
            items.Sum(x => x.LoadingListCount),
            SourceEvidenceBasis);

        // ERP-255：在分页 / 选定列投影之前，对同一份完整有界、作用域化、已筛选证据桶纯派生「全匹配」每日与期间汇总
        var summary = DynamicContainerStatsSummaryRules.BuildSummary(items);

        return new DynamicContainerStatsReportPageDto(
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
            SourceContextText,
            SourceLimitText,
            UnitContextText,
            UnknownCapacityContextText,
            TypeContextText,
            ShippingContextText,
            filterText,
            CustomerScopeContextText,
            GroupingContextText,
            context,
            summary);
    }

    /// <summary>把预览页分页信息格式化为上下文文本（日期 / 页 / 条数 / 行总数 / 总页数）。</summary>
    public static string BuildPageContext(DynamicContainerStatsReportPageDto page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return $"第 {page.Page} 页 · 每页 {page.PageSize} 条 · 证据桶总数 {page.Total} · 共 {page.TotalPages} 页";
    }

    /// <summary>把范围上下文格式化为上下文文本（证据桶 / 已审核清单 / 证据依据）。</summary>
    public static string BuildSourceCountContext(DynamicContainerStatsReportContextDto context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return $"{context.Label} {context.BucketCount} 桶 · 已审核清单 {context.ApprovedLists} · {context.EvidenceBasis}";
    }

    // ==================== 6. Excel 导出 ====================

    /// <summary>Excel 数据工作表名（选定字段证据）</summary>
    public const string DataSheetName = "柜量装柜证据";

    /// <summary>Excel 报表口径上下文工作表名</summary>
    public const string ContextSheetName = "报表口径";

    /// <summary>未知装载率 / 柜型的显式展示文本（未知，而非数值 0 或 68m³ 推导百分比）</summary>
    public const string UnknownValueText = "未知";

    /// <summary>上下文表「开始日期」行标签</summary>
    public const string ContextStartLabel = "开始日期";

    /// <summary>上下文表「结束日期」行标签</summary>
    public const string ContextEndLabel = "结束日期";

    /// <summary>上下文表「分页」行标签</summary>
    public const string ContextPageLabel = "分页";

    /// <summary>上下文表「页面覆盖」行标签</summary>
    public const string ContextPageOnlyLabel = "页面覆盖";

    /// <summary>上下文表「来源上限」行标签</summary>
    public const string ContextSourceLimitLabel = "来源上限";

    /// <summary>上下文表「来源计数」行标签</summary>
    public const string ContextSourceCountLabel = "来源计数";

    /// <summary>上下文表「客户范围口径」行标签</summary>
    public const string ContextCustomerScopeLabel = "客户范围口径";

    /// <summary>上下文表「分组身份口径」行标签</summary>
    public const string ContextGroupingLabel = "分组身份口径";

    /// <summary>上下文表「数量单位口径」行标签</summary>
    public const string ContextUnitLabel = "数量单位口径";

    /// <summary>上下文表「未知实际容积口径」行标签</summary>
    public const string ContextUnknownCapacityLabel = "未知实际容积口径";

    /// <summary>上下文表「柜型口径」行标签</summary>
    public const string ContextTypeLabel = "柜型口径";

    /// <summary>上下文表「出运口径」行标签</summary>
    public const string ContextShippingLabel = "出运口径";

    /// <summary>上下文表「来源证据」行标签</summary>
    public const string ContextSourceLabel = "来源证据";

    /// <summary>上下文表「边界口径」行标签</summary>
    public const string ContextBoundaryLabel = "边界口径";

    /// <summary>上下文表「免责声明」行标签</summary>
    public const string ContextDisclaimerLabel = "免责声明";

    /// <summary>上下文表「只读声明」行标签</summary>
    public const string ContextReadOnlyLabel = "只读声明";

    /// <summary>上下文表「应用筛选」行标签</summary>
    public const string ContextFilterLabel = "应用筛选";

    /// <summary>上下文表「空页说明」行标签</summary>
    public const string ContextEmptyLabel = "空页说明";

    /// <summary>上下文表「无筛选」显示文案</summary>
    public const string ContextNoFilterText = "无筛选";

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
    /// 把一页预览行转成导出行（字面文本、公式安全）：日期统一规范化为 <c>yyyy-MM-dd</c> 文本；字符串做公式注入转义；
    /// 数值（有符号箱数 / 毛重 / 体积 / 授权范围计数）原样保留为数值单元格（含负数 / 0）；装载率 / 柜型字段恒为字面「未知」，
    /// 绝不写成数值 0 或按 68m³ 推导百分比。
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
            export[key] = value switch
            {
                DateTime dt => dt.ToString("yyyy-MM-dd"),
                string s => EscapeFormulaLeading(s),
                _ => value,
            };
        }

        return export;
    }

}



