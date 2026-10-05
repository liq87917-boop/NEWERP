using ERP.Application.Services;

namespace ERP.Application.DTOs;

/// <summary>
/// 通用报表配置捆绑（ERP-307 Stage 2）的平台常量：无状态、客户端不可递增，与
/// <see cref="ReportConfigurationExecutionLimits"/> 对齐的跨节总量护栏。
/// <para>捆绑只引用既有定义 Id / 版本，不新增实体、不新增数据库结构；任何一节被拒绝 / 撤销 / 失效都
/// 使整个捆绑失败（fail closed），绝不产出部分数据或部分下载。</para>
/// </summary>
public static class ReportConfigurationBundleLimits
{
    /// <summary>捆绑最少节数（1）</summary>
    public const int MinSections = 1;

    /// <summary>捆绑最多节数（8；有界有序列表）</summary>
    public const int MaxSections = 8;

    /// <summary>单节可选标题最大长度（与配置名称上限一致；空 = 回退定义名称）</summary>
    public const int MaxSectionTitleLength = 200;

    /// <summary>捆绑预览跨节合计行数上限（8 × 单节当前页 200 行）</summary>
    public const int MaxTotalRows = MaxSections * ReportConfigurationExecutionLimits.MaxPreviewRows;

    /// <summary>捆绑导出跨节合计事实行数上限（8 × 单节有界一致快照 1000 条）</summary>
    public const int MaxTotalExportRows = MaxSections * ReportConfigurationExecutionLimits.MaxSnapshotFacts;

    /// <summary>捆绑预览序列化体积上限（8 MiB；逐节预览已受 1 MiB 单节上限约束，此处兜底跨节总量）</summary>
    public const int MaxSerializedBundlePreviewBytes = 8 * 1024 * 1024;
}

/// <summary>
/// 捆绑内单个节的请求（ERP-307 Stage 2）：无状态地引用一个既有私有 / 共享定义与可选固定发布版本。
/// <para>绝不携带定义正文 / SQL / 字段名 / 跨数据集联接语义；节标题仅供展示，可为空（回退定义名称）。</para>
/// </summary>
public sealed class ReportConfigurationBundleSectionRequest
{
    /// <summary>既有私有报表配置 Id（自有草稿 / 自有发布修订 / 被共享的固定发布快照）</summary>
    public long ConfigurationId { get; set; }

    /// <summary>固定发布修订版本号（可选；空 = 自有当前草稿；被共享节必须提供固定版本）</summary>
    public int? RevisionVersion { get; set; }

    /// <summary>节展示标题（可选，≤200 字符；空 = 回退定义名称）</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>页码覆盖（可选；空 = 使用已保存展示分页 / 数据集默认）</summary>
    public int? Page { get; set; }

    /// <summary>每页条数覆盖（可选；空 = 使用已保存展示分页 / 数据集默认；受数据集上限约束）</summary>
    public int? PageSize { get; set; }
}

/// <summary>捆绑预览 / 导出请求（ERP-307 Stage 2）：有界有序的既有定义 / 版本引用列表。</summary>
public sealed class ReportConfigurationBundleRequest
{
    /// <summary>有序节列表（1 ~ 8 节；顺序即输出顺序）</summary>
    public List<ReportConfigurationBundleSectionRequest> Sections { get; set; } = new();
}

/// <summary>捆绑预览内单个节的解析结果（ERP-307 Stage 2）：标题 + 该节已授权、已校验的预览。</summary>
public sealed class ReportConfigurationBundleSectionDto
{
    /// <summary>节序号（从 1 开始，稳定排序）</summary>
    public int Ordinal { get; set; }

    /// <summary>节展示标题（服务端解析后的稳定标题）</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>既有报表配置 Id</summary>
    public long ConfigurationId { get; set; }

    /// <summary>固定发布修订版本号（可选）</summary>
    public int? RevisionVersion { get; set; }

    /// <summary>该节的有界、已授权预览（列 / 行 / 分组 / 指标 / 证据上下文）</summary>
    public ReportConfigurationPreviewDto Preview { get; set; } = new();
}

/// <summary>捆绑预览结果（ERP-307 Stage 2）：全部节都加载并校验通过后才返回，绝不返回部分节。</summary>
public sealed class ReportConfigurationBundlePreviewDto
{
    /// <summary>捆绑展示名称（服务端统一命名；非用户提交）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>本次执行的关联 ID（受控追踪，绝不泄露 SQL / 栈 / 私有值）</summary>
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>有序节结果（1 ~ 8 节）</summary>
    public List<ReportConfigurationBundleSectionDto> Sections { get; set; } = new();

