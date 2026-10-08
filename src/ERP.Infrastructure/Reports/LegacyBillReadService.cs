using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Infrastructure.Data;
using Microsoft.Data.SqlClient;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 旧单据读侧（<c>api/v2/bills</c> 的查询 / 翻页导航 / 详情）的受控只读实现（ERP-405）。
/// <list type="number">
/// <item><b>范围先于读取</b>：未知 / 畸形族、非法分页、越界偏移与调用方数据范围一律在打开连接之前裁决
/// （<see cref="ResolveScopeClause"/>）；计数 / 分页 / 导航 / 表头读取全部共用同一范围子句。</item>
/// <item><b>有限参数化查询</b>：只用 <see cref="LegacyBillReadCatalog"/> 的受控常量标识符（表名 / 列名 / 外键）
/// 与参数化值；绝不 <c>SELECT *</c> / 模型 SQL / 任意 SQL / 联接；稳定按 <c>Oid</c> 排序。</item>
/// <item><b>fail closed</b>：受限账号在没有权威客户归属的族上拒绝；缺表 / 缺列（旧库 Oid-vs-Id 不兼容）映射为
/// 显式 environment-blocked，绝不回退到规范（EF 复数）表、绝不静默返回空集。</item>
/// <item><b>只读</b>：不写库、不调用任何存储过程、不写日志 / 通知；原币 / 原单位 / null 原样保留。</item>
/// </list>
/// </summary>
public sealed class LegacyBillReadService : ILegacyBillReadService
{
    /// <summary>每页最大条数（服务端常量，客户端不可递增）。</summary>
    public const int MaxPageSize = 200;

    /// <summary>受限账号客户数据范围的最大参数化条目数（超过即 fail closed，绝不生成无界条件）。</summary>
    public const int MaxScopeCustomers = 1000;

    private const int CommandTimeoutSeconds = 30;

    private readonly StoredProcedureService _sp;

    public LegacyBillReadService(StoredProcedureService sp)
    {
        _sp = sp ?? throw new ArgumentNullException(nameof(sp));
    }

