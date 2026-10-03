using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ERP.Application.Services;

/// <summary>
/// 通用报表配置平台（ERP-276 Stage 1）可移植报表定义传输服务实现：导出（自有草稿 / 自有发布修订 /
/// 被共享的固定发布快照）与导入（严格校验后创建当前用户新的私有草稿）。
/// <para>导出只走服务端详情解析并重新校验当前数据集授权（fail closed），信封只含格式 / schema 版本 /
/// 安全展示名称与结构化定义，绝不包含 ERP 数据行、内部授权 / 所有者 Id、审计身份、连接 / SQL 数据、
/// 修订历史或附件。</para>
/// <para>导入严格校验：UTF-8 原文 ≤64KiB、JSON 深度 ≤16、顶层键仅 format / schemaVersion / name /
/// definition 且无重复；拒绝未知 / 身份 / 共享 / 发布状态字段；复用 <see cref="IReportConfigurationService"/>
/// 创建当前用户私有草稿（version1 + 正常审计，绝不覆盖 Id / 绝不发布 / 绝不扩权）。</para>
/// </summary>
public sealed class ReportConfigurationTransferService : IReportConfigurationTransferService
{
    private const int MaxNameLength = 200;
    private const int MaxJsonDepth = 16;
    private const string FallbackName = "报表配置";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions StrictDefinitionOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly IErpDbContext _db;
    private readonly IReportConfigurationCatalog _catalog;
    private readonly IReportConfigurationService _service;

    public ReportConfigurationTransferService(
        IErpDbContext db,
        IReportConfigurationCatalog catalog,
        IReportConfigurationService service)
    {
        _db = db;
        _catalog = catalog;
        _service = service;
    }

    /// <inheritdoc />
    public async Task<ReportConfigurationTransferEnvelopeDto> ExportAsync(
        long userId, ReportConfigurationTransferExportRequest request, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(userId);
        ArgumentNullException.ThrowIfNull(request);
        if (request.ConfigurationId <= 0)
            throw BusinessException.InvalidParameter("请选择要导出的报表配置");

        var source = await ResolveExportSourceAsync(userId, request, cancellationToken);

        // 每次导出都按当前账号重新校验数据集既有菜单授权（fail closed）；已保存定义本身绝不授予权限。
        var dataset = await _catalog.GetDatasetAsync(source.DatasetKey, userId, cancellationToken);
        if (dataset is null)
        {
            throw new BusinessException(
                $"当前账号没有「{source.DatasetKey}」数据集授权：拒绝导出（fail closed）",
                ErrorCodes.Forbidden);
        }

        ReportConfigurationRules.Validate(source.Definition, dataset);

        return new ReportConfigurationTransferEnvelopeDto
        {
            Format = ReportConfigurationConstants.TransferFormat,
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            Name = SafeDisplayName(source.Name),
            Definition = source.Definition,
        };
    }

    /// <inheritdoc />
    public async Task<ReportConfigurationDto> ImportAsync(
        long userId, ReportConfigurationTransferImportRequest request, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(userId);
        ArgumentNullException.ThrowIfNull(request);

        var (name, definition) = ParseEnvelope(request.Json);

        // 复用既有私有配置服务创建接收人自有草稿：version1 + 正常审计，绝不覆盖 Id / 绝不发布 / 绝不扩权。
        return await _service.CreateAsync(userId,
            new ReportConfigurationSaveDto { Name = name, Definition = definition }, cancellationToken);
    }

    // ==================== 导出来源解析 ====================

    private sealed class ExportSource
    {
        public ExportSource(string name, string datasetKey, ReportConfigurationDefinition definition)
        {
            Name = name;
            DatasetKey = datasetKey;
            Definition = definition;
        }

        public string Name { get; }
        public string DatasetKey { get; }
        public ReportConfigurationDefinition Definition { get; }
    }

    private async Task<ExportSource> ResolveExportSourceAsync(
        long userId, ReportConfigurationTransferExportRequest request, CancellationToken cancellationToken)
    {
        // 1) 自有配置：空修订号 = 当前草稿；指定修订号 = 自有发布修订快照。
        var owned = await _db.ReportConfigurations.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == request.ConfigurationId && !c.IsDeleted && c.OwnerUserId == userId,
                cancellationToken);

        if (owned is not null)
        {
            if (request.RevisionVersion is null)
            {
                return new ExportSource(
                    owned.Name,
                    owned.DatasetKey,
                    DeserializeDefinition(owned.DefinitionJson)
                        ?? throw BusinessException.RuleConflict("报表定义缺失，无法导出"));
            }

            var revision = await _db.ReportConfigurationRevisions.AsNoTracking()
                .FirstOrDefaultAsync(r => r.ReportConfigurationId == owned.Id
                    && !r.IsDeleted && r.Version == request.RevisionVersion.Value, cancellationToken)
                ?? throw BusinessException.NotFound("要导出的发布修订不存在或无权访问");

            return new ExportSource(
                revision.Name,
                revision.DatasetKey,
                DeserializeDefinition(revision.DefinitionJson)
                    ?? throw BusinessException.RuleConflict("发布修订定义缺失，无法导出"));
        }

        // 2) 被共享的固定发布快照：只有仍在生效的授权才可导出；撤销后 fail closed。
        var grant = await _db.ReportConfigurationGrants.AsNoTracking()
            .FirstOrDefaultAsync(g => g.RecipientUserId == userId
                && g.ReportConfigurationId == request.ConfigurationId && !g.IsDeleted, cancellationToken);

        if (grant is null)
            throw BusinessException.NotFound("报表配置不存在或无权访问");