    /// <summary>节数</summary>
    public int SectionCount { get; set; }

    /// <summary>跨节合计预览行数（有界）</summary>
    public int TotalRowCount { get; set; }
}

/// <summary>捆绑导出内单个节的渲染数据（ERP-307 Stage 2）：预览 + 完整导出事实（≤1000 / ≤200）。</summary>
public sealed class ReportConfigurationBundleSectionExportDto
{
    /// <summary>节序号（从 1 开始，稳定排序）</summary>
    public int Ordinal { get; set; }

    /// <summary>节展示标题（服务端解析后的稳定标题）</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>既有报表配置 Id</summary>
    public long ConfigurationId { get; set; }

    /// <summary>固定发布修订版本号（可选）</summary>
    public int? RevisionVersion { get; set; }

    /// <summary>该节的有界、已授权预览（列 / 汇总 / 证据上下文 + 定义名称 / 版本 / 查询 / 排序 / 粒度）</summary>
    public ReportConfigurationPreviewDto Preview { get; set; } = new();

    /// <summary>导出要渲染的完整事实行（matched-set = 全部 ≤1000 条；current-page = 当前预览页 ≤200 行）</summary>
    public List<Dictionary<string, object?>> Facts { get; set; } = new();

    /// <summary>覆盖口径（current-page / matched-set）</summary>
    public string Coverage { get; set; } = ReportConfigurationConstants.CoverageCurrentPage;

    /// <summary>有界匹配事实命中条数（≤ 1000）</summary>
    public int MatchedCount { get; set; }

    /// <summary>导出渲染所基于的来源证据条数（= <see cref="Facts"/> 条数）</summary>
    public int SourceEvidenceCount { get; set; }
}

/// <summary>捆绑导出结果（ERP-307 Stage 2）：全部节都构建并校验通过后才返回，绝不返回部分节。</summary>
public sealed class ReportConfigurationBundleExportResultDto
{
    /// <summary>捆绑展示名称（服务端统一命名；非用户提交）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>本次执行的关联 ID（受控追踪，绝不泄露 SQL / 栈 / 私有值）</summary>
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>有序节导出结果（1 ~ 8 节）</summary>
    public List<ReportConfigurationBundleSectionExportDto> Sections { get; set; } = new();

    /// <summary>节数</summary>
    public int SectionCount { get; set; }

    /// <summary>跨节合计导出事实行数（有界）</summary>
    public int TotalRowCount { get; set; }
}

// ==================== ERP-316 Stage 2：有界表头 / 明细组合契约 ====================

/// <summary>
/// 表头 / 明细组合（ERP-316 Stage 2）的平台常量：有界父项 / 子项 / 单元格上限，客户端不可递增。
/// <para>组合只引用服务端声明场景（<see cref="ReportConfigurationBundleCompositionScenario"/>）与既有私有 / 共享定义，
/// 绝不接受客户端自选联接、父身份字段、明细外键字段或隐藏字段查询。</para>
/// </summary>
public static class ReportConfigurationBundleCompositionLimits
{
    /// <summary>组合最多父项（表头）数</summary>
    public const int MaxParents = 50;

    /// <summary>组合最多明细行总数（跨父项有界合计）</summary>
    public const int MaxTotalChildren = 100;

    /// <summary>组合单父项最多明细行数</summary>
    public const int MaxChildrenPerParent = 100;

    /// <summary>组合预览跨父项合计单元格数上限（表头单元格 + 明细单元格）</summary>
    public const int MaxCells = 5000;

    /// <summary>组合预览序列化体积上限（8 MiB，兜底）</summary>
    public const int MaxSerializedCompositionPreviewBytes = 8 * 1024 * 1024;
}

