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
/// StockOutController 单元测试：Create 汇总 + 重写的 Approve 校验库存并扣减。
/// </summary>
public class StockOutControllerTests
{
    [Fact]
    public async Task Create_正常创建_汇总TotalQuantity_Weight_Volume_Status_Pending_()
    {
        using var db = TestDbFactory.Create();
        var ctl = new StockOutController(db, new DocumentNumberService(db));

        var so = new StockOut
        {
            StockOutDate = DateTime.Today,
            CustomerId = 999999L,
            WarehouseId = 999999L,
            Remark = "INT_TEST_StockOut",
            Details = new List<StockOutDetail>
            {
                new StockOutDetail { ProductId = 1, ProductName = "P1", Quantity = 5m, Weight = 25m, Volume = 0.5m },
                new StockOutDetail { ProductId = 2, ProductName = "P2", Quantity = 3m, Weight = 15m, Volume = 0.3m }
            }
        };

        var result = await ctl.Create(so);
        Assert.IsType<OkObjectResult>(result);

        var dbEntity = db.StockOuts.Single();
        Assert.StartsWith("CK", dbEntity.StockOutNo);
        Assert.Equal(DocumentStatus.Pending, dbEntity.Status);
        Assert.Equal(8m, dbEntity.TotalQuantity);
        Assert.Equal(40m, dbEntity.TotalWeight);
        Assert.Equal(0.8m, dbEntity.TotalVolume);
    }

    [Fact]
    public async Task Approve_库存充足_扣减_数据库Stock减库存_Approval通过()
    {
        using var db = TestDbFactory.Create();
        // 先准备库存（数量 10）
        db.Stocks.Add(new Stock { WarehouseId = 999999L, ProductId = 1, Quantity = 10m, AvailableQuantity = 10m });

        var (stockOut, _) = SeedStockOut(db, "SO-1", DocumentStatus.Pending);
        var ctl = new StockOutController(db, new DocumentNumberService(db));

        await ctl.Submit(stockOut.Id);
        await ctl.Approve(stockOut.Id);

        var stock = db.Stocks.Single();
        Assert.Equal(0m, stock.Quantity);                                  // 10 - 10 = 0
        Assert.Equal(0m, stock.AvailableQuantity);
        Assert.Equal(DocumentStatus.Approved, db.StockOuts.Single().Status);
    }

    [Fact]
    public async Task Approve_库存不足_抛RuleConflict_且未扣减未变更Status()
    {
        using var db = TestDbFactory.Create();
        db.Stocks.Add(new Stock { WarehouseId = 999999L, ProductId = 1, Quantity = 5m, AvailableQuantity = 5m });

        var (stockOut, _) = SeedStockOut(db, "SO-2", DocumentStatus.Pending);
        var ctl = new StockOutController(db, new DocumentNumberService(db));

        await ctl.Submit(stockOut.Id);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(stockOut.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("库存不足", ex.Message);
        Assert.Equal(5m, db.Stocks.Single().Quantity);   // 未扣减
        Assert.Equal(DocumentStatus.Submitted, db.StockOuts.Single().Status);
    }

    [Fact]
    public async Task Approve_库存不存在_抛RuleConflict()
    {
        using var db = TestDbFactory.Create();
        // 没有库存
        var (stockOut, _) = SeedStockOut(db, "SO-3", DocumentStatus.Pending);
        var ctl = new StockOutController(db, new DocumentNumberService(db));

        await ctl.Submit(stockOut.Id);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(stockOut.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("库存不足", ex.Message);
    }

    [Fact]
    public async Task GetById_不存在_抛NotFound()
    {
        using var db = TestDbFactory.Create();
        var ctl = new StockOutController(db, new DocumentNumberService(db));
        await Assert.ThrowsAsync<BusinessException>(() => ctl.GetById(999));
    }

    private static (StockOut so, StockOutDetail detail) SeedStockOut(ErpDbContext db, string no, DocumentStatus status)
    {
        var so = new StockOut
        {
            StockOutNo = no,
            StockOutDate = DateTime.Today,
            CustomerId = 999999L,
            WarehouseId = 999999L,
            TotalQuantity = 10m, TotalWeight = 50m, TotalVolume = 1.0m,
            Status = status
        };
        db.StockOuts.Add(so);
        db.SaveChanges();
        var detail = new StockOutDetail
        {
            StockOutId = so.Id,
            ProductId = 1, ProductName = "P1",
            Quantity = 10m, Weight = 50m, Volume = 1.0m
        };
        db.StockOutDetails.Add(detail);
        db.SaveChanges();
        return (so, detail);
    }
}