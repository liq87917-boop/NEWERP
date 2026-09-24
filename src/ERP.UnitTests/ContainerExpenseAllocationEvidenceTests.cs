using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 装柜费用分摊证据单元测试（ERP-060，**只读**视图）。覆盖：单客户 / 多客户、多币种分组（不合并不换算）、
/// 部分分摊与未分摊参考、缺失证据显示「无 / 未知」而不是零、已作废与历史异常状态批次（有效合计排除 +
/// 历史可读 + 原值不改写）、失效链接（参与方缺失 / 客户停用 / 柜号不一致 / 清单删除）逐行标注且不改派、
/// 老柜无批次可读且无回填、装柜结算单只读回显（含未关联清单与多币种不做对照）、
/// 工作台显式字段筛选与分页有界、未知筛选取值拒绝、有界查询（固定数据集访问、无逐行查库、只读不写库）、
/// 来源记录非变更与结构契约。
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何 SQL / 部署脚本、不联系任何外部系统。
/// </summary>
public class ContainerExpenseAllocationEvidenceTests
{
    // ==================== 0. 测试脚手架 ====================

    private static BaseCustomer SeedCustomer(
        ErpDbContext db, string code, string name, int status = 1, bool deleted = false)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = status,
            IsDeleted = deleted
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static ContainerLoadingList SeedLoadingList(
        ErpDbContext db, string listNo, string containerNo, long customerId, bool deleted = false)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = listNo,
            LoadingDate = new DateTime(2026, 9, 25),
            ContainerNo = containerNo,
            CustomerId = customerId,
            Status = DocumentStatus.Pending,
            IsDeleted = deleted
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

    private static FinanceExpense SeedSourceExpense(
        ErpDbContext db, string expenseNo, string containerNo, decimal amount,
        string currency = "CNY", string refType = "拼柜", string allocationBase = "不分摊",
        decimal allocationRatio = 0m, decimal allocatedAmount = 0m, string batchNo = "")
    {
        var expense = new FinanceExpense
        {
            ExpenseNo = expenseNo,
            ExpenseDate = new DateTime(2026, 9, 25),
            ExpenseType = "报关费",
            Amount = amount,
            Currency = currency,
            ExchangeRate = 1m,
            AmountCny = amount,
            RefType = refType,
            RefNo = containerNo,
            AllocationBase = allocationBase,
            AllocationRatio = allocationRatio,
            AllocatedAmount = allocatedAmount,
            AllocationBatchNo = batchNo,
            PaymentStatus = "未付"
        };
        db.FinanceExpenses.Add(expense);
        db.SaveChanges();
        return expense;
    }

    /// <summary>断言业务异常的错误码（避免只断言消息文案）</summary>
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

    private static FinanceExpenseAllocationBatch SeedBatch(
        ErpDbContext db, string batchNo, ContainerLoadingList list, FinanceExpense source,
        string method, string basisKind, string currency, decimal sourceAmount, decimal allocatedTotal,
        int status = 1, decimal exchangeRate = 1m, string remark = "",
        DateTime? voidedAt = null, string voidReason = "", string? containerNo = null)
    {
        var batch = new FinanceExpenseAllocationBatch
        {
            BatchNo = batchNo,
            SourceExpenseId = source.Id,
            SourceExpenseNo = source.ExpenseNo,
            LoadingListId = list.Id,
            LoadingListNo = list.LoadingListNo,
            ContainerNo = containerNo ?? list.ContainerNo,
            AllocationMethod = method,
            BasisKind = basisKind,
            Currency = currency,
            ExchangeRate = exchangeRate,
            SourceAmount = sourceAmount,
            AllocatedTotal = allocatedTotal,
            LineCount = 0,
            Status = status,
            VoidedAt = voidedAt,
            VoidReason = voidReason,
            Remark = remark
        };
        db.FinanceExpenseAllocationBatches.Add(batch);
        db.SaveChanges();
        return batch;
    }

    private static FinanceExpenseAllocationLine SeedLine(
        ErpDbContext db, FinanceExpenseAllocationBatch batch, ContainerLoadingList list,
        ContainerLoadingListParticipant participant, string method, string basisKind,
        string basisSource, decimal basisValue, decimal ratio, decimal amount,
        string currency = "CNY", decimal amountCny = 0m, string expenseNo = "",
        bool primary = false, int sortOrder = 0)
    {
        var line = new FinanceExpenseAllocationLine
        {
            BatchId = batch.Id,
            BatchNo = batch.BatchNo,
            SourceExpenseId = batch.SourceExpenseId,
            SourceExpenseNo = batch.SourceExpenseNo,
            LoadingListId = list.Id,
            LoadingListNo = list.LoadingListNo,
            ContainerNo = batch.ContainerNo,
            ParticipantId = participant.Id,
            CustomerId = participant.CustomerId,
            CustomerCode = participant.CustomerCode,
            CustomerName = participant.CustomerName,
            ParticipantPrimary = primary,
            AllocationMethod = method,
            BasisKind = basisKind,
            BasisSource = basisSource,
            BasisValue = basisValue,
            Ratio = ratio,
            AllocatedAmount = amount,
            AllocatedAmountCny = amountCny,
            Currency = currency,
            ExpenseNo = expenseNo,
            SortOrder = sortOrder
        };
        db.FinanceExpenseAllocationLines.Add(line);

        var stored = db.FinanceExpenseAllocationBatches.Single(x => x.Id == batch.Id);
        stored.LineCount += 1;
        db.SaveChanges();
        return line;
    }

    private static FinanceContainerSettlement SeedSettlement(
        ErpDbContext db, string settlementNo, long? loadingListId, long customerId,
        decimal totalAmount, decimal freightCost = 0m, decimal otherCost = 0m, bool deleted = false)
    {
        var settlement = new FinanceContainerSettlement
        {
            SettlementNo = settlementNo,
            SettlementDate = new DateTime(2026, 9, 25),
            LoadingListId = loadingListId,
            CustomerId = customerId,
            TotalAmount = totalAmount,
            FreightCost = freightCost,
            OtherCost = otherCost,
            Status = DocumentStatus.Pending,
            IsDeleted = deleted
        };
        db.FinanceContainerSettlements.Add(settlement);
        db.SaveChanges();
        return settlement;
    }


    // ==================== 1. 装柜清单维度：单客户 / 多客户 / 多币种 ====================