/// <summary>
/// 服务端声明的表头 / 明细组合场景（ERP-316 Stage 2）：有限、不可变、顺序稳定。
/// <para>每个场景显式登记稳定父身份字段、精确明细外键字段、明细排序字段，以及用于独立合计的
/// 表头金额 / 币种与明细金额 / 币种 / 数量 / 单位字段键；这些键都来自受控数据集白名单，
/// 客户端只能引用场景键，绝不提交任意联接或字段键。</para>
/// </summary>
public sealed class ReportConfigurationBundleCompositionScenario
{
    public ReportConfigurationBundleCompositionScenario(
        string key,
        string name,
        string headerDatasetKey,
        string detailDatasetKey,
        string parentIdentityFieldKey,
        string detailForeignKeyFieldKey,
        string? detailOrderFieldKey,
        string headerAmountFieldKey,
        string headerCurrencyFieldKey,
        string detailAmountFieldKey,
        string detailCurrencyFieldKey,
        string detailQuantityFieldKey,
        string detailUnitFieldKey,
        IReadOnlyList<string> requiredMenuCodes,
        string currencyUnitSemantics,
        string readOnlyText,
        string boundaryText,
        string disclaimerText)
    {
        Key = key;
        Name = name;
        HeaderDatasetKey = headerDatasetKey;
        DetailDatasetKey = detailDatasetKey;
        ParentIdentityFieldKey = parentIdentityFieldKey;
        DetailForeignKeyFieldKey = detailForeignKeyFieldKey;
        DetailOrderFieldKey = detailOrderFieldKey;
        HeaderAmountFieldKey = headerAmountFieldKey;
        HeaderCurrencyFieldKey = headerCurrencyFieldKey;
        DetailAmountFieldKey = detailAmountFieldKey;
        DetailCurrencyFieldKey = detailCurrencyFieldKey;
        DetailQuantityFieldKey = detailQuantityFieldKey;
        DetailUnitFieldKey = detailUnitFieldKey;
        RequiredMenuCodes = requiredMenuCodes;
        CurrencyUnitSemantics = currencyUnitSemantics;
        ReadOnlyText = readOnlyText;
        BoundaryText = boundaryText;
        DisclaimerText = disclaimerText;
    }

    /// <summary>场景键（稳定；客户端只引用该键）</summary>
    public string Key { get; }

    /// <summary>场景展示名称</summary>
    public string Name { get; }

    /// <summary>表头节数据集键（受控）</summary>
    public string HeaderDatasetKey { get; }

    /// <summary>明细节数据集键（受控）</summary>
    public string DetailDatasetKey { get; }

    /// <summary>表头稳定父身份字段键（服务端登记，客户端不可改）</summary>
    public string ParentIdentityFieldKey { get; }

    /// <summary>明细精确外键字段键（服务端登记，客户端不可改）</summary>
    public string DetailForeignKeyFieldKey { get; }

    /// <summary>明细排序字段键（可选；空 = 保持数据集稳定顺序）</summary>
    public string? DetailOrderFieldKey { get; }

    /// <summary>表头金额字段键（独立于明细合计，绝不摊入明细）</summary>
    public string HeaderAmountFieldKey { get; }

    /// <summary>表头币种字段键</summary>
    public string HeaderCurrencyFieldKey { get; }

    /// <summary>明细金额字段键（按币种分区合计）</summary>
    public string DetailAmountFieldKey { get; }

    /// <summary>明细币种字段键</summary>
    public string DetailCurrencyFieldKey { get; }

    /// <summary>明细数量字段键（按单位分区合计）</summary>
    public string DetailQuantityFieldKey { get; }

    /// <summary>明细单位字段键</summary>
    public string DetailUnitFieldKey { get; }

    /// <summary>场景所需既有菜单编码（组合前独立校验，缺失 fail closed）</summary>
    public IReadOnlyList<string> RequiredMenuCodes { get; }

    /// <summary>币种 / 单位口径文案</summary>
    public string CurrencyUnitSemantics { get; }

    /// <summary>只读声明</summary>
    public string ReadOnlyText { get; }

    /// <summary>边界口径</summary>
    public string BoundaryText { get; }

    /// <summary>免责声明</summary>
    public string DisclaimerText { get; }
}


/// <summary>
/// 表头 / 明细组合场景清单（ERP-316 Stage 2）：编译期有限、不可变。目前登记出口单证中心
/// 场景（复用 ERP-306 的权威打印快照表头 / 明细行，不新建独立报表）。
/// </summary>
public static class ReportConfigurationBundleCompositionManifest
{
    /// <summary>出口单证中心（表头 + 明细）组合场景键</summary>
    public const string TradeDocumentHeaderDetail = "trade-document-header-detail";
    public const string QuotationHeaderDetail = "quotation-header-detail";
    public const string ProformaInvoiceHeaderDetail = "proforma-invoice-header-detail";

