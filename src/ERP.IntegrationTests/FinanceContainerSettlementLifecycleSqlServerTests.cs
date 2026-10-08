using ERP.Api.Controllers;
using ERP.Application.Common;
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
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-384 装柜结算单生命周期护栏 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）。
/// <list type="number">
/// <item><b>真实控制器</b>：以既有「装柜结算单」（<c>container-settlement</c>）菜单授权 + 既有业务员数据范围口径驱动真实
/// <see cref="FinanceContainerSettlementController"/>，验证实时启用身份、客户范围、金额（正总金额 / 非负费用，既有存储精度）、
/// 来源装柜清单精确资格（悬空 / 已删除 / 已取消 / 跨客户 / 多参与方越范围一律拒绝），以及被拒编辑不改写（含审计时间戳）；</item>
/// <item><b>装柜清单取消协同</b>：存在未删除且未取消的装柜结算单时拒绝取消装柜清单，显式取消结算单后释放；</item>
/// <item><b>三个独立连接竞态</b>：并发「编辑 vs 提交」「审核 vs 取消」「装柜清单取消 vs 装柜结算单创建」经同一把
/// 来源装柜清单行锁 + 装柜结算单行锁串行化后给出唯一一致结果：绝不出现陈旧来源、半成品写入或丢失更新，
/// 也不产生任何资金记账 / 收款 / 库存流水。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class FinanceContainerSettlementLifecycleSqlServerTests
    : IClassFixture<FinanceContainerSettlementLifecycleSqlServerFixture>
{
    private readonly FinanceContainerSettlementLifecycleSqlServerFixture _fixture;

    public FinanceContainerSettlementLifecycleSqlServerTests(
        FinanceContainerSettlementLifecycleSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(FinanceContainerSettlementLifecycleSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 控制器 / 双连接脚手架 ====================

    private static FinanceContainerSettlementController NewSettlementController(ErpDbContext db, long? userId)
    {
        var controller = new FinanceContainerSettlementController(db, new DocumentNumberService(db));
        SetUser(controller, userId);
        return controller;
    }

    private static ContainerLoadingListController NewLoadingListController(ErpDbContext db, long? userId)
    {
        var controller = new ContainerLoadingListController(db, new DocumentNumberService(db));
        SetUser(controller, userId);
        return controller;
    }

    private static void SetUser(ControllerBase controller, long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
    }

    private async Task<(bool Success, string Error)> TrySettlementActionAsync(
        long? userId, Func<FinanceContainerSettlementController, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await action(NewSettlementController(db, userId));
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool Success, string Error)> TryLoadingCancelAsync(long? userId, long loadingListId)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await NewLoadingListController(db, userId).Cancel(loadingListId);
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>两条独立连接在同一起点同时发起动作（各自独立 DbContext / 连接 / 事务）。</summary>
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

    private static async Task<BusinessException> AssertBusinessCodeAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    // ==================== 种子（既有菜单 / 既有业务员数据范围，不新增权限模型） ====================

    private static async Task<BaseCustomer> SeedCustomerAsync(
        ErpDbContext db, string code, string name, int status = 1, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = status,
            CreditStatus = "正常",
            EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<ContainerLoadingList> SeedLoadingListAsync(
        ErpDbContext db, string listNo, string containerNo, long customerId,
        DocumentStatus status = DocumentStatus.Pending)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = listNo,
            LoadingDate = DateTime.Today.AddDays(-4),
            ContainerNo = containerNo,
            CustomerId = customerId,
            Status = status
        };
        db.ContainerLoadingLists.Add(list);
        await db.SaveChangesAsync();
        return list;
    }

    private static async Task<ContainerLoadingListParticipant> SeedParticipantAsync(
        ErpDbContext db, ContainerLoadingList list, BaseCustomer customer, bool primary = false)
    {
        var participant = new ContainerLoadingListParticipant
        {
            LoadingListId = list.Id,
            CustomerId = customer.Id,
            CustomerCode = customer.CustomerCode,
            CustomerName = customer.CustomerName,
            IsPrimary = primary,
            Status = ContainerLoadingParticipantRules.ActiveStatus
        };
        db.ContainerLoadingListParticipants.Add(participant);
        await db.SaveChangesAsync();
        return participant;
    }

    private static async Task<FinanceContainerSettlement> SeedSettlementAsync(
        ErpDbContext db, string settlementNo, long? loadingListId, long customerId,
        decimal totalAmount = 1000m, decimal freightCost = 0m, decimal otherCost = 0m,
        DocumentStatus status = DocumentStatus.Pending)
    {
        var settlement = new FinanceContainerSettlement
        {
            SettlementNo = settlementNo,
            SettlementDate = DateTime.Today.AddDays(-2),
            LoadingListId = loadingListId,
            CustomerId = customerId,
            TotalAmount = totalAmount,
            FreightCost = freightCost,
            OtherCost = otherCost,
            Remark = "原始备注",
            Status = status
        };
        db.FinanceContainerSettlements.Add(settlement);
        await db.SaveChangesAsync();
        return settlement;
    }

    /// <summary>
    /// 播种一个真实登录账号：既有「装柜结算单」（与可选「装柜清单」）菜单授权 + 业务员员工映射
    /// （把范围内客户分配给它的员工，数据范围恰好覆盖该客户，不新增权限模型）；可选禁用账号 / 不授予菜单用于
    /// fail closed 场景。
    /// </summary>
    private static async Task<(long UserId, long RoleId)> SeedOperatorAsync(
        ErpDbContext db, long inScopeCustomerId, bool withSettlementMenu = true,
        bool withLoadingListMenu = true, UserStatus status = UserStatus.Enabled)
    {
        var code = $"erp384-op-{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code,
            EmployeeName = code,
            IsSalesman = true,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = code,
            DisplayName = code,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "装柜结算操作角色",
            RoleCode = $"Erp384Op-{Guid.NewGuid():N}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (withSettlementMenu)
            await GrantMenuAsync(db, role.Id, FinanceContainerSettlementLifecycleRules.RequiredMenuCode);
        if (withLoadingListMenu)
            await GrantMenuAsync(db, role.Id, LoadingListAuthorizationRules.RequiredMenuCode);

        var customer = await db.BaseCustomers.SingleAsync(c => c.Id == inScopeCustomerId);
        customer.EmpId = employee.Id;
        await db.SaveChangesAsync();

        return (user.Id, role.Id);
    }

    /// <summary>授予既有种子菜单（不新增菜单 / 权限模型）。</summary>
    private static async Task GrantMenuAsync(ErpDbContext db, long roleId, string menuCode)
    {
        var menuId = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == menuCode && !m.IsDeleted)
            .Select(m => m.Id)
            .FirstAsync();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
        await db.SaveChangesAsync();
    }

    private static async Task RevokeMenuAsync(ErpDbContext db, long roleId, string menuCode)
    {
        var menuId = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == menuCode && !m.IsDeleted)
            .Select(m => m.Id)
            .FirstAsync();
        foreach (var grant in await db.SysRoleMenus
                     .Where(g => g.RoleId == roleId && g.MenuId == menuId && !g.IsDeleted).ToListAsync())
            grant.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    private static async Task DisableUserAsync(ErpDbContext db, long userId)
    {
        var user = await db.SysUsers.SingleAsync(u => u.Id == userId);
        user.Status = UserStatus.Disabled;
        await db.SaveChangesAsync();
    }

    // ==================== 只读复核 / 非目标表计数 ====================

    private async Task<FinanceContainerSettlement> ReloadSettlementAsync(long settlementId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.FinanceContainerSettlements.AsNoTracking().SingleAsync(s => s.Id == settlementId);
    }

    private async Task<DocumentStatus> ReloadLoadingListStatusAsync(long loadingListId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.ContainerLoadingLists.AsNoTracking().Where(l => l.Id == loadingListId)
            .Select(l => l.Status).SingleAsync();
    }

    private async Task<int> LiveSettlementCountAsync(long loadingListId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.FinanceContainerSettlements.AsNoTracking()
            .CountAsync(s => s.LoadingListId == loadingListId && !s.IsDeleted
                             && s.Status != DocumentStatus.Cancelled);
    }

    /// <summary>既有资金 / 库存记账表条数合计：本护栏绝不产生任何新的收款 / 付款 / 费用 / 库存流水。</summary>
    private async Task<int> PostingRowCountAsync()
    {
        await using var db = _fixture.CreateDbContext();
        return await db.FinancePayments.AsNoTracking().CountAsync()
            + await db.FinanceReceipts.AsNoTracking().CountAsync()
            + await db.FinanceExpenses.AsNoTracking().CountAsync()
            + await db.FinanceBulkSettlements.AsNoTracking().CountAsync()
            + await db.StockMovements.AsNoTracking().CountAsync();
    }

    // ==================== 1. 真实身份 / 菜单 / 数据范围（own / foreign / revoked / disabled） ====================

    [Fact]
    public async Task Controller_OwnScopeAllowed_AndForeignRevokedDisabledDenied()
    {
        Guard();
        var tag = Tag();
        long ownerId, foreignId, revokedId, revokedRoleId, disabledId;
        long ownSettlementId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var ownerCustomer = await SeedCustomerAsync(seed, $"INT_E384_OWN_{tag}", "本人客户");
            ownerId = (await SeedOperatorAsync(seed, ownerCustomer.Id)).UserId;
            ownSettlementId = (await SeedSettlementAsync(seed, $"INT_E384_SOWN_{tag}", null, ownerCustomer.Id)).Id;

            // 范围外操作员：其客户范围不含 ownerCustomer（结算单属于 ownerCustomer）
            var foreignCustomer = await SeedCustomerAsync(seed, $"INT_E384_FOR_{tag}", "范围外客户");
            foreignId = (await SeedOperatorAsync(seed, foreignCustomer.Id)).UserId;

            var revokedCustomer = await SeedCustomerAsync(seed, $"INT_E384_REV_{tag}", "撤销菜单客户");
            (revokedId, revokedRoleId) = await SeedOperatorAsync(seed, revokedCustomer.Id);
            await RevokeMenuAsync(seed, revokedRoleId, FinanceContainerSettlementLifecycleRules.RequiredMenuCode);

            var disabledCustomer = await SeedCustomerAsync(seed, $"INT_E384_DIS_{tag}", "禁用账号客户");
            disabledId = (await SeedOperatorAsync(seed, disabledCustomer.Id)).UserId;
            await DisableUserAsync(seed, disabledId);
        }

        var before = await ReloadSettlementAsync(ownSettlementId);
        var postingsBefore = await PostingRowCountAsync();

        // 本人客户：详情可用
        await using (var db = _fixture.CreateDbContext())
            Assert.IsType<OkObjectResult>(await NewSettlementController(db, ownerId).GetById(ownSettlementId));

        // 范围外客户：详情 / 提交 / 取消拒绝（fail closed）
        await using (var db = _fixture.CreateDbContext())
        {
            var controller = NewSettlementController(db, foreignId);
            await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.GetById(ownSettlementId));
            await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Submit(ownSettlementId));
            await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Cancel(ownSettlementId));
        }

        // 撤销菜单授权：拒绝
        await using (var db = _fixture.CreateDbContext())
            await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
                () => NewSettlementController(db, revokedId).GetById(ownSettlementId));

        // 禁用账号：拒绝
        await using (var db = _fixture.CreateDbContext())
            await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
                () => NewSettlementController(db, disabledId).GetById(ownSettlementId));

        // 被拒请求绝不改写原始结算单（含审计时间戳）与非目标记账表
        var after = await ReloadSettlementAsync(ownSettlementId);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.TotalAmount, after.TotalAmount);
        Assert.Equal(before.CustomerId, after.CustomerId);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.False(after.IsDeleted);
        Assert.Equal(postingsBefore, await PostingRowCountAsync());
    }

    // ==================== 2. 金额 / 来源清单资格（invalid / foreign / cancelled） ====================

    [Fact]
    public async Task Controller_InvalidAmountsAndIllegalSourceList_Denied_NoWrite()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, cancelledListId, foreignListId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E384_CINV_{tag}", "校验客户");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;

            cancelledListId = (await SeedLoadingListAsync(
                seed, $"INT_E384_LGC_{tag}", $"TCLU-C-{tag}", customer.Id, DocumentStatus.Cancelled)).Id;

            var foreignCustomer = await SeedCustomerAsync(seed, $"INT_E384_CFOR_{tag}", "他客户");
            foreignListId = (await SeedLoadingListAsync(
                seed, $"INT_E384_LGF_{tag}", $"TCLU-F-{tag}", foreignCustomer.Id)).Id;
        }
        var postingsBefore = await PostingRowCountAsync();

        await using (var db = _fixture.CreateDbContext())
        {
            var controller = NewSettlementController(db, userId);

            await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter, () => controller.Create(
                new FinanceContainerSettlement { CustomerId = customerId, SettlementDate = DateTime.Today, TotalAmount = 0m }));
            await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter, () => controller.Create(
                new FinanceContainerSettlement { CustomerId = customerId, SettlementDate = DateTime.Today, TotalAmount = 100m, FreightCost = -1m }));
            await AssertBusinessCodeAsync(ErrorCodes.NotFound, () => controller.Create(
                new FinanceContainerSettlement { CustomerId = customerId, SettlementDate = DateTime.Today, TotalAmount = 100m, LoadingListId = 99999999L }));
            await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Create(
                new FinanceContainerSettlement { CustomerId = customerId, SettlementDate = DateTime.Today, TotalAmount = 100m, LoadingListId = cancelledListId }));
            await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Create(
                new FinanceContainerSettlement { CustomerId = customerId, SettlementDate = DateTime.Today, TotalAmount = 100m, LoadingListId = foreignListId }));
        }

        await using (var db = _fixture.CreateDbContext())
            Assert.Equal(0, await db.FinanceContainerSettlements.AsNoTracking().CountAsync(s => s.CustomerId == customerId));
        Assert.Equal(postingsBefore, await PostingRowCountAsync());
    }

    // ==================== 3. 被拒编辑不改写 + 装柜清单取消协同 ====================

    [Fact]
    public async Task Controller_DeniedEditsUnchanged_AndLinkedSettlementBlocksLoadingCancelUntilReleased()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, loadingListId, settlementId, cancelledListId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E384_CLNK_{tag}", "联动客户");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;
            loadingListId = (await SeedLoadingListAsync(
                seed, $"INT_E384_LGL_{tag}", $"TCLU-L-{tag}", customer.Id)).Id;
            settlementId = (await SeedSettlementAsync(
                seed, $"INT_E384_SLNK_{tag}", loadingListId, customer.Id, 1000m,
                status: DocumentStatus.Pending)).Id;
            cancelledListId = (await SeedLoadingListAsync(
                seed, $"INT_E384_LGX_{tag}", $"TCLU-X-{tag}", customer.Id, DocumentStatus.Cancelled)).Id;
        }
        var before = await ReloadSettlementAsync(settlementId);
        var postingsBefore = await PostingRowCountAsync();

        // 非法金额 / 非法来源（已取消清单）：编辑拒绝且不改写
        await using (var db = _fixture.CreateDbContext())
        {
            var controller = NewSettlementController(db, userId);
            await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter, () => controller.Update(settlementId,
                new FinanceContainerSettlement
                {
                    CustomerId = customerId, SettlementDate = DateTime.Today,
                    LoadingListId = loadingListId, TotalAmount = 0m
                }));
        }
        await using (var db = _fixture.CreateDbContext())
        {
            var controller = NewSettlementController(db, userId);
            await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Update(settlementId,
                new FinanceContainerSettlement
                {
                    CustomerId = customerId, SettlementDate = DateTime.Today,
                    LoadingListId = cancelledListId, TotalAmount = 300m
                }));
        }

        var denied = await ReloadSettlementAsync(settlementId);
        Assert.Equal(before.Status, denied.Status);
        Assert.Equal(before.TotalAmount, denied.TotalAmount);
        Assert.Equal(before.LoadingListId, denied.LoadingListId);
        Assert.Equal(before.CustomerId, denied.CustomerId);
        Assert.Equal(before.UpdatedAt, denied.UpdatedAt);

        // 存在未取消结算单：拒绝取消装柜清单（清单保持原状态）
        var refused = await TryLoadingCancelAsync(userId, loadingListId);
        Assert.False(refused.Success);
        Assert.Contains("装柜结算单", refused.Error);
        Assert.Equal(DocumentStatus.Pending, await ReloadLoadingListStatusAsync(loadingListId));

        // 显式取消结算单释放护栏（历史与审计保留，不物理删除）
        await using (var db = _fixture.CreateDbContext())
            Assert.IsType<OkObjectResult>(await NewSettlementController(db, userId).Cancel(settlementId));

        var released = await TryLoadingCancelAsync(userId, loadingListId);
        Assert.True(released.Success, released.Error);
        Assert.Equal(DocumentStatus.Cancelled, await ReloadLoadingListStatusAsync(loadingListId));

        var retained = await ReloadSettlementAsync(settlementId);
        Assert.Equal(DocumentStatus.Cancelled, retained.Status);
        Assert.False(retained.IsDeleted);
        Assert.Equal(postingsBefore, await PostingRowCountAsync());
    }

    // ==================== 4. 两个独立连接竞态（装柜清单行锁 + 结算单行锁串行化） ====================

    [Fact]
    public async Task Race_Edit_Versus_Submit_SerializedConsistentResult()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, loadingListId, settlementId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E384_RC1_{tag}", "并发客户1");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;
            loadingListId = (await SeedLoadingListAsync(
                seed, $"INT_E384_RGL1_{tag}", $"TCLU-R1-{tag}", customer.Id)).Id;
            settlementId = (await SeedSettlementAsync(
                seed, $"INT_E384_RS1_{tag}", loadingListId, customer.Id, 1000m)).Id;
        }
        var postingsBefore = await PostingRowCountAsync();

        var results = await RaceAsync(
            () => TrySettlementActionAsync(userId, ctl => ctl.Update(settlementId, new FinanceContainerSettlement
            {
                CustomerId = customerId,
                SettlementDate = DateTime.Today,
                LoadingListId = loadingListId,
                TotalAmount = 300m
            })),
            () => TrySettlementActionAsync(userId, ctl => ctl.Submit(settlementId)));

        var edit = results[0];
        var submit = results[1];

        // 同一行竞争可能由 RowVersion 拒绝一个请求；至少一个成功，失败方不得覆盖赢家。
        Assert.True(edit.Success || submit.Success, $"edit={edit.Error}; submit={submit.Error}");
        var applied = await ReloadSettlementAsync(settlementId);
        Assert.Equal(submit.Success ? DocumentStatus.Submitted : DocumentStatus.Pending, applied.Status);
        Assert.Equal(edit.Success ? 300m : 1000m, applied.TotalAmount);
        Assert.Equal(customerId, applied.CustomerId);
        Assert.Equal(loadingListId, applied.LoadingListId);
        Assert.False(applied.IsDeleted);
        Assert.Equal(postingsBefore, await PostingRowCountAsync());
    }

    [Fact]
    public async Task Race_Approve_Versus_Cancel_SerializedConsistentResult()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, loadingListId, settlementId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E384_RC2_{tag}", "并发客户2");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;
            loadingListId = (await SeedLoadingListAsync(
                seed, $"INT_E384_RGL2_{tag}", $"TCLU-R2-{tag}", customer.Id)).Id;
            settlementId = (await SeedSettlementAsync(
                seed, $"INT_E384_RS2_{tag}", loadingListId, customer.Id, 1000m,
                status: DocumentStatus.Submitted)).Id;
        }
        var postingsBefore = await PostingRowCountAsync();

        var results = await RaceAsync(
            () => TrySettlementActionAsync(userId, ctl => ctl.Approve(settlementId)),
            () => TrySettlementActionAsync(userId, ctl => ctl.Cancel(settlementId)));

        var approve = results[0];
        var cancel = results[1];

        // 串行化后绝无「已取消又被审核」或丢失更新：最终状态必须对应成功的操作
        Assert.True(approve.Success || cancel.Success, $"approve={approve.Error}; cancel={cancel.Error}");
        if (!approve.Success) Assert.Contains("当前状态不允许", approve.Error);

        var applied = await ReloadSettlementAsync(settlementId);
        Assert.Equal(cancel.Success ? DocumentStatus.Cancelled : DocumentStatus.Approved, applied.Status);
        Assert.Equal(1000m, applied.TotalAmount);
        Assert.Equal(customerId, applied.CustomerId);
        Assert.False(applied.IsDeleted);
        Assert.Equal(postingsBefore, await PostingRowCountAsync());
    }

    private async Task<(bool Success, string Error)> TrySettlementCreateAsync(
        long? userId, long customerId, long loadingListId)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await NewSettlementController(db, userId).Create(new FinanceContainerSettlement
            {
                CustomerId = customerId,
                SettlementDate = DateTime.Today,
                LoadingListId = loadingListId,
                TotalAmount = 500m
            });
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    [Fact]
    public async Task Race_LoadingCancel_Versus_LinkedSettlementCreate_SerializedConsistentResult()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, loadingListId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E384_RC3_{tag}", "并发客户3");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;
            loadingListId = (await SeedLoadingListAsync(
                seed, $"INT_E384_RGL3_{tag}", $"TCLU-R3-{tag}", customer.Id)).Id;
        }
        var postingsBefore = await PostingRowCountAsync();

        // 装柜清单取消（UPDLOCK 清单行）与装柜结算单创建（先取清单行锁）在同一把清单行锁上串行化
        var results = await RaceAsync(
            () => TryLoadingCancelAsync(userId, loadingListId),
            () => TrySettlementCreateAsync(userId, customerId, loadingListId));

        var cancel = results[0];
        var create = results[1];

        // 二者必然串行：要么取消先完成（创建随后被「已取消来源」拒绝），要么创建先完成（取消被未取消结算单阻断）
        Assert.True(cancel.Success || create.Success, $"cancel={cancel.Error}; create={create.Error}");
        Assert.False(cancel.Success && create.Success,
            "装柜清单取消与装柜结算单创建不可能同时成功（共享同一把清单行锁）");

        var listStatus = await ReloadLoadingListStatusAsync(loadingListId);
        var liveSettlements = await LiveSettlementCountAsync(loadingListId);
        if (cancel.Success)
        {
            Assert.Equal(DocumentStatus.Cancelled, listStatus);
            Assert.Equal(0, liveSettlements);
        }
        else
        {
            Assert.NotEqual(DocumentStatus.Cancelled, listStatus);
            Assert.Equal(1, liveSettlements);
        }

        Assert.Equal(postingsBefore, await PostingRowCountAsync());
    }
}

/// <summary>
/// ERP-384 专用 localdb 夹具：每次运行创建一个全新 GUID 库 + 完整 NEWERP 结构 + 种子数据；
/// 任何库访问之前先过专用目标护栏（实例 <c>NEWERP_AutoAcceptance</c> / 库名前缀 <c>NEWERP_AUTOTEST</c> / 集成安全），
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
/// </summary>
public sealed class FinanceContainerSettlementLifecycleSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP384";

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-384] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-384] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>专用目标护栏的 fail-closed 单元覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class FinanceContainerSettlementLifecycleTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => FinanceContainerSettlementLifecycleSqlServerFixture.AssertDedicatedTarget(connection));
}
