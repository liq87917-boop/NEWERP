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
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-407 附件证据归属授权单元测试（内存库 + 真实「角色 → 菜单」与 ERP-097 业务员数据范围）。
/// 覆盖普通路由（台账 / 按归属清单 / 归属候选 / 归属摘要 / 详情 / 上传 / 下载 / 作废）与附件中心工作台
/// （台账 / 摘要 / 详情 / 下载）：缺失 / 非法 / 已删除 / 已禁用身份、无菜单、撤销菜单、越界客户、
/// 无权威归属（销售订单 / 采购订单 / 出口单证 / 验货记录 / 样品）一律 fail closed；
/// 并证明被拒绝的请求既不改写任何行、也绝不读取内容存储（计数存储间谍）。
/// </summary>
public class AttachmentOwnerAuthorizationTests
{
    private const string OwnCustomerCode = "ERP407-OWN-CUSTOMER";
    private const string ForeignCustomerCode = "ERP407-FOREIGN-CUSTOMER";

    /// <summary>无业务员归属的客户 Id（未映射到任何业务员 → 受限账号恒不可见）</summary>
    private const long OwnerCustomerId = 501;

    // ==================== 0. 脚手架 ====================

    private static byte[] PdfBytes(string body = "abc") => Encoding.UTF8.GetBytes("%PDF-1.4\n" + body);

    private static void GrantMenu(ErpDbContext db, long roleId, string menuCode)
    {
        var menu = db.SysMenus.FirstOrDefault(m => m.MenuCode == menuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu
            {
                ParentId = 0,
                MenuName = $"菜单 {menuCode}",
                MenuCode = menuCode,
                Path = $"/{menuCode}",
                Icon = "test",
                SortOrder = 1,
                MenuType = MenuType.Menu
            };
            db.SysMenus.Add(menu);
            db.SaveChanges();
        }

        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
        db.SaveChanges();
    }

