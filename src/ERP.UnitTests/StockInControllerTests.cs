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
/// StockInController 单元测试：Create 汇总 TotalQuantity/Weight/Volume + 重写的 Approve 增加库存（ApplyStockAsync）。
/// </summary>
public class StockInControllerTests
{
    [Fact]
    public async Task Create_正常创建_汇总TotalQuantity_Weight_Volume_Status_Pending()
    {
        using var db = TestDbFactory.Create();
        var ctl = new StockInController(db, new DocumentNumberService(db));

        var stockIn = new StockIn
        {
            StockInDate = DateTime.Today,
            SupplierId = 999999L,
            WarehouseId = 999999L,
            Remark = "INT_TEST_StockIn",
            Details = new List<StockInDetail>
            {
                new StockInDetail { ProductId = 1, ProductName = "P1", Quantity = 10m, Weight = 50m, Volume = 1.0m },
                new StockInDetail { ProductId = 2, ProductName = "P2", Quantity = 5m,  Weight = 25m, Volume = 0.5m }
            }
        };

        var result = await ctl.Create(stockIn);
        Assert.IsType<OkObjectResult>(result);

        var dbEntity = db.StockIns.Single();
        Assert.StartsWith("RK", dbEntity.StockInNo);
        Assert.Equal(DocumentStatus.Pending, dbEntity.Status);
        Assert.Equal(15m, dbEntity.TotalQuantity);
        Assert.Equal(75m, dbEntity.TotalWeight);
        Assert.Equal(1.5m, dbEntity.TotalVolume);
    }

    [Fact]
    public async Task GetById_不存在_抛NotFound()
    {
        using var db = TestDbFactory.Create();
        var ctl = new StockInController(db, new DocumentNumberService(db));
        await Assert.ThrowsAsync<BusinessException>(() => ctl.GetById(999));
    }

    [Fact]
    public async Task Approve_新单据_通过Submit到Approve_库存被加_AddStockAsync()
    {
        using var db = TestDbFactory.Create();
        var (stockIn, _) = SeedStockIn(db, "SI-1", DocumentStatus.Pending);
        var ctl = new StockInController(db, new DocumentNumberService(db));

        // 审核前：Submitted → Approved + 增加库存
        await ctl.Submit(stockIn.Id);
        await ctl.Approve(stockIn.Id);

        var dbEntity = db.StockIns.Single();
        Assert.Equal(DocumentStatus.Approved, dbEntity.Status);

        var stock = db.Stocks.Single(s => s.WarehouseId == 999999L && s.ProductId == 1);
        Assert.Equal(10m, stock.Quantity);
        Assert.Equal(10m, stock.AvailableQuantity);
    }

    [Fact]
    public async Task Approve_非Submitted_抛RuleConflict_且库存未变()
    {
        using var db = TestDbFactory.Create();
        var (stockIn, _) = SeedStockIn(db, "SI-2", DocumentStatus.Pending);
        var ctl = new StockInController(db, new DocumentNumberService(db));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(stockIn.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.Stocks);
    }

    [Fact]
    public async Task Delete_Pending_软删除()
    {
        using var db = TestDbFactory.Create();
        var (stockIn, _) = SeedStockIn(db, "SI-3", DocumentStatus.Pending);
        var ctl = new StockInController(db, new DocumentNumberService(db));

        await ctl.Delete(stockIn.Id);
        Assert.True(db.StockIns.Single().IsDeleted);
    }

    private static (StockIn si, StockInDetail detail) SeedStockIn(ErpDbContext db, string no, DocumentStatus status)
    {
        var si = new StockIn
        {
            StockInNo = no,
            StockInDate = DateTime.Today,
            SupplierId = 999999L,
            WarehouseId = 999999L,
            TotalQuantity = 10m,
            TotalWeight = 50m,
            TotalVolume = 1.0m,
            Status = status
        };
        db.StockIns.Add(si);
        db.SaveChanges();
        var detail = new StockInDetail
        {
            StockInId = si.Id,
            ProductId = 1,
            ProductName = "P1",
            Quantity = 10m, Weight = 50m, Volume = 1.0m
        };
        db.StockInDetails.Add(detail);
        db.SaveChanges();
        return (si, detail);
    }
}