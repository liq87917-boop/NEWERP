using ERP.Application.DTOs;

namespace ERP.Application.Interfaces;

/// <summary>
/// 动态销售订单报表（ERP-112）查询接缝：字段目录与有界只读预览。
/// <para>由基础设施层实现（<c>ERP.Infrastructure.Reports.DynamicSalesOrderReportQuery</c>），
/// 应用层只依赖该只读查询契约，不依赖具体数据访问实现。</para>
/// </summary>
public interface IDynamicSalesOrderReportQuery
{
    /// <summary>
    /// 返回销售订单报表的有限白名单字段目录（需登录并具备销售订单菜单授权，fail closed）。
    /// </summary>
    Task<DynamicSalesOrderReportCatalogDto> GetCatalogAsync(long? userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按选定字段与有界筛选（订单日期 / 客户 / 状态 / 币种）预览当前账号数据范围内的销售订单，
    /// 稳定分页（单页上限 200）、只读不写库；无身份 / 无菜单授权 / 无效字段与筛选时 fail closed。
    /// </summary>
    Task<DynamicSalesOrderReportPageDto> PreviewAsync(
        DynamicSalesOrderReportRequest request, long? userId, CancellationToken cancellationToken = default);
}
