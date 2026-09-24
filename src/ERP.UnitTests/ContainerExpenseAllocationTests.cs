using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 装柜费用分摊批次单元测试（ERP-042）。覆盖：四种受支持基数（按体积 / 按重量 / 按箱数 / 按金额）
/// 与整柜法、币种精度与余差稳定归属、基准缺失 / 合计为 0 / 为负 / 越界参与方的一律拒绝、
/// 来源费用可分摊资格（非柜级 / 单号不一致 / 金额非正 / 已属批次 / 历史分摊行）、
/// 生成批次与逐行留痕、重复生成防护、事务性（持久化失败不留部分行）、作废保留历史、
/// 历史分摊行的只读辨识与不回填、批次台账分页与状态过滤、
/// 非变更边界（装柜明细 / 参与方 / 单证 / 库存 / 库存流水 / 订单 / 订柜跟踪值）与结构契约。
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何 SQL / 部署脚本。
/// </summary>
public class ContainerExpenseAllocationTests
{
    // ==================== 0. 测试脚手架 ====================

    private static ExpenseBillController BuildController(ErpDbContext db)
        => new(new GenericService<FinanceExpense>(db), db);

    /// <summary>共享同一内存库的上下文选项（用于「写入失败 → 换新上下文断言无残留」）</summary>
    private static DbContextOptions<ErpDbContext> SharedOptions(string name)
        => new DbContextOptionsBuilder<ErpDbContext>().UseInMemoryDatabase(name).Options;

    /// <summary>保存即失败的内存库上下文：验证「持久化失败不保留任何部分行」</summary>
    private sealed class FailingSaveDbContext : ErpDbContext
    {
        public FailingSaveDbContext(DbContextOptions<ErpDbContext> options) : base(options) { }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("模拟持久化失败");

