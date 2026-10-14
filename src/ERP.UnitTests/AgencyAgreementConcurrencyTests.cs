using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-519 代理服务费协议证据「草稿修改 / 登记冻结 / 作废」并发序列化单元测试（内存库 + 真实 HTTP 身份）：
/// <list type="number">
/// <item><b>护栏契约</b>：<see cref="AgencyFeeEvidenceConcurrencyRules"/> 的锁序 / 边界 / 受控错误文案与等价
/// T-SQL 行锁同源；非关系型提供程序判定为「无事务无行锁」，事务帮助方法等价无操作；
/// 唯一索引冲突识别（2601 / 2627 / duplicate key / UNIQUE）与跟踪器清理口径；</item>
/// <item><b>生命周期串行化</b>：已登记 / 已作废证据绝不接收迟到的草稿修改；重复登记 / 重复作废只有唯一赢家，
/// 输家绝不覆盖赢家已写入的登记人 / 时间戳 / 原始作废原因；被拒请求逐字段（含状态与全部审计）不变；</item>
/// <item><b>权威状态与客户范围</b>：锁内重读的权威行才是资格与范围的唯一依据，越范围（含已被并发归属变化影响
/// 的范围）一律受控拒绝且零字段变更；</item>
/// <item><b>源码契约</b>：新增 / 修改 / 登记 / 作废四条写路由都在同一事务 + 同一协议行锁内，控制器每条路由
/// 仍先解析实时身份 / 菜单 / 客户范围。</item>
/// </list>
/// 全部使用内存库（<see cref="TestDbFactory"/>）+ SQL Server 提供程序**仅构建模型**（不打开连接）；
/// 不连接 SQL Server、不执行 DDL / 部署脚本，也不做浏览器 / UI 验收。
/// </summary>
public class AgencyAgreementConcurrencyTests
{
    // ==================== 1. 护栏契约（纯规则） ====================

    [Fact]
    public void 并发护栏常量与锁序边界文案同源()
    {
        Assert.Contains("ERP-519", AgencyFeeEvidenceConcurrencyRules.GlobalLockOrderText);
        Assert.Contains("AgencyServiceFeeAgreements", AgencyFeeEvidenceConcurrencyRules.GlobalLockOrderText);
        Assert.Contains("绝不反向获取", AgencyFeeEvidenceConcurrencyRules.GlobalLockOrderText);

        Assert.Contains("UPDLOCK, HOLDLOCK", AgencyFeeEvidenceConcurrencyRules.AgreementRowLockSql);
        Assert.Contains("AgencyServiceFeeAgreements", AgencyFeeEvidenceConcurrencyRules.AgreementRowLockSql);

        // 边界：不新增权限 / 表列 / 索引，不改写既有业务记录，也不把空身份当作管理员
        Assert.Contains("不新增菜单 / 角色 / 用户授权", AgencyFeeEvidenceConcurrencyRules.BoundaryText);
        Assert.Contains("客户主数据", AgencyFeeEvidenceConcurrencyRules.BoundaryText);
        Assert.Contains("UpdatedAt", AgencyFeeEvidenceConcurrencyRules.BoundaryText);

        // 受控错误文案不得含数据库内部细节
        Assert.DoesNotContain("SqlException", AgencyFeeEvidenceConcurrencyRules.ActiveIdentityConflictText);
        Assert.DoesNotContain("UNIQUE", AgencyFeeEvidenceConcurrencyRules.ActiveIdentityConflictText);
        Assert.DoesNotContain("UX_", AgencyFeeEvidenceConcurrencyRules.ActiveIdentityConflictText);
        Assert.DoesNotContain("SqlException", AgencyFeeEvidenceConcurrencyRules.ConcurrentModificationText);
    }

