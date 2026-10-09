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
/// ERP-420 规范销售订单普通写入（新增 / 修改 / 删除 / 提交 / 审核 / 取消）实时授权与权威客户范围护栏的
/// 真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实规则 + 真实控制器 + 真实既有授权</b>：以新播种的既有「销售订单」<c>sales-order</c> 功能菜单授权与
/// 业务员客户数据范围驱动真实 <see cref="SalesOrderController"/>；不新增 / 不修改任何既有菜单 / 角色 / 用户授权，
/// 无匿名 / 管理员降级，也不把导出菜单 <c>sales-order-export</c> 当作模块权限。</item>
/// <item><b>覆盖全部写入路由</b>：手工无来源 / 已解析来源 / 无法解析历史来源 × 本人 / 他人 / null 客户，
/// 缺失 / 非法 / 已禁用 / 已删除 / 已撤销菜单身份，客户改派，以及新增 / 修改 / 删除 / 提交 / 审核 / 取消。</item>
/// <item><b>允许的写入仍可用</b>：本人范围内订单可新增 / 修改 / 提交 / 审核 / 删除 / 取消，且保留既有来源血缘与下游护栏。</item>
/// <item><b>零写入证据</b>：每次拒绝前后对 <c>SalesOrders</c> / <c>SalesOrderDetails</c> / <c>StockOuts</c> /
/// <c>StockMovements</c>（保留库存来源单据审计） / <c>ProformaInvoices</c> / <c>Quotations</c> / <c>SysOperationLogs</c>
/// 做只读快照，全部不变。</item>
/// <item><b>专用目标护栏</b>：必须在访问数据库之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> + <c>NEWERP_AUTOTEST</c>
/// 前缀 + <c>Integrated Security</c>；每次运行只创建一个全新 GUID 库，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何库，也绝不读取 appsettings / .env / 生产凭据。</item>
/// </list>
/// <para>保留既有库存来源单据审计与原始失败日志：本测试只读取计数与只读快照，不删除 / 不清理任何既有行。
/// 构建完成不等于阶段验收：只有本文件在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class SalesOrderMutationAuthorizationSqlServerTests
    : IClassFixture<SalesOrderMutationAuthorizationSqlServerFixture>
{
    private readonly SalesOrderMutationAuthorizationSqlServerFixture _fixture;

    public SalesOrderMutationAuthorizationSqlServerTests(SalesOrderMutationAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    private const long MissingOrderId = 9_420_999L;

    private static readonly string[] SnapshotTables =
    {
        "SalesOrders", "SalesOrderDetails", "StockOuts", "StockMovements",
        "ProformaInvoices", "Quotations", "SysOperationLogs",
    };

    /// <summary>专用目标护栏（任何数据库访问之前）。</summary>
    private void Guard() => SalesOrderMutationAuthorizationSqlServerFixture
        .AssertDedicatedTarget(_fixture.ConnectionString);

    private static SalesOrderController NewController(ErpDbContext db, long? userId)
        => NewController(db, userId?.ToString() ?? string.Empty, hasIdentity: userId.HasValue);

    private static SalesOrderController NewController(ErpDbContext db, string rawIdentity, bool hasIdentity)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/sales-orders";
        http.User = hasIdentity
            ? new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, rawIdentity) }, "Test"))
            : new ClaimsPrincipal(new ClaimsIdentity());

        return new SalesOrderController(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    /// <summary>必然不存在的来源 Id（无法解析的显式历史值）。</summary>
    private const long MissingPiId = 9_420_777L;

    private SalesOrder NewOrderBody(long customerId, long? piId = null, string piNo = "",
        string remark = "")
        => new()
        {
            OrderDate = DateTime.Today,
            CustomerId = customerId,
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            DepositRatio = 30m,
            Remark = remark,
            SourcePiId = piId,
            SourcePiNo = piNo,
            Details = new List<SalesOrderDetail>
            {
                new() { ProductId = _fixture.ProductId, ProductName = "ERP420 商品", Unit = "PCS", Quantity = 2m, UnitPrice = 5m }
            }
        };

    private async Task<Dictionary<string, int>> SnapshotAsync()
    {
        await using var db = _fixture.CreateDbContext();
        return new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["SalesOrders"] = await db.SalesOrders.CountAsync(),
            ["SalesOrderDetails"] = await db.SalesOrderDetails.CountAsync(),
            ["StockOuts"] = await db.StockOuts.CountAsync(),
            ["StockMovements"] = await db.StockMovements.CountAsync(),
            ["ProformaInvoices"] = await db.ProformaInvoices.CountAsync(),
            ["Quotations"] = await db.Quotations.CountAsync(),
            ["SysOperationLogs"] = await db.SysOperationLogs.CountAsync(),
        };
    }

    private static void AssertSnapshotUnchanged(
        IReadOnlyDictionary<string, int> before, IReadOnlyDictionary<string, int> after)
    {
        foreach (var table in SnapshotTables)
            Assert.Equal(before[table], after[table]);
    }

    // ==================== 新增：手工 / 历史来源 / 客户范围 ====================

    [Fact]
    public async Task 新增_手工无来源订单_本人客户_真实SQL放行()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();

        var result = await NewController(db, _fixture.RestrictedUserId)
            .Create(NewOrderBody(_fixture.CustomerAId));

        Assert.IsType<OkObjectResult>(result);
        await using var verify = _fixture.CreateDbContext();
        var order = await verify.SalesOrders.AsNoTracking()
            .Where(o => o.CustomerId == _fixture.CustomerAId && !o.IsDeleted)
            .OrderByDescending(o => o.Id).FirstAsync();
        Assert.Null(order.SourcePiId);
        Assert.Null(order.SourceQuotationId);
        Assert.Equal(DocumentStatus.Pending, order.Status);
        Assert.Equal(10m, order.TotalAmount);
        Assert.True(IsCreateDelta(before, await SnapshotAsync()));
    }

    private static bool IsCreateDelta(IReadOnlyDictionary<string, int> before,
        IReadOnlyDictionary<string, int> after)
        => after["SalesOrders"] == before["SalesOrders"] + 1
           && after["SalesOrderDetails"] == before["SalesOrderDetails"] + 1;

    [Fact]
    public async Task 新增_手工无来源订单_他人客户_拒绝且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.RestrictedUserId).Create(NewOrderBody(_fixture.CustomerBId)));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        AssertSnapshotUnchanged(before, await SnapshotAsync());
    }

    [Fact]
    public async Task 新增_手工无来源订单_null客户_拒绝且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.RestrictedUserId).Create(NewOrderBody(0L)));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        AssertSnapshotUnchanged(before, await SnapshotAsync());
    }

    [Fact]
    public async Task 新增_无法解析历史来源_本人客户_放行并原样保留显式历史值()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        var body = NewOrderBody(_fixture.CustomerAId, MissingPiId, "PI-HIST");
        Assert.IsType<OkObjectResult>(await NewController(db, _fixture.RestrictedUserId).Create(body));

        await using var verify = _fixture.CreateDbContext();
        var order = await verify.SalesOrders.AsNoTracking()
            .Where(o => o.SourcePiId == MissingPiId).OrderByDescending(o => o.Id).FirstAsync();
        Assert.Equal(MissingPiId, order.SourcePiId);
        Assert.Equal("PI-HIST", order.SourcePiNo);
        Assert.Equal(_fixture.CustomerAId, order.CustomerId);
    }

    [Fact]
    public async Task 新增_无法解析历史来源_他人客户_拒绝且零写入_修复无来源绕过()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();

        var body = NewOrderBody(_fixture.CustomerBId, MissingPiId, "PI-HIST");
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.RestrictedUserId).Create(body));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        AssertSnapshotUnchanged(before, await SnapshotAsync());
        await using var verify = _fixture.CreateDbContext();
        Assert.False(await verify.SalesOrders.AnyAsync(o => o.SourcePiId == MissingPiId));
    }

    // ==================== 新增：身份 / 菜单拒绝矩阵 ====================

    [Theory]
    [InlineData("no-identity")]
    [InlineData("malformed")]
    [InlineData("deleted")]
    public async Task 新增_未认证身份_拒绝且零写入(string scenario)
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();
        var controller = scenario switch
        {
            "malformed" => NewController(db, "not-a-number", hasIdentity: true),
            "deleted" => NewController(db, _fixture.DeletedUserId),
            _ => NewController(db, rawIdentity: string.Empty, hasIdentity: false),
        };

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            controller.Create(NewOrderBody(_fixture.CustomerAId)));

        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
        AssertSnapshotUnchanged(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("no-menu")]
    [InlineData("export-only")]
    [InlineData("revoked-menu")]
    public async Task 新增_权限不足身份_拒绝且零写入(string scenario)
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();
        var userId = scenario switch
        {
            "disabled" => _fixture.DisabledUserId,
            "export-only" => _fixture.ExportOnlyUserId,
            "revoked-menu" => _fixture.RevokedMenuUserId,
            _ => _fixture.MenuLessUserId,
        };

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, userId).Create(NewOrderBody(_fixture.CustomerAId)));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        AssertSnapshotUnchanged(before, await SnapshotAsync());
    }

    // ==================== 新增：特权账号保留既有口径 ====================

    [Fact]
    public async Task 新增_特权账号_他人客户仍放行()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        Assert.IsType<OkObjectResult>(await NewController(db, _fixture.PrivilegedUserId)
            .Create(NewOrderBody(_fixture.CustomerBId)));

        await using var verify = _fixture.CreateDbContext();
        Assert.True(await verify.SalesOrders.AnyAsync(o => o.CustomerId == _fixture.CustomerBId));
    }

    // ==================== 每次测试独立播种订单（避免相互干扰） ====================

    private async Task<SalesOrder> SeedOrderAsync(long customerId, DocumentStatus status)
    {
        await using var db = _fixture.CreateDbContext();
        var order = new SalesOrder
        {
            OrderNo = $"SO-ERP420-{Guid.NewGuid():N}"[..20],
            OrderDate = DateTime.Today,
            CustomerId = customerId,
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            DepositRatio = 30m,
            TotalAmount = 100m,
            Status = status
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = order.Id, ProductId = _fixture.ProductId, ProductName = "ERP420 商品",
            Unit = "PCS", Quantity = 1m, UnitPrice = 100m, Amount = 100m
        });
        await db.SaveChangesAsync();
        return order;
    }

    private async Task SeedApprovedStockOutAsync(long salesOrderId, long customerId)
    {
        await using var db = _fixture.CreateDbContext();
        db.StockOuts.Add(new StockOut
        {
            StockOutNo = $"CK-ERP420-{Guid.NewGuid():N}"[..20],
            StockOutDate = DateTime.Today,
            SalesOrderId = salesOrderId,
            CustomerId = customerId,
            Status = DocumentStatus.Approved
        });
        await db.SaveChangesAsync();
    }

    // ==================== 修改 ====================

    [Fact]
    public async Task 修改_本人待提交订单_真实SQL放行()
    {
        Guard();
        var order = await SeedOrderAsync(_fixture.CustomerAId, DocumentStatus.Pending);
        await using var db = _fixture.CreateDbContext();

        var result = await NewController(db, _fixture.RestrictedUserId)
            .Update(order.Id, NewOrderBody(_fixture.CustomerAId, remark: "UPDATED"));

        Assert.IsType<OkObjectResult>(result);
        await using var verify = _fixture.CreateDbContext();
        var saved = await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(_fixture.CustomerAId, saved.CustomerId);
        Assert.Equal("UPDATED", saved.Remark);
    }

    [Fact]
    public async Task 修改_他人订单_非披露NotFound且零写入()
    {
        Guard();
        var order = await SeedOrderAsync(_fixture.CustomerBId, DocumentStatus.Pending);
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.RestrictedUserId).Update(order.Id, NewOrderBody(_fixture.CustomerBId)));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Equal(SalesOrderMutationAuthorizationRules.OrderDeniedText, ex.Message);
        AssertSnapshotUnchanged(before, await SnapshotAsync());
    }

    [Fact]
    public async Task 修改_已删除订单_非披露NotFound()
    {
        Guard();
        var order = await SeedOrderAsync(_fixture.CustomerAId, DocumentStatus.Pending);
        await using (var db = _fixture.CreateDbContext())
        {
            db.SalesOrders.Single(o => o.Id == order.Id).IsDeleted = true;
            await db.SaveChangesAsync();
        }

        await using var controllerDb = _fixture.CreateDbContext();
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(controllerDb, _fixture.RestrictedUserId)
                .Update(order.Id, NewOrderBody(_fixture.CustomerAId)));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Equal(SalesOrderMutationAuthorizationRules.OrderDeniedText, ex.Message);
    }

    [Fact]
    public async Task 修改_改派到他人客户_拒绝且客户不变()
    {
        Guard();
        var order = await SeedOrderAsync(_fixture.CustomerAId, DocumentStatus.Pending);
        await using var db = _fixture.CreateDbContext();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.RestrictedUserId)
                .Update(order.Id, NewOrderBody(_fixture.CustomerBId)));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(_fixture.CustomerAId,
            (await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).CustomerId);
    }

    [Fact]
    public async Task 修改_不存在订单_非披露NotFound()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.RestrictedUserId)
                .Update(MissingOrderId, NewOrderBody(_fixture.CustomerAId)));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Equal(SalesOrderMutationAuthorizationRules.OrderDeniedText, ex.Message);
    }

    // ==================== 提交 / 审核 ====================

    [Fact]
    public async Task 提交与审核_本人订单_真实SQL放行至已审核()
    {
        Guard();
        var order = await SeedOrderAsync(_fixture.CustomerAId, DocumentStatus.Pending);
        await using var db = _fixture.CreateDbContext();

        Assert.IsType<OkObjectResult>(await NewController(db, _fixture.RestrictedUserId).Submit(order.Id));
        await using (var verify = _fixture.CreateDbContext())
            Assert.Equal(DocumentStatus.Submitted,
                (await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);

        await using var approveDb = _fixture.CreateDbContext();
        Assert.IsType<OkObjectResult>(await NewController(approveDb, _fixture.RestrictedUserId).Approve(order.Id));
        await using var final = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Approved,
            (await final.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
    }

    [Fact]
    public async Task 提交_他人订单_非披露NotFound且状态不变()
    {
        Guard();
        var order = await SeedOrderAsync(_fixture.CustomerBId, DocumentStatus.Pending);
        await using var db = _fixture.CreateDbContext();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.RestrictedUserId).Submit(order.Id));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Pending,
            (await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
    }

    [Fact]
    public async Task 审核_他人订单_非披露NotFound且状态不变()
    {
        Guard();
        var order = await SeedOrderAsync(_fixture.CustomerBId, DocumentStatus.Submitted);
        await using var db = _fixture.CreateDbContext();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.RestrictedUserId).Approve(order.Id));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Submitted,
            (await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
    }

    [Fact]
    public async Task 提交与审核_已禁用账号_权限不足且状态不变()
    {
        Guard();
        var pending = await SeedOrderAsync(_fixture.CustomerAId, DocumentStatus.Pending);
        var submitted = await SeedOrderAsync(_fixture.CustomerAId, DocumentStatus.Submitted);
        await using var db = _fixture.CreateDbContext();

        var submitEx = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.DisabledUserId).Submit(pending.Id));
        var approveEx = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.DisabledUserId).Approve(submitted.Id));

        Assert.Equal(ErrorCodes.Forbidden, submitEx.Code);
        Assert.Equal(ErrorCodes.Forbidden, approveEx.Code);
        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Pending,
            (await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == pending.Id)).Status);
        Assert.Equal(DocumentStatus.Submitted,
            (await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == submitted.Id)).Status);
    }

    // ==================== 删除 ====================

    [Fact]
    public async Task 删除_本人待提交订单_真实SQL放行软删除()
    {
        Guard();
        var order = await SeedOrderAsync(_fixture.CustomerAId, DocumentStatus.Pending);
        await using var db = _fixture.CreateDbContext();

        Assert.IsType<OkObjectResult>(await NewController(db, _fixture.RestrictedUserId).Delete(order.Id));
        await using var verify = _fixture.CreateDbContext();
        Assert.True((await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).IsDeleted);
    }

    [Fact]
    public async Task 删除_他人订单_非披露NotFound且未软删()
    {
        Guard();
        var order = await SeedOrderAsync(_fixture.CustomerBId, DocumentStatus.Pending);
        await using var db = _fixture.CreateDbContext();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.RestrictedUserId).Delete(order.Id));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        await using var verify = _fixture.CreateDbContext();
        Assert.False((await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).IsDeleted);
    }

    [Fact]
    public async Task 删除_不存在订单_非披露NotFound()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.RestrictedUserId).Delete(MissingOrderId));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Equal(SalesOrderMutationAuthorizationRules.OrderDeniedText, ex.Message);
    }

    // ==================== 取消（保留下游护栏） ====================

    [Fact]
    public async Task 取消_本人订单_真实SQL放行()
    {
        Guard();
        var order = await SeedOrderAsync(_fixture.CustomerAId, DocumentStatus.Approved);
        await using var db = _fixture.CreateDbContext();

        Assert.IsType<OkObjectResult>(await NewController(db, _fixture.RestrictedUserId).Cancel(order.Id));
        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Cancelled,
            (await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
    }

    [Fact]
    public async Task 取消_本人订单存在已审核出库_下游护栏仍拒绝()
    {
        Guard();
        var order = await SeedOrderAsync(_fixture.CustomerAId, DocumentStatus.Approved);
        await SeedApprovedStockOutAsync(order.Id, _fixture.CustomerAId);
        await using var db = _fixture.CreateDbContext();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.RestrictedUserId).Cancel(order.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Approved,
            (await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
    }

    [Fact]
    public async Task 取消_他人订单_非披露NotFound且状态不变()
    {
        Guard();
        var order = await SeedOrderAsync(_fixture.CustomerBId, DocumentStatus.Approved);
        await using var db = _fixture.CreateDbContext();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.RestrictedUserId).Cancel(order.Id));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Approved,
            (await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
    }

    [Fact]
    public async Task 取消_无身份_按未认证拒绝()
    {
        Guard();
        var order = await SeedOrderAsync(_fixture.CustomerAId, DocumentStatus.Approved);
        await using var db = _fixture.CreateDbContext();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, rawIdentity: string.Empty, hasIdentity: false).Cancel(order.Id));

        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Approved,
            (await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
    }
}

