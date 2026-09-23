using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 单据号生成服务测试
/// </summary>
public class DocumentNumberServiceTests
{
    [Fact]
    public async Task GenerateAsync_配置规则_生成正确格式()
    {
        using var db = TestDbFactory.Create();
        db.SysDocumentNumberRules.Add(new SysDocumentNumberRule
        {
            DocumentType = DocumentType.SalesOrder,
            RuleCode = "SO",
            RuleName = "销售订单",
            Prefix = "SO",
            DateFormat = "yyyyMMdd",
            SerialLength = 4,
            Separator = string.Empty,
            CurrentSequence = 0,
            YearlyReset = true
        });
        await db.SaveChangesAsync();

        var service = new DocumentNumberService(db);
        var no = await service.GenerateAsync(DocumentType.SalesOrder, new DateTime(2026, 8, 19));

        Assert.Equal("SO202608190001", no);
    }

    [Fact]
    public async Task GenerateAsync_连续生成_流水号递增()
    {
        using var db = TestDbFactory.Create();
        db.SysDocumentNumberRules.Add(new SysDocumentNumberRule
        {
            DocumentType = DocumentType.Inquiry,
            RuleCode = "INQ",
            RuleName = "询价单",
            Prefix = "INQ",
            DateFormat = "yyyyMMdd",
            SerialLength = 3,
            Separator = "-",
            CurrentSequence = 0
        });
        await db.SaveChangesAsync();

        var service = new DocumentNumberService(db);
        var no1 = await service.GenerateAsync(DocumentType.Inquiry, new DateTime(2026, 8, 19));
        var no2 = await service.GenerateAsync(DocumentType.Inquiry, new DateTime(2026, 8, 19));

        Assert.Equal("INQ20260819-001", no1);
        Assert.Equal("INQ20260819-002", no2);
    }

    [Fact]
    public async Task GenerateAsync_无规则_使用默认前缀()
    {
        using var db = TestDbFactory.Create();
        var service = new DocumentNumberService(db);
        var no = await service.GenerateAsync(DocumentType.SalesOrder, new DateTime(2026, 8, 19));

        Assert.StartsWith("SO20260819", no);
    }
}
