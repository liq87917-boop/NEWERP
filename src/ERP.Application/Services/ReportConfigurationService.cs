using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace ERP.Application.Services;

/// <summary>
/// 私有报表配置平台服务（ERP-260 Stage 1）实现：可复用的应用服务，负责用户私有报表定义的
/// 「保存 / 列表 / 加载 / 重命名 / 复制 / 更新 / 软删除」以及「发布 / 恢复 / 修订列表」的不可变版本链。
/// <para>安全口径：所有者只来自服务端认证身份（客户端不得提交）；无身份 fail closed；跨所有者访问一律按不存在处理
/// （fail closed，不泄露存在性）；保存 / 加载 / 发布 / 恢复 / 复制 / 重命名都按当前账号重新校验数据集既有菜单授权，
/// 已保存定义本身绝不授予权限。</para>
/// <para>版本口径：<see cref="ReportConfiguration.Version"/> 为乐观「预期版本」令牌（每次变更 +1，
/// 客户端回传 expectedVersion，不一致即拒绝陈旧写入）；发布 / 恢复只追加不可变修订快照，草稿编辑不静默替换已发布修订。</para>
/// </summary>
public sealed class ReportConfigurationService : IReportConfigurationService
{
    private const int MaxNameLength = 200;
    private const int InitialVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IErpDbContext _db;
    private readonly IReportConfigurationCatalog _catalog;

    public ReportConfigurationService(IErpDbContext db, IReportConfigurationCatalog catalog)
    {
        _db = db;
        _catalog = catalog;
    }

    // ==================== 1. 保存（新增草稿） ====================

    public async Task<ReportConfigurationDto> CreateAsync(
        long ownerUserId, ReportConfigurationSaveDto dto, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(ownerUserId);
        ArgumentNullException.ThrowIfNull(dto);

        var name = NormalizeName(dto.Name);
        var definition = dto.Definition
            ?? throw BusinessException.InvalidParameter("报表定义不能为空");

        var (json, dataset) = await ValidateAndSerializeAsync(definition, ownerUserId, cancellationToken);

        var config = new ReportConfiguration
        {
            OwnerUserId = ownerUserId,
            Name = name,
            DatasetKey = dataset.DatasetKey,
            DefinitionJson = json,
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            Status = ReportConfigurationStatus.Draft,
            Version = InitialVersion,
            CurrentPublishedVersion = 0,
            CreatedAt = DateTime.Now,
            CreatedBy = ownerUserId,
            UpdatedBy = ownerUserId,
        };

        _db.ReportConfigurations.Add(config);
        await _db.SaveChangesAsync(cancellationToken);

        return Map(config, definition);
    }

    // ==================== 2. 更新草稿定义 / 名称 ====================

    public async Task<ReportConfigurationDto> UpdateAsync(
        long ownerUserId, long id, int expectedVersion, ReportConfigurationSaveDto dto,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(ownerUserId);
        ArgumentNullException.ThrowIfNull(dto);

        var config = await LoadOwnedAsync(ownerUserId, id, cancellationToken);
        EnsureExpectedVersion(config, expectedVersion);

        var name = NormalizeName(dto.Name);
        var definition = dto.Definition
            ?? throw BusinessException.InvalidParameter("报表定义不能为空");

        var (json, dataset) = await ValidateAndSerializeAsync(definition, ownerUserId, cancellationToken);

        config.Name = name;
        config.DatasetKey = dataset.DatasetKey;
        config.DefinitionJson = json;
        config.SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion;
        // 草稿编辑使工作定义与最近已发布修订分歧（不静默替换已发布修订）
        config.Status = ReportConfigurationStatus.Draft;
        config.Version++;
        config.UpdatedAt = DateTime.Now;
        config.UpdatedBy = ownerUserId;

        await SaveChangesWithConcurrencyGuardAsync(cancellationToken);

        return Map(config, definition);
    }


    // ==================== 3. 重命名 ====================

