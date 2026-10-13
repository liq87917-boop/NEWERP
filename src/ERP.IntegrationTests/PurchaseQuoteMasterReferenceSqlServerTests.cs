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
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-476 供应商比价「实时主数据引用」护栏的**真实 SQL Server** 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实控制器 + 真实既有授权</b>：以既有系统内置角色（特权）与既有「供应商比价」/「采购订单」功能菜单
/// 驱动真实 <see cref="PurchaseQuoteController"/>；不新增 / 不修改任何菜单 / 角色 / 用户授权，无匿名 / 管理员降级。</item>
/// <item><b>受控错误与零写入</b>：悬空 / 已删除 / 已停用归属客户，零 / 负供应商或商品引用，已删除 / 已停用供应商或商品，
/// 均在单号预约与任何赋值之前被受控业务错误拒绝；比价行 / 采购订单 / 采购明细 / 审批决定与单号流水全部不变。</item>
/// <item><b>采购承诺</b>：带入预填 / 单行转采购订单 / 批次转采购订单在既有来源行锁内复核来源实时引用；
/// 供应商或商品引用缺失（<c>ProductId = 0</c> 绝不静默占位）/ 档案失效时零发号、零写入、来源状态与留痕不变；
/// 批次内一行失效即整批拒绝，绝不生成部分采购订单 / 部分批次组。</item>
/// <item><b>正向落库</b>：合法实时主数据（供应商 / 商品 / 客户）照常生成采购订单并回写来源留痕。</item>
/// <item><b>历史读取只读</b>：引用失效主数据的历史比价行仍可读、不被回填。</item>
/// <item><b>有界批量查询</b>：承诺复核按去重集合一次取回供应商 / 商品档案（命令拦截计数）。</item>
/// <item><b>专用目标护栏</b>：任何数据库访问之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> +
/// <c>NEWERP_AUTOTEST</c> 前缀 + <c>Integrated Security</c>；每次运行只创建一个全新 GUID 库，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何库，也绝不读取 appsettings / .env / 生产凭据。</item>
/// </list>
/// <para>保留既有比价 / 审批 / 采购审计与原始失败日志：本测试只读取计数与只读快照，不删除 / 不清理任何既有行。
/// 构建完成不等于阶段验收：只有本文件在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class PurchaseQuoteMasterReferenceSqlServerTests
    : IClassFixture<PurchaseQuoteMasterReferenceSqlServerFixture>
{
    private const string QuotePath = "/api/purchase/quotes";

    private readonly PurchaseQuoteMasterReferenceSqlServerFixture _fixture;

    public PurchaseQuoteMasterReferenceSqlServerTests(PurchaseQuoteMasterReferenceSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>专用目标护栏（任何数据库访问之前）。</summary>
    private void Guard() => PurchaseQuoteMasterReferenceSqlServerFixture
        .AssertDedicatedTarget(_fixture.ConnectionString);

    private static PurchaseQuoteController NewController(ErpDbContext db, long userId)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = QuotePath;
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"));

        return new PurchaseQuoteController(new GenericService<PurchaseQuote>(db), db,
            new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static readonly string[] SnapshotTables =
    {
        "PurchaseQuotes", "PurchaseOrders", "PurchaseOrderDetails", "PurchaseQuoteDecisions",
    };

    private async Task<Dictionary<string, int>> SnapshotAsync()
    {
        await using var db = _fixture.CreateDbContext();
        return new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["PurchaseQuotes"] = await db.PurchaseQuotes.CountAsync(),
            ["PurchaseOrders"] = await db.PurchaseOrders.CountAsync(),
            ["PurchaseOrderDetails"] = await db.PurchaseOrderDetails.CountAsync(),
            ["PurchaseQuoteDecisions"] = await db.PurchaseQuoteDecisions.CountAsync(),
        };
    }

    private static void AssertSnapshotUnchanged(
        IReadOnlyDictionary<string, int> before, IReadOnlyDictionary<string, int> after)
    {
        foreach (var table in SnapshotTables)
            Assert.Equal(before[table], after[table]);
    }

    /// <summary>比价行请求体（默认不含任何显式主数据引用）。</summary>
    private static PurchaseQuote NewQuoteBody(string quoteNo, long? customerId = null, long? supplierId = null,
        long? productId = null) => new()
    {
        QuoteNo = quoteNo,
        QuoteDate = DateTime.Today,
        ProductId = productId,
        ProductName = "ERP476 集成商品",
        Spec = "大号",
        Unit = "PCS",
        Quantity = 100m,
        SupplierId = supplierId,
        SupplierName = supplierId is > 0 ? $"ERP476 集成供应商 {supplierId}" : string.Empty,
        SupplierType = "档口",
        QuotePrice = 2m,
        TotalAmount = 200m,
        Currency = "USD",
        TaxIncluded = false,
        DeliveryDays = 10,
        MinOrderQty = 1,
        PaymentTerms = "现结",
        IsSelected = false,
        Status = PurchaseQuoteMutationRules.PendingStatus,
        CustomerId = customerId,
        CustomerName = "ERP476 客户名快照",
        Remark = "ERP476_SQL_TEST"
    };

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    // ==================== 2. 选择与修改：承诺级复核在锁内、写字段之前 ====================

    [Fact]
    public async Task 新增_选中承诺_商品引用缺失_真实SQL拒绝且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.PrivilegedUserId);

        var selected = NewQuoteBody($"PQ-476-SQL-{Tag()}", customerId: _fixture.CustomerId,
            supplierId: _fixture.SupplierId);
        selected.IsSelected = true;
        selected.Status = PurchaseQuoteMutationRules.SelectedStatus;

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(selected));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains(PurchaseQuoteMasterReferenceRules.CommitmentProductText, ex.Message);
        AssertSnapshotUnchanged(before, await SnapshotAsync());
    }

    [Fact]
    public async Task 修改_草稿后商品档案被停用_真实SQL锁内原子拒绝且原行不变()
    {
        Guard();
        var quote = await _fixture.SeedDraftQuoteAsync($"PQ-476-SQL-{Tag()}", _fixture.SupplierId,
            _fixture.ProductId, _fixture.CustomerId);
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.PrivilegedUserId);

        await _fixture.DisableProductAsync(_fixture.ProductId);
        try
        {
            var change = NewQuoteBody(quote.QuoteNo, _fixture.CustomerId, _fixture.SupplierId,
                _fixture.ProductId);
            change.Quantity = 999m;

            var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(quote.Id, change));

            Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
            var stored = await ReadQuoteAsync(quote.Id);
            Assert.Equal(100m, stored.Quantity);                          // 锁内权威行一行不改
            Assert.Equal(PurchaseQuoteMutationRules.PendingStatus, stored.Status);
        }
        finally
        {
            await _fixture.RestoreProductAsync(_fixture.ProductId);
        }
    }

    // ==================== 3. 采购承诺：带入预填 / 单行 / 批次（真实行锁内、发号之前） ====================

    [Fact]
    public async Task 带入预填_来源商品引用缺失_真实SQL拒绝且不落库不改来源()
    {
        Guard();
        var quote = await _fixture.SeedSelectedApprovedQuoteAsync($"PQ-476-SQL-{Tag()}",
            _fixture.SupplierId, productId: null, customerId: _fixture.CustomerId);
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.PrivilegedUserId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.OrderPrefill(quote.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains(PurchaseQuoteMasterReferenceRules.CommitmentProductText, ex.Message);
        var source = await ReadQuoteAsync(quote.Id);
        Assert.Equal(PurchaseQuoteConversion.SelectedStatus, source.Status);
        Assert.Equal(string.Empty, source.RefOrderNo ?? string.Empty);
        AssertSnapshotUnchanged(before, await SnapshotAsync());
    }

    [Fact]
    public async Task 单行转采购订单_来源商品引用缺失_真实SQL在发号与写入之前拒绝()
    {
        Guard();
        var quote = await _fixture.SeedSelectedApprovedQuoteAsync($"PQ-476-SQL-{Tag()}",
            _fixture.SupplierId, productId: null, customerId: _fixture.CustomerId);
        var before = await SnapshotAsync();
        var sequenceBefore = await _fixture.PurchaseOrderSequenceAsync();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.PrivilegedUserId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToPurchaseOrder(quote.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains(PurchaseQuoteMasterReferenceRules.CommitmentProductText, ex.Message);
        AssertSnapshotUnchanged(before, await SnapshotAsync());
        Assert.Equal(sequenceBefore, await _fixture.PurchaseOrderSequenceAsync());   // 零发号
        var source = await ReadQuoteAsync(quote.Id);
        Assert.Equal(PurchaseQuoteConversion.SelectedStatus, source.Status);
        Assert.Equal(string.Empty, source.RefOrderNo ?? string.Empty);
    }

    [Fact]
    public async Task 单行转采购订单_审批后档案失效_真实SQL受控拒绝且零写入零发号()
    {
        Guard();
        var supplierId = await _fixture.SeedTransientSupplierAsync();
        var quote = await _fixture.SeedSelectedApprovedQuoteAsync($"PQ-476-SQL-{Tag()}",
            supplierId, _fixture.ProductId, _fixture.CustomerId);
        var before = await SnapshotAsync();
        var sequenceBefore = await _fixture.PurchaseOrderSequenceAsync();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.PrivilegedUserId);

        await _fixture.DisableSupplierAsync(supplierId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToPurchaseOrder(quote.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains(PurchaseQuoteConversionMutationRules.SupplierMasterText, ex.Message);
        AssertSnapshotUnchanged(before, await SnapshotAsync());
        Assert.Equal(sequenceBefore, await _fixture.PurchaseOrderSequenceAsync());
        var decision = await ReadDecisionAsync(quote.Id);
        Assert.Equal(PurchaseQuoteApproval.Approved, decision.Decision);     // 审批决定历史一字不改
        Assert.Equal(supplierId, decision.SelectedSupplierId);
    }

    private async Task<PurchaseQuoteDecision> ReadDecisionAsync(long quoteId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.PurchaseQuoteDecisions.AsNoTracking().SingleAsync(d => d.QuoteId == quoteId);
    }

    [Fact]
    public async Task 单行转采购订单_合法实时主数据_真实SQL正向落库并留痕()
    {
        Guard();
        var sequenceBefore = await _fixture.PurchaseOrderSequenceAsync();
        var quote = await _fixture.SeedSelectedApprovedQuoteAsync($"PQ-476-SQL-{Tag()}",
            _fixture.SupplierId, _fixture.ProductId, _fixture.CustomerId);
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.PrivilegedUserId);

        var ok = Assert.IsType<OkObjectResult>(await ctl.ToPurchaseOrder(quote.Id));

        var data = Assert.IsType<ApiResponse<PurchaseOrderConversionResult>>(ok.Value).Data!;
        Assert.True(data.Id > 0);
        await using var check = _fixture.CreateDbContext();
        var order = await check.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == data.Id);
        Assert.Equal(_fixture.SupplierId, order.SupplierId);
        Assert.Equal(_fixture.CustomerId, order.OwningCustomerId);
        var detail = await check.PurchaseOrderDetails.AsNoTracking()
            .SingleAsync(d => d.PurchaseOrderId == order.Id);
        Assert.Equal(_fixture.ProductId, detail.ProductId);
        Assert.Equal(sequenceBefore + 1, await _fixture.PurchaseOrderSequenceAsync());

        var source = await ReadQuoteAsync(quote.Id);
        Assert.Equal(PurchaseQuoteConversion.ConvertedStatus, source.Status);
        Assert.Equal(order.OrderNo, source.RefOrderNo);
    }

    [Fact]
    public async Task 批次转采购订单_一行商品引用缺失_真实SQL整批拒绝零发号零写入()
    {
        Guard();
        var batchNo = $"PQ-476-SQL-BATCH-{Tag()}";
        var good = await _fixture.SeedSelectedApprovedQuoteAsync(batchNo, _fixture.SupplierId,
            _fixture.ProductId, _fixture.CustomerId);
        var bad = await _fixture.SeedSelectedApprovedQuoteAsync(batchNo, _fixture.SupplierId,
            productId: null, customerId: _fixture.CustomerId);
        var before = await SnapshotAsync();
        var sequenceBefore = await _fixture.PurchaseOrderSequenceAsync();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.PrivilegedUserId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.BatchToOrder(
            new PurchaseQuoteBatchConversionRequest { QuoteNo = batchNo }));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains(PurchaseQuoteMasterReferenceRules.CommitmentProductText, ex.Message);
        AssertSnapshotUnchanged(before, await SnapshotAsync());                // 绝不部分写入
        Assert.Equal(sequenceBefore, await _fixture.PurchaseOrderSequenceAsync());
        foreach (var quote in new[] { good, bad })
        {
            var source = await ReadQuoteAsync(quote.Id);
            Assert.Equal(PurchaseQuoteConversion.SelectedStatus, source.Status);
            Assert.Equal(string.Empty, source.RefOrderNo ?? string.Empty);
        }
    }

    // ==================== 4. 授权前置 / 历史读取 / 有界批量查询 ====================

    [Fact]
    public async Task 授权先于主数据_无菜单账号_真实SQL非披露拒绝且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.NoMenuUserId).Create(
                NewQuoteBody($"PQ-476-SQL-{Tag()}", customerId: _fixture.MissingCustomerId)));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Equal(PurchaseQuoteAuthorizationRules.MenuDeniedText, ex.Message);   // 非披露
        AssertSnapshotUnchanged(before, await SnapshotAsync());
    }

    [Fact]
    public async Task 历史读取只读_档案失效的来源仍可读且不被回填()
    {
        Guard();
        var supplierId = await _fixture.SeedTransientSupplierAsync();
        var productId = await _fixture.SeedTransientProductAsync();
        var customerId = await _fixture.SeedTransientCustomerAsync();
        var quote = await _fixture.SeedDraftQuoteAsync($"PQ-476-SQL-{Tag()}", supplierId, productId,
            customerId);

        await _fixture.DisableSupplierAsync(supplierId);
        await _fixture.SoftDeleteProductAsync(productId);
        await _fixture.SoftDeleteCustomerAsync(customerId);

        await using var db = _fixture.CreateDbContext();
        var read = Assert.IsType<OkObjectResult>(
            await NewController(db, _fixture.PrivilegedUserId).GetById(quote.Id));

        var data = Assert.IsType<ApiResponse<PurchaseQuote>>(read.Value).Data!;
        Assert.Equal(supplierId, data.SupplierId);              // 原样返回持久化值
        Assert.Equal(productId, data.ProductId);
        Assert.Equal(customerId, data.CustomerId);
        Assert.Equal("ERP476 集成商品", data.ProductName);       // 文本不回填 / 不改派
        Assert.Equal(PurchaseQuoteMutationRules.PendingStatus, data.Status);
    }

    [Fact]
    public async Task 承诺复核_真实SQL去重集合各一次批量查询()
    {
        Guard();
        var counter = new MasterQueryCounter();
        await using var db = _fixture.CreateDbContext(counter);
        var ctl = NewController(db, _fixture.PrivilegedUserId);

        var batchNo = $"PQ-476-SQL-COUNT-{Tag()}";
        for (var i = 0; i < 4; i++)
            await _fixture.SeedSelectedApprovedQuoteAsync(batchNo, _fixture.SupplierId, _fixture.ProductId,
                _fixture.CustomerId);

        var ok = Assert.IsType<OkObjectResult>(await ctl.BatchToOrder(
            new PurchaseQuoteBatchConversionRequest { QuoteNo = batchNo }));

        Assert.IsType<ApiResponse<PurchaseQuoteBatchConversionResult>>(ok.Value);
        // 两道有界护栏各一次（ERP-418 转换资格 / 主数据复核 + ERP-476 采购承诺复核）：次数与行数无关，
        // 绝不逐行查库；客户查询只在显式归属客户时发生（同样一次）。
        Assert.Equal(2, counter.SupplierQueries);
        Assert.Equal(2, counter.ProductQueries);
        Assert.True(counter.CustomerQueries <= 1);
    }

    /// <summary>统计主数据查询次数的命令拦截器（证明承诺复核按去重集合一次批量查询）。</summary>
    private sealed class MasterQueryCounter : DbCommandInterceptor
    {
        public int SupplierQueries { get; private set; }
        public int ProductQueries { get; private set; }
        public int CustomerQueries { get; private set; }

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
            if (command.CommandText.Contains("BaseSuppliers", StringComparison.OrdinalIgnoreCase))
                SupplierQueries++;
            if (command.CommandText.Contains("BaseProducts", StringComparison.OrdinalIgnoreCase))
                ProductQueries++;
            if (command.CommandText.Contains("BaseCustomers", StringComparison.OrdinalIgnoreCase))
                CustomerQueries++;
        }
    }

    private async Task<PurchaseQuote> ReadQuoteAsync(long id)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.PurchaseQuotes.AsNoTracking().SingleAsync(q => q.Id == id);
    }
}

