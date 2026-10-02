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
/// ERP-261 通用报表配置 API 单元测试（直接实例化控制器，不启动 API / 不连接 SQL Server）：
/// 覆盖未认证拒绝、目录、owner-only CRUD / 复制 / 发布 / 修订列表、跨所有者 fail closed、
/// 陈旧预期版本拒绝与软删除，以及预览端点跨所有者拒绝。
/// </summary>
public class ReportConfigurationApiTests
{
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

    private static SysRole SeedRole(ErpDbContext db, string suffix, bool isSystem = false)
    {
        var role = new SysRole
        {
            RoleName = $"Role-{suffix}",
            RoleCode = $"Role-{suffix}-{Guid.NewGuid():N}",
            IsSystem = isSystem,
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
        var role = SeedRole(db, menuCode);
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

    private static ReportConfigurationSaveDto SaveDto(string name, ReportConfigurationDefinition? definition = null)
        => new() { Name = name, Definition = definition ?? SalesOrderDefinition() };

    private static ReportConfigurationDefinition SalesOrderDefinition(params string[] fields)
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
            Fields = fields.Length == 0 ? new List<string> { "orderNo", "totalAmount", "currency" } : fields.ToList(),
            Filters = new List<ReportConfigurationFilter>(),
            Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
            Aggregates = new List<ReportConfigurationAggregate>(),
            Capabilities = new List<string>(),
        };

    [Fact]
    public async Task Catalog_未认证_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Catalog());
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task Catalog_销售订单授权_只返回该数据集()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "so-only", "sales-order");
        var ctl = BuildController(db);
        TestAuth.SetUser(ctl, user);

        var result = await ctl.Catalog();

        var resp = Assert.IsType<ApiResponse<ReportConfigurationCatalogDto>>(Assert.IsType<OkObjectResult>(result).Value);
        var dataset = Assert.Single(resp.Data!.Datasets);
        Assert.Equal(ReportConfigurationConstants.DatasetSalesOrder, dataset.DatasetKey);
    }

    [Fact]
    public async Task Create_正常_返回配置并落库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "owner", "sales-order");
        var ctl = BuildController(db);
        TestAuth.SetUser(ctl, user);

        var result = await ctl.Create(SaveDto("我的报表"));

        var resp = Assert.IsType<ApiResponse<ReportConfigurationDto>>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.True(resp.Data!.Id > 0);
        Assert.Equal(user, resp.Data.OwnerUserId);
        Assert.Single(db.ReportConfigurations);
    }

    [Fact]
    public async Task Get_跨所有者_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ownerA = SeedAuthorizedUser(db, "owner-a", "sales-order");
        var ownerB = SeedAuthorizedUser(db, "owner-b", "sales-order");

        var ctlA = BuildController(db);
        TestAuth.SetUser(ctlA, ownerA);
        var created = await ctlA.Create(SaveDto("报表"));
        var id = Assert.IsType<ApiResponse<ReportConfigurationDto>>(Assert.IsType<OkObjectResult>(created).Value).Data!.Id;

        var ctlB = BuildController(db);
        TestAuth.SetUser(ctlB, ownerB);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctlB.Get(id));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public async Task Update_陈旧预期版本_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "owner", "sales-order");
        var ctl = BuildController(db);
        TestAuth.SetUser(ctl, user);

        var created = await ctl.Create(SaveDto("报表"));
        var id = Assert.IsType<ApiResponse<ReportConfigurationDto>>(Assert.IsType<OkObjectResult>(created).Value).Data!.Id;

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(id, 999, SaveDto("报表")));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }

    [Fact]
    public async Task Delete_正常_软删除()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "owner", "sales-order");
        var ctl = BuildController(db);
        TestAuth.SetUser(ctl, user);

        var created = await ctl.Create(SaveDto("要删除的报表"));
        var createdResp = Assert.IsType<ApiResponse<ReportConfigurationDto>>(Assert.IsType<OkObjectResult>(created).Value).Data!;

        await ctl.Delete(createdResp.Id, createdResp.Version);

        Assert.True(Assert.Single(db.ReportConfigurations).IsDeleted);
    }

    [Fact]
    public async Task Publish_然后Revisions_返回不可变修订()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "owner", "sales-order");
        var ctl = BuildController(db);
        TestAuth.SetUser(ctl, user);

        var created = await ctl.Create(SaveDto("报表"));
        var createdResp = Assert.IsType<ApiResponse<ReportConfigurationDto>>(Assert.IsType<OkObjectResult>(created).Value).Data!;

        var publishResult = await ctl.Publish(createdResp.Id, createdResp.Version);
        Assert.IsType<OkObjectResult>(publishResult);

        var revisionsResult = await ctl.Revisions(createdResp.Id);
        var revisions = Assert.IsType<ApiResponse<List<ReportConfigurationRevisionDto>>>(
            Assert.IsType<OkObjectResult>(revisionsResult).Value).Data!;

        Assert.Single(revisions);
        Assert.Equal(1, revisions[0].Version);
    }

    [Fact]
    public async Task Preview_跨所有者_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ownerA = SeedAuthorizedUser(db, "owner-a", "sales-order");
        var ownerB = SeedAuthorizedUser(db, "owner-b", "sales-order");

        var ctlA = BuildController(db);
        TestAuth.SetUser(ctlA, ownerA);
        var created = await ctlA.Create(SaveDto("报表"));
        var id = Assert.IsType<ApiResponse<ReportConfigurationDto>>(Assert.IsType<OkObjectResult>(created).Value).Data!.Id;

        var ctlB = BuildController(db);
        TestAuth.SetUser(ctlB, ownerB);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctlB.Preview(new ReportConfigurationPreviewRequest { ConfigurationId = id }));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public async Task Export_授权用户_返回xlsx工作簿()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "owner", "sales-order");
        var ctl = BuildController(db);
        TestAuth.SetUser(ctl, user);

        var created = await ctl.Create(SaveDto("报表"));
        var id = Assert.IsType<ApiResponse<ReportConfigurationDto>>(Assert.IsType<OkObjectResult>(created).Value).Data!.Id;

        var result = await ctl.Export(new ReportConfigurationPreviewRequest { ConfigurationId = id });
        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.ContentType);
        Assert.EndsWith(".xlsx", file.FileDownloadName);
        Assert.NotEmpty(file.FileContents);
    }

    [Fact]
    public async Task Export_跨所有者_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ownerA = SeedAuthorizedUser(db, "owner-a", "sales-order");
        var ownerB = SeedAuthorizedUser(db, "owner-b", "sales-order");

        var ctlA = BuildController(db);
        TestAuth.SetUser(ctlA, ownerA);
        var created = await ctlA.Create(SaveDto("报表"));
        var id = Assert.IsType<ApiResponse<ReportConfigurationDto>>(Assert.IsType<OkObjectResult>(created).Value).Data!.Id;

        var ctlB = BuildController(db);
        TestAuth.SetUser(ctlB, ownerB);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctlB.Export(new ReportConfigurationPreviewRequest { ConfigurationId = id }));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public async Task Export_未认证_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db);
        TestAuth.SetUser(ctl, null);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.Export(new ReportConfigurationPreviewRequest { ConfigurationId = 1 }));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_授权用户_返回PDF文件()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "owner", "sales-order");
        var ctl = BuildController(db);
        TestAuth.SetUser(ctl, user);

        var created = await ctl.Create(SaveDto("报表"));
        var id = Assert.IsType<ApiResponse<ReportConfigurationDto>>(Assert.IsType<OkObjectResult>(created).Value).Data!.Id;

        var result = await ctl.ExportPdf(new ReportConfigurationPreviewRequest { ConfigurationId = id });
        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.EndsWith(".pdf", file.FileDownloadName);
        Assert.NotEmpty(file.FileContents);
    }

    [Fact]
    public async Task ExportPdf_跨所有者_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ownerA = SeedAuthorizedUser(db, "owner-a", "sales-order");
        var ownerB = SeedAuthorizedUser(db, "owner-b", "sales-order");

        var ctlA = BuildController(db);
        TestAuth.SetUser(ctlA, ownerA);
        var created = await ctlA.Create(SaveDto("报表"));
        var id = Assert.IsType<ApiResponse<ReportConfigurationDto>>(Assert.IsType<OkObjectResult>(created).Value).Data!.Id;

        var ctlB = BuildController(db);
        TestAuth.SetUser(ctlB, ownerB);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctlB.ExportPdf(new ReportConfigurationPreviewRequest { ConfigurationId = id }));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_未认证_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db);
        TestAuth.SetUser(ctl, null);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.ExportPdf(new ReportConfigurationPreviewRequest { ConfigurationId = 1 }));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    // ==================== ERP-265：只读共享授权 / 被授权人端点 ====================

    [Fact]
    public async Task Grants_授权与撤销_owner_only()
    {
        using var db = TestDbFactory.Create();
        var owner = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipient = SeedAuthorizedUser(db, "recipient", "sales-order");
        var ctlOwner = BuildController(db);
        TestAuth.SetUser(ctlOwner, owner);

        var created = await ctlOwner.Create(SaveDto("报表"));
        var id = Assert.IsType<ApiResponse<ReportConfigurationDto>>(
            Assert.IsType<OkObjectResult>(created).Value).Data!.Id;
        await ctlOwner.Publish(id, 1);

        var grantResult = await ctlOwner.Grant(id,
            new ReportConfigurationGrantRequestDto { RecipientUserId = recipient, RevisionVersion = 1 });
        var grantData = Assert.IsType<ApiResponse<ReportConfigurationGrantDto>>(
            Assert.IsType<OkObjectResult>(grantResult).Value).Data!;
        Assert.Equal(recipient, grantData.RecipientUserId);
        Assert.Equal(1, grantData.RevisionVersion);

        var list = await ctlOwner.Grants(id);
        Assert.Single(Assert.IsType<ApiResponse<List<ReportConfigurationGrantDto>>>(
            Assert.IsType<OkObjectResult>(list).Value).Data!);

        await ctlOwner.Revoke(id, recipient, grantData.Version);
        var after = await ctlOwner.Grants(id);
        Assert.Empty(Assert.IsType<ApiResponse<List<ReportConfigurationGrantDto>>>(
            Assert.IsType<OkObjectResult>(after).Value).Data!);
    }

    [Fact]
    public async Task Shared_列表与详情_recipient_only()
    {
        using var db = TestDbFactory.Create();
        var owner = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipient = SeedAuthorizedUser(db, "recipient", "sales-order");
        var ctlOwner = BuildController(db);
        TestAuth.SetUser(ctlOwner, owner);

        var created = await ctlOwner.Create(SaveDto("报表"));
        var id = Assert.IsType<ApiResponse<ReportConfigurationDto>>(
            Assert.IsType<OkObjectResult>(created).Value).Data!.Id;
        await ctlOwner.Publish(id, 1);
        await ctlOwner.Grant(id, new ReportConfigurationGrantRequestDto { RecipientUserId = recipient, RevisionVersion = 1 });

        var ctlRecipient = BuildController(db);
        TestAuth.SetUser(ctlRecipient, recipient);

        var list = await ctlRecipient.Shared();
        var items = Assert.IsType<ApiResponse<List<ReportConfigurationSharedSummaryDto>>>(
            Assert.IsType<OkObjectResult>(list).Value).Data!;
        var item = Assert.Single(items);
        Assert.Equal(id, item.ReportConfigurationId);
        Assert.Equal("报表", item.Name);
        Assert.Equal(1, item.RevisionVersion);

        var detail = await ctlRecipient.SharedDetail(id);
        var dto = Assert.IsType<ApiResponse<ReportConfigurationSharedDetailDto>>(
            Assert.IsType<OkObjectResult>(detail).Value).Data!;
        Assert.Equal(1, dto.RevisionVersion);
        Assert.NotNull(dto.Definition);
    }

    [Fact]
    public async Task Shared_复制为自有草稿()
    {
        using var db = TestDbFactory.Create();
        var owner = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipient = SeedAuthorizedUser(db, "recipient", "sales-order");
        var ctlOwner = BuildController(db);
        TestAuth.SetUser(ctlOwner, owner);

        var created = await ctlOwner.Create(SaveDto("报表"));
        var id = Assert.IsType<ApiResponse<ReportConfigurationDto>>(
            Assert.IsType<OkObjectResult>(created).Value).Data!.Id;
        await ctlOwner.Publish(id, 1);
        await ctlOwner.Grant(id, new ReportConfigurationGrantRequestDto { RecipientUserId = recipient, RevisionVersion = 1 });

        var ctlRecipient = BuildController(db);
        TestAuth.SetUser(ctlRecipient, recipient);
        var copy = await ctlRecipient.CopyShared(id);
        var dto = Assert.IsType<ApiResponse<ReportConfigurationDto>>(
            Assert.IsType<OkObjectResult>(copy).Value).Data!;
        Assert.Equal(recipient, dto.OwnerUserId);
        Assert.Equal(ReportConfigurationStatus.Draft, dto.Status);
        Assert.EndsWith("副本", dto.Name);
    }

    [Fact]
    public async Task Shared_非授权用户_列表为空且详情拒绝()
    {
        using var db = TestDbFactory.Create();
        var owner = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipient = SeedAuthorizedUser(db, "recipient", "sales-order");
        var stranger = SeedAuthorizedUser(db, "stranger", "sales-order");
        var ctlOwner = BuildController(db);
        TestAuth.SetUser(ctlOwner, owner);

        var created = await ctlOwner.Create(SaveDto("报表"));
        var id = Assert.IsType<ApiResponse<ReportConfigurationDto>>(
            Assert.IsType<OkObjectResult>(created).Value).Data!.Id;
        await ctlOwner.Publish(id, 1);
        await ctlOwner.Grant(id, new ReportConfigurationGrantRequestDto { RecipientUserId = recipient, RevisionVersion = 1 });

        var ctlStranger = BuildController(db);
        TestAuth.SetUser(ctlStranger, stranger);
        var list = await ctlStranger.Shared();
        Assert.Empty(Assert.IsType<ApiResponse<List<ReportConfigurationSharedSummaryDto>>>(
            Assert.IsType<OkObjectResult>(list).Value).Data!);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctlStranger.SharedDetail(id));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public async Task Export_共享被授权人_成功()
    {
        using var db = TestDbFactory.Create();
        var owner = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipient = SeedAuthorizedUser(db, "recipient", "sales-order");
        var ctlOwner = BuildController(db);
        TestAuth.SetUser(ctlOwner, owner);

        var created = await ctlOwner.Create(SaveDto("报表"));
        var id = Assert.IsType<ApiResponse<ReportConfigurationDto>>(
            Assert.IsType<OkObjectResult>(created).Value).Data!.Id;
        await ctlOwner.Publish(id, 1);
        await ctlOwner.Grant(id, new ReportConfigurationGrantRequestDto { RecipientUserId = recipient, RevisionVersion = 1 });

        var ctlRecipient = BuildController(db);
        TestAuth.SetUser(ctlRecipient, recipient);
        var result = await ctlRecipient.Export(new ReportConfigurationPreviewRequest { ConfigurationId = id });
        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.ContentType);
        Assert.NotEmpty(file.FileContents);
    }
}

