using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// SalesOrderController 单元测试：覆盖 GetPaged / GetById / Create / Update / Export 业务方法，
/// 以及 DocumentControllerBase 的 Submit / Approve / Cancel / Delete 状态流转。
/// </summary>
public class SalesOrderControllerTests
{
    // ==================== Create ====================

    [Fact]
    public async Task Create_正常创建_生成OrderNo_Status为Pending_Calculate_TotalAmount与DepositAmount自动计算()
    {
        using var db = TestDbFactory.Create();
        var noService = new DocumentNumberService(db);
        var ctl = new SalesOrderController(db, noService);

        var so = new SalesOrder
        {
            OrderDate = DateTime.Today,
            CustomerId = 999999L,
            SalesmanId = 999999L,
            Currency = (Currency)2,
            ExchangeRate = 7.1m,
            DepositRatio = 30m,
            PaymentTerms = "T/T 30",
            DeliveryDate = DateTime.Today.AddDays(30),
            ShippingMethod = "海运",
            Remark = "INT_TEST",
            Details = new List<SalesOrderDetail>
            {
                new SalesOrderDetail { ProductId = 1, ProductName = "P1", Quantity = 10m, UnitPrice = 100m },
                new SalesOrderDetail { ProductId = 2, ProductName = "P2", Quantity = 5m,  UnitPrice = 80m }
            }
        };

        var result = await ctl.Create(so);
        Assert.IsType<OkObjectResult>(result);

        var dbEntity = db.SalesOrders.Single();
        Assert.True(dbEntity.Id > 0);
        Assert.StartsWith("SO", dbEntity.OrderNo);
        Assert.Equal(DocumentStatus.Pending, dbEntity.Status);
        Assert.Equal(2, dbEntity.Details.Count);
        Assert.Equal(1400m, dbEntity.TotalAmount);                  // 10×100 + 5×80
        Assert.Equal(420m,  dbEntity.DepositAmount);                  // 1400 × 30%
        Assert.Equal(1000m, dbEntity.Details.Single(d => d.ProductId == 1).Amount);
        Assert.Equal(400m,  dbEntity.Details.Single(d => d.ProductId == 2).Amount);
    }

    // ==================== GetById ====================

    [Fact]
    public async Task GetById_不存在_抛NotFound()
    {
        using var db = TestDbFactory.Create();
        var ctl = new SalesOrderController(db, new DocumentNumberService(db));

        await Assert.ThrowsAsync<BusinessException>(() => ctl.GetById(999));
    }

    // ==================== GetPaged ====================

