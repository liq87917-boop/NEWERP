using ERP.Application.DTOs;
using ERP.Application.Services;

namespace ERP.Application.Interfaces;

/// <summary>
/// 旧单据读侧（<c>api/v2/bills</c> 的查询 / 翻页导航 / 详情）的受控只读接缝（ERP-405）。
/// <para>实现方必须在打开任何查询之前拒绝未知 / 畸形族标识与非法分页，并把调用方传入的
/// <see cref="SalespersonDataScope"/> 施加到计数 / 分页 / 导航 / 表头读取（受限但无权威归属的族 fail closed），
/// 只使用受控常量标识符（表名 / 列名 / 外键）与参数化值，绝不做 <c>SELECT *</c> / 模型 SQL / 任意 SQL / 联接。</para>
/// <para>实现方只读，绝不写库、不调用任何存储过程、不写日志 / 通知；缺表 / 缺列映射为显式 environment-blocked，
/// 绝不回退到规范（EF 复数）表。</para>
/// </summary>
public interface ILegacyBillReadService
{
    /// <summary>读取指定旧单据族的一页数据（范围受限、有界、参数化）。</summary>
    Task<LegacyBillReadPage> ReadPageAsync(
        string familyKey,
        LegacyBillReadQuery query,
        SalespersonDataScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取指定旧单据族的详情（表头 + 关联副表明细）；表头越权 / 不存在返回 <c>null</c>（不区分，避免泄露）。
    /// </summary>
    Task<LegacyBillReadDetail?> ReadDetailAsync(
        string familyKey,
        long oid,
        SalespersonDataScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 翻页导航（<c>first</c> / <c>last</c> / <c>prev</c> / <c>next</c>，大小写不敏感）；
    /// <c>prev</c> / <c>next</c> 会先在同范围内核验锚点，不可访问锚点与「没有更多」返回同一结果。
    /// </summary>
    Task<LegacyBillNavigateResult> NavigateAsync(
        string familyKey,
        long oid,
        string? direction,
        SalespersonDataScope scope,
        CancellationToken cancellationToken = default);
}
