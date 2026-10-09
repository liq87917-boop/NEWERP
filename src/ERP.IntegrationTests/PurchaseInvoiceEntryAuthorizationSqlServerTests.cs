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
/// ERP-462 供应商采购发票证据入口实时授权 SQL Server 集成测试（专用 NEWERP_AUTOTEST 护栏）。
/// <list type="number">
/// <item><b>真实控制器 + 真实既有身份 / 菜单 / 数据范围</b>：以既有「采购订单」菜单授权与既有
/// <see cref="SalespersonDataScopeService"/> 数据范围口径驱动真实 <see cref="PurchaseInvoiceController"/>。</item>
/// <item><b>与请求形状无关</b>：<strong>空 <c>Request.Path</c></strong> 与<strong>已赋值 <c>Request.Path</c></strong>
/// 必须给出完全一致的判定；缺失 / 零 / 已删除身份一律未认证，禁用 / 撤销菜单一律权限不足，且都在任何敏感读取 / 写入之前。</item>
/// <item><b>保留既有业务生命周期与零写入拒绝</b>：既有授权账号照常完成 台账 / 新增 / 关联 / 登记 / 作废 全生命周期；
/// 被拒绝的请求不新增 / 不修改任何发票或关联行，也不新增任何用户授权。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class PurchaseInvoiceEntryAuthorizationSqlServerTests
    : IClassFixture<PurchaseInvoiceEntryAuthorizationSqlServerFixture>
{
    private readonly PurchaseInvoiceEntryAuthorizationSqlServerFixture _fixture;

    public PurchaseInvoiceEntryAuthorizationSqlServerTests(PurchaseInvoiceEntryAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    private const string RoutePath = "/api/purchase-invoices";

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(PurchaseInvoiceEntryAuthorizationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 控制器脚手架 ====================

    /// <summary>绑定真实登录身份的真实控制器；<paramref name="requestPath"/> 为 null 表示空路径请求。</summary>
    private static PurchaseInvoiceController NewController(ErpDbContext db, long? userId, string? requestPath)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        if (requestPath is not null) http.Request.Path = requestPath;
        return new PurchaseInvoiceController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    /// <summary>用一条独立连接执行控制器动作，返回（是否成功、错误文案）。</summary>
    private async Task<(bool Success, string Error)> TryAsync(
        long? userId, string? requestPath, Func<PurchaseInvoiceController, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await action(NewController(db, userId, requestPath));
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<int?> DeniedCodeAsync(
        long? userId, string? requestPath, Func<PurchaseInvoiceController, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await action(NewController(db, userId, requestPath));
            return null;
        }
        catch (BusinessException ex)
        {
            return ex.Code;
        }
    }

    private async Task<T?> TryOkAsync<T>(long? userId, string? requestPath,
        Func<PurchaseInvoiceController, Task<IActionResult>> action) where T : class
    {
        await using var db = _fixture.CreateDbContext();
        var result = await action(NewController(db, userId, requestPath));
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        return resp.Data;
    }

    // ==================== 种子助手（EF 生成身份主键，绝不清理既有行） ====================

    private static async Task<BaseSupplier> SeedSupplierAsync(ErpDbContext db)
    {
        var code = $"INT_PI462_SUP_{Guid.NewGuid():N}";
        var supplier = new BaseSupplier { SupplierCode = code, SupplierName = code, Status = 1 };
        db.BaseSuppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier;
    }

    private static async Task<BaseCustomer> SeedCustomerAsync(ErpDbContext db)
    {
        var code = $"INT_PI462_CUS_{Guid.NewGuid():N}";
        var customer = new BaseCustomer
        {
            CustomerCode = code, CustomerName = code, Status = 1, CreditStatus = "正常"
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<PurchaseOrder> SeedPurchaseOrderAsync(
        ErpDbContext db, long supplierId, long? owningCustomerId, decimal totalAmount = 1000m)
    {
        var order = new PurchaseOrder
        {
            OrderNo = $"INT_PI462_PO_{Guid.NewGuid():N}",
            OrderDate = DateTime.Today,
            SupplierId = supplierId,
            Currency = Currency.CNY,
            TotalAmount = totalAmount,
            Status = DocumentStatus.Approved,
            ArrivalProgress = "未到货",
            SettlementProgress = "未结算",
            OwningCustomerId = owningCustomerId
        };
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static async Task<PurchaseInvoice> SeedInvoiceAsync(
        ErpDbContext db, long supplierId, decimal gross, int status = PurchaseInvoiceRules.StatusDraft)
    {
        var number = $"INT_PI462_INV_{Guid.NewGuid():N}";
        var invoice = new PurchaseInvoice
        {
            InvoiceType = PurchaseInvoiceRules.InvoiceTypeOrdinary,
            InvoiceCode = string.Empty,
            InvoiceNumber = number,
            NormalizedInvoiceCode = PurchaseInvoiceRules.NormalizeIdentityPart(string.Empty),
            NormalizedInvoiceNumber = PurchaseInvoiceRules.NormalizeIdentityPart(number),
            InvoiceDate = DateTime.Today,
            SupplierId = supplierId,
            SupplierCode = "INT_PI462_SUP",
            SupplierName = "集成发票供应商",
            Currency = "CNY",
            NetAmount = gross,
            TaxAmount = 0m,
            GrossAmount = gross,
            Status = status
        };
        db.PurchaseInvoices.Add(invoice);
        await db.SaveChangesAsync();
        return invoice;
    }

    private static async Task SeedInvoiceAllocationAsync(
        ErpDbContext db, long invoiceId, PurchaseOrder order, decimal amount)
    {
        db.PurchaseInvoiceAllocations.Add(new PurchaseInvoiceAllocation
        {
            PurchaseInvoiceId = invoiceId,
            PurchaseOrderId = order.Id,
            OrderNo = order.OrderNo ?? string.Empty,
            OrderDate = order.OrderDate,
            OrderCurrency = "CNY",
            SupplierId = order.SupplierId,
            SupplierCode = "INT_PI462_SUP",
            SupplierName = "集成发票供应商",
            AllocatedAmount = amount,
            Currency = "CNY",
            SortOrder = 1
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 播种真实操作账号（既有「采购订单」菜单 + 既有销售员客户范围）：<paramref name="privileged"/> 为 true 时复用系统内置角色口径，
    /// 否则为映射到 <paramref name="inScopeCustomerId"/> 客户的受限业务员。绝不新增任何用户授权，只复用既有菜单编码。
    /// </summary>
    private static async Task<(long UserId, long RoleId)> SeedOperatorAsync(
        ErpDbContext db, long inScopeCustomerId, bool privileged = false, bool withMenu = true,
        UserStatus status = UserStatus.Enabled)
    {
        var code = $"pi462-op-{Guid.NewGuid():N}";
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = code, DisplayName = code, PasswordHash = "hash", PasswordSalt = "salt", Status = status
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "发票入口集成角色", RoleCode = $"Pi462Op-{Guid.NewGuid():N}", IsSystem = privileged
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (withMenu)
        {
            var menuId = await db.SysMenus.AsNoTracking()
                .Where(m => m.MenuCode == PurchaseInvoiceAuthorizationRules.RequiredMenuCode && !m.IsDeleted)
                .Select(m => m.Id)
                .FirstAsync();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menuId });
            await db.SaveChangesAsync();
        }

        var customer = await db.BaseCustomers.SingleAsync(c => c.Id == inScopeCustomerId);
        customer.EmpId = employee.Id;
        await db.SaveChangesAsync();
        return (user.Id, role.Id);
    }

    private static async Task RevokeMenuAsync(ErpDbContext db, long roleId)
    {
        foreach (var grant in await db.SysRoleMenus.Where(g => g.RoleId == roleId && !g.IsDeleted).ToListAsync())
            grant.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    private static async Task SoftDeleteUserAsync(ErpDbContext db, long userId)
    {
        var user = await db.SysUsers.SingleAsync(u => u.Id == userId);
        user.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    private async Task<PurchaseInvoice> ReloadInvoiceAsync(long invoiceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.PurchaseInvoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId);
    }

    private async Task<int> AllocationCountAsync(long invoiceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.PurchaseInvoiceAllocations.CountAsync(a => a.PurchaseInvoiceId == invoiceId && !a.IsDeleted);
    }

    private static PurchaseInvoiceSaveDto InvoiceDto(long supplierId, string? number = null, decimal gross = 100m)
        => new()
        {
            InvoiceType = PurchaseInvoiceRules.InvoiceTypeOrdinary,
            InvoiceNumber = number ?? $"INT462-{Guid.NewGuid():N}"[..20],
            InvoiceDate = DateTime.Today,
            SupplierId = supplierId,
            Currency = "CNY",
            NetAmount = gross,
            TaxAmount = 0m,
            GrossAmount = gross
        };

    private static PurchaseInvoiceAllocationSaveRequest Lines(params (long OrderId, decimal Amount)[] lines)
        => new()
        {
            Lines = lines.Select(l => new PurchaseInvoiceAllocationSaveDto
            {
                PurchaseOrderId = l.OrderId,
                AllocatedAmount = l.Amount
            }).ToList()
        };

    /// <summary>供应商采购发票的每一条公开路由都必须先授权后读写（覆盖 12 条入口）。</summary>
    private async Task AssertAllRoutesDeniedAsync(
        long? userId, string? requestPath, long invoiceId, long supplierId, int code)
    {
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.GetPaged(new PurchaseInvoiceQuery())));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.GetById(invoiceId)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath,
            c => c.Reconciliation(new SupplierInvoiceReconciliationQuery())));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath,
            c => c.ReconciliationAging(new SupplierReconciliationAgingQuery())));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.ReconciliationAgingInvoiceDetail(invoiceId)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.OrderCandidates(invoiceId, null, 10)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath,
            c => c.PreviewAllocations(invoiceId, Lines((1L, 10m)))));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath,
            c => c.SaveAllocations(invoiceId, Lines((1L, 10m)))));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.Create(InvoiceDto(supplierId))));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.Update(invoiceId, InvoiceDto(supplierId))));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.Record(invoiceId)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath,
            c => c.Void(invoiceId, new PurchaseInvoiceVoidRequest { Reason = "重开" })));
    }

    // ==================== 1. 与请求形状无关：空路径与已赋值路径判定一致 ====================

    [Fact]
    public async Task Empty_and_populated_request_paths_enforce_identical_authority()
    {
        Guard();
        long invoiceId, ownerId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var supplier = await SeedSupplierAsync(seed);
            var customer = await SeedCustomerAsync(seed);
            var owner = await SeedOperatorAsync(seed, customer.Id, privileged: true);
            var order = await SeedPurchaseOrderAsync(seed, supplier.Id, customer.Id);
            var invoice = await SeedInvoiceAsync(seed, supplier.Id, 100m);
            await SeedInvoiceAllocationAsync(seed, invoice.Id, order, 40m);
            invoiceId = invoice.Id;
            ownerId = owner.UserId;
        }

        // 授权身份：空路径与已赋值路径都必须可读同一张有来源发票
        var emptyRead = await TryAsync(ownerId, null, c => c.GetById(invoiceId));
        var populatedRead = await TryAsync(ownerId, RoutePath, c => c.GetById(invoiceId));
        Assert.True(emptyRead.Success, emptyRead.Error);
        Assert.True(populatedRead.Success, populatedRead.Error);

        // 无身份：空路径与已赋值路径都一律未认证（空路径不再是请求形状旁路）
        Assert.Equal(ErrorCodes.Unauthorized, await DeniedCodeAsync(null, null, c => c.GetById(invoiceId)));
        Assert.Equal(ErrorCodes.Unauthorized, await DeniedCodeAsync(null, RoutePath, c => c.GetById(invoiceId)));
        Assert.Equal(ErrorCodes.Unauthorized, await DeniedCodeAsync(null, null, c => c.GetPaged(new PurchaseInvoiceQuery())));
        Assert.Equal(ErrorCodes.Unauthorized, await DeniedCodeAsync(null, RoutePath, c => c.GetPaged(new PurchaseInvoiceQuery())));

        Assert.Equal(PurchaseInvoiceRules.StatusDraft, (await ReloadInvoiceAsync(invoiceId)).Status);
    }

    // ==================== 2. 缺失 / 零 / 未知 / 已删除身份：任何路由、任何请求形状一律未认证 ====================

    [Fact]
    public async Task Missing_zero_unknown_and_deleted_identity_are_unauthorized_on_every_route()
    {
        Guard();
        long invoiceId, supplierId, ownerId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var supplier = await SeedSupplierAsync(seed);
            var customer = await SeedCustomerAsync(seed);
            var owner = await SeedOperatorAsync(seed, customer.Id, privileged: true);
            var order = await SeedPurchaseOrderAsync(seed, supplier.Id, customer.Id);
            var invoice = await SeedInvoiceAsync(seed, supplier.Id, 100m);
            await SeedInvoiceAllocationAsync(seed, invoice.Id, order, 40m);
            invoiceId = invoice.Id;
            supplierId = supplier.Id;
            ownerId = owner.UserId;
        }

        foreach (var path in new string?[] { null, RoutePath })
        {
            await AssertAllRoutesDeniedAsync(null, path, invoiceId, supplierId, ErrorCodes.Unauthorized);
            await AssertAllRoutesDeniedAsync(0L, path, invoiceId, supplierId, ErrorCodes.Unauthorized);
            await AssertAllRoutesDeniedAsync(987654321L, path, invoiceId, supplierId, ErrorCodes.Unauthorized);
        }

        // 已删除账号（真实软删除）同样按未认证拒绝
        await using (var remove = _fixture.CreateDbContext())
            await SoftDeleteUserAsync(remove, ownerId);
        foreach (var path in new string?[] { null, RoutePath })
            await AssertAllRoutesDeniedAsync(ownerId, path, invoiceId, supplierId, ErrorCodes.Unauthorized);

        // 被拒绝的请求零写入：发票状态与关联行原样保留
        Assert.Equal(PurchaseInvoiceRules.StatusDraft, (await ReloadInvoiceAsync(invoiceId)).Status);
        Assert.Equal(1, await AllocationCountAsync(invoiceId));
    }

    // ==================== 3. 禁用账号 / 撤销菜单：任何路由、任何请求形状一律权限不足 ====================

    [Fact]
    public async Task Disabled_identity_and_revoked_menu_are_forbidden_on_every_route()
    {
        Guard();
        long invoiceId, supplierId, disabledUserId, ownerId;
        long ownerRoleId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var supplier = await SeedSupplierAsync(seed);
            var customer = await SeedCustomerAsync(seed);
            var owner = await SeedOperatorAsync(seed, customer.Id, privileged: true);
            var disabled = await SeedOperatorAsync(seed, customer.Id, status: UserStatus.Disabled);
            var order = await SeedPurchaseOrderAsync(seed, supplier.Id, customer.Id);
            var invoice = await SeedInvoiceAsync(seed, supplier.Id, 100m);
            await SeedInvoiceAllocationAsync(seed, invoice.Id, order, 40m);
            invoiceId = invoice.Id;
            supplierId = supplier.Id;
            disabledUserId = disabled.UserId;
            ownerId = owner.UserId;
            ownerRoleId = owner.RoleId;
        }

        foreach (var path in new string?[] { null, RoutePath })
            await AssertAllRoutesDeniedAsync(disabledUserId, path, invoiceId, supplierId, ErrorCodes.Forbidden);

        // 既有授权仍在：空路径请求可读
        Assert.True((await TryAsync(ownerId, null, c => c.GetById(invoiceId))).Success);

        // 撤销既有菜单授权后立即收敛（同一控制器实例、空路径与已赋值路径一律权限不足）
        await using (var revoke = _fixture.CreateDbContext())
            await RevokeMenuAsync(revoke, ownerRoleId);
        foreach (var path in new string?[] { null, RoutePath })
            await AssertAllRoutesDeniedAsync(ownerId, path, invoiceId, supplierId, ErrorCodes.Forbidden);

        // 撤销后零写入：发票仍为草稿、关联行原样保留
        Assert.Equal(PurchaseInvoiceRules.StatusDraft, (await ReloadInvoiceAsync(invoiceId)).Status);
        Assert.Equal(1, await AllocationCountAsync(invoiceId));
    }

    // ==================== 4. 保留既有业务生命周期：既有授权身份在空 / 已赋值路径上照常完成全流程 ====================

    [Fact]
    public async Task Permitted_genuine_identity_completes_the_lifecycle_on_both_request_shapes()
    {
        Guard();
        long supplierId, orderId, ownerId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var supplier = await SeedSupplierAsync(seed);
            var customer = await SeedCustomerAsync(seed);
            var owner = await SeedOperatorAsync(seed, customer.Id, privileged: true);
            var order = await SeedPurchaseOrderAsync(seed, supplier.Id, customer.Id);
            supplierId = supplier.Id;
            orderId = order.Id;
            ownerId = owner.UserId;
        }

        // 新增（空路径）→ 关联（已赋值路径）→ 登记（空路径）→ 作废（已赋值路径）
        var created = await TryOkAsync<PurchaseInvoiceDto>(ownerId, null,
            c => c.Create(InvoiceDto(supplierId, gross: 200m)));
        Assert.NotNull(created);
        Assert.Equal(PurchaseInvoiceRules.StatusDraft, created!.Status);

        var allocated = await TryOkAsync<PurchaseInvoiceDto>(ownerId, RoutePath,
            c => c.SaveAllocations(created.Id, Lines((orderId, 120m))));
        Assert.NotNull(allocated);
        Assert.Equal(120m, allocated!.LinkedAmount);
        Assert.Single(allocated.Allocations);

        var recorded = await TryOkAsync<PurchaseInvoiceDto>(ownerId, null, c => c.Record(created.Id));
        Assert.NotNull(recorded!.RecordedAt);

        var voided = await TryOkAsync<PurchaseInvoiceDto>(ownerId, RoutePath,
            c => c.Void(created.Id, new PurchaseInvoiceVoidRequest { Reason = "录错单价" }));
        Assert.Equal(PurchaseInvoiceRules.StatusVoided, voided!.Status);

        // 既有商业口径不变：作废原因 / 冻结审计 / 含税总额 / 关联行全部保留
        var stored = await ReloadInvoiceAsync(created.Id);
        Assert.Equal(PurchaseInvoiceRules.StatusVoided, stored.Status);
        Assert.Equal("录错单价", stored.VoidReason);
        Assert.Equal(recorded.RecordedAt, stored.RecordedAt);
        Assert.Equal(200m, stored.GrossAmount);
        Assert.Equal(1, await AllocationCountAsync(created.Id));
    }

    // ==================== 5. 被拒绝的调用零写入、零授权扩张 ====================

    [Fact]
    public async Task Denied_operations_persist_zero_rows_and_grant_nothing()
    {
        Guard();
        long foreignInvoiceId, ownerId, grantCountBefore;
        await using (var seed = _fixture.CreateDbContext())
        {
            var supplier = await SeedSupplierAsync(seed);
            var ownerCustomer = await SeedCustomerAsync(seed);
            var foreignCustomer = await SeedCustomerAsync(seed);
            var owner = await SeedOperatorAsync(seed, ownerCustomer.Id);
            var foreignOrder = await SeedPurchaseOrderAsync(seed, supplier.Id, foreignCustomer.Id);
            var foreignInvoice = await SeedInvoiceAsync(seed, supplier.Id, 100m, PurchaseInvoiceRules.StatusRecorded);
            await SeedInvoiceAllocationAsync(seed, foreignInvoice.Id, foreignOrder, 40m);
            foreignInvoiceId = foreignInvoice.Id;
            ownerId = owner.UserId;
        }

        await using (var before = _fixture.CreateDbContext())
            grantCountBefore = await before.SysRoleMenus.CountAsync(g => !g.IsDeleted);

        // 范围外来源：读取 / 登记 / 作废 / 关联全部 fail closed（非披露，一律权限不足）
        Assert.Equal(ErrorCodes.Forbidden, await DeniedCodeAsync(ownerId, null, c => c.GetById(foreignInvoiceId)));
        Assert.Equal(ErrorCodes.Forbidden, await DeniedCodeAsync(ownerId, null, c => c.Record(foreignInvoiceId)));
        Assert.Equal(ErrorCodes.Forbidden, await DeniedCodeAsync(ownerId, null,
            c => c.Void(foreignInvoiceId, new PurchaseInvoiceVoidRequest { Reason = "重开" })));
        Assert.Equal(ErrorCodes.Forbidden, await DeniedCodeAsync(ownerId, RoutePath,
            c => c.SaveAllocations(foreignInvoiceId, Lines((1L, 10m)))));

        // 零写入：范围外发票仍为已登记、无作废原因、关联行原样保留；零授权扩张
        var stored = await ReloadInvoiceAsync(foreignInvoiceId);
        Assert.Equal(PurchaseInvoiceRules.StatusRecorded, stored.Status);
        Assert.True(string.IsNullOrEmpty(stored.VoidReason));
        Assert.Equal(1, await AllocationCountAsync(foreignInvoiceId));
        await using (var after = _fixture.CreateDbContext())
            Assert.Equal(grantCountBefore, await after.SysRoleMenus.CountAsync(g => !g.IsDeleted));
    }
}

