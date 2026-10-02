using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-215 商品销量排名可选客户 / 商品 / 单位筛选（服务层，只读）聚焦单元测试。
/// 覆盖：客户筛选在 Top 之前应用、客户筛选与业务员数据范围求交集（范围外客户返回空且不泄露）、
/// 商品 / 单位筛选在 Top 之前应用、单位精确匹配（绝不换算）、以及遗留 4 参调用保持既有排名行为。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class ProductSalesRankingFilterTests
{
    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

    private static readonly SalespersonDataScope PrivilegedScope = new() { IsPrivileged = true, AllowedCustomerIds = null };

    // ==================== 脚手架 ====================

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            EmpId = empId,
            IsDeleted = false
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = code,
            EmployeeName = code,
            IsSalesman = true,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    private static StockOut SeedStockOut(ErpDbContext db, string no, long customerId, DocumentStatus status)
    {
        var stockOut = new StockOut
        {
            StockOutNo = no,
            StockOutDate = new DateTime(2026, 9, 10),
            CustomerId = customerId,
            Status = status,
            IsDeleted = false
        };
        db.StockOuts.Add(stockOut);
        db.SaveChanges();
        return stockOut;
    }

    private static StockOutDetail SeedDetail(
        ErpDbContext db, long stockOutId, long productId, string productName, string spec, string unit, decimal quantity)
    {
        var detail = new StockOutDetail
        {
            StockOutId = stockOutId,
            ProductId = productId,
            ProductName = productName,
            Spec = spec,
            Unit = unit,
            Quantity = quantity,
            IsDeleted = false
        };
        db.StockOutDetails.Add(detail);
        db.SaveChanges();
        return detail;
    }

    // ==================== 客户筛选（在 Top 之前应用、与业务员范围求交集） ====================

    [Fact]
    public async Task 客户筛选_在Top前应用_只返回指定客户()
    {
        using var db = TestDbFactory.Create();
        var c1 = SeedCustomer(db, "C001", "客户一");
        var c2 = SeedCustomer(db, "C002", "客户二");
        var o1 = SeedStockOut(db, "OUT-1", c1.Id, DocumentStatus.Approved);
        var o2 = SeedStockOut(db, "OUT-2", c2.Id, DocumentStatus.Approved);
        SeedDetail(db, o1.Id, 1, "商品一", "大", "PCS", 100m);
        SeedDetail(db, o2.Id, 2, "商品二", "中", "PCS", 999m);

        var service = new ReportService(db);
        var result = await service.GetProductSalesRankingAsync(Start, End, 10, PrivilegedScope,
            new ProductSalesRankingFilterDto { CustomerId = c1.Id });

        var row = Assert.Single(result);
        Assert.Equal("商品一", row.ProductName);
        Assert.Equal(100m, row.TotalQuantity);
    }

    [Fact]
    public async Task 客户筛选_超出业务员范围_返回空且不泄露()
    {
        using var db = TestDbFactory.Create();
        var employee = SeedEmployee(db, "S001");
        var mine = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C002", "别人的客户", employee.Id + 1000);
        var o1 = SeedStockOut(db, "OUT-MINE", mine.Id, DocumentStatus.Approved);
        var o2 = SeedStockOut(db, "OUT-OTHER", other.Id, DocumentStatus.Approved);
        SeedDetail(db, o1.Id, 1, "商品一", "大", "PCS", 100m);
        SeedDetail(db, o2.Id, 2, "商品二", "中", "PCS", 999m);

        var restricted = new SalespersonDataScope { IsPrivileged = false, AllowedCustomerIds = new HashSet<long> { mine.Id } };
        var service = new ReportService(db);
        var result = await service.GetProductSalesRankingAsync(Start, End, 10, restricted,
            new ProductSalesRankingFilterDto { CustomerId = other.Id });

        Assert.Empty(result);
    }

    // ==================== 商品 / 单位筛选（在 Top 之前应用） ====================

    [Fact]
    public async Task 商品筛选_在Top前应用_只返回指定商品()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 1, "低排名商品", "大", "PCS", 100m);
        SeedDetail(db, out1.Id, 2, "高排名商品", "中", "PCS", 999m);

        var service = new ReportService(db);
        var result = await service.GetProductSalesRankingAsync(Start, End, 1, PrivilegedScope,
            new ProductSalesRankingFilterDto { ProductId = 1 });

        var row = Assert.Single(result);
        Assert.Equal(1L, row.ProductId);
        Assert.Equal(100m, row.TotalQuantity);
    }

    [Fact]
    public async Task 单位筛选_在Top前应用_只返回指定单位()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 1, "商品一", "大", "CTN", 999m);
        SeedDetail(db, out1.Id, 1, "商品一", "大", "PCS", 100m);

        var service = new ReportService(db);
        var result = await service.GetProductSalesRankingAsync(Start, End, 1, PrivilegedScope,
            new ProductSalesRankingFilterDto { Unit = "PCS" });

        var row = Assert.Single(result);
        Assert.Equal("PCS", row.Unit);
        Assert.Equal(100m, row.TotalQuantity);
    }

    [Fact]
    public async Task 单位筛选_精确匹配_不做单位换算_返回空()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 1, "商品一", "大", "PCS", 100m);

        var service = new ReportService(db);
        var result = await service.GetProductSalesRankingAsync(Start, End, 10, PrivilegedScope,
            new ProductSalesRankingFilterDto { Unit = "BOX" });

        Assert.Empty(result);
    }

    // ==================== 遗留调用（省略筛选） ====================

    [Fact]
    public async Task 遗留调用_省略筛选_保持既有排名行为()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 1, "商品一", "大", "PCS", 100m);
        SeedDetail(db, out1.Id, 2, "商品二", "中", "PCS", 50m);

        var service = new ReportService(db);
        var result = await service.GetProductSalesRankingAsync(Start, End, 10, PrivilegedScope);

        Assert.Equal(2, result.Count);
        Assert.Equal(100m, result[0].TotalQuantity);
        Assert.Equal(50m, result[1].TotalQuantity);
    }
}