/// <summary>
/// ERP-476 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class PurchaseQuoteMasterReferenceSqlServerFixture : IAsyncLifetime
{
    /// <summary>专用实例（精确匹配）。</summary>
    public const string InstanceTarget = @"(localdb)\NEWERP_AutoAcceptance";

    /// <summary>库名前缀（必须为 NEWERP_AUTOTEST）。</summary>
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    private const string DefaultDatabaseName = DatabasePrefix + "_PQMASTERREF";

    public string ConnectionString { get; private set; } = string.Empty;

    public long PrivilegedUserId { get; private set; }
    public long NoMenuUserId { get; private set; }
    public long CustomerId { get; private set; }
    public long DeletedCustomerId { get; private set; }
    public long DisabledCustomerId { get; private set; }
    public long SupplierId { get; private set; }
    public long DeletedSupplierId { get; private set; }
    public long DisabledSupplierId { get; private set; }
    public long ProductId { get; private set; }
    public long DeletedProductId { get; private set; }
    public long DisabledProductId { get; private set; }

    /// <summary>故意不存在的客户 Id（缺失引用场景）。</summary>
    public long MissingCustomerId => 9_476_001L;

    /// <summary>故意不存在的供应商 Id（既有「未引用档案」弱引用场景）。</summary>
    public long MissingSupplierId => 9_476_002L;

    /// <summary>故意不存在的商品 Id（既有「未引用档案」弱引用场景）。</summary>
    public long MissingProductId => 9_476_003L;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-476] 目标库护栏放行（实例 {InstanceTarget}，库名前缀 {DatabasePrefix}）。");
        await CreateFreshDatabaseAsync();
        await SeedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    /// <summary>带命令拦截器的上下文（用于统计主数据查询次数）。</summary>
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

        Console.WriteLine("[ERP-476] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }

    // ==================== 既有授权（不新增权限模型）+ 主数据夹具 ====================

    private async Task SeedAsync()
    {
        await using var db = CreateDbContext();

        // 1) 特权账号（系统内置角色 + 既有「供应商比价」/「采购订单」菜单）。
        var privilegedRole = new SysRole
        {
            RoleName = "ERP476 特权角色", RoleCode = $"ERP476-P-{Guid.NewGuid():N}", IsSystem = true
        };
        db.SysRoles.Add(privilegedRole);
        await db.SaveChangesAsync();
        var privileged = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(privileged);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = privileged.Id, RoleId = privilegedRole.Id });
        var quoteMenu = await EnsureMenuAsync(db, PurchaseQuoteAuthorizationRules.RequiredMenuCode, "供应商比价");
        var orderMenu = await EnsureMenuAsync(db, PurchaseQuoteAuthorizationRules.DestinationMenuCode, "采购订单");
        db.SysRoleMenus.AddRange(
            new SysRoleMenu { RoleId = privilegedRole.Id, MenuId = quoteMenu.Id },
            new SysRoleMenu { RoleId = privilegedRole.Id, MenuId = orderMenu.Id });
        await db.SaveChangesAsync();
        PrivilegedUserId = privileged.Id;

        // 2) 受限账号：非系统内置角色且未授予任何菜单 → 既有菜单授权 fail closed。
        NoMenuUserId = await SeedMenuLessUserAsync(db);

        // 3) 客户：启用 / 已删除 / 已停用。
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-476-{Guid.NewGuid():N}", CustomerName = "ERP476 可见客户",
            Status = 1, CreditStatus = "正常", DepositRatio = 30m
        };
        var deletedCustomer = new BaseCustomer
        {
            CustomerCode = $"C-476D-{Guid.NewGuid():N}", CustomerName = "ERP476 已删除客户",
            Status = 1, CreditStatus = "正常", DepositRatio = 30m, IsDeleted = true
        };
        var disabledCustomer = new BaseCustomer
        {
            CustomerCode = $"C-476S-{Guid.NewGuid():N}", CustomerName = "ERP476 已停用客户",
            Status = 0, CreditStatus = "正常", DepositRatio = 30m
        };
        db.BaseCustomers.AddRange(customer, deletedCustomer, disabledCustomer);
        await db.SaveChangesAsync();
        CustomerId = customer.Id;
        DeletedCustomerId = deletedCustomer.Id;
        DisabledCustomerId = disabledCustomer.Id;

        // 4) 供应商：启用 / 已删除 / 已停用。
        var supplier = new BaseSupplier
        {
            SupplierCode = $"S-476-{Guid.NewGuid():N}", SupplierName = "ERP476 供应商", Status = 1
        };
        var deletedSupplier = new BaseSupplier
        {
            SupplierCode = $"S-476D-{Guid.NewGuid():N}", SupplierName = "ERP476 已删除供应商",
            Status = 1, IsDeleted = true
        };
        var disabledSupplier = new BaseSupplier
        {
            SupplierCode = $"S-476S-{Guid.NewGuid():N}", SupplierName = "ERP476 已停用供应商", Status = 0
        };
        db.BaseSuppliers.AddRange(supplier, deletedSupplier, disabledSupplier);
        await db.SaveChangesAsync();
        SupplierId = supplier.Id;
        DeletedSupplierId = deletedSupplier.Id;
        DisabledSupplierId = disabledSupplier.Id;

        // 5) 商品：启用 / 已删除 / 已停用。
        var product = new BaseProduct
        {
            ProductCode = $"P-476-{Guid.NewGuid():N}", ProductName = "ERP476 商品", Unit = "PCS", Status = 1
        };
        var deletedProduct = new BaseProduct
        {
            ProductCode = $"P-476D-{Guid.NewGuid():N}", ProductName = "ERP476 已删除商品", Unit = "PCS",
            Status = 1, IsDeleted = true
        };
        var disabledProduct = new BaseProduct
        {
            ProductCode = $"P-476S-{Guid.NewGuid():N}", ProductName = "ERP476 已停用商品", Unit = "PCS", Status = 0
        };
        db.BaseProducts.AddRange(product, deletedProduct, disabledProduct);
        await db.SaveChangesAsync();
        ProductId = product.Id;
        DeletedProductId = deletedProduct.Id;
        DisabledProductId = disabledProduct.Id;

        // 6) 采购订单单号流水归零（全新库内统一从 0 起算，便于断言「零发号 / 恰好 +1」）。
        await ResetPurchaseOrderSequenceAsync(db);

        Console.WriteLine("[ERP-476] 既有授权 + 主数据夹具就绪（受控只读，不新增权限模型）。");
    }

    // ==================== 便捷夹具：比价行 / 临时主数据 / 失效模拟 / 单号流水 ====================

    /// <summary>播种一条「已选中 + 已批准」的比价行（可承诺转采购订单的来源；全新行，不清理既有数据）。</summary>
    public async Task<PurchaseQuote> SeedSelectedApprovedQuoteAsync(string quoteNo, long? supplierId,
        long? productId, long? customerId)
    {
        await using var db = CreateDbContext();
        var quote = NewQuote(quoteNo, supplierId, productId, customerId,
            PurchaseQuoteConversion.SelectedStatus, selected: true);
        db.PurchaseQuotes.Add(quote);
        await db.SaveChangesAsync();

        db.PurchaseQuoteDecisions.Add(new PurchaseQuoteDecision
        {
            QuoteId = quote.Id,
            QuoteNo = quote.QuoteNo,
            Decision = PurchaseQuoteApproval.Approved,
            SelectedSupplierId = quote.SupplierId,
            SelectedSupplierName = quote.SupplierName,
            DecisionBasis = "单价最优",
            DecidedBy = PrivilegedUserId,
            DecidedByName = "ERP476 隔离账号",
            DecidedAt = DateTime.Now,
            DecisionRef = PurchaseQuoteApproval.DecisionRef(quote),
            CreatedAt = DateTime.Now,
            CreatedBy = PrivilegedUserId
        });
        await db.SaveChangesAsync();
        return quote;
    }

    /// <summary>播种一条「待比较」草稿（未选中 / 未批准）。</summary>
    public async Task<PurchaseQuote> SeedDraftQuoteAsync(string quoteNo, long? supplierId, long? productId,
        long? customerId)
    {
        await using var db = CreateDbContext();
        var quote = NewQuote(quoteNo, supplierId, productId, customerId,
            PurchaseQuoteMutationRules.PendingStatus, selected: false);
        db.PurchaseQuotes.Add(quote);
        await db.SaveChangesAsync();
        return quote;
    }

    private static PurchaseQuote NewQuote(string quoteNo, long? supplierId, long? productId, long? customerId,
        string status, bool selected) => new()
    {
        QuoteNo = quoteNo,
        QuoteDate = DateTime.Today,
        ProductId = productId,
        ProductName = "ERP476 集成商品",
        Spec = "大号",
        Unit = "PCS",
        Quantity = 100m,
        SupplierId = supplierId,
        SupplierName = supplierId is > 0 ? $"ERP476 集成供应商 {supplierId}" : string.Empty,
        SupplierType = "档口",
        QuotePrice = 2m,
        TotalAmount = 200m,
        Currency = "USD",
        TaxIncluded = false,
        DeliveryDays = 10,
        MinOrderQty = 1,
        PaymentTerms = "现结",
        IsSelected = selected,
        Status = status,
        CustomerId = customerId,
        CustomerName = "ERP476 客户名快照",
        Remark = "ERP476_SQL_TEST"
    };

    /// <summary>播种**专用**的既有合法供应商（避免失效模拟污染共享夹具的其他用例）。</summary>
    public async Task<long> SeedTransientSupplierAsync()
    {
        await using var db = CreateDbContext();
        var supplier = new BaseSupplier
        {
            SupplierCode = $"S-T-{Guid.NewGuid():N}", SupplierName = "ERP476 临时供应商", Status = 1
        };
        db.BaseSuppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier.Id;
    }

    /// <summary>播种**专用**的既有合法商品（避免失效模拟污染共享夹具的其他用例）。</summary>
    public async Task<long> SeedTransientProductAsync()
    {
        await using var db = CreateDbContext();
        var product = new BaseProduct
        {
            ProductCode = $"P-T-{Guid.NewGuid():N}", ProductName = "ERP476 临时商品", Unit = "PCS", Status = 1
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    /// <summary>播种**专用**的既有合法客户（避免失效模拟污染共享夹具的其他用例）。</summary>
    public async Task<long> SeedTransientCustomerAsync()
    {
        await using var db = CreateDbContext();
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-T-{Guid.NewGuid():N}", CustomerName = "ERP476 临时客户",
            Status = 1, CreditStatus = "正常", DepositRatio = 30m
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    /// <summary>停用既有供应商（模拟草案 / 审批后主数据失效；只改既有行状态，绝不删除数据）。</summary>
    public async Task DisableSupplierAsync(long supplierId)
    {
        await using var db = CreateDbContext();
        var supplier = await db.BaseSuppliers.SingleAsync(s => s.Id == supplierId);
        supplier.Status = 0;
        await db.SaveChangesAsync();
    }

    /// <summary>停用既有商品（模拟草案后主数据失效；只改既有行状态，绝不删除数据）。</summary>
    public async Task DisableProductAsync(long productId)
    {
        await using var db = CreateDbContext();
        var product = await db.BaseProducts.SingleAsync(p => p.Id == productId);
        product.Status = 0;
        await db.SaveChangesAsync();
    }

    /// <summary>恢复既有商品为启用（测试结束后复位，绝不删除数据）。</summary>
    public async Task RestoreProductAsync(long productId)
    {
        await using var db = CreateDbContext();
        var product = await db.BaseProducts.SingleAsync(p => p.Id == productId);
        product.Status = 1;
        await db.SaveChangesAsync();
    }

    /// <summary>软删除既有商品（模拟历史读取的失效引用；只改既有行标记，绝不删除数据）。</summary>
    public async Task SoftDeleteProductAsync(long productId)
    {
        await using var db = CreateDbContext();
        var product = await db.BaseProducts.SingleAsync(p => p.Id == productId);
        product.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    /// <summary>软删除既有客户（模拟历史读取的失效引用；只改既有行标记，绝不删除数据）。</summary>
    public async Task SoftDeleteCustomerAsync(long customerId)
    {
        await using var db = CreateDbContext();
        var customer = await db.BaseCustomers.SingleAsync(c => c.Id == customerId);
        customer.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    /// <summary>读取采购订单单号流水的当前序号（被拒绝的请求必须保持不变）。</summary>
    public async Task<long> PurchaseOrderSequenceAsync()
    {
        await using var db = CreateDbContext();
        return await db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == DocumentType.PurchaseOrder && !r.IsDeleted)
            .MaxAsync(r => r.CurrentSequence);
    }

    /// <summary>复用既有 <c>SeedData</c> 单据号规则（专用全新库内统一从 0 起算流水）。</summary>
    private static async Task ResetPurchaseOrderSequenceAsync(ErpDbContext db)
    {
        var rules = await db.SysDocumentNumberRules
            .Where(r => r.DocumentType == DocumentType.PurchaseOrder && !r.IsDeleted).ToListAsync();
        if (rules.Count == 0)
        {
            var created = new SysDocumentNumberRule
            {
                DocumentType = DocumentType.PurchaseOrder,
                RuleCode = $"PO-{Guid.NewGuid():N}",
                RuleName = "采购订单单号",
                Prefix = "PO",
                DateFormat = "yyyyMMdd",
                SerialLength = 4,
                Separator = string.Empty,
                CurrentSequence = 0,
                IsDeleted = false
            };
            db.SysDocumentNumberRules.Add(created);
            rules.Add(created);
        }
        foreach (var rule in rules)
        {
            rule.CurrentSequence = 0;
            rule.UpdatedAt = DateTime.Now;
        }
        await db.SaveChangesAsync();
    }

    private static async Task<SysMenu> EnsureMenuAsync(ErpDbContext db, string menuCode, string menuName)
    {
        var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == menuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu { MenuName = menuName, MenuCode = menuCode, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            await db.SaveChangesAsync();
        }
        return menu;
    }

    private static async Task<long> SeedMenuLessUserAsync(ErpDbContext db)
    {
        var code = $"erp476-nomenu-{Guid.NewGuid():N}";
        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();
        var role = new SysRole { RoleName = code, RoleCode = code, IsSystem = false };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static SysUser NewUser(UserStatus status) => new()
    {
        UserName = $"erp476-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
        DisplayName = "ERP476 隔离账号", Status = status
    };
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class PurchaseQuoteMasterReferenceTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => PurchaseQuoteMasterReferenceSqlServerFixture.AssertDedicatedTarget(connection));
}
