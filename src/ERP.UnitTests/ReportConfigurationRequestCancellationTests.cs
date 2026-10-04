using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Reports;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-282 Stage 1：通用报表配置生命周期 API 的请求取消传播单元测试（直接实例化控制器，不连接 SQL Server）：
/// 覆盖每个端点族把 <see cref="Microsoft.AspNetCore.Http.HttpContext.RequestAborted"/> 原样传给既有应用接口、
/// 预取消请求在开始工作前不落草稿 / 修订 / 授权、未取消正常成功，以及非取消的业务失败仍按原错误码拒绝。
/// </summary>
public class ReportConfigurationRequestCancellationTests
{
    // ==================== 0. 脚手架（复用 API 测试口径） ====================

    private static SysUser SeedUser(ErpDbContext db, string userName)
    {
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = userName,
            Status = UserStatus.Enabled,
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        return user;
    }

    private static SysRole SeedRole(ErpDbContext db, string suffix)
    {
        var role = new SysRole
        {
            RoleName = $"Role-{suffix}",
            RoleCode = $"Role-{suffix}-{Guid.NewGuid():N}",
            IsSystem = false,
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        return role;
    }

    private static void SeedUserRole(ErpDbContext db, long userId, long roleId)
    {
        db.SysUserRoles.Add(new SysUserRole { UserId = userId, RoleId = roleId });
        db.SaveChanges();
    }

    private static SysMenu SeedMenu(ErpDbContext db, string code)
    {
        var menu = new SysMenu { MenuName = code, MenuCode = code, MenuType = MenuType.Menu };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    private static void SeedRoleMenu(ErpDbContext db, long roleId, long menuId)
    {
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
        db.SaveChanges();
    }

    private static long SeedAuthorizedUser(ErpDbContext db, string userName, string menuCode)
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, menuCode).Id);
        return user.Id;
    }

    private static IReadOnlyList<IReportConfigurationDatasetProvider> BuildProviders(ErpDbContext db)
        => new IReportConfigurationDatasetProvider[]
        {
            new SalesOrderReportConfigurationDatasetProvider(new DynamicSalesOrderReportQuery(db)),
            new ReceivableReportConfigurationDatasetProvider(new DynamicReceivableReportQuery(db)),
        };

    private static ReportConfigurationsController BuildController(ErpDbContext db)
        => new(
            new ReportConfigurationCatalog(BuildProviders(db)),
            new ReportConfigurationService(db, new ReportConfigurationCatalog(BuildProviders(db))),
            new ReportConfigurationExecutionService(db, BuildProviders(db)),
            new ReportConfigurationSharingService(db, new ReportConfigurationCatalog(BuildProviders(db))));

    private static ReportConfigurationsController BuildControllerWithBudget(
        ErpDbContext db, IReportConfigurationExecutionBudget budget)
        => new(
            new ReportConfigurationCatalog(BuildProviders(db)),
            new ReportConfigurationService(db, new ReportConfigurationCatalog(BuildProviders(db))),
            new ReportConfigurationExecutionService(db, BuildProviders(db), budget: budget),
            new ReportConfigurationSharingService(db, new ReportConfigurationCatalog(BuildProviders(db))),
            budget);

    private static ReportConfigurationSaveDto SaveDto(string name)
        => new() { Name = name, Definition = SalesOrderDefinition() };

    private static ReportConfigurationDefinition SalesOrderDefinition()
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
            Fields = new List<string> { "orderNo", "totalAmount", "currency" },
            Filters = new List<ReportConfigurationFilter>(),
            Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
            Aggregates = new List<ReportConfigurationAggregate>(),
            Capabilities = new List<string>(),
        };

    private static long CreatedId(IActionResult result)
        => Assert.IsType<ApiResponse<ReportConfigurationDto>>(
            Assert.IsType<OkObjectResult>(result).Value).Data!.Id;

    // ==================== 1. 令牌捕获 fakes（仅记录调用时收到的取消令牌） ====================

    private sealed class RecordingCatalog : IReportConfigurationCatalog
    {
        public CancellationToken LastToken { get; private set; }

        public Task<ReportConfigurationCatalogDto> GetCatalogAsync(long? userId, CancellationToken cancellationToken = default)
        {
            LastToken = cancellationToken;
            return Task.FromResult<ReportConfigurationCatalogDto>(null!);
        }

        public Task<ReportConfigurationDatasetDto?> GetDatasetAsync(string datasetKey, long? userId, CancellationToken cancellationToken = default)
        {
            LastToken = cancellationToken;
            return Task.FromResult<ReportConfigurationDatasetDto?>(null);
        }
    }

    private sealed class RecordingService : IReportConfigurationService
    {
        public CancellationToken LastToken { get; private set; }

        public Task<ReportConfigurationDto> CreateAsync(long ownerUserId, ReportConfigurationSaveDto dto, CancellationToken cancellationToken = default)
        { LastToken = cancellationToken; return Task.FromResult<ReportConfigurationDto>(null!); }

        public Task<ReportConfigurationDto> UpdateAsync(long ownerUserId, long id, int expectedVersion, ReportConfigurationSaveDto dto, CancellationToken cancellationToken = default)
        { LastToken = cancellationToken; return Task.FromResult<ReportConfigurationDto>(null!); }

        public Task<ReportConfigurationDto> RenameAsync(long ownerUserId, long id, int expectedVersion, string name, CancellationToken cancellationToken = default)
        { LastToken = cancellationToken; return Task.FromResult<ReportConfigurationDto>(null!); }

        public Task<ReportConfigurationDto> CopyAsync(long ownerUserId, long id, CancellationToken cancellationToken = default)
        { LastToken = cancellationToken; return Task.FromResult<ReportConfigurationDto>(null!); }

        public Task<ReportConfigurationDto> GetAsync(long ownerUserId, long id, CancellationToken cancellationToken = default)
        { LastToken = cancellationToken; return Task.FromResult<ReportConfigurationDto>(null!); }

        public Task<List<ReportConfigurationSummaryDto>> ListAsync(long ownerUserId, CancellationToken cancellationToken = default)
        { LastToken = cancellationToken; return Task.FromResult(new List<ReportConfigurationSummaryDto>()); }

        public Task<ReportConfigurationPage<ReportConfigurationSummaryDto>> ListPageAsync(long ownerUserId, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
        { LastToken = cancellationToken; return Task.FromResult<ReportConfigurationPage<ReportConfigurationSummaryDto>>(null!); }

        public Task DeleteAsync(long ownerUserId, long id, int expectedVersion, CancellationToken cancellationToken = default)
        { LastToken = cancellationToken; return Task.CompletedTask; }

        public Task<ReportConfigurationDto> PublishAsync(long ownerUserId, long id, int expectedVersion, CancellationToken cancellationToken = default)
        { LastToken = cancellationToken; return Task.FromResult<ReportConfigurationDto>(null!); }

        public Task<ReportConfigurationDto> RestoreAsync(long ownerUserId, long id, int expectedVersion, int versionNumber, CancellationToken cancellationToken = default)
        { LastToken = cancellationToken; return Task.FromResult<ReportConfigurationDto>(null!); }

        public Task<List<ReportConfigurationRevisionDto>> ListRevisionsAsync(long ownerUserId, long id, CancellationToken cancellationToken = default)
        { LastToken = cancellationToken; return Task.FromResult(new List<ReportConfigurationRevisionDto>()); }

        public Task<ReportConfigurationPage<ReportConfigurationRevisionDto>> ListRevisionsPageAsync(long ownerUserId, long id, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
        { LastToken = cancellationToken; return Task.FromResult<ReportConfigurationPage<ReportConfigurationRevisionDto>>(null!); }
    }

    private sealed class RecordingSharing : IReportConfigurationSharingService
    {
        public CancellationToken LastToken { get; private set; }

        public Task<List<ReportConfigurationGrantDto>> ListGrantsAsync(long ownerUserId, long configurationId, CancellationToken cancellationToken = default)
        { LastToken = cancellationToken; return Task.FromResult(new List<ReportConfigurationGrantDto>()); }

        public Task<ReportConfigurationPage<ReportConfigurationGrantDto>> ListGrantsPageAsync(long ownerUserId, long configurationId, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
        { LastToken = cancellationToken; return Task.FromResult<ReportConfigurationPage<ReportConfigurationGrantDto>>(null!); }

        public Task<ReportConfigurationGrantDto> GrantAsync(long ownerUserId, long configurationId, ReportConfigurationGrantRequestDto request, CancellationToken cancellationToken = default)
        { LastToken = cancellationToken; return Task.FromResult<ReportConfigurationGrantDto>(null!); }

        public Task RevokeAsync(long ownerUserId, long configurationId, long recipientUserId, int expectedVersion, CancellationToken cancellationToken = default)
        { LastToken = cancellationToken; return Task.CompletedTask; }

        public Task<List<ReportConfigurationSharedSummaryDto>> ListSharedAsync(long recipientUserId, CancellationToken cancellationToken = default)
        { LastToken = cancellationToken; return Task.FromResult(new List<ReportConfigurationSharedSummaryDto>()); }

        public Task<ReportConfigurationPage<ReportConfigurationSharedSummaryDto>> ListSharedPageAsync(long recipientUserId, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
        { LastToken = cancellationToken; return Task.FromResult<ReportConfigurationPage<ReportConfigurationSharedSummaryDto>>(null!); }

        public Task<ReportConfigurationSharedDetailDto> GetSharedAsync(long recipientUserId, long configurationId, CancellationToken cancellationToken = default)
        { LastToken = cancellationToken; return Task.FromResult<ReportConfigurationSharedDetailDto>(null!); }

        public Task<ReportConfigurationDto> CopySharedAsync(long recipientUserId, long configurationId, CancellationToken cancellationToken = default)
        { LastToken = cancellationToken; return Task.FromResult<ReportConfigurationDto>(null!); }
    }

    private sealed class RecordingTransfer : IReportConfigurationTransferService
    {
        public CancellationToken LastToken { get; private set; }

        public Task<ReportConfigurationTransferEnvelopeDto> ExportAsync(long userId, ReportConfigurationTransferExportRequest request, CancellationToken cancellationToken = default)
        { LastToken = cancellationToken; return Task.FromResult<ReportConfigurationTransferEnvelopeDto>(null!); }

        public Task<ReportConfigurationDto> ImportAsync(long userId, ReportConfigurationTransferImportRequest request, CancellationToken cancellationToken = default)
        { LastToken = cancellationToken; return Task.FromResult<ReportConfigurationDto>(null!); }
    }

    private sealed class StubExecution : IReportConfigurationExecutionService
    {
        public Task<ReportConfigurationPreviewDto> PreviewAsync(long ownerUserId, ReportConfigurationPreviewRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<ReportConfigurationPreviewDto>(null!);

        public Task<ReportConfigurationPreviewDto> PreviewAsync(long ownerUserId, ReportConfigurationPreviewRequest request, IReportConfigurationExecutionLease lease)
            => Task.FromResult<ReportConfigurationPreviewDto>(null!);

        public Task<ReportConfigurationExportResultDto> BuildExportResultAsync(long ownerUserId, ReportConfigurationPreviewRequest request, IReportConfigurationExecutionLease lease)
            => Task.FromResult<ReportConfigurationExportResultDto>(null!);
    }

    private sealed class RecordingExecution : IReportConfigurationExecutionService
    {
        public CancellationToken LastToken { get; private set; }

        public Task<ReportConfigurationPreviewDto> PreviewAsync(long ownerUserId, ReportConfigurationPreviewRequest request, CancellationToken cancellationToken = default)
        {
            LastToken = cancellationToken;
            return Task.FromResult<ReportConfigurationPreviewDto>(null!);
        }

        public Task<ReportConfigurationPreviewDto> PreviewAsync(long ownerUserId, ReportConfigurationPreviewRequest request, IReportConfigurationExecutionLease lease)
            => Task.FromResult<ReportConfigurationPreviewDto>(null!);

        public Task<ReportConfigurationExportResultDto> BuildExportResultAsync(long ownerUserId, ReportConfigurationPreviewRequest request, IReportConfigurationExecutionLease lease)
            => Task.FromResult<ReportConfigurationExportResultDto>(null!);
    }

    private sealed class SentinelExecutionDatasetProvider : IReportConfigurationDatasetProvider
    {
        public bool PreviewInvoked { get; private set; }

        public string DatasetKey => ReportConfigurationConstants.DatasetSalesOrder;

        public Task<ReportConfigurationDatasetDto?> GetDatasetAsync(long? userId, CancellationToken cancellationToken = default)
            => Task.FromResult<ReportConfigurationDatasetDto?>(null);

        public Task<ReportConfigurationPreviewDto> PreviewAsync(
            ReportConfigurationDefinition definition,
            ReportConfigurationPreviewParameters parameters,
            long? userId,
            CancellationToken cancellationToken = default)
        {
            PreviewInvoked = true;
            throw new InvalidOperationException("数据集查询不应在预取消请求中执行");
        }
    }

    private static ReportConfigurationsController BuildRecordingController(
        RecordingCatalog catalog,
        RecordingService service,
        RecordingSharing sharing,
        RecordingTransfer transfer,
        long userId,
        IReportConfigurationExecutionService? execution = null)
    {
        var ctl = new ReportConfigurationsController(
            catalog, service, execution ?? new StubExecution(), sharing, transfer: transfer);
        TestAuth.SetUser(ctl, userId);
        return ctl;
    }

    // ==================== 2. 每个端点族原样传递 HttpContext.RequestAborted ====================

    [Fact]
    public async Task Catalog_传递RequestAborted给目录接口()
    {
        var catalog = new RecordingCatalog();
        var service = new RecordingService();
        var sharing = new RecordingSharing();
        var transfer = new RecordingTransfer();
        var ctl = BuildRecordingController(catalog, service, sharing, transfer, 42);
        using var cts = new CancellationTokenSource();
        ctl.HttpContext.RequestAborted = cts.Token;

        await ctl.Catalog();

        Assert.Equal(cts.Token, catalog.LastToken);
    }

    [Fact]
    public async Task List_传递RequestAborted给自有分页接口()
    {
        var service = new RecordingService();
        var ctl = BuildRecordingController(new RecordingCatalog(), service, new RecordingSharing(), new RecordingTransfer(), 42);
        using var cts = new CancellationTokenSource();
        ctl.HttpContext.RequestAborted = cts.Token;

        await ctl.List(null, null);

        Assert.Equal(cts.Token, service.LastToken);
    }

    [Fact]
    public async Task Create_传递RequestAborted给保存接口()
    {
        var service = new RecordingService();
        var ctl = BuildRecordingController(new RecordingCatalog(), service, new RecordingSharing(), new RecordingTransfer(), 42);
        using var cts = new CancellationTokenSource();
        ctl.HttpContext.RequestAborted = cts.Token;

        await ctl.Create(SaveDto("草稿"));

        Assert.Equal(cts.Token, service.LastToken);
    }

    [Fact]
    public async Task Publish_传递RequestAborted给版本接口()
    {
        var service = new RecordingService();
        var ctl = BuildRecordingController(new RecordingCatalog(), service, new RecordingSharing(), new RecordingTransfer(), 42);
        using var cts = new CancellationTokenSource();
        ctl.HttpContext.RequestAborted = cts.Token;

        await ctl.Publish(1, 1);

        Assert.Equal(cts.Token, service.LastToken);
    }

    [Fact]
    public async Task Grants_传递RequestAborted给授权分页接口()
    {
        var sharing = new RecordingSharing();
        var ctl = BuildRecordingController(new RecordingCatalog(), new RecordingService(), sharing, new RecordingTransfer(), 42);
        using var cts = new CancellationTokenSource();
        ctl.HttpContext.RequestAborted = cts.Token;

        await ctl.Grants(1, null, null);

        Assert.Equal(cts.Token, sharing.LastToken);
    }

    [Fact]
    public async Task Grant_传递RequestAborted给授权写接口()
    {
        var sharing = new RecordingSharing();
        var ctl = BuildRecordingController(new RecordingCatalog(), new RecordingService(), sharing, new RecordingTransfer(), 42);
        using var cts = new CancellationTokenSource();
        ctl.HttpContext.RequestAborted = cts.Token;

        await ctl.Grant(1, new ReportConfigurationGrantRequestDto { RecipientUserId = 2, RevisionVersion = 1 });

        Assert.Equal(cts.Token, sharing.LastToken);
    }

    [Fact]
    public async Task Shared_传递RequestAborted给被共享分页接口()
    {
        var sharing = new RecordingSharing();
        var ctl = BuildRecordingController(new RecordingCatalog(), new RecordingService(), sharing, new RecordingTransfer(), 42);
        using var cts = new CancellationTokenSource();
        ctl.HttpContext.RequestAborted = cts.Token;

        await ctl.Shared(null, null);

        Assert.Equal(cts.Token, sharing.LastToken);
    }

    [Fact]
    public async Task ExportDefinition_传递RequestAborted给传输导出接口()
    {
        var transfer = new RecordingTransfer();
        var ctl = BuildRecordingController(new RecordingCatalog(), new RecordingService(), new RecordingSharing(), transfer, 42);
        using var cts = new CancellationTokenSource();
        ctl.HttpContext.RequestAborted = cts.Token;

        await ctl.ExportDefinition(new ReportConfigurationTransferExportRequest { ConfigurationId = 1 });

        Assert.Equal(cts.Token, transfer.LastToken);
    }

    [Fact]
    public async Task Preview_传递RequestAborted给预览接口()
    {
        var execution = new RecordingExecution();
        var ctl = BuildRecordingController(new RecordingCatalog(), new RecordingService(), new RecordingSharing(), new RecordingTransfer(), 42, execution);
        using var cts = new CancellationTokenSource();
        ctl.HttpContext.RequestAborted = cts.Token;

        await ctl.Preview(new ReportConfigurationPreviewRequest { ConfigurationId = 1 });

        Assert.Equal(cts.Token, execution.LastToken);
    }

    // ==================== 3. 预取消：开始工作前不落草稿 / 修订 / 授权 ====================

    [Fact]
    public async Task Create_预取消_抛已取消且不落草稿()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "owner", "sales-order");
        var ctl = BuildController(db);
        TestAuth.SetUser(ctl, user);
        ctl.HttpContext.RequestAborted = new CancellationToken(canceled: true);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(SaveDto("草稿")));

        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeCancelled, ex.Code);
        Assert.Empty(db.ReportConfigurations);
    }

    [Fact]
    public async Task Publish_预取消_抛已取消且不落修订()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "owner", "sales-order");
        var ctl = BuildController(db);
        TestAuth.SetUser(ctl, user);

        var id = CreatedId(await ctl.Create(SaveDto("草稿")));

        ctl.HttpContext.RequestAborted = new CancellationToken(canceled: true);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Publish(id, 1));

        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeCancelled, ex.Code);
        Assert.Empty(db.ReportConfigurationRevisions);
    }

    [Fact]
    public async Task Grant_预取消_抛已取消且不落授权()
    {
        using var db = TestDbFactory.Create();
        var owner = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipient = SeedAuthorizedUser(db, "recipient", "sales-order");
        var ctl = BuildController(db);
        TestAuth.SetUser(ctl, owner);

        var id = CreatedId(await ctl.Create(SaveDto("草稿")));
        await ctl.Publish(id, 1);

        ctl.HttpContext.RequestAborted = new CancellationToken(canceled: true);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Grant(id, new ReportConfigurationGrantRequestDto { RecipientUserId = recipient, RevisionVersion = 1 }));

        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeCancelled, ex.Code);
        Assert.Empty(db.ReportConfigurationGrants);
    }

    [Fact]
    public async Task Preview_预取消_抛已取消且不执行数据集查询()
    {
        using var db = TestDbFactory.Create();
        var sentinel = new SentinelExecutionDatasetProvider();
        var providers = new IReportConfigurationDatasetProvider[] { sentinel };
        var ctl = new ReportConfigurationsController(
            new ReportConfigurationCatalog(providers),
            new ReportConfigurationService(db, new ReportConfigurationCatalog(providers)),
            new ReportConfigurationExecutionService(db, providers),
            new ReportConfigurationSharingService(db, new ReportConfigurationCatalog(providers)));
        TestAuth.SetUser(ctl, 42);
        ctl.HttpContext.RequestAborted = new CancellationToken(canceled: true);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Preview(new ReportConfigurationPreviewRequest { ConfigurationId = 123 }));

        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeCancelled, ex.Code);
        Assert.False(sentinel.PreviewInvoked);
    }

    // ==================== 4. 未取消正常成功 & 非取消失败不误判 ====================

    [Fact]
    public async Task Create_未取消_正常成功并落库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "owner", "sales-order");
        var ctl = BuildController(db);
        TestAuth.SetUser(ctl, user);
        using var cts = new CancellationTokenSource();
        ctl.HttpContext.RequestAborted = cts.Token;

        var result = await ctl.Create(SaveDto("草稿"));

        var resp = Assert.IsType<ApiResponse<ReportConfigurationDto>>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.True(resp.Data!.Id > 0);
        Assert.Single(db.ReportConfigurations);
    }

    [Fact]
    public async Task Update_陈旧版本_仍按规则冲突拒绝而非取消()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "owner", "sales-order");
        var ctl = BuildController(db);
        TestAuth.SetUser(ctl, user);
        using var cts = new CancellationTokenSource();
        ctl.HttpContext.RequestAborted = cts.Token;

        var id = CreatedId(await ctl.Create(SaveDto("草稿")));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(id, 999, SaveDto("改名")));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }

    [Fact]
    public async Task Preview_未取消_正常成功()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "owner", "sales-order");
        var ctl = BuildController(db);
        TestAuth.SetUser(ctl, user);
        using var cts = new CancellationTokenSource();
        ctl.HttpContext.RequestAborted = cts.Token;

        var id = CreatedId(await ctl.Create(SaveDto("草稿")));

        var result = await ctl.Preview(new ReportConfigurationPreviewRequest { ConfigurationId = id });

        var resp = Assert.IsType<ApiResponse<ReportConfigurationPreviewDto>>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.NotNull(resp.Data);
    }

    [Fact]
    public async Task Preview_非取消_繁忙_仍按繁忙拒绝而非取消()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "owner", "sales-order");
        var service = new ReportConfigurationService(db, new ReportConfigurationCatalog(BuildProviders(db)));
        var created = await service.CreateAsync(user, SaveDto("草稿"));

        var budget = new ReportConfigurationExecutionBudget();
        var ctl = BuildControllerWithBudget(db, budget);
        TestAuth.SetUser(ctl, user);
        using var cts = new CancellationTokenSource();
        ctl.HttpContext.RequestAborted = cts.Token;

        using var first = budget.Acquire(user);
        using var second = budget.Acquire(user);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Preview(new ReportConfigurationPreviewRequest { ConfigurationId = created.Id }));

        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeBusy, ex.Code);
    }
}
