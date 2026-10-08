using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace ERP.IntegrationTests;
// Strict fresh GUID dedicated LocalDB fixture; no production or existing database reset.
public class StockInAuthorizationSqlServerTests : IClassFixture<SupplierPaymentLifecycleSqlServerFixture>
{
    private readonly SupplierPaymentLifecycleSqlServerFixture fixture;
    public StockInAuthorizationSqlServerTests(SupplierPaymentLifecycleSqlServerFixture fixture)=>this.fixture=fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target=new SqlConnectionStringBuilder(fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance",target.DataSource,ignoreCase:true);
        Assert.StartsWith(SupplierPaymentLifecycleSqlServerFixture.DatabasePrefix,target.InitialCatalog,StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("no-menu")]
    public async Task Live_identity_and_menu_denials_leave_inventory_unchanged(string scenario)
    {
        Guard();
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

    // ==================== 已授权入库操作员真实放行 ====================

    [Fact]
    public async Task Seeded_authorized_inbound_operator_is_admitted_with_existing_stock_in_menu()
    {
        Guard();
        await using var db=fixture.CreateDbContext();
        var supplierId=await SeedSupplierAsync(db);
        var warehouseId=await SeedWarehouseAsync(db);
        var adminId=await db.SysUsers.AsNoTracking().Where(u=>u.UserName==SeedData.AdminUserName).Select(u=>u.Id).FirstAsync();

        var menuCodes=await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(db,adminId);
        Assert.Contains(StockInAuthorizationRules.RequiredMenuCode,menuCodes,StringComparer.OrdinalIgnoreCase);

        // 合法放行：特权入库操作员可访问未链接入库单（不新增权限、不降低既有菜单授权口径）。
        await StockInAuthorizationRules.EnsureAuthorizedAsync(db,adminId,supplierId,warehouseId,purchaseOrderId:null);
        await StockInAuthorizationRules.EnsureAuthorizedAsync(db,adminId,new StockIn());
    }

    [Fact]
    public async Task Restricted_operator_scope_is_enforced_for_linked_and_unlinked_receipts_on_real_sql()
    {
        Guard();
        await using var db=fixture.CreateDbContext();
        var supplierId=await SeedSupplierAsync(db);
        var warehouseId=await SeedWarehouseAsync(db);
        var (userId,_,ownCustomerId)=await SeedRestrictedInboundOperatorAsync(db);
        var foreignCustomerId=await SeedCustomerAsync(db,"范围外客户");
        var ownOrderId=await SeedApprovedOrderAsync(db,supplierId,ownCustomerId);
        var foreignOrderId=await SeedApprovedOrderAsync(db,supplierId,foreignCustomerId);
        var beforeStockIns=await db.StockIns.CountAsync();
        var beforeMovements=await db.StockMovements.CountAsync();

        // 本人客户订单与合法未链接入库单：放行。
        await StockInAuthorizationRules.EnsureAuthorizedAsync(db,userId,supplierId,warehouseId,ownOrderId);
        await StockInAuthorizationRules.EnsureAuthorizedAsync(db,userId,supplierId,warehouseId,purchaseOrderId:null);
        await StockInAuthorizationRules.EnsureAuthorizedAsync(db,userId,new StockIn { PurchaseOrderId=ownOrderId });

        // 范围外客户订单：创建与读取（详情 / 流水）一律 fail closed，且不泄露范围外客户。
        var createError=await Assert.ThrowsAsync<BusinessException>(
            ()=>StockInAuthorizationRules.EnsureAuthorizedAsync(db,userId,supplierId,warehouseId,foreignOrderId));
        Assert.Equal(ErrorCodes.Forbidden,createError.Code);
        var readError=await Assert.ThrowsAsync<BusinessException>(
            ()=>StockInAuthorizationRules.EnsureAuthorizedAsync(db,userId,new StockIn { PurchaseOrderId=foreignOrderId }));
        Assert.Equal(ErrorCodes.Forbidden,readError.Code);

        Assert.Equal(beforeStockIns,await db.StockIns.CountAsync());
        Assert.Equal(beforeMovements,await db.StockMovements.CountAsync());
    }

    // ==================== 读取与变更之间撤销授权立即收敛 ====================

    [Fact]
    public async Task Revocation_between_resolution_and_mutation_is_fail_closed_on_real_sql()
    {
        Guard();
        await using var db=fixture.CreateDbContext();
        var (userId,roleId,_)=await SeedRestrictedInboundOperatorAsync(db);

        await StockInAuthorizationRules.EnsureMenuAuthorizedAsync(db,userId);

        var grants=await db.SysRoleMenus.Where(rm=>rm.RoleId==roleId && !rm.IsDeleted).ToListAsync();
        Assert.NotEmpty(grants);
        foreach(var grant in grants) grant.IsDeleted=true;
        await db.SaveChangesAsync();

        var error=await Assert.ThrowsAsync<BusinessException>(
            ()=>StockInAuthorizationRules.EnsureMenuAuthorizedAsync(db,userId));
        Assert.Equal(ErrorCodes.Forbidden,error.Code);
    }

    // ==================== 种子助手（SQL 生成身份键） ====================

    private static async Task<long> SeedSupplierAsync(ErpDbContext db)
    {
        var supplier=new BaseSupplier { SupplierCode=$"AUTH-{Guid.NewGuid():N}", SupplierName="授权测试供应商", Status=1 };
        db.BaseSuppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier.Id;
    }

    private static async Task<long> SeedWarehouseAsync(ErpDbContext db)
    {
        var warehouse=new BaseWarehouse { WarehouseCode=$"AUTH-{Guid.NewGuid():N}", WarehouseName="授权测试仓库", Status=1 };
        db.BaseWarehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return warehouse.Id;
    }

    private static async Task<long> SeedCustomerAsync(ErpDbContext db,string name,long? empId=null)
    {
        var customer=new BaseCustomer { CustomerCode=$"AUTH-C-{Guid.NewGuid():N}", CustomerName=name, EmpId=empId, Status=1 };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<long> SeedApprovedOrderAsync(ErpDbContext db,long supplierId,long? owningCustomerId)
    {
        var order=new PurchaseOrder
        {
            OrderNo=$"AUTH-PO-{Guid.NewGuid():N}",
            OrderDate=DateTime.Today,
            SupplierId=supplierId,
            Currency=Currency.CNY,
            ExchangeRate=1m,
            Status=DocumentStatus.Approved,
            OwningCustomerId=owningCustomerId
        };
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    }

    /// <summary>播种受限制入库操作员（业务员映射 + 既有 stock-in 菜单，无特权角色）与本人客户。</summary>
    private static async Task<(long UserId, long RoleId, long CustomerId)> SeedRestrictedInboundOperatorAsync(ErpDbContext db)
    {
        var code=$"stockin-op-{Guid.NewGuid():N}";
        var employee=new BaseEmployee { EmployeeCode=code, EmployeeName=code, IsSalesman=true, Status=1 };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var user=new SysUser { UserName=code, DisplayName=code, PasswordHash="hash", PasswordSalt="salt", Status=UserStatus.Enabled };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role=new SysRole { RoleCode=$"StockInOp-{Guid.NewGuid():N}", RoleName="入库操作员", IsSystem=false };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId=user.Id, RoleId=role.Id });
        await db.SaveChangesAsync();

        var menuId=await db.SysMenus.AsNoTracking()
            .Where(m=>m.MenuCode==StockInAuthorizationRules.RequiredMenuCode && !m.IsDeleted)
            .Select(m=>m.Id)
            .FirstAsync();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId=role.Id, MenuId=menuId });
        await db.SaveChangesAsync();

        var customerId=await SeedCustomerAsync(db,"范围内客户",employee.Id);
        return (user.Id, role.Id, customerId);
    }
}
