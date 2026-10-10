using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-421 规范销售订单普通写入（修改 / 删除 / 提交 / 审核）的确定性锁协议与状态口径单元测试：
/// 锁语句与锁序同源、事务绝不嵌套、合法状态流转、加锁范围（手工 / 历史 / 已解析来源）解析、
/// 「加锁前来源过期」判定，以及控制器在内存库上的可观测行为（手工订单进入协议、过期 / 已取消 /
/// 已删除不可复活、字段校验失败后回滚并清空半成品变更）。
/// </summary>
public class SalesOrderMutationTests
{
    // ==================== 1. 锁序 / 事务 / 状态口径 ====================

    [Fact]
    public void 确定性锁序_与取消_单证生成_转换共用同一把销售订单行锁()
    {
        Assert.Equal(PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql,
            SalesOrderSourceLineageRules.SalesOrderRowLockSql);
        Assert.Contains("db_owner.SalesOrders", SalesOrderSourceLineageRules.SalesOrderRowLockSql);
        Assert.Contains("UPDLOCK", SalesOrderSourceLineageRules.SalesOrderRowLockSql);
        Assert.Contains("HOLDLOCK", SalesOrderSourceLineageRules.SalesOrderRowLockSql);
        Assert.Equal(QuotationMutationRules.QuotationRowLockSql,
            SalesOrderSourceLineageRules.QuotationRowLockSql);
        Assert.Equal(ProformaInvoiceMutationRules.PiRowLockSql,
            SalesOrderSourceLineageRules.ProformaInvoiceRowLockSql);

        Assert.Contains("报价单来源行锁", SalesOrderMutationRules.LockOrderText);
        Assert.Contains("PI 来源行锁", SalesOrderMutationRules.LockOrderText);
        Assert.Contains("销售订单目标行锁", SalesOrderMutationRules.LockOrderText);
        Assert.Contains("绝不反向获取下游锁", SalesOrderMutationRules.LockOrderText);
        Assert.Contains("手工订单", SalesOrderMutationRules.LockOrderText);
        Assert.Contains("无法解析的历史来源", SalesOrderMutationRules.LockOrderText);
    }

    [Fact]
    public async Task 事务与行锁_内存库等价无操作_且绝不嵌套()
    {
        using var db = TestDbFactory.Create();
        Assert.False(SalesOrderMutationRules.IsRelationalProvider(db));

        await using var transaction = await SalesOrderMutationRules.BeginMutationTransactionAsync(db);
        Assert.Null(transaction);
        // 同一 DbContext 内重复调用绝不嵌套（内存库恒为 null；关系型后端由 CurrentTransaction 短路）。
        await using var again = await SalesOrderMutationRules.BeginMutationTransactionAsync(db);
        Assert.Null(again);
    }

    [Fact]
    public void 合法状态流转_仅待提交到已提交与已提交到已审核()
    {
        Assert.True(SalesOrderMutationRules.IsLegalTransition(DocumentStatus.Pending, DocumentStatus.Submitted));
        Assert.True(SalesOrderMutationRules.IsLegalTransition(DocumentStatus.Submitted, DocumentStatus.Approved));
        Assert.False(SalesOrderMutationRules.IsLegalTransition(DocumentStatus.Pending, DocumentStatus.Approved));
        Assert.False(SalesOrderMutationRules.IsLegalTransition(DocumentStatus.Submitted, DocumentStatus.Pending));
        Assert.False(SalesOrderMutationRules.IsLegalTransition(DocumentStatus.Approved, DocumentStatus.Submitted));
        Assert.False(SalesOrderMutationRules.IsLegalTransition(DocumentStatus.Approved, DocumentStatus.Cancelled));
    }

    [Theory]
    [InlineData(DocumentStatus.Pending, true)]
    [InlineData(DocumentStatus.Submitted, false)]
    [InlineData(DocumentStatus.Approved, false)]
    [InlineData(DocumentStatus.Rejected, false)]
    [InlineData(DocumentStatus.Completed, false)]
    [InlineData(DocumentStatus.Cancelled, false)]
    public void 编辑与删除_仅待提交放行(DocumentStatus status, bool allowed)
    {
        if (allowed)
        {
            SalesOrderMutationRules.EnsureEditable(status);
            SalesOrderMutationRules.EnsureDeletable(status);
            return;
        }

        var edit = Assert.Throws<BusinessException>(() => SalesOrderMutationRules.EnsureEditable(status));
        Assert.Equal(ErrorCodes.RuleConflict, edit.Code);
        Assert.Equal(SalesOrderMutationRules.NotPendingEditText, edit.Message);

        var delete = Assert.Throws<BusinessException>(() => SalesOrderMutationRules.EnsureDeletable(status));
        Assert.Equal(ErrorCodes.RuleConflict, delete.Code);
        Assert.Equal(SalesOrderMutationRules.NotPendingDeleteText, delete.Message);
    }

