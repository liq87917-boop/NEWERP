using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
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

/// <summary>
/// ERP-393 采购订单来源销售订单候选 / 已存储来源解析与最终保存的真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标，复用 <see cref="PurchaseSalesOrderLinkSqlServerFixture"/>）。
/// <para>直接执行<b>真实业务代码</b>（<see cref="PurchaseOrderController"/> + <see cref="PurchaseOrderSalesOrderSourceService"/>
/// + <see cref="PurchaseSalesOrderLinkRules"/>），不复制测试专用实现：</para>
/// <list type="number">
/// <item>真实权限：无身份 / 缺既有「采购订单」/ 禁用账号一律 fail closed，且不产生任何采购单据；</item>
/// <item>候选只含客户数据范围内「已审核、未删除、未取消」的销售订单；范围外（foreign）来源绝不出现；撤销菜单后立即收敛；</item>
/// <item>已存储来源：已取消 / 未审核 / 已删除 / 范围外显式标注，且不可用时绝不披露来源客户 / 订单字段；</item>
/// <item>最终保存仍以服务端为权威：伪造快照被权威派生覆盖，未审核 / 已取消 / 伪造 Id 的来源被拒绝（save denial）；</item>
/// <item><b>两条独立连接竞争</b>：并发「取消来源销售订单」vs「新建链接该来源的采购订单」只成功其一；</item>
/// <item><b>两条独立连接竞争</b>：并发「取消来源销售订单」vs「修改既有待提交采购订单链接该来源」只成功其一。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全，在访问数据库之前校验；绝不读取 appsettings / .env / 生产凭据，也绝不执行生产库。
/// <b>构建完成不等于阶段验收</b>：只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class PurchaseOrderSalesOrderSourceSqlServerTests
    : IClassFixture<PurchaseSalesOrderLinkSqlServerFixture>
{
    private readonly PurchaseSalesOrderLinkSqlServerFixture _fixture;

    public PurchaseOrderSalesOrderSourceSqlServerTests(PurchaseSalesOrderLinkSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        PurchaseSalesOrderLinkSqlServerFixture.AssertDedicatedTarget(_fixture.ConnectionString);
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(PurchaseSalesOrderLinkSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    private static PurchaseOrderController NewController(ErpDbContext db, long? userId)
        => new(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(userId.HasValue
                        ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
                        : Array.Empty<Claim>(), "Test"))
                }
            }
        };

    private static SalesOrderController NewSalesOrderController(ErpDbContext db, long? userId)
        => new(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(userId.HasValue
                        ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
                        : Array.Empty<Claim>(), "Test"))
                }
            }
        };

    private static PurchaseOrderSalesOrderSourceCandidatePageDto Candidates(IActionResult result)
        => Assert.IsType<ApiResponse<PurchaseOrderSalesOrderSourceCandidatePageDto>>(
            Assert.IsType<OkObjectResult>(result).Value).Data!;

    private static PurchaseOrderSalesOrderSourceViewDto StoredView(IActionResult result)
        => Assert.IsType<ApiResponse<PurchaseOrderSalesOrderSourceViewDto>>(
            Assert.IsType<OkObjectResult>(result).Value).Data!;

    private static async Task<long> AdminUserIdAsync(ErpDbContext db)
        => await db.SysUsers.AsNoTracking()
            .Where(u => u.UserName == SeedData.AdminUserName)
            .Select(u => u.Id)
            .FirstAsync();

    // ==================== 种子助手（真实 SQL，EF 生成身份键） ====================

    private static async Task<long> SeedCustomerAsync(ErpDbContext db, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-POSSEL-{Tag()}", CustomerName = name, EmpId = empId, Status = 1
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<long> SeedProductAsync(ErpDbContext db, string name, string unit = "PCS")
    {
        var product = new BaseProduct
        {
            ProductCode = $"P-POSSEL-{Tag()}", ProductName = name, Spec = "规格A", Unit = unit, Status = 1
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task<SalesOrder> SeedSalesOrderAsync(ErpDbContext db, string orderNo, long customerId,
        DocumentStatus status, params (long ProductId, string Unit)[] lines)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today.AddDays(-3),
            CustomerId = customerId,
            Currency = Currency.USD,
            Status = status,
            Details = lines.Select(l => new SalesOrderDetail
            {
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
                Spec = "规格A",
                Unit = l.Unit,
                Quantity = 10m,
                UnitPrice = 20m,
                Amount = 200m
            }).ToList()
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    /// <summary>直接写入一张未链接的待提交采购订单（供「修改链接」竞争使用；不走控制器以避免消耗单据号）。</summary>
    private static async Task<long> SeedUnlinkedPurchaseOrderAsync(ErpDbContext db, long productId, string unit)
    {
        var order = new PurchaseOrder
        {
            OrderNo = $"PO-POSSEL-{Tag()}",
            OrderDate = DateTime.Today,
            SupplierId = await EnsureSupplierAsync(db),
            Currency = Currency.CNY,
            Status = DocumentStatus.Pending,
            Details = new List<PurchaseOrderDetail>
            {
                new() { ProductId = productId, ProductName = $"商品{productId}", Spec = "规格A",
                    Unit = unit, Quantity = 2m, UnitPrice = 5m, Amount = 10m }
            }
        };
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    }

    private static async Task<long> EnsureSupplierAsync(ErpDbContext db)
    {
        var existing = await db.BaseSuppliers.AsNoTracking()
            .Where(s => s.SupplierCode == "S-POSSEL-1" && !s.IsDeleted)
            .Select(s => (long?)s.Id).FirstOrDefaultAsync();
        if (existing is > 0) return existing.Value;
        var supplier = new BaseSupplier { SupplierCode = "S-POSSEL-1", SupplierName = "来源选择供应商", Status = 1 };
        db.BaseSuppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier.Id;
    }

    /// <summary>
    /// 播种真实受限业务员账号（既有「采购订单」菜单；登录名 = 员工编码，映射为业务员）：
    /// <c>ok</c> = 齐备；<c>no-menu</c> = 缺「采购订单」；<c>disabled</c> = 已禁用；<c>missing</c> = 仅播种（控制器不注入身份）。
    /// 返回（用户 Id, 员工 Id, 角色 Id）。
    /// </summary>
    private static async Task<(long UserId, long EmployeeId, long RoleId)> SeedOperatorAsync(
        ErpDbContext db, string scenario)
    {
        var userName = $"possel-{Tag()}";
        var employee = new BaseEmployee
        {
            EmployeeCode = userName, EmployeeName = scenario, IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = scenario,
            Status = scenario == "disabled" ? UserStatus.Disabled : UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole { RoleCode = $"POSSEL-{Tag()}", RoleName = "采购订单来源操作员", IsSystem = false };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (scenario != "no-menu" && scenario != "missing")
        {
            var menu = await db.SysMenus.FirstAsync(m =>
                m.MenuCode == PurchaseSalesOrderLinkRules.RequiredMenuCode && !m.IsDeleted);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            await db.SaveChangesAsync();
        }

        return (user.Id, employee.Id, role.Id);
    }

    // ==================== 1. 真实授权：既有「采购订单」菜单，无匿名 / 管理员兜底 ====================

    [Theory]
    [InlineData("missing")]
    [InlineData("no-menu")]
    [InlineData("disabled")]
    public async Task Candidates_deny_identities_without_existing_permissions(string scenario)
    {
        Guard();
        long userId;
        await using (var db = _fixture.CreateDbContext())
        {
            var customerId = await SeedCustomerAsync(db, "候选权限客户");
            var productId = await SeedProductAsync(db, "候选权限商品");
            await SeedSalesOrderAsync(db, $"SO-POSSEL-PERM-{Tag()}", customerId, DocumentStatus.Approved,
                (productId, "PCS"));
            (userId, _, _) = await SeedOperatorAsync(db, scenario);
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var ctl = NewController(db, scenario == "missing" ? null : userId);
            var ex = await Assert.ThrowsAsync<BusinessException>(() =>
                ctl.GetSalesOrderSourceCandidates(null, null, 0, 0));
            Assert.Equal(scenario == "missing" ? ErrorCodes.Unauthorized : ErrorCodes.Forbidden, ex.Code);
        }
    }

    // ==================== 2. 候选：客户数据范围 / 范围外隐藏 / 撤销授权立即收敛 ====================

    [Fact]
    public async Task Candidates_scope_own_only_foreign_hidden_and_revoked_converges()
    {
        Guard();
        long userId, roleId, ownSalesOrderId;
        await using (var db = _fixture.CreateDbContext())
        {
            long employeeId;
            (userId, employeeId, roleId) = await SeedOperatorAsync(db, "ok");
            var ownCustomerId = await SeedCustomerAsync(db, "自有客户", employeeId);
            var foreignCustomerId = await SeedCustomerAsync(db, "范围外客户");
            var productId = await SeedProductAsync(db, "范围商品");
            ownSalesOrderId = (await SeedSalesOrderAsync(db, $"SO-POSSEL-OWN-{Tag()}", ownCustomerId,
                DocumentStatus.Approved, (productId, "PCS"))).Id;
            await SeedSalesOrderAsync(db, $"SO-POSSEL-FOREIGN-{Tag()}", foreignCustomerId,
                DocumentStatus.Approved, (productId, "PCS"));
            await SeedSalesOrderAsync(db, $"SO-POSSEL-PENDING-{Tag()}", ownCustomerId,
                DocumentStatus.Pending, (productId, "PCS"));
            await SeedSalesOrderAsync(db, $"SO-POSSEL-CANCELLED-{Tag()}", ownCustomerId,
                DocumentStatus.Cancelled, (productId, "PCS"));
            var deleted = await SeedSalesOrderAsync(db, $"SO-POSSEL-DELETED-{Tag()}", ownCustomerId,
                DocumentStatus.Approved, (productId, "PCS"));
            deleted.IsDeleted = true;
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var ctl = NewController(db, userId);
            var page = Candidates(await ctl.GetSalesOrderSourceCandidates(null, null, 1, 50));
            Assert.Equal(1, page.Total);
            Assert.Equal(ownSalesOrderId, page.Items.Single().SalesOrderId);
            Assert.True(page.Items.Single().Eligible);

            // 撤销本账号角色菜单授权 → 下一次请求立即收敛（绝不缓存授权）
            foreach (var grant in await db.SysRoleMenus
                         .Where(g => g.RoleId == roleId && !g.IsDeleted).ToListAsync())
                grant.IsDeleted = true;
            await db.SaveChangesAsync();

            var ex = await Assert.ThrowsAsync<BusinessException>(() =>
                ctl.GetSalesOrderSourceCandidates(null, null, 0, 0));
            Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        }
    }

    // ==================== 3. 已存储来源标注：已取消 / 未审核 / 已删除显式，且不可用不泄露 ====================

    [Fact]
    public async Task Stored_source_annotations_are_explicit_and_deleted_is_unavailable()
    {
        Guard();
        long adminId, liveId, cancelledId, pendingId, deletedId;
        await using (var db = _fixture.CreateDbContext())
        {
            adminId = await AdminUserIdAsync(db);
            var customerId = await SeedCustomerAsync(db, "来源标注客户");
            var productId = await SeedProductAsync(db, "来源标注商品");
            var live = await SeedSalesOrderAsync(db, $"SO-POSSEL-LIVE-{Tag()}", customerId,
                DocumentStatus.Approved, (productId, "PCS"));
            var cancelled = await SeedSalesOrderAsync(db, $"SO-POSSEL-CANCEL-{Tag()}", customerId,
                DocumentStatus.Cancelled, (productId, "PCS"));
            var pending = await SeedSalesOrderAsync(db, $"SO-POSSEL-PEND-{Tag()}", customerId,
                DocumentStatus.Pending, (productId, "PCS"));
            var deleted = await SeedSalesOrderAsync(db, $"SO-POSSEL-DEL-{Tag()}", customerId,
                DocumentStatus.Approved, (productId, "PCS"));
            deleted.IsDeleted = true;
            await db.SaveChangesAsync();

            liveId = await SeedUnlinkedPurchaseOrderAsync(db, productId, "PCS");
            cancelledId = await SeedUnlinkedPurchaseOrderAsync(db, productId, "PCS");
            pendingId = await SeedUnlinkedPurchaseOrderAsync(db, productId, "PCS");
            deletedId = await SeedUnlinkedPurchaseOrderAsync(db, productId, "PCS");
            foreach (var (orderId, sourceId) in new[]
                     {
                         (liveId, live.Id), (cancelledId, cancelled.Id),
                         (pendingId, pending.Id), (deletedId, deleted.Id)
                     })
            {
                var po = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
                po.OwningSalesOrderId = sourceId;
                po.OwningCustomerId = customerId;
                po.OwningSalesOrderNo = "FORGED";
            }
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var ctl = NewController(db, adminId);

            var live = StoredView(await ctl.GetStoredSalesOrderSource(liveId));
            Assert.True(live.Linked);
            Assert.False(live.Unavailable);
            Assert.True(live.EligibleForNewLink);
            Assert.NotEmpty(live.OrderNo);

            var cancelled = StoredView(await ctl.GetStoredSalesOrderSource(cancelledId));
            Assert.False(cancelled.Unavailable);
            Assert.False(cancelled.EligibleForNewLink);
            Assert.Contains("已取消", cancelled.Annotation);

            var pending = StoredView(await ctl.GetStoredSalesOrderSource(pendingId));
            Assert.False(pending.Unavailable);
            Assert.False(pending.EligibleForNewLink);
            Assert.Contains("未审核", pending.Annotation);

            var deleted = StoredView(await ctl.GetStoredSalesOrderSource(deletedId));
            Assert.True(deleted.Unavailable);
            Assert.True(deleted.Linked);
            Assert.Empty(deleted.OrderNo);
            Assert.Null(deleted.CustomerId);
        }
    }

    // ==================== 4. 最终保存：权威派生 + save denial（伪造 / 已取消来源） ====================

    [Fact]
    public async Task Final_save_derives_authoritative_snapshot_and_denies_forged_source()
    {
        Guard();
        long adminId, salesOrderId, customerId, productId, createdId;
        await using (var db = _fixture.CreateDbContext())
        {
            adminId = await AdminUserIdAsync(db);
            customerId = await SeedCustomerAsync(db, "保存权威客户");
            productId = await SeedProductAsync(db, "保存权威商品");
            salesOrderId = (await SeedSalesOrderAsync(db, $"SO-POSSEL-SAVE-{Tag()}", customerId,
                DocumentStatus.Approved, (productId, "PCS"))).Id;
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var ctl = NewController(db, adminId);
            var entity = new PurchaseOrder
            {
                OrderDate = DateTime.Today,
                SupplierId = await EnsureSupplierAsync(db),
                Currency = Currency.CNY,
                OwningSalesOrderId = salesOrderId,
                OwningSalesOrderNo = "FORGED-NO",
                OwningCustomerName = "FORGED-NAME",
                Details = new List<PurchaseOrderDetail>
                {
                    new() { ProductId = productId, ProductName = "商品", Spec = "规格A",
                        Unit = "PCS", Quantity = 2m, UnitPrice = 5m }
                }
            };

            Assert.IsType<OkObjectResult>(await ctl.Create(entity));
            createdId = entity.Id;
            var saved = await db.PurchaseOrders.Include(o => o.Details).SingleAsync(o => o.Id == createdId);
            Assert.Equal(salesOrderId, saved.OwningSalesOrderId);
            Assert.NotEqual("FORGED-NO", saved.OwningSalesOrderNo);
            Assert.Equal(customerId, saved.OwningCustomerId);
            Assert.Equal(Currency.CNY, saved.Currency);          // 原始商业币种不变（不强制币种一致）
            Assert.Equal(5m, saved.Details.Single().UnitPrice);  // 原始单价不变

            // 伪造 / 不存在的来源 Id 一律拒绝，且不新增单据
            var before = await db.PurchaseOrders.CountAsync();
            var forged = new PurchaseOrder
            {
                OrderDate = DateTime.Today,
                SupplierId = saved.SupplierId,
                OwningSalesOrderId = 987654321L,
                Details = new List<PurchaseOrderDetail>
                {
                    new() { ProductId = productId, ProductName = "商品", Spec = "规格A",
                        Unit = "PCS", Quantity = 1m, UnitPrice = 1m }
                }
            };
            var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(forged));
            Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
            Assert.Equal(before, await db.PurchaseOrders.CountAsync());
        }

        await using (var db = _fixture.CreateDbContext())
        {
            // 来源被取消后：保存 fail closed（业务界面必须重新选择来源）
            var so = await db.SalesOrders.SingleAsync(o => o.Id == salesOrderId);
            so.Status = DocumentStatus.Cancelled;
            await db.SaveChangesAsync();

            var ctl = NewController(db, adminId);
            var denied = new PurchaseOrder
            {
                OrderDate = DateTime.Today,
                SupplierId = await EnsureSupplierAsync(db),
                OwningSalesOrderId = salesOrderId,
                Details = new List<PurchaseOrderDetail>
                {
                    new() { ProductId = productId, ProductName = "商品", Spec = "规格A",
                        Unit = "PCS", Quantity = 1m, UnitPrice = 1m }
                }
            };
            var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(denied));
            Assert.Contains("已取消", ex.Message);
        }
    }

    // ==================== 5. 两条独立连接竞争（真实 SQL，串行化上游来源行锁） ====================

    [Fact]
    public async Task Concurrent_sales_order_cancel_vs_create_linked_purchase_order_only_one_wins()
    {
        Guard();
        long adminId, salesOrderId, productId;
        await using (var db = _fixture.CreateDbContext())
        {
            adminId = await AdminUserIdAsync(db);
            var customerId = await SeedCustomerAsync(db, "并发创建客户");
            productId = await SeedProductAsync(db, "并发创建商品");
            salesOrderId = (await SeedSalesOrderAsync(db, $"SO-POSSEL-RACE-A-{Tag()}", customerId,
                DocumentStatus.Approved, (productId, "PCS"))).Id;
        }

        var results = await Task.WhenAll(
            TryCreateLinkedAsync(adminId, salesOrderId, productId),
            TryCancelSalesOrderAsync(adminId, salesOrderId));

        Assert.Equal(1, results.Count(r => r.Ok));
        Assert.IsType<BusinessException>(Assert.Single(results.Where(r => !r.Ok)).Error);

        await using var verify = _fixture.CreateDbContext();
        var cancelled = (await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == salesOrderId))
            .Status == DocumentStatus.Cancelled;
        var linkedCount = await verify.PurchaseOrders.CountAsync(o =>
            !o.IsDeleted && o.OwningSalesOrderId == salesOrderId && o.Status != DocumentStatus.Cancelled);
        if (cancelled) Assert.Equal(0, linkedCount);       // 来源已取消 → 不可能存在未取消的链接采购单
        else Assert.Equal(1, linkedCount);                 // 采购单先落库 → 取消来源被拒绝
    }

    [Fact]
    public async Task Concurrent_sales_order_cancel_vs_update_link_only_one_wins()
    {
        Guard();
        long adminId, salesOrderId, purchaseOrderId, productId;
        await using (var db = _fixture.CreateDbContext())
        {
            adminId = await AdminUserIdAsync(db);
            var customerId = await SeedCustomerAsync(db, "并发修改客户");
            productId = await SeedProductAsync(db, "并发修改商品");
            salesOrderId = (await SeedSalesOrderAsync(db, $"SO-POSSEL-RACE-B-{Tag()}", customerId,
                DocumentStatus.Approved, (productId, "PCS"))).Id;
            purchaseOrderId = await SeedUnlinkedPurchaseOrderAsync(db, productId, "PCS");
        }

        var results = await Task.WhenAll(
            TryUpdateLinkedAsync(adminId, purchaseOrderId, salesOrderId, productId),
            TryCancelSalesOrderAsync(adminId, salesOrderId));

        Assert.Equal(1, results.Count(r => r.Ok));
        Assert.IsType<BusinessException>(Assert.Single(results.Where(r => !r.Ok)).Error);

        await using var verify = _fixture.CreateDbContext();
        var cancelled = (await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == salesOrderId))
            .Status == DocumentStatus.Cancelled;
        var linked = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == purchaseOrderId);
        if (cancelled) Assert.Null(linked.OwningSalesOrderId);   // 来源已取消 → 修改链接被拒绝，原单不链接
        else Assert.Equal(salesOrderId, linked.OwningSalesOrderId);
    }

    // ==================== 独立连接辅助（真实 SQL，两条连接竞争的证据） ====================

    private async Task<(bool Ok, Exception? Error)> TryCreateLinkedAsync(
        long userId, long salesOrderId, long productId)
    {
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, userId);
        try
        {
            var entity = new PurchaseOrder
            {
                OrderDate = DateTime.Today,
                SupplierId = await EnsureSupplierAsync(db),
                Currency = Currency.CNY,
                OwningSalesOrderId = salesOrderId,
                Details = new List<PurchaseOrderDetail>
                {
                    new() { ProductId = productId, ProductName = "商品", Spec = "规格A",
                        Unit = "PCS", Quantity = 1m, UnitPrice = 1m }
                }
            };
            await ctl.Create(entity);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }

    private async Task<(bool Ok, Exception? Error)> TryUpdateLinkedAsync(
        long userId, long purchaseOrderId, long salesOrderId, long productId)
    {
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, userId);
        try
        {
            var entity = new PurchaseOrder
            {
                OrderDate = DateTime.Today,
                SupplierId = await EnsureSupplierAsync(db),
                Currency = Currency.CNY,
                OwningSalesOrderId = salesOrderId,
                Details = new List<PurchaseOrderDetail>
                {
                    new() { ProductId = productId, ProductName = "商品", Spec = "规格A",
                        Unit = "PCS", Quantity = 1m, UnitPrice = 1m }
                }
            };
            await ctl.Update(purchaseOrderId, entity);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }

    private async Task<(bool Ok, Exception? Error)> TryCancelSalesOrderAsync(long userId, long salesOrderId)
    {
        await using var db = _fixture.CreateDbContext();
        var ctl = NewSalesOrderController(db, userId);
        try
        {
            await ctl.Cancel(salesOrderId);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }
}


/// <summary>ERP-393 真实 SQL 目标护栏断言（在访问数据库之前拒绝非专用目标）。</summary>
public sealed class PurchaseOrderSalesOrderSourceTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            PurchaseSalesOrderLinkSqlServerFixture.AssertDedicatedTarget(connection));
}
