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
/// ERP-520 代理服务费对账单证据「草稿行替换 / 登记冻结 / 作废」并发序列化单元测试（内存库 + 真实 HTTP 身份）：
/// <list type="number">
/// <item><b>护栏契约</b>：<see cref="AgencyFeeEvidenceConcurrencyRules"/> 的对账单行锁 / 确定性锁序 / 边界与
/// 受控错误文案同源；非关系型提供程序判定为「无事务无行锁」（对账单行锁帮助方法等价无操作）；</item>
/// <item><b>生命周期串行化</b>：已登记 / 已作废证据绝不接收迟到的草稿行替换；重复登记 / 重复作废只有唯一赢家，
/// 输家绝不覆盖赢家已写入的登记人 / 时间戳 / 原始作废原因；被拒请求逐字段（含状态与全部审计）与行集合不变；</item>
/// <item><b>冻结前权威复校（多行迟到失效）</b>：登记时若客户停用 / 协议已作废 / 任一行来源已取消，
/// 表头与全部行零部分冻结（合计、状态、时间戳、行状态全部保持草稿口径）；</item>
/// <item><b>权威状态与客户范围</b>：锁内重读的权威行才是资格与范围的唯一依据，越范围 / 菜单被撤销后
/// 修改、登记、作废一律 fail closed 且零字段变更；</item>
/// <item><b>源码契约</b>：创建 / 修改 / 登记 / 作废四条写路由都在同一事务 + 同一对账单行锁内，
/// 控制器每条写路由仍先解析实时身份 / 菜单 / 客户范围。</item>
/// </list>
/// 全部使用内存库（<see cref="TestDbFactory"/>）+ SQL Server 提供程序**仅构建模型**（不打开连接）；
/// 不连接 SQL Server、不执行 DDL / 部署脚本，也不做浏览器 / UI 验收。
/// </summary>
public class AgencyStatementConcurrencyTests
{
    // ==================== 1. 护栏契约（纯规则） ====================