/// <summary>
/// ERP-462 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class PurchaseInvoiceEntryAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_PURCHASEINVOICEENTRYAUTHORIZATION";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-462] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await CreateFreshDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};" +
           "Integrated Security=true;TrustServerCertificate=true;";

    internal static void AssertDedicatedTarget(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var server = builder.DataSource ?? string.Empty;
        var database = builder.InitialCatalog ?? string.Empty;

        Assert.Equal($"(localdb)\\{InstanceMarker}", server, ignoreCase: true);
        Assert.StartsWith(DatabasePrefix, database, StringComparison.OrdinalIgnoreCase);
        Assert.True(builder.IntegratedSecurity);
    }

    private async Task CreateFreshDatabaseAsync()
    {
        var database = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;

        // 任何库访问 / 建库之前再次护栏：绝不使用生产或非专用目标。
        AssertDedicatedTarget(ConnectionString);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // Never destroy a pre-existing fixture or another caller's database.
            cmd.CommandText = "SELECT DB_ID(@database)";
            cmd.Parameters.AddWithValue("@database", database);
            var existing = await cmd.ExecuteScalarAsync();
            if (existing is not null && existing != DBNull.Value)
                throw new InvalidOperationException(
                    "The isolated fixture database already exists; choose a fresh NEWERP_AUTOTEST database.");
        }

        await using (var db = CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await SchemaUpgrader.EnsureUpgradedAsync(db);
            await SeedData.InitializeAsync(db);
            await SchemaUpgrader.EnsureUpgradedAsync(db);
        }

        Console.WriteLine("[ERP-462] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }
}

/// <summary>专用目标护栏的 fail-closed 单元覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class PurchaseInvoiceEntryAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => PurchaseInvoiceEntryAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}
