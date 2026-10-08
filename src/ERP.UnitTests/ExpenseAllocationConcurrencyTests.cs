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
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-386 费用分摊并发生成 / 作废 / 来源费用改动护栏单元测试（内存库 + 真实控制器）。
/// <list type="number">
/// <item><b>原子性与锁序</b>：传统分摊生成、ERP-042 批次生成 / 作废、来源费用商业修改 / 删除、
/// 参与方维护都在同一事务内按「来源费用单行 → 装柜清单行 → 参与方行 → 分摊批次行 → 模块级单号键行」
/// 顺序加锁（源码契约 + 行为验证），失败整体回滚（不留部分行、不占用单号）。</item>
/// <item><b>重复生成与单号保留</b>：并发等价传统分摊请求最多生成一套完整费用单、不同请求单号不重复；
/// 同一「来源费用 + 装柜清单」有效期内的批次生成被拒绝（不区分方法）；作废后可重新生成。</item>
/// <item><b>来源证据完整性</b>：有效批次来源费用不得被删除、不得改写权威分摊基数（金额 / 币种 / 汇率 /
/// 归属客户 / 费用类型 / 柜级身份）；作废保留原因与原始审计，且不改写已生成费用单与装柜清单 / 结算口径。</item>
/// <item><b>授权与金额口径</b>：越界 / 无归属范围在任何写入之前 fail closed 且零副作用；
/// 币种精度与余差分布保持既有口径（合计恒等于来源金额，绝不发明余额）。</item>
/// </list>
/// <para>内存库无行锁语义（<see cref="ExpenseAllocationConcurrencyRules.IsRelationalProvider"/> 为 false 时锁定与事务等价无操作），
/// 因此本文件的「并发」以确定性重复拒绝 + 原子回滚口径验证；真实 SQL 两个独立连接竞态见
/// <c>ERP.IntegrationTests/ExpenseAllocationConcurrencySqlServerTests.cs</c>。</para>
/// </summary>
public class ExpenseAllocationConcurrencyTests
{
    // ==================== 0. 脚手架 ====================

    /// <summary>以显式范围模拟「已通过 HTTP 授权管线」的控制器（范围来自已认证主体，绝不来自请求体）。</summary>
    private static ExpenseBillController NewExpenseController(ErpDbContext db, SalespersonDataScope? scope)
    {
        var httpContext = new DefaultHttpContext();
        if (scope is not null)
            httpContext.Items[ExpenseRequestAuthorizationFilter.ScopeItemKey] = scope;

        var controller = new ExpenseBillController(new GenericService<FinanceExpense>(db), db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
            ActionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor()
        };
        return controller;
    }

    private static SalespersonDataScope Restricted(long salesmanId, params long[] allowedCustomerIds)
        => new() { IsPrivileged = false, SalesmanId = salesmanId, AllowedCustomerIds = allowedCustomerIds.ToHashSet() };

    private static SalespersonDataScope Privileged()
        => new() { IsPrivileged = true, SalesmanId = null, AllowedCustomerIds = null };

    private static BaseCustomer SeedCustomer(
        ErpDbContext db, string code, string name, int status = 1, bool deleted = false)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code, CustomerName = name, CreditStatus = "正常",
            Status = status, IsDeleted = deleted
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static FinanceExpense SeedExpense(
        ErpDbContext db, string expenseNo, long? customerId, decimal amount = 1000m, string currency = "CNY",
        decimal exchangeRate = 1m, string refType = "拼柜", string refNo = "TCLU-001",
        string expenseType = "报关费", DateTime? expenseDate = null)
    {
        var expense = new FinanceExpense
        {
            ExpenseNo = expenseNo,
            ExpenseDate = expenseDate ?? new DateTime(2026, 10, 8),
            ExpenseType = expenseType,
            Amount = amount,
            Currency = currency,
            ExchangeRate = exchangeRate,
            AmountCny = amount,
            RefType = refType,
            RefNo = refNo,
            CustomerId = customerId,
            PaymentStatus = "未付"
        };
        db.FinanceExpenses.Add(expense);
        db.SaveChanges();
        return expense;
    }

