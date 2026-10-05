using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Reports;
using System.Text.Json;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-310 Stage 2 捆绑预设编排单元测试：覆盖两个模板族（客户报告包 / 客户订单与收款核对）的只读列出、
/// 独立前置条件与 readiness、参数化私有物化、未声明参数拒绝、菜单撤销 fail closed、部分数据集缺失 readiness 降级、
/// 以及全有或全无回滚（只清理本次新建私有草稿）。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不执行 SQL / seed。</para>
/// </summary>
public class ReportConfigurationBundlePresetTests
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

    private static long SeedAuthorizedUser(ErpDbContext db, string userName, params string[] menuCodes)
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        foreach (var code in menuCodes)
            SeedRoleMenu(db, role.Id, SeedMenu(db, code).Id);
        return user.Id;
    }

    private static long SeedPrivilegedAuthorizedUser(ErpDbContext db, string userName, params string[] menuCodes)
    {
        var user = SeedUser(db, userName);
        var role = new SysRole { RoleName = $"Role-{userName}", RoleCode = $"Role-{userName}-{Guid.NewGuid():N}", IsSystem = true };
        db.SysRoles.Add(role);
        db.SaveChanges();
        SeedUserRole(db, user.Id, role.Id);
        foreach (var code in menuCodes)
            SeedRoleMenu(db, role.Id, SeedMenu(db, code).Id);
        return user.Id;
    }

    private static void RevokeMenu(ErpDbContext db, long userId, string menuCode)
    {
        var menu = db.SysMenus.First(m => m.MenuCode == menuCode);
        var roleIds = db.SysUserRoles.Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId).ToList();
        foreach (var link in db.SysRoleMenus.Where(rm => roleIds.Contains(rm.RoleId) && rm.MenuId == menu.Id))
            link.IsDeleted = true;
        db.SaveChanges();
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            IsDeleted = false,
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId, Currency currency, decimal totalAmount)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 25),
            CustomerId = customerId,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = DocumentStatus.Approved,
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static FinanceReceipt SeedReceipt(ErpDbContext db, string receiptNo, long customerId, decimal amount, Currency currency, DocumentStatus status)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = new DateTime(2026, 9, 24),
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            Status = status,
        };
        db.FinanceReceipts.Add(receipt);
        db.SaveChanges();
        return receipt;
    }

    private static IReportConfigurationDatasetProvider[] BuildProviders(ErpDbContext db)
        => new IReportConfigurationDatasetProvider[]
        {
            new SalesOrderReportConfigurationDatasetProvider(new DynamicSalesOrderReportQuery(db)),
            new ReceivableReportConfigurationDatasetProvider(new DynamicReceivableReportQuery(db)),
            new ReceiptReconciliationReportConfigurationDatasetProvider(db),
            new UnlinkedReceiptReportConfigurationDatasetProvider(db),
        };

    private static IReportConfigurationCatalog BuildRealCatalog(ErpDbContext db)
        => new ReportConfigurationCatalog(BuildProviders(db));

    private static IReportConfigurationService BuildService(ErpDbContext db, IReportConfigurationCatalog catalog)
        => new ReportConfigurationService(db, catalog);

    private static ReportConfigurationBundlePresetCatalog BuildPresets(ErpDbContext db)
    {
        var catalog = BuildRealCatalog(db);
        return new ReportConfigurationBundlePresetCatalog(catalog, BuildService(db, catalog), db);
    }

    // ==================== 1. 两个模板族 + 独立前置条件 / readiness ====================

    [Fact]
    public async Task List_两个模板族_双菜单授权_preset_ready()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "bp-list", "sales-order", "customer");
        var presets = BuildPresets(db);

        var list = await presets.ListPresetsAsync(user);

        Assert.Equal(2, list.Count);

        var packet = list.Single(p => p.PresetKey == "customer-report-packet");
        Assert.Equal("packet:customer-report-packet", packet.LegacyKey);
        Assert.Equal(ReportConfigurationBundlePresetConstants.ReadinessPresetReady, packet.Readiness);
        Assert.Equal(new[] { "customer", "date" }, packet.Parameters.Select(p => p.Key).ToArray());
        Assert.True(packet.Parameters.Single(p => p.Key == "customer").Required);
        Assert.Equal(new[] { "销售订单", "应收证据" }, packet.Sections.Select(s => s.Title).ToArray());
        Assert.Equal(new[] { "sales-order", "receivable" }, packet.Sections.Select(s => s.DatasetKey).ToArray());
        Assert.True(packet.Prerequisites.All(p => p.Satisfied));

        var recon = list.Single(p => p.PresetKey == "receipt-reconciliation");
        Assert.Equal("dynamic:receipt-reconciliation", recon.LegacyKey);
        Assert.Equal(ReportConfigurationBundlePresetConstants.ReadinessPresetReady, recon.Readiness);
        Assert.Equal(new[] { "customer", "status" }, recon.Parameters.Select(p => p.Key).ToArray());
        Assert.Equal(new[] { "订单证据", "未关联收款证据" }, recon.Sections.Select(s => s.Title).ToArray());
        Assert.Equal(new[] { "receipt-reconciliation", "unlinked-receipt" }, recon.Sections.Select(s => s.DatasetKey).ToArray());
        Assert.True(recon.Prerequisites.All(p => p.Satisfied));
    }

    [Fact]
    public async Task List_菜单未授权_隐藏()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "bp-hidden", "sales-order");
        var presets = BuildPresets(db);

        var list = await presets.ListPresetsAsync(user);

        Assert.DoesNotContain(list, p => p.PresetKey == "customer-report-packet");
    }

    // ==================== 2. 参数化私有物化 ====================

    [Fact]
    public async Task Materialize_客户报告包_两节私有草稿_参数绑定()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "bp-mat-packet", "sales-order", "customer");
        var catalog = BuildRealCatalog(db);
        var service = BuildService(db, catalog);
        var presets = new ReportConfigurationBundlePresetCatalog(catalog, service, db);

        var result = await presets.MaterializeAsync("customer-report-packet",
            new ReportConfigurationBundlePresetMaterializeRequest
            {
                CustomerId = 42,
                StartDate = new DateTime(2026, 9, 1),
                EndDate = new DateTime(2026, 9, 30),
            }, user);

        Assert.Equal(2, result.Bundle.Sections.Count);
        Assert.Equal(2, db.ReportConfigurations.Count());

        var first = await service.GetAsync(user, result.Bundle.Sections[0].ConfigurationId);
        Assert.Equal(ReportConfigurationConstants.DatasetSalesOrder, first.DatasetKey);
        Assert.Equal(user, first.OwnerUserId);
        Assert.Equal(2, first.Definition!.Filters.Count);
        Assert.Equal("customerId", first.Definition!.Filters[0].FieldKey);
        Assert.Equal(ReportConfigurationConstants.OperatorEq, first.Definition!.Filters[0].Operator);
        Assert.Equal(42L, ((JsonElement)first.Definition!.Filters[0].Value!).GetInt64());
        Assert.Equal("orderDate", first.Definition!.Filters[1].FieldKey);
        Assert.Equal(ReportConfigurationConstants.OperatorBetween, first.Definition!.Filters[1].Operator);

        var second = await service.GetAsync(user, result.Bundle.Sections[1].ConfigurationId);
        Assert.Equal(ReportConfigurationConstants.DatasetReceivable, second.DatasetKey);
        Assert.Equal(user, second.OwnerUserId);
        Assert.Equal(2, second.Definition!.Filters.Count);
        Assert.Equal("invoiceDate", second.Definition!.Filters[1].FieldKey);
    }

    [Fact]
    public async Task Materialize_收款核对_两节私有草稿()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "bp-mat-recon", "sales-order");
        var presets = BuildPresets(db);

        var result = await presets.MaterializeAsync("receipt-reconciliation",
            new ReportConfigurationBundlePresetMaterializeRequest { CustomerId = 7 }, user);

        Assert.Equal(2, result.Bundle.Sections.Count);
        Assert.Equal(2, db.ReportConfigurations.Count());
        Assert.Equal("订单证据", result.Sections[0].Title);
        Assert.Equal("未关联收款证据", result.Sections[1].Title);
    }

    [Fact]
    public async Task Materialize_收款核对_物化后预览_两节非空()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "bp-preview-recon", "sales-order");
        var customer = SeedCustomer(db, "C-BP1", "客户甲");
        SeedOrder(db, "SO-BP1", customer.Id, Currency.USD, 1000m);
        SeedReceipt(db, "SK-BP1", customer.Id, 80m, Currency.USD, DocumentStatus.Approved);

        var providers = BuildProviders(db);
        var catalog = new ReportConfigurationCatalog(providers);
        var service = BuildService(db, catalog);
        var execution = new ReportConfigurationExecutionService(db, providers);
        var bundle = new ReportConfigurationBundleService(execution);
        var presets = new ReportConfigurationBundlePresetCatalog(catalog, service, db);

        var materialized = await presets.MaterializeAsync("receipt-reconciliation",
            new ReportConfigurationBundlePresetMaterializeRequest { CustomerId = customer.Id }, user);

        var preview = await bundle.PreviewAsync(user, materialized.Bundle);

        Assert.Equal(2, preview.SectionCount);
        Assert.Equal(ReportConfigurationConstants.DatasetReceiptReconciliation, preview.Sections[0].Preview.DatasetKey);
        Assert.Equal(ReportConfigurationConstants.DatasetUnlinkedReceipt, preview.Sections[1].Preview.DatasetKey);
        Assert.True(preview.Sections[0].Preview.Rows.Count > 0);
        Assert.True(preview.Sections[1].Preview.Rows.Count > 0);
    }

    // ==================== 3. 未声明 / 不支持参数拒绝（持久化前） ====================

    [Fact]
    public async Task Materialize_客户报告包_status未声明_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "bp-status", "sales-order", "customer");
        var presets = BuildPresets(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => presets.MaterializeAsync("customer-report-packet",
            new ReportConfigurationBundlePresetMaterializeRequest { CustomerId = 1, Status = "Approved" }, user));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.ReportConfigurations);
    }

    [Fact]
    public async Task Materialize_收款核对_date未声明_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "bp-date", "sales-order");
        var presets = BuildPresets(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => presets.MaterializeAsync("receipt-reconciliation",
            new ReportConfigurationBundlePresetMaterializeRequest { StartDate = new DateTime(2026, 9, 1) }, user));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.ReportConfigurations);
    }

    [Fact]
    public async Task Materialize_客户报告包_客户必填_缺失拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "bp-required", "sales-order", "customer");
        var presets = BuildPresets(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => presets.MaterializeAsync("customer-report-packet",
            new ReportConfigurationBundlePresetMaterializeRequest(), user));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.ReportConfigurations);
    }

    // ==================== 4. 未知预设 / 菜单撤销 / 部分数据集缺失 / 回滚 ====================

    [Fact]
    public async Task Materialize_未知预设_NotFound()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "bp-unknown", "sales-order", "customer");
        var presets = BuildPresets(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => presets.MaterializeAsync("does-not-exist",
            new ReportConfigurationBundlePresetMaterializeRequest { CustomerId = 1 }, user));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Empty(db.ReportConfigurations);
    }

    [Fact]
    public async Task Materialize_菜单撤销_fail_closed()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "bp-revoked", "sales-order", "customer");
        var presets = BuildPresets(db);

        RevokeMenu(db, user, "customer");

        var ex = await Assert.ThrowsAsync<BusinessException>(() => presets.MaterializeAsync("customer-report-packet",
            new ReportConfigurationBundlePresetMaterializeRequest { CustomerId = 1 }, user));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Empty(db.ReportConfigurations);
    }

    [Fact]
    public async Task List_部分数据集缺失_readiness_dataset_ready_且不可物化()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "bp-partial", "sales-order");
        var catalog = new ReportConfigurationCatalog(new IReportConfigurationDatasetProvider[]
        {
            new ReceiptReconciliationReportConfigurationDatasetProvider(db),
        });
        var presets = new ReportConfigurationBundlePresetCatalog(catalog, BuildService(db, catalog), db);

        var preset = await presets.GetPresetAsync("receipt-reconciliation", user);

        Assert.NotNull(preset);
        Assert.Equal(ReportConfigurationBundlePresetConstants.ReadinessDatasetReady, preset!.Readiness);
        Assert.Contains(preset.Prerequisites, p => p.Kind == "dataset" && !p.Satisfied);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => presets.MaterializeAsync("receipt-reconciliation",
            new ReportConfigurationBundlePresetMaterializeRequest(), user));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.ReportConfigurations);
    }

    [Fact]
    public async Task Materialize_第二节约失败_回滚本次新建私有草稿()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "bp-rollback", "sales-order");
        var catalog = BuildRealCatalog(db);
        var fake = new FailingAfterFirstCreateService();
        var presets = new ReportConfigurationBundlePresetCatalog(catalog, fake, db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => presets.MaterializeAsync("receipt-reconciliation",
            new ReportConfigurationBundlePresetMaterializeRequest { CustomerId = 1 }, user));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Single(fake.DeletedIds);
        Assert.Equal(1, fake.DeletedIds[0]);
        Assert.Empty(db.ReportConfigurations);
    }

    private sealed class FailingAfterFirstCreateService : IReportConfigurationService
    {
        public List<long> DeletedIds { get; } = new();
        private int _creates;

        public Task<ReportConfigurationDto> CreateAsync(long ownerUserId, ReportConfigurationSaveDto dto, CancellationToken cancellationToken = default)
        {
            if (++_creates >= 2)
                throw BusinessException.RuleConflict("模拟第二节失败");
            return Task.FromResult(new ReportConfigurationDto { Id = _creates, OwnerUserId = ownerUserId, Name = dto.Name });
        }

        public Task DeleteAsync(long ownerUserId, long id, int expectedVersion, CancellationToken cancellationToken = default)
        {
            DeletedIds.Add(id);
            return Task.CompletedTask;
        }

        public Task<ReportConfigurationDto> UpdateAsync(long ownerUserId, long id, int expectedVersion, ReportConfigurationSaveDto dto, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<ReportConfigurationDto> RenameAsync(long ownerUserId, long id, int expectedVersion, string name, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<ReportConfigurationDto> CopyAsync(long ownerUserId, long id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<ReportConfigurationDto> GetAsync(long ownerUserId, long id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<List<ReportConfigurationSummaryDto>> ListAsync(long ownerUserId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<ReportConfigurationPage<ReportConfigurationSummaryDto>> ListPageAsync(long ownerUserId, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<ReportConfigurationDto> PublishAsync(long ownerUserId, long id, int expectedVersion, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<ReportConfigurationDto> RestoreAsync(long ownerUserId, long id, int expectedVersion, int versionNumber, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<List<ReportConfigurationRevisionDto>> ListRevisionsAsync(long ownerUserId, long id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<ReportConfigurationPage<ReportConfigurationRevisionDto>> ListRevisionsPageAsync(long ownerUserId, long id, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }
}
