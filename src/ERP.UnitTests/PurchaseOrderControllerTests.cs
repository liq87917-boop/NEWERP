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
/// PurchaseOrderController 单元测试：Create 修复了 SalesOrder 同款 Amount bug 后的行为 + DocumentControllerBase 状态流转。
/// </summary>
public class PurchaseOrderControllerTests
{
    [Fact]
    public async Task Create_正常创建_生成OrderNo_Detail的Amount被自动计算_TotalAmount汇总()
    {
        using var db = TestDbFactory.Create();
        var ctl = new PurchaseOrderController(db, new DocumentNumberService(db));

        var po = new PurchaseOrder
        {
            OrderDate = DateTime.Today,
            SupplierId = 999999L,
            Currency = (Currency)1,
            ExchangeRate = 1m,
            PaymentTerms = "Net 30",
            DeliveryDate = DateTime.Today.AddDays(30),
            Remark = "INT_TEST_PO_Create",
            Details = new List<PurchaseOrderDetail>
            {
                new PurchaseOrderDetail { ProductId = 1, ProductName = "P1", Quantity = 20m, UnitPrice = 50m },
                new PurchaseOrderDetail { ProductId = 2, ProductName = "P2", Quantity = 10m, UnitPrice = 80m }
            }
        };

        var result = await ctl.Create(po);
        Assert.IsType<OkObjectResult>(result);

        var dbEntity = db.PurchaseOrders.Single();
        Assert.StartsWith("PO", dbEntity.OrderNo);
        Assert.Equal(DocumentStatus.Pending, dbEntity.Status);
        Assert.Equal(1800m, dbEntity.TotalAmount);                          // 20×50 + 10×80
        Assert.Equal(1000m, dbEntity.Details.Single(d => d.ProductId == 1).Amount);
        Assert.Equal(800m,  dbEntity.Details.Single(d => d.ProductId == 2).Amount);
    }

    [Fact]
    public async Task GetById_不存在_抛NotFound()
    {
        using var db = TestDbFactory.Create();
        var ctl = new PurchaseOrderController(db, new DocumentNumberService(db));
        await Assert.ThrowsAsync<BusinessException>(() => ctl.GetById(999));
    }

    [Fact]
    public async Task GetPaged_关键字_OrderNo匹配()
    {
        using var db = TestDbFactory.Create();
        SeedPurchaseOrder(db, "PO-FOO-1", DocumentStatus.Pending);
        SeedPurchaseOrder(db, "PO-BAR-1", DocumentStatus.Pending);
        var ctl = new PurchaseOrderController(db, new DocumentNumberService(db));

        var result = await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10, Keyword = "FOO" }, null);
        var resp = Assert.IsType<ApiResponse<PagedResult<PurchaseOrder>>>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(1, resp.Data!.Total);
    }

    [Fact]
    public async Task Update_非Pending_抛RuleConflict_且数据库未变()
    {
        using var db = TestDbFactory.Create();
        var (po, _) = SeedPurchaseOrder(db, "PO-X", DocumentStatus.Approved);
        var ctl = new PurchaseOrderController(db, new DocumentNumberService(db));

        var update = new PurchaseOrder
        {
            OrderDate = DateTime.Today,
            SupplierId = 1, Currency = (Currency)1, ExchangeRate = 1m,
            PaymentTerms = "", DeliveryDate = null,
            Details = new List<PurchaseOrderDetail> { new() { ProductId = 99, Quantity = 1m, UnitPrice = 1m } }
        };

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(po.Id, update));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }

    [Fact]
    public async Task Update_Pending_修改成功_Details被全删全建_重算TotalAmount()
    {
        using var db = TestDbFactory.Create();
        var (po, _) = SeedPurchaseOrder(db, "PO-Y", DocumentStatus.Pending);
        var ctl = new PurchaseOrderController(db, new DocumentNumberService(db));

        var update = new PurchaseOrder
        {
            OrderDate = DateTime.Today,
            SupplierId = 999999L, Currency = (Currency)1, ExchangeRate = 1m,
            PaymentTerms = "Net 60", DeliveryDate = DateTime.Today.AddDays(60),
            Details = new List<PurchaseOrderDetail>
            {
                new() { ProductId = 1, Quantity = 100m, UnitPrice = 25m }     // TotalAmount = 2500
            }
        };

        await ctl.Update(po.Id, update);
        Assert.Equal(2500m, db.PurchaseOrders.Single().TotalAmount);
        Assert.Single(db.PurchaseOrderDetails, d => d.PurchaseOrderId == po.Id);
    }

    [Fact]
    public async Task Submit_Approve_完整流程_Pending到Approved()
    {
        using var db = TestDbFactory.Create();
        var (po, _) = SeedPurchaseOrder(db, "PO-WF", DocumentStatus.Pending);
        var ctl = new PurchaseOrderController(db, new DocumentNumberService(db));

        await ctl.Submit(po.Id);
        Assert.Equal(DocumentStatus.Submitted, db.PurchaseOrders.Single().Status);

        await ctl.Approve(po.Id);
        Assert.Equal(DocumentStatus.Approved, db.PurchaseOrders.Single().Status);
    }

    [Fact]
    public async Task Delete_Pending_软删除_非Pending_抛RuleConflict()
    {
        using var db = TestDbFactory.Create();
        var (po1, _) = SeedPurchaseOrder(db, "PO-D1", DocumentStatus.Pending);
        var (po2, _) = SeedPurchaseOrder(db, "PO-D2", DocumentStatus.Approved);
        var ctl = new PurchaseOrderController(db, new DocumentNumberService(db));

        await ctl.Delete(po1.Id);
        Assert.True(db.PurchaseOrders.Single(p => p.Id == po1.Id).IsDeleted);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Delete(po2.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }

    private static (PurchaseOrder po, PurchaseOrderDetail detail) SeedPurchaseOrder(ErpDbContext db, string no, DocumentStatus status)
    {
        var po = new PurchaseOrder
        {
            OrderNo = no,
            OrderDate = DateTime.Today,
            SupplierId = 1,
            Currency = (Currency)1,
            ExchangeRate = 1m,
            TotalAmount = 1000m,
            Status = status
        };
        db.PurchaseOrders.Add(po);
        db.SaveChanges();
        var detail = new PurchaseOrderDetail
        {
            PurchaseOrderId = po.Id,
            ProductId = 1, ProductName = "P1",
            Quantity = 10m, UnitPrice = 100m, Amount = 1000m
        };
        db.PurchaseOrderDetails.Add(detail);
        db.SaveChanges();
        return (po, detail);
    }
}