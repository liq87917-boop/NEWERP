using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Export;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-309 Stage 2 受控证据目录与既有动态来源目录的 parity 单元测试：
/// 证明出货财务进度 / 客户订单与收款核对受控数据集目录覆盖全部既有动态目录字段（键 / 类型 / 币种 / 单位 / 可空语义），
/// 并证明客户级未关联收款证据作为独立有界数据集复用 SalesOrderReceiptReconciliation.ForQueryAsync 的未关联收款结果
/// （独立收款粒度 / 原币 / 截断 / 引用与无订单归属语义，绝不并入订单合计），以及通用捆绑的匹配筛选与整体失败（fail closed）。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class ReportConfigurationEvidenceCatalogParityTests
{
    private static readonly SalespersonDataScope PrivilegedScope = new() { IsPrivileged = true, AllowedCustomerIds = null };
    private static readonly DateTime AsOf = new(2026, 9, 25);

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

    private static SysRole SeedRole(ErpDbContext db, string code, bool isSystem = false)
    {
        var role = new SysRole { RoleName = code, RoleCode = code, IsSystem = isSystem };
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

    private static SysUser SeedPrivilegedAuthorizedUser(ErpDbContext db, string name, params string[] menuCodes)
    {
        var user = SeedUser(db, name);
        var role = SeedRole(db, name + "-sys-role", isSystem: true);
        SeedUserRole(db, user.Id, role.Id);
        foreach (var code in menuCodes)
            SeedRoleMenu(db, role.Id, SeedMenu(db, code).Id);
        return user;
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

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId, Currency currency,
        decimal totalAmount, DateTime? orderDate = null)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = orderDate ?? AsOf,
            CustomerId = customerId,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = DocumentStatus.Approved,
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static void SeedDetail(ErpDbContext db, long salesOrderId, long productId, decimal quantity,
        string unit = "PCS")
    {
        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = salesOrderId,
            ProductId = productId,
            ProductName = $"商品{productId}",
            Spec = "规格A",
            Unit = unit,
            Quantity = quantity,
        });
        db.SaveChanges();
    }

    private static FinanceReceipt SeedReceipt(ErpDbContext db, string receiptNo, long customerId, decimal amount,
        Currency currency, DocumentStatus status)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = AsOf.AddDays(-2),
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            Status = status,
        };
        db.FinanceReceipts.Add(receipt);
        db.SaveChanges();
        return receipt;
    }

    private static IReadOnlyList<IReportConfigurationDatasetProvider> BuildProviders(ErpDbContext db)
        => new IReportConfigurationDatasetProvider[]
        {
            new ReceiptReconciliationReportConfigurationDatasetProvider(db),
            new UnlinkedReceiptReportConfigurationDatasetProvider(db),
            new ERP.Api.Controllers.ShipmentFinanceReportConfigurationDatasetProvider(db),
        };

    private static ReportConfigurationDefinition Definition(string datasetKey, params string[] fields) => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = datasetKey,
        Fields = fields.ToList(),
    };

    private static ReportConfigurationPreviewParameters Params(int page = 1, int pageSize = 200)
        => new(page, pageSize, ReportConfigurationConstants.GroupNone, null, null);

    private static string GenericTypeOf(string legacyType) => legacyType switch
    {
        "number" => ReportConfigurationConstants.TypeNumber,
        "text" => ReportConfigurationConstants.TypeText,
        "date" => ReportConfigurationConstants.TypeDate,
        "boolean" => ReportConfigurationConstants.TypeBoolean,
        // 既有 Api 层适配器把 enum 币种统一映射为 text（与 ERP-303 / ERP-309 受控目录口径一致）
        "enum" => ReportConfigurationConstants.TypeText,
        _ => legacyType,
    };


    // ==================== 1. 受控目录与既有动态目录字段 parity ====================

    [Fact]
    public async Task 出货财务进度_受控目录字段与既有动态目录一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "ship-parity", "sales-order");
        var provider = new ERP.Api.Controllers.ShipmentFinanceReportConfigurationDatasetProvider(db);

        var dataset = await provider.GetDatasetAsync(user.Id);
        Assert.NotNull(dataset);

        var legacy = DynamicShipmentFinanceReportRules.GetCatalog();
        Assert.Equal(legacy.Count, dataset.Fields.Count);
        foreach (var legacyField in legacy)
        {
            var field = Assert.Single(
                dataset.Fields, f => string.Equals(f.Key, legacyField.Key, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(GenericTypeOf(legacyField.DataType), field.Type);
            Assert.Equal(legacyField.Filterable, field.Filterable);
        }
    }

    [Fact]
    public async Task 客户订单与收款核对_受控目录覆盖全部既有动态目录字段()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "receipt-parity", "sales-order");
        var provider = new ReceiptReconciliationReportConfigurationDatasetProvider(db);

        var dataset = await provider.GetDatasetAsync(user.Id);
        Assert.NotNull(dataset);

        var legacy = DynamicReceiptReconciliationReportRules.GetCatalog();
        Assert.Equal(legacy.Count, dataset.Fields.Count);

        var legacyKeys = legacy.Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var providerKeys = dataset.Fields.Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(legacyKeys.SetEquals(providerKeys));

        foreach (var legacyField in legacy)
        {
            var field = Assert.Single(
                dataset.Fields, f => string.Equals(f.Key, legacyField.Key, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(GenericTypeOf(legacyField.DataType), field.Type);
        }

        // 明确断言修复补齐的分配 / 发票 / 已知未知 / 标签说明 / 计数 / 截断字段均以原始键与类型出现
        Assert.Contains(dataset.Fields, f => f.Key == "receiptCoverageKnown" && f.Type == ReportConfigurationConstants.TypeBoolean);
        Assert.Contains(dataset.Fields, f => f.Key == "receiptCoverageText" && f.Type == ReportConfigurationConstants.TypeText);
        Assert.Contains(dataset.Fields, f => f.Key == "overShippedQuantity" && f.Type == ReportConfigurationConstants.TypeNumber);
        Assert.Contains(dataset.Fields, f => f.Key == "unattributedReceiptsTruncated" && f.Type == ReportConfigurationConstants.TypeBoolean);
        Assert.Contains(dataset.Fields, f => f.Key == "receiptAllocationEvidenceLabel" && f.Type == ReportConfigurationConstants.TypeText);
        Assert.Contains(dataset.Fields, f => f.Key == "receiptAllocationCount" && f.Type == ReportConfigurationConstants.TypeNumber);
        Assert.Contains(dataset.Fields, f => f.Key == "receiptAllocationTruncated" && f.Type == ReportConfigurationConstants.TypeBoolean);
        Assert.Contains(dataset.Fields, f => f.Key == "unreferencedOrderAmount" && f.Type == ReportConfigurationConstants.TypeNumber);
        Assert.Contains(dataset.Fields, f => f.Key == "invoiceEvidenceLabel" && f.Type == ReportConfigurationConstants.TypeText);
        Assert.Contains(dataset.Fields, f => f.Key == "invoiceAllocationCount" && f.Type == ReportConfigurationConstants.TypeNumber);
        Assert.Contains(dataset.Fields, f => f.Key == "recordedInvoiceCount" && f.Type == ReportConfigurationConstants.TypeNumber);
        Assert.Contains(dataset.Fields, f => f.Key == "recordedInvoiceGrossAmount" && f.Type == ReportConfigurationConstants.TypeNumber);
        Assert.Contains(dataset.Fields, f => f.Key == "unreferencedInvoiceAmount" && f.Type == ReportConfigurationConstants.TypeNumber);
        Assert.Contains(dataset.Fields, f => f.Key == "invoiceUnreferencedOrderAmount" && f.Type == ReportConfigurationConstants.TypeNumber);
        Assert.Contains(dataset.Fields, f => f.Key == "invoiceEvidenceTruncated" && f.Type == ReportConfigurationConstants.TypeBoolean);
        Assert.Contains(dataset.Fields, f => f.Key == "invoiceEvidenceNote" && f.Type == ReportConfigurationConstants.TypeText);
    }

    [Fact]
    public async Task 未关联收款证据_受控目录字段与既有收款目录一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "unlinked-parity", "sales-order");
        var provider = new UnlinkedReceiptReportConfigurationDatasetProvider(db);

        var dataset = await provider.GetDatasetAsync(user.Id);
        Assert.NotNull(dataset);

        var legacy = DynamicReceiptReconciliationReportRules.GetReceiptCatalog();
        Assert.Equal(legacy.Count, dataset.Fields.Count);
        foreach (var legacyField in legacy)
        {
            var field = Assert.Single(
                dataset.Fields, f => string.Equals(f.Key, legacyField.Key, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(GenericTypeOf(legacyField.DataType), field.Type);
            Assert.Equal(legacyField.Label, field.Label);
        }

        // 收款侧有限筛选仅客户 / 币种 / 收款证据状态（与订单侧匹配源页筛选，无隐藏联接）
        var filterable = dataset.Fields.Where(f => f.Filterable).Select(f => f.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(filterable.SetEquals(new[] { "customerId", "currency", "evidenceStatus" }));
    }


    // ==================== 2. 未关联收款证据：复用权威派生结果 ====================

    [Fact]
    public async Task 未关联收款证据_预览复用权威派生结果_独立不并入订单()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "unlinked-preview", "sales-order");
        var customer = SeedCustomer(db, "C001", "客户A");
        var order = SeedOrder(db, "SO-1", customer.Id, Currency.USD, 1000m);
        SeedDetail(db, order.Id, 958101L, 10m);
        SeedReceipt(db, "SK-1", customer.Id, 40m, Currency.USD, DocumentStatus.Approved);
        SeedReceipt(db, "SK-2", customer.Id, 60m, Currency.USD, DocumentStatus.Approved);

        var legacy = await SalesOrderReceiptReconciliation.ForQueryAsync(
            db, new SalesOrderReceiptReconciliationQuery { PageSize = 200 }, PrivilegedScope);
        var provider = new UnlinkedReceiptReportConfigurationDatasetProvider(db);
        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetUnlinkedReceipt,
                "receiptNo", "currency", "amount", "evidenceStatus", "receiptLinkageStatus", "referenceField"),
            Params(), user.Id);

        Assert.Equal(legacy.UnlinkedReceipts.Count, preview.Total);
        Assert.Equal(legacy.UnlinkedReceipts.Count, preview.Rows.Count);
        foreach (var legacyReceipt in legacy.UnlinkedReceipts)
        {
            var row = Assert.Single(preview.Rows, r => (string)r["receiptNo"]! == legacyReceipt.ReceiptNo);
            Assert.Equal(legacyReceipt.Amount, (decimal)row["amount"]!);
            Assert.Equal(legacyReceipt.Currency, (string)row["currency"]!);
            Assert.Equal(SalesOrderReceiptReconciliationSemantics.ReceiptLinkageUnlinked, (string)row["receiptLinkageStatus"]!);
            Assert.Equal(SalesOrderReceiptReconciliationSemantics.UnlinkedReferenceField, (string)row["referenceField"]!);
            Assert.Equal(SalesOrderReceiptReconciliationSemantics.ReceiptStatusActive, (string)row["evidenceStatus"]!);
        }
    }

    [Fact]
    public async Task 未关联收款证据_空节与截断证据_有界保留()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "unlinked-bounds", "sales-order");
        var customer = SeedCustomer(db, "C001", "客户A");

        var provider = new UnlinkedReceiptReportConfigurationDatasetProvider(db);

        // 空节：无任何订单 / 收款单时返回空（独立收款节，不并入订单）
        var empty = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetUnlinkedReceipt, "receiptNo"),
            Params(), user.Id);
        Assert.Empty(empty.Rows);
        Assert.Equal(0, empty.Total);
        Assert.Equal(0, empty.TotalPages);

        // 截断证据：命中单次读取上限时显式标注「不完整」，绝不静默截断
        SeedOrder(db, "SO-T-1", customer.Id, Currency.USD, 100m);
        for (var i = 0; i < SalesOrderReceiptReconciliation.UnlinkedReceiptLimit; i++)
        {
            db.FinanceReceipts.Add(new FinanceReceipt
            {
                ReceiptNo = $"SK-{i:D4}",
                ReceiptDate = AsOf,
                CustomerId = customer.Id,
                Amount = 1m,
                Currency = Currency.USD,
                Status = DocumentStatus.Approved,
            });
        }
        db.SaveChanges();

        var truncated = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetUnlinkedReceipt, "receiptNo"),
            Params(), user.Id);
        Assert.Equal(SalesOrderReceiptReconciliation.UnlinkedReceiptLimit, truncated.Total);
        Assert.Equal(200, truncated.Rows.Count);
        Assert.NotNull(truncated.Evidence);
        Assert.Contains("截断", truncated.Evidence.DisclaimerText);
    }


    // ==================== 3. 通用捆绑：匹配筛选与整体失败 ====================

    [Fact]
    public async Task 捆绑_匹配客户筛选两节_任一撤销整体失败()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "bundle-receipt", "sales-order");
        var customer = SeedCustomer(db, "C001", "客户A");
        var order = SeedOrder(db, "SO-B-1", customer.Id, Currency.USD, 1000m);
        SeedDetail(db, order.Id, 958101L, 10m);
        SeedReceipt(db, "SK-B-1", customer.Id, 40m, Currency.USD, DocumentStatus.Approved);

        var providers = BuildProviders(db);
        var service = new ReportConfigurationService(db, new ReportConfigurationCatalog(providers));
        var execution = new ReportConfigurationExecutionService(db, providers);
        var bundle = new ReportConfigurationBundleService(execution);

        var orderDef = new ReportConfigurationDefinition
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetReceiptReconciliation,
            Fields = new List<string> { "orderNo", "currency", "orderAmount" },
            Filters = new List<ReportConfigurationFilter>
            {
                new() { FieldKey = "customerId", Operator = "eq", Value = customer.Id },
            },
            Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
            Aggregates = new List<ReportConfigurationAggregate>(),
            Capabilities = new List<string>(),
        };
        var receiptDef = new ReportConfigurationDefinition
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetUnlinkedReceipt,
            Fields = new List<string> { "receiptNo", "currency", "amount" },
            Filters = new List<ReportConfigurationFilter>
            {
                new() { FieldKey = "customerId", Operator = "eq", Value = customer.Id },
            },
            Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
            Aggregates = new List<ReportConfigurationAggregate>(),
            Capabilities = new List<string>(),
        };

        var orderConfig = await service.CreateAsync(user.Id, new ReportConfigurationSaveDto
        {
            Name = "订单节",
            Definition = orderDef,
        });
        var receiptConfig = await service.CreateAsync(user.Id, new ReportConfigurationSaveDto
        {
            Name = "未关联收款节",
            Definition = receiptDef,
        });

        var request = new ReportConfigurationBundleRequest
        {
            Sections = new List<ReportConfigurationBundleSectionRequest>
            {
                new() { ConfigurationId = orderConfig.Id, Title = "订单" },
                new() { ConfigurationId = receiptConfig.Id, Title = "收款" },
            },
        };

        var preview = await bundle.PreviewAsync(user.Id, request);
        Assert.Equal(2, preview.SectionCount);
        Assert.Single(preview.Sections[0].Preview.Rows);
        Assert.Single(preview.Sections[1].Preview.Rows);
        Assert.Equal("SK-B-1", (string)preview.Sections[1].Preview.Rows[0]["receiptNo"]!);

        // 撤销菜单后任一节被拒绝即整体失败（fail closed，无部分节）
        var link = Assert.Single(db.SysRoleMenus.ToList());
        db.SysRoleMenus.Remove(link);
        db.SaveChanges();

        await Assert.ThrowsAsync<BusinessException>(() => bundle.PreviewAsync(user.Id, request));
    }

    // ==================== 4. 通用 Excel / PDF 导出可执行 ====================

    [Fact]
    public async Task 未关联收款证据_通用Excel与PDF导出_可执行()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "unlinked-export", "sales-order");
        var customer = SeedCustomer(db, "C001", "客户A");
        var order = SeedOrder(db, "SO-EXP-1", customer.Id, Currency.USD, 500m);
        SeedDetail(db, order.Id, 958101L, 5m);
        SeedReceipt(db, "SK-EXP-1", customer.Id, 40m, Currency.USD, DocumentStatus.Approved);

        var provider = new UnlinkedReceiptReportConfigurationDatasetProvider(db);
        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetUnlinkedReceipt, "receiptNo", "currency", "amount"),
            Params(), user.Id);

        var excel = new ReportConfigurationExcelExporter().Build(preview);
        Assert.NotEmpty(excel);

        var fontPath = SimHeiPdfFontResolver.FindFontPath();
        if (fontPath is not null)
        {
            var pdf = ReportConfigurationPdfExporter.Export(preview, fontPath);
            Assert.NotEmpty(pdf);
        }
    }

}

