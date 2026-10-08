using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 附件内容证据「登记 / 作废」原子性与内容补偿单元测试（ERP-409）。
/// 覆盖：登记在原子事务 + 归属（父）行锁内重新读取实时父状态与权威归属、父单据已删除即零落库零落盘、
/// 加锁不改写父单据商业字段、元数据写入失败在**确认回滚**后只对**本次请求新建**的键做有界补偿、
/// 补偿失败 / 提交不确定（取消）时保留内容与可恢复内部证据且用户文案不含存储键与路径、
/// 作废的重复 / 并发语义（不覆盖原始原因与时间戳）、确定性锁序，以及隔离本地存储有界补偿键的安全口径
/// （不透明键形态校验 / 拒绝穿越与绝对路径 / 只删单文件不做目录扫荡 / 越界文件不动）。
/// <para>全部使用内存库（<c>TestDbFactory</c> 与测试内失败上下文）与测试内内容存储；隔离本地存储只在
/// 系统临时目录验证。不连接 SQL Server、不访问生产 OSS、不做浏览器 / UI 验收（真实 SQL 竞态见
/// <c>ERP.IntegrationTests.AttachmentEvidenceMutationSqlServerTests</c>）。</para>
/// </summary>
public class AttachmentEvidenceMutationTests
{
    // ==================== 0. 脚手架 ====================

    private static byte[] PdfBytes(string body = "erp409")
        => Encoding.UTF8.GetBytes("%PDF-1.4\n" + body);

