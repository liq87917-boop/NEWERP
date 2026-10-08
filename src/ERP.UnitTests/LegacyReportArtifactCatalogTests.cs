using System.Text;
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
/// ERP-337 旧报表实际产物适配器目录单元测试：覆盖有限能力矩阵（supported / blocked / 未知键）、
/// 动态 / 旧单据导出 / 报告包 / 单证四类受支持族的真实规范产物字节、菜单撤销 fail closed 与缺失规范导出器阻塞。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不执行 SQL / seed、不启动 API。</para>
/// </summary>
public class LegacyReportArtifactCatalogTests
{
    private const string Text = ReportConfigurationConstants.TypeText;
    private const string Number = ReportConfigurationConstants.TypeNumber;

    // ==================== 1. 有限能力矩阵 ====================

    [Fact]
    public void Catalog_binds_every_supported_and_blocked_family()
    {
        var byKey = LegacyReportArtifactCatalog.Capabilities.ToDictionary(c => c.LegacyKey, StringComparer.OrdinalIgnoreCase);

        AssertSupported(byKey, "dynamic:sales-order", excel: true, pdf: true);
        AssertSupported(byKey, "dynamic:receivable", excel: true, pdf: true);
        AssertSupported(byKey, "dynamic:purchase-order", excel: true, pdf: true);

        foreach (var family in LegacyBillExportCatalog.Families)
            AssertSupported(byKey, "export:bill-proc:" + family.FamilyKey, excel: true, pdf: false);

        AssertSupported(byKey, "packet:customer-report-packet", excel: true, pdf: true);
        AssertSupported(byKey, "document:trade-document-export-excel", excel: true, pdf: false);

        AssertBlocked(byKey, "document:trade-document-print");
        AssertBlocked(byKey, "dynamic:supplier-aging");

        foreach (var family in ReportPrintTemplateFamilies.Families)
            AssertBlocked(byKey, "print-template:" + family.FamilyKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown:legacy-key")]
    [InlineData("dynamic:sales-order;bad")]
    public void Catalog_unknown_blank_malformed_key_not_resolved(string legacyKey)
    {
        Assert.False(LegacyReportArtifactCatalog.TryResolve(legacyKey, out _));
    }

    // ==================== 2. 动态查询 / 导出族真实产物 ====================

    [Fact]
    public async Task Source_dynamic_sales_order_returns_real_excel_and_pdf()
    {
        var source = new LegacyReportArtifactSource(new FakeSalesOrderQuery());

        var result = await source.ReadArtifactsAsync(new LegacyReportSourceRequest
        {
            LegacyKey = "dynamic:sales-order",
            UserId = 1,
        });

        Assert.NotNull(result);
        Assert.NotNull(result!.ExcelBytes);
        Assert.NotNull(result.PdfBytes);
        Assert.StartsWith("PK", Encoding.ASCII.GetString(result.ExcelBytes));
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(result.PdfBytes));
    }

    // ==================== 3. 旧单据导出族真实产物 + 菜单撤销 ====================

    [Fact]
    public async Task Source_bill_export_returns_real_excel()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "bill-ok", "stock-in");
        var source = new LegacyReportArtifactSource(
            new FakeSalesOrderQuery(), null, null, new FakeBillExportReader(), db);

        var result = await source.ReadArtifactsAsync(new LegacyReportSourceRequest
        {
            LegacyKey = "export:bill-proc:stock-in",
            UserId = user.Id,
        });

