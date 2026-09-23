using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using System.Reflection;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-018 报价单治理单元测试：打印数据契约（与工作流同源）、有效期分类与到期提醒、
/// 报价成交率报表口径，以及共享打印配置 / 前端入口的接线静态断言。
/// </summary>
public class QuotationGovernanceTests
{
    private static readonly DateTime AsOf = new(2026, 9, 23);
    private static readonly DateTime PeriodStart = new(2026, 9, 1);
    private static readonly DateTime PeriodEnd = new(2026, 9, 30);

    // ==================== 1. 打印数据（GET /api/sales/quotations/{id}/print） ====================

    [Fact]
    public async Task GetPrint_主表与明细_取工作流持久化数据_合计与折人民币一致()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        // 走工作流新增：明细金额故意写成 1，服务端必须按「数量 × 单价」重算，并按数组顺序重排行号（1、2）
        var id = await CreateQuotationAsync(ctl, new Quotation
        {
            QuotationDate = new DateTime(2026, 9, 23),
            ValidUntil = new DateTime(2026, 10, 23),
            CustomerName = "义乌客户 A",
            Currency = Currency.USD,
            ExchangeRate = 1.2m,
            Details = new List<QuotationDetail>
            {
                new() { ProductCode = "P001", ProductName = "饰品 A", Quantity = 2m, UnitPrice = 500m, Amount = 1m },
                new() { ProductCode = "P002", ProductName = "饰品 B", Quantity = 1m, UnitPrice = 1000m, Amount = 1m },
            }
        });

        // 人为交换持久化行号：打印件必须按持久化的 SortNo 重新排序（而不是按 Id）
        var first = db.QuotationDetails.Single(d => d.ProductCode == "P001");
        var second = db.QuotationDetails.Single(d => d.ProductCode == "P002");
        first.SortNo = 3;
        second.SortNo = 1;
        await db.SaveChangesAsync();

        var print = GetData<Quotation>(await ctl.GetPrint(id));