    public static readonly IReadOnlyList<ReportConfigurationBundleCompositionScenario> Scenarios =
        new ReportConfigurationBundleCompositionScenario[]
        {
            new(
                TradeDocumentHeaderDetail,
                "出口单证中心（表头 + 明细）",
                ReportConfigurationConstants.DatasetTradeDocument,
                ReportConfigurationConstants.DatasetTradeDocument,
                "id",
                "id",
                "lineNo",
                "amount",
                "currency",
                "lineAmount",
                "lineCurrency",
                "quantity",
                "unit",
                new[] { "doc-center" },
                "金额按原币呈现；数量按基础单位；不跨币种换算或合并",
                "只读出口单证中心组合：仅读取单证台账持久表头与明细行快照，不新增 / 修改 / 删除任何记录，不重建缺失证据",
                "口径：表头按单证一次呈现，明细行按行序置于对应单证之下；表头金额只在表头粒度出现，明细金额按原币分区、数量按基础单位分区，绝不把表头金额摊入明细或跨币种合并；无明细行的单证明确标注为空明细，不臆造合计",
                "本组合为只读单证中心证据：金额按原币、数量按基础单位呈现，不跨币种换算或合并，不构成报关、清关、退税或财务结论"),
            SalesDocument(QuotationHeaderDetail, "quotation"),
            SalesDocument(ProformaInvoiceHeaderDetail, "proforma-invoice"),
        };

    private static ReportConfigurationBundleCompositionScenario SalesDocument(string key, string familyKey)
    {
        var family = ReportConfigurationSalesDocumentCatalog.Resolve(familyKey);
        return new(key, family.Title + "（表头 + 明细）", family.DatasetKey, family.DatasetKey,
            "id", "id", "sortNo", "totalAmount", "currency", "amount", "currency", "quantity", "unit",
            family.RequiredMenuCodes, ReportConfigurationSalesDocumentCatalog.CurrencyUnitSemantics,
            "只读既有销售单据持久表头与有效明细，不修改业务记录",
            "表头总额独立呈现一次；明细按单据 Id 关联、原行序排列，原币金额与原单位数量分区；无明细保留空证据；超过有界完整匹配范围拒绝组合",
            "原币原单位只读证据，不跨币种或单位混加，不构成财务结论");
    }