        Assert.NotNull(result);
        Assert.NotNull(result!.ExcelBytes);
        Assert.Null(result.PdfBytes);
        Assert.StartsWith("PK", Encoding.ASCII.GetString(result.ExcelBytes));
    }

    [Fact]
    public async Task Source_bill_export_denied_menu_returns_null()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "bill-denied", "other-menu");
        var source = new LegacyReportArtifactSource(
            new FakeSalesOrderQuery(), null, null, new FakeBillExportReader(), db);

        var result = await source.ReadArtifactsAsync(new LegacyReportSourceRequest
        {
            LegacyKey = "export:bill-proc:stock-in",
            UserId = user.Id,
        });

        Assert.Null(result);
    }

    // ==================== 4. 报告包 / 单证真实产物 ====================

    [Fact]
    public async Task Source_packet_returns_real_excel_and_pdf()
    {
        var source = new LegacyReportArtifactSource(
            new FakeSalesOrderQuery(), new FakeReceivableQuery(), null, null, null);

        var result = await source.ReadArtifactsAsync(new LegacyReportSourceRequest
        {
            LegacyKey = "packet:customer-report-packet",
            UserId = 1,
            CustomerId = 7,
        });

        Assert.NotNull(result);
        Assert.NotNull(result!.ExcelBytes);
        Assert.NotNull(result.PdfBytes);
        Assert.StartsWith("PK", Encoding.ASCII.GetString(result.ExcelBytes));
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(result.PdfBytes));
    }

    [Fact]
    public async Task Source_trade_document_export_returns_real_excel()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "doc-ok", "doc-center");
        db.TradeDocuments.Add(new TradeDocument
        {
            DocNo = "CI-1",
            DocType = "商业发票",
            Status = "待制作",
            Currency = "USD",
            Amount = 100m,
        });
        db.SaveChanges();

        var documentId = db.TradeDocuments.Single().Id;
        var source = new LegacyReportArtifactSource(
            new FakeSalesOrderQuery(), null, null, null, db);

        var result = await source.ReadArtifactsAsync(new LegacyReportSourceRequest
        {
            LegacyKey = "document:trade-document-export-excel",
            UserId = user.Id,
            DocumentId = documentId,
        });

        Assert.NotNull(result);
        Assert.NotNull(result!.ExcelBytes);
        Assert.Null(result.PdfBytes);
        Assert.StartsWith("PK", Encoding.ASCII.GetString(result.ExcelBytes));
    }

    [Fact]
    public async Task Source_trade_document_export_missing_document_returns_null()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "doc-missing", "doc-center");
        var source = new LegacyReportArtifactSource(
            new FakeSalesOrderQuery(), null, null, null, db);

        var result = await source.ReadArtifactsAsync(new LegacyReportSourceRequest
        {
            LegacyKey = "document:trade-document-export-excel",
            UserId = user.Id,
        });

        Assert.Null(result);
    }

    // ==================== 5. 阻塞 / 缺失规范导出器 ====================

    [Fact]
    public async Task Source_print_template_browser_source_returns_null()
    {
        var source = new LegacyReportArtifactSource(new FakeSalesOrderQuery());

        var result = await source.ReadArtifactsAsync(new LegacyReportSourceRequest
        {
            LegacyKey = "print-template:customer",
            UserId = 1,
        });

        Assert.Null(result);
    }

    private static void AssertSupported(
        Dictionary<string, LegacyReportArtifactCapability> byKey, string key, bool excel, bool pdf)
    {
        Assert.True(byKey.TryGetValue(key, out var capability), $"缺少能力矩阵条目：{key}");
        Assert.Equal(LegacyReportArtifactCapabilityStatus.Supported, capability.Status);
        Assert.Equal(excel, capability.ExcelSupported);
        Assert.Equal(pdf, capability.PdfSupported);
        Assert.Null(capability.BlockReason);
    }

    private static void AssertBlocked(Dictionary<string, LegacyReportArtifactCapability> byKey, string key)
    {
        Assert.True(byKey.TryGetValue(key, out var capability), $"缺少能力矩阵条目：{key}");
        Assert.Equal(LegacyReportArtifactCapabilityStatus.Blocked, capability.Status);
        Assert.False(string.IsNullOrWhiteSpace(capability.BlockReason));
    }

    // ==================== 测试脚手架 ====================

    private static SysUser SeedAuthorizedUser(ErpDbContext db, string name, params string[] menuCodes)
    {
        var user = new SysUser
        {
            UserName = name,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = name,
            Status = UserStatus.Enabled,
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleName = name + "-role", RoleCode = name + "-role", IsSystem = true };
        db.SysRoles.Add(role);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        foreach (var code in menuCodes)
        {
            var menu = new SysMenu { MenuName = code, MenuCode = code, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            db.SaveChanges();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            db.SaveChanges();
        }

        return user;
    }

    private sealed class FakeSalesOrderQuery : IDynamicSalesOrderReportQuery
    {
        public Task<DynamicSalesOrderReportCatalogDto> GetCatalogAsync(long? userId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<DynamicSalesOrderReportPageDto> PreviewAsync(DynamicSalesOrderReportRequest request, long? userId, CancellationToken cancellationToken = default)
        {
            var columns = new List<DynamicSalesOrderReportFieldDto>
            {
                new("orderNo", "订单号", Text, false),
                new("totalAmount", "金额", Number, false),
            };
            var rows = new List<Dictionary<string, object?>>
            {
                new(StringComparer.Ordinal) { ["orderNo"] = "SO-1", ["totalAmount"] = 100.5m },
            };
            return Task.FromResult(new DynamicSalesOrderReportPageDto(
                columns, rows, 1, 1, 20, 1, "只读", "边界", "免责"));
        }
    }

    private sealed class FakeReceivableQuery : IDynamicReceivableReportQuery
    {
        public Task<DynamicReceivableReportCatalogDto> GetCatalogAsync(long? userId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<DynamicReceivableReportPageDto> PreviewAsync(DynamicReceivableReportRequest request, long? userId, CancellationToken cancellationToken = default)
        {
            var columns = new List<DynamicReceivableReportFieldDto>
            {
                new("invoiceNo", "发票号", Text, false),
                new("grossAmount", "含税金额", Number, false),
            };
            var rows = new List<Dictionary<string, object?>>
            {
                new(StringComparer.Ordinal) { ["invoiceNo"] = "INV-1", ["grossAmount"] = 50.25m },
            };
            return Task.FromResult(new DynamicReceivableReportPageDto(
                columns, rows, 1, 1, 20, 1, "只读", "边界", "免责"));
        }
    }

    private sealed class FakeBillExportReader : ILegacyBillExportReadService
    {
        public Task<LegacyBillExportPage> ReadPageAsync(LegacyBillExportQuery query, CancellationToken cancellationToken = default)
        {
            var family = LegacyBillExportCatalog.Resolve(query.FamilyKey);
            var columns = family.Columns.ToList();
            var rows = new List<Dictionary<string, object?>>
            {
                new(StringComparer.Ordinal) { [family.Columns[0].Key] = "B-1" },
            };
            return Task.FromResult(new LegacyBillExportPage
            {
                Columns = columns,
                Rows = rows,
                Total = 1,
                Page = 1,
                PageSize = 20,
                TotalPages = 1,
            });
        }
    }
}