    private static SalesOrder SeedSalesOrder(ErpDbContext db, string orderNo, long customerId = 501)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 1),
            CustomerId = customerId,
            Currency = Currency.USD,
            TotalAmount = 8888.88m,
            DepositAmount = 1000m,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = order.Id,
            ProductId = 9001,
            ProductName = "原子性测试商品",
            Quantity = 7m
        });
        db.SaveChanges();
        return order;
    }

    private static AttachmentOwnerAccessContext SalesOrderAccess(long customerId, params string[] ownerTypes)
        => new()
        {
            UserId = 409,
            UserName = "erp409-salesman",
            Scope = new SalespersonDataScope
            {
                IsPrivileged = false,
                SalesmanId = 1,
                AllowedCustomerIds = new HashSet<long> { customerId }
            },
            AuthorizedOwnerTypes = ownerTypes.Length == 0
                ? new List<string> { AttachmentEvidenceRules.OwnerTypeSalesOrder }
                : ownerTypes.ToList()
        };

    private static AttachmentEvidenceUploadRequest UploadRequest(byte[] content, long ownerId)
        => new()
        {
            OwnerType = AttachmentEvidenceRules.OwnerTypeSalesOrder,
            OwnerId = ownerId,
            FileName = "原子性证据.pdf",
            DeclaredContentType = AttachmentEvidenceRules.MediaPdf,
            DeclaredLength = content.LongLength,
            Content = new MemoryStream(content, writable: false)
        };

    private static async Task<BusinessException> AssertBusinessAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    /// <summary>内存库选项（失败上下文复用；忽略内存库的事务警告）。</summary>
    private static DbContextOptions<ErpDbContext> InMemoryOptions()
        => new DbContextOptionsBuilder<ErpDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    /// <summary>内存库上下文，可在下一次保存时注入异常（模拟元数据写入失败 / 取消）。</summary>
    private sealed class FailingSaveDbContext : ErpDbContext
    {
        public FailingSaveDbContext(DbContextOptions<ErpDbContext> options) : base(options)
        {
        }

        /// <summary>下一次 <c>SaveChangesAsync</c> 抛出的异常（触发后自动清零）；null = 正常保存。</summary>
        public Exception? NextSaveException { get; set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var pending = NextSaveException;
            if (pending is null) return base.SaveChangesAsync(cancellationToken);

            NextSaveException = null;
            throw pending;
        }
    }

    /// <summary>
    /// 测试内内容存储：实现唯一内容接缝，并额外实现 ERP-409 的有界补偿契约
    /// （记录被补偿删除的键；可注入「清理失败」）。
    /// </summary>
    private sealed class SpyContentStore : IAttachmentContentStore
    {
        public Dictionary<string, byte[]> Items { get; } = new();

        public List<string> RemovedKeys { get; } = new();

        public int SaveCalls { get; private set; }

        public bool FailRemoval { get; set; }

        public bool IsProductionProvider => false;

        public string ProviderCode => AttachmentEvidenceRules.ProviderIsolatedLocal;

        public string ProviderText => AttachmentEvidenceRules.ProviderText(ProviderCode);

        public Task<string> SaveAsync(
            Stream content, string extension, CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            var key = $"{DateTime.UtcNow:yyyyMMdd}/{Guid.NewGuid():N}{extension}";
            Items[key] = buffer.ToArray();
            return Task.FromResult(key);
        }

        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream?>(Items.TryGetValue(storageKey, out var bytes)
                ? new MemoryStream(bytes, writable: false)
                : null);

        public Task<long?> GetLengthAsync(string storageKey, CancellationToken cancellationToken = default)
            => Task.FromResult<long?>(Items.TryGetValue(storageKey, out var bytes) ? bytes.LongLength : null);

        public Task<bool> TryRemoveRequestOwnedAsync(
            string? storageKey, CancellationToken cancellationToken = default)
        {
            if (FailRemoval) return Task.FromResult(false);
            var key = (storageKey ?? string.Empty).Trim();
            if (key.Length == 0 || !Items.ContainsKey(key)) return Task.FromResult(false);

            Items.Remove(key);
            RemovedKeys.Add(key);
            return Task.FromResult(true);
        }
    }


    // ==================== 1. 登记：锁内复核 + 原子元数据 ====================

    [Fact]
    public async Task 登记成功_使用本请求新建键且不改写父单据商业字段()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-409-A");
        var detailCountBefore = db.SalesOrderDetails.Count(d => d.SalesOrderId == order.Id);
        var store = new SpyContentStore();
        var content = PdfBytes();

        var saved = await AttachmentEvidenceService.UploadAsync(
            db, store, UploadRequest(content, order.Id), "上传人", 7, default,
            SalesOrderAccess(order.CustomerId));

        Assert.Equal(AttachmentEvidenceRules.StatusActive, saved.Status);
        Assert.Equal(order.Id, saved.OwnerId);
        Assert.Single(store.Items);
        Assert.Empty(store.RemovedKeys);

        var row = Assert.Single(db.AttachmentEvidences.ToList());
        Assert.Equal(row.StorageKey, store.Items.Keys.Single());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), row.Sha256);
        Assert.Equal(content.LongLength, row.SizeBytes);

        // 父单据商业字段与明细完全未被「加锁」改写
        var reloaded = db.SalesOrders.Single(o => o.Id == order.Id);
        Assert.Equal(8888.88m, reloaded.TotalAmount);
        Assert.Equal(1000m, reloaded.DepositAmount);
        Assert.Equal(order.CustomerId, reloaded.CustomerId);
        Assert.Equal(DocumentStatus.Approved, reloaded.Status);
        Assert.Equal(detailCountBefore, db.SalesOrderDetails.Count(d => d.SalesOrderId == order.Id));
    }

    [Fact]
    public async Task 登记_父单据已删除_锁内复核失败_零落库零落盘()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-409-DEL");
        var store = new SpyContentStore();
        var access = SalesOrderAccess(order.CustomerId);

        order.IsDeleted = true;
        db.SaveChanges();

        await AssertBusinessAsync(ErrorCodes.NotFound, () => AttachmentEvidenceService.UploadAsync(
            db, store, UploadRequest(PdfBytes(), order.Id), "上传人", 7, default, access));

        Assert.Equal(0, store.SaveCalls);
        Assert.Empty(store.Items);
        Assert.Empty(db.AttachmentEvidences);
    }

    [Fact]
    public async Task 登记_实时客户范围外_锁内复核失败_零落库零落盘()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-409-SCOPE", customerId: 501);
        var store = new SpyContentStore();
        var outsider = SalesOrderAccess(customerId: 999);

        await AssertBusinessAsync(ErrorCodes.Forbidden, () => AttachmentEvidenceService.UploadAsync(
            db, store, UploadRequest(PdfBytes(), order.Id), "上传人", 7, default, outsider));

        Assert.Equal(0, store.SaveCalls);
        Assert.Empty(db.AttachmentEvidences);
    }

    [Fact]
    public async Task 登记_元数据写入失败_确认回滚后只补偿本次新建键_零落库且无孤儿内容()
    {
        var options = InMemoryOptions();
        using var db = new FailingSaveDbContext(options);
        var order = SeedSalesOrder(db, "SO-409-FAIL");
        var store = new SpyContentStore();

        db.NextSaveException = new DbUpdateException("注入的元数据写入失败");

        await Assert.ThrowsAsync<DbUpdateException>(() => AttachmentEvidenceService.UploadAsync(
            db, store, UploadRequest(PdfBytes(), order.Id), "上传人", 7, default,
            SalesOrderAccess(order.CustomerId)));

        Assert.Empty(db.AttachmentEvidences);
        Assert.Empty(store.Items);                       // 新建内容已被有界补偿删除
        Assert.Single(store.RemovedKeys);
        Assert.Equal(1, store.SaveCalls);
    }

    [Fact]
    public async Task 登记_补偿失败_保留可恢复内部证据且用户文案不含存储键与路径()
    {
        var options = InMemoryOptions();
        using var db = new FailingSaveDbContext(options);
        var order = SeedSalesOrder(db, "SO-409-CLEANUP");
        var store = new SpyContentStore { FailRemoval = true };

        db.NextSaveException = new DbUpdateException("注入的元数据写入失败");

        var ex = await Assert.ThrowsAsync<AttachmentEvidenceIntegrityException>(
            () => AttachmentEvidenceService.UploadAsync(
                db, store, UploadRequest(PdfBytes(), order.Id), "上传人", 7, default,
                SalesOrderAccess(order.CustomerId)));

        var key = store.Items.Keys.Single();             // 清理失败：内容仍保留，可人工恢复
        Assert.Equal("confirmed-rollback-compensation-failed", ex.Stage);
        Assert.Contains(key, ex.InternalEvidence);
        Assert.Contains(order.Id.ToString(), ex.InternalEvidence);

        // 用户可见文案绝不出现存储键 / 路径形态
        Assert.DoesNotContain(key, ex.Message);
        Assert.DoesNotContain("/", ex.Message);
        Assert.DoesNotContain("\\", ex.Message);
        Assert.Empty(db.AttachmentEvidences);
    }

    [Fact]
    public async Task 登记_提交结果不确定_取消_保留可能已提交内容且保留内部证据()
    {
        var options = InMemoryOptions();
        using var db = new FailingSaveDbContext(options);
        var order = SeedSalesOrder(db, "SO-409-CANCEL");
        var store = new SpyContentStore();

        db.NextSaveException = new OperationCanceledException("注入的取消");

        var ex = await Assert.ThrowsAsync<AttachmentEvidenceIntegrityException>(
            () => AttachmentEvidenceService.UploadAsync(
                db, store, UploadRequest(PdfBytes(), order.Id), "上传人", 7, default,
                SalesOrderAccess(order.CustomerId)));

        var key = store.Items.Keys.Single();
        Assert.Equal("cancelled-retention", ex.Stage);
        Assert.Contains(key, ex.InternalEvidence);
        Assert.Single(store.Items);                      // 绝不删除可能已提交的内容
        Assert.Empty(store.RemovedKeys);
        Assert.Empty(db.AttachmentEvidences);
    }


    // ==================== 2. 作废：确定性行锁 + 不覆盖原始留痕 ====================

    private static async Task<AttachmentEvidence> SeedEvidenceAsync(ErpDbContext db, long ownerId)
        => await Task.Run(() =>
        {
            var row = new AttachmentEvidence
            {
                OwnerType = AttachmentEvidenceRules.OwnerTypeSalesOrder,
                OwnerId = ownerId,
                OwnerNo = "SO-409-VOID",
                OwnerTypeText = AttachmentEvidenceRules.OwnerTypeText(
                    AttachmentEvidenceRules.OwnerTypeSalesOrder),
                OriginalFileName = "作废证据.pdf",
                MediaType = AttachmentEvidenceRules.MediaPdf,
                SizeBytes = 12,
                Sha256 = new string('a', AttachmentEvidenceRules.Sha256Length),
                StorageKey = $"{DateTime.UtcNow:yyyyMMdd}/{Guid.NewGuid():N}.pdf",
                StorageProvider = AttachmentEvidenceRules.ProviderIsolatedLocal,
                UploadedBy = "上传人",
                RecordedAt = DateTime.Now,
                Status = AttachmentEvidenceRules.StatusActive,
                CreatedAt = DateTime.Now
            };
            db.AttachmentEvidences.Add(row);
            db.SaveChanges();
            return row;
        });

    [Fact]
    public async Task 作废_重复作废拒绝且绝不覆盖原始原因与时间戳()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-409-VOID-A");
        var row = await SeedEvidenceAsync(db, order.Id);
        var access = SalesOrderAccess(order.CustomerId);

        var first = await AttachmentEvidenceService.VoidAsync(
            db, row.Id, "第一份原始作废原因", default, access);
        Assert.Equal(AttachmentEvidenceRules.StatusVoided, first.Status);
        var originalVoidedAt = first.VoidedAt;
        var originalReason = first.VoidReason;

        await AssertBusinessAsync(ErrorCodes.Duplicate, () => AttachmentEvidenceService.VoidAsync(
            db, row.Id, "试图覆盖的第二份原因", default, access));

        var stored = db.AttachmentEvidences.AsNoTracking().Single(r => r.Id == row.Id);
        Assert.Equal(originalReason, stored.VoidReason);
        Assert.Equal(originalVoidedAt, stored.VoidedAt);
        Assert.Equal(AttachmentEvidenceRules.StatusVoided, stored.Status);
    }

    [Fact]
    public async Task 作废_不存在或已删除_拒绝且零改写()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-409-VOID-B");
        var row = await SeedEvidenceAsync(db, order.Id);
        var access = SalesOrderAccess(order.CustomerId);

        await AssertBusinessAsync(ErrorCodes.NotFound, () => AttachmentEvidenceService.VoidAsync(
            db, 999_999, "原因", default, access));

        // 重新从存储加载（护栏回滚会清空跟踪器），再软删除后复核
        db.AttachmentEvidences.Single(r => r.Id == row.Id).IsDeleted = true;
        db.SaveChanges();

        await AssertBusinessAsync(ErrorCodes.NotFound, () => AttachmentEvidenceService.VoidAsync(
            db, row.Id, "原因", default, access));

        var stored = db.AttachmentEvidences.AsNoTracking().Single(r => r.Id == row.Id);
        Assert.Equal(AttachmentEvidenceRules.StatusActive, stored.Status);
        Assert.Null(stored.VoidedAt);
        Assert.Equal(string.Empty, stored.VoidReason);
    }

    [Fact]
    public async Task 作废_归属范围外_拒绝且零改写()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-409-VOID-C", customerId: 501);
        var row = await SeedEvidenceAsync(db, order.Id);

        await AssertBusinessAsync(ErrorCodes.NotFound, () => AttachmentEvidenceService.VoidAsync(
            db, row.Id, "原因", default, SalesOrderAccess(customerId: 999)));

        var stored = db.AttachmentEvidences.AsNoTracking().Single(r => r.Id == row.Id);
        Assert.Equal(AttachmentEvidenceRules.StatusActive, stored.Status);
    }


    // ==================== 2.1 锁内实时权限重解析（撤销 / 禁用立即收敛） ====================

    private static long SeedEnabledUser(ErpDbContext db, string userName)
    {
        db.SysUsers.Add(new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = userName,
            Status = UserStatus.Enabled
        });
        db.SaveChanges();
        return db.SysUsers.Single(u => u.UserName == userName).Id;
    }

    [Fact]
    public async Task 登记_锁内按真实身份重解析权限_旧授权快照不能替代实时菜单()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-409-LIVE");
        var userId = SeedEnabledUser(db, "erp409-no-menu");
        var store = new SpyContentStore();

        // 调用方携带的旧快照声称已授权，但锁内按真实身份重解析得到「无任何菜单」→ fail closed
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => AttachmentEvidenceService.UploadAsync(
            db, store, UploadRequest(PdfBytes(), order.Id), "上传人", userId, default,
            SalesOrderAccess(order.CustomerId), userId));

        Assert.Equal(0, store.SaveCalls);
        Assert.Empty(store.Items);
        Assert.Empty(db.AttachmentEvidences);
    }

    [Fact]
    public async Task 作废_锁内按真实身份重解析权限_旧授权快照不能替代实时菜单()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-409-LIVE-V");
        var row = await SeedEvidenceAsync(db, order.Id);
        var userId = SeedEnabledUser(db, "erp409-no-menu-v");

        await AssertBusinessAsync(ErrorCodes.NotFound, () => AttachmentEvidenceService.VoidAsync(
            db, row.Id, "原因", default, SalesOrderAccess(order.CustomerId), userId));

        var stored = db.AttachmentEvidences.AsNoTracking().Single(r => r.Id == row.Id);
        Assert.Equal(AttachmentEvidenceRules.StatusActive, stored.Status);
        Assert.Null(stored.VoidedAt);
    }

    // ==================== 3. 护栏规则契约 ====================

    [Fact]
    public async Task 非关系型提供程序_锁等价无操作且不改写父单据()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-409-LOCK");
        var updatedAtBefore = order.UpdatedAt;

        Assert.False(AttachmentEvidenceMutationRules.IsRelationalProvider(db));
        Assert.True(await AttachmentEvidenceMutationRules.LockOwnerRowAsync(
            db, AttachmentEvidenceRules.OwnerTypeSalesOrder, order.Id));
        Assert.True(await AttachmentEvidenceMutationRules.LockEvidenceRowAsync(db, 12345));

        Assert.False(await AttachmentEvidenceMutationRules.LockOwnerRowAsync(
            db, AttachmentEvidenceRules.OwnerTypeSalesOrder, 0));
        Assert.False(await AttachmentEvidenceMutationRules.LockOwnerRowAsync(
            db, "UNKNOWN-TYPE", order.Id));
        Assert.False(await AttachmentEvidenceMutationRules.LockEvidenceRowAsync(db, 0));

        var reloaded = db.SalesOrders.AsNoTracking().Single(o => o.Id == order.Id);
        Assert.Equal(updatedAtBefore, reloaded.UpdatedAt);
        Assert.Equal(8888.88m, reloaded.TotalAmount);
    }

    [Fact]
    public void 确定性锁序_合并Id去重升序且丢弃非正整数()
    {
        var merged = AttachmentEvidenceMutationRules.MergeLockIds(new long[] { 5, 0, -3, 2, 5, 9, 2 });
        Assert.Equal(new long[] { 2, 5, 9 }, merged.ToArray());

        Assert.Empty(AttachmentEvidenceMutationRules.MergeLockIds(null));
        Assert.Empty(AttachmentEvidenceMutationRules.MergeLockIds(new long[] { 0, -1 }));
    }

    [Fact]
    public void 护栏契约_锁定SQL与文案声明不改写父业务字段()
    {
        Assert.Contains("UPDLOCK, HOLDLOCK", AttachmentEvidenceMutationRules.OwnerRowLockSqlTemplate);
        Assert.Contains("UPDLOCK, HOLDLOCK", AttachmentEvidenceMutationRules.EvidenceRowLockSql);
        Assert.Contains("UpdatedAt", AttachmentEvidenceMutationRules.LockOrderText);
        Assert.Contains("不改写", AttachmentEvidenceMutationRules.BoundaryText);

        var rules = File.ReadAllText(RepoFile(
            "src", "ERP.Application", "Services", "AttachmentEvidenceMutationRules.cs"));
        Assert.Contains("UPDLOCK, HOLDLOCK", rules);
        Assert.Contains("AttachmentEvidenceIntegrityException", rules);

        var service = File.ReadAllText(RepoFile(
            "src", "ERP.Application", "Services", "AttachmentEvidenceService.cs"));
        Assert.Contains("BeginMutationTransactionAsync", service);
        Assert.Contains("LockOwnerRowAsync", service);
        Assert.Contains("LockEvidenceRowAsync", service);
        Assert.Contains("TryCompensateContentAsync", service);
        Assert.DoesNotContain("Directory.Delete", service);
        Assert.DoesNotContain("GetFiles(", service);

        var contract = File.ReadAllText(RepoFile(
            "src", "ERP.Application", "Interfaces", "IAttachmentContentStore.cs"));
        Assert.Contains("TryRemoveRequestOwnedAsync", contract);
        Assert.Contains("Task.FromResult(false)", contract);
    }

    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));


    // ==================== 4. 隔离本地存储的有界补偿安全口径 ====================

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "erp409-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public async Task 隔离存储_有界补偿_只删除本次新建的单文件_且不做目录扫荡()
    {
        var root = NewTempRoot();
        try
        {
            var store = new IsolatedLocalAttachmentContentStore(new AttachmentStorageOptions { RootPath = root });
            var first = await store.SaveAsync(new MemoryStream(PdfBytes("one")), ".pdf");
            var second = await store.SaveAsync(new MemoryStream(PdfBytes("two")), ".pdf");

            await using (var opened = await store.OpenReadAsync(first)) Assert.NotNull(opened);
            Assert.Equal(new[] { first, second }.OrderBy(k => k).ToArray(),
                Directory.GetFiles(root, "*.pdf", SearchOption.AllDirectories)
                    .Select(p => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'))
                    .OrderBy(k => k).ToArray());

            Assert.True(await store.TryRemoveRequestOwnedAsync(first));

            var remaining = Directory.GetFiles(root, "*.pdf", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'))
                .ToArray();
            Assert.Equal(new[] { second }, remaining);                    // 只删目标文件，其它文件与目录保留
            Assert.False(await store.TryRemoveRequestOwnedAsync(first));  // 已不存在 → 不谎报已清理
            Assert.Null(await store.OpenReadAsync(first));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task 隔离存储_有界补偿_拒绝穿越绝对路径与非法形态键_且不动根外文件()
    {
        var outsideRoot = NewTempRoot();
        var root = NewTempRoot();
        try
        {
            var outsideFile = Path.Combine(outsideRoot, "outside.pdf");
            await File.WriteAllBytesAsync(outsideFile, PdfBytes("outside"));

            var store = new IsolatedLocalAttachmentContentStore(new AttachmentStorageOptions { RootPath = root });
            var insideKey = await store.SaveAsync(new MemoryStream(PdfBytes("inside")), ".pdf");

            Assert.False(await store.TryRemoveRequestOwnedAsync("../outside.pdf"));
            Assert.False(await store.TryRemoveRequestOwnedAsync(Path.Combine(outsideRoot, "outside.pdf")));
            Assert.False(await store.TryRemoveRequestOwnedAsync(""));
            Assert.False(await store.TryRemoveRequestOwnedAsync("not-a-key.pdf"));
            Assert.False(await store.TryRemoveRequestOwnedAsync("20260901/xyz.pdf"));
            Assert.False(await store.TryRemoveRequestOwnedAsync("20260901/20260901/x.pdf"));
            Assert.False(await store.TryRemoveRequestOwnedAsync(
                new string('k', AttachmentEvidenceRules.MaxStorageKeyLength + 1)));
            // 形态合法但不存在：返回 false（未删除任何内容）
            Assert.False(await store.TryRemoveRequestOwnedAsync(
                $"20260901/{new string('a', 32)}.pdf"));

            Assert.True(File.Exists(outsideFile));               // 根外文件绝不被触碰
            Assert.Equal(PdfBytes("outside"), await File.ReadAllBytesAsync(outsideFile));
            Assert.True(await store.TryRemoveRequestOwnedAsync(insideKey));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(outsideRoot)) Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public async Task 隔离存储_有界补偿_非生产提供程序与生产提供程序口径()
    {
        var root = NewTempRoot();
        try
        {
            var store = new IsolatedLocalAttachmentContentStore(new AttachmentStorageOptions { RootPath = root });
            var key = await store.SaveAsync(new MemoryStream(PdfBytes()), ".pdf");

            // 隔离本地存储恒为非生产提供程序：通过形态与根目录校验后执行单文件删除
            Assert.False(store.IsProductionProvider);
            Assert.True(await store.TryRemoveRequestOwnedAsync(key));

            // 不支持的实现（默认接口实现）返回 false：绝不删除任何内容（最安全失败姿态）
            IAttachmentContentStore unsupported = new SpyContentStore();
            var stored = await unsupported.SaveAsync(new MemoryStream(PdfBytes()), ".pdf");
            IAttachmentContentStore plain = new UnsupportedStore();
            Assert.False(await plain.TryRemoveRequestOwnedAsync(stored, default));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>未实现补偿接缝的存储：直接使用接口默认实现（返回 false = 不支持）。</summary>
    private sealed class UnsupportedStore : IAttachmentContentStore
    {
        public bool IsProductionProvider => false;

        public string ProviderCode => AttachmentEvidenceRules.ProviderIsolatedLocal;

        public string ProviderText => AttachmentEvidenceRules.ProviderText(ProviderCode);

        public Task<string> SaveAsync(
            Stream content, string extension, CancellationToken cancellationToken = default)
            => Task.FromResult($"{DateTime.UtcNow:yyyyMMdd}/{Guid.NewGuid():N}{extension}");

        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream?>(null);

        public Task<long?> GetLengthAsync(string storageKey, CancellationToken cancellationToken = default)
            => Task.FromResult<long?>(null);
    }
}

