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
/// ERP-408 附件引用登记授权的真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实规则 + 真实控制器</b>：以既有菜单（<c>sales-order</c> / <c>purchase-order</c> /
/// <c>loading-list</c> / <c>doc-center</c>）与真实业务员数据范围（两个客户）验证台账 / 按父单据清单 /
/// 父单据候选 / 详情 / 登记 / 作废的本人可用与越界 fail closed；直接知道父单据 Id 或引用 Id 也不能绕过；
/// 撤销菜单 / 禁用账号立即收敛。</item>
/// <item><b>全父单据家族</b>：销售订单 / 采购订单 / 装柜清单（参与方 + 显式上游订柜）/ 出口单证。</item>
/// <item><b>零落库</b>：被拒绝的创建 / 作废 / 读取既不改写引用行，也不改写任何父单据。</item>
/// <item><b>两个独立连接竞态</b>：① 两条独立连接并发伪造越界创建 → 二者都拒绝且零落库；
/// ② 越界作废与本人作废并发 → 越界始终拒绝、本人作废生效，父单据归属从未被改写（无撕裂）。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>保留既有库存来源单据审计与原始失败日志：本测试只新增自己的引用行，不清理 / 不删除任何既有行。
/// 构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class DocumentAttachmentReferenceAuthorizationSqlServerTests
    : IClassFixture<DocumentAttachmentReferenceAuthorizationSqlServerFixture>
{
    private readonly DocumentAttachmentReferenceAuthorizationSqlServerFixture _fixture;

    public DocumentAttachmentReferenceAuthorizationSqlServerTests(
        DocumentAttachmentReferenceAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(DocumentAttachmentReferenceAuthorizationSqlServerFixture.DatabasePrefix,
            target.InitialCatalog, StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 控制器 / 独立连接脚手架 ====================

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

    /// <summary>用一条独立连接执行控制器动作（每次调用各自 DbContext / 连接），返回成功标志与错误。</summary>
    private async Task<(bool Success, string Error)> TryAsync(
        long? userId, Func<DocumentAttachmentReferenceController, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await action(NewController(db, userId));
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
        var code = $"INT_E408_EMP_{Tag()}";
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
            RoleName = $"INT_E408_ROLE_{code}", RoleCode = $"INT_E408_{Guid.NewGuid():N}", IsSystem = false
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
            Currency = Currency.USD, TotalAmount = 1000m, Status = DocumentStatus.Approved
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
            TotalAmount = 500m, Status = DocumentStatus.Approved,
            OwningCustomerId = owningCustomerId, OwningSalesOrderId = owningSalesOrderId,
            ArrivalProgress = "未到货", QcStatus = "未验货"
        };
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static async Task<ContainerLoadingList> SeedLoadingListAsync(
        ErpDbContext db, string loadingListNo, long customerId,
        params (long CustomerId, int Status)[] participants)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = loadingListNo, LoadingDate = DateTime.Today,
            ContainerNo = "INTE4080001", CustomerId = customerId, Status = DocumentStatus.Approved
        };
        db.ContainerLoadingLists.Add(list);
        await db.SaveChangesAsync();

        foreach (var (customerIdOfParticipant, status) in participants)
        {
            db.ContainerLoadingListParticipants.Add(new ContainerLoadingListParticipant
            {
                LoadingListId = list.Id,
                CustomerId = customerIdOfParticipant,
                CustomerCode = $"C{customerIdOfParticipant}",
                CustomerName = $"C{customerIdOfParticipant}",
                Status = status
            });
        }

        await db.SaveChangesAsync();
        return list;
    }

    /// <summary>播种显式上游：装柜清单 → 预装柜单 → 订柜信息（订柜客户即显式上游权威客户）。</summary>
    private static async Task SeedUpstreamBookingAsync(
        ErpDbContext db, ContainerLoadingList list, long bookingCustomerId)
    {
        var booking = new ContainerBooking
        {
            BookingNo = $"INT_E408_BK_{Tag()}", BookingDate = DateTime.Today, CustomerId = bookingCustomerId
        };
        db.ContainerBookings.Add(booking);
        await db.SaveChangesAsync();

        var preLoading = new ContainerPreLoading
        {
            PreLoadingNo = $"INT_E408_PL_{Tag()}", LoadingDate = DateTime.Today, BookingId = booking.Id
        };
        db.ContainerPreLoadings.Add(preLoading);
        await db.SaveChangesAsync();

        list.PreLoadingId = preLoading.Id;
        await db.SaveChangesAsync();
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

    private static async Task<DocumentAttachmentReference> SeedReferenceAsync(
        ErpDbContext db, string parentType, long parentId, string parentNo, string referenceId)
    {
        var row = new DocumentAttachmentReference
        {
            ParentType = parentType,
            ParentId = parentId,
            ParentNo = parentNo,
            ParentTypeText = DocumentAttachmentReferenceRules.ParentTypeText(parentType),
            Category = DocumentAttachmentReferenceRules.CategoryContract,
            DisplayName = $"集成引用 {referenceId}",
            ReferenceId = referenceId,
            SourceAuthorizationAcknowledged = true,
            SourceAuthorizationNote = "集成测试留痕",
            AuthorizedBy = "集成登记人",
            AuthorizedAt = DateTime.Now,
            RegisteredAt = DateTime.Now,
            Status = DocumentAttachmentReferenceRules.StatusActive
        };
        db.DocumentAttachmentReferences.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    private static DocumentAttachmentReferenceSaveDto DarDto(
        string parentType, long parentId, string referenceId, string displayName = "集成合同扫描件")
        => new()
        {
            ParentType = parentType,
            ParentId = parentId,
            Category = DocumentAttachmentReferenceRules.CategoryContract,
            DisplayName = displayName,
            ReferenceId = referenceId,
            SourceAuthorizationAcknowledged = true,
            SourceAuthorizationNote = "集成测试来源授权留痕",
            AuthorizedBy = "集成登记人"
        };

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
        long userId, roleId, customerId, orderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, role) = await SeedSalesmanAsync(
                seed, DocumentAttachmentReferenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            roleId = role.Id;
            customerId = (await SeedCustomerAsync(seed, $"INT_E408_C_{tag}", employee.Id)).Id;
            var order = await SeedSalesOrderAsync(seed, $"INT_E408_SO_{tag}", customerId);
            orderId = order.Id;
            await SeedReferenceAsync(seed, DocumentAttachmentReferenceRules.ParentTypeSalesOrder,
                order.Id, order.OrderNo, $"int-e408-{tag}");
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var access = await AttachmentOwnerAuthorizationRules.ResolveDocumentReferenceAccessAsync(db, userId);
            Assert.False(access.IsPrivileged);
            Assert.True(access.Scope.AllowsCustomer(customerId));
            Assert.Equal(new[] { DocumentAttachmentReferenceRules.ParentTypeSalesOrder },
                access.AuthorizedParentTypes.ToArray());

            var controller = NewController(db, userId);
            var page = Assert.IsType<ApiResponse<PagedResult<DocumentAttachmentReferenceDto>>>(
                ((OkObjectResult)await controller.GetPaged(new DocumentAttachmentReferenceQuery())).Value).Data!;
            Assert.Equal(1, page.Total);

            var byParent = Assert.IsType<ApiResponse<List<DocumentAttachmentReferenceDto>>>(
                ((OkObjectResult)await controller.GetForParent(
                    DocumentAttachmentReferenceRules.ParentTypeSalesOrder, orderId)).Value).Data!;
            Assert.Single(byParent);
        }

        await using (var revoke = _fixture.CreateDbContext())
            await RevokeMenusAsync(revoke, roleId);

        await using (var db = _fixture.CreateDbContext())
        {
            var access = await AttachmentOwnerAuthorizationRules.ResolveDocumentReferenceAccessAsync(db, userId);
            Assert.Empty(access.AuthorizedParentTypes);
        }

        long disabledId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, _, _) = await SeedSalesmanAsync(seed, DocumentAttachmentReferenceRules.MenuCodeSalesOrder);
            user.Status = UserStatus.Disabled;
            await seed.SaveChangesAsync();
            disabledId = user.Id;
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(
                () => AttachmentOwnerAuthorizationRules.ResolveDocumentReferenceAccessAsync(db, disabledId));
            Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        }
    }

    // ==================== 2. 两个客户：四类父单据 + 拒绝零落库 ====================

    [Fact]
    public async Task 两客户_四类父单据_本人可用_越界一律fail_closed且零落库()
    {
        Guard();
        var tag = Tag();
        long userId;
        long ownOrderId, foreignOrderId, ownPoId, foreignPoId;
        long ownListId, sharedListId, upstreamListId, ownDocId;
        long ownSoReferenceId, foreignSoReferenceId, ownPoReferenceId, foreignPoReferenceId;
        long ownListReferenceId, sharedListReferenceId, upstreamListReferenceId;
        long ownDocReferenceId, foreignDocReferenceId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedSalesmanAsync(seed,
                DocumentAttachmentReferenceRules.MenuCodeSalesOrder,
                DocumentAttachmentReferenceRules.MenuCodePurchaseOrder,
                DocumentAttachmentReferenceRules.MenuCodeContainerLoadingList,
                DocumentAttachmentReferenceRules.MenuCodeTradeDocument);
            userId = user.Id;
            var own = await SeedCustomerAsync(seed, $"INT_E408_OWN_{tag}", employee.Id);
            var foreign = await SeedCustomerAsync(seed, $"INT_E408_FOREIGN_{tag}", null);

            var ownSo = await SeedSalesOrderAsync(seed, $"INT_E408_SO_OWN_{tag}", own.Id);
            var foreignSo = await SeedSalesOrderAsync(seed, $"INT_E408_SO_FOREIGN_{tag}", foreign.Id);
            ownOrderId = ownSo.Id;
            foreignOrderId = foreignSo.Id;
            ownSoReferenceId = (await SeedReferenceAsync(seed, DocumentAttachmentReferenceRules.ParentTypeSalesOrder,
                ownSo.Id, ownSo.OrderNo, $"int-e408-so-own-{tag}")).Id;
            foreignSoReferenceId = (await SeedReferenceAsync(seed, DocumentAttachmentReferenceRules.ParentTypeSalesOrder,
                foreignSo.Id, foreignSo.OrderNo, $"int-e408-so-foreign-{tag}")).Id;

            var ownPo = await SeedPurchaseOrderAsync(seed, $"INT_E408_PO_OWN_{tag}", own.Id, ownSo.Id);
            var foreignPo = await SeedPurchaseOrderAsync(seed, $"INT_E408_PO_FOREIGN_{tag}", foreign.Id, foreignSo.Id);
            ownPoId = ownPo.Id;
            foreignPoId = foreignPo.Id;
            ownPoReferenceId = (await SeedReferenceAsync(seed, DocumentAttachmentReferenceRules.ParentTypePurchaseOrder,
                ownPo.Id, ownPo.OrderNo, $"int-e408-po-own-{tag}")).Id;
            foreignPoReferenceId = (await SeedReferenceAsync(seed, DocumentAttachmentReferenceRules.ParentTypePurchaseOrder,
                foreignPo.Id, foreignPo.OrderNo, $"int-e408-po-foreign-{tag}")).Id;

            var ownList = await SeedLoadingListAsync(seed, $"INT_E408_LL_OWN_{tag}", own.Id,
                (own.Id, ContainerLoadingParticipantRules.ActiveStatus));
            var sharedList = await SeedLoadingListAsync(seed, $"INT_E408_LL_SHARED_{tag}", own.Id,
                (own.Id, ContainerLoadingParticipantRules.ActiveStatus),
                (foreign.Id, ContainerLoadingParticipantRules.ActiveStatus));
            var upstreamList = await SeedLoadingListAsync(seed, $"INT_E408_LL_UP_{tag}", own.Id);
            await SeedUpstreamBookingAsync(seed, upstreamList, foreign.Id);
            ownListId = ownList.Id;
            sharedListId = sharedList.Id;
            upstreamListId = upstreamList.Id;
            ownListReferenceId = (await SeedReferenceAsync(seed, DocumentAttachmentReferenceRules.ParentTypeContainerLoadingList,
                ownList.Id, ownList.LoadingListNo, $"int-e408-ll-own-{tag}")).Id;
            sharedListReferenceId = (await SeedReferenceAsync(seed, DocumentAttachmentReferenceRules.ParentTypeContainerLoadingList,
                sharedList.Id, sharedList.LoadingListNo, $"int-e408-ll-shared-{tag}")).Id;
            upstreamListReferenceId = (await SeedReferenceAsync(seed, DocumentAttachmentReferenceRules.ParentTypeContainerLoadingList,
                upstreamList.Id, upstreamList.LoadingListNo, $"int-e408-ll-up-{tag}")).Id;

            var ownDoc = await SeedTradeDocumentAsync(seed, $"INT_E408_DOC_OWN_{tag}", own.Id);
            var foreignDoc = await SeedTradeDocumentAsync(seed, $"INT_E408_DOC_FOREIGN_{tag}", foreign.Id);
            ownDocId = ownDoc.Id;
            ownDocReferenceId = (await SeedReferenceAsync(seed, DocumentAttachmentReferenceRules.ParentTypeTradeDocument,
                ownDoc.Id, ownDoc.DocNo, $"int-e408-doc-own-{tag}")).Id;
            foreignDocReferenceId = (await SeedReferenceAsync(seed, DocumentAttachmentReferenceRules.ParentTypeTradeDocument,
                foreignDoc.Id, foreignDoc.DocNo, $"int-e408-doc-foreign-{tag}")).Id;
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var controller = NewController(db, userId);
            var page = Assert.IsType<ApiResponse<PagedResult<DocumentAttachmentReferenceDto>>>(
                ((OkObjectResult)await controller.GetPaged(new DocumentAttachmentReferenceQuery())).Value).Data!;
            Assert.Equal(4, page.Total);   // 四类父单据各一条本人记录；共享柜 / 上游越界 / 越界客户不计
        }

        // 本人可用（真实控制器 + 每次独立连接）
        Assert.True((await TryAsync(userId, c => c.GetPaged(new DocumentAttachmentReferenceQuery()))).Success);
        Assert.True((await TryAsync(userId, c => c.GetById(ownSoReferenceId))).Success);
        Assert.True((await TryAsync(userId, c => c.GetForParent(
            DocumentAttachmentReferenceRules.ParentTypeSalesOrder, ownOrderId))).Success);
        Assert.True((await TryAsync(userId, c => c.ParentOptions(
            DocumentAttachmentReferenceRules.ParentTypeContainerLoadingList, null))).Success);

        // 越界客户 / 共享柜 / 上游越界 / 伪造创建：一律 fail closed
        Assert.False((await TryAsync(userId, c => c.GetById(foreignSoReferenceId))).Success);
        Assert.False((await TryAsync(userId, c => c.GetById(foreignPoReferenceId))).Success);
        Assert.False((await TryAsync(userId, c => c.GetById(sharedListReferenceId))).Success);
        Assert.False((await TryAsync(userId, c => c.GetById(upstreamListReferenceId))).Success);
        Assert.False((await TryAsync(userId, c => c.GetById(foreignDocReferenceId))).Success);
        Assert.False((await TryAsync(userId, c => c.GetForParent(
            DocumentAttachmentReferenceRules.ParentTypeSalesOrder, foreignOrderId))).Success);
        Assert.False((await TryAsync(userId, c => c.Void(
            foreignSoReferenceId, new DocumentAttachmentReferenceVoidRequest { Reason = "越界作废" }))).Success);
        Assert.False((await TryAsync(userId, c => c.Create(DarDto(
            DocumentAttachmentReferenceRules.ParentTypeSalesOrder, foreignOrderId,
            $"int-e408-forged-{tag}")))).Success);
        Assert.False((await TryAsync(userId, c => c.Create(DarDto(
            DocumentAttachmentReferenceRules.ParentTypeContainerLoadingList, sharedListId,
            $"int-e408-forged-ll-{tag}")))).Success);

        // 授权登记生效（本人父单据）
        Assert.True((await TryAsync(userId, c => c.Create(DarDto(
            DocumentAttachmentReferenceRules.ParentTypeTradeDocument, ownDocId,
            $"int-e408-new-{tag}")))).Success);

        await using (var verify = _fixture.CreateDbContext())
        {
            // 拒绝的调用零改写：越界引用仍有效、伪造引用不存在
            Assert.Equal(DocumentAttachmentReferenceRules.StatusActive,
                (await verify.DocumentAttachmentReferences.AsNoTracking()
                    .SingleAsync(r => r.Id == foreignSoReferenceId)).Status);
            Assert.False(await verify.DocumentAttachmentReferences.AsNoTracking()
                .AnyAsync(r => r.ReferenceId == $"int-e408-forged-{tag}"));
            Assert.False(await verify.DocumentAttachmentReferences.AsNoTracking()
                .AnyAsync(r => r.ReferenceId == $"int-e408-forged-ll-{tag}"));

            // 授权登记恰好新增一条（本人父单据，归属与快照正确）
            var created = await verify.DocumentAttachmentReferences.AsNoTracking()
                .SingleAsync(r => r.ReferenceId == $"int-e408-new-{tag}");
            Assert.Equal(DocumentAttachmentReferenceRules.ParentTypeTradeDocument, created.ParentType);
            Assert.Equal(ownDocId, created.ParentId);
            Assert.Equal(DocumentAttachmentReferenceRules.StatusActive, created.Status);
        }
    }

    // ==================== 3. 竞态一：两条独立连接并发伪造越界创建 ====================

    [Fact]
    public async Task 竞态一_两条独立连接并发伪造越界创建_二者都拒绝且零落库()
    {
        Guard();
        var tag = Tag();
        long userId, foreignOrderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedSalesmanAsync(
                seed, DocumentAttachmentReferenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            await SeedCustomerAsync(seed, $"INT_E408_R1_OWN_{tag}", employee.Id);
            var foreign = await SeedCustomerAsync(seed, $"INT_E408_R1_FOREIGN_{tag}", null);
            foreignOrderId = (await SeedSalesOrderAsync(seed, $"INT_E408_R1_SO_{tag}", foreign.Id)).Id;
        }

        var referenceIdLeft = $"int-e408-r1-left-{tag}";
        var referenceIdRight = $"int-e408-r1-right-{tag}";
        var results = await RaceAsync(
            () => TryAsync(userId, c => c.Create(DarDto(
                DocumentAttachmentReferenceRules.ParentTypeSalesOrder, foreignOrderId, referenceIdLeft))),
            () => TryAsync(userId, c => c.Create(DarDto(
                DocumentAttachmentReferenceRules.ParentTypeSalesOrder, foreignOrderId, referenceIdRight))));

        Assert.False(results[0].Success);
        Assert.False(results[1].Success);

        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.False(await verify.DocumentAttachmentReferences.AsNoTracking()
                .AnyAsync(r => r.ReferenceId == referenceIdLeft));
            Assert.False(await verify.DocumentAttachmentReferences.AsNoTracking()
                .AnyAsync(r => r.ReferenceId == referenceIdRight));
        }
    }

    // ==================== 4. 竞态二：越界作废与本人作废并发 ====================

    [Fact]
    public async Task 竞态二_越界作废与本人作废并发_越界始终拒绝本人作废生效且归属未改写()
    {
        Guard();
        var tag = Tag();
        long userId, ownReferenceId, foreignReferenceId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedSalesmanAsync(
                seed, DocumentAttachmentReferenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            var own = await SeedCustomerAsync(seed, $"INT_E408_R2_OWN_{tag}", employee.Id);
            var foreign = await SeedCustomerAsync(seed, $"INT_E408_R2_FOREIGN_{tag}", null);
            var ownOrder = await SeedSalesOrderAsync(seed, $"INT_E408_R2_SO_OWN_{tag}", own.Id);
            var foreignOrder = await SeedSalesOrderAsync(seed, $"INT_E408_R2_SO_FOREIGN_{tag}", foreign.Id);
            ownReferenceId = (await SeedReferenceAsync(seed, DocumentAttachmentReferenceRules.ParentTypeSalesOrder,
                ownOrder.Id, ownOrder.OrderNo, $"int-e408-r2-own-{tag}")).Id;
            foreignReferenceId = (await SeedReferenceAsync(seed, DocumentAttachmentReferenceRules.ParentTypeSalesOrder,
                foreignOrder.Id, foreignOrder.OrderNo, $"int-e408-r2-foreign-{tag}")).Id;
        }

        var results = await RaceAsync(
            () => TryAsync(userId, c => c.Void(
                foreignReferenceId, new DocumentAttachmentReferenceVoidRequest { Reason = "越界并发作废" })),
            () => TryAsync(userId, c => c.Void(
                ownReferenceId, new DocumentAttachmentReferenceVoidRequest { Reason = "本人并发作废" })));

        Assert.False(results[0].Success);   // 越界作废始终被拒绝
        Assert.True(results[1].Success);    // 本人作废在范围内引用上生效

        await using (var verify = _fixture.CreateDbContext())
        {
            var own = await verify.DocumentAttachmentReferences.AsNoTracking()
                .SingleAsync(r => r.Id == ownReferenceId);
            Assert.Equal(DocumentAttachmentReferenceRules.StatusVoided, own.Status);
            Assert.Equal("本人并发作废", own.VoidReason);

            var foreign = await verify.DocumentAttachmentReferences.AsNoTracking()
                .SingleAsync(r => r.Id == foreignReferenceId);
            Assert.Equal(DocumentAttachmentReferenceRules.StatusActive, foreign.Status);
            Assert.Equal(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, foreign.ParentType);
        }
    }

    // ==================== 5. 真实控制器往返：登记 + 显式作废保留原始证据 ====================

    [Fact]
    public async Task 真实控制器往返_授权登记与显式作废_保留原始证据且重复登记拒绝()
    {
        Guard();
        var tag = Tag();
        long userId, orderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedSalesmanAsync(
                seed, DocumentAttachmentReferenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            var own = await SeedCustomerAsync(seed, $"INT_E408_EV_{tag}", employee.Id);
            orderId = (await SeedSalesOrderAsync(seed, $"INT_E408_EV_SO_{tag}", own.Id)).Id;
        }

        var referenceId = $"int-e408-ev-{tag}";
        long createdId;
        await using (var db = _fixture.CreateDbContext())
        {
            var controller = NewController(db, userId);
            var created = Assert.IsType<ApiResponse<DocumentAttachmentReferenceDto>>(
                ((OkObjectResult)await controller.Create(DarDto(
                    DocumentAttachmentReferenceRules.ParentTypeSalesOrder, orderId, referenceId))).Value).Data!;
            createdId = created.Id;
            Assert.True(created.SourceAuthorizationAcknowledged);
            Assert.Equal("集成测试来源授权留痕", created.SourceAuthorizationNote);
            Assert.Equal("集成登记人", created.AuthorizedBy);
            Assert.Equal(DocumentAttachmentReferenceRules.CategoryContract, created.Category);
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var controller = NewController(db, userId);
            var duplicate = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(DarDto(
                DocumentAttachmentReferenceRules.ParentTypeSalesOrder, orderId, referenceId)));
            Assert.Equal(ErrorCodes.Duplicate, duplicate.Code);
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var controller = NewController(db, userId);
            var voided = Assert.IsType<ApiResponse<DocumentAttachmentReferenceDto>>(
                ((OkObjectResult)await controller.Void(createdId,
                    new DocumentAttachmentReferenceVoidRequest { Reason = "集成更正作废" })).Value).Data!;
            Assert.True(voided.IsVoided);
            Assert.Equal("集成更正作废", voided.VoidReason);
            Assert.Equal(referenceId, voided.ReferenceId);
            Assert.Equal("集成测试来源授权留痕", voided.SourceAuthorizationNote);
        }

        await using (var verify = _fixture.CreateDbContext())
        {
            var stored = await verify.DocumentAttachmentReferences.AsNoTracking()
                .SingleAsync(r => r.Id == createdId);
            Assert.Equal(DocumentAttachmentReferenceRules.StatusVoided, stored.Status);
            Assert.Equal(referenceId, stored.ReferenceId);
            Assert.Equal(DocumentAttachmentReferenceRules.CategoryContract, stored.Category);
            Assert.Equal("集成测试来源授权留痕", stored.SourceAuthorizationNote);

            // 作废后同一引用标识可重新登记（不静默合并、不覆盖历史）
            var controller = NewController(verify, userId);
            var reRegistered = Assert.IsType<ApiResponse<DocumentAttachmentReferenceDto>>(
                ((OkObjectResult)await controller.Create(DarDto(
                    DocumentAttachmentReferenceRules.ParentTypeSalesOrder, orderId, referenceId, "集成重登扫描件"))).Value).Data!;
            Assert.NotEqual(createdId, reRegistered.Id);

            Assert.Equal(2, await verify.DocumentAttachmentReferences.AsNoTracking()
                .CountAsync(r => r.ParentType == DocumentAttachmentReferenceRules.ParentTypeSalesOrder
                                 && r.ParentId == orderId && r.ReferenceId == referenceId));
        }
    }
}

/// <summary>
/// ERP-408 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class DocumentAttachmentReferenceAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_DAR_REF_AUTH_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-408] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-408] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }
}

/// <summary>ERP-408 专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class DocumentAttachmentReferenceTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => DocumentAttachmentReferenceAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}