    /// <summary>播种受限业务员账号（登录名 = 员工编码，ERP-097 权威映射）并按需授予既有模块菜单。</summary>
    private static (SysUser User, BaseEmployee Employee) SeedSalesman(
        ErpDbContext db, string userName, params string[] menuCodes)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = userName, EmployeeName = userName, IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = userName,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = $"R-{userName}",
            RoleCode = $"R-{Guid.NewGuid():N}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        foreach (var code in menuCodes) GrantMenu(db, role.Id, code);
        return (user, employee);
    }

    /// <summary>播种系统内置（特权）账号：菜单仍是唯一收敛维度，客户数据范围不受限。</summary>
    private static long SeedPrivilegedUser(ErpDbContext db, params string[] menuCodes)
    {
        var user = new SysUser
        {
            UserName = $"erp407-admin-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "特权账号",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = "ERP-407 系统角色",
            RoleCode = $"SYS-{Guid.NewGuid():N}",
            IsSystem = true
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        foreach (var code in menuCodes) GrantMenu(db, role.Id, code);
        return user.Id;
    }

    private static void RevokeMenus(ErpDbContext db, long userId)
    {
        var roleIds = db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId)
            .ToList();
        foreach (var grant in db.SysRoleMenus.Where(rm => roleIds.Contains(rm.RoleId) && !rm.IsDeleted).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, long? empId)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code, CustomerName = code, Status = 1, CreditStatus = "正常",
            Currency = "USD", EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedSalesOrder(ErpDbContext db, string orderNo, long customerId)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo, OrderDate = DateTime.Today, CustomerId = customerId,
            Currency = Currency.USD, TotalAmount = 100m, Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static PurchaseOrder SeedPurchaseOrder(
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
        db.SaveChanges();
        return order;
    }

    private static TradeDocument SeedTradeDocument(ErpDbContext db, string docNo, long? customerId)
    {
        var document = new TradeDocument
        {
            DocNo = docNo, DocType = "装箱单", IssueDate = DateTime.Today, Amount = 10m,
            Currency = "USD", Status = "待制作", CustomerId = customerId
        };
        db.TradeDocuments.Add(document);
        db.SaveChanges();
        return document;
    }

    private static Sample SeedSample(ErpDbContext db, string sampleNo, long? customerId)
    {
        var sample = new Sample
        {
            SampleNo = sampleNo, SampleDate = DateTime.Today, CustomerId = customerId,
            CustomerName = "样品客户", ProductName = "样品", SampleType = "寄样",
            Quantity = 1m, Unit = "件", Currency = "CNY", Result = "待反馈"
        };
        db.Samples.Add(sample);
        db.SaveChanges();
        return sample;
    }

    private static AttachmentEvidence SeedEvidence(
        ErpDbContext db, string ownerType, long ownerId, string ownerNo, string fileName = "证据.pdf")
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
            UploadedBy = "上传人",
            RecordedAt = DateTime.Now,
            Status = AttachmentEvidenceRules.StatusActive,
            CreatedAt = DateTime.Now
        };
        db.AttachmentEvidences.Add(row);
        db.SaveChanges();
        return row;
    }

    private static AttachmentEvidenceUploadRequest UploadRequest(
        string ownerType, long ownerId, byte[]? content = null, string fileName = "新证据.pdf")
    {
        var bytes = content ?? PdfBytes();
        return new AttachmentEvidenceUploadRequest
        {
            OwnerType = ownerType,
            OwnerId = ownerId,
            FileName = fileName,
            DeclaredContentType = AttachmentEvidenceRules.MediaPdf,
            DeclaredLength = bytes.LongLength,
            Description = string.Empty,
            Content = new MemoryStream(bytes, writable: false)
        };
    }

    private static AttachmentEvidenceController BuildController(
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

    private static async Task<BusinessException> AssertBusinessAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, response.Code);
        Assert.NotNull(response.Data);
        return response.Data!;
    }

    /// <summary>
    /// 测试内计数内容存储（唯一内容接缝）：拒绝路径必须证明 <see cref="SaveCalls"/> / <see cref="OpenCalls"/> 均为 0
    /// （即从未触碰任何字节）。
    /// </summary>
    private sealed class CountingStore : IAttachmentContentStore
    {
        public Dictionary<string, byte[]> Items { get; } = new();

        public int SaveCalls { get; private set; }

        public int OpenCalls { get; private set; }

        public bool IsProductionProvider => false;

        public string ProviderCode => AttachmentEvidenceRules.ProviderIsolatedLocal;

        public string ProviderText =>
            AttachmentEvidenceRules.ProviderText(AttachmentEvidenceRules.ProviderIsolatedLocal);

        public Task<string> SaveAsync(
            Stream content, string extension, CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            var key = $"{Guid.NewGuid():N}{extension}";
            Items[key] = buffer.ToArray();
            return Task.FromResult(key);
        }

        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            OpenCalls++;
            return Task.FromResult<Stream?>(Items.TryGetValue(storageKey, out var bytes)
                ? new MemoryStream(bytes, writable: false)
                : null);
        }

        public Task<long?> GetLengthAsync(string storageKey, CancellationToken cancellationToken = default)
            => Task.FromResult<long?>(Items.TryGetValue(storageKey, out var bytes) ? bytes.LongLength : null);
    }

    private static IFormFile TestFile(
        byte[]? content = null, string fileName = "证据.pdf",
        string contentType = AttachmentEvidenceRules.MediaPdf)
    {
        var bytes = content ?? PdfBytes();
        return new FormFile(new MemoryStream(bytes), 0, bytes.LongLength, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
    }

    // ==================== 1. 实时身份 + 既有菜单 ====================

    [Fact]
    public async Task 普通路由_缺失非法已删除身份按未认证拒绝_禁用账号按权限不足拒绝()
    {
        using var db = TestDbFactory.Create();
        var store = new CountingStore();
        var order = SeedSalesOrder(db, "SO-407-ID", OwnerCustomerId);
        var evidence = SeedEvidence(
            db, AttachmentEvidenceRules.OwnerTypeSalesOrder, order.Id, order.OrderNo);

        var (disabled, _) = SeedSalesman(db, "erp407-disabled", AttachmentEvidenceRules.MenuCodeSalesOrder);
        disabled.Status = UserStatus.Disabled;
        db.SaveChanges();

        foreach (long? userId in new long?[] { null, 0, 987_654_321 })
        {
            var controller = BuildController(db, store, userId);
            await AssertBusinessAsync(ErrorCodes.Unauthorized, () => controller.GetPaged(new AttachmentEvidenceQuery()));
            await AssertBusinessAsync(ErrorCodes.Unauthorized, () => controller.Metadata());
            await AssertBusinessAsync(ErrorCodes.Unauthorized, () => controller.GetById(evidence.Id));
            await AssertBusinessAsync(ErrorCodes.Unauthorized, () =>
                controller.GetForOwner(AttachmentEvidenceRules.OwnerTypeSalesOrder, order.Id));
            await AssertBusinessAsync(ErrorCodes.Unauthorized, () =>
                controller.OwnerOptions(AttachmentEvidenceRules.OwnerTypeSalesOrder, null));
            await AssertBusinessAsync(ErrorCodes.Unauthorized, () =>
                controller.OwnerSummary(AttachmentEvidenceRules.OwnerTypeSalesOrder, order.Id.ToString()));
            await AssertBusinessAsync(ErrorCodes.Unauthorized, () => controller.DownloadContent(evidence.Id));
            await AssertBusinessAsync(ErrorCodes.Unauthorized, () =>
                controller.Void(evidence.Id, new AttachmentEvidenceVoidRequest { Reason = "作废" }));
            await AssertBusinessAsync(ErrorCodes.Unauthorized, () => controller.Upload(
                TestFile(), AttachmentEvidenceRules.OwnerTypeSalesOrder, order.Id, "说明"));

            // 附件中心路由同样 fail closed，但沿用 ERP-064 口径：不披露存在性（空页 / 不存在），绝不返回数据
            Assert.Equal(0, AssertOk<PagedResult<AttachmentEvidenceDto>>(
                await controller.GetCenterPaged(new AttachmentEvidenceCenterQuery())).Total);
            Assert.False(AssertOk<AttachmentEvidenceCenterSummaryDto>(
                await controller.GetCenterSummary()).Scope.HasAnyAuthorizedOwnerType);
            await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.GetCenterDetail(evidence.Id));
            await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.DownloadCenterContent(evidence.Id));
        }

        var disabledController = BuildController(db, store, disabled.Id);
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => disabledController.GetPaged(new AttachmentEvidenceQuery()));
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => disabledController.Metadata());
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => disabledController.GetById(evidence.Id));
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => disabledController.DownloadContent(evidence.Id));
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => disabledController.Upload(
            TestFile(), AttachmentEvidenceRules.OwnerTypeSalesOrder, order.Id, "说明"));
        Assert.Equal(0, AssertOk<PagedResult<AttachmentEvidenceDto>>(
            await disabledController.GetCenterPaged(new AttachmentEvidenceCenterQuery())).Total);
        await AssertBusinessAsync(ErrorCodes.NotFound, () =>
            disabledController.GetCenterDetail(evidence.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound, () =>
            disabledController.DownloadCenterContent(evidence.Id));

        // 被拒绝的请求从未触碰任何字节、也从未落库
        Assert.Equal(0, store.SaveCalls);
        Assert.Equal(0, store.OpenCalls);
        Assert.Equal(AttachmentEvidenceRules.StatusActive,
            db.AttachmentEvidences.AsNoTracking().Single(r => r.Id == evidence.Id).Status);
    }

    [Fact]
    public async Task 无菜单授权_普通路由一律拒绝且不泄露记录计数与内容()
    {
        using var db = TestDbFactory.Create();
        var store = new CountingStore();
        var (user, employee) = SeedSalesman(db, "erp407-nomenu");
        var customer = SeedCustomer(db, OwnCustomerCode, employee.Id);
        var order = SeedSalesOrder(db, "SO-407-NOMENU", customer.Id);
        var evidence = SeedEvidence(
            db, AttachmentEvidenceRules.OwnerTypeSalesOrder, order.Id, order.OrderNo);

        var controller = BuildController(db, store, user.Id);

        Assert.Equal(0, (AssertOk<PagedResult<AttachmentEvidenceDto>>(
            await controller.GetPaged(new AttachmentEvidenceQuery()))).Total);
        await AssertBusinessAsync(ErrorCodes.Forbidden, () =>
            controller.GetForOwner(AttachmentEvidenceRules.OwnerTypeSalesOrder, order.Id));
        await AssertBusinessAsync(ErrorCodes.Forbidden, () =>
            controller.OwnerOptions(AttachmentEvidenceRules.OwnerTypeSalesOrder, null));
        await AssertBusinessAsync(ErrorCodes.Forbidden, () =>
            controller.OwnerSummary(AttachmentEvidenceRules.OwnerTypeSalesOrder, order.Id.ToString()));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.GetById(evidence.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.DownloadContent(evidence.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound, () =>
            controller.Void(evidence.Id, new AttachmentEvidenceVoidRequest { Reason = "作废" }));
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => controller.Upload(
            TestFile(), AttachmentEvidenceRules.OwnerTypeSalesOrder, order.Id, "说明"));

        Assert.Equal(0, store.SaveCalls);
        Assert.Equal(0, store.OpenCalls);
        Assert.Equal(AttachmentEvidenceRules.StatusActive,
            db.AttachmentEvidences.AsNoTracking().Single(r => r.Id == evidence.Id).Status);
    }

    [Fact]
    public async Task 撤销菜单授权后下一次请求立即收敛()
    {
        using var db = TestDbFactory.Create();
        var store = new CountingStore();
        var (user, employee) = SeedSalesman(db, "erp407-revoke", AttachmentEvidenceRules.MenuCodeSalesOrder);
        var customer = SeedCustomer(db, OwnCustomerCode, employee.Id);
        var order = SeedSalesOrder(db, "SO-407-REVOKE", customer.Id);
        var evidence = SeedEvidence(
            db, AttachmentEvidenceRules.OwnerTypeSalesOrder, order.Id, order.OrderNo);

        AssertOk<PagedResult<AttachmentEvidenceDto>>(
            await BuildController(db, store, user.Id).GetPaged(new AttachmentEvidenceQuery()));

        RevokeMenus(db, user.Id);

        var controller = BuildController(db, store, user.Id);
        Assert.Equal(0, AssertOk<PagedResult<AttachmentEvidenceDto>>(
            await controller.GetPaged(new AttachmentEvidenceQuery())).Total);
        await AssertBusinessAsync(ErrorCodes.Forbidden, () =>
            controller.GetForOwner(AttachmentEvidenceRules.OwnerTypeSalesOrder, order.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.GetById(evidence.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.DownloadContent(evidence.Id));
        Assert.Equal(0, store.OpenCalls);
    }

    [Fact]
    public async Task 未分配任何客户的受限账号_fail_closed()
    {
        using var db = TestDbFactory.Create();
        var store = new CountingStore();
        var (user, _) = SeedSalesman(db, "erp407-nocustomer", AttachmentEvidenceRules.MenuCodeSalesOrder);
        var order = SeedSalesOrder(db, "SO-407-NOCUSTOMER", OwnerCustomerId);
        var evidence = SeedEvidence(
            db, AttachmentEvidenceRules.OwnerTypeSalesOrder, order.Id, order.OrderNo);

        var controller = BuildController(db, store, user.Id);

        Assert.Equal(0, AssertOk<PagedResult<AttachmentEvidenceDto>>(
            await controller.GetPaged(new AttachmentEvidenceQuery())).Total);
        await AssertBusinessAsync(ErrorCodes.Forbidden, () =>
            controller.GetForOwner(AttachmentEvidenceRules.OwnerTypeSalesOrder, order.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.GetById(evidence.Id));
        Assert.Equal(0, store.OpenCalls);
    }

    // ==================== 2. 两个客户：越界一律 fail closed ====================

    [Fact]
    public async Task 两客户_销售订单附件_越界与无权威归属一律fail_closed且本人可用()
    {
        using var db = TestDbFactory.Create();
        var store = new CountingStore();
        var (user, employee) = SeedSalesman(db, "erp407-sales", AttachmentEvidenceRules.MenuCodeSalesOrder);
        var own = SeedCustomer(db, OwnCustomerCode, employee.Id);
        var foreign = SeedCustomer(db, ForeignCustomerCode, null);

        var ownOrder = SeedSalesOrder(db, "SO-407-OWN", own.Id);
        var foreignOrder = SeedSalesOrder(db, "SO-407-FOREIGN", foreign.Id);
        var ownEvidence = SeedEvidence(
            db, AttachmentEvidenceRules.OwnerTypeSalesOrder, ownOrder.Id, ownOrder.OrderNo, "本人证据.pdf");
        var foreignEvidence = SeedEvidence(
            db, AttachmentEvidenceRules.OwnerTypeSalesOrder, foreignOrder.Id, foreignOrder.OrderNo, "他人证据.pdf");

        var controller = BuildController(db, store, user.Id);

        // 台账：只返回本人归属，总数不含范围外（不泄露存在性与计数）
        var page = AssertOk<PagedResult<AttachmentEvidenceDto>>(
            await controller.GetPaged(new AttachmentEvidenceQuery()));
        Assert.Equal(1, page.Total);
        Assert.Equal(ownEvidence.Id, Assert.Single(page.Items).Id);

        // 按归属清单：本人可用；他人 fail closed
        var ownList = AssertOk<List<AttachmentEvidenceDto>>(
            await controller.GetForOwner(AttachmentEvidenceRules.OwnerTypeSalesOrder, ownOrder.Id));
        Assert.Equal(ownEvidence.Id, Assert.Single(ownList).Id);
        await AssertBusinessAsync(ErrorCodes.Forbidden, () =>
            controller.GetForOwner(AttachmentEvidenceRules.OwnerTypeSalesOrder, foreignOrder.Id));

        // 详情：本人可用；他人按「不存在」处理（不披露存在性）
        Assert.Equal(ownEvidence.Id, AssertOk<AttachmentEvidenceDto>(
            await controller.GetById(ownEvidence.Id)).Id);
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.GetById(foreignEvidence.Id));

        // 下载：本人证据（经上传写入内容）可用；他人一律拒绝且不读取存储
        var upload = AssertOk<AttachmentEvidenceDto>(await controller.Upload(
            TestFile(fileName: "本人下载证据.pdf"), AttachmentEvidenceRules.OwnerTypeSalesOrder,
            ownOrder.Id, "说明"));
        var download = Assert.IsType<FileStreamResult>(await controller.DownloadContent(upload.Id));
        await download.FileStream.DisposeAsync();
        Assert.Equal(1, store.OpenCalls);
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.DownloadContent(foreignEvidence.Id));
        Assert.Equal(1, store.OpenCalls);   // 越界下载没有触碰存储

        // 作废：他人拒绝且原行不变；本人成功
        await AssertBusinessAsync(ErrorCodes.NotFound, () =>
            controller.Void(foreignEvidence.Id, new AttachmentEvidenceVoidRequest { Reason = "越界作废" }));
        Assert.Equal(AttachmentEvidenceRules.StatusActive,
            db.AttachmentEvidences.AsNoTracking().Single(r => r.Id == foreignEvidence.Id).Status);
        AssertOk<AttachmentEvidenceDto>(
            await controller.Void(ownEvidence.Id, new AttachmentEvidenceVoidRequest { Reason = "本人作废" }));
        Assert.Equal(AttachmentEvidenceRules.StatusVoided,
            db.AttachmentEvidences.AsNoTracking().Single(r => r.Id == ownEvidence.Id).Status);
    }

    [Fact]
    public async Task 归属摘要_混合归属只返回范围内Id且范围外连零计数都不返回()
    {
        using var db = TestDbFactory.Create();
        var store = new CountingStore();
        var (user, employee) = SeedSalesman(db, "erp407-summary", AttachmentEvidenceRules.MenuCodeSalesOrder);
        var own = SeedCustomer(db, OwnCustomerCode, employee.Id);
        var foreign = SeedCustomer(db, ForeignCustomerCode, null);

        var ownOrder = SeedSalesOrder(db, "SO-407-SUM-OWN", own.Id);
        var foreignOrder = SeedSalesOrder(db, "SO-407-SUM-FOREIGN", foreign.Id);
        SeedEvidence(db, AttachmentEvidenceRules.OwnerTypeSalesOrder, ownOrder.Id, ownOrder.OrderNo);
        SeedEvidence(db, AttachmentEvidenceRules.OwnerTypeSalesOrder, foreignOrder.Id, foreignOrder.OrderNo);

        var controller = BuildController(db, store, user.Id);
        var summaries = AssertOk<List<AttachmentEvidenceOwnerSummaryDto>>(await controller.OwnerSummary(
            AttachmentEvidenceRules.OwnerTypeSalesOrder,
            $"{ownOrder.Id},{foreignOrder.Id}"));

        var summary = Assert.Single(summaries);
        Assert.Equal(ownOrder.Id, summary.OwnerId);
        Assert.Equal(1, summary.TotalCount);
        Assert.DoesNotContain(summaries, s => s.OwnerId == foreignOrder.Id);   // 范围外连零计数都不返回
        Assert.Equal(0, store.OpenCalls);
    }

    [Fact]
    public async Task 归属候选_只返回范围内客户且无可见客户即拒绝()
    {
        using var db = TestDbFactory.Create();
        var store = new CountingStore();
        var (user, employee) = SeedSalesman(db, "erp407-options", AttachmentEvidenceRules.MenuCodeSalesOrder);
        var own = SeedCustomer(db, OwnCustomerCode, employee.Id);
        var foreign = SeedCustomer(db, ForeignCustomerCode, null);
        SeedSalesOrder(db, "SO-407-OPT-OWN", own.Id);
        SeedSalesOrder(db, "SO-407-OPT-FOREIGN", foreign.Id);

        var controller = BuildController(db, store, user.Id);
        var options = AssertOk<List<AttachmentEvidenceOwnerOptionDto>>(
            await controller.OwnerOptions(AttachmentEvidenceRules.OwnerTypeSalesOrder, null));

        var option = Assert.Single(options);
        Assert.Equal("SO-407-OPT-OWN", option.OwnerNo);
    }

    // ==================== 3. 采购 / 验货 / 单证 / 样品 的权威归属 ====================

    [Fact]
    public async Task 采购与验货_权威归属两侧都必须落在范围内_无归属备货采购拒绝()
    {
        using var db = TestDbFactory.Create();
        var store = new CountingStore();
        var (user, employee) = SeedSalesman(db, "erp407-purchase",
            AttachmentEvidenceRules.MenuCodePurchaseOrder);
        var own = SeedCustomer(db, OwnCustomerCode, employee.Id);
        var foreign = SeedCustomer(db, ForeignCustomerCode, null);
        var ownOrder = SeedSalesOrder(db, "SO-407-PO-OWN", own.Id);
        var foreignOrder = SeedSalesOrder(db, "SO-407-PO-FOREIGN", foreign.Id);

        var poOwn = SeedPurchaseOrder(db, "PO-407-OWN", own.Id);
        var poLinkOwn = SeedPurchaseOrder(db, "PO-407-LINK-OWN", null, ownOrder.Id);
        var poForeign = SeedPurchaseOrder(db, "PO-407-FOREIGN", foreign.Id);
        var poMixedForeignLink = SeedPurchaseOrder(db, "PO-407-MIXED", own.Id, foreignOrder.Id);
        var poUnlinked = SeedPurchaseOrder(db, "PO-407-UNLINKED", null);

        SeedEvidence(db, AttachmentEvidenceRules.OwnerTypePurchaseOrder, poOwn.Id, poOwn.OrderNo);
        SeedEvidence(db, AttachmentEvidenceRules.OwnerTypePurchaseOrder, poLinkOwn.Id, poLinkOwn.OrderNo);
        SeedEvidence(db, AttachmentEvidenceRules.OwnerTypePurchaseOrder, poForeign.Id, poForeign.OrderNo);
        SeedEvidence(db, AttachmentEvidenceRules.OwnerTypePurchaseOrder, poUnlinked.Id, poUnlinked.OrderNo);
        SeedEvidence(db, AttachmentEvidenceRules.OwnerTypeQualityInspection, poOwn.Id, poOwn.OrderNo);
        SeedEvidence(db, AttachmentEvidenceRules.OwnerTypeQualityInspection, poForeign.Id, poForeign.OrderNo);
        SeedEvidence(db, AttachmentEvidenceRules.OwnerTypeQualityInspection, poUnlinked.Id, poUnlinked.OrderNo);

        var controller = BuildController(db, store, user.Id);

        // 本人显式归属 / 权威来源销售订单归属都可用
        Assert.Single(AssertOk<List<AttachmentEvidenceDto>>(await controller.GetForOwner(
            AttachmentEvidenceRules.OwnerTypePurchaseOrder, poOwn.Id)));
        Assert.Single(AssertOk<List<AttachmentEvidenceDto>>(await controller.GetForOwner(
            AttachmentEvidenceRules.OwnerTypePurchaseOrder, poLinkOwn.Id)));

        // 越界 / 混合归属（显式本人但链接到他人销售订单）/ 无归属备货采购一律拒绝
        foreach (var deniedId in new[] { poForeign.Id, poMixedForeignLink.Id, poUnlinked.Id })
        {
            var ownerId = deniedId;
            await AssertBusinessAsync(ErrorCodes.Forbidden, () =>
                controller.GetForOwner(AttachmentEvidenceRules.OwnerTypePurchaseOrder, ownerId));
            await AssertBusinessAsync(ErrorCodes.Forbidden, () =>
                controller.GetForOwner(AttachmentEvidenceRules.OwnerTypeQualityInspection, ownerId));
        }

        // 台账只保留允许的 3 条（2 采购订单 + 本人采购订单的 1 条验货记录），范围外与无归属一条都不计
        var page = AssertOk<PagedResult<AttachmentEvidenceDto>>(
            await controller.GetPaged(new AttachmentEvidenceQuery()));
        Assert.Equal(3, page.Total);

        // 候选只列出范围内采购订单
        var options = AssertOk<List<AttachmentEvidenceOwnerOptionDto>>(
            await controller.OwnerOptions(AttachmentEvidenceRules.OwnerTypePurchaseOrder, null));
        Assert.Equal(2, options.Count);
        Assert.All(options, o => Assert.Contains(o.OwnerNo, new[] { "PO-407-OWN", "PO-407-LINK-OWN" }));
    }

    [Fact]
    public async Task 出口单证与样品_权威客户缺失时受限fail_closed_特权保留历史()
    {
        using var db = TestDbFactory.Create();
        var store = new CountingStore();
        var (user, employee) = SeedSalesman(db, "erp407-doc",
            AttachmentEvidenceRules.MenuCodeTradeDocument, AttachmentEvidenceRules.MenuCodeSample);
        var own = SeedCustomer(db, OwnCustomerCode, employee.Id);
        var foreign = SeedCustomer(db, ForeignCustomerCode, null);

        var ownDoc = SeedTradeDocument(db, "DOC-407-OWN", own.Id);
        var unlinkedDoc = SeedTradeDocument(db, "DOC-407-UNLINKED", null);
        var foreignDoc = SeedTradeDocument(db, "DOC-407-FOREIGN", foreign.Id);
        var ownSample = SeedSample(db, "SP-407-OWN", own.Id);
        var unlinkedSample = SeedSample(db, "SP-407-UNLINKED", null);

        var ownDocEvidence = SeedEvidence(
            db, AttachmentEvidenceRules.OwnerTypeTradeDocument, ownDoc.Id, ownDoc.DocNo);
        SeedEvidence(db, AttachmentEvidenceRules.OwnerTypeTradeDocument, unlinkedDoc.Id, unlinkedDoc.DocNo);
        var foreignDocEvidence = SeedEvidence(
            db, AttachmentEvidenceRules.OwnerTypeTradeDocument, foreignDoc.Id, foreignDoc.DocNo);
        SeedEvidence(db, AttachmentEvidenceRules.OwnerTypeSample, ownSample.Id, ownSample.SampleNo);
        SeedEvidence(db, AttachmentEvidenceRules.OwnerTypeSample, unlinkedSample.Id, unlinkedSample.SampleNo);

        var controller = BuildController(db, store, user.Id);

        Assert.Single(AssertOk<List<AttachmentEvidenceDto>>(await controller.GetForOwner(
            AttachmentEvidenceRules.OwnerTypeTradeDocument, ownDoc.Id)));
        Assert.Single(AssertOk<List<AttachmentEvidenceDto>>(await controller.GetForOwner(
            AttachmentEvidenceRules.OwnerTypeSample, ownSample.Id)));

        foreach (var (ownerType, ownerId) in new[]
                 {
                     (AttachmentEvidenceRules.OwnerTypeTradeDocument, unlinkedDoc.Id),
                     (AttachmentEvidenceRules.OwnerTypeTradeDocument, foreignDoc.Id),
                     (AttachmentEvidenceRules.OwnerTypeSample, unlinkedSample.Id)
                 })
        {
            await AssertBusinessAsync(ErrorCodes.Forbidden, () => controller.GetForOwner(ownerType, ownerId));
        }

        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.GetById(foreignDocEvidence.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.DownloadContent(foreignDocEvidence.Id));
        Assert.Equal(0, store.OpenCalls);

        // 特权账号（系统内置角色）仍按菜单收敛，但保留历史无权威归属单证的可读性
        var privilegedId = SeedPrivilegedUser(db, AttachmentEvidenceRules.MenuCodeTradeDocument);
        var privileged = BuildController(db, store, privilegedId);
        Assert.Single(AssertOk<List<AttachmentEvidenceDto>>(await privileged.GetForOwner(
            AttachmentEvidenceRules.OwnerTypeTradeDocument, unlinkedDoc.Id)));
        Assert.Equal(ownDocEvidence.Id, AssertOk<AttachmentEvidenceDto>(
            await privileged.GetById(ownDocEvidence.Id)).Id);
    }

    // ==================== 4. 附件中心工作台 ====================

    [Fact]
    public async Task 附件中心_未授权类型越界归属与禁用身份一律不披露()
    {
        using var db = TestDbFactory.Create();
        var store = new CountingStore();
        var (user, employee) = SeedSalesman(db, "erp407-center",
            AttachmentEvidenceRules.MenuCodeTradeDocument);
        var own = SeedCustomer(db, OwnCustomerCode, employee.Id);
        var foreign = SeedCustomer(db, ForeignCustomerCode, null);

        var ownDoc = SeedTradeDocument(db, "DOC-407-CENTER-OWN", own.Id);
        var foreignDoc = SeedTradeDocument(db, "DOC-407-CENTER-FOREIGN", foreign.Id);
        var unlinkedDoc = SeedTradeDocument(db, "DOC-407-CENTER-UNLINKED", null);
        SeedEvidence(db, AttachmentEvidenceRules.OwnerTypeTradeDocument, ownDoc.Id, ownDoc.DocNo);
        var foreignEvidence = SeedEvidence(
            db, AttachmentEvidenceRules.OwnerTypeTradeDocument, foreignDoc.Id, foreignDoc.DocNo);
        SeedEvidence(db, AttachmentEvidenceRules.OwnerTypeTradeDocument, unlinkedDoc.Id, unlinkedDoc.DocNo);

        var controller = BuildController(db, store, user.Id);

        // 工作台台账：只保留范围内归属（越界与无权威归属都不计）
        var page = AssertOk<PagedResult<AttachmentEvidenceDto>>(
            await controller.GetCenterPaged(new AttachmentEvidenceCenterQuery()));
        Assert.Equal(1, page.Total);

        var summary = AssertOk<AttachmentEvidenceCenterSummaryDto>(await controller.GetCenterSummary());
        Assert.Equal(1, summary.TotalCount);
        Assert.Equal(new[] { AttachmentEvidenceRules.OwnerTypeTradeDocument },
            summary.OwnerTypeCounts.Select(c => c.OwnerType).ToArray());

        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.GetCenterDetail(foreignEvidence.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.DownloadCenterContent(foreignEvidence.Id));
        Assert.Equal(0, store.OpenCalls);

        // 无菜单账号：工作台不显示任何记录与计数（fail closed）
        var (noMenu, _) = SeedSalesman(db, "erp407-center-nomenu");
        var noMenuController = BuildController(db, store, noMenu.Id);
        Assert.Equal(0, AssertOk<PagedResult<AttachmentEvidenceDto>>(
            await noMenuController.GetCenterPaged(new AttachmentEvidenceCenterQuery())).Total);
        Assert.False(AssertOk<AttachmentEvidenceCenterSummaryDto>(
            await noMenuController.GetCenterSummary()).Scope.HasAnyAuthorizedOwnerType);
        await AssertBusinessAsync(ErrorCodes.NotFound, () =>
            noMenuController.GetCenterDetail(foreignEvidence.Id));
    }

    // ==================== 5. 上传 / 下载：拒绝路径零存储访问 ====================

    [Fact]
    public async Task 上传_本人成功_越界与无菜单一律拒绝且不保存任何内容()
    {
        using var db = TestDbFactory.Create();
        var store = new CountingStore();
        var (user, employee) = SeedSalesman(db, "erp407-upload", AttachmentEvidenceRules.MenuCodeSalesOrder);
        var own = SeedCustomer(db, OwnCustomerCode, employee.Id);
        var foreign = SeedCustomer(db, ForeignCustomerCode, null);
        var ownOrder = SeedSalesOrder(db, "SO-407-UPLOAD-OWN", own.Id);
        var foreignOrder = SeedSalesOrder(db, "SO-407-UPLOAD-FOREIGN", foreign.Id);

        var controller = BuildController(db, store, user.Id);

        await AssertBusinessAsync(ErrorCodes.Forbidden, () => controller.Upload(
            TestFile(fileName: "越界证据.pdf"), AttachmentEvidenceRules.OwnerTypeSalesOrder, foreignOrder.Id, "说明"));
        Assert.Equal(0, store.SaveCalls);
        Assert.Empty(db.AttachmentEvidences.AsNoTracking().ToList());

        var uploaded = AssertOk<AttachmentEvidenceDto>(await controller.Upload(
            TestFile(fileName: "本人证据.pdf"), AttachmentEvidenceRules.OwnerTypeSalesOrder, ownOrder.Id, "说明"));
        Assert.Equal(1, store.SaveCalls);
        Assert.Equal(AttachmentEvidenceRules.StatusActive, uploaded.Status);
        Assert.Equal(AttachmentEvidenceRules.OwnerTypeSalesOrder, uploaded.OwnerType);
        Assert.Equal(ownOrder.OrderNo, uploaded.OwnerNo);
    }

    [Fact]
    public async Task 下载_本人成功_越界一律拒绝且不打开存储对象()
    {
        using var db = TestDbFactory.Create();
        var store = new CountingStore();
        var (user, employee) = SeedSalesman(db, "erp407-download", AttachmentEvidenceRules.MenuCodeSalesOrder);
        var own = SeedCustomer(db, OwnCustomerCode, employee.Id);
        var foreign = SeedCustomer(db, ForeignCustomerCode, null);
        var ownOrder = SeedSalesOrder(db, "SO-407-DL-OWN", own.Id);
        var foreignOrder = SeedSalesOrder(db, "SO-407-DL-FOREIGN", foreign.Id);

        var controller = BuildController(db, store, user.Id);
        var ownEvidence = AssertOk<AttachmentEvidenceDto>(await controller.Upload(
            TestFile(fileName: "下载证据.pdf"), AttachmentEvidenceRules.OwnerTypeSalesOrder, ownOrder.Id, "说明"));
        var foreignEvidence = SeedEvidence(
            db, AttachmentEvidenceRules.OwnerTypeSalesOrder, foreignOrder.Id, foreignOrder.OrderNo);

        var download = Assert.IsType<FileStreamResult>(await controller.DownloadContent(ownEvidence.Id));
        await download.FileStream.DisposeAsync();
        Assert.Equal(1, store.OpenCalls);

        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.DownloadContent(foreignEvidence.Id));
        Assert.Equal(1, store.OpenCalls);
    }

    // ==================== 6. 只读边界与接线契约 ====================

    [Fact]
    public async Task 被拒绝的请求不改写父单据库存财务与相邻业务记录()
    {
        using var db = TestDbFactory.Create();
        var store = new CountingStore();
        var (user, employee) = SeedSalesman(db, "erp407-readonly", AttachmentEvidenceRules.MenuCodeSalesOrder);
        var own = SeedCustomer(db, OwnCustomerCode, employee.Id);
        var foreign = SeedCustomer(db, ForeignCustomerCode, null);
        var foreignOrder = SeedSalesOrder(db, "SO-407-RO-FOREIGN", foreign.Id);

        db.Stocks.Add(new Stock
        {
            WarehouseId = 1, ProductId = 9, Quantity = 5m, AvailableQuantity = 5m, LockedQuantity = 0m
        });
        db.FinanceExpenses.Add(new FinanceExpense
        {
            ExpenseNo = "FY-407", ExpenseDate = DateTime.Today, ExpenseType = "报关费",
            Amount = 10m, Currency = "CNY", AmountCny = 10m, PaymentStatus = "未付"
        });
        db.SaveChanges();
        Assert.NotEqual(0, own.Id);

        var foreignEvidence = SeedEvidence(
            db, AttachmentEvidenceRules.OwnerTypeSalesOrder, foreignOrder.Id, foreignOrder.OrderNo);

        static string Snapshot(ErpDbContext ctx)
            => string.Join("|",
                ctx.SalesOrders.AsNoTracking().OrderBy(o => o.Id)
                    .Select(o => $"{o.Id}:{o.CustomerId}:{o.Status}:{o.TotalAmount}:{o.IsDeleted}"),
                ctx.PurchaseOrders.AsNoTracking().OrderBy(o => o.Id)
                    .Select(o => $"{o.Id}:{o.OwningCustomerId}:{o.OwningSalesOrderId}:{o.IsDeleted}"),
                ctx.TradeDocuments.AsNoTracking().OrderBy(d => d.Id)
                    .Select(d => $"{d.Id}:{d.CustomerId}:{d.Status}"),
                ctx.Samples.AsNoTracking().OrderBy(s => s.Id).Select(s => $"{s.Id}:{s.CustomerId}"),
                ctx.Stocks.AsNoTracking().OrderBy(s => s.Id).Select(s => $"{s.Id}:{s.Quantity}"),
                ctx.FinanceExpenses.AsNoTracking().OrderBy(e => e.Id).Select(e => $"{e.Id}:{e.Amount}"));

        var before = Snapshot(db);
        var controller = BuildController(db, store, user.Id);

        await AssertBusinessAsync(ErrorCodes.Forbidden, () =>
            controller.GetForOwner(AttachmentEvidenceRules.OwnerTypeSalesOrder, foreignOrder.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.GetById(foreignEvidence.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.DownloadContent(foreignEvidence.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound, () =>
            controller.Void(foreignEvidence.Id, new AttachmentEvidenceVoidRequest { Reason = "越界作废" }));
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => controller.Upload(
            TestFile(), AttachmentEvidenceRules.OwnerTypeSalesOrder, foreignOrder.Id, "说明"));

        Assert.Equal(before, Snapshot(db));
        Assert.Equal(AttachmentEvidenceRules.StatusActive,
            db.AttachmentEvidences.AsNoTracking().Single(r => r.Id == foreignEvidence.Id).Status);
        Assert.Equal(0, store.SaveCalls);
        Assert.Equal(0, store.OpenCalls);
    }

    [Fact]
    public void 控制器契约_每个普通与中心路由都解析权威访问上下文且中心只读()
    {
        var source = File.ReadAllText(RepoFile(
            "src", "ERP.Api", "Controllers", "AttachmentEvidenceController.cs"));

        // 普通路由逐一声明权威访问上下文（上传 / 台账 / 按归属 / 候选 / 摘要 / 详情 / 下载 / 作废）
        var resolutions = source.Split("ResolveAccessAsync(cancellationToken)").Length - 1;
        Assert.True(resolutions >= 7, $"普通路由解析权威访问上下文的次数不足：{resolutions}");
        Assert.Contains("EnsureLiveIdentityAsync", source);                 // 模块元数据也要求实时身份
        Assert.Contains("AttachmentOwnerAuthorizationRules.ResolveAsync", source);
        Assert.DoesNotContain("[HttpDelete", source);                        // 不提供硬删除
        Assert.DoesNotContain("[HttpPut", source);                           // 不提供二进制替换 / 改派
    }

    [Fact]
    public void 纯规则_归属类型与既有菜单映射_Unknown类型不借用他类权限()
    {
        Assert.Equal(AttachmentEvidenceRules.MenuCodeSalesOrder,
            AttachmentEvidenceRules.RequiredMenuCodeOf(AttachmentEvidenceRules.OwnerTypeSalesOrder));
        Assert.Equal(AttachmentEvidenceRules.MenuCodePurchaseOrder,
            AttachmentEvidenceRules.RequiredMenuCodeOf(AttachmentEvidenceRules.OwnerTypeQualityInspection));
        Assert.Equal(AttachmentEvidenceRules.MenuCodeTradeDocument,
            AttachmentEvidenceRules.RequiredMenuCodeOf(AttachmentEvidenceRules.OwnerTypeTradeDocument));
        Assert.Equal(AttachmentEvidenceRules.MenuCodeSample,
            AttachmentEvidenceRules.RequiredMenuCodeOf(AttachmentEvidenceRules.OwnerTypeSample));
        Assert.Equal(string.Empty, AttachmentEvidenceRules.RequiredMenuCodeOf("UnknownType"));
        Assert.Contains("fail closed", AttachmentOwnerAuthorizationRules.RuleText);
    }

    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));
}

