using ERP.Application.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace ERP.Api.Controllers;

/// <summary>
/// 通用单据控制器：查询与翻页
/// </summary>
public partial class BillProcController
{
    /// <summary>分页查询（BillNo 搜索 + 状态筛选）</summary>
    [HttpGet("{billType}")]
    public async Task<IActionResult> GetPaged(string billType, [FromQuery] PageQuery query, [FromQuery] int? status)
    {
        if (!Bills.TryGetValue(billType, out var meta))
            return Ok(ApiResponse<object>.Fail("未知单据类型", ErrorCodes.InvalidParameter));

        query.Normalize();
        using var conn = new SqlConnection(_sp.GetConnectionString());
        await conn.OpenAsync();
        var where = "1=1";
        if (!string.IsNullOrWhiteSpace(query.Keyword)) where += " AND BillNo LIKE @kw";
        if (status.HasValue) where += " AND Status = @st";

        using var countCmd = new SqlCommand($"SELECT COUNT(*) FROM db_owner.{meta.Table} WHERE {where}", conn);
        if (!string.IsNullOrWhiteSpace(query.Keyword)) countCmd.Parameters.AddWithValue("@kw", $"%{query.Keyword}%");
        if (status.HasValue) countCmd.Parameters.AddWithValue("@st", status.Value);
        var total = (int)(await countCmd.ExecuteScalarAsync() ?? 0);

        var listSql = $@"SELECT * FROM db_owner.{meta.Table} WHERE {where}
                         ORDER BY Oid DESC OFFSET @off ROWS FETCH NEXT @size ROWS ONLY";
        using var listCmd = new SqlCommand(listSql, conn);
        if (!string.IsNullOrWhiteSpace(query.Keyword)) listCmd.Parameters.AddWithValue("@kw", $"%{query.Keyword}%");
        if (status.HasValue) listCmd.Parameters.AddWithValue("@st", status.Value);
        listCmd.Parameters.AddWithValue("@off", (query.Page - 1) * query.PageSize);
        listCmd.Parameters.AddWithValue("@size", query.PageSize);

        var items = new List<Dictionary<string, object?>>();
        using var reader = await listCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            items.Add(row);
        }
        return Ok(ApiResponse<object>.Success(new { items, total, page = query.Page, pageSize = query.PageSize }));
    }

    /// <summary>翻页导航（first/last/prev/next）</summary>
    [HttpGet("{billType}/navigate")]
    public async Task<IActionResult> Navigate(string billType, [FromQuery] long oid, [FromQuery] string direction)
    {
        if (!Bills.TryGetValue(billType, out var meta))
            return Ok(ApiResponse<object>.Fail("未知单据类型", ErrorCodes.InvalidParameter));

        string sql = direction switch
        {
            "first" => $"SELECT TOP 1 * FROM db_owner.{meta.Table} ORDER BY Oid ASC",
            "last" => $"SELECT TOP 1 * FROM db_owner.{meta.Table} ORDER BY Oid DESC",
            "prev" => $"SELECT TOP 1 * FROM db_owner.{meta.Table} WHERE Oid < @oid ORDER BY Oid DESC",
            "next" => $"SELECT TOP 1 * FROM db_owner.{meta.Table} WHERE Oid > @oid ORDER BY Oid ASC",
            _ => throw BusinessException.InvalidParameter("方向参数无效")
        };

        using var conn = new SqlConnection(_sp.GetConnectionString());
        await conn.OpenAsync();
        using var cmd = new SqlCommand(sql, conn);
        if (direction is "prev" or "next") cmd.Parameters.AddWithValue("@oid", oid);
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            return Ok(ApiResponse<object>.Success(row));
        }
        return Ok(ApiResponse<object>.Fail("没有更多单据", ErrorCodes.NotFound));
    }

    /// <summary>详情（主表 + 副表明细）</summary>
    [HttpGet("{billType}/{oid:long}")]
    public async Task<IActionResult> GetDetail(string billType, long oid)
    {
        if (!Bills.TryGetValue(billType, out var meta))
            return Ok(ApiResponse<object>.Fail("未知单据类型", ErrorCodes.InvalidParameter));

        using var conn = new SqlConnection(_sp.GetConnectionString());
        await conn.OpenAsync();

        // 主表
        var main = new Dictionary<string, object?>();
        using (var cmd = new SqlCommand($"SELECT * FROM db_owner.{meta.Table} WHERE Oid = @oid", conn))
        {
            cmd.Parameters.AddWithValue("@oid", oid);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                    main[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            else
            {
                return Ok(ApiResponse<object>.Fail("单据不存在", ErrorCodes.NotFound));
            }
        }

        // 副表明细
        var details = new List<Dictionary<string, object?>>();
        if (!string.IsNullOrEmpty(meta.DetailTable) && !string.IsNullOrEmpty(meta.DetailFk))
        {
            using var cmd = new SqlCommand(
                $"SELECT * FROM db_owner.{meta.DetailTable} WHERE {meta.DetailFk} = @oid ORDER BY Oid", conn);
            cmd.Parameters.AddWithValue("@oid", oid);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                details.Add(row);
            }
        }

        return Ok(ApiResponse<object>.Success(new { main, details }));
    }
}
