using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace ERP.Application.Services;

/// <summary>
/// 通用报表配置平台（ERP-265 Stage 1）只读共享授权服务实现：所有者以「被授权人用户 Id + 固定发布修订版本号」
/// 授予 / 撤销单个不可变已发布快照的只读访问；被授权人只读取固定快照、复制为自有草稿。
/// <para>安全口径：所有者 / 被授权人都只来自服务端认证身份；跨所有者 / 跨被授权人 fail closed（按不存在处理）；
/// 授权 / 撤销 / 列出 / 复制都按当前账号重新校验数据集既有菜单授权，已保存定义本身绝不授予权限。</para>
/// <para>固定修订口径：授权记录的 <see cref="ReportConfigurationGrant.RevisionVersion"/> 是唯一 pin；
/// 所有者再次发布不会改变 pin；变更 pin 是显式所有者动作（Grant 时携带当前预期版本）。</para>
/// </summary>
public sealed class ReportConfigurationSharingService : IReportConfigurationSharingService
{
    private const int InitialVersion = 1;
    private const int MaxNameLength = 200;
    private const string CopySuffix = " 副本";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IErpDbContext _db;
    private readonly IReportConfigurationCatalog _catalog;

    public ReportConfigurationSharingService(IErpDbContext db, IReportConfigurationCatalog catalog)
    {
        _db = db;
        _catalog = catalog;
    }
    // ==================== owner-only 管理 ====================

    public async Task<List<ReportConfigurationGrantDto>> ListGrantsAsync(
        long ownerUserId, long configurationId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(ownerUserId);
        await LoadOwnedAsync(ownerUserId, configurationId, cancellationToken);

        var grants = await _db.ReportConfigurationGrants.AsNoTracking()
            .Where(g => g.ReportConfigurationId == configurationId && !g.IsDeleted)
            .OrderBy(g => g.Id)
            .ToListAsync(cancellationToken);

        var users = await LoadUsersAsync(grants.Select(g => g.RecipientUserId).Distinct().ToList(), cancellationToken);

        return grants.Select(g => MapGrant(g, users)).ToList();
    }

    public async Task<ReportConfigurationGrantDto> GrantAsync(
        long ownerUserId, long configurationId, ReportConfigurationGrantRequestDto request,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(ownerUserId);
        ArgumentNullException.ThrowIfNull(request);
        if (request.RecipientUserId <= 0)
            throw BusinessException.InvalidParameter("请选择要授权的用户");
        if (request.RevisionVersion <= 0)
            throw BusinessException.InvalidParameter("请选择要共享的发布修订版本");

        var config = await LoadOwnedAsync(ownerUserId, configurationId, cancellationToken);

        // 被授权人必须是现有激活 ERP 用户（按 Id 校验存在性与启用状态，不提供用户目录检索）
        var recipient = await _db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == request.RecipientUserId && !u.IsDeleted, cancellationToken)
            ?? throw BusinessException.InvalidParameter("要授权的用户不存在");
        if (recipient.Status != UserStatus.Enabled)
            throw BusinessException.InvalidParameter("要授权的用户已被停用，无法授权");

        // 固定修订必须是已发布、未删除的快照
        if (request.RevisionVersion > config.CurrentPublishedVersion)
            throw BusinessException.InvalidParameter("要共享的发布修订版本不存在或尚未发布");
        var revisionExists = await _db.ReportConfigurationRevisions.AsNoTracking()
            .AnyAsync(r => r.ReportConfigurationId == configurationId
                && !r.IsDeleted && r.Version == request.RevisionVersion, cancellationToken);
        if (!revisionExists)
            throw BusinessException.InvalidParameter("要共享的发布修订版本不存在");

        var existing = await _db.ReportConfigurationGrants
            .FirstOrDefaultAsync(g => g.ReportConfigurationId == configurationId
                && g.RecipientUserId == request.RecipientUserId && !g.IsDeleted, cancellationToken);

