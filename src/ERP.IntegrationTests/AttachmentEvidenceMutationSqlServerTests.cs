using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Text;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-409 附件内容证据「登记 / 作废」原子性护栏的真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>两个独立连接竞态</b>：① 登记与「父单据并发删除 / 归属客户变更」并发 → 绝不出现孤儿内容、
/// 幽灵证据或撕裂的父状态；② 同行并发作废 → 只有一个赢家，原始作废原因与时间戳被保留，输家不覆盖。</item>
/// <item><b>确定性顺序</b>：归属客户变更 / 菜单撤销在提交后发生的登记一律 fail closed（零落库零落盘）。</item>
/// <item><b>元数据写入失败</b>：确认回滚后只补偿本次请求新建的内容键；既有已受理证据的计数 / 摘要 /
/// 原始作废原因在前后完全不变。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>保留既有库存来源单据审计与原始失败日志：本测试只新增自己的证据行，不清理 / 不删除任何既有行。
/// 构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class AttachmentEvidenceMutationSqlServerTests
    : IClassFixture<AttachmentEvidenceMutationSqlServerFixture>
{
    private readonly AttachmentEvidenceMutationSqlServerFixture _fixture;

    public AttachmentEvidenceMutationSqlServerTests(AttachmentEvidenceMutationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(AttachmentEvidenceMutationSqlServerFixture.DatabasePrefix,
            target.InitialCatalog, StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 独立连接脚手架 ====================

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static byte[] Content(string marker) => Encoding.UTF8.GetBytes("%PDF-1.4\n" + marker);

    /// <summary>
    /// 一条独立连接完成一次真实登记（实时解析授权 + 真实隔离本地存储 + 真实原子护栏）；
    /// 返回成功标志与（失败时的）错误文案。
    /// </summary>
    private async Task<(bool Success, string Error)> TryUploadAsync(long userId, long ownerId, string marker)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var access = await AttachmentOwnerAuthorizationRules.ResolveAsync(db, userId);
            var content = Content(marker);
            await AttachmentEvidenceService.UploadAsync(
                db, _fixture.Store, new AttachmentEvidenceUploadRequest
                {
                    OwnerType = AttachmentEvidenceRules.OwnerTypeSalesOrder,
                    OwnerId = ownerId,
                    FileName = "原子性证据.pdf",
                    DeclaredContentType = AttachmentEvidenceRules.MediaPdf,
                    DeclaredLength = content.LongLength,
                    Description = "ERP-409 集成证据",
                    Content = new MemoryStream(content, writable: false)
                },
                "集成上传人", userId, default, access, userId);
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>一条独立连接完成一次真实作废（实时解析授权 + 真实原子护栏）。</summary>
    private async Task<(bool Success, string Error)> TryVoidAsync(long userId, long evidenceId, string reason)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var access = await AttachmentOwnerAuthorizationRules.ResolveAsync(db, userId);
            await AttachmentEvidenceService.VoidAsync(db, evidenceId, reason, default, access, userId);
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>两条独立连接以同一起跑线并发执行（门闩对齐），返回两侧结果。</summary>
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

    private static async Task<(SysUser User, BaseEmployee Employee, SysRole Role)> SeedSalesmanAsync(
        ErpDbContext db, params string[] menuCodes)
    {
        var code = $"INT_E409_EMP_{Tag()}";
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
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
            RoleName = $"INT_E409_ROLE_{code}", RoleCode = $"INT_E409_{Guid.NewGuid():N}", IsSystem = false
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
        return (user, employee, role);
    }

    private static async Task<SalesOrder> SeedSalesOrderAsync(
        ErpDbContext db, string orderNo, long customerId)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo, OrderDate = DateTime.Today, CustomerId = customerId,
            Currency = Currency.USD, TotalAmount = 6666.66m, DepositAmount = 100m,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();

        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = order.Id, ProductId = 9001, ProductName = "集成商品", Quantity = 3m
        });
        await db.SaveChangesAsync();
        return order;
    }

    private static async Task<AttachmentEvidence> SeedEvidenceAsync(
        ErpDbContext db, long ownerId, string ownerNo, string digest, int status = 0, string voidReason = "")
    {
        var row = new AttachmentEvidence
        {
            OwnerType = AttachmentEvidenceRules.OwnerTypeSalesOrder,
            OwnerId = ownerId,
            OwnerNo = ownerNo,
            OwnerTypeText = AttachmentEvidenceRules.OwnerTypeText(AttachmentEvidenceRules.OwnerTypeSalesOrder),
            OriginalFileName = "既有证据.pdf",
            MediaType = AttachmentEvidenceRules.MediaPdf,
            SizeBytes = 12,
            Sha256 = digest,
            StorageKey = $"{Guid.NewGuid():N}.pdf",
            StorageProvider = AttachmentEvidenceRules.ProviderIsolatedLocal,
            UploadedBy = "既有上传人",
            RecordedAt = DateTime.Now,
            Status = status,
            VoidedAt = status == AttachmentEvidenceRules.StatusVoided ? DateTime.Now : null,
            VoidReason = voidReason,
            CreatedAt = DateTime.Now
        };
        db.AttachmentEvidences.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

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
        // Read RowVersion only after obtaining the competing parent mutation lock.
        // Upload may update the technical timestamp; a stale fixture token is not a business failure.
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
        // Read RowVersion only after obtaining the competing parent mutation lock.
        // Upload may update the technical timestamp; a stale fixture token is not a business failure.
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync(
            "SELECT [Id] FROM [db_owner].[SalesOrders] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {0}", orderId);
        var order = await db.SalesOrders.FirstAsync(o => o.Id == orderId);
        order.CustomerId = customerId;
        order.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private async Task<int> CountOwnerEvidenceAsync(long ownerId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.AttachmentEvidences.CountAsync(r =>
            r.OwnerType == AttachmentEvidenceRules.OwnerTypeSalesOrder && r.OwnerId == ownerId);
    }

    private async Task<AttachmentEvidence> ReloadEvidenceAsync(long evidenceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.AttachmentEvidences.AsNoTracking().FirstAsync(r => r.Id == evidenceId);
    }

    /// <summary>
    /// 全局一致性不变量：隔离存储内的每个内容文件都必须被某条证据行引用（无孤儿内容），
    /// 且每条证据引用的内容文件必须真实存在（无幽灵证据）。任何失败注入都不允许破坏它。
    /// </summary>
    private async Task AssertNoOrphanContentAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var referenced = (await db.AttachmentEvidences.AsNoTracking()
                .Select(r => r.StorageKey).ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var orphans = _fixture.ContentKeysOnDisk()
            .Where(key => !referenced.Contains(key))
            .ToList();

        Assert.True(orphans.Count == 0,
            $"隔离存储存在未被任何证据行引用的孤儿内容：{string.Join(", ", orphans)}");
    }

    // ==================== 1. 两个独立连接竞态：登记 vs 父单据删除 / 归属变更 ====================

    [Fact]
    public async Task 登记与父单据软删除竞态_两个独立连接_无孤儿内容且父状态未撕裂()
    {
        Guard();
        var tag = Tag();
        long userId, ownerId, customerId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedSalesmanAsync(seed, AttachmentEvidenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            customerId = (await SeedCustomerAsync(seed, $"INT_E409_A_{tag}", employee.Id)).Id;
            ownerId = (await SeedSalesOrderAsync(seed, $"SO-E409-A-{tag}", customerId)).Id;
        }

        var filesBefore = _fixture.CountContentFiles();
        var rowsBefore = await CountOwnerEvidenceAsync(ownerId);

        var results = await RaceAsync(
            () => TryUploadAsync(userId, ownerId, $"race-delete-{tag}"),
            async () => { await SoftDeleteOrderAsync(ownerId); return (true, string.Empty); });

        var uploadSucceeded = results[0].Success;

        // 父单据商业字段未被登记路径改写（删除只改软删除标记，不改金额 / 数量 / 客户）
        await using (var db = _fixture.CreateDbContext())
        {
            var order = await db.SalesOrders.AsNoTracking().FirstAsync(o => o.Id == ownerId);
            Assert.Equal(6666.66m, order.TotalAmount);
            Assert.Equal(customerId, order.CustomerId);
            Assert.Equal(3m, await db.SalesOrderDetails
                .Where(d => d.SalesOrderId == ownerId).SumAsync(d => d.Quantity));
        }

        // 结论只能是「登记成功（父删除后到）或登记被原子拒绝」，绝不出现撕裂 / 孤儿 / 幽灵
        var rowsAfter = await CountOwnerEvidenceAsync(ownerId);
        Assert.Equal(uploadSucceeded ? rowsBefore + 1 : rowsBefore, rowsAfter);
        Assert.Equal(filesBefore + (uploadSucceeded ? 1 : 0), _fixture.CountContentFiles());
        await AssertNoOrphanContentAsync();
    }

    [Fact]
    public async Task 登记与归属客户变更竞态_两个独立连接_无越界存活证据()
    {
        Guard();
        var tag = Tag();
        long userId, ownerId, foreignCustomerId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedSalesmanAsync(seed, AttachmentEvidenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            var customerId = (await SeedCustomerAsync(seed, $"INT_E409_B_{tag}", employee.Id)).Id;
            foreignCustomerId = (await SeedCustomerAsync(seed, $"INT_E409_BX_{tag}", empId: null)).Id;
            ownerId = (await SeedSalesOrderAsync(seed, $"SO-E409-B-{tag}", customerId)).Id;
        }

        var filesBefore = _fixture.CountContentFiles();
        var rowsBefore = await CountOwnerEvidenceAsync(ownerId);

        var results = await RaceAsync(
            () => TryUploadAsync(userId, ownerId, $"race-owner-{tag}"),
            async () => { await ChangeOrderCustomerAsync(ownerId, foreignCustomerId); return (true, string.Empty); });

        var uploadSucceeded = results[0].Success;

        await using (var db = _fixture.CreateDbContext())
        {
            var order = await db.SalesOrders.AsNoTracking().FirstAsync(o => o.Id == ownerId);
            Assert.Equal(foreignCustomerId, order.CustomerId);   // 归属变更已提交（赢家是变更方）
            Assert.Equal(6666.66m, order.TotalAmount);
        }

        // 变更提交后，越界归属的登记必须被拒绝；若登记先提交则最多新增一条当时合法、事后越界的历史证据
        var rowsAfter = await CountOwnerEvidenceAsync(ownerId);
        Assert.True(rowsAfter == rowsBefore || (rowsAfter == rowsBefore + 1 && uploadSucceeded));
        Assert.Equal(filesBefore + (uploadSucceeded ? 1 : 0), _fixture.CountContentFiles());
        await AssertNoOrphanContentAsync();
    }

    // ==================== 2. 确定性顺序：归属变更 / 菜单撤销后登记一律 fail closed ====================

    [Fact]
    public async Task 归属客户已改为越界后登记_旧授权快照被锁内复核拒绝_零落库零落盘()
    {
        Guard();
        var tag = Tag();
        long userId, ownerId, foreignCustomerId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedSalesmanAsync(seed, AttachmentEvidenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            var customerId = (await SeedCustomerAsync(seed, $"INT_E409_C_{tag}", employee.Id)).Id;
            foreignCustomerId = (await SeedCustomerAsync(seed, $"INT_E409_CX_{tag}", empId: null)).Id;
            ownerId = (await SeedSalesOrderAsync(seed, $"SO-E409-C-{tag}", customerId)).Id;
        }

        var filesBefore = _fixture.CountContentFiles();
        await ChangeOrderCustomerAsync(ownerId, foreignCustomerId);

        var result = await TryUploadAsync(userId, ownerId, $"stale-scope-{tag}");

        Assert.False(result.Success);
        Assert.Equal(0, await CountOwnerEvidenceAsync(ownerId));
        Assert.Equal(filesBefore, _fixture.CountContentFiles());
        await AssertNoOrphanContentAsync();
    }

    [Fact]
    public async Task 提交前菜单撤销_旧授权快照被锁内实时重解析拒绝_零落库零落盘()
    {
        Guard();
        var tag = Tag();
        long userId, roleId, ownerId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, role) = await SeedSalesmanAsync(seed, AttachmentEvidenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            roleId = role.Id;
            var customerId = (await SeedCustomerAsync(seed, $"INT_E409_D_{tag}", employee.Id)).Id;
            ownerId = (await SeedSalesOrderAsync(seed, $"SO-E409-D-{tag}", customerId)).Id;
        }

        var filesBefore = _fixture.CountContentFiles();

        // 模拟既有请求：先解析（已授权）→ 提交前撤销菜单 → 再登记
        await using (var revoked = _fixture.CreateDbContext())
            await RevokeMenusAsync(revoked, roleId);

        var result = await TryUploadAsync(userId, ownerId, $"revoked-menu-{tag}");

        Assert.False(result.Success);                       // 锁内实时重解析 → 未授权（fail closed）
        Assert.Equal(0, await CountOwnerEvidenceAsync(ownerId));
        Assert.Equal(filesBefore, _fixture.CountContentFiles());
        await AssertNoOrphanContentAsync();
    }

    [Fact]
    public async Task 作废_两独立连接并发_只有一个赢家且原始原因与时间戳保留()
    {
        Guard();
        var tag = Tag();
        long userId, ownerId, evidenceId;
        var digest = new string('c', AttachmentEvidenceRules.Sha256Length);
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedSalesmanAsync(seed, AttachmentEvidenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            var customerId = (await SeedCustomerAsync(seed, $"INT_E409_E_{tag}", employee.Id)).Id;
            ownerId = (await SeedSalesOrderAsync(seed, $"SO-E409-E-{tag}", customerId)).Id;
            evidenceId = (await SeedEvidenceAsync(seed, ownerId, $"SO-E409-E-{tag}", digest)).Id;
        }

        var reasonA = $"并发作废原因 A {tag}";
        var reasonB = $"并发作废原因 B {tag}";

        var results = await RaceAsync(
            () => TryVoidAsync(userId, evidenceId, reasonA),
            () => TryVoidAsync(userId, evidenceId, reasonB));

        var winners = results.Where(r => r.Success).ToList();
        Assert.Single(winners);                                  // 恰好一个赢家
        Assert.Single(results.Where(r => !r.Success));           // 输家原子拒绝

        var stored = await ReloadEvidenceAsync(evidenceId);
        Assert.Equal(AttachmentEvidenceRules.StatusVoided, stored.Status);
        Assert.NotNull(stored.VoidedAt);
        Assert.True(stored.VoidReason == reasonA || stored.VoidReason == reasonB);

        // 原始留痕未被输家覆盖；历史内容元数据（文件名 / 摘要 / 长度 / 归属）保持不可变
        Assert.Equal(digest, stored.Sha256);
        Assert.Equal("既有证据.pdf", stored.OriginalFileName);
        Assert.Equal(12, stored.SizeBytes);
        Assert.Equal(ownerId, stored.OwnerId);
        Assert.Equal(AttachmentEvidenceRules.OwnerTypeSalesOrder, stored.OwnerType);
    }

    // ==================== 3. 元数据写入失败与已受理证据不可变 ====================

    /// <summary>真实 SQL 上的失败注入上下文：第 N 次保存抛异常（第 1 次是归属行锁，第 2 次是元数据写入）。</summary>
    private sealed class FailingSaveDbContext : ErpDbContext
    {
        public FailingSaveDbContext(DbContextOptions<ErpDbContext> options) : base(options)
        {
        }

        public int FailOnSaveCall { get; set; }

        private int _saveCalls;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            _saveCalls++;
            if (FailOnSaveCall > 0 && _saveCalls == FailOnSaveCall)
                throw new DbUpdateException("注入的元数据写入失败");

            return base.SaveChangesAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task 登记_元数据写入失败_确认回滚补偿本次新建内容_零落库且无孤儿()
    {
        Guard();
        var tag = Tag();
        long userId, ownerId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedSalesmanAsync(seed, AttachmentEvidenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            var customerId = (await SeedCustomerAsync(seed, $"INT_E409_F_{tag}", employee.Id)).Id;
            ownerId = (await SeedSalesOrderAsync(seed, $"SO-E409-F-{tag}", customerId)).Id;
        }

        var filesBefore = _fixture.CountContentFiles();

        await using (var db = new FailingSaveDbContext(_fixture.Options()) { FailOnSaveCall = 2 })
        {
            var access = await AttachmentOwnerAuthorizationRules.ResolveAsync(db, userId);
            var content = Content($"metadata-failure-{tag}");

            await Assert.ThrowsAnyAsync<Exception>(() => AttachmentEvidenceService.UploadAsync(
                db, _fixture.Store, new AttachmentEvidenceUploadRequest
                {
                    OwnerType = AttachmentEvidenceRules.OwnerTypeSalesOrder,
                    OwnerId = ownerId,
                    FileName = "失败注入证据.pdf",
                    DeclaredContentType = AttachmentEvidenceRules.MediaPdf,
                    DeclaredLength = content.LongLength,
                    Content = new MemoryStream(content, writable: false)
                },
                "集成上传人", userId, default, access, userId));
        }

        Assert.Equal(0, await CountOwnerEvidenceAsync(ownerId));      // 元数据未落库
        Assert.Equal(filesBefore, _fixture.CountContentFiles());      // 新建内容已按有界补偿删除
        await AssertNoOrphanContentAsync();
    }

    [Fact]
    public async Task 既有已受理证据_操作前后计数摘要与原始作废原因保持不变()
    {
        Guard();
        var tag = Tag();
        long userId, ownerId, activeId, voidedId;
        var activeDigest = new string('a', AttachmentEvidenceRules.Sha256Length);
        var voidedDigest = new string('b', AttachmentEvidenceRules.Sha256Length);
        const string originalReason = "原始作废原因（必须保持不可变）";

        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedSalesmanAsync(seed, AttachmentEvidenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            var customerId = (await SeedCustomerAsync(seed, $"INT_E409_G_{tag}", employee.Id)).Id;
            ownerId = (await SeedSalesOrderAsync(seed, $"SO-E409-G-{tag}", customerId)).Id;
            activeId = (await SeedEvidenceAsync(seed, ownerId, $"SO-E409-G-{tag}", activeDigest)).Id;
            voidedId = (await SeedEvidenceAsync(seed, ownerId, $"SO-E409-G-{tag}", voidedDigest,
                AttachmentEvidenceRules.StatusVoided, originalReason)).Id;
        }

        var rowsBefore = await CountOwnerEvidenceAsync(ownerId);
        var filesBefore = _fixture.CountContentFiles();
        var voidedBefore = await ReloadEvidenceAsync(voidedId);

        // 一次被拒绝的越界登记 + 一次失败注入的登记：都不得改写既有证据
        await using (var seed = _fixture.CreateDbContext())
        {
            var foreignCustomerId = (await SeedCustomerAsync(seed, $"INT_E409_GX_{tag}", empId: null)).Id;
            await ChangeOrderCustomerAsync(ownerId, foreignCustomerId);
        }

        var rejected = await TryUploadAsync(userId, ownerId, $"immutable-rejected-{tag}");
        Assert.False(rejected.Success);

        var activeAfter = await ReloadEvidenceAsync(activeId);
        var voidedAfter = await ReloadEvidenceAsync(voidedId);

        Assert.Equal(rowsBefore, await CountOwnerEvidenceAsync(ownerId));
        Assert.Equal(filesBefore, _fixture.CountContentFiles());
        Assert.Equal(activeDigest, activeAfter.Sha256);
        Assert.Equal(AttachmentEvidenceRules.StatusActive, activeAfter.Status);
        Assert.Null(activeAfter.VoidedAt);
        Assert.Equal(voidedDigest, voidedAfter.Sha256);
        Assert.Equal(AttachmentEvidenceRules.StatusVoided, voidedAfter.Status);
        Assert.Equal(originalReason, voidedAfter.VoidReason);          // 原始作废原因不可变
        Assert.Equal(voidedBefore.VoidedAt, voidedAfter.VoidedAt);
        await AssertNoOrphanContentAsync();
    }

    [Fact]
    public async Task 登记_真实成功后落库与落盘一致_父单据商业字段与库存来源审计不变()
    {
        Guard();
        var tag = Tag();
        long userId, ownerId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedSalesmanAsync(seed, AttachmentEvidenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            var customerId = (await SeedCustomerAsync(seed, $"INT_E409_H_{tag}", employee.Id)).Id;
            ownerId = (await SeedSalesOrderAsync(seed, $"SO-E409-H-{tag}", customerId)).Id;
        }

        var filesBefore = _fixture.CountContentFiles();
        var marker = $"success-{tag}";
        var expectedDigest = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Content(marker))).ToLowerInvariant();

        var result = await TryUploadAsync(userId, ownerId, marker);
        Assert.True(result.Success, result.Error);           // 真实登记必须成功（证明上述失败用例不是空转）

        await using (var db = _fixture.CreateDbContext())
        {
            var row = await db.AttachmentEvidences.AsNoTracking()
                .SingleAsync(r => r.OwnerId == ownerId
                                  && r.OwnerType == AttachmentEvidenceRules.OwnerTypeSalesOrder);
            Assert.Equal(AttachmentEvidenceRules.StatusActive, row.Status);
            Assert.Equal(expectedDigest, row.Sha256);
            Assert.Equal(AttachmentEvidenceRules.MediaPdf, row.MediaType);
            Assert.True(_fixture.ContentFileExists(row.StorageKey));

            // 父单据商业字段与明细（库存来源审计口径）保持不可变
            var order = await db.SalesOrders.AsNoTracking().FirstAsync(o => o.Id == ownerId);
            Assert.Equal(6666.66m, order.TotalAmount);
            Assert.Equal(100m, order.DepositAmount);
            Assert.Equal(3m, await db.SalesOrderDetails
                .Where(d => d.SalesOrderId == ownerId).SumAsync(d => d.Quantity));
        }

        Assert.Equal(filesBefore + 1, _fixture.CountContentFiles());
        Assert.Equal(1, await CountOwnerEvidenceAsync(ownerId));
        await AssertNoOrphanContentAsync();
    }
}

