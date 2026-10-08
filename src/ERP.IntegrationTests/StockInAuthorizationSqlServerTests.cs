using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace ERP.IntegrationTests;
// Strict fresh GUID dedicated LocalDB fixture; no production or existing database reset.
public class StockInAuthorizationSqlServerTests : IClassFixture<SupplierPaymentLifecycleSqlServerFixture>
{
    private readonly SupplierPaymentLifecycleSqlServerFixture fixture;
    public StockInAuthorizationSqlServerTests(SupplierPaymentLifecycleSqlServerFixture fixture)=>this.fixture=fixture;
    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("no-menu")]
    public async Task Live_identity_and_menu_denials_leave_inventory_unchanged(string scenario)
    {
        await using var db=fixture.CreateDbContext(); long? userId=null;
        if(scenario!="missing")
        {
            var user=new SysUser { UserName="stockin-denied-"+Guid.NewGuid().ToString("N"), DisplayName="Denied fixture", PasswordHash="hash", PasswordSalt="salt", Status=scenario=="disabled" ? UserStatus.Disabled : UserStatus.Enabled };
            db.SysUsers.Add(user); await db.SaveChangesAsync(); userId=user.Id;
        }
        var beforeMovements=await db.StockMovements.CountAsync(); var beforeStock=await db.Stocks.CountAsync();
        var error=await Assert.ThrowsAsync<BusinessException>(()=>StockInAuthorizationRules.EnsureAuthorizedAsync(db,userId,new StockIn()));
        Assert.Equal(scenario=="missing" ? ErrorCodes.Unauthorized : ErrorCodes.Forbidden,error.Code);
        Assert.Equal(beforeMovements,await db.StockMovements.CountAsync()); Assert.Equal(beforeStock,await db.Stocks.CountAsync());
    }
}