        Assert.StartsWith("QT", print.QuotationNo);                 // 单号由字轨生成
        Assert.Equal(2000m, print.TotalAmount);                     // 2 × 500 + 1 × 1000
        Assert.Equal(2400m, print.TotalAmountCny);                  // 2000 × 1.2
        Assert.Equal(new[] { "P002", "P001" }, print.Details.Select(d => d.ProductCode));
        Assert.Equal(new[] { 1, 3 }, print.Details.Select(d => d.SortNo));
        Assert.All(print.Details, d => Assert.Equal(Math.Round(d.Quantity * d.UnitPrice, 2), d.Amount));
        Assert.Equal(new DateTime(2026, 10, 23), print.ValidUntil);
        Assert.Equal("义乌客户 A", print.CustomerName);
    }

    [Fact]
    public async Task GetPrint_软删除明细不进入打印件_并按行号排序()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        var id = await CreateQuotationAsync(ctl, new Quotation
        {
            QuotationDate = new DateTime(2026, 9, 23),
            CustomerName = "义乌客户 B",
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            Details = new List<QuotationDetail>
            {
                new() { ProductCode = "P001", ProductName = "饰品 A", Quantity = 2m, UnitPrice = 500m, SortNo = 1 },
                new() { ProductCode = "P002", ProductName = "饰品 B", Quantity = 1m, UnitPrice = 1000m, SortNo = 2 },
            }
        });

        var removed = db.QuotationDetails.Single(d => d.ProductCode == "P001");
        removed.IsDeleted = true;
        db.QuotationDetails.Add(new QuotationDetail
        {
            QuotationId = id, QuotationNo = removed.QuotationNo, SortNo = 0,
            ProductCode = "P003", ProductName = "饰品 C", Quantity = 3m, UnitPrice = 10m, Amount = 30m
        });
        await db.SaveChangesAsync();

        var print = GetData<Quotation>(await ctl.GetPrint(id));

        Assert.Equal(new[] { "P003", "P002" }, print.Details.Select(d => d.ProductCode));  // SortNo 0 排最前
        // 打印件使用工作流保存时算好的合计（绕过工作流直接改库不会重算，这正是「与工作流同源」的含义）
        Assert.Equal(2000m, print.TotalAmount);
    }

    [Fact]
    public async Task GetPrint_报价单不存在_抛NotFound()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetPrint(999999));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    // ==================== 2. 有效期分类规则（QuotationValidityRules） ====================

    [Theory]
    [InlineData(-1, "已过期", "danger", -1, true)]
    [InlineData(0, "今日到期", "warning", 0, true)]
    [InlineData(3, "即将到期", "warning", 3, true)]
    [InlineData(7, "即将到期", "warning", 7, true)]      // 窗口边界包含第 7 天
    [InlineData(8, "有效", "success", 8, false)]
    [InlineData(30, "有效", "success", 30, false)]
    public void 有效期状态_五种分类与窗口边界(int offsetDays, string status, string level, int expectedDays, bool isDue)
    {
        var validUntil = AsOf.AddDays(offsetDays);

        Assert.Equal(status, QuotationValidityRules.StatusOf(validUntil, AsOf, QuotationValidityRules.DefaultAheadDays));
        Assert.Equal(level, QuotationValidityRules.LevelOf(validUntil, AsOf, QuotationValidityRules.DefaultAheadDays));
        Assert.Equal(expectedDays, QuotationValidityRules.DaysRemaining(validUntil, AsOf));
        Assert.Equal(isDue, QuotationValidityRules.IsDue(validUntil, AsOf, QuotationValidityRules.DefaultAheadDays));
    }

    [Fact]
    public void 有效期规则_未设置有效期不参与提醒_负数窗口回落默认7天()
    {
        Assert.Equal(QuotationValidityRules.NoneText, QuotationValidityRules.StatusOf(null, AsOf));
        Assert.Equal(QuotationValidityRules.NeutralLevel, QuotationValidityRules.LevelOf(null, AsOf));
        Assert.Null(QuotationValidityRules.DaysRemaining(null, AsOf));
        Assert.False(QuotationValidityRules.IsDue(null, AsOf));

        Assert.Equal(QuotationValidityRules.DefaultAheadDays, QuotationValidityRules.NormalizeAheadDays(-5));
        Assert.Equal(17, QuotationValidityRules.NormalizeAheadDays(17));
        // 负数窗口视为默认 7 天：+8 天不再是「即将到期」
        Assert.Equal(QuotationValidityRules.ValidText, QuotationValidityRules.StatusOf(AsOf.AddDays(8), AsOf, -5));
        // 自定义窗口 0 天：只有已过期与今日到期算紧急
        Assert.Equal(QuotationValidityRules.ValidText, QuotationValidityRules.StatusOf(AsOf.AddDays(1), AsOf, 0));
        Assert.Equal(0, QuotationValidityRules.UrgencyOf(AsOf.AddDays(1), AsOf, 0));
    }

    [Fact]
    public void 有效期紧急度_已过期大于今日到期大于即将到期()
    {
        Assert.Equal(3, QuotationValidityRules.UrgencyOf(AsOf.AddDays(-1), AsOf));
        Assert.Equal(2, QuotationValidityRules.UrgencyOf(AsOf, AsOf));
        Assert.Equal(1, QuotationValidityRules.UrgencyOf(AsOf.AddDays(5), AsOf));
        Assert.Equal(0, QuotationValidityRules.UrgencyOf(AsOf.AddDays(60), AsOf));
        Assert.Equal(0, QuotationValidityRules.UrgencyOf(null, AsOf));
    }

    // ==================== 3. 有效期提醒端点（GET /api/sales/quotations/validity-due） ====================

    [Fact]
    public async Task ValidityDue_默认窗口_排除作废与未设置有效期_并按紧急度排序()
    {
        using var db = TestDbFactory.Create();
        db.Quotations.AddRange(
            NewQuotation("QT-EXP", "业务员 A", AsOf.AddDays(-2), 500m),
            NewQuotation("QT-DUE", "业务员 A", AsOf, 600m),
            NewQuotation("QT-SOON", "业务员 B", AsOf.AddDays(5), 700m),
            NewQuotation("QT-EDGE", "业务员 B", AsOf.AddDays(7), 800m),
            NewQuotation("QT-VALID", "业务员 B", AsOf.AddDays(30), 900m),
            NewQuotation("QT-NONE", "业务员 C", null, 1000m),
            NewQuotation("QT-VOID", "业务员 C", AsOf.AddDays(-1), 1100m, DocumentStatus.Cancelled));
        await db.SaveChangesAsync();
        var ctl = NewController(db);

        var items = GetData<List<QuotationValidityItem>>(
            await ctl.ValidityDue(AsOf, QuotationValidityRules.DefaultAheadDays));

        Assert.Equal(new[] { "QT-EXP", "QT-DUE", "QT-SOON", "QT-EDGE" }, items.Select(i => i.QuotationNo));
        Assert.Equal(new[] { "已过期", "今日到期", "即将到期", "即将到期" }, items.Select(i => i.ValidityStatus));
        Assert.Equal(new[] { "danger", "warning", "warning", "warning" }, items.Select(i => i.ValidityLevel));
        Assert.Equal(new int?[] { -2, 0, 5, 7 }, items.Select(i => i.ValidDays));
        Assert.Equal(new DateTime?[] { AsOf.AddDays(-2), AsOf, AsOf.AddDays(5), AsOf.AddDays(7) },
            items.Select(i => i.ValidUntil).ToArray());
        Assert.Equal(500m, items[0].TotalAmount);
        Assert.Equal(3600m, items[0].TotalAmountCny);            // 500 × 7.2
        Assert.Equal(Currency.USD, items[0].Currency);
        Assert.All(items, i => Assert.False(i.Converted));
    }

    [Fact]
    public async Task ValidityDue_自定义窗口_仅返回窗口内到期()
    {
        using var db = TestDbFactory.Create();
        db.Quotations.AddRange(
            NewQuotation("QT-EXP", "业务员 A", AsOf.AddDays(-1), 100m),
            NewQuotation("QT-TODAY", "业务员 A", AsOf, 100m),
            NewQuotation("QT-D3", "业务员 A", AsOf.AddDays(3), 100m),
            NewQuotation("QT-D10", "业务员 A", AsOf.AddDays(10), 100m));
        await db.SaveChangesAsync();
        var ctl = NewController(db);

        var noWindow = GetData<List<QuotationValidityItem>>(await ctl.ValidityDue(AsOf, 0));
        Assert.Equal(new[] { "QT-EXP", "QT-TODAY" }, noWindow.Select(i => i.QuotationNo));

        var tenDays = GetData<List<QuotationValidityItem>>(await ctl.ValidityDue(AsOf, 10));
        Assert.Equal(new[] { "QT-EXP", "QT-TODAY", "QT-D3", "QT-D10" }, tenDays.Select(i => i.QuotationNo));

        var negative = GetData<List<QuotationValidityItem>>(await ctl.ValidityDue(AsOf, -3));   // 负数 → 默认 7 天
        Assert.Equal(new[] { "QT-EXP", "QT-TODAY", "QT-D3" }, negative.Select(i => i.QuotationNo));

        var today = GetData<List<QuotationValidityItem>>(await ctl.ValidityDue(null, 7));       // 基准日默认今天
        Assert.All(today, i => Assert.Equal(QuotationValidityRules.StatusOf(i.ValidUntil, DateTime.Today), i.ValidityStatus));
    }

    [Fact]
    public async Task ValidityDue_已转出报价单_标记Converted但仍列出便于核对()
    {
        using var db = TestDbFactory.Create();
        var converted = NewQuotation("QT-PI", "业务员 A", AsOf.AddDays(-1), 1000m);
        var completed = NewQuotation("QT-DONE", "业务员 A", AsOf.AddDays(2), 2000m, DocumentStatus.Completed);
        db.Quotations.AddRange(converted, completed);
        await db.SaveChangesAsync();
        db.ProformaInvoices.Add(new ProformaInvoice
        {
            PiNo = "PI2609230001", PiDate = AsOf, QuotationId = converted.Id, QuotationNo = converted.QuotationNo,
            CustomerName = converted.CustomerName, Currency = Currency.USD, ExchangeRate = 7.2m
        });
        await db.SaveChangesAsync();
        var ctl = NewController(db);

        var items = GetData<List<QuotationValidityItem>>(await ctl.ValidityDue(AsOf, 7));

        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.True(i.Converted));
        Assert.Equal(DocumentStatus.Completed, items.Single(i => i.QuotationNo == "QT-DONE").Status);
        Assert.Equal(DocumentStatus.Approved, items.Single(i => i.QuotationNo == "QT-PI").Status);
    }

    [Fact]
    public void 有效期提醒路由_为validity_due字面量路由()
    {
        Assert.Contains("validity-due",
            GetGetTemplates(typeof(QuotationController), nameof(QuotationController.ValidityDue)));
    }

    // ==================== 4. 报价成交率报表（GET /api/reports/quotation-conversion） ====================

    [Fact]
    public async Task GetQuotationConversionAsync_按业务员聚合_成交率与金额口径()
    {
        using var db = TestDbFactory.Create();
        // 业务员 A：3 张有效报价（1 转 PI、1 转销售订单、1 未成交），每张 1000 → 成交率 2/3
        var toPi = NewQuotation("QT-A1", "业务员 A", PeriodEnd.AddDays(30), 1000m, DocumentStatus.Completed, new DateTime(2026, 9, 5));
        var toOrder = NewQuotation("QT-A2", "业务员 A", PeriodEnd.AddDays(30), 1000m, DocumentStatus.Approved, new DateTime(2026, 9, 10));
        var open = NewQuotation("QT-A3", "业务员 A", PeriodEnd.AddDays(30), 1000m, DocumentStatus.Approved, new DateTime(2026, 9, 15));
        db.Quotations.AddRange(toPi, toOrder, open);
        await db.SaveChangesAsync();
        db.ProformaInvoices.Add(new ProformaInvoice
        {
            PiNo = "PI-A1", PiDate = new DateTime(2026, 9, 6), QuotationId = toPi.Id, QuotationNo = toPi.QuotationNo,
            CustomerName = toPi.CustomerName, Currency = Currency.USD, ExchangeRate = 7.2m
        });
        db.SalesOrders.Add(new SalesOrder
        {
            OrderNo = "SO-A2", OrderDate = new DateTime(2026, 9, 12), SourceQuotationId = toOrder.Id,
            SourceQuotationNo = toOrder.QuotationNo
        });
        await db.SaveChangesAsync();

        var rows = await new ReportService(db).GetQuotationConversionAsync(PeriodStart, PeriodEnd);

        var row = Assert.Single(rows);
        Assert.Equal("业务员 A", row.SalesmanName);
        Assert.Equal(3, row.QuotationCount);
        Assert.Equal(2, row.ConvertedCount);
        Assert.Equal(66.67m, row.ConversionRate);       // 2 ÷ 3 × 100，保留 2 位
        Assert.Equal(3000m, row.TotalAmount);
        Assert.Equal(2000m, row.ConvertedAmount);
        Assert.Equal(1000m, row.AvgConvertedAmount);
        Assert.Equal(0, row.CancelledCount);
        Assert.Equal(0, row.ExpiredCount);
    }

    [Fact]
    public async Task GetQuotationConversionAsync_状态已完成但无来源外键_计入分子()
    {
        using var db = TestDbFactory.Create();
        db.Quotations.Add(NewQuotation("QT-DONE", "业务员 B", PeriodEnd.AddDays(30), 800m,
            DocumentStatus.Completed, new DateTime(2026, 9, 8)));
        await db.SaveChangesAsync();

        var rows = await new ReportService(db).GetQuotationConversionAsync(PeriodStart, PeriodEnd);

        var row = Assert.Single(rows);
        Assert.Equal(1, row.QuotationCount);
        Assert.Equal(1, row.ConvertedCount);
        Assert.Equal(100m, row.ConversionRate);
    }

    [Fact]
    public async Task GetQuotationConversionAsync_已作废不计入分母_单独计数且成交率为0()
    {
        using var db = TestDbFactory.Create();
        db.Quotations.AddRange(
            NewQuotation("QT-C1", "业务员 C", PeriodEnd.AddDays(30), 100m, DocumentStatus.Cancelled, new DateTime(2026, 9, 3)),
            NewQuotation("QT-C2", "业务员 C", PeriodEnd.AddDays(30), 200m, DocumentStatus.Cancelled, new DateTime(2026, 9, 4)));
        await db.SaveChangesAsync();

        var rows = await new ReportService(db).GetQuotationConversionAsync(PeriodStart, PeriodEnd);

        var row = Assert.Single(rows);
        Assert.Equal(0, row.QuotationCount);
        Assert.Equal(0, row.ConvertedCount);
        Assert.Equal(0m, row.ConversionRate);           // 分母为 0 的保护
        Assert.Equal(2, row.CancelledCount);
        Assert.Equal(0m, row.TotalAmount);
    }

    [Fact]
    public async Task GetQuotationConversionAsync_期间外与软删除不计入_并统计已过期未成交()
    {
        using var db = TestDbFactory.Create();
        var expired = NewQuotation("QT-EXP", "业务员 D", PeriodEnd.AddDays(-5), 700m, DocumentStatus.Approved, new DateTime(2026, 9, 9));
        var valid = NewQuotation("QT-VALID", "业务员 D", PeriodEnd.AddDays(40), 300m, DocumentStatus.Approved, new DateTime(2026, 9, 11));
        var deleted = NewQuotation("QT-DEL", "业务员 D", PeriodEnd.AddDays(-5), 900m, DocumentStatus.Approved, new DateTime(2026, 9, 12));
        deleted.IsDeleted = true;
        var outOfRange = NewQuotation("QT-OUT", "业务员 D", PeriodEnd.AddDays(-5), 500m, DocumentStatus.Approved, new DateTime(2026, 8, 20));
        db.Quotations.AddRange(expired, valid, deleted, outOfRange);
        await db.SaveChangesAsync();

        var rows = await new ReportService(db).GetQuotationConversionAsync(PeriodStart, PeriodEnd);

        var row = Assert.Single(rows);
        Assert.Equal(2, row.QuotationCount);            // 期间内 2 张（软删除与期间外各排除 1 张）
        Assert.Equal(1000m, row.TotalAmount);
        Assert.Equal(1, row.ExpiredCount);             // 以期间结束日为基准日：有效期 9/25 的那张已过期
        Assert.Equal(0, row.ConvertedCount);
    }

    [Fact]
    public async Task GetQuotationConversionAsync_未填写业务员归入未指定业务员()
    {
        using var db = TestDbFactory.Create();
        db.Quotations.AddRange(
            NewQuotation("QT-N1", "", PeriodEnd.AddDays(30), 400m, DocumentStatus.Approved, new DateTime(2026, 9, 6)),
            NewQuotation("QT-N2", "业务员 E", PeriodEnd.AddDays(30), 600m, DocumentStatus.Approved, new DateTime(2026, 9, 7)));
        await db.SaveChangesAsync();

        var rows = await new ReportService(db).GetQuotationConversionAsync(PeriodStart, PeriodEnd);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.SalesmanName == "未指定业务员" && r.QuotationCount == 1);
        Assert.Contains(rows, r => r.SalesmanName == "业务员 E" && r.QuotationCount == 1);
    }

    [Fact]
    public async Task GetQuotationConversionAsync_无报价单_返回空列表()
    {
        using var db = TestDbFactory.Create();

        var rows = await new ReportService(db).GetQuotationConversionAsync(PeriodStart, PeriodEnd);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task 成交率报表端点_经报表控制器可访问且路由为quotation_conversion()
    {
        using var db = TestDbFactory.Create();
        db.Quotations.Add(NewQuotation("QT-API", "业务员 F", PeriodEnd.AddDays(30), 1200m,
            DocumentStatus.Approved, new DateTime(2026, 9, 9)));
        await db.SaveChangesAsync();
        var controller = new ReportController(new ReportService(db));

        var rows = GetData<List<ReportDtos.QuotationConversionItem>>(
            await controller.QuotationConversion(PeriodStart, PeriodEnd));

        var row = Assert.Single(rows);
        Assert.Equal("业务员 F", row.SalesmanName);
        Assert.Equal(1200m, row.TotalAmount);
        Assert.Equal(0m, row.ConversionRate);
        Assert.Contains("quotation-conversion",
            GetGetTemplates(typeof(ReportController), nameof(ReportController.QuotationConversion)));
    }

    [Fact]
    public void 文档_成交率口径与打印机注册章节编号一致()
    {
        var doc = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "docs", "报价单与PI设计方案.md")));

        Assert.Contains("## 10. ERP-018：打印注册与有效期治理", doc);
        Assert.Contains("### 10.1 共享打印三件套注册", doc);
        Assert.Contains("### 10.2 有效期治理口径", doc);
        Assert.Contains("### 10.3 报价成交率计算口径", doc);
        Assert.Contains("| 成交率 | `已转出数 ÷ 有效报价数 × 100`", doc);
        Assert.DoesNotContain("§11.3", doc);       // 章节重编号后不得残留旧引用
    }

    // ==================== 5. 共享打印配置与前端入口接线（静态断言） ====================

    [Fact]
    public void 共享打印配置_报价单与PI已注册_且不进入存储过程菜单路由映射()
    {
        var directory = JsDirectory();
        Assert.True(Directory.Exists(directory), $"未找到前端脚本目录：{directory}");

        var billConfigEf = File.ReadAllText(Path.Combine(directory, "bill-config-ef.js"));
        foreach (var billType in new[] { "quotation", "proforma-invoice" })
        {
            Assert.Contains($"billType: '{billType}'", billConfigEf);
        }
        Assert.Contains("Object.assign(BILL_CONFIG", billConfigEf);
        Assert.Contains("ef: true", billConfigEf);
        Assert.Contains("api: '/api/sales/quotations'", billConfigEf);
        Assert.Contains("api: '/api/sales/proforma-invoices'", billConfigEf);
        Assert.Contains("noKey: 'quotationNo'", billConfigEf);
        Assert.Contains("noKey: 'piNo'", billConfigEf);

        // 报价单/PI 是 EF 主子表单据：不得登记进 BILL_CODE_MAP（否则菜单会跳到存储过程单据页）
        var billV2 = File.ReadAllText(Path.Combine(directory, "bill-v2.js"));
        var mapStart = billV2.IndexOf("const BILL_CODE_MAP = {", StringComparison.Ordinal);
        var mapEnd = billV2.IndexOf("};", mapStart, StringComparison.Ordinal);
        var billCodeMap = billV2[mapStart..mapEnd];
        Assert.DoesNotContain("quotation", billCodeMap);
        Assert.DoesNotContain("proforma-invoice", billCodeMap);

        // 脚本加载顺序：bill-config-ef.js 必须在 bill-config3.js 之后（Object.assign 到已存在的 BILL_CONFIG）
        var index = File.ReadAllText(Path.Combine(directory, "..", "index.html"));
        Assert.Contains("/js/bill-config-ef.js", index);
        Assert.True(index.IndexOf("/js/bill-config3.js", StringComparison.Ordinal)
                    < index.IndexOf("/js/bill-config-ef.js", StringComparison.Ordinal),
            "bill-config-ef.js 必须在 bill-config3.js 之后加载");

        // 打印设计单据清单去重（EF 单据不再重复出现在「基础资料」分组）
        Assert.Contains("!(BILL_CONFIG || {})[k]", File.ReadAllText(Path.Combine(directory, "print-design.js")));
        Assert.Contains("!(BILL_CONFIG || {})[k]", File.ReadAllText(Path.Combine(directory, "pd-grid.js")));
    }

    [Fact]
    public void 前端_打印三件套与有效期治理入口_接线一致()
    {
        var directory = JsDirectory();
        var modules = File.ReadAllText(Path.Combine(directory, "modules.js"));
        var salesPi = File.ReadAllText(Path.Combine(directory, "sales-pi.js"));
        var reports = File.ReadAllText(Path.Combine(directory, "reports.js"));
        var crud = File.ReadAllText(Path.Combine(directory, "crud.js"));

        // 行操作 → 全局函数（打印预览 / 直接打印 / 打印设计）
        foreach (var function in new[] { "previewSalesDocPrint", "printSalesDocDirect", "designSalesDocPrint" })
        {
            Assert.Contains($"onclick: '{function}'", modules);
            Assert.Contains($"function {function}(", salesPi);
        }
        Assert.Contains("function printSalesDoc()", salesPi);
        Assert.Contains("openSalesDocPrintWindow(ctx)", salesPi);
        Assert.Contains("gotoPrintDesign(code)", salesPi);

        // 有效期治理：列表派生列 + 提醒弹窗 + 后端端点
        Assert.Contains("key: 'validityStatus'", modules);
        Assert.Contains("render: row => quotationValidityBadge(row)", modules);
        Assert.Contains("virtual: true", modules);                              // 派生列标记
        Assert.Contains("c.type !== 'image' && !c.virtual", File.ReadAllText(Path.Combine(directory, "base-io.js")));
        Assert.Contains("function quotationValidityBadge(", salesPi);
        Assert.Contains("onclick: 'openQuotationValidityReminder'", modules);
        Assert.Contains("function openQuotationValidityReminder(", salesPi);
        Assert.Contains("/api/sales/quotations/validity-due?aheadDays=", salesPi);
        Assert.Contains("if (typeof c.render === 'function')", crud);          // crud.js 派生列渲染钩子

        // 成交率报表：模块入口 + 报表框架注册 + 接口路径
        Assert.Contains("onclick: 'openQuotationConversionReport'", modules);
        Assert.Contains("function openQuotationConversionReport(", salesPi);
        Assert.Contains("REPORTS['quotation-conversion']", salesPi);
        Assert.Contains("'quotation-conversion': { api: '/api/reports/quotation-conversion'", reports);
    }

    // ==================== 工厂与种子数据 ====================

    private static QuotationController NewController(ErpDbContext db)
        => new(db, new DocumentNumberService(db));

    /// <summary>读取控制器返回的统一响应数据</summary>
    private static T GetData<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, response.Code);
        return response.Data!;
    }

    /// <summary>走工作流新增报价单并返回 Id（单号由字轨生成，金额由服务端复核）</summary>
    private static async Task<long> CreateQuotationAsync(QuotationController ctl, Quotation entity)
    {
        var created = GetData<object>(await ctl.Create(entity));
        return (long)created.GetType().GetProperty(nameof(Quotation.Id))!.GetValue(created)!;
    }

    /// <summary>种一张报价单（USD / 汇率 7.2 / 折人民币自动按 7.2 计），有效期可空</summary>
    private static Quotation NewQuotation(string no, string salesmanName, DateTime? validUntil, decimal totalAmount,
        DocumentStatus status = DocumentStatus.Approved, DateTime? quotationDate = null)
        => new()
        {
            QuotationNo = no,
            QuotationDate = quotationDate ?? AsOf,
            ValidUntil = validUntil,
            CustomerId = 1L,
            CustomerName = "义乌客户（治理测试）",
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            TotalAmount = totalAmount,
            TotalAmountCny = Math.Round(totalAmount * 7.2m, 2),
            SalesmanId = 9L,
            SalesmanName = salesmanName,
            Status = status
        };

    /// <summary>读取控制器方法上的 HTTP 路由模板（GET）</summary>
    private static List<string> GetGetTemplates(Type controller, string methodName)
    {
        var method = controller.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        return method!.GetCustomAttributes<HttpMethodAttribute>(true)
            .Select(a => a.Template ?? string.Empty)
            .ToList();
    }

    /// <summary>前端脚本目录（沿测试程序集输出目录上溯到仓库根，与 UiTestFixture 同一约定）</summary>
    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));
}
