using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-487 客户销项发票（<c>api/customer-sales-invoices</c>）与「收款单 → 客户销项发票证据」收款分摊证据
/// （<c>api/customer-sales-invoice-collection-allocations</c>）入口实时授权单元测试。
/// <para>以真实 <see cref="DefaultHttpContext"/> 身份驱动真实控制器：空路径与已赋值路径必须给出完全一致的判定；
/// 绑定到请求管线但缺失 / 为零 / 未知 / 已删除身份一律未认证（2000），禁用 / 无菜单 / 被撤销菜单一律权限不足（2002），
/// 受限业务员越界客户 / 收款单 / 发票一律受控非披露拒绝，且全部先于任何计数、候选、快照、预览与写入；
/// 被拒调用零发票 / 分摊行 / 收款单 / 来源 / 状态改动。</para>
/// <para>收款分摊证据入口（含登记 / 作废）<strong>不再存在</strong>「无 HTTP 请求管线且无身份」的无授权服务直调旁路；
/// 客户销项发票入口同样不再读取 <c>Request.Path</c>，也不再依据是否携带可解析身份，仅未绑定任何请求的纯进程内直调
/// 沿用仓库一致的进程内边界（外部请求无法构造）。全部使用隔离内存测试数据（<see cref="TestDbFactory"/>），
/// 只播种既有菜单 / 角色授权与真实启用身份，绝不新增生产授权，也不提供任何测试专用开关。
/// 真实隔离 SQL 用例见
/// <c>ERP.IntegrationTests/CustomerSalesInvoiceEntryAuthorizationSqlServerTests.cs</c>。</para>
/// </summary>
public class CustomerSalesInvoiceEntryAuthorizationTests
{
    private const string EvidencePath = "/api/customer-sales-invoices";
    private const string AllocationPath = "/api/customer-sales-invoice-collection-allocations";

    // ==================== 0. 测试脚手架（隔离测试数据，绝不新增生产授权） ====================