    public async Task<ReportConfigurationDto> RenameAsync(
        long ownerUserId, long id, int expectedVersion, string name,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(ownerUserId);

        var config = await LoadOwnedAsync(ownerUserId, id, cancellationToken);
        EnsureExpectedVersion(config, expectedVersion);
        await RequireCurrentDatasetAsync(config, ownerUserId, cancellationToken);

        config.Name = NormalizeName(name);
        config.Version++;
        config.UpdatedAt = DateTime.Now;
        config.UpdatedBy = ownerUserId;

        await SaveChangesWithConcurrencyGuardAsync(cancellationToken);

        return Map(config, Deserialize(config.DefinitionJson));
    }

    // ==================== 4. 复制为新的私有草稿 ====================

    public async Task<ReportConfigurationDto> CopyAsync(
        long ownerUserId, long id, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(ownerUserId);

        var source = await LoadOwnedAsync(ownerUserId, id, cancellationToken);
        await RequireCurrentDatasetAsync(source, ownerUserId, cancellationToken);

        var copyName = BuildCopyName(source.Name);
        var config = new ReportConfiguration
        {
            OwnerUserId = ownerUserId,
            Name = copyName,
            DatasetKey = source.DatasetKey,
            DefinitionJson = source.DefinitionJson,
            SchemaVersion = source.SchemaVersion,
            Status = ReportConfigurationStatus.Draft,
            Version = InitialVersion,
            CurrentPublishedVersion = 0,
            CreatedAt = DateTime.Now,
            CreatedBy = ownerUserId,
            UpdatedBy = ownerUserId,
        };

        _db.ReportConfigurations.Add(config);
        await _db.SaveChangesAsync(cancellationToken);

        return Map(config, Deserialize(source.DefinitionJson));
    }

    // ==================== 5. 加载 / 列表 ====================

    public async Task<ReportConfigurationDto> GetAsync(
        long ownerUserId, long id, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(ownerUserId);

        var config = await LoadOwnedAsync(ownerUserId, id, cancellationToken);
        await RequireCurrentDatasetAsync(config, ownerUserId, cancellationToken);

        return Map(config, Deserialize(config.DefinitionJson));
    }

    public async Task<List<ReportConfigurationSummaryDto>> ListAsync(
        long ownerUserId, CancellationToken cancellationToken = default)
    {
        var page = await ListPageAsync(ownerUserId, ReportConfigurationPaging.MaxPageSize, null, cancellationToken);
        return ReportConfigurationPaging.CompleteOrThrow(page, "报表配置列表");
    }

    public async Task<ReportConfigurationPage<ReportConfigurationSummaryDto>> ListPageAsync(
        long ownerUserId, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(ownerUserId);
        var pageSize = ReportConfigurationPaging.ValidateLimit(limit);
        var afterId = ReportConfigurationPaging.DecodeCursor(cursor);

        // 身份 / 软删除过滤在 SQL Take 之前应用；keyset 续读（最新在前）+ 多取一条用于判断是否还有后续
        var rows = await _db.ReportConfigurations.AsNoTracking()
            .Where(c => !c.IsDeleted && c.OwnerUserId == ownerUserId
                && (afterId == null || c.Id < afterId.Value))
            .OrderByDescending(c => c.Id)
            .Select(c => new ReportConfigurationSummaryDto
            {
                Id = c.Id,
                Name = c.Name,
                DatasetKey = c.DatasetKey,
                Status = c.Status,
                Version = c.Version,
                CurrentPublishedVersion = c.CurrentPublishedVersion,
                CreatedAt = c.CreatedAt,
                UpdatedAt = c.UpdatedAt,
            })
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > pageSize;
        if (hasMore)
            rows.RemoveAt(rows.Count - 1);

        var nextCursor = hasMore && rows.Count > 0
            ? ReportConfigurationPaging.EncodeCursor(rows[^1].Id)
            : null;

        return ReportConfigurationPaging.Page(rows, hasMore, nextCursor);
    }


    // ==================== 6. 软删除 ====================

