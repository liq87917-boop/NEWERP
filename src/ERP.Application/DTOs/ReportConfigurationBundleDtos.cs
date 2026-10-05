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
