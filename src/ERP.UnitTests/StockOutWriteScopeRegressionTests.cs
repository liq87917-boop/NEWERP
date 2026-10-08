using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

public class StockOutWriteScopeRegressionTests
{
    [Fact]
    public async Task Create_ExplicitForeignSource_UnmappedCaller_CannotWrite()
    {
        using var db = TestDbFactory.Create();
        db.BaseProducts.Add(new BaseProduct { Id = 710001, ProductCode = "SCOPE-P1", ProductName = "P1", Unit = "PCS" });
        db.SalesOrders.Add(new SalesOrder
        {
            Id = 880001, OrderNo = "SCOPE-SO", CustomerId = 220001, Status = DocumentStatus.Approved,
            Details = new List<SalesOrderDetail>
            {
                new() { ProductId = 710001, ProductName = "P1", Unit = "PCS", Quantity = 4, UnitPrice = 1, Amount = 4 }
            }
        });
        await db.SaveChangesAsync();
        var scope = await SalespersonDataScopeService.ResolveAsync(db, 990001);
        Assert.False(scope.AllowsCustomer(220001));
        var controller = new StockOutController(db, new DocumentNumberService(db), new InventoryService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "990001") }, "Test"))
                }
            }
        };
        await Assert.ThrowsAsync<BusinessException>(() => controller.Create(new StockOut
        {
            SalesOrderId = 880001, CustomerId = 220001, WarehouseId = 330001,
            Details = new List<StockOutDetail>
            {
                new() { ProductId = 710001, ProductName = "P1", Unit = "PCS", Quantity = 1 }
            }
        }));
        Assert.Empty(db.StockOuts);
        Assert.Empty(db.StockMovements);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("approve")]
    [InlineData("cancel")]
    [InlineData("submit")]
    [InlineData("delete")]
    [InlineData("movements")]
    public async Task ForeignDocument_AllRoutes_RejectBeforeMutation(string operation)
    {
        using var db = TestDbFactory.Create();
        var entity = new StockOut { CustomerId = 220001, Status = operation == "approve" ? DocumentStatus.Submitted : DocumentStatus.Pending, StockOutNo = "SCOPE-FOREIGN" };
        db.StockOuts.Add(entity); await db.SaveChangesAsync();
        var initial = entity.Status;
        var controller = StockOutTestAuthorization.ForUser(db, 990001);
        Func<Task<IActionResult>> action = operation switch
        {
            "update" => () => controller.Update(entity.Id, new StockOut { CustomerId = 220001 }),
            "approve" => () => controller.Approve(entity.Id),
            "cancel" => () => controller.Cancel(entity.Id),
            "submit" => () => controller.Submit(entity.Id),
            "delete" => () => controller.Delete(entity.Id),
            _ => () => controller.GetMovements(entity.Id)
        };
        await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(initial, entity.Status); Assert.False(entity.IsDeleted);
        Assert.Empty(db.StockMovements); Assert.Empty(db.Stocks);
    }

    [Fact]
    public async Task MissingIdentity_CreateFailsClosed()
    {
        using var db = TestDbFactory.Create();
        var ctl = StockOutTestAuthorization.ForUser(db, null);
        await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(new StockOut { CustomerId = 220001 }));
        Assert.Empty(db.StockOuts);
    }

    [Fact]
    public async Task AssignedCustomer_Allowed_ThenReassignmentAndRevocationRejected()
    {
        using var db = TestDbFactory.Create();
        var user = new SysUser { UserName = "sales-scope", DisplayName = "Sales", PasswordHash = "hash", PasswordSalt = "salt", Status = UserStatus.Enabled };
        var employee = new BaseEmployee { EmployeeCode = "sales-scope", EmployeeName = "Sales", IsSalesman = true, Status = 1 };
        db.SysUsers.Add(user); db.BaseEmployees.Add(employee); await db.SaveChangesAsync();
        var customer = new BaseCustomer { CustomerCode = "SCOPE-C", CustomerName = "Assigned", EmpId = employee.Id };
        db.BaseCustomers.Add(customer); await db.SaveChangesAsync();
        var ctl = StockOutTestAuthorization.ForUser(db, user.Id);
        await ctl.Create(new StockOut { CustomerId = customer.Id });
        var saved = db.StockOuts.Single();
        await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(saved.Id, new StockOut { CustomerId = customer.Id + 500 }));
        Assert.Equal(customer.Id, saved.CustomerId);
        customer.EmpId = null; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(saved.Id));
        Assert.Equal(DocumentStatus.Pending, saved.Status); Assert.Empty(db.StockMovements);
    }
}
