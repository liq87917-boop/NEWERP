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
/// ERP-487 客户销项发票 / 「收款单 → 客户销项发票证据」收款分摊证据入口实时授权 SQL Server 集成测试
/// （专用 NEWERP_AUTOTEST 护栏）。
/// <list type="number">
/// <item><b>真实控制器 + 真实既有身份 / 菜单 / 数据范围</b>：以既有「销售订单」（sales-order）/「收款单」（receipt）
/// 菜单授权与既有 <see cref="SalespersonDataScopeService"/> 数据范围口径驱动真实
/// <see cref="CustomerSalesInvoiceEvidenceController"/> /
/// <see cref="CustomerSalesInvoiceCollectionAllocationController"/>。</item>
/// <item><b>与请求形状无关</b>：空请求路径与已赋值请求路径必须给出完全一致的判定；
/// 绑定到请求管线的缺失 / 零 / 未知 / 已删除身份一律未认证，禁用 / 撤销菜单一律权限不足，
/// 且都在任何计数、候选、快照、预览与写入之前。</item>
/// <item><b>收款分摊证据无空上下文旁路</b>：未绑定任何请求（<c>HttpContext</c> 为 null）时登记 / 作废同样 fail closed；
/// 客户销项发票入口保留仓库一致的纯进程内直调边界（外部请求无法构造，纯单元测试已覆盖）。</item>
/// <item><b>保留既有业务生命周期与零写入拒绝</b>：既有授权账号照常完成 台账 / 详情 / 候选 / 登记 / 作废 与
/// 收款分摊 登记 / 作废 全生命周期；被拒绝的请求不新增 / 不改写任何发票、分摊行、收款单或状态，
/// 也不新增任何用户授权。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class CustomerSalesInvoiceEntryAuthorizationSqlServerTests
    : IClassFixture<CustomerSalesInvoiceEntryAuthorizationSqlServerFixture>
{
    private const string EvidencePath = "/api/customer-sales-invoices";
    private const string AllocationPath = "/api/customer-sales-invoice-collection-allocations";

    private readonly CustomerSalesInvoiceEntryAuthorizationSqlServerFixture _fixture;

    public CustomerSalesInvoiceEntryAuthorizationSqlServerTests(
        CustomerSalesInvoiceEntryAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal($"(localdb)\\{CustomerSalesInvoiceEntryAuthorizationSqlServerFixture.InstanceMarker}",
            target.DataSource, ignoreCase: true);
        Assert.StartsWith(CustomerSalesInvoiceEntryAuthorizationSqlServerFixture.DatabasePrefix,
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

    private static CustomerSalesInvoiceEvidenceController EvidenceController(
        ErpDbContext db, long? userId, string? requestPath)
        => new(db)
        {
            ControllerContext = new ControllerContext { HttpContext = NewHttp(userId, requestPath) }
        };

    private static CustomerSalesInvoiceCollectionAllocationController AllocationController(
        ErpDbContext db, long? userId, string? requestPath)
        => new(db)
        {
            ControllerContext = new ControllerContext { HttpContext = NewHttp(userId, requestPath) }
        };

    /// <summary>未绑定任何请求上下文的纯进程内直调（外部请求无法构造）。</summary>
    private static CustomerSalesInvoiceCollectionAllocationController InProcessAllocationController(ErpDbContext db)
        => new(db);

    private static async Task<(bool Success, int Code, string Error)> TryEvidenceAsync(
        ErpDbContext db, long? userId, string? requestPath,
        Func<CustomerSalesInvoiceEvidenceController, Task<IActionResult>> action)
    {
        try
        {
            var result = await action(EvidenceController(db, userId, requestPath));
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

    private static async Task<(bool Success, int Code, string Error)> TryAllocationAsync(
        ErpDbContext db, long? userId, string? requestPath,
        Func<CustomerSalesInvoiceCollectionAllocationController, Task<IActionResult>> action)
        => await TryAllocationAsync(AllocationController(db, userId, requestPath), action);

    private static async Task<(bool Success, int Code, string Error)> TryAllocationAsync(
        CustomerSalesInvoiceCollectionAllocationController controller,
        Func<CustomerSalesInvoiceCollectionAllocationController, Task<IActionResult>> action)
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

    // ==================== 请求契约与路由判决助手 ====================

    private static CustomerSalesInvoiceEvidenceSaveDto EvidenceDto(long customerId, string invoiceNumber)
        => new()
        {
            InvoiceType = CustomerSalesInvoiceEvidenceRules.InvoiceTypeOrdinary,
            InvoiceNumber = invoiceNumber,
            InvoiceDate = DateTime.Today,
            CustomerId = customerId,
            Currency = "USD",
            NetAmount = 1000m,
            TaxAmount = 0m,
            GrossAmount = 1000m
        };

    private static CustomerSalesInvoiceAllocationSaveRequest AllocationRequest(long salesOrderId, decimal amount)
        => new()
        {
            Lines = new List<CustomerSalesInvoiceAllocationSaveDto>
            {
                new() { SalesOrderId = salesOrderId, AllocatedAmount = amount }
            }
        };

    private static CustomerSalesInvoiceCollectionAllocationSaveDto CollectionDto(long invoiceId, long receiptId)
        => new()
        {
            CustomerSalesInvoiceEvidenceId = invoiceId,
            ReceiptId = receiptId,
            AllocatedAmount = 100m,
            Remark = "INT487"
        };

    /// <summary>客户销项发票 11 条入口在同一身份 / 请求形状下的判决码必须一致。</summary>
    private async Task AssertEvidenceRoutesAsync(
        ErpDbContext db, long? userId, string? requestPath, long invoiceId, long salesOrderId, int code)
    {
        Assert.Equal(code, (await TryEvidenceAsync(db, userId, requestPath,
            c => c.GetPaged(new CustomerSalesInvoiceEvidenceQuery()))).Code);
        Assert.Equal(code, (await TryEvidenceAsync(db, userId, requestPath,
            c => c.CollectionTiming(new CustomerInvoiceCollectionTimingQuery()))).Code);
        Assert.Equal(code, (await TryEvidenceAsync(db, userId, requestPath,
            c => c.CommercialInvoiceCandidates(null, 10))).Code);
        Assert.Equal(code, (await TryEvidenceAsync(db, userId, requestPath,
            c => c.GetById(invoiceId))).Code);
        Assert.Equal(code, (await TryEvidenceAsync(db, userId, requestPath,
            c => c.Create(EvidenceDto(1, $"ROUTE-{Guid.NewGuid():N}")))).Code);
        Assert.Equal(code, (await TryEvidenceAsync(db, userId, requestPath,
            c => c.Update(invoiceId, EvidenceDto(1, $"ROUTE-{Guid.NewGuid():N}")))).Code);
        Assert.Equal(code, (await TryEvidenceAsync(db, userId, requestPath,
            c => c.OrderCandidates(invoiceId, null, 10))).Code);
        Assert.Equal(code, (await TryEvidenceAsync(db, userId, requestPath,
            c => c.PreviewAllocations(invoiceId, AllocationRequest(salesOrderId, 10m)))).Code);
        Assert.Equal(code, (await TryEvidenceAsync(db, userId, requestPath,
            c => c.SaveAllocations(invoiceId, AllocationRequest(salesOrderId, 10m)))).Code);
        Assert.Equal(code, (await TryEvidenceAsync(db, userId, requestPath,
            c => c.Record(invoiceId))).Code);
        Assert.Equal(code, (await TryEvidenceAsync(db, userId, requestPath,
            c => c.Void(invoiceId, new CustomerSalesInvoiceVoidRequest { Reason = "作废" }))).Code);
    }

    /// <summary>收款分摊证据 11 条入口在同一身份 / 请求形状下的判决码必须一致。</summary>
    private async Task AssertAllocationRoutesAsync(
        ErpDbContext db, long? userId, string? requestPath, long invoiceId, long receiptId, int code)
    {
        Assert.Equal(code, (await TryAllocationAsync(db, userId, requestPath,
            c => c.GetPaged(new CustomerSalesInvoiceCollectionAllocationQuery()))).Code);
        Assert.Equal(code, (await TryAllocationAsync(db, userId, requestPath,
            c => c.Metadata())).Code);
        Assert.Equal(code, (await TryAllocationAsync(db, userId, requestPath,
            c => c.ReceiptCandidates(1, "USD", null, 10))).Code);
        Assert.Equal(code, (await TryAllocationAsync(db, userId, requestPath,
            c => c.InvoiceCandidates(1, "USD", null, 10))).Code);
        Assert.Equal(code, (await TryAllocationAsync(db, userId, requestPath,
            c => c.ReceiptSummary(receiptId))).Code);
        Assert.Equal(code, (await TryAllocationAsync(db, userId, requestPath,
            c => c.AllocationsForReceipt(receiptId))).Code);
        Assert.Equal(code, (await TryAllocationAsync(db, userId, requestPath,
            c => c.InvoiceSummary(invoiceId))).Code);
        Assert.Equal(code, (await TryAllocationAsync(db, userId, requestPath,
            c => c.AllocationsForInvoice(invoiceId))).Code);
        Assert.Equal(code, (await TryAllocationAsync(db, userId, requestPath,
            c => c.GetById(1L))).Code);
        Assert.Equal(code, (await TryAllocationAsync(db, userId, requestPath,
            c => c.Create(CollectionDto(invoiceId, receiptId)))).Code);
        Assert.Equal(code, (await TryAllocationAsync(db, userId, requestPath,
            c => c.Void(1L, new CustomerSalesInvoiceCollectionAllocationVoidRequest { Reason = "作废" }))).Code);
    }

    // ==================== 种子助手（EF 生成身份主键，绝不清理既有行） ====================

    private static async Task<long> SeedCustomerAsync(ErpDbContext db, string tag)
    {
        // BaseCustomers.CustomerCode 上限 50：标签 + 32 位 GUID 长度恒 ≤ 45，绝不越界。
        var code = $"IC487C{tag}{Guid.NewGuid():N}";
        var customer = new BaseCustomer
        {
            CustomerCode = code, CustomerName = code, Status = 1, CreditStatus = "正常"
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<SalesOrder> SeedSalesOrderAsync(ErpDbContext db, long customerId)
    {
        var order = new SalesOrder
        {
            OrderNo = $"IS487-{Guid.NewGuid():N}"[..24],
            OrderDate = DateTime.Today.AddDays(-5),
            CustomerId = customerId,
            Currency = Currency.USD,
            ExchangeRate = 1m,
            TotalAmount = 1000m,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static async Task<CustomerSalesInvoiceEvidence> SeedInvoiceAsync(
        ErpDbContext db, long customerId, int status = CustomerSalesInvoiceEvidenceRules.StatusRecorded)
    {
        var number = $"II487-{Guid.NewGuid():N}"[..24];
        var invoice = new CustomerSalesInvoiceEvidence
        {
            InvoiceType = CustomerSalesInvoiceEvidenceRules.InvoiceTypeOrdinary,
            InvoiceCode = string.Empty,
            InvoiceNumber = number,
            NormalizedInvoiceNumber = CustomerSalesInvoiceEvidenceRules.NormalizeIdentityPart(number),
            InvoiceDate = DateTime.Today,
            CustomerId = customerId,
            CustomerCode = "IC487",
            CustomerName = "集成测试客户",
            Currency = "USD",
            NetAmount = 1000m,
            TaxAmount = 0m,
            GrossAmount = 1000m,
            Status = status
        };
        db.CustomerSalesInvoiceEvidences.Add(invoice);
        await db.SaveChangesAsync();
        return invoice;
    }

    private static async Task<FinanceReceipt> SeedReceiptAsync(ErpDbContext db, long customerId)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = $"IR487-{Guid.NewGuid():N}"[..24],
            ReceiptDate = DateTime.Today,
            CustomerId = customerId,
            Amount = 1000m,
            Currency = Currency.USD,
            PaymentMethod = PaymentMethod.BankTransfer,
            Status = DocumentStatus.Approved
        };
        db.FinanceReceipts.Add(receipt);
        await db.SaveChangesAsync();
        return receipt;
    }

    /// <summary>
    /// 播种真实操作账号并授予既有「销售订单」（sales-order）与「收款单」（receipt）菜单：<paramref name="privileged"/>
    /// 为 true 时复用系统内置角色口径（既有不受客户范围限制）；否则为映射到 <paramref name="ownedCustomerId"/> 的
    /// 受限业务员。绝不新增任何菜单 / 角色 / 用户授权，只复用既有菜单编码。
    /// </summary>
    private static async Task<(long UserId, long RoleId)> SeedOperatorAsync(
        ErpDbContext db, long? ownedCustomerId = null, bool salesOrderMenu = true, bool receiptMenu = true,
        bool privileged = true, UserStatus status = UserStatus.Enabled)
    {
        var code = $"ic487-op-{Guid.NewGuid():N}";
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
            RoleName = "销项发票入口集成角色", RoleCode = $"Ic487Op-{Guid.NewGuid():N}", IsSystem = privileged
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (salesOrderMenu)
        {
            var menuId = await db.SysMenus.AsNoTracking()
                .Where(m => m.MenuCode == CustomerSalesInvoiceAuthorizationRules.RequiredMenuCode && !m.IsDeleted)
                .Select(m => m.Id)
                .FirstAsync();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menuId });
        }
        if (receiptMenu)
        {
            var menuId = await db.SysMenus.AsNoTracking()
                .Where(m => m.MenuCode == CustomerReceiptLifecycleRules.RequiredMenuCode && !m.IsDeleted)
                .Select(m => m.Id)
                .FirstAsync();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menuId });
        }
        await db.SaveChangesAsync();

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

    // ==================== 1. 与请求形状无关：空路径与已赋值路径判定一致 ====================

    [Fact]
    public async Task Empty_and_populated_request_paths_enforce_identical_authority()
    {
        Guard();
        long invoiceId, receiptId, privilegedId, disabledId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customerId = await SeedCustomerAsync(seed, "PARITY");
            invoiceId = (await SeedInvoiceAsync(seed, customerId)).Id;
            receiptId = (await SeedReceiptAsync(seed, customerId)).Id;
            privilegedId = (await SeedOperatorAsync(seed)).UserId;
            disabledId = (await SeedOperatorAsync(seed, status: UserStatus.Disabled)).UserId;
        }

        // 匿名：两种请求形状都未认证；禁用身份：两种请求形状都权限不足；授权身份：两种请求形状都可读。
        foreach (var path in new[] { null, EvidencePath })
        {
            await using var db = _fixture.CreateDbContext();
            Assert.Equal(ErrorCodes.Unauthorized,
                (await TryEvidenceAsync(db, null, path, c => c.GetById(invoiceId))).Code);
            Assert.Equal(ErrorCodes.Forbidden,
                (await TryEvidenceAsync(db, disabledId, path, c => c.GetById(invoiceId))).Code);
            var ok = await TryEvidenceAsync(db, privilegedId, path, c => c.GetById(invoiceId));
            Assert.True(ok.Success, ok.Error);
        }

        foreach (var path in new[] { null, AllocationPath })
        {
            await using var db = _fixture.CreateDbContext();
            Assert.Equal(ErrorCodes.Unauthorized,
                (await TryAllocationAsync(db, null, path, c => c.ReceiptSummary(receiptId))).Code);
            Assert.Equal(ErrorCodes.Forbidden,
                (await TryAllocationAsync(db, disabledId, path, c => c.ReceiptSummary(receiptId))).Code);
            var ok = await TryAllocationAsync(db, privilegedId, path, c => c.ReceiptSummary(receiptId));
            Assert.True(ok.Success, ok.Error);
        }
    }

    // ==================== 2. 缺 / 零 / 未知 / 已删除身份：任何请求形状一律未认证 ====================

    [Fact]
    public async Task Missing_zero_unknown_and_deleted_identity_are_unauthorized_on_every_route()
    {
        Guard();
        long invoiceId, orderId, receiptId, deletedId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customerId = await SeedCustomerAsync(seed, "NOID");
            invoiceId = (await SeedInvoiceAsync(seed, customerId)).Id;
            orderId = (await SeedSalesOrderAsync(seed, customerId)).Id;
            receiptId = (await SeedReceiptAsync(seed, customerId)).Id;
            deletedId = (await SeedOperatorAsync(seed)).UserId;
            await SoftDeleteUserAsync(seed, deletedId);
        }

        await using var db = _fixture.CreateDbContext();
        foreach (var path in new[] { null, EvidencePath })
            foreach (long? identity in new long?[] { null, 0L, 987_654_321L, deletedId })
                await AssertEvidenceRoutesAsync(db, identity, path, invoiceId, orderId, ErrorCodes.Unauthorized);

        foreach (var path in new[] { null, AllocationPath })
            foreach (long? identity in new long?[] { null, 0L, 987_654_321L, deletedId })
                await AssertAllocationRoutesAsync(db, identity, path, invoiceId, receiptId, ErrorCodes.Unauthorized);
    }

    // ==================== 3. 禁用身份 / 撤销菜单：任何请求形状一律权限不足 ====================

    [Fact]
    public async Task Disabled_identity_and_revoked_menu_are_forbidden_on_every_route()
    {
        Guard();
        long invoiceId, orderId, receiptId, disabledId, revokedEvidenceId, revokedAllocationId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customerId = await SeedCustomerAsync(seed, "FORBID");
            invoiceId = (await SeedInvoiceAsync(seed, customerId)).Id;
            orderId = (await SeedSalesOrderAsync(seed, customerId)).Id;
            receiptId = (await SeedReceiptAsync(seed, customerId)).Id;
            disabledId = (await SeedOperatorAsync(seed, status: UserStatus.Disabled)).UserId;

            // 受限业务员（已映射本人客户）撤销既有「销售订单」菜单 → 权限不足。
            var (restricted, restrictedRole) =
                await SeedOperatorAsync(seed, ownedCustomerId: customerId, privileged: false);
            await RevokeMenuAsync(seed, restrictedRole, CustomerSalesInvoiceAuthorizationRules.RequiredMenuCode);
            revokedEvidenceId = restricted;

            // 特权账号撤销既有「收款单」菜单 → 权限不足（收款分摊证据不承认菜单豁免）。
            var (allocationOwner, allocationRole) = await SeedOperatorAsync(seed);
            await RevokeMenuAsync(seed, allocationRole, CustomerReceiptLifecycleRules.RequiredMenuCode);
            revokedAllocationId = allocationOwner;
        }

        await using var db = _fixture.CreateDbContext();
        foreach (var path in new[] { null, EvidencePath })
        {
            await AssertEvidenceRoutesAsync(db, disabledId, path, invoiceId, orderId, ErrorCodes.Forbidden);
            await AssertEvidenceRoutesAsync(db, revokedEvidenceId, path, invoiceId, orderId, ErrorCodes.Forbidden);
        }
        foreach (var path in new[] { null, AllocationPath })
        {
            await AssertAllocationRoutesAsync(db, disabledId, path, invoiceId, receiptId, ErrorCodes.Forbidden);
            await AssertAllocationRoutesAsync(db, revokedAllocationId, path, invoiceId, receiptId, ErrorCodes.Forbidden);
        }
    }

    // ==================== 4. 授权身份：保留完整生命周期（与请求形状无关） ====================

    [Fact]
    public async Task Permitted_genuine_identity_completes_the_lifecycle_on_both_request_shapes()
    {
        Guard();
        long customerId, orderId, receiptId, invoiceId, privilegedId;
        await using (var seed = _fixture.CreateDbContext())
        {
            customerId = await SeedCustomerAsync(seed, "OWN");
            orderId = (await SeedSalesOrderAsync(seed, customerId)).Id;
            receiptId = (await SeedReceiptAsync(seed, customerId)).Id;
            invoiceId = (await SeedInvoiceAsync(seed, customerId)).Id;
            privilegedId = (await SeedOperatorAsync(seed)).UserId;
        }

        // 客户销项发票：新建草稿 → 分摊 → 登记 → 作废（每种请求形状各一次）。
        foreach (var path in new[] { null, EvidencePath })
        {
            await using var db = _fixture.CreateDbContext();
            var ctl = EvidenceController(db, privilegedId, path);

            var created = Data<CustomerSalesInvoiceEvidenceDto>(
                await ctl.Create(EvidenceDto(customerId, $"II487-OWN-{Guid.NewGuid():N}")));
            Assert.Equal(CustomerSalesInvoiceEvidenceRules.StatusDraft, created.Status);
            Data<CustomerSalesInvoiceEvidenceDto>(
                await ctl.SaveAllocations(created.Id, AllocationRequest(orderId, 200m)));
            Assert.Equal(CustomerSalesInvoiceEvidenceRules.StatusRecorded,
                Data<CustomerSalesInvoiceEvidenceDto>(await ctl.Record(created.Id)).Status);
            Assert.Equal(CustomerSalesInvoiceEvidenceRules.StatusVoided,
                Data<CustomerSalesInvoiceEvidenceDto>(await ctl.Void(created.Id,
                    new CustomerSalesInvoiceVoidRequest { Reason = "集成重登" })).Status);
        }

        // 收款分摊证据：登记 → 作废（每种请求形状各一次）。
        foreach (var path in new[] { null, AllocationPath })
        {
            await using var db = _fixture.CreateDbContext();
            var ctl = AllocationController(db, privilegedId, path);

            var created = Data<CustomerSalesInvoiceCollectionAllocationDto>(
                await ctl.Create(CollectionDto(invoiceId, receiptId)));
            Assert.Equal(CustomerSalesInvoiceCollectionAllocationRules.StatusActive, created.Status);
            Assert.Equal(CustomerSalesInvoiceCollectionAllocationRules.StatusVoided,
                Data<CustomerSalesInvoiceCollectionAllocationDto>(await ctl.Void(created.Id,
                    new CustomerSalesInvoiceCollectionAllocationVoidRequest { Reason = "集成录错" })).Status);
        }
    }

    // ==================== 5. 空上下文 / 越界：受控拒绝且零写入 ====================

    [Fact]
    public async Task Collection_allocation_entry_denies_no_context_and_foreign_scope_with_zero_rows()
    {
        Guard();
        long mine, myInvoiceId, myReceiptId, foreignReceiptId, salesmanId;
        await using (var seed = _fixture.CreateDbContext())
        {
            mine = await SeedCustomerAsync(seed, "MINE");
            var foreign = await SeedCustomerAsync(seed, "FOREIGN");
            myInvoiceId = (await SeedInvoiceAsync(seed, mine)).Id;
            myReceiptId = (await SeedReceiptAsync(seed, mine)).Id;
            foreignReceiptId = (await SeedReceiptAsync(seed, foreign)).Id;
            salesmanId = (await SeedOperatorAsync(seed, ownedCustomerId: mine, privileged: false)).UserId;
        }

        await using (var db = _fixture.CreateDbContext())
        {
            // 纯进程内直调（无 HttpContext）：登记 / 作废同样 fail closed，绝不退回无授权服务直调。
            var inProcess = InProcessAllocationController(db);
            Assert.Equal(ErrorCodes.Unauthorized,
                (await TryAllocationAsync(inProcess,
                    c => c.GetPaged(new CustomerSalesInvoiceCollectionAllocationQuery()))).Code);
            Assert.Equal(ErrorCodes.Unauthorized,
                (await TryAllocationAsync(inProcess, c => c.Create(CollectionDto(myInvoiceId, myReceiptId)))).Code);
            Assert.Equal(ErrorCodes.Unauthorized,
                (await TryAllocationAsync(inProcess, c => c.Void(1L,
                    new CustomerSalesInvoiceCollectionAllocationVoidRequest { Reason = "越权" }))).Code);
        }

        await using (var db = _fixture.CreateDbContext())
        {
            // 受限业务员越界收款单：与「不存在」同一条不披露错误，且不新增任何证据行。
            foreach (var path in new[] { null, AllocationPath })
            {
                var denied = await TryAllocationAsync(db, salesmanId, path,
                    c => c.Create(CollectionDto(myInvoiceId, foreignReceiptId)));
                Assert.False(denied.Success);
                Assert.Equal(ErrorCodes.NotFound, denied.Code);
                Assert.Equal(CustomerReceiptLifecycleRules.AllocationNotFoundText, denied.Error);

                var summary = await TryAllocationAsync(db, salesmanId, path, c => c.ReceiptSummary(foreignReceiptId));
                Assert.Equal(ErrorCodes.NotFound, summary.Code);
                Assert.Equal(CustomerReceiptLifecycleRules.AllocationNotFoundText, summary.Error);
            }

            Assert.Equal(0, await db.CustomerSalesInvoiceCollectionAllocations.AsNoTracking()
                .CountAsync(a => a.CustomerSalesInvoiceEvidenceId == myInvoiceId));
        }
    }

    // ==================== 6. 被拒调用零写入、绝不新增授权 ====================

    [Fact]
    public async Task Denied_operations_persist_zero_rows_and_grant_nothing()
    {
        Guard();
        long invoiceId, orderId, receiptId, deletedId;
        int usersBefore, rolesBefore, menusBefore, grantsBefore, invoicesBefore, allocationsBefore;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customerId = await SeedCustomerAsync(seed, "ZERO");
            invoiceId = (await SeedInvoiceAsync(seed, customerId)).Id;
            orderId = (await SeedSalesOrderAsync(seed, customerId)).Id;
            receiptId = (await SeedReceiptAsync(seed, customerId)).Id;
            deletedId = (await SeedOperatorAsync(seed)).UserId;
            await SoftDeleteUserAsync(seed, deletedId);

            usersBefore = await seed.SysUsers.CountAsync();
            rolesBefore = await seed.SysRoles.CountAsync();
            menusBefore = await seed.SysMenus.CountAsync();
            grantsBefore = await seed.SysRoleMenus.CountAsync();
            invoicesBefore = await seed.CustomerSalesInvoiceEvidences.CountAsync();
            allocationsBefore = await seed.CustomerSalesInvoiceCollectionAllocations.CountAsync();
        }

        await using (var db = _fixture.CreateDbContext())
        {
            foreach (long? identity in new long?[] { null, 0L, 987_654_321L, deletedId })
            {
                await AssertEvidenceRoutesAsync(db, identity, null, invoiceId, orderId, ErrorCodes.Unauthorized);
                await AssertEvidenceRoutesAsync(db, identity, EvidencePath, invoiceId, orderId, ErrorCodes.Unauthorized);
                await AssertAllocationRoutesAsync(db, identity, null, invoiceId, receiptId, ErrorCodes.Unauthorized);
                await AssertAllocationRoutesAsync(db, identity, AllocationPath, invoiceId, receiptId,
                    ErrorCodes.Unauthorized);
            }
        }

        // 零写入：发票 / 分摊行 / 身份 / 角色 / 菜单授权行数全部不变，发票状态保持已登记。
        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(usersBefore, await verify.SysUsers.CountAsync());
            Assert.Equal(rolesBefore, await verify.SysRoles.CountAsync());
            Assert.Equal(menusBefore, await verify.SysMenus.CountAsync());
            Assert.Equal(grantsBefore, await verify.SysRoleMenus.CountAsync());
            Assert.Equal(invoicesBefore, await verify.CustomerSalesInvoiceEvidences.CountAsync());
            Assert.Equal(allocationsBefore, await verify.CustomerSalesInvoiceCollectionAllocations.CountAsync());
            Assert.Equal(CustomerSalesInvoiceEvidenceRules.StatusRecorded,
                await verify.CustomerSalesInvoiceEvidences.AsNoTracking()
                    .Where(x => x.Id == invoiceId).Select(x => x.Status).SingleAsync());
        }
    }
}

/// <summary>
/// ERP-487 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class CustomerSalesInvoiceEntryAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_CUSTOMERSALESINVOICEENTRYAUTHORIZATION";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-487] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-487] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }
}

/// <summary>专用目标护栏的 fail-closed 单元覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class CustomerSalesInvoiceEntryAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => CustomerSalesInvoiceEntryAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}
