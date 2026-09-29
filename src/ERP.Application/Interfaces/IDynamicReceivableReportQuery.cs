using ERP.Application.DTOs;

namespace ERP.Application.Interfaces;

/// <summary>
/// 动态客户应收账款证据报表（ERP-117）查询接缝：字段目录与有界只读预览。
/// <para>由基础设施层实现（<c>ERP.Infrastructure.Reports.DynamicReceivableReportQuery</c>），
/// 应用层只依赖该只读查询契约，不依赖具体数据访问实现。</para>
/// </summary>
public interface IDynamicReceivableReportQuery
{
    /// <summary>
    /// 返回应收账款证据报表的有限白名单字段目录（需登录并具备「客户资料」菜单授权，fail closed）。
    /// </summary>
    Task<DynamicReceivableReportCatalogDto> GetCatalogAsync(long? userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按选定字段与有界筛选（客户 / 发票日期 / 币种 / 分配状态）预览当前账号数据范围内的客户销项发票证据，
    /// 稳定分页（单页上限 100）、只读不写库；无身份 / 无菜单授权 / 无效字段与筛选 / 页大小超限时 fail closed。
    /// </summary>
    Task<DynamicReceivableReportPageDto> PreviewAsync(
        DynamicReceivableReportRequest request, long? userId, CancellationToken cancellationToken = default);
}