    private static ContainerLoadingList SeedLoadingList(
        ErpDbContext db, string listNo, string containerNo, long customerId,
        decimal cartons = 10m, decimal weight = 500m, decimal volume = 3.5m)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = listNo,
            LoadingDate = new DateTime(2026, 10, 8),
            ContainerNo = containerNo,
            CustomerId = customerId,
            Status = DocumentStatus.Pending,
            TotalCartons = cartons,
            TotalWeight = weight,
            TotalVolume = volume
        };
        db.ContainerLoadingLists.Add(list);
        db.SaveChanges();
        return list;
    }

    private static ContainerLoadingListParticipant SeedParticipant(
        ErpDbContext db, long loadingListId, BaseCustomer customer, bool primary = false, int status = 1)
    {
        var participant = new ContainerLoadingListParticipant
        {
            LoadingListId = loadingListId,
            CustomerId = customer.Id,
            CustomerCode = customer.CustomerCode,
            CustomerName = customer.CustomerName,
            IsPrimary = primary,
            Status = status
        };
        db.ContainerLoadingListParticipants.Add(participant);
        db.SaveChanges();
        return participant;
    }

    private static ContainerExpenseAllocationRequest BatchRequest(
        long sourceExpenseId, long loadingListId, string method = "按体积", string remark = "",
        params (long ParticipantId, decimal? BasisValue)[] lines)
        => new()
        {
            SourceExpenseId = sourceExpenseId,
            LoadingListId = loadingListId,
            AllocationMethod = method,
            Remark = remark,
            Lines = lines.Select(l => new ContainerExpenseAllocationBasisDto
            {
                ParticipantId = l.ParticipantId,
                BasisValue = l.BasisValue
            }).ToList()
        };

    private static ExpenseAllocateRequest LegacyRequest(
        string refNo, long? customerId, decimal total = 100m, string currency = "CNY",
        decimal rate = 1m, DateTime? expenseDate = null, params (long? CustomerId, decimal Volume)[] details)
    {
        var rows = details.Length == 0
            ? new[] { (CustomerId: customerId, Volume: 1m) }
            : details;
        return new ExpenseAllocateRequest
        {
            RefType = "拼柜",
            RefNo = refNo,
            ExpenseType = "报关费",
            TotalAmount = total,
            Currency = currency,
            ExchangeRate = rate,
            AllocationBase = "按体积",
            ExpenseDate = expenseDate,
            Details = rows.Select(r => new ExpenseAllocateDetail
            {
                CustomerId = r.CustomerId, CustomerName = "客户", Volume = r.Volume
            }).ToList()
        };
    }

    private static async Task<BusinessException> AssertCodeAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NEWERP.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName,
            Path.Combine(segments).Replace('/', Path.DirectorySeparatorChar)));
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    // ==================== 1. 合约：原子事务 / 唯一锁序 / 不新增授权 ====================

    [Fact]
    public void 生成与作废_同一事务内按固定锁序加锁并提交()
    {
        var service = ReadSource("src", "ERP.Application", "Services", "ContainerExpenseAllocationService.cs");
        var generate = service[service.IndexOf("// ==================== 5.", StringComparison.Ordinal)..];
        generate = generate[..generate.IndexOf("// ==================== 6.", StringComparison.Ordinal)];

        var transaction = generate.IndexOf("BeginTransactionIfRelationalAsync", StringComparison.Ordinal);
        var sourceLock = generate.IndexOf("LockSourceExpenseRowsAsync", StringComparison.Ordinal);
        var loadingLock = generate.IndexOf("LockLoadingListRowsAsync", StringComparison.Ordinal);
        var participantLock = generate.IndexOf("LockParticipantRowsAsync", StringComparison.Ordinal);
        var numberKey = generate.IndexOf("LockModuleNumberKeyAsync", StringComparison.Ordinal);
        var duplicate = generate.IndexOf("var duplicated = await db.FinanceExpenseAllocationBatches.AnyAsync", StringComparison.Ordinal);
        var commit = generate.IndexOf("transaction.CommitAsync", StringComparison.Ordinal);

        // 事务 → 来源费用单行 → 装柜清单行 → 参与方行 → 模块单号键行 → 锁内重复检查 → 提交
        Assert.True(transaction >= 0, "生成必须先开启原子事务");
        Assert.True(sourceLock > transaction, "来源费用单行锁必须在事务内且先于其他锁");
        Assert.True(loadingLock > sourceLock, "装柜清单行锁必须晚于来源费用单行锁");
        Assert.True(participantLock > loadingLock, "参与方行锁必须晚于装柜清单行锁");
        Assert.True(numberKey > participantLock, "模块单号键行锁必须晚于参与方行锁");
        Assert.True(duplicate > numberKey, "重复生成检查必须在模块单号键行锁内");
        Assert.True(commit > duplicate, "提交必须在锁内校验与写入之后");
        Assert.Equal(1, Count(generate, "await db.SaveChangesAsync();"));

        var voidPart = service[service.IndexOf(
            "public static async Task<ContainerExpenseAllocationBatchDto> VoidAsync", StringComparison.Ordinal)..];
        var vSource = voidPart.IndexOf("LockSourceExpenseRowsAsync", StringComparison.Ordinal);
        var vList = voidPart.IndexOf("LockLoadingListRowsAsync", StringComparison.Ordinal);
        var vBatch = voidPart.IndexOf("LockBatchRowAsync", StringComparison.Ordinal);
        var vCommit = voidPart.IndexOf("transaction.CommitAsync", StringComparison.Ordinal);
        Assert.True(vSource >= 0 && vList > vSource && vBatch > vList && vCommit > vBatch,
            "作废必须按「来源费用单行 → 装柜清单行 → 分摊批次行」加锁并在同一事务内提交");
    }

    [Fact]
    public void 传统分摊与来源费用改动_同一事务内使用模块单号键行与来源费用单行锁()
    {
        var controller = ReadSource("src", "ERP.Api", "Controllers", "ExpenseBillController.cs");
        var apply = controller[controller.IndexOf(
            "public async Task<IActionResult> AllocateApply", StringComparison.Ordinal)..];
        apply = apply[..apply.IndexOf("private async Task EnsureLegacyAllocateRequestValidAsync", StringComparison.Ordinal)];

        var transaction = apply.IndexOf("BeginTransactionIfRelationalAsync", StringComparison.Ordinal);
        var numberKey = apply.IndexOf("LockModuleNumberKeyAsync", StringComparison.Ordinal);
        var duplicateCheck = apply.IndexOf("x.RefNo == req.RefNo", StringComparison.Ordinal);
        var commit = apply.IndexOf("transaction.CommitAsync", StringComparison.Ordinal);

        Assert.True(transaction >= 0 && numberKey > transaction, "传统分摊必须在事务内先取模块单号键行锁");
        Assert.True(duplicateCheck > numberKey, "重复检查必须在模块单号键行锁内");
        Assert.True(commit > duplicateCheck, "提交必须在重复检查 + 单号保留 + 写入之后");

        // 传统分摊不引用权威来源单据：绝不按自由文本柜号 / 单号反查来源，也绝不把 RefNo 当锁键
        Assert.Contains("绝不把自由文本 RefNo / 柜号当作权威来源身份或锁键", controller);
        Assert.DoesNotContain("ContainerLoadingLists", controller);

        // 来源费用修改 / 删除 / 批量删除：同一事务内先取来源费用单行锁 + 有效批次来源护栏
        Assert.True(Count(controller, "LockSourceExpenseRowsAsync") >= 3);
        Assert.True(Count(controller, "BeginTransactionIfRelationalAsync") >= 4);
        Assert.Contains("EnsureSourceAllocationBasisUnchanged", controller);
        Assert.Contains("EnsureSourceDeletableAsync", controller);

        // 不新增授权 / 不引入匿名或管理员兜底
        Assert.DoesNotContain("AllowAnonymous", controller);
        Assert.DoesNotContain("SysRoleMenu", controller);
    }

    [Fact]
    public void 参与方维护_锁序与生成一致且先加锁后读取()
    {
        var service = ReadSource("src", "ERP.Application", "Services", "ContainerLoadingParticipantService.cs");
        Assert.True(Count(service, "LockLoadingListRowsAsync") >= 5, "全部 5 条参与方写路由都必须取装柜清单行锁");
        Assert.True(Count(service, "BeginTransactionIfRelationalAsync") >= 5);
        Assert.True(Count(service, "LockParticipantRowsAsync") >= 4);
        Assert.True(Count(service, "transaction.CommitAsync") >= 5);

        var create = service[service.IndexOf(
            "public static async Task<ContainerLoadingParticipantDto> CreateAsync", StringComparison.Ordinal)..];
        Assert.True(create.IndexOf("LockLoadingListRowsAsync", StringComparison.Ordinal)
            < create.IndexOf("EnsureLoadingListAsync", StringComparison.Ordinal),
            "必须先加锁再读取参与方 / 清单，绝不留下先读后锁窗口");
    }

    // ==================== 2. 纯规则与并发键 ====================

    [Fact]
    public void 规则_行锁集合去重升序且内存库无行锁语义()
    {
        using var db = TestDbFactory.Create();
        Assert.False(ExpenseAllocationConcurrencyRules.IsRelationalProvider(db));

        Assert.Equal(new long[] { 2, 5, 9 },
            ExpenseAllocationConcurrencyRules.MergeRowLockIds(new long[] { 9, 2, 5, 2, 0, -3 }));
        Assert.Empty(ExpenseAllocationConcurrencyRules.MergeRowLockIds(null));

        // 单号键只取既有模块菜单行（权威行），绝不使用自由文本键
        var rules = ReadSource("src", "ERP.Application", "Services", "ExpenseAllocationConcurrencyRules.cs");
        Assert.Contains("db.SysMenus.FirstOrDefaultAsync", rules);
        Assert.Contains("MenuCode == ExpenseAuthorizationRules.RequiredMenuCode", rules);
        // 调用方已开启事务时绝不嵌套（参与方路由的既有可串行化事务直接复用）
        Assert.Contains("db.Database.CurrentTransaction is not null", rules);
        Assert.Contains("来源费用单行", ExpenseAllocationConcurrencyRules.GlobalLockOrderText);
        Assert.Contains("模块级单号键", ExpenseAllocationConcurrencyRules.NumberKeyText);
        Assert.Contains("expense-bill", ExpenseAllocationConcurrencyRules.NumberKeyText);
        Assert.Contains("不新增菜单 / 角色 / 用户授权", ExpenseAllocationConcurrencyRules.BoundaryText);
        Assert.Contains("不物理删除", ExpenseAllocationConcurrencyRules.BoundaryText);

        // 锁序 / 单号键 / 双连接竞态口径与文档同源
        var doc = ReadSource("docs", "expense-allocation-concurrency.md");
        Assert.Contains("来源费用单行", doc);
        Assert.Contains("装柜清单行", doc);
        Assert.Contains("参与方行", doc);
        Assert.Contains("分摊批次行", doc);
        Assert.Contains("模块级单号键", doc);
        Assert.Contains("绝不使用自由文本", doc);
        Assert.Contains("两个独立连接", doc);
        Assert.Contains("不新增任何用户授权", doc);
        Assert.Contains("NEWERP_AutoAcceptance", doc);
        Assert.Contains("NEWERP_AUTOTEST", doc);
        Assert.Contains("库存来源单据审计", doc);
    }

    [Fact]
    public void 来源分摊基数护栏_仅权威基数变更被拒绝()
    {
        var stored = new FinanceExpense
        {
            Amount = 100m, Currency = "CNY", ExchangeRate = 1m, CustomerId = 7,
            ExpenseType = "报关费", RefType = "拼柜", RefNo = "TCLU-1", Remark = "旧"
        };
        var sameBasis = new FinanceExpense
        {
            Amount = 100m, Currency = "cny", ExchangeRate = 1m, CustomerId = 7,
            ExpenseType = " 报关费 ", RefType = "拼柜", RefNo = " TCLU-1 ", Remark = "新"
        };
        ExpenseAllocationConcurrencyRules.EnsureSourceAllocationBasisUnchanged(stored, sameBasis);

        FinanceExpense Changed(decimal amount, string currency, decimal rate, long customerId,
            string type, string refType, string refNo)
            => new()
            {
                Amount = amount, Currency = currency, ExchangeRate = rate, CustomerId = customerId,
                ExpenseType = type, RefType = refType, RefNo = refNo
            };

        foreach (var changed in new[]
                 {
                     Changed(101m, "CNY", 1m, 7, "报关费", "拼柜", "TCLU-1"),
                     Changed(100m, "USD", 1m, 7, "报关费", "拼柜", "TCLU-1"),
                     Changed(100m, "CNY", 7m, 7, "报关费", "拼柜", "TCLU-1"),
                     Changed(100m, "CNY", 1m, 8, "报关费", "拼柜", "TCLU-1"),
                     Changed(100m, "CNY", 1m, 7, "拖车费", "拼柜", "TCLU-1"),
                     Changed(100m, "CNY", 1m, 7, "报关费", "整柜", "TCLU-1"),
                     Changed(100m, "CNY", 1m, 7, "报关费", "拼柜", "TCLU-2")
                 })
        {
            var ex = Assert.Throws<BusinessException>(
                () => ExpenseAllocationConcurrencyRules.EnsureSourceAllocationBasisUnchanged(stored, changed));
            Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        }
    }

    // ==================== 3. 传统分摊：重复检查 + 单号保留原子化 ====================

    [Fact]
    public async Task 传统分摊_无效明细整批拒绝且不占用任何单号()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db, "C-OWN", "本人客户");
        var other = SeedCustomer(db, "C-OTHER", "他人客户");
        var scope = Restricted(1, own.Id);

        await AssertCodeAsync(ErrorCodes.Forbidden, () => NewExpenseController(db, scope).AllocateApply(
            LegacyRequest("TCLU-A", own.Id,
                details: new[] { (CustomerId: (long?)own.Id, Volume: 1m), (CustomerId: (long?)other.Id, Volume: 1m) })));
        Assert.Equal(0, await db.FinanceExpenses.CountAsync());

        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => NewExpenseController(db, scope).AllocateApply(
            LegacyRequest("TCLU-A", own.Id, total: 0m)));
        Assert.Equal(0, await db.FinanceExpenses.CountAsync());

        // 非法请求不占用单号：随后合规请求仍从当日 001 开始
        Assert.IsType<OkObjectResult>(await NewExpenseController(db, scope).AllocateApply(LegacyRequest("TCLU-A", own.Id)));
        var single = Assert.Single(await db.FinanceExpenses.AsNoTracking().Select(x => x.ExpenseNo).ToListAsync());
        Assert.EndsWith("-001", single);
    }

    [Fact]
    public async Task 传统分摊_等价请求最多一套完整费用单且不同请求单号绝不重复()
    {
        using var db = TestDbFactory.Create();
        var a = SeedCustomer(db, "C-A", "客户A");
        var b = SeedCustomer(db, "C-B", "客户B");
        var scope = Restricted(1, a.Id, b.Id);

        var multi = LegacyRequest("TCLU-MULTI", a.Id, total: 100m,
            details: new[] { (CustomerId: (long?)a.Id, Volume: 3m), (CustomerId: (long?)b.Id, Volume: 1m) });
        Assert.IsType<OkObjectResult>(await NewExpenseController(db, scope).AllocateApply(multi));
        var rowsAfterFirst = await db.FinanceExpenses.AsNoTracking().OrderBy(x => x.ExpenseNo).ToListAsync();
        Assert.Equal(2, rowsAfterFirst.Count);
        Assert.Equal(100m, rowsAfterFirst.Sum(x => x.Amount));

        // 等价请求：拒绝且不新增任何行（也不重新占用单号）
        await AssertCodeAsync(ErrorCodes.RuleConflict, () =>
            NewExpenseController(db, scope).AllocateApply(multi));
        Assert.Equal(2, await db.FinanceExpenses.CountAsync());

        // 另一笔请求（不同 RefNo）：单号递增且与既有单号绝不重复
        Assert.IsType<OkObjectResult>(await NewExpenseController(db, scope).AllocateApply(
            LegacyRequest("TCLU-NEXT", a.Id, total: 50m)));
        var all = await db.FinanceExpenses.AsNoTracking().Select(x => x.ExpenseNo).ToListAsync();
        Assert.Equal(3, all.Count);
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    // ==================== 4. ERP-042 批次：生成 / 作废 / 来源证据完整性 ====================

    [Fact]
    public async Task 批次生成与作废_重复生成拒绝_作废保留历史与原始证据且可重新生成()
    {
        using var db = TestDbFactory.Create();
        var a = SeedCustomer(db, "C-A", "客户A");
        var b = SeedCustomer(db, "C-B", "客户B");
        var list = SeedLoadingList(db, "ZG-1", "TCLU-1", a.Id);
        var pa = SeedParticipant(db, list.Id, a, primary: true);
        var pb = SeedParticipant(db, list.Id, b);
        var source = SeedExpense(db, "EXP-SRC-1", a.Id, amount: 300m, refNo: "TCLU-1");
        var request = BatchRequest(source.Id, list.Id,
            lines: new[]
            {
                (ParticipantId: pa.Id, BasisValue: (decimal?)2m),
                (ParticipantId: pb.Id, BasisValue: (decimal?)1m)
            });

        var generated = await ContainerExpenseAllocationService.GenerateAsync(db, request, Privileged());
        Assert.Equal(300m, generated.SourceAmount);
        Assert.Equal(300m, generated.AllocatedTotal);
        Assert.Equal(2, generated.LineCount);
        Assert.Equal(2, await db.FinanceExpenseAllocationLines.CountAsync(x => x.BatchId == generated.BatchId));

        var expenseRows = await db.FinanceExpenses.AsNoTracking()
            .Where(x => x.AllocationBatchNo == generated.BatchNo).ToListAsync();
        Assert.Equal(2, expenseRows.Count);
        Assert.Equal(300m, expenseRows.Sum(x => x.Amount));
        Assert.Equal(200m, expenseRows.Single(x => x.CustomerId == a.Id).Amount);
        Assert.Equal(2, generated.ExpenseNos.Distinct().Count());

        // 重复生成（同一来源 + 同一清单，不区分方法）：拒绝且不留任何部分行
        await AssertCodeAsync(ErrorCodes.Duplicate, () =>
            ContainerExpenseAllocationService.GenerateAsync(db, request, Privileged()));
        Assert.Equal(1, await db.FinanceExpenseAllocationBatches.CountAsync(x => !x.IsDeleted));
        Assert.Equal(2, await db.FinanceExpenseAllocationLines.CountAsync());
        Assert.Equal(2, await db.FinanceExpenses.CountAsync(x => x.AllocationBatchNo == generated.BatchNo));

        // 作废必须显式填写原因
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () =>
            ContainerExpenseAllocationService.VoidAsync(db, generated.BatchId, "   ", Privileged()));

        var voided = await ContainerExpenseAllocationService.VoidAsync(
            db, generated.BatchId, "金额填错，重新分摊", Privileged());
        Assert.Equal(ContainerExpenseAllocationRules.BatchVoided, voided.Status);
        Assert.Equal("金额填错，重新分摊", voided.VoidReason);
        Assert.NotNull(voided.VoidedAt);

        // 作废不改写已生成的费用单行 / 来源费用 / 装柜清单口径
        var rowsAfterVoid = await db.FinanceExpenses.AsNoTracking()
            .Where(x => x.AllocationBatchNo == generated.BatchNo).ToListAsync();
        Assert.Equal(300m, rowsAfterVoid.Sum(x => x.Amount));
        var sourceAfter = await db.FinanceExpenses.AsNoTracking().SingleAsync(x => x.Id == source.Id);
        Assert.Equal(300m, sourceAfter.Amount);
        Assert.Equal(string.Empty, (sourceAfter.AllocationBatchNo ?? string.Empty).Trim());
        var listAfter = await db.ContainerLoadingLists.AsNoTracking().SingleAsync(x => x.Id == list.Id);
        Assert.Equal(10m, listAfter.TotalCartons);
        Assert.Equal(500m, listAfter.TotalWeight);
        Assert.Equal(3.5m, listAfter.TotalVolume);

        // 重复作废拒绝
        await AssertCodeAsync(ErrorCodes.RuleConflict, () =>
            ContainerExpenseAllocationService.VoidAsync(db, generated.BatchId, "再次作废", Privileged()));

        // 作废后可重新生成：新批次号 / 新单号，旧批次历史保留
        var again = await ContainerExpenseAllocationService.GenerateAsync(db, request, Privileged());
        Assert.NotEqual(generated.BatchNo, again.BatchNo);
        Assert.Equal(2, await db.FinanceExpenseAllocationBatches.CountAsync(x => !x.IsDeleted));
        Assert.Equal(ContainerExpenseAllocationRules.BatchVoided,
            (await db.FinanceExpenseAllocationBatches.AsNoTracking().SingleAsync(x => x.Id == generated.BatchId)).Status);
        var numbers = await db.FinanceExpenses.AsNoTracking().Select(x => x.ExpenseNo).ToListAsync();
        Assert.Equal(numbers.Count, numbers.Distinct().Count());
        Assert.True(await ExpenseAllocationConcurrencyRules.HasEffectiveBatchForSourceAsync(db, source.Id));
    }

    [Fact]
    public async Task 来源费用修改与删除_有效批次来源受保护_作废后释放()
    {
        using var db = TestDbFactory.Create();
        var a = SeedCustomer(db, "C-A", "客户A");
        var other = SeedCustomer(db, "C-B", "客户B");
        var list = SeedLoadingList(db, "ZG-2", "TCLU-2", a.Id);
        var pa = SeedParticipant(db, list.Id, a, primary: true);
        var source = SeedExpense(db, "EXP-SRC-2", a.Id, amount: 200m, refNo: "TCLU-2");
        var scope = Privileged();

        var generated = await ContainerExpenseAllocationService.GenerateAsync(db,
            BatchRequest(source.Id, list.Id, lines: new[] { (ParticipantId: pa.Id, BasisValue: (decimal?)5m) }), scope);

        FinanceExpense Body(decimal amount, string remark) => new()
        {
            ExpenseNo = source.ExpenseNo, ExpenseDate = source.ExpenseDate, ExpenseType = source.ExpenseType,
            Amount = amount, Currency = source.Currency, ExchangeRate = source.ExchangeRate, AmountCny = amount,
            RefType = source.RefType, RefNo = source.RefNo, CustomerId = source.CustomerId,
            PaymentStatus = source.PaymentStatus, Remark = remark
        };

        // 金额 / 归属客户 / 币种等权威分摊基数：拒绝且不改写原行与批次证据
        await AssertCodeAsync(ErrorCodes.RuleConflict, () =>
            NewExpenseController(db, scope).Update(source.Id, Body(250m, "改金额")));
        var customerChanged = Body(200m, "改客户");
        customerChanged.CustomerId = other.Id;
        await AssertCodeAsync(ErrorCodes.RuleConflict, () =>
            NewExpenseController(db, scope).Update(source.Id, customerChanged));
        var currencyChanged = Body(200m, "改币种");
        currencyChanged.Currency = "USD";
        await AssertCodeAsync(ErrorCodes.RuleConflict, () =>
            NewExpenseController(db, scope).Update(source.Id, currencyChanged));

        var stored = await db.FinanceExpenses.AsNoTracking().SingleAsync(x => x.Id == source.Id);
        Assert.Equal(200m, stored.Amount);
        Assert.Equal(a.Id, stored.CustomerId);
        Assert.Equal("CNY", stored.Currency);
        Assert.Equal(200m, generated.SourceAmount);

        // 非基数字段（备注）仍可修改
        Assert.IsType<OkObjectResult>(await NewExpenseController(db, scope).Update(source.Id, Body(200m, "改备注")));
        Assert.Equal("改备注",
            (await db.FinanceExpenses.AsNoTracking().SingleAsync(x => x.Id == source.Id)).Remark);

        // 有效批次来源费用：单条 / 批量删除都拒绝且不改写任何行
        await AssertCodeAsync(ErrorCodes.RuleConflict, () => NewExpenseController(db, scope).Delete(source.Id));
        await AssertCodeAsync(ErrorCodes.RuleConflict, () =>
            NewExpenseController(db, scope).BatchDelete(new List<long> { source.Id }));
        Assert.False((await db.FinanceExpenses.AsNoTracking().SingleAsync(x => x.Id == source.Id)).IsDeleted);

        // 显式作废批次后释放：来源费用可以软删除（历史与逐行留痕保留）
        await ContainerExpenseAllocationService.VoidAsync(db, generated.BatchId, "来源口径更正", scope);
        Assert.False(await ExpenseAllocationConcurrencyRules.HasEffectiveBatchForSourceAsync(db, source.Id));
        Assert.IsType<OkObjectResult>(await NewExpenseController(db, scope).Delete(source.Id));
        Assert.True((await db.FinanceExpenses.AsNoTracking().SingleAsync(x => x.Id == source.Id)).IsDeleted);
        Assert.Equal(1, await db.FinanceExpenseAllocationBatches.CountAsync(x => !x.IsDeleted));
        Assert.Equal(1, await db.FinanceExpenseAllocationLines.CountAsync());
    }

    [Fact]
    public async Task 批次生成_无效来源或无效基数拒绝且不留任何行()
    {
        using var db = TestDbFactory.Create();
        var a = SeedCustomer(db, "C-A", "客户A");
        var b = SeedCustomer(db, "C-B", "客户B");
        var list = SeedLoadingList(db, "ZG-4", "TCLU-4", a.Id);
        var pa = SeedParticipant(db, list.Id, a, primary: true);
        var pb = SeedParticipant(db, list.Id, b);
        var deletedSource = SeedExpense(db, "EXP-DEL", a.Id, amount: 100m, refNo: "TCLU-4");
        deletedSource.IsDeleted = true;
        var zeroSource = SeedExpense(db, "EXP-ZERO", a.Id, amount: 0m, refNo: "TCLU-4");
        var source = SeedExpense(db, "EXP-OK", a.Id, amount: 100m, refNo: "TCLU-4");
        var wrongContainerSource = SeedExpense(db, "EXP-WRONG", a.Id, amount: 100m, refNo: "TCLU-OTHER");
        await db.SaveChangesAsync();

        var validLines = new[]
        {
            (ParticipantId: pa.Id, BasisValue: (decimal?)1m),
            (ParticipantId: pb.Id, BasisValue: (decimal?)1m)
        };

        // 已删除来源 / 金额为 0 / 柜号不一致：一律拒绝
        await AssertCodeAsync(ErrorCodes.NotFound, () => ContainerExpenseAllocationService.GenerateAsync(
            db, BatchRequest(deletedSource.Id, list.Id, lines: validLines), Privileged()));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ContainerExpenseAllocationService.GenerateAsync(
            db, BatchRequest(zeroSource.Id, list.Id, lines: validLines), Privileged()));
        await AssertCodeAsync(ErrorCodes.RuleConflict, () => ContainerExpenseAllocationService.GenerateAsync(
            db, BatchRequest(wrongContainerSource.Id, list.Id, lines: validLines), Privileged()));

        // 零基数 / 缺失基数 / 清单外参与方：一律拒绝
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ContainerExpenseAllocationService.GenerateAsync(
            db, BatchRequest(source.Id, list.Id, lines: new[]
            {
                (ParticipantId: pa.Id, BasisValue: (decimal?)0m),
                (ParticipantId: pb.Id, BasisValue: (decimal?)0m)
            }), Privileged()));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ContainerExpenseAllocationService.GenerateAsync(
            db, BatchRequest(source.Id, list.Id, lines: new[] { (ParticipantId: pa.Id, BasisValue: (decimal?)null) }),
            Privileged()));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ContainerExpenseAllocationService.GenerateAsync(
            db, BatchRequest(source.Id, list.Id, lines: new[]
            {
                (ParticipantId: pa.Id, BasisValue: (decimal?)1m),
                (ParticipantId: 999_999L, BasisValue: (decimal?)1m)
            }), Privileged()));

        // 全部失败请求零副作用：无批次 / 无分摊行 / 无生成费用单
        Assert.Equal(0, await db.FinanceExpenseAllocationBatches.CountAsync());
        Assert.Equal(0, await db.FinanceExpenseAllocationLines.CountAsync());
        Assert.Equal(4, await db.FinanceExpenses.CountAsync());   // 只有播种的来源费用，没有生成行

        // 合规请求仍可生成且单号从当日 001 开始（失败请求不占用单号）
        var generated = await ContainerExpenseAllocationService.GenerateAsync(
            db, BatchRequest(source.Id, list.Id, lines: validLines), Privileged());
        Assert.Equal(100m, generated.AllocatedTotal);
        Assert.EndsWith("-001", generated.ExpenseNos[0]);
    }

    [Fact]
    public async Task 批次生成_币种精度与余差分布_合计恒等于来源金额且不改写任何既有口径()
    {
        using var db = TestDbFactory.Create();
        var a = SeedCustomer(db, "C-A", "客户A");
        var b = SeedCustomer(db, "C-B", "客户B");
        var c = SeedCustomer(db, "C-C", "客户C");
        var list = SeedLoadingList(db, "ZG-5", "TCLU-5", a.Id);
        var pa = SeedParticipant(db, list.Id, a, primary: true);
        var pb = SeedParticipant(db, list.Id, b);
        var pc = SeedParticipant(db, list.Id, c);
        var cnySource = SeedExpense(db, "EXP-CNY", a.Id, amount: 100m, refNo: "TCLU-5");
        var jpySource = SeedExpense(db, "EXP-JPY", a.Id, amount: 100m, currency: "JPY", refNo: "TCLU-5");

        var lines = new[]
        {
            (ParticipantId: pa.Id, BasisValue: (decimal?)1m),
            (ParticipantId: pb.Id, BasisValue: (decimal?)1m),
            (ParticipantId: pc.Id, BasisValue: (decimal?)1m)
        };

        var cny = await ContainerExpenseAllocationService.GenerateAsync(
            db, BatchRequest(cnySource.Id, list.Id, lines: lines), Privileged());
        Assert.Equal(100m, cny.AllocatedTotal);
        Assert.Equal(100m, cny.Lines.Sum(l => l.AllocatedAmount));
        Assert.Equal(1, cny.Lines.Count(l => l.RemainderCarrier));
        Assert.All(cny.Lines, l => Assert.Equal(l.AllocatedAmount, Math.Round(l.AllocatedAmount, 2)));

        var jpy = await ContainerExpenseAllocationService.GenerateAsync(
            db, BatchRequest(jpySource.Id, list.Id, lines: lines), Privileged());
        Assert.Equal(100m, jpy.AllocatedTotal);
        Assert.Equal(100m, jpy.Lines.Sum(l => l.AllocatedAmount));
        // 无小数币种（JPY）按 0 位取整：合计仍恒等于来源金额
        Assert.All(jpy.Lines, l => Assert.Equal(l.AllocatedAmount, Math.Round(l.AllocatedAmount, 0)));

        // 生成只新增费用单行：来源费用 / 装柜清单口径与既有批次逐行留痕都不被改写
        var sourceAfter = await db.FinanceExpenses.AsNoTracking().SingleAsync(x => x.Id == cnySource.Id);
        Assert.Equal(100m, sourceAfter.Amount);
        Assert.Equal("CNY", sourceAfter.Currency);
        Assert.Equal(string.Empty, (sourceAfter.AllocationBatchNo ?? string.Empty).Trim());
        var listAfter = await db.ContainerLoadingLists.AsNoTracking().SingleAsync(x => x.Id == list.Id);
        Assert.Equal(10m, listAfter.TotalCartons);
        Assert.Equal(500m, listAfter.TotalWeight);
        Assert.Equal(3.5m, listAfter.TotalVolume);
        Assert.Equal(2, await db.FinanceExpenseAllocationBatches.CountAsync(x => !x.IsDeleted));
        Assert.Equal(6, await db.FinanceExpenseAllocationLines.CountAsync());

        // 同日两笔批次：生成单号绝不重复
        var numbers = await db.FinanceExpenses.AsNoTracking()
            .Where(x => x.AllocationBatchNo != null).Select(x => x.ExpenseNo).ToListAsync();
        Assert.Equal(numbers.Count, numbers.Distinct().Count());
    }

    [Fact]
    public async Task 参与方维护与生成_停用参与方后按最新参与方集合判定且不留半成品()
    {
        using var db = TestDbFactory.Create();
        var a = SeedCustomer(db, "C-A", "客户A");
        var b = SeedCustomer(db, "C-B", "客户B");
        var list = SeedLoadingList(db, "ZG-6", "TCLU-6", a.Id);
        var pa = SeedParticipant(db, list.Id, a, primary: true);
        var pb = SeedParticipant(db, list.Id, b);
        var source = SeedExpense(db, "EXP-SRC-6", a.Id, amount: 100m, refNo: "TCLU-6");

        // 参与方维护与生成共用同一把装柜清单行锁 + 同一事务（见合约测试）：停用 b
        await ContainerLoadingParticipantService.SetStatusAsync(
            db, list.Id, pb.Id, ContainerLoadingParticipantRules.DisabledStatus, Privileged());
        Assert.Equal(ContainerLoadingParticipantRules.DisabledStatus,
            (await db.ContainerLoadingListParticipants.AsNoTracking().SingleAsync(x => x.Id == pb.Id)).Status);

        // 生成按「当前启用参与方」判定：只覆盖 a（b 已停用）可以生成，合计恒等于来源金额
        var generated = await ContainerExpenseAllocationService.GenerateAsync(db,
            BatchRequest(source.Id, list.Id, lines: new[] { (ParticipantId: pa.Id, BasisValue: (decimal?)7m) }),
            Privileged());
        Assert.Equal(1, generated.LineCount);
        Assert.Equal(100m, generated.AllocatedTotal);

        // 已停用参与方不能被当作分摊行：拒绝且不新增任何行 / 不产生第二套证据
        var linesBefore = await db.FinanceExpenseAllocationLines.CountAsync();
        var expensesBefore = await db.FinanceExpenses.CountAsync();
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ContainerExpenseAllocationService.GenerateAsync(
            db, BatchRequest(source.Id, list.Id, lines: new[]
            {
                (ParticipantId: pa.Id, BasisValue: (decimal?)1m),
                (ParticipantId: pb.Id, BasisValue: (decimal?)1m)
            }), Privileged()));
        Assert.Equal(1, await db.FinanceExpenseAllocationBatches.CountAsync(x => !x.IsDeleted));
        Assert.Equal(linesBefore, await db.FinanceExpenseAllocationLines.CountAsync());
        Assert.Equal(expensesBefore, await db.FinanceExpenses.CountAsync());
    }

    [Fact]
    public async Task 越界范围_批次预览生成与作废在任何写入之前拒绝且零副作用()
    {
        using var db = TestDbFactory.Create();
        var mine = SeedCustomer(db, "C-MINE", "本人客户");
        var other = SeedCustomer(db, "C-OTHER", "他人客户");
        var list = SeedLoadingList(db, "ZG-7", "TCLU-7", mine.Id);
        var pa = SeedParticipant(db, list.Id, mine, primary: true);
        var source = SeedExpense(db, "EXP-SRC-7", mine.Id, amount: 100m, refNo: "TCLU-7");
        var request = BatchRequest(source.Id, list.Id,
            lines: new[] { (ParticipantId: pa.Id, BasisValue: (decimal?)1m) });

        // 受限账号（范围只有他人客户）：预览 / 生成 fail closed 且零副作用
        var foreignScope = Restricted(9, other.Id);
        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            ContainerExpenseAllocationService.PreviewAsync(db, request, foreignScope));
        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            ContainerExpenseAllocationService.GenerateAsync(db, request, foreignScope));
        Assert.Equal(0, await db.FinanceExpenseAllocationBatches.CountAsync());
        Assert.Equal(0, await db.FinanceExpenseAllocationLines.CountAsync());

        // 范围内受限账号可以生成；越界账号不能作废（状态与审计保持不变）
        var ownScope = Restricted(9, mine.Id);
        var generated = await ContainerExpenseAllocationService.GenerateAsync(db, request, ownScope);
        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            ContainerExpenseAllocationService.VoidAsync(db, generated.BatchId, "越界作废", foreignScope));
        var batch = await db.FinanceExpenseAllocationBatches.AsNoTracking()
            .SingleAsync(x => x.Id == generated.BatchId);
        Assert.Equal(ContainerExpenseAllocationRules.BatchActive, batch.Status);
        Assert.Null(batch.VoidedAt);
        Assert.Equal(string.Empty, batch.VoidReason);
    }
}
