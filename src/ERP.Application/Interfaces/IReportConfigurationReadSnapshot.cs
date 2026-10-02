using ERP.Application.DTOs;

namespace ERP.Application.Interfaces;

/// <summary>
/// 通用报表配置平台（ERP-273 Stage 1）的请求作用域有界一致只读快照抽象：
/// 一次执行内捕获「已按当前账号重新校验菜单 / 字段 / 客户数据范围并归一化谓词」后的有界匹配事实，
/// 并在同一只读事务内批量装载身份键与证据行。快照绝不跨请求 / 用户持久化或缓存，
/// 绝不暴露隐藏依赖；事务通过 <see cref="CompleteAsync"/> 完成、通过 <see cref="IAsyncDisposable"/> 释放。
/// </summary>
public interface IReportConfigurationReadSnapshot : IAsyncDisposable
{
    /// <summary>本次执行的关联 ID（受控追踪，绝不泄露 SQL / 栈 / 私有值）。</summary>
    string CorrelationId { get; }

    /// <summary>数据集键。</summary>
    string DatasetKey { get; }

    /// <summary>覆盖口径（matched-set）。</summary>
    string Coverage { get; }

    /// <summary>有界匹配事实命中条数（≤ <c>MaxSnapshotFacts</c>）。</summary>
    int MatchedCount { get; }

    /// <summary>内部证据字节估算（≤ <c>MaxSnapshotBytes</c>）。</summary>
    long EstimatedBytes { get; }

    /// <summary>是否已建立一致只读事务（Serializable）；false 表示环境无法提供一致性。</summary>
    bool IsConsistent { get; }

    /// <summary>稳定源排序后的有界身份键（事实身份，不重复）。</summary>
    IReadOnlyList<long> FactIds { get; }

    /// <summary>稳定源排序后的有界事实证据行（与 <see cref="FactIds"/> 平行，保持源粒度 / 顺序 / null 语义）。</summary>
    IReadOnlyList<Dictionary<string, object?>> Rows { get; }

    /// <summary>有界类型化列（选定字段 + 必要授权依赖；隐藏依赖在渲染层剥离）。</summary>
    IReadOnlyList<ReportConfigurationColumnDto> Columns { get; }

    /// <summary>快照证据上下文（数据集 / 粒度 / 币种单位口径 / 只读 / 覆盖口径）。</summary>
    ReportConfigurationEvidenceContextDto Evidence { get; }

    /// <summary>本次读取时的数据范围指纹（用于最终新鲜范围复核：变更即拒绝整个响应）。</summary>
    string ScopeFingerprint { get; }

    /// <summary>完成只读事务（在最终新鲜授权复核前调用；幂等）。</summary>
    Task CompleteAsync(CancellationToken cancellationToken = default);
}
