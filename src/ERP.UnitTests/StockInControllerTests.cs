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
/// StockInController 单元测试：Create 汇总 TotalQuantity/Weight/Volume + 重写的 Approve 按基础单位增加库存
/// （ERP-025 起经 InventoryService 记账并写库存流水，流水细节见 StockInOutMovementLedgerTests）。
/// <para>ERP-477：采购入库菜单改由规范 EF 主子表模块承载，手工表单不提交入库单号（省略 / 空串照常绑定，
/// 服务端权威生成单号并覆盖身份 / 状态）。</para>
/// </summary>
public class StockInControllerTests
{
    [Fact]
    public async Task Create_正常创建_汇总TotalQuantity_Weight_Volume_Status_Pending()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);

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
        var ctl = NewController(db);
        await Assert.ThrowsAsync<BusinessException>(() => ctl.GetById(999));
    }

    /// <summary>
    /// ERP-477：手工录入表单不提交入库单号（空串 / 省略）与身份 / 状态：绑定层放行后，
    /// 服务端权威生成入库单号并覆盖状态为「待提交」，客户端伪造值不参与持久化。
    /// </summary>
    [Fact]
    public async Task Create_空串入库单号与伪造身份_服务端权威覆盖()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);

        var stockIn = new StockIn
        {
            StockInNo = string.Empty,
            StockInDate = DateTime.Today,
            SupplierId = 999999L,
            WarehouseId = 999999L,
            Status = DocumentStatus.Approved,
            IsDeleted = true,
            Details = new List<StockInDetail>
            {
                new StockInDetail { ProductId = 1, ProductName = "P1", Unit = "PCS", Quantity = 2m }
            }
        };

        var result = await ctl.Create(stockIn);
        Assert.IsType<OkObjectResult>(result);

        var saved = db.StockIns.Single();
        Assert.StartsWith("RK", saved.StockInNo);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.False(saved.IsDeleted);
        Assert.True(saved.UpdatedAt > DateTime.Today.AddMinutes(-5));
        Assert.Equal(2m, saved.TotalQuantity);
    }

    [Fact]
    public async Task Approve_新单据_通过Submit到Approve_库存被加_AddStockAsync()
    {
        using var db = TestDbFactory.Create();
        var (stockIn, _) = SeedStockIn(db, "SI-1", DocumentStatus.Pending);
        var ctl = StockInLegacyTestFixture.Create(db, 999999L, 999999L);

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
        var ctl = StockInLegacyTestFixture.Create(db, 999999L, 999999L);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(stockIn.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.Stocks);
    }

    [Fact]
    public async Task Delete_Pending_软删除()
    {
        using var db = TestDbFactory.Create();
        var (stockIn, _) = SeedStockIn(db, "SI-3", DocumentStatus.Pending);
        var ctl = StockInLegacyTestFixture.Create(db, 999999L, 999999L);

        await ctl.Delete(stockIn.Id);
        Assert.True(db.StockIns.Single().IsDeleted);
    }

    /// <summary>
    /// ERP-504：创建 / 提交 / 审核要求明细商品真实在用 —— 统一播种夹具主数据（供应商 / 仓库由既有脚手架播种，
    /// 不新增任何授权或业务断言）。
    /// </summary>
    private static StockInController NewController(ErpDbContext db)
    {
        EnsureProducts(db);
        return StockInLegacyTestFixture.Create(db, 999999L, 999999L);
    }

    /// <summary>ERP-504：幂等播种本夹具明细使用的在用商品（存在、未删除、已启用）。</summary>
    private static void EnsureProducts(ErpDbContext db)
    {
        StockInTestAuthorization.SeedProduct(db, 1L, name: "P1");
        StockInTestAuthorization.SeedProduct(db, 2L, name: "P2");
    }

    private static (StockIn si, StockInDetail detail) SeedStockIn(ErpDbContext db, string no, DocumentStatus status)
    {
        EnsureProducts(db);
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