    [Fact]
    public void 并发护栏常量与锁序边界文案同源()
    {
        // ERP-519 / ERP-520 唯一确定性锁序：协议行 → 对账单行，绝不反向获取。
        Assert.Contains("ERP-520", AgencyFeeEvidenceConcurrencyRules.GlobalLockOrderText);
        Assert.Contains("AgencyServiceFeeStatements", AgencyFeeEvidenceConcurrencyRules.GlobalLockOrderText);
        Assert.Contains("AgencyServiceFeeAgreements", AgencyFeeEvidenceConcurrencyRules.GlobalLockOrderText);
        Assert.Contains("绝不反向获取", AgencyFeeEvidenceConcurrencyRules.GlobalLockOrderText);

        Assert.Contains("UPDLOCK, HOLDLOCK", AgencyFeeEvidenceConcurrencyRules.StatementRowLockSql);
        Assert.Contains("AgencyServiceFeeStatements", AgencyFeeEvidenceConcurrencyRules.StatementRowLockSql);
        Assert.Contains("UPDLOCK, HOLDLOCK", AgencyFeeEvidenceConcurrencyRules.AgreementRowLockSql);

        // 受控错误文案不得含数据库内部细节。
        Assert.DoesNotContain("SqlException", AgencyFeeEvidenceConcurrencyRules.StatementActiveEvidenceConflictText);
        Assert.DoesNotContain("UNIQUE", AgencyFeeEvidenceConcurrencyRules.StatementActiveEvidenceConflictText);
        Assert.DoesNotContain("UX_", AgencyFeeEvidenceConcurrencyRules.StatementActiveEvidenceConflictText);
        Assert.Contains("对账单", AgencyFeeEvidenceConcurrencyRules.StatementActiveEvidenceConflictText);
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
                + "Database=NEWERP_AUTOTEST_ERP520_MODEL_ONLY;Integrated Security=true;TrustServerCertificate=true")
            .Options;
        using var relational = new ErpDbContext(options);
        Assert.True(AgencyFeeEvidenceConcurrencyRules.IsRelationalProvider(relational));
    }

    [Fact]
    public async Task 非关系型_对账单行锁等价无操作_且受控写入可用()
    {
        using var db = TestDbFactory.Create();

        // 内存库没有行锁语义：对账单行锁帮助方法等价无操作（返回 null → 调用方按既有装载口径处理）。
        Assert.Null(await AgencyFeeEvidenceConcurrencyRules.LockStatementRowAsync(db, 123));
        Assert.Null(await AgencyFeeEvidenceConcurrencyRules.LockStatementRowAsync(db, 0));

        // 无变更时受控写入等价普通 SaveChanges（不抛错、不误报重复）。
        await AgencyFeeEvidenceConcurrencyRules.SaveStatementEvidenceAsync(db);
    }

    [Fact]
    public void 唯一索引冲突识别_对账单口径不误报非唯一冲突()
    {
        Assert.True(AgencyFeeEvidenceConcurrencyRules.LooksLikeUniqueViolation(
            new DbUpdateException("Cannot insert duplicate key row (2601)")));
        Assert.False(AgencyFeeEvidenceConcurrencyRules.LooksLikeUniqueViolation(
            new DbUpdateException("Invalid object name 'db_owner.AgencyServiceFeeStatements'")));
    }

    // ==================== 2. 生命周期串行化（内存库 + 真实 HTTP 身份） ====================

    [Fact]
    public async Task 已登记对账单_拒绝迟到的草稿行替换_且表头与行集合逐字段不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-520-A", "并发客户A");
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var order = SeedOrder(db, "SO-520-A", customer.Id);
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeStatementDto>(await controller.Create(
            SalesOrderSave(customer.Id, agreement.Id, order.Id, amount: 100m, statementNo: "ASFS-520-A")));
        var recorded = AssertOk<AgencyServiceFeeStatementDto>(await controller.Record(created.Id));
        Assert.Equal(AgencyServiceFeeStatementRules.StatusRecorded, recorded.Status);

        var before = Reload(db, created.Id);
        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Update(
            created.Id, SalesOrderSave(customer.Id, agreement.Id, order.Id, amount: 999m, statementNo: "ASFS-520-A")));

        Assert.Contains("已登记", ex.Message);
        var after = Reload(db, created.Id);
        AssertUnchanged(before, after);
        Assert.Equal(100m, after.TotalAmount);
        Assert.Single(ActiveLines(db, created.Id));                 // 没有多出草稿行
        Assert.Equal(100m, ActiveLines(db, created.Id)[0].Amount);  // 原行金额未被改写
        Assert.Equal(AgencyServiceFeeStatementRules.StatusRecorded, ActiveLines(db, created.Id)[0].Status);
    }

    [Fact]
    public async Task 已作废对账单_拒绝重复登记与重复作废与迟到修改_原始留痕保留()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-520-B", "并发客户B");
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var order = SeedOrder(db, "SO-520-B", customer.Id);
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeStatementDto>(await controller.Create(
            SalesOrderSave(customer.Id, agreement.Id, order.Id, amount: 100m, statementNo: "ASFS-520-B")));
        AssertOk<AgencyServiceFeeStatementDto>(await controller.Record(created.Id));
        var voided = AssertOk<AgencyServiceFeeStatementDto>(await controller.Void(
            created.Id, new AgencyServiceFeeStatementVoidRequest { Reason = "对账口径更正" }));
        Assert.Equal(AgencyServiceFeeStatementRules.StatusVoided, voided.Status);

        var before = Reload(db, created.Id);
        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Update(
            created.Id, SalesOrderSave(customer.Id, agreement.Id, order.Id, amount: 888m, statementNo: "ASFS-520-B")));
        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Record(created.Id));
        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Void(
            created.Id, new AgencyServiceFeeStatementVoidRequest { Reason = "再次作废" }));

        var after = Reload(db, created.Id);
        AssertUnchanged(before, after);
        Assert.Equal("对账口径更正", after.VoidReason);          // 原始作废原因被保留
        Assert.NotNull(after.RecordedAt);                        // 作废不改写登记时间
        Assert.Single(ActiveLines(db, created.Id));              // 不新增影子行、不物理删除
        Assert.Equal(AgencyServiceFeeStatementRules.StatusVoided, ActiveLines(db, created.Id)[0].Status);
        Assert.Equal("对账口径更正", ActiveLines(db, created.Id)[0].VoidReason);
    }

    [Fact]
    public async Task 重复登记_恰好一个赢家_输家不覆盖登记人与时间戳()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-520-C", "并发客户C");
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var order = SeedOrder(db, "SO-520-C", customer.Id);
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeStatementDto>(await controller.Create(
            SalesOrderSave(customer.Id, agreement.Id, order.Id, statementNo: "ASFS-520-C")));

        var winner = await AgencyServiceFeeStatementService.RecordAsync(db, created.Id, "赢家登记人");
        Assert.Equal("赢家登记人", winner.RecordedBy);

        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => AgencyServiceFeeStatementService.RecordAsync(db, created.Id, "迟到登记人"));
        Assert.Contains("已登记", ex.Message);

        var after = Reload(db, created.Id);
        Assert.Equal("赢家登记人", after.RecordedBy);
        Assert.Equal(winner.RecordedAt, after.RecordedAt);
        Assert.Equal(AgencyServiceFeeStatementRules.StatusRecorded, after.Status);
    }

    [Fact]
    public async Task 登记冻结_合计等于服务端重算的全部行金额之和_且行状态与表头一致()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-520-D", "并发客户D");
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var first = SeedOrder(db, "SO-520-D1", customer.Id);
        var second = SeedOrder(db, "SO-520-D2", customer.Id);
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeStatementDto>(await controller.Create(SaveDto(
            customer.Id, agreement.Id,
            new[]
            {
                LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, first.Id, amount: 100m),
                LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, second.Id, amount: 200.50m)
            },
            statementNo: "ASFS-520-D")));

        var recorded = await AgencyServiceFeeStatementService.RecordAsync(db, created.Id, "服务端登记人");

        Assert.Equal(300.50m, recorded.TotalAmount);
        Assert.Equal(AgencyServiceFeeStatementRules.StatusRecorded, recorded.Status);
        Assert.Equal("服务端登记人", recorded.RecordedBy);
        Assert.NotNull(recorded.RecordedAt);
        Assert.All(recorded.Lines, l => Assert.Equal(AgencyServiceFeeStatementRules.StatusRecorded, l.Status));
        Assert.All(recorded.Lines, l => Assert.Equal(recorded.RecordedAt, l.RecordedAt));
        Assert.Equal(300.50m, Reload(db, created.Id).TotalAmount);
    }

    // ==================== 3. 冻结前权威复校（迟到失效 → 零部分冻结） ====================

    [Fact]
    public async Task 登记时_多行中有一行来源已取消_整体回滚_表头与全部行零部分冻结()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-520-E", "并发客户E");
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var first = SeedOrder(db, "SO-520-E1", customer.Id);
        var second = SeedOrder(db, "SO-520-E2", customer.Id);
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeStatementDto>(await controller.Create(SaveDto(
            customer.Id, agreement.Id,
            new[]
            {
                LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, first.Id, amount: 100m),
                LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, second.Id, amount: 250m)
            },
            statementNo: "ASFS-520-E")));
        Assert.Equal(350m, created.TotalAmount);

        // 第二行来源在对账单登记之后被取消：登记冻结必须整体失败，绝不冻结过期 / 失效证据。
        (await db.SalesOrders.FirstAsync(o => o.Id == second.Id)).Status = DocumentStatus.Cancelled;
        await db.SaveChangesAsync();

        var beforeHeader = Reload(db, created.Id);
        var beforeLines = ActiveLines(db, created.Id);

        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => AgencyServiceFeeStatementService.RecordAsync(db, created.Id, "失败登记人"));
        Assert.Contains("已取消", ex.Message);

        var afterHeader = Reload(db, created.Id);
        AssertUnchanged(beforeHeader, afterHeader);
        Assert.Equal(AgencyServiceFeeStatementRules.StatusDraft, afterHeader.Status);
        Assert.Null(afterHeader.RecordedAt);
        Assert.Equal(string.Empty, afterHeader.RecordedBy);
        Assert.Equal(350m, afterHeader.TotalAmount);

        var afterLines = ActiveLines(db, created.Id);
        AssertLinesUnchanged(beforeLines, afterLines);
        Assert.All(afterLines, l => Assert.Equal(AgencyServiceFeeStatementRules.StatusDraft, l.Status));
        Assert.All(afterLines, l => Assert.Null(l.RecordedAt));
        Assert.All(afterLines, l => Assert.False(l.IsDeleted));
    }

    [Fact]
    public async Task 登记时_客户已停用_整体回滚_零部分冻结()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-520-F", "并发客户F");
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var order = SeedOrder(db, "SO-520-F", customer.Id);
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeStatementDto>(await controller.Create(
            SalesOrderSave(customer.Id, agreement.Id, order.Id, statementNo: "ASFS-520-F")));

        (await db.BaseCustomers.FirstAsync(c => c.Id == customer.Id)).Status = 0;
        await db.SaveChangesAsync();

        var before = Reload(db, created.Id);
        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => AgencyServiceFeeStatementService.RecordAsync(db, created.Id, "失败登记人"));
        Assert.Contains("停用", ex.Message);
        AssertUnchanged(before, Reload(db, created.Id));
        Assert.Equal(AgencyServiceFeeStatementRules.StatusDraft, Reload(db, created.Id).Status);
    }

    [Fact]
    public async Task 登记时_关联协议已作废_整体回滚_零部分冻结()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-520-G", "并发客户G");
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var order = SeedOrder(db, "SO-520-G", customer.Id);
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeStatementDto>(await controller.Create(
            SalesOrderSave(customer.Id, agreement.Id, order.Id, statementNo: "ASFS-520-G")));

        await AgencyServiceFeeAgreementService.VoidAsync(db, agreement.Id, "协议终止");

        var before = Reload(db, created.Id);
        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => AgencyServiceFeeStatementService.RecordAsync(db, created.Id, "失败登记人"));
        Assert.Contains("协议", ex.Message);
        AssertUnchanged(before, Reload(db, created.Id));
        Assert.Equal(AgencyServiceFeeStatementRules.StatusDraft, Reload(db, created.Id).Status);
    }

    // ==================== 4. 权威客户范围（锁内重新施加） ====================

    [Fact]
    public async Task 越范围对账单_修改登记作废一律拒绝且零变更()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db, "C-520-OWN", "本人客户");
        var foreign = SeedCustomer(db, "C-520-FGN", "他人客户");
        var agreement = await SeedRecordedAgreementAsync(db, foreign.Id, agreementNo: "ASF-520-FGN");
        var order = SeedOrder(db, "SO-520-FGN", foreign.Id);
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeStatementDto>(await controller.Create(
            SalesOrderSave(foreign.Id, agreement.Id, order.Id, statementNo: "ASFS-520-FGN")));

        // 受限业务员的范围只含本人客户：越范围 / 已删除 / 不存在统一受控不披露存在性。
        var restricted = new SalespersonDataScope
        {
            IsPrivileged = false,
            SalesmanId = 42,
            AllowedCustomerIds = new HashSet<long> { own.Id }
        };

        var before = Reload(db, created.Id);
        var updateEx = await AssertBusinessAsync(ErrorCodes.NotFound,
            () => AgencyServiceFeeStatementService.UpdateAsync(
                db, created.Id,
                SalesOrderSave(foreign.Id, agreement.Id, order.Id, statementNo: "ASFS-520-FGN"),
                restricted));
        Assert.Equal(AgencyServiceFeeReconciliationRules.RegisterNotFoundText, updateEx.Message);
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => AgencyServiceFeeStatementService.RecordAsync(db, created.Id, "受限登记人", restricted));
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => AgencyServiceFeeStatementService.VoidAsync(db, created.Id, "受限作废", restricted));

        var after = Reload(db, created.Id);
        AssertUnchanged(before, after);
        Assert.Equal(AgencyServiceFeeStatementRules.StatusDraft, after.Status);
    }

    [Fact]
    public async Task 范围内对账单_使用受限范围可正常登记_保留权威客户与协议快照()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-520-IN", "范围内客户");
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var order = SeedOrder(db, "SO-520-IN", customer.Id);
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeStatementDto>(await controller.Create(
            SalesOrderSave(customer.Id, agreement.Id, order.Id, statementNo: "ASFS-520-IN")));

        var scope = new SalespersonDataScope
        {
            IsPrivileged = false,
            SalesmanId = 7,
            AllowedCustomerIds = new HashSet<long> { customer.Id }
        };

        var recorded = await AgencyServiceFeeStatementService.RecordAsync(db, created.Id, "范围内登记人", scope);
        Assert.Equal(AgencyServiceFeeStatementRules.StatusRecorded, recorded.Status);
        Assert.Equal("范围内登记人", recorded.RecordedBy);
        Assert.Equal(customer.CustomerCode, recorded.CustomerCode);
        Assert.Equal(customer.CustomerName, recorded.CustomerName);

        var voided = await AgencyServiceFeeStatementService.VoidAsync(db, created.Id, "范围内作废", scope);
        Assert.Equal(AgencyServiceFeeStatementRules.StatusVoided, voided.Status);
        Assert.Equal(customer.CustomerName, voided.CustomerName);
    }

    [Fact]
    public async Task 菜单被撤销后_修改登记作废一律fail_closed_零变更()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-520-MENU", "菜单撤销客户");
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var order = SeedOrder(db, "SO-520-MENU", customer.Id);
        var userId = SeedPrivilegedUser(db);
        var controller = BuildControllerWithUser(db, userId);

        var created = AssertOk<AgencyServiceFeeStatementDto>(await controller.Create(
            SalesOrderSave(customer.Id, agreement.Id, order.Id, statementNo: "ASFS-520-MENU")));
        var before = Reload(db, created.Id);

        RevokeMenus(db, userId);

        await AssertBusinessAsync(ErrorCodes.Forbidden, () => controller.Update(
            created.Id, SalesOrderSave(customer.Id, agreement.Id, order.Id, statementNo: "ASFS-520-MENU")));
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => controller.Record(created.Id));
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => controller.Void(
            created.Id, new AgencyServiceFeeStatementVoidRequest { Reason = "撤销后作废" }));

        AssertUnchanged(before, Reload(db, created.Id));
        Assert.Equal(AgencyServiceFeeStatementRules.StatusDraft, Reload(db, created.Id).Status);
    }

    [Fact]
    public async Task 生命周期_创建_修改_登记_作废_历史读取与作废证据保留()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-520-L", "并发客户L");
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var first = SeedOrder(db, "SO-520-L1", customer.Id);
        var second = SeedOrder(db, "SO-520-L2", customer.Id);
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeStatementDto>(await controller.Create(
            SalesOrderSave(customer.Id, agreement.Id, first.Id, amount: 100m, statementNo: "ASFS-520-L")));

        var updated = AssertOk<AgencyServiceFeeStatementDto>(await controller.Update(created.Id, SaveDto(
            customer.Id, agreement.Id,
            new[] { LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, second.Id, amount: 260m) },
            statementNo: "ASFS-520-L")));
        Assert.Equal(260m, updated.TotalAmount);
        Assert.Single(ActiveLines(db, created.Id));                       // 旧草稿行被软删除、只剩新行
        Assert.Equal(second.Id, ActiveLines(db, created.Id)[0].SourceId);

        var recorded = AssertOk<AgencyServiceFeeStatementDto>(await controller.Record(created.Id));
        var voided = AssertOk<AgencyServiceFeeStatementDto>(await controller.Void(
            created.Id, new AgencyServiceFeeStatementVoidRequest { Reason = "服务范围调整" }));
        Assert.Equal(AgencyServiceFeeStatementRules.StatusVoided, voided.Status);
        Assert.Equal(recorded.RecordedAt, voided.RecordedAt);             // 作废不改写登记时间
        Assert.Equal(recorded.RecordedBy, voided.RecordedBy);             // 作废不改写登记人

        var historical = AssertOk<AgencyServiceFeeStatementDto>(await controller.GetById(created.Id));
        Assert.Equal(AgencyServiceFeeStatementRules.StatusVoided, historical.Status);
        Assert.Equal(260m, historical.TotalAmount);                      // 原始合计保留可读
        Assert.Equal("服务范围调整", historical.VoidReason);
        Assert.Single(db.AgencyServiceFeeStatements);                    // 不物理删除、也不新增影子记录
    }

    // ==================== 5. 源码契约（写路由共用同一事务 + 同一对账单行锁） ====================

    [Fact]
    public void 对账单写路由_共用同一事务与同一对账单行锁_并保留既有锁内校验顺序()
    {
        var service = File.ReadAllText(RepoFile("src", "ERP.Application", "Services",
            "AgencyServiceFeeStatementService.cs"));

        // 四条写路由都经由共用护栏：同一事务 + 同一对账单行锁 + 受控写入 + 拒绝清理。
        Assert.Contains("AgencyFeeEvidenceConcurrencyRules.BeginMutationTransactionAsync", service);
        Assert.Contains("AgencyFeeEvidenceConcurrencyRules.LockStatementRowAsync", service);
        Assert.Contains("AgencyFeeEvidenceConcurrencyRules.SaveStatementEvidenceAsync", service);
        Assert.Contains("AgencyFeeEvidenceConcurrencyRules.TryRollbackAsync", service);
        Assert.Contains("AgencyFeeEvidenceConcurrencyRules.CommitAsync", service);
        Assert.Contains("LockStatementAuthoritativeAsync(db, statementId)", service);
        Assert.Contains("EnsureAuthoritativeScope(scope, statement)", service);
        Assert.Contains("BuildPersistedSaveDto(statement, lines)", service);

        // 旧路径「先校验 / 先读行、后开事务」必须消失（写完再开事务的陈旧口径不再存在）。
        Assert.DoesNotContain("db.Database.BeginTransactionAsync", service);

        // 修改核心：先取锁、再校验（锁内权威状态与范围才是唯一依据）。
        var lockIndex = service.IndexOf(
            "statement = await LockStatementAuthoritativeAsync(db, statementId);", StringComparison.Ordinal);
        Assert.True(lockIndex >= 0);
        var validateIndex = service.IndexOf(
            "var input = await ValidateAsync(db, dto);", lockIndex, StringComparison.Ordinal);
        Assert.True(validateIndex > lockIndex);

        // 创建核心：事务先于校验 / 写入（被拒请求零写入）。
        var txIndex = service.IndexOf(
            "AgencyFeeEvidenceConcurrencyRules.BeginMutationTransactionAsync", StringComparison.Ordinal);
        var createValidateIndex = service.IndexOf(
            "var input = await ValidateAsync(db, dto);", StringComparison.Ordinal);
        Assert.True(txIndex >= 0 && createValidateIndex > txIndex);
    }

    [Fact]
    public void 控制器写路由_仍先做实时身份菜单与客户范围授权()
    {
        var controller = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers",
            "AgencyServiceFeeStatementController.cs"));

        var authorizations = Regex.Matches(controller,
            "EnsureRegisterAuthorizedAsync\\(_db, CurrentUserId\\(\\)\\)").Count;
        Assert.Equal(7, authorizations);   // 台账 / 来源候选 / 月度汇总 / 详情 / 新增 / 修改 / 登记 / 作废

        Assert.Contains("AgencyServiceFeeStatementService.CreateAsync(_db, dto, scope)", controller);
        Assert.Contains("AgencyServiceFeeStatementService.UpdateAsync(_db, id, dto, scope)", controller);
        Assert.Contains("AgencyServiceFeeStatementService.RecordAsync(_db, id, CurrentUserName(), scope)", controller);
        Assert.Contains("AgencyServiceFeeStatementService.VoidAsync(_db, id, request?.Reason, scope)", controller);
        Assert.DoesNotContain("HttpContext.Items", controller);
    }

    // ==================== 6. 测试脚手架 ====================

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

    /// <summary>播种具备既有「客户资料」菜单的特权账号（系统内置角色 → 数据范围不受限）</summary>
    private static long SeedPrivilegedUser(ErpDbContext db)
    {
        var role = new SysRole
        {
            RoleName = "代理服务费对账单并发特权角色",
            RoleCode = $"AsfStmtConcPriv-{Guid.NewGuid():N}",
            IsSystem = true
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"asf-stmt-conc-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "代理服务费对账单并发操作员",
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

    /// <summary>撤销指定账号全部既有菜单授权（含 customer），用于验证「授权撤销后立即收敛」</summary>
    private static void RevokeMenus(ErpDbContext db, long userId)
    {
        var roleIds = db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && !ur.IsDeleted).Select(ur => ur.RoleId).ToList();
        foreach (var grant in db.SysRoleMenus.Where(rm => !rm.IsDeleted && roleIds.Contains(rm.RoleId)).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();
    }

    /// <summary>绑定指定身份（缺失 = 匿名请求）的控制器；未认证 / 越权一律由授权规则 fail closed。</summary>
    private static AgencyServiceFeeStatementController BuildControllerWithUser(ErpDbContext db, long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
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

    /// <summary>构造控制器并注入测试身份（已认证且具备既有「客户资料」菜单；登记人由 ClaimsPrincipal 写入）</summary>
    private static AgencyServiceFeeStatementController BuildController(ErpDbContext db, string? userName = null)
    {
        var userId = SeedPrivilegedUser(db);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (userName is not null) claims.Add(new Claim(ClaimTypes.Name, userName));
        var controller = new AgencyServiceFeeStatementController(db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
        return controller;
    }

    private static BaseCustomer SeedCustomer(
        ErpDbContext db, string code, string name, int status = 1, bool deleted = false)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = status,
            CreditStatus = "正常",
            CreditLimit = 100000m,
            CreditDays = 30,
            IsDeleted = deleted
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedOrder(
        ErpDbContext db, string orderNo, long customerId, Currency currency = Currency.USD,
        decimal totalAmount = 1000m, DocumentStatus status = DocumentStatus.Approved, bool deleted = false)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 8, 15),
            CustomerId = customerId,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = status,
            IsDeleted = deleted
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    /// <summary>登记一条**已登记**的 ERP-069 协议证据（对账单的显式依据；草稿 / 已作废都不能作为依据）</summary>
    private static async Task<AgencyServiceFeeAgreementDto> SeedRecordedAgreementAsync(
        ErpDbContext db, long customerId, string agreementNo = "ASF-520-001", string currency = "USD",
        string feeMethod = AgencyServiceFeeAgreementRules.FeeMethodRate, decimal? ratePercent = 1.5m,
        decimal? fixedAmount = null)
    {
        var created = await AgencyServiceFeeAgreementService.CreateAsync(db, new AgencyServiceFeeAgreementSaveDto
        {
            AgreementNo = agreementNo,
            CustomerId = customerId,
            EffectiveFrom = new DateTime(2026, 1, 1),
            Currency = currency,
            FeeMethod = feeMethod,
            RatePercent = ratePercent,
            FixedAmount = fixedAmount,
            FeeBasis = "按出口发票金额"
        });
        return await AgencyServiceFeeAgreementService.RecordAsync(db, created.Id, "协议登记员");
    }

    private static AgencyServiceFeeStatementLineSaveDto LineDto(
        string sourceType, long sourceId, decimal amount = 100m, string description = "代理服务费",
        decimal? basisQuantity = null, string basisNote = "按协议约定的服务期间", string remark = "")
        => new()
        {
            SourceType = sourceType,
            SourceId = sourceId,
            Description = description,
            Amount = amount,
            BasisQuantity = basisQuantity,
            BasisNote = basisNote,
            Remark = remark
        };

    private static AgencyServiceFeeStatementSaveDto SaveDto(
        long customerId, long agreementId, IEnumerable<AgencyServiceFeeStatementLineSaveDto> lines,
        string statementNo = "ASFS-2026-001", string currency = "USD",
        DateTime? statementDate = null, DateTime? dueDate = null,
        DateTime? periodFrom = null, DateTime? periodTo = null, string remark = "")
        => new()
        {
            StatementNo = statementNo,
            CustomerId = customerId,
            Currency = currency,
            StatementDate = statementDate ?? new DateTime(2026, 9, 20),
            DueDate = dueDate,
            ServicePeriodFrom = periodFrom ?? new DateTime(2026, 8, 1),
            ServicePeriodTo = periodTo ?? new DateTime(2026, 8, 31),
            AgreementId = agreementId,
            Remark = remark,
            Lines = lines.ToList()
        };

    /// <summary>便捷用法：单行销售订单来源的对账单请求</summary>
    private static AgencyServiceFeeStatementSaveDto SalesOrderSave(
        long customerId, long agreementId, long orderId, decimal amount = 100m,
        string statementNo = "ASFS-2026-001", string currency = "USD")
        => SaveDto(customerId, agreementId,
            new[] { LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, orderId, amount) },
            statementNo: statementNo, currency: currency);

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

    private static AgencyServiceFeeStatement Reload(ErpDbContext db, long id)
    {
        db.ChangeTracker.Clear();
        return db.AgencyServiceFeeStatements.AsNoTracking().Single(x => x.Id == id);
    }

    private static List<AgencyServiceFeeStatementLine> ActiveLines(ErpDbContext db, long statementId)
    {
        db.ChangeTracker.Clear();
        return db.AgencyServiceFeeStatementLines.AsNoTracking()
            .Where(l => l.StatementId == statementId && !l.IsDeleted)
            .OrderBy(l => l.LineNo).ThenBy(l => l.Id)
            .ToList();
    }

    /// <summary>被拒请求零变更：对账单表头逐字段（含状态 / 登记 / 作废 / 客户与协议快照与全部审计）不变。</summary>
    private static void AssertUnchanged(AgencyServiceFeeStatement expected, AgencyServiceFeeStatement actual)
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
    }

    /// <summary>被拒请求零变更：全部有效行逐字段（含状态 / 登记 / 作废 / 金额 / 软删除标记）不变。</summary>
    private static void AssertLinesUnchanged(
        IReadOnlyList<AgencyServiceFeeStatementLine> expected,
        IReadOnlyList<AgencyServiceFeeStatementLine> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].Id, actual[index].Id);
            Assert.Equal(expected[index].LineNo, actual[index].LineNo);
            Assert.Equal(expected[index].SourceType, actual[index].SourceType);
            Assert.Equal(expected[index].SourceId, actual[index].SourceId);
            Assert.Equal(expected[index].Amount, actual[index].Amount);
            Assert.Equal(expected[index].Status, actual[index].Status);
            Assert.Equal(expected[index].RecordedAt, actual[index].RecordedAt);
            Assert.Equal(expected[index].RecordedBy, actual[index].RecordedBy);
            Assert.Equal(expected[index].VoidedAt, actual[index].VoidedAt);
            Assert.Equal(expected[index].VoidReason, actual[index].VoidReason);
            Assert.Equal(expected[index].UpdatedAt, actual[index].UpdatedAt);
            Assert.False(actual[index].IsDeleted);
        }
    }
}