    /// <inheritdoc />
    public async Task<LegacyBillReadPage> ReadPageAsync(
        string familyKey, LegacyBillReadQuery query, SalespersonDataScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(scope);

        // 打开连接之前完成全部受控校验（族标识 / 分页边界 / 偏移溢出 / 调用方数据范围）。
        var family = LegacyBillReadCatalog.Resolve(familyKey);
        var page = ValidatePage(query.Page);
        var pageSize = ValidatePageSize(query.PageSize);
        var offset = CheckedOffset(page, pageSize);
        var scopeClause = ResolveScopeClause(family, scope);
        var where = BuildWhere(scopeClause, query.Keyword, query.Status);

        var table = Table(family);
        var selectList = string.Join(", ", family.HeaderColumns.Select(Quote));
        var countSql = $"SELECT COUNT(*) FROM {table} WHERE {where.Sql}";
        var pageSql = $"SELECT {selectList} FROM {table} WHERE {where.Sql} ORDER BY {Quote(Oid)} DESC "
                      + "OFFSET @off ROWS FETCH NEXT @size ROWS ONLY";

        try
        {
            using var conn = new SqlConnection(_sp.GetConnectionString());
            await conn.OpenAsync(cancellationToken);

            int total;
            using (var countCmd = new SqlCommand(countSql, conn) { CommandTimeout = CommandTimeoutSeconds })
            {
                AddParameters(countCmd, where);
                total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(cancellationToken) ?? 0);
            }

            var items = new List<Dictionary<string, object?>>();
            using (var pageCmd = new SqlCommand(pageSql, conn) { CommandTimeout = CommandTimeoutSeconds })
            {
                AddParameters(pageCmd, where);
                pageCmd.Parameters.AddWithValue("@off", offset);
                pageCmd.Parameters.AddWithValue("@size", pageSize);
                using var reader = await pageCmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    items.Add(ReadRow(reader, family.HeaderColumns));
            }

            return new LegacyBillReadPage
            {
                Columns = family.HeaderColumns,
                Items = items,
                Total = total,
                Page = page,
                PageSize = pageSize,
                TotalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize),
            };
        }
        catch (SqlException ex)
        {
            throw MapSqlException(ex, family);
        }
    }

    /// <inheritdoc />
    public async Task<LegacyBillReadDetail?> ReadDetailAsync(
        string familyKey, long oid, SalespersonDataScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var family = LegacyBillReadCatalog.Resolve(familyKey);
        var anchor = ValidateOid(oid);
        var scopeClause = ResolveScopeClause(family, scope);

        var table = Table(family);
        var selectList = string.Join(", ", family.HeaderColumns.Select(Quote));
        var headerSql = $"SELECT {selectList} FROM {table} "
                        + $"WHERE {Quote(Oid)} = @oid AND {scopeClause.Sql}";

        try
        {
            using var conn = new SqlConnection(_sp.GetConnectionString());
            await conn.OpenAsync(cancellationToken);

            Dictionary<string, object?>? main = null;
            using (var headerCmd = new SqlCommand(headerSql, conn) { CommandTimeout = CommandTimeoutSeconds })
            {
                AddParameters(headerCmd, scopeClause.Parameters);
                headerCmd.Parameters.AddWithValue("@oid", anchor);
                using var reader = await headerCmd.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                    main = ReadRow(reader, family.HeaderColumns);
            }

            // 表头不在范围内 / 不存在：同一结果（不区分，避免泄露锚点存在性）；绝不读取任何副表。
            if (main is null)
                return null;

            var details = new List<Dictionary<string, object?>>();
            if (family.Detail is { } detail)
            {
                var detailSelect = string.Join(", ", detail.Columns.Select(Quote));
                var detailSql = $"SELECT {detailSelect} FROM db_owner.{Quote(detail.TableName)} "
                                + $"WHERE {Quote(detail.ForeignKeyColumn)} = @oid ORDER BY {Quote(Oid)}";
                using var detailCmd = new SqlCommand(detailSql, conn) { CommandTimeout = CommandTimeoutSeconds };
                detailCmd.Parameters.AddWithValue("@oid", anchor);
                using var reader = await detailCmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    details.Add(ReadRow(reader, detail.Columns));
            }

            return new LegacyBillReadDetail { Main = main, Details = details };
        }
        catch (SqlException ex)
        {
            throw MapSqlException(ex, family);
        }
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, object?>?> ReadAuthoritativeHeaderAsync(
        string familyKey, long oid, SalespersonDataScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var family = LegacyBillReadCatalog.Resolve(familyKey);
        var anchor = ValidateOid(oid);
        var scopeClause = ResolveScopeClause(family, scope);

        var table = Table(family);
        var selectList = string.Join(", ", family.HeaderColumns.Select(Quote));
        var sql = $"SELECT TOP (1) {selectList} FROM {table} "
                  + $"WHERE {Quote(Oid)} = @oid AND {scopeClause.Sql} ORDER BY {Quote(Oid)} DESC";

        try
        {
            using var conn = new SqlConnection(_sp.GetConnectionString());
            await conn.OpenAsync(cancellationToken);

            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = CommandTimeoutSeconds };
            AddParameters(cmd, scopeClause.Parameters);
            cmd.Parameters.AddWithValue("@oid", anchor);
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

            // 越权 / 不存在：同一结果（不区分，避免泄露锚点存在性）；绝不读取任何副表。
            return await reader.ReadAsync(cancellationToken)
                ? ReadRow(reader, family.HeaderColumns)
                : null;
        }
        catch (SqlException ex)
        {
            throw MapSqlException(ex, family);
        }
    }

    /// <inheritdoc />
    public async Task<LegacyBillNavigateResult> NavigateAsync(
        string familyKey, long oid, string? direction, SalespersonDataScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var family = LegacyBillReadCatalog.Resolve(familyKey);
        var normalized = (direction ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is not ("first" or "last" or "prev" or "next"))
            throw BusinessException.InvalidParameter("方向参数无效");

        // first / last 不需要锚点（调用方通常传 0）：只有 prev / next 才校验锚点为合法正整数。
        var relative = normalized is "prev" or "next";
        var anchor = relative ? ValidateOid(oid) : 0L;
        var scopeClause = ResolveScopeClause(family, scope);

        var table = Table(family);
        var selectList = string.Join(", ", family.HeaderColumns.Select(Quote));
        var oidColumn = Quote(Oid);
        var sql = normalized switch
        {
            "first" => $"SELECT TOP 1 {selectList} FROM {table} WHERE {scopeClause.Sql} ORDER BY {oidColumn} ASC",
            "last" => $"SELECT TOP 1 {selectList} FROM {table} WHERE {scopeClause.Sql} ORDER BY {oidColumn} DESC",
            "prev" => $"SELECT TOP 1 {selectList} FROM {table} WHERE {scopeClause.Sql} AND {oidColumn} < @oid "
                      + $"ORDER BY {oidColumn} DESC",
            _ => $"SELECT TOP 1 {selectList} FROM {table} WHERE {scopeClause.Sql} AND {oidColumn} > @oid "
                 + $"ORDER BY {oidColumn} ASC",
        };

        try
        {
            using var conn = new SqlConnection(_sp.GetConnectionString());
            await conn.OpenAsync(cancellationToken);

            if (relative)
            {
                // 先在同范围内核验锚点：不可访问（越权 / 不存在）与「范围内没有更多」返回同一结果，绝不泄露。
                var anchorSql = $"SELECT TOP 1 {oidColumn} FROM {table} "
                                + $"WHERE {oidColumn} = @oid AND {scopeClause.Sql}";
                using var anchorCmd = new SqlCommand(anchorSql, conn) { CommandTimeout = CommandTimeoutSeconds };
                AddParameters(anchorCmd, scopeClause.Parameters);
                anchorCmd.Parameters.AddWithValue("@oid", anchor);
                var accessible = await anchorCmd.ExecuteScalarAsync(cancellationToken);
                if (accessible is null || accessible == DBNull.Value)
                    return new LegacyBillNavigateResult { Found = false, Row = null };
            }

            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = CommandTimeoutSeconds };
            AddParameters(cmd, scopeClause.Parameters);
            if (relative)
                cmd.Parameters.AddWithValue("@oid", anchor);

            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return new LegacyBillNavigateResult { Found = false, Row = null };

            return new LegacyBillNavigateResult { Found = true, Row = ReadRow(reader, family.HeaderColumns) };
        }
        catch (SqlException ex)
        {
            throw MapSqlException(ex, family);
        }
    }

    /// <summary>旧库主键列名（受控常量，稳定排序键）。</summary>
    private const string Oid = LegacyBillReadCatalog.OidColumn;

    /// <summary>
    /// 解析调用方数据范围对应的行约束子句（<strong>纯函数</strong>，供单元测试直接验证；不触碰数据库）：
    /// 特权（全量范围）不加约束；受限账号按显式客户归属列参数化 <c>IN</c>；可见客户为空恒假；
    /// 受限账号在无权威客户归属的族上、或客户范围超过受控上限时 fail closed。
    /// </summary>
    public static LegacyBillScopeClause ResolveScopeClause(
        LegacyBillReadFamilyDefinition family, SalespersonDataScope scope)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(scope);

        if (scope.AllowedCustomerIds is null)
            return LegacyBillScopeClause.Privileged;

        if (family.OwnershipKind != LegacyBillOwnershipKind.Customer ||
            string.IsNullOrWhiteSpace(family.OwnershipColumn))
        {
            throw new BusinessException(
                LegacyBillAuthorizationRules.ReadOwnershipDeniedText(family.FamilyKey, family.Title),
                ErrorCodes.Forbidden);
        }

        var allowed = scope.AllowedCustomerIds;
        if (allowed.Count == 0)
            return LegacyBillScopeClause.DenyAll;
        if (allowed.Count > MaxScopeCustomers)
        {
            throw new BusinessException(
                LegacyBillAuthorizationRules.ReadScopeTooLargeText(family.FamilyKey, MaxScopeCustomers),
                ErrorCodes.Forbidden);
        }

        var ordered = allowed.OrderBy(id => id).ToList();
        var names = new List<string>(ordered.Count);
        var parameters = new List<(string Name, object? Value)>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var name = "@scope" + i;
            names.Add(name);
            parameters.Add((name, ordered[i]));
        }

        return new LegacyBillScopeClause(
            $"{Quote(family.OwnershipColumn)} IN ({string.Join(", ", names)})", parameters);
    }

    private static LegacyBillWhere BuildWhere(LegacyBillScopeClause scope, string? keyword, int? status)
    {
        var clauses = new List<string> { scope.Sql };
        var parameters = new List<(string Name, object? Value)>();
        parameters.AddRange(scope.Parameters);

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            clauses.Add($"{Quote("BillNo")} LIKE @kw");
            parameters.Add(("@kw", $"%{keyword.Trim()}%"));
        }

        if (status.HasValue)
        {
            clauses.Add($"{Quote("Status")} = @st");
            parameters.Add(("@st", status.Value));
        }

        return new LegacyBillWhere(string.Join(" AND ", clauses), parameters);
    }

    private static void AddParameters(SqlCommand command, LegacyBillWhere where)
        => AddParameters(command, where.Parameters);

    private static void AddParameters(SqlCommand command, IReadOnlyList<(string Name, object? Value)> parameters)
    {
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static Dictionary<string, object?> ReadRow(SqlDataReader reader, IReadOnlyList<string> columns)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < columns.Count && i < reader.FieldCount; i++)
            row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return row;
    }

    private static int ValidatePage(int page)
    {
        if (page < 1)
            throw BusinessException.InvalidParameter($"页码必须从 1 开始（收到 {page}）");
        return page;
    }

    private static int ValidatePageSize(int pageSize)
    {
        if (pageSize < 1 || pageSize > MaxPageSize)
            throw BusinessException.InvalidParameter($"每页条数必须在 1 ~ {MaxPageSize} 之间（收到 {pageSize}）");
        return pageSize;
    }

    private static int CheckedOffset(int page, int pageSize)
    {
        var offset = (long)(page - 1) * pageSize;
        if (offset > int.MaxValue)
            throw BusinessException.InvalidParameter("分页偏移超出安全范围");
        return (int)offset;
    }

    private static long ValidateOid(long oid)
    {
        if (oid <= 0)
            throw BusinessException.InvalidParameter("单据 Oid 必须为正整数");
        return oid;
    }

    private static string Table(LegacyBillReadFamilyDefinition family)
        => $"db_owner.{Quote(family.TableName)}";

    private static string Quote(string identifier) => $"[{identifier}]";

    private static BusinessException MapSqlException(SqlException ex, LegacyBillReadFamilyDefinition family)
    {
        // 208 = 无效对象名（缺表），207 = 无效列名（Oid-vs-Id / 缺列不兼容）。
        // 显式 environment-blocked，绝不猜测 / 降级 / 回退到规范（EF 复数）表。
        if (ex.Number is 208 or 207)
        {
            return new BusinessException(
                $"旧单据表 {family.TableName} 不存在或列结构不兼容"
                + "（旧库 Oid-vs-Id 差异：environment-blocked，拒绝读取，绝不回退到规范表）",
                ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported);
        }

        return new BusinessException(
            $"旧单据读取失败（environment-blocked）：{ex.Message}",
            ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported);
    }

    private sealed record LegacyBillWhere(string Sql, IReadOnlyList<(string Name, object? Value)> Parameters);
}
