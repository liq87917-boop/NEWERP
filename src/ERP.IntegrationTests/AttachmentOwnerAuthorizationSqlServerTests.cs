using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-407 附件证据归属授权的真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实规则 + 真实控制器</b>：以既有菜单（<c>sales-order</c> / <c>purchase-order</c> / <c>doc-center</c> /
/// <c>sample</c>）与真实业务员数据范围（两个客户）验证普通路由（台账 / 按归属清单 / 归属候选 / 归属摘要 /
/// 详情 / 上传 / 下载 / 作废）与附件中心工作台（台账 / 摘要 / 详情 / 下载）的本人可用与越界 fail closed；
/// 直接知道证据 Id 也不能绕过父单据授权；撤销菜单 / 禁用账号 / 无菜单立即收敛。</item>
/// <item><b>计数存储间谍</b>：被拒绝的请求既不改写任何行，也绝不读取或写入任何字节。</item>
/// <item><b>两个独立连接竞态</b>：① 两连接并发伪造越界上传 → 二者都拒绝且零落库零落盘；
/// ② 越界下载与本人作废并发 → 越界始终拒绝、本人作废生效，归属从未被改写（无撕裂）。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>保留既有库存来源单据审计与原始失败日志：本测试只新增自己的证据行，不清理 / 不删除任何既有行。
/// 构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class AttachmentOwnerAuthorizationSqlServerTests
    : IClassFixture<AttachmentOwnerAuthorizationSqlServerFixture>
{
    private readonly AttachmentOwnerAuthorizationSqlServerFixture _fixture;

    public AttachmentOwnerAuthorizationSqlServerTests(AttachmentOwnerAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(AttachmentOwnerAuthorizationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 控制器 / 独立连接脚手架 ====================

    private static AttachmentEvidenceController NewController(
        ErpDbContext db, IAttachmentContentStore store, long? userId)
    {
        var http = new DefaultHttpContext();
        if (userId.HasValue)
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"));

        return new AttachmentEvidenceController(db, store)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    /// <summary>用一条独立连接执行控制器动作（每次调用各自 DbContext / 连接），返回成功标志与错误。</summary>
    private async Task<(bool Success, string Error)> TryAsync(
        IAttachmentContentStore store, long? userId,
        Func<AttachmentEvidenceController, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await action(NewController(db, store, userId));
            return (result is OkObjectResult, string.Empty);
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

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static IFormFile TestFile(string fileName)
    {
        var bytes = Encoding.UTF8.GetBytes("%PDF-1.4\n" + fileName);
        return new FormFile(new MemoryStream(bytes), 0, bytes.LongLength, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = AttachmentEvidenceRules.MediaPdf
        };
    }

    /// <summary>线程安全的计数内容存储间谍：拒绝路径必须证明保存 / 读取调用数均不变。</summary>
    private sealed class CountingAttachmentContentStore : IAttachmentContentStore
    {
        private readonly Dictionary<string, byte[]> _items = new();
        private readonly object _gate = new();
        private int _saveCalls;
        private int _openCalls;

        public int SaveCalls => Volatile.Read(ref _saveCalls);

        public int OpenCalls => Volatile.Read(ref _openCalls);

        public bool IsProductionProvider => false;

        public string ProviderCode => AttachmentEvidenceRules.ProviderIsolatedLocal;

        public string ProviderText =>
            AttachmentEvidenceRules.ProviderText(AttachmentEvidenceRules.ProviderIsolatedLocal);

        public Task<string> SaveAsync(
            Stream content, string extension, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _saveCalls);
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            var key = $"{Guid.NewGuid():N}{extension}";
            lock (_gate) _items[key] = buffer.ToArray();
            return Task.FromResult(key);
        }

        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _openCalls);
            lock (_gate)
            {
                return Task.FromResult<Stream?>(_items.TryGetValue(storageKey, out var bytes)
                    ? new MemoryStream(bytes, writable: false)
                    : null);
            }
        }

        public Task<long?> GetLengthAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult<long?>(_items.TryGetValue(storageKey, out var bytes)
                    ? bytes.LongLength
                    : null);
            }
        }
    }

    // ==================== 种子（既有菜单 / 既有业务员数据范围，不新增权限模型） ====================

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

    /// <summary>播种受限业务员账号：登录名 = 员工编码（ERP-097 权威映射），并按需授予既有模块菜单。</summary>
    private static async Task<(SysUser User, BaseEmployee Employee, SysRole Role)> SeedSalesmanAsync(
        ErpDbContext db, params string[] menuCodes)
    {
        var code = $"INT_E407_EMP_{Tag()}";
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
            RoleName = $"INT_E407_ROLE_{code}", RoleCode = $"INT_E407_{Guid.NewGuid():N}", IsSystem = false
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

    private static async Task<SalesOrder> SeedSalesOrderAsync(ErpDbContext db, string orderNo, long customerId)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo, OrderDate = DateTime.Today, CustomerId = customerId,
            Currency = Currency.USD, TotalAmount = 100m, Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static async Task<PurchaseOrder> SeedPurchaseOrderAsync(
        ErpDbContext db, string orderNo, long? owningCustomerId, long? owningSalesOrderId = null)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo, OrderDate = DateTime.Today, SupplierId = 601, Currency = Currency.CNY,
            TotalAmount = 50m, Status = DocumentStatus.Approved,
            OwningCustomerId = owningCustomerId, OwningSalesOrderId = owningSalesOrderId,
            ArrivalProgress = "未到货", QcStatus = "未验货"
        };
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static async Task<TradeDocument> SeedTradeDocumentAsync(
        ErpDbContext db, string docNo, long? customerId)
    {
        var document = new TradeDocument
        {
            DocNo = docNo, DocType = "装箱单", IssueDate = DateTime.Today, Amount = 10m,
            Currency = "USD", Status = "待制作", CustomerId = customerId
        };
        db.TradeDocuments.Add(document);
        await db.SaveChangesAsync();
        return document;
    }

    private static async Task<Sample> SeedSampleAsync(ErpDbContext db, string sampleNo, long? customerId)
    {
        var sample = new Sample
        {
            SampleNo = sampleNo, SampleDate = DateTime.Today, CustomerId = customerId,
            CustomerName = "集成样品客户", ProductName = "样品", SampleType = "寄样",
            Quantity = 1m, Unit = "件", Currency = "CNY", Result = "待反馈"
        };
        db.Samples.Add(sample);
        await db.SaveChangesAsync();
        return sample;
    }

    private static async Task<AttachmentEvidence> SeedEvidenceAsync(
        ErpDbContext db, string ownerType, long ownerId, string ownerNo, string fileName = "集成证据.pdf")
    {
        var row = new AttachmentEvidence
        {
            OwnerType = ownerType,
            OwnerId = ownerId,
            OwnerNo = ownerNo,
            OwnerTypeText = AttachmentEvidenceRules.OwnerTypeText(ownerType),
            OriginalFileName = fileName,
            MediaType = AttachmentEvidenceRules.MediaPdf,
            SizeBytes = 12,
            Sha256 = new string('a', AttachmentEvidenceRules.Sha256Length),
            StorageKey = $"{Guid.NewGuid():N}.pdf",
            StorageProvider = AttachmentEvidenceRules.ProviderIsolatedLocal,
            UploadedBy = "集成上传人",
            RecordedAt = DateTime.Now,
            Status = AttachmentEvidenceRules.StatusActive,
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

    // ==================== 1. 真实身份 / 菜单 / 数据范围 ====================

    [Fact]
    public async Task 真实身份与菜单_本人可用_撤销菜单与禁用账号立即收敛()
    {
        Guard();
        var tag = Tag();
        long userId, roleId, customerId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, role) = await SeedSalesmanAsync(seed, AttachmentEvidenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            roleId = role.Id;
            customerId = (await SeedCustomerAsync(seed, $"INT_E407_C_{tag}", employee.Id)).Id;
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var access = await AttachmentOwnerAuthorizationRules.ResolveAsync(db, userId);
            Assert.False(access.IsPrivileged);
            Assert.True(access.Scope.AllowsCustomer(customerId));
            Assert.Equal(new[] { AttachmentEvidenceRules.OwnerTypeSalesOrder }, access.AuthorizedOwnerTypes.ToArray());
        }

        await using (var revoke = _fixture.CreateDbContext())
            await RevokeMenusAsync(revoke, roleId);

        await using (var db = _fixture.CreateDbContext())
        {
            var access = await AttachmentOwnerAuthorizationRules.ResolveAsync(db, userId);
            Assert.Empty(access.AuthorizedOwnerTypes);
        }

        long disabledId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, _, _) = await SeedSalesmanAsync(seed, AttachmentEvidenceRules.MenuCodeSalesOrder);
            user.Status = UserStatus.Disabled;
            await seed.SaveChangesAsync();
            disabledId = user.Id;
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(
                () => AttachmentOwnerAuthorizationRules.ResolveAsync(db, disabledId));
            Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        }
    }

    // ==================== 2. 两个客户：普通路由按范围收敛 ====================

    [Fact]
    public async Task 两客户_普通路由_本人可用_越界一律fail_closed且零落盘零字节()
    {
        Guard();
        var tag = Tag();
        var store = new CountingAttachmentContentStore();
        long userId, ownOrderId, foreignOrderId, foreignEvidenceId, ownEvidenceId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedSalesmanAsync(seed, AttachmentEvidenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            var own = await SeedCustomerAsync(seed, $"INT_E407_OWN_{tag}", employee.Id);
            var foreign = await SeedCustomerAsync(seed, $"INT_E407_FOREIGN_{tag}", null);
            var ownOrder = await SeedSalesOrderAsync(seed, $"INT_E407_SO_OWN_{tag}", own.Id);
            var foreignOrder = await SeedSalesOrderAsync(seed, $"INT_E407_SO_FOREIGN_{tag}", foreign.Id);
            ownOrderId = ownOrder.Id;
            foreignOrderId = foreignOrder.Id;
            ownEvidenceId = (await SeedEvidenceAsync(
                seed, AttachmentEvidenceRules.OwnerTypeSalesOrder, ownOrder.Id, ownOrder.OrderNo)).Id;
            foreignEvidenceId = (await SeedEvidenceAsync(
                seed, AttachmentEvidenceRules.OwnerTypeSalesOrder, foreignOrder.Id, foreignOrder.OrderNo)).Id;
        }

        // 本人可用（台账 / 按归属 / 候选 / 摘要 / 详情）
        Assert.True((await TryAsync(store, userId, c => c.GetPaged(new AttachmentEvidenceQuery()))).Success);
        Assert.True((await TryAsync(store, userId, c => c.GetForOwner(
            AttachmentEvidenceRules.OwnerTypeSalesOrder, ownOrderId))).Success);
        Assert.True((await TryAsync(store, userId, c => c.GetById(ownEvidenceId))).Success);
        Assert.True((await TryAsync(store, userId, c => c.OwnerOptions(
            AttachmentEvidenceRules.OwnerTypeSalesOrder, null))).Success);
        Assert.True((await TryAsync(store, userId, c => c.OwnerSummary(
            AttachmentEvidenceRules.OwnerTypeSalesOrder, $"{ownOrderId},{foreignOrderId}"))).Success);

        // 越界一律拒绝（按归属 / 详情 / 下载 / 作废 / 上传）
        Assert.False((await TryAsync(store, userId, c => c.GetForOwner(
            AttachmentEvidenceRules.OwnerTypeSalesOrder, foreignOrderId))).Success);
        Assert.False((await TryAsync(store, userId, c => c.GetById(foreignEvidenceId))).Success);
        Assert.False((await TryAsync(store, userId, c => c.DownloadContent(foreignEvidenceId))).Success);
        Assert.False((await TryAsync(store, userId, c => c.Void(
            foreignEvidenceId, new AttachmentEvidenceVoidRequest { Reason = "越界作废" }))).Success);
        Assert.False((await TryAsync(store, userId, c => c.Upload(
            TestFile("越界上传.pdf"), AttachmentEvidenceRules.OwnerTypeSalesOrder, foreignOrderId, "说明"))).Success);

        // 台账总数与摘要只包含本人归属（不泄露范围外存在性与计数）
        await using (var db = _fixture.CreateDbContext())
        {
            var controller = NewController(db, store, userId);
            var page = Assert.IsType<OkObjectResult>(await controller.GetPaged(new AttachmentEvidenceQuery()));
            var data = Assert.IsType<ApiResponse<PagedResult<AttachmentEvidenceDto>>>(page.Value).Data!;
            Assert.Equal(1, data.Total);
            Assert.Equal(ownEvidenceId, Assert.Single(data.Items).Id);

            var summaryResult = Assert.IsType<OkObjectResult>(await controller.OwnerSummary(
                AttachmentEvidenceRules.OwnerTypeSalesOrder, $"{ownOrderId},{foreignOrderId}"));
            var summaries = Assert.IsType<ApiResponse<List<AttachmentEvidenceOwnerSummaryDto>>>(
                summaryResult.Value).Data!;
            Assert.DoesNotContain(summaries, s => s.OwnerId == foreignOrderId);
        }

        // 越界请求不改写任何行、也不保存 / 读取任何字节
        Assert.Equal(0, store.SaveCalls);
        Assert.Equal(0, store.OpenCalls);
        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(AttachmentEvidenceRules.StatusActive,
                (await verify.AttachmentEvidences.AsNoTracking()
                    .SingleAsync(r => r.Id == foreignEvidenceId)).Status);
            Assert.False(await verify.AttachmentEvidences.AsNoTracking()
                .AnyAsync(r => r.OriginalFileName == "越界上传.pdf"));
        }
    }

    // ==================== 3. 附件中心工作台 ====================

    [Fact]
    public async Task 附件中心_台账摘要详情下载_按范围与菜单收敛()
    {
        Guard();
        var tag = Tag();
        var store = new CountingAttachmentContentStore();
        long userId, ownDocId, foreignEvidenceId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedSalesmanAsync(seed, AttachmentEvidenceRules.MenuCodeTradeDocument);
            userId = user.Id;
            var own = await SeedCustomerAsync(seed, $"INT_E407_CD_OWN_{tag}", employee.Id);
            var foreign = await SeedCustomerAsync(seed, $"INT_E407_CD_FOREIGN_{tag}", null);
            var ownDoc = await SeedTradeDocumentAsync(seed, $"INT_E407_CD_OWN_{tag}", own.Id);
            var foreignDoc = await SeedTradeDocumentAsync(seed, $"INT_E407_CD_FOREIGN_{tag}", foreign.Id);
            var unlinkedDoc = await SeedTradeDocumentAsync(seed, $"INT_E407_CD_UNLINKED_{tag}", null);
            ownDocId = ownDoc.Id;
            await SeedEvidenceAsync(seed, AttachmentEvidenceRules.OwnerTypeTradeDocument, ownDoc.Id, ownDoc.DocNo);
            foreignEvidenceId = (await SeedEvidenceAsync(
                seed, AttachmentEvidenceRules.OwnerTypeTradeDocument, foreignDoc.Id, foreignDoc.DocNo)).Id;
            await SeedEvidenceAsync(seed, AttachmentEvidenceRules.OwnerTypeTradeDocument, unlinkedDoc.Id, unlinkedDoc.DocNo);
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var controller = NewController(db, store, userId);

            var page = Assert.IsType<ApiResponse<PagedResult<AttachmentEvidenceDto>>>(
                ((OkObjectResult)await controller.GetCenterPaged(new AttachmentEvidenceCenterQuery())).Value).Data!;
            Assert.Equal(1, page.Total);                                   // 越界与无权威归属都不计
            Assert.Equal(ownDocId, Assert.Single(page.Items).OwnerId);

            var summary = Assert.IsType<ApiResponse<AttachmentEvidenceCenterSummaryDto>>(
                ((OkObjectResult)await controller.GetCenterSummary()).Value).Data!;
            Assert.Equal(1, summary.TotalCount);
            Assert.Equal(new[] { AttachmentEvidenceRules.OwnerTypeTradeDocument },
                summary.OwnerTypeCounts.Select(c => c.OwnerType).ToArray());

            Assert.False((await TryAsync(store, userId, c => c.GetCenterDetail(foreignEvidenceId))).Success);
            Assert.False((await TryAsync(store, userId, c => c.DownloadCenterContent(foreignEvidenceId))).Success);
        }

        Assert.Equal(0, store.OpenCalls);                                  // 拒绝路径没有读取任何字节

        // 无菜单账号：工作台不披露任何记录与计数
        long noMenuId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, _, _) = await SeedSalesmanAsync(seed);
            noMenuId = user.Id;
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var controller = NewController(db, store, noMenuId);
            var page = Assert.IsType<ApiResponse<PagedResult<AttachmentEvidenceDto>>>(
                ((OkObjectResult)await controller.GetCenterPaged(new AttachmentEvidenceCenterQuery())).Value).Data!;
            Assert.Equal(0, page.Total);

            var summary = Assert.IsType<ApiResponse<AttachmentEvidenceCenterSummaryDto>>(
                ((OkObjectResult)await controller.GetCenterSummary()).Value).Data!;
            Assert.False(summary.Scope.HasAnyAuthorizedOwnerType);
            Assert.False((await TryAsync(store, noMenuId, c => c.GetCenterDetail(foreignEvidenceId))).Success);
        }
    }

    // ==================== 4. 采购 / 验货：权威归属两侧与特权历史可见性 ====================

    [Fact]
    public async Task 采购与验货_权威归属两侧与无归属备货采购_以及特权历史可见性()
    {
        Guard();
        var tag = Tag();
        var store = new CountingAttachmentContentStore();
        long userId, privilegedId, poOwnId, poLinkOwnId, poForeignId, poMixedId, poUnlinkedId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedSalesmanAsync(seed, AttachmentEvidenceRules.MenuCodePurchaseOrder);
            userId = user.Id;
            var (privileged, _, privilegedRole) = await SeedSalesmanAsync(
                seed, AttachmentEvidenceRules.MenuCodePurchaseOrder);
            privilegedRole.IsSystem = true;      // 系统内置角色 = 特权数据范围（既有口径）
            await seed.SaveChangesAsync();
            privilegedId = privileged.Id;

            var own = await SeedCustomerAsync(seed, $"INT_E407_PO_OWN_{tag}", employee.Id);
            var foreign = await SeedCustomerAsync(seed, $"INT_E407_PO_FOREIGN_{tag}", null);
            var ownOrder = await SeedSalesOrderAsync(seed, $"INT_E407_PO_SO_OWN_{tag}", own.Id);
            var foreignOrder = await SeedSalesOrderAsync(seed, $"INT_E407_PO_SO_FOREIGN_{tag}", foreign.Id);

            var poOwn = await SeedPurchaseOrderAsync(seed, $"INT_E407_PO_OWN_{tag}", own.Id);
            var poLinkOwn = await SeedPurchaseOrderAsync(seed, $"INT_E407_PO_LINK_{tag}", null, ownOrder.Id);
            var poForeign = await SeedPurchaseOrderAsync(seed, $"INT_E407_PO_FOREIGN_{tag}", foreign.Id);
            var poMixed = await SeedPurchaseOrderAsync(seed, $"INT_E407_PO_MIXED_{tag}", own.Id, foreignOrder.Id);
            var poUnlinked = await SeedPurchaseOrderAsync(seed, $"INT_E407_PO_UNLINKED_{tag}", null);
            poOwnId = poOwn.Id;
            poLinkOwnId = poLinkOwn.Id;
            poForeignId = poForeign.Id;
            poMixedId = poMixed.Id;
            poUnlinkedId = poUnlinked.Id;

            await SeedEvidenceAsync(seed, AttachmentEvidenceRules.OwnerTypePurchaseOrder, poOwn.Id, poOwn.OrderNo);
            await SeedEvidenceAsync(seed, AttachmentEvidenceRules.OwnerTypePurchaseOrder, poLinkOwn.Id, poLinkOwn.OrderNo);
            await SeedEvidenceAsync(seed, AttachmentEvidenceRules.OwnerTypePurchaseOrder, poForeign.Id, poForeign.OrderNo);
            await SeedEvidenceAsync(seed, AttachmentEvidenceRules.OwnerTypePurchaseOrder, poUnlinked.Id, poUnlinked.OrderNo);
            await SeedEvidenceAsync(seed, AttachmentEvidenceRules.OwnerTypeQualityInspection, poOwn.Id, poOwn.OrderNo);
            await SeedEvidenceAsync(seed, AttachmentEvidenceRules.OwnerTypeQualityInspection, poForeign.Id, poForeign.OrderNo);
            await SeedEvidenceAsync(seed, AttachmentEvidenceRules.OwnerTypeQualityInspection, poUnlinked.Id, poUnlinked.OrderNo);
        }

        // 本人显式归属与权威来源销售订单归属都可用
        Assert.True((await TryAsync(store, userId, c => c.GetForOwner(
            AttachmentEvidenceRules.OwnerTypePurchaseOrder, poOwnId))).Success);
        Assert.True((await TryAsync(store, userId, c => c.GetForOwner(
            AttachmentEvidenceRules.OwnerTypePurchaseOrder, poLinkOwnId))).Success);
        Assert.True((await TryAsync(store, userId, c => c.GetForOwner(
            AttachmentEvidenceRules.OwnerTypeQualityInspection, poOwnId))).Success);

        // 越界 / 混合归属 / 无归属备货采购一律拒绝（采购订单与验货记录同口径）
        foreach (var deniedId in new[] { poForeignId, poMixedId, poUnlinkedId })
        {
            var ownerId = deniedId;
            Assert.False((await TryAsync(store, userId, c => c.GetForOwner(
                AttachmentEvidenceRules.OwnerTypePurchaseOrder, ownerId))).Success);
            Assert.False((await TryAsync(store, userId, c => c.GetForOwner(
                AttachmentEvidenceRules.OwnerTypeQualityInspection, ownerId))).Success);
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var controller = NewController(db, store, userId);
            var page = Assert.IsType<ApiResponse<PagedResult<AttachmentEvidenceDto>>>(
                ((OkObjectResult)await controller.GetPaged(new AttachmentEvidenceQuery())).Value).Data!;
            Assert.Equal(3, page.Total);      // 2 条采购订单 + 本人采购订单的 1 条验货记录

            var options = Assert.IsType<ApiResponse<List<AttachmentEvidenceOwnerOptionDto>>>(
                ((OkObjectResult)await controller.OwnerOptions(
                    AttachmentEvidenceRules.OwnerTypePurchaseOrder, null)).Value).Data!;
            Assert.Equal(2, options.Count);
        }

        // 特权账号：保留既有历史访问（范围外的采购订单也可读），但仍受既有菜单限制
        Assert.True((await TryAsync(store, privilegedId, c => c.GetForOwner(
            AttachmentEvidenceRules.OwnerTypePurchaseOrder, poForeignId))).Success);
        Assert.True((await TryAsync(store, privilegedId, c => c.GetForOwner(
            AttachmentEvidenceRules.OwnerTypePurchaseOrder, poUnlinkedId))).Success);
    }

    // ==================== 5. 两个独立连接竞态 ====================

    [Fact]
    public async Task 竞态一_两连接并发伪造越界上传_二者都拒绝且零落库零落盘()
    {
        Guard();
        var tag = Tag();
        var store = new CountingAttachmentContentStore();
        long userId, foreignOrderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedSalesmanAsync(seed, AttachmentEvidenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            await SeedCustomerAsync(seed, $"INT_E407_R1_OWN_{tag}", employee.Id);
            var foreign = await SeedCustomerAsync(seed, $"INT_E407_R1_FOREIGN_{tag}", null);
            foreignOrderId = (await SeedSalesOrderAsync(seed, $"INT_E407_R1_SO_{tag}", foreign.Id)).Id;
        }

        var results = await RaceAsync(
            () => TryAsync(store, userId, c => c.Upload(
                TestFile($"INT_E407_R1_A_{tag}.pdf"), AttachmentEvidenceRules.OwnerTypeSalesOrder, foreignOrderId, "说明")),
            () => TryAsync(store, userId, c => c.Upload(
                TestFile($"INT_E407_R1_B_{tag}.pdf"), AttachmentEvidenceRules.OwnerTypeSalesOrder, foreignOrderId, "说明")));

        Assert.All(results, r => Assert.False(r.Success));
        Assert.Equal(0, store.SaveCalls);                       // 任何一侧都没有落盘

        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.False(await verify.AttachmentEvidences.AsNoTracking().AnyAsync(
                r => r.OriginalFileName == $"INT_E407_R1_A_{tag}.pdf"
                     || r.OriginalFileName == $"INT_E407_R1_B_{tag}.pdf"));
        }
    }

    [Fact]
    public async Task 竞态二_越界下载与本人作废并发_越界始终拒绝本人作废生效且归属未改写()
    {
        Guard();
        var tag = Tag();
        var store = new CountingAttachmentContentStore();
        long userId, foreignEvidenceId, ownEvidenceId, foreignOrderId, ownOrderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedSalesmanAsync(seed, AttachmentEvidenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            var own = await SeedCustomerAsync(seed, $"INT_E407_R2_OWN_{tag}", employee.Id);
            var foreign = await SeedCustomerAsync(seed, $"INT_E407_R2_FOREIGN_{tag}", null);
            var ownOrder = await SeedSalesOrderAsync(seed, $"INT_E407_R2_SO_OWN_{tag}", own.Id);
            var foreignOrder = await SeedSalesOrderAsync(seed, $"INT_E407_R2_SO_FOREIGN_{tag}", foreign.Id);
            foreignOrderId = foreignOrder.Id;
            ownOrderId = ownOrder.Id;
            ownEvidenceId = (await SeedEvidenceAsync(
                seed, AttachmentEvidenceRules.OwnerTypeSalesOrder, ownOrder.Id, ownOrder.OrderNo)).Id;
            foreignEvidenceId = (await SeedEvidenceAsync(
                seed, AttachmentEvidenceRules.OwnerTypeSalesOrder, foreignOrder.Id, foreignOrder.OrderNo)).Id;
        }

        var results = await RaceAsync(
            () => TryAsync(store, userId, c => c.DownloadContent(foreignEvidenceId)),
            () => TryAsync(store, userId, c => c.Void(
                ownEvidenceId, new AttachmentEvidenceVoidRequest { Reason = "本人并发作废" })));

        Assert.False(results[0].Success);   // 越界下载始终被拒绝
        Assert.True(results[1].Success);    // 本人作废在范围内证据上生效
        Assert.Equal(0, store.OpenCalls);   // 越界下载没有读取任何字节

        await using (var verify = _fixture.CreateDbContext())
        {
            var own = await verify.AttachmentEvidences.AsNoTracking().SingleAsync(r => r.Id == ownEvidenceId);
            Assert.Equal(AttachmentEvidenceRules.StatusVoided, own.Status);
            Assert.Equal("本人并发作废", own.VoidReason);
            Assert.Equal(ownOrderId, own.OwnerId);              // 归属 Id 从未被改写

            var foreign = await verify.AttachmentEvidences.AsNoTracking().SingleAsync(r => r.Id == foreignEvidenceId);
            Assert.Equal(AttachmentEvidenceRules.StatusActive, foreign.Status);
            Assert.Equal(foreignOrderId, foreign.OwnerId);
            Assert.Equal(AttachmentEvidenceRules.OwnerTypeSalesOrder, foreign.OwnerType);
        }
    }
}

/// <summary>
/// ERP-407 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class AttachmentOwnerAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ATTACHMENT_OWNER_AUTH_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-407] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await CreateFreshDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

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

        Console.WriteLine("[ERP-407] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }
}

/// <summary>ERP-407 专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class AttachmentOwnerAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => AttachmentOwnerAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}
