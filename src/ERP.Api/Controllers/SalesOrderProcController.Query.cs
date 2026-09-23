using ERP.Application.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace ERP.Api.Controllers;

/// <summary>
/// 销售订单控制器：查询与翻页导航
/// </summary>
public partial class SalesOrderProcController
{
    /// <summary>分页查询（支持单据号搜索、状态筛选）</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] int? status)
    {
        query.Normalize();
        using var conn = new SqlConnection(_sp.GetConnectionString());
        await conn.OpenAsync();
        var where = "1=1";
        if (!string.IsNullOrWhiteSpace(query.Keyword)) where += " AND BillNo LIKE @kw";
        if (status.HasValue) where += " AND Status = @st";

        using var countCmd = new SqlCommand($"SELECT COUNT(*) FROM db_owner.SalesOrder WHERE {where}", conn);
        if (!string.IsNullOrWhiteSpace(query.Keyword)) countCmd.Parameters.AddWithValue("@kw", $"%{query.Keyword}%");
        if (status.HasValue) countCmd.Parameters.AddWithValue("@st", status.Value);
        var total = (int)(await countCmd.ExecuteScalarAsync() ?? 0);

        var listSql = $@"SELECT Oid, BillNo, OrderDate, CustId, TotalAmount, DepositAmount, Status
                         FROM db_owner.SalesOrder WHERE {where}
                         ORDER BY Oid DESC OFFSET @off ROWS FETCH NEXT @size ROWS ONLY";
        using var listCmd = new SqlCommand(listSql, conn);
        if (!string.IsNullOrWhiteSpace(query.Keyword)) listCmd.Parameters.AddWithValue("@kw", $"%{query.Keyword}%");
        if (status.HasValue) listCmd.Parameters.AddWithValue("@st", status.Value);
        listCmd.Parameters.AddWithValue("@off", (query.Page - 1) * query.PageSize);
        listCmd.Parameters.AddWithValue("@size", query.PageSize);

        var items = new List<object>();
        using var reader = await listCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new
            {
                Oid = reader.GetInt64(0), BillNo = reader.GetString(1), OrderDate = reader.GetDateTime(2),
                CustId = reader.GetInt64(3), TotalAmount = reader.GetDecimal(4),
                DepositAmount = reader.GetDecimal(5), Status = reader.GetInt32(6)
            });
        }
        return Ok(ApiResponse<object>.Success(new { items, total, page = query.Page, pageSize = query.PageSize }));
    }

    /// <summary>翻页导航（第一张/上一张/下一张/最后一张）</summary>
    [HttpGet("navigate")]
    public async Task<IActionResult> Navigate([FromQuery] long oid, [FromQuery] string direction)
    {
        string sql = direction switch
        {
            "first" => "SELECT TOP 1 Oid, BillNo, OrderDate, CustId, TotalAmount, Status FROM db_owner.SalesOrder ORDER BY Oid ASC",
            "last" => "SELECT TOP 1 Oid, BillNo, OrderDate, CustId, TotalAmount, Status FROM db_owner.SalesOrder ORDER BY Oid DESC",
            "prev" => "SELECT TOP 1 Oid, BillNo, OrderDate, CustId, TotalAmount, Status FROM db_owner.SalesOrder WHERE Oid < @oid ORDER BY Oid DESC",
            "next" => "SELECT TOP 1 Oid, BillNo, OrderDate, CustId, TotalAmount, Status FROM db_owner.SalesOrder WHERE Oid > @oid ORDER BY Oid ASC",
            _ => throw BusinessException.InvalidParameter("方向参数无效")
        };

        using var conn = new SqlConnection(_sp.GetConnectionString());
        await conn.OpenAsync();
        using var cmd = new SqlCommand(sql, conn);
        if (direction is "prev" or "next") cmd.Parameters.AddWithValue("@oid", oid);
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return Ok(ApiResponse<object>.Success(new
            {
                Oid = reader.GetInt64(0), BillNo = reader.GetString(1), OrderDate = reader.GetDateTime(2),
                CustId = reader.GetInt64(3), TotalAmount = reader.GetDecimal(4), Status = reader.GetInt32(5)
            }));
        }
        return Ok(ApiResponse<object>.Fail("没有更多单据", ErrorCodes.NotFound));
    }
}

/// <summary>销售订单保存请求</summary>
public class SalesOrderSaveRequest
{
    public long Oid { get; set; }
    public DateTime OrderDate { get; set; } = DateTime.Today;
    public long CustId { get; set; }
    public long? SalesmanId { get; set; }
    public int Currency { get; set; } = 2;
    public decimal ExchangeRate { get; set; } = 1;
    public decimal TotalAmount { get; set; }
    public decimal DepositRatio { get; set; }
    public decimal DepositAmount { get; set; }
    public string? PaymentTerms { get; set; }
    public DateTime? DeliveryDate { get; set; }
    public string? ShippingMethod { get; set; }
    public long? PortId { get; set; }
    public string? Remark { get; set; }
}
