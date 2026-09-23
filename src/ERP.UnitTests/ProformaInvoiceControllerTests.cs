using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ProformaInvoiceController 单元测试：覆盖 PI 创建（金额 / 定金后端复核）、详情、分页、
/// 修改、审核 / 销审 / 作废状态流转与打印数据端点。
/// </summary>
public class ProformaInvoiceControllerTests
{
    // ==================== Create ====================

    [Fact]
    public async Task Create_正常创建_生成PiNo_Pending_明细金额与定金按比例计算()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);

        var pi = new ProformaInvoice
        {
            PiDate = DateTime.Today,
            CustomerId = 1,
            CustomerName = "义乌外贸客户 A",
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            DepositRatio = 30m,
            Consignee = "ABC IMPORT CO., LTD",
            NotifyParty = "SAME AS CONSIGNEE",
            ShippingMarks = "ABC / YIWU / C/NO.1-100",
            BankInfo = "Beneficiary: ABC Co.\r\nSWIFT: BKCHCNBJ",
            TradeTerms = "FOB",
            PaymentTerms = "T/T 30% deposit",
            ShippingTerms = "By sea, FCL",
            Remark = "INT_TEST",
            Details = new List<ProformaInvoiceDetail>
            {
                new() { ProductCode = "P001", ProductName = "饰品 A", Quantity = 100m, UnitPrice = 2.5m },
                new() { ProductCode = "P002", ProductName = "饰品 B", Quantity = 40m, UnitPrice = 12.5m }
            }
        };

        var result = await ctl.Create(pi);
        Assert.IsType<OkObjectResult>(result);

        var saved = db.ProformaInvoices.Single();
        Assert.True(saved.Id > 0);
        Assert.StartsWith("PI", saved.PiNo);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.Equal(750m, saved.TotalAmount);            // 100×2.5 + 40×12.5
        Assert.Equal(5400m, saved.TotalAmountCny);        // 750 × 7.2
        Assert.Equal(225m, saved.DepositAmount);          // 750 × 30%
        Assert.Equal("义乌外贸客户 A", saved.CustomerName);
        Assert.Equal("ABC IMPORT CO., LTD", saved.Consignee);
        Assert.Equal("SAME AS CONSIGNEE", saved.NotifyParty);
        Assert.Equal("ABC / YIWU / C/NO.1-100", saved.ShippingMarks);
        Assert.Contains("SWIFT", saved.BankInfo);
        Assert.Equal("By sea, FCL", saved.ShippingTerms);

        var details = db.ProformaInvoiceDetails.Where(d => d.PiId == saved.Id).OrderBy(d => d.SortNo).ToList();
        Assert.Equal(2, details.Count);
        Assert.Equal(1, details[0].SortNo);
        Assert.Equal(250m, details[0].Amount);
        Assert.Equal(500m, details[1].Amount);
        Assert.Equal(saved.PiNo, details[0].PiNo);
    }

    [Fact]
    public async Task Create_手工覆盖定金金额_小于总额时保留原值()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);

        await ctl.Create(new ProformaInvoice
        {
            DepositRatio = 30m,
            DepositAmount = 250m,               // 手工改大但仍小于总额 750
            Details = new List<ProformaInvoiceDetail> { new() { Quantity = 100m, UnitPrice = 7.5m } }
        });

        Assert.Equal(250m, db.ProformaInvoices.Single().DepositAmount);
    }

    [Fact]
    public async Task Create_定金比例越界_抛InvalidParameter()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(new ProformaInvoice
        {
            DepositRatio = 120m,
            Details = new List<ProformaInvoiceDetail> { new() { Quantity = 1m, UnitPrice = 1m } }
        }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Create_定金金额大于总额_抛InvalidParameter()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(new ProformaInvoice
        {
            DepositRatio = 30m,
            DepositAmount = 999m,
            Details = new List<ProformaInvoiceDetail> { new() { Quantity = 10m, UnitPrice = 10m } }
        }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== GetById / GetPaged ====================

    [Fact]
    public async Task GetById_存在_返回PI与明细()
    {
        using var db = TestDbFactory.Create();
        var (pi, _) = SeedPi(db, "PI202609230001", DocumentStatus.Pending);
        var ctl = NewController(db);

        var ok = Assert.IsType<OkObjectResult>(await ctl.GetById(pi.Id));
        var resp = Assert.IsType<ApiResponse<ProformaInvoice>>(ok.Value);
        Assert.Equal(pi.Id, resp.Data!.Id);
        Assert.Single(resp.Data.Details);
    }

    [Fact]
    public async Task GetById_不存在_抛NotFound()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetById(999));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public async Task GetPaged_按关键字与状态过滤_分页正确()
    {
        using var db = TestDbFactory.Create();
        SeedPi(db, "PI-FOO-001", DocumentStatus.Pending);
        SeedPi(db, "PI-FOO-002", DocumentStatus.Approved);
        SeedPi(db, "PI-BAR-003", DocumentStatus.Pending);
        var ctl = NewController(db);

        var ok = Assert.IsType<OkObjectResult>(await ctl.GetPaged(
            new PageQuery { Page = 1, PageSize = 10, Keyword = "FOO" }, null, null, null));
        var resp = Assert.IsType<ApiResponse<PagedResult<ProformaInvoice>>>(ok.Value);
        Assert.Equal(2, resp.Data!.Total);

        var okApproved = Assert.IsType<OkObjectResult>(await ctl.GetPaged(
            new PageQuery { Page = 1, PageSize = 10 }, DocumentStatus.Approved, null, null));
        var respApproved = Assert.IsType<ApiResponse<PagedResult<ProformaInvoice>>>(okApproved.Value);
        Assert.Single(respApproved.Data!.Items);
        Assert.Equal("PI-FOO-002", respApproved.Data.Items[0].PiNo);
    }

    // ==================== Update ====================

    [Fact]
    public async Task Update_草稿_替换明细并重算合计与定金()
    {
        using var db = TestDbFactory.Create();
        var (pi, originalDetail) = SeedPi(db, "PI-UPD-001", DocumentStatus.Pending);
        var ctl = NewController(db);

        await ctl.Update(pi.Id, new ProformaInvoice
        {
            PiDate = DateTime.Today,
            CustomerName = "客户 B",
            DepositRatio = 50m,
            Details = new List<ProformaInvoiceDetail>
            {
                new() { ProductCode = "P100", ProductName = "新行 1", Quantity = 4m, UnitPrice = 25m },
                new() { ProductCode = "P200", ProductName = "新行 2", Quantity = 2m, UnitPrice = 50m }
            }
        });

        var saved = db.ProformaInvoices.Single();
        Assert.Equal("客户 B", saved.CustomerName);
        Assert.Equal(200m, saved.TotalAmount);       // 100 + 100
        Assert.Equal(100m, saved.DepositAmount);     // 200 × 50%
        Assert.DoesNotContain(db.ProformaInvoiceDetails, d => d.Id == originalDetail.Id);
        Assert.Equal(2, db.ProformaInvoiceDetails.Count(d => d.PiId == pi.Id));
        Assert.NotNull(saved.UpdatedAt);
    }

    [Fact]
    public async Task Update_已审核_抛RuleConflict()
    {
        using var db = TestDbFactory.Create();
        var (pi, _) = SeedPi(db, "PI-UPD-002", DocumentStatus.Approved);
        var ctl = NewController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(pi.Id, new ProformaInvoice()));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }

    [Fact]
    public async Task Update_修改定金比例_定金金额按新比例重算()
    {
        using var db = TestDbFactory.Create();
        var (pi, _) = SeedPi(db, "PI-UPD-003", DocumentStatus.Pending);   // 已存在 30% / 300
        var ctl = NewController(db);

        // 前端表单会回传旧的定金金额，这里模拟「只把比例从 30% 改成 40%」
        await ctl.Update(pi.Id, new ProformaInvoice
        {
            PiDate = DateTime.Today,
            DepositRatio = 40m,
            DepositAmount = 300m,
            Details = new List<ProformaInvoiceDetail> { new() { Quantity = 10m, UnitPrice = 100m } }
        });

        var saved = db.ProformaInvoices.Single();
        Assert.Equal(40m, saved.DepositRatio);
        Assert.Equal(400m, saved.DepositAmount);      // 1000 × 40%，而不是表单回传的 300
    }

    [Fact]
    public async Task Update_比例不变_保留手工覆盖的定金金额()
    {
        using var db = TestDbFactory.Create();
        var (pi, _) = SeedPi(db, "PI-UPD-004", DocumentStatus.Pending);
        var ctl = NewController(db);

        await ctl.Update(pi.Id, new ProformaInvoice
        {
            PiDate = DateTime.Today,
            DepositRatio = 30m,                      // 比例未变
            DepositAmount = 350m,                    // 手工把定金改成 350
            Details = new List<ProformaInvoiceDetail> { new() { Quantity = 10m, UnitPrice = 100m } }
        });

        var saved = db.ProformaInvoices.Single();
        Assert.Equal(30m, saved.DepositRatio);
        Assert.Equal(350m, saved.DepositAmount);
    }

    // ==================== 状态流转 ====================

    [Fact]
    public async Task Approve_草稿到已审核_返回Ok()
    {
        using var db = TestDbFactory.Create();
        var (pi, _) = SeedPi(db, "PI-AP-001", DocumentStatus.Pending);
        var ctl = NewController(db);

        Assert.IsType<OkObjectResult>(await ctl.Approve(pi.Id));
        Assert.Equal(DocumentStatus.Approved, db.ProformaInvoices.Single().Status);
    }

    [Fact]
    public async Task Approve_已审核_抛RuleConflict()
    {
        using var db = TestDbFactory.Create();
        var (pi, _) = SeedPi(db, "PI-AP-002", DocumentStatus.Approved);
        var ctl = NewController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(pi.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }

    [Fact]
    public async Task Approve_无明细_抛RuleConflict()
    {
        using var db = TestDbFactory.Create();
        var pi = new ProformaInvoice { PiNo = "PI-AP-003", PiDate = DateTime.Today, Status = DocumentStatus.Pending };
        db.ProformaInvoices.Add(pi);
        db.SaveChanges();
        var ctl = NewController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(pi.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("明细", ex.Message);
    }

    [Fact]
    public async Task Unaudit_已审核回到草稿_返回Ok()
    {
        using var db = TestDbFactory.Create();
        var (pi, _) = SeedPi(db, "PI-UA-001", DocumentStatus.Approved);
        var ctl = NewController(db);

        Assert.IsType<OkObjectResult>(await ctl.Unaudit(pi.Id));
        Assert.Equal(DocumentStatus.Pending, db.ProformaInvoices.Single().Status);
    }

    [Fact]
    public async Task Unaudit_非已审核_抛RuleConflict()
    {
        using var db = TestDbFactory.Create();
        var (pi, _) = SeedPi(db, "PI-UA-002", DocumentStatus.Pending);
        var ctl = NewController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Unaudit(pi.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }

    [Fact]
    public async Task Void_已审核到已作废_返回Ok()
    {
        using var db = TestDbFactory.Create();
        var (pi, _) = SeedPi(db, "PI-VO-001", DocumentStatus.Approved);
        var ctl = NewController(db);

        Assert.IsType<OkObjectResult>(await ctl.Void(pi.Id));
        Assert.Equal(DocumentStatus.Cancelled, db.ProformaInvoices.Single().Status);
    }

    [Fact]
    public async Task Void_已作废_抛RuleConflict()
    {
        using var db = TestDbFactory.Create();
        var (pi, _) = SeedPi(db, "PI-VO-002", DocumentStatus.Cancelled);
        var ctl = NewController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Void(pi.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }

    [Fact]
    public async Task Void_已转销售订单_抛RuleConflict()
    {
        using var db = TestDbFactory.Create();
        var (pi, _) = SeedPi(db, "PI-VO-003", DocumentStatus.Completed);
        var ctl = NewController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Void(pi.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }

    // ==================== 打印数据 ====================

    [Fact]
    public async Task GetPrint_返回主表与明细_供打印模板使用()
    {
        using var db = TestDbFactory.Create();
        var (pi, _) = SeedPi(db, "PI-PR-001", DocumentStatus.Approved);
        var ctl = NewController(db);

        var ok = Assert.IsType<OkObjectResult>(await ctl.GetPrint(pi.Id));
        var resp = Assert.IsType<ApiResponse<ProformaInvoice>>(ok.Value);
        Assert.Equal("PI-PR-001", resp.Data!.PiNo);
        Assert.Single(resp.Data.Details);
        Assert.Equal(1000m, resp.Data.TotalAmount);
    }

    // ==================== 种子与工厂 ====================

    private static ProformaInvoiceController NewController(ErpDbContext db)
        => new(db, new DocumentNumberService(db));

    private static (ProformaInvoice pi, ProformaInvoiceDetail detail) SeedPi(
        ErpDbContext db, string no, DocumentStatus status)
    {
        var pi = new ProformaInvoice
        {
            PiNo = no,
            PiDate = DateTime.Today,
            CustomerId = 1,
            CustomerName = "客户 A",
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            DepositRatio = 30m,
            TotalAmount = 1000m,
            TotalAmountCny = 7200m,
            DepositAmount = 300m,
            Status = status,
            Remark = "INT_TEST"
        };
        db.ProformaInvoices.Add(pi);
        db.SaveChanges();

        var detail = new ProformaInvoiceDetail
        {
            PiId = pi.Id,
            PiNo = pi.PiNo,
            SortNo = 1,
            ProductCode = "P001",
            ProductName = "饰品 A",
            Quantity = 10m,
            UnitPrice = 100m,
            Amount = 1000m
        };
        db.ProformaInvoiceDetails.Add(detail);
        db.SaveChanges();
        return (pi, detail);
    }
}
