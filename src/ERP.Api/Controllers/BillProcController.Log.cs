using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
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

    /// <summary>
    /// 查询指定单据的操作日志（ERP-406：精确单据身份 + 有限模块 / 路径边界）。
    /// <para>顺序：有限族解析 → 复用 ERP-405 读侧门禁（实时身份 + 该族既有功能菜单 + 客户数据范围）→ 正数 Oid →
    /// 调用方数据范围内的权威旧库行 → 仅按「精确单据路径（含有限动作段）+ 族既有模块标题 + 权威单号」交叉过滤后计数 / 分页。</para>
    /// <para>越权 / 不存在 / 缺结构不返回任何单号、客户提示与历史；零 / 负数 Oid 拒绝全局历史（既有入口为 <c>api/sys/logs</c>）；
    /// 统一只返回受控业务时间线字段（绝不含请求体等原始载荷 / 令牌 / 密钥）。</para>
    /// </summary>
    [HttpGet("{billType}/{oid:long}/logs")]
    public async Task<IActionResult> GetBillLogs(string billType, long oid,
        [FromQuery] int page = 1, [FromQuery] int pageSize = LegacyBillHistoryRules.DefaultPageSize)
    {
        if (!Bills.TryGetValue(billType, out _))
            return Ok(ApiResponse<object>.Fail("未知单据类型", ErrorCodes.InvalidParameter));
        if (!BillTitles.TryGetValue(billType, out var moduleTitle))
            return Ok(ApiResponse<object>.Fail("未知单据类型", ErrorCodes.InvalidParameter));

        try
        {
            // 1) 复用 ERP-405 读侧门禁：在任何单号读取 / 计数 / 历史读取之前完成实时身份 + 既有功能菜单 + 客户数据范围。
            var scope = await LegacyBillAuthorizationRules.EnsureReadAuthorizedAsync(_db, CurrentUserId(), billType);

            // 2) 精确单据的有限允许路径（正数 Oid；零 / 负数在此拒绝，绝不返回全局历史）。
            var allowedPaths = LegacyBillHistoryRules.BuildDocumentPaths(billType, oid).ToArray();

            // 3) 权威旧库行必须在调用方数据范围内：越权 / 不存在 / 缺结构都不返回单号与历史。
            var header = await _legacyReads.ReadAuthoritativeHeaderAsync(billType, oid, scope, RequestCancellation());
            if (header is null || !header.TryGetValue("BillNo", out var rawBillNo))
                return Ok(ApiResponse<object>.Fail("单据不存在", ErrorCodes.NotFound));
            var billNo = rawBillNo?.ToString() ?? string.Empty;

            // 4) 有界分页（页码 / 页大小收敛 + 偏移检查运算）。
            var (normalizedPage, normalizedSize, offset) = LegacyBillHistoryRules.ResolvePaging(page, pageSize);

            // 5) 精确文档身份：族既有模块标题 + 权威单号 + 有限精确路径（绝不按单号单独匹配 / 前缀碰撞 / 任意查询）。
            var source = _db.SysOperationLogs.AsNoTracking()
                .Where(l => !l.IsDeleted
                    && l.Module == moduleTitle
                    && l.BillNo == billNo
                    && allowedPaths.Contains(l.Path));

            var total = await source.CountAsync();
            var items = await source.OrderByDescending(l => l.Id)
                .Skip(offset).Take(normalizedSize)
                .Select(l => new
                {
                    l.Id, l.UserName, l.Module, l.Action, l.BillNo, l.Path,
                    l.IpAddress, l.CreatedAt, l.DurationMs, l.StatusCode
                }).ToListAsync();

            return Ok(ApiResponse<object>.Success(new
            {
                items, total, page = normalizedPage, pageSize = normalizedSize,
                totalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)normalizedSize)
            }));
        }
        catch (BusinessException ex)
        {
            LogLegacyReadDenied("操作历史", billType, ex);
            return Ok(ApiResponse<object>.Fail(ex.Message, ex.Code));
        }
    }
}
