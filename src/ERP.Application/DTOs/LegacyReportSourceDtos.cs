namespace ERP.Application.DTOs;

/// <summary>
/// 旧报表来源统一接缝（ERP-330）的读取结果状态（有限、稳定）。
/// </summary>
public enum LegacyReportSourceStatus
{
    /// <summary>已解析并返回有界旧结果快照。</summary>
    Success = 0,

    /// <summary>当前账号菜单 / 数据范围授权不足（fail closed，绝不返回任何数据）。</summary>
    Forbidden = 1,

    /// <summary>来源在受控登记册中存在，但其既有读取服务在当前环境 / 阶段不可用（显式 environment-blocked，绝不回退为全表读取）。</summary>
    EnvironmentBlocked = 2,
}

/// <summary>
/// 旧报表来源统一读取请求（ERP-330，全部为受控参数）：
/// 只承载旧报表键、当前账号身份与有界读取参数，绝不承载任意 SQL / 表名 / 列名 / 联接语义。
/// </summary>
public sealed class LegacyReportSourceRequest
{
    /// <summary>旧报表键（必须命中受控登记册清单，空白 / 畸形 / 未知在打开任何查询前拒绝）。</summary>
    public string? LegacyKey { get; set; }

    /// <summary>当前账号 Id（未认证 / 非正整数直接拒绝）。</summary>
    public long? UserId { get; set; }

    /// <summary>日期区间起（含当日；仅对需要日期范围的旧报表生效）。</summary>
    public DateTime? Start { get; set; }

    /// <summary>日期区间止（含当日）。</summary>
    public DateTime? End { get; set; }

    /// <summary>截止日（as-of；资产负债表 / 应收账龄 / 跟进提醒等）。</summary>
    public DateTime? AsOfDate { get; set; }

    /// <summary>Top 条数（商品销量排名；有界 1 ~ 200）。</summary>
    public int Top { get; set; } = 200;

    /// <summary>提前天数（跟进提醒）。</summary>
    public int AheadDays { get; set; } = 7;

    /// <summary>页码（从 1 开始）。</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（有界）。</summary>
    public int PageSize { get; set; } = 200;

    /// <summary>关键字（旧单据导出 / 关键字族）。</summary>
    public string? Keyword { get; set; }

    /// <summary>客户 Id（客户报告包等按客户聚合的来源）。</summary>
    public long? CustomerId { get; set; }

    /// <summary>单证 Id（单证打印 / 导出族）。</summary>
    public long? DocumentId { get; set; }
}

/// <summary>
/// 旧报表来源统一读取结果（ERP-330）：有界旧结果快照或显式失败状态。
/// <para>输入错误（空白 / 畸形 / 未知键、未认证）由实现方以 <see cref="ERP.Application.Common.BusinessException"/> 精确拒绝，
/// 以便与既有目录 / 接缝一致；菜单 / 数据范围撤销与环境阻塞以本结果状态返回，绝不产出部分数据。</para>
/// </summary>
public sealed record LegacyReportSourceResult(
    string? LegacyKey,
    string Category,
    LegacyReportSourceStatus Status,
    int? ErrorCode,
    string? ErrorMessage,
    ReportMigrationParitySnapshotDto? Snapshot);