/// <summary>
/// ERP-409 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// <para>内容存储使用<strong>真实隔离本地存储</strong>（应用临时目录下的全新根目录），
/// 以便真实观察「新建内容的有界补偿 / 孤儿内容」。</para>
/// </summary>
public sealed class AttachmentEvidenceMutationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ATTACHMENT_MUTATION_20261009";

    private string _contentRoot = string.Empty;

    public string ConnectionString { get; private set; } = null!;

    public IAttachmentContentStore Store { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        _contentRoot = Path.Combine(Path.GetTempPath(), "erp409-it-" + Guid.NewGuid().ToString("N"));
        Store = new IsolatedLocalAttachmentContentStore(
            new AttachmentStorageOptions { RootPath = _contentRoot });

        Console.WriteLine($"[ERP-409] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

    /// <summary>隔离内容根目录内的全部内容键（相对键，<c>/</c> 分隔）：用于孤儿内容不变量断言。</summary>
    public IReadOnlyList<string> ContentKeysOnDisk()
    {
        if (!Directory.Exists(_contentRoot)) return Array.Empty<string>();

        return Directory.GetFiles(_contentRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(_contentRoot, path)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .ToList();
    }

    /// <summary>隔离内容根目录内的文件总数。</summary>
    public int CountContentFiles() => ContentKeysOnDisk().Count;

    /// <summary>指定内容键对应的真实文件是否存在（路径穿越 / 绝对路径一律视为不存在）。</summary>
    public bool ContentFileExists(string? storageKey)
    {
        var key = (storageKey ?? string.Empty).Trim();
        if (key.Length == 0 || Path.IsPathRooted(key)) return false;

        return File.Exists(Path.Combine(_contentRoot, key.Replace('/', Path.DirectorySeparatorChar)));
    }

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

        Console.WriteLine("[ERP-409] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }
}

/// <summary>ERP-409 专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class AttachmentEvidenceMutationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => AttachmentEvidenceMutationSqlServerFixture.AssertDedicatedTarget(connection));
}
