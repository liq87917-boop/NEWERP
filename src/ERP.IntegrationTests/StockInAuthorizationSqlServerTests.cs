using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
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

        // 范围外客户订单：创建与读取（详情 / 流水）一律按非披露「不存在」拒绝，不泄露范围外客户。
        var createError=await Assert.ThrowsAsync<BusinessException>(
            ()=>StockInAuthorizationRules.EnsureAuthorizedAsync(db,userId,supplierId,warehouseId,foreignOrderId));
        Assert.Equal(ErrorCodes.NotFound,createError.Code);
        Assert.Equal(StockInAuthorizationRules.SourceScopeDeniedText,createError.Message);
        var readError=await Assert.ThrowsAsync<BusinessException>(
            ()=>StockInAuthorizationRules.EnsureAuthorizedAsync(db,userId,new StockIn { PurchaseOrderId=foreignOrderId }));
        Assert.Equal(ErrorCodes.NotFound,readError.Code);
        Assert.Equal(StockInAuthorizationRules.NotFoundText,readError.Message);

        Assert.Equal(beforeStockIns,await db.StockIns.CountAsync());
        Assert.Equal(beforeMovements,await db.StockMovements.CountAsync());
    }

    // ==================== 控制器级：闸门先于读取、存在与不存在同错（真实 SQL） ====================

    private static StockInController BuildController(ErpDbContext db,long? userId)
    {
        var claims=userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier,userId.Value.ToString()) }
            : Array.Empty<Claim>();
        return new StockInController(db,new DocumentNumberService(db),new InventoryService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims,"IntTest"))
                }
            }
        };
    }

    [Fact]
    public async Task Denied_callers_share_the_same_controlled_error_for_existing_and_unknown_receipt_ids_on_real_sql()
    {
        Guard();
        await using var db=fixture.CreateDbContext();
        var supplierId=await SeedSupplierAsync(db);
        var warehouseId=await SeedWarehouseAsync(db);
        var (_,_,customerId)=await SeedRestrictedInboundOperatorAsync(db);
        var orderId=await SeedApprovedOrderAsync(db,supplierId,customerId);
        var receiptId=await SeedReceiptAsync(db,supplierId,warehouseId,orderId,DocumentStatus.Pending);
        const long unknownId=999_999_999L;

        // 缺失身份：已存在与不存在的 Id 都得到受控未认证（绝不因 Id 存在与否而差异化）。
        var anonymous=BuildController(db,null);
        Assert.Equal(ErrorCodes.Unauthorized,(await Assert.ThrowsAsync<BusinessException>(()=>anonymous.GetById(receiptId))).Code);
        Assert.Equal(ErrorCodes.Unauthorized,(await Assert.ThrowsAsync<BusinessException>(()=>anonymous.GetById(unknownId))).Code);
        Assert.Equal(ErrorCodes.Unauthorized,(await Assert.ThrowsAsync<BusinessException>(()=>anonymous.GetMovements(receiptId))).Code);
        Assert.Equal(ErrorCodes.Unauthorized,(await Assert.ThrowsAsync<BusinessException>(()=>anonymous.GetMovements(unknownId))).Code);

        // 禁用账号 / 无菜单账号：已存在与不存在的 Id 都得到权限不足。
        foreach(var deniedUserId in new[]
                {
                    await SeedDeniedUserAsync(db,UserStatus.Disabled,withStockInMenu:true),
                    await SeedDeniedUserAsync(db,UserStatus.Enabled,withStockInMenu:false)
                })
        {
            var ctl=BuildController(db,deniedUserId);
            Assert.Equal(ErrorCodes.Forbidden,(await Assert.ThrowsAsync<BusinessException>(()=>ctl.GetById(receiptId))).Code);
            Assert.Equal(ErrorCodes.Forbidden,(await Assert.ThrowsAsync<BusinessException>(()=>ctl.GetById(unknownId))).Code);
            Assert.Equal(ErrorCodes.Forbidden,(await Assert.ThrowsAsync<BusinessException>(()=>ctl.GetMovements(receiptId))).Code);
            Assert.Equal(ErrorCodes.Forbidden,(await Assert.ThrowsAsync<BusinessException>(()=>ctl.GetMovements(unknownId))).Code);
        }

        // 拒绝方零副作用：状态 / 库存 / 流水保持原样。
        Assert.Equal(DocumentStatus.Pending,(await db.StockIns.AsNoTracking().SingleAsync(o=>o.Id==receiptId)).Status);
        Assert.Equal(0,await db.StockMovements.CountAsync());
    }

    [Fact]
    public async Task Restricted_operator_reads_and_lifecycle_are_non_disclosing_for_foreign_and_unknown_receipts_on_real_sql()
    {
        Guard();
        await using var db=fixture.CreateDbContext();
        var supplierId=await SeedSupplierAsync(db);
        var warehouseId=await SeedWarehouseAsync(db);
        var (userId,_,ownCustomerId)=await SeedRestrictedInboundOperatorAsync(db);
        var foreignCustomerId=await SeedCustomerAsync(db,"范围外客户");
        var ownOrderId=await SeedApprovedOrderAsync(db,supplierId,ownCustomerId);
        var foreignOrderId=await SeedApprovedOrderAsync(db,supplierId,foreignCustomerId);
        var ownId=await SeedReceiptAsync(db,supplierId,warehouseId,ownOrderId,DocumentStatus.Pending);
        var foreignId=await SeedReceiptAsync(db,supplierId,warehouseId,foreignOrderId,DocumentStatus.Submitted);
        const long unknownId=999_999_999L;
        var ctl=BuildController(db,userId);

        // 范围内单据仍可读（授权路径保持可用）。
        Assert.IsType<OkObjectResult>(await ctl.GetById(ownId));

        var foreignDetail=await Assert.ThrowsAsync<BusinessException>(()=>ctl.GetById(foreignId));
        var unknownDetail=await Assert.ThrowsAsync<BusinessException>(()=>ctl.GetById(unknownId));
        Assert.Equal(ErrorCodes.NotFound,foreignDetail.Code);
        Assert.Equal(foreignDetail.Code,unknownDetail.Code);
        Assert.Equal(foreignDetail.Message,unknownDetail.Message);

        var foreignMovement=await Assert.ThrowsAsync<BusinessException>(()=>ctl.GetMovements(foreignId));
        var unknownMovement=await Assert.ThrowsAsync<BusinessException>(()=>ctl.GetMovements(unknownId));
        Assert.Equal(ErrorCodes.NotFound,foreignMovement.Code);
        Assert.Equal(foreignMovement.Message,unknownMovement.Message);

        var foreignCancel=await Assert.ThrowsAsync<BusinessException>(()=>ctl.Cancel(foreignId));
        var unknownCancel=await Assert.ThrowsAsync<BusinessException>(()=>ctl.Cancel(unknownId));
        Assert.Equal(ErrorCodes.NotFound,foreignCancel.Code);
        Assert.Equal(foreignCancel.Message,unknownCancel.Message);

        // 拒绝方零副作用：范围外单据状态不变，库存 / 流水不变。
        Assert.Equal(DocumentStatus.Submitted,(await db.StockIns.AsNoTracking().SingleAsync(o=>o.Id==foreignId)).Status);
        Assert.Equal(0,await db.StockMovements.CountAsync());
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

    /// <summary>播种一张入库单（SQL 生成主键），用于真实 SQL 的读取 / 生命周期非披露验证。</summary>
    private static async Task<long> SeedReceiptAsync(ErpDbContext db,long supplierId,long warehouseId,
        long? purchaseOrderId,DocumentStatus status)
    {
        var receipt=new StockIn
        {
            StockInNo=$"AUTH-RK-{Guid.NewGuid():N}",
            StockInDate=DateTime.Today,
            SupplierId=supplierId,
            WarehouseId=warehouseId,
            PurchaseOrderId=purchaseOrderId,
            TotalQuantity=10m,
            Status=status
        };
        db.StockIns.Add(receipt);
        await db.SaveChangesAsync();
        return receipt.Id;
    }

    /// <summary>播种被拒绝账号（禁用 / 无 stock-in 菜单），用于「存在与不存在同错」验证。</summary>
    private static async Task<long> SeedDeniedUserAsync(ErpDbContext db,UserStatus status,bool withStockInMenu)
    {
        var user=new SysUser
        {
            UserName=$"stockin-denied-{Guid.NewGuid():N}",
            DisplayName="拒绝场景账号",
            PasswordHash="hash",
            PasswordSalt="salt",
            Status=status
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role=new SysRole { RoleCode=$"Denied-{Guid.NewGuid():N}", RoleName="拒绝场景角色", IsSystem=false };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId=user.Id, RoleId=role.Id });
        await db.SaveChangesAsync();

        if(withStockInMenu)
        {
            var menuId=await db.SysMenus.AsNoTracking()
                .Where(m=>m.MenuCode==StockInAuthorizationRules.RequiredMenuCode && !m.IsDeleted)
                .Select(m=>m.Id)
                .FirstAsync();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId=role.Id, MenuId=menuId });
            await db.SaveChangesAsync();
        }

        return user.Id;
    }
}
