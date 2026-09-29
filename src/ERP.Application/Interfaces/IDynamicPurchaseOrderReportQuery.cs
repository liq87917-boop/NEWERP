using ERP.Application.DTOs;

namespace ERP.Application.Interfaces;

/// <summary>
/// 动态采购订单报表（ERP-125）查询接缝：字段目录与有界只读预览。
/// <para>由基础设施层实现（<c>ERP.Infrastructure.Reports.DynamicPurchaseOrderReportQuery</c>），
/// 应用层只依赖该只读查询契约，不依赖具体数据访问实现。</para>
/// </summary>
public interface IDynamicPurchaseOrderReportQuery
{
    /// <summary>
    /// 返回采购订单报表的有限白名单字段目录（需登录并具备采购订单菜单授权，fail closed）。
    /// </summary>
    Task<DynamicPurchaseOrderReportCatalogDto> GetCatalogAsync(long? userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按选定字段与有界筛选（供应商 / 订单日期 / 状态 / 币种）预览当前账号可见（未删除）的采购订单，
    /// 稳定分页（单页上限 100）、只读不写库；无身份 / 无菜单授权 / 无效字段与筛选时 fail closed。
    /// </summary>
    Task<DynamicPurchaseOrderReportPageDto> PreviewAsync(
        DynamicPurchaseOrderReportRequest request, long? userId, CancellationToken cancellationToken = default);
}
