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
using System.Data;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-349 客户收款单生命周期护栏 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）。
/// <para>直接对 <see cref="CustomerReceiptLifecycleRules"/> 与两套收款分摊证据服务做真实 SQL Server 验证：
/// 有效收款引用（ERP-053）拒绝收款单改金额 / 取消、作废释放、有效代理服务费分摊（ERP-071）拒绝改金额、
/// 以及同一收款单并发「登记证据 vs 改金额」「登记证据 vs 取消」经 UPDLOCK/HOLDLOCK 串行化后只能成功其一，
/// 失败时原始收款单与分摊证据保持不变。</para>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class CustomerReceiptLifecycleSqlServerTests
    : IClassFixture<CustomerReceiptLifecycleSqlServerFixture>
{
    private readonly CustomerReceiptLifecycleSqlServerFixture _fixture;

    public CustomerReceiptLifecycleSqlServerTests(CustomerReceiptLifecycleSqlServerFixture fixture)
        => _fixture = fixture;

    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith("NEWERP_AUTOTEST", target.InitialCatalog);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 事务化助手（与控制器 / ERP-343 同源锁定口径） ====================

    private async Task<(bool Success, string Error)> TryChangeAmountAsync(long receiptId, decimal newAmount)
    {
        await using var db = _fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await CustomerReceiptLifecycleRules.LockReceiptRowAsync(db, receiptId);

            var receipt = await db.FinanceReceipts
                .FirstOrDefaultAsync(r => r.Id == receiptId && !r.IsDeleted)
                ?? throw BusinessException.NotFound("收款单不存在");

            if (receipt.Status != DocumentStatus.Pending)
                throw BusinessException.RuleConflict("仅待提交状态的单据可修改");

            var amount = CustomerReceiptLifecycleRules.NormalizeReceiptAmount(newAmount, receipt.Currency.ToString());
            var changesEvidence = amount != CustomerReceiptLifecycleRules.AuthoritativeReceiptAmount(
                receipt.Amount, receipt.Currency.ToString());
            if (changesEvidence)
                await CustomerReceiptLifecycleRules.EnsureNoActiveAllocationAsync(db, receiptId, "修改金额");

            receipt.Amount = amount;
            receipt.UpdatedAt = DateTime.Now;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return (true, string.Empty);
        }
        catch (BusinessException ex)
        {
            await transaction.RollbackAsync();
            return (false, ex.Message);
        }
    }

    private async Task<(bool Success, string Error)> TryCancelAsync(long receiptId)
    {
        await using var db = _fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await CustomerReceiptLifecycleRules.LockReceiptRowAsync(db, receiptId);

            var receipt = await db.FinanceReceipts
                .FirstOrDefaultAsync(r => r.Id == receiptId && !r.IsDeleted)
                ?? throw BusinessException.NotFound("收款单不存在");

            if (receipt.Status == DocumentStatus.Cancelled)
                throw BusinessException.RuleConflict("收款单已取消，不能重复取消");

            await CustomerReceiptLifecycleRules.EnsureNoActiveAllocationAsync(db, receiptId, "取消");

            receipt.Status = DocumentStatus.Cancelled;
            receipt.UpdatedAt = DateTime.Now;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return (true, string.Empty);
        }
        catch (BusinessException ex)
        {
            await transaction.RollbackAsync();
            return (false, ex.Message);
        }
    }

    private async Task<(bool Success, string Error)> TryAllocateAsync(long receiptId, long orderId, decimal amount)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await CustomerReceiptAllocationService.CreateAsync(db, new CustomerReceiptAllocationSaveDto
            {
                ReceiptId = receiptId,
                SalesOrderId = orderId,
                AllocatedAmount = amount
            });
            return (true, string.Empty);
        }
        catch (BusinessException ex)
        {
            return (false, ex.Message);
        }
    }

    // ==================== 种子助手（EF 生成身份键） ====================

    private static long SeedCustomer(ErpDbContext db, string code, string name)
    {
        var customer = new BaseCustomer { CustomerCode = code, CustomerName = name, Status = 1, CreditStatus = "正常" };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer.Id;
    }

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId, Currency currency)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today.AddDays(-5),
            CustomerId = customerId,
            Currency = currency,
            ExchangeRate = 1m,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static FinanceReceipt SeedReceipt(
        ErpDbContext db, string receiptNo, long customerId, decimal amount, Currency currency,
        DocumentStatus status = DocumentStatus.Pending)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = DateTime.Today,
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            PaymentMethod = PaymentMethod.BankTransfer,
            Status = status
        };
        db.FinanceReceipts.Add(receipt);
        db.SaveChanges();
        return receipt;
    }

    private static void SeedCustomerOrderAllocation(
        ErpDbContext db, FinanceReceipt receipt, SalesOrder order, decimal amount, int status)
    {
        db.CustomerReceiptAllocations.Add(new CustomerReceiptAllocation
        {
            ReceiptId = receipt.Id,
            ReceiptNo = receipt.ReceiptNo,
            ReceiptDate = receipt.ReceiptDate,
            ReceiptStatus = (int)receipt.Status,
            ReceiptStatusText = receipt.Status.ToString(),
            ReceiptAmount = receipt.Amount,
            SalesOrderId = order.Id,
            OrderNo = order.OrderNo,
            OrderDate = order.OrderDate,
            OrderStatus = (int)order.Status,
            OrderCurrency = order.Currency.ToString(),
            CustomerId = receipt.CustomerId,
            CustomerCode = "CUST",
            CustomerName = "客户",
            AllocatedAmount = amount,
            Currency = order.Currency.ToString(),
            Status = status,
            AllocatedAt = DateTime.Now,
            VoidedAt = status == CustomerReceiptAllocationRules.StatusVoided ? DateTime.Now : null,
            VoidReason = status == CustomerReceiptAllocationRules.StatusVoided ? "录错" : string.Empty
        });
        db.SaveChanges();
    }

    private static void SeedAgencyFeeAllocation(ErpDbContext db, FinanceReceipt receipt, decimal amount, int status)
    {
        db.AgencyServiceFeeCollectionAllocations.Add(new AgencyServiceFeeCollectionAllocation
        {
            StatementId = 990001L,
            StatementNo = "STMT-990001",
            StatementDate = DateTime.Today.AddDays(-2),
            StatementStatus = 1,
            StatementStatusText = "已登记",
            StatementTotalAmount = 5000m,
            StatementCurrency = receipt.Currency.ToString(),
            StatementAgreementId = 1L,
            StatementAgreementNo = "AG-1",
            ReceiptId = receipt.Id,
            ReceiptNo = receipt.ReceiptNo,
            ReceiptDate = receipt.ReceiptDate,
            ReceiptStatus = (int)receipt.Status,
            ReceiptStatusText = receipt.Status.ToString(),
            ReceiptAmount = receipt.Amount,
            CustomerId = receipt.CustomerId,
            CustomerCode = "CUST",
            CustomerName = "客户",
            AllocatedAmount = amount,
            Currency = receipt.Currency.ToString(),
            Status = status,
            AllocatedAt = DateTime.Now,
            AllocatedBy = "tester",
            VoidedAt = status == AgencyServiceFeeCollectionAllocationRules.StatusVoided ? DateTime.Now : null,
            VoidReason = status == AgencyServiceFeeCollectionAllocationRules.StatusVoided ? "录错" : string.Empty
        });
        db.SaveChanges();
    }

    // ==================== 真实 SQL 场景 ====================

    [Fact]
    public async Task ReceiptChange_ActiveCustomerOrderAllocation_Refused_ThenVoidReleases()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var customerId = SeedCustomer(seed, "INT_TEST_RLC_CUST_A", "客户A");
        var order = SeedOrder(seed, "INT_TEST_RLC_SO_A", customerId, Currency.CNY);
        var receipt = SeedReceipt(seed, "INT_TEST_RLC_REC_A", customerId, 1000m, Currency.CNY);
        SeedCustomerOrderAllocation(seed, receipt, order, 300m, CustomerReceiptAllocationRules.StatusActive);

        var refused = await TryChangeAmountAsync(receipt.Id, 500m);

        Assert.False(refused.Success);
        Assert.Contains("收款分摊证据", refused.Error);
        using (var check = _fixture.CreateDbContext())
        {
            Assert.Equal(1000m, check.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id).Amount);
            Assert.Equal(CustomerReceiptAllocationRules.StatusActive,
                check.CustomerReceiptAllocations.AsNoTracking().Single(a => a.ReceiptId == receipt.Id).Status);
        }

        // 显式作废释放限制
        await using (var release = _fixture.CreateDbContext())
        {
            var row = release.CustomerReceiptAllocations.Single(a => a.ReceiptId == receipt.Id && !a.IsDeleted);
            row.Status = CustomerReceiptAllocationRules.StatusVoided;
            row.VoidedAt = DateTime.Now;
            row.VoidReason = "录错";
            await release.SaveChangesAsync();
        }

        var released = await TryChangeAmountAsync(receipt.Id, 500m);
        Assert.True(released.Success, released.Error);
        using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(500m, verify.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id).Amount);
        }
    }

    [Fact]
    public async Task ReceiptChange_ActiveAgencyFeeAllocation_Refused()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var customerId = SeedCustomer(seed, "INT_TEST_RLC_CUST_B", "客户B");
        var receipt = SeedReceipt(seed, "INT_TEST_RLC_REC_B", customerId, 1000m, Currency.USD);
        SeedAgencyFeeAllocation(seed, receipt, 300m, AgencyServiceFeeCollectionAllocationRules.StatusActive);

        var refused = await TryChangeAmountAsync(receipt.Id, 500m);

        Assert.False(refused.Success);
        Assert.Contains("收款分摊证据", refused.Error);
        using var check = _fixture.CreateDbContext();
        Assert.Equal(1000m, check.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id).Amount);
    }

    [Fact]
    public async Task Cancel_ActiveCustomerOrderAllocation_Refused_ThenVoidReleases()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var customerId = SeedCustomer(seed, "INT_TEST_RLC_CUST_C", "客户C");
        var order = SeedOrder(seed, "INT_TEST_RLC_SO_C", customerId, Currency.CNY);
        var receipt = SeedReceipt(seed, "INT_TEST_RLC_REC_C", customerId, 1000m, Currency.CNY);
        SeedCustomerOrderAllocation(seed, receipt, order, 300m, CustomerReceiptAllocationRules.StatusActive);

        var refused = await TryCancelAsync(receipt.Id);

        Assert.False(refused.Success);
        Assert.Contains("收款分摊证据", refused.Error);
        using (var check = _fixture.CreateDbContext())
        {
            Assert.Equal(DocumentStatus.Pending, check.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id).Status);
        }
    }

    [Fact]
    public async Task ConcurrentAllocation_Race_OnlyOneSucceeds_OriginalUnchangedOnFailure()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var customerId = SeedCustomer(seed, "INT_TEST_RLC_CUST_D", "客户D");
        var orderA = SeedOrder(seed, "INT_TEST_RLC_SO_D1", customerId, Currency.CNY);
        var orderB = SeedOrder(seed, "INT_TEST_RLC_SO_D2", customerId, Currency.CNY);
        var receipt = SeedReceipt(seed, "INT_TEST_RLC_REC_D", customerId, 1000m, Currency.CNY);

        var results = await Task.WhenAll(
            TryAllocateAsync(receipt.Id, orderA.Id, 600m),
            TryAllocateAsync(receipt.Id, orderB.Id, 600m));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(1, results.Count(r => !r.Success));

        using var check = _fixture.CreateDbContext();
        var savedReceipt = check.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id);
        Assert.Equal(1000m, savedReceipt.Amount);
        Assert.Equal(DocumentStatus.Pending, savedReceipt.Status);
        var rows = check.CustomerReceiptAllocations.AsNoTracking()
            .Where(a => a.ReceiptId == receipt.Id && !a.IsDeleted && a.Status == CustomerReceiptAllocationRules.StatusActive)
            .ToList();
        Assert.Single(rows);
        Assert.Equal(600m, rows[0].AllocatedAmount);
    }

    [Fact]
    public async Task ConcurrentAllocationVersusCancel_OnlyOneSucceeds()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var customerId = SeedCustomer(seed, "INT_TEST_RLC_CUST_E", "客户E");
        var order = SeedOrder(seed, "INT_TEST_RLC_SO_E", customerId, Currency.CNY);
        var receipt = SeedReceipt(seed, "INT_TEST_RLC_REC_E", customerId, 1000m, Currency.CNY);

        var results = await Task.WhenAll(
            TryAllocateAsync(receipt.Id, order.Id, 100m),
            TryCancelAsync(receipt.Id));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(1, results.Count(r => !r.Success));
    }

    // ==================== 6. 认证控制器（ERP-434：实时身份 + 收款单菜单 + 客户范围） ====================

    private static long SeedPrivilegedUserWithMenu(ErpDbContext db)
    {
        var role = new SysRole
        {
            RoleName = "集成收款特权角色",
            RoleCode = $"INT_RC434_PRIV_{Guid.NewGuid():N}",
            IsSystem = true
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        var user = new SysUser
        {
            UserName = $"int-rc434-priv-{Guid.NewGuid():N}",
            PasswordHash = "h", PasswordSalt = "s", DisplayName = "特权", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        var menu = db.SysMenus.Single(m => m.MenuCode == CustomerReceiptLifecycleRules.RequiredMenuCode && !m.IsDeleted);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return user.Id;
    }

    private static long SeedSalesmanWithMenu(ErpDbContext db, string userName, bool grantMenu = true)
    {
        var role = new SysRole
        {
            RoleName = "集成收款业务员",
            RoleCode = $"INT_RC434_SALES_{Guid.NewGuid():N}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        var user = new SysUser
        {
            UserName = userName, PasswordHash = "h", PasswordSalt = "s",
            DisplayName = userName, Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        if (grantMenu)
        {
            var menu = db.SysMenus.Single(m => m.MenuCode == CustomerReceiptLifecycleRules.RequiredMenuCode && !m.IsDeleted);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        }
        db.BaseEmployees.Add(new BaseEmployee
        {
            EmployeeCode = userName, EmployeeName = userName, IsSalesman = true, Status = 1
        });
        db.SaveChanges();
        return user.Id;
    }

    private static void RevokeMenus(ErpDbContext db, long userId)
    {
        var roleIds = db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && !ur.IsDeleted).Select(ur => ur.RoleId).ToList();
        foreach (var grant in db.SysRoleMenus.Where(rm => !rm.IsDeleted && roleIds.Contains(rm.RoleId)).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();
    }

    private static long SeedCustomerWithOwner(ErpDbContext db, string code, string name, long? empId)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code, CustomerName = name, Status = 1, CreditStatus = "正常", EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer.Id;
    }

    private static long SeedInvoiceEvidence(
        ErpDbContext db, string number, long customerId, string currency, decimal grossAmount = 1000m)
    {
        var invoice = new CustomerSalesInvoiceEvidence
        {
            InvoiceType = "普票",
            InvoiceCode = string.Empty,
            InvoiceNumber = number,
            NormalizedInvoiceNumber = number.Replace("-", "").Replace("_", "").ToUpperInvariant(),
            InvoiceDate = DateTime.Today.AddDays(-3),
            CustomerId = customerId,
            CustomerCode = "C" + customerId,
            CustomerName = "客户" + customerId,
            Currency = currency,
            NetAmount = grossAmount,
            TaxAmount = 0m,
            GrossAmount = grossAmount,
            Status = CustomerSalesInvoiceEvidenceRules.StatusRecorded
        };
        db.CustomerSalesInvoiceEvidences.Add(invoice);
        db.SaveChanges();
        return invoice.Id;
    }

    private static T BuildAuthController<T>(T controller, long? userId, string path) where T : ControllerBase
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "IntTest"))
        };
        // 标记为真实 HTTP 路由（Request.Path 已赋值）：缺失身份也必须实时授权并 fail closed。
        http.Request.Path = path;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static CustomerReceiptAllocationController OrderController(ErpDbContext db, long? userId)
        => BuildAuthController(new CustomerReceiptAllocationController(db), userId,
            "/api/customer-receipt-allocations");

    private static CustomerSalesInvoiceCollectionAllocationController InvoiceController(ErpDbContext db, long? userId)
        => BuildAuthController(new CustomerSalesInvoiceCollectionAllocationController(db), userId,
            "/api/customer-sales-invoice-collection-allocations");

    private static CustomerReceiptAllocationSaveDto OrderDto(long receiptId, long orderId, decimal amount)
        => new() { ReceiptId = receiptId, SalesOrderId = orderId, AllocatedAmount = amount, Remark = string.Empty };

    private static CustomerSalesInvoiceCollectionAllocationSaveDto InvoiceDto(long receiptId, long invoiceId, decimal amount)
        => new() { ReceiptId = receiptId, CustomerSalesInvoiceEvidenceId = invoiceId, AllocatedAmount = amount };

    private static async Task<BusinessException> AssertCodeAsync(int code, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(code, ex.Code);
        return ex;
    }

    private static T AssertOkDto<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    [Fact]
    public async Task 认证控制器_缺失停用删除撤权身份与无菜单一律拒绝且不落行()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = SeedCustomer(db, "INT_RC434_C1", "客户434A");
        var order = SeedOrder(db, "INT_RC434_SO1", customerId, Currency.CNY);
        var receipt = SeedReceipt(db, "INT_RC434_R1", customerId, 100m, Currency.CNY, DocumentStatus.Approved);
        var invoiceId = SeedInvoiceEvidence(db, "INT_RC434_INV1", customerId, "CNY");

        var disabledId = SeedPrivilegedUserWithMenu(db);
        (await db.SysUsers.SingleAsync(u => u.Id == disabledId)).Status = UserStatus.Disabled;

        var deletedId = SeedPrivilegedUserWithMenu(db);
        (await db.SysUsers.SingleAsync(u => u.Id == deletedId)).IsDeleted = true;

        var revokedId = SeedPrivilegedUserWithMenu(db);
        RevokeMenus(db, revokedId);
        await db.SaveChangesAsync();

        var noMenuId = SeedSalesmanWithMenu(db, $"int-rc434-nomenu-{Guid.NewGuid():N}", grantMenu: false);

        // 缺失 / 非法 / 已删除身份 → 未认证
        foreach (long? userId in new long?[] { null, 0, deletedId })
        {
            var controller = OrderController(db, userId);
            await AssertCodeAsync(ErrorCodes.Unauthorized,
                () => controller.GetPaged(new CustomerReceiptAllocationQuery()));
            await AssertCodeAsync(ErrorCodes.Unauthorized, () => controller.ReceiptSummary(receipt.Id));
            await AssertCodeAsync(ErrorCodes.Unauthorized, () => controller.Create(OrderDto(receipt.Id, order.Id, 10m)));
            await AssertCodeAsync(ErrorCodes.Unauthorized,
                () => controller.Void(1, new CustomerReceiptAllocationVoidRequest { Reason = "作废" }));
            var invoiceController = InvoiceController(db, userId);
            await AssertCodeAsync(ErrorCodes.Unauthorized,
                () => invoiceController.Create(InvoiceDto(receipt.Id, invoiceId, 10m)));
        }

        // 禁用 / 删除菜单 / 无菜单 → 权限不足
        foreach (var userId in new[] { disabledId, revokedId, noMenuId })
        {
            var controller = OrderController(db, userId);
            await AssertCodeAsync(ErrorCodes.Forbidden,
                () => controller.GetPaged(new CustomerReceiptAllocationQuery()));
            await AssertCodeAsync(ErrorCodes.Forbidden, () => controller.Create(OrderDto(receipt.Id, order.Id, 10m)));
            await AssertCodeAsync(ErrorCodes.Forbidden,
                () => controller.Void(1, new CustomerReceiptAllocationVoidRequest { Reason = "作废" }));
            var invoiceController = InvoiceController(db, userId);
            await AssertCodeAsync(ErrorCodes.Forbidden,
                () => invoiceController.Create(InvoiceDto(receipt.Id, invoiceId, 10m)));
        }

        // 被拒请求一律不落任何证据行
        Assert.Equal(0, await db.CustomerReceiptAllocations.AsNoTracking().CountAsync(a => a.ReceiptId == receipt.Id));
        Assert.Equal(0, await db.CustomerSalesInvoiceCollectionAllocations.AsNoTracking()
            .CountAsync(a => a.ReceiptId == receipt.Id));
    }

    [Fact]
    public async Task 认证控制器_自有范围登记与作废成功_越范围与已删除收款单同一条错误且零变更()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var ownName = $"int-rc434-order-{Guid.NewGuid():N}";
        var salesmanId = SeedSalesmanWithMenu(db, ownName);
        var employeeId = (await db.BaseEmployees.SingleAsync(e => e.EmployeeCode == ownName)).Id;
        var ownCustomerId = SeedCustomerWithOwner(db, "INT_RC434_OWN", "自有客户", employeeId);
        var foreignCustomerId = SeedCustomerWithOwner(db, "INT_RC434_FGN", "他人客户", null);

        var ownReceipt = SeedReceipt(db, "INT_RC434_OWN_R", ownCustomerId, 1000m, Currency.CNY, DocumentStatus.Approved);
        var ownReceipt2 = SeedReceipt(db, "INT_RC434_OWN_R2", ownCustomerId, 1000m, Currency.CNY, DocumentStatus.Approved);
        var foreignReceipt = SeedReceipt(db, "INT_RC434_FGN_R", foreignCustomerId, 1000m, Currency.CNY, DocumentStatus.Approved);
        var deletionReceipt = SeedReceipt(db, "INT_RC434_DEL_R", ownCustomerId, 1000m, Currency.CNY, DocumentStatus.Approved);
        var ownOrder = SeedOrder(db, "INT_RC434_OWN_SO", ownCustomerId, Currency.CNY);
        var ownOrder2 = SeedOrder(db, "INT_RC434_OWN_SO2", ownCustomerId, Currency.CNY);
        var foreignOrder = SeedOrder(db, "INT_RC434_FGN_SO", foreignCustomerId, Currency.CNY);
        var deletionOrder = SeedOrder(db, "INT_RC434_DEL_SO", ownCustomerId, Currency.CNY);

        // 特权账号先建立越范围与即将软删除收款单的有效引用行
        var privileged = OrderController(db, SeedPrivilegedUserWithMenu(db));
        var ownRow = AssertOkDto<CustomerReceiptAllocationDto>(
            await privileged.Create(OrderDto(ownReceipt.Id, ownOrder.Id, 50m)));
        var foreignRow = AssertOkDto<CustomerReceiptAllocationDto>(
            await privileged.Create(OrderDto(foreignReceipt.Id, foreignOrder.Id, 50m)));
        var deletionRow = AssertOkDto<CustomerReceiptAllocationDto>(
            await privileged.Create(OrderDto(deletionReceipt.Id, deletionOrder.Id, 50m)));

        (await db.FinanceReceipts.SingleAsync(r => r.Id == deletionReceipt.Id)).IsDeleted = true;
        await db.SaveChangesAsync();

        var controller = OrderController(db, salesmanId);

        // 自有范围内：登记与作废均被许可（被许可的生命周期）
        var ownNew = AssertOkDto<CustomerReceiptAllocationDto>(
            await controller.Create(OrderDto(ownReceipt2.Id, ownOrder2.Id, 20m)));
        Assert.True(ownNew.IsActive);
        var voided = AssertOkDto<CustomerReceiptAllocationDto>(
            await controller.Void(ownRow.Id, new CustomerReceiptAllocationVoidRequest { Reason = "录错" }));
        Assert.True(voided.IsVoided);

        // 越范围付款单：与不存在收款单同一条不披露错误
        var foreignDenied = await AssertCodeAsync(ErrorCodes.NotFound, () => controller.ReceiptSummary(foreignReceipt.Id));
        var missingDenied = await AssertCodeAsync(ErrorCodes.NotFound, () => controller.ReceiptSummary(987_654_321L));
        Assert.Equal(missingDenied.Message, foreignDenied.Message);
        await AssertCodeAsync(ErrorCodes.NotFound, () => controller.Create(OrderDto(foreignReceipt.Id, ownOrder2.Id, 10m)));
        await AssertCodeAsync(ErrorCodes.NotFound,
            () => controller.Void(foreignRow.Id, new CustomerReceiptAllocationVoidRequest { Reason = "越权作废" }));

        // 已删除收款单：登记与作废一律按不存在拒绝
        await AssertCodeAsync(ErrorCodes.NotFound, () => controller.Create(OrderDto(deletionReceipt.Id, deletionOrder.Id, 10m)));
        await AssertCodeAsync(ErrorCodes.NotFound,
            () => controller.Void(deletionRow.Id, new CustomerReceiptAllocationVoidRequest { Reason = "已删除作废" }));

        // 证据零变更：被拒行仍为有效、无作废时间
        var storedForeign = await db.CustomerReceiptAllocations.AsNoTracking().SingleAsync(a => a.Id == foreignRow.Id);
        Assert.Equal(CustomerReceiptAllocationRules.StatusActive, storedForeign.Status);
        Assert.Null(storedForeign.VoidedAt);
        var storedDeletion = await db.CustomerReceiptAllocations.AsNoTracking().SingleAsync(a => a.Id == deletionRow.Id);
        Assert.Equal(CustomerReceiptAllocationRules.StatusActive, storedDeletion.Status);
        Assert.Null(storedDeletion.VoidedAt);

        // 台账按客户范围过滤：受限业务员只看到自有范围内收款单的引用行
        var ledger = AssertOkDto<PagedResult<CustomerReceiptAllocationDto>>(
            await controller.GetPaged(new CustomerReceiptAllocationQuery()));
        Assert.All(ledger.Items, i => Assert.Equal(ownCustomerId, i.CustomerId));
    }

    [Fact]
    public async Task 认证控制器_越范围收款单与发票证据读取同一条不披露错误_且范围外证据不泄露()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var ownName = $"int-rc434-inv-{Guid.NewGuid():N}";
        var salesmanId = SeedSalesmanWithMenu(db, ownName);
        var employeeId = (await db.BaseEmployees.SingleAsync(e => e.EmployeeCode == ownName)).Id;
        var ownCustomerId = SeedCustomerWithOwner(db, "INT_RC434I_OWN", "自有客户I", employeeId);
        var foreignCustomerId = SeedCustomerWithOwner(db, "INT_RC434I_FGN", "他人客户I", null);

        var ownReceipt = SeedReceipt(db, "INT_RC434I_OWN_R", ownCustomerId, 1000m, Currency.CNY, DocumentStatus.Approved);
        var foreignReceipt = SeedReceipt(db, "INT_RC434I_FGN_R", foreignCustomerId, 1000m, Currency.CNY, DocumentStatus.Approved);
        var deletionReceipt = SeedReceipt(db, "INT_RC434I_DEL_R", ownCustomerId, 1000m, Currency.CNY, DocumentStatus.Approved);
        var ownInvoiceId = SeedInvoiceEvidence(db, "INT_RC434I_OWN_INV", ownCustomerId, "CNY");
        var foreignInvoiceId = SeedInvoiceEvidence(db, "INT_RC434I_FGN_INV", foreignCustomerId, "CNY");
        var deletionInvoiceId = SeedInvoiceEvidence(db, "INT_RC434I_DEL_INV", ownCustomerId, "CNY");

        var privileged = InvoiceController(db, SeedPrivilegedUserWithMenu(db));
        AssertOkDto<CustomerSalesInvoiceCollectionAllocationDto>(
            await privileged.Create(InvoiceDto(ownReceipt.Id, ownInvoiceId, 50m)));
        var foreignRow = AssertOkDto<CustomerSalesInvoiceCollectionAllocationDto>(
            await privileged.Create(InvoiceDto(foreignReceipt.Id, foreignInvoiceId, 50m)));
        var deletionRow = AssertOkDto<CustomerSalesInvoiceCollectionAllocationDto>(
            await privileged.Create(InvoiceDto(deletionReceipt.Id, deletionInvoiceId, 50m)));

        (await db.FinanceReceipts.SingleAsync(r => r.Id == deletionReceipt.Id)).IsDeleted = true;
        await db.SaveChangesAsync();

        var controller = InvoiceController(db, salesmanId);

        // 自有范围内：发票侧汇总被许可
        Assert.NotNull(AssertOkDto<CustomerSalesInvoiceCollectionAllocationInvoiceSummaryDto>(
            await controller.InvoiceSummary(ownInvoiceId)));

        // 越范围收款单：与不存在收款单同一条不披露错误
        var foreignDenied = await AssertCodeAsync(ErrorCodes.NotFound, () => controller.ReceiptSummary(foreignReceipt.Id));
        var missingDenied = await AssertCodeAsync(ErrorCodes.NotFound, () => controller.ReceiptSummary(987_654_321L));
        Assert.Equal(missingDenied.Message, foreignDenied.Message);
        await AssertCodeAsync(ErrorCodes.NotFound,
            () => controller.Void(foreignRow.Id, new CustomerSalesInvoiceCollectionAllocationVoidRequest { Reason = "越权作废" }));

        // 越范围发票证据：与不存在发票同一条不披露错误
        var foreignInvoiceDenied = await AssertCodeAsync(ErrorCodes.NotFound, () => controller.InvoiceSummary(foreignInvoiceId));
        var missingInvoiceDenied = await AssertCodeAsync(ErrorCodes.NotFound, () => controller.InvoiceSummary(987_654_321L));
        Assert.Equal(missingInvoiceDenied.Message, foreignInvoiceDenied.Message);
        await AssertCodeAsync(ErrorCodes.NotFound, () => controller.AllocationsForInvoice(foreignInvoiceId));
        await AssertCodeAsync(ErrorCodes.NotFound, () => controller.InvoiceCandidates(foreignCustomerId, "CNY", null, 50));

        // 已删除收款单与发票：登记与作废一律按不存在拒绝
        await AssertCodeAsync(ErrorCodes.NotFound, () => controller.Create(InvoiceDto(deletionReceipt.Id, deletionInvoiceId, 10m)));
        await AssertCodeAsync(ErrorCodes.NotFound,
            () => controller.Void(deletionRow.Id, new CustomerSalesInvoiceCollectionAllocationVoidRequest { Reason = "已删除作废" }));

        // 证据零变更：被拒行仍为有效、无作废时间
        var storedForeign = await db.CustomerSalesInvoiceCollectionAllocations.AsNoTracking()
            .SingleAsync(a => a.Id == foreignRow.Id);
        Assert.Equal(CustomerSalesInvoiceCollectionAllocationRules.StatusActive, storedForeign.Status);
        Assert.Null(storedForeign.VoidedAt);
        var storedDeletion = await db.CustomerSalesInvoiceCollectionAllocations.AsNoTracking()
            .SingleAsync(a => a.Id == deletionRow.Id);
        Assert.Equal(CustomerSalesInvoiceCollectionAllocationRules.StatusActive, storedDeletion.Status);
        Assert.Null(storedDeletion.VoidedAt);
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture：每次运行创建一个全新 GUID 后缀库并重置为完整 NEWERP 结构 + 种子数据，
/// 供真实 SQL Server 集成测试复用；发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
/// </summary>
public sealed class CustomerReceiptLifecycleSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_RECEIPTLIFECYCLE_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-349] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await CreateFreshDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};Integrated Security=true;TrustServerCertificate=true;";

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
        var builder = new SqlConnectionStringBuilder(ConnectionString);
        var database = builder.InitialCatalog;

        // 破坏性重置前再次护栏：绝不使用生产回退。
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
                throw new InvalidOperationException("The isolated fixture database already exists; choose a fresh NEWERP_AUTOTEST database.");
        }

        await using (var db = CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await SchemaUpgrader.EnsureUpgradedAsync(db);
            await SeedData.InitializeAsync(db);
            await SchemaUpgrader.EnsureUpgradedAsync(db);
        }

        Console.WriteLine("[ERP-349] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据。");
    }
}

public sealed class CustomerReceiptLifecycleTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() => CustomerReceiptLifecycleSqlServerFixture.AssertDedicatedTarget(connection));
}