/// <summary>
/// ERP-420 集成夹具：在专用 localdb 实例上创建<b>全新 GUID 后缀</b>的 <c>NEWERP_AUTOTEST</c> 库并播种既有授权
/// （既有「销售订单」菜单 + 业务员客户数据范围，不新增权限模型）。
/// <para>安全口径：目标必须精确为 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且集成安全；
/// 任何库访问 / 建库之前先护栏；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库。</para>
/// </summary>
public sealed class SalesOrderMutationAuthorizationSqlServerFixture : IAsyncLifetime
{
    /// <summary>专用实例（精确匹配）。</summary>
    public const string InstanceTarget = @"(localdb)\NEWERP_AutoAcceptance";

    /// <summary>库名前缀（必须为 NEWERP_AUTOTEST）。</summary>
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    private const string DefaultDatabaseName = DatabasePrefix + "_SOMUTAUTH";

    public string ConnectionString { get; private set; } = string.Empty;

    public long PrivilegedUserId { get; private set; }
    public long RestrictedUserId { get; private set; }
    public long MenuLessUserId { get; private set; }
    public long ExportOnlyUserId { get; private set; }
    public long RevokedMenuUserId { get; private set; }
    public long DisabledUserId { get; private set; }
    public long DeletedUserId { get; private set; }
    public long CustomerAId { get; private set; }
    public long CustomerBId { get; private set; }

