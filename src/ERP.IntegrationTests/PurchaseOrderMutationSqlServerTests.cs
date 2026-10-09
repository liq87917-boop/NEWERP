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
using System.Data;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-425 采购订单普通写入（创建 / 修改）确定性锁协议的真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标；构建完成不等于阶段验收）。
/// <para>直接执行<b>真实业务代码</b>（<see cref="PurchaseOrderController"/> +
/// <see cref="PurchaseOrderMutationRules"/> + <see cref="PurchaseSalesOrderLinkRules"/>），不复制测试专用实现：</para>
/// <list type="number">
/// <item><b>两条独立连接竞态</b>：手工订单「改单 vs 提交」「改单 vs 删除」「改单 vs 取消」，均只有一致结果
/// （受控失败方 + 唯一合法终态 + 完整明细集 + 不复活）；</item>
/// <item><b>已链接来源</b>：请求未给出来源时绝不静默清除血缘（真实 SQL 重读）；并发「来源取消（持来源行锁）
/// vs 采购改单（未给出来源）」串行化后来源仍被保留；显式改绑到另一已审核来源后权威快照更新且原始来源审计不被改写；</item>
/// <item><b>强制明细替换保存失败</b>：在同一原子事务内「删除旧明细 + 插入新明细」的插入被数据库 CHECK 约束拒绝后，
/// 表头 / 明细 / 状态整体回滚，绝不残留半成品变更。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只创建<b>全新 GUID 后缀库</b>，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。使用既有种子管理员
/// （<c>SeedData</c> 已授予全部菜单）身份，不新增任何用户授权、不使用 HTTP / 测试身份绕过。</para>
/// </summary>
public sealed class PurchaseOrderMutationSqlServerTests : IClassFixture<PurchaseOrderMutationSqlServerFixture>
{
    private readonly PurchaseOrderMutationSqlServerFixture _fixture;

    public PurchaseOrderMutationSqlServerTests(PurchaseOrderMutationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(PurchaseOrderMutationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static DefaultHttpContext HttpFor(long? userId)
    {
        var http = new DefaultHttpContext();
        if (userId.HasValue)
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "IntegrationTest"));
        return http;
    }

