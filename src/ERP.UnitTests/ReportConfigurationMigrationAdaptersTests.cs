using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Reports;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-297 采购 / 供应商动态报表受控数据集适配器单元测试：覆盖目录聚合与有限字段白名单、
/// 菜单撤销（目录不暴露 + 预览 fail closed）、币种分区（绝不跨币种合并）、分页上限与稳定分页、
/// 以及与旧报表语义的预览一致性，并把迁移登记册对应条目推进到 dataset-ready。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行 SQL / seed。</para>
/// </summary>
public class ReportConfigurationMigrationAdaptersTests
{
    // ==================== 0. 测试脚手架 ====================

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

    private static SysRole SeedRole(ErpDbContext db, string code)
    {
        var role = new SysRole { RoleName = code, RoleCode = code, IsSystem = false };
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

    private static SysRoleMenu SeedRoleMenu(ErpDbContext db, long roleId, long menuId)
    {
        var link = new SysRoleMenu { RoleId = roleId, MenuId = menuId };
        db.SysRoleMenus.Add(link);
        db.SaveChanges();
        return link;
    }

    private static long SeedPrivilegedUser(ErpDbContext db, string userName = "priv")
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, $"Role-{userName}");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, "purchase-order").Id);
        return user.Id;
    }

    private static BaseSupplier SeedSupplier(ErpDbContext db, long id, string name, int status = 1)
    {
        var supplier = new BaseSupplier
        {
            Id = id,
            SupplierCode = $"S{id}",
            SupplierName = name,
            Status = status,
            IsDeleted = false,
        };
        db.BaseSuppliers.Add(supplier);
        db.SaveChanges();
        return supplier;
    }

    private static PurchaseOrder SeedOrder(
        ErpDbContext db, string orderNo, long supplierId, DateTime orderDate,
        Currency currency, decimal totalAmount, long? owningSalesOrderId = null)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            SupplierId = supplierId,
            OrderDate = orderDate.Date,
            Status = DocumentStatus.Approved,
            Currency = currency,
            TotalAmount = totalAmount,
            OwningSalesOrderId = owningSalesOrderId,
            OwningSalesOrderNo = owningSalesOrderId is null ? string.Empty : $"SO-{owningSalesOrderId}",
            IsDeleted = false,
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static PurchaseInvoice SeedInvoice(
        ErpDbContext db, long id, long supplierId, string currency, string number,
        decimal gross, int status, DateTime invoiceDate, DateTime? dueDate = null)
    {
        var supplier = db.BaseSuppliers.Local.FirstOrDefault(s => s.Id == supplierId);
        var invoice = new PurchaseInvoice
        {
            Id = id,
            InvoiceType = PurchaseInvoiceRules.InvoiceTypeOrdinary,
            InvoiceCode = string.Empty,
            InvoiceNumber = number,
            NormalizedInvoiceCode = string.Empty,
            NormalizedInvoiceNumber = PurchaseInvoiceRules.NormalizeIdentityPart(number),
            InvoiceDate = invoiceDate.Date,
            SupplierId = supplierId,
            SupplierCode = supplier?.SupplierCode ?? $"S{supplierId}",
            SupplierName = supplier?.SupplierName ?? $"供应商{supplierId}",
            Currency = currency,
            NetAmount = gross,
            TaxAmount = 0m,
            GrossAmount = gross,
            DueDate = dueDate?.Date,
            PaymentTerms = string.Empty,
            Status = status,
            RecordedAt = status == PurchaseInvoiceRules.StatusDraft ? null : invoiceDate.Date.AddDays(1),
            VoidedAt = status == PurchaseInvoiceRules.StatusVoided ? invoiceDate.Date.AddDays(2) : null,
            VoidReason = status == PurchaseInvoiceRules.StatusVoided ? "作废测试" : string.Empty,
            IsDeleted = false,
        };
        db.PurchaseInvoices.Add(invoice);
        db.SaveChanges();
        return invoice;
    }

    private static IReportConfigurationCatalog BuildCatalog(ErpDbContext db)
        => new ReportConfigurationCatalog(new IReportConfigurationDatasetProvider[]
        {
            new SalesOrderReportConfigurationDatasetProvider(new DynamicSalesOrderReportQuery(db)),
            new ReceivableReportConfigurationDatasetProvider(new DynamicReceivableReportQuery(db)),
            new PurchaseOrderReportConfigurationDatasetProvider(new DynamicPurchaseOrderReportQuery(db)),
            new SupplierAgingReportConfigurationDatasetProvider(db),
            new SupplierExposureReportConfigurationDatasetProvider(db),
        });

    private static ReportConfigurationDefinition Definition(string datasetKey, string[] fields)
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = datasetKey,
            Fields = fields.ToList(),
            Filters = new List<ReportConfigurationFilter>(),
            Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
            Aggregates = new List<ReportConfigurationAggregate>(),
            Capabilities = new List<string>(),
        };

    private static ReportConfigurationPreviewParameters Params(int page, int pageSize, string groupBy = "none")
        => new(page, pageSize, groupBy, null, null);


    // ==================== 1. 目录聚合与有限字段白名单 ====================

    [Fact]
    public async Task Catalog_采购供应商三个数据集_目录聚合与字段白名单匹配旧报表()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var catalog = BuildCatalog(db);

        var result = await catalog.GetCatalogAsync(uid);
        var byKey = result.Datasets.ToDictionary(d => d.DatasetKey, StringComparer.Ordinal);

        Assert.Equal(3, result.Datasets.Count);

        // 采购订单：复用 IDynamicPurchaseOrderReportQuery；分组 = month；上限 100。
        var po = byKey[ReportConfigurationConstants.DatasetPurchaseOrder];
        Assert.Equal(DynamicPurchaseOrderReportRules.AllFieldKeys.Count, po.Fields.Count);
        Assert.Equal(100, po.MaxPageSize);
        Assert.Contains(ReportConfigurationConstants.CapabilityGrouping, po.SupportedCapabilities);
        Assert.Contains(ReportConfigurationConstants.CapabilityDateRange, po.SupportedCapabilities);
        Assert.Contains(ReportConfigurationConstants.CapabilityPaging, po.SupportedCapabilities);
        Assert.Equal("金额按订单原币呈现，不跨币种换算或合并", po.CurrencyUnitSemantics);
        Assert.All(po.Fields, f => Assert.True(ReportConfigurationRules.IsKnownFieldType(f.Type)));

        // 供应商对账与账龄：无分组（平台仅 customer/month 分组），上限 200。
        var aging = byKey[ReportConfigurationConstants.DatasetSupplierAging];
        Assert.Equal(DynamicSupplierAgingReportRules.AllFieldKeys.Count, aging.Fields.Count);
        Assert.Equal(200, aging.MaxPageSize);
        Assert.Contains(ReportConfigurationConstants.CapabilityDateRange, aging.SupportedCapabilities);
        Assert.Contains(ReportConfigurationConstants.CapabilityPaging, aging.SupportedCapabilities);
        Assert.DoesNotContain(ReportConfigurationConstants.CapabilityGrouping, aging.SupportedCapabilities);
        Assert.Equal("金额按原币呈现；账龄按自然日；不跨币种换算或合并", aging.CurrencyUnitSemantics);
        var agingKeys = aging.Fields.Select(f => f.Key).ToHashSet();
        Assert.DoesNotContain("grandTotal", agingKeys);
        Assert.DoesNotContain("payableBalance", agingKeys);

        // 供应商采购敞口：无分组，上限 200。
        var exposure = byKey[ReportConfigurationConstants.DatasetSupplierExposure];
        Assert.Equal(DynamicSupplierExposureReportRules.GetCatalog().Count, exposure.Fields.Count);
        Assert.Equal(200, exposure.MaxPageSize);
        Assert.Contains(ReportConfigurationConstants.CapabilityDateRange, exposure.SupportedCapabilities);
        Assert.Contains(ReportConfigurationConstants.CapabilityPaging, exposure.SupportedCapabilities);
        Assert.DoesNotContain(ReportConfigurationConstants.CapabilityGrouping, exposure.SupportedCapabilities);
        Assert.Equal("金额按原币呈现；不跨币种换算或合并", exposure.CurrencyUnitSemantics);
    }

    [Fact]
    public async Task Catalog_无采购订单菜单授权_三个数据集不暴露()
    {
        using var db = TestDbFactory.Create();
        // 只授予销售订单菜单，未授予采购订单菜单。
        var user = SeedUser(db, "no-po");
        var role = SeedRole(db, "Role-no-po");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, "sales-order").Id);

        var po = new PurchaseOrderReportConfigurationDatasetProvider(new DynamicPurchaseOrderReportQuery(db));
        var aging = new SupplierAgingReportConfigurationDatasetProvider(db);
        var exposure = new SupplierExposureReportConfigurationDatasetProvider(db);

        Assert.Null(await po.GetDatasetAsync(user.Id));
        Assert.Null(await aging.GetDatasetAsync(user.Id));
        Assert.Null(await exposure.GetDatasetAsync(user.Id));
    }

    // ==================== 2. 菜单撤销 → 预览 fail closed ====================

    [Fact]
    public async Task Preview_无采购订单菜单授权_三个数据集fail_closed()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "revoked");
        var role = SeedRole(db, "Role-revoked");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, "sales-order").Id);

        var po = new PurchaseOrderReportConfigurationDatasetProvider(new DynamicPurchaseOrderReportQuery(db));
        var aging = new SupplierAgingReportConfigurationDatasetProvider(db);
        var exposure = new SupplierExposureReportConfigurationDatasetProvider(db);

        var poDef = Definition(ReportConfigurationConstants.DatasetPurchaseOrder, new[] { "orderNo", "currency" });
        var agingDef = Definition(ReportConfigurationConstants.DatasetSupplierAging, new[] { "invoiceId", "currency" });
        var exposureDef = Definition(ReportConfigurationConstants.DatasetSupplierExposure, new[] { "orderId", "currency" });

        var poEx = await Assert.ThrowsAsync<BusinessException>(() => po.PreviewAsync(poDef, Params(1, 20), user.Id));
        Assert.Equal(ErrorCodes.Forbidden, poEx.Code);

        var agingEx = await Assert.ThrowsAsync<BusinessException>(() => aging.PreviewAsync(agingDef, Params(1, 50), user.Id));
        Assert.Equal(ErrorCodes.Forbidden, agingEx.Code);

        var exposureEx = await Assert.ThrowsAsync<BusinessException>(() => exposure.PreviewAsync(exposureDef, Params(1, 50), user.Id));
        Assert.Equal(ErrorCodes.Forbidden, exposureEx.Code);
    }


    // ==================== 3. 采购订单预览：与旧查询逐行一致 + 币种分区 ====================

    [Fact]
    public async Task PurchaseOrder_Preview_与旧查询逐行一致_且币种分区不合并()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, 7001L, "供应商甲");
        var day = new DateTime(2026, 9, 15);
        SeedOrder(db, "PO-CNY", 7001L, day, Currency.CNY, 100m);
        SeedOrder(db, "PO-USD", 7001L, day, Currency.USD, 200m);

        var provider = new PurchaseOrderReportConfigurationDatasetProvider(new DynamicPurchaseOrderReportQuery(db));
        var legacy = new DynamicPurchaseOrderReportQuery(db);

        var fields = new[] { "orderNo", "currency", "totalAmount" };
        var definition = Definition(ReportConfigurationConstants.DatasetPurchaseOrder, fields);
        var preview = await provider.PreviewAsync(definition, Params(1, 20), uid);

        var legacyPage = await legacy.PreviewAsync(new DynamicPurchaseOrderReportRequest
        {
            Fields = fields.ToList(),
            Page = 1,
            PageSize = 20,
        }, uid);

        Assert.Equal(legacyPage.Total, preview.Total);
        Assert.Equal(legacyPage.Rows.Count, preview.Rows.Count);
        for (var i = 0; i < legacyPage.Rows.Count; i++)
        {
            Assert.Equal(legacyPage.Rows[i]["orderNo"], preview.Rows[i]["orderNo"]);
            Assert.Equal(legacyPage.Rows[i]["currency"], preview.Rows[i]["currency"]);
            Assert.Equal(legacyPage.Rows[i]["totalAmount"], preview.Rows[i]["totalAmount"]);
        }

        // 币种分区：每一行只携带自己的原币，绝无跨币种合并列。
        Assert.Equal(2, preview.Rows.Count);
        Assert.Contains(preview.Rows, r => (string)r["currency"]! == "CNY");
        Assert.Contains(preview.Rows, r => (string)r["currency"]! == "USD");
        Assert.All(preview.Columns, c => Assert.DoesNotContain("grandTotal", c.Key));
    }

    [Fact]
    public async Task PurchaseOrder_Preview_按月分组_币种分区小计()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, 7002L, "供应商乙");
        var day = new DateTime(2026, 9, 20);
        SeedOrder(db, "PO-CNY-2", 7002L, day, Currency.CNY, 300m);
        SeedOrder(db, "PO-USD-2", 7002L, day, Currency.USD, 400m);

        var provider = new PurchaseOrderReportConfigurationDatasetProvider(new DynamicPurchaseOrderReportQuery(db));
        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetPurchaseOrder, new[] { "orderNo", "currency", "totalAmount" }),
            Params(1, 20, groupBy: ReportConfigurationConstants.GroupMonth),
            uid);

        var group = Assert.Single(preview.Groups!);
        Assert.Equal(2, group.Partitions.Count);
        Assert.Contains(group.Partitions, p => p.Currency == "CNY" && p.Amount == 300m);
        Assert.Contains(group.Partitions, p => p.Currency == "USD" && p.Amount == 400m);
    }

    [Fact]
    public async Task PurchaseOrder_Preview_分页上限100_稳定分页()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, 7003L, "供应商丙");
        for (var i = 1; i <= 105; i++)
            SeedOrder(db, $"PO-{i:D3}", 7003L, new DateTime(2026, 9, 1), Currency.CNY, 10m);

        var provider = new PurchaseOrderReportConfigurationDatasetProvider(new DynamicPurchaseOrderReportQuery(db));
        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetPurchaseOrder, new[] { "orderNo", "currency" }),
            Params(1, 100),
            uid);

        Assert.Equal(105, preview.Total);
        Assert.Equal(100, preview.Rows.Count);
        Assert.Equal(2, preview.TotalPages);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetPurchaseOrder, new[] { "orderNo" }),
            Params(1, 101),
            uid));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }


    // ==================== 4. 供应商对账与账龄预览：与旧语义一致 + 币种分区 + 分页 ====================

    [Fact]
    public async Task Aging_Preview_与旧语义一致_币种分区与分配证据()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, 8001L, "供应商甲");
        SeedSupplier(db, 8002L, "供应商乙");
        var today = DateTime.Today;
        SeedInvoice(db, 9001L, 8001L, "CNY", "INV-CNY", 100m, PurchaseInvoiceRules.StatusRecorded, today.AddDays(-10), today.AddDays(-5));
        SeedInvoice(db, 9002L, 8002L, "USD", "INV-USD", 300m, PurchaseInvoiceRules.StatusRecorded, today.AddDays(-9), null);

        var provider = new SupplierAgingReportConfigurationDatasetProvider(db);
        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetSupplierAging, new[] { "invoiceId", "currency", "grossAmount", "agingBucket", "allocationState", "remainingAmount" }),
            Params(1, 50),
            uid);

        var legacy = await SupplierReconciliationAging.ForQueryAsync(db, new SupplierReconciliationAgingQuery { Page = 1, PageSize = 50 });
        var legacyByInvoice = legacy.Groups.SelectMany(g => g.Invoices).ToDictionary(i => i.InvoiceId);

        Assert.Equal(2, preview.Total);
        Assert.Equal(2, preview.Rows.Count);

        foreach (var row in preview.Rows)
        {
            var invoiceId = (long)row["invoiceId"]!;
            var legacyRow = legacyByInvoice[invoiceId];

            Assert.Equal(legacyRow.Currency, row["currency"]);
            Assert.Equal(legacyRow.GrossAmount, row["grossAmount"]);
            Assert.Equal(legacyRow.AgingBucket, row["agingBucket"]);
            Assert.Equal(legacyRow.AllocationState, row["allocationState"]);
            Assert.Equal(legacyRow.RemainingAmount, row["remainingAmount"]);
        }

        // 币种分区：不同币种分别成行，绝无跨币种合并列。
        Assert.Contains(preview.Rows, r => (string)r["currency"]! == "CNY");
        Assert.Contains(preview.Rows, r => (string)r["currency"]! == "USD");
        Assert.All(preview.Columns, c => Assert.DoesNotContain("grandTotal", c.Key));
    }

    [Fact]
    public async Task Aging_Preview_分页上限200()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, 8003L, "供应商丙");
        for (var i = 1; i <= 205; i++)
            SeedInvoice(db, 90000L + i, 8003L, "CNY", $"INV-{i:D3}", 10m, PurchaseInvoiceRules.StatusRecorded, DateTime.Today.AddDays(-i));

        var provider = new SupplierAgingReportConfigurationDatasetProvider(db);
        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetSupplierAging, new[] { "invoiceId", "currency" }),
            Params(1, 200),
            uid);

        Assert.Equal(205, preview.Total);
        Assert.Equal(200, preview.Rows.Count);
        Assert.Equal(2, preview.TotalPages);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetSupplierAging, new[] { "invoiceId" }),
            Params(1, 201),
            uid));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }


    // ==================== 5. 供应商采购敞口预览：与旧语义一致 + 币种分区 + 分页 ====================

    [Fact]
    public async Task Exposure_Preview_与旧语义一致_链接状态与币种分区()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, 7100L, "供应商甲");
        var day = new DateTime(2026, 9, 10);

        // linked（归属销售订单唯一）、ambiguous（同归属销售订单两张）、unavailable（未关联归属销售订单）。
        SeedOrder(db, "EXP-LINKED", 7100L, day, Currency.CNY, 100m, owningSalesOrderId: 80001L);
        SeedOrder(db, "EXP-AMB-1", 7100L, day, Currency.USD, 200m, owningSalesOrderId: 80002L);
        SeedOrder(db, "EXP-AMB-2", 7100L, day, Currency.USD, 300m, owningSalesOrderId: 80002L);
        SeedOrder(db, "EXP-NONE", 7100L, day, Currency.EUR, 400m, owningSalesOrderId: null);

        var provider = new SupplierExposureReportConfigurationDatasetProvider(db);
        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetSupplierExposure, new[] { "orderId", "currency", "orderedAmount", "linkStatus" }),
            Params(1, 50),
            uid);

        var legacy = await SupplierPurchaseExposure.ForQueryAsync(db, new SupplierPurchaseExposureQuery { Page = 1, PageSize = 50 });
        var legacyByOrder = legacy.Groups.SelectMany(g => g.Orders).ToDictionary(o => o.OrderId);

        Assert.Equal(4, preview.Total);
        Assert.Equal(4, preview.Rows.Count);

        foreach (var row in preview.Rows)
        {
            var orderId = (long)row["orderId"]!;
            var legacyRow = legacyByOrder[orderId];

            Assert.Equal(legacyRow.Currency, row["currency"]);
            Assert.Equal(legacyRow.OrderedAmount, row["orderedAmount"]);
            Assert.Equal(legacyRow.LinkStatus, row["linkStatus"]);
        }

        // 币种分区：CNY / USD / EUR 各自成行，绝无跨币种合并列。
        Assert.Contains(preview.Rows, r => (string)r["currency"]! == "CNY");
        Assert.Contains(preview.Rows, r => (string)r["currency"]! == "USD");
        Assert.Contains(preview.Rows, r => (string)r["currency"]! == "EUR");
        Assert.All(preview.Columns, c => Assert.DoesNotContain("grandTotal", c.Key));
    }

    [Fact]
    public async Task Exposure_Preview_分页上限200()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, 7200L, "供应商乙");
        for (var i = 1; i <= 203; i++)
            SeedOrder(db, $"EXP-{i:D3}", 7200L, new DateTime(2026, 9, 1), Currency.CNY, 10m);

        var provider = new SupplierExposureReportConfigurationDatasetProvider(db);
        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetSupplierExposure, new[] { "orderId", "currency" }),
            Params(1, 200),
            uid);

        Assert.Equal(203, preview.Total);
        Assert.Equal(200, preview.Rows.Count);
        Assert.Equal(2, preview.TotalPages);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetSupplierExposure, new[] { "orderId" }),
            Params(1, 201),
            uid));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 6. 迁移登记册：采购 / 供应商条目 dataset-ready ====================

    [Fact]
    public async Task MigrationRegistry_采购供应商条目_数据集就绪()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var registry = new ReportMigrationRegistry(
            BuildCatalog(db), db, new EmptyReportMigrationPresetCatalog());

        var result = await registry.GetRegistryAsync(uid);
        var byKey = result.Entries.ToDictionary(e => e.LegacyKey, StringComparer.Ordinal);

        Assert.Equal(ReportMigrationParityStatusText.DatasetReady, byKey["dynamic:purchase-order"].ParityStatus);
        Assert.Equal(ReportMigrationParityStatusText.DatasetReady, byKey["dynamic:supplier-aging"].ParityStatus);
        Assert.Equal(ReportMigrationParityStatusText.DatasetReady, byKey["dynamic:supplier-exposure"].ParityStatus);
    }

}
