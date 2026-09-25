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
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 代理服务费收款分摊证据单元测试（ERP-071）。覆盖：**显式分摊**（只按对账单 Id + 收款单 Id 的持久化标识符，
/// 绝不按文本 / 金额 / 相似度猜对应关系）、对账单**已登记**与收款单**未取消**的权威资格、客户与币种一致性、
/// 部分 / 全额与一对多 / 多对一、重复与**双向超额**拒绝、金额精度与正数、作废原因与历史保留、
/// 作废后额度释放与重新登记、两侧汇总、两侧有界候选、**证据维度分离**（不与 ERP-053 / ERP-055 相加）、
/// 有界查询（固定数据集访问、无逐行查库）、**来源记录非变更**边界，
/// 以及模型 / 幂等结构（SchemaUpgrader 第 44 段）/ 请求契约 / 前端与路由接线契约。
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何 SQL / 部署脚本、
/// 不收款 / 不付款 / 不记账 / 不核销 / 不联系客户，也不做任何浏览器 / UI 验收（浏览器验收按 browser_deferred 延后）。
/// </summary>
public class AgencyServiceFeeCollectionAllocationTests
{
    // ==================== 0. 测试脚手架 ====================

    /// <summary>构造控制器并注入测试身份（登记人由服务端按 ClaimsPrincipal 写入；无身份时记「未知用户」）</summary>
    private static AgencyServiceFeeCollectionAllocationController BuildController(ErpDbContext db, string? userName = null)
    {
        var controller = new AgencyServiceFeeCollectionAllocationController(db);
        var identity = new ClaimsIdentity(
            userName is null ? Array.Empty<Claim>() : new[] { new Claim(ClaimTypes.Name, userName) },
            "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
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

    /// <summary>登记一张既有收款单（本模块只读它：金额 / 币种 / 客户 / 状态都不会被分摊改写）</summary>
    private static FinanceReceipt SeedReceipt(
        ErpDbContext db, string receiptNo, long customerId, decimal amount = 1000m,
        Currency currency = Currency.USD, DocumentStatus status = DocumentStatus.Approved, bool deleted = false)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = new DateTime(2026, 9, 25),
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            PaymentMethod = PaymentMethod.BankTransfer,
            BankAccount = "TEST-ACCOUNT",
            Status = status,
            IsDeleted = deleted
        };
        db.FinanceReceipts.Add(receipt);
        db.SaveChanges();
        return receipt;
    }

    /// <summary>登记一条代理服务费对账单证据（ERP-070 口径：合计由服务端按已校验行计算后冻结）</summary>
    private static AgencyServiceFeeStatement SeedStatement(
        ErpDbContext db, string statementNo, long customerId, decimal totalAmount = 1000m,
        string currency = "USD", int status = AgencyServiceFeeStatementRules.StatusRecorded,
        bool deleted = false, long agreementId = 1, string agreementNo = "ASF-2026-001")
    {
        var statement = new AgencyServiceFeeStatement
        {
            StatementNo = statementNo,
            NormalizedStatementNo = AgencyServiceFeeStatementRules.NormalizeIdentityPart(statementNo),
            CustomerId = customerId,
            CustomerCode = "C001",
            CustomerName = "义乌进出口",
            Currency = currency,
            StatementDate = new DateTime(2026, 9, 20),
            ServicePeriodFrom = new DateTime(2026, 8, 1),
            ServicePeriodTo = new DateTime(2026, 8, 31),
            AgreementId = agreementId,
            AgreementNo = agreementNo,
            AgreementCurrency = currency,
            AgreementCustomerId = customerId,
            AgreementFeeMethod = "比例费率",
            AgreementTermsText = "比例费率",
            TotalAmount = totalAmount,
            Status = status,
            RecordedAt = status == AgencyServiceFeeStatementRules.StatusRecorded
                ? new DateTime(2026, 9, 21)
                : null,
            RecordedBy = status == AgencyServiceFeeStatementRules.StatusRecorded ? "张三" : string.Empty,
            IsDeleted = deleted
        };
        db.AgencyServiceFeeStatements.Add(statement);
        db.SaveChanges();
        return statement;
    }

    private static AgencyServiceFeeCollectionAllocationSaveDto SaveDto(
        long statementId, long receiptId, decimal amount = 100m, string remark = "")
        => new()
        {
            StatementId = statementId,
            ReceiptId = receiptId,
            AllocatedAmount = amount,
            Remark = remark
        };

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

    // ==================== 1. 显式分摊与快照（绝不猜对应关系） ====================

    [Fact]
    public async Task 登记分摊_写入两侧与客户快照_登记人与时间由服务端写入()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var statement = SeedStatement(db, "ASFS-2026-001", customer.Id, totalAmount: 1288.5m);
        var receipt = SeedReceipt(db, "RC-001", customer.Id, amount: 2000m);
        var controller = BuildController(db, "张三");