    /// <summary>补缺失的既有菜单行（与 <c>SeedData.Menus</c> 同源编码 / 文案），绝不改写既有菜单语义。</summary>
    private static SysMenu EnsureMenu(ErpDbContext db, string code, string name, string path)
    {
        var menu = db.SysMenus.FirstOrDefault(m => !m.IsDeleted && m.MenuCode == code);
        if (menu is not null) return menu;

        menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = code,
            MenuName = name,
            Path = path,
            MenuType = MenuType.Menu,
            CreatedAt = DateTime.Now
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    /// <summary>
    /// 播种启用账号 + 真实角色授权：<paramref name="privileged"/> = 系统内置角色口径（不受客户范围限制）；
    /// 否则为受限业务员口径（须另行调用 <see cref="SeedSalesman"/> 映射员工编码与本人客户）。
    /// </summary>
    private static (long UserId, long RoleId) SeedOperator(
        ErpDbContext db, bool salesOrderMenu = true, bool receiptMenu = true,
        UserStatus status = UserStatus.Enabled, bool privileged = true)
    {
        var role = new SysRole
        {
            RoleCode = $"CsiEntry-{Guid.NewGuid():N}",
            RoleName = "销项发票入口测试角色",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"csi-entry-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "销项发票入口测试账号",
            Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (salesOrderMenu)
            db.SysRoleMenus.Add(new SysRoleMenu
            {
                RoleId = role.Id,
                MenuId = EnsureMenu(db, CustomerSalesInvoiceAuthorizationRules.RequiredMenuCode,
                    CustomerSalesInvoiceAuthorizationRules.RequiredMenuText, "/sales/sales-order").Id
            });
        if (receiptMenu)
            db.SysRoleMenus.Add(new SysRoleMenu
            {
                RoleId = role.Id,
                MenuId = EnsureMenu(db, CustomerReceiptLifecycleRules.RequiredMenuCode,
                    CustomerReceiptLifecycleRules.RequiredMenuText, "/finance/receipt").Id
            });
        db.SaveChanges();

        return (user.Id, role.Id);
    }

    /// <summary>把指定账号映射为受限业务员（员工编码 = 用户名）并归属给定客户，保持 ERP-097 唯一口径。</summary>
    private static void SeedSalesman(ErpDbContext db, long userId, params long[] ownedCustomerIds)
    {
        var user = db.SysUsers.Single(u => u.Id == userId);
        var employee = new BaseEmployee
        {
            EmployeeCode = user.UserName,
            EmployeeName = user.DisplayName,
            IsSalesman = true,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        foreach (var customerId in ownedCustomerIds)
            db.BaseCustomers.Single(c => c.Id == customerId).EmpId = employee.Id;
        db.SaveChanges();
    }

    /// <summary>撤销指定账号全部菜单授权（保留角色关联，模拟既有授权被撤销）。</summary>
    private static void RevokeMenus(ErpDbContext db, long userId)
    {
        var roleIds = db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && !ur.IsDeleted).Select(ur => ur.RoleId).ToList();
        foreach (var grant in db.SysRoleMenus.Where(rm => !rm.IsDeleted && roleIds.Contains(rm.RoleId)).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name, bool deleted = false)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            CreditLimit = 100000m,
            CreditDays = 30,
            IsDeleted = deleted
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedOrder(
        ErpDbContext db, string orderNo, long customerId, Currency currency = Currency.USD,
        DocumentStatus status = DocumentStatus.Approved, decimal totalAmount = 1000m)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 1),
            CustomerId = customerId,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = status
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static CustomerSalesInvoiceEvidence SeedInvoice(
        ErpDbContext db, string number, long customerId, string currency = "USD", decimal gross = 1000m,
        int status = CustomerSalesInvoiceEvidenceRules.StatusDraft, bool deleted = false,
        string customerName = "入口测试客户")
    {
        var invoice = new CustomerSalesInvoiceEvidence
        {
            InvoiceType = CustomerSalesInvoiceEvidenceRules.InvoiceTypeOrdinary,
            InvoiceCode = string.Empty,
            InvoiceNumber = number,
            NormalizedInvoiceNumber = CustomerSalesInvoiceEvidenceRules.NormalizeIdentityPart(number),
            InvoiceDate = new DateTime(2026, 9, 10),
            CustomerId = customerId,
            CustomerCode = "C-ENTRY",
            CustomerName = customerName,
            Currency = currency,
            NetAmount = gross,
            TaxAmount = 0m,
            GrossAmount = gross,
            Status = status,
            IsDeleted = deleted
        };
        db.CustomerSalesInvoiceEvidences.Add(invoice);
        db.SaveChanges();
        return invoice;
    }

    private static FinanceReceipt SeedReceipt(
        ErpDbContext db, string receiptNo, long customerId, decimal amount = 1000m, bool deleted = false)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = new DateTime(2026, 9, 25),
            CustomerId = customerId,
            Amount = amount,
            Currency = Currency.USD,
            PaymentMethod = PaymentMethod.BankTransfer,
            BankAccount = "ENTRY-TEST",
            Status = DocumentStatus.Approved,
            IsDeleted = deleted
        };
        db.FinanceReceipts.Add(receipt);
        db.SaveChanges();
        return receipt;
    }

    // ==================== 控制器工厂与断言助手 ====================