    /// <summary>ERP-423：既有合法商品（单位 PCS）—— 规范销售订单写入要求实时商品主数据。</summary>
    public long ProductId { get; private set; }

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-420] 目标库护栏放行（实例 {InstanceTarget}，库名前缀 {DatabasePrefix}）。");
        await CreateFreshDatabaseAsync();
        await SeedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

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

        Console.WriteLine("[ERP-420] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }

    // ==================== 既有授权（不新增权限模型）+ 客户夹具 ====================

    private async Task SeedAsync()
    {
        await using var db = CreateDbContext();

        // 1) 特权账号（系统内置角色，沿用既有全部访问口径）。
        var privilegedRole = new SysRole
        {
            RoleName = "ERP420 特权角色", RoleCode = $"ERP420-P-{Guid.NewGuid():N}", IsSystem = true
        };
        db.SysRoles.Add(privilegedRole);
        await db.SaveChangesAsync();
        var privileged = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(privileged);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = privileged.Id, RoleId = privilegedRole.Id });
        await db.SaveChangesAsync();
        PrivilegedUserId = privileged.Id;

        // 2) 受限业务员：既有「销售订单」功能菜单 + 客户数据范围（登录账号 == 员工编码，ERP-097 权威映射）。
        var restricted = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(restricted);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole
        {
            UserId = restricted.Id, RoleId = await AddRoleAsync(db, "sales-order")
        });
        await db.SaveChangesAsync();
        RestrictedUserId = restricted.Id;

        var employee = new BaseEmployee
        {
            EmployeeCode = restricted.UserName, EmployeeName = "ERP420 受限业务员", IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var customerA = new BaseCustomer
        {
            CustomerCode = $"C-A-{Guid.NewGuid():N}", CustomerName = "ERP420 可见客户",
            EmpId = employee.Id, Status = 1, CreditStatus = "正常"
        };
        var customerB = new BaseCustomer
        {
            CustomerCode = $"C-B-{Guid.NewGuid():N}", CustomerName = "ERP420 隐藏客户",
            EmpId = null, Status = 1, CreditStatus = "正常"
        };
        db.BaseCustomers.AddRange(customerA, customerB);
        await db.SaveChangesAsync();
        CustomerAId = customerA.Id;
        CustomerBId = customerB.Id;

        // ERP-423：既有合法商品（实时主数据引用护栏要求商品存在 / 未删除 / 启用，且单位落在既有有效口径内）。
        var product = new BaseProduct
        {
            ProductCode = $"P-ERP420-{Guid.NewGuid():N}", ProductName = "ERP420 商品",
            Unit = "PCS", Status = 1
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        ProductId = product.Id;

        // 3) 拒绝侧账号：无菜单 / 仅导出菜单 / 已撤销菜单 / 已禁用 / 已删除。
        MenuLessUserId = await SeedDeniedUserAsync(db, Array.Empty<string>(), UserStatus.Enabled);
        ExportOnlyUserId = await SeedDeniedUserAsync(db, new[] { "sales-order-export" }, UserStatus.Enabled);
        RevokedMenuUserId = await SeedRevokedMenuUserAsync(db);
        DisabledUserId = await SeedDeniedUserAsync(db, new[] { "sales-order" }, UserStatus.Disabled);
        DeletedUserId = await SeedDeniedUserAsync(db, new[] { "sales-order" }, UserStatus.Enabled, deleted: true);

        Console.WriteLine("[ERP-420] 既有授权 + 客户夹具就绪（受控只读，不新增权限模型）。");
    }

    private static SysUser NewUser(UserStatus status) => new()
    {
        UserName = $"erp420-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
        DisplayName = "ERP420 隔离账号", Status = status
    };

    private static async Task<long> AddRoleAsync(ErpDbContext db, params string[] menuCodes)
    {
        var role = new SysRole
        {
            RoleName = $"ERP420-{Guid.NewGuid():N}", RoleCode = $"ERP420-{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        foreach (var menuCode in menuCodes)
        {
            var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == menuCode && !m.IsDeleted);
            if (menu is null)
            {
                menu = new SysMenu { MenuName = menuCode, MenuCode = menuCode, MenuType = MenuType.Menu };
                db.SysMenus.Add(menu);
                await db.SaveChangesAsync();
            }

            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            await db.SaveChangesAsync();
        }

        return role.Id;
    }

    /// <summary>播种拒绝侧账号（无菜单 / 仅导出菜单 / 已禁用 / 已删除），不新增任何业务客户数据。</summary>
    private static async Task<long> SeedDeniedUserAsync(ErpDbContext db, string[] menuCodes, UserStatus status,
        bool deleted = false)
    {
        var user = NewUser(status);
        user.IsDeleted = deleted;
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = await AddRoleAsync(db, menuCodes) });
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>授予后又撤销既有「销售订单」菜单（角色仍在，授权已回收 → 下一次请求立即收敛）。</summary>
    private static async Task<long> SeedRevokedMenuUserAsync(ErpDbContext db)
    {
        var user = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();
        var roleId = await AddRoleAsync(db, "sales-order");
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = roleId });
        await db.SaveChangesAsync();
        db.SysRoleMenus.RemoveRange(db.SysRoleMenus.Where(rm => rm.RoleId == roleId));
        await db.SaveChangesAsync();
        return user.Id;
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class SalesOrderMutationAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => SalesOrderMutationAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}
