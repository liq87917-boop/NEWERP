namespace ERP.Application.Interfaces;

/// <summary>
/// 迁移 parity 三方一致读取目标（ERP-338 Stage 2）：受控旧报表键 + 目标数据集键 +
/// 重新校验授权所需的既有菜单编码。只承载归一化的受控身份与有限边界，绝不承载
/// 任意 SQL / 表名 / 列名 / 联接语义；当前账号身份由服务端认证注入。
/// </summary>
public sealed record ReportMigrationParityReadTarget(
    string LegacyKey,
    string DatasetKey,
    IReadOnlyList<string> RequiredMenuCodes);

/// <summary>
/// 迁移 parity 三方一致读取作用域（ERP-338 Stage 2）：把「旧来源快照 + 通用预览 +
/// 实际旧产物」三次有限已注册读取与最终授权复核包在同一个受支持的一致只读事务内，
/// 通过 <see cref="IReportMigrationParityReadScopeFactory"/> 在既有作用域 DbContext 上获取。
/// <para>事务通过 <see cref="CompleteAsync"/> 完成、通过 <see cref="IAsyncDisposable"/> 释放（幂等、确定性）；
/// 绝不扩大业务写入权限、绝不新增表 / 列、绝不执行任意 SQL / 联接。</para>
/// </summary>
public interface IReportMigrationParityReadScope : IAsyncDisposable
{
    /// <summary>本次执行的关联 ID（受控追踪，绝不泄露 SQL / 栈 / 私有值）。</summary>
    string CorrelationId { get; }

    /// <summary>是否已建立一致只读事务；false 表示环境无法提供一致性（fail closed 场景不返回 false）。</summary>
    bool IsConsistent { get; }

    /// <summary>
    /// 在三次读取完成后、提交事务前做最终新鲜授权复核（菜单 / 数据集 / 数据范围，fail closed）。
    /// <para>任何一节被撤销 / 失效即拒绝整个证据，绝不返回部分数据、绝不沿用旧缓存。</para>
    /// </summary>
    Task RecheckAsync(
        long ownerUserId,
        ReportMigrationParityReadTarget target,
        CancellationToken cancellationToken = default);

    /// <summary>完成只读事务（幂等；在最终授权复核通过后调用）。</summary>
    Task CompleteAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 迁移 parity 三方一致读取作用域工厂（ERP-338 Stage 2）。
/// <para>在既有作用域 DbContext 上获取受支持的一致只读事务；后端 / 隔离级别不支持或存在
/// 嵌套事务时显式失败（environment-blocked），绝不静默回退为无保护的三次独立读取。</para>
/// </summary>
public interface IReportMigrationParityReadScopeFactory
{
    /// <summary>打开三方一致读取作用域（使用既有租约 / 取消 / 边界与确定性释放）。</summary>
    Task<IReportMigrationParityReadScope> OpenAsync(
        long ownerUserId,
        string correlationId,
        CancellationToken cancellationToken = default);
}