    private static PurchaseOrderController ControllerFor(ErpDbContext db, long userId)
        => new(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = HttpFor(userId) }
        };

    private static async Task<long> ResolveAdminIdAsync(ErpDbContext db)
        => await db.SysUsers.AsNoTracking()
            .Where(u => u.UserName == SeedData.AdminUserName && !u.IsDeleted)
            .Select(u => u.Id).FirstAsync();

    // ==================== 控制器 / 双连接脚手架 ====================

    private async Task<(bool Success, string Error)> TryUpdateAsync(long userId, long orderId, PurchaseOrder body)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await ControllerFor(db, userId).Update(orderId, body);
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool Success, string Error)> TrySubmitAsync(long userId, long orderId)
        => await RunAsync(db => ControllerFor(db, userId).Submit(orderId));

    private async Task<(bool Success, string Error)> TryDeleteAsync(long userId, long orderId)
        => await RunAsync(db => ControllerFor(db, userId).Delete(orderId));

    private async Task<(bool Success, string Error)> TryCancelAsync(long userId, long orderId)
        => await RunAsync(db => ControllerFor(db, userId).Cancel(orderId));

    private async Task<(bool Success, string Error)> RunAsync(Func<ErpDbContext, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            return (await action(db) is OkObjectResult, string.Empty);
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

    private async Task ExecuteRawAsync(string sql)
    {
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    // ==================== 1. 两条独立连接：手工订单 改单 vs 提交 ====================

    [Fact]
    public async Task 两条独立连接_手工订单改单与提交_受控失败_唯一合法终态_完整明细()
    {
        Guard();
        var tag = Tag();
        long adminId, orderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            adminId = await ResolveAdminIdAsync(seed);
            orderId = (await SeedOrderAsync(seed, DocumentStatus.Pending)).Id;
        }

        var results = await RaceAsync(
            () => TryUpdateAsync(adminId, orderId, NewBody(productName: "改后商品")),
            () => TrySubmitAsync(adminId, orderId));

        // 提交必然成功（受控失败方只可能是改单：提交先行后改单读到已提交状态并原子拒绝）。
        Assert.True(results[1].Success, results[1].Error);

        await using var verify = _fixture.CreateDbContext();
        var order = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal(DocumentStatus.Submitted, order.Status);       // 唯一合法终态
        Assert.False(order.IsDeleted);

        var details = await verify.PurchaseOrderDetails.AsNoTracking()
            .Where(d => d.PurchaseOrderId == orderId && !d.IsDeleted).ToListAsync();
        Assert.Single(details);                                     // 明细集完整（改单先行则替换，否则保持原样）
        Assert.Equal(results[0].Success ? "改后商品" : "集成商品", details[0].ProductName);
        Assert.True(order.TotalAmount is 1200m or 1000m);           // 改单先行 = 20×60；否则保持原值 1000
    }

    // ==================== 2. 两条独立连接：手工订单 改单 vs 删除 ====================

    [Fact]
    public async Task 两条独立连接_手工订单改单与删除_删除恒成功_软删除不可复活()
    {
        Guard();
        var tag = Tag();
        long adminId, orderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            adminId = await ResolveAdminIdAsync(seed);
            orderId = (await SeedOrderAsync(seed, DocumentStatus.Pending)).Id;
        }

        var results = await RaceAsync(
            () => TryUpdateAsync(adminId, orderId, NewBody(productName: "改后商品")),
            () => TryDeleteAsync(adminId, orderId));

        Assert.True(results[1].Success, results[1].Error);   // 删除恒成功；改单只有在先取得锁时才成功

        await using var verify = _fixture.CreateDbContext();
        var order = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.True(order.IsDeleted);                       // 软删除不可复活
        Assert.Equal(DocumentStatus.Pending, order.Status);
        Assert.Equal(1, await verify.PurchaseOrderDetails.AsNoTracking()
            .CountAsync(d => d.PurchaseOrderId == orderId && !d.IsDeleted));
    }

    // ==================== 3. 两条独立连接：手工订单 改单 vs 取消 ====================

    [Fact]
    public async Task 两条独立连接_手工订单改单与取消_取消恒成功_唯一合法终态()
    {
        Guard();
        var tag = Tag();
        long adminId, orderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            adminId = await ResolveAdminIdAsync(seed);
            orderId = (await SeedOrderAsync(seed, DocumentStatus.Pending)).Id;
        }

        var results = await RaceAsync(
            () => TryUpdateAsync(adminId, orderId, NewBody(productName: "改后商品")),
            () => TryCancelAsync(adminId, orderId));

        Assert.True(results[1].Success, results[1].Error);   // 取消恒成功；改单只有在先取得锁时才成功

        await using var verify = _fixture.CreateDbContext();
        var order = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal(DocumentStatus.Cancelled, order.Status);   // 唯一合法终态
        Assert.False(order.IsDeleted);
        Assert.Equal(1, await verify.PurchaseOrderDetails.AsNoTracking()
            .CountAsync(d => d.PurchaseOrderId == orderId && !d.IsDeleted));
    }

    // ==================== 4. 已链接来源：请求未给出来源绝不静默清除血缘 ====================

    [Fact]
    public async Task 已链接来源_请求未给出来源_保留血缘且不变更原始来源审计()
    {
        Guard();
        var tag = Tag();
        long adminId, orderId, salesOrderId;
        DateTime? sourceUpdatedAt;
        await using (var seed = _fixture.CreateDbContext())
        {
            adminId = await ResolveAdminIdAsync(seed);
            var customer = await SeedCustomerAsync(seed);
            var salesOrder = await SeedApprovedSalesOrderAsync(seed, customer.Id);
            salesOrderId = salesOrder.Id;
            orderId = (await SeedLinkedOrderAsync(seed, customer.Id, salesOrder.Id, DocumentStatus.Pending)).Id;
        }

        // 以全新连接读取来源审计基线（避免内存值 / 数据库精度差异）。
        await using (var before = _fixture.CreateDbContext())
            sourceUpdatedAt = await before.SalesOrders.AsNoTracking()
                .Where(o => o.Id == salesOrderId).Select(o => (DateTime?)o.UpdatedAt).SingleAsync();

        var edit = await TryUpdateAsync(adminId, orderId, WithoutSource(NewBody(remark: "仅改备注")));

        Assert.True(edit.Success, edit.Error);
        await using var verify = _fixture.CreateDbContext();
        var order = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal(salesOrderId, order.OwningSalesOrderId);       // 血缘保留（绝不静默清除）
        Assert.Equal("仅改备注", order.Remark);

        // 原始来源审计不被本次改单改写（来源销售订单行锁只用 UPDLOCK/HOLDLOCK，绝不 UPDATE 来源行）。
        var source = await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == salesOrderId);
        Assert.Equal(DocumentStatus.Approved, source.Status);
        Assert.False(source.IsDeleted);
        Assert.Equal(sourceUpdatedAt, source.UpdatedAt);
    }

    // ==================== 5. 两条独立连接：来源取消 vs 已链接改单（未给出来源） ====================

    [Fact]
    public async Task 两条独立连接_来源取消与已链接改单_来源仍被保留且取消受控成功()
    {
        Guard();
        var tag = Tag();
        long adminId, orderId, salesOrderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            adminId = await ResolveAdminIdAsync(seed);
            var customer = await SeedCustomerAsync(seed);
            var salesOrder = await SeedApprovedSalesOrderAsync(seed, customer.Id);
            salesOrderId = salesOrder.Id;
            orderId = (await SeedLinkedOrderAsync(seed, customer.Id, salesOrder.Id, DocumentStatus.Pending)).Id;
        }

        // 连接 1：持来源销售订单行锁并取消来源；连接 2：同单采购改单（请求未给出来源，须先锁来源行再锁采购行）。
        var lockAcquired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancel = Task.Run(() => TryCancelSourceAsync(salesOrderId, lockAcquired));
        await lockAcquired.Task;

        var edit = await TryUpdateAsync(adminId, orderId, WithoutSource(NewBody(remark: "并发改单")));
        var cancelResult = await cancel;

        Assert.True(cancelResult.Success, cancelResult.Error);        // 来源取消受控成功
        Assert.False(edit.Success);                                   // 改单读到已取消来源并原子拒绝
        Assert.Contains("已取消", edit.Error);

        await using var verify = _fixture.CreateDbContext();
        var order = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal(salesOrderId, order.OwningSalesOrderId);         // 血缘原样保留（绝不静默清除）
        Assert.Equal(DocumentStatus.Pending, order.Status);
        Assert.Equal(DocumentStatus.Cancelled,
            (await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == salesOrderId)).Status);
    }

    // ==================== 7. 已链接来源：显式改绑到另一已审核来源 ====================

    [Fact]
    public async Task 已链接来源_显式改绑另一已审核来源_权威快照更新且原始来源审计保留()
    {
        Guard();
        var tag = Tag();
        long adminId, orderId, originalSalesOrderId, newSalesOrderId;
        DateTime? originalUpdatedAt;
        await using (var seed = _fixture.CreateDbContext())
        {
            adminId = await ResolveAdminIdAsync(seed);
            var customer = await SeedCustomerAsync(seed);
            var original = await SeedApprovedSalesOrderAsync(seed, customer.Id);
            var replacement = await SeedApprovedSalesOrderAsync(seed, customer.Id);
            originalSalesOrderId = original.Id;
            newSalesOrderId = replacement.Id;
            orderId = (await SeedLinkedOrderAsync(seed, customer.Id, original.Id, DocumentStatus.Pending)).Id;
        }

        // 以全新连接读取原始来源审计基线（避免内存值 / 数据库精度差异）。
        await using (var before = _fixture.CreateDbContext())
            originalUpdatedAt = await before.SalesOrders.AsNoTracking()
                .Where(o => o.Id == originalSalesOrderId).Select(o => (DateTime?)o.UpdatedAt).SingleAsync();

        var edit = await TryUpdateAsync(adminId, orderId, WithSource(NewBody(remark: "改绑"), newSalesOrderId));

        Assert.True(edit.Success, edit.Error);
        await using var verify = _fixture.CreateDbContext();
        var order = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal(newSalesOrderId, order.OwningSalesOrderId);      // 权威改绑生效
        Assert.Equal(DocumentStatus.Pending, order.Status);

        var originalSource = await verify.SalesOrders.AsNoTracking()
            .SingleAsync(o => o.Id == originalSalesOrderId);
        Assert.Equal(DocumentStatus.Approved, originalSource.Status); // 原始来源审计原样保留
        Assert.False(originalSource.IsDeleted);
        Assert.Equal(originalUpdatedAt, originalSource.UpdatedAt);
    }

    // ==================== 8. 强制明细替换保存失败：整体回滚 ====================

    [Fact]
    public async Task 明细替换保存失败_头与明细整体回滚且不留半成品()
    {
        Guard();
        var tag = Tag();
        long adminId, orderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            adminId = await ResolveAdminIdAsync(seed);
            orderId = (await SeedOrderAsync(seed, DocumentStatus.Pending)).Id;
        }

        // 在专用 GUID 测试库上强制「删除旧明细 + 插入新明细」的插入被数据库 CHECK 约束拒绝。
        await ExecuteRawAsync(
            "ALTER TABLE db_owner.PurchaseOrderDetails WITH NOCHECK ADD CONSTRAINT "
            + $"CK_ERP425_FAIL_{tag} CHECK (ProductName <> N'ERP425_FORCE_FAIL');");

        var edit = await TryUpdateAsync(adminId, orderId, NewBody(productName: "ERP425_FORCE_FAIL"));
        Assert.False(edit.Success);

        await using var verify = _fixture.CreateDbContext();
        var order = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal(DocumentStatus.Pending, order.Status);     // 表头 / 状态零改动
        Assert.False(order.IsDeleted);
        Assert.Equal(1000m, order.TotalAmount);

        var details = await verify.PurchaseOrderDetails.AsNoTracking()
            .Where(d => d.PurchaseOrderId == orderId && !d.IsDeleted).ToListAsync();
        Assert.Single(details);                                 // 原明细完整保留，绝不残留半成品
        Assert.Equal("集成商品", details[0].ProductName);
        Assert.Equal(10m, details[0].Quantity);
    }

    // ==================== 9. 合法普通生命周期：链接创建 → 改单 → 提交 → 审核 ====================

    [Fact]
    public async Task 合法普通生命周期_链接创建_改单_提交_审核_在真实SQL上依次成功()
    {
        Guard();
        long adminId, salesOrderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            adminId = await ResolveAdminIdAsync(seed);
            var customer = await SeedCustomerAsync(seed);
            salesOrderId = (await SeedApprovedSalesOrderAsync(seed, customer.Id)).Id;
        }

        long orderId;
        await using (var db = _fixture.CreateDbContext())
        {
            var body = WithSource(NewBody(productName: "生命周期商品"), salesOrderId);
            var result = await ControllerFor(db, adminId).Create(body);
            Assert.IsType<OkObjectResult>(result);
            orderId = body.Id;
        }

        // 改单（请求未给出来源 → 保留血缘）→ 提交 → 审核 依次成功。
        var edit = await TryUpdateAsync(adminId, orderId, WithoutSource(NewBody(productName: "生命周期改后")));
        Assert.True(edit.Success, edit.Error);

        await using (var db = _fixture.CreateDbContext())
            Assert.IsType<OkObjectResult>(await ControllerFor(db, adminId).Submit(orderId));
        await using (var db = _fixture.CreateDbContext())
            Assert.IsType<OkObjectResult>(await ControllerFor(db, adminId).Approve(orderId));

        await using var verify = _fixture.CreateDbContext();
        var order = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal(DocumentStatus.Approved, order.Status);
        Assert.Equal(salesOrderId, order.OwningSalesOrderId);   // 来源血缘贯穿整个生命周期
        Assert.Equal(1, await verify.PurchaseOrderDetails.AsNoTracking()
            .CountAsync(d => d.PurchaseOrderId == orderId && !d.IsDeleted));
    }

    /// <summary>在独立连接 + 可串行化事务内持来源销售订单行锁并取消来源（模拟真实「来源取消」竞态方）。</summary>
    private async Task<(bool Success, string Error)> TryCancelSourceAsync(long salesOrderId,
        TaskCompletionSource<bool> lockAcquired)
    {
        await using var db = _fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await db.Database.SqlQueryRaw<long>(
                "SELECT Id FROM db_owner.SalesOrders WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}", salesOrderId)
                .ToListAsync();
            var order = await db.SalesOrders.SingleAsync(o => o.Id == salesOrderId);
            order.Status = DocumentStatus.Cancelled;
            await db.SaveChangesAsync();

            lockAcquired.TrySetResult(true);
            // 持锁等待，确保采购改单侧在同一把来源行锁上竞争后才提交。
            await Task.Delay(200);

            await transaction.CommitAsync();
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ==================== 种子助手（合法夹具身份 / 主数据，不使用生产旁路） ====================

    private static PurchaseOrder NewBody(long? owningCustomerId = null, string productName = "改后商品",
        decimal quantity = 20m, decimal unitPrice = 60m, string remark = "")
        => new()
        {
            OrderDate = DateTime.Today,
            SupplierId = 1,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            OwningCustomerId = owningCustomerId,
            Remark = remark,
            Details = new List<PurchaseOrderDetail>
            {
                new()
                {
                    ProductId = 100, ProductName = productName, Spec = "规格A", Unit = "PCS",
                    Quantity = quantity, UnitPrice = unitPrice, Amount = quantity * unitPrice
                }
            }
        };

    private static PurchaseOrder WithoutSource(PurchaseOrder order)
    {
        order.OwningSalesOrderId = null;
        return order;
    }

    private static PurchaseOrder WithSource(PurchaseOrder order, long salesOrderId)
    {
        order.OwningSalesOrderId = salesOrderId;
        return order;
    }

    private static async Task<BaseCustomer> SeedCustomerAsync(ErpDbContext db)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"PO425-C-{Guid.NewGuid():N}",
            CustomerName = "集成客户",
            Status = 1
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<SalesOrder> SeedApprovedSalesOrderAsync(ErpDbContext db, long customerId)
    {
        var order = new SalesOrder
        {
            OrderNo = $"PO425-SO-{Guid.NewGuid():N}",
            OrderDate = DateTime.Today.AddDays(-5),
            CustomerId = customerId,
            Currency = Currency.USD,
            Status = DocumentStatus.Approved,
            Details = new List<SalesOrderDetail>
            {
                new()
                {
                    ProductId = 100, ProductName = "集成商品", Spec = "规格A", Unit = "PCS",
                    Quantity = 100m, UnitPrice = 10m, Amount = 1000m
                }
            }
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static async Task<PurchaseOrder> SeedOrderAsync(ErpDbContext db, DocumentStatus status)
    {
        var order = new PurchaseOrder
        {
            OrderNo = $"PO425-{Guid.NewGuid():N}",
            OrderDate = DateTime.Today,
            SupplierId = 1,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            Status = status,
            TotalAmount = 1000m,
            Details = new List<PurchaseOrderDetail>
            {
                new()
                {
                    ProductId = 100, ProductName = "集成商品", Spec = "规格A", Unit = "PCS",
                    Quantity = 10m, UnitPrice = 100m, Amount = 1000m
                }
            }
        };
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static async Task<PurchaseOrder> SeedLinkedOrderAsync(ErpDbContext db, long customerId,
        long salesOrderId, DocumentStatus status)
    {
        var order = new PurchaseOrder
        {
            OrderNo = $"PO425-L-{Guid.NewGuid():N}",
            OrderDate = DateTime.Today,
            SupplierId = 1,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            OwningCustomerId = customerId,
            OwningSalesOrderId = salesOrderId,
            Status = status,
            TotalAmount = 1000m,
            Details = new List<PurchaseOrderDetail>
            {
                new()
                {
                    ProductId = 100, ProductName = "集成商品", Spec = "规格A", Unit = "PCS",
                    Quantity = 10m, UnitPrice = 100m, Amount = 1000m
                }
            }
        };
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }
}

/// <summary>
/// ERP-425 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库；连接串只来自进程环境变量
/// <c>ERP_ConnectionStrings__Default</c> 或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class PurchaseOrderMutationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_POMUTATION_20261009";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine("[ERP-425] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-425] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class PurchaseOrderMutationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => PurchaseOrderMutationSqlServerFixture.AssertDedicatedTarget(connection));
}

