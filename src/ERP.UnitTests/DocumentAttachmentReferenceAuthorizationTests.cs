using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-408 附件引用登记（ERP-045）授权的单元测试（内存库 + 真实「角色 → 菜单」与 ERP-097 业务员数据范围）。
/// 覆盖：缺失 / 非法 / 已删除 / 已禁用身份；无菜单与撤销菜单立即收敛；两个客户下台账计数 / 父单据候选 /
/// 详情 / 按父单据清单 / 作废 / 伪造创建的范围收敛；四类父单据家族（销售订单 / 采购订单 / 装柜清单 /
/// 出口单证）的权威归属复核（采购订单两侧归属与无归属备货采购、装柜清单参与方与上游订柜、单证客户缺失）；
/// 父单据删除后历史行保留且受限账号 fail closed；重复登记拒绝；显式作废保留原始证据；被拒绝调用零落库。
/// <para>全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何 SQL / 迁移 / 部署脚本，也不访问对象存储。</para>
/// </summary>
public class DocumentAttachmentReferenceAuthorizationTests
{
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    // ==================== 0. 脚手架 ====================

    /// <summary>授予既有模块菜单（复用既有「角色 → 菜单」授权，缺菜单时补一条功能菜单，不新增权限模型）。</summary>
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

        if (db.SysRoleMenus.Any(rm => rm.RoleId == roleId && rm.MenuId == menu.Id && !rm.IsDeleted)) return;
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
        db.SaveChanges();
    }

    /// <summary>播种受限业务员账号（登录名 = 员工编码，ERP-097 权威映射）并按需授予既有模块菜单。</summary>
    private static (SysUser User, BaseEmployee Employee, SysRole Role) SeedSalesman(
        ErpDbContext db, params string[] menuCodes)
    {
        var code = $"ERP408-EMP-{Tag()}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = code,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = code,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = $"R-{code}", RoleCode = $"R-{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        foreach (var menuCode in menuCodes) GrantMenu(db, role.Id, menuCode);
        return (user, employee, role);
    }

    /// <summary>播种系统内置（特权）账号：客户范围不受限，但菜单仍是唯一收敛维度。</summary>
    private static long SeedPrivilegedUser(ErpDbContext db, params string[] menuCodes)
    {
        var user = new SysUser
        {
            UserName = $"erp408-admin-{Tag()}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "ERP-408 特权账号",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = "ERP-408 系统角色", RoleCode = $"SYS-{Guid.NewGuid():N}", IsSystem = true
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        foreach (var menuCode in menuCodes) GrantMenu(db, role.Id, menuCode);
        return user.Id;
    }

    private static void RevokeMenus(ErpDbContext db, long roleId)
    {
        foreach (var grant in db.SysRoleMenus.Where(rm => rm.RoleId == roleId && !rm.IsDeleted).ToList())
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

    private static SalesOrder SeedSalesOrder(ErpDbContext db, string orderNo, long customerId, bool deleted = false)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo, OrderDate = DateTime.Today, CustomerId = customerId,
            Currency = Currency.USD, TotalAmount = 1000m,
            Status = DocumentStatus.Approved, IsDeleted = deleted
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
            TotalAmount = 500m, Status = DocumentStatus.Approved,
            OwningCustomerId = owningCustomerId, OwningSalesOrderId = owningSalesOrderId,
            ArrivalProgress = "未到货", QcStatus = "未验货"
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static ContainerLoadingList SeedLoadingList(
        ErpDbContext db, string loadingListNo, long customerId,
        params (long CustomerId, int Status)[] participants)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = loadingListNo, LoadingDate = DateTime.Today,
            ContainerNo = "MSKU4080001", CustomerId = customerId,
            Status = DocumentStatus.Approved
        };
        db.ContainerLoadingLists.Add(list);
        db.SaveChanges();

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

        db.SaveChanges();
        return list;
    }

    /// <summary>播种显式上游：装柜清单 → 预装柜单 → 订柜信息（订柜客户即显式上游权威客户）。</summary>
    private static void SeedUpstreamBooking(ErpDbContext db, ContainerLoadingList list, long bookingCustomerId)
    {
        var booking = new ContainerBooking
        {
            BookingNo = $"BK-{Tag()}", BookingDate = DateTime.Today, CustomerId = bookingCustomerId
        };
        db.ContainerBookings.Add(booking);
        db.SaveChanges();

        var preLoading = new ContainerPreLoading
        {
            PreLoadingNo = $"PL-{Tag()}", LoadingDate = DateTime.Today, BookingId = booking.Id
        };
        db.ContainerPreLoadings.Add(preLoading);
        db.SaveChanges();

        list.PreLoadingId = preLoading.Id;
        db.SaveChanges();
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

    /// <summary>直接播种附件引用行（模拟历史 / 外部数据；不经过登记接口）。</summary>
    private static DocumentAttachmentReference SeedReference(
        ErpDbContext db, string parentType, long parentId, string parentNo, string referenceId)
    {
        var row = new DocumentAttachmentReference
        {
            ParentType = parentType,
            ParentId = parentId,
            ParentNo = parentNo,
            ParentTypeText = DocumentAttachmentReferenceRules.ParentTypeText(parentType),
            Category = DocumentAttachmentReferenceRules.CategoryContract,
            DisplayName = "历史扫描件",
            ReferenceId = referenceId,
            SourceAuthorizationAcknowledged = true,
            SourceAuthorizationNote = "历史留痕",
            AuthorizedBy = "历史登记人",
            AuthorizedAt = DateTime.Now,
            RegisteredAt = DateTime.Now,
            Status = DocumentAttachmentReferenceRules.StatusActive
        };
        db.DocumentAttachmentReferences.Add(row);
        db.SaveChanges();
        return row;
    }

    private static DocumentAttachmentReferenceSaveDto DarDto(
        string parentType, long parentId, string referenceId = "att-001",
        string displayName = "合同扫描件")
        => new()
        {
            ParentType = parentType,
            ParentId = parentId,
            Category = DocumentAttachmentReferenceRules.CategoryContract,
            DisplayName = displayName,
            ReferenceId = referenceId,
            SourceAuthorizationAcknowledged = true,
            SourceAuthorizationNote = "客户邮件确认可引用该扫描件",
            AuthorizedBy = "张三"
        };

    private static DocumentAttachmentReferenceController BuildController(ErpDbContext db, long? userId)
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

    private static async Task<DocumentAttachmentReferenceDto> CreateAsync(
        DocumentAttachmentReferenceController controller, DocumentAttachmentReferenceSaveDto dto)
        => AssertOk<DocumentAttachmentReferenceDto>(await controller.Create(dto));

    private static async Task<PagedResult<DocumentAttachmentReferenceDto>> ListAsync(
        DocumentAttachmentReferenceController controller)
        => AssertOk<PagedResult<DocumentAttachmentReferenceDto>>(
            await controller.GetPaged(new DocumentAttachmentReferenceQuery()));

    // ==================== 1. 实时身份：缺失 / 非法 / 已删除 / 已禁用 ====================

    [Fact]
    public async Task 缺失非法已删除身份_按未认证拒绝_禁用账号_按权限不足拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, $"ERP408-SO-{Tag()}", 1);
        var reference = SeedReference(
            db, DocumentAttachmentReferenceRules.ParentTypeSalesOrder, order.Id, order.OrderNo, "att-identity");

        foreach (long? identity in new long?[] { null, 0, -1, 999_999 })
        {
            var controller = BuildController(db, identity);
            await AssertBusinessAsync(ErrorCodes.Unauthorized, () => controller.GetPaged(new DocumentAttachmentReferenceQuery()));
            await AssertBusinessAsync(ErrorCodes.Unauthorized, () => controller.Metadata());
            await AssertBusinessAsync(ErrorCodes.Unauthorized, () => controller.GetById(reference.Id));
            await AssertBusinessAsync(ErrorCodes.Unauthorized, () =>
                controller.GetForParent(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, order.Id));
            await AssertBusinessAsync(ErrorCodes.Unauthorized, () =>
                controller.ParentOptions(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, null));
            await AssertBusinessAsync(ErrorCodes.Unauthorized, () =>
                controller.Create(DarDto(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, order.Id)));
            await AssertBusinessAsync(ErrorCodes.Unauthorized, () =>
                controller.Void(reference.Id, new DocumentAttachmentReferenceVoidRequest { Reason = "越权作废" }));
        }

        var (deletedUser, _, _) = SeedSalesman(db, DocumentAttachmentReferenceRules.MenuCodeSalesOrder);
        deletedUser.IsDeleted = true;
        db.SaveChanges();
        var deletedController = BuildController(db, deletedUser.Id);
        await AssertBusinessAsync(ErrorCodes.Unauthorized, () => deletedController.GetPaged(new DocumentAttachmentReferenceQuery()));
        await AssertBusinessAsync(ErrorCodes.Unauthorized, () => deletedController.GetById(reference.Id));

        var (disabledUser, _, _) = SeedSalesman(db, DocumentAttachmentReferenceRules.MenuCodeSalesOrder);
        disabledUser.Status = UserStatus.Disabled;
        db.SaveChanges();
        var disabledController = BuildController(db, disabledUser.Id);
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => disabledController.GetPaged(new DocumentAttachmentReferenceQuery()));
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => disabledController.Metadata());

        // 被拒绝的调用绝不改写任何引用行
        Assert.Equal(1, await db.DocumentAttachmentReferences.CountAsync());
        Assert.Equal(DocumentAttachmentReferenceRules.StatusActive,
            (await db.DocumentAttachmentReferences.AsNoTracking().SingleAsync()).Status);
    }

    // ==================== 2. 既有菜单授权：无菜单与撤销菜单立即收敛 ====================

    [Fact]
    public async Task 无菜单与撤销菜单_清单空页_写与候选授权不足_读按不存在()
    {
        using var db = TestDbFactory.Create();
        var (user, employee, role) = SeedSalesman(db);
        var own = SeedCustomer(db, $"ERP408-C-{Tag()}", employee.Id);
        var order = SeedSalesOrder(db, $"ERP408-SO-{Tag()}", own.Id);
        var reference = SeedReference(
            db, DocumentAttachmentReferenceRules.ParentTypeSalesOrder, order.Id, order.OrderNo, "att-no-menu");

        var controller = BuildController(db, user.Id);

        // 列表：无已授权父单据类型 → 空页（不披露任何记录与计数）
        Assert.Equal(0, (await ListAsync(controller)).Total);

        // 模块元数据：只需实时启用身份（静态口径）
        AssertOk<DocumentAttachmentReferenceMetadataDto>(await controller.Metadata());

        await AssertBusinessAsync(ErrorCodes.Forbidden, () =>
            controller.Create(DarDto(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, order.Id)));
        await AssertBusinessAsync(ErrorCodes.Forbidden, () =>
            controller.ParentOptions(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, null));
        await AssertBusinessAsync(ErrorCodes.Forbidden, () =>
            controller.GetForParent(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, order.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.GetById(reference.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound, () =>
            controller.Void(reference.Id, new DocumentAttachmentReferenceVoidRequest { Reason = "越权作废" }));
        Assert.Equal(0, await db.DocumentAttachmentReferences.CountAsync(x => x.Status == DocumentAttachmentReferenceRules.StatusVoided));

        // 授权后立即可读；撤销菜单后下一次请求立即收敛（无缓存）
        GrantMenu(db, role.Id, DocumentAttachmentReferenceRules.MenuCodeSalesOrder);
        AssertOk<DocumentAttachmentReferenceDto>(await BuildController(db, user.Id).GetById(reference.Id));

        RevokeMenus(db, role.Id);
        await AssertBusinessAsync(ErrorCodes.NotFound, () =>
            BuildController(db, user.Id).GetById(reference.Id));
        Assert.Equal(0, (await ListAsync(BuildController(db, user.Id))).Total);
    }

    // ==================== 3. 两客户：销售订单的范围收敛与伪造创建 ====================

    [Fact]
    public async Task 两客户_销售订单_计数候选详情按范围收敛_伪造创建与越界作废拒绝()
    {
        using var db = TestDbFactory.Create();
        var (user, employee, _) = SeedSalesman(db, DocumentAttachmentReferenceRules.MenuCodeSalesOrder);
        var own = SeedCustomer(db, $"ERP408-OWN-{Tag()}", employee.Id);
        var foreign = SeedCustomer(db, $"ERP408-FOREIGN-{Tag()}", null);
        var ownOrder = SeedSalesOrder(db, $"ERP408-SO-OWN-{Tag()}", own.Id);
        var foreignOrder = SeedSalesOrder(db, $"ERP408-SO-FOREIGN-{Tag()}", foreign.Id);
        var ownReference = SeedReference(db, DocumentAttachmentReferenceRules.ParentTypeSalesOrder,
            ownOrder.Id, ownOrder.OrderNo, "att-own");
        var foreignReference = SeedReference(db, DocumentAttachmentReferenceRules.ParentTypeSalesOrder,
            foreignOrder.Id, foreignOrder.OrderNo, "att-foreign");

        var controller = BuildController(db, user.Id);

        var page = await ListAsync(controller);
        Assert.Equal(1, page.Total);
        Assert.Equal(ownReference.Id, Assert.Single(page.Items).Id);
        Assert.Equal(1, AssertOk<PagedResult<DocumentAttachmentReferenceDto>>(
            await controller.GetPaged(new DocumentAttachmentReferenceQuery
            {
                ParentType = DocumentAttachmentReferenceRules.ParentTypeSalesOrder
            })).Total);

        Assert.Single(AssertOk<List<DocumentAttachmentReferenceDto>>(
            await controller.GetForParent(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, ownOrder.Id)));
        await AssertBusinessAsync(ErrorCodes.Forbidden, () =>
            controller.GetForParent(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, foreignOrder.Id));

        var options = AssertOk<List<DocumentAttachmentReferenceParentOptionDto>>(
            await controller.ParentOptions(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, null));
        Assert.Contains(options, o => o.ParentId == ownOrder.Id);
        Assert.DoesNotContain(options, o => o.ParentId == foreignOrder.Id);

        AssertOk<DocumentAttachmentReferenceDto>(await controller.GetById(ownReference.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.GetById(foreignReference.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound, () =>
            controller.Void(foreignReference.Id, new DocumentAttachmentReferenceVoidRequest { Reason = "越界作废" }));

        // 伪造创建：范围外父单据 → 授权不足，且零落库
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => controller.Create(
            DarDto(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, foreignOrder.Id, "att-forged")));
        Assert.Equal(2, await db.DocumentAttachmentReferences.CountAsync());
        Assert.Equal(0, await db.DocumentAttachmentReferences.CountAsync(x => x.ReferenceId == "att-forged"));

        // 本人父单据登记成功
        var created = await CreateAsync(controller,
            DarDto(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, ownOrder.Id, "att-new"));
        Assert.Equal(ownOrder.Id, created.ParentId);
        Assert.Equal(ownOrder.OrderNo, created.ParentNo);
        Assert.Equal(3, await db.DocumentAttachmentReferences.CountAsync());
    }

    // ==================== 4. 父单据删除：历史保留，受限账号 fail closed ====================

    [Fact]
    public async Task 父单据已删除_历史行保留_受限账号fail_closed_特权账号仍可读标注不可用()
    {
        using var db = TestDbFactory.Create();
        var (user, employee, _) = SeedSalesman(db, DocumentAttachmentReferenceRules.MenuCodeSalesOrder);
        var own = SeedCustomer(db, $"ERP408-DEL-{Tag()}", employee.Id);
        var order = SeedSalesOrder(db, $"ERP408-SO-DEL-{Tag()}", own.Id);
        var reference = SeedReference(db, DocumentAttachmentReferenceRules.ParentTypeSalesOrder,
            order.Id, order.OrderNo, "att-deleted-parent");

        order.IsDeleted = true;
        db.SaveChanges();

        var restricted = BuildController(db, user.Id);
        await AssertBusinessAsync(ErrorCodes.NotFound, () => restricted.GetById(reference.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound, () =>
            restricted.Void(reference.Id, new DocumentAttachmentReferenceVoidRequest { Reason = "父单据已删除" }));
        Assert.Equal(0, (await ListAsync(restricted)).Total);

        // 历史行保留且从未被改写（不重挂父单据、不静默修复）
        var stored = await db.DocumentAttachmentReferences.AsNoTracking().SingleAsync();
        Assert.Equal(DocumentAttachmentReferenceRules.StatusActive, stored.Status);
        Assert.Equal(order.Id, stored.ParentId);
        Assert.Equal(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, stored.ParentType);
        Assert.Equal(order.OrderNo, stored.ParentNo);

        // 特权账号保留既有历史可读性，并照实标注父单据不可用
        var privilegedId = SeedPrivilegedUser(db, DocumentAttachmentReferenceRules.MenuCodeSalesOrder);
        var dto = AssertOk<DocumentAttachmentReferenceDto>(
            await BuildController(db, privilegedId).GetById(reference.Id));
        Assert.False(dto.ParentAvailable);
    }

    // ==================== 5. 采购订单：权威归属两侧 + 无归属备货采购 ====================

    [Fact]
    public async Task 采购订单_权威归属两侧与无归属备货采购_全部按范围收敛()
    {
        using var db = TestDbFactory.Create();
        var (user, employee, _) = SeedSalesman(db, DocumentAttachmentReferenceRules.MenuCodePurchaseOrder);
        var own = SeedCustomer(db, $"ERP408-PO-OWN-{Tag()}", employee.Id);
        var foreign = SeedCustomer(db, $"ERP408-PO-FOREIGN-{Tag()}", null);
        var ownSo = SeedSalesOrder(db, $"ERP408-PO-SO-OWN-{Tag()}", own.Id);
        var foreignSo = SeedSalesOrder(db, $"ERP408-PO-SO-FOREIGN-{Tag()}", foreign.Id);

        var okOrder = SeedPurchaseOrder(db, $"ERP408-PO-OK-{Tag()}", own.Id, ownSo.Id);
        var crossOrder = SeedPurchaseOrder(db, $"ERP408-PO-CROSS-{Tag()}", own.Id, foreignSo.Id);
        var unownedOrder = SeedPurchaseOrder(db, $"ERP408-PO-NONE-{Tag()}", null, null);

        var okReference = SeedReference(db, DocumentAttachmentReferenceRules.ParentTypePurchaseOrder,
            okOrder.Id, okOrder.OrderNo, "po-ok");
        var crossReference = SeedReference(db, DocumentAttachmentReferenceRules.ParentTypePurchaseOrder,
            crossOrder.Id, crossOrder.OrderNo, "po-cross");
        var unownedReference = SeedReference(db, DocumentAttachmentReferenceRules.ParentTypePurchaseOrder,
            unownedOrder.Id, unownedOrder.OrderNo, "po-none");

        var controller = BuildController(db, user.Id);

        var page = await ListAsync(controller);
        Assert.Equal(1, page.Total);
        Assert.Equal(okReference.Id, Assert.Single(page.Items).Id);

        AssertOk<DocumentAttachmentReferenceDto>(await controller.GetById(okReference.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.GetById(crossReference.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.GetById(unownedReference.Id));

        await AssertBusinessAsync(ErrorCodes.Forbidden, () => controller.Create(
            DarDto(DocumentAttachmentReferenceRules.ParentTypePurchaseOrder, crossOrder.Id, "po-forged")));
        Assert.Equal(0, await db.DocumentAttachmentReferences.CountAsync(x => x.ReferenceId == "po-forged"));

        var options = AssertOk<List<DocumentAttachmentReferenceParentOptionDto>>(
            await controller.ParentOptions(DocumentAttachmentReferenceRules.ParentTypePurchaseOrder, null));
        Assert.Contains(options, o => o.ParentId == okOrder.Id);
        Assert.DoesNotContain(options, o => o.ParentId == crossOrder.Id);
        Assert.DoesNotContain(options, o => o.ParentId == unownedOrder.Id);
    }

    // ==================== 6. 装柜清单：有效参与方 + 显式上游订柜 ====================

    [Fact]
    public async Task 装柜清单_有效参与方与显式上游订柜_按既有权威口径收敛()
    {
        using var db = TestDbFactory.Create();
        var (user, employee, _) = SeedSalesman(db, DocumentAttachmentReferenceRules.MenuCodeContainerLoadingList);
        var own = SeedCustomer(db, $"ERP408-LL-OWN-{Tag()}", employee.Id);
        var foreign = SeedCustomer(db, $"ERP408-LL-FOREIGN-{Tag()}", null);

        var ownList = SeedLoadingList(db, $"ERP408-LL-OWN-{Tag()}", own.Id,
            (own.Id, ContainerLoadingParticipantRules.ActiveStatus));
        var legacyList = SeedLoadingList(db, $"ERP408-LL-LEGACY-{Tag()}", own.Id);
        var sharedList = SeedLoadingList(db, $"ERP408-LL-SHARED-{Tag()}", own.Id,
            (own.Id, ContainerLoadingParticipantRules.ActiveStatus),
            (foreign.Id, ContainerLoadingParticipantRules.ActiveStatus));
        var upstreamList = SeedLoadingList(db, $"ERP408-LL-UP-{Tag()}", own.Id);
        SeedUpstreamBooking(db, upstreamList, foreign.Id);
        var unownedList = SeedLoadingList(db, $"ERP408-LL-NONE-{Tag()}", 0);

        var ownReference = SeedReference(db, DocumentAttachmentReferenceRules.ParentTypeContainerLoadingList,
            ownList.Id, ownList.LoadingListNo, "ll-own");
        SeedReference(db, DocumentAttachmentReferenceRules.ParentTypeContainerLoadingList,
            legacyList.Id, legacyList.LoadingListNo, "ll-legacy");
        var sharedReference = SeedReference(db, DocumentAttachmentReferenceRules.ParentTypeContainerLoadingList,
            sharedList.Id, sharedList.LoadingListNo, "ll-shared");
        var upstreamReference = SeedReference(db, DocumentAttachmentReferenceRules.ParentTypeContainerLoadingList,
            upstreamList.Id, upstreamList.LoadingListNo, "ll-upstream");
        var unownedReference = SeedReference(db, DocumentAttachmentReferenceRules.ParentTypeContainerLoadingList,
            unownedList.Id, unownedList.LoadingListNo, "ll-unowned");

        var controller = BuildController(db, user.Id);

        // 台账只含本人清单（历史单客户 + 本柜仅本人参与方），共享柜 / 上游越界 / 无权威归属一律不返回
        var page = await ListAsync(controller);
        Assert.Equal(2, page.Total);
        Assert.Contains(page.Items, r => r.Id == ownReference.Id);
        Assert.DoesNotContain(page.Items, r => r.Id == sharedReference.Id);
        Assert.DoesNotContain(page.Items, r => r.Id == upstreamReference.Id);
        Assert.DoesNotContain(page.Items, r => r.Id == unownedReference.Id);

        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.GetById(sharedReference.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.GetById(upstreamReference.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.GetById(unownedReference.Id));
        AssertOk<DocumentAttachmentReferenceDto>(await controller.GetById(ownReference.Id));

        await AssertBusinessAsync(ErrorCodes.Forbidden, () => controller.Create(
            DarDto(DocumentAttachmentReferenceRules.ParentTypeContainerLoadingList, sharedList.Id, "ll-forged")));
        Assert.Equal(0, await db.DocumentAttachmentReferences.CountAsync(x => x.ReferenceId == "ll-forged"));

        var options = AssertOk<List<DocumentAttachmentReferenceParentOptionDto>>(
            await controller.ParentOptions(DocumentAttachmentReferenceRules.ParentTypeContainerLoadingList, null));
        Assert.Contains(options, o => o.ParentId == ownList.Id);
        Assert.Contains(options, o => o.ParentId == legacyList.Id);
        Assert.DoesNotContain(options, o => o.ParentId == sharedList.Id);
        Assert.DoesNotContain(options, o => o.ParentId == upstreamList.Id);
        Assert.DoesNotContain(options, o => o.ParentId == unownedList.Id);
    }

    // ==================== 7. 出口单证：权威客户缺失 / 越界 ====================

    [Fact]
    public async Task 出口单证_客户归属缺失或越界_受限账号fail_closed()
    {
        using var db = TestDbFactory.Create();
        var (user, employee, _) = SeedSalesman(db, DocumentAttachmentReferenceRules.MenuCodeTradeDocument);
        var own = SeedCustomer(db, $"ERP408-TD-OWN-{Tag()}", employee.Id);
        var foreign = SeedCustomer(db, $"ERP408-TD-FOREIGN-{Tag()}", null);
        var ownDoc = SeedTradeDocument(db, $"ERP408-TD-OWN-{Tag()}", own.Id);
        var unlinkedDoc = SeedTradeDocument(db, $"ERP408-TD-NULL-{Tag()}", null);
        var foreignDoc = SeedTradeDocument(db, $"ERP408-TD-FOREIGN-{Tag()}", foreign.Id);

        var ownReference = SeedReference(db, DocumentAttachmentReferenceRules.ParentTypeTradeDocument,
            ownDoc.Id, ownDoc.DocNo, "td-own");
        var unlinkedReference = SeedReference(db, DocumentAttachmentReferenceRules.ParentTypeTradeDocument,
            unlinkedDoc.Id, unlinkedDoc.DocNo, "td-unlinked");
        var foreignReference = SeedReference(db, DocumentAttachmentReferenceRules.ParentTypeTradeDocument,
            foreignDoc.Id, foreignDoc.DocNo, "td-foreign");

        var controller = BuildController(db, user.Id);

        Assert.Equal(1, (await ListAsync(controller)).Total);
        AssertOk<DocumentAttachmentReferenceDto>(await controller.GetById(ownReference.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.GetById(unlinkedReference.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.GetById(foreignReference.Id));

        await AssertBusinessAsync(ErrorCodes.Forbidden, () => controller.Create(
            DarDto(DocumentAttachmentReferenceRules.ParentTypeTradeDocument, unlinkedDoc.Id, "td-forged")));
        Assert.Equal(0, await db.DocumentAttachmentReferences.CountAsync(x => x.ReferenceId == "td-forged"));

        var options = AssertOk<List<DocumentAttachmentReferenceParentOptionDto>>(
            await controller.ParentOptions(DocumentAttachmentReferenceRules.ParentTypeTradeDocument, null));
        Assert.Contains(options, o => o.ParentId == ownDoc.Id);
        Assert.DoesNotContain(options, o => o.ParentId == unlinkedDoc.Id);
        Assert.DoesNotContain(options, o => o.ParentId == foreignDoc.Id);
    }

    // ==================== 8. 显式作废保留原始证据 + 重复登记 ====================

    [Fact]
    public async Task 授权登记与显式作废_保留原始证据_重复登记拒绝且父单据不变()
    {
        using var db = TestDbFactory.Create();
        var (user, employee, _) = SeedSalesman(db, DocumentAttachmentReferenceRules.MenuCodeSalesOrder);
        var own = SeedCustomer(db, $"ERP408-EV-{Tag()}", employee.Id);
        var order = SeedSalesOrder(db, $"ERP408-EV-SO-{Tag()}", own.Id);
        var controller = BuildController(db, user.Id);

        var dto = DarDto(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, order.Id, "att-dup", "合同扫描件 A");
        var created = await CreateAsync(controller, dto);
        Assert.Equal("att-dup", created.ReferenceId);
        Assert.True(created.SourceAuthorizationAcknowledged);
        Assert.Equal(dto.SourceAuthorizationNote, created.SourceAuthorizationNote);
        Assert.Equal("张三", created.AuthorizedBy);
        var registeredAt = created.RegisteredAt;

        await AssertBusinessAsync(ErrorCodes.Duplicate, () => controller.Create(
            DarDto(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, order.Id, "att-dup")));
        Assert.Equal(1, await db.DocumentAttachmentReferences.CountAsync());

        var voided = AssertOk<DocumentAttachmentReferenceDto>(
            await controller.Void(created.Id, new DocumentAttachmentReferenceVoidRequest { Reason = "更正分类" }));
        Assert.True(voided.IsVoided);
        Assert.Equal("更正分类", voided.VoidReason);
        Assert.Equal("att-dup", voided.ReferenceId);
        Assert.Equal("合同扫描件 A", voided.DisplayName);
        Assert.Equal(dto.SourceAuthorizationNote, voided.SourceAuthorizationNote);
        Assert.Equal("张三", voided.AuthorizedBy);
        Assert.Equal(registeredAt, voided.RegisteredAt);

        // 作废不占身份：同一引用标识可重新登记（不静默合并、不覆盖历史）
        var reRegistered = await CreateAsync(controller,
            DarDto(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, order.Id, "att-dup", "合同扫描件 B"));
        Assert.NotEqual(created.Id, reRegistered.Id);
        Assert.Equal(2, await db.DocumentAttachmentReferences.CountAsync());

        // 父单据从未被改写
        var orderAfter = await db.SalesOrders.AsNoTracking().SingleAsync();
        Assert.Equal(1000m, orderAfter.TotalAmount);
        Assert.Equal(DocumentStatus.Approved, orderAfter.Status);
        Assert.False(orderAfter.IsDeleted);
    }

    // ==================== 9. 未知类型 / 不存在父单据的稳定失败 + 控制器契约 ====================

    [Fact]
    public async Task 未知类型与不存在父单据_稳定失败_控制器无编辑删除接口()
    {
        using var db = TestDbFactory.Create();
        var (user, employee, _) = SeedSalesman(db,
            DocumentAttachmentReferenceRules.MenuCodeSalesOrder,
            DocumentAttachmentReferenceRules.MenuCodePurchaseOrder,
            DocumentAttachmentReferenceRules.MenuCodeContainerLoadingList,
            DocumentAttachmentReferenceRules.MenuCodeTradeDocument);
        var own = SeedCustomer(db, $"ERP408-STABLE-{Tag()}", employee.Id);
        var order = SeedSalesOrder(db, $"ERP408-STABLE-SO-{Tag()}", own.Id);
        var controller = BuildController(db, user.Id);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () =>
            controller.Create(DarDto("Quotation", order.Id)));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.ParentOptions("Quotation", null));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () =>
            controller.GetForParent("Quotation", order.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound, () =>
            controller.Create(DarDto(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, 999_123)));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.GetById(999_123));
        Assert.Equal(0, await db.DocumentAttachmentReferences.CountAsync());

        var controllerType = typeof(DocumentAttachmentReferenceController);
        Assert.NotNull(controllerType.GetCustomAttribute<AuthorizeAttribute>());
        var declared = controllerType.GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(declared, m => m.GetCustomAttribute<HttpPutAttribute>() is not null
            || m.GetCustomAttribute<HttpDeleteAttribute>() is not null
            || m.GetCustomAttribute<HttpPatchAttribute>() is not null);
    }
}