        var config = await _db.ReportConfigurations.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == grant.ReportConfigurationId && !c.IsDeleted, cancellationToken);
        if (config is null)
            throw BusinessException.NotFound("共享报表不存在或无权访问");

        var pinned = await _db.ReportConfigurationRevisions.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ReportConfigurationId == config.Id
                && !r.IsDeleted && r.Version == grant.RevisionVersion, cancellationToken)
            ?? throw BusinessException.NotFound("共享报表不存在或无权访问");

        return new ExportSource(
            pinned.Name,
            pinned.DatasetKey,
            DeserializeDefinition(pinned.DefinitionJson)
                ?? throw BusinessException.RuleConflict("共享发布修订定义缺失，无法导出"));
    }

    // ==================== 导入信封严格解析 ====================

    private static (string Name, ReportConfigurationDefinition Definition) ParseEnvelope(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw BusinessException.InvalidParameter("导入内容不能为空");

        var byteCount = Encoding.UTF8.GetByteCount(json);
        if (byteCount > ReportConfigurationRules.MaxSerializedBytes)
        {
            throw BusinessException.InvalidParameter(
                $"导入内容 {byteCount} 字节，超过上限 {ReportConfigurationRules.MaxSerializedBytes} 字节");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = MaxJsonDepth,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        }
        catch (JsonException ex)
        {
            throw BusinessException.InvalidParameter($"导入 JSON 无效（含过深嵌套或非法结构）：{ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw BusinessException.InvalidParameter("导入内容必须是 JSON 对象");

            List<JsonProperty> properties;
            try
            {
                properties = root.EnumerateObject().ToList();
            }
            catch (InvalidOperationException)
            {
                throw BusinessException.InvalidParameter("导入 JSON 含重复的顶层键");
            }

            if (properties.Count != 4)
            {
                throw BusinessException.InvalidParameter(
                    "导入 JSON 顶层键必须且仅包含 format / schemaVersion / name / definition");
            }

            string? format = null;
            int? schemaVersion = null;
            string? name = null;
            JsonElement? definitionElement = null;

            foreach (var property in properties)
            {
                switch (property.Name)
                {
                    case "format":
                        if (property.Value.ValueKind != JsonValueKind.String)
                            throw BusinessException.InvalidParameter("format 必须是字符串");
                        format = property.Value.GetString();
                        break;
                    case "schemaVersion":
                        if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var sv))
                            throw BusinessException.InvalidParameter("schemaVersion 必须是整数");
                        schemaVersion = sv;
                        break;
                    case "name":
                        if (property.Value.ValueKind != JsonValueKind.String)
                            throw BusinessException.InvalidParameter("name 必须是字符串");
                        name = property.Value.GetString();
                        break;
                    case "definition":
                        if (property.Value.ValueKind != JsonValueKind.Object)
                            throw BusinessException.InvalidParameter("definition 必须是 JSON 对象");
                        definitionElement = property.Value;
                        break;
                    default:
                        throw BusinessException.InvalidParameter($"未知顶层键: {property.Name}");
                }
            }

            if (!string.Equals(format, ReportConfigurationConstants.TransferFormat, StringComparison.Ordinal))
            {
                throw BusinessException.InvalidParameter(
                    $"不支持的格式: {format ?? "(空)"}（当前仅支持 {ReportConfigurationConstants.TransferFormat}）");
            }

            if (schemaVersion != ReportConfigurationRules.CurrentSchemaVersion)
            {
                throw BusinessException.InvalidParameter(
                    $"不支持的 schema 版本: {schemaVersion}（当前仅支持 {ReportConfigurationRules.CurrentSchemaVersion}）");
            }

            if (definitionElement is null)
                throw BusinessException.InvalidParameter("缺少报表定义");

            var safeName = NormalizeAndValidateName(name);

            ReportConfigurationDefinition? definition;
            try
            {
                definition = JsonSerializer.Deserialize<ReportConfigurationDefinition>(
                    definitionElement.Value.GetRawText(), StrictDefinitionOptions);
            }
            catch (JsonException)
            {
                throw BusinessException.InvalidParameter(
                    "报表定义含未知或不允许的字段（身份 / 共享 / 发布状态等一律拒绝导入）");
            }

            if (definition is null)
                throw BusinessException.InvalidParameter("报表定义无效");

            ValidateDefinitionDisplayText(definition);

            return (safeName, definition);
        }
    }

    private static string NormalizeAndValidateName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            throw BusinessException.InvalidParameter("报表名称不能为空");
        if (trimmed.Length > MaxNameLength)
            throw BusinessException.InvalidParameter($"报表名称不能超过 {MaxNameLength} 字符");
        ReportConfigurationRules.ValidateSafeText(trimmed, "报表名称");
        return trimmed;
    }

    private static void ValidateDefinitionDisplayText(ReportConfigurationDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(definition.Presentation?.Title))
            ReportConfigurationRules.ValidateSafeText(definition.Presentation!.Title, "报表标题");

        if (definition.ComputedColumns is not { Count: > 0 })
            return;

        foreach (var column in definition.ComputedColumns)
        {
            if (column is null)
                continue;
            if (!string.IsNullOrWhiteSpace(column.Key))
                ReportConfigurationRules.ValidateSafeText(column.Key, "计算列键");
            if (!string.IsNullOrWhiteSpace(column.Label))
                ReportConfigurationRules.ValidateSafeText(column.Label, "计算列标签");
        }
    }

    private static string SafeDisplayName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return FallbackName;
        return trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength] : trimmed;
    }

    private static ReportConfigurationDefinition? DeserializeDefinition(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<ReportConfigurationDefinition>(json, JsonOptions);

    private static void EnsureAuthenticated(long userId)
    {
        if (userId <= 0)
            throw new BusinessException("请先登录后再操作报表配置传输", ErrorCodes.Unauthorized);
    }
}


