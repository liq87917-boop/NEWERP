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
/// ERP-488「付款单 → 供应商采购发票」付款发票引用（分摊）证据入口（
/// <c>api/supplier-payment-invoice-allocations</c>）实时授权 SQL Server 集成测试（专用 NEWERP_AUTOTEST 护栏）。
/// <list type="number">
/// <item><b>真实控制器 + 真实既有身份 / 菜单 / 数据范围</b>：以既有「付款单」（<c>payment</c>）菜单授权与既有
/// <see cref="SalespersonDataScopeService"/> 数据范围口径驱动真实
/// <see cref="SupplierPaymentInvoiceAllocationController"/>。</item>
/// <item><b>与请求形状无关</b>：空请求路径与已赋值请求路径必须给出完全一致的判定；绑定到请求管线的缺失 / 零 / 未知 /
/// 已删除身份一律未认证，禁用 / 撤销菜单一律权限不足，且都在任何计数、候选、快照与写入之前。</item>
/// <item><b>无空上下文旁路</b>：未绑定任何请求（<c>HttpContext</c> 为 null）时登记 / 作废同样 fail closed。</item>
/// <item><b>保留既有业务生命周期与零写入拒绝</b>：既有授权账号照常完成台账 / 候选 / 汇总 / 清单 / 详情 / 登记 / 作废
/// 全生命周期；被拒绝的请求不新增 / 不改写任何引用行、付款单、发票或状态，也不新增任何用户授权。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class SupplierPaymentInvoiceEntryAuthorizationSqlServerTests
    : IClassFixture<SupplierPaymentInvoiceEntryAuthorizationSqlServerFixture>
{
    private const string AllocationPath = "/api/supplier-payment-invoice-allocations";

    private readonly SupplierPaymentInvoiceEntryAuthorizationSqlServerFixture _fixture;

    public SupplierPaymentInvoiceEntryAuthorizationSqlServerTests(
        SupplierPaymentInvoiceEntryAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal($"(localdb)\\{SupplierPaymentInvoiceEntryAuthorizationSqlServerFixture.InstanceMarker}",
            target.DataSource, ignoreCase: true);
        Assert.StartsWith(SupplierPaymentInvoiceEntryAuthorizationSqlServerFixture.DatabasePrefix,
            target.InitialCatalog, StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 控制器脚手架 ====================

    private static DefaultHttpContext NewHttp(long? userId, string? requestPath)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "IntTest")) };
        if (requestPath is not null) http.Request.Path = requestPath;
        return http;
    }

    private static SupplierPaymentInvoiceAllocationController AllocationController(
        ErpDbContext db, long? userId, string? requestPath)
        => new(db)
        {
            ControllerContext = new ControllerContext { HttpContext = NewHttp(userId, requestPath) }
        };

    /// <summary>未绑定任何请求上下文的纯进程内直调（外部请求无法构造）。</summary>
    private static SupplierPaymentInvoiceAllocationController InProcessController(ErpDbContext db) => new(db);

    private static async Task<(bool Success, int Code, string Error)> TryAllocationAsync(
        ErpDbContext db, long? userId, string? requestPath,
        Func<SupplierPaymentInvoiceAllocationController, Task<IActionResult>> action)
        => await TryAllocationAsync(AllocationController(db, userId, requestPath), action);

    private static async Task<(bool Success, int Code, string Error)> TryAllocationAsync(
        SupplierPaymentInvoiceAllocationController controller,
        Func<SupplierPaymentInvoiceAllocationController, Task<IActionResult>> action)
    {
        try
        {
            var result = await action(controller);
            return (result is OkObjectResult, ErrorCodes.Success, string.Empty);
        }
        catch (BusinessException ex)
        {
            return (false, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return (false, -1, ex.ToString());
        }
    }

    /// <summary>断言成功响应并取出数据体（业务码必须为 0）。</summary>
    private static T Data<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, response.Code);
        return response.Data!;
    }

    private static SupplierPaymentInvoiceAllocationSaveDto InvoiceDto(long paymentId, long invoiceId, decimal amount)
        => new() { PaymentId = paymentId, PurchaseInvoiceId = invoiceId, AllocatedAmount = amount, Remark = "INT488" };

    /// <summary>付款发票引用 10 条入口在同一身份 / 请求形状下的判决码必须一致。</summary>
    private async Task AssertAllRoutesAsync(
        ErpDbContext db, long? userId, string? requestPath, long paymentId, long invoiceId, int code)
    {
        Assert.Equal(code, (await TryAllocationAsync(db, userId, requestPath,
            c => c.GetPaged(new SupplierPaymentInvoiceAllocationQuery()))).Code);
        Assert.Equal(code, (await TryAllocationAsync(db, userId, requestPath,
            c => c.PaymentCandidates(null, null, 10))).Code);
        Assert.Equal(code, (await TryAllocationAsync(db, userId, requestPath,
            c => c.PaymentSummary(paymentId))).Code);
        Assert.Equal(code, (await TryAllocationAsync(db, userId, requestPath,
            c => c.AllocationsForPayment(paymentId))).Code);
        Assert.Equal(code, (await TryAllocationAsync(db, userId, requestPath,
            c => c.InvoiceCandidates(paymentId, null, 10))).Code);
        Assert.Equal(code, (await TryAllocationAsync(db, userId, requestPath,
            c => c.InvoiceSummary(invoiceId))).Code);
        Assert.Equal(code, (await TryAllocationAsync(db, userId, requestPath,
            c => c.AllocationsForInvoice(invoiceId))).Code);
        Assert.Equal(code, (await TryAllocationAsync(db, userId, requestPath,
            c => c.GetById(1L))).Code);
        Assert.Equal(code, (await TryAllocationAsync(db, userId, requestPath,
            c => c.Create(InvoiceDto(paymentId, invoiceId, 10m)))).Code);
        Assert.Equal(code, (await TryAllocationAsync(db, userId, requestPath,
            c => c.Void(1L, new SupplierPaymentInvoiceAllocationVoidRequest { Reason = "越权" }))).Code);
    }

    // ==================== 种子助手（EF 生成身份主键，绝不清理既有行） ====================

    private static async Task<long> SeedSupplierAsync(ErpDbContext db, string tag)
    {
        // BaseSuppliers.SupplierCode 上限 50：标签 + 32 位 GUID 长度恒 ≤ 45，绝不越界。
        var code = $"IS488S{tag}{Guid.NewGuid():N}";
        var supplier = new BaseSupplier { SupplierCode = code, SupplierName = code, Status = 1 };
        db.BaseSuppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier.Id;
    }

    private static async Task<BaseCustomer> SeedCustomerAsync(ErpDbContext db, string tag)
    {
        var code = $"IC488C{tag}{Guid.NewGuid():N}";
        var customer = new BaseCustomer
        {
            CustomerCode = code, CustomerName = code, Status = 1, CreditStatus = "正常"
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<FinancePaymentApply> SeedPaymentApplyAsync(ErpDbContext db, long customerId)
    {
        var apply = new FinancePaymentApply
        {
            ApplyNo = $"IA488-{Guid.NewGuid():N}"[..24],
            ApplyDate = DateTime.Today,
            CustomerId = customerId,
            Amount = 1000m,
            Currency = Currency.CNY,
            Status = DocumentStatus.Approved
        };
        db.FinancePaymentApplies.Add(apply);
        await db.SaveChangesAsync();
        return apply;
    }

    private static async Task<FinancePayment> SeedPaymentAsync(
        ErpDbContext db, long supplierId, long? paymentApplyId, decimal amount = 1000m)
    {
        var payment = new FinancePayment
        {
            PaymentNo = $"IP488-{Guid.NewGuid():N}"[..24],
            PaymentDate = DateTime.Today,
            SupplierId = supplierId,
            PaymentApplyId = paymentApplyId,
            Amount = amount,
            Currency = Currency.CNY,
            PaymentMethod = PaymentMethod.BankTransfer,
            Status = DocumentStatus.Approved
        };
        db.FinancePayments.Add(payment);
        await db.SaveChangesAsync();
        return payment;
    }

    private static async Task<PurchaseInvoice> SeedInvoiceAsync(
        ErpDbContext db, long supplierId, decimal grossAmount = 1000m)
    {
        var number = $"II488-{Guid.NewGuid():N}"[..24];
        var invoice = new PurchaseInvoice
        {
            InvoiceType = "普票",
            InvoiceCode = string.Empty,
            InvoiceNumber = number,
            NormalizedInvoiceCode = PurchaseInvoiceRules.NormalizeIdentityPart(string.Empty),
            NormalizedInvoiceNumber = PurchaseInvoiceRules.NormalizeIdentityPart(number),
            InvoiceDate = DateTime.Today,
            SupplierId = supplierId,
            SupplierCode = "IS488",
            SupplierName = "集成测试供应商",
            Currency = "CNY",
            NetAmount = grossAmount,
            TaxAmount = 0m,
            GrossAmount = grossAmount,
            Status = PurchaseInvoiceRules.StatusRecorded
        };
        db.PurchaseInvoices.Add(invoice);
        await db.SaveChangesAsync();
        return invoice;
    }

    /// <summary>
    /// 播种真实操作账号并授予既有「付款单」（<c>payment</c>）菜单：<paramref name="ownedCustomerId"/> 非空时为映射到该客户的
    /// 受限业务员；否则复用系统内置特权角色口径。绝不新增任何菜单，只复用既有菜单编码。
    /// </summary>
    private static async Task<(long UserId, long RoleId)> SeedOperatorAsync(
        ErpDbContext db, long? ownedCustomerId = null, bool paymentMenu = true,
        bool privileged = true, UserStatus status = UserStatus.Enabled)
    {
        var code = $"is488-op-{Guid.NewGuid():N}";
        long? employeeId = null;
        if (ownedCustomerId is not null)
        {
            var employee = new BaseEmployee
            {
                EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1
            };
            db.BaseEmployees.Add(employee);
            await db.SaveChangesAsync();
            employeeId = employee.Id;
        }

        var user = new SysUser
        {
            UserName = code, DisplayName = code, PasswordHash = "hash", PasswordSalt = "salt", Status = status
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "付款发票引用入口集成角色", RoleCode = $"Is488Op-{Guid.NewGuid():N}", IsSystem = privileged
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (paymentMenu)
        {
            var menuId = await db.SysMenus.AsNoTracking()
                .Where(m => m.MenuCode == SupplierPaymentLifecycleRules.RequiredMenuCode && !m.IsDeleted)
                .Select(m => m.Id)
                .FirstAsync();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menuId });
            await db.SaveChangesAsync();
        }

        if (ownedCustomerId is not null && employeeId is not null)
        {
            var customer = await db.BaseCustomers.SingleAsync(c => c.Id == ownedCustomerId.Value);
            customer.EmpId = employeeId;
            await db.SaveChangesAsync();
        }

        return (user.Id, role.Id);
    }

    private static async Task RevokeMenuAsync(ErpDbContext db, long roleId, string menuCode)
    {
        var menuIds = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == menuCode).Select(m => m.Id).ToListAsync();
        foreach (var grant in await db.SysRoleMenus
                     .Where(g => !g.IsDeleted && g.RoleId == roleId && menuIds.Contains(g.MenuId)).ToListAsync())
            grant.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    private static async Task SoftDeleteUserAsync(ErpDbContext db, long userId)
    {
        var user = await db.SysUsers.SingleAsync(u => u.Id == userId);
        user.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    // ==================== 1. 与请求形状无关：空路径与已赋值路径判定一致（含空上下文 fail closed） ====================

    [Fact]
    public async Task Empty_and_populated_request_paths_enforce_identical_authority()
    {
        Guard();
        long paymentId, invoiceId, privilegedId, disabledId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var supplierId = await SeedSupplierAsync(seed, "PARITY");
            var customer = await SeedCustomerAsync(seed, "PARITY");
            var apply = await SeedPaymentApplyAsync(seed, customer.Id);
            paymentId = (await SeedPaymentAsync(seed, supplierId, apply.Id)).Id;
            invoiceId = (await SeedInvoiceAsync(seed, supplierId)).Id;
            privilegedId = (await SeedOperatorAsync(seed)).UserId;
            disabledId = (await SeedOperatorAsync(seed, status: UserStatus.Disabled)).UserId;
        }

        foreach (var path in new[] { null, AllocationPath })
        {
            await using var db = _fixture.CreateDbContext();
            Assert.Equal(ErrorCodes.Unauthorized,
                (await TryAllocationAsync(db, null, path, c => c.PaymentSummary(paymentId))).Code);
            Assert.Equal(ErrorCodes.Forbidden,
                (await TryAllocationAsync(db, disabledId, path, c => c.PaymentSummary(paymentId))).Code);
            var ok = await TryAllocationAsync(db, privilegedId, path, c => c.PaymentSummary(paymentId));
            Assert.True(ok.Success, ok.Error);
        }

        // 纯进程内（无 HttpContext）：登记 / 作废同样 fail closed（不再有无授权服务直调旁路）。
        await using (var db = _fixture.CreateDbContext())
        {
            var inProcess = InProcessController(db);
            Assert.Equal(ErrorCodes.Unauthorized,
                (await TryAllocationAsync(inProcess, c => c.Create(InvoiceDto(paymentId, invoiceId, 10m)))).Code);
            Assert.Equal(ErrorCodes.Unauthorized,
                (await TryAllocationAsync(inProcess,
                    c => c.Void(1L, new SupplierPaymentInvoiceAllocationVoidRequest { Reason = "越权" }))).Code);
            // 被拒调用零写入：本次付款单没有新增任何引用行。
            Assert.Equal(0, await db.SupplierPaymentInvoiceAllocations
                .CountAsync(a => a.PaymentId == paymentId));
        }
    }

    // ==================== 2. 缺 / 零 / 未知 / 已删除身份：任何请求形状一律未认证 ====================

    [Fact]
    public async Task Missing_zero_unknown_and_deleted_identity_are_unauthorized_on_every_route()
    {
        Guard();
        long paymentId, invoiceId, deletedId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var supplierId = await SeedSupplierAsync(seed, "NOID");
            var customer = await SeedCustomerAsync(seed, "NOID");
            var apply = await SeedPaymentApplyAsync(seed, customer.Id);
            paymentId = (await SeedPaymentAsync(seed, supplierId, apply.Id)).Id;
            invoiceId = (await SeedInvoiceAsync(seed, supplierId)).Id;
            deletedId = (await SeedOperatorAsync(seed)).UserId;
            await SoftDeleteUserAsync(seed, deletedId);
        }

        await using var db = _fixture.CreateDbContext();
        foreach (var path in new[] { null, AllocationPath })
            foreach (long? identity in new long?[] { null, 0L, 987_654_321L, deletedId })
                await AssertAllRoutesAsync(db, identity, path, paymentId, invoiceId, ErrorCodes.Unauthorized);
    }

    // ==================== 3. 禁用身份 / 撤销菜单：任何请求形状一律权限不足 ====================

    [Fact]
    public async Task Disabled_identity_and_revoked_payment_menu_are_forbidden_on_every_route()
    {
        Guard();
        long paymentId, invoiceId, disabledId, revokedId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var supplierId = await SeedSupplierAsync(seed, "FORBID");
            var customer = await SeedCustomerAsync(seed, "FORBID");
            var apply = await SeedPaymentApplyAsync(seed, customer.Id);
            paymentId = (await SeedPaymentAsync(seed, supplierId, apply.Id)).Id;
            invoiceId = (await SeedInvoiceAsync(seed, supplierId)).Id;
            disabledId = (await SeedOperatorAsync(seed, status: UserStatus.Disabled)).UserId;

            // 特权账号撤销既有「付款单」菜单 → 权限不足（付款发票引用不承认任何菜单豁免）。
            var (owner, roleId) = await SeedOperatorAsync(seed);
            await RevokeMenuAsync(seed, roleId, SupplierPaymentLifecycleRules.RequiredMenuCode);
            revokedId = owner;
        }

        await using var db = _fixture.CreateDbContext();
        foreach (var path in new[] { null, AllocationPath })
        {
            await AssertAllRoutesAsync(db, disabledId, path, paymentId, invoiceId, ErrorCodes.Forbidden);
            await AssertAllRoutesAsync(db, revokedId, path, paymentId, invoiceId, ErrorCodes.Forbidden);
        }
    }

    // ==================== 4. 授权身份：两种请求形状都完成既有登记 / 作废生命周期 ====================

    [Fact]
    public async Task Permitted_genuine_identity_completes_the_lifecycle_on_both_request_shapes()
    {
        Guard();
        long paymentId, invoiceAId, invoiceBId, privilegedId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var supplierId = await SeedSupplierAsync(seed, "OWN");
            var customer = await SeedCustomerAsync(seed, "OWN");
            var apply = await SeedPaymentApplyAsync(seed, customer.Id);
            paymentId = (await SeedPaymentAsync(seed, supplierId, apply.Id, amount: 1000m)).Id;
            invoiceAId = (await SeedInvoiceAsync(seed, supplierId)).Id;
            invoiceBId = (await SeedInvoiceAsync(seed, supplierId)).Id;
            privilegedId = (await SeedOperatorAsync(seed)).UserId;
        }

        var invoiceIds = new[] { invoiceAId, invoiceBId };
        var index = 0;
        foreach (var path in new[] { null, AllocationPath })
        {
            await using var db = _fixture.CreateDbContext();
            var ctl = AllocationController(db, privilegedId, path);

            var created = Data<SupplierPaymentInvoiceAllocationDto>(
                await ctl.Create(InvoiceDto(paymentId, invoiceIds[index], 100m)));
            Assert.Equal(SupplierPaymentInvoiceAllocationRules.StatusActive, created.Status);
            Assert.Equal(100m, created.AllocatedAmount);
            Assert.Equal(paymentId, created.PaymentId);

            var voided = Data<SupplierPaymentInvoiceAllocationDto>(await ctl.Void(created.Id,
                new SupplierPaymentInvoiceAllocationVoidRequest { Reason = "录错" }));
            Assert.Equal(SupplierPaymentInvoiceAllocationRules.StatusVoided, voided.Status);
            Assert.Equal(100m, voided.AllocatedAmount);

            index++;
        }

        await using (var verify = _fixture.CreateDbContext())
        {
            var rows = await verify.SupplierPaymentInvoiceAllocations.AsNoTracking()
                .Where(a => a.PaymentId == paymentId).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal(100m, r.AllocatedAmount));
            Assert.All(rows, r => Assert.Equal(SupplierPaymentInvoiceAllocationRules.StatusVoided, r.Status));
            Assert.Equal(PurchaseInvoiceRules.StatusRecorded, await verify.PurchaseInvoices.AsNoTracking()
                .Where(i => i.Id == invoiceAId).Select(i => i.Status).SingleAsync());
            Assert.Equal(1000m, await verify.PurchaseInvoices.AsNoTracking()
                .Where(i => i.Id == invoiceAId).Select(i => i.GrossAmount).SingleAsync());
        }
    }

    // ==================== 5. 被拒调用零写入且绝不新增授权 ====================

    [Fact]
    public async Task Denied_operations_persist_zero_rows_and_grant_nothing()
    {
        Guard();
        long paymentId, invoiceId, deletedId;
        int usersBefore, rolesBefore, menusBefore, grantsBefore, allocationsBefore;
        await using (var seed = _fixture.CreateDbContext())
        {
            var supplierId = await SeedSupplierAsync(seed, "ZERO");
            var customer = await SeedCustomerAsync(seed, "ZERO");
            var apply = await SeedPaymentApplyAsync(seed, customer.Id);
            paymentId = (await SeedPaymentAsync(seed, supplierId, apply.Id)).Id;
            invoiceId = (await SeedInvoiceAsync(seed, supplierId)).Id;
            deletedId = (await SeedOperatorAsync(seed)).UserId;
            await SoftDeleteUserAsync(seed, deletedId);

            usersBefore = await seed.SysUsers.CountAsync();
            rolesBefore = await seed.SysRoles.CountAsync();
            menusBefore = await seed.SysMenus.CountAsync();
            grantsBefore = await seed.SysRoleMenus.CountAsync();
            allocationsBefore = await seed.SupplierPaymentInvoiceAllocations.CountAsync();
        }

        await using (var db = _fixture.CreateDbContext())
        {
            foreach (long? identity in new long?[] { null, 0L, 987_654_321L, deletedId })
            {
                await AssertAllRoutesAsync(db, identity, null, paymentId, invoiceId, ErrorCodes.Unauthorized);
                await AssertAllRoutesAsync(db, identity, AllocationPath, paymentId, invoiceId, ErrorCodes.Unauthorized);
            }

            var inProcess = InProcessController(db);
            Assert.Equal(ErrorCodes.Unauthorized,
                (await TryAllocationAsync(inProcess, c => c.Create(InvoiceDto(paymentId, invoiceId, 10m)))).Code);
        }

        // 零写入：引用行 / 身份 / 角色 / 菜单授权行数全部不变，付款单与发票保持原值。
        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(usersBefore, await verify.SysUsers.CountAsync());
            Assert.Equal(rolesBefore, await verify.SysRoles.CountAsync());
            Assert.Equal(menusBefore, await verify.SysMenus.CountAsync());
            Assert.Equal(grantsBefore, await verify.SysRoleMenus.CountAsync());
            Assert.Equal(allocationsBefore, await verify.SupplierPaymentInvoiceAllocations.CountAsync());
            Assert.Equal(1000m, await verify.FinancePayments.AsNoTracking()
                .Where(p => p.Id == paymentId).Select(p => p.Amount).SingleAsync());
            Assert.Equal(PurchaseInvoiceRules.StatusRecorded, await verify.PurchaseInvoices.AsNoTracking()
                .Where(i => i.Id == invoiceId).Select(i => i.Status).SingleAsync());
        }
    }
}

/// <summary>
/// ERP-488 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class SupplierPaymentInvoiceEntryAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_SUPPLIERPAYMENTINVOICEENTRYAUTHORIZATION";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-488] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-488] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }
}

/// <summary>专用目标护栏的 fail-closed 单元覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class SupplierPaymentInvoiceEntryAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => SupplierPaymentInvoiceEntryAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}
