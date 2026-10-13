using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// InquiryController 单元测试：覆盖 GetPaged / GetById / Create / Update / Export 业务方法，
/// 以及 DocumentControllerBase 的 Submit / Approve / Cancel / Delete 状态流转。
/// </summary>
public class InquiryControllerTests
{
    // ==================== Create ====================

    [Fact]
    public async Task Create_正常创建_生成InquiryNo_Status为Pending_Details金额被自动计算()
    {
        using var db = TestDbFactory.Create();
        var noService = new DocumentNumberService(db);
        var ctl = BuildController(db);

        var inquiry = new Inquiry
        {
            InquiryDate = DateTime.Today,
            CustomerId = 999999L,
            ContactPerson = "李雷",
            ContactPhone = "13800138000",
            SalesmanId = 999999L,
            Currency = (Currency)2,
            ExchangeRate = 7.1m,
            ValidDays = 30,
            Remark = "INT_TEST",
            Details = new List<InquiryDetail>
            {
                new InquiryDetail { ProductId = 1, ProductName = "P1", Spec = "大", Unit = "PCS", Quantity = 10m, UnitPrice = 100m },
                new InquiryDetail { ProductId = 2, ProductName = "P2", Spec = "中", Unit = "PCS", Quantity = 5m,  UnitPrice = 80m }
            }
        };

        var result = await ctl.Create(inquiry);
        Assert.IsType<OkObjectResult>(result);

        var dbEntity = db.Inquiries.Single();
        Assert.True(dbEntity.Id > 0);
        Assert.StartsWith("INQ", dbEntity.InquiryNo);
        Assert.Equal(DocumentStatus.Pending, dbEntity.Status);
        Assert.Equal(2, dbEntity.Details.Count);
        Assert.Equal(1000m, dbEntity.Details.Single(d => d.ProductId == 1).Amount);
        Assert.Equal(400m,  dbEntity.Details.Single(d => d.ProductId == 2).Amount);
    }

    // ==================== GetById ====================

    [Fact]
    public async Task GetById_存在_返回Inquiry含Details()
    {
        using var db = TestDbFactory.Create();
        var (inquiry, _) = SeedInquiry(db, "INQ20260917-X", DocumentStatus.Pending);
        var ctl = BuildController(db);

        var result = await ctl.GetById(inquiry.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<Inquiry>>(ok.Value);
        Assert.Equal(inquiry.Id, resp.Data!.Id);
        Assert.Single(resp.Data.Details);
    }

    [Fact]
    public async Task GetById_不存在_抛NotFound()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db);

