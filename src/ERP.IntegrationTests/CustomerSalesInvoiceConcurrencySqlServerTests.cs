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
/// ERP-383 客户销项发票并发与授权 SQL Server 集成测试（专用 NEWERP_AUTOTEST 护栏）。
/// <list type="number">
/// <item><b>真实控制器 + 真实身份</b>：以既有「销售订单」菜单授权 + 既有业务员客户数据范围口径驱动真实
/// <see cref="CustomerSalesInvoiceEvidenceController"/>，验证「发票已存储客户 + 全部来源订单 + 交叉引用单证」
/// 都在本人客户范围内才可读 / 可写，混源 / 越界 / 来源不可证明都 fail closed，撤销授权后立即收敛；
/// 不新增任何用户授权、绝无匿名 / 管理员兜底；被拒绝的请求不改写任何行。</item>
/// <item><b>两个独立连接竞态（至少两组）</b>：① 两张不同收款单并发分摊同一发票 → 同币种收款容量竞争只允许一方成功；
/// ② 两张不同草稿发票并发分摊同一销售订单 → 订单发票容量竞争只允许一方成功；另外覆盖
/// 「收款分摊登记 vs 发票作废」「草稿修改 vs 登记」「分摊整体替换 vs 登记」的一致性结果。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>保留既有库存来源单据审计与原始失败日志：本测试只新增自己的证据行，不清理 / 不删除任何既有行。
/// 构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class CustomerSalesInvoiceConcurrencySqlServerTests
    : IClassFixture<CustomerSalesInvoiceConcurrencySqlServerFixture>
{
    private readonly CustomerSalesInvoiceConcurrencySqlServerFixture _fixture;

    public CustomerSalesInvoiceConcurrencySqlServerTests(CustomerSalesInvoiceConcurrencySqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(CustomerSalesInvoiceConcurrencySqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 控制器 / 双连接脚手架 ====================

    /// <summary>绑定真实 HTTP 请求管线（<c>Request.Path</c> 已赋值）+ 真实身份，使实时授权按真实请求口径生效。</summary>
    private static CustomerSalesInvoiceEvidenceController NewController(ErpDbContext db, long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        http.Request.Path = "/api/customer-sales-invoices";
        return new CustomerSalesInvoiceEvidenceController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    /// <summary>用一条独立连接执行控制器动作（每次调用各自 DbContext / 连接 / 事务）。</summary>
    private async Task<(bool Success, string Error)> TryControllerAsync(
        long? userId, Func<CustomerSalesInvoiceEvidenceController, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await action(NewController(db, userId));
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>用一条独立连接登记「收款单 → 客户销项发票」收款分摊证据（与发票 / 收款单行锁竞争）。</summary>
    private async Task<(bool Success, string Error)> TryAssignReceiptAsync(
        long invoiceId, long receiptId, decimal amount)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await CustomerSalesInvoiceCollectionAllocationService.CreateAsync(db,
                new CustomerSalesInvoiceCollectionAllocationSaveDto
                {
                    CustomerSalesInvoiceEvidenceId = invoiceId,
                    ReceiptId = receiptId,
                    AllocatedAmount = amount
                }, "tester");
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>两条独立连接在同一起点同时发起动作（各自独立 DbContext / 连接 / 事务）。</summary>
    private static async Task<List<(bool Success, string Error)>> RaceAsync(
        Func<Task<(bool Success, string Error)>> first,
        Func<Task<(bool Success, string Error)>> second)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<(bool Success, string Error)> Run(Func<Task<(bool Success, string Error)>> action)
        {
            await gate.Task;
            return await action();
        }

        var left = Run(first);
        var right = Run(second);
        gate.SetResult();
        return (await Task.WhenAll(left, right)).ToList();
    }

    // ==================== 种子助手（EF 生成身份主键，绝不清理既有行） ====================

    private static async Task<BaseCustomer> SeedCustomerAsync(ErpDbContext db, string code, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code, CustomerName = name, Status = 1, CreditStatus = "正常"
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<SalesOrder> SeedSalesOrderAsync(
        ErpDbContext db, string orderNo, long customerId, decimal totalAmount, Currency currency = Currency.CNY)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today,
            CustomerId = customerId,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static async Task<TradeDocument> SeedTradeDocumentAsync(
        ErpDbContext db, string docNo, long? customerId)
    {
        var document = new TradeDocument
        {
            DocNo = docNo,
            DocType = "商业发票",
            IssueDate = DateTime.Today,
            CustomerId = customerId,
            CustomerName = customerId is > 0 ? "集成单证客户" : string.Empty,
            Amount = 100m,
            Currency = "CNY",
            Status = "已制作"
        };
        db.TradeDocuments.Add(document);
        await db.SaveChangesAsync();
        return document;
    }

    private static async Task<CustomerSalesInvoiceEvidence> SeedInvoiceAsync(
        ErpDbContext db, string number, long customerId, decimal gross,
        int status = CustomerSalesInvoiceEvidenceRules.StatusDraft, string currency = "CNY",
        long? tradeDocumentId = null)
    {
        var invoice = new CustomerSalesInvoiceEvidence
        {
            InvoiceType = CustomerSalesInvoiceEvidenceRules.InvoiceTypeOrdinary,
            InvoiceCode = string.Empty,
            InvoiceNumber = number,
            NormalizedInvoiceNumber = CustomerSalesInvoiceEvidenceRules.NormalizeIdentityPart(number),
            InvoiceDate = DateTime.Today,
            CustomerId = customerId,
            CustomerCode = "INT_CSI383_CUST",
            CustomerName = "集成发票客户",
            Currency = currency,
            NetAmount = gross,
            TaxAmount = 0m,
            GrossAmount = gross,
            TradeDocumentId = tradeDocumentId,
            TradeDocumentNo = tradeDocumentId is null ? string.Empty : $"INT_CSI383_DOC_{tradeDocumentId}",
            TradeDocumentDocType = tradeDocumentId is null ? string.Empty : "商业发票",
            Status = status
        };
        db.CustomerSalesInvoiceEvidences.Add(invoice);
        await db.SaveChangesAsync();
        return invoice;
    }

    private static async Task SeedInvoiceAllocationAsync(
        ErpDbContext db, long invoiceId, SalesOrder order, decimal amount, string currency = "CNY")
    {
        db.CustomerSalesInvoiceAllocations.Add(new CustomerSalesInvoiceAllocation
        {
            CustomerSalesInvoiceEvidenceId = invoiceId,
            SalesOrderId = order.Id,
            OrderNo = order.OrderNo ?? string.Empty,
            OrderDate = order.OrderDate,
            OrderStatus = (int)order.Status,
            OrderCurrency = currency,
            CustomerId = order.CustomerId,
            CustomerCode = "INT_CSI383_CUST",
            CustomerName = "集成发票客户",
            AllocatedAmount = amount,
            Currency = currency,
            SortOrder = 1
        });
        await db.SaveChangesAsync();
    }

    private static async Task<FinanceReceipt> SeedReceiptAsync(
        ErpDbContext db, string receiptNo, long customerId, decimal amount,
        DocumentStatus status = DocumentStatus.Approved)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = DateTime.Today,
            CustomerId = customerId,
            Amount = amount,
            Currency = Currency.CNY,
            PaymentMethod = PaymentMethod.BankTransfer,
            BankAccount = "INT-CSI383-BANK",
            Status = status
        };
        db.FinanceReceipts.Add(receipt);
        await db.SaveChangesAsync();
        return receipt;
    }

    /// <summary>
    /// 播种真实操作账号（既有「销售订单」菜单 + 既有销售员客户范围）：<paramref name="privileged"/> 为 true 时复用
    /// 系统内置角色口径（真正不受限的授权账号），否则为映射到 <paramref name="inScopeCustomerId"/> 客户的受限业务员。
    /// 绝不新增任何用户授权，只复用既有菜单编码。
    /// </summary>
    private static async Task<(long UserId, long RoleId)> SeedInvoiceOperatorAsync(
        ErpDbContext db, long inScopeCustomerId, bool withMenu = true,
        UserStatus status = UserStatus.Enabled, bool privileged = false)
    {
        var code = $"csi383-op-{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1
        };
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
            RoleName = "销项发票操作角色",
            RoleCode = $"Csi383Op-{Guid.NewGuid():N}",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (withMenu)
        {
            var menuId = await db.SysMenus.AsNoTracking()
                .Where(m => m.MenuCode == CustomerSalesInvoiceAuthorizationRules.RequiredMenuCode && !m.IsDeleted)
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

    private static async Task RevokeInvoiceMenuAsync(ErpDbContext db, long roleId)
    {
        foreach (var grant in await db.SysRoleMenus.Where(g => g.RoleId == roleId && !g.IsDeleted).ToListAsync())
            grant.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    // ==================== 只读复核助手 ====================

    private async Task<CustomerSalesInvoiceEvidence> ReloadInvoiceAsync(long invoiceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.CustomerSalesInvoiceEvidences.AsNoTracking().SingleAsync(i => i.Id == invoiceId);
    }

    private async Task<decimal> ActiveOrderAllocationSumAsync(long orderId)
    {
        await using var db = _fixture.CreateDbContext();
        var rows = await (from allocation in db.CustomerSalesInvoiceAllocations.AsNoTracking()
                          join owner in db.CustomerSalesInvoiceEvidences.AsNoTracking()
                              on allocation.CustomerSalesInvoiceEvidenceId equals owner.Id
                          where !allocation.IsDeleted && !owner.IsDeleted
                                && owner.Status != CustomerSalesInvoiceEvidenceRules.StatusVoided
                                && allocation.SalesOrderId == orderId
                          select allocation.AllocatedAmount).ToListAsync();
        return rows.Sum();
    }

    private async Task<decimal> ActiveReceiptAllocationSumAsync(long invoiceId)
    {
        await using var db = _fixture.CreateDbContext();
        var rows = await db.CustomerSalesInvoiceCollectionAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.CustomerSalesInvoiceEvidenceId == invoiceId
                        && a.Status == CustomerSalesInvoiceCollectionAllocationRules.StatusActive)
            .Select(a => a.AllocatedAmount).ToListAsync();
        return rows.Sum();
    }

    private async Task<int> ReceiptAllocationRowCountAsync(long invoiceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.CustomerSalesInvoiceCollectionAllocations.AsNoTracking()
            .CountAsync(a => !a.IsDeleted && a.CustomerSalesInvoiceEvidenceId == invoiceId);
    }

    private async Task<int> InvoiceAllocationRowCountAsync(long invoiceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.CustomerSalesInvoiceAllocations.AsNoTracking()
            .CountAsync(a => !a.IsDeleted && a.CustomerSalesInvoiceEvidenceId == invoiceId);
    }

    private static CustomerSalesInvoiceAllocationSaveRequest Lines(
        params (long OrderId, decimal Amount)[] lines)
        => new()
        {
            Lines = lines.Select(l => new CustomerSalesInvoiceAllocationSaveDto
            {
                SalesOrderId = l.OrderId,
                AllocatedAmount = l.Amount
            }).ToList()
        };

    // ==================== 1. 两个独立连接竞态：发票收款容量 ====================

    [Fact]
    public async Task Race_TwoDistinctReceipts_OneInvoice_OnlyOneSucceeds()
    {
        Guard();
        long invoiceId, firstReceiptId, secondReceiptId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_CSI383_C1_{Guid.NewGuid():N}", "集成客户A");
            invoiceId = (await SeedInvoiceAsync(seed, $"INT_CSI383_INV1_{Guid.NewGuid():N}", customer.Id, 100m,
                CustomerSalesInvoiceEvidenceRules.StatusRecorded)).Id;
            firstReceiptId = (await SeedReceiptAsync(seed, $"INT_CSI383_SK1_{Guid.NewGuid():N}", customer.Id, 100m)).Id;
            secondReceiptId = (await SeedReceiptAsync(seed, $"INT_CSI383_SK2_{Guid.NewGuid():N}", customer.Id, 100m)).Id;
        }

        // 两条独立连接：两张不同收款单并发分摊同一发票（发票同币种收款容量竞争）
        var results = await RaceAsync(
            () => TryAssignReceiptAsync(invoiceId, firstReceiptId, 100m),
            () => TryAssignReceiptAsync(invoiceId, secondReceiptId, 100m));

        Assert.True(results[0].Success || results[1].Success, $"{results[0].Error} | {results[1].Error}");
        Assert.True(results[0].Success != results[1].Success);

        // 发票容量 100 绝不被两张收款单合计突破；失败方不留下任何分摊行
        Assert.Equal(100m, await ActiveReceiptAllocationSumAsync(invoiceId));
        Assert.Equal(1, await ReceiptAllocationRowCountAsync(invoiceId));
    }

    [Fact]
    public async Task Race_TwoInvoices_OneOrder_OnlyOneSucceeds()
    {
        Guard();
        long userId, orderId, firstInvoiceId, secondInvoiceId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_CSI383_C2_{Guid.NewGuid():N}", "集成客户B");
            userId = (await SeedInvoiceOperatorAsync(seed, customer.Id, privileged: true)).UserId;
            orderId = (await SeedSalesOrderAsync(seed, $"INT_CSI383_SO2_{Guid.NewGuid():N}", customer.Id, 100m)).Id;
            firstInvoiceId = (await SeedInvoiceAsync(seed, $"INT_CSI383_INV2A_{Guid.NewGuid():N}", customer.Id, 100m)).Id;
            secondInvoiceId = (await SeedInvoiceAsync(seed, $"INT_CSI383_INV2B_{Guid.NewGuid():N}", customer.Id, 100m)).Id;
        }

        // 两条独立连接：两张不同草稿发票并发分摊同一销售订单（订单发票容量竞争）
        var results = await RaceAsync(
            () => TryControllerAsync(userId, ctl => ctl.SaveAllocations(firstInvoiceId, Lines((orderId, 100m)))),
            () => TryControllerAsync(userId, ctl => ctl.SaveAllocations(secondInvoiceId, Lines((orderId, 100m)))));

        Assert.True(results[0].Success || results[1].Success, $"{results[0].Error} | {results[1].Error}");
        Assert.True(results[0].Success != results[1].Success);

        // 订单发票容量 100 绝不被两张发票合计突破；失败方不留下任何分摊行
        Assert.Equal(100m, await ActiveOrderAllocationSumAsync(orderId));
        Assert.Equal(1, await InvoiceAllocationRowCountAsync(firstInvoiceId)
                        + await InvoiceAllocationRowCountAsync(secondInvoiceId));
    }

    // ==================== 2. 两个独立连接竞态：作废 / 登记 / 分摊替换 ====================

    [Fact]
    public async Task Race_ReceiptAssignmentVersusInvoiceVoid_SerializedConsistentResult()
    {
        Guard();
        long userId, invoiceId, receiptId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_CSI383_C3_{Guid.NewGuid():N}", "集成客户C");
            userId = (await SeedInvoiceOperatorAsync(seed, customer.Id, privileged: true)).UserId;
            invoiceId = (await SeedInvoiceAsync(seed, $"INT_CSI383_INV3_{Guid.NewGuid():N}", customer.Id, 100m,
                CustomerSalesInvoiceEvidenceRules.StatusRecorded)).Id;
            receiptId = (await SeedReceiptAsync(seed, $"INT_CSI383_SK3_{Guid.NewGuid():N}", customer.Id, 100m)).Id;
        }

        // 两条独立连接：收款分摊登记（发票行锁 → 收款单行锁） vs 发票作废（发票行锁）
        var results = await RaceAsync(
            () => TryAssignReceiptAsync(invoiceId, receiptId, 100m),
            () => TryControllerAsync(userId, ctl => ctl.Void(invoiceId,
                new CustomerSalesInvoiceVoidRequest { Reason = "并发作废" })));

        var invoice = await ReloadInvoiceAsync(invoiceId);
        Assert.True(results[1].Success, results[1].Error);
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.StatusVoided, invoice.Status);
        Assert.Equal("并发作废", invoice.VoidReason);
        Assert.NotNull(invoice.VoidedAt);
        Assert.Equal(100m, invoice.GrossAmount);

        // 分摊行要么完整登记、要么完全不存在（无半成品行）；发票作废只让发票证据失效，绝不改写分摊行原始值
        var rowCount = await ReceiptAllocationRowCountAsync(invoiceId);
        Assert.Equal(results[0].Success ? 1 : 0, rowCount);
        if (results[0].Success) Assert.Equal(100m, await ActiveReceiptAllocationSumAsync(invoiceId));
    }

    [Fact]
    public async Task Race_DraftEditVersusRecord_SerializedConsistentResult()
    {
        Guard();
        long userId, customerId, invoiceId;
        string number;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_CSI383_C4_{Guid.NewGuid():N}", "集成客户D");
            customerId = customer.Id;
            userId = (await SeedInvoiceOperatorAsync(seed, customer.Id, privileged: true)).UserId;
            number = $"INT_CSI383_INV4_{Guid.NewGuid():N}";
            invoiceId = (await SeedInvoiceAsync(seed, number, customer.Id, 100m)).Id;
        }

        var edit = new CustomerSalesInvoiceEvidenceSaveDto
        {
            InvoiceType = CustomerSalesInvoiceEvidenceRules.InvoiceTypeOrdinary,
            InvoiceNumber = number,
            InvoiceDate = DateTime.Today,
            CustomerId = customerId,
            Currency = "CNY",
            NetAmount = 200m,
            TaxAmount = 0m,
            GrossAmount = 200m,
            Remark = "并发修改"
        };

        // 两条独立连接：草稿修改 vs 登记（同一发票行锁串行化）
        var results = await RaceAsync(
            () => TryControllerAsync(userId, ctl => ctl.Update(invoiceId, edit)),
            () => TryControllerAsync(userId, ctl => ctl.Record(invoiceId)));

        var invoice = await ReloadInvoiceAsync(invoiceId);
        Assert.True(results[1].Success, $"{results[0].Error} | {results[1].Error}");
        // 登记总是最终状态：绝不出现「已登记却仍被并发改写」的撕裂状态
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.StatusRecorded, invoice.Status);
        Assert.NotNull(invoice.RecordedAt);
        if (results[0].Success)
        {
            Assert.Equal(200m, invoice.GrossAmount);
            Assert.Equal("并发修改", invoice.Remark);
        }
        else
        {
            Assert.Equal(100m, invoice.GrossAmount);
        }
    }

    [Fact]
    public async Task Race_AllocationReplacementVersusRecord_SerializedConsistentResult()
    {
        Guard();
        long userId, orderId, invoiceId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_CSI383_C5_{Guid.NewGuid():N}", "集成客户E");
            userId = (await SeedInvoiceOperatorAsync(seed, customer.Id, privileged: true)).UserId;
            var order = await SeedSalesOrderAsync(seed, $"INT_CSI383_SO5_{Guid.NewGuid():N}", customer.Id, 1000m);
            orderId = order.Id;
            invoiceId = (await SeedInvoiceAsync(seed, $"INT_CSI383_INV5_{Guid.NewGuid():N}", customer.Id, 100m)).Id;
            await SeedInvoiceAllocationAsync(seed, invoiceId, order, 40m);
        }

        // 两条独立连接：分摊整体替换 vs 登记（订单行锁 → 发票行锁）
        var results = await RaceAsync(
            () => TryControllerAsync(userId, ctl => ctl.SaveAllocations(invoiceId, Lines((orderId, 100m)))),
            () => TryControllerAsync(userId, ctl => ctl.Record(invoiceId)));

        var invoice = await ReloadInvoiceAsync(invoiceId);
        var sum = await ActiveOrderAllocationSumAsync(orderId);
        Assert.True(results[1].Success, results[1].Error);
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.StatusRecorded, invoice.Status);
        Assert.Equal(results[0].Success ? 100m : 40m, sum);
        Assert.True(sum <= invoice.GrossAmount);
        Assert.Equal(1, await InvoiceAllocationRowCountAsync(invoiceId));
    }

    // ==================== 3. 真实身份实时授权（无新增授权、无匿名兜底） ====================

    [Fact]
    public async Task Realtime_authorization_denies_foreign_mixed_and_owned_by_trade_document_sources()
    {
        Guard();
        long ownerUserId, ownerRoleId, foreignUserId, privilegedUserId, disabledUserId;
        long ownOrderId, ownInvoiceId, foreignInvoiceId, mixedInvoiceId, foreignDocumentInvoiceId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var ownCustomer = await SeedCustomerAsync(seed, $"INT_CSI383_C6A_{Guid.NewGuid():N}", "本人客户");
            var foreignCustomer = await SeedCustomerAsync(seed, $"INT_CSI383_C6B_{Guid.NewGuid():N}", "他人客户");
            var privilegedCustomer = await SeedCustomerAsync(seed, $"INT_CSI383_C6C_{Guid.NewGuid():N}", "特权客户");
            var disabledCustomer = await SeedCustomerAsync(seed, $"INT_CSI383_C6D_{Guid.NewGuid():N}", "禁用账号客户");

            var owner = await SeedInvoiceOperatorAsync(seed, ownCustomer.Id);
            ownerUserId = owner.UserId;
            ownerRoleId = owner.RoleId;
            foreignUserId = (await SeedInvoiceOperatorAsync(seed, foreignCustomer.Id)).UserId;
            // 特权账号单独映射到自己的客户，避免覆盖受限业务员的客户归属（真实权限口径不变）
            privilegedUserId = (await SeedInvoiceOperatorAsync(seed, privilegedCustomer.Id, privileged: true)).UserId;
            disabledUserId = (await SeedInvoiceOperatorAsync(seed, disabledCustomer.Id,
                status: UserStatus.Disabled)).UserId;

            var ownOrder = await SeedSalesOrderAsync(seed, $"INT_CSI383_SO6A_{Guid.NewGuid():N}", ownCustomer.Id, 1000m);
            var foreignOrder = await SeedSalesOrderAsync(seed, $"INT_CSI383_SO6B_{Guid.NewGuid():N}",
                foreignCustomer.Id, 1000m);
            var mixedOwnOrder = await SeedSalesOrderAsync(seed, $"INT_CSI383_SO6C_{Guid.NewGuid():N}",
                ownCustomer.Id, 1000m);
            ownOrderId = ownOrder.Id;

            var foreignDocument = await SeedTradeDocumentAsync(seed, $"INT_CSI383_DOC6_{Guid.NewGuid():N}",
                foreignCustomer.Id);

            ownInvoiceId = (await SeedInvoiceAsync(seed, $"INT_CSI383_INV6A_{Guid.NewGuid():N}",
                ownCustomer.Id, 100m)).Id;
            await SeedInvoiceAllocationAsync(seed, ownInvoiceId, ownOrder, 40m);

            foreignInvoiceId = (await SeedInvoiceAsync(seed, $"INT_CSI383_INV6B_{Guid.NewGuid():N}",
                foreignCustomer.Id, 100m)).Id;
            await SeedInvoiceAllocationAsync(seed, foreignInvoiceId, foreignOrder, 40m);

            // 混源：本人客户发票同时分摊本人与他人的订单 → 每一个相关客户都必须允许
            mixedInvoiceId = (await SeedInvoiceAsync(seed, $"INT_CSI383_INV6C_{Guid.NewGuid():N}",
                ownCustomer.Id, 100m)).Id;
            await SeedInvoiceAllocationAsync(seed, mixedInvoiceId, mixedOwnOrder, 30m);
            await SeedInvoiceAllocationAsync(seed, mixedInvoiceId, foreignOrder, 30m);

            // 显式交叉引用归属客户在范围外的单证 → 对受限账号 fail closed
            foreignDocumentInvoiceId = (await SeedInvoiceAsync(seed, $"INT_CSI383_INV6D_{Guid.NewGuid():N}",
                ownCustomer.Id, 100m, tradeDocumentId: foreignDocument.Id)).Id;
        }

        // 本人客户来源：可读
        var ownRead = await TryControllerAsync(ownerUserId, ctl => ctl.GetById(ownInvoiceId));
        Assert.True(ownRead.Success, ownRead.Error);

        // 范围外 / 混源 / 单证归属越界：fail closed
        Assert.False((await TryControllerAsync(ownerUserId, ctl => ctl.GetById(foreignInvoiceId))).Success);
        Assert.False((await TryControllerAsync(ownerUserId, ctl => ctl.GetById(mixedInvoiceId))).Success);
        Assert.False((await TryControllerAsync(ownerUserId, ctl => ctl.GetById(foreignDocumentInvoiceId))).Success);
        Assert.False((await TryControllerAsync(foreignUserId, ctl => ctl.GetById(ownInvoiceId))).Success);

        // 无身份 / 禁用账号：fail closed（绝不匿名 / 管理员兜底）
        Assert.False((await TryControllerAsync(null, ctl => ctl.GetById(ownInvoiceId))).Success);
        Assert.False((await TryControllerAsync(disabledUserId, ctl => ctl.GetById(ownInvoiceId))).Success);

        // 特权账号保留历史 / 任意来源发票可读（真正不受限的授权账号，而非新增授权）
        var privilegedRead = await TryControllerAsync(privilegedUserId, ctl => ctl.GetById(mixedInvoiceId));
        Assert.True(privilegedRead.Success, privilegedRead.Error);

        // 撤销菜单后立即收敛；被拒绝的请求不改写任何发票 / 分摊行
        await using (var revoke = _fixture.CreateDbContext())
            await RevokeInvoiceMenuAsync(revoke, ownerRoleId);
        Assert.False((await TryControllerAsync(ownerUserId, ctl => ctl.GetById(ownInvoiceId))).Success);
        Assert.False((await TryControllerAsync(ownerUserId, ctl => ctl.Record(ownInvoiceId))).Success);
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.StatusDraft, (await ReloadInvoiceAsync(ownInvoiceId)).Status);
        Assert.Equal(40m, await ActiveOrderAllocationSumAsync(ownOrderId));
        Assert.Equal(1, await InvoiceAllocationRowCountAsync(ownInvoiceId));
        // 混源发票的既有分摊行原样保留（被拒绝的请求绝不改写任何分摊行）
        Assert.Equal(2, await InvoiceAllocationRowCountAsync(mixedInvoiceId));
    }
}

/// <summary>
/// ERP-383 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class CustomerSalesInvoiceConcurrencySqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_CUSTOMERSALESINVOICECONCURRENCY_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-383] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-383] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }
}

/// <summary>专用目标护栏的 fail-closed 单元覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class CustomerSalesInvoiceConcurrencyTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => CustomerSalesInvoiceConcurrencySqlServerFixture.AssertDedicatedTarget(connection));
}