    public async Task DeleteAsync(
        long ownerUserId, long id, int expectedVersion, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(ownerUserId);

        var config = await LoadOwnedWithRevisionsAsync(ownerUserId, id, cancellationToken);
        EnsureExpectedVersion(config, expectedVersion);

        var now = DateTime.Now;
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        config.IsDeleted = true;
        config.Version++;
        config.UpdatedAt = now;
        config.UpdatedBy = ownerUserId;

        foreach (var revision in config.Revisions.Where(r => !r.IsDeleted))
        {
            revision.IsDeleted = true;
            revision.UpdatedAt = now;
            revision.UpdatedBy = ownerUserId;
        }

        await SaveChangesWithConcurrencyGuardAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // ==================== 7. 发布 / 恢复 / 修订列表 ====================

    public async Task<ReportConfigurationDto> PublishAsync(
        long ownerUserId, long id, int expectedVersion, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(ownerUserId);

        var config = await LoadOwnedAsync(ownerUserId, id, cancellationToken);
        EnsureExpectedVersion(config, expectedVersion);

        var definition = Deserialize(config.DefinitionJson)
            ?? throw BusinessException.RuleConflict("报表配置定义缺失，无法发布");

        // 发布前按当前账号重新校验数据集授权与有界定义（失败即拒绝，不发布失效快照）
        await ValidateAndSerializeAsync(definition, ownerUserId, cancellationToken);

        var newVersion = config.CurrentPublishedVersion + 1;
        var revision = BuildRevision(config, newVersion, config.Name, config.DatasetKey,
            config.DefinitionJson, config.SchemaVersion, ownerUserId);

        var now = DateTime.Now;
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        // 先推进父配置：并发令牌 / rowversion 在此率先校验，败者在此抛出并发冲突，绝不先插入幽灵修订
        config.Status = ReportConfigurationStatus.Published;
        config.CurrentPublishedVersion = newVersion;
        config.Version++;
        config.UpdatedAt = now;
        config.UpdatedBy = ownerUserId;

        await SaveChangesWithConcurrencyGuardAsync(cancellationToken);

        _db.ReportConfigurationRevisions.Add(revision);
        await _db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return Map(config, definition);
    }


    public async Task<ReportConfigurationDto> RestoreAsync(
        long ownerUserId, long id, int expectedVersion, int versionNumber,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(ownerUserId);

        var config = await LoadOwnedAsync(ownerUserId, id, cancellationToken);
        EnsureExpectedVersion(config, expectedVersion);

        if (versionNumber <= 0 || versionNumber > config.CurrentPublishedVersion)
            throw BusinessException.InvalidParameter("要恢复的版本号无效");

        var target = await _db.ReportConfigurationRevisions.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ReportConfigurationId == id && !r.IsDeleted && r.Version == versionNumber,
                cancellationToken)
            ?? throw BusinessException.NotFound("要恢复的版本不存在");

        var historical = Deserialize(target.DefinitionJson)
            ?? throw BusinessException.RuleConflict("历史修订定义缺失，无法恢复");

        // 恢复前按当前账号重新校验数据集授权与有界定义（历史快照也必须仍然有效才可恢复）
        await ValidateAndSerializeAsync(historical, ownerUserId, cancellationToken);

        var newVersion = config.CurrentPublishedVersion + 1;
        var revision = BuildRevision(config, newVersion, config.Name, target.DatasetKey,
            target.DefinitionJson, target.SchemaVersion, ownerUserId);

        var now = DateTime.Now;
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        // 先推进父配置：并发令牌 / rowversion 在此率先校验，败者在此抛出并发冲突，绝不先插入幽灵修订
        // 恢复只回退定义内容（数据集键 / 定义 / schema 版本），不覆盖当前名称
        config.DatasetKey = target.DatasetKey;
        config.DefinitionJson = target.DefinitionJson;
        config.SchemaVersion = target.SchemaVersion;
        config.Status = ReportConfigurationStatus.Published;
        config.CurrentPublishedVersion = newVersion;
        config.Version++;
        config.UpdatedAt = now;
        config.UpdatedBy = ownerUserId;

        await SaveChangesWithConcurrencyGuardAsync(cancellationToken);

        _db.ReportConfigurationRevisions.Add(revision);
        await _db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return Map(config, historical);
    }

