namespace ERP.Application.Interfaces;

/// <summary>
/// 通用报表配置平台（ERP-335 Stage 2）的表头 / 明细组合一致读取目标：既有私有或共享报表配置引用。
/// <para>只携带配置 Id / 固定修订与经执行服务确认的数据集键，绝不携带定义正文 / SQL / 字段键 / 联接语义；
/// 所有者身份由服务端认证注入。</para>
/// </summary>
public sealed record ReportConfigurationCompositionReadTarget(
    long ConfigurationId,
    int? RevisionVersion,
    string DatasetKey);

/// <summary>
/// 通用报表配置平台（ERP-335 Stage 2）的表头 / 明细组合一致读取作用域。
/// <para>作用域把「两次有限已注册读取 + 最终授权复核」包在同一个受支持的一致只读事务内，
/// 通过 <see cref="IReportConfigurationCompositionReadScopeFactory"/> 在既有作用域 DbContext 上获取；
/// 事务通过 <see cref="CompleteAsync"/> 完成、通过 <see cref="IAsyncDisposable"/> 释放（幂等、确定性）。</para>
/// </summary>
public interface IReportConfigurationCompositionReadScope : IAsyncDisposable
{
    /// <summary>本次执行的关联 ID（受控追踪，绝不泄露 SQL / 栈 / 私有值）。</summary>
    string CorrelationId { get; }

    /// <summary>是否已建立一致只读事务；false 表示环境无法提供一致性（fail closed 场景不返回 false）。</summary>
    bool IsConsistent { get; }

    /// <summary>
    /// 在两次读取完成后、提交事务前做最终新鲜授权复核（菜单 / 数据范围 / 归属或共享授权，fail closed）。
    /// <para>任何一节被撤销 / 失效即拒绝整个组合，绝不返回部分数据。</para>
    /// </summary>
    Task RecheckAsync(
        long ownerUserId,
        IReadOnlyList<ReportConfigurationCompositionReadTarget> targets,
        CancellationToken cancellationToken = default);

    /// <summary>完成只读事务（幂等；在最终授权复核通过后调用）。</summary>
    Task CompleteAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 通用报表配置平台（ERP-335 Stage 2）的表头 / 明细组合一致读取作用域工厂。
/// <para>在既有作用域 DbContext 上获取受支持的一致只读事务；后端 / 隔离级别不支持时显式失败（environment-blocked），
/// 绝不静默回退为无保护的多读。</para>
/// </summary>
public interface IReportConfigurationCompositionReadScopeFactory
{
    /// <summary>打开组合一致读取作用域（使用既有租约 / 取消 / 边界与确定性释放）。</summary>
    Task<IReportConfigurationCompositionReadScope> OpenAsync(
        long ownerUserId,
        string correlationId,
        CancellationToken cancellationToken = default);
}
