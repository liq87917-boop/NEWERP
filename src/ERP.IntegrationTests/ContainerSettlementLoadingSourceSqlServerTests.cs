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
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-392 装柜结算单来源装柜清单候选 / 已存储来源 + 表单显式选择的真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标，专用 <see cref="ContainerSettlementLoadingSourceSqlServerFixture"/>）。
/// <list type="number">
/// <item><b>真实控制器</b>：既有「装柜结算单」+「装柜清单」菜单与既有业务员数据范围驱动真实
/// <see cref="FinanceContainerSettlementController"/>，验证候选只含<b>精确客户</b>确实是权威归属客户（有效参与方或历史兼容客户）
/// 且未删除、未取消的装柜清单，共享柜越范围参与方 / 越范围上游客户整张不返回；</item>
/// <item><b>伪造来源</b>：候选选择不等于授权——最终保存按 ERP-384 生命周期规则锁内复核精确来源，
/// 伪造 / 已取消 / 跨客户来源一律拒绝且不消耗单号、不落半成品；</item>
/// <item><b>历史只读</b>：来源事后取消 / 删除后，结算单链接原样保留并显式标注，绝不静默清除 / 重绑定；</item>
/// <item><b>两个独立连接竞态</b>：并发「装柜清单取消 vs 结算单创建链接」与「装柜清单取消 vs 结算单修改链接」
/// 在兼容锁序下收敛为唯一一致结果。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，
/// 绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class ContainerSettlementLoadingSourceSqlServerTests
    : IClassFixture<ContainerSettlementLoadingSourceSqlServerFixture>
{
    private readonly ContainerSettlementLoadingSourceSqlServerFixture _fixture;

    public ContainerSettlementLoadingSourceSqlServerTests(ContainerSettlementLoadingSourceSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(ContainerSettlementLoadingSourceSqlServerFixture.DatabasePrefix, target.InitialCatalog,
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

    private static ContainerSettlementLoadingListCandidatePageDto Candidates(IActionResult result)
        => Assert.IsType<ApiResponse<ContainerSettlementLoadingListCandidatePageDto>>(
            Assert.IsType<OkObjectResult>(result).Value).Data!;

    private static ContainerSettlementLoadingSourceViewDto ViewOf(IActionResult result)
        => Assert.IsType<ApiResponse<ContainerSettlementLoadingSourceViewDto>>(
            Assert.IsType<OkObjectResult>(result).Value).Data!;

    // ==================== 种子（既有菜单 / 既有业务员数据范围，不新增权限模型） ====================

    private static async Task<BaseCustomer> SeedCustomerAsync(ErpDbContext db, string code, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code, CustomerName = name, Status = 1, CreditStatus = "正常"
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<ContainerLoadingList> SeedLoadingListAsync(
        ErpDbContext db, string listNo, string containerNo, long customerId,
        DocumentStatus status = DocumentStatus.Pending, long? preLoadingId = null)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = listNo, LoadingDate = DateTime.Today.AddDays(-4), ContainerNo = containerNo,
            CustomerId = customerId, Status = status, PreLoadingId = preLoadingId
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
            LoadingListId = list.Id, CustomerId = customer.Id, CustomerCode = customer.CustomerCode,
            CustomerName = customer.CustomerName, IsPrimary = primary,
            Status = ContainerLoadingParticipantRules.ActiveStatus
        };
        db.ContainerLoadingListParticipants.Add(participant);
        await db.SaveChangesAsync();
        return participant;
    }

    private static async Task<ContainerPreLoading> SeedUpstreamAsync(
        ErpDbContext db, string bookingNo, string preLoadingNo, long bookingCustomerId)
    {
        var booking = new ContainerBooking
        {
            BookingNo = bookingNo, BookingDate = DateTime.Today.AddDays(-10), CustomerId = bookingCustomerId
        };
        db.ContainerBookings.Add(booking);
        await db.SaveChangesAsync();

        var pre = new ContainerPreLoading
        {
            PreLoadingNo = preLoadingNo, LoadingDate = DateTime.Today.AddDays(-8), BookingId = booking.Id
        };
        db.ContainerPreLoadings.Add(pre);
        await db.SaveChangesAsync();
        return pre;
    }

    private static async Task<FinanceContainerSettlement> SeedSettlementAsync(
        ErpDbContext db, string settlementNo, long? loadingListId, long customerId,
        decimal totalAmount = 1000m, DocumentStatus status = DocumentStatus.Pending)
    {
        var settlement = new FinanceContainerSettlement
        {
            SettlementNo = settlementNo, SettlementDate = DateTime.Today.AddDays(-2), LoadingListId = loadingListId,
            CustomerId = customerId, TotalAmount = totalAmount, FreightCost = 0m, OtherCost = 0m,
            Remark = "原始备注", Status = status
        };
        db.FinanceContainerSettlements.Add(settlement);
        await db.SaveChangesAsync();
        return settlement;
    }

    /// <summary>
    /// 播种一个真实登录账号：既有「装柜结算单」（与可选「装柜清单」）菜单授权 + 业务员员工映射
    /// （把范围内客户分配给它的员工，数据范围恰好覆盖该客户，不新增权限模型）；可选禁用账号 / 不授予菜单用于 fail closed 场景。
    /// </summary>
    private static async Task<(long UserId, long RoleId)> SeedOperatorAsync(
        ErpDbContext db, long inScopeCustomerId, bool withSettlementMenu = true,
        bool withLoadingListMenu = true, UserStatus status = UserStatus.Enabled)
    {
        var code = $"erp392-op-{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = code, DisplayName = code, PasswordHash = "hash", PasswordSalt = "salt", Status = status
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "装柜来源操作角色", RoleCode = $"Erp392Op-{Guid.NewGuid():N}", IsSystem = false
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

    // ==================== 1. 候选：own / foreign / shared participant / upstream ====================

    [Fact]
    public async Task Candidates_OwnParticipantAndLegacyVisible_ForeignAndUpstreamOutOfScopeHidden()
    {
        Guard();
        var tag = Tag();
        long userId, ownCustomerId, foreignCustomerId;
        long legacyId, participantId, foreignId, cancelledId, upstreamOutOfScopeId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var own = await SeedCustomerAsync(seed, $"INT_E392_OWN_{tag}", "本人客户");
            ownCustomerId = own.Id;
            userId = (await SeedOperatorAsync(seed, own.Id)).UserId;

            var foreign = await SeedCustomerAsync(seed, $"INT_E392_FOR_{tag}", "范围外客户");
            foreignCustomerId = foreign.Id;

            legacyId = (await SeedLoadingListAsync(seed, $"INT_E392_LG_{tag}", $"TCLU-LG-{tag}", own.Id)).Id;

            var shared = await SeedLoadingListAsync(seed, $"INT_E392_SH_{tag}", $"TCLU-SH-{tag}", own.Id);
            participantId = shared.Id;
            await SeedParticipantAsync(seed, shared, own, primary: true);

            var foreignList = await SeedLoadingListAsync(seed, $"INT_E392_FG_{tag}", $"TCLU-FG-{tag}", foreign.Id);
            foreignId = foreignList.Id;
            await SeedParticipantAsync(seed, foreignList, foreign, primary: true);

            cancelledId = (await SeedLoadingListAsync(
                seed, $"INT_E392_CX_{tag}", $"TCLU-CX-{tag}", own.Id, DocumentStatus.Cancelled)).Id;

            // 精确客户是本人，但显式上游订柜客户是范围外客户：整张不返回（不通过共享出运泄露）
            var pre = await SeedUpstreamAsync(seed, $"INT_E392_BK_{tag}", $"INT_E392_PL_{tag}", foreign.Id);
            upstreamOutOfScopeId = (await SeedLoadingListAsync(
                seed, $"INT_E392_UP_{tag}", $"TCLU-UP-{tag}", own.Id, preLoadingId: pre.Id)).Id;
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var page = Candidates(await NewSettlementController(db, userId)
                .GetLoadingListCandidates(ownCustomerId, null, 0, 0));

            Assert.Contains(page.Items, i => i.LoadingListId == legacyId);
            Assert.Contains(page.Items, i => i.LoadingListId == participantId);
            Assert.DoesNotContain(page.Items, i => i.LoadingListId == foreignId);
            Assert.DoesNotContain(page.Items, i => i.LoadingListId == cancelledId);
            Assert.DoesNotContain(page.Items, i => i.LoadingListId == upstreamOutOfScopeId);

            Assert.All(page.Items, i => Assert.True(i.Eligible));
            Assert.All(page.Items, i => Assert.Equal(ownCustomerId, i.CustomerId));
            Assert.DoesNotContain(page.Items, i => i.CustomerId == foreignCustomerId);
            var legacy = page.Items.Single(i => i.LoadingListId == legacyId);
            Assert.True(legacy.LegacySingleCustomer);
            var participant = page.Items.Single(i => i.LoadingListId == participantId);
            Assert.False(participant.LegacySingleCustomer);
        }
    }

    [Fact]
    public async Task Candidates_SharedParticipantOutOfScope_Hidden()
    {
        Guard();
        var tag = Tag();
        long userId, ownCustomerId, sharedId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var own = await SeedCustomerAsync(seed, $"INT_E392_SOWN_{tag}", "拼柜本人客户");
            ownCustomerId = own.Id;
            userId = (await SeedOperatorAsync(seed, own.Id)).UserId;

            var other = await SeedCustomerAsync(seed, $"INT_E392_SFOR_{tag}", "拼柜他人客户");
            var shared = await SeedLoadingListAsync(seed, $"INT_E392_SSH_{tag}", $"TCLU-SSH-{tag}", own.Id);
            sharedId = shared.Id;
            await SeedParticipantAsync(seed, shared, own, primary: true);
            await SeedParticipantAsync(seed, shared, other);
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var page = Candidates(await NewSettlementController(db, userId)
                .GetLoadingListCandidates(ownCustomerId, null, 0, 0));
            Assert.DoesNotContain(page.Items, i => i.LoadingListId == sharedId);
        }
    }

    // ==================== 2. 实时授权 fail closed（revoked / disabled / 越范围客户） ====================

    [Fact]
    public async Task Candidates_RevokedLoadingListMenuOrDisabledUserOrOutOfScope_Denied()
    {
        Guard();
        var tag = Tag();
        long ownerId, disabledId, noLoadingListMenuUserId, ownerCustomerId, foreignCustomerId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var owner = await SeedCustomerAsync(seed, $"INT_E392_AUTH_{tag}", "授权客户");
            ownerCustomerId = owner.Id;
            ownerId = (await SeedOperatorAsync(seed, owner.Id)).UserId;

            var foreign = await SeedCustomerAsync(seed, $"INT_E392_AUTHF_{tag}", "授权范围外客户");
            foreignCustomerId = foreign.Id;
            await SeedLoadingListAsync(seed, $"INT_E392_AL_{tag}", $"TCLU-AL-{tag}", owner.Id);

            var noLoadingMenu = await SeedCustomerAsync(seed, $"INT_E392_NLM_{tag}", "无装柜清单菜单客户");
            noLoadingListMenuUserId = (await SeedOperatorAsync(seed, noLoadingMenu.Id, withLoadingListMenu: false)).UserId;

            var disabledCustomer = await SeedCustomerAsync(seed, $"INT_E392_DIS_{tag}", "禁用客户");
            disabledId = (await SeedOperatorAsync(seed, disabledCustomer.Id)).UserId;
            await DisableUserAsync(seed, disabledId);
        }

        await using (var db = _fixture.CreateDbContext())
            await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
                () => NewSettlementController(db, noLoadingListMenuUserId).GetLoadingListCandidates(ownerCustomerId, null, 0, 0));
        await using (var db = _fixture.CreateDbContext())
            await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
                () => NewSettlementController(db, disabledId).GetLoadingListCandidates(ownerCustomerId, null, 0, 0));
        await using (var db = _fixture.CreateDbContext())
            await AssertBusinessCodeAsync(ErrorCodes.Unauthorized,
                () => NewSettlementController(db, null).GetLoadingListCandidates(ownerCustomerId, null, 0, 0));
        await using (var db = _fixture.CreateDbContext())
            await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
                () => NewSettlementController(db, ownerId).GetLoadingListCandidates(foreignCustomerId, null, 0, 0));
    }

    // ==================== 3. 已存储来源只读：取消 / 删除后原样保留并显式标注 ====================

    [Fact]
    public async Task StoredSource_AfterCancelOrDelete_ReadOnlyPreservedWithAnnotation()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, livelyId, cancelledId, deletedId, unlinkedId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E392_HIST_{tag}", "历史客户");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;

            var lively = await SeedLoadingListAsync(seed, $"INT_E392_HL_{tag}", $"TCLU-HL-{tag}", customer.Id);
            livelyId = lively.Id;
            var cancelled = await SeedLoadingListAsync(
                seed, $"INT_E392_HC_{tag}", $"TCLU-HC-{tag}", customer.Id, DocumentStatus.Cancelled);
            cancelledId = cancelled.Id;
            var deleted = await SeedLoadingListAsync(seed, $"INT_E392_HD_{tag}", $"TCLU-HD-{tag}", customer.Id);
            deleted.IsDeleted = true;
            await seed.SaveChangesAsync();
            deletedId = deleted.Id;

            unlinkedId = (await SeedSettlementAsync(seed, $"INT_E392_SU_{tag}", null, customer.Id)).Id;
        }

        long linkedSettlementId, cancelledSettlementId, deletedSettlementId;
        await using (var seed = _fixture.CreateDbContext())
        {
            linkedSettlementId = (await SeedSettlementAsync(seed, $"INT_E392_SL_{tag}", livelyId, customerId)).Id;
            cancelledSettlementId = (await SeedSettlementAsync(seed, $"INT_E392_SC_{tag}", cancelledId, customerId)).Id;
            deletedSettlementId = (await SeedSettlementAsync(seed, $"INT_E392_SD_{tag}", deletedId, customerId)).Id;
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var ctl = NewSettlementController(db, userId);

            var unlinked = ViewOf(await ctl.GetLoadingListSource(unlinkedId));
            Assert.False(unlinked.Linked);
            Assert.Contains("未关联", unlinked.Annotation);

            var lively = ViewOf(await ctl.GetLoadingListSource(linkedSettlementId));
            Assert.True(lively.Linked);
            Assert.True(lively.EligibleForNewLink);
            Assert.Equal(livelyId, lively.LoadingListId);

            var cancelled = ViewOf(await ctl.GetLoadingListSource(cancelledSettlementId));
            Assert.True(cancelled.Linked);
            Assert.True(cancelled.Cancelled);
            Assert.False(cancelled.EligibleForNewLink);
            Assert.Contains("已取消", cancelled.Annotation);

            var deleted = ViewOf(await ctl.GetLoadingListSource(deletedSettlementId));
            Assert.True(deleted.Unavailable);
            Assert.Contains("不可用", deleted.Annotation);

            // 只读展示绝不改写链接
            var stored = await db.FinanceContainerSettlements.AsNoTracking()
                .SingleAsync(s => s.Id == cancelledSettlementId);
            Assert.Equal(cancelledId, stored.LoadingListId);
        }
    }

    // ==================== 4. 伪造保存拒绝且不落半成品 ====================

    [Fact]
    public async Task Save_ForgedForeignOrCancelledSource_DeniedWithoutWrite()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, foreignId, cancelledId, validId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E392_SAVE_{tag}", "保存客户");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;

            var foreign = await SeedCustomerAsync(seed, $"INT_E392_SAVEF_{tag}", "保存范围外客户");
            foreignId = (await SeedLoadingListAsync(seed, $"INT_E392_SF_{tag}", $"TCLU-SF-{tag}", foreign.Id)).Id;
            cancelledId = (await SeedLoadingListAsync(
                seed, $"INT_E392_SCX_{tag}", $"TCLU-SCX-{tag}", customer.Id, DocumentStatus.Cancelled)).Id;
            validId = (await SeedLoadingListAsync(seed, $"INT_E392_SOK_{tag}", $"TCLU-SOK-{tag}", customer.Id)).Id;
        }

        var postingsBefore = await PostingRowCountAsync();

        await using (var db = _fixture.CreateDbContext())
            await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => NewSettlementController(db, userId).Create(
                new FinanceContainerSettlement
                {
                    CustomerId = customerId, SettlementDate = DateTime.Today, TotalAmount = 100m, LoadingListId = foreignId
                }));
        await using (var db = _fixture.CreateDbContext())
            await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => NewSettlementController(db, userId).Create(
                new FinanceContainerSettlement
                {
                    CustomerId = customerId, SettlementDate = DateTime.Today, TotalAmount = 100m, LoadingListId = cancelledId
                }));

        // 被拒保存绝不落半成品
        await using (var db = _fixture.CreateDbContext())
            Assert.Equal(0, await db.FinanceContainerSettlements.AsNoTracking().CountAsync(s => s.CustomerId == customerId));

        // 精确来源可用：真正写入且带出正确链接
        await using (var db = _fixture.CreateDbContext())
            Assert.IsType<OkObjectResult>(await NewSettlementController(db, userId).Create(
                new FinanceContainerSettlement
                {
                    CustomerId = customerId, SettlementDate = DateTime.Today, TotalAmount = 100m, LoadingListId = validId
                }));

        await using (var db = _fixture.CreateDbContext())
        {
            var created = await db.FinanceContainerSettlements.AsNoTracking().SingleAsync(s => s.CustomerId == customerId);
            Assert.Equal(validId, created.LoadingListId);
        }

        Assert.Equal(postingsBefore, await PostingRowCountAsync());
    }

    // ==================== 5. 两个独立连接竞态（装柜清单行锁串行化） ====================

    [Fact]
    public async Task Race_LoadingListCancel_Versus_SettlementCreate_SerializedConsistentResult()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, loadingListId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E392_RC1_{tag}", "并发客户1");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;
            loadingListId = (await SeedLoadingListAsync(
                seed, $"INT_E392_RGL1_{tag}", $"TCLU-R1-{tag}", customer.Id)).Id;
        }
        var postingsBefore = await PostingRowCountAsync();

        // 装柜清单取消（UPDLOCK 清单行）与结算单创建（先取清单行锁）在同一把清单行锁上串行化
        var results = await RaceAsync(
            () => TryLoadingCancelAsync(userId, loadingListId),
            () => TrySettlementActionAsync(userId, ctl => ctl.Create(new FinanceContainerSettlement
            {
                CustomerId = customerId, SettlementDate = DateTime.Today,
                LoadingListId = loadingListId, TotalAmount = 500m
            })));

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

    [Fact]
    public async Task Race_LoadingListCancel_Versus_SettlementUpdateLink_SerializedConsistentResult()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, loadingListId, settlementId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E392_RC2_{tag}", "并发客户2");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;
            loadingListId = (await SeedLoadingListAsync(
                seed, $"INT_E392_RGL2_{tag}", $"TCLU-R2-{tag}", customer.Id)).Id;
            settlementId = (await SeedSettlementAsync(
                seed, $"INT_E392_RS2_{tag}", null, customer.Id, 1000m)).Id;
        }
        var postingsBefore = await PostingRowCountAsync();

        // 装柜清单取消与「把未关联结算单显式链接到该清单」并发：更新在锁内复核来源未取消
        var results = await RaceAsync(
            () => TryLoadingCancelAsync(userId, loadingListId),
            () => TrySettlementActionAsync(userId, ctl => ctl.Update(settlementId, new FinanceContainerSettlement
            {
                CustomerId = customerId, SettlementDate = DateTime.Today,
                LoadingListId = loadingListId, TotalAmount = 1000m
            })));

        var cancel = results[0];
        var update = results[1];

        Assert.True(cancel.Success || update.Success, $"cancel={cancel.Error}; update={update.Error}");
        Assert.False(cancel.Success && update.Success,
            "装柜清单取消与结算单链接修改不可能同时成功（共享同一把清单行锁）");

        var listStatus = await ReloadLoadingListStatusAsync(loadingListId);
        var applied = await ReloadSettlementAsync(settlementId);
        if (cancel.Success)
        {
            Assert.Equal(DocumentStatus.Cancelled, listStatus);
            Assert.Null(applied.LoadingListId);   // 取消先完成：链接修改被拒，绝不写入已取消来源
        }
        else
        {
            Assert.NotEqual(DocumentStatus.Cancelled, listStatus);
            Assert.Equal(loadingListId, applied.LoadingListId);
        }

        Assert.Equal(postingsBefore, await PostingRowCountAsync());
    }
}

/// <summary>
/// ERP-392 专用 localdb 夹具：每次运行创建一个全新 GUID 库 + 完整 NEWERP 结构 + 种子数据；
/// 任何库访问之前先过专用目标护栏（实例 <c>NEWERP_AutoAcceptance</c> / 库名前缀 <c>NEWERP_AUTOTEST</c> / 集成安全），
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
/// </summary>
public sealed class ContainerSettlementLoadingSourceSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP392";

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-392] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-392] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>专用目标护栏的 fail-closed 单元覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class ContainerSettlementLoadingSourceTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=x")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            ContainerSettlementLoadingSourceSqlServerFixture.AssertDedicatedTarget(connection));
}
