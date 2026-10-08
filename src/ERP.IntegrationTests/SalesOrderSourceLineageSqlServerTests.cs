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

/// <summary>
/// ERP-401 销售订单普通表单保存（新增 / 修改 / 提交 / 审核）来源血缘护栏的真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <para>直接执行<b>真实业务代码</b>（<see cref="SalesOrderController"/> + <see cref="SalesOrderSourceLineageRules"/> +
/// <see cref="SalesOrderConversion"/> + <see cref="ProformaInvoiceController"/> 转换入口），不复制测试专用实现：</para>
/// <list type="number">
/// <item>真实既有身份 / 菜单 / 客户范围：无身份、无既有「销售订单」菜单、禁用账号、越客户范围一律 fail closed 且零写入；</item>
/// <item>伪造 / 异客户 / 已删除 / 未审核（不合格）/ 冲突来源对一律原子拒绝，不写订单、不改写来源与状态；</item>
/// <item>普通保存显式链接已审核来源时按来源行<b>规范化</b>来源号（不采信提交文本），来源状态不被伪造；</item>
/// <item>来源行锁的审计时间戳刷新在失败时随事务<b>整体回滚</b>（拒绝后 <c>UpdatedAt</c> 与状态零变化）；</item>
/// <item><b>两条独立连接竞态</b>：普通保存 vs 直接转换、普通保存 vs 来源作废、两张并发普通保存，均只产生一致结果。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只创建<b>全新 GUID 后缀库</b>，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。
/// <b>构建完成不等于阶段验收</b>：只有以下场景在专用 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class SalesOrderSourceLineageSqlServerTests
    : IClassFixture<SalesOrderSourceLineageSqlServerFixture>
{
    private readonly SalesOrderSourceLineageSqlServerFixture _fixture;

    public SalesOrderSourceLineageSqlServerTests(SalesOrderSourceLineageSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(SalesOrderSourceLineageSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 控制器 / 双连接脚手架 ====================

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>绑定真实 HttpContext 身份（可空 = 无身份）的真实销售订单控制器。</summary>
    private static SalesOrderController NewSalesOrderController(ErpDbContext db, long? userId)
        => new(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = HttpFor(userId) }
        };

    /// <summary>绑定真实 HttpContext 身份（可空 = 无身份）的真实 PI 控制器（直接转换 / 作废入口）。</summary>
    private static ProformaInvoiceController NewPiController(ErpDbContext db, long? userId)
        => new(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = HttpFor(userId) }
        };

    private static DefaultHttpContext HttpFor(long? userId)
    {
        var http = new DefaultHttpContext();
        if (userId.HasValue)
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"));
        return http;
    }

    /// <summary>用一条独立连接执行「普通保存」（各自 DbContext / 连接 / 事务）。</summary>
    private async Task<(bool Success, string Error)> TryCreateAsync(long? userId, SalesOrder body)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await NewSalesOrderController(db, userId).Create(body);
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>用一条独立连接执行「PI → 销售订单」直接转换。</summary>
    private async Task<(bool Success, string Error)> TryConvertAsync(long? userId, long piId)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await NewPiController(db, userId).ToSalesOrder(piId);
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>用一条独立连接执行 PI 作废（来源失效）。</summary>
    private async Task<(bool Success, string Error)> TryVoidAsync(long? userId, long piId)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await NewPiController(db, userId).Void(piId);
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>两条独立连接以同一起跑线并发执行（门闩对齐），返回两侧结果。</summary>
    private static async Task<List<(bool Success, string Error)>> RaceAsync(
        Func<Task<(bool Success, string Error)>> first, Func<Task<(bool Success, string Error)>> second)
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

    // ==================== 种子（既有菜单 / 既有业务员数据范围，不新增权限模型） ====================

    /// <summary>播种受限操作员：登录账号 = 员工编码（ERP-097 权威映射），并按需授予既有 PI / 销售订单菜单。</summary>
    private static async Task<(SysUser User, BaseEmployee Employee, SysRole Role)> SeedOperatorAsync(
        ErpDbContext db, bool piMenu = true, bool soMenu = true, UserStatus status = UserStatus.Enabled)
    {
        var code = $"INT_E401_{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code, Status = status
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = $"E401R_{code}", RoleCode = $"INT_E401_{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        if (piMenu) await GrantMenuAsync(db, role.Id, ProformaInvoiceAuthorizationRules.RequiredMenuCode);
        if (soMenu) await GrantMenuAsync(db, role.Id, SalesOrderSourceLineageRules.SalesOrderMenuCode);
        await db.SaveChangesAsync();
        return (user, employee, role);
    }

    private static async Task GrantMenuAsync(ErpDbContext db, long roleId, string menuCode)
    {
        var menu = await db.SysMenus.FirstAsync(m => !m.IsDeleted && m.MenuCode == menuCode);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
    }

    private static async Task<BaseCustomer> SeedCustomerAsync(ErpDbContext db, string code, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code, CustomerName = "集成客户", Status = 1, CreditStatus = "正常", EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<Quotation> SeedQuotationAsync(ErpDbContext db, string no, long? customerId,
        DocumentStatus status = DocumentStatus.Approved)
    {
        var quotation = new Quotation
        {
            QuotationNo = no, QuotationDate = DateTime.Today, CustomerId = customerId, CustomerName = "集成客户",
            Currency = Currency.USD, ExchangeRate = 7.2m, Status = status
        };
        quotation.Details.Add(new QuotationDetail
        {
            SortNo = 1, ProductCode = "INT-E401-Q1", ProductName = "集成商品", Unit = "PCS",
            Quantity = 100m, UnitPrice = 2.5m, Amount = 250m
        });
        db.Quotations.Add(quotation);
        await db.SaveChangesAsync();
        return quotation;
    }

    private static async Task<ProformaInvoice> SeedPiAsync(ErpDbContext db, string no, long? customerId,
        DocumentStatus status = DocumentStatus.Pending, decimal amount = 1000m, Quotation? source = null)
    {
        var pi = new ProformaInvoice
        {
            PiNo = no, PiDate = DateTime.Today, CustomerId = customerId, CustomerName = "集成客户",
            QuotationId = source?.Id, QuotationNo = source?.QuotationNo ?? string.Empty,
            Currency = Currency.USD, ExchangeRate = 7.2m, DepositRatio = 30m,
            TotalAmount = amount, TotalAmountCny = amount * 7.2m, DepositAmount = amount * 0.3m,
            Status = status
        };
        pi.Details.Add(new ProformaInvoiceDetail
        {
            SortNo = 1, ProductCode = "INT-E401-P1", ProductName = "集成商品", Unit = "PCS",
            Quantity = 100m, UnitPrice = amount / 100m, Amount = amount
        });
        db.ProformaInvoices.Add(pi);
        await db.SaveChangesAsync();
        return pi;
    }

    private static SalesOrder NewOrderBody(long customerId, long? quotationId, long? piId,
        string quotationNo = "", string piNo = "")
        => new()
        {
            OrderDate = DateTime.Today,
            CustomerId = customerId,
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            DepositRatio = 30m,
            SourceQuotationId = quotationId,
            SourceQuotationNo = quotationNo,
            SourcePiId = piId,
            SourcePiNo = piNo,
            Details = new List<SalesOrderDetail>
            {
                new()
                {
                    ProductId = 0, ProductName = "集成商品", Spec = "标准", Unit = "PCS",
                    Quantity = 100m, UnitPrice = 10m
                }
            }
        };

    // ==================== 1. 身份 / 菜单 / 客户范围 fail closed（零写入） ====================

    [Theory]
    [InlineData("missing")]
    [InlineData("no-menu")]
    [InlineData("disabled")]
    public async Task 无身份_无菜单_禁用账号_均fail_closed且不写订单不改来源(string scenario)
    {
        Guard();
        var tag = Tag();
        long? userId;
        long customerId, piId;
        DateTime? updatedAtBefore;

        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed,
                soMenu: scenario != "no-menu",
                status: scenario == "disabled" ? UserStatus.Disabled : UserStatus.Enabled);
            userId = scenario == "missing" ? null : user.Id;
            var customer = await SeedCustomerAsync(seed, $"INT_E401_D_{tag}", employee.Id);
            customerId = customer.Id;
            var pi = await SeedPiAsync(seed, $"INT_E401_PI_D_{tag}", customer.Id, DocumentStatus.Approved);
            piId = pi.Id;
            updatedAtBefore = pi.UpdatedAt;
        }

        var result = await TryCreateAsync(userId, NewOrderBody(customerId, null, piId));
        Assert.False(result.Success);

        await using var verify = _fixture.CreateDbContext();
        Assert.False(await verify.SalesOrders.AnyAsync(o => o.SourcePiId == piId));
        var stored = await verify.ProformaInvoices.AsNoTracking().SingleAsync(p => p.Id == piId);
        Assert.Equal(DocumentStatus.Approved, stored.Status);
        Assert.Equal(updatedAtBefore, stored.UpdatedAt);   // 行锁的审计时间戳刷新随事务整体回滚
    }

    // ==================== 2. 伪造 / 异客户 / 已删除 / 不合格 / 冲突来源（零写入） ====================

    [Fact]
    public async Task 异客户_已删除_未审核_冲突来源对_均原子拒绝且不改来源()
    {
        Guard();
        var tag = Tag();
        long userId, ownCustomerId, foreignCustomerId;
        long foreignPiId, deletedPiId, pendingPiId, quotationId, conflictingQuotationId, ownPiId;
        var updatedAtBefore = new Dictionary<long, DateTime?>();

        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            userId = user.Id;
            var own = await SeedCustomerAsync(seed, $"INT_E401_OWN_{tag}", employee.Id);
            ownCustomerId = own.Id;
            var foreign = await SeedCustomerAsync(seed, $"INT_E401_FGN_{tag}");
            foreignCustomerId = foreign.Id;

            foreignPiId = (await SeedPiAsync(seed, $"INT_E401_PI_F_{tag}", foreign.Id,
                DocumentStatus.Approved)).Id;
            deletedPiId = (await SeedPiAsync(seed, $"INT_E401_PI_X_{tag}", own.Id,
                DocumentStatus.Approved)).Id;
            pendingPiId = (await SeedPiAsync(seed, $"INT_E401_PI_P_{tag}", own.Id,
                DocumentStatus.Pending)).Id;

            var quotation = await SeedQuotationAsync(seed, $"INT_E401_QT_{tag}", own.Id);
            quotationId = quotation.Id;
            conflictingQuotationId = (await SeedQuotationAsync(seed, $"INT_E401_QTX_{tag}", own.Id)).Id;
            ownPiId = (await SeedPiAsync(seed, $"INT_E401_PI_OWN_{tag}", own.Id, DocumentStatus.Approved,
                source: quotation)).Id;

            var toDelete = await seed.ProformaInvoices.SingleAsync(p => p.Id == deletedPiId);
            toDelete.IsDeleted = true;
            await seed.SaveChangesAsync();
        }

        await using (var snapshot = _fixture.CreateDbContext())
        {
            foreach (var id in new[] { foreignPiId, deletedPiId, pendingPiId, ownPiId })
                updatedAtBefore[id] = await snapshot.ProformaInvoices.AsNoTracking()
                    .Where(p => p.Id == id).Select(p => p.UpdatedAt).SingleAsync();
        }

        // 异客户来源（目标订单为本客户、来源属他客户）：跨客户链接拒绝。
        Assert.False((await TryCreateAsync(userId, NewOrderBody(ownCustomerId, null, foreignPiId))).Success);
        // 越客户范围（目标订单客户不在本账号实时范围）：fail closed 拒绝。
        Assert.False((await TryCreateAsync(userId, NewOrderBody(foreignCustomerId, null, foreignPiId))).Success);
        // 已删除来源：无效来源拒绝。
        Assert.False((await TryCreateAsync(userId, NewOrderBody(ownCustomerId, null, deletedPiId))).Success);
        // 不合格来源（未审核 PI）：既有转换资格拒绝。
        Assert.False((await TryCreateAsync(userId, NewOrderBody(ownCustomerId, null, pendingPiId))).Success);
        // 冲突来源对：显式报价单与 PI 的权威报价单祖先不一致。
        Assert.False((await TryCreateAsync(userId,
            NewOrderBody(ownCustomerId, conflictingQuotationId, ownPiId))).Success);

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(0, await verify.SalesOrders.CountAsync(o =>
            o.SourcePiId == foreignPiId || o.SourcePiId == deletedPiId
            || o.SourcePiId == pendingPiId || o.SourcePiId == ownPiId));
        Assert.Equal(0, await verify.SalesOrders.CountAsync(o => o.SourceQuotationId == quotationId));

        foreach (var piId in new[] { foreignPiId, deletedPiId, pendingPiId, ownPiId })
        {
            var stored = await verify.ProformaInvoices.AsNoTracking().SingleAsync(p => p.Id == piId);
            Assert.Equal(updatedAtBefore[piId], stored.UpdatedAt);   // 失败整体回滚：行锁的时间戳刷新也不留痕
        }

        // 来源报价单与 PI 的商业口径 / 状态零变化。
        var quotationRow = await verify.Quotations.AsNoTracking().SingleAsync(q => q.Id == quotationId);
        Assert.Equal(DocumentStatus.Approved, quotationRow.Status);
        var ownPi = await verify.ProformaInvoices.AsNoTracking().SingleAsync(p => p.Id == ownPiId);
        Assert.Equal(DocumentStatus.Approved, ownPi.Status);
        Assert.Equal(1000m, ownPi.TotalAmount);
    }

    // ==================== 3. 权威来源规范化 / 未链接手工订单 ====================

    [Fact]
    public async Task 普通保存_显式链接已审核PI_以权威来源号落库且不伪造来源状态()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, piId, quotationId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            userId = user.Id;
            var customer = await SeedCustomerAsync(seed, $"INT_E401_OK_{tag}", employee.Id);
            customerId = customer.Id;
            var quotation = await SeedQuotationAsync(seed, $"INT_E401_QTOK_{tag}", customer.Id);
            quotationId = quotation.Id;
            piId = (await SeedPiAsync(seed, $"INT_E401_PIOK_{tag}", customer.Id, DocumentStatus.Approved,
                source: quotation)).Id;
        }

        // 提交文本故意伪造来源号（含跨客户 / 不存在的来源）：服务端一律按来源行规范化，绝不采信文本。
        var body = NewOrderBody(customerId, quotationId, piId,
            quotationNo: "FORGED-QT-999", piNo: "FORGED-PI-999");
        var result = await TryCreateAsync(userId, body);
        Assert.True(result.Success, result.Error);

        await using var verify = _fixture.CreateDbContext();
        var order = await verify.SalesOrders.AsNoTracking().Include(o => o.Details)
            .SingleAsync(o => o.SourcePiId == piId);
        Assert.StartsWith("SO", order.OrderNo);
        Assert.Equal(DocumentStatus.Pending, order.Status);
        Assert.Equal($"INT_E401_PIOK_{tag}", order.SourcePiNo);
        Assert.Equal(quotationId, order.SourceQuotationId);
        Assert.Equal($"INT_E401_QTOK_{tag}", order.SourceQuotationNo);
        Assert.Equal(1000m, order.TotalAmount);          // 服务端按 100 × 10 重算
        Assert.Equal(300m, order.DepositAmount);          // 定金 30%

        // 来源 PI 状态与商业口径零改写（普通保存不是直接转换，也不消耗来源）。
        var pi = await verify.ProformaInvoices.AsNoTracking().SingleAsync(p => p.Id == piId);
        Assert.Equal(DocumentStatus.Approved, pi.Status);
        Assert.Equal(1000m, pi.TotalAmount);

        // 未链接的手工订单保持完全有效（不取任何来源锁、不写来源字段）。
        var manual = NewOrderBody(customerId, null, null);
        var manualResult = await TryCreateAsync(userId, manual);
        Assert.True(manualResult.Success, manualResult.Error);
        var manualOrder = await verify.SalesOrders.AsNoTracking()
            .SingleAsync(o => o.CustomerId == customerId && o.SourcePiId == null && o.SourceQuotationId == null);
        Assert.Null(manualOrder.SourcePiId);
        Assert.Null(manualOrder.SourceQuotationId);
        Assert.Equal(string.Empty, manualOrder.SourcePiNo);
    }

    // ==================== 4. 失败整体回滚（行锁时间戳 / 来源状态 / 订单） ====================

    [Fact]
    public async Task 拒绝时_来源行锁与审计时间戳_状态_订单全部回滚()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, piId, quotationId;
        DateTime? updatedAtBefore;

        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            userId = user.Id;
            var customer = await SeedCustomerAsync(seed, $"INT_E401_RB_{tag}", employee.Id);
            customerId = customer.Id;
            quotationId = (await SeedQuotationAsync(seed, $"INT_E401_QTRB_{tag}", customer.Id)).Id;
            // PI 未审核 → 锁内既有转换资格拒绝（发生在取锁之后、写订单与占号之前）。
            var pi = await SeedPiAsync(seed, $"INT_E401_PIRB_{tag}", customer.Id, DocumentStatus.Pending);
            piId = pi.Id;
            updatedAtBefore = pi.UpdatedAt;
        }

        var result = await TryCreateAsync(userId, NewOrderBody(customerId, null, piId));
        Assert.False(result.Success);

        await using var verify = _fixture.CreateDbContext();
        Assert.False(await verify.SalesOrders.AnyAsync(o => o.SourcePiId == piId));
        Assert.False(await verify.SalesOrders.AnyAsync(o => o.SourceQuotationId == quotationId));

        var piRow = await verify.ProformaInvoices.AsNoTracking().SingleAsync(p => p.Id == piId);
        Assert.Equal(DocumentStatus.Pending, piRow.Status);
        Assert.Equal(1000m, piRow.TotalAmount);
        Assert.Equal(updatedAtBefore, piRow.UpdatedAt);   // 行锁的审计时间戳刷新在同一事务内回滚
    }

    // ==================== 5. 两条独立连接的竞态（普通保存 vs 转换 / 作废 / 普通保存） ====================

    [Fact]
    public async Task 两条独立连接_普通保存与直接转换_同一来源至多一张目标订单()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, piId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            userId = user.Id;
            var customer = await SeedCustomerAsync(seed, $"INT_E401_R1_{tag}", employee.Id);
            customerId = customer.Id;
            piId = (await SeedPiAsync(seed, $"INT_E401_PI_R1_{tag}", customer.Id,
                DocumentStatus.Approved)).Id;
        }

        var results = await RaceAsync(
            () => TryCreateAsync(userId, NewOrderBody(customerId, null, piId)),
            () => TryConvertAsync(userId, piId));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var orders = await verify.SalesOrders.AsNoTracking()
            .Where(o => o.SourcePiId == piId).ToListAsync();
        Assert.Single(orders);   // 同一来源至多一张目标订单（既有唯一目标规则）
        Assert.False(orders[0].IsDeleted);

        var pi = await verify.ProformaInvoices.AsNoTracking().SingleAsync(p => p.Id == piId);
        // 直接转换赢：来源置「已完成」；普通保存赢：来源保持「已审核」（转换随后按重复规则被拒）。
        Assert.Equal(results[1].Success ? DocumentStatus.Completed : DocumentStatus.Approved, pi.Status);
    }

    [Fact]
    public async Task 两条独立连接_普通保存与来源作废_只产生一致的历史()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, piId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            userId = user.Id;
            var customer = await SeedCustomerAsync(seed, $"INT_E401_R2_{tag}", employee.Id);
            customerId = customer.Id;
            piId = (await SeedPiAsync(seed, $"INT_E401_PI_R2_{tag}", customer.Id,
                DocumentStatus.Approved)).Id;
        }

        var results = await RaceAsync(
            () => TryCreateAsync(userId, NewOrderBody(customerId, null, piId)),
            () => TryVoidAsync(userId, piId));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var orderCount = await verify.SalesOrders.AsNoTracking().CountAsync(o => o.SourcePiId == piId);
        var pi = await verify.ProformaInvoices.AsNoTracking().SingleAsync(p => p.Id == piId);

        if (results[0].Success)
        {
            // 保存赢：来源保持已审核且恰有一张目标订单（随后作废因下游链接被拒）。
            Assert.Equal(DocumentStatus.Approved, pi.Status);
            Assert.Equal(1, orderCount);
        }
        else
        {
            // 作废赢：来源已作废，且绝不留下孤儿 / 撕裂的目标订单。
            Assert.Equal(DocumentStatus.Cancelled, pi.Status);
            Assert.Equal(0, orderCount);
        }
    }

    [Fact]
    public async Task 两条独立连接_两张并发普通保存_同一来源至多一张目标订单()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, piId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            userId = user.Id;
            var customer = await SeedCustomerAsync(seed, $"INT_E401_R3_{tag}", employee.Id);
            customerId = customer.Id;
            piId = (await SeedPiAsync(seed, $"INT_E401_PI_R3_{tag}", customer.Id,
                DocumentStatus.Approved)).Id;
        }

        var results = await RaceAsync(
            () => TryCreateAsync(userId, NewOrderBody(customerId, null, piId, piNo: "RACE-A")),
            () => TryCreateAsync(userId, NewOrderBody(customerId, null, piId, piNo: "RACE-B")));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var orders = await verify.SalesOrders.AsNoTracking().Where(o => o.SourcePiId == piId).ToListAsync();
        Assert.Single(orders);
        Assert.Equal($"INT_E401_PI_R3_{tag}", orders[0].SourcePiNo);   // 权威来源号，绝不是提交文本
    }

    // ==================== 6. 锁语句 / 锁序契约 ====================

    [Fact]
    public void 锁语句与确定性锁序_与ERP399_400转换共用同一把来源行锁()
    {
        Assert.Equal(ProformaInvoiceMutationRules.PiRowLockSql,
            SalesOrderSourceLineageRules.ProformaInvoiceRowLockSql);
        Assert.Equal(QuotationMutationRules.QuotationRowLockSql,
            SalesOrderSourceLineageRules.QuotationRowLockSql);
        Assert.Equal(PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql,
            SalesOrderSourceLineageRules.SalesOrderRowLockSql);
        Assert.Contains("UPDLOCK", SalesOrderSourceLineageRules.ProformaInvoiceRowLockSql);
        Assert.Contains("HOLDLOCK", SalesOrderSourceLineageRules.ProformaInvoiceRowLockSql);
        Assert.Equal(SalesOrderSourceLineageRules.LockOrderText, SalesOrderConversion.SourceLockOrderText);
        Assert.Contains("PI 来源行锁", ProformaInvoiceMutationRules.ManualLinkLockOrderText);
        Assert.Contains("报价单来源行锁", QuotationMutationRules.ManualLinkLockOrderText);
    }
}

/// <summary>
/// ERP-401 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库；连接串只来自进程环境变量
/// <c>ERP_ConnectionStrings__Default</c> 或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class SalesOrderSourceLineageSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_SOSOURCELINEAGE_20261009";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-401] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await CreateFreshDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};"
           + "Integrated Security=true;TrustServerCertificate=true;";

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
            // 绝不销毁已存在的夹具库或其它调用方的数据库。
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

        Console.WriteLine("[ERP-401] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class SalesOrderSourceLineageTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => SalesOrderSourceLineageSqlServerFixture.AssertDedicatedTarget(connection));
}