    /// <summary>按场景键查找（忽略大小写；未知返回 null，fail closed）。</summary>
    public static ReportConfigurationBundleCompositionScenario? Find(string? key)
        => Scenarios.FirstOrDefault(s =>
            string.Equals(s.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 表头 / 明细组合请求（ERP-316 Stage 2）：只引用服务端声明场景键 + 既有表头 / 明细私有定义，
/// 绝不携带字段键 / 联接 / SQL。所有者身份由服务端认证注入。
/// </summary>
public sealed class ReportConfigurationBundleCompositionRequest
{
    /// <summary>服务端声明的组合场景键（如 trade-document-header-detail）</summary>
    public string CompositionKey { get; set; } = string.Empty;

    /// <summary>表头节既有私有 / 共享报表配置 Id</summary>
    public long HeaderConfigurationId { get; set; }

    /// <summary>表头节固定发布修订版本号（可选；空 = 当前草稿）</summary>
    public int? HeaderRevisionVersion { get; set; }

    /// <summary>明细节既有私有 / 共享报表配置 Id</summary>
    public long DetailConfigurationId { get; set; }

    /// <summary>明细节固定发布修订版本号（可选；空 = 当前草稿）</summary>
    public int? DetailRevisionVersion { get; set; }

    /// <summary>表头节展示标题（可选）</summary>
    public string? HeaderTitle { get; set; }

    /// <summary>明细节展示标题（可选）</summary>
    public string? DetailTitle { get; set; }
}


/// <summary>组合明细金额分区（按币种，原币；绝不跨币种合并）</summary>
public sealed class ReportConfigurationBundleComposedAmountDto
{
    /// <summary>币种（空 = 未知币种，单独隔离）</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>该币种明细金额合计</summary>
    public decimal Amount { get; set; }

    /// <summary>参与合计的明细行数</summary>
    public int Count { get; set; }

    /// <summary>显式原因（如未知币种）；无异常为 null</summary>
    public string? Reason { get; set; }
}

/// <summary>组合明细数量分区（按单位；绝不跨单位合并）</summary>
public sealed class ReportConfigurationBundleComposedQuantityDto
{
    /// <summary>单位（空 = 未知单位，单独隔离）</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>该单位明细数量合计</summary>
    public decimal Quantity { get; set; }

    /// <summary>参与合计的明细行数</summary>
    public int Count { get; set; }

    /// <summary>显式原因（如未知单位）；无异常为 null</summary>
    public string? Reason { get; set; }
}

/// <summary>单个父项的组合合计：表头金额独立保留，绝不与明细合计相乘 / 相加。</summary>
public sealed class ReportConfigurationBundleComposedTotalsDto
{
    /// <summary>表头金额（原样保留，绝不按明细行数翻倍）</summary>
    public decimal? HeaderAmount { get; set; }

    /// <summary>表头币种</summary>
    public string? HeaderCurrency { get; set; }

    /// <summary>明细金额按币种分区</summary>
    public List<ReportConfigurationBundleComposedAmountDto> DetailAmounts { get; set; } = new();

    /// <summary>明细数量按单位分区</summary>
    public List<ReportConfigurationBundleComposedQuantityDto> DetailQuantities { get; set; } = new();
}

/// <summary>组合中的单个父项（表头一次呈现 + 其下有序明细）。</summary>
public sealed class ReportConfigurationBundleComposedHeaderDto
{
    /// <summary>父项序号（从 1 开始，稳定排序）</summary>
    public int Ordinal { get; set; }

    /// <summary>稳定父身份键（类型前缀编码，绝不混写不同身份的相同字符串）</summary>
    public string ParentKey { get; set; } = string.Empty;

    /// <summary>原始表头行（一次呈现，字段键与表头节列一致）</summary>
    public Dictionary<string, object?> Header { get; set; } = new();

    /// <summary>该父项之下有序明细行</summary>
    public List<Dictionary<string, object?>> Details { get; set; } = new();

    /// <summary>是否含有明细行（空明细为 false，明确标注而非失败）</summary>
    public bool HasDetails { get; set; }

    /// <summary>空明细时的显式证据文案</summary>
    public string? EmptyDetailsEvidence { get; set; }

    /// <summary>该父项的组合合计（表头金额独立 + 明细按币种 / 单位分区）</summary>
    public ReportConfigurationBundleComposedTotalsDto Totals { get; set; } = new();
}


/// <summary>表头 / 明细组合预览结果（ERP-316 Stage 2）：全部校验通过后才返回，绝不返回部分父项。</summary>
public sealed class ReportConfigurationBundleCompositionPreviewDto
{
    /// <summary>组合展示名称（服务端统一命名；非用户提交）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>本次执行的关联 ID（受控追踪，绝不泄露 SQL / 栈 / 私有值）</summary>
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>组合场景键</summary>
    public string CompositionKey { get; set; } = string.Empty;

    /// <summary>组合场景名称</summary>
    public string CompositionName { get; set; } = string.Empty;

    /// <summary>表头节展示标题</summary>
    public string HeaderTitle { get; set; } = string.Empty;

    /// <summary>明细节展示标题</summary>
    public string DetailTitle { get; set; } = string.Empty;

    /// <summary>表头节报表配置 Id</summary>
    public long HeaderConfigurationId { get; set; }

    /// <summary>表头节固定发布修订版本号（可选）</summary>
    public int? HeaderRevisionVersion { get; set; }

    /// <summary>明细节报表配置 Id</summary>
    public long DetailConfigurationId { get; set; }

    /// <summary>明细节固定发布修订版本号（可选）</summary>
    public int? DetailRevisionVersion { get; set; }

    /// <summary>表头节列（字段顺序稳定）</summary>
    public List<ReportConfigurationColumnDto> HeaderColumns { get; set; } = new();

    /// <summary>明细节列（字段顺序稳定）</summary>
    public List<ReportConfigurationColumnDto> DetailColumns { get; set; } = new();

    /// <summary>有序父项（每个父项表头一次 + 其下有序明细）</summary>
    public List<ReportConfigurationBundleComposedHeaderDto> Parents { get; set; } = new();

    /// <summary>父项数</summary>
    public int ParentCount { get; set; }

    /// <summary>明细行总数</summary>
    public int DetailCount { get; set; }

    /// <summary>单元格总数（表头 + 明细）</summary>
    public int CellCount { get; set; }

    /// <summary>币种 / 单位口径文案</summary>
    public string CurrencyUnitSemantics { get; set; } = string.Empty;

    /// <summary>只读声明</summary>
    public string ReadOnlyText { get; set; } = string.Empty;

    /// <summary>边界口径</summary>
    public string BoundaryText { get; set; } = string.Empty;

    /// <summary>免责声明</summary>
    public string DisclaimerText { get; set; } = string.Empty;
}

/// <summary>表头 / 明细组合导出结果（ERP-316 Stage 2）：包裹已组合、已校验的预览，供 Excel / PDF 渲染。</summary>
public sealed class ReportConfigurationBundleCompositionExportDto
{
    /// <summary>已组合、已校验的预览</summary>
    public ReportConfigurationBundleCompositionPreviewDto Preview { get; set; } = new();
}

