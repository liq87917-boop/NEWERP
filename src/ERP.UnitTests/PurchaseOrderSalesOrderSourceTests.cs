using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-393 采购订单来源销售订单候选 / 已存储来源解析单元测试。
/// <para>覆盖：归一化（关键字 / 页码 / 每页条数）、候选只含「已审核、未删除、未取消」且落在客户数据范围之内、
/// 可选精确客户过滤、关键字匹配、分页与总数、客户缺失显式不可选、复用 <see cref="PurchaseSalesOrderLinkRules"/>
/// 资格判定、受限账号范围先于计数、未映射业务员 fail closed；已存储来源的未关联 / 有效 / 已取消 / 已删除 /
/// 范围外（<b>绝不披露</b>范围外客户 / 订单字段）标注；控制器端点的实时身份 / 菜单 / 范围 fail closed
/// 与只读接口接线契约。</para>
/// <para>全部使用内存数据库，不连接 SQL Server、不启动 API、不运行浏览器验收。</para>
/// </summary>
public class PurchaseOrderSalesOrderSourceTests
{
    // ==================== 脚手架 ====================

    private static PurchaseOrderController NewController(ErpDbContext db)
        => new(db, new DocumentNumberService(db));

    private static BaseCustomer SeedCustomer(ErpDbContext db, long id, string code, string name, long? empId = null)
    {
        var customer = new BaseCustomer { Id = id, CustomerCode = code, CustomerName = name, EmpId = empId };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedSalesOrder(ErpDbContext db, string orderNo, long customerId,
        DocumentStatus status, string customerPoNo = "", string contractNo = "")
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today.AddDays(-5),
            CustomerId = customerId,
            Currency = Currency.USD,
            Status = status,
            CustomerPoNo = customerPoNo,
            ContractNo = contractNo,
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static long SeedPurchaseOrder(ErpDbContext db, long? owningSalesOrderId, long? owningCustomerId)
    {
        var order = new PurchaseOrder
        {
            OrderNo = $"PO-{Guid.NewGuid():N}"[..16],
            OrderDate = DateTime.Today,
            SupplierId = 1,
            OwningSalesOrderId = owningSalesOrderId,
            OwningCustomerId = owningCustomerId,
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        return order.Id;
    }

    private static SysMenu SeedMenu(ErpDbContext db, string code)
    {
        var menu = new SysMenu { MenuName = code, MenuCode = code, MenuType = MenuType.Menu };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    private static long SeedPrivilegedUser(ErpDbContext db)
    {
        var user = new SysUser
        {
            UserName = $"priv-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "特权采购用户",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleName = "特权角色", RoleCode = $"Privileged-{Guid.NewGuid():N}", IsSystem = true };
        db.SysRoles.Add(role);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        var menu = SeedMenu(db, PurchaseSalesOrderLinkRules.RequiredMenuCode);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>播种「系统内置角色但无采购订单菜单授权」的用户。</summary>
    private static long SeedUserWithoutMenu(ErpDbContext db)
    {
        var user = new SysUser
        {
            UserName = $"nomenu-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "无菜单用户",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleName = "系统角色", RoleCode = $"System-{Guid.NewGuid():N}", IsSystem = true };
        db.SysRoles.Add(role);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>播种受限制业务员账号（非系统角色 + 采购订单菜单，登录名 = 员工编码）；返回（用户 Id, 员工 Id）。</summary>
    private static (long UserId, long EmployeeId) SeedRestrictedUser(ErpDbContext db, string code)
    {
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true };
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

        var role = new SysRole { RoleName = code, RoleCode = $"Role-{code}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        var menu = SeedMenu(db, PurchaseSalesOrderLinkRules.RequiredMenuCode);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return (user.Id, employee.Id);
    }

    private static PurchaseOrderSalesOrderSourceCandidatePageDto Candidates(IActionResult result)
        => Assert.IsType<ApiResponse<PurchaseOrderSalesOrderSourceCandidatePageDto>>(
            Assert.IsType<OkObjectResult>(result).Value).Data!;

    private static PurchaseOrderSalesOrderSourceViewDto StoredView(IActionResult result)
        => Assert.IsType<ApiResponse<PurchaseOrderSalesOrderSourceViewDto>>(
            Assert.IsType<OkObjectResult>(result).Value).Data!;

    // ==================== 1. 归一化与资格口径复用 ====================

    [Fact]
    public void 归一化_关键字截断_页码与每页条数有界()
    {
        Assert.Equal("SO-1", PurchaseOrderSalesOrderSourceService.NormalizeKeyword("  SO-1  "));
        Assert.Equal(PurchaseOrderSalesOrderSourceService.MaxKeywordLength,
            PurchaseOrderSalesOrderSourceService.NormalizeKeyword(new string('x', 500)).Length);
        Assert.Equal(1, PurchaseOrderSalesOrderSourceService.NormalizePage(0));
        Assert.Equal(1, PurchaseOrderSalesOrderSourceService.NormalizePage(-9));
        Assert.Equal(PurchaseOrderSalesOrderSourceService.DefaultPageSize,
            PurchaseOrderSalesOrderSourceService.NormalizePageSize(0));
        Assert.Equal(PurchaseOrderSalesOrderSourceService.MaxPageSize,
            PurchaseOrderSalesOrderSourceService.NormalizePageSize(9999));
        Assert.Equal(15, PurchaseOrderSalesOrderSourceService.NormalizePageSize(15));
    }

    [Fact]
    public void 资格判定_复用链接规则_仅已审核可作为新来源()
    {
        Assert.True(PurchaseSalesOrderLinkRules.IsEligibleNewSource(DocumentStatus.Approved));
        Assert.False(PurchaseSalesOrderLinkRules.IsEligibleNewSource(DocumentStatus.Pending));
        Assert.False(PurchaseSalesOrderLinkRules.IsEligibleNewSource(DocumentStatus.Cancelled));
        Assert.Equal(PurchaseSalesOrderLinkRules.SourceCancelledText,
            PurchaseSalesOrderLinkRules.SourceIneligibleReason(DocumentStatus.Cancelled));
        Assert.Equal(PurchaseSalesOrderLinkRules.SourceNotApprovedText,
            PurchaseSalesOrderLinkRules.SourceIneligibleReason(DocumentStatus.Pending));
        Assert.Empty(PurchaseSalesOrderLinkRules.SourceIneligibleReason(DocumentStatus.Approved));
    }

    // ==================== 2. 候选：范围 / 状态 / 过滤 / 分页 ====================

    [Fact]
    public async Task 候选_只含已审核未删除且落在客户数据范围之内()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db, 940001L, "C-OWN", "自有客户");
        SeedCustomer(db, 940002L, "C-OTHER", "他人客户");
        var approved = SeedSalesOrder(db, "SO-OK-1", own.Id, DocumentStatus.Approved);
        SeedSalesOrder(db, "SO-PENDING-1", own.Id, DocumentStatus.Pending);
        SeedSalesOrder(db, "SO-CANCELLED-1", own.Id, DocumentStatus.Cancelled);
        var deleted = SeedSalesOrder(db, "SO-DELETED-1", own.Id, DocumentStatus.Approved);
        deleted.IsDeleted = true;
        db.SaveChanges();
        var (userId, employeeId) = SeedRestrictedUser(db, "pos-src-scope-1");
        own.EmpId = employeeId;              // 自有客户分配给该业务员
        db.SaveChanges();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, userId);

        var page = Candidates(await ctl.GetSalesOrderSourceCandidates(null, null, 0, 0));

        Assert.Equal(1, page.Total);
        var item = Assert.Single(page.Items);
        Assert.Equal(approved.Id, item.SalesOrderId);
        Assert.True(item.Eligible);
        Assert.Equal("自有客户", item.CustomerName);
        Assert.Equal("USD", item.Currency);          // 币种只作展示，不作门槛
        Assert.DoesNotContain(page.Items, i => i.OrderNo.Contains("CANCELLED"));
    }

    [Fact]
    public async Task 候选_可选精确客户过滤与关键字匹配()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, 940101L, "C-A", "客户A");
        var customerB = SeedCustomer(db, 940102L, "C-B", "客户B");
        SeedSalesOrder(db, "SO-KW-1", customerA.Id, DocumentStatus.Approved, customerPoNo: "CPO-HIT");
        SeedSalesOrder(db, "SO-KW-2", customerB.Id, DocumentStatus.Approved);
        var userId = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, userId);

        var byCustomer = Candidates(await ctl.GetSalesOrderSourceCandidates(customerA.Id, null, 0, 0));
        Assert.Equal(1, byCustomer.Total);
        Assert.Equal(customerA.Id, byCustomer.Items[0].CustomerId);

        var byKeyword = Candidates(await ctl.GetSalesOrderSourceCandidates(null, "CPO-HIT", 0, 0));
        Assert.Equal(1, byKeyword.Total);
        Assert.Equal("SO-KW-1", byKeyword.Items[0].OrderNo);

        var none = Candidates(await ctl.GetSalesOrderSourceCandidates(null, "NO-SUCH-KEYWORD", 0, 0));
        Assert.Equal(0, none.Total);
        Assert.Empty(none.Items);
    }

    [Fact]
    public async Task 候选_分页先归一化再计数且每页有界()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, 940201L, "C-PAGE", "分页客户");
        for (var i = 0; i < 25; i++) SeedSalesOrder(db, $"SO-PAGE-{i:D2}", customer.Id, DocumentStatus.Approved);
        var userId = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, userId);

        var first = Candidates(await ctl.GetSalesOrderSourceCandidates(null, null, 1, 10));
        Assert.Equal(25, first.Total);
        Assert.Equal(10, first.Items.Count);
        Assert.Equal(1, first.Page);
        Assert.Equal(10, first.PageSize);

        var last = Candidates(await ctl.GetSalesOrderSourceCandidates(null, null, 3, 10));
        Assert.Equal(5, last.Items.Count);

        var clamped = Candidates(await ctl.GetSalesOrderSourceCandidates(null, null, 0, 9999));
        Assert.Equal(PurchaseOrderSalesOrderSourceService.MaxPageSize, clamped.PageSize);
    }

    [Fact]
    public async Task 候选_归属客户缺失时显式不可选且仍可见()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, 940301L, "C-GONE", "将删除客户");
        SeedSalesOrder(db, "SO-GONE-1", customer.Id, DocumentStatus.Approved);
        customer.IsDeleted = true;
        db.SaveChanges();
        var userId = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, userId);

        var page = Candidates(await ctl.GetSalesOrderSourceCandidates(null, null, 0, 0));

        var item = Assert.Single(page.Items);
        Assert.False(item.Eligible);
        Assert.Contains("客户", item.IneligibleReason);
        Assert.Empty(item.CustomerName);
    }

    // ==================== 3. 候选：授权 fail closed ====================

    [Fact]
    public async Task 候选_无身份拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, 940401L, "C-NOID", "客户");
        SeedSalesOrder(db, "SO-NOID-1", customer.Id, DocumentStatus.Approved);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.GetSalesOrderSourceCandidates(null, null, 0, 0));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task 候选_无采购订单菜单拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, 940501L, "C-NOMENU", "客户");
        SeedSalesOrder(db, "SO-NOMENU-1", customer.Id, DocumentStatus.Approved);
        var userId = SeedUserWithoutMenu(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.GetSalesOrderSourceCandidates(null, null, 0, 0));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 候选_受限账号未映射业务员_拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, 940601L, "C-UNMAPPED", "客户");
        SeedSalesOrder(db, "SO-UNMAPPED-1", customer.Id, DocumentStatus.Approved);
        // 无 BaseEmployee 映射：受限账号可见客户集为空 → fail closed（绝不泄露任何单据）
        var user = new SysUser
        {
            UserName = "unmapped-pos-src", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "未映射", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        var role = new SysRole { RoleName = "受限", RoleCode = "Restricted-pos-src", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        var menu = SeedMenu(db, PurchaseSalesOrderLinkRules.RequiredMenuCode);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.GetSalesOrderSourceCandidates(null, null, 0, 0));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("未映射", ex.Message);
    }

    // ==================== 4. 已存储来源解析 ====================

    [Fact]
    public async Task 已存储_未关联_显式标注且不回填()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, 941001L, "C-UNLINK", "客户");
        var poId = SeedPurchaseOrder(db, null, customer.Id);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, SeedPrivilegedUser(db));

        var view = StoredView(await ctl.GetStoredSalesOrderSource(poId));

        Assert.False(view.Linked);
        Assert.False(view.Unavailable);
        Assert.Empty(view.OrderNo);
        Assert.Contains("未关联", view.Annotation);
    }

    [Fact]
    public async Task 已存储_有效来源_返回权威客户与订单号()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, 941101L, "C-LIVE", "有效客户");
        var so = SeedSalesOrder(db, "SO-LIVE-1", customer.Id, DocumentStatus.Approved);
        var poId = SeedPurchaseOrder(db, so.Id, customer.Id);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, SeedPrivilegedUser(db));

        var view = StoredView(await ctl.GetStoredSalesOrderSource(poId));

        Assert.True(view.Linked);
        Assert.False(view.Unavailable);
        Assert.True(view.EligibleForNewLink);
        Assert.Equal("SO-LIVE-1", view.OrderNo);
        Assert.Equal(customer.Id, view.CustomerId);
        Assert.Equal("有效客户", view.CustomerName);
        Assert.Equal("USD", view.Currency);
        Assert.Contains("已关联", view.Annotation);
    }

    [Fact]
    public async Task 已存储_来源已取消_只读保留且不可作为新来源()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, 941201L, "C-CANCEL", "客户");
        var so = SeedSalesOrder(db, "SO-CANCEL-1", customer.Id, DocumentStatus.Cancelled);
        var poId = SeedPurchaseOrder(db, so.Id, customer.Id);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, SeedPrivilegedUser(db));

        var view = StoredView(await ctl.GetStoredSalesOrderSource(poId));

        Assert.True(view.Linked);
        Assert.False(view.Unavailable);
        Assert.False(view.EligibleForNewLink);
        Assert.Equal("SO-CANCEL-1", view.OrderNo);
        Assert.Contains("已取消", view.Annotation);
        Assert.Contains("只读保留", view.Annotation);
    }

    [Fact]
    public async Task 已存储_来源已删除_不可用且不填充字段()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, 941301L, "C-DEL", "客户");
        var so = SeedSalesOrder(db, "SO-DEL-1", customer.Id, DocumentStatus.Approved);
        var poId = SeedPurchaseOrder(db, so.Id, customer.Id);
        so.IsDeleted = true;
        db.SaveChanges();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, SeedPrivilegedUser(db));

        var view = StoredView(await ctl.GetStoredSalesOrderSource(poId));

        Assert.True(view.Linked);
        Assert.True(view.Unavailable);
        Assert.False(view.EligibleForNewLink);
        Assert.Empty(view.OrderNo);
        Assert.Null(view.CustomerId);
        Assert.Empty(view.Annotation.Replace("已存储来源销售订单不可用（已删除 / 已取消 / 未审核 / 不在当前账号客户数据范围内），原链接原样保留", ""));
        Assert.DoesNotContain("SO-DEL-1", view.Annotation);
    }

    [Fact]
    public async Task 已存储_订单不存在_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, SeedPrivilegedUser(db));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetStoredSalesOrderSource(999999L));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public async Task 已存储_无身份拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, 941401L, "C-NOID2", "客户");
        var poId = SeedPurchaseOrder(db, null, customer.Id);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetStoredSalesOrderSource(poId));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task 已存储_无采购订单菜单拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, 941501L, "C-NOMENU2", "客户");
        var poId = SeedPurchaseOrder(db, null, customer.Id);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, SeedUserWithoutMenu(db));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetStoredSalesOrderSource(poId));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 已存储_范围外来源_控制器拒绝且服务端绝不披露字段()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedUser(db, "pos-src-foreign");
        var own = SeedCustomer(db, 941601L, "C-OWN2", "自有客户", empId: employeeId);
        var foreign = SeedCustomer(db, 941602L, "C-FOREIGN", "范围外客户");
        var foreignOrder = SeedSalesOrder(db, "SO-FOREIGN-1", foreign.Id, DocumentStatus.Approved);
        // 本单显式归属客户在范围内，但权威来源客户在范围外
        var poId = SeedPurchaseOrder(db, foreignOrder.Id, own.Id);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, userId);

        // 控制器端：单据级范围复核 fail closed（绝不泄露范围外归属）
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetStoredSalesOrderSource(poId));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);

        // 服务端解析（即使直接调用）：只返回不可用标注，绝不填充范围外客户 / 订单字段
        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId);
        var order = await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == poId);
        var view = await PurchaseOrderSalesOrderSourceService.DescribeStoredSourceAsync(db, scope, order);

        Assert.True(view.Linked);
        Assert.True(view.Unavailable);
        Assert.False(view.EligibleForNewLink);
        Assert.Empty(view.OrderNo);
        Assert.Null(view.CustomerId);
        Assert.Empty(view.CustomerName);
        Assert.Empty(view.Status);
        Assert.DoesNotContain("SO-FOREIGN-1", view.Annotation);
        Assert.DoesNotContain("范围外客户", view.Annotation);
    }

    // ==================== 5. 接口与前端接线契约 ====================

    [Fact]
    public void 接口与前端接线契约_只读复用且不执行任意SQL()
    {
        var root = RepoRoot();
        var controller = File.ReadAllText(Path.Combine(
            root, "src", "ERP.Api", "Controllers", "PurchaseOrderController.cs"));

        Assert.Contains("[HttpGet(\"sales-order-source-candidates\")]", controller);
        Assert.Contains("[HttpGet(\"{id:long}/sales-order-source\")]", controller);
        Assert.Contains("PurchaseOrderSalesOrderSourceService.QueryCandidatesAsync", controller);
        Assert.Contains("PurchaseOrderSalesOrderSourceService.DescribeStoredSourceAsync", controller);
        Assert.Contains("PurchaseOrderAuthorizationRules.EnsureMenuAuthorizedAsync", controller);

        var jsPath = Path.Combine(root, "src", "ERP.Api", "wwwroot", "js", "purchase-order-sales-order-source.js");
        var js = File.ReadAllText(jsPath);
        Assert.Contains("/sales-order-source-candidates?", js);
        Assert.Contains("/sales-order-source`", js);
        Assert.Contains("async function openPurchaseOrderSalesOrderSourcePicker", js);
        Assert.Contains("function posInstallFormHook", js);
        Assert.Contains("posApplyToForm", js);
        Assert.Contains("posUnlinkSource", js);
        Assert.DoesNotContain("FromSql", js);
        Assert.DoesNotContain("ExecuteSql", js);
        Assert.DoesNotContain("SqlCommand", js);

        var index = File.ReadAllText(Path.Combine(root, "src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/purchase-order-sales-order-source.js", index);

        var modules = File.ReadAllText(Path.Combine(root, "src", "ERP.Api", "wwwroot", "js", "modules-doc.js"));
        Assert.Contains("selector: 'purchase-order-sales-order-source'", modules);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NEWERP.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("未找到仓库根目录（NEWERP.sln）");
    }
}
