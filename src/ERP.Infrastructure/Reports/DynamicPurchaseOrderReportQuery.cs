using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 动态采购订单报表（ERP-125）查询实现（只读、有界）：复用既有「角色 → 菜单」模块授权（<c>purchase-order</c>）
/// 与采购订单现有可见性（仅未删除，同 <c>PurchaseOrderController.GetPaged</c> 口径，不引入更宽的角色或数据范围策略），
/// 先校验（身份 / 菜单 / 字段 / 筛选 / 页大小）再按供应商 / 日期 / 状态 / 币种过滤并稳定分页读取；
/// 全程 <c>AsNoTracking</c>，不调用 <c>SaveChangesAsync</c>，不执行任意 SQL。
/// <para>审计：请求由既有 <c>OperationLogMiddleware</c> 按 HTTP 方法记录，本查询自身不写任何操作日志。</para>
/// </summary>
public sealed class DynamicPurchaseOrderReportQuery : IDynamicPurchaseOrderReportQuery
{
    private readonly IErpDbContext _db;

    public DynamicPurchaseOrderReportQuery(IErpDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<DynamicPurchaseOrderReportCatalogDto> GetCatalogAsync(
        long? userId, CancellationToken cancellationToken = default)
    {
        await EnsureAuthorizedAsync(userId, cancellationToken);
        return DynamicPurchaseOrderReportRules.GetCatalogDto();
    }

    /// <inheritdoc />
    public async Task<DynamicPurchaseOrderReportPageDto> PreviewAsync(
        DynamicPurchaseOrderReportRequest request, long? userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1) 身份 + 既有采购订单菜单授权（无身份 / 无角色 / 无菜单授权 → fail closed）
        await EnsureAuthorizedAsync(userId, cancellationToken);

        // 2) 字段 / 筛选 / 页大小校验（全部在读取采购订单之前完成）
        var fieldKeys = DynamicPurchaseOrderReportRules.NormalizeFields(request.Fields);
        var status = DynamicPurchaseOrderReportRules.NormalizeStatus(request.Status);
        var currency = DynamicPurchaseOrderReportRules.NormalizeCurrency(request.Currency);
        DynamicPurchaseOrderReportRules.ValidateDateRange(request.StartDate, request.EndDate);
        DynamicPurchaseOrderReportRules.ValidatePageSize(request.PageSize);
        if (request.Page < 1) request.Page = 1;

        // 3) 现有采购订单可见性 = 未删除（同既有采购订单列表口径，不引入更宽的数据范围策略）
        var source = _db.PurchaseOrders.AsNoTracking().Where(o => !o.IsDeleted);

        // 4) 有界筛选（供应商 / 订单日期 / 状态 / 币种）
        if (request.SupplierId.HasValue) source = source.Where(o => o.SupplierId == request.SupplierId.Value);
        if (request.StartDate.HasValue) source = source.Where(o => o.OrderDate >= request.StartDate.Value.Date);
        if (request.EndDate.HasValue) source = source.Where(o => o.OrderDate <= request.EndDate.Value.Date);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (currency.HasValue) source = source.Where(o => o.Currency == currency.Value);

        // 5) 稳定分页（按 Id 升序）与只读行映射
        var total = await source.CountAsync(cancellationToken);
        var pageOrders = await source
            .OrderBy(o => o.Id)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        var columns = fieldKeys.Select(key => DynamicPurchaseOrderReportRules.GetField(key)!).ToList();
        var rows = pageOrders.Select(o => DynamicPurchaseOrderReportRules.BuildRow(o, fieldKeys)).ToList();

        return new DynamicPurchaseOrderReportPageDto(
            columns,
            rows,
            total,
            request.Page,
            request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize),
            DynamicPurchaseOrderReportRules.ReadOnlyText,
            DynamicPurchaseOrderReportRules.BoundaryText,
            DynamicPurchaseOrderReportRules.DisclaimerText);
    }

    /// <summary>身份 + 既有「角色 → 菜单」采购订单模块授权（fail closed，绝不猜测身份）</summary>
    private async Task EnsureAuthorizedAsync(long? userId, CancellationToken cancellationToken)
    {
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再预览采购订单报表", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, userId.Value);
        if (!menuCodes.Contains(DynamicPurchaseOrderReportRules.RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{DynamicPurchaseOrderReportRules.RequiredMenuText}」"
                + $"（{DynamicPurchaseOrderReportRules.RequiredMenuCode}）模块授权：拒绝预览采购订单报表"
                + "（fail closed，不返回任何数据）",
                ErrorCodes.Forbidden);
        }
    }
}
