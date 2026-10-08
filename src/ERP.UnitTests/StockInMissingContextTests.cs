using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using Xunit;
namespace ERP.UnitTests;
public class StockInMissingContextTests
{
    [Fact]
    public async Task Missing_http_context_is_unauthorized_and_never_an_admin_fallback()
    {
        using var db=TestDbFactory.Create();
        var controller=new StockInController(db,new DocumentNumberService(db),new InventoryService(db));
        var error=await Assert.ThrowsAsync<BusinessException>(()=>controller.GetPaged(new PageQuery(),null));
        Assert.Equal(ErrorCodes.Unauthorized,error.Code);
        Assert.Empty(db.StockMovements); Assert.Empty(db.StockIns);
    }
}