    public async Task<List<ReportConfigurationRevisionDto>> ListRevisionsAsync(
        long ownerUserId, long id, CancellationToken cancellationToken = default)
    {
        var page = await ListRevisionsPageAsync(ownerUserId, id, ReportConfigurationPaging.MaxPageSize, null, cancellationToken);
        return ReportConfigurationPaging.CompleteOrThrow(page, "发布修订列表");
    }

    public async Task<ReportConfigurationPage<ReportConfigurationRevisionDto>> ListRevisionsPageAsync(
        long ownerUserId, long id, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(ownerUserId);
        var pageSize = ReportConfigurationPaging.ValidateLimit(limit);
        var afterVersion = ReportConfigurationPaging.DecodeCursor(cursor);

        var config = await LoadOwnedAsync(ownerUserId, id, cancellationToken);
        await RequireCurrentDatasetAsync(config, ownerUserId, cancellationToken);

        // 身份 / 软删除过滤在 SQL Take 之前应用；修订在同一配置内按版本号稳定递增
        var revisions = await _db.ReportConfigurationRevisions.AsNoTracking()
            .Where(r => r.ReportConfigurationId == id && !r.IsDeleted
                && (afterVersion == null || r.Version > afterVersion.Value))
            .OrderBy(r => r.Version)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);

        var hasMore = revisions.Count > pageSize;
        if (hasMore)
            revisions.RemoveAt(revisions.Count - 1);

        var items = revisions.Select(r => new ReportConfigurationRevisionDto
        {
            Id = r.Id,
            ReportConfigurationId = r.ReportConfigurationId,
            Version = r.Version,
            Name = r.Name,
            DatasetKey = r.DatasetKey,
            SchemaVersion = r.SchemaVersion,
            PublishedAt = r.PublishedAt,
            PublishedBy = r.PublishedBy,
            Definition = Deserialize(r.DefinitionJson),
        }).ToList();

        var nextCursor = hasMore && revisions.Count > 0
            ? ReportConfigurationPaging.EncodeCursor(revisions[^1].Version)
            : null;