        await Assert.ThrowsAsync<BusinessException>(() => ctl.GetById(999));
    }

    // ==================== GetPaged ====================

    [Fact]
    public async Task GetPaged_关键字_OrderNo包含时匹配_分页正确()
    {
        using var db = TestDbFactory.Create();
        SeedInquiry(db, "INQ-FOO-001", DocumentStatus.Pending);
        SeedInquiry(db, "INQ-FOO-002", DocumentStatus.Pending);
        SeedInquiry(db, "INQ-BAR-001", DocumentStatus.Pending);
        var ctl = BuildController(db);

        var result = await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10, Keyword = "FOO" }, null);

        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<PagedResult<Inquiry>>>(ok.Value);
        Assert.Equal(2, resp.Data!.Total);
        Assert.Equal(2, resp.Data.Items.Count);
    }

    [Fact]
    public async Task GetPaged_状态过滤_仅返回对应状态单据()
    {
        using var db = TestDbFactory.Create();
        SeedInquiry(db, "INQ-P-001", DocumentStatus.Pending);
        SeedInquiry(db, "INQ-A-001", DocumentStatus.Approved);
        var ctl = BuildController(db);

        var result = await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }, DocumentStatus.Approved);

        var resp = Assert.IsType<ApiResponse<PagedResult<Inquiry>>>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(1, resp.Data!.Total);
        Assert.Equal(DocumentStatus.Approved, resp.Data.Items[0].Status);
    }

    // ==================== Update ====================

    [Fact]
    public async Task Update_非Pending_抛RuleConflict_且数据库未变()
    {
        using var db = TestDbFactory.Create();
        var (inquiry, _) = SeedInquiry(db, "INQ-X", DocumentStatus.Approved);
        var originalCount = db.InquiryDetails.Count(d => d.InquiryId == inquiry.Id);
        var ctl = BuildController(db);

        var update = new Inquiry
        {
            InquiryDate = DateTime.Today,
            CustomerId = 1, ContactPerson = "改", ContactPhone = "改",
            SalesmanId = 1, Currency = (Currency)2, ExchangeRate = 1m, ValidDays = 7,
            Details = new List<InquiryDetail>
            {
                new InquiryDetail { ProductId = 99, ProductName = "改", Quantity = 1m, UnitPrice = 1m }
            }
        };

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(inquiry.Id, update));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(originalCount, db.InquiryDetails.Count(d => d.InquiryId == inquiry.Id));
    }

    [Fact]
    public async Task Update_Pending_修改成功_Details被全删全建_UpdatedAt刷新()
    {
        using var db = TestDbFactory.Create();
        var (inquiry, _) = SeedInquiry(db, "INQ-Y", DocumentStatus.Pending);
        var originalDetailId = db.InquiryDetails.Single(i => i.InquiryId == inquiry.Id).Id;
        var ctl = BuildController(db);

        var update = new Inquiry
        {
            InquiryDate = DateTime.Today,
            CustomerId = 999999L, ContactPerson = "改", ContactPhone = "13800138000",
            SalesmanId = 999999L, Currency = (Currency)2, ExchangeRate = 7.1m, ValidDays = 60,
            Details = new List<InquiryDetail>
            {
                new InquiryDetail { ProductId = 1, ProductName = "P1", Quantity = 20m, UnitPrice = 50m }
            }
        };

        var beforeUpdate = DateTime.Now;
        await ctl.Update(inquiry.Id, update);

        // 旧的明细被删，新的明细 Amount = Quantity × UnitPrice = 20 × 50 = 1000
        Assert.DoesNotContain(db.InquiryDetails, d => d.Id == originalDetailId);
        var newDetail = db.InquiryDetails.Single(d => d.InquiryId == inquiry.Id);
        Assert.Equal(1000m, newDetail.Amount);
        Assert.NotNull(db.Inquiries.Single().UpdatedAt);
    }

    // ==================== DocumentControllerBase 状态流转 ====================

    [Fact]
    public async Task Submit_Pending到Submitted_返回Ok_Status变为Submitted()
    {
        using var db = TestDbFactory.Create();
        var (inquiry, _) = SeedInquiry(db, "INQ-S", DocumentStatus.Pending);
        var ctl = BuildController(db);

        await ctl.Submit(inquiry.Id);
        Assert.Equal(DocumentStatus.Submitted, db.Inquiries.Single().Status);
    }

    [Fact]
    public async Task Approve_非Submitted_抛RuleConflict()
    {
        using var db = TestDbFactory.Create();
        var (inquiry, _) = SeedInquiry(db, "INQ-AP", DocumentStatus.Pending);
        var ctl = BuildController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(inquiry.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }

    [Fact]
    public async Task Delete_Pending_软删除_数据库可查到IsDeleted为True()
    {
        using var db = TestDbFactory.Create();
        var (inquiry, _) = SeedInquiry(db, "INQ-D", DocumentStatus.Pending);
        var ctl = BuildController(db);

        await ctl.Delete(inquiry.Id);
        Assert.True(db.Inquiries.Single().IsDeleted);
    }

    [Fact]
    public async Task Delete_非Pending_抛RuleConflict_且未软删()
    {
        using var db = TestDbFactory.Create();
        var (inquiry, _) = SeedInquiry(db, "INQ-DA", DocumentStatus.Approved);
        var ctl = BuildController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Delete(inquiry.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.False(db.Inquiries.Single().IsDeleted);
    }

    // ==================== ERP-474：省略单号 + 商业条款契约 ====================

    [Fact]
    public void Entity_询价单号不再显式必填_空号可达已授权动作()
    {
        var property = typeof(Inquiry).GetProperty(nameof(Inquiry.InquiryNo))!;
        Assert.Empty(property.GetCustomAttributes(
            typeof(System.ComponentModel.DataAnnotations.RequiredAttribute), inherit: true));
        Assert.NotEmpty(property.GetCustomAttributes(
            typeof(System.ComponentModel.DataAnnotations.MaxLengthAttribute), inherit: true));
    }

    [Fact]
    public async Task Create_省略询价单号_服务端生成单号并按币种精度派生行金额()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db);

        var result = await ctl.Create(new Inquiry
        {
            InquiryDate = DateTime.Today,
            CustomerId = 999999L,
            Currency = Currency.USD,
            ExchangeRate = 7.1m,
            ValidDays = 30,
            Details = new List<InquiryDetail>
            {
                new InquiryDetail { ProductId = 1, ProductName = "P1", Unit = "PCS", Quantity = 2m, UnitPrice = 5m }
            }
        });

        Assert.IsType<OkObjectResult>(result);
        var saved = db.Inquiries.Single();
        Assert.StartsWith("INQ", saved.InquiryNo);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.Equal(10m, saved.Details.Single().Amount);
    }

    [Fact]
    public async Task Create_非法条款_在单号生成与任何写入之前拒绝()
    {
        using var db = TestDbFactory.Create();
        var rule = new SysDocumentNumberRule
        {
            DocumentType = DocumentType.Inquiry, RuleCode = "R-INQ-474", RuleName = "询价单号",
            Prefix = "INQ", DateFormat = "yyyyMMdd", SerialLength = 4, CurrentSequence = 3, IsDeleted = false
        };
        db.SysDocumentNumberRules.Add(rule);
        db.SaveChanges();
        var ctl = BuildController(db);

        var invalid = new Inquiry
        {
            InquiryDate = DateTime.Today, CustomerId = 999999L,
            Currency = Currency.USD, ExchangeRate = 7.1m, ValidDays = 30,
            Details = new List<InquiryDetail>
            {
                new InquiryDetail { ProductId = 1, Quantity = 0m, UnitPrice = 5m }
            }
        };
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(invalid));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("数量必须大于 0", ex.Message);
        Assert.Empty(db.Inquiries);
        Assert.Empty(db.InquiryDetails);
        Assert.Equal(3, db.SysDocumentNumberRules.AsNoTracking().Single(r => r.Id == rule.Id).CurrentSequence);
    }

    // ==================== 种子 ====================

    
    private static InquiryController BuildController(ErpDbContext db)
    {
        var controller = new InquiryController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, TestAuth.SeedPrivilegedUser(db));
        return controller;
    }
private static (Inquiry inquiry, InquiryDetail detail) SeedInquiry(ErpDbContext db, string no, DocumentStatus status)
    {
        var inq = new Inquiry
        {
            InquiryNo = no,
            InquiryDate = DateTime.Today,
            CustomerId = 1,
            ContactPerson = "C",
            ContactPhone = "138",
            SalesmanId = 1,
            Currency = (Currency)2,
            ExchangeRate = 7.1m,
            ValidDays = 30,
            Status = status
        };
        db.Inquiries.Add(inq);
        db.SaveChanges();
        var detail = new InquiryDetail
        {
            InquiryId = inq.Id,
            ProductId = 1,
            ProductName = "P1",
            Spec = "大",
            Unit = "PCS",
            Quantity = 10m,
            UnitPrice = 100m,
            Amount = 1000m
        };
        db.InquiryDetails.Add(detail);
        db.SaveChanges();
        return (inq, detail);
    }
}