    [Fact]
    public async Task GetPaged_关键字_OrderNo匹配_分页正确()
    {
        using var db = TestDbFactory.Create();
        SeedSalesOrder(db, "SO-FOO-001", DocumentStatus.Pending);
        SeedSalesOrder(db, "SO-FOO-002", DocumentStatus.Pending);
        SeedSalesOrder(db, "SO-BAR-001", DocumentStatus.Pending);
        var ctl = new SalesOrderController(db, new DocumentNumberService(db));

        var result = await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10, Keyword = "FOO" }, null);

        var resp = Assert.IsType<ApiResponse<PagedResult<SalesOrder>>>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(2, resp.Data!.Total);
        Assert.Equal(2, resp.Data.Items.Count);
    }

    [Fact]
    public async Task GetPaged_状态过滤_仅返回对应状态()
    {
        using var db = TestDbFactory.Create();
        SeedSalesOrder(db, "SO-P", DocumentStatus.Pending);
        SeedSalesOrder(db, "SO-A", DocumentStatus.Approved);
        var ctl = new SalesOrderController(db, new DocumentNumberService(db));

        var result = await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }, DocumentStatus.Approved);

        var resp = Assert.IsType<ApiResponse<PagedResult<SalesOrder>>>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(1, resp.Data!.Total);
        Assert.Equal(DocumentStatus.Approved, resp.Data.Items[0].Status);
    }

    // ==================== Update ====================

    [Fact]
    public async Task Update_非Pending_抛RuleConflict_且数据库未变()
    {
        using var db = TestDbFactory.Create();
        var (so, _) = SeedSalesOrder(db, "SO-X", DocumentStatus.Approved);
        var originalAmount = db.SalesOrderDetails.Single().Amount;
        var ctl = new SalesOrderController(db, new DocumentNumberService(db));

        var update = new SalesOrder
        {
            OrderDate = DateTime.Today,
            CustomerId = 1, SalesmanId = 1, Currency = (Currency)1, ExchangeRate = 1m,
            DepositRatio = 0m, PaymentTerms = "", DeliveryDate = DateTime.Today, ShippingMethod = "",
            Details = new List<SalesOrderDetail>
            {
                new SalesOrderDetail { ProductId = 99, ProductName = "改", Quantity = 1m, UnitPrice = 1m }
            }
        };

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(so.Id, update));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(originalAmount, db.SalesOrderDetails.Single().Amount);
    }

    [Fact]
    public async Task Update_Pending_修改成功_重算TotalAmount与DepositAmount()
    {
        using var db = TestDbFactory.Create();
        var (so, _) = SeedSalesOrder(db, "SO-Y", DocumentStatus.Pending);
        var ctl = new SalesOrderController(db, new DocumentNumberService(db));

        var update = new SalesOrder
        {
            OrderDate = DateTime.Today,
            CustomerId = 999999L, SalesmanId = 999999L,
            Currency = (Currency)2, ExchangeRate = 7.1m,
            DepositRatio = 50m, PaymentTerms = "T/T 60",
            DeliveryDate = DateTime.Today.AddDays(60), ShippingMethod = "海运",
            Details = new List<SalesOrderDetail>
            {
                new SalesOrderDetail { ProductId = 1, ProductName = "P1", Quantity = 20m, UnitPrice = 50m }
            }
        };

        await ctl.Update(so.Id, update);

        var dbSo = db.SalesOrders.Single();
        Assert.Equal(1000m, dbSo.TotalAmount);                       // 20 × 50
        Assert.Equal(500m, dbSo.DepositAmount);                      // 1000 × 50%
        Assert.Single(db.SalesOrderDetails, d => d.SalesOrderId == so.Id);
    }

    // ==================== DocumentControllerBase 状态流转 ====================

    [Fact]
    public async Task Submit_Approve_完整流程_Pending到Approved()
    {
        using var db = TestDbFactory.Create();
        var (so, _) = SeedSalesOrder(db, "SO-WF", DocumentStatus.Pending);
        var ctl = new SalesOrderController(db, new DocumentNumberService(db));

        await ctl.Submit(so.Id);
        Assert.Equal(DocumentStatus.Submitted, db.SalesOrders.Single().Status);

        await ctl.Approve(so.Id);
        Assert.Equal(DocumentStatus.Approved, db.SalesOrders.Single().Status);
    }

    [Fact]
    public async Task Cancel_任意状态_返回Ok_Status变Cancelled()
    {
        using var db = TestDbFactory.Create();
        var (so, _) = SeedSalesOrder(db, "SO-C", DocumentStatus.Approved);
        var ctl = new SalesOrderController(db, new DocumentNumberService(db));

        await ctl.Cancel(so.Id);
        Assert.Equal(DocumentStatus.Cancelled, db.SalesOrders.Single().Status);
    }

    [Fact]
    public async Task Delete_非Pending_抛RuleConflict_且未软删()
    {
        using var db = TestDbFactory.Create();
        var (so, _) = SeedSalesOrder(db, "SO-DA", DocumentStatus.Approved);
        var ctl = new SalesOrderController(db, new DocumentNumberService(db));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Delete(so.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.False(db.SalesOrders.Single().IsDeleted);
    }

    // ==================== 种子 ====================

    private static (SalesOrder so, SalesOrderDetail detail) SeedSalesOrder(ErpDbContext db, string no, DocumentStatus status)
    {
        var so = new SalesOrder
        {
            OrderNo = no,
            OrderDate = DateTime.Today,
            CustomerId = 1,
            SalesmanId = 1,
            Currency = (Currency)2,
            ExchangeRate = 7.1m,
            DepositRatio = 30m,
            DepositAmount = 300m,
            TotalAmount = 1000m,
            Status = status
        };
        db.SalesOrders.Add(so);
        db.SaveChanges();
        var detail = new SalesOrderDetail
        {
            SalesOrderId = so.Id,
            ProductId = 1,
            ProductName = "P1",
            Spec = "大",
            Unit = "PCS",
            Quantity = 10m,
            UnitPrice = 100m,
            Amount = 1000m
        };
        db.SalesOrderDetails.Add(detail);
        db.SaveChanges();
        return (so, detail);
    }
}