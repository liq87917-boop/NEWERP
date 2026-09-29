using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 客户报告包预览控制器（ERP-122，只读、有界）：从客户销项发票登记册打开，把同一客户的
/// <b>销售订单</b>与<b>发票 + 显式收款分摊证据</b>作为两个独立有界分区返回。
/// <list type="number">
/// <item><b>POST /api/customer-report-packet</b>：按正整数客户 Id + 有界日期 / 分页筛选预览两个分区。</item>
/// </list>
/// <para>授权：必须同时具备「销售订单」与「客户资料」两个既有菜单授权；两个分区复用
/// <see cref="IDynamicSalesOrderReportQuery"/>（ERP-112）与 <see cref="IDynamicReceivableReportQuery"/>（ERP-117），
/// 各自独立重检本分区菜单授权与 <see cref="SalespersonDataScopeService"/>（ERP-097）数据范围，任一失败即拒绝整个响应（fail closed）。</para>
/// <para>全程只读：无 Add / Update / Remove / SaveChanges，不执行任意 SQL、不推断发票到订单的链接、不结算、不计算账户余额或催收状态；
/// 请求由既有 <c>OperationLogMiddleware</c> 记录审计。</para>
/// </summary>
[ApiController]
[Route("api/customer-report-packet")]
[Authorize]
public class CustomerReportPacketController : ControllerBase
{
    private readonly IDynamicSalesOrderReportQuery _orderQuery;
    private readonly IDynamicReceivableReportQuery _receivableQuery;
    private readonly IErpDbContext _db;

    public CustomerReportPacketController(
        IDynamicSalesOrderReportQuery orderQuery,
        IDynamicReceivableReportQuery receivableQuery,
        IErpDbContext db)
    {
        _orderQuery = orderQuery;
        _receivableQuery = receivableQuery;
        _db = db;
    }

    /// <summary>当前登录用户 Id（缺失或非数字时返回 null，由规则层 fail closed 拒绝，绝不猜测身份）</summary>
    private long? CurrentUserId()
        => long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    /// <summary>按有界筛选预览两个独立分区（只读；任一权限 / 校验失败均拒绝整个响应，绝不返回部分结果）</summary>
    [HttpPost]
    public async Task<IActionResult> Preview([FromBody] CustomerReportPacketRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1) 字段 / 日期 / 分页有界校验（先于任何读取）
        CustomerReportPacketRules.ValidateCustomerId(request.CustomerId);
        CustomerReportPacketRules.ValidateDateRange(request.StartDate, request.EndDate);
        CustomerReportPacketRules.ValidatePageSize(request.PageSize);
        var page = CustomerReportPacketRules.NormalizePage(request.Page);

        var userId = CurrentUserId();
        CustomerReportPacketRules.EnsureAuthenticated(userId);

        // 2) 端点级双菜单授权：销售订单 + 客户资料都必须具备（任一缺失即 fail closed）
        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(_db, userId!.Value);
        CustomerReportPacketRules.EnsureBothMenuPermissions(menuCodes);

        // 3) 两个作用域化报表查询接口：各自独立重检本分区菜单授权 + 业务员数据范围
        var salesOrders = await _orderQuery.PreviewAsync(new DynamicSalesOrderReportRequest
        {
            CustomerId = request.CustomerId,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            Page = page,
            PageSize = request.PageSize,
        }, userId);

        var receivableEvidence = await _receivableQuery.PreviewAsync(new DynamicReceivableReportRequest
        {
            CustomerId = request.CustomerId,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            Page = page,
            PageSize = request.PageSize,
        }, userId);

        var packet = CustomerReportPacketRules.BuildPacket(request.CustomerId, salesOrders, receivableEvidence);
        return Ok(ApiResponse<CustomerReportPacketDto>.Success(packet));
    }
}
