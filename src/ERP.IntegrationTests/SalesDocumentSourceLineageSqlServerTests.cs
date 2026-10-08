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
/// ERP-403 报价单 / 形式发票 PI 普通表单保存（新增 / 修改 / 提交 / 审核）上游来源血缘护栏的真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <para>直接执行<b>真实业务代码</b>（<see cref="QuotationController"/> + <see cref="ProformaInvoiceController"/> +
/// <see cref="SalesDocumentSourceLineageRules"/> + <see cref="InquiryController"/> 直接转换），不复制测试专用实现：</para>
/// <list type="number">
/// <item>真实既有身份 / 菜单 / 客户范围：无身份、缺来源 / 目标菜单、越客户范围一律 fail closed 且零写入；</item>
/// <item>伪造 / 异客户 / 已删除 / 未审核 / 重复目标来源一律原子拒绝，不写目标单据、不改写来源与状态；</item>
/// <item>普通保存显式链接来源时按来源行<b>规范化</b>来源号（不采信提交文本），来源状态不被伪造；</item>
/// <item>来源行锁的审计时间戳刷新在失败时随事务<b>整体回滚</b>（拒绝后 <c>UpdatedAt</c> 与状态零变化）；</item>
/// <item><b>两条独立连接竞态</b>：普通保存 vs 直接转换、普通保存 vs 来源取消、两张并发普通保存，均只产生一致结果。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只创建<b>全新 GUID 后缀库</b>，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。
/// <b>构建完成不等于阶段验收</b>：只有以下场景在专用 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class SalesDocumentSourceLineageSqlServerTests
    : IClassFixture<SalesDocumentSourceLineageSqlServerFixture>
{
    private readonly SalesDocumentSourceLineageSqlServerFixture _fixture;

    public SalesDocumentSourceLineageSqlServerTests(SalesDocumentSourceLineageSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(SalesDocumentSourceLineageSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 控制器 / 双连接脚手架 ====================

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static DefaultHttpContext HttpFor(long? userId)
    {
        var http = new DefaultHttpContext();
        if (userId.HasValue)
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"));
        return http;
    }

    private static QuotationController NewQuotationController(ErpDbContext db, long? userId)
        => new(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = HttpFor(userId) }
        };

    private static ProformaInvoiceController NewPiController(ErpDbContext db, long? userId)
        => new(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = HttpFor(userId) }
        };

    private static InquiryController NewInquiryController(ErpDbContext db, long? userId)
        => new(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = HttpFor(userId) }
        };

    private async Task<(bool Success, string Error)> TryCreateQuotationAsync(long? userId, Quotation body)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await NewQuotationController(db, userId).Create(body);
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool Success, string Error)> TryUpdateQuotationAsync(long? userId, long id, Quotation body)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await NewQuotationController(db, userId).Update(id, body);
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool Success, string Error)> TryCreatePiAsync(long? userId, ProformaInvoice body)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await NewPiController(db, userId).Create(body);
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool Success, string Error)> TryConvertInquiryAsync(long? userId, long inquiryId)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await NewInquiryController(db, userId).ToQuotation(inquiryId);
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool Success, string Error)> TryConvertQuotationAsync(long? userId, long quotationId)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await NewQuotationController(db, userId).ToProformaInvoice(quotationId);
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool Success, string Error)> TryCancelQuotationAsync(long? userId, long quotationId)
        => await TryAsync(userId, quotationId, static (ctl, id) => ctl.Cancel(id));

    private async Task<(bool Success, string Error)> TryAsync(long? userId, long id,
        Func<QuotationController, long, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await action(NewQuotationController(db, userId), id);
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

    /// <summary>播种受限操作员：登录账号 = 员工编码（ERP-097 权威映射），并按需授予既有报价单 / 询价单 / PI 菜单。</summary>
    private static async Task<(SysUser User, BaseEmployee Employee, SysRole Role)> SeedOperatorAsync(
        ErpDbContext db, bool quotationMenu = true, bool inquiryMenu = true, bool piMenu = true,
        UserStatus status = UserStatus.Enabled)
    {
        var code = $"INT_E403_{Guid.NewGuid():N}";
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
            RoleName = $"E403R_{code}", RoleCode = $"INT_E403_{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        if (quotationMenu) await GrantMenuAsync(db, role.Id, QuotationAuthorizationRules.RequiredMenuCode);
        if (inquiryMenu) await GrantMenuAsync(db, role.Id, InquiryAuthorizationRules.RequiredMenuCode);
        if (piMenu) await GrantMenuAsync(db, role.Id, ProformaInvoiceAuthorizationRules.RequiredMenuCode);
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

    private static async Task<Inquiry> SeedInquiryAsync(ErpDbContext db, string no, long customerId,
        DocumentStatus status = DocumentStatus.Approved)
    {
        var inquiry = new Inquiry
        {
            InquiryNo = no, InquiryDate = DateTime.Today, CustomerId = customerId,
            Currency = Currency.USD, ExchangeRate = 7.2m, ValidDays = 30, Status = status
        };
        inquiry.Details.Add(new InquiryDetail
        {
            ProductId = 1, ProductName = "集成商品", Unit = "PCS", Quantity = 10m, UnitPrice = 100m, Amount = 1000m
        });
        db.Inquiries.Add(inquiry);
        await db.SaveChangesAsync();
        return inquiry;
    }

    private static async Task<Quotation> SeedQuotationAsync(ErpDbContext db, string no, long? customerId,
        DocumentStatus status = DocumentStatus.Approved, Inquiry? source = null)
    {
        var quotation = new Quotation
        {
            QuotationNo = no, QuotationDate = DateTime.Today, CustomerId = customerId, CustomerName = "集成客户",
            InquiryId = source?.Id, InquiryNo = source?.InquiryNo ?? string.Empty,
            Currency = Currency.USD, ExchangeRate = 7.2m, Status = status
        };
        quotation.Details.Add(new QuotationDetail
        {
            SortNo = 1, ProductCode = "INT-E403-Q1", ProductName = "集成商品", Unit = "PCS",
            Quantity = 10m, UnitPrice = 100m, Amount = 1000m
        });
        quotation.TotalAmount = 1000m;
        quotation.TotalAmountCny = 7200m;
        db.Quotations.Add(quotation);
        await db.SaveChangesAsync();
        return quotation;
    }

    private static async Task<ProformaInvoice> SeedPiAsync(ErpDbContext db, string no, long? customerId,
        DocumentStatus status = DocumentStatus.Pending, Quotation? source = null)
    {
        var pi = new ProformaInvoice
        {
            PiNo = no, PiDate = DateTime.Today, CustomerId = customerId, CustomerName = "集成客户",
            QuotationId = source?.Id, QuotationNo = source?.QuotationNo ?? string.Empty,
            Currency = Currency.USD, ExchangeRate = 7.2m, DepositRatio = 30m,
            TotalAmount = 1000m, TotalAmountCny = 7200m, DepositAmount = 300m, Status = status
        };
        pi.Details.Add(new ProformaInvoiceDetail
        {
            SortNo = 1, ProductCode = "INT-E403-P1", ProductName = "集成商品", Unit = "PCS",
            Quantity = 10m, UnitPrice = 100m, Amount = 1000m
        });
        db.ProformaInvoices.Add(pi);
        await db.SaveChangesAsync();
        return pi;
    }

    private static Quotation NewQuotationBody(long customerId, long? inquiryId, string inquiryNo = "")
        => new()
        {
            QuotationDate = DateTime.Today,
            CustomerId = customerId,
            CustomerName = "集成客户",
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            InquiryId = inquiryId,
            InquiryNo = inquiryNo,
            Details = new List<QuotationDetail>
            {
                new()
                {
                    ProductCode = "INT-E403-N1", ProductName = "手工商品", Unit = "PCS",
                    Quantity = 10m, UnitPrice = 100m
                }
            }
        };

    private static ProformaInvoice NewPiBody(long customerId, long? quotationId, string quotationNo = "")
        => new()
        {
            PiDate = DateTime.Today,
            CustomerId = customerId,
            CustomerName = "集成客户",
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            DepositRatio = 30m,
            QuotationId = quotationId,
            QuotationNo = quotationNo,
            Details = new List<ProformaInvoiceDetail>
            {
                new()
                {
                    ProductCode = "INT-E403-N2", ProductName = "手工商品", Unit = "PCS",
                    Quantity = 10m, UnitPrice = 100m
                }
            }
        };

    // ==================== 1. 合法链接 / 未链接手工单据 ====================

    [Fact]
    public async Task 普通保存_报价单显式链接已审核询价单_以权威来源号落库且来源状态零改写()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, inquiryId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            userId = user.Id;
            var customer = await SeedCustomerAsync(seed, $"INT_E403_OK_{tag}", employee.Id);
            customerId = customer.Id;
            inquiryId = (await SeedInquiryAsync(seed, $"INT_E403_INQ_OK_{tag}", customer.Id)).Id;
        }

        // 提交文本故意伪造来源号：服务端一律按来源行规范化，绝不采信文本。
        var result = await TryCreateQuotationAsync(userId, NewQuotationBody(customerId, inquiryId, "FORGED-INQ"));
        Assert.True(result.Success, result.Error);

        await using var verify = _fixture.CreateDbContext();
        var quotation = await verify.Quotations.AsNoTracking().Include(q => q.Details)
            .SingleAsync(q => q.InquiryId == inquiryId);
        Assert.StartsWith("QT", quotation.QuotationNo);
        Assert.Equal(DocumentStatus.Pending, quotation.Status);
        Assert.Equal($"INT_E403_INQ_OK_{tag}", quotation.InquiryNo);
        Assert.Equal(1000m, quotation.TotalAmount);

        var inquiry = await verify.Inquiries.AsNoTracking().SingleAsync(i => i.Id == inquiryId);
        Assert.Equal(DocumentStatus.Approved, inquiry.Status);   // 普通保存不是直接转换，来源状态零改写

        // 未链接的手工报价单保持完全有效（不取任何来源锁）。
        var manual = await TryCreateQuotationAsync(userId, NewQuotationBody(customerId, null));
        Assert.True(manual.Success, manual.Error);
        var manualRow = await verify.Quotations.AsNoTracking()
            .SingleAsync(q => q.CustomerId == customerId && q.InquiryId == null);
        Assert.Equal(string.Empty, manualRow.InquiryNo);
    }

    [Fact]
    public async Task 普通保存_PI显式链接已审核报价单_以权威来源号落库且来源状态零改写()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, quotationId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            userId = user.Id;
            var customer = await SeedCustomerAsync(seed, $"INT_E403_PIOK_{tag}", employee.Id);
            customerId = customer.Id;
            quotationId = (await SeedQuotationAsync(seed, $"INT_E403_QPIOK_{tag}", customer.Id)).Id;
        }

        var result = await TryCreatePiAsync(userId, NewPiBody(customerId, quotationId, "FORGED-QT"));
        Assert.True(result.Success, result.Error);

        await using var verify = _fixture.CreateDbContext();
        var pi = await verify.ProformaInvoices.AsNoTracking()
            .SingleAsync(p => p.QuotationId == quotationId);
        Assert.StartsWith("PI", pi.PiNo);
        Assert.Equal(DocumentStatus.Pending, pi.Status);
        Assert.Equal($"INT_E403_QPIOK_{tag}", pi.QuotationNo);
        Assert.Equal(1000m, pi.TotalAmount);
        Assert.Equal(300m, pi.DepositAmount);

        // 来源报价单状态零改写（普通 PI 保存不是「报价单 → PI」直接转换）。
        Assert.Equal(DocumentStatus.Approved,
            (await verify.Quotations.AsNoTracking().SingleAsync(q => q.Id == quotationId)).Status);
    }

    // ==================== 2. 拒绝矩阵（伪造 / 异客户 / 已删除 / 未审核 / 重复目标） ====================

    [Fact]
    public async Task 报价单拒绝矩阵_异客户已删除未审核重复目标_原子拒绝且来源零改写()
    {
        Guard();
        var tag = Tag();
        long userId, ownCustomerId, foreignInquiryId, deletedInquiryId, pendingInquiryId, dupInquiryId;
        DateTime? pendingUpdatedAtBefore;

        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            userId = user.Id;
            var own = await SeedCustomerAsync(seed, $"INT_E403_QOWN_{tag}", employee.Id);
            var foreign = await SeedCustomerAsync(seed, $"INT_E403_QFR_{tag}");
            ownCustomerId = own.Id;
            foreignInquiryId = (await SeedInquiryAsync(seed, $"INT_E403_INQ_FR_{tag}", foreign.Id)).Id;

            var deleted = await SeedInquiryAsync(seed, $"INT_E403_INQ_DEL_{tag}", own.Id);
            deleted.IsDeleted = true;
            await seed.SaveChangesAsync();
            deletedInquiryId = deleted.Id;

            var pending = await SeedInquiryAsync(seed, $"INT_E403_INQ_PD_{tag}", own.Id, DocumentStatus.Pending);
            pendingInquiryId = pending.Id;
            pendingUpdatedAtBefore = pending.UpdatedAt;

            var dup = await SeedInquiryAsync(seed, $"INT_E403_INQ_DUP_{tag}", own.Id);
            dupInquiryId = dup.Id;
            await SeedQuotationAsync(seed, $"INT_E403_QT_DUP_{tag}", own.Id, source: dup);
        }

        Assert.False((await TryCreateQuotationAsync(userId, NewQuotationBody(ownCustomerId, foreignInquiryId))).Success);
        Assert.False((await TryCreateQuotationAsync(userId, NewQuotationBody(ownCustomerId, deletedInquiryId))).Success);
        Assert.False((await TryCreateQuotationAsync(userId, NewQuotationBody(ownCustomerId, pendingInquiryId))).Success);
        Assert.False((await TryCreateQuotationAsync(userId, NewQuotationBody(ownCustomerId, dupInquiryId))).Success);

        await using var verify = _fixture.CreateDbContext();
        // 拒绝的一律不落库：仅保留种子的那张重复目标报价单。
        Assert.Equal(1, await verify.Quotations.CountAsync(q => q.CustomerId == ownCustomerId));
        Assert.Equal(0, await verify.Quotations.CountAsync(q => q.InquiryId == foreignInquiryId
            || q.InquiryId == deletedInquiryId || q.InquiryId == pendingInquiryId));

        // 来源零改写：已删除来源保持软删除、未审核保持未审核、重复来源保持已审核且时间戳回滚。
        Assert.True((await verify.Inquiries.AsNoTracking().SingleAsync(i => i.Id == deletedInquiryId)).IsDeleted);
        var pendingRow = await verify.Inquiries.AsNoTracking().SingleAsync(i => i.Id == pendingInquiryId);
        Assert.Equal(DocumentStatus.Pending, pendingRow.Status);
        Assert.Equal(pendingUpdatedAtBefore, pendingRow.UpdatedAt);   // 行锁的时间戳刷新随事务回滚
        Assert.Equal(DocumentStatus.Approved,
            (await verify.Inquiries.AsNoTracking().SingleAsync(i => i.Id == dupInquiryId)).Status);
    }

    [Fact]
    public async Task PI拒绝矩阵_异客户已删除未审核重复目标_原子拒绝且来源零改写()
    {
        Guard();
        var tag = Tag();
        long userId, ownCustomerId, foreignQuotationId, deletedQuotationId, pendingQuotationId, dupQuotationId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            userId = user.Id;
            var own = await SeedCustomerAsync(seed, $"INT_E403_POWN_{tag}", employee.Id);
            var foreign = await SeedCustomerAsync(seed, $"INT_E403_PFR_{tag}");
            ownCustomerId = own.Id;
            foreignQuotationId = (await SeedQuotationAsync(seed, $"INT_E403_QFR_{tag}", foreign.Id)).Id;

            var deleted = await SeedQuotationAsync(seed, $"INT_E403_QDEL_{tag}", own.Id);
            deleted.IsDeleted = true;
            await seed.SaveChangesAsync();
            deletedQuotationId = deleted.Id;

            pendingQuotationId = (await SeedQuotationAsync(seed, $"INT_E403_QPD_{tag}", own.Id,
                DocumentStatus.Pending)).Id;

            var dup = await SeedQuotationAsync(seed, $"INT_E403_QDUP_{tag}", own.Id);
            dupQuotationId = dup.Id;
            await SeedPiAsync(seed, $"INT_E403_PI_DUP_{tag}", own.Id, DocumentStatus.Pending, dup);
        }

        Assert.False((await TryCreatePiAsync(userId, NewPiBody(ownCustomerId, foreignQuotationId))).Success);
        Assert.False((await TryCreatePiAsync(userId, NewPiBody(ownCustomerId, deletedQuotationId))).Success);
        Assert.False((await TryCreatePiAsync(userId, NewPiBody(ownCustomerId, pendingQuotationId))).Success);
        Assert.False((await TryCreatePiAsync(userId, NewPiBody(ownCustomerId, dupQuotationId))).Success);

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(1, await verify.ProformaInvoices.CountAsync(p => p.CustomerId == ownCustomerId));
        Assert.Equal(0, await verify.ProformaInvoices.CountAsync(p => p.QuotationId == foreignQuotationId
            || p.QuotationId == deletedQuotationId || p.QuotationId == pendingQuotationId));
        Assert.True((await verify.Quotations.AsNoTracking()
            .SingleAsync(q => q.Id == deletedQuotationId)).IsDeleted);
        Assert.Equal(DocumentStatus.Pending, (await verify.Quotations.AsNoTracking()
            .SingleAsync(q => q.Id == pendingQuotationId)).Status);
    }

    // ==================== 3. 两条独立连接的竞态 ====================

    private async Task<(bool Success, string Error)> TryCancelInquiryAsync(long? userId, long inquiryId)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await NewInquiryController(db, userId).Cancel(inquiryId);
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    [Fact]
    public async Task 两条独立连接_普通报价单保存与询价直接转换_同一询价单至多一张报价单()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, inquiryId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            userId = user.Id;
            var customer = await SeedCustomerAsync(seed, $"INT_E403_R1_{tag}", employee.Id);
            customerId = customer.Id;
            inquiryId = (await SeedInquiryAsync(seed, $"INT_E403_INQ_R1_{tag}", customer.Id)).Id;
        }

        var results = await RaceAsync(
            () => TryCreateQuotationAsync(userId, NewQuotationBody(customerId, inquiryId)),
            () => TryConvertInquiryAsync(userId, inquiryId));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var quotations = await verify.Quotations.AsNoTracking()
            .Where(q => q.InquiryId == inquiryId).ToListAsync();
        Assert.Single(quotations);   // 同一询价单至多一张有效报价单

        var inquiry = await verify.Inquiries.AsNoTracking().SingleAsync(i => i.Id == inquiryId);
        // 直接转换赢：来源置「已完成」；普通保存赢：来源保持「已审核」（转换随后按重复规则被拒）。
        Assert.Equal(results[1].Success ? DocumentStatus.Completed : DocumentStatus.Approved, inquiry.Status);
    }

    [Fact]
    public async Task 两条独立连接_普通PI保存与报价单直接转换_同一报价单至多一张PI()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, quotationId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            userId = user.Id;
            var customer = await SeedCustomerAsync(seed, $"INT_E403_R2_{tag}", employee.Id);
            customerId = customer.Id;
            quotationId = (await SeedQuotationAsync(seed, $"INT_E403_Q_R2_{tag}", customer.Id)).Id;
        }

        var results = await RaceAsync(
            () => TryCreatePiAsync(userId, NewPiBody(customerId, quotationId)),
            () => TryConvertQuotationAsync(userId, quotationId));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var pis = await verify.ProformaInvoices.AsNoTracking()
            .Where(p => p.QuotationId == quotationId).ToListAsync();
        Assert.Single(pis);   // 同一报价单至多一张有效 PI

        var quotation = await verify.Quotations.AsNoTracking().SingleAsync(q => q.Id == quotationId);
        Assert.Equal(results[1].Success ? DocumentStatus.Completed : DocumentStatus.Approved, quotation.Status);
    }

    [Fact]
    public async Task 两条独立连接_普通报价单保存与来源询价单取消_只产生一致的历史()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, inquiryId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            userId = user.Id;
            var customer = await SeedCustomerAsync(seed, $"INT_E403_R3_{tag}", employee.Id);
            customerId = customer.Id;
            inquiryId = (await SeedInquiryAsync(seed, $"INT_E403_INQ_R3_{tag}", customer.Id)).Id;
        }

        var results = await RaceAsync(
            () => TryCreateQuotationAsync(userId, NewQuotationBody(customerId, inquiryId)),
            () => TryCancelInquiryAsync(userId, inquiryId));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var quotationCount = await verify.Quotations.AsNoTracking().CountAsync(q => q.InquiryId == inquiryId);
        var inquiry = await verify.Inquiries.AsNoTracking().SingleAsync(i => i.Id == inquiryId);

        if (results[0].Success)
        {
            // 保存赢：恰有一张报价单，来源保持已审核（随后取消因存在实时报价单下游被拒）。
            Assert.Equal(DocumentStatus.Approved, inquiry.Status);
            Assert.Equal(1, quotationCount);
        }
        else
        {
            // 取消赢：来源已取消，绝不留下孤儿 / 撕裂的报价单。
            Assert.Equal(DocumentStatus.Cancelled, inquiry.Status);
            Assert.Equal(0, quotationCount);
        }
    }

    [Fact]
    public async Task 两条独立连接_两张并发普通报价单保存_同一询价单至多一张报价单()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, inquiryId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            userId = user.Id;
            var customer = await SeedCustomerAsync(seed, $"INT_E403_R4_{tag}", employee.Id);
            customerId = customer.Id;
            inquiryId = (await SeedInquiryAsync(seed, $"INT_E403_INQ_R4_{tag}", customer.Id)).Id;
        }

        var results = await RaceAsync(
            () => TryCreateQuotationAsync(userId, NewQuotationBody(customerId, inquiryId, "RACE-A")),
            () => TryCreateQuotationAsync(userId, NewQuotationBody(customerId, inquiryId, "RACE-B")));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var quotations = await verify.Quotations.AsNoTracking().Where(q => q.InquiryId == inquiryId).ToListAsync();
        Assert.Single(quotations);
        Assert.Equal($"INT_E403_INQ_R4_{tag}", quotations[0].InquiryNo);   // 权威来源号，绝不是提交文本
    }

    // ==================== 4. 锁语句 / 锁序契约 ====================

    [Fact]
    public void 锁语句与确定性锁序_与ERP402_400转换共用同一把来源行锁()
    {
        Assert.Equal(InquiryMutationRules.InquiryRowLockSql, SalesDocumentSourceLineageRules.InquiryRowLockSql);
        Assert.Equal(QuotationMutationRules.QuotationRowLockSql, SalesDocumentSourceLineageRules.QuotationRowLockSql);
        Assert.Equal(ProformaInvoiceMutationRules.PiRowLockSql,
            SalesDocumentSourceLineageRules.ProformaInvoiceRowLockSql);

        Assert.Contains("db_owner.Inquiries", SalesDocumentSourceLineageRules.InquiryRowLockSql);
        Assert.Contains("db_owner.Quotations", SalesDocumentSourceLineageRules.QuotationRowLockSql);
        Assert.Equal(SalesDocumentSourceLineageRules.LockOrderText,
            InquiryQuotationConversion.SourceLineageLockOrderText);
        Assert.Contains("绝不反向获取", SalesDocumentSourceLineageRules.LockOrderText);
    }

    // ==================== 5. 真实既有授权 fail closed ====================

    [Fact]
    public async Task 真实既有授权_无身份与缺来源菜单与越界一律拒绝且零写入()
    {
        Guard();
        var tag = Tag();
        long operatorUserId, ownCustomerId, ownInquiryId, foreignCustomerId, foreignInquiryId, quotationOnlyUserId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            operatorUserId = user.Id;
            var own = await SeedCustomerAsync(seed, $"INT_E403_AOWN_{tag}", employee.Id);
            ownCustomerId = own.Id;
            ownInquiryId = (await SeedInquiryAsync(seed, $"INT_E403_INQ_A_{tag}", own.Id)).Id;
            var foreign = await SeedCustomerAsync(seed, $"INT_E403_AFR_{tag}");
            foreignCustomerId = foreign.Id;
            foreignInquiryId = (await SeedInquiryAsync(seed, $"INT_E403_INQ_AFR_{tag}", foreign.Id)).Id;
        }

        await using (var seed = _fixture.CreateDbContext())
        {
            // 仅授予报价单菜单（缺来源询价单菜单）→ 来源菜单 fail closed。
            var (user, _, _) = await SeedOperatorAsync(seed, inquiryMenu: false);
            quotationOnlyUserId = user.Id;
        }

        // 无身份：未认证拒绝。
        Assert.False((await TryCreateQuotationAsync(null, NewQuotationBody(ownCustomerId, ownInquiryId))).Success);
        // 缺来源菜单：非特权账号 fail closed（同时复核目标 + 来源双菜单）。
        Assert.False((await TryCreateQuotationAsync(quotationOnlyUserId,
            NewQuotationBody(ownCustomerId, ownInquiryId))).Success);
        // 越客户范围（他人的客户 / 来源）：拒绝。
        Assert.False((await TryCreateQuotationAsync(operatorUserId,
            NewQuotationBody(foreignCustomerId, foreignInquiryId))).Success);

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(0, await verify.Quotations.CountAsync(q => q.CustomerId == ownCustomerId
            || q.CustomerId == foreignCustomerId));
    }
}

/// <summary>
/// ERP-403 专用 localdb 夹具：仅创建<b>全新 GUID 后缀</b>的 <c>NEWERP_AUTOTEST</c> 库，发现同名库已存在立即拒绝；
/// 绝不 drop / reset / 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值。
/// </summary>
public sealed class SalesDocumentSourceLineageSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_SRCLINEAGE_20261009";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-403] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-403] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class SalesDocumentSourceLineageTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => SalesDocumentSourceLineageSqlServerFixture.AssertDedicatedTarget(connection));
}