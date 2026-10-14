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
/// ERP-520 代理服务费对账单证据「草稿行替换 / 登记冻结 / 作废」并发序列化真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST_ERP520_*</c> 目标）。
/// <list type="number">
/// <item><b>真两条独立连接竞态</b>（各自独立 DbContext / 连接 / 事务，用门闩对齐起跑，绝不使用时间睡眠）：
/// ① 草稿行替换 vs 登记；② 草稿行替换 vs 作废；③ 登记 vs 登记（恰好一个赢家，登记人与时间戳不被覆盖）；
/// ④ 登记 vs 作废；⑤ 登记 vs 关键行来源被并发取消（多行迟到失效，要么两行全部冻结、要么零部分冻结）。
/// 每条竞态都断言落库状态 / 合计 / 行集合 / 审计<b>恰好等于一个串行顺序</b>，绝无部分写入。</item>
/// <item><b>确定性拒绝</b>：越范围（授权收敛后）与菜单被撤销的修改 / 登记 / 作废零字段变更；
/// 对账单行锁只刷新既有技术审计字段 <c>UpdatedAt</c>，被拒请求的加锁时间戳随事务回滚撤销。</item>
/// <item><b>真实生命周期</b>：新建 → 修改（整行替换）→ 登记 → 作废 → 历史读取，表头 / 行 / 来源快照 /
/// 登记人 / 时间戳 / 作废原因在真实列上原样往返；已登记 / 已作废证据绝不再接收编辑。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class AgencyStatementConcurrencySqlServerTests
    : IClassFixture<AgencyStatementConcurrencySqlServerFixture>
{
    private readonly AgencyStatementConcurrencySqlServerFixture _fixture;

    public AgencyStatementConcurrencySqlServerTests(AgencyStatementConcurrencySqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(AgencyStatementConcurrencySqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    // ==================== 独立连接脚手架 ====================

    private readonly record struct ActionOutcome(bool Success, int? Code, string Type, string Error);

    private static AgencyServiceFeeStatementController NewController(
        ErpDbContext db, long? userId, string? displayName)
    {
        var claims = new List<Claim>();
        if (userId.HasValue) claims.Add(new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()));
        if (!string.IsNullOrEmpty(displayName)) claims.Add(new Claim(ClaimTypes.Name, displayName));

        return new AgencyServiceFeeStatementController(db)
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
        Func<AgencyServiceFeeStatementController, Task<IActionResult>> action)
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

    private Task<ActionOutcome> TryCreateAsync(long? userId, AgencyServiceFeeStatementSaveDto dto)
        => TryAsync(userId, null, ctl => ctl.Create(dto));

    private Task<ActionOutcome> TryUpdateAsync(long? userId, long id, AgencyServiceFeeStatementSaveDto dto)
        => TryAsync(userId, null, ctl => ctl.Update(id, dto));

    private Task<ActionOutcome> TryRecordAsync(long? userId, string? displayName, long id)
        => TryAsync(userId, displayName, ctl => ctl.Record(id));

    private Task<ActionOutcome> TryVoidAsync(long? userId, long id, string reason)
        => TryAsync(userId, null, ctl => ctl.Void(id, new AgencyServiceFeeStatementVoidRequest { Reason = reason }));

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
            CustomerCode = code, CustomerName = code, Status = 1, CreditStatus = "正常",
            CreditLimit = 100000m, CreditDays = 30, EmpId = empId
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
        ErpDbContext db, long? inScopeCustomerId, bool privileged = false)
    {
        var code = $"int-e520-op-{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleCode = $"INT_E520_ROLE_{Guid.NewGuid():N}",
            RoleName = "代理服务费对账单并发集成测试角色",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var menuId = await db.SysMenus.AsNoTracking()
            .Where(m => !m.IsDeleted && m.MenuCode == AgencyServiceFeeReconciliationRules.RequiredMenuCode)
            .Select(m => m.Id)
            .FirstAsync();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menuId });
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "代理服务费对账单并发集成账号", Status = UserStatus.Enabled
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

    /// <summary>播种一条**已登记**的 ERP-069 协议证据（对账单的显式依据）。</summary>
    private static async Task<long> SeedAgreementAsync(ErpDbContext db, long customerId, string agreementNo)
    {
        var agreement = new AgencyServiceFeeAgreement
        {
            AgreementNo = agreementNo,
            NormalizedAgreementNo = AgencyServiceFeeAgreementRules.NormalizeAgreementNo(agreementNo),
            CustomerId = customerId,
            Currency = "USD",
            EffectiveFrom = new DateTime(2026, 1, 1),
            FeeMethod = AgencyServiceFeeAgreementRules.FeeMethodRate,
            RatePercent = 1.5m,
            FeeBasis = "按出口发票金额",
            Status = AgencyServiceFeeAgreementRules.StatusRecorded,
            RecordedAt = new DateTime(2026, 1, 2),
            RecordedBy = "集成测试"
        };
        db.AgencyServiceFeeAgreements.Add(agreement);
        await db.SaveChangesAsync();
        return agreement.Id;
    }

    private static async Task<long> SeedOrderAsync(ErpDbContext db, long customerId, string orderNo)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo, OrderDate = new DateTime(2026, 8, 15), CustomerId = customerId,
            Currency = Currency.USD, TotalAmount = 1000m, Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    }

    private static AgencyServiceFeeStatementLineSaveDto LineDto(
        string sourceType, long sourceId, decimal amount, string description = "代理服务费",
        string basisNote = "按协议约定的服务期间")
        => new()
        {
            SourceType = sourceType, SourceId = sourceId, Description = description,
            Amount = amount, BasisNote = basisNote, Remark = string.Empty
        };

    private static AgencyServiceFeeStatementSaveDto SaveDto(
        long customerId, long agreementId, IEnumerable<AgencyServiceFeeStatementLineSaveDto> lines,
        string statementNo, string currency = "USD")
        => new()
        {
            StatementNo = statementNo,
            CustomerId = customerId,
            Currency = currency,
            StatementDate = new DateTime(2026, 9, 20),
            ServicePeriodFrom = new DateTime(2026, 8, 1),
            ServicePeriodTo = new DateTime(2026, 8, 31),
            AgreementId = agreementId,
            Lines = lines.ToList()
        };

    private static AgencyServiceFeeStatementSaveDto SalesOrderSave(
        long customerId, long agreementId, long orderId, string statementNo, decimal amount = 100m)
        => SaveDto(customerId, agreementId,
            new[] { LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, orderId, amount) },
            statementNo);

    private async Task<AgencyServiceFeeStatement> ReloadAsync(long id)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.AgencyServiceFeeStatements.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private async Task<List<AgencyServiceFeeStatementLine>> ActiveLinesAsync(long statementId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.AgencyServiceFeeStatementLines.AsNoTracking()
            .Where(l => l.StatementId == statementId && !l.IsDeleted)
            .OrderBy(l => l.LineNo).ThenBy(l => l.Id)
            .ToListAsync();
    }

    private async Task<long> StatementIdAsync(string statementNo)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.AgencyServiceFeeStatements.AsNoTracking()
            .Where(x => x.StatementNo == statementNo)
            .Select(x => x.Id)
            .SingleAsync();
    }

    private async Task<int> CountStatementsAsync(long customerId, string statementNo)
    {
        var normalized = AgencyServiceFeeStatementRules.NormalizeIdentityPart(statementNo);
        await using var db = _fixture.CreateDbContext();
        return await db.AgencyServiceFeeStatements.AsNoTracking()
            .CountAsync(x => x.CustomerId == customerId && x.NormalizedStatementNo == normalized);
    }

    private async Task CancelOrderAsync(long orderId)
    {
        await using var db = _fixture.CreateDbContext();
        var row = await db.SalesOrders.SingleAsync(o => o.Id == orderId);
        row.Status = DocumentStatus.Cancelled;
        row.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();
    }

    /// <summary>被拒请求零变更：表头逐字段（含状态 / 合计 / 登记 / 作废与全部审计）与全部有效行不变。</summary>
    private async Task AssertStatementUnchangedAsync(
        AgencyServiceFeeStatement expected, AgencyServiceFeeStatement actual)
    {
        Assert.Equal(expected.StatementNo, actual.StatementNo);
        Assert.Equal(expected.NormalizedStatementNo, actual.NormalizedStatementNo);
        Assert.Equal(expected.CustomerId, actual.CustomerId);
        Assert.Equal(expected.CustomerCode, actual.CustomerCode);
        Assert.Equal(expected.CustomerName, actual.CustomerName);
        Assert.Equal(expected.Currency, actual.Currency);
        Assert.Equal(expected.StatementDate, actual.StatementDate);
        Assert.Equal(expected.DueDate, actual.DueDate);
        Assert.Equal(expected.ServicePeriodFrom, actual.ServicePeriodFrom);
        Assert.Equal(expected.ServicePeriodTo, actual.ServicePeriodTo);
        Assert.Equal(expected.AgreementId, actual.AgreementId);
        Assert.Equal(expected.AgreementNo, actual.AgreementNo);
        Assert.Equal(expected.AgreementCurrency, actual.AgreementCurrency);
        Assert.Equal(expected.AgreementCustomerId, actual.AgreementCustomerId);
        Assert.Equal(expected.AgreementFeeMethod, actual.AgreementFeeMethod);
        Assert.Equal(expected.AgreementTermsText, actual.AgreementTermsText);
        Assert.Equal(expected.TotalAmount, actual.TotalAmount);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.RecordedAt, actual.RecordedAt);
        Assert.Equal(expected.RecordedBy, actual.RecordedBy);
        Assert.Equal(expected.VoidedAt, actual.VoidedAt);
        Assert.Equal(expected.VoidReason, actual.VoidReason);
        Assert.Equal(expected.Remark, actual.Remark);
        Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
        Assert.False(actual.IsDeleted);

        foreach (var line in await ActiveLinesAsync(expected.Id))
        {
            Assert.Equal(AgencyServiceFeeStatementRules.StatusDraft, line.Status);
            Assert.Null(line.RecordedAt);
            Assert.Equal(string.Empty, line.RecordedBy);
            Assert.Null(line.VoidedAt);
            Assert.Equal(string.Empty, line.VoidReason);
        }
    }

    // ==================== 1. 双独立连接竞态：草稿整行替换 vs 登记 / 作废 ====================

    /// <summary>
    /// 竞态①：<b>草稿整行替换 vs 登记</b>（两条独立连接 + 门闩对齐起跑，绝不使用时间睡眠）。
    /// 只允许两种一致串行结果：登记赢 → 迟到替换被锁内权威状态受控拒绝（RuleConflict「已登记」）、
    /// 旧行未被软删除、新行未落库；替换赢 → 登记在锁内读到替换后的草稿并原样冻结（含旧行的软删除痕迹）。
    /// 两种结果都断言：合计 = 有效行金额之和、表头与全部行状态一致、登记审计成对存在，绝不出现
    /// 「已登记表头 + 事后草稿行」的混合证据，也绝无部分写入残留。
    /// </summary>
    [Fact]
    public async Task Race_draft_line_replacement_versus_record_is_serialized_without_mixed_evidence()
    {
        Guard();
        var scenario = await SeedScenarioAsync("R1");
        var operatorId = await SeedPrivilegedOperatorAsync();
        var statementNo = $"ASFS-520-R1-{Tag()}";
        var statementId = await CreateDraftAsync(operatorId, SalesOrderSave(
            scenario.CustomerId, scenario.AgreementId, scenario.FirstOrderId, statementNo, amount: 100m));

        var outcomes = await RaceAsync(
            () => TryUpdateAsync(operatorId, statementId, SalesOrderSave(
                scenario.CustomerId, scenario.AgreementId, scenario.SecondOrderId, statementNo, amount: 999m)),
            () => TryRecordAsync(operatorId, "并发登记人", statementId));

        var replace = outcomes[0];
        var record = outcomes[1];
        Assert.True(record.Success, $"登记必须成功（先冻结或在替换后冻结）：{record.Type} {record.Error}");

        var header = await ReloadAsync(statementId);
        var rows = await AllLinesAsync(statementId);
        Assert.Equal(AgencyServiceFeeStatementRules.StatusRecorded, header.Status);
        Assert.Equal("并发登记人", header.RecordedBy);
        Assert.NotNull(header.RecordedAt);

        if (replace.Success)
        {
            // 串行顺序 替换 → 登记：冻结的是替换后的行集合；旧草稿行只被软删除（保留可审计痕迹）。
            Assert.Equal(999m, header.TotalAmount);
            Assert.Equal(2, rows.Count);
            Assert.Single(rows.Where(l => !l.IsDeleted));
            Assert.Equal(scenario.FirstOrderId, rows.Single(l => l.IsDeleted).SourceId);
            await AssertActiveSingleLineAsync(statementId, scenario.SecondOrderId, 999m,
                AgencyServiceFeeStatementRules.StatusRecorded, header.RecordedAt, "并发登记人");
        }
        else
        {
            // 串行顺序 登记 → 替换：替换被受控拒绝，零字段变更、零残留行。
            Assert.Equal(ErrorCodes.RuleConflict, replace.Code);
            Assert.Contains("已登记", replace.Error);
            Assert.Equal(100m, header.TotalAmount);
            Assert.Single(rows);
            await AssertActiveSingleLineAsync(statementId, scenario.FirstOrderId, 100m,
                AgencyServiceFeeStatementRules.StatusRecorded, header.RecordedAt, "并发登记人");
        }

        await AssertFrozenConsistencyAsync(statementId);
        Assert.Equal(1, await CountStatementsAsync(scenario.CustomerId, statementNo));
    }

    /// <summary>
    /// 竞态②：<b>草稿整行替换 vs 作废</b>（两条独立连接 + 门闩对齐起跑）。
    /// 作废在草稿与已登记状态都只允许一次，因此必须成功；替换要么在作废前落库（冻结合计 999、两行痕迹）、
    /// 要么在作废后被「已作废」受控拒绝（合计 100、单行）。表头与全部有效行的作废状态 / 原因 / 时间戳
    /// 必须成对一致，绝无部分作废或混合行集合。
    /// </summary>
    [Fact]
    public async Task Race_draft_line_replacement_versus_void_is_serialized_without_mixed_evidence()
    {
        Guard();
        var scenario = await SeedScenarioAsync("R2");
        var operatorId = await SeedPrivilegedOperatorAsync();
        var statementNo = $"ASFS-520-R2-{Tag()}";
        const string voidReason = "并发作废原因";
        var statementId = await CreateDraftAsync(operatorId, SalesOrderSave(
            scenario.CustomerId, scenario.AgreementId, scenario.FirstOrderId, statementNo, amount: 100m));

        var outcomes = await RaceAsync(
            () => TryUpdateAsync(operatorId, statementId, SalesOrderSave(
                scenario.CustomerId, scenario.AgreementId, scenario.SecondOrderId, statementNo, amount: 999m)),
            () => TryVoidAsync(operatorId, statementId, voidReason));

        var replace = outcomes[0];
        var voided = outcomes[1];
        Assert.True(voided.Success, $"作废必须成功（草稿与已登记都只允许作废一次）：{voided.Type} {voided.Error}");

        var header = await ReloadAsync(statementId);
        var rows = await AllLinesAsync(statementId);
        Assert.Equal(AgencyServiceFeeStatementRules.StatusVoided, header.Status);
        Assert.Equal(voidReason, header.VoidReason);
        Assert.NotNull(header.VoidedAt);

        if (replace.Success)
        {
            Assert.Equal(999m, header.TotalAmount);
            Assert.Equal(2, rows.Count);
            Assert.Single(rows.Where(l => !l.IsDeleted));
            Assert.Equal(scenario.FirstOrderId, rows.Single(l => l.IsDeleted).SourceId);
        }
        else
        {
            Assert.Equal(ErrorCodes.RuleConflict, replace.Code);
            Assert.Contains("已作废", replace.Error);
            Assert.Equal(100m, header.TotalAmount);
            Assert.Single(rows);
        }

        var active = await ActiveLinesAsync(statementId);
        var line = Assert.Single(active);
        Assert.Equal(replace.Success ? scenario.SecondOrderId : scenario.FirstOrderId, line.SourceId);
        Assert.Equal(replace.Success ? 999m : 100m, line.Amount);
        Assert.Equal(AgencyServiceFeeStatementRules.StatusVoided, line.Status);
        Assert.Equal(voidReason, line.VoidReason);
        Assert.Equal(header.VoidedAt, line.VoidedAt);
        Assert.Equal(header.TotalAmount, active.Sum(l => l.Amount));
        Assert.Equal(1, await CountStatementsAsync(scenario.CustomerId, statementNo));
    }

    /// <summary>
    /// 竞态③：<b>并发重复登记</b>（两条独立连接 + 门闩对齐起跑）。恰好一个赢家，输家在锁内读到「已登记」
    /// 被受控拒绝，绝不覆盖赢家已写入的登记人 / 登记时间；冻结后表头与全部行的登记审计成对一致。
    /// </summary>
    [Fact]
    public async Task Race_duplicate_record_has_exactly_one_winner_and_never_overwrites_winner_audit()
    {
        Guard();
        var scenario = await SeedScenarioAsync("R3");
        var operatorId = await SeedPrivilegedOperatorAsync();
        var statementNo = $"ASFS-520-R3-{Tag()}";
        var statementId = await CreateDraftAsync(operatorId, SalesOrderSave(
            scenario.CustomerId, scenario.AgreementId, scenario.FirstOrderId, statementNo, amount: 100m));

        var outcomes = await RaceAsync(
            () => TryRecordAsync(operatorId, "并发登记甲", statementId),
            () => TryRecordAsync(operatorId, "并发登记乙", statementId));

        var winners = outcomes.Where(o => o.Success).ToList();
        var losers = outcomes.Where(o => !o.Success).ToList();
        Assert.Single(winners);
        Assert.Equal(ErrorCodes.RuleConflict, Assert.Single(losers).Code);
        Assert.Contains("已登记", losers[0].Error);
        Assert.Contains("不能重复登记", losers[0].Error);

        var winner = outcomes[0].Success ? "并发登记甲" : "并发登记乙";
        var header = await ReloadAsync(statementId);
        Assert.Equal(AgencyServiceFeeStatementRules.StatusRecorded, header.Status);
        Assert.Equal(winner, header.RecordedBy);
        Assert.NotNull(header.RecordedAt);
        await AssertActiveSingleLineAsync(statementId, scenario.FirstOrderId, 100m,
            AgencyServiceFeeStatementRules.StatusRecorded, header.RecordedAt, winner);
        await AssertFrozenConsistencyAsync(statementId);
        Assert.Equal(1, await CountStatementsAsync(scenario.CustomerId, statementNo));
    }

    /// <summary>
    /// 竞态④：<b>登记 vs 作废</b>（两条独立连接 + 门闩对齐起跑）。作废必须成功；若登记先赢，则作废保留
    /// 赢家写入的登记审计（不回填、不清空）；若作废先赢，则登记读到「已作废」被受控拒绝且不写入任何登记审计。
    /// 两种串行结果下表头 / 全部有效行的状态、原因与时间戳都必须一致。
    /// </summary>
    [Fact]
    public async Task Race_record_versus_void_is_serialized_and_keeps_winner_record_audit_inside_void_evidence()
    {
        Guard();
        var scenario = await SeedScenarioAsync("R4");
        var operatorId = await SeedPrivilegedOperatorAsync();
        var statementNo = $"ASFS-520-R4-{Tag()}";
        const string voidReason = "并发作废原因";
        var statementId = await CreateDraftAsync(operatorId, SalesOrderSave(
            scenario.CustomerId, scenario.AgreementId, scenario.FirstOrderId, statementNo, amount: 100m));

        var outcomes = await RaceAsync(
            () => TryRecordAsync(operatorId, "并发登记人", statementId),
            () => TryVoidAsync(operatorId, statementId, voidReason));

        var record = outcomes[0];
        var voided = outcomes[1];
        Assert.True(voided.Success, $"作废必须成功：{voided.Type} {voided.Error}");

        var header = await ReloadAsync(statementId);
        var line = Assert.Single(await ActiveLinesAsync(statementId));
        Assert.Equal(AgencyServiceFeeStatementRules.StatusVoided, header.Status);
        Assert.Equal(voidReason, header.VoidReason);
        Assert.NotNull(header.VoidedAt);
        Assert.Equal(AgencyServiceFeeStatementRules.StatusVoided, line.Status);
        Assert.Equal(voidReason, line.VoidReason);
        Assert.Equal(header.VoidedAt, line.VoidedAt);
        Assert.Equal(100m, header.TotalAmount);

        if (record.Success)
        {
            Assert.Equal("并发登记人", header.RecordedBy);
            Assert.NotNull(header.RecordedAt);
            Assert.Equal(header.RecordedAt, line.RecordedAt);
            Assert.Equal("并发登记人", line.RecordedBy);
        }
        else
        {
            Assert.Equal(ErrorCodes.RuleConflict, record.Code);
            Assert.Contains("已作废", record.Error);
            Assert.Equal(string.Empty, header.RecordedBy);
            Assert.Null(header.RecordedAt);
            Assert.Null(line.RecordedAt);
            Assert.Equal(string.Empty, line.RecordedBy);
        }

        // 登记审计永远成对出现：绝不出现「有登记时间但无登记人」的混合留痕。
        Assert.Equal(header.RecordedAt is null, string.IsNullOrEmpty(header.RecordedBy));
        Assert.Single(await AllLinesAsync(statementId));
        Assert.Equal(1, await CountStatementsAsync(scenario.CustomerId, statementNo));
    }

    // ==================== 2. 多行迟到失效与授权收敛 ====================

    /// <summary>
    /// 多行对账单登记前的**迟到失效**（真实 SQL + 另一条独立连接提交来源取消）：
    /// 登记必须在冻结前按登记口径复校已持久化表头与全部行，任一行来源已取消即整体回滚——
    /// 表头保持草稿、合计与全部行状态 / 登记审计逐字段不变，且零残留行 / 零软删除。
    /// </summary>
    [Fact]
    public async Task Record_with_a_cancelled_source_line_in_multi_line_statement_rolls_back_with_zero_partial_freeze()
    {
        Guard();
        var scenario = await SeedScenarioAsync("R5");
        var operatorId = await SeedPrivilegedOperatorAsync();
        var statementNo = $"ASFS-520-R5-{Tag()}";
        var statementId = await CreateDraftAsync(operatorId, SaveDto(
            scenario.CustomerId, scenario.AgreementId,
            new[]
            {
                LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, scenario.FirstOrderId, 100m),
                LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, scenario.SecondOrderId, 250m)
            },
            statementNo));
        Assert.Equal(350m, (await ReloadAsync(statementId)).TotalAmount);

        await CancelOrderAsync(scenario.SecondOrderId);

        var before = await ReloadAsync(statementId);
        var record = await TryRecordAsync(operatorId, "失败登记人", statementId);
        Assert.False(record.Success, "来源已取消时必须拒绝冻结");
        Assert.Equal(ErrorCodes.RuleConflict, record.Code);
        Assert.Contains("已取消", record.Error);

        await AssertStatementUnchangedAsync(before, await ReloadAsync(statementId));
        var active = await ActiveLinesAsync(statementId);
        Assert.Equal(2, active.Count);
        Assert.All(active, l => Assert.Equal(AgencyServiceFeeStatementRules.StatusDraft, l.Status));
        Assert.All(active, l => Assert.Null(l.RecordedAt));
        Assert.All(active, l => Assert.Equal(string.Empty, l.RecordedBy));
        Assert.Equal(2, (await AllLinesAsync(statementId)).Count);   // 零部分写入：不多行、不软删除
        Assert.Equal(1, await CountStatementsAsync(scenario.CustomerId, statementNo));
    }

    /// <summary>
    /// 竞态⑤：<b>多行登记 vs 关键行来源被并发取消</b>（两条独立连接 + 门闩对齐起跑）。
    /// 只有两种一致结果：来源取消在登记复校之后提交 → 两行全部冻结（合计 350、登记审计成对）；
    /// 来源取消在复校之前提交 → 登记受控拒绝且**零部分冻结**（表头仍草稿、合计 350、两行仍草稿草删除）。
    /// 两个连接各自的动作都不因对方回滚 / 被拒而丢失。
    /// </summary>
    [Fact]
    public async Task Race_record_versus_source_cancellation_never_freezes_partial_or_stale_evidence()
    {
        Guard();
        var scenario = await SeedScenarioAsync("R6");
        var operatorId = await SeedPrivilegedOperatorAsync();
        var statementNo = $"ASFS-520-R6-{Tag()}";
        var statementId = await CreateDraftAsync(operatorId, SaveDto(
            scenario.CustomerId, scenario.AgreementId,
            new[]
            {
                LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, scenario.FirstOrderId, 100m),
                LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, scenario.SecondOrderId, 250m)
            },
            statementNo));

        var (barrier, release) = NewBarrier();
        var recordTask = Task.Run(async () =>
        {
            await barrier;
            return await TryRecordAsync(operatorId, "并发登记人", statementId);
        });
        var cancelTask = Task.Run(async () =>
        {
            await barrier;
            await CancelOrderAsync(scenario.SecondOrderId);
            return true;
        });
        release();

        var record = await recordTask;
        await cancelTask;

        var header = await ReloadAsync(statementId);
        var active = await ActiveLinesAsync(statementId);
        Assert.Equal(350m, header.TotalAmount);
        Assert.Equal(2, active.Count);
        Assert.Equal(2, (await AllLinesAsync(statementId)).Count);
        Assert.Equal(scenario.FirstOrderId, active.Single(l => l.SourceId == scenario.FirstOrderId).SourceId);

        if (record.Success)
        {
            Assert.Equal(AgencyServiceFeeStatementRules.StatusRecorded, header.Status);
            await AssertFrozenConsistencyAsync(statementId);
        }
        else
        {
            Assert.Equal(ErrorCodes.RuleConflict, record.Code);
            Assert.Contains("已取消", record.Error);
            await AssertDraftConsistencyAsync(statementId);
        }

        // 来源取消是另一条独立连接自己的提交：不因对账单被拒或成功而回滚 / 被改写。
        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Cancelled,
            (await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == scenario.SecondOrderId)).Status);
        Assert.Equal(1, await CountStatementsAsync(scenario.CustomerId, statementNo));
    }

    // ==================== 3. 授权 / 数据范围收敛（真实身份 + 既有菜单 + 权威客户归属） ====================

    /// <summary>
    /// 授权收敛（真实 SQL + 真实控制器授权管线）：草稿创建成功后，
    /// ① 客户归属改派他人（数据范围收敛）→ 修改 / 登记 / 作废一律返回同一条不披露存在性的受控错误且零字段变更；
    /// ② 既有「客户资料」菜单被撤销（授权收敛）→ 写路由在任何读取 / 写入之前 fail closed（权限不足）且零字段变更。
    /// 被拒请求绝不落任何行、不消耗任何来源、也不改写任何审计。
    /// </summary>
    [Fact]
    public async Task Scope_and_menu_revocation_after_draft_creation_fails_closed_with_zero_mutation()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customer = await SeedCustomerAsync(db, $"C-520-R7-{Tag()}");
        var agreementId = await SeedAgreementAsync(db, customer.Id, $"ASF-520-R7-{Tag()}");
        var orderId = await SeedOrderAsync(db, customer.Id, $"SO-520-R7-{Tag()}");
        var (operatorId, roleId) = await SeedOperatorAsync(db, inScopeCustomerId: customer.Id);
        var otherEmployeeId = await SeedOtherEmployeeAsync(db);
        var statementNo = $"ASFS-520-R7-{Tag()}";

        // 范围内：草稿创建成功（证明实时身份 + 菜单 + 权威客户范围口径真的生效）。
        var statementId = await CreateDraftAsync(operatorId, SalesOrderSave(
            customer.Id, agreementId, orderId, statementNo, amount: 100m));

        // ① 客户归属改派他人：数据范围立即收敛。
        var before = await ReloadAsync(statementId);
        customer.EmpId = otherEmployeeId;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var deniedUpdate = await TryUpdateAsync(operatorId, statementId, SalesOrderSave(
            customer.Id, agreementId, orderId, statementNo, amount: 999m));
        Assert.Equal(ErrorCodes.NotFound, deniedUpdate.Code);
        Assert.Equal(AgencyServiceFeeReconciliationRules.RegisterNotFoundText, deniedUpdate.Error);
        var deniedRecord = await TryRecordAsync(operatorId, "越范围登记人", statementId);
        Assert.Equal(ErrorCodes.NotFound, deniedRecord.Code);
        Assert.Equal(AgencyServiceFeeReconciliationRules.RegisterNotFoundText, deniedRecord.Error);
        var deniedVoid = await TryVoidAsync(operatorId, statementId, "越范围作废");
        Assert.Equal(ErrorCodes.NotFound, deniedVoid.Code);
        Assert.Equal(AgencyServiceFeeReconciliationRules.RegisterNotFoundText, deniedVoid.Error);

        await AssertStatementUnchangedAsync(before, await ReloadAsync(statementId));
        Assert.Single(await AllLinesAsync(statementId));
        Assert.Equal(1, await CountStatementsAsync(customer.Id, statementNo));

        // ② 既有菜单授权被撤销：写路由 fail closed（权限不足），仍然零字段变更。
        await using (var revoke = _fixture.CreateDbContext())
            await RevokeMenuAsync(revoke, roleId);

        Assert.Equal(ErrorCodes.Forbidden, (await TryUpdateAsync(operatorId, statementId, SalesOrderSave(
            customer.Id, agreementId, orderId, statementNo))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await TryRecordAsync(operatorId, "无菜单登记人", statementId)).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await TryVoidAsync(operatorId, statementId, "无菜单作废")).Code);

        await AssertStatementUnchangedAsync(before, await ReloadAsync(statementId));
        Assert.Single(await AllLinesAsync(statementId));
        Assert.Equal(1, await CountStatementsAsync(customer.Id, statementNo));
    }

    /// <summary>
    /// 竞态⑥：<b>登记 vs 客户归属被并发改派（数据范围撤销）</b>（两条独立连接 + 门闩对齐起跑）。
    /// 只有两种一致结果：登记在范围仍有效时先冻结（合计 100、登记审计成对、全部行已登记）；
    /// 范围先收敛则登记返回同一条不披露存在性的受控错误且**零部分冻结**（表头仍草稿、无登记审计）。
    /// 归属改派本身是既有的独立写入，不因对账单被拒 / 成功而回滚。
    /// </summary>
    [Fact]
    public async Task Race_record_versus_scope_revocation_stays_serialized_with_zero_partial_freeze()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customer = await SeedCustomerAsync(db, $"C-520-R8-{Tag()}");
        var agreementId = await SeedAgreementAsync(db, customer.Id, $"ASF-520-R8-{Tag()}");
        var orderId = await SeedOrderAsync(db, customer.Id, $"SO-520-R8-{Tag()}");
        var (operatorId, _) = await SeedOperatorAsync(db, inScopeCustomerId: customer.Id);
        var otherEmployeeId = await SeedOtherEmployeeAsync(db);
        var statementNo = $"ASFS-520-R8-{Tag()}";
        var statementId = await CreateDraftAsync(operatorId, SalesOrderSave(
            customer.Id, agreementId, orderId, statementNo, amount: 100m));

        var (barrier, release) = NewBarrier();
        var recordTask = Task.Run(async () =>
        {
            await barrier;
            return await TryRecordAsync(operatorId, "并发登记人", statementId);
        });
        var revokeTask = Task.Run(async () =>
        {
            await barrier;
            await ReassignCustomerAsync(customer.Id, otherEmployeeId);
            return true;
        });
        release();

        var record = await recordTask;
        await revokeTask;

        var header = await ReloadAsync(statementId);
        Assert.Equal(100m, header.TotalAmount);
        Assert.Single(await AllLinesAsync(statementId));

        if (record.Success)
        {
            Assert.Equal(AgencyServiceFeeStatementRules.StatusRecorded, header.Status);
            await AssertFrozenConsistencyAsync(statementId);
        }
        else
        {
            Assert.Equal(ErrorCodes.NotFound, record.Code);
            Assert.Equal(AgencyServiceFeeReconciliationRules.RegisterNotFoundText, record.Error);
            await AssertDraftConsistencyAsync(statementId);
        }

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(otherEmployeeId,
            (await verify.BaseCustomers.AsNoTracking().SingleAsync(c => c.Id == customer.Id)).EmpId);
        Assert.Equal(1, await CountStatementsAsync(customer.Id, statementNo));
    }

    // ==================== 4. 真实生命周期（新建 → 修改 → 登记 → 作废 → 历史读取） ====================

    /// <summary>
    /// 真实生命周期（真实 SQL + 真实控制器）：新建草稿 → 修改整行替换（旧行软删除、合计服务端重算）→
    /// 登记冻结（表头 + 全部行 + 登记人 / 时间戳）→ 作废（保留登记审计 / 原始行 / 来源快照，只追加作废留痕）→
    /// 历史读取仍可读且不再接收编辑；作废释放来源身份（同一来源可被新对账单显式引用），不回填、不删除历史证据。
    /// </summary>
    [Fact]
    public async Task Lifecycle_create_replace_record_void_keeps_history_and_releases_sources()
    {
        Guard();
        var scenario = await SeedScenarioAsync("L1");
        var operatorId = await SeedPrivilegedOperatorAsync();
        var statementNo = $"ASFS-520-L1-{Tag()}";

        var statementId = await CreateDraftAsync(operatorId, SalesOrderSave(
            scenario.CustomerId, scenario.AgreementId, scenario.FirstOrderId, statementNo, amount: 100m));

        // 修改：整行替换（旧行软删除 + 新行插入，合计在服务端按已校验行重算）。
        var update = await TryUpdateAsync(operatorId, statementId, SaveDto(
            scenario.CustomerId, scenario.AgreementId,
            new[] { LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, scenario.SecondOrderId, 260m) },
            statementNo));
        Assert.True(update.Success, $"{update.Type} {update.Error}");
        Assert.Equal(260m, (await ReloadAsync(statementId)).TotalAmount);
        Assert.Single(await ActiveLinesAsync(statementId));
        Assert.Equal(scenario.FirstOrderId, (await AllLinesAsync(statementId)).Single(l => l.IsDeleted).SourceId);

        // 登记：冻结表头与全部行，登记人按已认证身份写入。
        var record = await TryRecordAsync(operatorId, "生命周期登记人", statementId);
        Assert.True(record.Success, $"{record.Type} {record.Error}");
        var recorded = await ReloadAsync(statementId);
        Assert.Equal(AgencyServiceFeeStatementRules.StatusRecorded, recorded.Status);
        var recordedAt = recorded.RecordedAt;
        Assert.NotNull(recordedAt);
        await AssertFrozenConsistencyAsync(statementId);

        // 作废：保留登记审计 / 原始行 / 来源快照，只追加作废留痕（不物理删除、不回填）。
        var voided = await TryVoidAsync(operatorId, statementId, "服务范围调整");
        Assert.True(voided.Success, $"{voided.Type} {voided.Error}");
        var afterVoid = await ReloadAsync(statementId);
        Assert.Equal(AgencyServiceFeeStatementRules.StatusVoided, afterVoid.Status);
        Assert.Equal("服务范围调整", afterVoid.VoidReason);
        Assert.Equal(recordedAt, afterVoid.RecordedAt);
        Assert.Equal("生命周期登记人", afterVoid.RecordedBy);
        Assert.Equal(260m, afterVoid.TotalAmount);

        var finalLine = Assert.Single(await ActiveLinesAsync(statementId));
        Assert.Equal(scenario.SecondOrderId, finalLine.SourceId);
        Assert.Equal(260m, finalLine.Amount);
        Assert.Equal(AgencyServiceFeeStatementRules.StatusVoided, finalLine.Status);
        Assert.Equal("服务范围调整", finalLine.VoidReason);
        Assert.Equal(recordedAt, finalLine.RecordedAt);
        Assert.Equal(2, (await AllLinesAsync(statementId)).Count);   // 被替换的旧行仍保留（历史痕迹）

        // 已作废证据绝不接收迟到的草稿行替换。
        var lateEdit = await TryUpdateAsync(operatorId, statementId, SalesOrderSave(
            scenario.CustomerId, scenario.AgreementId, scenario.FirstOrderId, statementNo, amount: 500m));
        Assert.False(lateEdit.Success);
        Assert.Equal(ErrorCodes.RuleConflict, lateEdit.Code);
        Assert.Contains("已作废", lateEdit.Error);

        // 历史读取：原始行 / 来源快照 / 登记与作废留痕原样返回。
        var historical = await GetDtoAsync(operatorId, statementId);
        Assert.Equal(AgencyServiceFeeStatementRules.StatusVoided, historical.Status);
        Assert.True(historical.IsVoided);
        Assert.Equal("服务范围调整", historical.VoidReason);
        Assert.Equal(260m, historical.TotalAmount);
        Assert.Equal("生命周期登记人", historical.RecordedBy);
        Assert.Equal(recordedAt, historical.RecordedAt);
        var historicalLine = Assert.Single(historical.Lines);
        Assert.Equal(scenario.SecondOrderId, historicalLine.SourceId);
        Assert.Equal(260m, historicalLine.Amount);
        Assert.Equal(AgencyServiceFeeStatementRules.StatusVoided, historicalLine.Status);

        // 作废释放被占用的来源身份：同一来源可被新对账单显式引用（旧历史证据保持可读）。
        var secondNo = $"ASFS-520-L1B-{Tag()}";
        Assert.True(await CreateDraftAsync(operatorId, SalesOrderSave(
            scenario.CustomerId, scenario.AgreementId, scenario.SecondOrderId, secondNo, amount: 30m)) > 0);
        Assert.Equal(1, await CountStatementsAsync(scenario.CustomerId, statementNo));
        Assert.Equal(1, await CountStatementsAsync(scenario.CustomerId, secondNo));
    }

    // ==================== 5. 测试脚手架（独立连接 / 真实主数据 / 断言助手） ====================

    /// <summary>播种独立场景：权威客户 + 一条**已登记**协议证据 + 两张可显式引用的销售订单（SQL 自增主键）。</summary>
    private async Task<(long CustomerId, long AgreementId, long FirstOrderId, long SecondOrderId)>
        SeedScenarioAsync(string tag)
    {
        var suffix = $"{tag}-{Tag()}";
        await using var db = _fixture.CreateDbContext();
        var customer = await SeedCustomerAsync(db, $"C-520-{suffix}");
        var agreementId = await SeedAgreementAsync(db, customer.Id, $"ASF-520-{suffix}");
        var firstOrderId = await SeedOrderAsync(db, customer.Id, $"SO-520-{suffix}-1");
        var secondOrderId = await SeedOrderAsync(db, customer.Id, $"SO-520-{suffix}-2");
        return (customer.Id, agreementId, firstOrderId, secondOrderId);
    }

    /// <summary>播种特权操作账号（系统内置角色 → 数据范围不受限；复用既有「客户资料」菜单，不新增权限）。</summary>
    private async Task<long> SeedPrivilegedOperatorAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var (userId, _) = await SeedOperatorAsync(db, inScopeCustomerId: null, privileged: true);
        return userId;
    }

    /// <summary>播种一条既有员工（用于把客户归属改派给「他人」，从而收敛原账号的数据范围）。</summary>
    private static async Task<long> SeedOtherEmployeeAsync(ErpDbContext db)
    {
        var code = $"asf520-other-{Guid.NewGuid():N}";
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();
        return employee.Id;
    }

    /// <summary>用一条独立连接经真实控制器创建草稿对账单，并返回落库 Id。</summary>
    private async Task<long> CreateDraftAsync(long userId, AgencyServiceFeeStatementSaveDto dto)
    {
        await using var db = _fixture.CreateDbContext();
        var result = await NewController(db, userId, null).Create(dto);
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<AgencyServiceFeeStatementDto>>(ok.Value);
        Assert.NotNull(response.Data);
        Assert.Equal(AgencyServiceFeeStatementRules.StatusDraft, response.Data!.Status);
        return response.Data.Id;
    }

    /// <summary>用一条独立连接经真实控制器读取对账单详情（含行清单）。</summary>
    private async Task<AgencyServiceFeeStatementDto> GetDtoAsync(long userId, long statementId)
    {
        await using var db = _fixture.CreateDbContext();
        var result = await NewController(db, userId, null).GetById(statementId);
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<ApiResponse<AgencyServiceFeeStatementDto>>(ok.Value).Data!;
    }

    /// <summary>对账单**全部行**（含被替换软删除的行），用于断言零残留 / 零部分写入。</summary>
    private async Task<List<AgencyServiceFeeStatementLine>> AllLinesAsync(long statementId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.AgencyServiceFeeStatementLines.AsNoTracking()
            .Where(l => l.StatementId == statementId)
            .OrderBy(l => l.LineNo).ThenBy(l => l.Id)
            .ToListAsync();
    }

    /// <summary>在另一条独立连接上把客户归属改派给他人（既有的权威主数据写入，用于收敛数据范围）。</summary>
    private async Task ReassignCustomerAsync(long customerId, long employeeId)
    {
        await using var db = _fixture.CreateDbContext();
        var customer = await db.BaseCustomers.SingleAsync(c => c.Id == customerId);
        customer.EmpId = employeeId;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 双连接竞态的确定性门闩：返回「等待屏障 + 放行动作」，两条独立连接在自己的线程上等待同一屏障后同时起跑，
    /// 绝不使用时间睡眠来制造交错。
    /// </summary>
    private static (Task Barrier, Action Release) NewBarrier()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return (gate.Task, () => gate.TrySetResult(true));
    }

    /// <summary>恰好一条有效行且逐字段等于期望值（来源 / 金额 / 状态 / 登记审计；未作废）。</summary>
    private async Task AssertActiveSingleLineAsync(long statementId, long expectedSourceId, decimal expectedAmount,
        int expectedStatus, DateTime? expectedRecordedAt, string expectedRecordedBy)
    {
        var line = Assert.Single(await ActiveLinesAsync(statementId));
        Assert.Equal(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, line.SourceType);
        Assert.Equal(expectedSourceId, line.SourceId);
        Assert.Equal(expectedAmount, line.Amount);
        Assert.Equal(expectedStatus, line.Status);
        Assert.Equal(expectedRecordedAt, line.RecordedAt);
        Assert.Equal(expectedRecordedBy, line.RecordedBy);
        Assert.Null(line.VoidedAt);
        Assert.Equal(string.Empty, line.VoidReason);
    }

    /// <summary>已登记冻结的一致性：合计 = 有效行金额之和，表头与全部行的状态 / 登记审计成对一致。</summary>
    private async Task AssertFrozenConsistencyAsync(long statementId)
    {
        var header = await ReloadAsync(statementId);
        var active = await ActiveLinesAsync(statementId);
        Assert.Equal(AgencyServiceFeeStatementRules.StatusRecorded, header.Status);
        Assert.NotEmpty(active);
        Assert.NotNull(header.RecordedAt);
        Assert.NotEmpty(header.RecordedBy);
        Assert.Equal(header.TotalAmount, active.Sum(l => l.Amount));
        Assert.All(active, l => Assert.Equal(AgencyServiceFeeStatementRules.StatusRecorded, l.Status));
        Assert.All(active, l => Assert.Equal(header.RecordedAt, l.RecordedAt));
        Assert.All(active, l => Assert.Equal(header.RecordedBy, l.RecordedBy));
        Assert.All(active, l => Assert.Null(l.VoidedAt));
        Assert.All(active, l => Assert.Equal(string.Empty, l.VoidReason));
    }

    /// <summary>被拒请求的草稿口径：表头仍草稿、合计 = 有效行金额之和、全部行仍草稿且无登记 / 作废审计。</summary>
    private async Task AssertDraftConsistencyAsync(long statementId)
    {
        var header = await ReloadAsync(statementId);
        var active = await ActiveLinesAsync(statementId);
        Assert.Equal(AgencyServiceFeeStatementRules.StatusDraft, header.Status);
        Assert.Null(header.RecordedAt);
        Assert.Equal(string.Empty, header.RecordedBy);
        Assert.Null(header.VoidedAt);
        Assert.Equal(string.Empty, header.VoidReason);
        Assert.Equal(header.TotalAmount, active.Sum(l => l.Amount));
        Assert.All(active, l => Assert.Equal(AgencyServiceFeeStatementRules.StatusDraft, l.Status));
        Assert.All(active, l => Assert.Null(l.RecordedAt));
        Assert.All(active, l => Assert.Equal(string.Empty, l.RecordedBy));
        Assert.All(active, l => Assert.Null(l.VoidedAt));
        Assert.All(active, l => Assert.Equal(string.Empty, l.VoidReason));
    }
}



/// <summary>
/// ERP-520 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>
/// （默认 <c>NEWERP_AUTOTEST_ERP520_STATEMENTCONCURRENCY_&lt;guid&gt;</c>），发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class AgencyStatementConcurrencySqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP520_STATEMENTCONCURRENCY";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-520] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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
        Assert.Equal($"(localdb)\\{InstanceMarker}", builder.DataSource ?? string.Empty, ignoreCase: true);
        Assert.StartsWith(DatabasePrefix, builder.InitialCatalog ?? string.Empty, StringComparison.OrdinalIgnoreCase);
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

        Console.WriteLine("[ERP-520] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }
}


/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class AgencyStatementConcurrencyTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => AgencyStatementConcurrencySqlServerFixture.AssertDedicatedTarget(connection));

    [Fact]
    public void Accepts_the_dedicated_localdb_target_with_integrated_security()
        => AgencyStatementConcurrencySqlServerFixture.AssertDedicatedTarget(
            "Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_X;Integrated Security=true");
}