    [Fact]
    public async Task Evidence_单客户单币种_呈现批次客户金额比例与方法基数()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "单客户");
        var list = SeedLoadingList(db, "ZG20260925001", "TCLU-001", customer.Id);
        var participant = SeedParticipant(db, list.Id, customer, primary: true);
        var source = SeedSourceExpense(db, "EXP-20260925-001", "TCLU-001", 1000m);
        var batch = SeedBatch(db, "EAB-20260925-001", list, source, "按体积", "按体积", "CNY", 1000m, 1000m);
        SeedLine(db, batch, list, participant, "按体积", "按体积", "用户确认请求值",
            basisValue: 3.5m, ratio: 100m, amount: 1000m, expenseNo: "EXP-20260925-002", primary: true);

        var d = await ContainerExpenseAllocationEvidenceService.GetForLoadingListAsync(db, list.Id);

        Assert.True(d.HasEvidence);
        Assert.Equal("loading-list", d.ScopeType);
        Assert.Equal(list.Id, d.ScopeId);
        Assert.Equal(list.Id, d.LoadingListId);
        Assert.True(d.LoadingListAvailable);
        Assert.Equal("TCLU-001", d.ContainerNo);
        Assert.True(d.ContainerNoAvailable);
        Assert.Equal(1, d.ActiveBatchCount);
        Assert.Equal(0, d.VoidedBatchCount);
        Assert.Equal(0, d.UnknownStatusBatchCount);
        Assert.Equal(1, d.ActiveLineCount);
        Assert.Equal(1, d.ActiveCustomerCount);
        Assert.Equal(new[] { "CNY" }, d.Currencies);
        Assert.False(d.EvidenceTruncated);
        Assert.Empty(d.HistoryBatches);
        Assert.Equal(0, d.InvalidLinkCount);

        var group = Assert.Single(d.Groups);
        Assert.Equal("CNY", group.Currency);
        Assert.Equal(2, group.AmountPrecision);
        Assert.True(group.TotalsConsistent);
        Assert.Equal(1000m, group.AllocatedTotal);
        Assert.Equal(1000m, group.SourceTotal);
        Assert.Equal(1, group.ActiveBatchCount);
        Assert.Equal(1, group.ActiveLineCount);

        var customerGroup = Assert.Single(group.Customers);
        Assert.Equal(customer.Id, customerGroup.CustomerId);
        Assert.Equal("单客户", customerGroup.CustomerDisplay);
        Assert.True(customerGroup.CustomerAvailable);
        Assert.False(customerGroup.HasInvalidLink);
        Assert.Equal(1000m, customerGroup.AllocatedAmount);
        Assert.Equal(100m, customerGroup.RatioTotal);
        Assert.Equal("按体积", customerGroup.MethodsText);
        Assert.Contains("按体积", customerGroup.BasisText);
        Assert.Equal(new[] { "EAB-20260925-001" }, customerGroup.BatchNos);

        var batchDto = Assert.Single(d.ActiveBatches);
        Assert.Equal("EAB-20260925-001", batchDto.BatchNo);
        Assert.Equal("有效", batchDto.StatusText);
        Assert.True(batchDto.IsActive);
        Assert.Equal(1, batchDto.StoredLineCount);
        Assert.True(batchDto.ContainerLinkConsistent);
        Assert.Contains("柜号链接一致", batchDto.ContainerLinkText);
        Assert.Contains("取自批次持久化列", batchDto.TotalsText);

        var line = Assert.Single(batchDto.Lines);
        Assert.Equal(participant.Id, line.ParticipantId);
        Assert.True(line.IsPrimary);
        Assert.False(line.LinkInvalid);
        Assert.Equal("ok", line.LinkStatus);
        Assert.Equal(3.5m, line.BasisValue);
        Assert.Contains("用户确认请求值", line.BasisEvidenceText);
        Assert.Equal("EXP-20260925-002", line.ExpenseNo);

        Assert.Contains("不改写", d.BoundaryText);
        Assert.Contains("不是会计记账", d.DisclaimerText);
        Assert.Contains("只读", d.ReadOnlyText);
    }


    [Fact]
    public async Task Evidence_多客户多币种_按币种分组且不合并不换算()
    {
        using var db = TestDbFactory.Create();
        var legacy = SeedCustomer(db, "C000", "老客户");
        var customerA = SeedCustomer(db, "C001", "客户A");
        var customerB = SeedCustomer(db, "C002", "客户B");
        var customerC = SeedCustomer(db, "C003", "客户C");
        var list = SeedLoadingList(db, "ZG20260925002", "TCLU-002", legacy.Id);
        var pa = SeedParticipant(db, list.Id, customerA, primary: true);
        var pb = SeedParticipant(db, list.Id, customerB);
        var pc = SeedParticipant(db, list.Id, customerC);

        var sourceCny = SeedSourceExpense(db, "EXP-20260925-010", "TCLU-002", 1000m);
        var batchCny = SeedBatch(db, "EAB-20260925-010", list, sourceCny, "按体积", "按体积", "CNY", 1000m, 1000m);
        SeedLine(db, batchCny, list, pa, "按体积", "按体积", "用户确认请求值", 30m, 30m, 300m,
            sortOrder: 0, primary: true);
        SeedLine(db, batchCny, list, pb, "按体积", "按体积", "用户确认请求值", 70m, 70m, 700m, sortOrder: 1);

        var sourceUsd = SeedSourceExpense(db, "EXP-20260925-011", "TCLU-002", 500m, currency: "USD");
        var batchUsd = SeedBatch(db, "EAB-20260925-011", list, sourceUsd, "整柜", "箱数", "USD", 500m, 500m,
            exchangeRate: 7.2m);
        SeedLine(db, batchUsd, list, pc, "整柜", "箱数", "持久化装柜证据", 10m, 100m, 500m,
            currency: "USD", amountCny: 3600m, sortOrder: 0);

        var d = await ContainerExpenseAllocationEvidenceService.GetForLoadingListAsync(db, list.Id);

        Assert.Equal(2, d.ActiveBatchCount);
        Assert.Equal(2, d.Groups.Count);
        Assert.Equal(new[] { "CNY", "USD" }, d.Currencies);
        Assert.Equal(3, d.ActiveCustomerCount);

        var cny = d.Groups[0];
        Assert.Equal("CNY", cny.Currency);
        Assert.Equal(1000m, cny.AllocatedTotal);
        Assert.Equal(2, cny.Customers.Count);
        Assert.Equal(300m, Assert.Single(cny.Customers, c => c.CustomerId == customerA.Id).AllocatedAmount);
        Assert.Equal(700m, Assert.Single(cny.Customers, c => c.CustomerId == customerB.Id).AllocatedAmount);

        var usd = d.Groups[1];
        Assert.Equal("USD", usd.Currency);
        Assert.Equal(500m, usd.AllocatedTotal);
        Assert.Equal(3600m, usd.AllocatedTotalCny);
        Assert.Contains("不跨币种合并", usd.ConversionText);
        var usdCustomer = Assert.Single(usd.Customers);
        Assert.Equal(customerC.Id, usdCustomer.CustomerId);
        Assert.Contains("整柜", usdCustomer.MethodsText);
        Assert.Contains("持久化装柜证据", usdCustomer.BasisText);

        // 不同币种不合并：任何币种分组文案都不含跨币种相加后的金额
        Assert.DoesNotContain("1500", cny.TotalsText + usd.TotalsText + cny.ConversionText + usd.ConversionText);
    }

    [Fact]
    public async Task Evidence_部分分摊_未分摊参考按币种单列且不推断为零费用()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户A");
        var list = SeedLoadingList(db, "ZG20260925003", "TCLU-003", customer.Id);
        var participant = SeedParticipant(db, list.Id, customer, primary: true);

        var allocated = SeedSourceExpense(db, "EXP-20260925-020", "TCLU-003", 1000m);
        var pending = SeedSourceExpense(db, "EXP-20260925-021", "TCLU-003", 800m);
        var pendingUsd = SeedSourceExpense(db, "EXP-20260925-022", "TCLU-003", 300m, currency: "USD");
        var batch = SeedBatch(db, "EAB-20260925-020", list, allocated, "按箱数", "按箱数", "CNY", 1000m, 1000m);
        SeedLine(db, batch, list, participant, "按箱数", "按箱数", "用户确认请求值", 10m, 100m, 1000m);

        var d = await ContainerExpenseAllocationEvidenceService.GetForLoadingListAsync(db, list.Id);
        Assert.Single(d.Groups);
        Assert.Equal(2, d.Unallocated.Count);

        var cny = Assert.Single(d.Unallocated, u => u.Currency == "CNY");
        Assert.Equal(1, cny.SourceExpenseCount);
        Assert.Equal(800m, cny.SourceAmount);
        Assert.Equal(new[] { "EXP-20260925-021" }, cny.SourceExpenseNos);
        Assert.Equal(0, cny.VoidedBatchSourceCount);
        Assert.Contains("不代表费用为零", cny.Text);
        Assert.Contains("未分摊参考", cny.Text);

        var usd = Assert.Single(d.Unallocated, u => u.Currency == "USD");
        Assert.Equal(300m, usd.SourceAmount);

        Assert.Contains("2 条不属于任何有效分摊批次", d.UnallocatedContextText);
        Assert.Contains("历史分摊行", d.LegacyAllocationText);
    }

    [Fact]
    public async Task Evidence_无任何批次_显示无而不是零且不推断已结算()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户A");
        var list = SeedLoadingList(db, "ZG20260925004", "TCLU-004", customer.Id);
        SeedParticipant(db, list.Id, customer, primary: true);
        SeedSourceExpense(db, "EXP-20260925-030", "TCLU-004", 600m);

        var d = await ContainerExpenseAllocationEvidenceService.GetForLoadingListAsync(db, list.Id);

        Assert.False(d.HasEvidence);
        Assert.Empty(d.Groups);
        Assert.Empty(d.ActiveBatches);
        Assert.Equal(0, d.ActiveBatchCount);
        Assert.Equal(0, d.ActiveLineCount);
        Assert.Equal(0, d.ActiveCustomerCount);
        Assert.Equal(ContainerExpenseAllocationEvidenceRules.MissingEvidenceText, d.EvidenceStatusText);
        Assert.Contains(ContainerExpenseAllocationEvidenceRules.MissingEvidenceText, d.EvidenceMissingText);
        Assert.Contains("不代表费用为零", d.EvidenceMissingText);
        Assert.Contains("不代表已结清", d.EvidenceMissingText);
        Assert.Single(d.Unallocated);
        Assert.Equal(600m, d.Unallocated[0].SourceAmount);
    }


    // ==================== 2. 已作废 / 历史异常 / 失效链接 / 旧柜可读 ====================

    [Fact]
    public async Task Evidence_已作废批次_不计入有效合计但历史可读且原值不改()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户A");
        var list = SeedLoadingList(db, "ZG20260925005", "TCLU-005", customer.Id);
        var participant = SeedParticipant(db, list.Id, customer, primary: true);
        var source = SeedSourceExpense(db, "EXP-20260925-040", "TCLU-005", 900m);
        var batch = SeedBatch(db, "EAB-20260925-040", list, source, "按金额", "按金额", "CNY", 900m, 900m,
            status: 0, voidedAt: new DateTime(2026, 9, 26), voidReason: "客户归属更正", remark: "原分摊");
        SeedLine(db, batch, list, participant, "按金额", "按金额", "用户确认请求值", 900m, 100m, 900m);

        var d = await ContainerExpenseAllocationEvidenceService.GetForLoadingListAsync(db, list.Id);

        Assert.False(d.HasEvidence);
        Assert.Empty(d.Groups);
        Assert.Empty(d.ActiveBatches);
        Assert.Equal(0, d.ActiveBatchCount);
        Assert.Equal(1, d.VoidedBatchCount);
        Assert.Equal(1, d.HistoryTotal);

        var history = Assert.Single(d.HistoryBatches);
        Assert.True(history.IsVoided);
        Assert.False(history.IsActive);
        Assert.Equal("已作废", history.StatusText);
        Assert.Equal("客户归属更正", history.VoidReason);
        Assert.Equal(new DateTime(2026, 9, 26), history.VoidedAt);
        Assert.Single(history.Lines);
        Assert.Contains("已作废", d.EvidenceStatusText);
        Assert.Contains("不计入有效合计", ContainerExpenseAllocationEvidenceRules.VoidedOnlyText(1, 0));

        var unallocated = Assert.Single(d.Unallocated);
        Assert.Equal(900m, unallocated.SourceAmount);
        Assert.Equal(1, unallocated.VoidedBatchSourceCount);
        Assert.Contains("需重新分摊", unallocated.Text);

        var withoutHistory = await ContainerExpenseAllocationEvidenceService.GetForLoadingListAsync(
            db, list.Id, includeHistory: false);
        Assert.Empty(withoutHistory.HistoryBatches);
        Assert.Equal(1, withoutHistory.HistoryTotal);

        var stored = db.FinanceExpenseAllocationBatches.AsNoTracking().Single(x => x.Id == batch.Id);
        Assert.Equal(0, stored.Status);
        Assert.Equal("客户归属更正", stored.VoidReason);
        Assert.Equal(1, db.FinanceExpenseAllocationLines.Count(x => x.BatchId == batch.Id));
        Assert.Equal(900m, db.FinanceExpenses.AsNoTracking().Single(x => x.Id == source.Id).Amount);
    }

    [Fact]
    public async Task Evidence_历史异常状态批次_照实回显且不当作有效()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户A");
        var list = SeedLoadingList(db, "ZG20260925006", "TCLU-006", customer.Id);
        var participant = SeedParticipant(db, list.Id, customer, primary: true);
        var source = SeedSourceExpense(db, "EXP-20260925-050", "TCLU-006", 400m);
        var batch = SeedBatch(db, "EAB-20260925-050", list, source, "按重量", "按重量", "CNY", 400m, 400m,
            status: 9);
        SeedLine(db, batch, list, participant, "按重量", "按重量", "用户确认请求值", 100m, 100m, 400m);

        var d = await ContainerExpenseAllocationEvidenceService.GetForLoadingListAsync(db, list.Id);

        Assert.False(d.HasEvidence);
        Assert.Equal(0, d.ActiveBatchCount);
        Assert.Equal(1, d.UnknownStatusBatchCount);
        Assert.Equal(0, d.VoidedBatchCount);

        var history = Assert.Single(d.HistoryBatches);
        Assert.False(history.StatusKnown);
        Assert.Contains("未知状态（原值 9）", history.StatusText);
        Assert.Contains("历史异常状态", d.EvidenceStatusText);
    }


    [Fact]
    public async Task Evidence_失效链接_参与方缺失客户停用柜号不一致_逐行标注且不改派()
    {
        using var db = TestDbFactory.Create();
        var legacy = SeedCustomer(db, "C000", "老客户");
        var customerA = SeedCustomer(db, "C001", "客户A");
        var customerB = SeedCustomer(db, "C002", "客户B", status: 0);
        var list = SeedLoadingList(db, "ZG20260925007", "TCLU-007", legacy.Id);
        var pa = SeedParticipant(db, list.Id, customerA, primary: true);
        var pb = SeedParticipant(db, list.Id, customerB);

        // 批次一：柜号与装柜清单不一致（历史异常链接）
        var sourceMismatch = SeedSourceExpense(db, "EXP-20260925-060", "TCLU-007", 400m);
        var batchMismatch = SeedBatch(db, "EAB-20260925-060", list, sourceMismatch, "按体积", "按体积", "CNY",
            400m, 400m, containerNo: "OTHER-999");
        SeedLine(db, batchMismatch, list, pa, "按体积", "按体积", "用户确认请求值", 40m, 100m, 400m);

        // 批次二：柜号一致，但一条行客户已停用，另一条行引用被删除的参与方
        var source = SeedSourceExpense(db, "EXP-20260925-061", "TCLU-007", 600m);
        var batch = SeedBatch(db, "EAB-20260925-061", list, source, "按体积", "按体积", "CNY", 600m, 600m);
        SeedLine(db, batch, list, pb, "按体积", "按体积", "用户确认请求值", 60m, 50m, 300m, sortOrder: 0);
        var doomedParticipant = SeedParticipant(db, list.Id, customerB);
        SeedLine(db, batch, list, doomedParticipant, "按体积", "按体积", "用户确认请求值", 60m, 50m, 300m,
            sortOrder: 1);
        db.ContainerLoadingListParticipants.RemoveRange(
            db.ContainerLoadingListParticipants.Where(p => p.Id == doomedParticipant.Id));
        db.SaveChanges();

        var d = await ContainerExpenseAllocationEvidenceService.GetForLoadingListAsync(db, list.Id);

        Assert.Equal(2, d.ActiveBatchCount);
        var mismatchDto = Assert.Single(d.ActiveBatches, b => b.BatchNo == "EAB-20260925-060");
        Assert.False(mismatchDto.ContainerLinkConsistent);
        Assert.Contains("柜号不一致", mismatchDto.ContainerLinkText);
        Assert.All(mismatchDto.Lines, l => Assert.Equal("container-mismatch", l.LinkStatus));

        var scopeDto = Assert.Single(d.ActiveBatches, b => b.BatchNo == "EAB-20260925-061");
        Assert.True(scopeDto.ContainerLinkConsistent);
        Assert.Contains(scopeDto.Lines, l => l.LinkStatus == "customer-unavailable");
        Assert.Contains(scopeDto.Lines, l => l.LinkStatus == "participant-missing");
        Assert.All(scopeDto.Lines, l => Assert.True(l.LinkInvalid));
        Assert.All(scopeDto.Lines, l => Assert.True(
            l.LinkStatusText.Contains("不改派", StringComparison.Ordinal)
            || l.LinkStatusText.Contains("仅历史可读", StringComparison.Ordinal)));

        Assert.Equal(3, d.InvalidLinkCount);
        Assert.Contains("既不改派也不修复", d.InvalidLinkText);

        // 客户可用性照实标注；记录本身不被改成有效
        Assert.False(db.BaseCustomers.AsNoTracking().Single(x => x.Id == customerB.Id).IsDeleted);
        Assert.Equal(0, db.BaseCustomers.AsNoTracking().Single(x => x.Id == customerB.Id).Status);
    }

    [Fact]
    public async Task Evidence_装柜清单已删除_证据仍可读并标注不可用且无回填()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户A");
        var list = SeedLoadingList(db, "ZG20260925008", "TCLU-008", customer.Id, deleted: true);
        var participant = SeedParticipant(db, list.Id, customer, primary: true);
        var source = SeedSourceExpense(db, "EXP-20260925-070", "TCLU-008", 700m);
        var batch = SeedBatch(db, "EAB-20260925-070", list, source, "按箱数", "按箱数", "CNY", 700m, 700m);
        SeedLine(db, batch, list, participant, "按箱数", "按箱数", "用户确认请求值", 7m, 100m, 700m);

        var d = await ContainerExpenseAllocationEvidenceService.GetForLoadingListAsync(db, list.Id);

        Assert.False(d.LoadingListAvailable);
        Assert.Contains("已删除", d.LoadingListAvailabilityText);
        Assert.True(d.HasEvidence);
        Assert.Equal(700m, Assert.Single(d.Groups).AllocatedTotal);
        Assert.True(d.InvalidLinkCount > 0);
        Assert.All(Assert.Single(d.ActiveBatches).Lines,
            l => Assert.Equal("loading-list-unavailable", l.LinkStatus));
        Assert.True(db.ContainerLoadingLists.AsNoTracking().Single(x => x.Id == list.Id).IsDeleted);
    }

    [Fact]
    public async Task Evidence_旧柜无批次_可读且不产生任何新记录()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "老客户");
        var list = SeedLoadingList(db, "ZG20260925009", "TCLU-009", customer.Id);
        SeedSourceExpense(db, "EXP-20260925-080", "TCLU-009", 500m);

        var batchesBefore = db.FinanceExpenseAllocationBatches.Count();
        var linesBefore = db.FinanceExpenseAllocationLines.Count();
        var expensesBefore = db.FinanceExpenses.Count();

        var d = await ContainerExpenseAllocationEvidenceService.GetForLoadingListAsync(db, list.Id);

        Assert.False(d.HasEvidence);
        Assert.Empty(d.Groups);
        Assert.Equal(ContainerExpenseAllocationEvidenceRules.MissingEvidenceText, d.EvidenceStatusText);
        Assert.Equal(batchesBefore, db.FinanceExpenseAllocationBatches.Count());
        Assert.Equal(linesBefore, db.FinanceExpenseAllocationLines.Count());
        Assert.Equal(expensesBefore, db.FinanceExpenses.Count());
    }


    // ==================== 3. 只读与来源记录非变更 ====================

    [Fact]
    public async Task Evidence_读取证据_不改写任何来源记录()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户A");
        var list = SeedLoadingList(db, "ZG20260925010", "TCLU-010", customer.Id);
        var participant = SeedParticipant(db, list.Id, customer, primary: true);
        var source = SeedSourceExpense(db, "EXP-20260925-090", "TCLU-010", 1200m);
        var batch = SeedBatch(db, "EAB-20260925-090", list, source, "按体积", "按体积", "CNY", 1200m, 1200m);
        SeedLine(db, batch, list, participant, "按体积", "按体积", "用户确认请求值", 12m, 100m, 1200m,
            expenseNo: "EXP-20260925-091");
        var settlement = SeedSettlement(db, "JS-20260925-001", list.Id, customer.Id, 1200m, 300m, 100m);

        var snapshot = new
        {
            Batches = db.FinanceExpenseAllocationBatches.AsNoTracking().Select(x => new
            {
                x.Id, x.BatchNo, x.Status, x.SourceAmount, x.AllocatedTotal, x.LineCount, x.Remark, x.VoidReason
            }).ToList(),
            Lines = db.FinanceExpenseAllocationLines.AsNoTracking().Select(x => new
            {
                x.Id, x.BatchId, x.CustomerId, x.AllocatedAmount, x.Ratio, x.BasisValue, x.Currency
            }).ToList(),
            Expenses = db.FinanceExpenses.AsNoTracking().Select(x => new
            {
                x.Id, x.ExpenseNo, x.Amount, x.Currency, x.RefType, x.RefNo, x.AllocationBatchNo, x.PaymentStatus
            }).ToList(),
            LoadingList = db.ContainerLoadingLists.AsNoTracking().Select(x => new
            {
                x.Id, x.LoadingListNo, x.ContainerNo, x.CustomerId, x.TotalCartons, x.Status, x.IsDeleted
            }).ToList(),
            Settlement = db.FinanceContainerSettlements.AsNoTracking().Select(x => new
            {
                x.Id, x.SettlementNo, x.TotalAmount, x.FreightCost, x.OtherCost, x.CustomerId, x.Status
            }).ToList(),
            Participants = db.ContainerLoadingListParticipants.AsNoTracking().Select(x => new
            {
                x.Id, x.CustomerId, x.IsPrimary, x.Status
            }).ToList(),
        };

        await ContainerExpenseAllocationEvidenceService.GetForLoadingListAsync(db, list.Id);
        await ContainerExpenseAllocationEvidenceService.GetForSettlementAsync(db, settlement.Id);
        await ContainerExpenseAllocationEvidenceService.ListAsync(
            db, new ContainerExpenseAllocationEvidenceQuery { ContainerNo = list.ContainerNo });

        Assert.Equal(snapshot.Batches, db.FinanceExpenseAllocationBatches.AsNoTracking().Select(x => new
        {
            x.Id, x.BatchNo, x.Status, x.SourceAmount, x.AllocatedTotal, x.LineCount, x.Remark, x.VoidReason
        }).ToList());
        Assert.Equal(snapshot.Lines, db.FinanceExpenseAllocationLines.AsNoTracking().Select(x => new
        {
            x.Id, x.BatchId, x.CustomerId, x.AllocatedAmount, x.Ratio, x.BasisValue, x.Currency
        }).ToList());
        Assert.Equal(snapshot.Expenses, db.FinanceExpenses.AsNoTracking().Select(x => new
        {
            x.Id, x.ExpenseNo, x.Amount, x.Currency, x.RefType, x.RefNo, x.AllocationBatchNo, x.PaymentStatus
        }).ToList());
        Assert.Equal(snapshot.LoadingList, db.ContainerLoadingLists.AsNoTracking().Select(x => new
        {
            x.Id, x.LoadingListNo, x.ContainerNo, x.CustomerId, x.TotalCartons, x.Status, x.IsDeleted
        }).ToList());
        Assert.Equal(snapshot.Settlement, db.FinanceContainerSettlements.AsNoTracking().Select(x => new
        {
            x.Id, x.SettlementNo, x.TotalAmount, x.FreightCost, x.OtherCost, x.CustomerId, x.Status
        }).ToList());
        Assert.Equal(snapshot.Participants, db.ContainerLoadingListParticipants.AsNoTracking().Select(x => new
        {
            x.Id, x.CustomerId, x.IsPrimary, x.Status
        }).ToList());
    }


    // ==================== 4. 工作台（显式筛选、分页有界、取值校验） ====================

    [Fact]
    public async Task Workspace_显式字段筛选与分页有界()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "客户A");
        var customerB = SeedCustomer(db, "C002", "客户B");

        var listA = SeedLoadingList(db, "ZG-A", "TCLU-A", customerA.Id);
        var pa = SeedParticipant(db, listA.Id, customerA, primary: true);
        var sourceA = SeedSourceExpense(db, "EXP-A", "TCLU-A", 100m);
        var batchA = SeedBatch(db, "EAB-A", listA, sourceA, "按体积", "按体积", "CNY", 100m, 100m);
        SeedLine(db, batchA, listA, pa, "按体积", "按体积", "用户确认请求值", 1m, 100m, 100m);

        var listB = SeedLoadingList(db, "ZG-B", "TCLU-B", customerB.Id);
        var pb = SeedParticipant(db, listB.Id, customerB, primary: true);
        var sourceB = SeedSourceExpense(db, "EXP-B", "TCLU-B", 200m, currency: "USD");
        var batchB = SeedBatch(db, "EAB-B", listB, sourceB, "按箱数", "按箱数", "USD", 200m, 200m);
        SeedLine(db, batchB, listB, pb, "按箱数", "按箱数", "用户确认请求值", 2m, 100m, 200m, currency: "USD");

        var listC = SeedLoadingList(db, "ZG-C", "TCLU-C", customerA.Id);
        var pc = SeedParticipant(db, listC.Id, customerA, primary: true);
        var sourceC = SeedSourceExpense(db, "EXP-C", "TCLU-C", 300m);
        var batchC = SeedBatch(db, "EAB-C", listC, sourceC, "按金额", "按金额", "CNY", 300m, 300m, status: 0,
            voidedAt: new DateTime(2026, 9, 26), voidReason: "更正");
        SeedLine(db, batchC, listC, pc, "按金额", "按金额", "用户确认请求值", 3m, 100m, 300m);

        var page1 = await ContainerExpenseAllocationEvidenceService.ListAsync(
            db, new ContainerExpenseAllocationEvidenceQuery { Page = 1, PageSize = 2 });
        Assert.Equal(3, page1.Total);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(new[] { listC.Id, listB.Id }, page1.Items.Select(x => x.LoadingListId).ToArray());
        Assert.Equal(2, page1.PageSize);
        Assert.False(page1.ScanBounded);
        Assert.Contains("不合并", page1.BasisAndCurrencyText);
        Assert.Contains("币种", page1.GroupingText);
        Assert.Contains("不新建表", page1.BoundaryText);
        Assert.Contains("操作性成本分配证据", page1.DisclaimerText);

        var page2 = await ContainerExpenseAllocationEvidenceService.ListAsync(
            db, new ContainerExpenseAllocationEvidenceQuery { Page = 2, PageSize = 2 });
        Assert.Single(page2.Items);
        Assert.Equal(listA.Id, page2.Items[0].LoadingListId);

        // 柜号 / 清单号 / 批次号：显式等值（统一大小写）
        var byContainer = await ContainerExpenseAllocationEvidenceService.ListAsync(
            db, new ContainerExpenseAllocationEvidenceQuery { ContainerNo = " tclu-b " });
        Assert.Equal(listB.Id, Assert.Single(byContainer.Items).LoadingListId);

        var byListNo = await ContainerExpenseAllocationEvidenceService.ListAsync(
            db, new ContainerExpenseAllocationEvidenceQuery { LoadingListNo = "zg-a" });
        Assert.Equal(listA.Id, Assert.Single(byListNo.Items).LoadingListId);

        var byBatchNo = await ContainerExpenseAllocationEvidenceService.ListAsync(
            db, new ContainerExpenseAllocationEvidenceQuery { BatchNo = "eab-c" });
        var voidedRow = Assert.Single(byBatchNo.Items);
        Assert.Equal(0, voidedRow.ActiveBatchCount);
        Assert.Equal(1, voidedRow.VoidedBatchCount);
        Assert.Equal(ContainerExpenseAllocationEvidenceRules.MissingEvidenceText, voidedRow.TotalsText);

        // 币种 / 状态显式筛选
        var byCurrency = await ContainerExpenseAllocationEvidenceService.ListAsync(
            db, new ContainerExpenseAllocationEvidenceQuery { Currency = "usd" });
        Assert.Equal(listB.Id, Assert.Single(byCurrency.Items).LoadingListId);

        var byStatus = await ContainerExpenseAllocationEvidenceService.ListAsync(
            db, new ContainerExpenseAllocationEvidenceQuery { Status = 0 });
        Assert.Equal(listC.Id, Assert.Single(byStatus.Items).LoadingListId);

        var excludeHistory = await ContainerExpenseAllocationEvidenceService.ListAsync(
            db, new ContainerExpenseAllocationEvidenceQuery { IncludeHistory = false });
        Assert.Equal(2, excludeHistory.Total);

        // 客户 Id：显式 Id 匹配（不按客户名合并）；关键字只命中显式列
        var byCustomer = await ContainerExpenseAllocationEvidenceService.ListAsync(
            db, new ContainerExpenseAllocationEvidenceQuery { CustomerId = customerA.Id, Status = 1 });
        Assert.Equal(listA.Id, Assert.Single(byCustomer.Items).LoadingListId);

        var byKeyword = await ContainerExpenseAllocationEvidenceService.ListAsync(
            db, new ContainerExpenseAllocationEvidenceQuery { Keyword = "客户B" });
        Assert.Equal(listB.Id, Assert.Single(byKeyword.Items).LoadingListId);
    }


    [Fact]
    public async Task Workspace_未知筛选取值_一律拒绝()
    {
        using var db = TestDbFactory.Create();

        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () =>
            ContainerExpenseAllocationEvidenceService.ListAsync(
                db, new ContainerExpenseAllocationEvidenceQuery { Status = 5 }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () =>
            ContainerExpenseAllocationEvidenceService.ListAsync(
                db, new ContainerExpenseAllocationEvidenceQuery { CustomerId = 0 }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () =>
            ContainerExpenseAllocationEvidenceService.ListAsync(
                db, new ContainerExpenseAllocationEvidenceQuery { Keyword = new string('k', 101) }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () =>
            ContainerExpenseAllocationEvidenceService.ListAsync(
                db, new ContainerExpenseAllocationEvidenceQuery { ContainerNo = new string('x', 101) }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () =>
            ContainerExpenseAllocationEvidenceService.GetForLoadingListAsync(db, 0));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () =>
            ContainerExpenseAllocationEvidenceService.GetForLoadingListAsync(db, 1, historyTake: 0));

        // 分页越界自动收敛（不是报错）
        var normalized = await ContainerExpenseAllocationEvidenceService.ListAsync(
            db, new ContainerExpenseAllocationEvidenceQuery { Page = 0, PageSize = 9999 });
        Assert.Equal(1, normalized.Page);
        Assert.Equal(ContainerExpenseAllocationEvidenceRules.MaxPageSize, normalized.PageSize);
    }


    // ==================== 5. 装柜结算单维度 ====================

    [Fact]
    public async Task Settlement_结算字段只读回显_单币种给出算术对照()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户A");
        var list = SeedLoadingList(db, "ZG20260925011", "TCLU-011", customer.Id);
        var participant = SeedParticipant(db, list.Id, customer, primary: true);
        var source = SeedSourceExpense(db, "EXP-20260925-100", "TCLU-011", 1000m);
        var batch = SeedBatch(db, "EAB-20260925-100", list, source, "按体积", "按体积", "CNY", 1000m, 1000m);
        SeedLine(db, batch, list, participant, "按体积", "按体积", "用户确认请求值", 10m, 100m, 1000m);
        var settlement = SeedSettlement(db, "JS-20260925-100", list.Id, customer.Id, 1200m, 150m, 50m);

        var d = await ContainerExpenseAllocationEvidenceService.GetForSettlementAsync(db, settlement.Id);

        Assert.Equal(settlement.Id, d.SettlementId);
        Assert.Equal("JS-20260925-100", d.SettlementNo);
        Assert.Equal("待提交", d.SettlementStatusText);
        Assert.Equal(1200m, d.TotalAmount);
        Assert.Equal(150m, d.FreightCost);
        Assert.Equal(50m, d.OtherCost);
        Assert.True(d.SettlementCustomerAvailable);
        Assert.Contains("客户A", d.SettlementCustomerDisplay);
        Assert.Contains("持久化原值", d.SettlementTotalsText);
        Assert.Contains("不参与结算金额计算", d.SettlementTotalsText);

        Assert.True(d.EvidenceAvailable);
        Assert.NotNull(d.Evidence);
        Assert.Equal(list.Id, d.Evidence!.LoadingListId);
        Assert.Equal("settlement", d.Evidence.ScopeType);
        Assert.Equal(settlement.Id, d.Evidence.ScopeId);
        Assert.Equal(1000m, Assert.Single(d.Evidence.Groups).AllocatedTotal);

        Assert.True(d.ComparisonComparable);
        Assert.Contains("算术对照", d.ComparisonText);
        Assert.Contains("200", d.ComparisonText);
        Assert.Contains("不构成结算差异", d.ComparisonText);

        // 结算单字段不被改写
        var stored = db.FinanceContainerSettlements.AsNoTracking().Single(x => x.Id == settlement.Id);
        Assert.Equal(1200m, stored.TotalAmount);
        Assert.Equal(150m, stored.FreightCost);
        Assert.Equal(50m, stored.OtherCost);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task Settlement_未关联清单或多币种_证据未知且不做合计对照()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户A");
        var unlinked = SeedSettlement(db, "JS-20260925-101", null, customer.Id, 500m);

        var d = await ContainerExpenseAllocationEvidenceService.GetForSettlementAsync(db, unlinked.Id);

        Assert.False(d.EvidenceAvailable);
        Assert.Null(d.Evidence);
        Assert.False(d.ComparisonComparable);
        Assert.Contains("未关联装柜清单", d.EvidenceUnavailableText);
        Assert.Contains("未知", d.EvidenceUnavailableText);
        Assert.Contains("不代表零费用或已结清", d.EvidenceUnavailableText);
        Assert.Contains(ContainerExpenseAllocationEvidenceRules.MissingEvidenceText, d.ComparisonText);

        // 多币种：不做任何合计对照
        var list = SeedLoadingList(db, "ZG20260925012", "TCLU-012", customer.Id);
        var participant = SeedParticipant(db, list.Id, customer, primary: true);
        var cny = SeedSourceExpense(db, "EXP-20260925-110", "TCLU-012", 100m);
        var batchCny = SeedBatch(db, "EAB-20260925-110", list, cny, "按体积", "按体积", "CNY", 100m, 100m);
        SeedLine(db, batchCny, list, participant, "按体积", "按体积", "用户确认请求值", 1m, 100m, 100m);
        var usd = SeedSourceExpense(db, "EXP-20260925-111", "TCLU-012", 50m, currency: "USD");
        var batchUsd = SeedBatch(db, "EAB-20260925-111", list, usd, "按体积", "按体积", "USD", 50m, 50m);
        SeedLine(db, batchUsd, list, participant, "按体积", "按体积", "用户确认请求值", 1m, 100m, 50m,
            currency: "USD");
        var multi = SeedSettlement(db, "JS-20260925-102", list.Id, customer.Id, 150m);

        var multiDto = await ContainerExpenseAllocationEvidenceService.GetForSettlementAsync(db, multi.Id);
        Assert.True(multiDto.EvidenceAvailable);
        Assert.Equal(2, multiDto.Evidence!.Groups.Count);
        Assert.False(multiDto.ComparisonComparable);
        Assert.Contains("不合并、不换算", multiDto.ComparisonText);
    }

    [Fact]
    public async Task Settlement_不存在或已删除_返回业务异常()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户A");
        var deleted = SeedSettlement(db, "JS-20260925-103", null, customer.Id, 100m, deleted: true);

        await AssertBusinessAsync(ErrorCodes.NotFound, () =>
            ContainerExpenseAllocationEvidenceService.GetForSettlementAsync(db, 999999));
        await AssertBusinessAsync(ErrorCodes.NotFound, () =>
            ContainerExpenseAllocationEvidenceService.GetForSettlementAsync(db, deleted.Id));
    }


    // ==================== 6. 有界查询与只读（计数代理） ====================

    [Fact]
    public async Task Evidence_有界查询_访问次数不随行数变化且只读不写库()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户A");
        var list = SeedLoadingList(db, "ZG20260925020", "TCLU-020", customer.Id);
        var participant = SeedParticipant(db, list.Id, customer, primary: true);
        var source = SeedSourceExpense(db, "EXP-20260925-200", "TCLU-020", 600m);
        var batch = SeedBatch(db, "EAB-20260925-200", list, source, "按体积", "按体积", "CNY", 600m, 600m);
        SeedLine(db, batch, list, participant, "按体积", "按体积", "用户确认请求值", 6m, 100m, 600m);

        var small = CountingDbContext.Wrap(db);
        var smallDto = await ContainerExpenseAllocationEvidenceService.GetForLoadingListAsync(small.Proxy, list.Id);
        var smallReads = small.DatasetReads;

        Assert.Equal(1, smallDto.ActiveBatchCount);
        Assert.Equal(0, small.WriteCalls);
        Assert.Contains(nameof(IErpDbContext.FinanceExpenseAllocationBatches), small.ReadProperties);
        Assert.Contains(nameof(IErpDbContext.ContainerLoadingLists), small.ReadProperties);

        // 追加 5 个批次 / 10 行：查询次数必须恒定（无逐行数据库访问）
        for (var i = 0; i < 5; i++)
        {
            var extraSource = SeedSourceExpense(db, $"EXP-20260925-2{i:00}", "TCLU-020", 100m);
            var extraBatch = SeedBatch(db, $"EAB-20260925-2{i:00}", list, extraSource,
                "按箱数", "按箱数", "CNY", 100m, 100m);
            SeedLine(db, extraBatch, list, participant, "按箱数", "按箱数", "用户确认请求值", 1m, 50m, 50m);
            SeedLine(db, extraBatch, list, participant, "按箱数", "按箱数", "用户确认请求值", 1m, 50m, 50m);
        }

        var large = CountingDbContext.Wrap(db);
        var largeDto = await ContainerExpenseAllocationEvidenceService.GetForLoadingListAsync(large.Proxy, list.Id);
        var largeReads = large.DatasetReads;

        Assert.Equal(6, largeDto.ActiveBatchCount);
        Assert.Equal(smallReads, largeReads);
        Assert.Equal(0, large.WriteCalls);

        // 工作台同样有界：分页行数变化不改变数据集访问次数
        var pageSmall = CountingDbContext.Wrap(db);
        await ContainerExpenseAllocationEvidenceService.ListAsync(
            pageSmall.Proxy, new ContainerExpenseAllocationEvidenceQuery { PageSize = 1 });
        var pageSmallReads = pageSmall.DatasetReads;

        var pageLarge = CountingDbContext.Wrap(db);
        await ContainerExpenseAllocationEvidenceService.ListAsync(
            pageLarge.Proxy, new ContainerExpenseAllocationEvidenceQuery { PageSize = 50 });
        Assert.Equal(pageSmallReads, pageLarge.DatasetReads);
        Assert.Equal(0, pageSmall.WriteCalls);
        Assert.Equal(0, pageLarge.WriteCalls);
    }

    /// <summary>
    /// 只读计数上下文代理（<see cref="DispatchProxy"/>）：记录访问的数据集（<c>DbSet</c> 属性）名称与写入次数，
    /// 用于断言「分页有界 / 无逐行查库」与「只读不写库」；不改动生产代码。
    /// </summary>
    public class CountingDbContext : DispatchProxy
    {
        private IErpDbContext _inner = null!;

        /// <summary>包装后的上下文（服务 / 控制器按 <see cref="IErpDbContext"/> 使用）</summary>
        public IErpDbContext Proxy { get; private set; } = null!;

        /// <summary>数据集（<c>DbSet</c> 属性）访问次数：即本次查询实际发起的数据集访问次数</summary>
        public int DatasetReads => ReadProperties.Count;

        /// <summary>被访问的数据集属性名（本模块预期只有装柜清单 / 批次 / 分摊行 / 客户 / 参与方 / 费用单）</summary>
        public List<string> ReadProperties { get; } = new();

        /// <summary><c>SaveChangesAsync</c> 调用次数：只读库恒为 0</summary>
        public int WriteCalls { get; private set; }

        /// <summary>包装一个真实上下文（计数从返回对象上读取）</summary>
        public static CountingDbContext Wrap(IErpDbContext inner)
        {
            var proxy = DispatchProxy.Create<IErpDbContext, CountingDbContext>();
            var counting = (CountingDbContext)(object)proxy;
            counting._inner = inner;
            counting.Proxy = proxy;
            return counting;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            if (targetMethod.Name == nameof(IErpDbContext.SaveChangesAsync))
            {
                WriteCalls++;
                return _inner.SaveChangesAsync(args is { Length: > 0 } ? (CancellationToken)args[0]! : default);
            }

            if (targetMethod.Name.StartsWith("get_", StringComparison.Ordinal))
            {
                ReadProperties.Add(targetMethod.Name[4..]);
            }

            return targetMethod.Invoke(_inner, args);
        }
    }


    // ==================== 7. 纯规则文案与取值校验 ====================

    [Fact]
    public void Rules_文案与取值校验_非法取值一律拒绝()
    {
        // 显式等值筛选：去空白 + 统一大写；空 = 不过滤；超长拒绝
        Assert.Equal("TCLU-1", ContainerExpenseAllocationEvidenceRules.NormalizeEqualsFilter(" tclu-1 ", "柜号"));
        Assert.Null(ContainerExpenseAllocationEvidenceRules.NormalizeEqualsFilter("  ", "柜号"));
        Assert.Equal("usd", ContainerExpenseAllocationEvidenceRules.NormalizeKeyword(" usd "));
        Assert.Null(ContainerExpenseAllocationEvidenceRules.NormalizeKeyword(null));
        Assert.Equal("USD", ContainerExpenseAllocationEvidenceRules.NormalizeCurrencyFilter(" usd "));
        Assert.Null(ContainerExpenseAllocationEvidenceRules.NormalizeCurrencyFilter(""));
        Assert.Throws<BusinessException>(() =>
            ContainerExpenseAllocationEvidenceRules.NormalizeEqualsFilter(new string('x', 101), "柜号"));
        Assert.Throws<BusinessException>(() =>
            ContainerExpenseAllocationEvidenceRules.NormalizeHistoryTake(0));
        Assert.Throws<BusinessException>(() =>
            ContainerExpenseAllocationEvidenceRules.NormalizeHistoryTake(
                ContainerExpenseAllocationEvidenceRules.MaxHistoryTake + 1));
        Assert.Throws<BusinessException>(() =>
            ContainerExpenseAllocationEvidenceRules.NormalizeCustomerIdFilter(0));
        Assert.Equal(3, ContainerExpenseAllocationEvidenceRules.EnsureScopeId(3, "装柜清单"));
        Assert.Throws<BusinessException>(() =>
            ContainerExpenseAllocationEvidenceRules.EnsureScopeId(0, "装柜清单"));
        Assert.Null(ContainerExpenseAllocationEvidenceRules.NormalizeStatusFilter(null));
        Assert.Equal(1, ContainerExpenseAllocationEvidenceRules.NormalizeStatusFilter(1));
        Assert.Throws<BusinessException>(() =>
            ContainerExpenseAllocationEvidenceRules.NormalizeStatusFilter(7));

        // 批次状态安全文案：未知状态照实回显，不误判为有效
        Assert.Equal("有效", ContainerExpenseAllocationEvidenceRules.BatchStatusTextSafe(1));
        Assert.Equal("已作废", ContainerExpenseAllocationEvidenceRules.BatchStatusTextSafe(0));
        Assert.Equal("未知状态（原值 7）", ContainerExpenseAllocationEvidenceRules.BatchStatusTextSafe(7));

        // 金额 / 币种精度口径
        Assert.Equal("1000.00 CNY", ContainerExpenseAllocationEvidenceRules.AmountText(1000m, "CNY"));
        Assert.Equal("1000 JPY", ContainerExpenseAllocationEvidenceRules.AmountText(1000m, "JPY"));
        Assert.Contains("0 位小数", ContainerExpenseAllocationEvidenceRules.CurrencyLabel("JPY", 0));
        Assert.Contains("2 位小数", ContainerExpenseAllocationEvidenceRules.CurrencyLabel("CNY", 2));
    }


    [Fact]
    public void Rules_链接判定与证据文案()
    {
        // 链接判定优先级：清单不可用 > 柜号不一致 > 参与方缺失 > 参与方越界 > 参与方客户不一致 > 客户不可用 > 有效
        Assert.Equal("loading-list-unavailable",
            ContainerExpenseAllocationEvidenceRules.EvaluateLineLink(false, false, false, false, false, false).Status);
        Assert.Equal("container-mismatch",
            ContainerExpenseAllocationEvidenceRules.EvaluateLineLink(true, false, false, false, false, false).Status);
        Assert.Equal("participant-missing",
            ContainerExpenseAllocationEvidenceRules.EvaluateLineLink(true, true, false, false, false, false).Status);
        Assert.Equal("participant-foreign",
            ContainerExpenseAllocationEvidenceRules.EvaluateLineLink(true, true, true, false, false, false).Status);
        Assert.Equal("participant-customer-mismatch",
            ContainerExpenseAllocationEvidenceRules.EvaluateLineLink(true, true, true, true, false, false).Status);
        Assert.Equal("customer-unavailable",
            ContainerExpenseAllocationEvidenceRules.EvaluateLineLink(true, true, true, true, true, false).Status);
        var ok = ContainerExpenseAllocationEvidenceRules.EvaluateLineLink(true, true, true, true, true, true);
        Assert.False(ok.Invalid);
        Assert.Contains("链接有效", ok.Text);
        Assert.Contains("不可校验", ContainerExpenseAllocationEvidenceRules.ContainerLinkText(false, false));
        Assert.Contains("柜号链接一致", ContainerExpenseAllocationEvidenceRules.ContainerLinkText(true, true));
        Assert.Contains("不按柜号改派", ContainerExpenseAllocationEvidenceRules.ContainerLinkText(true, false));

        // 证据状态 / 缺失说明 / 未分摊说明 / 边界与免责
        Assert.Equal(ContainerExpenseAllocationEvidenceRules.MissingEvidenceText,
            ContainerExpenseAllocationEvidenceRules.EvidenceStatusText(0, 0, 0));
        Assert.Contains("1 个有效分摊批次",
            ContainerExpenseAllocationEvidenceRules.EvidenceStatusText(1, 0, 0));
        Assert.Contains("未登记有效分摊批次",
            ContainerExpenseAllocationEvidenceRules.EvidenceStatusText(0, 1, 0));
        Assert.Contains("不代表费用为零", ContainerExpenseAllocationEvidenceRules.EvidenceMissingText(0));
        Assert.Contains("不代表已结清", ContainerExpenseAllocationEvidenceRules.EvidenceMissingText(0));
        Assert.Contains("不代表费用为零", ContainerExpenseAllocationEvidenceRules.NotZeroText);
        Assert.Contains("无批次留痕", ContainerExpenseAllocationEvidenceRules.LegacyAllocationText(2));
        Assert.Contains("不改派", ContainerExpenseAllocationEvidenceRules.InvalidLinkText(1));
        Assert.Contains("无失效链接", ContainerExpenseAllocationEvidenceRules.InvalidLinkText(0));
        Assert.Contains("不是零费用", ContainerExpenseAllocationEvidenceRules.ContainerNoMissingText);
        Assert.Contains("未知", ContainerExpenseAllocationEvidenceRules.UnallocatedContextText(false, 0, 0));
        Assert.Contains("不代表费用为零",
            ContainerExpenseAllocationEvidenceRules.UnallocatedContextText(true, 0, 0));
        Assert.Contains("不新建表", ContainerExpenseAllocationEvidenceRules.BoundaryText);
        Assert.Contains("不是会计记账", ContainerExpenseAllocationEvidenceRules.DisclaimerText);
        Assert.Contains("不是结算确认", ContainerExpenseAllocationEvidenceRules.DisclaimerText);
        Assert.Contains("只读", ContainerExpenseAllocationEvidenceRules.ReadOnlyText);
        Assert.Contains("不合并", ContainerExpenseAllocationEvidenceRules.BasisAndCurrencyText);
        Assert.Contains("不合并", ContainerExpenseAllocationEvidenceRules.GroupingText);
        Assert.Contains("不换算", ContainerExpenseAllocationEvidenceRules.MultiCurrencyNoTotalText);

        // 结算单算术对照：无证据 / 多币种 / 单币种三种口径
        Assert.Contains(ContainerExpenseAllocationEvidenceRules.MissingEvidenceText,
            ContainerExpenseAllocationEvidenceRules.SettlementComparisonText(false, string.Empty, 100m, 0m, 0));
        Assert.Contains(ContainerExpenseAllocationEvidenceRules.MultiCurrencyNoTotalText,
            ContainerExpenseAllocationEvidenceRules.SettlementComparisonText(true, string.Empty, 100m, 60m, 2));
        var comparison = ContainerExpenseAllocationEvidenceRules.SettlementComparisonText(true, "CNY", 1200m, 1000m, 1);
        Assert.Contains("算术对照", comparison);
        Assert.Contains("200", comparison);
        Assert.Contains("不构成结算差异", comparison);
    }


    // ==================== 8. 接线与审计契约 ====================

    [Fact]
    public void Frontend_分摊证据入口与脚本已接线且只读()
    {
        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/container-expense-allocation-evidence.js", index);
        Assert.Contains("ERP-060", index);

        var js = File.ReadAllText(
            RepoFile("src", "ERP.Api", "wwwroot", "js", "container-expense-allocation-evidence.js"));
        Assert.Contains("showContainerAllocationEvidence", js);
        Assert.Contains("openContainerAllocationEvidenceWorkspace", js);
        Assert.Contains("/expense-allocation-evidence", js);
        Assert.Contains("/api/container/expense-allocation-evidence", js);
        Assert.Contains("无（未登记任何有效分摊批次）", js);
        Assert.Contains("只读", js);
        Assert.DoesNotContain("POST", js);
        Assert.DoesNotContain("PUT", js);
        Assert.DoesNotContain("DELETE", js);

        // 两个模块的入口（装柜清单 / 装柜结算单）
        var doc2 = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "modules-doc2.js"));
        Assert.Contains("showContainerAllocationEvidence", doc2);
        Assert.Contains("openContainerAllocationEvidenceWorkspace()", doc2);

        var finance = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "modules-finance.js"));
        Assert.Contains("showContainerAllocationEvidence", finance);
        Assert.Contains("openContainerAllocationEvidenceWorkspace()", finance);
        Assert.Contains("container-settlement", finance);

        // 控制器与两个工作流入口的只读端点
        var controller = File.ReadAllText(
            RepoFile("src", "ERP.Api", "Controllers", "ContainerExpenseAllocationEvidenceController.cs"));
        Assert.Contains("api/container/expense-allocation-evidence", controller);
        Assert.Contains("loading-lists/{loadingListId:long}", controller);
        Assert.Contains("settlements/{settlementId:long}", controller);
        Assert.Contains("[HttpGet", controller);
        Assert.DoesNotContain("[HttpPost", controller);

        foreach (var file in new[] { "ContainerLoadingListController.cs", "FinanceSettlementControllers.cs" })
        {
            var source = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", file));
            Assert.Contains("{id:long}/expense-allocation-evidence", source);
        }

        // 文档与实现口径同源
        var doc = File.ReadAllText(RepoFile("docs", "装柜费用分摊证据视图说明.md"));
        Assert.Contains("ERP-060", doc);
        Assert.Contains("只读", doc);
        Assert.Contains("无（未登记任何有效分摊批次）", doc);
        Assert.Contains("不合并", doc);
        Assert.Contains("不记账", doc);
    }

    [Fact]
    public void ERP060审计_只读派生_不新增表不改结构也不回写来源()
    {
        // IErpDbContext 里没有新增「分摊证据」相关数据集（只读派生，无新表）
        var dbSets = typeof(IErpDbContext).GetProperties()
            .Where(p => p.PropertyType.IsGenericType
                        && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(p => p.Name)
            .ToList();
        Assert.DoesNotContain(dbSets, name => name.Contains("AllocationEvidence", StringComparison.Ordinal));
        Assert.DoesNotContain(dbSets, name => name.Contains("ContainerAllocation", StringComparison.Ordinal));

        // 既有两套权威登记册仍在（本模块只读它们，不复制、不新增第三套）
        Assert.Contains(nameof(IErpDbContext.FinanceExpenseAllocationBatches), dbSets);
        Assert.Contains(nameof(IErpDbContext.FinanceExpenseAllocationLines), dbSets);

        // 服务层只读：没有任何写入调用（落库 / 数据集增删改都没有；仅内存集合与只读查询）
        var service = File.ReadAllText(RepoFile(
            "src", "ERP.Application", "Services", "ContainerExpenseAllocationEvidenceService.cs"));
        Assert.DoesNotContain("SaveChangesAsync(", service);
        Assert.DoesNotContain("SaveChanges()", service);
        Assert.DoesNotMatch(
            @"(?i)\bdb\.[A-Za-z]+\.(Add|AddRange|Remove|RemoveRange|Update|UpdateRange)\b", service);
        Assert.Contains("AsNoTracking", service);
        Assert.Contains("只读", service);

        // 规则层明确「只读 / 不合并 / 不改派」口径
        var rules = File.ReadAllText(RepoFile(
            "src", "ERP.Application", "Services", "ContainerExpenseAllocationEvidenceRules.cs"));
        Assert.Contains("不新建表", rules);
        Assert.Contains("不合并", rules);
        Assert.Contains("不改派", rules);

        // 结构脚本不包含任何分摊证据相关 DDL（本任务不新增表 / 列、不做任何回填）
        var schema = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs"));
        Assert.DoesNotContain("AllocationEvidence", schema);
        Assert.DoesNotContain("ContainerExpenseAllocationEvidence", schema);

        // 领域层与基础设施层没有新增「分摊证据」实体（本任务只改 Application / Api / 测试 / 文档）
        Assert.DoesNotContain(
            typeof(ERP.Domain.Entities.FinanceExpense).Assembly.GetTypes(),
            t => t.Name.Contains("AllocationEvidence", StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(ErpDbContext).Assembly.GetTypes(),
            t => t.Name.Contains("AllocationEvidence", StringComparison.Ordinal));

        // 证据类型只存在于应用层（DTO / 规则 / 服务）
        Assert.Equal("ERP.Application", typeof(ContainerExpenseAllocationEvidenceRules).Assembly.GetName().Name);
        Assert.Equal("ERP.Application", typeof(ContainerExpenseAllocationEvidenceDto).Assembly.GetName().Name);
    }

}
