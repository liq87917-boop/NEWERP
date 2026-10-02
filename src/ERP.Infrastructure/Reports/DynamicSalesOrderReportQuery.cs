using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 动态销售订单报表（ERP-112）查询实现（只读、有界）：复用既有「角色 → 菜单」模块授权（<c>sales-order</c>）
/// 与 <see cref="SalespersonDataScopeService"/>（ERP-097）数据范围，先校验（身份 / 菜单 / 字段 / 筛选 / 页大小）
/// 再按客户范围过滤并稳定分页读取；全程 <c>AsNoTracking</c>，不调用 <c>SaveChangesAsync</c>，不执行任意 SQL。
/// <para>审计：请求由既有 <c>OperationLogMiddleware</c> 按 HTTP 方法记录，本查询自身不写任何操作日志。</para>
/// </summary>
public sealed class DynamicSalesOrderReportQuery : IDynamicSalesOrderReportQuery
{
    private readonly IErpDbContext _db;

    public DynamicSalesOrderReportQuery(IErpDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<DynamicSalesOrderReportCatalogDto> GetCatalogAsync(
        long? userId, CancellationToken cancellationToken = default)
    {
        await EnsureAuthorizedAsync(userId, cancellationToken);
        return DynamicSalesOrderReportRules.GetCatalogDto();
    }

    /// <inheritdoc />
    public async Task<DynamicSalesOrderReportPageDto> PreviewAsync(
        DynamicSalesOrderReportRequest request, long? userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1) 身份 + 既有销售订单菜单授权（无身份 / 无角色 / 无菜单授权 → fail closed）
        await EnsureAuthorizedAsync(userId, cancellationToken);

        // 2) 字段 / 筛选 / 页大小 / 排序校验（全部在读取销售订单之前完成）
        var fieldKeys = DynamicSalesOrderReportRules.NormalizeFields(request.Fields);
        var status = DynamicSalesOrderReportRules.NormalizeStatus(request.Status);
        var currency = DynamicSalesOrderReportRules.NormalizeCurrency(request.Currency);
        DynamicSalesOrderReportRules.ValidateDateRange(request.StartDate, request.EndDate);
        DynamicSalesOrderReportRules.ValidatePageSize(request.PageSize);
        if (request.Page < 1) request.Page = 1;
        var sort = DynamicSalesOrderReportRules.NormalizeSort(request.SortFieldKey, request.SortDirection);
        var offset = CheckedPageOffset(request.Page, request.PageSize);

        // 3) 解析当前账号业务员数据范围（特权账号不过滤）
        var scope = await SalespersonDataScopeService.ResolveAsync(_db, userId);

        // 4) 客户范围硬边界 → 显式筛选（订单日期 / 客户 / 状态 / 币种）
        var source = SalespersonDataScopeService.FilterByCustomer(
            _db.SalesOrders.AsNoTracking().Where(o => !o.IsDeleted), scope, o => o.CustomerId);

        if (request.StartDate.HasValue) source = source.Where(o => o.OrderDate >= request.StartDate.Value.Date);
        if (request.EndDate.HasValue) source = source.Where(o => o.OrderDate <= request.EndDate.Value.Date);
        if (request.CustomerId.HasValue) source = source.Where(o => o.CustomerId == request.CustomerId.Value);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (currency.HasValue) source = source.Where(o => o.Currency == currency.Value);

        // 5) 有限类型化源侧排序（先于 Skip/Take）+ 不可变身份并列决断 + 稳定分页
        var total = await source.CountAsync(cancellationToken);
        var pageOrders = await ApplySort(source, sort)
            .Skip(offset)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        var columns = fieldKeys.Select(key => DynamicSalesOrderReportRules.GetField(key)!).ToList();
        var rows = pageOrders.Select(o => DynamicSalesOrderReportRules.BuildRow(o, fieldKeys)).ToList();

        return new DynamicSalesOrderReportPageDto(
            columns,
            rows,
            total,
            request.Page,
            request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize),
            DynamicSalesOrderReportRules.ReadOnlyText,
            DynamicSalesOrderReportRules.BoundaryText,
            DynamicSalesOrderReportRules.DisclaimerText);
    }

    /// <summary>有限类型化源侧排序：仅 id / orderDate / customerId，附带不可变身份并列决断（Id 升序）。</summary>
    private static IQueryable<SalesOrder> ApplySort(IQueryable<SalesOrder> source, (string FieldKey, bool Descending) sort)
    {
        switch (sort.FieldKey)
        {
            case "orderDate":
                return sort.Descending
                    ? source.OrderByDescending(o => o.OrderDate).ThenBy(o => o.Id)
                    : source.OrderBy(o => o.OrderDate).ThenBy(o => o.Id);
            case "customerId":
                return sort.Descending
                    ? source.OrderByDescending(o => o.CustomerId).ThenBy(o => o.Id)
                    : source.OrderBy(o => o.CustomerId).ThenBy(o => o.Id);
            default:
                return sort.Descending
                    ? source.OrderByDescending(o => o.Id)
                    : source.OrderBy(o => o.Id);
        }
    }

    /// <summary>安全分页偏移（拒绝算术溢出，绝不回绕负数 / 小偏移）。</summary>
    private static int CheckedPageOffset(int page, int pageSize)
    {
        var offset = (long)(page - 1) * pageSize;
        if (offset > int.MaxValue)
            throw BusinessException.InvalidParameter("分页偏移超出安全范围");
        return (int)offset;
    }

    /// <summary>身份 + 既有「角色 → 菜单」销售订单模块授权（fail closed，绝不猜测身份）</summary>
    private async Task EnsureAuthorizedAsync(long? userId, CancellationToken cancellationToken)
    {
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再预览销售订单报表", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, userId.Value);
        if (!menuCodes.Contains(DynamicSalesOrderReportRules.RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{DynamicSalesOrderReportRules.RequiredMenuText}」"
                + $"（{DynamicSalesOrderReportRules.RequiredMenuCode}）模块授权：拒绝预览销售订单报表"
                + "（fail closed，不返回任何数据）",
                ErrorCodes.Forbidden);
        }
    }
}