    /// <summary>
    /// 绑定真实 HTTP 身份（可空 = 缺身份）的客户销项发票控制器；<paramref name="path"/> 为 null 时刻意保持
    /// 空 <c>Request.Path</c>，用于证明授权判定与请求形状无关。
    /// </summary>
    private static CustomerSalesInvoiceEvidenceController EvidenceController(
        ErpDbContext db, long? userId, string? path)
    {
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(Claims(userId), "Test")) };
        if (path is not null) http.Request.Path = path;
        return new CustomerSalesInvoiceEvidenceController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    /// <summary>绑定真实 HTTP 身份的「收款单 → 客户销项发票证据」收款分摊证据控制器（同一请求形状口径）。</summary>
    private static CustomerSalesInvoiceCollectionAllocationController AllocationController(
        ErpDbContext db, long? userId, string? path, string? userName = null)
    {
        var claims = new List<Claim>(Claims(userId));
        if (userName is not null) claims.Add(new Claim(ClaimTypes.Name, userName));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        if (path is not null) http.Request.Path = path;
        return new CustomerSalesInvoiceCollectionAllocationController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static Claim[] Claims(long? userId)
        => userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, response.Code);
        Assert.NotNull(response.Data);
        return response.Data!;
    }

    private static async Task<BusinessException> AssertDenied(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        return ex;
    }

    private static string ReadSource(params string[] segments)
        => File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", Path.Combine(segments))));

    private static CustomerSalesInvoiceEvidenceSaveDto EvidenceDto(long customerId, string number)
        => new()
        {
            InvoiceType = CustomerSalesInvoiceEvidenceRules.InvoiceTypeOrdinary,
            InvoiceNumber = number,
            InvoiceDate = new DateTime(2026, 9, 10),
            CustomerId = customerId,
            Currency = "USD",
            NetAmount = 1000m,
            TaxAmount = 0m,
            GrossAmount = 1000m
        };

    private static CustomerSalesInvoiceAllocationSaveRequest AllocationRequest(long orderId, decimal amount = 100m)
        => new()
        {
            Lines = new List<CustomerSalesInvoiceAllocationSaveDto>
            {
                new() { SalesOrderId = orderId, AllocatedAmount = amount }
            }
        };

    private static CustomerSalesInvoiceCollectionAllocationSaveDto CollectionDto(long invoiceId, long receiptId)
        => new()
        {
            CustomerSalesInvoiceEvidenceId = invoiceId,
            ReceiptId = receiptId,
            AllocatedAmount = 100m
        };

    private static (int Code, string Message) Outcome(BusinessException ex) => (ex.Code, ex.Message);

    // ==================== 1. 控制器源码契约：授权门与请求形状无关、无兜底 ====================

    [Fact]
    public void Controllers_gate_live_authorization_independently_of_request_shape()
    {
        var evidence = ReadSource("src", "ERP.Api", "Controllers", "CustomerSalesInvoiceEvidenceController.cs");
        var allocation = ReadSource("src", "ERP.Api", "Controllers",
            "CustomerSalesInvoiceCollectionAllocationController.cs");

        foreach (var controller in new[] { evidence, allocation })
        {
            // 不再读取 Request.Path；无匿名 / 角色白名单 / 环境变量 / 内存库开关兜底。
            Assert.DoesNotContain("Request.Path", controller);
            Assert.DoesNotContain("AllowAnonymous", controller);
            Assert.DoesNotContain("[Authorize(Roles", controller);
            Assert.DoesNotContain("Environment.GetEnvironmentVariable", controller);
            Assert.DoesNotContain("UseInMemoryDatabase", controller);
            Assert.Contains("EnsureMenuAuthorizedAsync", controller);
        }

        // 客户销项发票：授权门只依据「是否绑定到请求管线」，范围谓词 / 单据 / 单证范围复核全部无条件调用。
        Assert.Contains("ControllerContext?.HttpContext is not null", evidence);
        Assert.Contains("CustomerSalesInvoiceAuthorizationRules.BuildScopePredicateAsync", evidence);
        Assert.Contains("CustomerSalesInvoiceAuthorizationRules.BuildTradeDocumentScopePredicateAsync", evidence);
        Assert.Contains("CustomerSalesInvoiceAuthorizationRules.EnsureInvoiceAuthorizedWithScopeAsync", evidence);
        Assert.Contains("CustomerSalesInvoiceAuthorizationRules.EnsureTradeDocumentAuthorizedAsync", evidence);
        Assert.Contains("CustomerSalesInvoiceAuthorizationRules.EnsureSalesOrderIdsAuthorizedAsync", evidence);

        // 收款分摊证据：登记 / 作废不再有无授权的服务直调旁路，全部走授权服务重载。
        Assert.DoesNotContain("RequiresLiveAuthorization", allocation);
        Assert.Contains("CustomerSalesInvoiceCollectionAllocationService.CreateAuthorizedAsync", allocation);
        Assert.Contains("CustomerSalesInvoiceCollectionAllocationService.VoidAuthorizedAsync", allocation);
        Assert.DoesNotContain("CustomerSalesInvoiceCollectionAllocationService.CreateAsync", allocation);
        Assert.DoesNotContain("CustomerSalesInvoiceCollectionAllocationService.VoidAsync", allocation);

        // 口径文案与实现同源（接口 / 文档 / 测试共用）。
        Assert.Contains("Request.Path", CustomerSalesInvoiceAuthorizationRules.PathIndependenceText);
        Assert.Contains("未认证", CustomerSalesInvoiceAuthorizationRules.PathIndependenceText);
        Assert.Contains("权限不足", CustomerSalesInvoiceAuthorizationRules.PathIndependenceText);
        Assert.Contains("无授权的服务直调", CustomerReceiptLifecycleRules.CollectionAllocationPathIndependenceText);
        Assert.Contains("未认证", CustomerReceiptLifecycleRules.CollectionAllocationPathIndependenceText);
        Assert.Contains("权限不足", CustomerReceiptLifecycleRules.CollectionAllocationPathIndependenceText);
    }

    // ==================== 2. 缺身份：空路径 / 已赋值路径完全一致且零写入 ====================

    [Fact]
    public async Task Evidence_missing_identity_denied_identically_on_empty_and_populated_paths_with_zero_mutation()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C487-A", "入口客户A");
        var invoice = SeedInvoice(db, "INV-487-A", customer.Id);
        var order = SeedOrder(db, "SO-487-A", customer.Id);
        var beforeStatus = invoice.Status;
        var beforeGross = invoice.GrossAmount;
        var beforeCount = await db.CustomerSalesInvoiceEvidences.CountAsync();

        var outcomes = new List<(int Code, string Message)>();
        foreach (var path in new string?[] { null, EvidencePath })
        {
            var ctl = EvidenceController(db, null, path);

            outcomes.Add(Outcome(await AssertDenied(ErrorCodes.Unauthorized,
                () => ctl.GetPaged(new CustomerSalesInvoiceEvidenceQuery()))));
            outcomes.Add(Outcome(await AssertDenied(ErrorCodes.Unauthorized,
                () => ctl.CollectionTiming(new CustomerInvoiceCollectionTimingQuery()))));
            outcomes.Add(Outcome(await AssertDenied(ErrorCodes.Unauthorized,
                () => ctl.CommercialInvoiceCandidates(null, 50))));
            outcomes.Add(Outcome(await AssertDenied(ErrorCodes.Unauthorized, () => ctl.GetById(invoice.Id))));
            outcomes.Add(Outcome(await AssertDenied(ErrorCodes.Unauthorized,
                () => ctl.Create(EvidenceDto(customer.Id, "INV-487-NEW")))));
            outcomes.Add(Outcome(await AssertDenied(ErrorCodes.Unauthorized,
                () => ctl.Update(invoice.Id, EvidenceDto(customer.Id, "INV-487-A")))));
            outcomes.Add(Outcome(await AssertDenied(ErrorCodes.Unauthorized,
                () => ctl.OrderCandidates(invoice.Id, null, 50))));
            outcomes.Add(Outcome(await AssertDenied(ErrorCodes.Unauthorized,
                () => ctl.PreviewAllocations(invoice.Id, AllocationRequest(order.Id)))));
            outcomes.Add(Outcome(await AssertDenied(ErrorCodes.Unauthorized,
                () => ctl.SaveAllocations(invoice.Id, AllocationRequest(order.Id)))));
            outcomes.Add(Outcome(await AssertDenied(ErrorCodes.Unauthorized, () => ctl.Record(invoice.Id))));
            outcomes.Add(Outcome(await AssertDenied(ErrorCodes.Unauthorized,
                () => ctl.Void(invoice.Id, new CustomerSalesInvoiceVoidRequest { Reason = "越权" }))));
        }

        // 空路径与已赋值路径对同一身份给出完全相同的判定（逐入口一致）。
        var half = outcomes.Count / 2;
        for (var i = 0; i < half; i++)
            Assert.Equal(outcomes[i], outcomes[i + half]);

        // 全部为同一条受控未认证文案（不泄露发票 / 客户 / 分摊证据）。
        Assert.All(outcomes, o =>
            Assert.Equal(CustomerSalesInvoiceAuthorizationRules.UnauthorizedText, o.Message));

        // 零写入：发票状态 / 金额不变、无新增发票、无分摊行。
        var reloaded = await db.CustomerSalesInvoiceEvidences.AsNoTracking().SingleAsync(x => x.Id == invoice.Id);
        Assert.Equal(beforeStatus, reloaded.Status);
        Assert.Equal(beforeGross, reloaded.GrossAmount);
        Assert.Equal(beforeCount, await db.CustomerSalesInvoiceEvidences.CountAsync());
        Assert.Equal(0, await db.CustomerSalesInvoiceAllocations.CountAsync());
    }

    // ==================== 3. 停用 / 删除 / 未知 / 撤销菜单身份一律受控拒绝 ====================

    [Fact]
    public async Task Evidence_identity_states_are_denied_before_any_read_or_write()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C487-B", "入口客户B");
        var invoice = SeedInvoice(db, "INV-487-B", customer.Id);

        var (disabledId, _) = SeedOperator(db);
        (await db.SysUsers.SingleAsync(u => u.Id == disabledId)).Status = UserStatus.Disabled;

        var (deletedId, _) = SeedOperator(db);
        (await db.SysUsers.SingleAsync(u => u.Id == deletedId)).IsDeleted = true;

        // 受限业务员（已映射本人客户）被撤销既有菜单 → 权限不足（特权账号的菜单豁免口径不适用于受限账号）。
        var (revokedId, _) = SeedOperator(db, privileged: false);
        SeedSalesman(db, revokedId, customer.Id);
        RevokeMenus(db, revokedId);
        await db.SaveChangesAsync();

        foreach (var path in new string?[] { null, EvidencePath })
        {
            Assert.Equal(CustomerSalesInvoiceAuthorizationRules.DisabledIdentityText,
                (await AssertDenied(ErrorCodes.Forbidden,
                    () => EvidenceController(db, disabledId, path)
                        .GetPaged(new CustomerSalesInvoiceEvidenceQuery()))).Message);
            Assert.Equal(CustomerSalesInvoiceAuthorizationRules.DisabledIdentityText,
                (await AssertDenied(ErrorCodes.Forbidden,
                    () => EvidenceController(db, disabledId, path).Record(invoice.Id))).Message);
            Assert.Equal(CustomerSalesInvoiceAuthorizationRules.DeletedIdentityText,
                (await AssertDenied(ErrorCodes.Unauthorized,
                    () => EvidenceController(db, deletedId, path).Record(invoice.Id))).Message);
            Assert.Equal(CustomerSalesInvoiceAuthorizationRules.DeletedIdentityText,
                (await AssertDenied(ErrorCodes.Unauthorized,
                    () => EvidenceController(db, 999_999L, path).GetById(invoice.Id))).Message);
            Assert.Equal(CustomerSalesInvoiceAuthorizationRules.MenuDeniedText,
                (await AssertDenied(ErrorCodes.Forbidden,
                    () => EvidenceController(db, revokedId, path)
                        .GetPaged(new CustomerSalesInvoiceEvidenceQuery()))).Message);
            Assert.Equal(CustomerSalesInvoiceAuthorizationRules.MenuDeniedText,
                (await AssertDenied(ErrorCodes.Forbidden,
                    () => EvidenceController(db, revokedId, path).Record(invoice.Id))).Message);
        }

        // 零写入：被拒身份绝不改写任何发票 / 分摊行。
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.StatusDraft, invoice.Status);
        Assert.Equal(1, await db.CustomerSalesInvoiceEvidences.CountAsync());
        Assert.Equal(0, await db.CustomerSalesInvoiceAllocations.CountAsync());
    }

    // ==================== 4. 受限业务员：越界非披露拒绝 + 自有范围许可 ====================

    [Fact]
    public async Task Evidence_restricted_scope_denies_foreign_sources_non_disclosing_and_allows_own_flow()
    {
        using var db = TestDbFactory.Create();
        var mine = SeedCustomer(db, "C487-MINE", "本人客户");
        var foreign = SeedCustomer(db, "C487-FOREIGN", "范围外客户");
        var myOrder = SeedOrder(db, "SO-487-MINE", mine.Id);
        var foreignOrder = SeedOrder(db, "SO-487-FOREIGN", foreign.Id);
        var foreignInvoice = SeedInvoice(db, "INV-487-FOREIGN", foreign.Id, customerName: "范围外客户");
        var myInvoice = SeedInvoice(db, "INV-487-MINE", mine.Id, customerName: "本人客户");

        var (userId, _) = SeedOperator(db, privileged: false);
        SeedSalesman(db, userId, mine.Id);

        foreach (var path in new string?[] { null, EvidencePath })
        {
            var ctl = EvidenceController(db, userId, path);

            // 台账先按权威客户范围下推：范围外发票既不出现、也不暴露存在性。
            var page = AssertOk<PagedResult<CustomerSalesInvoiceEvidenceDto>>(
                await ctl.GetPaged(new CustomerSalesInvoiceEvidenceQuery()));
            Assert.Equal(myInvoice.Id, Assert.Single(page.Items).Id);

            // 范围外发票：受控非披露拒绝（不泄露发票号 / 客户名 / 金额）。
            var foreignDenied = await AssertDenied(ErrorCodes.Forbidden, () => ctl.GetById(foreignInvoice.Id));
            Assert.Equal(CustomerSalesInvoiceAuthorizationRules.ScopeDeniedText, foreignDenied.Message);
            Assert.DoesNotContain("INV-487-FOREIGN", foreignDenied.Message);
            Assert.DoesNotContain("范围外客户", foreignDenied.Message);

            // 范围外客户的拟提议发票：写入之前拒绝，零新增。
            Assert.Equal(CustomerSalesInvoiceAuthorizationRules.ScopeDeniedText,
                (await AssertDenied(ErrorCodes.Forbidden,
                    () => ctl.Create(EvidenceDto(foreign.Id, "INV-487-FOREIGN-NEW")))).Message);

            // 范围外销售订单的拟提议分摊：受控非披露拒绝（受限账号无法证明来源归属）。
            var allocationDenied = await AssertDenied(ErrorCodes.Forbidden,
                () => ctl.SaveAllocations(myInvoice.Id, AllocationRequest(foreignOrder.Id, 250m)));
            Assert.Equal(CustomerSalesInvoiceAuthorizationRules.ScopeDeniedText, allocationDenied.Message);
            Assert.DoesNotContain("SO-487-FOREIGN", allocationDenied.Message);
        }

        // 零写入：越界调用绝不改写任何发票 / 分摊行。
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.StatusDraft, foreignInvoice.Status);
        Assert.Equal(0, await db.CustomerSalesInvoiceAllocations.CountAsync());
        Assert.Equal(2, await db.CustomerSalesInvoiceEvidences.CountAsync());

        // 自有范围许可：新建 → 分摊 → 登记 → 作废（授权用户保留完整生命周期，金额 / 状态口径不变）。
        var own = EvidenceController(db, userId, null);
        var created = AssertOk<CustomerSalesInvoiceEvidenceDto>(
            await own.Create(EvidenceDto(mine.Id, "INV-487-OWN-NEW")));
        AssertOk<CustomerSalesInvoiceEvidenceDto>(
            await own.SaveAllocations(created.Id, AllocationRequest(myOrder.Id, 250m)));

        var recorded = AssertOk<CustomerSalesInvoiceEvidenceDto>(await own.Record(created.Id));
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.StatusRecorded, recorded.Status);

        var voided = AssertOk<CustomerSalesInvoiceEvidenceDto>(await own.Void(created.Id,
            new CustomerSalesInvoiceVoidRequest { Reason = "录错重登" }));
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.StatusVoided, voided.Status);

        // 作废保留原始金额与历史（不物理删除、不静默改写）。
        var history = await db.CustomerSalesInvoiceAllocations.AsNoTracking()
            .Where(a => a.CustomerSalesInvoiceEvidenceId == created.Id).ToListAsync();
        Assert.Equal(250m, Assert.Single(history).AllocatedAmount);
    }

    // ==================== 5. 收款分摊证据：身份 / 菜单 / 范围 + 无服务直调旁路 ====================

    [Fact]
    public async Task Collection_allocation_entry_requires_live_identity_and_menu_independently_of_request_shape()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C487-COL", "收款客户");
        var invoice = SeedInvoice(db, "INV-487-COL", customer.Id,
            status: CustomerSalesInvoiceEvidenceRules.StatusRecorded);
        var receipt = SeedReceipt(db, "SK-487-COL", customer.Id);

        // 缺身份：空路径 / 已赋值路径 / 其它路径全部 fail closed（登记 / 作废不再退回无授权服务直调）。
        foreach (var path in new string?[] { null, AllocationPath, EvidencePath })
        {
            var ctl = AllocationController(db, null, path);
            Assert.Equal(CustomerReceiptLifecycleRules.UnauthorizedText,
                (await AssertDenied(ErrorCodes.Unauthorized,
                    () => ctl.GetPaged(new CustomerSalesInvoiceCollectionAllocationQuery()))).Message);
            Assert.Equal(ErrorCodes.Unauthorized, (await AssertDenied(ErrorCodes.Unauthorized,
                () => ctl.Metadata())).Code);
            Assert.Equal(ErrorCodes.Unauthorized, (await AssertDenied(ErrorCodes.Unauthorized,
                () => ctl.ReceiptSummary(receipt.Id))).Code);
            Assert.Equal(ErrorCodes.Unauthorized, (await AssertDenied(ErrorCodes.Unauthorized,
                () => ctl.Create(CollectionDto(invoice.Id, receipt.Id)))).Code);
            Assert.Equal(ErrorCodes.Unauthorized, (await AssertDenied(ErrorCodes.Unauthorized,
                () => ctl.Void(1, new CustomerSalesInvoiceCollectionAllocationVoidRequest
                {
                    Reason = "越权"
                }))).Code);
        }
        Assert.Equal(0, await db.CustomerSalesInvoiceCollectionAllocations.CountAsync());

        // 停用 → 权限不足；已删除 → 未认证；撤销菜单（收款分摊证据不承认任何菜单豁免）→ 权限不足。
        var (disabledId, _) = SeedOperator(db);
        (await db.SysUsers.SingleAsync(u => u.Id == disabledId)).Status = UserStatus.Disabled;

        var (deletedId, _) = SeedOperator(db);
        (await db.SysUsers.SingleAsync(u => u.Id == deletedId)).IsDeleted = true;

        var (revokedId, _) = SeedOperator(db);
        RevokeMenus(db, revokedId);
        await db.SaveChangesAsync();

        Assert.Equal(CustomerReceiptLifecycleRules.DisabledIdentityText,
            (await AssertDenied(ErrorCodes.Forbidden,
                () => AllocationController(db, disabledId, null)
                    .Create(CollectionDto(invoice.Id, receipt.Id)))).Message);
        Assert.Equal(CustomerReceiptLifecycleRules.DeletedIdentityText,
            (await AssertDenied(ErrorCodes.Unauthorized,
                () => AllocationController(db, deletedId, null)
                    .Create(CollectionDto(invoice.Id, receipt.Id)))).Message);
        Assert.Equal(CustomerReceiptLifecycleRules.MenuDeniedText,
            (await AssertDenied(ErrorCodes.Forbidden,
                () => AllocationController(db, revokedId, null)
                    .Create(CollectionDto(invoice.Id, receipt.Id)))).Message);
        Assert.Equal(0, await db.CustomerSalesInvoiceCollectionAllocations.CountAsync());
    }

    // ==================== 6. 收款分摊证据：越界非披露拒绝 + 自有范围登记 / 作废 ====================

    [Fact]
    public async Task Collection_allocation_restricted_scope_denies_foreign_receipts_and_allows_own_flow()
    {
        using var db = TestDbFactory.Create();
        var mine = SeedCustomer(db, "C487-COL-MINE", "本人收款客户");
        var foreign = SeedCustomer(db, "C487-COL-FOREIGN", "范围外收款客户");
        var myInvoice = SeedInvoice(db, "INV-487-COL-MINE", mine.Id,
            status: CustomerSalesInvoiceEvidenceRules.StatusRecorded);
        var myReceipt = SeedReceipt(db, "SK-487-COL-MINE", mine.Id);
        var foreignReceipt = SeedReceipt(db, "SK-487-COL-FOREIGN", foreign.Id);

        var (salesmanId, _) = SeedOperator(db, privileged: false);
        SeedSalesman(db, salesmanId, mine.Id);

        var expectedRows = 0;
        foreach (var path in new string?[] { null, AllocationPath })
        {
            var ctl = AllocationController(db, salesmanId, path, "本人业务员");

            // 越界收款单：与「不存在」同一条不披露错误（不泄露范围外 Id / 单号 / 金额）。
            Assert.Equal(CustomerReceiptLifecycleRules.AllocationNotFoundText,
                (await AssertDenied(ErrorCodes.NotFound,
                    () => ctl.Create(CollectionDto(myInvoice.Id, foreignReceipt.Id)))).Message);
            Assert.Equal(CustomerReceiptLifecycleRules.AllocationNotFoundText,
                (await AssertDenied(ErrorCodes.NotFound, () => ctl.ReceiptSummary(foreignReceipt.Id))).Message);
            Assert.Equal(expectedRows, await db.CustomerSalesInvoiceCollectionAllocations.CountAsync());

            // 自有范围许可：登记（登记人取登录账号）→ 作废（保留原始金额与历史）。
            var created = AssertOk<CustomerSalesInvoiceCollectionAllocationDto>(
                await ctl.Create(CollectionDto(myInvoice.Id, myReceipt.Id)));
            Assert.Equal(CustomerSalesInvoiceCollectionAllocationRules.StatusActive, created.Status);
            Assert.Equal(100m, created.AllocatedAmount);
            Assert.Equal("本人业务员", created.AllocatedBy);

            var voided = AssertOk<CustomerSalesInvoiceCollectionAllocationDto>(await ctl.Void(created.Id,
                new CustomerSalesInvoiceCollectionAllocationVoidRequest { Reason = "录错" }));
            Assert.Equal(CustomerSalesInvoiceCollectionAllocationRules.StatusVoided, voided.Status);
            Assert.Equal(100m, voided.AllocatedAmount);
            expectedRows++;
        }

        // 作废保留历史（行仍在、金额不变、发票证据未被改写）。
        Assert.Equal(2, await db.CustomerSalesInvoiceCollectionAllocations.CountAsync());
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.StatusRecorded, myInvoice.Status);
        Assert.Equal(1000m, myInvoice.GrossAmount);
    }
}