        var now = DateTime.Now;
        if (existing is not null)
        {
            // 变更固定修订（pin）是显式所有者动作：必须携带当前预期版本，否则拒绝陈旧写入
            if (request.ExpectedVersion is null)
                throw BusinessException.RuleConflict("该用户已被授权，请刷新后重试（变更固定修订需携带当前授权版本）");
            EnsureExpectedVersion(existing, request.ExpectedVersion.Value);

            existing.RevisionVersion = request.RevisionVersion;
            existing.GrantedByUserId = ownerUserId;
            existing.Version++;
            existing.UpdatedAt = now;
            existing.UpdatedBy = ownerUserId;
            await _db.SaveChangesAsync(cancellationToken);
            return MapGrant(existing, new Dictionary<long, SysUser> { [recipient.Id] = recipient });
        }

        if (request.ExpectedVersion is not null)
            throw BusinessException.RuleConflict("该用户已不存在有效授权（可能已被撤销），请刷新后重试");

        var grant = new ReportConfigurationGrant
        {
            RecipientUserId = request.RecipientUserId,
            ReportConfigurationId = configurationId,
            RevisionVersion = request.RevisionVersion,
            GrantedByUserId = ownerUserId,
            Version = InitialVersion,
            CreatedAt = now,
            CreatedBy = ownerUserId,
            UpdatedBy = ownerUserId,
        };
        _db.ReportConfigurationGrants.Add(grant);
        await _db.SaveChangesAsync(cancellationToken);