        return ReportConfigurationPaging.Page(items, hasMore, nextCursor);
    }


    // ==================== 内部辅助 ====================

    private static void EnsureAuthenticated(long ownerUserId)
    {
        if (ownerUserId <= 0)
            throw new BusinessException("请先登录后再操作报表配置", ErrorCodes.Unauthorized);
    }

    private static string NormalizeName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            throw BusinessException.InvalidParameter("报表配置名称不能为空");
        if (trimmed.Length > MaxNameLength)
            throw BusinessException.InvalidParameter($"报表配置名称不能超过 {MaxNameLength} 个字符");
        return trimmed;
    }

    private static string BuildCopyName(string sourceName)
    {
        const string suffix = " 副本";
        var baseName = sourceName.Length + suffix.Length > MaxNameLength
            ? sourceName[..(MaxNameLength - suffix.Length)]
            : sourceName;
        return NormalizeName(baseName + suffix);
    }

    private static void EnsureExpectedVersion(ReportConfiguration config, int expectedVersion)
    {
        if (config.Version != expectedVersion)
            throw BusinessException.RuleConflict("报表配置已被其他操作修改（预期版本不一致），请刷新后重试");
    }

    /// <summary>
    /// 落库并发守卫：把数据库级乐观并发失败（预期版本 / rowversion 冲突）统一映射为业务规则冲突。
    /// 绝不自动重试、绝不覆盖赢家状态，也不向调用方暴露提供程序消息 / 连接串 / SQL。
    /// </summary>
    private async Task SaveChangesWithConcurrencyGuardAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw BusinessException.RuleConflict("报表配置已被其他操作修改（并发冲突），请刷新后重试");
        }
    }

    private async Task<ReportConfigurationDatasetDto> RequireDatasetAsync(
        string datasetKey, long ownerUserId, CancellationToken cancellationToken)
    {
        var dataset = await _catalog.GetDatasetAsync(datasetKey, ownerUserId, cancellationToken);
        if (dataset is null)
        {
            throw new BusinessException(
                $"当前账号没有「{datasetKey}」数据集授权：拒绝操作该报表配置（fail closed）",
                ErrorCodes.Forbidden);
        }

        return dataset;
    }

    private Task<ReportConfigurationDatasetDto> RequireCurrentDatasetAsync(
        ReportConfiguration config, long ownerUserId, CancellationToken cancellationToken)
        => RequireDatasetAsync(config.DatasetKey, ownerUserId, cancellationToken);

    private async Task<(string Json, ReportConfigurationDatasetDto Dataset)> ValidateAndSerializeAsync(
        ReportConfigurationDefinition definition, long ownerUserId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var dataset = await RequireDatasetAsync(definition.DatasetKey, ownerUserId, cancellationToken);
        ReportConfigurationRules.Validate(definition, dataset);

        // 存储前归一化数据集键（大小写 / 空白），schema 版本已在校验中强制为当前版本
        definition.DatasetKey = dataset.DatasetKey;
        definition.SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion;

        return (JsonSerializer.Serialize(definition, JsonOptions), dataset);
    }

    private async Task<ReportConfiguration> LoadOwnedAsync(long ownerUserId, long id, CancellationToken cancellationToken)
    {
        EnsureAuthenticated(ownerUserId);
        if (id <= 0)
            throw BusinessException.InvalidParameter("请选择要操作的报表配置");

        return await _db.ReportConfigurations
            .FirstOrDefaultAsync(c => c.Id == id && !c.IsDeleted && c.OwnerUserId == ownerUserId, cancellationToken)
            ?? throw BusinessException.NotFound("报表配置不存在或无权访问");
    }

    private async Task<ReportConfiguration> LoadOwnedWithRevisionsAsync(
        long ownerUserId, long id, CancellationToken cancellationToken)
    {
        EnsureAuthenticated(ownerUserId);
        if (id <= 0)
            throw BusinessException.InvalidParameter("请选择要操作的报表配置");

        return await _db.ReportConfigurations
            .Include(c => c.Revisions)
            .FirstOrDefaultAsync(c => c.Id == id && !c.IsDeleted && c.OwnerUserId == ownerUserId, cancellationToken)
            ?? throw BusinessException.NotFound("报表配置不存在或无权访问");
    }

    private static ReportConfigurationRevision BuildRevision(
        ReportConfiguration config, int version, string name, string datasetKey,
        string definitionJson, int schemaVersion, long ownerUserId)
    {
        var now = DateTime.Now;
        return new ReportConfigurationRevision
        {
            ReportConfigurationId = config.Id,
            OwnerUserId = ownerUserId,
            Version = version,
            Name = name,
            DatasetKey = datasetKey,
            DefinitionJson = definitionJson,
            SchemaVersion = schemaVersion,
            PublishedAt = now,
            PublishedBy = ownerUserId,
            CreatedAt = now,
            CreatedBy = ownerUserId,
            UpdatedBy = ownerUserId,
        };
    }

    private static ReportConfigurationDto Map(ReportConfiguration config, ReportConfigurationDefinition? definition)
        => new()
        {
            Id = config.Id,
            OwnerUserId = config.OwnerUserId,
            Name = config.Name,
            DatasetKey = config.DatasetKey,
            SchemaVersion = config.SchemaVersion,
            Status = config.Status,
            Version = config.Version,
            CurrentPublishedVersion = config.CurrentPublishedVersion,
            Definition = definition,
            CreatedAt = config.CreatedAt,
            UpdatedAt = config.UpdatedAt,
        };

    private static ReportConfigurationDefinition? Deserialize(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<ReportConfigurationDefinition>(json, JsonOptions);
}

