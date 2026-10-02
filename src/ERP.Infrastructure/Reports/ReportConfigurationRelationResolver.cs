using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 通用报表配置平台（ERP-268）受控关系解析实现：把已授权当前预览页行中持久化的客户 Id 批量解析为
/// 受控客户维度字段（客户编码 / 国别），并写回预览。只读、批量、AsNoTracking、当前页去重、至多页大小
/// 条数、零 N+1；缺失 / 已删除 / 越权以 null + 有界原因呈现；重复目标身份 fail closed；审计只记录关系键 /
/// 状态，绝不包含客户姓名 / 编码等隐私值。
/// </summary>
public sealed class ReportConfigurationRelationResolver : IReportConfigurationRelationResolver
{
    private readonly IErpDbContext _db;

    public ReportConfigurationRelationResolver(IErpDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task EnrichAsync(
        ReportConfigurationPreviewDto preview,
        ReportConfigurationDefinition definition,
        long? userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(definition);

        var selections = definition.Relations ?? new List<ReportConfigurationRelationSelection>();
        if (selections.Count == 0)
            return;

        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再访问客户维度", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(_db, userId.Value);
        var menuAuthorized = menuCodes.Contains(
            ReportConfigurationRelationRules.RequiredMenuCode, StringComparer.OrdinalIgnoreCase);

        var scope = await SalespersonDataScopeService.ResolveAsync(_db, userId);

        var distinctIds = CollectDistinctCustomerIds(preview.Rows, preview.PageSize);

        var customers = await LookupCustomersAsync(distinctIds, menuAuthorized, cancellationToken);

        var selectedFields = new HashSet<string>(
            definition.Fields ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

        AppendColumns(preview, selections);
        PadCellReasons(preview);

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["resolved"] = 0, ["missing"] = 0, ["deleted"] = 0, ["forbidden"] = 0,
        };

        for (var i = 0; i < preview.Rows.Count; i++)
        {
            var row = preview.Rows[i];
            var customerId = ReadCustomerId(row);

            foreach (var selection in selections)
            {
                if (selection is null)
                    continue;
                foreach (var fieldKey in (selection.Fields ?? new List<string>()))
                {
                    if (string.IsNullOrWhiteSpace(fieldKey))
                        continue;
                    var columnKey = RelationColumnKey(selection.RelationKey, fieldKey);
                    var (value, reason, state) = ResolveCell(customerId, fieldKey, menuAuthorized, scope, customers);
                    row[columnKey] = value;
                    counts[state]++;
                    if (reason is not null)
                        preview.CellReasons[i][columnKey] = reason;
                }
            }
        }

        preview.RelationEvidence = BuildEvidence(selections, counts);

        StripHiddenSourceKey(preview, selectedFields);

        await RecordAuditAsync(userId.Value, selections, counts, cancellationToken);
    }

    private async Task<Dictionary<long, BaseCustomer>> LookupCustomersAsync(
        IReadOnlyList<long> distinctIds, bool menuAuthorized, CancellationToken cancellationToken)
    {
        if (!menuAuthorized || distinctIds.Count == 0)
            return new Dictionary<long, BaseCustomer>();

        var ids = distinctIds.ToList();
        var loaded = await _db.BaseCustomers.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .ToListAsync(cancellationToken);

        var duplicates = loaded.GroupBy(c => c.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            throw BusinessException.RuleConflict("客户维度目标身份重复：失败关闭");

        return loaded.ToDictionary(c => c.Id);
    }

    private static List<long> CollectDistinctCustomerIds(IReadOnlyList<Dictionary<string, object?>> rows, int pageSize)
    {
        var seen = new HashSet<long>();
        var result = new List<long>();
        var limit = Math.Max(pageSize, 1);
        foreach (var row in rows)
        {
            if (row is null)
                continue;
            var id = ReadCustomerId(row);
            if (id is > 0 && seen.Add(id.Value))
            {
                result.Add(id.Value);
                if (result.Count >= limit)
                    break;
            }
        }
        return result;
    }

    private static long? ReadCustomerId(Dictionary<string, object?> row)
    {
        if (row.TryGetValue(ReportConfigurationRelationRules.SourceFactKey, out var value))
            return ToLong(value);
        foreach (var kv in row)
        {
            if (string.Equals(kv.Key, ReportConfigurationRelationRules.SourceFactKey, StringComparison.OrdinalIgnoreCase))
                return ToLong(kv.Value);
        }
        return null;
    }

    private static long? ToLong(object? value) => value switch
    {
        long l => l,
        int i => i,
        short s => s,
        byte b => b,
        decimal d when d == Math.Truncate(d) => (long)d,
        double d when d == Math.Truncate(d) => (long)d,
        string s when long.TryParse(s, out var l) => l,
        _ => null,
    };

    private static (object? Value, string? Reason, string State) ResolveCell(
        long? customerId,
        string fieldKey,
        bool menuAuthorized,
        SalespersonDataScope scope,
        IReadOnlyDictionary<long, BaseCustomer> customers)
    {
        if (customerId is null or <= 0)
            return (null, "缺失", "missing");
        if (!menuAuthorized)
            return (null, "越权", "forbidden");
        if (!scope.AllowsCustomer(customerId.Value))
            return (null, "越权", "forbidden");
        if (!customers.TryGetValue(customerId.Value, out var customer))
            return (null, "缺失", "missing");
        if (customer.IsDeleted)
            return (null, "已删除", "deleted");
        return (SelectField(customer, fieldKey), null, "resolved");
    }

    private static string? SelectField(BaseCustomer customer, string fieldKey)
    {
        if (string.Equals(fieldKey, ReportConfigurationRelationRules.FieldCustomerCode, StringComparison.OrdinalIgnoreCase))
            return customer.CustomerCode;
        if (string.Equals(fieldKey, ReportConfigurationRelationRules.FieldCountry, StringComparison.OrdinalIgnoreCase))
            return customer.Country;
        return null;
    }

    private static string RelationColumnKey(string relationKey, string fieldKey)
        => $"{relationKey.Trim()}.{fieldKey.Trim()}";

    private static void AppendColumns(
        ReportConfigurationPreviewDto preview, IReadOnlyList<ReportConfigurationRelationSelection> selections)
    {
        foreach (var selection in selections)
        {
            if (selection is null)
                continue;
            foreach (var fieldKey in selection.Fields ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(fieldKey))
                    continue;
                var meta = ReportConfigurationRelationRules.Fields.FirstOrDefault(f =>
                    string.Equals(f.Key, fieldKey, StringComparison.OrdinalIgnoreCase));
                var columnKey = RelationColumnKey(selection.RelationKey, fieldKey);
                preview.Columns.Add(new ReportConfigurationColumnDto(
                    columnKey,
                    meta?.Label ?? fieldKey,
                    meta?.Type ?? ReportConfigurationConstants.TypeText,
                    null));
            }
        }
    }

    private static void PadCellReasons(ReportConfigurationPreviewDto preview)
    {
        while (preview.CellReasons.Count < preview.Rows.Count)
            preview.CellReasons.Add(new Dictionary<string, string?>(StringComparer.Ordinal));
    }

    private static void StripHiddenSourceKey(ReportConfigurationPreviewDto preview, HashSet<string> selectedFields)
    {
        var sourceKey = ReportConfigurationRelationRules.SourceFactKey;
        if (selectedFields.Contains(sourceKey))
            return;

        preview.Columns = preview.Columns
            .Where(c => !string.Equals(c.Key, sourceKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var row in preview.Rows)
        {
            var keys = row.Keys.Where(k => string.Equals(k, sourceKey, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var key in keys)
                row.Remove(key);
        }
    }

    private static List<ReportConfigurationRelationEvidenceDto> BuildEvidence(
        IReadOnlyList<ReportConfigurationRelationSelection> selections, IReadOnlyDictionary<string, int> counts)
    {
        return selections
            .Where(s => s is not null)
            .Select(s => new ReportConfigurationRelationEvidenceDto
            {
                RelationKey = s.RelationKey,
                Fields = (s.Fields ?? new List<string>()).ToList(),
                StatusSummary =
                    $"resolved={counts["resolved"]},missing={counts["missing"]},deleted={counts["deleted"]},forbidden={counts["forbidden"]}",
            })
            .ToList();
    }

    private async Task RecordAuditAsync(
        long userId, IReadOnlyList<ReportConfigurationRelationSelection> selections,
        IReadOnlyDictionary<string, int> counts, CancellationToken cancellationToken)
    {
        try
        {
            var summary = new
            {
                relations = selections.Where(s => s is not null)
                    .Select(s => new { key = s.RelationKey, fields = (s.Fields ?? new List<string>()).ToList() })
                    .ToList(),
                resolved = counts["resolved"],
                missing = counts["missing"],
                deleted = counts["deleted"],
                forbidden = counts["forbidden"],
            };
            _db.SysOperationLogs.Add(new SysOperationLog
            {
                UserId = userId,
                UserName = string.Empty,
                Module = "报表管理",
                Action = "预览-客户维度",
                Method = "POST",
                Path = "/api/report-configurations/preview",
                BillNo = string.Empty,
                RequestBody = JsonSerializer.Serialize(summary),
                IpAddress = string.Empty,
                StatusCode = 200,
                DurationMs = 0,
                CreatedAt = DateTime.Now,
            });
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // 审计失败不影响主流程（与 OperationLogMiddleware 同一约定）
        }
    }
}

