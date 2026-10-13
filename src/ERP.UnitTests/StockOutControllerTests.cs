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
/// StockOutController 单元测试：Create 汇总 + 重写的 Approve 校验库存并扣减
/// （ERP-025 起经 InventoryService 记账并写库存流水，流水细节见 StockInOutMovementLedgerTests）。
/// </summary>
public class StockOutControllerTests
{
    [Fact]
    public async Task Create_正常创建_汇总TotalQuantity_Weight_Volume_Status_Pending_()
    {
        using var db = TestDbFactory.Create();
        var ctl = StockOutTestAuthorization.Create(db);

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
    public async Task Create_空串单号与伪造身份_一律由服务端权威覆盖()
    {
        using var db = TestDbFactory.Create();
        var ctl = StockOutTestAuthorization.Create(db);

        var so = new StockOut
        {
            Id = 999999L,                              // 伪造主键
            StockOutNo = string.Empty,                 // 手工表单省略 / 空串单号
            StockOutDate = DateTime.Today,
            CustomerId = 999999L,
            WarehouseId = 999999L,
            Status = DocumentStatus.Approved,          // 伪造状态
            IsDeleted = true,                          // 伪造删除态
            CreatedAt = new DateTime(2000, 1, 1),      // 伪造时间戳
            UpdatedAt = new DateTime(2000, 1, 1),
            Details = new List<StockOutDetail>
            {
                new StockOutDetail
                {
                    Id = 888888L, StockOutId = 777777L, IsDeleted = true,
                    ProductId = 1, ProductName = "P1", Unit = "PCS", Quantity = 3m
                }
            }
        };

        var result = await ctl.Create(so);

        Assert.IsType<OkObjectResult>(result);
        var saved = db.StockOuts.Include(o => o.Details).Single();
        Assert.NotEqual(999999L, saved.Id);
        Assert.StartsWith("CK", saved.StockOutNo);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.False(saved.IsDeleted);
        Assert.NotEqual(new DateTime(2000, 1, 1), saved.CreatedAt);
        Assert.NotEqual(new DateTime(2000, 1, 1), saved.UpdatedAt);   // 客户端注入时间戳不参与持久化
        Assert.Equal(3m, saved.TotalQuantity);

        var detail = Assert.Single(saved.Details);
        Assert.NotEqual(888888L, detail.Id);
        Assert.Equal(saved.Id, detail.StockOutId);
        Assert.False(detail.IsDeleted);
        Assert.NotEqual(new DateTime(2000, 1, 1), detail.UpdatedAt);
        Assert.Empty(db.StockMovements);
    }

    [Fact]
    public async Task Create_伪造出库单号_被服务端权威单号覆盖()
    {
        using var db = TestDbFactory.Create();
        var ctl = StockOutTestAuthorization.Create(db);

        var result = await ctl.Create(new StockOut
        {
            StockOutNo = "HACK-CK-478",
            StockOutDate = DateTime.Today,
            CustomerId = 999999L,
            WarehouseId = 999999L,
            Details = new List<StockOutDetail>
            {
                new StockOutDetail { ProductId = 1, ProductName = "P1", Unit = "PCS", Quantity = 1m }
            }
        });

        Assert.IsType<OkObjectResult>(result);
        var saved = db.StockOuts.Single();
        Assert.StartsWith("CK", saved.StockOutNo);
        Assert.NotEqual("HACK-CK-478", saved.StockOutNo);
    }

    [Fact]
    public async Task Approve_库存充足_扣减_数据库Stock减库存_Approval通过()
    {
        using var db = TestDbFactory.Create();
        // 先准备库存（数量 10）
        db.Stocks.Add(new Stock { WarehouseId = 999999L, ProductId = 1, Quantity = 10m, AvailableQuantity = 10m });

        var (stockOut, _) = SeedStockOut(db, "SO-1", DocumentStatus.Pending);
        var ctl = StockOutTestAuthorization.Create(db);

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
        var ctl = StockOutTestAuthorization.Create(db);

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
        var ctl = StockOutTestAuthorization.Create(db);

        await ctl.Submit(stockOut.Id);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(stockOut.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("库存不足", ex.Message);
    }

    [Fact]
    public async Task GetById_不存在_抛NotFound()
    {
        using var db = TestDbFactory.Create();
        var ctl = StockOutTestAuthorization.Create(db);
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