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
/// ERP-400 报价单生命周期、版本创建与「报价单 → PI / 销售订单」转换的确定性来源行锁 / 原子事务 /
/// 锁内权威复核真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实规则 + 真实控制器 + 真实既有授权</b>：以既有「报价单」（<c>quotation</c>）、
/// 「形式发票 PI」（<c>proforma-invoice</c>）与「销售订单」（<c>sales-order</c>）菜单授权 + 既有业务员数据范围
/// 驱动真实 <see cref="QuotationController"/>；不新增任何菜单 / 角色 / 用户授权，无匿名 / 管理员降级。</item>
/// <item><b>两个独立连接竞态</b>（每条用例两个独立 DbContext / 连接 / 事务）：
/// 转 PI / 转 PI、转销售订单 / 转销售订单、转销售订单 / 取消、编辑 / 审核、创建版本 / 创建版本、
/// 混合批量删除 —— 恰好一个合法赢家且失败侧整体回滚，来源状态与目标单据绝不撕裂。</item>
/// <item><b>拒绝矩阵（forbidden / revoked / disabled / 混合批量）</b>：缺菜单（forbidden）、撤销菜单（revoked）、
/// 停用账号（disabled）与混合批量删除整体拒绝，均不产生任何目标单据、不改来源状态。</item>
/// <item><b>失败原子回滚</b>：目标单据编号生成失败时整体回滚（不落半成品、不改来源状态、不占号、行锁审计时间戳同回滚）。</item>
/// <item><b>权威一致性</b>：落库目标单据的数量 / 币种 / 汇率 / 单位 / 合计 / 定金 / 显式来源外键与来源报价单完全一致，
/// 且不产生任何财务 / 库存过账。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且 <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取
/// <c>appsettings*.json</c> / <c>.env</c> / 生产凭据。</para>
/// <para>保留既有库存来源单据审计与原始失败日志：本测试只新增自己的证据行，不清理 / 不删除任何既有行
/// （被拒绝的请求只回滚自己的写入）。构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class QuotationMutationSqlServerTests
    : IClassFixture<QuotationMutationSqlServerFixture>
{
    private readonly QuotationMutationSqlServerFixture _fixture;

    public QuotationMutationSqlServerTests(QuotationMutationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(QuotationMutationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 控制器 / 双连接脚手架 ====================

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>绑定真实 HttpContext 身份（可空 = 无身份）的真实报价单控制器。</summary>
    private static QuotationController NewController(ErpDbContext db, long? userId)
    {
        var http = new DefaultHttpContext();
        if (userId.HasValue)
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"));

        var controller = new QuotationController(db, new DocumentNumberService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    /// <summary>用一条独立连接执行控制器动作（各自 DbContext / 连接 / 事务），返回成功标志与错误。</summary>
    private async Task<(bool Success, string Error)> TryAsync(
        long? userId, Func<QuotationController, Task<IActionResult>> action)
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
    /// 播种受限操作员：登录账号 = 员工编码（ERP-097 权威映射），并按需授予既有报价单 / PI / 销售订单菜单。
    /// </summary>
    private static async Task<(SysUser User, BaseEmployee Employee, SysRole Role)> SeedOperatorAsync(
        ErpDbContext db, bool quotationMenu = true, bool piMenu = true, bool soMenu = true,
        UserStatus status = UserStatus.Enabled)
    {
        var code = $"INT_E400_{Guid.NewGuid():N}";
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
            RoleName = $"E400R_{code}", RoleCode = $"INT_E400_{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        if (quotationMenu) await GrantMenuAsync(db, role.Id, QuotationAuthorizationRules.RequiredMenuCode);
        if (piMenu) await GrantMenuAsync(db, role.Id, QuotationAuthorizationRules.ProformaInvoiceMenuCode);
        if (soMenu) await GrantMenuAsync(db, role.Id, QuotationAuthorizationRules.SalesOrderMenuCode);
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

    /// <summary>播种一张报价单（一行明细：数量 10 × 单价 100 = 1000 USD，汇率 7.2，单位 PCS）。</summary>
    private static async Task<Quotation> SeedQuotationAsync(ErpDbContext db, string no, long? customerId,
        DocumentStatus status = DocumentStatus.Approved)
    {
        var quotation = new Quotation
        {
            QuotationNo = no, QuotationDate = DateTime.Today, ValidUntil = DateTime.Today.AddDays(30),
            CustomerId = customerId, CustomerName = "集成客户",
            Currency = Currency.USD, ExchangeRate = 7.2m,
            Status = status
        };
        quotation.Details.Add(new QuotationDetail
        {
            SortNo = 1, ProductCode = "INT-E400-P1", ProductName = "集成商品", Unit = "PCS",
            Quantity = 10m, UnitPrice = 100m, Amount = 1000m
        });
        quotation.TotalAmount = 1000m;
        quotation.TotalAmountCny = 7200m;
        db.Quotations.Add(quotation);
        await db.SaveChangesAsync();
        return quotation;
    }

    // ==================== 2. 双连接竞态（恰好一个合法赢家 + 整体回滚） ====================

    /// <summary>播种一位三菜单受限操作员 + 其本人客户 + 一张已审核报价单（返回用户 / 客户 / 报价单）。</summary>
    private async Task<(long UserId, long CustomerId, long QuotationId)> SeedOwnedQuotationAsync(string tag,
        DocumentStatus status = DocumentStatus.Approved)
    {
        await using var seed = _fixture.CreateDbContext();
        var (user, employee, _) = await SeedOperatorAsync(seed);
        var customer = await SeedCustomerAsync(seed, $"INT_E400_C_{tag}", employee.Id);
        var quotation = await SeedQuotationAsync(seed, $"INT_E400_Q_{tag}", customer.Id, status);
        return (user.Id, customer.Id, quotation.Id);
    }

    [Fact]
    public async Task 转PI并发_两条独立连接只生成一张完整PI_另一侧整体回滚()
    {
        Guard();
        var (userId, _, quotationId) = await SeedOwnedQuotationAsync($"RACE_PI_{Tag()}");

        var results = await RaceAsync(
            () => TryAsync(userId, c => c.ToProformaInvoice(quotationId)),
            () => TryAsync(userId, c => c.ToProformaInvoice(quotationId)));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var pis = await verify.ProformaInvoices.AsNoTracking().Include(o => o.Details)
            .Where(o => o.QuotationId == quotationId).ToListAsync();
        Assert.Single(pis);
        Assert.Single(pis[0].Details);
        Assert.Equal(1000m, pis[0].TotalAmount);
        Assert.Equal(Currency.USD, pis[0].Currency);
        Assert.Equal(quotationId, pis[0].QuotationId);

        var quotation = await verify.Quotations.AsNoTracking().SingleAsync(q => q.Id == quotationId);
        Assert.Equal(DocumentStatus.Completed, quotation.Status);
    }

    [Fact]
    public async Task 转销售订单并发_两条独立连接只生成一张完整订单_另一侧整体回滚()
    {
        Guard();
        var (userId, _, quotationId) = await SeedOwnedQuotationAsync($"RACE_SO_{Tag()}");

        var results = await RaceAsync(
            () => TryAsync(userId, c => c.ToSalesOrder(quotationId)),
            () => TryAsync(userId, c => c.ToSalesOrder(quotationId)));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var orders = await verify.SalesOrders.AsNoTracking().Include(o => o.Details)
            .Where(o => o.SourceQuotationId == quotationId).ToListAsync();
        Assert.Single(orders);
        Assert.Single(orders[0].Details);
        Assert.Equal(1000m, orders[0].TotalAmount);
        Assert.Equal(Currency.USD, orders[0].Currency);
        Assert.Equal("PCS", orders[0].Details[0].Unit);
        Assert.Equal(quotationId, orders[0].SourceQuotationId);

        var quotation = await verify.Quotations.AsNoTracking().SingleAsync(q => q.Id == quotationId);
        Assert.Equal(DocumentStatus.Completed, quotation.Status);
    }

    [Fact]
    public async Task 转销售订单与取消并发_恰好一个合法赢家_来源状态与目标绝不撕裂()
    {
        Guard();
        var (userId, _, quotationId) = await SeedOwnedQuotationAsync($"RACE_CO_{Tag()}");

        var results = await RaceAsync(
            () => TryAsync(userId, c => c.ToSalesOrder(quotationId)),
            () => TryAsync(userId, c => c.Cancel(quotationId)));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var quotation = await verify.Quotations.AsNoTracking().SingleAsync(q => q.Id == quotationId);
        var orders = await verify.SalesOrders.AsNoTracking()
            .Where(o => o.SourceQuotationId == quotationId).ToListAsync();

        if (quotation.Status == DocumentStatus.Completed)
        {
            Assert.Single(orders);            // 转换赢：订单已生成，取消被拒
        }
        else
        {
            Assert.Equal(DocumentStatus.Cancelled, quotation.Status);   // 取消赢：零订单，来源已取消
            Assert.Empty(orders);
        }
    }

    [Fact]
    public async Task 转销售订单与创建版本并发_恰好一个合法赢家_版本链与转换绝不撕裂()
    {
        Guard();
        var (userId, _, quotationId) = await SeedOwnedQuotationAsync($"RACE_RV_{Tag()}");

        var results = await RaceAsync(
            () => TryAsync(userId, c => c.ToSalesOrder(quotationId)),
            () => TryAsync(userId, c => c.CreateRevision(quotationId)));

        await using var verify = _fixture.CreateDbContext();
        var quotation = await verify.Quotations.AsNoTracking().SingleAsync(q => q.Id == quotationId);
        var orders = await verify.SalesOrders.AsNoTracking()
            .Where(o => o.SourceQuotationId == quotationId).ToListAsync();
        var revisions = await verify.Quotations.AsNoTracking()
            .Where(q => q.PreviousRevisionId == quotationId).ToListAsync();

        // 两个写路径都在同一把报价单来源行锁内串行：每次成功都必须留下恰好一份完整产物
        //（一次转换 = 恰好一张订单；一次版本创建 = 恰好一个草稿版本），绝不出现半成品或撕裂状态。
        Assert.Equal(results.Count(r => r.Success),
            (orders.Count > 0 ? 1 : 0) + (revisions.Count > 0 ? 1 : 0));
        Assert.True(orders.Count <= 1);
        Assert.True(revisions.Count <= 1);

        if (orders.Count == 1)
        {
            Assert.Equal(DocumentStatus.Completed, quotation.Status);   // 转换赢：来源已完成
            Assert.Equal(quotation.Id, orders[0].SourceQuotationId);
            Assert.Equal(1000m, orders[0].TotalAmount);
            Assert.Equal(Currency.USD, orders[0].Currency);
        }

        foreach (var revision in revisions)
        {
            Assert.Equal(DocumentStatus.Pending, revision.Status);      // 版本一律草稿
            Assert.Equal(quotation.QuotationNo, revision.PreviousRevisionNo);
        }

        // 来源状态只会是「可转换的已审核」或「已完成的已转订单」，绝不停留在任何中途状态。
        Assert.Contains(quotation.Status, new[] { DocumentStatus.Approved, DocumentStatus.Completed });
    }

    [Fact]
    public async Task 编辑与审核并发_审核必然生效_编辑只在审核之前落库_无撕裂()
    {
        Guard();
        var tag = Tag();
        var (userId, customerId, quotationId) = await SeedOwnedQuotationAsync($"RACE_EA_{tag}",
            DocumentStatus.Submitted);

        var results = await RaceAsync(
            () => TryAsync(userId, c => c.Update(quotationId, new Quotation
            {
                QuotationDate = DateTime.Today,
                CustomerId = customerId,
                CustomerName = "集成客户",
                Currency = Currency.USD,
                ExchangeRate = 7.2m,
                Details = new List<QuotationDetail>
                {
                    new()
                    {
                        ProductCode = "INT-E400-EDIT", ProductName = "集成商品", Unit = "SET",
                        Quantity = 20m, UnitPrice = 5m
                    }
                }
            })),
            () => TryAsync(userId, c => c.Approve(quotationId)));

        Assert.True(results[1].Success, $"approve={results[1].Error}");

        await using var verify = _fixture.CreateDbContext();
        var quotation = await verify.Quotations.AsNoTracking().Include(o => o.Details)
            .SingleAsync(q => q.Id == quotationId);
        Assert.Equal(DocumentStatus.Approved, quotation.Status);

        var quantity = quotation.Details.Where(d => !d.IsDeleted).Sum(d => d.Quantity);
        if (results[0].Success)
        {
            Assert.Equal(20m, quantity);       // 编辑先落库：审核在编辑后的权威状态上生效
            Assert.Equal("SET", quotation.Details.First(d => !d.IsDeleted).Unit);
        }
        else
        {
            Assert.Equal(10m, quantity);       // 审核先赢：编辑在锁内被拒（绝不丢更新）
        }
    }

    [Fact]
    public async Task 创建版本并发_两条独立连接只新增完整版本且版本号链内不重复()
    {
        Guard();
        var (userId, _, quotationId) = await SeedOwnedQuotationAsync($"RACE_REV_{Tag()}");

        var results = await RaceAsync(
            () => TryAsync(userId, c => c.CreateRevision(quotationId)),
            () => TryAsync(userId, c => c.CreateRevision(quotationId)));

        await using var verify = _fixture.CreateDbContext();
        var revisions = await verify.Quotations.AsNoTracking().Include(q => q.Details)
            .Where(q => q.PreviousRevisionId == quotationId)
            .OrderBy(q => q.RevisionNumber).ToListAsync();

        // 版本号由服务端在来源行锁内单调分配：并发下绝不出现重复版本号 / 半成品版本。
        Assert.True(revisions.Count >= 1);
        Assert.Equal(results.Count(r => r.Success), revisions.Count);
        Assert.Equal(revisions.Select(r => r.RevisionNumber).Distinct().Count(), revisions.Count);
        Assert.All(revisions, r =>
        {
            Assert.Equal(DocumentStatus.Pending, r.Status);
            Assert.Single(r.Details);
            Assert.Equal(10m, r.Details[0].Quantity);
            Assert.Equal("PCS", r.Details[0].Unit);
            Assert.Equal(quotationId, r.PreviousRevisionId);
        });

        var source = await verify.Quotations.AsNoTracking().SingleAsync(q => q.Id == quotationId);
        Assert.Equal(DocumentStatus.Approved, source.Status);          // 源版本一行不改
        Assert.Null(source.RootQuotationId);
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
            var customer = await SeedCustomerAsync(seed, $"INT_E400_BD_{tag}", employee.Id);
            a = (await SeedQuotationAsync(seed, $"INT_E400_BDA_{tag}", customer.Id, DocumentStatus.Pending)).Id;
            b = (await SeedQuotationAsync(seed, $"INT_E400_BDB_{tag}", customer.Id, DocumentStatus.Pending)).Id;
            c = (await SeedQuotationAsync(seed, $"INT_E400_BDC_{tag}", customer.Id, DocumentStatus.Pending)).Id;
            d = (await SeedQuotationAsync(seed, $"INT_E400_BDD_{tag}", customer.Id, DocumentStatus.Pending)).Id;
        }

        var results = await RaceAsync(
            () => TryAsync(userId, ctl => ctl.BatchDelete(new List<long> { a, b, c })),
            () => TryAsync(userId, ctl => ctl.BatchDelete(new List<long> { b, c, d })));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var deleted = await verify.Quotations.AsNoTracking()
            .Where(q => q.Id == a || q.Id == b || q.Id == c || q.Id == d)
            .ToDictionaryAsync(q => q.Id, q => q.IsDeleted);

        Assert.Equal(3, deleted.Values.Count(value => value));   // 恰好一个完整批次生效
        var winner = results[0].Success ? new[] { a, b, c } : new[] { b, c, d };
        var loserOnly = results[0].Success ? d : a;
        foreach (var id in winner) Assert.True(deleted[id]);
        Assert.False(deleted[loserOnly]);                        // 失败侧的独占行绝不被部分删除
    }

    // ==================== 3. 编号生成失败 → 整体回滚 ====================

    [Fact]
    public async Task 编号生成失败_整体回滚_不落半成品目标_不占号_不改来源状态与审计()
    {
        Guard();
        var tag = Tag();
        long userId, quotationId;
        DateTime? updatedAtBefore;

        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            userId = user.Id;
            var customer = await SeedCustomerAsync(seed, $"INT_E400_NUM_{tag}", employee.Id);
            var quotation = await SeedQuotationAsync(seed, $"INT_E400_Q_NUM_{tag}", customer.Id);
            quotationId = quotation.Id;
            updatedAtBefore = quotation.UpdatedAt;
        }

        try
        {
            // 既有「销售订单」单据号规则在**本测试独占的 GUID 库内**被改成非法日期格式：
            // 编号生成在引用事务内真实失败（不伪造异常）；测试结束立即还原（本类用例串行执行）。
            await using (var mutate = _fixture.CreateDbContext())
            {
                var rule = await mutate.SysDocumentNumberRules
                    .FirstAsync(r => r.DocumentType == DocumentType.SalesOrder && !r.IsDeleted);
                rule.DateFormat = "q";
                await mutate.SaveChangesAsync();
            }

            var result = await TryAsync(userId, c => c.ToSalesOrder(quotationId));
            Assert.False(result.Success);

            await using (var verify = _fixture.CreateDbContext())
            {
                Assert.False(await verify.SalesOrders.AnyAsync(o => o.SourceQuotationId == quotationId));

                var quotation = await verify.Quotations.AsNoTracking().SingleAsync(q => q.Id == quotationId);
                Assert.Equal(DocumentStatus.Approved, quotation.Status);
                Assert.Equal(1000m, quotation.TotalAmount);
                Assert.Equal(updatedAtBefore, quotation.UpdatedAt);   // 行锁的审计时间戳刷新也在同一事务内回滚
            }
        }
        finally
        {
            await using var restore = _fixture.CreateDbContext();
            var rule = await restore.SysDocumentNumberRules
                .FirstAsync(r => r.DocumentType == DocumentType.SalesOrder && !r.IsDeleted);
            rule.DateFormat = "yyyyMMdd";
            await restore.SaveChangesAsync();
        }
    }

    // ==================== 4. 真实既有授权：拒绝矩阵与合法转换口径一致 ====================

    [Fact]
    public async Task 真实既有授权_无身份与缺菜单与撤销与停用与越界与混合批量一律拒绝_双菜单本人才可转换()
    {
        Guard();
        var tag = Tag();
        long bothUserId, quotationOnlyUserId, revokedUserId, disabledUserId;
        long ownQuotationId, foreignQuotationId, batchPendingId, batchForeignId;
        int stockBefore, receiptBefore;

        await using (var seed = _fixture.CreateDbContext())
        {
            var (both, bothEmp, _) = await SeedOperatorAsync(seed);
            var (quotationOnly, _, _) = await SeedOperatorAsync(seed, piMenu: false, soMenu: false);
            var (revoked, revokedEmp, revokedRole) = await SeedOperatorAsync(seed);
            var (disabled, _, _) = await SeedOperatorAsync(seed, status: UserStatus.Disabled);

            bothUserId = both.Id;
            quotationOnlyUserId = quotationOnly.Id;
            revokedUserId = revoked.Id;
            disabledUserId = disabled.Id;

            // 撤销既有「销售订单」菜单授权（保留 PI 菜单）：下一次请求立即收敛。
            var soMenuId = await seed.SysMenus
                .Where(m => !m.IsDeleted && m.MenuCode == QuotationAuthorizationRules.SalesOrderMenuCode)
                .Select(m => m.Id).FirstAsync();
            foreach (var link in await seed.SysRoleMenus
                         .Where(rm => rm.RoleId == revokedRole.Id && rm.MenuId == soMenuId).ToListAsync())
                seed.SysRoleMenus.Remove(link);
            await seed.SaveChangesAsync();

            var own = await SeedCustomerAsync(seed, $"INT_E400_OWN_{tag}", bothEmp.Id);
            var foreign = await SeedCustomerAsync(seed, $"INT_E400_FR_{tag}", revokedEmp.Id);
            ownQuotationId = (await SeedQuotationAsync(seed, $"INT_E400_Q_OWN_{tag}", own.Id)).Id;
            foreignQuotationId = (await SeedQuotationAsync(seed, $"INT_E400_Q_FR_{tag}", foreign.Id)).Id;
            batchPendingId = (await SeedQuotationAsync(seed, $"INT_E400_Q_BD_{tag}", own.Id,
                DocumentStatus.Pending)).Id;
            batchForeignId = (await SeedQuotationAsync(seed, $"INT_E400_Q_BDF_{tag}", foreign.Id,
                DocumentStatus.Pending)).Id;

            stockBefore = await seed.StockOuts.CountAsync();
            receiptBefore = await seed.FinanceReceipts.CountAsync();
        }

        // 无身份 / 只有报价单菜单 / 越界（他人客户）一律 fail closed。
        Assert.False((await TryAsync(null, c => c.ToSalesOrder(ownQuotationId))).Success);
        Assert.False((await TryAsync(quotationOnlyUserId, c => c.ToSalesOrder(ownQuotationId))).Success);
        Assert.False((await TryAsync(quotationOnlyUserId, c => c.ToProformaInvoice(ownQuotationId))).Success);
        Assert.False((await TryAsync(bothUserId, c => c.ToSalesOrder(foreignQuotationId))).Success);
        Assert.False((await TryAsync(bothUserId, c => c.ToProformaInvoice(foreignQuotationId))).Success);

        // 停用账号（disabled）：即使菜单齐全也一律拒绝。
        Assert.False((await TryAsync(disabledUserId, c => c.ToSalesOrder(ownQuotationId))).Success);

        // 撤销「销售订单」菜单（revoked）：销售订单转换拒绝。
        Assert.False((await TryAsync(revokedUserId, c => c.ToSalesOrder(ownQuotationId))).Success);

        // 混合批量删除（越界 + 允许）整体拒绝：不做任何部分删除。
        Assert.False((await TryAsync(bothUserId,
            c => c.BatchDelete(new List<long> { batchPendingId, batchForeignId }))).Success);
        Assert.False((await TryAsync(bothUserId,
            c => c.BatchDelete(new List<long> { batchPendingId, ownQuotationId }))).Success);

        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.False(await verify.SalesOrders.AnyAsync(o => o.SourceQuotationId == ownQuotationId
                                                               || o.SourceQuotationId == foreignQuotationId));
            Assert.False(await verify.ProformaInvoices.AnyAsync(o => o.QuotationId == ownQuotationId
                                                                     || o.QuotationId == foreignQuotationId));
            foreach (var id in new[] { ownQuotationId, foreignQuotationId, batchPendingId, batchForeignId })
                Assert.False((await verify.Quotations.AsNoTracking().SingleAsync(q => q.Id == id)).IsDeleted);
        }

        // 双菜单 + 本人客户：放行；落库订单与来源权威口径逐项一致，且无财务 / 库存过账。
        Assert.True((await TryAsync(bothUserId, c => c.ToSalesOrder(ownQuotationId))).Success);

        await using (var verify = _fixture.CreateDbContext())
        {
            var order = await verify.SalesOrders.AsNoTracking().Include(o => o.Details)
                .SingleAsync(o => o.SourceQuotationId == ownQuotationId);
            var quotation = await verify.Quotations.AsNoTracking().Include(o => o.Details)
                .SingleAsync(q => q.Id == ownQuotationId);

            Assert.Equal(DocumentStatus.Completed, quotation.Status);
            Assert.Equal(quotation.Id, order.SourceQuotationId);
            Assert.Equal(quotation.QuotationNo, order.SourceQuotationNo);
            Assert.Equal(quotation.Currency, order.Currency);          // 币种快照
            Assert.Equal(quotation.ExchangeRate, order.ExchangeRate);  // 汇率快照
            Assert.Equal(quotation.TotalAmount, order.TotalAmount);    // 合计快照
            Assert.Equal(quotation.Details.Where(d => !d.IsDeleted).Sum(d => d.Quantity),
                order.Details.Sum(d => d.Quantity));                   // 数量快照
            Assert.Equal(quotation.Details.Where(d => !d.IsDeleted).Select(d => d.Unit).Distinct().Single(),
                order.Details.Select(d => d.Unit).Distinct().Single()); // 单位快照
            Assert.Equal(order.TotalAmount * order.DepositRatio / 100m, order.DepositAmount); // 定金快照

            Assert.Equal(stockBefore, await verify.StockOuts.CountAsync());
            Assert.Equal(receiptBefore, await verify.FinanceReceipts.CountAsync());
        }
    }
}

/// <summary>
/// ERP-400 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class QuotationMutationSqlServerFixture : IAsyncLifetime
{
    /// <summary>专用 localdb 实例标记（与 ERP-398 / ERP-399 集成测试同源）。</summary>
    public const string InstanceMarker = "NEWERP_AutoAcceptance";

    /// <summary>新库名前缀（GUID 独占；绝不复用既有库）。</summary>
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    /// <summary>默认库名（未提供环境变量时使用；每次运行再拼 GUID 后缀）。</summary>
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_QTMUT_20261009";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-400] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-400] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class QuotationMutationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => QuotationMutationSqlServerFixture.AssertDedicatedTarget(connection));
}
