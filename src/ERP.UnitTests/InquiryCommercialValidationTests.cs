using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-474 询价单商业条款（币种 / 汇率 / 有效期天数 / 数量 · 单价 / 行金额）校验与整理的单元测试
/// （纯内存库 + 直接实例化控制器，不连接 SQL Server、不执行任何 DDL / 部署脚本）：
/// <list type="number">
/// <item><b>唯一权威契约</b>（<see cref="InquiryCommercialValidationRules"/>）：币种必须已定义、汇率 &gt; 0、
/// 有效期必须是 1 ~ 3650 的整数天、数量 &gt; 0、单价 ≥ 0 且全部在持久化精度内可表示；软删除明细行注入拒绝；
/// 行金额一律由服务端按 <see cref="CurrencyAmountRules"/> 的币种精度（JPY 等 0 位、其余 2 位、0.5 进位）派生；</item>
/// <item><b>EF 精度契约</b>：用 SQL Server 提供程序**仅构建模型**（不打开连接）断言询价单商业列的实际存储精度
/// 与规则常量同源；</item>
/// <item><b>控制器行为</b>：新增 / 修改在单号预约与任何字段改写之前拒绝非法条款（零写入、零占号、原证据不变）；
/// 客户端伪造单号 / 行金额被服务端重算与权威覆盖；提交 / 审核 / 转报价单在既有行锁内复核已持久化条款，
/// 非法即原子拒绝且状态 / 报价单 / 单号预约均不变；显式支持的空草稿仍可保存。</item>
/// </list>
/// <para>安全口径：全部使用内存库 <see cref="TestDbFactory"/>，不读取 appsettings / .env / 生产凭据，
/// 不执行 drop / reset，也不使用生产数据。</para>
/// </summary>
public class InquiryCommercialValidationTests
{
    // ==================== 1. 唯一权威契约（纯规则） ====================

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-0.5)]
    [InlineData(0.001)]   // 正数量但会在 DECIMAL(18,2) 被静默舍入为 0
    [InlineData(1.005)]
    public void 条款校验_数量非正或超出持久化精度_拒绝(decimal quantity)
    {
        var inquiry = Draft(Line(quantity, 10m));

        var ex = Assert.Throws<BusinessException>(() =>
            InquiryCommercialValidationRules.ValidateIncomingDraft(inquiry));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("数量必须大于 0", ex.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-0.01)]
    [InlineData(1.005)]
    public void 条款校验_单价为负或超出持久化精度_拒绝(decimal unitPrice)
    {
        var inquiry = Draft(Line(1m, unitPrice));

        var ex = Assert.Throws<BusinessException>(() =>
            InquiryCommercialValidationRules.ValidateIncomingDraft(inquiry));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("单价不能为负", ex.Message);
    }

    [Fact]
    public void 条款校验_币种未定义_拒绝()
    {
        var inquiry = Draft(Line(1m, 1m));
        inquiry.Currency = (Currency)999;

        var ex = Assert.Throws<BusinessException>(() =>
            InquiryCommercialValidationRules.ValidateIncomingDraft(inquiry));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(InquiryCommercialValidationRules.CurrencyText, ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-7.2)]
    [InlineData(7.123)]   // 超出 DECIMAL(18,2)：落库会被静默取整
    public void 条款校验_汇率非正或不可表示_拒绝(decimal exchangeRate)
    {
        var inquiry = Draft(Line(1m, 1m));
        inquiry.ExchangeRate = exchangeRate;

        var ex = Assert.Throws<BusinessException>(() =>
            InquiryCommercialValidationRules.ValidateIncomingDraft(inquiry));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(InquiryCommercialValidationRules.ExchangeRateText, ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(InquiryCommercialValidationRules.MaxValidDays + 1)]
    public void 条款校验_有效期非有界整数天_拒绝(int validDays)
    {
        var inquiry = Draft(Line(1m, 1m));
        inquiry.ValidDays = validDays;

        var ex = Assert.Throws<BusinessException>(() =>
            InquiryCommercialValidationRules.ValidateIncomingDraft(inquiry));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(InquiryCommercialValidationRules.ValidDaysText, ex.Message);
    }

    [Fact]
    public void 条款校验_有效期边界值_接受()
    {
        foreach (var validDays in new[] { InquiryCommercialValidationRules.MinValidDays,
                     InquiryCommercialValidationRules.MaxValidDays })
        {
            var inquiry = Draft(Line(1m, 1m));
            inquiry.ValidDays = validDays;
            InquiryCommercialValidationRules.ValidateIncomingDraft(inquiry);
        }
    }

    [Fact]
    public void 条款校验_软删除明细行注入_拒绝()
    {
        var injected = Draft(Line(1m, 1m), Line(2m, 2m, deleted: true));

        var draft = Assert.Throws<BusinessException>(() =>
            InquiryCommercialValidationRules.ValidateIncomingDraft(injected));
        Assert.Equal(ErrorCodes.InvalidParameter, draft.Code);
        Assert.Equal(InquiryCommercialValidationRules.SoftDeletedDetailText, draft.Message);

        // 已持久化记录的条款复核按有效明细进行：历史软删除行本身不构成拒绝原因。
        InquiryCommercialValidationRules.ValidatePersisted(injected);
    }

    [Fact]
    public void 条款校验_空草稿可保存_提交与转换要求有效明细()
    {
        var empty = Draft(Array.Empty<InquiryDetail>());

        InquiryCommercialValidationRules.NormalizeIncomingDraft(empty);

        var ex = Assert.Throws<BusinessException>(() =>
            InquiryCommercialValidationRules.ValidateOperational(empty));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(InquiryCommercialValidationRules.EmptyOperationalDetailsText, ex.Message);
    }

    // ==================== 3. 控制器：新增（零写入、零占号、服务端权威覆盖） ====================

    [Fact]
    public async Task 新增_非法条款_零写入零占号_单号流水不变()
    {
        using var db = TestDbFactory.Create();
        SeedInquiryNumberRule(db);
        var controller = NewController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(Draft(Line(0m, 10m))));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("数量必须大于 0", ex.Message);
        Assert.Empty(db.Inquiries);
        Assert.Empty(db.InquiryDetails);
        Assert.Equal(7L, db.SysDocumentNumberRules.Single(r => r.DocumentType == DocumentType.Inquiry)
            .CurrentSequence);                                    // 被拒绝的请求绝不消耗单号
    }

    [Theory]
    [InlineData("currency")]
    [InlineData("rate")]
    [InlineData("validDays")]
    [InlineData("injection")]
    public async Task 新增_非法币种汇率有效期与软删除注入_拒绝且零写入(string scenario)
    {
        using var db = TestDbFactory.Create();
        SeedInquiryNumberRule(db);
        var controller = NewController(db);

        var entity = Draft(Line(1m, 1m), Line(2m, 2m));
        switch (scenario)
        {
            case "currency": entity.Currency = (Currency)999; break;
            case "rate": entity.ExchangeRate = 0m; break;
            case "validDays": entity.ValidDays = 0; break;
            default:
                entity.Details = new List<InquiryDetail> { Line(1m, 1m), Line(1m, 1m, deleted: true) };
                break;
        }

        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(entity));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.Inquiries);
        Assert.Empty(db.InquiryDetails);
        Assert.Equal(7L, db.SysDocumentNumberRules.Single(r => r.DocumentType == DocumentType.Inquiry)
            .CurrentSequence);
    }

    [Fact]
    public async Task 新增_伪造单号与身份状态_被服务端权威覆盖_空草稿单号由服务端生成()
    {
        using var db = TestDbFactory.Create();
        SeedInquiryNumberRule(db);
        var entity = Draft(Line(2m, 10m), Line(3m, 4m));
        // ERP-475：新增在单号预约与任何写入之前复核**必填客户的实时主数据引用** —— 夹具播种既有合法客户，
        // 本用例仍只断言「伪造单号 / 身份状态被服务端权威覆盖」。
        entity.CustomerId = SeedCustomer(db).Id;
        entity.InquiryNo = "HACK-ERP474";
        entity.Id = 4242;
        entity.Status = DocumentStatus.Approved;
        entity.IsDeleted = true;
        entity.Details[0].Id = 777;
        entity.Details[0].Amount = 888m;

        var result = await NewController(db).Create(entity);

        Assert.IsType<OkObjectResult>(result);
        var saved = db.Inquiries.Include(i => i.Details).Single();
        Assert.StartsWith("INQ", saved.InquiryNo);
        Assert.NotEqual("HACK-ERP474", saved.InquiryNo);
        Assert.NotEqual(4242, saved.Id);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.False(saved.IsDeleted);
        Assert.Equal(new[] { 20m, 12m }, saved.Details.OrderBy(d => d.Id).Select(d => d.Amount));
        Assert.Equal(8L, db.SysDocumentNumberRules.Single(r => r.DocumentType == DocumentType.Inquiry)
            .CurrentSequence);
    }

    [Fact]
    public async Task 新增_空草稿_作为待提交草稿保存且不生成明细()
    {
        using var db = TestDbFactory.Create();
        // ERP-475：空草稿仍可保存，但必填客户必须解析到既有实时主数据行（夹具播种启用未删除客户）。
        var draft = Draft(Array.Empty<InquiryDetail>());
        draft.CustomerId = SeedCustomer(db).Id;

        Assert.IsType<OkObjectResult>(await NewController(db).Create(draft));

        var saved = db.Inquiries.Single();
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.StartsWith("INQ", saved.InquiryNo);
        Assert.Equal(30, saved.ValidDays);
        Assert.Empty(db.InquiryDetails);
    }

    // ==================== 4. 控制器：修改（原证据不变） ====================

    [Fact]
    public async Task 修改_非法明细与越界有效期_原表头明细与单号全部不变()
    {
        using var db = TestDbFactory.Create();
        var original = Seed(db, "INQ-KEEP", DocumentStatus.Pending, Line(2m, 10m));
        var oldDetailId = db.InquiryDetails.Single().Id;

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db).Update(original.Id, Draft(Line(-3m, 5m))));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        var after = db.Inquiries.Include(i => i.Details).Single();
        Assert.Equal("INQ-KEEP", after.InquiryNo);
        Assert.Equal(DocumentStatus.Pending, after.Status);
        var line = Assert.Single(after.Details);
        Assert.Equal(oldDetailId, line.Id);
        Assert.Equal(2m, line.Quantity);

        var invalidDays = Draft(Line(1m, 1m));
        invalidDays.ValidDays = InquiryCommercialValidationRules.MaxValidDays + 1;
        var daysError = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db).Update(original.Id, invalidDays));
        Assert.Equal(InquiryCommercialValidationRules.ValidDaysText, daysError.Message);
        Assert.Single(db.InquiryDetails);
    }

    // ==================== 5. 提交 / 审核 / 转报价（下游承诺复核已持久化条款） ====================

    [Fact]
    public async Task 提交_已持久化数量非法_原子拒绝且状态与证据不变()
    {
        using var db = TestDbFactory.Create();
        var inquiry = Seed(db, "INQ-BAD-QTY", DocumentStatus.Pending, Line(0m, 10m));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).Submit(inquiry.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("数量必须大于 0", ex.Message);
        Assert.Equal(DocumentStatus.Pending, db.Inquiries.AsNoTracking().Single().Status);
        Assert.Equal(1, db.InquiryDetails.Count());
    }

    [Fact]
    public async Task 提交_空草稿_运营承诺拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var inquiry = Seed(db, "INQ-EMPTY", DocumentStatus.Pending);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).Submit(inquiry.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(InquiryCommercialValidationRules.EmptyOperationalDetailsText, ex.Message);
        Assert.Equal(DocumentStatus.Pending, db.Inquiries.AsNoTracking().Single().Status);
    }

    [Fact]
    public async Task 审核_已持久化币种未定义_原子拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var inquiry = Seed(db, "INQ-BAD-CUR", DocumentStatus.Submitted, Line(1m, 10m));
        var stored = db.Inquiries.Single(i => i.Id == inquiry.Id);
        stored.Currency = (Currency)777;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).Approve(inquiry.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(InquiryCommercialValidationRules.CurrencyText, ex.Message);
        Assert.Equal(DocumentStatus.Submitted, db.Inquiries.AsNoTracking().Single().Status);
    }

    [Fact]
    public async Task 转报价_已持久化有效期越界_拒绝且零报价零占号()
    {
        using var db = TestDbFactory.Create();
        SeedQuotationNumberRule(db);
        var inquiry = Seed(db, "INQ-CVT-BAD", DocumentStatus.Approved, Line(1m, 10m));
        var stored = db.Inquiries.Single(i => i.Id == inquiry.Id);
        stored.ValidDays = 0;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).ToQuotation(inquiry.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(InquiryCommercialValidationRules.ValidDaysText, ex.Message);
        Assert.Empty(db.Quotations);
        Assert.Equal(DocumentStatus.Approved, db.Inquiries.AsNoTracking().Single().Status);
        Assert.Equal(7L, db.SysDocumentNumberRules.Single(r => r.DocumentType == DocumentType.Quotation)
            .CurrentSequence);
    }

    [Fact]
    public async Task 合法询价单_提交审核后转报价仍正常流转()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var inquiry = Draft(Line(2m, 10m));
        inquiry.CustomerId = customer.Id;
        inquiry.InquiryNo = "INQ-GOOD";
        db.Inquiries.Add(inquiry);
        db.SaveChanges();
        var controller = NewController(db);

        Assert.IsType<OkObjectResult>(await controller.Submit(inquiry.Id));
        Assert.IsType<OkObjectResult>(await controller.Approve(inquiry.Id));
        Assert.IsType<OkObjectResult>(await controller.ToQuotation(inquiry.Id));

        Assert.Equal(DocumentStatus.Completed, db.Inquiries.AsNoTracking().Single().Status);
        Assert.Equal(20m, db.Quotations.Include(q => q.Details).Single().TotalAmount);
    }

    // ==================== 工厂与种子数据 ====================

    private static InquiryController NewController(ErpDbContext db)
    {
        var controller = new InquiryController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, TestAuth.SeedPrivilegedUser(db));
        return controller;
    }

    private static void SeedInquiryNumberRule(ErpDbContext db, long current = 7) =>
        SeedNumberRule(db, DocumentType.Inquiry, "INQ", current);

    private static void SeedQuotationNumberRule(ErpDbContext db, long current = 7) =>
        SeedNumberRule(db, DocumentType.Quotation, "QT", current);

    private static void SeedNumberRule(ErpDbContext db, DocumentType type, string prefix, long current)
    {
        db.SysDocumentNumberRules.Add(new SysDocumentNumberRule
        {
            DocumentType = type,
            RuleCode = $"R-{prefix}-{Guid.NewGuid():N}",
            RuleName = $"{prefix} 单号",
            Prefix = prefix,
            DateFormat = "yyyyMMdd",
            SerialLength = 4,
            Separator = string.Empty,
            CurrentSequence = current,
            IsDeleted = false
        });
        db.SaveChanges();
    }

    /// <summary>直接落库的询价单种子（不经契约，允许构造非法历史条款用于承诺前复核测试）。</summary>
    private static Inquiry Seed(ErpDbContext db, string no, DocumentStatus status,
        params InquiryDetail[] lines)
    {
        var inquiry = Draft(lines);
        inquiry.InquiryNo = no;
        inquiry.Status = status;
        db.Inquiries.Add(inquiry);
        db.SaveChanges();
        return inquiry;
    }

    /// <summary>既有客户主数据行（转报价单须复核实时客户引用）。</summary>
    private static BaseCustomer SeedCustomer(ErpDbContext db)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-474-{Guid.NewGuid():N}", CustomerName = "ERP474 客户",
            ContactPerson = "客户联系人", Phone = "13800000000",
            TradeTerms = "FOB", DestinationPort = "NINGBO", PaymentTerms = "T/T"
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static Inquiry Draft(params InquiryDetail[] lines) => new()
    {
        InquiryNo = string.Empty,
        InquiryDate = new DateTime(2026, 9, 1),
        CustomerId = 1,
        ContactPerson = "询价联系人",
        Currency = Currency.USD,
        ExchangeRate = 7.2m,
        ValidDays = 30,
        Status = DocumentStatus.Pending,
        Details = lines.ToList()
    };

    private static InquiryDetail Line(decimal quantity, decimal unitPrice, bool deleted = false) => new()
    {
        ProductId = 1, ProductName = "询价商品", Spec = "标准", Unit = "PCS",
        Quantity = quantity, UnitPrice = unitPrice, Amount = 999m, IsDeleted = deleted
    };

    private static string ColumnType(IModel model, Type entityType, string propertyName)
        => model.FindEntityType(entityType)!.FindProperty(propertyName)!.GetColumnType()!;
}


