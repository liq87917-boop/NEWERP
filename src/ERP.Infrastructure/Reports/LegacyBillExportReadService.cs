using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Infrastructure.Data;
using Microsoft.Data.SqlClient;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 旧单据导出族的受控只读读取实现（ERP-308 Stage 2）：以受控常量标识符（表名 / 列名 / Oid / 日期字段）
/// + 参数化值读取旧单据表一页原值数据。SQL 只用目录常量标识符，绝不做 SELECT * / 模型 SQL / 联接 / 用户标识符；
/// 稳定按 Oid DESC 分页（与旧导出路由一致）；缺表 / 缺列（Oid-vs-Id 不兼容）映射为显式 environment-blocked。
/// </summary>
public sealed class LegacyBillExportReadService : ILegacyBillExportReadService
{
    private readonly StoredProcedureService _sp;

    private const int MaxPageSize = 200;
    private const int MaxDateRangeDays = 366;
    private const int CommandTimeoutSeconds = 30;

    public LegacyBillExportReadService(StoredProcedureService sp)
    {
        _sp = sp ?? throw new ArgumentNullException(nameof(sp));
    }

    /// <inheritdoc />
    public async Task<LegacyBillExportPage> ReadPageAsync(
        LegacyBillExportQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // 打开查询之前先做全部受控校验（拒绝未知 / 畸形标识 + 有界约束）。
        var family = LegacyBillExportCatalog.Resolve(query.FamilyKey);
        var columns = LegacyBillExportCatalog.ResolveColumns(family, query.Fields);

        if (query.Page < 1)
            throw BusinessException.InvalidParameter($"页码必须从 1 开始（收到 {query.Page}）");
        if (query.PageSize < 1 || query.PageSize > MaxPageSize)
            throw BusinessException.InvalidParameter($"每页条数必须在 1 ~ {MaxPageSize} 之间（收到 {query.PageSize}）");

        var (start, end) = ValidateDateRange(query.StartDate, query.EndDate);
        var where = BuildWhere(family, query.Keyword, query.Status, start, end);

        var selectList = string.Join(", ", columns.Select(c => Quote(c.Key)));
        var table = $"db_owner.{Quote(family.TableName)}";
        var whereSql = where.Sql;

        var countSql = $"SELECT COUNT(*) FROM {table} WHERE {whereSql}";
        var pageSql = $"SELECT {selectList} FROM {table} WHERE {whereSql} ORDER BY Oid DESC OFFSET @off ROWS FETCH NEXT @size ROWS ONLY";

        var connectionString = _sp.GetConnectionString();
        try
        {
            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(cancellationToken);

            int total;
            using (var countCmd = new SqlCommand(countSql, conn) { CommandTimeout = CommandTimeoutSeconds })
            {
                AddParameters(countCmd, where);
                total = (int)(await countCmd.ExecuteScalarAsync(cancellationToken) ?? 0);
            }

            var rows = new List<Dictionary<string, object?>>();
            using (var pageCmd = new SqlCommand(pageSql, conn) { CommandTimeout = CommandTimeoutSeconds })
            {
                AddParameters(pageCmd, where);
                pageCmd.Parameters.AddWithValue("@off", (query.Page - 1) * query.PageSize);
                pageCmd.Parameters.AddWithValue("@size", query.PageSize);

                using var reader = await pageCmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                    for (var i = 0; i < columns.Count; i++)
                        row[columns[i].Key] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    rows.Add(row);
                }
            }

            return new LegacyBillExportPage
            {
                Columns = columns,
                Rows = rows,
                Total = total,
                Page = query.Page,
                PageSize = query.PageSize,
                TotalPages = total == 0 ? 1 : (int)Math.Ceiling(total / (double)query.PageSize),
            };
        }
        catch (SqlException ex)
        {
            throw MapSqlException(ex, family);
        }
    }

    private static (DateTime? Start, DateTime? End) ValidateDateRange(DateTime? start, DateTime? end)
    {
        if (start is null && end is null)
            return (null, null);

        var startDate = (start ?? DateTime.Today).Date;
        var endDate = (end ?? DateTime.Today).Date;
        if (endDate < startDate)
            throw BusinessException.InvalidParameter("旧单据导出的结束日期不能早于开始日期");
        if ((endDate - startDate).Days + 1 > MaxDateRangeDays)
            throw BusinessException.InvalidParameter($"旧单据导出的日期范围最大 {MaxDateRangeDays} 天（含首尾）");

        return (startDate, endDate);
    }

    private static WhereClause BuildWhere(
        LegacyBillExportFamilyDefinition family, string? keyword, int? status, DateTime? start, DateTime? end)
    {
        var clauses = new List<string> { "1=1" };
        var parameters = new List<(string Name, object? Value)>();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            clauses.Add("[BillNo] LIKE @kw");
            parameters.Add(("@kw", $"%{keyword.Trim()}%"));
        }

        if (status.HasValue)
        {
            clauses.Add("[Status] = @st");
            parameters.Add(("@st", status.Value));
        }

        if (start.HasValue)
        {
            clauses.Add($"[{family.DateField}] >= @start");
            parameters.Add(("@start", start.Value));
        }

        if (end.HasValue)
        {
            clauses.Add($"[{family.DateField}] <= @end");
            parameters.Add(("@end", end.Value));
        }

        return new WhereClause(string.Join(" AND ", clauses), parameters);
    }

    private static void AddParameters(SqlCommand command, WhereClause where)
    {
        foreach (var (name, value) in where.Parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static string Quote(string identifier) => $"[{identifier}]";

    private static BusinessException MapSqlException(SqlException ex, LegacyBillExportFamilyDefinition family)
    {
        // 208 = 无效对象名（缺表），207 = 无效列名（Oid-vs-Id / 缺列不兼容）。显式 environment-blocked，绝不猜测 / 降级 / 授权。
        if (ex.Number is 208 or 207)
        {
            return new BusinessException(
                $"旧单据表 {family.TableName} 不存在或列结构不兼容（旧库 Oid-vs-Id 差异：environment-blocked，拒绝读取）",
                ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported);
        }

        return new BusinessException(
            $"旧单据导出读取失败（environment-blocked）：{ex.Message}",
            ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported);
    }

    private sealed record WhereClause(string Sql, List<(string Name, object? Value)> Parameters);
}
