using ERP.Application.Common;
using ERP.Application.DTOs;

namespace ERP.Application.Services;

/// <summary>
/// 通用报表配置平台（ERP-268）的受控关系纯校验器与静态白名单：定义受控「客户」维度关系的稳定键、
/// 来源事实键、目标维度唯一键、多对一基数、目标权限、允许字段清单与缺失 / 删除语义，并校验定义中
/// 的关系选择（任意 / 基数不安全 / 不支持的关系 fail closed）。无数据库依赖，便于逐条单测。
/// </summary>
public static class ReportConfigurationRelationRules
{
    // ==================== 目标维度权限（客户资料菜单，与 ERP-074 / ERP-117 同源） ====================

    /// <summary>目标维度所需既有菜单编码（客户资料）</summary>
    public const string RequiredMenuCode = "customer";

    /// <summary>目标维度所需菜单中文文案</summary>
    public const string RequiredMenuText = "客户资料";

    // ==================== 稳定关系元数据（服务端白名单，绝不来自客户端） ====================

    /// <summary>来源事实键：事实行持久化的客户 Id（引用 BaseCustomer.Id）</summary>
    public const string SourceFactKey = "customerId";

    /// <summary>目标维度唯一键</summary>
    public const string TargetDimensionUniqueKey = "BaseCustomer.Id";

    /// <summary>允许字段：仅客户编码（不泄露隐私）</summary>
    public const string FieldCustomerCode = "code";

    /// <summary>允许字段：仅国家 / 地区（不泄露隐私）</summary>
    public const string FieldCountry = "country";

    /// <summary>缺失 / 删除 / 越权语义</summary>
    public const string MissingDeletedSemantics =
        "客户缺失 / 已删除 / 越权时该维度单元格显示为空并给出有界原因（缺失 / 已删除 / 越权），绝不泄露其它客户";

    /// <summary>允许字段清单（有限、有序）</summary>
    public static readonly IReadOnlyList<ReportConfigurationRelationFieldDto> Fields = new[]
    {
        new ReportConfigurationRelationFieldDto(FieldCustomerCode, "客户编码", ReportConfigurationConstants.TypeText),
        new ReportConfigurationRelationFieldDto(FieldCountry, "国家/地区", ReportConfigurationConstants.TypeText),
    };

    /// <summary>已知关系键（仅「客户」；任意键一律不识别）</summary>
    public static bool IsKnownRelationKey(string? key)
        => !string.IsNullOrWhiteSpace(key)
           && string.Equals(key.Trim(), ReportConfigurationConstants.RelationCustomer, StringComparison.OrdinalIgnoreCase);

    /// <summary>已知关系基数（仅「多对一」；任意 / 一对多 / 事实联接一律不安全）</summary>
    public static bool IsKnownCardinality(string? cardinality)
        => string.Equals(cardinality, ReportConfigurationConstants.CardinalityManyToOne, StringComparison.OrdinalIgnoreCase);

    /// <summary>构造受控「客户」维度关系目录项（服务端静态白名单）。</summary>
    public static ReportConfigurationRelationDto BuildCustomerRelation(string sourceFactGrain) => new(
        ReportConfigurationConstants.RelationCustomer,
        "客户",
        sourceFactGrain,
        SourceFactKey,
        TargetDimensionUniqueKey,
        ReportConfigurationConstants.CardinalityManyToOne,
        RequiredMenuCode,
        RequiredMenuText,
        Fields,
        MissingDeletedSemantics);

    /// <summary>
    /// 校验定义中的关系选择（对数据集关系目录白名单；任意关系键 / 基数不安全 / 不支持字段 / 空字段 /
    /// 重复关系键 / 重复字段键均 fail closed）。关系字段为文本维度、非聚合、不可筛选 / 排序 / 分组 / 公式。
    /// </summary>
    public static void Validate(ReportConfigurationDefinition definition, ReportConfigurationDatasetDto dataset)
    {
        var relations = definition?.Relations ?? new List<ReportConfigurationRelationSelection>();
        if (relations.Count == 0)
            return;

        if (dataset is null)
            throw BusinessException.InvalidParameter("数据集缺失：无法校验关系选择");

        var catalog = dataset.Relations ?? new List<ReportConfigurationRelationDto>();
        var seenRelations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relation in relations)
        {
            if (relation is null)
                throw BusinessException.InvalidParameter("关系选择不能为空");

            var relationKey = (relation.RelationKey ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(relationKey))
                throw BusinessException.InvalidParameter("关系键不能为空");
            EnsureNoSqlOrScript(relationKey, "关系键");

            if (!seenRelations.Add(relationKey))
                throw BusinessException.InvalidParameter($"重复关系键：{relationKey}");

            var catalogRelation = catalog.FirstOrDefault(r =>
                string.Equals(r.Key, relationKey, StringComparison.OrdinalIgnoreCase))
                ?? throw BusinessException.InvalidParameter($"未知关系：{relationKey}（不在数据集受控关系白名单内）");

            if (!IsKnownCardinality(catalogRelation.Cardinality))
                throw BusinessException.RuleConflict($"关系 {relationKey} 的基数不受支持：{catalogRelation.Cardinality}（仅支持多对一）");

            ValidateFields(relation, catalogRelation, relationKey);
        }
    }

    private static void ValidateFields(
        ReportConfigurationRelationSelection relation, ReportConfigurationRelationDto catalogRelation, string relationKey)
    {
        var fields = relation.Fields ?? new List<string>();
        if (fields.Count == 0)
            throw BusinessException.InvalidParameter($"关系 {relationKey} 至少选择一个字段");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in fields)
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw BusinessException.InvalidParameter($"关系 {relationKey} 字段键不能为空");
            var key = raw.Trim();
            EnsureNoSqlOrScript(key, "关系字段");

            if (!seen.Add(key))
                throw BusinessException.InvalidParameter($"关系 {relationKey} 重复字段键：{key}");

            var allowed = catalogRelation.Fields.FirstOrDefault(f =>
                string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase))
                ?? throw BusinessException.InvalidParameter($"关系 {relationKey} 未知字段：{key}（不在允许字段清单内）");

            // 关系字段只能是文本维度（非聚合、非数值），拒绝任何把关系字段当作度量 / 公式输入的载荷
            if (!string.Equals(allowed.Type, ReportConfigurationConstants.TypeText, StringComparison.OrdinalIgnoreCase))
                throw BusinessException.RuleConflict($"关系 {relationKey} 字段 {key} 类型不受支持：{allowed.Type}（仅文本维度）");
        }
    }

    private static void EnsureNoSqlOrScript(string value, string context)
    {
        var lower = value.ToLowerInvariant();
        var markers = new[] { "--", "/*", "*/", ";", "<script", "</script", "eval(", "exec(", "execute(" };
        if (markers.Any(m => lower.Contains(m)))
            throw BusinessException.InvalidParameter($"{context} 含疑似 SQL / 脚本载荷，已拒绝");
    }
}