        var created = AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Create(SaveDto(statement.Id, receipt.Id, 1288.5m, "客户付款对应 8 月服务费对账")));

        Assert.Equal(statement.Id, created.StatementId);
        Assert.Equal("ASFS-2026-001", created.StatementNo);
        Assert.Equal(receipt.Id, created.ReceiptId);
        Assert.Equal("RC-001", created.ReceiptNo);
        Assert.Equal(customer.Id, created.CustomerId);
        Assert.Equal("C001", created.CustomerCode);
        Assert.Equal("义乌进出口", created.CustomerName);
        Assert.Equal("USD", created.Currency);
        Assert.Equal(1288.5m, created.AllocatedAmount);
        Assert.Equal("1288.50 USD", created.AllocatedAmountText);
        Assert.Equal(2000m, created.ReceiptAmount);
        Assert.Equal(1288.5m, created.StatementTotalAmount);
        Assert.Equal("张三", created.AllocatedBy);
        Assert.NotEqual(default, created.AllocatedAt);
        Assert.True(created.IsActive);
        Assert.False(created.IsVoided);
        Assert.Equal("有效", created.StatusText);
        Assert.True(created.ReceiptAvailable);
        Assert.True(created.StatementAvailable);

        // 持久化行只保存服务端写入的快照与金额（1 行，未写库其它表）
        var row = await db.AgencyServiceFeeCollectionAllocations.SingleAsync();
        Assert.Equal(AgencyServiceFeeCollectionAllocationRules.StatusActive, row.Status);
        Assert.Equal(1, row.StatementStatus);
        Assert.Equal("已登记", row.StatementStatusText);
        Assert.Equal("已审核", row.ReceiptStatusText);
        Assert.Equal(1, await db.FinanceReceipts.CountAsync());
        Assert.Equal(1, await db.AgencyServiceFeeStatements.CountAsync());
    }

    [Fact]
    public async Task 无身份时登记人记未知用户_客户端提交的快照字段不被采信()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var statement = SeedStatement(db, "ASFS-2026-001", customer.Id);
        var receipt = SeedReceipt(db, "RC-001", customer.Id);
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Create(SaveDto(statement.Id, receipt.Id, 100m)));

        Assert.Equal("未知用户", created.AllocatedBy);
        Assert.Equal("C001", created.CustomerCode);
        Assert.Equal("USD", created.Currency);
    }

    [Fact]
    public async Task 未显式选择对账单或收款单被拒绝_不做文本或相似度匹配()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var statement = SeedStatement(db, "ASFS-2026-001", customer.Id);
        var receipt = SeedReceipt(db, "RC-001", customer.Id);
        var controller = BuildController(db);

        var noStatement = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(SaveDto(0, receipt.Id, 100m)));
        Assert.Contains("显式选择", noStatement.Message);

        var noReceipt = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(SaveDto(statement.Id, 0, 100m)));
        Assert.Contains("显式选择", noReceipt.Message);
    }

    // ==================== 2. 权威资格（对账单已登记 / 收款单未取消） ====================

    [Fact]
    public async Task 对账单不存在或已删除被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var receipt = SeedReceipt(db, "RC-001", customer.Id);
        SeedStatement(db, "ASFS-2026-001", customer.Id);
        var deleted = SeedStatement(db, "ASFS-2026-002", customer.Id, deleted: true);
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(SaveDto(9999, receipt.Id, 100m)));

        var ex = await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(SaveDto(deleted.Id, receipt.Id, 100m)));
        Assert.Contains("不存在或已删除", ex.Message);
    }

    [Fact]
    public async Task 草稿或已作废的对账单不能承接分摊()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var receipt = SeedReceipt(db, "RC-001", customer.Id);
        var draft = SeedStatement(db, "ASFS-DRAFT-001", customer.Id,
            status: AgencyServiceFeeStatementRules.StatusDraft);
        var voided = SeedStatement(db, "ASFS-VOID-001", customer.Id,
            status: AgencyServiceFeeStatementRules.StatusVoided);
        var controller = BuildController(db);

        var draftEx = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(SaveDto(draft.Id, receipt.Id, 100m)));
        Assert.Contains("只有**已登记**的对账单证据可以承接收款分摊", draftEx.Message);
        Assert.Contains("草稿", draftEx.Message);

        var voidedEx = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(SaveDto(voided.Id, receipt.Id, 100m)));
        Assert.Contains("已作废", voidedEx.Message);

        Assert.Equal(0, await db.AgencyServiceFeeCollectionAllocations.CountAsync());
    }

    [Fact]
    public async Task 收款单不存在或已删除被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var statement = SeedStatement(db, "ASFS-2026-001", customer.Id);
        var deleted = SeedReceipt(db, "RC-DEL", customer.Id, deleted: true);
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(SaveDto(statement.Id, 9999, 100m)));

        var ex = await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(SaveDto(statement.Id, deleted.Id, 100m)));
        Assert.Contains("不存在或已删除", ex.Message);
    }

    [Fact]
    public async Task 已取消的收款单不能登记新分摊_历史分摊仍可读()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var statement = SeedStatement(db, "ASFS-2026-001", customer.Id);
        var cancelled = SeedReceipt(db, "RC-CAN", customer.Id, status: DocumentStatus.Cancelled);
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(SaveDto(statement.Id, cancelled.Id, 100m)));
        Assert.Contains("已取消", ex.Message);
        Assert.Equal(0, await db.AgencyServiceFeeCollectionAllocations.CountAsync());
    }

    [Fact]
    public async Task 客户不一致被拒绝_不做跨客户合并()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "义乌进出口");
        var customerB = SeedCustomer(db, "C002", "杭州贸易");
        var statement = SeedStatement(db, "ASFS-2026-001", customerA.Id);
        var otherReceipt = SeedReceipt(db, "RC-OTHER", customerB.Id);
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(SaveDto(statement.Id, otherReceipt.Id, 100m)));
        Assert.Contains("客户", ex.Message);
        Assert.Contains("不一致", ex.Message);
        Assert.Contains("跨客户合并", ex.Message);
        Assert.Equal(0, await db.AgencyServiceFeeCollectionAllocations.CountAsync());
    }

    [Fact]
    public async Task 币种不一致被拒绝且不做汇率换算()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var statement = SeedStatement(db, "ASFS-2026-001", customer.Id, currency: "USD");
        var cnyReceipt = SeedReceipt(db, "RC-CNY", customer.Id, currency: Currency.CNY);
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(SaveDto(statement.Id, cnyReceipt.Id, 100m)));
        Assert.Contains("币种", ex.Message);
        Assert.Contains("不做汇率换算", ex.Message);
        Assert.Equal(0, await db.AgencyServiceFeeCollectionAllocations.CountAsync());
    }

    [Fact]
    public async Task 对账单客户已停用或已删除时不能登记新分摊()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口", status: 0);
        var statement = SeedStatement(db, "ASFS-2026-001", customer.Id);
        var receipt = SeedReceipt(db, "RC-001", customer.Id);
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(SaveDto(statement.Id, receipt.Id, 100m)));
        Assert.Contains("已停用", ex.Message);
        Assert.Equal(0, await db.AgencyServiceFeeCollectionAllocations.CountAsync());
    }

    // ==================== 3. 金额精度与正数 ====================

    [Fact]
    public async Task 分摊金额按币种精度取整且必须大于0()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var statement = SeedStatement(db, "ASFS-2026-001", customer.Id, totalAmount: 1000m);
        var receipt = SeedReceipt(db, "RC-001", customer.Id, amount: 1000m);
        var controller = BuildController(db);

        // 0.5 进位（AwayFromZero）：100.005 → 100.01
        var rounded = AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Create(SaveDto(statement.Id, receipt.Id, 100.005m)));
        Assert.Equal(100.01m, rounded.AllocatedAmount);
        Assert.Equal("100.01 USD", rounded.AllocatedAmountText);

        var zero = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(SaveDto(statement.Id, receipt.Id, 0m)));
        Assert.Contains("必须大于 0", zero.Message);

        var tiny = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(SaveDto(statement.Id, receipt.Id, 0.004m)));
        Assert.Contains("精度取整后为 0", tiny.Message);

        var negative = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(SaveDto(statement.Id, receipt.Id, -10m)));
        Assert.Contains("必须大于 0", negative.Message);
    }

    [Fact]
    public async Task 无小数币种按整数取整_备注超长被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var statement = SeedStatement(db, "ASFS-JPY-001", customer.Id,
            totalAmount: 100000m, currency: "JPY");
        var receipt = SeedReceipt(db, "RC-JPY-001", customer.Id, amount: 100000m, currency: Currency.JPY);
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Create(SaveDto(statement.Id, receipt.Id, 100.6m)));
        Assert.Equal(101m, created.AllocatedAmount);
        Assert.Equal("101 JPY", created.AllocatedAmountText);
        Assert.Equal(0, created.AmountDecimals);

        var tooLong = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(SaveDto(statement.Id, receipt.Id, 10m, new string('注', 501))));
        Assert.Contains("备注长度不能超过", tooLong.Message);
    }

    // ==================== 4. 重复与双向超额 ====================

    [Fact]
    public async Task 同一对账单与收款单重复分摊被拒绝而不是合并()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var statement = SeedStatement(db, "ASFS-2026-001", customer.Id, totalAmount: 1000m);
        var receipt = SeedReceipt(db, "RC-001", customer.Id, amount: 1000m);
        var controller = BuildController(db);

        AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Create(SaveDto(statement.Id, receipt.Id, 300m)));

        var ex = await AssertBusinessAsync(ErrorCodes.Duplicate,
            () => controller.Create(SaveDto(statement.Id, receipt.Id, 100m)));
        Assert.Contains("同一对账单与收款单只允许一条有效分摊行", ex.Message);
        Assert.Contains("拒绝而不是合并或覆盖", ex.Message);
        Assert.Equal(1, await db.AgencyServiceFeeCollectionAllocations.CountAsync());
    }

    [Fact]
    public async Task 超过收款单可分摊余额被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var statementA = SeedStatement(db, "ASFS-2026-001", customer.Id, totalAmount: 1000m);
        var statementB = SeedStatement(db, "ASFS-2026-002", customer.Id, totalAmount: 1000m);
        var receipt = SeedReceipt(db, "RC-001", customer.Id, amount: 400m);
        var controller = BuildController(db);

        AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Create(SaveDto(statementA.Id, receipt.Id, 300m)));

        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(SaveDto(statementB.Id, receipt.Id, 150m)));
        Assert.Contains("可分摊余额为 100", ex.Message);
        Assert.Contains("不做超额分摊", ex.Message);
        Assert.Equal(1, await db.AgencyServiceFeeCollectionAllocations.CountAsync());
    }

    [Fact]
    public async Task 超过对账单未分摊额被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var statement = SeedStatement(db, "ASFS-2026-001", customer.Id, totalAmount: 500m);
        var receiptA = SeedReceipt(db, "RC-001", customer.Id, amount: 1000m);
        var receiptB = SeedReceipt(db, "RC-002", customer.Id, amount: 1000m);
        var controller = BuildController(db);

        AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Create(SaveDto(statement.Id, receiptA.Id, 400m)));

        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(SaveDto(statement.Id, receiptB.Id, 150m)));
        Assert.Contains("未分摊额为 100", ex.Message);
        Assert.Contains("不做超额分摊", ex.Message);
        Assert.Equal(1, await db.AgencyServiceFeeCollectionAllocations.CountAsync());
    }

    // ==================== 5. 部分 / 全额与一对多 / 多对一 ====================

    [Fact]
    public async Task 部分分摊_两侧未分摊金额分别可见且不被核销()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var statement = SeedStatement(db, "ASFS-2026-001", customer.Id, totalAmount: 1000m);
        var receipt = SeedReceipt(db, "RC-001", customer.Id, amount: 600m);
        var controller = BuildController(db);

        AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Create(SaveDto(statement.Id, receipt.Id, 250m)));

        var statementSummary = AssertOk<AgencyServiceFeeCollectionAllocationStatementSummaryDto>(
            await controller.StatementSummary(statement.Id));
        Assert.Equal(1000m, statementSummary.StatementTotalAmount);
        Assert.Equal(250m, statementSummary.AllocatedAmount);
        Assert.Equal(750m, statementSummary.UnallocatedAmount);
        Assert.Equal("partial", statementSummary.LinkageStatus);
        Assert.Equal(1, statementSummary.AllocationCount);
        Assert.Equal(0, statementSummary.VoidedCount);
        Assert.Contains("部分分摊", statementSummary.LinkageText);
        Assert.Contains("未分摊 750", statementSummary.LinkageText);
        Assert.Contains("也不代表已付 / 已结清 / 逾期 / 收入确认 / 记账状态或应收余额", statementSummary.LinkageText);

        var receiptSummary = AssertOk<AgencyServiceFeeCollectionAllocationReceiptSummaryDto>(
            await controller.ReceiptSummary(receipt.Id));
        Assert.Equal(600m, receiptSummary.ReceiptAmount);
        Assert.Equal(250m, receiptSummary.AllocatedAmount);
        Assert.Equal(350m, receiptSummary.UnallocatedAmount);
        Assert.Equal("partial", receiptSummary.LinkageStatus);
        Assert.Contains("未分摊 350", receiptSummary.LinkageText);

        // 未分摊金额只作为算术证据展示：收款单与对账单本身没有任何列被改写
        var receiptRow = await db.FinanceReceipts.SingleAsync();
        Assert.Equal(600m, receiptRow.Amount);
        Assert.Equal(DocumentStatus.Approved, receiptRow.Status);
        var statementRow = await db.AgencyServiceFeeStatements.SingleAsync();
        Assert.Equal(1000m, statementRow.TotalAmount);
        Assert.Equal(AgencyServiceFeeStatementRules.StatusRecorded, statementRow.Status);
    }

    [Fact]
    public async Task 一张收款单分摊到两张对账单_一对多()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var statementA = SeedStatement(db, "ASFS-2026-001", customer.Id, totalAmount: 300m);
        var statementB = SeedStatement(db, "ASFS-2026-002", customer.Id, totalAmount: 700m);
        var receipt = SeedReceipt(db, "RC-001", customer.Id, amount: 1000m);
        var controller = BuildController(db);

        AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Create(SaveDto(statementA.Id, receipt.Id, 300m)));
        AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Create(SaveDto(statementB.Id, receipt.Id, 700m)));

        var summaryA = AssertOk<AgencyServiceFeeCollectionAllocationStatementSummaryDto>(
            await controller.StatementSummary(statementA.Id));
        var summaryB = AssertOk<AgencyServiceFeeCollectionAllocationStatementSummaryDto>(
            await controller.StatementSummary(statementB.Id));
        Assert.Equal("fully_allocated", summaryA.LinkageStatus);
        Assert.Equal(0m, summaryA.UnallocatedAmount);
        Assert.Equal("fully_allocated", summaryB.LinkageStatus);
        Assert.Equal(0m, summaryB.UnallocatedAmount);

        var receiptSummary = AssertOk<AgencyServiceFeeCollectionAllocationReceiptSummaryDto>(
            await controller.ReceiptSummary(receipt.Id));
        Assert.Equal(1000m, receiptSummary.AllocatedAmount);
        Assert.Equal(0m, receiptSummary.UnallocatedAmount);
        Assert.Equal("fully_allocated", receiptSummary.LinkageStatus);
        Assert.Equal(2, receiptSummary.AllocationCount);
        Assert.Contains("全部指向 2 条记录", receiptSummary.LinkageText);
    }

    [Fact]
    public async Task 两张收款单分摊到同一对账单_多对一_各自余额分别可见()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var statement = SeedStatement(db, "ASFS-2026-001", customer.Id, totalAmount: 1000m);
        var receiptA = SeedReceipt(db, "RC-001", customer.Id, amount: 600m);
        var receiptB = SeedReceipt(db, "RC-002", customer.Id, amount: 600m);
        var controller = BuildController(db);

        AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Create(SaveDto(statement.Id, receiptA.Id, 400m)));
        AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Create(SaveDto(statement.Id, receiptB.Id, 600m)));

        var statementSummary = AssertOk<AgencyServiceFeeCollectionAllocationStatementSummaryDto>(
            await controller.StatementSummary(statement.Id));
        Assert.Equal(1000m, statementSummary.AllocatedAmount);
        Assert.Equal(0m, statementSummary.UnallocatedAmount);
        Assert.Equal("fully_allocated", statementSummary.LinkageStatus);
        Assert.Equal(2, statementSummary.AllocationCount);

        // 收款单各自的未分摊金额分别可见（不等额拆分，绝不轧差）
        var summaryA = AssertOk<AgencyServiceFeeCollectionAllocationReceiptSummaryDto>(
            await controller.ReceiptSummary(receiptA.Id));
        var summaryB = AssertOk<AgencyServiceFeeCollectionAllocationReceiptSummaryDto>(
            await controller.ReceiptSummary(receiptB.Id));
        Assert.Equal(400m, summaryA.AllocatedAmount);
        Assert.Equal(200m, summaryA.UnallocatedAmount);
        Assert.Equal(600m, summaryB.AllocatedAmount);
        Assert.Equal(0m, summaryB.UnallocatedAmount);
    }

    [Fact]
    public async Task 单侧有效行数有界_超限被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var statement = SeedStatement(db, "ASFS-MANY-001", customer.Id, totalAmount: 1000m);
        var receipt = SeedReceipt(db, "RC-MANY-001", customer.Id, amount: 100000m);
        var controller = BuildController(db);

        for (var i = 0; i < AgencyServiceFeeCollectionAllocationRules.MaxAllocationsPerReceipt; i++)
        {
            var otherStatement = SeedStatement(db, $"ASFS-BULK-{i:d4}", customer.Id, totalAmount: 10m);
            db.AgencyServiceFeeCollectionAllocations.Add(new AgencyServiceFeeCollectionAllocation
            {
                StatementId = otherStatement.Id,
                StatementNo = otherStatement.StatementNo,
                StatementDate = otherStatement.StatementDate,
                StatementStatus = AgencyServiceFeeStatementRules.StatusRecorded,
                StatementStatusText = "已登记",
                StatementTotalAmount = 10m,
                StatementCurrency = "USD",
                ReceiptId = receipt.Id,
                ReceiptNo = receipt.ReceiptNo,
                ReceiptDate = receipt.ReceiptDate,
                ReceiptStatus = (int)receipt.Status,
                ReceiptStatusText = "已审核",
                ReceiptAmount = 100000m,
                CustomerId = customer.Id,
                CustomerCode = "C001",
                CustomerName = "义乌进出口",
                AllocatedAmount = 1m,
                Currency = "USD",
                Status = AgencyServiceFeeCollectionAllocationRules.StatusActive,
                AllocatedAt = DateTime.Now,
                AllocatedBy = "张三"
            });
        }
        await db.SaveChangesAsync();

        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(SaveDto(statement.Id, receipt.Id, 1m)));
        Assert.Contains("收款单「RC-MANY-001」的有效分摊行已达上限", ex.Message);
        Assert.Contains("作废保留历史，不物理删除", ex.Message);
    }

    // ==================== 6. 作废与历史保留 ====================

    [Fact]
    public async Task 作废原因必填且有界_重复作废被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var statement = SeedStatement(db, "ASFS-2026-001", customer.Id);
        var receipt = SeedReceipt(db, "RC-001", customer.Id);
        var controller = BuildController(db, "张三");

        var created = AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Create(SaveDto(statement.Id, receipt.Id, 100m)));

        var empty = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Void(created.Id, new AgencyServiceFeeCollectionAllocationVoidRequest { Reason = "   " }));
        Assert.Contains("请填写作废原因", empty.Message);

        var tooLong = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Void(created.Id,
                new AgencyServiceFeeCollectionAllocationVoidRequest { Reason = new string('原', 501) }));
        Assert.Contains("作废原因长度不能超过", tooLong.Message);

        var noBody = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Void(created.Id, null));
        Assert.Contains("请填写作废原因", noBody.Message);

        AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Void(created.Id,
                new AgencyServiceFeeCollectionAllocationVoidRequest { Reason = "分摊口径更正" }));

        var again = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Void(created.Id, new AgencyServiceFeeCollectionAllocationVoidRequest { Reason = "再次作废" }));
        Assert.Contains("不能重复作废", again.Message);
    }

    [Fact]
    public async Task 作废保留原始金额快照与登记人_历史仍可读()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var statement = SeedStatement(db, "ASFS-2026-001", customer.Id, totalAmount: 1000m);
        var receipt = SeedReceipt(db, "RC-001", customer.Id, amount: 900m);
        var controller = BuildController(db, "李四");

        var created = AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Create(SaveDto(statement.Id, receipt.Id, 288.5m, "首次分摊")));
        var allocatedAt = created.AllocatedAt;

        var voided = AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Void(created.Id,
                new AgencyServiceFeeCollectionAllocationVoidRequest { Reason = "收款单选择错误" }));

        Assert.True(voided.IsVoided);
        Assert.False(voided.IsActive);
        Assert.Equal("已作废", voided.StatusText);
        Assert.Equal(288.5m, voided.AllocatedAmount);
        Assert.Equal("288.50 USD", voided.AllocatedAmountText);
        Assert.Equal("首次分摊", voided.Remark);
        Assert.Equal("李四", voided.AllocatedBy);
        Assert.Equal(allocatedAt, voided.AllocatedAt);
        Assert.NotNull(voided.VoidedAt);
        Assert.Equal("收款单选择错误", voided.VoidReason);
        Assert.Equal(customer.Id, voided.CustomerId);
        Assert.Equal("C001", voided.CustomerCode);

        // 行仍可读（详情 + 台账默认含已作废历史）
        var detail = AssertOk<AgencyServiceFeeCollectionAllocationDto>(await controller.GetById(created.Id));
        Assert.Equal(288.5m, detail.AllocatedAmount);
        Assert.Equal("收款单选择错误", detail.VoidReason);

        var ledger = AssertOk<PagedResult<AgencyServiceFeeCollectionAllocationDto>>(
            await controller.GetPaged(new AgencyServiceFeeCollectionAllocationQuery()));
        Assert.Single(ledger.Items);
        Assert.Equal(288.5m, ledger.Items[0].AllocatedAmount);

        // 已作废行不再占用两侧额度（对账单未分摊额恢复为合计）
        var statementSummary = AssertOk<AgencyServiceFeeCollectionAllocationStatementSummaryDto>(
            await controller.StatementSummary(statement.Id));
        Assert.Equal(0m, statementSummary.AllocatedAmount);
        Assert.Equal(1000m, statementSummary.UnallocatedAmount);
        Assert.Equal(0, statementSummary.AllocationCount);
        Assert.Equal(1, statementSummary.VoidedCount);
        Assert.Equal("unallocated", statementSummary.LinkageStatus);
    }

    [Fact]
    public async Task 作废后同一组合可重新登记_新旧并存可查且额度已释放()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var statement = SeedStatement(db, "ASFS-2026-001", customer.Id, totalAmount: 1000m);
        var receipt = SeedReceipt(db, "RC-001", customer.Id, amount: 1000m);
        var controller = BuildController(db, "张三");

        var first = AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Create(SaveDto(statement.Id, receipt.Id, 1000m)));
        AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Void(first.Id,
                new AgencyServiceFeeCollectionAllocationVoidRequest { Reason = "金额口径更正" }));

        var second = AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Create(SaveDto(statement.Id, receipt.Id, 600m)));

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(600m, second.AllocatedAmount);

        var rows = await db.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(AgencyServiceFeeCollectionAllocationRules.StatusVoided, rows[0].Status);
        Assert.Equal(1000m, rows[0].AllocatedAmount);      // 原始值保留，绝不静默替换
        Assert.Equal(AgencyServiceFeeCollectionAllocationRules.StatusActive, rows[1].Status);

        var statementSummary = AssertOk<AgencyServiceFeeCollectionAllocationStatementSummaryDto>(
            await controller.StatementSummary(statement.Id));
        Assert.Equal(600m, statementSummary.AllocatedAmount);
        Assert.Equal(400m, statementSummary.UnallocatedAmount);
        Assert.Equal(1, statementSummary.AllocationCount);
        Assert.Equal(1, statementSummary.VoidedCount);
    }

    [Fact]
    public async Task 分摊行不存在或非法Id时详情与作废被拒绝()
    {
        using var db = TestDbFactory.Create();
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetById(0));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.GetById(9999));
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Void(9999, new AgencyServiceFeeCollectionAllocationVoidRequest { Reason = "不存在" }));
    }

    // ==================== 7. 台账 / 清单 / 候选 / 元数据（只读、有界） ====================

    [Fact]
    public async Task 台账支持对账单_收款单_客户_状态_币种_时间区间与关键字过滤()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "义乌进出口");
        var customerB = SeedCustomer(db, "C002", "杭州贸易");
        var statementA = SeedStatement(db, "ASFS-2026-001", customerA.Id, totalAmount: 1000m);
        var statementB = SeedStatement(db, "ASFS-2026-002", customerA.Id, totalAmount: 1000m);
        var statementC = SeedStatement(db, "ASFS-2026-003", customerB.Id, totalAmount: 1000m);
        var receiptA = SeedReceipt(db, "RC-001", customerA.Id, amount: 1000m);
        var receiptB = SeedReceipt(db, "RC-002", customerA.Id, amount: 1000m);
        var receiptC = SeedReceipt(db, "RC-003", customerB.Id, amount: 1000m);
        var controller = BuildController(db, "张三");

        var first = AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Create(SaveDto(statementA.Id, receiptA.Id, 100m, "首笔")));
        AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Create(SaveDto(statementB.Id, receiptB.Id, 200m, "第二笔")));
        AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Create(SaveDto(statementC.Id, receiptC.Id, 300m, "第三笔")));
        AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Void(first.Id, new AgencyServiceFeeCollectionAllocationVoidRequest { Reason = "更正" }));

        var byStatement = AssertOk<PagedResult<AgencyServiceFeeCollectionAllocationDto>>(
            await controller.GetPaged(new AgencyServiceFeeCollectionAllocationQuery { StatementId = statementB.Id }));
        Assert.Single(byStatement.Items);
        Assert.Equal(statementB.Id, byStatement.Items[0].StatementId);

        var byReceipt = AssertOk<PagedResult<AgencyServiceFeeCollectionAllocationDto>>(
            await controller.GetPaged(new AgencyServiceFeeCollectionAllocationQuery { ReceiptId = receiptC.Id }));
        Assert.Single(byReceipt.Items);

        var byCustomer = AssertOk<PagedResult<AgencyServiceFeeCollectionAllocationDto>>(
            await controller.GetPaged(new AgencyServiceFeeCollectionAllocationQuery { CustomerId = customerA.Id }));
        Assert.Equal(2, byCustomer.Total);
        Assert.All(byCustomer.Items, x => Assert.Equal(customerA.Id, x.CustomerId));

        var voidedOnly = AssertOk<PagedResult<AgencyServiceFeeCollectionAllocationDto>>(
            await controller.GetPaged(new AgencyServiceFeeCollectionAllocationQuery
            {
                Status = AgencyServiceFeeCollectionAllocationRules.StatusVoided
            }));
        Assert.Single(voidedOnly.Items);
        Assert.True(voidedOnly.Items[0].IsVoided);

        var activeOnly = AssertOk<PagedResult<AgencyServiceFeeCollectionAllocationDto>>(
            await controller.GetPaged(new AgencyServiceFeeCollectionAllocationQuery
            {
                Status = AgencyServiceFeeCollectionAllocationRules.StatusActive
            }));
        Assert.Equal(2, activeOnly.Total);

        var byCurrency = AssertOk<PagedResult<AgencyServiceFeeCollectionAllocationDto>>(
            await controller.GetPaged(new AgencyServiceFeeCollectionAllocationQuery { Currency = "USD" }));
        Assert.Equal(3, byCurrency.Total);

        var byDate = AssertOk<PagedResult<AgencyServiceFeeCollectionAllocationDto>>(
            await controller.GetPaged(new AgencyServiceFeeCollectionAllocationQuery
            {
                AllocatedDateFrom = AllocationToday(),
                AllocatedDateTo = AllocationToday()
            }));
        Assert.Equal(3, byDate.Total);

        var yesterdayOnly = AssertOk<PagedResult<AgencyServiceFeeCollectionAllocationDto>>(
            await controller.GetPaged(new AgencyServiceFeeCollectionAllocationQuery
            {
                AllocatedDateFrom = AllocationToday().AddDays(-1),
                AllocatedDateTo = AllocationToday().AddDays(-1)
            }));
        Assert.Equal(0, yesterdayOnly.Total);

        var byKeyword = AssertOk<PagedResult<AgencyServiceFeeCollectionAllocationDto>>(
            await controller.GetPaged(new AgencyServiceFeeCollectionAllocationQuery { Keyword = "杭州" }));
        Assert.Single(byKeyword.Items);

        var byReceiptNo = AssertOk<PagedResult<AgencyServiceFeeCollectionAllocationDto>>(
            await controller.GetPaged(new AgencyServiceFeeCollectionAllocationQuery { Keyword = "RC-002" }));
        Assert.Single(byReceiptNo.Items);
    }

    private static DateTime AllocationToday() => DateTime.Now.Date;

    [Fact]
    public async Task 台账未知筛选取值与超长关键字被拒绝()
    {
        using var db = TestDbFactory.Create();
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetPaged(
            new AgencyServiceFeeCollectionAllocationQuery { Status = 7 }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetPaged(
            new AgencyServiceFeeCollectionAllocationQuery { Currency = "XXX" }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetPaged(
            new AgencyServiceFeeCollectionAllocationQuery { Keyword = new string('关', 101) }));
    }

    [Fact]
    public async Task 台账分页有界且按登记时间倒序()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var receipt = SeedReceipt(db, "RC-001", customer.Id, amount: 100000m);
        var controller = BuildController(db);

        for (var i = 0; i < 5; i++)
        {
            var statement = SeedStatement(db, $"ASFS-PAGE-{i:d3}", customer.Id, totalAmount: 100m);
            AssertOk<AgencyServiceFeeCollectionAllocationDto>(
                await controller.Create(SaveDto(statement.Id, receipt.Id, 10m + i)));
        }

        var page1 = AssertOk<PagedResult<AgencyServiceFeeCollectionAllocationDto>>(
            await controller.GetPaged(new AgencyServiceFeeCollectionAllocationQuery
            {
                Page = 1,
                PageSize = 2
            }));
        Assert.Equal(5, page1.Total);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(2, page1.PageSize);

        var page2 = AssertOk<PagedResult<AgencyServiceFeeCollectionAllocationDto>>(
            await controller.GetPaged(new AgencyServiceFeeCollectionAllocationQuery
            {
                Page = 2,
                PageSize = 2
            }));
        Assert.Equal(2, page2.Items.Count);
        Assert.DoesNotContain(page2.Items, x => page1.Items.Any(y => y.Id == x.Id));

        // 每页上限有界（请求 5000 条也被截断到 MaxPageSize，且不报错）
        var capped = AssertOk<PagedResult<AgencyServiceFeeCollectionAllocationDto>>(
            await controller.GetPaged(new AgencyServiceFeeCollectionAllocationQuery { PageSize = 5000 }));
        Assert.Equal(AgencyServiceFeeCollectionAllocationQuery.MaxPageSize, capped.PageSize);
        Assert.True(capped.Items.Count <= AgencyServiceFeeCollectionAllocationQuery.MaxPageSize);
    }

    [Fact]
    public async Task 对账单侧与收款单侧清单只读有界且可按状态筛选()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var statement = SeedStatement(db, "ASFS-2026-001", customer.Id, totalAmount: 1000m);
        var receiptA = SeedReceipt(db, "RC-001", customer.Id, amount: 600m);
        var receiptB = SeedReceipt(db, "RC-002", customer.Id, amount: 600m);
        var controller = BuildController(db, "张三");

        var first = AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Create(SaveDto(statement.Id, receiptA.Id, 200m)));
        AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Create(SaveDto(statement.Id, receiptB.Id, 300m)));
        AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Void(first.Id, new AgencyServiceFeeCollectionAllocationVoidRequest { Reason = "更正" }));

        var forStatement = AssertOk<List<AgencyServiceFeeCollectionAllocationDto>>(
            await controller.AllocationsForStatement(statement.Id));
        Assert.Equal(2, forStatement.Count);

        var activeOnly = AssertOk<List<AgencyServiceFeeCollectionAllocationDto>>(
            await controller.AllocationsForStatement(statement.Id,
                AgencyServiceFeeCollectionAllocationRules.StatusActive));
        Assert.Single(activeOnly);
        Assert.Equal(receiptB.Id, activeOnly[0].ReceiptId);

        var voidedOnly = AssertOk<List<AgencyServiceFeeCollectionAllocationDto>>(
            await controller.AllocationsForStatement(statement.Id,
                AgencyServiceFeeCollectionAllocationRules.StatusVoided));
        Assert.Single(voidedOnly);
        Assert.Equal(200m, voidedOnly[0].AllocatedAmount);

        var forReceipt = AssertOk<List<AgencyServiceFeeCollectionAllocationDto>>(
            await controller.AllocationsForReceipt(receiptA.Id));
        Assert.Single(forReceipt);
        Assert.True(forReceipt[0].IsVoided);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.AllocationsForStatement(0));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.AllocationsForStatement(statement.Id, 9));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.AllocationsForReceipt(0));
    }

    [Fact]
    public async Task 收款单候选_必须给出客户与币种_只返回未删除未取消并逐条给出资格文案()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var otherCustomer = SeedCustomer(db, "C002", "杭州贸易");
        var statement = SeedStatement(db, "ASFS-2026-001", customer.Id, totalAmount: 500m);
        var eligible = SeedReceipt(db, "RC-OK-001", customer.Id, amount: 500m);
        SeedReceipt(db, "RC-CAN-001", customer.Id, amount: 500m, status: DocumentStatus.Cancelled);
        SeedReceipt(db, "RC-DEL-001", customer.Id, deleted: true);
        SeedReceipt(db, "RC-EUR-001", customer.Id, currency: Currency.EUR);
        SeedReceipt(db, "RC-OTHER-001", otherCustomer.Id);
        var controller = BuildController(db);

        var missingCustomer = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.ReceiptCandidates(0, "USD", null));
        Assert.Contains("请先选择客户", missingCustomer.Message);

        var missingCurrency = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.ReceiptCandidates(customer.Id, null, null));
        Assert.Contains("请先选择对账单币种", missingCurrency.Message);

        var options = AssertOk<List<AgencyServiceFeeCollectionAllocationReceiptCandidateDto>>(
            await controller.ReceiptCandidates(customer.Id, "USD", null));
        Assert.Single(options);
        Assert.Equal(eligible.Id, options[0].ReceiptId);
        Assert.Equal("USD", options[0].Currency);
        Assert.Equal(500m, options[0].ReceiptAmount);
        Assert.Equal(0m, options[0].AllocatedAmount);
        Assert.Equal(500m, options[0].UnallocatedAmount);
        Assert.True(options[0].Eligible);
        Assert.Contains("可分摊", options[0].EligibilityText);

        // 占满后候选仍可见但标注不可分摊（逐条给出原因，不静默隐藏）
        AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Create(SaveDto(statement.Id, eligible.Id, 500m)));
        var afterFull = AssertOk<List<AgencyServiceFeeCollectionAllocationReceiptCandidateDto>>(
            await controller.ReceiptCandidates(customer.Id, "USD", null));
        Assert.Single(afterFull);
        Assert.False(afterFull[0].Eligible);
        Assert.Contains("已被有效分摊行占满", afterFull[0].EligibilityText);
        Assert.Equal(0m, afterFull[0].UnallocatedAmount);
    }

    [Fact]
    public async Task 对账单候选_只列已登记对账单_草稿与已作废不出现()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var recorded = SeedStatement(db, "ASFS-REC-001", customer.Id, totalAmount: 800m);
        SeedStatement(db, "ASFS-DRAFT-001", customer.Id,
            status: AgencyServiceFeeStatementRules.StatusDraft);
        SeedStatement(db, "ASFS-VOID-001", customer.Id,
            status: AgencyServiceFeeStatementRules.StatusVoided);
        SeedStatement(db, "ASFS-DEL-001", customer.Id, deleted: true);
        SeedStatement(db, "ASFS-EUR-001", customer.Id, currency: "EUR");
        var receipt = SeedReceipt(db, "RC-001", customer.Id, amount: 1000m);
        var controller = BuildController(db);

        var options = AssertOk<List<AgencyServiceFeeCollectionAllocationStatementCandidateDto>>(
            await controller.StatementCandidates(customer.Id, "USD", null));
        Assert.Single(options);
        Assert.Equal(recorded.Id, options[0].StatementId);
        Assert.Equal("已登记", options[0].StatementStatusText);
        Assert.Equal(800m, options[0].StatementTotalAmount);
        Assert.Equal(800m, options[0].UnallocatedAmount);
        Assert.True(options[0].Eligible);

        AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Create(SaveDto(recorded.Id, receipt.Id, 300m)));
        var afterPartial = AssertOk<List<AgencyServiceFeeCollectionAllocationStatementCandidateDto>>(
            await controller.StatementCandidates(customer.Id, "USD", null));
        Assert.Equal(300m, afterPartial[0].AllocatedAmount);
        Assert.Equal(500m, afterPartial[0].UnallocatedAmount);
        Assert.True(afterPartial[0].Eligible);

        var byKeyword = AssertOk<List<AgencyServiceFeeCollectionAllocationStatementCandidateDto>>(
            await controller.StatementCandidates(customer.Id, "USD", "ASFS-REC"));
        Assert.Single(byKeyword);

        var missingCurrency = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.StatementCandidates(customer.Id, null, null));
        Assert.Contains("请先选择币种", missingCurrency.Message);
    }

    [Fact]
    public void 元数据_有界额度与口径文案与文档同源()
    {
        var metadata = AgencyServiceFeeCollectionAllocationService.GetMetadata();

        Assert.Equal(AgencyServiceFeeCollectionAllocationRules.SupportedCurrencies.ToList(),
            metadata.SupportedCurrencies);
        Assert.Equal(AgencyServiceFeeCollectionAllocationRules.MaxAllocationsPerStatement,
            metadata.MaxAllocationsPerStatement);
        Assert.Equal(AgencyServiceFeeCollectionAllocationRules.MaxAllocationsPerReceipt,
            metadata.MaxAllocationsPerReceipt);
        Assert.Equal(AgencyServiceFeeCollectionAllocationService.MaxReceiptCandidates, metadata.MaxReceiptCandidates);
        Assert.Equal(AgencyServiceFeeCollectionAllocationService.MaxStatementCandidates, metadata.MaxStatementCandidates);
        Assert.Equal(AgencyServiceFeeCollectionAllocationQuery.MaxPageSize, metadata.MaxPageSize);

        Assert.Contains("持久化标识符", metadata.RuleText);
        Assert.Contains("不得超过收款单可分摊余额与对账单未分摊额", metadata.RuleText);
        Assert.Contains("绝不被静默核销或改派", metadata.AmountRuleText);
        Assert.Contains("绝不相加", metadata.DimensionSeparationText);
        Assert.Contains("ERP-053", metadata.DimensionSeparationText);
        Assert.Contains("ERP-055", metadata.DimensionSeparationText);
        Assert.Contains("未作废", metadata.UniquenessRuleText);
        Assert.Contains("历史分摊行仍按登记当时的收款单与对账单快照可读", metadata.HistoricalEvidenceText);
        Assert.Contains("不是银行入账 / 到账凭证", metadata.BoundaryText);
        Assert.Contains("不会真的收款或付款", metadata.BoundaryText);
    }

    // ==================== 8. 有界查询（固定数据集访问、无逐行查库、只读不写库） ====================

    [Fact]
    public async Task 台账读取是固定数量的数据集访问_行数变化不改变访问次数且只读不写库()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var receipt = SeedReceipt(db, "RC-001", customer.Id, amount: 100000m);
        var statement = SeedStatement(db, "ASFS-2026-001", customer.Id, totalAmount: 100m);
        var controller = BuildController(db);
        AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Create(SaveDto(statement.Id, receipt.Id, 50m)));

        var counting = AllocationReadCounter.Wrap(db);
        var single = await AgencyServiceFeeCollectionAllocationService.ListAsync(
            counting.Proxy, new AgencyServiceFeeCollectionAllocationQuery { PageSize = 1 });
        var singleReads = counting.DatasetReads;

        Assert.Equal(1, single.Total);
        Assert.Equal(
            new[]
            {
                nameof(IErpDbContext.AgencyServiceFeeCollectionAllocations),
                nameof(IErpDbContext.FinanceReceipts),
                nameof(IErpDbContext.AgencyServiceFeeStatements)
            },
            counting.ReadProperties.Distinct().ToArray());

        // 再补 300 条（跨多页）：数据集访问次数必须保持不变（无逐行查库、无 N+1）
        for (var i = 0; i < 300; i++)
        {
            var bulkStatement = SeedStatement(db, $"ASFS-BULK-{i:d4}", customer.Id, totalAmount: 10m);
            var bulkReceipt = SeedReceipt(db, $"RC-BULK-{i:d4}", customer.Id, amount: 10m);
            db.AgencyServiceFeeCollectionAllocations.Add(new AgencyServiceFeeCollectionAllocation
            {
                StatementId = bulkStatement.Id,
                StatementNo = bulkStatement.StatementNo,
                StatementDate = bulkStatement.StatementDate,
                StatementStatus = AgencyServiceFeeStatementRules.StatusRecorded,
                StatementStatusText = "已登记",
                StatementTotalAmount = 10m,
                StatementCurrency = "USD",
                ReceiptId = bulkReceipt.Id,
                ReceiptNo = bulkReceipt.ReceiptNo,
                ReceiptDate = bulkReceipt.ReceiptDate,
                ReceiptStatus = (int)bulkReceipt.Status,
                ReceiptStatusText = "已审核",
                ReceiptAmount = 10m,
                CustomerId = customer.Id,
                CustomerCode = "C001",
                CustomerName = "义乌进出口",
                AllocatedAmount = 1m,
                Currency = "USD",
                Status = AgencyServiceFeeCollectionAllocationRules.StatusActive,
                AllocatedAt = DateTime.Now.AddMinutes(-i),
                AllocatedBy = "张三"
            });
        }
        await db.SaveChangesAsync();

        var large = await AgencyServiceFeeCollectionAllocationService.ListAsync(
            counting.Proxy, new AgencyServiceFeeCollectionAllocationQuery { PageSize = 200 });
        var largeReads = counting.DatasetReads - singleReads;

        Assert.Equal(301, large.Total);
        Assert.Equal(AgencyServiceFeeCollectionAllocationQuery.MaxPageSize, large.Items.Count);  // 单页有界
        Assert.Equal(singleReads, largeReads);                                               // 行数变化不改变访问次数
        Assert.Equal(0, counting.WriteCalls);                                                // 只读：不落库
    }

    [Fact]
    public async Task 详情与两侧汇总的数据集访问次数与行数无关_无逐行查库()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var statement = SeedStatement(db, "ASFS-MANY-001", customer.Id, totalAmount: 1000m);
        var controller = BuildController(db);

        // 一张对账单上 1 行
        var singleReceipt = SeedReceipt(db, "RC-SINGLE", customer.Id, amount: 10m);
        var single = AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Create(SaveDto(statement.Id, singleReceipt.Id, 1m)));

        // 另一张对账单上 40 行（不同收款单）
        var manyStatement = SeedStatement(db, "ASFS-MANY-002", customer.Id, totalAmount: 1000m);
        var manyReceiptIds = new List<long>();
        for (var i = 0; i < 40; i++)
        {
            var receipt = SeedReceipt(db, $"RC-MANY-{i:d3}", customer.Id, amount: 10m);
            manyReceiptIds.Add(receipt.Id);
            AssertOk<AgencyServiceFeeCollectionAllocationDto>(
                await controller.Create(SaveDto(manyStatement.Id, receipt.Id, 1m)));
        }

        var counting = AllocationReadCounter.Wrap(db);

        var beforeSingle = counting.DatasetReads;
        var singleRows = await AgencyServiceFeeCollectionAllocationService.ListForStatementAsync(
            counting.Proxy, statement.Id, null, AgencyServiceFeeCollectionAllocationService.MaxDetailsPerSummary);
        var singleRowReads = counting.DatasetReads - beforeSingle;

        var beforeMany = counting.DatasetReads;
        var manyRows = await AgencyServiceFeeCollectionAllocationService.ListForStatementAsync(
            counting.Proxy, manyStatement.Id, null, AgencyServiceFeeCollectionAllocationService.MaxDetailsPerSummary);
        var manyRowReads = counting.DatasetReads - beforeMany;

        Assert.Single(singleRows);
        Assert.Equal(40, manyRows.Count);
        Assert.Equal(singleRowReads, manyRowReads);   // 行数变化不改变数据集访问次数（无逐行查库）

        var beforeSmall = counting.DatasetReads;
        var smallSummary = await AgencyServiceFeeCollectionAllocationService.GetStatementSummaryAsync(
            counting.Proxy, statement.Id);
        var smallSummaryReads = counting.DatasetReads - beforeSmall;

        var beforeLarge = counting.DatasetReads;
        var largeSummary = await AgencyServiceFeeCollectionAllocationService.GetStatementSummaryAsync(
            counting.Proxy, manyStatement.Id);
        var largeSummaryReads = counting.DatasetReads - beforeLarge;

        Assert.Single(smallSummary.Allocations);
        Assert.Equal(40, largeSummary.Allocations.Count);
        Assert.Equal(smallSummaryReads, largeSummaryReads);  // 汇总读取次数与行数无关
        Assert.Equal(40, largeSummary.AllocationCount);
        Assert.Equal(960m, largeSummary.UnallocatedAmount);

        var receiptSummary = await AgencyServiceFeeCollectionAllocationService.GetReceiptSummaryAsync(
            counting.Proxy, manyReceiptIds[0]);
        Assert.Equal(1, receiptSummary.AllocationCount);
        Assert.Equal(0, counting.WriteCalls);               // 全程只读：不落库
    }

    // ==================== 9. 非变更边界与证据维度分离 ====================

    [Fact]
    public async Task 登记与作废不改写收款单_对账单_协议_客户_订单_发票与收款引用记录()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var statement = SeedStatement(db, "ASFS-2026-001", customer.Id, totalAmount: 1000m);
        var receipt = SeedReceipt(db, "RC-001", customer.Id, amount: 1000m);
        var order = new SalesOrder
        {
            OrderNo = "SO-001",
            OrderDate = new DateTime(2026, 8, 15),
            CustomerId = customer.Id,
            Currency = Currency.USD,
            TotalAmount = 1500m,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        var agreement = new AgencyServiceFeeAgreement
        {
            AgreementNo = "ASF-2026-001",
            NormalizedAgreementNo = "ASF2026001",
            CustomerId = customer.Id,
            CustomerCode = "C001",
            CustomerName = "义乌进出口",
            EffectiveFrom = new DateTime(2026, 1, 1),
            Currency = "USD",
            FeeMethod = "比例费率",
            RatePercent = 5m,
            FeeBasis = "按服务期间",
            Status = AgencyServiceFeeAgreementRules.StatusRecorded,
            RecordedAt = new DateTime(2026, 1, 2),
            RecordedBy = "张三"
        };
        db.AgencyServiceFeeAgreements.Add(agreement);
        // ERP-053 的销售订单收款引用（另一个证据维度）与 ERP-055 的销项发票证据
        db.CustomerReceiptAllocations.Add(new CustomerReceiptAllocation
        {
            ReceiptId = receipt.Id,
            ReceiptNo = receipt.ReceiptNo,
            ReceiptDate = receipt.ReceiptDate,
            ReceiptStatus = (int)receipt.Status,
            ReceiptStatusText = "已审核",
            ReceiptAmount = receipt.Amount,
            SalesOrderId = order.Id,
            OrderNo = order.OrderNo,
            OrderDate = order.OrderDate,
            OrderStatus = (int)order.Status,
            OrderCurrency = "USD",
            CustomerId = customer.Id,
            CustomerCode = "C001",
            CustomerName = "义乌进出口",
            AllocatedAmount = 700m,
            Currency = "USD",
            Status = CustomerReceiptAllocationRules.StatusActive,
            AllocatedAt = DateTime.Now.AddDays(-1)
        });
        db.CustomerSalesInvoiceEvidences.Add(new CustomerSalesInvoiceEvidence
        {
            InvoiceType = "普票",
            InvoiceNumber = "INV-001",
            NormalizedInvoiceNumber = "INV001",
            InvoiceDate = new DateTime(2026, 8, 20),
            CustomerId = customer.Id,
            CustomerCode = "C001",
            CustomerName = "义乌进出口",
            Currency = "USD",
            NetAmount = 900m,
            TaxAmount = 100m,
            GrossAmount = 1000m,
            Status = CustomerSalesInvoiceEvidenceRules.StatusRecorded
        });
        await db.SaveChangesAsync();

        var receiptBefore = await db.FinanceReceipts.AsNoTracking().SingleAsync();
        var statementBefore = await db.AgencyServiceFeeStatements.AsNoTracking().SingleAsync();
        var customerBefore = await db.BaseCustomers.AsNoTracking().SingleAsync();
        var agreementBefore = await db.AgencyServiceFeeAgreements.AsNoTracking().SingleAsync();
        var orderBefore = await db.SalesOrders.AsNoTracking().SingleAsync();
        var receiptAllocationBefore = await db.CustomerReceiptAllocations.AsNoTracking().SingleAsync();
        var invoiceBefore = await db.CustomerSalesInvoiceEvidences.AsNoTracking().SingleAsync();

        var controller = BuildController(db, "张三");
        var created = AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Create(SaveDto(statement.Id, receipt.Id, 300m, "登记不改写来源")));
        AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Void(created.Id,
                new AgencyServiceFeeCollectionAllocationVoidRequest { Reason = "更正" }));

        var receiptAfter = await db.FinanceReceipts.AsNoTracking().SingleAsync();
        var statementAfter = await db.AgencyServiceFeeStatements.AsNoTracking().SingleAsync();
        var customerAfter = await db.BaseCustomers.AsNoTracking().SingleAsync();
        var agreementAfter = await db.AgencyServiceFeeAgreements.AsNoTracking().SingleAsync();
        var orderAfter = await db.SalesOrders.AsNoTracking().SingleAsync();
        var receiptAllocationAfter = await db.CustomerReceiptAllocations.AsNoTracking().SingleAsync();
        var invoiceAfter = await db.CustomerSalesInvoiceEvidences.AsNoTracking().SingleAsync();

        // 收款单：全部字段不变（本册只读它）
        Assert.Equal(receiptBefore.ReceiptNo, receiptAfter.ReceiptNo);
        Assert.Equal(receiptBefore.Amount, receiptAfter.Amount);
        Assert.Equal(receiptBefore.Currency, receiptAfter.Currency);
        Assert.Equal(receiptBefore.Status, receiptAfter.Status);
        Assert.Equal(receiptBefore.PaymentMethod, receiptAfter.PaymentMethod);
        Assert.Equal(receiptBefore.BankAccount, receiptAfter.BankAccount);
        Assert.Equal(receiptBefore.CustomerId, receiptAfter.CustomerId);
        Assert.Equal(receiptBefore.Remark, receiptAfter.Remark);
        Assert.Equal(receiptBefore.IsDeleted, receiptAfter.IsDeleted);
        Assert.Equal(receiptBefore.UpdatedAt, receiptAfter.UpdatedAt);

        // 对账单证据：合计 / 状态 / 登记人 / 时间戳不变
        Assert.Equal(statementBefore.TotalAmount, statementAfter.TotalAmount);
        Assert.Equal(statementBefore.Status, statementAfter.Status);
        Assert.Equal(statementBefore.RecordedAt, statementAfter.RecordedAt);
        Assert.Equal(statementBefore.RecordedBy, statementAfter.RecordedBy);
        Assert.Equal(statementBefore.VoidedAt, statementAfter.VoidedAt);
        Assert.Equal(statementBefore.UpdatedAt, statementAfter.UpdatedAt);

        // 客户主数据（含信用）不变；协议证据不变；销售订单不变
        Assert.Equal(customerBefore.CreditStatus, customerAfter.CreditStatus);
        Assert.Equal(customerBefore.CreditLimit, customerAfter.CreditLimit);
        Assert.Equal(customerBefore.CreditDays, customerAfter.CreditDays);
        Assert.Equal(customerBefore.Status, customerAfter.Status);
        Assert.Equal(customerBefore.UpdatedAt, customerAfter.UpdatedAt);

        Assert.Equal(agreementBefore.RatePercent, agreementAfter.RatePercent);
        Assert.Equal(agreementBefore.FixedAmount, agreementAfter.FixedAmount);
        Assert.Equal(agreementBefore.Status, agreementAfter.Status);
        Assert.Equal(agreementBefore.RecordedBy, agreementAfter.RecordedBy);
        Assert.Equal(agreementBefore.UpdatedAt, agreementAfter.UpdatedAt);

        Assert.Equal(orderBefore.Status, orderAfter.Status);
        Assert.Equal(orderBefore.TotalAmount, orderAfter.TotalAmount);
        Assert.Equal(orderBefore.Currency, orderAfter.Currency);
        Assert.Equal(orderBefore.UpdatedAt, orderAfter.UpdatedAt);

        // ERP-053 收款引用行与 ERP-055 发票证据：完全不受本模块影响（维度分离）
        Assert.Equal(receiptAllocationBefore.AllocatedAmount, receiptAllocationAfter.AllocatedAmount);
        Assert.Equal(receiptAllocationBefore.Status, receiptAllocationAfter.Status);
        Assert.Equal(receiptAllocationBefore.UpdatedAt, receiptAllocationAfter.UpdatedAt);
        Assert.Equal(invoiceBefore.GrossAmount, invoiceAfter.GrossAmount);
        Assert.Equal(invoiceBefore.Status, invoiceAfter.Status);
        Assert.Equal(invoiceBefore.UpdatedAt, invoiceAfter.UpdatedAt);

        // 也没有产生任何库存 / 付款 / 结算 / 费用侧的记录
        Assert.Equal(0, await db.StockMovements.CountAsync());
        Assert.Equal(0, await db.FinancePayments.CountAsync());
        Assert.Equal(0, await db.FinanceContainerSettlements.CountAsync());
        Assert.Equal(0, await db.FinanceBulkSettlements.CountAsync());
        Assert.Equal(0, await db.FinanceExpenses.CountAsync());
    }

    [Fact]
    public async Task 证据维度分离_销售订单收款引用不占用代理服务费可分摊余额且绝不相加()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var statement = SeedStatement(db, "ASFS-2026-001", customer.Id, totalAmount: 1000m);
        var receipt = SeedReceipt(db, "RC-001", customer.Id, amount: 1000m);
        var order = new SalesOrder
        {
            OrderNo = "SO-001",
            OrderDate = new DateTime(2026, 8, 15),
            CustomerId = customer.Id,
            Currency = Currency.USD,
            TotalAmount = 1000m,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        // ERP-053：同一张收款单已被「收款单 → 销售订单」引用占满
        db.CustomerReceiptAllocations.Add(new CustomerReceiptAllocation
        {
            ReceiptId = receipt.Id,
            ReceiptNo = receipt.ReceiptNo,
            ReceiptDate = receipt.ReceiptDate,
            ReceiptStatus = (int)receipt.Status,
            ReceiptStatusText = "已审核",
            ReceiptAmount = receipt.Amount,
            SalesOrderId = order.Id,
            OrderNo = order.OrderNo,
            OrderDate = order.OrderDate,
            OrderStatus = (int)order.Status,
            OrderCurrency = "USD",
            CustomerId = customer.Id,
            CustomerCode = "C001",
            CustomerName = "义乌进出口",
            AllocatedAmount = 1000m,
            Currency = "USD",
            Status = CustomerReceiptAllocationRules.StatusActive,
            AllocatedAt = DateTime.Now.AddDays(-1)
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        // 本维度只扣减**本表的**有效分摊行：ERP-053 的引用不占用代理服务费可分摊余额
        var options = AssertOk<List<AgencyServiceFeeCollectionAllocationReceiptCandidateDto>>(
            await controller.ReceiptCandidates(customer.Id, "USD", null));
        Assert.Single(options);
        Assert.Equal(1000m, options[0].UnallocatedAmount);
        Assert.True(options[0].Eligible);

        var created = AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await controller.Create(SaveDto(statement.Id, receipt.Id, 400m)));
        Assert.Equal(400m, created.AllocatedAmount);

        var receiptSummary = AssertOk<AgencyServiceFeeCollectionAllocationReceiptSummaryDto>(
            await controller.ReceiptSummary(receipt.Id));
        Assert.Equal(400m, receiptSummary.AllocatedAmount);
        Assert.Equal(600m, receiptSummary.UnallocatedAmount);
        Assert.Contains("收款单在本维度", receiptSummary.LinkageText);
        Assert.Contains("ERP-053", receiptSummary.DimensionSeparationText);
        Assert.Contains("ERP-055", receiptSummary.DimensionSeparationText);
        Assert.Contains("绝不相加", receiptSummary.DimensionSeparationText);
        Assert.Contains("被当作几张不同的收款单", receiptSummary.DimensionSeparationText);

        // ERP-053 的引用行未被改写，也未被并入本维度合计
        var other = await db.CustomerReceiptAllocations.AsNoTracking().SingleAsync();
        Assert.Equal(1000m, other.AllocatedAmount);
        Assert.Equal(1, await db.AgencyServiceFeeCollectionAllocations.CountAsync());
    }

    // ==================== 10. 纯规则 / 模型 / 幂等结构 / 契约 ====================

    [Fact]
    public void 纯规则_金额取整_资格判定与关联状态()
    {
        Assert.Equal(100.01m, AgencyServiceFeeCollectionAllocationRules.NormalizeAllocationAmount(100.005m, "USD"));
        Assert.Equal(101m, AgencyServiceFeeCollectionAllocationRules.NormalizeAllocationAmount(100.6m, "JPY"));
        Assert.Equal("101 JPY", AgencyServiceFeeCollectionAllocationRules.AmountText(101m, "JPY"));
        Assert.Equal("100.00 USD", AgencyServiceFeeCollectionAllocationRules.AmountText(100m, "USD"));
        Assert.Equal("100.00 CNY", AgencyServiceFeeCollectionAllocationRules.AmountText(100m, null));

        Assert.Throws<BusinessException>(() =>
            AgencyServiceFeeCollectionAllocationRules.NormalizeAllocationAmount(0.004m, "USD"));
        Assert.Throws<BusinessException>(() =>
            AgencyServiceFeeCollectionAllocationRules.NormalizeCurrencyStrict("XXX"));

        // 收款单资格：不存在 / 已删除 / 已取消 / 无金额 / 已占满
        Assert.False(AgencyServiceFeeCollectionAllocationRules
            .EvaluateReceiptEligibility(null, 0m).Eligible);
        Assert.False(AgencyServiceFeeCollectionAllocationRules
            .EvaluateReceiptEligibility(
                new FinanceReceipt { ReceiptNo = "R", Amount = 100m, Currency = Currency.USD },
                100m).Eligible);
        Assert.True(AgencyServiceFeeCollectionAllocationRules
            .EvaluateReceiptEligibility(
                new FinanceReceipt { ReceiptNo = "R", Amount = 100m, Currency = Currency.USD },
                40m).Eligible);

        // 对账单资格：只有在「已登记且仍有未分摊额」时可分摊
        var recorded = new AgencyServiceFeeStatement
        {
            StatementNo = "S", Currency = "USD", TotalAmount = 100m,
            Status = AgencyServiceFeeStatementRules.StatusRecorded
        };
        Assert.True(AgencyServiceFeeCollectionAllocationRules
            .EvaluateStatementEligibility(recorded, 0m).Eligible);
        Assert.False(AgencyServiceFeeCollectionAllocationRules
            .EvaluateStatementEligibility(recorded, 100m).Eligible);
        var draft = new AgencyServiceFeeStatement
        {
            StatementNo = "S", Currency = "USD", TotalAmount = 100m,
            Status = AgencyServiceFeeStatementRules.StatusDraft
        };
        Assert.False(AgencyServiceFeeCollectionAllocationRules
            .EvaluateStatementEligibility(draft, 0m).Eligible);

        // 关联状态：未分摊 / 部分 / 已全部
        Assert.Equal(AgencyServiceFeeCollectionAllocationRules.LinkageUnallocated,
            AgencyServiceFeeCollectionAllocationRules.LinkageStatusOf(100m, 0m));
        Assert.Equal(AgencyServiceFeeCollectionAllocationRules.LinkagePartial,
            AgencyServiceFeeCollectionAllocationRules.LinkageStatusOf(100m, 60m));
        Assert.Equal(AgencyServiceFeeCollectionAllocationRules.LinkageFullyAllocated,
            AgencyServiceFeeCollectionAllocationRules.LinkageStatusOf(100m, 100m));

        // 文案：未分摊金额明示不代表已付 / 已结清 / 逾期 / 收入确认 / 记账状态
        var text = AgencyServiceFeeCollectionAllocationRules.LinkageText(100m, 60m, 2, "USD", "对账单在本维度");
        Assert.Contains("部分分摊", text);
        Assert.Contains("未分摊 40", text);
        Assert.Contains("不代表已付 / 已结清 / 逾期 / 收入确认 / 记账状态或应收余额", text);

        // 身份与作废原因规则
        Assert.Equal("(未填对账单号)", AgencyServiceFeeCollectionAllocationRules.IdentityText("  "));
        Assert.Equal("ASFS-1 ← RC-1",
            AgencyServiceFeeCollectionAllocationRules.AllocationIdentityText("ASFS-1", "RC-1"));
        Assert.Equal("未知用户", AgencyServiceFeeCollectionAllocationRules.NormalizeAllocatedBy(null));
        Assert.Throws<BusinessException>(() =>
            AgencyServiceFeeCollectionAllocationRules.NormalizeVoidReason("  "));
        Assert.Throws<BusinessException>(() =>
            AgencyServiceFeeCollectionAllocationRules.StatusText(9));
    }

    [Fact]
    public void 模型结构_长度精度_唯一约束_无外键且读取标注不落库()
    {
        using var db = TestDbFactory.Create();

        var type = db.Model.FindEntityType(typeof(AgencyServiceFeeCollectionAllocation));
        Assert.NotNull(type);
        Assert.Equal(nameof(AgencyServiceFeeCollectionAllocation), type!.GetTableName());
        Assert.Equal("db_owner", type.GetSchema());

        Assert.Equal(50, type.FindProperty(nameof(AgencyServiceFeeCollectionAllocation.StatementNo))!.GetMaxLength());
        Assert.Equal(30, type.FindProperty(
            nameof(AgencyServiceFeeCollectionAllocation.StatementStatusText))!.GetMaxLength());
        Assert.Equal(20, type.FindProperty(
            nameof(AgencyServiceFeeCollectionAllocation.StatementCurrency))!.GetMaxLength());
        Assert.Equal(50, type.FindProperty(
            nameof(AgencyServiceFeeCollectionAllocation.StatementAgreementNo))!.GetMaxLength());
        Assert.Equal(50, type.FindProperty(nameof(AgencyServiceFeeCollectionAllocation.ReceiptNo))!.GetMaxLength());
        Assert.Equal(30, type.FindProperty(
            nameof(AgencyServiceFeeCollectionAllocation.ReceiptStatusText))!.GetMaxLength());
        Assert.Equal(50, type.FindProperty(nameof(AgencyServiceFeeCollectionAllocation.CustomerCode))!.GetMaxLength());
        Assert.Equal(200, type.FindProperty(nameof(AgencyServiceFeeCollectionAllocation.CustomerName))!.GetMaxLength());
        Assert.Equal(20, type.FindProperty(nameof(AgencyServiceFeeCollectionAllocation.Currency))!.GetMaxLength());
        Assert.Equal(500, type.FindProperty(nameof(AgencyServiceFeeCollectionAllocation.Remark))!.GetMaxLength());
        Assert.Equal(100, type.FindProperty(nameof(AgencyServiceFeeCollectionAllocation.AllocatedBy))!.GetMaxLength());
        Assert.Equal(500, type.FindProperty(nameof(AgencyServiceFeeCollectionAllocation.VoidReason))!.GetMaxLength());

        Assert.Equal(18, type.FindProperty(
            nameof(AgencyServiceFeeCollectionAllocation.StatementTotalAmount))!.GetPrecision());
        Assert.Equal(2, type.FindProperty(
            nameof(AgencyServiceFeeCollectionAllocation.StatementTotalAmount))!.GetScale());
        Assert.Equal(18, type.FindProperty(nameof(AgencyServiceFeeCollectionAllocation.ReceiptAmount))!.GetPrecision());
        Assert.Equal(2, type.FindProperty(nameof(AgencyServiceFeeCollectionAllocation.ReceiptAmount))!.GetScale());
        Assert.Equal(18, type.FindProperty(nameof(AgencyServiceFeeCollectionAllocation.AllocatedAmount))!.GetPrecision());
        Assert.Equal(2, type.FindProperty(nameof(AgencyServiceFeeCollectionAllocation.AllocatedAmount))!.GetScale());

        var unique = type.GetIndexes().Single(i =>
            i.GetDatabaseName() == "UX_AgencyServiceFeeCollectionAllocations_StatementReceipt");
        Assert.True(unique.IsUnique);
        Assert.Equal("IsDeleted = 0 AND Status <> 2", unique.GetFilter());
        Assert.Equal(
            new[]
            {
                nameof(AgencyServiceFeeCollectionAllocation.StatementId),
                nameof(AgencyServiceFeeCollectionAllocation.ReceiptId)
            },
            unique.Properties.Select(p => p.Name).ToArray());

        Assert.Contains(type.GetIndexes(), i =>
            i.GetDatabaseName() == "IX_AgencyServiceFeeCollectionAllocations_StatementId_Status");
        Assert.Contains(type.GetIndexes(), i =>
            i.GetDatabaseName() == "IX_AgencyServiceFeeCollectionAllocations_ReceiptId_Status");
        Assert.Contains(type.GetIndexes(), i =>
            i.GetDatabaseName() == "IX_AgencyServiceFeeCollectionAllocations_CustomerId_Status");
        Assert.Contains(type.GetIndexes(), i =>
            i.GetDatabaseName() == "IX_AgencyServiceFeeCollectionAllocations_Status_AllocatedAt");

        // 刻意不建任何外键
        Assert.Empty(type.GetForeignKeys());

        // 读取侧标注全部是**非持久化**列（不落库）
        Assert.DoesNotContain(type.GetProperties(), p => p.Name
            is nameof(AgencyServiceFeeCollectionAllocation.ReceiptAvailable)
            or nameof(AgencyServiceFeeCollectionAllocation.ReceiptAvailabilityText)
            or nameof(AgencyServiceFeeCollectionAllocation.StatementAvailable)
            or nameof(AgencyServiceFeeCollectionAllocation.StatementAvailabilityText)
            or nameof(AgencyServiceFeeCollectionAllocation.AmountDecimals)
            or nameof(AgencyServiceFeeCollectionAllocation.AmountText)
            or nameof(AgencyServiceFeeCollectionAllocation.StatusText));

        // 只建一张新表：本模块在 IErpDbContext 中只有这一个数据集
        var contextType = typeof(IErpDbContext);
        var collectionSets = contextType.GetProperties()
            .Where(p => p.Name.Contains("CollectionAllocation", StringComparison.Ordinal))
            .ToList();
        Assert.Single(collectionSets);
        Assert.Equal("AgencyServiceFeeCollectionAllocations", collectionSets[0].Name);
        Assert.Equal(typeof(DbSet<AgencyServiceFeeCollectionAllocation>), collectionSets[0].PropertyType);

        // 命名契约：DbSet 名同时含 Receipt 与 Allocation 的模型**仍然只有 ERP-053 一套**（本模型用 Collection 命名，
        // 因此不会破坏 CustomerReceiptAllocationTests 锁定的「仓库只有一套收款引用模型」审计结论）
        var receiptAllocationSets = contextType.GetProperties()
            .Where(p => p.Name.Contains("Receipt", StringComparison.Ordinal)
                        && p.Name.Contains("Allocation", StringComparison.Ordinal))
            .ToList();
        Assert.Single(receiptAllocationSets);
        Assert.Equal(nameof(IErpDbContext.CustomerReceiptAllocations), receiptAllocationSets[0].Name);
    }

    [Fact]
    public void Schema_第44段幂等建表建索引且不含任何回填或账务语句()
    {
        var script = File.ReadAllText(
            RepoFile("src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs"));

        Assert.Contains("IF OBJECT_ID('db_owner.AgencyServiceFeeCollectionAllocations') IS NULL", script);
        Assert.Contains("StatementTotalAmount DECIMAL(18,2) NOT NULL DEFAULT 0", script);
        Assert.Contains("ReceiptAmount DECIMAL(18,2) NOT NULL DEFAULT 0", script);
        Assert.Contains("AllocatedAmount DECIMAL(18,2) NOT NULL DEFAULT 0", script);
        Assert.Contains("AllocatedAt DATETIME2 NOT NULL", script);
        Assert.Contains("AllocatedBy NVARCHAR(100) NOT NULL DEFAULT N''", script);
        Assert.Contains("CREATE UNIQUE INDEX UX_AgencyServiceFeeCollectionAllocations_StatementReceipt", script);
        Assert.Contains(
            "ON db_owner.AgencyServiceFeeCollectionAllocations(StatementId, ReceiptId)", script);
        Assert.Contains("WHERE IsDeleted = 0 AND Status <> 2;", script);

        var start = script.IndexOf("// 44. 客户收款 → 代理服务费对账单 分摊登记", StringComparison.Ordinal);
        Assert.True(start > 0);
        var segment = script[start..];

        // 本模块段落只建一张表 + 过滤索引：不改写任何既有表结构，也不做任何回填 / 收付款 / 记账语句
        Assert.DoesNotContain("ALTER TABLE", segment);
        Assert.DoesNotContain("UPDATE db_owner", segment);
        Assert.DoesNotContain("INSERT INTO db_owner", segment);
        Assert.DoesNotContain("DELETE FROM db_owner", segment);
        Assert.DoesNotContain("EXEC ", segment);
        Assert.DoesNotContain("FK_AgencyServiceFeeCollectionAllocation", segment);

        // 章节说明也锁定证据维度分离口径（不与 ERP-053 / ERP-055 相加）
        Assert.Contains("ERP-053", segment);
        Assert.Contains("ERP-055", segment);
    }

    [Fact]
    public void 请求契约_不提供快照或账务字段_服务端权威字段全部由服务端写入()
    {
        var requestNames = typeof(AgencyServiceFeeCollectionAllocationSaveDto)
            .GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain(requestNames, n => n.Contains("Customer", StringComparison.Ordinal)
            || n.Contains("Currency", StringComparison.Ordinal)
            || n.Contains("Status", StringComparison.Ordinal)
            || n.Contains("AmountText", StringComparison.Ordinal)
            || n.Contains("AllocatedBy", StringComparison.Ordinal)
            || n.Contains("AllocatedAt", StringComparison.Ordinal)
            || n.Contains("Balance", StringComparison.Ordinal)
            || n.Contains("Unallocated", StringComparison.Ordinal)
            || n.Contains("Invoice", StringComparison.Ordinal)
            || n.Contains("Ledger", StringComparison.Ordinal)
            || n.Contains("Voucher", StringComparison.Ordinal));
        Assert.Contains("StatementId", requestNames);
        Assert.Contains("ReceiptId", requestNames);
        Assert.Contains("AllocatedAmount", requestNames);

        // 实体确实持久化两侧快照、登记人与时间（服务端权威字段而非仅 DTO 计算）
        var entityProperties = typeof(AgencyServiceFeeCollectionAllocation).GetProperties()
            .Select(p => p.Name).ToList();
        Assert.Contains("StatementTotalAmount", entityProperties);
        Assert.Contains("StatementAgreementId", entityProperties);
        Assert.Contains("ReceiptAmount", entityProperties);
        Assert.Contains("CustomerCode", entityProperties);
        Assert.Contains("AllocatedBy", entityProperties);
        Assert.Contains("AllocatedAt", entityProperties);
        Assert.Contains("VoidReason", entityProperties);
    }

    [Fact]
    public void 前端与路由接线契约()
    {
        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/agency-service-fee-collection-allocations.js", index);

        var modules = File.ReadAllText(
            RepoFile("src", "ERP.Api", "wwwroot", "js", "modules.js"));
        // 工具栏入口必须带括号（extraActions 直接注入 onclick 属性），行操作只写函数名（渲染时注入行 Id）
        Assert.Contains("onclick: 'openAgencyServiceFeeCollectionAllocationRegister()'", modules);
        Assert.Contains("onclick: 'openAgencyServiceFeeCollectionAllocationForCustomer'", modules);

        var js = File.ReadAllText(RepoFile(
            "src", "ERP.Api", "wwwroot", "js", "agency-service-fee-collection-allocations.js"));
        Assert.Contains("async function openAgencyServiceFeeCollectionAllocationRegister", js);
        Assert.Contains("async function openAgencyServiceFeeCollectionAllocationForCustomer", js);
        Assert.Contains("'/api/agency-service-fee-collection-allocations'", js);
        Assert.Contains("'/api/agency-service-fee-collection-allocations?'", js);
        Assert.Contains("/receipts?customerId=", js);
        Assert.Contains("/receipts/' + receiptId + '/summary'", js);
        Assert.Contains("/statements/' + statementId + '/summary'", js);
        Assert.Contains("/void", js);
        Assert.Contains("asfcaCreate", js);
        Assert.Contains("asfcaConfirmVoid", js);
        Assert.Contains("不是到账凭证", js);
        Assert.Contains("不会按单号、金额或日期相似度猜对应关系", js);
        Assert.Contains("绝不相加", js);
        Assert.Contains("不会被静默核销或改派", js);

        // 对账单详情视图提供「收款分摊」入口（从 ERP-070 工作流直达本册）
        var statementJs = File.ReadAllText(RepoFile(
            "src", "ERP.Api", "wwwroot", "js", "agency-service-fee-statements.js"));
        Assert.Contains("openAgencyServiceFeeCollectionAllocationRegister(${row.id})", statementJs);

        var controller = File.ReadAllText(RepoFile(
            "src", "ERP.Api", "Controllers", "AgencyServiceFeeCollectionAllocationController.cs"));
        Assert.Contains("[Route(\"api/agency-service-fee-collection-allocations\")]", controller);
        Assert.Contains("[HttpGet(\"metadata\")]", controller);
        Assert.Contains("[HttpGet(\"receipts\")]", controller);
        Assert.Contains("[HttpGet(\"statements\")]", controller);
        Assert.Contains("[HttpGet(\"receipts/{receiptId:long}/summary\")]", controller);
        Assert.Contains("[HttpGet(\"statements/{statementId:long}/summary\")]", controller);
        Assert.Contains("{id:long}/void", controller);
        Assert.Contains("CurrentUserName()", controller);

        var docs = File.ReadAllText(RepoFile("docs", "代理服务费收款分摊证据说明.md"));
        Assert.Contains("ERP-071", docs);
        Assert.Contains("AgencyServiceFeeCollectionAllocations", docs);
        Assert.Contains("UX_AgencyServiceFeeCollectionAllocations_StatementReceipt", docs);
    }

    /// <summary>
    /// 只读计数上下文代理（<see cref="DispatchProxy"/>）：记录访问的数据集（<c>DbSet</c> 属性）名称与写入次数，
    /// 用于断言「分页 / 有界查询」「无逐行查库」与「只读不写库」；不改动生产代码。
    /// </summary>
    public class AllocationReadCounter : DispatchProxy
    {
        private IErpDbContext _inner = null!;

        /// <summary>包装后的上下文（服务按 <see cref="IErpDbContext"/> 使用）</summary>
        public IErpDbContext Proxy { get; private set; } = null!;

        /// <summary>数据集（<c>DbSet</c> 属性）访问次数：即本次查询实际发起的数据集访问次数</summary>
        public int DatasetReads => ReadProperties.Count;

        /// <summary>被访问的数据集属性名</summary>
        public List<string> ReadProperties { get; } = new();

        /// <summary><c>SaveChangesAsync</c> 调用次数：只读库恒为 0</summary>
        public int WriteCalls { get; private set; }

        /// <summary>包装一个真实上下文（计数从返回对象上读取）</summary>
        public static AllocationReadCounter Wrap(IErpDbContext inner)
        {
            var proxy = DispatchProxy.Create<IErpDbContext, AllocationReadCounter>();
            var counting = (AllocationReadCounter)(object)proxy;
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
                return _inner.SaveChangesAsync(
                    args is { Length: > 0 } ? (CancellationToken)args[0]! : default);
            }

            if (targetMethod.Name.StartsWith("get_", StringComparison.Ordinal))
            {
                ReadProperties.Add(targetMethod.Name[4..]);
            }

            return targetMethod.Invoke(_inner, args);
        }
    }
}
