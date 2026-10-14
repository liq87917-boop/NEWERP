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
using System.Data.Common;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-509 供应商付款单 / 货款申请单「标量金额存储边界」的真实 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）：
/// 用与运行时完全相同的调用路径（真实控制器 + 真实 HTTP 身份 + 真实 SQL Server 上下文）验证：
/// <list type="number">
/// <item>实际列精度：EF 模型未显式 <c>HasPrecision</c> → 真实 <c>INFORMATION_SCHEMA</c> 列为
/// <c>decimal(18,2)</c>，与 <see cref="FinancePersistedAmountRules"/> 常量同源；恰好等于上限的金额原样往返，
/// 超过上限的金额连原始 EF 写入都被真实列拒绝；</item>
/// <item>创建 / 修改：受权账号提交「取整进位越过上限 / 1e16 / 极大十进制」一律在**单号预约与任何写入之前**
/// 被受控拒绝（受权同一控制器随后合法创建成功，证明拒绝原因是金额而非授权，且零表头写入、零占号）；</item>
/// <item>小数位与零小数币种取整（<c>100.005 USD/CNY → 100.01</c>、<c>100.5 JPY → 101</c>）原样落库；</item>
/// <item>合法付款单 / 货款申请单照常「创建 → 提交 → 审核」，金额与单号序列符合既有口径。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class PaymentApplyPersistedAmountSqlServerTests
    : IClassFixture<PaymentApplyPersistedAmountSqlServerFixture>
{
    private const decimal MaxAmount = FinancePersistedAmountRules.MaxDecimal18Scale2;
    private const decimal RoundUpCrossing = 9_999_999_999_999_999.995m;  // USD 取整进位越过上限
    private const decimal OneE16 = 10_000_000_000_000_000m;               // 1e16（超出上限）

    private readonly PaymentApplyPersistedAmountSqlServerFixture _fixture;

    public PaymentApplyPersistedAmountSqlServerTests(PaymentApplyPersistedAmountSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(PaymentApplyPersistedAmountSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    // ==================== 1. 实际列精度 + 边界值原样往返 + 真实列拒绝越界 ====================

    [Fact]
    public async Task Actual_column_precision_matches_rules_and_boundary_values_round_trip()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        Assert.Equal(18, FinancePersistedAmountRules.DecimalPrecision);
        Assert.Equal(2, FinancePersistedAmountRules.DecimalScale);
        Assert.Equal("decimal(18,2)", ColumnType(typeof(FinancePayment), nameof(FinancePayment.Amount)));
        Assert.Equal("decimal(18,2)", ColumnType(typeof(FinancePaymentApply), nameof(FinancePaymentApply.Amount)));

        Assert.Equal("decimal(18,2)", await SqlColumnTypeAsync(db, "FinancePayments", "Amount"));
        Assert.Equal("decimal(18,2)", await SqlColumnTypeAsync(db, "FinancePaymentApplies", "Amount"));

        var supplierId = await SeedSupplierAsync(db, "精度边界供应商");
        var customerId = await SeedCustomerAsync(db, "精度边界客户");
        var payment = await SeedPaymentAsync(db, $"FK-{Tag()}", supplierId, MaxAmount, Currency.CNY);
        var apply = await SeedPaymentApplyAsync(db, $"HK-{Tag()}", customerId, MaxAmount, Currency.CNY);

        var reloadedPayment = await db.FinancePayments.AsNoTracking().SingleAsync(p => p.Id == payment.Id);
        Assert.Equal(MaxAmount, reloadedPayment.Amount);
        var reloadedApply = await db.FinancePaymentApplies.AsNoTracking().SingleAsync(a => a.Id == apply.Id);
        Assert.Equal(MaxAmount, reloadedApply.Amount);
    }

    [Fact]
    public async Task Raw_ef_insert_of_over_limit_amount_is_refused_by_the_actual_column()
    {
        Guard();
        var supplierId = await SeedSupplierAsync("精度越界供应商");
        var customerId = await SeedCustomerAsync("精度越界客户");
        var over = MaxAmount + 0.01m;

        await using (var db = _fixture.CreateDbContext())
        {
            db.FinancePayments.Add(new FinancePayment
            {
                PaymentNo = $"FK-OVF-{Tag()}",
                PaymentDate = DateTime.Today,
                SupplierId = supplierId,
                Amount = over,
                Currency = Currency.CNY,
                PaymentMethod = PaymentMethod.BankTransfer,
                Status = DocumentStatus.Pending
            });
            // 真实 decimal(18,2) 列无法容纳越界金额：证明应用层边界与实际存储一致，必须在写入前拒绝。
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            AssertProviderRejected(ex);
        }

        await using (var db = _fixture.CreateDbContext())
        {
            db.FinancePaymentApplies.Add(new FinancePaymentApply
            {
                ApplyNo = $"HK-OVF-{Tag()}",
                ApplyDate = DateTime.Today,
                CustomerId = customerId,
                Amount = over,
                Currency = Currency.CNY,
                ExchangeRate = 1m,
                Status = DocumentStatus.Pending
            });
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            AssertProviderRejected(ex);
        }

        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(0, await verify.FinancePayments.CountAsync(p => p.SupplierId == supplierId));
            Assert.Equal(0, await verify.FinancePaymentApplies.CountAsync(a => a.CustomerId == customerId));
        }
    }

    /// <summary>
    /// 异常链中必须出现提供程序层面的拒绝：SQL Server 算术溢出（<see cref="SqlException"/>）或
    /// SqlClient 客户端参数范围校验（<see cref="ArgumentException"/>）；绝不能是应用层提前拦截。
    /// </summary>
    private static void AssertProviderRejected(Exception exception)
    {
        var chain = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            chain.Add($"{current.GetType().Name}: {current.Message}");
            if (current is SqlException or ArgumentException) return;
        }
        Assert.Fail("expected the provider to reject the over-limit scalar amount: " + string.Join(" -> ", chain));
    }

    [Fact]
    public async Task Sql_server_itself_cannot_convert_over_limit_scalar_amount_to_the_column_type()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        // 真实列精度 decimal(18,2)：SQL Server 对越界标量报「算术溢出」（错误号 8115，与语言无关），
        // 证明应用层单列边界（FinancePersistedAmountRules）与真实存储边界逐分一致。
        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            db.Database.SqlQueryRaw<decimal>("SELECT CAST(10000000000000000.00 AS decimal(18,2)) AS [Value]")
                .ToListAsync());
        Assert.Equal(8115, ex.Number);
    }

    // ==================== 2. 创建 / 修改：越界拒绝且零写入 / 零占号 ====================

    [Theory]
    [InlineData("round-up-cross")]
    [InlineData("1e16")]
    [InlineData("huge-decimal")]
    public async Task Create_supplier_payment_rejects_unpersistable_amount_without_numbering_or_write(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = await SeedSupplierAsync(db, "付款越界供应商");
        var userId = await SeedUserAsync(db, SupplierPaymentLifecycleRules.RequiredMenuCode, "付款越界操作员");
        var controller = NewPaymentController(db, userId);

        var before = await PaymentSnapshotAsync(db, supplierId);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => controller.Create(PaymentDraft(supplierId, OverLimit(scenario))));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("DECIMAL(18,2)", ex.Message);
        await AssertPaymentRejectedWithoutSideEffectsAsync(db, supplierId, before);

        // 同一受权控制器随后合法创建成功：证明拒绝原因是金额而非授权，单号只被合法请求消耗。
        var id = CreatedId(await controller.Create(PaymentDraft(supplierId, 100m)));
        var after = await PaymentSnapshotAsync(db, supplierId);
        Assert.True(id > 0);
        Assert.Equal(before.Documents + 1, after.Documents);
        Assert.Equal(before.Sequence + 1, after.Sequence);
    }

    [Theory]
    [InlineData("round-up-cross")]
    [InlineData("1e16")]
    [InlineData("huge-decimal")]
    public async Task Create_payment_apply_rejects_unpersistable_amount_without_numbering_or_write(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db, "货款申请越界客户");
        var userId = await SeedUserAsync(db, FinancePaymentApplyLifecycleRules.RequiredMenuCode, "货款申请越界操作员");
        var controller = NewApplyController(db, userId);

        var before = await ApplySnapshotAsync(db, customerId);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => controller.Create(ApplyDraft(customerId, OverLimit(scenario))));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("DECIMAL(18,2)", ex.Message);
        await AssertApplyRejectedWithoutSideEffectsAsync(db, customerId, before);

        var id = CreatedId(await controller.Create(ApplyDraft(customerId, 100m)));
        var after = await ApplySnapshotAsync(db, customerId);
        Assert.True(id > 0);
        Assert.Equal(before.Documents + 1, after.Documents);
        Assert.Equal(before.Sequence + 1, after.Sequence);
    }

    [Fact]
    public async Task Update_supplier_payment_rejects_unpersistable_amount_and_leaves_stored_payment_unchanged()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = await SeedSupplierAsync(db, "付款修改供应商");
        var userId = await SeedUserAsync(db, SupplierPaymentLifecycleRules.RequiredMenuCode, "付款修改操作员");
        var controller = NewPaymentController(db, userId);
        var id = CreatedId(await controller.Create(PaymentDraft(supplierId, 1000m)));
        var before = await db.FinancePayments.AsNoTracking().SingleAsync(p => p.Id == id);

        var request = PaymentDraft(supplierId, RoundUpCrossing);
        request.Remark = "SHOULD_NOT_BE_SAVED";
        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Update(id, request));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        var after = await db.FinancePayments.AsNoTracking().SingleAsync(p => p.Id == id);
        Assert.Equal(before.Amount, after.Amount);
        Assert.Equal(before.PaymentNo, after.PaymentNo);
        Assert.Equal(before.Remark, after.Remark);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task Update_payment_apply_rejects_unpersistable_amount_and_leaves_stored_apply_unchanged()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db, "货款申请修改客户");
        var userId = await SeedUserAsync(db, FinancePaymentApplyLifecycleRules.RequiredMenuCode, "货款申请修改操作员");
        var controller = NewApplyController(db, userId);
        var id = CreatedId(await controller.Create(ApplyDraft(customerId, 1000m)));
        var before = await db.FinancePaymentApplies.AsNoTracking().SingleAsync(a => a.Id == id);

        var request = ApplyDraft(customerId, OneE16);
        request.Remark = "SHOULD_NOT_BE_SAVED";
        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Update(id, request));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        var after = await db.FinancePaymentApplies.AsNoTracking().SingleAsync(a => a.Id == id);
        Assert.Equal(before.Amount, after.Amount);
        Assert.Equal(before.ApplyNo, after.ApplyNo);
        Assert.Equal(before.Remark, after.Remark);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    // ==================== 3. 取整口径与合法生命周期（真实 SQL） ====================

    [Fact]
    public async Task Create_supplier_payment_rounds_fractional_amount_and_persists_exact_boundary()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = await SeedSupplierAsync(db, "付款取整供应商");
        var userId = await SeedUserAsync(db, SupplierPaymentLifecycleRules.RequiredMenuCode, "付款取整操作员");
        var controller = NewPaymentController(db, userId);

        var fractionalId = CreatedId(await controller.Create(PaymentDraft(supplierId, 100.005m)));
        Assert.Equal(100.01m, await PaymentAmountAsync(db, fractionalId));

        var jpyId = CreatedId(await controller.Create(PaymentDraft(supplierId, 100.5m, Currency.JPY)));
        Assert.Equal(101m, await PaymentAmountAsync(db, jpyId));

        var boundaryId = CreatedId(await controller.Create(PaymentDraft(supplierId, MaxAmount)));
        Assert.Equal(MaxAmount, await PaymentAmountAsync(db, boundaryId));

        // 零小数币种取整进位越过上限：受控拒绝、零写入。
        var before = await PaymentSnapshotAsync(db, supplierId);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => controller.Create(PaymentDraft(supplierId, 9_999_999_999_999_999.6m, Currency.JPY)));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        await AssertPaymentRejectedWithoutSideEffectsAsync(db, supplierId, before);
    }

    [Fact]
    public async Task Create_payment_apply_rounds_fractional_amount_and_persists_exact_boundary()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db, "货款申请取整客户");
        var userId = await SeedUserAsync(db, FinancePaymentApplyLifecycleRules.RequiredMenuCode, "货款申请取整操作员");
        var controller = NewApplyController(db, userId);

        var fractionalId = CreatedId(await controller.Create(ApplyDraft(customerId, 100.005m)));
        Assert.Equal(100.01m, await ApplyAmountAsync(db, fractionalId));

        var boundaryId = CreatedId(await controller.Create(ApplyDraft(customerId, MaxAmount)));
        Assert.Equal(MaxAmount, await ApplyAmountAsync(db, boundaryId));
    }

    [Fact]
    public async Task Valid_supplier_payment_lifecycle_create_submit_approve_persists_boundary_amount()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = await SeedSupplierAsync(db, "付款合法流转供应商");
        var userId = await SeedUserAsync(db, SupplierPaymentLifecycleRules.RequiredMenuCode, "付款合法流转操作员");
        var controller = NewPaymentController(db, userId);

        var before = await PaymentSnapshotAsync(db, supplierId);
        var id = CreatedId(await controller.Create(PaymentDraft(supplierId, MaxAmount)));
        Assert.IsType<OkObjectResult>(await controller.Submit(id));
        Assert.IsType<OkObjectResult>(await controller.Approve(id));

        var stored = await db.FinancePayments.AsNoTracking().SingleAsync(p => p.Id == id);
        Assert.Equal(DocumentStatus.Approved, stored.Status);
        Assert.Equal(MaxAmount, stored.Amount);
        Assert.Equal(before.Sequence + 1, (await PaymentSnapshotAsync(db, supplierId)).Sequence);
    }

    [Fact]
    public async Task Valid_linked_supplier_payment_lifecycle_keeps_source_link_and_boundary_amount()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db, "付款关联来源客户");
        var supplierId = await SeedSupplierAsync(db, "付款关联来源供应商");

        // 先走货款申请单「创建 → 提交 → 审核」形成真实、未删除且未取消的来源单据。
        var applyUserId = await SeedUserAsync(db, FinancePaymentApplyLifecycleRules.RequiredMenuCode, "关联来源申请操作员");
        var applyController = NewApplyController(db, applyUserId);
        var applyId = CreatedId(await applyController.Create(ApplyDraft(customerId, MaxAmount)));
        Assert.IsType<OkObjectResult>(await applyController.Submit(applyId));
        Assert.IsType<OkObjectResult>(await applyController.Approve(applyId));

        var userId = await SeedUserAsync(db, SupplierPaymentLifecycleRules.RequiredMenuCode, "关联来源付款操作员");
        var controller = NewPaymentController(db, userId);
        var draft = PaymentDraft(supplierId, MaxAmount);
        draft.PaymentApplyId = applyId;
        var id = CreatedId(await controller.Create(draft));

        Assert.IsType<OkObjectResult>(await controller.Submit(id));
        Assert.IsType<OkObjectResult>(await controller.Approve(id));

        // 存储边界与来源资格 / 容量（来源可解析、未取消、币种一致、付款金额不超过申请金额）原样保留。
        var stored = await db.FinancePayments.AsNoTracking().SingleAsync(p => p.Id == id);
        Assert.Equal(DocumentStatus.Approved, stored.Status);
        Assert.Equal(MaxAmount, stored.Amount);
        Assert.Equal(applyId, stored.PaymentApplyId);
    }

    [Fact]
    public async Task Valid_payment_apply_lifecycle_create_submit_approve_persists_boundary_amount()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db, "货款申请合法流转客户");
        var userId = await SeedUserAsync(db, FinancePaymentApplyLifecycleRules.RequiredMenuCode, "货款申请合法流转操作员");
        var controller = NewApplyController(db, userId);

        var before = await ApplySnapshotAsync(db, customerId);
        var id = CreatedId(await controller.Create(ApplyDraft(customerId, MaxAmount)));
        Assert.IsType<OkObjectResult>(await controller.Submit(id));
        Assert.IsType<OkObjectResult>(await controller.Approve(id));

        var stored = await db.FinancePaymentApplies.AsNoTracking().SingleAsync(a => a.Id == id);
        Assert.Equal(DocumentStatus.Approved, stored.Status);
        Assert.Equal(MaxAmount, stored.Amount);
        Assert.Equal(before.Sequence + 1, (await ApplySnapshotAsync(db, customerId)).Sequence);
    }

    // ==================== 4. 真实 SQL 夹具种子与脚手架 ====================

    private static decimal OverLimit(string scenario) => scenario switch
    {
        "round-up-cross" => RoundUpCrossing,
        "1e16" => OneE16,
        _ => decimal.MaxValue
    };

    private static FinancePaymentController NewPaymentController(ErpDbContext db, long? userId)
    {
        var controller = new FinancePaymentController(db, new DocumentNumberService(db));
        SetUser(controller, userId);
        return controller;
    }

    private static FinancePaymentApplyController NewApplyController(ErpDbContext db, long? userId)
    {
        var controller = new FinancePaymentApplyController(db, new DocumentNumberService(db));
        SetUser(controller, userId);
        return controller;
    }

    private static void SetUser(ControllerBase controller, long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
    }

    private static long CreatedId(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

    private static async Task<long> SeedSupplierAsync(ErpDbContext db, string name)
    {
        var supplier = new BaseSupplier
        {
            SupplierCode = $"EPS-{Tag()}",
            SupplierName = name,
            Status = 1
        };
        db.BaseSuppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier.Id;
    }

    private async Task<long> SeedSupplierAsync(string name)
    {
        await using var db = _fixture.CreateDbContext();
        return await SeedSupplierAsync(db, name);
    }

    private static async Task<long> SeedCustomerAsync(ErpDbContext db, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"EPC-{Tag()}",
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常"
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private async Task<long> SeedCustomerAsync(string name)
    {
        await using var db = _fixture.CreateDbContext();
        return await SeedCustomerAsync(db, name);
    }

    private static async Task<FinancePayment> SeedPaymentAsync(
        ErpDbContext db, string paymentNo, long supplierId, decimal amount, Currency currency)
    {
        var payment = new FinancePayment
        {
            PaymentNo = paymentNo,
            PaymentDate = DateTime.Today,
            SupplierId = supplierId,
            Amount = amount,
            Currency = currency,
            PaymentMethod = PaymentMethod.BankTransfer,
            Status = DocumentStatus.Pending
        };
        db.FinancePayments.Add(payment);
        await db.SaveChangesAsync();
        return payment;
    }

    private static async Task<FinancePaymentApply> SeedPaymentApplyAsync(
        ErpDbContext db, string applyNo, long customerId, decimal amount, Currency currency)
    {
        var apply = new FinancePaymentApply
        {
            ApplyNo = applyNo,
            ApplyDate = DateTime.Today,
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            ExchangeRate = 1m,
            Status = DocumentStatus.Pending
        };
        db.FinancePaymentApplies.Add(apply);
        await db.SaveChangesAsync();
        return apply;
    }

    private static FinancePayment PaymentDraft(
        long supplierId, decimal amount, Currency currency = Currency.CNY) => new()
    {
        PaymentDate = DateTime.Today,
        SupplierId = supplierId,
        Amount = amount,
        Currency = currency,
        PaymentMethod = PaymentMethod.BankTransfer,
        BankAccount = "ACC-509",
        Remark = "ERP-509_SQL_TEST"
    };

    private static FinancePaymentApply ApplyDraft(
        long customerId, decimal amount, Currency currency = Currency.CNY) => new()
    {
        ApplyDate = DateTime.Today,
        CustomerId = customerId,
        Amount = amount,
        Currency = currency,
        ExchangeRate = 1m,
        BankAccount = "ACC-509",
        Payee = "义乌档口",
        Reason = "ERP-509_SQL_TEST",
        Remark = "ERP-509_SQL_TEST"
    };

    /// <summary>播种真实授权账号：既有菜单授权 + 启用账号（系统内置角色 → 不受客户数据范围限制）。</summary>
    private static async Task<long> SeedUserAsync(ErpDbContext db, string menuCode, string displayName)
    {
        var role = new SysRole { RoleCode = $"PAMT-{Tag()}", RoleName = displayName, IsSystem = true };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = $"pamt-{Tag()}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = displayName,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var menu = await db.SysMenus.FirstAsync(m => m.MenuCode == menuCode && !m.IsDeleted);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static Task<decimal> PaymentAmountAsync(ErpDbContext db, long id)
        => db.FinancePayments.AsNoTracking().Where(p => p.Id == id).Select(p => p.Amount).SingleAsync();

    private static Task<decimal> ApplyAmountAsync(ErpDbContext db, long id)
        => db.FinancePaymentApplies.AsNoTracking().Where(a => a.Id == id).Select(a => a.Amount).SingleAsync();

    /// <summary>本用例名下单据数与既有单号序列基线（夹具库被多个用例复用，拒绝断言一律相对快照）。</summary>
    private sealed record AmountSnapshot(int Documents, long Sequence);

    private static Task<AmountSnapshot> PaymentSnapshotAsync(ErpDbContext db, long supplierId)
        => SnapshotAsync(db, DocumentType.Payment,
            db.FinancePayments.CountAsync(p => p.SupplierId == supplierId));

    private static Task<AmountSnapshot> ApplySnapshotAsync(ErpDbContext db, long customerId)
        => SnapshotAsync(db, DocumentType.PaymentApply,
            db.FinancePaymentApplies.CountAsync(a => a.CustomerId == customerId));

    private static async Task<AmountSnapshot> SnapshotAsync(ErpDbContext db, DocumentType type, Task<int> documents)
        => new(await documents, await SequenceAsync(db, type));

    private static Task<long> SequenceAsync(ErpDbContext db, DocumentType type)
        => db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == type && !r.IsDeleted)
            .Select(r => r.CurrentSequence).SingleAsync();

    private static async Task AssertPaymentRejectedWithoutSideEffectsAsync(
        ErpDbContext db, long supplierId, AmountSnapshot before)
    {
        Assert.Equal(before.Documents, await db.FinancePayments.CountAsync(p => p.SupplierId == supplierId));
        Assert.Equal(before.Sequence, await SequenceAsync(db, DocumentType.Payment));
    }

    private static async Task AssertApplyRejectedWithoutSideEffectsAsync(
        ErpDbContext db, long customerId, AmountSnapshot before)
    {
        Assert.Equal(before.Documents, await db.FinancePaymentApplies.CountAsync(a => a.CustomerId == customerId));
        Assert.Equal(before.Sequence, await SequenceAsync(db, DocumentType.PaymentApply));
    }

    private static string ColumnType(Type entityType, string propertyName)
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer("Server=(localdb)\\NEWERP_AutoAcceptance;" +
                          "Database=NEWERP_AUTOTEST_ERP509_MODEL_ONLY;Integrated Security=true;" +
                          "TrustServerCertificate=true")
            .Options;
        using var db = new ErpDbContext(options);
        return db.Model.FindEntityType(entityType)!.FindProperty(propertyName)!.GetColumnType()!;
    }

    /// <summary>真实 SQL Server 列类型（<c>INFORMATION_SCHEMA.COLUMNS</c>），证明实际存储精度与规则常量同源。</summary>
    private static async Task<string> SqlColumnTypeAsync(ErpDbContext db, string table, string column)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT DISTINCT DATA_TYPE + '(' + CAST(NUMERIC_PRECISION AS varchar(10)) + ',' + " +
            "CAST(NUMERIC_SCALE AS varchar(10)) + ')' FROM INFORMATION_SCHEMA.COLUMNS " +
            "WHERE TABLE_NAME = @table AND COLUMN_NAME = @column AND DATA_TYPE = 'decimal'";
        AddParameter(command, "@table", table);
        AddParameter(command, "@column", column);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

// ==================== 专用 localdb 夹具 + 目标护栏 ====================

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-509）：每次运行创建一个全新 GUID 后缀库并初始化为完整 NEWERP 结构 +
/// 种子数据，供真实 SQL Server 集成测试复用。
/// <para>每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库。</para>
/// </summary>
public sealed class PaymentApplyPersistedAmountSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_PAYMENTAPPLYAMOUNT_20261014";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-509] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-509] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class PaymentApplyPersistedAmountTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            PaymentApplyPersistedAmountSqlServerFixture.AssertDedicatedTarget(connection));
}
