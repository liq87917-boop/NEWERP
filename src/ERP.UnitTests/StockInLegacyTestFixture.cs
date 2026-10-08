using ERP.Api.Controllers;
using ERP.Infrastructure.Data;
namespace ERP.UnitTests;
internal static class StockInLegacyTestFixture
{
    public static StockInController Create(ErpDbContext db, long supplier, long warehouse, params long[] otherSuppliers)
    {
        foreach(var id in otherSuppliers.Append(supplier).Distinct())
            if(!db.BaseSuppliers.Any(s=>s.Id==id)) StockInTestAuthorization.SeedSupplier(db,id);
        if(!db.BaseWarehouses.Any(w=>w.Id==warehouse)) StockInTestAuthorization.SeedWarehouse(db,warehouse);
        return StockInTestAuthorization.Create(db);
    }
}