        return MapGrant(grant, new Dictionary<long, SysUser> { [recipient.Id] = recipient });
    }

    public async Task RevokeAsync(
        long ownerUserId, long configurationId, long recipientUserId, int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(ownerUserId);
        await LoadOwnedAsync(ownerUserId, configurationId, cancellationToken);
        if (recipientUserId <= 0)
            throw BusinessException.InvalidParameter("请选择要撤销授权的用户");

        var grant = await _db.ReportConfigurationGrants
            .FirstOrDefaultAsync(g => g.ReportConfigurationId == configurationId
                && g.RecipientUserId == recipientUserId && !g.IsDeleted, cancellationToken)
            ?? throw BusinessException.NotFound("该用户没有有效授权");

        EnsureExpectedVersion(grant, expectedVersion);

        var now = DateTime.Now;
        grant.IsDeleted = true;
        grant.Version++;
        grant.UpdatedAt = now;
        grant.UpdatedBy = ownerUserId;
        await _db.SaveChangesAsync(cancellationToken);
    }

    // ==================== 被授权人只读 / 复制 ====================

    public async Task<List<ReportConfigurationSharedSummaryDto>> ListSharedAsync(
        long recipientUserId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(recipientUserId);
        if (!await IsActiveUserAsync(recipientUserId, cancellationToken))
            return new List<ReportConfigurationSharedSummaryDto>();

        var grants = await _db.ReportConfigurationGrants.AsNoTracking()
            .Where(g => g.RecipientUserId == recipientUserId && !g.IsDeleted)
            .OrderBy(g => g.Id)
            .ToListAsync(cancellationToken);

        var result = new List<ReportConfigurationSharedSummaryDto>();
        foreach (var grant in grants)
        {
            var snapshot = await ResolveSharedSnapshotAsync(grant, cancellationToken);
            if (snapshot is null)
                continue; // 配置被删除 / 固定修订丢失 → 该共享不再可见（fail closed）

            result.Add(new ReportConfigurationSharedSummaryDto
            {
                ReportConfigurationId = snapshot.ReportConfigurationId,
                Name = snapshot.Name,
                DatasetKey = snapshot.DatasetKey,
                RevisionVersion = snapshot.RevisionVersion,
                PublishedAt = snapshot.PublishedAt,
                OwnerUserId = snapshot.OwnerUserId,
                OwnerDisplayName = snapshot.OwnerDisplayName,
            });
        }

        return result;
    }

    public async Task<ReportConfigurationSharedDetailDto> GetSharedAsync(
        long recipientUserId, long configurationId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(recipientUserId);
        await EnsureActiveRecipientAsync(recipientUserId, cancellationToken);
        if (configurationId <= 0)
            throw BusinessException.InvalidParameter("请选择要查看的共享报表");

        var grant = await LoadRecipientGrantAsync(recipientUserId, configurationId, cancellationToken);
        var snapshot = await ResolveSharedSnapshotAsync(grant, cancellationToken)
            ?? throw BusinessException.NotFound("共享报表不存在或无权访问");

        return new ReportConfigurationSharedDetailDto
        {
            ReportConfigurationId = snapshot.ReportConfigurationId,
            Name = snapshot.Name,
            DatasetKey = snapshot.DatasetKey,
            RevisionVersion = snapshot.RevisionVersion,
            SchemaVersion = snapshot.SchemaVersion,
            PublishedAt = snapshot.PublishedAt,
            Definition = snapshot.Definition,
        };
    }

    public async Task<ReportConfigurationDto> CopySharedAsync(
        long recipientUserId, long configurationId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(recipientUserId);
        await EnsureActiveRecipientAsync(recipientUserId, cancellationToken);
        if (configurationId <= 0)
            throw BusinessException.InvalidParameter("请选择要复制的共享报表");

        var grant = await LoadRecipientGrantAsync(recipientUserId, configurationId, cancellationToken);
        var snapshot = await ResolveSharedSnapshotAsync(grant, cancellationToken)
            ?? throw BusinessException.NotFound("共享报表不存在或无权访问");

        var definition = snapshot.Definition
            ?? throw BusinessException.RuleConflict("共享发布修订定义缺失，无法复制");

        // 新鲜校验：按被授权人当前数据集授权与有界定义重新校验，绝不信任所有者保存时的校验结果
        var dataset = await _catalog.GetDatasetAsync(snapshot.DatasetKey, recipientUserId, cancellationToken);
        if (dataset is null)
        {
            throw new BusinessException(
                $"当前账号没有「{snapshot.DatasetKey}」数据集授权：拒绝复制该共享报表（fail closed）",
                ErrorCodes.Forbidden);
        }

        ReportConfigurationRules.Validate(definition, dataset);

        // 存储前归一化数据集键与 schema 版本（与 ReportConfigurationService 同口径）
        definition.DatasetKey = dataset.DatasetKey;
        definition.SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion;
        var json = JsonSerializer.Serialize(definition, JsonOptions);

        var config = new ReportConfiguration
        {
            OwnerUserId = recipientUserId,
            Name = BuildCopyName(snapshot.Name),
            DatasetKey = dataset.DatasetKey,
            DefinitionJson = json,
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            Status = ReportConfigurationStatus.Draft,
            Version = InitialVersion,
            CurrentPublishedVersion = 0,
            CreatedAt = DateTime.Now,
            CreatedBy = recipientUserId,
            UpdatedBy = recipientUserId,
        };
        _db.ReportConfigurations.Add(config);
        await _db.SaveChangesAsync(cancellationToken);

        return Map(config, definition);
    }

    // ==================== 内部辅助 ====================

    private sealed class ResolvedShared
    {
        public long ReportConfigurationId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string DatasetKey { get; set; } = string.Empty;
        public int RevisionVersion { get; set; }
        public int SchemaVersion { get; set; }
        public DateTime PublishedAt { get; set; }
        public long OwnerUserId { get; set; }
        public string OwnerDisplayName { get; set; } = string.Empty;
        public ReportConfigurationDefinition? Definition { get; set; }
    }

    private async Task<ResolvedShared?> ResolveSharedSnapshotAsync(
        ReportConfigurationGrant grant, CancellationToken cancellationToken)
    {
        var config = await _db.ReportConfigurations.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == grant.ReportConfigurationId && !c.IsDeleted, cancellationToken);
        if (config is null)
            return null;

        var revision = await _db.ReportConfigurationRevisions.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ReportConfigurationId == config.Id
                && !r.IsDeleted && r.Version == grant.RevisionVersion, cancellationToken);
        if (revision is null)
            return null;

        var definition = Deserialize(revision.DefinitionJson);
        if (definition is null)
            return null;

        var owner = await _db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == config.OwnerUserId, cancellationToken);
        var ownerDisplayName = owner is null
            ? string.Empty
            : (string.IsNullOrWhiteSpace(owner.DisplayName) ? owner.UserName : owner.DisplayName);

        return new ResolvedShared
        {
            ReportConfigurationId = config.Id,
            Name = revision.Name,
            DatasetKey = revision.DatasetKey,
            RevisionVersion = revision.Version,
            SchemaVersion = revision.SchemaVersion,
            PublishedAt = revision.PublishedAt,
            OwnerUserId = config.OwnerUserId,
            OwnerDisplayName = ownerDisplayName,
            Definition = definition,
        };
    }

    private static void EnsureAuthenticated(long userId)
    {
        if (userId <= 0)
            throw new BusinessException("请先登录后再操作共享报表配置", ErrorCodes.Unauthorized);
    }

    private async Task<ReportConfiguration> LoadOwnedAsync(
        long ownerUserId, long configurationId, CancellationToken cancellationToken)
    {
        EnsureAuthenticated(ownerUserId);
        if (configurationId <= 0)
            throw BusinessException.InvalidParameter("请选择要操作的报表配置");

        return await _db.ReportConfigurations
            .FirstOrDefaultAsync(c => c.Id == configurationId && !c.IsDeleted && c.OwnerUserId == ownerUserId, cancellationToken)
            ?? throw BusinessException.NotFound("报表配置不存在或无权访问");
    }

    private async Task<ReportConfigurationGrant> LoadRecipientGrantAsync(
        long recipientUserId, long configurationId, CancellationToken cancellationToken)
    {
        return await _db.ReportConfigurationGrants
            .FirstOrDefaultAsync(g => g.RecipientUserId == recipientUserId
                && g.ReportConfigurationId == configurationId && !g.IsDeleted, cancellationToken)
            ?? throw BusinessException.NotFound("共享报表不存在或无权访问");
    }

    private async Task EnsureActiveRecipientAsync(long userId, CancellationToken cancellationToken)
    {
        if (!await IsActiveUserAsync(userId, cancellationToken))
            throw BusinessException.NotFound("账号不存在或已停用");
    }

    private async Task<bool> IsActiveUserAsync(long userId, CancellationToken cancellationToken)
    {
        var user = await _db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, cancellationToken);
        return user is not null && user.Status == UserStatus.Enabled;
    }

    private async Task<Dictionary<long, SysUser>> LoadUsersAsync(
        IReadOnlyList<long> ids, CancellationToken cancellationToken)
    {
        if (ids is null || ids.Count == 0)
            return new Dictionary<long, SysUser>();

        return await _db.SysUsers.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, cancellationToken);
    }

    private static void EnsureExpectedVersion(ReportConfigurationGrant grant, int expectedVersion)
    {
        if (grant.Version != expectedVersion)
            throw BusinessException.RuleConflict("授权已被其他操作修改（预期版本不一致），请刷新后重试");
    }

    private static ReportConfigurationGrantDto MapGrant(
        ReportConfigurationGrant grant, IReadOnlyDictionary<long, SysUser> users)
    {
        users.TryGetValue(grant.RecipientUserId, out var user);
        return new ReportConfigurationGrantDto
        {
            Id = grant.Id,
            ReportConfigurationId = grant.ReportConfigurationId,
            RecipientUserId = grant.RecipientUserId,
            RecipientUserName = user?.UserName ?? string.Empty,
            RecipientDisplayName = user?.DisplayName ?? string.Empty,
            RevisionVersion = grant.RevisionVersion,
            Version = grant.Version,
            GrantedByUserId = grant.GrantedByUserId,
            CreatedAt = grant.CreatedAt,
            UpdatedAt = grant.UpdatedAt,
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

    private static string BuildCopyName(string sourceName)
    {
        var baseName = sourceName.Length + CopySuffix.Length > MaxNameLength
            ? sourceName[..(MaxNameLength - CopySuffix.Length)]
            : sourceName;
        var name = (baseName + CopySuffix).Trim();
        return string.IsNullOrWhiteSpace(name) ? $"共享报表{CopySuffix}" : name;
    }

    private static ReportConfigurationDefinition? Deserialize(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<ReportConfigurationDefinition>(json, JsonOptions);
}