    [Fact]
    public void 状态流转授权_锁内陈旧状态一律拒绝且已取消不可复活()
    {
        SalesOrderMutationRules.EnsureTransitionAllowed(DocumentStatus.Pending,
            DocumentStatus.Pending, DocumentStatus.Submitted);

        var stale = Assert.Throws<BusinessException>(() => SalesOrderMutationRules.EnsureTransitionAllowed(
            DocumentStatus.Submitted, DocumentStatus.Pending, DocumentStatus.Submitted));
        Assert.Equal(SalesOrderMutationRules.IllegalTransitionText, stale.Message);

        Assert.Throws<BusinessException>(() => SalesOrderMutationRules.EnsureTransitionAllowed(
            DocumentStatus.Cancelled, DocumentStatus.Submitted, DocumentStatus.Approved));
        Assert.Throws<BusinessException>(() => SalesOrderMutationRules.EnsureTransitionAllowed(
            DocumentStatus.Approved, DocumentStatus.Pending, DocumentStatus.Submitted));
    }

    // ==================== 2. 加锁范围与过期判定（纯口径） ====================

    [Fact]
    public void 加锁范围_未链接与无法解析历史值均不取来源锁()
    {
        var unlinked = SalesOrderMutationRules.DescribeLiveScope(new SalesOrderSourceLineage());
        Assert.False(unlinked.IsLiveSource);
        Assert.Null(unlinked.QuotationId);
        Assert.Null(unlinked.PiId);

        var legacy = new SalesOrderSourceLineage
        {
            QuotationId = 11L, PiId = 22L, IsUnresolvedLegacy = true
        };
        var legacyScope = SalesOrderMutationRules.DescribeLiveScope(legacy);
        Assert.False(legacyScope.IsLiveSource);
        Assert.Null(legacyScope.QuotationId);
        Assert.Null(legacyScope.PiId);

        Assert.False(SalesOrderMutationLockScope.None.IsLiveSource);
        Assert.Null(SalesOrderMutationLockScope.None.QuotationId);
        Assert.Null(SalesOrderMutationLockScope.None.PiId);
    }

    [Fact]
    public void 加锁范围_仅锁确实解析到的来源行_PI祖先行缺失时只锁PI()
    {
        var quoteOnly = new SalesOrderSourceLineage
        {
            QuotationId = 7L, Quotation = new Quotation { Id = 7L }
        };
        var quoteScope = SalesOrderMutationRules.DescribeLiveScope(quoteOnly);
        Assert.True(quoteScope.IsLiveSource);
        Assert.Equal(7L, quoteScope.QuotationId);
        Assert.Null(quoteScope.PiId);

        // PI 的报价单祖先未解析到行（IsDeleted / 缺失）：只锁 PI，绝不对不存在的祖先行加锁。
        var piWithMissingAncestor = new SalesOrderSourceLineage
        {
            QuotationId = 7L, Quotation = null, PiId = 9L,
            ProformaInvoice = new ProformaInvoice { Id = 9L }
        };
        var piScope = SalesOrderMutationRules.DescribeLiveScope(piWithMissingAncestor);
        Assert.True(piScope.IsLiveSource);
        Assert.Null(piScope.QuotationId);
        Assert.Equal(9L, piScope.PiId);

        var both = new SalesOrderSourceLineage
        {
            QuotationId = 7L, Quotation = new Quotation { Id = 7L },
            PiId = 9L, ProformaInvoice = new ProformaInvoice { Id = 9L }
        };
        var bothScope = SalesOrderMutationRules.DescribeLiveScope(both);
        Assert.Equal(7L, bothScope.QuotationId);
        Assert.Equal(9L, bothScope.PiId);
    }