    [Fact]
    public void 关系型判定_内存库为非关系型_SQLServer提供程序为关系型()
    {
        using var inMemory = TestDbFactory.Create();
        Assert.False(AgencyFeeEvidenceConcurrencyRules.IsRelationalProvider(inMemory));

        // 仅构建模型（绝不打开连接）：与集成测试的真实 SQL Server 口径同源。
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;"
                + "Database=NEWERP_AUTOTEST_ERP519_MODEL_ONLY;Integrated Security=true;TrustServerCertificate=true")
            .Options;
        using var relational = new ErpDbContext(options);
        Assert.True(AgencyFeeEvidenceConcurrencyRules.IsRelationalProvider(relational));
    }

    [Fact]
    public async Task 事务帮助_非关系型等价无操作_且拒绝返回已确认回滚()
    {
        using var db = TestDbFactory.Create();

        Assert.Null(await AgencyFeeEvidenceConcurrencyRules.BeginMutationTransactionAsync(db));
        await AgencyFeeEvidenceConcurrencyRules.CommitAsync(null);              // 空操作，绝不抛错

        db.AgencyServiceFeeAgreements.Add(new AgencyServiceFeeAgreement
        {
            AgreementNo = "ASF-519-TX", NormalizedAgreementNo = "ASF519TX", CustomerId = 1
        });
        Assert.NotEmpty(db.ChangeTracker.Entries());

        // 非关系型没有真实事务与行锁：拒绝路径不清空跟踪器（保持既有内存语义），但事务语义等价「已回滚」。
        Assert.True(await AgencyFeeEvidenceConcurrencyRules.TryRollbackAsync(null, db));

        // 显式清理口径仍复用既有业务护栏语义（清空跟踪器）。
        AgencyFeeEvidenceConcurrencyRules.DiscardTrackedChanges(db);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public void 唯一索引冲突识别_按错误码与文案_绝不误报非唯一冲突()
    {
        Assert.True(AgencyFeeEvidenceConcurrencyRules.LooksLikeUniqueViolation(
            new DbUpdateException("Cannot insert duplicate key row (2601)")));
        Assert.True(AgencyFeeEvidenceConcurrencyRules.LooksLikeUniqueViolation(
            new DbUpdateException("Violation of UNIQUE KEY constraint (2627)")));
        Assert.True(AgencyFeeEvidenceConcurrencyRules.LooksLikeUniqueViolation(
            new DbUpdateException("outer", new InvalidOperationException("duplicate key"))));

        Assert.False(AgencyFeeEvidenceConcurrencyRules.LooksLikeUniqueViolation(
            new DbUpdateException("Invalid object name 'db_owner.AgencyServiceFeeAgreements'")));
        Assert.Throws<ArgumentNullException>(
            () => AgencyFeeEvidenceConcurrencyRules.LooksLikeUniqueViolation(null!));
    }

    // ==================== 2. 生命周期串行化（内存库 + 真实 HTTP 身份） ====================

    [Fact]
    public async Task 已登记的协议_拒绝迟到的草稿修改_且逐字段不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-519-A", "并发客户A");
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Create(SaveDto(customer.Id, agreementNo: "ASF-519-A", ratePercent: 1.5m)));
        var recorded = AssertOk<AgencyServiceFeeAgreementDto>(await controller.Record(created.Id));
        Assert.Equal(AgencyServiceFeeAgreementRules.StatusRecorded, recorded.Status);

        var before = Reload(db, created.Id);
        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Update(
            created.Id, SaveDto(customer.Id, agreementNo: "ASF-519-A", ratePercent: 9.9m)));

        Assert.Contains("已登记", ex.Message);
        AssertUnchanged(before, Reload(db, created.Id));
        Assert.Equal(1.5m, Reload(db, created.Id).RatePercent);
    }

    [Fact]
    public async Task 已作废的协议_拒绝迟到的草稿修改与重复登记_原始作废留痕保留()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-519-B", "并发客户B");
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Create(SaveDto(customer.Id, agreementNo: "ASF-519-B")));
        var voided = AssertOk<AgencyServiceFeeAgreementDto>(await controller.Void(
            created.Id, new AgencyServiceFeeAgreementVoidRequest { Reason = "协议终止" }));
        Assert.Equal(AgencyServiceFeeAgreementRules.StatusVoided, voided.Status);

        var before = Reload(db, created.Id);
        await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Update(created.Id, SaveDto(customer.Id, agreementNo: "ASF-519-B", ratePercent: 8m)));
        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Record(created.Id));
        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Void(
            created.Id, new AgencyServiceFeeAgreementVoidRequest { Reason = "再次作废" }));

        var after = Reload(db, created.Id);
        AssertUnchanged(before, after);
        Assert.Equal("协议终止", after.VoidReason);        // 绝不覆盖赢家已保留的原始作废原因
        Assert.Equal(before.VoidedAt, after.VoidedAt);
        Assert.Equal(string.Empty, after.RecordedBy);      // 草稿作废：没有登记人，也绝不被迟到的登记补齐
        Assert.Null(after.RecordedAt);
    }


    [Fact]
    public async Task 重复登记_只有唯一赢家_赢家登记人与时间戳绝不被覆盖()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-519-C", "并发客户C");
        var first = BuildController(db, displayName: "第一位登记人");
        var second = BuildController(db, displayName: "第二位登记人");

        var created = AssertOk<AgencyServiceFeeAgreementDto>(
            await first.Create(SaveDto(customer.Id, agreementNo: "ASF-519-C")));

        var winner = AssertOk<AgencyServiceFeeAgreementDto>(await first.Record(created.Id));
        Assert.Equal("第一位登记人", winner.RecordedBy);

        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => second.Record(created.Id));

        var stored = Reload(db, created.Id);
        Assert.Equal(AgencyServiceFeeAgreementRules.StatusRecorded, stored.Status);
        Assert.Equal("第一位登记人", stored.RecordedBy);    // 输家绝不覆盖赢家审计
        Assert.Equal(winner.RecordedAt, stored.RecordedAt);
        Assert.Equal(winner.UpdatedAt, stored.UpdatedAt);
    }

    [Fact]
    public async Task 草稿修改后登记_冻结的是修改后的条款而不是初始值()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-519-D", "并发客户D");
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeAgreementDto>(await controller.Create(
            SaveDto(customer.Id, agreementNo: "ASF-519-D", ratePercent: 1.5m, feeBasis: "初始口径")));
        var updated = AssertOk<AgencyServiceFeeAgreementDto>(await controller.Update(
            created.Id, SaveDto(customer.Id, agreementNo: "ASF-519-D", ratePercent: 2.5m, feeBasis: "修订口径")));
        Assert.Equal(2.5m, updated.RatePercent);

        var recorded = AssertOk<AgencyServiceFeeAgreementDto>(await controller.Record(created.Id));
        Assert.Equal(AgencyServiceFeeAgreementRules.StatusRecorded, recorded.Status);
        Assert.Equal(2.5m, recorded.RatePercent);
        Assert.Equal("修订口径", recorded.FeeBasis);
        Assert.Equal(customer.Id, recorded.CustomerId);

        var stored = Reload(db, created.Id);
        Assert.Equal(2.5m, stored.RatePercent);
        Assert.Equal("修订口径", stored.FeeBasis);
        Assert.Equal(customer.CustomerCode, stored.CustomerCode);   // 客户快照仍是服务端权威写入
    }

    [Fact]
    public async Task 生命周期_创建_修改_登记_作废_历史读取与作废证据保留()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-519-E", "并发客户E");
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeAgreementDto>(await controller.Create(
            SaveDto(customer.Id, agreementNo: "ASF-519-E", feeMethod: "固定金额",
                ratePercent: null, fixedAmount: 1200.50m)));
        AssertOk<AgencyServiceFeeAgreementDto>(await controller.Update(
            created.Id, SaveDto(customer.Id, agreementNo: "ASF-519-E", feeMethod: "固定金额",
                ratePercent: null, fixedAmount: 1300.75m)));
        var recorded = AssertOk<AgencyServiceFeeAgreementDto>(await controller.Record(created.Id));
        var voided = AssertOk<AgencyServiceFeeAgreementDto>(await controller.Void(
            created.Id, new AgencyServiceFeeAgreementVoidRequest { Reason = "服务范围调整" }));

        Assert.Equal(AgencyServiceFeeAgreementRules.StatusVoided, voided.Status);
        Assert.Equal(recorded.RecordedAt, voided.RecordedAt);        // 作废不改写登记时间
        Assert.Equal(recorded.RecordedBy, voided.RecordedBy);        // 作废不改写登记人

        var historical = AssertOk<AgencyServiceFeeAgreementDto>(await controller.GetById(created.Id));
        Assert.Equal(AgencyServiceFeeAgreementRules.StatusVoided, historical.Status);
        Assert.Equal(1300.75m, historical.FixedAmount);              // 原始条款保留可读
        Assert.Equal("服务范围调整", historical.VoidReason);
        Assert.Single(db.AgencyServiceFeeAgreements);                // 不物理删除、也不新增影子记录
    }


    [Fact]
    public async Task 越范围协议_登记与作废与修改一律拒绝且零变更()
    {
        using var db = TestDbFactory.Create();
        var ownCustomer = SeedCustomer(db, "C-519-OWN", "本人客户");
        var foreignCustomer = SeedCustomer(db, "C-519-FGN", "他人客户");
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Create(SaveDto(foreignCustomer.Id, agreementNo: "ASF-519-FGN")));

        // 受限业务员的范围只含本人客户：越范围 / 已删除 / 不存在统一受控不披露存在性。
        var restricted = new SalespersonDataScope
        {
            IsPrivileged = false,
            SalesmanId = 42,
            AllowedCustomerIds = new HashSet<long> { ownCustomer.Id }
        };

        var before = Reload(db, created.Id);
        var recordEx = await AssertBusinessAsync(ErrorCodes.NotFound,
            () => AgencyServiceFeeAgreementService.RecordAsync(db, created.Id, "受限登记人", restricted));
        Assert.Equal(AgencyServiceFeeAgreementRules.AgreementNotFoundText, recordEx.Message);
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => AgencyServiceFeeAgreementService.VoidAsync(db, created.Id, "受限作废", restricted));
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => AgencyServiceFeeAgreementService.UpdateAsync(
                db, created.Id, SaveDto(foreignCustomer.Id, agreementNo: "ASF-519-FGN"), restricted));

        var after = Reload(db, created.Id);
        AssertUnchanged(before, after);
        Assert.Equal(AgencyServiceFeeAgreementRules.StatusDraft, after.Status);
    }

    [Fact]
    public async Task 范围内协议_使用受限范围可正常登记并保留权威客户快照()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-519-IN", "范围内客户");
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Create(SaveDto(customer.Id, agreementNo: "ASF-519-IN")));

        var scope = new SalespersonDataScope
        {
            IsPrivileged = false,
            SalesmanId = 7,
            AllowedCustomerIds = new HashSet<long> { customer.Id }
        };

        var recorded = await AgencyServiceFeeAgreementService.RecordAsync(
            db, created.Id, "范围内登记人", scope);
        Assert.Equal(AgencyServiceFeeAgreementRules.StatusRecorded, recorded.Status);
        Assert.Equal("范围内登记人", recorded.RecordedBy);

        var voided = await AgencyServiceFeeAgreementService.VoidAsync(db, created.Id, "范围内作废", scope);
        Assert.Equal(AgencyServiceFeeAgreementRules.StatusVoided, voided.Status);
        Assert.Equal(customer.CustomerCode, voided.CustomerCode);
        Assert.Equal(customer.CustomerName, voided.CustomerName);
    }

    [Fact]
    public async Task 重复身份创建_被受控拒绝且零新增行()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-519-F", "并发客户F");
        var controller = BuildController(db);

        AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Create(SaveDto(customer.Id, agreementNo: "ASF-519-F")));

        var ex = await AssertBusinessAsync(ErrorCodes.Duplicate,
            () => controller.Create(SaveDto(customer.Id, agreementNo: " ASF_519 f ")));   // 规范化后同一身份
        Assert.DoesNotContain("SqlException", ex.Message);
        Assert.DoesNotContain("UNIQUE", ex.Message);
        Assert.Single(db.AgencyServiceFeeAgreements);
    }


    // ==================== 3. 源码契约（写路由共用同一事务 + 同一行锁） ====================

    [Fact]
    public void 协议写路由_共用同一事务与同一协议行锁_并保留既有校验顺序()
    {
        var service = File.ReadAllText(RepoFile("src", "ERP.Application", "Services",
            "AgencyServiceFeeAgreementService.cs"));

        // 四条写路由都经由共用护栏：同一事务 + 同一协议行锁 + 受控写入 + 拒绝清理。
        Assert.Contains("AgencyFeeEvidenceConcurrencyRules.BeginMutationTransactionAsync", service);
        Assert.Contains("AgencyFeeEvidenceConcurrencyRules.LockAgreementRowAsync", service);
        Assert.Contains("AgencyFeeEvidenceConcurrencyRules.SaveEvidenceAsync", service);
        Assert.Contains("AgencyFeeEvidenceConcurrencyRules.TryRollbackAsync", service);

        // 锁内重读权威状态 / 范围 / 已持久化条款之后才写入（既有 ERP-516 条款复核不被绕过）。
        Assert.Contains("EnsureEditable(agreement.Status, IdentityOf(agreement))", service);
        Assert.Contains("EnsureRecordable(agreement.Status, IdentityOf(agreement))", service);
        Assert.Contains("EnsureVoidable(agreement.Status, IdentityOf(agreement))", service);
        Assert.Contains("EnsureAuthoritativeScope(scope, agreement)", service);
        Assert.Contains("EnsureStoredFeeTermsConsistent(agreement)", service);

        // 范围预检仍在取锁 / 写库之前（被拒请求不刷新任何技术时间戳）。
        var precheckIndex = service.IndexOf(
            "EnsureAgreementInScopeAsync(db, scope, agreementId)", StringComparison.Ordinal);
        var lockIndex = service.IndexOf(
            "AgencyFeeEvidenceConcurrencyRules.LockAgreementRowAsync", StringComparison.Ordinal);
        Assert.True(precheckIndex >= 0 && lockIndex > precheckIndex);
    }

    [Fact]
    public void 控制器路由_仍先做实时身份菜单与客户范围授权()
    {
        var controller = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers",
            "AgencyServiceFeeAgreementController.cs"));

        var authorizations = Regex.Matches(controller,
            "EnsureRegisterAuthorizedAsync\\(_db, CurrentUserId\\(\\)\\)").Count;
        Assert.Equal(6, authorizations);   // 台账 / 详情 / 新增 / 修改 / 登记 / 作废

        // 授权之后才调用服务（授权先于任何读取 / 写入）。
        Assert.Contains("AgencyServiceFeeAgreementService.RecordAsync(_db, id, CurrentUserName(), scope)", controller);
        Assert.DoesNotContain("HttpContext.Items", controller);
    }


    // ==================== 4. 测试脚手架 ====================

    /// <summary>播种既有「客户资料」（customer）功能菜单（复用既有菜单口径，不新增菜单 / 权限）</summary>
    private static SysMenu EnsureCustomerMenu(ErpDbContext db)
    {
        var menu = db.SysMenus.FirstOrDefault(m => !m.IsDeleted
            && m.MenuCode == AgencyServiceFeeReconciliationRules.RequiredMenuCode);
        if (menu is not null) return menu;

        menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = AgencyServiceFeeReconciliationRules.RequiredMenuCode,
            MenuName = AgencyServiceFeeReconciliationRules.RequiredMenuText,
            Path = "/master/customer",
            MenuType = MenuType.Menu
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    /// <summary>播种具备既有「客户资料」菜单的启用账号（系统内置角色 → 数据范围不受限）</summary>
    private static long SeedPrivilegedUser(ErpDbContext db)
    {
        var role = new SysRole
        {
            RoleName = "代理服务费协议并发角色",
            RoleCode = $"AsfAgrConc-{Guid.NewGuid():N}",
            IsSystem = true
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"asf-agr-conc-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "代理服务费协议并发操作员",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        var menu = EnsureCustomerMenu(db);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return user.Id;
    }

    private static AgencyServiceFeeAgreementController BuildController(
        ErpDbContext db, string? displayName = null)
        => BuildControllerWithUser(db, SeedPrivilegedUser(db), displayName);

    private static AgencyServiceFeeAgreementController BuildControllerWithUser(
        ErpDbContext db, long? userId, string? displayName = null)
    {
        var claims = userId.HasValue
            ? new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()),
                new Claim(ClaimTypes.Name, displayName ?? $"代理服务费协议并发用户{userId.Value}")
            }
            : Array.Empty<Claim>();
        return new AgencyServiceFeeAgreementController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
                }
            }
        };
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            CreditLimit = 100000m,
            CreditDays = 30
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }


    private static AgencyServiceFeeAgreementSaveDto SaveDto(
        long customerId, string agreementNo = "ASF-519-001", string feeMethod = "比例费率",
        decimal? ratePercent = 1.5m, decimal? fixedAmount = null, string currency = "USD",
        string feeBasis = "按出口发票金额", DateTime? effectiveFrom = null, DateTime? effectiveTo = null,
        string remark = "")
        => new()
        {
            AgreementNo = agreementNo,
            CustomerId = customerId,
            EffectiveFrom = effectiveFrom ?? new DateTime(2026, 1, 1),
            EffectiveTo = effectiveTo,
            Currency = currency,
            FeeMethod = feeMethod,
            RatePercent = ratePercent,
            FixedAmount = fixedAmount,
            FeeBasis = feeBasis,
            Remark = remark
        };

    private static AgencyServiceFeeAgreement Reload(ErpDbContext db, long id)
    {
        db.ChangeTracker.Clear();
        return db.AgencyServiceFeeAgreements.AsNoTracking().Single(a => a.Id == id);
    }

    /// <summary>被拒请求零变更：协议逐字段（含状态 / 登记 / 作废 / 客户快照与全部审计）不变。</summary>
    private static void AssertUnchanged(AgencyServiceFeeAgreement expected, AgencyServiceFeeAgreement actual)
    {
        Assert.Equal(expected.AgreementNo, actual.AgreementNo);
        Assert.Equal(expected.NormalizedAgreementNo, actual.NormalizedAgreementNo);
        Assert.Equal(expected.CustomerId, actual.CustomerId);
        Assert.Equal(expected.CustomerCode, actual.CustomerCode);
        Assert.Equal(expected.CustomerName, actual.CustomerName);
        Assert.Equal(expected.EffectiveFrom, actual.EffectiveFrom);
        Assert.Equal(expected.EffectiveTo, actual.EffectiveTo);
        Assert.Equal(expected.Currency, actual.Currency);
        Assert.Equal(expected.FeeMethod, actual.FeeMethod);
        Assert.Equal(expected.RatePercent, actual.RatePercent);
        Assert.Equal(expected.FixedAmount, actual.FixedAmount);
        Assert.Equal(expected.FeeBasis, actual.FeeBasis);
        Assert.Equal(expected.Remark, actual.Remark);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.RecordedAt, actual.RecordedAt);
        Assert.Equal(expected.RecordedBy, actual.RecordedBy);
        Assert.Equal(expected.VoidedAt, actual.VoidedAt);
        Assert.Equal(expected.VoidReason, actual.VoidReason);
        Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
        Assert.False(actual.IsDeleted);
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static async Task<BusinessException> AssertBusinessAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

}
