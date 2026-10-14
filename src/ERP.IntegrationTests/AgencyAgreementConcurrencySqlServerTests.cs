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
/// ERP-519 代理服务费协议证据「草稿修改 / 登记冻结 / 作废」并发序列化真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST_ERP519_*</c> 目标）。
/// <list type="number">
/// <item><b>真两条独立连接竞态</b>（各自独立 DbContext / 连接 / 事务，用门闩对齐起跑，
/// 绝不使用时间睡眠）：① 修改 vs 登记；② 修改 vs 作废；③ 登记 vs 登记（恰好一个赢家，登记人与时间戳不被覆盖）；
/// ④ 登记 vs 作废；⑤ 同身份并发创建（恰好一条有效身份，输家受控重复错误，绝不泄露数据库细节）。
/// 每条竞态都断言落库状态 / 条款 / 审计<b>恰好等于一个串行顺序</b>，绝无部分写入。</item>
/// <item><b>确定性拒绝</b>：越范围（授权收敛后）与菜单被撤销的登记 / 作废 / 修改零字段变更；
/// 协议行锁只刷新既有技术审计字段 <c>UpdatedAt</c>，被拒请求的加锁时间戳随事务回滚撤销。</item>
/// <item><b>真实生命周期</b>：新建 → 修改 → 登记 → 作废 → 历史读取，条款 / 客户快照 / 登记人 / 时间戳 /
/// 作废原因在真实列上原样往返；已登记 / 已作废证据绝不再接收编辑。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class AgencyAgreementConcurrencySqlServerTests
    : IClassFixture<AgencyAgreementConcurrencySqlServerFixture>
{
    private readonly AgencyAgreementConcurrencySqlServerFixture _fixture;

    public AgencyAgreementConcurrencySqlServerTests(AgencyAgreementConcurrencySqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(AgencyAgreementConcurrencySqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    // ==================== 独立连接脚手架 ====================

    private readonly record struct ActionOutcome(bool Success, int? Code, string Type, string Error);

    private static AgencyServiceFeeAgreementController NewController(
        ErpDbContext db, long? userId, string? displayName)
    {
        var claims = new List<Claim>();
        if (userId.HasValue) claims.Add(new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()));
        if (!string.IsNullOrEmpty(displayName)) claims.Add(new Claim(ClaimTypes.Name, displayName));

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

    /// <summary>一条独立连接完成一次真实动作（含实时身份 / 菜单 / 客户范围解析），返回成功标志与受控错误。</summary>
    private async Task<ActionOutcome> TryAsync(
        long? userId, string? displayName,
        Func<AgencyServiceFeeAgreementController, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await action(NewController(db, userId, displayName));
            return new ActionOutcome(result is OkObjectResult, ErrorCodes.Success, string.Empty, string.Empty);
        }
        catch (BusinessException ex)
        {
            return new ActionOutcome(false, ex.Code, ex.GetType().Name, ex.Message);
        }
        catch (Exception ex)
        {
            return new ActionOutcome(false, null, ex.GetType().Name, ex.Message);
        }
    }

    private Task<ActionOutcome> TryCreateAsync(
        long? userId, string? displayName, AgencyServiceFeeAgreementSaveDto dto)
        => TryAsync(userId, displayName, ctl => ctl.Create(dto));

    private Task<ActionOutcome> TryUpdateAsync(
        long? userId, string? displayName, long id, AgencyServiceFeeAgreementSaveDto dto)
        => TryAsync(userId, displayName, ctl => ctl.Update(id, dto));

    private Task<ActionOutcome> TryRecordAsync(long? userId, string? displayName, long id)
        => TryAsync(userId, displayName, ctl => ctl.Record(id));

    private Task<ActionOutcome> TryVoidAsync(long? userId, string? displayName, long id, string reason)
        => TryAsync(userId, displayName, ctl => ctl.Void(
            id, new AgencyServiceFeeAgreementVoidRequest { Reason = reason }));

    /// <summary>两条独立连接以同一起跑线并发执行（门闩对齐 + 独立 DbContext / 连接 / 事务；绝不用时间睡眠）。</summary>
    private static async Task<List<T>> RaceAsync<T>(Func<Task<T>> first, Func<Task<T>> second)
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new[] { first, second }
            .Select(action => Task.Run(async () =>
            {
                await gate.Task;
                return await action();
            }))
            .ToArray();

        gate.SetResult(true);
        return (await Task.WhenAll(tasks)).ToList();
    }

    // ==================== 种子 / 观测脚手架（真实既有菜单与主数据，不新增权限模型） ====================

    private static async Task<BaseCustomer> SeedCustomerAsync(ErpDbContext db, string code, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code, CustomerName = code, Status = 1, CreditStatus = "正常", EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    /// <summary>
    /// 播种真实操作账号：既有「客户资料」（customer）功能菜单 + 可选业务员客户数据范围
    /// （<paramref name="privileged"/> 为 true 时复用系统内置角色口径，否则映射到
    /// <paramref name="inScopeCustomerId"/> 客户）。绝不新增任何用户授权。
    /// </summary>
    private static async Task<(long UserId, long RoleId)> SeedOperatorAsync(
        ErpDbContext db, long? inScopeCustomerId, bool privileged = false, bool withMenu = true)
    {
        var code = $"int-e519-op-{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleCode = $"INT_E519_ROLE_{Guid.NewGuid():N}",
            RoleName = "代理服务费协议并发集成测试角色",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        if (withMenu)
        {
            var menuId = await db.SysMenus.AsNoTracking()
                .Where(m => !m.IsDeleted && m.MenuCode == AgencyServiceFeeReconciliationRules.RequiredMenuCode)
                .Select(m => m.Id)
                .FirstAsync();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menuId });
            await db.SaveChangesAsync();
        }

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "代理服务费协议并发集成账号", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (inScopeCustomerId is > 0)
        {
            var customer = await db.BaseCustomers.SingleAsync(c => c.Id == inScopeCustomerId.Value);
            customer.EmpId = employee.Id;
            await db.SaveChangesAsync();
        }

        return (user.Id, role.Id);
    }

    /// <summary>撤销本测试自己授予的菜单（既有角色授权收敛，绝不新增授权）。</summary>
    private static async Task RevokeMenuAsync(ErpDbContext db, long roleId)
    {
        var menuId = await db.SysMenus.AsNoTracking()
            .Where(m => !m.IsDeleted && m.MenuCode == AgencyServiceFeeReconciliationRules.RequiredMenuCode)
            .Select(m => m.Id)
            .FirstAsync();
        var grants = await db.SysRoleMenus
            .Where(g => g.RoleId == roleId && g.MenuId == menuId && !g.IsDeleted).ToListAsync();
        foreach (var grant in grants) grant.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    private static AgencyServiceFeeAgreementSaveDto SaveDto(
        long customerId, string agreementNo, string? feeMethod = null, decimal? ratePercent = 1.5m,
        decimal? fixedAmount = null, string currency = "USD", string feeBasis = "按出口发票金额",
        string remark = "")
        => new()
        {
            AgreementNo = agreementNo,
            CustomerId = customerId,
            EffectiveFrom = new DateTime(2026, 1, 1),
            Currency = currency,
            FeeMethod = feeMethod ?? AgencyServiceFeeAgreementRules.FeeMethodRate,
            RatePercent = ratePercent,
            FixedAmount = fixedAmount,
            FeeBasis = feeBasis,
            Remark = remark
        };

    private async Task<AgencyServiceFeeAgreement> ReloadAsync(long id)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.AgencyServiceFeeAgreements.AsNoTracking().SingleAsync(a => a.Id == id);
    }

    private async Task<int> CountIdentityRowsAsync(long customerId, string agreementNo)
    {
        var normalized = AgencyServiceFeeAgreementRules.NormalizeIdentityPart(agreementNo);
        await using var db = _fixture.CreateDbContext();
        return await db.AgencyServiceFeeAgreements.AsNoTracking().CountAsync(a =>
            a.CustomerId == customerId && a.NormalizedAgreementNo == normalized);
    }

    private static void AssertUnchanged(AgencyServiceFeeAgreement expected, AgencyServiceFeeAgreement actual)
    {
        Assert.Equal(expected.AgreementNo, actual.AgreementNo);
        Assert.Equal(expected.NormalizedAgreementNo, actual.NormalizedAgreementNo);
        Assert.Equal(expected.CustomerId, actual.CustomerId);
        Assert.Equal(expected.CustomerCode, actual.CustomerCode);
        Assert.Equal(expected.CustomerName, actual.CustomerName);
        Assert.Equal(expected.Currency, actual.Currency);
        Assert.Equal(expected.FeeMethod, actual.FeeMethod);
        Assert.Equal(expected.RatePercent, actual.RatePercent);
        Assert.Equal(expected.FixedAmount, actual.FixedAmount);
        Assert.Equal(expected.FeeBasis, actual.FeeBasis);
        Assert.Equal(expected.EffectiveFrom, actual.EffectiveFrom);
        Assert.Equal(expected.Remark, actual.Remark);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.RecordedAt, actual.RecordedAt);
        Assert.Equal(expected.RecordedBy, actual.RecordedBy);
        Assert.Equal(expected.VoidedAt, actual.VoidedAt);
        Assert.Equal(expected.VoidReason, actual.VoidReason);
        Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
    }


    private async Task<long> AgreementIdAsync(string agreementNo)
    {
        var normalized = AgencyServiceFeeAgreementRules.NormalizeIdentityPart(agreementNo);
        await using var db = _fixture.CreateDbContext();
        return await db.AgencyServiceFeeAgreements.AsNoTracking()
            .Where(a => a.NormalizedAgreementNo == normalized)
            .Select(a => a.Id)
            .SingleAsync();
    }

    private async Task<int> CountActiveIdentityAsync(long customerId, string agreementNo)
    {
        var normalized = AgencyServiceFeeAgreementRules.NormalizeIdentityPart(agreementNo);
        await using var db = _fixture.CreateDbContext();
        return await db.AgencyServiceFeeAgreements.AsNoTracking().CountAsync(a =>
            a.CustomerId == customerId && a.NormalizedAgreementNo == normalized && !a.IsDeleted
            && a.Status != AgencyServiceFeeAgreementRules.StatusVoided);
    }

    // ==================== 1. 真两条独立连接竞态：修改 vs 登记 / 修改 vs 作废 ====================

    [Fact]
    public async Task Race_update_and_record_serialize_without_partial_state()
    {
        Guard();
        long userId, customerId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E519_A_{Tag()}");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;
        }

        var agreementNo = $"ASF-519-A-{Tag()}";
        var created = await TryCreateAsync(userId, "并发创建人", SaveDto(customerId, agreementNo, ratePercent: 1.5m));
        Assert.True(created.Success, created.Error);
        var id = await AgreementIdAsync(agreementNo);
        var original = await ReloadAsync(id);

        var results = await RaceAsync(
            () => TryUpdateAsync(userId, "并发修改人", id,
                SaveDto(customerId, agreementNo, ratePercent: 9.9m, feeBasis: "修订口径")),
            () => TryRecordAsync(userId, "并发登记人", id));

        var update = results[0];
        var record = results[1];

        // 登记必然成功（草稿或修改后仍是草稿）；修改是否成功取决于串行顺序。
        Assert.True(record.Success, record.Error);

        var stored = await ReloadAsync(id);
        Assert.Equal(AgencyServiceFeeAgreementRules.StatusRecorded, stored.Status);
        Assert.Equal("并发登记人", stored.RecordedBy);
        Assert.NotNull(stored.RecordedAt);

        if (update.Success)
        {
            // 串行顺序「先修改、后登记」：冻结的必须是修改后的条款。
            Assert.Equal(9.9m, stored.RatePercent);
            Assert.Equal("修订口径", stored.FeeBasis);
        }
        else
        {
            // 串行顺序「先登记、后修改」：迟到的草稿修改受控拒绝且零字段变更。
            Assert.Equal(ErrorCodes.RuleConflict, update.Code);
            Assert.Equal(1.5m, stored.RatePercent);
            Assert.Equal(original.FeeBasis, stored.FeeBasis);
        }

        Assert.Equal(original.AgreementNo, stored.AgreementNo);
        Assert.Equal(original.CustomerCode, stored.CustomerCode);   // 客户快照不可变
        Assert.Equal(original.CustomerName, stored.CustomerName);
        Assert.Equal(1, await CountIdentityRowsAsync(customerId, agreementNo));
    }

    [Fact]
    public async Task Race_update_and_void_serialize_without_partial_state()
    {
        Guard();
        long userId, customerId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E519_B_{Tag()}");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;
        }

        var agreementNo = $"ASF-519-B-{Tag()}";
        var created = await TryCreateAsync(userId, "并发创建人", SaveDto(customerId, agreementNo, ratePercent: 1.5m));
        Assert.True(created.Success, created.Error);
        var id = await AgreementIdAsync(agreementNo);

        var results = await RaceAsync(
            () => TryUpdateAsync(userId, "并发修改人", id,
                SaveDto(customerId, agreementNo, ratePercent: 9.9m, feeBasis: "修订口径")),
            () => TryVoidAsync(userId, "并发作废人", id, "并发作废原因"));

        var update = results[0];
        var voidOutcome = results[1];

        // 作废必然成功（草稿与已登记都可作废一次）；修改是否成功取决于串行顺序。
        Assert.True(voidOutcome.Success, voidOutcome.Error);

        var stored = await ReloadAsync(id);
        Assert.Equal(AgencyServiceFeeAgreementRules.StatusVoided, stored.Status);
        Assert.Equal("并发作废原因", stored.VoidReason);       // 原始作废原因被保留
        Assert.Null(stored.RecordedAt);                        // 从未登记
        Assert.Equal(string.Empty, stored.RecordedBy);

        if (update.Success)
        {
            Assert.Equal(9.9m, stored.RatePercent);
            Assert.Equal("修订口径", stored.FeeBasis);
        }
        else
        {
            Assert.Equal(ErrorCodes.RuleConflict, update.Code);
            Assert.Equal(1.5m, stored.RatePercent);
        }

        Assert.Equal(1, await CountIdentityRowsAsync(customerId, agreementNo));
    }

    // ==================== 2. 真两条独立连接竞态：登记 vs 登记 / 登记 vs 作废 / 同身份创建 ====================

    [Fact]
    public async Task Race_two_registrations_exactly_one_winner_preserving_audit()
    {
        Guard();
        long userId, customerId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E519_C_{Tag()}");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;
        }

        var agreementNo = $"ASF-519-C-{Tag()}";
        var created = await TryCreateAsync(userId, "并发创建人", SaveDto(customerId, agreementNo, ratePercent: 1.5m));
        Assert.True(created.Success, created.Error);
        var id = await AgreementIdAsync(agreementNo);

        var results = await RaceAsync(
            () => TryRecordAsync(userId, "登记人甲", id),
            () => TryRecordAsync(userId, "登记人乙", id));

        // 恰好一个赢家：输家在锁内读到「已登记」一律受控拒绝。
        Assert.NotEqual(results[0].Success, results[1].Success);
        var loser = results[0].Success ? results[1] : results[0];
        var winnerName = results[0].Success ? "登记人甲" : "登记人乙";
        Assert.Equal(ErrorCodes.RuleConflict, loser.Code);

        var stored = await ReloadAsync(id);
        Assert.Equal(AgencyServiceFeeAgreementRules.StatusRecorded, stored.Status);
        Assert.Equal(winnerName, stored.RecordedBy);      // 赢家审计绝不被输家覆盖
        Assert.NotNull(stored.RecordedAt);
        Assert.Equal(1.5m, stored.RatePercent);           // 条款在登记过程中原样冻结
        Assert.Equal(1, await CountIdentityRowsAsync(customerId, agreementNo));
    }

    [Fact]
    public async Task Race_record_and_void_serialize_without_partial_state()
    {
        Guard();
        long userId, customerId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E519_D_{Tag()}");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;
        }

        var agreementNo = $"ASF-519-D-{Tag()}";
        var created = await TryCreateAsync(userId, "并发创建人", SaveDto(customerId, agreementNo, ratePercent: 1.5m));
        Assert.True(created.Success, created.Error);
        var id = await AgreementIdAsync(agreementNo);

        var results = await RaceAsync(
            () => TryRecordAsync(userId, "并发登记人", id),
            () => TryVoidAsync(userId, "并发作废人", id, "并发作废原因"));

        var record = results[0];
        var voidOutcome = results[1];

        // 作废在两种串行顺序下都必须成功（草稿或已登记都可作废一次）。
        Assert.True(voidOutcome.Success, voidOutcome.Error);

        var stored = await ReloadAsync(id);
        Assert.Equal(AgencyServiceFeeAgreementRules.StatusVoided, stored.Status);
        Assert.Equal("并发作废原因", stored.VoidReason);
        Assert.Equal(1.5m, stored.RatePercent);

        if (record.Success)
        {
            // 串行顺序「先登记、后作废」：登记留痕必须被保留。
            Assert.Equal("并发登记人", stored.RecordedBy);
            Assert.NotNull(stored.RecordedAt);
        }
        else
        {
            // 串行顺序「先作废、后登记」：迟到的登记受控拒绝，绝不把作废证据补写为已登记。
            Assert.Equal(ErrorCodes.RuleConflict, record.Code);
            Assert.Equal(string.Empty, stored.RecordedBy);
            Assert.Null(stored.RecordedAt);
        }

        Assert.Equal(1, await CountIdentityRowsAsync(customerId, agreementNo));
    }

    [Fact]
    public async Task Race_duplicate_identity_create_exactly_one_active_row()
    {
        Guard();
        long userId, customerId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E519_E_{Tag()}");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;
        }

        var agreementNo = $"ASF-519-E-{Tag()}";
        // 规范化后同一身份（大小写与分隔符差异由服务端规范化收敛）。
        var variant = agreementNo.ToLowerInvariant().Replace('-', '_');

        var results = await RaceAsync(
            () => TryCreateAsync(userId, "创建人甲", SaveDto(customerId, agreementNo)),
            () => TryCreateAsync(userId, "创建人乙", SaveDto(customerId, variant)));

        Assert.NotEqual(results[0].Success, results[1].Success);
        var loser = results[0].Success ? results[1] : results[0];
        Assert.Equal(ErrorCodes.Duplicate, loser.Code);
        // 受控错误绝不暴露数据库内部细节（驱动异常 / 错误码 / 索引名）。
        Assert.DoesNotContain("SqlException", loser.Error);
        Assert.DoesNotContain("2601", loser.Error);
        Assert.DoesNotContain("UNIQUE", loser.Error);
        Assert.DoesNotContain("UX_AgencyServiceFeeAgreements", loser.Error);

        // 恰好一条有效身份：草稿同样占用身份。
        Assert.Equal(1, await CountActiveIdentityAsync(customerId, agreementNo));
    }


    // ==================== 3. 确定性拒绝：越范围 / 菜单撤销 / 冻结后编辑 ====================

    [Fact]
    public async Task Cross_scope_and_revoked_menu_writes_leave_agreement_unchanged()
    {
        Guard();
        long privilegedUserId, restrictedUserId, restrictedRoleId, foreignCustomerId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var own = await SeedCustomerAsync(seed, $"INT_E519_OWN_{Tag()}");
            var foreign = await SeedCustomerAsync(seed, $"INT_E519_FGN_{Tag()}");
            foreignCustomerId = foreign.Id;
            privilegedUserId = (await SeedOperatorAsync(seed, null, privileged: true)).UserId;
            (restrictedUserId, restrictedRoleId) = await SeedOperatorAsync(seed, own.Id);
        }

        var agreementNo = $"ASF-519-S-{Tag()}";
        var created = await TryCreateAsync(
            privilegedUserId, "特权创建人", SaveDto(foreignCustomerId, agreementNo));
        Assert.True(created.Success, created.Error);
        var id = await AgreementIdAsync(agreementNo);
        var before = await ReloadAsync(id);

        // 受限业务员的范围不含该客户 → 登记 / 修改 / 作废一律受控不披露存在性且零字段变更。
        var record = await TryRecordAsync(restrictedUserId, "越范围登记人", id);
        Assert.False(record.Success);
        Assert.Equal(ErrorCodes.NotFound, record.Code);
        Assert.DoesNotContain(id.ToString(), record.Error);
        var update = await TryUpdateAsync(
            restrictedUserId, null, id, SaveDto(foreignCustomerId, agreementNo));
        Assert.Equal(ErrorCodes.NotFound, update.Code);
        var voidOutcome = await TryVoidAsync(restrictedUserId, null, id, "越范围作废");
        Assert.Equal(ErrorCodes.NotFound, voidOutcome.Code);

        var afterDenied = await ReloadAsync(id);
        AssertUnchanged(before, afterDenied);
        Assert.Equal(AgencyServiceFeeAgreementRules.StatusDraft, afterDenied.Status);

        // 菜单撤销后（同一受权账号）→ 权限不足；拒绝发生在取锁 / 写库之前，审计时间戳同样不变。
        await using (var revoke = _fixture.CreateDbContext())
            await RevokeMenuAsync(revoke, restrictedRoleId);
        var deniedRecord = await TryRecordAsync(restrictedUserId, "撤销后登记人", id);
        Assert.Equal(ErrorCodes.Forbidden, deniedRecord.Code);
        var deniedVoid = await TryVoidAsync(restrictedUserId, null, id, "撤销后作废");
        Assert.Equal(ErrorCodes.Forbidden, deniedVoid.Code);
        AssertUnchanged(before, await ReloadAsync(id));

        // 特权账号授权未受影响：合法登记照常完成（既有授权口径不被本次改动改变）。
        var allowed = await TryRecordAsync(privilegedUserId, "特权登记人", id);
        Assert.True(allowed.Success, allowed.Error);
        var recorded = await ReloadAsync(id);
        Assert.Equal(AgencyServiceFeeAgreementRules.StatusRecorded, recorded.Status);
        Assert.Equal("特权登记人", recorded.RecordedBy);
    }

    [Fact]
    public async Task Valid_lifecycle_persists_exact_terms_and_rejects_edits_after_freeze()
    {
        Guard();
        long userId, customerId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E519_L_{Tag()}");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;
        }

        var agreementNo = $"ASF-519-L-{Tag()}";
        var created = await TryCreateAsync(
            userId, "生命周期创建人", SaveDto(customerId, agreementNo, ratePercent: 1.25m));
        Assert.True(created.Success, created.Error);
        var id = await AgreementIdAsync(agreementNo);

        var updated = await TryUpdateAsync(userId, "生命周期修改人", id,
            SaveDto(customerId, agreementNo, ratePercent: 2.75m, feeBasis: "修订计费依据"));
        Assert.True(updated.Success, updated.Error);

        var recorded = await TryRecordAsync(userId, "生命周期登记人", id);
        Assert.True(recorded.Success, recorded.Error);
        var frozen = await ReloadAsync(id);
        Assert.Equal(AgencyServiceFeeAgreementRules.StatusRecorded, frozen.Status);
        Assert.Equal(2.75m, frozen.RatePercent);
        Assert.Equal("修订计费依据", frozen.FeeBasis);
        Assert.Equal("生命周期登记人", frozen.RecordedBy);
        Assert.NotNull(frozen.RecordedAt);

        // 登记后：迟到修改 / 重复登记一律受控拒绝，且逐字段（含锁定时间戳）不变 → 加锁时间戳随事务回滚撤销。
        var lateEdit = await TryUpdateAsync(userId, "迟到修改人", id,
            SaveDto(customerId, agreementNo, ratePercent: 8.5m));
        Assert.Equal(ErrorCodes.RuleConflict, lateEdit.Code);
        AssertUnchanged(frozen, await ReloadAsync(id));

        var repeated = await TryRecordAsync(userId, "重复登记人", id);
        Assert.Equal(ErrorCodes.RuleConflict, repeated.Code);
        AssertUnchanged(frozen, await ReloadAsync(id));

        // 登记后可显式作废一次：原始条款 / 登记人 / 登记时间保留，历史仍可读。
        var voided = await TryVoidAsync(userId, "生命周期作废人", id, "服务范围调整");
        Assert.True(voided.Success, voided.Error);
        var afterVoid = await ReloadAsync(id);
        Assert.Equal(AgencyServiceFeeAgreementRules.StatusVoided, afterVoid.Status);
        Assert.Equal(frozen.RecordedBy, afterVoid.RecordedBy);
        Assert.Equal(frozen.RecordedAt, afterVoid.RecordedAt);
        Assert.Equal(frozen.RatePercent, afterVoid.RatePercent);
        Assert.Equal(frozen.FeeBasis, afterVoid.FeeBasis);
        Assert.Equal("服务范围调整", afterVoid.VoidReason);

        var afterVoidEdit = await TryUpdateAsync(userId, "作废后修改人", id,
            SaveDto(customerId, agreementNo, ratePercent: 3.5m));
        Assert.Equal(ErrorCodes.RuleConflict, afterVoidEdit.Code);
        AssertUnchanged(afterVoid, await ReloadAsync(id));

        var afterVoidRecord = await TryRecordAsync(userId, "作废后登记人", id);
        Assert.Equal(ErrorCodes.RuleConflict, afterVoidRecord.Code);
        AssertUnchanged(afterVoid, await ReloadAsync(id));
    }


    [Fact]
    public async Task Soft_deleted_agreement_rejects_lifecycle_writes_with_controlled_error()
    {
        Guard();
        long userId, customerId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E519_X_{Tag()}");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;
        }

        var agreementNo = $"ASF-519-X-{Tag()}";
        var created = await TryCreateAsync(userId, "并发创建人", SaveDto(customerId, agreementNo));
        Assert.True(created.Success, created.Error);
        var id = await AgreementIdAsync(agreementNo);
        var before = await ReloadAsync(id);

        // 独立连接软删除协议行（模拟「被并发删除」）。
        await using (var delete = _fixture.CreateDbContext())
        {
            var row = await delete.AgencyServiceFeeAgreements.SingleAsync(a => a.Id == id);
            row.IsDeleted = true;
            row.UpdatedAt = DateTime.Now;
            await delete.SaveChangesAsync();
        }

        // 已删除 / 不存在统一受控错误（不披露存在性），绝不落任何部分写入。
        var record = await TryRecordAsync(userId, "删除后登记人", id);
        Assert.Equal(ErrorCodes.NotFound, record.Code);
        var voidOutcome = await TryVoidAsync(userId, null, id, "删除后作废");
        Assert.Equal(ErrorCodes.NotFound, voidOutcome.Code);
        var update = await TryUpdateAsync(userId, null, id,
            SaveDto(customerId, agreementNo, ratePercent: 4.5m));
        Assert.Equal(ErrorCodes.NotFound, update.Code);

        await using var verify = _fixture.CreateDbContext();
        var stored = await verify.AgencyServiceFeeAgreements.AsNoTracking().SingleAsync(a => a.Id == id);
        Assert.True(stored.IsDeleted);                       // 软删除行不被恢复
        Assert.Equal(before.Status, stored.Status);
        Assert.Null(stored.RecordedAt);
        Assert.Equal(string.Empty, stored.RecordedBy);
        Assert.Equal(before.RatePercent, stored.RatePercent);
        Assert.Equal(string.Empty, stored.VoidReason);
    }

}

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-519）：把目标库初始化为完整 NEWERP 结构 + 种子数据，供真实 SQL Server 竞争测试复用。
/// <para>安全口径：库名一律带本次运行的 GUID 后缀（绝不复用 / 绝不 drop 既有库）；任何数据库访问之前先校验
/// 实例名、库名前缀与集成安全；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env /
/// 生产凭据。</para>
/// </summary>
public sealed class AgencyAgreementConcurrencySqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP519_AGREEMENTCONCURRENCY";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine(
            $"[ERP-519] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await EnsureFreshDatabaseAsync();
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

    private async Task EnsureFreshDatabaseAsync()
    {
        var database = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;

        // 任何数据库访问之前再次护栏：绝不使用生产 / 非专用回退。
        AssertDedicatedTarget(ConnectionString);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // 绝不销毁已存在的 Fixture 库或其它调用方的数据库。
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

        Console.WriteLine("[ERP-519] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class AgencyAgreementConcurrencyTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_ERP519_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_ERP519_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_ERP519_BAD;User Id=sa;Password=x")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => AgencyAgreementConcurrencySqlServerFixture.AssertDedicatedTarget(connection));

    [Fact]
    public void Accepts_the_dedicated_localdb_target_with_integrated_security()
        => AgencyAgreementConcurrencySqlServerFixture.AssertDedicatedTarget(
            "Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_ERP519_X;Integrated Security=true");
}