    [Fact]
    public void 加锁范围覆盖_来源不一致即视为过期()
    {
        var scope = new SalesOrderMutationLockScope { QuotationId = 5L, PiId = 9L, IsLiveSource = true };
        Assert.True(SalesOrderMutationRules.ScopeCovers(scope, 5L, 9L));
        Assert.False(SalesOrderMutationRules.ScopeCovers(scope, null, 9L));
        Assert.False(SalesOrderMutationRules.ScopeCovers(scope, 5L, null));
        Assert.False(SalesOrderMutationRules.ScopeCovers(scope, 6L, 9L));

        Assert.True(SalesOrderMutationRules.ScopeCovers(SalesOrderMutationLockScope.None, null, null));
        Assert.False(SalesOrderMutationRules.ScopeCovers(SalesOrderMutationLockScope.None, 5L, null));
    }

    [Fact]
    public void 加锁前后来源一致性_非正整数归一为空且任一变化即过期()
    {
        Assert.True(SalesOrderMutationRules.PersistedSourceUnchanged(5L, 9L, 5L, 9L));
        Assert.True(SalesOrderMutationRules.PersistedSourceUnchanged(null, null, 0L, -1L));
        Assert.False(SalesOrderMutationRules.PersistedSourceUnchanged(5L, 9L, 5L, 10L));
        Assert.False(SalesOrderMutationRules.PersistedSourceUnchanged(null, null, 5L, null));
    }

    // ==================== 3. 加锁范围权威解析（内存库） ====================

    [Fact]
    public async Task 加锁范围解析_未链接返回无来源锁()
    {
        using var db = TestDbFactory.Create();
        var scope = await SalesOrderMutationRules.ResolveLiveSourceLockScopeAsync(db, 1L, null, null);
        Assert.False(scope.IsLiveSource);
        Assert.Null(scope.QuotationId);
        Assert.Null(scope.PiId);
    }

    [Fact]
    public async Task 加锁范围解析_PI与报价单祖先均可解析时两把来源锁都取()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var quotation = SeedQuotation(db, $"QT-{Guid.NewGuid():N}"[..18], customer.Id);
        var pi = SeedPi(db, $"PI-{Guid.NewGuid():N}"[..18], customer.Id, quotation.Id);

        var scope = await SalesOrderMutationRules.ResolveLiveSourceLockScopeAsync(db, customer.Id, null, pi.Id);

