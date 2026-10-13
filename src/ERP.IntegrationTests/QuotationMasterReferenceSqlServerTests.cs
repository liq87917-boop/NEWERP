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
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using System.Reflection;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-472 报价单「实时主数据引用」护栏的**真实 SQL Server** 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实控制器 + 真实既有授权</b>：以既有系统内置角色（特权）与既有「报价单」功能菜单 + 业务员客户数据范围
/// 驱动真实 <see cref="QuotationController"/>；不新增 / 不修改任何菜单 / 角色 / 用户授权，无匿名 / 管理员降级。</item>
/// <item><b>受控错误与零写入</b>：缺失 / 已删除 / 已停用客户，缺失（无自带文本）/ 已删除 / 已停用 / 零 Id 商品，
/// 已删除 / 非业务员业务员，均在单号预约与任何赋值之前被受控业务错误拒绝；
/// <c>Quotations</c> / <c>QuotationDetails</c> / <c>Inquiries</c> 与报价单号流水全部不变。</item>
/// <item><b>可空引用与手工快照行</b>：未指定客户 / 业务员与手工商品快照行（<c>ProductId = null</c>、或悬空 Id + 自带
/// 受界文本）照常保存、提交、审核，且悬空 Id 与文本原样保留（不回填 / 不改派）。</item>
/// <item><b>锁内重查</b>：草稿保存后客户被删除 / 商品被停用时提交 / 审核原子拒绝，状态与原始证据不变；
/// 创建版本不把失效引用复制成新草稿；询价转报价失败绝不消耗单号、绝不落库、绝不改写来源询价单状态。</item>
/// <item><b>历史读取 / 打印只读</b>：引用失效主数据的历史报价单仍可读、不被回填。</item>
/// <item><b>商品批量查询</b>：多条明细引用多个 / 重复商品时，商品资料只发生**一次**查询（命令拦截计数）。</item>
/// <item><b>专用目标护栏</b>：任何数据库访问之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> +
/// <c>NEWERP_AUTOTEST</c> 前缀 + <c>Integrated Security</c>；每次运行只创建一个全新 GUID 库，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何库，也绝不读取 appsettings / .env / 生产凭据。</item>
/// </list>
/// <para>保留既有报价单来源审计与原始失败日志：本测试只读取计数与只读快照，不删除 / 不清理任何既有行。
/// 构建完成不等于阶段验收：只有本文件在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class QuotationMasterReferenceSqlServerTests
    : IClassFixture<QuotationMasterReferenceSqlServerFixture>
{
    private readonly QuotationMasterReferenceSqlServerFixture _fixture;

    public QuotationMasterReferenceSqlServerTests(QuotationMasterReferenceSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>专用目标护栏（任何数据库访问之前）。</summary>
    private void Guard() => QuotationMasterReferenceSqlServerFixture
        .AssertDedicatedTarget(_fixture.ConnectionString);

    private static QuotationController NewController(ErpDbContext db, long userId)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/sales/quotations";
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"));

        return new QuotationController(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static InquiryController NewInquiryController(ErpDbContext db, long userId)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/inquiries";
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"));

        return new InquiryController(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private async Task<Dictionary<string, int>> SnapshotAsync()
    {
        await using var db = _fixture.CreateDbContext();
        return new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Quotations"] = await db.Quotations.CountAsync(),
            ["QuotationDetails"] = await db.QuotationDetails.CountAsync(),
            ["Inquiries"] = await db.Inquiries.CountAsync(),
            ["ProformaInvoices"] = await db.ProformaInvoices.CountAsync(),
            ["SalesOrders"] = await db.SalesOrders.CountAsync(),
        };
    }

    private static void AssertSnapshotUnchanged(
        IReadOnlyDictionary<string, int> before, IReadOnlyDictionary<string, int> after)
    {
        foreach (var table in before.Keys)
            Assert.Equal(before[table], after[table]);
    }

    private static Quotation NewQuotationBody(long? customerId, params QuotationDetail[] details)
        => new()
        {
            QuotationDate = DateTime.Today,
            CustomerId = customerId,
            CustomerName = "ERP472 集成客户",
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            Details = details.ToList()
        };

    private static QuotationDetail ManualDetail(string code, string name)
        => new() { ProductCode = code, ProductName = name, Unit = "PCS", Quantity = 2m, UnitPrice = 5m };

    private static QuotationDetail LinkedDetail(string code, string name, long? productId)
        => new() { ProductId = productId, ProductCode = code, ProductName = name, Unit = "PCS", Quantity = 2m, UnitPrice = 5m };

    private static long CreatedId(IActionResult result)
    {
        var data = Assert.IsType<ApiResponse<object>>(Assert.IsType<OkObjectResult>(result).Value).Data!;
        return (long)data.GetType().GetProperty(nameof(Quotation.Id))!.GetValue(data)!;
    }

    // ==================== 1. 新增：失效引用受控拒绝、零写入、零占号 ====================

    [Fact]
    public async Task 新增_失效引用_真实SQL受控拒绝且零写入零占号()
    {
        Guard();
        var before = await SnapshotAsync();
        var sequenceBefore = await _fixture.QuotationSequenceAsync();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.PrivilegedUserId);

        var missingCustomer = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewQuotationBody(_fixture.MissingCustomerId, ManualDetail("P-1", "手工商品"))));
        Assert.Equal(ErrorCodes.NotFound, missingCustomer.Code);

        var deletedCustomer = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewQuotationBody(_fixture.DeletedCustomerId, ManualDetail("P-2", "手工商品"))));
        Assert.Equal(ErrorCodes.NotFound, deletedCustomer.Code);

        var disabledCustomer = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewQuotationBody(_fixture.DisabledCustomerId, ManualDetail("P-3", "手工商品"))));
        Assert.Equal(ErrorCodes.RuleConflict, disabledCustomer.Code);

        var zeroCustomer = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewQuotationBody(0L, ManualDetail("P-4", "手工商品"))));
        Assert.Equal(ErrorCodes.InvalidParameter, zeroCustomer.Code);

        var disabledProduct = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewQuotationBody(null, LinkedDetail("P-5", "已停用商品", _fixture.DisabledProductId))));
        Assert.Equal(ErrorCodes.RuleConflict, disabledProduct.Code);

        var deletedProduct = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewQuotationBody(null, LinkedDetail("P-6", "已删除商品", _fixture.DeletedProductId))));
        Assert.Equal(ErrorCodes.NotFound, deletedProduct.Code);

        var missingProduct = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewQuotationBody(null,
                LinkedDetail(string.Empty, string.Empty, _fixture.MissingProductId))));
        Assert.Equal(ErrorCodes.NotFound, missingProduct.Code);

        var zeroProduct = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewQuotationBody(null, LinkedDetail("P-8", "零 Id 商品", 0L))));
        Assert.Equal(ErrorCodes.InvalidParameter, zeroProduct.Code);

        var deletedSalesman = await Assert.ThrowsAsync<BusinessException>(() =>
        {
            var body = NewQuotationBody(null, ManualDetail("P-9", "手工商品"));
            body.SalesmanId = _fixture.DeletedEmployeeId;
            return ctl.Create(body);
        });
        Assert.Equal(ErrorCodes.NotFound, deletedSalesman.Code);

        var disabledSalesman = await Assert.ThrowsAsync<BusinessException>(() =>
        {
            var body = NewQuotationBody(null, ManualDetail("P-10", "手工商品"));
            body.SalesmanId = _fixture.DisabledEmployeeId;
            return ctl.Create(body);
        });
        Assert.Equal(ErrorCodes.RuleConflict, disabledSalesman.Code);

        var notSalesman = await Assert.ThrowsAsync<BusinessException>(() =>
        {
            var body = NewQuotationBody(null, ManualDetail("P-11", "手工商品"));
            body.SalesmanId = _fixture.NotSalesmanEmployeeId;
            return ctl.Create(body);
        });
        Assert.Equal(ErrorCodes.RuleConflict, notSalesman.Code);

        AssertSnapshotUnchanged(before, await SnapshotAsync());
        Assert.Equal(sequenceBefore, await _fixture.QuotationSequenceAsync());   // 被拒绝的请求绝不消耗单号
    }

    // ==================== 2. 可空引用与手工快照行 ====================

    [Fact]
    public async Task 新增_可空引用与手工快照行_真实SQL完整生命周期可用且原样保留()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.PrivilegedUserId);

        var body = NewQuotationBody(null, ManualDetail("FREE-1", "手工商品"), ManualDetail("FREE-2", "自由文本商品"));
        var id = CreatedId(await ctl.Create(body));

        await using (var verify = _fixture.CreateDbContext())
        {
            var quotation = await verify.Quotations.AsNoTracking().Include(q => q.Details)
                .SingleAsync(q => q.Id == id);
            Assert.Null(quotation.CustomerId);
            Assert.Null(quotation.SalesmanId);
            Assert.All(quotation.Details, d => Assert.Null(d.ProductId));
            Assert.Equal(20m, quotation.TotalAmount);          // 2 行 × 2 × 5
            Assert.Equal(144m, quotation.TotalAmountCny);      // 20 × 7.2
        }

        Assert.IsType<OkObjectResult>(await ctl.Submit(id));
        Assert.IsType<OkObjectResult>(await ctl.Approve(id));

        await using (var verify = _fixture.CreateDbContext())
        {
            var quotation = await verify.Quotations.AsNoTracking().SingleAsync(q => q.Id == id);
            Assert.Equal(DocumentStatus.Approved, quotation.Status);
            Assert.Equal(2, await verify.QuotationDetails.CountAsync(d => d.QuotationId == id && !d.IsDeleted));
        }
    }

    [Fact]
    public async Task 新增_悬空商品Id但自带受界文本_按手工快照行原样保留()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.PrivilegedUserId);

        var id = CreatedId(await ctl.Create(NewQuotationBody(null,
            LinkedDetail("P-LEGACY", "历史手工商品", _fixture.MissingProductId))));

        await using var verify = _fixture.CreateDbContext();
        var detail = await verify.QuotationDetails.AsNoTracking().SingleAsync(d => d.QuotationId == id);
        Assert.Equal(_fixture.MissingProductId, detail.ProductId);      // 原样保留，绝不回填 / 改派
        Assert.Equal("历史手工商品", detail.ProductName);
    }

    // ==================== 3. 修改：失效引用零变更 ====================

    [Fact]
    public async Task 修改_失效引用_真实SQL拒绝且原表头明细与单号全部不变()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.PrivilegedUserId);

        var id = CreatedId(await ctl.Create(NewQuotationBody(_fixture.CustomerId,
            LinkedDetail("P-1", "商品 A", _fixture.ProductId))));

        var update = NewQuotationBody(_fixture.MissingCustomerId, ManualDetail("P-9", "手工商品"));
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(id, update));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);

        await using var verify = _fixture.CreateDbContext();
        var after = await verify.Quotations.AsNoTracking().Include(q => q.Details).SingleAsync(q => q.Id == id);
        Assert.Equal(_fixture.CustomerId, after.CustomerId);
        Assert.Equal("商品 A", after.Details.Single().ProductName);
        Assert.Equal(_fixture.ProductId, after.Details.Single().ProductId);
        Assert.Equal(10m, after.TotalAmount);
        Assert.NotEmpty(after.QuotationNo);

        // 修改失败绝不改写明细：原明细行数不变。
        Assert.Equal(1, await verify.QuotationDetails.CountAsync(d => d.QuotationId == id));
    }

    // ==================== 4. 提交 / 审核：锁内重查引用 ====================

    [Fact]
    public async Task 提交_草稿保存后客户被删除_真实SQL原子拒绝且状态不变()
    {
        Guard();
        var (customerId, productId) = await _fixture.SeedTransientMastersAsync();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.PrivilegedUserId);
        var id = CreatedId(await ctl.Create(NewQuotationBody(customerId,
            LinkedDetail("T-1", "临时商品", productId))));

        await using (var mutate = _fixture.CreateDbContext())
        {
            mutate.BaseCustomers.Single(c => c.Id == customerId).IsDeleted = true;
            await mutate.SaveChangesAsync();
        }

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(id));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Pending, (await verify.Quotations.AsNoTracking().SingleAsync(q => q.Id == id)).Status);
    }

    [Fact]
    public async Task 审核_草稿保存后商品被停用_真实SQL原子拒绝且状态不变()
    {
        Guard();
        var (customerId, productId) = await _fixture.SeedTransientMastersAsync();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.PrivilegedUserId);
        var id = CreatedId(await ctl.Create(NewQuotationBody(customerId,
            LinkedDetail("T-2", "临时商品", productId))));

        Assert.IsType<OkObjectResult>(await ctl.Submit(id));
        await using (var mutate = _fixture.CreateDbContext())
        {
            mutate.BaseProducts.Single(p => p.Id == productId).Status = 0;
            await mutate.SaveChangesAsync();
        }

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已停用", ex.Message);

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Submitted,
            (await verify.Quotations.AsNoTracking().SingleAsync(q => q.Id == id)).Status);
    }

    // ==================== 5. 创建版本：失效引用零变更 ====================

    [Fact]
    public async Task 创建版本_源客户已删除_真实SQL拒绝且不新增版本不改源()
    {
        Guard();
        var (customerId, productId) = await _fixture.SeedTransientMastersAsync();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.PrivilegedUserId);
        var id = CreatedId(await ctl.Create(NewQuotationBody(customerId,
            LinkedDetail("T-3", "临时商品", productId))));
        Assert.IsType<OkObjectResult>(await ctl.Approve(id));

        await using (var mutate = _fixture.CreateDbContext())
        {
            mutate.BaseCustomers.Single(c => c.Id == customerId).IsDeleted = true;
            await mutate.SaveChangesAsync();
        }

        var before = await SnapshotAsync();
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.CreateRevision(id));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);

        AssertSnapshotUnchanged(before, await SnapshotAsync());
        await using var verify = _fixture.CreateDbContext();
        var source = await verify.Quotations.AsNoTracking().SingleAsync(q => q.Id == id);
        Assert.Equal(DocumentStatus.Approved, source.Status);
        Assert.Null(source.RootQuotationId);
        Assert.Equal(1, source.RevisionNumber);
    }

    // ==================== 6. 询价转报价：失败绝不改写来源 ====================

    [Fact]
    public async Task 询价转报价_来源商品已停用_真实SQL拒绝且来源状态写入单号全部不变()
    {
        Guard();
        var (customerId, productId) = await _fixture.SeedTransientMastersAsync();
        var inquiryId = await _fixture.SeedInquiryAsync(customerId, productId);
        await using (var mutate = _fixture.CreateDbContext())
        {
            mutate.BaseProducts.Single(p => p.Id == productId).Status = 0;
            await mutate.SaveChangesAsync();
        }

        var before = await SnapshotAsync();
        var sequenceBefore = await _fixture.QuotationSequenceAsync();
        await using var db = _fixture.CreateDbContext();
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewInquiryController(db, _fixture.PrivilegedUserId).ToQuotation(inquiryId));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已停用", ex.Message);
        AssertSnapshotUnchanged(before, await SnapshotAsync());
        Assert.Equal(sequenceBefore, await _fixture.QuotationSequenceAsync());

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Approved,
            (await verify.Inquiries.AsNoTracking().SingleAsync(i => i.Id == inquiryId)).Status);
    }

    [Fact]
    public async Task 询价转报价_合法引用_真实SQL生成报价单并置来源已完成()
    {
        Guard();
        var (customerId, productId) = await _fixture.SeedTransientMastersAsync();
        var inquiryId = await _fixture.SeedInquiryAsync(customerId, productId);
        await using var db = _fixture.CreateDbContext();

        Assert.IsType<OkObjectResult>(
            await NewInquiryController(db, _fixture.PrivilegedUserId).ToQuotation(inquiryId));

        await using var verify = _fixture.CreateDbContext();
        var quotation = await verify.Quotations.AsNoTracking().Include(q => q.Details)
            .SingleAsync(q => q.InquiryId == inquiryId);
        Assert.Equal(customerId, quotation.CustomerId);
        Assert.Equal(productId, quotation.Details.Single().ProductId);
        Assert.Equal(DocumentStatus.Completed,
            (await verify.Inquiries.AsNoTracking().SingleAsync(i => i.Id == inquiryId)).Status);
    }

    // ==================== 7. 历史读取 / 打印只读 ====================

    [Fact]
    public async Task 历史报价单_引用失效_真实SQL读取打印照原样且不被回填()
    {
        Guard();
        var (customerId, productId) = await _fixture.SeedTransientMastersAsync();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.PrivilegedUserId);
        var id = CreatedId(await ctl.Create(NewQuotationBody(customerId,
            LinkedDetail("T-4", "临时商品", productId))));
        Assert.IsType<OkObjectResult>(await ctl.Approve(id));

        await using (var mutate = _fixture.CreateDbContext())
        {
            mutate.BaseCustomers.Single(c => c.Id == customerId).IsDeleted = true;
            mutate.BaseProducts.Single(p => p.Id == productId).IsDeleted = true;
            await mutate.SaveChangesAsync();
        }

        var read = Assert.IsType<ApiResponse<Quotation>>(
            Assert.IsType<OkObjectResult>(await ctl.GetById(id)).Value).Data!;
        Assert.Equal(customerId, read.CustomerId);
        Assert.Equal(productId, read.Details.Single().ProductId);

        var print = Assert.IsType<ApiResponse<Quotation>>(
            Assert.IsType<OkObjectResult>(await ctl.GetPrint(id)).Value).Data!;
        Assert.Equal(productId, print.Details.Single().ProductId);
        Assert.Equal(10m, print.TotalAmount);
    }

    // ==================== 8. 授权正向：受限账号在范围内可正常落库 ====================

    [Fact]
    public async Task 新增_受限账号在范围内_真实SQL手工快照行放行()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.RestrictedUserId);

        var id = CreatedId(await ctl.Create(NewQuotationBody(_fixture.CustomerId,
            ManualDetail("R-1", "手工商品"))));

        await using var verify = _fixture.CreateDbContext();
        var quotation = await verify.Quotations.AsNoTracking().SingleAsync(q => q.Id == id);
        Assert.Equal(_fixture.CustomerId, quotation.CustomerId);
        Assert.Null((await verify.QuotationDetails.AsNoTracking().SingleAsync(d => d.QuotationId == id)).ProductId);
    }

    // ==================== 9. 商品批量查询：绝不逐行查库 ====================

    [Fact]
    public async Task 新增_多行多商品_真实SQL只发生一次商品查询()
    {
        Guard();
        var (customerId, productId) = await _fixture.SeedTransientMastersAsync();
        var (_, secondProductId) = await _fixture.SeedTransientMastersAsync();
        var counter = new ProductQueryCounter();
        await using var db = _fixture.CreateDbContext(counter);
        var ctl = NewController(db, _fixture.PrivilegedUserId);

        // 4 行明细引用 2 个不同商品（含重复引用与手工快照行）→ 商品资料只查询一次。
        var body = NewQuotationBody(customerId,
            LinkedDetail("B-1", "商品一", productId),
            LinkedDetail("B-2", "商品二", secondProductId),
            LinkedDetail("B-3", "商品一（重复引用）", productId),
            ManualDetail("B-4", "手工快照行"));

        Assert.IsType<OkObjectResult>(await ctl.Create(body));
        Assert.Equal(1, counter.BaseProductQueries);
    }

    // ==================== 9. 授权前置（非披露） ====================

    [Fact]
    public async Task 新增_受限账号缺报价单菜单_即便引用失效也返回权限错误且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.NoMenuUserId)
                .Create(NewQuotationBody(_fixture.MissingCustomerId, ManualDetail("A-1", "手工商品"))));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        AssertSnapshotUnchanged(before, await SnapshotAsync());
    }

    /// <summary>统计 <c>BaseProducts</c> 查询次数的命令拦截器（证明商品资料一次有界批量查询）。</summary>
    private sealed class ProductQueryCounter : DbCommandInterceptor
    {
        public int BaseProductQueries { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Count(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Count(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Count(DbCommand command)
        {
            if (command.CommandText.Contains("BaseProducts", StringComparison.OrdinalIgnoreCase))
                BaseProductQueries++;
        }
    }
}

/// <summary>
/// ERP-472 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class QuotationMasterReferenceSqlServerFixture : IAsyncLifetime
{
    /// <summary>专用实例（精确匹配）。</summary>
    public const string InstanceTarget = @"(localdb)\NEWERP_AutoAcceptance";

    /// <summary>库名前缀（必须为 NEWERP_AUTOTEST）。</summary>
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    private const string DefaultDatabaseName = DatabasePrefix + "_QTMASTERREF";

    private long _quotationNumberRuleId;
    private long _restrictedEmployeeId;

    public string ConnectionString { get; private set; } = string.Empty;

    public long PrivilegedUserId { get; private set; }
    public long RestrictedUserId { get; private set; }
    public long NoMenuUserId { get; private set; }
    public long CustomerId { get; private set; }
    public long DeletedCustomerId { get; private set; }
    public long DisabledCustomerId { get; private set; }
    public long ProductId { get; private set; }
    public long DeletedProductId { get; private set; }
    public long DisabledProductId { get; private set; }
    public long EmployeeId { get; private set; }
    public long DeletedEmployeeId { get; private set; }
    public long DisabledEmployeeId { get; private set; }
    public long NotSalesmanEmployeeId { get; private set; }

    /// <summary>故意不存在的客户 Id（缺失引用场景）。</summary>
    public long MissingCustomerId => 9_472_001L;

    /// <summary>故意不存在的商品 Id（缺失引用场景）。</summary>
    public long MissingProductId => 9_472_002L;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-472] 目标库护栏放行（实例 {InstanceTarget}，库名前缀 {DatabasePrefix}）。");
        await CreateFreshDatabaseAsync();
        await SeedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    /// <summary>带命令拦截器的上下文（用于统计商品查询次数）。</summary>
    public ErpDbContext CreateDbContext(DbCommandInterceptor interceptor) => new(BuildOptions(interceptor));

    private DbContextOptions<ErpDbContext> BuildOptions(DbCommandInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString);
        if (interceptor is not null) builder.AddInterceptors(interceptor);
        return builder.Options;
    }

    private static string BuildDefaultConnectionString()
        => $"Server={InstanceTarget};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};"
           + "Integrated Security=true;TrustServerCertificate=true;";

    /// <summary>专用目标护栏：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
    public static void AssertDedicatedTarget(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        Assert.Equal(InstanceTarget, builder.DataSource ?? string.Empty, ignoreCase: true);
        Assert.StartsWith(DatabasePrefix, builder.InitialCatalog ?? string.Empty, StringComparison.OrdinalIgnoreCase);
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

        Console.WriteLine("[ERP-472] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }

    // ==================== 既有授权（不新增权限模型）+ 主数据夹具 ====================

    private async Task SeedAsync()
    {
        await using var db = CreateDbContext();

        // 1) 特权账号（系统内置角色，沿用既有全部访问口径）。
        var privilegedRole = new SysRole
        {
            RoleName = "ERP472 特权角色", RoleCode = $"ERP472-P-{Guid.NewGuid():N}", IsSystem = true
        };
        db.SysRoles.Add(privilegedRole);
        await db.SaveChangesAsync();
        var privileged = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(privileged);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = privileged.Id, RoleId = privilegedRole.Id });
        await db.SaveChangesAsync();
        PrivilegedUserId = privileged.Id;

        // 2) 受限业务员：既有「报价单」功能菜单 + 客户数据范围（登录账号 == 员工编码，ERP-097 权威映射）。
        var restricted = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(restricted);
        await db.SaveChangesAsync();
        var restrictedRole = new SysRole
        {
            RoleName = $"ERP472-{Guid.NewGuid():N}", RoleCode = $"ERP472-{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(restrictedRole);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = restricted.Id, RoleId = restrictedRole.Id });
        var menu = await db.SysMenus.FirstOrDefaultAsync(m =>
            m.MenuCode == QuotationAuthorizationRules.RequiredMenuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu
            {
                MenuName = QuotationAuthorizationRules.RequiredMenuText,
                MenuCode = QuotationAuthorizationRules.RequiredMenuCode,
                MenuType = MenuType.Menu
            };
            db.SysMenus.Add(menu);
            await db.SaveChangesAsync();
        }
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = restrictedRole.Id, MenuId = menu.Id });
        await db.SaveChangesAsync();
        RestrictedUserId = restricted.Id;

        var restrictedEmployee = new BaseEmployee
        {
            EmployeeCode = restricted.UserName, EmployeeName = "ERP472 受限业务员", IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(restrictedEmployee);
        await db.SaveChangesAsync();
        _restrictedEmployeeId = restrictedEmployee.Id;

        // 3) 无菜单账号：既有菜单授权 fail closed（非披露：引用是否有效不影响授权结果）。
        var noMenu = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(noMenu);
        await db.SaveChangesAsync();
        var noMenuRole = new SysRole
        {
            RoleName = $"ERP472-NM-{Guid.NewGuid():N}", RoleCode = $"ERP472-NM-{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(noMenuRole);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = noMenu.Id, RoleId = noMenuRole.Id });
        await db.SaveChangesAsync();
        NoMenuUserId = noMenu.Id;

        await SeedMastersAsync(db);
        await SeedQuotationNumberRuleAsync(db);
    }

    /// <summary>既有主数据夹具：客户（启用 / 已删除 / 已停用）、员工（在职业务员 / 已删除 / 已停用 / 非业务员）、商品（启用 / 已删除 / 已停用）。</summary>
    private async Task SeedMastersAsync(ErpDbContext db)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-472-{Guid.NewGuid():N}", CustomerName = "ERP472 可见客户",
            EmpId = _restrictedEmployeeId,
            Status = 1, CreditStatus = "正常", DepositRatio = 30m
        };
        var deletedCustomer = new BaseCustomer
        {
            CustomerCode = $"C-472D-{Guid.NewGuid():N}", CustomerName = "ERP472 已删除客户",
            Status = 1, CreditStatus = "正常", DepositRatio = 30m, IsDeleted = true
        };
        var disabledCustomer = new BaseCustomer
        {
            CustomerCode = $"C-472S-{Guid.NewGuid():N}", CustomerName = "ERP472 已停用客户",
            Status = 0, CreditStatus = "正常", DepositRatio = 30m
        };
        db.BaseCustomers.AddRange(customer, deletedCustomer, disabledCustomer);
        await db.SaveChangesAsync();
        CustomerId = customer.Id;
        DeletedCustomerId = deletedCustomer.Id;
        DisabledCustomerId = disabledCustomer.Id;

        var salesman = new BaseEmployee
        {
            EmployeeCode = $"E-472-{Guid.NewGuid():N}", EmployeeName = "ERP472 在职业务员",
            IsSalesman = true, Status = 1
        };
        var deletedEmployee = new BaseEmployee
        {
            EmployeeCode = $"E-472D-{Guid.NewGuid():N}", EmployeeName = "ERP472 已删除业务员",
            IsSalesman = true, Status = 1, IsDeleted = true
        };
        var disabledEmployee = new BaseEmployee
        {
            EmployeeCode = $"E-472S-{Guid.NewGuid():N}", EmployeeName = "ERP472 离职业务员",
            IsSalesman = true, Status = 0
        };
        var notSalesman = new BaseEmployee
        {
            EmployeeCode = $"E-472N-{Guid.NewGuid():N}", EmployeeName = "ERP472 非业务员员工",
            IsSalesman = false, Status = 1
        };
        db.BaseEmployees.AddRange(salesman, deletedEmployee, disabledEmployee, notSalesman);
        await db.SaveChangesAsync();
        EmployeeId = salesman.Id;
        DeletedEmployeeId = deletedEmployee.Id;
        DisabledEmployeeId = disabledEmployee.Id;
        NotSalesmanEmployeeId = notSalesman.Id;

        var product = new BaseProduct
        {
            ProductCode = $"P-472-{Guid.NewGuid():N}", ProductName = "ERP472 商品", Unit = "PCS", Status = 1
        };
        var deletedProduct = new BaseProduct
        {
            ProductCode = $"P-472D-{Guid.NewGuid():N}", ProductName = "ERP472 已删除商品", Unit = "PCS",
            Status = 1, IsDeleted = true
        };
        var disabledProduct = new BaseProduct
        {
            ProductCode = $"P-472S-{Guid.NewGuid():N}", ProductName = "ERP472 已停用商品", Unit = "PCS", Status = 0
        };
        db.BaseProducts.AddRange(product, deletedProduct, disabledProduct);
        await db.SaveChangesAsync();
        ProductId = product.Id;
        DeletedProductId = deletedProduct.Id;
        DisabledProductId = disabledProduct.Id;

        Console.WriteLine("[ERP-472] 既有授权 + 主数据夹具就绪（受控只读，不新增权限模型）。");
    }

    /// <summary>播种报价单单号规则（专用库内唯一；用于断言「被拒绝的请求绝不消耗单号」）。</summary>
    private async Task SeedQuotationNumberRuleAsync(ErpDbContext db)
    {
        var rule = new SysDocumentNumberRule
        {
            DocumentType = DocumentType.Quotation,
            RuleCode = $"ERP472-QT-{Guid.NewGuid():N}",
            RuleName = "报价单号",
            Prefix = "QT",
            DateFormat = "yyyyMMdd",
            SerialLength = 4,
            Separator = string.Empty,
            CurrentSequence = 0,
            IsDeleted = false
        };
        db.SysDocumentNumberRules.Add(rule);
        await db.SaveChangesAsync();
        _quotationNumberRuleId = rule.Id;
    }

    /// <summary>读取报价单单号流水的当前序号（被拒绝的请求必须保持不变）。</summary>
    public async Task<long> QuotationSequenceAsync()
    {
        await using var db = CreateDbContext();
        return await db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.Id == _quotationNumberRuleId).Select(r => r.CurrentSequence).SingleAsync();
    }

    /// <summary>
    /// 播种**专用**的既有合法客户 / 商品（避免「提交前被删除 / 停用」模拟污染共享夹具的其他用例）。
    /// </summary>
    public async Task<(long CustomerId, long ProductId)> SeedTransientMastersAsync()
    {
        await using var db = CreateDbContext();
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-T-{Guid.NewGuid():N}", CustomerName = "ERP472 临时客户",
            Status = 1, CreditStatus = "正常", DepositRatio = 30m
        };
        var product = new BaseProduct
        {
            ProductCode = $"P-T-{Guid.NewGuid():N}", ProductName = "ERP472 临时商品", Unit = "PCS", Status = 1
        };
        db.BaseCustomers.Add(customer);
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return (customer.Id, product.Id);
    }

    /// <summary>播种一张已审核询价单（引用既有实时客户 / 商品），供询价转报价场景使用。</summary>
    public async Task<long> SeedInquiryAsync(long customerId, long productId)
    {
        await using var db = CreateDbContext();
        var inquiry = new Inquiry
        {
            InquiryNo = $"INQ-472-{Guid.NewGuid():N}"[..18],
            InquiryDate = DateTime.Today,
            CustomerId = customerId,
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            ValidDays = 30,
            Status = DocumentStatus.Approved,
            Details = new List<InquiryDetail>
            {
                new()
                {
                    ProductId = productId, ProductName = "ERP472 询价商品", Spec = "标准", Unit = "PCS",
                    Quantity = 2m, UnitPrice = 5m, Amount = 10m
                }
            }
        };
        db.Inquiries.Add(inquiry);
        await db.SaveChangesAsync();
        return inquiry.Id;
    }

    private static SysUser NewUser(UserStatus status) => new()
    {
        UserName = $"erp472-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
        DisplayName = "ERP472 隔离账号", Status = status
    };
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class QuotationMasterReferenceTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => QuotationMasterReferenceSqlServerFixture.AssertDedicatedTarget(connection));
}
