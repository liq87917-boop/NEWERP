using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 动态客户应收账款证据报表（ERP-117）查询实现（只读、有界）：复用既有「角色 → 菜单」模块授权（客户资料菜单）
/// 与 <see cref="SalespersonDataScopeService"/>（ERP-097）数据范围，先校验（身份 / 菜单 / 字段 / 筛选 / 页大小）
/// 再按客户范围过滤并稳定分页读取；派生证据复用 <see cref="CustomerReceivableReconciliationService"/>（ERP-074）。
/// <para>全程只读：不调用 <c>SaveChangesAsync</c>，不执行任意 SQL。审计：请求由既有 <c>OperationLogMiddleware</c> 按 HTTP 方法记录。</para>
/// </summary>
public sealed class DynamicReceivableReportQuery : IDynamicReceivableReportQuery
{
    private readonly IErpDbContext _db;

    public DynamicReceivableReportQuery(IErpDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<DynamicReceivableReportCatalogDto> GetCatalogAsync(
        long? userId, CancellationToken cancellationToken = default)
    {
        await EnsureAuthorizedAsync(userId, cancellationToken);
        return DynamicReceivableReportRules.GetCatalogDto();
    }

    /// <inheritdoc />
    public async Task<DynamicReceivableReportPageDto> PreviewAsync(
        DynamicReceivableReportRequest request, long? userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1) 身份 + 既有「客户资料」菜单授权（无身份 / 无角色 / 无菜单授权 → fail closed）
        await EnsureAuthorizedAsync(userId, cancellationToken);

        // 2) 字段 / 筛选 / 页大小 / 排序校验（全部在读取发票 / 分摊证据之前完成）
        var fieldKeys = DynamicReceivableReportRules.NormalizeFields(request.Fields);
        DynamicReceivableReportRules.ValidateCustomerId(request.CustomerId);
        var currency = DynamicReceivableReportRules.NormalizeCurrency(request.Currency);
        var allocationState = DynamicReceivableReportRules.NormalizeAllocationState(request.AllocationState);
        var invoiceStatus = DynamicReceivableReportRules.NormalizeInvoiceStatus(request.InvoiceStatus);
        DynamicReceivableReportRules.ValidateDateRange(request.StartDate, request.EndDate);
        DynamicReceivableReportRules.ValidatePageSize(request.PageSize);
        var page = request.Page < 1 ? 1 : request.Page;
        DynamicReceivableReportRules.NormalizeSort(request.SortFieldKey, request.SortDirection);

        // 3) 每次请求重新解析当前账号业务员数据范围（特权账号不过滤）
        var scope = await SalespersonDataScopeService.ResolveAsync(_db, userId);

        // 4) 组装 ERP-074 对账查询，并传入作用域：在 Count / Skip / Take 前过滤允许客户
        var query = new CustomerReceivableReconciliationQuery
        {
            CustomerId = request.CustomerId,
            Currency = currency,
            AllocationState = allocationState,
            InvoiceStatus = invoiceStatus,
            InvoiceDateFrom = request.StartDate?.Date,
            InvoiceDateTo = request.EndDate?.Date,
            Page = page,
            PageSize = request.PageSize,
        };

        var (rows, total) = await CustomerReceivableReconciliationService.ForScopedPreviewAsync(
            _db, query, scope, request.SortFieldKey, request.SortDirection, cancellationToken);

        var columns = fieldKeys.Select(key => DynamicReceivableReportRules.GetField(key)!).ToList();
        var mapped = rows.Select(row => DynamicReceivableReportRules.BuildRow(row, fieldKeys)).ToList();

        return new DynamicReceivableReportPageDto(
            columns,
            mapped,
            total,
            page,
            request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize),
            DynamicReceivableReportRules.ReadOnlyText,
            DynamicReceivableReportRules.BoundaryText,
            DynamicReceivableReportRules.DisclaimerText);
    }

    /// <summary>身份 + 既有「角色 → 菜单」客户资料模块授权（fail closed，绝不猜测身份）</summary>
    private async Task EnsureAuthorizedAsync(long? userId, CancellationToken cancellationToken)
    {
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再预览客户应收账款证据报表", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, userId.Value);
        if (!menuCodes.Contains(DynamicReceivableReportRules.RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{DynamicReceivableReportRules.RequiredMenuText}」"
                + $"（{DynamicReceivableReportRules.RequiredMenuCode}）模块授权：拒绝预览客户应收账款证据报表"
                + "（fail closed，不返回任何数据）",
                ErrorCodes.Forbidden);
        }
    }
}