        Assert.True(scope.IsLiveSource);
        Assert.Equal(quotation.Id, scope.QuotationId);
        Assert.Equal(pi.Id, scope.PiId);
    }

    [Fact]
    public async Task 加锁范围解析_无法解析的历史值不取来源锁()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);

        // 完全不存在 → 显式历史值：不构成实时链接，因此不取任何来源锁。
        var scope = await SalesOrderMutationRules.ResolveLiveSourceLockScopeAsync(db, customer.Id, null, 999_999L);
        Assert.False(scope.IsLiveSource);
        Assert.Null(scope.PiId);
    }

    [Fact]
    public async Task 加锁范围解析_已删除来源与异客户来源按既有口径拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var other = SeedCustomer(db);

        var deleted = SeedPi(db, $"PI-{Guid.NewGuid():N}"[..18], customer.Id, null);
        deleted.IsDeleted = true;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<BusinessException>(() => SalesOrderMutationRules
            .ResolveLiveSourceLockScopeAsync(db, customer.Id, null, deleted.Id));

        var foreign = SeedPi(db, $"PI-{Guid.NewGuid():N}"[..18], other.Id, null);
        await Assert.ThrowsAsync<BusinessException>(() => SalesOrderMutationRules
            .ResolveLiveSourceLockScopeAsync(db, customer.Id, null, foreign.Id));

        // Try 变体把业务拒绝收敛为「无可加锁的实时来源」，把是否放行留给后续锁内复核。
        var tolerant = await SalesOrderMutationRules
            .TryResolveLiveSourceLockScopeAsync(db, customer.Id, null, deleted.Id);
        Assert.False(tolerant.IsLiveSource);
    }

    // ==================== 4. 控制器普通写入协议（内存库） ====================

    [Fact]
    public async Task 手工订单_提交与审核_进入协议且保持合法流转()
    {
        using var db = TestDbFactory.Create();
        SeedMasterFixtures(db);
        var controller = PrivilegedController(db);
        var order = SeedOrder(db, DocumentStatus.Pending);

        Assert.IsType<OkObjectResult>(await controller.Submit(order.Id));
        Assert.Equal(DocumentStatus.Submitted, db.SalesOrders.Single(o => o.Id == order.Id).Status);

        Assert.IsType<OkObjectResult>(await controller.Approve(order.Id));
        Assert.Equal(DocumentStatus.Approved, db.SalesOrders.Single(o => o.Id == order.Id).Status);
    }

    [Fact]
    public async Task 过期删除与编辑_并发提交后一律拒绝且原始证据不变()
    {
        using var db = TestDbFactory.Create();
        SeedMasterFixtures(db);
        var controller = PrivilegedController(db);
        var order = SeedOrder(db, DocumentStatus.Pending);

        await controller.Submit(order.Id);

        var delete = await Assert.ThrowsAsync<BusinessException>(() => controller.Delete(order.Id));
        Assert.Equal(ErrorCodes.RuleConflict, delete.Code);
        var update = await Assert.ThrowsAsync<BusinessException>(
            () => controller.Update(order.Id, NewBody()));
        Assert.Equal(ErrorCodes.RuleConflict, update.Code);

        var persisted = db.SalesOrders.AsNoTracking().Single(o => o.Id == order.Id);
        Assert.Equal(DocumentStatus.Submitted, persisted.Status);
        Assert.False(persisted.IsDeleted);
        Assert.Equal(1, db.SalesOrderDetails.AsNoTracking().Count(d => d.SalesOrderId == order.Id));
    }

    [Theory]
    [InlineData(DocumentStatus.Approved)]
    [InlineData(DocumentStatus.Cancelled)]
    [InlineData(DocumentStatus.Rejected)]
    public async Task 非待提交订单_编辑删除提交审核一律拒绝且不复活(DocumentStatus status)
    {
        using var db = TestDbFactory.Create();
        var controller = PrivilegedController(db);
        var order = SeedOrder(db, status);

        await Assert.ThrowsAsync<BusinessException>(() => controller.Update(order.Id, NewBody()));
        await Assert.ThrowsAsync<BusinessException>(() => controller.Delete(order.Id));
        await Assert.ThrowsAsync<BusinessException>(() => controller.Submit(order.Id));
        await Assert.ThrowsAsync<BusinessException>(() => controller.Approve(order.Id));

        var persisted = db.SalesOrders.AsNoTracking().Single(o => o.Id == order.Id);
        Assert.Equal(status, persisted.Status);
        Assert.False(persisted.IsDeleted);
    }

    [Fact]
    public async Task 已删除订单_不可复活且返回同一非披露错误()
    {
        using var db = TestDbFactory.Create();
        var controller = PrivilegedController(db);
        var order = SeedOrder(db, DocumentStatus.Pending);
        order.IsDeleted = true;
        await db.SaveChangesAsync();

        var update = await Assert.ThrowsAsync<BusinessException>(() => controller.Update(order.Id, NewBody()));
        Assert.Equal(ErrorCodes.NotFound, update.Code);
        var delete = await Assert.ThrowsAsync<BusinessException>(() => controller.Delete(order.Id));
        Assert.Equal(ErrorCodes.NotFound, delete.Code);
        var submit = await Assert.ThrowsAsync<BusinessException>(() => controller.Submit(order.Id));
        Assert.Equal(ErrorCodes.NotFound, submit.Code);
    }

    [Fact]
    public async Task 编辑_手工订单_明细整体替换并重算合计()
    {
        using var db = TestDbFactory.Create();
        SeedMasterFixtures(db);
        var controller = PrivilegedController(db);
        var order = SeedOrder(db, DocumentStatus.Pending);

        Assert.IsType<OkObjectResult>(await controller.Update(order.Id, NewBody(quantity: 20m, unitPrice: 60m)));

        var persisted = db.SalesOrders.AsNoTracking().Single(o => o.Id == order.Id);
        Assert.Equal(1200m, persisted.TotalAmount);
        Assert.Null(persisted.SourcePiId);
        Assert.Null(persisted.SourceQuotationId);
        Assert.Equal(1, db.SalesOrderDetails.AsNoTracking().Count(d => d.SalesOrderId == order.Id));
        Assert.Equal(20m, db.SalesOrderDetails.AsNoTracking()
            .Single(d => d.SalesOrderId == order.Id).Quantity);
    }

    [Fact]
    public async Task 编辑_校验失败_回滚半成品明细并清空变更跟踪器()
    {
        using var db = TestDbFactory.Create();
        var controller = PrivilegedController(db);
        var order = SeedOrder(db, DocumentStatus.Pending);

        var failure = await Assert.ThrowsAsync<BusinessException>(() => controller.Update(order.Id,
            NewBody(details: 3, commissionRatio: 200m)));
        Assert.Equal(ErrorCodes.InvalidParameter, failure.Code);

        // 失败后不得残留「已删除旧明细 + 已新增新明细」的半成品变更。
        Assert.Empty(db.ChangeTracker.Entries());
        var persisted = db.SalesOrders.AsNoTracking().Single(o => o.Id == order.Id);
        Assert.Equal(1000m, persisted.TotalAmount);
        Assert.Equal(1, db.SalesOrderDetails.AsNoTracking().Count(d => d.SalesOrderId == order.Id));
    }

    // ==================== 5. 控制器源码协议契约 ====================

    [Fact]
    public void 控制器源码_普通写入统一进入确定性锁协议()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ERP.Api", "Controllers",
            "SalesOrderController.cs"));

        // 修改 / 删除 / 提交 / 审核：统一事务入口 + 确定性锁 + 锁内权威重读与过期拒绝。
        Assert.Contains("SalesOrderMutationRules.BeginMutationTransactionAsync", source);
        Assert.Contains("SalesOrderSourceLineageRules.LockSalesOrderRowAsync", source);
        Assert.Contains("SalesOrderMutationRules.TryResolveLiveSourceLockScopeAsync", source);
        Assert.Contains("SalesOrderMutationRules.PersistedSourceUnchanged", source);
        Assert.Contains("SalesOrderMutationRules.EnsureEditable", source);
        Assert.Contains("SalesOrderMutationRules.EnsureDeletable", source);
        Assert.Contains("SalesOrderMutationRules.EnsureTransitionAllowed", source);
        // 删除不再直接复用基类（基类不取锁 / 不开事务），而是进入同一协议。
        Assert.Contains("public override async Task<IActionResult> Delete(long id)", source);
        Assert.DoesNotContain("return await base.Delete(id);", source);
    }

    // ==================== 工厂与种子数据 ====================

    private static string RepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static SalesOrderController PrivilegedController(ErpDbContext db)
    {
        var controller = new SalesOrderController(db, new DocumentNumberService(db));
        // ERP-465：普通写入入口的实时身份 / 账号状态 / 既有「销售订单」菜单口径与请求形状无关；
        // 夹具绑定真实的既有启用身份 + 既有「销售订单」菜单授权（特权口径，数据范围不受限），刻意不设置 Request.Path。
        TestAuth.SetUser(controller, SeedAuthorizedActor(db));
        return controller;
    }

    /// <summary>
    /// ERP-465：在隔离内存测试数据里播种<b>既有启用身份 + 既有「销售订单」（<c>sales-order</c>）菜单授权</b>
    /// （真实角色 → 菜单口径，菜单编码与 <c>SeedData</c> 同源，不新增任何生产权限），返回其用户 Id。
    /// </summary>
    private static long SeedAuthorizedActor(ErpDbContext db)
    {
        var menu = db.SysMenus.FirstOrDefault(
            m => m.MenuCode == SalesOrderExecutionAuthorizationRules.RequiredMenuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu
            {
                MenuCode = SalesOrderExecutionAuthorizationRules.RequiredMenuCode,
                MenuName = SalesOrderExecutionAuthorizationRules.RequiredMenuText,
                MenuType = MenuType.Menu
            };
            db.SysMenus.Add(menu);
            db.SaveChanges();
        }

        var role = new SysRole
        {
            RoleCode = $"so-mut-{Guid.NewGuid():N}",
            RoleName = "销售订单写入测试角色",
            IsSystem = true
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"so-mut-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "销售订单写入测试账号",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        return user.Id;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-{Guid.NewGuid():N}", CustomerName = "ERP-421 客户", DepositRatio = 30m
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static Quotation SeedQuotation(ErpDbContext db, string no, long customerId)
    {
        var quotation = new Quotation
        {
            QuotationNo = no, QuotationDate = DateTime.Today, CustomerId = customerId,
            CustomerName = "ERP-421 客户", Currency = Currency.USD, ExchangeRate = 7.2m,
            SalesmanId = 66L, Status = DocumentStatus.Approved,
            Details = new List<QuotationDetail>
            {
                new()
                {
                    SortNo = 1, ProductId = 11, ProductCode = "P-1", ProductName = "商品 A",
                    Spec = "大", Unit = "PCS", Quantity = 100m, UnitPrice = 2.5m, Amount = 250m
                }
            }
        };
        db.Quotations.Add(quotation);
        db.SaveChanges();
        return quotation;
    }

    private static ProformaInvoice SeedPi(ErpDbContext db, string no, long customerId, long? quotationId)
    {
        var pi = new ProformaInvoice
        {
            PiNo = no, PiDate = DateTime.Today, QuotationId = quotationId,
            QuotationNo = quotationId is null ? string.Empty : no,
            CustomerId = customerId, CustomerName = "ERP-421 客户",
            Currency = Currency.USD, ExchangeRate = 7.2m, TotalAmount = 1000m, DepositRatio = 30m,
            SalesmanId = 66L, Status = DocumentStatus.Approved,
            Details = new List<ProformaInvoiceDetail>
            {
                new()
                {
                    SortNo = 1, ProductId = 21, ProductCode = "PX-1", ProductName = "PI 商品",
                    Spec = "标准", Unit = "PCS", Quantity = 100m, UnitPrice = 10m, Amount = 1000m
                }
            }
        };
        db.ProformaInvoices.Add(pi);
        db.SaveChanges();
        return pi;
    }

    private static SalesOrder SeedOrder(ErpDbContext db, DocumentStatus status)
    {
        var order = new SalesOrder
        {
            OrderNo = $"SO-{Guid.NewGuid():N}"[..18], OrderDate = DateTime.Today, CustomerId = 1L,
            SalesmanId = 1L, Currency = Currency.USD, ExchangeRate = 7.2m, DepositRatio = 30m,
            DepositAmount = 300m, TotalAmount = 1000m, Status = status,
            Details = new List<SalesOrderDetail>
            {
                new()
                {
                    ProductId = 21, ProductName = "PI 商品", Spec = "标准", Unit = "PCS",
                    Quantity = 100m, UnitPrice = 10m, Amount = 1000m
                }
            }
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static SalesOrder NewBody(decimal quantity = 20m, decimal unitPrice = 60m,
        int details = 1, decimal commissionRatio = 0m)
    {
        var body = new SalesOrder
        {
            OrderDate = DateTime.Today, CustomerId = 1L, SalesmanId = 1L, Currency = Currency.USD,
            ExchangeRate = 7.2m, DepositRatio = 30m, CommissionRatio = commissionRatio,
            Details = new List<SalesOrderDetail>()
        };
        for (var i = 0; i < details; i++)
            body.Details.Add(new SalesOrderDetail
            {
                ProductId = 100 + i, ProductName = $"手填商品 {i}", Spec = "标准", Unit = "PCS",
                Quantity = quantity, UnitPrice = unitPrice
            });
        return body;
    }

    /// <summary>
    /// ERP-423：播种既有合法主数据（客户 / 在职业务员 / 商品）供规范销售订单写入使用；
    /// 只补寄存器中缺失的行，绝不改写生产校验口径。
    /// </summary>
    private static void SeedMasterFixtures(ErpDbContext db)
    {
        if (!db.BaseCustomers.Any(c => c.Id == 1L))
            db.BaseCustomers.Add(new BaseCustomer
            {
                Id = 1L, CustomerCode = "C-MR-1", CustomerName = "ERP423 客户", Status = 1, DepositRatio = 30m
            });
        if (!db.BaseEmployees.Any(e => e.Id == 1L))
            db.BaseEmployees.Add(new BaseEmployee
            {
                Id = 1L, EmployeeCode = "E-MR-1", EmployeeName = "ERP423 业务员", IsSalesman = true, Status = 1
            });

        foreach (var id in new[] { 21L, 100L, 101L, 102L })
        {
            if (!db.BaseProducts.Any(p => p.Id == id))
                db.BaseProducts.Add(new BaseProduct
                {
                    Id = id, ProductCode = $"P-MR-{id}", ProductName = $"ERP423 商品 {id}", Status = 1
                });
        }

        db.SaveChanges();
    }
}
