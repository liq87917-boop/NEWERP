using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

public class StockUnitConversionTests
{
    [Fact]
    public async Task Stock_in_package_quantity_is_persisted_and_posted_in_base_units_then_reversed()
    {
        using var db = TestDbFactory.Create();
        var product = Product(db, "PCS", "CTN", 12);
        var controller = new StockInController(db, new DocumentNumberService(db));
        var document = new StockIn
        {
            SupplierId = 1, WarehouseId = 7,
            Details = new List<StockInDetail>
            {
                new() { ProductId = product.Id, ProductName = product.ProductName, Unit = "ctn", Quantity = 2 }
            }
        };

        await controller.Create(document);
        var saved = db.StockIns.Include(x => x.Details).Single();
        Assert.Equal(24m, saved.TotalQuantity);
        Assert.Equal(24m, saved.Details.Single().Quantity);
        Assert.Equal("PCS", saved.Details.Single().Unit);

        await controller.Submit(saved.Id);
        await controller.Approve(saved.Id);
        Assert.Equal(24m, db.Stocks.Single().Quantity);

        await controller.Cancel(saved.Id);
        Assert.Equal(0m, db.Stocks.Single().Quantity);
        Assert.Equal(DocumentStatus.Cancelled, db.StockIns.Single().Status);
    }

    [Fact]
    public async Task Stock_out_uses_same_base_quantity_for_validation_posting_and_cancel()
    {
        using var db = TestDbFactory.Create();
        var product = Product(db, "PCS", "BOX", 10);
        db.Stocks.Add(new Stock { WarehouseId = 3, ProductId = product.Id, Quantity = 50, AvailableQuantity = 50 });
        db.SaveChanges();
        var controller = new StockOutController(db, new DocumentNumberService(db));
        var document = new StockOut
        {
            CustomerId = 1, WarehouseId = 3,
            Details = new List<StockOutDetail>
            {
                new() { ProductId = product.Id, ProductName = product.ProductName, Unit = "BOX", Quantity = 2 }
            }
        };

        await controller.Create(document);
        var saved = db.StockOuts.Include(x => x.Details).Single();
        Assert.Equal(20m, saved.TotalQuantity);
        Assert.Equal("PCS", saved.Details.Single().Unit);
        await controller.Submit(saved.Id);
        await controller.Approve(saved.Id);
        Assert.Equal(30m, db.Stocks.Single().Quantity);

        await controller.Cancel(saved.Id);
        Assert.Equal(50m, db.Stocks.Single().Quantity);
        Assert.Equal(50m, db.Stocks.Single().AvailableQuantity);
    }

    [Fact]
    public async Task Product_without_conversion_metadata_preserves_existing_quantity_behavior()
    {
        using var db = TestDbFactory.Create();
        var product = Product(db, "PCS", string.Empty, 0);
        var detail = new StockInDetail { ProductId = product.Id, Unit = "PCS", Quantity = 3.5m };

        await StockUnitConversion.NormalizeAsync(db, new[] { detail });

        Assert.Equal(3.5m, detail.Quantity);
        Assert.Equal("PCS", detail.Unit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public async Task Explicit_package_unit_with_invalid_factor_is_rejected(int factor)
    {
        using var db = TestDbFactory.Create();
        var product = Product(db, "PCS", "CTN", factor);
        var detail = new StockOutDetail { ProductId = product.Id, ProductName = "Widget", Unit = "CTN", Quantity = 1 };

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => StockUnitConversion.NormalizeAsync(db, new[] { detail }));

        Assert.Contains("必须大于 0", error.Message);
    }

    [Fact]
    public async Task Base_unit_is_idempotent_and_never_converted_twice()
    {
        using var db = TestDbFactory.Create();
        var product = Product(db, "PCS", "CTN", 12);
        var detail = new StockInDetail { ProductId = product.Id, Unit = "PCS", Quantity = 24 };

        await StockUnitConversion.NormalizeAsync(db, new[] { detail });
        await StockUnitConversion.NormalizeAsync(db, new[] { detail });

        Assert.Equal(24m, detail.Quantity);
        Assert.Equal("PCS", detail.Unit);
    }

    private static BaseProduct Product(ERP.Infrastructure.Data.ErpDbContext db, string unit,
        string packageUnit, int factor)
    {
        var product = new BaseProduct
        {
            ProductCode = Guid.NewGuid().ToString("N"), ProductName = "Widget",
            Unit = unit, PackageUnit = packageUnit, UnitsPerPackage = factor
        };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
    }
}