        public override int SaveChanges() => throw new InvalidOperationException("模拟持久化失败");
    }

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
        ErpDbContext db, string listNo, string containerNo, long customerId,
        decimal cartons = 10m, decimal weight = 500m, decimal volume = 3.5m)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = listNo,
            LoadingDate = new DateTime(2026, 9, 25),
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

    private static FinanceExpense SeedSourceExpense(
        ErpDbContext db, string expenseNo, string containerNo, decimal amount,
        string currency = "CNY", decimal exchangeRate = 1m, string refType = "拼柜",
        string allocationBase = "不分摊", decimal allocationRatio = 0m, decimal allocatedAmount = 0m)
    {
        var expense = new FinanceExpense
        {
            ExpenseNo = expenseNo,
            ExpenseDate = new DateTime(2026, 9, 25),
            ExpenseType = "报关费",
            Amount = amount,
            Currency = currency,
            ExchangeRate = exchangeRate,
            AmountCny = amount,
            RefType = refType,
            RefNo = containerNo,
            AllocationBase = allocationBase,
            AllocationRatio = allocationRatio,
            AllocatedAmount = allocatedAmount,
            PaymentStatus = "未付"
        };
        db.FinanceExpenses.Add(expense);
        db.SaveChanges();
        return expense;
    }

    private static ContainerExpenseAllocationRequest Request(
        long sourceExpenseId, long loadingListId, string method = "按体积",
        long? wholeTargetId = null, string wholeBasisKind = "箱数", string remark = "",
        params (long ParticipantId, decimal? BasisValue)[] lines)
        => new()
        {
            SourceExpenseId = sourceExpenseId,
            LoadingListId = loadingListId,
            AllocationMethod = method,
            WholeContainerBasisKind = wholeBasisKind,
            WholeContainerParticipantId = wholeTargetId,
            Remark = remark,
            Lines = lines.Select(l => new ContainerExpenseAllocationBasisDto
            {
                ParticipantId = l.ParticipantId,
                BasisValue = l.BasisValue
            }).ToList()
        };

    /// <summary>断言成功响应并取出数据（业务码必须为 0）</summary>
    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
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

    // ==================== 1. 预览：四种基数、余差与币种精度 ====================

    [Theory]
    [InlineData("按体积")]
    [InlineData("按重量")]
    [InlineData("按箱数")]
    [InlineData("按金额")]
    public async Task Preview_四种受支持基数_都按各参与方基准分摊且合计等于来源金额(string method)
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "拼柜客户A");
        var customerB = SeedCustomer(db, "C002", "拼柜客户B");
        var list = SeedLoadingList(db, "ZG20260925001", "TCLU-001", customerA.Id);
        var participantA = SeedParticipant(db, list.Id, customerA, primary: true);
        var participantB = SeedParticipant(db, list.Id, customerB);
        var source = SeedSourceExpense(db, "EXP-20260925-001", "TCLU-001", 1000m);
        var ctl = BuildController(db);

        var preview = AssertOk<ContainerExpenseAllocationPreviewDto>(
            await ctl.AllocationPreview(Request(source.Id, list.Id, method, null, "箱数", "",
                (participantA.Id, 30m), (participantB.Id, 70m))));

        Assert.Equal(method, preview.AllocationMethod);
        Assert.Equal("用户确认请求值", preview.BasisSource);
        Assert.Equal(2, preview.Lines.Count);
        Assert.Equal(1000m, preview.AllocatedTotal);
        Assert.Equal(100m, preview.RatioTotal);
        Assert.Equal(2, preview.AmountPrecision);
        Assert.Contains("不改写", preview.BoundaryText);

        var lineA = Assert.Single(preview.Lines, l => l.ParticipantId == participantA.Id);
        Assert.False(lineA.RemainderCarrier);          // 基准值最大者承接余差（此处为客户B）
        Assert.Equal(300m, lineA.AllocatedAmount);
        Assert.Equal(30m, lineA.Ratio);
        Assert.Equal(30m, lineA.BasisValue);
        Assert.True(lineA.IsPrimary);
        Assert.Equal("C001", lineA.CustomerCode);

        var lineB = Assert.Single(preview.Lines, l => l.ParticipantId == participantB.Id);
        Assert.Equal(700m, lineB.AllocatedAmount);
        Assert.True(lineB.RemainderCarrier);
        Assert.Equal("拼柜客户B", lineB.CustomerName);
    }

    [Fact]
    public async Task Preview_余差归基准值最大的参与方_并列时归参与方Id较小者()
    {
        using var db = TestDbFactory.Create();
        var legacy = SeedCustomer(db, "C001", "老客户");
        var customerA = SeedCustomer(db, "C002", "客户A");
        var customerB = SeedCustomer(db, "C003", "客户B");
        var customerC = SeedCustomer(db, "C004", "客户C");
        var list = SeedLoadingList(db, "ZG20260925002", "TCLU-002", legacy.Id);
        var pa = SeedParticipant(db, list.Id, customerA);
        var pb = SeedParticipant(db, list.Id, customerB);
        var pc = SeedParticipant(db, list.Id, customerC);
        var source = SeedSourceExpense(db, "EXP-20260925002", "TCLU-002", 100m);
        var ctl = BuildController(db);

        // 三点等权：33.33 / 33.33 / 33.34，余差归基准并列中 Id 最小者（与提交顺序无关）
        var preview = AssertOk<ContainerExpenseAllocationPreviewDto>(
            await ctl.AllocationPreview(Request(source.Id, list.Id, "按箱数", null, "箱数", "",
                (pc.Id, 1m), (pb.Id, 1m), (pa.Id, 1m))));

        var carrier = Assert.Single(preview.Lines, l => l.RemainderCarrier);
        Assert.Equal(pa.Id, carrier.ParticipantId);
        Assert.Equal(pa.Id, preview.RemainderCarrierParticipantId);
        Assert.Equal(33.34m, carrier.AllocatedAmount);
        Assert.Equal(100m, preview.AllocatedTotal);
        Assert.Equal(0.01m, preview.Remainder);
        Assert.Contains("基准值最大", preview.RemainderRuleText);

        // 基准值最大者优先承接余差
        var preview2 = AssertOk<ContainerExpenseAllocationPreviewDto>(
            await ctl.AllocationPreview(Request(source.Id, list.Id, "按箱数", null, "箱数", "",
                (pa.Id, 1m), (pb.Id, 1m), (pc.Id, 8m))));
        Assert.Equal(pc.Id, preview2.RemainderCarrierParticipantId);
        Assert.Equal(80m, Assert.Single(preview2.Lines, l => l.ParticipantId == pc.Id).AllocatedAmount);
        Assert.Equal(100m, preview2.AllocatedTotal);
    }

    [Fact]
    public async Task Preview_无小数币种按0位取整且行合计仍等于来源金额()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "客户A");
        var customerB = SeedCustomer(db, "C002", "客户B");
        var customerC = SeedCustomer(db, "C003", "客户C");
        var list = SeedLoadingList(db, "ZG20260925003", "TCLU-003", customerA.Id);
        var pa = SeedParticipant(db, list.Id, customerA);
        var pb = SeedParticipant(db, list.Id, customerB);
        var pc = SeedParticipant(db, list.Id, customerC);
        var source = SeedSourceExpense(db, "EXP-20260925003", "TCLU-003", 100m, currency: "JPY", exchangeRate: 0.05m);
        var ctl = BuildController(db);

        var preview = AssertOk<ContainerExpenseAllocationPreviewDto>(
            await ctl.AllocationPreview(Request(source.Id, list.Id, "按金额", null, "箱数", "",
                (pa.Id, 1m), (pb.Id, 1m), (pc.Id, 1m))));

        Assert.Equal("JPY", preview.Currency);
        Assert.Equal(0, preview.AmountPrecision);
        Assert.Equal(100m, preview.AllocatedTotal);
        Assert.All(preview.Lines, l => Assert.Equal(0m, decimal.Remainder(l.AllocatedAmount, 1m)));
        Assert.Equal(34m, Assert.Single(preview.Lines, l => l.ParticipantId == pa.Id).AllocatedAmount);
        Assert.Equal(5m, preview.SourceAmountCny);      // 100 JPY × 0.05
    }

    [Fact]
    public async Task Preview_只读_不写库且不改写来源费用()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "客户A");
        var customerB = SeedCustomer(db, "C002", "客户B");
        var list = SeedLoadingList(db, "ZG20260925004", "TCLU-004", customerA.Id);
        var pa = SeedParticipant(db, list.Id, customerA);
        var pb = SeedParticipant(db, list.Id, customerB);
        var source = SeedSourceExpense(db, "EXP-20260925004", "TCLU-004", 500m);
        var ctl = BuildController(db);

        var beforeExpense = db.FinanceExpenses.Count();
        AssertOk<ContainerExpenseAllocationPreviewDto>(
            await ctl.AllocationPreview(Request(source.Id, list.Id, "按体积", null, "箱数", "",
                (pa.Id, 3.5m), (pb.Id, 1.5m))));

        Assert.Equal(beforeExpense, db.FinanceExpenses.Count());
        Assert.Empty(db.FinanceExpenseAllocationBatches);
        Assert.Empty(db.FinanceExpenseAllocationLines);
        var reloaded = db.FinanceExpenses.Single(x => x.Id == source.Id);
        Assert.Equal(500m, reloaded.Amount);
        Assert.Equal("未付", reloaded.PaymentStatus);
        Assert.Equal(string.Empty, reloaded.AllocationBatchNo);
        Assert.Null(reloaded.AllocationSourceExpenseId);
    }

    // ==================== 2. 预览：显式基准与整柜法校验 ====================

    [Fact]
    public async Task Preview_参与方基准缺失或合计为零_一律拒绝而不是猜测()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "客户A");
        var customerB = SeedCustomer(db, "C002", "客户B");
        var list = SeedLoadingList(db, "ZG20260925005", "TCLU-005", customerA.Id);
        var pa = SeedParticipant(db, list.Id, customerA);
        var pb = SeedParticipant(db, list.Id, customerB);
        var source = SeedSourceExpense(db, "EXP-20260925005", "TCLU-005", 800m);
        var ctl = BuildController(db);

        // 缺少参与方基准值（未提交 / 显式 null）
        var missing = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () =>
            ctl.AllocationPreview(Request(source.Id, list.Id, "按体积", null, "箱数", "", (pa.Id, 10m))));
        Assert.Contains("缺少以下参与方的基准值", missing.Message);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () =>
            ctl.AllocationPreview(Request(source.Id, list.Id, "按体积", null, "箱数", "",
                (pa.Id, 10m), (pb.Id, null))));

        // 基准值合计为 0：缺少可用证据，不按经验推断
        var zero = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () =>
            ctl.AllocationPreview(Request(source.Id, list.Id, "按体积", null, "箱数", "",
                (pa.Id, 0m), (pb.Id, 0m))));
        Assert.Contains("基准值合计为 0", zero.Message);

        // 负基准
        var negative = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () =>
            ctl.AllocationPreview(Request(source.Id, list.Id, "按体积", null, "箱数", "",
                (pa.Id, -1m), (pb.Id, 5m))));
        Assert.Contains("负数", negative.Message);

        // 拒绝后没有任何写入
        Assert.Empty(db.FinanceExpenseAllocationBatches);
        Assert.Empty(db.FinanceExpenseAllocationLines);
        Assert.Equal(1, db.FinanceExpenses.Count());
    }

    [Fact]
    public async Task Preview_越界或停用的参与方与方法不受支持_都被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "客户A");
        var customerB = SeedCustomer(db, "C002", "客户B");
        var customerC = SeedCustomer(db, "C003", "客户C");
        var list = SeedLoadingList(db, "ZG20260925006", "TCLU-006", customerA.Id);
        var pa = SeedParticipant(db, list.Id, customerA);
        var disabled = SeedParticipant(db, list.Id, customerB, status: 0);
        var pb = SeedParticipant(db, list.Id, customerC);
        var source = SeedSourceExpense(db, "EXP-20260925006", "TCLU-006", 600m);
        var ctl = BuildController(db);

        // 本柜以外的参与方（含已停用的历史参与方）
        var foreign = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () =>
            ctl.AllocationPreview(Request(source.Id, list.Id, "按体积", null, "箱数", "",
                (pa.Id, 1m), (disabled.Id, 1m))));
        Assert.Contains("不属于该装柜清单启用中的参与方", foreign.Message);

        var unknown = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () =>
            ctl.AllocationPreview(Request(source.Id, list.Id, "按体积", null, "箱数", "",
                (pa.Id, 1m), (999999L, 1m))));
        Assert.Contains("不属于该装柜清单启用中的参与方", unknown.Message);

        // 重复提交同一参与方
        await AssertBusinessAsync(ErrorCodes.Duplicate, () =>
            ctl.AllocationPreview(Request(source.Id, list.Id, "按体积", null, "箱数", "",
                (pa.Id, 1m), (pa.Id, 2m), (pb.Id, 1m))));

        // 不受支持的方法
        var method = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () =>
            ctl.AllocationPreview(Request(source.Id, list.Id, "按心情", null, "箱数", "",
                (pa.Id, 1m), (pb.Id, 1m))));
        Assert.Contains("不受支持", method.Message);

        // 全部拒绝：装柜清单、参与方、费用单都没有被改动
        Assert.Equal(1, db.ContainerLoadingLists.Count());
        Assert.Equal(3, db.ContainerLoadingListParticipants.Count());
        Assert.Equal(1, db.FinanceExpenses.Count());
    }

    [Fact]
    public async Task Preview_整柜法_全额归指定参与方且基数取持久化装柜总量()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "客户A");
        var customerB = SeedCustomer(db, "C002", "客户B");
        var list = SeedLoadingList(db, "ZG20260925007", "TCLU-007", customerA.Id,
            cartons: 12m, weight: 640m, volume: 4.25m);
        var pa = SeedParticipant(db, list.Id, customerA, primary: true);
        SeedParticipant(db, list.Id, customerB);
        var source = SeedSourceExpense(db, "EXP-20260925007", "TCLU-007", 900m);
        var ctl = BuildController(db);

        var preview = AssertOk<ContainerExpenseAllocationPreviewDto>(
            await ctl.AllocationPreview(Request(source.Id, list.Id, "整柜",
                wholeTargetId: pa.Id, wholeBasisKind: "体积")));

        Assert.Equal("整柜", preview.AllocationMethod);
        Assert.Equal("体积", preview.BasisKind);        // 整柜法的基数种类 = 选定的持久化装柜证据
        Assert.Equal("持久化装柜证据", preview.BasisSource);
        var line = Assert.Single(preview.Lines);
        Assert.Equal(pa.Id, line.ParticipantId);
        Assert.Equal(100m, line.Ratio);
        Assert.Equal(900m, line.AllocatedAmount);
        Assert.Equal(4.25m, line.BasisValue);          // 装柜清单持久化总体积
        Assert.Contains("持久化", line.BasisEvidence);
        Assert.Equal(900m, preview.AllocatedTotal);

        // 恰好一条启用参与方时可省略目标参与方
        db.ContainerLoadingListParticipants.RemoveRange(
            db.ContainerLoadingListParticipants.Where(x => x.Id != pa.Id));
        db.SaveChanges();
        var singleTarget = AssertOk<ContainerExpenseAllocationPreviewDto>(
            await ctl.AllocationPreview(Request(source.Id, list.Id, "整柜", wholeBasisKind: "箱数")));
        Assert.Equal(12m, Assert.Single(singleTarget.Lines).BasisValue);
    }

    [Fact]
    public async Task Preview_整柜法_未指定目标或持久化证据为零_都被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "客户A");
        var customerB = SeedCustomer(db, "C002", "客户B");
        var list = SeedLoadingList(db, "ZG20260925008", "TCLU-008", customerA.Id);
        var pa = SeedParticipant(db, list.Id, customerA);
        SeedParticipant(db, list.Id, customerB);
        var empty = SeedLoadingList(db, "ZG20260925009", "TCLU-009", customerA.Id, 0m, 0m, 0m);
        var pb = SeedParticipant(db, empty.Id, customerA);
        var source = SeedSourceExpense(db, "EXP-20260925008", "TCLU-008", 700m);
        var emptySource = SeedSourceExpense(db, "EXP-20260925009", "TCLU-009", 700m);
        var ctl = BuildController(db);

        // 多条启用参与方但未显式指定目标：不按主参与方 / 列表顺序猜测
        var noTarget = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () =>
            ctl.AllocationPreview(Request(source.Id, list.Id, "整柜", wholeBasisKind: "箱数")));
        Assert.Contains("必须显式指定唯一目标参与方", noTarget.Message);

        // 目标不属于本柜启用参与方（属于另一柜）
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () =>
            ctl.AllocationPreview(Request(source.Id, list.Id, "整柜",
                wholeTargetId: pb.Id, wholeBasisKind: "箱数")));

        // 整柜法基数种类必须是 箱数 / 毛重 / 体积
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () =>
            ctl.AllocationPreview(Request(source.Id, list.Id, "整柜",
                wholeTargetId: pa.Id, wholeBasisKind: "金额")));

        // 持久化装柜证据为 0：拒绝而不是按经验推断
        var noEvidence = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () =>
            ctl.AllocationPreview(Request(emptySource.Id, empty.Id, "整柜",
                wholeTargetId: pb.Id, wholeBasisKind: "箱数")));
        Assert.Contains("整柜法基数缺失", noEvidence.Message);
    }

    // ==================== 3. 生成：批次留痕、重复防护、作废历史 ====================

    [Fact]
    public async Task Generate_写入批次与逐行留痕_费用单行携带批次号与来源费用()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "拼柜客户A");
        var customerB = SeedCustomer(db, "C002", "拼柜客户B");
        var list = SeedLoadingList(db, "ZG20260925010", "TCLU-010", customerA.Id);
        var pa = SeedParticipant(db, list.Id, customerA, primary: true);
        var pb = SeedParticipant(db, list.Id, customerB);
        var source = SeedSourceExpense(db, "EXP-20260925010", "TCLU-010", 1000m);
        var ctl = BuildController(db);

        var result = AssertOk<ContainerExpenseAllocationGenerateResultDto>(
            await ctl.AllocationGenerate(Request(source.Id, list.Id, "按体积", null, "箱数", "9 月拼柜报关费分摊",
                (pa.Id, 30m), (pb.Id, 70m))));

        Assert.StartsWith("EAB-", result.BatchNo);
        Assert.Equal(2, result.LineCount);
        Assert.Equal(1000m, result.AllocatedTotal);
        Assert.Equal("有效", result.StatusText);
        Assert.Equal(2, result.ExpenseNos.Count);
        Assert.All(result.ExpenseNos, no => Assert.StartsWith("EXP-", no));
        Assert.All(result.Lines, l => Assert.NotNull(l.ExpenseId));

        // 批次留痕：来源费用 / 装柜清单 / 柜号 / 方法 / 基数种类 / 金额
        var batch = db.FinanceExpenseAllocationBatches.Single(x => x.Id == result.BatchId);
        Assert.Equal(source.Id, batch.SourceExpenseId);
        Assert.Equal("EXP-20260925010", batch.SourceExpenseNo);
        Assert.Equal(list.Id, batch.LoadingListId);
        Assert.Equal("ZG20260925010", batch.LoadingListNo);
        Assert.Equal("TCLU-010", batch.ContainerNo);
        Assert.Equal("按体积", batch.AllocationMethod);
        Assert.Equal("按体积", batch.BasisKind);
        Assert.Equal("CNY", batch.Currency);
        Assert.Equal(1000m, batch.SourceAmount);
        Assert.Equal(1000m, batch.AllocatedTotal);
        Assert.Equal(2, batch.LineCount);
        Assert.Equal(ContainerExpenseAllocationRules.BatchActive, batch.Status);
        Assert.Null(batch.VoidedAt);
        Assert.Equal("9 月拼柜报关费分摊", batch.Remark);

        // 逐行留痕：批次 Id（由 EF 关系回填）/ 参与方 / 客户 / 基数 / 比例 / 金额 / 生成的费用单
        var lines = db.FinanceExpenseAllocationLines
            .Where(x => x.BatchId == batch.Id).OrderBy(x => x.SortOrder).ToList();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.NotEqual(0, l.BatchId));
        var lineA = Assert.Single(lines, l => l.ParticipantId == pa.Id);
        Assert.Equal("C001", lineA.CustomerCode);
        Assert.Equal("拼柜客户A", lineA.CustomerName);
        Assert.True(lineA.ParticipantPrimary);
        Assert.Equal("用户确认请求值", lineA.BasisSource);
        Assert.Equal(30m, lineA.BasisValue);
        Assert.Equal(30m, lineA.Ratio);
        Assert.Equal(300m, lineA.AllocatedAmount);
        Assert.Equal("TCLU-010", lineA.ContainerNo);
        Assert.NotNull(lineA.ExpenseId);

        // 生成的费用单行沿用既有字段，并携带批次号 / 来源费用留痕
        var expense = db.FinanceExpenses.Single(x => x.Id == lineA.ExpenseId!.Value);
        Assert.Equal(lineA.ExpenseNo, expense.ExpenseNo);
        Assert.Equal(300m, expense.Amount);
        Assert.Equal(300m, expense.AllocatedAmount);
        Assert.Equal(30m, expense.AllocationRatio);
        Assert.Equal("按体积", expense.AllocationBase);
        Assert.Equal("拼柜", expense.RefType);
        Assert.Equal("TCLU-010", expense.RefNo);
        Assert.Equal(customerA.Id, expense.CustomerId);
        Assert.Equal("拼柜客户A", expense.CustomerName);
        Assert.Equal("未付", expense.PaymentStatus);
        Assert.Equal(result.BatchNo, expense.AllocationBatchNo);
        Assert.Equal(source.Id, expense.AllocationSourceExpenseId);
        Assert.Equal(source.ExpenseNo, expense.AllocationSourceExpenseNo);

        // 来源费用单本身不被改写
        var reloadedSource = db.FinanceExpenses.Single(x => x.Id == source.Id);
        Assert.Equal(1000m, reloadedSource.Amount);
        Assert.Equal(string.Empty, reloadedSource.AllocationBatchNo);
        Assert.Equal("未付", reloadedSource.PaymentStatus);
    }

    [Fact]
    public async Task Generate_同一来源费用与装柜清单重复生成_被拒绝且不新增任何行()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "客户A");
        var customerB = SeedCustomer(db, "C002", "客户B");
        var list = SeedLoadingList(db, "ZG20260925011", "TCLU-011", customerA.Id);
        var pa = SeedParticipant(db, list.Id, customerA);
        var pb = SeedParticipant(db, list.Id, customerB);
        var source = SeedSourceExpense(db, "EXP-20260925011", "TCLU-011", 500m);
        var ctl = BuildController(db);

        AssertOk<ContainerExpenseAllocationGenerateResultDto>(
            await ctl.AllocationGenerate(Request(source.Id, list.Id, "按体积", null, "箱数", "",
                (pa.Id, 1m), (pb.Id, 1m))));

        // 同方法重复
        var duplicate = await AssertBusinessAsync(ErrorCodes.Duplicate, () => ctl.AllocationGenerate(
            Request(source.Id, list.Id, "按体积", null, "箱数", "", (pa.Id, 1m), (pb.Id, 1m))));
        Assert.Contains("不能重复生成", duplicate.Message);

        // 换方法同样拒绝：同一笔柜级费用不允许被重复分摊
        await AssertBusinessAsync(ErrorCodes.Duplicate, () => ctl.AllocationGenerate(
            Request(source.Id, list.Id, "按箱数", null, "箱数", "", (pa.Id, 2m), (pb.Id, 3m))));

        Assert.Equal(1, db.FinanceExpenseAllocationBatches.Count());
        Assert.Equal(2, db.FinanceExpenseAllocationLines.Count());
        Assert.Equal(3, db.FinanceExpenses.Count());       // 来源费用单 + 2 条生成行
        Assert.Empty(db.FinanceReceipts);
        Assert.Empty(db.FinancePayments);
    }

    [Fact]
    public async Task Void_作废保留批次与逐行留痕_不改写来源费用与已生成费用单()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "客户A");
        var customerB = SeedCustomer(db, "C002", "客户B");
        var list = SeedLoadingList(db, "ZG20260925012", "TCLU-012", customerA.Id);
        var pa = SeedParticipant(db, list.Id, customerA, primary: true);
        var pb = SeedParticipant(db, list.Id, customerB);
        var source = SeedSourceExpense(db, "EXP-20260925012", "TCLU-012", 1000m);
        var ctl = BuildController(db);

        var generated = AssertOk<ContainerExpenseAllocationGenerateResultDto>(
            await ctl.AllocationGenerate(Request(source.Id, list.Id, "按重量", null, "箱数", "",
                (pa.Id, 40m), (pb.Id, 60m))));

        var snapshot = db.FinanceExpenses.AsNoTracking().ToDictionary(
            x => x.Id, x => (x.Amount, x.AllocatedAmount, x.AllocationRatio, x.PaymentStatus, x.AllocationBatchNo));

        var voided = AssertOk<ContainerExpenseAllocationBatchDto>(await ctl.VoidAllocationBatch(
            generated.BatchId,
            new ContainerExpenseAllocationVoidRequest { Reason = "基准值填错，需重新分摊" }));

        Assert.True(voided.IsVoided);
        Assert.False(voided.IsActive);
        Assert.Equal("已作废", voided.StatusText);
        Assert.NotNull(voided.VoidedAt);
        Assert.Equal("基准值填错，需重新分摊", voided.VoidReason);
        Assert.Equal(2, voided.LineCount);
        Assert.Equal(2, voided.Lines.Count);                    // 逐行留痕保留
        Assert.All(voided.Lines, l => Assert.NotNull(l.ExpenseId));

        // 批次、分摊行、费用单行一个都没少；金额 / 归属 / 付款状态一律未被改写
        Assert.Equal(1, db.FinanceExpenseAllocationBatches.Count());
        Assert.Equal(2, db.FinanceExpenseAllocationLines.Count());
        Assert.Equal(3, db.FinanceExpenses.Count());
        foreach (var (id, expected) in snapshot)
        {
            var actual = db.FinanceExpenses.Single(x => x.Id == id);
            Assert.Equal(expected.Amount, actual.Amount);
            Assert.Equal(expected.AllocatedAmount, actual.AllocatedAmount);
            Assert.Equal(expected.AllocationRatio, actual.AllocationRatio);
            Assert.Equal(expected.PaymentStatus, actual.PaymentStatus);
            Assert.Equal(expected.AllocationBatchNo, actual.AllocationBatchNo);
        }

        // 作废不产生任何收付款 / 结算动作
        Assert.Empty(db.FinanceReceipts);
        Assert.Empty(db.FinancePayments);
        Assert.Empty(db.FinanceContainerSettlements);
    }

    [Fact]
    public async Task Void_重复作废或空原因被拒绝_作废后可按状态过滤台账()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "客户A");
        var customerB = SeedCustomer(db, "C002", "客户B");
        var list = SeedLoadingList(db, "ZG20260925013", "TCLU-013", customerA.Id);
        var pa = SeedParticipant(db, list.Id, customerA);
        var pb = SeedParticipant(db, list.Id, customerB);
        var source = SeedSourceExpense(db, "EXP-20260925013", "TCLU-013", 400m);
        var ctl = BuildController(db);

        var first = AssertOk<ContainerExpenseAllocationGenerateResultDto>(
            await ctl.AllocationGenerate(Request(source.Id, list.Id, "按体积", null, "箱数", "",
                (pa.Id, 1m), (pb.Id, 1m))));

        // 空原因 / 不存在的批次不能作废（作废必须留下有据可查的更正原因）
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () =>
            ctl.VoidAllocationBatch(first.BatchId, new ContainerExpenseAllocationVoidRequest { Reason = "   " }));
        await AssertBusinessAsync(ErrorCodes.NotFound, () =>
            ctl.VoidAllocationBatch(999999, new ContainerExpenseAllocationVoidRequest { Reason = "不存在" }));

        AssertOk<ContainerExpenseAllocationBatchDto>(await ctl.VoidAllocationBatch(
            first.BatchId, new ContainerExpenseAllocationVoidRequest { Reason = "客户归属调整" }));

        // 重复作废被拒绝（历史状态不被覆盖）
        var again = await AssertBusinessAsync(ErrorCodes.RuleConflict, () =>
            ctl.VoidAllocationBatch(first.BatchId, new ContainerExpenseAllocationVoidRequest { Reason = "再作废" }));
        Assert.Contains("不能重复作废", again.Message);

        // 作废后可重新生成（同一来源费用 + 装柜清单不再占用有效批次）
        var second = AssertOk<ContainerExpenseAllocationGenerateResultDto>(
            await ctl.AllocationGenerate(Request(source.Id, list.Id, "按箱数", null, "箱数", "",
                (pa.Id, 1m), (pb.Id, 3m))));
        Assert.NotEqual(first.BatchNo, second.BatchNo);

        // 台账：默认含已作废历史；按状态可过滤
        var all = AssertOk<PagedResult<ContainerExpenseAllocationBatchDto>>(
            await ctl.GetAllocationBatches(new ContainerExpenseAllocationBatchQuery { Page = 1, PageSize = 50 }));
        Assert.Equal(2, all.Total);
        Assert.Contains(all.Items, b => b.BatchNo == first.BatchNo && b.IsVoided && b.VoidReason == "客户归属调整");
        Assert.Contains(all.Items, b => b.BatchNo == second.BatchNo && b.IsActive);

        var activeOnly = AssertOk<PagedResult<ContainerExpenseAllocationBatchDto>>(
            await ctl.GetAllocationBatches(new ContainerExpenseAllocationBatchQuery { Status = 1 }));
        Assert.Equal(1, activeOnly.Total);
        Assert.Equal(second.BatchNo, Assert.Single(activeOnly.Items).BatchNo);

        var voidedOnly = AssertOk<PagedResult<ContainerExpenseAllocationBatchDto>>(
            await ctl.GetAllocationBatches(new ContainerExpenseAllocationBatchQuery { Status = 0 }));
        Assert.Equal(1, voidedOnly.Total);
        Assert.Equal(first.BatchNo, Assert.Single(voidedOnly.Items).BatchNo);

        // 状态取值非法直接拒绝；非法 Id 读取单个批次
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () =>
            ctl.GetAllocationBatches(new ContainerExpenseAllocationBatchQuery { Status = 7 }));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => ctl.GetAllocationBatch(999999));
        AssertOk<ContainerExpenseAllocationBatchDto>(await ctl.GetAllocationBatch(second.BatchId));
    }

    // ==================== 4. 读侧留痕标注与来源费用资格 ====================

    [Fact]
    public async Task Lineage_批次留痕与历史分摊与未分摊_在费用单列表上显式标注()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "客户A");
        var customerB = SeedCustomer(db, "C002", "客户B");
        var list = SeedLoadingList(db, "ZG20260925014", "TCLU-014", customerA.Id);
        var pa = SeedParticipant(db, list.Id, customerA);
        var pb = SeedParticipant(db, list.Id, customerB);
        var source = SeedSourceExpense(db, "EXP-20260925014", "TCLU-014", 600m);

        // 历史分摊行（旧「拼柜分摊」接口生成：带分摊基数 / 比例 / 分摊金额，但无批次留痕）
        var legacy = new FinanceExpense
        {
            ExpenseNo = "EXP-20260925014-001",
            ExpenseDate = new DateTime(2026, 9, 24),
            ExpenseType = "报关费",
            Amount = 150m,
            Currency = "CNY",
            ExchangeRate = 1m,
            AmountCny = 150m,
            RefType = "拼柜",
            RefNo = "TCLU-014",
            CustomerId = customerA.Id,
            CustomerName = "客户A",
            AllocationBase = "按体积",
            AllocationRatio = 50m,
            AllocatedAmount = 150m,
            PaymentStatus = "未付"
        };
        db.FinanceExpenses.Add(legacy);
        db.SaveChanges();

        var ctl = BuildController(db);
        var generated = AssertOk<ContainerExpenseAllocationGenerateResultDto>(
            await ctl.AllocationGenerate(Request(source.Id, list.Id, "按体积", null, "箱数", "",
                (pa.Id, 1m), (pb.Id, 1m))));

        var page = AssertOk<PagedResult<FinanceExpense>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 50 }));

        var sourceRow = Assert.Single(page.Items, x => x.Id == source.Id);
        Assert.Equal(ContainerExpenseAllocationRules.LineageNone, sourceRow.AllocationLineage);
        Assert.Contains("未分摊", sourceRow.AllocationLineageText);

        var legacyRow = Assert.Single(page.Items, x => x.Id == legacy.Id);
        Assert.Equal(ContainerExpenseAllocationRules.LineageLegacy, legacyRow.AllocationLineage);
        Assert.Contains("历史分摊", legacyRow.AllocationLineageText);
        Assert.Contains("无批次留痕", legacyRow.AllocationLineageText);

        var batchRow = Assert.Single(page.Items, x => x.ExpenseNo == generated.ExpenseNos[0]);
        Assert.Equal(ContainerExpenseAllocationRules.LineageBatch, batchRow.AllocationLineage);
        Assert.Contains(generated.BatchNo, batchRow.AllocationLineageText);
        Assert.Contains("有效批次", batchRow.AllocationLineageText);

        // 作废后同一行改写为「已作废批次」（行本身仍然可读、未被删除或改写）
        AssertOk<ContainerExpenseAllocationBatchDto>(await ctl.VoidAllocationBatch(
            generated.BatchId, new ContainerExpenseAllocationVoidRequest { Reason = "更正分摊口径" }));
        var afterVoid = AssertOk<PagedResult<FinanceExpense>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 50 }));
        var voidedRow = Assert.Single(afterVoid.Items, x => x.ExpenseNo == generated.ExpenseNos[0]);
        Assert.Equal(ContainerExpenseAllocationRules.LineageBatch, voidedRow.AllocationLineage);
        Assert.Contains("已作废批次", voidedRow.AllocationLineageText);
    }

    [Fact]
    public async Task Source_历史分摊行与已属批次行_都不能作为分摊来源()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "客户A");
        var customerB = SeedCustomer(db, "C002", "客户B");
        var list = SeedLoadingList(db, "ZG20260925015", "TCLU-015", customerA.Id);
        var pa = SeedParticipant(db, list.Id, customerA);
        var pb = SeedParticipant(db, list.Id, customerB);
        var source = SeedSourceExpense(db, "EXP-20260925015", "TCLU-015", 300m);
        var legacy = SeedSourceExpense(db, "EXP-20260925015-001", "TCLU-015", 150m,
            refType: "拼柜", allocationBase: "按体积", allocationRatio: 50m, allocatedAmount: 150m);
        var ctl = BuildController(db);

        var legacyRejected = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => ctl.AllocationGenerate(
            Request(legacy.Id, list.Id, "按体积", null, "箱数", "", (pa.Id, 1m), (pb.Id, 1m))));
        Assert.Contains("历史分摊生成的明细行", legacyRejected.Message);

        var generated = AssertOk<ContainerExpenseAllocationGenerateResultDto>(
            await ctl.AllocationGenerate(Request(source.Id, list.Id, "按体积", null, "箱数", "",
                (pa.Id, 1m), (pb.Id, 1m))));

        // 生成行自身带批次留痕，不能再作为另一笔分摊的来源
        var generatedExpense = db.FinanceExpenses.Single(x => x.ExpenseNo == generated.ExpenseNos[0]);
        var withBatch = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => ctl.AllocationGenerate(
            Request(generatedExpense.Id, list.Id, "按体积", null, "箱数", "", (pa.Id, 1m), (pb.Id, 1m))));
        Assert.Contains("已属于分摊批次", withBatch.Message);
    }

    [Fact]
    public async Task Source_非柜级归属或单号不一致或金额非正_都不能作为分摊来源()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "客户A");
        var customerB = SeedCustomer(db, "C002", "客户B");
        var list = SeedLoadingList(db, "ZG20260925016", "TCLU-016", customerA.Id);
        var pa = SeedParticipant(db, list.Id, customerA);
        var pb = SeedParticipant(db, list.Id, customerB);

        var orderScoped = SeedSourceExpense(db, "EXP-20260925016-001", "TCLU-016", 200m, refType: "订单");
        var otherContainer = SeedSourceExpense(db, "EXP-20260925016-002", "OTHER-999", 200m);
        var zero = SeedSourceExpense(db, "EXP-20260925016-003", "TCLU-016", 0m);
        var deleted = SeedSourceExpense(db, "EXP-20260925016-004", "TCLU-016", 200m);
        deleted.IsDeleted = true;
        db.SaveChanges();
        var lowerCase = SeedSourceExpense(db, "EXP-20260925016-005", "tclu-016", 200m);

        var ctl = BuildController(db);

        var notContainer = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => ctl.AllocationPreview(
            Request(orderScoped.Id, list.Id, "按体积", null, "箱数", "", (pa.Id, 1m), (pb.Id, 1m))));
        Assert.Contains("不是柜级费用", notContainer.Message);

        var mismatch = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => ctl.AllocationPreview(
            Request(otherContainer.Id, list.Id, "按体积", null, "箱数", "", (pa.Id, 1m), (pb.Id, 1m))));
        Assert.Contains("不一致", mismatch.Message);

        var zeroAmount = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => ctl.AllocationPreview(
            Request(zero.Id, list.Id, "按体积", null, "箱数", "", (pa.Id, 1m), (pb.Id, 1m))));
        Assert.Contains("金额必须大于 0", zeroAmount.Message);

        await AssertBusinessAsync(ErrorCodes.NotFound, () => ctl.AllocationPreview(
            Request(deleted.Id, list.Id, "按体积", null, "箱数", "", (pa.Id, 1m), (pb.Id, 1m))));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => ctl.AllocationPreview(
            Request(999999, list.Id, "按体积", null, "箱数", "", (pa.Id, 1m), (pb.Id, 1m))));

        // 柜号大小写差异按同柜识别（不区分大小写），仍可正常分摊
        var ok = AssertOk<ContainerExpenseAllocationPreviewDto>(await ctl.AllocationPreview(
            Request(lowerCase.Id, list.Id, "按体积", null, "箱数", "", (pa.Id, 1m), (pb.Id, 1m))));
        Assert.Equal(200m, ok.AllocatedTotal);

        // 非法装柜清单 / 来源费用 Id
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => ctl.AllocationPreview(
            Request(0, list.Id, "按体积", null, "箱数", "", (pa.Id, 1m), (pb.Id, 1m))));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => ctl.AllocationPreview(
            Request(lowerCase.Id, 999999, "按体积", null, "箱数", "", (pa.Id, 1m), (pb.Id, 1m))));
    }

    [Fact]
    public async Task Context_只读汇总参与方与费用单资格_并体现有效与已作废批次()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "客户A");
        var customerB = SeedCustomer(db, "C002", "客户B");
        var customerC = SeedCustomer(db, "C003", "客户C");
        var list = SeedLoadingList(db, "ZG20260925017", "TCLU-017", customerA.Id);
        var pa = SeedParticipant(db, list.Id, customerA, primary: true);
        var pb = SeedParticipant(db, list.Id, customerB);
        SeedParticipant(db, list.Id, customerC, status: 0);              // 停用的历史参与方
        var source = SeedSourceExpense(db, "EXP-20260925017", "TCLU-017", 900m);
        var legacy = SeedSourceExpense(db, "EXP-20260925017-001", "TCLU-017", 300m,
            refType: "拼柜", allocationBase: "按箱数", allocationRatio: 100m, allocatedAmount: 300m);
        var ctl = BuildController(db);

        var context = AssertOk<ContainerExpenseAllocationContextDto>(await ctl.GetAllocationContext(list.Id));
        Assert.Equal("TCLU-017", context.ContainerNo);
        Assert.Equal("ZG20260925017", context.LoadingListNo);
        Assert.Equal(10m, context.PersistedTotalCartons);
        Assert.Equal(500m, context.PersistedTotalWeight);
        Assert.Equal(3.5m, context.PersistedTotalVolume);
        Assert.True(context.HasPersistedEvidence);
        Assert.Equal(2, context.ActiveParticipantCount);
        Assert.Equal(3, context.TotalParticipantCount);
        Assert.False(context.LegacySingleCustomer);
        Assert.Equal(customerA.Id, context.PrimaryParticipantId);
        Assert.Contains("分摊范围", context.ParticipantScopeText);
        Assert.Contains("停用", context.ParticipantScopeText);
        Assert.Equal(3, context.Participants.Count);
        Assert.Contains(context.Participants, p => !p.Selectable);
        Assert.Equal(5, context.SupportedMethods.Count);
        Assert.Equal(3, context.WholeContainerBasisKinds.Count);
        Assert.Contains("余差", context.RemainderRuleText);

        // 费用单资格：来源费用可分摊；历史分摊行不可分摊且给出原因
        var eligible = Assert.Single(context.SourceExpenses, s => s.Id == source.Id);
        Assert.True(eligible.Eligible);
        Assert.Equal(ContainerExpenseAllocationRules.LineageNone, eligible.Lineage);
        var ineligible = Assert.Single(context.SourceExpenses, s => s.Id == legacy.Id);
        Assert.False(ineligible.Eligible);
        Assert.Contains("历史分摊生成的明细行", ineligible.EligibilityText);
        Assert.Equal(ContainerExpenseAllocationRules.LineageLegacy, ineligible.Lineage);

        Assert.Empty(context.ActiveBatches);
        Assert.Empty(context.VoidedBatches);
        Assert.Empty(db.FinanceExpenseAllocationBatches);               // 上下文读取不写库

        // 生成 + 作废后：上下文同时体现有效批次与已作废批次
        var generated = AssertOk<ContainerExpenseAllocationGenerateResultDto>(
            await ctl.AllocationGenerate(Request(source.Id, list.Id, "按体积", null, "箱数", "",
                (pa.Id, 1m), (pb.Id, 1m))));
        AssertOk<ContainerExpenseAllocationBatchDto>(await ctl.VoidAllocationBatch(
            generated.BatchId, new ContainerExpenseAllocationVoidRequest { Reason = "口径更正" }));

        var after = AssertOk<ContainerExpenseAllocationContextDto>(await ctl.GetAllocationContext(list.Id));
        Assert.Empty(after.ActiveBatches);
        Assert.Equal(generated.BatchNo, Assert.Single(after.VoidedBatches).BatchNo);
        Assert.Equal(2, Assert.Single(after.VoidedBatches).Lines.Count);

        // 没有参与方的清单：历史单客户视图，不能分摊
        var legacyList = SeedLoadingList(db, "ZG20260925018", "TCLU-017", customerA.Id);
        var legacyContext = AssertOk<ContainerExpenseAllocationContextDto>(
            await ctl.GetAllocationContext(legacyList.Id));
        Assert.True(legacyContext.LegacySingleCustomer);
        Assert.Equal(0, legacyContext.ActiveParticipantCount);
        Assert.Empty(legacyContext.Participants);
        Assert.Contains("没有参与方行", legacyContext.ParticipantScopeText);
        var noParticipant = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => ctl.AllocationPreview(
            Request(source.Id, legacyList.Id, "按体积", null, "箱数", "")));
        Assert.Contains("没有启用中的参与方", noParticipant.Message);
    }

    // ==================== 5. 边界：非变更与事务性 ====================

    [Fact]
    public async Task Generate_不改写装柜明细参与方单证库存订单与订柜跟踪值()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "客户A");
        var customerB = SeedCustomer(db, "C002", "客户B");
        var list = SeedLoadingList(db, "ZG20260925019", "TCLU-019", customerA.Id);
        var pa = SeedParticipant(db, list.Id, customerA, primary: true);
        var pb = SeedParticipant(db, list.Id, customerB);

        db.ContainerLoadingDetails.Add(new ContainerLoadingDetail
        {
            LoadingListId = list.Id, ProductId = 9001, ProductName = "保温杯",
            Quantity = 100m, Cartons = 10m, Weight = 500m, Volume = 3.5m
        });
        db.ContainerBookings.Add(new ContainerBooking
        {
            BookingNo = "BK-2026-001", ShipmentMode = "FCL",
            BillOfLadingNo = "BL-001", VoyageNo = "V001", TransitPort = "SGSIN",
            Etd = new DateTime(2026, 10, 1), Eta = new DateTime(2026, 10, 20),
            Atd = new DateTime(2026, 10, 2), Ata = new DateTime(2026, 10, 21)
        });
        db.TradeDocuments.Add(new TradeDocument
        {
            DocNo = "DOC-001", DocType = "装箱单", RefNo = "TCLU-019", Amount = 1000m, Status = "待制作"
        });
        db.Stocks.Add(new Stock
        {
            ProductId = 9001, WarehouseId = 1, Quantity = 500m, AvailableQuantity = 500m,
            AverageCost = 10m, TotalCost = 5000m
        });
        db.StockMovements.Add(new StockMovement
        {
            WarehouseId = 1, ProductId = 9001, MovementType = InventoryMovementType.PurchaseIn,
            Quantity = 500m, Amount = 5000m, BalanceQuantity = 500m
        });
        db.PurchaseOrders.Add(new PurchaseOrder
        {
            OrderNo = "PO-2026-001", TotalAmount = 1000m, ArrivalProgress = "未到货"
        });
        db.SalesOrders.Add(new SalesOrder { OrderNo = "SO-2026-001", CustomerId = customerA.Id });
        db.SaveChanges();

        var source = SeedSourceExpense(db, "EXP-20260925019", "TCLU-019", 700m);
        var ctl = BuildController(db);
        var request = Request(source.Id, list.Id, "按体积", null, "箱数", "", (pa.Id, 3.5m), (pb.Id, 1.5m));

        AssertOk<ContainerExpenseAllocationPreviewDto>(await ctl.AllocationPreview(request));
        var generated = AssertOk<ContainerExpenseAllocationGenerateResultDto>(
            await ctl.AllocationGenerate(request));
        AssertOk<ContainerExpenseAllocationBatchDto>(await ctl.VoidAllocationBatch(
            generated.BatchId, new ContainerExpenseAllocationVoidRequest { Reason = "更正口径" }));

        // 装柜清单与明细：数量 / 箱数 / 重量 / 体积 / 柜号 / 兼容客户字段一律未变
        var afterList = db.ContainerLoadingLists.Single(x => x.Id == list.Id);
        Assert.Equal("TCLU-019", afterList.ContainerNo);
        Assert.Equal(customerA.Id, afterList.CustomerId);
        Assert.Equal(10m, afterList.TotalCartons);
        Assert.Equal(500m, afterList.TotalWeight);
        Assert.Equal(3.5m, afterList.TotalVolume);

        var detail = Assert.Single(db.ContainerLoadingDetails.Where(x => x.LoadingListId == list.Id));
        Assert.Equal(100m, detail.Quantity);
        Assert.Equal(10m, detail.Cartons);
        Assert.Equal(500m, detail.Weight);
        Assert.Equal(3.5m, detail.Volume);

        // 参与方身份与主参与方未变
        var participants = db.ContainerLoadingListParticipants.Where(x => x.LoadingListId == list.Id).ToList();
        Assert.Equal(2, participants.Count);
        Assert.Equal(pa.Id, Assert.Single(participants, p => p.IsPrimary).Id);

        // 单证 / 库存 / 库存流水 / 采购订单 / 销售订单 / 订柜跟踪值一律未变
        var doc = Assert.Single(db.TradeDocuments);
        Assert.Equal("待制作", doc.Status);
        Assert.Equal(1000m, doc.Amount);
        var stock = Assert.Single(db.Stocks);
        Assert.Equal(500m, stock.Quantity);
        Assert.Equal(5000m, stock.TotalCost);
        Assert.Single(db.StockMovements);
        Assert.Equal("未到货", Assert.Single(db.PurchaseOrders).ArrivalProgress);
        Assert.Single(db.SalesOrders);

        var booking = Assert.Single(db.ContainerBookings);
        Assert.Equal("BL-001", booking.BillOfLadingNo);
        Assert.Equal("V001", booking.VoyageNo);
        Assert.Equal(new DateTime(2026, 10, 1), booking.Etd);
        Assert.Equal(new DateTime(2026, 10, 21), booking.Ata);
        Assert.Equal(customerA.Id, afterList.CustomerId);
    }

    [Fact]
    public async Task Generate_持久化失败_不保留任何部分行()
    {
        var name = Guid.NewGuid().ToString();
        var options = SharedOptions(name);
        long sourceId, listId, participantAId, participantBId;

        using (var seed = new ErpDbContext(options))
        {
            var customerA = SeedCustomer(seed, "C001", "客户A");
            var customerB = SeedCustomer(seed, "C002", "客户B");
            var list = SeedLoadingList(seed, "ZG20260925020", "TCLU-020", customerA.Id);
            participantAId = SeedParticipant(seed, list.Id, customerA).Id;
            participantBId = SeedParticipant(seed, list.Id, customerB).Id;
            sourceId = SeedSourceExpense(seed, "EXP-20260925020", "TCLU-020", 800m).Id;
            listId = list.Id;
        }

        var request = Request(sourceId, listId, "按体积", null, "箱数", "",
            (participantAId, 1m), (participantBId, 3m));

        using (var failing = new FailingSaveDbContext(options))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => ContainerExpenseAllocationService.GenerateAsync(failing, request));
        }

        // 换新上下文断言：批次 / 分摊行 / 生成的费用单行都没有留下半条（校验与写入同批完成）
        using var reader = new ErpDbContext(options);
        Assert.Empty(reader.FinanceExpenseAllocationBatches);
        Assert.Empty(reader.FinanceExpenseAllocationLines);
        Assert.Equal(1, reader.FinanceExpenses.Count());          // 只剩来源费用单
        Assert.Equal(800m, reader.FinanceExpenses.Single().Amount);
        Assert.Equal(string.Empty, reader.FinanceExpenses.Single().AllocationBatchNo);
    }

    // ==================== 6. 模型 / 幂等结构 / 前端接线契约与纯规则 ====================

    [Fact]
    public void Model_批次与分摊行索引过滤条件及外键口径与建表脚本一致()
    {
        using var db = TestDbFactory.Create();

        var batch = db.Model.FindEntityType(typeof(FinanceExpenseAllocationBatch));
        Assert.NotNull(batch);
        var batchNoIndex = Assert.Single(batch!.GetIndexes(), i =>
            i.IsUnique && i.Properties.Count == 1
            && i.Properties[0].Name == nameof(FinanceExpenseAllocationBatch.BatchNo));
        Assert.Equal("UX_FinanceExpenseAllocationBatches_BatchNo", batchNoIndex.GetDatabaseName());

        var sourceLive = Assert.Single(batch.GetIndexes(), i => i.IsUnique && i.Properties.Count == 3);
        Assert.Equal("UX_FinanceExpenseAllocationBatches_SourceLive", sourceLive.GetDatabaseName());
        Assert.Contains("Status = 1", sourceLive.GetFilter());
        Assert.Contains("IsDeleted = 0", sourceLive.GetFilter());

        Assert.Equal(50, batch.FindProperty(nameof(FinanceExpenseAllocationBatch.BatchNo))!.GetMaxLength());
        Assert.Equal(50, batch.FindProperty(nameof(FinanceExpenseAllocationBatch.ContainerNo))!.GetMaxLength());
        Assert.Equal(30, batch.FindProperty(nameof(FinanceExpenseAllocationBatch.AllocationMethod))!.GetMaxLength());
        Assert.Equal(30, batch.FindProperty(nameof(FinanceExpenseAllocationBatch.BasisKind))!.GetMaxLength());
        Assert.Equal(500, batch.FindProperty(nameof(FinanceExpenseAllocationBatch.VoidReason))!.GetMaxLength());
        Assert.Equal(2, batch.FindProperty(nameof(FinanceExpenseAllocationBatch.SourceAmount))!.GetScale());

        var line = db.Model.FindEntityType(typeof(FinanceExpenseAllocationLine));
        Assert.NotNull(line);
        var batchParticipant = Assert.Single(line!.GetIndexes(), i => i.IsUnique && i.Properties.Count == 2);
        Assert.Equal("UX_FinanceExpenseAllocationLines_BatchParticipant", batchParticipant.GetDatabaseName());
        Assert.Contains("IsDeleted = 0", batchParticipant.GetFilter());
        Assert.Equal(4, line.FindProperty(nameof(FinanceExpenseAllocationLine.Ratio))!.GetScale());
        Assert.Equal(4, line.FindProperty(nameof(FinanceExpenseAllocationLine.BasisValue))!.GetScale());
        Assert.Equal(2, line.FindProperty(nameof(FinanceExpenseAllocationLine.AllocatedAmount))!.GetScale());
        Assert.Equal(50, line.FindProperty(nameof(FinanceExpenseAllocationLine.CustomerCode))!.GetMaxLength());
        Assert.Equal(200, line.FindProperty(nameof(FinanceExpenseAllocationLine.CustomerName))!.GetMaxLength());

        // 分摊行只有两条外键：批次（级联）与生成的费用单（Restrict）；
        // 刻意不建到参与方 / 客户 / 装柜清单的外键（历史留痕必须始终可读）
        var foreignKeys = line.GetForeignKeys().ToList();
        Assert.Equal(2, foreignKeys.Count);
        Assert.Contains(foreignKeys, fk => fk.PrincipalEntityType.ClrType == typeof(FinanceExpenseAllocationBatch));
        Assert.Contains(foreignKeys, fk => fk.PrincipalEntityType.ClrType == typeof(FinanceExpense));
        Assert.DoesNotContain(foreignKeys, fk => fk.PrincipalEntityType.ClrType == typeof(BaseCustomer));
        Assert.DoesNotContain(foreignKeys, fk => fk.PrincipalEntityType.ClrType == typeof(ContainerLoadingListParticipant));
        Assert.DoesNotContain(foreignKeys, fk => fk.PrincipalEntityType.ClrType == typeof(ContainerLoadingList));

        // 费用单侧只有可空留痕列，**不加导航属性**（避免编辑费用单时把批次留痕静默清空）
        var expense = db.Model.FindEntityType(typeof(FinanceExpense));
        Assert.NotNull(expense);
        Assert.DoesNotContain(expense!.GetNavigations(),
            n => n.ClrType == typeof(FinanceExpenseAllocationBatch));
        Assert.Equal(50, expense.FindProperty(nameof(FinanceExpense.AllocationBatchNo))!.GetMaxLength());
        Assert.Equal(50, expense.FindProperty(nameof(FinanceExpense.AllocationSourceExpenseNo))!.GetMaxLength());
    }

    [Fact]
    public void Schema_upgrade_幂等建表建索引且不做任何回填()
    {
        var script = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs"));

        // 幂等补列（可空）：历史费用单保持空值 = 历史分摊 / 未分摊，绝不回填
        Assert.Contains("IF COL_LENGTH('db_owner.FinanceExpenses', 'AllocationBatchNo') IS NULL", script);
        Assert.Contains("ALTER TABLE db_owner.FinanceExpenses ADD AllocationBatchNo NVARCHAR(50) NULL;", script);
        Assert.Contains("IF COL_LENGTH('db_owner.FinanceExpenses', 'AllocationSourceExpenseId') IS NULL", script);
        Assert.Contains("IF COL_LENGTH('db_owner.FinanceExpenses', 'AllocationSourceExpenseNo') IS NULL", script);

        // 幂等建表与索引（与 EF 模型同名同过滤条件）
        Assert.Contains("IF OBJECT_ID('db_owner.FinanceExpenseAllocationBatches') IS NULL", script);
        Assert.Contains("CREATE TABLE db_owner.FinanceExpenseAllocationBatches", script);
        Assert.Contains("BasisKind NVARCHAR(30) NOT NULL DEFAULT N''", script);
        Assert.Contains("VoidReason NVARCHAR(500) NOT NULL DEFAULT N''", script);
        Assert.Contains("CREATE UNIQUE INDEX UX_FinanceExpenseAllocationBatches_BatchNo", script);
        Assert.Contains("CREATE UNIQUE INDEX UX_FinanceExpenseAllocationBatches_SourceLive", script);
        Assert.Contains(
            "ON db_owner.FinanceExpenseAllocationBatches(SourceExpenseId, LoadingListId, AllocationMethod)", script);
        Assert.Contains("WHERE IsDeleted = 0 AND Status = 1;", script);

        Assert.Contains("IF OBJECT_ID('db_owner.FinanceExpenseAllocationLines') IS NULL", script);
        Assert.Contains("CREATE TABLE db_owner.FinanceExpenseAllocationLines", script);
        Assert.Contains("BasisValue DECIMAL(18,4) NOT NULL DEFAULT 0", script);
        Assert.Contains("Ratio DECIMAL(18,4) NOT NULL DEFAULT 0", script);
        Assert.Contains("AllocatedAmount DECIMAL(18,2) NOT NULL DEFAULT 0", script);
        Assert.Contains("CREATE UNIQUE INDEX UX_FinanceExpenseAllocationLines_BatchParticipant", script);
        Assert.Contains("FK_FinanceExpenseAllocationLines_Expense", script);

        // 幂等结构：不得出现任何按分摊批次回填 / 改写费用单、装柜、单证、库存与订单的语句
        Assert.DoesNotContain("UPDATE db_owner.FinanceExpenseAllocationBatches", script);
        Assert.DoesNotContain("UPDATE db_owner.FinanceExpenseAllocationLines", script);
        Assert.DoesNotContain("UPDATE db_owner.FinanceExpenses", script);
        Assert.DoesNotContain("UPDATE db_owner.ContainerLoadingLists", script);
        Assert.DoesNotContain("UPDATE db_owner.ContainerLoadingListParticipants", script);
        Assert.DoesNotContain("UPDATE db_owner.TradeDocuments", script);
        Assert.DoesNotContain("UPDATE db_owner.Stocks", script);
        Assert.DoesNotContain("INSERT INTO db_owner.FinanceExpenseAllocation", script);
    }

    [Fact]
    public void Frontend_分摊入口与留痕列已接线且脚本已注册()
    {
        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/expense-allocation-batch.js", index);

        var modules = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "modules.js"));
        Assert.Contains("openExpenseAllocationBatches", modules);
        Assert.Contains("expenseLineageCellHtml", modules);

        var script = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "expense-allocation-batch.js"));
        Assert.Contains("/api/finance/expenses/allocation-context", script);
        Assert.Contains("/api/finance/expenses/allocation-preview", script);
        Assert.Contains("/api/finance/expenses/allocation-generate", script);
        Assert.Contains("/api/finance/expenses/allocation-batches", script);

        var controller = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "ExpenseBillController.cs"));
        Assert.Contains("[HttpPost(\"allocation-preview\")]", controller);
        Assert.Contains("[HttpPost(\"allocation-generate\")]", controller);
        Assert.Contains("[HttpGet(\"allocation-context\")]", controller);
        Assert.Contains("[HttpGet(\"allocation-batches\")]", controller);
        Assert.Contains("[HttpPost(\"allocation-batches/{batchId:long}/void\")]", controller);
    }

    [Fact]
    public void Rules_币种精度方法规范化与留痕分类_非法取值一律拒绝()
    {
        Assert.Equal(2, ContainerExpenseAllocationRules.PrecisionOf(null));
        Assert.Equal(2, ContainerExpenseAllocationRules.PrecisionOf("usd"));
        Assert.Equal(0, ContainerExpenseAllocationRules.PrecisionOf("jpy"));
        Assert.Equal("CNY", ContainerExpenseAllocationRules.NormalizeCurrency("  "));
        Assert.Equal(2.35m, ContainerExpenseAllocationRules.RoundAmount(2.345m, "CNY"));
        Assert.Equal(2m, ContainerExpenseAllocationRules.RoundAmount(2.345m, "JPY"));
        Assert.Equal(33.3333m, ContainerExpenseAllocationRules.RoundRatio(100m / 3m));

        Assert.Equal("按体积", ContainerExpenseAllocationRules.NormalizeMethod(" 按体积 "));
        Assert.True(ContainerExpenseAllocationRules.IsSupportedMethod("整柜"));
        Assert.False(ContainerExpenseAllocationRules.IsSupportedMethod("按心情"));
        Assert.Throws<BusinessException>(() => ContainerExpenseAllocationRules.NormalizeMethod(""));
        Assert.Throws<BusinessException>(() => ContainerExpenseAllocationRules.NormalizeWholeContainerBasisKind("金额"));
        Assert.Equal("毛重", ContainerExpenseAllocationRules.NormalizeWholeContainerBasisKind(" 毛重 "));
        Assert.Throws<BusinessException>(() => ContainerExpenseAllocationRules.NormalizeRemark(
            new string('x', ContainerExpenseAllocationRules.MaxRemarkLength + 1)));
        Assert.Equal("有效", ContainerExpenseAllocationRules.BatchStatusText(1));
        Assert.Equal("已作废", ContainerExpenseAllocationRules.BatchStatusText(0));
        Assert.Throws<BusinessException>(() => ContainerExpenseAllocationRules.BatchStatusText(2));
        Assert.Throws<BusinessException>(() => ContainerExpenseAllocationRules.NormalizeBatchStatusFilter(3));
        Assert.True(ContainerExpenseAllocationRules.IsContainerLevelRefType("拼柜"));
        Assert.False(ContainerExpenseAllocationRules.IsContainerLevelRefType("订单"));

        var expense = new FinanceExpense { ExpenseNo = "E1", AllocationBase = "不分摊" };
        Assert.Equal(ContainerExpenseAllocationRules.LineageNone, ContainerExpenseAllocationRules.LineageOf(expense));
        Assert.Contains("未分摊", ContainerExpenseAllocationRules.LineageText(expense));

        expense.AllocationBase = "按体积";
        expense.AllocationRatio = 50m;
        Assert.True(ContainerExpenseAllocationRules.HasLegacyAllocationEvidence(expense));
        Assert.Equal(ContainerExpenseAllocationRules.LineageLegacy, ContainerExpenseAllocationRules.LineageOf(expense));
        Assert.Contains("无批次留痕", ContainerExpenseAllocationRules.LineageText(expense));

        expense.AllocationBatchNo = "EAB-20260925-001";
        expense.AllocationSourceExpenseNo = "EXP-20260925-001";
        Assert.Equal(ContainerExpenseAllocationRules.LineageBatch, ContainerExpenseAllocationRules.LineageOf(expense));
        Assert.Contains("（批次状态未知）", ContainerExpenseAllocationRules.LineageText(expense));
        Assert.Contains("有效批次", ContainerExpenseAllocationRules.LineageText(expense, 1));
        Assert.Contains("已作废批次", ContainerExpenseAllocationRules.LineageText(expense, 0));
        Assert.Contains("EXP-20260925-001", ContainerExpenseAllocationRules.LineageText(expense, 1));

        // 行数上限、空基准与来源金额必须为正
        Assert.Throws<BusinessException>(() => ContainerExpenseAllocationRules.Distribute(100m, "CNY",
            Enumerable.Range(1, ContainerExpenseAllocationRules.MaxLinesPerBatch + 1)
                .Select(i => new ContainerExpenseAllocationRules.AllocationBasis(i, 1m)).ToList()));
        Assert.Throws<BusinessException>(() => ContainerExpenseAllocationRules.Distribute(0m, "CNY",
            new List<ContainerExpenseAllocationRules.AllocationBasis> { new(1, 1m) }));
        Assert.Throws<BusinessException>(() => ContainerExpenseAllocationRules.Distribute(100m, "CNY",
            new List<ContainerExpenseAllocationRules.AllocationBasis>()));
    }
}
