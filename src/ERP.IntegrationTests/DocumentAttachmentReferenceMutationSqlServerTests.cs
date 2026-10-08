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
/// ERP-412 业务单据附件引用「登记 / 作废」原子性与确定性行锁的真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>两个独立连接竞态</b>：① 同身份并发登记 → 恰好一条有效身份、输家稳定业务冲突（Duplicate，不含
/// SqlException / 内部路径）；② 登记 vs 父单据归属变更 → 合法一致登记或零写入拒绝；③ 同行并发作废 →
/// 只有一个赢家，原始作废原因与时间戳保留，输家绝不覆盖。</item>
/// <item><b>确定性顺序</b>：父单据已软删除 / 归属越界 / 提交前菜单撤销后登记一律 fail closed（零落库）。</item>
/// <item><b>显式重登记</b>：作废后同一「父单据 + 分类 + 引用标识」可重新登记（新身份），历史留痕不可变。</item>
/// <item><b>元数据写入失败</b>：完整回滚零部分写入，父单据 / 库存 / 财务计数前后完全不变。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>保留既有库存来源单据审计与原始失败日志：本测试只新增自己的引用行，不清理 / 不删除任何既有行。
/// 构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class DocumentAttachmentReferenceMutationSqlServerTests
    : IClassFixture<DocumentAttachmentReferenceMutationSqlServerFixture>
{
    private readonly DocumentAttachmentReferenceMutationSqlServerFixture _fixture;

    public DocumentAttachmentReferenceMutationSqlServerTests(
        DocumentAttachmentReferenceMutationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(DocumentAttachmentReferenceMutationSqlServerFixture.DatabasePrefix,
            target.InitialCatalog, StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 独立连接脚手架 ====================

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static DocumentAttachmentReferenceController NewController(ErpDbContext db, long? userId)
    {
        var http = new DefaultHttpContext();
        if (userId.HasValue)
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"));

        return new DocumentAttachmentReferenceController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    /// <summary>一条独立连接完成一次真实登记；返回成功标志、业务错误码 / 类型与错误文案。</summary>
    private async Task<(bool Success, int? Code, string Type, string Error)> TryCreateAsync(
        long? userId, DocumentAttachmentReferenceSaveDto dto)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await NewController(db, userId).Create(dto);
            return (result is OkObjectResult, ErrorCodes.Success, string.Empty, string.Empty);
        }
        catch (BusinessException ex)
        {
            return (false, ex.Code, ex.GetType().Name, ex.Message);
        }
        catch (Exception ex)
        {
            return (false, null, ex.GetType().Name, ex.Message);
        }
    }

    /// <summary>一条独立连接完成一次真实作废（实时解析授权 + 真实原子护栏）。</summary>
    private async Task<(bool Success, int? Code, string Type, string Error)> TryVoidAsync(
        long? userId, long id, string reason)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await NewController(db, userId).Void(
                id, new DocumentAttachmentReferenceVoidRequest { Reason = reason });
            return (result is OkObjectResult, ErrorCodes.Success, string.Empty, string.Empty);
        }
        catch (BusinessException ex)
        {
            return (false, ex.Code, ex.GetType().Name, ex.Message);
        }
        catch (Exception ex)
        {
            return (false, null, ex.GetType().Name, ex.Message);
        }
    }

    /// <summary>两条独立连接以同一起跑线并发执行（门闩对齐），返回两侧结果。</summary>
    private static async Task<List<T>> RaceAsync<T>(Func<Task<T>> first, Func<Task<T>> second)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<T> Run(Func<Task<T>> action)
        {
            await gate.Task;
            return await action();
        }

        var left = Run(first);
        var right = Run(second);
        gate.SetResult();
        return (await Task.WhenAll(left, right)).ToList();
    }

    // ==================== 种子与观测（真实既有菜单 / 业务员数据范围，不新增权限模型） ====================

    private static async Task<BaseCustomer> SeedCustomerAsync(ErpDbContext db, string code, long? empId)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code, CustomerName = code, Status = 1, CreditStatus = "正常",
            Currency = "USD", EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<(SysUser User, SysRole Role)> SeedSalesmanAsync(
        ErpDbContext db, params string[] menuCodes)
    {
        var code = $"INT_E412_EMP_{Tag()}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = $"INT_E412_ROLE_{code}", RoleCode = $"INT_E412_{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });

        foreach (var menuCode in menuCodes)
        {
            var menu = await db.SysMenus.FirstAsync(m => !m.IsDeleted && m.MenuCode == menuCode);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        }

        await db.SaveChangesAsync();
        return (user, role);
    }

    private static async Task<SalesOrder> SeedSalesOrderAsync(ErpDbContext db, string orderNo, long customerId)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo, OrderDate = DateTime.Today, CustomerId = customerId,
            Currency = Currency.USD, TotalAmount = 2468.00m, DepositAmount = 100m,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static DocumentAttachmentReferenceSaveDto DarDto(
        long parentId, string referenceId,
        string parentType = DocumentAttachmentReferenceRules.ParentTypeSalesOrder)
        => new()
        {
            ParentType = parentType,
            ParentId = parentId,
            Category = DocumentAttachmentReferenceRules.CategoryContract,
            DisplayName = "ERP-412 集成合同扫描件",
            ReferenceId = referenceId,
            SourceAuthorizationAcknowledged = true,
            SourceAuthorizationNote = "ERP-412 集成测试来源授权留痕",
            AuthorizedBy = "ERP-412 集成登记人"
        };

    private static async Task RevokeMenusAsync(ErpDbContext db, long roleId)
    {
        var grants = await db.SysRoleMenus.Where(rm => rm.RoleId == roleId && !rm.IsDeleted).ToListAsync();
        foreach (var grant in grants) grant.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    /// <summary>独立连接：软删除父单据（模拟「父单据并发删除」）。</summary>
    private async Task SoftDeleteOrderAsync(long orderId)
    {
        await using var db = _fixture.CreateDbContext();
        // 先取得竞争方父单据行锁，再读取 RowVersion：避免陈旧令牌被误报成业务失败。
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync(
            "SELECT [Id] FROM [db_owner].[SalesOrders] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {0}", orderId);
        var order = await db.SalesOrders.FirstAsync(o => o.Id == orderId);
        order.IsDeleted = true;
        order.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    /// <summary>独立连接：改写父单据归属客户（模拟「归属客户并发变更」）。</summary>
    private async Task ChangeOrderCustomerAsync(long orderId, long customerId)
    {
        await using var db = _fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync(
            "SELECT [Id] FROM [db_owner].[SalesOrders] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {0}", orderId);
        var order = await db.SalesOrders.FirstAsync(o => o.Id == orderId);
        order.CustomerId = customerId;
        order.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private async Task<int> CountReferencesAsync(long orderId, int? status = null)
    {
        await using var db = _fixture.CreateDbContext();
        var query = db.DocumentAttachmentReferences.AsNoTracking()
            .Where(r => r.ParentType == DocumentAttachmentReferenceRules.ParentTypeSalesOrder
                        && r.ParentId == orderId);
        if (status.HasValue) query = query.Where(r => r.Status == status.Value);
        return await query.CountAsync();
    }

    private async Task<int> CountReferenceByIdentityAsync(long orderId, string referenceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.DocumentAttachmentReferences.AsNoTracking()
            .CountAsync(r => r.ParentId == orderId && r.ReferenceId == referenceId);
    }

    private async Task<DocumentAttachmentReference> ReloadReferenceAsync(long referenceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.DocumentAttachmentReferences.AsNoTracking().FirstAsync(r => r.Id == referenceId);
    }

    private async Task<DocumentAttachmentReference> ReloadReferenceForParentAsync(long orderId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.DocumentAttachmentReferences.AsNoTracking()
            .FirstAsync(r => r.ParentId == orderId && r.Status == DocumentAttachmentReferenceRules.StatusActive);
    }

    private async Task<(decimal TotalAmount, decimal DepositAmount, long CustomerId, DocumentStatus Status, bool Deleted)>
        ReadParentAsync(long orderId)
    {
        await using var db = _fixture.CreateDbContext();
        var order = await db.SalesOrders.AsNoTracking().FirstAsync(o => o.Id == orderId);
        return (order.TotalAmount, order.DepositAmount, order.CustomerId, order.Status, order.IsDeleted);
    }

    /// <summary>库存 / 库存流水 / 财务记录计数（元数据引用路径必须零写入）。</summary>
    private async Task<(int Stocks, int Movements, int Receipts)> CountUntouchedAsync()
    {
        await using var db = _fixture.CreateDbContext();
        return (await db.Stocks.CountAsync(),
                await db.StockMovements.CountAsync(),
                await db.FinanceReceipts.CountAsync());
    }

    // ==================== 1. 竞态一：两条独立连接并发同身份登记 ====================

    [Fact]
    public async Task 竞态一_两条独立连接并发同身份登记_恰好一条有效身份且输家稳定业务冲突()
    {
        Guard();
        var tag = Tag();
        long userId, orderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, _) = await SeedSalesmanAsync(seed, DocumentAttachmentReferenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            var employee = await seed.BaseEmployees.FirstAsync(e => e.EmployeeCode == user.UserName);
            var customerId = (await SeedCustomerAsync(seed, $"INT_E412_A_{tag}", employee.Id)).Id;
            orderId = (await SeedSalesOrderAsync(seed, $"SO-E412-A-{tag}", customerId)).Id;
        }

        var identity = $"int-e412-a-{tag}";
        var parentBefore = await ReadParentAsync(orderId);
        var untouchedBefore = await CountUntouchedAsync();

        var results = await RaceAsync(
            () => TryCreateAsync(userId, DarDto(orderId, identity)),
            () => TryCreateAsync(userId, DarDto(orderId, identity)));

        var winners = results.Where(r => r.Success).ToList();
        var losers = results.Where(r => !r.Success).ToList();
        Assert.Single(winners);
        Assert.Single(losers);

        // 输家是稳定的业务冲突（Duplicate），绝不暴露 SqlException / 内部路径 / 索引名 / 错误码
        Assert.Equal(ErrorCodes.Duplicate, losers[0].Code);
        Assert.Equal("BusinessException", losers[0].Type);
        Assert.DoesNotContain("SqlException", losers[0].Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.Data.SqlClient", losers[0].Error, StringComparison.Ordinal);
        Assert.DoesNotContain("UX_", losers[0].Error, StringComparison.Ordinal);
        Assert.DoesNotContain("2601", losers[0].Error, StringComparison.Ordinal);

        // 恰好一条有效身份 + 零部分写入（输家已完整回滚）
        Assert.Equal(1, await CountReferencesAsync(orderId, DocumentAttachmentReferenceRules.StatusActive));
        Assert.Equal(1, await CountReferencesAsync(orderId));
        Assert.Equal(1, await CountReferenceByIdentityAsync(orderId, identity));

        // 父单据身份 / 商业字段不变；库存 / 财务零写入
        var parentAfter = await ReadParentAsync(orderId);
        Assert.Equal(parentBefore.TotalAmount, parentAfter.TotalAmount);
        Assert.Equal(parentBefore.DepositAmount, parentAfter.DepositAmount);
        Assert.Equal(parentBefore.CustomerId, parentAfter.CustomerId);
        Assert.Equal(parentBefore.Status, parentAfter.Status);
        Assert.Equal(parentBefore.Deleted, parentAfter.Deleted);
        Assert.Equal(untouchedBefore, await CountUntouchedAsync());
    }

    // ==================== 2. 竞态二：两条独立连接 · 登记 vs 父单据归属变更 ====================

    [Fact]
    public async Task 竞态二_两条独立连接_登记与父单据归属变更并发_合法一致登记或零写入拒绝()
    {
        Guard();
        var tag = Tag();
        long userId, orderId, foreignCustomerId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, _) = await SeedSalesmanAsync(seed, DocumentAttachmentReferenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            var employee = await seed.BaseEmployees.FirstAsync(e => e.EmployeeCode == user.UserName);
            var customerId = (await SeedCustomerAsync(seed, $"INT_E412_B_{tag}", employee.Id)).Id;
            foreignCustomerId = (await SeedCustomerAsync(seed, $"INT_E412_BX_{tag}", null)).Id;
            orderId = (await SeedSalesOrderAsync(seed, $"SO-E412-B-{tag}", customerId)).Id;
        }

        var identity = $"int-e412-b-{tag}";
        var untouchedBefore = await CountUntouchedAsync();

        var results = await RaceAsync(
            () => TryCreateAsync(userId, DarDto(orderId, identity)),
            async () =>
            {
                await ChangeOrderCustomerAsync(orderId, foreignCustomerId);
                return (Success: true, Code: (int?)ErrorCodes.Success, Type: string.Empty, Error: string.Empty);
            });

        var created = results[0].Success;

        // 归属变更已提交（变更方是赢家）：父单据身份 / 金额 / 押金不变，仅归属客户改写
        var parent = await ReadParentAsync(orderId);
        Assert.Equal(foreignCustomerId, parent.CustomerId);
        Assert.Equal(2468.00m, parent.TotalAmount);
        Assert.Equal(100m, parent.DepositAmount);
        Assert.False(parent.Deleted);

        // 结论只能是「登记成功（当时合法）且恰好一条有效身份」或「零写入拒绝」，绝不出现撕裂
        if (created)
        {
            Assert.Equal(1, await CountReferencesAsync(orderId, DocumentAttachmentReferenceRules.StatusActive));
            await using var verify = _fixture.CreateDbContext();
            var row = await verify.DocumentAttachmentReferences.AsNoTracking()
                .SingleAsync(r => r.ParentId == orderId);
            Assert.Equal(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, row.ParentType);
            Assert.Equal(orderId, row.ParentId);
            Assert.Equal(DocumentAttachmentReferenceRules.StatusActive, row.Status);
        }
        else
        {
            Assert.Equal(0, await CountReferencesAsync(orderId));
        }

        Assert.Equal(untouchedBefore, await CountUntouchedAsync());
    }

    // ==================== 3. 竞态三：两条独立连接并发作废 ====================

    [Fact]
    public async Task 竞态三_两条独立连接并发作废_原始原因与时间保留且输家不覆盖()
    {
        Guard();
        var tag = Tag();
        long userId, orderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, _) = await SeedSalesmanAsync(seed, DocumentAttachmentReferenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            var employee = await seed.BaseEmployees.FirstAsync(e => e.EmployeeCode == user.UserName);
            var customerId = (await SeedCustomerAsync(seed, $"INT_E412_C_{tag}", employee.Id)).Id;
            orderId = (await SeedSalesOrderAsync(seed, $"SO-E412-C-{tag}", customerId)).Id;
        }

        var identity = $"int-e412-c-{tag}";
        var created = await TryCreateAsync(userId, DarDto(orderId, identity));
        Assert.True(created.Success);
        var referenceId = (await ReloadReferenceForParentAsync(orderId)).Id;

        var reasonA = $"并发作废原因 A {tag}";
        var reasonB = $"并发作废原因 B {tag}";
        var results = await RaceAsync(
            () => TryVoidAsync(userId, referenceId, reasonA),
            () => TryVoidAsync(userId, referenceId, reasonB));

        Assert.Single(results.Where(r => r.Success));               // 恰好一个赢家
        var loser = results.First(r => !r.Success);                 // 输家原子拒绝
        Assert.Equal(ErrorCodes.RuleConflict, loser.Code);
        Assert.DoesNotContain("SqlException", loser.Error, StringComparison.Ordinal);

        var stored = await ReloadReferenceAsync(referenceId);
        Assert.Equal(DocumentAttachmentReferenceRules.StatusVoided, stored.Status);
        Assert.NotNull(stored.VoidedAt);
        Assert.True(stored.VoidReason == reasonA || stored.VoidReason == reasonB);
        // 原始留痕不可变（来源授权确认 / 备注 / 匿名快照均保留）
        Assert.True(stored.SourceAuthorizationAcknowledged);
        Assert.Equal("ERP-412 集成测试来源授权留痕", stored.SourceAuthorizationNote);

        // 显式作废后同一身份可重新登记（新身份）；历史仍不可变
        var reCreate = await TryCreateAsync(userId, DarDto(orderId, identity));
        Assert.True(reCreate.Success);
        var active = await ReloadReferenceForParentAsync(orderId);
        Assert.NotEqual(referenceId, active.Id);

        Assert.Equal(2, await CountReferenceByIdentityAsync(orderId, identity));
        Assert.Equal(1, await CountReferencesAsync(orderId, DocumentAttachmentReferenceRules.StatusActive));

        var original = await ReloadReferenceAsync(referenceId);
        Assert.Equal(DocumentAttachmentReferenceRules.StatusVoided, original.Status);
        Assert.Equal(stored.VoidReason, original.VoidReason);
        Assert.Equal(stored.VoidedAt, original.VoidedAt);
    }

    // ==================== 4. 确定性顺序与零写入拒绝 ====================

    [Fact]
    public async Task 确定性顺序_父单据已删除或归属越界后登记一律fail_closed零写入()
    {
        Guard();
        var tag = Tag();
        long userId, ownOrderId, deletedOrderId, foreignOrderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, _) = await SeedSalesmanAsync(seed, DocumentAttachmentReferenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            var employee = await seed.BaseEmployees.FirstAsync(e => e.EmployeeCode == user.UserName);
            var customerId = (await SeedCustomerAsync(seed, $"INT_E412_D_{tag}", employee.Id)).Id;
            var foreignCustomerId = (await SeedCustomerAsync(seed, $"INT_E412_DX_{tag}", null)).Id;
            ownOrderId = (await SeedSalesOrderAsync(seed, $"SO-E412-D1-{tag}", customerId)).Id;
            deletedOrderId = (await SeedSalesOrderAsync(seed, $"SO-E412-D2-{tag}", customerId)).Id;
            foreignOrderId = (await SeedSalesOrderAsync(seed, $"SO-E412-D3-{tag}", foreignCustomerId)).Id;
        }

        var untouchedBefore = await CountUntouchedAsync();
        await SoftDeleteOrderAsync(deletedOrderId);

        var onDeleted = await TryCreateAsync(userId, DarDto(deletedOrderId, $"int-e412-d1-{tag}"));
        Assert.False(onDeleted.Success);
        Assert.Equal(ErrorCodes.NotFound, onDeleted.Code);
        Assert.Contains("不存在或已删除", onDeleted.Error, StringComparison.Ordinal);

        var onForeign = await TryCreateAsync(userId, DarDto(foreignOrderId, $"int-e412-d2-{tag}"));
        Assert.False(onForeign.Success);
        Assert.Equal(ErrorCodes.Forbidden, onForeign.Code);

        // 本人订单仍可用（授权未被误伤），且零部分写入
        var onOwn = await TryCreateAsync(userId, DarDto(ownOrderId, $"int-e412-d3-{tag}"));
        Assert.True(onOwn.Success);

        Assert.Equal(0, await CountReferencesAsync(deletedOrderId));
        Assert.Equal(0, await CountReferencesAsync(foreignOrderId));
        Assert.Equal(1, await CountReferencesAsync(ownOrderId, DocumentAttachmentReferenceRules.StatusActive));

        // 父单据商业字段不变（软删除只改标记）；库存 / 财务零写入
        var own = await ReadParentAsync(ownOrderId);
        Assert.Equal(DocumentStatus.Approved, own.Status);
        Assert.Equal(2468.00m, own.TotalAmount);
        Assert.False(own.Deleted);
        Assert.Equal(untouchedBefore, await CountUntouchedAsync());
    }

    [Fact]
    public async Task 提交前菜单撤销_旧授权快照被实时重解析拒绝_零落库()
    {
        Guard();
        var tag = Tag();
        long userId, roleId, orderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, role) = await SeedSalesmanAsync(seed, DocumentAttachmentReferenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            roleId = role.Id;
            var employee = await seed.BaseEmployees.FirstAsync(e => e.EmployeeCode == user.UserName);
            var customerId = (await SeedCustomerAsync(seed, $"INT_E412_E_{tag}", employee.Id)).Id;
            orderId = (await SeedSalesOrderAsync(seed, $"SO-E412-E-{tag}", customerId)).Id;
        }

        await using (var revoked = _fixture.CreateDbContext())
            await RevokeMenusAsync(revoked, roleId);

        var result = await TryCreateAsync(userId, DarDto(orderId, $"int-e412-e-{tag}"));
        Assert.False(result.Success);
        Assert.Equal(0, await CountReferencesAsync(orderId));
    }

    // ==================== 5. 元数据写入失败与零部分写入 ====================

    /// <summary>真实 SQL 上的失败注入上下文：仅在插入新附件引用时抛异常（父单据行锁保存照常成功）。</summary>
    private sealed class FailingReferenceInsertDbContext : ErpDbContext
    {
        public FailingReferenceInsertDbContext(DbContextOptions<ErpDbContext> options) : base(options)
        {
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (ChangeTracker.Entries<DocumentAttachmentReference>().Any(e => e.State == EntityState.Added))
                throw new InvalidOperationException("ERP-412 注入的元数据写入失败（应确认回滚且零部分写入）");

            return base.SaveChangesAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task 登记_元数据写入失败_完整回滚零部分写入_父单据与库存财务不变()
    {
        Guard();
        var tag = Tag();
        long userId, orderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, _) = await SeedSalesmanAsync(seed, DocumentAttachmentReferenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            var employee = await seed.BaseEmployees.FirstAsync(e => e.EmployeeCode == user.UserName);
            var customerId = (await SeedCustomerAsync(seed, $"INT_E412_F_{tag}", employee.Id)).Id;
            orderId = (await SeedSalesOrderAsync(seed, $"SO-E412-F-{tag}", customerId)).Id;
        }

        var parentBefore = await ReadParentAsync(orderId);
        var untouchedBefore = await CountUntouchedAsync();
        var rowsBefore = await CountReferencesAsync(orderId);

        await using (var db = new FailingReferenceInsertDbContext(_fixture.Options()))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                NewController(db, userId).Create(DarDto(orderId, $"int-e412-f-{tag}")));
            Assert.DoesNotContain("SqlException", ex.Message, StringComparison.Ordinal);
        }

        // 完整回滚：零部分写入 + 父单据商业字段（含加锁只刷新 UpdatedAt 的部分）全部回滚
        Assert.Equal(rowsBefore, await CountReferencesAsync(orderId));

        var parentAfter = await ReadParentAsync(orderId);
        Assert.Equal(parentBefore.TotalAmount, parentAfter.TotalAmount);
        Assert.Equal(parentBefore.DepositAmount, parentAfter.DepositAmount);
        Assert.Equal(parentBefore.CustomerId, parentAfter.CustomerId);
        Assert.Equal(parentBefore.Status, parentAfter.Status);
        Assert.Equal(parentBefore.Deleted, parentAfter.Deleted);

        Assert.Equal(untouchedBefore, await CountUntouchedAsync());
    }
}

/// <summary>
/// ERP-412 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class DocumentAttachmentReferenceMutationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_DAR_REF_MUTATION_20261009";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-412] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await CreateFreshDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    internal DbContextOptions<ErpDbContext> Options() => BuildOptions();

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};" +
           "Integrated Security=true;TrustServerCertificate=true;";

    /// <summary>专用目标护栏：实例名 / 库名前缀 / 集成安全三项必须精确匹配，否则在任何库访问之前拒绝。</summary>
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

        Console.WriteLine("[ERP-412] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }
}

/// <summary>ERP-412 专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class DocumentAttachmentReferenceMutationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => DocumentAttachmentReferenceMutationSqlServerFixture.AssertDedicatedTarget(connection));
}

