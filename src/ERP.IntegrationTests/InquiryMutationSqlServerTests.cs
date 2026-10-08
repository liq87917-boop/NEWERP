using ERP.Api.Controllers;
using ERP.Application.Common;
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
/// ERP-402 询价单生命周期、「询价单 → 报价单」转换与批量删除的确定性来源行锁 / 原子事务 / 锁内权威复核
/// 真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实规则 + 真实控制器 + 真实既有授权</b>：以既有「询价单」（<c>inquiry</c>）与「报价单」
/// （<c>quotation</c>）菜单授权 + 既有业务员数据范围驱动真实 <see cref="InquiryController"/>；
/// 不新增任何菜单 / 角色 / 用户授权，无匿名 / 管理员降级。</item>
/// <item><b>两个独立连接竞态</b>（每条用例两个独立 DbContext / 连接 / 事务）：
/// 转报价单 / 转报价单、取消 / 转报价单、编辑 / 审核、混合批量删除 —— 恰好一个合法赢家且失败侧整体回滚，
/// 来源状态与目标单据绝不撕裂。</item>
/// <item><b>拒绝矩阵（无身份 / 缺菜单 / 撤销 / 停用 / 越界 / 混合批量）</b>：一律零写入、零状态变更。</item>
/// <item><b>失败原子回滚</b>：报价单编号生成失败时整体回滚（不落半成品、不改来源状态、不占号、
/// 行锁审计时间戳同回滚）。</item>
/// <item><b>权威一致性</b>：落库报价单的数量 / 单价 / 金额 / 币种 / 汇率 / 单位 / 来源外键与来源询价单完全一致，
/// 且不产生任何财务 / 库存过账。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且 <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取
/// <c>appsettings*.json</c> / <c>.env</c> / 生产凭据。</para>
/// <para>保留既有库存来源单据审计与原始失败日志：本测试只新增自己的证据行，不清理 / 不删除任何既有行
/// （被拒绝的请求只回滚自己的写入）。构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class InquiryMutationSqlServerTests : IClassFixture<InquiryMutationSqlServerFixture>
{
    private readonly InquiryMutationSqlServerFixture _fixture;

    public InquiryMutationSqlServerTests(InquiryMutationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(InquiryMutationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 控制器 / 双连接脚手架 ====================

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>绑定真实 HttpContext 身份（可空 = 无身份）的真实询价单控制器。</summary>
    private static InquiryController NewController(ErpDbContext db, long? userId)
    {
        var http = new DefaultHttpContext();
        if (userId.HasValue)
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"));

        var controller = new InquiryController(db, new DocumentNumberService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    /// <summary>用一条独立连接执行控制器动作（各自 DbContext / 连接 / 事务），返回成功标志与错误。</summary>
    private async Task<(bool Success, string Error)> TryAsync(
        long? userId, Func<InquiryController, Task<IActionResult>> action)
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
        Func<Task<(bool Success, string Error)>> first, Func<Task<(bool Success, string Error)>> second)
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

    // ==================== 种子（既有菜单 / 既有业务员数据范围，不新增权限模型） ====================

    /// <summary>
    /// 播种受限操作员：登录账号 = 员工编码（ERP-097 权威映射），并按需授予既有询价单 / 报价单菜单。
    /// </summary>
    private static async Task<(SysUser User, BaseEmployee Employee, SysRole Role)> SeedOperatorAsync(
        ErpDbContext db, bool inquiryMenu = true, bool quotationMenu = true,
        UserStatus status = UserStatus.Enabled)
    {
        var code = $"INT_E402_{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code, Status = status
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = $"E402R_{code}", RoleCode = $"INT_E402_{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        if (inquiryMenu) await GrantMenuAsync(db, role.Id, InquiryAuthorizationRules.RequiredMenuCode);
        if (quotationMenu) await GrantMenuAsync(db, role.Id, InquiryAuthorizationRules.QuotationMenuCode);
        await db.SaveChangesAsync();
        return (user, employee, role);
    }

    private static async Task GrantMenuAsync(ErpDbContext db, long roleId, string menuCode)
    {
        var menu = await db.SysMenus.FirstAsync(m => !m.IsDeleted && m.MenuCode == menuCode);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
    }

    private static async Task<BaseCustomer> SeedCustomerAsync(ErpDbContext db, string code, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code, CustomerName = "集成客户", Status = 1, CreditStatus = "正常", EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    /// <summary>播种一张询价单（一行明细：数量 10 × 单价 100 = 1000 USD，汇率 7.2，单位 PCS）。</summary>
    private static async Task<Inquiry> SeedInquiryAsync(ErpDbContext db, string no, long customerId,
        DocumentStatus status = DocumentStatus.Approved, bool withDetail = true)
    {
        var inquiry = new Inquiry
        {
            InquiryNo = no, InquiryDate = DateTime.Today, CustomerId = customerId,
            ContactPerson = "集成联系人", ContactPhone = "13800000000",
            Currency = Currency.USD, ExchangeRate = 7.2m, ValidDays = 30, Status = status
        };
        if (withDetail)
        {
            inquiry.Details.Add(new InquiryDetail
            {
                ProductId = 1, ProductName = "集成商品", Spec = "标准", Unit = "PCS",
                Quantity = 10m, UnitPrice = 100m, Amount = 1000m
            });
        }
        db.Inquiries.Add(inquiry);
        await db.SaveChangesAsync();
        return inquiry;
    }

    /// <summary>播种「本人客户 + 已审核询价单」，返回操作员 Id、客户 Id 与询价单 Id。</summary>
    private async Task<(long UserId, long CustomerId, long InquiryId)> SeedOwnedInquiryAsync(
        string tag, DocumentStatus status = DocumentStatus.Approved)
    {
        await using var db = _fixture.CreateDbContext();
        var (user, employee, _) = await SeedOperatorAsync(db);
        var customer = await SeedCustomerAsync(db, $"INT_E402_C_{tag}", employee.Id);
        var inquiry = await SeedInquiryAsync(db, $"INT_E402_INQ_{tag}", customer.Id, status);
        return (user.Id, customer.Id, inquiry.Id);
    }

    // ==================== 1. 两个独立连接竞态 ====================

    [Fact]
    public async Task 转报价单并发_两条独立连接只生成一张完整报价单_另一侧整体回滚()
    {
        Guard();
        var (userId, _, inquiryId) = await SeedOwnedInquiryAsync($"RACE_CVT_{Tag()}");

        var results = await RaceAsync(
            () => TryAsync(userId, c => c.ToQuotation(inquiryId)),
            () => TryAsync(userId, c => c.ToQuotation(inquiryId)));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var quotations = await verify.Quotations.AsNoTracking().Include(o => o.Details)
            .Where(o => o.InquiryId == inquiryId).ToListAsync();
        Assert.Single(quotations);
        Assert.Single(quotations[0].Details);
        Assert.Equal(1000m, quotations[0].TotalAmount);
        Assert.Equal(7200m, quotations[0].TotalAmountCny);
        Assert.Equal(Currency.USD, quotations[0].Currency);
        Assert.Equal(7.2m, quotations[0].ExchangeRate);
        Assert.Equal(10m, quotations[0].Details[0].Quantity);
        Assert.Equal(100m, quotations[0].Details[0].UnitPrice);
        Assert.Equal("PCS", quotations[0].Details[0].Unit);
        Assert.Equal(inquiryId, quotations[0].InquiryId);

        var inquiry = await verify.Inquiries.AsNoTracking().SingleAsync(i => i.Id == inquiryId);
        Assert.Equal(DocumentStatus.Completed, inquiry.Status);
    }

    [Fact]
    public async Task 取消与转报价单并发_恰好一个合法赢家_来源状态与目标绝不撕裂()
    {
        Guard();
        var (userId, _, inquiryId) = await SeedOwnedInquiryAsync($"RACE_CC_{Tag()}");

        var results = await RaceAsync(
            () => TryAsync(userId, c => c.ToQuotation(inquiryId)),
            () => TryAsync(userId, c => c.Cancel(inquiryId)));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var inquiry = await verify.Inquiries.AsNoTracking().SingleAsync(i => i.Id == inquiryId);
        var quotations = await verify.Quotations.AsNoTracking()
            .Where(o => o.InquiryId == inquiryId).ToListAsync();

        if (inquiry.Status == DocumentStatus.Completed)
        {
            Assert.Single(quotations);            // 转换赢：报价单已生成，取消被下游冻结拒绝
        }
        else
        {
            Assert.Equal(DocumentStatus.Cancelled, inquiry.Status);   // 取消赢：零报价单，来源已取消
            Assert.Empty(quotations);
        }
    }

    [Fact]
    public async Task 编辑与审核并发_审核必然生效_编辑只在合法状态下落库_无撕裂()
    {
        Guard();
        var tag = Tag();
        var (userId, customerId, inquiryId) = await SeedOwnedInquiryAsync($"RACE_EA_{tag}",
            DocumentStatus.Submitted);

        var results = await RaceAsync(
            () => TryAsync(userId, c => c.Update(inquiryId, new Inquiry
            {
                CustomerId = customerId,
                Currency = Currency.USD,
                ExchangeRate = 7.2m,
                Details = new List<InquiryDetail>
                {
                    new()
                    {
                        ProductId = 1, ProductName = "集成商品", Unit = "SET",
                        Quantity = 20m, UnitPrice = 5m
                    }
                }
            })),
            () => TryAsync(userId, c => c.Approve(inquiryId)));

        Assert.True(results[1].Success, $"approve={results[1].Error}");

        await using var verify = _fixture.CreateDbContext();
        var inquiry = await verify.Inquiries.AsNoTracking().Include(i => i.Details)
            .SingleAsync(i => i.Id == inquiryId);
        Assert.Equal(DocumentStatus.Approved, inquiry.Status);

        var active = inquiry.Details.Where(d => !d.IsDeleted).ToList();
        if (results[0].Success)
        {
            Assert.Equal(20m, active.Sum(d => d.Quantity));      // 编辑先落库（仅合法状态）：审核在其之上生效
            Assert.Equal("SET", active.Single().Unit);
        }
        else
        {
            Assert.Equal(10m, active.Sum(d => d.Quantity));      // 编辑被拒（状态或并发）：绝不丢更新
            Assert.Equal("PCS", active.Single().Unit);
        }
    }

    [Fact]
    public async Task 混合批量删除并发_恰好一个合法赢家_失败侧整体回滚不部分删除()
    {
        Guard();
        var tag = Tag();
        long userId, a, b, c, d;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            userId = user.Id;
            var customer = await SeedCustomerAsync(seed, $"INT_E402_BD_{tag}", employee.Id);
            a = (await SeedInquiryAsync(seed, $"INT_E402_BDA_{tag}", customer.Id, DocumentStatus.Pending)).Id;
            b = (await SeedInquiryAsync(seed, $"INT_E402_BDB_{tag}", customer.Id, DocumentStatus.Pending)).Id;
            c = (await SeedInquiryAsync(seed, $"INT_E402_BDC_{tag}", customer.Id, DocumentStatus.Pending)).Id;
            d = (await SeedInquiryAsync(seed, $"INT_E402_BDD_{tag}", customer.Id, DocumentStatus.Pending)).Id;
        }

        var results = await RaceAsync(
            () => TryAsync(userId, ctl => ctl.BatchDelete(new List<long> { a, b, c })),
            () => TryAsync(userId, ctl => ctl.BatchDelete(new List<long> { b, c, d })));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var deleted = await verify.Inquiries.AsNoTracking()
            .Where(q => q.Id == a || q.Id == b || q.Id == c || q.Id == d)
            .ToDictionaryAsync(q => q.Id, q => q.IsDeleted);

        Assert.Equal(3, deleted.Values.Count(value => value));   // 恰好一个完整批次生效
        var winner = results[0].Success ? new[] { a, b, c } : new[] { b, c, d };
        var loserOnly = results[0].Success ? d : a;
        foreach (var id in winner) Assert.True(deleted[id]);
        Assert.False(deleted[loserOnly]);                        // 失败侧的独占行绝不被部分删除
    }

    // ==================== 2. 编号生成失败 → 整体回滚 ====================

    [Fact]
    public async Task 编号生成失败_整体回滚_不落半成品报价单_不占号_不改来源状态与审计()
    {
        Guard();
        var tag = Tag();
        long userId, inquiryId;
        DateTime? updatedAtBefore;

        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            userId = user.Id;
            var customer = await SeedCustomerAsync(seed, $"INT_E402_NUM_{tag}", employee.Id);
            var inquiry = await SeedInquiryAsync(seed, $"INT_E402_INQ_NUM_{tag}", customer.Id);
            inquiryId = inquiry.Id;
            updatedAtBefore = inquiry.UpdatedAt;
        }

        try
        {
            // 既有「报价单」单据号规则在**本测试独占的 GUID 库内**被改成非法日期格式：
            // 编号生成在引用事务内真实失败（不伪造异常）；测试结束立即还原（本类用例串行执行）。
            await using (var mutate = _fixture.CreateDbContext())
            {
                var rule = await mutate.SysDocumentNumberRules
                    .FirstAsync(r => r.DocumentType == DocumentType.Quotation && !r.IsDeleted);
                rule.DateFormat = "q";
                await mutate.SaveChangesAsync();
            }

            var result = await TryAsync(userId, c => c.ToQuotation(inquiryId));
            Assert.False(result.Success);

            await using (var verify = _fixture.CreateDbContext())
            {
                Assert.False(await verify.Quotations.AnyAsync(o => o.InquiryId == inquiryId));

                var inquiry = await verify.Inquiries.AsNoTracking().SingleAsync(i => i.Id == inquiryId);
                Assert.Equal(DocumentStatus.Approved, inquiry.Status);
                // 行锁的审计时间戳刷新也在同一事务内回滚：来源一行未改。
                Assert.Equal(updatedAtBefore, inquiry.UpdatedAt);
            }
        }
        finally
        {
            await using var restore = _fixture.CreateDbContext();
            var rule = await restore.SysDocumentNumberRules
                .FirstAsync(r => r.DocumentType == DocumentType.Quotation && !r.IsDeleted);
            rule.DateFormat = "yyyyMMdd";
            await restore.SaveChangesAsync();
        }
    }

    // ==================== 3. 真实既有授权：拒绝矩阵与合法转换口径一致 ====================

    [Fact]
    public async Task 真实既有授权_无身份与缺菜单与撤销与停用与越界与混合批量一律拒绝_双菜单本人才可转换()
    {
        Guard();
        var tag = Tag();
        long bothUserId, inquiryOnlyUserId, revokedUserId, disabledUserId;
        long ownInquiryId, foreignInquiryId, batchPendingId, batchApprovedId;
        int stockBefore, receiptBefore;

        await using (var seed = _fixture.CreateDbContext())
        {
            var (both, bothEmp, _) = await SeedOperatorAsync(seed);
            var (inquiryOnly, _, _) = await SeedOperatorAsync(seed, quotationMenu: false);
            var (revoked, revokedEmp, revokedRole) = await SeedOperatorAsync(seed);
            var (disabled, _, _) = await SeedOperatorAsync(seed, status: UserStatus.Disabled);

            bothUserId = both.Id;
            inquiryOnlyUserId = inquiryOnly.Id;
            revokedUserId = revoked.Id;
            disabledUserId = disabled.Id;

            // 撤销既有「报价单」菜单授权：下一次请求立即收敛（不缓存）。
            var quotationMenuId = await seed.SysMenus
                .Where(m => !m.IsDeleted && m.MenuCode == InquiryAuthorizationRules.QuotationMenuCode)
                .Select(m => m.Id).FirstAsync();
            foreach (var link in await seed.SysRoleMenus
                         .Where(rm => rm.RoleId == revokedRole.Id && rm.MenuId == quotationMenuId).ToListAsync())
                seed.SysRoleMenus.Remove(link);
            await seed.SaveChangesAsync();

            var own = await SeedCustomerAsync(seed, $"INT_E402_OWN_{tag}", bothEmp.Id);
            var foreign = await SeedCustomerAsync(seed, $"INT_E402_FR_{tag}", revokedEmp.Id);
            ownInquiryId = (await SeedInquiryAsync(seed, $"INT_E402_INQ_OWN_{tag}", own.Id)).Id;
            foreignInquiryId = (await SeedInquiryAsync(seed, $"INT_E402_INQ_FR_{tag}", foreign.Id)).Id;
            batchPendingId = (await SeedInquiryAsync(seed, $"INT_E402_INQ_BDP_{tag}", own.Id,
                DocumentStatus.Pending)).Id;
            batchApprovedId = (await SeedInquiryAsync(seed, $"INT_E402_INQ_BDA_{tag}", own.Id,
                DocumentStatus.Approved)).Id;

            stockBefore = await seed.StockOuts.CountAsync();
            receiptBefore = await seed.FinanceReceipts.CountAsync();
        }

        // 无身份 / 只有询价单菜单 / 越界（他人客户）一律 fail closed。
        Assert.False((await TryAsync(null, c => c.ToQuotation(ownInquiryId))).Success);
        Assert.False((await TryAsync(inquiryOnlyUserId, c => c.ToQuotation(ownInquiryId))).Success);
        Assert.False((await TryAsync(inquiryOnlyUserId, c => c.QuotationPrefill(ownInquiryId))).Success);
        Assert.False((await TryAsync(bothUserId, c => c.ToQuotation(foreignInquiryId))).Success);

        // 停用账号（disabled）与撤销「报价单」菜单（revoked）：一律拒绝。
        Assert.False((await TryAsync(disabledUserId, c => c.ToQuotation(ownInquiryId))).Success);
        Assert.False((await TryAsync(revokedUserId, c => c.ToQuotation(ownInquiryId))).Success);

        // 混合批量删除（越界 + 允许 / 状态混合）整体拒绝：不做任何部分删除。
        Assert.False((await TryAsync(bothUserId,
            c => c.BatchDelete(new List<long> { batchPendingId, foreignInquiryId }))).Success);
        Assert.False((await TryAsync(bothUserId,
            c => c.BatchDelete(new List<long> { batchPendingId, batchApprovedId }))).Success);

        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.False(await verify.Quotations.AnyAsync(q => q.InquiryId == ownInquiryId
                                                              || q.InquiryId == foreignInquiryId));
            foreach (var id in new[] { ownInquiryId, foreignInquiryId, batchPendingId, batchApprovedId })
                Assert.False((await verify.Inquiries.AsNoTracking().SingleAsync(i => i.Id == id)).IsDeleted);
        }

        // 双菜单 + 本人客户：放行；落库报价单与来源权威口径逐项一致，且无财务 / 库存过账。
        var allowed = await TryAsync(bothUserId, c => c.ToQuotation(ownInquiryId));
        Assert.True(allowed.Success, allowed.Error);

        await using (var verify = _fixture.CreateDbContext())
        {
            var quotation = await verify.Quotations.AsNoTracking().Include(o => o.Details)
                .SingleAsync(q => q.InquiryId == ownInquiryId);
            var inquiry = await verify.Inquiries.AsNoTracking().Include(i => i.Details)
                .SingleAsync(i => i.Id == ownInquiryId);

            Assert.Equal(DocumentStatus.Completed, inquiry.Status);
            Assert.Equal(inquiry.InquiryNo, quotation.InquiryNo);
            Assert.Equal(inquiry.CustomerId, quotation.CustomerId);
            Assert.Equal(inquiry.Currency, quotation.Currency);          // 币种快照
            Assert.Equal(inquiry.ExchangeRate, quotation.ExchangeRate);  // 汇率快照
            Assert.Equal(inquiry.Details.Where(d => !d.IsDeleted).Sum(d => d.Quantity),
                quotation.Details.Sum(d => d.Quantity));                 // 数量快照
            Assert.Equal(inquiry.Details.Where(d => !d.IsDeleted).Select(d => d.Unit).Distinct().Single(),
                quotation.Details.Select(d => d.Unit).Distinct().Single());   // 单位快照
            Assert.Equal(inquiry.Details.Where(d => !d.IsDeleted).Sum(d => d.Amount),
                quotation.TotalAmount);                                  // 金额快照（Σ 数量 × 单价）

            Assert.Equal(stockBefore, await verify.StockOuts.CountAsync());
            Assert.Equal(receiptBefore, await verify.FinanceReceipts.CountAsync());
        }
    }
}

/// <summary>
/// ERP-402 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class InquiryMutationSqlServerFixture : IAsyncLifetime
{
    /// <summary>专用 localdb 实例标记（与 ERP-398 / ERP-399 / ERP-400 集成测试同源）。</summary>
    public const string InstanceMarker = "NEWERP_AutoAcceptance";

    /// <summary>新库名前缀（GUID 独占；绝不复用既有库）。</summary>
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    /// <summary>默认库名（未提供环境变量时使用；每次运行再拼 GUID 后缀）。</summary>
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_INQMUT_20261009";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-402] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-402] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class InquiryMutationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => InquiryMutationSqlServerFixture.AssertDedicatedTarget(connection));
}
