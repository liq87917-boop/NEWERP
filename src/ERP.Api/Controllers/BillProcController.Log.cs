using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 通用单据控制器：单据操作日志（记录 + 查看）
/// </summary>
public partial class BillProcController
{
    /// <summary>单据类型中文名称（用于日志与打印）</summary>
    public static readonly Dictionary<string, string> BillTitles = new()
    {
        ["sales-order"] = "销售订单", ["purchase-order"] = "采购订单", ["inquiry"] = "询价单",
        ["stock-in"] = "采购入库单", ["stock-out"] = "销售出库单",
        ["receipt"] = "收款单", ["payment"] = "付款单",
        ["deposit-apply"] = "定金申请单", ["payment-apply"] = "货款申请单",
        ["container-settlement"] = "装柜结算单", ["bulk-settlement"] = "散货结算单",
        ["complaint"] = "客诉单", ["receiving-plan"] = "收货计划",
        ["booking"] = "订柜信息", ["pre-loading"] = "预装柜单", ["loading-list"] = "装柜清单",
    };

    /// <summary>操作动作英文标识 -> 中文名称</summary>
    private static readonly Dictionary<string, string> ActionTitles = new()
    {
        ["Save"] = "新增", ["Update"] = "保存", ["Audit"] = "审核", ["UnAudit"] = "销审",
        ["Void"] = "作废", ["Restore"] = "还原", ["Delete"] = "删除",
    };

    /// <summary>读取单据号（用于日志记录，失败返回空字符串）</summary>
    private async Task<string> ReadBillNoAsync(string table, long oid)
    {
        try
        {
            using var conn = new SqlConnection(_sp.GetConnectionString());
            await conn.OpenAsync();
            using var cmd = new SqlCommand($"SELECT BillNo FROM db_owner.{table} WHERE Oid = @oid", conn);
            cmd.Parameters.AddWithValue("@oid", oid);
            var value = await cmd.ExecuteScalarAsync();
            return value?.ToString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "读取单据号失败：表 {Table} 主键 {Oid}", table, oid);
            return string.Empty;
        }
    }

    /// <summary>
    /// 写入单据操作日志（含单据号）。日志写入失败不影响业务主流程。
    /// </summary>
    private async Task WriteBillLogAsync(string billType, long oid, string billNo, string action)
    {
        if (oid <= 0) return;
        try
        {
            var userIdClaim = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            long.TryParse(userIdClaim, out var userId);
            var userName = User?.Identity?.Name ?? "系统";

            _db.SysOperationLogs.Add(new SysOperationLog
            {
                UserId = userId == 0 ? null : userId,
                UserName = userName,
                Module = BillTitles.TryGetValue(billType, out var title) ? title : billType,
                Action = ActionTitles.TryGetValue(action, out var actionTitle) ? actionTitle : action,
                Method = "POST",
                Path = $"/api/v2/bills/{billType}/{oid}",
                BillNo = billNo ?? string.Empty,
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
                StatusCode = 200,
                DurationMs = 0,
                CreatedAt = DateTime.Now
            });
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "写入单据操作日志失败：{BillType} / {Oid}", billType, oid);
        }
    }

    /// <summary>查询指定单据的操作日志（按单据号模糊匹配，含主表与明细操作）</summary>
    [HttpGet("{billType}/{oid:long}/logs")]
    public async Task<IActionResult> GetBillLogs(string billType, long oid,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        if (!Bills.TryGetValue(billType, out var meta))
            return Ok(ApiResponse<object>.Fail("未知单据类型", ErrorCodes.InvalidParameter));

        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 500) pageSize = 50;

        // 未传 Oid（=0）时退回按单据类型查询全部操作日志
        var source = _db.SysOperationLogs.AsNoTracking().Where(l => !l.IsDeleted);
        if (oid > 0)
        {
            var billNo = await ReadBillNoAsync(meta.Table, oid);
            if (!string.IsNullOrEmpty(billNo))
                source = source.Where(l => l.BillNo == billNo || l.Path == $"/api/v2/bills/{billType}/{oid}");
            else
                source = source.Where(l => l.Path == $"/api/v2/bills/{billType}/{oid}");
        }
        else
        {
            source = source.Where(l => l.Path.StartsWith($"/api/v2/bills/{billType}/"));
        }

        var total = await source.CountAsync();
        var items = await source.OrderByDescending(l => l.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(l => new
            {
                l.Id, l.UserName, l.Module, l.Action, l.BillNo, l.Path,
                l.IpAddress, l.CreatedAt, l.DurationMs, l.StatusCode
            }).ToListAsync();

        return Ok(ApiResponse<object>.Success(new
        {
            items, total, page, pageSize,
            totalPages = (int)Math.Ceiling(total / (double)pageSize)
        }));
    }
}
